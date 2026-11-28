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
///
/// Flak level (Flak Upgrade System): every base holds ONE Flak level (L1..L4,
/// FlakUpgradeRules) shared by all its Flaks; its engagement band comes from
/// FlakLevelRanges, measured from this base's centre (the root position). It lives on this component, so it
/// belongs to exactly this base instance and disappears with it (New World
/// regenerates bases at L1). Construction builds at this level; an upgrade
/// replaces every Flak in place (ReplaceAllFlaks). Session state only: there is
/// no save system, so levels do not survive a restart.
/// </summary>
[DisallowMultipleComponent]
public sealed class BaseFlakDefence : MonoBehaviour
{
    [SerializeField] private FlakDefensiveLayout layout;
    [SerializeField] private BaseBuildingState state;

    [Tooltip("This base's Flak level (L1..L4). Every Flak on the base is at this level.")]
    [SerializeField] private int flakLevel = FlakUpgradeRules.StartingLevel;

    private readonly Dictionary<string, BuildingSlot> slotById = new Dictionary<string, BuildingSlot>();

    // Positions reserved by queued constructions (BaseFlakConstruction): empty, but not free.
    private readonly HashSet<string> reserved = new HashSet<string>();

    /// <summary>
    /// Raised after this base's constructed-Flak set changes (construct, demolish,
    /// initial state). Listeners (e.g. the Base Management Menu) read the new
    /// ConstructedCount from the instance; nothing is shared between bases.
    /// </summary>
    public static event System.Action<BaseFlakDefence> Changed;

    /// <summary>
    /// TEST HOOK ONLY (null in the game): when it returns true for a slot, the
    /// replacement Flak for that slot is treated as a failed instantiation, so
    /// the rollback path of ReplaceAllFlaks can be exercised.
    /// </summary>
    public static System.Func<BuildingSlot, bool> ReplacementFaultForTests;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        Changed = null;
        ReplacementFaultForTests = null;
    }

    /// <summary>This base's Flak level, L1..L4 (an unset or invalid value reads as L1).</summary>
    public int FlakLevel => FlakUpgradeRules.Sanitize(flakLevel);

    /// <summary>Range origin: the centre of the base (its root position), as in CrossfireGeometry.</summary>
    public Vector3 BaseCentre => transform.position;

    /// <summary>Engagement band of the current Flak level, SC units (FlakLevelRanges).</summary>
    public FlakRangeBand RangeSC => FlakLevelRanges.GetRangeSC(FlakLevel);

    /// <summary>Engagement band of the current Flak level, Unity world units (SC x the provisional S).</summary>
    public FlakRangeBand RangeUnity => FlakLevelRanges.GetRangeUnity(FlakLevel);

    /// <summary>True when the current Flak level could engage <paramref name="worldTarget"/> (full 3D distance from the base centre).</summary>
    public bool CanEngage(Vector3 worldTarget) => FlakLevelRanges.CanEngage(FlakLevel, BaseCentre, worldTarget);

    /// <summary>True while an upgrade transaction of this base is running (blocks re-entrant purchases).</summary>
    public bool IsUpgrading { get; internal set; }

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

    /// <summary>Positions reserved by queued constructions (empty until they complete).</summary>
    public int ReservedCount => reserved.Count;

    /// <summary>Positions neither built nor reserved: what new constructions can still take.</summary>
    public int AvailableCount => PlannedCount - ConstructedCount - ReservedCount;

    public bool IsReserved(string slotId) => slotId != null && reserved.Contains(slotId);

    /// <summary>
    /// Binds to the approved layout and checks that this base's Flak slots match
    /// it exactly (same 28 IDs, same base-root local position and yaw).
    /// </summary>
    public bool Bind(FlakDefensiveLayout approvedLayout, out string error)
    {
        error = string.Empty;
        slotById.Clear();
        reserved.Clear();
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

    /// <summary>
    /// First free planned position in the layout's default construction order (not built and
    /// not reserved by a queued construction), or null.
    /// </summary>
    public BuildingSlot NextFreeSlot()
    {
        if (layout == null) return null;
        foreach (FlakDefensiveLayout.Position p in layout.InConstructionOrder())
        {
            BuildingSlot s = GetSlot(p.slotId);
            if (s != null && !s.IsOccupied && !reserved.Contains(s.SlotId)) return s;
        }

        return null;
    }

    /// <summary>
    /// Reserves the next free position in the approved construction order for a queued
    /// construction, so no other construction can take it. Null when none is free.
    /// </summary>
    public BuildingSlot ReserveNextFreeSlot()
    {
        if (!IsBound || IsUpgrading) return null;
        BuildingSlot s = NextFreeSlot();
        if (s == null) return null;
        reserved.Add(s.SlotId);
        return s;
    }

    /// <summary>Frees a reservation (cancelled or failed construction). False when it was not reserved.</summary>
    public bool ReleaseReservation(string slotId)
    {
        return slotId != null && reserved.Remove(slotId);
    }

    /// <summary>
    /// Completes a queued construction: builds one Flak at the base's CURRENT FlakLevel in the
    /// reserved position (so a construction finishing after an upgrade is built at the new level)
    /// and grounds it. The reservation is consumed only when the Flak was placed. Null (reservation
    /// kept) while an upgrade is running or when the position cannot take the Flak.
    /// </summary>
    public BuildingInstance ConstructReserved(BuildingDefinition flakDefinition, string slotId,
        FlakGrounding.HeightQuery ground = null)
    {
        if (!IsBound || IsUpgrading || !reserved.Contains(slotId)) return null;
        if (flakDefinition == null || flakDefinition.Type != BuildingType.Flak) return null;
        BuildingSlot slot = GetSlot(slotId);
        if (slot == null || slot.IsOccupied) return null;
        BuildingInstance instance = BuildingPlacement.Place(slot, flakDefinition, FlakLevel);
        if (instance == null) return null;
        reserved.Remove(slotId);
        if (ground != null) FlakGrounding.Ground(slot, transform, ground, out _);
        NotifyChanged();
        return instance;
    }

    /// <summary>
    /// Constructs one Flak at this base's current <see cref="FlakLevel"/> (new Flaks
    /// inherit the base level) in a planned position (slotId, or the next free one
    /// in the approved construction order). Null when refused.
    /// </summary>
    public BuildingInstance ConstructAtFlakLevel(BuildingDefinition flakDefinition, string slotId = null,
        FlakGrounding.HeightQuery ground = null)
    {
        return Construct(flakDefinition, FlakLevel, slotId, ground);
    }

    /// <summary>
    /// Constructs one Flak of the given building level in a planned position
    /// (slotId, or the next free one when null) and grounds it on the terrain
    /// when a height query is supplied. Returns null when no position is free,
    /// the position is occupied, the level has no model, the level differs from
    /// this base's <see cref="FlakLevel"/> (all Flaks on a base share one level),
    /// or an upgrade is running.
    /// </summary>
    public BuildingInstance Construct(BuildingDefinition flakDefinition, int buildingLevel, string slotId = null,
        FlakGrounding.HeightQuery ground = null)
    {
        if (!IsBound || flakDefinition == null || flakDefinition.Type != BuildingType.Flak) return null;
        if (IsUpgrading || buildingLevel != FlakLevel) return null;
        BuildingSlot slot = slotId != null ? GetSlot(slotId) : NextFreeSlot();
        if (slot == null || slot.IsOccupied || reserved.Contains(slot.SlotId)) return null; // reserved = taken by a queued construction
        BuildingInstance instance = BuildingPlacement.Place(slot, flakDefinition, buildingLevel);
        if (instance != null && ground != null) FlakGrounding.Ground(slot, transform, ground, out _);
        if (instance != null) NotifyChanged();
        return instance;
    }

    /// <summary>Removes a constructed Flak; the planned position stays available.</summary>
    public bool Demolish(string slotId)
    {
        if (IsUpgrading) return false;
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

    // ------------------------------------------------------------------ upgrade

    private struct Replacement
    {
        public BuildingSlot slot;
        public BuildingInstance previous;
        public bool previousWasActive;
        public BuildingInstance replacement;
    }

    /// <summary>
    /// Replaces EVERY constructed Flak on this base with the <paramref name="targetLevel"/>
    /// model, all or nothing, and on success sets <see cref="FlakLevel"/>. No Gold is
    /// handled here (see BaseConstruction.TryUpgradeFlaks). Does not raise Changed.
    ///
    /// Each replacement goes into the SAME BuildingSlot (so the same planned position,
    /// layout slot and construction-order index) through BuildingPlacement.Place, which
    /// gives the slot's facing, the level's facing correction and the 0.05 scale; the
    /// previous Flak's exact position under the slot (its grounded height) is then copied over.
    ///
    /// Phase 1 parks each old Flak (deactivated, kept) and places the new one. If any
    /// placement fails or throws, every new Flak is destroyed and every old Flak is put
    /// back exactly as it was; the level is unchanged. Phase 2 (only after all
    /// succeeded) detaches and destroys the old Flaks and raises the level.
    /// </summary>
    public bool ReplaceAllFlaks(BuildingDefinition flakDefinition, int targetLevel, out int replaced, out string error)
    {
        replaced = 0;
        error = string.Empty;
        if (!IsBound) { error = "base not bound to its Flak layout"; return false; }
        if (flakDefinition == null || flakDefinition.Type != BuildingType.Flak) { error = "no Flak definition"; return false; }
        if (!FlakUpgradeRules.IsValidLevel(targetLevel)) { error = "invalid Flak level " + targetLevel; return false; }
        if (flakDefinition.GetPrefab(targetLevel) == null) { error = $"no model registered for Flak L{targetLevel}"; return false; }

        List<Replacement> done = new List<Replacement>(PlannedCount);
        try
        {
            foreach (FlakDefensiveLayout.Position p in layout.InConstructionOrder())
            {
                BuildingSlot slot = GetSlot(p.slotId);
                if (slot == null || !slot.IsOccupied) continue;

                BuildingInstance previous = slot.Occupant;
                // Same parent (the slot) for old and new, so copying the LOCAL position keeps the
                // world position bit-exact (a world-space copy would round through the 6.5 scale).
                Vector3 groundedLocalPosition = previous.transform.localPosition;
                Replacement r = new Replacement { slot = slot, previous = previous, previousWasActive = previous.gameObject.activeSelf };
                slot.ClearOccupant();
                previous.gameObject.SetActive(false);
                done.Add(r);

                bool fault = ReplacementFaultForTests != null && ReplacementFaultForTests(slot);
                BuildingInstance fresh = fault ? null : BuildingPlacement.Place(slot, flakDefinition, targetLevel);
                if (fresh == null)
                {
                    error = $"could not place Flak L{targetLevel} in {slot.SlotId}";
                    Rollback(done);
                    return false;
                }

                fresh.transform.localPosition = groundedLocalPosition; // XZ of the slot + the grounded height
                r.replacement = fresh;
                done[done.Count - 1] = r;
            }
        }
        catch (System.Exception exception)
        {
            error = "Flak replacement threw: " + exception.Message;
            Rollback(done);
            return false;
        }

        if (done.Count == 0)
        {
            error = "no constructed Flak to replace";
            return false;
        }

        foreach (Replacement r in done) Discard(r.previous);
        flakLevel = targetLevel;
        replaced = done.Count;
        return true;
    }

    private static void Rollback(List<Replacement> done)
    {
        for (int i = done.Count - 1; i >= 0; i--)
        {
            Replacement r = done[i];
            BuildingInstance stray = r.slot.Occupant != r.previous ? r.slot.Occupant : null;
            if (stray != null)
            {
                r.slot.ClearOccupant();
                Discard(stray);
            }

            if (r.replacement != null && r.replacement != stray) Discard(r.replacement);
            if (r.previous != null)
            {
                r.previous.gameObject.SetActive(r.previousWasActive);
                r.slot.SetOccupant(r.previous);
            }
        }

        done.Clear();
    }

    /// <summary>
    /// Removes a Flak object at once from the base: deactivated and detached first, so
    /// hover bounds and counts never see it during Destroy's end-of-frame delay.
    /// </summary>
    private static void Discard(BuildingInstance instance)
    {
        if (instance == null) return;
        GameObject go = instance.gameObject;
        go.SetActive(false);
        go.transform.SetParent(null, true);
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }
}
