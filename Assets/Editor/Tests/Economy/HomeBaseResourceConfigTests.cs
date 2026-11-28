using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>Edit-mode tests for <see cref="HomeBaseResourceConfig"/> and its asset.</summary>
    public class HomeBaseResourceConfigTests
    {
        private const string AssetPath = "Assets/Data/Economy/HomeBaseResourceConfig.asset";
        private const long FiveSecondsMs = 5_000;

        private readonly List<Object> _runtimeInstances = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _runtimeInstances)
                if (o != null) Object.DestroyImmediate(o);
            _runtimeInstances.Clear();
        }

        private static HomeBaseResourceConfig LoadAsset()
        {
            var asset = AssetDatabase.LoadAssetAtPath<HomeBaseResourceConfig>(AssetPath);
            Assert.IsNotNull(asset, $"Missing {AssetPath}. Run Tools/IRONVEIL/Economy/Create Home Base Resource Config.");
            return asset;
        }

        private HomeBaseResourceConfig Runtime(IEnumerable<ResourceConfigEntry> entries)
        {
            var config = HomeBaseResourceConfig.CreateRuntime(entries);
            _runtimeInstances.Add(config);
            return config;
        }

        private static List<ResourceConfigEntry> ValidEntries()
        {
            var list = new List<ResourceConfigEntry>();
            foreach (ResourceConfig c in HomeBaseResourceProfile.CreateSinglePlayerConfigs())
                list.Add(ResourceConfigEntry.FromConfig(c));
            return list;
        }

        private static ResourceConfigEntry Find(HomeBaseResourceConfig config, ResourceType resource)
        {
            foreach (ResourceConfigEntry e in config.Entries)
                if (e.Resource == resource) return e;
            Assert.Fail($"{resource} has no entry.");
            return default;
        }

        // ------------------------------------------------------------ asset

        [Test]
        public void Asset_ExistsAndIsValid()
        {
            var errors = new List<string>();
            Assert.IsTrue(LoadAsset().Validate(errors), string.Join("\n", errors));
        }

        [Test]
        public void Asset_MatchesHomeBaseResourceProfile_ExactlyAndInOrder()
        {
            IReadOnlyList<ResourceConfigEntry> asset = LoadAsset().Entries;
            List<ResourceConfigEntry> profile = ValidEntries();
            Assert.AreEqual(profile.Count, asset.Count);
            for (int i = 0; i < profile.Count; i++)
                Assert.AreEqual(profile[i], asset[i], $"Entry {i} ({profile[i].Resource}) differs from HomeBaseResourceProfile.");
        }

        // ------------------------------------------------------------ finalized values

        [TestCase(ResourceType.Concrete, 1_300_000L)]
        [TestCase(ResourceType.Iron, 900_000L)]
        [TestCase(ResourceType.Aluminium, 400_000L)]
        [TestCase(ResourceType.Fuel, 100_000L)]
        [TestCase(ResourceType.Gold, 10_000L)]
        [TestCase(ResourceType.Economy, 1_000L)]
        [TestCase(ResourceType.Military, 1_000L)]
        public void StartingBalances_AreFinalizedValues(ResourceType resource, long expected)
        {
            HomeBaseResourceConfig asset = LoadAsset();
            Assert.AreEqual(expected, Find(asset, resource).StartingBalance);
            Assert.AreEqual(expected, asset.CreateLedger().GetBalance(resource));
        }

        [TestCase(ResourceType.Concrete, 1_300_000L)]
        [TestCase(ResourceType.Iron, 900_000L)]
        [TestCase(ResourceType.Aluminium, 400_000L)]
        [TestCase(ResourceType.Fuel, 100_000L)]
        [TestCase(ResourceType.Gold, 10_000L)]
        public void Capacities_AreFinalizedValues(ResourceType resource, long expected)
        {
            HomeBaseResourceConfig asset = LoadAsset();
            ResourceConfigEntry entry = Find(asset, resource);
            Assert.IsTrue(entry.HasCapacity);
            Assert.AreEqual(expected, entry.Capacity);
            Assert.AreEqual(expected, asset.CreateLedger().GetCapacity(resource));
        }

        [TestCase(ResourceType.Concrete, 30_000L)]
        [TestCase(ResourceType.Iron, 20_000L)]
        [TestCase(ResourceType.Aluminium, 10_000L)]
        [TestCase(ResourceType.Fuel, 5_000L)]
        public void ReplenishmentRates_AreFinalizedValues_AndApplyEveryFiveSeconds(ResourceType resource, long rate)
        {
            HomeBaseResourceConfig asset = LoadAsset();
            ResourceConfigEntry entry = Find(asset, resource);
            Assert.IsTrue(entry.Replenishes);
            Assert.AreEqual(rate, entry.ReplenishAmount);
            Assert.AreEqual(FiveSecondsMs, entry.ReplenishIntervalMilliseconds);

            ResourceLedger ledger = asset.CreateLedger();
            long start = ledger.GetBalance(resource);
            Assert.IsTrue(ledger.TrySpend(resource, rate * 3));
            ledger.Advance(TimeSpan.FromMilliseconds(4_999));
            Assert.AreEqual(start - rate * 3, ledger.GetBalance(resource));
            ledger.Advance(TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(start - rate * 2, ledger.GetBalance(resource));
            ledger.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(start - rate, ledger.GetBalance(resource));
        }

        [Test]
        public void Gold_HasFinalizedCapacity_AndDoesNotReplenish()
        {
            HomeBaseResourceConfig asset = LoadAsset();
            ResourceConfigEntry gold = Find(asset, ResourceType.Gold);
            Assert.AreEqual(10_000, gold.StartingBalance);
            Assert.IsTrue(gold.HasCapacity);
            Assert.AreEqual(10_000, gold.Capacity);
            Assert.IsFalse(gold.Replenishes);
            Assert.AreEqual(0, gold.ReplenishAmount);
            Assert.AreEqual(0, gold.ReplenishIntervalMilliseconds);

            ResourceLedger ledger = asset.CreateLedger();
            Assert.IsFalse(ledger.IsReplenishing(ResourceType.Gold));
            Assert.IsTrue(ledger.TrySpend(ResourceType.Gold, 4_000));
            ledger.Advance(TimeSpan.FromHours(1));
            Assert.AreEqual(6_000, ledger.GetBalance(ResourceType.Gold));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Gold));
        }

        [TestCase(ResourceType.Economy)]
        [TestCase(ResourceType.Military)]
        public void EconomyAndMilitary_HaveNoConfiguredCapacity_AndDoNotReplenish(ResourceType resource)
        {
            HomeBaseResourceConfig asset = LoadAsset();
            ResourceConfigEntry entry = Find(asset, resource);
            Assert.IsFalse(entry.HasCapacity, "Capacity is undecided and must stay explicitly unconfigured.");
            Assert.AreEqual(0, entry.Capacity, "No placeholder capacity value may be stored.");
            Assert.IsFalse(entry.Replenishes);

            ResourceLedger ledger = asset.CreateLedger();
            Assert.IsTrue(ledger.IsConfigured(resource));
            Assert.IsFalse(ledger.HasCapacity(resource));
            Assert.IsNull(ledger.GetCapacity(resource));
            Assert.IsFalse(ledger.IsReplenishing(resource));
        }

        [Test]
        public void GroupLimit_RemainsUnconfigured()
        {
            HomeBaseResourceConfig asset = LoadAsset();
            foreach (ResourceConfigEntry e in asset.Entries)
                Assert.AreNotEqual(ResourceType.GroupLimit, e.Resource, "GroupLimit must not be configured yet.");
            CollectionAssert.DoesNotContain(HomeBaseResourceConfig.RequiredResources, ResourceType.GroupLimit);

            ResourceLedger ledger = asset.CreateLedger();
            Assert.IsFalse(ledger.IsConfigured(ResourceType.GroupLimit));
            Assert.IsFalse(ledger.TrySpend(ResourceType.GroupLimit, 1, out var result));
            Assert.AreEqual(ResourceTransactionResult.NotConfigured, result);
        }

        // ------------------------------------------------------------ ledger creation

        [Test]
        public void CreateLedger_ProducesCorrectlyInitializedLedger()
        {
            ResourceLedger ledger = LoadAsset().CreateLedger();
            Assert.AreEqual(TimeSpan.Zero, ledger.CurrentTime);

            int configured = 0;
            for (int i = 0; i < ResourceTypeRules.Count; i++)
            {
                var r = (ResourceType)i;
                Assert.AreEqual(0, ledger.GetActiveCycleCount(r), $"{r} must start with no replenishment cycles.");
                if (ledger.IsConfigured(r)) configured++;
            }
            Assert.AreEqual(7, configured);

            Assert.IsTrue(ledger.IsReplenishing(ResourceType.Concrete));
            Assert.IsTrue(ledger.IsReplenishing(ResourceType.Iron));
            Assert.IsTrue(ledger.IsReplenishing(ResourceType.Aluminium));
            Assert.IsTrue(ledger.IsReplenishing(ResourceType.Fuel));
        }

        [Test]
        public void AssetLedger_BehavesExactlyLikeStage1ProfileLedger()
        {
            ResourceLedger fromAsset = LoadAsset().CreateLedger();
            ResourceLedger fromProfile = HomeBaseResourceProfile.CreateSinglePlayerLedger();
            var rng = new Random(2026);

            for (int step = 0; step < 300; step++)
            {
                var r = (ResourceType)rng.Next(ResourceTypeRules.Count);
                long amount = rng.Next(1, 250_000);
                Assert.AreEqual(fromProfile.TrySpend(r, amount), fromAsset.TrySpend(r, amount));

                var elapsed = TimeSpan.FromMilliseconds(rng.Next(0, 20_000));
                fromProfile.Advance(elapsed);
                fromAsset.Advance(elapsed);

                for (int i = 0; i < ResourceTypeRules.Count; i++)
                {
                    var res = (ResourceType)i;
                    Assert.AreEqual(fromProfile.TryGetBalance(res, out long p), fromAsset.TryGetBalance(res, out long a));
                    Assert.AreEqual(p, a, $"step {step} {res}");
                    Assert.AreEqual(fromProfile.GetActiveCycleCount(res), fromAsset.GetActiveCycleCount(res), $"step {step} {res}");
                }
            }
        }

        [Test]
        public void OverlappingCycles_StillIndependent_WhenLedgerComesFromAsset()
        {
            ResourceLedger ledger = LoadAsset().CreateLedger();
            ledger.TrySpend(ResourceType.Concrete, 100_000);
            ledger.Advance(TimeSpan.FromSeconds(2));
            ledger.TrySpend(ResourceType.Concrete, 100_000);
            Assert.AreEqual(1_100_000, ledger.GetBalance(ResourceType.Concrete));
            ledger.Advance(TimeSpan.FromSeconds(3));
            Assert.AreEqual(1_130_000, ledger.GetBalance(ResourceType.Concrete));
            ledger.Advance(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1_160_000, ledger.GetBalance(ResourceType.Concrete));
        }

        // ------------------------------------------------------------ no duplication

        [Test]
        public void EachResourceIsBuiltExactlyOnce()
        {
            IReadOnlyList<ResourceConfig> configs = LoadAsset().BuildResourceConfigs();
            Assert.AreEqual(7, configs.Count);
            var seen = new HashSet<ResourceType>();
            foreach (ResourceConfig c in configs)
                Assert.IsTrue(seen.Add(c.Resource), $"{c.Resource} built more than once.");
        }

        [Test]
        public void LedgersAreIndependent_AndOneSpendCreatesExactlyOneCycle()
        {
            HomeBaseResourceConfig asset = LoadAsset();
            int entriesBefore = asset.Entries.Count;

            ResourceLedger first = asset.CreateLedger();
            ResourceLedger second = asset.CreateLedger();
            Assert.AreNotSame(first, second);

            Assert.IsTrue(first.TrySpend(ResourceType.Iron, 100_000));
            Assert.AreEqual(1, first.GetActiveCycleCount(ResourceType.Iron));
            Assert.AreEqual(0, second.GetActiveCycleCount(ResourceType.Iron));
            Assert.AreEqual(900_000, second.GetBalance(ResourceType.Iron));

            first.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(820_000, first.GetBalance(ResourceType.Iron), "One spend must add exactly one tick per interval.");

            ResourceLedger third = asset.CreateLedger();
            Assert.AreEqual(900_000, third.GetBalance(ResourceType.Iron));
            Assert.AreEqual(0, third.GetActiveCycleCount(ResourceType.Iron));
            Assert.AreEqual(entriesBefore, asset.Entries.Count, "Creating ledgers must not modify the asset.");
        }

        [Test]
        public void EntryRoundTrip_PreservesEveryProfileValue()
        {
            foreach (ResourceConfig original in HomeBaseResourceProfile.CreateSinglePlayerConfigs())
            {
                ResourceConfig back = ResourceConfigEntry.FromConfig(original).ToConfig();
                Assert.AreEqual(original.Resource, back.Resource);
                Assert.AreEqual(original.StartingBalance, back.StartingBalance);
                Assert.AreEqual(original.Capacity, back.Capacity);
                Assert.AreEqual(original.ReplenishAmount, back.ReplenishAmount);
                Assert.AreEqual(original.ReplenishInterval, back.ReplenishInterval);
            }
        }

        // ------------------------------------------------------------ invalid configuration

        private static IEnumerable<TestCaseData> InvalidConfigurations()
        {
            const long ms = FiveSecondsMs;
            yield return Case("empty", l => l.Clear(), "No resources");
            yield return Case("duplicate Concrete", l => l.Add(l[0]), "more than once");
            yield return Case("undefined resource", l => l.Add(new ResourceConfigEntry((ResourceType)42, 0, true, 0, false, 0, 0)), "undefined resource");
            foreach (ResourceType required in HomeBaseResourceConfig.RequiredResources)
            {
                ResourceType r = required;
                yield return Case($"missing {r}", l => l.RemoveAll(e => e.Resource == r), $"{r} is missing");
            }
            yield return Case("negative starting", l => l[4] = new ResourceConfigEntry(ResourceType.Gold, -1, true, 10_000, false, 0, 0), "Gold");
            yield return Case("starting above capacity", l => l[4] = new ResourceConfigEntry(ResourceType.Gold, 10_001, true, 10_000, false, 0, 0), "Gold");
            yield return Case("negative capacity", l => l[4] = new ResourceConfigEntry(ResourceType.Gold, 0, true, -1, false, 0, 0), "Gold");
            yield return Case("capacity value while undecided", l => l[5] = new ResourceConfigEntry(ResourceType.Economy, 1_000, false, 5_000, false, 0, 0), "Has Capacity");
            yield return Case("replenish values while not replenishing", l => l[4] = new ResourceConfigEntry(ResourceType.Gold, 10_000, true, 10_000, false, 100, ms), "'Replenishes' is off");
            yield return Case("replenishing without capacity", l => l[0] = new ResourceConfigEntry(ResourceType.Concrete, 1_500_000, false, 0, true, 30_000, ms), "must have a configured capacity");
            yield return Case("zero replenish amount", l => l[0] = new ResourceConfigEntry(ResourceType.Concrete, 1_500_000, true, 1_500_000, true, 0, ms), "Concrete");
            yield return Case("negative replenish amount", l => l[0] = new ResourceConfigEntry(ResourceType.Concrete, 1_500_000, true, 1_500_000, true, -30_000, ms), "Concrete");
            yield return Case("zero interval", l => l[0] = new ResourceConfigEntry(ResourceType.Concrete, 1_500_000, true, 1_500_000, true, 30_000, 0), "interval must be positive");
            yield return Case("huge interval", l => l[0] = new ResourceConfigEntry(ResourceType.Concrete, 1_500_000, true, 1_500_000, true, 30_000, long.MaxValue), "must not exceed");
            yield return Case("replenishing Gold", l => l[4] = new ResourceConfigEntry(ResourceType.Gold, 10_000, true, 10_000, true, 100, ms), "must not receive automatic replenishment");
            yield return Case("replenishing Economy", l => l[5] = new ResourceConfigEntry(ResourceType.Economy, 1_000, true, 1_000, true, 100, ms), "must not receive automatic replenishment");
        }

        private static TestCaseData Case(string name, Action<List<ResourceConfigEntry>> mutate, string expectedMessage) =>
            new TestCaseData(mutate, expectedMessage).SetName($"InvalidConfiguration_IsRejected({name})");

        [TestCaseSource(nameof(InvalidConfigurations))]
        public void InvalidConfiguration_IsRejected(Action<List<ResourceConfigEntry>> mutate, string expectedMessage)
        {
            List<ResourceConfigEntry> entries = ValidEntries();
            mutate(entries);
            HomeBaseResourceConfig config = Runtime(entries);

            var errors = new List<string>();
            Assert.IsFalse(config.Validate(errors));
            Assert.IsFalse(config.IsValid());
            Assert.IsTrue(errors.Exists(e => e.Contains(expectedMessage)),
                $"Expected an error containing '{expectedMessage}', got:\n{string.Join("\n", errors)}");

            var ex = Assert.Throws<InvalidOperationException>(() => config.BuildResourceConfigs());
            StringAssert.Contains(expectedMessage, ex.Message);
            Assert.Throws<InvalidOperationException>(() => config.CreateLedger());
        }

        [Test]
        public void Validation_ReportsEveryProblem_NotJustTheFirst()
        {
            List<ResourceConfigEntry> entries = ValidEntries();
            entries[4] = new ResourceConfigEntry(ResourceType.Gold, -1, true, 10_000, false, 0, 0); // invalid Gold
            entries.RemoveAll(e => e.Resource == ResourceType.Fuel);                                // missing Fuel
            entries.Add(entries[0]);                                                                // duplicate Concrete

            var errors = new List<string>();
            Assert.IsFalse(Runtime(entries).Validate(errors));
            Assert.AreEqual(3, errors.Count, string.Join("\n", errors));
        }

        [Test]
        public void ValidRuntimeConfig_FromProfile_Passes()
        {
            HomeBaseResourceConfig config = HomeBaseResourceConfig.CreateFromProfile();
            _runtimeInstances.Add(config);
            Assert.IsTrue(config.IsValid());
            Assert.AreEqual(7, config.Entries.Count);
        }

        [Test]
        public void GroupLimit_MayBeAddedLater_WithoutBreakingValidation()
        {
            // Not part of the current profile. These values are test-only and only show the architecture accepts it.
            List<ResourceConfigEntry> entries = ValidEntries();
            entries.Add(new ResourceConfigEntry(ResourceType.GroupLimit, 1, true, 1, false, 0, 0));
            Assert.IsTrue(Runtime(entries).IsValid());
        }

        [Test]
        public void FromConfig_RejectsSubMillisecondInterval()
        {
            var config = ResourceConfig.Replenishing(ResourceType.Fuel, 10, 10, 1, TimeSpan.FromTicks(5 * TimeSpan.TicksPerSecond + 1));
            Assert.Throws<ArgumentException>(() => ResourceConfigEntry.FromConfig(config));
            Assert.Throws<ArgumentNullException>(() => ResourceConfigEntry.FromConfig(null));
            Assert.Throws<ArgumentNullException>(() => HomeBaseResourceConfig.CreateRuntime(null));
        }
    }
}
