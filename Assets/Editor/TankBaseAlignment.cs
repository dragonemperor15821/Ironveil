// IRONVEIL - Tank Base Alignment Tool
// -----------------------------------
// Vertical-only alignment of "Tank Base Master" contents (wall + buildings)
// against the measured geometry of the "Tank Base Roads" surface model.
//
// - Sampled from the actual imported road mesh (triangle raster, not bounds).
// - Wall ring footprint fitted from its real contact band; interior courtyard
//   vertices are rejected by a Y-level histogram instead of contaminating the fit.
// - Wall target surface is sampled along the RING CENTERLINE (perimeter rule).
// - Buildings aligned individually from their own foundation contact points.
// - Dry run changes nothing. Apply creates file backups first and records Undo.
// - X/Z positions, scales, meshes, materials and layout are never modified.
//
// Menus (Tools/IRONVEIL/Align Tank Base):
//   Backup Now                          - create a timestamped backup only
//   Dry Run Report                      - full analysis, ZERO changes
//   Apply Aligned Offsets (Scene)       - backup + apply to the scene instance

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class TankBaseAlignment
{
    private const string MenuRoot = "Tools/IRONVEIL/Align Tank Base/";

    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------

    private const int GridRes = 384;                  // surface raster resolution
    private const float ContactBandFraction = 0.30f;  // fraction of object height scanned for the contact level
    private const int ContactHistogramBins = 32;
    private const int WallAngleSamples = 240;         // perimeter samples along the ring centerline
    private const float Epsilon = 0.0005f;            // shifts below this are treated as "already aligned"
    private const float UnreliableSampleLimit = 0.05f;// >5% unsampleable points -> object is skipped in Apply
    private const int MaxPointsPerObject = 400;       // cap of sample points per building

    // ------------------------------------------------------------------
    // Menu items
    // ------------------------------------------------------------------

    [MenuItem(MenuRoot + "Backup Now", priority = 100)]
    private static void MenuBackupNow()
    {
        string folder = CreateBackupFolder();
        if (!string.IsNullOrEmpty(folder))
            Debug.Log("IRONVEIL: Backup created at " + folder + ".");
    }

    [MenuItem(MenuRoot + "Dry Run Report", priority = 0)]
    private static void MenuDryRun()
    {
        Run(false);
    }

    [MenuItem(MenuRoot + "Apply Aligned Offsets (Scene)", priority = 1)]
    private static void MenuApply()
    {
        Run(true);
    }

    // ------------------------------------------------------------------
    // Orchestration
    // ------------------------------------------------------------------

    private static void Run(bool apply)
    {
        BaseContext ctx = null;
        StringBuilder log = new StringBuilder();

        try
        {
            // Each section is flushed to the Console immediately after it is
            // computed. If anything fails later in the run, everything computed
            // so far is already visible instead of being lost with the combined
            // end-of-run log.
            void Flush()
            {
                if (log.Length == 0)
                    return;
                Debug.Log(log.ToString(), ctx != null && ctx.baseRoot != null ? ctx.baseRoot : null);
                log.Clear();
            }

            ctx = BuildContext();

            if (ctx == null)
            {
                Debug.LogError("IRONVEIL: No 'Tank Base Master' object found in the loaded scenes. " +
                               "Open the MainBuild scene and make sure the object is present and active.");
                return;
            }

            log.AppendLine("========== IRONVEIL - TANK BASE ALIGNMENT " +
                           (apply ? "APPLY" : "DRY RUN") + " ==========");
            log.AppendLine("Base root: '" + ctx.baseRoot.name + "'  world pos " + P(ctx.baseRoot.position) +
                           "  world scale " + S(ctx.baseRoot.lossyScale));
            log.AppendLine();

            // ---------------- (1/5) ROADS OBJECTS ----------------
            log.AppendLine("--- (1/5) Structures named 'Tank Base Roads' (surface source check) ---");
            foreach (CandidateInfo c in ctx.roadCandidates)
            {
                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0}  parent='{1}'  active={2}  meshFilters={3}  tris={4}  baseXZ-area={5:F4}  -> {6}",
                    c.path, c.parentName, c.active, c.filterCount, c.triCount, c.area,
                    c.transform == ctx.roadsRoot ? "USED for surface sampling" : "reference only"));
                if (c.note != null)
                    log.AppendLine("      NOTE: " + c.note);
            }

            if (ctx.roadCandidates.Count > 1)
                log.AppendLine("  NOTE: multiple objects with this name exist. The largest ACTIVE surface was " +
                               "chosen; the others were not touched.");
            else
                log.AppendLine("  NOTE: exactly one 'Tank Base Roads' object exists - no duplicates to resolve.");

            log.AppendLine("  Mesh inventory of the chosen surface root:");
            foreach (MeshFilter f in ctx.roadsFilters)
            {
                string meshName = f.sharedMesh != null ? f.sharedMesh.name : "(null)";
                int verts = 0;
                int tris = 0;
                string readNote = "";
                try
                {
                    verts = f.sharedMesh.vertices.Length;
                    tris = f.sharedMesh.triangles.Length / 3;
                }
                catch (Exception e)
                {
                    readNote = "  UNREADABLE: " + e.Message;
                }

                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    mesh '{0}' on '{1}'  verts={2}  tris={3}{4}",
                    meshName, GetTransformPath(f.transform), verts, tris, readNote));
            }

            Flush();

            // ---------------- (2/5) SURFACE RASTER ----------------
            string rasterReason;
            SurfaceMap surface = SurfaceMap.Build(ctx.roadsFilters, out rasterReason);

            if (surface == null || surface.coveredCells == 0)
            {
                Debug.LogError("IRONVEIL: The roads surface could not be rasterized. " +
                               "Alignment aborted; nothing changed.\n" +
                               "Reason: " + (rasterReason ?? "(unknown)") + "\n" +
                               "Section (1/5) above lists the detected roads objects and the mesh inventory.");
                return;
            }

            ctx.surface = surface;

            log.AppendLine("--- (2/5) Roads surface raster ---");
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Roads raster: {0}x{0} cells, covered {1}, triangles used {2} (degenerate skipped {3}), " +
                "world bounds X[{4:F3} {5:F3}] Z[{6:F3} {7:F3}] Y[{8:F4} {9:F4}]",
                surface.res, surface.coveredCells, surface.trisRead, surface.trisDegenerate,
                surface.minX, surface.maxX, surface.minZ, surface.maxZ, surface.minY, surface.maxY));
            if (surface.buildNotes != null)
                log.AppendLine("  Build notes:" + surface.buildNotes);

            Flush();

            // ---------------- (3/5) WALL ----------------
            log.AppendLine("--- (3/5) WALL: Tank Base Wall Structure ---");
            ObjectPlan wallPlan = AnalyzeWall(ctx);
            AppendPlan(log, wallPlan);
            Flush();

            // ---------------- (4/5) BUILDINGS ----------------
            log.AppendLine("--- (4/5) BUILDINGS (per-anchor foundation alignment) ---");
            List<ObjectPlan> buildingPlans = new List<ObjectPlan>();
            foreach (AnchorInfo anchor in ctx.anchors)
            {
                ObjectPlan plan = AnalyzeBuilding(ctx, anchor);
                buildingPlans.Add(plan);
                AppendPlan(log, plan);
            }
            Flush();

            // ---------------- (5/5) ROADS LATERAL X/Z + SUMMARY ----------------
            log.AppendLine("--- (5/5) ROADS LATERAL X/Z (report only - NOT modified) ---");
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Roads mesh center XZ (world): ({0:F4}, {1:F4})", surface.centerX, surface.centerZ));
            if (wallPlan != null && wallPlan.ok)
            {
                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Wall ring center XZ (world):  ({0:F4}, {1:F4})",
                    wallPlan.contact.centroid.x, wallPlan.contact.centroid.y));
                float dx = surface.centerX - wallPlan.contact.centroid.x;
                float dz = surface.centerZ - wallPlan.contact.centroid.y;
                float magnitude = Mathf.Sqrt(dx * dx + dz * dz);
                float roadWidth = Mathf.Max(surface.maxX - surface.minX, 1e-6f);
                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Offset (dx={0:F4}, dz={1:F4}) magnitude {2:F4} = {3:F1}% of the roads width",
                    dx, dz, magnitude, 100f * magnitude / roadWidth));
                log.AppendLine("  Assessment: the wall gap is a VERTICAL problem (see section 3/5). " +
                               "The lateral offset only shifts the snow apron sideways relative to the ring - " +
                               "it does not create or remove the gap. Left untouched per your instruction #6.");
            }

            // ---------------- APPLY ----------------
            if (apply)
            {
                string backup = CreateBackupFolder();
                if (backup == null)
                {
                    Debug.LogError("IRONVEIL: Backup failed - alignment ABORTED, nothing was modified.");
                    return;
                }

                log.AppendLine();
                log.AppendLine("--- APPLY (backup at " + backup + ") ---");
                log.AppendLine();

                ApplyPlan(ctx, wallPlan, log);
                foreach (ObjectPlan plan in buildingPlans)
                    ApplyPlan(ctx, plan, log);

                if (ctx.applied.Count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(ctx.baseRoot.gameObject.scene);
                    log.AppendLine();
                    log.AppendLine("Scene marked dirty. Ctrl+S saves it; Ctrl+Z reverts the alignment step-by-step.");
                }
            }
            else
            {
                log.AppendLine();
                log.AppendLine("--- NOTHING MODIFIED (dry run) ---");
            }

            Flush();

            if (apply)
            {
                if (ctx.applied.Count > 0)
                {
                    EditorUtility.DisplayDialog("IRONVEIL - Tank Base Alignment",
                        "Applied:\n" + string.Join("\n", ctx.applied.ToArray()) +
                        "\n\nFull report in Console. Ctrl+S to save the scene.", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("IRONVEIL - Tank Base Alignment",
                        "Nothing was applied.\nEverything is within tolerance, or objects were flagged unreliable " +
                        "(see Console report).", "OK");
                }
            }
            else
            {
                EditorUtility.DisplayDialog("IRONVEIL - Tank Base Alignment (DRY RUN)",
                    "Dry run complete - nothing was modified.\n\n" +
                    "The Console report lists the proposed Y shift for the wall and for every building.\n" +
                    "Review the numbers, then run 'Apply Aligned Offsets (Scene)'.", "OK");
            }
        }
        catch (Exception e)
        {
            StringBuilder err = new StringBuilder();
            err.AppendLine("IRONVEIL: THE " + (apply ? "APPLY" : "DRY RUN") + " ABORTED WITH AN EXCEPTION.");
            if (!apply)
                err.AppendLine("Nothing was modified - this was a dry run.");
            else
                err.AppendLine("If the partial report below already contains 'APPLIED ..." +
                               "' entries, those objects WERE moved - press Ctrl+Z in Unity to revert " +
                               "(a timestamped backup folder also exists under Assets/Backups/).");
            err.AppendLine(e.ToString());
            if (log.Length > 0)
            {
                err.AppendLine();
                err.AppendLine("=== partial report captured before the failure ===");
                err.Append(log.ToString());
            }
            Debug.LogError(err.ToString(), ctx != null && ctx.baseRoot != null ? ctx.baseRoot : null);
        }
    }

    // ------------------------------------------------------------------
    // Context discovery
    // ------------------------------------------------------------------

    private class CandidateInfo
    {
        public Transform transform;
        public string path;
        public string parentName;
        public bool active;
        public int filterCount;
        public int triCount;
        public float area;
        public string note;     // non-null when mesh statistics could not be read
    }

    private class AnchorInfo
    {
        public Transform anchor;
        public string label;
        public Transform building;
    }

    private class BaseContext
    {
        public Transform baseRoot;
        public Transform roadsRoot;
        public List<CandidateInfo> roadCandidates = new List<CandidateInfo>();
        public List<MeshFilter> roadsFilters;
        public SurfaceMap surface;
        public List<AnchorInfo> anchors = new List<AnchorInfo>();
        public List<string> applied = new List<string>();
    }

    private static BaseContext BuildContext()
    {
        List<Transform> masters = new List<Transform>();
        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded)
                continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(false))
                {
                    if (t.name.Equals("Tank Base Master", StringComparison.OrdinalIgnoreCase) ||
                        t.name.Equals("Tank Base", StringComparison.OrdinalIgnoreCase))
                        masters.Add(t);
                }
            }
        }

        if (masters.Count == 0)
            return null;

        BaseContext ctx = new BaseContext();
        ctx.baseRoot = masters.Count == 1
            ? masters[0]
            : (masters.FirstOrDefault(t => t.gameObject.scene == SceneManager.GetActiveScene()) ?? masters[0]);

        if (masters.Count > 1)
        {
            Debug.LogWarning("IRONVEIL: Multiple 'Tank Base'-roots found (" +
                             string.Join(", ", masters.Select(m => "'" + m.name + "' in " + m.gameObject.scene.name).ToArray()) +
                             "). Using '" + ctx.baseRoot.name + "' from scene '" + ctx.baseRoot.gameObject.scene.name + "'.");
        }

        // Roads candidates: every descendant named "Tank Base Roads".
        foreach (Transform t in ctx.baseRoot.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.Equals("Tank Base Roads", StringComparison.OrdinalIgnoreCase))
                continue;

            CandidateInfo info = new CandidateInfo();
            info.transform = t;
            info.path = GetTransformPath(t);
            info.parentName = t.parent != null ? t.parent.name : "(null)";
            info.active = t.gameObject.activeInHierarchy;

            MeshFilter[] filters = t.GetComponentsInChildren<MeshFilter>(false)
                .Where(f => f.sharedMesh != null).ToArray();
            info.filterCount = filters.Length;

            try
            {
                foreach (MeshFilter f in filters)
                {
                    Mesh mesh = f.sharedMesh;
                    info.triCount += mesh.triangles.Length / 3;
                    info.area += MeshXZArea(mesh, f.transform);
                }
            }
            catch (Exception e)
            {
                info.note = "mesh statistics could not be read (" + e.Message + ")";
                Debug.LogWarning("IRONVEIL: " + info.path + " - " + info.note);
            }

            ctx.roadCandidates.Add(info);
        }

        // The largest active surface wins.
        ctx.roadsRoot = ctx.roadCandidates
            .Where(c => c.active && c.filterCount > 0)
            .OrderByDescending(c => c.area)
            .Select(c => c.transform)
            .FirstOrDefault();

        if (ctx.roadsRoot == null)
        {
            Debug.LogError("IRONVEIL: No active 'Tank Base Roads' object with a mesh was found under '" +
                           ctx.baseRoot.name + "'. Surface sampling is impossible - aborting.");
            return null;
        }

        ctx.roadsFilters = ctx.roadsRoot.GetComponentsInChildren<MeshFilter>(false)
            .Where(f => f.sharedMesh != null).ToList();

        ctx.anchors.Clear();

        List<Transform> direct = new List<Transform>();
        foreach (Transform child in ctx.baseRoot)
            direct.Add(child);

        Transform anchorsRoot = direct.FirstOrDefault(t =>
            t.name.IndexOf("Building Anchor", StringComparison.OrdinalIgnoreCase) >= 0);

        if (anchorsRoot != null)
        {
            foreach (Transform anchor in anchorsRoot)
            {
                AnchorInfo ai = new AnchorInfo();
                ai.anchor = anchor;
                ai.label = anchor.name;
                ai.building = FindBuildingUnder(anchor);
                ctx.anchors.Add(ai);
            }
        }
        else
        {
            Debug.LogError("IRONVEIL: No 'Building Anchors' container found under '" + ctx.baseRoot.name + "'.");
        }

        return ctx;
    }

    private static Transform FindBuildingUnder(Transform anchor)
    {
        MeshFilter best = null;
        float bestArea = 0f;

        foreach (MeshFilter f in anchor.GetComponentsInChildren<MeshFilter>(true))
        {
            if (f.sharedMesh == null)
                continue;

            if (f.transform == anchor)
                continue;

            float area = MeshXZArea(f.sharedMesh, f.transform);
            if (area > bestArea)
            {
                bestArea = area;
                best = f;
            }
        }

        return best != null ? best.transform : null;
    }

    private static string GetTransformPath(Transform t)
    {
        StringBuilder sb = new StringBuilder(t.name);
        Transform p = t.parent;
        while (p != null)
        {
            sb.Insert(0, p.name + "/");
            p = p.parent;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Surface raster - top-of-mesh height map in WORLD space
    // ------------------------------------------------------------------

    private class SurfaceMap
    {
        public int res;
        public float cell;
        public Vector2 origin;
        public float[,] height;
        public bool[,] hasValue;
        public int coveredCells;
        public float minX, maxX, minZ, maxZ, minY, maxY;
        public float centerX, centerZ;

        public string buildNotes;   // diagnostics captured while building (null when none)
        public int trisRead;        // triangles actually included in rasterization
        public int trisDegenerate;  // triangles skipped as degenerate in XZ

        public bool TrySample(Vector2 xz, out float y)
        {
            y = float.MinValue;

            int cx = Mathf.Clamp((int)((xz.x - origin.x) / cell), 0, res - 1);
            int cz = Mathf.Clamp((int)((xz.y - origin.y) / cell), 0, res - 1);

            if (hasValue[cx, cz])
            {
                y = height[cx, cz];
                return true;
            }

            // Neighborhood fallback in rings.
            for (int r = 1; r <= 2; r++)
            {
                bool any = false;
                float bestD = float.MaxValue;
                float bestY = 0f;

                for (int ox = -r; ox <= r; ox++)
                {
                    for (int oz = -r; oz <= r; oz++)
                    {
                        if (Mathf.Abs(ox) < r && Mathf.Abs(oz) < r)
                            continue;   // ring perimeter cells only

                        int nx = cx + ox;
                        int nz = cz + oz;
                        if (nx < 0 || nz < 0 || nx >= res || nz >= res || !hasValue[nx, nz])
                            continue;

                        Vector2 cpos = origin + new Vector2((nx + 0.5f) * cell, (nz + 0.5f) * cell);
                        float d = Vector2.Distance(xz, cpos);
                        if (d < bestD)
                        {
                            bestD = d;
                            bestY = height[nx, nz];
                            any = true;
                        }
                    }
                }

                if (any)
                {
                    y = bestY;
                    return true;
                }
            }

            return false;
        }

        public static SurfaceMap Build(List<MeshFilter> filters, out string failureReason)
        {
            failureReason = null;

            if (filters == null || filters.Count == 0)
            {
                failureReason = "no mesh filters were collected under the chosen roads root";
                return null;
            }

            StringBuilder notes = new StringBuilder();

            Vector3 bMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            int totalVerts = 0;

            foreach (MeshFilter f in filters)
            {
                Vector3[] vertices;
                try
                {
                    vertices = f.sharedMesh.vertices;
                }
                catch (Exception e)
                {
                    notes.AppendLine("    UNREADABLE mesh '" +
                                     (f.sharedMesh != null ? f.sharedMesh.name : "(null)") + "' under '" +
                                     GetTransformPath(f.transform) + "': " + e.Message);
                    continue;
                }

                totalVerts += vertices.Length;

                Matrix4x4 m = f.transform.localToWorldMatrix;

                foreach (Vector3 v in vertices)
                {
                    Vector3 w = m.MultiplyPoint3x4(v);
                    if (w.x < bMin.x) bMin.x = w.x;
                    if (w.y < bMin.y) bMin.y = w.y;
                    if (w.z < bMin.z) bMin.z = w.z;
                    if (w.x > bMax.x) bMax.x = w.x;
                    if (w.y > bMax.y) bMax.y = w.y;
                    if (w.z > bMax.z) bMax.z = w.z;
                }
            }

            if (totalVerts == 0)
            {
                failureReason = "no mesh vertices could be read" +
                                (notes.Length > 0 ? ":\n" + notes.ToString() : ".");
                return null;
            }

            float span = Mathf.Max(bMax.x - bMin.x, bMax.z - bMin.z);
            if (span <= 1e-6f)
            {
                failureReason = "roads geometry has no meaningful XZ extent (span " +
                                span.ToString("R", CultureInfo.InvariantCulture) + ")";
                return null;
            }

            SurfaceMap map = new SurfaceMap();
            map.res = GridRes;
            map.cell = span / GridRes;
            map.minX = bMin.x;
            map.maxX = bMax.x;
            map.minZ = bMin.z;
            map.maxZ = bMax.z;
            map.origin = new Vector2(bMin.x - 2 * map.cell, bMin.z - 2 * map.cell);
            map.height = new float[GridRes, GridRes];
            map.hasValue = new bool[GridRes, GridRes];

            int trisRead = 0;
            int trisDegenerate = 0;

            foreach (MeshFilter f in filters)
            {
                Mesh mesh = f.sharedMesh;

                Vector3[] v;
                int[] tri;
                try
                {
                    v = mesh.vertices;
                    tri = mesh.triangles;
                }
                catch (Exception e)
                {
                    notes.AppendLine("    UNREADABLE mesh '" + mesh.name + "' under '" +
                                     GetTransformPath(f.transform) + "': " + e.Message);
                    continue;
                }

                if (tri == null || tri.Length < 3)
                {
                    notes.AppendLine("    mesh '" + mesh.name + "' under '" +
                                     GetTransformPath(f.transform) + "' has no triangles.");
                    continue;
                }

                Matrix4x4 m = f.transform.localToWorldMatrix;

                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    Vector3 a = m.MultiplyPoint3x4(v[tri[i]]);
                    Vector3 b = m.MultiplyPoint3x4(v[tri[i + 1]]);
                    Vector3 c = m.MultiplyPoint3x4(v[tri[i + 2]]);

                    float area2 = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
                    if (Mathf.Abs(area2) < 1e-9f)
                    {
                        trisDegenerate++;
                        continue;
                    }

                    trisRead++;

                    int ix0 = Mathf.Clamp((int)((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - map.origin.x) / map.cell), 0, GridRes - 1);
                    int ix1 = Mathf.Clamp((int)((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - map.origin.x) / map.cell), 0, GridRes - 1);
                    int iz0 = Mathf.Clamp((int)((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - map.origin.y) / map.cell), 0, GridRes - 1);
                    int iz1 = Mathf.Clamp((int)((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - map.origin.y) / map.cell), 0, GridRes - 1);

                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        for (int iz = iz0; iz <= iz1; iz++)
                        {
                            float px = map.origin.x + (ix + 0.5f) * map.cell;
                            float pz = map.origin.y + (iz + 0.5f) * map.cell;

                            float wa = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / area2;
                            float wb = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / area2;
                            float wc = 1f - wa - wb;

                            if (wa < -0.002f || wb < -0.002f || wc < -0.002f)
                                continue;

                            float y = wa * a.y + wb * b.y + wc * c.y;

                            if (!map.hasValue[ix, iz])
                            {
                                map.hasValue[ix, iz] = true;
                                map.height[ix, iz] = y;
                                map.coveredCells++;
                            }
                            else if (y > map.height[ix, iz])
                            {
                                map.height[ix, iz] = y;
                            }
                        }
                    }
                }
            }

            if (map.coveredCells == 0)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "no triangles were rasterized onto the {0}x{0} grid (non-degenerate triangles attempted: {1}, " +
                    "skipped as degenerate: {2})", GridRes, trisRead, trisDegenerate);
                if (notes.Length > 0)
                    failureReason += "\nNotes:" + notes.ToString();
                return null;
            }

            map.trisRead = trisRead;
            map.trisDegenerate = trisDegenerate;
            map.buildNotes = notes.Length > 0 ? notes.ToString() : null;

            float sx = 0f, sz = 0f;
            int cnt = 0;
            map.minY = float.MaxValue;
            map.maxY = float.MinValue;

            for (int ix = 0; ix < GridRes; ix++)
            {
                for (int iz = 0; iz < GridRes; iz++)
                {
                    if (!map.hasValue[ix, iz])
                        continue;

                    float y = map.height[ix, iz];
                    if (y < map.minY) map.minY = y;
                    if (y > map.maxY) map.maxY = y;

                    sx += map.origin.x + (ix + 0.5f) * map.cell;
                    sz += map.origin.y + (iz + 0.5f) * map.cell;
                    cnt++;
                }
            }

            if (cnt > 0)
            {
                map.centerX = sx / cnt;
                map.centerZ = sz / cnt;
            }
            else
            {
                map.centerX = (map.minX + map.maxX) * 0.5f;
                map.centerZ = (map.minZ + map.maxZ) * 0.5f;
            }

            return map;
        }
    }

    // ------------------------------------------------------------------
    // Contact-band extraction (bottom band + dominant level histogram)
    // ------------------------------------------------------------------

    private class ContactInfo
    {
        public bool ok;
        public string error;

        public List<Vector3> points = new List<Vector3>();  // world-space contact points
        public float planeY;      // current world Y of the contact plane (mean)
        public float planeStd;    // levelness of the contact band (RMS)
        public float objectHeight;
        public Vector2 centroid;  // XZ center of the contact points
    }

    private static ContactInfo ExtractContact(Transform target)
    {
        List<Vector3> verts = new List<Vector3>();
        string readError = null;

        foreach (MeshFilter f in target.GetComponentsInChildren<MeshFilter>(true))
        {
            if (f.sharedMesh == null)
                continue;

            Vector3[] v;
            try
            {
                v = f.sharedMesh.vertices;
            }
            catch (Exception e)
            {
                readError = "could not read vertices of mesh '" + f.sharedMesh.name + "': " + e.Message;
                break;
            }

            Matrix4x4 m = f.transform.localToWorldMatrix;

            for (int i = 0; i < v.Length; i++)
                verts.Add(m.MultiplyPoint3x4(v[i]));
        }

        if (readError != null)
            return new ContactInfo { ok = false, error = readError };

        if (verts.Count < 24)
            return new ContactInfo { ok = false, error = "subtree has fewer than 24 mesh vertices" };

        float ymin = float.MaxValue;
        float ymax = float.MinValue;
        foreach (Vector3 p in verts)
        {
            if (p.y < ymin) ymin = p.y;
            if (p.y > ymax) ymax = p.y;
        }

        float height = ymax - ymin;
        if (height <= 1e-5f)
            return new ContactInfo { ok = false, error = "object has no vertical extent" };

        // Bottom band.
        float bandTop = ymin + height * ContactBandFraction;
        List<Vector3> band = new List<Vector3>();
        foreach (Vector3 p in verts)
        {
            if (p.y <= bandTop)
                band.Add(p);
        }

        if (band.Count < 8)
            return new ContactInfo { ok = false, error = "fewer than 8 vertices in the bottom band" };

        // Dominant Y level inside the band. This rejects interior courtyard / debris
        // vertices that sit BELOW the actual foundation (they form their own, much
        // smaller histogram bin and are therefore ignored).
        int bins = ContactHistogramBins;
        float lo = ymin;
        float binWidth = (bandTop - lo) / bins;
        int[] counts = new int[bins];

        foreach (Vector3 p in band)
        {
            int b = Mathf.Clamp((int)((p.y - lo) / (bandTop - lo + 1e-9f) * bins), 0, bins - 1);
            counts[b]++;
        }

        int best1 = 0;
        for (int b = 1; b < bins; b++)
            if (counts[b] > counts[best1])
                best1 = b;

        float bandLo = Mathf.Max(ymin, (lo + best1 * binWidth) - binWidth * 0.5f);
        float bandHi = Mathf.Min(bandTop, (lo + (best1 + 1) * binWidth) + binWidth * 0.5f);

        ContactInfo info = new ContactInfo();

        float sumY = 0f, sumX = 0f, sumZ = 0f;
        foreach (Vector3 p in band)
        {
            if (p.y < bandLo || p.y > bandHi)
                continue;

            info.points.Add(p);
            sumY += p.y;
            sumX += p.x;
            sumZ += p.z;
        }

        if (info.points.Count < 8)
            return new ContactInfo { ok = false, error = "dominant contact level has fewer than 8 vertices" };

        info.planeY = sumY / info.points.Count;

        float vt = 0f;
        foreach (Vector3 p in info.points)
        {
            float d = p.y - info.planeY;
            vt += d * d;
        }
        info.planeStd = Mathf.Sqrt(vt / info.points.Count);

        info.centroid = new Vector2(sumX / info.points.Count, sumZ / info.points.Count);
        info.objectHeight = height;
        info.ok = true;

        return info;
    }

    // ------------------------------------------------------------------
    // Plan objects
    // ------------------------------------------------------------------

    private class ObjectPlan
    {
        public string label;
        public Transform target;
        public ContactInfo contact;
        public bool ok;
        public string error;      // analysis failure description; null when plan.ok == true

        public bool isRing;
        public float ringInner, ringCenter, ringOuter;   // world radii (fit)
        public float ringLevelStd;

        public int samplesTotal;
        public int samplesResolved;
        public List<string> failedSamples = new List<string>();

        // Surface statistics at the sampling footprint (world).
        public float sMin = float.MaxValue;
        public float sP10 = float.MaxValue;
        public float sMedian = float.MaxValue;
        public float sMax = float.MinValue;

        public float Target => sMin;
        public float DeltaWorld => Target - contact.planeY;
    }

    private static void AppendPlan(StringBuilder log, ObjectPlan plan)
    {
        log.AppendLine();

        if (plan == null)
        {
            log.AppendLine("  (no plan)");
            return;
        }

        if (!plan.ok)
        {
            log.AppendLine("  " + plan.label + ": NOT ANALYZED - " + plan.error);
            return;
        }

        ContactInfo c = plan.contact;

        log.AppendLine("  Object           : " + plan.label + "   (path: " + GetTransformPath(plan.target) + ")");

        if (plan.isRing)
        {
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  Ring fit         : inner {0:F4}  center {1:F4}  outer {2:F4} (world units)", plan.ringInner, plan.ringCenter, plan.ringOuter));
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  Ring level RMS   : {0:F4}", plan.ringLevelStd));
        }

        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  Object height    : {0:F4}   (world)", c.objectHeight));
        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  Contact plane    : {0:F4}   level-RMS vs plane {1:F4} | contact points {2}",
            c.planeY, c.planeStd, c.points.Count));
        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  Surface samples  : resolved {0}/{1}{2}",
            plan.samplesResolved, plan.samplesTotal, plan.failedSamples.Count > 0
                ? "  UNRESOLVED at: " + string.Join("; ", plan.failedSamples.Take(6).ToArray())
                : ""));
        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  Surface stats    : min {0:F4} | P10 {1:F4} | median {2:F4} | max {3:F4}   (world)",
            plan.sMin, plan.sP10, plan.sMedian, plan.sMax));

        float parentScaleY = ParentScaleY(plan.target);
        float deltaWorld = plan.DeltaWorld;
        float deltaLocal = deltaWorld / Mathf.Max(parentScaleY, 1e-9f);

        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  PROPOSED SHIFT   : contact plane -> surface min {0:F4}; move object by dy={1:F4} world",
            plan.Target, deltaWorld));
        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "                     (local Y offset change {0:F4}, parent scale {1:F4})",
            deltaLocal, parentScaleY));
        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  After the shift  : max burial into surface = {0:F4} ({1:F1}% of height), floating gap at contact points = 0",
            plan.sMax - plan.sMin, 100f * (plan.sMax - plan.sMin) / Mathf.Max(c.objectHeight, 1e-9f)));

        if ((float)plan.samplesResolved / plan.samplesTotal <= 1f - UnreliableSampleLimit)
            log.AppendLine("  WARNING: sampling reliability below threshold - Apply will SKIP this object.");

        if (Mathf.Abs(deltaWorld) < Epsilon)
            log.AppendLine("  Note: already within tolerance - Apply will not touch this object.");
    }

    // ------------------------------------------------------------------
    // Wall analysis
    // ------------------------------------------------------------------

    private static ObjectPlan AnalyzeWall(BaseContext ctx)
    {
        ObjectPlan plan = new ObjectPlan();
        plan.label = "Tank Base Wall Structure";
        plan.isRing = true;

        Transform wall = null;
        foreach (Transform child in ctx.baseRoot)
        {
            if (child.name.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                wall = child;
                break;
            }
        }

        if (wall == null)
        {
            plan.ok = false;
            plan.error = "wall object not found under '" + ctx.baseRoot.name + "'";
            return plan;
        }

        plan.target = wall;

        ContactInfo c = ExtractContact(wall);
        plan.contact = c;

        if (!c.ok)
        {
            plan.ok = false;
            plan.error = "wall contact extraction failed: " + c.error;
            return plan;
        }

        // ---- ring fit (rejects stray courtyard vertices by radius clustering) ----
        List<float> radii = new List<float>();
        foreach (Vector3 p in c.points)
            radii.Add(Vector2.Distance(new Vector2(p.x, p.z), c.centroid));

        radii.Sort();

        float extentGuess = Mathf.Max(
            (ctx.surface.maxX - ctx.surface.minX) * 0.5f,
            radii[radii.Count / 2] * 1.5f);

        float clusterGap = 0.08f * Mathf.Max(extentGuess, 0.01f);
        List<float> bestCluster = new List<float>();
        List<float> curCluster = new List<float>();

        for (int i = 0; i < radii.Count; i++)
        {
            if (curCluster.Count > 0 && radii[i] - curCluster[curCluster.Count - 1] > clusterGap)
            {
                if (curCluster.Count > bestCluster.Count)
                    bestCluster = curCluster;
                curCluster = new List<float>();
            }
            curCluster.Add(radii[i]);
        }
        if (curCluster.Count > bestCluster.Count)
            bestCluster = curCluster;

        if (bestCluster.Count < 8 || bestCluster.Count < c.points.Count / 2)
        {
            plan.ok = false;
            plan.error = string.Format(CultureInfo.InvariantCulture,
                "ring fit failed: dominant radius cluster too small ({0} of {1} contact points)",
                bestCluster.Count, c.points.Count);
            return plan;
        }

        plan.ringInner = bestCluster[0];
        plan.ringOuter = bestCluster[bestCluster.Count - 1];
        plan.ringCenter = bestCluster[bestCluster.Count / 2];

        // Levelness of the ring contact band.
        float ry = 0f;
        foreach (Vector3 p in c.points)
            ry += p.y;
        ry /= c.points.Count;
        float rv = 0f;
        foreach (Vector3 p in c.points)
        {
            float d = p.y - ry;
            rv += d * d;
        }
        plan.ringLevelStd = Mathf.Sqrt(rv / c.points.Count);

        // ---- perimeter sampling along the ring centerline ----
        List<Vector2> centerline = new List<Vector2>();
        for (int i = 0; i < WallAngleSamples; i++)
        {
            float ang = Mathf.PI * 2f * i / WallAngleSamples;
            Vector2 p = c.centroid + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * plan.ringCenter;
            centerline.Add(p);
        }

        List<string> fails;
        List<float> surfaceHeights = SampleSurfaceList(ctx, centerline, out fails);

        plan.samplesTotal = centerline.Count;
        plan.samplesResolved = surfaceHeights.Count;
        plan.failedSamples = fails;

        if (surfaceHeights.Count == 0)
        {
            plan.ok = false;
            plan.error = "the roads surface could not be sampled anywhere along the ring centerline";
            return plan;
        }

        if (fails.Count > WallAngleSamples * UnreliableSampleLimit)
        {
            plan.ok = false;
            plan.error = string.Format(CultureInfo.InvariantCulture,
                "surface could not be sampled at {0}/{1} perimeter points ({2}% unresolved)",
                fails.Count, WallAngleSamples, Mathf.RoundToInt(100f * fails.Count / WallAngleSamples));
            return plan;
        }

        surfaceHeights.Sort();
        plan.sMin = surfaceHeights[0];
        plan.sMedian = surfaceHeights[surfaceHeights.Count / 2];
        plan.sP10 = surfaceHeights[Mathf.Clamp((int)(surfaceHeights.Count * 0.1f), 0, surfaceHeights.Count - 1)];
        plan.sMax = surfaceHeights[surfaceHeights.Count - 1];

        plan.ok = true;
        return plan;
    }

    // ------------------------------------------------------------------
    // Building analysis
    // ------------------------------------------------------------------

    private static ObjectPlan AnalyzeBuilding(BaseContext ctx, AnchorInfo anchor)
    {
        ObjectPlan plan = new ObjectPlan();
        plan.label = anchor.label;

        if (anchor.building == null)
        {
            plan.ok = false;
            plan.error = "no building mesh found under this anchor";
            return plan;
        }

        plan.target = anchor.building;

        ContactInfo c = ExtractContact(anchor.building);
        plan.contact = c;

        if (!c.ok)
        {
            plan.ok = false;
            plan.error = "building contact extraction failed: " + c.error;
            return plan;
        }

        List<Vector2> samplePoints = Reduce(
            c.points.Select(p => new Vector2(p.x, p.z)).ToList(), MaxPointsPerObject);

        List<string> fails;
        List<float> heights = SampleSurfaceList(ctx, samplePoints, out fails);

        plan.samplesTotal = samplePoints.Count;
        plan.samplesResolved = heights.Count;
        plan.failedSamples = fails;

        if (heights.Count == 0)
        {
            plan.ok = false;
            plan.error = "none of the foundation contact points overlaps the roads surface";
            return plan;
        }

        if (fails.Count > samplePoints.Count * UnreliableSampleLimit)
        {
            plan.ok = false;
            plan.error = string.Format(CultureInfo.InvariantCulture,
                "surface could not be sampled at {0}/{1} foundation points",
                fails.Count, samplePoints.Count);
            return plan;
        }

        heights.Sort();
        plan.sMin = heights[0];
        plan.sMedian = heights[heights.Count / 2];
        plan.sP10 = heights[Mathf.Clamp((int)(heights.Count * 0.1f), 0, heights.Count - 1)];
        plan.sMax = heights[heights.Count - 1];

        plan.ok = true;
        return plan;
    }

    // ------------------------------------------------------------------
    // Apply
    // ------------------------------------------------------------------

    private static void ApplyPlan(BaseContext ctx, ObjectPlan plan, StringBuilder log)
    {
        if (plan == null || !plan.ok || plan.target == null)
        {
            log.AppendLine("  SKIP " + (plan != null ? plan.label : "(null)") + ": " +
                           (plan != null && plan.error != null ? plan.error : "unreliable"));
            return;
        }

        if (plan.samplesResolved < plan.samplesTotal * (1f - UnreliableSampleLimit))
        {
            log.AppendLine("  SKIP " + plan.label + ": sampling reliability below threshold.");
            return;
        }

        float deltaWorld = plan.DeltaWorld;

        if (Mathf.Abs(deltaWorld) < Epsilon)
        {
            ctx.applied.Add(plan.label + ": already aligned (delta " +
                            deltaWorld.ToString("F5", CultureInfo.InvariantCulture) + ")");
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0}: already aligned (delta {1:F5}) - not touched.",
                plan.label, deltaWorld));
            return;
        }

        float parentScaleY = ParentScaleY(plan.target);
        float deltaLocal = deltaWorld / Mathf.Max(parentScaleY, 1e-9f);

        Undo.RecordObject(plan.target, "IRONVEIL: Align " + plan.label);

        Vector3 lp = plan.target.localPosition;
        lp.y -= deltaLocal;
        plan.target.localPosition = lp;

        EditorUtility.SetDirty(plan.target);

        string entry = string.Format(CultureInfo.InvariantCulture,
            "{0}: target {1} dyLocal={2:F4} (dyWorld={3:F4}); contact plane {4:F4} -> {5:F4} (surface min)",
            plan.label, plan.target.name, deltaLocal, deltaWorld, plan.contact.planeY, plan.Target);

        ctx.applied.Add(entry);
        log.AppendLine("  APPLIED " + entry);
    }

    private static float ParentScaleY(Transform t)
    {
        Transform p = t.parent;
        float s = p != null ? Mathf.Abs(p.lossyScale.y) : 1f;
        return Mathf.Max(s, 1e-9f);
    }

    // ------------------------------------------------------------------
    // Sampling helpers
    // ------------------------------------------------------------------

    private static List<float> SampleSurfaceList(BaseContext ctx, List<Vector2> points, out List<string> failures)
    {
        failures = new List<string>();
        List<float> heights = new List<float>();
        const int maxReported = 6;

        for (int i = 0; i < points.Count; i++)
        {
            float y;
            if (ctx.surface.TrySample(points[i], out y))
            {
                heights.Add(y);
            }
            else if (failures.Count < maxReported)
            {
                failures.Add(string.Format(CultureInfo.InvariantCulture,
                    "#({0:F3},{1:F3})", points[i].x, points[i].y));
            }
        }

        return heights;
    }

    private static List<Vector2> Reduce(List<Vector2> source, int max)
    {
        if (source.Count <= max)
            return source;

        List<Vector2> reduced = new List<Vector2>(max);
        float step = (float)source.Count / max;

        for (int i = 0; i < max; i++)
            reduced.Add(source[Mathf.Clamp((int)(i * step), 0, source.Count - 1)]);

        return reduced;
    }

    // ------------------------------------------------------------------
    // Backup
    // ------------------------------------------------------------------

    private static string CreateBackupFolder()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine("Assets", "Backups", "Alignment_" + stamp);
            Directory.CreateDirectory(dir);

            CopyIfExists("Assets/Prefabs/Tank Base.prefab", dir, "Tank Base");
            CopyIfExists("Assets/Scenes/Ironveil - MainBuild.unity", dir, "Ironveil - MainBuild");
            CopyIfExists("Assets/Prefabs/Base-Outpost.prefab", dir, "Base-Outpost");

            AssetDatabase.Refresh();
            return dir;
        }
        catch (Exception e)
        {
            Debug.LogError("IRONVEIL: backup failed - " + e.Message + ". Alignment aborted, nothing was modified.");
            return null;
        }
    }

    private static void CopyIfExists(string path, string dir, string friendly)
    {
        if (!File.Exists(path))
            return;

        string dest = Path.Combine(dir, Path.GetFileName(path));
        File.Copy(path, dest, true);
        Debug.Log("IRONVEIL: backed up " + friendly + " -> " + dest);
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------

    private static string P(Vector3 v)
    {
        return string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", v.x, v.y, v.z);
    }

    private static string S(Vector3 s)
    {
        return string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4})", s.x, s.y, s.z);
    }

    private static float MeshXZArea(Mesh mesh, Transform t)
    {
        float area = 0f;
        Vector3[] v = mesh.vertices;
        int[] tri = mesh.triangles;
        Matrix4x4 m = t.localToWorldMatrix;

        for (int i = 0; i + 2 < tri.Length; i += 3)
        {
            Vector3 a = m.MultiplyPoint3x4(v[tri[i]]);
            Vector3 b = m.MultiplyPoint3x4(v[tri[i + 1]]);
            Vector3 c = m.MultiplyPoint3x4(v[tri[i + 2]]);
            area += Mathf.Abs((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x)) * 0.5f;
        }

        return area;
    }
}
