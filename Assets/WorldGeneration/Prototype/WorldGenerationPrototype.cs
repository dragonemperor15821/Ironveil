using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// Isolated, runtime-generated snowy battlefield prototype. It creates only
/// Terrain/TerrainCollider chunks and visual guide lines; it does not call or
/// modify BaseSpawner, RiverGenerator, or RailGenerator.
/// </summary>
public sealed class WorldGenerationPrototype : MonoBehaviour
{
    private const uint RiverStream = 0xA511E9B3u;
    private const uint RailStream = 0x63D83595u;
    private const uint PlainStream = 0xB5297A4Du;
    private const uint DetailStream = 0xD1B54A35u;
    private const float NavalVisibleWaterOverlapTarget = 0.85f;

    private static readonly Vector2[] FootprintSampleCoordinates =
    {
        new Vector2(0f, 0f),
        new Vector2(1f, 0f),
        new Vector2(-1f, 0f),
        new Vector2(0f, 1f),
        new Vector2(0f, -1f),
        new Vector2(1f, 1f),
        new Vector2(1f, -1f),
        new Vector2(-1f, 1f),
        new Vector2(-1f, -1f)
    };

    [SerializeField] private WorldGenerationPrototypeConfig config;
    [SerializeField] private Camera prototypeCamera;
    [SerializeField] private WorldTerrainQueryProvider terrainQueryProvider;
    [SerializeField] private RiverGenerator riverGenerator;
    [SerializeField] private RailGenerator railGenerator;
    [Tooltip("TerrainV2: generators for the 2nd, 3rd... planned rivers (river guide i+1 uses element i).")]
    [SerializeField] private RiverGenerator[] additionalRiverGenerators = new RiverGenerator[0];
    [Tooltip("TerrainV2: generators for the 2nd... planned railways.")]
    [SerializeField] private RailGenerator[] additionalRailGenerators = new RailGenerator[0];
    [SerializeField] private BaseSpawner baseSpawner;
    [SerializeField] private bool runBaseSpawnerAfterGeneration;
    // Corridor guide lines, the capacity-screening report and its on-screen
    // overlay are test-scene diagnostics; production scenes turn them off.
    [SerializeField] private bool showPrototypeDiagnostics = true;
    [Tooltip("Debug only: floating orange/blue LineRenderer ribbons along the planned rail and river guides. They hide the real track and water, so they are off by default.")]
    [SerializeField] private bool showRouteGuideOverlays = false;
    [Tooltip("Keep ordinary land bases out of every railway corridor (plus the spawner's clearance), also with a single railway.")]
    [SerializeField] private bool keepLandBasesOffRailways = true;
    [SerializeField, Min(0)] private int primaryRiverGuideIndex = 1;
    [SerializeField, Min(0)] private int primaryRailGuideIndex = 0;

    [Header("Base Placement Seed")]
    [Tooltip("Off (default): bases use the world seed, exactly as before. On: bases use Base Seed Override instead. Terrain, hills, river and railway always use the world seed.")]
    [SerializeField] private bool useBaseSeedOverride = false;
    [SerializeField] private int baseSeedOverride = 12345;

    [Header("Generation Scheduling")]
    [Tooltip("Terrain generation runs complete heightmap and splat-map rows until this much main-thread time (ms) has passed in the current frame, then yields. Editor and desktop players.")]
    [SerializeField, Min(0.5f)] private float generationSliceBudgetMs = 5f;
    [Tooltip("Same slice budget for Web builds (single-threaded browser main thread).")]
    [SerializeField, Min(0.5f)] private float webGenerationSliceBudgetMs = 5f;

    [TextArea(8, 20)]
    [SerializeField] private string lastDiagnosticReport;

    [TextArea(8, 20)]
    [SerializeField] private string lastBaseSpawnerReport;

    private readonly List<Guide> riverGuides = new List<Guide>();
    private readonly List<Guide> railGuides = new List<Guide>();
    private readonly List<Vector3[]> riverGuideControlPoints = new List<Vector3[]>();
    private readonly List<Vector3[]> railGuideControlPoints = new List<Vector3[]>();
    private readonly List<Vector2> plainCenters = new List<Vector2>();
    private readonly List<Terrain> terrainChunks = new List<Terrain>();
    private readonly List<TerrainData> generatedTerrainData = new List<TerrainData>();

    // Stage 3D: reusable work buffers owned by this generator instance.
    // TerrainData.SetHeights/SetAlphamaps copy their input, so the chunk
    // buffers are refilled for every chunk; every element is overwritten
    // before use. terrainBuffersInUse guards against two overlapping terrain
    // generations on this instance: a second one uses private arrays instead.
    private float[,] heightBuffer;
    private float[,,] splatBuffer;
    private bool terrainBuffersInUse;
    private float[,] carveDistanceBuffer;
    private float[,] carveWaterBuffer;
    private readonly List<LineRenderer> guideRenderers = new List<LineRenderer>();
    private readonly List<Material> generatedGuideMaterials = new List<Material>();
    private readonly List<Vector3> potentialCrossings = new List<Vector3>();

    // TerrainV2 ground layers: three copies of the same rocky albedo/normal
    // at different scales/offsets (anti-tiling) plus a snow-dusting copy.
    // Per-chunk copies (world-continuous UVs); all are tracked for cleanup.
    private readonly List<TerrainLayer> generatedGroundLayers = new List<TerrainLayer>();
    private string lastError;
    private string baseSpawnReport = string.Empty;
    private float generationProgress;
    private bool baseSpawnerWasRun;
    private readonly List<string> baseSpawnerErrors = new List<string>();
    private readonly Dictionary<string, Dictionary<string, int>>
        rejectedAttemptsByVariant =
            new Dictionary<string, Dictionary<string, int>>();

    private static readonly string[] BaseVariantNames =
    {
        "Tank", "Heli", "Air", "Naval", "Rail",
        "Heli Naval", "Heli Rail", "Air Naval", "Air Rail"
    };

    private static readonly int[] RequestedBaseCounts =
    {
        110, 90, 80, 25, 17, 11, 7, 6, 4
    };

    public string LastDiagnosticReport => lastDiagnosticReport;
    public string LastError => lastError;
    public int WorldSeed => config != null ? config.WorldSeed : 0;
    public bool IsGenerated => config != null &&
        terrainChunks.Count == config.ChunksX * config.ChunksZ;
    public IReadOnlyList<Terrain> TerrainChunks => terrainChunks;

    /// <summary>
    /// True once the current generation has finished successfully: terrain,
    /// routes and every requested base spawned. Reset to false when a new
    /// generation begins. Late subscribers check this instead of waiting.
    /// </summary>
    public bool IsGenerationComplete { get; private set; }

    /// <summary>
    /// Raised exactly once per successful production generation, after the
    /// terrain, the river and railway and all requested bases exist. Not
    /// raised for failed or incomplete generations or editor previews.
    /// </summary>
    public event Action<WorldGenerationPrototype> GenerationCompleted;

    /// <summary>Lifecycle of the full world generation and of base-only regeneration.</summary>
    public enum WorldGenerationPhase
    {
        Idle,                    // nothing generated yet, or an editor preview
        Generating,              // initial (full) world generation running
        Completed,               // initial generation succeeded: terrain, routes, all bases
        Failed,                  // initial generation finished without every base
        RegeneratingBases,       // RegenerateBases is running
        BasesRegenerated,        // the last RegenerateBases succeeded
        BaseRegenerationFailed   // the last RegenerateBases failed; terrain and routes are intact
    }

    public WorldGenerationPhase Phase { get; private set; }

    /// <summary>
    /// Seed BaseSpawner receives during the full generation: the world seed
    /// unless the base seed override is enabled.
    /// </summary>
    public int BaseSeed => useBaseSeedOverride ? baseSeedOverride : WorldSeed;

    /// <summary>Seed of the bases currently placed (initial generation or last regeneration).</summary>
    public int CurrentBaseSeed { get; private set; }

    /// <summary>
    /// Raised after every RegenerateBases call that ran (true = exactly the
    /// requested 350 bases now exist). Separate from GenerationCompleted,
    /// which belongs to the full world generation only.
    /// </summary>
    public event Action<WorldGenerationPrototype, bool> BaseRegenerationFinished;
    public IReadOnlyList<Vector3[]> RiverGuideControlPoints => riverGuideControlPoints;
    public IReadOnlyList<Vector3[]> RailGuideControlPoints => railGuideControlPoints;
    public IReadOnlyList<Vector3> PotentialGuideCrossings => potentialCrossings;
    public float GenerationProgress => generationProgress;
    public Bounds PrototypeWorldBounds => config == null
        ? new Bounds(Vector3.zero, Vector3.zero)
        : new Bounds(Vector3.up * config.TerrainHeight * 0.5f,
            new Vector3(config.MapSize.x, config.TerrainHeight, config.MapSize.y));

    private sealed class Guide
    {
        public string name;
        public Vector3[] controls;
        public Vector3[] samples;
        public float[] cumulativeDistances;
        public float length;
        public bool progressesAlongZ;
        // Segment grid for DistanceToGuide (TerrainV2).
        public List<int>[] cells;
        // Per cell, the same candidate segments sorted by a conservative lower
        // bound on their distance from any point in the cell (Stage 3B).
        public int[][] cellSegments;
        public float[][] cellBounds;
        public float[][] cellBoundsSqr;
        public Vector2 gridOrigin;
        public int gridWidth;
        public int gridHeight;
        public float gridReach;
    }

    private struct Footprint
    {
        public Vector3 center;
        public Vector3 size;
        public bool valid;
    }

    private struct StableRandom
    {
        private uint state;

        public StableRandom(uint seed)
        {
            state = seed == 0u ? 0x9E3779B9u : seed;
        }

        public uint NextUInt()
        {
            uint value = state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            state = value == 0u ? 0xA341316Cu : value;
            return state;
        }

        public float Next01()
        {
            return (NextUInt() >> 8) * (1f / 16777215f);
        }

        public float Range(float min, float max)
        {
            return Mathf.Lerp(min, max, Next01());
        }
    }

    private IEnumerator Start()
    {
        if (config == null || !config.GenerateOnPlay)
        {
            yield break;
        }

        // StartCoroutine gives Unity the nested iterator explicitly. This is
        // important because terrain generation yields incrementally between
        // rows and tiles rather than completing one huge frame-bound step.
        yield return StartCoroutine(GeneratePrototype(runBaseSpawnerAfterGeneration));
    }

    /// <summary>
    /// Runs the full generation and records its lifecycle phase. The steps
    /// and their yields are passed through unchanged; every early exit of the
    /// steps ends here, so the phase can never stay at Generating after the
    /// steps have finished.
    /// </summary>
    private IEnumerator GeneratePrototype(bool runProductionPlacement)
    {
        Phase = WorldGenerationPhase.Generating;
        IEnumerator steps = GeneratePrototypeSteps(runProductionPlacement);
        while (steps.MoveNext())
        {
            yield return steps.Current;
        }
        if (!IsGenerationComplete)
        {
            Phase = runProductionPlacement
                ? WorldGenerationPhase.Failed
                : WorldGenerationPhase.Idle;
        }
    }

    private IEnumerator GeneratePrototypeSteps(bool runProductionPlacement)
    {
        // A new generation invalidates any previous completion.
        IsGenerationComplete = false;

        if (config == null)
        {
            lastError = "Assign the prototype configuration asset.";
            Debug.LogError("WorldGenerationPrototype: " + lastError, this);
            yield break;
        }

        if (!ValidateConfiguration())
        {
            Debug.LogError("WorldGenerationPrototype: " + lastError, this);
            yield break;
        }

        baseSpawnReport = string.Empty;
        lastBaseSpawnerReport = string.Empty;
        baseSpawnerWasRun = false;
        rejectedAttemptsByVariant.Clear();
        generationProgress = 0f;
        Debug.Log("WorldGenerationPrototype: GeneratePrototype started, runProductionPlacement=" + runProductionPlacement);
        BuildGeographicLayout();
        // TerrainV2: the floating route guide ribbons (orange rail, blue river
        // LineRenderers) are debug-only and off by default; they covered the
        // real track and river water. Placement never uses them.
        if (showPrototypeDiagnostics && showRouteGuideOverlays)
        {
            CreateGuideVisuals();
        }
        else
        {
            ClearGuideVisuals();
        }
        IEnumerator terrainGeneration = GenerateTerrainChunks();
        while (terrainGeneration.MoveNext())
        {
            yield return terrainGeneration.Current;
        }

        if (terrainChunks.Count == 0)
        {
            Debug.LogError(
                "WorldGenerationPrototype: Terrain generation produced no chunks.",
                this);
            yield break;
        }

        ConfigureTerrainNeighbors();
        FramePrototypeCamera();
        Physics.SyncTransforms();

        if (terrainQueryProvider == null)
        {
            terrainQueryProvider = GetComponent<WorldTerrainQueryProvider>();
        }
        if (terrainQueryProvider == null)
        {
            terrainQueryProvider = gameObject.AddComponent<WorldTerrainQueryProvider>();
        }
        if (!terrainQueryProvider.SetTerrainTiles(
                terrainChunks, config.MapSize, config.ChunksX, config.ChunksZ))
        {
            lastError = "Multi-terrain query cache could not be initialized.";
            Debug.LogError(
                "WorldGenerationPrototype: " + lastError,
                this);
            yield break;
        }

        if (baseSpawner != null)
        {
            baseSpawner.SetTerrainQueryProvider(terrainQueryProvider);
        }
        if (riverGenerator != null)
        {
            riverGenerator.SetTerrainQueryProvider(terrainQueryProvider);
        }
        if (railGenerator != null)
        {
            railGenerator.SetTerrainQueryProvider(terrainQueryProvider);
        }
        foreach (RiverGenerator extra in additionalRiverGenerators)
        {
            if (extra != null) extra.SetTerrainQueryProvider(terrainQueryProvider);
        }
        foreach (RailGenerator extra in additionalRailGenerators)
        {
            if (extra != null) extra.SetTerrainQueryProvider(terrainQueryProvider);
        }

        if (!runProductionPlacement)
        {
            baseSpawnReport = runBaseSpawnerAfterGeneration
                ? "Editor preview only: route meshes and BaseSpawner are deferred to Play Mode."
                : "Editor preview only: production route and base generation is disabled.";
        }
        else
        {
            bool routesReady = GenerateGuidedRoutes();
            if (baseSpawner == null)
            {
                baseSpawnReport =
                    "BaseSpawner was requested but is not assigned in this isolated test scene.";
                Debug.LogError("WorldGenerationPrototype: " + baseSpawnReport, this);
            }
            else if (!routesReady)
            {
                baseSpawnReport =
                    "BaseSpawner was not invoked because a configured route could not be generated.";
                Debug.LogError("WorldGenerationPrototype: " + baseSpawnReport, this);
            }
            else
            {
                RunProductionBaseSpawner(BaseSeed);
            }
        }

        if (showPrototypeDiagnostics)
        {
            BuildDiagnosticReport();
        }

        // Completion notification: only for a successful production run in
        // which every requested base of every variant actually spawned.
        if (runProductionPlacement && baseSpawnerWasRun && AllRequestedBasesSpawned())
        {
            IsGenerationComplete = true;
            Phase = WorldGenerationPhase.Completed;
            RaiseGenerationCompleted();
        }
    }

    private bool AllRequestedBasesSpawned()
    {
        List<Transform> roots = GetSuccessfulGeneratedRoots();
        for (int variantIndex = 0; variantIndex < BaseVariantNames.Length; variantIndex++)
        {
            string variant = BaseVariantNames[variantIndex];
            if (roots.Count(root => RootIsVariant(root, variant)) != RequestedBaseCounts[variantIndex])
                return false;
        }
        return true;
    }

    private void RaiseGenerationCompleted()
    {
        Action<WorldGenerationPrototype> handlers = GenerationCompleted;
        if (handlers == null) return;
        // One failing subscriber must not stop the others or the generator.
        foreach (Action<WorldGenerationPrototype> handler in handlers.GetInvocationList())
        {
            try { handler(this); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    // ------------------------------------------------------------------
    // Base-only regeneration (World Explorer stage 1)
    // ------------------------------------------------------------------

    private const string GeneratedBasePrefix = "GeneratedBase_";
    private const string RemovedBasePrefix = "RemovedBase_";

    /// <summary>
    /// Replaces the placed bases with the arrangement for baseSeed on the
    /// existing terrain. Terrain, hills, river, railway, terrain buffers and
    /// the terrain-query cache are reused untouched; BaseSpawner's placement
    /// runs synchronously exactly as in the full generation. Allowed only
    /// after the initial generation completed and while no other
    /// regeneration runs. Returns true when exactly the requested 350 bases
    /// (exact per-variant counts) exist afterwards. Never raises
    /// GenerationCompleted; raises BaseRegenerationFinished instead.
    /// </summary>
    public bool RegenerateBases(int baseSeed)
    {
        if (Phase == WorldGenerationPhase.RegeneratingBases)
        {
            Debug.LogWarning("WorldGenerationPrototype: RegenerateBases(" + baseSeed +
                ") refused: a base regeneration is already running.", this);
            return false;
        }
        if (Phase != WorldGenerationPhase.Completed &&
            Phase != WorldGenerationPhase.BasesRegenerated &&
            Phase != WorldGenerationPhase.BaseRegenerationFailed)
        {
            Debug.LogWarning("WorldGenerationPrototype: RegenerateBases(" + baseSeed +
                ") refused: the initial world generation has not completed (phase " + Phase + ").", this);
            return false;
        }
        if (!RegenerationReferencesValid(out string problem))
        {
            Debug.LogError("WorldGenerationPrototype: RegenerateBases(" + baseSeed +
                ") refused: " + problem, this);
            return false;
        }

        Phase = WorldGenerationPhase.RegeneratingBases;
        bool success = false;
        try
        {
            RemoveGeneratedBaseRoots();
            baseSpawnReport = string.Empty;
            lastBaseSpawnerReport = string.Empty;
            baseSpawnerWasRun = false;
            RunProductionBaseSpawner(baseSeed);
            success = baseSpawnerWasRun && AllRequestedBasesSpawned();
            if (!success)
            {
                Debug.LogError("WorldGenerationPrototype: RegenerateBases(" + baseSeed +
                    ") did not produce exactly the requested bases. " + baseSpawnReport, this);
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
        }
        finally
        {
            Phase = success
                ? WorldGenerationPhase.BasesRegenerated
                : WorldGenerationPhase.BaseRegenerationFailed;
        }

        Action<WorldGenerationPrototype, bool> handlers = BaseRegenerationFinished;
        if (handlers != null)
        {
            foreach (Action<WorldGenerationPrototype, bool> handler in handlers.GetInvocationList())
            {
                try { handler(this, success); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }
        return success;
    }

    /// <summary>
    /// The objects base placement reads must still be the ones the initial
    /// generation built: spawner, ready terrain-query cache, every terrain
    /// chunk with its collider, and every river and railway route. (Missing
    /// routes would make BaseSpawner build new ones, so they are required.)
    /// </summary>
    private bool RegenerationReferencesValid(out string problem)
    {
        problem = string.Empty;
        if (baseSpawner == null) { problem = "BaseSpawner is not assigned."; return false; }
        if (terrainQueryProvider == null || !terrainQueryProvider.IsReady)
        {
            problem = "the terrain-query cache is not ready.";
            return false;
        }
        if (config == null || terrainChunks.Count != config.ChunksX * config.ChunksZ)
        {
            problem = "the terrain chunks are incomplete.";
            return false;
        }
        foreach (Terrain chunk in terrainChunks)
        {
            if (chunk == null || chunk.terrainData == null)
            {
                problem = "a terrain chunk was destroyed.";
                return false;
            }
            TerrainCollider collider = chunk.GetComponent<TerrainCollider>();
            if (collider == null || !collider.enabled)
            {
                problem = "a terrain chunk collider is missing or disabled.";
                return false;
            }
        }
        foreach (RiverGenerator river in RiverGeneratorList())
        {
            if (river.GeneratedRoute == null || river.GeneratedRoute.Count < 3)
            {
                problem = "river '" + river.name + "' has no generated route.";
                return false;
            }
        }
        foreach (RailGenerator rail in RailGeneratorList())
        {
            if (rail.GeneratedRoute == null || rail.GeneratedRoute.Count < 3)
            {
                problem = "railway '" + rail.name + "' has no generated route.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Removes only the generated base roots: direct children of the
    /// BaseSpawner whose name starts with "GeneratedBase_" (the same filter
    /// BaseSpawner uses). Each root is deactivated, renamed and detached
    /// immediately, so it leaves every name- and hierarchy-based lookup in
    /// this frame, and is then destroyed (deferred in Play Mode). A root that
    /// contains terrain or route components is never touched.
    /// </summary>
    private int RemoveGeneratedBaseRoots()
    {
        Transform parent = baseSpawner.transform;
        int removed = 0;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            if (!child.name.StartsWith(GeneratedBasePrefix, StringComparison.Ordinal)) continue;
            if (child.GetComponentInChildren<Terrain>(true) != null ||
                child.GetComponentInChildren<TerrainCollider>(true) != null ||
                child.GetComponentInChildren<RiverGenerator>(true) != null ||
                child.GetComponentInChildren<RailGenerator>(true) != null ||
                child.GetComponentInChildren<BaseSpawner>(true) != null)
            {
                Debug.LogError("WorldGenerationPrototype: '" + child.name +
                    "' contains terrain or route components and was not removed.", child);
                continue;
            }

            GameObject root = child.gameObject;
            root.SetActive(false);
            root.name = RemovedBasePrefix + root.name.Substring(GeneratedBasePrefix.Length);
            child.SetParent(null, true);
            DestroyGeneratedObject(root);
            removed++;
        }
        return removed;
    }

#if UNITY_EDITOR
    public bool IsGeneratingPreview { get; private set; }

    public IEnumerator GenerateEditorPreview()
    {
        IsGeneratingPreview = true;
        IEnumerator preview = GeneratePrototype(false);
        while (preview.MoveNext()) yield return preview.Current;
        IsGeneratingPreview = false;
    }

    public void CancelEditorPreview()
    {
        IsGeneratingPreview = false;
        ClearEditorPreview();
    }

    public void ClearEditorPreview()
    {
        ClearGuideVisuals();
        CleanupGeneratedTerrain();
        if (terrainQueryProvider != null) terrainQueryProvider.ClearTerrainTiles();
        if (baseSpawner != null)
        {
            MethodInfo clearGenerated = typeof(BaseSpawner).GetMethod(
                "ClearGeneratedBases",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (clearGenerated != null) clearGenerated.Invoke(baseSpawner, null);
        }
        DestroyGroundLayers();
        lastDiagnosticReport = string.Empty;
        lastBaseSpawnerReport = string.Empty;
        baseSpawnReport = string.Empty;
        lastError = string.Empty;
        baseSpawnerWasRun = false;
        rejectedAttemptsByVariant.Clear();
        lastError = string.Empty;
        generationProgress = 0f;
        Phase = WorldGenerationPhase.Idle;
    }
#endif

    private List<RiverGenerator> RiverGeneratorList()
    {
        List<RiverGenerator> list = new List<RiverGenerator>();
        if (riverGenerator != null) list.Add(riverGenerator);
        foreach (RiverGenerator extra in additionalRiverGenerators)
            if (extra != null && !list.Contains(extra)) list.Add(extra);
        return list;
    }

    private List<RailGenerator> RailGeneratorList()
    {
        List<RailGenerator> list = new List<RailGenerator>();
        if (railGenerator != null) list.Add(railGenerator);
        foreach (RailGenerator extra in additionalRailGenerators)
            if (extra != null && !list.Contains(extra)) list.Add(extra);
        return list;
    }

    private bool GenerateGuidedRoutes()
    {
        bool routesReady = true;
        List<RiverGenerator> rivers = RiverGeneratorList();
        List<RailGenerator> rails = RailGeneratorList();
        Debug.Log("WorldGenerationPrototype: GenerateGuidedRoutes called, river generators=" +
            rivers.Count + ", rail generators=" + rails.Count);

        // Guided route generation: each planned guide is handed to its own
        // unchanged generator, which projects it through the terrain-query
        // provider. A failed projection is reported, never silently replaced.
        // With a single generator per family the legacy primary guide index
        // is honoured; with several, generator i builds planned guide i.
        for (int i = 0; i < rivers.Count; i++)
        {
            RiverGenerator river = rivers[i];
            if (riverGuideControlPoints.Count <= (rivers.Count == 1 ? 0 : i))
            {
                Debug.LogError("WorldGenerationPrototype: No river guide is available for " + river.name + ".", this);
                routesReady = false;
                continue;
            }
            int guideIndex = rivers.Count == 1
                ? Mathf.Clamp(primaryRiverGuideIndex, 0, riverGuideControlPoints.Count - 1)
                : i;
            river.SetRouteGuide(riverGuideControlPoints[guideIndex]);
            river.GenerateRiver();
            Debug.Log("WorldGenerationPrototype: " + river.name + " built guide " + (guideIndex + 1) +
                ", route count=" + (river.GeneratedRoute != null ? river.GeneratedRoute.Count : 0));
            if (river.GeneratedRoute == null || river.GeneratedRoute.Count < 3)
            {
                Debug.LogError("WorldGenerationPrototype: " + river.name +
                    " did not produce a route from its guide.", this);
                routesReady = false;
            }
            else
            {
                CarveRiverChannel(river.GeneratedRoute, river.RiverWidth);
            }
        }

        for (int i = 0; i < rails.Count; i++)
        {
            RailGenerator rail = rails[i];
            if (railGuideControlPoints.Count <= (rails.Count == 1 ? 0 : i))
            {
                Debug.LogError("WorldGenerationPrototype: No rail guide is available for " + rail.name + ".", this);
                routesReady = false;
                continue;
            }
            int guideIndex = rails.Count == 1
                ? Mathf.Clamp(primaryRailGuideIndex, 0, railGuideControlPoints.Count - 1)
                : i;
            rail.SetRouteGuide(railGuideControlPoints[guideIndex]);
            rail.GenerateRail();
            Debug.Log("WorldGenerationPrototype: " + rail.name + " built guide " + (guideIndex + 1) +
                ", route count=" + (rail.GeneratedRoute != null ? rail.GeneratedRoute.Count : 0));
            if (rail.GeneratedRoute == null || rail.GeneratedRoute.Count < 3)
            {
                Debug.LogError("WorldGenerationPrototype: " + rail.name +
                    " did not produce a route from its guide.", this);
                routesReady = false;
                continue;
            }
            float grade = MaximumRouteGrade(rail.GeneratedRoute, 10f);
            if (grade > config.MaximumRailGrade)
                Debug.LogWarning(string.Format(
                    "WorldGenerationPrototype: {0} maximum grade {1:P2} exceeds the {2:P2} limit.",
                    rail.name, grade, config.MaximumRailGrade), this);
        }

        if (baseSpawner != null)
        {
            // Hand the extra generators to the spawner. Empty arrays (single
            // river and railway) keep the spawner on its route-selection
            // path for one route per family; the land-off-railway rule is
            // kept on independently of the route count.
            bool multiRoute = additionalRiverGenerators.Length > 0 || additionalRailGenerators.Length > 0;
            baseSpawner.SetAdditionalRouteGenerators(
                rivers.Skip(1).ToArray(), rails.Skip(1).ToArray(),
                multiRoute || keepLandBasesOffRailways);
        }

        if (baseSpawner != null &&
            ((rivers.Count == 0 && RequestedCountForPlacement(true) > 0) ||
             (rails.Count == 0 && RequestedCountForPlacement(false) > 0)))
        {
            routesReady = false;
        }

        if (terrainQueryProvider != null)
        {
            terrainQueryProvider.RecordDiagnostics();
        }

        Debug.Log("WorldGenerationPrototype: GenerateGuidedRoutes returning routesReady=" + routesReady);
        return routesReady;
    }

    /// <summary>Steepest rise/run over chords of at least minimumRun metres.</summary>
    private static float MaximumRouteGrade(IReadOnlyList<Vector3> route, float minimumRun)
    {
        float grade = 0f;
        for (int i = 0; i < route.Count; i++)
        {
            for (int j = i + 1; j < route.Count; j++)
            {
                float run = DistanceXZ(route[i], route[j]);
                if (run < minimumRun) continue;
                grade = Mathf.Max(grade, Mathf.Abs(route[j].y - route[i].y) / run);
                break;
            }
        }
        return grade;
    }

    /// <summary>
    /// Cuts a shallow channel into the terrain along the published river so the
    /// bed under the water ribbon always lies ChannelDepth below the water
    /// surface (the route's Y). Across the water edge the bed ramps smoothly up
    /// to just below the water line (BankEdgeDrop), and the bank then blends
    /// gently back to the original terrain, so no dry trench or steep step is
    /// cut beside the water where riverbank bases stand. Terrain is only ever
    /// lowered. Every height is computed from its world position, so samples
    /// shared by neighbouring tiles stay identical.
    /// </summary>
    private void CarveRiverChannel(IReadOnlyList<Vector3> waterRoute, float riverWidth)
    {
        const float ChannelDepth = 0.25f;
        const float BankEdgeDrop = 0.02f;
        // About one heightmap cell (0.59 m) either side of the water edge.
        const float EdgeRamp = 0.6f;
        const float BankBlend = 8f;
        if (waterRoute == null || waterRoute.Count < 2) return;

        float channelHalfWidth = riverWidth * 0.5f;
        float bankStart = channelHalfWidth + EdgeRamp;
        float reach = bankStart + BankBlend;
        int carvedSamples = 0;
        float deepestCut = 0f;

        foreach (Terrain terrain in terrainChunks)
        {
            if (terrain == null) continue;
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            int resolution = data.heightmapResolution;
            float step = size.x / (resolution - 1);

            // Nearest route distance and water level per heightmap sample,
            // accumulated segment by segment over each segment's bounding box.
            float[,] distance = null;
            float[,] water = null;
            for (int s = 0; s < waterRoute.Count - 1; s++)
            {
                Vector3 a = waterRoute[s];
                Vector3 b = waterRoute[s + 1];
                int minX = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - reach - origin.x) / step));
                int maxX = Mathf.Min(resolution - 1, Mathf.CeilToInt((Mathf.Max(a.x, b.x) + reach - origin.x) / step));
                int minZ = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, b.z) - reach - origin.z) / step));
                int maxZ = Mathf.Min(resolution - 1, Mathf.CeilToInt((Mathf.Max(a.z, b.z) + reach - origin.z) / step));
                if (minX > maxX || minZ > maxZ) continue;

                if (distance == null)
                {
                    // Stage 3D: reuse the instance carve buffers (this method
                    // never yields), prepared exactly like fresh arrays:
                    // distance = +infinity everywhere, water = 0 everywhere.
                    if (carveDistanceBuffer == null || carveDistanceBuffer.GetLength(0) != resolution || carveDistanceBuffer.GetLength(1) != resolution)
                    {
                        carveDistanceBuffer = new float[resolution, resolution];
                        carveWaterBuffer = new float[resolution, resolution];
                    }
                    distance = carveDistanceBuffer;
                    water = carveWaterBuffer;
                    Array.Clear(water, 0, water.Length);
                    for (int z = 0; z < resolution; z++)
                        for (int x = 0; x < resolution; x++)
                            distance[z, x] = float.PositiveInfinity;
                }

                Vector2 segmentStart = new Vector2(a.x, a.z);
                Vector2 segment = new Vector2(b.x - a.x, b.z - a.z);
                float lengthSqr = segment.sqrMagnitude;
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        Vector2 p = new Vector2(origin.x + x * step, origin.z + z * step);
                        float t = lengthSqr > 1e-8f
                            ? Mathf.Clamp01(Vector2.Dot(p - segmentStart, segment) / lengthSqr)
                            : 0f;
                        float d = (p - (segmentStart + segment * t)).magnitude;
                        if (d < distance[z, x])
                        {
                            distance[z, x] = d;
                            water[z, x] = Mathf.Lerp(a.y, b.y, t);
                        }
                    }
                }
            }

            if (distance == null) continue;

            float[,] heights = data.GetHeights(0, 0, resolution, resolution);
            bool changed = false;
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float d = distance[z, x];
                    if (d >= reach) continue;
                    float current = origin.y + heights[z, x] * size.y;
                    float bed = water[z, x] - ChannelDepth;
                    float edge = water[z, x] - BankEdgeDrop;
                    float target = d <= bankStart
                        ? Mathf.Lerp(bed, edge, SmoothStep(channelHalfWidth - EdgeRamp, bankStart, d))
                        : Mathf.Lerp(edge, current, SmoothStep(bankStart, reach, d));
                    if (target >= current - 0.0001f) continue;
                    heights[z, x] = Mathf.Clamp01((target - origin.y) / size.y);
                    deepestCut = Mathf.Max(deepestCut, current - target);
                    carvedSamples++;
                    changed = true;
                }
            }

            if (changed) data.SetHeights(0, 0, heights);
        }

        Physics.SyncTransforms();
        Debug.Log(string.Format(
            "WorldGenerationPrototype: Carved river channel ({0} heightmap samples, deepest cut {1:F2} m, half-width {2:F2} m + {3:F1} m bank).",
            carvedSamples, deepestCut, channelHalfWidth, BankBlend), this);
    }

    private static int RequestedCountForPlacement(bool river)
    {
        return river ? 42 : 28;
    }

    private void RunProductionBaseSpawner(int baseSeed)
    {
        rejectedAttemptsByVariant.Clear();
        baseSpawnerErrors.Clear();
        baseSpawner.PlacementRejected += RecordRejectedPlacement;
        Application.logMessageReceived += CaptureBaseSpawnerLog;
        try
        {
            // Base positions follow the base seed: the world seed by default
            // (Stage 2A), or the override / RegenerateBases seed. Seed 12345
            // equals BaseSpawner's former fixed seed, so that world is unchanged.
            CurrentBaseSeed = baseSeed;
            baseSpawner.SetGenerationSeed(baseSeed);
            baseSpawner.GenerateBases();
            baseSpawnerWasRun = true;
        }
        catch (Exception exception)
        {
            baseSpawnReport = "BaseSpawner threw during GenerateBases: " + exception;
            Debug.LogException(exception, this);
        }
        finally
        {
            baseSpawner.PlacementRejected -= RecordRejectedPlacement;
            Application.logMessageReceived -= CaptureBaseSpawnerLog;
        }

        if (baseSpawnerWasRun)
        {
            lastBaseSpawnerReport = BuildBaseSpawnerReport();
            Debug.Log(lastBaseSpawnerReport, this);
        }
    }

    private void CaptureBaseSpawnerLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Error && condition.StartsWith("BaseSpawner:", StringComparison.Ordinal))
        {
            baseSpawnerErrors.Add(condition);
        }
    }

    private void RecordRejectedPlacement(string variant, string reason)
    {
        if (!rejectedAttemptsByVariant.TryGetValue(
                variant, out Dictionary<string, int> byReason))
        {
            byReason = new Dictionary<string, int>();
            rejectedAttemptsByVariant.Add(variant, byReason);
        }

        byReason[reason] = byReason.TryGetValue(reason, out int count)
            ? count + 1
            : 1;
    }

    private string BuildBaseSpawnerReport()
    {
        List<Transform> roots = GetSuccessfulGeneratedRoots();
        int totalRequested = 0;
        int totalSpawned = 0;
        StringBuilder report = new StringBuilder(4096);
        report.AppendLine("Actual BaseSpawner run (generated logical roots; fallback log arithmetic ignored):");

        Dictionary<string, int> counts = new Dictionary<string, int>();
        for (int variantIndex = 0; variantIndex < BaseVariantNames.Length; variantIndex++)
        {
            string variant = BaseVariantNames[variantIndex];
            int count = roots.Count(root => RootIsVariant(root, variant));
            int requested = RequestedBaseCounts[variantIndex];
            counts[variant] = count;
            totalRequested += requested;
            totalSpawned += count;
            report.AppendLine(string.Format(
                "  {0}: requested {1}, spawned {2}, failed {3}",
                variant, requested, count, Mathf.Max(0, requested - count)));
        }
        report.AppendLine(string.Format(
            "  TOTAL: requested {0}, spawned {1}, failed {2}",
            totalRequested, totalSpawned, Mathf.Max(0, totalRequested - totalSpawned)));

        int rejectedAttempts = 0;
        report.AppendLine("Rejected candidate attempts by variant/reason (BaseSpawner observer):");
        foreach (string variant in BaseVariantNames)
        {
            if (!rejectedAttemptsByVariant.TryGetValue(
                    variant, out Dictionary<string, int> byReason) || byReason.Count == 0)
            {
                report.AppendLine("  " + variant + ": none");
                continue;
            }

            List<string> reasons = new List<string>();
            foreach (KeyValuePair<string, int> pair in
                     byReason.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                reasons.Add(pair.Key + " x" + pair.Value);
                rejectedAttempts += pair.Value;
            }
            report.AppendLine("  " + variant + ": " + string.Join(", ", reasons));
        }
        report.AppendLine("  Total rejected candidate attempts: " + rejectedAttempts);

        if (baseSpawnerErrors.Count > 0)
        {
            report.AppendLine("BaseSpawner errors:");
            foreach (string error in baseSpawnerErrors)
            {
                report.AppendLine("  " + error);
            }
        }

        AppendRouteValidationReport(report, roots);
        AppendTerrainAndBoundsReport(report, roots);
        AppendSpacingAndOverlapReport(report, roots);
        return report.ToString();
    }

    private void AppendTerrainAndBoundsReport(StringBuilder report, List<Transform> roots)
    {
        WorldTerrainQueryProvider provider = terrainQueryProvider;
        float mapSizeX = GetPrivateField<float>(baseSpawner, "mapSizeX");
        float mapSizeZ = GetPrivateField<float>(baseSpawner, "mapSizeZ");
        float edgePadding = GetPrivateField<float>(baseSpawner, "edgePadding");
        int terrainHits = 0;
        int terrainMisses = 0;
        int outsideBounds = 0;
        int missingMainBases = 0;
        int railHangarTerrainMisses = 0;

        for (int i = 0; i < roots.Count; i++)
        {
            string variant = VariantForRoot(roots[i].name);
            Transform mainBase = IsNavalVariant(variant) || IsRailVariant(variant)
                ? FindChild(roots[i], variant + " Main Base")
                : roots[i];
            if (mainBase == null)
            {
                missingMainBases++;
                continue;
            }

            float boundsCenterX = baseSpawner.transform.position.x;
            float boundsCenterZ = baseSpawner.transform.position.z;
            if (IsNavalVariant(variant) && riverGenerator != null)
            {
                Vector2 riverCenter = riverGenerator.MapCenter;
                boundsCenterX = riverCenter.x;
                boundsCenterZ = riverCenter.y;
            }

            float localX = mainBase.position.x - boundsCenterX;
            float localZ = mainBase.position.z - boundsCenterZ;
            if (Mathf.Abs(localX) > mapSizeX * 0.5f - edgePadding ||
                Mathf.Abs(localZ) > mapSizeZ * 0.5f - edgePadding)
            {
                outsideBounds++;
            }

            if (provider != null && provider.TryRaycastDown(
                    mainBase.position, 200f, out _))
            {
                terrainHits++;
            }
            else
            {
                terrainMisses++;
            }

            if (IsRailVariant(variant))
            {
                Transform hangar = FindChild(roots[i], "Rail Hangar");
                if (hangar == null || provider == null ||
                    !provider.TryRaycastDown(hangar.position, 200f, out _))
                {
                    railHangarTerrainMisses++;
                }
            }
        }

        report.AppendLine("Generated main-base pivots: terrain hits=" + terrainHits +
            ", misses=" + terrainMisses + ", outside configured world bounds=" +
            outsideBounds + ", missing main-base transforms=" + missingMainBases + ".");
        report.AppendLine("Rail hangar terrain-ray misses=" + railHangarTerrainMisses +
            " (Naval hangars are intentionally positioned over water).");
    }

    private static Transform FindChild(Transform parent, string childName)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName) return child;
        }
        return null;
    }

    private List<Transform> GetSuccessfulGeneratedRoots()
    {
        List<Transform> roots = new List<Transform>();
        if (baseSpawner == null) return roots;

        for (int i = 0; i < baseSpawner.transform.childCount; i++)
        {
            Transform child = baseSpawner.transform.GetChild(i);
            if (!child.name.StartsWith("GeneratedBase_", StringComparison.Ordinal))
            {
                continue;
            }

            string variant = VariantForRoot(child.name);
            if (string.IsNullOrEmpty(variant)) continue;
            BaseController[] controllers = child.GetComponentsInChildren<BaseController>(true);
            if (controllers.Length != 1 || controllers[0].BaseName != variant + " Base")
            {
                // A failed pair can be waiting for deferred Destroy in Play
                // Mode. Count only roots whose identity was initialized after
                // a successful instantiation.
                continue;
            }
            roots.Add(child);
        }
        return roots;
    }

    private static bool RootIsVariant(Transform root, string variant)
    {
        return root != null && root.name.StartsWith(
            "GeneratedBase_" + variant + "_", StringComparison.Ordinal);
    }

    private static string VariantForRoot(string rootName)
    {
        for (int i = 0; i < BaseVariantNames.Length; i++)
        {
            if (rootName.StartsWith(
                    "GeneratedBase_" + BaseVariantNames[i] + "_",
                    StringComparison.Ordinal))
            {
                return BaseVariantNames[i];
            }
        }
        return null;
    }

    private void AppendRouteValidationReport(StringBuilder report, List<Transform> roots)
    {
        IList validations = GetPrivateField(baseSpawner, "lastPlacements") as IList;
        if (validations == null || validations.Count != roots.Count)
        {
            report.AppendLine("Route validation flags: unavailable/misaligned with successful roots.");
            return;
        }

        int navalRoots = 0;
        int navalHangars = 0;
        int navalWaterPass = 0;
        int navalBankPass = 0;
        int railRoots = 0;
        int railHangars = 0;
        int railTrackPass = 0;
        int railBankPass = 0;
        int railGaugePass = 0;
        int terrainValidRouteBases = 0;
        float maximumRailAlignmentError = 0f;
        float minimumNavalWaterOverlap = 1f;

        for (int i = 0; i < roots.Count; i++)
        {
            string variant = VariantForRoot(roots[i].name);
            object validation = validations[i];
            if (IsNavalVariant(variant))
            {
                navalRoots++;
                bool hasHangar = roots[i].Find("Naval Hangar") != null;
                if (hasHangar) navalHangars++;
                if (ReadValidation<bool>(validation, "valid")) terrainValidRouteBases++;
                if (ReadValidation<bool>(validation, "hangarOverWater")) navalWaterPass++;
                if (ReadValidation<bool>(validation, "baseOnLand")) navalBankPass++;
                minimumNavalWaterOverlap = Mathf.Min(
                    minimumNavalWaterOverlap,
                    ReadValidation<float>(validation, "hangarWaterOverlapFraction"));
            }
            else if (IsRailVariant(variant))
            {
                railRoots++;
                bool hasHangar = roots[i].Find("Rail Hangar") != null;
                if (hasHangar) railHangars++;
                if (ReadValidation<bool>(validation, "valid")) terrainValidRouteBases++;
                if (ReadValidation<bool>(validation, "hangarOnTrack")) railTrackPass++;
                if (ReadValidation<bool>(validation, "baseBesideTrack")) railBankPass++;
                if (ReadValidation<bool>(validation, "hangarCoversRails")) railGaugePass++;
                maximumRailAlignmentError = Mathf.Max(
                    maximumRailAlignmentError,
                    ReadValidation<float>(validation, "hangarRouteDistance"));
            }
        }

        report.AppendLine("Naval pair/placement checks: roots=" + navalRoots +
            ", hangars=" + navalHangars + ", water-overlap-centre pass=" + navalWaterPass +
            ", base-on-bank pass=" + navalBankPass +
            ", minimum visible-bounds water overlap=" +
            (navalRoots > 0 ? minimumNavalWaterOverlap.ToString("P1") : "n/a"));
        report.AppendLine("Rail pair/placement checks: roots=" + railRoots +
            ", hangars=" + railHangars + ", on-track pass=" + railTrackPass +
            ", base-beside-track pass=" + railBankPass + ", gauge-covered pass=" + railGaugePass +
            ", maximum hangar centreline error=" + maximumRailAlignmentError.ToString("F3") +
            " m (limit " + config.RailHangarTolerance.ToString("F3") + " m).");
        report.AppendLine("Route-family base terrain validations marked valid: " +
            terrainValidRouteBases + "/" + (navalRoots + railRoots));
    }

    private void AppendSpacingAndOverlapReport(StringBuilder report, List<Transform> roots)
    {
        IList positions = GetPrivateField(baseSpawner, "placedPositions") as IList;
        IList validations = GetPrivateField(baseSpawner, "lastPlacements") as IList;
        if (positions == null || validations == null ||
            positions.Count != roots.Count || validations.Count != roots.Count)
        {
            report.AppendLine("Spacing/overlap audit: unavailable/misaligned with successful roots.");
            return;
        }

        float landSpacing = GetPrivateField<float>(baseSpawner, "minSpacing");
        float routeSpacing = GetPrivateField<float>(baseSpawner, "routeBaseMinSpacing");
        float tolerance = 0.05f;
        int pivotViolations = 0;
        int checkedPivotPairs = 0;
        float minimumCheckedPivotDistance = float.PositiveInfinity;
        int navalFootprintOverlaps = 0;
        int navalHangarSpacingViolations = 0;
        int crossFamilyHangarSpacingViolations = 0;
        float minimumSameBankNavalHangarGap = float.PositiveInfinity;
        MethodInfo overlapMethod = typeof(BaseSpawner).GetMethod(
            "FootprintsOverlapXZ",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo edgeGapMethod = typeof(BaseSpawner).GetMethod(
            "FootprintEdgeGapXZ",
            BindingFlags.Static | BindingFlags.NonPublic);

        for (int later = 1; later < roots.Count; later++)
        {
            string laterVariant = VariantForRoot(roots[later].name);
            object laterValidation = validations[later];
            Vector3 laterPosition = (Vector3)positions[later];
            bool laterLand = !IsNavalVariant(laterVariant) && !IsRailVariant(laterVariant);
            float requiredPivotSpacing = laterLand ? landSpacing : routeSpacing;

            for (int earlier = 0; earlier < later; earlier++)
            {
                string earlierVariant = VariantForRoot(roots[earlier].name);
                object earlierValidation = validations[earlier];
                Vector3 earlierPosition = (Vector3)positions[earlier];
                bool bothNaval = IsNavalVariant(laterVariant) &&
                                 IsNavalVariant(earlierVariant);
                bool oppositeNavalBanks = bothNaval &&
                    OnOppositeRiverBanks(laterValidation, earlierValidation);

                if (bothNaval)
                {
                    bool overlap = InvokeFootprintOverlap(
                        overlapMethod, laterValidation, earlierValidation);
                    if (overlap)
                    {
                        navalFootprintOverlaps++;
                        continue;
                    }
                    if (oppositeNavalBanks) continue;
                }

                float distance = DistanceXZ(laterPosition, earlierPosition);
                minimumCheckedPivotDistance = Mathf.Min(minimumCheckedPivotDistance, distance);
                checkedPivotPairs++;
                if (distance + tolerance < requiredPivotSpacing)
                {
                    pivotViolations++;
                }

                if (!IsNavalVariant(laterVariant)) continue;
                if (IsNavalVariant(earlierVariant))
                {
                    float edgeGap = InvokeFootprintEdgeGap(
                        edgeGapMethod, laterValidation, earlierValidation);
                    bool sameBank = !oppositeNavalBanks;
                    if (sameBank)
                    {
                        minimumSameBankNavalHangarGap = Mathf.Min(
                            minimumSameBankNavalHangarGap, edgeGap);
                    }
                    if (edgeGap <= 0.001f ||
                        (sameBank && edgeGap + 0.001f < config.NavalHangarEdgeClearance))
                    {
                        navalHangarSpacingViolations++;
                    }
                }
            }

            if (IsRailVariant(laterVariant) || IsNavalVariant(laterVariant))
            {
                Vector3 laterHangar = ReadValidation<Vector3>(
                    laterValidation, "hangarWorldPosition");
                for (int earlier = 0; earlier < later; earlier++)
                {
                    string earlierVariant = VariantForRoot(roots[earlier].name);
                    if (!IsNavalVariant(earlierVariant) && !IsRailVariant(earlierVariant)) continue;
                    if (IsNavalVariant(laterVariant) && IsNavalVariant(earlierVariant)) continue;
                    float hangarDistance = DistanceXZ(
                        laterHangar,
                        ReadValidation<Vector3>(validations[earlier], "hangarWorldPosition"));
                    float requiredHangarSpacing = IsNavalVariant(laterVariant)
                        ? NavalToRailHangarSpacing(laterValidation, routeSpacing)
                        : routeSpacing;
                    if (hangarDistance + tolerance < requiredHangarSpacing)
                    {
                        crossFamilyHangarSpacingViolations++;
                    }
                }
            }
        }

        int rendererAabbOverlaps = CountRendererAabbOverlaps(roots);
        report.AppendLine("Pivot spacing audit: checked pairs=" + checkedPivotPairs +
            ", violations=" + pivotViolations + ", minimum checked separation=" +
            (checkedPivotPairs > 0 ? minimumCheckedPivotDistance.ToString("F2") + " m" : "n/a") +
            ".");
        report.AppendLine("Naval main-base footprint overlaps=" + navalFootprintOverlaps +
            ", Naval hangar spacing violations=" + navalHangarSpacingViolations +
            ", cross-family route-hangar spacing violations=" + crossFamilyHangarSpacingViolations +
            ", minimum measured Naval hangar edge gap=" +
            (float.IsPositiveInfinity(minimumSameBankNavalHangarGap)
                ? "n/a"
                : minimumSameBankNavalHangarGap.ToString("F2") + " m") + ".");
        report.AppendLine("Conservative combined-root renderer XZ-AABB overlap pairs=" +
            rendererAabbOverlaps + " (AABB overlap is not a mesh collision test).");
    }

    private int CountRendererAabbOverlaps(List<Transform> roots)
    {
        List<Bounds> bounds = new List<Bounds>(roots.Count);
        List<bool> measured = new List<bool>(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            Renderer[] renderers = roots[i].GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                bounds.Add(default(Bounds));
                measured.Add(false);
                continue;
            }
            Bounds combined = renderers[0].bounds;
            for (int j = 1; j < renderers.Length; j++) combined.Encapsulate(renderers[j].bounds);
            bounds.Add(combined);
            measured.Add(true);
        }

        int overlaps = 0;
        for (int i = 0; i < bounds.Count; i++)
        {
            if (!measured[i]) continue;
            for (int j = i + 1; j < bounds.Count; j++)
            {
                if (!measured[j]) continue;
                bool overlapXZ = bounds[i].min.x <= bounds[j].max.x &&
                                 bounds[i].max.x >= bounds[j].min.x &&
                                 bounds[i].min.z <= bounds[j].max.z &&
                                 bounds[i].max.z >= bounds[j].min.z;
                if (overlapXZ) overlaps++;
            }
        }
        return overlaps;
    }

    private static bool OnOppositeRiverBanks(object a, object b)
    {
        int bankA = RiverBankSign(a);
        int bankB = RiverBankSign(b);
        return bankA != 0 && bankB != 0 && bankA != bankB;
    }

    private static int RiverBankSign(object validation)
    {
        Vector3 center = ReadValidation<Vector3>(validation, "navalBaseFootprintCenter");
        Vector3 routePoint = ReadValidation<Vector3>(validation, "routePoint");
        Vector3 tangent = ReadValidation<Vector3>(validation, "routeTangent");
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.0001f) return 0;
        tangent.Normalize();
        Vector3 normal = Vector3.Cross(Vector3.up, tangent).normalized;
        float signedOffset = Vector3.Dot(center - routePoint, normal);
        return Mathf.Abs(signedOffset) <= 0.001f ? 0 : signedOffset > 0f ? 1 : -1;
    }

    private float NavalToRailHangarSpacing(object navalValidation, float routeSpacing)
    {
        Vector3 size = ReadValidation<Vector3>(
            navalValidation, "navalHangarFootprintSize");
        Quaternion rotation = ReadValidation<Quaternion>(
            navalValidation, "hangarRootRotation");
        Vector3 tangent = ReadValidation<Vector3>(
            navalValidation, "routeTangent");
        tangent.y = 0f;
        tangent = tangent.sqrMagnitude > 0.0001f
            ? tangent.normalized
            : Vector3.forward;
        Vector3 right = rotation * Vector3.right;
        Vector3 forward = rotation * Vector3.forward;
        float halfAlongRoute = Mathf.Abs(Vector3.Dot(right, tangent)) * size.x * 0.5f +
                               Mathf.Abs(Vector3.Dot(forward, tangent)) * size.z * 0.5f;
        float bankClearance = GetPrivateField<float>(baseSpawner, "navalBankPadClearance");
        return Mathf.Max(routeSpacing, halfAlongRoute * 2f + bankClearance);
    }

    private static bool InvokeFootprintOverlap(MethodInfo method, object a, object b)
    {
        if (method == null) return false;
        return (bool)method.Invoke(null, new object[]
        {
            ReadValidation<Vector3>(a, "navalBaseFootprintCenter"),
            ReadValidation<Vector3>(a, "navalBaseFootprintSize"),
            ReadValidation<Quaternion>(a, "navalBaseFootprintRotation"),
            ReadValidation<Vector3>(b, "navalBaseFootprintCenter"),
            ReadValidation<Vector3>(b, "navalBaseFootprintSize"),
            ReadValidation<Quaternion>(b, "navalBaseFootprintRotation")
        });
    }

    private static float InvokeFootprintEdgeGap(MethodInfo method, object a, object b)
    {
        if (method == null) return 0f;
        return (float)method.Invoke(null, new object[]
        {
            ReadValidation<Vector3>(a, "hangarWorldPosition"),
            ReadValidation<Vector3>(a, "navalHangarFootprintSize"),
            ReadValidation<Quaternion>(a, "hangarRootRotation"),
            ReadValidation<Vector3>(b, "hangarWorldPosition"),
            ReadValidation<Vector3>(b, "navalHangarFootprintSize"),
            ReadValidation<Quaternion>(b, "hangarRootRotation")
        });
    }

    private static bool IsNavalVariant(string variant)
    {
        return !string.IsNullOrEmpty(variant) && variant.Contains("Naval");
    }

    private static bool IsRailVariant(string variant)
    {
        return !string.IsNullOrEmpty(variant) && variant.Contains("Rail");
    }

    private static T ReadValidation<T>(object validation, string fieldName)
    {
        if (validation == null) return default(T);
        FieldInfo field = validation.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) return default(T);
        object value = field.GetValue(validation);
        return value is T typed ? typed : default(T);
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        if (target == null) return null;
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field != null ? field.GetValue(target) : null;
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        object value = GetPrivateField(target, fieldName);
        return value is T typed ? typed : default(T);
    }

    private static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private bool ValidateConfiguration()
    {
        if (config.MapSize.x <= 0f || config.MapSize.y <= 0f ||
            config.ChunksX <= 0 || config.ChunksZ <= 0 ||
            config.TerrainHeight <= config.BaseElevation ||
            config.MaximumFootprintVariation < 0f ||
            config.LandSpacing <= 0f || config.RouteSpacing <= 0f)
        {
            lastError = "Map dimensions, chunk counts, height range, or placement settings are invalid.";
            return false;
        }

        int intervals = config.HeightmapResolution - 1;
        if (intervals < 32 || (intervals & (intervals - 1)) != 0)
        {
            lastError = "Heightmap resolution must be 2^n + 1 and at least 33.";
            return false;
        }

        float chunkSizeX = config.MapSize.x / config.ChunksX;
        float chunkSizeZ = config.MapSize.y / config.ChunksZ;
        if (Mathf.Abs(chunkSizeX - chunkSizeZ) > 0.01f)
        {
            lastError = "Unity Terrain chunks must be square in this prototype; adjust map size or chunk counts.";
            return false;
        }

        if (config.SnowAlbedo == null)
        {
            lastError = "Assign the existing snowy albedo texture in the prototype configuration.";
            return false;
        }

        lastError = string.Empty;
        return true;
    }

    private void BuildGeographicLayout()
    {
        riverGuides.Clear();
        railGuides.Clear();
        riverGuideControlPoints.Clear();
        railGuideControlPoints.Clear();
        plainCenters.Clear();
        potentialCrossings.Clear();

        float halfX = config.MapSize.x * 0.5f;
        float halfZ = config.MapSize.y * 0.5f;

        // TerrainV2: seeded, geometry-validated U/S/Z/meander route planning
        // for every river and railway (replaces the near-straight lane guides).
        PlanRoutes();

        // TerrainV2: procedural mountain ranges and peaks were removed entirely.
        // Every other stream (river, rail, plain, detail) is independent, so
        // their seeded output is unchanged by this removal.

        StableRandom plainRandom = new StableRandom(StreamSeed(PlainStream));
        for (int i = 0; i < config.PlainCount; i++)
        {
            float x = plainRandom.Range(-halfX * 0.78f, halfX * 0.78f);
            float z = plainRandom.Range(-halfZ * 0.78f, halfZ * 0.78f);
            plainCenters.Add(new Vector2(x, z));
        }

        PlaceIsolatedHills();

        FindPotentialGuideCrossings();
    }

    // ------------------------------------------------------------------
    // TerrainV2 isolated hills
    // ------------------------------------------------------------------
    // 20-30 smooth, separated hills from their own seeded stream, so river,
    // railway, plain and detail streams (and therefore route and base
    // patterns) are not re-sequenced. Placed after the routes are planned so
    // every hill keeps its whole footprint clear of the river and rail
    // valley floors; added after the plains in EvaluateTerrainHeight so a
    // plain never flattens a hill. Profile: h * (1 - smoothstep(0, R, d)),
    // whose steepest slope is 1.5 h / R.
    private const uint HillStream = 0x2545F491u;
    private const int HillPlacementAttemptsPerHill = 400;

    private struct Hill
    {
        public Vector2 center;
        public float radius;
        public float height;
    }

    private readonly List<Hill> hills = new List<Hill>();
    private int hillTargetCount;

    private void PlaceIsolatedHills()
    {
        hills.Clear();
        StableRandom random = new StableRandom(StreamSeed(HillStream));
        int countMin = config.HillCountMin;
        int countMax = config.HillCountMax;
        hillTargetCount = countMin + Mathf.Min(countMax - countMin,
            (int)(random.Next01() * (countMax - countMin + 1)));
        if (hillTargetCount <= 0) return;

        float halfX = config.MapSize.x * 0.5f;
        float halfZ = config.MapSize.y * 0.5f;
        int attempts = hillTargetCount * HillPlacementAttemptsPerHill;
        for (int attempt = 0; attempt < attempts && hills.Count < hillTargetCount; attempt++)
        {
            float radius = random.Range(config.HillRadiusMin, config.HillRadiusMax);
            // One draw for the height in either mode, so switching between the
            // ratio and an absolute height range never moves a hill.
            float heightDraw = random.Next01();
            float height = config.UseAbsoluteHillHeight
                ? Mathf.Lerp(config.HillHeightMin, config.HillHeightMax, heightDraw)
                : radius * Mathf.Lerp(config.HillHeightToRadiusMin, config.HillHeightToRadiusMax, heightDraw);
            float x = random.Range(-halfX + radius, halfX - radius);
            float z = random.Range(-halfZ + radius, halfZ - radius);
            if (!HillSiteIsClear(x, z, radius)) continue;
            hills.Add(new Hill { center = new Vector2(x, z), radius = radius, height = height });
        }

        if (hills.Count < hillTargetCount)
        {
            // Below the drawn target but within [min, max] is acceptable
            // (information); below the configured minimum is a warning.
            string message = string.Format(
                "WorldGenerationPrototype: placed {0} of {1} isolated hills (configured range {2}-{3}; separation and route clearance leave no further room on a {4} x {5} map).",
                hills.Count, hillTargetCount, countMin, countMax, config.MapSize.x, config.MapSize.y);
            if (hills.Count < countMin) Debug.LogWarning(message, this);
            else Debug.Log(message, this);
        }
    }

    private bool HillSiteIsClear(float x, float z, float radius)
    {
        float riverClear = radius + config.RiverValleyHalfWidth + config.HillRouteClearance;
        for (int i = 0; i < riverGuides.Count; i++)
            if (DistanceToGuide(x, z, riverGuides[i]) < riverClear) return false;
        float railClear = radius + config.RailValleyHalfWidth + config.HillRouteClearance;
        for (int i = 0; i < railGuides.Count; i++)
            if (DistanceToGuide(x, z, railGuides[i]) < railClear) return false;
        Vector2 c = new Vector2(x, z);
        for (int i = 0; i < hills.Count; i++)
        {
            float minimum = config.HillSeparationFactor * (radius + hills[i].radius);
            if ((hills[i].center - c).sqrMagnitude < minimum * minimum) return false;
        }
        return true;
    }

    private float HillElevation(float x, float z)
    {
        float total = 0f;
        for (int i = 0; i < hills.Count; i++)
        {
            Hill hill = hills[i];
            float dx = x - hill.center.x;
            float dz = z - hill.center.y;
            float distanceSqr = dx * dx + dz * dz;
            if (distanceSqr >= hill.radius * hill.radius) continue;
            total += hill.height * (1f - SmoothStep(0f, hill.radius, Mathf.Sqrt(distanceSqr)));
        }
        return total;
    }

    // ------------------------------------------------------------------
    // TerrainV2 route planner (revision 2: guaranteed visible variety)
    // ------------------------------------------------------------------
    // Every route lives in a lane across the map's primary axis (seeded:
    // routes run north-south or east-west). Lane order (rivers/railways)
    // and uneven lane widths come from the seed.
    //
    // World-level variety constraints, drawn from the seed (never a fixed
    // arrangement), are fixed BEFORE geometry is generated:
    //   * shape families (U, S, Z, Meander): at least three different
    //     families per world, no family on more than two routes, the two
    //     railways never share a family, the
    //     three rivers are never all the same, and neighbouring lanes never
    //     share a family;
    //   * bend directions: the three rivers never all bend the same way and
    //     the two railways bend opposite ways.
    // Shapes are pronounced: U = deep trough with two clear bends (or, in
    // an outer lane, a true U through the side edge); S = two directional
    // changes; Z = straight run, one long straight diagonal, straight run;
    // Meander = irregular three-wave course.
    //
    // A candidate is accepted only if, on the exact Catmull-Rom curve the
    // route generators build, it meets the minimum curve radius, never
    // crosses itself, stays inside the map margins, keeps the configured
    // separation from (and never crosses) every route accepted before it,
    // and - unless its attempts are nearly used up - does not run at a
    // near-constant distance beside another route. Rejected candidates are
    // redrawn from the same seeded stream with gradually smaller swing;
    // the last resort is the straight lane centreline. Same seed => same
    // routes.
    private const uint RoutePlanStream = 0x3C6EF372u;
    private const int RouteControlCount = 21;
    private const int RouteValidationSamples = 320;
    // Minimum visible swing and the parallel-route test are config values
    // (MinimumRouteSwing, ParallelRouteDistance/Deviation) so they scale
    // with the map size.
    private readonly List<string> plannedRouteShapes = new List<string>();

    private enum RouteShape { S, Z, U, Meander, EdgeU, StraightFallback }

    private sealed class PlannedRoute
    {
        public bool river;
        public Vector3[] controls;
        public List<Vector2> samples;
        public RouteShape shape;
        public int bend;
        public int lane;
    }

    private void PlanRoutes()
    {
        plannedRouteShapes.Clear();
        float half = Mathf.Min(config.MapSize.x, config.MapSize.y) * 0.5f;
        StableRandom random = new StableRandom(StreamSeed(RoutePlanStream));
        bool alongZ = random.Next01() < 0.5f;
        int riverCount = config.RiverGuideCount;
        int railCount = config.RailGuideCount;
        int laneCount = riverCount + railCount;

        // Seeded lane order of rivers and railways.
        bool[] laneIsRiver = new bool[laneCount];
        for (int i = 0; i < laneCount; i++) laneIsRiver[i] = i < riverCount;
        for (int i = laneCount - 1; i > 0; i--)
        {
            int j = Mathf.Min(i, (int)(random.Next01() * (i + 1)));
            bool swap = laneIsRiver[i]; laneIsRiver[i] = laneIsRiver[j]; laneIsRiver[j] = swap;
        }

        // Whole-world attempts: if any route cannot be placed with its
        // assigned shape, the complete world (families, bends, lane widths
        // and geometry) is redrawn from the same seeded stream, with the
        // swing tapering slightly per retry. The straight-lane fallback is
        // only used if every world attempt fails.
        const int WorldAttempts = 40;
        List<PlannedRoute> accepted = null;
        for (int worldAttempt = 0; worldAttempt < WorldAttempts && accepted == null; worldAttempt++)
        {
            float worldScale = Mathf.Lerp(1f, 0.7f, worldAttempt / (float)(WorldAttempts - 1));
            accepted = TryPlanWorld(laneIsRiver, half, alongZ, worldScale, false, ref random);
        }
        if (accepted == null)
        {
            Debug.LogWarning("WorldGenerationPrototype: no world attempt placed every route " +
                "with its assigned shape; using straight fallbacks where needed.", this);
            accepted = TryPlanWorld(laneIsRiver, half, alongZ, 0.6f, true, ref random);
        }

        // Publish rivers first, then railways (generator order).
        List<PlannedRoute> ordered = accepted.Where(r => r.river)
            .Concat(accepted.Where(r => !r.river)).ToList();
        int riverNumber = 0, railNumber = 0;
        foreach (PlannedRoute route in ordered)
        {
            string label = route.river
                ? "River " + (++riverNumber)
                : "Rail " + (++railNumber);
            plannedRouteShapes.Add(label + ": " + route.shape +
                " bending " + (route.bend > 0 ? "+" : "-") +
                " (lane " + (route.lane + 1) + "/" + laneCount + ", " +
                (alongZ ? "north-south" : "east-west") + ")");
            if (route.river)
            {
                riverGuides.Add(CreateGuide("River Corridor Guide " + riverNumber, route.controls));
                riverGuideControlPoints.Add(route.controls);
            }
            else
            {
                railGuides.Add(CreateGuide("Rail Corridor Guide " + railNumber, route.controls));
                railGuideControlPoints.Add(route.controls);
            }
        }
    }

    /// <summary>
    /// One world attempt: draws families, bends and lane widths, then plans
    /// every lane in order. Returns null as soon as a route cannot be placed
    /// (unless allowFallback, which substitutes the straight lane line).
    /// </summary>
    private List<PlannedRoute> TryPlanWorld(
        bool[] laneIsRiver, float half, bool alongZ, float worldScale, bool allowFallback,
        ref StableRandom random)
    {
        int laneCount = laneIsRiver.Length;
        AssignRouteVariety(laneIsRiver, ref random, out RouteShape[] family, out int[] bend);

        // Seeded, uneven lane widths: curvier families get a little more room.
        float[] weight = new float[laneCount];
        float weightSum = 0f;
        for (int i = 0; i < laneCount; i++)
        {
            float demand = family[i] == RouteShape.Meander ? 0.95f : 1.1f;
            weight[i] = demand * random.Range(0.85f, 1.2f);
            weightSum += weight[i];
        }
        float[] laneCentre = new float[laneCount];
        float[] laneWidth = new float[laneCount];
        float cursor = -half;
        for (int i = 0; i < laneCount; i++)
        {
            laneWidth[i] = 2f * half * weight[i] / weightSum;
            laneCentre[i] = cursor + laneWidth[i] * 0.5f;
            cursor += laneWidth[i];
        }

        // Plan in lane order regardless of route type, so no railway is
        // forced to shadow an already-fixed river.
        List<PlannedRoute> accepted = new List<PlannedRoute>();
        for (int lane = 0; lane < laneCount; lane++)
        {
            PlannedRoute route = PlanOneRoute(laneIsRiver[lane], lane, laneCount, family[lane],
                bend[lane], laneCentre[lane], laneWidth[lane], half, alongZ, worldScale,
                allowFallback, accepted, ref random);
            if (route == null) return null;
            accepted.Add(route);
        }
        return accepted;
    }

    /// <summary>
    /// Draws a seeded shape family and bend direction for every lane that
    /// satisfies the world-level variety constraints (redrawn until valid).
    /// </summary>
    private static void AssignRouteVariety(
        bool[] laneIsRiver, ref StableRandom random, out RouteShape[] family, out int[] bend)
    {
        RouteShape[] families = { RouteShape.U, RouteShape.S, RouteShape.Z, RouteShape.Meander };
        int lanes = laneIsRiver.Length;
        family = new RouteShape[lanes];
        bend = new int[lanes];
        for (int draw = 0; draw < 500; draw++)
        {
            for (int i = 0; i < lanes; i++)
                family[i] = families[Mathf.Min(3, (int)(random.Next01() * 4f))];
            if (FamiliesAreVaried(family, laneIsRiver)) break;
        }
        for (int draw = 0; draw < 500; draw++)
        {
            for (int i = 0; i < lanes; i++) bend[i] = random.Next01() < 0.5f ? -1 : 1;
            if (BendsAreVaried(bend, laneIsRiver)) break;
        }
    }

    private static bool FamiliesAreVaried(RouteShape[] family, bool[] laneIsRiver)
    {
        if (new HashSet<RouteShape>(family).Count < Mathf.Min(3, family.Length)) return false;
        // No family may dominate a world: at most two routes share a family.
        foreach (RouteShape f in family)
            if (family.Count(x => x == f) > 2) return false;
        for (int i = 1; i < family.Length; i++)
            if (family[i] == family[i - 1]) return false;
        List<RouteShape> rails = new List<RouteShape>(), rivers = new List<RouteShape>();
        for (int i = 0; i < family.Length; i++) (laneIsRiver[i] ? rivers : rails).Add(family[i]);
        if (rails.Count > 1 && rails.Distinct().Count() < rails.Count) return false;
        if (rivers.Count > 1 && rivers.Distinct().Count() == 1) return false;
        return true;
    }

    private static bool BendsAreVaried(int[] bend, bool[] laneIsRiver)
    {
        List<int> rails = new List<int>(), rivers = new List<int>();
        for (int i = 0; i < bend.Length; i++) (laneIsRiver[i] ? rivers : rails).Add(bend[i]);
        if (rivers.Count > 1 && rivers.Distinct().Count() == 1) return false;
        if (rails.Count > 1 && rails.Distinct().Count() < 2) return false;
        return true;
    }

    private PlannedRoute PlanOneRoute(
        bool river, int lane, int laneCount, RouteShape family, int bend, float centre,
        float laneWidth, float half, bool alongZ, float worldScale, bool allowFallback,
        List<PlannedRoute> accepted, ref StableRandom random)
    {
        int attempts = Mathf.Max(1, config.RouteCandidateAttempts);
        bool outerLane = lane == 0 || lane == laneCount - 1;
        // A U in an outer lane may open through the side edge it touches; its
        // bend direction is then the inward direction there.
        bool canEdgeU = config.AllowEdgeRoutes &&
                        family == RouteShape.U && outerLane && (lane == 0 ? 1 : -1) == bend;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // Swing shrinks gently as attempts are used up.
            float scale = worldScale * Mathf.Lerp(1f, 0.45f, attempt / (float)attempts);
            RouteShape shape = canEdgeU && random.Next01() < 0.6f ? RouteShape.EdgeU : family;
            Vector3[] controls = BuildRouteControls(shape, bend, lane, centre, laneWidth, half,
                alongZ, scale, ref random);
            if (controls == null) continue;
            PlannedRoute candidate = MakePlannedRoute(river, lane, shape, controls);
            candidate.bend = bend;
            bool enforceVariety = attempt < attempts * 9 / 10;
            if (ValidateRoute(candidate, half, alongZ, accepted, enforceVariety))
                return candidate;
        }

        if (!allowFallback) return null;

        // Last resort: the straight lane centreline (validated lane spacing).
        Vector3[] straight = new Vector3[RouteControlCount];
        for (int i = 0; i < RouteControlCount; i++)
        {
            float a = Mathf.Lerp(-half, half, i / (float)(RouteControlCount - 1));
            straight[i] = FrameToWorld(a, centre, alongZ);
        }
        PlannedRoute fallback = MakePlannedRoute(river, lane, RouteShape.StraightFallback, straight);
        fallback.bend = bend;
        Debug.LogWarning("WorldGenerationPrototype: " + (river ? "river" : "railway") +
            " lane " + (lane + 1) + " used the straight fallback" +
            (ValidateRoute(fallback, half, alongZ, accepted, false)
                ? "." : " and does not meet every route constraint."), this);
        return fallback;
    }

    private static Vector3 FrameToWorld(float along, float lateral, bool alongZ)
    {
        return alongZ ? new Vector3(lateral, 0f, along) : new Vector3(along, 0f, lateral);
    }

    private Vector3[] BuildRouteControls(
        RouteShape shape, int bend, int lane, float centre, float laneWidth, float half,
        bool alongZ, float scale, ref StableRandom random)
    {
        if (shape == RouteShape.EdgeU)
        {
            // True U through the outer side edge next to this lane.
            float edge = lane == 0 ? -half : half;
            float inward = lane == 0 ? 1f : -1f;
            float laneDepth = Mathf.Abs(centre - edge);
            float depth = Mathf.Clamp(laneDepth + random.Range(0f, 0.4f) * laneWidth * scale,
                0.6f * laneDepth, laneDepth + 0.45f * laneWidth);
            float span = random.Range(0.8f, 1.3f) * half;
            float start = random.Range(-half + config.RouteEdgeMargin + 40f,
                half - config.RouteEdgeMargin - 40f - span);
            if (start + span > half - config.RouteEdgeMargin - 40f) return null;
            float[] alongShare = { 0f, 0f, 0.06f, 0.2f, 0.35f, 0.5f, 0.65f, 0.8f, 0.94f, 1f, 1f };
            float[] depthShare = { 0f, 0.3f, 0.72f, 0.95f, 1f, 1f, 1f, 0.95f, 0.72f, 0.3f, 0f };
            float bulge = random.Range(-0.1f, 0.1f);
            Vector3[] u = new Vector3[alongShare.Length];
            for (int i = 0; i < u.Length; i++)
            {
                float d = depthShare[i] * (1f + (i > 2 && i < 8 ? bulge * Mathf.Sin(i) : 0f));
                u[i] = FrameToWorld(start + alongShare[i] * span, edge + inward * depth * d, alongZ);
            }
            return u;
        }

        // Pronounced lateral swing (half-amplitude up to ~0.9 lane width).
        float amplitude;
        switch (shape)
        {
            case RouteShape.Meander: amplitude = random.Range(0.55f, 0.8f); break;
            case RouteShape.U: amplitude = random.Range(0.6f, 0.9f); break;
            case RouteShape.Z: amplitude = random.Range(0.65f, 0.9f); break;
            default: amplitude = random.Range(0.5f, 0.85f); break;
        }
        amplitude *= laneWidth * scale;

        // Shape parameters (all drawn every time so the stream stays regular).
        float uStart = random.Range(0.12f, 0.3f), uEnd = random.Range(0.7f, 0.88f);
        float uRamp = random.Range(0.1f, 0.16f);
        float sPhase = random.Range(-0.25f, 0.25f), sCycles = random.Range(1.0f, 1.25f);
        float z1 = random.Range(0.18f, 0.32f), z2 = random.Range(0.42f, 0.58f), z3 = random.Range(0.68f, 0.82f);
        float m1 = random.Range(1.2f, 1.8f), m2 = random.Range(2.3f, 3.1f), m3 = random.Range(3.6f, 4.6f);
        float q1 = random.Range(0f, 6.2832f), q2 = random.Range(0f, 6.2832f), q3 = random.Range(0f, 6.2832f);
        float w1 = random.Range(0.45f, 0.6f), w2 = random.Range(0.25f, 0.4f);

        Vector3[] controls = new Vector3[RouteControlCount];
        for (int i = 0; i < RouteControlCount; i++)
        {
            float t = i / (float)(RouteControlCount - 1);
            float offset;
            switch (shape)
            {
                case RouteShape.U:
                    // Trough: approach, two clear bends, a long bottom.
                    offset = (SmoothStep(uStart - uRamp, uStart + uRamp, t) -
                              SmoothStep(uEnd - uRamp, uEnd + uRamp, t)) * 1.6f - 0.6f;
                    break;
                case RouteShape.S:
                    // Two directional changes (one full wave or slightly more).
                    offset = Mathf.Sin(2f * Mathf.PI * sCycles * t + sPhase);
                    break;
                case RouteShape.Z:
                    // Letter Z: a straight run on one side, one long straight
                    // diagonal across the lane, a straight run on the other
                    // side. Corners rounded by a ~60 m moving average so the
                    // curve-radius limit is met while the legs stay straight.
                    offset = SmoothedZ(t, z1 + 0.05f, z3 - 0.05f, 0.05f);
                    break;
                default:
                    offset = 1.35f * (w1 * Mathf.Sin(2f * Mathf.PI * m1 * t + q1) +
                                      w2 * Mathf.Sin(2f * Mathf.PI * m2 * t + q2) +
                                      (1f - w1 - w2) * Mathf.Sin(2f * Mathf.PI * m3 * t + q3));
                    break;
            }
            float along = Mathf.Lerp(-half, half, t);
            if (i > 0 && i < RouteControlCount - 1)
                along += random.Range(-0.03f, 0.03f) * laneWidth;
            controls[i] = FrameToWorld(along, centre + bend * amplitude * offset, alongZ);
        }
        return controls;
    }

    private static float SmoothedZ(float t, float corner1, float corner2, float halfWindow)
    {
        const int Taps = 9;
        float sum = 0f;
        for (int k = 0; k < Taps; k++)
        {
            float u = Mathf.Clamp01(t + halfWindow * (2f * k / (Taps - 1) - 1f));
            sum += u <= corner1 ? 1f
                 : u >= corner2 ? -1f
                 : Mathf.Lerp(1f, -1f, (u - corner1) / (corner2 - corner1));
        }
        return sum / Taps;
    }

    private static PlannedRoute MakePlannedRoute(bool river, int lane, RouteShape shape, Vector3[] controls)
    {
        List<Vector2> samples = new List<Vector2>(RouteValidationSamples);
        for (int i = 0; i < RouteValidationSamples; i++)
        {
            Vector3 p = EvaluateSpline(controls, i / (float)(RouteValidationSamples - 1));
            samples.Add(new Vector2(p.x, p.z));
        }
        return new PlannedRoute { river = river, lane = lane, shape = shape, controls = controls, samples = samples };
    }

    private bool ValidateRoute(
        PlannedRoute route, float half, bool alongZ, List<PlannedRoute> accepted, bool enforceVariety)
    {
        float margin = config.RouteEdgeMargin;
        bool edgeU = route.shape == RouteShape.EdgeU;
        foreach (Vector2 p in route.samples)
        {
            float lateral = alongZ ? p.x : p.y;
            float along = alongZ ? p.y : p.x;
            if (Mathf.Abs(lateral) > half + 0.01f || Mathf.Abs(along) > half + 0.01f) return false;
            if (!edgeU && Mathf.Abs(lateral) > half - margin) return false;
            if (edgeU && Mathf.Abs(along) > half - margin) return false;
        }

        // 10% margin: the generators resample the same curve with fewer
        // points, which can read a slightly tighter radius.
        float minRadius = route.river ? config.MinimumRiverCurveRadius : config.MinimumRailCurveRadius;
        if (RouteMinimumRadius(route.samples) < minRadius * 1.1f) return false;

        // Visible curvature: every lane route must swing at least
        // config.MinimumRouteSwing metres sideways (edge-U routes are deep by
        // construction). Relaxed only with the other variety rules.
        if (enforceVariety && !edgeU)
        {
            float minLateral = float.PositiveInfinity, maxLateral = float.NegativeInfinity;
            foreach (Vector2 p in route.samples)
            {
                float lateral = alongZ ? p.x : p.y;
                minLateral = Mathf.Min(minLateral, lateral);
                maxLateral = Mathf.Max(maxLateral, lateral);
            }
            if (maxLateral - minLateral < config.MinimumRouteSwing) return false;
        }
        if (PolylineSelfIntersects(route.samples)) return false;

        foreach (PlannedRoute other in accepted)
        {
            float required = route.river && other.river ? config.RiverRiverSeparation
                : !route.river && !other.river ? config.RailRailSeparation
                : config.RailRiverSeparation;
            if (PolylinesCross(route.samples, other.samples)) return false;
            if (PolylineMinimumDistance(route.samples, other.samples, required) < required) return false;

            if (!enforceVariety) continue;

            // Shape families are fixed per world by AssignRouteVariety. Here:
            // no route may run beside a neighbouring route, or beside any
            // route of the other kind, at a near-constant distance.
            bool neighbours = Mathf.Abs(other.lane - route.lane) == 1;
            if ((neighbours || route.river != other.river) &&
                TracksAtConstantDistance(route.samples, other.samples))
                return false;
        }
        return true;
    }

    // Circumradius over points at least 8 m apart (the same measure the
    // harness reports on the generated routes).
    private static float RouteMinimumRadius(List<Vector2> p)
    {
        List<Vector2> q = new List<Vector2> { p[0] };
        for (int i = 1; i < p.Count; i++) if ((p[i] - q[q.Count - 1]).magnitude >= 8f) q.Add(p[i]);
        float best = float.PositiveInfinity;
        for (int i = 1; i < q.Count - 1; i++)
        {
            Vector2 a = q[i - 1], b = q[i], c = q[i + 1];
            float cross = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
            if (cross < 1e-6f) continue;
            best = Mathf.Min(best, (b - a).magnitude * (c - b).magnitude * (a - c).magnitude / (2f * cross));
        }
        return best;
    }

    private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float d1 = Cross2D(d - c, a - c), d2 = Cross2D(d - c, b - c);
        float d3 = Cross2D(b - a, c - a), d4 = Cross2D(b - a, d - a);
        return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
               ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
    }

    private static bool PolylineSelfIntersects(List<Vector2> p)
    {
        for (int i = 0; i < p.Count - 1; i++)
            for (int j = i + 2; j < p.Count - 1; j++)
                if (SegmentsCross(p[i], p[i + 1], p[j], p[j + 1])) return true;
        return false;
    }

    private static bool PolylinesCross(List<Vector2> a, List<Vector2> b)
    {
        for (int i = 0; i < a.Count - 1; i++)
            for (int j = 0; j < b.Count - 1; j++)
                if (SegmentsCross(a[i], a[i + 1], b[j], b[j + 1])) return true;
        return false;
    }

    private static float PointToPolyline(Vector2 q, List<Vector2> line)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i < line.Count - 1; i++)
            best = Mathf.Min(best, (q - ClosestPoint(q, line[i], line[i + 1])).magnitude);
        return best;
    }

    private static float PolylineMinimumDistance(List<Vector2> a, List<Vector2> b, float stopBelow)
    {
        float best = float.PositiveInfinity;
        foreach (Vector2 q in a) { best = Mathf.Min(best, PointToPolyline(q, b)); if (best < stopBelow) return best; }
        foreach (Vector2 q in b) { best = Mathf.Min(best, PointToPolyline(q, a)); if (best < stopBelow) return best; }
        return best;
    }

    /// <summary>
    /// True when a railway runs beside a river at a nearly constant distance
    /// for most of its length (simply copying the river's shape).
    /// </summary>
    private bool TracksAtConstantDistance(List<Vector2> rail, List<Vector2> river)
    {
        List<float> distances = new List<float>();
        for (int i = 0; i < rail.Count; i += 8) distances.Add(PointToPolyline(rail[i], river));
        float mean = 0f; foreach (float d in distances) mean += d; mean /= distances.Count;
        float variance = 0f; foreach (float d in distances) variance += (d - mean) * (d - mean);
        float deviation = Mathf.Sqrt(variance / distances.Count);
        return mean < config.ParallelRouteDistance && deviation < config.ParallelRouteDeviation;
    }

    private uint StreamSeed(uint stream)
    {
        unchecked
        {
            uint value = (uint)config.WorldSeed ^ stream ^ 2166136261u;
            value = (value ^ (value >> 16)) * 0x7FEB352Du;
            value = (value ^ (value >> 15)) * 0x846CA68Bu;
            value ^= value >> 16;
            return value == 0u ? 0x9E3779B9u : value;
        }
    }

    private Guide CreateGuide(string name, Vector3[] controls)
    {
        // TerrainV2: denser samples (smooth-curve chord error < 0.1 m at the
        // minimum river radius) plus a segment grid for exact distances.
        const int sampleCount = 257;
        Guide guide = new Guide
        {
            name = name,
            controls = controls,
            samples = new Vector3[sampleCount],
            cumulativeDistances = new float[sampleCount],
            progressesAlongZ = Mathf.Abs(
                controls[controls.Length - 1].z - controls[0].z) >= Mathf.Abs(
                controls[controls.Length - 1].x - controls[0].x)
        };

        float travelled = 0f;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1);
            guide.samples[i] = EvaluateSpline(controls, t);
            if (i > 0)
            {
                travelled += Vector3.Distance(guide.samples[i - 1], guide.samples[i]);
            }
            guide.cumulativeDistances[i] = travelled;
        }

        guide.length = travelled;
        BuildGuideSegmentGrid(guide);
        return guide;
    }

    private const float GuideGridCellSize = 40f;

    /// <summary>
    /// Registers every sampled segment in each grid cell within the largest
    /// valley influence distance, so DistanceToGuide can return the exact
    /// distance to the smooth curve while only testing nearby segments.
    /// </summary>
    private void BuildGuideSegmentGrid(Guide guide)
    {
        float reach = Mathf.Max(
            config.RiverValleyHalfWidth + config.RiverValleyShoulder,
            config.RailValleyHalfWidth + config.RailValleyShoulder) + GuideGridCellSize;
        guide.gridReach = reach;
        guide.gridOrigin = new Vector2(-config.MapSize.x * 0.5f, -config.MapSize.y * 0.5f);
        guide.gridWidth = Mathf.CeilToInt(config.MapSize.x / GuideGridCellSize) + 1;
        guide.gridHeight = Mathf.CeilToInt(config.MapSize.y / GuideGridCellSize) + 1;
        guide.cells = new List<int>[guide.gridWidth * guide.gridHeight];
        for (int s = 0; s < guide.samples.Length - 1; s++)
        {
            Vector3 a = guide.samples[s], b = guide.samples[s + 1];
            int minX = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - reach - guide.gridOrigin.x) / GuideGridCellSize));
            int maxX = Mathf.Min(guide.gridWidth - 1, Mathf.FloorToInt((Mathf.Max(a.x, b.x) + reach - guide.gridOrigin.x) / GuideGridCellSize));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, b.z) - reach - guide.gridOrigin.y) / GuideGridCellSize));
            int maxZ = Mathf.Min(guide.gridHeight - 1, Mathf.FloorToInt((Mathf.Max(a.z, b.z) + reach - guide.gridOrigin.y) / GuideGridCellSize));
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                {
                    int cell = z * guide.gridWidth + x;
                    if (guide.cells[cell] == null) guide.cells[cell] = new List<int>();
                    guide.cells[cell].Add(s);
                }
        }
        BuildSortedCellCandidates(guide);
    }

    // Safety margin (m) for the per-cell lower bounds: the cell rectangle is
    // grown by this much (query points may round just past a cell edge) and
    // the bound is reduced by it, so float rounding in the exact segment
    // distance can never make a skipped segment closer than the bound.
    private const double GuideBoundMargin = 0.05;

    /// <summary>
    /// Stage 3B: for every grid cell, the same registered segments sorted by a
    /// conservative lower bound on their distance from any point in the cell
    /// (box-to-box distance between the cell rectangle and the segment's
    /// bounding box). DistanceToGuide evaluates candidates in this order and
    /// stops once no remaining segment can be closer, so it returns exactly
    /// the value of the full scan.
    /// </summary>
    private static void BuildSortedCellCandidates(Guide guide)
    {
        int cellCount = guide.cells.Length;
        guide.cellSegments = new int[cellCount][];
        guide.cellBounds = new float[cellCount][];
        guide.cellBoundsSqr = new float[cellCount][];
        for (int cell = 0; cell < cellCount; cell++)
        {
            List<int> list = guide.cells[cell];
            if (list == null) continue;
            int cellX = cell % guide.gridWidth;
            int cellZ = cell / guide.gridWidth;
            double rectMinX = guide.gridOrigin.x + (double)cellX * GuideGridCellSize - GuideBoundMargin;
            double rectMaxX = guide.gridOrigin.x + (double)(cellX + 1) * GuideGridCellSize + GuideBoundMargin;
            double rectMinZ = guide.gridOrigin.y + (double)cellZ * GuideGridCellSize - GuideBoundMargin;
            double rectMaxZ = guide.gridOrigin.y + (double)(cellZ + 1) * GuideGridCellSize + GuideBoundMargin;
            int[] segments = list.ToArray();
            double[] bounds = new double[segments.Length];
            for (int k = 0; k < segments.Length; k++)
            {
                Vector3 a = guide.samples[segments[k]];
                Vector3 b = guide.samples[segments[k] + 1];
                double segMinX = Math.Min(a.x, b.x), segMaxX = Math.Max(a.x, b.x);
                double segMinZ = Math.Min(a.z, b.z), segMaxZ = Math.Max(a.z, b.z);
                double dx = Math.Max(0.0, Math.Max(segMinX - rectMaxX, rectMinX - segMaxX));
                double dz = Math.Max(0.0, Math.Max(segMinZ - rectMaxZ, rectMinZ - segMaxZ));
                bounds[k] = Math.Max(0.0, Math.Sqrt(dx * dx + dz * dz) - GuideBoundMargin);
            }
            Array.Sort(bounds, segments);
            float[] linear = new float[segments.Length];
            float[] squared = new float[segments.Length];
            for (int k = 0; k < segments.Length; k++)
            {
                linear[k] = (float)bounds[k];
                squared[k] = (float)(bounds[k] * bounds[k]);
            }
            guide.cellSegments[cell] = segments;
            guide.cellBounds[cell] = linear;
            guide.cellBoundsSqr[cell] = squared;
        }
    }

    private void CreateGuideVisuals()
    {
        ClearGuideVisuals();
        CreateGuideLines(riverGuides, new Color(0.12f, 0.62f, 0.92f, 0.9f),
            config.GuideRiverWidth);
        CreateGuideLines(railGuides, new Color(1f, 0.62f, 0.12f, 0.95f), 2f);
    }

    private void CreateGuideLines(List<Guide> guides, Color color, float width)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) return;

        Material material = new Material(shader)
        {
            name = "Runtime World Guide Material",
            hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSaveInEditor
        };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        generatedGuideMaterials.Add(material);

        foreach (Guide guide in guides)
        {
            GameObject guideObject = new GameObject(guide.name + " (Guide Only)");
            if (!Application.isPlaying)
                guideObject.hideFlags = HideFlags.DontSaveInEditor;
            guideObject.transform.SetParent(transform, false);
            LineRenderer line = guideObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = guide.samples.Length;
            line.widthMultiplier = width;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.sharedMaterial = material;
            line.startColor = color;
            line.endColor = color;

            for (int i = 0; i < guide.samples.Length; i++)
            {
                Vector3 point = guide.samples[i];
                point.y = EvaluateTerrainHeight(point.x, point.z) + 1.25f;
                line.SetPosition(i, point);
            }

            guideRenderers.Add(line);
        }
    }

    private void ClearGuideVisuals()
    {
        foreach (LineRenderer line in guideRenderers)
        {
            if (line != null)
            {
                DestroyGeneratedObject(line.gameObject);
            }
        }
        guideRenderers.Clear();

        foreach (Material material in generatedGuideMaterials)
        {
            if (material != null) DestroyGeneratedObject(material);
        }
        generatedGuideMaterials.Clear();
    }

    private IEnumerator GenerateTerrainChunks()
    {
        CleanupGeneratedTerrain();
        DestroyGroundLayers();
        snowWeightSum = 0d;
        snowTexelCount = 0;
        snowCoveredTexelCount = 0;
        bareRockTexelCount = 0;

        float chunkSizeX = config.MapSize.x / config.ChunksX;
        float chunkSizeZ = config.MapSize.y / config.ChunksZ;
        float originX = -config.MapSize.x * 0.5f;
        float originZ = -config.MapSize.y * 0.5f;
        int resolution = config.HeightmapResolution;
        int alphamapResolution = Mathf.Max(16, config.AlphamapResolution);
        float[] groundWeights = new float[GroundLayerCount];

        // Time-budgeted slicing (Stage 3C): complete rows are processed until
        // the slice budget is used up, then the coroutine yields and resumes at
        // the next unfinished row. Rows are never split, skipped or repeated,
        // and every calculation keeps its original order.
        long sliceBudgetTicks = SliceBudgetTicks();
        long sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Stage 3D: claim the reusable chunk buffers for this generation. A
        // generation that overlaps an unfinished one uses private arrays.
        bool ownsBuffers = !terrainBuffersInUse;
        if (ownsBuffers) terrainBuffersInUse = true;

        GameObject chunkRoot = new GameObject("Generated Terrain Chunks");
        if (!Application.isPlaying)
            chunkRoot.hideFlags = HideFlags.DontSaveInEditor;
        chunkRoot.transform.SetParent(transform, false);

        for (int chunkZ = 0; chunkZ < config.ChunksZ; chunkZ++)
        {
            for (int chunkX = 0; chunkX < config.ChunksX; chunkX++)
            {
                TerrainData data = new TerrainData
                {
                    heightmapResolution = resolution,
                    alphamapResolution = alphamapResolution,
                    baseMapResolution = alphamapResolution,
                    size = new Vector3(chunkSizeX, config.TerrainHeight, chunkSizeZ)
                };
                data.name = string.Format("WorldSeed_{0}_Terrain_{1}_{2}",
                    config.WorldSeed, chunkX, chunkZ);
                if (!Application.isPlaying)
                    data.hideFlags = HideFlags.DontSaveInEditor;
                generatedTerrainData.Add(data);
                // Every [z, x] is written below before SetHeights copies it.
                float[,] heights = AcquireHeightBuffer(resolution, ownsBuffers);
                float chunkOriginX = originX + chunkX * chunkSizeX;
                float chunkOriginZ = originZ + chunkZ * chunkSizeZ;
                data.terrainLayers = CreateGroundTerrainLayers(chunkOriginX, chunkOriginZ);
                for (int z = 0; z < resolution; z++)
                {
                    float worldZ = chunkOriginZ +
                                   z / (float)(resolution - 1) * chunkSizeZ;
                    for (int x = 0; x < resolution; x++)
                    {
                        float worldX = chunkOriginX +
                                       x / (float)(resolution - 1) * chunkSizeX;
                        heights[z, x] = Mathf.Clamp01(
                            EvaluateTerrainHeight(worldX, worldZ) /
                            config.TerrainHeight);
                    }

                    // Bound main-thread work: yield after this complete row once
                    // the slice budget is used up.
                    if (System.Diagnostics.Stopwatch.GetTimestamp() - sliceStart >= sliceBudgetTicks)
                    {
                        int chunkIndex = chunkZ * config.ChunksX + chunkX;
                        generationProgress = (chunkIndex + (z + 1f) / resolution) /
                                             (config.ChunksX * config.ChunksZ);
                        yield return null;
                        sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }

                data.SetHeights(0, 0, heights);
                if (data.terrainLayers != null && data.terrainLayers.Length == GroundLayerCount)
                {
                    // Stage 3D (Explorer) splat registration fix: the terrain shader
                    // maps alphamap texel i to i / (resolution - 1) of the chunk
                    // (first and last texels on the chunk edges, like heightmap
                    // vertices). Sampling at (i + 0.5) / resolution drew every
                    // chunk's splat up to half a texel off, so neighbouring chunks
                    // showed different weights at their shared border (hard,
                    // chunk-aligned seams). Edge texels of adjacent chunks now
                    // sample the same world position. Weights stay a pure
                    // function of world position; heights are unchanged.
                    // Every [z, x, layer] is written below before SetAlphamaps copies it.
                    float[,,] alphamap = AcquireSplatBuffer(alphamapResolution, GroundLayerCount, ownsBuffers);
                    for (int z = 0; z < alphamapResolution; z++)
                    {
                        float worldZ = chunkOriginZ + z / (float)(alphamapResolution - 1) * chunkSizeZ;
                        for (int x = 0; x < alphamapResolution; x++)
                        {
                            float worldX = chunkOriginX + x / (float)(alphamapResolution - 1) * chunkSizeX;
                            EvaluateGroundWeights(worldX, worldZ, groundWeights);
                            for (int l = 0; l < GroundLayerCount; l++)
                                alphamap[z, x, l] = groundWeights[l];
                            float snowWeight = groundWeights[SnowLayerIndex];
                            snowWeightSum += snowWeight;
                            snowTexelCount++;
                            if (snowWeight > 0.5f) snowCoveredTexelCount++;
                            if (snowWeight < 0.2f) bareRockTexelCount++;
                        }

                        // Same budget for the splat rows (formerly one
                        // unbroken block per chunk).
                        if (System.Diagnostics.Stopwatch.GetTimestamp() - sliceStart >= sliceBudgetTicks)
                        {
                            yield return null;
                            sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        }
                    }
                    data.SetAlphamaps(0, 0, alphamap);
                }

                GameObject terrainObject = Terrain.CreateTerrainGameObject(data);
                terrainObject.name = string.Format("Terrain Chunk {0}_{1}", chunkX, chunkZ);
                if (!Application.isPlaying)
                    terrainObject.hideFlags = HideFlags.DontSaveInEditor;
                terrainObject.transform.SetParent(chunkRoot.transform, true);
                terrainObject.transform.position = new Vector3(
                    originX + chunkX * chunkSizeX, 0f,
                    originZ + chunkZ * chunkSizeZ);

                Terrain terrain = terrainObject.GetComponent<Terrain>();
                terrain.heightmapPixelError = 5f;
                terrain.basemapDistance = 1200f;
                terrain.detailObjectDistance = 0f;
                terrain.treeDistance = 0f;
                terrain.drawInstanced = true;
                // URP's default terrain material exists only in the Editor; in a
                // player the pipeline returns none and CreateTerrainGameObject
                // falls back to Unity's error material, so the chunk renders flat.
                // Only then use the configured runtime material (URP TerrainLit,
                // the Editor default), so the Editor is unchanged.
                if (config.RuntimeTerrainMaterial != null &&
                    (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null ||
                     UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial == null))
                {
                    terrain.materialTemplate = config.RuntimeTerrainMaterial;
                }
#if !UNITY_EDITOR
                // Player builds only: instanced terrain drawing renders the chunks
                // black and flat in the Web player, while the regular path renders
                // them correctly. Rendering setting only; terrain data is unchanged.
                // The Editor keeps drawInstanced = true (set above).
                terrain.drawInstanced = false;
#endif
                terrainChunks.Add(terrain);

                // Chunk finished: yield only if the slice budget is used up
                // (no unconditional per-tile frame wait).
                generationProgress = (terrainChunks.Count) /
                                     (float)(config.ChunksX * config.ChunksZ);
                if (System.Diagnostics.Stopwatch.GetTimestamp() - sliceStart >= sliceBudgetTicks)
                {
                    yield return null;
                    sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
        }
        generationProgress = 1f;
        if (ownsBuffers) terrainBuffersInUse = false;
    }

    /// <summary>
    /// Stage 3D: the instance height buffer when this generation owns the work
    /// buffers (allocated once, reused while the size matches), otherwise a
    /// private array. Callers overwrite every element before reading it.
    /// </summary>
    private float[,] AcquireHeightBuffer(int resolution, bool ownsBuffers)
    {
        if (!ownsBuffers) return new float[resolution, resolution];
        if (heightBuffer == null || heightBuffer.GetLength(0) != resolution || heightBuffer.GetLength(1) != resolution)
            heightBuffer = new float[resolution, resolution];
        return heightBuffer;
    }

    /// <summary>Stage 3D: same as AcquireHeightBuffer for the splat map.</summary>
    private float[,,] AcquireSplatBuffer(int resolution, int layers, bool ownsBuffers)
    {
        if (!ownsBuffers) return new float[resolution, resolution, layers];
        if (splatBuffer == null || splatBuffer.GetLength(0) != resolution ||
            splatBuffer.GetLength(1) != resolution || splatBuffer.GetLength(2) != layers)
            splatBuffer = new float[resolution, resolution, layers];
        return splatBuffer;
    }

    /// <summary>Stage 3D: drops the reusable work buffers (they are rebuilt on demand).</summary>
    private void ReleaseWorkBuffers()
    {
        heightBuffer = null;
        splatBuffer = null;
        carveDistanceBuffer = null;
        carveWaterBuffer = null;
        terrainBuffersInUse = false;
    }

    /// <summary>Slice budget in Stopwatch ticks for the current platform.</summary>
    private long SliceBudgetTicks()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        float budgetMs = webGenerationSliceBudgetMs;
#else
        float budgetMs = generationSliceBudgetMs;
#endif
        return (long)(Mathf.Max(0.5f, budgetMs) * 0.001 * System.Diagnostics.Stopwatch.Frequency);
    }

    // TerrainV2 ground material. Every layer reuses the existing rocky albedo,
    // normal map and M_SnowGround tint; no new texture assets are introduced.
    //   0-2: the rock texture at three non-integer scale ratios and offsets,
    //        blended by broad seeded noise so the 64 m repeat grid breaks up.
    //   3:   a whitened copy of the same texture (snow dusting), so the
    //        rocky relief stays readable under the snow.
    // Smoothness is near zero on all layers to remove the specular sparkle.
    private const uint GroundLayerStream = 0x5F356495u;
    private const int GroundLayerCount = 4;
    private const int SnowLayerIndex = 3;
    private const float RockSmoothness = 0f;
    private const float SnowSmoothness = 0.04f;
    private static readonly float[] RockTileScale = { 1f, 1.37f, 0.81f };
    private static readonly Vector2[] RockTileOffset =
    {
        Vector2.zero, new Vector2(31f, 17f), new Vector2(13f, 43f)
    };

    /// <summary>
    /// Builds the four ground layers for one chunk. Terrain layer UVs are
    /// chunk-local (uv = (local + tileOffset) / tileSize), so each chunk gets
    /// its own copies whose tileOffset adds the chunk's world origin. Every
    /// chunk then samples uv = (world + globalOffset) / tileSize: one
    /// continuous world-space pattern with no jump at 300 m chunk borders.
    /// The offset is reduced modulo the tile size only for float precision;
    /// that does not change the sampled pattern.
    /// </summary>
    private TerrainLayer[] CreateGroundTerrainLayers(float chunkOriginX, float chunkOriginZ)
    {
        Color tint = Color.white;
        if (config.SnowGroundMaterial != null && config.SnowGroundMaterial.HasProperty("_BaseColor"))
            tint = config.SnowGroundMaterial.GetColor("_BaseColor");

        // URP's terrain shader applies only the remap scale, so snow cannot be
        // made by whitening the rock albedo. A small runtime snow albedo is
        // used instead; it keeps the rock normal map so relief reads through.
        if (generatedSnowAlbedo == null)
            generatedSnowAlbedo = CreateSnowAlbedo(StreamSeed(GroundLayerStream ^ 0x5u));
        // TerrainV2 visual fix: the source rock albedo is olive/brown (mean
        // sRGB 0.43/0.39/0.23, saturation 0.48). A one-time, low-resolution
        // neutral-grey copy is used for the rock layers instead.
        if (generatedRockAlbedo == null)
            generatedRockAlbedo = CreateNeutralRockAlbedo(config.SnowAlbedo);
        Texture2D rockAlbedo = generatedRockAlbedo != null ? generatedRockAlbedo : config.SnowAlbedo;
        if (generatedSnowNormal == null)
            generatedSnowNormal = CreateSnowNormal(StreamSeed(GroundLayerStream ^ 0x7u));

        TerrainLayer[] layers = new TerrainLayer[GroundLayerCount];
        for (int i = 0; i < GroundLayerCount; i++)
        {
            bool snow = i == SnowLayerIndex;
            float tile = config.SnowTextureTileSize * (snow ? 1f : RockTileScale[i]);
            Vector2 globalOffset = snow ? Vector2.zero : RockTileOffset[i];
            Vector2 chunkOffset = new Vector2(
                Mathf.Repeat(chunkOriginX + globalOffset.x, tile),
                Mathf.Repeat(chunkOriginZ + globalOffset.y, tile));
            TerrainLayer layer = new TerrainLayer
            {
                name = snow ? "Runtime Snow Dusting Layer" : "Runtime Rock Ground Layer " + i,
                diffuseTexture = snow ? generatedSnowAlbedo : rockAlbedo,
                normalMapTexture = snow ? generatedSnowNormal : config.SnowNormal,
                tileSize = Vector2.one * tile,
                tileOffset = chunkOffset,
                metallic = 0f,
                smoothness = snow ? SnowSmoothness : RockSmoothness,
                diffuseRemapMin = Vector4.zero,
                diffuseRemapMax = snow
                    ? Vector4.one
                    : new Vector4(tint.r, tint.g, tint.b, tint.a)
            };
            if (!Application.isPlaying) layer.hideFlags = HideFlags.DontSaveInEditor;
            layers[i] = layer;
            generatedGroundLayers.Add(layer);
        }
        return layers;
    }

    private Texture2D generatedSnowAlbedo;
    private Texture2D generatedRockAlbedo;
    private const int RockAlbedoSize = 1024;

    /// <summary>
    /// Builds a neutral grey copy of the rock albedo: GPU downsample (the
    /// source is not CPU-readable), read back once, convert to luminance
    /// with a very faint green-grey bias. Returns null if the GPU path is
    /// unavailable, in which case the caller keeps the original texture.
    /// </summary>
    private static Texture2D CreateNeutralRockAlbedo(Texture2D source)
    {
        if (source == null) return null;
        RenderTexture previous = RenderTexture.active;
        RenderTexture rt = RenderTexture.GetTemporary(
            RockAlbedoSize, RockAlbedoSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D texture = new Texture2D(RockAlbedoSize, RockAlbedoSize, TextureFormat.RGBA32, true, false)
            {
                name = "Runtime Neutral Rock Albedo",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };
            if (!Application.isPlaying) texture.hideFlags = HideFlags.DontSaveInEditor;
            texture.ReadPixels(new Rect(0, 0, RockAlbedoSize, RockAlbedoSize), 0, 0, false);
            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                float luminance = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                // Lift and slightly compress so exposed rock reads as mid grey,
                // not near-black, next to the snow.
                float v = Mathf.Clamp01(0.22f + 0.80f * luminance);
                pixels[i] = new Color32(
                    (byte)Mathf.RoundToInt(v * 0.975f * 255f),
                    (byte)Mathf.RoundToInt(v * 255f),
                    (byte)Mathf.RoundToInt(v * 0.975f * 255f),
                    0);
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
            return texture;
        }
        catch (Exception e)
        {
            Debug.LogWarning("WorldGenerationPrototype: neutral rock albedo unavailable (" + e.Message +
                "); using the original rock texture.");
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    private static Texture2D CreateSnowAlbedo(uint seed)
    {
        const int size = 256;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "Runtime Snow Albedo",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4
        };
        if (!Application.isPlaying) texture.hideFlags = HideFlags.DontSaveInEditor;
        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Periodic lattice (period = size / 16) so the texture tiles.
                float n = 0.6f * PeriodicValueNoise(x / 16f, y / 16f, 16, seed) +
                          0.4f * PeriodicValueNoise(x / 4f, y / 4f, 64, seed ^ 0x77u);
                // TerrainV2 visual fix: brighter, neutral white (the former
                // blue bias read as blue snow). A slight warm bias offsets the scene's
                // pale-blue sun (0.82/0.90/1.00) and blue ambient so snow reads white.
                float v = 0.90f + 0.05f * n;
                // Alpha 0: no albedo-alpha smoothness contribution.
                pixels[y * size + x] = new Color(v, v * 0.965f, v * 0.92f, 0f);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(true, false);
        return texture;
    }

    private static float PeriodicValueNoise(float x, float z, int period, uint seed)
    {
        int x0 = Mathf.FloorToInt(x);
        int z0 = Mathf.FloorToInt(z);
        float tx = x - x0;
        float tz = z - z0;
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        int xa = ((x0 % period) + period) % period, xb = (xa + 1) % period;
        int za = ((z0 % period) + period) % period, zb = (za + 1) % period;
        return Mathf.Lerp(
            Mathf.Lerp(LatticeNoise(xa, za, seed), LatticeNoise(xb, za, seed), tx),
            Mathf.Lerp(LatticeNoise(xa, zb, seed), LatticeNoise(xb, zb, seed), tx), tz);
    }

    // TerrainV2 visual fix: a seamless, feature-free fine-relief normal map
    // for the snow layer. The rock normal map has a baked radial "star" and
    // mountain ridge pattern that repeated every 64 m across the snow.
    // Encoded R=x, G=y, B=z, A=1 in a linear texture, which decodes
    // correctly through both the RGB and the AG (DXT5nm) unpack paths.
    private Texture2D generatedSnowNormal;
    // Option C: softer fine relief (was 2.2) so the snow texture no longer
    // masks the shading of larger landforms; finest octave weakened below.
    private const float SnowNormalStrength = 1.2f;

    private static Texture2D CreateSnowNormal(uint seed)
    {
        const int size = 256;
        float[] h = new float[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                h[y * size + x] =
                    0.58f * PeriodicValueNoise(x / 32f, y / 32f, 8, seed ^ 0x101u) +
                    0.32f * PeriodicValueNoise(x / 12.8f, y / 12.8f, 20, seed ^ 0x102u) +
                    0.10f * PeriodicValueNoise(x / 5.12f, y / 5.12f, 50, seed ^ 0x103u);
            }
        }
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
        {
            name = "Runtime Snow Normal",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4
        };
        if (!Application.isPlaying) texture.hideFlags = HideFlags.DontSaveInEditor;
        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = h[y * size + (x + 1) % size] - h[y * size + (x + size - 1) % size];
                float dy = h[((y + 1) % size) * size + x] - h[((y + size - 1) % size) * size + x];
                Vector3 n = new Vector3(-dx * SnowNormalStrength, -dy * SnowNormalStrength, 1f).normalized;
                pixels[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(true, false);
        return texture;
    }

    private void DestroyGroundLayers()
    {
        if (generatedSnowNormal != null)
        {
            DestroyGeneratedObject(generatedSnowNormal);
            generatedSnowNormal = null;
        }
        foreach (TerrainLayer layer in generatedGroundLayers)
        {
            if (layer != null) DestroyGeneratedObject(layer);
        }
        generatedGroundLayers.Clear();
        if (generatedSnowAlbedo != null)
        {
            DestroyGeneratedObject(generatedSnowAlbedo);
            generatedSnowAlbedo = null;
        }
        if (generatedRockAlbedo != null)
        {
            DestroyGeneratedObject(generatedRockAlbedo);
            generatedRockAlbedo = null;
        }
    }

    /// <summary>
    /// Stateless, world-position based ground-layer weights so neighbouring
    /// chunks agree at their borders. Writes GroundLayerCount weights that
    /// sum to one.
    /// </summary>
    // Position of the baked snowy "star" inside the rock albedo, in texture UV
    // (measured from albedo.png: snow centroid 0.472/0.447, shading centroid
    // 0.455/0.519), and its radius in tile units (snow lies within 0.20).
    private static readonly Vector2 StarUV = new Vector2(0.465f, 0.47f);
    private const float StarInnerRadius = 0.24f;
    private const float StarOuterRadius = 0.32f;
    // Fraction of star instances left visible so the snowy character stays,
    // but as a sparse random scatter instead of a lattice.
    private const float KeptStarFraction = 0.06f;

    /// <summary>
    /// How strongly rock layer <paramref name="layer"/> shows a suppressed
    /// star at this point (0 = no star, 1 = star centre). Layer UVs are
    /// world-continuous: uv = (world + RockTileOffset) / tileSize, so star
    /// cells are global and agree across chunk borders.
    /// </summary>
    private float StarSuppression(int layer, float worldX, float worldZ, uint seed)
    {
        return StarMask(layer, worldX, worldZ, seed, out bool kept) * (kept ? 0f : 1f);
    }

    /// <summary>Strength of a KEPT star of this layer at this point.</summary>
    private float KeptStarStrength(int layer, float worldX, float worldZ, uint seed)
    {
        return StarMask(layer, worldX, worldZ, seed, out bool kept) * (kept ? 1f : 0f);
    }

    private float StarMask(int layer, float worldX, float worldZ, uint seed, out bool kept)
    {
        kept = false;
        float tile = config.SnowTextureTileSize * RockTileScale[layer];
        float u = (worldX + RockTileOffset[layer].x) / tile - StarUV.x + 0.5f;
        float v = (worldZ + RockTileOffset[layer].y) / tile - StarUV.y + 0.5f;
        int cellU = Mathf.FloorToInt(u);
        int cellV = Mathf.FloorToInt(v);
        float distance = new Vector2(u - cellU - 0.5f, v - cellV - 0.5f).magnitude;
        float mask = 1f - SmoothStep(StarInnerRadius, StarOuterRadius, distance);
        if (mask <= 0f) return 0f;

        uint keepSeed = seed ^ (uint)(layer * 0x9E3779B1);
        kept = (LatticeNoise(cellU, cellV, keepSeed) * 0.5f + 0.5f) < KeptStarFraction;
        return mask;
    }

    private void EvaluateGroundWeights(
        float x, float z, float[] weights)
    {
        uint seed = StreamSeed(GroundLayerStream);
        float a = ValueNoise((0.906f * x + 0.423f * z) / 170f,
                             (-0.423f * x + 0.906f * z) / 170f, seed ^ 0x1u);
        float b = ValueNoise((0.574f * x - 0.819f * z) / 240f,
                             (0.819f * x + 0.574f * z) / 240f, seed ^ 0x2u);
        // Narrow transitions: each region shows one grid phase/scale, so the
        // global 64 m lattice breaks up without wide double-image zones.
        float w1 = SmoothStep(-0.025f, 0.025f, a);
        float w2 = SmoothStep(0.035f, 0.085f, b);
        float r0 = (1f - w1) * (1f - w2);
        float r1 = w1 * (1f - 0.5f * w2);
        float r2 = w2 * (1f - 0.5f * w1);

        // Star suppression: where the preferred layer would show a baked star,
        // hand the spot to a layer whose star lies elsewhere. The small floor
        // lets non-preferred layers take over; if every layer has a star here
        // (rare, about 1-2% of the map) the regional choice is kept.
        float s0 = StarSuppression(0, x, z, seed ^ 0x10u);
        float s1 = StarSuppression(1, x, z, seed ^ 0x10u);
        float s2 = StarSuppression(2, x, z, seed ^ 0x10u);
        const float TakeoverFloor = 0.04f;
        // A kept star claims its own footprint so it is never shown cut in
        // half by a region boundary.
        const float KeptStarClaim = 3f;
        float q0 = (r0 + TakeoverFloor) * (1f - s0) +
                   KeptStarClaim * KeptStarStrength(0, x, z, seed ^ 0x10u);
        float q1 = (r1 + TakeoverFloor) * (1f - s1) +
                   KeptStarClaim * KeptStarStrength(1, x, z, seed ^ 0x10u);
        float q2 = (r2 + TakeoverFloor) * (1f - s2) +
                   KeptStarClaim * KeptStarStrength(2, x, z, seed ^ 0x10u);
        if (q0 + q1 + q2 > 0.02f)
        {
            r0 = q0; r1 = q1; r2 = q2;
        }
        float rockSum = Mathf.Max(1e-4f, r0 + r1 + r2);

        // Snow dusting: broad patches with a ragged fine edge, capped below
        // full coverage so the rock texture always shows through.
        // "Slightly more snow": a light frost everywhere plus sparse, soft
        // patches. Noise is sampled in rotated coordinates so the value-noise
        // lattice does not produce axis-aligned blocky shapes.
        // TerrainV2 visual fix: snow is now the dominant cover (about 92-94%
        // of the ground has snow weight > 0.5, calibrated offline against
        // this exact noise). Rock is exposed only in irregular patches where
        // a domain-warped, rotated three-octave noise exceeds a high
        // threshold, plus a faint thin-snow veil (at most 8% rock show-
        // through) that keeps the rocky surface character under the snow.
        // Everything is a pure function of world position, so all 16 chunks
        // agree at their borders and there is no lattice-aligned pattern.
        float snow = EvaluateSnowCover(x, z, seed);
        // TerrainV2 hill visibility (option B): wind-scoured rocky caps on the
        // isolated hills, so 4-8 m hills read from the gameplay camera.
        snow = Mathf.Min(snow, 1f - HillRockExposure(x, z, seed));

        float rockShare = 1f - snow;
        weights[0] = rockShare * r0 / rockSum;
        weights[1] = rockShare * r1 / rockSum;
        weights[2] = rockShare * r2 / rockSum;
        weights[SnowLayerIndex] = snow;
    }

    // Random open-ground rock patches start a little later than before
    // (0.38 -> 0.44) so the hill caps below keep the overall map at about
    // 92-94% snow-dominant (calibrated offline on real 900 m hill layouts).
    private const float RockExposureStart = 0.44f;
    private const float RockExposureEnd = 0.54f;
    // Option C: fainter thin-snow veil (was 0.08) so fine streaks do not
    // hide larger landforms.
    private const float ThinSnowRockShare = 0.05f;

    // Hill caps: exposure rises from the upper flank (normalised height
    // 0.45) to the crest (0.9), shifted toward the wind-scoured side (the
    // side facing away from the sun heading), capped at 0.85 so rock shows
    // through as muted grey under thinned snow rather than bare dark rock,
    // and broken up by warped two-octave noise so edges are irregular.
    private const float HillRockLow = 0.45f;
    private const float HillRockHigh = 0.9f;
    private const float HillRockCap = 0.85f;
    private const float HillRockAsymmetry = 0.35f;
    private static readonly Vector2 HillWindScourDirection =
        new Vector2(Mathf.Cos(-210f * Mathf.Deg2Rad), Mathf.Sin(-210f * Mathf.Deg2Rad));

    private float HillRockExposure(float x, float z, uint seed)
    {
        if (hills.Count == 0) return 0f;
        float exposure = 0f;
        for (int i = 0; i < hills.Count; i++)
        {
            Hill hill = hills[i];
            float dx = x - hill.center.x, dz = z - hill.center.y;
            float distanceSqr = dx * dx + dz * dz;
            if (distanceSqr >= hill.radius * hill.radius) continue;
            float d = Mathf.Sqrt(distanceSqr);
            float t = d / hill.radius;
            float height = 1f - SmoothStep(0f, 1f, t);
            float side = d > 1e-3f
                ? (dx * HillWindScourDirection.x + dz * HillWindScourDirection.y) / d * Mathf.Clamp01(t * 2.5f)
                : 0f;
            exposure = Mathf.Max(exposure,
                SmoothStep(HillRockLow, HillRockHigh, height + HillRockAsymmetry * side));
        }
        if (exposure <= 0f) return 0f;
        float rx = 0.799f * x - 0.602f * z;
        float rz = 0.602f * x + 0.799f * z;
        float ux = rx + 18f * ValueNoise(rx / 70f, rz / 70f, seed ^ 0x21u);
        float uz = rz + 18f * ValueNoise(rz / 70f, -rx / 70f, seed ^ 0x22u);
        float breakup = 0.6f * ValueNoise(ux / 19f, uz / 19f, seed ^ 0x31u) +
                        0.4f * ValueNoise(uz / 7.5f, -ux / 7.5f, seed ^ 0x32u);
        return HillRockCap * exposure * SmoothStep(-0.45f, 0.2f, breakup + 0.15f * (exposure - 0.5f));
    }

    private static float EvaluateSnowCover(float x, float z, uint seed)
    {
        float rx = 0.799f * x - 0.602f * z;
        float rz = 0.602f * x + 0.799f * z;
        float ux = rx + 18f * ValueNoise(rx / 70f, rz / 70f, seed ^ 0x21u);
        float uz = rz + 18f * ValueNoise(rz / 70f, -rx / 70f, seed ^ 0x22u);
        float exposureNoise = 0.55f * ValueNoise(ux / 95f, uz / 95f, seed ^ 0x23u) +
                              0.30f * ValueNoise(uz / 34f, -ux / 34f, seed ^ 0x24u) +
                              0.15f * ValueNoise(ux / 12f, uz / 12f, seed ^ 0x25u);
        float exposed = SmoothStep(RockExposureStart, RockExposureEnd, exposureNoise);
        float veilNoise = 0.6f * ValueNoise(ux / 41f, uz / 41f, seed ^ 0x26u) +
                          0.4f * ValueNoise(uz / 15f, -ux / 15f, seed ^ 0x27u);
        float veil = ThinSnowRockShare * SmoothStep(0f, 0.5f, veilNoise);
        return Mathf.Clamp01(1f - Mathf.Max(exposed, veil));
    }

    // Snow-cover statistics over every alphamap texel of every chunk,
    // reported by the screening report (TerrainV2 visual validation).
    private double snowWeightSum;
    private long snowTexelCount;
    private long snowCoveredTexelCount;
    private long bareRockTexelCount;

    private void ConfigureTerrainNeighbors()
    {
        Terrain[,] grid = new Terrain[config.ChunksZ, config.ChunksX];
        int index = 0;
        for (int z = 0; z < config.ChunksZ; z++)
        {
            for (int x = 0; x < config.ChunksX; x++)
            {
                grid[z, x] = terrainChunks[index++];
            }
        }

        for (int z = 0; z < config.ChunksZ; z++)
        {
            for (int x = 0; x < config.ChunksX; x++)
            {
                Terrain left = x > 0 ? grid[z, x - 1] : null;
                Terrain top = z < config.ChunksZ - 1 ? grid[z + 1, x] : null;
                Terrain right = x < config.ChunksX - 1 ? grid[z, x + 1] : null;
                Terrain bottom = z > 0 ? grid[z - 1, x] : null;
                grid[z, x].SetNeighbors(left, top, right, bottom);
            }
        }
    }

    private void CleanupGeneratedTerrain()
    {
        Transform generatedRoot = transform.Find("Generated Terrain Chunks");
        if (generatedRoot != null)
        {
            DestroyGeneratedObject(generatedRoot.gameObject);
        }
        terrainChunks.Clear();

        foreach (TerrainData data in generatedTerrainData)
        {
            if (data != null) DestroyGeneratedObject(data);
        }
        generatedTerrainData.Clear();
    }

    private static void DestroyGeneratedObject(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value);
        else DestroyImmediate(value);
    }

    private float EvaluateTerrainHeight(float x, float z)
    {
        float broadDetail = ValueNoise(
            x * config.DetailFrequency,
            z * config.DetailFrequency,
            StreamSeed(DetailStream));
        float fineDetail = ValueNoise(
            x * config.FineDetailFrequency,
            z * config.FineDetailFrequency,
            StreamSeed(DetailStream ^ 0x91E10DA5u));

        float height = config.BaseElevation + broadDetail * config.DetailAmplitude +
                       fineDetail * config.FineDetailAmplitude;

        // Plains are blended before the river and rail valleys are carved, so a
        // plain never refills a valley floor (it had raised the river bed by
        // up to 7.6 m, forcing the water to climb over the plateau).
        for (int i = 0; i < plainCenters.Count; i++)
        {
            Vector2 delta = new Vector2(x, z) - plainCenters[i];
            float distance = delta.magnitude;
            float inner = config.PlainRadius * 0.68f;
            float blend = 1f - SmoothStep(inner, config.PlainRadius, distance);
            // TerrainV2: plains grade the ground to its own smoothed level.
            // The former +10 m offset only made sense inside mountain country;
            // on open ground it produced 10 m flat-topped mesas.
            float plainElevation = config.BaseElevation + broadDetail * 0.5f;
            height = Mathf.Lerp(height, plainElevation, blend);
        }

        // TerrainV2: isolated hills, after the plains (so no plain flattens a
        // hill) and before the valleys (which they never reach by design).
        height += HillElevation(x, z);

        // TerrainV2: the river floor sits below the natural ground and the
        // valley may only lower terrain. The former +3 m floor raised the
        // river onto an embankment once mountains were removed.
        float riverFloor = config.BaseElevation - 2.5f + broadDetail * 0.35f;
        float nearestRiverDistance = float.PositiveInfinity;
        // Stage 3B: beyond this distance every river term below is exactly
        // unchanged (shoulder and floor blends are 0, river protection is 1),
        // so DistanceToGuide may stop there and return the limit itself.
        float riverInfluence = Mathf.Max(
            config.RiverValleyHalfWidth + config.RiverValleyShoulder,
            config.RiverValleyHalfWidth + 30f);
        for (int i = 0; i < riverGuides.Count; i++)
        {
            float distance = DistanceToGuide(x, z, riverGuides[i], riverInfluence);
            nearestRiverDistance = Mathf.Min(nearestRiverDistance, distance);
            float inner = config.RiverValleyHalfWidth;
            float outer = inner + config.RiverValleyShoulder;
            float shoulderBlend = 1f - SmoothStep(inner, outer, distance);
            float carvedHeight = Mathf.Min(height, Mathf.Max(
                riverFloor,
                height - config.RiverValleyDepth));
            height = Mathf.Lerp(height, carvedHeight, shoulderBlend);
            float flatFloorBlend = 1f - SmoothStep(inner * 0.65f, inner, distance);
            height = Mathf.Lerp(height, Mathf.Min(height, riverFloor), flatFloorBlend);
        }

        // TerrainV2: a level rail bed at the base ground elevation (cut and
        // fill of at most a few metres) instead of a fixed +7 m causeway, so
        // railway grades stay near zero. Rail grading fades out before any
        // river valley floor, so a railway can never refill a river channel.
        float railValleyFloor = config.BaseElevation;
        float riverProtection = SmoothStep(
            config.RiverValleyHalfWidth,
            config.RiverValleyHalfWidth + 30f,
            nearestRiverDistance);
        // Stage 3B: beyond this distance both rail blends are exactly 0.
        float railInfluence = Mathf.Max(
            config.RailValleyHalfWidth + config.RailValleyShoulder,
            config.RailValleyHalfWidth);
        for (int i = 0; i < railGuides.Count; i++)
        {
            float distance = DistanceToGuide(x, z, railGuides[i], railInfluence);
            float inner = config.RailValleyHalfWidth;
            float outer = inner + config.RailValleyShoulder;
            float shoulderBlend = (1f - SmoothStep(inner, outer, distance)) * riverProtection;
            float carvedHeight = Mathf.Max(
                railValleyFloor,
                height - config.RailValleyDepth);
            height = Mathf.Lerp(height, carvedHeight, shoulderBlend);
            float flatFloorBlend = (1f - SmoothStep(inner * 0.65f, inner, distance)) * riverProtection;
            height = Mathf.Lerp(height, railValleyFloor, flatFloorBlend);
        }

        return Mathf.Clamp(height, 0f, config.TerrainHeight - 0.5f);
    }

    private static float ValueNoise(float x, float z, uint seed)
    {
        int x0 = Mathf.FloorToInt(x);
        int z0 = Mathf.FloorToInt(z);
        float tx = x - x0;
        float tz = z - z0;
        tx = tx * tx * tx * (tx * (tx * 6f - 15f) + 10f);
        tz = tz * tz * tz * (tz * (tz * 6f - 15f) + 10f);

        float a = LatticeNoise(x0, z0, seed);
        float b = LatticeNoise(x0 + 1, z0, seed);
        float c = LatticeNoise(x0, z0 + 1, seed);
        float d = LatticeNoise(x0 + 1, z0 + 1, seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
    }

    private static float LatticeNoise(int x, int z, uint seed)
    {
        unchecked
        {
            uint hash = seed ^ ((uint)x * 0x8DA6B343u) ^ ((uint)z * 0xD8163841u);
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) * (2f / 16777215f) - 1f;
        }
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0) return value < edge0 ? 0f : 1f;
        float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static float DistanceToPolyline(float x, float z, Vector3[] points)
    {
        float minimumSqr = float.PositiveInfinity;
        Vector2 point = new Vector2(x, z);
        for (int i = 0; i < points.Length - 1; i++)
        {
            Vector2 a = new Vector2(points[i].x, points[i].z);
            Vector2 b = new Vector2(points[i + 1].x, points[i + 1].z);
            Vector2 segment = b - a;
            float lengthSqr = segment.sqrMagnitude;
            float t = lengthSqr > 0.0001f
                ? Mathf.Clamp01(Vector2.Dot(point - a, segment) / lengthSqr)
                : 0f;
            minimumSqr = Mathf.Min(minimumSqr, (point - (a + segment * t)).sqrMagnitude);
        }
        return Mathf.Sqrt(minimumSqr);
    }

    /// <summary>
    /// TerrainV2: exact XZ distance to the guide's smooth sampled curve for
    /// any route shape (U, Z, meander...). Points farther than the largest
    /// valley influence return that reach, which every caller treats as
    /// "outside the corridor". (The former version assumed a route that
    /// advances monotonically along one axis and measured to the coarse
    /// control polygon.)
    /// </summary>
    private static float DistanceToGuide(float x, float z, Guide guide)
    {
        return DistanceToGuide(x, z, guide, float.PositiveInfinity);
    }

    /// <summary>
    /// Stage 3B: same result as scanning every segment registered in the
    /// query's cell. Candidates are visited in ascending order of a
    /// conservative per-cell lower bound, and the scan stops when the next
    /// bound exceeds the best exact distance found (no remaining segment can
    /// be closer), so the minimum and the returned value are unchanged.
    /// <paramref name="influenceLimit"/>: when the distance is at least this
    /// value, the limit itself is returned instead. Only callers for which
    /// every distance at or beyond the limit has an identical effect may pass
    /// a finite limit (EvaluateTerrainHeight); others pass +infinity.
    /// </summary>
    private static float DistanceToGuide(float x, float z, Guide guide, float influenceLimit)
    {
        Vector3[] points = guide.samples;
        if (guide.cells == null) return DistanceToPolyline(x, z, points);
        int cellX = Mathf.FloorToInt((x - guide.gridOrigin.x) / GuideGridCellSize);
        int cellZ = Mathf.FloorToInt((z - guide.gridOrigin.y) / GuideGridCellSize);
        if (cellX < 0 || cellZ < 0 || cellX >= guide.gridWidth || cellZ >= guide.gridHeight)
            return DistanceToPolyline(x, z, points);
        int cellIndex = cellZ * guide.gridWidth + cellX;
        int[] segments = guide.cellSegments[cellIndex];
        if (segments == null) return guide.gridReach;
        float[] bounds = guide.cellBounds[cellIndex];
        float[] boundsSqr = guide.cellBoundsSqr[cellIndex];

        float minimumSqr = float.PositiveInfinity;
        Vector2 point = new Vector2(x, z);
        for (int k = 0; k < segments.Length; k++)
        {
            // No remaining candidate can be closer than the best found.
            if (boundsSqr[k] > minimumSqr) break;
            // No remaining candidate can be closer than the influence limit.
            if (bounds[k] >= influenceLimit) break;
            int i = segments[k];
            Vector2 a = new Vector2(points[i].x, points[i].z);
            Vector2 b = new Vector2(points[i + 1].x, points[i + 1].z);
            Vector2 segment = b - a;
            float lengthSqr = segment.sqrMagnitude;
            float t = lengthSqr > 0.0001f
                ? Mathf.Clamp01(Vector2.Dot(point - a, segment) / lengthSqr)
                : 0f;
            minimumSqr = Mathf.Min(minimumSqr, (point - (a + segment * t)).sqrMagnitude);
        }
        float distance = Mathf.Sqrt(minimumSqr);
        return distance < influenceLimit ? distance : influenceLimit;
    }

    private static Vector3 EvaluateSpline(Vector3[] points, float t)
    {
        float scaled = Mathf.Clamp01(t) * (points.Length - 1);
        int i = Mathf.Min(Mathf.FloorToInt(scaled), points.Length - 2);
        float u = scaled - i;
        Vector3 p0 = points[Mathf.Max(0, i - 1)];
        Vector3 p1 = points[i];
        Vector3 p2 = points[i + 1];
        Vector3 p3 = points[Mathf.Min(points.Length - 1, i + 2)];
        float u2 = u * u;
        float u3 = u2 * u;
        return 0.5f * (2f * p1 + (-p0 + p2) * u +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * u3);
    }

    private static Vector3 PointAtDistance(Guide guide, float distance)
    {
        int index = FindGuideSegment(guide, distance, out float t);
        return Vector3.Lerp(guide.samples[index], guide.samples[index + 1], t);
    }

    private static Vector3 TangentAtDistance(Guide guide, float distance)
    {
        float delta = Mathf.Min(1f, guide.length * 0.002f);
        Vector3 before = PointAtDistance(guide, Mathf.Max(0f, distance - delta));
        Vector3 after = PointAtDistance(guide, Mathf.Min(guide.length, distance + delta));
        Vector3 tangent = after - before;
        tangent.y = 0f;
        return tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.forward;
    }

    private static int FindGuideSegment(Guide guide, float distance, out float t)
    {
        distance = Mathf.Clamp(distance, 0f, guide.length);
        for (int i = 0; i < guide.cumulativeDistances.Length - 1; i++)
        {
            float start = guide.cumulativeDistances[i];
            float end = guide.cumulativeDistances[i + 1];
            if (distance <= end || i == guide.cumulativeDistances.Length - 2)
            {
                t = end > start ? (distance - start) / (end - start) : 0f;
                return i;
            }
        }
        t = 0f;
        return 0;
    }

    private void FindPotentialGuideCrossings()
    {
        const float crossingScreenDistance = 10f;
        for (int r = 0; r < riverGuides.Count; r++)
        {
            for (int t = 0; t < railGuides.Count; t++)
            {
                Guide river = riverGuides[r];
                Guide rail = railGuides[t];
                for (int i = 0; i < river.samples.Length - 1; i += 2)
                {
                    for (int j = 0; j < rail.samples.Length - 1; j += 2)
                    {
                        Vector2 midpoint;
                        float gap = SegmentDistanceXZ(
                            river.samples[i], river.samples[i + 1],
                            rail.samples[j], rail.samples[j + 1], out midpoint);
                        if (gap <= crossingScreenDistance &&
                            !ContainsNear(potentialCrossings, midpoint, 35f))
                        {
                            potentialCrossings.Add(new Vector3(midpoint.x, 0f, midpoint.y));
                        }
                    }
                }
            }
        }
    }

    private static float SegmentDistanceXZ(
        Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, out Vector2 midpoint)
    {
        Vector2 a = new Vector2(a0.x, a0.z);
        Vector2 b = new Vector2(a1.x, a1.z);
        Vector2 c = new Vector2(b0.x, b0.z);
        Vector2 d = new Vector2(b1.x, b1.z);

        Vector2 r = b - a;
        Vector2 s = d - c;
        float denominator = Cross2D(r, s);
        if (Mathf.Abs(denominator) > 0.0001f)
        {
            float u = Cross2D(c - a, r) / denominator;
            float t = Cross2D(c - a, s) / denominator;
            if (u >= 0f && u <= 1f && t >= 0f && t <= 1f)
            {
                midpoint = a + r * t;
                return 0f;
            }
        }

        Vector2 closestA = ClosestPoint(a, c, d);
        Vector2 closestB = ClosestPoint(b, c, d);
        Vector2 closestC = ClosestPoint(c, a, b);
        Vector2 closestD = ClosestPoint(d, a, b);
        float best = (closestA - a).sqrMagnitude;
        Vector2 pointA = closestA;
        Vector2 pointB = a;
        float candidate = (closestB - b).sqrMagnitude;
        if (candidate < best) { best = candidate; pointA = closestB; pointB = b; }
        candidate = (closestC - c).sqrMagnitude;
        if (candidate < best) { best = candidate; pointA = c; pointB = closestC; }
        candidate = (closestD - d).sqrMagnitude;
        if (candidate < best) { best = candidate; pointA = d; pointB = closestD; }
        midpoint = (pointA + pointB) * 0.5f;
        return Mathf.Sqrt(best);
    }

    private static Vector2 ClosestPoint(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 segment = b - a;
        float denominator = segment.sqrMagnitude;
        float t = denominator > 0.0001f
            ? Mathf.Clamp01(Vector2.Dot(p - a, segment) / denominator)
            : 0f;
        return a + segment * t;
    }

    private static float Cross2D(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static bool ContainsNear(List<Vector3> points, Vector2 candidate, float distance)
    {
        float distanceSqr = distance * distance;
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 existing = new Vector2(points[i].x, points[i].z);
            if ((existing - candidate).sqrMagnitude < distanceSqr) return true;
        }
        return false;
    }

    private void ValidateTerrainChunks(out float maximumSeamError, out int colliderMatches)
    {
        maximumSeamError = 0f;
        colliderMatches = 0;
        int resolution = config.HeightmapResolution;

        for (int z = 0; z < config.ChunksZ; z++)
        {
            for (int x = 0; x < config.ChunksX; x++)
            {
                Terrain terrain = GetTerrainAtChunk(x, z);
                TerrainCollider collider = terrain != null
                    ? terrain.GetComponent<TerrainCollider>()
                    : null;
                if (collider != null && collider.terrainData == terrain.terrainData)
                {
                    colliderMatches++;
                }

                if (terrain == null) continue;
                if (x + 1 < config.ChunksX)
                {
                    Terrain right = GetTerrainAtChunk(x + 1, z);
                    float[,] leftEdge = terrain.terrainData.GetHeights(
                        resolution - 1, 0, 1, resolution);
                    float[,] rightEdge = right.terrainData.GetHeights(
                        0, 0, 1, resolution);
                    for (int i = 0; i < resolution; i++)
                    {
                        maximumSeamError = Mathf.Max(maximumSeamError,
                            Mathf.Abs(leftEdge[i, 0] - rightEdge[i, 0]) * config.TerrainHeight);
                    }
                }

                if (z + 1 < config.ChunksZ)
                {
                    Terrain top = GetTerrainAtChunk(x, z + 1);
                    float[,] bottomEdge = terrain.terrainData.GetHeights(
                        0, resolution - 1, resolution, 1);
                    float[,] topEdge = top.terrainData.GetHeights(
                        0, 0, resolution, 1);
                    for (int i = 0; i < resolution; i++)
                    {
                        maximumSeamError = Mathf.Max(maximumSeamError,
                            Mathf.Abs(bottomEdge[0, i] - topEdge[0, i]) * config.TerrainHeight);
                    }
                }
            }
        }
    }

    private Terrain GetTerrainAtChunk(int x, int z)
    {
        int index = z * config.ChunksX + x;
        return index >= 0 && index < terrainChunks.Count ? terrainChunks[index] : null;
    }

    /// <summary>
    /// In the Editor, reports go to this project's own
    /// Logs/WorldGenerationReports folder. Application.persistentDataPath is
    /// derived from companyName/productName, which this copied project shares
    /// with the original Ironveil-Unity project, so Editor runs there would
    /// overwrite the original project's reports. Built players keep
    /// persistentDataPath.
    /// </summary>
    private static string DiagnosticReportDirectory()
    {
#if UNITY_EDITOR
        string directory = Path.Combine(
            Path.GetDirectoryName(Application.dataPath), "Logs", "WorldGenerationReports");
#else
        string directory = Application.persistentDataPath;
#endif
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void BuildDiagnosticReport()
    {
        ValidateTerrainChunks(out float seamError, out int colliderMatches);
        bool deterministic = VerifyDeterministicSamples(out float deterministicError);
        int expectedChunks = config.ChunksX * config.ChunksZ;

        StringBuilder report = new StringBuilder(1200);
        report.AppendLine("IRONVEIL World-Generation Prototype Capacity Screening");
        report.AppendLine("======================================================");
        report.AppendLine("Seed: " + config.WorldSeed);
        report.AppendLine("World: " + config.MapSize.x.ToString("F0") + " x " +
                          config.MapSize.y.ToString("F0") + " world units");
        report.AppendLine("Requested distribution preserved: Tank 110, Heli 90, Air 80, " +
                          "Naval 25, Rail 17, Heli Naval 11, Heli Rail 7, " +
                          "Air Naval 6, Air Rail 4 (350 total).");
        report.AppendLine("Terrain chunks: " + terrainChunks.Count + "/" + expectedChunks +
                          "; heightmap resolution: " + config.HeightmapResolution);
        report.AppendLine("Seed/data reproducibility sample check: " +
            (deterministic ? "PASS" : "FAIL") +
            " (max TerrainData-vs-seed height error " + deterministicError.ToString("F5") + " m)");
        report.AppendLine("Maximum shared-edge height difference: " + seamError.ToString("F6") + " m");
        if (snowTexelCount > 0)
        {
            report.AppendLine(string.Format(
                "Snow cover (all {0} alphamap texels, {4} chunks): mean snow weight {1:P1}, snow-dominant (>0.5) {2:P1}, bare rock (<0.2) {3:P1}",
                snowTexelCount, snowWeightSum / snowTexelCount,
                snowCoveredTexelCount / (double)snowTexelCount,
                bareRockTexelCount / (double)snowTexelCount,
                terrainChunks.Count));
        }
        if (hills.Count > 0)
        {
            report.AppendLine(string.Format(
                "Isolated hills: {0} of {1} placed; radius {2:F0}-{3:F0} m; height {4:F1}-{5:F1} m; steepest profile slope {6:F1} deg",
                hills.Count, hillTargetCount,
                hills.Min(h => h.radius), hills.Max(h => h.radius),
                hills.Min(h => h.height), hills.Max(h => h.height),
                Mathf.Atan(hills.Max(h => 1.5f * h.height / h.radius)) * Mathf.Rad2Deg));
        }
        report.AppendLine("TerrainCollider-to-TerrainData matches: " + colliderMatches + "/" + expectedChunks);
        report.AppendLine("Potential river/rail guide crossings (bridge planning points): " + potentialCrossings.Count);
        report.AppendLine();
        report.AppendLine("Mirrored placement screen: footprint variation <= " +
            config.MaximumFootprintVariation.ToString("F2") + " m; sample slope <= " +
            config.MaximumSlope.ToString("F1") + " deg; land spacing " +
            config.LandSpacing.ToString("F1") + " m; route spacing " +
            config.RouteSpacing.ToString("F1") + " m.");
        report.AppendLine("Rail hangar guide alignment: 0 m by construction (current tolerance " +
            config.RailHangarTolerance.ToString("F2") + " m).");

        int landCapacity = ScreenLandCapacity();
        int navalCapacity = ScreenNavalCapacity();
        int railCapacity = ScreenRailCapacity();

        report.AppendLine("Land conservative-envelope terrain-screened sites: " + landCapacity +
                          " (target " + config.LandCapacityTarget + ")");
        report.AppendLine("Naval geometry-screened guide slots: " + navalCapacity +
                          " (target " + config.NavalCapacityTarget + ")");
        report.AppendLine("Rail geometry-screened guide slots: " + railCapacity +
                          " (target " + config.RailCapacityTarget + ")");
        report.AppendLine();
        report.AppendLine("IMPORTANT: This is a terrain/corridor screening report, not a BaseSpawner run.");
        report.AppendLine("Naval/Rail capacities use measured prefab renderer bounds and mirrored corridor rules;");
        report.AppendLine("they do not validate final route-generator meshes, exact turret anchors, or spawning order.");
        report.AppendLine("Family capacities are screened independently; cross-family allocation conflicts are not simulated.");
        report.AppendLine("A full unchanged BaseSpawner run is required before claiming 350-base support.");
        report.AppendLine("Screening target result: " +
            (landCapacity >= config.LandCapacityTarget &&
             navalCapacity >= config.NavalCapacityTarget &&
             railCapacity >= config.RailCapacityTarget
                ? "ALL SCREENING TARGETS MET (NOT SPAWN CERTIFICATION)"
                : "ONE OR MORE SCREENING TARGETS NOT MET"));

        if (!string.IsNullOrEmpty(baseSpawnReport))
        {
            report.AppendLine();
            report.AppendLine(baseSpawnReport);
        }
        if (!string.IsNullOrEmpty(lastBaseSpawnerReport))
        {
            report.AppendLine();
            report.AppendLine(lastBaseSpawnerReport);
        }

        lastDiagnosticReport = report.ToString();
        Debug.Log(lastDiagnosticReport, this);

        try
        {
            string reportName = "IronveilWorldGeneration_Seed_" + config.WorldSeed + ".txt";
            string reportPath = Path.Combine(DiagnosticReportDirectory(), reportName);
            File.WriteAllText(reportPath, lastDiagnosticReport);
            Debug.Log("WorldGenerationPrototype: Diagnostic report saved to " + reportPath, this);
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "WorldGenerationPrototype: Could not save the diagnostic report: " +
                exception.Message, this);
        }
    }

    private bool VerifyDeterministicSamples(out float maximumHeightError)
    {
        maximumHeightError = 0f;
        int globalIntervalsX = (config.HeightmapResolution - 1) * config.ChunksX;
        int globalIntervalsZ = (config.HeightmapResolution - 1) * config.ChunksZ;
        uint firstHash = 2166136261u;
        uint secondHash = 2166136261u;
        for (int i = 0; i < 257; i++)
        {
            int xIndex = (i * 67) % (globalIntervalsX + 1);
            int zIndex = (i * 131) % (globalIntervalsZ + 1);
            float x = -config.MapSize.x * 0.5f +
                      xIndex * config.MapSize.x / globalIntervalsX;
            float z = -config.MapSize.y * 0.5f +
                      zIndex * config.MapSize.y / globalIntervalsZ;
            float a = EvaluateTerrainHeight(x, z);
            float b = EvaluateTerrainHeight(x, z);
            if (!TrySampleTerrain(x, z, out float terrainHeight, out _))
            {
                maximumHeightError = float.PositiveInfinity;
                return false;
            }
            maximumHeightError = Mathf.Max(maximumHeightError,
                Mathf.Abs(a - terrainHeight));
            firstHash = MixFloat(firstHash, a);
            secondHash = MixFloat(secondHash, b);
        }
        return firstHash == secondHash && maximumHeightError <= 0.02f;
    }

    private static uint MixFloat(uint hash, float value)
    {
        unchecked
        {
            uint bits = (uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            return (hash ^ bits) * 16777619u;
        }
    }

    private int ScreenLandCapacity()
    {
        Footprint footprint = ConservativeFootprint(config.LandBasePrefabs);
        if (!footprint.valid) return 0;

        float halfX = config.MapSize.x * 0.5f;
        float halfZ = config.MapSize.y * 0.5f;
        float riverExclusion = config.GuideRiverWidth * 0.5f + config.LandRiverClearance;
        float spacingSqr = config.LandSpacing * config.LandSpacing;
        List<Vector2> accepted = new List<Vector2>();
        Quaternion rotation = Quaternion.identity;

        for (float z = -halfZ + config.EdgePadding; z <= halfZ - config.EdgePadding;
             z += config.LandSpacing)
        {
            for (float x = -halfX + config.EdgePadding; x <= halfX - config.EdgePadding;
                 x += config.LandSpacing)
            {
                Vector3 root = new Vector3(x, 0f, z);
                Vector3 center = root + rotation * footprint.center;
                float radiusToEdgeX = ProjectedHalfExtent(footprint.size, rotation, Vector3.right);
                float radiusToEdgeZ = ProjectedHalfExtent(footprint.size, rotation, Vector3.forward);
                if (Mathf.Abs(center.x) + radiusToEdgeX > halfX - config.EdgePadding ||
                    Mathf.Abs(center.z) + radiusToEdgeZ > halfZ - config.EdgePadding)
                {
                    continue;
                }

                if (DistanceToGuides(x, z, riverGuides) < riverExclusion)
                {
                    continue;
                }

                bool tooClose = false;
                Vector2 candidate = new Vector2(x, z);
                for (int i = 0; i < accepted.Count; i++)
                {
                    if ((candidate - accepted[i]).sqrMagnitude < spacingSqr)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                if (!SampleFootprint(center, footprint, rotation,
                        out float variation, out float maxSlope, out float averageTilt))
                {
                    continue;
                }

                if (variation <= config.MaximumFootprintVariation &&
                    maxSlope <= config.MaximumSlope &&
                    averageTilt <= config.MaximumLandTilt)
                {
                    accepted.Add(candidate);
                }
            }
        }
        return accepted.Count;
    }

    private int ScreenNavalCapacity()
    {
        Footprint baseFootprint = ConservativeFootprint(config.NavalBasePrefabs);
        Footprint hangar = MeasureFootprint(config.NavalHangarPrefab);
        if (!baseFootprint.valid || !hangar.valid || riverGuides.Count == 0) return 0;

        float halfRiver = config.GuideRiverWidth * 0.5f;
        float hangarLong = Mathf.Max(hangar.size.x, hangar.size.z);
        float hangarShort = Mathf.Min(hangar.size.x, hangar.size.z);
        float hangarHalfAcross = Mathf.Max(hangarLong, 0.01f) * 0.5f;
        float hangarHalfAlong = Mathf.Max(hangarShort, 0.01f) * 0.5f;
        float baseHalfAcross = Mathf.Max(baseFootprint.size.x, baseFootprint.size.z) * 0.5f;
        float targetOverlap = Mathf.Max(0.25f, config.NavalHangarTargetWaterOverlap);
        float hangarCenterOffset = Mathf.Max(0f,
            halfRiver - (2f * targetOverlap - 1f) * hangarHalfAcross);
        float overlapFraction = OneDimensionalOverlapFraction(
            halfRiver, hangarCenterOffset, hangarHalfAcross);
        if (overlapFraction < 0.25f) return 0;

        float slotSpacing = Mathf.Max(config.RouteSpacing,
            2f * hangarHalfAlong + config.NavalHangarEdgeClearance);
        float baseClearance = Mathf.Min(config.NavalBankPadClearance, hangarLong);
        float baseCenterDistance = hangarCenterOffset + hangarHalfAcross +
                                   baseHalfAcross + baseClearance;
        List<Vector2> acceptedBases = new List<Vector2>();
        int accepted = 0;

        for (int guideIndex = 0; guideIndex < riverGuides.Count; guideIndex++)
        {
            Guide guide = riverGuides[guideIndex];
            float endMargin = Mathf.Max(20f, slotSpacing * 0.5f);
            for (int bank = -1; bank <= 1; bank += 2)
            {
                for (float distance = endMargin;
                     distance <= guide.length - endMargin;
                     distance += slotSpacing)
                {
                    Vector3 routePoint = PointAtDistance(guide, distance);
                    Vector3 tangent = TangentAtDistance(guide, distance);
                    Vector3 bankSide = Vector3.Cross(Vector3.up, tangent).normalized * bank;
                    Vector3 baseCenter = routePoint + bankSide * baseCenterDistance;

                    if (!InsidePaddedMap(baseCenter, baseFootprint.size) ||
                        Vector3.Distance(baseCenter, routePoint) - baseHalfAcross < halfRiver)
                    {
                        continue;
                    }

                    bool tooClose = false;
                    Vector2 xz = new Vector2(baseCenter.x, baseCenter.z);
                    for (int i = 0; i < acceptedBases.Count; i++)
                    {
                        if ((xz - acceptedBases[i]).sqrMagnitude <
                            config.RouteSpacing * config.RouteSpacing)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose) continue;

                    Quaternion baseRotation = Quaternion.LookRotation(-bankSide, Vector3.up);
                    if (!SampleFootprint(baseCenter, baseFootprint, baseRotation,
                            out float variation, out float slope, out _))
                    {
                        continue;
                    }
                    if (variation > config.MaximumFootprintVariation ||
                        slope > config.MaximumSlope)
                    {
                        continue;
                    }

                    acceptedBases.Add(xz);
                    accepted++;
                }
            }
        }
        return accepted;
    }

    private int ScreenRailCapacity()
    {
        Footprint baseFootprint = ConservativeFootprint(config.RailBasePrefabs);
        Footprint hangar = MeasureFootprint(config.RailHangarPrefab);
        if (!baseFootprint.valid || !hangar.valid || railGuides.Count == 0) return 0;

        float halfAlong = Mathf.Max(hangar.size.x, hangar.size.z) * 0.5f;
        float halfAcross = Mathf.Min(hangar.size.x, hangar.size.z) * 0.5f;
        float baseHalfAcross = Mathf.Max(baseFootprint.size.x, baseFootprint.size.z) * 0.5f;
        float corridorHalfWidth = Mathf.Max(
            config.RailGauge * 0.5f + config.RailWidth * 0.5f,
            config.SleeperLength * 0.5f);
        if (halfAcross < config.RailGauge * 0.5f + 0.01f) return 0;

        float baseCenterDistance = halfAcross + baseHalfAcross +
                                   config.RailBaseHangarClearance;
        float minimumCandidateSpacing = Mathf.Max(
            config.RouteSpacing, 2f * halfAlong + config.RouteSpacing);
        List<Vector2> acceptedBases = new List<Vector2>();
        int accepted = 0;

        for (int guideIndex = 0; guideIndex < railGuides.Count; guideIndex++)
        {
            Guide guide = railGuides[guideIndex];
            float endMargin = Mathf.Max(25f, minimumCandidateSpacing);
            int slot = 0;
            for (float distance = endMargin;
                 distance <= guide.length - endMargin;
                 distance += minimumCandidateSpacing)
            {
                Vector3 routePoint = PointAtDistance(guide, distance);
                Vector3 tangent = TangentAtDistance(guide, distance);
                Vector3 across = Vector3.Cross(Vector3.up, tangent).normalized;
                Vector3 bankSide = ((slot + guideIndex) & 1) == 0 ? across : -across;
                slot++;

                if (DistanceToGuides(routePoint.x, routePoint.z, riverGuides) <
                    config.RailRiverClearance)
                {
                    continue;
                }

                Vector3 baseCenter = routePoint + bankSide * baseCenterDistance;
                if (!InsidePaddedMap(routePoint, hangar.size) ||
                    !InsidePaddedMap(baseCenter, baseFootprint.size) ||
                    Vector3.Distance(baseCenter, routePoint) - baseHalfAcross <
                    corridorHalfWidth + 0.05f)
                {
                    continue;
                }

                bool tooClose = false;
                Vector2 xz = new Vector2(baseCenter.x, baseCenter.z);
                for (int i = 0; i < acceptedBases.Count; i++)
                {
                    if ((xz - acceptedBases[i]).sqrMagnitude <
                        config.RouteSpacing * config.RouteSpacing)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                Quaternion hangarRotation = RotationAligningLongAxis(hangar, tangent);
                Quaternion baseRotation = Quaternion.LookRotation(-bankSide, Vector3.up);
                if (!SampleFootprint(routePoint, hangar, hangarRotation,
                        out float hangarVariation, out float hangarSlope, out _) ||
                    !SampleFootprint(baseCenter, baseFootprint, baseRotation,
                        out float baseVariation, out float baseSlope, out _))
                {
                    continue;
                }

                if (hangarVariation > config.MaximumFootprintVariation ||
                    hangarSlope > config.MaximumSlope ||
                    baseVariation > config.MaximumFootprintVariation ||
                    baseSlope > config.MaximumSlope)
                {
                    continue;
                }

                acceptedBases.Add(xz);
                accepted++;
            }
        }
        return accepted;
    }

    private static Quaternion RotationAligningLongAxis(Footprint footprint, Vector3 direction)
    {
        Vector3 localAxis = footprint.size.x >= footprint.size.z
            ? Vector3.right
            : Vector3.forward;
        localAxis.y = 0f;
        direction.y = 0f;
        if (localAxis.sqrMagnitude < 0.0001f || direction.sqrMagnitude < 0.0001f)
        {
            return Quaternion.identity;
        }
        return Quaternion.Euler(0f,
            Vector3.SignedAngle(localAxis.normalized, direction.normalized, Vector3.up), 0f);
    }

    private static float OneDimensionalOverlapFraction(
        float riverHalfWidth,
        float hangarCenterOffset,
        float hangarHalfWidth)
    {
        float min = Mathf.Max(-riverHalfWidth, hangarCenterOffset - hangarHalfWidth);
        float max = Mathf.Min(riverHalfWidth, hangarCenterOffset + hangarHalfWidth);
        float width = Mathf.Max(0f, max - min);
        return width / Mathf.Max(0.001f, hangarHalfWidth * 2f);
    }

    private bool SampleFootprint(
        Vector3 center,
        Footprint footprint,
        Quaternion rotation,
        out float variation,
        out float maximumSlope,
        out float averageTilt)
    {
        variation = float.PositiveInfinity;
        maximumSlope = float.PositiveInfinity;
        averageTilt = float.PositiveInfinity;
        if (!footprint.valid) return false;

        float halfX = footprint.size.x * 0.5f;
        float halfZ = footprint.size.z * 0.5f;
        Vector3 bottomOffset = new Vector3(0f, -footprint.size.y * 0.5f, 0f);
        float minHeight = float.PositiveInfinity;
        float maxHeight = float.NegativeInfinity;
        Vector3 averageNormal = Vector3.zero;
        float steepest = 0f;

        // Same 9-point layout as BaseSpawner: centre, edge midpoints, corners.
        for (int i = 0; i < FootprintSampleCoordinates.Length; i++)
        {
            Vector2 coordinate = FootprintSampleCoordinates[i];
            Vector3 offset = rotation * (bottomOffset + new Vector3(
                coordinate.x * halfX, 0f, coordinate.y * halfZ));
            Vector3 point = center + offset;
            if (!TrySampleTerrain(point.x, point.z, out float height, out Vector3 normal))
            {
                return false;
            }

            minHeight = Mathf.Min(minHeight, height);
            maxHeight = Mathf.Max(maxHeight, height);
            averageNormal += normal;
            steepest = Mathf.Max(steepest, Vector3.Angle(normal, Vector3.up));
        }

        averageNormal.Normalize();
        variation = maxHeight - minHeight;
        maximumSlope = steepest;
        averageTilt = Vector3.Angle(averageNormal, Vector3.up);
        return true;
    }

    private bool TrySampleTerrain(float worldX, float worldZ, out float height, out Vector3 normal)
    {
        height = 0f;
        normal = Vector3.up;
        if (terrainChunks.Count == 0) return false;

        float chunkSizeX = config.MapSize.x / config.ChunksX;
        float chunkSizeZ = config.MapSize.y / config.ChunksZ;
        float halfX = config.MapSize.x * 0.5f;
        float halfZ = config.MapSize.y * 0.5f;
        if (worldX < -halfX || worldX > halfX || worldZ < -halfZ || worldZ > halfZ)
        {
            return false;
        }

        int chunkX = Mathf.Clamp(
            Mathf.FloorToInt((worldX + halfX) / chunkSizeX), 0, config.ChunksX - 1);
        int chunkZ = Mathf.Clamp(
            Mathf.FloorToInt((worldZ + halfZ) / chunkSizeZ), 0, config.ChunksZ - 1);
        Terrain terrain = GetTerrainAtChunk(chunkX, chunkZ);
        if (terrain == null || terrain.terrainData == null) return false;

        // Convert world coordinates to this tile's TerrainData local coordinates.
        Vector3 local = new Vector3(
            worldX - terrain.transform.position.x,
            0f,
            worldZ - terrain.transform.position.z);
        float normalizedX = Mathf.Clamp01(local.x / terrain.terrainData.size.x);
        float normalizedZ = Mathf.Clamp01(local.z / terrain.terrainData.size.z);
        height = terrain.transform.position.y +
                 terrain.terrainData.GetInterpolatedHeight(normalizedX, normalizedZ);
        normal = terrain.terrainData.GetInterpolatedNormal(normalizedX, normalizedZ).normalized;
        return true;
    }

    /// <summary>
    /// Samples the generated TerrainData (not the analytic height function).
    /// This is the appropriate seam-aware query for prototype consumers.
    /// </summary>
    public bool TrySampleGeneratedTerrain(
        float worldX,
        float worldZ,
        out float height,
        out Vector3 normal)
    {
        return TrySampleTerrain(worldX, worldZ, out height, out normal);
    }

    private bool InsidePaddedMap(Vector3 center, Vector3 footprintSize)
    {
        float halfX = config.MapSize.x * 0.5f - config.EdgePadding;
        float halfZ = config.MapSize.y * 0.5f - config.EdgePadding;
        return Mathf.Abs(center.x) + footprintSize.x * 0.5f <= halfX &&
               Mathf.Abs(center.z) + footprintSize.z * 0.5f <= halfZ;
    }

    private float DistanceToGuides(float x, float z, List<Guide> guides)
    {
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < guides.Count; i++)
        {
            nearest = Mathf.Min(nearest,
                DistanceToPolyline(x, z, guides[i].controls));
        }
        return nearest;
    }

    private Footprint ConservativeFootprint(GameObject[] prefabs)
    {
        if (prefabs == null) return default(Footprint);
        Vector3 min = new Vector3(float.PositiveInfinity,
            float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity,
            float.NegativeInfinity, float.NegativeInfinity);
        int measured = 0;
        for (int i = 0; i < prefabs.Length; i++)
        {
            Footprint candidate = MeasureFootprint(prefabs[i]);
            if (!candidate.valid) continue;
            Vector3 candidateMin = candidate.center - candidate.size * 0.5f;
            Vector3 candidateMax = candidate.center + candidate.size * 0.5f;
            min = Vector3.Min(min, candidateMin);
            max = Vector3.Max(max, candidateMax);
            measured++;
        }
        if (measured == 0) return default(Footprint);
        return new Footprint
        {
            center = (min + max) * 0.5f,
            size = max - min,
            valid = true
        };
    }

    private Footprint MeasureFootprint(GameObject prefab)
    {
        if (prefab == null) return default(Footprint);
        GameObject probe = Instantiate(prefab, Vector3.zero, Quaternion.identity);
        probe.hideFlags = HideFlags.HideAndDontSave;
        Renderer[] renderers = probe.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            if (Application.isPlaying) Destroy(probe);
            else DestroyImmediate(probe);
            return default(Footprint);
        }

        Bounds worldBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            worldBounds.Encapsulate(renderers[i].bounds);
        }

        Vector3 localCenter = probe.transform.InverseTransformPoint(worldBounds.center);
        Vector3 localSize = worldBounds.size;
        if (Application.isPlaying) Destroy(probe);
        else DestroyImmediate(probe);
        return new Footprint
        {
            center = localCenter,
            size = localSize,
            valid = localSize.x > 0.001f && localSize.z > 0.001f
        };
    }

    private static float ProjectedHalfExtent(Vector3 size, Quaternion rotation, Vector3 axis)
    {
        axis.y = 0f;
        axis.Normalize();
        Vector3 right = rotation * Vector3.right;
        Vector3 forward = rotation * Vector3.forward;
        return Mathf.Abs(Vector3.Dot(right, axis)) * size.x * 0.5f +
               Mathf.Abs(Vector3.Dot(forward, axis)) * size.z * 0.5f;
    }

    private void FramePrototypeCamera()
    {
        if (prototypeCamera == null) return;
        prototypeCamera.orthographic = true;
        float aspect = Mathf.Max(0.1f, prototypeCamera.aspect);
        prototypeCamera.orthographicSize = Mathf.Max(
            config.MapSize.y * 0.64f,
            config.MapSize.x / aspect * 0.64f);
        Vector3 center = new Vector3(0f, config.BaseElevation + 40f, 0f);
        float distance = Mathf.Max(config.MapSize.x, config.MapSize.y) * 0.9f;
        Vector3 elevatedDirection = new Vector3(0f, 0.78f, -0.63f).normalized;
        prototypeCamera.transform.position = center + elevatedDirection * distance;
        prototypeCamera.transform.LookAt(center);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.1f, 0.7f, 1f, 1f);
        foreach (Guide guide in riverGuides)
        {
            DrawGuideGizmo(guide);
        }
        Gizmos.color = new Color(1f, 0.55f, 0.1f, 1f);
        foreach (Guide guide in railGuides)
        {
            DrawGuideGizmo(guide);
        }
        Gizmos.color = Color.magenta;
        for (int i = 0; i < potentialCrossings.Count; i++)
        {
            Vector3 point = potentialCrossings[i];
            point.y = EvaluateTerrainHeight(point.x, point.z) + 8f;
            Gizmos.DrawWireSphere(point, 8f);
        }
    }

    private void DrawGuideGizmo(Guide guide)
    {
        if (guide.samples == null || guide.samples.Length < 2) return;
        for (int i = 0; i < guide.samples.Length - 1; i++)
        {
            Vector3 a = guide.samples[i];
            Vector3 b = guide.samples[i + 1];
            a.y = EvaluateTerrainHeight(a.x, a.z) + 2f;
            b.y = EvaluateTerrainHeight(b.x, b.z) + 2f;
            Gizmos.DrawLine(a, b);
        }
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(lastDiagnosticReport)) return;
        Rect area = new Rect(12f, 12f, Mathf.Min(600f, Screen.width - 24f), 310f);
        GUI.Box(area, "World Generation Test — Screening Report");
        GUI.Label(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 38f),
            lastDiagnosticReport);
    }

    private void OnDestroy()
    {
        ReleaseWorkBuffers();
        ClearGuideVisuals();
        foreach (TerrainData data in generatedTerrainData)
        {
            if (data != null) DestroyGeneratedObject(data);
        }
        generatedTerrainData.Clear();
        DestroyGroundLayers();
    }
}
