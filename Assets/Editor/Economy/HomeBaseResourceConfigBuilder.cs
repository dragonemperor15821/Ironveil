using Ironveil.Economy;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates Assets/Data/Economy/HomeBaseResourceConfig.asset from the finalized
/// <see cref="HomeBaseResourceProfile"/> values. It never overwrites an existing asset; delete the asset by hand
/// first if you need to regenerate it.
/// Batch mode: -executeMethod HomeBaseResourceConfigBuilder.BuildBatch
/// </summary>
public static class HomeBaseResourceConfigBuilder
{
    public const string FolderPath = "Assets/Data/Economy";
    public const string AssetPath = FolderPath + "/HomeBaseResourceConfig.asset";

    [MenuItem("Tools/IRONVEIL/Economy/Create Home Base Resource Config")]
    public static void BuildFromMenu() => Debug.Log(Build());

    public static void BuildBatch()
    {
        string result = Build();
        Debug.Log(result);
        EditorApplication.Exit(result.StartsWith("ERROR") ? 1 : 0);
    }

    public static string Build()
    {
        if (AssetDatabase.LoadAssetAtPath<Object>(AssetPath) != null)
            return $"SKIPPED: {AssetPath} already exists and was left unchanged.";

        HomeBaseResourceConfig config = HomeBaseResourceConfig.CreateFromProfile();
        var errors = new System.Collections.Generic.List<string>();
        if (!config.Validate(errors))
            return "ERROR: profile produced an invalid configuration: " + string.Join("; ", errors);

        if (!AssetDatabase.IsValidFolder("Assets/Data"))
            AssetDatabase.CreateFolder("Assets", "Data");
        if (!AssetDatabase.IsValidFolder(FolderPath))
            AssetDatabase.CreateFolder("Assets/Data", "Economy");

        AssetDatabase.CreateAsset(config, AssetPath);
        AssetDatabase.SaveAssets();
        return $"CREATED: {AssetPath} with {config.Entries.Count} resources.";
    }
}
