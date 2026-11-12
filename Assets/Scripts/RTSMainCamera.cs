using UnityEngine;
using UnityEngine.EventSystems;
using System.Collections;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// RTS-style main camera controller for IRONVEIL.
/// Provides elevated isometric view, smooth zoom/panning, and boundary clamping.
/// Designed to attach to the Main Camera in Ironveil - MainBuild.
/// </summary>
[RequireComponent(typeof(Camera))]
public class RTSMainCamera : MonoBehaviour
{
    [Header("Camera Settings")]
    [Tooltip("Orthographic gives clean RTS scaling; Perspective keeps original feel.")]
    [SerializeField] private bool useOrthographic = true;

    [Tooltip("Zoom speed in orthographic units per scroll tick.")]
    [SerializeField] private float zoomSpeed = 5f;

    [Tooltip("Pan speed in units per second.")]
    [SerializeField] private float panSpeed = 10f;

    [Tooltip("Minimum orthographic size (zoom in max).")]
    [SerializeField] private float minOrthographicSize = 10f;

    [Tooltip("Maximum orthographic size (zoom out max).")]
    [SerializeField] private float maxOrthographicSize = 150f;

    [Tooltip("Smooth time for zoom transitions (seconds).")]
    [SerializeField] private float zoomSmoothTime = 0.2f;

    [Tooltip("Smooth time for pan transitions (seconds).")]
    [SerializeField] private float panSmoothTime = 0.3f;

    [Header("Elevated View Settings")]
    [Tooltip("Elevation angle above the battlefield (degrees).")]
    [SerializeField] private float elevationAngle = 60f;

    [Tooltip("Yaw offset from forward (degrees). Positive = rightward yaw.")]
    [SerializeField] private float yawOffset = 0f;

    [Tooltip("Distance from map centre to camera position (height above map).")]
    [SerializeField] private float cameraDistance = 300f;

    [Tooltip("How far the camera leads the pan direction (units).")]
    [SerializeField] private float panLead = 20f;

    [Header("Mobile Settings")]
    [Tooltip("Pinch zoom sensitivity multiplier.")]
    [SerializeField] private float pinchSensitivity = 1f;

    [Header("Bounds / Map Framing")]
    [Tooltip("Auto-calculate from generated base positions at Start.")]
    [SerializeField] private bool autoCalculateBounds = true;

    [Tooltip("Manual override of map half-extents (XZ). Use if auto-calc fails.")]
    [SerializeField] private Vector2 mapHalfExtents = new Vector2(200f, 200f);

    [Tooltip("Height floor for camera (prevents going underground).")]
    [SerializeField] private float minHeight = 5f;

    [Tooltip("World generator whose GenerationCompleted event triggers bounds calculation from the spawned bases. Leave empty to use the timed detection and Map Half Extents fallback.")]
    [SerializeField] private WorldGenerationPrototype worldGenerator;

    [Header("Initial View")]
    [Tooltip("Orthographic size (half the visible height, in world units) used when the camera first frames the map. 0 = fit the whole map.")]
    [SerializeField, Min(0f)] private float initialOrthographicSize = 0f;

    [Tooltip("Start looking at Initial Focus (world X/Z) instead of the map centre.")]
    [SerializeField] private bool useInitialFocus = false;

    [Tooltip("World X/Z point the camera looks at when it first frames the map.")]
    [SerializeField] private Vector2 initialFocus = Vector2.zero;

    // Runtime state
    private Camera _cam;
    private float _currentZoom;
    private Vector3 _currentPan;
    private Vector3 _velocityZoom;
    private Vector3 _velocityPan;

    // Calculated map bounds (XZ plane)
    private Vector3 _mapCenter;
    private float _mapSizeX;
    private float _mapSizeZ;

    // Touch tracking
    private Touch _touchZeroStart;
    private Touch _touchOneStart;
    private float _pinchStartDist;

    // Generation completion detection
    private bool _generationCompleted = false;
    private bool _generationInProgress = false;
    private float _generationStartTime = float.MaxValue;

    // Event-driven completion (assigned world generator)
    private bool _subscribedToGenerator;
    private bool _started;

    /// <summary>
    /// True when bounds come from the generator's GenerationCompleted event
    /// rather than the timed detection.
    /// </summary>
    private bool UsesGenerationEvent => autoCalculateBounds && worldGenerator != null;

    void OnEnable()
    {
        _cam = GetComponent<Camera>();
        if (useOrthographic)
            _cam.orthographic = true;

        // Record generation start time
        _generationStartTime = Time.time;

        // Auto-calculate map bounds from generated bases
        if (UsesGenerationEvent)
        {
            SubscribeToGenerator();
        }
        else if (autoCalculateBounds)
        {
            // No generator assigned: original timed detection.
            // Don't calculate bounds before generation is complete
            // We'll do this in Start() after a short delay
            Invoke("CalculateBoundsAfterDelay", 0.5f);
        }

        // Set initial zoom
        if (_cam.orthographic)
            SetZoomImmediate(minOrthographicSize);

        // Ensure we start above the terrain
        _cam.transform.position = new Vector3(_cam.transform.position.x,
            Mathf.Max(_cam.transform.position.y, minHeight),
            _cam.transform.position.z);

        // Re-enabled after generation finished: take the bounds from the real
        // bases now. (On the first enable, Start applies the initial framing.)
        if (UsesGenerationEvent && _started && worldGenerator.IsGenerationComplete)
        {
            _generationCompleted = true;
            CalculateMapBoundsFromBases();
        }
    }

    void OnDisable()
    {
        UnsubscribeFromGenerator();
    }

    void Start()
    {
        _started = true;

        if (UsesGenerationEvent)
        {
            if (worldGenerator.IsGenerationComplete)
            {
                // Completed before the first frame: bases already exist.
                SetGenerationCompleted();
            }
            else
            {
                // Initial framing now; real bounds arrive with the event.
                ReframeMap();
            }
            return;
        }

        // Second pass: ensure bounds are calculated after generation completes
        if (autoCalculateBounds)
            EnsureBoundsCalculated();

        // Apply initial framing
        ReframeMap();
    }

    private void SubscribeToGenerator()
    {
        if (_subscribedToGenerator || worldGenerator == null) return;
        worldGenerator.GenerationCompleted += HandleGenerationCompleted;
        _subscribedToGenerator = true;
    }

    private void UnsubscribeFromGenerator()
    {
        if (!_subscribedToGenerator) return;
        if (worldGenerator != null)
            worldGenerator.GenerationCompleted -= HandleGenerationCompleted;
        _subscribedToGenerator = false;
    }

    private void HandleGenerationCompleted(WorldGenerationPrototype generator)
    {
        SetGenerationCompleted();
    }

    /// <summary>
    /// Call this after base generation completes to reframe the camera to the full map.
    /// </summary>
    public void SetGenerationCompleted()
    {
        _generationCompleted = true;
        ReframeMap();
    }

    /// <summary>
    /// Marks that generation is currently in progress (called by BaseSpawner).
    /// </summary>
    public void SetGenerationInProgress(bool inProgress)
    {
        _generationInProgress = inProgress;
    }

    /// <summary>
    /// Ensures bounds are calculated, waiting for generation to complete if needed.
    /// </summary>
    private void EnsureBoundsCalculated()
    {
        // If generation just finished, calculate bounds immediately
        if (_generationCompleted)
        {
            CalculateMapBoundsFromBases();
            return;
        }

        // If generation is still in progress, we'll retry after a delay
        if (_generationInProgress)
        {
            Invoke("EnsureBoundsCalculated", 1.0f);
            return;
        }

        // Generation not started or already done - calculate whatever we can
        CalculateMapBoundsFromBases();
    }

    /// <summary>
    /// Wait for generation to settle, then calculate bounds.
    /// </summary>
    private void CalculateBoundsAfterDelay()
    {
        // Small wait to let bases be placed
        Invoke("CalculateMapBoundsFromBases", 2.0f);
    }

    /// <summary>
    /// Calculate map bounds from all generated base root objects.
    /// Uses the actual naming convention: GeneratedBase_{variant}_{index:000}
    /// </summary>
    private void CalculateMapBoundsFromBases()
    {
        _mapCenter = Vector3.zero;
        _mapSizeX = 0f;
        _mapSizeZ = 0f;
        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;
        int baseCount = 0;
        bool foundAny = false;

        // Strategy 1: Find generated base root objects by name pattern
        // The BaseSpawner creates objects named "GeneratedBase_{variant}_{index:000}"
        // under its own transform. We need to find the BaseSpawner first.
        GameObject[] allSceneObjects = FindObjectsOfType<GameObject>();

        foreach (GameObject obj in allSceneObjects)
        {
            // Check if this object is a generated base root
            // Naming convention: GeneratedBase_{Variant}_{Index}
            if (obj.name.StartsWith("GeneratedBase_"))
            {
                if (ProcessBaseObject(obj, ref minX, ref maxX, ref minZ, ref maxZ))
                {
                    baseCount++;
                    foundAny = true;
                }
            }
        }

        // Strategy 2: If no generated bases found by name, try finding by tag
        // as a fallback (some bases may have been tagged at runtime)
        if (!foundAny)
        {
            string[] baseTags = { "TankBase", "HeliBase", "AirBase", "NavalBase", "RailBase",
                                  "HeliNavalBase", "HeliRailBase", "AirNavalBase", "AirRailBase" };

            foreach (string tag in baseTags)
            {
                GameObject[] taggedBases;
                try
                {
                    taggedBases = GameObject.FindGameObjectsWithTag(tag);
                }
                catch (UnityException)
                {
                    // A variant tag may not be defined in this project's TagManager.
                    continue;
                }

                foreach (GameObject taggedBase in taggedBases)
                {
                    if (ProcessBaseObject(taggedBase, ref minX, ref maxX, ref minZ, ref maxZ))
                    {
                        baseCount++;
                        foundAny = true;
                    }
                }
            }
        }

        // Strategy 3: Fallback to manual extents
        if (!foundAny || baseCount == 0)
        {
            _mapCenter = Vector3.zero;
            _mapSizeX = mapHalfExtents.x;
            _mapSizeZ = mapHalfExtents.y;
            Debug.LogWarning(
                "RTSMainCamera: Could not detect any generated base objects. " +
                "Using manual mapHalfExtents = " + mapHalfExtents +
                ". Ensure bases have 'GeneratedBase_' in their name, or have correct tags.");
            return;
        }

        // The bounds are accumulated from the world-space XZ positions of the
        // generated roots. Store their midpoint and half-extents separately.
        _mapCenter = new Vector3(
            (minX + maxX) * 0.5f,
            0f,
            (minZ + maxZ) * 0.5f);
        _mapSizeX = (maxX - minX) * 0.5f;
        _mapSizeZ = (maxZ - minZ) * 0.5f;

        // Add margin so camera doesn't sit exactly on the edge
        _mapSizeX = Mathf.Max(_mapSizeX, 50f) + 50f; // minimum 50f margin
        _mapSizeZ = Mathf.Max(_mapSizeZ, 50f) + 50f;

        // Clamp to reasonable ranges so the camera doesn't go to extremes.
        // The configured map half-extents may raise the upper limit for
        // larger battlefields.
        _mapSizeX = Mathf.Clamp(_mapSizeX, 100f, Mathf.Max(500f, mapHalfExtents.x));
        _mapSizeZ = Mathf.Clamp(_mapSizeZ, 100f, Mathf.Max(500f, mapHalfExtents.y));

        Debug.Log(
            "RTSMainCamera: Map bounds calculated from " + baseCount + " base(s). " +
            "Center=" + _mapCenter +
            " SizeX=" + _mapSizeX + " SizeZ=" + _mapSizeZ);
        Debug.Log(
            "RTSMainCamera: Initial orthographic size = " + CalculateDesiredOrthoSize());
    }

    /// <summary>
    /// Process a single base object, extracting its position for bounds calculation.
    /// </summary>
    /// <param name="baseObj">The base GameObject to process</param>
    private bool ProcessBaseObject(
        GameObject baseObj,
        ref float minX,
        ref float maxX,
        ref float minZ,
        ref float maxZ)
    {
        if (baseObj == null) return false;

        // Skip hangars and non-base objects
        if (baseObj.name.Contains("Hangar")) return false;
        if (baseObj.name.Contains("Test")) return false;
        if (baseObj.name.Contains("Prototype")) return false;
        if (baseObj.name.Contains("Editor")) return false;

        // Extract position from the root/center of the base
        // Generated bases have their root at the base position
        Vector3 pos = baseObj.transform.position;

        // Accumulate world-space XZ bounds; terrain height does not affect map framing.
        minX = Mathf.Min(minX, pos.x);
        maxX = Mathf.Max(maxX, pos.x);
        minZ = Mathf.Min(minZ, pos.z);
        maxZ = Mathf.Max(maxZ, pos.z);
        return true;
    }

    /// <summary>
    /// Calculate the desired orthographic size based on map dimensions and aspect ratio.
    /// </summary>
    /// <returns>The desired orthographic size.</returns>
    private float CalculateDesiredOrthoSize()
    {
        if (_mapSizeX <= 0 || _mapSizeZ <= 0) return minOrthographicSize;

        // Fit the map in a tilted orthographic view (size = half the visible
        // height). The map's depth appears foreshortened by sin(pitch); its
        // width must fit the visible height times the aspect ratio.
        float aspect = Mathf.Max(0.1f, _cam != null ? _cam.aspect : (float)Screen.width / Screen.height);
        float pitch = Mathf.Clamp(elevationAngle, 1f, 90f) * Mathf.Deg2Rad;
        float halfHeightForDepth = _mapSizeZ * Mathf.Sin(pitch);
        float halfHeightForWidth = _mapSizeX / aspect;
        return Mathf.Clamp(
            Mathf.Max(halfHeightForDepth, halfHeightForWidth) * 1.05f,
            minOrthographicSize,
            maxOrthographicSize);
    }

    private void SetZoomImmediate(float size)
    {
        _currentZoom = size;
        _cam.orthographicSize = size;
    }

    private void LateUpdate()
    {
        if (_cam == null) return;

        // --- Generation completion check ---
        // If generation was marked completed, ensure we stay framed.
        // No extra work needed beyond normal update loop.

        // Handle input (desktop and mobile)
        HandleInput();

        // Ease the orthographic size toward the requested zoom every frame.
        if (_cam.orthographic)
        {
            _cam.orthographicSize = Mathf.Lerp(
                _cam.orthographicSize,
                _currentZoom,
                1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, zoomSmoothTime)));
        }

        // Clamp camera to map boundaries
        ClampToMapBounds();

        // Apply smoothed position with elevated rotation
        ApplyCameraPosition();
    }

    /// <summary>
    /// Handle all input: desktop mouse and mobile touch.
    /// </summary>
    private void HandleInput()
    {
        // Safe EventSystem check: if no EventSystem exists, allow camera input
        bool pointerOverUI = false;
        if (EventSystem.current != null)
        {
            pointerOverUI = EventSystem.current.IsPointerOverGameObject();
        }

        // Read pointer state. The project uses the Input System package; the
        // legacy UnityEngine.Input API throws when only that is enabled.
        Vector2 dragDelta = Vector2.zero;
        float scroll = 0f;
        int touchCount = 0;
        Vector2 touchZero = Vector2.zero;
        Vector2 touchOne = Vector2.zero;
        Vector2 touchDelta = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed) dragDelta = mouse.delta.ReadValue();
            scroll = mouse.scroll.ReadValue().y;
        }

        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen != null)
        {
            foreach (var touch in touchscreen.touches)
            {
                if (!touch.press.isPressed) continue;
                if (touchCount == 0)
                {
                    touchZero = touch.position.ReadValue();
                    touchDelta = touch.delta.ReadValue();
                }
                else if (touchCount == 1)
                {
                    touchOne = touch.position.ReadValue();
                }
                touchCount++;
            }
        }
#else
        if (Input.GetMouseButton(1)) dragDelta = Input.mousePositionDelta;
        scroll = Input.mouseScrollDelta.y;
        touchCount = Input.touchCount;
        if (touchCount >= 1) { touchZero = Input.GetTouch(0).position; touchDelta = Input.GetTouch(0).deltaPosition; }
        if (touchCount >= 2) touchOne = Input.GetTouch(1).position;
#endif

        if (touchCount < 2) _pinchStartDist = 0f;
        if (pointerOverUI) return;

        // World units per screen pixel at the current zoom, so a drag keeps
        // the terrain under the pointer at every zoom level.
        float unitsPerPixel = 2f * _cam.orthographicSize /
                              Mathf.Max(1, Screen.height) * (panSpeed / 10f);

        // --- Desktop Input ---
        // Right mouse button: pan
        if (dragDelta != Vector2.zero)
        {
            _currentPan.x -= dragDelta.x * unitsPerPixel;
            _currentPan.z -= dragDelta.y * unitsPerPixel;
        }

        // Mouse wheel: each notch changes the zoom by zoomSpeed * 2 percent.
        if (scroll != 0f)
        {
            float step = Mathf.Sign(scroll) * zoomSpeed * 0.02f;
            _currentZoom = Mathf.Clamp(
                _currentZoom * (1f - step),
                minOrthographicSize,
                maxOrthographicSize);
        }

        // --- Mobile Input ---
        // One-finger drag: pan
        if (touchCount == 1)
        {
            _currentPan.x -= touchDelta.x * unitsPerPixel;
            _currentPan.z -= touchDelta.y * unitsPerPixel;
        }
        // Two-finger pinch: spreading the fingers zooms in.
        else if (touchCount >= 2)
        {
            float currentDist = Mathf.Max(Vector2.Distance(touchZero, touchOne), 0.1f);
            if (_pinchStartDist > 0f)
            {
                float ratio = Mathf.Pow(_pinchStartDist / currentDist, pinchSensitivity);
                _currentZoom = Mathf.Clamp(
                    _currentZoom * ratio,
                    minOrthographicSize,
                    maxOrthographicSize);
            }

            _pinchStartDist = currentDist;
        }
    }

    /// <summary>
    /// Apply the camera position with elevated RTS/isometric rotation.
    /// The camera orbits around the map center at the configured elevation angle.
    /// </summary>
    private void ApplyCameraPosition()
    {
        // Calculate the camera's target position (map centre + pan offset)
        Vector3 targetOffset = _currentPan;

        // Calculate the elevated position around the map centre
        // We use spherical coordinates: elevationAngle above the plane, 
        // with yaw determined by camera orientation
        float yaw = yawOffset; // Can be adjusted based on gameplay needs
        float pitch = elevationAngle;

        // Direction vector from camera to target
        float sinPitch = Mathf.Sin(pitch * Mathf.Deg2Rad);
        float cosPitch = Mathf.Cos(pitch * Mathf.Deg2Rad);
        float sinYaw = Mathf.Sin(yaw * Mathf.Deg2Rad);
        float cosYaw = Mathf.Cos(yaw * Mathf.Deg2Rad);

        // Camera position: at distance, elevated, looking at centre
        Vector3 camDir = new Vector3(
            sinYaw * cosPitch,
            sinPitch,
            -cosYaw * cosPitch);

        // camDir points from the target up toward the camera (its y is
        // +sin(pitch)), so the camera sits along +camDir. Subtracting it put
        // the camera below the map, where minHeight clamped it to ground level.
        Vector3 desiredPos = _mapCenter + targetOffset + camDir * cameraDistance;

        // Prevent camera from going below minimum height
        desiredPos.y = Mathf.Max(desiredPos.y, minHeight);

        // Smoothly move the camera to the desired position
        _cam.transform.position = Vector3.SmoothDamp(
            _cam.transform.position,
            desiredPos,
            ref _velocityPan,
            panSmoothTime * Time.deltaTime);

        // Keep Y at whatever it ended up being, but minimum minHeight
        _cam.transform.position = new Vector3(
            _cam.transform.position.x,
            Mathf.Max(_cam.transform.position.y, minHeight),
            _cam.transform.position.z);

        // Look at the panned target so panning moves the view instead of
        // turning the camera around the map centre.
        _cam.transform.LookAt(_mapCenter + targetOffset + Vector3.up * minHeight * 0.5f);
    }

    /// <summary>
    /// Clamp camera to map boundaries so it cannot escape the playable area.
    /// </summary>
    private void ClampToMapBounds()
    {
        if (_mapSizeX <= 0 || _mapSizeZ <= 0) return;

        // Keep the point the camera looks at inside the map. The camera itself
        // sits cameraDistance back from that point, so clamping the camera
        // position would distort the view near the edges.
        _currentPan.x = Mathf.Clamp(_currentPan.x, -_mapSizeX, _mapSizeX);
        _currentPan.z = Mathf.Clamp(_currentPan.z, -_mapSizeZ, _mapSizeZ);
    }

    /// <summary>
    /// Reframe the camera to the full generated battlefield.
    /// </summary>
    public void ReframeMap()
    {
        bool awaitingGeneration = UsesGenerationEvent && !_generationCompleted;
        if (awaitingGeneration)
        {
            // Generation still running: no bases to detect yet. Use the
            // configured half-extents until GenerationCompleted arrives.
            _mapCenter = Vector3.zero;
            _mapSizeX = mapHalfExtents.x;
            _mapSizeZ = mapHalfExtents.y;
        }
        else
        {
            // Calculate bounds from generated bases
            CalculateMapBoundsFromBases();
        }

        // Set the starting zoom (the whole map unless an initial size is set)
        SetZoomImmediate(InitialZoom());

        if (useInitialFocus)
        {
            _currentPan = new Vector3(
                initialFocus.x - _mapCenter.x,
                0f,
                initialFocus.y - _mapCenter.z);
        }

        // Small delay to let bases settle, then re-confirm the frame.
        // Not needed with the generation event: bases are spawned
        // synchronously before GenerationCompleted is raised.
        if (!UsesGenerationEvent)
            StartCoroutine(ReframeAfterDelay());
    }

    private float InitialZoom()
    {
        return initialOrthographicSize > 0f
            ? Mathf.Clamp(initialOrthographicSize, minOrthographicSize, maxOrthographicSize)
            : CalculateDesiredOrthoSize();
    }

    private System.Collections.IEnumerator ReframeAfterDelay()
    {
        yield return new WaitForSeconds(0.5f);
        // Re-read bounds in case bases moved/settled
        CalculateMapBoundsFromBases();
        SetZoomImmediate(InitialZoom());
    }
}
