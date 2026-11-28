using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>
    /// Edit-mode tests for <see cref="ResourceManager"/> and <see cref="ResourceHudBootstrap"/> logic, plus checks
    /// of the scene files. Play Mode behaviour (DontDestroyOnLoad, real scene loads, Time.deltaTime) is in
    /// <see cref="ResourceManagerPlayModeTests"/>.
    /// </summary>
    public class ResourceManagerTests
    {
        internal const string ConfigPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        internal const string MainBuildPath = "Assets/Scenes/Ironveil - MainBuild.unity";
        internal const string WorldExplorerPath = "Assets/Scenes/Ironveil - World Explorer.unity";
        private const string BootstrapScriptPath = "Assets/Scripts/Economy/ResourceHudBootstrap.cs";

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include))
                _created.Add(m.gameObject);
            foreach (Object o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        internal static HomeBaseResourceConfig LoadConfig()
        {
            var config = AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(ConfigPath);
            Assert.IsNotNull(config, ConfigPath + " is missing.");
            return config;
        }

        private ResourceManager NewManager()
        {
            ResourceManager m = ResourceManager.GetOrCreate();
            _created.Add(m.gameObject);
            return m;
        }

        private ResourceHudBootstrap NewBootstrap(HomeBaseResourceConfig config)
        {
            var go = new GameObject("Test Bootstrap");
            _created.Add(go);
            var bootstrap = go.AddComponent<ResourceHudBootstrap>(); // no Awake in Edit Mode; Run() is called explicitly
            bootstrap.EditorSetConfig(config);
            return bootstrap;
        }

        private HomeBaseResourceConfig InvalidConfig()
        {
            var entries = new List<ResourceConfigEntry>(LoadConfig().Entries);
            entries.RemoveAll(e => e.Resource == ResourceType.Iron);
            var config = HomeBaseResourceConfig.CreateRuntime(entries);
            config.name = "InvalidTestConfig";
            _created.Add(config);
            return config;
        }

        // ------------------------------------------------------------ 1. correct asset, in both scenes

        [TestCase(MainBuildPath)]
        [TestCase(WorldExplorerPath)]
        public void Scene_HasExactlyOneBootstrap_ReferencingTheSharedConfigAsset(string scenePath)
        {
            string scriptGuid = AssetDatabase.AssetPathToGUID(BootstrapScriptPath);
            string configGuid = AssetDatabase.AssetPathToGUID(ConfigPath);
            Assert.IsNotEmpty(scriptGuid);
            Assert.IsNotEmpty(configGuid);

            string yaml = File.ReadAllText(scenePath);
            Assert.AreEqual(1, Regex.Matches(yaml, "guid: " + scriptGuid).Count, "Exactly one ResourceHudBootstrap.");
            Assert.AreEqual(1, Regex.Matches(yaml, @"homeBaseConfig: \{fileID: 11400000, guid: " + configGuid + @", type: 2\}").Count);

            CollectionAssert.Contains(AssetDatabase.GetDependencies(scenePath, false), ConfigPath);
        }

        [Test]
        public void OnlyOneHomeBaseResourceConfigAssetExists()
        {
            string[] guids = AssetDatabase.FindAssets("t:" + nameof(HomeBaseResourceConfig));
            Assert.AreEqual(1, guids.Length, string.Join(", ", Array.ConvertAll(guids, AssetDatabase.GUIDToAssetPath)));
            Assert.AreEqual(ConfigPath, AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        [Test]
        public void Bootstrap_InitializesManagerFromItsAsset()
        {
            HomeBaseResourceConfig config = LoadConfig();
            ResourceHudBootstrap bootstrap = NewBootstrap(config);
            Assert.IsTrue(bootstrap.Run());
            Assert.IsTrue(bootstrap.LastRunSucceeded);

            ResourceManager m = ResourceManager.Instance;
            Assert.IsNotNull(m);
            _created.Add(m.gameObject);
            Assert.IsTrue(m.IsInitialized);
            Assert.AreSame(config, m.Config);
            Assert.IsNull(m.LastError);
        }

        // ------------------------------------------------------------ 2. initial balances

        [Test]
        public void InitialBalances_MatchHomeBaseConfiguration()
        {
            ResourceManager m = NewManager();
            Assert.IsTrue(m.Initialize(LoadConfig()));

            var expected = new Dictionary<ResourceType, long>
            {
                { ResourceType.Concrete, 1_500_000 }, { ResourceType.Iron, 900_000 },
                { ResourceType.Aluminium, 400_000 }, { ResourceType.Fuel, 100_000 },
                { ResourceType.Gold, 10_000 }, { ResourceType.Economy, 1_000 }, { ResourceType.Military, 1_000 },
            };
            foreach (KeyValuePair<ResourceType, long> pair in expected)
            {
                Assert.IsTrue(m.TryGetBalance(pair.Key, out long balance), pair.Key.ToString());
                Assert.AreEqual(pair.Value, balance, pair.Key.ToString());
            }

            Assert.IsFalse(m.IsConfigured(ResourceType.GroupLimit));
            Assert.IsFalse(m.TryGetCapacity(ResourceType.Economy, out _));
            Assert.IsTrue(m.TryGetCapacity(ResourceType.Concrete, out long cap));
            Assert.AreEqual(1_500_000, cap);
        }

        // ------------------------------------------------------------ 3 / 4 / 10. single manager, single ledger, idempotent

        [Test]
        public void GetOrCreate_NeverCreatesASecondManager()
        {
            ResourceManager first = NewManager();
            ResourceManager second = ResourceManager.GetOrCreate();
            Assert.AreSame(first, second);
            Assert.AreEqual(1, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length);
        }

        [Test]
        public void RepeatedBootstrapRuns_ReuseTheSameManagerAndLedger()
        {
            HomeBaseResourceConfig config = LoadConfig();
            int ledgersBefore = ResourceManager.LedgersCreatedThisSession;

            Assert.IsTrue(NewBootstrap(config).Run());
            ResourceManager m = ResourceManager.Instance;
            _created.Add(m.gameObject);
            ResourceLedger ledger = m.Ledger;
            Assert.IsTrue(m.TrySpend(ResourceType.Concrete, 100_000));
            m.Tick(2f);

            for (int i = 0; i < 5; i++)
                Assert.IsTrue(NewBootstrap(config).Run(), $"run {i}");

            Assert.AreSame(m, ResourceManager.Instance);
            Assert.AreSame(ledger, m.Ledger, "The live ledger must never be replaced by a bootstrap.");
            Assert.AreEqual(ledgersBefore + 1, ResourceManager.LedgersCreatedThisSession, "Exactly one ledger created.");
            Assert.AreEqual(1, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length);
            Assert.AreEqual(1_400_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Concrete));
            Assert.AreEqual(TimeSpan.FromSeconds(2), ledger.CurrentTime, "Timers are not reset.");
        }

        [Test]
        public void RepeatedRuns_DoNotDuplicateEventForwarding()
        {
            HomeBaseResourceConfig config = LoadConfig();
            Assert.IsTrue(NewBootstrap(config).Run());
            ResourceManager m = ResourceManager.Instance;
            _created.Add(m.gameObject);
            for (int i = 0; i < 3; i++) NewBootstrap(config).Run();
            for (int i = 0; i < 3; i++) m.Initialize(config);

            int events = 0;
            m.ResourceChanged += _ => events++;
            m.TrySpend(ResourceType.Fuel, 1_000);
            Assert.AreEqual(1, events);
            m.Tick(5f);
            Assert.AreEqual(2, events);
        }

        [Test]
        public void Initialize_WithDifferentConfig_IsRefused_AndLiveLedgerKept()
        {
            ResourceManager m = NewManager();
            Assert.IsTrue(m.Initialize(LoadConfig()));
            ResourceLedger ledger = m.Ledger;
            m.TrySpend(ResourceType.Gold, 500);

            HomeBaseResourceConfig other = HomeBaseResourceConfig.CreateFromProfile();
            other.name = "OtherConfig";
            _created.Add(other);

            LogAssert.Expect(LogType.Error, new Regex("refusing to replace the live ledger"));
            Assert.IsFalse(m.Initialize(other));
            Assert.AreSame(ledger, m.Ledger);
            Assert.AreSame(LoadConfig(), m.Config);
            Assert.IsTrue(m.TryGetBalance(ResourceType.Gold, out long gold));
            Assert.AreEqual(9_500, gold);
        }

        // ------------------------------------------------------------ 5. spending reaches the ledger

        [Test]
        public void Spending_ReachesTheLedger_AndIsForwarded()
        {
            ResourceManager m = NewManager();
            m.Initialize(LoadConfig());
            var changes = new List<ResourceChange>();
            m.ResourceChanged += changes.Add;

            Assert.IsTrue(m.CanSpend(ResourceType.Iron, 250_000));
            Assert.IsTrue(m.TrySpend(ResourceType.Iron, 250_000, out var result));
            Assert.AreEqual(ResourceTransactionResult.Success, result);
            Assert.AreEqual(650_000, m.Ledger.GetBalance(ResourceType.Iron));
            Assert.AreEqual(1, m.Ledger.GetActiveCycleCount(ResourceType.Iron));

            Assert.IsFalse(m.TrySpend(ResourceType.Fuel, 1_000_000, out result));
            Assert.AreEqual(ResourceTransactionResult.InsufficientBalance, result);

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual(new ResourceChange(ResourceType.Iron, 900_000, 650_000, ResourceChangeReason.Spent), changes[0]);
        }

        [Test]
        public void UninitializedManager_RejectsEverythingSafely()
        {
            ResourceManager m = NewManager();
            Assert.IsFalse(m.IsInitialized);
            Assert.IsFalse(m.TrySpend(ResourceType.Concrete, 1, out var result));
            Assert.AreEqual(ResourceTransactionResult.NotConfigured, result);
            Assert.IsFalse(m.CanSpend(ResourceType.Concrete, 1));
            Assert.IsFalse(m.TryGetBalance(ResourceType.Concrete, out _));
            Assert.DoesNotThrow(() => m.Tick(5f));
        }

        [Test]
        public void ThrowingSubscriber_DoesNotBlockOthers()
        {
            ResourceManager m = NewManager();
            m.Initialize(LoadConfig());
            int good = 0;
            m.ResourceChanged += _ => throw new InvalidOperationException("bad subscriber");
            m.ResourceChanged += _ => good++;

            LogAssert.Expect(LogType.Exception, new Regex("bad subscriber"));
            m.TrySpend(ResourceType.Concrete, 1);
            Assert.AreEqual(1, good);
        }

        // ------------------------------------------------------------ 6 / 7. gameplay time (logic level)

        [Test]
        public void Tick_AdvancesReplenishment_WithScaledSeconds()
        {
            ResourceManager m = NewManager();
            m.Initialize(LoadConfig());
            m.TrySpend(ResourceType.Aluminium, 100_000);

            for (int i = 0; i < 299; i++) m.Tick(1f / 60f);
            Assert.AreEqual(300_000, m.Ledger.GetBalance(ResourceType.Aluminium));
            m.Tick(1f / 60f);
            Assert.AreEqual(310_000, m.Ledger.GetBalance(ResourceType.Aluminium));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void Tick_WithPausedOrInvalidDelta_DoesNothing(float delta)
        {
            ResourceManager m = NewManager();
            m.Initialize(LoadConfig());
            m.TrySpend(ResourceType.Concrete, 100_000);
            for (int i = 0; i < 1_000; i++) m.Tick(delta);
            Assert.AreEqual(TimeSpan.Zero, m.Ledger.CurrentTime);
            Assert.AreEqual(1_400_000, m.Ledger.GetBalance(ResourceType.Concrete));
        }

        // ------------------------------------------------------------ 11. invalid configuration

        [Test]
        public void Bootstrap_WithInvalidConfig_ReportsError_AndCreatesNoManager()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"InvalidTestConfig' is invalid(.|\n)*Iron is missing"));
            ResourceHudBootstrap bootstrap = NewBootstrap(InvalidConfig());
            Assert.IsFalse(bootstrap.Run());
            Assert.IsFalse(bootstrap.LastRunSucceeded);
            Assert.IsTrue(ResourceManager.Instance == null);
            Assert.AreEqual(0, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length);
        }

        [Test]
        public void Bootstrap_WithMissingConfig_ReportsError_AndCreatesNoManager()
        {
            LogAssert.Expect(LogType.Error, new Regex("no HomeBaseResourceConfig assigned"));
            Assert.IsFalse(NewBootstrap(null).Run());
            Assert.AreEqual(0, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length);
        }

        [Test]
        public void Manager_WithInvalidConfig_StaysCompletelyUninitialized()
        {
            ResourceManager m = NewManager();
            int ledgersBefore = ResourceManager.LedgersCreatedThisSession;

            LogAssert.Expect(LogType.Error, new Regex("Iron is missing"));
            Assert.IsFalse(m.Initialize(InvalidConfig()));
            Assert.IsFalse(m.IsInitialized);
            Assert.IsNull(m.Ledger);
            Assert.IsNull(m.Config);
            StringAssert.Contains("Iron is missing", m.LastError);
            Assert.AreEqual(ledgersBefore, ResourceManager.LedgersCreatedThisSession);

            LogAssert.Expect(LogType.Error, new Regex("no HomeBaseResourceConfig"));
            Assert.IsFalse(m.Initialize(null));
            Assert.IsFalse(m.IsInitialized);

            // A later valid initialization still works.
            Assert.IsTrue(m.Initialize(LoadConfig()));
            Assert.IsNull(m.LastError);
        }

        [Test]
        public void Bootstrap_WithInvalidConfig_DoesNotDisturbInitializedManager()
        {
            ResourceManager m = NewManager();
            m.Initialize(LoadConfig());
            ResourceLedger ledger = m.Ledger;

            LogAssert.Expect(LogType.Error, new Regex("refusing to replace the live ledger"));
            Assert.IsFalse(NewBootstrap(InvalidConfig()).Run());
            Assert.AreSame(ledger, m.Ledger);
        }

        // ------------------------------------------------------------ 12. reset API

        [Test]
        public void Reset_RestoresInitialBalances_ClearsCycles_KeepsSubscribers()
        {
            ResourceManager m = NewManager();
            m.Initialize(LoadConfig());
            ResourceLedger old = m.Ledger;
            m.TrySpend(ResourceType.Concrete, 500_000);
            m.TrySpend(ResourceType.Concrete, 1);
            m.TrySpend(ResourceType.Gold, 9_000);
            m.Tick(7f);

            int resets = 0, changes = 0;
            m.LedgerReset += () => resets++;
            m.ResourceChanged += _ => changes++;

            Assert.IsTrue(m.ResetToInitialConfiguration());
            Assert.AreEqual(1, resets);
            Assert.AreNotSame(old, m.Ledger);
            Assert.AreEqual(TimeSpan.Zero, m.Ledger.CurrentTime);
            for (int i = 0; i < ResourceTypeRules.Count; i++)
                Assert.AreEqual(0, m.Ledger.GetActiveCycleCount((ResourceType)i));
            Assert.AreEqual(1_500_000, m.Ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(10_000, m.Ledger.GetBalance(ResourceType.Gold));

            // Old ledger is detached; new ledger forwards exactly once.
            old.TrySpend(ResourceType.Iron, 1);
            Assert.AreEqual(0, changes);
            m.TrySpend(ResourceType.Iron, 1);
            Assert.AreEqual(1, changes);
            m.Tick(60f);
            Assert.AreEqual(900_000, m.Ledger.GetBalance(ResourceType.Iron));
        }

        [Test]
        public void Reset_WhenUninitialized_FailsSafely()
        {
            ResourceManager m = NewManager();
            LogAssert.Expect(LogType.Error, new Regex("cannot reset"));
            Assert.IsFalse(m.ResetToInitialConfiguration());
            Assert.IsNull(m.Ledger);
        }

        [Test]
        public void Reset_IsNeverCalledByGameCode()
        {
            foreach (string file in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.EndsWith("Economy/ResourceManager.cs") || normalized.Contains("/Editor/Tests/")) continue;
                StringAssert.DoesNotContain("ResetToInitialConfiguration", File.ReadAllText(file), normalized);
            }
        }
    }
}
