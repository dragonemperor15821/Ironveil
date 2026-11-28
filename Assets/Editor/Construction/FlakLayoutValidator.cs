using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Validates the finalised 28-Flak defensive layout: the frozen layout assets,
/// their exact match with all nine foundations, planned-vs-constructed
/// behaviour (BaseFlakDefence), model dimensions and the absence of debug
/// guides. Every test object is temporary. Writes flak_layout_report.txt and
/// clean (guide-free) comparison screenshots.
/// </summary>
public static class FlakLayoutValidator
{
    private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    private static StringBuilder report;
    private static int pass, fail;

    [MenuItem("Tools/IRONVEIL/Modular Bases/Validate Flak Defensive Layout")]
    public static void ValidateFromMenu() => Run(false);

    public static void ValidateBatch() => Run(true);

    private static bool Check(bool ok, string what)
    {
        report.AppendLine((ok ? "  PASS " : "  FAIL ") + what);
        if (ok) pass++; else fail++;
        return ok;
    }

    private static void Info(string what) => report.AppendLine("  info " + what);
    private static string F(float v) => v.ToString("0.00000", IC);

    private static void Run(bool screenshots)
    {
        report = new StringBuilder("=== IRONVEIL finalised 28-Flak defensive layout validation ===\n");
        pass = fail = 0;
        FlakLayoutCatalog flakCatalog = AssetDatabase.LoadAssetAtPath<FlakLayoutCatalog>(FlakLayoutFreezer.CatalogPath);
        BuildingCatalog catalog = AssetDatabase.LoadAssetAtPath<BuildingCatalog>(ModularFoundationBuilder.CatalogPath);
        InitialBuildingConfig config = AssetDatabase.LoadAssetAtPath<InitialBuildingConfig>(ModularFoundationBuilder.InitialConfigPath);

        report.AppendLine("\n## Layout assets");
        if (!Check(flakCatalog != null, "catalog " + FlakLayoutFreezer.CatalogPath)) { Finish(); return; }
        List<string> covered = new List<string>();
        foreach (FlakDefensiveLayout l in flakCatalog.Layouts)
        {
            report.AppendLine($"\n### {AssetDatabase.GetAssetPath(l)} -> [{string.Join(", ", l.BaseTypes)}]");
            covered.AddRange(l.BaseTypes);
            ValidateLayout(l, config);
        }

        string[] all = ModularFoundationBuilder.Bases.Select(b => b.baseType).ToArray();
        Check(covered.OrderBy(s => s).SequenceEqual(all.OrderBy(s => s)), $"every one of the 9 base types maps to exactly one layout ({covered.Count})");

        report.AppendLine("\n## Model dimensions (Flak levels as placed, relative to a 6.5 base root)");
        ValidateModels(catalog);

        foreach (var spec in ModularFoundationBuilder.Bases)
        {
            report.AppendLine($"\n## {spec.baseType}");
            ValidateFoundation(spec, flakCatalog, catalog, config);
        }

        report.AppendLine("\n## Debug guides");
        ValidateNoDebugGuides();

        Finish();
        if (screenshots) Stage3AScreenshots.RenderFinalComparison(catalog, config, flakCatalog);
    }

    private static void Finish()
    {
        report.AppendLine($"\n=== SUMMARY: {pass} PASS, {fail} FAIL ===");
        ModularFoundationBuilder.WriteOut("flak_layout_report.txt", report.ToString());
        Debug.Log(report.ToString());
    }

    private static void ValidateLayout(FlakDefensiveLayout l, InitialBuildingConfig config)
    {
        Check(l.Validate(out string error), "structure: 28 positions, 14 per side, 7 per row, construction order 1..28 " + error);
        Check(l.Count == 28 && l.CountSide(true) == 14 && l.CountSide(false) == 14, $"count {l.Count}: right {l.CountSide(true)}, left {l.CountSide(false)}");
        Check(new[] { true, false }.All(r => l.CountRow(r, 1) == 7 && l.CountRow(r, 2) == 7), "seven positions in each of the four rows");
        Check(Mathf.Approximately(l.InstanceScale, 0.05f), $"instance scale {l.InstanceScale}");

        var order = l.InConstructionOrder();
        string[] starting = config.Entries.First(e => e.type == BuildingType.Flak).slotIds;
        Check(order[0].slotId == starting[0] && order[1].slotId == starting[1],
            $"construction order starts with the two starting Flaks {order[0].slotId}, {order[1].slotId}");

        FlakArcLayout.Parameters p = l.SourceParameters;
        Vector2 c = p.centre;
        float maxRadial = 0f, maxMirror = 0f, maxPitch = 0f, minCorridor = float.PositiveInfinity, maxFront = float.NegativeInfinity;
        foreach (var a in l.Positions)
        {
            float radial = Mathf.Atan2(a.localPosition.x - c.x, a.localPosition.z - c.y) * Mathf.Rad2Deg;
            maxRadial = Mathf.Max(maxRadial, Mathf.Abs(Mathf.DeltaAngle(radial, a.yawDegrees)));
            if (a.right && l.TryGet(FlakArcLayout.SlotId(false, a.row, a.index), out var m))
            {
                maxMirror = Mathf.Max(maxMirror, Mathf.Abs((a.localPosition.x - c.x) + (m.localPosition.x - c.x)),
                    Mathf.Abs(a.localPosition.z - m.localPosition.z), Mathf.Abs(a.localPosition.y - m.localPosition.y),
                    Mathf.Abs(Mathf.DeltaAngle(a.yawDegrees, -m.yawDegrees)) * 1e-3f);
            }

            if (a.index > 1 && l.TryGet(FlakArcLayout.SlotId(a.right, a.row, a.index - 1), out var prev))
            {
                float d = Vector2.Distance(new Vector2(a.localPosition.x, a.localPosition.z), new Vector2(prev.localPosition.x, prev.localPosition.z));
                maxPitch = Mathf.Max(maxPitch, Mathf.Abs(d - p.chordPitch));
            }

            var rect = new ModularFoundationGeometry.Rect2(new Vector2(a.localPosition.x, a.localPosition.z), l.FlakFootprint, a.yawDegrees);
            foreach (Vector2 k in rect.Corners())
            {
                minCorridor = Mathf.Min(minCorridor, Mathf.Abs(k.x - c.x));
                maxFront = Mathf.Max(maxFront, k.y);
            }
        }

        Check(maxRadial < 1e-3f, $"orientation: every Flak faces radially outward from the base centre (max deviation {maxRadial:E2} deg)");
        Check(maxMirror < 1e-5f, $"left = exact mirror of right about x = {F(c.x)} (max error {maxMirror:E2})");
        Check(maxPitch < 1e-5f, $"consistent spacing {F(p.chordPitch)} between neighbours in every row (max error {maxPitch:E2})");
        Check(minCorridor >= p.clearHalfWidth - 1e-5f, $"entrance clearance: corridor half-width {F(minCorridor)} >= {F(p.clearHalfWidth)} (clear width {F(2 * minCorridor)} = {2 * minCorridor * 6.5f:F3} world)");
        Check(maxFront <= p.frontLimitZ + 1e-5f, $"no Flak beyond the hangar-gap limit z {F(p.frontLimitZ)} (front-most {F(maxFront)})");
        Info($"rows: radius {F(FlakArcLayout.RowRadius(p, 1))} / {F(FlakArcLayout.RowRadius(p, 2))}, separation {F(p.rowSeparation)}, centre ({F(c.x)}, {F(c.y)})");
    }

    private static void ValidateModels(BuildingCatalog catalog)
    {
        BuildingDefinition flak = catalog.Get(BuildingType.Flak);
        Vector3? l1 = null;
        foreach (var lv in flak.Levels)
        {
            GameObject src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(lv.prefab) ?? lv.prefab;
            string fbx = AssetDatabase.GetAssetPath(src);
            ModelImporter imp = AssetImporter.GetAtPath(fbx) as ModelImporter;
            Check(imp != null && Mathf.Approximately(imp.globalScale, 1f) && imp.useFileScale, $"L{lv.level} FBX import unchanged: {fbx} globalScale {imp?.globalScale} useFileScale {imp?.useFileScale}");
            Check(lv.prefab.transform.localScale == Vector3.one, $"L{lv.level} model root scale {lv.prefab.transform.localScale} (no baked scaling)");
            ModularFoundationGeometry.TryModelFootprint(lv.prefab, flak.InstanceScale, out Vector2 size, out _, out float h, out float minY, lv.facingYawOffset);
            Info($"L{lv.level}: footprint {F(size.x)} x {F(size.y)}, height {F(h)} base units = {size.x * 6.5f:F3} x {size.y * 6.5f:F3} x {h * 6.5f:F3} world; facing offset {lv.facingYawOffset} deg; pivot min y {minY:E1}");
            if (lv.level == 1) l1 = new Vector3(size.x, size.y, h);
            else if (l1.HasValue) Info($"L{lv.level}/L1 height ratio {h / l1.Value.z:F3} (native model heights 0.785/0.611 = {0.785f / 0.611f:F3})");
        }
    }

    private static void ValidateFoundation(ModularFoundationBuilder.BaseSpec spec, FlakLayoutCatalog flakCatalog,
        BuildingCatalog catalog, InitialBuildingConfig config)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModularFoundationBuilder.FoundationPath(spec.baseType));
        FlakDefensiveLayout layout = flakCatalog.ForBaseType(spec.baseType);
        Check(layout != null, $"layout '{layout?.LayoutName}'");
        if (layout == null) return;

        // Exact match between the foundation's Flak slots and the approved layout.
        var slots = prefab.GetComponent<BaseBuildingState>().Slots.Where(s => s.Accepts(BuildingType.Flak)).ToList();
        int exact = 0;
        foreach (BuildingSlot s in slots)
        {
            if (!layout.TryGet(s.SlotId, out var p)) continue;
            Quaternion q = s.transform.localRotation, a = p.localRotation;
            bool rotExact = q.x.Equals(a.x) && q.y.Equals(a.y) && q.z.Equals(a.z) && q.w.Equals(a.w);
            if (SpawnerFootprintProbe.BitwiseEqual(s.transform.localPosition, p.localPosition) && rotExact &&
                Mathf.Abs(Mathf.DeltaAngle(s.transform.localEulerAngles.y, p.yawDegrees)) < 1e-3f) exact++;
        }

        Check(slots.Count == 28 && exact == 28, $"foundation Flak slots match the approved layout bit-exactly (position + rotation) ({exact}/28)");
        Check(slots.All(s => Mathf.Approximately(s.InstanceScale, 0.05f)), "every Flak slot instance scale 0.05");

        // Planned vs constructed lifecycle on a temporary instance at runtime scale.
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.hideFlags = HideFlags.HideAndDontSave;
        inst.transform.position = ModularFoundationGeometry.SamplingOrigin;
        inst.transform.localScale = Vector3.one * 6.5f;
        try
        {
            Transform flakRoot = inst.transform.Find("Flak Slots");
            int Built() => flakRoot.GetComponentsInChildren<BuildingInstance>(true).Length;
            Check(Built() == 0 && flakRoot.GetComponentsInChildren<Renderer>(true).Length == 0, "fresh foundation: 0 Flaks constructed, no Flak geometry");
            BaseFlakDefence defence = inst.AddComponent<BaseFlakDefence>();
            Check(Built() == 0, "adding BaseFlakDefence constructs nothing");
            Check(defence.Bind(layout, out string err), "BaseFlakDefence binds to the approved layout " + err);
            Check(defence.PlannedCount == 28 && defence.ConstructedCount == 0, $"planned {defence.PlannedCount}, constructed {defence.ConstructedCount}");

            BaseBuildingState state = inst.GetComponent<BaseBuildingState>();
            Check(state.ApplyInitialConfiguration(config, catalog, out err), "initial configuration applies " + err);
            var occupied = state.Slots.Where(s => s.IsOccupied).Select(s => s.SlotId).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Check(defence.ConstructedCount == 2 && defence.PlannedCount == 28 && occupied.SequenceEqual(new[] { "FactoryRefinery", "Flak_L1_01", "Flak_R1_01" }),
                $"new base: 2 of 28 Flaks constructed + 1 L1 Factory/Refinery, nothing else ({string.Join(", ", occupied)})");

            BuildingDefinition flak = catalog.Get(BuildingType.Flak);
            Check(defence.NextFreeSlot()?.SlotId == "Flak_R1_02", $"next planned position {defence.NextFreeSlot()?.SlotId}");
            Check(defence.Construct(flak, 1, "Flak_R1_01") == null, "cannot construct into an occupied position");
            int built = 2;
            while (defence.Construct(flak, 1) != null) built++;
            Check(built == 28 && defence.ConstructedCount == 28 && defence.FreeCount == 0, $"capacity: exactly 28 constructible ({built})");
            Check(defence.Construct(flak, 1) == null, "no 29th Flak");
            Check(defence.Demolish("Flak_L2_07") && defence.ConstructedCount == 27 && defence.NextFreeSlot()?.SlotId == "Flak_L2_07",
                "demolish frees the planned position for rebuilding");

            bool scaleOk = state.Slots.Where(s => s.IsOccupied && s.Accepts(BuildingType.Flak))
                .All(s => Mathf.Abs(s.Occupant.transform.lossyScale.x / inst.transform.lossyScale.x - 0.05f) < 1e-6f);
            Check(scaleOk, "every constructed Flak is exactly 0.05 relative to the base root");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(inst);
        }
    }

    private static void ValidateNoDebugGuides()
    {
        Type[] debugTypes = { typeof(LineRenderer), typeof(TrailRenderer), typeof(TextMesh) };
        int bad = 0;
        List<string> paths = ModularFoundationBuilder.Bases.Select(b => ModularFoundationBuilder.FoundationPath(b.baseType)).ToList();
        paths.AddRange(ModularFoundationBuilder.Buildings.Select(ModularFoundationBuilder.Level1PrefabPath));
        foreach (string path in paths)
        {
            GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            foreach (Type t in debugTypes) bad += go.GetComponentsInChildren(t, true).Length;
            Transform marker = go.transform.Find("Reserved Footprint");
            if (marker != null && (marker.gameObject.activeSelf || marker.GetComponent<Renderer>().enabled)) bad++;
        }

        Check(bad == 0, $"no LineRenderer/TrailRenderer/TextMesh guide objects in the 9 foundations or 8 L1 prefabs; reserved-footprint markers inactive and unrendered ({paths.Count} prefabs)");

        int sourceHits = 0;
        foreach (string file in Directory.GetFiles("Assets/Scripts/Construction", "*.cs"))
        {
            string text = File.ReadAllText(file);
            if (text.Contains("LineRenderer") || text.Contains("Debug.DrawLine") || text.Contains("OnGUI")) sourceHits++;
            int giz = text.IndexOf("OnDrawGizmos", StringComparison.Ordinal);
            if (giz >= 0 && text.LastIndexOf("#if UNITY_EDITOR", giz, StringComparison.Ordinal) < 0) sourceHits++;
        }

        Check(sourceHits == 0, "runtime construction scripts draw no debug lines/GUI; slot gizmos are inside #if UNITY_EDITOR (stripped from builds)");
        Check(!EditorPrefs.GetBool(BuildingSlot.ShowGizmosPrefKey, false), "slot gizmo debug toggle is OFF (Tools/IRONVEIL/Modular Bases/Show Slot Gizmos)");
        Info("the yellow/cyan/magenta preview guides are temporary LineRenderers created and destroyed by the editor screenshot tool only (Assets/Editor); game cameras never draw gizmos");
    }
}
