using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// Stage 2c settings for deterministic inward-facing L5/L6 clusters (land Heli/Air only).
/// Only read when BaseSpawner.generateStrategicClusters is on.
/// </summary>
[Serializable]
public sealed class ClusterSettings
{
    [Tooltip("Inclusive range for the number of clusters per seed (chosen deterministically).")]
    [Min(0)] public int minClusters = 2;
    [Min(0)] public int maxClusters = 3;

    [Tooltip("Inclusive range for the members of each cluster (3 or 4, chosen deterministically per cluster).")]
    [Range(3, 4)] public int minMembers = 3;
    [Range(3, 4)] public int maxMembers = 4;

    [Tooltip("Every member's front attack position must be engageable (inside min and max range, full 3D) by at least this many OTHER members. Not relaxed automatically.")]
    [Min(1)] public int minSupportersPerFront = 2;

    [Tooltip("The cluster radius is sampled this far inside the analytic radius window (Unity units) where the intended supporters engage the fronts on a flat plane. Every cluster is still validated exactly.")]
    [Min(0f)] public float radiusEdgeMargin = 0.3f;

    [Tooltip("Per-member deviation from the evenly spaced slot angle (degrees).")]
    [Range(0f, 15f)] public float maxAngleJitterDegrees = 4f;

    [Tooltip("Per-member deviation from the cluster radius (Unity units).")]
    [Min(0f)] public float maxRadialJitter = 0.4f;

    [Tooltip("Each member's yaw deviates from 'facing the cluster centre' by up to this many degrees.")]
    [Range(0f, 15f)] public float maxYawJitterDegrees = 3f;

    [Tooltip("Allowed difference between the placed base's horizontal forward (after terrain alignment) and its intended heading (degrees).")]
    [Min(0f)] public float facingToleranceDegrees = 1.5f;

    [Tooltip("Candidate cluster poses tried before swapping in fresh members.")]
    [Min(1)] public int attemptsPerCluster = 6000;

    [Tooltip("Times a cluster may swap in fresh members after exhausting its attempts.")]
    [Min(0)] public int maxMemberSubstitutions = 3;

    [Header("Approach reservation (separate from the facing-pair corridor settings)")]
    [Tooltip("Reserve each member's frontal approach strip (front attack position + analysed extra approach). Needs footprintAwareSpacing.")]
    public bool reserveApproaches = true;

    [Tooltip("PLACEHOLDER, not a troop-clearance value. Full width of each cluster approach strip (Unity units). Replace once troop dimensions are known.")]
    [Min(0.1f)] public float approachWidth = 8f;
}

[Serializable]
public sealed class ClusterMemberRecord
{
    public string baseId;
    public string baseType;
    public BaseLevel level;
    public int slotIndex;
    /// <summary>Seated root position (range origin / base centre).</summary>
    public Vector3 position;
    /// <summary>Exact world XZ passed to the placement checks.</summary>
    public Vector3 placementInputXZ;
    public float yawDegrees;
    public float forwardHeadingDegrees;
    public float intendedHeadingDegrees;
    public float yawJitterDegrees;
    public float facingErrorDegrees;
    /// <summary>|forward - direction to the cluster centre| (horizontal).</summary>
    public float angleToCentreDegrees;
    /// <summary>Slot angle around the centre (0 = +Z) and horizontal distance from the centre.</summary>
    public float slotAngleDegrees;
    public float radius;
    public Vector3 frontAttackPosition;
}

/// <summary>One supporter evaluated against one member's front attack position and approach.</summary>
[Serializable]
public sealed class ClusterSupport
{
    public string supporterBaseId;
    public BaseLevel supporterLevel;
    public float distanceToFront;
    public RangeBandStatus statusAtFront;
    public float bandMinimum;
    public float bandMaximum;
    public List<ApproachInterval> coverage = new List<ApproachInterval>();
    public float coveredLength;
    public bool EngagesAtFront => statusAtFront == RangeBandStatus.InBand;
}

/// <summary>Number of supporters engaging along one stretch of a member's approach.</summary>
[Serializable]
public struct ApproachSupportSpan
{
    public float start;
    public float end;
    public int supporters;
    public float Length => end - start;
}

[Serializable]
public sealed class ClusterTarget
{
    public string defendedBaseId;
    public Vector3 frontAttackPosition;
    public List<ClusterSupport> candidates = new List<ClusterSupport>();
    /// <summary>Other members inside min and max range of the front attack position.</summary>
    public int supportersAtFront;
    /// <summary>Exact partition of the analysed approach (0..maxExtra) by supporter count.</summary>
    public List<ApproachSupportSpan> profile = new List<ApproachSupportSpan>();
    public float lengthWithAtLeastOne;
    public float lengthWithAtLeastTwo;
    public float lengthUncovered;
    public float analysedLength;
}

/// <summary>Result of one placed cluster (Stage 2c).</summary>
[Serializable]
public sealed class ClusterRecord
{
    public string formationId;
    public List<ClusterMemberRecord> members = new List<ClusterMemberRecord>();
    /// <summary>Sampled centre (XZ) with y = mean member height.</summary>
    public Vector3 centre;
    public float requestedRadius;
    public float radiusWindowMinimum;
    public float radiusWindowMaximum;
    public float rotationDegrees;
    /// <summary>Intersection of all member bands [max min, min max] (Unity units).</summary>
    public float sharedBandMinimum;
    public float sharedBandMaximum;
    public List<ClusterTarget> targets = new List<ClusterTarget>();
    public int minimumSupportersAtFront;
    public List<FormationCorridorSegment> approachStrips = new List<FormationCorridorSegment>();
    public int attempts;
    public int memberSubstitutions;
    public string rejectionSummary;
}

/// <summary>
/// Deterministic inward-facing cluster selection, geometry and crossfire
/// evaluation (Stage 2c). Selection and pose sampling use private SplitMix64
/// streams; UnityEngine.Random is never used. Placement (real checks,
/// reservation, instantiation) is done by BaseSpawner.
/// </summary>
public static class ClusterFormations
{
    public const string SelectionSalt = "IRONVEIL.Clusters.Select.v1";
    public const string PlacementSalt = "IRONVEIL.Clusters.Place.v1";

    /// <summary>Intersection of the bands of all levels; false when empty.</summary>
    public static bool TryGetSharedBand(
        IReadOnlyList<BaseLevel> levels, FlakRangeTable ranges, out float minimum, out float maximum)
    {
        minimum = float.MinValue;
        maximum = float.MaxValue;
        if (ranges == null || levels == null || levels.Count == 0) return false;
        foreach (BaseLevel level in levels)
        {
            if (!ranges.TryGetRangeUnity(level, out FlakRangeBand band)) return false;
            minimum = Mathf.Max(minimum, band.Minimum);
            maximum = Mathf.Min(maximum, band.Maximum);
        }

        return minimum <= maximum;
    }

    /// <summary>
    /// Flat-plane distance from a member on a circle of radius r to the front
    /// attack position of another member separated by angle theta, both facing
    /// the centre exactly.
    /// </summary>
    public static float FlatSupportDistance(float radius, float thetaDegrees, float frontDistance)
    {
        float b = radius - frontDistance;
        float c = Mathf.Cos(thetaDegrees * Mathf.Deg2Rad);
        return Mathf.Sqrt(Mathf.Max(0f, radius * radius + b * b - 2f * radius * b * c));
    }

    /// <summary>
    /// Radius window where adjacent members (separation 360/n) engage each
    /// other's fronts inside [bandMin, bandMax] on a flat plane. The distance
    /// grows monotonically with the radius for n = 3 and 4 (radius > front distance).
    /// </summary>
    public static bool TryGetRadiusWindow(
        int memberCount, float bandMin, float bandMax, float frontDistance,
        out float minimum, out float maximum)
    {
        minimum = maximum = 0f;
        if (memberCount < 3 || bandMin > bandMax) return false;
        float theta = 360f / memberCount;
        float lo = frontDistance + 1e-3f;
        float hi = frontDistance + 4f * bandMax + 10f;
        if (FlatSupportDistance(lo, theta, frontDistance) > bandMax) return false;
        minimum = SolveRadius(theta, bandMin, frontDistance, lo, hi);
        maximum = SolveRadius(theta, bandMax, frontDistance, lo, hi);
        return minimum <= maximum;
    }

    private static float SolveRadius(float theta, float target, float front, float lo, float hi)
    {
        if (FlatSupportDistance(lo, theta, front) >= target) return lo;
        for (int i = 0; i < 80; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (FlatSupportDistance(mid, theta, front) < target) lo = mid; else hi = mid;
        }

        return 0.5f * (lo + hi);
    }

    /// <summary>
    /// Chooses the cluster count and per-cluster member counts, then a
    /// deterministic ordering of eligible candidates not in excludedIds.
    /// Input order never matters (sorted by ID first; duplicate IDs collapse).
    /// </summary>
    public static List<FormationCandidate> SelectPool(
        int seed,
        IEnumerable<FormationCandidate> candidates,
        FormationPlannerSettings eligibility,
        ClusterSettings settings,
        ICollection<string> excludedIds,
        out List<int> memberCounts,
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
            string reason;
            if (excludedIds != null && excludedIds.Contains(c.baseId))
            {
                reason = "already a facing-pair member";
            }
            else if (StrategicFormationPlanner.IsEligible(c, eligibility, out reason))
            {
                pool.Add(c);
                continue;
            }

            if (exclusions != null) exclusions[reason] = exclusions.TryGetValue(reason, out int n) ? n + 1 : 1;
        }

        FormationRandom rng = new FormationRandom(seed, SelectionSalt);
        int cLo = Mathf.Min(settings.minClusters, settings.maxClusters);
        int cHi = Mathf.Max(settings.minClusters, settings.maxClusters);
        int mLo = Mathf.Clamp(Mathf.Min(settings.minMembers, settings.maxMembers), 3, 4);
        int mHi = Mathf.Clamp(Mathf.Max(settings.minMembers, settings.maxMembers), 3, 4);
        int clusterCount = rng.RangeInclusive(cLo, cHi);
        memberCounts = new List<int>(clusterCount);
        for (int i = 0; i < clusterCount; i++) memberCounts.Add(rng.RangeInclusive(mLo, mHi));

        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = (int)(rng.NextUInt64() % (ulong)(i + 1));
            FormationCandidate t = pool[i];
            pool[i] = pool[j];
            pool[j] = t;
        }

        return pool;
    }

    /// <summary>
    /// Crossfire evaluation of placed members: for each member's front attack
    /// position, every OTHER member is tested (full 3D, min and max inclusive)
    /// and its exact approach coverage computed. A base never supports itself.
    /// </summary>
    public static List<ClusterTarget> EvaluateSupport(
        IReadOnlyList<CrossfireBase> members, FlakRangeTable ranges, CrossfireAttackModel model)
    {
        List<ClusterTarget> targets = new List<ClusterTarget>();
        for (int i = 0; i < members.Count; i++)
        {
            CrossfireBase defended = members[i];
            ClusterTarget target = new ClusterTarget
            {
                defendedBaseId = defended.baseId,
                frontAttackPosition = CrossfireGeometry.FrontAttackPosition(defended, model, 0f),
                analysedLength = model.MaxExtraApproach
            };

            List<List<ApproachInterval>> covers = new List<List<ApproachInterval>>();
            for (int j = 0; j < members.Count; j++)
            {
                if (j == i) continue;
                CrossfireBase supporter = members[j];
                EngagementTest test = CrossfireGeometry.Evaluate(supporter, target.frontAttackPosition, ranges);
                ClusterSupport s = new ClusterSupport
                {
                    supporterBaseId = supporter.baseId,
                    supporterLevel = supporter.level,
                    distanceToFront = test.distance,
                    statusAtFront = test.status,
                    bandMinimum = test.band.Minimum,
                    bandMaximum = test.band.Maximum,
                    coverage = CrossfireGeometry.ApproachCoverage(supporter, defended, ranges, model)
                };
                foreach (ApproachInterval iv in s.coverage) s.coveredLength += Mathf.Max(0f, iv.Length);
                if (s.EngagesAtFront) target.supportersAtFront++;
                target.candidates.Add(s);
                covers.Add(s.coverage);
            }

            target.profile = SupportProfile(covers, model.MaxExtraApproach);
            foreach (ApproachSupportSpan span in target.profile)
            {
                if (span.supporters >= 1) target.lengthWithAtLeastOne += span.Length;
                if (span.supporters >= 2) target.lengthWithAtLeastTwo += span.Length;
                if (span.supporters == 0) target.lengthUncovered += span.Length;
            }

            targets.Add(target);
        }

        return targets;
    }

    /// <summary>Exact partition of [0, maxExtra] by the number of covering interval lists.</summary>
    public static List<ApproachSupportSpan> SupportProfile(IReadOnlyList<List<ApproachInterval>> covers, float maxExtra)
    {
        List<float> cuts = new List<float> { 0f, maxExtra };
        foreach (List<ApproachInterval> c in covers)
            foreach (ApproachInterval iv in c)
            {
                cuts.Add(Mathf.Clamp(iv.start, 0f, maxExtra));
                cuts.Add(Mathf.Clamp(iv.end, 0f, maxExtra));
            }

        cuts.Sort();
        List<ApproachSupportSpan> spans = new List<ApproachSupportSpan>();
        for (int k = 0; k + 1 < cuts.Count; k++)
        {
            float a = cuts[k], b = cuts[k + 1];
            if (b - a <= 1e-5f) continue;
            float mid = 0.5f * (a + b);
            int count = 0;
            foreach (List<ApproachInterval> c in covers)
                foreach (ApproachInterval iv in c)
                    if (mid >= iv.start && mid <= iv.end) { count++; break; }

            if (spans.Count > 0 && spans[spans.Count - 1].supporters == count && Mathf.Abs(spans[spans.Count - 1].end - a) < 1e-5f)
            {
                ApproachSupportSpan last = spans[spans.Count - 1];
                last.end = b;
                spans[spans.Count - 1] = last;
            }
            else
            {
                spans.Add(new ApproachSupportSpan { start = a, end = b, supporters = count });
            }
        }

        return spans;
    }

    /// <summary>
    /// One approach strip per member: from its front attack position along its
    /// own forward for the analysed extra approach. Nothing behind or beside a
    /// member is reserved.
    /// </summary>
    public static List<FormationCorridorSegment> BuildApproachStrips(
        string formationId, IReadOnlyList<ClusterMemberRecord> members, ClusterSettings settings, CrossfireAttackModel model)
    {
        List<FormationCorridorSegment> strips = new List<FormationCorridorSegment>();
        if (settings == null || !settings.reserveApproaches || model == null || model.MaxExtraApproach <= 1e-4f) return strips;
        foreach (ClusterMemberRecord m in members)
        {
            Vector3 front = new Vector3(m.frontAttackPosition.x, 0f, m.frontAttackPosition.z);
            Vector3 dir = m.frontAttackPosition - m.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f) continue;
            strips.Add(new FormationCorridorSegment
            {
                formationId = formationId,
                kind = "approach " + m.baseId,
                start = front,
                end = front + dir.normalized * model.MaxExtraApproach,
                width = settings.approachWidth
            });
        }

        return strips;
    }

    public static string BuildReport(
        int seed, float scToUnity, CrossfireAttackModel model, ClusterSettings settings,
        IReadOnlyList<int> requestedMembers, int eligibleCount, IReadOnlyList<ClusterRecord> clusters,
        IReadOnlyDictionary<string, int> exclusions, IReadOnlyList<string> unfilledReasons)
    {
        CultureInfo ic = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)");
        sb.AppendFormat(ic, "seed {0} | S = {1:0.###} | front attack {2:0.##} | extra approach analysed {3:0.##} | min supporters per front {4} | approach strip width {5:0.##} (placeholder)\n",
            seed, scToUnity, model.FrontAttackDistance, model.MaxExtraApproach, settings.minSupportersPerFront, settings.approachWidth);
        sb.AppendFormat(ic, "requested clusters {0} (members {1}) | placed {2} | unfilled {3} | eligible L5/L6 land Heli/Air {4}\n",
            requestedMembers.Count, string.Join("/", requestedMembers), clusters.Count, unfilledReasons.Count, eligibleCount);
        if (exclusions != null)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in exclusions) parts.Add(kv.Key + " " + kv.Value);
            sb.AppendLine("excluded: " + string.Join(", ", parts));
        }

        foreach (string u in unfilledReasons) sb.AppendLine("UNFILLED: " + u);

        foreach (ClusterRecord c in clusters)
        {
            sb.AppendLine();
            sb.AppendFormat(ic, "{0}  {1} members  centre ({2:0.00}, {3:0.00}, {4:0.00})  radius {5:0.00} (window {6:0.00}-{7:0.00})  rotation {8:0.0}  shared band [{9:0.##}, {10:0.##}]  attempts {11}  substitutions {12}\n",
                c.formationId, c.members.Count, c.centre.x, c.centre.y, c.centre.z, c.requestedRadius,
                c.radiusWindowMinimum, c.radiusWindowMaximum, c.rotationDegrees, c.sharedBandMinimum, c.sharedBandMaximum,
                c.attempts, c.memberSubstitutions);
            foreach (ClusterMemberRecord m in c.members)
            {
                sb.AppendFormat(ic,
                    "  [{0}] {1,-14} {2,-4} {3}  pos ({4:0.00}, {5:0.00}, {6:0.00})  r {7:0.00}  slot {8:0.0}  fwd {9:0.0}  intended {10:0.0} (jitter {11:+0.0;-0.0})  facing err {12:0.00}  to centre {13:0.0}\n",
                    m.slotIndex, m.baseId, m.baseType, m.level, m.position.x, m.position.y, m.position.z, m.radius,
                    m.slotAngleDegrees, m.forwardHeadingDegrees, m.intendedHeadingDegrees, m.yawJitterDegrees,
                    m.facingErrorDegrees, m.angleToCentreDegrees);
            }

            foreach (ClusterTarget t in c.targets)
            {
                sb.AppendFormat(ic, "  front of {0} ({1:0.00}, {2:0.00}, {3:0.00}): {4} supporter(s) at the front position\n",
                    t.defendedBaseId, t.frontAttackPosition.x, t.frontAttackPosition.y, t.frontAttackPosition.z, t.supportersAtFront);
                foreach (ClusterSupport s in t.candidates)
                {
                    List<string> iv = new List<string>();
                    foreach (ApproachInterval i in s.coverage) iv.Add(i.ToString());
                    sb.AppendFormat(ic, "    {0} {1}: 3D {2:0.00} in [{3:0.##}, {4:0.##}] {5}; approach coverage {6} = {7:0.00}/{8:0.##}\n",
                        s.supporterBaseId, s.supporterLevel, s.distanceToFront, s.bandMinimum, s.bandMaximum, s.statusAtFront,
                        iv.Count == 0 ? "none" : string.Join(" ", iv), s.coveredLength, t.analysedLength);
                }

                List<string> prof = new List<string>();
                foreach (ApproachSupportSpan sp in t.profile) prof.Add(string.Format(ic, "[{0:0.###}, {1:0.###}] x{2}", sp.start, sp.end, sp.supporters));
                sb.AppendFormat(ic, "    approach profile (0 = front position, {0:0.##} = end): {1}\n", t.analysedLength, string.Join(" ", prof));
                sb.AppendFormat(ic, "    covered by >=1: {0:0.00}/{3:0.##}  by >=2: {1:0.00}/{3:0.##}  OUTSIDE every engagement band: {2:0.00}/{3:0.##} (partial)\n",
                    t.lengthWithAtLeastOne, t.lengthWithAtLeastTwo, t.lengthUncovered, t.analysedLength);
            }

            if (c.approachStrips.Count > 0)
            {
                List<string> segs = new List<string>();
                foreach (FormationCorridorSegment s in c.approachStrips)
                    segs.Add(string.Format(ic, "{0} {1:0.00}x{2:0.##} hdg {3:0.0}", s.kind, s.Length, s.width, s.HeadingDegrees));
                sb.AppendLine("  reserved approach strips: " + string.Join("; ", segs));
            }
            else
            {
                sb.AppendLine("  reserved approach strips: none");
            }

            sb.AppendLine("  rejected candidate poses: " + (c.rejectionSummary ?? "none"));
        }

        return sb.ToString();
    }
}
