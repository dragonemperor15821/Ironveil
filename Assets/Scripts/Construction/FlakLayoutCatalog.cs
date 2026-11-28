using System.Collections.Generic;
using UnityEngine;

/// <summary>Maps each base type (BaseSpawner naming) to its approved FlakDefensiveLayout.</summary>
[CreateAssetMenu(menuName = "IRONVEIL/Construction/Flak Layout Catalog", fileName = "FlakLayoutCatalog")]
public sealed class FlakLayoutCatalog : ScriptableObject
{
    [SerializeField] private List<FlakDefensiveLayout> layouts = new List<FlakDefensiveLayout>();

    public IReadOnlyList<FlakDefensiveLayout> Layouts => layouts;

    public FlakDefensiveLayout ForBaseType(string baseType)
    {
        foreach (FlakDefensiveLayout l in layouts)
        {
            if (l != null && l.AppliesTo(baseType)) return l;
        }

        return null;
    }

#if UNITY_EDITOR
    public void EditorSetLayouts(List<FlakDefensiveLayout> newLayouts)
    {
        layouts = newLayouts ?? new List<FlakDefensiveLayout>();
    }
#endif
}
