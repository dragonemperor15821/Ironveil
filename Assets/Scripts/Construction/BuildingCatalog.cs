using System.Collections.Generic;
using UnityEngine;

/// <summary>Stage 3A: the set of building definitions, one per BuildingType.</summary>
[CreateAssetMenu(menuName = "IRONVEIL/Construction/Building Catalog", fileName = "BuildingCatalog")]
public sealed class BuildingCatalog : ScriptableObject
{
    [SerializeField] private List<BuildingDefinition> definitions = new List<BuildingDefinition>();

    public IReadOnlyList<BuildingDefinition> Definitions => definitions;

    public BuildingDefinition Get(BuildingType type)
    {
        for (int i = 0; i < definitions.Count; i++)
        {
            if (definitions[i] != null && definitions[i].Type == type)
            {
                return definitions[i];
            }
        }

        return null;
    }

#if UNITY_EDITOR
    public void EditorSetDefinitions(List<BuildingDefinition> newDefinitions)
    {
        definitions = newDefinitions ?? new List<BuildingDefinition>();
    }
#endif
}
