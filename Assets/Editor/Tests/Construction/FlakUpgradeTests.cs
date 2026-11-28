using System.Collections.Generic;
using System.Linq;
using Ironveil.Economy;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironveil.Construction.Tests
{
    /// <summary>
    /// Edit-mode tests of the Flak Upgrade System (Stage 3): FlakUpgradeRules, the per-base
    /// BaseFlakDefence.FlakLevel, BaseConstruction.TryUpgradeFlaks / EvaluateFlakUpgrade and
    /// level inheritance of new Flaks. Every base is a temporary instance of a real foundation
    /// prefab, initialised through the production path (BaseConstruction.InitialiseBase), and
    /// the Gold comes from a real ResourceManager built from HomeBaseResourceConfig.asset.
    /// Nothing is saved; every object is destroyed in TearDown.
    /// </summary>
    public class FlakUpgradeTests
    {
        private const string TankFoundationPath = "Assets/Prefabs/BaseFoundations/Tank Foundation.prefab";
        private const string AirFoundationPath = "Assets/Prefabs/BaseFoundations/Air Foundation.prefab";
        private const string ConfigPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        private const float RuntimeBaseScale = 6.5f;

        private readonly List<Object> _created = new List<Object>();
        private ConstructionSettings _settings;
        private BuildingDefinition _flak;
        private ResourceManager _resources;

        /// <summary>Sloped ground so every Flak gets its own grounded height (not the slot height).</summary>
        private static bool SlopedGround(Vector3 p, out float h)
        {
            h = 2f + 0.05f * p.x + 0.03f * p.z;
            return true;
        }

        [SetUp]
        public void SetUp()
        {
            _settings = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath);
            Assert.IsNotNull(_settings, "Resources/IronveilConstructionSettings missing");
            Assert.IsTrue(_settings.IsComplete);
            _flak = _settings.BuildingCatalog.Get(BuildingType.Flak);
            Assert.IsNotNull(_flak);

            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath)), _resources.LastError);
            BaseFlakDefence.ReplacementFaultForTests = null;
        }

        [TearDown]
        public void TearDown()
        {
            BaseFlakDefence.ReplacementFaultForTests = null;
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include)) _created.Add(m.gameObject);
            foreach (BuildingInstance b in Object.FindObjectsByType<BuildingInstance>(FindObjectsInactive.Include))
                if (b.transform.parent == null) _created.Add(b.gameObject); // anything a failed test left detached
            foreach (Object o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        // ------------------------------------------------------------------ helpers

        private BaseController NewBase(string id, string foundationPath = TankFoundationPath, Vector3 position = default)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(foundationPath);
            Assert.IsNotNull(prefab, foundationPath);
            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _created.Add(root);
            root.transform.position = position;
            root.transform.localScale = Vector3.one * RuntimeBaseScale;
            BaseController controller = root.AddComponent<BaseController>();
            controller.InitializeIdentity(id, "Test Base " + id);
            Assert.AreEqual(BaseConstruction.InitResult.Initialised,
                BaseConstruction.InitialiseBase(controller, _settings, SlopedGround, out string error), error);
            return controller;
        }

        private static BaseFlakDefence Defence(BaseController c) => c.GetComponent<BaseFlakDefence>();

        private long Gold()
        {
            Assert.IsTrue(_resources.TryGetBalance(ResourceType.Gold, out long g));
            return g;
        }

        private void SetGold(long amount)
        {
            long now = Gold();
            if (now > amount) Assert.IsTrue(_resources.TrySpend(ResourceType.Gold, now - amount));
            Assert.AreEqual(amount, Gold());
        }

        private void FillTo(BaseController c, int count)
        {
            BaseFlakDefence d = Defence(c);
            while (d.ConstructedCount < count) Assert.IsNotNull(d.ConstructAtFlakLevel(_flak, null, SlopedGround), "construct");
        }

        private static List<BuildingSlot> FlakSlots(BaseFlakDefence d) =>
            d.Layout.InConstructionOrder().Select(p => d.GetSlot(p.slotId)).ToList();

        private struct FlakSnapshot
        {
            public BuildingSlot slot;
            public BuildingInstance instance;
            public Vector3 position;
            public Vector3 slotLocalPosition;
            public Quaternion slotLocalRotation;
            public Vector3 slotWorldPosition;
            public Quaternion slotWorldRotation;
            public Vector3 barrelWorldForward;
        }

        /// <summary>World direction the barrels point to: model forward with the level's facing correction undone.</summary>
        private Vector3 BarrelForward(BuildingInstance b)
        {
            GameObject prefab = _flak.GetPrefab(b.BuildingLevel);
            Quaternion model = b.transform.rotation * Quaternion.Inverse(prefab.transform.localRotation);
            return model * Quaternion.Euler(0f, -_flak.GetFacingYawOffset(b.BuildingLevel), 0f) * Vector3.forward;
        }

        private List<FlakSnapshot> Snapshot(BaseFlakDefence d)
        {
            var list = new List<FlakSnapshot>();
            foreach (BuildingSlot s in FlakSlots(d))
            {
                list.Add(new FlakSnapshot
                {
                    slot = s,
                    instance = s.Occupant,
                    position = s.IsOccupied ? s.Occupant.transform.position : Vector3.zero,
                    slotLocalPosition = s.transform.localPosition,
                    slotLocalRotation = s.transform.localRotation,
                    slotWorldPosition = s.transform.position,
                    slotWorldRotation = s.transform.rotation,
                    barrelWorldForward = s.IsOccupied ? BarrelForward(s.Occupant) : Vector3.zero,
                });
            }
            return list;
        }

        private static int InstancesUnder(BaseController c, BuildingType type) =>
            c.GetComponentsInChildren<BuildingInstance>(true).Count(b => b.Type == type);

        private void AssertAllFlaksAt(BaseController c, int level)
        {
            BaseFlakDefence d = Defence(c);
            Assert.AreEqual(level, d.FlakLevel);
            GameObject prefab = _flak.GetPrefab(level);
            foreach (BuildingSlot s in FlakSlots(d).Where(s => s.IsOccupied))
            {
                Assert.AreEqual(level, s.Occupant.BuildingLevel, s.SlotId);
                Assert.AreEqual(prefab.name, s.Occupant.name, s.SlotId);
                Assert.AreSame(s, s.Occupant.Slot, s.SlotId);
                Assert.AreSame(s.transform, s.Occupant.transform.parent, s.SlotId);
                Assert.IsTrue(s.Occupant.gameObject.activeSelf, s.SlotId);
            }
            Assert.AreEqual(d.ConstructedCount, InstancesUnder(c, BuildingType.Flak), "no leftover Flak objects under the base");
        }

        // ------------------------------------------------------------ rules / default

        [Test]
        public void Rules_CostsAndMaximumAreCentralised()
        {
            Assert.AreEqual(1, FlakUpgradeRules.MinLevel);
            Assert.AreEqual(4, FlakUpgradeRules.MaxLevel);
            Assert.AreEqual(1, FlakUpgradeRules.StartingLevel);
            Assert.IsTrue(FlakUpgradeRules.TryGetUpgradeCost(1, out long c1)); Assert.AreEqual(19, c1);
            Assert.IsTrue(FlakUpgradeRules.TryGetUpgradeCost(2, out long c2)); Assert.AreEqual(29, c2);
            Assert.AreEqual(39L, FlakUpgradeRules.GoldCostL3ToL4);
            Assert.IsTrue(FlakUpgradeRules.TryGetUpgradeCost(3, out long c3)); Assert.AreEqual(39, c3);
            for (int lv = 0; lv <= 5; lv++) Assert.IsFalse(FlakUpgradeRules.IsPricePending(lv), "every upgrade is priced");
            Assert.IsFalse(FlakUpgradeRules.TryGetUpgradeCost(4, out long c4)); Assert.AreEqual(0, c4);
            Assert.AreEqual(2, FlakUpgradeRules.NextLevel(1));
            Assert.AreEqual(3, FlakUpgradeRules.NextLevel(2));
            Assert.AreEqual(4, FlakUpgradeRules.NextLevel(3));
            Assert.AreEqual(0, FlakUpgradeRules.NextLevel(4));
            Assert.IsTrue(FlakUpgradeRules.CanUpgradeFrom(3));
            Assert.IsFalse(FlakUpgradeRules.CanUpgradeFrom(4));
            Assert.IsFalse(FlakUpgradeRules.IsValidLevel(5), "no fifth level");
            Assert.AreEqual(0, FlakUpgradeRules.NextLevel(5));
            Assert.AreEqual(1, FlakUpgradeRules.Sanitize(0));
            Assert.AreEqual(1, FlakUpgradeRules.Sanitize(-5));
            Assert.AreEqual(4, FlakUpgradeRules.Sanitize(9));
        }

        [Test]
        public void Rules_EveryUpgradeTargetHasARegisteredModel()
        {
            for (int level = FlakUpgradeRules.MinLevel; level <= FlakUpgradeRules.MaxLevel; level++)
                Assert.IsTrue(_flak.HasLevel(level), "Flak.asset has no model for L" + level);
            Assert.AreEqual(FlakUpgradeRules.MaxLevel, _flak.HighestAvailableLevel);
        }

        [Test]
        public void NewBase_StartsAtL1_AndUnassignedDefenceDefaultsToL1()
        {
            BaseController c = NewBase("A");
            Assert.AreEqual(1, Defence(c).FlakLevel);
            AssertAllFlaksAt(c, 1);
            Assert.AreEqual(2, Defence(c).ConstructedCount);

            var go = new GameObject("bare");
            _created.Add(go);
            Assert.AreEqual(1, go.AddComponent<BaseFlakDefence>().FlakLevel);
        }

        // ------------------------------------------------------------ A. progression

        [Test]
        public void Upgrade_L1ToL2_CostsExactly19Gold()
        {
            BaseController c = NewBase("A");
            long before = Gold();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(before - 19, Gold());
            AssertAllFlaksAt(c, 2);
        }

        [Test]
        public void Upgrade_L2ToL3_CostsExactly29Gold()
        {
            BaseController c = NewBase("A");
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            long before = Gold();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(before - 29, Gold());
            AssertAllFlaksAt(c, 3);
        }

        /// <summary>Buys L1 -> L2 -> L3 -> L4 for real.</summary>
        private void ForceL4(BaseController c)
        {
            for (int i = 0; i < 3; i++)
                Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(4, Defence(c).FlakLevel);
        }

        [Test]
        public void Upgrade_L3ToL4_CostsExactly39Gold()
        {
            BaseController c = NewBase("A");
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            FlakUpgradeQuote q = BaseConstruction.EvaluateFlakUpgrade(c, _flak, _resources);
            Assert.AreEqual(FlakUpgradeResult.Available, q.status);
            Assert.AreEqual(3, q.currentLevel);
            Assert.AreEqual(4, q.nextLevel);
            Assert.IsTrue(q.goldCostKnown);
            Assert.AreEqual(39, q.goldCost);
            long before = Gold();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources, expectedCurrentLevel: 3));
            Assert.AreEqual(before - 39, Gold());
            AssertAllFlaksAt(c, 4);
            Assert.AreEqual(FlakUpgradeResult.LevelChanged, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources, expectedCurrentLevel: 3), "stale duplicate");
            Assert.AreEqual(before - 39, Gold(), "charged once");
        }

        [Test]
        public void FullProgression_CostsExactly19Plus29Plus39()
        {
            BaseController c = NewBase("A");
            long start = Gold();
            ForceL4(c);
            Assert.AreEqual(start - 19 - 29 - 39, Gold());
            AssertAllFlaksAt(c, 4);
        }

        [Test]
        public void Upgrade_AtL4_IsRefused_MaxLevel_NothingChanges()
        {
            BaseController c = NewBase("A");
            ForceL4(c);
            AssertAllFlaksAt(c, 4);
            List<FlakSnapshot> before = Snapshot(Defence(c));
            long gold = Gold();

            FlakUpgradeQuote q = BaseConstruction.EvaluateFlakUpgrade(c, _flak, _resources);
            Assert.AreEqual(FlakUpgradeResult.AtMaximumLevel, q.status);
            Assert.AreEqual(4, q.currentLevel);
            Assert.AreEqual(0, q.nextLevel);
            Assert.AreEqual(0, q.goldCost);
            Assert.AreEqual(FlakUpgradeResult.AtMaximumLevel, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(gold, Gold());
            Assert.AreEqual(4, Defence(c).FlakLevel);
            Assert.IsFalse(Defence(c).ReplaceAllFlaks(_flak, 5, out _, out string error5), "no fifth level can be created");
            StringAssert.Contains("invalid Flak level 5", error5);
            Assert.AreEqual(4, Defence(c).FlakLevel);
            List<FlakSnapshot> after = Snapshot(Defence(c));
            for (int i = 0; i < before.Count; i++) Assert.AreSame(before[i].instance, after[i].instance);
        }

        [Test]
        public void Upgrade_L3ToL4_ReplacesEveryFlakWithTheL4Model()
        {
            BaseController c = NewBase("A");
            FillTo(c, 9);
            ForceL4(c);
            AssertAllFlaksAt(c, 4);
            Assert.AreEqual(9, Defence(c).ConstructedCount);
            Assert.AreEqual("mecha_tank_3d_model", _flak.GetPrefab(4).name, "L4 = the former L6 Base Turret model");
        }

        [Test]
        public void Quote_ReportsLevelCostBalanceAndCount()
        {
            BaseController c = NewBase("A");
            FlakUpgradeQuote q = BaseConstruction.EvaluateFlakUpgrade(c, _flak, _resources);
            Assert.AreEqual(FlakUpgradeResult.Available, q.status);
            Assert.IsTrue(q.CanUpgrade);
            Assert.AreEqual(1, q.currentLevel);
            Assert.AreEqual(2, q.nextLevel);
            Assert.AreEqual(19, q.goldCost);
            Assert.AreEqual(Gold(), q.goldBalance);
            Assert.AreEqual(2, q.flakCount);
        }

        [Test]
        public void DefaultOverload_UsesProjectDefinitionAndSharedResourceManager()
        {
            BaseController c = NewBase("A");
            Assert.AreSame(_resources, ResourceManager.Instance);
            long before = Gold();
            Assert.AreEqual(FlakUpgradeResult.Available, BaseConstruction.EvaluateFlakUpgrade(c).status);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c));
            Assert.AreEqual(before - 19, Gold());
            AssertAllFlaksAt(c, 2);
        }

        // ------------------------------------------------------------ B. collective

        [Test]
        public void Upgrade_ReplacesEveryFlakOnTheBase_Together()
        {
            BaseController c = NewBase("A");
            FillTo(c, 11);
            int refineries = InstancesUnder(c, BuildingType.FactoryRefinery);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(11, Defence(c).ConstructedCount);
            AssertAllFlaksAt(c, 2);
            Assert.AreEqual(refineries, InstancesUnder(c, BuildingType.FactoryRefinery), "non-Flak buildings untouched");
        }

        [Test]
        public void Upgrade_OfOneBase_DoesNotAffectAnotherBase()
        {
            BaseController a = NewBase("A");
            BaseController b = NewBase("B", AirFoundationPath, new Vector3(40f, 0f, 0f));
            FillTo(b, 5);
            List<FlakSnapshot> bBefore = Snapshot(Defence(b));

            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(a, _flak, _resources));
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(a, _flak, _resources));
            AssertAllFlaksAt(a, 3);
            AssertAllFlaksAt(b, 1);
            List<FlakSnapshot> bAfter = Snapshot(Defence(b));
            for (int i = 0; i < bBefore.Count; i++)
            {
                Assert.AreSame(bBefore[i].instance, bAfter[i].instance);
                Assert.AreEqual(bBefore[i].position, bAfter[i].position);
            }
            Assert.AreEqual(FlakUpgradeResult.Available, BaseConstruction.EvaluateFlakUpgrade(b, _flak, _resources).status);
            Assert.AreEqual(19, BaseConstruction.EvaluateFlakUpgrade(b, _flak, _resources).goldCost);
        }

        [Test]
        public void NewFlaks_InheritTheBaseLevel_ThroughTryConstructFlak()
        {
            BaseController c = NewBase("A");
            BuildingInstance l1 = BaseConstruction.TryConstructFlak(c);
            Assert.IsNotNull(l1); Assert.AreEqual(1, l1.BuildingLevel);

            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            BuildingInstance l2 = BaseConstruction.TryConstructFlak(c);
            Assert.IsNotNull(l2); Assert.AreEqual(2, l2.BuildingLevel);
            Assert.AreEqual(_flak.GetPrefab(2).name, l2.name);

            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            BuildingInstance l3 = BaseConstruction.TryConstructFlak(c);
            Assert.IsNotNull(l3); Assert.AreEqual(3, l3.BuildingLevel);
            Assert.AreEqual(_flak.GetPrefab(3).name, l3.name);
            AssertAllFlaksAt(c, 3);

            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            BuildingInstance l4 = BaseConstruction.TryConstructFlak(c);
            Assert.IsNotNull(l4); Assert.AreEqual(4, l4.BuildingLevel);
            Assert.AreEqual(_flak.GetPrefab(4).name, l4.name);
            AssertAllFlaksAt(c, 4);
            Assert.AreEqual(6, Defence(c).ConstructedCount);
        }

        [Test]
        public void NewFlaks_FollowTheApprovedConstructionOrder_AfterAnUpgrade()
        {
            BaseController a = NewBase("A");
            BaseController b = NewBase("B", TankFoundationPath, new Vector3(40f, 0f, 0f));
            BaseConstruction.TryUpgradeFlaks(a, _flak, _resources);
            for (int i = 0; i < 6; i++)
            {
                BuildingSlot nextA = Defence(a).NextFreeSlot(), nextB = Defence(b).NextFreeSlot();
                Assert.AreEqual(nextB.SlotId, nextA.SlotId, "same order at any level");
                Assert.AreSame(nextA, BaseConstruction.TryConstructFlak(a).Slot);
                Assert.AreSame(nextB, BaseConstruction.TryConstructFlak(b).Slot);
            }
        }

        [Test]
        public void Construct_WithALevelOtherThanTheBaseLevel_IsRefused()
        {
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            Assert.IsNull(d.Construct(_flak, 2), "L2 Flak on an L1 base");
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            Assert.IsNull(d.Construct(_flak, 1), "L1 Flak on an L2 base");
            Assert.IsNotNull(d.Construct(_flak, 2));
            AssertAllFlaksAt(c, 2);
        }

        // ------------------------------------------------------------ C. Gold

        [Test]
        public void InsufficientGold_PreventsTheUpgrade_AndChangesNothing()
        {
            BaseController c = NewBase("A");
            SetGold(18);
            List<FlakSnapshot> before = Snapshot(Defence(c));
            Assert.AreEqual(FlakUpgradeResult.InsufficientGold, BaseConstruction.EvaluateFlakUpgrade(c, _flak, _resources).status);
            Assert.AreEqual(FlakUpgradeResult.InsufficientGold, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(18, Gold());
            AssertAllFlaksAt(c, 1);
            List<FlakSnapshot> after = Snapshot(Defence(c));
            for (int i = 0; i < before.Count; i++) Assert.AreSame(before[i].instance, after[i].instance);
        }

        [Test]
        public void InsufficientGold_ForL3_AfterAffordableL2()
        {
            BaseController c = NewBase("A");
            SetGold(19 + 28);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(28, Gold());
            Assert.AreEqual(FlakUpgradeResult.InsufficientGold, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(28, Gold());
            AssertAllFlaksAt(c, 2);
        }

        [Test]
        public void ExactBalance_IsEnough_AndLeavesZero()
        {
            BaseController c = NewBase("A");
            SetGold(19);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(0, Gold());
        }

        [Test]
        public void GoldComesFromTheSharedBalance_TwoBasesBothCharged()
        {
            BaseController a = NewBase("A");
            BaseController b = NewBase("B", AirFoundationPath, new Vector3(40f, 0f, 0f));
            long start = Gold();
            BaseConstruction.TryUpgradeFlaks(a, _flak, _resources);
            BaseConstruction.TryUpgradeFlaks(b, _flak, _resources);
            BaseConstruction.TryUpgradeFlaks(b, _flak, _resources);
            Assert.AreEqual(start - 19 - 19 - 29, Gold());
        }

        [Test]
        public void NoResourceSystem_RefusesTheUpgrade()
        {
            BaseController c = NewBase("A");
            Assert.AreEqual(FlakUpgradeResult.NoResourceSystem, BaseConstruction.TryUpgradeFlaks(c, _flak, null));
            AssertAllFlaksAt(c, 1);
        }

        // ------------------------------------------------------------ D. placement

        [Test]
        public void All28Slots_StayUnchanged_ThroughAllThreeUpgrades()
        {
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            FillTo(c, 28);
            List<FlakSnapshot> before = Snapshot(d);
            Assert.AreEqual(28, before.Count);

            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            List<FlakSnapshot> after = Snapshot(d);
            Assert.AreEqual(28, d.ConstructedCount);
            Assert.AreEqual(0, d.FreeCount);
            for (int i = 0; i < 28; i++)
            {
                Assert.AreSame(before[i].slot, after[i].slot);
                Assert.AreEqual(before[i].slotLocalPosition, after[i].slotLocalPosition, before[i].slot.SlotId);
                Assert.AreEqual(before[i].slotLocalRotation, after[i].slotLocalRotation, before[i].slot.SlotId);
                Assert.AreEqual(before[i].slotWorldPosition, after[i].slotWorldPosition, before[i].slot.SlotId);
                Assert.AreEqual(before[i].slotWorldRotation, after[i].slotWorldRotation, before[i].slot.SlotId);
            }
            Assert.IsTrue(d.Bind(d.Layout, out string error), "slots still match the approved layout bit-exactly: " + error);
            Assert.IsNull(d.ConstructAtFlakLevel(_flak), "no 29th Flak");
            AssertAllFlaksAt(c, 4);
        }

        [Test]
        public void ReplacedFlaks_KeepExactPositionGroundingFacingAndScale()
        {
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            FillTo(c, 28);
            List<FlakSnapshot> l1 = Snapshot(d);
            // The sloped ground gives each Flak its own height, different from its slot height.
            Assert.IsTrue(l1.Any(s => Mathf.Abs(s.position.y - s.slotWorldPosition.y) > 0.01f), "grounding was applied");

            foreach (int level in new[] { 2, 3, 4 })
            {
                Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
                List<FlakSnapshot> now = Snapshot(d);
                GameObject prefab = _flak.GetPrefab(level);
                for (int i = 0; i < 28; i++)
                {
                    string id = now[i].slot.SlotId;
                    BuildingInstance b = now[i].instance;
                    Assert.AreNotSame(l1[i].instance, b, id + " replaced");
                    Assert.AreEqual(l1[i].position, now[i].position, id + " exact world position (grounded height)");
                    Assert.AreEqual(Quaternion.Euler(0f, _flak.GetFacingYawOffset(level), 0f) * prefab.transform.localRotation,
                        b.transform.localRotation, id + " slot facing x level facing correction");
                    Assert.AreEqual(prefab.transform.localScale * now[i].slot.InstanceScale, b.transform.localScale, id + " scale 0.05");
                    Assert.Less(Vector3.Angle(l1[i].barrelWorldForward, now[i].barrelWorldForward), 0.01f, id + " barrels still face outward");
                    Assert.AreEqual(i + 1, d.Layout.InConstructionOrder().FindIndex(p => p.slotId == id) + 1, id + " construction index");
                }
            }
        }

        // ------------------------------------------------------------ E. failures

        [Test]
        public void MissingNextLevelModel_RefusesWithoutSpending()
        {
            BaseController c = NewBase("A");
            BuildingDefinition onlyL1 = ScriptableObject.CreateInstance<BuildingDefinition>();
            _created.Add(onlyL1);
            onlyL1.EditorInitialize(BuildingType.Flak, "Flak", 0.05f, 28,
                new List<BuildingDefinition.LevelModel> { _flak.Levels[0] }, "test: L1 only");
            long gold = Gold();
            Assert.AreEqual(FlakUpgradeResult.ModelMissing, BaseConstruction.TryUpgradeFlaks(c, onlyL1, _resources));
            Assert.AreEqual(FlakUpgradeResult.ModelMissing, BaseConstruction.TryUpgradeFlaks(c, null, _resources));
            Assert.AreEqual(gold, Gold());
            AssertAllFlaksAt(c, 1);
        }

        [Test]
        public void BaseWithNoFlaks_IsRefused_AsDestroyed()
        {
            BaseController c = NewBase("A");
            while (BaseConstruction.TryDemolishFlak(c)) { }
            Assert.AreEqual(0, Defence(c).ConstructedCount);
            long gold = Gold();
            Assert.AreEqual(FlakUpgradeResult.NoFlaks, BaseConstruction.EvaluateFlakUpgrade(c, _flak, _resources).status);
            Assert.AreEqual(FlakUpgradeResult.NoFlaks, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(gold, Gold());
            Assert.AreEqual(1, Defence(c).FlakLevel);
        }

        [Test]
        public void InvalidBaseReferences_AreRefused()
        {
            long gold = Gold();
            Assert.AreEqual(FlakUpgradeResult.InvalidBase, BaseConstruction.TryUpgradeFlaks(null, _flak, _resources));

            var plain = new GameObject("plain base");
            _created.Add(plain);
            BaseController notFoundation = plain.AddComponent<BaseController>();
            Assert.AreEqual(FlakUpgradeResult.NotInitialised, BaseConstruction.TryUpgradeFlaks(notFoundation, _flak, _resources));

            var unbound = new GameObject("unbound defence");
            _created.Add(unbound);
            BaseController unboundController = unbound.AddComponent<BaseController>();
            unbound.AddComponent<BaseFlakDefence>();
            Assert.AreEqual(FlakUpgradeResult.NotInitialised, BaseConstruction.TryUpgradeFlaks(unboundController, _flak, _resources));

            BaseController destroyed = NewBase("GONE");
            Object.DestroyImmediate(destroyed.gameObject);
            Assert.AreEqual(FlakUpgradeResult.InvalidBase, BaseConstruction.TryUpgradeFlaks(destroyed, _flak, _resources));
            Assert.AreEqual(gold, Gold());
        }

        [TestCase(0)]
        [TestCase(4)]
        [TestCase(9)]
        public void FailedReplacement_RollsBackEverything_AndRefundsGold(int failingIndex)
        {
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            FillTo(c, 10);
            List<FlakSnapshot> before = Snapshot(d);
            string failingSlot = d.Layout.InConstructionOrder()[failingIndex].slotId;
            BaseFlakDefence.ReplacementFaultForTests = s => s.SlotId == failingSlot;
            long gold = Gold();
            int flakObjects = InstancesUnder(c, BuildingType.Flak);

            Assert.AreEqual(FlakUpgradeResult.ReplacementFailed, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));

            Assert.AreEqual(gold, Gold(), "Gold refunded");
            Assert.AreEqual(1, d.FlakLevel, "level unchanged");
            Assert.AreEqual(10, d.ConstructedCount);
            Assert.AreEqual(flakObjects, InstancesUnder(c, BuildingType.Flak), "no partial replacements left");
            Assert.IsFalse(d.IsUpgrading);
            List<FlakSnapshot> after = Snapshot(d);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.AreSame(before[i].instance, after[i].instance, before[i].slot.SlotId + " original restored");
                Assert.AreEqual(before[i].position, after[i].position, before[i].slot.SlotId);
                if (after[i].instance != null) Assert.IsTrue(after[i].instance.gameObject.activeSelf, before[i].slot.SlotId + " active again");
            }
            AssertAllFlaksAt(c, 1);

            BaseFlakDefence.ReplacementFaultForTests = null;
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources), "works once the fault is gone");
            Assert.AreEqual(gold - 19, Gold());
            AssertAllFlaksAt(c, 2);
        }

        [Test]
        public void FailedReplacement_AtL2_KeepsL2AndRefunds29()
        {
            BaseController c = NewBase("A");
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            BaseFlakDefence.ReplacementFaultForTests = s => true;
            long gold = Gold();
            Assert.AreEqual(FlakUpgradeResult.ReplacementFailed, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(gold, Gold());
            AssertAllFlaksAt(c, 2);
        }

        [Test]
        public void FailedReplacement_L3ToL4_RollsBackToL3_AndRefunds39()
        {
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            FillTo(c, 6);
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            List<FlakSnapshot> before = Snapshot(d);
            string failingSlot = d.Layout.InConstructionOrder()[3].slotId;
            BaseFlakDefence.ReplacementFaultForTests = s => s.SlotId == failingSlot;
            long gold = Gold();
            Assert.AreEqual(FlakUpgradeResult.ReplacementFailed, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(gold, Gold(), "39 Gold refunded");
            Assert.AreEqual(3, d.FlakLevel);
            Assert.IsFalse(d.IsUpgrading);
            List<FlakSnapshot> after = Snapshot(d);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.AreSame(before[i].instance, after[i].instance, before[i].slot.SlotId + " original L3 restored");
                Assert.AreEqual(before[i].position, after[i].position, before[i].slot.SlotId);
            }
            AssertAllFlaksAt(c, 3);
        }

        [Test]
        public void DuplicatePurchase_WithStaleExpectedLevel_IsRefused()
        {
            BaseController c = NewBase("A");
            long gold = Gold();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources, expectedCurrentLevel: 1));
            Assert.AreEqual(FlakUpgradeResult.LevelChanged, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources, expectedCurrentLevel: 1));
            Assert.AreEqual(gold - 19, Gold(), "charged once");
            AssertAllFlaksAt(c, 2);
        }

        [Test]
        public void ReentrantPurchase_DuringAnUpgrade_IsRefused()
        {
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            long gold = Gold();
            var inner = new List<FlakUpgradeResult>();
            bool constructRefused = false, demolishRefused = false;
            System.Action<BaseFlakDefence> handler = changed =>
            {
                if (changed != d) return;
                inner.Add(BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
                constructRefused = d.ConstructAtFlakLevel(_flak) == null;
                demolishRefused = !d.Demolish(d.LatestConstructedSlot().SlotId);
            };
            BaseFlakDefence.Changed += handler;
            try
            {
                Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            }
            finally
            {
                BaseFlakDefence.Changed -= handler;
            }

            CollectionAssert.AreEqual(new[] { FlakUpgradeResult.UpgradeInProgress }, inner);
            Assert.IsTrue(constructRefused, "no construction mid-upgrade");
            Assert.IsTrue(demolishRefused, "no demolition mid-upgrade");
            Assert.IsFalse(d.IsUpgrading);
            Assert.AreEqual(gold - 19, Gold());
            AssertAllFlaksAt(c, 2);
            Assert.AreEqual(2, d.ConstructedCount);
        }

        [Test]
        public void SuccessfulUpgrade_RaisesChangedOnce_ForThatBaseOnly()
        {
            BaseController a = NewBase("A");
            BaseController b = NewBase("B", TankFoundationPath, new Vector3(40f, 0f, 0f));
            var raised = new List<BaseFlakDefence>();
            System.Action<BaseFlakDefence> handler = raised.Add;
            BaseFlakDefence.Changed += handler;
            try
            {
                BaseConstruction.TryUpgradeFlaks(a, _flak, _resources);
                SetGold(0);
                BaseConstruction.TryUpgradeFlaks(b, _flak, _resources); // refused: no Gold
            }
            finally
            {
                BaseFlakDefence.Changed -= handler;
            }

            Assert.AreEqual(1, raised.Count);
            Assert.AreSame(Defence(a), raised[0]);
        }
    }
}
