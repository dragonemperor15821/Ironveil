using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Base Management Menu: Row 1, Box 2 - "Build Additional Flaks".
///
/// Added by BaseMenuBootstrap next to BaseMenuView. Places the official
/// Level 1 Base Flak artwork (unmodified, aspect kept) inside the cell's
/// Content, so the painted frame and grid stay visible around it, and shows
/// the shared BaseMenuInfoPanel while the pointer is over the cell.
///
/// The hover text is built from the selected base's own surviving Flak count
/// (BaseFlakRoster) and the shared pricing table (FlakBuildOptionConfig).
/// Visual and hover only: no clicks are handled, nothing is deducted or built.
/// Row 1, Box 1 (Upgrade Flaks) is untouched.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1030)] // after BaseMenuView (1020) has positioned the panel
[AddComponentMenu("")]
public sealed class BaseMenuFlakBuildOption : MonoBehaviour
{
    public const string IconName = "Flak Build Icon";

    [Tooltip("Optional; when empty the config is loaded from Resources/" + FlakBuildOptionConfig.ResourcePath + ".")]
    [SerializeField] private FlakBuildOptionConfig configOverride;

    private FlakBuildOptionConfig config;
    private BaseMenuView view;
    private BaseMenuCell cell;
    private Image icon;
    private BaseMenuInfoPanel info;
    private bool hovered;
    private bool reportedMissing;
    private readonly List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>(5);

    public FlakBuildOptionConfig Config => config;
    public BaseMenuCell Cell => cell;
    public Image Icon => icon;
    public BaseMenuInfoPanel Info => info;

    private void OnEnable()
    {
        TryBind();
    }

    private void OnDisable()
    {
        Unbind();
        info?.Hide();
    }

    private void OnDestroy()
    {
        Unbind();
        info?.Destroy();
        info = null;
        if (icon != null)
        {
            if (Application.isPlaying) Destroy(icon.gameObject);
            else DestroyImmediate(icon.gameObject);
        }
    }

    /// <summary>Finds the view, config and target cell; places the icon. Idempotent.</summary>
    public bool TryBind()
    {
        if (config == null)
        {
            config = configOverride != null ? configOverride : Resources.Load<FlakBuildOptionConfig>(FlakBuildOptionConfig.ResourcePath);
        }

        if (config == null || config.artwork == null)
        {
            if (!reportedMissing)
            {
                reportedMissing = true;
                Debug.LogError($"BaseMenuFlakBuildOption: config or artwork missing (Resources/{FlakBuildOptionConfig.ResourcePath}); Row 1 Box 2 stays empty.", this);
            }

            return false;
        }

        BaseMenuView target = GetComponent<BaseMenuView>();
        if (target == null) target = BaseMenuView.Instance;
        if (target == null || !target.IsBuilt)
        {
            return false;
        }

        BaseMenuCell slot = target.GetSlot(config.row, config.column);
        if (slot == null)
        {
            if (!reportedMissing)
            {
                reportedMissing = true;
                Debug.LogError($"BaseMenuFlakBuildOption: no menu slot at row {config.row}, column {config.column}.", this);
            }

            return false;
        }

        if (view != target)
        {
            Unbind();
            view = target;
            view.CellHoverChanged += OnCellHoverChanged;
        }

        if (cell != slot || icon == null)
        {
            cell = slot;
            PlaceIcon();
            hovered = cell.IsHovered;
        }

        if (info == null && view.Canvas != null)
        {
            info = new BaseMenuInfoPanel(view.Canvas.transform);
        }

        return true;
    }

    private void Unbind()
    {
        if (view != null)
        {
            view.CellHoverChanged -= OnCellHoverChanged;
        }

        view = null;
        hovered = false;
    }

    private void PlaceIcon()
    {
        if (icon != null)
        {
            if (Application.isPlaying) Destroy(icon.gameObject);
            else DestroyImmediate(icon.gameObject);
            icon = null;
        }

        GameObject go = new GameObject(IconName, typeof(RectTransform));
        go.layer = 5;
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(cell.Content, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        // Padding as a fraction of the cell height, applied through a
        // stretched child with relative insets so it scales with the panel.
        Rect px = view.Layout.GetSlotPixelRect(config.row, config.column);
        float pad = Mathf.Clamp01(config.cellPadding);
        float padX = px.width > 0f ? pad * px.height / px.width : 0f;
        rect.anchorMin = new Vector2(padX, pad);
        rect.anchorMax = new Vector2(1f - padX, 1f - pad);

        icon = go.AddComponent<Image>();
        icon.sprite = config.artwork;
        icon.type = Image.Type.Simple;
        icon.preserveAspect = true;
        icon.color = Color.white; // untinted: the artwork is shown exactly as supplied
        icon.raycastTarget = false; // hover and clicks belong to the cell underneath
    }

    private void OnCellHoverChanged(BaseMenuCell changed, bool isHovered)
    {
        if (changed != cell)
        {
            return;
        }

        hovered = isHovered;
        UpdateInfo();
    }

    private void LateUpdate()
    {
        if (view == null || icon == null)
        {
            TryBind();
        }

        UpdateInfo();
    }

    /// <summary>Shows, refreshes or hides the hover panel. Tests call it directly.</summary>
    public void UpdateInfo()
    {
        if (info == null)
        {
            return;
        }

        BaseMenuController controller = BaseMenuController.Instance;
        bool visible = hovered && view != null && view.IsShown && cell != null && cell.IsHovered;
        if (!visible)
        {
            info.Hide();
            shownKey = null;
            return;
        }

        BaseContext selected = controller != null ? controller.SelectedContext : null;
        int surviving = BaseFlakRoster.GetSurvivingFlaks(selected);
        Rect panel = view.PanelScreenRect;
        Vector2 screen = view.ScreenSize;
        // The base object is compared too, not just its ID: IDs are reused after regeneration.
        BaseController selectedBase = selected != null ? selected.Controller : null;
        string key = $"{selected?.BaseId}|{surviving}|{panel}|{screen}";
        if (info.IsShown && key == shownKey && ReferenceEquals(selectedBase, shownBase))
        {
            return; // nothing changed: skip the layout rebuild
        }

        info.Show(BuildContent(selected), CellScreenRect(), panel, screen, InfoWidth(panel.height));
        shownKey = key;
        shownBase = selectedBase;
    }

    private string shownKey;
    private BaseController shownBase;

    /// <summary>Hover panel width for a menu panel of the given height.</summary>
    public static float InfoWidth(float menuHeight)
    {
        return Mathf.Clamp(Mathf.Round(menuHeight * 0.62f), 300f, 520f);
    }

    /// <summary>The target cell's rectangle in screen pixels (bottom-left origin).</summary>
    public Rect CellScreenRect()
    {
        Rect panel = view.PanelScreenRect;
        Rect px = view.Layout.GetSlotPixelRect(config.row, config.column);
        Vector2 art = view.Layout.artworkSize;
        float sx = panel.width / Mathf.Max(1f, art.x);
        float sy = panel.height / Mathf.Max(1f, art.y);
        return new Rect(panel.x + px.xMin * sx, panel.y + (art.y - px.yMax) * sy, px.width * sx, px.height * sy);
    }

    /// <summary>Hover text for <paramref name="selected"/> (null = no base: starting count).</summary>
    public BaseMenuInfoPanel.Content BuildContent(BaseContext selected)
    {
        int surviving = BaseFlakRoster.GetSurvivingFlaks(selected);
        return BuildContent(config, surviving, selected != null ? selected.BaseName : null,
            selected != null ? selected.BaseId : null, rows);
    }

    /// <summary>Hover text for a base with <paramref name="surviving"/> Flaks. Pure; used by tests.</summary>
    public static BaseMenuInfoPanel.Content BuildContent(FlakBuildOptionConfig config, int surviving, string baseName,
        string baseId, List<KeyValuePair<string, string>> rowBuffer)
    {
        rowBuffer.Clear();
        string footer = $"Base turrets: {Mathf.Max(0, surviving)} / {BaseFlakRoster.MaxFlaks}";
        string identity = string.IsNullOrEmpty(baseName) ? baseId
            : string.IsNullOrEmpty(baseId) ? baseName
            : $"{baseName} ({baseId})";
        if (!string.IsNullOrEmpty(identity))
        {
            footer += "\n" + identity;
        }

        BaseMenuInfoPanel.Content content = new BaseMenuInfoPanel.Content
        {
            preview = config != null ? config.artwork : null,
            rows = rowBuffer,
            footer = footer
        };

        if (BaseFlakRoster.IsDead(surviving))
        {
            content.title = "BASE DESTROYED";
            content.description = "This base has no surviving turrets and has fallen. No further turrets can be built.";
            return content;
        }

        if (surviving >= BaseFlakRoster.MaxFlaks)
        {
            content.title = "BASE TURRETS AT MAXIMUM";
            content.description = $"This base already has the maximum of {BaseFlakRoster.MaxFlaks} turrets.";
            return content;
        }

        int next = BaseFlakRoster.NextTurretNumber(surviving);
        content.title = $"BUILD {BaseFlakRoster.Ordinal(next)} BASE TURRET";
        content.description = config != null ? config.description : string.Empty;
        if (config != null && config.TryGetCost(next, out FlakBuildCost cost))
        {
            rowBuffer.Add(new KeyValuePair<string, string>("Concrete", cost.concrete.ToString("N0")));
            rowBuffer.Add(new KeyValuePair<string, string>("Iron", cost.iron.ToString("N0")));
            rowBuffer.Add(new KeyValuePair<string, string>("Aluminium", cost.aluminium.ToString("N0")));
            rowBuffer.Add(new KeyValuePair<string, string>("Construction time", FlakBuildOptionConfig.FormatDuration(cost.durationSeconds)));
        }
        else
        {
            rowBuffer.Add(new KeyValuePair<string, string>("Requirements", "not priced"));
        }

        return content;
    }
}
