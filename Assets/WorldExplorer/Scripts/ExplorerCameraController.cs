using System;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// IRONVEIL World Explorer camera: an elevated, orthographic 2.5D RTS map view.
///
/// The camera always looks at a focus point on the terrain from a fixed pitch.
/// The visible area is defined by <see cref="VisibleSpan"/>, the vertical world
/// span in metres (orthographic size x 2), so the framing is identical at every
/// browser resolution; only the visible width follows the window aspect ratio.
///
/// Controls (Input System):
///   WASD / arrow keys  move the focus relative to the view (Shift = faster)
///   mouse drag         pan; the ground point under the cursor stays under it
///   scroll wheel       zoom toward the cursor
///   Q / E              rotate in fixed steps around the focus
///   R                  reset to the initial framing
///
/// Before the initial world generation completes the camera holds a close view
/// of the world centre and ignores input. On completion it focuses on the
/// generated base nearest the world centre. It never changes terrain or bases.
/// Explorer scene only; the game's RTSMainCamera is separate and unchanged.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class ExplorerCameraController : MonoBehaviour
{
    private const string GeneratedBasePrefix = "GeneratedBase_";

    [Header("References")]
    [Tooltip("World generator of this scene. Its GenerationCompleted event triggers the initial framing.")]
    [SerializeField] private WorldGenerationPrototype worldGenerator;
    [Tooltip("BaseSpawner whose GeneratedBase_* children are the placed bases.")]
    [SerializeField] private BaseSpawner baseSpawner;

    [Header("View")]
    [Tooltip("Camera pitch above the horizon in degrees.")]
    [SerializeField, Range(30f, 80f)] private float pitch = 50f;
    [Tooltip("Initial view direction in degrees (0 = looking north, +Z).")]
    [SerializeField] private float initialYaw = 45f;
    [Tooltip("Rotation per Q/E press in degrees.")]
    [SerializeField, Range(15f, 90f)] private float rotationStep = 45f;
    [SerializeField, Min(0.01f)] private float rotationSmoothTime = 0.15f;
    [Tooltip("Camera distance from the focus at the closest zoom. The distance grows with the zoom so the nearest visible ground never falls behind the near plane. Keep it below the URP shadow distance (50 m) to keep shadows at close zoom.")]
    [SerializeField, Min(5f)] private float baseDistance = 40f;
    [SerializeField, Min(0.01f)] private float nearClipPlane = 0.3f;
    [SerializeField, Min(10f)] private float farClipPlane = 1000f;

    [Header("Zoom (visible vertical world span, metres)")]
    [SerializeField, Min(0.5f)] private float initialSpan = 10f;
    [SerializeField, Min(0.5f)] private float minSpan = 3f;
    [SerializeField, Min(1f)] private float maxSpan = 150f;
    [Tooltip("Fraction of the span removed per scroll notch (scroll input is normalised to 1 per notch).")]
    [SerializeField, Range(0.02f, 0.5f)] private float zoomStepPerNotch = 0.15f;
    [SerializeField, Min(0.01f)] private float zoomSmoothTime = 0.1f;

    [Header("Movement")]
    [Tooltip("Keyboard speed in visible spans (screen heights) per second, so movement feels the same at every zoom.")]
    [SerializeField, Min(0.05f)] private float keyboardSpeed = 0.8f;
    [SerializeField, Min(1f)] private float fastMultiplier = 3f;
    [SerializeField] private bool panWithLeftButton = true;
    [SerializeField] private bool panWithMiddleButton = true;
    [SerializeField] private bool panWithRightButton = true;
    [Tooltip("Pointer movement (pixels) before a press becomes a pan, so a plain click stays a click for later UI.")]
    [SerializeField, Min(0f)] private float dragThresholdPixels = 4f;

    [Header("World and terrain")]
    [Tooltip("Used only when the generator's world bounds are unavailable.")]
    [SerializeField, Min(1f)] private float fallbackWorldHalfExtent = 450f;
    [Tooltip("Focus height used before the terrain exists or where it cannot be sampled.")]
    [SerializeField] private float fallbackFocusHeight = 12f;
    [SerializeField, Min(0.01f)] private float heightSmoothTime = 0.12f;

    private Camera cam;
    private bool subscribed;

    // View state. The camera transform is derived from these every frame.
    private Vector2 focusXZ;
    private float focusY;
    private float focusYVelocity;
    private float span;
    private float targetSpan;
    private float spanVelocity;
    private float yaw;
    private float targetYaw;
    private float yawVelocity;

    // Zoom-to-cursor anchor (screen point whose ground point is kept fixed while the zoom eases).
    private bool zoomAnchorActive;
    private Vector2 zoomAnchorScreen;

    // Mouse drag pan.
    private bool pointerDown;
    private bool dragging;
    // Set when a press begins over the UI; that press never pans, even after the
    // pointer leaves the UI, until every pan button is released.
    private bool pressStartedOverUI;
    private Vector2 pressScreen;
    private Vector3 dragGround;
    private float dragPlaneY;
    private Vector2 dragPointer;

    private Vector2 worldCenter;
    private float worldHalfX;
    private float worldHalfZ;

    /// <summary>True once the camera has been framed on the generated world.</summary>
    public bool IsFramed { get; private set; }
    /// <summary>Base focused by the last initial framing or ResetView (null before generation).</summary>
    public Transform FocusBase { get; private set; }
    /// <summary>Current focus point on the terrain.</summary>
    public Vector3 Focus => new Vector3(focusXZ.x, focusY, focusXZ.y);
    /// <summary>Current visible vertical world span in metres.</summary>
    public float VisibleSpan => span;
    /// <summary>Current view direction in degrees.</summary>
    public float Yaw => yaw;
    /// <summary>Raised after the initial framing and after every ResetView.</summary>
    public event Action<ExplorerCameraController> ViewReset;

    private void Awake()
    {
        cam = GetComponent<Camera>();
        cam.orthographic = true;
        cam.nearClipPlane = nearClipPlane;
        cam.farClipPlane = farClipPlane;
        if (baseSpawner == null) baseSpawner = FindAnyObjectByType<BaseSpawner>();
        if (worldGenerator == null) worldGenerator = FindAnyObjectByType<WorldGenerationPrototype>();
        ReadWorldBounds();

        // Close view of the world centre while the world generates (never the distant overview).
        focusXZ = worldCenter;
        focusY = fallbackFocusHeight;
        span = targetSpan = ClampSpan(initialSpan);
        yaw = targetYaw = initialYaw;
        ApplyPose();
    }

    private void OnEnable()
    {
        if (worldGenerator != null && !subscribed)
        {
            worldGenerator.GenerationCompleted += HandleGenerationCompleted;
            subscribed = true;
        }
    }

    private void OnDisable()
    {
        if (worldGenerator != null && subscribed)
        {
            worldGenerator.GenerationCompleted -= HandleGenerationCompleted;
            subscribed = false;
        }
        CancelPointer();
    }

    private void Start()
    {
        // Late subscriber: the generation may already have completed.
        if (!IsFramed && worldGenerator != null && worldGenerator.IsGenerationComplete)
        {
            ResetView();
        }
    }

    private void HandleGenerationCompleted(WorldGenerationPrototype generator)
    {
        ResetView();
    }

    /// <summary>
    /// Focuses on the generated base nearest the world centre with the initial
    /// zoom and rotation. Call again after the bases change (New World).
    /// </summary>
    public void ResetView()
    {
        ReadWorldBounds();
        FocusBase = FindBaseNearestCenter(out Vector3 basePoint);
        focusXZ = FocusBase != null ? new Vector2(basePoint.x, basePoint.z) : worldCenter;
        ClampFocus();
        focusY = SampleTerrainHeight(focusXZ, fallbackFocusHeight);
        focusYVelocity = 0f;
        span = targetSpan = ClampSpan(initialSpan);
        spanVelocity = 0f;
        yaw = targetYaw = initialYaw;
        yawVelocity = 0f;
        zoomAnchorActive = false;
        CancelPointer();
        IsFramed = true;
        ApplyPose();
        ViewReset?.Invoke(this);
    }

    /// <summary>Moves the focus to a world position, keeping zoom and rotation.</summary>
    public void FocusOn(Vector3 worldPoint)
    {
        focusXZ = new Vector2(worldPoint.x, worldPoint.z);
        ClampFocus();
        focusY = SampleTerrainHeight(focusXZ, focusY);
        focusYVelocity = 0f;
        zoomAnchorActive = false;
        CancelPointer();
        ApplyPose();
    }

    private void LateUpdate()
    {
        if (cam == null) return;
        if (!IsFramed)
        {
            ApplyPose();
            return;
        }

        float dt = Time.unscaledDeltaTime;
        HandleInput(dt);

        // Rotation eases toward the 45-degree step target around the focus.
        yaw = Mathf.SmoothDampAngle(yaw, targetYaw, ref yawVelocity, rotationSmoothTime, Mathf.Infinity, dt);

        // Zoom eases toward the target; the anchored ground point stays under the cursor.
        if (!Mathf.Approximately(span, targetSpan))
        {
            Vector3 before = default;
            bool anchored = zoomAnchorActive && TryGroundPoint(zoomAnchorScreen, focusY, out before);
            span = Mathf.SmoothDamp(span, targetSpan, ref spanVelocity, zoomSmoothTime, Mathf.Infinity, dt);
            if (Mathf.Abs(span - targetSpan) < targetSpan * 0.0005f) { span = targetSpan; spanVelocity = 0f; }
            if (anchored)
            {
                ApplyPose();
                if (TryGroundPoint(zoomAnchorScreen, focusY, out Vector3 after))
                {
                    focusXZ.x += before.x - after.x;
                    focusXZ.y += before.z - after.z;
                }
            }
        }
        else
        {
            zoomAnchorActive = false;
        }

        ClampFocus();

        // Follow the terrain height under the focus smoothly (hills, river valley).
        float terrainY = SampleTerrainHeight(focusXZ, focusY);
        focusY = Mathf.SmoothDamp(focusY, terrainY, ref focusYVelocity, heightSmoothTime, Mathf.Infinity, dt);

        if (dragging) ApplyDragPan();

        ApplyPose();
    }

    /// <summary>
    /// Moves the focus so the ground point grabbed at the press stays under the
    /// cursor. Runs after the height update so the vertical camera movement from
    /// terrain following is included in this frame's correction.
    /// </summary>
    private void ApplyDragPan()
    {
        ApplyPose();
        if (TryGroundPoint(dragPointer, dragPlaneY, out Vector3 current))
        {
            focusXZ.x += dragGround.x - current.x;
            focusXZ.y += dragGround.z - current.z;
            ClampFocus();
        }
    }

    private void HandleInput(float dt)
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        Mouse mouse = Mouse.current;

        if (keyboard != null)
        {
            if (keyboard.rKey.wasPressedThisFrame)
            {
                ResetView();
                return;
            }
            if (keyboard.qKey.wasPressedThisFrame) targetYaw -= rotationStep;
            if (keyboard.eKey.wasPressedThisFrame) targetYaw += rotationStep;

            float forward = 0f, right = 0f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) forward += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) forward -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) right += 1f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) right -= 1f;
            if (forward != 0f || right != 0f)
            {
                // Movement follows the current view direction on the ground plane.
                float yawRad = yaw * Mathf.Deg2Rad;
                Vector2 fwd = new Vector2(Mathf.Sin(yawRad), Mathf.Cos(yawRad));
                Vector2 rgt = new Vector2(fwd.y, -fwd.x);
                Vector2 move = fwd * forward + rgt * right;
                if (move.sqrMagnitude > 1f) move.Normalize();
                float speed = keyboardSpeed * span;
                if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) speed *= fastMultiplier;
                focusXZ += move * (speed * dt);
                zoomAnchorActive = false;
            }
        }

        if (mouse != null)
        {
            Vector2 pointer = mouse.position.ReadValue();
            bool panButton = (panWithLeftButton && mouse.leftButton.isPressed) ||
                             (panWithMiddleButton && mouse.middleButton.isPressed) ||
                             (panWithRightButton && mouse.rightButton.isPressed);

            if (!panButton)
            {
                pressStartedOverUI = false;
            }
            else if (!pointerDown && !pressStartedOverUI && PointerOverUI())
            {
                pressStartedOverUI = true;
            }

            if (panButton && !pointerDown && !pressStartedOverUI)
            {
                // A press that starts over UI never pans the map (see pressStartedOverUI).
                if (PointerInsideView(pointer))
                {
                    pointerDown = true;
                    dragging = false;
                    pressScreen = pointer;
                    dragPlaneY = focusY;
                    if (!TryGroundPoint(pointer, dragPlaneY, out dragGround)) pointerDown = false;
                }
            }
            else if (!panButton && pointerDown)
            {
                CancelPointer();
            }

            if (pointerDown)
            {
                if (!dragging && (pointer - pressScreen).sqrMagnitude >= dragThresholdPixels * dragThresholdPixels)
                {
                    dragging = true;
                }
                if (dragging)
                {
                    // The pan itself is applied in LateUpdate after the height update (ApplyDragPan).
                    dragPointer = pointer;
                    zoomAnchorActive = false;
                }
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f && !PointerOverUI() && PointerInsideView(pointer))
            {
                float notches = Mathf.Clamp(scroll, -3f, 3f);
                targetSpan = ClampSpan(targetSpan * Mathf.Pow(1f - zoomStepPerNotch, notches));
                zoomAnchorActive = true;
                zoomAnchorScreen = pointer;
            }
        }
#endif
    }

    private void CancelPointer()
    {
        pointerDown = false;
        dragging = false;
    }

    private static bool PointerOverUI()
    {
        EventSystem eventSystem = EventSystem.current;
        return eventSystem != null && eventSystem.IsPointerOverGameObject();
    }

    private static bool PointerInsideView(Vector2 pointer)
    {
        return pointer.x >= 0f && pointer.y >= 0f && pointer.x <= Screen.width && pointer.y <= Screen.height;
    }

    /// <summary>Places the camera from the current view state.</summary>
    private void ApplyPose()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        cam.orthographicSize = span * 0.5f;
        Vector3 focus = new Vector3(focusXZ.x, focusY, focusXZ.y);
        transform.SetPositionAndRotation(focus - rotation * Vector3.forward * CurrentDistance(), rotation);
    }

    /// <summary>
    /// Distance from the focus: baseDistance plus the depth of the lower half of
    /// the view, so the nearest visible ground stays in front of the near plane
    /// at every zoom.
    /// </summary>
    private float CurrentDistance()
    {
        return baseDistance + span * 0.5f / Mathf.Tan(pitch * Mathf.Deg2Rad);
    }

    /// <summary>Intersects the camera ray through a screen point with a horizontal plane.</summary>
    private bool TryGroundPoint(Vector2 screenPoint, float planeY, out Vector3 point)
    {
        Ray ray = cam.ScreenPointToRay(screenPoint);
        if (Mathf.Abs(ray.direction.y) < 1e-4f)
        {
            point = default;
            return false;
        }
        float t = (planeY - ray.origin.y) / ray.direction.y;
        point = ray.origin + ray.direction * t;
        return true;
    }

    private float SampleTerrainHeight(Vector2 xz, float fallback)
    {
        if (worldGenerator != null && worldGenerator.IsGenerated &&
            worldGenerator.TrySampleGeneratedTerrain(xz.x, xz.y, out float height, out _))
        {
            return height;
        }
        return fallback;
    }

    private void ReadWorldBounds()
    {
        if (worldGenerator != null)
        {
            Bounds bounds = worldGenerator.PrototypeWorldBounds;
            if (bounds.size.x > 1f && bounds.size.z > 1f)
            {
                worldCenter = new Vector2(bounds.center.x, bounds.center.z);
                worldHalfX = bounds.extents.x;
                worldHalfZ = bounds.extents.z;
                return;
            }
        }
        worldCenter = Vector2.zero;
        worldHalfX = worldHalfZ = fallbackWorldHalfExtent;
    }

    private void ClampFocus()
    {
        focusXZ.x = Mathf.Clamp(focusXZ.x, worldCenter.x - worldHalfX, worldCenter.x + worldHalfX);
        focusXZ.y = Mathf.Clamp(focusXZ.y, worldCenter.y - worldHalfZ, worldCenter.y + worldHalfZ);
    }

    private float ClampSpan(float value)
    {
        return Mathf.Clamp(value, minSpan, Mathf.Max(minSpan, maxSpan));
    }

    /// <summary>
    /// Deterministic choice of the generated base nearest the world centre:
    /// each base is located by the bounds of its renderers excluding its naval
    /// or rail hangar (the GeneratedBase_ roots themselves sit at the origin).
    /// Ties resolve by name. Runs only on framing, never per frame.
    /// </summary>
    private Transform FindBaseNearestCenter(out Vector3 basePoint)
    {
        basePoint = default;
        if (baseSpawner == null) return null;
        Transform parent = baseSpawner.transform;
        Transform best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform root = parent.GetChild(i);
            if (!root.gameObject.activeInHierarchy ||
                !root.name.StartsWith(GeneratedBasePrefix, StringComparison.Ordinal)) continue;
            if (!TryGetBasePoint(root, out Vector3 point)) continue;
            float distance = (new Vector2(point.x, point.z) - worldCenter).sqrMagnitude;
            if (distance < bestDistance - 1e-6f ||
                (Mathf.Abs(distance - bestDistance) <= 1e-6f && best != null &&
                 string.CompareOrdinal(root.name, best.name) < 0))
            {
                best = root;
                bestDistance = distance;
                basePoint = point;
            }
        }
        return best;
    }

    private static bool TryGetBasePoint(Transform root, out Vector3 point)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
        bool any = false;
        Bounds bounds = default;
        foreach (Renderer renderer in renderers)
        {
            if (UnderHangar(renderer.transform, root)) continue;
            if (!any) { bounds = renderer.bounds; any = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        point = any ? bounds.center : default;
        return any;
    }

    private static bool UnderHangar(Transform t, Transform root)
    {
        for (Transform p = t; p != null && p != root; p = p.parent)
        {
            if (p.name.IndexOf("Hangar", StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }
}
