using System;
using UnityEngine;

/// <summary>
/// Engagement range of each FLAK level (Flak Upgrade System, Stage 6). Data and geometry only:
/// there is no firing, targeting or damage yet.
///
/// Approved bands, in Strategy Combat (SC) reference units:
///
///   Flak L1   16 - 20
///   Flak L2   18 - 22
///   Flak L3   20 - 24
///   Flak L4   22 - 26      (inherits the original L6 crossfire range)
///
/// Every band is 4 wide and each level shifts both ends outward by 2. Ranges are measured from the
/// centre of the base (its root position, the same origin CrossfireGeometry uses) to the target as
/// full 3D distance, both ends inclusive. Overlapping fire between bases is allowed.
///
/// Units: values are stored in SC units and converted to Unity world units with the project's single
/// SC-to-Unity factor S (UnityRange = SCRange x S). S is still the PROVISIONAL
/// <see cref="FlakRangeTable.ProvisionalScToUnity"/> (1.0), not a confirmed calibration, so callers may
/// pass another S for analysis. At S = 1 the bands are Unity world units directly (the front attack
/// position, 3.3 ahead of a base centre, and the 10-unit approach are in the same space).
///
/// Relation to FlakRangeTable: that table is keyed by BASE level L1..L6 and drives the strategic
/// formation planner (L5/L6 pairs and clusters); it is unchanged. Flak level n has exactly the band of
/// formation level n + 2 (Flak L4 == formation L6 22-26, Flak L3 == formation L5 20-24); a test
/// asserts this so the two tables cannot drift apart silently.
/// </summary>
public static class FlakLevelRanges
{
    /// <summary>Width of every band, SC units.</summary>
    public const float BandWidthSC = 4f;

    /// <summary>Outward shift of both ends per Flak level, SC units.</summary>
    public const float LevelStepSC = 2f;

    // Index 0 = Flak L1. The single source of these numbers in code.
    private static readonly float[] MinimumSC = { 16f, 18f, 20f, 22f };
    private static readonly float[] MaximumSC = { 20f, 22f, 24f, 26f };

    /// <summary>Approved SC band of a Flak level; false for a level outside L1..L4.</summary>
    public static bool TryGetRangeSC(int flakLevel, out FlakRangeBand band)
    {
        if (!FlakUpgradeRules.IsValidLevel(flakLevel) || flakLevel > MinimumSC.Length)
        {
            band = default(FlakRangeBand);
            return false;
        }

        band = new FlakRangeBand(MinimumSC[flakLevel - 1], MaximumSC[flakLevel - 1], RangeUnits.StrategyCombatReference);
        return true;
    }

    /// <summary>Approved SC band of a Flak level. Throws for a level outside L1..L4.</summary>
    public static FlakRangeBand GetRangeSC(int flakLevel)
    {
        if (!TryGetRangeSC(flakLevel, out FlakRangeBand band))
        {
            throw new ArgumentOutOfRangeException(nameof(flakLevel), flakLevel, "Flak level must be L1-L4.");
        }

        return band;
    }

    /// <summary>Unity-world band of a Flak level: SC band x S (default: the provisional project S).</summary>
    public static FlakRangeBand GetRangeUnity(int flakLevel, float scToUnity = FlakRangeTable.ProvisionalScToUnity)
    {
        if (float.IsNaN(scToUnity) || float.IsInfinity(scToUnity) || scToUnity <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(scToUnity), scToUnity, "S must be finite and > 0.");
        }

        FlakRangeBand sc = GetRangeSC(flakLevel);
        return new FlakRangeBand(sc.Minimum * scToUnity, sc.Maximum * scToUnity, RangeUnits.UnityWorld);
    }

    /// <summary>The base level of FlakRangeTable whose band equals this Flak level's band (n + 2).</summary>
    public static BaseLevel EquivalentFormationLevel(int flakLevel)
    {
        GetRangeSC(flakLevel); // validates
        return (BaseLevel)(flakLevel + 2);
    }

    /// <summary>Full 3D distance from a base centre to a target.</summary>
    public static float Distance3D(Vector3 baseCentre, Vector3 target) => CrossfireGeometry.Distance3D(baseCentre, target);

    /// <summary>Where a target lies relative to a Flak level's band, measured from the base centre (full 3D).</summary>
    public static RangeBandStatus Classify(int flakLevel, Vector3 baseCentre, Vector3 target,
        float scToUnity = FlakRangeTable.ProvisionalScToUnity)
    {
        return CrossfireGeometry.Classify(Distance3D(baseCentre, target), GetRangeUnity(flakLevel, scToUnity));
    }

    /// <summary>True when a base at this Flak level can engage the target geometrically (min and max inclusive).</summary>
    public static bool CanEngage(int flakLevel, Vector3 baseCentre, Vector3 target,
        float scToUnity = FlakRangeTable.ProvisionalScToUnity)
    {
        return Classify(flakLevel, baseCentre, target, scToUnity) == RangeBandStatus.InBand;
    }
}
