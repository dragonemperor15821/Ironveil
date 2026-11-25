using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base Management Menu (Phase 1A): marks one generated main base as an
/// interaction target. Added at runtime by BaseMenuBootstrap, next to the
/// base's BaseController; never placed on hangars.
///
/// Holds the BaseContext, keeps the base registered in BaseRegistry while
/// enabled, and provides world-space bounds built only from the main base's
/// own renderers. For Naval/Rail bases the hangar is a sibling of the main
/// base (both under the GeneratedBase_* root), so it is excluded by
/// construction. No colliders are added and no transform or renderer is
/// modified. No Update loop.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public sealed class BaseInteractionTarget : MonoBehaviour
{
    private static readonly List<Renderer> rendererBuffer = new List<Renderer>(64);

    private Renderer[] renderers = new Renderer[0];
    private Bounds cachedBounds;
    private bool hasBounds;

    // Phase 1B: oriented box of the same main-base renderers, in this base's
    // local frame. The world AABB of a rotated base can swallow its sibling
    // hangar; the oriented box follows the base's yaw and does not.
    private Bounds cachedLocalBounds;
    private Matrix4x4 cachedWorldToLocal = Matrix4x4.identity;
    private Matrix4x4 cachedLocalToWorld = Matrix4x4.identity;

    public BaseContext Context { get; private set; }

    public string BaseId => Context != null ? Context.BaseId : string.Empty;

    /// <summary>Number of main-base renderers contributing to the bounds.</summary>
    public int RendererCount => renderers.Length;

    /// <summary>
    /// Binds this target to the base's identity components and registers it.
    /// Safe to call again (e.g. after regeneration); rebinding refreshes the
    /// context and renderer cache.
    /// </summary>
    public bool Bind(BaseController controller, BaseLevelInfo levelInfo)
    {
        if (controller == null || controller.gameObject != gameObject)
        {
            Debug.LogWarning("BaseInteractionTarget: must sit on the base's BaseController object.", this);
            return false;
        }

        if (Context != null && Context.BaseId != controller.BaseId)
        {
            BaseRegistry.Unregister(this);
        }

        Context = new BaseContext(controller, levelInfo);
        CacheRenderers();
        RecalculateBounds();

        // OnEnable does not run for components added in edit mode, so register
        // directly whenever the object is live.
        return isActiveAndEnabled && BaseRegistry.Register(this);
    }

    /// <summary>
    /// World-space bounds of the main base's rendered geometry (hangars and
    /// any other sibling objects excluded). Cached; bases are static.
    /// </summary>
    public bool TryGetWorldBounds(out Bounds bounds)
    {
        if (!hasBounds)
        {
            RecalculateBounds();
        }

        bounds = cachedBounds;
        return hasBounds;
    }

    /// <summary>Rebuilds the cached bounds from the current renderer state.</summary>
    public void RecalculateBounds()
    {
        hasBounds = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (hasBounds)
            {
                cachedBounds.Encapsulate(r.bounds);
            }
            else
            {
                cachedBounds = r.bounds;
                hasBounds = true;
            }
        }

        if (!hasBounds)
        {
            cachedBounds = new Bounds(transform.position, Vector3.zero);
        }

        RecalculateLocalBounds();
    }

    /// <summary>
    /// Ray test for hover picking: cheap world-AABB rejection first, then the
    /// oriented main-base box. Returns the world-space distance from the ray
    /// origin to the entry point. Allocation-free.
    /// </summary>
    public bool IntersectRay(Ray worldRay, out float distance)
    {
        distance = 0f;
        if (!TryGetWorldBounds(out Bounds world) || !world.IntersectRay(worldRay))
        {
            return false;
        }

        Vector3 localOrigin = cachedWorldToLocal.MultiplyPoint3x4(worldRay.origin);
        Vector3 localDirection = cachedWorldToLocal.MultiplyVector(worldRay.direction);
        Ray localRay = new Ray(localOrigin, localDirection);
        if (!cachedLocalBounds.IntersectRay(localRay, out float localDistance))
        {
            return false;
        }

        Vector3 worldHit = cachedLocalToWorld.MultiplyPoint3x4(localRay.GetPoint(localDistance));
        distance = Vector3.Dot(worldHit - worldRay.origin, worldRay.direction);
        return true;
    }

    private void RecalculateLocalBounds()
    {
        cachedWorldToLocal = transform.worldToLocalMatrix;
        cachedLocalToWorld = transform.localToWorldMatrix;
        bool any = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
            {
                continue;
            }

            // Mesh-space bounds when available (tight); otherwise the
            // renderer's world AABB re-expressed in the base frame.
            Matrix4x4 toBase;
            Bounds source;
            MeshFilter filter = r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
            if (filter != null && filter.sharedMesh != null)
            {
                source = filter.sharedMesh.bounds;
                toBase = cachedWorldToLocal * r.transform.localToWorldMatrix;
            }
            else
            {
                source = r.bounds;
                toBase = cachedWorldToLocal;
            }

            Vector3 c = source.center;
            Vector3 e = source.extents;
            for (int k = 0; k < 8; k++)
            {
                Vector3 corner = toBase.MultiplyPoint3x4(new Vector3(
                    c.x + ((k & 1) == 0 ? -e.x : e.x),
                    c.y + ((k & 2) == 0 ? -e.y : e.y),
                    c.z + ((k & 4) == 0 ? -e.z : e.z)));
                if (any)
                {
                    cachedLocalBounds.Encapsulate(corner);
                }
                else
                {
                    cachedLocalBounds = new Bounds(corner, Vector3.zero);
                    any = true;
                }
            }
        }

        if (!any)
        {
            cachedLocalBounds = new Bounds(Vector3.zero, Vector3.zero);
        }
    }

    private void CacheRenderers()
    {
        // Only renderers in the main base's own hierarchy; mesh geometry only
        // (particles, lines and trails would distort the hover volume).
        rendererBuffer.Clear();
        GetComponentsInChildren(true, rendererBuffer);
        int count = 0;
        for (int i = 0; i < rendererBuffer.Count; i++)
        {
            if (IsGeometry(rendererBuffer[i])) count++;
        }

        renderers = new Renderer[count];
        int index = 0;
        for (int i = 0; i < rendererBuffer.Count; i++)
        {
            if (IsGeometry(rendererBuffer[i])) renderers[index++] = rendererBuffer[i];
        }

        rendererBuffer.Clear();
    }

    private static bool IsGeometry(Renderer r)
    {
        return r is MeshRenderer || r is SkinnedMeshRenderer;
    }

    private void OnEnable()
    {
        if (Context != null)
        {
            BaseRegistry.Register(this);
        }
    }

    private void OnDisable()
    {
        BaseRegistry.Unregister(this);
    }

    private void OnDestroy()
    {
        BaseRegistry.Unregister(this);
    }
}
