using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

public class BaseSpawner : MonoBehaviour
{
    private const float GeometryValidationEpsilon = 0.001f;
    private const float TargetNavalHangarWaterOverlap = 0.85f;
    // Largest share of the Naval Hangar's measured height allowed below water.
    private const float MaxNavalHangarDraughtFraction = 0.1f;
    // Naval main-base seat height above the lowest footprint sample, as a
    // share of the measured footprint height variation.
    private const float NavalBaseSeatFraction = 0.25f;
    // Ground contact (TrySeatOnFootprint): rim samples on the platform outline,
    // and the share of the measured terrain relief left as a gap under the
    // lowest contact (the remainder is embedded, which reads as a foundation).
    private const int GroundContactRingSamples = 16;
    private const float GroundContactAirShare = 1f / 3f;
    private const float TargetNavalHangarEdgeClearance = 7.5f;
    private const int TerrainFootprintSampleCount = 9;
    private const int TerrainAlignmentSampleIterations = 6;
    private const float TerrainAlignmentSampleTolerance = 0.05f;
    private const int MaxTerrainSamplingRepresentativeRecords = 6;
    private static readonly Vector2[] TerrainFootprintSampleCoordinates =
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

    [Serializable]
    private class BaseEntry
    {
        public string baseName;
        public GameObject prefab;
        [Min(0)] public int count;
        public PlacementType placement;
    }

    private enum PlacementType
    {
        Land,
        River,
        Rail
    }

    private class BaseRequest
    {
        public string baseName;
        public GameObject prefab;
        public PlacementType placement;
        public int index;
        public RouteIndexSamplingState routeSamplingState;
        // Index into AllRiverGenerators()/AllRailGenerators(); always 0 on
        // the single-generator path.
        public int preferredRouteId;
        public int routeId;
    }

    private sealed class RouteIndexSamplingState
    {
        private readonly uint initialSeed;
        private uint randomState;
        private int routePointCount = -1;
        private int availableRouteIndices;
        private int[] cycleOrder = Array.Empty<int>();
        private int cycleOrderCursor;
        private readonly HashSet<int> evaluatedIndicesThisCycle =
            new HashSet<int>();
        private readonly HashSet<int> evaluatedIndicesEver =
            new HashSet<int>();

        public int UniqueIndicesAttempted => evaluatedIndicesEver.Count;
        public int RepeatedIndexAttempts { get; private set; }
        public int CompleteCyclesExhausted { get; private set; }
        public int AvailableRouteIndices => availableRouteIndices;

        public RouteIndexSamplingState(uint seed)
        {
            initialSeed = seed == 0u ? 0x9E3779B9u : seed;
            randomState = initialSeed;
        }

        public bool TryGetRawIndex(
            int routeCount,
            int randomizedPreferredIndex,
            out int index)
        {
            index = 0;
            if (!EnsureRoute(routeCount))
            {
                return false;
            }

            if (evaluatedIndicesThisCycle.Count >= availableRouteIndices)
            {
                BeginNextCycle();
            }

            if (IsAvailableIndex(randomizedPreferredIndex) &&
                !evaluatedIndicesThisCycle.Contains(randomizedPreferredIndex))
            {
                index = randomizedPreferredIndex;
                return true;
            }

            index = FindNextUnevaluatedIndex();
            return IsAvailableIndex(index) &&
                   !evaluatedIndicesThisCycle.Contains(index);
        }

        public int CommitCandidateIndex(
            int routeCount,
            int rawIndex,
            int proposedFinalIndex)
        {
            if (!EnsureRoute(routeCount))
            {
                return 0;
            }

            if (evaluatedIndicesThisCycle.Count >= availableRouteIndices)
            {
                BeginNextCycle();
            }

            int finalIndex;
            if (IsAvailableIndex(proposedFinalIndex) &&
                !evaluatedIndicesThisCycle.Contains(proposedFinalIndex))
            {
                finalIndex = proposedFinalIndex;
            }
            else if (IsAvailableIndex(rawIndex) &&
                     !evaluatedIndicesThisCycle.Contains(rawIndex))
            {
                finalIndex = rawIndex;
            }
            else
            {
                finalIndex = FindNextUnevaluatedIndex();
            }

            if (!evaluatedIndicesThisCycle.Add(finalIndex))
            {
                return finalIndex;
            }

            if (!evaluatedIndicesEver.Add(finalIndex))
            {
                RepeatedIndexAttempts++;
            }

            if (evaluatedIndicesThisCycle.Count == availableRouteIndices)
            {
                CompleteCyclesExhausted++;
            }

            return finalIndex;
        }

        private bool EnsureRoute(int routeCount)
        {
            if (routeCount < 3)
            {
                return false;
            }

            if (routePointCount == routeCount)
            {
                return availableRouteIndices > 0;
            }

            routePointCount = routeCount;
            availableRouteIndices = routeCount - 2;
            evaluatedIndicesThisCycle.Clear();
            evaluatedIndicesEver.Clear();
            RepeatedIndexAttempts = 0;
            CompleteCyclesExhausted = 0;
            randomState = MixSeed(initialSeed, (uint)routeCount);
            cycleOrder = new int[availableRouteIndices];
            for (int i = 0; i < availableRouteIndices; i++)
            {
                cycleOrder[i] = i + 1;
            }

            ShuffleCycleOrder();
            cycleOrderCursor = 0;
            return true;
        }

        private bool IsAvailableIndex(int index)
        {
            return index >= 1 && index < routePointCount - 1;
        }

        private int FindNextUnevaluatedIndex()
        {
            for (int i = 0; i < cycleOrder.Length; i++)
            {
                int candidate = cycleOrder[cycleOrderCursor];
                cycleOrderCursor = (cycleOrderCursor + 1) % cycleOrder.Length;
                if (!evaluatedIndicesThisCycle.Contains(candidate))
                {
                    return candidate;
                }
            }

            BeginNextCycle();
            return cycleOrder[cycleOrderCursor++ % cycleOrder.Length];
        }

        private void BeginNextCycle()
        {
            evaluatedIndicesThisCycle.Clear();
            ShuffleCycleOrder();
            cycleOrderCursor = 0;
        }

        private void ShuffleCycleOrder()
        {
            for (int i = cycleOrder.Length - 1; i > 0; i--)
            {
                int j = NextIndex(i + 1);
                int temp = cycleOrder[i];
                cycleOrder[i] = cycleOrder[j];
                cycleOrder[j] = temp;
            }
        }

        private int NextIndex(int exclusiveMax)
        {
            uint value = NextRandomUInt();
            return (int)(((ulong)value * (uint)exclusiveMax) >> 32);
        }

        private uint NextRandomUInt()
        {
            uint value = randomState;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            randomState = value == 0u ? 0xA341316Cu : value;
            return randomState;
        }

        private static uint MixSeed(uint seed, uint value)
        {
            unchecked
            {
                uint mixed = seed ^ 2166136261u;
                mixed = (mixed ^ value) * 16777619u;
                return mixed == 0u ? 0x9E3779B9u : mixed;
            }
        }
    }

    private sealed class RouteSamplingAggregate
    {
        public int failedRequests;
        public long uniqueRouteIndices;
        public long availableRouteIndices;
        public long repeatedRouteIndexAttempts;
        public long completeCyclesExhausted;
    }

    // Deterministic prefab-anchor data measured once per prefab from a
    // temporary instance. All values are root-local.
    private struct PrefabAnchor
    {
        public Vector3 localPosition;      // hangar transform localPosition
        public Quaternion localRotation;   // hangar transform localRotation
        public Vector3 localScale;         // hangar transform localScale
        public Vector3 boundsCenter;       // renderer bounds centre (root-local)
        public Vector3 boundsSize;         // renderer bounds size
        public Vector3 trackAxisLocal;     // prefab's longest horizontal geometry axis
        public float trackAxisLength;      // measured geometry extent along that axis
        public bool hasRenderer;
    }

    private struct PrefabFootprint
    {
        public Vector3 boundsCenter;
        public Vector3 boundsSize;
        public Vector3 turretFacingLocal;
        public bool hasRenderer;
        public bool hasTurretFacing;
    }

    // Full validation result for a route placement.
    private struct PlacementValidation
    {
        public bool valid;
        public bool hangarOverWater;   // naval: visible hangar over water corridor
        public bool baseOnLand;        // naval: circular base footprint on land
        public bool hangarOnTrack;     // rail: hangar centred over track centreline
        public bool baseBesideTrack;   // rail: circular base outside rail corridor
        public bool hangarCoversRails; // rail: hangar wide enough for both rails
        public int routeId;            // which river/railway (0 when single)
        public int routeIndex;
        public Vector3 routePoint;
        public Vector3 routeTangent;
        public Vector3 hangarWorldPosition;
        public Quaternion hangarWorldRotation;
        public Vector3 hangarRootPosition;
        public Quaternion hangarRootRotation;
        public float hangarRootScale;
        public float localRiverWidth;
        public float riverNarrowingTaperHalfDistance;
        public float hangarWaterOverlapFraction;
        public float hangarRouteDistance;
        public float rootRouteDistance;
        public Vector3 rootPosition;
        public Quaternion rootRotation;
        public Vector3 navalBaseFootprintCenter;
        public Vector3 navalBaseFootprintSize;
        public Quaternion navalBaseFootprintRotation;
        public Vector3 navalHangarFootprintSize;
        public float footprintHeightVariation;
        public float footprintMaxSlope;
        public string rejectReason;
    }

    private struct PlacedNavalPlacement
    {
        public Vector3 rootPosition;
        public Vector3 baseFootprintCenter;
        public Vector3 baseFootprintSize;
        public Quaternion baseFootprintRotation;
        public Vector3 routePoint;
        public Vector3 routeTangent;
        public int routeIndex;
        public int routeId;
        public Vector3 hangarCenter;
        public Vector3 hangarFootprintSize;
        public Quaternion hangarRootRotation;
    }

    private struct FootprintCornersXZ
    {
        public Vector2 c0;
        public Vector2 c1;
        public Vector2 c2;
        public Vector2 c3;

        public Vector2 Get(int index)
        {
            switch (index)
            {
                case 0: return c0;
                case 1: return c1;
                case 2: return c2;
                default: return c3;
            }
        }
    }

    // Result of sampling the terrain around a base footprint.
    private struct TerrainSampleResult
    {
        public Vector3 position;          // recommended root position (XZ from candidate, Y from terrain)
        public Vector3 normal;            // averaged terrain normal over the footprint
        public float heightVariation;     // max height - min height across samples
        public float maxSlopeAngle;       // steepest sample normal vs up
        public float contactHeight;       // recommended ground-contact height (mean of samples)
        public bool isValid;              // true when within configured limits
    }

    private struct TerrainFootprintSnapshot
    {
        public Vector3[] positions;
        public float[] heights;
        public Vector3[] normals;
        public bool[] hit;
        public bool allHits;
        public float heightVariation;
        public float maxSlopeAngle;
    }

    private sealed class TerrainSamplingRepresentative
    {
        public int priority;
        public string message;
    }

    [Header("Terrain")]
    [SerializeField] private Collider terrainCollider;
    [SerializeField] private WorldTerrainQueryProvider terrainQueryProvider;

    public void SetTerrainQueryProvider(WorldTerrainQueryProvider provider)
    {
        terrainQueryProvider = provider;
    }

    [Header("Route Generators")]
    [SerializeField] private RiverGenerator riverGenerator;
    [SerializeField] private RailGenerator railGenerator;

    // Multi-route support (TerrainV2). With both arrays empty — the default,
    // and MainBuild's configuration — every code path, every random-number
    // draw and every placement is identical to the single-generator spawner.
    [Tooltip("Extra rivers used in addition to riverGenerator. Leave empty for single-river worlds.")]
    [SerializeField] private RiverGenerator[] additionalRiverGenerators = new RiverGenerator[0];
    [Tooltip("Extra railways used in addition to railGenerator. Leave empty for single-railway worlds.")]
    [SerializeField] private RailGenerator[] additionalRailGenerators = new RailGenerator[0];
    [Tooltip("Reject ordinary land bases whose pivot lies within the rail corridor plus this clearance of any railway. Off by default (MainBuild behaviour unchanged).")]
    [SerializeField] private bool keepLandBasesOffRailways = false;
    [SerializeField, Min(0f)] private float landRailClearance = 8f;

    // Generator selected for the route request currently being evaluated.
    private RiverGenerator activeRiverGenerator;
    private RailGenerator activeRailGenerator;
    private int activeRouteId;

    public void SetAdditionalRouteGenerators(
        RiverGenerator[] rivers,
        RailGenerator[] rails,
        bool keepLandOffRailways)
    {
        additionalRiverGenerators = rivers ?? new RiverGenerator[0];
        additionalRailGenerators = rails ?? new RailGenerator[0];
        keepLandBasesOffRailways = keepLandOffRailways;
    }

    private RiverGenerator CurrentRiver =>
        activeRiverGenerator != null ? activeRiverGenerator : riverGenerator;

    private RailGenerator CurrentRail =>
        activeRailGenerator != null ? activeRailGenerator : railGenerator;

    private List<RiverGenerator> AllRiverGenerators()
    {
        List<RiverGenerator> rivers = new List<RiverGenerator>();
        if (riverGenerator != null) rivers.Add(riverGenerator);
        if (additionalRiverGenerators != null)
        {
            foreach (RiverGenerator river in additionalRiverGenerators)
            {
                if (river != null && !rivers.Contains(river)) rivers.Add(river);
            }
        }
        return rivers;
    }

    private List<RailGenerator> AllRailGenerators()
    {
        List<RailGenerator> rails = new List<RailGenerator>();
        if (railGenerator != null) rails.Add(railGenerator);
        if (additionalRailGenerators != null)
        {
            foreach (RailGenerator rail in additionalRailGenerators)
            {
                if (rail != null && !rails.Contains(rail)) rails.Add(rail);
            }
        }
        return rails;
    }

    private static float RouteLengthXZ(IReadOnlyList<Vector3> route)
    {
        float length = 0f;
        if (route == null) return length;
        for (int i = 1; i < route.Count; i++)
        {
            Vector3 d = route[i] - route[i - 1];
            length += new Vector2(d.x, d.z).magnitude;
        }
        return length;
    }

    /// <summary>
    /// Deterministically assigns each route request a preferred route in
    /// proportion to route length (D'Hondt allocation in request order). No
    /// random numbers are consumed. Single-route families keep routeId 0.
    /// </summary>
    private void AssignPreferredRoutes(List<BaseRequest> requests)
    {
        List<float> riverLengths = AllRiverGenerators()
            .Select(r => RouteLengthXZ(r.GeneratedRoute)).ToList();
        List<float> railLengths = AllRailGenerators()
            .Select(r => RouteLengthXZ(r.GeneratedRoute)).ToList();
        if (riverLengths.Count <= 1 && railLengths.Count <= 1) return;
        int[] riverAssigned = new int[riverLengths.Count];
        int[] railAssigned = new int[railLengths.Count];

        foreach (BaseRequest request in requests)
        {
            if (request.placement == PlacementType.Land) continue;
            bool river = request.placement == PlacementType.River;
            List<float> lengths = river ? riverLengths : railLengths;
            int[] assigned = river ? riverAssigned : railAssigned;
            if (lengths.Count == 0) continue;
            int best = 0;
            float bestQuotient = float.NegativeInfinity;
            for (int r = 0; r < lengths.Count; r++)
            {
                float quotient = lengths[r] / (assigned[r] + 1f);
                if (quotient > bestQuotient)
                {
                    bestQuotient = quotient;
                    best = r;
                }
            }
            assigned[best]++;
            request.preferredRouteId = best;
            request.routeId = best;
        }
    }

    private int RouteChoiceCount(BaseRequest request)
    {
        switch (request.placement)
        {
            case PlacementType.River: return Mathf.Max(1, AllRiverGenerators().Count);
            case PlacementType.Rail: return Mathf.Max(1, AllRailGenerators().Count);
            default: return 1;
        }
    }

    /// <summary>
    /// Route used for a request's choice-th try: its preferred route first,
    /// then the remaining routes in order. Fallback routes get their own
    /// deterministic sampling state; choice 0 keeps the original state, so
    /// the single-route path is untouched.
    /// </summary>
    private void SelectRequestRoute(BaseRequest request, int choice, int choiceCount)
    {
        if (choice == 0) return;
        request.routeId = (request.preferredRouteId + choice) % choiceCount;
        request.routeSamplingState = new RouteIndexSamplingState(
            CreateRouteSamplingSeed(
                seed ^ unchecked((int)(0x5BD1E995u * (uint)choice)),
                request.baseName,
                request.index,
                request.placement));
    }

    [Header("Standalone Hangar Prefabs")]
    [SerializeField] private GameObject navalHangarPrefab;
    [SerializeField] private GameObject railHangarPrefab;

    [Header("Base Prefabs and Counts")]
    [SerializeField] private List<BaseEntry> baseEntries =
        new List<BaseEntry>
    {
        new BaseEntry
        {
            baseName = "Tank",
            count = 110,
            placement = PlacementType.Land
        },
        new BaseEntry
        {
            baseName = "Heli",
            count = 90,
            placement = PlacementType.Land
        },
        new BaseEntry
        {
            baseName = "Air",
            count = 80,
            placement = PlacementType.Land
        },
        new BaseEntry
        {
            baseName = "Naval",
            count = 25,
            placement = PlacementType.River
        },
        new BaseEntry
        {
            baseName = "Rail",
            count = 17,
            placement = PlacementType.Rail
        },
        new BaseEntry
        {
            baseName = "Heli Naval",
            count = 11,
            placement = PlacementType.River
        },
        new BaseEntry
        {
            baseName = "Heli Rail",
            count = 7,
            placement = PlacementType.Rail
        },
        new BaseEntry
        {
            baseName = "Air Naval",
            count = 6,
            placement = PlacementType.River
        },
        new BaseEntry
        {
            baseName = "Air Rail",
            count = 4,
            placement = PlacementType.Rail
        }
    };

    [Header("Battlefield Bounds")]
    [SerializeField] private float mapSizeX = 400f;
    [SerializeField] private float mapSizeZ = 400f;
    [SerializeField] private float edgePadding = 8f;

    [Header("Organic Land Distribution")]
    [SerializeField, Min(0f)] private float minSpacing = 10f;
    [SerializeField, Min(1)] private int clusterCount = 18;
    [SerializeField, Min(0f)] private float clusterRadius = 35f;

    [SerializeField, Range(0f, 1f)]
    private float clusterChance = 0.7f;

    [Header("River Placement")]
    [Tooltip(
        "Minimum distance from the river centreline for ordinary land bases.")]
    [SerializeField, Min(0f)]
    private float landRiverClearance = 15f;

    [Tooltip(
        "Additional bank-side spacing term for the Naval main base and hangar.")]
    [SerializeField, Min(0f)]
    private float navalBankPadClearance = 0.6f;

    [Tooltip(
        "How far the naval hangar bottom may sit below the water " +
        "surface. Higher values keep the circular base closer to the " +
        "terrain; zero puts the hangar bottom exactly at the waterline.")]
    [SerializeField, Min(0f)]
    private float navalWaterlineOffset = 0.05f;

    [Tooltip("Variation along the river route.")]
    [SerializeField, Min(0f)]
    private float riverRouteJitter = 2f;

    [Header("Rail Placement")]
    [Tooltip("Variation along the railway route.")]
    [SerializeField, Min(0f)]
    private float railRouteJitter = 2f;

    [Tooltip(
        "Minimum pivot spacing for bases placed along routes.")]
    [SerializeField, Min(0f)]
    private float routeBaseMinSpacing = 4.5f;

    [Tooltip(
        "Maximum distance between the hangar anchor and its route point " +
        "for a route placement to count as aligned.")]
    [SerializeField, Min(0.01f)]
    private float routeHangarTolerance = 0.35f;

    [Tooltip("Clear gap between the measured rail hangar and main-base bounds.")]
    [SerializeField, Min(0f)]
    private float railBaseHangarClearance = 0.5f;

    [Header("Logging")]
    [Tooltip(
        "Log the route point, hangar position and base position for " +
        "every placed route base.")]
    [SerializeField]
    private bool verboseLogging = true;

    [Header("Terrain Conformance")]
    [Tooltip(
        "Radius around the base pivot used to sample terrain heights " +
        "across the circular footprint.")]
    // Retained for compatibility with the existing isolated validation runner.
    // Placement validation now derives its sample points from renderer bounds.
    [SerializeField, Min(0f)]
    private float terrainSampleRadius = 0.55f;

    [Tooltip(
        "Number of terrain sample points taken around the footprint.")]
    // Retained for compatibility with the existing isolated validation runner.
    [SerializeField, Min(3)]
    private int terrainSampleCount = 8;

    [Tooltip(
        "Maximum allowed height difference (max - min) across the " +
        "sampled footprint. Larger values mean steeper or uneven ground " +
        "and the placement is rejected.")]
    [SerializeField, Min(0f)]
    private float maxFootprintHeightVariation = 0.3f;

    [Tooltip(
        "Maximum tilt (degrees) applied to ordinary land bases to " +
        "conform to terrain. Naval and rail bases are never tilted " +
        "away from their route alignment; bad terrain is rejected instead.")]
    [SerializeField, Range(0f, 90f)]
    private float maxBaseTiltAngle = 20f;

    [Header("Placement")]
    [SerializeField, Min(1f)]
    private float raycastHeight = 200f;

    [SerializeField, Range(0f, 90f)]
    private float maxSlopeAngle = 30f;

    [SerializeField]
    private float surfaceOffset = 0.05f;

    [SerializeField]
    private int seed = 12345;

    /// <summary>
    /// Sets the seed used by the existing base-generation randomization
    /// (Random.InitState and route sampling) for the next GenerateBases()
    /// call. The world generator passes its world seed here so every base
    /// position follows the world seed. Placement logic is unchanged.
    /// </summary>
    public void SetGenerationSeed(int generationSeed)
    {
        seed = generationSeed;
    }

    [Header("Generation")]
    [SerializeField]
    private bool generateOnStart = true;

    [SerializeField, Min(100)]
    private int attemptsPerBase = 2000;

    [Header("Diagnostic Visualisation")]
    [Tooltip(
        "Draw editor-only gizmos showing the last-placed route targets, " +
        "hangar anchors and base roots. Safe to disable.")]
    [SerializeField]
    private bool showDiagnosticGizmos = true;

    private readonly List<Vector3> placedPositions =
        new List<Vector3>();

    private readonly List<Vector3> placedRouteHangarPositions =
        new List<Vector3>();

    private readonly List<PlacedNavalPlacement> placedNavalPlacements =
        new List<PlacedNavalPlacement>();

    private readonly List<int> registeredRiverNarrowingIds =
        new List<int>();

    // Anchor caches keyed by prefab (Unity Object reference works as key).
    private readonly Dictionary<GameObject, PrefabAnchor> navalAnchorCache =
        new Dictionary<GameObject, PrefabAnchor>();

    private readonly Dictionary<GameObject, PrefabAnchor> railAnchorCache =
        new Dictionary<GameObject, PrefabAnchor>();

    private readonly Dictionary<GameObject, PrefabFootprint> baseFootprintCache =
        new Dictionary<GameObject, PrefabFootprint>();

    private readonly HashSet<GameObject> terrainFootprintWarnings =
        new HashSet<GameObject>();

    private readonly Dictionary<string, int> terrainSamplingComparisonCounts =
        new Dictionary<string, int>();

    private readonly Dictionary<string, int> terrainSamplingVariantCounts =
        new Dictionary<string, int>();

    private readonly Dictionary<string, int>
        terrainSamplingAcceptanceDisagreements =
            new Dictionary<string, int>();

    private readonly Dictionary<string, int>
        terrainSamplingRouteIndexDisagreements =
            new Dictionary<string, int>();

    private readonly Dictionary<string, TerrainSamplingRepresentative>
        terrainSamplingRepresentatives =
            new Dictionary<string, TerrainSamplingRepresentative>();

    private int terrainSamplingDiagnosticErrors;

    // Last-placed data for editor gizmos.
    private readonly List<PlacementValidation> lastPlacements =
        new List<PlacementValidation>();

    private readonly Dictionary<string, RouteSamplingAggregate>
        routeSamplingAggregates =
            new Dictionary<string, RouteSamplingAggregate>();

    private Vector2[] clusters;

    /// <summary>
    /// Optional observer used by the isolated world-generation test to record
    /// every rejected placement attempt. With no subscribers, normal spawning
    /// behavior and logging are unchanged.
    /// </summary>
    public event Action<string, string> PlacementRejected;

    private void Start()
    {
        if (generateOnStart)
        {
            GenerateBases();
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!showDiagnosticGizmos || lastPlacements.Count == 0)
        {
            return;
        }

        foreach (var p in lastPlacements)
        {
            if (!p.valid)
            {
                continue;
            }

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(p.routePoint, 0.25f);

            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(p.routePoint, p.hangarWorldPosition);

            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(p.hangarWorldPosition, 0.15f);

            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(p.rootPosition, 0.2f);
        }
    }
#endif

    [ContextMenu("Generate Bases")]
    public void GenerateBases()
    {
        if (!ValidateSetup())
        {
            return;
        }

        // Make sure the river route exists.
        if (riverGenerator != null &&
            (riverGenerator.GeneratedRoute == null ||
             riverGenerator.GeneratedRoute.Count < 2))
        {
            riverGenerator.GenerateRiver();
        }

        // Additional rivers/railways (multi-route worlds only): every
        // configured route must exist before placement starts.
        foreach (RiverGenerator extraRiver in AllRiverGenerators())
        {
            if (extraRiver == riverGenerator) continue;
            if (extraRiver.GeneratedRoute == null || extraRiver.GeneratedRoute.Count < 2)
                extraRiver.GenerateRiver();
            if (extraRiver.GeneratedRoute == null || extraRiver.GeneratedRoute.Count < 2)
            {
                Debug.LogError(
                    $"BaseSpawner: Additional river '{extraRiver.name}' has no route.", this);
                return;
            }
        }
        foreach (RailGenerator extraRail in AllRailGenerators())
        {
            if (extraRail == railGenerator) continue;
            if (extraRail.GeneratedRoute == null || extraRail.GeneratedRoute.Count < 2)
                extraRail.GenerateRail();
            if (extraRail.GeneratedRoute == null || extraRail.GeneratedRoute.Count < 2)
            {
                Debug.LogError(
                    $"BaseSpawner: Additional railway '{extraRail.name}' has no route.", this);
                return;
            }
        }

        if (HasRiverBasesRequested() &&
            (riverGenerator == null ||
             riverGenerator.GeneratedRoute == null ||
             riverGenerator.GeneratedRoute.Count < 2))
        {
            Debug.LogError(
                "BaseSpawner: River bases are requested, but the river " +
                "route is unavailable. Check RiverGenerator's terrain " +
                "and material.",
                this);

            return;
        }

        // Make sure the railway route exists.
        if (railGenerator != null &&
            (railGenerator.GeneratedRoute == null ||
             railGenerator.GeneratedRoute.Count < 2))
        {
            railGenerator.GenerateRail();
        }

        if (HasRailBasesRequested() &&
            (railGenerator == null ||
             railGenerator.GeneratedRoute == null ||
             railGenerator.GeneratedRoute.Count < 2))
        {
            Debug.LogError(
                "BaseSpawner: Rail bases are requested, but the railway " +
                "route is unavailable. Check RailGenerator's terrain " +
                "and materials.",
                this);

            return;
        }

        Random.State previousRandomState = Random.state;
        Random.InitState(seed);

        ClearRegisteredRiverNarrowings();
        ClearGeneratedBases();
        placedPositions.Clear();
        placedRouteHangarPositions.Clear();
        placedNavalPlacements.Clear();
        lastPlacements.Clear();
        terrainSamplingComparisonCounts.Clear();
        terrainSamplingVariantCounts.Clear();
        terrainSamplingAcceptanceDisagreements.Clear();
        terrainSamplingRouteIndexDisagreements.Clear();
        terrainSamplingRepresentatives.Clear();
        terrainSamplingDiagnosticErrors = 0;
        routeSamplingAggregates.Clear();

        CreateClusters();

        List<BaseRequest> requests = BuildRequests();
        AssignPreferredRoutes(requests);   // no-op with a single river and railway
        ShuffleRequests(requests);

        int spawned = 0;
        int failed = 0;

        Dictionary<string, int> spawnedByType =
            new Dictionary<string, int>();

        Dictionary<string, int> validatedByType =
            new Dictionary<string, int>();

        Dictionary<string, int> shortfallByType =
            new Dictionary<string, int>();

        Dictionary<string, int> rejectReasonsTotal =
            new Dictionary<string, int>();

        foreach (BaseRequest request in requests)
        {
            bool success = false;

            Dictionary<string, int> rejectReasons =
                new Dictionary<string, int>();

            // Multi-route worlds: try the preferred route first, then the
            // others. With one route this outer loop runs exactly once.
            int routeChoices = RouteChoiceCount(request);
            for (int routeChoice = 0;
                 routeChoice < routeChoices && !success;
                 routeChoice++)
            {
            SelectRequestRoute(request, routeChoice, routeChoices);
            for (int attempt = 0;
                 attempt < attemptsPerBase;
                 attempt++)
            {
                if (!TryFindPlacement(
                    request,
                    out Pose pose,
                    out PlacementValidation validation))
                {
                    string reason = validation.rejectReason;
                    if (string.IsNullOrEmpty(reason))
                    {
                        reason = "rejected";
                    }

                    NotifyPlacementRejected(request.baseName, reason);

                    rejectReasons[reason] =
                        rejectReasons.TryGetValue(
                            reason,
                            out int reasonCount)
                                ? reasonCount + 1
                                : 1;

                    continue;
                }

                if (!TryInstantiateRequest(
                    request,
                    pose,
                    validation,
                    out string spawnFailure))
                {
                    NotifyPlacementRejected(request.baseName, spawnFailure);
                    rejectReasons[spawnFailure] =
                        rejectReasons.TryGetValue(
                            spawnFailure,
                            out int spawnFailureCount)
                                ? spawnFailureCount + 1
                                : 1;

                    continue;
                }

                placedPositions.Add(pose.position);
                if (request.placement != PlacementType.Land &&
                    validation.valid)
                {
                    placedRouteHangarPositions.Add(
                        validation.hangarWorldPosition);

                    if (request.placement == PlacementType.River)
                    {
                        placedNavalPlacements.Add(new PlacedNavalPlacement
                        {
                            rootPosition = pose.position,
                            baseFootprintCenter =
                                validation.navalBaseFootprintCenter,
                            baseFootprintSize =
                                validation.navalBaseFootprintSize,
                            baseFootprintRotation =
                                validation.navalBaseFootprintRotation,
                            routePoint = validation.routePoint,
                            routeTangent = validation.routeTangent,
                            routeIndex = validation.routeIndex,
                            routeId = validation.routeId,
                            hangarCenter = validation.hangarWorldPosition,
                            hangarFootprintSize =
                                validation.navalHangarFootprintSize,
                            hangarRootRotation =
                                validation.hangarRootRotation
                        });
                    }
                }

                lastPlacements.Add(validation);
                spawned++;

                if (!spawnedByType.ContainsKey(request.baseName))
                {
                    spawnedByType[request.baseName] = 0;
                }

                spawnedByType[request.baseName]++;

                if (validation.valid)
                {
                    validatedByType[request.baseName] =
                        validatedByType.TryGetValue(
                            request.baseName,
                            out int validatedCount)
                                ? validatedCount + 1
                                : 1;
                }

                if (verboseLogging)
                {
                    Debug.Log(
                        $"BaseSpawner: {request.baseName} " +
                        $"#{request.index:000} placed after " +
                        $"{attempt + 1} attempt(s). " +
                        $"Root {pose.position:F2}, rotY {pose.rotation.eulerAngles.y:F1}, " +
                        $"route {validation.routePoint:F2} (idx {validation.routeIndex}), " +
                        $"hangar {validation.hangarWorldPosition:F2}, " +
                        $"dist {validation.hangarRouteDistance:F2} m" +
                        (request.placement != PlacementType.Land
                            ? $", hangar scale {validation.hangarRootScale:F2}x"
                            : string.Empty) +
                        (request.placement == PlacementType.River
                            ? $", river width {validation.localRiverWidth:F2} m"
                            : string.Empty) +
                        (request.placement == PlacementType.River
                            ? $", hangar bounds over water {validation.hangarWaterOverlapFraction:P0}"
                            : string.Empty) +
                        (validation.valid ? ", validated." : ", NOT validated."),
                        this);
                }

                success = true;
                break;
            }
            } // route choices

            if (!success)
            {
                failed++;
                string routeSamplingSummary =
                    RecordFailedRouteSampling(request);

                foreach (var kvp in rejectReasons)
                {
                    rejectReasonsTotal[kvp.Key] =
                        rejectReasonsTotal.TryGetValue(
                            kvp.Key,
                            out int totalCount)
                                ? totalCount + kvp.Value
                                : kvp.Value;
                }

                string reasons = string.Join(
                    ", ",
                    rejectReasons.Select(
                        pair => $"{pair.Key} x{pair.Value}"));

                Debug.LogWarning(
                    $"BaseSpawner: Could not place {request.baseName} " +
                    $"base #{request.index} after {attemptsPerBase} " +
                    "attempts. No random-land fallback was used. " +
                    $"Rejection reasons: {reasons}" +
                    routeSamplingSummary,
                    this);
            }
        }

        Random.state = previousRandomState;
        LogTerrainSamplingComparisonSummary();

        string summary = "";

        foreach (BaseEntry entry in baseEntries)
        {
            int actual = spawnedByType.TryGetValue(
                entry.baseName,
                out int value) ? value : 0;

            int requested = entry.count;
            int validated = validatedByType.TryGetValue(
                entry.baseName,
                out int validatedCount) ? validatedCount : 0;

            summary +=
                $"{entry.baseName}: {actual}/{requested}";

            if (entry.placement != PlacementType.Land)
            {
                summary += $" (validated {validated})";

                if (validated < requested)
                {
                    int shortfall = requested - validated;
                    shortfallByType[entry.baseName] = shortfall;
                    summary += $" SHORTFALL {shortfall}";
                }
            }

            summary += ", ";
        }

        if (summary.EndsWith(", "))
            summary = summary.Substring(0, summary.Length - 2);

        // Report only bases that were actually instantiated. (A former
        // "fallback" block added up to 10 Heli and 10 Air bases to a second
        // summary line without creating them; it has been removed.)
        Debug.Log(
            $"IRONVEIL: Spawned {spawned}/{requests.Count} requested " +
            $"bases. Failed placements: {failed}. {summary}",

            this);
    }

    private bool ValidateSetup()
    {
        if (terrainCollider == null &&
            (terrainQueryProvider == null || !terrainQueryProvider.IsReady))
        {
            Debug.LogError(
                "BaseSpawner: Assign a terrain collider or generate a registered multi-terrain query provider.",
                this);

            return false;
        }

        if (clusterCount <= 0 ||
            mapSizeX <= edgePadding * 2f ||
            mapSizeZ <= edgePadding * 2f ||
            attemptsPerBase <= 0)
        {
            Debug.LogError(
                "BaseSpawner: Check battlefield bounds, cluster count " +
                "and attempts.",
                this);

            return false;
        }

        HashSet<string> names = new HashSet<string>();

        foreach (BaseEntry entry in baseEntries)
        {
            if (entry == null ||
                string.IsNullOrWhiteSpace(entry.baseName))
            {
                Debug.LogError(
                    "BaseSpawner: Every entry must have a name.",
                    this);

                return false;
            }

            if (!names.Add(entry.baseName))
            {
                Debug.LogError(
                    $"BaseSpawner: Duplicate entry name " +
                    $"'{entry.baseName}'.",
                    this);

                return false;
            }

            if (entry.count < 0)
            {
                Debug.LogError(
                    $"BaseSpawner: Negative count for {entry.baseName}.",
                    this);

                return false;
            }

            if (entry.count > 0 && entry.prefab == null)
            {
                Debug.LogError(
                    $"BaseSpawner: Assign the prefab for " +
                    $"{entry.baseName}.",
                    this);

                return false;
            }
        }

        if (HasRiverBasesRequested() && navalHangarPrefab == null)
        {
            Debug.LogError(
                "BaseSpawner: Assign the standalone Naval Hangar prefab.",
                this);

            return false;
        }

        if (HasRailBasesRequested() && railHangarPrefab == null)
        {
            Debug.LogError(
                "BaseSpawner: Assign the standalone Rail Hangar prefab.",
                this);

            return false;
        }

        return true;
    }

    private void NotifyPlacementRejected(string baseName, string reason)
    {
        Action<string, string> observers = PlacementRejected;
        if (observers == null)
        {
            return;
        }

        try
        {
            observers(baseName, reason);
        }
        catch (Exception exception)
        {
            // Diagnostic observers must not interrupt or change placement.
            Debug.LogException(exception, this);
        }
    }

    private bool HasRiverBasesRequested()
    {
        foreach (BaseEntry entry in baseEntries)
        {
            if (entry.placement == PlacementType.River &&
                entry.count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasRailBasesRequested()
    {
        foreach (BaseEntry entry in baseEntries)
        {
            if (entry.placement == PlacementType.Rail &&
                entry.count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void ClearRegisteredRiverNarrowings()
    {
        if (riverGenerator != null && registeredRiverNarrowingIds.Count > 0)
        {
            riverGenerator.RemoveLocalNarrowings(
                registeredRiverNarrowingIds);
        }

        registeredRiverNarrowingIds.Clear();
    }

    private void ClearGeneratedBases()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);

            if (!child.name.StartsWith("GeneratedBase_"))
            {
                continue;
            }

            if (Application.isPlaying)
            {
                Destroy(child.gameObject);
            }
            else
            {
                DestroyImmediate(child.gameObject);
            }
        }
    }

    private void CreateClusters()
    {
        clusters = new Vector2[clusterCount];

        for (int i = 0; i < clusterCount; i++)
        {
            clusters[i] = new Vector2(
                Random.Range(
                    -mapSizeX / 2f + edgePadding,
                    mapSizeX / 2f - edgePadding),

                Random.Range(
                    -mapSizeZ / 2f + edgePadding,
                    mapSizeZ / 2f - edgePadding));
        }
    }

    private List<BaseRequest> BuildRequests()
    {
        List<BaseRequest> requests =
            new List<BaseRequest>();

        foreach (BaseEntry entry in baseEntries)
        {
            for (int i = 1; i <= entry.count; i++)
            {
                requests.Add(new BaseRequest
                {
                    baseName = entry.baseName,
                    prefab = entry.prefab,
                    placement = entry.placement,
                    index = i,
                    routeSamplingState = entry.placement == PlacementType.Land
                        ? null
                        : new RouteIndexSamplingState(
                            CreateRouteSamplingSeed(
                                seed,
                                entry.baseName,
                                i,
                                entry.placement))
                });
            }
        }

        return requests;
    }

    private static uint CreateRouteSamplingSeed(
        int generationSeed,
        string baseName,
        int requestIndex,
        PlacementType placement)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)generationSeed) * 16777619u;
            hash = (hash ^ (uint)placement) * 16777619u;
            hash = (hash ^ (uint)requestIndex) * 16777619u;

            if (!string.IsNullOrEmpty(baseName))
            {
                for (int i = 0; i < baseName.Length; i++)
                {
                    hash = (hash ^ baseName[i]) * 16777619u;
                }
            }

            return hash == 0u ? 0x9E3779B9u : hash;
        }
    }

    private void ShuffleRequests(List<BaseRequest> requests)
    {
        for (int i = requests.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);

            BaseRequest temp = requests[i];
            requests[i] = requests[j];
            requests[j] = temp;
        }
    }

    private bool TryFindPlacement(
        BaseRequest request,
        out Pose pose,
        out PlacementValidation validation)
    {
        pose = default(Pose);
        validation = default(PlacementValidation);
        validation.rejectReason = string.Empty;

        switch (request.placement)
        {
            case PlacementType.River:
            {
                // routeId is 0 on the single-generator path, so this selects
                // riverGenerator exactly as before.
                List<RiverGenerator> rivers = AllRiverGenerators();
                activeRiverGenerator = request.routeId < rivers.Count
                    ? rivers[request.routeId]
                    : riverGenerator;
                activeRouteId = request.routeId;
                try
                {
                    bool found = TryFindRiverPlacement(
                        request.baseName,
                        request.index,
                        request.routeSamplingState,
                        request.prefab,
                        navalHangarPrefab,
                        CurrentRiver.GeneratedRoute,
                        out pose,
                        out validation);
                    validation.routeId = request.routeId;
                    return found;
                }
                finally
                {
                    activeRiverGenerator = null;
                    activeRouteId = 0;
                }
            }

            case PlacementType.Rail:
            {
                List<RailGenerator> rails = AllRailGenerators();
                activeRailGenerator = request.routeId < rails.Count
                    ? rails[request.routeId]
                    : railGenerator;
                activeRouteId = request.routeId;
                try
                {
                    bool found = TryFindRailPlacement(
                        request.baseName,
                        request.index,
                        request.routeSamplingState,
                        request.prefab,
                        railHangarPrefab,
                        CurrentRail.GeneratedRoute,
                        out pose,
                        out validation);
                    validation.routeId = request.routeId;
                    return found;
                }
                finally
                {
                    activeRailGenerator = null;
                    activeRouteId = 0;
                }
            }

            default:
                validation = default(PlacementValidation);
                validation.rejectReason = string.Empty;

                if (!TryFindLandPlacement(
                    request.baseName,
                    request.index,
                    request.prefab,
                    out Vector3 landPosition,
                    out Quaternion landRotation,
                    out string landRejectReason))
                {
                    validation.rejectReason = landRejectReason;
                    pose = default(Pose);
                    return false;
                }

                pose = new Pose(landPosition, landRotation);
                return true;
        }
    }

    private bool TryInstantiateRequest(
        BaseRequest request,
        Pose basePose,
        PlacementValidation validation,
        out string rejectReason)
    {
        rejectReason = string.Empty;

        string generatedName =
            $"GeneratedBase_{request.baseName}_{request.index:000}";

        bool isRoutePair = request.placement != PlacementType.Land;
        GameObject generatedRoot = null;
        GameObject baseInstance = null;
        GameObject hangarInstance = null;

        try
        {
            Transform parent = transform;
            if (isRoutePair)
            {
                generatedRoot = new GameObject(generatedName);
                generatedRoot.transform.SetParent(transform, false);
                generatedRoot.transform.localPosition = Vector3.zero;
                generatedRoot.transform.localRotation = Quaternion.identity;
                generatedRoot.transform.localScale = Vector3.one;
                parent = generatedRoot.transform;
            }

            baseInstance = Instantiate(
                request.prefab,
                basePose.position,
                basePose.rotation,
                parent);

            if (baseInstance == null)
            {
                rejectReason = "main base instantiation failed";
                DestroyTemporary(generatedRoot);
                return false;
            }

            baseInstance.name = isRoutePair
                ? $"{request.baseName} Main Base"
                : generatedName;

            if (isRoutePair)
            {
                GameObject hangarPrefab = request.placement == PlacementType.River
                    ? navalHangarPrefab
                    : railHangarPrefab;

                hangarInstance = Instantiate(
                    hangarPrefab,
                    validation.hangarRootPosition,
                    validation.hangarRootRotation,
                    parent);

                if (hangarInstance == null)
                {
                    rejectReason = "hangar instantiation failed";
                    DestroyTemporary(generatedRoot);
                    return false;
                }

                float hangarScale = validation.hangarRootScale > 0f
                    ? validation.hangarRootScale
                    : 1f;
                hangarInstance.transform.localScale =
                    Vector3.one * hangarScale;

                hangarInstance.name = request.placement == PlacementType.River
                    ? "Naval Hangar"
                    : "Rail Hangar";
                ReverseHangarEntranceAroundVisibleCenter(hangarInstance);
            }

            BaseController[] controllers =
                baseInstance.GetComponentsInChildren<BaseController>(true);

            if (controllers.Length > 1 ||
                (controllers.Length == 1 &&
                 controllers[0].gameObject != baseInstance))
            {
                rejectReason = "main base must have one root BaseController";
                DestroyTemporary(generatedRoot != null
                    ? generatedRoot
                    : baseInstance);
                return false;
            }

            BaseController controller = controllers.Length == 1
                ? controllers[0]
                : baseInstance.AddComponent<BaseController>();

            if (hangarInstance != null &&
                hangarInstance.GetComponentsInChildren<BaseController>(true).Length > 0)
            {
                rejectReason = "hangar prefab contains a BaseController";
                DestroyTemporary(generatedRoot);
                return false;
            }

            controller.InitializeIdentity(
                BuildBaseId(request),
                $"{request.baseName} Base");

            return true;
        }
        catch (Exception exception)
        {
            rejectReason = "instantiation failed";

            DestroyTemporary(generatedRoot != null
                ? generatedRoot
                : baseInstance);

            Debug.LogWarning(
                $"BaseSpawner: Could not instantiate {request.baseName} " +
                $"base #{request.index:000}: {exception.Message}",
                this);

            return false;
        }
    }

    private static void ReverseHangarEntranceAroundVisibleCenter(
        GameObject hangarRoot)
    {
        if (hangarRoot == null)
        {
            return;
        }

        Renderer[] renderers =
            hangarRoot.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            hangarRoot.transform.Rotate(
                0f,
                180f,
                0f,
                Space.Self);
            return;
        }

        Bounds visibleBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            visibleBounds.Encapsulate(renderers[i].bounds);
        }

        Vector3 visibleCenter = visibleBounds.center;
        hangarRoot.transform.Rotate(
            0f,
            180f,
            0f,
            Space.Self);

        renderers = hangarRoot.GetComponentsInChildren<Renderer>(true);
        visibleBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            visibleBounds.Encapsulate(renderers[i].bounds);
        }

        hangarRoot.transform.position += visibleCenter - visibleBounds.center;
    }

    private static string BuildBaseId(BaseRequest request)
    {
        string typeId = new string(
            request.baseName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        return $"BASE_{typeId}_{request.index:000}";
    }

    private bool TryFindLandPlacement(
        string variant,
        int requestIndex,
        GameObject prefab,
        out Vector3 position,
        out Quaternion rotation,
        out string rejectReason)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        rejectReason = string.Empty;

        float x;
        float z;

        if (Random.value < clusterChance)
        {
            Vector2 center =
                clusters[Random.Range(0, clusters.Length)];

            Vector2 offset =
                Random.insideUnitCircle * clusterRadius;

            x = center.x + offset.x;
            z = center.y + offset.y;
        }
        else
        {
            x = Random.Range(
                -mapSizeX / 2f + edgePadding,
                mapSizeX / 2f - edgePadding);

            z = Random.Range(
                -mapSizeZ / 2f + edgePadding,
                mapSizeZ / 2f - edgePadding);
        }

        if (!InsideBounds(x, z))
        {
            rejectReason = "outside world bounds";
            return false;
        }

        Vector3 worldXZ = new Vector3(
            transform.position.x + x,
            0f,
            transform.position.z + z);

        // Keep ordinary land bases away from every river (with a single
        // river this is exactly the original riverGenerator check).
        foreach (RiverGenerator river in AllRiverGenerators())
        {
            if (RouteDistanceXZ(
                    river.GeneratedRoute,
                    worldXZ) <
                river.RiverWidth * 0.5f +
                landRiverClearance)
            {
                rejectReason = "too close to river";
                return false;
            }
        }

        // Optional (off by default, enabled by multi-route worlds): keep
        // ordinary land bases off every railway track.
        if (keepLandBasesOffRailways)
        {
            foreach (RailGenerator rail in AllRailGenerators())
            {
                if (RouteDistanceXZ(rail.GeneratedRoute, worldXZ) <
                    rail.RailCorridorHalfWidth + landRailClearance)
                {
                    rejectReason = "too close to railway";
                    return false;
                }
            }
        }

        Random.State randomStateBeforeFootprintLookup = Random.state;
        bool hasFootprint = TryGetPrefabFootprint(
            prefab,
            out PrefabFootprint footprint);
        Random.state = randomStateBeforeFootprintLookup;

        if (!hasFootprint ||
            !HasMeasurableHorizontalFootprint(
                footprint.boundsCenter,
                footprint.boundsSize))
        {
            ReportTerrainFootprintProblem(
                prefab,
                "no measurable renderer footprint for land placement");
            rejectReason = "no measurable renderer footprint for land placement";
            return false;
        }

        // Sample with the candidate's actual renderer bounds and rotation.
        // The terrain-aligned rotation depends on sampled normals, so iterate
        // until the samples and resulting transform agree.
        Random.State randomStateBeforeYaw = Random.state;
        float randomYaw = Random.Range(0f, 360f);
        Quaternion authoredRotation =
            prefab.transform.rotation * Quaternion.Euler(0f, randomYaw, 0f);
        rotation = authoredRotation;
        TerrainSampleResult sample = default(TerrainSampleResult);
        TerrainFootprintSnapshot snapshot = default(TerrainFootprintSnapshot);
        Vector3 footprintCenter = Vector3.zero;
        bool alignmentConverged = false;

        for (int i = 0; i < TerrainAlignmentSampleIterations; i++)
        {
            footprintCenter = worldXZ + rotation * footprint.boundsCenter;
            if (!SampleTerrainFootprintFromBounds(
                worldXZ,
                footprintCenter,
                footprint.boundsSize,
                rotation,
                out sample,
                out snapshot))
            {
                rejectReason = "no terrain hit";
                RecordTerrainSamplingComparison(
                    variant,
                    "main base",
                    requestIndex,
                    -1,
                    worldXZ,
                    snapshot);
                Random.state = randomStateBeforeYaw;
                return false;
            }

            Quaternion terrainAlign =
                Quaternion.FromToRotation(Vector3.up, sample.normal);
            float tilt = Quaternion.Angle(Quaternion.identity, terrainAlign);
            if (tilt > maxBaseTiltAngle && tilt > 0.0001f)
            {
                terrainAlign = Quaternion.Slerp(
                    Quaternion.identity,
                    terrainAlign,
                    maxBaseTiltAngle / tilt);
            }

            Quaternion alignedRotation = terrainAlign * authoredRotation;
            if (Quaternion.Angle(rotation, alignedRotation) <=
                TerrainAlignmentSampleTolerance)
            {
                alignmentConverged = true;
                break;
            }

            rotation = alignedRotation;
        }

        if (!alignmentConverged)
        {
            rejectReason = "terrain-aligned renderer-footprint sampling did not converge";
            ReportTerrainFootprintProblem(
                prefab,
                "terrain-aligned renderer-footprint sampling did not converge",
                handledRejection: true);
            RecordTerrainSamplingComparison(
                variant,
                "main base",
                requestIndex,
                -1,
                worldXZ,
                snapshot);
            Random.state = randomStateBeforeYaw;
            return false;
        }

        RecordTerrainSamplingComparison(
            variant,
            "main base",
            requestIndex,
            -1,
            worldXZ,
            snapshot);

        if (sample.heightVariation > maxFootprintHeightVariation)
        {
            rejectReason =
                $"footprint too uneven ({sample.heightVariation:F2} m)";
            Random.state = randomStateBeforeYaw;
            return false;
        }

        if (sample.maxSlopeAngle > maxSlopeAngle)
        {
            rejectReason = $"slope too steep ({sample.maxSlopeAngle:F1} deg)";
            Random.state = randomStateBeforeYaw;
            return false;
        }

        if (IsTooClose(sample.position, minSpacing))
        {
            rejectReason = "too close to another base";
            Random.state = randomStateBeforeYaw;
            return false;
        }

        position = sample.position + sample.normal * surfaceOffset;

        // Keep the terrain-aligned rotation and XZ; replace the lifted height
        // with one seated on the platform's actual underside.
        if (TrySeatOnFootprint(position, rotation, footprint, out float seatedY))
        {
            position.y = seatedY;
        }

        return true;
    }

    private static bool TryGetRouteSample(
        IReadOnlyList<Vector3> route,
        RouteIndexSamplingState routeSamplingState,
        out int index,
        out Vector3 routePoint,
        out Vector3 tangent,
        out string rejectReason)
    {
        index = 0;
        routePoint = Vector3.zero;
        tangent = Vector3.zero;
        rejectReason = string.Empty;

        if (route == null || route.Count < 3)
        {
            rejectReason = "route unavailable";
            return false;
        }

        if (routeSamplingState == null)
        {
            rejectReason = "route sampling state unavailable";
            return false;
        }

        // Preserve the legacy global Random draw so downstream bank, jitter,
        // and land-placement draws retain their seeded stream position. The
        // per-request sampler uses it as a randomized preference while
        // guaranteeing shuffled coverage before final-index repeats.
        int randomizedPreferredIndex = Random.Range(1, route.Count - 1);
        if (!routeSamplingState.TryGetRawIndex(
            route.Count,
            randomizedPreferredIndex,
            out index))
        {
            rejectReason = "route sampling state unavailable";
            return false;
        }

        routePoint = route[index];

        tangent = route[index + 1] - route[index - 1];
        tangent.y = 0f;

        if (tangent.sqrMagnitude < 0.0001f)
        {
            rejectReason = "degenerate route tangent";
            return false;
        }

        tangent.Normalize();

        return true;
    }

    // ------------------------------------------------------------------
    // Rail placement – deterministic anchor-based
    // ------------------------------------------------------------------

    private bool TryFindRailPlacement(
        string variant,
        int requestIndex,
        RouteIndexSamplingState routeSamplingState,
        GameObject basePrefab,
        GameObject hangarPrefab,
        IReadOnlyList<Vector3> route,
        out Pose pose,
        out PlacementValidation validation)
    {
        pose = default(Pose);
        validation = default(PlacementValidation);
        validation.rejectReason = string.Empty;

        if (!TryGetRouteSample(
            route,
            routeSamplingState,
            out int index,
            out Vector3 routePoint,
            out Vector3 tangent,
            out string rejectReason))
        {
            if (rejectReason == "degenerate route tangent" &&
                routeSamplingState != null)
            {
                routeSamplingState.CommitCandidateIndex(
                    route.Count,
                    index,
                    index);
            }

            validation.rejectReason = rejectReason;
            return false;
        }

        index = routeSamplingState.CommitCandidateIndex(
            route.Count,
            index,
            index);
        routePoint = route[index];
        tangent = route[index + 1] - route[index - 1];
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.0001f)
        {
            validation.rejectReason = "degenerate route tangent";
            return false;
        }
        tangent.Normalize();

        if (!TryGetPrefabAnchor(
            hangarPrefab,
            "Rail Hangar",
            false,
            out PrefabAnchor anchor))
        {
            validation.rejectReason = "missing Rail Hangar model child";
            return false;
        }

        if (!anchor.hasRenderer ||
            !HasMeasurableHorizontalFootprint(
                anchor.boundsCenter,
                anchor.boundsSize))
        {
            validation.rejectReason =
                "Rail Hangar has no measurable horizontal renderer footprint";
            return false;
        }

        if (!TryGetPrefabFootprint(
            basePrefab,
            out PrefabFootprint baseFootprint))
        {
            validation.rejectReason = "main base has no measurable renderer bounds";
            return false;
        }

        if (!baseFootprint.hasRenderer ||
            !HasMeasurableHorizontalFootprint(
                baseFootprint.boundsCenter,
                baseFootprint.boundsSize))
        {
            validation.rejectReason =
                "main base has no measurable horizontal renderer footprint";
            return false;
        }

        if (!baseFootprint.hasTurretFacing)
        {
            validation.rejectReason = "main base has no measurable turret-facing side";
            return false;
        }

        // Align the hangar's track axis (measured from the prefab) with the
        // route tangent so that the track passes through the hangar's
        // intended corridor. Rotation is around Y only so the hangar stays
        // upright.
        Vector3 trackAxisXZ = new Vector3(
            anchor.trackAxisLocal.x, 0f, anchor.trackAxisLocal.z);
        if (trackAxisXZ.sqrMagnitude < 0.0001f)
        {
            trackAxisXZ = Vector3.forward;
        }

        // A track axis is undirected; choose the closer of its two signs to
        // avoid unnecessary 180-degree flips of the hangar and base.
        if (Vector3.Dot(trackAxisXZ, tangent) < 0f)
        {
            trackAxisXZ = -trackAxisXZ;
        }

        float yaw = Vector3.SignedAngle(trackAxisXZ, tangent, Vector3.up);
        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);

        // Center the visible hangar geometry, not its imported FBX pivot, on
        // the rail route. The model keeps its authored scale for track gauge.
        Vector3 hangarRootPosition =
            routePoint - rotation * anchor.boundsCenter;
        hangarRootPosition.y += surfaceOffset;
        Vector3 hangarWorld =
            hangarRootPosition + rotation * anchor.boundsCenter;
        Quaternion hangarWorldRotation = rotation * anchor.localRotation;

        float corridorHalfWidth = CurrentRail != null
            ? CurrentRail.RailCorridorHalfWidth
            : 0f;

        Vector3 acrossTrack = Vector3.Cross(Vector3.up, tangent).normalized;
        Vector3 bankSide = Random.value < 0.5f
            ? acrossTrack
            : -acrossTrack;

        Vector3 towardHangar = -bankSide;
        float baseYaw = Vector3.SignedAngle(
            baseFootprint.turretFacingLocal,
            towardHangar,
            Vector3.up);
        Quaternion baseRotation = Quaternion.Euler(0f, baseYaw, 0f);

        float hangarHalfAcrossTrack =
            ProjectedHalfExtent(anchor.boundsSize, rotation, acrossTrack);
        float baseHalfAcrossTrack =
            ProjectedHalfExtent(baseFootprint.boundsSize, baseRotation, acrossTrack);

        // Place the main base beside the measured hangar footprint, with a
        // deliberate gap, rather than at a fixed distance from the tracks.
        float baseCenterDistance =
            hangarHalfAcrossTrack + baseHalfAcrossTrack +
            railBaseHangarClearance;
        Vector3 baseGeometryCenter =
            routePoint + bankSide * baseCenterDistance;
        baseGeometryCenter.y = 0f;
        Vector3 baseRootXZ =
            baseGeometryCenter - baseRotation * baseFootprint.boundsCenter;
        baseRootXZ.y = 0f;

        if (!InsideWorldBounds(baseGeometryCenter) ||
            !InsideWorldBounds(baseRootXZ))
        {
            validation.rejectReason = "outside world bounds";
            return false;
        }

        // The rail route is terrain-projected by RailGenerator. Re-sample the
        // hangar footprint as well as the adjacent base so route validation
        // rejects missing or excessively uneven support beneath the hangar.
        bool hangarSampled = SampleTerrainFootprintFromBounds(
            routePoint,
            routePoint,
            anchor.boundsSize,
            rotation,
            out TerrainSampleResult hangarTerrain,
            out TerrainFootprintSnapshot hangarSnapshot);
        RecordTerrainSamplingComparison(
            variant,
            "Rail hangar",
            requestIndex,
            index,
            routePoint,
            hangarSnapshot);
        if (!hangarSampled)
        {
            validation.rejectReason = "no terrain hit under rail hangar";
            return false;
        }

        if (hangarTerrain.heightVariation > maxFootprintHeightVariation)
        {
            validation.rejectReason =
                $"hangar footprint too uneven ({hangarTerrain.heightVariation:F2} m)";
            return false;
        }

        if (hangarTerrain.maxSlopeAngle > maxSlopeAngle)
        {
            validation.rejectReason =
                $"hangar route slope too steep ({hangarTerrain.maxSlopeAngle:F1} deg)";
            return false;
        }

        // Sample the main-base footprint on adjacent land. Rail bases are
        // never tilted: bad terrain is rejected and another route point is tried.
        bool baseSampled = SampleTerrainFootprintFromBounds(
            baseGeometryCenter,
            baseGeometryCenter,
            baseFootprint.boundsSize,
            baseRotation,
            out TerrainSampleResult sample,
            out TerrainFootprintSnapshot baseSnapshot);
        RecordTerrainSamplingComparison(
            variant,
            "main base",
            requestIndex,
            index,
            baseGeometryCenter,
            baseSnapshot);
        if (!baseSampled)
        {
            validation.rejectReason = "no terrain hit";
            return false;
        }

        if (sample.heightVariation > maxFootprintHeightVariation)
        {
            validation.rejectReason = $"footprint too uneven ({sample.heightVariation:F2} m)";
            return false;
        }

        if (sample.maxSlopeAngle > maxSlopeAngle)
        {
            validation.rejectReason = $"slope too steep ({sample.maxSlopeAngle:F1} deg)";
            return false;
        }

        // Keep the base level (track alignment takes priority), place its
        // measured bounds on the sampled terrain, and retain the authored pivot.
        Vector3 position =
            sample.position - baseRotation * baseFootprint.boundsCenter;
        position.y = sample.contactHeight + surfaceOffset -
            baseFootprint.boundsCenter.y + baseFootprint.boundsSize.y * 0.5f;
        if (TrySeatOnFootprint(position, baseRotation, baseFootprint, out float seatedY))
        {
            position.y = seatedY;
        }

        // Route bases use their own spacing allowance so that the
        // land-base spacing does not reject too many route placements.
        if (IsTooClose(position, routeBaseMinSpacing))
        {
            validation.rejectReason = "too close to another base";
            return false;
        }

        float hangarDistance = DistanceXZ(hangarWorld, routePoint);
        float rootDistance = DistanceXZ(sample.position, routePoint);

        if (IsTooCloseToRouteHangar(hangarWorld, routeBaseMinSpacing))
        {
            validation.rejectReason = "too close to another route hangar";
            return false;
        }

        // The hangar must be centred on the track (within tolerance).
        bool hangarOnTrack = hangarDistance <= routeHangarTolerance;

        // The sampled circular base footprint must be outside the rail corridor.
        bool baseBesideTrack =
            rootDistance - baseHalfAcrossTrack >= corridorHalfWidth + 0.05f;

        // Validate that the hangar is wide enough across-track to cover both
        // rails. Project the rotated bounds onto the across-track axis.
        float railGauge = CurrentRail != null ? CurrentRail.RailGauge : 0f;
        bool hangarCoversRails =
            hangarHalfAcrossTrack >= railGauge * 0.5f + 0.01f;

        if (!hangarOnTrack)
        {
            validation.rejectReason = $"hangar not on track ({hangarDistance:F2} m from centreline)";
            return false;
        }

        if (!baseBesideTrack)
        {
            validation.rejectReason = $"base inside rail corridor ({rootDistance:F2} m from centreline)";
            return false;
        }

        if (!hangarCoversRails)
        {
            validation.rejectReason = $"hangar too narrow for rails ({hangarHalfAcrossTrack:F2} m half-width)";
            return false;
        }

        validation.valid = true;
        validation.hangarOnTrack = hangarOnTrack;
        validation.baseBesideTrack = baseBesideTrack;
        validation.hangarCoversRails = hangarCoversRails;
        validation.routeIndex = index;
        validation.routePoint = routePoint;
        validation.routeTangent = tangent;
        validation.hangarWorldPosition = hangarWorld;
        validation.hangarWorldRotation = hangarWorldRotation;
        validation.hangarRootPosition = hangarRootPosition;
        validation.hangarRootRotation = rotation;
        validation.hangarRootScale = 1f;
        validation.hangarRouteDistance = hangarDistance;
        validation.rootRouteDistance = rootDistance;
        validation.rootPosition = position;
        validation.rootRotation = baseRotation;
        validation.footprintHeightVariation = sample.heightVariation;
        validation.footprintMaxSlope = sample.maxSlopeAngle;

        pose = new Pose(position, baseRotation);

        return true;
    }

    // ------------------------------------------------------------------
    // River placement – deterministic anchor-based
    // ------------------------------------------------------------------

    private bool TryFindRiverPlacement(
        string variant,
        int requestIndex,
        RouteIndexSamplingState routeSamplingState,
        GameObject basePrefab,
        GameObject hangarPrefab,
        IReadOnlyList<Vector3> route,
        out Pose pose,
        out PlacementValidation validation)
    {
        pose = default(Pose);
        validation = default(PlacementValidation);
        validation.rejectReason = string.Empty;

        if (!TryGetRouteSample(
            route,
            routeSamplingState,
            out int index,
            out Vector3 routePoint,
            out Vector3 tangent,
            out string rejectReason))
        {
            if (rejectReason == "degenerate route tangent" &&
                routeSamplingState != null)
            {
                routeSamplingState.CommitCandidateIndex(
                    route.Count,
                    index,
                    index);
            }

            validation.rejectReason = rejectReason;
            return false;
        }

        if (!TryGetPrefabAnchor(
            hangarPrefab,
            "Naval Hangar",
            true,
            out PrefabAnchor anchor))
        {
            routeSamplingState.CommitCandidateIndex(
                route.Count,
                index,
                index);
            validation.rejectReason = "missing Naval Hangar model child";
            return false;
        }

        if (!anchor.hasRenderer ||
            !HasMeasurableHorizontalFootprint(
                anchor.boundsCenter,
                anchor.boundsSize))
        {
            routeSamplingState.CommitCandidateIndex(
                route.Count,
                index,
                index);
            validation.rejectReason =
                "Naval Hangar has no measurable horizontal renderer footprint";
            return false;
        }

        if (!TryGetPrefabFootprint(
            basePrefab,
            out PrefabFootprint baseFootprint))
        {
            routeSamplingState.CommitCandidateIndex(
                route.Count,
                index,
                index);
            validation.rejectReason = "main base has no measurable renderer bounds";
            return false;
        }

        if (!baseFootprint.hasRenderer ||
            !HasMeasurableHorizontalFootprint(
                baseFootprint.boundsCenter,
                baseFootprint.boundsSize))
        {
            routeSamplingState.CommitCandidateIndex(
                route.Count,
                index,
                index);
            validation.rejectReason =
                "main base has no measurable horizontal renderer footprint";
            return false;
        }

        if (!baseFootprint.hasTurretFacing)
        {
            routeSamplingState.CommitCandidateIndex(
                route.Count,
                index,
                index);
            validation.rejectReason = "main base has no measurable turret-facing side";
            return false;
        }

        // Pick which bank the main base sits on. The base faces the water;
        // the hangar's measured long axis is aligned across the channel.
        bool usePositiveBankSide = Random.value < 0.5f;

        float riverWidth = CurrentRiver != null
            ? CurrentRiver.RiverWidth
            : 6f;
        if (riverWidth <= 0.01f)
        {
            routeSamplingState.CommitCandidateIndex(
                route.Count,
                index,
                index);
            validation.rejectReason = "Naval Hangar requires a measurable river width";
            return false;
        }

        float localRiverWidth = riverWidth;

        // Preserve seeded along-route variation by snapping jitter to a
        // nearby route sample instead of offsetting away from the river mesh.
        float routeStep = Vector3.Distance(
            route[index - 1],
            route[index + 1]) * 0.5f;
        int jitterSteps = Mathf.RoundToInt(
            Random.Range(-riverRouteJitter, riverRouteJitter) /
            Mathf.Max(routeStep, 0.01f));
        int jitteredIndex =
            Mathf.Clamp(index + jitterSteps, 1, route.Count - 2);
        index = routeSamplingState.CommitCandidateIndex(
            route.Count,
            index,
            jitteredIndex);
        routePoint = route[index];
        tangent = route[index + 1] - route[index - 1];
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.0001f)
        {
            validation.rejectReason = "degenerate route tangent";
            return false;
        }
        tangent.Normalize();

        Vector3 side =
            Vector3.Cross(Vector3.up, tangent).normalized;
        Vector3 bankSide = usePositiveBankSide ? side : -side;

        float riverHalfWidth = localRiverWidth * 0.5f;

        // Face the base toward the water. The hangar uses its measured long
        // local axis, which is not the prefab's local +Z axis.
        Vector3 waterDirXZ = -bankSide;
        waterDirXZ.y = 0f;
        float baseYaw = Vector3.SignedAngle(
            baseFootprint.turretFacingLocal,
            waterDirXZ,
            Vector3.up);
        Quaternion baseRotation = Quaternion.Euler(0f, baseYaw, 0f);

        Vector3 hangarLongAxis = anchor.trackAxisLocal;
        hangarLongAxis.y = 0f;
        float hangarYaw = Vector3.SignedAngle(
            hangarLongAxis,
            waterDirXZ,
            Vector3.up);
        Quaternion hangarRotation = Quaternion.Euler(0f, hangarYaw, 0f);

        float hangarHalfAlongBank = ProjectedHalfExtent(
            anchor.boundsSize,
            hangarRotation,
            bankSide);
        Bounds centeredHangarBounds = ComputeWorldHangarBounds(
            -(hangarRotation * anchor.boundsCenter),
            hangarRotation,
            anchor,
            1f);
        float visibleBoundsHalfAlongBank =
            centeredHangarBounds.extents.x * Mathf.Abs(bankSide.x) +
            centeredHangarBounds.extents.z * Mathf.Abs(bankSide.z);

        // BoundsOverlapRiver samples the world AABB, so use its projected
        // cross-bank half-extent to target the requested visible overlap.
        float hangarCenterOffset = Mathf.Max(
            0f,
            riverHalfWidth -
            (2f * TargetNavalHangarWaterOverlap - 1f) *
            visibleBoundsHalfAlongBank);

        if (anchor.trackAxisLength <= 0.001f)
        {
            validation.rejectReason = "Naval Hangar has no measurable horizontal length";
            return false;
        }

        Vector3 hangarGeometryCenter =
            routePoint + bankSide * hangarCenterOffset;
        Vector3 hangarRootPosition =
            hangarGeometryCenter - hangarRotation * anchor.boundsCenter;
        float hangarBottomLocalY =
            anchor.boundsCenter.y - anchor.boundsSize.y * 0.5f;
        // navalWaterlineOffset is an absolute depth; against the measured
        // Naval Hangar height (0.074 m) the 0.05 m setting sank 68% of the
        // model. Cap the draught to a fraction of the hangar's own height.
        float hangarDraught = Mathf.Min(
            navalWaterlineOffset,
            anchor.boundsSize.y * MaxNavalHangarDraughtFraction);
        hangarRootPosition.y =
            routePoint.y - hangarDraught - hangarBottomLocalY;

        Vector3 hangarWorld =
            hangarRootPosition + hangarRotation * anchor.boundsCenter;
        Quaternion hangarWorldRotation = hangarRotation * anchor.localRotation;

        float hangarHalfAlongRoute = ProjectedHalfExtent(
            anchor.boundsSize,
            hangarRotation,
            tangent);
        float baseHalfAlongBank = ProjectedHalfExtent(
            baseFootprint.boundsSize,
            baseRotation,
            bankSide);

        // Keep the main base adjacent to the landward end of the visible
        // hangar, not at an unrelated fixed offset from the river centerline.
        // Place the base beyond the hangar's landward edge using measured
        // extents, with a gap no larger than one measured hangar length.
        float baseHangarClearance = Mathf.Min(
            navalBankPadClearance,
            anchor.trackAxisLength);
        float baseCenterDistance =
            hangarCenterOffset + hangarHalfAlongBank + baseHalfAlongBank +
            baseHangarClearance;
        Vector3 baseGeometryCenter =
            routePoint + bankSide * baseCenterDistance;
        baseGeometryCenter.y = 0f;
        Vector3 baseRootXZ =
            baseGeometryCenter - baseRotation * baseFootprint.boundsCenter;
        baseRootXZ.y = 0f;

        if (!InsideRiverWorldBounds(baseGeometryCenter) ||
            !InsideRiverWorldBounds(baseRootXZ))
        {
            validation.rejectReason = "outside world bounds";
            return false;
        }

        // Sample the terrain across the footprint. Naval bases keep their
        // river-facing orientation and are never tilted to conform to a
        // bank slope: bad terrain is rejected and another route point is tried.
        bool baseSampled = SampleTerrainFootprintFromBounds(
            baseGeometryCenter,
            baseGeometryCenter,
            baseFootprint.boundsSize,
            baseRotation,
            out TerrainSampleResult sample,
            out TerrainFootprintSnapshot snapshot);
        RecordTerrainSamplingComparison(
            variant,
            "main base",
            requestIndex,
            index,
            baseGeometryCenter,
            snapshot);
        if (!baseSampled)
        {
            validation.rejectReason = "no terrain hit";
            return false;
        }

        if (sample.heightVariation > maxFootprintHeightVariation)
        {
            validation.rejectReason = $"footprint too uneven ({sample.heightVariation:F2} m)";
            return false;
        }

        if (sample.maxSlopeAngle > maxSlopeAngle)
        {
            validation.rejectReason = $"slope too steep ({sample.maxSlopeAngle:F1} deg)";
            return false;
        }

        // Validate the visible base bounds against the full river corridor.
        float rootDistance = DistanceXZ(sample.position, routePoint);
        bool baseOnLand =
            rootDistance - baseHalfAlongBank >= riverHalfWidth;

        if (!baseOnLand)
        {
            validation.rejectReason =
                $"base footprint in river ({rootDistance:F2} m from centreline)";
            return false;
        }

        // Place the measured main-base bounds on the sampled bank terrain. The
        // base stays level and is seated on its round platform outline
        // (TrySeatOnFootprint). If that sampling misses the terrain, fall back
        // to seating the lowest bounds point a fraction of the measured
        // footprint variation above the lowest sample (no extra lift).
        float lowestTerrain = snapshot.heights[0];
        for (int i = 1; i < snapshot.heights.Length; i++)
        {
            lowestTerrain = Mathf.Min(lowestTerrain, snapshot.heights[i]);
        }

        float seatHeight =
            lowestTerrain + sample.heightVariation * NavalBaseSeatFraction;
        Vector3 position =
            sample.position - baseRotation * baseFootprint.boundsCenter;
        position.y = seatHeight -
            baseFootprint.boundsCenter.y + baseFootprint.boundsSize.y * 0.5f;
        if (TrySeatOnFootprint(position, baseRotation, baseFootprint, out float seatedY))
        {
            position.y = seatedY;
        }

        if (IsTooCloseForNavalBase(
            position,
            sample.position,
            baseFootprint.boundsSize,
            baseRotation,
            routePoint,
            tangent,
            routeBaseMinSpacing))
        {
            validation.rejectReason = "too close to another base";
            return false;
        }

        float hangarDistance = DistanceXZ(hangarWorld, routePoint);
        float hangarMinRouteSpacing = Mathf.Max(
            routeBaseMinSpacing,
            hangarHalfAlongRoute * 2f + navalBankPadClearance);
        if (IsNavalHangarSpacingRejected(
            hangarWorld,
            anchor.boundsSize,
            hangarRotation,
            sample.position,
            routePoint,
            tangent,
            hangarMinRouteSpacing))
        {
            validation.rejectReason = "too close to another route hangar";
            return false;
        }

        // The hangar must be over water: its centre within the river
        // corridor (|distance| <= riverHalfWidth). The hangar extends from
        // the base (on land) toward the water, so it is on the same side of
        // the centreline as the base.
        bool hangarOverWater =
            Mathf.Abs(hangarDistance) <=
            riverHalfWidth + GeometryValidationEpsilon;

        // Additional check: a meaningful portion of the hangar's visible
        // bounds must lie over the water, not just the anchor point.
        bool hangarBoundsOverWater = false;
        float hangarWaterOverlapFraction = 0f;
        if (CurrentRiver != null && anchor.hasRenderer)
        {
            Bounds hangarWorldBounds = ComputeWorldHangarBounds(
                hangarRootPosition,
                hangarRotation,
                anchor,
                1f);

            hangarBoundsOverWater = BoundsOverlapRiver(
                hangarWorldBounds,
                CurrentRiver,
                -1,
                0f,
                0f,
                0.25f,
                out hangarWaterOverlapFraction);
        }

        if (!hangarOverWater)
        {
            validation.rejectReason = $"hangar not over water (dist={hangarDistance:F2} m from centreline)";
            return false;
        }

        if (!baseOnLand)
        {
            validation.rejectReason = $"base not on land ({rootDistance:F2} m from centreline)";
            return false;
        }

        if (!hangarBoundsOverWater)
        {
            validation.rejectReason = "hangar visible bounds not over water";
            return false;
        }

        validation.valid = true;
        validation.hangarOverWater = hangarOverWater;
        validation.baseOnLand = baseOnLand;
        validation.routeIndex = index;
        validation.routePoint = routePoint;
        validation.routeTangent = tangent;
        validation.hangarWorldPosition = hangarWorld;
        validation.hangarWorldRotation = hangarWorldRotation;
        validation.hangarRootPosition = hangarRootPosition;
        validation.hangarRootRotation = hangarRotation;
        validation.hangarRootScale = 1f;
        validation.localRiverWidth = localRiverWidth;
        validation.riverNarrowingTaperHalfDistance = 0f;
        validation.hangarWaterOverlapFraction = hangarWaterOverlapFraction;
        validation.hangarRouteDistance = hangarDistance;
        validation.rootRouteDistance = rootDistance;
        validation.rootPosition = position;
        validation.rootRotation = baseRotation;
        validation.navalBaseFootprintCenter = sample.position;
        validation.navalBaseFootprintSize = baseFootprint.boundsSize;
        validation.navalBaseFootprintRotation = baseRotation;
        validation.navalHangarFootprintSize = anchor.boundsSize;
        validation.footprintHeightVariation = sample.heightVariation;
        validation.footprintMaxSlope = sample.maxSlopeAngle;

        pose = new Pose(position, baseRotation);

        return true;
    }

    // ------------------------------------------------------------------
    // Prefab anchor measurement
    // ------------------------------------------------------------------

    private bool TryGetPrefabAnchor(
        GameObject prefab,
        string hangarName,
        bool isRiver,
        out PrefabAnchor anchor)
    {
        Dictionary<GameObject, PrefabAnchor> cache =
            isRiver
                ? navalAnchorCache
                : railAnchorCache;

        // Cache the result for this prefab.
        if (cache.TryGetValue(prefab, out anchor))
        {
            return true;
        }

        // Create a temporary instance to inspect the prefab hierarchy.
        GameObject temporary = Instantiate(
            prefab,
            Vector3.zero,
            Quaternion.identity);

        temporary.name = "TEMP_PrefabAnchorLookup";
        temporary.hideFlags = HideFlags.HideAndDontSave;

        Transform hangar = FindChildRecursive(
            temporary.transform,
            hangarName);

        if (hangar == null)
        {
            DestroyTemporary(temporary);

            Debug.LogError(
                $"BaseSpawner: Prefab '{prefab.name}' has no child named " +
                $"'{hangarName}'. Check the prefab hierarchy. " +
                "The hangar name must match a child GameObject (name " +
                "overrides inside PrefabInstance modifications are applied " +
                "by Unity at runtime).",
                this);

            anchor = default(PrefabAnchor);
            return false;
        }

        // Read the hangar's local transform relative to the prefab root.
        Vector3 localPosition = hangar.localPosition;
        Quaternion localRotation = hangar.localRotation;
        Vector3 localScale = hangar.localScale;

        // Read the combined renderer bounds, converted to root-local space.
        Renderer[] renderers =
            hangar.GetComponentsInChildren<Renderer>(true);

        bool hasRenderer = false;
        Bounds combinedBounds = new Bounds();

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            if (!hasRenderer)
            {
                combinedBounds = renderer.bounds;
                hasRenderer = true;
            }
            else
            {
                combinedBounds.Encapsulate(renderer.bounds);
            }
        }

        Vector3 boundsCenterLocal;
        Vector3 boundsSizeLocal;

        if (hasRenderer)
        {
            ConvertWorldBoundsToRootLocal(
                temporary.transform,
                combinedBounds,
                out boundsCenterLocal,
                out boundsSizeLocal);
        }
        else
        {
            boundsCenterLocal = localPosition;
            boundsSizeLocal = Vector3.zero;
        }

        Vector3 trackAxisLocal = FindLongestHorizontalMeshAxis(
            temporary.transform,
            renderers,
            boundsSizeLocal.x > boundsSizeLocal.z
                ? Vector3.right
                : Vector3.forward,
            boundsSizeLocal,
            out float trackAxisLength);

        DestroyTemporary(temporary);

        anchor = new PrefabAnchor
        {
            localPosition = localPosition,
            localRotation = localRotation,
            localScale = localScale,
            boundsCenter = boundsCenterLocal,
            boundsSize = boundsSizeLocal,
            trackAxisLocal = trackAxisLocal,
            trackAxisLength = trackAxisLength,
            hasRenderer = hasRenderer
        };

        cache[prefab] = anchor;

        if (verboseLogging)
        {
            Debug.Log(
                $"BaseSpawner: Measured {prefab.name} geometry bounds " +
                $"center {boundsCenterLocal:F3}, size {boundsSizeLocal:F3}, " +
                $"long axis {trackAxisLocal}, length {trackAxisLength:F3} m.",
                this);
        }

        return true;
    }

    private bool TryGetPrefabFootprint(
        GameObject prefab,
        out PrefabFootprint footprint)
    {
        if (baseFootprintCache.TryGetValue(prefab, out footprint))
        {
            return true;
        }

        GameObject temporary = Instantiate(
            prefab,
            Vector3.zero,
            Quaternion.identity);

        temporary.name = "TEMP_BaseFootprintLookup";
        temporary.hideFlags = HideFlags.HideAndDontSave;

        Renderer[] renderers =
            temporary.GetComponentsInChildren<Renderer>(true);

        bool hasRenderer = false;
        Bounds combinedBounds = new Bounds();

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            if (!hasRenderer)
            {
                combinedBounds = renderer.bounds;
                hasRenderer = true;
            }
            else
            {
                combinedBounds.Encapsulate(renderer.bounds);
            }
        }

        if (!hasRenderer)
        {
            DestroyTemporary(temporary);
            footprint = default(PrefabFootprint);
            return false;
        }

        ConvertWorldBoundsToRootLocal(
            temporary.transform,
            combinedBounds,
            out Vector3 boundsCenter,
            out Vector3 boundsSize);

        Transform turretGroup = FindChildRecursive(
            temporary.transform,
            "Base Flaks");
        Vector3 turretFacingLocal = Vector3.zero;
        bool hasTurretFacing = turretGroup != null;
        if (hasTurretFacing)
        {
            turretFacingLocal = temporary.transform.InverseTransformVector(
                turretGroup.position - temporary.transform.position);
            turretFacingLocal.y = 0f;
            hasTurretFacing = turretFacingLocal.sqrMagnitude > 0.0001f;
            if (hasTurretFacing)
            {
                turretFacingLocal.Normalize();
            }
        }

        DestroyTemporary(temporary);

        footprint = new PrefabFootprint
        {
            boundsCenter = boundsCenter,
            boundsSize = boundsSize,
            turretFacingLocal = turretFacingLocal,
            hasTurretFacing = hasTurretFacing,
            hasRenderer = true
        };
        baseFootprintCache[prefab] = footprint;

        if (verboseLogging)
        {
            Debug.Log(
                $"BaseSpawner: Measured {prefab.name} base footprint " +
                $"center {boundsCenter:F3}, size {boundsSize:F3}.",
                this);
        }

        return true;
    }

    // Root pivot height that seats a base on the terrain under its actual
    // round platform: the ellipse inscribed in the measured renderer bounds
    // (rim and half-radius ring plus centre), instead of the corners of the
    // bounding square, which lie outside the platform. The lowest contact is
    // left at most GroundContactAirShare of the measured relief above ground
    // and the rest is embedded. Uses bounds only, so it behaves identically in
    // builds where the imported meshes are not CPU-readable. Placement
    // validation is unaffected; this only chooses the height.
    private bool TrySeatOnFootprint(
        Vector3 pivot,
        Quaternion rotation,
        PrefabFootprint footprint,
        out float pivotY)
    {
        pivotY = pivot.y;
        float bottom = footprint.boundsCenter.y - footprint.boundsSize.y * 0.5f;
        float radiusX = footprint.boundsSize.x * 0.5f;
        float radiusZ = footprint.boundsSize.z * 0.5f;
        float lowest = float.PositiveInfinity;
        float highest = float.NegativeInfinity;

        for (int ring = 0; ring <= 2; ring++)
        {
            float scale = ring == 0 ? 1f : ring == 1 ? 0.5f : 0f;
            int samples = ring == 0 ? GroundContactRingSamples
                : ring == 1 ? GroundContactRingSamples / 2
                : 1;
            for (int i = 0; i < samples; i++)
            {
                float angle = i * Mathf.PI * 2f / samples;
                Vector3 local = new Vector3(
                    footprint.boundsCenter.x + Mathf.Cos(angle) * radiusX * scale,
                    bottom,
                    footprint.boundsCenter.z + Mathf.Sin(angle) * radiusZ * scale);
                Vector3 offset = rotation * local;
                if (!TryGetTerrainHit(
                        new Vector3(pivot.x + offset.x, 0f, pivot.z + offset.z),
                        out RaycastHit hit))
                {
                    return false;
                }

                // Pivot height at which this contact rests exactly on the ground.
                float required = hit.point.y - offset.y;
                lowest = Mathf.Min(lowest, required);
                highest = Mathf.Max(highest, required);
            }
        }

        pivotY = lowest + (highest - lowest) * GroundContactAirShare;
        return true;
    }

    private static void ConvertWorldBoundsToRootLocal(
        Transform root,
        Bounds worldBounds,
        out Vector3 localCenter,
        out Vector3 localSize)
    {
        Vector3 extents = worldBounds.extents;
        Vector3 first = root.InverseTransformPoint(
            worldBounds.center + new Vector3(-extents.x, -extents.y, -extents.z));
        Bounds localBounds = new Bounds(first, Vector3.zero);

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = worldBounds.center + new Vector3(
                        extents.x * x,
                        extents.y * y,
                        extents.z * z);

                    localBounds.Encapsulate(root.InverseTransformPoint(corner));
                }
            }
        }

        localCenter = localBounds.center;
        localSize = localBounds.size;
    }

    private static Vector3 FindLongestHorizontalMeshAxis(
        Transform root,
        Renderer[] renderers,
        Vector3 fallbackAxis,
        Vector3 fallbackBoundsSize,
        out float longestExtent)
    {
        Vector3 longestAxis = fallbackAxis;
        longestExtent = 0f;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                continue;
            }

            Vector3 meshSize = meshFilter.sharedMesh.bounds.size;
            Vector3[] localAxes =
            {
                Vector3.right,
                Vector3.up,
                Vector3.forward
            };
            float[] localExtents = { meshSize.x, meshSize.y, meshSize.z };

            for (int i = 0; i < localAxes.Length; i++)
            {
                Vector3 rootAxis = root.InverseTransformVector(
                    renderer.transform.TransformVector(localAxes[i]));
                Vector3 horizontalAxis =
                    new Vector3(rootAxis.x, 0f, rootAxis.z);
                float horizontalScale = horizontalAxis.magnitude;
                float horizontalExtent = localExtents[i] * horizontalScale;

                if (horizontalExtent <= longestExtent ||
                    horizontalAxis.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                longestExtent = horizontalExtent;
                longestAxis = horizontalAxis.normalized;
            }
        }

        if (longestExtent <= 0.001f)
        {
            longestAxis = fallbackAxis;
            longestExtent = ProjectedHalfExtent(
                fallbackBoundsSize,
                Quaternion.identity,
                fallbackAxis) * 2f;
        }

        return longestAxis.sqrMagnitude >= 0.0001f
            ? longestAxis.normalized
            : Vector3.forward;
    }

    // ------------------------------------------------------------------
    // Validation helpers
    // ------------------------------------------------------------------

    // Computes the world-space bounds of the hangar mesh after placement,
    // from the cached root-local anchor bounds.
    private Bounds ComputeWorldHangarBounds(
        Vector3 position,
        Quaternion rotation,
        PrefabAnchor anchor,
        float rootScale)
    {
        Vector3 localCenter = anchor.boundsCenter * rootScale;
        Vector3 min = position + rotation * localCenter;
        Bounds bounds = new Bounds(min, Vector3.zero);

        Vector3 half = anchor.boundsSize * (0.5f * rootScale);
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 local = localCenter + new Vector3(
                        half.x * x,
                        half.y * y,
                        half.z * z);
                    bounds.Encapsulate(position + rotation * local);
                }
            }
        }

        return bounds;
    }

    // Samples a 5x5 grid over the visible bounds. This detects a shipyard
    // spanning across the channel even when its four outer corners are on land.
    private bool BoundsOverlapRiver(
        Bounds hangarWorldBounds,
        RiverGenerator river,
        int pendingNarrowingRouteIndex,
        float pendingNarrowingWidth,
        float pendingNarrowingTaperHalfDistance,
        float minOverWaterFraction,
        out float overWaterFraction)
    {
        overWaterFraction = 0f;
        IReadOnlyList<Vector3> route = river != null
            ? river.GeneratedRoute
            : null;
        if (route == null || route.Count == 0)
        {
            return false;
        }

        float[] widthAtVertex = new float[route.Count];
        for (int i = 0; i < route.Count; i++)
        {
            widthAtVertex[i] = river.GetRiverWidthAtIndexWithPendingNarrowing(
                i,
                pendingNarrowingRouteIndex,
                pendingNarrowingWidth,
                pendingNarrowingTaperHalfDistance);
        }

        Vector3 center = hangarWorldBounds.center;
        Vector3 extents = hangarWorldBounds.extents;

        int overWater = 0;
        const int samplesPerAxis = 5;
        int sampleCount = samplesPerAxis * samplesPerAxis;

        for (int x = 0; x < samplesPerAxis; x++)
        {
            float xT = x / (float)(samplesPerAxis - 1) * 2f - 1f;

            for (int z = 0; z < samplesPerAxis; z++)
            {
                float zT = z / (float)(samplesPerAxis - 1) * 2f - 1f;
                Vector3 pt = center + new Vector3(
                    extents.x * xT,
                    0f,
                    extents.z * zT);

                Vector2 pointXZ = new Vector2(pt.x, pt.z);
                bool pointOverWater = false;

                for (int i = 0; i < route.Count - 1; i++)
                {
                    Vector2 start = new Vector2(route[i].x, route[i].z);
                    Vector2 end = new Vector2(route[i + 1].x, route[i + 1].z);
                    Vector2 segment = end - start;
                    float segmentLengthSquared = segment.sqrMagnitude;
                    float t = segmentLengthSquared > 0.000001f
                        ? Mathf.Clamp01(Vector2.Dot(pointXZ - start, segment) /
                                        segmentLengthSquared)
                        : 0f;
                    Vector2 closest = start + segment * t;
                    float distance = Vector2.Distance(pointXZ, closest);
                    float localWidth = Mathf.Lerp(
                        widthAtVertex[i],
                        widthAtVertex[i + 1],
                        t);

                    if (distance <= localWidth * 0.5f + GeometryValidationEpsilon)
                    {
                        pointOverWater = true;
                        break;
                    }
                }

                if (pointOverWater)
                {
                    overWater++;
                }
            }
        }

        overWaterFraction = (float)overWater / sampleCount;
        return overWaterFraction >= minOverWaterFraction;
    }

    private static float ProjectedHalfExtent(
        Vector3 boundsSize,
        Quaternion rotation,
        Vector3 axisWorld)
    {
        if (axisWorld.sqrMagnitude < 0.0001f)
        {
            return 0f;
        }

        axisWorld.Normalize();
        Vector3 xAxis = rotation * Vector3.right;
        Vector3 yAxis = rotation * Vector3.up;
        Vector3 zAxis = rotation * Vector3.forward;

        return
            boundsSize.x * 0.5f * Mathf.Abs(Vector3.Dot(xAxis, axisWorld)) +
            boundsSize.y * 0.5f * Mathf.Abs(Vector3.Dot(yAxis, axisWorld)) +
            boundsSize.z * 0.5f * Mathf.Abs(Vector3.Dot(zAxis, axisWorld));
    }

    // ------------------------------------------------------------------
    // Terrain sampling
    // ------------------------------------------------------------------

    private bool SampleTerrainFootprint(
        Vector3 samplePosition,
        float radius,
        int count,
        out TerrainSampleResult result)
    {
        result = default(TerrainSampleResult);

        // Compatibility entry point for the existing isolated validation
        // runner. Resolve a spawned renderer footprint; never fall back to
        // the legacy radius or count when geometry cannot be identified.
        if (radius <= 0f || count < 1 ||
            !TryResolvePlacedTerrainFootprint(
                samplePosition,
                out Vector3 footprintCenter,
                out Vector3 footprintSize,
                out Quaternion footprintRotation))
        {
            return false;
        }

        return SampleTerrainFootprintFromBounds(
            samplePosition,
            footprintCenter,
            footprintSize,
            footprintRotation,
            out result,
            out _);
    }

    private bool SampleTerrainFootprintFromBounds(
        Vector3 samplePosition,
        Vector3 footprintCenter,
        Vector3 footprintSize,
        Quaternion footprintRotation,
        out TerrainSampleResult result,
        out TerrainFootprintSnapshot snapshot)
    {
        result = default(TerrainSampleResult);
        snapshot = default(TerrainFootprintSnapshot);

        if (!HasMeasurableHorizontalFootprint(Vector3.zero, footprintSize) ||
            !IsFinite(samplePosition) ||
            !IsFinite(footprintCenter) ||
            !IsFinite(footprintRotation) ||
            footprintRotation.x * footprintRotation.x +
            footprintRotation.y * footprintRotation.y +
            footprintRotation.z * footprintRotation.z +
            footprintRotation.w * footprintRotation.w < 0.0001f)
        {
            return false;
        }

        float halfX = footprintSize.x * 0.5f;
        float halfZ = footprintSize.z * 0.5f;
        float[] heights = new float[TerrainFootprintSampleCount];
        Vector3[] normals = new Vector3[TerrainFootprintSampleCount];
        Vector3[] positions = new Vector3[TerrainFootprintSampleCount];
        bool[] hits = new bool[TerrainFootprintSampleCount];
        bool allHits = true;
        Vector3 bottomFaceOffset =
            new Vector3(0f, -footprintSize.y * 0.5f, 0f);

        // Coordinates are centre, four edge midpoints, then four corners.
        // Sample on the renderer bounds' bottom face, transformed by the
        // placement rotation, so tilted land bases use their actual support.
        for (int i = 0; i < TerrainFootprintSampleCoordinates.Length; i++)
        {
            Vector2 coordinate = TerrainFootprintSampleCoordinates[i];
            Vector3 offset = footprintRotation * (
                bottomFaceOffset + new Vector3(
                    coordinate.x * halfX,
                    0f,
                    coordinate.y * halfZ));
            Vector3 worldPoint = footprintCenter + offset;
            Vector3 samplePoint =
                new Vector3(worldPoint.x, 0f, worldPoint.z);
            positions[i] = samplePoint;

            if (!TryGetTerrainHit(
                samplePoint,
                out RaycastHit hit))
            {
                allHits = false;
                continue;
            }

            heights[i] = hit.point.y;
            normals[i] = hit.normal;
            hits[i] = true;
        }

        snapshot = new TerrainFootprintSnapshot
        {
            positions = positions,
            heights = heights,
            normals = normals,
            hit = hits,
            allHits = allHits
        };

        if (!allHits)
        {
            return false;
        }

        float minH = heights[0];
        float maxH = heights[0];
        Vector3 avgNormal = Vector3.zero;
        float contactHeight = 0f;
        float maxSlope = 0f;
        for (int i = 0; i < TerrainFootprintSampleCount; i++)
        {
            float h = heights[i];
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
            avgNormal += normals[i];
            contactHeight += h;

            float a = Vector3.Angle(normals[i], Vector3.up);
            if (a > maxSlope) maxSlope = a;
        }
        avgNormal.Normalize();
        contactHeight /= TerrainFootprintSampleCount;

        snapshot.heightVariation = maxH - minH;
        snapshot.maxSlopeAngle = maxSlope;

        // Recommended contact height: the mean of samples so the base sits
        // flush with the average ground rather than halfway between extremes
        // (which would bury half and float half on slopes).
        result.position = new Vector3(
            samplePosition.x,
            contactHeight,
            samplePosition.z);
        result.normal = avgNormal;
        result.heightVariation = maxH - minH;
        result.maxSlopeAngle = maxSlope;
        result.contactHeight = contactHeight;
        result.isValid = true;

        return true;
    }

    private bool TryResolvePlacedTerrainFootprint(
        Vector3 samplePosition,
        out Vector3 footprintCenter,
        out Vector3 footprintSize,
        out Quaternion footprintRotation)
    {
        footprintCenter = Vector3.zero;
        footprintSize = Vector3.zero;
        footprintRotation = Quaternion.identity;

        float bestDistance = float.PositiveInfinity;
        Transform bestRoot = null;
        Vector3 bestLocalCenter = Vector3.zero;
        Vector3 bestLocalSize = Vector3.zero;

        for (int i = 0; i < transform.childCount; i++)
        {
            Transform generatedRoot = transform.GetChild(i);
            if (!generatedRoot.name.StartsWith(
                    "GeneratedBase_",
                    StringComparison.Ordinal))
            {
                continue;
            }

            Transform railHangar = FindChildRecursive(
                generatedRoot,
                "Rail Hangar");
            Transform navalHangar = FindChildRecursive(
                generatedRoot,
                "Naval Hangar");

            Transform candidateRoot;
            bool isHangar;
            Vector3 localCenter;
            Vector3 localSize;
            if (railHangar != null)
            {
                if (!railAnchorCache.TryGetValue(
                        railHangarPrefab,
                        out PrefabAnchor railAnchor) ||
                    !railAnchor.hasRenderer ||
                    !HasMeasurableHorizontalFootprint(
                        railAnchor.boundsCenter,
                        railAnchor.boundsSize))
                {
                    continue;
                }

                candidateRoot = railHangar;
                isHangar = true;
                localCenter = railAnchor.boundsCenter;
                localSize = railAnchor.boundsSize;
            }
            else if (navalHangar != null)
            {
                continue;
            }
            else
            {
                BaseEntry entry = baseEntries.FirstOrDefault(candidate =>
                    candidate != null &&
                    !string.IsNullOrEmpty(candidate.baseName) &&
                    generatedRoot.name.StartsWith(
                        $"GeneratedBase_{candidate.baseName}_",
                        StringComparison.Ordinal));
                if (entry == null || entry.prefab == null ||
                    !baseFootprintCache.TryGetValue(
                        entry.prefab,
                        out PrefabFootprint baseFootprint) ||
                    !baseFootprint.hasRenderer ||
                    !HasMeasurableHorizontalFootprint(
                        baseFootprint.boundsCenter,
                        baseFootprint.boundsSize))
                {
                    continue;
                }

                candidateRoot = generatedRoot;
                isHangar = false;
                localCenter = baseFootprint.boundsCenter;
                localSize = baseFootprint.boundsSize;
            }

            Vector3 worldCenter =
                candidateRoot.position + candidateRoot.rotation * localCenter;
            float distance = isHangar
                ? DistanceXZ(worldCenter, samplePosition)
                : DistanceXZ(candidateRoot.position, samplePosition);

            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestRoot = candidateRoot;
            bestLocalCenter = localCenter;
            bestLocalSize = localSize;
        }

        // The compatibility path is only used to independently re-check a
        // specific spawned base or Rail hangar in the isolated test scene.
        if (bestRoot == null || bestDistance > 0.05f ||
            !HasMeasurableHorizontalFootprint(
                bestLocalCenter,
                bestLocalSize))
        {
            return false;
        }

        footprintCenter =
            bestRoot.position + bestRoot.rotation * bestLocalCenter;
        footprintSize = bestLocalSize;
        footprintRotation = bestRoot.rotation;
        return true;
    }

    private TerrainFootprintSnapshot SampleLegacyTerrainFootprint(
        Vector3 samplePosition)
    {
        TerrainFootprintSnapshot snapshot = default(TerrainFootprintSnapshot);
        if (terrainSampleRadius <= 0f || terrainSampleCount < 1)
        {
            return snapshot;
        }

        int sampleCount = terrainSampleCount + 1;
        Vector3[] positions = new Vector3[sampleCount];
        float[] heights = new float[sampleCount];
        Vector3[] normals = new Vector3[sampleCount];
        bool[] hits = new bool[sampleCount];
        bool allHits = true;

        for (int i = 0; i < terrainSampleCount; i++)
        {
            float angle = 2f * Mathf.PI * i / terrainSampleCount;
            Vector3 point = new Vector3(
                samplePosition.x + Mathf.Cos(angle) * terrainSampleRadius,
                0f,
                samplePosition.z + Mathf.Sin(angle) * terrainSampleRadius);
            positions[i] = point;
            if (!TryGetTerrainHit(point, out RaycastHit hit))
            {
                allHits = false;
                continue;
            }

            heights[i] = hit.point.y;
            normals[i] = hit.normal;
            hits[i] = true;
        }

        int centerIndex = terrainSampleCount;
        Vector3 centerPoint = new Vector3(
            samplePosition.x,
            0f,
            samplePosition.z);
        positions[centerIndex] = centerPoint;
        if (!TryGetTerrainHit(centerPoint, out RaycastHit centerHit))
        {
            allHits = false;
        }
        else
        {
            heights[centerIndex] = centerHit.point.y;
            normals[centerIndex] = centerHit.normal;
            hits[centerIndex] = true;
        }

        snapshot = new TerrainFootprintSnapshot
        {
            positions = positions,
            heights = heights,
            normals = normals,
            hit = hits,
            allHits = allHits
        };

        if (allHits)
        {
            CalculateTerrainSnapshotMetrics(ref snapshot);
        }

        return snapshot;
    }

    private void RecordTerrainSamplingComparison(
        string variant,
        string footprintKind,
        int requestIndex,
        int routeIndex,
        Vector3 samplePosition,
        TerrainFootprintSnapshot newSnapshot)
    {
        if (newSnapshot.positions == null)
        {
            return;
        }

        try
        {
            TerrainFootprintSnapshot oldSnapshot =
                SampleLegacyTerrainFootprint(samplePosition);
            string oldReason = TerrainSnapshotRejectionReason(oldSnapshot);
            string newReason = TerrainSnapshotRejectionReason(newSnapshot);
            string groupKey = $"{variant}|{footprintKind}";

            terrainSamplingVariantCounts[groupKey] =
                terrainSamplingVariantCounts.TryGetValue(
                    groupKey,
                    out int comparedCount)
                        ? comparedCount + 1
                        : 1;

            if (!string.Equals(oldReason, newReason, StringComparison.Ordinal))
            {
                string transitionKey =
                    $"{groupKey}|old:{oldReason}->new:{newReason}";
                terrainSamplingComparisonCounts[transitionKey] =
                    terrainSamplingComparisonCounts.TryGetValue(
                        transitionKey,
                        out int transitionCount)
                            ? transitionCount + 1
                            : 1;
            }

            bool oldAccepted = oldReason == "pass";
            bool newAccepted = newReason == "pass";
            if (oldAccepted != newAccepted)
            {
                string disagreementKey =
                    $"{groupKey}|old:{oldReason}->new:{newReason}";
                terrainSamplingAcceptanceDisagreements[
                    disagreementKey] =
                    terrainSamplingAcceptanceDisagreements.TryGetValue(
                        disagreementKey,
                        out int disagreementCount)
                            ? disagreementCount + 1
                            : 1;

                if (routeIndex >= 0)
                {
                    string routeIndexKey =
                        $"{groupKey}|idx:{routeIndex}|old:{oldReason}->new:{newReason}";
                    terrainSamplingRouteIndexDisagreements[routeIndexKey] =
                        terrainSamplingRouteIndexDisagreements.TryGetValue(
                            routeIndexKey,
                            out int routeDisagreementCount)
                                ? routeDisagreementCount + 1
                                : 1;
                }

                if (!terrainSamplingRepresentatives.ContainsKey(groupKey))
                {
                    terrainSamplingRepresentatives[groupKey] =
                        new TerrainSamplingRepresentative
                        {
                            priority = TerrainSamplingRepresentativePriority(
                                variant,
                                footprintKind),
                            message = FormatTerrainSamplingRepresentative(
                                variant,
                                footprintKind,
                                requestIndex,
                                routeIndex,
                                samplePosition,
                                oldSnapshot,
                                oldReason,
                                newSnapshot,
                                newReason)
                        };
                }
            }
        }
        catch (Exception exception)
        {
            // Diagnostic failures must not affect the placement decision.
            terrainSamplingDiagnosticErrors++;
            if (terrainSamplingDiagnosticErrors == 1)
            {
                Debug.LogWarning(
                    "BaseSpawner: Terrain comparison diagnostics failed; " +
                    "placement validation continues unchanged. " +
                    exception.Message,
                    this);
            }
        }
    }

    private string TerrainSnapshotRejectionReason(
        TerrainFootprintSnapshot snapshot)
    {
        if (snapshot.positions == null || snapshot.positions.Length == 0)
        {
            return "unavailable";
        }

        if (!snapshot.allHits)
        {
            return "no-hit";
        }

        if (snapshot.heightVariation > maxFootprintHeightVariation)
        {
            return "uneven";
        }

        if (snapshot.maxSlopeAngle > maxSlopeAngle)
        {
            return "slope";
        }

        return "pass";
    }

    private static void CalculateTerrainSnapshotMetrics(
        ref TerrainFootprintSnapshot snapshot)
    {
        if (snapshot.positions == null ||
            snapshot.heights == null ||
            snapshot.normals == null ||
            snapshot.heights.Length == 0 ||
            !snapshot.allHits)
        {
            return;
        }

        float minHeight = snapshot.heights[0];
        float maxHeight = snapshot.heights[0];
        float maxSlope = 0f;
        for (int i = 0; i < snapshot.heights.Length; i++)
        {
            float height = snapshot.heights[i];
            if (height < minHeight) minHeight = height;
            if (height > maxHeight) maxHeight = height;

            float slope = Vector3.Angle(snapshot.normals[i], Vector3.up);
            if (slope > maxSlope) maxSlope = slope;
        }

        snapshot.heightVariation = maxHeight - minHeight;
        snapshot.maxSlopeAngle = maxSlope;
    }

    private static int TerrainSamplingRepresentativePriority(
        string variant,
        string footprintKind)
    {
        if (variant == "Rail" && footprintKind == "Rail hangar") return 0;
        if (variant == "Heli Rail" && footprintKind == "Rail hangar") return 1;
        if (variant == "Naval" && footprintKind == "main base") return 2;
        if (variant == "Heli Naval" && footprintKind == "main base") return 3;
        if (variant == "Air Naval" && footprintKind == "main base") return 4;
        return 5;
    }

    private string FormatTerrainSamplingRepresentative(
        string variant,
        string footprintKind,
        int requestIndex,
        int routeIndex,
        Vector3 samplePosition,
        TerrainFootprintSnapshot oldSnapshot,
        string oldReason,
        TerrainFootprintSnapshot newSnapshot,
        string newReason)
    {
        string oldMetrics = oldSnapshot.allHits
            ? $"variation={oldSnapshot.heightVariation:F3}m, slope={oldSnapshot.maxSlopeAngle:F2}deg"
            : "terrain hit missing";
        string newMetrics = newSnapshot.allHits
            ? $"variation={newSnapshot.heightVariation:F3}m, slope={newSnapshot.maxSlopeAngle:F2}deg"
            : "terrain hit missing";

        return
            $"BaseSpawner: Terrain comparison seed={seed}, " +
            $"{variant} #{requestIndex}, {footprintKind}, routeIndex=" +
            $"{(routeIndex >= 0 ? routeIndex.ToString() : "n/a")}, " +
            $"sampleCenter=({samplePosition.x:F2},{samplePosition.z:F2}), " +
            $"old[{oldReason}; {oldMetrics}] points={FormatTerrainSnapshotPoints(oldSnapshot)}; " +
            $"new[{newReason}; {newMetrics}] points={FormatTerrainSnapshotPoints(newSnapshot)}";
    }

    private static string FormatTerrainSnapshotPoints(
        TerrainFootprintSnapshot snapshot)
    {
        if (snapshot.positions == null)
        {
            return "unavailable";
        }

        List<string> points = new List<string>(snapshot.positions.Length);
        for (int i = 0; i < snapshot.positions.Length; i++)
        {
            Vector3 point = snapshot.positions[i];
            if (snapshot.hit == null || i >= snapshot.hit.Length ||
                !snapshot.hit[i])
            {
                points.Add(
                    $"{i}:({point.x:F2},{point.z:F2})=MISS");
                continue;
            }

            Vector3 normal = snapshot.normals[i];
            points.Add(
                $"{i}:({point.x:F2},{point.z:F2}) h={snapshot.heights[i]:F3} " +
                $"n=({normal.x:F2},{normal.y:F2},{normal.z:F2})");
        }

        return "[" + string.Join("; ", points) + "]";
    }

    private void LogTerrainSamplingComparisonSummary()
    {
        int compared = terrainSamplingVariantCounts.Values.Sum();
        int acceptanceDisagreements =
            terrainSamplingAcceptanceDisagreements.Values.Sum();
        int reasonTransitions = terrainSamplingComparisonCounts.Values.Sum();

        List<string> lines = new List<string>
        {
            "BaseSpawner: Terrain sampling comparison " +
            $"seed={seed}; old circle={terrainSampleRadius:F2}m/" +
            $"{terrainSampleCount}+centre; new renderer bounds=" +
            $"{TerrainFootprintSampleCount} bottom-face points; " +
            $"thresholds variation={maxFootprintHeightVariation:F2}m, " +
            $"slope={maxSlopeAngle:F1}deg; candidates={compared}, " +
            $"acceptance disagreements={acceptanceDisagreements}, " +
            $"reason transitions={reasonTransitions}, " +
            $"diagnostic errors={terrainSamplingDiagnosticErrors}."
        };

        foreach (KeyValuePair<string, int> group in
                 terrainSamplingVariantCounts.OrderBy(pair => pair.Key))
        {
            string prefix = group.Key + "|";
            List<string> transitions = terrainSamplingComparisonCounts
                .Where(pair => pair.Key.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
                .OrderBy(pair => pair.Key)
                .Select(pair =>
                    pair.Key.Substring(prefix.Length) + $" x{pair.Value}")
                .ToList();

            List<string> routeIndices = terrainSamplingRouteIndexDisagreements
                .Where(pair => pair.Key.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(3)
                .Select(pair =>
                    pair.Key.Substring(prefix.Length) + $" x{pair.Value}")
                .ToList();

            int disagreements = terrainSamplingAcceptanceDisagreements
                .Where(pair => pair.Key.StartsWith(
                    prefix,
                    StringComparison.Ordinal))
                .Sum(pair => pair.Value);

            lines.Add(
                $"  {group.Key}: compared={group.Value}, " +
                $"acceptance disagreements={disagreements}" +
                (transitions.Count > 0
                    ? ", transitions=" + string.Join(", ", transitions)
                    : ", no reason transitions") +
                (routeIndices.Count > 0
                    ? ", disagreement route indices=" +
                      string.Join(", ", routeIndices)
                    : string.Empty));
        }

        Debug.Log(string.Join(Environment.NewLine, lines), this);

        foreach (TerrainSamplingRepresentative representative in
                 terrainSamplingRepresentatives.Values
                     .OrderBy(item => item.priority)
                     .ThenBy(item => item.message, StringComparer.Ordinal)
                     .Take(MaxTerrainSamplingRepresentativeRecords))
        {
            Debug.Log(representative.message, this);
        }
    }

    private string RecordFailedRouteSampling(BaseRequest request)
    {
        RouteIndexSamplingState sampling = request.routeSamplingState;
        if (sampling == null || sampling.AvailableRouteIndices <= 0)
        {
            return string.Empty;
        }

        if (!routeSamplingAggregates.TryGetValue(
            request.baseName,
            out RouteSamplingAggregate aggregate))
        {
            aggregate = new RouteSamplingAggregate();
            routeSamplingAggregates[request.baseName] = aggregate;
        }

        aggregate.failedRequests++;
        aggregate.uniqueRouteIndices += sampling.UniqueIndicesAttempted;
        aggregate.availableRouteIndices += sampling.AvailableRouteIndices;
        aggregate.repeatedRouteIndexAttempts +=
            sampling.RepeatedIndexAttempts;
        aggregate.completeCyclesExhausted +=
            sampling.CompleteCyclesExhausted;

        return
            $" Route-index coverage: {sampling.UniqueIndicesAttempted}/" +
            $"{sampling.AvailableRouteIndices} unique, " +
            $"repeats={sampling.RepeatedIndexAttempts}, " +
            $"complete cycles={sampling.CompleteCyclesExhausted}.";
    }

    private void LogRouteSamplingDiagnostics()
    {
        if (routeSamplingAggregates.Count == 0)
        {
            return;
        }

        string details = string.Join(
            "; ",
            routeSamplingAggregates
                .OrderBy(pair => pair.Key)
                .Select(pair =>
                {
                    RouteSamplingAggregate aggregate = pair.Value;
                    return
                        $"{pair.Key}: failed={aggregate.failedRequests}, " +
                        $"unique={aggregate.uniqueRouteIndices}/" +
                        $"{aggregate.availableRouteIndices}, " +
                        $"repeats={aggregate.repeatedRouteIndexAttempts}, " +
                        $"complete cycles={aggregate.completeCyclesExhausted}";
                }));

        Debug.Log(
            $"BaseSpawner: Route-index coverage for failed requests: {details}.",
            this);
    }

    private static bool HasMeasurableHorizontalFootprint(
        Vector3 boundsCenter,
        Vector3 boundsSize)
    {
        return IsFinite(boundsCenter) &&
               IsFinite(boundsSize) &&
               boundsSize.x > GeometryValidationEpsilon &&
               boundsSize.z > GeometryValidationEpsilon &&
               boundsSize.y >= 0f;
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) && IsFinite(value.y) &&
               IsFinite(value.z) && IsFinite(value.w);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void ReportTerrainFootprintProblem(
        GameObject prefab,
        string reason,
        bool handledRejection = false)
    {
        if (prefab == null || terrainFootprintWarnings.Add(prefab))
        {
            string message =
                $"BaseSpawner: Cannot safely sample terrain for " +
                $"'{(prefab != null ? prefab.name : "<missing prefab>")}'. " +
                $"{reason}. Placement is rejected; no fallback is used.";
            // A candidate site whose terrain alignment does not converge is
            // rejected and another site is tried, so it is reported as a
            // warning. Every other caller remains an error.
            if (handledRejection) Debug.LogWarning(message, this);
            else Debug.LogError(message, this);
        }
    }

    private bool TryGetTerrainHit(
        Vector3 worldXZ,
        out RaycastHit hit)
    {
        if (terrainQueryProvider != null && terrainQueryProvider.IsReady)
        {
            return terrainQueryProvider.TryRaycastDown(
                worldXZ,
                raycastHeight,
                out hit);
        }

        if (terrainCollider == null)
        {
            hit = default(RaycastHit);
            return false;
        }

        Bounds bounds = terrainCollider.bounds;

        Vector3 origin = new Vector3(
            worldXZ.x,
            bounds.max.y + raycastHeight,
            worldXZ.z);

        float distance =
            origin.y - bounds.min.y + raycastHeight;

        return terrainCollider.Raycast(
            new Ray(origin, Vector3.down),
            out hit,
            distance);
    }

    private bool InsideBounds(
        float localX,
        float localZ)
    {
        return
            Mathf.Abs(localX) <=
                mapSizeX / 2f - edgePadding &&
            Mathf.Abs(localZ) <=
                mapSizeZ / 2f - edgePadding;
    }

    private bool InsideWorldBounds(Vector3 worldPosition)
    {
        float localX =
            worldPosition.x - transform.position.x;

        float localZ =
            worldPosition.z - transform.position.z;

        return InsideBounds(localX, localZ);
    }

    private bool InsideRiverWorldBounds(Vector3 worldPosition)
    {
        if (CurrentRiver == null)
        {
            return InsideWorldBounds(worldPosition);
        }

        Vector2 routeMapCenter = CurrentRiver.MapCenter;
        return InsideBounds(
            worldPosition.x - routeMapCenter.x,
            worldPosition.z - routeMapCenter.y);
    }

    private bool IsTooCloseForNavalBase(
        Vector3 candidateRoot,
        Vector3 candidateFootprintCenter,
        Vector3 candidateFootprintSize,
        Quaternion candidateFootprintRotation,
        Vector3 candidateRoutePoint,
        Vector3 candidateRouteTangent,
        float sameShoreSpacing)
    {
        // Preserve the ordinary route spacing rule against land and Rail
        // bases. Naval-family bases are compared separately using their local
        // river-bank side and renderer-footprint overlap.
        foreach (Vector3 existing in placedPositions)
        {
            if (IsPlacedNavalRoot(existing))
            {
                continue;
            }

            if (DistanceXZ(candidateRoot, existing) < sameShoreSpacing)
            {
                return true;
            }
        }

        int candidateBank = GetRiverBankSideSign(
            candidateFootprintCenter,
            candidateRoutePoint,
            candidateRouteTangent);

        foreach (PlacedNavalPlacement existing in placedNavalPlacements)
        {
            if (FootprintsOverlapXZ(
                candidateFootprintCenter,
                candidateFootprintSize,
                candidateFootprintRotation,
                existing.baseFootprintCenter,
                existing.baseFootprintSize,
                existing.baseFootprintRotation))
            {
                return true;
            }

            int existingBank = GetRiverBankSideSign(
                existing.baseFootprintCenter,
                existing.routePoint,
                existing.routeTangent);
            // Bank sides are only comparable on the same river (routeId is
            // always 0 when there is a single river).
            bool oppositeBanks = existing.routeId == activeRouteId &&
                                 candidateBank != 0 &&
                                 existingBank != 0 &&
                                 candidateBank != existingBank;

            if (oppositeBanks)
            {
                // Main-base proximity is allowed across the channel unless
                // the measured renderer footprints would physically overlap.
                continue;
            }

            if (DistanceXZ(candidateRoot, existing.rootPosition) <
                sameShoreSpacing)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsNavalHangarSpacingRejected(
        Vector3 candidateCenter,
        Vector3 candidateSize,
        Quaternion candidateRotation,
        Vector3 candidateBankReferenceCenter,
        Vector3 candidateRoutePoint,
        Vector3 candidateRouteTangent,
        float otherHangarSpacing)
    {
        int candidateBank = GetRiverBankSideSign(
            candidateBankReferenceCenter,
            candidateRoutePoint,
            candidateRouteTangent);

        foreach (PlacedNavalPlacement existing in placedNavalPlacements)
        {
            float edgeGap = FootprintEdgeGapXZ(
                candidateCenter,
                candidateSize,
                candidateRotation,
                existing.hangarCenter,
                existing.hangarFootprintSize,
                existing.hangarRootRotation);

            if (edgeGap <= GeometryValidationEpsilon)
            {
                return true;
            }

            int existingBank = GetRiverBankSideSign(
                existing.baseFootprintCenter,
                existing.routePoint,
                existing.routeTangent);
            // Bank sides are only comparable on the same river (routeId is
            // always 0 when there is a single river).
            bool oppositeBanks = existing.routeId == activeRouteId &&
                                 candidateBank != 0 &&
                                 existingBank != 0 &&
                                 candidateBank != existingBank;

            // Across the river the channel may make the 7.5 m target
            // impossible. In that case allow the pair if the visible bounds
            // remain disjoint; on the same bank retain the target edge gap.
            if (!oppositeBanks &&
                edgeGap + GeometryValidationEpsilon <
                TargetNavalHangarEdgeClearance)
            {
                return true;
            }
        }

        // Keep the existing center-spacing rule against Rail hangars. Rail
        // placement itself continues to use IsTooCloseToRouteHangar unchanged.
        foreach (Vector3 existing in placedRouteHangarPositions)
        {
            if (IsPlacedNavalHangar(existing))
            {
                continue;
            }

            if (DistanceXZ(candidateCenter, existing) < otherHangarSpacing)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPlacedNavalRoot(Vector3 position)
    {
        foreach (PlacedNavalPlacement existing in placedNavalPlacements)
        {
            if (DistanceXZ(position, existing.rootPosition) <=
                GeometryValidationEpsilon)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPlacedNavalHangar(Vector3 position)
    {
        foreach (PlacedNavalPlacement existing in placedNavalPlacements)
        {
            if (DistanceXZ(position, existing.hangarCenter) <=
                GeometryValidationEpsilon)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetRiverBankSideSign(
        Vector3 footprintCenter,
        Vector3 routePoint,
        Vector3 routeTangent)
    {
        routeTangent.y = 0f;
        if (routeTangent.sqrMagnitude < 0.0001f)
        {
            return 0;
        }

        routeTangent.Normalize();
        Vector3 localBankNormal =
            Vector3.Cross(Vector3.up, routeTangent).normalized;
        float signedOffset = Vector3.Dot(
            footprintCenter - routePoint,
            localBankNormal);

        if (Mathf.Abs(signedOffset) <= GeometryValidationEpsilon)
        {
            return 0;
        }

        return signedOffset > 0f ? 1 : -1;
    }

    private static bool FootprintsOverlapXZ(
        Vector3 centerA,
        Vector3 sizeA,
        Quaternion rotationA,
        Vector3 centerB,
        Vector3 sizeB,
        Quaternion rotationB)
    {
        Vector2 delta = new Vector2(centerB.x - centerA.x, centerB.z - centerA.z);
        Vector3[] axes =
        {
            rotationA * Vector3.right,
            rotationA * Vector3.forward,
            rotationB * Vector3.right,
            rotationB * Vector3.forward
        };

        foreach (Vector3 axis3 in axes)
        {
            Vector2 axis = new Vector2(axis3.x, axis3.z);
            if (axis.sqrMagnitude < 0.0001f)
            {
                continue;
            }

            axis.Normalize();
            float centerDistance = Mathf.Abs(Vector2.Dot(delta, axis));
            float halfA = ProjectedHalfExtent(
                sizeA,
                rotationA,
                new Vector3(axis.x, 0f, axis.y));
            float halfB = ProjectedHalfExtent(
                sizeB,
                rotationB,
                new Vector3(axis.x, 0f, axis.y));

            if (centerDistance > halfA + halfB + GeometryValidationEpsilon)
            {
                return false;
            }
        }

        return true;
    }

    private static float FootprintEdgeGapXZ(
        Vector3 centerA,
        Vector3 sizeA,
        Quaternion rotationA,
        Vector3 centerB,
        Vector3 sizeB,
        Quaternion rotationB)
    {
        if (FootprintsOverlapXZ(
            centerA,
            sizeA,
            rotationA,
            centerB,
            sizeB,
            rotationB))
        {
            return 0f;
        }

        Vector2[] cornersA = GetFootprintCornersXZ(centerA, sizeA, rotationA);
        Vector2[] cornersB = GetFootprintCornersXZ(centerB, sizeB, rotationB);
        float minimumSqrDistance = float.PositiveInfinity;

        for (int i = 0; i < 4; i++)
        {
            Vector2 a0 = cornersA[i];
            Vector2 a1 = cornersA[(i + 1) % 4];
            for (int j = 0; j < 4; j++)
            {
                Vector2 b0 = cornersB[j];
                Vector2 b1 = cornersB[(j + 1) % 4];
                minimumSqrDistance = Mathf.Min(
                    minimumSqrDistance,
                    PointToSegmentSqrDistance(a0, b0, b1),
                    PointToSegmentSqrDistance(a1, b0, b1),
                    PointToSegmentSqrDistance(b0, a0, a1),
                    PointToSegmentSqrDistance(b1, a0, a1));
            }
        }

        return Mathf.Sqrt(minimumSqrDistance);
    }

    private static Vector2[] GetFootprintCornersXZ(
        Vector3 center,
        Vector3 size,
        Quaternion rotation)
    {
        float halfX = size.x * 0.5f;
        float halfZ = size.z * 0.5f;
        float[,] signs = { { -1f, -1f }, { 1f, -1f }, { 1f, 1f }, { -1f, 1f } };
        Vector2[] corners = new Vector2[4];

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 offset = rotation * new Vector3(
                signs[i, 0] * halfX,
                0f,
                signs[i, 1] * halfZ);
            corners[i] = new Vector2(center.x + offset.x, center.z + offset.z);
        }

        return corners;
    }

    private static float PointToSegmentSqrDistance(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSqr = segment.sqrMagnitude;
        float t = lengthSqr > 0.000001f
            ? Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSqr)
            : 0f;
        Vector2 closest = start + segment * t;
        return (point - closest).sqrMagnitude;
    }

    private bool IsTooClose(
        Vector3 candidate,
        float spacing)
    {
        float minSpacingSqr = spacing * spacing;

        foreach (Vector3 existing in placedPositions)
        {
            Vector2 a = new Vector2(
                candidate.x,
                candidate.z);

            Vector2 b = new Vector2(
                existing.x,
                existing.z);

            if ((a - b).sqrMagnitude < minSpacingSqr)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsTooCloseToRouteHangar(
        Vector3 candidate,
        float spacing)
    {
        float minSpacingSqr = spacing * spacing;
        Vector2 candidateXZ = new Vector2(candidate.x, candidate.z);

        foreach (Vector3 existing in placedRouteHangarPositions)
        {
            Vector2 existingXZ = new Vector2(existing.x, existing.z);
            if ((candidateXZ - existingXZ).sqrMagnitude < minSpacingSqr)
            {
                return true;
            }
        }

        return false;
    }

    private static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;

        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static float RouteDistanceXZ(
        IReadOnlyList<Vector3> route,
        Vector3 position)
    {
        if (route == null || route.Count == 0)
        {
            return float.MaxValue;
        }

        Vector2 point = new Vector2(
            position.x,
            position.z);

        float nearest = float.MaxValue;

        for (int i = 0; i < route.Count; i++)
        {
            Vector2 routePoint = new Vector2(
                route[i].x,
                route[i].z);

            nearest = Mathf.Min(
                nearest,
                Vector2.Distance(point, routePoint));
        }

        return nearest;
    }

    private static Transform FindChildRecursive(
        Transform parent,
        string childName)
    {
        if (parent == null || string.IsNullOrEmpty(childName))
        {
            return null;
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);

            if (child.name == childName)
            {
                return child;
            }

            Transform nested = FindChildRecursive(child, childName);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void DestroyTemporary(GameObject temporary)
    {
        if (temporary == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(temporary);
        }
        else
        {
            DestroyImmediate(temporary);
        }
    }
}
