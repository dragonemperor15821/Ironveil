using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stage 3A: the buildings a NEWLY created base starts with. Data only; not
/// applied to existing scenes or already generated bases in this stage.
/// Approved state: 2 x L1 Flak, 1 x L1 Factory/Refinery, nothing else.
/// The levels here are building levels, never base levels.
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Construction/Initial Building Config", fileName = "InitialBuildingConfig")]
public sealed class InitialBuildingConfig : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        public BuildingType type;
        [Min(0)] public int count;
        [Min(1)] public int buildingLevel;
        [Tooltip("Slot IDs to occupy, in order. Must hold at least as many IDs as the count.")]
        public string[] slotIds;
    }

    [SerializeField] private List<Entry> entries = new List<Entry>();

    public IReadOnlyList<Entry> Entries => entries;

    public int CountOf(BuildingType type)
    {
        int n = 0;
        foreach (Entry e in entries)
        {
            if (e.type == type) n += e.count;
        }

        return n;
    }

#if UNITY_EDITOR
    public void EditorSetEntries(List<Entry> newEntries)
    {
        entries = newEntries ?? new List<Entry>();
    }
#endif
}
