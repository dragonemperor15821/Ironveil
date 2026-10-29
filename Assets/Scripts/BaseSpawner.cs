using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

public class BaseSpawner : MonoBehaviour
{
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
        public float hangarRouteDistance;
        public float rootRouteDistance;
        public Vector3 rootPosition;
        public Quaternion rootRotation;
        public float footprintHeightVariation;
        public float footprintMaxSlope;
        public string rejectReason;
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

    [Header("Terrain")]
    [SerializeField] private Collider terrainCollider;

    [Header("Route Generators")]
    [SerializeField] private RiverGenerator riverGenerator;
    [SerializeField] private RailGenerator railGenerator;

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
        "Clear gap between the Naval Hangar's landward footprint and the " +
        "main-base footprint. The local river width is derived from the " +
        "hangar's measured length.")]
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
    [SerializeField, Min(0f)]
    private float terrainSampleRadius = 0.55f;

    [Tooltip(
        "Number of terrain sample points taken around the footprint.")]
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

    private readonly List<int> registeredRiverNarrowingIds =
        new List<int>();

    // Anchor caches keyed by prefab (Unity Object reference works as key).
    private readonly Dictionary<GameObject, PrefabAnchor> navalAnchorCache =
        new Dictionary<GameObject, PrefabAnchor>();

    private readonly Dictionary<GameObject, PrefabAnchor> railAnchorCache =
        new Dictionary<GameObject, PrefabAnchor>();

    private readonly Dictionary<GameObject, PrefabFootprint> baseFootprintCache =
        new Dictionary<GameObject, PrefabFootprint>();

    // Last-placed data for editor gizmos.
    private readonly List<PlacementValidation> lastPlacements =
        new List<PlacementValidation>();

    private Vector2[] clusters;

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
        lastPlacements.Clear();

        CreateClusters();

        List<BaseRequest> requests = BuildRequests();
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
                            ? $", local river width {validation.localRiverWidth:F2} m"
                            : string.Empty) +
                        (validation.valid ? ", validated." : ", NOT validated."),
                        this);
                }

                success = true;
                break;
            }

            if (!success)
            {
                failed++;

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
                    $"Rejection reasons: {reasons}",
                    this);
            }
        }

        Random.state = previousRandomState;

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

        Debug.Log(
            $"IRONVEIL: Spawned {spawned}/{requests.Count} requested " +
            $"bases. Failed placements: {failed}. {summary}",

            this);

        if (shortfallByType.Count > 0)
        {
            string shortfalls = string.Join(
                ", ",
                shortfallByType.Select(
                    pair => $"{pair.Key} x{pair.Value}"));

            Debug.LogWarning(
                $"BaseSpawner: Route-validation shortfalls: {shortfalls}. " +
                "These bases instantiated but did not pass all " +
                "spatial validation checks.",
                this);
        }

        if (rejectReasonsTotal.Count > 0 && verboseLogging)
        {
            string allReasons = string.Join(
                ", ",
                rejectReasonsTotal.Select(
                    pair => $"{pair.Key} x{pair.Value}"));

            Debug.Log(
                $"BaseSpawner: Total rejection reasons across all types: {allReasons}",
                this);
        }
    }

    private bool ValidateSetup()
    {
        if (terrainCollider == null)
        {
            Debug.LogError(
                "BaseSpawner: Assign the terrain collider.",
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
                    index = i
                });
            }
        }

        return requests;
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
                return TryFindRiverPlacement(
                    request.prefab,
                    navalHangarPrefab,
                    riverGenerator.GeneratedRoute,
                    out pose,
                    out validation);

            case PlacementType.Rail:
                return TryFindRailPlacement(
                    request.prefab,
                    railHangarPrefab,
                    railGenerator.GeneratedRoute,
                    out pose,
                    out validation);

            default:
                validation = default(PlacementValidation);
                validation.rejectReason = string.Empty;

                if (!TryFindLandPlacement(
                    request.prefab,
                    out Vector3 landPosition,
                    out Quaternion landRotation))
                {
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
        int riverNarrowingId = 0;

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

            if (request.placement == PlacementType.River)
            {
                if (riverGenerator != null &&
                    validation.localRiverWidth < riverGenerator.RiverWidth &&
                    !riverGenerator.TryAddLocalNarrowing(
                        validation.routeIndex,
                        validation.localRiverWidth,
                        validation.riverNarrowingTaperHalfDistance,
                        out riverNarrowingId))
                {
                    rejectReason = "could not narrow river at naval base location";
                    DestroyTemporary(generatedRoot);
                    return false;
                }

                if (riverGenerator == null && validation.localRiverWidth > 0f)
                {
                    rejectReason = "river generator missing for naval narrowing";
                    DestroyTemporary(generatedRoot);
                    return false;
                }

                if (riverNarrowingId != 0)
                {
                    registeredRiverNarrowingIds.Add(riverNarrowingId);
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            rejectReason = "instantiation failed";
            if (riverNarrowingId != 0 && riverGenerator != null)
            {
                riverGenerator.RemoveLocalNarrowings(
                    new[] { riverNarrowingId });
                registeredRiverNarrowingIds.Remove(riverNarrowingId);
            }

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
        GameObject prefab,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

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
            return false;
        }

        Vector3 worldXZ = new Vector3(
            transform.position.x + x,
            0f,
            transform.position.z + z);

        // Keep ordinary land bases away from the river.
        if (riverGenerator != null &&
            RouteDistanceXZ(
                riverGenerator.GeneratedRoute,
                worldXZ) <
            riverGenerator.RiverWidth * 0.5f +
            landRiverClearance)
        {
            return false;
        }

        // Sample the terrain across the base footprint, not just at the
        // pivot. Reject uneven or steep locations before placing.
        if (!SampleTerrainFootprint(
            worldXZ,
            terrainSampleRadius,
            terrainSampleCount,
            out TerrainSampleResult sample))
        {
            return false;
        }

        if (sample.heightVariation > maxFootprintHeightVariation)
        {
            return false;
        }

        if (sample.maxSlopeAngle > maxSlopeAngle)
        {
            return false;
        }

        if (IsTooClose(sample.position, minSpacing))
        {
            return false;
        }

        position = sample.position + sample.normal * surfaceOffset;

        // Terrain-aligned rotation: conform to the sampled ground normal
        // while preserving the prefab's authored orientation. Tilt is
        // clamped so the base never leans excessively.
        Quaternion terrainAlign =
            Quaternion.FromToRotation(Vector3.up, sample.normal);

        float tilt = Quaternion.Angle(Quaternion.identity, terrainAlign);
        if (tilt > maxBaseTiltAngle && tilt > 0.0001f)
        {
            terrainAlign =
                Quaternion.Slerp(
                    Quaternion.identity,
                    terrainAlign,
                    maxBaseTiltAngle / tilt);
        }

        rotation =
            terrainAlign *
            prefab.transform.rotation *
            Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        return true;
    }

    private static bool TryGetRouteSample(
        IReadOnlyList<Vector3> route,
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

        // Avoid route endpoints.
        index = Random.Range(1, route.Count - 1);

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
            out int index,
            out Vector3 routePoint,
            out Vector3 tangent,
            out string rejectReason))
        {
            validation.rejectReason = rejectReason;
            return false;
        }

        if (!TryGetPrefabAnchor(
            hangarPrefab,
            "Rail Hangar",
            false,
            out PrefabAnchor anchor))
        {
            validation.rejectReason = "missing Rail Hangar model child";
            return false;
        }

        if (!TryGetPrefabFootprint(
            basePrefab,
            out PrefabFootprint baseFootprint))
        {
            validation.rejectReason = "main base has no measurable renderer bounds";
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

        float corridorHalfWidth = railGenerator != null
            ? railGenerator.RailCorridorHalfWidth
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
        if (!SampleTerrainFootprint(
            routePoint,
            terrainSampleRadius,
            terrainSampleCount,
            out TerrainSampleResult hangarTerrain))
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
        if (!SampleTerrainFootprint(
            baseGeometryCenter,
            terrainSampleRadius,
            terrainSampleCount,
            out TerrainSampleResult sample))
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
        float railGauge = railGenerator != null ? railGenerator.RailGauge : 0f;
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
            out int index,
            out Vector3 routePoint,
            out Vector3 tangent,
            out string rejectReason))
        {
            validation.rejectReason = rejectReason;
            return false;
        }

        if (!TryGetPrefabAnchor(
            hangarPrefab,
            "Naval Hangar",
            true,
            out PrefabAnchor anchor))
        {
            validation.rejectReason = "missing Naval Hangar model child";
            return false;
        }

        if (!TryGetPrefabFootprint(
            basePrefab,
            out PrefabFootprint baseFootprint))
        {
            validation.rejectReason = "main base has no measurable renderer bounds";
            return false;
        }

        if (!baseFootprint.hasTurretFacing)
        {
            validation.rejectReason = "main base has no measurable turret-facing side";
            return false;
        }

        // Pick which bank the main base sits on. The base faces the water;
        // the hangar's measured long axis is aligned across the channel.
        bool usePositiveBankSide = Random.value < 0.5f;

        float localRiverWidth = Mathf.Min(
            riverGenerator != null ? riverGenerator.RiverWidth : 6f,
            anchor.trackAxisLength);
        if (localRiverWidth <= 0.01f)
        {
            validation.rejectReason = "Naval Hangar cannot define a local river width";
            return false;
        }

        // Local width changes are attached to a river mesh route vertex.
        // Preserve the seeded along-route variation by snapping the old jitter
        // to a nearby route sample instead of offsetting away from the mesh.
        float routeStep = Vector3.Distance(
            route[index - 1],
            route[index + 1]) * 0.5f;
        int jitterSteps = Mathf.RoundToInt(
            Random.Range(-riverRouteJitter, riverRouteJitter) /
            Mathf.Max(routeStep, 0.01f));
        index = Mathf.Clamp(index + jitterSteps, 1, route.Count - 2);
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

        if (anchor.trackAxisLength <= 0.001f)
        {
            validation.rejectReason = "Naval Hangar has no measurable horizontal length";
            return false;
        }

        Vector3 hangarGeometryCenter =
            routePoint + bankSide * riverHalfWidth;
        Vector3 hangarRootPosition =
            hangarGeometryCenter - hangarRotation * anchor.boundsCenter;
        float hangarBottomLocalY =
            anchor.boundsCenter.y - anchor.boundsSize.y * 0.5f;
        hangarRootPosition.y =
            routePoint.y - navalWaterlineOffset - hangarBottomLocalY;

        Vector3 hangarWorld =
            hangarRootPosition + hangarRotation * anchor.boundsCenter;
        Quaternion hangarWorldRotation = hangarRotation * anchor.localRotation;

        float hangarHalfAlongBank = ProjectedHalfExtent(
            anchor.boundsSize,
            hangarRotation,
            bankSide);
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
        float baseCenterDistance =
            riverHalfWidth + hangarHalfAlongBank + baseHalfAlongBank +
            navalBankPadClearance;
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

        // Sample the terrain across the footprint. Naval bases keep their
        // river-facing orientation and are never tilted to conform to a
        // bank slope: bad terrain is rejected and another route point is tried.
        if (!SampleTerrainFootprint(
            baseGeometryCenter,
            terrainSampleRadius,
            terrainSampleCount,
            out TerrainSampleResult sample))
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

        // Place the measured main-base bounds on the sampled bank terrain.
        Vector3 position =
            sample.position - baseRotation * baseFootprint.boundsCenter;
        position.y = sample.contactHeight + surfaceOffset -
            baseFootprint.boundsCenter.y + baseFootprint.boundsSize.y * 0.5f;

        if (IsTooClose(position, routeBaseMinSpacing))
        {
            validation.rejectReason = "too close to another base";
            return false;
        }

        float hangarDistance = DistanceXZ(hangarWorld, routePoint);

        float hangarMinRouteSpacing = Mathf.Max(
            routeBaseMinSpacing,
            hangarHalfAlongRoute * 2f + navalBankPadClearance);
        if (IsTooCloseToRouteHangar(hangarWorld, hangarMinRouteSpacing))
        {
            validation.rejectReason = "too close to another route hangar";
            return false;
        }

        // The hangar must be over water: its centre within the river
        // corridor (|distance| <= riverHalfWidth). The hangar extends from
        // the base (on land) toward the water, so it is on the same side of
        // the centreline as the base.
        bool hangarOverWater =
            Mathf.Abs(hangarDistance) <= riverHalfWidth;

        // Additional check: a meaningful portion of the hangar's visible
        // bounds must lie over the water, not just the anchor point.
        bool hangarBoundsOverWater = false;
        if (riverGenerator != null && anchor.hasRenderer)
        {
            Bounds hangarWorldBounds = ComputeWorldHangarBounds(
                hangarRootPosition,
                hangarRotation,
                anchor,
                1f);

            hangarBoundsOverWater = BoundsOverlapRiver(
                hangarWorldBounds,
                riverGenerator.GeneratedRoute,
                riverHalfWidth,
                0.25f);
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
        validation.riverNarrowingTaperHalfDistance =
            Mathf.Max(routeBaseMinSpacing * 0.5f, riverRouteJitter);
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
        IReadOnlyList<Vector3> route,
        float riverHalfWidth,
        float minOverWaterFraction)
    {
        if (route == null || route.Count == 0)
        {
            return false;
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

                float nearest = float.MaxValue;
                for (int i = 0; i < route.Count; i++)
                {
                    float d = DistanceXZ(pt, route[i]);
                    if (d < nearest)
                    {
                        nearest = d;
                    }
                }

                if (nearest <= riverHalfWidth)
                {
                    overWater++;
                }
            }
        }

        return (float)overWater / sampleCount >= minOverWaterFraction;
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
        Vector3 rootXZ,
        float radius,
        int count,
        out TerrainSampleResult result)
    {
        result = default(TerrainSampleResult);

        if (radius <= 0f || count < 1)
        {
            return false;
        }

        var heights = new List<float>(count + 1);
        var normals = new List<Vector3>(count + 1);

        // Sample points evenly around a circle at the given radius.
        for (int i = 0; i < count; i++)
        {
            float angle = 2f * Mathf.PI * i / count;
            float rx = rootXZ.x + Mathf.Cos(angle) * radius;
            float rz = rootXZ.z + Mathf.Sin(angle) * radius;

            if (!TryGetTerrainHit(new Vector3(rx, 0f, rz), out RaycastHit hit))
            {
                return false;
            }

            heights.Add(hit.point.y);
            normals.Add(hit.normal);
        }

        // Also sample the footprint centre.
        if (!TryGetTerrainHit(rootXZ, out RaycastHit centerHit))
        {
            return false;
        }

        heights.Add(centerHit.point.y);
        normals.Add(centerHit.normal);

        float minH = heights[0];
        float maxH = heights[0];
        foreach (float h in heights)
        {
            if (h < minH) minH = h;
            if (h > maxH) maxH = h;
        }

        Vector3 avgNormal = Vector3.zero;
        foreach (Vector3 n in normals) avgNormal += n;
        avgNormal.Normalize();

        float maxSlope = 0f;
        foreach (Vector3 n in normals)
        {
            float a = Vector3.Angle(n, Vector3.up);
            if (a > maxSlope) maxSlope = a;
        }

        // Recommended contact height: the mean of samples so the base sits
        // flush with the average ground rather than halfway between extremes
        // (which would bury half and float half on slopes).
        float contactHeight = 0f;
        foreach (float h in heights) contactHeight += h;
        contactHeight /= heights.Count;

        result.position = new Vector3(
            rootXZ.x,
            contactHeight,
            rootXZ.z);
        result.normal = avgNormal;
        result.heightVariation = maxH - minH;
        result.maxSlopeAngle = maxSlope;
        result.contactHeight = contactHeight;
        result.isValid = true;

        return true;
    }

    private bool TryGetTerrainHit(
        Vector3 worldXZ,
        out RaycastHit hit)
    {
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
