using UnityEngine;

/// <summary>
/// Stage 3A: marks the inactive, never-rendered object whose mesh bounds equal
/// the ORIGINAL base prefab's renderer footprint. BaseSpawner measures every
/// child renderer including inactive ones, so this keeps a foundation's
/// measured footprint identical to the original prefab's (placement and
/// spacing unchanged once foundations are used). Hover picking ignores it
/// because the object is inactive and its renderer is disabled.
/// </summary>
[DisallowMultipleComponent]
public sealed class ReservedFootprintMarker : MonoBehaviour
{
    [SerializeField] private string sourcePrefabName;
    [SerializeField] private Vector3 min;
    [SerializeField] private Vector3 max;

    public string SourcePrefabName => sourcePrefabName;
    public Vector3 Min => min;
    public Vector3 Max => max;

#if UNITY_EDITOR
    public void EditorInitialize(string newSourcePrefabName, Vector3 newMin, Vector3 newMax)
    {
        sourcePrefabName = newSourcePrefabName;
        min = newMin;
        max = newMax;
    }
#endif
}
