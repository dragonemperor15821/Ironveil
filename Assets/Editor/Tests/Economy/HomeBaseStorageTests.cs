using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>
    /// Stage 8B: Home Base designation (PlayerHomeBase), its protection, the developer-only ownership API, and its
    /// storage: normal L1 / L5 contribution plus the special bonus only while its Factory / Refinery is at L5.
    /// Bases are temporary instances of the real Tank foundation prefab initialised through BaseConstruction.
    /// </summary>
    public class HomeBaseStorageTests
    {
        private const string ConfigPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        private static readonly ResourceType C = ResourceType.Concrete, I = ResourceType.Iron, A = ResourceType.Aluminium, F = ResourceType.Fuel;
        private const long HC = 1_300_000, HI = 900_000, HA = 400_000, HF = 100_000;

        private readonly List<Object> _created = new List<Object>();
        private ConstructionSettings _settings;
        private BuildingDefinition _refinery;
        private BuildingDefinition _flak;
        private HomeBaseResourceConfig _config;
        private ResourceManager _resources;

        [SetUp]
        public void SetUp()
        {
            _settings = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath);
            _refinery = _settings.BuildingCatalog.Get(BuildingType.FactoryRefinery);
            _flak = _settings.BuildingCatalog.Get(BuildingType.Flak);
            _config = AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath);
            if (PlayerHomeBase.HasHomeBase) PlayerHomeBase.Release(PlayerHomeBase.Current);
            ResourceStorageExpansion.Attach();
            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(_config), _resources.LastError);
        }

        [TearDown]
        public void TearDown()
        {
            if (PlayerHomeBase.HasHomeBase) PlayerHomeBase.Release(PlayerHomeBase.Current);
            ResourceStorageExpansion.Detach();
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include)) _created.Add(m.gameObject);
            foreach (Object o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private BaseController NewBase(string id, float x = 0f)
        {
            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/BaseFoundations/Tank Foundation.prefab"));
            _created.Add(root);
            root.transform.position = new Vector3(x, 0f, 0f);
            root.transform.localScale = Vector3.one * 6.5f;
            BaseController c = root.AddComponent<BaseController>();
            c.InitializeIdentity(id, "Tank Base");
            Assert.AreEqual(BaseConstruction.InitResult.Initialised, BaseConstruction.InitialiseBase(c, _settings, null, out string e), e);
            return c;
        }

        private BaseController NewHomeBase(float x = 0f)
        {
            BaseController home = NewBase(PlayerHomeBase.HomeBaseId, x);
            home.InitializeIdentity(PlayerHomeBase.HomeBaseId, PlayerHomeBase.HomeBaseName);
            home.SetOwner(BaseController.PlayerOwnerId);
            Assert.IsTrue(PlayerHomeBase.TryDesignate(home, out string error), error);
            return home;
        }

        private void SetRefinery(BaseController c, int level)
        {
            BuildingSlot slot = c.GetComponent<BaseBuildingState>().Slots.First(s => s != null && s.Accepts(BuildingType.FactoryRefinery));
            BuildingPlacement.Remove(slot);
            if (level > 0) Assert.IsNotNull(BuildingPlacement.Place(slot, _refinery, level));
        }

        private long Cap(ResourceType r) { Assert.IsTrue(_resources.TryGetCapacity(r, out long v)); return v; }
        private long Bal(ResourceType r) { Assert.IsTrue(_resources.TryGetBalance(r, out long v)); return v; }

        private void AssertCaps(long c, long i, long a, long f, string m = "")
        {
            Assert.AreEqual(c, Cap(C), "Concrete " + m);
            Assert.AreEqual(i, Cap(I), "Iron " + m);
            Assert.AreEqual(a, Cap(A), "Aluminium " + m);
            Assert.AreEqual(f, Cap(F), "Fuel " + m);
        }

        // ------------------------------------------------------------ designation

        [Test]
        public void SpecialBonusValues_AreTheApprovedOnes()
        {
            Assert.AreEqual(50_000, BaseStorageRules.HomeBaseL5Bonus(C));
            Assert.AreEqual(30_000, BaseStorageRules.HomeBaseL5Bonus(I));
            Assert.AreEqual(20_000, BaseStorageRules.HomeBaseL5Bonus(A));
            Assert.AreEqual(5_000, BaseStorageRules.HomeBaseL5Bonus(F));
            Assert.AreEqual(0, BaseStorageRules.HomeBaseL5Bonus(ResourceType.Gold));
        }

        [Test]
        public void HomeBase_IsAnOrdinaryTankBase_PlayerOwned_AndVisiblyLabelled()
        {
            BaseController home = NewHomeBase();
            Assert.IsTrue(home.IsHomeBase);
            Assert.AreSame(home, PlayerHomeBase.Current);
            Assert.AreEqual(BaseController.PlayerOwnerId, home.OwnerId, "the existing ownership field");
            Assert.AreEqual("Tank", home.GetComponent<BaseFoundation>().BaseType, "an ordinary Tank Base");

            HomeBaseLabel label = home.GetComponentInChildren<HomeBaseLabel>(true);
            Assert.IsNotNull(label, "HOME BASE marking");
            Assert.AreEqual("HOME BASE", label.Text.text);
            Assert.AreEqual(RenderMode.WorldSpace, label.Canvas.renderMode);
            Assert.AreEqual(0, label.GetComponentsInChildren<Renderer>(true).Length, "no Renderer: hover/selection bounds unchanged");
            Bounds geometry = home.GetComponentsInChildren<Renderer>(true).Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            Assert.Greater(label.transform.position.y, geometry.max.y, "shown above the base");

            BaseController ordinary = NewBase("BASE_TANK_001", 80f);
            Assert.IsFalse(ordinary.IsHomeBase);
            Assert.IsNull(ordinary.GetComponentInChildren<HomeBaseLabel>(true), "ordinary Tank Bases are not marked");
        }

        [Test]
        public void APlayerCannotHaveTwoHomeBases()
        {
            BaseController first = NewHomeBase();
            BaseController second = NewBase("SECOND", 80f);
            second.SetOwner(BaseController.PlayerOwnerId);
            Assert.IsFalse(PlayerHomeBase.TryDesignate(second, out string error));
            StringAssert.Contains("already has a Home Base", error);
            Assert.AreSame(first, PlayerHomeBase.Current);
            Assert.IsFalse(second.IsHomeBase);
            Assert.IsTrue(PlayerHomeBase.TryDesignate(first, out _), "designating the same base again is a no-op");

            BaseController neutral = NewBase("NEUTRAL1", 160f);
            PlayerHomeBase.Release(first);
            Assert.IsFalse(PlayerHomeBase.TryDesignate(neutral, out string neutralError), "must be player-owned");
            StringAssert.Contains("owned by the player", neutralError);
        }

        [Test]
        public void HomeBase_IsProtectedFromNeutralisation_DebugOwnershipWorksForOrdinaryBases()
        {
            BaseController home = NewHomeBase();
            home.SetOwner(BaseController.NeutralOwnerId);
            home.SetOwner("ENEMY");
            Assert.AreEqual(BaseController.PlayerOwnerId, home.OwnerId, "ordinary ownership changes cannot remove it");
            Assert.IsTrue(home.IsHomeBase);
            Assert.IsFalse(DebugOwnership.TryReturnToNeutral(home, out string error));
            StringAssert.Contains("protected", error);
            Assert.IsTrue(home.IsHomeBase);

            BaseController b = NewBase("BASE_TANK_002", 80f);
            Assert.IsTrue(DebugOwnership.TryAssignToPlayer(b, out _));
            Assert.AreEqual(BaseController.PlayerOwnerId, b.OwnerId);
            Assert.AreEqual(HC + 20_000, Cap(C), "home L1 + ordinary L1, recalculated");
            Assert.IsTrue(DebugOwnership.TryReturnToNeutral(b, out _));
            Assert.AreEqual(BaseController.NeutralOwnerId, b.OwnerId);
            Assert.AreEqual(HC + 10_000, Cap(C), "ordinary contribution removed");
            Assert.IsFalse(DebugOwnership.TryAssignToPlayer(null, out _));
        }

        // ------------------------------------------------------------ storage

        [Test]
        public void HomeBaseL1_GetsTheNormalContributionOnly()
        {
            NewHomeBase();
            AssertCaps(HC + 10_000, HI + 5_000, HA + 2_000, HF + 1_000);
            Assert.IsFalse(ResourceStorageExpansion.LastCalculation.homeBaseBonusApplied);
            Assert.AreEqual(PlayerHomeBase.HomeBaseId, ResourceStorageExpansion.LastCalculation.homeBaseId);
        }

        [Test]
        public void HomeBaseL5_GetsNormalL5PlusTheSpecialBonus()
        {
            BaseController home = NewHomeBase();
            SetRefinery(home, 5);
            AssertCaps(HC + 30_500 + 50_000, HI + 21_100 + 30_000, HA + 9_500 + 20_000, HF + 6_000 + 5_000);
            Assert.IsTrue(ResourceStorageExpansion.LastCalculation.homeBaseBonusApplied);
            SetRefinery(home, 1);
            AssertCaps(HC + 10_000, HI + 5_000, HA + 2_000, HF + 1_000, "back to L1: special bonus gone");
        }

        [Test]
        public void OrdinaryL5Bases_NeverGetTheSpecialBonus()
        {
            BaseController a = NewBase("BASE_TANK_001");
            BaseController b = NewBase("BASE_TANK_002", 80f);
            SetRefinery(a, 5); SetRefinery(b, 5);
            a.SetOwner(BaseController.PlayerOwnerId); b.SetOwner(BaseController.PlayerOwnerId);
            AssertCaps(HC + 2 * 30_500, HI + 2 * 21_100, HA + 2 * 9_500, HF + 2 * 6_000);
            Assert.IsFalse(ResourceStorageExpansion.LastCalculation.homeBaseBonusApplied);
            Assert.IsNull(ResourceStorageExpansion.LastCalculation.homeBaseId);

            StorageCalculation pure = ResourceStorageExpansion.Calculate(_config, new[]
            {
                new BaseStorageSource("H1", true, 5, 1, true),
                new BaseStorageSource("H2", true, 5, 1, true),
            });
            Assert.AreEqual(HC + 2 * 30_500 + 50_000, pure.TargetCapacity(C), "at most one Home Base bonus");
            CollectionAssert.AreEqual(new[] { "H2" }, pure.extraHomeBaseIds);
        }

        [Test]
        public void LosingAnOrdinaryBase_RemovesOnlyItsContribution()
        {
            BaseController home = NewHomeBase();
            SetRefinery(home, 5);
            BaseController b = NewBase("BASE_TANK_001", 80f);
            b.SetOwner(BaseController.PlayerOwnerId);
            long withB = Cap(C);
            b.SetOwner("ENEMY");
            Assert.AreEqual(withB - 10_000, Cap(C));
            Assert.AreEqual(HC + 30_500 + 50_000, Cap(C), "home normal + special unchanged");
        }

        [Test]
        public void HomeRefineryRemoved_RemovesNormalAndSpecial_RebuildRestores()
        {
            BaseController home = NewHomeBase();
            SetRefinery(home, 5);
            Assert.IsTrue(_resources.TrySpend(C, 300_000)); // keep the balance under every limit involved
            SetRefinery(home, 0);
            AssertCaps(HC, HI, HA, HF, "destroyed: no normal, no special");
            SetRefinery(home, 5);
            AssertCaps(HC + 80_500, HI + 51_100, HA + 29_500, HF + 11_000, "rebuilt at L5");
        }

        [Test]
        public void RepeatedRecalculation_NeverDuplicates()
        {
            BaseController home = NewHomeBase();
            SetRefinery(home, 5);
            for (int k = 0; k < 6; k++) ResourceStorageExpansion.Recalculate(_resources);
            PlayerHomeBase.TryDesignate(home, out _);
            AssertCaps(HC + 80_500, HI + 51_100, HA + 29_500, HF + 11_000);
        }

        [Test]
        public void Shrinking_PreservesExcess_Growing_ReplenishesImmediately()
        {
            BaseController home = NewHomeBase();
            Assert.AreEqual(HC + 10_000, Cap(C));
            Assert.AreEqual(1, _resources.Ledger.GetActiveCycleCount(C), "storage grew while full: refilling started without a spend");
            _resources.Tick(5f);
            Assert.AreEqual(HC + 10_000, Bal(C), "filled to the new limit");

            SetRefinery(home, 0); // shrink below the balance
            Assert.AreEqual(HC, Cap(C));
            Assert.AreEqual(HC + 10_000, Bal(C), "excess preserved");
            Assert.AreEqual(0, _resources.Ledger.GetActiveCycleCount(C), "no replenishment above the limit");
            _resources.Tick(60f);
            Assert.AreEqual(HC + 10_000, Bal(C));
            Assert.IsTrue(_resources.TrySpend(C, 50_000));
            Assert.AreEqual(1, _resources.Ledger.GetActiveCycleCount(C), "below the limit: replenishment resumes");
        }

        // ------------------------------------------------------------ 200 owned bases including the Home Base

        private static IEnumerable<BaseStorageSource> TwoHundredWithHome(int ordinaryL5 = 199)
        {
            yield return new BaseStorageSource(PlayerHomeBase.HomeBaseId, true, 5, 1, true);
            for (int k = 1; k <= ordinaryL5; k++) yield return new BaseStorageSource($"BASE_TANK_{k:000}", true, 5);
        }

        [Test]
        public void TwoHundredOwnedBases_IncludingTheHomeBase_AllL5_ExactTotals()
        {
            StorageCalculation calc = ResourceStorageExpansion.Calculate(_config, TwoHundredWithHome());
            Assert.AreEqual(200, calc.ContributingCount);
            Assert.IsTrue(calc.homeBaseBonusApplied);
            Assert.AreEqual(7_450_000, calc.TargetCapacity(C));
            Assert.AreEqual(5_150_000, calc.TargetCapacity(I));
            Assert.AreEqual(2_320_000, calc.TargetCapacity(A));
            Assert.AreEqual(1_305_000, calc.TargetCapacity(F));
        }

        // Minimum owned bases INCLUDING an L5 Home Base (all L5) for #13..#28.
        private static readonly int[] MinWithHomeAllL5 = { 1, 6, 12, 18, 26, 34, 44, 54, 66, 79, 94, 110, 129, 149, 173, 199 };

        [Test]
        public void ExactMinimumOwnedBases_WithAnL5HomeBase()
        {
            for (int n = 13; n <= 28; n++)
            {
                FlakConstructionRules.TryGetCost(n, out FlakConstructionCost cost);
                int total = MinWithHomeAllL5[n - 13];
                StorageCalculation enough = ResourceStorageExpansion.Calculate(_config, TwoHundredWithHome(total - 1));
                Assert.IsTrue(cost.concrete <= enough.TargetCapacity(C) && cost.iron <= enough.TargetCapacity(I) && cost.aluminium <= enough.TargetCapacity(A), $"#{n} at {total}");
                if (total > 1)
                {
                    StorageCalculation shortBy1 = ResourceStorageExpansion.Calculate(_config, TwoHundredWithHome(total - 2));
                    Assert.IsFalse(cost.concrete <= shortBy1.TargetCapacity(C) && cost.iron <= shortBy1.TargetCapacity(I) && cost.aluminium <= shortBy1.TargetCapacity(A), $"#{n} at {total - 1}");
                }
            }
        }

        [Test]
        public void Flak28_PassesTheLiveStorageCheck_AtTheHomeBaseTarget()
        {
            BaseController builder = NewBase("BUILDER");
            ResourceStorageExpansion.Detach();
            ResourceStorageExpansion.Apply(_resources, ResourceStorageExpansion.Calculate(_config, TwoHundredWithHome()));
            AssertCaps(7_450_000, 5_150_000, 2_320_000, 1_305_000);
            for (int n = 3; n <= 27; n++)
            {
                Refill();
                Assert.AreEqual(FlakConstructionResult.Started, BaseConstruction.TryStartFlakConstruction(builder, _flak, _resources, n), "#" + n);
                BaseConstruction.GetConstructionQueue(builder).Tick(10_000);
            }
            Refill();
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(builder, _flak, _resources);
            Assert.AreEqual(28, q.flakNumber);
            Assert.AreEqual(FlakConstructionResult.Available, q.status);
            Assert.AreEqual(0, q.exceedsStorage.Count, "all three resources pass the storage check");
            Assert.AreEqual(FlakConstructionResult.Started, BaseConstruction.TryStartFlakConstruction(builder, _flak, _resources, 28));
            AssertCaps(7_450_000, 5_150_000, 2_320_000, 1_305_000, "storage never deducted");
            Assert.AreEqual(7_450_000 - 7_389_300, Bal(C));
            Assert.AreEqual(5_150_000 - 5_098_600, Bal(I));
            Assert.AreEqual(2_320_000 - 2_290_700, Bal(A));
        }

        private void Refill()
        {
            foreach (ResourceType r in new[] { C, I, A })
            {
                long room = Cap(r) - Bal(r);
                if (room > 0) Assert.IsTrue(_resources.Ledger.TryAdd(r, room, out _));
            }
        }
    }
}
