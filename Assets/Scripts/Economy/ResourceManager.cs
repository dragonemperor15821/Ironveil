using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ironveil.Economy
{
    /// <summary>
    /// Owns the player's single live <see cref="ResourceLedger"/> for the whole Play session.
    ///
    /// - Created by <see cref="ResourceHudBootstrap"/> through <see cref="GetOrCreate"/>, on its own
    ///   DontDestroyOnLoad host, so it survives runtime scene loads (MainBuild and World Explorer share it).
    /// - Initialized once from the <see cref="HomeBaseResourceConfig"/> asset. Later Initialize calls with the same
    ///   asset reuse the existing ledger; a different asset is refused, so the live ledger is never replaced.
    /// - Advances the ledger with scaled gameplay time (Time.deltaTime), so replenishment stops at Time.timeScale = 0.
    ///   Run In Background stays off, so an unfocused player does not replenish either. There is no offline
    ///   replenishment, and nothing is saved between Play sessions.
    /// - All resource rules stay in the ledger. This class only delegates to it and forwards its Changed event.
    ///
    /// Domain reload is disabled in this project, so the statics are reset at SubsystemRegistration
    /// (the same pattern as BaseMenuBootstrap).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ResourceManager : MonoBehaviour
    {
        public const string HostName = "Resource Manager";

        public static ResourceManager Instance { get; private set; }

        /// <summary>Number of ledgers created this Play session (initialization plus explicit resets). Diagnostics and tests only.</summary>
        public static int LedgersCreatedThisSession { get; private set; }

        /// <summary>The asset the live ledger was built from; null until initialized.</summary>
        public HomeBaseResourceConfig Config { get; private set; }

        /// <summary>
        /// The live ledger; null until initialized. Do not keep this reference across
        /// <see cref="ResetToInitialConfiguration"/>, which replaces it. Subscribe to <see cref="ResourceChanged"/>
        /// and <see cref="LedgerReset"/> on the manager instead of to the ledger.
        /// </summary>
        public ResourceLedger Ledger { get; private set; }

        public bool IsInitialized => Ledger != null;

        /// <summary>Why the most recent initialization or reset failed; null after a success.</summary>
        public string LastError { get; private set; }

        /// <summary>The ledger's Changed event, forwarded. It survives ledger resets.</summary>
        public event Action<ResourceChange> ResourceChanged;

        /// <summary>Raised after <see cref="ResetToInitialConfiguration"/> replaces the ledger. Listeners should re-read every balance.</summary>
        public event Action LedgerReset;

        /// <summary>Raised after a storage capacity of the live ledger changes at runtime (Stage 8 storage expansion).</summary>
        public event Action<ResourceCapacityChange> CapacityChanged;

        /// <summary>Raised whenever a manager starts using a new ledger (Initialize or ResetToInitialConfiguration).</summary>
        public static event Action<ResourceManager> LedgerAttached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            Instance = null;
            LedgersCreatedThisSession = 0;
        }

        /// <summary>Returns the session's manager, creating its persistent host if none exists. Never creates a second one.</summary>
        public static ResourceManager GetOrCreate()
        {
            if (Instance != null) return Instance;

            ResourceManager existing = FindAnyObjectByType<ResourceManager>(FindObjectsInactive.Include);
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            var host = new GameObject(HostName);
            if (Application.isPlaying) DontDestroyOnLoad(host);
            ResourceManager manager = host.AddComponent<ResourceManager>(); // Awake (Play Mode) sets Instance
            Instance = manager;
            return manager;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"ResourceManager: a second manager on '{name}' was removed; the session already has one on '{Instance.name}'.", this);
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            SetLedger(null);
            Instance = null;
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>
        /// Advances replenishment by scaled gameplay seconds. Called from Update with Time.deltaTime;
        /// public so tests can drive it. Zero (paused) or invalid deltas are ignored.
        /// </summary>
        public void Tick(float scaledDeltaSeconds)
        {
            if (Ledger == null) return;
            if (!(scaledDeltaSeconds > 0f) || float.IsInfinity(scaledDeltaSeconds)) return; // also rejects NaN
            Ledger.AdvanceSeconds(scaledDeltaSeconds);
        }

        /// <summary>
        /// Initializes the manager from <paramref name="config"/>. The config is fully validated before a ledger is
        /// created. On failure the manager stays exactly as it was (uninitialized, or still holding its live ledger)
        /// and the reason is in <see cref="LastError"/>.
        /// </summary>
        /// <returns>True if the manager is initialized from <paramref name="config"/> after the call.</returns>
        public bool Initialize(HomeBaseResourceConfig config)
        {
            if (config == null)
                return Fail("no HomeBaseResourceConfig was supplied.");

            if (IsInitialized)
            {
                if (Config == config)
                {
                    LastError = null;
                    return true; // reuse the live ledger: balances and cycles are untouched
                }

                return Fail($"already initialized from '{Config.name}'; refusing to replace the live ledger with '{config.name}'.");
            }

            var errors = new List<string>();
            if (!config.Validate(errors))
                return Fail($"'{config.name}' is invalid:\n - " + string.Join("\n - ", errors));

            ResourceLedger ledger;
            try
            {
                ledger = config.CreateLedger();
            }
            catch (Exception e)
            {
                return Fail($"creating the ledger from '{config.name}' failed: {e.Message}");
            }

            Config = config;
            SetLedger(ledger);
            LastError = null;
            return true;
        }

        /// <summary>
        /// Replaces the live ledger with a fresh one built from <see cref="Config"/>: initial balances, no replenishment
        /// cycles, clock at zero. NOT called automatically by anything, including New World. When resources reset is
        /// still awaiting design approval. Returns false (and changes nothing) if uninitialized or the config is now invalid.
        /// </summary>
        public bool ResetToInitialConfiguration()
        {
            if (!IsInitialized)
                return Fail("cannot reset: not initialized.");

            ResourceLedger fresh;
            try
            {
                fresh = Config.CreateLedger();
            }
            catch (Exception e)
            {
                return Fail($"reset failed; the live ledger was kept: {e.Message}");
            }

            SetLedger(fresh);
            LastError = null;

            Action handlers = LedgerReset;
            if (handlers != null)
            {
                foreach (Delegate d in handlers.GetInvocationList())
                {
                    try { ((Action)d)(); }
                    catch (Exception e) { Debug.LogException(e, this); }
                }
            }
            return true;
        }

        // ------------------------------------------------------------ ledger access (thin delegation)

        public bool IsConfigured(ResourceType resource) => Ledger != null && Ledger.IsConfigured(resource);

        public bool TryGetBalance(ResourceType resource, out long balance)
        {
            if (Ledger != null) return Ledger.TryGetBalance(resource, out balance);
            balance = 0;
            return false;
        }

        public bool TryGetCapacity(ResourceType resource, out long capacity)
        {
            if (Ledger != null) return Ledger.TryGetCapacity(resource, out capacity);
            capacity = 0;
            return false;
        }

        /// <summary>Changes a storage capacity of the live ledger (see ResourceLedger.TrySetCapacity). Balances are untouched.</summary>
        public bool TrySetCapacity(ResourceType resource, long capacity, out ResourceTransactionResult result)
        {
            if (Ledger == null)
            {
                result = ResourceTransactionResult.NotConfigured;
                return false;
            }
            return Ledger.TrySetCapacity(resource, capacity, out result);
        }

        public bool CanSpend(ResourceType resource, long amount) => Ledger != null && Ledger.CanSpend(resource, amount);

        public bool TrySpend(ResourceType resource, long amount) => TrySpend(resource, amount, out _);

        /// <summary>Spends through the live ledger. Before initialization every spend is rejected with NotConfigured.</summary>
        public bool TrySpend(ResourceType resource, long amount, out ResourceTransactionResult result)
        {
            if (Ledger == null)
            {
                result = ResourceTransactionResult.NotConfigured;
                return false;
            }
            return Ledger.TrySpend(resource, amount, out result);
        }

        // ------------------------------------------------------------ internals

        private void SetLedger(ResourceLedger ledger)
        {
            if (Ledger != null)
            {
                Ledger.Changed -= ForwardChange;
                Ledger.CapacityChanged -= ForwardCapacityChange;
            }
            Ledger = ledger;
            if (ledger == null) return;

            ledger.Changed += ForwardChange; // exactly one subscription per ledger
            ledger.CapacityChanged += ForwardCapacityChange;
            LedgersCreatedThisSession++;

            Action<ResourceManager> attached = LedgerAttached;
            if (attached != null)
            {
                foreach (Delegate d in attached.GetInvocationList())
                {
                    try { ((Action<ResourceManager>)d)(this); }
                    catch (Exception e) { Debug.LogException(e, this); }
                }
            }
        }

        private void ForwardCapacityChange(ResourceCapacityChange change)
        {
            Action<ResourceCapacityChange> handlers = CapacityChanged;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<ResourceCapacityChange>)d)(change); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
        }

        private void ForwardChange(ResourceChange change)
        {
            Action<ResourceChange> handlers = ResourceChanged;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<ResourceChange>)d)(change); }
                catch (Exception e) { Debug.LogException(e, this); }
            }
        }

        private bool Fail(string reason)
        {
            LastError = reason;
            Debug.LogError("ResourceManager: " + reason, this);
            return false;
        }
    }
}
