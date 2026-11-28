using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stage 3A: per-base building state on a foundation root: the base's slots
/// and which of them are occupied. Kept separate from BaseLevelInfo: the base
/// level never changes building levels and building levels never change the
/// base level.
///
/// Nothing calls ApplyInitialConfiguration automatically in this stage; it
/// exists so the prepared starting state (2 x L1 Flak + 1 x L1 Factory/
/// Refinery) can be verified and used once foundations are approved.
/// </summary>
[DisallowMultipleComponent]
public sealed class BaseBuildingState : MonoBehaviour
{
    [SerializeField] private List<BuildingSlot> slots = new List<BuildingSlot>();

    public IReadOnlyList<BuildingSlot> Slots => slots;

    public BuildingSlot FindSlot(string slotId)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] != null && slots[i].SlotId == slotId)
            {
                return slots[i];
            }
        }

        return null;
    }

    public int CountOf(BuildingType type)
    {
        int n = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            BuildingSlot s = slots[i];
            if (s != null && s.IsOccupied && s.Occupant.Type == type) n++;
        }

        return n;
    }

    public int OccupiedCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] != null && slots[i].IsOccupied) n++;
            }

            return n;
        }
    }

    /// <summary>
    /// Places the configured starting buildings into their designated slots.
    /// Returns false (placing nothing further) on the first entry that cannot
    /// be satisfied. Intended for newly created bases only.
    /// </summary>
    public bool ApplyInitialConfiguration(InitialBuildingConfig config, BuildingCatalog catalog, out string error)
    {
        error = string.Empty;
        if (config == null || catalog == null)
        {
            error = "missing config or catalog";
            return false;
        }

        foreach (InitialBuildingConfig.Entry entry in config.Entries)
        {
            BuildingDefinition definition = catalog.Get(entry.type);
            if (definition == null)
            {
                error = "no definition for " + entry.type;
                return false;
            }

            if (entry.slotIds == null || entry.slotIds.Length < entry.count)
            {
                error = entry.type + ": fewer slot IDs than the count";
                return false;
            }

            for (int i = 0; i < entry.count; i++)
            {
                BuildingSlot slot = FindSlot(entry.slotIds[i]);
                if (slot == null)
                {
                    error = "slot not found: " + entry.slotIds[i];
                    return false;
                }

                if (BuildingPlacement.Place(slot, definition, entry.buildingLevel) == null)
                {
                    error = $"could not place {entry.type} L{entry.buildingLevel} in {entry.slotIds[i]}";
                    return false;
                }
            }
        }

        return true;
    }

#if UNITY_EDITOR
    public void EditorSetSlots(List<BuildingSlot> newSlots)
    {
        slots = newSlots ?? new List<BuildingSlot>();
    }
#endif
}
