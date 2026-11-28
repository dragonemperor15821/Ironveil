using System.Collections.Generic;
using Ironveil.Economy;

/// <summary>
/// The single source of the Flak CONSTRUCTION rules (Build Additional Flaks, Row 1 Box 2):
/// capacity, the approved cost table and the build times, shared by all nine base types. Nothing
/// else in the project may hard-code these values. (Upgrade prices live in FlakUpgradeRules.)
///
/// Every base starts with 2 L1 Flaks. Costs are the APPROVED table (2026-11-28), used verbatim:
///
///   #2        conditional REBUILD: only when attacks have reduced the base to exactly 1 Flak
///             (and nothing is queued). Never an ordinary expansion purchase.
///   #3..#12   fit inside the passive storage of a player with ONE Home Base
///             (Concrete 1,300,000 / Iron 900,000 / Aluminium 400,000, HomeBaseResourceProfile).
///   #13..#28  each exceeds at least one one-base storage limit: they need storage expanded by
///             additional bases (checked at runtime against the ledger's current capacities).
///
///   Time      360 s + 20.8 s x (n - 3), rounded to whole seconds (#3 = 360 s, #28 = 880 s);
///             #2 takes the #3 time (360 s), as before. Unchanged by the cost revision.
///   Fuel 0, Gold 0.
/// </summary>
public static class FlakConstructionRules
{
    public const int StartingFlaks = 2;
    public const int MaxFlaks = FlakDefensiveLayout.RequiredPositions; // 28 approved positions
    public const int FirstAdditionalFlak = StartingFlaks + 1;          // #3
    public const int FinalFlak = MaxFlaks;                             // #28

    /// <summary>The conditional rebuild (#2): only when the base is down to exactly one Flak.</summary>
    public const int RebuildFlak = 2;
    /// <summary>Last Flak whose cost fits the one-Home-Base passive storage (#12).</summary>
    public const int LastOneBaseFlak = 12;
    /// <summary>First Flak that needs storage expanded by additional bases (#13).</summary>
    public const int FirstExpandedStorageFlak = LastOneBaseFlak + 1;

    /// <summary>First Flak number with a cost (#2; #1 can never be built, a base with no Flak has fallen).</summary>
    public const int FirstCostedFlak = RebuildFlak;

    public const int FirstSeconds = 360;
    public const int FinalSeconds = 880;

    /// <summary>Steps from #3 to #28 (25).</summary>
    public const int Steps = FinalFlak - FirstAdditionalFlak;

    // APPROVED table, #2..#28: { Concrete, Iron, Aluminium }. Do not edit without approval.
    private static readonly long[,] ApprovedCosts =
    {
        {    10_000,     6_900,     3_100 }, // #2 (conditional rebuild)
        {    20_000,    13_800,     6_200 }, // #3
        {    31_500,    21_700,     9_800 }, // #4
        {    49_700,    34_300,    15_400 }, // #5
        {    78_300,    54_000,    24_300 }, // #6
        {   123_400,    85_100,    38_300 }, // #7
        {   194_500,   134_200,    60_300 }, // #8
        {   306_500,   211_500,    95_000 }, // #9
        {   483_100,   333_300,   149_800 }, // #10
        {   761_400,   525_400,   236_000 }, // #11
        { 1_200_000,   828_000,   372_000 }, // #12
        { 1_350_000,   931_500,   418_500 }, // #13
        { 1_512_000, 1_043_300,   468_700 }, // #14
        { 1_693_400, 1_168_400,   525_000 }, // #15
        { 1_896_700, 1_308_700,   588_000 }, // #16
        { 2_124_300, 1_465_800,   658_500 }, // #17
        { 2_379_200, 1_641_600,   737_600 }, // #18
        { 2_664_700, 1_838_600,   826_100 }, // #19
        { 2_984_400, 2_059_200,   925_200 }, // #20
        { 3_342_600, 2_306_400, 1_036_200 }, // #21
        { 3_743_700, 2_583_200, 1_160_500 }, // #22
        { 4_192_900, 2_893_100, 1_299_800 }, // #23
        { 4_696_000, 3_240_200, 1_455_800 }, // #24
        { 5_259_600, 3_629_100, 1_630_500 }, // #25
        { 5_890_700, 4_064_600, 1_826_100 }, // #26
        { 6_597_600, 4_552_300, 2_045_300 }, // #27
        { 7_389_300, 5_098_600, 2_290_700 }, // #28
    };

    /// <summary>True for a Flak number that has a construction cost (2..28).</summary>
    public static bool IsValidFlakNumber(int flakNumber) => flakNumber >= FirstCostedFlak && flakNumber <= MaxFlaks;

    /// <summary>True for #2, the conditional rebuild.</summary>
    public static bool IsRebuild(int flakNumber) => flakNumber == RebuildFlak;

    /// <summary>
    /// True when a base with <paramref name="builtCount"/> constructed and <paramref name="queuedCount"/>
    /// queued Flaks qualifies for the #2 rebuild: exactly one surviving Flak and nothing under construction.
    /// </summary>
    public static bool IsRebuildEligible(int builtCount, int queuedCount) => builtCount == 1 && queuedCount == 0;

    /// <summary>True for #13..#28: the approved design needs storage expanded through additional bases.</summary>
    public static bool RequiresExpandedStorage(int flakNumber) => flakNumber >= FirstExpandedStorageFlak && flakNumber <= MaxFlaks;

    /// <summary>Price and build time of Flak number <paramref name="flakNumber"/> (2..28); false outside that range.</summary>
    public static bool TryGetCost(int flakNumber, out FlakConstructionCost cost)
    {
        if (!IsValidFlakNumber(flakNumber))
        {
            cost = default;
            return false;
        }

        int row = flakNumber - FirstCostedFlak;
        int k = flakNumber < FirstAdditionalFlak ? 0 : flakNumber - FirstAdditionalFlak;
        cost = new FlakConstructionCost
        {
            flakNumber = flakNumber,
            concrete = ApprovedCosts[row, 0],
            iron = ApprovedCosts[row, 1],
            aluminium = ApprovedCosts[row, 2],
            // 360 + 520 k / 25, rounded half up, in integer arithmetic (exact at both ends). Unchanged.
            durationSeconds = FirstSeconds + ((FinalSeconds - FirstSeconds) * k * 2 + Steps) / (2 * Steps),
        };
        return true;
    }

    /// <summary>Sum of the build times of Flaks <paramref name="from"/>..<paramref name="to"/> (inclusive).</summary>
    public static int TotalSeconds(int from = FirstAdditionalFlak, int to = FinalFlak)
    {
        int total = 0;
        for (int n = from; n <= to; n++)
            if (TryGetCost(n, out FlakConstructionCost c)) total += c.durationSeconds;
        return total;
    }
}

/// <summary>Resources and build time of one Flak construction.</summary>
[System.Serializable]
public struct FlakConstructionCost
{
    public int flakNumber;
    public long concrete;
    public long iron;
    public long aluminium;
    public int durationSeconds;

    /// <summary>The amounts to spend, by resource (Fuel and Gold are 0 and not listed).</summary>
    public IEnumerable<KeyValuePair<ResourceType, long>> Amounts()
    {
        yield return new KeyValuePair<ResourceType, long>(ResourceType.Concrete, concrete);
        yield return new KeyValuePair<ResourceType, long>(ResourceType.Iron, iron);
        yield return new KeyValuePair<ResourceType, long>(ResourceType.Aluminium, aluminium);
    }

    public long Amount(ResourceType resource) =>
        resource == ResourceType.Concrete ? concrete : resource == ResourceType.Iron ? iron : resource == ResourceType.Aluminium ? aluminium : 0;
}

/// <summary>Outcome of BaseConstruction.EvaluateFlakConstruction / TryStartFlakConstruction.</summary>
public enum FlakConstructionResult
{
    /// <summary>Evaluate: a construction can be started now.</summary>
    Available = 0,
    /// <summary>Start: resources spent, a position reserved, the construction queued.</summary>
    Started,
    /// <summary>No base (null or destroyed controller).</summary>
    InvalidBase,
    /// <summary>The base has no bound BaseFlakDefence (not a modular foundation, or not initialised).</summary>
    NotInitialised,
    /// <summary>The base has no constructed Flak: it has fallen and cannot build.</summary>
    BaseDestroyed,
    /// <summary>Built + queued Flaks already fill all 28 positions.</summary>
    MaxCapacity,
    /// <summary>The base's Flak level has no registered model (or the settings are missing).</summary>
    ModelMissing,
    /// <summary>The shared resource system is not running.</summary>
    NoResourceSystem,
    /// <summary>Not enough of at least one resource (see FlakConstructionQuote.missing).</summary>
    InsufficientResources,
    /// <summary>An upgrade of this base is running.</summary>
    UpgradeInProgress,
    /// <summary>The caller's expected Flak number no longer matches (stale or duplicate purchase).</summary>
    FlakNumberChanged,
    /// <summary>The resources could not be spent; nothing was taken.</summary>
    SpendFailed,
    /// <summary>No position could be reserved; the resources were refunded.</summary>
    ReservationFailed,
    /// <summary>
    /// The cost exceeds the player's current storage capacity of at least one resource (see
    /// FlakConstructionQuote.exceedsStorage): #13..#28 need storage expanded by additional bases.
    /// </summary>
    StorageInsufficient,
    /// <summary>#2 is a conditional rebuild: only when the base is down to exactly one Flak with nothing queued.</summary>
    RebuildNotEligible,
}

/// <summary>What starting a Flak construction on one base would do right now (read-only).</summary>
public struct FlakConstructionQuote
{
    public FlakConstructionResult status;
    /// <summary>Number of the Flak the next construction builds (built + queued + 1), 0 when none.</summary>
    public int flakNumber;
    public FlakConstructionCost cost;
    public long concreteBalance;
    public long ironBalance;
    public long aluminiumBalance;
    /// <summary>Resources whose balance is short, with the missing amount (empty unless InsufficientResources).</summary>
    public List<KeyValuePair<ResourceType, long>> missing;
    /// <summary>Resources whose current storage CAPACITY is below the cost, with the shortfall (StorageInsufficient).</summary>
    public List<KeyValuePair<ResourceType, long>> exceedsStorage;
    /// <summary>True when the next Flak is the #2 conditional rebuild.</summary>
    public bool isRebuild;
    public int builtCount;
    public int queuedCount;
    /// <summary>Level the constructions will be built at (the base's current Flak level).</summary>
    public int flakLevel;
    /// <summary>Seconds left on the construction in progress (0 when the queue is empty).</summary>
    public double currentRemainingSeconds;
    /// <summary>Seconds until the whole queue is finished.</summary>
    public double queueRemainingSeconds;
    public bool CanStart => status == FlakConstructionResult.Available;

    public long Balance(ResourceType resource) =>
        resource == ResourceType.Concrete ? concreteBalance : resource == ResourceType.Iron ? ironBalance : resource == ResourceType.Aluminium ? aluminiumBalance : 0;
}
