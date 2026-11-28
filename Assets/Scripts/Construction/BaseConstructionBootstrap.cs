using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Applies the starting construction state to every generated base. Same
/// architecture as BaseMenuBootstrap: created automatically once per Play
/// session (no scene objects, no BaseSpawner changes), listens to
/// WorldGenerationPrototype's GenerationCompleted / BaseRegenerationFinished
/// events, and scans only the active direct "GeneratedBase_*" children of each
/// BaseSpawner after placement has finished. Idempotent: an initialised base is
/// never populated again, so repeated scans, regenerations and the deferred
/// first scan cannot duplicate Flaks or the Refinery. Old worlds' buildings are
/// children of their base roots and are destroyed with them by BaseSpawner.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public sealed class BaseConstructionBootstrap : MonoBehaviour
{
    public const string GeneratedBasePrefix = "GeneratedBase_";

    public struct ScanResult
    {
        public int bases;
        public int initialised;
        public int alreadyInitialised;
        public int notFoundation;
        public int failed;
    }

    private static readonly List<BaseController> controllerBuffer = new List<BaseController>(4);

    [SerializeField] private bool logScanSummary = true;

    private readonly List<WorldGenerationPrototype> tracked = new List<WorldGenerationPrototype>();

    public static BaseConstructionBootstrap Instance { get; private set; }
    public ScanResult LastScan { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        Instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateForPlaySession()
    {
        if (Instance != null || FindAnyObjectByType<BaseConstructionBootstrap>() != null)
        {
            return;
        }

        GameObject host = new GameObject("Base Construction Bootstrap");
        DontDestroyOnLoad(host);
        host.AddComponent<BaseConstructionBootstrap>();
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
    }

    private void Start()
    {
        TrackSceneGenerators();
        StartCoroutine(DeferredInitialScan());
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        foreach (WorldGenerationPrototype g in tracked) Untrack(g);
        tracked.Clear();
        Instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TrackSceneGenerators();
        StartCoroutine(DeferredInitialScan());
    }

    /// <summary>Covers bases that exist before any generation event (e.g. generateOnStart).</summary>
    private IEnumerator DeferredInitialScan()
    {
        yield return null;
        Rescan();
    }

    public void TrackSceneGenerators()
    {
        foreach (WorldGenerationPrototype g in FindObjectsByType<WorldGenerationPrototype>(FindObjectsInactive.Exclude))
        {
            Track(g);
        }
    }

    /// <summary>Subscribes to one generator (idempotent); scans at once if it already finished.</summary>
    public void Track(WorldGenerationPrototype generator)
    {
        if (generator == null || tracked.Contains(generator)) return;
        tracked.Add(generator);
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
        if (generator == null) return;
        generator.GenerationCompleted -= OnGenerationCompleted;
        generator.BaseRegenerationFinished -= OnBaseRegenerationFinished;
    }

    private void OnGenerationCompleted(WorldGenerationPrototype generator) => Rescan();

    private void OnBaseRegenerationFinished(WorldGenerationPrototype generator, bool success) => Rescan();

    [ContextMenu("Rescan Generated Bases")]
    public void Rescan()
    {
        LastScan = InitialiseAll();
        if (logScanSummary && LastScan.bases > 0)
        {
            Debug.Log($"BaseConstructionBootstrap: {LastScan.bases} bases scanned, {LastScan.initialised} initialised " +
                      $"(2 x L1 Flak + 1 x L1 Refinery), {LastScan.alreadyInitialised} already initialised, " +
                      $"{LastScan.notFoundation} not foundations, {LastScan.failed} failed.", this);
        }
    }

    /// <summary>
    /// Initialises every active generated base of every BaseSpawner. Static and
    /// usable from editor tooling; runs only on generation events in play.
    /// </summary>
    public static ScanResult InitialiseAll()
    {
        ScanResult result = default;
        ConstructionSettings settings = ConstructionSettings.Load();
        FlakGrounding.HeightQuery ground = BaseConstruction.FindTerrainQuery();
        foreach (BaseSpawner spawner in FindObjectsByType<BaseSpawner>(FindObjectsInactive.Exclude))
        {
            Transform parent = spawner.transform;
            for (int c = 0; c < parent.childCount; c++)
            {
                Transform root = parent.GetChild(c);
                if (!root.gameObject.activeInHierarchy ||
                    !root.name.StartsWith(GeneratedBasePrefix, System.StringComparison.Ordinal))
                {
                    continue;
                }

                root.GetComponentsInChildren(true, controllerBuffer);
                if (controllerBuffer.Count != 1)
                {
                    controllerBuffer.Clear();
                    result.failed++;
                    continue;
                }

                BaseController controller = controllerBuffer[0];
                controllerBuffer.Clear();
                result.bases++;
                switch (BaseConstruction.InitialiseBase(controller, settings, ground, out string error))
                {
                    case BaseConstruction.InitResult.Initialised: result.initialised++; break;
                    case BaseConstruction.InitResult.AlreadyInitialised: result.alreadyInitialised++; break;
                    case BaseConstruction.InitResult.NotAFoundation: result.notFoundation++; break;
                    default:
                        result.failed++;
                        Debug.LogWarning($"BaseConstructionBootstrap: {controller.BaseId}: {error}", controller);
                        break;
                }
            }
        }

        return result;
    }
}
