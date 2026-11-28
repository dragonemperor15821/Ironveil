using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Freezes the APPROVED 28-Flak positions from the nine foundation prefabs into
/// FlakDefensiveLayout assets (one per distinct layout) plus a FlakLayoutCatalog.
/// Reads the prefabs only; never modifies them.
/// </summary>
public static class FlakLayoutFreezer
{
    public const string LayoutDir = ModularFoundationBuilder.DataDir + "/FlakLayouts";
    public const string CatalogPath = LayoutDir + "/FlakLayoutCatalog.asset";

    [MenuItem("Tools/IRONVEIL/Modular Bases/Freeze Approved Flak Layouts")]
    public static void FreezeFromMenu() => Debug.Log(Freeze());

    public static void FreezeBatch()
    {
        string log = Freeze();
        ModularFoundationBuilder.WriteOut("freeze_report.txt", log);
        Debug.Log(log);
    }

    /// <summary>Default construction order: inner rows first, entrance outwards, right then left.</summary>
    public static int ConstructionOrder(bool right, int row, int index) => (row - 1) * 14 + (index - 1) * 2 + (right ? 1 : 2);

    private static string Signature(List<FlakDefensiveLayout.Position> ps)
    {
        StringBuilder sb = new StringBuilder();
        foreach (var p in ps.OrderBy(p => p.slotId, StringComparer.Ordinal))
        {
            sb.Append(p.slotId).Append(':')
              .Append(BitConverter.SingleToInt32Bits(p.localPosition.x)).Append(',')
              .Append(BitConverter.SingleToInt32Bits(p.localPosition.y)).Append(',')
              .Append(BitConverter.SingleToInt32Bits(p.localPosition.z)).Append(',')
              .Append(p.yawDegrees.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }

        return sb.ToString();
    }

    public static List<FlakDefensiveLayout.Position> ReadPositions(GameObject foundation)
    {
        List<FlakDefensiveLayout.Position> list = new List<FlakDefensiveLayout.Position>();
        Transform root = foundation.transform;
        foreach (BuildingSlot s in foundation.GetComponent<BaseBuildingState>().Slots.Where(s => s.Accepts(BuildingType.Flak)))
        {
            string id = s.SlotId;                                   // Flak_R1_01
            bool right = id[5] == 'R';
            int row = id[6] - '0';
            int index = int.Parse(id.Substring(8, 2), CultureInfo.InvariantCulture);
            Vector3 local = root.InverseTransformPoint(s.transform.position);
            if (s.transform.parent.localPosition == Vector3.zero && s.transform.parent.localRotation == Quaternion.identity &&
                s.transform.parent.localScale == Vector3.one && s.transform.parent.parent == root)
            {
                local = s.transform.localPosition;                  // exact serialized values
            }

            float yaw = Mathf.DeltaAngle(0f, s.transform.localEulerAngles.y);
            list.Add(new FlakDefensiveLayout.Position
            {
                slotId = id, right = right, row = row, index = index, localPosition = local, yawDegrees = yaw,
                localRotation = s.transform.localRotation,
                constructionOrder = ConstructionOrder(right, row, index)
            });
        }

        return list.OrderBy(p => p.constructionOrder).ToList();
    }

    public static string Freeze()
    {
        StringBuilder log = new StringBuilder("=== Freeze approved Flak layouts ===\n");
        if (!AssetDatabase.IsValidFolder(LayoutDir)) AssetDatabase.CreateFolder(ModularFoundationBuilder.DataDir, "FlakLayouts");

        // Group base types with bit-identical Flak positions.
        Dictionary<string, List<ModularFoundationBuilder.BaseSpec>> groups = new Dictionary<string, List<ModularFoundationBuilder.BaseSpec>>();
        Dictionary<string, List<FlakDefensiveLayout.Position>> positionsBySig = new Dictionary<string, List<FlakDefensiveLayout.Position>>();
        Dictionary<string, BaseFoundation> sample = new Dictionary<string, BaseFoundation>();
        foreach (var spec in ModularFoundationBuilder.Bases)
        {
            GameObject f = AssetDatabase.LoadAssetAtPath<GameObject>(ModularFoundationBuilder.FoundationPath(spec.baseType));
            var ps = ReadPositions(f);
            string sig = Signature(ps);
            if (!groups.ContainsKey(sig)) { groups[sig] = new List<ModularFoundationBuilder.BaseSpec>(); positionsBySig[sig] = ps; sample[sig] = f.GetComponent<BaseFoundation>(); }
            groups[sig].Add(spec);
        }

        BuildingCatalog catalog = AssetDatabase.LoadAssetAtPath<BuildingCatalog>(ModularFoundationBuilder.CatalogPath);
        Vector2 footprint = ModularFoundationBuilder.SlotFootprints(catalog, null)[BuildingType.Flak];
        List<FlakDefensiveLayout> layouts = new List<FlakDefensiveLayout>();
        foreach (var kv in groups)
        {
            var specs = kv.Value;
            string name = specs.Any(s => s.family == BaseLayoutFamily.Air) ? "Air"
                : specs.Any(s => s.baseType == "Tank") && specs.Count == 1 ? "Tank" : "Arena";
            string path = $"{LayoutDir}/FlakLayout_{name}.asset";
            FlakDefensiveLayout layout = AssetDatabase.LoadAssetAtPath<FlakDefensiveLayout>(path);
            if (layout == null)
            {
                layout = ScriptableObject.CreateInstance<FlakDefensiveLayout>();
                AssetDatabase.CreateAsset(layout, path);
            }

            layout.EditorInitialize(name, specs.Select(s => s.baseType).ToArray(), sample[kv.Key].FlakLayout,
                ModularFoundationBuilder.FlakScale, footprint, positionsBySig[kv.Key]);
            EditorUtility.SetDirty(layout);
            bool valid = layout.Validate(out string error);
            log.AppendLine($"  {path}: {layout.Count} positions for [{string.Join(", ", specs.Select(s => s.baseType))}] valid={valid} {error}");
            layouts.Add(layout);
        }

        FlakLayoutCatalog cat = AssetDatabase.LoadAssetAtPath<FlakLayoutCatalog>(CatalogPath);
        if (cat == null)
        {
            cat = ScriptableObject.CreateInstance<FlakLayoutCatalog>();
            AssetDatabase.CreateAsset(cat, CatalogPath);
        }

        cat.EditorSetLayouts(layouts.OrderBy(l => l.LayoutName, StringComparer.Ordinal).ToList());
        EditorUtility.SetDirty(cat);
        AssetDatabase.SaveAssets();
        log.AppendLine($"  catalog {CatalogPath}: {layouts.Count} layouts");
        return log.ToString();
    }
}
