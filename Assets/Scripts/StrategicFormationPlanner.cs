using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Kind of strategic formation.</summary>
public enum FormationKind
{
    /// <summary>Two bases facing each other across a shared attack corridor.</summary>
    FacingPair = 0,
    /// <summary>Three or four bases arranged to support one another.</summary>
    Cluster = 1
}

/// <summary>Member role inside a formation.</summary>
public enum FormationRole
{
    /// <summary>Slot 0; the formation frame is centred on the formation, oriented by the anchor's slot.</summary>
    Anchor = 0,
    /// <summary>The other base of a facing pair.</summary>
    Partner = 1,
    /// <summary>Any non-anchor cluster member.</summary>
    Supporter = 2
}

/// <summary>How cluster members are laid out and oriented in the formation frame.</summary>
public enum ClusterArrangement
{
    /// <summary>Members on a circle, each facing the centre (attacks enter the middle).</summary>
    RadialInward = 0,
    /// <summary>Members on a circle, each facing away from the centre.</summary>
    RadialOutward = 1,
    /// <summary>Members side by side along local X, all facing local +Z (one shared approach).</summary>
    LineAbreast = 2
}

/// <summary>Planner input: one base known before placement.</summary>
public struct FormationCandidate
{
    public string baseId;
    public string baseType;
    public BaseLevel level;
    public bool hasLevel;
    /// <summary>Naval or rail (hangar on a route). Excluded from formations in Stage 2a.</summary>
    public bool isRouteBase;
    /// <summary>False for bases without a defined defensive front (Tank).</summary>
    public bool hasDefensiveFront;
}

/// <summary>One member slot. Offsets/yaw are in the formation frame (Unity units / degrees).</summary>
[Serializable]
public sealed class FormationMember
{
    public string baseId;
    public string baseType;
    public BaseLevel level;
    public FormationRole role;
    public int slotIndex;
    /// <summary>Slot centre relative to the formation origin (local XZ, Unity units). Zero when geometry is unresolved.</summary>
    public Vector2 localOffset;
    /// <summary>Member yaw relative to the formation frame (0 = local +Z).</summary>
    public float localYawDegrees;
    /// <summary>Direction troops are expected to come from: the member's own forward (local, horizontal).</summary>
    public Vector3 IntendedApproachLocal => CrossfireGeometry.ForwardFromYaw(localYawDegrees);
}

/// <summary>Expected support: "supporter could engage troops attacking defended from its front".</summary>
[Serializable]
public sealed class FormationSupportLink
{
    public string supporterBaseId;
    public string defendedBaseId;
    /// <summary>Supporter can engage at the initial front attack position (extra approach 0).</summary>
    public bool engagesAtFrontPosition;
    /// <summary>Extra-approach intervals (0..maxExtra, Unity units) where the supporter can engage.</summary>
    public List<ApproachInterval> coverage = new List<ApproachInterval>();
    /// <summary>Distance supporter centre -> initial front attack position (Unity units, formation plane).</summary>
    public float distanceToFrontPosition;
    public RangeBandStatus statusAtFrontPosition;
}

/// <summary>A planned formation (membership, roles, orientation, expected support). Not placed.</summary>
[Serializable]
public sealed class FormationSpec
{
    public string formationId;
    public FormationKind kind;
    public ClusterArrangement arrangement;
    /// <summary>Neighbour centre-to-centre spacing used for the slot layout (Unity units); 0 = unresolved.</summary>
    public float spacingUnity;
    public bool geometryResolved;
    public List<FormationMember> members = new List<FormationMember>();
    public List<FormationSupportLink> supportLinks = new List<FormationSupportLink>();
}

/// <summary>Planner output.</summary>
[Serializable]
public sealed class FormationPlan
{
    public const string AlgorithmVersion = "IRONVEIL.Formations.v1";

    public int seed;
    public float scToUnity;
    public List<FormationSpec> formations = new List<FormationSpec>();
    public int eligibleCount;
    /// <summary>Ineligible candidates by reason (deterministically ordered).</summary>
    public SortedDictionary<string, int> exclusions =
        new SortedDictionary<string, int>(StringComparer.Ordinal);
    /// <summary>Requested formations that could not be filled from eligible bases.</summary>
    public int unfilledFormations;

    public bool Contains(string baseId)
    {
        foreach (FormationSpec f in formations)
            foreach (FormationMember m in f.members)
                if (m.baseId == baseId) return true;
        return false;
    }
}

/// <summary>Planner settings. Counts default to 0 and spacings to 0 (= geometry unresolved).</summary>
[Serializable]
public sealed class FormationPlannerSettings
{
    [Min(0)] public int facingPairs = 0;
    [Min(0)] public int clustersOfThree = 0;
    [Min(0)] public int clustersOfFour = 0;

    [Tooltip("Levels eligible for formations.")]
    public BaseLevel[] eligibleLevels = { BaseLevel.L5, BaseLevel.L6 };

    [Tooltip("Facing-pair centre-to-centre distance (Unity units). 0 = unresolved (Stage 2b decision).")]
    [Min(0f)] public float facingPairSpacing = 0f;

    [Tooltip("Cluster neighbour centre-to-centre distance (Unity units). 0 = unresolved (Stage 2b decision).")]
    [Min(0f)] public float clusterSpacing = 0f;

    public ClusterArrangement clusterArrangement = ClusterArrangement.RadialInward;
}

/// <summary>
/// Deterministic strategic-formation planner (Stage 2a foundation).
///
/// Chooses which bases form facing pairs and 3-4 base clusters, assigns roles,
/// slot offsets, member yaw, intended approach and the expected supporting
/// relationships (via <see cref="CrossfireGeometry"/>). It does not place,
/// move or rotate anything.
///
/// Determinism: a pure function of (seed, candidate IDs/data, settings, range
/// table, attack model). Candidates are sorted ordinally by base ID and
/// shuffled with a private SplitMix64 stream salted with
/// <see cref="FormationPlan.AlgorithmVersion"/>, mirroring
/// BaseLevelAssignment. UnityEngine.Random is never used, so terrain, routes,
/// levels and ordinary placement are unaffected.
///
/// Exclusions (Stage 2a decisions): bases without a defensive front (Tank),
/// route bases (naval/rail, which keep their route-facing orientation),
/// bases without a level, and levels outside settings.eligibleLevels.
/// </summary>
public static class StrategicFormationPlanner
{
    public const string ReasonNoLevel = "no level assigned";
    public const string ReasonNoFront = "no defensive front (Tank)";
    public const string ReasonRouteBase = "route base (naval/rail)";
    public const string ReasonLevel = "level not eligible";

    public static bool IsEligible(
        FormationCandidate candidate, FormationPlannerSettings settings, out string reason)
    {
        reason = null;
        if (!candidate.hasLevel) { reason = ReasonNoLevel; return false; }
        if (!candidate.hasDefensiveFront) { reason = ReasonNoFront; return false; }
        if (candidate.isRouteBase) { reason = ReasonRouteBase; return false; }
        if (settings == null || settings.eligibleLevels == null ||
            Array.IndexOf(settings.eligibleLevels, candidate.level) < 0)
        {
            reason = ReasonLevel;
            return false;
        }

        return true;
    }

    public static FormationPlan Plan(
        int seed,
        IEnumerable<FormationCandidate> candidates,
        FormationPlannerSettings settings,
        FlakRangeTable ranges,
        CrossfireAttackModel attackModel)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (ranges == null) throw new ArgumentNullException(nameof(ranges));
        if (attackModel == null) throw new ArgumentNullException(nameof(attackModel));

        FormationPlan plan = new FormationPlan { seed = seed, scToUnity = ranges.ScToUnity };

        // Deduplicate by ID and sort ordinally: input order never matters.
        SortedDictionary<string, FormationCandidate> byId =
            new SortedDictionary<string, FormationCandidate>(StringComparer.Ordinal);
        foreach (FormationCandidate c in candidates)
        {
            if (!string.IsNullOrEmpty(c.baseId)) byId[c.baseId] = c;
        }

        List<FormationCandidate> eligible = new List<FormationCandidate>();
        foreach (FormationCandidate c in byId.Values)
        {
            if (IsEligible(c, settings, out string reason))
            {
                eligible.Add(c);
            }
            else
            {
                plan.exclusions[reason] = plan.exclusions.TryGetValue(reason, out int n) ? n + 1 : 1;
            }
        }

        plan.eligibleCount = eligible.Count;

        ulong state = MixSeed(seed);
        for (int i = eligible.Count - 1; i > 0; i--)
        {
            int j = (int)(NextUInt64(ref state) % (ulong)(i + 1));
            FormationCandidate t = eligible[i];
            eligible[i] = eligible[j];
            eligible[j] = t;
        }

        int cursor = 0;
        int pairNo = 0, c3No = 0, c4No = 0;

        // Fixed fill order: pairs, then 4-clusters, then 3-clusters.
        for (int k = 0; k < settings.facingPairs; k++)
        {
            if (!Take(eligible, ref cursor, 2, out List<FormationCandidate> m)) { plan.unfilledFormations++; continue; }
            plan.formations.Add(BuildPair($"FORM_PAIR_{++pairNo:000}", m, settings, ranges, attackModel));
        }

        for (int k = 0; k < settings.clustersOfFour; k++)
        {
            if (!Take(eligible, ref cursor, 4, out List<FormationCandidate> m)) { plan.unfilledFormations++; continue; }
            plan.formations.Add(BuildCluster($"FORM_CL4_{++c4No:000}", m, settings, ranges, attackModel));
        }

        for (int k = 0; k < settings.clustersOfThree; k++)
        {
            if (!Take(eligible, ref cursor, 3, out List<FormationCandidate> m)) { plan.unfilledFormations++; continue; }
            plan.formations.Add(BuildCluster($"FORM_CL3_{++c3No:000}", m, settings, ranges, attackModel));
        }

        return plan;
    }

    /// <summary>
    /// Slot layout for a formation of <paramref name="memberCount"/> members.
    /// Pair: members at (0, -d/2) yaw 0 and (0, +d/2) yaw 180 (facing each other).
    /// Radial: circle with neighbour chord d; LineAbreast: x = (i - (n-1)/2) d, yaw 0.
    /// </summary>
    public static void SlotPose(
        FormationKind kind, ClusterArrangement arrangement, int memberCount, int slot,
        float spacing, out Vector2 offset, out float yawDegrees)
    {
        if (kind == FormationKind.FacingPair)
        {
            offset = new Vector2(0f, slot == 0 ? -spacing * 0.5f : spacing * 0.5f);
            yawDegrees = slot == 0 ? 0f : 180f;
            return;
        }

        if (arrangement == ClusterArrangement.LineAbreast)
        {
            offset = new Vector2((slot - (memberCount - 1) * 0.5f) * spacing, 0f);
            yawDegrees = 0f;
            return;
        }

        float bearing = 360f * slot / memberCount;               // 0 = local +Z
        float radius = spacing / (2f * Mathf.Sin(Mathf.PI / memberCount));
        Vector3 dir = CrossfireGeometry.ForwardFromYaw(bearing);
        offset = new Vector2(dir.x, dir.z) * radius;
        yawDegrees = arrangement == ClusterArrangement.RadialOutward
            ? bearing
            : Mathf.Repeat(bearing + 180f, 360f);
    }

    private static FormationSpec BuildPair(
        string id, List<FormationCandidate> members, FormationPlannerSettings s,
        FlakRangeTable ranges, CrossfireAttackModel model)
    {
        FormationSpec spec = new FormationSpec
        {
            formationId = id,
            kind = FormationKind.FacingPair,
            arrangement = s.clusterArrangement,
            spacingUnity = s.facingPairSpacing,
            geometryResolved = s.facingPairSpacing > 0f
        };
        FillMembers(spec, members, FormationRole.Partner);
        ComputeSupport(spec, ranges, model);
        return spec;
    }

    private static FormationSpec BuildCluster(
        string id, List<FormationCandidate> members, FormationPlannerSettings s,
        FlakRangeTable ranges, CrossfireAttackModel model)
    {
        FormationSpec spec = new FormationSpec
        {
            formationId = id,
            kind = FormationKind.Cluster,
            arrangement = s.clusterArrangement,
            spacingUnity = s.clusterSpacing,
            geometryResolved = s.clusterSpacing > 0f
        };
        FillMembers(spec, members, FormationRole.Supporter);
        ComputeSupport(spec, ranges, model);
        return spec;
    }

    private static void FillMembers(FormationSpec spec, List<FormationCandidate> members, FormationRole otherRole)
    {
        for (int i = 0; i < members.Count; i++)
        {
            Vector2 offset = Vector2.zero;
            float yaw;
            SlotPose(spec.kind, spec.arrangement, members.Count, i,
                spec.geometryResolved ? spec.spacingUnity : 0f, out offset, out yaw);
            if (!spec.geometryResolved) offset = Vector2.zero;

            spec.members.Add(new FormationMember
            {
                baseId = members[i].baseId,
                baseType = members[i].baseType,
                level = members[i].level,
                role = i == 0 ? FormationRole.Anchor : otherRole,
                slotIndex = i,
                localOffset = offset,
                localYawDegrees = yaw
            });
        }
    }

    // Expected supporting relationships, evaluated on the flat formation plane.
    private static void ComputeSupport(FormationSpec spec, FlakRangeTable ranges, CrossfireAttackModel model)
    {
        if (!spec.geometryResolved) return;

        List<CrossfireBase> bases = new List<CrossfireBase>();
        foreach (FormationMember m in spec.members)
        {
            bases.Add(new CrossfireBase
            {
                baseId = m.baseId,
                baseType = m.baseType,
                level = m.level,
                centre = new Vector3(m.localOffset.x, 0f, m.localOffset.y),
                forward = m.IntendedApproachLocal,
                hasDefensiveFront = true
            });
        }

        foreach (CrossfireBase defended in bases)
        {
            Vector3 front = CrossfireGeometry.FrontAttackPosition(defended, model, 0f);
            foreach (CrossfireBase supporter in bases)
            {
                if (supporter.baseId == defended.baseId) continue;
                List<ApproachInterval> cov = CrossfireGeometry.ApproachCoverage(supporter, defended, ranges, model);
                if (cov.Count == 0) continue;
                EngagementTest atFront = CrossfireGeometry.Evaluate(supporter, front, ranges);
                spec.supportLinks.Add(new FormationSupportLink
                {
                    supporterBaseId = supporter.baseId,
                    defendedBaseId = defended.baseId,
                    engagesAtFrontPosition = atFront.CanEngage,
                    coverage = cov,
                    distanceToFrontPosition = atFront.distance,
                    statusAtFrontPosition = atFront.status
                });
            }
        }
    }

    private static bool Take(List<FormationCandidate> pool, ref int cursor, int n, out List<FormationCandidate> taken)
    {
        taken = null;
        if (cursor + n > pool.Count) return false;
        taken = pool.GetRange(cursor, n);
        cursor += n;
        return true;
    }

    private static ulong MixSeed(int seed)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            string salt = FormationPlan.AlgorithmVersion;
            for (int i = 0; i < salt.Length; i++) hash = (hash ^ salt[i]) * 1099511628211UL;
            return (hash ^ (uint)seed) * 1099511628211UL;
        }
    }

    private static ulong NextUInt64(ref ulong state)
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
}
