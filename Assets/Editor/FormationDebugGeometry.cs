using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Stage 2d (editor-only): converts the formation records BaseSpawner already
// produced (facing pairs, clusters, their engagement links, coverage
// intervals and reserved strips) into a cached list of draw commands. No
// geometry is re-derived with a different algorithm and nothing is written
// back to the spawner. Debug reference only: no weapon or combat system.

[System.Flags]
public enum FormationDebugLayer
{
    None = 0,
    Structure = 1,       // formation outline, pair line, cluster centre, member markers
    Facing = 2,          // member horizontal forward
    Attack = 4,          // front attack positions (+ supporter count)
    Supporters = 8,      // supporter -> target lines (and out-of-band candidates)
    Ranges = 16,         // min/max engagement boundaries (wire spheres)
    Coverage = 32,       // approach coverage by supporter count
    Reservation = 64,    // reserved corridor / approach strips
    Labels = 128         // text labels
}

public enum FormationDebugPrimitive { Line, Disc, Point, Quad, Label }

public struct FormationDebugCommand
{
    public FormationDebugPrimitive primitive;
    public FormationDebugLayer layer;
    /// <summary>Only drawn when the camera is within the detail distance (or detail is forced).</summary>
    public bool detailOnly;
    public Vector3 a;
    public Vector3 b;          // line end / disc normal
    public float radius;       // disc or point radius
    public float thickness;
    public bool dashed;
    public Color color;
    public Color fill;         // quad fill
    public Vector3[] quad;     // 4 corners (quad)
    public Vector3[] points;   // dashed disc: precomputed segment endpoint pairs (built once, not per frame)
    public string text;        // label
}

public sealed class FormationDebugEntry
{
    public string formationId;
    public FormationKind kind;
    public Vector3 centre;
    public float radius;
    public string[] memberIds;
    public string details;     // inspector text
    public string summary;     // one line for the list
    public bool allTargetsMeetRequirement;
    public readonly List<FormationDebugCommand> commands = new List<FormationDebugCommand>();
}

/// <summary>Per-target data exposed for tests and the inspector.</summary>
public sealed class FormationDebugTarget
{
    public string formationId;
    public string defendedBaseId;
    public Vector3 front;
    public Vector3 direction;
    public float analysedLength;
    public int supporters;
    public int required;
    public List<string> supporterIds = new List<string>();
    public List<ApproachSupportSpan> profile = new List<ApproachSupportSpan>();
}

public sealed class FormationDebugScene
{
    public readonly List<FormationDebugEntry> formations = new List<FormationDebugEntry>();
    public readonly List<FormationDebugTarget> targets = new List<FormationDebugTarget>();
    public int pairCount;
    public int clusterCount;
}

public static class FormationDebugGeometry
{
    public const int PairRequiredSupporters = 1;   // a pair has exactly one possible supporter: its partner
    public static readonly Color PairColor = new Color(1f, 0.25f, 0.85f);
    public static readonly Color ClusterColor = new Color(0.1f, 0.85f, 1f);
    public static readonly Color FacingColor = Color.white;
    public static readonly Color PassColor = new Color(0.15f, 1f, 0.3f);
    public static readonly Color FailColor = new Color(1f, 0.15f, 0.1f);
    public static readonly Color OneColor = new Color(1f, 0.85f, 0.1f);
    public static readonly Color MinRangeColor = new Color(1f, 0.55f, 0.1f);
    public static readonly Color MaxRangeColor = new Color(0.35f, 0.55f, 1f);
    public static readonly Color CandidateColor = new Color(0.6f, 0.25f, 0.25f);
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    const float Lift = 0.35f;   // draw strips/approaches slightly above the base-centre height

    public static FormationDebugScene Build(
        IReadOnlyList<FacingPairRecord> pairs,
        IReadOnlyList<ClusterRecord> clusters,
        FlakRangeTable ranges,
        CrossfireAttackModel model,
        int clusterRequiredSupporters)
    {
        FormationDebugScene scene = new FormationDebugScene();
        if (pairs != null) foreach (FacingPairRecord p in pairs) scene.formations.Add(BuildPair(p, ranges, model, scene));
        if (clusters != null) foreach (ClusterRecord c in clusters) scene.formations.Add(BuildCluster(c, ranges, model, clusterRequiredSupporters, scene));
        scene.pairCount = pairs?.Count ?? 0;
        scene.clusterCount = clusters?.Count ?? 0;
        return scene;
    }

    // ------------------------------------------------------------------ pairs
    static FormationDebugEntry BuildPair(FacingPairRecord p, FlakRangeTable ranges, CrossfireAttackModel model, FormationDebugScene scene)
    {
        FormationDebugEntry e = new FormationDebugEntry
        {
            formationId = p.formationId,
            kind = FormationKind.FacingPair,
            centre = (p.memberA.position + p.memberB.position) * 0.5f,
            radius = p.horizontalDistance * 0.5f + 12f,
            memberIds = new[] { p.memberA.baseId, p.memberB.baseId }
        };
        float y = e.centre.y + Lift;

        // structure + labels
        Line(e, FormationDebugLayer.Structure, p.memberA.position, p.memberB.position, PairColor, 3f, false, false);
        foreach (FacingPairMember m in new[] { p.memberA, p.memberB })
        {
            Disc(e, FormationDebugLayer.Structure, m.position, Vector3.up, 3.4f, PairColor, 2f, false, false);
            Label(e, m.position + Vector3.up * 4f, m.baseId + "  " + m.level + "  (" + p.formationId + (m == p.memberA ? " A)" : " B)"), false);
            AddFacing(e, m.position, m.frontAttackPosition, model);
            AddRanges(e, m.position, m.level, ranges);
        }

        Label(e, e.centre + Vector3.up * 9f, p.formationId + "  FACING PAIR  " + (p.mixedLevel ? "mixed" : "same-level") +
            string.Format(IC, "  spacing {0:0.0}", p.horizontalDistance), false);

        // each target: the partner is the only possible supporter
        foreach ((FacingPairMember target, FacingPairMember supporter, FacingPairLink link) in
                 new[] { (p.memberA, p.memberB, p.bSupportsA), (p.memberB, p.memberA, p.aSupportsB) })
        {
            int count = link.EngagesAtFront ? 1 : 0;
            FormationDebugTarget t = Target(scene, p.formationId, target.baseId, target.position, target.frontAttackPosition, model, count, PairRequiredSupporters);
            if (link.EngagesAtFront) t.supporterIds.Add(supporter.baseId);
            t.profile = ClusterFormations.SupportProfile(new List<List<ApproachInterval>> { link.coverage }, model.MaxExtraApproach);
            AddTarget(e, t, new[] { (supporter.position, supporter.baseId, link.EngagesAtFront, link.distanceToFront, link.bandMinimum, link.bandMaximum, link.statusAtFront) });
        }

        e.allTargetsMeetRequirement = p.bSupportsA.EngagesAtFront && p.aSupportsB.EngagesAtFront;
        foreach (FormationCorridorSegment s in p.corridor)
            AddStrip(e, s, y, s.kind == "axis" ? PairColor : Color.Lerp(PairColor, Color.white, 0.45f));

        e.summary = string.Format(IC, "{0}  pair  {1} {2} + {3} {4}  {5}", p.formationId, p.memberA.baseId, p.memberA.level,
            p.memberB.baseId, p.memberB.level, e.allTargetsMeetRequirement ? "both fronts supported" : "UNSUPPORTED FRONT");
        e.details = PairDetails(p, model);
        return e;
    }

    // ------------------------------------------------------------------ clusters
    static FormationDebugEntry BuildCluster(ClusterRecord c, FlakRangeTable ranges, CrossfireAttackModel model, int required, FormationDebugScene scene)
    {
        float maxR = 0f;
        foreach (ClusterMemberRecord m in c.members) maxR = Mathf.Max(maxR, m.radius);
        FormationDebugEntry e = new FormationDebugEntry
        {
            formationId = c.formationId,
            kind = FormationKind.Cluster,
            centre = c.centre,
            radius = maxR + 12f,
            memberIds = c.members.ConvertAll(m => m.baseId).ToArray()
        };
        float y = c.centre.y + Lift;
        string tag = "C" + c.formationId.Substring(c.formationId.Length - 3).TrimStart('0');

        // boundary polygon through the members, centre marker, labels
        for (int i = 0; i < c.members.Count; i++)
            Line(e, FormationDebugLayer.Structure, c.members[i].position, c.members[(i + 1) % c.members.Count].position, ClusterColor, 3f, true, false);
        Point(e, FormationDebugLayer.Structure, c.centre, 0.6f, ClusterColor, false);
        Label(e, c.centre + Vector3.up * 10f, string.Format(IC, "{0} [{1}]  CLUSTER  {2} members  r {3:0.0}  min support {4}/{5}",
            c.formationId, tag, c.members.Count, c.requestedRadius, c.minimumSupportersAtFront, required), false);
        foreach (ClusterMemberRecord m in c.members)
        {
            Disc(e, FormationDebugLayer.Structure, m.position, Vector3.up, 3.4f, ClusterColor, 2f, false, false);
            Label(e, m.position + Vector3.up * 4f, m.baseId + "  " + m.level + "  [" + tag + "#" + m.slotIndex + "]", false);
            AddFacing(e, m.position, m.frontAttackPosition, model);
            AddRanges(e, m.position, m.level, ranges);
        }

        bool all = true;
        foreach (ClusterTarget ct in c.targets)
        {
            ClusterMemberRecord target = c.members.Find(m => m.baseId == ct.defendedBaseId);
            FormationDebugTarget t = Target(scene, c.formationId, ct.defendedBaseId, target.position, ct.frontAttackPosition, model, ct.supportersAtFront, required);
            var sups = new List<(Vector3, string, bool, float, float, float, RangeBandStatus)>();
            foreach (ClusterSupport s in ct.candidates)
            {
                if (s.EngagesAtFront) t.supporterIds.Add(s.supporterBaseId);
                sups.Add((c.members.Find(m => m.baseId == s.supporterBaseId).position, s.supporterBaseId, s.EngagesAtFront, s.distanceToFront, s.bandMinimum, s.bandMaximum, s.statusAtFront));
            }

            t.profile = ct.profile;
            AddTarget(e, t, sups.ToArray());
            all &= ct.supportersAtFront >= required;
        }

        e.allTargetsMeetRequirement = all;
        foreach (FormationCorridorSegment s in c.approachStrips) AddStrip(e, s, y, Color.Lerp(ClusterColor, OneColor, 0.55f));

        e.summary = string.Format(IC, "{0}  cluster  {1} members  min support {2}  {3}", c.formationId, c.members.Count,
            c.minimumSupportersAtFront, all ? "all fronts >= " + required : "FRONT BELOW " + required);
        e.details = ClusterDetails(c, model, required);
        return e;
    }

    // ------------------------------------------------------------------ shared pieces
    static FormationDebugTarget Target(FormationDebugScene scene, string id, string defended, Vector3 basePos, Vector3 front, CrossfireAttackModel model, int count, int required)
    {
        Vector3 dir = front - basePos; dir.y = 0f;
        FormationDebugTarget t = new FormationDebugTarget
        {
            formationId = id, defendedBaseId = defended, front = front,
            direction = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.forward,
            analysedLength = model.MaxExtraApproach, supporters = count, required = required
        };
        scene.targets.Add(t);
        return t;
    }

    static void AddFacing(FormationDebugEntry e, Vector3 pos, Vector3 front, CrossfireAttackModel model)
    {
        Vector3 dir = front - pos; dir.y = 0f; dir.Normalize();
        Vector3 tip = pos + dir * (model.FrontAttackDistance + 1.2f);
        Line(e, FormationDebugLayer.Facing, pos, tip, FacingColor, 3f, false, false);
        Vector3 side = new Vector3(-dir.z, 0f, dir.x);
        Line(e, FormationDebugLayer.Facing, tip, tip - dir * 0.9f + side * 0.6f, FacingColor, 3f, false, false);
        Line(e, FormationDebugLayer.Facing, tip, tip - dir * 0.9f - side * 0.6f, FacingColor, 3f, false, false);
    }

    // Wire sphere = three orthogonal great circles, centred on the base centre (range origin), radius = band x S.
    static void AddRanges(FormationDebugEntry e, Vector3 pos, BaseLevel level, FlakRangeTable ranges)
    {
        if (ranges == null || !ranges.TryGetRangeUnity(level, out FlakRangeBand band)) return;
        foreach ((float r, Color col, float th, bool dash, string name) in new[] { (band.Minimum, MinRangeColor, 1.5f, true, "min"), (band.Maximum, MaxRangeColor, 2.5f, false, "max") })
        {
            Disc(e, FormationDebugLayer.Ranges, pos, Vector3.up, r, col, th, dash, false);
            Disc(e, FormationDebugLayer.Ranges, pos, Vector3.right, r, col, th, dash, true);
            Disc(e, FormationDebugLayer.Ranges, pos, Vector3.forward, r, col, th, dash, true);
            e.commands.Add(new FormationDebugCommand
            {
                primitive = FormationDebugPrimitive.Label, layer = FormationDebugLayer.Ranges | FormationDebugLayer.Labels, detailOnly = true,
                a = pos + new Vector3(r, 0.5f, 0f), color = col, text = string.Format(IC, "{0} {1} {2:0.#}", level, name, r)
            });
        }
    }

    static void AddTarget(FormationDebugEntry e, FormationDebugTarget t,
        (Vector3 pos, string id, bool engages, float distance, float min, float max, RangeBandStatus status)[] candidates)
    {
        bool ok = t.supporters >= t.required;
        Color c = ok ? PassColor : FailColor;
        Point(e, FormationDebugLayer.Attack, t.front, 0.55f, c, false);
        e.commands.Add(new FormationDebugCommand
        {
            primitive = FormationDebugPrimitive.Label, layer = FormationDebugLayer.Attack | FormationDebugLayer.Labels, detailOnly = true,
            a = t.front + Vector3.up * 1.6f, color = c,
            text = (ok ? "OK " : "FAIL ") + t.supporters + " supporter" + (t.supporters == 1 ? "" : "s") + " (need " + t.required + ")  front of " + t.defendedBaseId
        });

        foreach (var s in candidates)
        {
            // 70% towards the target: the two reciprocal lines between neighbours label near different ends
            Vector3 mid = Vector3.Lerp(s.pos, t.front, 0.7f) + Vector3.up * 0.6f;
            if (s.engages)
            {
                Line(e, FormationDebugLayer.Supporters, s.pos, t.front, PassColor, 2.5f, false, false);
                e.commands.Add(new FormationDebugCommand
                {
                    primitive = FormationDebugPrimitive.Label, layer = FormationDebugLayer.Supporters | FormationDebugLayer.Labels, detailOnly = true,
                    a = mid, color = PassColor, text = string.Format(IC, "{0} -> front {1}: {2:0.00} in [{3:0.#}, {4:0.#}]", s.id, t.defendedBaseId, s.distance, s.min, s.max)
                });
            }
            else
            {
                Line(e, FormationDebugLayer.Supporters, s.pos, t.front, CandidateColor, 1f, true, true);
                // near the target end, so it never piles up at a cluster centre
                e.commands.Add(new FormationDebugCommand
                {
                    primitive = FormationDebugPrimitive.Label, layer = FormationDebugLayer.Supporters | FormationDebugLayer.Labels, detailOnly = true,
                    a = Vector3.Lerp(s.pos, t.front, 0.82f) + Vector3.up * 0.6f, color = CandidateColor, text = string.Format(IC, "{0}: {1:0.00} {2} [{3:0.#}, {4:0.#}] - not a supporter", s.id, s.distance, s.status, s.min, s.max)
                });
            }
        }

        // approach coverage from the recorded intervals: green >= 2, yellow 1, red 0 (uncovered)
        Vector3 lift = Vector3.up * Lift;
        float covered = 0f;
        foreach (ApproachSupportSpan span in t.profile)
        {
            Color col = span.supporters >= 2 ? PassColor : span.supporters == 1 ? OneColor : FailColor;
            Line(e, FormationDebugLayer.Coverage, t.front + t.direction * span.start + lift, t.front + t.direction * span.end + lift, col, span.supporters == 0 ? 3f : 6f, span.supporters == 0, false);
            if (span.supporters > 0) covered += span.Length;
            // boundary tick
            Vector3 side = new Vector3(-t.direction.z, 0f, t.direction.x) * 0.5f;
            Vector3 at = t.front + t.direction * span.end + lift;
            Line(e, FormationDebugLayer.Coverage, at - side, at + side, Color.white, 1.5f, false, true);
        }

        e.commands.Add(new FormationDebugCommand
        {
            primitive = FormationDebugPrimitive.Label, layer = FormationDebugLayer.Coverage | FormationDebugLayer.Labels, detailOnly = true,
            // midpoint of the approach, offset to its side (approach ends converge at a cluster centre)
            a = t.front + t.direction * (t.analysedLength * 0.5f) + new Vector3(-t.direction.z, 0f, t.direction.x) * 1.8f + lift + Vector3.up * 0.8f, color = covered >= t.analysedLength - 1e-3f ? PassColor : FailColor,
            text = string.Format(IC, "approach {0}: covered {1:0.00}/{2:0} ({3})", t.defendedBaseId, covered, t.analysedLength, covered >= t.analysedLength - 1e-3f ? "full" : "partial")
        });
    }

    // Reserved strip exactly as recorded (centre, width x length, heading) at the formation's height.
    static void AddStrip(FormationDebugEntry e, FormationCorridorSegment s, float y, Color col)
    {
        Quaternion q = Quaternion.Euler(0f, s.HeadingDegrees, 0f);
        Vector3 c = new Vector3(s.Center.x, y, s.Center.z);
        float hx = s.width * 0.5f, hz = s.Length * 0.5f;
        Vector3[] quad =
        {
            c + q * new Vector3(-hx, 0f, -hz), c + q * new Vector3(hx, 0f, -hz),
            c + q * new Vector3(hx, 0f, hz), c + q * new Vector3(-hx, 0f, hz)
        };
        e.commands.Add(new FormationDebugCommand
        {
            primitive = FormationDebugPrimitive.Quad, layer = FormationDebugLayer.Reservation, quad = quad,
            color = col, fill = new Color(col.r, col.g, col.b, 0.12f), thickness = 2f
        });
        e.commands.Add(new FormationDebugCommand
        {
            primitive = FormationDebugPrimitive.Label, layer = FormationDebugLayer.Reservation | FormationDebugLayer.Labels, detailOnly = true,
            a = c + Vector3.up * 0.4f, color = col, text = string.Format(IC, "{0} {1:0.0} x {2:0.#}", s.kind, s.Length, s.width)
        });
    }

    static void Line(FormationDebugEntry e, FormationDebugLayer layer, Vector3 a, Vector3 b, Color c, float th, bool dashed, bool detail) =>
        e.commands.Add(new FormationDebugCommand { primitive = FormationDebugPrimitive.Line, layer = layer, a = a, b = b, color = c, thickness = th, dashed = dashed, detailOnly = detail });
    static void Disc(FormationDebugEntry e, FormationDebugLayer layer, Vector3 centre, Vector3 normal, float r, Color c, float th, bool dashed, bool detail) =>
        e.commands.Add(new FormationDebugCommand { primitive = FormationDebugPrimitive.Disc, layer = layer, a = centre, b = normal, radius = r, color = c, thickness = th, dashed = dashed, detailOnly = detail, points = dashed ? CirclePairs(centre, normal, r, 96) : null });

    /// <summary>Endpoint pairs of an n-gon approximating the circle (for dotted drawing).</summary>
    public static Vector3[] CirclePairs(Vector3 centre, Vector3 normal, float r, int n)
    {
        Vector3 u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 v = Vector3.Cross(normal, u).normalized;
        Vector3[] pts = new Vector3[2 * n];
        for (int i = 0; i < n; i++)
        {
            float a0 = 2f * Mathf.PI * i / n, a1 = 2f * Mathf.PI * (i + 1) / n;
            pts[2 * i] = centre + (u * Mathf.Cos(a0) + v * Mathf.Sin(a0)) * r;
            pts[2 * i + 1] = centre + (u * Mathf.Cos(a1) + v * Mathf.Sin(a1)) * r;
        }

        return pts;
    }
    static void Point(FormationDebugEntry e, FormationDebugLayer layer, Vector3 p, float r, Color c, bool detail) =>
        e.commands.Add(new FormationDebugCommand { primitive = FormationDebugPrimitive.Point, layer = layer, a = p, radius = r, color = c, detailOnly = detail });
    static void Label(FormationDebugEntry e, Vector3 p, string text, bool detail) =>
        e.commands.Add(new FormationDebugCommand { primitive = FormationDebugPrimitive.Label, layer = FormationDebugLayer.Labels, a = p, color = Color.white, text = text, detailOnly = detail });

    // ------------------------------------------------------------------ inspector text
    static string V(Vector3 v) => string.Format(IC, "({0:0.00}, {1:0.00}, {2:0.00})", v.x, v.y, v.z);
    static string Iv(List<ApproachInterval> l) { if (l == null || l.Count == 0) return "none"; var p = new List<string>(); foreach (var i in l) p.Add(i.ToString()); return string.Join(" ", p); }
    static string Prof(List<ApproachSupportSpan> l) { var p = new List<string>(); foreach (var s in l) p.Add(string.Format(IC, "[{0:0.###}, {1:0.###}] x{2}", s.start, s.end, s.supporters)); return string.Join(" ", p); }

    static string PairDetails(FacingPairRecord p, CrossfireAttackModel model)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendFormat(IC, "{0}  FACING PAIR ({1})\nspacing horizontal {2:0.00}  3D {3:0.00}  dy {4:0.00}  overlapping band [{5:0.#}, {6:0.#}]\n",
            p.formationId, p.mixedLevel ? "mixed" : "same-level", p.horizontalDistance, p.centreDistance3D, p.heightDifference, p.overlapBandMinimum, p.overlapBandMaximum);
        foreach ((string tag, FacingPairMember m) in new[] { ("A", p.memberA), ("B", p.memberB) })
            sb.AppendFormat(IC, "\n{0} {1} {2} {3}\n  position {4}\n  forward heading {5:0.0} (intended {6:0.0}, facing error {7:0.00})\n  front attack position {8}\n",
                tag, m.baseId, m.baseType, m.level, V(m.position), m.forwardHeadingDegrees, m.intendedHeadingDegrees, m.facingErrorDegrees, V(m.frontAttackPosition));
        foreach (FacingPairLink l in new[] { p.bSupportsA, p.aSupportsB })
        {
            var prof = ClusterFormations.SupportProfile(new List<List<ApproachInterval>> { l.coverage }, model.MaxExtraApproach);
            sb.AppendFormat(IC, "\nfront of {0}: {1} valid supporter(s) (need {2})\n  {3}: 3D {4:0.00} in [{5:0.#}, {6:0.#}] {7}\n  approach coverage {8} = {9:0.00}/{10:0} ({11})\n  profile {12}\n",
                l.defendedBaseId, l.EngagesAtFront ? 1 : 0, PairRequiredSupporters, l.supporterBaseId, l.distanceToFront, l.bandMinimum, l.bandMaximum, l.statusAtFront,
                Iv(l.coverage), l.coveredLength, l.analysedLength, l.FullApproachCovered ? "full" : "partial", Prof(prof));
        }

        sb.Append("\nreserved corridor (width x length):\n");
        foreach (FormationCorridorSegment s in p.corridor) sb.AppendFormat(IC, "  {0}: {1:0.##} x {2:0.00}, heading {3:0.0}, centre ({4:0.00}, {5:0.00})\n", s.kind, s.width, s.Length, s.HeadingDegrees, s.Center.x, s.Center.z);
        return sb.ToString();
    }

    static string ClusterDetails(ClusterRecord c, CrossfireAttackModel model, int required)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendFormat(IC, "{0}  CLUSTER  {1} members\ncentre {2}  radius {3:0.00} (window {4:0.00}-{5:0.00})  shared band [{6:0.#}, {7:0.#}]\nminimum supporters at any front: {8} (need {9})\n",
            c.formationId, c.members.Count, V(c.centre), c.requestedRadius, c.radiusWindowMinimum, c.radiusWindowMaximum, c.sharedBandMinimum, c.sharedBandMaximum, c.minimumSupportersAtFront, required);
        foreach (ClusterMemberRecord m in c.members)
            sb.AppendFormat(IC, "\n#{0} {1} {2} {3}\n  position {4}  r {5:0.00}\n  forward heading {6:0.0} (to centre {7:0.0} deg, facing error {8:0.00})\n  front attack position {9}\n",
                m.slotIndex, m.baseId, m.baseType, m.level, V(m.position), m.radius, m.forwardHeadingDegrees, m.angleToCentreDegrees, m.facingErrorDegrees, V(m.frontAttackPosition));
        foreach (ClusterTarget t in c.targets)
        {
            sb.AppendFormat(IC, "\nfront of {0}: {1} valid supporter(s) (need {2}) {3}\n", t.defendedBaseId, t.supportersAtFront, required, t.supportersAtFront >= required ? "OK" : "FAIL");
            foreach (ClusterSupport s in t.candidates)
                sb.AppendFormat(IC, "  {0} {1}: 3D {2:0.00} in [{3:0.#}, {4:0.#}] {5}; approach {6} = {7:0.00}\n", s.supporterBaseId, s.supporterLevel, s.distanceToFront, s.bandMinimum, s.bandMaximum, s.statusAtFront, Iv(s.coverage), s.coveredLength);
            sb.AppendFormat(IC, "  profile {0}\n  covered >=1 {1:0.00}/{4:0}, >=2 {2:0.00}/{4:0}, outside every band {3:0.00}/{4:0} (partial)\n", Prof(t.profile), t.lengthWithAtLeastOne, t.lengthWithAtLeastTwo, t.lengthUncovered, t.analysedLength);
        }

        sb.Append("\nreserved approach strips (width x length):\n");
        foreach (FormationCorridorSegment s in c.approachStrips) sb.AppendFormat(IC, "  {0}: {1:0.##} x {2:0.00}, heading {3:0.0}\n", s.kind, s.width, s.Length, s.HeadingDegrees);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ filtering (used by the window and the tests)
    public static FormationDebugLayer EnabledLayers(FormationDebugSettings s)
    {
        if (s == null || !s.showFormationDebug) return FormationDebugLayer.None;
        FormationDebugLayer l = FormationDebugLayer.Structure;
        if (s.showMemberFacing) l |= FormationDebugLayer.Facing;
        if (s.showAttackPositions) l |= FormationDebugLayer.Attack;
        if (s.showSupporterLines) l |= FormationDebugLayer.Supporters;
        if (s.showEngagementRanges) l |= FormationDebugLayer.Ranges;
        if (s.showApproachCoverage) l |= FormationDebugLayer.Coverage;
        if (s.showApproachReservations) l |= FormationDebugLayer.Reservation;
        if (s.showFormationLabels) l |= FormationDebugLayer.Labels;
        return l;
    }

    public static bool FormationVisible(FormationDebugEntry e, FormationDebugSettings s, string selectedId)
    {
        if (s == null || !s.showFormationDebug) return false;
        if (e.kind == FormationKind.FacingPair && !s.showFacingPairDebug) return false;
        if (e.kind == FormationKind.Cluster && !s.showClusterDebug) return false;
        return !s.onlySelectedFormation || selectedId == e.formationId;
    }

    /// <summary>A command is drawn only if every layer flag it carries is enabled (labels of a layer need Labels too).</summary>
    public static bool CommandVisible(in FormationDebugCommand c, FormationDebugLayer enabled, bool detail) =>
        (c.layer & enabled) == c.layer && (detail || !c.detailOnly);
}

/// <summary>Visualization switches (all OFF by default). Stored per user in EditorPrefs by the window.</summary>
public sealed class FormationDebugSettings
{
    public bool showFormationDebug;
    public bool showFacingPairDebug;
    public bool showClusterDebug;
    public bool showMemberFacing;
    public bool showAttackPositions;
    public bool showSupporterLines;
    public bool showEngagementRanges;
    public bool showApproachCoverage;
    public bool showApproachReservations;
    public bool showFormationLabels;
    public bool onlySelectedFormation;
    public bool alwaysFullDetail;
    /// <summary>Camera distance (Unity units) below which detail labels, side rings and candidate lines appear.</summary>
    public float detailDistance = 150f;
}
