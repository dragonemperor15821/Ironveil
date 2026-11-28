using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-base Flak defence: the approved 28 PLANNED positions (FlakDefensiveLayout)
/// versus the Flaks actually CONSTRUCTED in them (occupied BuildingSlots).
///
/// Nothing is ever built automatically: no Awake/Start/OnEnable logic. A new
/// base gets its two starting Flaks only through BaseBuildingState.
/// ApplyInitialConfiguration (2 x L1 Flak + 1 x L1 Factory/Refinery), applied
/// once per base by BaseConstructionBootstrap, and further Flaks only through
/// explicit Construct calls (BaseConstruction.TryConstructFlak).
///
/// Independent of base generation: it lives on a foundation instance and uses
/// BuildingPlacement + FlakGrounding. Building levels are independent of the
/// base level.
/// </summary>
[DisallowMultipleComponent]
public sealed class BaseFlakDefence : MonoBehaviour
{
    [SerializeField] private FlakDefensiveLayout layout;
    [SerializeField] private BaseBuildingState state;

    private readonly Dictionary<string, BuildingSlot> slotById = new Dictionary<string, BuildingSlot>();

    /// <summary>
    /// Raised after this base's constructed-Flak set changes (construct, demolish,
    /// initial state). Listeners (e.g. the Base Management Menu) read the new
    /// ConstructedCount from the instance; nothing is shared between bases.
    /// </summary>
    public static event System.Action<BaseFlakDefence> Changed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        Changed = null;
    }

    /// <summary>True once this base's starting state (2 x L1 Flak + 1 x L1 Refinery) has been applied.</summary>
    public bool InitialStateApplied { get; private set; }

    public void MarkInitialStateApplied()
    {
        InitialStateApplied = true;
    }

    /// <summary>Raises <see cref="Changed"/> for this base.</summary>
    public void NotifyChanged()
    {
        Changed?.Invoke(this);
    }

    public FlakDefensiveLayout Layout => layout;
    public bool IsBound => layout != null && slotById.Count == layout.Count;
    public int PlannedCount => layout != null ? layout.Count : 0;

    public int ConstructedCount
    {
        get
        {
            int n = 0;
            foreach (BuildingSlot s in slotById.Values) if (s.IsOccupied) n++;
            return n;
        }
    }

    public int FreeCount => PlannedCount - ConstructedCount;

    /// <summary>
    /// Binds to the approved layout and checks that this base's Flak slots match
    /// it exactly (same 28 IDs, same base-root local position and yaw).
    /// </summary>
    public bool Bind(FlakDefensiveLayout approvedLayout, out string error)
    {
        error = string.Empty;
        slotById.Clear();
        layout = approvedLayout;
        state = GetComponent<BaseBuildingState>();
        if (layout == null || state == null)
        {
            error = layout == null ? "no layout" : "no BaseBuildingState on this object";
            return false;
        }

        if (!layout.Validate(out error)) return false;

        foreach (BuildingSlot s in state.Slots)
        {
            if (s == null || !s.Accepts(BuildingType.Flak)) continue;
            if (!layout.TryGet(s.SlotId, out FlakDefensiveLayout.Position p))
            {
                error = "slot not in approved layout: " + s.SlotId;
                slotById.Clear();
                return false;
            }

            // Compose the pose through the local hierarchy (exact, independent of
            // where the base stands in the world or its runtime scale).
            if (!TryRootLocalPose(s.transform, out Vector3 local, out Quaternion localRot))
            {
                error = $"slot {s.SlotId} is not under this base root";
                slotById.Clear();
                return false;
            }

            float yawLocal = localRot.eulerAngles.y;
            if ((local - p.localPosition).sqrMagnitude > 1e-10f || Mathf.Abs(Mathf.DeltaAngle(yawLocal, p.yawDegrees)) > 0.01f)
            {
                error = $"slot {s.SlotId} differs from the approved layout";
                slotById.Clear();
                return false;
            }

            slotById[s.SlotId] = s;
        }

        if (slotById.Count != layout.Count)
        {
            error = $"{slotById.Count} Flak slots on the base, layout has {layout.Count}";
            slotById.Clear();
            return false;
        }

        return true;
    }

    private bool TryRootLocalPose(Transform t, out Vector3 position, out Quaternion rotation)
    {
        Matrix4x4 m = Matrix4x4.identity;
        rotation = Quaternion.identity;
        for (Transform c = t; c != null; c = c.parent)
        {
            if (c == transform)
            {
                position = m.MultiplyPoint3x4(Vector3.zero);
                return true;
            }

            m = Matrix4x4.TRS(c.localPosition, c.localRotation, c.localScale) * m;
            rotation = c.localRotation * rotation;
        }

        position = Vector3.zero;
        return false;
    }

    public BuildingSlot GetSlot(string slotId) => slotById.TryGetValue(slotId, out BuildingSlot s) ? s : null;

    /// <summary>First free planned position in the layout's default construction order, or null.</summary>
    public BuildingSlot NextFreeSlot()
    {
        if (layout == null) return null;
        foreach (FlakDefensiveLayout.Position p in layout.InConstructionOrder())
        {
            BuildingSlot s = GetSlot(p.slotId);
            if (s != null && !s.IsOccupied) return s;
        }

        return null;
    }

    /// <summary>
    /// Constructs one Flak of the given building level in a planned position
    /// (slotId, or the next free one when null) and grounds it on the terrain
    /// when a height query is supplied. Returns null when no position is free,
    /// the position is occupied, or the level has no model.
    /// </summary>
    public BuildingInstance Construct(BuildingDefinition flakDefinition, int buildingLevel, string slotId = null,
        FlakGrounding.HeightQuery ground = null)
    {
        if (!IsBound || flakDefinition == null || flakDefinition.Type != BuildingType.Flak) return null;
        BuildingSlot slot = slotId != null ? GetSlot(slotId) : NextFreeSlot();
        if (slot == null || slot.IsOccupied) return null;
        BuildingInstance instance = BuildingPlacement.Place(slot, flakDefinition, buildingLevel);
        if (instance != null && ground != null) FlakGrounding.Ground(slot, transform, ground, out _);
        if (instance != null) NotifyChanged();
        return instance;
    }

    /// <summary>Removes a constructed Flak; the planned position stays available.</summary>
    public bool Demolish(string slotId)
    {
        BuildingSlot s = GetSlot(slotId);
        if (s == null || !s.IsOccupied) return false;
        BuildingPlacement.Remove(s);
        NotifyChanged();
        return true;
    }

    /// <summary>The occupied position latest in the construction order, or null.</summary>
    public BuildingSlot LatestConstructedSlot()
    {
        if (layout == null) return null;
        List<FlakDefensiveLayout.Position> order = layout.InConstructionOrder();
        for (int i = order.Count - 1; i >= 0; i--)
        {
            BuildingSlot s = GetSlot(order[i].slotId);
            if (s != null && s.IsOccupied) return s;
        }

        return null;
    }
}
