using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stage 3A: static data for one building type. Holds only facts that exist
/// in the project: the building-level models that are actually imported, the
/// per-instance scale relative to the base root, and a maximum count where
/// one has been defined. No costs, build times or upgrade requirements.
///
/// Building level is independent of base level (BaseLevelInfo): nothing here
/// reads or depends on the base level.
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Construction/Building Definition", fileName = "BuildingDefinition")]
public sealed class BuildingDefinition : ScriptableObject
{
    [Serializable]
    public struct LevelModel
    {
        [Min(1)] public int level;
        public GameObject prefab;

        [Tooltip("Yaw (degrees about the slot's up axis) that turns this model's own forward " +
                 "(e.g. a Flak's barrels) onto the slot's +Z facing. 0 for models already facing +Z.")]
        public float facingYawOffset;
    }

    [SerializeField] private BuildingType type;
    [SerializeField] private string displayName;

    [Tooltip("Only building levels whose model exists in the project, sorted by level.")]
    [SerializeField] private List<LevelModel> levels = new List<LevelModel>();

    [Tooltip("Uniform local scale of a placed instance relative to the BASE ROOT " +
             "(the base root keeps its own runtime scale, e.g. 6.5). Buildings 0.2, Flaks 0.05.")]
    [SerializeField, Min(0.0001f)] private float instanceScale = 0.2f;

    [Tooltip("Maximum number per base, or 0 when no maximum has been defined yet.")]
    [SerializeField, Min(0)] private int maxCount;

    [Tooltip("Model levels known to be missing, recorded for later. Informational only; " +
             "the maximum building level has not been confirmed.")]
    [SerializeField, TextArea] private string missingLevelsNote;

    public BuildingType Type => type;
    public string DisplayName => string.IsNullOrEmpty(displayName) ? type.ToString() : displayName;
    public float InstanceScale => instanceScale;
    public bool HasMaxCount => maxCount > 0;
    public int MaxCount => maxCount;
    public string MissingLevelsNote => missingLevelsNote;
    public IReadOnlyList<LevelModel> Levels => levels;

    /// <summary>True when a model for this building level exists.</summary>
    public bool HasLevel(int level) => GetPrefab(level) != null;

    /// <summary>The model for an exact building level, or null if none is imported.</summary>
    public GameObject GetPrefab(int level)
    {
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i].level == level && levels[i].prefab != null)
            {
                return levels[i].prefab;
            }
        }

        return null;
    }

    /// <summary>Facing yaw offset of an exact building level's model (0 when not registered).</summary>
    public float GetFacingYawOffset(int level)
    {
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i].level == level && levels[i].prefab != null)
            {
                return levels[i].facingYawOffset;
            }
        }

        return 0f;
    }

    /// <summary>Highest level with a model (not necessarily the building's maximum level).</summary>
    public int HighestAvailableLevel
    {
        get
        {
            int best = 0;
            foreach (LevelModel m in levels)
            {
                if (m.prefab != null && m.level > best) best = m.level;
            }

            return best;
        }
    }

#if UNITY_EDITOR
    /// <summary>Editor-only initialiser used by the Stage 3A asset builder.</summary>
    public void EditorInitialize(BuildingType newType, string newDisplayName, float newInstanceScale,
        int newMaxCount, List<LevelModel> newLevels, string newMissingLevelsNote)
    {
        type = newType;
        displayName = newDisplayName;
        instanceScale = newInstanceScale;
        maxCount = newMaxCount;
        levels = newLevels ?? new List<LevelModel>();
        levels.Sort((a, b) => a.level.CompareTo(b.level));
        missingLevelsNote = newMissingLevelsNote;
    }
#endif
}
