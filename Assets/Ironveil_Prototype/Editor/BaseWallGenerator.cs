
using UnityEngine;
using UnityEditor;

public class BaseWallGenerator : EditorWindow
{
    [SerializeField] private GameObject wallPrefab;
    [SerializeField] private Transform wallParent;

    [SerializeField] private float radius = 18f;
    [SerializeField] private float wallHeight = 4f;
    [SerializeField] private float wallWidth = 2.4f;
    [SerializeField] private float wallThickness = 0.8f;
    [SerializeField] private float wallCenterY = 2f;

    [SerializeField] private int segmentCount = 48;
    [SerializeField] private float entranceAngle = 0f;
    [SerializeField] private float entranceWidth = 12f;

    private const string GeneratedPrefix = "GeneratedWall_";

    [MenuItem("IRONVEIL/Prototype/Wall Generator")]
    public static void ShowWindow()
    {
        GetWindow<BaseWallGenerator>("Base Wall Generator");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(
            "IRONVEIL - Circular Wall Generator",
            EditorStyles.boldLabel
        );

        EditorGUILayout.Space();

        wallPrefab = (GameObject)EditorGUILayout.ObjectField(
            "Wall Segment Prefab",
            wallPrefab,
            typeof(GameObject),
            false
        );

        wallParent = (Transform)EditorGUILayout.ObjectField(
            "Wall Parent",
            wallParent,
            typeof(Transform),
            true
        );

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Wall Dimensions", EditorStyles.boldLabel);

        radius = EditorGUILayout.FloatField("Radius", radius);
        wallHeight = EditorGUILayout.FloatField("Wall Height", wallHeight);
        wallWidth = EditorGUILayout.FloatField("Segment Width", wallWidth);
        wallThickness = EditorGUILayout.FloatField("Thickness", wallThickness);
        wallCenterY = EditorGUILayout.FloatField("Wall Center Y", wallCenterY);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Circular Layout", EditorStyles.boldLabel);

        segmentCount = EditorGUILayout.IntSlider(
            "Segment Count", segmentCount, 12, 120
        );

        entranceAngle = EditorGUILayout.Slider(
            "Entrance Angle", entranceAngle, 0f, 359f
        );

        entranceWidth = EditorGUILayout.Slider(
            "Entrance Width", entranceWidth, 0f, 24f
        );

        EditorGUILayout.HelpBox(
            "Segments follow the circular tangent. " +
            "An entrance is left open around the selected angle. " +
            "Generated objects are parented under Wall Parent.",
            MessageType.Info
        );

        using (new EditorGUI.DisabledScope(
            wallPrefab == null || wallParent == null))
        {
            if (GUILayout.Button("Generate / Regenerate Walls"))
            {
                GenerateWalls();
            }
        }
    }

    private void GenerateWalls()
    {
        if (wallPrefab == null || wallParent == null)
        {
            Debug.LogWarning(
                "Assign the wall prefab and wall parent first."
            );
            return;
        }

        if (radius <= 0f || wallHeight <= 0f ||
            wallWidth <= 0f || wallThickness <= 0f ||
            segmentCount < 1 || entranceWidth < 0f)
        {
            Debug.LogWarning("Wall dimensions or layout settings are invalid.");
            return;
        }

        if (wallPrefab.transform.IsChildOf(wallParent))
        {
            Debug.LogError(
                "The prefab reference must be an asset, not a scene child."
            );
            return;
        }

        // Remove only objects created by this generator.
        for (int i = wallParent.childCount - 1; i >= 0; i--)
        {
            Transform child = wallParent.GetChild(i);

            if (child.name.StartsWith(GeneratedPrefix))
            {
                Undo.DestroyObjectImmediate(child.gameObject);
            }
        }

        float stepAngle = 360f / segmentCount;

        // Approximate the entrance width as an arc along the circle.
        float halfEntranceAngle =
            (entranceWidth * 0.5f / radius) * Mathf.Rad2Deg;

        int created = 0;

        for (int i = 0; i < segmentCount; i++)
        {
            float angle = i * stepAngle;

            if (Mathf.Abs(
                Mathf.DeltaAngle(angle, entranceAngle)
            ) < halfEntranceAngle + stepAngle * 0.5f)
            {
                continue;
            }

            float radians = angle * Mathf.Deg2Rad;

            Vector3 localPosition = new Vector3(
                Mathf.Sin(radians) * radius,
                wallCenterY,
                Mathf.Cos(radians) * radius
            );

            GameObject wall = (GameObject)PrefabUtility.InstantiatePrefab(
                wallPrefab,
                wallParent
            );

            if (wall == null)
            {
                Debug.LogError("Failed to instantiate the wall prefab.");
                return;
            }

            Undo.RegisterCreatedObjectUndo(
                wall, "Generate IRONVEIL Wall"
            );

            wall.name = $"{GeneratedPrefix}{created + 1:00}";
            wall.transform.localPosition = localPosition;

            // A cube's long local X axis follows the circle's tangent.
            wall.transform.localRotation =
                Quaternion.Euler(0f, angle, 0f);

            wall.transform.localScale = new Vector3(
                wallWidth / 4f,
                wallHeight / 4f,
                wallThickness / 0.8f
            );

            created++;
        }

        Debug.Log(
            $"IRONVEIL wall generation complete: {created} segments. " +
            $"Radius: {radius:F1}, entrance width: {entranceWidth:F1}."
        );
    }
}
