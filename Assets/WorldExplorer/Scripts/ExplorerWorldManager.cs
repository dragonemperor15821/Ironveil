using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// World seed lifecycle of the IRONVEIL World Explorer.
///
/// The terrain, hills, river, railway and snow are fixed; a "world" is the
/// arrangement of the 350 bases, identified by a base seed. Startup reads
/// <c>?seed=N&amp;v=1</c> from the page URL (or draws a random seed), waits
/// for the initial generation, applies the seed through
/// <see cref="WorldGenerationPrototype.RegenerateBases"/> only when it
/// differs from the generated one, writes the canonical URL and reframes the
/// camera, all before <see cref="IsReady"/> becomes true. New World and Share
/// World reuse the same API; terrain is never regenerated.
/// </summary>
[DisallowMultipleComponent]
public sealed class ExplorerWorldManager : MonoBehaviour
{
    public enum State { WaitingForGeneration, ApplyingSeed, Ready, Regenerating, Failed }
    public enum ShareOutcome { None, Copied, CopiedLegacy, FallbackShown, Failed }

    [SerializeField] private WorldGenerationPrototype worldGenerator;
    [SerializeField] private ExplorerCameraController cameraController;

    /// <summary>Current lifecycle state.</summary>
    public State CurrentState { get; private set; } = State.WaitingForGeneration;
    /// <summary>True once the requested world is shown and the controls may be used.</summary>
    public bool IsReady => CurrentState == State.Ready;
    /// <summary>True while a base regeneration runs (controls must be disabled).</summary>
    public bool IsBusy => CurrentState != State.Ready;
    /// <summary>Seed of the world currently shown (0 until ready).</summary>
    public int CurrentSeed { get; private set; }
    /// <summary>The canonical share URL of the current world.</summary>
    public string ShareUrl { get; private set; } = string.Empty;
    /// <summary>The page request read at startup.</summary>
    public ExplorerSeedUrl.Request StartupRequest { get; private set; }
    /// <summary>True when the startup seed came from the URL (otherwise random).</summary>
    public bool StartupSeedFromUrl { get; private set; }
    /// <summary>Notice for links with an unknown version (empty otherwise).</summary>
    public string CompatibilityNotice { get; private set; } = string.Empty;
    /// <summary>Notice for a missing or invalid seed parameter (empty otherwise).</summary>
    public string SeedNotice { get; private set; } = string.Empty;
    /// <summary>Last error shown to the visitor (empty when none).</summary>
    public string LastError { get; private set; } = string.Empty;
    /// <summary>Outcome of the last Share World action.</summary>
    public ShareOutcome LastShareOutcome { get; private set; }
    /// <summary>True while a clipboard write is pending.</summary>
    public bool IsCopying { get; private set; }
    /// <summary>Duration of the last base regeneration in milliseconds.</summary>
    public double LastRegenerationMs { get; private set; }
    /// <summary>The scene's world generator (read-only use: loading progress display).</summary>
    public WorldGenerationPrototype Generator => worldGenerator;

    /// <summary>Raised whenever the state, seed, notices or share outcome change.</summary>
    public event Action<ExplorerWorldManager> Changed;

    private string pageAddress = string.Empty;
    private bool subscribed;
    private bool startupDone;
    private Coroutine running;

    private void Awake()
    {
        if (worldGenerator == null) worldGenerator = FindAnyObjectByType<WorldGenerationPrototype>();
        if (cameraController == null) cameraController = FindAnyObjectByType<ExplorerCameraController>();

        // The URL is read once; it is never parsed per frame.
        string url = ExplorerBrowserBridge.CurrentPageUrl();
        pageAddress = ExplorerSeedUrl.PageAddress(url);
        StartupRequest = ExplorerSeedUrl.ParseUrl(url);
        if (!StartupRequest.VersionKnown)
        {
            CompatibilityNotice = "This link uses an unknown format (v=" + StartupRequest.RawVersion +
                "). The world was loaded with format v" + ExplorerSeedUrl.CurrentVersion + " and may differ.";
        }
    }

    private void OnEnable()
    {
        if (worldGenerator != null && !subscribed)
        {
            worldGenerator.GenerationCompleted += HandleGenerationCompleted;
            subscribed = true;
        }
    }

    private void OnDisable()
    {
        if (worldGenerator != null && subscribed)
        {
            worldGenerator.GenerationCompleted -= HandleGenerationCompleted;
            subscribed = false;
        }
    }

    private void Start()
    {
        if (worldGenerator == null)
        {
            Fail("The world generator is missing from this scene.");
            return;
        }
        // Late subscriber: the generation may already have completed.
        if (worldGenerator.IsGenerationComplete) BeginStartup();
    }

    private void HandleGenerationCompleted(WorldGenerationPrototype generator)
    {
        BeginStartup();
    }

    private void BeginStartup()
    {
        if (startupDone || running != null) return;
        running = StartCoroutine(ApplyStartupSeed());
    }

    private IEnumerator ApplyStartupSeed()
    {
        CurrentState = State.ApplyingSeed;
        RaiseChanged();

        int generated = worldGenerator.CurrentBaseSeed;
        int target;
        switch (StartupRequest.SeedStatus)
        {
            case ExplorerSeedUrl.SeedStatus.Valid:
                target = StartupRequest.Seed;
                StartupSeedFromUrl = true;
                break;
            case ExplorerSeedUrl.SeedStatus.Invalid:
                target = ExplorerBrowserBridge.NewRandomSeed(0);
                SeedNotice = "The link's world number was not valid, so a new world was generated.";
                break;
            default:
                target = ExplorerBrowserBridge.NewRandomSeed(0);
                break;
        }

        if (target != generated)
        {
            // Let the generating screen stay on screen for this frame.
            yield return null;
            if (!Regenerate(target))
            {
                LastError = "The requested world could not be generated.";
                // Keep a consistent world: put back the generated arrangement.
                target = Regenerate(generated) ? generated : 0;
                if (target == 0)
                {
                    running = null;
                    Fail("The world could not be generated. Please reload the page.");
                    yield break;
                }
            }
        }

        SetCurrentSeed(target);
        cameraController?.ResetView();
        startupDone = true;
        running = null;
        CurrentState = State.Ready;
        RaiseChanged();
    }

    /// <summary>
    /// Generates a new random arrangement of the 350 bases (terrain unchanged).
    /// Ignored while another regeneration runs. Returns true when started.
    /// </summary>
    public bool NewWorld()
    {
        if (CurrentState != State.Ready || running != null) return false;
        running = StartCoroutine(NewWorldRoutine());
        return true;
    }

    private IEnumerator NewWorldRoutine()
    {
        int previous = CurrentSeed;
        int next = ExplorerBrowserBridge.NewRandomSeed(previous);
        LastError = string.Empty;
        LastShareOutcome = ShareOutcome.None;
        ExplorerBrowserBridge.HideLinkField();
        CurrentState = State.Regenerating;
        RaiseChanged();

        // Present one frame with the generating state before the synchronous placement.
        yield return null;
        yield return new WaitForEndOfFrame();

        if (Regenerate(next))
        {
            SetCurrentSeed(next);
            cameraController?.ResetView();
        }
        else
        {
            // The failed attempt removed the bases: restore the previous world.
            bool restored = Regenerate(previous);
            LastError = restored
                ? "Could not generate a new world. The previous world was kept - please try again."
                : "Could not generate a new world. Please try again or reload the page.";
            if (restored) cameraController?.ResetView();
        }
        running = null;
        CurrentState = State.Ready;
        RaiseChanged();
    }

    /// <summary>
    /// Copies the canonical link of the current world to the clipboard. Must be
    /// called from a user click. Success is reported only after the browser
    /// confirms it; otherwise a selectable link field is shown.
    /// </summary>
    public bool ShareWorld()
    {
        if (CurrentState != State.Ready || IsCopying || string.IsNullOrEmpty(ShareUrl)) return false;
        IsCopying = true;
        LastShareOutcome = ShareOutcome.None;
        copyUrl = ShareUrl;
        copyStarted = Time.realtimeSinceStartup;
        ExplorerBrowserBridge.CopyText(copyUrl);   // still inside the click: keeps the user activation
        RaiseChanged();
        return true;
    }

    private void Update()
    {
        // Only polls while a clipboard write is pending (no per-frame work otherwise).
        if (!IsCopying) return;
        bool finished = ExplorerBrowserBridge.TryGetCopyResult(out ExplorerBrowserBridge.CopyResult result);
        if (!finished && Time.realtimeSinceStartup - copyStarted < CopyTimeoutSeconds) return;
        if (!finished) result = ExplorerBrowserBridge.CopyResult.Failed; // no answer from the browser: treat as failed
        IsCopying = false;
        if (result == ExplorerBrowserBridge.CopyResult.ClipboardApi) LastShareOutcome = ShareOutcome.Copied;
        else if (result == ExplorerBrowserBridge.CopyResult.ExecCommand) LastShareOutcome = ShareOutcome.CopiedLegacy;
        else LastShareOutcome = ShowFallback(copyUrl) ? ShareOutcome.FallbackShown : ShareOutcome.Failed;
        // A confirmed copy makes an earlier fallback field obsolete.
        if (LastShareOutcome == ShareOutcome.Copied || LastShareOutcome == ShareOutcome.CopiedLegacy)
            ExplorerBrowserBridge.HideLinkField();
        RaiseChanged();
    }

    private const float CopyTimeoutSeconds = 4f; // a real copy answers within milliseconds
    private string copyUrl = string.Empty;
    private float copyStarted;

    /// <summary>Shows the selectable link field without trying the clipboard.</summary>
    public bool ShowFallback(string url)
    {
        return ExplorerBrowserBridge.ShowLinkField(url, FallbackTopCssPx, FallbackLeftCssPx);
    }

    /// <summary>Position of the HTML link field relative to the canvas (CSS px), set by the UI.</summary>
    public int FallbackTopCssPx { get; set; } = 120;
    public int FallbackLeftCssPx { get; set; } = 12;

    private bool Regenerate(int seed)
    {
        var watch = Stopwatch.StartNew();
        bool ok;
        try { ok = worldGenerator.RegenerateBases(seed); }
        catch (Exception exception) { Debug.LogException(exception, this); ok = false; }
        watch.Stop();
        LastRegenerationMs = watch.Elapsed.TotalMilliseconds;
        return ok && worldGenerator.CurrentBaseSeed == seed;
    }

    private void SetCurrentSeed(int seed)
    {
        CurrentSeed = seed;
        ShareUrl = ExplorerSeedUrl.CanonicalUrl(pageAddress, seed);
        ExplorerBrowserBridge.ReplaceQuery(ExplorerSeedUrl.CanonicalQuery(seed));
    }

    private void Fail(string message)
    {
        LastError = message;
        CurrentState = State.Failed;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Action<ExplorerWorldManager> handlers = Changed;
        if (handlers == null) return;
        foreach (Action<ExplorerWorldManager> handler in handlers.GetInvocationList())
        {
            try { handler(this); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }
}
