using System;
using System.Collections.Generic;

namespace Ironveil.Economy
{
    /// <summary>
    /// Immutable configuration for one resource. Construct through <see cref="Fixed"/> or
    /// <see cref="Replenishing"/>; both validate their arguments and throw on invalid values.
    /// </summary>
    public sealed class ResourceConfig
    {
        /// <summary>Longest replenishment interval accepted. Keeps clock arithmetic far from overflow.</summary>
        public static readonly TimeSpan MaxReplenishInterval = TimeSpan.FromDays(365);

        public ResourceType Resource { get; }
        public long StartingBalance { get; }

        /// <summary>Configured capacity, or null when the capacity has not been decided yet.</summary>
        public long? Capacity { get; }

        /// <summary>Amount added per replenishment tick; 0 when the resource does not replenish.</summary>
        public long ReplenishAmount { get; }

        /// <summary>Delay before a cycle's first tick and between its later ticks; zero when not replenishing.</summary>
        public TimeSpan ReplenishInterval { get; }

        public bool IsReplenishing => ReplenishAmount > 0;

        private ResourceConfig(ResourceType resource, long startingBalance, long? capacity,
            long replenishAmount, TimeSpan replenishInterval)
        {
            Resource = resource;
            StartingBalance = startingBalance;
            Capacity = capacity;
            ReplenishAmount = replenishAmount;
            ReplenishInterval = replenishInterval;
        }

        /// <summary>A resource with no automatic replenishment. Pass null capacity when it is not yet decided.</summary>
        public static ResourceConfig Fixed(ResourceType resource, long startingBalance, long? capacity)
        {
            ValidateCommon(resource, startingBalance, capacity);
            return new ResourceConfig(resource, startingBalance, capacity, 0, TimeSpan.Zero);
        }

        /// <summary>A primary resource that refills through independent per-spend cycles.</summary>
        public static ResourceConfig Replenishing(ResourceType resource, long startingBalance, long capacity,
            long replenishAmount, TimeSpan replenishInterval)
        {
            ValidateCommon(resource, startingBalance, capacity);
            if (!ResourceTypeRules.SupportsReplenishment(resource))
                throw new ArgumentException($"{resource} must not receive automatic replenishment.", nameof(resource));
            if (replenishAmount <= 0)
                throw new ArgumentOutOfRangeException(nameof(replenishAmount), replenishAmount, "Replenish amount must be positive.");
            if (replenishInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(replenishInterval), replenishInterval, "Replenish interval must be positive.");
            if (replenishInterval > MaxReplenishInterval)
                throw new ArgumentOutOfRangeException(nameof(replenishInterval), replenishInterval, $"Replenish interval must not exceed {MaxReplenishInterval}.");
            return new ResourceConfig(resource, startingBalance, capacity, replenishAmount, replenishInterval);
        }

        private static void ValidateCommon(ResourceType resource, long startingBalance, long? capacity)
        {
            if (!ResourceTypeRules.IsDefined(resource))
                throw new ArgumentOutOfRangeException(nameof(resource), resource, "Undefined resource type.");
            if (startingBalance < 0)
                throw new ArgumentOutOfRangeException(nameof(startingBalance), startingBalance, "Starting balance must not be negative.");
            if (capacity.HasValue)
            {
                if (capacity.Value < 0)
                    throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must not be negative.");
                if (startingBalance > capacity.Value)
                    throw new ArgumentOutOfRangeException(nameof(startingBalance), startingBalance, "Starting balance must not exceed capacity.");
            }
        }
    }

    /// <summary>Why a resource balance changed.</summary>
    public enum ResourceChangeReason
    {
        Spent,
        Added,
        Replenished,
    }

    /// <summary>Payload of <see cref="ResourceLedger.Changed"/>.</summary>
    public readonly struct ResourceChange
    {
        public ResourceType Resource { get; }
        public long PreviousBalance { get; }
        public long NewBalance { get; }
        public ResourceChangeReason Reason { get; }
        public long Delta => NewBalance - PreviousBalance;

        public ResourceChange(ResourceType resource, long previousBalance, long newBalance, ResourceChangeReason reason)
        {
            Resource = resource;
            PreviousBalance = previousBalance;
            NewBalance = newBalance;
            Reason = reason;
        }

        public override string ToString() => $"{Resource}: {PreviousBalance} -> {NewBalance} ({Reason})";
    }

    /// <summary>Outcome of a spend or add request.</summary>
    public enum ResourceTransactionResult
    {
        Success,
        InvalidAmount,
        NotConfigured,
        InsufficientBalance,
        CapacityNotConfigured,
        ExceedsCapacity,
    }

    /// <summary>A storage capacity changed at runtime (ResourceLedger.TrySetCapacity).</summary>
    public readonly struct ResourceCapacityChange
    {
        public ResourceType Resource { get; }
        public long PreviousCapacity { get; }
        public long NewCapacity { get; }

        public ResourceCapacityChange(ResourceType resource, long previousCapacity, long newCapacity)
        {
            Resource = resource;
            PreviousCapacity = previousCapacity;
            NewCapacity = newCapacity;
        }

        public override string ToString() => $"{Resource} capacity: {PreviousCapacity} -> {NewCapacity}";
    }

    /// <summary>
    /// Plain C# resource ledger. It does not use Unity time, coroutines, timers or threads:
    /// its owner calls <see cref="Advance"/> with elapsed game time.
    ///
    /// Replenishment: every successful spend of a replenishing resource starts a new,
    /// independent cycle whose first tick is one interval after the spend and which repeats every
    /// interval. Each tick adds the resource's replenish amount, clamped to capacity. When
    /// capacity is reached, all cycles for that resource are removed.
    ///
    /// Not thread-safe; call it from one thread (Unity's main thread).
    /// </summary>
    public sealed class ResourceLedger
    {
        /// <summary>Latest clock value the ledger accepts (about 14,600 years). Prevents tick-time overflow.</summary>
        public static readonly TimeSpan MaxTime = TimeSpan.FromTicks(long.MaxValue / 2);

        private readonly bool[] _configured = new bool[ResourceTypeRules.Count];
        private readonly long[] _balances = new long[ResourceTypeRules.Count];
        private readonly long?[] _capacities = new long?[ResourceTypeRules.Count];
        private readonly long[] _replenishAmounts = new long[ResourceTypeRules.Count];
        private readonly long[] _intervalTicks = new long[ResourceTypeRules.Count];

        // Per resource: next tick time (TimeSpan ticks) of each active cycle, in creation order.
        private readonly List<long>[] _cycles = new List<long>[ResourceTypeRules.Count];

        private long _nowTicks;
        private bool _advancing;

        /// <summary>Raised after a balance changes. During <see cref="Advance"/> it is raised at most once per resource, in enum order, after all ticks are applied.</summary>
        public event Action<ResourceChange> Changed;

        /// <summary>Raised after <see cref="TrySetCapacity"/> changes a capacity. Balances are never changed by it.</summary>
        public event Action<ResourceCapacityChange> CapacityChanged;

        /// <summary>
        /// Receives exceptions thrown by <see cref="Changed"/> subscribers. Other subscribers are still notified.
        /// Defaults to UnityEngine.Debug.LogException inside Unity; set to null to ignore listener exceptions.
        /// </summary>
        public Action<Exception> ListenerExceptionHandler { get; set; }

        /// <summary>Ledger clock: total time passed to <see cref="Advance"/>.</summary>
        public TimeSpan CurrentTime => TimeSpan.FromTicks(_nowTicks);

        public ResourceLedger(IEnumerable<ResourceConfig> configs)
        {
            if (configs == null) throw new ArgumentNullException(nameof(configs));

            foreach (ResourceConfig config in configs)
            {
                if (config == null)
                    throw new ArgumentException("Resource configuration list contains a null entry.", nameof(configs));
                int i = (int)config.Resource;
                if (_configured[i])
                    throw new ArgumentException($"{config.Resource} is configured more than once.", nameof(configs));

                _configured[i] = true;
                _balances[i] = config.StartingBalance;
                _capacities[i] = config.Capacity;
                _replenishAmounts[i] = config.ReplenishAmount;
                _intervalTicks[i] = config.ReplenishInterval.Ticks;
            }

            for (int i = 0; i < ResourceTypeRules.Count; i++)
                _cycles[i] = new List<long>();

#if UNITY_5_3_OR_NEWER
            ListenerExceptionHandler = UnityEngine.Debug.LogException;
#endif
        }

        // ---------------------------------------------------------------- queries

        public bool IsConfigured(ResourceType resource) =>
            ResourceTypeRules.IsDefined(resource) && _configured[(int)resource];

        /// <summary>Current balance. Throws for an unconfigured resource rather than returning a made-up value.</summary>
        public long GetBalance(ResourceType resource)
        {
            RequireConfigured(resource);
            return _balances[(int)resource];
        }

        public bool TryGetBalance(ResourceType resource, out long balance)
        {
            if (!IsConfigured(resource)) { balance = 0; return false; }
            balance = _balances[(int)resource];
            return true;
        }

        /// <summary>Configured capacity, or null when the capacity is not decided yet. Throws for an unconfigured resource.</summary>
        public long? GetCapacity(ResourceType resource)
        {
            RequireConfigured(resource);
            return _capacities[(int)resource];
        }

        public bool HasCapacity(ResourceType resource) =>
            IsConfigured(resource) && _capacities[(int)resource].HasValue;

        public bool TryGetCapacity(ResourceType resource, out long capacity)
        {
            if (HasCapacity(resource)) { capacity = _capacities[(int)resource].Value; return true; }
            capacity = 0;
            return false;
        }

        public bool IsReplenishing(ResourceType resource) =>
            IsConfigured(resource) && _replenishAmounts[(int)resource] > 0;

        /// <summary>Number of replenishment cycles currently running for the resource.</summary>
        public int GetActiveCycleCount(ResourceType resource) =>
            IsConfigured(resource) ? _cycles[(int)resource].Count : 0;

        /// <summary>
        /// Read-only (Stage 4 HUD countdown): the earliest scheduled tick among the resource's active replenishment
        /// cycles, as a ledger clock time comparable with <see cref="CurrentTime"/>. False when no cycle is active.
        /// </summary>
        public bool TryGetNextReplenishTime(ResourceType resource, out TimeSpan nextTickTime)
        {
            nextTickTime = TimeSpan.Zero;
            if (!IsConfigured(resource)) return false;
            List<long> cycles = _cycles[(int)resource];
            if (cycles.Count == 0) return false;

            long earliest = cycles[0];
            for (int c = 1; c < cycles.Count; c++)
                if (cycles[c] < earliest) earliest = cycles[c];
            nextTickTime = TimeSpan.FromTicks(earliest);
            return true;
        }

        public bool CanSpend(ResourceType resource, long amount) =>
            EvaluateSpend(resource, amount) == ResourceTransactionResult.Success;

        public ResourceTransactionResult EvaluateSpend(ResourceType resource, long amount)
        {
            if (amount <= 0) return ResourceTransactionResult.InvalidAmount;
            if (!IsConfigured(resource)) return ResourceTransactionResult.NotConfigured;
            if (_balances[(int)resource] < amount) return ResourceTransactionResult.InsufficientBalance;
            return ResourceTransactionResult.Success;
        }

        public ResourceTransactionResult EvaluateAdd(ResourceType resource, long amount)
        {
            if (amount <= 0) return ResourceTransactionResult.InvalidAmount;
            if (!IsConfigured(resource)) return ResourceTransactionResult.NotConfigured;
            long? capacity = _capacities[(int)resource];
            if (!capacity.HasValue) return ResourceTransactionResult.CapacityNotConfigured;
            // Written as a subtraction so it cannot overflow. After a storage decrease the balance may exceed the
            // capacity (excess preserved); the difference is then negative and every addition is refused.
            if (amount > capacity.Value - _balances[(int)resource]) return ResourceTransactionResult.ExceedsCapacity;
            return ResourceTransactionResult.Success;
        }

        // ---------------------------------------------------------------- mutations

        public bool TrySpend(ResourceType resource, long amount) => TrySpend(resource, amount, out _);

        /// <summary>
        /// Deducts <paramref name="amount"/> if possible. If the resource replenishes, a successful spend that leaves the
        /// balance below capacity starts a new independent cycle and leaves existing cycles unchanged. (A spend that
        /// leaves a preserved excess still at or above a lowered capacity starts none: replenishment resumes only once
        /// spending brings the balance below the limit.) A rejected spend changes nothing.
        /// </summary>
        public bool TrySpend(ResourceType resource, long amount, out ResourceTransactionResult result)
        {
            result = EvaluateSpend(resource, amount);
            if (result != ResourceTransactionResult.Success) return false;

            int i = (int)resource;
            long previous = _balances[i];
            _balances[i] = previous - amount;

            if (_replenishAmounts[i] > 0 && (!_capacities[i].HasValue || _balances[i] < _capacities[i].Value))
                _cycles[i].Add(_nowTicks + _intervalTicks[i]);

            Raise(new ResourceChange(resource, previous, _balances[i], ResourceChangeReason.Spent));
            return true;
        }

        public bool TryAdd(ResourceType resource, long amount) => TryAdd(resource, amount, out _);

        /// <summary>
        /// Adds exactly <paramref name="amount"/>, or nothing at all. Rejected when the capacity is not configured
        /// (an unknown capacity is neither zero nor unlimited) or when the result would exceed capacity.
        /// Reaching capacity removes the resource's replenishment cycles.
        /// </summary>
        public bool TryAdd(ResourceType resource, long amount, out ResourceTransactionResult result)
        {
            result = EvaluateAdd(resource, amount);
            if (result != ResourceTransactionResult.Success) return false;

            int i = (int)resource;
            long previous = _balances[i];
            _balances[i] = previous + amount;
            if (_balances[i] == _capacities[i].Value)
                _cycles[i].Clear();

            Raise(new ResourceChange(resource, previous, _balances[i], ResourceChangeReason.Added));
            return true;
        }

        /// <summary>
        /// Stage 8: changes a configured capacity at runtime (storage expansion). Balances are NEVER changed (approved
        /// storage behaviour):
        ///  - Decrease: a balance above the new limit is preserved; passive replenishment stops (cycles removed) while
        ///    the balance is at or above the limit, and resumes once a spend brings it below (see TrySpend). Additions
        ///    are refused while at or above the limit.
        ///  - Increase: if the balance is below the new limit and no cycle is running, one replenishment cycle starts
        ///    now (first tick one interval later), so the store refills without the player spending first. Running
        ///    cycles are kept.
        /// A resource whose capacity was not decided (null) cannot be given one here. Setting the current value again is
        /// a successful no-op without event.
        /// </summary>
        public bool TrySetCapacity(ResourceType resource, long capacity, out ResourceTransactionResult result)
        {
            if (!IsConfigured(resource)) { result = ResourceTransactionResult.NotConfigured; return false; }
            if (capacity < 0) { result = ResourceTransactionResult.InvalidAmount; return false; }
            int i = (int)resource;
            if (!_capacities[i].HasValue) { result = ResourceTransactionResult.CapacityNotConfigured; return false; }

            result = ResourceTransactionResult.Success;
            long previous = _capacities[i].Value;
            if (previous == capacity) return true;

            _capacities[i] = capacity;
            if (_balances[i] >= capacity)
                _cycles[i].Clear();
            else if (_replenishAmounts[i] > 0 && _cycles[i].Count == 0)
                _cycles[i].Add(_nowTicks + _intervalTicks[i]);

            Action<ResourceCapacityChange> handlers = CapacityChanged;
            if (handlers != null)
            {
                var change = new ResourceCapacityChange(resource, previous, capacity);
                foreach (Delegate d in handlers.GetInvocationList())
                {
                    try { ((Action<ResourceCapacityChange>)d)(change); }
                    catch (Exception e)
                    {
                        try { ListenerExceptionHandler?.Invoke(e); }
                        catch { /* see Raise */ }
                    }
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- time

        /// <summary>Advances the ledger clock by a frame delta in seconds (for example Time.deltaTime).</summary>
        public void AdvanceSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Elapsed seconds must be finite and non-negative.");
            double ticks = Math.Round(seconds * TimeSpan.TicksPerSecond);
            if (ticks > MaxTime.Ticks)
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Elapsed time is too large.");
            Advance(TimeSpan.FromTicks((long)ticks));
        }

        /// <summary>
        /// Advances the ledger clock and applies every replenishment tick due within the elapsed time, including
        /// long catch-up intervals. Ticks at exactly the new time are included.
        /// </summary>
        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time must not be negative.");
            if (elapsed.Ticks > MaxTime.Ticks - _nowTicks)
                throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, $"Ledger clock must not exceed {MaxTime}.");
            if (_advancing)
                throw new InvalidOperationException("Advance must not be called from inside a Changed handler raised by Advance.");
            if (elapsed == TimeSpan.Zero) return;

            _nowTicks += elapsed.Ticks;

            _advancing = true;
            try
            {
                // Apply every resource and record its change before notifying anyone, so listeners see a fully
                // updated ledger. A listener that spends during notification cannot then cause a false
                // "Replenished" event for a resource later in the loop.
                Span<ResourceChange> changes = stackalloc ResourceChange[ResourceTypeRules.Count];
                int changeCount = 0;
                for (int i = 0; i < ResourceTypeRules.Count; i++)
                {
                    long previous = _balances[i];
                    ApplyDueTicks(i, _nowTicks);
                    if (_balances[i] != previous)
                        changes[changeCount++] = new ResourceChange((ResourceType)i, previous, _balances[i], ResourceChangeReason.Replenished);
                }

                for (int c = 0; c < changeCount; c++)
                    Raise(changes[c]);
            }
            finally
            {
                _advancing = false;
            }
        }

        /// <summary>
        /// Applies all ticks due at or before <paramref name="now"/> for one resource.
        /// Every cycle of a resource adds the same amount, so the resulting balance depends only on how many ticks are
        /// due, not on their order. That allows an O(cycles) closed-form catch-up instead of simulating each tick.
        /// The result matches applying ticks one by one in (time, creation) order, clamping the final tick to capacity
        /// and removing all cycles once capacity is reached.
        /// </summary>
        private void ApplyDueTicks(int i, long now)
        {
            List<long> cycles = _cycles[i];
            if (cycles.Count == 0) return;

            long capacity = _capacities[i].Value; // replenishing resources always have a capacity
            long deficit = capacity - _balances[i];
            if (deficit <= 0) { cycles.Clear(); return; }

            long interval = _intervalTicks[i];
            long amount = _replenishAmounts[i];

            long dueTicks = 0;
            for (int c = 0; c < cycles.Count; c++)
            {
                long next = cycles[c];
                if (next > now) continue;
                long count = (now - next) / interval + 1;
                dueTicks = count > long.MaxValue - dueTicks ? long.MaxValue : dueTicks + count;
            }
            if (dueTicks == 0) return;

            // Ticks needed to reach capacity, i.e. ceil(deficit / amount), computed without overflow.
            long ticksToFill = deficit / amount + (deficit % amount != 0 ? 1 : 0);
            if (dueTicks >= ticksToFill)
            {
                _balances[i] = capacity;
                cycles.Clear();
                return;
            }

            // dueTicks < ticksToFill, so dueTicks * amount < deficit and cannot overflow.
            _balances[i] += dueTicks * amount;
            for (int c = 0; c < cycles.Count; c++)
            {
                long next = cycles[c];
                if (next > now) continue;
                long count = (now - next) / interval + 1;
                cycles[c] = next + count * interval; // <= now + interval, bounded by MaxTime + MaxReplenishInterval
            }
        }

        // ---------------------------------------------------------------- helpers

        private void RequireConfigured(ResourceType resource)
        {
            if (!IsConfigured(resource))
                throw new InvalidOperationException($"{resource} is not configured in this ledger.");
        }

        private void Raise(ResourceChange change)
        {
            Action<ResourceChange> handlers = Changed;
            if (handlers == null) return;

            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<ResourceChange>)d)(change);
                }
                catch (Exception e)
                {
                    try { ListenerExceptionHandler?.Invoke(e); }
                    catch { /* a failing error handler must not break notification of other listeners */ }
                }
            }
        }
    }

    /// <summary>
    /// Resource configuration for the current single-player Home Base.
    /// Only finalized values are included. Economy and Military have no capacity yet, and GroupLimit is left
    /// unconfigured because neither its starting value nor its capacity is decided.
    /// </summary>
    public static class HomeBaseResourceProfile
    {
        public static readonly TimeSpan PrimaryReplenishInterval = TimeSpan.FromSeconds(5);

        public static IReadOnlyList<ResourceConfig> CreateSinglePlayerConfigs()
        {
            return new[]
            {
                ResourceConfig.Replenishing(ResourceType.Concrete, 1_300_000, 1_300_000, 30_000, PrimaryReplenishInterval), // capacity 1.3M approved 2026-11-28 (was 1.5M); starts full
                ResourceConfig.Replenishing(ResourceType.Iron, 900_000, 900_000, 20_000, PrimaryReplenishInterval),
                ResourceConfig.Replenishing(ResourceType.Aluminium, 400_000, 400_000, 10_000, PrimaryReplenishInterval),
                ResourceConfig.Replenishing(ResourceType.Fuel, 100_000, 100_000, 5_000, PrimaryReplenishInterval),
                ResourceConfig.Fixed(ResourceType.Gold, 10_000, 10_000),
                ResourceConfig.Fixed(ResourceType.Economy, 1_000, null),  // capacity not finalized
                ResourceConfig.Fixed(ResourceType.Military, 1_000, null), // capacity not finalized
                // GroupLimit: starting value and capacity not finalized, so it is deliberately not configured.
            };
        }

        public static ResourceLedger CreateSinglePlayerLedger() => new ResourceLedger(CreateSinglePlayerConfigs());
    }
}
