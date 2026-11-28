using UnityEngine;

/// <summary>
/// Stage 3A: marks a placed building. The building level is the building's own
/// level and is completely independent of the base level (BaseLevelInfo).
/// </summary>
[DisallowMultipleComponent]
public sealed class BuildingInstance : MonoBehaviour
{
    [SerializeField] private BuildingType type;
    [SerializeField, Min(1)] private int buildingLevel = 1;
    [SerializeField] private BuildingSlot slot;
    [SerializeField] private BuildingDefinition definition;

    public BuildingType Type => type;
    public int BuildingLevel => buildingLevel;
    public BuildingSlot Slot => slot;
    public BuildingDefinition Definition => definition;

    public void Initialize(BuildingDefinition newDefinition, int newBuildingLevel, BuildingSlot newSlot)
    {
        definition = newDefinition;
        if (newDefinition != null) type = newDefinition.Type;
        buildingLevel = Mathf.Max(1, newBuildingLevel);
        slot = newSlot;
    }
}
