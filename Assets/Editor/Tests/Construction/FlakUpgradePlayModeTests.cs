using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ironveil.Economy;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Ironveil.Construction.Tests
{
    /// <summary>
    /// Play Mode test of the Flak Upgrade System on REAL generated bases: World Explorer
    /// generates its world, BaseConstructionBootstrap gives every base 2 x L1 Flak on the
    /// real terrain, and the session's ResourceManager (HomeBaseResourceConfig) pays.
    /// Covers deferred Destroy (old Flaks gone next frame, never orphaned), terrain-grounded
    /// heights kept exactly, inheritance for new Flaks grounded on the real terrain, other
    /// bases untouched, and the hover target staying bound.
    /// </summary>
    public class FlakUpgradePlayModeTests
    {
        private const string WorldExplorerPath = "Assets/Scenes/Ironveil - World Explorer.unity";
        private const float GenerationTimeoutSeconds = 240f;

        [UnityTest]
        public IEnumerator WorldExplorer_GeneratedBase_UpgradesL1ToL3_InPlace_WithSharedGold()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Scene scene = EditorSceneManager.LoadSceneInPlayMode(WorldExplorerPath, new LoadSceneParameters(LoadSceneMode.Single));
                // Stage 9: wait for World Explorer's FINAL world (its startup seed replaces the initial generation).
                yield return Ironveil.WorldGeneration.Tests.WorldGenerationTestUtil.WaitForFinalWorld(scene, WorldExplorerPath);
                float start = Time.realtimeSinceStartup;
                List<BaseFlakDefence> bases = null;
                while (true)
                {
                    if (scene.isLoaded)
                    {
                        bases = Object.FindObjectsByType<BaseFlakDefence>(FindObjectsInactive.Exclude)
                            .Where(d => d.IsBound && d.InitialStateApplied && d.ConstructedCount == 2).ToList();
                        if (bases.Count >= 50) break;
                    }

                    if (Time.realtimeSinceStartup - start > GenerationTimeoutSeconds)
                        Assert.Fail($"World Explorer produced only {bases?.Count ?? 0} initialised bases.");
                    yield return null;
                }

                for (int i = 0; i < 10; i++) yield return null; // let the last bootstrap scan settle
                ResourceManager resources = ResourceManager.Instance;
                Assert.IsNotNull(resources);
                Assert.IsTrue(resources.IsInitialized, resources.LastError);
                Time.timeScale = 0f;

                bases = Object.FindObjectsByType<BaseFlakDefence>(FindObjectsInactive.Exclude)
                    .Where(d => d.IsBound && d.InitialStateApplied).ToList();
                Assert.IsTrue(bases.All(d => d.FlakLevel == 1), "every generated base starts at L1");
                List<BaseController> controllers = bases.Select(d => d.GetComponent<BaseController>())
                    .OrderBy(c => c.BaseId, System.StringComparer.Ordinal).ToList();
                BaseController a = controllers[0];
                BaseController b = controllers[1];
                BaseFlakDefence da = a.GetComponent<BaseFlakDefence>(), db = b.GetComponent<BaseFlakDefence>();

                Assert.IsNotNull(BaseConstruction.TryConstructFlak(a), "third Flak on the real terrain");
                var before = da.Layout.InConstructionOrder().Select(p => da.GetSlot(p.slotId)).Where(s => s.IsOccupied)
                    .ToDictionary(s => s, s => (s.Occupant, s.Occupant.transform.position));
                var otherBefore = db.Layout.InConstructionOrder().Select(p => db.GetSlot(p.slotId)).Where(s => s.IsOccupied)
                    .ToDictionary(s => s, s => (s.Occupant, s.Occupant.transform.position));
                Assert.IsTrue(resources.TryGetBalance(ResourceType.Gold, out long gold0));

                // L1 -> L2
                Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(a, da.FlakLevel));
                Assert.IsTrue(resources.TryGetBalance(ResourceType.Gold, out long gold1));
                Assert.AreEqual(gold0 - FlakUpgradeRules.GoldCostL1ToL2, gold1);
                yield return null; // deferred Destroy runs
                AssertBase(da, 2, 3);
                foreach (var kv in before)
                {
                    Assert.IsTrue(kv.Value.Item1 == null, kv.Key.SlotId + ": old L1 Flak destroyed");
                    Assert.AreEqual(kv.Value.Item2, kv.Key.Occupant.transform.position, kv.Key.SlotId + ": exact grounded position");
                }

                // Duplicate click with the stale level: nothing bought.
                Assert.AreEqual(FlakUpgradeResult.LevelChanged, BaseConstruction.TryUpgradeFlaks(a, 1));
                Assert.IsTrue(resources.TryGetBalance(ResourceType.Gold, out long goldDup));
                Assert.AreEqual(gold1, goldDup);

                // New Flak inherits L2, grounded on the real terrain like before.
                BuildingInstance inherited = BaseConstruction.TryConstructFlak(a);
                Assert.IsNotNull(inherited);
                Assert.AreEqual(2, inherited.BuildingLevel);

                // L2 -> L3
                Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(a, 2));
                Assert.IsTrue(resources.TryGetBalance(ResourceType.Gold, out long gold2));
                Assert.AreEqual(gold1 - FlakUpgradeRules.GoldCostL2ToL3, gold2);
                yield return null;
                AssertBase(da, 3, 4);
                foreach (var kv in before)
                    Assert.AreEqual(kv.Value.Item2, kv.Key.Occupant.transform.position, kv.Key.SlotId + ": exact grounded position at L3");

                // L3 -> L4 for 39 Gold on the real terrain.
                Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(a, 3));
                Assert.IsTrue(resources.TryGetBalance(ResourceType.Gold, out long gold3));
                Assert.AreEqual(gold2 - FlakUpgradeRules.GoldCostL3ToL4.Value, gold3);
                Assert.AreEqual(FlakUpgradeResult.LevelChanged, BaseConstruction.TryUpgradeFlaks(a, 3), "stale duplicate refused");
                yield return null;
                AssertBase(da, 4, 4);
                foreach (var kv in before)
                    Assert.AreEqual(kv.Value.Item2, kv.Key.Occupant.transform.position, kv.Key.SlotId + ": exact grounded position at L4");

                Assert.AreEqual(FlakUpgradeResult.AtMaximumLevel, BaseConstruction.TryUpgradeFlaks(a));
                Assert.AreEqual(4, BaseConstruction.TryConstructFlak(a).BuildingLevel);

                // The other base is untouched.
                AssertBase(db, 1, 2);
                foreach (var kv in otherBefore)
                {
                    Assert.AreSame(kv.Value.Item1, kv.Key.Occupant);
                    Assert.AreEqual(kv.Value.Item2, kv.Key.Occupant.transform.position);
                }
                Assert.IsTrue(bases.Where(d => d != da).All(d => d.FlakLevel == 1), "no other base changed level");

                // Nothing orphaned anywhere in the scene.
                Assert.AreEqual(0, Object.FindObjectsByType<BuildingInstance>(FindObjectsInactive.Include).Count(x => x.transform.parent == null));

                // Hover target still bound to this base and its bounds cover the base.
                BaseInteractionTarget target = a.GetComponent<BaseInteractionTarget>();
                if (target != null) Assert.AreSame(a, target.Context.Controller);
                Assert.AreEqual(5, BaseFlakRoster.GetSurvivingFlaks(a));
            }
            finally
            {
                Time.timeScale = 1f;
            }

            yield return new ExitPlayMode();
        }

        private static void AssertBase(BaseFlakDefence d, int level, int count)
        {
            Assert.AreEqual(level, d.FlakLevel, d.name);
            Assert.AreEqual(count, d.ConstructedCount, d.name);
            BuildingInstance[] flaks = d.GetComponentsInChildren<BuildingInstance>(true).Where(x => x.Type == BuildingType.Flak).ToArray();
            Assert.AreEqual(count, flaks.Length, d.name + ": exactly the constructed Flaks exist under the base");
            foreach (BuildingInstance f in flaks)
            {
                Assert.AreEqual(level, f.BuildingLevel);
                Assert.IsTrue(f.gameObject.activeInHierarchy);
                Assert.AreSame(f.Slot.transform, f.transform.parent);
                Assert.AreSame(f, f.Slot.Occupant);
            }
        }
    }
}
