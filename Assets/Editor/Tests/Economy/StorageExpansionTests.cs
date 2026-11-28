using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>
    /// Stage 8: shared player storage expansion (ResourceStorageExpansion + BaseStorageRules +
    /// ResourceLedger.TrySetCapacity). Bases are temporary instances of the real foundation prefabs initialised
    /// through BaseConstruction.InitialiseBase (2 x L1 Flak + 1 x L1 Factory / Refinery); ownership is set with the
    /// existing BaseController.SetOwner. Large-count cases use the pure calculation.
    /// </summary>
    public class StorageExpansionTests
    {
        private const string ConfigPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        private const string FoundationDir = "Assets/Prefabs/BaseFoundations/";
        private static readonly ResourceType C = ResourceType.Concrete, I = ResourceType.Iron, A = ResourceType.Aluminium, F = ResourceType.Fuel;

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
            ResourceStorageExpansion.Attach();
            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(_config), _resources.LastError);
        }

        [TearDown]
        public void TearDown()
        {
            ResourceStorageExpansion.Detach();
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include)) _created.Add(m.gameObject);
            foreach (BuildingInstance b in Object.FindObjectsByType<BuildingInstance>(FindObjectsInactive.Include))
                if (b.transform.parent == null) _created.Add(b.gameObject);
            foreach (Object o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        // ------------------------------------------------------------ helpers

        private BaseController NewBase(string id, string type = "Tank", float x = 0f)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FoundationDir + type + " Foundation.prefab");
            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _created.Add(root);
            root.transform.position = new Vector3(x, 0f, 0f);
            root.transform.localScale = Vector3.one * 6.5f;
            BaseController c = root.AddComponent<BaseController>();
            c.InitializeIdentity(id, type + " Base");
            Assert.AreEqual(BaseConstruction.InitResult.Initialised, BaseConstruction.InitialiseBase(c, _settings, null, out string e), e);
            return c;
        }

        private static BuildingSlot RefinerySlot(BaseController c) =>
            c.GetComponent<BaseBuildingState>().Slots.First(s => s != null && s.Accepts(BuildingType.FactoryRefinery));

        private void SetRefinery(BaseController c, int level)
        {
            BuildingSlot slot = RefinerySlot(c);
            BuildingPlacement.Remove(slot);
            if (level > 0) Assert.IsNotNull(BuildingPlacement.Place(slot, _refinery, level), "L" + level);
        }

        private long Cap(ResourceType r)
        {
            Assert.IsTrue(_resources.TryGetCapacity(r, out long cap), r.ToString());
            return cap;
        }

        private long Bal(ResourceType r)
        {
            Assert.IsTrue(_resources.TryGetBalance(r, out long b));
            return b;
        }

        private void AssertCaps(long c, long i, long a, long f, string message = null)
        {
            Assert.AreEqual(c, Cap(C), "Concrete " + message);
            Assert.AreEqual(i, Cap(I), "Iron " + message);
            Assert.AreEqual(a, Cap(A), "Aluminium " + message);
            Assert.AreEqual(f, Cap(F), "Fuel " + message);
        }

        private const long HC = 1_300_000, HI = 900_000, HA = 400_000, HF = 100_000;

        private static IEnumerable<BaseStorageSource> Owned(int l5, int l1, int start = 0)
        {
            for (int k = 0; k < l5; k++) yield return new BaseStorageSource($"B{start + k:000}", true, 5);
            for (int k = 0; k < l1; k++) yield return new BaseStorageSource($"B{start + l5 + k:000}", true, 1);
        }

        // ------------------------------------------------------------ 0. rules

        [Test]
        public void Rules_ApprovedL1AndL5Values_NoIntermediateLevels()
        {
            Assert.AreEqual(10_000, BaseStorageRules.L1Contribution(C)); Assert.AreEqual(20_500, BaseStorageRules.L5Bonus(C));
            Assert.AreEqual(5_000, BaseStorageRules.L1Contribution(I)); Assert.AreEqual(16_100, BaseStorageRules.L5Bonus(I));
            Assert.AreEqual(2_000, BaseStorageRules.L1Contribution(A)); Assert.AreEqual(7_500, BaseStorageRules.L5Bonus(A));
            Assert.AreEqual(1_000, BaseStorageRules.L1Contribution(F)); Assert.AreEqual(5_000, BaseStorageRules.L5Bonus(F));
            Assert.AreEqual(0, BaseStorageRules.L1Contribution(ResourceType.Gold));
            Assert.AreEqual(0, BaseStorageRules.L5Bonus(ResourceType.Gold));
            Assert.IsTrue(BaseStorageRules.TryGetContribution(5, C, out long l5)); Assert.AreEqual(30_500, l5, "L5 = L1 + bonus");
            for (int level = 2; level <= 4; level++)
            {
                Assert.IsFalse(BaseStorageRules.TryGetContribution(level, C, out long none), "L" + level);
                Assert.AreEqual(0, none);
            }
            Assert.IsFalse(BaseStorageRules.TryGetContribution(6, C, out _));
            CollectionAssert.AreEqual(new[] { 1, 5 }, _refinery.Levels.Select(l => l.level).ToArray(), "FactoryRefinery.asset has models for L1 and L5 only");
        }

        // ------------------------------------------------------------ 1-4. contributions

        [Test]
        public void HomeBaseStartingStorage_WithNoOwnedBase_GoldUntouched()
        {
            NewBase("N1");
            Assert.AreEqual(0, ResourceStorageExpansion.Recalculate(_resources).ContributingCount);
            AssertCaps(HC, HI, HA, HF);
            Assert.AreEqual(10_000, Cap(ResourceType.Gold));
        }

        [Test]
        public void OneOwnedL1Base_AddsTheL1Contribution()
        {
            BaseController b = NewBase("B1");
            b.SetOwner(BaseController.PlayerOwnerId);
            AssertCaps(HC + 10_000, HI + 5_000, HA + 2_000, HF + 1_000);
            Assert.AreEqual(10_000, Cap(ResourceType.Gold));
        }

        [Test]
        public void OneOwnedL5Base_AddsL1PlusTheL5Bonus()
        {
            BaseController b = NewBase("B1");
            SetRefinery(b, 5);
            b.SetOwner(BaseController.PlayerOwnerId);
            AssertCaps(HC + 30_500, HI + 21_100, HA + 9_500, HF + 6_000);
        }

        [Test]
        public void MultipleOwnedBases_MixedLevels_AllNineVariantsCountOnce()
        {
            string[] types = { "Tank", "Heli", "Air", "Naval", "Rail", "Heli Naval", "Heli Rail", "Air Naval", "Air Rail" };
            var bases = types.Select((t, k) => NewBase("V" + k, t, 60f * k)).ToList();
            SetRefinery(bases[0], 5);
            SetRefinery(bases[4], 5);
            foreach (BaseController b in bases) b.SetOwner(BaseController.PlayerOwnerId);
            // 2 x L5 + 7 x L1
            AssertCaps(HC + 2 * 30_500 + 7 * 10_000, HI + 2 * 21_100 + 7 * 5_000, HA + 2 * 9_500 + 7 * 2_000, HF + 2 * 6_000 + 7 * 1_000);
            Assert.AreEqual(9, ResourceStorageExpansion.LastCalculation.ContributingCount);
            Assert.AreEqual(2, ResourceStorageExpansion.LastCalculation.l5Count);
        }

        [Test]
        public void NeutralAndOtherOwners_ContributeNothing()
        {
            BaseController neutral = NewBase("N1");
            SetRefinery(neutral, 5);
            BaseController enemy = NewBase("E1", "Air", 60f);
            enemy.SetOwner("ENEMY");
            Assert.AreEqual(BaseController.NeutralOwnerId, neutral.OwnerId);
            ResourceStorageExpansion.Recalculate(_resources);
            AssertCaps(HC, HI, HA, HF);
        }

        // ------------------------------------------------------------ 5-6. ownership changes

        [Test]
        public void OwnershipLoss_RemovesStorage_AndRegainRestoresIt()
        {
            BaseController b = NewBase("B1");
            SetRefinery(b, 5);
            b.SetOwner(BaseController.PlayerOwnerId);
            AssertCaps(HC + 30_500, HI + 21_100, HA + 9_500, HF + 6_000, "owned");
            Assert.IsTrue(_resources.TrySpend(C, 100_000)); // balance below every limit involved
            b.SetOwner("ENEMY");
            AssertCaps(HC, HI, HA, HF, "lost");
            b.SetOwner(BaseController.PlayerOwnerId);
            AssertCaps(HC + 30_500, HI + 21_100, HA + 9_500, HF + 6_000, "regained");
        }

        // ------------------------------------------------------------ 7-9. Factory / Refinery state

        [Test]
        public void RefineryDestroyed_RemovesContribution_RebuiltRestoresIt()
        {
            BaseController b = NewBase("B1");
            b.SetOwner(BaseController.PlayerOwnerId);
            AssertCaps(HC + 10_000, HI + 5_000, HA + 2_000, HF + 1_000);
            BuildingPlacement.Remove(RefinerySlot(b));
            AssertCaps(HC, HI, HA, HF, "destroyed");
            CollectionAssert.AreEqual(new[] { "B1" }, ResourceStorageExpansion.LastCalculation.ownedWithoutRefinery);
            Assert.IsNotNull(BuildingPlacement.Place(RefinerySlot(b), _refinery, 1));
            AssertCaps(HC + 10_000, HI + 5_000, HA + 2_000, HF + 1_000, "rebuilt");
        }

        [Test]
        public void UpgradeL1ToL5_AddsExactlyTheBonus()
        {
            BaseController b = NewBase("B1");
            b.SetOwner(BaseController.PlayerOwnerId);
            long c1 = Cap(C), i1 = Cap(I), a1 = Cap(A), f1 = Cap(F);
            SetRefinery(b, 5);
            Assert.AreEqual(20_500, Cap(C) - c1);
            Assert.AreEqual(16_100, Cap(I) - i1);
            Assert.AreEqual(7_500, Cap(A) - a1);
            Assert.AreEqual(5_000, Cap(F) - f1);
        }

        [Test]
        public void IntermediateLevels_CannotBePlaced_AndWouldContributeNothing()
        {
            BaseController b = NewBase("B1");
            BuildingPlacement.Remove(RefinerySlot(b));
            Assert.IsNull(BuildingPlacement.Place(RefinerySlot(b), _refinery, 3), "no L3 model exists");
            StorageCalculation calc = ResourceStorageExpansion.Calculate(_config, new[] { new BaseStorageSource("X", true, 3) });
            Assert.AreEqual(0, calc.ContributingCount);
            CollectionAssert.AreEqual(new[] { "X" }, calc.unsupportedLevel);
            Assert.AreEqual(HC, calc.TargetCapacity(C));
        }

        // ------------------------------------------------------------ 10. duplicates

        [Test]
        public void RepeatedRecalculation_AndDuplicateIds_NeverDoubleCount()
        {
            BaseController a = NewBase("SAME");
            BaseController b = NewBase("SAME", "Air", 60f);
            a.SetOwner(BaseController.PlayerOwnerId);
            b.SetOwner(BaseController.PlayerOwnerId);
            for (int k = 0; k < 5; k++) ResourceStorageExpansion.Recalculate(_resources);
            AssertCaps(HC + 10_000, HI + 5_000, HA + 2_000, HF + 1_000);
            CollectionAssert.AreEqual(new[] { "SAME" }, ResourceStorageExpansion.LastCalculation.duplicateBaseIds);

            StorageCalculation pure = ResourceStorageExpansion.Calculate(_config, Owned(3, 0).Concat(Owned(3, 0)));
            Assert.AreEqual(3, pure.ContributingCount, "same IDs twice: counted once");
        }

        [Test]
        public void Calculation_IsOrderIndependent()
        {
            var list = Owned(7, 11).ToList();
            StorageCalculation forward = ResourceStorageExpansion.Calculate(_config, list);
            list.Reverse();
            StorageCalculation reverse = ResourceStorageExpansion.Calculate(_config, list);
            foreach (ResourceType r in BaseStorageRules.Resources) Assert.AreEqual(forward.TargetCapacity(r), reverse.TargetCapacity(r));
            CollectionAssert.AreEqual(forward.contributingBaseIds, reverse.contributingBaseIds);
        }

        // ------------------------------------------------------------ 11. runtime limits in the ledger

        [Test]
        public void Ledger_TrySetCapacity_ApprovedGrowthAndShrinkBehaviour()
        {
            ResourceLedger ledger = HomeBaseResourceProfile.CreateSinglePlayerLedger();
            var events = new List<ResourceCapacityChange>();
            ledger.CapacityChanged += events.Add;
            var balanceEvents = new List<ResourceChange>();
            ledger.Changed += balanceEvents.Add;

            // Growth while full: replenishment starts immediately, without a spend.
            Assert.IsTrue(ledger.TrySetCapacity(C, 2_000_000, out var r1)); Assert.AreEqual(ResourceTransactionResult.Success, r1);
            Assert.AreEqual(2_000_000, ledger.GetCapacity(C));
            Assert.AreEqual(1_300_000, ledger.GetBalance(C), "raising never adds resources by itself");
            Assert.AreEqual(1, ledger.GetActiveCycleCount(C), "a replenishment cycle starts towards the new limit");
            Assert.AreEqual(new ResourceCapacityChange(C, 1_300_000, 2_000_000).ToString(), events.Single().ToString());
            ledger.Advance(System.TimeSpan.FromSeconds(5));
            Assert.AreEqual(1_330_000, ledger.GetBalance(C), "first tick one interval later, no spend needed");

            Assert.IsTrue(ledger.TrySetCapacity(C, 2_000_000, out _), "same value");
            Assert.AreEqual(1, events.Count, "no event for an unchanged value");
            Assert.IsTrue(ledger.TrySetCapacity(C, 2_100_000, out _));
            Assert.AreEqual(1, ledger.GetActiveCycleCount(C), "running cycles are kept, none added");
            ledger.Advance(System.TimeSpan.FromHours(10));
            Assert.AreEqual(2_100_000, ledger.GetBalance(C));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(C));

            // Shrink below the balance: excess preserved, replenishment stopped, additions refused.
            Assert.IsTrue(ledger.TrySpend(C, 50_000));
            Assert.AreEqual(1, ledger.GetActiveCycleCount(C));
            Assert.IsTrue(ledger.TrySetCapacity(C, 1_500_000, out var shrink)); Assert.AreEqual(ResourceTransactionResult.Success, shrink);
            Assert.AreEqual(1_500_000, ledger.GetCapacity(C));
            Assert.AreEqual(2_050_000, ledger.GetBalance(C), "no resources destroyed");
            Assert.AreEqual(0, ledger.GetActiveCycleCount(C), "replenishment stops at or above the limit");
            Assert.IsFalse(ledger.TryAdd(C, 1, out var add)); Assert.AreEqual(ResourceTransactionResult.ExceedsCapacity, add);
            ledger.Advance(System.TimeSpan.FromHours(1));
            Assert.AreEqual(2_050_000, ledger.GetBalance(C));

            // Spending that stays at/above the limit starts no cycle; spending below it resumes replenishment.
            Assert.IsTrue(ledger.TrySpend(C, 550_000));
            Assert.AreEqual(1_500_000, ledger.GetBalance(C));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(C), "exactly at the limit: still no replenishment");
            Assert.IsTrue(ledger.TrySpend(C, 60_000));
            Assert.AreEqual(1, ledger.GetActiveCycleCount(C), "below the limit: replenishment resumes");
            ledger.Advance(System.TimeSpan.FromHours(1));
            Assert.AreEqual(1_500_000, ledger.GetBalance(C), "refills to the new limit, not beyond");

            Assert.IsFalse(ledger.TrySetCapacity(ResourceType.Economy, 5, out var undecided));
            Assert.AreEqual(ResourceTransactionResult.CapacityNotConfigured, undecided, "an undecided capacity stays undecided");
            Assert.IsFalse(ledger.TrySetCapacity(ResourceType.GroupLimit, 5, out var notConfigured));
            Assert.AreEqual(ResourceTransactionResult.NotConfigured, notConfigured);
            Assert.IsFalse(ledger.TrySetCapacity(C, -1, out var invalid));
            Assert.AreEqual(ResourceTransactionResult.InvalidAmount, invalid);
            Assert.IsTrue(balanceEvents.All(e => e.Reason != ResourceChangeReason.Added), "capacity changes raise no balance events");
        }

        [Test]
        public void LostStorage_PreservesTheExcess_StopsAndThenResumesReplenishment()
        {
            BaseController b = NewBase("B1");
            b.SetOwner(BaseController.PlayerOwnerId);
            Assert.IsTrue(_resources.Ledger.TryAdd(C, 10_000)); // fill the expanded store: 1,310,000
            b.SetOwner("ENEMY");
            Assert.AreEqual(HC, Cap(C), "the lower limit is applied");
            Assert.AreEqual(1_310_000, Bal(C), "the 10,000 excess is preserved");
            Assert.IsTrue(ResourceStorageExpansion.LastCalculation.Applied(C));
            Assert.AreEqual(0, _resources.Ledger.GetActiveCycleCount(C), "no replenishment above the limit");
            Assert.AreEqual(HI, Cap(I));

            Assert.IsTrue(_resources.TrySpend(C, 5_000));
            Assert.AreEqual(0, _resources.Ledger.GetActiveCycleCount(C), "1,305,000 is still above 1,300,000");
            Assert.IsTrue(_resources.TrySpend(C, 35_000));
            Assert.AreEqual(1, _resources.Ledger.GetActiveCycleCount(C), "1,270,000: replenishment resumes");
            _resources.Tick(5f);
            Assert.AreEqual(1_300_000, Bal(C), "refilled to the current limit");
        }

        [Test]
        public void Recalculation_PreservesBalancesCyclesAndConstructionQueues()
        {
            BaseController b = NewBase("B1");
            Assert.AreEqual(FlakConstructionResult.Started, BaseConstruction.TryStartFlakConstruction(b, _flak, _resources));
            _resources.Tick(2f);
            var balances = BaseStorageRules.Resources.Select(Bal).ToArray();
            var spent = new[] { C, I, A }; // Fuel is full and unspent
            var cycles = spent.Select(r => _resources.Ledger.GetActiveCycleCount(r)).ToArray();
            BaseFlakConstruction queue = BaseConstruction.GetConstructionQueue(b);
            double remaining = queue.CurrentRemainingSeconds;

            b.SetOwner(BaseController.PlayerOwnerId);
            SetRefinery(b, 5);
            b.SetOwner("ENEMY");
            b.SetOwner(BaseController.PlayerOwnerId);

            CollectionAssert.AreEqual(balances, BaseStorageRules.Resources.Select(Bal).ToArray());
            CollectionAssert.AreEqual(cycles, spent.Select(r => _resources.Ledger.GetActiveCycleCount(r)).ToArray(), "running cycles kept");
            Assert.AreEqual(1, _resources.Ledger.GetActiveCycleCount(F), "Fuel storage grew while full: one cycle refills it (approved growth behaviour)");
            Assert.AreEqual(1, queue.Count);
            Assert.AreEqual(remaining, queue.CurrentRemainingSeconds, 1e-9);
            Assert.AreEqual(1, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length, "one shared manager");
        }

        [Test]
        public void LedgerReset_ReappliesOwnedBaseStorage()
        {
            BaseController b = NewBase("B1");
            b.SetOwner(BaseController.PlayerOwnerId);
            Assert.IsTrue(_resources.ResetToInitialConfiguration());
            AssertCaps(HC + 10_000, HI + 5_000, HA + 2_000, HF + 1_000, "after reset");
        }

        // ------------------------------------------------------------ world regeneration

        [Test]
        public void WorldRegeneration_DestroyedOwnedBasesStopContributing_NewNeutralBasesAddNothing()
        {
            BaseController a = NewBase("OLD1");
            BaseController b = NewBase("OLD2", "Air", 60f);
            a.SetOwner(BaseController.PlayerOwnerId);
            b.SetOwner(BaseController.PlayerOwnerId);
            AssertCaps(HC + 20_000, HI + 10_000, HA + 4_000, HF + 2_000);
            Assert.IsTrue(_resources.TrySpend(C, 100_000));
            Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(b.gameObject);
            // Edit mode does not call OnDestroy on plain MonoBehaviours; in Play Mode BaseController.BaseDestroyed
            // triggers this recalculation (see the Play Mode test). Here it is invoked directly.
            ResourceStorageExpansion.Recalculate(_resources);
            AssertCaps(HC, HI, HA, HF, "old world gone");
            NewBase("NEW1"); NewBase("NEW2", "Heli", 60f);
            ResourceStorageExpansion.Recalculate(_resources);
            AssertCaps(HC, HI, HA, HF, "new world: all neutral");
        }

        // ------------------------------------------------------------ 12. shared configuration

        [Test]
        public void MainBuildAndWorldExplorer_ShareTheOneConfig_AndTheCalculationUsesIt()
        {
            string guid = AssetDatabase.AssetPathToGUID(ConfigPath);
            foreach (string scene in new[] { "Assets/Scenes/Ironveil - MainBuild.unity", "Assets/Scenes/Ironveil - World Explorer.unity" })
            {
                string text = File.ReadAllText(scene);
                Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(text, "homeBaseConfig: \\{fileID: 11400000, guid: " + guid).Count, scene);
            }
            Assert.AreEqual(1, AssetDatabase.FindAssets("t:HomeBaseResourceConfig").Length, "one storage configuration");
            Assert.AreSame(_config, _resources.Config);
            StorageCalculation calc = ResourceStorageExpansion.Recalculate(_resources);
            Assert.AreEqual(HC, calc.BaseCapacity(C));
            Assert.AreEqual(HF, calc.BaseCapacity(F));
            Assert.IsFalse(calc.HasBaseCapacity(ResourceType.Gold), "Gold storage is separate");
        }

        // ------------------------------------------------------------ 13-14. 200-base target and Flak #28

        [Test]
        public void TwoHundredOwnedBases_AllL5_GiveTheApprovedTotals()
        {
            StorageCalculation calc = ResourceStorageExpansion.Calculate(_config, Owned(200, 0));
            Assert.AreEqual(200, calc.ContributingCount);
            Assert.AreEqual(7_400_000, calc.TargetCapacity(C));
            Assert.AreEqual(5_120_000, calc.TargetCapacity(I));
            Assert.AreEqual(2_300_000, calc.TargetCapacity(A));
            Assert.AreEqual(1_300_000, calc.TargetCapacity(F));
        }

        [Test]
        public void LowerCountsAndMixedLevels_ScaleLinearly()
        {
            StorageCalculation calc = ResourceStorageExpansion.Calculate(_config, Owned(100, 50));
            Assert.AreEqual(HC + 100 * 30_500 + 50 * 10_000, calc.TargetCapacity(C));
            Assert.AreEqual(HI + 100 * 21_100 + 50 * 5_000, calc.TargetCapacity(I));
            Assert.AreEqual(HA + 100 * 9_500 + 50 * 2_000, calc.TargetCapacity(A));
            Assert.AreEqual(HF + 100 * 6_000 + 50 * 1_000, calc.TargetCapacity(F));
            calc = ResourceStorageExpansion.Calculate(_config, Owned(0, 1));
            Assert.AreEqual(HC + 10_000, calc.TargetCapacity(C));
            calc = ResourceStorageExpansion.Calculate(_config, Owned(1, 0));
            Assert.AreEqual(HC + 30_500, calc.TargetCapacity(C));
        }

        private static bool Fits(StorageCalculation calc, FlakConstructionCost cost) =>
            cost.concrete <= calc.TargetCapacity(C) && cost.iron <= calc.TargetCapacity(I) && cost.aluminium <= calc.TargetCapacity(A);

        // Independently computed minimum owned-base counts for #13..#28.
        private static readonly int[] MinAllL5 = { 2, 8, 14, 20, 28, 36, 45, 56, 67, 81, 95, 112, 130, 151, 174, 200 };
        private static readonly int[] MinAllL1 = { 10, 35, 63, 94, 130, 169, 214, 263, 319, 381, 450, 528, 616, 714, 823, 946 };

        [Test]
        public void ExactMinimumOwnedBases_ForEveryFlak13To28()
        {
            for (int n = 3; n <= 12; n++)
            {
                FlakConstructionRules.TryGetCost(n, out FlakConstructionCost cost);
                Assert.IsTrue(Fits(ResourceStorageExpansion.Calculate(_config, Owned(0, 0)), cost), "#" + n + " needs no extra base");
            }
            for (int n = 13; n <= 28; n++)
            {
                FlakConstructionRules.TryGetCost(n, out FlakConstructionCost cost);
                int l5 = MinAllL5[n - 13], l1 = MinAllL1[n - 13];
                Assert.IsTrue(Fits(ResourceStorageExpansion.Calculate(_config, Owned(l5, 0)), cost), $"#{n} at {l5} L5 bases");
                Assert.IsFalse(Fits(ResourceStorageExpansion.Calculate(_config, Owned(l5 - 1, 0)), cost), $"#{n} at {l5 - 1} L5 bases");
                Assert.IsTrue(Fits(ResourceStorageExpansion.Calculate(_config, Owned(0, l1)), cost), $"#{n} at {l1} L1 bases");
                Assert.IsFalse(Fits(ResourceStorageExpansion.Calculate(_config, Owned(0, l1 - 1)), cost), $"#{n} at {l1 - 1} L1 bases");
            }
            Assert.Greater(MinAllL1[15], 349, "#28 cannot be reached with L1 Factory / Refineries in a 350-base world");
        }

        [Test]
        public void Flak28_PassesTheLiveStorageCheck_At200L5Bases_And199IsShort()
        {
            BaseController b = NewBase("BUILDER");
            ResourceStorageExpansion.Detach(); // drive the limits from the pure calculation only
            ResourceStorageExpansion.Apply(_resources, ResourceStorageExpansion.Calculate(_config, Owned(200, 0)));
            AssertCaps(7_400_000, 5_120_000, 2_300_000, 1_300_000);

            for (int n = 3; n <= 27; n++)
            {
                Refill();
                Assert.AreEqual(FlakConstructionResult.Started, BaseConstruction.TryStartFlakConstruction(b, _flak, _resources, n), "#" + n);
                BaseConstruction.GetConstructionQueue(b).Tick(10_000);
            }

            Refill();
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(b, _flak, _resources);
            Assert.AreEqual(28, q.flakNumber);
            Assert.AreEqual(FlakConstructionResult.Available, q.status);
            Assert.AreEqual(0, q.exceedsStorage.Count);

            // At 199 L5 bases #28 is short of storage (Concrete and Aluminium), with the exact shortfall.
            StorageCalculation c199 = ResourceStorageExpansion.Calculate(_config, Owned(199, 0));
            Assert.IsTrue(_resources.TrySpend(C, Bal(C) - c199.TargetCapacity(C) + 1));
            Assert.IsTrue(_resources.TrySpend(A, Bal(A) - c199.TargetCapacity(A) + 1));
            ResourceStorageExpansion.Apply(_resources, c199);
            Assert.IsTrue(c199.Applied(C) && c199.Applied(A));
            q = BaseConstruction.EvaluateFlakConstruction(b, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.StorageInsufficient, q.status);
            CollectionAssert.AreEquivalent(new[]
            {
                new KeyValuePair<ResourceType, long>(C, 7_389_300 - 7_369_500),
                new KeyValuePair<ResourceType, long>(A, 2_290_700 - 2_290_500),
            }, q.exceedsStorage);

            // Back at 200: construction starts and storage is never deducted.
            ResourceStorageExpansion.Apply(_resources, ResourceStorageExpansion.Calculate(_config, Owned(200, 0)));
            Refill();
            Assert.AreEqual(FlakConstructionResult.Started, BaseConstruction.TryStartFlakConstruction(b, _flak, _resources, 28));
            AssertCaps(7_400_000, 5_120_000, 2_300_000, 1_300_000, "capacity untouched by construction");
            Assert.AreEqual(7_400_000 - 7_389_300, Bal(C));
            Assert.AreEqual(5_120_000 - 5_098_600, Bal(I));
            Assert.AreEqual(2_300_000 - 2_290_700, Bal(A));
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
