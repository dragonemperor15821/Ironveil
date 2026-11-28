using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Stage 3A editor helpers shared by the foundation builder and validator:
/// model footprints, oriented-rectangle overlap tests and foundation-surface
/// height sampling. Editor only; nothing here runs in builds.
/// </summary>
public static class ModularFoundationGeometry
{
    // Far-away sampling origin so temporary colliders never meet scene objects
    // when the tools run inside an open editor session.
    public static readonly Vector3 SamplingOrigin = new Vector3(20000f, 5000f, 20000f);

    public struct Rect2
    {
        public Vector2 centre;
        public Vector2 half;      // half size along local X, Z
        public float yawDegrees;  // rotation about +Y (Unity convention)

        public Rect2(Vector2 c, Vector2 size, float yaw)
        {
            centre = c;
            half = size * 0.5f;
            yawDegrees = yaw;
        }

        public Vector2 AxisX
        {
            get
            {
                float r = yawDegrees * Mathf.Deg2Rad;
                // Unity yaw: local +X -> (cos, -sin) in XZ, local +Z -> (sin, cos).
                return new Vector2(Mathf.Cos(r), -Mathf.Sin(r));
            }
        }

        public Vector2 AxisZ
        {
            get
            {
                float r = yawDegrees * Mathf.Deg2Rad;
                return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            }
        }

        public Vector2[] Corners()
        {
            Vector2 ax = AxisX * half.x;
            Vector2 az = AxisZ * half.y;
            return new[] { centre + ax + az, centre + ax - az, centre - ax - az, centre - ax + az };
        }
    }

    /// <summary>
    /// Signed separation along the best SAT axis: positive = clear gap,
    /// negative = penetration depth.
    /// </summary>
    public static float Separation(Rect2 a, Rect2 b)
    {
        Vector2[] axes = { a.AxisX, a.AxisZ, b.AxisX, b.AxisZ };
        Vector2[] ca = a.Corners();
        Vector2[] cb = b.Corners();
        float best = float.NegativeInfinity;
        foreach (Vector2 axis in axes)
        {
            Project(ca, axis, out float minA, out float maxA);
            Project(cb, axis, out float minB, out float maxB);
            float gap = Mathf.Max(minB - maxA, minA - maxB);
            if (gap > best) best = gap;
        }

        return best;
    }

    private static void Project(Vector2[] pts, Vector2 axis, out float min, out float max)
    {
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        foreach (Vector2 p in pts)
        {
            float d = Vector2.Dot(p, axis);
            if (d < min) min = d;
            if (d > max) max = d;
        }
    }

    /// <summary>
    /// Horizontal footprint (X by Z size and centre offset) of a model placed
    /// the way BuildingPlacement places it: prefab root rotation, uniform
    /// scale = prefab scale * instanceScale, slot at the origin with no yaw.
    /// </summary>
    public static bool TryModelFootprint(GameObject prefab, float instanceScale, out Vector2 size,
        out Vector2 centreOffset, out float height, out float minY, float facingYawOffset = 0f)
    {
        size = Vector2.zero;
        centreOffset = Vector2.zero;
        height = 0f;
        minY = 0f;
        if (prefab == null) return false;

        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.transform.SetPositionAndRotation(SamplingOrigin,
            Quaternion.Euler(0f, facingYawOffset, 0f) * prefab.transform.localRotation);
        go.transform.localScale = prefab.transform.localScale * instanceScale;
        bool any = TryCombinedBounds(go, true, out Bounds b);
        Object.DestroyImmediate(go);
        if (!any) return false;

        size = new Vector2(b.size.x, b.size.z);
        centreOffset = new Vector2(b.center.x - SamplingOrigin.x, b.center.z - SamplingOrigin.z);
        height = b.size.y;
        minY = b.min.y - SamplingOrigin.y;
        return true;
    }

    /// <summary>Combined renderer bounds, the way BaseSpawner combines them.</summary>
    public static bool TryCombinedBounds(GameObject root, bool includeInactive, out Bounds bounds)
    {
        bounds = default(Bounds);
        bool any = false;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(includeInactive))
        {
            if (r == null) continue;
            if (!any)
            {
                bounds = r.bounds;
                any = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        return any;
    }

    /// <summary>
    /// Height sampler over a temporary instance of a foundation-like object.
    /// Only enabled renderers in active objects receive temporary colliders.
    /// Coordinates are base-root local (instance placed at SamplingOrigin, scale 1).
    /// </summary>
    public sealed class SurfaceSampler : System.IDisposable
    {
        private readonly GameObject instance;

        public SurfaceSampler(GameObject prefabOrInstanceSource, System.Func<Transform, bool> excludeSubtree)
        {
            instance = (GameObject)Object.Instantiate(prefabOrInstanceSource);
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.SetPositionAndRotation(SamplingOrigin, Quaternion.identity);
            instance.transform.localScale = Vector3.one;

            List<Transform> excluded = new List<Transform>();
            foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
            {
                if (t != instance.transform && excludeSubtree != null && excludeSubtree(t)) excluded.Add(t);
            }

            foreach (MeshFilter mf in instance.GetComponentsInChildren<MeshFilter>(false))
            {
                bool skip = false;
                foreach (Transform ex in excluded)
                {
                    if (mf.transform == ex || mf.transform.IsChildOf(ex)) { skip = true; break; }
                }

                Renderer r = mf.GetComponent<Renderer>();
                if (skip || r == null || !r.enabled || mf.sharedMesh == null) continue;
                MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }

            Physics.SyncTransforms();
        }

        /// <summary>Highest surface Y (base-local) at an XZ point, or null when nothing is hit.</summary>
        public float? HeightAt(Vector2 xz)
        {
            Vector3 from = SamplingOrigin + new Vector3(xz.x, 3f, xz.y);
            RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, 6f);
            float? best = null;
            foreach (RaycastHit h in hits)
            {
                if (!h.collider.transform.IsChildOf(instance.transform)) continue;
                float y = h.point.y - SamplingOrigin.y;
                if (best == null || y > best.Value) best = y;
            }

            return best;
        }

        /// <summary>Samples a rectangle on a grid; returns (max height, hit fraction).</summary>
        public void SampleRect(Rect2 rect, int gridPerAxis, out float maxHeight, out float hitFraction)
        {
            maxHeight = float.NegativeInfinity;
            int hits = 0, total = 0;
            Vector2 ax = rect.AxisX, az = rect.AxisZ;
            for (int i = 0; i < gridPerAxis; i++)
            {
                for (int j = 0; j < gridPerAxis; j++)
                {
                    float u = gridPerAxis == 1 ? 0f : (i / (gridPerAxis - 1f)) * 2f - 1f;
                    float v = gridPerAxis == 1 ? 0f : (j / (gridPerAxis - 1f)) * 2f - 1f;
                    Vector2 p = rect.centre + ax * (u * rect.half.x) + az * (v * rect.half.y);
                    float? h = HeightAt(p);
                    total++;
                    if (h.HasValue)
                    {
                        hits++;
                        if (h.Value > maxHeight) maxHeight = h.Value;
                    }
                }
            }

            hitFraction = total == 0 ? 0f : hits / (float)total;
            if (hits == 0) maxHeight = 0f;
        }

        public void Dispose()
        {
            if (instance != null) Object.DestroyImmediate(instance);
        }
    }
}
