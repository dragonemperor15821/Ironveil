using System;
using UnityEngine;

/// <summary>
/// Which space a range value is expressed in. Strategy Combat (SC) reference
/// units are design values; Unity world units are what geometry uses. The two
/// are related only by <see cref="FlakRangeTable.ScToUnity"/> and are never
/// mixed implicitly.
/// </summary>
public enum RangeUnits
{
    StrategyCombatReference = 0,
    UnityWorld = 1
}

/// <summary>Minimum/maximum engagement distance for one level, tagged with its units.</summary>
public readonly struct FlakRangeBand
{
    public readonly float Minimum;
    public readonly float Maximum;
    public readonly RangeUnits Units;

    public FlakRangeBand(float minimum, float maximum, RangeUnits units)
    {
        Minimum = minimum;
        Maximum = maximum;
        Units = units;
    }

    /// <summary>Engagement band width (Maximum - Minimum).</summary>
    public float Width => Maximum - Minimum;

    public override string ToString() =>
        $"{Minimum:0.###}-{Maximum:0.###} " +
        (Units == RangeUnits.UnityWorld ? "Unity" : "SC");
}

/// <summary>
/// Flak range configuration (Stage 2a). Data only: no firing, targeting or damage.
///
/// Ranges are stored as Strategy Combat (SC) reference units, the approved
/// design values. Geometry converts them with one factor:
///
///     UnityRange = SCRange x S          (S = <see cref="ScToUnity"/>)
///
/// S is PROVISIONAL (1.0 for geometric testing only) and is not a physical
/// calibration. It lives only here, so changing it never requires terrain
/// regeneration and never changes base levels or base-type counts.
///
/// Ranges are measured from the base centre as full 3D distance.
/// </summary>
[Serializable]
public sealed class FlakRangeTable
{
    /// <summary>Provisional SC-to-Unity factor (Stage 2a working value, not approved as final).</summary>
    public const float ProvisionalScToUnity = 1.0f;

    // FINAL APPROVED SC reference ranges, L1..L6 (index 0 = L1). This is the
    // single source of these numbers in code; everything else reads the table.
    private static readonly float[] ApprovedMinimumSC = { 12f, 14f, 16f, 18f, 20f, 22f };
    private static readonly float[] ApprovedMaximumSC = { 16f, 18f, 20f, 22f, 24f, 26f };

    [Tooltip("Minimum engagement range per level L1..L6, in SC reference units.")]
    [SerializeField]
    private float[] minimumRangeSC = (float[])ApprovedMinimumSC.Clone();

    [Tooltip("Maximum engagement range per level L1..L6, in SC reference units.")]
    [SerializeField]
    private float[] maximumRangeSC = (float[])ApprovedMaximumSC.Clone();

    [Tooltip(
        "S in UnityRange = SCRange x S. PROVISIONAL working value for geometric " +
        "testing only; not a final physical calibration.")]
    [SerializeField, Min(0.0001f)]
    private float scToUnity = ProvisionalScToUnity;

    /// <summary>A table holding the final approved SC ranges and the provisional S.</summary>
    public static FlakRangeTable CreateApproved() => new FlakRangeTable();

    /// <summary>Approved SC band for a level, independent of any table instance.</summary>
    public static FlakRangeBand ApprovedRangeSC(BaseLevel level)
    {
        int i = LevelIndex(level);
        return new FlakRangeBand(
            ApprovedMinimumSC[i],
            ApprovedMaximumSC[i],
            RangeUnits.StrategyCombatReference);
    }

    /// <summary>S: Unity world units per SC reference unit.</summary>
    public float ScToUnity => scToUnity;

    /// <summary>A copy of this table with a different S (SC values unchanged). For analysis.</summary>
    public FlakRangeTable WithScToUnity(float s)
    {
        if (!IsFinite(s) || s <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(s), s, "S must be finite and > 0.");
        }

        return new FlakRangeTable
        {
            minimumRangeSC = (float[])minimumRangeSC.Clone(),
            maximumRangeSC = (float[])maximumRangeSC.Clone(),
            scToUnity = s
        };
    }

    /// <summary>Converts an SC reference distance to Unity world units.</summary>
    public float ToUnity(float scDistance) => scDistance * scToUnity;

    public bool TryGetRangeSC(BaseLevel level, out FlakRangeBand band)
    {
        band = default(FlakRangeBand);
        if (!IsDefinedLevel(level) || !Validate(out _))
        {
            return false;
        }

        int i = LevelIndex(level);
        band = new FlakRangeBand(
            minimumRangeSC[i],
            maximumRangeSC[i],
            RangeUnits.StrategyCombatReference);
        return true;
    }

    public bool TryGetRangeUnity(BaseLevel level, out FlakRangeBand band)
    {
        if (!TryGetRangeSC(level, out FlakRangeBand sc))
        {
            band = default(FlakRangeBand);
            return false;
        }

        band = new FlakRangeBand(
            ToUnity(sc.Minimum),
            ToUnity(sc.Maximum),
            RangeUnits.UnityWorld);
        return true;
    }

    /// <summary>SC band for a level. Throws for undefined levels or an invalid table.</summary>
    public FlakRangeBand GetRangeSC(BaseLevel level)
    {
        ThrowIfUnusable(level);
        TryGetRangeSC(level, out FlakRangeBand band);
        return band;
    }

    /// <summary>Unity-world band for a level (SC x S). Throws for undefined levels or an invalid table.</summary>
    public FlakRangeBand GetRangeUnity(BaseLevel level)
    {
        ThrowIfUnusable(level);
        TryGetRangeUnity(level, out FlakRangeBand band);
        return band;
    }

    /// <summary>Checks every level has a finite, non-negative band with max > min, and S > 0.</summary>
    public bool Validate(out string error)
    {
        error = null;
        if (minimumRangeSC == null || maximumRangeSC == null ||
            minimumRangeSC.Length != BaseLevelAssignment.LevelCount ||
            maximumRangeSC.Length != BaseLevelAssignment.LevelCount)
        {
            error = $"range table must define exactly {BaseLevelAssignment.LevelCount} levels (L1-L6)";
            return false;
        }

        if (!IsFinite(scToUnity) || scToUnity <= 0f)
        {
            error = $"S (SC-to-Unity) must be finite and > 0, was {scToUnity}";
            return false;
        }

        for (int i = 0; i < BaseLevelAssignment.LevelCount; i++)
        {
            float min = minimumRangeSC[i];
            float max = maximumRangeSC[i];
            if (!IsFinite(min) || !IsFinite(max) || min < 0f || max <= min)
            {
                error = $"L{i + 1}: invalid band {min}-{max} (need finite 0 <= min < max)";
                return false;
            }
        }

        return true;
    }

    public static bool IsDefinedLevel(BaseLevel level) =>
        (int)level >= 1 && (int)level <= BaseLevelAssignment.LevelCount;

    private void ThrowIfUnusable(BaseLevel level)
    {
        if (!IsDefinedLevel(level))
        {
            throw new ArgumentOutOfRangeException(
                nameof(level), (int)level, "Base level must be L1-L6.");
        }

        if (!Validate(out string error))
        {
            throw new InvalidOperationException("FlakRangeTable is invalid: " + error);
        }
    }

    private static int LevelIndex(BaseLevel level)
    {
        if (!IsDefinedLevel(level))
        {
            throw new ArgumentOutOfRangeException(
                nameof(level), (int)level, "Base level must be L1-L6.");
        }

        return (int)level - 1;
    }

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);
}
