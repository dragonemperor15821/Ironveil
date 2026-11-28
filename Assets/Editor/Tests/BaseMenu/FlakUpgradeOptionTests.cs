using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ironveil.Economy;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironveil.BaseMenu.Tests
{
    /// <summary>
    /// Edit-mode tests of the Row 1 Box 1 "Upgrade Flaks" option (Stage 4): its config
    /// asset and artwork, the image shown per level, and the hover content built from
    /// real BaseConstruction.EvaluateFlakUpgrade quotes on temporary foundation bases
    /// paid from a real ResourceManager. Box 2's files are checked to be untouched.
    /// </summary>
    public class FlakUpgradeOptionTests
    {
        private const string TankFoundationPath = "Assets/Prefabs/BaseFoundations/Tank Foundation.prefab";
        private const string ConfigPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        private const string L1ArtPath = "Assets/IRONVEIL/UI/Flak/Level 1 Base Flak.png";
        private const string L2ArtPath = "Assets/IRONVEIL/UI/Flak/Level 2 Base Flak.png";
        private const string L3ArtPath = "Assets/IRONVEIL/UI/Flak/Level 3 Base Flak.png";
        private const string L4ArtPath = "Assets/IRONVEIL/UI/Flak/Level 4 Base Flak.png";
        // Stage 4 renders keep their GUIDs through the Stage 6 renames: the former "Level 2" render (of the model
        // that is now L3) and the former "Level 3" render (of the L6 Base Turret, now L4).
        private const string L3ArtGuid = "036c3aae2be045bdbdbfedfbe3744dfc";
        private const string L4ArtGuid = "a9da6b9f0f43437b9060ecbdffa02474";
        private const string OptionScriptPath = "Assets/Scripts/BaseMenu/BaseMenuFlakUpgradeOption.cs";
        private const string ConfigScriptPath = "Assets/Scripts/BaseMenu/FlakUpgradeOptionConfig.cs";

        private readonly List<Object> _created = new List<Object>();
        private readonly List<KeyValuePair<string, string>> _rows = new List<KeyValuePair<string, string>>();
        private FlakUpgradeOptionConfig _config;
        private ConstructionSettings _settings;
        private BuildingDefinition _flak;
        private ResourceManager _resources;

        [SetUp]
        public void SetUp()
        {
            _config = Resources.Load<FlakUpgradeOptionConfig>(FlakUpgradeOptionConfig.ResourcePath);
            Assert.IsNotNull(_config, "Resources/IronveilFlakUpgradeOption missing");
            _settings = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath);
            _flak = _settings.BuildingCatalog.Get(BuildingType.Flak);
            _resources = ResourceManager.GetOrCreate();
            _created.Add(_resources.gameObject);
            Assert.IsTrue(_resources.Initialize(AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath)), _resources.LastError);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include)) _created.Add(m.gameObject);
            foreach (Object o in _created) if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private BaseController NewBase(string id)
        {
            GameObject root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(TankFoundationPath));
            _created.Add(root);
            root.transform.localScale = Vector3.one * 6.5f;
            BaseController c = root.AddComponent<BaseController>();
            c.InitializeIdentity(id, "Tank Base");
            Assert.AreEqual(BaseConstruction.InitResult.Initialised, BaseConstruction.InitialiseBase(c, _settings, null, out string error), error);
            return c;
        }

        private FlakUpgradeQuote Quote(BaseController c) => BaseConstruction.EvaluateFlakUpgrade(c, _flak, _resources);

        private BaseMenuInfoPanel.Content Content(FlakUpgradeQuote q, string id = "BASE_TANK_001") =>
            BaseMenuFlakUpgradeOption.BuildContent(_config, q, "Tank Base", id, null, _rows);

        private string Row(string label)
        {
            foreach (var kv in _rows) if (kv.Key == label) return kv.Value;
            return null;
        }

        private long Gold() { _resources.TryGetBalance(ResourceType.Gold, out long g); return g; }

        private void UpgradeTo(BaseController c, int level)
        {
            while (BaseConstruction.GetDefence(c).FlakLevel < level)
                Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(c, _flak, _resources));
            Assert.AreEqual(level, BaseConstruction.GetDefence(c).FlakLevel);
        }

        // ------------------------------------------------------------------ config + artwork

        [Test]
        public void Config_IsRow1Box1_AndSeparateFromBox2()
        {
            Assert.AreEqual(0, _config.row);
            Assert.AreEqual(0, _config.column);
            FlakBuildOptionConfig box2 = Resources.Load<FlakBuildOptionConfig>(FlakBuildOptionConfig.ResourcePath);
            Assert.IsNotNull(box2);
            Assert.AreEqual(0, box2.row);
            Assert.AreEqual(1, box2.column, "Box 2 stays at column 1");
            Assert.AreNotSame((Object)box2, _config);
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Sprite>(L1ArtPath), box2.artwork, "Box 2 still shows the L1 artwork");
        }

        [Test]
        public void Artwork_L2L3L4_AreTheRenderedImages_AtTheL1ImageSizeAndImportSettings()
        {
            Sprite l2 = AssetDatabase.LoadAssetAtPath<Sprite>(L2ArtPath);
            Sprite l3 = AssetDatabase.LoadAssetAtPath<Sprite>(L3ArtPath);
            Sprite l4 = AssetDatabase.LoadAssetAtPath<Sprite>(L4ArtPath);
            Assert.IsNotNull(l2, L2ArtPath);
            Assert.IsNotNull(l3, L3ArtPath);
            Assert.IsNotNull(l4, L4ArtPath);
            Assert.AreSame(l2, _config.ArtworkForLevel(2));
            Assert.AreSame(l3, _config.ArtworkForLevel(3));
            Assert.AreSame(l4, _config.ArtworkForLevel(4));
            Assert.IsNull(_config.ArtworkForLevel(1), "Box 1 never shows the current L1");
            Assert.IsNull(_config.ArtworkForLevel(5), "no fifth level");
            Assert.AreEqual(3, _config.artwork.Length);
            Assert.AreEqual(L3ArtGuid, AssetDatabase.AssetPathToGUID(L3ArtPath), "L3 image = the existing render of the model now registered as L3");
            Assert.AreEqual(L4ArtGuid, AssetDatabase.AssetPathToGUID(L4ArtPath), "L4 image = the existing render of the L6 Base Turret model");
            Assert.AreNotEqual(L3ArtGuid, AssetDatabase.AssetPathToGUID(L2ArtPath), "L2 image is the NEW model's render");
            Texture2D l1Tex = AssetDatabase.LoadAssetAtPath<Texture2D>(L1ArtPath);
            foreach (string p in new[] { L2ArtPath, L3ArtPath, L4ArtPath })
            {
                Texture2D t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                Assert.AreEqual(l1Tex.width, t.width, p);
                Assert.AreEqual(l1Tex.height, t.height, p);
                var i1 = (TextureImporter)AssetImporter.GetAtPath(L1ArtPath);
                var i = (TextureImporter)AssetImporter.GetAtPath(p);
                Assert.AreEqual(i1.textureType, i.textureType, p);
                Assert.AreEqual(i1.spriteImportMode, i.spriteImportMode, p);
                Assert.AreEqual(i1.sRGBTexture, i.sRGBTexture, p);
                Assert.AreEqual(i1.mipmapEnabled, i.mipmapEnabled, p);
            }
        }

        [TestCase(1, 2)]
        [TestCase(2, 3)]
        [TestCase(3, 4)]
        [TestCase(4, 4)]
        public void CellImage_ShowsTheNextLevel_OrL4AtTheMaximum(int currentLevel, int shownLevel)
        {
            Assert.AreEqual(shownLevel, FlakUpgradeOptionConfig.DisplayedLevel(currentLevel));
            Assert.AreSame(_config.ArtworkForLevel(shownLevel), _config.ArtworkForCurrentLevel(currentLevel));
        }

        [Test]
        public void NoPricesOrLevelRules_AreDuplicatedInTheMenu()
        {
            foreach (string path in new[] { OptionScriptPath, ConfigScriptPath })
            {
                string code = File.ReadAllText(path);
                Assert.IsFalse(Regex.IsMatch(code, @"(?<![\w.])(19|29|39)(?![\w.])"), path + " hard-codes an upgrade price");
                StringAssert.DoesNotContain("TrySpend", code, path + " must not spend Gold itself");
                StringAssert.DoesNotContain("BuildingPlacement", code, path + " must not place Flak models itself");
                StringAssert.DoesNotContain("ReplaceAllFlaks", code, path + " must not replace Flak models itself");
            }

            Assert.IsFalse(typeof(FlakUpgradeOptionConfig).GetFields().Any(f => Regex.IsMatch(f.Name, "cost|price|gold", RegexOptions.IgnoreCase)),
                "the config holds no prices");
        }

        // ------------------------------------------------------------------ hover content

        [Test]
        public void Hover_L1_ShowsTargetL2_Cost19_GoldAndCount()
        {
            BaseController c = NewBase("A");
            FlakUpgradeQuote q = Quote(c);
            BaseMenuInfoPanel.Content content = Content(q);
            Assert.AreEqual("UPGRADE FLAKS TO L2", content.title);
            Assert.AreSame(_config.ArtworkForLevel(2), content.preview);
            Assert.AreEqual("L1 of 4", Row("Current level"));
            Assert.AreEqual("L2", Row("Next level"));
            Assert.AreEqual(19L.ToString("N0") + " Gold", Row("Cost"));
            Assert.AreEqual(Gold().ToString("N0"), Row("Your Gold"));
            Assert.AreEqual("2 / 28", Row("Flaks on base"));
            Assert.AreEqual("Click to upgrade", Row("Availability"));
            StringAssert.Contains("BASE_TANK_001", content.footer);
        }

        [Test]
        public void Hover_L2_ShowsTargetL3_Cost29()
        {
            BaseController c = NewBase("A");
            UpgradeTo(c, 2);
            BaseMenuInfoPanel.Content content = Content(Quote(c));
            Assert.AreEqual("UPGRADE FLAKS TO L3", content.title);
            Assert.AreSame(_config.ArtworkForLevel(3), content.preview);
            Assert.AreEqual("L2 of 4", Row("Current level"));
            Assert.AreEqual("L3", Row("Next level"));
            Assert.AreEqual("29 Gold", Row("Cost"));
            Assert.AreEqual("Click to upgrade", Row("Availability"));
        }

        [Test]
        public void Hover_L3_ShowsTargetL4_Cost39()
        {
            BaseController c = NewBase("A");
            UpgradeTo(c, 3);
            FlakUpgradeQuote q = Quote(c);
            Assert.AreEqual(FlakUpgradeResult.Available, q.status);
            BaseMenuInfoPanel.Content content = Content(q);
            Assert.AreEqual("UPGRADE FLAKS TO L4", content.title);
            Assert.AreSame(_config.ArtworkForLevel(4), content.preview, "next-level preview = L4 image");
            Assert.AreEqual("L3 of 4", Row("Current level"));
            Assert.AreEqual("L4", Row("Next level"));
            Assert.AreEqual("39 Gold", Row("Cost"));
            Assert.AreEqual("Click to upgrade", Row("Availability"));
            Assert.AreEqual(Gold().ToString("N0"), Row("Your Gold"));
            Assert.AreEqual("2 / 28", Row("Flaks on base"));
            Assert.IsFalse(BaseMenuFlakUpgradeOption.IsMaxLevelState(q));
        }

        [Test]
        public void PriceNotSetText_StaysAvailableForAnUnpricedUpgrade()
        {
            // Unreachable with every price configured; kept so a future unpriced level reads correctly.
            var q = new FlakUpgradeQuote { status = FlakUpgradeResult.PriceNotSet, currentLevel = 3, nextLevel = 4, goldCostKnown = false };
            Assert.AreEqual("Price not set", BaseMenuFlakUpgradeOption.AvailabilityText(q));
            Assert.AreEqual("Price pending approval", BaseMenuFlakUpgradeOption.CostText(q));
            StringAssert.Contains("has not been approved", BaseMenuFlakUpgradeOption.StatusText(q));
        }

        [Test]
        public void Hover_L4_ShowsMaxLevel_AndNoUpgrade()
        {
            BaseController c = NewBase("A");
            UpgradeTo(c, 4);
            FlakUpgradeQuote q = Quote(c);
            BaseMenuInfoPanel.Content content = Content(q);
            Assert.AreEqual("FLAKS AT MAX LEVEL", content.title);
            Assert.AreSame(_config.ArtworkForLevel(4), content.preview, "L4 image kept at the maximum");
            Assert.AreEqual("L4 of 4", Row("Current level"));
            Assert.AreEqual("MAX LEVEL", Row("Next level"));
            Assert.AreEqual("MAX LEVEL", Row("Availability"));
            Assert.IsNull(Row("Cost"));
            Assert.IsNotNull(Row("Your Gold"));
            Assert.AreEqual("2 / 28", Row("Flaks on base"));
            Assert.IsTrue(BaseMenuFlakUpgradeOption.IsMaxLevelState(q));
            Assert.AreEqual("MAX LEVEL", _config.maxLevelLabel);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void MaxLevelState_IsOnlyL4(int level)
        {
            BaseController c = NewBase("A");
            UpgradeTo(c, level);
            Assert.IsFalse(BaseMenuFlakUpgradeOption.IsMaxLevelState(Quote(c)));
            Assert.IsFalse(BaseMenuFlakUpgradeOption.IsMaxLevelState(BaseConstruction.EvaluateFlakUpgrade(null, _flak, _resources)));
        }

        [Test]
        public void Hover_InsufficientGold_ExplainsTheShortfall()
        {
            BaseController c = NewBase("A");
            Assert.IsTrue(_resources.TrySpend(ResourceType.Gold, Gold() - 5));
            FlakUpgradeQuote q = Quote(c);
            Assert.AreEqual(FlakUpgradeResult.InsufficientGold, q.status);
            Content(q);
            Assert.AreEqual("Need 14 more Gold", Row("Availability"));
            StringAssert.Contains("14 short", BaseMenuFlakUpgradeOption.StatusText(q));
            StringAssert.Contains("14 short", Content(q).description, "the blocking reason is part of the hover text");
            Assert.AreEqual("5", Row("Your Gold"));
        }

        [Test]
        public void Hover_NoFlaks_ShowsBaseDestroyed()
        {
            BaseController c = NewBase("A");
            while (BaseConstruction.TryDemolishFlak(c)) { }
            BaseMenuInfoPanel.Content content = Content(Quote(c));
            Assert.AreEqual("BASE DESTROYED", content.title);
            Assert.AreEqual("0 / 28", Row("Flaks on base"));
            Assert.AreEqual("Base destroyed", Row("Availability"));
        }

        [Test]
        public void Hover_DestroyedBase_NoBase_NotInitialised_MissingModel_NoResources()
        {
            Assert.AreEqual("BASE DESTROYED", Content(BaseConstruction.EvaluateFlakUpgrade(null, _flak, _resources)).title);
            Assert.AreEqual("NO BASE SELECTED", Content(BaseConstruction.EvaluateFlakUpgrade(null, _flak, _resources), null).title);

            var plain = new GameObject("plain");
            _created.Add(plain);
            BaseMenuInfoPanel.Content notInit = Content(BaseConstruction.EvaluateFlakUpgrade(plain.AddComponent<BaseController>(), _flak, _resources));
            Assert.AreEqual("FLAK UPGRADE UNAVAILABLE", notInit.title);
            StringAssert.Contains("no Flak defence system", notInit.description);

            BaseController c = NewBase("A");
            BuildingDefinition onlyL1 = ScriptableObject.CreateInstance<BuildingDefinition>();
            _created.Add(onlyL1);
            onlyL1.EditorInitialize(BuildingType.Flak, "Flak", 0.05f, 28, new List<BuildingDefinition.LevelModel> { _flak.Levels[0] }, "test");
            BaseMenuInfoPanel.Content missing = Content(BaseConstruction.EvaluateFlakUpgrade(c, onlyL1, _resources));
            Assert.AreEqual("FLAK UPGRADE UNAVAILABLE", missing.title);
            StringAssert.Contains("no L2 Flak model", missing.description);

            BaseMenuInfoPanel.Content noRes = Content(BaseConstruction.EvaluateFlakUpgrade(c, _flak, null));
            Assert.AreEqual("FLAK UPGRADE UNAVAILABLE", noRes.title);
            Assert.AreEqual("unavailable", Row("Your Gold"));
        }

        [Test]
        public void Hover_FeedbackLine_IsShownAboveTheIdentity()
        {
            BaseController c = NewBase("A");
            BaseMenuInfoPanel.Content content = BaseMenuFlakUpgradeOption.BuildContent(_config, Quote(c), "Tank Base", "BASE_TANK_001",
                "Upgraded 2 Flaks to L2.", _rows);
            Assert.AreEqual("Upgraded 2 Flaks to L2.\nTank Base (BASE_TANK_001)", content.footer);
        }

        // ------------------------------------------------------------------ Box 2 untouched

        [Test]
        public void Box2_HoverContent_IsIndependentOfTheUpgrade()
        {
            BaseController c = NewBase("A");
            FlakBuildOptionConfig box2 = Resources.Load<FlakBuildOptionConfig>(FlakBuildOptionConfig.ResourcePath);
            var rows = new List<KeyValuePair<string, string>>();
            var before = BaseMenuFlakBuildOption.BuildContent(box2, BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources), "Tank Base", "A", null, rows);
            BaseConstruction.TryUpgradeFlaks(c, _flak, _resources);
            FlakConstructionQuote q = BaseConstruction.EvaluateFlakConstruction(c, _flak, _resources);
            var after = BaseMenuFlakBuildOption.BuildContent(box2, q, "Tank Base", "A", null, rows);
            Assert.AreEqual(before.title, after.title, "the upgrade does not change Box 2's offer");
            Assert.AreEqual("BUILD 3RD BASE TURRET", after.title);
            Assert.AreSame(box2.artwork, after.preview);
            Assert.AreEqual(2, q.flakLevel, "the next turret will be built at the base's new level");
            Assert.AreEqual(20_000, q.cost.concrete, "construction prices (#3) are not upgrade prices");
        }
    }
}
