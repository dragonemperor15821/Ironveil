
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RiverGenerator : MonoBehaviour
{
    private struct LocalNarrowing
    {
        public int id;
        public int routeIndex;
        public float width;
        public float taperHalfDistance;
    }

    [Header("Terrain")]
    [SerializeField] private Collider terrainCollider;

    [Header("River Appearance")]
    [SerializeField] private Material riverMaterial;
    [SerializeField, Min(1f)] private float riverWidth = 6f;
    [SerializeField, Min(20)] private int pointCount = 160;
    [SerializeField] private float surfaceOffset = 0.12f;

    // Public access for naval base placement.
    public float RiverWidth => riverWidth;

    [Header("Route")]
    [SerializeField] private int seed = 12345;
    [SerializeField] private Vector2 mapCenter = Vector2.zero;
    [SerializeField] private Vector2 mapSize = new Vector2(400f, 400f);
    [SerializeField, Min(1f)] private float edgePadding = 15f;
    [SerializeField, Min(0f)] private float bendAmount = 50f;
    [SerializeField, Range(3, 12)] private int routeControlCount = 9;
    [SerializeField, Range(0f, 1f)] private float windingAmount = 0.65f;

    [Header("Generation")]
    [SerializeField] private bool generateOnStart = false;

    private Mesh generatedMesh;
    private readonly List<Vector3> generatedRoute = new List<Vector3>();
    private readonly List<float> cumulativeRouteDistances = new List<float>();
    private readonly List<LocalNarrowing> localNarrowings =
        new List<LocalNarrowing>();
    private int nextNarrowingId = 1;

    // Public access to the actual generated river route.
    public IReadOnlyList<Vector3> GeneratedRoute => generatedRoute;

    // Adds a local width profile without changing the generated route or the
    // global RiverWidth used by ordinary land placement and route generation.
    public bool TryAddLocalNarrowing(
        int routeIndex,
        float width,
        float taperHalfDistance,
        out int narrowingId)
    {
        narrowingId = 0;

        if (generatedRoute.Count < 3 ||
            routeIndex <= 0 ||
            routeIndex >= generatedRoute.Count - 1 ||
            width <= 0f ||
            width >= riverWidth ||
            taperHalfDistance <= 0f)
        {
            return false;
        }

        LocalNarrowing narrowing = new LocalNarrowing
        {
            id = nextNarrowingId++,
            routeIndex = routeIndex,
            width = width,
            taperHalfDistance = taperHalfDistance
        };

        localNarrowings.Add(narrowing);
        narrowingId = narrowing.id;
        RebuildRiverMesh();
        return true;
    }

    public void RemoveLocalNarrowings(IReadOnlyList<int> narrowingIds)
    {
        if (narrowingIds == null || narrowingIds.Count == 0 ||
            localNarrowings.Count == 0)
        {
            return;
        }

        HashSet<int> ids = new HashSet<int>(narrowingIds);
        int removed = localNarrowings.RemoveAll(
            narrowing => ids.Contains(narrowing.id));

        if (removed > 0)
        {
            RebuildRiverMesh();
        }
    }

    private void Start()
    {
        if (generateOnStart)
            GenerateRiver();
    }

    [ContextMenu("Generate River")]
    public void GenerateRiver()
    {
        if (terrainCollider == null || riverMaterial == null)
        {
            Debug.LogError(
                "RiverGenerator: Assign Terrain Collider and River Material.",
                this);
            return;
        }

        if (mapSize.x <= edgePadding * 2f ||
            mapSize.y <= edgePadding * 2f ||
            pointCount < 2 ||
            routeControlCount < 3)
        {
            Debug.LogError(
                "RiverGenerator: Check map size, point count and controls.",
                this);
            return;
        }

        Random.State previousRandomState = Random.state;
        Random.InitState(seed);

        generatedRoute.Clear();
        generatedRoute.AddRange(BuildRoute());

        localNarrowings.Clear();
        RebuildCumulativeRouteDistances();
        RebuildRiverMesh();

        Random.state = previousRandomState;

        Debug.Log(
            $"RiverGenerator: Generated river with {generatedRoute.Count} route points using seed {seed}.",
            this);
    }

    private void RebuildRiverMesh()
    {
        if (generatedRoute.Count < 2)
        {
            return;
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();

        for (int i = 0; i < generatedRoute.Count; i++)
        {
            Vector3 tangent = GetTangent(generatedRoute, i);
            Vector3 side = Vector3.Cross(Vector3.up, tangent).normalized;
            float localWidth = GetRiverWidthAtIndex(i);

            Vector3 left = generatedRoute[i] - side * localWidth * 0.5f;
            Vector3 right = generatedRoute[i] + side * localWidth * 0.5f;

            vertices.Add(transform.InverseTransformPoint(left));
            vertices.Add(transform.InverseTransformPoint(right));

            float lengthUV = i / (float)(generatedRoute.Count - 1) * 12f;
            uvs.Add(new Vector2(0f, lengthUV));
            uvs.Add(new Vector2(1f, lengthUV));

            if (i < generatedRoute.Count - 1)
            {
                int v = i * 2;

                triangles.Add(v);
                triangles.Add(v + 2);
                triangles.Add(v + 1);

                triangles.Add(v + 1);
                triangles.Add(v + 2);
                triangles.Add(v + 3);
            }
        }

        Mesh mesh = new Mesh
        {
            name = "GeneratedRiverMesh",
            indexFormat = vertices.Count > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16
        };

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetUVs(0, uvs);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        ReplaceGeneratedMesh(mesh);

        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = riverMaterial;
    }

    private void RebuildCumulativeRouteDistances()
    {
        cumulativeRouteDistances.Clear();
        float distance = 0f;

        for (int i = 0; i < generatedRoute.Count; i++)
        {
            if (i > 0)
            {
                distance += Vector3.Distance(
                    generatedRoute[i - 1],
                    generatedRoute[i]);
            }

            cumulativeRouteDistances.Add(distance);
        }
    }

    private float GetRiverWidthAtIndex(int routeIndex)
    {
        float width = riverWidth;
        float routeDistance = cumulativeRouteDistances[routeIndex];

        foreach (LocalNarrowing narrowing in localNarrowings)
        {
            float centerDistance =
                cumulativeRouteDistances[narrowing.routeIndex];
            float normalizedDistance = Mathf.Clamp01(
                Mathf.Abs(routeDistance - centerDistance) /
                narrowing.taperHalfDistance);
            float smoothT = normalizedDistance * normalizedDistance *
                            (3f - 2f * normalizedDistance);
            float candidateWidth = Mathf.Lerp(
                narrowing.width,
                riverWidth,
                smoothT);

            width = Mathf.Min(width, candidateWidth);
        }

        return width;
    }

    private List<Vector3> BuildRoute()
    {
        float minX = mapCenter.x - mapSize.x * 0.5f + edgePadding;
        float maxX = mapCenter.x + mapSize.x * 0.5f - edgePadding;
        float minZ = mapCenter.y - mapSize.y * 0.5f + edgePadding;
        float maxZ = mapCenter.y + mapSize.y * 0.5f - edgePadding;

        Vector3 start;
        Vector3 end;

        // Start and end on opposite edges.
        if (Random.value < 0.5f)
        {
            start = new Vector3(minX, 0f, Random.Range(minZ, maxZ));
            end = new Vector3(maxX, 0f, Random.Range(minZ, maxZ));
        }
        else
        {
            start = new Vector3(Random.Range(minX, maxX), 0f, minZ);
            end = new Vector3(Random.Range(minX, maxX), 0f, maxZ);
        }

        Vector3 forward = (end - start).normalized;
        Vector3 sideways = new Vector3(-forward.z, 0f, forward.x);

        List<Vector3> controls = new List<Vector3> { start };

        for (int i = 1; i < routeControlCount - 1; i++)
        {
            float t = i / (float)(routeControlCount - 1);
            float envelope = Mathf.Sin(t * Mathf.PI);

            Vector3 p = Vector3.Lerp(start, end, t);

            p += sideways *
                 Random.Range(-bendAmount, bendAmount) *
                 windingAmount * envelope;

            p += forward *
                 Random.Range(-bendAmount * 0.25f, bendAmount * 0.25f) *
                 windingAmount * envelope;

            p.x = Mathf.Clamp(p.x, minX, maxX);
            p.z = Mathf.Clamp(p.z, minZ, maxZ);

            controls.Add(p);
        }

        controls.Add(end);

        List<Vector3> route = new List<Vector3>(pointCount);

        for (int i = 0; i < pointCount; i++)
        {
            float t = i / (float)(pointCount - 1);
            route.Add(ProjectOntoTerrain(EvaluateSpline(controls, t)));
        }

        return route;
    }

    private Vector3 ProjectOntoTerrain(Vector3 point)
    {
        Bounds bounds = terrainCollider.bounds;

        Ray ray = new Ray(
            new Vector3(point.x, bounds.max.y + 100f, point.z),
            Vector3.down);

        if (terrainCollider.Raycast(
            ray, out RaycastHit hit, bounds.size.y + 200f))
        {
            point.y = hit.point.y + surfaceOffset;
        }
        else
        {
            point.y = bounds.min.y + surfaceOffset;
        }

        return point;
    }

    private static Vector3 EvaluateSpline(List<Vector3> points, float t)
    {
        float scaled = Mathf.Clamp01(t) * (points.Count - 1);
        int i = Mathf.Min(Mathf.FloorToInt(scaled), points.Count - 2);
        float u = scaled - i;

        return CatmullRom(
            points[Mathf.Max(0, i - 1)],
            points[i],
            points[i + 1],
            points[Mathf.Min(points.Count - 1, i + 2)],
            u);
    }

    private static Vector3 CatmullRom(
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static Vector3 GetTangent(List<Vector3> points, int index)
    {
        Vector3 tangent;

        if (index == 0)
            tangent = points[1] - points[0];
        else if (index == points.Count - 1)
            tangent = points[index] - points[index - 1];
        else
            tangent = points[index + 1] - points[index - 1];

        tangent.y = 0f;

        return tangent.sqrMagnitude > 0.0001f
            ? tangent.normalized
            : Vector3.forward;
    }

    private void ReplaceGeneratedMesh(Mesh mesh)
    {
        MeshFilter filter = GetComponent<MeshFilter>();

        if (generatedMesh != null)
        {
            if (Application.isPlaying)
                Destroy(generatedMesh);
            else
                DestroyImmediate(generatedMesh);
        }

        generatedMesh = mesh;
        filter.sharedMesh = generatedMesh;
    }

    private void OnDestroy()
    {
        if (generatedMesh == null)
            return;

        if (Application.isPlaying)
            Destroy(generatedMesh);
        else
            DestroyImmediate(generatedMesh);
    }
}
