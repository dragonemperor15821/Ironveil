/// <summary>
/// The single source of the Flak upgrade rules: four Flak levels per base, the Gold price of each
/// collective upgrade and the maximum level. Nothing else in the project may hard-code these values.
///
/// An upgrade is bought PER BASE and replaces every Flak on that base at once; the Gold comes from the
/// shared player-wide ledger (ResourceManager). Prices are totals per upgrade, not per Flak.
///
///   L1 -> L2   19 Gold
///   L2 -> L3   29 Gold   (Stage 6; was 39 in the three-level design)
///   L3 -> L4   39 Gold   (approved Stage 6; configured in <see cref="GoldCostL3ToL4"/>. Setting it to
///              null would make the upgrade unavailable with FlakUpgradeResult.PriceNotSet.)
///   L4         maximum, no further upgrade
///
/// Models per level live in Flak.asset (L1 Level 1 Flak, L2 Level 2 Flak, L3 Level 3 Flak,
/// L4 the L6 Base Turret model); range bands per level live in FlakLevelRanges.
/// </summary>
public static class FlakUpgradeRules
{
    public const int MinLevel = 1;
    public const int MaxLevel = 4;

    /// <summary>Level every newly generated base starts at (matches InitialBuildingConfig: 2 x L1 Flak).</summary>
    public const int StartingLevel = MinLevel;

    public const long GoldCostL1ToL2 = 19;
    public const long GoldCostL2ToL3 = 29;

    /// <summary>
    /// Gold price of L3 -> L4 (approved: 39). <c>null</c> would mean "not approved" (the upgrade is then
    /// unavailable with <see cref="FlakUpgradeResult.PriceNotSet"/>). Change the total here only.
    /// </summary>
    public static readonly long? GoldCostL3ToL4 = 39;

    /// <summary>True for L1..L4.</summary>
    public static bool IsValidLevel(int level) => level >= MinLevel && level <= MaxLevel;

    /// <summary>Clamps any value into L1..L4 (an unset or corrupt value reads as L1).</summary>
    public static int Sanitize(int level) => IsValidLevel(level) ? level : (level > MaxLevel ? MaxLevel : MinLevel);

    /// <summary>True below the maximum level (whether or not that upgrade's price is approved yet).</summary>
    public static bool CanUpgradeFrom(int currentLevel) => IsValidLevel(currentLevel) && currentLevel < MaxLevel;

    /// <summary>The level an upgrade from <paramref name="currentLevel"/> leads to, or 0 when none is allowed.</summary>
    public static int NextLevel(int currentLevel) => CanUpgradeFrom(currentLevel) ? currentLevel + 1 : 0;

    /// <summary>True when an upgrade exists from <paramref name="currentLevel"/> but its Gold price is not approved yet.</summary>
    public static bool IsPricePending(int currentLevel) => CanUpgradeFrom(currentLevel) && !TryGetUpgradeCost(currentLevel, out _);

    /// <summary>
    /// Total Gold for the collective upgrade from <paramref name="currentLevel"/>; false at L4, for an
    /// invalid level, or while that upgrade's price is not approved (L3 -> L4 today).
    /// </summary>
    public static bool TryGetUpgradeCost(int currentLevel, out long gold)
    {
        switch (currentLevel)
        {
            case 1: gold = GoldCostL1ToL2; return true;
            case 2: gold = GoldCostL2ToL3; return true;
            case 3:
                gold = GoldCostL3ToL4 ?? 0;
                return GoldCostL3ToL4.HasValue && GoldCostL3ToL4.Value >= 0;
            default: gold = 0; return false;
        }
    }
}

/// <summary>Outcome of BaseConstruction.EvaluateFlakUpgrade / TryUpgradeFlaks.</summary>
public enum FlakUpgradeResult
{
    /// <summary>EvaluateFlakUpgrade: the upgrade can be bought now.</summary>
    Available = 0,
    /// <summary>TryUpgradeFlaks: every Flak on the base was replaced, the Gold was spent and the level raised.</summary>
    Upgraded,
    /// <summary>No base (null or destroyed controller).</summary>
    InvalidBase,
    /// <summary>The base has no bound BaseFlakDefence (not a modular foundation, or not initialised yet).</summary>
    NotInitialised,
    /// <summary>The base has no constructed Flak (destroyed base).</summary>
    NoFlaks,
    /// <summary>The base is already at L4, the maximum.</summary>
    AtMaximumLevel,
    /// <summary>The next level has no registered model in Flak.asset (or the settings are missing).</summary>
    ModelMissing,
    /// <summary>The shared resource system is not running (no initialised ResourceManager).</summary>
    NoResourceSystem,
    /// <summary>Not enough Gold in the shared player-wide balance.</summary>
    InsufficientGold,
    /// <summary>An upgrade of this base is already running (re-entrant call).</summary>
    UpgradeInProgress,
    /// <summary>The caller's expected current level no longer matches (stale or duplicate purchase).</summary>
    LevelChanged,
    /// <summary>The Gold could not be taken from the ledger.</summary>
    SpendFailed,
    /// <summary>A replacement Flak could not be placed; everything was rolled back and the Gold refunded.</summary>
    ReplacementFailed,
    /// <summary>The next upgrade exists but its Gold price is not approved yet (L3 -> L4, FlakUpgradeRules.GoldCostL3ToL4).</summary>
    PriceNotSet,
}

/// <summary>What an upgrade of one base would do right now (read-only; nothing is spent or changed).</summary>
public struct FlakUpgradeQuote
{
    public FlakUpgradeResult status;
    public int currentLevel;
    /// <summary>Level after the upgrade, or 0 when none is possible.</summary>
    public int nextLevel;
    /// <summary>Gold the upgrade costs (0 at L4 or while the price is not approved).</summary>
    public long goldCost;
    /// <summary>False when the next upgrade's price is not approved yet (goldCost is then 0 and meaningless).</summary>
    public bool goldCostKnown;
    /// <summary>Shared Gold balance at evaluation time (0 without a resource system).</summary>
    public long goldBalance;
    /// <summary>Constructed Flaks that would be replaced.</summary>
    public int flakCount;
    public bool CanUpgrade => status == FlakUpgradeResult.Available;
}
