using UnityEngine;

/// <summary>
/// Base Management Menu (Phase 1C): reusable world-to-screen anchor for a base.
///
/// The anchor is the centre of the main base's cached world bounds (hangars
/// excluded), projected through the given camera. Pure and allocation-free;
/// call it every frame after the camera has moved (LateUpdate) so the menu
/// follows pan, zoom and rotation.
/// </summary>
public static class BaseScreenProjection
{
    /// <summary>Result of projecting a base into screen space.</summary>
    public struct Anchor
    {
        /// <summary>Screen position in pixels (origin bottom-left, like Input System and uGUI overlay).</summary>
        public Vector2 screenPosition;

        /// <summary>Distance in front of the camera along its view axis; negative = behind.</summary>
        public float depth;

        /// <summary>In front of the camera and inside its pixel rect.</summary>
        public bool onScreen;

        /// <summary>In front of the camera (the screen position is meaningful, possibly outside the rect).</summary>
        public bool inFront;
    }

    /// <summary>World point the menu is centred on: the main base's bounds centre.</summary>
    public static bool TryGetWorldAnchor(BaseInteractionTarget target, out Vector3 worldPoint)
    {
        worldPoint = default;
        if (target == null || !target.TryGetWorldBounds(out Bounds bounds))
        {
            return false;
        }

        worldPoint = bounds.center;
        return true;
    }

    /// <summary>Projects the base's anchor through <paramref name="cam"/>. False if either is missing.</summary>
    public static bool TryProject(Camera cam, BaseInteractionTarget target, out Anchor anchor)
    {
        anchor = default;
        if (cam == null || !TryGetWorldAnchor(target, out Vector3 world))
        {
            return false;
        }

        Project(cam, world, out anchor);
        return true;
    }

    /// <summary>Projects an arbitrary world point (shared by tests and future UI).</summary>
    public static void Project(Camera cam, Vector3 worldPoint, out Anchor anchor)
    {
        Vector3 screen = cam.WorldToScreenPoint(worldPoint);
        anchor.screenPosition = new Vector2(screen.x, screen.y);
        anchor.depth = screen.z;
        anchor.inFront = screen.z > cam.nearClipPlane;
        anchor.onScreen = anchor.inFront && cam.pixelRect.Contains(anchor.screenPosition);
    }
}
