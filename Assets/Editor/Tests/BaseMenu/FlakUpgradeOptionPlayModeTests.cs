using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ironveil.Economy;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Ironveil.BaseMenu.Tests
{
    /// <summary>
    /// Play Mode test of Row 1 Box 1 in the REAL Base Management Menu of World Explorer:
    /// the bootstrap-created menu, real generated bases with 2 x L1 Flak, the session's
    /// ResourceManager. The menu is opened on a base through the controller's own Open /
    /// Switch steps (the pointer cannot rest on a base in batch mode), and clicks go
    /// through BaseMenuCell.Click -> BaseMenuView.CellClicked exactly as a mouse click does.
    /// </summary>
    public class FlakUpgradeOptionPlayModeTests
    {
        private const string WorldExplorerPath = "Assets/Scenes/Ironveil - World Explorer.unity";
        private const float GenerationTimeoutSeconds = 240f;

        private BaseMenuController _controller;
        private BaseMenuView _view;
        private BaseMenuFlakUpgradeOption _upgrade;
        private BaseMenuFlakBuildOption _build;
        private ResourceManager _resources;
        private FlakUpgradeOptionConfig _config;

        private static void Invoke(BaseMenuController c, string method, BaseInteractionTarget t) =>
            typeof(BaseMenuController).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, new object[] { t });

        /// <summary>
        /// Points the scene camera at the base (the menu is shown only while its base projects onto the
        /// screen; which bases the Explorer camera happens to see varies between runs). The camera's own
        /// controller scripts are disabled for the test.
        /// </summary>
        private static void FrameCamera(BaseController b)
        {
            Camera cam = Camera.main;
            Assert.IsNotNull(cam);
            foreach (MonoBehaviour m in cam.GetComponents<MonoBehaviour>())
                if (m.GetType().Name != "UniversalAdditionalCameraData") m.enabled = false;
            Assert.IsTrue(b.GetComponent<BaseInteractionTarget>().TryGetWorldBounds(out Bounds bounds));
            float r = Mathf.Max(1f, Mathf.Max(bounds.extents.x, bounds.extents.z));
            cam.transform.position = bounds.center + new Vector3(-0.45f, 0.75f, -0.85f).normalized * r * 3.5f;
            cam.transform.LookAt(bounds.center);
        }

        private void OpenOn(BaseController b)
        {
            FrameCamera(b);
            BaseInteractionTarget t = b.GetComponent<BaseInteractionTarget>();
            Assert.IsNotNull(t, b.BaseId + " has no interaction target");
            if (_controller.SelectedContext == null) Invoke(_controller, "Open", t);
            else Invoke(_controller, "Switch", t);
            Assert.AreSame(b, _controller.SelectedContext.Controller);
        }

        /// <summary>
        /// A selection switch can disable and re-enable the menu cells, which clears their hover state
        /// (BaseMenuCell.OnDisable). With a real mouse uGUI sends a new pointer-enter; batch mode has no
        /// pointer, so the test re-applies it.
        /// </summary>
        private static void Rehover(BaseMenuCell cell)
        {
            if (!cell.IsHovered) cell.SetHovered(true);
        }

        private long Gold()
        {
            Assert.IsTrue(_resources.TryGetBalance(ResourceType.Gold, out long g));
            return g;
        }

        private string Row(string label)
        {
            foreach (Text[] r in _upgrade.Info.Rows)
                if (r[0].gameObject.activeInHierarchy && r[0].text == label) return r[1].text;
            return null;
        }

        /// <summary>
        /// Waits until <paramref name="n"/> real player frames have run, plus one: the test coroutine resumes after
        /// LateUpdate, so a refresh made in the frame it resumes in is only on screen in the next one (the menu
        /// refuses clicks on what has not been shown yet).
        /// </summary>
        private static IEnumerator Frames(int n)
        {
            int target = Time.frameCount + n + 1;
            float timeout = Time.realtimeSinceStartup + 10f;
            while (Time.frameCount < target)
            {
                if (Time.realtimeSinceStartup > timeout) Assert.Fail("frames did not advance");
                yield return null;
            }
        }

        private static IEnumerator WaitRealtime(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private static int FlakLevel(BaseController b) => b.GetComponent<BaseFlakDefence>().FlakLevel;

        private static void AssertAllFlaks(BaseController b, int level)
        {
            BaseFlakDefence d = b.GetComponent<BaseFlakDefence>();
            Assert.AreEqual(level, d.FlakLevel, b.BaseId);
            foreach (BuildingInstance f in d.GetComponentsInChildren<BuildingInstance>(true).Where(x => x.Type == BuildingType.Flak))
                Assert.AreEqual(level, f.BuildingLevel, b.BaseId + " " + f.Slot.SlotId);
        }

        [UnityTest]
        public IEnumerator Row1Box1_ShowsUpgradesAndRefreshes_InTheRealMenu()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(WorldExplorerPath, new LoadSceneParameters(LoadSceneMode.Single));
            // Stage 9: wait for World Explorer's FINAL world (its startup seed replaces the initial generation).
            yield return Ironveil.WorldGeneration.Tests.WorldGenerationTestUtil.WaitForFinalWorld(scene, WorldExplorerPath);
            float start = Time.realtimeSinceStartup;
            List<BaseController> bases = null;
            while (true)
            {
                if (scene.isLoaded)
                {
                    bases = Object.FindObjectsByType<BaseFlakDefence>(FindObjectsInactive.Exclude)
                        .Where(d => d.IsBound && d.InitialStateApplied && d.ConstructedCount == 2 && d.GetComponent<BaseInteractionTarget>() != null)
                        .Select(d => d.GetComponent<BaseController>()).ToList();
                    if (bases.Count >= 50 && BaseMenuView.Instance != null && BaseMenuView.Instance.IsBuilt) break;
                }

                if (Time.realtimeSinceStartup - start > GenerationTimeoutSeconds) Assert.Fail("World Explorer did not generate in time");
                yield return null;
            }

            // Generation can replace its bases in a later pass: wait until the set is stable, then re-query.
            int stable = 0, lastCount = -1;
            while (stable < 30)
            {
                yield return null;
                bases = Object.FindObjectsByType<BaseFlakDefence>(FindObjectsInactive.Exclude)
                    .Where(d => d.IsBound && d.InitialStateApplied && d.ConstructedCount == 2 && d.GetComponent<BaseInteractionTarget>() != null)
                    .Select(d => d.GetComponent<BaseController>()).ToList();
                stable = bases.Count == lastCount && bases.Count >= 50 ? stable + 1 : 0;
                lastCount = bases.Count;
                if (Time.realtimeSinceStartup - start > GenerationTimeoutSeconds) Assert.Fail("bases did not settle");
            }

            bases = bases.OrderBy(b => b.BaseId, System.StringComparer.Ordinal).ToList();
            Assert.GreaterOrEqual(bases.Count, 50);
            _controller = BaseMenuController.Instance;
            _view = BaseMenuView.Instance;
            _upgrade = Object.FindAnyObjectByType<BaseMenuFlakUpgradeOption>();
            _build = Object.FindAnyObjectByType<BaseMenuFlakBuildOption>();
            _resources = ResourceManager.Instance;
            _config = Resources.Load<FlakUpgradeOptionConfig>(FlakUpgradeOptionConfig.ResourcePath);
            Assert.IsNotNull(_controller); Assert.IsNotNull(_view); Assert.IsNotNull(_upgrade, "BaseMenuBootstrap adds Row 1 Box 1");
            Assert.IsNotNull(_build); Assert.IsNotNull(_resources);
            Assert.AreEqual(1, Object.FindObjectsByType<BaseMenuFlakUpgradeOption>(FindObjectsInactive.Include).Length);
            Assert.IsTrue(_upgrade.TryBind());
            Assert.AreSame(_view.GetSlot(0, 0), _upgrade.Cell, "Row 1 Box 1 = grid row 0, column 0");
            Assert.AreSame(_view.GetSlot(0, 1), _build.Cell, "Box 2 still at row 0, column 1");
            Sprite box2Art = _build.Icon.sprite;
            Assert.AreSame(_build.Config.artwork, box2Art);

            // Keep the menu open without a real pointer (batch mode).
            typeof(BaseMenuController).GetField("hoverExitGrace", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_controller, 1e6f);
            Time.timeScale = 0f; // freeze the world; the menu runs on unscaled time

            BaseController a = bases[0], b = bases[1], c = bases[2];
            List<BaseController> others = bases.Skip(3).ToList();
            var otherFlaks = others.ToDictionary(x => x, x => x.GetComponentsInChildren<BuildingInstance>().Where(f => f.Type == BuildingType.Flak).ToArray());

            // ---- Base A at L1: L2 image, hover 19 Gold.
            OpenOn(a);
            yield return Frames(2);
            Assert.IsTrue(_view.IsShown, "menu panel shown");
            Assert.AreSame(a, _upgrade.ShownBase);
            Assert.AreEqual(1, _upgrade.ShownLevel);
            Assert.AreSame(_config.ArtworkForLevel(2), _upgrade.Icon.sprite, "L1 base shows the L2 image");
            Assert.IsFalse(_upgrade.MaxBadge.gameObject.activeSelf, "no MAX LEVEL badge below L4");
            Assert.AreEqual(Color.white, _upgrade.Icon.color);
            _upgrade.Cell.SetHovered(true);
            yield return Frames(1);
            Assert.IsTrue(_upgrade.Info.IsShown, "hover panel shown");
            Assert.AreEqual("UPGRADE FLAKS TO L2", _upgrade.Info.Title.text);
            Assert.AreEqual("L1 of 4", Row("Current level"));
            Assert.AreEqual("L2", Row("Next level"));
            Assert.AreEqual("19 Gold", Row("Cost"));
            Assert.AreEqual("2 / 28", Row("Flaks on base"));
            Assert.AreEqual(Gold().ToString("N0"), Row("Your Gold"));

            // ---- Gold display refresh.
            Assert.IsTrue(_resources.TrySpend(ResourceType.Gold, 100));
            yield return Frames(1);
            Assert.AreEqual(Gold().ToString("N0"), Row("Your Gold"), "Gold row follows the shared balance");

            // ---- Click L1 -> L2 through the real cell; a second click in the same frame buys nothing.
            long g0 = Gold();
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, _upgrade.LastClickResult, _upgrade.LastFeedback);
            Assert.AreEqual(g0 - 19, Gold());
            AssertAllFlaks(a, 2);
            _upgrade.Cell.Click();
            Assert.AreNotEqual(FlakUpgradeResult.Upgraded, _upgrade.LastClickResult, "duplicate click refused");
            Assert.AreEqual(g0 - 19, Gold(), "charged once");
            Assert.AreEqual(2, FlakLevel(a));
            Assert.AreEqual(1, FlakLevel(b), "another base is never upgraded by A's click");
            yield return Frames(1);
            Assert.AreSame(_config.ArtworkForLevel(3), _upgrade.Icon.sprite, "L2 base shows the L3 image");
            Assert.AreEqual("UPGRADE FLAKS TO L3", _upgrade.Info.Title.text);
            Assert.AreEqual("L2 of 4", Row("Current level"));
            Assert.AreEqual("29 Gold", Row("Cost"));
            StringAssert.Contains("Upgraded 2 Flaks to L2", _upgrade.Info.Footer.text);
            Assert.AreSame(box2Art, _build.Icon.sprite, "Box 2 artwork unchanged");

            // ---- Stale click: the selection switches to B and the click lands before the cell refreshed.
            yield return WaitRealtime(_config.clickGuardSeconds + 0.1f);
            OpenOn(b);
            long g1 = Gold();
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.LevelChanged, _upgrade.LastClickResult, "cell still showed A");
            Assert.AreEqual(g1, Gold());
            Assert.AreEqual(2, FlakLevel(a));
            Assert.AreEqual(1, FlakLevel(b));

            // ---- Base switching refresh: B at L1.
            Rehover(_upgrade.Cell);
            yield return Frames(1);
            Assert.AreSame(b, _upgrade.ShownBase);
            Assert.AreSame(_config.ArtworkForLevel(2), _upgrade.Icon.sprite);
            Assert.AreEqual("UPGRADE FLAKS TO L2", _upgrade.Info.Title.text);
            Assert.AreEqual("19 Gold", Row("Cost"));
            StringAssert.Contains(b.BaseId, _upgrade.Info.Footer.text);
            StringAssert.DoesNotContain(a.BaseId, _upgrade.Info.Footer.text);

            // ---- Stale level: B is upgraded elsewhere in the same frame; the click passes the shown L1.
            Assert.AreEqual(FlakUpgradeResult.Upgraded, BaseConstruction.TryUpgradeFlaks(b, 1));
            long g2 = Gold();
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.LevelChanged, _upgrade.LastClickResult, "expectedCurrentLevel guard");
            Assert.AreEqual(g2, Gold(), "nothing bought on a stale level");
            AssertAllFlaks(b, 2);
            yield return Frames(1);
            Assert.AreEqual("L2 of 4", Row("Current level"), "refreshed after the external upgrade");

            // ---- Back to A: L2 -> L3 click.
            OpenOn(a);
            Rehover(_upgrade.Cell);
            yield return Frames(1);
            long g3 = Gold();
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, _upgrade.LastClickResult, _upgrade.LastFeedback);
            Assert.AreEqual(g3 - 29, Gold());
            AssertAllFlaks(a, 3);
            yield return Frames(1);

            // ---- A at L3: L4 image, 39 Gold; the click buys the real L3 -> L4 upgrade.
            Assert.AreSame(_config.ArtworkForLevel(4), _upgrade.Icon.sprite, "L3 base shows the L4 image");
            Assert.AreEqual("UPGRADE FLAKS TO L4", _upgrade.Info.Title.text);
            Assert.AreEqual("L3 of 4", Row("Current level"));
            Assert.AreEqual("L4", Row("Next level"));
            Assert.AreEqual("39 Gold", Row("Cost"));
            Assert.AreEqual("Click to upgrade", Row("Availability"));
            Assert.IsFalse(_upgrade.MaxBadge.gameObject.activeSelf);
            yield return WaitRealtime(_config.clickGuardSeconds + 0.1f);
            long gL3 = Gold();
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, _upgrade.LastClickResult, _upgrade.LastFeedback);
            Assert.AreEqual(gL3 - 39, Gold());
            _upgrade.Cell.Click();
            Assert.AreNotEqual(FlakUpgradeResult.Upgraded, _upgrade.LastClickResult, "duplicate click refused");
            Assert.AreEqual(gL3 - 39, Gold(), "charged once");

            // ---- A at L4: MAX LEVEL state.
            yield return Frames(2);
            AssertAllFlaks(a, 4);
            Assert.AreEqual(4, _upgrade.ShownLevel, "menu refreshed to L4 at once");
            Assert.AreSame(_config.ArtworkForLevel(4), _upgrade.Icon.sprite, "L4 image kept at the maximum");
            Assert.IsTrue(_upgrade.MaxBadge.gameObject.activeSelf, "MAX LEVEL badge on the cell");
            Assert.AreEqual("MAX LEVEL", _upgrade.MaxBadgeText.text);
            Assert.AreEqual(BaseMenuFlakUpgradeOption.MaxLevelIconTint, _upgrade.Icon.color, "artwork dimmed: no action");
            Assert.AreEqual("FLAKS AT MAX LEVEL", _upgrade.Info.Title.text);
            Assert.AreEqual("MAX LEVEL", Row("Next level"));
            Assert.AreEqual("MAX LEVEL", Row("Availability"));
            Assert.IsNull(Row("Cost"));
            yield return WaitRealtime(_config.clickGuardSeconds + 0.1f);
            long g4 = Gold();
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.AtMaximumLevel, _upgrade.LastClickResult);
            Assert.AreEqual(g4, Gold());
            AssertAllFlaks(a, 4);

            // ---- Flak count refresh: a Flak built on A shows up at once (and inherits L4).
            Assert.AreEqual(4, BaseConstruction.TryConstructFlak(a).BuildingLevel);
            yield return Frames(1);
            Assert.AreEqual("3 / 28", Row("Flaks on base"));
            AssertAllFlaks(a, 4);

            // ---- Switching to a lower-level base clears the MAX state at once.
            OpenOn(b);
            Rehover(_upgrade.Cell);
            yield return Frames(1);
            Assert.IsFalse(_upgrade.MaxBadge.gameObject.activeSelf, "B is L2: no badge");
            Assert.AreEqual(Color.white, _upgrade.Icon.color);

            // ---- Insufficient Gold on B (L2 -> L3 needs 29).
            Assert.IsTrue(_resources.TrySpend(ResourceType.Gold, Gold() - 10));
            yield return Frames(1);
            Assert.AreEqual("Need 19 more Gold", Row("Availability"));
            Assert.AreEqual("10", Row("Your Gold"));
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.InsufficientGold, _upgrade.LastClickResult);
            Assert.AreEqual(10, Gold());
            AssertAllFlaks(b, 2);

            // ---- Zero Flaks on C: BASE DESTROYED, nothing bought.
            OpenOn(c);
            Rehover(_upgrade.Cell);
            while (BaseConstruction.TryDemolishFlak(c)) { }
            yield return Frames(1);
            Assert.AreEqual("BASE DESTROYED", _upgrade.Info.Title.text);
            Assert.AreEqual("0 / 28", Row("Flaks on base"));
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.NoFlaks, _upgrade.LastClickResult);
            Assert.AreEqual(10, Gold());

            // ---- Box 2 separate: hover shows its own panel; its click starts a construction (Stage 5),
            //      spends no Gold, is not handled by Box 1 and changes no Flak level.
            _upgrade.Cell.SetHovered(false);
            OpenOn(b);
            Rehover(_build.Cell);
            yield return Frames(1);
            Assert.IsTrue(_build.Info.IsShown);
            Assert.IsFalse(_upgrade.Info.IsShown, "Box 1 panel hidden while Box 2 is hovered");
            Assert.AreEqual("BUILD 3RD BASE TURRET", _build.Info.Title.text);
            FlakUpgradeResult before = _upgrade.LastClickResult;
            _build.Cell.Click();
            Assert.AreEqual(before, _upgrade.LastClickResult, "Box 2 clicks are not handled by Box 1");
            Assert.AreEqual(10, Gold(), "building a turret costs no Gold");
            Assert.AreEqual(FlakConstructionResult.Started, _build.LastClickResult, "Box 2 click starts one construction");
            Assert.AreEqual(2, b.GetComponent<BaseFlakDefence>().ConstructedCount, "the turret is under construction, not built yet");
            Assert.AreEqual(1, BaseConstruction.GetConstructionQueue(b).Count);
            AssertAllFlaks(b, 2);
            Assert.AreSame(box2Art, _build.Icon.sprite);
            _build.Cell.SetHovered(false);

            // ---- No other base changed.
            foreach (var kv in otherFlaks)
            {
                Assert.AreEqual(1, FlakLevel(kv.Key), kv.Key.BaseId);
                CollectionAssert.AreEqual(kv.Value, kv.Key.GetComponentsInChildren<BuildingInstance>().Where(f => f.Type == BuildingType.Flak).ToArray(), kv.Key.BaseId);
            }

            Time.timeScale = 1f;
            yield return new ExitPlayMode();
        }
    }
}
