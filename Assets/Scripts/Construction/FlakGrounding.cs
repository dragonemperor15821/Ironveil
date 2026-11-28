using UnityEngine;

/// <summary>
/// Stage 3A.1: grounds a placed Flak (or any slot instance) on the ACTUAL
/// ground at its location. Only the instance's world Y changes; its
/// base-relative XZ position, facing and local scale stay exactly as placed.
///
/// Ground height:
///   - terrain: the MEAN of a grid of terrain samples over the slot's reserved
///     footprint (centre, corners and edges; default 3 x 3). On a slope the
///     model base then embeds on the uphill side as much as it floats on the
///     downhill side (both reported), instead of floating by the full rise.
///   - foundation support: when part of the footprint rests on the foundation
///     mesh (slot.HasFoundationSupport, e.g. the low apron outside the Ground
///     arena wall), the instance is never placed below that surface (recorded
///     in the slot's local Y at build time, transformed by the base root).
/// Off-foundation slots use the terrain alone.
///
/// The terrain is queried through the same WorldTerrainQueryProvider that
/// BaseSpawner uses for terrain conformance, or through any HeightQuery
/// (e.g. a TerrainCollider raycast) when no provider exists.
/// </summary>
public static class FlakGrounding
{
    public delegate bool HeightQuery(Vector3 worldPosition, out float worldHeight);

    public struct Result
    {
        public bool grounded;          // at least one terrain sample succeeded
        public int samples;
        public int misses;
        public float terrainMin;       // world Y
        public float terrainMax;       // world Y
        public float foundationSurface;// world Y of the recorded foundation surface
        public float terrainMean;      // world Y
        public bool usedFoundation;    // the foundation surface was higher than the terrain support
        public float finalY;           // world Y of the instance pivot (model bottom)
        public float slopeGap;         // finalY - terrainMin: largest float above the terrain (downhill side)
        public float embed;            // terrainMax - finalY (>= 0): deepest terrain rise into the model base
        public float slopeDegrees;     // footprint slope estimate from the samples
    }

    /// <summary>Terrain query through BaseSpawner's terrain provider (same raycast conventions).</summary>
    public static HeightQuery FromProvider(WorldTerrainQueryProvider provider, float extraHeight = 20f)
    {
        return (Vector3 p, out float h) =>
        {
            h = 0f;
            if (provider == null || !provider.IsReady) return false;
            if (!provider.TryRaycastDown(p, extraHeight, out RaycastHit hit)) return false;
            h = hit.point.y;
            return true;
        };
    }

    /// <summary>Fallback: raycast straight down against one TerrainCollider.</summary>
    public static HeightQuery FromTerrainCollider(TerrainCollider collider, float extraHeight = 20f)
    {
        return (Vector3 p, out float h) =>
        {
            h = 0f;
            if (collider == null) return false;
            Bounds b = collider.bounds;
            Ray ray = new Ray(new Vector3(p.x, b.max.y + extraHeight, p.z), Vector3.down);
            if (!collider.Raycast(ray, out RaycastHit hit, b.size.y + extraHeight * 2f + 1f)) return false;
            h = hit.point.y;
            return true;
        };
    }

    /// <summary>
    /// Grounds the slot's occupant. Returns false (instance untouched) when no
    /// terrain sample succeeds.
    /// </summary>
    public static bool Ground(BuildingSlot slot, Transform baseRoot, HeightQuery query, out Result result,
        int gridPerAxis = 3)
    {
        result = default(Result);
        if (slot == null || slot.Occupant == null || baseRoot == null || query == null)
        {
            return false;
        }

        Transform instance = slot.Occupant.transform;

        // Slot pose in base-root space (slots have unit scale and yaw only).
        Vector3 slotLocal = baseRoot.InverseTransformPoint(slot.transform.position);
        Quaternion slotLocalRot = Quaternion.Inverse(baseRoot.rotation) * slot.transform.rotation;
        Vector3 axisX = slotLocalRot * Vector3.right;
        Vector3 axisZ = slotLocalRot * Vector3.forward;
        Vector2 half = slot.ReservedFootprint * 0.5f;

        gridPerAxis = Mathf.Max(1, gridPerAxis);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        double sumX = 0, sumZ = 0, sumH = 0, sumXX = 0, sumZZ = 0, sumXZ = 0, sumXH = 0, sumZH = 0;
        for (int i = 0; i < gridPerAxis; i++)
        {
            for (int j = 0; j < gridPerAxis; j++)
            {
                float u = gridPerAxis == 1 ? 0f : i / (gridPerAxis - 1f) * 2f - 1f;
                float v = gridPerAxis == 1 ? 0f : j / (gridPerAxis - 1f) * 2f - 1f;
                Vector3 local = slotLocal + axisX * (u * half.x) + axisZ * (v * half.y);
                Vector3 world = baseRoot.TransformPoint(local);
                result.samples++;
                if (!query(world, out float h))
                {
                    result.misses++;
                    continue;
                }

                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
                sumX += world.x; sumZ += world.z; sumH += h;
                sumXX += (double)world.x * world.x; sumZZ += (double)world.z * world.z;
                sumXZ += (double)world.x * world.z;
                sumXH += (double)world.x * h; sumZH += (double)world.z * h;
            }
        }

        int n = result.samples - result.misses;
        if (n == 0)
        {
            return false;
        }

        Vector3 surfaceLocal = new Vector3(slotLocal.x, slot.transform.localPosition.y, slotLocal.z);
        result.foundationSurface = baseRoot.TransformPoint(surfaceLocal).y;
        result.terrainMin = min;
        result.terrainMax = max;
        result.terrainMean = (float)(sumH / n);
        result.finalY = result.terrainMean;
        if (slot.HasFoundationSupport && result.foundationSurface > result.finalY)
        {
            result.finalY = result.foundationSurface;
            result.usedFoundation = true;
        }

        result.slopeGap = result.finalY - min;
        result.embed = Mathf.Max(0f, max - result.finalY);

        // Least-squares plane h = a + gx*x + gz*z through the samples (handles a rotated grid).
        double mx = sumX / n, mz = sumZ / n, mh = sumH / n;
        double vx = sumXX / n - mx * mx, vz = sumZZ / n - mz * mz, cxz = sumXZ / n - mx * mz;
        double cxh = sumXH / n - mx * mh, czh = sumZH / n - mz * mh;
        double det = vx * vz - cxz * cxz;
        double gx = 0, gz = 0;
        if (det > 1e-12)
        {
            gx = (cxh * vz - czh * cxz) / det;
            gz = (czh * vx - cxh * cxz) / det;
        }

        result.slopeDegrees = (float)(System.Math.Atan(System.Math.Sqrt(gx * gx + gz * gz)) * 180.0 / System.Math.PI);

        Vector3 p = instance.position;
        instance.position = new Vector3(p.x, result.finalY, p.z);
        result.grounded = true;
        return true;
    }
}
