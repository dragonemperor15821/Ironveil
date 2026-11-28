using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Stage 3A: renders review screenshots of the foundations with temporary
/// building/Flak placements. Every object is temporary; no asset or scene is
/// saved. Output: STAGE3A_OUT/screenshots (or Logs/Stage3A/screenshots).
/// </summary>
public static class Stage3AScreenshots
{
    private const float BaseScale = 6.5f;
    private static readonly Vector3 Origin = new Vector3(5000f, 0f, 5000f);
    private static readonly List<UnityEngine.Object> temporaries = new List<UnityEngine.Object>();

    [MenuItem("Tools/IRONVEIL/Modular Bases/Render Review Screenshots")]
    public static void RenderFromMenu()
    {
        RenderAll(AssetDatabase.LoadAssetAtPath<BuildingCatalog>(ModularFoundationBuilder.CatalogPath),
            AssetDatabase.LoadAssetAtPath<InitialBuildingConfig>(ModularFoundationBuilder.InitialConfigPath));
    }

    public static void RenderAll(BuildingCatalog catalog, InitialBuildingConfig config)
    {
        string dir = Environment.GetEnvironmentVariable("STAGE3A_OUT");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "Stage3A");
        dir = Path.Combine(dir, "screenshots");
        Directory.CreateDirectory(dir);

        try
        {
            SetupEnvironment();
            FlakModelComparison(catalog, dir);
            OriginalShot("Heli Base", dir);
            OriginalShot("Air Base", dir);
            Shot(catalog, config, "Heli", true, dir, top: true, front: true, oblique: true);
            Shot(catalog, config, "Heli", false, dir, top: true, front: true, oblique: true);
            Shot(catalog, config, "Naval", true, dir, top: true, front: false, oblique: false);
            Shot(catalog, config, "Tank", true, dir, top: true, front: false, oblique: false);
            Shot(catalog, config, "Air", true, dir, top: true, front: true, oblique: true);
            Shot(catalog, config, "Air", false, dir, top: true, front: false, oblique: true);
        }
        finally
        {
            foreach (UnityEngine.Object o in temporaries) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            temporaries.Clear();
        }
    }

    /// <summary>
    /// Clean (NO guide overlays) original-vs-final comparison for all nine base
    /// types: ORIGINAL prefab vs foundation with the full 28-Flak capacity
    /// constructed through BaseFlakDefence (+ the starting Factory/Refinery),
    /// same cameras; plus the initial 2-Flak + 1-Refinery state.
    /// Output: STAGE3A_OUT/final_screenshots.
    /// </summary>
    public static void RenderFinalComparison(BuildingCatalog catalog, InitialBuildingConfig config, FlakLayoutCatalog flakCatalog)
    {
        string dir = Environment.GetEnvironmentVariable("STAGE3A_OUT");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "Stage3A");
        dir = Path.Combine(dir, "final_screenshots");
        Directory.CreateDirectory(dir);
        try
        {
            SetupEnvironment();
            foreach (ModularFoundationBuilder.BaseSpec spec in ModularFoundationBuilder.Bases)
            {
                string tag = spec.baseType.Replace(" ", "");
                GameObject original = AssetDatabase.LoadAssetAtPath<GameObject>(spec.originalPath);
                GameObject foundation = AssetDatabase.LoadAssetAtPath<GameObject>(ModularFoundationBuilder.FoundationPath(spec.baseType));
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    if (mode == 2 && spec.baseType != "Heli" && spec.baseType != "Air" && spec.baseType != "Tank") continue;
                    GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(mode == 0 ? original : foundation);
                    inst.transform.position = Origin;
                    inst.transform.localScale = Vector3.one * BaseScale;
                    try
                    {
                        if (mode > 0)
                        {
                            BaseBuildingState state = inst.GetComponent<BaseBuildingState>();
                            BaseFlakDefence defence = inst.AddComponent<BaseFlakDefence>();
                            defence.Bind(flakCatalog.ForBaseType(spec.baseType), out _);
                            state.ApplyInitialConfiguration(config, catalog, out _);
                            if (mode == 1)
                            {
                                while (defence.Construct(catalog.Get(BuildingType.Flak), 1) != null) { }
                            }
                        }

                        string name = mode == 0 ? "ORIGINAL" : mode == 1 ? "FINAL_28Flak_capacity" : "FINAL_initial_2Flak_1Refinery";
                        Render(Path.Combine(dir, $"{tag}_{name}_top.png"), Origin + Vector3.up * 30f, Quaternion.Euler(90f, 0f, 0f), true, 4.4f, 1400);
                        Vector3 eye = Origin + new Vector3(4.6f, 4.4f, 7.6f);
                        Render(Path.Combine(dir, $"{tag}_{name}_oblique.png"), eye,
                            Quaternion.LookRotation(Origin + new Vector3(0f, 0f, 0.6f) - eye, Vector3.up), false, 0f, 1400);
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(inst);
                    }
                }
            }
        }
        finally
        {
            foreach (UnityEngine.Object o in temporaries) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            temporaries.Clear();
        }
    }

    private static T Temp<T>(T o) where T : UnityEngine.Object
    {
        temporaries.Add(o);
        return o;
    }

    private static Material UnlitMaterial(Color c)
    {
        Material m = Temp(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
        m.SetColor("_BaseColor", c);
        return m;
    }

    private static void SetupEnvironment()
    {
        GameObject light = Temp(new GameObject("TEMP_Stage3A_Light"));
        Light l = light.AddComponent<Light>();
        l.type = LightType.Directional;
        l.intensity = 1.3f;
        l.shadows = LightShadows.Soft;
        light.transform.rotation = Quaternion.Euler(55f, -35f, 0f);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.6f);

        GameObject ground = Temp(GameObject.CreatePrimitive(PrimitiveType.Plane));
        ground.name = "TEMP_Stage3A_Terrain";
        ground.transform.position = Origin + new Vector3(0f, -0.002f, 0f);
        ground.transform.localScale = Vector3.one * 3f;
        Material gm = Temp(new Material(Shader.Find("Universal Render Pipeline/Lit")));
        gm.SetColor("_BaseColor", new Color(0.62f, 0.66f, 0.68f));
        ground.GetComponent<Renderer>().sharedMaterial = gm;
    }

    private static void Shot(BuildingCatalog catalog, InitialBuildingConfig config, string baseType, bool fullPreview,
        string dir, bool top, bool front, bool oblique)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModularFoundationBuilder.FoundationPath(baseType));
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.transform.position = Origin;
        inst.transform.localScale = Vector3.one * BaseScale;
        List<GameObject> overlay = new List<GameObject>();
        try
        {
            BaseBuildingState state = inst.GetComponent<BaseBuildingState>();
            BaseFoundation bf = inst.GetComponent<BaseFoundation>();
            if (fullPreview)
            {
                foreach (BuildingSlot s in state.Slots)
                {
                    BuildingPlacement.Place(s, catalog.Get(s.AcceptedTypes[0]), 1);
                }
            }
            else
            {
                state.ApplyInitialConfiguration(config, catalog, out _);
            }

            // Overlays: slot footprints (yellow buildings, cyan Flaks; dim when occupied) and corridor lines.
            Material yellow = UnlitMaterial(new Color(1f, 0.85f, 0.1f));
            Material cyan = UnlitMaterial(new Color(0.1f, 0.9f, 1f));
            Material magenta = UnlitMaterial(new Color(1f, 0.2f, 0.8f));
            foreach (BuildingSlot s in state.Slots)
            {
                bool flak = s.Accepts(BuildingType.Flak);
                if (fullPreview && flak) continue;
                Vector3 lp = inst.transform.InverseTransformPoint(s.transform.position);
                float yaw = s.transform.localEulerAngles.y;
                var rect = new ModularFoundationGeometry.Rect2(new Vector2(lp.x, lp.z), s.ReservedFootprint, yaw);
                Vector2[] c = rect.Corners();
                Vector3[] pts = c.Select(k => inst.transform.TransformPoint(new Vector3(k.x, lp.y + 0.004f, k.y))).ToArray();
                overlay.Add(Line(pts, true, flak ? cyan : yellow, flak ? 0.012f : 0.02f));
            }

            FlakArcLayout.Parameters p = bf.FlakLayout;
            float half = p.corridorOffset - 0.025f;
            float zFrom = p.centre.y + 0.35f, zTo = p.centre.y + FlakArcLayout.RowRadius(p, 2) + 0.08f;
            foreach (float sx in new[] { -1f, 1f })
            {
                Vector3 a = inst.transform.TransformPoint(new Vector3(p.centre.x + sx * half, 0.03f, zFrom));
                Vector3 b = inst.transform.TransformPoint(new Vector3(p.centre.x + sx * half, 0.03f, zTo));
                overlay.Add(Line(new[] { a, b }, false, magenta, 0.015f));
            }

            Vector3 centre = inst.transform.TransformPoint(new Vector3(p.centre.x, 0f, p.centre.y));
            string tag = $"{baseType.Replace(" ", "")}_{(fullPreview ? "all28_L1preview" : "initial_2flak_1factory")}";
            if (top) Render(Path.Combine(dir, tag + "_top.png"), centre + Vector3.up * 30f, Quaternion.Euler(90f, 0f, 0f), true, 4.3f, 1600);
            if (front)
            {
                Vector3 fc = centre + new Vector3(0f, 30f, (p.innerRadius + 0.03f) * BaseScale - 0.6f);
                Render(Path.Combine(dir, tag + "_front_zoom.png"), fc, Quaternion.Euler(90f, 0f, 0f), true, 1.9f, 1600);
            }

            if (oblique)
            {
                Vector3 eye = centre + new Vector3(0f, 4.2f, 8.2f);
                Quaternion rot = Quaternion.LookRotation(centre + new Vector3(0f, 0f, 1.2f) - eye, Vector3.up);
                Render(Path.Combine(dir, tag + "_oblique.png"), eye, rot, false, 0f, 1600);
            }
        }
        finally
        {
            foreach (GameObject o in overlay) UnityEngine.Object.DestroyImmediate(o);
            UnityEngine.Object.DestroyImmediate(inst);
        }
    }

    /// <summary>
    /// Stage 3A.1: the registered Flak level models side by side, each placed
    /// exactly as BuildingPlacement places it (slot facing +Z = up in the top
    /// view, model facing offset applied, 0.05 relative to a 6.5 base root).
    /// </summary>
    private static void FlakModelComparison(BuildingCatalog catalog, string dir)
    {
        BuildingDefinition flak = catalog.Get(BuildingType.Flak);
        GameObject root = new GameObject("TEMP_Stage3A_FlakCompare");
        root.transform.position = Origin + new Vector3(30f, 0f, 0f);
        root.transform.localScale = Vector3.one * BaseScale;
        try
        {
            float x = -0.04f;
            foreach (var lv in flak.Levels)
            {
                GameObject slotGo = new GameObject("Slot L" + lv.level);
                slotGo.transform.SetParent(root.transform, false);
                slotGo.transform.localPosition = new Vector3(x, 0f, 0f);
                BuildingSlot slot = slotGo.AddComponent<BuildingSlot>();
                slot.EditorInitialize("L" + lv.level, new[] { BuildingType.Flak }, flak.InstanceScale, new Vector2(0.05f, 0.05f), true);
                BuildingPlacement.Place(slot, flak, lv.level);
                x += 0.08f;
            }

            Vector3 c = root.transform.position;
            Render(Path.Combine(dir, "Flak_L1_vs_L6_top.png"), c + Vector3.up * 10f, Quaternion.Euler(90f, 0f, 0f), true, 0.45f, 1200);
            Vector3 eye = c + new Vector3(0.25f, 0.55f, -1.1f);
            Render(Path.Combine(dir, "Flak_L1_vs_L6_oblique.png"), eye, Quaternion.LookRotation(c + Vector3.up * 0.08f - eye), false, 0f, 1200);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>Reference render of an ORIGINAL base prefab (read-only instance).</summary>
    private static void OriginalShot(string prefabName, string dir)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Prefabs/{prefabName}.prefab");
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.transform.position = Origin;
        inst.transform.localScale = Vector3.one * BaseScale;
        try
        {
            string tag = "ORIGINAL_" + prefabName.Replace(" ", "");
            Vector3 centre = Origin;
            Render(Path.Combine(dir, tag + "_top.png"), centre + Vector3.up * 30f, Quaternion.Euler(90f, 0f, 0f), true, 4.3f, 1600);
            Render(Path.Combine(dir, tag + "_front_zoom.png"), centre + new Vector3(0f, 30f, 0.51f * BaseScale - 0.6f),
                Quaternion.Euler(90f, 0f, 0f), true, 1.9f, 1600);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(inst);
        }
    }

    private static GameObject Line(Vector3[] pts, bool loop, Material m, float width)
    {
        GameObject go = new GameObject("TEMP_Stage3A_Line");
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.positionCount = pts.Length;
        lr.SetPositions(pts);
        lr.widthMultiplier = width;
        lr.sharedMaterial = m;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.alignment = LineAlignment.View;
        return go;
    }

    private static void Render(string file, Vector3 pos, Quaternion rot, bool ortho, float orthoSize, int size)
    {
        GameObject camGo = new GameObject("TEMP_Stage3A_Camera");
        try
        {
            Camera cam = camGo.AddComponent<Camera>();
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.orthographic = ortho;
            cam.orthographicSize = orthoSize;
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.18f, 0.2f);
            RenderTexture rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(camGo);
        }
    }
}
