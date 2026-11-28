using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Base Management Menu (Phase 1D): the visual menu.
///
/// One instance per Play session, added by BaseMenuBootstrap next to
/// BaseMenuController. It owns a single Screen Space Overlay canvas holding
/// one panel that shows the supplied artwork (IRONVEIL_BaseMenu.png) at its
/// original aspect, with invisible BaseMenuCell hit areas laid over the
/// painted cells. The same panel is reused for every base.
///
/// Each frame, after BaseMenuController (execution order 1010) has updated
/// its state and the selected base's projected anchor, the panel is shown
/// while the menu is Open or Closing, sized relative to the screen, centred
/// on the anchor and clamped inside the screen. It implements
/// IBaseMenuRegion, so the controller's "pointer is over the menu" test is
/// the panel's real on-screen rectangle (which also hides the Phase 1C debug
/// placeholder).
///
/// No gameplay: cell clicks only raise CellClicked.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1020)]
[AddComponentMenu("")]
public sealed class BaseMenuView : MonoBehaviour, IBaseMenuRegion
{
    /// <summary>A click on one of the menu's interactive areas.</summary>
    public struct CellClick
    {
        public BaseMenuCell cell;
        /// <summary>The base the menu is open for (null if it closed meanwhile).</summary>
        public BaseContext selected;
        public bool isEmpty;
    }

    public const string CanvasName = "Base Menu Canvas";
    public const string PanelName = "Base Menu Panel";

    [Tooltip("Optional; when empty the skin is loaded from Resources/" + BaseMenuSkin.ResourcePath + ".")]
    [SerializeField] private BaseMenuSkin skinOverride;
    [SerializeField] private bool debugLogging = true;

    private BaseMenuSkin skin;
    private BaseMenuGridLayout layout;
    private BaseMenuController controller;
    private bool subscribed;

    private Canvas canvas;
    private RectTransform panel;
    private Image panelImage;
    private RectTransform grid;
    private readonly List<BaseMenuCell> cells = new List<BaseMenuCell>(80);

    private bool shown;
    private Rect screenRect;

    private EventSystem raycastEventSystem;
    private PointerEventData raycastPointer;
    private readonly List<RaycastResult> raycastResults = new List<RaycastResult>(16);

    public static BaseMenuView Instance { get; private set; }

    /// <summary>Raised for every intentional left click on a cell or category header.</summary>
    public event Action<CellClick> CellClicked;

    /// <summary>Raised when the pointer enters (true) or leaves (false) a cell or category header.</summary>
    public event Action<BaseMenuCell, bool> CellHoverChanged;

    /// <summary>The cell under the pointer, or null.</summary>
    public BaseMenuCell HoveredCell { get; private set; }

    /// <summary>True once the canvas and panel were built from a valid skin.</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>True while the panel is displayed.</summary>
    public bool IsShown => shown;

    /// <summary>The panel's current rectangle in screen pixels (bottom-left origin). Valid while shown.</summary>
    public Rect PanelScreenRect => screenRect;

    public Canvas Canvas => canvas;
    public RectTransform Panel => panel;
    public Image PanelImage => panelImage;
    public BaseMenuSkin Skin => skin;
    public BaseMenuGridLayout Layout => layout;
    public IReadOnlyList<BaseMenuCell> Cells => cells;
    public bool DebugLogging { get => debugLogging; set => debugLogging = value; }

    /// <summary>Tests only: screen size used instead of Screen.width/height.</summary>
    public Vector2? ScreenSizeOverride { get; set; }

    public Vector2 ScreenSize => ScreenSizeOverride ?? new Vector2(Screen.width, Screen.height);

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
        EnsureBuilt();
    }

    private void OnEnable()
    {
        Bind(GetComponent<BaseMenuController>());
    }

    private void OnDisable()
    {
        Hide();
        Unbind();
    }

    private void OnDestroy()
    {
        Unbind();
        if (canvas != null)
        {
            DestroyObject(canvas.gameObject);
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    // ------------------------------------------------------------------ setup

    /// <summary>Builds the canvas, panel and cells once. Idempotent; false if the skin is unusable.</summary>
    public bool EnsureBuilt()
    {
        if (IsBuilt)
        {
            return true;
        }

        skin = skinOverride != null ? skinOverride : Resources.Load<BaseMenuSkin>(BaseMenuSkin.ResourcePath);
        if (skin == null || skin.artwork == null)
        {
            Debug.LogError($"BaseMenuView: menu skin or artwork missing (Resources/{BaseMenuSkin.ResourcePath}); the Phase 1C placeholder stays in use.", this);
            return false;
        }

        BaseMenuGridLayout candidate = skin.layout ?? BaseMenuGridLayout.CreateDefault();
        if (!candidate.Validate(out string error))
        {
            Debug.LogError($"BaseMenuView: invalid grid layout ({error}); the Phase 1C placeholder stays in use.", this);
            return false;
        }

        layout = candidate;
        BuildCanvas();
        RebuildGrid();
        IsBuilt = true;
        if (controller != null)
        {
            controller.MenuRegion = this;
        }

        return true;
    }

    private void BuildCanvas()
    {
        GameObject canvasObject = new GameObject(CanvasName, typeof(RectTransform));
        canvasObject.transform.SetParent(transform, false);
        canvasObject.layer = 5; // UI
        canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = skin.sortingOrder;
        canvas.pixelPerfect = false;
        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;
        canvasObject.AddComponent<GraphicRaycaster>();

        // Panel: pivot and anchors at the canvas' bottom-left, so
        // anchoredPosition and sizeDelta are screen pixels (scale factor 1).
        GameObject panelObject = new GameObject(PanelName, typeof(RectTransform));
        panelObject.layer = 5;
        panel = (RectTransform)panelObject.transform;
        panel.SetParent(canvasObject.transform, false);
        panel.anchorMin = Vector2.zero;
        panel.anchorMax = Vector2.zero;
        panel.pivot = Vector2.zero;
        panelImage = panelObject.AddComponent<Image>();
        panelImage.sprite = skin.artwork;
        panelImage.type = Image.Type.Simple;
        panelImage.preserveAspect = true;
        panelImage.color = Color.white;
        panelImage.raycastTarget = true; // the whole panel blocks camera input underneath

        GameObject gridObject = new GameObject("Grid", typeof(RectTransform));
        gridObject.layer = 5;
        grid = (RectTransform)gridObject.transform;
        grid.SetParent(panel, false);
        Stretch(grid);

        panelObject.SetActive(false);
    }

    /// <summary>
    /// Recreates the interactive areas from <paramref name="newLayout"/> (or the
    /// current layout). Rows and columns are fully data-driven.
    /// </summary>
    public bool RebuildGrid(BaseMenuGridLayout newLayout = null)
    {
        if (newLayout != null)
        {
            if (!newLayout.Validate(out string error))
            {
                Debug.LogError($"BaseMenuView: layout rejected ({error}).", this);
                return false;
            }

            layout = newLayout;
        }

        if (grid == null || layout == null)
        {
            return false;
        }

        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i] != null)
            {
                cells[i].Clicked -= OnCellClicked;
                cells[i].HoverChanged -= OnCellHoverChanged;
                cells[i].gameObject.SetActive(false);
                DestroyObject(cells[i].gameObject);
            }
        }

        cells.Clear();
        HoveredCell = null;
        for (int r = 0; r < layout.RowCount; r++)
        {
            BaseMenuRowDefinition row = layout.rows[r];
            if (row.HasHeader)
            {
                CreateCell(r, 0, BaseMenuCellKind.CategoryHeader, row, layout.GetHeaderPixelRect(r),
                    $"Header r{r} {row.label}");
            }

            for (int c = row.firstSlotColumn; c < layout.ColumnCount; c++)
            {
                CreateCell(r, c, BaseMenuCellKind.Slot, row, layout.GetSlotPixelRect(r, c),
                    $"Slot r{r} c{c}");
            }
        }

        return true;
    }

    private void CreateCell(int row, int column, BaseMenuCellKind kind, BaseMenuRowDefinition definition, Rect pixelRect, string name)
    {
        GameObject cellObject = new GameObject(name, typeof(RectTransform));
        cellObject.layer = 5;
        RectTransform rect = (RectTransform)cellObject.transform;
        rect.SetParent(grid, false);
        layout.ToAnchors(pixelRect, out Vector2 anchorMin, out Vector2 anchorMax);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        BaseMenuRaycastArea hitArea = cellObject.AddComponent<BaseMenuRaycastArea>();
        hitArea.raycastTarget = true;

        GameObject contentObject = new GameObject("Content", typeof(RectTransform));
        contentObject.layer = 5;
        RectTransform content = (RectTransform)contentObject.transform;
        content.SetParent(rect, false);
        Stretch(content);

        BaseMenuCell cell = cellObject.AddComponent<BaseMenuCell>();
        cell.Configure(row, column, kind, definition, content);
        cell.Clicked += OnCellClicked;
        cell.HoverChanged += OnCellHoverChanged;
        cells.Add(cell);
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    // ------------------------------------------------------------ controller

    /// <summary>Connects to the menu controller (idempotent) and registers as its menu region.</summary>
    public void Bind(BaseMenuController menuController)
    {
        BaseMenuController target = menuController != null ? menuController : BaseMenuController.Instance;
        if (target == null)
        {
            return;
        }

        if (controller != target)
        {
            Unbind();
            controller = target;
        }

        if (!subscribed)
        {
            controller.SelectionChanged += OnSelectionChanged;
            controller.MenuClosed += OnMenuClosed;
            subscribed = true;
        }

        if (IsBuilt)
        {
            controller.MenuRegion = this;
        }
    }

    private void Unbind()
    {
        if (controller == null)
        {
            return;
        }

        if (subscribed)
        {
            controller.SelectionChanged -= OnSelectionChanged;
            controller.MenuClosed -= OnMenuClosed;
            subscribed = false;
        }

        if (ReferenceEquals(controller.MenuRegion, this))
        {
            controller.MenuRegion = null;
        }

        controller = null;
    }

    private void OnSelectionChanged(BaseContext previous, BaseContext current)
    {
        Refresh(); // reposition on switch in the same frame
    }

    private void OnMenuClosed(BaseContext context)
    {
        Hide(); // New World / close: hide immediately, even outside LateUpdate
    }

    private void LateUpdate()
    {
        if (controller == null)
        {
            Bind(GetComponent<BaseMenuController>());
        }

        Refresh();
    }

    /// <summary>Shows, sizes and positions the panel from the controller's state. Called every LateUpdate; tests call it directly.</summary>
    public void Refresh()
    {
        bool visible = IsBuilt && controller != null && controller.IsMenuVisible &&
                       controller.HasAnchor && controller.Anchor.inFront;
        if (!visible)
        {
            Hide();
            return;
        }

        Vector2 screen = ScreenSize;
        Vector2 size = ComputePanelSize(screen, layout.Aspect, skin.screenHeightFraction,
            skin.maxScreenFraction, skin.minHeightPixels);
        Rect rect = PlacePanel(screen, controller.Anchor.screenPosition, size, skin.screenMarginPixels);

        if (panel.sizeDelta != rect.size) panel.sizeDelta = rect.size;
        if (panel.anchoredPosition != rect.position) panel.anchoredPosition = rect.position;
        screenRect = rect;

        if (!shown)
        {
            EnsureEventSystem();
            panel.gameObject.SetActive(true);
            shown = true;
        }
    }

    /// <summary>Hides the panel (the single instance stays alive for the next base).</summary>
    public void Hide()
    {
        if (panel != null && panel.gameObject.activeSelf)
        {
            panel.gameObject.SetActive(false);
        }

        shown = false;
        screenRect = default;
    }

    // ------------------------------------------------------------- geometry

    /// <summary>
    /// Panel size in whole pixels: <paramref name="heightFraction"/> of the screen
    /// height (at least <paramref name="minHeight"/>), shrunk if needed so it never
    /// exceeds <paramref name="maxFraction"/> of either screen dimension. Aspect kept.
    /// </summary>
    public static Vector2 ComputePanelSize(Vector2 screen, float aspect, float heightFraction, float maxFraction, float minHeight)
    {
        aspect = Mathf.Max(0.01f, aspect);
        float maxHeight = Mathf.Max(1f, screen.y * maxFraction);
        float maxWidth = Mathf.Max(1f, screen.x * maxFraction);
        float height = Mathf.Max(screen.y * heightFraction, minHeight);
        height = Mathf.Min(height, maxHeight);
        float width = height * aspect;
        if (width > maxWidth)
        {
            width = maxWidth;
            height = width / aspect;
        }

        height = Mathf.Max(1f, Mathf.Floor(height));
        width = Mathf.Max(1f, Mathf.Min(Mathf.Round(height * aspect), Mathf.Floor(maxWidth)));
        return new Vector2(width, height);
    }

    /// <summary>
    /// Panel rectangle centred on <paramref name="anchor"/>, clamped to stay
    /// <paramref name="margin"/> pixels inside the screen (centred on the screen
    /// on an axis where it cannot fit with the margin). Whole-pixel position.
    /// </summary>
    public static Rect PlacePanel(Vector2 screen, Vector2 anchor, Vector2 size, float margin)
    {
        float x = ClampAxis(anchor.x - size.x * 0.5f, size.x, screen.x, margin);
        float y = ClampAxis(anchor.y - size.y * 0.5f, size.y, screen.y, margin);
        return new Rect(Mathf.Round(x), Mathf.Round(y), size.x, size.y);
    }

    private static float ClampAxis(float start, float size, float screen, float margin)
    {
        if (size + 2f * margin > screen)
        {
            return Mathf.Max(0f, (screen - size) * 0.5f);
        }

        return Mathf.Clamp(start, margin, screen - margin - size);
    }

    // ---------------------------------------------------------------- region

    /// <summary>
    /// IBaseMenuRegion: the pointer is over the menu when it is inside the shown
    /// panel and no other UI (e.g. World Explorer controls, drawn above) covers
    /// that point.
    /// </summary>
    public bool ContainsScreenPoint(Vector2 screenPosition)
    {
        if (!shown || !screenRect.Contains(screenPosition))
        {
            return false;
        }

        return !IsCoveredByOtherUI(screenPosition);
    }

    private bool IsCoveredByOtherUI(Vector2 screenPosition)
    {
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null || canvas == null)
        {
            return false;
        }

        if (raycastPointer == null || raycastEventSystem != eventSystem)
        {
            raycastEventSystem = eventSystem;
            raycastPointer = new PointerEventData(eventSystem);
        }

        raycastPointer.Reset();
        raycastPointer.position = screenPosition;
        raycastResults.Clear();
        eventSystem.RaycastAll(raycastPointer, raycastResults);
        if (raycastResults.Count == 0)
        {
            return false;
        }

        GameObject top = raycastResults[0].gameObject;
        raycastResults.Clear();
        return top != null && !top.transform.IsChildOf(canvas.transform);
    }

    // ----------------------------------------------------------------- cells

    private void OnCellClicked(BaseMenuCell cell)
    {
        CellClick click = new CellClick
        {
            cell = cell,
            selected = controller != null ? controller.SelectedContext : null,
            isEmpty = cell.IsEmpty
        };

        if (debugLogging)
        {
            Debug.Log($"[BaseMenu] {cell} clicked{(click.isEmpty ? " (empty, no action)" : string.Empty)} for {click.selected?.BaseId ?? "no base"}", cell);
        }

        Action<CellClick> handlers = CellClicked;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<CellClick> handler in handlers.GetInvocationList())
        {
            try { handler(click); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    private void OnCellHoverChanged(BaseMenuCell cell, bool hovered)
    {
        if (hovered)
        {
            HoveredCell = cell;
        }
        else if (HoveredCell == cell)
        {
            HoveredCell = null;
        }

        Action<BaseMenuCell, bool> handlers = CellHoverChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<BaseMenuCell, bool> handler in handlers.GetInvocationList())
        {
            try { handler(cell, hovered); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    /// <summary>The slot at (row, column), or null.</summary>
    public BaseMenuCell GetSlot(int row, int column)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            BaseMenuCell cell = cells[i];
            if (cell.Kind == BaseMenuCellKind.Slot && cell.Row == row && cell.Column == column) return cell;
        }

        return null;
    }

    /// <summary>The category header of a row, or null (construction rows have none).</summary>
    public BaseMenuCell GetHeader(int row)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            BaseMenuCell cell = cells[i];
            if (cell.Kind == BaseMenuCellKind.CategoryHeader && cell.Row == row) return cell;
        }

        return null;
    }

    /// <summary>Appends the slots of the row(s) with <paramref name="category"/> (None = construction rows).</summary>
    public void GetSlots(BaseMenuCategory category, List<BaseMenuCell> results)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            BaseMenuCell cell = cells[i];
            if (cell.Kind == BaseMenuCellKind.Slot && cell.Category == category) results.Add(cell);
        }
    }

    // ------------------------------------------------------------------ misc

    /// <summary>
    /// uGUI needs an EventSystem for cell clicks and for the cameras' "pointer
    /// over UI" checks. World Explorer already creates one; MainBuild has none,
    /// so one is added to the active scene the first time the menu is shown.
    /// </summary>
    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null)
        {
            return;
        }

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        go.AddComponent<InputSystemUIInputModule>();
#else
        go.AddComponent<StandaloneInputModule>();
#endif
    }

    private static void DestroyObject(GameObject target)
    {
        if (target == null) return;
        if (Application.isPlaying) Destroy(target);
        else DestroyImmediate(target);
    }
}
