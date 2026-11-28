using UnityEditor;
using UnityEngine;

/// <summary>Creates/updates Resources/IronveilConstructionSettings (runtime entry to the construction data).</summary>
public static class ConstructionSettingsBuilder
{
    public const string Folder = ModularFoundationBuilder.DataDir + "/Resources";
    public const string AssetPath = Folder + "/" + ConstructionSettings.ResourcePath + ".asset";

    [MenuItem("Tools/IRONVEIL/Modular Bases/Create Construction Settings")]
    public static void BuildFromMenu() => Debug.Log(Build());

    public static void BuildBatch()
    {
        string log = Build();
        ModularFoundationBuilder.WriteOut("settings_report.txt", log);
        Debug.Log(log);
    }

    public static string Build()
    {
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder(ModularFoundationBuilder.DataDir, "Resources");
        ConstructionSettings settings = AssetDatabase.LoadAssetAtPath<ConstructionSettings>(AssetPath);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<ConstructionSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
        }

        settings.EditorInitialize(
            AssetDatabase.LoadAssetAtPath<BuildingCatalog>(ModularFoundationBuilder.CatalogPath),
            AssetDatabase.LoadAssetAtPath<InitialBuildingConfig>(ModularFoundationBuilder.InitialConfigPath),
            AssetDatabase.LoadAssetAtPath<FlakLayoutCatalog>(FlakLayoutFreezer.CatalogPath));
        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        return $"{AssetPath}: catalog={settings.BuildingCatalog != null} initial={settings.InitialConfig != null} " +
               $"layouts={settings.FlakLayouts != null} complete={settings.IsComplete}";
    }
}
