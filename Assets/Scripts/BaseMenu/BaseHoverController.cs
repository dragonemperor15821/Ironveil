using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Base Management Menu (Phase 1B): central mouse-hover detection.
///
/// One instance per Play session, added by BaseMenuBootstrap. Each frame it
/// casts Camera.ScreenPointToRay through the mouse position (new Input
/// System) and tests that ray against every BaseInteractionTarget in
/// BaseRegistry: first its cached world-space AABB (cheap rejection), then
/// its cached oriented main-base box, picking the nearest entry point.
/// No physics, no colliders, no per-base Update. Hangars are never targets
/// and are not part of any base's bounds; the oriented test also keeps a
/// hangar that sits inside a rotated base's AABB from counting as the base.
///
/// Runs in LateUpdate after the camera controllers (RTSMainCamera and
/// ExplorerCameraController move the camera in LateUpdate), so the ray
/// matches the frame being rendered. It only reads input; camera controls
/// are untouched. Detection is skipped while the pointer is over UI.
///
/// Event order on a change A -> B: HoverExited(A), HoverEntered(B),
/// HoverChanged(A, B). From nothing: HoverEntered, HoverChanged(null, B).
/// To nothing: HoverExited, HoverChanged(A, null). Nothing fires while the
/// pointer stays on the same base.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)]
[AddComponentMenu("")]
public sealed class BaseHoverController : MonoBehaviour
{
    private const float CameraSearchInterval = 1f;

    [Tooltip("Log the hovered base (ID, type, level) whenever it changes.")]
    [SerializeField] private bool debugLogging = true;

    [Tooltip("Treat the pointer as over nothing while it is over a UI element.")]
    [SerializeField] private bool respectUI = true;

    private readonly List<BaseInteractionTarget> candidates = new List<BaseInteractionTarget>(512);
    private bool candidatesDirty = true;
    private bool subscribed;

    private Camera cachedCamera;
    private float nextCameraSearchTime;

    private BaseInteractionTarget hoveredTarget;

    // Last processed state; detection is skipped when nothing changed.
    private Vector2 lastScreenPosition = new Vector2(float.NaN, float.NaN);
    private Matrix4x4 lastViewProjection;
    private Camera lastCamera;
    private bool lastWasBlocked;

    public static BaseHoverController Instance { get; private set; }

    /// <summary>Raised when the pointer starts hovering a base.</summary>
    public event Action<BaseContext> HoverEntered;

    /// <summary>Raised on every change of hovered base (either side may be null).</summary>
    public event Action<BaseContext, BaseContext> HoverChanged;

    /// <summary>Raised when the pointer stops hovering a base (moved off, over UI, or base removed).</summary>
    public event Action<BaseContext> HoverExited;

    /// <summary>The hovered base, or null. Never returns a destroyed base.</summary>
    public BaseContext CurrentHover =>
        hoveredTarget != null && hoveredTarget.Context != null && hoveredTarget.Context.IsAlive
            ? hoveredTarget.Context
            : null;

    public BaseInteractionTarget CurrentTarget => CurrentHover != null ? hoveredTarget : null;

    /// <summary>Camera used for the last detection (null if none was found).</summary>
    public Camera ActiveCamera => cachedCamera;

    public bool DebugLogging
    {
        get => debugLogging;
        set => debugLogging = value;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        Instance = null;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
    }

    private void OnEnable()
    {
        EnsureInitialized();
    }

    private void OnDisable()
    {
        SetHovered(null);
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>Subscribes to registry changes. Idempotent; called from OnEnable and the test entry points.</summary>
    public void EnsureInitialized()
    {
        if (subscribed)
        {
            return;
        }

        BaseRegistry.Registered += OnTargetRegistered;
        BaseRegistry.Unregistered += OnTargetUnregistered;
        subscribed = true;
        candidatesDirty = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
        {
            return;
        }

        BaseRegistry.Registered -= OnTargetRegistered;
        BaseRegistry.Unregistered -= OnTargetUnregistered;
        subscribed = false;
    }

    private void OnTargetRegistered(BaseInteractionTarget target)
    {
        candidatesDirty = true;
    }

    private void OnTargetUnregistered(string baseId)
    {
        candidatesDirty = true;

        // The hovered base was disabled/destroyed (e.g. New World): clear now
        // so listeners never keep a base that no longer exists.
        if (!ReferenceEquals(hoveredTarget, null) && hoveredTarget.Context != null &&
            string.Equals(hoveredTarget.Context.BaseId, baseId, StringComparison.Ordinal))
        {
            SetHovered(null);
        }
    }

    private void LateUpdate()
    {
        Camera cam = ResolveCamera();
        bool hasPointer = TryReadPointer(out Vector2 screenPosition);
        bool blocked = !hasPointer || (respectUI && IsPointerOverUI());
        ProcessPointer(cam, screenPosition, blocked);
    }

    /// <summary>
    /// One detection step for a given camera and screen position. Update uses
    /// it with the live mouse; tests call it directly with synthetic input.
    /// </summary>
    public void ProcessPointer(Camera cam, Vector2 screenPosition, bool pointerBlocked)
    {
        EnsureInitialized();

        // Current hover destroyed without an unregister callback.
        if (!ReferenceEquals(hoveredTarget, null) && CurrentHover == null)
        {
            SetHovered(null);
        }

        if (pointerBlocked || cam == null || !cam.isActiveAndEnabled ||
            !cam.pixelRect.Contains(screenPosition))
        {
            lastWasBlocked = true;
            SetHovered(null);
            return;
        }

        Matrix4x4 viewProjection = cam.projectionMatrix * cam.worldToCameraMatrix;
        if (!candidatesDirty && !lastWasBlocked && cam == lastCamera &&
            screenPosition == lastScreenPosition && viewProjection == lastViewProjection)
        {
            return;
        }

        lastWasBlocked = false;
        lastCamera = cam;
        lastScreenPosition = screenPosition;
        lastViewProjection = viewProjection;

        SetHovered(Pick(cam.ScreenPointToRay(screenPosition), out _));
    }

    /// <summary>
    /// Nearest registered base whose cached main-base box the ray enters.
    /// Allocation-free; destroyed targets are skipped and trigger a refresh.
    /// </summary>
    public BaseInteractionTarget Pick(Ray ray, out float distance)
    {
        EnsureInitialized();
        RefreshCandidates();

        BaseInteractionTarget best = null;
        distance = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            BaseInteractionTarget target = candidates[i];
            if (target == null)
            {
                candidatesDirty = true;
                continue;
            }

            // World AABB rejection, then the oriented main-base box (keeps
            // sibling hangars out even when they sit inside the AABB).
            if (!target.IntersectRay(ray, out float hit) || hit >= distance)
            {
                continue;
            }

            distance = hit;
            best = target;
        }

        return best;
    }

    private void RefreshCandidates()
    {
        if (!candidatesDirty)
        {
            return;
        }

        BaseRegistry.GetAll(candidates);
        candidatesDirty = false;
    }

    private void SetHovered(BaseInteractionTarget next)
    {
        if (next != null && (next.Context == null || !next.Context.IsAlive))
        {
            next = null;
        }

        if (ReferenceEquals(next, hoveredTarget))
        {
            return;
        }

        BaseContext previous = ReferenceEquals(hoveredTarget, null) ? null : hoveredTarget.Context;
        BaseContext current = next != null ? next.Context : null;
        hoveredTarget = next;

        if (debugLogging)
        {
            if (current != null)
            {
                Debug.Log(
                    $"[BaseHover] Base ID: {current.BaseId}\nBase Type: {current.BaseType}\nBase Level: {current.Level}",
                    current.BaseObject);
            }
            else if (previous != null)
            {
                Debug.Log($"[BaseHover] No base hovered (left {previous.BaseId})");
            }
        }

        if (previous != null) Raise(HoverExited, previous);
        if (current != null) Raise(HoverEntered, current);
        if (previous != null || current != null) Raise(HoverChanged, previous, current);
    }

    private void Raise(Action<BaseContext> handlers, BaseContext context)
    {
        if (handlers == null) return;
        foreach (Action<BaseContext> handler in handlers.GetInvocationList())
        {
            try { handler(context); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    private void Raise(Action<BaseContext, BaseContext> handlers, BaseContext previous, BaseContext current)
    {
        if (handlers == null) return;
        foreach (Action<BaseContext, BaseContext> handler in handlers.GetInvocationList())
        {
            try { handler(previous, current); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    /// <summary>
    /// Main camera (both scenes tag their single gameplay camera MainCamera);
    /// falls back to the camera driven by RTSMainCamera or
    /// ExplorerCameraController. Re-searched at most once per second while missing.
    /// </summary>
    public Camera ResolveCamera()
    {
        if (cachedCamera != null && cachedCamera.isActiveAndEnabled)
        {
            return cachedCamera;
        }

        float now = Time.unscaledTime;
        if (now < nextCameraSearchTime)
        {
            return null;
        }

        nextCameraSearchTime = now + CameraSearchInterval;
        cachedCamera = FindGameplayCamera();
        return cachedCamera;
    }

    private static Camera FindGameplayCamera()
    {
        Camera main = Camera.main;
        if (main != null && main.isActiveAndEnabled) return main;

        RTSMainCamera rts = FindAnyObjectByType<RTSMainCamera>();
        if (rts != null && rts.TryGetComponent(out Camera rtsCamera) && rtsCamera.isActiveAndEnabled) return rtsCamera;

        ExplorerCameraController explorer = FindAnyObjectByType<ExplorerCameraController>();
        if (explorer != null && explorer.TryGetComponent(out Camera explorerCamera) && explorerCamera.isActiveAndEnabled)
            return explorerCamera;

        return null;
    }

    private static bool TryReadPointer(out Vector2 screenPosition)
    {
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            screenPosition = mouse.position.ReadValue();
            return true;
        }
#endif
        screenPosition = default;
        return false;
    }

    private static bool IsPointerOverUI()
    {
        EventSystem eventSystem = EventSystem.current;
        return eventSystem != null && eventSystem.IsPointerOverGameObject();
    }
}
