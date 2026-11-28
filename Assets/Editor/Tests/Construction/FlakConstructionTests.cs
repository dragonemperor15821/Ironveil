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
    /// Edit-mode tests of queued Flak construction (Stage 5, cost revision 2026-11-28):
    /// FlakConstructionRules (approved #2..#28 table, #2 conditional rebuild, one-base storage tiers),
    /// BaseConstruction.EvaluateFlakConstruction / TryStartFlakConstruction /
    /// TryCancelFlakConstruction, BaseFlakConstruction (queue + timer, driven with Tick) and the
    /// slot reservations in BaseFlakDefence. Bases are temporary instances of the real foundation
    /// prefabs initialised through BaseConstruction.InitialiseBase. Resources come from a real
    /// ResourceManager: the project's HomeBaseResourceConfig (one Home Base: Concrete 1,300,000,
    /// Iron 900,000, Aluminium 400,000, which holds #3..#12 but not #13..#28), a config with
    /// multiplied capacities standing in for storage expanded by additional bases, or very large
    /// capacities where a test needs all 28 Flaks quickly.
    /// </summary>
    public class FlakConstructionTests
    {
        private const string ConfigPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        private const string FoundationDir = "Assets/Prefabs/BaseFoundations/";
        private static readonly string[] NineTypes = { "Tank", "Heli", "Air", "Naval", "Rail", "Heli Naval", "Heli Rail", "Air Naval", "Air Rail" };
        private const long Plenty = 1_000_000_000_000L;

        private readonly List<Object> _created = new List<Object>();
        private ConstructionSettings _settings;
        private BuildingDefinition _flak;
        private ResourceManager _resources;

        [SetUp]
        public void SetUp()
        {
            _settings = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath);
            _flak = _settings.BuildingCatalog.Get(BuildingType.Flak);
            BaseFlakDefence.ReplacementFaultForTests = null;
        }

        [TearDown]
        public void TearDown()
        {
            BaseFlakDefence.ReplacementFaultForTests = null;
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include)) _created.Add(m.gameObject);
            foreach (BuildingInstance b in Object.FindObjectsByType<BuildingInstance>(FindObjectsInactive.Include))
                if (b.transform.parent == null) _created.Add(b.gameObject);
            foreach (Object o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        // ------------------------------------------------------------------ helpers

        private void UseProjectResources()
        {
            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath)), _resources.LastError);
        }

        /// <summary>The project's config with Concrete, Iron and Aluminium made effectively unlimited (no replenishment).</summary>
        private void UsePlentifulResources()
        {
            var project = AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath);
            var entries = project.Entries.Select(e =>
                e.Resource == ResourceType.Concrete || e.Resource == ResourceType.Iron || e.Resource == ResourceType.Aluminium
                    ? new ResourceConfigEntry(e.Resource, Plenty, true, Plenty, false, 0, 0)
                    : e).ToList();
            HomeBaseResourceConfig config = HomeBaseResourceConfig.CreateRuntime(entries);
            _created.Add(config);
            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(config), _resources.LastError);
        }

        private BaseController NewBase(string id, string type = "Tank", Vector3 position = default)
        {
            string path = FoundationDir + type + " Foundation.prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(prefab, path);
            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            _created.Add(root);
            root.transform.position = position;
            root.transform.localScale = Vector3.one * 6.5f;
            BaseController c = root.AddComponent<BaseController>();
            c.InitializeIdentity(id, type + " Base");
            Assert.AreEqual(BaseConstruction.InitResult.Initialised, BaseConstruction.InitialiseBase(c, _settings, null, out string error), error);
            return c;
        }

        private static BaseFlakDefence Defence(BaseController c) => c.GetComponent<BaseFlakDefence>();
        private static BaseFlakConstruction Queue(BaseController c) => BaseConstruction.GetConstructionQueue(c);

        private FlakConstructionResult Start(BaseController c, int expected = 0) =>
            BaseConstruction.TryStartFlakConstruction(c, _flak, _resources, expected);

        private long Bal(ResourceType r)
        {
            Assert.IsTrue(_resources.TryGetBalance(r, out long b));
            return b;
        }

        private (long c, long i, long a) Balances() => (Bal(ResourceType.Concrete), Bal(ResourceType.Iron), Bal(ResourceType.Aluminium));

        private static FlakConstructionCost Cost(int n)
        {
            Assert.IsTrue(FlakConstructionRules.TryGetCost(n, out FlakConstructionCost c));
            return c;
        }

        private static List<string> Order(BaseFlakDefence d) => d.Layout.InConstructionOrder().Select(p => p.slotId).ToList();

        private static void AssertAllFlaksAt(BaseController c, int level)
        {
            BaseFlakDefence d = Defence(c);
            Assert.AreEqual(level, d.FlakLevel);
            BuildingInstance[] flaks = c.GetComponentsInChildren<BuildingInstance>(true).Where(b => b.Type == BuildingType.Flak).ToArray();
            Assert.AreEqual(d.ConstructedCount, flaks.Length, "no stray Flak objects");
            foreach (BuildingInstance f in flaks)
            {
                Assert.AreEqual(level, f.BuildingLevel, f.Slot.SlotId);
                Assert.AreSame(f, f.Slot.Occupant);
                Assert.AreSame(f.Slot.transform, f.transform.parent);
            }
        }

        // ------------------------------------------------------------------ 5/6. approved cost table

        // Independent copy of the APPROVED table (2026-11-28): { flak, concrete, iron, aluminium }.
        private static readonly long[][] Approved =
        {
            new long[] {  2,    10_000,     6_900,     3_100 },
            new long[] {  3,    20_000,    13_800,     6_200 },
            new long[] {  4,    31_500,    21_700,     9_800 },
            new long[] {  5,    49_700,    34_300,    15_400 },
            new long[] {  6,    78_300,    54_000,    24_300 },
            new long[] {  7,   123_400,    85_100,    38_300 },
            new long[] {  8,   194_500,   134_200,    60_300 },
            new long[] {  9,   306_500,   211_500,    95_000 },
            new long[] { 10,   483_100,   333_300,   149_800 },
            new long[] { 11,   761_400,   525_400,   236_000 },
            new long[] { 12, 1_200_000,   828_000,   372_000 },
            new long[] { 13, 1_350_000,   931_500,   418_500 },
            new long[] { 14, 1_512_000, 1_043_300,   468_700 },
            new long[] { 15, 1_693_400, 1_168_400,   525_000 },
            new long[] { 16, 1_896_700, 1_308_700,   588_000 },
            new long[] { 17, 2_124_300, 1_465_800,   658_500 },
            new long[] { 18, 2_379_200, 1_641_600,   737_600 },
            new long[] { 19, 2_664_700, 1_838_600,   826_100 },
            new long[] { 20, 2_984_400, 2_059_200,   925_200 },
            new long[] { 21, 3_342_600, 2_306_400, 1_036_200 },
            new long[] { 22, 3_743_700, 2_583_200, 1_160_500 },
            new long[] { 23, 4_192_900, 2_893_100, 1_299_800 },
            new long[] { 24, 4_696_000, 3_240_200, 1_455_800 },
            new long[] { 25, 5_259_600, 3_629_100, 1_630_500 },
            new long[] { 26, 5_890_700, 4_064_600, 1_826_100 },
            new long[] { 27, 6_597_600, 4_552_300, 2_045_300 },
            new long[] { 28, 7_389_300, 5_098_600, 2_290_700 },
        };

        private static IEnumerable<TestCaseData> ApprovedCases() =>
            Approved.Select(r => new TestCaseData((int)r[0], r[1], r[2], r[3]).SetName($"ApprovedCost_Flak{r[0]:00}"));

        [TestCaseSource(nameof(ApprovedCases))]
        public void ApprovedCost_EveryFlakFrom2To28_MatchesTheTableExactly(int n, long concrete, long iron, long aluminium)
        {
            FlakConstructionCost c = Cost(n);
            Assert.AreEqual(n, c.flakNumber);
            Assert.AreEqual(concrete, c.concrete, "#" + n + " Concrete");
            Assert.AreEqual(iron, c.iron, "#" + n + " Iron");
            Assert.AreEqual(aluminium, c.aluminium, "#" + n + " Aluminium");
            Assert.AreEqual(0, c.Amount(ResourceType.Fuel));
            Assert.AreEqual(0, c.Amount(ResourceType.Gold));
        }

        [Test]
        public void ApprovedTable_Covers2To28Only_StrictlyRising_TimesUnchanged()
        {
            Assert.AreEqual(27, Approved.Length);
            Assert.IsFalse(FlakConstructionRules.TryGetCost(0, out _));
            Assert.IsFalse(FlakConstructionRules.TryGetCost(1, out _), "#1 is never built: a base with no Flak has fallen");
            Assert.IsFalse(FlakConstructionRules.TryGetCost(29, out _));
            Assert.AreEqual(28, FlakConstructionRules.MaxFlaks);
            for (int n = 3; n <= 28; n++)
            {
                Assert.Greater(Cost(n).concrete, Cost(n - 1).concrete, "#" + n);
                Assert.Greater(Cost(n).iron, Cost(n - 1).iron, "#" + n);
                Assert.Greater(Cost(n).aluminium, Cost(n - 1).aluminium, "#" + n);
                // Build times are not part of the revision: 360 s + 20.8 s x (n - 3), rounded.
                Assert.AreEqual(Mathf.RoundToInt(360f + 20.8f * (n - 3)), Cost(n).durationSeconds, "#" + n);
            }
            Assert.AreEqual(360, Cost(2).durationSeconds, "the #2 rebuild keeps the #3 time");
            Assert.AreEqual(360, Cost(3).durationSeconds);
            Assert.AreEqual(381, Cost(4).durationSeconds);
            Assert.AreEqual(859, Cost(27).durationSeconds);
            Assert.AreEqual(880, Cost(28).durationSeconds);
            Assert.AreEqual(16_120, FlakConstructionRules.TotalSeconds(), "4 h 28 m 40 s, unchanged");
        }

        // ------------------------------------------------------------------ storage limits

        [Test]
        public void OneBaseStorage_Concrete1300000_Iron900000_Aluminium400000_Fuel100000()
        {
            UseProjectResources();
            Assert.IsTrue(_resources.TryGetCapacity(ResourceType.Concrete, out long c)); Assert.AreEqual(1_300_000, c);
            Assert.IsTrue(_resources.TryGetCapacity(ResourceType.Iron, out long i)); Assert.AreEqual(900_000, i);
            Assert.IsTrue(_resources.TryGetCapacity(ResourceType.Aluminium, out long a)); Assert.AreEqual(400_000, a);
            Assert.IsTrue(_resources.TryGetCapacity(ResourceType.Fuel, out long f)); Assert.AreEqual(100_000, f);
        }

        [Test]
        public void Tiers_3To12FitOneBaseStorage_13To28ExceedAtLeastOneLimit()
        {
            UseProjectResources();
            _resources.TryGetCapacity(ResourceType.Concrete, out long capC);
            _resources.TryGetCapacity(ResourceType.Iron, out long capI);
            _resources.TryGetCapacity(ResourceType.Aluminium, out long capA);
            for (int n = 3; n <= 12; n++)
            {
                FlakConstructionCost k = Cost(n);
                Assert.IsTrue(k.concrete <= capC && k.iron <= capI && k.aluminium <= capA, "#" + n + " fits one Home Base");
                Assert.IsFalse(FlakConstructionRules.RequiresExpandedStorage(n), "#" + n);
            }
            for (int n = 13; n <= 28; n++)
            {
                FlakConstructionCost k = Cost(n);
                Assert.IsTrue(k.concrete > capC || k.iron > capI || k.aluminium > capA, "#" + n + " exceeds a one-base limit");
                Assert.IsTrue(FlakConstructionRules.RequiresExpandedStorage(n), "#" + n);
            }
            Assert.AreEqual(12, FlakConstructionRules.LastOneBaseFlak);
            Assert.AreEqual(13, FlakConstructionRules.FirstExpandedStorageFlak);
        }

        /// <summary>Tops the three construction resources up to capacity (stands in for replenishment over time).</summary>
        private void Refill()
        {
            foreach (ResourceType r in new[] { ResourceType.Concrete, ResourceType.Iron, ResourceType.Aluminium })
            {
                Assert.IsTrue(_resources.TryGetCapacity(r, out long cap));
                long room = cap - Bal(r);
                if (room > 0) Assert.IsTrue(_resources.Ledger.TryAdd(r, room, out _));
            }
        }

        [Test]
        public void OneHomeBase_Builds3To12_Then13IsRefusedForStorage_NothingSpentOrReserved()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            for (int n = 3; n <= 12; n++)
            {
                Refill();
                var before = Balances();
                FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
                Assert.AreEqual(n, q.flakNumber);
                Assert.AreEqual(FlakConstructionResult.Available, q.status, "#" + n);
                Assert.AreEqual(FlakConstructionResult.Started, Start(c, n), "#" + n);
                Assert.AreEqual((before.c - Cost(n).concrete, before.i - Cost(n).iron, before.a - Cost(n).aluminium), Balances(), "#" + n + " exact deduction");
                Assert.AreEqual(1, Queue(c).Tick(Cost(n).durationSeconds), "#" + n + " completes on time");
                Assert.AreEqual(n, d.ConstructedCount);
            }

            Refill();
            var full = Balances();
            FlakConstructionQuote q13 = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(13, q13.flakNumber);
            Assert.AreEqual(FlakConstructionResult.StorageInsufficient, q13.status, "even with full storage #13 cannot be paid");
            CollectionAssert.AreEquivalent(new[]
            {
                new KeyValuePair<ResourceType, long>(ResourceType.Concrete, 50_000),
                new KeyValuePair<ResourceType, long>(ResourceType.Iron, 31_500),
                new KeyValuePair<ResourceType, long>(ResourceType.Aluminium, 18_500),
            }, q13.exceedsStorage);
            Assert.AreEqual(FlakConstructionResult.StorageInsufficient, Start(c));
            Assert.AreEqual(full, Balances(), "capacity is compared, never deducted; nothing spent");
            Assert.AreEqual(0, d.ReservedCount);
            Assert.AreEqual(0, Queue(c).Count);
            Assert.AreEqual(12, d.ConstructedCount);
        }

        /// <summary>A config whose storage capacities are <paramref name="factor"/> x the project's (stands in for additional bases), starting full.</summary>
        private void UseExpandedStorage(long factor)
        {
            var project = AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath);
            var entries = project.Entries.Select(e =>
                e.Resource == ResourceType.Concrete || e.Resource == ResourceType.Iron || e.Resource == ResourceType.Aluminium
                    ? new ResourceConfigEntry(e.Resource, e.Capacity * factor, true, e.Capacity * factor, false, 0, 0)
                    : e).ToList();
            HomeBaseResourceConfig config = HomeBaseResourceConfig.CreateRuntime(entries);
            _created.Add(config);
            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(config), _resources.LastError);
        }

        [Test]
        public void ExpandedStorage_Allows13_AndEveryHigherFlakOnceItsCostFits()
        {
            UseExpandedStorage(2); // 2.6M / 1.8M / 800k
            BaseController c = NewBase("A");
            for (int n = 3; n <= 12; n++) { Refill(); Assert.AreEqual(FlakConstructionResult.Started, Start(c, n)); Queue(c).Tick(10_000); }
            Refill();
            var before = Balances();
            Assert.AreEqual(FlakConstructionResult.Started, Start(c, 13), "#13 fits doubled storage");
            Assert.AreEqual((before.c - 1_350_000, before.i - 931_500, before.a - 418_500), Balances());
            Queue(c).Tick(10_000);
            for (int n = 14; n <= 18; n++) { Refill(); Assert.AreEqual(FlakConstructionResult.Started, Start(c, n), "#" + n); Queue(c).Tick(10_000); }
            Refill();
            FlakConstructionQuote q19 = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(19, q19.flakNumber);
            Assert.AreEqual(FlakConstructionResult.StorageInsufficient, q19.status, "#19 exceeds doubled storage");
            CollectionAssert.AreEquivalent(new[]
            {
                new KeyValuePair<ResourceType, long>(ResourceType.Concrete, 2_664_700 - 2_600_000),
                new KeyValuePair<ResourceType, long>(ResourceType.Iron, 1_838_600 - 1_800_000),
                new KeyValuePair<ResourceType, long>(ResourceType.Aluminium, 826_100 - 800_000),
            }, q19.exceedsStorage);
            Assert.AreEqual(18, Defence(c).ConstructedCount);
        }

        [Test]
        public void ExpandedStorage_All28CanBeBuilt_WhenStorageCoversTheLargestCost()
        {
            UseExpandedStorage(6); // 7.8M / 5.4M / 2.4M covers #28 (7,389,300 / 5,098,600 / 2,290,700)
            BaseController c = NewBase("A");
            for (int n = 3; n <= 28; n++) { Refill(); Assert.AreEqual(FlakConstructionResult.Started, Start(c, n), "#" + n); Queue(c).Tick(10_000); }
            Assert.AreEqual(28, Defence(c).ConstructedCount);
            Assert.AreEqual(FlakConstructionResult.MaxCapacity, BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources).status, "never a 29th");
        }

        [Test]
        public void StorageFits_ButBalanceShort_IsInsufficientResources()
        {
            UseExpandedStorage(2);
            BaseController c = NewBase("A");
            for (int n = 3; n <= 12; n++) { Refill(); Start(c, n); Queue(c).Tick(10_000); }
            // Storage fits #13 but the balance does not: InsufficientResources, not StorageInsufficient.
            Assert.IsTrue(_resources.TrySpend(ResourceType.Concrete, Bal(ResourceType.Concrete) - 1_000_000));
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.InsufficientResources, q.status);
            Assert.AreEqual(0, q.exceedsStorage.Count);
            CollectionAssert.AreEquivalent(new[] { new KeyValuePair<ResourceType, long>(ResourceType.Concrete, 350_000) }, q.missing);
        }

        [Test]
        public void StorageInsufficient_IsReportedBeforeTheBalanceShortfall()
        {
            // One-base storage and a drained balance: storage is the reason reported, the shortfall is still listed.
            UseProjectResources();
            BaseController b = NewBase("B", "Air", new Vector3(40f, 0f, 0f));
            for (int n = 3; n <= 12; n++) { Refill(); Start(b, n); Queue(b).Tick(10_000); }
            FlakConstructionQuote qb = BaseConstruction.EvaluateFlakConstruction(b, _flak, _resources);
            Assert.AreEqual(13, qb.flakNumber);
            Assert.AreEqual(FlakConstructionResult.StorageInsufficient, qb.status);
            Assert.IsTrue(qb.missing.Count > 0, "balance shortfall is also computed");
        }

        // ------------------------------------------------------------------ #2 conditional rebuild

        [Test]
        public void Flak2_IsNeverAnOrdinaryExpansion()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(2, q.builtCount, "every base starts with two L1 Flaks");
            Assert.AreEqual(3, q.flakNumber, "the first purchase is #3");
            Assert.IsFalse(q.isRebuild);
            Assert.IsTrue(FlakConstructionRules.IsRebuild(2));
            Assert.IsTrue(FlakConstructionRules.IsRebuildEligible(1, 0));
            Assert.IsFalse(FlakConstructionRules.IsRebuildEligible(2, 0));
            Assert.IsFalse(FlakConstructionRules.IsRebuildEligible(1, 1), "with a construction queued the next one is #3");
            Assert.IsFalse(FlakConstructionRules.IsRebuildEligible(0, 0), "a base with no Flak has fallen");
        }

        [Test]
        public void Flak2_RebuildAfterAttackReducesToOne_UsesQueueAndPlacement_AndCostsExactly()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            string lost = Order(d)[1];
            Assert.IsTrue(BaseConstruction.TryDemolishFlak(c), "an attack destroys one Flak");
            Assert.AreEqual(1, d.ConstructedCount);

            var before = Balances();
            long gold = Bal(ResourceType.Gold), fuel = Bal(ResourceType.Fuel);
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.Available, q.status);
            Assert.AreEqual(2, q.flakNumber);
            Assert.IsTrue(q.isRebuild);
            Assert.AreEqual(10_000, q.cost.concrete); Assert.AreEqual(6_900, q.cost.iron); Assert.AreEqual(3_100, q.cost.aluminium);

            Assert.AreEqual(FlakConstructionResult.Started, Start(c, 2));
            Assert.AreEqual((before.c - 10_000, before.i - 6_900, before.a - 3_100), Balances(), "exact deduction");
            Assert.AreEqual(gold, Bal(ResourceType.Gold)); Assert.AreEqual(fuel, Bal(ResourceType.Fuel));
            BaseFlakConstruction queue = Queue(c);
            Assert.AreEqual(1, queue.Count, "normal construction queue");
            Assert.AreEqual(2, queue.Current.FlakNumber);
            Assert.AreEqual(lost, queue.Current.SlotId, "the lost position is the next free one in the approved order");
            Assert.IsTrue(d.IsReserved(lost));

            FlakConstructionQuote next = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(3, next.flakNumber, "once #2 is queued the next purchase is the ordinary #3");
            Assert.IsFalse(next.isRebuild);

            Assert.AreEqual(1, queue.Tick(360));
            Assert.AreEqual(2, d.ConstructedCount);
            Assert.IsNotNull(d.GetSlot(lost).Occupant);
            Assert.IsFalse(BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources).isRebuild);
        }

        [Test]
        public void Flak2_RebuildCancellation_RefundsExactly()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseConstruction.TryDemolishFlak(c);
            Assert.IsTrue(_resources.TrySpend(ResourceType.Concrete, 500_000)); // room for the refund
            Assert.IsTrue(_resources.TrySpend(ResourceType.Iron, 500_000));
            Assert.IsTrue(_resources.TrySpend(ResourceType.Aluminium, 200_000));
            var before = Balances();
            Assert.AreEqual(FlakConstructionResult.Started, Start(c, 2));
            Assert.AreEqual((before.c - 10_000, before.i - 6_900, before.a - 3_100), Balances());
            Assert.IsTrue(BaseConstruction.TryCancelFlakConstruction(c, -1, _resources));
            Assert.AreEqual(before, Balances());
            Assert.AreEqual(0, Defence(c).ReservedCount);
            Assert.AreEqual(2, BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources).flakNumber, "still eligible after cancelling");
        }

        [Test]
        public void Flak2_InsufficientResources_Refused()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseConstruction.TryDemolishFlak(c);
            Assert.IsTrue(_resources.TrySpend(ResourceType.Iron, Bal(ResourceType.Iron) - 6_899));
            var before = Balances();
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.InsufficientResources, q.status);
            CollectionAssert.AreEquivalent(new[] { new KeyValuePair<ResourceType, long>(ResourceType.Iron, 1) }, q.missing);
            Assert.AreEqual(FlakConstructionResult.InsufficientResources, Start(c, 2));
            Assert.AreEqual(before, Balances());
            Assert.AreEqual(0, Defence(c).ReservedCount);
        }

        [Test]
        public void AllNineBaseVariants_ApplyTheSameCostsAndRules()
        {
            UseProjectResources();
            for (int t = 0; t < NineTypes.Length; t++)
            {
                BaseController c = NewBase("W" + t, NineTypes[t], new Vector3(60f * t, 0f, 0f));
                Refill();
                FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
                Assert.AreEqual(3, q.flakNumber, NineTypes[t]);
                Assert.AreEqual(20_000, q.cost.concrete, NineTypes[t]);
                Assert.AreEqual(13_800, q.cost.iron, NineTypes[t]);
                Assert.AreEqual(6_200, q.cost.aluminium, NineTypes[t]);
                Assert.IsTrue(BaseConstruction.TryDemolishFlak(c), NineTypes[t]);
                q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
                Assert.AreEqual(2, q.flakNumber, NineTypes[t]);
                Assert.IsTrue(q.isRebuild, NineTypes[t]);
                Assert.AreEqual(FlakConstructionResult.Available, q.status, NineTypes[t]);
                Assert.AreEqual(10_000, q.cost.concrete, NineTypes[t]);
                Assert.AreEqual(1, Defence(c).ConstructedCount, NineTypes[t]);
            }
        }


        [Test]
        public void Schedule_ChargesNoGoldOrFuel_AndUpgradePricesAreUnchanged()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            long gold = Bal(ResourceType.Gold), fuel = Bal(ResourceType.Fuel);
            Assert.AreEqual(FlakConstructionResult.Started, Start(c));
            Assert.AreEqual(gold, Bal(ResourceType.Gold));
            Assert.AreEqual(fuel, Bal(ResourceType.Fuel));
            Assert.AreEqual(19, FlakUpgradeRules.GoldCostL1ToL2);
            Assert.AreEqual(29, FlakUpgradeRules.GoldCostL2ToL3);
            Assert.AreEqual(39L, FlakUpgradeRules.GoldCostL3ToL4);
        }

        // ------------------------------------------------------------------ 1. third Flak

        [Test]
        public void ThirdFlak_PaysReservesQueuesAndCompletesAfter360s()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            var before = Balances();
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.Available, q.status);
            Assert.AreEqual(3, q.flakNumber);
            Assert.AreEqual(2, q.builtCount);
            Assert.AreEqual(0, q.queuedCount);

            Assert.AreEqual(FlakConstructionResult.Started, Start(c));
            Assert.AreEqual((before.c - 20_000, before.i - 13_800, before.a - 6_200), Balances(), "exact #3 deduction");
            BaseFlakConstruction queue = Queue(c);
            Assert.AreEqual(1, queue.Count);
            string slot = queue.Current.SlotId;
            Assert.AreEqual(Order(d)[2], slot, "next position in the approved order");
            Assert.IsTrue(d.IsReserved(slot));
            Assert.IsFalse(d.GetSlot(slot).IsOccupied);
            Assert.AreEqual(2, d.ConstructedCount);
            Assert.AreEqual(1, d.ReservedCount);
            Assert.AreEqual(25, d.AvailableCount);
            Assert.AreEqual(360.0, queue.CurrentRemainingSeconds, 1e-9);

            Assert.AreEqual(0, queue.Tick(359.5));
            Assert.AreEqual(0.5, queue.CurrentRemainingSeconds, 1e-9);
            Assert.IsFalse(d.GetSlot(slot).IsOccupied, "not built before its time");
            Assert.AreEqual(1, queue.Tick(0.5));
            Assert.AreEqual(0, queue.Count);
            Assert.AreEqual(3, d.ConstructedCount);
            Assert.IsFalse(d.IsReserved(slot));
            BuildingInstance built = d.GetSlot(slot).Occupant;
            Assert.IsNotNull(built);
            Assert.AreEqual(1, built.BuildingLevel);
            Assert.AreEqual(_flak.GetPrefab(1).name, built.name);
            Assert.AreEqual(4, BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources).flakNumber);
        }

        // ------------------------------------------------------------------ 2/6. multiple, sequential timing

        [Test]
        public void MultipleFlaks_QueueOneAfterAnother_EachWithItsOwnPriceSlotAndTime()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            var before = Balances();
            for (int i = 0; i < 3; i++) Assert.AreEqual(FlakConstructionResult.Started, Start(c));
            BaseFlakConstruction queue = Queue(c);
            CollectionAssert.AreEqual(new[] { 3, 4, 5 }, queue.Queue.Select(o => o.FlakNumber).ToArray());
            CollectionAssert.AreEqual(Order(d).Skip(2).Take(3).ToArray(), queue.Queue.Select(o => o.SlotId).ToArray());
            Assert.AreEqual(3, queue.Queue.Select(o => o.SlotId).Distinct().Count(), "never two constructions in one slot");
            long concrete = Cost(3).concrete + Cost(4).concrete + Cost(5).concrete;
            long iron = Cost(3).iron + Cost(4).iron + Cost(5).iron;
            long aluminium = Cost(3).aluminium + Cost(4).aluminium + Cost(5).aluminium;
            Assert.AreEqual((before.c - concrete, before.i - iron, before.a - aluminium), Balances());
            Assert.AreEqual((before.c - 101_200, before.i - 69_800, before.a - 31_400), Balances(), "20,000 + 31,500 + 49,700 ...");
            Assert.AreEqual(360 + 381 + 402, queue.QueueRemainingSeconds, 1e-9);

            Assert.AreEqual(1, queue.Tick(360));
            Assert.AreEqual(0.0, queue.Queue[1].ElapsedSeconds, 1e-9, "only the first order progresses");
            Assert.AreEqual(381.0, queue.CurrentRemainingSeconds, 1e-9);
            Assert.AreEqual(2, queue.Tick(381 + 402 + 1000), "left-over time carries into the next order");
            Assert.AreEqual(5, d.ConstructedCount);
            Assert.AreEqual(0, d.ReservedCount);
            AssertAllFlaksAt(c, 1);
        }

        // ------------------------------------------------------------------ 3/9. order and capacity

        [Test]
        public void ConstructionOrder_AndCapacity28_IncludingPending()
        {
            UsePlentifulResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            for (int i = 0; i < 26; i++) Assert.AreEqual(FlakConstructionResult.Started, Start(c), "construction " + (i + 3));
            BaseFlakConstruction queue = Queue(c);
            CollectionAssert.AreEqual(Order(d).Skip(2).ToArray(), queue.Queue.Select(o => o.SlotId).ToArray(), "approved order: inner rows first, right then left");
            CollectionAssert.AreEqual(Enumerable.Range(3, 26).ToArray(), queue.Queue.Select(o => o.FlakNumber).ToArray());
            Assert.AreEqual(2, d.ConstructedCount);
            Assert.AreEqual(26, d.ReservedCount);
            Assert.AreEqual(0, d.AvailableCount);

            var before = Balances();
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.MaxCapacity, q.status, "pending constructions count toward 28");
            Assert.AreEqual(FlakConstructionResult.MaxCapacity, Start(c));
            Assert.AreEqual(before, Balances(), "nothing spent at capacity");
            Assert.IsNull(BaseConstruction.TryConstructFlak(c), "an instant build cannot take a reserved position");
            Assert.IsNull(d.Construct(_flak, 1, Order(d)[5]), "nor an explicit reserved slot");

            Assert.AreEqual(26, queue.Tick(FlakConstructionRules.TotalSeconds()));
            Assert.AreEqual(28, d.ConstructedCount);
            Assert.AreEqual(0, d.ReservedCount);
            Assert.IsTrue(d.Bind(d.Layout, out string error), "all 28 slots still match the approved layout: " + error);
            Assert.AreEqual(FlakConstructionResult.MaxCapacity, BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources).status);
        }

        [Test]
        public void FullSequence_TakesExactly16120Seconds()
        {
            UsePlentifulResources();
            BaseController c = NewBase("A");
            for (int i = 0; i < 26; i++) Start(c);
            BaseFlakConstruction queue = Queue(c);
            Assert.AreEqual(16_120, queue.QueueRemainingSeconds, 1e-6);
            Assert.AreEqual(25, queue.Tick(16_119.5), "the last one is half a second short");
            Assert.AreEqual(1, queue.Tick(0.5));
            Assert.AreEqual(28, Defence(c).ConstructedCount);
        }

        // ------------------------------------------------------------------ 4/15. levels and upgrades

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void NewFlaks_InheritTheBaseLevel(int level)
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            for (int l = 1; l < level; l++) Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Start(c); Start(c);
            Queue(c).Tick(10_000);
            Assert.AreEqual(4, Defence(c).ConstructedCount);
            AssertAllFlaksAt(c, level);
        }

        [Test]
        public void UpgradeWhilePending_CompletesAtTheNewLevel_NeverMixed()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            Start(c); Start(c);
            Queue(c).Tick(100); // first order part-built at L1
            List<string> reservedBefore = Queue(c).Queue.Select(o => o.SlotId).ToList();

            long gold = Bal(ResourceType.Gold);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(gold - 19, Bal(ResourceType.Gold), "upgrade price unchanged by pending constructions");
            AssertAllFlaksAt(c, 2);
            CollectionAssert.AreEqual(reservedBefore, Queue(c).Queue.Select(o => o.SlotId).ToList(), "reservations untouched by the upgrade");
            Assert.IsTrue(reservedBefore.All(d.IsReserved));
            Assert.AreEqual(100.0, Queue(c).Current.ElapsedSeconds, 1e-9, "progress kept");

            Queue(c).Tick(260);
            AssertAllFlaksAt(c, 2);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Queue(c).Tick(1000);
            Assert.AreEqual(4, d.ConstructedCount);
            AssertAllFlaksAt(c, 3);
        }

        [Test]
        public void Completion_DuringAnUpgrade_WaitsAndThenBuildsAtTheNewLevel()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            Start(c);
            Queue(c).Tick(359);
            int completedDuringUpgrade = -1;
            System.Action<BaseFlakDefence> handler = changed =>
            {
                if (changed == d && d.IsUpgrading && completedDuringUpgrade < 0) completedDuringUpgrade = Queue(c).Tick(5); // order finishes during the upgrade
            };
            BaseFlakDefence.Changed += handler;
            try { Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources)); }
            finally { BaseFlakDefence.Changed -= handler; }

            Assert.AreEqual(0, completedDuringUpgrade, "nothing is placed while the upgrade runs");
            Assert.AreEqual(1, Queue(c).Count, "the finished order waits");
            Assert.IsTrue(Queue(c).Current.IsFinished);
            Assert.AreEqual(1, Queue(c).Tick(0), "and completes on the next tick");
            Assert.AreEqual(3, d.ConstructedCount);
            AssertAllFlaksAt(c, 2);
        }

        [Test]
        public void StartingDuringAnUpgrade_IsRefused()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            FlakConstructionResult inner = FlakConstructionResult.Available;
            System.Action<BaseFlakDefence> handler = changed => { if (changed == d && d.IsUpgrading) inner = Start(c); };
            BaseFlakDefence.Changed += handler;
            try { BaseConstruction.TryUpgradeFlaks(c, _flak, _resources); }
            finally { BaseFlakDefence.Changed -= handler; }
            Assert.AreEqual(FlakConstructionResult.UpgradeInProgress, inner);
            Assert.IsNull(Queue(c));
        }

        // ------------------------------------------------------------------ 7/8. resources

        [Test]
        public void TwoBases_AreChargedFromTheSharedBalance()
        {
            UseProjectResources();
            BaseController a = NewBase("A");
            BaseController b = NewBase("B", "Air", new Vector3(40f, 0f, 0f));
            var before = Balances();
            Start(a); Start(b); Start(b);
            long concrete = Cost(3).concrete + Cost(3).concrete + Cost(4).concrete;
            Assert.AreEqual(before.c - concrete, Bal(ResourceType.Concrete));
            Assert.AreEqual(1, Queue(a).Count);
            Assert.AreEqual(2, Queue(b).Count);
        }

        [Test]
        public void InsufficientResources_RefuseAndNameWhatIsMissing_WithoutSpendingOrReserving()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            Assert.IsTrue(_resources.TrySpend(ResourceType.Concrete, Bal(ResourceType.Concrete) - 4_000));
            Assert.IsTrue(_resources.TrySpend(ResourceType.Aluminium, Bal(ResourceType.Aluminium) - 999));
            var before = Balances();
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.InsufficientResources, q.status);
            CollectionAssert.AreEquivalent(new[] { new KeyValuePair<ResourceType, long>(ResourceType.Concrete, 16_000), new KeyValuePair<ResourceType, long>(ResourceType.Aluminium, 5_201) }, q.missing);
            Assert.AreEqual(FlakConstructionResult.InsufficientResources, Start(c));
            Assert.AreEqual(before, Balances());
            Assert.AreEqual(0, Defence(c).ReservedCount);
            Assert.IsNull(Queue(c));
        }


        // ------------------------------------------------------------------ 10. independent queues

        [Test]
        public void Queues_AreIndependentPerBase()
        {
            UseProjectResources();
            BaseController a = NewBase("A");
            BaseController b = NewBase("B", "Heli", new Vector3(40f, 0f, 0f));
            Start(a); Start(b); Start(b);
            Queue(a).Tick(200);
            Assert.AreEqual(160.0, Queue(a).CurrentRemainingSeconds, 1e-9);
            Assert.AreEqual(360.0, Queue(b).CurrentRemainingSeconds, 1e-9, "ticking A does not advance B");
            Queue(b).Tick(360);
            Assert.AreEqual(3, Defence(b).ConstructedCount);
            Assert.AreEqual(2, Defence(a).ConstructedCount);
            Assert.AreEqual(Order(Defence(b))[3], Queue(b).Current.SlotId);
            Assert.AreNotSame(Queue(a), Queue(b));
        }

        // ------------------------------------------------------------------ 11. completion placement

        [Test]
        public void Completion_PlacesTheLevelModelInTheReservedSlot_WithLayoutPoseAndScale()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            Start(c);
            string slotId = Queue(c).Current.SlotId;
            BuildingSlot slot = d.GetSlot(slotId);
            Vector3 slotPos = slot.transform.localPosition;
            Quaternion slotRot = slot.transform.localRotation;
            Queue(c).Tick(360);
            BuildingInstance b = slot.Occupant;
            Assert.IsNotNull(b);
            GameObject prefab = _flak.GetPrefab(2);
            Assert.AreEqual(prefab.name, b.name);
            Assert.AreSame(slot.transform, b.transform.parent);
            Assert.AreEqual(Quaternion.Euler(0f, _flak.GetFacingYawOffset(2), 0f) * prefab.transform.localRotation, b.transform.localRotation);
            Assert.AreEqual(prefab.transform.localScale * slot.InstanceScale, b.transform.localScale);
            Assert.AreEqual(slotPos, slot.transform.localPosition, "slot never moves");
            Assert.AreEqual(slotRot, slot.transform.localRotation);
            Assert.AreEqual(3, BaseFlakRoster.GetSurvivingFlaks(c));
        }

        [Test]
        public void FailedCompletion_ReleasesTheSlotAndRefunds()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            var before = Balances();
            Start(c);
            string slotId = Queue(c).Current.SlotId;
            // Simulate a broken completion: something else occupies the reserved position.
            BuildingInstance intruder = BuildingPlacement.Place(d.GetSlot(slotId), _flak, 1);
            Assert.IsNotNull(intruder);
            LogAssertIgnore();
            Assert.AreEqual(0, Queue(c).Tick(360));
            Assert.AreEqual(0, Queue(c).Count);
            Assert.IsFalse(d.IsReserved(slotId), "reservation released");
            Assert.AreEqual(before, Balances(), "resources refunded");
            StringAssert.Contains(slotId, Queue(c).LastFailure);
        }

        private static void LogAssertIgnore() => UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;

        // ------------------------------------------------------------------ 12. cancellation

        [Test]
        public void Cancel_ReleasesTheSlot_RefundsExactly_AndTheSlotIsReused()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            BaseFlakDefence d = Defence(c);
            var start = Balances();
            Start(c); Start(c); Start(c);
            List<string> slots = Queue(c).Queue.Select(o => o.SlotId).ToList();

            Assert.IsTrue(BaseConstruction.TryCancelFlakConstruction(c, 1, _resources), "cancel the middle one (#4)");
            Assert.IsFalse(d.IsReserved(slots[1]));
            Assert.AreEqual(2, Queue(c).Count);
            Assert.AreEqual((start.c - Cost(3).concrete - Cost(5).concrete, start.i - Cost(3).iron - Cost(5).iron, start.a - Cost(3).aluminium - Cost(5).aluminium), Balances());

            Assert.AreEqual(5, BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources).flakNumber);
            Start(c);
            Assert.AreEqual(slots[1], Queue(c).Queue[2].SlotId, "the freed position is the next free one in the approved order");

            Queue(c).Tick(100);
            Assert.IsTrue(BaseConstruction.TryCancelFlakConstruction(c, 0, _resources), "cancel the one in progress");
            Assert.IsFalse(d.IsReserved(slots[0]));
            Assert.AreEqual(0.0, Queue(c).Current.ElapsedSeconds, 1e-9, "the next order starts from zero");
            Assert.IsTrue(BaseConstruction.TryCancelFlakConstruction(c, -1, _resources));
            Assert.IsTrue(BaseConstruction.TryCancelFlakConstruction(c, -1, _resources));
            Assert.IsFalse(BaseConstruction.TryCancelFlakConstruction(c, -1, _resources), "nothing left");
            Assert.AreEqual(start, Balances(), "everything refunded");
            Assert.AreEqual(0, d.ReservedCount);
            Assert.AreEqual(2, d.ConstructedCount);
        }

        // ------------------------------------------------------------------ 13. duplicate purchase guard

        [Test]
        public void StaleExpectedFlakNumber_BuysNothing()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            Assert.AreEqual(FlakConstructionResult.Started, Start(c, 3));
            var after = Balances();
            Assert.AreEqual(FlakConstructionResult.FlakNumberChanged, Start(c, 3), "a repeated click for #3");
            Assert.AreEqual(after, Balances());
            Assert.AreEqual(1, Queue(c).Count);
            Assert.AreEqual(FlakConstructionResult.Started, Start(c, 4));
        }

        // ------------------------------------------------------------------ states

        [Test]
        public void InvalidStates_AreRefused()
        {
            UseProjectResources();
            Assert.AreEqual(FlakConstructionResult.InvalidBase, Start(null));
            var plain = new GameObject("plain");
            _created.Add(plain);
            Assert.AreEqual(FlakConstructionResult.NotInitialised, Start(plain.AddComponent<BaseController>()));

            BaseController c = NewBase("A");
            Assert.AreEqual(FlakConstructionResult.NoResourceSystem, BaseConstruction.TryStartFlakConstruction(c, _flak, null));
            BuildingDefinition onlyL1 = ScriptableObject.CreateInstance<BuildingDefinition>();
            _created.Add(onlyL1);
            onlyL1.EditorInitialize(BuildingType.Flak, "Flak", 0.05f, 28, new List<BuildingDefinition.LevelModel> { _flak.Levels[0] }, "test");
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            Assert.AreEqual(FlakConstructionResult.ModelMissing, BaseConstruction.TryStartFlakConstruction(c, onlyL1, _resources), "no model for the base's L2");

            while (BaseConstruction.TryDemolishFlak(c)) { }
            Assert.AreEqual(FlakConstructionResult.BaseDestroyed, Start(c));
            Assert.AreEqual(0, Defence(c).ReservedCount);
        }

        // ------------------------------------------------------------------ 16. all nine base variants

        [Test]
        public void AllNineBaseVariants_BuildInTheirApprovedOrder()
        {
            UsePlentifulResources();
            for (int t = 0; t < NineTypes.Length; t++)
            {
                BaseController c = NewBase("V" + t, NineTypes[t], new Vector3(60f * t, 0f, 0f));
                BaseFlakDefence d = Defence(c);
                Assert.IsTrue(d.IsBound, NineTypes[t]);
                for (int i = 0; i < 4; i++) Assert.AreEqual(FlakConstructionResult.Started, Start(c), NineTypes[t]);
                CollectionAssert.AreEqual(Order(d).Skip(2).Take(4).ToArray(), Queue(c).Queue.Select(o => o.SlotId).ToArray(), NineTypes[t]);
                Assert.AreEqual(4, Queue(c).Tick(10_000), NineTypes[t]);
                Assert.AreEqual(6, d.ConstructedCount, NineTypes[t]);
                CollectionAssert.AreEquivalent(Order(d).Take(6).ToArray(),
                    d.Layout.InConstructionOrder().Select(p => d.GetSlot(p.slotId)).Where(s => s.IsOccupied).Select(s => s.SlotId).ToArray(), NineTypes[t]);
                AssertAllFlaksAt(c, 1);
            }
        }

        // ------------------------------------------------------------------ 17. upgrade regression

        [Test]
        public void UpgradeSystem_StillReplacesBuiltFlaksOnly_AndCosts19Then29Then39()
        {
            UseProjectResources();
            BaseController c = NewBase("A");
            Start(c);
            Queue(c).Tick(360);
            Start(c);
            long gold = Bal(ResourceType.Gold);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(gold - 19 - 29, Bal(ResourceType.Gold));
            Assert.AreEqual(3, Defence(c).ConstructedCount, "the pending one is not a Flak yet");
            AssertAllFlaksAt(c, 3);
            Queue(c).Tick(381);
            AssertAllFlaksAt(c, 3);
            Assert.AreEqual(4, Defence(c).ConstructedCount, "the queued Flak completed at the base's L3");

            // L3 -> L4 bought while a construction is queued: the queued Flak completes at L4.
            Start(c);
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(gold - 19 - 29 - 39, Bal(ResourceType.Gold));
            Queue(c).Tick(402);
            Assert.AreEqual(5, Defence(c).ConstructedCount);
            AssertAllFlaksAt(c, 4);
            Assert.AreEqual(FlakUpgradeResult.AtMaximumLevel, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
        }
    }
}
