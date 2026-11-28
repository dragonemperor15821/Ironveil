using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Stage 3A validation of the modular foundations, building prefabs and data.
/// Read-only with respect to assets: every test object is temporary and
/// destroyed. Writes a report, a Flak position table and screenshots to the
/// STAGE3A_OUT folder (or Logs/Stage3A).
/// </summary>
public static class ModularFoundationValidator
{
    private const float Eps = 1e-5f;
    private const float WallHeightThreshold = 0.03f;   // above the deck/apron (~0.013-0.024)
    private const float RuntimeBaseScale = 6.5f;       // MainBuild baseSpawnScale
    private const float HangarGapLocal = 0.5f;         // railBaseHangarClearance (x baseSpawnScale) in base units

    private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    private static StringBuilder report;
    private static int passCount, failCount, warnCount;

    [MenuItem("Tools/IRONVEIL/Modular Bases/Validate Foundations")]
    public static void ValidateFromMenu()
    {
        Run(false);
    }

    /// <summary>Batch entry point (-executeMethod ModularFoundationValidator.ValidateBatch).</summary>
    public static void ValidateBatch()
    {
        Run(true);
    }

    private static void Run(bool screenshots)
    {
        report = new StringBuilder();
        passCount = failCount = warnCount = 0;
        report.AppendLine("=== IRONVEIL Stage 3A validation " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", IC) + " ===");

        BuildingCatalog catalog = AssetDatabase.LoadAssetAtPath<BuildingCatalog>(ModularFoundationBuilder.CatalogPath);
        InitialBuildingConfig config = AssetDatabase.LoadAssetAtPath<InitialBuildingConfig>(ModularFoundationBuilder.InitialConfigPath);

        Section("Level 1 building prefabs");
        ValidateLevel1Prefabs();

        Section("Building definitions and catalog");
        ValidateCatalog(catalog);
        Dictionary<BuildingType, Vector2> footprints = ModularFoundationBuilder.SlotFootprints(catalog, null);

        Section("Initial building configuration (data)");
        ValidateConfigData(config);

        StringBuilder flakTable = new StringBuilder();
        flakTable.AppendLine("layout,baseTypes,slotId,side,row,index,x,y,z,yawDeg,radius,angleFromFrontDeg,worldX_at6.5,worldY_at6.5,worldZ_at6.5,groundOnTerrain");
        StringBuilder bounds = new StringBuilder();
        bounds.AppendLine("baseType | spawner footprint ORIGINAL (scale 1) | spawner footprint FOUNDATION (scale 1) | bit-identical @1 | bit-identical @6.5 | visible bounds original | visible bounds foundation (empty slots)");
        HashSet<BaseLayoutFamily> tabled = new HashSet<BaseLayoutFamily>();

        foreach (ModularFoundationBuilder.BaseSpec spec in ModularFoundationBuilder.Bases)
        {
            Section($"Foundation: {spec.baseType}");
            ValidateFoundation(spec, catalog, config, footprints, flakTable, bounds, tabled);
        }

        Section("Flak range data (must be unchanged)");
        float[] expMin = { 12f, 14f, 16f, 18f, 20f, 22f };
        float[] expMax = { 16f, 18f, 20f, 22f, 24f, 26f };
        for (int lv = 1; lv <= 6; lv++)
        {
            FlakRangeBand band = FlakRangeTable.ApprovedRangeSC((BaseLevel)lv);
            Check(band.Minimum == expMin[lv - 1] && band.Maximum == expMax[lv - 1],
                $"L{lv} SC range {band.Minimum}-{band.Maximum} ({band.Units})");
        }

        Check(Mathf.Approximately(FlakRangeTable.ProvisionalScToUnity, 1f), "S (SC->Unity) is still the provisional 1.0");

        report.AppendLine();
        report.AppendLine("=== Original vs foundation bounds ===");
        report.Append(bounds);
        report.AppendLine();
        report.AppendLine($"=== SUMMARY: {passCount} PASS, {failCount} FAIL, {warnCount} WARN ===");

        ModularFoundationBuilder.WriteOut("validation_report.txt", report.ToString());
        ModularFoundationBuilder.WriteOut("flak_positions.csv", flakTable.ToString());
        Debug.Log(report.ToString());

        if (screenshots)
        {
            Stage3AScreenshots.RenderAll(catalog, config);
        }
    }

    // ------------------------------------------------------------------

    private static void Section(string title)
    {
        report.AppendLine();
        report.AppendLine("## " + title);
    }

    private static bool Check(bool ok, string what)
    {
        report.AppendLine((ok ? "  PASS " : "  FAIL ") + what);
        if (ok) passCount++; else failCount++;
        return ok;
    }

    private static void Warn(string what)
    {
        report.AppendLine("  WARN " + what);
        warnCount++;
    }

    private static void Info(string what)
    {
        report.AppendLine("  info " + what);
    }

    private static string F(float v) => v.ToString("0.0000", IC);
    private static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

    // ------------------------------------------------------------------

    private static void ValidateLevel1Prefabs()
    {
        Quaternion expectedRot = Quaternion.Euler(-90f, 0f, 0f);
        foreach (ModularFoundationBuilder.BuildingSpec s in ModularFoundationBuilder.Buildings)
        {
            string path = ModularFoundationBuilder.Level1PrefabPath(s);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!Check(prefab != null, $"{s.type}: prefab exists at {path}")) continue;

            GameObject source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(prefab);
            string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : "(none)";
            Check(sourcePath == s.level1Fbx, $"{s.type}: model source is {sourcePath}");

            Check(prefab.transform.localScale == Vector3.one, $"{s.type}: root scale {V(prefab.transform.localScale)} (no baked scaling)");
            float angle = Quaternion.Angle(prefab.transform.localRotation, expectedRot);
            Check(angle < 0.01f, $"{s.type}: root rotation {V(prefab.transform.localEulerAngles)} matches the X 270 convention (diff {angle:F4} deg)");

            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            Check(renderers.Length >= 1, $"{s.type}: {renderers.Length} renderer(s)");
            string folder = Path.GetDirectoryName(s.level1Fbx).Replace('\\', '/');
            foreach (Renderer r in renderers)
            {
                foreach (Material m in r.sharedMaterials)
                {
                    string mp = m != null ? AssetDatabase.GetAssetPath(m) : "(null)";
                    bool inFolder = mp.StartsWith(folder + "/Materials/", StringComparison.Ordinal);
                    Check(m != null && inFolder, $"{s.type}: material {mp}");
                    if (m == null) continue;
                    Check(m.shader != null && m.shader.name == "Universal Render Pipeline/Lit", $"{s.type}: shader {m.shader?.name}");
                    foreach (string prop in new[] { "_BaseMap", "_BumpMap", "_MetallicGlossMap" })
                    {
                        Texture t = m.HasProperty(prop) ? m.GetTexture(prop) : null;
                        string tp = t != null ? AssetDatabase.GetAssetPath(t) : "(none)";
                        Check(t != null && tp.StartsWith(folder + "/", StringComparison.Ordinal), $"{s.type}: {prop} = {tp}");
                    }
                }
            }

            if (ModularFoundationGeometry.TryModelFootprint(prefab, 1f, out Vector2 size, out Vector2 offset, out float h, out float minY))
            {
                Check(Mathf.Abs(minY) < 0.001f, $"{s.type}: pivot at bottom (min y {minY:F5})");
                Check(Mathf.Abs(offset.x) < 0.002f && Mathf.Abs(offset.y) < 0.002f, $"{s.type}: pivot centred (offset {offset.x:F4}, {offset.y:F4}); native size {size.x:F3} x {size.y:F3} x {h:F3}");
            }
        }

        string[] refinery = AssetDatabase.FindAssets("Refinery t:Prefab", new[] { ModularFoundationBuilder.Level1Dir });
        Check(refinery.Length == 1, $"exactly one Factory/Refinery L1 prefab ({string.Join(", ", refinery.Select(AssetDatabase.GUIDToAssetPath))})");
    }

    private static void ValidateCatalog(BuildingCatalog catalog)
    {
        if (!Check(catalog != null, "catalog exists at " + ModularFoundationBuilder.CatalogPath)) return;
        Check(catalog.Definitions.Count == 8, $"catalog holds {catalog.Definitions.Count} definitions");
        HashSet<BuildingType> seen = new HashSet<BuildingType>();
        foreach (BuildingDefinition d in catalog.Definitions)
        {
            Check(d != null && seen.Add(d.Type), $"definition {d?.Type} unique");
            if (d == null) continue;
            bool flak = d.Type == BuildingType.Flak;
            Check(Mathf.Approximately(d.InstanceScale, flak ? 0.05f : 0.2f), $"{d.Type}: instance scale {d.InstanceScale}");
            Check(d.HasLevel(1), $"{d.Type}: L1 model registered ({AssetDatabase.GetAssetPath(d.GetPrefab(1))})");
            string lv = string.Join(", ", d.Levels.Select(l => $"L{l.level}={AssetDatabase.GetAssetPath(l.prefab)}"));
            Check(d.Levels.All(l => l.prefab != null), $"{d.Type}: levels {lv}");
            Info($"{d.Type}: max count {(d.HasMaxCount ? d.MaxCount.ToString(IC) : "undefined")}; {d.MissingLevelsNote}");
        }

        Check(catalog.Get(BuildingType.Flak) != null && catalog.Get(BuildingType.Flak).MaxCount == 28, "Flak max count 28");
        Check(Enum.GetValues(typeof(BuildingType)).Length == 8, "BuildingType has 8 values (FactoryRefinery is one type)");
    }

    private static void ValidateConfigData(InitialBuildingConfig config)
    {
        if (!Check(config != null, "initial config exists")) return;
        Check(config.CountOf(BuildingType.Flak) == 2, "Flak count 2");
        Check(config.CountOf(BuildingType.FactoryRefinery) == 1, "Factory/Refinery count 1");
        int others = 0;
        foreach (BuildingType t in Enum.GetValues(typeof(BuildingType)))
        {
            if (t != BuildingType.Flak && t != BuildingType.FactoryRefinery) others += config.CountOf(t);
        }

        Check(others == 0, "all other buildings 0");
        Check(config.Entries.All(e => e.buildingLevel == 1), "all starting buildings are building level 1");
        foreach (var e in config.Entries) Info($"{e.type} x{e.count} L{e.buildingLevel} -> {string.Join(", ", e.slotIds)}");
    }

    // ------------------------------------------------------------------

    private static void ValidateFoundation(ModularFoundationBuilder.BaseSpec spec, BuildingCatalog catalog,
        InitialBuildingConfig config, Dictionary<BuildingType, Vector2> footprints, StringBuilder flakTable,
        StringBuilder boundsTable, HashSet<BaseLayoutFamily> tabled)
    {
        string path = ModularFoundationBuilder.FoundationPath(spec.baseType);
        GameObject foundation = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        GameObject original = AssetDatabase.LoadAssetAtPath<GameObject>(spec.originalPath);
        if (!Check(foundation != null, "exists: " + path)) return;

        BaseFoundation bf = foundation.GetComponent<BaseFoundation>();
        BaseBuildingState state = foundation.GetComponent<BaseBuildingState>();
        Check(bf != null && bf.BaseType == spec.baseType && bf.LayoutFamily == spec.family && bf.SourcePrefab == original,
            $"identity: baseType '{bf?.BaseType}', family {bf?.LayoutFamily}, source {AssetDatabase.GetAssetPath(bf?.SourcePrefab)}");
        Check(state != null, "BaseBuildingState present");
        Check(PrefabUtility.GetPrefabAssetType(foundation) == PrefabAssetType.Regular, "standalone prefab (not a variant of the original)");
        Check(foundation.GetComponentsInChildren<BaseController>(true).Length == 0, "no BaseController baked in (spawner adds it)");
        Check(foundation.transform.Find("Naval Hangar") == null && foundation.transform.Find("Rail Hangar") == null,
            "no hangar inside (hangars stay with BaseSpawner's separate spawning)");

        // Structure / helipad / roads.
        string structure = spec.family == BaseLayoutFamily.Air ? "futuristic_circular_base_3d_model" : "circular_arena_3d_model";
        Transform st = foundation.transform.Find(structure);
        Transform stO = original.transform.Find(structure);
        Check(st != null && st.GetComponentInChildren<MeshRenderer>(true) != null && SameLocal(st, stO),
            $"structure '{structure}' present with original transform");
        Transform pad = foundation.transform.Find("Helipad");
        bool wantPad = spec.family == BaseLayoutFamily.GroundHeli;
        Check((pad != null) == wantPad, $"Helipad {(wantPad ? "present" : "absent")} as in the original");
        if (pad != null)
        {
            Transform padModel = pad.GetChild(0);
            Check(SameLocal(pad, original.transform.Find("Helipad")) && Mathf.Approximately(padModel.localScale.x, 0.1f),
                $"Helipad structural, unchanged transform, model scale {padModel.localScale.x}");
        }

        if (spec.baseType == "Tank")
        {
            foreach (string n in new[] { "Tank Base Roads", "Tank Base Wall Structure" })
            {
                Transform t = foundation.transform.Find(n);
                Check(t != null && !t.gameObject.activeSelf, $"{n} preserved (inactive, as in the original)");
            }
        }

        // Anchors.
        Transform anchors = foundation.transform.Find("Building Anchors");
        Transform anchorsO = original.transform.Find("Building Anchors");
        bool anchorsOk = anchors != null && SameLocal(anchors, anchorsO) && anchors.childCount == anchorsO.childCount;
        foreach (Transform a in anchorsO)
        {
            Transform b = anchors != null ? anchors.Find(a.name) : null;
            anchorsOk &= b != null && SameLocal(a, b);
        }

        Check(anchorsOk, "Building Anchors and all 7 anchors present with names and transforms identical to the original");
        int anchorRenderers = anchors != null ? anchors.GetComponentsInChildren<Renderer>(true).Length : -1;
        Check(anchorRenderers == 0, $"no building geometry left under Building Anchors ({anchorRenderers} renderers)");

        Transform flaksO = original.transform.Find("Base Flaks");
        Transform flaks = foundation.transform.Find("Base Flaks");
        Check((flaks != null) == (flaksO != null), $"Base Flaks group {(flaksO != null ? "kept" : "absent (as in the original)")}");
        if (flaks != null)
        {
            Check(SameLocal(flaks, flaksO) && flaks.childCount == 0,
                $"Base Flaks empty defensive-front reference at original local position {V(flaks.localPosition)}");
        }

        bool stray = false;
        foreach (Transform c in foundation.transform)
        {
            if (ModularFoundationBuilder.IsStrayRootTurret(c, foundation.transform)) stray = true;
        }

        Check(!stray, "redundant third root turret not included");

        // Footprint (spawner's own measurement), facing and formation eligibility.
        SpawnerFootprintProbe.Result o1 = SpawnerFootprintProbe.Measure(original, 1f);
        SpawnerFootprintProbe.Result f1 = SpawnerFootprintProbe.Measure(foundation, 1f);
        SpawnerFootprintProbe.Result o65 = SpawnerFootprintProbe.Measure(original, RuntimeBaseScale);
        SpawnerFootprintProbe.Result f65 = SpawnerFootprintProbe.Measure(foundation, RuntimeBaseScale);
        Check(o1.BitEquals(f1), $"spawner footprint bit-identical @1: {f1}");
        Check(o65.BitEquals(f65), $"spawner footprint bit-identical @6.5: {f65}");
        Check(o1.hasDefensiveFront == f1.hasDefensiveFront,
            $"formation eligibility unchanged (PrefabHasDefensiveFront {f1.hasDefensiveFront})");

        Bounds visO = VisibleBounds(original, out _);
        Bounds visF = VisibleBounds(foundation, out _);
        boundsTable.AppendLine($"{spec.baseType} | c{V(o1.boundsCenter)} s{V(o1.boundsSize)} | c{V(f1.boundsCenter)} s{V(f1.boundsSize)} | " +
                               $"{o1.BitEquals(f1)} | {o65.BitEquals(f65)} | c{V(visO.center)} s{V(visO.size)} | c{V(visF.center)} s{V(visF.size)}");

        // Slots.
        if (state == null) return;
        List<BuildingSlot> slots = state.Slots.ToList();
        BuildingSlot[] found = foundation.GetComponentsInChildren<BuildingSlot>(true);
        Check(slots.Count == 35 && found.Length == 35 && found.All(slots.Contains), $"35 slots (7 building + 28 Flak) registered ({slots.Count}/{found.Length})");
        Check(slots.Select(s => s.SlotId).Distinct().Count() == slots.Count, "slot IDs unique");
        Check(slots.All(s => !s.IsOccupied), "every slot starts empty in the foundation prefab");

        bool scaleChain = true;
        foreach (BuildingSlot s in slots)
        {
            for (Transform t = s.transform; t != foundation.transform; t = t.parent)
            {
                if (t.localScale != Vector3.one) { scaleChain = false; Warn($"non-unit scale on {t.name}"); }
            }
        }

        Check(scaleChain, "every transform between base root and each slot has unit scale (scale applied once, on the instance)");

        List<BuildingSlot> buildingSlots = slots.Where(s => !s.Accepts(BuildingType.Flak)).ToList();
        List<BuildingSlot> flakSlots = slots.Where(s => s.Accepts(BuildingType.Flak)).ToList();
        Check(buildingSlots.Count == 7 && buildingSlots.All(s => s.AcceptedTypes.Length == 1 && Mathf.Approximately(s.InstanceScale, 0.2f)),
            "7 building slots, one type each, instance scale 0.2");
        Check(flakSlots.Count == 28 && flakSlots.All(s => s.AcceptedTypes.Length == 1 && Mathf.Approximately(s.InstanceScale, 0.05f)),
            "28 Flak slots, instance scale 0.05");
        foreach (ModularFoundationBuilder.BuildingSpec b in ModularFoundationBuilder.Buildings)
        {
            if (b.anchorName == null) continue;
            BuildingSlot s = state.FindSlot(b.slotId);
            Check(s != null && s.Accepts(b.type) && s.transform.parent.name == b.anchorName,
                $"slot {b.slotId} under anchor '{b.anchorName}' accepts {b.type}");
        }

        ValidateGeometry(spec, foundation, bf, buildingSlots, flakSlots, footprints, flakTable, tabled, o1);
        ValidatePlacementAndState(spec, foundation, catalog, config);
    }

    private static void ValidateGeometry(ModularFoundationBuilder.BaseSpec spec, GameObject foundation, BaseFoundation bf,
        List<BuildingSlot> buildingSlots, List<BuildingSlot> flakSlots, Dictionary<BuildingType, Vector2> footprints,
        StringBuilder flakTable, HashSet<BaseLayoutFamily> tabled, SpawnerFootprintProbe.Result originalFootprint)
    {
        Transform root = foundation.transform;
        FlakArcLayout.Parameters p = bf.FlakLayout;
        Vector2 c = p.centre;

        ModularFoundationGeometry.Rect2 RectOf(BuildingSlot s)
        {
            Vector3 lp = root.InverseTransformPoint(s.transform.position);
            float yaw = (Quaternion.Inverse(root.rotation) * s.transform.rotation).eulerAngles.y;
            return new ModularFoundationGeometry.Rect2(new Vector2(lp.x, lp.z), s.ReservedFootprint, yaw);
        }

        Check(buildingSlots.All(s => s.ReservedFootprint == footprints[s.AcceptedTypes[0]]),
            "slot footprints = max of every registered model level of the accepted type");

        // --- Flak layout: counts, symmetry, spacing.
        Dictionary<string, BuildingSlot> byId = flakSlots.ToDictionary(s => s.SlotId);
        bool countsOk = true, symOk = true, pitchOk = true, radiusOk = true;
        float maxSymErr = 0f, maxPitchErr = 0f, maxRadErr = 0f;
        foreach (bool right in new[] { true, false })
        {
            for (int row = 1; row <= 2; row++)
            {
                float r = FlakArcLayout.RowRadius(p, row);
                for (int i = 1; i <= 7; i++)
                {
                    string id = FlakArcLayout.SlotId(right, row, i);
                    if (!byId.TryGetValue(id, out BuildingSlot s)) { countsOk = false; continue; }
                    Vector3 lp = root.InverseTransformPoint(s.transform.position);
                    float radErr = Mathf.Abs(Vector2.Distance(new Vector2(lp.x, lp.z), c) - r);
                    maxRadErr = Mathf.Max(maxRadErr, radErr);
                    radiusOk &= radErr < Eps;
                    if (i > 1 && byId.TryGetValue(FlakArcLayout.SlotId(right, row, i - 1), out BuildingSlot prev))
                    {
                        Vector3 pp = root.InverseTransformPoint(prev.transform.position);
                        float e = Mathf.Abs(Vector2.Distance(new Vector2(lp.x, lp.z), new Vector2(pp.x, pp.z)) - p.chordPitch);
                        maxPitchErr = Mathf.Max(maxPitchErr, e);
                        pitchOk &= e < Eps;
                    }

                    if (right && byId.TryGetValue(FlakArcLayout.SlotId(false, row, i), out BuildingSlot m))
                    {
                        Vector3 mp = root.InverseTransformPoint(m.transform.position);
                        float yawR = s.transform.localEulerAngles.y, yawL = m.transform.localEulerAngles.y;
                        float yawErr = Mathf.Abs(Mathf.DeltaAngle(yawR, -yawL));
                        float err = Mathf.Max(Mathf.Abs((lp.x - c.x) + (mp.x - c.x)), Mathf.Abs(lp.z - mp.z), Mathf.Abs(lp.y - mp.y), yawErr * 1e-3f);
                        maxSymErr = Mathf.Max(maxSymErr, err);
                        symOk &= err < Eps;
                    }
                }
            }
        }

        Check(countsOk && flakSlots.Count == 28, "28 Flak positions: Left/Right x Row 1/Row 2 x 7");
        Check(symOk, $"left/right exact mirror about the entrance axis x = {F(c.x)} (x, y, z, yaw; max err {maxSymErr:E2})");
        Check(pitchOk, $"consistent spacing in every row: chord {F(p.chordPitch)} (max err {maxPitchErr:E2})");
        Check(radiusOk, $"rows on radii {F(FlakArcLayout.RowRadius(p, 1))} / {F(FlakArcLayout.RowRadius(p, 2))}: separation {F(p.rowSeparation)} (max err {maxRadErr:E2})");

        // --- Flak overlaps, corridor, buildings, helipad, structure.
        List<ModularFoundationGeometry.Rect2> flakRects = flakSlots.Select(RectOf).ToList();
        float minFlakGap = float.PositiveInfinity;
        for (int i = 0; i < flakRects.Count; i++)
            for (int j = i + 1; j < flakRects.Count; j++)
                minFlakGap = Mathf.Min(minFlakGap, ModularFoundationGeometry.Separation(flakRects[i], flakRects[j]));
        Check(minFlakGap > 0f, $"seven Flaks fit in every row, no Flak footprints overlap (min gap {F(minFlakGap)} = {minFlakGap * RuntimeBaseScale:F3} world @6.5)");

        float corridorHalf = float.PositiveInfinity;
        foreach (var r in flakRects)
            foreach (Vector2 corner in r.Corners())
                corridorHalf = Mathf.Min(corridorHalf, Mathf.Abs(corner.x - c.x));
        Check(corridorHalf >= p.clearHalfWidth - Eps,
            $"central entrance / hangar approach corridor unobstructed: clear width {F(2f * corridorHalf)} base units = {2f * corridorHalf * RuntimeBaseScale:F3} world @6.5 (required {F(2f * p.clearHalfWidth)})");

        List<ModularFoundationGeometry.Rect2> buildingRects = buildingSlots.Select(RectOf).ToList();
        float minFB = float.PositiveInfinity;
        foreach (var fr in flakRects) foreach (var br in buildingRects) minFB = Mathf.Min(minFB, ModularFoundationGeometry.Separation(fr, br));
        Check(minFB > 0f, $"no Flak position collides with a building slot (min gap {F(minFB)})");

        float minBB = float.PositiveInfinity;
        string worstPair = string.Empty;
        for (int i = 0; i < buildingRects.Count; i++)
        {
            for (int j = i + 1; j < buildingRects.Count; j++)
            {
                float g = ModularFoundationGeometry.Separation(buildingRects[i], buildingRects[j]);
                if (g < minBB) { minBB = g; worstPair = buildingSlots[i].SlotId + "/" + buildingSlots[j].SlotId; }
            }
        }

        Check(minBB > 0f, $"no building slot collisions (min gap {F(minBB)} between {worstPair})");

        Transform pad = root.Find("Helipad");
        if (pad != null && ModularFoundationGeometry.TryCombinedBounds(pad.gameObject, true, out Bounds pb))
        {
            var padRect = new ModularFoundationGeometry.Rect2(new Vector2(pb.center.x, pb.center.z), new Vector2(pb.size.x, pb.size.z), 0f);
            float minPad = buildingRects.Concat(flakRects).Min(r => ModularFoundationGeometry.Separation(r, padRect));
            Check(minPad > 0f, $"Helipad clear of every slot (min gap {F(minPad)}; Resource Optimiser gap {F(ModularFoundationGeometry.Separation(RectOf(buildingSlots.First(s => s.SlotId == "ResourceOptimiser")), padRect))})");
        }

        // Structure clearance: sample the foundation surface under every footprint.
        float wallInner = spec.family == BaseLayoutFamily.Air ? 0.445f : 0.40f;
        using (var sampler = new ModularFoundationGeometry.SurfaceSampler(foundation, t => t.name == "Helipad"))
        {
            float flakMax = 0f;
            foreach (var r in flakRects)
            {
                sampler.SampleRect(r, 7, out float h, out _);
                flakMax = Mathf.Max(flakMax, h);
            }

            Check(flakMax <= WallHeightThreshold, $"Flak positions clear of the wall/towers: highest surface under any Flak {F(flakMax)} (deck/apron <= {WallHeightThreshold})");

            GameObject slotSource = AssetDatabase.LoadAssetAtPath<GameObject>(spec.slotSourcePath);
            foreach (BuildingSlot s in buildingSlots)
            {
                var r = RectOf(s);
                float maxCorner = r.Corners().Max(k => Vector2.Distance(k, c));
                sampler.SampleRect(r, 9, out float h, out float hit);
                string anchorName = s.transform.parent.name;
                bool hasR0 = ModularFoundationBuilder.TryOriginalCornerRadius(slotSource, anchorName, c, out float r0);
                string msg = $"{s.SlotId}: reserved footprint furthest corner r {F(maxCorner)}; original building there {(hasR0 ? F(r0) : "n/a")}; " +
                             $"wall inner face ~{F(wallInner)}; highest surface under footprint {F(h)}";
                bool centreSlot = Vector2.Distance(r.centre, c) < 0.05f;
                if (centreSlot) Info(msg + " (centre slot, far from the wall)");
                else if (hasR0 && maxCorner > r0 + 0.001f) Warn(msg + " -> reaches further out than the original building");
                else if (maxCorner > wallInner) Info(msg + " (inherited from the original layout)");
                else Info(msg);
                Check(hit >= 0.999f, $"{s.SlotId} inside the base disc");
            }
        }

        // Front protrusion into the hangar gap.
        float frontMax = flakRects.Max(r => r.Corners().Max(k => k.y));
        float originalFront = originalFootprint.boundsCenter.z + originalFootprint.boundsSize.z * 0.5f;
        float protrude = frontMax - originalFront;
        Check(protrude <= 0f,
            $"no Flak enters the gap before a Naval/Rail hangar: front-most Flak z {F(frontMax)} vs original footprint front {F(originalFront)} (margin {F(-protrude)} = {-protrude * RuntimeBaseScale:F3} world)");
        float sideMax = flakRects.Max(r => r.Corners().Max(k => Mathf.Abs(k.x - c.x)));
        float sideFootprint = originalFootprint.boundsSize.x * 0.5f;
        Info($"lateral extent of Flak rows |x - cx| {F(sideMax)} vs original footprint half-width {F(sideFootprint)} " +
             $"(overhang {F(Mathf.Max(0f, sideMax - sideFootprint))} = {Mathf.Max(0f, sideMax - sideFootprint) * RuntimeBaseScale:F3} world; checked against real neighbours by the scratch generation harness)");

        // Flak position table (once per layout).
        if (tabled.Add(spec.family))
        {
            string types = string.Join("/", ModularFoundationBuilder.Bases.Where(b => b.family == spec.family).Select(b => b.baseType));
            foreach (BuildingSlot s in flakSlots.OrderBy(s => s.SlotId, StringComparer.Ordinal))
            {
                Vector3 lp = root.InverseTransformPoint(s.transform.position);
                float yaw = Mathf.DeltaAngle(0f, s.transform.localEulerAngles.y);
                float rad = Vector2.Distance(new Vector2(lp.x, lp.z), c);
                float ang = Mathf.Abs(Mathf.Atan2(lp.x - c.x, lp.z - c.y) * Mathf.Rad2Deg);
                string id = s.SlotId;
                flakTable.AppendLine(string.Join(",", spec.family, types, id, id.Substring(5, 1), id.Substring(6, 1), id.Substring(8, 2),
                    lp.x.ToString("0.00000", IC), lp.y.ToString("0.00000", IC), lp.z.ToString("0.00000", IC),
                    yaw.ToString("0.000", IC), rad.ToString("0.00000", IC), ang.ToString("0.000", IC),
                    (lp.x * RuntimeBaseScale).ToString("0.0000", IC), (lp.y * RuntimeBaseScale).ToString("0.0000", IC),
                    (lp.z * RuntimeBaseScale).ToString("0.0000", IC), s.GroundOnTerrain));
            }
        }
    }

    private static void ValidatePlacementAndState(ModularFoundationBuilder.BaseSpec spec, GameObject foundation,
        BuildingCatalog catalog, InitialBuildingConfig config)
    {
        // A) Initial configuration on a fresh instance at runtime scale.
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(foundation);
        inst.hideFlags = HideFlags.HideAndDontSave;
        inst.transform.position = ModularFoundationGeometry.SamplingOrigin;
        inst.transform.localScale = Vector3.one * RuntimeBaseScale;
        try
        {
            BaseBuildingState state = inst.GetComponent<BaseBuildingState>();

            // Hover accommodation: bind like BaseMenuBootstrap does, before any building exists.
            BaseController controller = inst.AddComponent<BaseController>();
            controller.InitializeIdentity("STAGE3A_TEST_" + spec.baseType.Replace(' ', '_'), spec.baseType + " Test");
            BaseLevelInfo levelInfo = inst.AddComponent<BaseLevelInfo>();
            levelInfo.Initialize(new GeneratedBaseRecord { baseId = controller.BaseId, baseType = spec.baseType, level = BaseLevel.L5 });
            BaseInteractionTarget target = inst.AddComponent<BaseInteractionTarget>();
            target.Bind(controller, levelInfo);
            int renderersBefore = target.RendererCount;
            target.TryGetWorldBounds(out Bounds hoverBefore);

            bool applied = state.ApplyInitialConfiguration(config, catalog, out string error);
            Check(applied, "initial configuration applies" + (applied ? string.Empty : ": " + error));
            List<string> occupied = state.Slots.Where(s => s.IsOccupied).Select(s => s.SlotId).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Check(occupied.SequenceEqual(new[] { "FactoryRefinery", "Flak_L1_01", "Flak_R1_01" }),
                $"occupied slots exactly [{string.Join(", ", occupied)}]");
            Check(state.CountOf(BuildingType.Flak) == 2 && state.CountOf(BuildingType.FactoryRefinery) == 1 && state.OccupiedCount == 3,
                "2 x Flak + 1 x Factory/Refinery, 26 Flak and 6 building slots empty");
            Check(state.Slots.Where(s => s.IsOccupied).All(s => s.Occupant.BuildingLevel == 1) && levelInfo.Level == BaseLevel.L5,
                "starting buildings are building level 1 on a base-level-5 base (levels independent)");

            // Scale applied exactly once: relative to the base root.
            foreach (BuildingSlot s in state.Slots.Where(s => s.IsOccupied))
            {
                Transform t = s.Occupant.transform;
                float rel = t.lossyScale.x / inst.transform.lossyScale.x;
                Check(Mathf.Abs(rel - s.InstanceScale) < 1e-6f,
                    $"{s.SlotId}: instance scale relative to base root {rel:R} (expected {s.InstanceScale}), world {t.lossyScale.x:F4}");
                BuildingDefinition d = s.Occupant.Definition;
                Quaternion expected = Quaternion.Euler(0f, d.GetFacingYawOffset(1), 0f) * d.GetPrefab(1).transform.localRotation;
                Check(Quaternion.Angle(t.localRotation, expected) < 0.01f,
                    $"{s.SlotId}: model facing = slot facing (yaw {Mathf.DeltaAngle(0f, s.transform.localEulerAngles.y):F2}) + model offset {d.GetFacingYawOffset(1):F0}");
            }

            target.Bind(controller, levelInfo);
            target.TryGetWorldBounds(out Bounds hoverAfter);
            hoverAfter.Expand(1e-4f); // float tolerance: a new building may define the box edge itself
            bool contains = true;
            foreach (BuildingSlot s in state.Slots.Where(s => s.IsOccupied))
            {
                foreach (Renderer r in s.Occupant.GetComponentsInChildren<Renderer>())
                {
                    contains &= hoverAfter.Contains(r.bounds.min) && hoverAfter.Contains(r.bounds.max);
                }
            }

            Check(target.RendererCount == renderersBefore + 3 && contains,
                $"hover target picks up constructed buildings after re-Bind (renderers {renderersBefore} -> {target.RendererCount}); reserved-footprint marker excluded from hover");
            Info($"hover bounds before {V(hoverBefore.size)} after {V(hoverAfter.size)} (world, @6.5)");

            // B) Every building type at every registered level fits its slot with the right scale.
            BaseBuildingState probeState = state;
            int placed = 0, failed = 0;
            foreach (BuildingDefinition d in catalog.Definitions)
            {
                foreach (var lv in d.Levels)
                {
                    BuildingSlot slot = probeState.Slots.FirstOrDefault(s => s.Accepts(d.Type) && !s.IsOccupied);
                    if (slot == null) continue;
                    BuildingInstance bi = BuildingPlacement.Place(slot, d, lv.level);
                    if (bi == null) { failed++; continue; }
                    float rel = bi.transform.lossyScale.x / inst.transform.lossyScale.x;
                    if (Mathf.Abs(rel - d.InstanceScale) > 1e-6f) failed++;
                    placed++;
                    BuildingPlacement.Remove(slot);
                }
            }

            Check(failed == 0 && placed > 0, $"every registered model level placed and removed with correct relative scale ({placed} placements)");
        }
        finally
        {
            BaseRegistry.Unregister(inst.GetComponent<BaseInteractionTarget>());
            UnityEngine.Object.DestroyImmediate(inst);
        }
    }

    // ------------------------------------------------------------------

    private static bool SameLocal(Transform a, Transform b)
    {
        if (a == null || b == null) return false;
        return SpawnerFootprintProbe.BitwiseEqual(a.localPosition, b.localPosition) &&
               a.localRotation.Equals(b.localRotation) &&
               SpawnerFootprintProbe.BitwiseEqual(a.localScale, b.localScale);
    }

    /// <summary>Bounds of renderers that would actually draw (enabled, active).</summary>
    private static Bounds VisibleBounds(GameObject prefab, out int count)
    {
        GameObject go = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        go.hideFlags = HideFlags.HideAndDontSave;
        Bounds b = default(Bounds);
        count = 0;
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(false))
        {
            if (!r.enabled) continue;
            if (count++ == 0) b = r.bounds; else b.Encapsulate(r.bounds);
        }

        UnityEngine.Object.DestroyImmediate(go);
        return b;
    }
}
