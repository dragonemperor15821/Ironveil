using UnityEngine;

/// <summary>
/// Stage 3A: a designated construction position inside a base foundation.
///
/// The slot transform supplies the local position and rotation. The slot
/// itself always keeps unit scale; the building placed under it receives the
/// slot's instance scale exactly once (0.2 buildings, 0.05 Flaks), so the base
/// root's runtime scale (6.5) is the only other factor. No transform between
/// the base root and the slot may carry a non-unit scale (validated by the
/// Stage 3A editor tooling).
/// </summary>
[DisallowMultipleComponent]
public sealed class BuildingSlot : MonoBehaviour
{
    [SerializeField] private string slotId;
    [SerializeField] private BuildingType[] acceptedTypes = new BuildingType[0];

    [Tooltip("Uniform local scale given to the placed instance (relative to the base root).")]
    [SerializeField, Min(0.0001f)] private float instanceScale = 0.2f;

    [Tooltip("Reserved horizontal footprint in base-root units (X by Z, slot frame), covering " +
             "every registered model level of the accepted types. Used for overlap checks.")]
    [SerializeField] private Vector2 reservedFootprint = new Vector2(0.2f, 0.2f);

    [Tooltip("True when no foundation mesh lies under this slot (outer Flak rows stand on terrain). " +
             "A later construction step should ground the instance on the actual terrain height.")]
    [SerializeField] private bool groundOnTerrain;

    [Tooltip("True when part of the footprint rests on the foundation mesh (e.g. the low apron outside " +
             "the Ground arena wall). The slot's local Y is then that surface height, and grounding never " +
             "places the instance below it.")]
    [SerializeField] private bool hasFoundationSupport;

    [SerializeField] private BuildingInstance occupant;

    public string SlotId => slotId;
    public BuildingType[] AcceptedTypes => acceptedTypes;
    public float InstanceScale => instanceScale;
    public Vector2 ReservedFootprint => reservedFootprint;
    public bool GroundOnTerrain => groundOnTerrain;
    public bool HasFoundationSupport => hasFoundationSupport;
    public Vector3 LocalPosition => transform.localPosition;
    public Quaternion LocalRotation => transform.localRotation;
    public BuildingInstance Occupant => occupant;
    public bool IsOccupied => occupant != null;

    public bool Accepts(BuildingType type)
    {
        for (int i = 0; i < acceptedTypes.Length; i++)
        {
            if (acceptedTypes[i] == type) return true;
        }

        return false;
    }

    /// <summary>Records the occupant. Does not instantiate anything.</summary>
    public void SetOccupant(BuildingInstance instance)
    {
        occupant = instance;
    }

    public void ClearOccupant()
    {
        occupant = null;
    }

#if UNITY_EDITOR
    public void EditorInitialize(string newSlotId, BuildingType[] newAcceptedTypes, float newInstanceScale,
        Vector2 newReservedFootprint, bool newGroundOnTerrain)
    {
        slotId = newSlotId;
        acceptedTypes = newAcceptedTypes;
        instanceScale = newInstanceScale;
        reservedFootprint = newReservedFootprint;
        groundOnTerrain = newGroundOnTerrain;
        hasFoundationSupport = !newGroundOnTerrain;
        occupant = null;
    }

    public void EditorSetFoundationSupport(bool value)
    {
        hasFoundationSupport = value;
    }

    /// <summary>EditorPrefs key of the explicit debug toggle (Tools/IRONVEIL/Modular Bases/Show Slot Gizmos). Default OFF.</summary>
    public const string ShowGizmosPrefKey = "IRONVEIL.ModularBases.ShowSlotGizmos";

    // Editor-only debug drawing: compiled out of builds, never rendered by game
    // cameras, and drawn in the Scene view only when the toggle is on (or for
    // the selected slot).
    private void OnDrawGizmos()
    {
        if (UnityEditor.EditorPrefs.GetBool(ShowGizmosPrefKey, false)) DrawSlotGizmo();
    }

    private void OnDrawGizmosSelected()
    {
        if (!UnityEditor.EditorPrefs.GetBool(ShowGizmosPrefKey, false)) DrawSlotGizmo();
    }

    private void DrawSlotGizmo()
    {
        // Footprint is in base-root units; draw it in the base root's frame so
        // the root's runtime scale applies, positioned/rotated by this slot.
        BaseFoundation foundation = GetComponentInParent<BaseFoundation>();
        Transform root = foundation != null ? foundation.transform : transform.root;
        Vector3 rootLocalPos = root.InverseTransformPoint(transform.position);
        Quaternion rootLocalRot = Quaternion.Inverse(root.rotation) * transform.rotation;
        Matrix4x4 previous = Gizmos.matrix;
        Gizmos.matrix = root.localToWorldMatrix * Matrix4x4.TRS(rootLocalPos, rootLocalRot, Vector3.one);
        Gizmos.color = IsOccupied ? new Color(0.2f, 1f, 0.3f, 0.9f) : new Color(1f, 0.8f, 0.1f, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(reservedFootprint.x, 0.002f, reservedFootprint.y));
        Gizmos.matrix = previous;
    }
#endif
}
