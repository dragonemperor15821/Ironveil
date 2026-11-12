using System.Text;
using UnityEditor;
using UnityEngine;

// ----------------------------------------------------------------------------
// ONE-OFF utility - IRONVEIL ground-contact fix (report/dry-run validated).
//
// Applies four SMALL vertical shifts to FOUR objects of the "Tank Base Master"
// scene instance ONLY:
//
//   1. "Tank Base Wall Structure"      world Y -0.0046  (P10 compromise)
//   2. "Level 10 Safety Power Plant"   world Y -0.0230  (MIN rule)
//   3. "Level 5 Refinery"              world Y -0.0651  (MIN on deck-ring plane)
//   4. "Level 3 Resource Optimiser"    world Y -0.0108  (MIN rule)
//
// Constraints honoured:
//   - changes are recorded as prefab-instance overrides on the Tank Base
//     Master scene instance only (NOT applied to the prefab asset, NOT applied
//     to any of the 350 spawned Tank Base instances);
//   - lateral positions, scales, rotations, road transforms, FBX files,
//     BaseSpawner.cs and BaseController.cs are never touched;
//   - does NOT use TankBaseAlignment.ApplyPlan (its sign convention is inverted
//     and was therefore bypassed entirely);
//   - every shift is undoable (single undo group) and there is a matching
//     restore menu item.
//
// Delete this file after sign-off.
// ----------------------------------------------------------------------------
public static class TankBaseGroundContact
{
    private struct Fix
    {
        public string goName;
        public float expectWorldY;   // guard: expected current world Y
        public float deltaWorld;     // signed world shift (negative = down)
        public string note;
    }

    // NOTE: the goName values must match the scene GameObject names exactly;
    // the MISMATCH guard + skip logic will refuse wrong targets instead of
    // guessing. Observed names verified offline:
    //   "Tank Base Wall Structure", "Level 5 Refinery",
    //   "Level 10 Power Plant", "Level 3 Resource Optimiser"
    private static readonly Fix[] Fixes =
    {
        new Fix
        {
            goName = "Tank Base Wall Structure",
            expectWorldY = -0.0060f,
            deltaWorld = -0.0046f,
            note = "P10 compromise of ring road contact (fallback MIN = -0.0175)"
        },
        new Fix
        {
            goName = "Level 10 Power Plant",
            expectWorldY = 0.0300f,
            deltaWorld = -0.0230f,
            note = "MIN rule: plane 0.0576 -> surface min 0.0346; expected burial 15.6% (dry-run gold 15.6%)"
        },
        new Fix
        {
            goName = "Level 5 Refinery",
            expectWorldY = 0.0530f,
            deltaWorld = -0.0651f,
            note = "MIN rule on deck-ring plane 0.0991 -> 0.0340 (surface min under CURRENT footprint)"
        },
        new Fix
        {
            goName = "Level 3 Resource Optimiser",
            expectWorldY = 0.0370f,
            deltaWorld = -0.0108f,
            note = "MIN rule: plane 0.0378 -> surface min 0.0270; expected burial 24.0% (gold 24.6%)"
        },
    };

    [MenuItem("IRONVEIL/Ground Contact/01 - Apply Fix")]
    public static void ApplyFix()
    {
        Run(true);
    }

    [MenuItem("IRONVEIL/Ground Contact/02 - Restore Original Values")]
    public static void RestoreOriginal()
    {
        Run(false);
    }

    private static void Run(bool apply)
    {
        var master = GameObject.Find("Tank Base Master");
        if (master == null)
        {
            Debug.LogError("[GroundContact] No active 'Tank Base Master' in the open scene - nothing changed.");
            return;
        }

        Undo.SetCurrentGroupName("IRONVEIL ground contact fix");
        int group = Undo.GetCurrentGroup();

        var log = new StringBuilder();
        log.AppendLine("[GroundContact] " + (apply ? "APPLY" : "RESTORE") +
                       " on '" + master.name + "'  (path: " + GetPath(master.transform) + ")" +
                       "  - instance-scoped overrides only");

        bool allOk = true;
        foreach (var fix in Fixes)
        {
            Transform t = FindUniqueChildByName(master.transform, fix.goName);
            if (t == null)
            {
                log.AppendLine("  SKIP " + fix.goName + ": not found (or found more than once) under the Master instance.");
                allOk = false;
                continue;
            }

            float worldY = t.position.y;
            float expectY = apply ? fix.expectWorldY : fix.expectWorldY + fix.deltaWorld;
            if (Mathf.Abs(worldY - expectY) > 0.002f)
            {
                log.AppendLine("  SKIP " + fix.goName + ": MISMATCH guard - live world Y is " +
                               F(worldY) + " but expected " + F(expectY) +
                               ". Nothing changed for this object.");
                allOk = false;
                continue;
            }

            Transform parent = t.parent;
            float parentScaleY = parent != null ? Mathf.Abs(Mathf.Abs(parent.lossyScale.y) < 1e-9f ? 1e-9f : parent.lossyScale.y) : 1f;

            Undo.RecordObject(t, "IRONVEIL ground contact fix");

            float deltaLocal = Mathf.Round((fix.deltaWorld / parentScaleY) * 10000f) / 10000f;
            if (!apply)
            {
                deltaLocal = -deltaLocal;
            }

            Vector3 lp = t.localPosition;
            lp.y += deltaLocal;
            t.localPosition = lp;

            // keep 4-decimal YAML-clean values (scene diff friendly)
            var snapped = t.localPosition;
            snapped.y = Mathf.Round(snapped.y * 10000f) / 10000f;
            t.localPosition = snapped;

            PrefabUtility.RecordPrefabInstancePropertyModifications(t);
            EditorUtility.SetDirty(t);

            log.AppendLine("  " + (apply ? "SHIFTED " : "RESTORED ") + fix.goName +
                           "  world Y " + F(worldY) + " -> " + F(t.position.y) +
                           "  local Y " + F(lp.y) + " (parentScaleY " + F(parentScaleY) + ")" +
                           "  - " + fix.note);
        }

        log.AppendLine(allOk
            ? "Done. One undo group (Ctrl-Z reverts all four). Do NOT click Major/Apply All on the Master prefab overrides while these overrides exist."
            : "Completed WITH SKIPS - resolve the MISMATCH guards before retrying.");

        Debug.Log(log.ToString());
        Undo.CollapseUndoOperations(group);
    }

    private static Transform FindUniqueChildByName(Transform root, string goName)
    {
        int count = 0;
        Transform hit = null;
        Search(root);
        return (count == 1) ? hit : null;

        void Search(Transform node)
        {
            if (node != null && node.name == goName)
            {
                count++;
                if (count == 1)
                {
                    hit = node;
                }
                return;
            }
            foreach (Transform child in node)
            {
                Search(child);
            }
        }
    }

    private static string F(float v)
    {
        return v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetPath(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
        }
        return sb.ToString();
    }
}
