using System;
using System.Globalization;

namespace Ironveil.Economy
{
    /// <summary>
    /// Text formatting for the resource HUD. Pure and culture-independent, so the HUD shows the same digits and
    /// separators on every machine and browser locale.
    /// </summary>
    public static class ResourceHudFormat
    {
        /// <summary>Shown wherever a value is not configured or not decided yet. Never replaced by a made-up number.</summary>
        public const string Unavailable = "—"; // em dash

        /// <summary>Display order of the eight HUD items (also the <see cref="ResourceType"/> declaration order).</summary>
        public static readonly ResourceType[] DisplayOrder =
        {
            ResourceType.Concrete,
            ResourceType.Iron,
            ResourceType.Aluminium,
            ResourceType.Fuel,
            ResourceType.Economy,
            ResourceType.Military,
            ResourceType.Gold,
            ResourceType.GroupLimit,
        };

        /// <summary>Concrete, Iron, Aluminium and Fuel form the primary group; the rest are strategic statistics.</summary>
        public static bool IsPrimary(ResourceType resource) => ResourceTypeRules.SupportsReplenishment(resource);

        public static string DisplayName(ResourceType resource)
        {
            switch (resource)
            {
                case ResourceType.Concrete: return "CONCRETE";
                case ResourceType.Iron: return "IRON";
                case ResourceType.Aluminium: return "ALUMINIUM";
                case ResourceType.Fuel: return "FUEL";
                case ResourceType.Economy: return "ECONOMY";
                case ResourceType.Military: return "MILITARY";
                case ResourceType.Gold: return "GOLD";
                case ResourceType.GroupLimit: return "GROUP LIMIT";
                default: return resource.ToString().ToUpperInvariant();
            }
        }

        /// <summary>Full number with comma group separators, never abbreviated: 1500000 -> "1,500,000".</summary>
        public static string Amount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>"/ 1,500,000".</summary>
        public static string Capacity(long capacity) => "/ " + Amount(capacity);

        /// <summary>"+30,000 / 5s". Whole seconds print without decimals; other intervals with one decimal.</summary>
        public static string Replenishment(long amount, TimeSpan interval) => "+" + Amount(amount) + " / " + Interval(interval);

        public static string Interval(TimeSpan interval)
        {
            double s = interval.TotalSeconds;
            if (Math.Abs(s - Math.Round(s)) < 1e-9) return ((long)Math.Round(s)).ToString(CultureInfo.InvariantCulture) + "s";
            return s.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>
        /// Countdown rounded UP to the next tenth of a second, so it never reads 0.0s before the tick has happened.
        /// Returns the tenths value so callers can skip rebuilding unchanged text.
        /// </summary>
        public static int CountdownTenths(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero) return 0;
            long tenths = (remaining.Ticks + TimeSpan.TicksPerSecond / 10 - 1) / (TimeSpan.TicksPerSecond / 10);
            return tenths > int.MaxValue ? int.MaxValue : (int)tenths;
        }

        /// <summary>"NEXT 3.2s".</summary>
        public static string Countdown(int tenths) =>
            "NEXT " + (tenths / 10).ToString(CultureInfo.InvariantCulture) + "." + (tenths % 10).ToString(CultureInfo.InvariantCulture) + "s";

        /// <summary>Active cycle badge, "×2". Empty when no cycle is running.</summary>
        public static string Cycles(int count) => count > 0 ? "×" + count.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }
}
