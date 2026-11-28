using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>
    /// Real Play Mode tests: enter Play Mode, load the actual MainBuild and World Explorer scenes with
    /// LoadSceneMode.Single (World Explorer is not in Build Settings, so EditorSceneManager.LoadSceneInPlayMode is
    /// used), and drive replenishment with Unity's own Time.deltaTime and Time.timeScale.
    ///
    /// Scene content unrelated to the economy (world generation, cameras) may log errors in batch mode, so failing
    /// log messages are ignored here. Economy behaviour is asserted directly.
    /// </summary>
    public class ResourceManagerPlayModeTests
    {
        private const float SceneLoadTimeoutSeconds = 120f;

        [UnityTest]
        public IEnumerator MainBuild_To_WorldExplorer_And_Back_PreservesTheSingleLedger()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                HomeBaseResourceConfig asset = ResourceManagerTests.LoadConfig();

                // --- MainBuild
                yield return LoadScene(ResourceManagerTests.MainBuildPath);
                ResourceManager manager = ResourceManager.Instance;
                Assert.IsNotNull(manager, "MainBuild's bootstrap must create the manager.");
                Assert.IsTrue(manager.IsInitialized, manager.LastError);
                Assert.AreSame(asset, manager.Config, "Initialized from Assets/Data/Economy/HomeBaseResourceConfig.asset.");
                Assert.AreEqual("DontDestroyOnLoad", manager.gameObject.scene.name);
                AssertSingleton(manager, 1);
                Assert.IsTrue(manager.TryGetBalance(ResourceType.Concrete, out long concrete0));
                Assert.AreEqual(1_500_000, concrete0);

                // Pause, so scene loads cannot change balances and before/after can be compared exactly.
                Time.timeScale = 0f;
                yield return null;
                ResourceLedger ledger = manager.Ledger;
                Assert.IsTrue(manager.TrySpend(ResourceType.Concrete, 100_000));
                Assert.IsTrue(manager.TrySpend(ResourceType.Iron, 50_000));
                Assert.IsTrue(manager.TrySpend(ResourceType.Iron, 50_000));
                TimeSpan clockAtSpend = ledger.CurrentTime;

                // 7. Paused time does not advance replenishment.
                for (int i = 0; i < 30; i++) yield return null;
                Assert.AreEqual(clockAtSpend, ledger.CurrentTime, "Clock must not move while Time.timeScale = 0.");
                Assert.AreEqual(1_400_000, ledger.GetBalance(ResourceType.Concrete));

                int forwarded = 0;
                manager.ResourceChanged += _ => forwarded++;

                // --- World Explorer (8, 9, 10)
                yield return LoadScene(ResourceManagerTests.WorldExplorerPath);
                AssertPreserved(manager, ledger, clockAtSpend);
                Assert.AreEqual(1, Object.FindObjectsByType<ResourceHudBootstrap>(FindObjectsInactive.Include).Length);
                Assert.IsTrue(Object.FindAnyObjectByType<ResourceHudBootstrap>().LastRunSucceeded);

                manager.TrySpend(ResourceType.Fuel, 1_000);
                Assert.AreEqual(1, forwarded, "Exactly one forwarded event per change after a scene switch.");

                // --- back to MainBuild
                yield return LoadScene(ResourceManagerTests.MainBuildPath);
                AssertPreserved(manager, ledger, clockAtSpend);
                Assert.AreEqual(99_000, ledger.GetBalance(ResourceType.Fuel));

                // 6. Unpause: replenishment advances with Time.deltaTime.
                Time.timeScale = 1f;
                double observedScaled = 0;
                float startReal = Time.realtimeSinceStartup;
                while (ledger.CurrentTime - clockAtSpend < TimeSpan.FromSeconds(5.2))
                {
                    yield return null;
                    observedScaled += Time.deltaTime;
                    Assert.Less(Time.realtimeSinceStartup - startReal, 60f, "Gameplay time did not advance.");
                }

                // The test coroutine is pumped by the editor loop, not the player loop, so its samples of
                // Time.deltaTime do not line up frame for frame with ResourceManager.Update. Check bounds instead:
                // the ledger can never run ahead of real time at timeScale 1 (plus one capped frame).
                double ledgerAdvanced = (ledger.CurrentTime - clockAtSpend).TotalSeconds;
                double realElapsed = Time.realtimeSinceStartup - startReal;
                Assert.LessOrEqual(ledgerAdvanced, realElapsed + Time.maximumDeltaTime + 0.05, "Ledger clock must follow gameplay time, not run ahead.");
                Assert.Greater(observedScaled, 0.0, "Time.deltaTime advanced.");
                long ticks = (long)Math.Floor(ledgerAdvanced / 5.0);
                Assert.GreaterOrEqual(ticks, 1);
                Assert.AreEqual(1_400_000 + ticks * 30_000, ledger.GetBalance(ResourceType.Concrete));
                Assert.AreEqual(800_000 + 2 * ticks * 20_000, ledger.GetBalance(ResourceType.Iron), "Both Iron cycles tick.");
                Assert.AreEqual(1, ResourceManager.LedgersCreatedThisSession);
            }
            finally
            {
                Time.timeScale = 1f;
            }
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator WorldExplorer_First_CreatesManager_AndReloadReusesIt()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Time.timeScale = 0f;
                yield return LoadScene(ResourceManagerTests.WorldExplorerPath);
                ResourceManager manager = ResourceManager.Instance;
                Assert.IsNotNull(manager, "World Explorer's bootstrap must create the manager.");
                Assert.IsTrue(manager.IsInitialized, manager.LastError);
                Assert.AreSame(ResourceManagerTests.LoadConfig(), manager.Config);
                AssertSingleton(manager, 1);
                Assert.IsTrue(manager.TryGetBalance(ResourceType.Gold, out long gold));
                Assert.AreEqual(10_000, gold);

                ResourceLedger ledger = manager.Ledger;
                manager.TrySpend(ResourceType.Aluminium, 40_000);
                TimeSpan clock = ledger.CurrentTime;

                yield return LoadScene(ResourceManagerTests.WorldExplorerPath); // same scene again
                Assert.AreSame(manager, ResourceManager.Instance);
                Assert.AreSame(ledger, manager.Ledger);
                Assert.AreEqual(360_000, ledger.GetBalance(ResourceType.Aluminium));
                Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Aluminium));
                Assert.AreEqual(clock, ledger.CurrentTime);
                AssertSingleton(manager, 1);
            }
            finally
            {
                Time.timeScale = 1f;
            }
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ExtraBootstrapsAndManagers_InPlayMode_AreRejected()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Time.timeScale = 0f;
                yield return LoadScene(ResourceManagerTests.MainBuildPath);
                ResourceManager manager = ResourceManager.Instance;
                Assert.IsNotNull(manager);
                ResourceLedger ledger = manager.Ledger;
                manager.TrySpend(ResourceType.Concrete, 1);

                // Extra bootstrap waking up in the same scene (its Awake runs on activation).
                var go = new GameObject("Extra Bootstrap");
                go.SetActive(false);
                go.AddComponent<ResourceHudBootstrap>().EditorSetConfig(ResourceManagerTests.LoadConfig());
                go.SetActive(true);
                Assert.IsTrue(go.GetComponent<ResourceHudBootstrap>().LastRunSucceeded);

                // A stray manager component is removed by its own Awake.
                var stray = new GameObject("Stray Manager").AddComponent<ResourceManager>();
                yield return null;
                Assert.IsTrue(stray == null, "Duplicate manager component must be destroyed.");

                AssertSingleton(manager, 1);
                Assert.AreSame(ledger, manager.Ledger);
                Assert.AreEqual(1_499_999, ledger.GetBalance(ResourceType.Concrete));
                Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Concrete));
            }
            finally
            {
                Time.timeScale = 1f;
            }
            yield return new ExitPlayMode();
        }

        // ------------------------------------------------------------ helpers

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
            yield return null; // let Start run
            Assert.AreEqual(scene, SceneManager.GetActiveScene(), $"{path} should be the active scene.");
        }

        private static void AssertSingleton(ResourceManager manager, int expectedLedgers)
        {
            Assert.AreEqual(1, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length, "Exactly one ResourceManager.");
            Assert.AreSame(manager, ResourceManager.Instance);
            Assert.AreEqual(expectedLedgers, ResourceManager.LedgersCreatedThisSession, "Exactly one ledger this session.");
        }

        private static void AssertPreserved(ResourceManager manager, ResourceLedger ledger, TimeSpan clock)
        {
            AssertSingleton(manager, 1);
            Assert.AreSame(ledger, manager.Ledger, "Scene switch must not create another ledger.");
            Assert.AreEqual(clock, ledger.CurrentTime, "Replenishment timers must not reset.");
            Assert.AreEqual(1_400_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(800_000, ledger.GetBalance(ResourceType.Iron));
            Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Concrete), "Concrete cycle survives.");
            Assert.AreEqual(2, ledger.GetActiveCycleCount(ResourceType.Iron), "Both Iron cycles survive, not duplicated.");
        }
    }
}
