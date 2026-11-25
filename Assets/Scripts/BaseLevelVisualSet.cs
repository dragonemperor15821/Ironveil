using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Optional future hook: per (base type, level) replacement prefab.
///
/// No asset of this type exists yet, so every level keeps the current (L6)
/// prefab. When level-specific models are imported, create an asset, add
/// entries and assign it to BaseSpawner's levelVisualSet. Because levels are
/// assigned before placement, the replacement prefab's own footprint is the
/// one placement measures.
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Base Level Visual Set", fileName = "BaseLevelVisualSet")]
public sealed class BaseLevelVisualSet : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        public string baseType;
        public BaseLevel level;
        public GameObject prefab;
    }

    [SerializeField] private List<Entry> entries = new List<Entry>();

    /// <summary>Returns the replacement prefab, or null to keep the default.</summary>
    public GameObject Resolve(string baseType, BaseLevel level)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (entry.prefab != null && entry.level == level &&
                string.Equals(entry.baseType, baseType, StringComparison.Ordinal))
            {
                return entry.prefab;
            }
        }

        return null;
    }
}
