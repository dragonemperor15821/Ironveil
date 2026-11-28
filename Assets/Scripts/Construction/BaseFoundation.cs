using UnityEngine;

/// <summary>
/// Stage 3A: identity of a modular base foundation prefab (one per base type).
/// Data only. The production spawner does not use foundations yet.
/// </summary>
[DisallowMultipleComponent]
public sealed class BaseFoundation : MonoBehaviour
{
    [Tooltip("Base type exactly as BaseSpawner names it (Tank, Heli, Air, Naval, Rail, Heli Naval, ...).")]
    [SerializeField] private string baseType;
    [SerializeField] private BaseLayoutFamily layoutFamily;

    [Tooltip("The original (fully built) base prefab this foundation was derived from. Never modified.")]
    [SerializeField] private GameObject sourcePrefab;

    [Tooltip("Base-local front/entrance direction (unchanged convention: +Z).")]
    [SerializeField] private Vector3 frontLocal = Vector3.forward;

    [SerializeField] private FlakArcLayout.Parameters flakLayout;

    [Tooltip("Original prefab footprint (scale 1, root-local) reproduced by the Reserved Footprint marker.")]
    [SerializeField] private Vector3 reservedFootprintCenter;
    [SerializeField] private Vector3 reservedFootprintSize;

    public string BaseType => baseType;
    public BaseLayoutFamily LayoutFamily => layoutFamily;
    public GameObject SourcePrefab => sourcePrefab;
    public Vector3 FrontLocal => frontLocal;
    public FlakArcLayout.Parameters FlakLayout => flakLayout;
    public Vector3 ReservedFootprintCenter => reservedFootprintCenter;
    public Vector3 ReservedFootprintSize => reservedFootprintSize;

#if UNITY_EDITOR
    public void EditorInitialize(string newBaseType, BaseLayoutFamily newFamily, GameObject newSourcePrefab,
        FlakArcLayout.Parameters newFlakLayout, Vector3 footprintCenter, Vector3 footprintSize)
    {
        baseType = newBaseType;
        layoutFamily = newFamily;
        sourcePrefab = newSourcePrefab;
        frontLocal = Vector3.forward;
        flakLayout = newFlakLayout;
        reservedFootprintCenter = footprintCenter;
        reservedFootprintSize = footprintSize;
    }
#endif
}
