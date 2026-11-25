using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// Stage 2b settings for deterministic L5/L6 facing pairs (land Heli/Air only).
/// Only read when BaseSpawner.generateStrategicFormations is on.
/// </summary>
[Serializable]
public sealed class FacingPairSettings
{
    [Tooltip("Inclusive range for the number of facing pairs per seed (chosen deterministically).")]
    [Min(0)] public int minPairs = 7;
    [Min(0)] public int maxPairs = 8;

    [Tooltip("Each member's yaw deviates from 'facing its partner' by up to this many degrees (deterministic).")]
    [Range(0f, 15f)] public float maxYawJitterDegrees = 4f;

    [Tooltip("Spacing is sampled this far inside the overlapping engagement window (Unity units), so 3D height and yaw jitter rarely push a pair out of band. Every pair is still validated exactly.")]
    [Min(0f)] public float windowEdgeMargin = 0.3f;

    [Tooltip("Allowed difference between the placed base's horizontal forward (after terrain alignment) and its intended heading, in degrees.")]
    [Min(0f)] public float facingToleranceDegrees = 1.5f;

    [Tooltip("Candidate pair poses tried per pair before swapping members.")]
    [Min(1)] public int attemptsPerPair = 4000;

    [Tooltip("Times a pair may swap in fresh members after exhausting its attempts.")]
    [Min(0)] public int maxMemberSubstitutions = 3;

    [Header("Corridor reservation (Stage 2b.1)")]
    [Tooltip("Reserve each pair's engagement corridor (front point to front point) and both frontal approach strips. No other base footprint (ordinary, route or another formation's member) may overlap them. Needs footprintAwareSpacing.")]
    public bool reserveCorridors = true;

    [Tooltip("PLACEHOLDER, not a troop-clearance value. Full width of every reserved strip in Unity units. 8 = about one 6.5x base diameter (~6.2) plus margin, and equals two land footprint clearances (2 x 4). Replace once troop dimensions are known.")]
    [Min(0.1f)] public float corridorWidth = 8f;

    [Tooltip("Also reserve each member's frontal approach along its own (jittered) forward, from its front attack position for the analysed extra approach, clipped at the partner's front position.")]
    public bool reserveFrontalApproaches = true;
}

/// <summary>One reserved strip (oriented rectangle on XZ) of a facing pair's corridor.</summary>
[Serializable]
public struct FormationCorridorSegment
{
    public string formationId;
    /// <summary>"axis" (front A to front B) or "approach A" / "approach B".</summary>
    public string kind;
    /// <summary>Start and end of the centreline on XZ (y = 0).</summary>
    public Vector3 start;
    public Vector3 end;
    public float width;

    public Vector3 Center => (start + end) * 0.5f;
    public float Length => Vector3.Distance(start, end);
    public float HeadingDegrees => FacingPairFormations.HeadingDegrees(end - start);
}

/// <summary>Private SplitMix64 stream seeded from (salt, seed). Never touches UnityEngine.Random.</summary>
public sealed class FormationRandom
{
    private ulong state;

    public FormationRandom(int seed, string salt)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < salt.Length; i++) hash = (hash ^ salt[i]) * 1099511628211UL;
            state = (hash ^ (uint)seed) * 1099511628211UL;
        }
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Uniform in [min, max].</summary>
    public float Range(float min, float max) => (float)(min + (max - min) * NextDouble());

    /// <summary>Uniform integer in [minInclusive, maxInclusive].</summary>
    public int RangeInclusive(int minInclusive, int maxInclusive)
    {
        if (maxInclusive <= minInclusive) return minInclusive;
        ulong span = (ulong)(maxInclusive - minInclusive + 1);
        return minInclusive + (int)(NextUInt64() % span);
    }
}

/// <summary>One supporting relationship inside a placed facing pair.</summary>
[Serializable]
public sealed class FacingPairLink
{
    public string supporterBaseId;
    public string defendedBaseId;
    /// <summary>Full 3D distance from the supporter's centre to the defended base's front attack position.</summary>
    public float distanceToFront;
    public RangeBandStatus statusAtFront;
    public float bandMinimum;
    public float bandMaximum;
    /// <summary>Exact extra-approach intervals (0..maxExtra) the supporter covers.</summary>
    public List<ApproachInterval> coverage = new List<ApproachInterval>();
    public float coveredLength;
    public float analysedLength;
    public bool FullApproachCovered => analysedLength > 0f && coveredLength >= analysedLength - 1e-4f;
    public bool EngagesAtFront => statusAtFront == RangeBandStatus.InBand;
}

[Serializable]
public sealed class FacingPairMember
{
    public string baseId;
    public string baseType;
    public BaseLevel level;
    /// <summary>Seated root position (range origin / base centre).</summary>
    public Vector3 position;
    /// <summary>Exact world XZ passed to the placement checks (reproduces the pose via ProbeLandPose).</summary>
    public Vector3 placementInputXZ;
    /// <summary>Root rotation euler Y (includes terrain alignment).</summary>
    public float yawDegrees;
    /// <summary>Heading of the horizontal forward (0 = +Z, 90 = +X).</summary>
    public float forwardHeadingDegrees;
    /// <summary>Intended heading: towards the partner plus deterministic jitter.</summary>
    public float intendedHeadingDegrees;
    /// <summary>Deterministic deviation from facing the partner exactly.</summary>
    public float yawJitterDegrees;
    /// <summary>|forward - intended| after terrain alignment.</summary>
    public float facingErrorDegrees;
    /// <summary>|forward - direction to partner|.</summary>
    public float angleToPartnerDegrees;
    public Vector3 frontAttackPosition;
}

/// <summary>Result of one placed facing pair (Stage 2b).</summary>
[Serializable]
public sealed class FacingPairRecord
{
    public string formationId;
    public FacingPairMember memberA;
    public FacingPairMember memberB;
    public bool mixedLevel;
    /// <summary>Overlapping horizontal centre-spacing window for this level combination (flat, colinear).</summary>
    public float windowMinimum;
    public float windowMaximum;
    /// <summary>Overlapping engagement band both front distances must lie in (mixed pairs: the intersection).</summary>
    public float overlapBandMinimum;
    public float overlapBandMaximum;
    /// <summary>Sampled horizontal spacing before terrain seating.</summary>
    public float requestedSpacing;
    public float horizontalDistance;
    public float centreDistance3D;
    public float heightDifference;
    /// <summary>B supports A (B engages troops at A's front).</summary>
    public FacingPairLink bSupportsA;
    /// <summary>A supports B.</summary>
    public FacingPairLink aSupportsB;
    public int attempts;
    public int memberSubstitutions;
    /// <summary>Reserved corridor strips (empty when reservation is off).</summary>
    public List<FormationCorridorSegment> corridor = new List<FormationCorridorSegment>();
    /// <summary>Rejected candidate poses before this pair was accepted, by reason.</summary>
    public string rejectionSummary;
}

/// <summary>
/// Deterministic L5/L6 facing-pair selection and engagement evaluation
/// (Stage 2b). Selection and pose sampling use private SplitMix64 streams
/// derived from the base seed; UnityEngine.Random is never used.
/// Placement itself (real footprint checks, reservation, instantiation) is
/// done by BaseSpawner.
/// </summary>
public static class FacingPairFormations
{
    public const string SelectionSalt = "IRONVEIL.FacingPairs.Select.v1";
    public const string PlacementSalt = "IRONVEIL.FacingPairs.Place.v1";

    /// <summary>
    /// Overlapping engagement band of two levels (Unity units): the distances
    /// both members can engage at, [max(minA, minB), min(maxA, maxB)]. For a
    /// same-level pair this is the level's own band. False when they do not overlap.
    /// </summary>
    public static bool TryGetOverlapBand(
        BaseLevel a, BaseLevel b, FlakRangeTable ranges, out float minimum, out float maximum)
    {
        minimum = maximum = 0f;
        if (ranges == null ||
            !ranges.TryGetRangeUnity(a, out FlakRangeBand ba) ||
            !ranges.TryGetRangeUnity(b, out FlakRangeBand bb))
        {
            return false;
        }

        minimum = Mathf.Max(ba.Minimum, bb.Minimum);
        maximum = Mathf.Min(ba.Maximum, bb.Maximum);
        return minimum <= maximum;
    }

    /// <summary>
    /// Horizontal centre-spacing window where each member can engage troops at
    /// the other's front attack position on a flat plane when both face each
    /// other exactly: the overlapping band shifted by the front attack distance.
    /// False when the two bands do not overlap.
    /// </summary>
    public static bool TryGetFacingWindow(
        BaseLevel a, BaseLevel b, FlakRangeTable ranges, CrossfireAttackModel model,
        out float minimum, out float maximum)
    {
        minimum = maximum = 0f;
        if (model == null || !TryGetOverlapBand(a, b, ranges, out float lo, out float hi))
        {
            return false;
        }

        minimum = lo + model.FrontAttackDistance;
        maximum = hi + model.FrontAttackDistance;
        return true;
    }

    /// <summary>
    /// Chooses the pair count (minPairs..maxPairs) and a deterministic ordering
    /// of eligible candidates. Input order never matters (sorted by ID first).
    /// </summary>
    public static List<FormationCandidate> SelectPool(
        int seed,
        IEnumerable<FormationCandidate> candidates,
        FormationPlannerSettings eligibility,
        FacingPairSettings settings,
        out int pairCount,
        SortedDictionary<string, int> exclusions)
    {
        SortedDictionary<string, FormationCandidate> byId =
            new SortedDictionary<string, FormationCandidate>(StringComparer.Ordinal);
        foreach (FormationCandidate c in candidates)
        {
            if (!string.IsNullOrEmpty(c.baseId)) byId[c.baseId] = c;
        }

        List<FormationCandidate> pool = new List<FormationCandidate>();
        foreach (FormationCandidate c in byId.Values)
        {
            if (StrategicFormationPlanner.IsEligible(c, eligibility, out string reason))
            {
                pool.Add(c);
            }
            else if (exclusions != null)
            {
                exclusions[reason] = exclusions.TryGetValue(reason, out int n) ? n + 1 : 1;
            }
        }

        FormationRandom rng = new FormationRandom(seed, SelectionSalt);
        int lo = Mathf.Min(settings.minPairs, settings.maxPairs);
        int hi = Mathf.Max(settings.minPairs, settings.maxPairs);
        pairCount = rng.RangeInclusive(lo, hi);

        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.NextUInt64() % (ulong)(i + 1));
            FormationCandidate t = pool[i];
            pool[i] = pool[j];
            pool[j] = t;
        }

        return pool;
    }

    /// <summary>Evaluates "supporter engages troops at defended's front" plus the extended approach.</summary>
    public static FacingPairLink EvaluateLink(
        CrossfireBase supporter, CrossfireBase defended, FlakRangeTable ranges, CrossfireAttackModel model)
    {
        Vector3 front = CrossfireGeometry.FrontAttackPosition(defended, model, 0f);
        EngagementTest test = CrossfireGeometry.Evaluate(supporter, front, ranges);
        FacingPairLink link = new FacingPairLink
        {
            supporterBaseId = supporter.baseId,
            defendedBaseId = defended.baseId,
            distanceToFront = test.distance,
            statusAtFront = test.status,
            bandMinimum = test.band.Minimum,
            bandMaximum = test.band.Maximum,
            coverage = CrossfireGeometry.ApproachCoverage(supporter, defended, ranges, model),
            analysedLength = model.MaxExtraApproach
        };

        foreach (ApproachInterval i in link.coverage) link.coveredLength += Mathf.Max(0f, i.Length);
        return link;
    }

    /// <summary>
    /// Corridor strips for a pair (pure geometry, XZ): the axis strip from A's
    /// front attack position to B's, and, optionally, each member's frontal
    /// approach along its own forward for model.MaxExtraApproach, clipped at
    /// the front-to-front distance. The space behind and beside the members is
    /// not reserved.
    /// </summary>
    public static List<FormationCorridorSegment> BuildCorridor(
        string formationId, FacingPairMember a, FacingPairMember b,
        FacingPairSettings settings, CrossfireAttackModel model)
    {
        List<FormationCorridorSegment> segments = new List<FormationCorridorSegment>();
        if (settings == null || !settings.reserveCorridors) return segments;

        Vector3 fa = Flat(a.frontAttackPosition);
        Vector3 fb = Flat(b.frontAttackPosition);
        float span = Vector3.Distance(fa, fb);
        if (span > 1e-4f)
        {
            segments.Add(new FormationCorridorSegment
            {
                formationId = formationId, kind = "axis", start = fa, end = fb, width = settings.corridorWidth
            });
        }

        if (settings.reserveFrontalApproaches && model != null)
        {
            float length = Mathf.Min(model.MaxExtraApproach, span);
            foreach ((FacingPairMember m, Vector3 front, string kind) in
                     new[] { (a, fa, "approach A"), (b, fb, "approach B") })
            {
                Vector3 dir = Flat(m.frontAttackPosition - m.position);
                if (length <= 1e-4f || dir.sqrMagnitude < 1e-8f) continue;
                segments.Add(new FormationCorridorSegment
                {
                    formationId = formationId, kind = kind, start = front,
                    end = front + dir.normalized * length, width = settings.corridorWidth
                });
            }
        }

        return segments;
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    public static float HeadingDegrees(Vector3 v) =>
        Mathf.Repeat(Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg, 360f);

    public static float HorizontalAngle(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Angle(a, b);
    }

    /// <summary>Editor-readable text report of placed pairs.</summary>
    public static string BuildReport(
        int seed, float scToUnity, CrossfireAttackModel model, int requestedPairs,
        int eligibleCount, IReadOnlyList<FacingPairRecord> pairs,
        IReadOnlyDictionary<string, int> exclusions, int unfilled)
    {
        CultureInfo ic = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("IRONVEIL Stage 2b.1 - Strategic formation report (facing pairs, corridor reservation)");
        sb.AppendFormat(ic, "seed {0} | S = {1:0.###} | front attack {2:0.##} | extra approach analysed {3:0.##}\n",
            seed, scToUnity, model.FrontAttackDistance, model.MaxExtraApproach);
        sb.AppendFormat(ic, "requested pairs {0} | placed {1} | unfilled {2} | eligible L5/L6 land Heli/Air {3}\n",
            requestedPairs, pairs.Count, unfilled, eligibleCount);
        if (exclusions != null)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in exclusions) parts.Add(kv.Key + " " + kv.Value);
            sb.AppendLine("excluded: " + string.Join(", ", parts));
        }

        foreach (FacingPairRecord p in pairs)
        {
            sb.AppendLine();
            sb.AppendFormat(ic, "{0}  {1}  window {2:0.00}-{3:0.00}  requested {4:0.00}  horizontal {5:0.00}  3D {6:0.00}  dy {7:0.00}  attempts {8}\n",
                p.formationId, p.mixedLevel ? "mixed" : "same-level", p.windowMinimum, p.windowMaximum,
                p.requestedSpacing, p.horizontalDistance, p.centreDistance3D, p.heightDifference, p.attempts);
            sb.AppendFormat(ic, "  overlapping engagement band [{0:0.##}, {1:0.##}]\n", p.overlapBandMinimum, p.overlapBandMaximum);
            AppendMember(sb, ic, "A", p.memberA);
            AppendMember(sb, ic, "B", p.memberB);
            AppendLink(sb, ic, p.bSupportsA);
            AppendLink(sb, ic, p.aSupportsB);
            if (p.corridor != null && p.corridor.Count > 0)
            {
                List<string> segs = new List<string>();
                foreach (FormationCorridorSegment s in p.corridor)
                    segs.Add(string.Format(ic, "{0} {1:0.00}x{2:0.##} hdg {3:0.0}", s.kind, s.Length, s.width, s.HeadingDegrees));
                sb.AppendLine("  reserved corridor: " + string.Join("; ", segs));
            }
            else
            {
                sb.AppendLine("  reserved corridor: none");
            }
            sb.AppendLine("  rejected candidate poses: " + (p.rejectionSummary ?? "none"));
        }

        return sb.ToString();
    }

    private static void AppendMember(StringBuilder sb, CultureInfo ic, string tag, FacingPairMember m)
    {
        sb.AppendFormat(ic,
            "  {0} {1,-14} {2,-4} {3}  pos ({4:0.00}, {5:0.00}, {6:0.00})  yaw {7:0.0}  fwd {8:0.0}  intended {9:0.0} (jitter {10:+0.0;-0.0})  facing err {11:0.00}  to partner {12:0.0}\n",
            tag, m.baseId, m.baseType, m.level, m.position.x, m.position.y, m.position.z,
            m.yawDegrees, m.forwardHeadingDegrees, m.intendedHeadingDegrees, m.yawJitterDegrees,
            m.facingErrorDegrees, m.angleToPartnerDegrees);
    }

    private static void AppendLink(StringBuilder sb, CultureInfo ic, FacingPairLink l)
    {
        List<string> iv = new List<string>();
        foreach (ApproachInterval i in l.coverage) iv.Add(i.ToString());
        sb.AppendFormat(ic,
            "  {0} -> front of {1}: {2:0.00} in [{3:0.##}, {4:0.##}] {5}; approach coverage {6} = {7:0.00}/{8:0.##} ({9})\n",
            l.supporterBaseId, l.defendedBaseId, l.distanceToFront, l.bandMinimum, l.bandMaximum, l.statusAtFront,
            iv.Count == 0 ? "none" : string.Join(" ", iv), l.coveredLength, l.analysedLength,
            l.FullApproachCovered ? "full" : "partial");
    }
}
