using UnityEditor;

/// <summary>Editor-only debug toggle for construction-slot gizmos (default OFF).</summary>
public static class ModularBasesDebugMenu
{
    private const string MenuPath = "Tools/IRONVEIL/Modular Bases/Show Slot Gizmos";

    [MenuItem(MenuPath)]
    private static void Toggle()
    {
        bool on = !EditorPrefs.GetBool(BuildingSlot.ShowGizmosPrefKey, false);
        EditorPrefs.SetBool(BuildingSlot.ShowGizmosPrefKey, on);
        SceneView.RepaintAll();
    }

    [MenuItem(MenuPath, true)]
    private static bool ToggleValidate()
    {
        Menu.SetChecked(MenuPath, EditorPrefs.GetBool(BuildingSlot.ShowGizmosPrefKey, false));
        return true;
    }
}
