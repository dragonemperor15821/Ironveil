
using UnityEditor;
using UnityEngine;
using System;
using System.IO;

public static class TripoTextureFix
{
    private const string TripoRoot = "Assets/TripoModels";

    private const string TankBaseStructureFolder =
        "Assets/TripoModels/Tank Base Structure";

    private const string TankBaseStructureTextureName =
        "circular_arena_3d_model_basecolor";

    private const string HelipadFolder =
        "Assets/TripoModels/Helipad";

    private const string Level5MaterialPath =
        "Assets/TripoModels/Level 5 Base Command/Materials/futuristic_tower_3d_model.mat";

    private const string Level5TexturePath =
        "Assets/TripoModels/Level 5 Base Command/futuristic_tower_3d_model.fbm/futuristic_tower_3d_model_basecolor.JPEG";

    private const string Level10PowerPlantFolder =
        "Assets/TripoModels/Level 10 Power Plant";

    private const string Level10PowerPlantMaterialPath =
        "Assets/TripoModels/Level 10 Power Plant/Materials/industrial_refinery_3d_model.mat";


    // ---------------------------------------------------------
    // SHARED HELPERS
    // ---------------------------------------------------------

    private static bool ApplyBaseColor(
        Material material,
        Texture2D texture,
        string undoName)
    {
        if (material == null)
        {
            Debug.LogError("IRONVEIL: Material not found.");
            return false;
        }

        if (texture == null)
        {
            Debug.LogError("IRONVEIL: Base-color texture not found.");
            return false;
        }

        Shader shader =
            Shader.Find("Universal Render Pipeline/Lit");

        if (shader == null)
        {
            Debug.LogError(
                "IRONVEIL: Universal Render Pipeline/Lit shader not found.");
            return false;
        }

        Undo.RecordObject(material, undoName);

        material.shader = shader;
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);

        EditorUtility.SetDirty(material);

        Debug.Log(
            "IRONVEIL: Assigned texture '" + texture.name +
            "' to material '" + material.name + "'.");

        return true;
    }


    private static Texture2D FindBaseColorTexture(
        string folder,
        string filenameFragment = "basecolor")
    {
        if (string.IsNullOrEmpty(folder) ||
            !AssetDatabase.IsValidFolder(folder))
        {
            Debug.LogWarning(
                "IRONVEIL: Texture search folder not found: " + folder);
            return null;
        }

        string[] guids = AssetDatabase.FindAssets(
            "t:Texture2D",
            new[] { folder });

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);

            string filename =
                Path.GetFileNameWithoutExtension(path);

            if (filename.IndexOf(
                filenameFragment,
                StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            Texture2D texture =
                AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            if (texture != null)
            {
                Debug.Log(
                    "IRONVEIL: Found texture at: " + path);

                return texture;
            }
        }

        Debug.LogWarning(
            "IRONVEIL: No texture containing '" +
            filenameFragment + "' found in " + folder);

        return null;
    }


    private static string FindModelFolder(string folderName)
    {
        if (!AssetDatabase.IsValidFolder(TripoRoot))
        {
            Debug.LogError(
                "IRONVEIL: TripoModels folder not found: " +
                TripoRoot);
            return null;
        }

        string[] guids = AssetDatabase.FindAssets(
            "t:Model",
            new[] { TripoRoot });

        foreach (string guid in guids)
        {
            string assetPath =
                AssetDatabase.GUIDToAssetPath(guid);

            string directory =
                Path.GetDirectoryName(assetPath)?.Replace("\\", "/");

            if (string.IsNullOrEmpty(directory))
                continue;

            if (string.Equals(
                Path.GetFileName(directory),
                folderName,
                StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        string[] folders =
            AssetDatabase.GetSubFolders(TripoRoot);

        foreach (string folder in folders)
        {
            if (string.Equals(
                Path.GetFileName(folder),
                folderName,
                StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        Debug.LogError(
            "IRONVEIL: Could not find model folder '" +
            folderName + "' under " + TripoRoot);

        return null;
    }


    private static void FixModelFolder(
        string folderName,
        string undoName)
    {
        string modelFolder =
            FindModelFolder(folderName);

        if (string.IsNullOrEmpty(modelFolder))
            return;

        string[] materialGuids =
            AssetDatabase.FindAssets(
                "t:Material",
                new[] { modelFolder });

        int fixedCount = 0;

        foreach (string guid in materialGuids)
        {
            string materialPath =
                AssetDatabase.GUIDToAssetPath(guid);

            Material material =
                AssetDatabase.LoadAssetAtPath<Material>(
                    materialPath);

            if (material == null)
                continue;

            string materialFolder =
                Path.GetDirectoryName(materialPath)
                    ?.Replace("\\", "/");

            Texture2D texture =
                FindBaseColorTexture(
                    materialFolder,
                    "basecolor");

            if (texture == null)
            {
                texture = FindBaseColorTexture(
                    modelFolder,
                    "basecolor");
            }

            if (texture == null)
            {
                Debug.LogWarning(
                    "IRONVEIL: No base-color texture found for material '" +
                    material.name + "'.");

                continue;
            }

            if (ApplyBaseColor(
                material,
                texture,
                undoName))
            {
                fixedCount++;
            }
        }

        AssetDatabase.SaveAssets();

        if (fixedCount > 0)
        {
            Debug.Log(
                "IRONVEIL: " + folderName +
                " completed. Materials fixed: " + fixedCount);
        }
        else
        {
            Debug.LogWarning(
                "IRONVEIL: " + folderName +
                " finished, but no materials were changed.");
        }
    }


    // ---------------------------------------------------------
    // FIX SELECTED TRIPO MODEL
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Selected Tripo Model")]
    public static void FixSelectedModel()
    {
        GameObject selected =
            Selection.activeObject as GameObject;

        if (selected == null)
        {
            Debug.LogError(
                "IRONVEIL: Select an imported model asset " +
                "in the Project window.");

            return;
        }

        Renderer[] renderers =
            selected.GetComponentsInChildren<Renderer>(true);

        int fixedCount = 0;

        foreach (Renderer renderer in renderers)
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null)
                    continue;

                string materialPath =
                    AssetDatabase.GetAssetPath(material);

                if (string.IsNullOrEmpty(materialPath) ||
                    !materialPath.StartsWith(
                        TripoRoot + "/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string materialFolder =
                    Path.GetDirectoryName(materialPath)
                        ?.Replace("\\", "/");

                Texture2D texture =
                    FindBaseColorTexture(
                        materialFolder,
                        "basecolor");

                if (texture == null)
                    continue;

                if (ApplyBaseColor(
                    material,
                    texture,
                    "Fix Selected Tripo Model"))
                {
                    fixedCount++;
                }
            }
        }

        AssetDatabase.SaveAssets();

        Debug.Log(
            "IRONVEIL: Selected model materials fixed: " +
            fixedCount);
    }


    // ---------------------------------------------------------
    // LEVEL 5 BASE COMMAND
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Level 5 Base Command")]
    public static void FixLevel5BaseCommand()
    {
        Material material =
            AssetDatabase.LoadAssetAtPath<Material>(
                Level5MaterialPath);

        Texture2D texture =
            AssetDatabase.LoadAssetAtPath<Texture2D>(
                Level5TexturePath);

        if (material == null || texture == null)
        {
            Debug.LogError(
                "IRONVEIL: Level 5 Base Command material or texture " +
                "was not found. Check the configured paths.");

            return;
        }

        if (ApplyBaseColor(
            material,
            texture,
            "Fix Level 5 Base Command"))
        {
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(material);
        }
    }


    // ---------------------------------------------------------
    // LEVEL 10 POWER PLANT
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Level 10 Power Plant")]
    public static void FixLevel10PowerPlant()
    {
        Material material =
            AssetDatabase.LoadAssetAtPath<Material>(
                Level10PowerPlantMaterialPath);

        if (material == null)
        {
            Debug.LogError(
                "IRONVEIL: Power Plant material not found: " +
                Level10PowerPlantMaterialPath);

            return;
        }

        Texture2D texture =
            FindBaseColorTexture(
                Level10PowerPlantFolder,
                "basecolor");

        if (texture == null)
        {
            Debug.LogError(
                "IRONVEIL: Power Plant base-color texture not found.");

            return;
        }

        if (ApplyBaseColor(
            material,
            texture,
            "Fix Level 10 Power Plant"))
        {
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(material);
            Selection.activeObject = material;
        }
    }


    // ---------------------------------------------------------
    // LEVEL 3 GROUP EXTENSION
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Level 3 Group Extension")]
    public static void FixLevel3GroupExtension()
    {
        FixModelFolder(
            "Level 3 Group Extension",
            "Fix Level 3 Group Extension");
    }


    // ---------------------------------------------------------
    // LEVEL 3 MILITARY OFFICE
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Level 3 Military Office")]
    public static void FixLevel3MilitaryOffice()
    {
        FixModelFolder(
            "Level 3 Military Office",
            "Fix Level 3 Military Office");
    }


    // ---------------------------------------------------------
    // LEVEL 3 RESOURCE OPTIMISER
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Level 3 Resource Optimiser")]
    public static void FixLevel3ResourceOptimiser()
    {
        FixModelFolder(
            "Level 3 Resource Optimiser",
            "Fix Level 3 Resource Optimiser");
    }


    // ---------------------------------------------------------
    // LEVEL 6 BASE FLAK
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix L6 Base Flak")]
    public static void FixL6BaseFlak()
    {
        FixModelFolder(
            "Level 6 Base Turret",
            "Fix L6 Base Flak");
    }


    // ---------------------------------------------------------
    // HELIPAD
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Helipad")]
    public static void FixHelipad()
    {
        GameObject target =
            Selection.activeGameObject;

        if (target == null ||
            !string.Equals(
                target.name,
                "Helipad",
                StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogError(
                "IRONVEIL: Select the Helipad GameObject in the " +
                "Hierarchy before running this command.");

            return;
        }

        if (!AssetDatabase.IsValidFolder(HelipadFolder))
        {
            Debug.LogError(
                "IRONVEIL: Helipad model folder not found: " +
                HelipadFolder +
                ". Check that the imported model folder is named " +
                "'Helipad' and is inside Assets/TripoModels.");

            return;
        }

        Texture2D texture =
            FindBaseColorTexture(
                HelipadFolder,
                "basecolor");

        if (texture == null)
        {
            Debug.LogError(
                "IRONVEIL: No texture containing 'basecolor' was found " +
                "inside " + HelipadFolder +
                ". Check the texture filename and imported files.");

            return;
        }

        Renderer[] renderers =
            target.GetComponentsInChildren<Renderer>(true);

        if (renderers.Length == 0)
        {
            Debug.LogError(
                "IRONVEIL: No Renderer components were found on " +
                "Helipad or its children.");

            return;
        }

        int fixedCount = 0;

        foreach (Renderer renderer in renderers)
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null)
                    continue;

                if (ApplyBaseColor(
                    material,
                    texture,
                    "Fix Helipad"))
                {
                    fixedCount++;
                }
            }
        }

        AssetDatabase.SaveAssets();

        if (fixedCount > 0)
        {
            Debug.Log(
                "IRONVEIL: Helipad texture assignment successful. " +
                "Materials fixed: " + fixedCount +
                ". Texture: " + AssetDatabase.GetAssetPath(texture));

            EditorGUIUtility.PingObject(texture);
            Selection.activeObject = texture;
        }
        else
        {
            Debug.LogWarning(
                "IRONVEIL: Helipad was found, but no materials were " +
                "changed. Check its Renderer and material assignments.");
        }
    }


    // ---------------------------------------------------------
    // TANK BASE STRUCTURE
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Tank Base Structure")]
    public static void FixTankBaseStructure()
    {
        GameObject target =
            Selection.activeGameObject;

        if (target == null ||
            target.name != "Tank Base Structure")
        {
            Debug.LogError(
                "IRONVEIL: Select Tank Base Structure " +
                "in the Hierarchy before running this command.");

            return;
        }

        if (!AssetDatabase.IsValidFolder(
            TankBaseStructureFolder))
        {
            Debug.LogError(
                "IRONVEIL: Model folder not found: " +
                TankBaseStructureFolder);

            return;
        }

        Texture2D texture =
            FindBaseColorTexture(
                TankBaseStructureFolder,
                TankBaseStructureTextureName);

        if (texture == null)
        {
            Debug.LogError(
                "IRONVEIL: Could not find the texture '" +
                TankBaseStructureTextureName +
                "' inside " + TankBaseStructureFolder);

            return;
        }

        Renderer[] renderers =
            target.GetComponentsInChildren<Renderer>(true);

        int fixedCount = 0;

        foreach (Renderer renderer in renderers)
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null)
                    continue;

                if (ApplyBaseColor(
                    material,
                    texture,
                    "Fix Tank Base Structure"))
                {
                    fixedCount++;
                }
            }
        }

        AssetDatabase.SaveAssets();

        if (fixedCount > 0)
        {
            Debug.Log(
                "IRONVEIL: Tank Base Structure texture assignment " +
                "successful. Materials fixed: " + fixedCount);
        }
        else
        {
            Debug.LogWarning(
                "IRONVEIL: Tank Base Structure was found, but " +
                "no materials were changed. Check its Renderer " +
                "and material assignments.");
        }
    }


    // ---------------------------------------------------------
    // AIR BASE
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Air Base")]
    public static void FixAirBase()
    {
        GameObject target = Selection.activeGameObject;

        if (target == null ||
            !string.Equals(
                target.name,
                "Air Base",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                target.name,
                "Air Base Master",
                StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogError(
                "IRONVEIL: Select the Air Base or Air Base Master " +
                "root GameObject in the Hierarchy before running " +
                "this command.");

            return;
        }

        Renderer[] renderers =
            target.GetComponentsInChildren<Renderer>(true);

        if (renderers.Length == 0)
        {
            Debug.LogError(
                "IRONVEIL: No Renderer components found under " +
                target.name + ".");

            return;
        }

        int fixedCount = 0;
        int missingTextureCount = 0;
        int skippedCount = 0;

        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];

                if (material == null)
                {
                    skippedCount++;
                    continue;
                }

                string materialPath =
                    AssetDatabase.GetAssetPath(material);

                if (string.IsNullOrEmpty(materialPath))
                {
                    Debug.LogWarning(
                        "IRONVEIL: Material '" + material.name +
                        "' on '" + renderer.name +
                        "' is not a saved asset. Skipping it.");

                    skippedCount++;
                    continue;
                }

                string materialFolder =
                    Path.GetDirectoryName(materialPath)
                        ?.Replace("\\", "/");

                Texture2D texture =
                    FindBaseColorTexture(
                        materialFolder,
                        "basecolor");

                if (texture == null)
                {
                    string modelFolder =
                        FindModelFolderForAirBaseMaterial(
                            materialPath);

                    if (!string.IsNullOrEmpty(modelFolder))
                    {
                        texture = FindBaseColorTexture(
                            modelFolder,
                            "basecolor");
                    }
                }

                if (texture == null)
                {
                    Debug.LogWarning(
                        "IRONVEIL: No base-color texture found for " +
                        "material '" + material.name +
                        "' at " + materialPath +
                        ". Its existing appearance was left unchanged.");

                    missingTextureCount++;
                    continue;
                }

                if (ApplyBaseColor(
                    material,
                    texture,
                    "Fix Air Base"))
                {
                    fixedCount++;
                }
            }
        }

        AssetDatabase.SaveAssets();

        Debug.Log(
            "IRONVEIL: Air Base texture pass completed. " +
            "Materials fixed: " + fixedCount +
            "; missing textures: " + missingTextureCount +
            "; skipped materials: " + skippedCount + ".");

        if (fixedCount > 0)
        {
            Debug.Log(
                "IRONVEIL: Air Base materials updated successfully.");
        }
        else
        {
            Debug.LogWarning(
                "IRONVEIL: No Air Base materials were updated. " +
                "Check the Console for missing texture paths.");
        }
    }


    private static string FindModelFolderForAirBaseMaterial(
        string materialPath)
    {
        if (string.IsNullOrEmpty(materialPath))
            return null;

        string normalizedPath =
            materialPath.Replace("\\", "/");

        string tripoRoot =
            TripoRoot.TrimEnd('/');

        if (!normalizedPath.StartsWith(
            tripoRoot + "/",
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relativePath =
            normalizedPath.Substring(tripoRoot.Length + 1);

        string[] parts = relativePath.Split('/');

        if (parts.Length == 0)
            return null;

        string candidate =
            tripoRoot + "/" + parts[0];

        if (AssetDatabase.IsValidFolder(candidate))
            return candidate;

        return null;
    }


    // ---------------------------------------------------------
    // NAVAL HANGAR
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Naval Hangar")]
    public static void FixNavalHangar()
    {
        GameObject target = Selection.activeGameObject;

        if (target == null)
        {
            Debug.LogError(
                "IRONVEIL: Select the Naval Hangar root GameObject " +
                "in the Hierarchy before running this command.");
            return;
        }

        Renderer[] renderers =
            target.GetComponentsInChildren<Renderer>(true);

        if (renderers.Length == 0)
        {
            Debug.LogError(
                "IRONVEIL: No Renderer components found under '" +
                target.name + "'.");
            return;
        }

        int fixedCount = 0;
        int missingTextureCount = 0;
        int skippedCount = 0;

        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];

                if (material == null)
                {
                    skippedCount++;
                    continue;
                }

                string materialPath =
                    AssetDatabase.GetAssetPath(material);

                if (string.IsNullOrEmpty(materialPath) ||
                    !materialPath.StartsWith(
                        TripoRoot + "/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning(
                        "IRONVEIL: Material '" + material.name +
                        "' on '" + renderer.name +
                        "' is not a saved asset under " + TripoRoot +
                        ". Skipping it.");
                    skippedCount++;
                    continue;
                }

                string modelFolder =
                    FindModelFolderForAirBaseMaterial(materialPath);

                Texture2D texture = null;

                // Search the model folder, including nested .fbm folders.
                if (!string.IsNullOrEmpty(modelFolder))
                {
                    texture = FindBaseColorTexture(
                        modelFolder, "basecolor");
                }

                // Fallback: search the material's own folder.
                if (texture == null)
                {
                    string materialFolder =
                        Path.GetDirectoryName(materialPath)
                            ?.Replace("\\", "/");

                    texture = FindBaseColorTexture(
                        materialFolder, "basecolor");
                }

                if (texture == null)
                {
                    Debug.LogWarning(
                        "IRONVEIL: No texture with 'basecolor' in its " +
                        "filename was found for material '" +
                        material.name + "' at " + materialPath +
                        ". Existing appearance left unchanged.");
                    missingTextureCount++;
                    continue;
                }

                if (ApplyBaseColor(
                    material,
                    texture,
                    "Fix Naval Hangar"))
                {
                    fixedCount++;
                }
            }
        }

        AssetDatabase.SaveAssets();

        Debug.Log(
            "IRONVEIL: Naval Hangar texture pass completed. " +
            "Materials fixed: " + fixedCount +
            "; missing textures: " + missingTextureCount +
            "; skipped materials: " + skippedCount + ".");

        if (fixedCount > 0)
        {
            Debug.Log(
                "IRONVEIL: Naval Hangar base-color textures assigned.");
        }
        else
        {
            Debug.LogWarning(
                "IRONVEIL: No Naval Hangar materials were updated. " +
                "Check the Console for texture paths and ensure the " +
                "exported model and textures are inside " + TripoRoot + ".");
        }
    }


    // ---------------------------------------------------------
    // RAIL HANGAR
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Rail Hangar")]
    public static void FixRailHangar()
    {
        GameObject target = Selection.activeGameObject;

        if (target == null)
        {
            Debug.LogError(
                "IRONVEIL: Select the Rail Hangar root GameObject " +
                "in the Hierarchy before running this command.");
            return;
        }

        Renderer[] renderers =
            target.GetComponentsInChildren<Renderer>(true);

        if (renderers.Length == 0)
        {
            Debug.LogError(
                "IRONVEIL: No Renderer components found under '" +
                target.name + "'.");
            return;
        }

        int fixedCount = 0;
        int missingTextureCount = 0;
        int skippedCount = 0;

        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];

                if (material == null)
                {
                    skippedCount++;
                    continue;
                }

                string materialPath =
                    AssetDatabase.GetAssetPath(material);

                if (string.IsNullOrEmpty(materialPath) ||
                    !materialPath.StartsWith(
                        TripoRoot + "/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning(
                        "IRONVEIL: Material '" + material.name +
                        "' on '" + renderer.name +
                        "' is not a saved asset under " + TripoRoot +
                        ". Skipping it.");
                    skippedCount++;
                    continue;
                }

                string modelFolder =
                    FindModelFolderForAirBaseMaterial(materialPath);

                Texture2D texture = null;

                // Search the model folder, including nested .fbm folders.
                if (!string.IsNullOrEmpty(modelFolder))
                {
                    texture = FindBaseColorTexture(
                        modelFolder, "basecolor");
                }

                // Fallback: search the material's own folder.
                if (texture == null)
                {
                    string materialFolder =
                        Path.GetDirectoryName(materialPath)
                            ?.Replace("\\", "/");

                    texture = FindBaseColorTexture(
                        materialFolder, "basecolor");
                }

                if (texture == null)
                {
                    Debug.LogWarning(
                        "IRONVEIL: No texture containing 'basecolor' was " +
                        "found for Rail Hangar material '" + material.name +
                        "' at " + materialPath +
                        ". Its existing appearance was left unchanged.");
                    missingTextureCount++;
                    continue;
                }

                if (ApplyBaseColor(
                    material,
                    texture,
                    "Fix Rail Hangar"))
                {
                    fixedCount++;
                }
            }
        }

        AssetDatabase.SaveAssets();

        Debug.Log(
            "IRONVEIL: Rail Hangar texture pass completed. " +
            "Materials fixed: " + fixedCount +
            "; missing textures: " + missingTextureCount +
            "; skipped materials: " + skippedCount + ".");

        if (fixedCount > 0)
        {
            Debug.Log(
                "IRONVEIL: Rail Hangar base-color textures assigned.");
        }
        else
        {
            Debug.LogWarning(
                "IRONVEIL: No Rail Hangar materials were updated. " +
                "Check the Console for missing texture paths and ensure " +
                "the exported model and textures are inside " + TripoRoot + ".");
        }
    }

}
