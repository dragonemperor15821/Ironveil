using System.Collections.Generic;
using System.Linq;
using Ironveil.Economy;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironveil.BaseMenu.Tests
{
    /// <summary>
    /// Edit-mode tests of Row 1 Box 2 "Build Additional Flaks" content (Stage 5), built from real
    /// BaseConstruction.EvaluateFlakConstruction quotes on temporary foundation bases.
    /// </summary>
    public class FlakBuildOptionTests
    {
        private const string ConfigPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        private readonly List<Object> _created = new List<Object>();
        private readonly List<KeyValuePair<string, string>> _rows = new List<KeyValuePair<string, string>>();
        private FlakBuildOptionConfig _config;
        private ConstructionSettings _settings;
        private BuildingDefinition _flak;
        private ResourceManager _resources;

        [SetUp]
        public void SetUp()
        {
            _config = Resources.Load<FlakBuildOptionConfig>(FlakBuildOptionConfig.ResourcePath);
            _settings = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath);
            _flak = _settings.BuildingCatalog.Get(BuildingType.Flak);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include)) _created.Add(m.gameObject);
            foreach (Object o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private void Resources_(bool plenty)
        {
            var project = AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath);
            HomeBaseResourceConfig config = project;
            if (plenty)
            {
                config = HomeBaseResourceConfig.CreateRuntime(project.Entries.Select(e =>
                    e.Resource == ResourceType.Concrete || e.Resource == ResourceType.Iron || e.Resource == ResourceType.Aluminium
                        ? new ResourceConfigEntry(e.Resource, 1_000_000_000_000L, true, 1_000_000_000_000L, false, 0, 0) : e));
                _created.Add(config);
            }

            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(config), _resources.LastError);
        }

        private BaseController NewBase()
        {
            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/BaseFoundations/Tank Foundation.prefab"));
            _created.Add(root);
            root.transform.localScale = Vector3.one * 6.5f;
            BaseController c = root.AddComponent<BaseController>();
            c.InitializeIdentity("BASE_TANK_001", "Tank Base");
            Assert.AreEqual(BaseConstruction.InitResult.Initialised, BaseConstruction.InitialiseBase(c, _settings, null, out string e), e);
            return c;
        }

        private FlakConstructionQuote Quote(BaseController c) => BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
        private BaseMenuInfoPanel.Content Content(FlakConstructionQuote q) => BaseMenuFlakBuildOption.BuildContent(_config, q, "Tank Base", "BASE_TANK_001", null, _rows);

        private string Row(string label)
        {
            foreach (var kv in _rows) if (kv.Key == label) return kv.Value;
            return null;
        }

        [Test]
        public void Title_FollowsTheNextTurretNumber_AndRowsShowCostAvailableTimeAndQueue()
        {
            Resources_(false);
            BaseController c = NewBase();
            Assert.AreEqual("BUILD 3RD BASE TURRET", Content(Quote(c)).title);
            Assert.AreEqual("20,000 / 1,300,000", Row("Concrete"));
            Assert.AreEqual("13,800 / 900,000", Row("Iron"));
            Assert.AreEqual("6,200 / 400,000", Row("Aluminium"));
            Assert.AreEqual("6m", Row("Construction time"));
            Assert.AreEqual("2 / 28", Row("Turrets"));
            Assert.AreEqual("Click to build", Row("Availability"));
            Assert.IsNull(Row("Building now"));
            Assert.AreSame(_config.artwork, Content(Quote(c)).preview);

            BaseConstruction.TryStartFlakConstruction(c, _flak, _resources);
            Assert.AreEqual("BUILD 4TH BASE TURRET", Content(Quote(c)).title);
            Assert.AreEqual("31,500 / 1,280,000", Row("Concrete"));
            Assert.AreEqual("6m 21s", Row("Construction time"));
            Assert.AreEqual("2 + 1 queued / 28", Row("Turrets"));
            Assert.AreEqual("6m left", Row("Building now"));
            Assert.IsNull(Row("Queue finished in"));

            BaseConstruction.TryStartFlakConstruction(c, _flak, _resources);
            BaseConstruction.GetConstructionQueue(c).Tick(60.4);
            Assert.AreEqual("BUILD 5TH BASE TURRET", Content(Quote(c)).title);
            Assert.AreEqual("2 + 2 queued / 28", Row("Turrets"));
            Assert.AreEqual("5m left", Row("Building now"), "299.6 s rounds up to 300 s");
            Assert.AreEqual("11m 21s", Row("Queue finished in"));
            StringAssert.Contains("Base turrets: 2 / 28", Content(Quote(c)).footer);
        }

        [Test]
        public void Ordinals_RunFrom3rdTo28th()
        {
            Assert.AreEqual("3RD", BaseFlakRoster.Ordinal(3));
            Assert.AreEqual("11TH", BaseFlakRoster.Ordinal(11));
            Assert.AreEqual("21ST", BaseFlakRoster.Ordinal(21));
            Assert.AreEqual("22ND", BaseFlakRoster.Ordinal(22));
            Assert.AreEqual("28TH", BaseFlakRoster.Ordinal(28));
        }

        [Test]
        public void MaxCapacity_WhenAll28AreBuiltOrQueued()
        {
            Resources_(true);
            BaseController c = NewBase();
            for (int i = 0; i < 25; i++) BaseConstruction.TryStartFlakConstruction(c, _flak, _resources);
            Assert.AreEqual("BUILD 28TH BASE TURRET", Content(Quote(c)).title);
            Assert.AreEqual("7,389,300 / " + ResourceHudFormat.Amount(_resources.Ledger.GetBalance(ResourceType.Concrete)), Row("Concrete"));
            Assert.AreEqual("14m 40s", Row("Construction time"));
            BaseConstruction.TryStartFlakConstruction(c, _flak, _resources);
            BaseMenuInfoPanel.Content content = Content(Quote(c));
            Assert.AreEqual("MAX CAPACITY", content.title);
            Assert.AreEqual("MAX CAPACITY", Row("Availability"));
            Assert.AreEqual("2 + 26 queued / 28", Row("Turrets"));
            Assert.IsNull(Row("Concrete"), "no price at capacity");
        }

        [Test]
        public void InsufficientResources_NameTheMissingResources()
        {
            Resources_(false);
            BaseController c = NewBase();
            _resources.TrySpend(ResourceType.Concrete, _resources.Ledger.GetBalance(ResourceType.Concrete) - 2_500);
            _resources.TrySpend(ResourceType.Iron, _resources.Ledger.GetBalance(ResourceType.Iron) - 400);
            FlakConstructionQuote q = Quote(c);
            Assert.AreEqual("BUILD 3RD BASE TURRET", Content(q).title);
            Assert.AreEqual("Missing Concrete, Iron", Row("Availability"));
            Assert.AreEqual("20,000 / 2,500", Row("Concrete"));
            Assert.AreEqual("Not enough resources: missing 17,500 Concrete, 13,400 Iron.", BaseMenuFlakBuildOption.StatusText(q));
        }

        [Test]
        public void DestroyedAndUnavailableBases()
        {
            Resources_(false);
            BaseController c = NewBase();
            while (BaseConstruction.TryDemolishFlak(c)) { }
            Assert.AreEqual("BASE DESTROYED", Content(Quote(c)).title);
            Assert.AreEqual("Base destroyed", Row("Availability"));
            Assert.AreEqual("NO BASE SELECTED", BaseMenuFlakBuildOption.BuildContent(_config, BaseConstruction.EvaluateFlakConstruction(null, _flak, _resources), null, null, null, _rows).title);
            var plain = new GameObject("plain"); _created.Add(plain);
            Assert.AreEqual("TURRET CONSTRUCTION UNAVAILABLE", Content(BaseConstruction.EvaluateFlakConstruction(plain.AddComponent<BaseController>(), _flak, _resources)).title);
        }

        [Test]
        public void Rebuild_TitleCostAndTextAfterAttackLeavesOneTurret()
        {
            Resources_(false);
            BaseController c = NewBase();
            Assert.IsTrue(BaseConstruction.TryDemolishFlak(c));
            FlakConstructionQuote q = Quote(c);
            BaseMenuInfoPanel.Content content = Content(q);
            Assert.AreEqual("REBUILD 2ND BASE TURRET", content.title);
            StringAssert.Contains("Rebuild: attacks left this base with one turret.", content.description);
            Assert.AreEqual("10,000 / 1,300,000", Row("Concrete"));
            Assert.AreEqual("6,900 / 900,000", Row("Iron"));
            Assert.AreEqual("3,100 / 400,000", Row("Aluminium"));
            Assert.AreEqual("6m", Row("Construction time"));
            Assert.AreEqual("1 / 28", Row("Turrets"));
            Assert.AreEqual("Click to build", Row("Availability"));
        }

        [Test]
        public void StorageInsufficient_ExplainsThatMoreStorageIsNeeded()
        {
            Resources_(false);
            BaseController c = NewBase();
            var add = new[] { ResourceType.Concrete, ResourceType.Iron, ResourceType.Aluminium };
            for (int n = 3; n <= 12; n++)
            {
                foreach (ResourceType r in add)
                {
                    _resources.TryGetCapacity(r, out long cap);
                    long room = cap - _resources.Ledger.GetBalance(r);
                    if (room > 0) _resources.Ledger.TryAdd(r, room, out _);
                }
                Assert.AreEqual(FlakConstructionResult.Started, BaseConstruction.TryStartFlakConstruction(c, _flak, _resources, n), "#" + n);
                BaseConstruction.GetConstructionQueue(c).Tick(10_000);
            }

            FlakConstructionQuote q = Quote(c);
            Assert.AreEqual(FlakConstructionResult.StorageInsufficient, q.status);
            BaseMenuInfoPanel.Content content = Content(q);
            Assert.AreEqual("BUILD 13TH BASE TURRET", content.title);
            Assert.AreEqual("Needs more storage", Row("Availability"));
            StringAssert.StartsWith("1,350,000 / ", Row("Concrete"));
            Assert.AreEqual("Storage too small: this turret costs more than you can store. Expand storage with additional bases: Concrete (+50,000), Iron (+31,500), Aluminium (+18,500).",
                BaseMenuFlakBuildOption.StatusText(q));
            StringAssert.Contains("Expand storage with additional bases", content.description);
        }

        [Test]
        public void RebuildNotEligible_HasText()
        {
            var q = new FlakConstructionQuote { status = FlakConstructionResult.RebuildNotEligible };
            Assert.AreEqual("Rebuild only", BaseMenuFlakBuildOption.AvailabilityText(q));
            StringAssert.Contains("#2 can only be rebuilt", BaseMenuFlakBuildOption.StatusText(q));
        }

        [Test]
        public void ProgressLabel_Formats()
        {
            Assert.AreEqual("#3  6:00", BaseMenuFlakBuildOption.ProgressLabel(3, 360, 0));
            Assert.AreEqual("#3  5:00  +2", BaseMenuFlakBuildOption.ProgressLabel(3, 299.2, 2));
            Assert.AreEqual("#9  1:02:05", BaseMenuFlakBuildOption.ProgressLabel(9, 3725, 0));
        }

        [Test]
        public void Box2Config_HoldsNoPrices()
        {
            Assert.IsFalse(typeof(FlakBuildOptionConfig).GetFields().Any(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, "cost|price|concrete|iron|alumin", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
            Assert.AreEqual(0, _config.row);
            Assert.AreEqual(1, _config.column);
        }
    }
}
