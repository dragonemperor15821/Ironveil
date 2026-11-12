using UnityEngine;

/// <summary>
/// Tunable, seed-based settings for the isolated IRONVEIL terrain prototype.
/// This asset is deliberately independent of the production route generators
/// and BaseSpawner.
/// </summary>
[CreateAssetMenu(
    fileName = "IronveilWorldGenerationPrototype",
    menuName = "IRONVEIL/World Generation/Prototype Configuration")]
public sealed class WorldGenerationPrototypeConfig : ScriptableObject
{
    [Header("World Identity")]
    [SerializeField] private int worldSeed = 12345;
    [SerializeField] private bool generateOnPlay = true;

    [Header("World and Terrain Chunks")]
    [SerializeField] private Vector2 mapSize = new Vector2(1200f, 1200f);
    [SerializeField, Min(1)] private int chunksX = 4;
    [SerializeField, Min(1)] private int chunksZ = 4;
    [Tooltip("Unity Terrain heightmap resolution. Use a 2^n + 1 resolution.")]
    [SerializeField, Min(33)] private int heightmapResolution = 513;
    [SerializeField, Min(32f)] private float terrainHeight = 280f;
    [SerializeField, Min(0f)] private float baseElevation = 12f;
    [SerializeField, Min(32)] private int alphamapResolution = 256;
    [SerializeField, Min(8f)] private float snowTextureTileSize = 64f;
    [SerializeField] private Texture2D snowAlbedo;
    [SerializeField] private Texture2D snowNormal;
    [SerializeField] private Material snowGroundMaterial;
    [Tooltip("Terrain material for players (e.g. Web). URP only provides its default terrain material in the Editor, so runtime-created terrain chunks get this material when the pipeline has no default. Use URP's TerrainLit material (the Editor default) to keep the look identical.")]
    [SerializeField] private Material runtimeTerrainMaterial;

    [Header("Macro Geography")]
    [SerializeField, Range(1, 5)] private int plainCount = 5;
    [SerializeField, Min(16f)] private float plainRadius = 115f;

    [Header("River Corridor Guide Data (not a generated river mesh)")]
    [SerializeField, Range(1, 5)] private int riverGuideCount = 3;
    [SerializeField, Min(1f)] private float riverValleyHalfWidth = 48f;
    [SerializeField, Min(1f)] private float riverValleyShoulder = 72f;
    [SerializeField, Min(0f)] private float riverValleyDepth = 26f;
    [SerializeField, Min(0.1f)] private float guideRiverWidth = 3f;

    [Header("Rail Corridor Guide Data (not generated track geometry)")]
    [SerializeField, Range(1, 4)] private int railGuideCount = 2;
    [SerializeField, Min(1f)] private float railValleyHalfWidth = 32f;
    [SerializeField, Min(1f)] private float railValleyShoulder = 48f;
    [SerializeField, Min(0f)] private float railValleyDepth = 14f;

    [Header("Route Planning (TerrainV2: seeded U/S/Z/meander routes)")]
    [Tooltip("Seeded candidates tried per route before the straight-lane fallback.")]
    [SerializeField, Range(4, 400)] private int routeCandidateAttempts = 120;
    [SerializeField, Min(10f)] private float minimumRiverCurveRadius = 60f;
    [SerializeField, Min(10f)] private float minimumRailCurveRadius = 120f;
    [Tooltip("Minimum centreline separation between two rivers (valleys plus naval bases on both).")]
    [SerializeField, Min(10f)] private float riverRiverSeparation = 100f;
    [Tooltip("Minimum centreline separation between a railway and a river.")]
    [SerializeField, Min(10f)] private float railRiverSeparation = 110f;
    [SerializeField, Min(10f)] private float railRailSeparation = 80f;
    [Tooltip("Minimum distance of a route centreline from the map sides it runs along.")]
    [SerializeField, Min(0f)] private float routeEdgeMargin = 40f;
    [Tooltip("Steepest allowed railway grade (rise/run) after terrain projection.")]
    [SerializeField, Range(0.001f, 0.1f)] private float maximumRailGrade = 0.02f;
    [Tooltip("Allow a U route in an outer lane to enter and leave through the same side edge. Off: every route crosses the map from one side to the opposite side.")]
    [SerializeField] private bool allowEdgeRoutes = false;
    [Tooltip("Every lane route must swing at least this far sideways (visible curvature). Formerly a fixed 160 m.")]
    [SerializeField, Min(0f)] private float minimumRouteSwing = 160f;
    [Tooltip("Routes whose mean distance is below this AND whose distance deviation is below Parallel Route Deviation count as running in parallel and are redrawn. Formerly fixed 400 m / 40 m.")]
    [SerializeField, Min(0f)] private float parallelRouteDistance = 400f;
    [SerializeField, Min(0f)] private float parallelRouteDeviation = 40f;

    [Header("Isolated Hills (TerrainV2, own seeded stream)")]
    [Tooltip("Seeded hill count is drawn in [min, max].")]
    [SerializeField, Min(0)] private int hillCountMin = 20;
    [SerializeField, Min(0)] private int hillCountMax = 30;
    [Tooltip("Outer radius where the smooth hill profile returns to the surrounding ground.")]
    [SerializeField, Min(5f)] private float hillRadiusMin = 50f;
    [SerializeField, Min(5f)] private float hillRadiusMax = 90f;
    [Tooltip("Hill height = radius x a seeded ratio in [min, max]. Steepest slope = 1.5 x ratio (0.08 -> 6.8 deg).")]
    [SerializeField, Range(0f, 0.2f)] private float hillHeightToRadiusMin = 0.06f;
    [SerializeField, Range(0f, 0.2f)] private float hillHeightToRadiusMax = 0.08f;
    [Tooltip("Optional absolute hill height range in metres. When Max > 0 it replaces the height-to-radius ratio (same seeded draw, so hill positions and radii are unchanged). 0 = use the ratio.")]
    [SerializeField, Min(0f)] private float hillHeightMin = 0f;
    [SerializeField, Min(0f)] private float hillHeightMax = 0f;
    [Tooltip("Hill centres are at least this factor x (r1 + r2) apart, so hills never connect.")]
    [SerializeField, Min(1f)] private float hillSeparationFactor = 1.3f;
    [Tooltip("Extra gap between a hill's outer radius and a river/rail valley floor.")]
    [SerializeField, Min(0f)] private float hillRouteClearance = 10f;

    [Header("Independent Terrain Detail Stream")]
    [SerializeField, Min(0f)] private float detailAmplitude = 2.2f;
    [SerializeField, Min(0.0001f)] private float detailFrequency = 0.008f;
    [SerializeField, Min(0f)] private float fineDetailAmplitude = 0.45f;
    [SerializeField, Min(0.0001f)] private float fineDetailFrequency = 0.035f;

    [Header("Placement Screening — MainBuild Baseline")]
    [SerializeField, Min(0f)] private float edgePadding = 8f;
    [SerializeField, Min(0.1f)] private float landSpacing = 10f;
    [SerializeField, Min(0.1f)] private float routeSpacing = 4.5f;
    [SerializeField, Min(0f)] private float landRiverClearance = 15f;
    [SerializeField, Min(0f)] private float maximumFootprintVariation = 0.3f;
    [SerializeField, Range(0f, 90f)] private float maximumSlope = 30f;
    [SerializeField, Range(0f, 90f)] private float maximumLandTilt = 20f;
    [SerializeField, Min(0f)] private float navalHangarEdgeClearance = 7.5f;
    [SerializeField, Min(0f)] private float navalBankPadClearance = 0.6f;
    [SerializeField, Range(0f, 1f)] private float navalHangarTargetWaterOverlap = 0.85f;
    [SerializeField, Min(0f)] private float railHangarTolerance = 0.35f;
    [SerializeField, Min(0f)] private float railBaseHangarClearance = 0.5f;
    [SerializeField, Min(0f)] private float railGauge = 0.09f;
    [SerializeField, Min(0f)] private float railWidth = 0.015f;
    [SerializeField, Min(0f)] private float sleeperLength = 0.11f;
    [SerializeField, Min(0f)] private float railRiverClearance = 18f;

    [Header("Measured Prefab Footprints for Screening")]
    [Tooltip("Tank, Heli, and Air prefabs; their renderer bounds are measured at runtime.")]
    [SerializeField] private GameObject[] landBasePrefabs;
    [Tooltip("Naval, Heli Naval, and Air Naval prefabs.")]
    [SerializeField] private GameObject[] navalBasePrefabs;
    [Tooltip("Rail, Heli Rail, and Air Rail prefabs.")]
    [SerializeField] private GameObject[] railBasePrefabs;
    [SerializeField] private GameObject navalHangarPrefab;
    [SerializeField] private GameObject railHangarPrefab;

    [Header("Capacity Targets")]
    [SerializeField, Min(1)] private int landCapacityTarget = 322;
    [SerializeField, Min(1)] private int navalCapacityTarget = 53;
    [SerializeField, Min(1)] private int railCapacityTarget = 35;

    public int WorldSeed => worldSeed;
    public bool GenerateOnPlay => generateOnPlay;
    public Vector2 MapSize => mapSize;
    public int ChunksX => chunksX;
    public int ChunksZ => chunksZ;
    public int HeightmapResolution => heightmapResolution;
    public float TerrainHeight => terrainHeight;
    public float BaseElevation => baseElevation;
    public int AlphamapResolution => alphamapResolution;
    public float SnowTextureTileSize => snowTextureTileSize;
    public Texture2D SnowAlbedo => snowAlbedo;
    public Texture2D SnowNormal => snowNormal;
    public Material SnowGroundMaterial => snowGroundMaterial;
    public Material RuntimeTerrainMaterial => runtimeTerrainMaterial;
    public int PlainCount => plainCount;
    public float PlainRadius => plainRadius;
    public int RiverGuideCount => riverGuideCount;
    public float RiverValleyHalfWidth => riverValleyHalfWidth;
    public float RiverValleyShoulder => riverValleyShoulder;
    public float RiverValleyDepth => riverValleyDepth;
    public float GuideRiverWidth => guideRiverWidth;
    public int RailGuideCount => railGuideCount;
    public float RailValleyHalfWidth => railValleyHalfWidth;
    public float RailValleyShoulder => railValleyShoulder;
    public float RailValleyDepth => railValleyDepth;
    public int RouteCandidateAttempts => routeCandidateAttempts;
    public float MinimumRiverCurveRadius => minimumRiverCurveRadius;
    public float MinimumRailCurveRadius => minimumRailCurveRadius;
    public float RiverRiverSeparation => riverRiverSeparation;
    public float RailRiverSeparation => railRiverSeparation;
    public float RailRailSeparation => railRailSeparation;
    public float RouteEdgeMargin => routeEdgeMargin;
    public float MaximumRailGrade => maximumRailGrade;
    public bool AllowEdgeRoutes => allowEdgeRoutes;
    public float MinimumRouteSwing => minimumRouteSwing;
    public float ParallelRouteDistance => parallelRouteDistance;
    public float ParallelRouteDeviation => parallelRouteDeviation;
    public int HillCountMin => hillCountMin;
    public int HillCountMax => Mathf.Max(hillCountMin, hillCountMax);
    public float HillRadiusMin => hillRadiusMin;
    public float HillRadiusMax => Mathf.Max(hillRadiusMin, hillRadiusMax);
    public float HillHeightToRadiusMin => hillHeightToRadiusMin;
    public float HillHeightToRadiusMax => Mathf.Max(hillHeightToRadiusMin, hillHeightToRadiusMax);
    public bool UseAbsoluteHillHeight => hillHeightMax > 0f;
    public float HillHeightMin => Mathf.Min(hillHeightMin, hillHeightMax);
    public float HillHeightMax => hillHeightMax;
    public float HillSeparationFactor => hillSeparationFactor;
    public float HillRouteClearance => hillRouteClearance;
    public float DetailAmplitude => detailAmplitude;
    public float DetailFrequency => detailFrequency;
    public float FineDetailAmplitude => fineDetailAmplitude;
    public float FineDetailFrequency => fineDetailFrequency;
    public float EdgePadding => edgePadding;
    public float LandSpacing => landSpacing;
    public float RouteSpacing => routeSpacing;
    public float LandRiverClearance => landRiverClearance;
    public float MaximumFootprintVariation => maximumFootprintVariation;
    public float MaximumSlope => maximumSlope;
    public float MaximumLandTilt => maximumLandTilt;
    public float NavalHangarEdgeClearance => navalHangarEdgeClearance;
    public float NavalBankPadClearance => navalBankPadClearance;
    public float NavalHangarTargetWaterOverlap => navalHangarTargetWaterOverlap;
    public float RailHangarTolerance => railHangarTolerance;
    public float RailBaseHangarClearance => railBaseHangarClearance;
    public float RailGauge => railGauge;
    public float RailWidth => railWidth;
    public float SleeperLength => sleeperLength;
    public float RailRiverClearance => railRiverClearance;
    public GameObject[] LandBasePrefabs => landBasePrefabs;
    public GameObject[] NavalBasePrefabs => navalBasePrefabs;
    public GameObject[] RailBasePrefabs => railBasePrefabs;
    public GameObject NavalHangarPrefab => navalHangarPrefab;
    public GameObject RailHangarPrefab => railHangarPrefab;
    public int LandCapacityTarget => landCapacityTarget;
    public int NavalCapacityTarget => navalCapacityTarget;
    public int RailCapacityTarget => railCapacityTarget;
}
