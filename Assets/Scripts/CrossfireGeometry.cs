using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Provisional model of where troops stand when attacking a base from its
/// front. All values are Unity world units (not SC units).
/// </summary>
[Serializable]
public sealed class CrossfireAttackModel
{
    /// <summary>Provisional: troops attacking from the front stand this far ahead of the base centre.</summary>
    public const float ProvisionalFrontAttackDistance = 3.3f;

    /// <summary>Furthest extra advance along the approach that analysis considers (not an assumed attack distance).</summary>
    public const float ProvisionalMaxExtraApproach = 10f;

    [Tooltip("Unity units ahead of the base centre, along its forward, where front attackers stand. Provisional.")]
    [SerializeField, Min(0f)]
    private float frontAttackDistance = ProvisionalFrontAttackDistance;

    [Tooltip("Analysis only: how much further along the approach direction troops may be tested (Unity units).")]
    [SerializeField, Min(0f)]
    private float maxExtraApproach = ProvisionalMaxExtraApproach;

    public CrossfireAttackModel() { }

    public CrossfireAttackModel(float frontAttackDistance, float maxExtraApproach)
    {
        this.frontAttackDistance = Mathf.Max(0f, frontAttackDistance);
        this.maxExtraApproach = Mathf.Max(0f, maxExtraApproach);
    }

    public static CrossfireAttackModel CreateProvisional() => new CrossfireAttackModel();

    public float FrontAttackDistance => frontAttackDistance;
    public float MaxExtraApproach => maxExtraApproach;
}

/// <summary>Where a target lies relative to one base's engagement band.</summary>
public enum RangeBandStatus
{
    /// <summary>Closer than the minimum range: the base cannot engage.</summary>
    InsideMinimum = 0,
    /// <summary>Minimum &lt;= distance &lt;= maximum (both inclusive): the base can engage geometrically.</summary>
    InBand = 1,
    /// <summary>Further than the maximum range.</summary>
    BeyondMaximum = 2,
    /// <summary>The base has no valid range (undefined level / invalid table).</summary>
    NoRange = 3
}

/// <summary>Geometry of one base for crossfire analysis (Unity world space).</summary>
public struct CrossfireBase
{
    public string baseId;
    public string baseType;
    public BaseLevel level;
    /// <summary>Base centre (range origin).</summary>
    public Vector3 centre;
    /// <summary>Horizontal unit forward (base local +Z, the spawner's turret-facing convention).</summary>
    public Vector3 forward;
    /// <summary>False for bases with no defined defensive front (currently Tank).</summary>
    public bool hasDefensiveFront;

    public static CrossfireBase FromPose(
        string baseId, string baseType, BaseLevel level,
        Vector3 centre, Quaternion rotation, bool hasDefensiveFront)
    {
        return new CrossfireBase
        {
            baseId = baseId,
            baseType = baseType,
            level = level,
            centre = centre,
            forward = CrossfireGeometry.HorizontalForward(rotation),
            hasDefensiveFront = hasDefensiveFront
        };
    }

    public static CrossfireBase FromRecord(GeneratedBaseRecord record, bool hasDefensiveFront)
    {
        return new CrossfireBase
        {
            baseId = record.baseId,
            baseType = record.baseType,
            level = record.level,
            centre = record.position,
            forward = CrossfireGeometry.ForwardFromYaw(record.yawDegrees),
            hasDefensiveFront = hasDefensiveFront
        };
    }
}

/// <summary>Result of testing one supporting base against one target point.</summary>
public struct EngagementTest
{
    public RangeBandStatus status;
    /// <summary>Full 3D centre-to-target distance, Unity units.</summary>
    public float distance;
    /// <summary>The supporter's band in Unity units (SC x S).</summary>
    public FlakRangeBand band;

    public bool CanEngage => status == RangeBandStatus.InBand;
}

/// <summary>Closed interval [start, end] of extra approach distance (Unity units).</summary>
[Serializable]
public struct ApproachInterval
{
    public float start;
    public float end;
    public float Length => end - start;
    public override string ToString() => $"[{start:0.###}, {end:0.###}]";
}

/// <summary>
/// Pure crossfire geometry (Stage 2a). It answers "could this base engage
/// that point" from ranges and positions only. It does not implement firing,
/// damage, target acquisition or troop behaviour, and it never touches
/// UnityEngine.Random.
///
/// Conventions:
///  - Range originates from the supporting base's centre; distance is full 3D.
///  - Bands are inclusive at both ends.
///  - The front attack position is the defended base centre plus
///    frontAttackDistance along its horizontal forward (same height as the
///    centre), plus an optional extra advance along the same direction.
/// </summary>
public static class CrossfireGeometry
{
    /// <summary>Horizontal unit forward (local +Z flattened onto XZ).</summary>
    public static Vector3 HorizontalForward(Quaternion rotation)
    {
        Vector3 f = rotation * Vector3.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-12f ? f.normalized : Vector3.forward;
    }

    /// <summary>Horizontal unit forward for a yaw in degrees (0 = +Z, 90 = +X).</summary>
    public static Vector3 ForwardFromYaw(float yawDegrees)
    {
        float r = yawDegrees * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
    }

    public static float Distance3D(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    /// <summary>
    /// Front attack position: centre + forward_h x (attackDistance + extraApproach).
    /// Height is the base centre's height.
    /// </summary>
    public static Vector3 FrontAttackPosition(
        Vector3 centre, Vector3 forward, float attackDistance, float extraApproach = 0f)
    {
        Vector3 f = forward;
        f.y = 0f;
        f = f.sqrMagnitude > 1e-12f ? f.normalized : Vector3.forward;
        return centre + f * (attackDistance + extraApproach);
    }

    public static Vector3 FrontAttackPosition(
        CrossfireBase defended, CrossfireAttackModel model, float extraApproach = 0f)
    {
        return FrontAttackPosition(
            defended.centre, defended.forward, model.FrontAttackDistance, extraApproach);
    }

    /// <summary>Classifies a distance against a Unity-space band (inclusive ends).</summary>
    public static RangeBandStatus Classify(float distance, FlakRangeBand unityBand)
    {
        if (unityBand.Units != RangeUnits.UnityWorld)
        {
            throw new ArgumentException(
                "Classify needs a Unity-world band; convert SC ranges with FlakRangeTable first.",
                nameof(unityBand));
        }

        if (distance < unityBand.Minimum) return RangeBandStatus.InsideMinimum;
        if (distance > unityBand.Maximum) return RangeBandStatus.BeyondMaximum;
        return RangeBandStatus.InBand;
    }

    /// <summary>Tests whether a supporting base could engage a target point.</summary>
    public static EngagementTest Evaluate(
        CrossfireBase supporter, Vector3 target, FlakRangeTable ranges)
    {
        EngagementTest test = new EngagementTest
        {
            distance = Distance3D(supporter.centre, target)
        };

        if (ranges == null || !ranges.TryGetRangeUnity(supporter.level, out test.band))
        {
            test.status = RangeBandStatus.NoRange;
            return test;
        }

        test.status = Classify(test.distance, test.band);
        return test;
    }

    public static bool CanEngage(CrossfireBase supporter, Vector3 target, FlakRangeTable ranges) =>
        Evaluate(supporter, target, ranges).CanEngage;

    /// <summary>True when both bases could engage the same target point.</summary>
    public static bool CouldBothEngage(
        CrossfireBase a, CrossfireBase b, Vector3 target, FlakRangeTable ranges) =>
        CanEngage(a, target, ranges) && CanEngage(b, target, ranges);

    /// <summary>Every base in the list that could engage the target.</summary>
    public static List<CrossfireBase> EngagingBases(
        IEnumerable<CrossfireBase> bases, Vector3 target, FlakRangeTable ranges)
    {
        List<CrossfireBase> result = new List<CrossfireBase>();
        foreach (CrossfireBase b in bases)
        {
            if (CanEngage(b, target, ranges)) result.Add(b);
        }

        return result;
    }

    /// <summary>
    /// Exact sub-intervals of extra approach e in [0, maxExtra] where the
    /// supporter can engage troops at FrontAttackPosition(defended, e).
    /// Solved analytically (the distance along a line is a quadratic), so
    /// no sampling resolution is involved.
    /// </summary>
    public static List<ApproachInterval> ApproachCoverage(
        CrossfireBase supporter,
        CrossfireBase defended,
        FlakRangeTable ranges,
        CrossfireAttackModel model)
    {
        List<ApproachInterval> result = new List<ApproachInterval>();
        if (ranges == null || model == null ||
            !ranges.TryGetRangeUnity(supporter.level, out FlakRangeBand band))
        {
            return result;
        }

        Vector3 start = FrontAttackPosition(defended, model, 0f);
        Vector3 dir = defended.forward;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.forward;

        // |start + dir*e - P|^2 = e^2 + 2 e (w.dir) + |w|^2,  w = start - P.
        Vector3 w = start - supporter.centre;
        double bHalf = Vector3.Dot(w, dir);
        double c = w.sqrMagnitude;
        double eMax = model.MaxExtraApproach;

        // d(e) <= max  <=>  e in [r1, r2]   (or empty)
        if (!SolveWithin(bHalf, c, band.Maximum, out double lo, out double hi))
        {
            return result;
        }

        lo = Math.Max(lo, 0.0);
        hi = Math.Min(hi, eMax);
        if (lo > hi) return result;

        // d(e) >= min  <=>  e outside (m1, m2)
        if (!SolveWithin(bHalf, c, band.Minimum, out double m1, out double m2) || band.Minimum <= 0f)
        {
            result.Add(new ApproachInterval { start = (float)lo, end = (float)hi });
            return result;
        }

        if (lo <= m1)
        {
            result.Add(new ApproachInterval { start = (float)lo, end = (float)Math.Min(hi, m1) });
        }

        if (hi >= m2)
        {
            result.Add(new ApproachInterval { start = (float)Math.Max(lo, m2), end = (float)hi });
        }

        return result;
    }

    /// <summary>True when the supporter can engage somewhere on the defended base's approach (front .. front+maxExtra).</summary>
    public static bool CanEngageAnywhereOnApproach(
        CrossfireBase supporter, CrossfireBase defended, FlakRangeTable ranges, CrossfireAttackModel model) =>
        ApproachCoverage(supporter, defended, ranges, model).Count > 0;

    /// <summary>
    /// Whether two bases could both engage the same troops somewhere on the
    /// defended base's approach. Returns the shared extra-approach intervals.
    /// The defended base may itself be one of the two.
    /// </summary>
    public static List<ApproachInterval> SharedApproachCoverage(
        CrossfireBase a, CrossfireBase b, CrossfireBase defended,
        FlakRangeTable ranges, CrossfireAttackModel model)
    {
        List<ApproachInterval> ia = ApproachCoverage(a, defended, ranges, model);
        List<ApproachInterval> ib = ApproachCoverage(b, defended, ranges, model);
        List<ApproachInterval> shared = new List<ApproachInterval>();
        foreach (ApproachInterval x in ia)
        {
            foreach (ApproachInterval y in ib)
            {
                float s = Mathf.Max(x.start, y.start);
                float e = Mathf.Min(x.end, y.end);
                if (s <= e) shared.Add(new ApproachInterval { start = s, end = e });
            }
        }

        return shared;
    }

    // Roots of e^2 + 2 bHalf e + c = r^2. False when the line never comes within r.
    private static bool SolveWithin(double bHalf, double c, double r, out double lo, out double hi)
    {
        double disc = bHalf * bHalf - (c - r * r);
        if (disc < 0.0)
        {
            lo = hi = 0.0;
            return false;
        }

        double s = Math.Sqrt(disc);
        lo = -bHalf - s;
        hi = -bHalf + s;
        return true;
    }
}
