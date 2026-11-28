using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Stage 3A: builds the modular-base assets from the ORIGINAL nine base
/// prefabs and the imported building models, without modifying any of them:
///   - five missing Level 1 building prefabs (variants of their FBX, unscaled),
///   - one BuildingDefinition per type, the BuildingCatalog and the
///     InitialBuildingConfig (2 x L1 Flak + 1 x L1 Factory/Refinery),
///   - nine standalone foundation prefabs in Assets/Prefabs/BaseFoundations.
///
/// Idempotent: re-running overwrites only the assets this builder generates.
/// The production BaseSpawner and scenes are not touched; foundations are not
/// used by production spawning.
/// </summary>
public static class ModularFoundationBuilder
{
    public const string FoundationDir = "Assets/Prefabs/BaseFoundations";
    public const string FootprintDir = FoundationDir + "/Reserved Footprints";
    public const string Level1Dir = "Assets/Prefabs/Buildings/Level 1";
    public const string DataDir = "Assets/Data/Construction";
    public const string DefinitionDir = DataDir + "/Definitions";
    public const string CatalogPath = DataDir + "/BuildingCatalog.asset";
    public const string InitialConfigPath = DataDir + "/InitialBuildingConfig.asset";

    // Existing project values (verified against the original prefab hierarchy
    // before use; see VerifyExistingScales).
    public const float BuildingScale = 0.2f;
    public const float FlakScale = 0.05f;
    public const float HelipadScale = 0.1f;

    // Flak arc layout (base-root units; the base root keeps its 6.5 runtime scale).
    public const float FlakChordPitch = 0.06f;      // Flak footprint 0.050 wide -> 0.010 gap
    public const float FlakRowSeparation = 0.065f;  // Flak footprint <=0.049 deep -> >=0.016 gap
    public const float FlakCorridorOffset = 0.10f;  // first Flak of each row: |x - cx| (original Flak 1/2 ~ +-0.10)
    // Inner-row radius: every Flak stands fully on terrain, outside the foundation mesh.
    // Ground arena: the mesh (wall band r 0.40-0.44, low apron, jagged rim) ends at r 0.50.
    // Stage 3A.1: 0.48 straddled the apron edge, which sits ~0.14 world above the
    // surrounding terrain, so the outer half of every inner-row Flak floated (median
    // 0.17 world on the generated map). Now 0.50 + 0.025 (radial half extent of a
    // radially-facing Flak) + 0.005 = 0.53.
    // Air structure: wall band r 0.445-0.485 plus outer towers reaching the structure
    // bounds radius 0.50 (bounds 1.000 wide) -> 0.50 + 0.035 (half diagonal) + 0.005 = 0.54.
    public const float GroundFlakInnerRadius = 0.53f;
    public const float AirFlakInnerRadius = 0.54f;

    // Stage 3A.1 front clear zone. Measured on the generated MainBuild world (seed as in the
    // scene, 350 bases): furthest hangar vertex from the base front axis = 0.1334 base units
    // (Rail / Air Rail hangars; Naval 0.0826). 0.145 = 0.1334 + 0.0116 margin.
    public const float FlakClearHalfWidth = 0.145f;
    // Margin kept inside the original prefab footprint front (no Flak enters the hangar gap).
    public const float FlakFrontLimitMargin = 0.002f;

    public const float BuildingSlotClearance = 0.005f;
    // Smallest Helipad clearance accepted by the 2D slot search (the Heli Resource Optimiser
    // sits between the Helipad and the wall; see ApplyClearanceSearch).
    public const float HelipadMinClearance = 0.0025f;

    public struct BuildingSpec
    {
        public BuildingType type;
        public string anchorName;      // existing anchor under "Building Anchors" (null for Flak)
        public string slotId;
        public string displayName;
        public string level1PrefabName;
        public string level1Fbx;
        public int[] higherLevels;
        public string[] higherModels;
        public float level1FacingYawOffset;  // turns the L1 model's forward onto slot +Z
        public string note;
    }

    public struct BaseSpec
    {
        public string baseType;
        public string originalPath;
        public BaseLayoutFamily family;
        public string slotSourcePath;  // prefab whose building instances give the slot positions
    }

    private const string Tripo = "Assets/TripoModels/";

    public static readonly BuildingSpec[] Buildings =
    {
        new BuildingSpec { type = BuildingType.BaseCommand, anchorName = "Base Command", slotId = "BaseCommand",
            displayName = "Base Command", level1PrefabName = "Level 1 Base Command",
            level1Fbx = Tripo + "Level 1 Base Command/futuristic_bunker_3d_model.fbx",
            higherLevels = new[] { 5 }, higherModels = new[] { Tripo + "Level 5 Base Command/futuristic_tower_3d_model.fbx" } },
        new BuildingSpec { type = BuildingType.MilitaryCentral, anchorName = "Military Central", slotId = "MilitaryCentral",
            displayName = "Military Central", level1PrefabName = "Level 1 Military Central",
            level1Fbx = Tripo + "Level 1 Military Central/sci-fi_outpost_3d_model.fbx",
            higherLevels = new[] { 10 }, higherModels = new[] { Tripo + "Level 10 Military Central/sci-fi_fortress_3d_model.fbx" } },
        new BuildingSpec { type = BuildingType.PowerPlant, anchorName = "Power Plant", slotId = "PowerPlant",
            displayName = "Power Plant", level1PrefabName = "Level 1 Power Plant",
            level1Fbx = Tripo + "Level 1 Power Plant/industrial_tank_3d_model.fbx",
            higherLevels = new[] { 10 }, higherModels = new[] { Tripo + "Level 10 Power Plant/industrial_refinery_3d_model.fbx" } },
        new BuildingSpec { type = BuildingType.FactoryRefinery, anchorName = "Refinery", slotId = "FactoryRefinery",
            displayName = "Factory / Refinery", level1PrefabName = "Level 1 Factory Refinery",
            level1Fbx = Tripo + "Level 1 Factory/industrial_building_3d_model.fbx",
            higherLevels = new[] { 5 }, higherModels = new[] { Tripo + "Level 5 Refinery/industrial_factory_3d_model.fbx" },
            note = "Factory and Refinery are one building type. The L1 model folder is 'Level 1 Factory', the L5 folder 'Level 5 Refinery'." },
        new BuildingSpec { type = BuildingType.Flak, anchorName = null, slotId = null,
            displayName = "Flak", level1PrefabName = "Level 1 Flak",
            level1Fbx = Tripo + "Level 1 Base Flak/futuristic_dual-barrel_turret_3d_model.fbx",
            higherLevels = new[] { 6 }, higherModels = new[] { Tripo + "L6 Base Turret/mecha_tank_3d_model.fbx" },
            // L1 barrels run along the model's local +X; the original L6 turret's run along +Z
            // (the existing convention: barrels on the slot/front axis). -90 maps +X onto +Z.
            level1FacingYawOffset = -90f,
            note = "Level 6 = 'L6 Base Turret', the turret used by the original base prefabs. Registration as Flak building level 6 NEEDS CONFIRMATION. " +
                   "L1 model facing offset -90 deg (barrels along local +X); L6 offset 0 (barrels along +Z, original convention)." },
        new BuildingSpec { type = BuildingType.ResourceOptimiser, anchorName = "Resource Optimiser", slotId = "ResourceOptimiser",
            displayName = "Resource Optimiser", level1PrefabName = "Level 1 Resource Optimiser",
            level1Fbx = Tripo + "Level 1 Resource Optimiser/tripo_convert_6ea87cd9-3dd9-4424-b433-8ef74ce33aaf.fbx",
            higherLevels = new[] { 3 }, higherModels = new[] { Tripo + "Level 3 Resource Optimiser/industrial_base_3d_model.fbx" } },
        new BuildingSpec { type = BuildingType.GroupExtension, anchorName = "Group Extension", slotId = "GroupExtension",
            displayName = "Group Extension", level1PrefabName = "Level 1 Group Extension",
            level1Fbx = Tripo + "Level 1 Group Extension/tripo_convert_a9f62b5d-b2a2-4d17-aaf8-70c43aba6820.fbx",
            higherLevels = new[] { 3 }, higherModels = new[] { Tripo + "Level 3 Group Extension/sci-fi_fortress_3d_model.fbx" } },
        new BuildingSpec { type = BuildingType.MilitaryOffice, anchorName = "Military Office", slotId = "MilitaryOffice",
            displayName = "Military Office", level1PrefabName = "Level 1 Military Office",
            level1Fbx = Tripo + "Level 1 Military Office/tripo_convert_03035b5b-b7d5-43cb-a494-75853020bd11.fbx",
            higherLevels = new[] { 3 }, higherModels = new[] { Tripo + "Level 3 Military Office/futuristic_fortress_3d_model.fbx" } },
    };

    public static readonly BaseSpec[] Bases =
    {
        // Tank has no usable layout of its own: proposed slots come from the
        // identical arena layout of the Naval Base (Tank Base Master scene instance ignored).
        new BaseSpec { baseType = "Tank", originalPath = "Assets/Prefabs/Tank Base.prefab", family = BaseLayoutFamily.Ground, slotSourcePath = "Assets/Prefabs/Naval Base.prefab" },
        new BaseSpec { baseType = "Heli", originalPath = "Assets/Prefabs/Heli Base.prefab", family = BaseLayoutFamily.GroundHeli, slotSourcePath = "Assets/Prefabs/Heli Base.prefab" },
        new BaseSpec { baseType = "Air", originalPath = "Assets/Prefabs/Air Base.prefab", family = BaseLayoutFamily.Air, slotSourcePath = "Assets/Prefabs/Air Base.prefab" },
        new BaseSpec { baseType = "Naval", originalPath = "Assets/Prefabs/Naval Base.prefab", family = BaseLayoutFamily.Ground, slotSourcePath = "Assets/Prefabs/Naval Base.prefab" },
        new BaseSpec { baseType = "Rail", originalPath = "Assets/Prefabs/Rail Base.prefab", family = BaseLayoutFamily.Ground, slotSourcePath = "Assets/Prefabs/Rail Base.prefab" },
        new BaseSpec { baseType = "Heli Naval", originalPath = "Assets/Prefabs/Heli Naval Base.prefab", family = BaseLayoutFamily.GroundHeli, slotSourcePath = "Assets/Prefabs/Heli Naval Base.prefab" },
        new BaseSpec { baseType = "Heli Rail", originalPath = "Assets/Prefabs/Heli Rail Base.prefab", family = BaseLayoutFamily.GroundHeli, slotSourcePath = "Assets/Prefabs/Heli Rail Base.prefab" },
        new BaseSpec { baseType = "Air Naval", originalPath = "Assets/Prefabs/Air Naval Base.prefab", family = BaseLayoutFamily.Air, slotSourcePath = "Assets/Prefabs/Air Naval Base.prefab" },
        new BaseSpec { baseType = "Air Rail", originalPath = "Assets/Prefabs/Air Rail Base.prefab", family = BaseLayoutFamily.Air, slotSourcePath = "Assets/Prefabs/Air Rail Base.prefab" },
    };

    public static string FoundationPath(string baseType) => $"{FoundationDir}/{baseType} Foundation.prefab";
    public static string Level1PrefabPath(BuildingSpec s) => $"{Level1Dir}/{s.level1PrefabName}.prefab";

    public static BuildingSpec Spec(BuildingType type)
    {
        foreach (BuildingSpec s in Buildings) if (s.type == type) return s;
        throw new ArgumentException("no spec for " + type);
    }

    public static bool IsStrayRootTurret(Transform t, Transform root) =>
        t.parent == root && t.name.StartsWith("mecha_tank_3d_model", StringComparison.Ordinal);

    // ------------------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Modular Bases/Build Foundations and Building Prefabs")]
    public static void BuildFromMenu()
    {
        string report = BuildAll();
        Debug.Log(report);
    }

    /// <summary>Batch entry point (-executeMethod ModularFoundationBuilder.BuildBatch).</summary>
    public static void BuildBatch()
    {
        string report = BuildAll();
        WriteOut("build_report.txt", report);
        Debug.Log(report);
    }

    public static void WriteOut(string fileName, string text)
    {
        string dir = Environment.GetEnvironmentVariable("STAGE3A_OUT");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "Stage3A");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), text);
    }

    public static string BuildAll()
    {
        StringBuilder log = new StringBuilder();
        log.AppendLine("=== IRONVEIL Stage 3A builder ===");
        EnsureFolder(FoundationDir);
        EnsureFolder(FootprintDir);
        EnsureFolder(Level1Dir);
        EnsureFolder(DataDir);
        EnsureFolder(DefinitionDir);

        VerifyExistingScales(log);

        Dictionary<BuildingType, GameObject> level1 = EnsureLevel1Prefabs(log);
        BuildingCatalog catalog = EnsureDefinitions(level1, log);
        EnsureInitialConfig(log);

        Dictionary<BuildingType, Vector2> footprints = SlotFootprints(catalog, log);

        foreach (BaseSpec spec in Bases)
        {
            BuildFoundation(spec, footprints, log);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        log.AppendLine("=== builder finished ===");
        return log.ToString();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    /// <summary>Reads the scales actually used in the original prefabs and logs them; never changes them.</summary>
    private static void VerifyExistingScales(StringBuilder log)
    {
        GameObject heli = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Heli Base.prefab");
        Transform anchors = heli.transform.Find("Building Anchors");
        foreach (Transform anchor in anchors)
        {
            Transform model = anchor.childCount > 0 ? anchor.GetChild(0) : null;
            log.AppendLine($"  verify scale: Heli/{anchor.name} model {(model ? model.localScale.x.ToString("R") : "none")}");
        }

        foreach (string flak in new[] { "Base Flaks/Flak 1", "Base Flaks/Flak 2" })
        {
            Transform f = heli.transform.Find(flak);
            log.AppendLine($"  verify scale: Heli/{flak} model {f.GetChild(0).localScale.x:R}");
        }

        Transform pad = heli.transform.Find("Helipad");
        log.AppendLine($"  verify scale: Heli/Helipad model {pad.GetChild(0).localScale.x:R}");
    }

    private static Dictionary<BuildingType, GameObject> EnsureLevel1Prefabs(StringBuilder log)
    {
        Dictionary<BuildingType, GameObject> result = new Dictionary<BuildingType, GameObject>();
        foreach (BuildingSpec s in Buildings)
        {
            string path = Level1PrefabPath(s);
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
            {
                result[s.type] = existing;
                log.AppendLine($"  L1 prefab reused: {path}");
                continue;
            }

            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(s.level1Fbx);
            if (fbx == null)
            {
                log.AppendLine($"  ERROR missing FBX {s.level1Fbx}");
                continue;
            }

            // Same recipe as the three existing L1 prefabs: a Prefab Variant of
            // the FBX model with its imported root rotation and unit scale.
            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            inst.name = s.level1PrefabName;
            inst.transform.localPosition = Vector3.zero;
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(inst, path, out bool ok);
            UnityEngine.Object.DestroyImmediate(inst);
            log.AppendLine($"  L1 prefab created: {path} ok={ok} rot={saved.transform.localEulerAngles} scale={saved.transform.localScale}");
            result[s.type] = saved;
        }

        return result;
    }

    private static BuildingCatalog EnsureDefinitions(Dictionary<BuildingType, GameObject> level1, StringBuilder log)
    {
        List<BuildingDefinition> defs = new List<BuildingDefinition>();
        foreach (BuildingSpec s in Buildings)
        {
            List<BuildingDefinition.LevelModel> levels = new List<BuildingDefinition.LevelModel>();
            if (level1.TryGetValue(s.type, out GameObject l1) && l1 != null)
            {
                levels.Add(new BuildingDefinition.LevelModel { level = 1, prefab = l1, facingYawOffset = s.level1FacingYawOffset });
            }

            for (int i = 0; i < s.higherLevels.Length; i++)
            {
                GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(s.higherModels[i]);
                if (model != null)
                {
                    levels.Add(new BuildingDefinition.LevelModel { level = s.higherLevels[i], prefab = model });
                }
                else
                {
                    log.AppendLine($"  WARNING model missing for {s.type} L{s.higherLevels[i]}: {s.higherModels[i]}");
                }
            }

            List<int> present = new List<int>();
            foreach (var m in levels) present.Add(m.level);
            present.Sort();
            int top = present[present.Count - 1];
            List<int> gaps = new List<int>();
            for (int lv = 1; lv < top; lv++) if (!present.Contains(lv)) gaps.Add(lv);
            string note = $"Models present for building levels {string.Join(", ", present)}. " +
                          (gaps.Count > 0 ? $"No models for levels {string.Join(", ", gaps)}. " : string.Empty) +
                          $"Maximum building level NOT confirmed (levels above {top} unknown)." +
                          (string.IsNullOrEmpty(s.note) ? string.Empty : " " + s.note);

            bool flak = s.type == BuildingType.Flak;
            string path = $"{DefinitionDir}/{s.type}.asset";
            BuildingDefinition def = AssetDatabase.LoadAssetAtPath<BuildingDefinition>(path);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<BuildingDefinition>();
                AssetDatabase.CreateAsset(def, path);
            }

            // Max count: only the Flak limit (28) is defined (BaseFlakRoster.MaxFlaks and the 28-position layout).
            def.EditorInitialize(s.type, s.displayName, flak ? FlakScale : BuildingScale,
                flak ? BaseFlakRoster.MaxFlaks : 0, levels, note);
            EditorUtility.SetDirty(def);
            defs.Add(def);
            log.AppendLine($"  definition {path}: levels [{string.Join(", ", present)}] scale {def.InstanceScale} max {(def.HasMaxCount ? def.MaxCount.ToString() : "undefined")}");
        }

        BuildingCatalog catalog = AssetDatabase.LoadAssetAtPath<BuildingCatalog>(CatalogPath);
        if (catalog == null)
        {
            catalog = ScriptableObject.CreateInstance<BuildingCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
        }

        catalog.EditorSetDefinitions(defs);
        EditorUtility.SetDirty(catalog);
        return catalog;
    }

    private static void EnsureInitialConfig(StringBuilder log)
    {
        InitialBuildingConfig config = AssetDatabase.LoadAssetAtPath<InitialBuildingConfig>(InitialConfigPath);
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<InitialBuildingConfig>();
            AssetDatabase.CreateAsset(config, InitialConfigPath);
        }

        config.EditorSetEntries(new List<InitialBuildingConfig.Entry>
        {
            new InitialBuildingConfig.Entry
            {
                type = BuildingType.Flak, count = 2, buildingLevel = 1,
                // Row 1 position 1 on each side: the two entrance Flaks (original Flak 1/2 side of the base).
                slotIds = new[] { FlakArcLayout.SlotId(true, 1, 1), FlakArcLayout.SlotId(false, 1, 1) }
            },
            new InitialBuildingConfig.Entry
            {
                type = BuildingType.FactoryRefinery, count = 1, buildingLevel = 1,
                slotIds = new[] { Spec(BuildingType.FactoryRefinery).slotId }
            }
        });
        EditorUtility.SetDirty(config);
        log.AppendLine($"  initial config {InitialConfigPath}: 2 x L1 Flak + 1 x L1 Factory/Refinery");
    }

    /// <summary>Reserved slot footprint per type: max X and Z over every registered level model.</summary>
    public static Dictionary<BuildingType, Vector2> SlotFootprints(BuildingCatalog catalog, StringBuilder log)
    {
        Dictionary<BuildingType, Vector2> result = new Dictionary<BuildingType, Vector2>();
        foreach (BuildingDefinition def in catalog.Definitions)
        {
            Vector2 max = Vector2.zero;
            foreach (var lv in def.Levels)
            {
                if (ModularFoundationGeometry.TryModelFootprint(lv.prefab, def.InstanceScale, out Vector2 size,
                        out Vector2 offset, out float height, out float minY, lv.facingYawOffset))
                {
                    max = Vector2.Max(max, size + 2f * new Vector2(Mathf.Abs(offset.x), Mathf.Abs(offset.y)));
                    log?.AppendLine($"    footprint {def.Type} L{lv.level}: {size.x:F4} x {size.y:F4} (offset {offset.x:F4},{offset.y:F4}) h {height:F4} minY {minY:F5}");
                }
            }

            result[def.Type] = max;
            log?.AppendLine($"  slot footprint {def.Type}: {max.x:F4} x {max.y:F4}");
        }

        return result;
    }

    public static FlakArcLayout.Parameters FlakParameters(BaseLayoutFamily family, Vector2 structureCentre,
        float originalFootprintFrontZ, float flakHalfExtent)
    {
        return new FlakArcLayout.Parameters
        {
            centre = structureCentre,
            innerRadius = family == BaseLayoutFamily.Air ? AirFlakInnerRadius : GroundFlakInnerRadius,
            rowSeparation = FlakRowSeparation,
            chordPitch = FlakChordPitch,
            corridorOffset = FlakCorridorOffset,
            clearHalfWidth = FlakClearHalfWidth,
            frontLimitZ = originalFootprintFrontZ - FlakFrontLimitMargin,
            flakHalfExtent = flakHalfExtent
        };
    }

    public static Vector2 StructureCentre(GameObject original, BaseLayoutFamily family)
    {
        string structure = family == BaseLayoutFamily.Air ? "futuristic_circular_base_3d_model" : "circular_arena_3d_model";
        Transform t = original.transform.Find(structure);
        return t == null ? Vector2.zero : new Vector2(t.localPosition.x, t.localPosition.z);
    }

    /// <summary>Root-local building position of each anchor's original model instance.</summary>
    public static Dictionary<string, Vector3> OriginalBuildingPositions(GameObject source)
    {
        Dictionary<string, Vector3> result = new Dictionary<string, Vector3>();
        Transform anchors = source.transform.Find("Building Anchors");
        foreach (Transform anchor in anchors)
        {
            if (anchor.childCount == 0) continue;
            Transform model = anchor.GetChild(0);
            result[anchor.name] = anchors.localPosition + anchor.localPosition + model.localPosition;
        }

        return result;
    }

    private static bool ExcludeForSurface(Transform t)
    {
        Transform p = t.parent;
        return (p != null && p.name == "Building Anchors") || t.name == "Base Flaks" || t.name == "Helipad" ||
               (p != null && p.parent == null && t.name.StartsWith("mecha_tank_3d_model", StringComparison.Ordinal));
    }

    private static void BuildFoundation(BaseSpec spec, Dictionary<BuildingType, Vector2> footprints, StringBuilder log)
    {
        GameObject original = AssetDatabase.LoadAssetAtPath<GameObject>(spec.originalPath);
        GameObject slotSource = AssetDatabase.LoadAssetAtPath<GameObject>(spec.slotSourcePath);
        log.AppendLine($"--- {spec.baseType} Foundation (from {spec.originalPath}, slots from {spec.slotSourcePath})");

        // 1. Original footprint exactly as BaseSpawner combines it (origin, identity).
        GameObject probe = UnityEngine.Object.Instantiate(original, Vector3.zero, Quaternion.identity);
        probe.hideFlags = HideFlags.HideAndDontSave;
        ModularFoundationGeometry.TryCombinedBounds(probe, true, out Bounds originalBounds);
        Bounds helipadBounds = default(Bounds);
        bool hasHelipad = false;
        Transform padT = probe.transform.Find("Helipad");
        if (padT != null && ModularFoundationGeometry.TryCombinedBounds(padT.gameObject, true, out helipadBounds)) hasHelipad = true;
        UnityEngine.Object.DestroyImmediate(probe);

        // 2. Slot positions from the existing building instances, then layout corrections.
        Dictionary<string, Vector3> positions = OriginalBuildingPositions(slotSource);
        ApplyLayoutCorrections(spec, positions, footprints, hasHelipad, helipadBounds, log);
        Vector2 centre = StructureCentre(original, spec.family);
        ApplyEnvelopeRule(spec, slotSource, centre, positions, footprints, hasHelipad, helipadBounds, log);
        ApplyClearanceSearch(spec, slotSource, centre, positions, footprints, hasHelipad, helipadBounds, log);

        Vector2 flakFootprint = footprints[BuildingType.Flak];
        FlakArcLayout.Parameters flakParams = FlakParameters(spec.family, centre, originalBounds.max.z,
            Mathf.Max(flakFootprint.x, flakFootprint.y) * 0.5f);
        foreach (int row in new[] { 1, 2 })
        {
            float r = FlakArcLayout.RowRadius(flakParams, row);
            log.AppendLine($"  Flak row {row}: radius {r:F4}, first angle {FlakArcLayout.FirstAngle(flakParams, r) * Mathf.Rad2Deg:F3} deg " +
                           $"(clear half-width {flakParams.clearHalfWidth:F4}, front limit z {flakParams.frontLimitZ:F4})");
        }

        // 3. Working copy: unpack only the outermost prefab so the structure,
        // helipad and road/wall stay connected to their FBX models.
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(original);
        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
        inst.name = $"{spec.baseType} Foundation";

        Transform anchors = inst.transform.Find("Building Anchors");
        int removed = 0;
        foreach (Transform anchor in anchors)
        {
            for (int i = anchor.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(anchor.GetChild(i).gameObject);
                removed++;
            }
        }

        Transform baseFlaks = inst.transform.Find("Base Flaks");
        if (baseFlaks != null)
        {
            for (int i = baseFlaks.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(baseFlaks.GetChild(i).gameObject);
                removed++;
            }
        }

        for (int i = inst.transform.childCount - 1; i >= 0; i--)
        {
            Transform c = inst.transform.GetChild(i);
            if (IsStrayRootTurret(c, inst.transform))
            {
                string turretName = c.name;
                UnityEngine.Object.DestroyImmediate(c.gameObject);
                removed++;
                log.AppendLine("  removed redundant root turret " + turretName);
            }
        }

        log.AppendLine($"  removed {removed} constructible/redundant objects; Base Flaks kept: {baseFlaks != null}");

        // 4. Building slots under the existing anchors (anchors themselves unchanged).
        List<BuildingSlot> slots = new List<BuildingSlot>();
        foreach (BuildingSpec b in Buildings)
        {
            if (b.anchorName == null) continue;
            Transform anchor = anchors.Find(b.anchorName);
            if (anchor == null || !positions.TryGetValue(b.anchorName, out Vector3 rootPos))
            {
                log.AppendLine($"  ERROR anchor/position missing for {b.anchorName}");
                continue;
            }

            GameObject slotGo = new GameObject("Slot " + b.slotId);
            slotGo.transform.SetParent(anchor, false);
            slotGo.transform.localPosition = rootPos - anchors.localPosition - anchor.localPosition;
            slotGo.transform.localRotation = Quaternion.identity;
            slotGo.transform.localScale = Vector3.one;
            BuildingSlot slot = slotGo.AddComponent<BuildingSlot>();
            slot.EditorInitialize(b.slotId, new[] { b.type }, BuildingScale, footprints[b.type], false);
            slots.Add(slot);
            log.AppendLine($"  slot {b.slotId,-18} root ({rootPos.x:F4}, {rootPos.y:F4}, {rootPos.z:F4}) footprint {footprints[b.type].x:F4}x{footprints[b.type].y:F4}");
        }

        // 5. 28 Flak slots, outside the wall, heights from the foundation surface.
        GameObject flakRoot = new GameObject("Flak Slots");
        flakRoot.transform.SetParent(inst.transform, false);
        Vector2 flakSize = footprints[BuildingType.Flak];
        List<FlakArcLayout.Position> flakPositions = FlakArcLayout.Generate(flakParams);
        Dictionary<string, float> heightById = new Dictionary<string, float>();
        Dictionary<string, bool> terrainById = new Dictionary<string, bool>();
        Dictionary<string, bool> supportById = new Dictionary<string, bool>();
        using (var sampler = new ModularFoundationGeometry.SurfaceSampler(original, ExcludeForSurface))
        {
            foreach (FlakArcLayout.Position p in flakPositions)
            {
                var rect = new ModularFoundationGeometry.Rect2(p.xz, flakSize, p.yawDegrees);
                sampler.SampleRect(rect, 5, out float maxH, out float hitFraction);
                heightById[p.slotId] = hitFraction > 0f ? maxH : 0f;
                terrainById[p.slotId] = hitFraction < 1f;
                supportById[p.slotId] = hitFraction > 0f;
                if (maxH > 0.03f) log.AppendLine($"  WARNING {p.slotId} footprint reaches surface height {maxH:F4} (wall?)");
            }
        }

        // Approved layout (frozen FlakDefensiveLayout) is authoritative when present,
        // so a rebuild can never drift from the approved positions.
        FlakLayoutCatalog flakCatalog = AssetDatabase.LoadAssetAtPath<FlakLayoutCatalog>(FlakLayoutFreezer.CatalogPath);
        FlakDefensiveLayout approved = flakCatalog != null ? flakCatalog.ForBaseType(spec.baseType) : null;
        int approvedMismatches = 0;
        foreach (FlakArcLayout.Position p in flakPositions)
        {
            // Mirrored pairs share one height (the higher sampled surface) so the
            // left and right layouts are exact mirrors in X, Y, Z and yaw.
            string mirror = FlakArcLayout.SlotId(!p.right, p.row, p.index);
            float y = Mathf.Max(heightById[p.slotId], heightById[mirror]);
            bool terrain = terrainById[p.slotId] || terrainById[mirror];
            Vector3 localPos = new Vector3(p.xz.x, y, p.xz.y);
            Quaternion localRot = Quaternion.Euler(0f, p.yawDegrees, 0f);
            if (approved != null && approved.TryGet(p.slotId, out FlakDefensiveLayout.Position ap))
            {
                if (!SpawnerFootprintProbe.BitwiseEqual(localPos, ap.localPosition) || Mathf.Abs(Mathf.DeltaAngle(p.yawDegrees, ap.yawDegrees)) > 0.001f)
                    approvedMismatches++;
                localPos = ap.localPosition;
                bool hasExactRotation = ap.localRotation.x != 0f || ap.localRotation.y != 0f || ap.localRotation.z != 0f || ap.localRotation.w != 0f;
                localRot = hasExactRotation ? ap.localRotation : Quaternion.Euler(0f, ap.yawDegrees, 0f);
            }

            GameObject go = new GameObject("Slot " + p.slotId);
            go.transform.SetParent(flakRoot.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = Vector3.one;
            BuildingSlot slot = go.AddComponent<BuildingSlot>();
            slot.EditorInitialize(p.slotId, new[] { BuildingType.Flak }, FlakScale, flakSize, terrain);
            slot.EditorSetFoundationSupport(supportById[p.slotId] || supportById[mirror]);
            slots.Add(slot);
        }

        log.AppendLine(approved == null
            ? "  Flak slots from FlakArcLayout (no approved layout asset yet)"
            : $"  Flak slots from approved layout '{approved.LayoutName}' ({approvedMismatches} generated positions differed from it)");

        // 6. Reserved footprint marker = the original prefab's footprint.
        GameObject marker = new GameObject("Reserved Footprint");
        marker.transform.SetParent(inst.transform, false);
        marker.transform.SetSiblingIndex(0);
        Mesh mesh = EnsureFootprintMesh(spec.baseType, originalBounds.min, originalBounds.max);
        marker.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = marker.AddComponent<MeshRenderer>();
        mr.enabled = false;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        marker.AddComponent<ReservedFootprintMarker>().EditorInitialize(original.name, originalBounds.min, originalBounds.max);
        marker.SetActive(false);

        BaseFoundation foundation = inst.AddComponent<BaseFoundation>();
        foundation.EditorInitialize(spec.baseType, spec.family, original, flakParams, originalBounds.center, originalBounds.size);
        BaseBuildingState state = inst.AddComponent<BaseBuildingState>();
        state.EditorSetSlots(slots);

        string path = FoundationPath(spec.baseType);
        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(inst, path, out bool saved);
        UnityEngine.Object.DestroyImmediate(inst);
        log.AppendLine($"  saved {path} ok={saved}; slots {slots.Count}; original bounds c{originalBounds.center.ToString("F4")} s{originalBounds.size.ToString("F4")}");
        TuneFootprintMarker(original, savedPrefab, mesh, log);
    }

    /// <summary>
    /// Makes the foundation's spawner footprint BIT-identical to the original's.
    /// Bounds.Encapsulate re-derives centre/extents from min/max, so a marker at
    /// the exact original min/max can still differ by one float ULP. Each axis
    /// is independent, so search +-3 ULP on that axis's min and max until the
    /// spawner's own measurement matches. Only the marker mesh asset changes.
    /// </summary>
    private static void TuneFootprintMarker(GameObject original, GameObject foundation, Mesh mesh, StringBuilder log)
    {
        SpawnerFootprintProbe.Result target = SpawnerFootprintProbe.Measure(original, 1f);
        Vector3[] baseVerts = mesh.vertices;
        Vector3 min = baseVerts[0], max = baseVerts[6];

        bool AxisMatches(int axis)
        {
            SpawnerFootprintProbe.Result r = SpawnerFootprintProbe.Measure(foundation, 1f);
            return BitConverter.SingleToInt32Bits(r.boundsCenter[axis]) == BitConverter.SingleToInt32Bits(target.boundsCenter[axis]) &&
                   BitConverter.SingleToInt32Bits(r.boundsSize[axis]) == BitConverter.SingleToInt32Bits(target.boundsSize[axis]);
        }

        for (int axis = 0; axis < 3; axis++)
        {
            if (AxisMatches(axis)) continue;
            float min0 = min[axis], max0 = max[axis];
            bool found = false;
            for (int dMin = -3; dMin <= 3 && !found; dMin++)
            {
                for (int dMax = -3; dMax <= 3 && !found; dMax++)
                {
                    min[axis] = Ulp(min0, dMin);
                    max[axis] = Ulp(max0, dMax);
                    SetBoxVertices(mesh, min, max);
                    found = AxisMatches(axis);
                    if (found) log.AppendLine($"  footprint marker axis {"XYZ"[axis]} tuned by ULP min {dMin:+0;-0;0} max {dMax:+0;-0;0}");
                }
            }

            if (!found)
            {
                min[axis] = min0;
                max[axis] = max0;
                SetBoxVertices(mesh, min, max);
                log.AppendLine($"  WARNING footprint marker axis {"XYZ"[axis]} could not be made bit-identical");
            }
        }

        EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssets();
        bool exact = SpawnerFootprintProbe.Measure(foundation, 1f).BitEquals(target);
        log.AppendLine($"  spawner footprint bit-identical to original: {exact}");
    }

    private static float Ulp(float v, int steps)
    {
        return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) + steps);
    }

    private static void SetBoxVertices(Mesh mesh, Vector3 min, Vector3 max)
    {
        mesh.vertices = new[]
        {
            new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z)
        };
        mesh.RecalculateBounds();
    }

    /// <summary>
    /// Furthest horizontal corner distance (from the structure centre) of the
    /// ORIGINAL building model at an anchor, measured with its real footprint.
    /// </summary>
    public static bool TryOriginalCornerRadius(GameObject slotSource, string anchorName, Vector2 centre, out float radius)
    {
        radius = 0f;
        Transform anchors = slotSource.transform.Find("Building Anchors");
        Transform anchor = anchors != null ? anchors.Find(anchorName) : null;
        if (anchor == null || anchor.childCount == 0) return false;
        Transform model = anchor.GetChild(0);
        GameObject src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(model.gameObject);
        if (src == null || !ModularFoundationGeometry.TryModelFootprint(src, model.localScale.x / src.transform.localScale.x,
                out Vector2 size, out Vector2 offset, out _, out _)) return false;
        Vector3 p = anchors.localPosition + anchor.localPosition + model.localPosition;
        var rect = new ModularFoundationGeometry.Rect2(new Vector2(p.x, p.z) + offset, size, 0f);
        foreach (Vector2 k in rect.Corners()) radius = Mathf.Max(radius, Vector2.Distance(k, centre));
        return true;
    }

    private static float CornerRadius(Vector3 pos, Vector2 size, Vector2 centre)
    {
        var rect = new ModularFoundationGeometry.Rect2(new Vector2(pos.x, pos.z), size, 0f);
        float r = 0f;
        foreach (Vector2 k in rect.Corners()) r = Mathf.Max(r, Vector2.Distance(k, centre));
        return r;
    }

    /// <summary>
    /// A slot's reserved footprint (largest registered level, often the wider
    /// L1 model) must not reach further towards the wall than the original
    /// building at that anchor did. Offending slots move radially inward, but
    /// never into another slot or the Helipad.
    /// </summary>
    private static void ApplyEnvelopeRule(BaseSpec spec, GameObject slotSource, Vector2 centre,
        Dictionary<string, Vector3> positions, Dictionary<BuildingType, Vector2> footprints,
        bool hasHelipad, Bounds helipadBounds, StringBuilder log)
    {
        var padRect = hasHelipad
            ? new ModularFoundationGeometry.Rect2(new Vector2(helipadBounds.center.x, helipadBounds.center.z),
                new Vector2(helipadBounds.size.x, helipadBounds.size.z), 0f)
            : default(ModularFoundationGeometry.Rect2);

        foreach (BuildingSpec b in Buildings)
        {
            if (b.anchorName == null || !positions.TryGetValue(b.anchorName, out Vector3 pos)) continue;
            Vector2 rel = new Vector2(pos.x, pos.z) - centre;
            if (rel.magnitude < 0.05f) continue; // centre slot: no radial direction
            if (!TryOriginalCornerRadius(slotSource, b.anchorName, centre, out float r0)) continue;
            Vector2 size = footprints[b.type];
            float r1 = CornerRadius(pos, size, centre);
            if (r1 <= r0 + 0.0005f) continue;

            Vector2 dir = rel.normalized;
            float wanted = 0f;
            while (wanted < 0.1f && CornerRadius(pos - new Vector3(dir.x, 0f, dir.y) * wanted, size, centre) > r0) wanted += 0.0005f;

            float t = wanted;
            while (t > 0f)
            {
                Vector3 cand = pos - new Vector3(dir.x, 0f, dir.y) * t;
                var rect = new ModularFoundationGeometry.Rect2(new Vector2(cand.x, cand.z), size, 0f);
                bool clear = !hasHelipad || ModularFoundationGeometry.Separation(rect, padRect) >= BuildingSlotClearance;
                foreach (BuildingSpec o in Buildings)
                {
                    if (!clear) break;
                    if (o.anchorName == null || o.anchorName == b.anchorName || !positions.TryGetValue(o.anchorName, out Vector3 op)) continue;
                    var orect = new ModularFoundationGeometry.Rect2(new Vector2(op.x, op.z), footprints[o.type], 0f);
                    clear = ModularFoundationGeometry.Separation(rect, orect) >= BuildingSlotClearance;
                }

                if (clear) break;
                t -= 0.0005f;
            }

            t = Mathf.Max(0f, t);
            Vector3 moved = pos - new Vector3(dir.x, 0f, dir.y) * t;
            moved.x = Mathf.Round(moved.x * 10000f) / 10000f;
            moved.z = Mathf.Round(moved.z * 10000f) / 10000f;
            float r2 = CornerRadius(moved, size, centre);
            log.AppendLine($"  envelope: {b.slotId} corner r {r1:F4} > original {r0:F4}; moved inward {t:F4} " +
                           $"({pos.x:F4},{pos.z:F4}) -> ({moved.x:F4},{moved.z:F4}), corner r now {r2:F4}" +
                           (r2 > r0 + 0.0005f ? " (blocked by a neighbour: residual " + (r2 - r0).ToString("F4") + ")" : string.Empty));
            positions[b.anchorName] = moved;
        }
    }

    /// <summary>
    /// Stage 3A.1: for slots the radial envelope rule could not fix (blocked by
    /// a neighbour or the Helipad), search a 2D neighbourhood (+-0.04, step
    /// 0.0005) for the position nearest the ORIGINAL building position whose
    /// reserved footprint reaches no further out than the original building,
    /// keeps 0.005 from every other slot and HelipadMinClearance from the Helipad.
    /// </summary>
    private static void ApplyClearanceSearch(BaseSpec spec, GameObject slotSource, Vector2 centre,
        Dictionary<string, Vector3> positions, Dictionary<BuildingType, Vector2> footprints,
        bool hasHelipad, Bounds helipadBounds, StringBuilder log)
    {
        Dictionary<string, Vector3> originals = OriginalBuildingPositions(slotSource);
        var padRect = hasHelipad
            ? new ModularFoundationGeometry.Rect2(new Vector2(helipadBounds.center.x, helipadBounds.center.z),
                new Vector2(helipadBounds.size.x, helipadBounds.size.z), 0f)
            : default(ModularFoundationGeometry.Rect2);

        foreach (BuildingSpec b in Buildings)
        {
            if (b.anchorName == null || !positions.TryGetValue(b.anchorName, out Vector3 pos)) continue;
            Vector2 rel = new Vector2(pos.x, pos.z) - centre;
            if (rel.magnitude < 0.05f) continue;
            if (!TryOriginalCornerRadius(slotSource, b.anchorName, centre, out float r0)) continue;
            Vector2 size = footprints[b.type];
            float r1 = CornerRadius(pos, size, centre);
            if (r1 <= r0 + 0.0005f) continue;

            Vector3 origin = originals[b.anchorName];
            float bestCost = float.PositiveInfinity;
            Vector3 best = pos;
            float bestPad = float.NaN, bestNeighbour = float.NaN;
            const int Steps = 80;
            for (int i = -Steps; i <= Steps; i++)
            {
                for (int j = -Steps; j <= Steps; j++)
                {
                    Vector3 cand = new Vector3(
                        Mathf.Round((pos.x + i * 0.0005f) * 10000f) / 10000f, pos.y,
                        Mathf.Round((pos.z + j * 0.0005f) * 10000f) / 10000f);
                    if (CornerRadius(cand, size, centre) > r0) continue;
                    var rect = new ModularFoundationGeometry.Rect2(new Vector2(cand.x, cand.z), size, 0f);
                    float padGap = hasHelipad ? ModularFoundationGeometry.Separation(rect, padRect) : float.PositiveInfinity;
                    if (padGap < HelipadMinClearance) continue;
                    float minNeighbour = float.PositiveInfinity;
                    foreach (BuildingSpec o in Buildings)
                    {
                        if (o.anchorName == null || o.anchorName == b.anchorName || !positions.TryGetValue(o.anchorName, out Vector3 op)) continue;
                        var orect = new ModularFoundationGeometry.Rect2(new Vector2(op.x, op.z), footprints[o.type], 0f);
                        minNeighbour = Mathf.Min(minNeighbour, ModularFoundationGeometry.Separation(rect, orect));
                    }

                    if (minNeighbour < BuildingSlotClearance) continue;
                    float cost = Vector2.Distance(new Vector2(cand.x, cand.z), new Vector2(origin.x, origin.z));
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = cand;
                        bestPad = padGap;
                        bestNeighbour = minNeighbour;
                    }
                }
            }

            if (float.IsPositiveInfinity(bestCost))
            {
                log.AppendLine($"  WARNING clearance search: no valid position for {b.slotId} within +-0.04");
                continue;
            }

            log.AppendLine($"  clearance search: {b.slotId} ({pos.x:F4},{pos.z:F4}) -> ({best.x:F4},{best.z:F4}); corner r {r1:F4} -> " +
                           $"{CornerRadius(best, size, centre):F4} (original building {r0:F4}); nearest slot gap {bestNeighbour:F4}" +
                           (hasHelipad ? $"; Helipad gap {bestPad:F4}" : string.Empty) +
                           $"; distance from original building position {bestCost:F4}");
            positions[b.anchorName] = best;
        }
    }

    private static void ApplyLayoutCorrections(BaseSpec spec, Dictionary<string, Vector3> positions,
        Dictionary<BuildingType, Vector2> footprints, bool hasHelipad, Bounds helipadBounds, StringBuilder log)
    {
        if (spec.family == BaseLayoutFamily.GroundHeli && hasHelipad)
        {
            // Resource Optimiser slot in front of the structural Helipad: clear the
            // largest registered RO model (L3) from the Helipad edge.
            Vector3 ro = positions["Resource Optimiser"];
            float halfDepth = footprints[BuildingType.ResourceOptimiser].y * 0.5f;
            float required = helipadBounds.max.z + BuildingSlotClearance + halfDepth;
            if (ro.z < required)
            {
                float z = Mathf.Ceil(required * 10000f) / 10000f;
                log.AppendLine($"  correction: Resource Optimiser z {ro.z:F4} -> {z:F4} (Helipad max z {helipadBounds.max.z:F4})");
                ro.z = z;
                positions["Resource Optimiser"] = ro;
            }
        }

        if (spec.family == BaseLayoutFamily.Air)
        {
            // Base Command overlapped Power Plant: move Base Command along -X only.
            Vector3 pp = positions["Power Plant"];
            Vector3 bc = positions["Base Command"];
            float halfX = footprints[BuildingType.PowerPlant].x * 0.5f + footprints[BuildingType.BaseCommand].x * 0.5f;
            float halfZ = footprints[BuildingType.PowerPlant].y * 0.5f + footprints[BuildingType.BaseCommand].y * 0.5f;
            bool overlap = Mathf.Abs(pp.x - bc.x) < halfX + BuildingSlotClearance && Mathf.Abs(pp.z - bc.z) < halfZ + BuildingSlotClearance;
            if (overlap)
            {
                float x = Mathf.Floor((pp.x - halfX - BuildingSlotClearance) * 10000f) / 10000f;
                log.AppendLine($"  correction: Base Command x {bc.x:F4} -> {x:F4} (clears Power Plant)");
                bc.x = x;
                positions["Base Command"] = bc;
            }
        }
    }

    private static Mesh EnsureFootprintMesh(string baseType, Vector3 min, Vector3 max)
    {
        string path = $"{FootprintDir}/{baseType} Reserved Footprint.asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool create = mesh == null;
        if (create) mesh = new Mesh();
        mesh.Clear();
        mesh.name = $"{baseType} Reserved Footprint";
        Vector3[] v =
        {
            new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z)
        };
        int[] tris =
        {
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7
        };
        mesh.vertices = v;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        if (create) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        return mesh;
    }
}
