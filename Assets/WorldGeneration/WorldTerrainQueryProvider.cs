using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cached world-position queries for a set of generated Unity Terrain tiles.
/// It is opt-in: existing scenes without an active provider continue using
/// their serialized single Collider.
/// </summary>
[DisallowMultipleComponent]
public sealed class WorldTerrainQueryProvider : MonoBehaviour
{
    private const float BoundaryEpsilon = 0.002f;
    // Rays are cast this far inside a tile: a heightfield ray placed exactly on
    // a tile's maximum X/Z edge misses (measured: every +X/+Z outer-edge
    // sample failed), which previously discarded whole river/rail routes.
    private const float EdgeInset = 0.001f;
    private const int FailedQuerySampleLimit = 8;
    private const string FailureOutsideWorld = "outside_world";
    private const string FailureAtBoundary = "at_boundary";
    private const string FailureInsideTile = "inside_tile";
    private const string FailureInvalidInput = "invalid_input";
    private const string FailureNotReady = "not_ready";

    private TerrainCollider[,] colliders;
    // Tile placement from Terrain/TerrainData, used for tile selection and the
    // ray origin instead of physics bounds.
    private Vector3[,] tileMinimums;
    private Vector3[,] tileSizes;
    private int columns;
    private int rows;
    private float tileSizeX;
    private float tileSizeZ;
    private float originX;
    private float originZ;
    private float maximumX;
    private float maximumZ;
    private int cachedTerrainCount;
    private int cacheVersion;

    /// <summary>Aggregated diagnostic counters for the last Play-mode generation.</summary>
    public struct QueryDiagnostics
    {
        public int totalQueries;
        public int successfulQueries;
        public int failedQueries;
        public int failedAtBoundary;
        public int failedInsideTile;
        public int failedOutsideWorldBounds;
        public int failedInvalidInput;
        public int failedNotReady;
        // Per-tile failure counts (index = z*columns + x)
        public int[] failuresByTile;
        // Sample failed query details (last N)
        public struct FailedQuery
        {
            public Vector3 worldPosition;
            public int selectedX, selectedZ;
            public bool colliderEnabled;
            public bool insideOverallBounds;
            public string reason; // "outside_world", "at_boundary", "inside_tile", "invalid_input", "not_ready"
        }
        public int failedQueryLogLimit;
        public FailedQuery[] failedQuerySamples;
    }

    // Backing field so the counters can be incremented in place; a struct
    // returned from an auto-property cannot be modified (CS1612).
    private QueryDiagnostics lastDiagnostics;
    public QueryDiagnostics LastDiagnostics => lastDiagnostics;

    /// <summary>
    /// True when every tile of the configured grid has a cached collider.
    /// </summary>
    public bool IsReady =>
        colliders != null &&
        cachedTerrainCount == columns * rows &&
        columns > 0 &&
        rows > 0;

    // Number of entries used in lastDiagnostics.failedQuerySamples.
    private int failedSampleCount;

    private void OnDisable()
    {
        ClearCache();
    }

    /// <summary>
    /// Rebuilds the tile cache from the Terrain components created by the
    /// prototype. Call once after generation and whenever its tile set changes.
    /// No scene-wide search is performed by terrain queries.
    /// </summary>
    public bool SetTerrainTiles(
        IReadOnlyList<Terrain> terrains,
        Vector2 worldSize,
        int chunkCountX,
        int chunkCountZ)
    {
        ClearCache();
        cacheVersion++;

        if (terrains == null || chunkCountX <= 0 || chunkCountZ <= 0 ||
            terrains.Count != chunkCountX * chunkCountZ ||
            worldSize.x <= 0f || worldSize.y <= 0f)
        {
            Debug.LogError(
                "WorldTerrainQueryProvider: Terrain tile list or grid dimensions are invalid.",
                this);
            return false;
        }

        columns = chunkCountX;
        rows = chunkCountZ;
        tileSizeX = worldSize.x / columns;
        tileSizeZ = worldSize.y / rows;
        colliders = new TerrainCollider[rows, columns];
        tileMinimums = new Vector3[rows, columns];
        tileSizes = new Vector3[rows, columns];

        originX = float.PositiveInfinity;
        originZ = float.PositiveInfinity;
        for (int i = 0; i < terrains.Count; i++)
        {
            Terrain terrain = terrains[i];
            if (terrain == null || terrain.terrainData == null)
            {
                ClearCache();
                Debug.LogError(
                    "WorldTerrainQueryProvider: A generated Terrain or TerrainData is missing.",
                    this);
                return false;
            }

            originX = Mathf.Min(originX, terrain.transform.position.x);
            originZ = Mathf.Min(originZ, terrain.transform.position.z);
        }

        maximumX = originX + worldSize.x;
        maximumZ = originZ + worldSize.y;

        for (int i = 0; i < terrains.Count; i++)
        {
            Terrain terrain = terrains[i];
            TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
            if (collider == null || !collider.enabled || collider.terrainData != terrain.terrainData)
            {
                ClearCache();
                Debug.LogError(
                    "WorldTerrainQueryProvider: TerrainCollider is missing, disabled, or uses different TerrainData.",
                    terrain);
                return false;
            }

            int x = Mathf.RoundToInt((terrain.transform.position.x - originX) / tileSizeX);
            int z = Mathf.RoundToInt((terrain.transform.position.z - originZ) / tileSizeZ);
            if (x < 0 || x >= columns || z < 0 || z >= rows || colliders[z, x] != null)
            {
                ClearCache();
                Debug.LogError(
                    "WorldTerrainQueryProvider: Terrain tile positions do not form the configured regular grid.",
                    terrain);
                return false;
            }

            Vector3 size = terrain.terrainData.size;
            if (Mathf.Abs(size.x - tileSizeX) > 0.01f || Mathf.Abs(size.z - tileSizeZ) > 0.01f)
            {
                ClearCache();
                Debug.LogError(
                    "WorldTerrainQueryProvider: Terrain tile size does not match the configured grid.",
                    terrain);
                return false;
            }

            colliders[z, x] = collider;
            tileMinimums[z, x] = terrain.transform.position;
            tileSizes[z, x] = size;
            cachedTerrainCount++;
        }

        if (!IsReady)
        {
            ClearCache();
            Debug.LogError("WorldTerrainQueryProvider: Terrain grid cache is incomplete.", this);
            return false;
        }

        ResetDiagnostics();
        return true;
    }

    public void ClearTerrainTiles()
    {
        ClearCache();
        cacheVersion++;
        ResetDiagnostics();
    }

    /// <summary>
    /// Casts straight down against the cached tile containing the supplied XZ
    /// point. The ray height and length come from the tile's TerrainData, not
    /// from physics bounds, and the cast position is held EdgeInset inside the
    /// tile so a ray never grazes the exact edge of a heightfield. Points on a
    /// shared seam may fall back to the neighbouring tile. Points on the outer
    /// world edge (within BoundaryEpsilon) are valid; points further outside,
    /// non-finite input, or an uninitialised cache fail without logging. All
    /// failures are aggregated in LastDiagnostics.
    /// </summary>
    public bool TryRaycastDown(
        Vector3 worldPosition,
        float extraHeight,
        out RaycastHit hit)
    {
        hit = default(RaycastHit);
        lastDiagnostics.totalQueries++;

        if (!IsReady)
        {
            return RecordFailure(worldPosition, -1, -1, FailureNotReady);
        }

        if (!IsFinite(worldPosition) ||
            float.IsNaN(extraHeight) || float.IsInfinity(extraHeight) ||
            extraHeight < 0f)
        {
            return RecordFailure(worldPosition, -1, -1, FailureInvalidInput);
        }

        if (worldPosition.x < originX - BoundaryEpsilon ||
            worldPosition.x > maximumX + BoundaryEpsilon ||
            worldPosition.z < originZ - BoundaryEpsilon ||
            worldPosition.z > maximumZ + BoundaryEpsilon)
        {
            return RecordFailure(worldPosition, -1, -1, FailureOutsideWorld);
        }

        float x = Mathf.Clamp(worldPosition.x, originX, maximumX);
        float z = Mathf.Clamp(worldPosition.z, originZ, maximumZ);
        int primaryX = Mathf.Clamp(
            Mathf.FloorToInt((x - originX) / tileSizeX), 0, columns - 1);
        int primaryZ = Mathf.Clamp(
            Mathf.FloorToInt((z - originZ) / tileSizeZ), 0, rows - 1);

        if (CastTile(primaryX, primaryZ, x, z, extraHeight, out hit))
        {
            lastDiagnostics.successfulQueries++;
            return true;
        }

        // Seam fallback: only neighbours whose rectangle actually contains the
        // point (within BoundaryEpsilon) may answer, so a neighbour can never
        // return a height for a point it does not cover.
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int tx = primaryX + dx;
                int tz = primaryZ + dz;
                if (tx < 0 || tx >= columns || tz < 0 || tz >= rows) continue;
                if (!TileContains(tx, tz, x, z, BoundaryEpsilon)) continue;
                if (CastTile(tx, tz, x, z, extraHeight, out hit))
                {
                    lastDiagnostics.successfulQueries++;
                    return true;
                }
            }
        }

        bool nearEdge = !TileContains(primaryX, primaryZ, x, z, -BoundaryEpsilon);
        return RecordFailure(
            worldPosition, primaryX, primaryZ,
            nearEdge ? FailureAtBoundary : FailureInsideTile);
    }

    private bool CastTile(
        int x, int z, float worldX, float worldZ, float extraHeight,
        out RaycastHit hit)
    {
        hit = default(RaycastHit);
        TerrainCollider collider = colliders[z, x];
        if (collider == null || !collider.enabled) return false;

        Vector3 min = tileMinimums[z, x];
        Vector3 size = tileSizes[z, x];
        float insetX = Mathf.Min(EdgeInset, size.x * 0.25f);
        float insetZ = Mathf.Min(EdgeInset, size.z * 0.25f);
        float castX = Mathf.Clamp(worldX, min.x + insetX, min.x + size.x - insetX);
        float castZ = Mathf.Clamp(worldZ, min.z + insetZ, min.z + size.z - insetZ);

        float originY = min.y + size.y + extraHeight + 1f;
        float distance = size.y + extraHeight * 2f + 2f;
        Ray ray = new Ray(new Vector3(castX, originY, castZ), Vector3.down);
        return collider.Raycast(ray, out hit, distance);
    }

    private bool TileContains(int x, int z, float worldX, float worldZ, float tolerance)
    {
        Vector3 min = tileMinimums[z, x];
        Vector3 size = tileSizes[z, x];
        return worldX >= min.x - tolerance && worldX <= min.x + size.x + tolerance &&
               worldZ >= min.z - tolerance && worldZ <= min.z + size.z + tolerance;
    }

    private bool RecordFailure(Vector3 worldPosition, int tileX, int tileZ, string reason)
    {
        lastDiagnostics.failedQueries++;
        switch (reason)
        {
            case FailureOutsideWorld: lastDiagnostics.failedOutsideWorldBounds++; break;
            case FailureAtBoundary: lastDiagnostics.failedAtBoundary++; break;
            case FailureInsideTile: lastDiagnostics.failedInsideTile++; break;
            case FailureInvalidInput: lastDiagnostics.failedInvalidInput++; break;
            case FailureNotReady: lastDiagnostics.failedNotReady++; break;
        }

        if (lastDiagnostics.failuresByTile != null &&
            tileX >= 0 && tileZ >= 0 &&
            tileZ * columns + tileX < lastDiagnostics.failuresByTile.Length)
        {
            lastDiagnostics.failuresByTile[tileZ * columns + tileX]++;
        }

        if (lastDiagnostics.failedQuerySamples != null &&
            failedSampleCount < lastDiagnostics.failedQuerySamples.Length)
        {
            lastDiagnostics.failedQuerySamples[failedSampleCount++] =
                new QueryDiagnostics.FailedQuery
                {
                    worldPosition = worldPosition,
                    selectedX = tileX,
                    selectedZ = tileZ,
                    colliderEnabled = tileX >= 0 && tileZ >= 0 &&
                                      colliders != null &&
                                      colliders[tileZ, tileX] != null &&
                                      colliders[tileZ, tileX].enabled,
                    insideOverallBounds = reason != FailureOutsideWorld,
                    reason = reason
                };
        }

        return false;
    }

    /// <summary>
    /// Replaces the Y of every route point with the terrain height under it.
    /// A short run of failed samples (at most maxGapPoints) between two valid
    /// samples is filled by linear interpolation over horizontal distance, so
    /// the route stays continuous. A failed first/last sample or a longer gap
    /// makes the route invalid; the reason is returned in failure.
    /// </summary>
    public bool TryProjectRoute(
        List<Vector3> route,
        float extraHeight,
        int maxGapPoints,
        out int failedSamples,
        out int recoveredSamples,
        out string failure)
    {
        failedSamples = 0;
        recoveredSamples = 0;
        failure = null;
        if (route == null || route.Count < 2)
        {
            failure = "route has fewer than two points";
            return false;
        }

        int count = route.Count;
        bool[] valid = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (TryRaycastDown(route[i], extraHeight, out RaycastHit hit))
            {
                Vector3 point = route[i];
                point.y = hit.point.y;
                route[i] = point;
                valid[i] = true;
            }
            else
            {
                failedSamples++;
            }
        }

        if (failedSamples == 0) return true;

        if (!valid[0] || !valid[count - 1])
        {
            failure = string.Format(
                "route endpoint could not be projected ({0} of {1} samples failed)",
                failedSamples, count);
            return false;
        }

        int index = 1;
        while (index < count - 1)
        {
            if (valid[index]) { index++; continue; }

            int gapStart = index;
            while (index < count && !valid[index]) index++;
            int gapLength = index - gapStart;
            if (gapLength > maxGapPoints)
            {
                failure = string.Format(
                    "{0} consecutive samples failed starting at index {1} (limit {2})",
                    gapLength, gapStart, maxGapPoints);
                return false;
            }

            Vector3 before = route[gapStart - 1];
            Vector3 after = route[index];
            float total = 0f;
            for (int k = gapStart; k <= index; k++) total += HorizontalDistance(route[k - 1], route[k]);
            float travelled = 0f;
            for (int k = gapStart; k < index; k++)
            {
                travelled += HorizontalDistance(route[k - 1], route[k]);
                Vector3 point = route[k];
                point.y = Mathf.Lerp(before.y, after.y, total > 0f ? travelled / total : 0.5f);
                route[k] = point;
                recoveredSamples++;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the parameter range [t0, t1] of a curve whose ends lie inside the
    /// given XZ rectangle, so a guide that runs to (or past) the world edge can
    /// be trimmed to the padded playable area without changing its shape.
    /// Returns false when no part of the curve lies inside.
    /// </summary>
    public static bool TryGetCurveRangeInside(
        System.Func<float, Vector3> evaluate,
        float minX, float maxX, float minZ, float maxZ,
        out float t0, out float t1)
    {
        const int Samples = 1024;
        t0 = 0f;
        t1 = 1f;
        int first = -1;
        int last = -1;
        for (int i = 0; i <= Samples; i++)
        {
            Vector3 p = evaluate(i / (float)Samples);
            if (p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ)
            {
                if (first < 0) first = i;
                last = i;
            }
        }

        if (first < 0) return false;

        t0 = first == 0 ? 0f : Bisect(evaluate, (first - 1) / (float)Samples, first / (float)Samples, minX, maxX, minZ, maxZ);
        t1 = last == Samples ? 1f : Bisect(evaluate, (last + 1) / (float)Samples, last / (float)Samples, minX, maxX, minZ, maxZ);
        return t1 > t0;
    }

    // Narrows [outsideT, insideT] onto the rectangle boundary and returns the
    // parameter on the inside of it.
    private static float Bisect(
        System.Func<float, Vector3> evaluate, float outsideT, float insideT,
        float minX, float maxX, float minZ, float maxZ)
    {
        for (int i = 0; i < 32; i++)
        {
            float mid = (outsideT + insideT) * 0.5f;
            Vector3 p = evaluate(mid);
            if (p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ) insideT = mid;
            else outsideT = mid;
        }
        return insideT;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Clears the aggregated query counters and failure samples.</summary>
    public void ResetDiagnostics()
    {
        failedSampleCount = 0;
        lastDiagnostics = new QueryDiagnostics
        {
            failuresByTile = new int[Mathf.Max(0, columns * rows)],
            failedQueryLogLimit = FailedQuerySampleLimit,
            failedQuerySamples = new QueryDiagnostics.FailedQuery[FailedQuerySampleLimit]
        };
    }

    /// <summary>One-line summary of the counters plus the first failure samples.</summary>
    public string DescribeDiagnostics()
    {
        QueryDiagnostics d = lastDiagnostics;
        System.Text.StringBuilder text = new System.Text.StringBuilder();
        text.AppendFormat(
            "queries={0}, ok={1}, failed={2} (outside_world={3}, at_boundary={4}, inside_tile={5}, invalid_input={6}, not_ready={7})",
            d.totalQueries, d.successfulQueries, d.failedQueries,
            d.failedOutsideWorldBounds, d.failedAtBoundary, d.failedInsideTile,
            d.failedInvalidInput, d.failedNotReady);
        for (int i = 0; i < failedSampleCount && d.failedQuerySamples != null; i++)
        {
            QueryDiagnostics.FailedQuery q = d.failedQuerySamples[i];
            text.AppendFormat(
                "; [{0}] ({1:F3}, {2:F3}) tile {3},{4}",
                q.reason, q.worldPosition.x, q.worldPosition.z, q.selectedX, q.selectedZ);
        }
        return text.ToString();
    }

    /// <summary>
    /// Logs one aggregated summary of the queries since the last reset:
    /// a warning when any query failed, otherwise an informational line.
    /// </summary>
    public void RecordDiagnostics()
    {
        string summary = "WorldTerrainQueryProvider: " + DescribeDiagnostics();
        if (lastDiagnostics.failedQueries > 0) Debug.LogWarning(summary, this);
        else Debug.Log(summary, this);
    }

    public bool TryGetHeightAndNormal(
        Vector3 worldPosition,
        float extraHeight,
        out float height,
        out Vector3 normal)
    {
        if (TryRaycastDown(worldPosition, extraHeight, out RaycastHit hit))
        {
            height = hit.point.y;
            normal = hit.normal;
            return true;
        }

        height = 0f;
        normal = Vector3.up;
        return false;
    }

    private void ClearCache()
    {
        colliders = null;
        tileMinimums = null;
        tileSizes = null;
        columns = 0;
        rows = 0;
        cachedTerrainCount = 0;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
