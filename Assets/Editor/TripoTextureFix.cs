
using UnityEditor;
using UnityEngine;
using System;
using System.IO;

public static class TripoTextureFix
{
    private const string FortressName = "sci-fi_fortress_3d_model";

    private const string TripoRoot = "Assets/TripoModels";

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
            Debug.LogError("IRONVEIL: URP/Lit shader not found.");
            return false;
        }

        Undo.RecordObject(material, undoName);

        material.shader = shader;
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);

        EditorUtility.SetDirty(material);

        Debug.Log(
            "IRONVEIL: Texture '" + texture.name +
            "' assigned to material '" + material.name + "'.");

        return true;
    }


    private static Texture2D FindBaseColorTexture(
        string folder,
        string filenameFragment = "basecolor")
    {
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Debug.LogError("IRONVEIL: Folder not found: " + folder);
            return null;
        }

        string[] guids = AssetDatabase.FindAssets(
            "t:Texture2D",
            new[] { folder });

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string filename = Path.GetFileNameWithoutExtension(path);

            if (filename.IndexOf(
                filenameFragment,
                StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            Texture2D texture =
                AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            if (texture != null)
            {
                Debug.Log("IRONVEIL: Found texture: " + path);
                return texture;
            }
        }

        return null;
    }


    // Finds a model folder by its exported folder name.
    private static string FindModelFolder(string folderName)
    {
        if (!AssetDatabase.IsValidFolder(TripoRoot))
        {
            Debug.LogError(
                "IRONVEIL: TripoModels folder not found: " + TripoRoot);
            return null;
        }

        string[] guids = AssetDatabase.FindAssets(
            "t:Model",
            new[] { TripoRoot });

        foreach (string guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
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

        // Fallback: search for an exact folder name.
        string[] folders = AssetDatabase.GetSubFolders(TripoRoot);

        foreach (string folder in folders)
        {
            if (string.Equals(
                Path.GetFileName(folder),
                folderName,
                StringComparison.OrdinalIgnoreCase))
                return folder;
        }

        Debug.LogError(
            "IRONVEIL: Could not find model folder named '" +
            folderName + "' under " + TripoRoot +
            ". Check the actual folder name in Unity.");

        return null;
    }


    // Fixes all materials belonging to a model folder.
    private static void FixModelFolder(
        string folderName,
        string undoName)
    {
        string modelFolder = FindModelFolder(folderName);

        if (string.IsNullOrEmpty(modelFolder))
            return;

        string[] materialGuids = AssetDatabase.FindAssets(
            "t:Material",
            new[] { modelFolder });

        int fixedCount = 0;

        foreach (string guid in materialGuids)
        {
            string materialPath = AssetDatabase.GUIDToAssetPath(guid);

            Material material =
                AssetDatabase.LoadAssetAtPath<Material>(materialPath);

            if (material == null)
                continue;

            string materialFolder =
                Path.GetDirectoryName(materialPath)?.Replace("\\", "/");

            Texture2D texture = FindBaseColorTexture(
                materialFolder,
                "basecolor");

            if (texture == null)
            {
                // Search the entire model folder, including .fbm folders.
                texture = FindBaseColorTexture(
                    modelFolder,
                    "basecolor");
            }

            if (texture == null)
            {
                Debug.LogWarning(
                    "IRONVEIL: No base-color texture found for material '" +
                    material.name + "' in " + modelFolder);
                continue;
            }

            if (ApplyBaseColor(material, texture, undoName))
                fixedCount++;
        }

        AssetDatabase.SaveAssets();

        Debug.Log(
            "IRONVEIL: " + folderName +
            " completed. Materials fixed: " + fixedCount);
    }


    // ---------------------------------------------------------
    // SELECTED MODEL
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Selected Tripo Model")]
    public static void FixSelectedModel()
    {
        GameObject selected = Selection.activeObject as GameObject;

        if (selected == null)
        {
            Debug.LogError(
                "IRONVEIL: Select the imported model asset in the Project window.");
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
                    continue;

                string folder =
                    Path.GetDirectoryName(materialPath)?.Replace("\\", "/");

                Texture2D texture =
                    FindBaseColorTexture(folder, "basecolor");

                if (texture == null)
                    continue;

                if (ApplyBaseColor(
                    material, texture, "Fix Selected Tripo Model"))
                    fixedCount++;
            }
        }

        AssetDatabase.SaveAssets();

        Debug.Log(
            "IRONVEIL: Selected model materials fixed = " + fixedCount);
    }


    // ---------------------------------------------------------
    // LEVEL 5 BASE COMMAND
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Level 5 Base Command")]
    public static void FixLevel5BaseCommand()
    {
        Material material =
            AssetDatabase.LoadAssetAtPath<Material>(Level5MaterialPath);

        Texture2D texture =
            AssetDatabase.LoadAssetAtPath<Texture2D>(Level5TexturePath);

        if (material == null || texture == null)
        {
            Debug.LogError(
                "IRONVEIL: Level 5 Base Command material or texture " +
                "was not found. Check the paths at the top of this script.");
            return;
        }

        if (ApplyBaseColor(
            material, texture, "Fix Level 5 Base Command"))
        {
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(material);
        }
    }


    // ---------------------------------------------------------
    // ORIGINAL FORTRESS
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Tripo Fortress Material")]
    public static void FixOriginalFortress()
    {
        string[] materialGuids =
            AssetDatabase.FindAssets(FortressName + " t:Material");

        foreach (string guid in materialGuids)
        {
            string materialPath = AssetDatabase.GUIDToAssetPath(guid);

            if (!materialPath.Replace("\\", "/").Contains("/Materials/"))
                continue;

            Material material =
                AssetDatabase.LoadAssetAtPath<Material>(materialPath);

            if (material == null)
                continue;

            string folder =
                Path.GetDirectoryName(materialPath)?.Replace("\\", "/");

            Texture2D texture =
                FindBaseColorTexture(folder, FortressName + "_basecolor");

            if (texture == null)
                texture = FindBaseColorTexture(
                    TripoRoot, FortressName + "_basecolor");

            if (ApplyBaseColor(
                material, texture, "Fix Original Fortress"))
            {
                AssetDatabase.SaveAssets();
                EditorGUIUtility.PingObject(material);
                return;
            }
        }

        Debug.LogError(
            "IRONVEIL: Original fortress material or texture not found.");
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

        Texture2D texture = FindBaseColorTexture(
            Level10PowerPlantFolder, "basecolor");

        if (texture == null)
        {
            Debug.LogError(
                "IRONVEIL: Power Plant base-color texture not found.");
            return;
        }

        if (ApplyBaseColor(
            material, texture, "Fix Level 10 Power Plant"))
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
    // TANK BASE WALL STRUCTURE
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Tank Base Wall Structure")]
    public static void FixTankBaseWallStructure()
    {
        FixModelFolder(
            "Tank Base Wall Structure",
            "Fix Tank Base Wall Structure");
    }


    // ---------------------------------------------------------
    // TANK BASE ROADS
    // ---------------------------------------------------------

    [MenuItem("Tools/IRONVEIL/Fix Tank Base Roads")]
    public static void FixTankBaseRoads()
    {
        FixModelFolder(
            "Tank Base Roads",
            "Fix Tank Base Roads");
    }
}
