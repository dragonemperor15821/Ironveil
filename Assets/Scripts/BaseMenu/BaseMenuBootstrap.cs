using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Base Management Menu (Phase 1A): discovers generated bases and attaches a
/// BaseInteractionTarget to each main base, registering it in BaseRegistry.
///
/// Self-contained: created automatically once per Play session (no scene or
/// BaseSpawner changes). It listens to WorldGenerationPrototype's existing
/// GenerationCompleted and BaseRegenerationFinished events and scans only
/// then, after placement has finished, so it never runs during generation or
/// formation placement. World Explorer regeneration deactivates the old base
/// roots (their targets unregister in OnDisable) before the new bases spawn;
/// the following scan registers the new ones.
///
/// Discovery is restricted to direct "GeneratedBase_*" children of each
/// BaseSpawner and to the single root BaseController inside each, which is
/// the main base. Hangars carry no BaseController and are never targeted.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public sealed class BaseMenuBootstrap : MonoBehaviour
{
    public const string GeneratedBasePrefix = "GeneratedBase_";

    /// <summary>Outcome of one discovery pass.</summary>
    public struct ScanResult
    {
        public int spawners;
        public int baseRoots;
        public int targetsAdded;
        public int registered;
        public int rejected;
        public int pruned;
    }

    private static readonly List<BaseController> controllerBuffer = new List<BaseController>(4);

    [SerializeField] private bool logScanSummary = true;

    private readonly List<WorldGenerationPrototype> tracked = new List<WorldGenerationPrototype>();
    private readonly List<WorldGenerationPrototype.WorldGenerationPhase> trackedPhases =
        new List<WorldGenerationPrototype.WorldGenerationPhase>();

    public static BaseMenuBootstrap Instance { get; private set; }

    /// <summary>Result of the most recent scan.</summary>
    public ScanResult LastScan { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        Instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateForPlaySession()
    {
        if (Instance != null || FindAnyObjectByType<BaseMenuBootstrap>() != null)
        {
            return;
        }

        GameObject host = new GameObject("Base Menu Bootstrap");
        DontDestroyOnLoad(host);
        host.AddComponent<BaseMenuBootstrap>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;

        // Phase 1B: the single hover controller lives on this per-session object.
        if (GetComponent<BaseHoverController>() == null)
        {
            gameObject.AddComponent<BaseHoverController>();
        }

        // Phase 1C: the single menu interaction controller, after the hover controller.
        if (GetComponent<BaseMenuController>() == null)
        {
            gameObject.AddComponent<BaseMenuController>();
        }

        // Phase 1D: the single visual menu (one canvas, one reusable panel).
        if (GetComponent<BaseMenuView>() == null)
        {
            gameObject.AddComponent<BaseMenuView>();
        }

        if (GetComponent<BaseMenuFlakBuildOption>() == null)
        {
            gameObject.AddComponent<BaseMenuFlakBuildOption>();
        }

        // Row 1, Box 1: Upgrade Flaks (independent of Box 2 above).
        if (GetComponent<BaseMenuFlakUpgradeOption>() == null)
        {
            gameObject.AddComponent<BaseMenuFlakUpgradeOption>();
        }
    }

    private void Start()
    {
        TrackSceneGenerators();
        StartCoroutine(DeferredInitialScan());
    }

    private void OnDestroy()
    {
        if (Instance != this)
        {
            return;
        }

        SceneManager.sceneLoaded -= OnSceneLoaded;
        for (int i = 0; i < tracked.Count; i++)
        {
            Untrack(tracked[i]);
        }

        tracked.Clear();
        trackedPhases.Clear();
        Instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TrackSceneGenerators();
        StartCoroutine(DeferredInitialScan());
    }

    /// <summary>
    /// Covers scenes whose bases already exist or are spawned without a
    /// WorldGenerationPrototype (BaseSpawner.generateOnStart): one scan after
    /// every Start has run. Idempotent.
    /// </summary>
    private IEnumerator DeferredInitialScan()
    {
        yield return null;
        Rescan();
    }

    /// <summary>
    /// Only watches tracked generators' phase (normally one) so a generation
    /// that ends as Failed, which raises no event, still gets its bases
    /// registered. No per-base work.
    /// </summary>
    private void Update()
    {
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            WorldGenerationPrototype generator = tracked[i];
            if (generator == null)
            {
                tracked.RemoveAt(i);
                trackedPhases.RemoveAt(i);
                continue;
            }

            WorldGenerationPrototype.WorldGenerationPhase phase = generator.Phase;
            if (phase == trackedPhases[i])
            {
                continue;
            }

            trackedPhases[i] = phase;
            if (phase == WorldGenerationPrototype.WorldGenerationPhase.Failed)
            {
                Rescan();
            }
        }
    }

    /// <summary>Subscribes to every WorldGenerationPrototype in the loaded scenes.</summary>
    public void TrackSceneGenerators()
    {
        WorldGenerationPrototype[] generators =
            FindObjectsByType<WorldGenerationPrototype>(FindObjectsInactive.Exclude);
        for (int i = 0; i < generators.Length; i++)
        {
            Track(generators[i]);
        }
    }

    /// <summary>
    /// Subscribes to one generator's completion events (idempotent). Scans
    /// immediately if it already finished before we subscribed.
    /// </summary>
    public void Track(WorldGenerationPrototype generator)
    {
        if (generator == null || tracked.Contains(generator))
        {
            return;
        }

        tracked.Add(generator);
        trackedPhases.Add(generator.Phase);
        generator.GenerationCompleted += OnGenerationCompleted;
        generator.BaseRegenerationFinished += OnBaseRegenerationFinished;

        if (generator.IsGenerationComplete ||
            generator.Phase == WorldGenerationPrototype.WorldGenerationPhase.BasesRegenerated ||
            generator.Phase == WorldGenerationPrototype.WorldGenerationPhase.BaseRegenerationFailed ||
            generator.Phase == WorldGenerationPrototype.WorldGenerationPhase.Failed)
        {
            Rescan();
        }
    }

    private void Untrack(WorldGenerationPrototype generator)
    {
        if (generator == null)
        {
            return;
        }

        generator.GenerationCompleted -= OnGenerationCompleted;
        generator.BaseRegenerationFinished -= OnBaseRegenerationFinished;
    }

    private void OnGenerationCompleted(WorldGenerationPrototype generator)
    {
        Rescan();
    }

    private void OnBaseRegenerationFinished(WorldGenerationPrototype generator, bool success)
    {
        // Register whatever exists, including a partial (failed) regeneration.
        Rescan();
    }

    /// <summary>Discovers every generated base in the loaded scenes and registers it.</summary>
    [ContextMenu("Rescan Generated Bases")]
    public void Rescan()
    {
        LastScan = ScanAll();
        if (logScanSummary && LastScan.baseRoots > 0)
        {
            Debug.Log(BuildSummary(LastScan), this);
        }
    }

    /// <summary>
    /// Static discovery pass over all BaseSpawners. Runs only on generation
    /// events, never per frame. Usable from editor tooling as well.
    /// </summary>
    public static ScanResult ScanAll()
    {
        ScanResult result = default;
        result.pruned = BaseRegistry.PruneDestroyed();

        BaseSpawner[] spawners = FindObjectsByType<BaseSpawner>(FindObjectsInactive.Exclude);
        for (int i = 0; i < spawners.Length; i++)
        {
            ScanSpawner(spawners[i], ref result);
        }

        return result;
    }

    /// <summary>Attaches and registers targets for one spawner's generated bases.</summary>
    public static void ScanSpawner(BaseSpawner spawner, ref ScanResult result)
    {
        if (spawner == null)
        {
            return;
        }

        result.spawners++;
        Transform parent = spawner.transform;
        for (int c = 0; c < parent.childCount; c++)
        {
            Transform root = parent.GetChild(c);
            if (!root.gameObject.activeInHierarchy ||
                !root.name.StartsWith(GeneratedBasePrefix, System.StringComparison.Ordinal))
            {
                continue;
            }

            // BaseSpawner guarantees one BaseController, on the main base's
            // root: the GeneratedBase_* root itself for land bases, its
            // "<Type> Main Base" child for Naval/Rail bases (hangar sibling).
            root.GetComponentsInChildren(true, controllerBuffer);
            if (controllerBuffer.Count != 1)
            {
                result.rejected++;
                Debug.LogWarning(
                    $"BaseMenuBootstrap: '{root.name}' has {controllerBuffer.Count} BaseControllers; skipped.", root);
                controllerBuffer.Clear();
                continue;
            }

            BaseController controller = controllerBuffer[0];
            controllerBuffer.Clear();
            result.baseRoots++;

            BaseInteractionTarget target = controller.GetComponent<BaseInteractionTarget>();
            if (target == null)
            {
                target = controller.gameObject.AddComponent<BaseInteractionTarget>();
                result.targetsAdded++;
            }

            bool alreadyBound = target.Context != null &&
                                target.Context.Controller == controller &&
                                target.Context.BaseId == controller.BaseId;
            bool ok = alreadyBound
                ? BaseRegistry.Register(target)
                : target.Bind(controller, controller.GetComponent<BaseLevelInfo>());

            if (ok) result.registered++;
            else result.rejected++;
        }
    }

    private static string BuildSummary(ScanResult scan)
    {
        List<BaseInteractionTarget> all = new List<BaseInteractionTarget>(BaseRegistry.Count);
        BaseRegistry.GetAll(all);
        SortedDictionary<string, int> byType = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
        foreach (BaseInteractionTarget t in all)
        {
            string type = t.Context.BaseType;
            byType[type] = byType.TryGetValue(type, out int n) ? n + 1 : 1;
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("BaseMenuBootstrap: ").Append(all.Count).Append(" bases registered (")
          .Append(scan.targetsAdded).Append(" targets added, ")
          .Append(scan.pruned).Append(" stale pruned, ")
          .Append(scan.rejected).Append(" rejected):");
        foreach (KeyValuePair<string, int> pair in byType)
        {
            sb.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
        }

        return sb.ToString();
    }
}
