using System.Collections.Generic;
using Ironveil.Economy;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Base Management Menu: Row 1, Box 1 - "Upgrade Flaks".
///
/// Added by BaseMenuBootstrap next to BaseMenuView, independent of Box 2
/// (BaseMenuFlakBuildOption). Shows the artwork of the selected base's NEXT
/// Flak level (L2 image for an L1 base, L3 image for an L2 base, L4 image for
/// an L3 base; at L4 the L4 image, dimmed, with a MAX LEVEL badge and no
/// upgrade action), a hover panel built from BaseConstruction.EvaluateFlakUpgrade,
/// and on a click buys the collective upgrade through
/// BaseConstruction.TryUpgradeFlaks. The menu never touches Flak models itself
/// and holds no prices or level rules (FlakUpgradeRules owns them).
///
/// Purchases are protected against stale and duplicate clicks: the click passes
/// the level the cell was SHOWING as expectedCurrentLevel, a click for a base
/// other than the one shown (or in the frame the shown base or level changed,
/// before the player could see it) buys nothing, and after a purchase further
/// clicks on that base are ignored for a short guard time.
///
/// Everything is refreshed every frame from live state (selected base, its
/// Flak level and count, the shared Gold), so switching bases quickly, building
/// or losing Flaks and Gold changes never leave stale information.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1031)] // after BaseMenuView (1020) has positioned the panel
[AddComponentMenu("")]
public sealed class BaseMenuFlakUpgradeOption : MonoBehaviour
{
    public const string IconName = "Flak Upgrade Icon";
    public const string MaxBadgeName = "Flak Max Level Badge";

    /// <summary>Icon tint at the maximum level (the artwork stays visible but reads as unavailable).</summary>
    public static readonly Color MaxLevelIconTint = new Color(0.55f, 0.55f, 0.55f, 1f);

    [Tooltip("Optional; when empty the config is loaded from Resources/" + FlakUpgradeOptionConfig.ResourcePath + ".")]
    [SerializeField] private FlakUpgradeOptionConfig configOverride;

    private FlakUpgradeOptionConfig config;
    private BaseMenuView view;
    private BaseMenuCell cell;
    private Image icon;
    private RectTransform maxBadge;
    private Text maxBadgeText;
    private BaseMenuInfoPanel info;
    private bool hovered;
    private bool reportedMissing;
    private readonly List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>(7);

    private ResourceManager subscribedResources;
    private bool subscribedDefence;

    // What the cell is currently showing (the basis of a purchase).
    private BaseController shownBase;
    private int shownLevel;
    private bool hasShown;
    // Frame in which the shown base or level last changed: it is not on screen until the next frame,
    // so a click in that same frame was made on what was shown before and buys nothing.
    private int shownChangedFrame = -1;

    // Result of the latest click, shown for the base it was made on.
    private BaseController feedbackBase;
    private string feedback;

    // Double-click guard after a purchase.
    private BaseController guardBase;
    private float guardUntil;

    private string shownKey;

    public FlakUpgradeOptionConfig Config => config;
    public BaseMenuCell Cell => cell;
    public Image Icon => icon;
    public BaseMenuInfoPanel Info => info;

    /// <summary>The MAX LEVEL badge on the cell (active only while the shown base is at the maximum level).</summary>
    public RectTransform MaxBadge => maxBadge;
    public Text MaxBadgeText => maxBadgeText;

    /// <summary>The quote the cell and panel were last refreshed from.</summary>
    public FlakUpgradeQuote CurrentQuote { get; private set; }

    /// <summary>The base the cell is showing (null when the menu has no base).</summary>
    public BaseController ShownBase => hasShown ? shownBase : null;

    /// <summary>The Flak level of <see cref="ShownBase"/> the cell is showing.</summary>
    public int ShownLevel => shownLevel;

    /// <summary>Result of the most recent click (Available when nothing has been clicked yet).</summary>
    public FlakUpgradeResult LastClickResult { get; private set; } = FlakUpgradeResult.Available;

    /// <summary>Feedback line of the most recent click, or null.</summary>
    public string LastFeedback => feedback;

    private void OnEnable()
    {
        TryBind();
        SubscribeDefence();
    }

    private void OnDisable()
    {
        Unbind();
        UnsubscribeResources();
        UnsubscribeDefence();
        info?.Hide();
    }

    private void OnDestroy()
    {
        Unbind();
        UnsubscribeResources();
        UnsubscribeDefence();
        info?.Destroy();
        info = null;
        if (icon != null)
        {
            if (Application.isPlaying) Destroy(icon.gameObject);
            else DestroyImmediate(icon.gameObject);
        }

        if (maxBadge != null)
        {
            if (Application.isPlaying) Destroy(maxBadge.gameObject);
            else DestroyImmediate(maxBadge.gameObject);
        }
    }

    // ------------------------------------------------------------------ binding

    /// <summary>Finds the view, config and target cell; places the icon. Idempotent.</summary>
    public bool TryBind()
    {
        if (config == null)
        {
            config = configOverride != null ? configOverride : Resources.Load<FlakUpgradeOptionConfig>(FlakUpgradeOptionConfig.ResourcePath);
        }

        if (config == null || config.ArtworkForLevel(FlakUpgradeRules.MaxLevel) == null)
        {
            if (!reportedMissing)
            {
                reportedMissing = true;
                Debug.LogError($"BaseMenuFlakUpgradeOption: config or artwork missing (Resources/{FlakUpgradeOptionConfig.ResourcePath}); Row 1 Box 1 stays empty.", this);
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
                Debug.LogError($"BaseMenuFlakUpgradeOption: no menu slot at row {config.row}, column {config.column}.", this);
            }

            return false;
        }

        if (view != target)
        {
            Unbind();
            view = target;
            view.CellHoverChanged += OnCellHoverChanged;
            view.CellClicked += OnCellClicked;
        }

        if (cell != slot || icon == null)
        {
            cell = slot;
            PlaceIcon();
            hovered = cell.IsHovered;
            shownKey = null;
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
            view.CellClicked -= OnCellClicked;
        }

        view = null;
        hovered = false;
    }

    private void SubscribeDefence()
    {
        if (subscribedDefence) return;
        BaseFlakDefence.Changed += OnDefenceChanged;
        subscribedDefence = true;
    }

    private void UnsubscribeDefence()
    {
        if (!subscribedDefence) return;
        BaseFlakDefence.Changed -= OnDefenceChanged;
        subscribedDefence = false;
    }

    /// <summary>Follows the session's ResourceManager (it may be created after the menu).</summary>
    private void EnsureResourceSubscription()
    {
        ResourceManager current = ResourceManager.Instance;
        if (ReferenceEquals(current, subscribedResources)) return;
        UnsubscribeResources();
        if (current == null) return;
        current.ResourceChanged += OnResourceChanged;
        current.LedgerReset += OnLedgerReset;
        subscribedResources = current;
    }

    private void UnsubscribeResources()
    {
        if (!ReferenceEquals(subscribedResources, null) && subscribedResources != null)
        {
            subscribedResources.ResourceChanged -= OnResourceChanged;
            subscribedResources.LedgerReset -= OnLedgerReset;
        }

        subscribedResources = null;
    }

    private void OnDefenceChanged(BaseFlakDefence changed) => shownKey = null;

    private void OnResourceChanged(ResourceChange change)
    {
        if (change.Resource == ResourceType.Gold) shownKey = null;
    }

    private void OnLedgerReset() => shownKey = null;

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
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        // Same padding rule as Box 2: a fraction of the cell height, as relative insets.
        Rect px = view.Layout.GetSlotPixelRect(config.row, config.column);
        float pad = Mathf.Clamp01(config.cellPadding);
        float padX = px.width > 0f ? pad * px.height / px.width : 0f;
        rect.anchorMin = new Vector2(padX, pad);
        rect.anchorMax = new Vector2(1f - padX, 1f - pad);

        icon = go.AddComponent<Image>();
        icon.sprite = config.ArtworkForCurrentLevel(FlakUpgradeRules.StartingLevel);
        icon.type = Image.Type.Simple;
        icon.preserveAspect = true;
        icon.color = Color.white; // untinted: the artwork is shown exactly as rendered
        icon.raycastTarget = false; // hover and clicks belong to the cell underneath

        PlaceMaxBadge();
    }

    /// <summary>Band across the lower cell reading "MAX LEVEL" (same style as Box 2's progress strip).</summary>
    private void PlaceMaxBadge()
    {
        if (maxBadge != null)
        {
            if (Application.isPlaying) Destroy(maxBadge.gameObject);
            else DestroyImmediate(maxBadge.gameObject);
            maxBadge = null;
        }

        GameObject root = new GameObject(MaxBadgeName, typeof(RectTransform));
        root.layer = 5;
        maxBadge = (RectTransform)root.transform;
        maxBadge.SetParent(cell.Content, false);
        maxBadge.anchorMin = new Vector2(0.04f, 0.04f);
        maxBadge.anchorMax = new Vector2(0.96f, 0.30f);
        maxBadge.offsetMin = Vector2.zero;
        maxBadge.offsetMax = Vector2.zero;
        Image back = root.AddComponent<Image>();
        back.color = new Color(0.04f, 0.06f, 0.08f, 0.82f);
        back.raycastTarget = false;

        GameObject label = new GameObject("Label", typeof(RectTransform));
        label.layer = 5;
        RectTransform labelRect = (RectTransform)label.transform;
        labelRect.SetParent(maxBadge, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(2f, 0f);
        labelRect.offsetMax = new Vector2(-2f, 0f);
        maxBadgeText = label.AddComponent<Text>();
        maxBadgeText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        maxBadgeText.alignment = TextAnchor.MiddleCenter;
        maxBadgeText.color = BaseMenuInfoPanel.Gold;
        maxBadgeText.fontStyle = FontStyle.Bold;
        maxBadgeText.resizeTextForBestFit = true;
        maxBadgeText.resizeTextMinSize = 6;
        maxBadgeText.resizeTextMaxSize = 40;
        maxBadgeText.raycastTarget = false;
        maxBadgeText.text = config != null && !string.IsNullOrEmpty(config.maxLevelLabel) ? config.maxLevelLabel : "MAX LEVEL";
        Outline outline = label.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.9f);

        root.SetActive(false);
    }

    // ------------------------------------------------------------------ input

    private void OnCellHoverChanged(BaseMenuCell changed, bool isHovered)
    {
        if (changed != cell)
        {
            return;
        }

        hovered = isHovered;
        Refresh(true);
    }

    private void OnCellClicked(BaseMenuView.CellClick click)
    {
        if (click.cell == null || click.cell != cell)
        {
            return; // another cell (e.g. Box 2): not ours
        }

        HandleClick(click.selected);
    }

    /// <summary>
    /// A click on the cell for the menu's base <paramref name="selected"/>. Buys the upgrade
    /// through BaseConstruction.TryUpgradeFlaks when EvaluateFlakUpgrade allows it; otherwise
    /// only reports the status. Always refreshes the cell and panel afterwards.
    /// </summary>
    public FlakUpgradeResult HandleClick(BaseContext selected)
    {
        BaseController controller = selected != null && selected.Controller != null ? selected.Controller : null;
        FlakUpgradeQuote quote = BaseConstruction.EvaluateFlakUpgrade(controller);
        FlakUpgradeResult result;

        if (controller != null && ReferenceEquals(controller, guardBase) && Time.realtimeSinceStartup < guardUntil)
        {
            result = FlakUpgradeResult.UpgradeInProgress; // a double click: ignored, the purchase message stays
        }
        else if (!hasShown || !ReferenceEquals(controller, shownBase) || Time.frameCount == shownChangedFrame)
        {
            result = FlakUpgradeResult.LevelChanged;
            SetFeedback(controller, "The menu was still showing another base; nothing was bought.");
        }
        else if (!quote.CanUpgrade)
        {
            result = quote.status;
            SetFeedback(controller, StatusText(quote));
        }
        else
        {
            result = BaseConstruction.TryUpgradeFlaks(controller, shownLevel);
            switch (result)
            {
                case FlakUpgradeResult.Upgraded:
                    guardBase = controller;
                    guardUntil = Time.realtimeSinceStartup + (config != null ? config.clickGuardSeconds : 0f);
                    SetFeedback(controller, $"Upgraded {quote.flakCount} Flak{(quote.flakCount == 1 ? string.Empty : "s")} to L{quote.nextLevel} for {quote.goldCost:N0} Gold.");
                    break;
                case FlakUpgradeResult.LevelChanged:
                    SetFeedback(controller, "This base's Flak level had already changed; nothing was bought.");
                    break;
                default:
                    FlakUpgradeQuote after = BaseConstruction.EvaluateFlakUpgrade(controller);
                    after.status = result;
                    SetFeedback(controller, "Upgrade failed: " + StatusText(after) + " No Gold was spent.");
                    break;
            }
        }

        LastClickResult = result;
        Refresh(true);
        return result;
    }

    private void SetFeedback(BaseController controller, string text)
    {
        feedbackBase = controller;
        feedback = text;
    }

    // ------------------------------------------------------------------ refresh

    private void LateUpdate()
    {
        if (view == null || icon == null)
        {
            TryBind();
        }

        EnsureResourceSubscription();
        Refresh(false);
    }

    /// <summary>
    /// Re-reads the selected base, its upgrade quote and the shared Gold; updates the cell
    /// artwork and shows, refreshes or hides the hover panel. Tests call it directly.
    /// </summary>
    public void Refresh(bool force)
    {
        if (config == null || cell == null || icon == null || view == null)
        {
            return;
        }

        BaseMenuController controller = BaseMenuController.Instance;
        BaseContext selected = controller != null ? controller.SelectedContext : null;
        BaseController selectedBase = selected != null && selected.Controller != null ? selected.Controller : null;
        FlakUpgradeQuote quote = BaseConstruction.EvaluateFlakUpgrade(selectedBase);
        CurrentQuote = quote;

        Sprite art = config.ArtworkForCurrentLevel(quote.currentLevel);
        if (art != null && icon.sprite != art) icon.sprite = art;
        bool atMax = IsMaxLevelState(quote);
        Color tint = atMax ? MaxLevelIconTint : Color.white;
        if (icon.color != tint) icon.color = tint;
        if (maxBadge != null && maxBadge.gameObject.activeSelf != atMax) maxBadge.gameObject.SetActive(atMax);

        if (!ReferenceEquals(selectedBase, shownBase) && !ReferenceEquals(feedbackBase, selectedBase))
        {
            feedback = null; // feedback belongs to the base it was made on
            feedbackBase = null;
        }

        if (!ReferenceEquals(selectedBase, shownBase) || quote.currentLevel != shownLevel) shownChangedFrame = Time.frameCount;
        shownBase = selectedBase;
        shownLevel = quote.currentLevel;
        hasShown = selectedBase != null;

        if (info == null)
        {
            return;
        }

        bool visible = hovered && view.IsShown && cell.IsHovered;
        if (!visible)
        {
            info.Hide();
            shownKey = null;
            return;
        }

        Rect panel = view.PanelScreenRect;
        Vector2 screen = view.ScreenSize;
        string key = $"{selected?.BaseId}|{quote.status}|{quote.currentLevel}|{quote.flakCount}|{quote.goldBalance}|{feedback}|{panel}|{screen}";
        if (!force && info.IsShown && key == shownKey && ReferenceEquals(selectedBase, shownKeyBase))
        {
            return; // nothing changed: skip the layout rebuild
        }

        string shownFeedback = ReferenceEquals(feedbackBase, selectedBase) ? feedback : null;
        info.Show(BuildContent(config, quote, selected != null ? selected.BaseName : null, selected != null ? selected.BaseId : null,
            shownFeedback, rows), CellScreenRect(), panel, screen, InfoWidth(panel.height));
        shownKey = key;
        shownKeyBase = selectedBase;
    }

    private BaseController shownKeyBase;

    /// <summary>Hover panel width for a menu panel of the given height (same proportions as the other menu options).</summary>
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

    // ------------------------------------------------------------------ content (pure)

    /// <summary>True when the quote's base exists and its Flaks are at the maximum level (MAX LEVEL badge, no action). Pure.</summary>
    public static bool IsMaxLevelState(FlakUpgradeQuote q)
    {
        return q.status != FlakUpgradeResult.InvalidBase && q.status != FlakUpgradeResult.NotInitialised &&
               q.currentLevel >= FlakUpgradeRules.MaxLevel;
    }

    /// <summary>Hover "Cost" value: the Gold price, or a pending note when the price is not approved. Pure.</summary>
    public static string CostText(FlakUpgradeQuote q)
    {
        return q.goldCostKnown ? $"{q.goldCost:N0} Gold" : "Price pending approval";
    }

    /// <summary>One-line availability / status for a quote. Pure; used by tests.</summary>
    public static string StatusText(FlakUpgradeQuote q)
    {
        switch (q.status)
        {
            case FlakUpgradeResult.Available: return "Available: click to upgrade.";
            case FlakUpgradeResult.Upgraded: return "Upgraded.";
            case FlakUpgradeResult.InsufficientGold:
                return $"Not enough Gold: {q.goldCost:N0} needed, {q.goldBalance:N0} available ({q.goldCost - q.goldBalance:N0} short).";
            case FlakUpgradeResult.NoFlaks: return "Base destroyed: it has no Flak turrets to upgrade.";
            case FlakUpgradeResult.InvalidBase: return "Base destroyed: it no longer exists.";
            case FlakUpgradeResult.NotInitialised: return "Unavailable: this base has no Flak defence system.";
            case FlakUpgradeResult.AtMaximumLevel: return "MAX LEVEL: no further upgrade.";
            case FlakUpgradeResult.ModelMissing: return $"Unavailable: no L{q.nextLevel} Flak model is configured.";
            case FlakUpgradeResult.NoResourceSystem: return "Unavailable: the resource system is not running.";
            case FlakUpgradeResult.UpgradeInProgress: return "An upgrade of this base is in progress.";
            case FlakUpgradeResult.LevelChanged: return "This base's Flak level has changed.";
            case FlakUpgradeResult.SpendFailed: return "The Gold could not be spent.";
            case FlakUpgradeResult.ReplacementFailed: return "The Flaks could not be replaced; everything was restored.";
            case FlakUpgradeResult.PriceNotSet: return $"Unavailable: the Gold price of the L{q.currentLevel} -> L{q.nextLevel} upgrade has not been approved yet.";
            default: return q.status.ToString();
        }
    }

    /// <summary>Short availability value for the hover panel's two-column row. Pure; used by tests.</summary>
    public static string AvailabilityText(FlakUpgradeQuote q)
    {
        switch (q.status)
        {
            case FlakUpgradeResult.Available: return "Click to upgrade";
            case FlakUpgradeResult.InsufficientGold: return $"Need {q.goldCost - q.goldBalance:N0} more Gold";
            case FlakUpgradeResult.NoFlaks:
            case FlakUpgradeResult.InvalidBase: return "Base destroyed";
            case FlakUpgradeResult.AtMaximumLevel: return "MAX LEVEL";
            case FlakUpgradeResult.ModelMissing: return "Model missing";
            case FlakUpgradeResult.PriceNotSet: return "Price not set";
            case FlakUpgradeResult.UpgradeInProgress: return "In progress";
            default: return "Unavailable";
        }
    }

    /// <summary>Hover text for a quote. Pure; used by tests.</summary>
    public static BaseMenuInfoPanel.Content BuildContent(FlakUpgradeOptionConfig config, FlakUpgradeQuote q, string baseName,
        string baseId, string feedback, List<KeyValuePair<string, string>> rowBuffer)
    {
        rowBuffer.Clear();
        string identity = string.IsNullOrEmpty(baseName) ? baseId
            : string.IsNullOrEmpty(baseId) ? baseName
            : $"{baseName} ({baseId})";
        string footer = identity ?? string.Empty;
        if (!string.IsNullOrEmpty(feedback)) footer = string.IsNullOrEmpty(footer) ? feedback : feedback + "\n" + footer;

        BaseMenuInfoPanel.Content content = new BaseMenuInfoPanel.Content
        {
            preview = config != null ? config.ArtworkForCurrentLevel(q.currentLevel) : null,
            rows = rowBuffer,
            footer = footer
        };

        bool hasBase = q.status != FlakUpgradeResult.InvalidBase && q.status != FlakUpgradeResult.NotInitialised;
        switch (q.status)
        {
            case FlakUpgradeResult.InvalidBase:
                content.title = string.IsNullOrEmpty(baseId) ? "NO BASE SELECTED" : "BASE DESTROYED";
                content.description = string.IsNullOrEmpty(baseId) ? "Open the menu on a base to upgrade its Flaks." : "This base no longer exists.";
                break;
            case FlakUpgradeResult.NoFlaks:
                content.title = "BASE DESTROYED";
                content.description = "This base has no surviving Flak turrets and cannot be upgraded.";
                break;
            case FlakUpgradeResult.NotInitialised:
            case FlakUpgradeResult.ModelMissing:
            case FlakUpgradeResult.NoResourceSystem:
                content.title = "FLAK UPGRADE UNAVAILABLE";
                content.description = StatusText(q);
                break;
            case FlakUpgradeResult.AtMaximumLevel:
                content.title = "FLAKS AT MAX LEVEL";
                content.description = $"Every Flak turret on this base is at L{q.currentLevel}, the highest level. No further upgrade is available.";
                break;
            default:
                content.title = $"UPGRADE FLAKS TO L{q.nextLevel}";
                content.description = config != null ? config.description : string.Empty;
                if (q.status != FlakUpgradeResult.Available)
                {
                    // The blocking reason (not enough Gold, price pending, in progress, ...).
                    string reason = StatusText(q);
                    content.description = string.IsNullOrEmpty(content.description) ? reason : content.description + "\n" + reason;
                }

                break;
        }

        if (hasBase) rowBuffer.Add(new KeyValuePair<string, string>("Current level", $"L{q.currentLevel} of {FlakUpgradeRules.MaxLevel}"));
        if (IsMaxLevelState(q))
        {
            rowBuffer.Add(new KeyValuePair<string, string>("Next level", "MAX LEVEL"));
        }
        else if (hasBase && q.nextLevel > 0)
        {
            rowBuffer.Add(new KeyValuePair<string, string>("Next level", "L" + q.nextLevel));
            rowBuffer.Add(new KeyValuePair<string, string>("Cost", CostText(q)));
        }

        rowBuffer.Add(new KeyValuePair<string, string>("Your Gold", q.status == FlakUpgradeResult.NoResourceSystem ? "unavailable" : q.goldBalance.ToString("N0")));
        if (hasBase) rowBuffer.Add(new KeyValuePair<string, string>("Flaks on base", $"{q.flakCount} / {BaseFlakRoster.MaxFlaks}"));
        rowBuffer.Add(new KeyValuePair<string, string>("Availability", AvailabilityText(q)));

        return content;
    }
}
