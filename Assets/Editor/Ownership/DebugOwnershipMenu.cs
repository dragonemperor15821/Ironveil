using Ironveil.Economy;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Stage 8B: DEVELOPER-ONLY ownership tools (editor menu, never part of the player-facing game). Select a base
/// (or any object inside it) in the Hierarchy or Scene view and assign it to the player or return it to neutral.
/// Uses the existing BaseController ownership field through DebugOwnership, so storage recalculates; the Home
/// Base is protected from neutralisation.
/// </summary>
public static class DebugOwnershipMenu
{
    private const string Root = "Tools/IRONVEIL/Debug Ownership/";

    private static BaseController Selected() =>
        Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<BaseController>(true) : null;

    [MenuItem(Root + "Assign Selected Base To Player")]
    private static void AssignToPlayer()
    {
        BaseController b = Selected();
        bool ok = DebugOwnership.TryAssignToPlayer(b, out string error);
        Report(b, ok, error, "assigned to the player");
    }

    [MenuItem(Root + "Return Selected Base To Neutral")]
    private static void ReturnToNeutral()
    {
        BaseController b = Selected();
        bool ok = DebugOwnership.TryReturnToNeutral(b, out string error);
        Report(b, ok, error, "returned to neutral");
    }

    [MenuItem(Root + "Assign Selected Base To Player", true)]
    [MenuItem(Root + "Return Selected Base To Neutral", true)]
    private static bool HasSelection() => Selected() != null;

    [MenuItem(Root + "Log Player Storage")]
    private static void LogStorage()
    {
        StorageCalculation calc = ResourceStorageExpansion.Recalculate();
        Debug.Log(calc == null
            ? "Storage: no initialised ResourceManager (enter Play Mode)."
            : $"Storage: {calc}. Home Base: {(PlayerHomeBase.HasHomeBase ? PlayerHomeBase.Current.BaseId : "none")}.");
    }

    private static void Report(BaseController b, bool ok, string error, string what)
    {
        if (ok) Debug.Log($"Debug ownership: {b.BaseName} ({b.BaseId}) {what}. {ResourceStorageExpansion.LastCalculation}", b);
        else Debug.LogWarning($"Debug ownership: {(b != null ? b.BaseId : "no base")}: {error}", b);
    }
}
