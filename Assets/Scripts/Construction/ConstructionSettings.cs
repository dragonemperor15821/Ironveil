using UnityEngine;

/// <summary>
/// Runtime entry point to the construction data (loaded from Resources, the
/// same pattern as the Base Menu's FlakBuildOptionConfig): the building catalog,
/// the starting configuration (2 x L1 Flak + 1 x L1 Refinery) and the approved
/// Flak layouts. Data only.
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Construction/Construction Settings", fileName = "IronveilConstructionSettings")]
public sealed class ConstructionSettings : ScriptableObject
{
    public const string ResourcePath = "IronveilConstructionSettings";

    [SerializeField] private BuildingCatalog buildingCatalog;
    [SerializeField] private InitialBuildingConfig initialConfig;
    [SerializeField] private FlakLayoutCatalog flakLayouts;

    public BuildingCatalog BuildingCatalog => buildingCatalog;
    public InitialBuildingConfig InitialConfig => initialConfig;
    public FlakLayoutCatalog FlakLayouts => flakLayouts;
    public bool IsComplete => buildingCatalog != null && initialConfig != null && flakLayouts != null;

    private static ConstructionSettings cached;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        cached = null;
    }

    /// <summary>The project settings asset (Resources/IronveilConstructionSettings), or null.</summary>
    public static ConstructionSettings Load()
    {
        if (cached == null) cached = Resources.Load<ConstructionSettings>(ResourcePath);
        return cached;
    }

#if UNITY_EDITOR
    public void EditorInitialize(BuildingCatalog catalog, InitialBuildingConfig config, FlakLayoutCatalog layouts)
    {
        buildingCatalog = catalog;
        initialConfig = config;
        flakLayouts = layouts;
    }
#endif
}
