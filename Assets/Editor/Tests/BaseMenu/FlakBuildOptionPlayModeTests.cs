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
    /// Play Mode test of real Flak construction through Row 1 Box 2 in World Explorer: real
    /// generated bases, the bootstrap-created menu, the session's ResourceManager, construction
    /// timed by gameplay time (sped up with Time.timeScale) and completion grounded on the real
    /// terrain. Clicks go through BaseMenuCell.Click -> BaseMenuView.CellClicked.
    /// </summary>
    public class FlakBuildOptionPlayModeTests
    {
        private const string WorldExplorerPath = "Assets/Scenes/Ironveil - World Explorer.unity";

        private BaseMenuController _controller;
        private BaseMenuFlakBuildOption _build;
        private BaseMenuFlakUpgradeOption _upgrade;
        private ResourceManager _resources;

        private static void FrameCamera(BaseController b)
        {
            Camera cam = Camera.main;
            foreach (MonoBehaviour m in cam.GetComponents<MonoBehaviour>())
                if (m.GetType().Name != "UniversalAdditionalCameraData") m.enabled = false;
            b.GetComponent<BaseInteractionTarget>().TryGetWorldBounds(out Bounds bounds);
            float r = Mathf.Max(1f, Mathf.Max(bounds.extents.x, bounds.extents.z));
            cam.transform.position = bounds.center + new Vector3(-0.45f, 0.75f, -0.85f).normalized * r * 3.5f;
            cam.transform.LookAt(bounds.center);
        }

        private void OpenOn(BaseController b)
        {
            FrameCamera(b);
            string method = _controller.SelectedContext == null ? "Open" : "Switch";
            typeof(BaseMenuController).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_controller, new object[] { b.GetComponent<BaseInteractionTarget>() });
            Assert.AreSame(b, _controller.SelectedContext.Controller);
        }

        private static void Rehover(BaseMenuCell cell)
        {
            if (!cell.IsHovered) cell.SetHovered(true);
        }

        /// <summary>n real frames plus one (the coroutine resumes after LateUpdate).</summary>
        private static int _step;
        private static IEnumerator Frames(int n)
        {
            int target = Time.frameCount + n + 1;
            float timeout = Time.realtimeSinceStartup + 10f;
            while (Time.frameCount < target)
            {
                if (Time.realtimeSinceStartup > timeout) Assert.Fail($"frames did not advance (wait #{_step}, frame {Time.frameCount}, target {target}, timeScale {Time.timeScale})");
                yield return null;
            }
        }

        private static IEnumerator WaitRealtime(float s)
        {
            float end = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private string Row(string label)
        {
            foreach (Text[] r in _build.Info.Rows)
                if (r[0].gameObject.activeInHierarchy && r[0].text == label) return r[1].text;
            return null;
        }

        private long Bal(ResourceType r) { _resources.TryGetBalance(r, out long b); return b; }

        private static void AssertAllFlaks(BaseController b, int level)
        {
            BaseFlakDefence d = b.GetComponent<BaseFlakDefence>();
            Assert.AreEqual(level, d.FlakLevel, b.BaseId);
            BuildingInstance[] flaks = d.GetComponentsInChildren<BuildingInstance>(true).Where(x => x.Type == BuildingType.Flak).ToArray();
            Assert.AreEqual(d.ConstructedCount, flaks.Length, b.BaseId + " no stray Flak objects");
            foreach (BuildingInstance f in flaks) Assert.AreEqual(level, f.BuildingLevel, b.BaseId + " " + f.Slot.SlotId);
        }

        [UnityTest]
        public IEnumerator Row1Box2_BuildsQueuedFlaks_InTheRealMenu()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(WorldExplorerPath, new LoadSceneParameters(LoadSceneMode.Single));
            // Stage 9: wait for World Explorer's FINAL world (its startup seed replaces the initial generation).
            yield return Ironveil.WorldGeneration.Tests.WorldGenerationTestUtil.WaitForFinalWorld(scene, WorldExplorerPath);
            float start = Time.realtimeSinceStartup;
            List<BaseController> bases = new List<BaseController>();
            int stable = 0, last = -1;
            while (stable < 30)
            {
                yield return null;
                if (scene.isLoaded)
                {
                    bases = Object.FindObjectsByType<BaseFlakDefence>(FindObjectsInactive.Exclude)
                        .Where(d => d.IsBound && d.InitialStateApplied && d.ConstructedCount == 2 && d.GetComponent<BaseInteractionTarget>() != null)
                        .Select(d => d.GetComponent<BaseController>()).ToList();
                    stable = bases.Count == last && bases.Count >= 50 && BaseMenuView.Instance != null && BaseMenuView.Instance.IsBuilt ? stable + 1 : 0;
                    last = bases.Count;
                }

                if (Time.realtimeSinceStartup - start > 240f) Assert.Fail("World Explorer did not settle");
            }

            bases = bases.OrderBy(b => b.BaseId, System.StringComparer.Ordinal).ToList();
            _controller = BaseMenuController.Instance;
            _build = Object.FindAnyObjectByType<BaseMenuFlakBuildOption>();
            _upgrade = Object.FindAnyObjectByType<BaseMenuFlakUpgradeOption>();
            _resources = ResourceManager.Instance;
            Assert.IsNotNull(_build); Assert.IsNotNull(_upgrade); Assert.IsNotNull(_resources);
            Assert.IsTrue(_build.TryBind());
            typeof(BaseMenuController).GetField("hoverExitGrace", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_controller, 1e6f);
            Time.timeScale = 0f; // construction and replenishment wait until we let time run

            BaseController a = bases[0], b = bases[1];
            BaseFlakDefence da = a.GetComponent<BaseFlakDefence>(), db = b.GetComponent<BaseFlakDefence>();
            List<string> order = da.Layout.InConstructionOrder().Select(p => p.slotId).ToList();
            Sprite box2Art = _build.Icon.sprite;

            // ---- Hover: BUILD 3RD with the #3 price.
            OpenOn(a);
            Rehover(_build.Cell);
            _step = 1; yield return Frames(1);
            Assert.IsTrue(_build.Info.IsShown);
            Assert.AreEqual("BUILD 3RD BASE TURRET", _build.Info.Title.text);
            StringAssert.StartsWith("20,000 / ", Row("Concrete"));
            StringAssert.StartsWith("13,800 / ", Row("Iron"));
            StringAssert.StartsWith("6,200 / ", Row("Aluminium"));
            Assert.AreEqual("6m", Row("Construction time"));
            Assert.AreEqual("2 / 28", Row("Turrets"));
            Assert.IsFalse(_build.Progress.gameObject.activeSelf, "no progress strip without construction");

            // ---- Click: one construction starts; a same-frame double click starts nothing.
            long c0 = Bal(ResourceType.Concrete), i0 = Bal(ResourceType.Iron), a0 = Bal(ResourceType.Aluminium), g0 = Bal(ResourceType.Gold);
            _build.Cell.Click();
            Assert.AreEqual(FlakConstructionResult.Started, _build.LastClickResult, _build.LastFeedback);
            _build.Cell.Click();
            Assert.AreNotEqual(FlakConstructionResult.Started, _build.LastClickResult, "double click refused");
            BaseFlakConstruction qa = BaseConstruction.GetConstructionQueue(a);
            Assert.AreEqual(1, qa.Count, "exactly one construction");
            Assert.AreEqual(order[2], qa.Current.SlotId);
            Assert.IsTrue(da.IsReserved(order[2]));
            Assert.AreEqual(c0 - 20_000, Bal(ResourceType.Concrete));
            Assert.AreEqual(i0 - 13_800, Bal(ResourceType.Iron));
            Assert.AreEqual(a0 - 6_200, Bal(ResourceType.Aluminium));
            Assert.AreEqual(g0, Bal(ResourceType.Gold), "no Gold for construction");
            _step = 2; yield return Frames(1);
            Assert.AreEqual("BUILD 4TH BASE TURRET", _build.Info.Title.text);
            Assert.AreEqual("2 + 1 queued / 28", Row("Turrets"));
            Assert.AreEqual("6m left", Row("Building now"));
            Assert.IsTrue(_build.Progress.gameObject.activeSelf);
            Assert.AreEqual("#3  6:00", _build.ProgressText.text);
            StringAssert.Contains("Started base turret #3", _build.Info.Footer.text);

            // ---- Second click after the guard: #4 queued behind #3.
            yield return WaitRealtime(0.5f);
            _build.Cell.Click();
            Assert.AreEqual(FlakConstructionResult.Started, _build.LastClickResult, _build.LastFeedback);
            Assert.AreEqual(2, qa.Count);
            Assert.AreEqual(order[3], qa.Queue[1].SlotId);
            Assert.AreEqual(c0 - 20_000 - 31_500, Bal(ResourceType.Concrete));
            _step = 3; yield return Frames(1);
            Assert.AreEqual("#3  6:00  +1", _build.ProgressText.text);
            Assert.AreEqual("12m 21s", Row("Queue finished in"));

            // ---- Base switching: B has no queue; A keeps its own.
            OpenOn(b);
            Rehover(_build.Cell);
            _step = 4; yield return Frames(1);
            Assert.AreEqual("BUILD 3RD BASE TURRET", _build.Info.Title.text, "B's own next number");
            Assert.AreEqual("2 / 28", Row("Turrets"));
            Assert.IsFalse(_build.Progress.gameObject.activeSelf, "B has nothing under construction");
            yield return WaitRealtime(0.5f);
            _build.Cell.Click();
            Assert.AreEqual(FlakConstructionResult.Started, _build.LastClickResult);
            Assert.AreEqual(1, BaseConstruction.GetConstructionQueue(b).Count);
            Assert.AreEqual(2, qa.Count, "A's queue unchanged");

            // ---- Let gameplay time run (x4 for 2 s): Update advances both queues independently.
            Time.timeScale = 4f;
            yield return WaitRealtime(2.0f);
            Time.timeScale = 0f;
            Assert.Greater(qa.Current.ElapsedSeconds, 3.0, "A progressed in real gameplay time");
            Assert.Greater(BaseConstruction.GetConstructionQueue(b).Current.ElapsedSeconds, 3.0, "B progressed on its own queue");
            Assert.AreEqual(0.0, qa.Queue[1].ElapsedSeconds, 1e-9, "A's second order waits for the first");
            OpenOn(a);
            Rehover(_build.Cell);
            _step = 6; yield return Frames(1);
            Assert.IsTrue(_build.Progress.gameObject.activeSelf);
            Assert.Greater(_build.ProgressFill.rectTransform.anchorMax.x, 0.005f, "progress fill advanced");
            StringAssert.StartsWith("#3  ", _build.ProgressText.text);
            Assert.AreNotEqual("#3  6:00  +1", _build.ProgressText.text, "countdown moved");

            // ---- Upgrade A to L2 while #3 and #4 are pending (Box 1), then finish both.
            Rehover(_upgrade.Cell);
            _step = 7; yield return Frames(1);
            _upgrade.Cell.Click();
            Assert.AreEqual(FlakUpgradeResult.Upgraded, _upgrade.LastClickResult, _upgrade.LastFeedback);
            AssertAllFlaks(a, 2);
            Assert.AreEqual(2, qa.Count, "pending constructions untouched by the upgrade");
            _upgrade.Cell.SetHovered(false);

            // Fast-forward the rest through the queues' own Tick (what Update calls every frame).
            qa.Tick(qa.QueueRemainingSeconds + 1.0);
            BaseFlakConstruction qb = BaseConstruction.GetConstructionQueue(b);
            qb.Tick(qb.QueueRemainingSeconds + 1.0);
            Assert.AreEqual(4, da.ConstructedCount, "both constructions completed");
            Assert.AreEqual(0, qa.Count);
            Assert.AreEqual(0, da.ReservedCount);
            AssertAllFlaks(a, 2);
            foreach (string id in new[] { order[2], order[3] })
            {
                BuildingSlot s = da.GetSlot(id);
                Assert.IsNotNull(s.Occupant, id);
                Assert.AreEqual(2, s.Occupant.BuildingLevel, id + " built at the upgraded level");
                Vector3 p = s.Occupant.transform.position;
                FlakGrounding.Ground(s, a.transform, BaseConstruction.FindTerrainQuery(), out _);
                Assert.Less(Vector3.Distance(p, s.Occupant.transform.position), 1e-4f, id + " was grounded on the real terrain at completion");
            }

            Assert.GreaterOrEqual(db.ConstructedCount, 3, "B's construction completed on its own queue");
            AssertAllFlaks(b, 1);

            // ---- Menu after completion.
            Rehover(_build.Cell);
            _step = 8; yield return Frames(1);
            Assert.AreEqual("BUILD 5TH BASE TURRET", _build.Info.Title.text);
            Assert.AreEqual("4 / 28", Row("Turrets"));
            Assert.IsFalse(_build.Progress.gameObject.activeSelf);

            // ---- Insufficient resources: refused, missing named, nothing spent.
            yield return WaitRealtime(0.5f);
            Assert.IsTrue(_resources.TrySpend(ResourceType.Concrete, Bal(ResourceType.Concrete) - 1_000));
            long cBefore = Bal(ResourceType.Concrete), iBefore = Bal(ResourceType.Iron);
            _step = 9; yield return Frames(1);
            StringAssert.StartsWith("Missing Concrete", Row("Availability"));
            _build.Cell.Click();
            Assert.AreEqual(FlakConstructionResult.InsufficientResources, _build.LastClickResult);
            Assert.AreEqual(cBefore, Bal(ResourceType.Concrete));
            Assert.AreEqual(iBefore, Bal(ResourceType.Iron));
            Assert.IsTrue(qa.Count == 0 && da.ReservedCount == 0);
            _step = 10; yield return Frames(1);
            StringAssert.Contains("Concrete", _build.Info.Footer.text);

            // ---- Box 1 still works and Box 2 kept its artwork.
            Assert.AreSame(box2Art, _build.Icon.sprite);
            Rehover(_upgrade.Cell);
            _step = 11; yield return Frames(1);
            Assert.AreEqual("UPGRADE FLAKS TO L3", _upgrade.Info.Title.text);
            Assert.AreSame(Resources.Load<FlakUpgradeOptionConfig>(FlakUpgradeOptionConfig.ResourcePath).ArtworkForLevel(3), _upgrade.Icon.sprite);

            Time.timeScale = 1f;
            yield return new ExitPlayMode();
        }
    }
}
