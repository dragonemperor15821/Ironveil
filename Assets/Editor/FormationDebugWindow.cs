using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Stage 2d (editor-only): Tools > IRONVEIL > Formation Debug.
// Draws the generated facing pairs and clusters in the Scene view from the
// records BaseSpawner already holds (via FormationDebugGeometry). Settings are
// per-user EditorPrefs (all OFF by default) - nothing is stored in a scene or
// the project, no GameObjects are created and generation is never touched,
// except by the explicit Play-mode "Regenerate" buttons, whose switch changes
// Unity reverts when Play mode ends. Geometric reference only: not a weapon,
// targeting or combat system.

[InitializeOnLoad]
public static class FormationDebugSceneDrawer
{
    const string Prefix = "IRONVEIL.FormationDebug.";
    public static readonly FormationDebugSettings Settings = new FormationDebugSettings();
    public static string SelectedFormationId;

    static BaseSpawner spawner;
    static FormationDebugScene cache;
    static object cacheKeyReport;
    static int cacheKeyPairs = -1, cacheKeyClusters = -1;
    static GUIStyle labelStyle;

    static FormationDebugSceneDrawer()
    {
        LoadSettings();
        SceneView.duringSceneGui += OnSceneGUI;
        Selection.selectionChanged += SyncSelection;
        EditorApplication.playModeStateChanged += _ => Invalidate();
    }

    // ------------------------------------------------------------------ settings
    public static void LoadSettings()
    {
        var s = Settings;
        s.showFormationDebug = EditorPrefs.GetBool(Prefix + "showFormationDebug", false);
        s.showFacingPairDebug = EditorPrefs.GetBool(Prefix + "showFacingPairDebug", false);
        s.showClusterDebug = EditorPrefs.GetBool(Prefix + "showClusterDebug", false);
        s.showMemberFacing = EditorPrefs.GetBool(Prefix + "showMemberFacing", false);
        s.showAttackPositions = EditorPrefs.GetBool(Prefix + "showAttackPositions", false);
        s.showSupporterLines = EditorPrefs.GetBool(Prefix + "showSupporterLines", false);
        s.showEngagementRanges = EditorPrefs.GetBool(Prefix + "showEngagementRanges", false);
        s.showApproachCoverage = EditorPrefs.GetBool(Prefix + "showApproachCoverage", false);
        s.showApproachReservations = EditorPrefs.GetBool(Prefix + "showApproachReservations", false);
        s.showFormationLabels = EditorPrefs.GetBool(Prefix + "showFormationLabels", false);
        s.onlySelectedFormation = EditorPrefs.GetBool(Prefix + "onlySelectedFormation", false);
        s.alwaysFullDetail = EditorPrefs.GetBool(Prefix + "alwaysFullDetail", false);
        s.detailDistance = EditorPrefs.GetFloat(Prefix + "detailDistance", 150f);
    }

    public static void SaveSettings()
    {
        var s = Settings;
        EditorPrefs.SetBool(Prefix + "showFormationDebug", s.showFormationDebug);
        EditorPrefs.SetBool(Prefix + "showFacingPairDebug", s.showFacingPairDebug);
        EditorPrefs.SetBool(Prefix + "showClusterDebug", s.showClusterDebug);
        EditorPrefs.SetBool(Prefix + "showMemberFacing", s.showMemberFacing);
        EditorPrefs.SetBool(Prefix + "showAttackPositions", s.showAttackPositions);
        EditorPrefs.SetBool(Prefix + "showSupporterLines", s.showSupporterLines);
        EditorPrefs.SetBool(Prefix + "showEngagementRanges", s.showEngagementRanges);
        EditorPrefs.SetBool(Prefix + "showApproachCoverage", s.showApproachCoverage);
        EditorPrefs.SetBool(Prefix + "showApproachReservations", s.showApproachReservations);
        EditorPrefs.SetBool(Prefix + "showFormationLabels", s.showFormationLabels);
        EditorPrefs.SetBool(Prefix + "onlySelectedFormation", s.onlySelectedFormation);
        EditorPrefs.SetBool(Prefix + "alwaysFullDetail", s.alwaysFullDetail);
        EditorPrefs.SetFloat(Prefix + "detailDistance", s.detailDistance);
        SceneView.RepaintAll();
    }

    // ------------------------------------------------------------------ data
    public static BaseSpawner Spawner
    {
        get
        {
            if (spawner == null) spawner = Object.FindAnyObjectByType<BaseSpawner>();
            return spawner;
        }
    }

    public static void Invalidate() { cache = null; spawner = null; cacheKeyReport = null; }

    /// <summary>Cached geometry; rebuilt only when the spawner produced a new run (report string identity / counts).</summary>
    public static FormationDebugScene Scene
    {
        get
        {
            BaseSpawner sp = Spawner;
            if (sp == null) { cache = null; return null; }
            string report = sp.LastFormationReport;
            int pc = sp.LastFacingPairs.Count, cc = sp.LastClusters.Count;
            if (cache == null || !ReferenceEquals(report, cacheKeyReport) || pc != cacheKeyPairs || cc != cacheKeyClusters)
            {
                // Required supporters come from the spawner's own cluster settings (read-only).
                ClusterSettings cs = typeof(BaseSpawner).GetField("clusterSettings",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(sp) as ClusterSettings;
                cache = FormationDebugGeometry.Build(sp.LastFacingPairs, sp.LastClusters, sp.CrossfireRanges, sp.AttackModel,
                    cs != null ? cs.minSupportersPerFront : 2);
                cacheKeyReport = report; cacheKeyPairs = pc; cacheKeyClusters = cc;
            }

            return cache;
        }
    }

    static void SyncSelection()
    {
        GameObject go = Selection.activeGameObject;
        FormationMemberInfo info = go != null ? go.GetComponentInParent<FormationMemberInfo>() : null;
        if (info != null && !string.IsNullOrEmpty(info.FormationId))
        {
            SelectedFormationId = info.FormationId;
            SceneView.RepaintAll();
            if (EditorWindow.HasOpenInstances<FormationDebugWindow>()) EditorWindow.GetWindow<FormationDebugWindow>(false, null, false).Repaint();
        }
    }

    // ------------------------------------------------------------------ drawing
    static void OnSceneGUI(SceneView view)
    {
        if (!Settings.showFormationDebug || Event.current.type != EventType.Repaint) return;
        FormationDebugScene scene = Scene;
        if (scene == null) return;

        if (labelStyle == null) labelStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11, richText = false };
        FormationDebugLayer layers = FormationDebugGeometry.EnabledLayers(Settings);
        Vector3 cam = view.camera.transform.position;
        Color previous = Handles.color;
        var zTest = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

        foreach (FormationDebugEntry e in scene.formations)
        {
            if (!FormationDebugGeometry.FormationVisible(e, Settings, SelectedFormationId)) continue;
            bool detail = Settings.alwaysFullDetail || Vector3.Distance(cam, e.centre) < Settings.detailDistance + e.radius;
            for (int i = 0; i < e.commands.Count; i++)
            {
                FormationDebugCommand c = e.commands[i];
                if (FormationDebugGeometry.CommandVisible(c, layers, detail)) Draw(c);
            }

            if (e.formationId == SelectedFormationId)
            {
                Handles.color = Color.yellow;
                Handles.DrawWireDisc(e.centre, Vector3.up, e.radius, 3f);
            }
        }

        Handles.zTest = zTest;
        Handles.color = previous;
    }

    static void Draw(in FormationDebugCommand c)
    {
        Handles.color = c.color;
        switch (c.primitive)
        {
            case FormationDebugPrimitive.Line:
                if (c.dashed) Handles.DrawDottedLine(c.a, c.b, 4f); else Handles.DrawLine(c.a, c.b, c.thickness);
                break;
            case FormationDebugPrimitive.Disc:
                if (c.dashed && c.points != null) Handles.DrawDottedLines(c.points, 5f); else Handles.DrawWireDisc(c.a, c.b, c.radius, c.thickness);
                break;
            case FormationDebugPrimitive.Point:
                Handles.SphereHandleCap(0, c.a, Quaternion.identity, c.radius * 2f, EventType.Repaint);
                break;
            case FormationDebugPrimitive.Quad:
                Handles.DrawSolidRectangleWithOutline(c.quad, c.fill, c.color);
                break;
            case FormationDebugPrimitive.Label:
                labelStyle.normal.textColor = c.color;
                Handles.Label(c.a, c.text, labelStyle);
                break;
        }
    }
}

public sealed class FormationDebugWindow : EditorWindow
{
    Vector2 scroll;
    Vector2 detailScroll;

    [MenuItem("Tools/IRONVEIL/Formation Debug")]
    public static void Open() => GetWindow<FormationDebugWindow>("Formation Debug");

    void OnInspectorUpdate() => Repaint();

    void OnGUI()
    {
        FormationDebugSettings s = FormationDebugSceneDrawer.Settings;
        EditorGUILayout.HelpBox("Debug reference only: draws generated facing pairs and clusters, their engagement geometry and reserved strips. " +
            "The range spheres are geometric references, not an implemented weapon system. Settings are stored per user (EditorPrefs), never in the scene.", MessageType.None);

        EditorGUI.BeginChangeCheck();
        s.showFormationDebug = EditorGUILayout.ToggleLeft("Show formation debug (master)", s.showFormationDebug, EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(!s.showFormationDebug))
        {
            EditorGUILayout.BeginHorizontal();
            s.showFacingPairDebug = EditorGUILayout.ToggleLeft("Facing pairs", s.showFacingPairDebug, GUILayout.Width(120));
            s.showClusterDebug = EditorGUILayout.ToggleLeft("Clusters", s.showClusterDebug, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();
            s.showMemberFacing = EditorGUILayout.ToggleLeft("Member facing (forward arrows)", s.showMemberFacing);
            s.showAttackPositions = EditorGUILayout.ToggleLeft("Front attack positions + supporter count", s.showAttackPositions);
            s.showSupporterLines = EditorGUILayout.ToggleLeft("Supporter lines (green = valid, dotted red = out of band)", s.showSupporterLines);
            s.showEngagementRanges = EditorGUILayout.ToggleLeft("Engagement ranges (orange dotted = min, blue = max; 3D wire spheres)", s.showEngagementRanges);
            s.showApproachCoverage = EditorGUILayout.ToggleLeft("Approach coverage (green >=2, yellow 1, red dotted = uncovered)", s.showApproachCoverage);
            s.showApproachReservations = EditorGUILayout.ToggleLeft("Approach reservations (pair corridors, cluster strips)", s.showApproachReservations);
            s.showFormationLabels = EditorGUILayout.ToggleLeft("Labels", s.showFormationLabels);
            s.onlySelectedFormation = EditorGUILayout.ToggleLeft("Only the selected formation", s.onlySelectedFormation);
            s.alwaysFullDetail = EditorGUILayout.ToggleLeft("Always full detail (ignore camera distance)", s.alwaysFullDetail);
            s.detailDistance = EditorGUILayout.Slider("Detail distance", s.detailDistance, 20f, 1000f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("All layers on")) { s.showFacingPairDebug = s.showClusterDebug = s.showMemberFacing = s.showAttackPositions = s.showSupporterLines = s.showEngagementRanges = s.showApproachCoverage = s.showApproachReservations = s.showFormationLabels = true; }
            if (GUILayout.Button("All off")) { s.showFormationDebug = s.showFacingPairDebug = s.showClusterDebug = s.showMemberFacing = s.showAttackPositions = s.showSupporterLines = s.showEngagementRanges = s.showApproachCoverage = s.showApproachReservations = s.showFormationLabels = s.onlySelectedFormation = s.alwaysFullDetail = false; }
            EditorGUILayout.EndHorizontal();
        }
        if (EditorGUI.EndChangeCheck()) FormationDebugSceneDrawer.SaveSettings();

        EditorGUILayout.Space();
        BaseSpawner sp = FormationDebugSceneDrawer.Spawner;
        if (sp == null) { EditorGUILayout.HelpBox("No BaseSpawner in the open scene.", MessageType.Warning); return; }

        // Play-mode-only generation helpers (switch changes revert when Play mode ends).
        EditorGUILayout.LabelField("Generation (scene switches: pairs " + (sp.GenerateStrategicFormations ? "ON" : "OFF") + ", clusters " + (sp.GenerateStrategicClusters ? "ON" : "OFF") + ")", EditorStyles.boldLabel);
        WorldGenerationPrototype gen = Object.FindAnyObjectByType<WorldGenerationPrototype>();
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Formations exist only after generation. Enter Play mode, wait for the world to finish, then use the buttons below " +
                "(they set the switches for this Play session only and regenerate the bases with the current seed).", MessageType.Info);
        }

        using (new EditorGUI.DisabledScope(!Application.isPlaying || gen == null || !gen.IsGenerationComplete))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Pairs only")) Regenerate(sp, gen, true, false);
            if (GUILayout.Button("Clusters only")) Regenerate(sp, gen, false, true);
            if (GUILayout.Button("Both")) Regenerate(sp, gen, true, true);
            if (GUILayout.Button("None")) Regenerate(sp, gen, false, false);
            EditorGUILayout.EndHorizontal();
        }
        if (gen != null && Application.isPlaying) EditorGUILayout.LabelField("Current base seed: " + gen.CurrentBaseSeed);

        FormationDebugScene scene = FormationDebugSceneDrawer.Scene;
        int n = scene?.formations.Count ?? 0;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Formations: " + (scene?.pairCount ?? 0) + " facing pairs, " + (scene?.clusterCount ?? 0) + " clusters", EditorStyles.boldLabel);
        if (n == 0) { EditorGUILayout.HelpBox("No formations in the last generation.", MessageType.None); return; }

        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(220));
        foreach (FormationDebugEntry e in scene.formations)
        {
            EditorGUILayout.BeginHorizontal();
            bool sel = e.formationId == FormationDebugSceneDrawer.SelectedFormationId;
            GUI.color = e.allTargetsMeetRequirement ? Color.white : new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Toggle(sel, e.summary, "Button") && !sel) { FormationDebugSceneDrawer.SelectedFormationId = e.formationId; SceneView.RepaintAll(); }
            GUI.color = Color.white;
            if (GUILayout.Button("Frame", GUILayout.Width(50))) Frame(e);
            if (GUILayout.Button("Select", GUILayout.Width(50))) SelectMembers(e);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        FormationDebugEntry chosen = scene.formations.Find(f => f.formationId == FormationDebugSceneDrawer.SelectedFormationId);
        if (chosen != null)
        {
            EditorGUILayout.LabelField("Selected: " + chosen.formationId, EditorStyles.boldLabel);
            detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
            EditorGUILayout.SelectableLabel(chosen.details, EditorStyles.textArea, GUILayout.ExpandHeight(true), GUILayout.MinHeight(EditorStyles.textArea.CalcHeight(new GUIContent(chosen.details), position.width - 30)));
            EditorGUILayout.EndScrollView();
        }
    }

    static void Regenerate(BaseSpawner sp, WorldGenerationPrototype gen, bool pairs, bool clusters)
    {
        SerializedObject so = new SerializedObject(sp);
        so.FindProperty("generateStrategicFormations").boolValue = pairs;
        so.FindProperty("generateStrategicClusters").boolValue = clusters;
        so.ApplyModifiedPropertiesWithoutUndo();
        gen.RegenerateBases(gen.CurrentBaseSeed);
        FormationDebugSceneDrawer.Invalidate();
        SceneView.RepaintAll();
    }

    static void Frame(FormationDebugEntry e)
    {
        FormationDebugSceneDrawer.SelectedFormationId = e.formationId;
        SceneView view = SceneView.lastActiveSceneView;
        if (view != null) view.Frame(new Bounds(e.centre, Vector3.one * e.radius * 2f), false);
        SceneView.RepaintAll();
    }

    static void SelectMembers(FormationDebugEntry e)
    {
        FormationDebugSceneDrawer.SelectedFormationId = e.formationId;
        var list = new List<Object>();
        foreach (FormationMemberInfo info in Object.FindObjectsByType<FormationMemberInfo>(FindObjectsInactive.Exclude))
            if (info.FormationId == e.formationId) list.Add(info.gameObject);
        Selection.objects = list.ToArray();
    }
}
