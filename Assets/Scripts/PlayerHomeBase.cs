using System;
using UnityEngine;

/// <summary>
/// Stage 8B: the player-level Home Base designation. The Home Base is an ordinary Tank Base (BaseController,
/// owner PLAYER through the existing ownership field) spawned as an ADDITIONAL base by BaseSpawner when the map's
/// player has a Home Base. A player has at most one: a second designation is refused while the first exists.
/// The designation lives here (not in the base's ownership), so ordinary ownership changes cannot remove it, and
/// BaseController.SetOwner refuses to give the Home Base away (see BaseController).
/// </summary>
public static class PlayerHomeBase
{
    /// <summary>Stable base ID of the Home Base (never collides with BASE_&lt;TYPE&gt;_### standard IDs).</summary>
    public const string HomeBaseId = "HOME_BASE";
    public const string HomeBaseName = "Home Base";
    public const string LabelText = "HOME BASE";

    private static BaseController current;

    /// <summary>Raised after the designation changes (designated or released).</summary>
    public static event Action<BaseController> Changed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        current = null;
        Changed = null;
    }

    /// <summary>The player's Home Base, or null (none designated, or it was destroyed).</summary>
    public static BaseController Current => current != null ? current : null;

    public static bool HasHomeBase => Current != null;

    public static bool IsHomeBase(BaseController controller) => controller != null && ReferenceEquals(Current, controller);

    /// <summary>
    /// Designates <paramref name="controller"/> as the player's Home Base. Refused when another Home Base exists,
    /// or when the base is not player-owned. Adds the "HOME BASE" label. Designating the current one again is a no-op.
    /// </summary>
    public static bool TryDesignate(BaseController controller, out string error)
    {
        error = null;
        if (controller == null) { error = "no base"; return false; }
        if (IsHomeBase(controller)) return true;
        if (HasHomeBase) { error = $"the player already has a Home Base ({Current.BaseId})"; return false; }
        if (!controller.IsPlayerOwned) { error = "the Home Base must be owned by the player"; return false; }

        current = controller;
        HomeBaseLabel.Ensure(controller);
        Raise(controller);
        return true;
    }

    /// <summary>Releases the designation of <paramref name="controller"/> (map regeneration). Returns false when it is not the Home Base.</summary>
    public static bool Release(BaseController controller)
    {
        if (!IsHomeBase(controller)) return false;
        current = null;
        Raise(null);
        return true;
    }

    /// <summary>Releases the designation when the Home Base is <paramref name="root"/> or inside it (map regeneration).</summary>
    public static bool ReleaseIfUnder(Transform root)
    {
        BaseController home = Current;
        return home != null && root != null && home.transform.IsChildOf(root) && Release(home);
    }

    /// <summary>Clears a stale designation (destroyed object) without an event.</summary>
    internal static void ForgetIfDestroyed()
    {
        if (!ReferenceEquals(current, null) && current == null) current = null;
    }

    private static void Raise(BaseController controller)
    {
        Action<BaseController> handlers = Changed;
        if (handlers == null) return;
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try { ((Action<BaseController>)d)(controller); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}

/// <summary>
/// Stage 8B: DEVELOPER-ONLY ownership changes (no player-facing UI; used by the editor menu
/// Tools/IRONVEIL/Debug Ownership and by tests). Uses the existing BaseController ownership field and events,
/// so storage recalculates. The Home Base cannot be neutralised or reassigned here.
/// </summary>
public static class DebugOwnership
{
    public static bool TryAssignToPlayer(BaseController controller, out string error)
    {
        error = null;
        if (controller == null) { error = "no base selected"; return false; }
        if (controller.IsPlayerOwned) return true;
        controller.SetOwner(BaseController.PlayerOwnerId);
        return controller.IsPlayerOwned;
    }

    public static bool TryReturnToNeutral(BaseController controller, out string error)
    {
        error = null;
        if (controller == null) { error = "no base selected"; return false; }
        if (PlayerHomeBase.IsHomeBase(controller)) { error = "the Home Base is protected and cannot be neutralised"; return false; }
        controller.SetOwner(BaseController.NeutralOwnerId);
        return controller.OwnerId == BaseController.NeutralOwnerId;
    }
}
