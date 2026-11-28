using System;
using UnityEngine;

namespace Ironveil.Economy
{
    /// <summary>
    /// Unity-serializable form of a <see cref="ResourceConfig"/>. Unity cannot serialize <c>long?</c> or
    /// <see cref="TimeSpan"/>, so an undecided capacity is stored as an explicit <see cref="HasCapacity"/> flag,
    /// and the interval is stored in whole milliseconds.
    ///
    /// The entry holds data only. Validation is done by <see cref="ResourceConfig"/> (see <see cref="ToConfig"/>),
    /// plus a few checks below that exist only because of the serialized shape.
    /// </summary>
    [Serializable]
    public struct ResourceConfigEntry
    {
        [SerializeField] private ResourceType resource;
        [SerializeField] private long startingBalance;

        [Tooltip("Off = capacity not decided yet. This is neither zero nor unlimited.")]
        [SerializeField] private bool hasCapacity;
        [SerializeField] private long capacity;

        [SerializeField] private bool replenishes;
        [SerializeField] private long replenishAmount;
        [SerializeField] private long replenishIntervalMilliseconds;

        public ResourceType Resource => resource;
        public long StartingBalance => startingBalance;
        public bool HasCapacity => hasCapacity;
        public long Capacity => capacity;
        public bool Replenishes => replenishes;
        public long ReplenishAmount => replenishAmount;
        public long ReplenishIntervalMilliseconds => replenishIntervalMilliseconds;

        public ResourceConfigEntry(ResourceType resource, long startingBalance, bool hasCapacity, long capacity,
            bool replenishes, long replenishAmount, long replenishIntervalMilliseconds)
        {
            this.resource = resource;
            this.startingBalance = startingBalance;
            this.hasCapacity = hasCapacity;
            this.capacity = capacity;
            this.replenishes = replenishes;
            this.replenishAmount = replenishAmount;
            this.replenishIntervalMilliseconds = replenishIntervalMilliseconds;
        }

        /// <summary>Converts a validated <see cref="ResourceConfig"/> into its serialized form.</summary>
        public static ResourceConfigEntry FromConfig(ResourceConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            long intervalTicks = config.ReplenishInterval.Ticks;
            if (intervalTicks % TimeSpan.TicksPerMillisecond != 0)
                throw new ArgumentException($"{config.Resource}: replenish interval must be a whole number of milliseconds.", nameof(config));

            return new ResourceConfigEntry(
                config.Resource,
                config.StartingBalance,
                config.Capacity.HasValue,
                config.Capacity ?? 0,
                config.IsReplenishing,
                config.ReplenishAmount,
                intervalTicks / TimeSpan.TicksPerMillisecond);
        }

        /// <summary>
        /// Builds the runtime <see cref="ResourceConfig"/>. Throws <see cref="ArgumentException"/> (or a subclass)
        /// describing the first problem found.
        /// </summary>
        public ResourceConfig ToConfig()
        {
            // Values left in disabled fields are ambiguous; reject them rather than silently ignoring them.
            if (!hasCapacity && capacity != 0)
                throw new ArgumentException($"{resource}: capacity is {capacity} but 'Has Capacity' is off. Enable it, or set capacity to 0 to leave it undecided.");
            if (!replenishes && (replenishAmount != 0 || replenishIntervalMilliseconds != 0))
                throw new ArgumentException($"{resource}: replenish values are set but 'Replenishes' is off.");

            if (!replenishes)
                return ResourceConfig.Fixed(resource, startingBalance, hasCapacity ? capacity : (long?)null);

            if (!hasCapacity)
                throw new ArgumentException($"{resource}: a replenishing resource must have a configured capacity.");
            if (replenishIntervalMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(replenishIntervalMilliseconds), replenishIntervalMilliseconds, $"{resource}: replenish interval must be positive.");
            if (replenishIntervalMilliseconds > (long)ResourceConfig.MaxReplenishInterval.TotalMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(replenishIntervalMilliseconds), replenishIntervalMilliseconds, $"{resource}: replenish interval must not exceed {ResourceConfig.MaxReplenishInterval}.");

            return ResourceConfig.Replenishing(resource, startingBalance, capacity, replenishAmount,
                TimeSpan.FromTicks(replenishIntervalMilliseconds * TimeSpan.TicksPerMillisecond));
        }
    }
}
