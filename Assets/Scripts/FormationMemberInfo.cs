using UnityEngine;

/// <summary>
/// Stage 2b: editor-readable formation membership attached to each facing-pair
/// member when strategic formations are on. Data only; no combat or targeting.
/// </summary>
[DisallowMultipleComponent]
public sealed class FormationMemberInfo : MonoBehaviour
{
    [SerializeField] private string formationId;
    [SerializeField] private FormationKind kind;
    [SerializeField] private FormationRole role;
    [SerializeField] private string baseId;
    [SerializeField] private BaseLevel level;
    [SerializeField] private string partnerBaseId;
    [SerializeField] private BaseLevel partnerLevel;
    [SerializeField] private float centreDistance3D;
    [SerializeField] private float horizontalDistance;
    [SerializeField] private Vector3 frontAttackPosition;
    [Tooltip("Partner's 3D distance to this base's front attack position.")]
    [SerializeField] private float partnerDistanceToMyFront;
    [SerializeField] private RangeBandStatus partnerStatusAtMyFront;
    [Tooltip("Extra approach (beyond the front position) the partner covers.")]
    [SerializeField] private string partnerApproachCoverage;
    [SerializeField] private float partnerCoveredLength;
    [SerializeField] private Vector3 partnerPosition;
    [Tooltip("Stage 2b.1 reserved corridor strips of this pair (placeholder width).")]
    [SerializeField] private FormationCorridorSegment[] reservedCorridor = new FormationCorridorSegment[0];

    [Header("Stage 2c cluster (empty for facing pairs)")]
    [SerializeField] private string[] clusterMembers = new string[0];
    [Tooltip("Other cluster members inside min..max range of this base's front attack position (full 3D).")]
    [SerializeField] private int supportersAtMyFront;
    [Tooltip("Per other member: 3D distance to this base's front, band and status.")]
    [SerializeField] private string[] supportAtMyFront = new string[0];
    [Tooltip("Partition of this base's analysed approach by supporter count.")]
    [SerializeField] private string approachSupportProfile;

    public string FormationId => formationId;
    public FormationKind Kind => kind;
    public FormationRole Role => role;
    public string PartnerBaseId => partnerBaseId;

    public void Initialize(FacingPairRecord pair, bool isMemberA)
    {
        FacingPairMember me = isMemberA ? pair.memberA : pair.memberB;
        FacingPairMember other = isMemberA ? pair.memberB : pair.memberA;
        FacingPairLink partnerCoversMe = isMemberA ? pair.bSupportsA : pair.aSupportsB;

        formationId = pair.formationId;
        kind = FormationKind.FacingPair;
        role = isMemberA ? FormationRole.Anchor : FormationRole.Partner;
        baseId = me.baseId;
        level = me.level;
        partnerBaseId = other.baseId;
        partnerLevel = other.level;
        centreDistance3D = pair.centreDistance3D;
        horizontalDistance = pair.horizontalDistance;
        frontAttackPosition = me.frontAttackPosition;
        partnerDistanceToMyFront = partnerCoversMe.distanceToFront;
        partnerStatusAtMyFront = partnerCoversMe.statusAtFront;
        partnerCoveredLength = partnerCoversMe.coveredLength;
        partnerPosition = other.position;
        reservedCorridor = pair.corridor != null ? pair.corridor.ToArray() : new FormationCorridorSegment[0];

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (ApproachInterval i in partnerCoversMe.coverage)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(i.ToString());
        }

        partnerApproachCoverage = sb.Length == 0 ? "none" : sb.ToString();
    }

    public void InitializeCluster(ClusterRecord cluster, int index)
    {
        ClusterMemberRecord me = cluster.members[index];
        formationId = cluster.formationId;
        kind = FormationKind.Cluster;
        role = index == 0 ? FormationRole.Anchor : FormationRole.Supporter;
        baseId = me.baseId;
        level = me.level;
        partnerBaseId = string.Empty;
        partnerPosition = cluster.centre;   // gizmo line to the cluster centre
        frontAttackPosition = me.frontAttackPosition;
        horizontalDistance = me.radius;
        clusterMembers = new string[cluster.members.Count];
        for (int i = 0; i < cluster.members.Count; i++) clusterMembers[i] = cluster.members[i].baseId;
        reservedCorridor = cluster.approachStrips != null ? cluster.approachStrips.ToArray() : new FormationCorridorSegment[0];

        System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;
        ClusterTarget mine = cluster.targets.Find(t => t.defendedBaseId == me.baseId);
        if (mine == null) return;
        supportersAtMyFront = mine.supportersAtFront;
        supportAtMyFront = new string[mine.candidates.Count];
        for (int i = 0; i < mine.candidates.Count; i++)
        {
            ClusterSupport s = mine.candidates[i];
            supportAtMyFront[i] = string.Format(ic, "{0} {1:0.00} in [{2:0.##}, {3:0.##}] {4}, approach {5:0.00}",
                s.supporterBaseId, s.distanceToFront, s.bandMinimum, s.bandMaximum, s.statusAtFront, s.coveredLength);
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (ApproachSupportSpan sp in mine.profile)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.AppendFormat(ic, "[{0:0.##}, {1:0.##}] x{2}", sp.start, sp.end, sp.supporters);
        }

        approachSupportProfile = sb.ToString();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawLine(transform.position, partnerPosition);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(frontAttackPosition, 0.6f);

        Gizmos.color = new Color(1f, 0.85f, 0f);
        float y = transform.position.y;
        foreach (FormationCorridorSegment s in reservedCorridor)
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(
                new Vector3(s.Center.x, y, s.Center.z), Quaternion.Euler(0f, s.HeadingDegrees, 0f), Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(s.width, 0.2f, s.Length));
            Gizmos.matrix = previous;
        }
    }
#endif
}
