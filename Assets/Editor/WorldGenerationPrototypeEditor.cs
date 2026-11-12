using System;
using System.Collections;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(WorldGenerationPrototype))]
public sealed class WorldGenerationPrototypeEditor : Editor
{
    private WorldGenerationPrototype prototype;
    private IEnumerator generation;
    private bool isGenerating;

    private void OnEnable()
    {
        prototype = (WorldGenerationPrototype)target;
    }

    private void OnDisable()
    {
        if (isGenerating) CancelPreview();
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Generate/Refresh creates one owned terrain-preview root beneath this " +
            "prototype. The preview is transient and is not saved into the scene. " +
            "Play Mode runs the configured production generators and BaseSpawner. " +
            "Neither operation changes a production scene.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(Application.isPlaying || isGenerating))
        {
            if (GUILayout.Button("Generate / Refresh Terrain Preview"))
            {
                BeginPreview();
            }
        }

        using (new EditorGUI.DisabledScope(Application.isPlaying || isGenerating))
        {
            if (GUILayout.Button("Clear Terrain, Guides, and Generated Bases"))
            {
                prototype.ClearEditorPreview();
                SceneView.RepaintAll();
            }
        }

        if (!string.IsNullOrEmpty(prototype.LastError))
        {
            EditorGUILayout.HelpBox(prototype.LastError, MessageType.Error);
        }

        if (!string.IsNullOrEmpty(prototype.LastDiagnosticReport))
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Latest Capacity/Integrity Report", EditorStyles.boldLabel);
            EditorGUILayout.TextArea(prototype.LastDiagnosticReport, GUILayout.MinHeight(180f));
        }
    }

    private void BeginPreview()
    {
        generation = prototype.GenerateEditorPreview();
        if (generation == null) return;

        isGenerating = true;
        EditorApplication.update += AdvancePreview;
    }

    private void AdvancePreview()
    {
        if (prototype == null || generation == null)
        {
            FinishPreview(false);
            return;
        }

        try
        {
            if (!generation.MoveNext())
            {
                FinishPreview(true);
                return;
            }

            EditorUtility.DisplayProgressBar(
                "IRONVEIL World Generation",
                "Generating terrain chunks and validating placement capacity...",
                Mathf.Clamp01(prototype.GenerationProgress));
            SceneView.RepaintAll();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, prototype);
            prototype.CancelEditorPreview();
            FinishPreview(false);
        }
    }

    private void FinishPreview(bool completed)
    {
        EditorApplication.update -= AdvancePreview;
        EditorUtility.ClearProgressBar();
        generation = null;
        isGenerating = false;

        if (completed && prototype != null && prototype.IsGenerated)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null) view.Frame(prototype.PrototypeWorldBounds, true);
            SceneView.RepaintAll();
        }
    }

    private void CancelPreview()
    {
        EditorApplication.update -= AdvancePreview;
        EditorUtility.ClearProgressBar();
        generation = null;
        isGenerating = false;
        if (prototype != null) prototype.CancelEditorPreview();
    }
}
