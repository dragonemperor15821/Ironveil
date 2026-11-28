using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>
    /// Stage 8 / 8B in the REAL generated gameplay scenes: the map has the 350 standard bases (unchanged distribution
    /// and placement) plus ONE additional Tank Base designated as the player's Home Base; the Home Base contributes
    /// storage to the session's one shared ResourceManager; the HUD follows; regeneration keeps the standard bases
    /// identical and re-creates exactly one Home Base.
    /// </summary>
    public class StorageExpansionPlayModeTests
    {
        private const float GenerationTimeoutSeconds = 300f;
        private const string WorldExplorerPath = "Assets/Scenes/Ironveil - World Explorer.unity";

        private static readonly Dictionary<string, int> Distribution = new Dictionary<string, int>
        {
            { "Tank", 110 }, { "Heli", 90 }, { "Air", 80 }, { "Naval", 25 }, { "Rail", 17 },
            { "Heli Naval", 11 }, { "Heli Rail", 7 }, { "Air Naval", 6 }, { "Air Rail", 4 },
        };

        private struct Snapshot
        {
            public Vector3 position;
            public float yaw;
            public string level;
        }

        // ------------------------------------------------------------ helpers

        private static IEnumerator LoadAndGenerate(string path, bool? forceHomeBase = null)
        {
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
            float start = Time.realtimeSinceStartup;
            if (forceHomeBase.HasValue)
            {
                // Switch the map's Home Base before the world (and its bases) is generated.
                BaseSpawner early = null;
                while (early == null || early.gameObject.scene != scene)
                {
                    early = scene.isLoaded ? Object.FindAnyObjectByType<BaseSpawner>() : null;
                    if (Time.realtimeSinceStartup - start > GenerationTimeoutSeconds) Assert.Fail(path + ": no BaseSpawner.");
                    if (early == null || early.gameObject.scene != scene) yield return null;
                }
                SpawnHomeField.SetValue(early, forceHomeBase.Value);
            }

            // Stage 9: the FINAL world (World Explorer replaces the initial generation with its startup seed).
            yield return Ironveil.WorldGeneration.Tests.WorldGenerationTestUtil.WaitForFinalWorld(scene, path);
        }

        private static IEnumerator WaitForInitialState(BaseController b)
        {
            float start = Time.realtimeSinceStartup;
            while (true)
            {
                BaseFlakDefence d = b != null ? b.GetComponent<BaseFlakDefence>() : null;
                if (d != null && d.InitialStateApplied) break;
                if (Time.realtimeSinceStartup - start > 60f) Assert.Fail("the Home Base was never initialised (2 x L1 Flak + L1 Factory / Refinery)");
                yield return null;
            }
        }

        private static List<Transform> StandardRoots(BaseSpawner spawner) =>
            spawner.transform.Cast<Transform>()
                .Where(t => t.gameObject.activeInHierarchy && t.name.StartsWith("GeneratedBase_") && t.name != BaseSpawner.HomeBaseObjectName)
                .ToList();

        private static string VariantOf(Transform root)
        {
            string rest = root.name.Substring("GeneratedBase_".Length);
            return rest.Substring(0, rest.LastIndexOf('_'));
        }

        private static Dictionary<string, Snapshot> Capture(BaseSpawner spawner)
        {
            var result = new Dictionary<string, Snapshot>();
            foreach (Transform root in StandardRoots(spawner))
            {
                BaseController c = root.GetComponentInChildren<BaseController>(true);
                BaseLevelInfo level = c.GetComponent<BaseLevelInfo>();
                result.Add(c.BaseId, new Snapshot
                {
                    position = c.transform.position,
                    yaw = c.transform.eulerAngles.y,
                    level = level != null ? level.Level.ToString() : "-",
                });
            }
            return result;
        }

        private static void AssertSameStandardBases(Dictionary<string, Snapshot> a, Dictionary<string, Snapshot> b, string message)
        {
            Assert.AreEqual(350, a.Count, message);
            Assert.AreEqual(350, b.Count, message);
            CollectionAssert.AreEquivalent(a.Keys, b.Keys, message + ": IDs");
            foreach (var kv in a)
            {
                Snapshot o = b[kv.Key];
                Assert.Less((kv.Value.position - o.position).sqrMagnitude, 1e-8f, $"{message}: {kv.Key} position");
                Assert.AreEqual(kv.Value.yaw, o.yaw, 1e-4f, $"{message}: {kv.Key} yaw");
                Assert.AreEqual(kv.Value.level, o.level, $"{message}: {kv.Key} level");
            }
        }

        private static List<BaseController> AllBases(BaseSpawner spawner) =>
            spawner.GetComponentsInChildren<BaseController>(false).ToList();

        private static string[] MeshSignature(BaseController b) =>
            b.GetComponentsInChildren<MeshFilter>(true)
                .Select(f => f.sharedMesh != null ? f.sharedMesh.name : "-")
                .OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

        private static long Cap(ResourceManager m, ResourceType r)
        {
            Assert.IsTrue(m.TryGetCapacity(r, out long v));
            return v;
        }

        private static void SetRefinery(BaseController c, int level)
        {
            var settings = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath);
            BuildingSlot slot = c.GetComponent<BaseBuildingState>().Slots.First(s => s != null && s.Accepts(BuildingType.FactoryRefinery));
            BuildingPlacement.Remove(slot);
            if (level > 0) Assert.IsNotNull(BuildingPlacement.Place(slot, settings.BuildingCatalog.Get(BuildingType.FactoryRefinery), level));
        }

        private static readonly FieldInfo SpawnHomeField =
            typeof(BaseSpawner).GetField("spawnPlayerHomeBase", BindingFlags.Instance | BindingFlags.NonPublic);

        // ------------------------------------------------------------ World Explorer

        [UnityTest]
        public IEnumerator WorldExplorer_GeneratedMap_350StandardPlusOneHomeBase_StorageAndRegeneration()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            BaseSpawner spawner = null;
            try
            {
                yield return LoadAndGenerate(WorldExplorerPath);
                Time.timeScale = 0f;
                spawner = Object.FindAnyObjectByType<BaseSpawner>();
                WorldGenerationPrototype proto = Object.FindAnyObjectByType<WorldGenerationPrototype>();
                ResourceManager manager = ResourceManager.Instance;
                Assert.IsTrue(spawner.SpawnsPlayerHomeBase, "World Explorer's map has the player's Home Base");

                // ---- Map structure: 350 standard + 1 Home Base = 351.
                List<Transform> standard = StandardRoots(spawner);
                Assert.AreEqual(350, standard.Count, "exactly 350 standard bases");
                foreach (var kv in Distribution)
                    Assert.AreEqual(kv.Value, standard.Count(r => VariantOf(r) == kv.Key), kv.Key + " count unchanged");
                Assert.AreEqual(351, AllBases(spawner).Count, "351 bases in total");

                BaseController home = spawner.LastHomeBase;
                Assert.IsNotNull(home, "Home Base placed: " + spawner.LastHomeBaseFailure);
                Assert.AreSame(home, PlayerHomeBase.Current);
                Assert.AreEqual(BaseSpawner.HomeBaseObjectName, home.name);
                Assert.AreEqual(PlayerHomeBase.HomeBaseId, home.BaseId);
                Assert.AreEqual(BaseController.PlayerOwnerId, home.OwnerId);
                Assert.AreEqual("Tank", home.GetComponent<BaseFoundation>().BaseType, "an ordinary Tank Base");
                Assert.IsNull(home.GetComponent<BaseLevelInfo>(), "no Stage 1 base level (none is defined for the Home Base)");
                Assert.AreEqual("HOME BASE", home.GetComponentInChildren<HomeBaseLabel>(true).Text.text);
                Assert.IsFalse(standard.Any(r => r.GetComponentInChildren<BaseController>(true).IsPlayerOwned), "standard bases start neutral");
                Assert.IsFalse(standard.Any(r => r.GetComponentInChildren<HomeBaseLabel>(true) != null));

                yield return WaitForInitialState(home);
                BaseController someTank = standard.First(r => VariantOf(r) == "Tank").GetComponent<BaseController>();
                yield return WaitForInitialState(someTank);
                CollectionAssert.AreEqual(MeshSignature(someTank), MeshSignature(home), "same Tank Base prefab and starting buildings");

                // ---- Real storage: Home Base L1 = normal contribution only.
                Assert.AreEqual(1_310_000, Cap(manager, ResourceType.Concrete));
                Assert.AreEqual(905_000, Cap(manager, ResourceType.Iron));
                Assert.AreEqual(402_000, Cap(manager, ResourceType.Aluminium));
                Assert.AreEqual(101_000, Cap(manager, ResourceType.Fuel));
                Assert.AreEqual(10_000, Cap(manager, ResourceType.Gold));
                Assert.AreEqual(1, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length, "one shared manager");

                SetRefinery(home, 5);
                Assert.AreEqual(1_380_500, Cap(manager, ResourceType.Concrete), "L5: normal + special");
                Assert.AreEqual(951_100, Cap(manager, ResourceType.Iron));
                Assert.AreEqual(429_500, Cap(manager, ResourceType.Aluminium));
                Assert.AreEqual(111_000, Cap(manager, ResourceType.Fuel));

                // ---- Developer ownership on a real standard base.
                Assert.IsTrue(DebugOwnership.TryAssignToPlayer(someTank, out _));
                Assert.AreEqual(1_390_500, Cap(manager, ResourceType.Concrete));
                Assert.IsTrue(DebugOwnership.TryReturnToNeutral(someTank, out _));
                Assert.AreEqual(1_380_500, Cap(manager, ResourceType.Concrete));
                Assert.IsFalse(DebugOwnership.TryReturnToNeutral(home, out _), "Home Base protected");
                Assert.IsTrue(home.IsHomeBase);

                Dictionary<string, Snapshot> currentWithHome = Capture(spawner);
                int seed = proto.CurrentBaseSeed;

                // ---- Regeneration with the same seed (no warm-up; Stage 9): identical standard bases without and with a Home Base.
                SpawnHomeField.SetValue(spawner, false);
                Assert.IsTrue(proto.RegenerateBases(seed), "350 exact after regeneration without Home Base");
                yield return null; yield return null;
                Dictionary<string, Snapshot> withoutHome = Capture(spawner);
                AssertSameStandardBases(currentWithHome, withoutHome, "regeneration without Home Base");
                Assert.IsFalse(PlayerHomeBase.HasHomeBase, "no Home Base on a map without one");
                Assert.AreEqual(350, AllBases(spawner).Count);

                SpawnHomeField.SetValue(spawner, true);
                Assert.IsTrue(proto.RegenerateBases(seed), "350 exact after regeneration with Home Base");
                yield return null; yield return null;
                AssertSameStandardBases(withoutHome, Capture(spawner), "regeneration: Home Base off vs on");
                Assert.AreEqual(351, AllBases(spawner).Count);
                Assert.IsNotNull(spawner.LastHomeBase);
                Assert.AreSame(spawner.LastHomeBase, PlayerHomeBase.Current, "exactly one Home Base, re-designated");
                Assert.AreEqual(1, AllBases(spawner).Count(b => b.IsHomeBase));
                yield return WaitForInitialState(spawner.LastHomeBase);
                Assert.AreEqual(1_310_000, Cap(manager, ResourceType.Concrete), "new Home Base at L1");

                // ---- The initial generation is unchanged by the Home Base: the world-seed layout with a Home Base equals
                // the INITIAL generation (captured in GenerationCompleted) of a fresh load with the Home Base switched off.
                Assert.IsTrue(proto.RegenerateBases(proto.BaseSeed));
                Dictionary<string, Snapshot> worldSeedWithHome = Capture(spawner);
                Time.timeScale = 1f;
                Ironveil.WorldGeneration.Tests.WorldGenerationTestUtil.WorldSnapshot initialOff = null;
                yield return Ironveil.WorldGeneration.Tests.WorldGenerationTestUtil.Load(WorldExplorerPath, false, s => initialOff = s);
                spawner = Object.FindAnyObjectByType<BaseSpawner>();
                Assert.IsFalse(spawner.SpawnsPlayerHomeBase);
                Assert.IsFalse(PlayerHomeBase.HasHomeBase);
                Assert.AreEqual(350, initialOff.totalBases, "a map without a Home Base has exactly 350 bases");
                Assert.AreEqual(350, AllBases(spawner).Count);
                Assert.AreEqual(350, worldSeedWithHome.Count);
                foreach (var kv in worldSeedWithHome)
                {
                    var o = initialOff.standard[kv.Key];
                    Assert.AreEqual(kv.Value.position, o.position, kv.Key + ": initial generation, Home Base on vs off");
                    Assert.IsTrue(kv.Value.position.x == o.position.x && kv.Value.position.y == o.position.y && kv.Value.position.z == o.position.z, kv.Key + " exact");
                    Assert.AreEqual(kv.Value.level, o.level, kv.Key);
                }
                Assert.AreEqual(1_300_000, Cap(ResourceManager.Instance, ResourceType.Concrete), "no Home Base contribution");
            }
            finally
            {
                if (spawner != null) SpawnHomeField.SetValue(spawner, true);
                Time.timeScale = 1f;
            }

            yield return new ExitPlayMode();
        }

        // ------------------------------------------------------------ MainBuild

        [UnityTest]
        public IEnumerator MainBuild_HomeBaseContributes_OwnedBaseExpandsSharedStorage_DestroyRemovesIt_HudFollows()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            GameObject root = null;
            try
            {
                yield return LoadAndGenerate(ResourceManagerTests.MainBuildPath);
                Time.timeScale = 0f;
                Assert.IsTrue(ResourceStorageExpansion.IsAttached, "attached automatically for the play session");
                ResourceManager manager = ResourceManager.Instance;
                Assert.AreEqual(1, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length);

                BaseSpawner spawner = Object.FindAnyObjectByType<BaseSpawner>();
                Assert.AreEqual(350, StandardRoots(spawner).Count);
                Assert.AreEqual(351, AllBases(spawner).Count);
                BaseController home = PlayerHomeBase.Current;
                Assert.IsNotNull(home, spawner.LastHomeBaseFailure);
                yield return WaitForInitialState(home);
                Assert.AreEqual(1_310_000, Cap(manager, ResourceType.Concrete), "Home Base L1 contribution");
                Assert.AreEqual(101_000, Cap(manager, ResourceType.Fuel));

                var settings = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath);
                root = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/BaseFoundations/Tank Foundation.prefab"),
                    new Vector3(5000f, 0f, 5000f), Quaternion.identity);
                root.transform.localScale = Vector3.one * 6.5f;
                BaseController b = root.AddComponent<BaseController>();
                b.InitializeIdentity("STAGE8_PLAYMODE", "Stage 8 Test Base");
                Assert.AreEqual(BaseConstruction.InitResult.Initialised, BaseConstruction.InitialiseBase(b, settings, null, out string error), error);

                b.SetOwner(BaseController.PlayerOwnerId);
                Assert.AreEqual(1_320_000, Cap(manager, ResourceType.Concrete));
                Assert.AreEqual(102_000, Cap(manager, ResourceType.Fuel));
                yield return null;
                ResourceHud hud = Object.FindAnyObjectByType<ResourceHud>();
                if (hud != null) Assert.AreEqual("/ 1,320,000", hud.Items[(int)ResourceType.Concrete].Capacity.text, "HUD shows the live limit");

                Object.Destroy(root);
                root = null;
                yield return null;
                Assert.AreEqual(1_310_000, Cap(manager, ResourceType.Concrete), "a destroyed owned base stops contributing");
                if (hud != null) Assert.AreEqual("/ 1,310,000", hud.Items[(int)ResourceType.Concrete].Capacity.text);
            }
            finally
            {
                if (root != null) Object.Destroy(root);
                Time.timeScale = 1f;
            }

            yield return new ExitPlayMode();
        }
    }
}
