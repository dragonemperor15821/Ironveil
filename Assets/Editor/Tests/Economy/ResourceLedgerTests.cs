using System;
using System.Collections.Generic;
using Ironveil.Economy;
using NUnit.Framework;

namespace Ironveil.Economy.Tests
{
    /// <summary>Edit-mode tests for <see cref="ResourceLedger"/>. Time is simulated; nothing waits on a real clock.</summary>
    public class ResourceLedgerTests
    {
        private static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);

        private static ResourceLedger NewLedger() => HomeBaseResourceProfile.CreateSinglePlayerLedger();

        private static void AdvanceSeconds(ResourceLedger ledger, double seconds) =>
            ledger.Advance(TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)));

        // ------------------------------------------------------------ 1. initial balances

        [Test]
        public void InitialBalances_MatchHomeBaseProfile()
        {
            var ledger = NewLedger();
            Assert.AreEqual(1_500_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(900_000, ledger.GetBalance(ResourceType.Iron));
            Assert.AreEqual(400_000, ledger.GetBalance(ResourceType.Aluminium));
            Assert.AreEqual(100_000, ledger.GetBalance(ResourceType.Fuel));
            Assert.AreEqual(10_000, ledger.GetBalance(ResourceType.Gold));
            Assert.AreEqual(1_000, ledger.GetBalance(ResourceType.Economy));
            Assert.AreEqual(1_000, ledger.GetBalance(ResourceType.Military));

            Assert.IsFalse(ledger.IsConfigured(ResourceType.GroupLimit), "GroupLimit values are not finalized.");
            Assert.IsFalse(ledger.TryGetBalance(ResourceType.GroupLimit, out _));
            Assert.Throws<InvalidOperationException>(() => ledger.GetBalance(ResourceType.GroupLimit));
            Assert.AreEqual(TimeSpan.Zero, ledger.CurrentTime);
        }

        // ------------------------------------------------------------ 2. capacities

        [Test]
        public void Capacities_MatchProfile_AndUnfinalizedCapacitiesAreUnknown()
        {
            var ledger = NewLedger();
            Assert.AreEqual(1_500_000, ledger.GetCapacity(ResourceType.Concrete));
            Assert.AreEqual(900_000, ledger.GetCapacity(ResourceType.Iron));
            Assert.AreEqual(400_000, ledger.GetCapacity(ResourceType.Aluminium));
            Assert.AreEqual(100_000, ledger.GetCapacity(ResourceType.Fuel));
            Assert.AreEqual(10_000, ledger.GetCapacity(ResourceType.Gold));

            // Unknown capacity is reported as unknown, not as zero or unlimited.
            Assert.IsNull(ledger.GetCapacity(ResourceType.Economy));
            Assert.IsNull(ledger.GetCapacity(ResourceType.Military));
            Assert.IsFalse(ledger.HasCapacity(ResourceType.Economy));
            Assert.IsFalse(ledger.TryGetCapacity(ResourceType.Military, out _));
            Assert.IsTrue(ledger.TryGetCapacity(ResourceType.Gold, out long gold));
            Assert.AreEqual(10_000, gold);

            Assert.Throws<InvalidOperationException>(() => ledger.GetCapacity(ResourceType.GroupLimit));
        }

        [Test]
        public void UnknownCapacity_RejectsAdding_ButAllowsSpending()
        {
            var ledger = NewLedger();
            Assert.IsFalse(ledger.TryAdd(ResourceType.Economy, 1, out var addResult));
            Assert.AreEqual(ResourceTransactionResult.CapacityNotConfigured, addResult);
            Assert.AreEqual(1_000, ledger.GetBalance(ResourceType.Economy));

            Assert.IsTrue(ledger.TrySpend(ResourceType.Economy, 400));
            Assert.AreEqual(600, ledger.GetBalance(ResourceType.Economy));
        }

        // ------------------------------------------------------------ 3. successful spend

        [Test]
        public void TrySpend_Success_DeductsAndStartsOneCycle()
        {
            var ledger = NewLedger();
            Assert.IsTrue(ledger.CanSpend(ResourceType.Concrete, 100_000));
            Assert.IsTrue(ledger.TrySpend(ResourceType.Concrete, 100_000, out var result));
            Assert.AreEqual(ResourceTransactionResult.Success, result);
            Assert.AreEqual(1_400_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Concrete));

            // The whole balance can be spent; balances never go negative.
            Assert.IsTrue(ledger.TrySpend(ResourceType.Gold, 10_000));
            Assert.AreEqual(0, ledger.GetBalance(ResourceType.Gold));
            Assert.IsFalse(ledger.TrySpend(ResourceType.Gold, 1));
            Assert.AreEqual(0, ledger.GetBalance(ResourceType.Gold));
        }

        // ------------------------------------------------------------ 4. insufficient

        [Test]
        public void TrySpend_Insufficient_IsRejectedWithoutSideEffects()
        {
            var ledger = NewLedger();
            int events = 0;
            ledger.Changed += _ => events++;

            Assert.IsFalse(ledger.CanSpend(ResourceType.Fuel, 100_001));
            Assert.IsFalse(ledger.TrySpend(ResourceType.Fuel, 100_001, out var result));
            Assert.AreEqual(ResourceTransactionResult.InsufficientBalance, result);
            Assert.AreEqual(100_000, ledger.GetBalance(ResourceType.Fuel));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Fuel));
            Assert.AreEqual(0, events);
        }

        // ------------------------------------------------------------ 5. zero / negative

        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(-30_000L)]
        [TestCase(long.MinValue)]
        public void TrySpend_ZeroOrNegative_IsRejected(long amount)
        {
            var ledger = NewLedger();
            Assert.IsFalse(ledger.CanSpend(ResourceType.Concrete, amount));
            Assert.IsFalse(ledger.TrySpend(ResourceType.Concrete, amount, out var result));
            Assert.AreEqual(ResourceTransactionResult.InvalidAmount, result);
            Assert.AreEqual(1_500_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Concrete));

            Assert.IsFalse(ledger.TryAdd(ResourceType.Gold, amount, out var addResult));
            Assert.AreEqual(ResourceTransactionResult.InvalidAmount, addResult);
        }

        // ------------------------------------------------------------ 6. independent resources

        [Test]
        public void ConcreteAndIronCycles_AreIndependent()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Concrete, 100_000); // t=0, ticks at 5, 10, ...
            AdvanceSeconds(ledger, 3);
            ledger.TrySpend(ResourceType.Iron, 100_000);     // t=3, ticks at 8, 13, ...

            AdvanceSeconds(ledger, 2); // t=5
            Assert.AreEqual(1_430_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(800_000, ledger.GetBalance(ResourceType.Iron));

            AdvanceSeconds(ledger, 3); // t=8
            Assert.AreEqual(1_430_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(820_000, ledger.GetBalance(ResourceType.Iron));

            AdvanceSeconds(ledger, 2); // t=10
            Assert.AreEqual(1_460_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(820_000, ledger.GetBalance(ResourceType.Iron));

            Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Concrete));
            Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Iron));
            Assert.AreEqual(400_000, ledger.GetBalance(ResourceType.Aluminium), "Unspent resources are unaffected.");
        }

        // ------------------------------------------------------------ 7. overlapping cycles

        [Test]
        public void OverlappingCycles_ForSameResource_TickIndependently()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Concrete, 100_000); // t=0
            Assert.AreEqual(1_400_000, ledger.GetBalance(ResourceType.Concrete));

            AdvanceSeconds(ledger, 2);
            ledger.TrySpend(ResourceType.Concrete, 100_000); // t=2
            Assert.AreEqual(1_300_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(2, ledger.GetActiveCycleCount(ResourceType.Concrete));

            AdvanceSeconds(ledger, 3); // t=5: first cycle
            Assert.AreEqual(1_330_000, ledger.GetBalance(ResourceType.Concrete));
            AdvanceSeconds(ledger, 2); // t=7: second cycle
            Assert.AreEqual(1_360_000, ledger.GetBalance(ResourceType.Concrete));
            AdvanceSeconds(ledger, 3); // t=10: first cycle
            Assert.AreEqual(1_390_000, ledger.GetBalance(ResourceType.Concrete));
            AdvanceSeconds(ledger, 2); // t=12: second cycle
            Assert.AreEqual(1_420_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(2, ledger.GetActiveCycleCount(ResourceType.Concrete));
        }

        [Test]
        public void SpendingAgain_DoesNotResetExistingCycle()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Iron, 200_000); // t=0
            AdvanceSeconds(ledger, 4.9);
            ledger.TrySpend(ResourceType.Iron, 1);       // t=4.9: must not delay the first cycle's t=5 tick
            AdvanceSeconds(ledger, 0.1);                 // t=5
            Assert.AreEqual(900_000 - 200_000 - 1 + 20_000, ledger.GetBalance(ResourceType.Iron));
        }

        [Test]
        public void SimultaneousTicks_AreProcessedDeterministically_AndClampTogether()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Fuel, 3_000);
            ledger.TrySpend(ResourceType.Fuel, 3_000); // two cycles, both tick at t=5
            Assert.AreEqual(94_000, ledger.GetBalance(ResourceType.Fuel));

            AdvanceSeconds(ledger, 5);
            // First tick: 94,000 -> 99,000. Second tick is clamped: 99,000 -> 100,000. All cycles removed.
            Assert.AreEqual(100_000, ledger.GetBalance(ResourceType.Fuel));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Fuel));
        }

        // ------------------------------------------------------------ 8. every five seconds

        [Test]
        public void Replenishment_RepeatsEveryFiveSeconds_StartingFiveSecondsAfterSpend()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Aluminium, 100_000); // 300,000

            AdvanceSeconds(ledger, 4.999);
            Assert.AreEqual(300_000, ledger.GetBalance(ResourceType.Aluminium), "No tick before 5 seconds.");
            AdvanceSeconds(ledger, 0.001);
            Assert.AreEqual(310_000, ledger.GetBalance(ResourceType.Aluminium), "First tick at exactly 5 seconds.");

            for (int n = 2; n <= 6; n++)
            {
                AdvanceSeconds(ledger, 4.999);
                Assert.AreEqual(300_000 + (n - 1) * 10_000, ledger.GetBalance(ResourceType.Aluminium));
                AdvanceSeconds(ledger, 0.001);
                Assert.AreEqual(300_000 + n * 10_000, ledger.GetBalance(ResourceType.Aluminium));
            }
        }

        [Test]
        public void FrameDeltas_AccumulateToTicks()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Concrete, 90_000);
            for (int frame = 0; frame < 299; frame++) ledger.AdvanceSeconds(1.0 / 60.0);
            Assert.AreEqual(1_410_000, ledger.GetBalance(ResourceType.Concrete));
            ledger.AdvanceSeconds(1.0 / 60.0); // 300 frames ~= 5 s
            Assert.AreEqual(1_440_000, ledger.GetBalance(ResourceType.Concrete));
        }

        // ------------------------------------------------------------ 9. removal at capacity

        [Test]
        public void Cycles_AreRemoved_WhenCapacityIsReached()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Fuel, 10_000); // needs exactly 2 ticks
            AdvanceSeconds(ledger, 1);
            ledger.TrySpend(ResourceType.Fuel, 5_000);  // second cycle; total deficit 15,000 = 3 ticks
            Assert.AreEqual(2, ledger.GetActiveCycleCount(ResourceType.Fuel));

            AdvanceSeconds(ledger, 4); // t=5: cycle A -> 90,000
            AdvanceSeconds(ledger, 1); // t=6: cycle B -> 95,000
            Assert.AreEqual(95_000, ledger.GetBalance(ResourceType.Fuel));
            Assert.AreEqual(2, ledger.GetActiveCycleCount(ResourceType.Fuel));

            AdvanceSeconds(ledger, 4); // t=10: cycle A -> 100,000 (full)
            Assert.AreEqual(100_000, ledger.GetBalance(ResourceType.Fuel));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Fuel), "Cycle B is removed too.");

            AdvanceSeconds(ledger, 60);
            Assert.AreEqual(100_000, ledger.GetBalance(ResourceType.Fuel));
        }

        [Test]
        public void TryAdd_ToCapacity_RemovesCycles()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Iron, 50_000);
            Assert.IsTrue(ledger.TryAdd(ResourceType.Iron, 50_000));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Iron));
            Assert.IsFalse(ledger.TryAdd(ResourceType.Iron, 1, out var result));
            Assert.AreEqual(ResourceTransactionResult.ExceedsCapacity, result);
            Assert.AreEqual(900_000, ledger.GetBalance(ResourceType.Iron));
        }

        // ------------------------------------------------------------ 10. fresh cycle after restore

        [Test]
        public void SpendingAfterCapacityRestored_CreatesFreshCycle()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Concrete, 30_000);
            AdvanceSeconds(ledger, 5);
            Assert.AreEqual(1_500_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Concrete));

            AdvanceSeconds(ledger, 2.5); // t=7.5, off the old cycle's 5-second grid
            ledger.TrySpend(ResourceType.Concrete, 60_000);
            Assert.AreEqual(1, ledger.GetActiveCycleCount(ResourceType.Concrete));

            AdvanceSeconds(ledger, 2.5); // t=10: old cycle would have ticked here, but it is gone
            Assert.AreEqual(1_440_000, ledger.GetBalance(ResourceType.Concrete));
            AdvanceSeconds(ledger, 2.5); // t=12.5: fresh cycle's first tick
            Assert.AreEqual(1_470_000, ledger.GetBalance(ResourceType.Concrete));
        }

        // ------------------------------------------------------------ 11. clamping

        [Test]
        public void Replenishment_IsClampedToCapacity()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Concrete, 10_000); // tick is 30,000
            AdvanceSeconds(ledger, 5);
            Assert.AreEqual(1_500_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Concrete));
        }

        // ------------------------------------------------------------ 12. long catch-up

        [Test]
        public void LongElapsedTime_CatchesUpAndStopsAtCapacity()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Concrete, 1_000_000);
            ledger.TrySpend(ResourceType.Iron, 900_000);
            ledger.Advance(TimeSpan.FromHours(10));
            Assert.AreEqual(1_500_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(900_000, ledger.GetBalance(ResourceType.Iron));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Concrete));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Iron));
        }

        [Test]
        public void PartialCatchUp_AppliesEveryDueTick_AndKeepsCyclePhase()
        {
            var ledger = NewLedger();
            ledger.TrySpend(ResourceType.Concrete, 600_000); // 20 ticks to fill
            AdvanceSeconds(ledger, 1);
            ledger.TrySpend(ResourceType.Concrete, 1);       // second cycle at t=1, phase 6, 11, ...

            AdvanceSeconds(ledger, 26); // t=27: cycle A ticks at 5..25 (5), cycle B at 6..26 (5)
            Assert.AreEqual(1_500_000 - 600_001 + 10 * 30_000, ledger.GetBalance(ResourceType.Concrete));

            AdvanceSeconds(ledger, 3); // t=30: only cycle A ticks
            Assert.AreEqual(1_500_000 - 600_001 + 11 * 30_000, ledger.GetBalance(ResourceType.Concrete));
            AdvanceSeconds(ledger, 1); // t=31: only cycle B ticks
            Assert.AreEqual(1_500_000 - 600_001 + 12 * 30_000, ledger.GetBalance(ResourceType.Concrete));
        }

        [Test]
        public void CatchUp_MatchesTickByTickReferenceModel_ForRandomSchedules()
        {
            var rng = new Random(12345);
            for (int run = 0; run < 200; run++)
            {
                var ledger = NewLedger();
                var model = new ReferenceModel(1_500_000, 1_500_000, 30_000, FiveSeconds.Ticks);

                for (int step = 0; step < 40; step++)
                {
                    if (rng.Next(3) == 0)
                    {
                        long amount = rng.Next(1, 400_000);
                        bool expected = model.TrySpend(amount);
                        Assert.AreEqual(expected, ledger.TrySpend(ResourceType.Concrete, amount), $"run {run} step {step}");
                    }
                    // Mix of sub-tick, on-grid and long catch-up advances.
                    long elapsed = rng.Next(4) switch
                    {
                        0 => rng.Next(0, 10_000_000),
                        1 => FiveSeconds.Ticks * rng.Next(1, 4),
                        2 => rng.Next(0, 120) * TimeSpan.TicksPerSecond,
                        _ => (long)rng.Next(0, 2_000) * TimeSpan.TicksPerSecond,
                    };
                    model.Advance(elapsed);
                    ledger.Advance(TimeSpan.FromTicks(elapsed));

                    Assert.AreEqual(model.Balance, ledger.GetBalance(ResourceType.Concrete), $"run {run} step {step}");
                    Assert.AreEqual(model.CycleCount, ledger.GetActiveCycleCount(ResourceType.Concrete), $"run {run} step {step}");
                }
            }
        }

        // ------------------------------------------------------------ 13-16. non-replenishing resources

        [Test]
        public void Gold_DoesNotReplenish()
        {
            AssertDoesNotReplenish(NewLedger(), ResourceType.Gold, 2_500);
        }

        [Test]
        public void Economy_DoesNotReplenish()
        {
            AssertDoesNotReplenish(NewLedger(), ResourceType.Economy, 500);
        }

        [Test]
        public void Military_DoesNotReplenish()
        {
            AssertDoesNotReplenish(NewLedger(), ResourceType.Military, 500);
        }

        [Test]
        public void GroupLimit_DoesNotReplenish()
        {
            // GroupLimit is unconfigured in the real profile. These values exist only to exercise the rule.
            var ledger = new ResourceLedger(new[] { ResourceConfig.Fixed(ResourceType.GroupLimit, 10, 10) });
            AssertDoesNotReplenish(ledger, ResourceType.GroupLimit, 3);

            Assert.Throws<ArgumentException>(() =>
                ResourceConfig.Replenishing(ResourceType.GroupLimit, 10, 10, 1, FiveSeconds));
        }

        [TestCase(ResourceType.Gold)]
        [TestCase(ResourceType.Economy)]
        [TestCase(ResourceType.Military)]
        [TestCase(ResourceType.GroupLimit)]
        public void NonPrimaryResources_CannotBeConfiguredToReplenish(ResourceType resource)
        {
            Assert.IsFalse(ResourceTypeRules.SupportsReplenishment(resource));
            Assert.Throws<ArgumentException>(() => ResourceConfig.Replenishing(resource, 100, 100, 10, FiveSeconds));
        }

        private static void AssertDoesNotReplenish(ResourceLedger ledger, ResourceType resource, long spend)
        {
            long start = ledger.GetBalance(resource);
            Assert.IsFalse(ledger.IsReplenishing(resource));
            Assert.IsTrue(ledger.TrySpend(resource, spend));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(resource));

            AdvanceSeconds(ledger, 5);
            AdvanceSeconds(ledger, 60);
            ledger.Advance(TimeSpan.FromDays(3));
            Assert.AreEqual(start - spend, ledger.GetBalance(resource));
        }

        // ------------------------------------------------------------ 17. unconfigured

        [Test]
        public void UnconfiguredResource_RejectsSpendingAndAdding()
        {
            var ledger = NewLedger();
            Assert.IsFalse(ledger.CanSpend(ResourceType.GroupLimit, 1));
            Assert.IsFalse(ledger.TrySpend(ResourceType.GroupLimit, 1, out var spend));
            Assert.AreEqual(ResourceTransactionResult.NotConfigured, spend);
            Assert.IsFalse(ledger.TryAdd(ResourceType.GroupLimit, 1, out var add));
            Assert.AreEqual(ResourceTransactionResult.NotConfigured, add);
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.GroupLimit));

            var concreteOnly = new ResourceLedger(new[] { ResourceConfig.Fixed(ResourceType.Concrete, 5, 5) });
            Assert.IsFalse(concreteOnly.TrySpend(ResourceType.Iron, 1));
            Assert.IsFalse(concreteOnly.IsConfigured((ResourceType)99));
            Assert.IsFalse(concreteOnly.TrySpend((ResourceType)99, 1));
        }

        // ------------------------------------------------------------ 18. Changed events

        [Test]
        public void ChangedEvents_ReportCorrectResourceReasonAndBalances()
        {
            var ledger = NewLedger();
            var events = new List<ResourceChange>();
            ledger.Changed += events.Add;

            ledger.TrySpend(ResourceType.Iron, 40_000);
            ledger.TrySpend(ResourceType.Concrete, 40_000);
            ledger.TrySpend(ResourceType.Fuel, 999_999); // rejected, no event
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(new ResourceChange(ResourceType.Iron, 900_000, 860_000, ResourceChangeReason.Spent), events[0]);
            Assert.AreEqual(new ResourceChange(ResourceType.Concrete, 1_500_000, 1_460_000, ResourceChangeReason.Spent), events[1]);
            Assert.AreEqual(-40_000, events[0].Delta);

            events.Clear();
            AdvanceSeconds(ledger, 4);
            Assert.AreEqual(0, events.Count, "No event when nothing ticks.");

            AdvanceSeconds(ledger, 1); // both tick; one event per resource, in enum order
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(new ResourceChange(ResourceType.Concrete, 1_460_000, 1_490_000, ResourceChangeReason.Replenished), events[0]);
            Assert.AreEqual(new ResourceChange(ResourceType.Iron, 860_000, 880_000, ResourceChangeReason.Replenished), events[1]);

            events.Clear();
            ledger.TryAdd(ResourceType.Gold, 1); // at capacity: rejected, no event
            ledger.TrySpend(ResourceType.Gold, 10);
            ledger.TryAdd(ResourceType.Gold, 10);
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual(new ResourceChange(ResourceType.Gold, 9_990, 10_000, ResourceChangeReason.Added), events[1]);
        }

        [Test]
        public void ThrowingListener_DoesNotBlockOtherListeners()
        {
            var ledger = NewLedger();
            var captured = new List<Exception>();
            ledger.ListenerExceptionHandler = captured.Add;

            int before = 0, after = 0;
            ledger.Changed += _ => before++;
            ledger.Changed += _ => throw new InvalidOperationException("bad subscriber");
            ledger.Changed += _ => after++;

            Assert.IsTrue(ledger.TrySpend(ResourceType.Concrete, 1));
            AdvanceSeconds(ledger, 5);

            Assert.AreEqual(2, before);
            Assert.AreEqual(2, after);
            Assert.AreEqual(2, captured.Count);
            Assert.AreEqual(1_500_000, ledger.GetBalance(ResourceType.Concrete), "State is committed regardless of listeners.");

            ledger.ListenerExceptionHandler = _ => throw new Exception("bad handler");
            Assert.DoesNotThrow(() => ledger.TrySpend(ResourceType.Concrete, 1));
            Assert.AreEqual(3, after);
        }

        [Test]
        public void ListenerMaySpend_ButMayNotAdvanceReentrantly()
        {
            var ledger = NewLedger();
            var captured = new List<Exception>();
            ledger.ListenerExceptionHandler = captured.Add;
            ledger.TrySpend(ResourceType.Concrete, 30_000);

            ledger.Changed += c =>
            {
                if (c.Reason != ResourceChangeReason.Replenished) return;
                ledger.TrySpend(ResourceType.Iron, 1);
                ledger.Advance(TimeSpan.FromSeconds(1));
            };
            AdvanceSeconds(ledger, 5);

            Assert.AreEqual(899_999, ledger.GetBalance(ResourceType.Iron));
            Assert.AreEqual(1, captured.Count);
            Assert.IsInstanceOf<InvalidOperationException>(captured[0]);
            Assert.AreEqual(FiveSeconds, ledger.CurrentTime);
        }

        // ------------------------------------------------------------ 19. invalid configuration

        [Test]
        public void InvalidConfiguration_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Fixed(ResourceType.Gold, -1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Fixed(ResourceType.Gold, 11, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Fixed(ResourceType.Gold, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Fixed(ResourceType.Economy, -5, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Fixed((ResourceType)8, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Fixed((ResourceType)(-1), 0, 0));

            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Replenishing(ResourceType.Iron, 10, 10, 0, FiveSeconds));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Replenishing(ResourceType.Iron, 10, 10, -1, FiveSeconds));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Replenishing(ResourceType.Iron, 10, 10, 1, TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Replenishing(ResourceType.Iron, 10, 10, 1, TimeSpan.FromSeconds(-5)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Replenishing(ResourceType.Iron, 10, 10, 1, TimeSpan.FromDays(366)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceConfig.Replenishing(ResourceType.Iron, 20, 10, 1, FiveSeconds));

            Assert.Throws<ArgumentNullException>(() => new ResourceLedger(null));
            Assert.Throws<ArgumentException>(() => new ResourceLedger(new ResourceConfig[] { null }));
            Assert.Throws<ArgumentException>(() => new ResourceLedger(new[]
            {
                ResourceConfig.Fixed(ResourceType.Gold, 1, 1),
                ResourceConfig.Fixed(ResourceType.Gold, 2, 2),
            }));
        }

        [Test]
        public void InvalidTimeAdvance_IsRejected()
        {
            var ledger = NewLedger();
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Advance(TimeSpan.FromTicks(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.AdvanceSeconds(-0.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.AdvanceSeconds(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.AdvanceSeconds(double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Advance(TimeSpan.MaxValue));
            Assert.AreEqual(TimeSpan.Zero, ledger.CurrentTime);
        }

        // ------------------------------------------------------------ 20. overflow

        [Test]
        public void LargeAmounts_DoNotOverflow()
        {
            const long max = long.MaxValue;
            var ledger = new ResourceLedger(new[]
            {
                ResourceConfig.Replenishing(ResourceType.Concrete, max, max, max, TimeSpan.FromSeconds(5)),
                ResourceConfig.Replenishing(ResourceType.Iron, max, max, 1, TimeSpan.FromTicks(1)),
                ResourceConfig.Fixed(ResourceType.Gold, max - 1, max),
            });

            // Huge tick amount clamps instead of wrapping.
            Assert.IsTrue(ledger.TrySpend(ResourceType.Concrete, max));
            Assert.AreEqual(0, ledger.GetBalance(ResourceType.Concrete));
            Assert.IsFalse(ledger.TrySpend(ResourceType.Concrete, 1));

            // Many cycles with a 1-tick interval over a very long time: due-tick count saturates instead of wrapping.
            Assert.IsTrue(ledger.TrySpend(ResourceType.Iron, max / 2));
            for (int k = 0; k < 50; k++) Assert.IsTrue(ledger.TrySpend(ResourceType.Iron, 1));

            // Adding beyond capacity is rejected without overflowing.
            Assert.IsFalse(ledger.TryAdd(ResourceType.Gold, max, out var result));
            Assert.AreEqual(ResourceTransactionResult.ExceedsCapacity, result);
            Assert.IsTrue(ledger.TryAdd(ResourceType.Gold, 1));
            Assert.AreEqual(max, ledger.GetBalance(ResourceType.Gold));

            ledger.Advance(TimeSpan.FromDays(365 * 1000));
            Assert.AreEqual(max, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(max, ledger.GetBalance(ResourceType.Iron));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Iron));

            // Clock limit is enforced rather than overflowing.
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Advance(ResourceLedger.MaxTime));
        }

        [Test]
        public void PartialTicks_NearLongMax_DoNotOverflow()
        {
            const long max = long.MaxValue;
            long tick = max / 4;
            var ledger = new ResourceLedger(new[]
            {
                ResourceConfig.Replenishing(ResourceType.Fuel, max, max, tick, TimeSpan.FromSeconds(5)),
            });
            ledger.TrySpend(ResourceType.Fuel, max); // 0, needs 5 ticks (4 full + clamp)
            AdvanceSeconds(ledger, 15);
            Assert.AreEqual(3 * tick, ledger.GetBalance(ResourceType.Fuel));
            AdvanceSeconds(ledger, 5);
            Assert.AreEqual(4 * tick, ledger.GetBalance(ResourceType.Fuel));
            AdvanceSeconds(ledger, 5);
            Assert.AreEqual(max, ledger.GetBalance(ResourceType.Fuel));
            Assert.AreEqual(0, ledger.GetActiveCycleCount(ResourceType.Fuel));
        }

        // ------------------------------------------------------------ reference model

        /// <summary>Naive simulator that applies ticks one at a time in (time, creation) order. Used only to cross-check the ledger.</summary>
        private sealed class ReferenceModel
        {
            private readonly long _capacity, _amount, _interval;
            private readonly List<long> _next = new List<long>();
            private long _now;

            public long Balance { get; private set; }
            public int CycleCount => _next.Count;

            public ReferenceModel(long start, long capacity, long amount, long interval)
            {
                Balance = start; _capacity = capacity; _amount = amount; _interval = interval;
            }

            public bool TrySpend(long amount)
            {
                if (amount <= 0 || amount > Balance) return false;
                Balance -= amount;
                _next.Add(_now + _interval);
                return true;
            }

            public void Advance(long elapsed)
            {
                long target = _now + elapsed;
                while (_next.Count > 0)
                {
                    int best = 0;
                    for (int c = 1; c < _next.Count; c++)
                        if (_next[c] < _next[best]) best = c; // ties keep the earliest-created cycle
                    if (_next[best] > target) break;

                    Balance = Math.Min(_capacity, Balance + _amount);
                    if (Balance == _capacity) { _next.Clear(); break; }
                    _next[best] += _interval;
                }
                _now = target;
            }
        }
    }
}
