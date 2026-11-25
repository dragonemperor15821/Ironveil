using System;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Base Management Menu (Phase 1C): interaction lifecycle of the menu.
///
/// One instance per Play session (added by BaseMenuBootstrap next to
/// BaseHoverController). It does not detect bases itself: it reads
/// BaseHoverController.CurrentTarget, classifies the pointer as over the
/// selected menu, over a base, or over neither, and runs a small state
/// machine that opens, keeps, switches and closes the (future) menu. No
/// visual menu exists yet; an optional IMGUI debug outline shows the
/// placeholder menu region.
///
/// States
///   Closed       nothing selected.
///   PendingOpen  resting on a base; opens after hoverActivationDelay.
///   Open         menu shown for SelectedContext. Pointer on the menu or the
///                selected base keeps it open. Resting on another base for
///                hoverActivationDelay switches the selection. Over neither
///                for hoverExitGrace starts closing.
///   Closing      still shown; closes after menuCloseDelay unless the
///                pointer returns to the menu or a base.
/// The selected base being destroyed or unregistered (New World) closes the
/// menu immediately.
///
/// Runs in LateUpdate after BaseHoverController and the camera controllers,
/// so the anchor matches the rendered frame. Reads input only; it consumes
/// nothing, so camera drags and uGUI (New World, Share World) are unaffected.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1010)]
[AddComponentMenu("")]
public sealed class BaseMenuController : MonoBehaviour
{
    public enum MenuState
    {
        Closed,
        PendingOpen,
        Open,
        Closing
    }

    public enum PointerRegion
    {
        None,
        Base,
        Menu
    }

    /// <summary>One frame of pointer input.</summary>
    public struct PointerSample
    {
        public bool valid;
        public Vector2 position;
        public bool overUI;
        public bool primary;
        public bool secondary;
        public bool middle;
    }

    /// <summary>An intentional primary click (never a drag).</summary>
    public struct MenuClick
    {
        public Vector2 screenPosition;
        public PointerRegion region;
        /// <summary>The menu's selected base at click time (null when closed).</summary>
        public BaseContext selected;
        /// <summary>The base under the pointer (null over the menu, UI or terrain).</summary>
        public BaseContext hovered;
        /// <summary>The press began over a uGUI element; uGUI delivers that click itself.</summary>
        public bool pressStartedOverUI;
    }

    [Header("Timing (seconds, unscaled)")]
    [Tooltip("Time the pointer must rest on a base before the menu opens, and on another base before the selection switches.")]
    [SerializeField, Min(0f)] private float hoverActivationDelay = 0.25f;

    [Tooltip("Time the pointer may be over neither the base nor the menu (e.g. crossing the gap between them) before closing begins.")]
    [SerializeField, Min(0f)] private float hoverExitGrace = 0.2f;

    [Tooltip("Time from closing begins until the menu is closed (reserved for the close transition); returning cancels it.")]
    [SerializeField, Min(0f)] private float menuCloseDelay = 0.1f;

    [Header("Pointer")]
    [SerializeField] private BasePointerGesture gesture = new BasePointerGesture();

    [Tooltip("Do not open or switch while a mouse button is dragging (camera pan).")]
    [SerializeField] private bool suppressWhileDragging = true;

    [Tooltip("Placeholder menu area (pixels) centred on the selected base, used until a real menu provides IBaseMenuRegion.")]
    [SerializeField] private Vector2 placeholderMenuSize = new Vector2(320f, 240f);

    [Header("Debug")]
    [SerializeField] private bool debugLogging = true;
    [SerializeField] private bool drawDebugOverlay = true;

    private BaseHoverController hover;
    private bool subscribed;

    private BaseInteractionTarget selected;
    private BaseInteractionTarget pending;
    private BaseInteractionTarget switchCandidate;
    private float pendingTimer;
    private float switchTimer;
    private float graceTimer;
    private float closeTimer;

    private BaseScreenProjection.Anchor anchor;
    private bool hasAnchor;
    private string overlayLabel = string.Empty;

    public static BaseMenuController Instance { get; private set; }

    public event Action<MenuState, MenuState> StateChanged;
    public event Action<BaseContext> MenuOpened;
    public event Action<BaseContext> MenuClosed;
    /// <summary>Selection changes: null->A on open, A->B on switch, A->null on close.</summary>
    public event Action<BaseContext, BaseContext> SelectionChanged;
    public event Action<PointerRegion, PointerRegion> RegionChanged;
    public event Action<MenuClick> Clicked;

    public MenuState State { get; private set; } = MenuState.Closed;
    public PointerRegion Region { get; private set; } = PointerRegion.None;

    /// <summary>True while the menu should be shown (Open or Closing).</summary>
    public bool IsMenuVisible => State == MenuState.Open || State == MenuState.Closing;

    public BaseContext SelectedContext => IsAlive(selected) ? selected.Context : null;
    public BaseInteractionTarget SelectedTarget => IsAlive(selected) ? selected : null;
    public BaseContext PendingContext => IsAlive(pending) ? pending.Context : null;

    /// <summary>Projected anchor of the selected base (valid when HasAnchor).</summary>
    public BaseScreenProjection.Anchor Anchor => anchor;
    public bool HasAnchor => hasAnchor;

    public BasePointerGesture Gesture => gesture;

    /// <summary>Set by the future visual menu; null uses the placeholder rectangle.</summary>
    public IBaseMenuRegion MenuRegion { get; set; }

    public float HoverActivationDelay { get => hoverActivationDelay; set => hoverActivationDelay = Mathf.Max(0f, value); }
    public float HoverExitGrace { get => hoverExitGrace; set => hoverExitGrace = Mathf.Max(0f, value); }
    public float MenuCloseDelay { get => menuCloseDelay; set => menuCloseDelay = Mathf.Max(0f, value); }
    public Vector2 PlaceholderMenuSize { get => placeholderMenuSize; set => placeholderMenuSize = value; }
    public bool DebugLogging { get => debugLogging; set => debugLogging = value; }

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
        Initialize(GetComponent<BaseHoverController>());
    }

    private void OnDisable()
    {
        CloseImmediately();
        gesture.Reset();
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

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            gesture.Reset();
        }
    }

    /// <summary>Connects to the hover controller and the registry. Idempotent.</summary>
    public void Initialize(BaseHoverController hoverController)
    {
        if (hoverController != null)
        {
            hover = hoverController;
        }

        if (!subscribed)
        {
            BaseRegistry.Unregistered += OnTargetUnregistered;
            subscribed = true;
        }
    }

    private void Unsubscribe()
    {
        if (!subscribed)
        {
            return;
        }

        BaseRegistry.Unregistered -= OnTargetUnregistered;
        subscribed = false;
    }

    private void OnTargetUnregistered(string baseId)
    {
        if (Matches(selected, baseId))
        {
            CloseImmediately();
        }
        else if (Matches(pending, baseId) || Matches(switchCandidate, baseId))
        {
            if (Matches(switchCandidate, baseId)) { switchCandidate = null; switchTimer = 0f; }
            if (Matches(pending, baseId)) { pending = null; SetState(MenuState.Closed); }
        }
    }

    private void LateUpdate()
    {
        if (hover == null)
        {
            BaseHoverController local = GetComponent<BaseHoverController>();
            Initialize(local != null ? local : BaseHoverController.Instance);
        }

        Camera cam = hover != null ? hover.ResolveCamera() : null;
        Tick(Time.unscaledDeltaTime, cam, ReadPointer());
    }

    private static PointerSample ReadPointer()
    {
        PointerSample sample = default;
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            sample.valid = true;
            sample.position = mouse.position.ReadValue();
            sample.primary = mouse.leftButton.isPressed;
            sample.secondary = mouse.rightButton.isPressed;
            sample.middle = mouse.middleButton.isPressed;
        }
#endif
        EventSystem eventSystem = EventSystem.current;
        sample.overUI = eventSystem != null && eventSystem.IsPointerOverGameObject();
        return sample;
    }

    /// <summary>
    /// One lifecycle step. LateUpdate calls it with live input after
    /// BaseHoverController has updated; tests call it with synthetic input.
    /// Allocation-free.
    /// </summary>
    public void Tick(float deltaTime, Camera cam, PointerSample sample)
    {
        Initialize(null);
        gesture.Update(sample.position, sample.primary, sample.secondary, sample.middle, sample.overUI);

        // Destroyed without an unregister callback.
        if (!ReferenceEquals(selected, null) && !IsAlive(selected)) CloseImmediately();
        if (!ReferenceEquals(pending, null) && !IsAlive(pending)) { pending = null; if (State == MenuState.PendingOpen) SetState(MenuState.Closed); }
        if (!ReferenceEquals(switchCandidate, null) && !IsAlive(switchCandidate)) { switchCandidate = null; switchTimer = 0f; }

        UpdateAnchor(cam);

        BaseInteractionTarget hovered = hover != null ? hover.CurrentTarget : null;
        PointerRegion region;
        if (!sample.valid) region = PointerRegion.None;
        else if (IsMenuVisible && MenuContains(sample.position)) region = PointerRegion.Menu;
        else if (hovered != null) region = PointerRegion.Base;
        else region = PointerRegion.None;
        SetRegion(region);

        bool dragging = suppressWhileDragging && gesture.IsDragging;
        float dt = Mathf.Max(0f, deltaTime);

        switch (State)
        {
            case MenuState.Closed:
                if (region == PointerRegion.Base && !dragging)
                {
                    BeginPending(hovered);
                    AdvancePending(0f);
                }
                break;

            case MenuState.PendingOpen:
                if (region != PointerRegion.Base)
                {
                    pending = null;
                    SetState(MenuState.Closed);
                }
                else if (hovered != pending)
                {
                    BeginPending(hovered);          // moved to another base: restart the delay
                    if (dragging) pendingTimer = 0f;
                    else AdvancePending(0f);
                }
                else if (dragging)
                {
                    pendingTimer = 0f;              // a camera drag never opens the menu
                }
                else
                {
                    AdvancePending(dt);
                }
                break;

            case MenuState.Open:
                UpdateOpen(region, hovered, dragging, dt);
                break;

            case MenuState.Closing:
                if (region == PointerRegion.None)
                {
                    closeTimer += dt;
                    if (closeTimer >= menuCloseDelay)
                    {
                        CloseImmediately();
                    }
                }
                else
                {
                    // Returned before the close finished: the menu never closed.
                    graceTimer = 0f;
                    closeTimer = 0f;
                    SetState(MenuState.Open);
                    UpdateOpen(region, hovered, dragging, 0f);
                }
                break;
        }

        if (gesture.ClickedThisFrame)
        {
            Raise(new MenuClick
            {
                screenPosition = sample.position,
                region = Region,
                selected = SelectedContext,
                hovered = hovered != null ? hovered.Context : null,
                pressStartedOverUI = gesture.ClickPressStartedOverUI
            });
        }
    }

    private void UpdateOpen(PointerRegion region, BaseInteractionTarget hovered, bool dragging, float dt)
    {
        if (region == PointerRegion.Menu || (region == PointerRegion.Base && hovered == selected))
        {
            graceTimer = 0f;
            switchCandidate = null;
            switchTimer = 0f;
            return;
        }

        if (region == PointerRegion.Base)
        {
            graceTimer = 0f;
            if (hovered != switchCandidate)
            {
                switchCandidate = hovered;
                switchTimer = 0f;
            }
            else if (!dragging)
            {
                switchTimer += dt;
            }

            if (dragging)
            {
                switchTimer = 0f;
            }
            else if (switchTimer >= hoverActivationDelay)
            {
                Switch(switchCandidate);
            }

            return;
        }

        // Over neither: grace, then closing.
        switchCandidate = null;
        switchTimer = 0f;
        graceTimer += dt;
        if (graceTimer >= hoverExitGrace)
        {
            closeTimer = 0f;
            SetState(MenuState.Closing);
            if (menuCloseDelay <= 0f)
            {
                CloseImmediately();
            }
        }
    }

    private void BeginPending(BaseInteractionTarget target)
    {
        pending = target;
        pendingTimer = 0f;
        SetState(MenuState.PendingOpen);
    }

    private void AdvancePending(float dt)
    {
        pendingTimer += dt;
        if (pendingTimer >= hoverActivationDelay)
        {
            Open(pending);
        }
    }

    private void Open(BaseInteractionTarget target)
    {
        pending = null;
        selected = target;
        graceTimer = closeTimer = switchTimer = 0f;
        switchCandidate = null;
        SetState(MenuState.Open);
        UpdateAnchorForSelected();
        RefreshOverlayLabel();
        BaseContext context = target.Context;
        if (debugLogging) Debug.Log($"[BaseMenu] Open {context}", context.BaseObject);
        Raise(MenuOpened, context);
        RaiseSelection(null, context);
    }

    private void Switch(BaseInteractionTarget target)
    {
        BaseContext previous = selected != null ? selected.Context : null;
        selected = target;
        switchCandidate = null;
        graceTimer = closeTimer = switchTimer = 0f;
        UpdateAnchorForSelected();
        RefreshOverlayLabel();
        if (debugLogging) Debug.Log($"[BaseMenu] Switch {previous?.BaseId} -> {target.Context}", target.Context.BaseObject);
        RaiseSelection(previous, target.Context);
    }

    /// <summary>Closes now, without delays (selected base removed, controller disabled).</summary>
    public void CloseImmediately()
    {
        bool wasVisible = IsMenuVisible;
        BaseContext previous = !ReferenceEquals(selected, null) ? selected.Context : null;
        selected = null;
        pending = null;
        switchCandidate = null;
        pendingTimer = switchTimer = graceTimer = closeTimer = 0f;
        hasAnchor = false;
        SetState(MenuState.Closed);
        if (wasVisible && previous != null)
        {
            if (debugLogging) Debug.Log($"[BaseMenu] Close {previous.BaseId}");
            Raise(MenuClosed, previous);
            RaiseSelection(previous, null);
        }
    }

    private Camera lastCamera;

    private void UpdateAnchor(Camera cam)
    {
        lastCamera = cam;
        UpdateAnchorForSelected();
    }

    private void UpdateAnchorForSelected()
    {
        hasAnchor = IsAlive(selected) && BaseScreenProjection.TryProject(lastCamera, selected, out anchor);
    }

    private bool MenuContains(Vector2 screenPosition)
    {
        if (MenuRegion != null)
        {
            return MenuRegion.ContainsScreenPoint(screenPosition);
        }

        if (!hasAnchor || !anchor.inFront)
        {
            return false;
        }

        Vector2 d = screenPosition - anchor.screenPosition;
        return Mathf.Abs(d.x) <= placeholderMenuSize.x * 0.5f && Mathf.Abs(d.y) <= placeholderMenuSize.y * 0.5f;
    }

    private void SetState(MenuState next)
    {
        if (next == State) return;
        MenuState previous = State;
        State = next;
        RefreshOverlayLabel();
        StateChanged?.Invoke(previous, next);
    }

    private void SetRegion(PointerRegion next)
    {
        if (next == Region) return;
        PointerRegion previous = Region;
        Region = next;
        RegionChanged?.Invoke(previous, next);
    }

    private static bool IsAlive(BaseInteractionTarget target)
    {
        return target != null && target.Context != null && target.Context.IsAlive;
    }

    private static bool Matches(BaseInteractionTarget target, string baseId)
    {
        return !ReferenceEquals(target, null) && target.Context != null &&
               string.Equals(target.Context.BaseId, baseId, StringComparison.Ordinal);
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

    private void RaiseSelection(BaseContext previous, BaseContext current)
    {
        Action<BaseContext, BaseContext> handlers = SelectionChanged;
        if (handlers == null) return;
        foreach (Action<BaseContext, BaseContext> handler in handlers.GetInvocationList())
        {
            try { handler(previous, current); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    private void Raise(MenuClick click)
    {
        Action<MenuClick> handlers = Clicked;
        if (handlers == null) return;
        foreach (Action<MenuClick> handler in handlers.GetInvocationList())
        {
            try { handler(click); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    private void RefreshOverlayLabel()
    {
        if (!drawDebugOverlay) return;
        BaseContext context = SelectedContext;
        overlayLabel = context == null
            ? string.Empty
            : $"MENU PLACEHOLDER\n{context.BaseId}\n{context.BaseType}  {context.Level}\n{State}";
    }

    // Debug only: outline of the placeholder menu region. IMGUI draws no
    // uGUI raycast target, so it never blocks the camera or existing UI.
    private void OnGUI()
    {
        if (!drawDebugOverlay || !IsMenuVisible || !hasAnchor || !anchor.inFront || MenuRegion != null)
        {
            return;
        }

        Rect rect = new Rect(
            anchor.screenPosition.x - placeholderMenuSize.x * 0.5f,
            Screen.height - anchor.screenPosition.y - placeholderMenuSize.y * 0.5f,
            placeholderMenuSize.x,
            placeholderMenuSize.y);
        GUI.Box(rect, overlayLabel);
    }
}
