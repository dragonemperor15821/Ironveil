using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>
    /// Real Play Mode tests of the Stage 4 HUD in the actual MainBuild and World Explorer scenes: automatic creation,
    /// one HUD across scene switches, live values, Explorer layout against the real ExplorerWorldUI rectangles,
    /// pointer blocking, and the Base Management Menu's components and sorting order.
    /// </summary>
    public class ResourceHudPlayModeTests
    {
        private const float SceneLoadTimeoutSeconds = 120f;

        [UnityTest]
        public IEnumerator MainBuild_ShowsHud_AndMenuStillWorks()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Time.timeScale = 0f;
                yield return LoadScene(ResourceManagerTests.MainBuildPath);
                yield return null; // HUD LateUpdate

                ResourceHud hud = AssertSingleHud();
                ResourceManager manager = ResourceManager.Instance;
                Assert.AreSame(manager, hud.BoundManager);
                Assert.AreEqual("DontDestroyOnLoad", hud.gameObject.scene.name);
                Assert.AreEqual(80, hud.Canvas.sortingOrder);
                Assert.AreEqual(ResourceHudPlacementMode.FullWidthTop, hud.Placement.mode);
                Assert.IsTrue(hud.Canvas.isActiveAndEnabled && hud.Bar.gameObject.activeInHierarchy);
                Assert.AreEqual("1,500,000", hud.Items[(int)ResourceType.Concrete].Value.text);
                Assert.AreEqual(1, ResourceHudTests.CountHudHandlers(manager, "ResourceChanged"));

                // Live update after spending in the real scene.
                manager.TrySpend(ResourceType.Concrete, 250_000);
                Assert.AreEqual("1,250,000", hud.Items[(int)ResourceType.Concrete].Value.text);
                yield return null;
                Assert.AreEqual("NEXT 5.0s", hud.Items[(int)ResourceType.Concrete].Status.text, "Paused: countdown holds.");

                // Pointer: an EventSystem exists, and the HUD blocks the world only inside the bar.
                Assert.IsNotNull(EventSystem.current ?? Object.FindAnyObjectByType<EventSystem>(), "EventSystem required for pointer-over-UI checks.");
                Rect bar = BarScreenRect(hud);
                Assert.IsTrue(HudHit(new Vector2(bar.center.x, bar.center.y)), "Pointer over the HUD must count as over UI.");
                Assert.IsTrue(HudHit(new Vector2(bar.xMin + 3, bar.yMax - 3)), "Corner of the bar.");
                Assert.IsFalse(HudHit(new Vector2(bar.center.x, bar.yMin - 20)), "Below the HUD the world stays interactive.");
                Assert.IsFalse(HudHit(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)));

                // Base Management Menu: same components as before, sorting order 90 above the HUD.
                BaseMenuBootstrap menu = BaseMenuBootstrap.Instance;
                Assert.IsNotNull(menu, "Base menu bootstrap must still be created.");
                Assert.IsNotNull(menu.GetComponent<BaseHoverController>());
                Assert.IsNotNull(menu.GetComponent<BaseMenuController>());
                BaseMenuView view = menu.GetComponent<BaseMenuView>();
                Assert.IsNotNull(view);
                if (view.Canvas != null)
                {
                    Assert.AreEqual(90, view.Canvas.sortingOrder);
                    Assert.Greater(view.Canvas.sortingOrder, hud.Canvas.sortingOrder, "Menu stays above the HUD.");
                }
                Assert.IsNotNull(Object.FindAnyObjectByType<RTSMainCamera>(), "MainBuild camera is untouched.");
            }
            finally
            {
                Time.timeScale = 1f;
            }
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SceneTransitions_KeepOneHud_WithTheSameState()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Time.timeScale = 0f;
                yield return LoadScene(ResourceManagerTests.MainBuildPath);
                ResourceHud hud = AssertSingleHud();
                ResourceManager manager = ResourceManager.Instance;
                manager.TrySpend(ResourceType.Iron, 300_000);
                manager.TrySpend(ResourceType.Gold, 1_234);

                // --- World Explorer
                yield return LoadScene(ResourceManagerTests.WorldExplorerPath);
                yield return null;
                Assert.AreSame(hud, AssertSingleHud(), "Same HUD instance after the scene switch.");
                Assert.AreEqual(1, ResourceHudTests.CountHudHandlers(manager, "ResourceChanged"), "No duplicate subscription.");
                Assert.AreEqual(1, ResourceHudTests.CountHudHandlers(manager, "LedgerReset"));
                Assert.AreEqual("600,000", hud.Items[(int)ResourceType.Iron].Value.text);
                Assert.AreEqual("8,766", hud.Items[(int)ResourceType.Gold].Value.text);

                ExplorerWorldUI explorer = Object.FindAnyObjectByType<ExplorerWorldUI>();
                Assert.IsNotNull(explorer);
                Canvas explorerCanvas = explorer.PanelRect.GetComponentInParent<Canvas>().rootCanvas;
                Assert.AreEqual(100, explorerCanvas.sortingOrder);
                Assert.Less(hud.Canvas.sortingOrder, explorerCanvas.sortingOrder);
                Assert.AreNotEqual(ResourceHudPlacementMode.FullWidthTop, hud.Placement.mode, "Explorer layout must adapt.");

                Rect bar = BarScreenRect(hud);
                Rect panel = ScreenRect(explorer.PanelRect);
                Assert.IsFalse(bar.Overlaps(panel), $"HUD {bar} overlaps the Explorer panel {panel}.");
                Transform indicator = explorer.PanelRect.parent.Find(ResourceHud.ExplorerIndicatorName);
                Assert.IsNotNull(indicator, "Explorer indicator found by name.");
                Rect ind = ScreenRect((RectTransform)indicator);
                Assert.IsFalse(bar.Overlaps(ind), $"HUD {bar} overlaps the Explorer indicator {ind}.");
                Transform controls = explorer.PanelRect.parent.Find("Controls Card");
                if (controls != null)
                {
                    Rect c = ScreenRect((RectTransform)controls);
                    Assert.IsFalse(bar.Overlaps(c), $"HUD {bar} overlaps the Explorer controls {c}.");
                }

                manager.TrySpend(ResourceType.Fuel, 10_000);
                Assert.AreEqual("90,000", hud.Items[(int)ResourceType.Fuel].Value.text, "Live in World Explorer.");

                // --- back to MainBuild
                yield return LoadScene(ResourceManagerTests.MainBuildPath);
                yield return null;
                Assert.AreSame(hud, AssertSingleHud());
                Assert.AreEqual(ResourceHudPlacementMode.FullWidthTop, hud.Placement.mode);
                Assert.AreEqual("90,000", hud.Items[(int)ResourceType.Fuel].Value.text);
                Assert.AreEqual(1, ResourceHudTests.CountHudHandlers(manager, "ResourceChanged"));
                Assert.AreEqual(1, ResourceManager.LedgersCreatedThisSession, "The HUD never creates a ledger.");

                // Ledger reset in Play Mode reaches the HUD.
                Assert.IsTrue(manager.ResetToInitialConfiguration());
                Assert.AreEqual("900,000", hud.Items[(int)ResourceType.Iron].Value.text);
            }
            finally
            {
                Time.timeScale = 1f;
            }
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator WorldExplorer_ShowsHud_AndDestroyedHudUnsubscribes()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Time.timeScale = 1f;
                yield return LoadScene(ResourceManagerTests.WorldExplorerPath);
                yield return null;
                ResourceHud hud = AssertSingleHud();
                ResourceManager manager = ResourceManager.Instance;
                Assert.AreEqual("10,000", hud.Items[(int)ResourceType.Gold].Value.text);
                Assert.AreEqual("—", hud.Items[(int)ResourceType.GroupLimit].Value.text);

                // Real-time replenishment through Update -> ledger -> event -> HUD.
                manager.TrySpend(ResourceType.Aluminium, 50_000);
                float start = Time.realtimeSinceStartup;
                while (manager.Ledger.GetBalance(ResourceType.Aluminium) == 350_000)
                {
                    Assert.Less(Time.realtimeSinceStartup - start, 60f, "No replenishment within 60 s.");
                    yield return null;
                }
                yield return null;
                Assert.AreEqual(ResourceHudFormat.Amount(manager.Ledger.GetBalance(ResourceType.Aluminium)),
                    hud.Items[(int)ResourceType.Aluminium].Value.text);
                StringAssert.StartsWith("NEXT ", hud.Items[(int)ResourceType.Aluminium].Status.text);

                // Destroy: subscriptions removed; the next scene load creates exactly one new HUD.
                Object.Destroy(hud.gameObject);
                yield return null;
                Assert.IsTrue(hud == null);
                Assert.AreEqual(0, ResourceHudTests.CountHudHandlers(manager, "ResourceChanged"));
                Assert.AreEqual(0, ResourceHudTests.CountHudHandlers(manager, "LedgerReset"));

                yield return LoadScene(ResourceManagerTests.WorldExplorerPath);
                yield return null;
                ResourceHud again = AssertSingleHud();
                Assert.AreEqual(1, ResourceHudTests.CountHudHandlers(manager, "ResourceChanged"));
                Assert.AreEqual(ResourceHudFormat.Amount(manager.Ledger.GetBalance(ResourceType.Aluminium)),
                    again.Items[(int)ResourceType.Aluminium].Value.text);
            }
            finally
            {
                Time.timeScale = 1f;
            }
            yield return new ExitPlayMode();
        }

        // ------------------------------------------------------------ helpers

        private static ResourceHud AssertSingleHud()
        {
            ResourceHud[] all = Object.FindObjectsByType<ResourceHud>(FindObjectsInactive.Include);
            Assert.AreEqual(1, all.Length, "Exactly one Resource HUD.");
            Assert.AreSame(all[0], ResourceHud.Instance);
            int canvases = 0;
            foreach (Canvas c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
                if (c.name == ResourceHud.CanvasName) canvases++;
            Assert.AreEqual(1, canvases, "Exactly one HUD canvas.");
            return all[0];
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>Screen rect (pixels, bottom-left origin) of an overlay-canvas element.</summary>
        private static Rect ScreenRect(RectTransform rt)
        {
            rt.GetWorldCorners(Corners);
            return Rect.MinMaxRect(Corners[0].x, Corners[0].y, Corners[2].x, Corners[2].y);
        }

        private static Rect BarScreenRect(ResourceHud hud) => ScreenRect(hud.Bar);

        private static bool HudHit(Vector2 screenPoint)
        {
            EventSystem es = EventSystem.current ?? Object.FindAnyObjectByType<EventSystem>();
            var data = new PointerEventData(es) { position = screenPoint };
            var results = new List<RaycastResult>();
            es.RaycastAll(data, results);
            foreach (RaycastResult r in results)
                if (r.gameObject != null && r.gameObject.GetComponentInParent<ResourceHud>() != null) return true;
            return false;
        }

        private static IEnumerator LoadScene(string path)
        {
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
            float start = Time.realtimeSinceStartup;
            while (!scene.isLoaded)
            {
                if (Time.realtimeSinceStartup - start > SceneLoadTimeoutSeconds)
                    Assert.Fail($"Timed out loading {path}.");
                yield return null;
            }
            yield return null;
        }
    }
}
