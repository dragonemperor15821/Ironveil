namespace Ironveil.Economy
{
    /// <summary>
    /// Every resource tracked by IRONVEIL's economy.
    /// Numeric values are used as array indices by <see cref="ResourceLedger"/>;
    /// append new members at the end and update <see cref="ResourceTypeRules.Count"/>.
    /// </summary>
    public enum ResourceType
    {
        Concrete = 0,
        Iron = 1,
        Aluminium = 2,
        Fuel = 3,
        Economy = 4,
        Military = 5,
        Gold = 6,
        GroupLimit = 7,
    }

    /// <summary>Static rules about resource types that do not depend on configuration.</summary>
    public static class ResourceTypeRules
    {
        /// <summary>Number of members in <see cref="ResourceType"/>.</summary>
        public const int Count = 8;

        /// <summary>True for members actually declared in <see cref="ResourceType"/>.</summary>
        public static bool IsDefined(ResourceType resource)
        {
            int index = (int)resource;
            return index >= 0 && index < Count;
        }

        /// <summary>
        /// Primary resources (Concrete, Iron, Aluminium, Fuel) are the only ones that may
        /// receive automatic replenishment. Gold, Economy, Military and GroupLimit never do.
        /// </summary>
        public static bool SupportsReplenishment(ResourceType resource)
        {
            switch (resource)
            {
                case ResourceType.Concrete:
                case ResourceType.Iron:
                case ResourceType.Aluminium:
                case ResourceType.Fuel:
                    return true;
                default:
                    return false;
            }
        }
    }
}
