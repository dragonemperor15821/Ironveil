using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Stage 3A editor test helper: measures a prefab exactly the way the
/// production BaseSpawner does, by invoking its private TryGetPrefabFootprint
/// on a temporary, never-saved BaseSpawner. BaseSpawner itself is not
/// modified. Used to prove a foundation's footprint equals its original's.
/// </summary>
public static class SpawnerFootprintProbe
{
    public struct Result
    {
        public bool ok;
        public Vector3 boundsCenter;
        public Vector3 boundsSize;
        public Vector3 turretFacingLocal;
        public bool hasTurretFacing;
        public bool hasDefensiveFront;

        public bool BitEquals(Result o)
        {
            return ok == o.ok && hasTurretFacing == o.hasTurretFacing && hasDefensiveFront == o.hasDefensiveFront &&
                   BitwiseEqual(boundsCenter, o.boundsCenter) && BitwiseEqual(boundsSize, o.boundsSize) &&
                   BitwiseEqual(turretFacingLocal, o.turretFacingLocal);
        }

        public override string ToString()
        {
            return $"center {boundsCenter.ToString("F6")} size {boundsSize.ToString("F6")} " +
                   $"facing {turretFacingLocal.ToString("F6")} hasFacing {hasTurretFacing} front {hasDefensiveFront}";
        }
    }

    public static bool BitwiseEqual(Vector3 a, Vector3 b)
    {
        return BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x) &&
               BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y) &&
               BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z);
    }

    public static Result Measure(GameObject prefab, float baseSpawnScale)
    {
        Result result = default(Result);
        GameObject host = new GameObject("TEMP_Stage3A_FootprintProbe");
        host.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            BaseSpawner spawner = host.AddComponent<BaseSpawner>();
            Type t = typeof(BaseSpawner);
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            t.GetField("baseSpawnScale", F).SetValue(spawner, baseSpawnScale);

            MethodInfo measure = t.GetMethod("TryGetPrefabFootprint", F);
            object[] args = { prefab, null };
            result.ok = (bool)measure.Invoke(spawner, args);
            object fp = args[1];
            Type ft = fp.GetType();
            const BindingFlags P = BindingFlags.Instance | BindingFlags.Public;
            result.boundsCenter = (Vector3)ft.GetField("boundsCenter", P).GetValue(fp);
            result.boundsSize = (Vector3)ft.GetField("boundsSize", P).GetValue(fp);
            result.turretFacingLocal = (Vector3)ft.GetField("turretFacingLocal", P).GetValue(fp);
            result.hasTurretFacing = (bool)ft.GetField("hasTurretFacing", P).GetValue(fp);

            MethodInfo front = t.GetMethod("PrefabHasDefensiveFront", BindingFlags.Static | BindingFlags.NonPublic);
            result.hasDefensiveFront = (bool)front.Invoke(null, new object[] { prefab });
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }

        return result;
    }
}
