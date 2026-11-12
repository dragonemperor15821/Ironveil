using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BaseSpawnerValidationRunner
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class ExpectedVariant
    {
        public string name;
        public int fullMapCount;
        public int placement;

        public ExpectedVariant(string name, int fullMapCount, int placement)
        {
            this.name = name;
            this.fullMapCount = fullMapCount;
            this.placement = placement;
        }
    }

    private sealed class VariantConfiguration
    {
        public string name;
        public int placement;
        public GameObject prefab;
        public string prefabPath;
    }

    private sealed class SceneBaseline
    {
        public readonly HashSet<GameObject> hangars =
            new HashSet<GameObject>();
        public readonly Dictionary<GameObject, int> controllerCounts =
            new Dictionary<GameObject, int>();
    }

    private sealed class SpawnedFootprint
    {
        public string name;
        public int placement;
        public Vector3 pivot;
        public Bounds bounds;
        public object placementValidation;
    }

    private sealed class ValidationReport
    {
        private readonly List<string> lines = new List<string>();

        public int failures { get; private set; }
        public int passes { get; private set; }
        public int notTested { get; private set; }

        public void Add(
            string subject,
            string category,
            bool? passed,
            string details)
        {
            string status;
            if (!passed.HasValue)
            {
                status = "NOT TESTED";
                notTested++;
            }
            else if (passed.Value)
            {
                status = "PASS";
                passes++;
            }
            else
            {
                status = "FAIL";
                failures++;
            }

            lines.Add($"{status,-10} | {subject,-18} | {category,-28} | {details}");
        }

        public string Format()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("IRONVEIL BaseSpawner automated validation");
            text.AppendLine($"PASS {passes} | FAIL {failures} | NOT TESTED {notTested}");
            text.AppendLine(new string('-', 112));

            foreach (string line in lines)
            {
                text.AppendLine(line);
            }

            return text.ToString();
        }
    }

    private static readonly ExpectedVariant[] ExpectedVariants =
    {
        new ExpectedVariant("Tank", 110, 0),
        new ExpectedVariant("Heli", 90, 0),
        new ExpectedVariant("Air", 80, 0),
        new ExpectedVariant("Naval", 25, 1),
        new ExpectedVariant("Rail", 17, 2),
        new ExpectedVariant("Heli Naval", 11, 1),
        new ExpectedVariant("Heli Rail", 7, 2),
        new ExpectedVariant("Air Naval", 6, 1),
        new ExpectedVariant("Air Rail", 4, 2)
    };

    [MenuItem("Tools/IRONVEIL/Validate All Nine Base Types")]
    public static void RunFromMenu()
    {
        RunValidation(false);
    }

    // Invocation for Unity batch mode:
    // Unity.exe -batchmode -projectPath <project> -executeMethod
    // BaseSpawnerValidationRunner.RunInBatchMode -quit
    public static void RunInBatchMode()
    {
        RunValidation(true);
    }

    private static void RunValidation(bool exitAfterRun)
    {
        ValidationReport report = new ValidationReport();
        Scene previousActiveScene = SceneManager.GetActiveScene();
        Scene validationScene = default(Scene);
        string temporaryScenePath = null;
        bool validationSceneLoaded = false;

        try
        {
            ValidateFullMapConfiguration(report);

            string mainBuildPath = Path.Combine(
                Application.dataPath,
                "Scenes",
                "Ironveil - MainBuild.unity");
            if (!File.Exists(mainBuildPath))
            {
                report.Add(
                    "MainBuild",
                    "350-base configuration",
                    false,
                    $"Missing {mainBuildPath}.");
            }

            string baselineFullPath = Path.Combine(
                Application.dataPath,
                "Scenes",
                "Ironveil - HangarTest.unity");
            if (!File.Exists(baselineFullPath))
            {
                throw new FileNotFoundException(
                    "Known-good HangarTest scene not found.",
                    baselineFullPath);
            }

            string temporarySceneName =
                $"__IronveilValidation_{Guid.NewGuid():N}.unity";
            temporaryScenePath = $"Assets/Scenes/{temporarySceneName}";
            File.Copy(
                baselineFullPath,
                Path.Combine(Application.dataPath, "Scenes", temporarySceneName),
                false);

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            validationScene = EditorSceneManager.OpenScene(
                temporaryScenePath,
                OpenSceneMode.Additive);
            validationSceneLoaded = validationScene.IsValid() &&
                                    validationScene.isLoaded;

            if (!validationSceneLoaded)
            {
                throw new InvalidOperationException(
                    "Unity could not open the temporary HangarTest copy.");
            }

            EditorSceneManager.SetActiveScene(validationScene);
            RunVariantTests(validationScene, report);
            RunPairRollbackTest(validationScene, report);
        }
        catch (Exception exception)
        {
            report.Add(
                "Runner",
                "Execution",
                false,
                exception.ToString());
        }
        finally
        {
            if (validationSceneLoaded && validationScene.IsValid() &&
                validationScene.isLoaded)
            {
                try
                {
                    BaseSpawner spawner = FindSpawner(validationScene);
                    if (spawner != null)
                    {
                        InvokePrivate(spawner, "ClearRegisteredRiverNarrowings");
                        InvokePrivate(spawner, "ClearGeneratedBases");
                    }
                }
                catch (Exception cleanupException)
                {
                    report.Add(
                        "Temporary scene",
                        "Cleanup",
                        false,
                        cleanupException.Message);
                }

                EditorSceneManager.CloseScene(validationScene, true);
            }

            if (!string.IsNullOrEmpty(temporaryScenePath))
            {
                AssetDatabase.DeleteAsset(temporaryScenePath);
            }

            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
            {
                EditorSceneManager.SetActiveScene(previousActiveScene);
            }

            AssetDatabase.Refresh();

            string formatted = report.Format();
            string reportPath = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName,
                "Library",
                "IronveilBaseValidation-report.txt");
            File.WriteAllText(reportPath, formatted);
            Debug.Log(formatted);

            if (exitAfterRun)
            {
                EditorApplication.Exit(report.failures == 0 ? 0 : 1);
            }
        }
    }

    private static void ValidateFullMapConfiguration(ValidationReport report)
    {
        string mainBuildPath = Path.Combine(
            Application.dataPath,
            "Scenes",
            "Ironveil - MainBuild.unity");
        if (!File.Exists(mainBuildPath))
        {
            return;
        }

        string yaml = File.ReadAllText(mainBuildPath);
        MatchCollection matches = Regex.Matches(
            yaml,
            @"(?m)^\s*- baseName: (?<name>[^\r\n]+)\r?\n\s+prefab: [^\r\n]*\r?\n\s+count: (?<count>\d+)\r?\n\s+placement: (?<placement>\d+)");

        Dictionary<string, ExpectedVariant> expected =
            ExpectedVariants.ToDictionary(item => item.name);
        Dictionary<string, int> actual = new Dictionary<string, int>();
        bool allMatch = matches.Count == ExpectedVariants.Length;
        int total = 0;

        foreach (Match match in matches)
        {
            string name = match.Groups["name"].Value.Trim();
            int count = int.Parse(match.Groups["count"].Value);
            int placement = int.Parse(match.Groups["placement"].Value);
            total += count;
            actual[name] = count;

            if (!expected.TryGetValue(name, out ExpectedVariant expectedVariant) ||
                expectedVariant.fullMapCount != count ||
                expectedVariant.placement != placement)
            {
                allMatch = false;
            }
        }

        allMatch &= total == 350;
        report.Add(
            "MainBuild",
            "350-base configuration",
            allMatch,
            $"Entries {matches.Count}/9, total {total}/350, " +
            (allMatch ? "names/counts/placements match." :
                $"observed: {string.Join(", ", actual.Select(kv => $"{kv.Key}={kv.Value}"))}"));

        bool idsUnique = VerifyConfiguredIds(actual, out string idDetails);
        report.Add(
            "MainBuild",
            "350 deterministic IDs",
            idsUnique,
            idDetails);
    }

    private static bool VerifyConfiguredIds(
        Dictionary<string, int> configuredCounts,
        out string details)
    {
        Type requestType = typeof(BaseSpawner).GetNestedType(
            "BaseRequest",
            BindingFlags.NonPublic);
        MethodInfo buildBaseId = typeof(BaseSpawner).GetMethod(
            "BuildBaseId",
            BindingFlags.Static | BindingFlags.NonPublic);

        if (requestType == null || buildBaseId == null)
        {
            details = "Could not access the existing BaseSpawner ID generator.";
            return false;
        }

        HashSet<string> ids = new HashSet<string>();
        int generated = 0;
        bool deterministic = true;

        foreach (ExpectedVariant variant in ExpectedVariants)
        {
            if (!configuredCounts.TryGetValue(variant.name, out int count))
            {
                details = $"No configured count for {variant.name}.";
                return false;
            }

            for (int index = 1; index <= count; index++)
            {
                object request = Activator.CreateInstance(requestType, true);
                SetField(request, "baseName", variant.name);
                SetField(request, "index", index);

                string first = buildBaseId.Invoke(null, new[] { request }) as string;
                string second = buildBaseId.Invoke(null, new[] { request }) as string;
                deterministic &= first == second &&
                                 !string.IsNullOrWhiteSpace(first);
                if (string.IsNullOrWhiteSpace(first) || !ids.Add(first))
                {
                    details = $"Duplicate or empty ID at {variant.name} #{index}: {first}.";
                    return false;
                }

                generated++;
            }
        }

        bool passed = generated == 350 && ids.Count == 350 && deterministic;
        details = $"Generated {generated} IDs without spawning bases; unique={ids.Count}, deterministic={deterministic}.";
        return passed;
    }

    private static void RunVariantTests(
        Scene scene,
        ValidationReport report)
    {
        BaseSpawner spawner = FindSpawner(scene);
        if (spawner == null)
        {
            report.Add("HangarTest", "spawner setup", false,
                "Temporary scene has no BaseSpawner.");
            return;
        }

        List<VariantConfiguration> configurations =
            ConfigureOneRequestPerVariant(spawner, report);

        if (configurations.Count != ExpectedVariants.Length ||
            configurations.Any(item => item.prefab == null))
        {
            report.Add("HangarTest", "prefab references", false,
                "Every one of the nine entries must have its main-base prefab assigned.");
            return;
        }

        Dictionary<string, bool> requestPrefabMatches =
            VerifyBuildRequestPrefabs(spawner, configurations, report);

        report.Add("HangarTest", "prefab references", true,
            "All nine main-base prefabs and both hangar prefabs are assigned.");

        SceneBaseline baseline = CaptureSceneBaseline(scene);
        spawner.GenerateBases();

        List<GameObject> generatedRoots = spawner.transform
            .Cast<Transform>()
            .Where(child => child.name.StartsWith("GeneratedBase_", StringComparison.Ordinal))
            .Select(child => child.gameObject)
            .ToList();

        IList validations = GetPrivateField(spawner, "lastPlacements") as IList;
        bool diagnosticsAligned = validations != null &&
                                  validations.Count == generatedRoots.Count;
        report.Add(
            "All variants",
            "placement diagnostics",
            diagnosticsAligned,
            diagnosticsAligned
                ? $"{generatedRoots.Count} generated roots have matching placement diagnostics."
                : $"Generated roots={generatedRoots.Count}; diagnostics={validations?.Count ?? -1}.");

        Dictionary<string, object> validationByRoot = new Dictionary<string, object>();
        if (diagnosticsAligned)
        {
            for (int i = 0; i < generatedRoots.Count; i++)
            {
                validationByRoot[generatedRoots[i].name] = validations[i];
            }
        }

        HashSet<string> uniqueIds = new HashSet<string>();
        int expectedNavalHangars = 0;
        int expectedRailHangars = 0;

        foreach (ExpectedVariant expected in ExpectedVariants)
        {
            string generatedName = $"GeneratedBase_{expected.name}_001";
            GameObject generatedRoot =
                generatedRoots.FirstOrDefault(root => root.name == generatedName);
            bool spawned = generatedRoot != null;

            report.Add(
                expected.name,
                "spawn",
                spawned,
                spawned ? $"Found {generatedName}." : "No generated root; request failed or prefab was rejected.");

            if (!spawned)
            {
                report.Add(expected.name, "prefab identity", false,
                    "Cannot inspect main-base prefab because it did not spawn.");
                report.Add(expected.name, "controller / unique ID", false,
                    "Cannot inspect controller because the main base did not spawn.");
                report.Add(expected.name, "pair integrity", false,
                    "Expected generated root is missing.");
                report.Add(expected.name, "terrain / placement", false,
                    "No successful placement to validate.");
                report.Add(expected.name, "turret orientation", null,
                    "No spawned base to compare.");
                report.Add(expected.name, "collider test", null,
                    "No spawned base to inspect.");
                continue;
            }

            bool isPair = expected.placement != 0;
            string mainName = isPair ? $"{expected.name} Main Base" : generatedName;
            GameObject mainBase = isPair
                ? FindDirectChild(generatedRoot.transform, mainName)?.gameObject
                : generatedRoot;
            VariantConfiguration configuration =
                configurations.First(item => item.name == expected.name);

            bool prefabMatches = mainBase != null &&
                                 requestPrefabMatches.TryGetValue(
                                     expected.name,
                                     out bool requestMatches) &&
                                 requestMatches &&
                                 PrefabHierarchyMatches(mainBase, configuration.prefab);
            report.Add(
                expected.name,
                "prefab identity",
                prefabMatches,
                prefabMatches
                    ? $"BuildRequests uses {configuration.prefabPath}; spawned hierarchy/mesh/material signature matches."
                    : $"Request mapping or spawned hierarchy signature differs from {configuration.prefabPath}.");

            BaseController[] mainControllers = mainBase != null
                ? mainBase.GetComponentsInChildren<BaseController>(true)
                : Array.Empty<BaseController>();
            BaseController[] logicalControllers = generatedRoot
                .GetComponentsInChildren<BaseController>(true);

            string expectedId = BuildExpectedBaseId(expected.name, 1);
            bool controllerValid = logicalControllers.Length == 1 &&
                                   mainControllers.Length == 1 &&
                                   logicalControllers[0].gameObject == mainBase &&
                                   mainControllers[0].BaseId == expectedId &&
                                   mainControllers[0].BaseName == $"{expected.name} Base" &&
                                   uniqueIds.Add(mainControllers[0].BaseId);
            report.Add(
                expected.name,
                "controller / unique ID",
                controllerValid,
                $"logical-root controllers={logicalControllers.Length}, " +
                $"main-root controllers={mainControllers.Length}, " +
                (mainControllers.Length == 1
                    ? $"id={mainControllers[0].BaseId}, name={mainControllers[0].BaseName}."
                    : "main-base controller missing or duplicated."));

            GameObject hangar = null;
            if (expected.placement == 1)
            {
                expectedNavalHangars++;
                hangar = FindDirectChild(generatedRoot.transform, "Naval Hangar")?.gameObject;
            }
            else if (expected.placement == 2)
            {
                expectedRailHangars++;
                hangar = FindDirectChild(generatedRoot.transform, "Rail Hangar")?.gameObject;
            }

            bool pairValid;
            if (!isPair)
            {
                bool hasEmbeddedHangar = FindDescendant(mainBase.transform, "Naval Hangar") != null ||
                                         FindDescendant(mainBase.transform, "Rail Hangar") != null;
                pairValid = generatedRoot == mainBase && !hasEmbeddedHangar;
                report.Add(expected.name, "pair integrity", pairValid,
                    pairValid ? "Land base is a single root with no hangar." :
                        "Land base has unexpected pair structure or embedded hangar.");
            }
            else
            {
                int expectedChildren = 2;
                BaseController[] hangarControllers = hangar != null
                    ? hangar.GetComponentsInChildren<BaseController>(true)
                    : Array.Empty<BaseController>();
                string embeddedHangarName = expected.placement == 1
                    ? "Naval Hangar"
                    : "Rail Hangar";
                bool mainBaseContainsHangar = mainBase != null &&
                    FindDescendant(mainBase.transform, embeddedHangarName) != null;
                pairValid = hangar != null && mainBase != null &&
                            generatedRoot.transform.childCount == expectedChildren &&
                            mainBase.transform.parent == generatedRoot.transform &&
                            hangar.transform.parent == generatedRoot.transform &&
                            !mainBaseContainsHangar &&
                            hangarControllers.Length == 0 &&
                            logicalControllers.Length == 1 &&
                            mainControllers.Length == 1 &&
                            logicalControllers[0].gameObject == mainBase;
                report.Add(expected.name, "pair integrity", pairValid,
                    pairValid
                        ? $"Main base + one {hangar.name} under one root; hangar has no controller."
                        : $"children={generatedRoot.transform.childCount}, embeddedHangar={mainBaseContainsHangar}, hangarControllers={hangarControllers.Length}.");
            }

            object placementValidation = validationByRoot.TryGetValue(
                generatedName,
                out object foundValidation) ? foundValidation : null;
            bool routeValidationPassed = false;
            if (expected.placement == 0)
            {
                float radius = GetPrivateField<float>(spawner, "terrainSampleRadius");
                int sampleCount = GetPrivateField<int>(spawner, "terrainSampleCount");
                float maxVariation = GetPrivateField<float>(spawner, "maxFootprintHeightVariation");
                float maxSlope = GetPrivateField<float>(spawner, "maxSlopeAngle");

                bool sampled = TrySampleTerrain(
                    spawner,
                    mainBase.transform.position,
                    radius,
                    sampleCount,
                    out float variation,
                    out float slope);

                routeValidationPassed = sampled &&
                                        variation <= maxVariation &&
                                        slope <= maxSlope;
                report.Add(expected.name, "terrain / placement", routeValidationPassed,
                    sampled
                        ? $"height variation {variation:F3}/{maxVariation:F3} m, slope {slope:F2}/{maxSlope:F2} deg."
                        : "Terrain footprint could not be sampled.");
                report.Add(expected.name, "turret orientation", null,
                    "Land bases have no associated hangar to face.");
            }
            else if (placementValidation == null)
            {
                report.Add(expected.name, "terrain / placement", false,
                    "Missing route placement diagnostics.");
                report.Add(expected.name, "turret orientation", false,
                    "Missing route placement diagnostics.");
            }
            else
            {
                bool valid = ReadValidation<bool>(placementValidation, "valid");
                float variation = ReadValidation<float>(
                    placementValidation,
                    "footprintHeightVariation");
                float slope = ReadValidation<float>(
                    placementValidation,
                    "footprintMaxSlope");
                float maxVariation = GetPrivateField<float>(spawner, "maxFootprintHeightVariation");
                float maxSlope = GetPrivateField<float>(spawner, "maxSlopeAngle");

                bool routeChecks;
                if (expected.placement == 1)
                {
                    routeChecks = valid &&
                                  ReadValidation<bool>(placementValidation, "hangarOverWater") &&
                                  ReadValidation<bool>(placementValidation, "baseOnLand");
                }
                else
                {
                    routeChecks = valid &&
                                  ReadValidation<bool>(placementValidation, "hangarOnTrack") &&
                                  ReadValidation<bool>(placementValidation, "baseBesideTrack") &&
                                  ReadValidation<bool>(placementValidation, "hangarCoversRails");
                }

                routeValidationPassed = routeChecks &&
                                        variation <= maxVariation &&
                                        slope <= maxSlope;

                bool hangarTerrainPassed = true;
                if (expected.placement == 2)
                {
                    float radius = GetPrivateField<float>(spawner, "terrainSampleRadius");
                    int sampleCount = GetPrivateField<int>(spawner, "terrainSampleCount");
                    Vector3 routePoint = ReadValidation<Vector3>(
                        placementValidation,
                        "routePoint");
                    hangarTerrainPassed = TrySampleTerrain(
                        spawner,
                        routePoint,
                        radius,
                        sampleCount,
                        out float hangarVariation,
                        out float hangarSlope) &&
                                          hangarVariation <= maxVariation &&
                                          hangarSlope <= maxSlope;
                }

                routeValidationPassed &= hangarTerrainPassed;
                report.Add(expected.name, "terrain / placement", routeValidationPassed,
                    $"valid={valid}, variation {variation:F3}/{maxVariation:F3} m, " +
                    $"slope {slope:F2}/{maxSlope:F2} deg, separate hangar terrain={hangarTerrainPassed}.");

                float distance = ReadValidation<float>(
                    placementValidation,
                    "hangarRouteDistance");
                float tolerance = GetPrivateField<float>(spawner, "routeHangarTolerance");
                bool alignment = expected.placement == 1
                    ? ReadValidation<bool>(placementValidation, "hangarOverWater") &&
                      distance <= ReadValidation<float>(placementValidation, "localRiverWidth") * 0.5f + 0.001f
                    : ReadValidation<bool>(placementValidation, "hangarOnTrack") &&
                      distance <= tolerance;
                report.Add(expected.name, "route / gauge", alignment,
                    expected.placement == 1
                        ? $"hangar-to-route distance {distance:F3} m, river width {ReadValidation<float>(placementValidation, "localRiverWidth"):F3} m."
                        : $"hangar-to-track distance {distance:F3}/{tolerance:F3} m, gauge covered={ReadValidation<bool>(placementValidation, "hangarCoversRails")}.");

                bool turretFacing =
                    TryCheckTurretFacing(mainBase, hangar, out float turretDot);
                report.Add(expected.name, "turret orientation", turretFacing,
                    $"turret-side dot toward hangar={turretDot:F3} (required >= 0.75)." );

                if (expected.placement == 2)
                {
                    bool axisMeasured = TryMeasureRailHangarAxis(
                        hangar,
                        ReadValidation<Vector3>(placementValidation, "routeTangent"),
                        out float axisAngle,
                        out string axisDetails);
                    report.Add(
                        expected.name,
                        "rail axis / tangent",
                        axisMeasured ? axisAngle <= 5f : (bool?)null,
                        axisDetails);
                }
            }

            if (isPair)
            {
                Collider[] pairColliders =
                    mainBase.GetComponentsInChildren<Collider>(true)
                        .Concat(hangar.GetComponentsInChildren<Collider>(true))
                        .ToArray();
                Bounds mainBounds = default(Bounds);
                Bounds hangarBounds = default(Bounds);
                bool boundsMeasured = TryGetRendererBounds(mainBase, out mainBounds) &&
                                      TryGetRendererBounds(hangar, out hangarBounds);

                if (pairColliders.Length == 0)
                {
                    string boundsDetail = boundsMeasured
                        ? hangarBounds.Intersects(mainBounds)
                            ? "No colliders; renderer AABBs overlap conservatively, actual mesh collision not measurable."
                            : "No colliders; renderer AABBs are disjoint."
                        : "No colliders and one object has no renderer bounds.";
                    report.Add(expected.name, "collider test", null, boundsDetail);
                }
                else
                {
                    report.Add(expected.name, "collider test", null,
                        $"Found {pairColliders.Length} collider(s); automated physics overlap not run in Edit Mode.");
                }
            }
            else
            {
                Collider[] colliders = mainBase.GetComponentsInChildren<Collider>(true);
                report.Add(expected.name, "collider test", null,
                    colliders.Length == 0
                        ? "No colliders exist in this base prefab."
                        : $"Found {colliders.Length} collider(s); physics overlap not run in Edit Mode.");
            }
        }

        VerifyNoOrphanHangars(
            scene,
            spawner,
            report,
            expectedNavalHangars,
            expectedRailHangars,
            baseline);
        VerifyUniformRiverWidth(scene, report);
        VerifyPairwiseSpacing(
            spawner,
            generatedRoots,
            configurations,
            validationByRoot,
            report);
        report.Add("All variants", "visual quality", null,
            "Geometry checks are automated; appearance still requires a Unity Scene-view screenshot review.");
    }

    private static List<VariantConfiguration> ConfigureOneRequestPerVariant(
        BaseSpawner spawner,
        ValidationReport report)
    {
        SerializedObject serialized = new SerializedObject(spawner);
        SerializedProperty entries = serialized.FindProperty("baseEntries");
        List<VariantConfiguration> configurations =
            new List<VariantConfiguration>();

        if (entries == null || !entries.isArray)
        {
            report.Add("HangarTest", "entry configuration", false,
                "Serialized baseEntries list is missing.");
            return configurations;
        }

        bool allNamesAndPlacementsMatch = entries.arraySize == ExpectedVariants.Length;

        for (int i = 0; i < entries.arraySize; i++)
        {
            SerializedProperty entry = entries.GetArrayElementAtIndex(i);
            string name = entry.FindPropertyRelative("baseName").stringValue;
            GameObject prefab = entry.FindPropertyRelative("prefab").objectReferenceValue as GameObject;
            int placement = entry.FindPropertyRelative("placement").intValue;

            ExpectedVariant expected = ExpectedVariants.FirstOrDefault(item => item.name == name);
            if (expected == null || expected.placement != placement)
            {
                allNamesAndPlacementsMatch = false;
            }

            configurations.Add(new VariantConfiguration
            {
                name = name,
                placement = placement,
                prefab = prefab,
                prefabPath = prefab != null ? AssetDatabase.GetAssetPath(prefab) : string.Empty
            });

            entry.FindPropertyRelative("count").intValue = 1;
        }

        SerializedProperty generateOnStart = serialized.FindProperty("generateOnStart");
        if (generateOnStart != null)
        {
            generateOnStart.boolValue = false;
        }

        serialized.ApplyModifiedPropertiesWithoutUndo();
        report.Add("HangarTest", "entry configuration", allNamesAndPlacementsMatch,
            allNamesAndPlacementsMatch
                ? "All nine expected base entries and placement types found; temporary test counts set to 1 each."
                : "Expected nine entries with the audited Land/River/Rail placement assignments.");

        return configurations;
    }

    private static Dictionary<string, bool> VerifyBuildRequestPrefabs(
        BaseSpawner spawner,
        List<VariantConfiguration> configurations,
        ValidationReport report)
    {
        MethodInfo buildRequests = typeof(BaseSpawner).GetMethod(
            "BuildRequests",
            PrivateInstance);
        IList requests = buildRequests?.Invoke(spawner, null) as IList;
        Dictionary<string, bool> result = new Dictionary<string, bool>();

        if (requests == null)
        {
            report.Add("All variants", "BuildRequests mapping", false,
                "Could not inspect BaseSpawner.BuildRequests().");
            return result;
        }

        foreach (VariantConfiguration configuration in configurations)
        {
            object[] matching = requests.Cast<object>()
                .Where(request => ReadField<string>(request, "baseName") == configuration.name)
                .ToArray();

            bool matches = matching.Length == 1;
            if (matches)
            {
                object request = matching[0];
                GameObject requestPrefab = ReadField<GameObject>(request, "prefab");
                int requestPlacement = Convert.ToInt32(
                    ReadField<object>(request, "placement"));
                int requestIndex = ReadField<int>(request, "index");
                ExpectedVariant expected =
                    ExpectedVariants.First(item => item.name == configuration.name);

                matches = requestPrefab == configuration.prefab &&
                          requestPlacement == expected.placement &&
                          requestIndex == 1;
            }

            result[configuration.name] = matches;
            report.Add(
                configuration.name,
                "BuildRequests prefab mapping",
                matches,
                matches
                    ? $"One request uses {configuration.prefabPath}."
                    : $"Request count/prefab/placement mismatch; found {matching.Length} request(s).");
        }

        report.Add("All variants", "BuildRequests total", requests.Count == ExpectedVariants.Length,
            $"Built {requests.Count} requests; expected {ExpectedVariants.Length} for isolated validation.");
        return result;
    }

    private static bool PrefabHierarchyMatches(
        GameObject instance,
        GameObject expectedPrefab)
    {
        if (instance == null || expectedPrefab == null)
        {
            return false;
        }

        StringBuilder instanceSignature = new StringBuilder();
        StringBuilder prefabSignature = new StringBuilder();
        AppendHierarchySignature(instance.transform, instanceSignature, true);
        AppendHierarchySignature(expectedPrefab.transform, prefabSignature, true);
        return instanceSignature.ToString() == prefabSignature.ToString();
    }

    private static void AppendHierarchySignature(
        Transform current,
        StringBuilder signature,
        bool isRoot)
    {
        if (!isRoot)
        {
            signature.Append("GO:").Append(current.name)
                .Append('|').Append(current.gameObject.activeSelf)
                .Append('|').Append(FormatVector(current.localPosition))
                .Append('|').Append(FormatQuaternion(current.localRotation))
                .Append('|').Append(FormatVector(current.localScale)).Append(';');
        }

        foreach (Component component in current.GetComponents<Component>())
        {
            if (component == null || component is Transform ||
                component is BaseController)
            {
                continue;
            }

            signature.Append("C:").Append(component.GetType().FullName);

            if (component is MeshFilter meshFilter && meshFilter.sharedMesh != null)
            {
                signature.Append("|M:")
                    .Append(AssetDatabase.GetAssetPath(meshFilter.sharedMesh))
                    .Append('|').Append(meshFilter.sharedMesh.name);
            }

            if (component is Renderer renderer)
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    signature.Append("|Mat:")
                        .Append(material != null
                            ? AssetDatabase.GetAssetPath(material) + ":" + material.name
                            : "null");
                }
            }

            if (component is MeshCollider meshCollider && meshCollider.sharedMesh != null)
            {
                signature.Append("|ColliderMesh:")
                    .Append(AssetDatabase.GetAssetPath(meshCollider.sharedMesh))
                    .Append('|').Append(meshCollider.sharedMesh.name);
            }

            signature.Append(';');
        }

        for (int i = 0; i < current.childCount; i++)
        {
            AppendHierarchySignature(current.GetChild(i), signature, false);
        }
    }

    private static string FormatVector(Vector3 value)
    {
        return $"{value.x.ToString("R", CultureInfo.InvariantCulture)}," +
               $"{value.y.ToString("R", CultureInfo.InvariantCulture)}," +
               $"{value.z.ToString("R", CultureInfo.InvariantCulture)}";
    }

    private static string FormatQuaternion(Quaternion value)
    {
        return $"{value.x.ToString("R", CultureInfo.InvariantCulture)}," +
               $"{value.y.ToString("R", CultureInfo.InvariantCulture)}," +
               $"{value.z.ToString("R", CultureInfo.InvariantCulture)}," +
               $"{value.w.ToString("R", CultureInfo.InvariantCulture)}";
    }

    private static void VerifyNoOrphanHangars(
        Scene scene,
        BaseSpawner spawner,
        ValidationReport report,
        int expectedNaval,
        int expectedRail,
        SceneBaseline baseline)
    {
        int naval = 0;
        int rail = 0;
        bool allUnderGeneratedRoot = true;
        List<string> invalidPaths = new List<string>();
        HashSet<GameObject> expectedHangarObjects = new HashSet<GameObject>();
        HashSet<GameObject> expectedControllerObjects = new HashSet<GameObject>();

        for (int i = 0; i < spawner.transform.childCount; i++)
        {
            Transform child = spawner.transform.GetChild(i);
            if (baseline.hangars.Contains(child.gameObject))
            {
                continue;
            }

            if (!child.name.StartsWith("GeneratedBase_", StringComparison.Ordinal))
            {
                if (child.name == "Naval Hangar" || child.name == "Rail Hangar")
                {
                    allUnderGeneratedRoot = false;
                    invalidPaths.Add(child.name);
                }

                continue;
            }

            for (int childIndex = 0; childIndex < child.childCount; childIndex++)
            {
                Transform pairChild = child.GetChild(childIndex);
                if (pairChild.name == "Naval Hangar")
                {
                    naval++;
                    foreach (Transform hangarObject in Traverse(pairChild))
                    {
                        expectedHangarObjects.Add(hangarObject.gameObject);
                    }
                }
                else if (pairChild.name == "Rail Hangar")
                {
                    rail++;
                    foreach (Transform hangarObject in Traverse(pairChild))
                    {
                        expectedHangarObjects.Add(hangarObject.gameObject);
                    }
                }

                bool isMainBase = pairChild.name.EndsWith(
                    " Main Base",
                    StringComparison.Ordinal);
                if (isMainBase &&
                    (FindDescendant(pairChild, "Naval Hangar") != null ||
                     FindDescendant(pairChild, "Rail Hangar") != null))
                {
                    allUnderGeneratedRoot = false;
                    invalidPaths.Add($"{child.name}/{pairChild.name}/embedded-hangar");
                }
            }

            Transform mainBaseTransform = null;
            for (int childIndex = 0; childIndex < child.childCount; childIndex++)
            {
                Transform pairChild = child.GetChild(childIndex);
                if (pairChild.name.EndsWith(" Main Base", StringComparison.Ordinal))
                {
                    mainBaseTransform = pairChild;
                    break;
                }
            }

            if (mainBaseTransform == null)
            {
                // Land bases use the GeneratedBase object as the main base.
                mainBaseTransform = child;
            }

            if (mainBaseTransform.GetComponent<BaseController>() != null)
            {
                expectedControllerObjects.Add(mainBaseTransform.gameObject);
            }
        }

        int sceneOrphanHangars = 0;
        int sceneUnexpectedControllers = 0;
        foreach (GameObject sceneRoot in scene.GetRootGameObjects())
        {
            foreach (Transform item in Traverse(sceneRoot.transform))
            {
                bool isHangarName = item.name == "Naval Hangar" ||
                                    item.name == "Rail Hangar";
                if (isHangarName &&
                    !baseline.hangars.Contains(item.gameObject) &&
                    !expectedHangarObjects.Contains(item.gameObject))
                {
                    sceneOrphanHangars++;
                    invalidPaths.Add(GetHierarchyPath(item));
                }

                BaseController[] controllers =
                    item.GetComponents<BaseController>();
                int baselineCount = baseline.controllerCounts.TryGetValue(
                    item.gameObject,
                    out int count) ? count : 0;
                int newControllerCount = controllers.Length - baselineCount;

                if (newControllerCount > 0 &&
                    (!expectedControllerObjects.Contains(item.gameObject) ||
                     controllers.Length != baselineCount + 1))
                {
                    sceneUnexpectedControllers += newControllerCount;
                }
            }
        }

        allUnderGeneratedRoot &= sceneOrphanHangars == 0 &&
                                 sceneUnexpectedControllers == 0;

        report.Add(
            "All variants",
            "orphan / duplicate hangars",
            allUnderGeneratedRoot && naval == expectedNaval && rail == expectedRail,
            $"Naval hangars={naval}/{expectedNaval}, rail hangars={rail}/{expectedRail}, " +
            $"new scene orphans={sceneOrphanHangars}, unexpected controllers={sceneUnexpectedControllers}, " +
            $"all generated objects owned={allUnderGeneratedRoot}; invalid paths={string.Join(" | ", invalidPaths)}.");
    }

    private static SceneBaseline CaptureSceneBaseline(Scene scene)
    {
        SceneBaseline baseline = new SceneBaseline();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform item in Traverse(root.transform))
            {
                if (item.name == "Naval Hangar" || item.name == "Rail Hangar")
                {
                    baseline.hangars.Add(item.gameObject);
                }

                int controllerCount = item.GetComponents<BaseController>().Length;
                if (controllerCount > 0)
                {
                    baseline.controllerCounts[item.gameObject] = controllerCount;
                }
            }
        }

        return baseline;
    }

    private static string GetHierarchyPath(Transform transform)
    {
        List<string> segments = new List<string>();
        for (Transform current = transform; current != null; current = current.parent)
        {
            segments.Add(current.name);
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static void VerifyUniformRiverWidth(
        Scene scene,
        ValidationReport report)
    {
        RiverGenerator river = FindComponentInScene<RiverGenerator>(scene);
        if (river == null)
        {
            report.Add("Naval variants", "no local river narrowing", false,
                "No RiverGenerator in validation scene.");
            report.Add("Naval variants", "uniform river mesh width", null,
                "No RiverGenerator mesh is available to measure.");
            return;
        }

        IList narrowings = GetPrivateField(river, "localNarrowings") as IList;
        bool noLocalNarrowings = narrowings != null && narrowings.Count == 0;
        report.Add("Naval variants", "no local river narrowing", noLocalNarrowings,
            $"Registered local profiles={narrowings?.Count ?? -1} (required 0).");

        MeshFilter riverFilter = river.GetComponent<MeshFilter>();
        Mesh mesh = riverFilter != null ? riverFilter.sharedMesh : null;
        int routeCount = river.GeneratedRoute != null
            ? river.GeneratedRoute.Count
            : 0;
        if (mesh == null || routeCount < 3)
        {
            report.Add("Naval variants", "uniform river mesh width", null,
                "Generated MeshFilter mesh or centerline route is unavailable.");
            return;
        }

        if (mesh.vertexCount != routeCount * 2)
        {
            report.Add("Naval variants", "uniform river mesh width", null,
                $"Mesh has {mesh.vertexCount} vertices for {routeCount} route points; expected two vertices per route sample.");
            return;
        }

        Vector3[] vertices = mesh.vertices;
        float maximumWidthError = 0f;
        float minimumMeasuredWidth = float.MaxValue;
        float maximumMeasuredWidth = 0f;
        for (int i = 0; i < routeCount; i++)
        {
            Vector3 left = river.transform.TransformPoint(vertices[i * 2]);
            Vector3 right = river.transform.TransformPoint(vertices[i * 2 + 1]);
            float measuredWidth = Vector3.Distance(left, right);
            minimumMeasuredWidth = Mathf.Min(minimumMeasuredWidth, measuredWidth);
            maximumMeasuredWidth = Mathf.Max(maximumMeasuredWidth, measuredWidth);
            maximumWidthError = Mathf.Max(
                maximumWidthError,
                Mathf.Abs(measuredWidth - river.RiverWidth));
        }

        bool uniform = noLocalNarrowings && maximumWidthError <= 0.01f;
        report.Add("Naval variants", "uniform river mesh width", uniform,
            $"measured width range {minimumMeasuredWidth:F3}–{maximumMeasuredWidth:F3} m; " +
            $"configured width {river.RiverWidth:F3} m; max error {maximumWidthError:F4} m (tolerance 0.010 m).");
    }

    private static void VerifyPairwiseSpacing(
        BaseSpawner spawner,
        List<GameObject> generatedRoots,
        List<VariantConfiguration> configurations,
        Dictionary<string, object> validationByRoot,
        ValidationReport report)
    {
        float landSpacing = GetPrivateField<float>(spawner, "minSpacing");
        float routeSpacing = GetPrivateField<float>(spawner, "routeBaseMinSpacing");
        float pivotTolerance = 0.05f;
        List<SpawnedFootprint> footprints = new List<SpawnedFootprint>();
        bool boundsMeasured = true;
        bool allRequestsSpawned = generatedRoots.Count == configurations.Count;

        foreach (GameObject root in generatedRoots)
        {
            VariantConfiguration configuration = configurations.FirstOrDefault(item =>
                root.name == $"GeneratedBase_{item.name}_001");
            if (configuration == null)
            {
                boundsMeasured = false;
                continue;
            }

            GameObject mainBase = configuration.placement == 0
                ? root
                : FindDirectChild(
                    root.transform,
                    $"{configuration.name} Main Base")?.gameObject;

            if (mainBase == null || !TryGetRendererBounds(mainBase, out Bounds bounds))
            {
                boundsMeasured = false;
                continue;
            }

            footprints.Add(new SpawnedFootprint
            {
                name = configuration.name,
                placement = configuration.placement,
                pivot = mainBase.transform.position,
                bounds = bounds,
                placementValidation = validationByRoot.TryGetValue(
                    root.name,
                    out object foundValidation)
                        ? foundValidation
                        : null
            });
        }

        bool pivotSpacingValid = allRequestsSpawned &&
                                 footprints.Count == configurations.Count;
        float minimumPivotDistance = float.MaxValue;
        List<string> pivotViolations = new List<string>();
        int oppositeShoreNavalPairs = 0;

        for (int later = 1; later < footprints.Count; later++)
        {
            float required = footprints[later].placement == 0
                ? landSpacing
                : routeSpacing;

            for (int earlier = 0; earlier < later; earlier++)
            {
                bool oppositeShoreNavalPair =
                    footprints[later].placement == 1 &&
                    footprints[earlier].placement == 1 &&
                    AreOppositeRiverBanks(
                        footprints[later].placementValidation,
                        footprints[earlier].placementValidation);

                float distance = Vector2.Distance(
                    new Vector2(footprints[later].pivot.x, footprints[later].pivot.z),
                    new Vector2(footprints[earlier].pivot.x, footprints[earlier].pivot.z));

                // Only track minimum distance for pairs checked against spacing rule.
                if (!oppositeShoreNavalPair)
                {
                    minimumPivotDistance = Mathf.Min(minimumPivotDistance, distance);
                }

                if (oppositeShoreNavalPair)
                {
                    oppositeShoreNavalPairs++;
                    continue;
                }

                if (distance + pivotTolerance < required)
                {
                    pivotSpacingValid = false;
                    pivotViolations.Add(
                        $"{footprints[earlier].name}/{footprints[later].name} " +
                        $"{distance:F2}m < {required:F2}m");
                }
            }
        }

        report.Add(
            "All variants",
            "base pivot spacing",
            pivotSpacingValid,
                footprints.Count < 2
                    ? "Fewer than two measurable bases."
                : $"Minimum pivot separation {minimumPivotDistance:F2} m; " +
                  $"same-shore Naval and other pairs checked against the configured spacing " +
                  $"with {pivotTolerance:F2} m surface-offset tolerance; " +
                  $"opposite-shore Naval pairs exempted={oppositeShoreNavalPairs} " +
                  "(renderer overlap checked separately). " +
                  (pivotViolations.Count == 0
                      ? "No violations."
                      : string.Join("; ", pivotViolations)));

        if (!boundsMeasured || footprints.Count != configurations.Count)
        {
            report.Add("All variants", "renderer footprint spacing", null,
                "At least one main-base renderer bound could not be measured.");
            return;
        }

        int overlaps = 0;
        float minimumAabbGap = float.MaxValue;
        List<string> overlapPairs = new List<string>();
        List<string> smallGaps = new List<string>();
        float smallGapWarning = GetPrivateField<float>(
            spawner,
            "railBaseHangarClearance");

        for (int a = 0; a < footprints.Count; a++)
        {
            for (int b = a + 1; b < footprints.Count; b++)
            {
                float gap = HorizontalAabbGap(
                    footprints[a].bounds,
                    footprints[b].bounds,
                    out bool intersects);
                minimumAabbGap = Mathf.Min(minimumAabbGap, gap);

                if (intersects)
                {
                    overlaps++;
                    overlapPairs.Add($"{footprints[a].name}/{footprints[b].name}");
                }
                else if (gap < smallGapWarning)
                {
                    smallGaps.Add(
                        $"{footprints[a].name}/{footprints[b].name}={gap:F2}m");
                }
            }
        }

        report.Add(
            "All variants",
            "renderer footprint spacing",
            overlaps == 0 && smallGaps.Count == 0,
            $"Renderer XZ AABB overlaps={overlaps} (conservative, not physics); " +
            $"minimum AABB gap={minimumAabbGap:F2} m; " +
            $"gaps below {smallGapWarning:F2} m diagnostic threshold={smallGaps.Count}" +
            (smallGaps.Count == 0 ? "." : $": {string.Join(", ", smallGaps)}") +
            (overlapPairs.Count == 0 ? string.Empty : $"; overlapping AABBs: {string.Join(", ", overlapPairs)}."));
    }

    private static float HorizontalAabbGap(
        Bounds a,
        Bounds b,
        out bool intersects)
    {
        float gapX = Mathf.Max(
            0f,
            Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
        float gapZ = Mathf.Max(
            0f,
            Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));

        intersects = gapX <= 0.0001f && gapZ <= 0.0001f;
        return Mathf.Sqrt(gapX * gapX + gapZ * gapZ);
    }

    private static void RunPairRollbackTest(
        Scene scene,
        ValidationReport report)
    {
        BaseSpawner spawner = FindSpawner(scene);
        if (spawner == null)
        {
            report.Add("Rollback", "failed pair cleanup", false,
                "No BaseSpawner in temporary scene.");
            return;
        }

        InvokePrivate(spawner, "ClearRegisteredRiverNarrowings");
        InvokePrivate(spawner, "ClearGeneratedBases");
        ClearPrivateList(spawner, "placedPositions");
        ClearPrivateList(spawner, "placedRouteHangarPositions");
        ClearPrivateList(spawner, "lastPlacements");

        int rootsBefore = CountGeneratedRoots(spawner);
        int controllersBefore = CountSceneComponents<BaseController>(scene);
        int narrowingsBefore = CountPrivateList(
            FindComponentInScene<RiverGenerator>(scene),
            "localNarrowings");
        GameObject originalHangar = GetPrivateField<GameObject>(
            spawner,
            "navalHangarPrefab");

        bool failedAsExpected = false;
        string reason = string.Empty;

        try
        {
            SetPrivateField(spawner, "navalHangarPrefab", null);
            object request = CreatePrivateNestedObject(typeof(BaseSpawner), "BaseRequest");
            SetField(request, "baseName", "Naval");
            SetField(request, "prefab", GetConfiguredPrefab(spawner, "Naval"));
            SetField(request, "placement", Enum.ToObject(
                typeof(BaseSpawner).GetNestedType("PlacementType", BindingFlags.NonPublic),
                1));
            SetField(request, "index", 1);

            object validation = Activator.CreateInstance(
                typeof(BaseSpawner).GetNestedType("PlacementValidation", BindingFlags.NonPublic));
            SetField(validation, "valid", true);
            SetField(validation, "hangarRootPosition", Vector3.zero);
            SetField(validation, "hangarRootRotation", Quaternion.identity);
            SetField(validation, "hangarRootScale", 1f);
            SetField(validation, "localRiverWidth", 0.2f);
            SetField(validation, "riverNarrowingTaperHalfDistance", 2.25f);

            MethodInfo spawnMethod = typeof(BaseSpawner).GetMethod(
                "TryInstantiateRequest",
                PrivateInstance);
            object[] args =
            {
                request,
                new Pose(Vector3.zero, Quaternion.identity),
                validation,
                null
            };

            bool spawned = (bool)spawnMethod.Invoke(spawner, args);
            failedAsExpected = !spawned;
            reason = args[3] as string ?? string.Empty;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
        }
        finally
        {
            SetPrivateField(spawner, "navalHangarPrefab", originalHangar);
        }

        int rootsAfter = CountGeneratedRoots(spawner);
        int controllersAfter = CountSceneComponents<BaseController>(scene);
        int narrowingsAfter = CountPrivateList(
            FindComponentInScene<RiverGenerator>(scene),
            "localNarrowings");
        int placedAfter = CountPrivateList(spawner, "placedPositions");
        int routeHangarsAfter = CountPrivateList(spawner, "placedRouteHangarPositions");
        int diagnosticsAfter = CountPrivateList(spawner, "lastPlacements");

        bool clean = failedAsExpected &&
                     rootsAfter == rootsBefore &&
                     controllersAfter == controllersBefore &&
                     narrowingsAfter == narrowingsBefore &&
                     placedAfter == 0 &&
                     routeHangarsAfter == 0 &&
                     diagnosticsAfter == 0;

        report.Add("Rollback", "failed pair cleanup", clean,
            $"spawn result failed={failedAsExpected} ({reason}); generated roots {rootsBefore}->{rootsAfter}, " +
            $"controllers {controllersBefore}->{controllersAfter}, river narrowings {narrowingsBefore}->{narrowingsAfter}, " +
            $"bookkeeping {placedAfter}/{routeHangarsAfter}/{diagnosticsAfter}.");
        report.Add("Rollback", "Play Mode cleanup", null,
            "Runner executes in Edit Mode on a disposable scene copy; runtime deferred-Destroy timing was not exercised.");
    }

    private static bool TrySampleTerrain(
        BaseSpawner spawner,
        Vector3 position,
        float radius,
        int count,
        out float heightVariation,
        out float maxSlope)
    {
        heightVariation = float.PositiveInfinity;
        maxSlope = float.PositiveInfinity;

        MethodInfo method = typeof(BaseSpawner).GetMethod(
            "SampleTerrainFootprint",
            PrivateInstance);
        object[] args =
        {
            new Vector3(position.x, 0f, position.z),
            radius,
            count,
            null
        };

        bool sampled = (bool)method.Invoke(spawner, args);
        if (!sampled || args[3] == null)
        {
            return false;
        }

        heightVariation = ReadField<float>(args[3], "heightVariation");
        maxSlope = ReadField<float>(args[3], "maxSlopeAngle");
        return ReadField<bool>(args[3], "isValid");
    }

    private static bool TryCheckTurretFacing(
        GameObject mainBase,
        GameObject hangar,
        out float dot)
    {
        dot = -1f;
        if (mainBase == null || hangar == null ||
            !TryGetRendererBounds(mainBase, out Bounds mainBounds) ||
            !TryGetRendererBounds(hangar, out Bounds hangarBounds))
        {
            return false;
        }

        Transform turretGroup = FindDescendant(mainBase.transform, "Base Flaks");
        if (turretGroup == null)
        {
            return false;
        }

        Vector3 turretFacing = turretGroup.position - mainBase.transform.position;
        turretFacing.y = 0f;
        Vector3 toHangar = hangarBounds.center - mainBounds.center;
        toHangar.y = 0f;

        if (turretFacing.sqrMagnitude < 0.0001f ||
            toHangar.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        dot = Vector3.Dot(turretFacing.normalized, toHangar.normalized);
        return dot >= 0.75f;
    }

    private static bool TryMeasureRailHangarAxis(
        GameObject hangar,
        Vector3 routeTangent,
        out float angleDegrees,
        out string details)
    {
        angleDegrees = float.PositiveInfinity;
        details = string.Empty;

        if (hangar == null || routeTangent.sqrMagnitude < 0.0001f)
        {
            details = "Hangar or route tangent is unavailable.";
            return false;
        }

        if (!TryMeasureHangarLongAxis(
            hangar,
            out Vector3 longestAxisInHangarRoot,
            out float longestHorizontalExtent,
            out details))
        {
            return false;
        }

        Vector3 worldAxis = hangar.transform.TransformDirection(
            longestAxisInHangarRoot).normalized;
        float signedAxisAngle = Vector3.Angle(worldAxis, routeTangent.normalized);
        angleDegrees = Mathf.Min(signedAxisAngle, 180f - signedAxisAngle);
        details =
            $"mesh long axis vs local route tangent {angleDegrees:F2} deg (tolerance 5.00 deg); " +
            $"measured local axis {longestAxisInHangarRoot}, extent {longestHorizontalExtent:F3} m.";
        return true;
    }

    private static bool TryMeasureHangarLongAxis(
        GameObject hangar,
        out Vector3 longestAxisInHangarRoot,
        out float longestHorizontalExtent,
        out string details)
    {
        longestAxisInHangarRoot = Vector3.zero;
        longestHorizontalExtent = 0f;
        details = string.Empty;

        if (hangar == null)
        {
            details = "Hangar object is unavailable.";
            return false;
        }

        MeshFilter[] filters = hangar.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter filter in filters)
        {
            if (filter == null || filter.sharedMesh == null)
            {
                continue;
            }

            Vector3 meshSize = filter.sharedMesh.bounds.size;
            Vector3[] meshAxes =
            {
                Vector3.right,
                Vector3.up,
                Vector3.forward
            };
            float[] extents = { meshSize.x, meshSize.y, meshSize.z };

            for (int i = 0; i < meshAxes.Length; i++)
            {
                Vector3 rootAxis = hangar.transform.InverseTransformVector(
                    filter.transform.TransformVector(meshAxes[i]));
                Vector3 horizontalAxis = new Vector3(rootAxis.x, 0f, rootAxis.z);
                float horizontalExtent = extents[i] * horizontalAxis.magnitude;

                if (horizontalExtent <= longestHorizontalExtent ||
                    horizontalAxis.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                longestHorizontalExtent = horizontalExtent;
                longestAxisInHangarRoot = horizontalAxis.normalized;
            }
        }

        if (longestHorizontalExtent <= 0.001f)
        {
            details = "No measurable horizontal MeshFilter bounds; axis alignment cannot be checked.";
            return false;
        }

        details = $"measured long-axis extent {longestHorizontalExtent:F3} m.";
        return true;
    }

    private static bool TryGetRendererBounds(GameObject gameObject, out Bounds bounds)
    {
        bounds = new Bounds();
        Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
        bool found = false;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
            {
                continue;
            }

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    private static string BuildExpectedBaseId(string baseName, int index)
    {
        string typeId = new string(
            baseName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        return $"BASE_{typeId}_{index:000}";
    }

    private static BaseSpawner FindSpawner(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            BaseSpawner spawner = root.GetComponentInChildren<BaseSpawner>(true);
            if (spawner != null)
            {
                return spawner;
            }
        }

        return null;
    }

    private static T FindComponentInScene<T>(Scene scene) where T : Component
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            T component = root.GetComponentInChildren<T>(true);
            if (component != null)
            {
                return component;
            }
        }

        return null;
    }

    private static Transform FindDirectChild(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
            {
                return child;
            }
        }

        return null;
    }

    private static Transform FindDescendant(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
            {
                return child;
            }

            Transform nested = FindDescendant(child, name);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<Transform> Traverse(Transform root)
    {
        yield return root;
        for (int i = 0; i < root.childCount; i++)
        {
            foreach (Transform child in Traverse(root.GetChild(i)))
            {
                yield return child;
            }
        }
    }

    private static int CountGeneratedRoots(BaseSpawner spawner)
    {
        int count = 0;
        for (int i = 0; i < spawner.transform.childCount; i++)
        {
            if (spawner.transform.GetChild(i).name.StartsWith(
                    "GeneratedBase_",
                    StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static GameObject GetConfiguredPrefab(
        BaseSpawner spawner,
        string baseName)
    {
        SerializedObject serialized = new SerializedObject(spawner);
        SerializedProperty entries = serialized.FindProperty("baseEntries");
        if (entries == null || !entries.isArray)
        {
            return null;
        }

        for (int i = 0; i < entries.arraySize; i++)
        {
            SerializedProperty entry = entries.GetArrayElementAtIndex(i);
            if (entry.FindPropertyRelative("baseName").stringValue == baseName)
            {
                return entry.FindPropertyRelative("prefab").objectReferenceValue as GameObject;
            }
        }

        return null;
    }

    private static int CountSceneComponents<T>(Scene scene) where T : Component
    {
        int count = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            count += root.GetComponentsInChildren<T>(true).Length;
        }

        return count;
    }

    private static int CountPrivateList(object target, string fieldName)
    {
        if (target == null)
        {
            return -1;
        }

        return (GetPrivateField(target, fieldName) as IList)?.Count ?? -1;
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, PrivateInstance);
        if (field == null)
        {
            throw new MissingFieldException(target.GetType().Name, fieldName);
        }

        return field.GetValue(target);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        return (T)GetPrivateField(target, fieldName);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        SetField(target, fieldName, value);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new MissingFieldException(target.GetType().Name, fieldName);
        }

        field.SetValue(target, value);
    }

    private static T ReadValidation<T>(object validation, string fieldName)
    {
        return ReadField<T>(validation, fieldName);
    }

    private static T ReadField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new MissingFieldException(target.GetType().Name, fieldName);
        }

        return (T)field.GetValue(target);
    }

    private static void ClearPrivateList(object target, string fieldName)
    {
        (GetPrivateField(target, fieldName) as IList)?.Clear();
    }

    private static void InvokePrivate(object target, string methodName)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, PrivateInstance);
        if (method == null)
        {
            throw new MissingMethodException(target.GetType().Name, methodName);
        }

        method.Invoke(target, null);
    }

    private static object CreatePrivateNestedObject(Type parentType, string nestedName)
    {
        Type nestedType = parentType.GetNestedType(nestedName, BindingFlags.NonPublic);
        if (nestedType == null)
        {
            throw new MissingMemberException(parentType.Name, nestedName);
        }

        return Activator.CreateInstance(nestedType, true);
    }

    static int GetRiverBankSideSign(Vector3 footprintCenter, Vector3 routePoint, Vector3 routeTangent)
    {
        Vector3 tangent = routeTangent;
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.0001f)
        {
            return 0;
        }
        tangent.Normalize();
        Vector3 localBankNormal = Vector3.Cross(Vector3.up, tangent).normalized;
        float signedOffset = Vector3.Dot(footprintCenter - routePoint, localBankNormal);

        if (Mathf.Abs(signedOffset) <= 0.001f)
        {
            return 0;
        }
        return signedOffset > 0f ? 1 : -1;
    }

    static bool AreOppositeRiverBanks(object validationA, object validationB)
    {
        // Read fields via the runner's reflection-based reader rather than
        // casting to the private PlacementValidation struct (nested inside
        // BaseSpawner and not accessible from this module).
        var vaCenter = (Vector3)(ReadField<Vector3>(validationA, "navalBaseFootprintCenter"));
        var vaPoint   = (Vector3)(ReadField<Vector3>(validationA, "routePoint"));
        var vaTangent = (Vector3)(ReadField<Vector3>(validationA, "routeTangent"));

        var vbCenter = (Vector3)(ReadField<Vector3>(validationB, "navalBaseFootprintCenter"));
        var vbPoint   = (Vector3)(ReadField<Vector3>(validationB, "routePoint"));
        var vbTangent = (Vector3)(ReadField<Vector3>(validationB, "routeTangent"));

        int bankA = GetRiverBankSideSign(vaCenter, vaPoint, vaTangent);
        int bankB = GetRiverBankSideSign(vbCenter, vbPoint, vbTangent);

        return bankA != 0 && bankB != 0 && bankA != bankB;
    }
}
