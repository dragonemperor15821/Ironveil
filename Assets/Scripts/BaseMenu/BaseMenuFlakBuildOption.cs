using System.Collections.Generic;
using Ironveil.Economy;
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
/// Stage 5: connected to real construction. Everything shown comes from
/// BaseConstruction.EvaluateFlakConstruction (Version 1 schedule in
/// FlakConstructionRules, the base's built and queued Flaks, the shared
/// resources). A click starts exactly ONE construction through
/// BaseConstruction.TryStartFlakConstruction; the base's BaseFlakConstruction
/// queue builds it over time and places the Flak at the base's Flak level.
/// A progress strip on the cell shows the construction in progress, its
/// remaining time and the number of further queued Flaks. The menu never
/// spends resources or places Flaks itself.
///
/// Purchases are protected like Row 1 Box 1: the click passes the Flak number
/// the cell was SHOWING, a click for a base other than the one shown (or in
/// the frame it changed) buys nothing, and after a purchase further clicks on
/// that base are ignored for a short guard time. Independent of Box 1.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1030)] // after BaseMenuView (1020) has positioned the panel
[AddComponentMenu("")]
public sealed class BaseMenuFlakBuildOption : MonoBehaviour
{
    public const string IconName = "Flak Build Icon";
    public const string ProgressName = "Flak Build Progress";

    [Tooltip("Optional; when empty the config is loaded from Resources/" + FlakBuildOptionConfig.ResourcePath + ".")]
    [SerializeField] private FlakBuildOptionConfig configOverride;

    private FlakBuildOptionConfig config;
    private BaseMenuView view;
    private BaseMenuCell cell;
    private Image icon;
    private BaseMenuInfoPanel info;
    private bool hovered;
    private bool reportedMissing;
    private readonly List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>(8);

    // Progress strip on the cell.
    private RectTransform progressRoot;
    private Image progressFill;
    private Text progressText;

    private bool subscribed;
    private ResourceManager subscribedResources;

    // What the cell is showing (the basis of a purchase).
    private BaseController shownBase;
    private int shownFlakNumber;
    private bool hasShown;
    private int shownChangedFrame = -1;

    // Result of the latest click, shown for the base it was made on.
    private BaseController feedbackBase;
    private string feedback;

    // Double-click guard after a purchase.
    private BaseController guardBase;
    private float guardUntil;

    private string shownKey;
    private BaseController shownKeyBase;

    public FlakBuildOptionConfig Config => config;
    public BaseMenuCell Cell => cell;
    public Image Icon => icon;
    public BaseMenuInfoPanel Info => info;

    /// <summary>The progress strip on the cell (active while the shown base has queued constructions).</summary>
    public RectTransform Progress => progressRoot;
    public Text ProgressText => progressText;
    public Image ProgressFill => progressFill;

    /// <summary>The quote the cell and panel were last refreshed from.</summary>
    public FlakConstructionQuote CurrentQuote { get; private set; }

    /// <summary>The base the cell is showing (null when the menu has no base).</summary>
    public BaseController ShownBase => hasShown ? shownBase : null;

    /// <summary>Result of the most recent click (Available when nothing has been clicked yet).</summary>
    public FlakConstructionResult LastClickResult { get; private set; } = FlakConstructionResult.Available;

    /// <summary>Feedback line of the most recent click, or null.</summary>
    public string LastFeedback => feedback;

    private void OnEnable()
    {
        TryBind();
        Subscribe();
    }

    private void OnDisable()
    {
        Unbind();
        Unsubscribe();
        info?.Hide();
    }

    private void OnDestroy()
    {
        Unbind();
        Unsubscribe();
        info?.Destroy();
        info = null;
        DestroyObject(icon != null ? icon.gameObject : null);
        DestroyObject(progressRoot != null ? progressRoot.gameObject : null);
    }

    private static void DestroyObject(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    // ------------------------------------------------------------------ binding

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
            view.CellClicked += OnCellClicked;
        }

        if (cell != slot || icon == null)
        {
            cell = slot;
            PlaceIcon();
            PlaceProgress();
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

    private void Subscribe()
    {
        if (subscribed) return;
        BaseFlakDefence.Changed += OnDefenceChanged;
        BaseFlakConstruction.Changed += OnQueueChanged;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (subscribed)
        {
            BaseFlakDefence.Changed -= OnDefenceChanged;
            BaseFlakConstruction.Changed -= OnQueueChanged;
            subscribed = false;
        }

        if (!ReferenceEquals(subscribedResources, null) && subscribedResources != null)
        {
            subscribedResources.ResourceChanged -= OnResourceChanged;
            subscribedResources.LedgerReset -= OnLedgerReset;
        }

        subscribedResources = null;
    }

    /// <summary>Follows the session's ResourceManager (it may be created after the menu).</summary>
    private void EnsureResourceSubscription()
    {
        ResourceManager current = ResourceManager.Instance;
        if (ReferenceEquals(current, subscribedResources)) return;
        if (!ReferenceEquals(subscribedResources, null) && subscribedResources != null)
        {
            subscribedResources.ResourceChanged -= OnResourceChanged;
            subscribedResources.LedgerReset -= OnLedgerReset;
        }

        subscribedResources = current;
        if (current == null) return;
        current.ResourceChanged += OnResourceChanged;
        current.LedgerReset += OnLedgerReset;
    }

    private void OnDefenceChanged(BaseFlakDefence changed) => shownKey = null;
    private void OnQueueChanged(BaseFlakConstruction changed) => shownKey = null;
    private void OnResourceChanged(ResourceChange change) => shownKey = null;
    private void OnLedgerReset() => shownKey = null;

    private void PlaceIcon()
    {
        DestroyObject(icon != null ? icon.gameObject : null);
        icon = null;

        GameObject go = new GameObject(IconName, typeof(RectTransform));
        go.layer = 5;
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(cell.Content, false);
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

    /// <summary>Thin strip along the bottom of the cell: progress fill + "#3 5:12 +2".</summary>
    private void PlaceProgress()
    {
        DestroyObject(progressRoot != null ? progressRoot.gameObject : null);

        GameObject root = new GameObject(ProgressName, typeof(RectTransform));
        root.layer = 5;
        progressRoot = (RectTransform)root.transform;
        progressRoot.SetParent(cell.Content, false);
        progressRoot.anchorMin = new Vector2(0.04f, 0.04f);
        progressRoot.anchorMax = new Vector2(0.96f, 0.30f);
        progressRoot.offsetMin = Vector2.zero;
        progressRoot.offsetMax = Vector2.zero;
        Image back = root.AddComponent<Image>();
        back.color = new Color(0.04f, 0.06f, 0.08f, 0.82f);
        back.raycastTarget = false;

        GameObject fill = new GameObject("Fill", typeof(RectTransform));
        fill.layer = 5;
        RectTransform fillRect = (RectTransform)fill.transform;
        fillRect.SetParent(progressRoot, false);
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = new Vector2(0f, 1f);
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        progressFill = fill.AddComponent<Image>();
        progressFill.color = new Color(BaseMenuInfoPanel.Gold.r, BaseMenuInfoPanel.Gold.g, BaseMenuInfoPanel.Gold.b, 0.85f);
        progressFill.raycastTarget = false;

        GameObject label = new GameObject("Label", typeof(RectTransform));
        label.layer = 5;
        RectTransform labelRect = (RectTransform)label.transform;
        labelRect.SetParent(progressRoot, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(2f, 0f);
        labelRect.offsetMax = new Vector2(-2f, 0f);
        progressText = label.AddComponent<Text>();
        progressText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        progressText.alignment = TextAnchor.MiddleCenter;
        progressText.color = Color.white;
        progressText.fontStyle = FontStyle.Bold;
        progressText.resizeTextForBestFit = true;
        progressText.resizeTextMinSize = 6;
        progressText.resizeTextMaxSize = 40;
        progressText.raycastTarget = false;
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
        UpdateInfo();
    }

    private void OnCellClicked(BaseMenuView.CellClick click)
    {
        if (click.cell == null || click.cell != cell)
        {
            return; // another cell (e.g. Box 1): not ours
        }

        HandleClick(click.selected);
    }

    /// <summary>
    /// A click on the cell for the menu's base <paramref name="selected"/>. Starts ONE construction
    /// through BaseConstruction.TryStartFlakConstruction when EvaluateFlakConstruction allows it;
    /// otherwise only reports why not. Always refreshes the cell and panel afterwards.
    /// </summary>
    public FlakConstructionResult HandleClick(BaseContext selected)
    {
        BaseController controller = selected != null && selected.Controller != null ? selected.Controller : null;
        FlakConstructionQuote quote = BaseConstruction.EvaluateFlakConstruction(controller);
        FlakConstructionResult result;

        if (controller != null && ReferenceEquals(controller, guardBase) && Time.realtimeSinceStartup < guardUntil)
        {
            result = FlakConstructionResult.FlakNumberChanged; // a double click: ignored, the purchase message stays
        }
        else if (!hasShown || !ReferenceEquals(controller, shownBase) || Time.frameCount == shownChangedFrame)
        {
            result = FlakConstructionResult.FlakNumberChanged;
            SetFeedback(controller, "The menu had not shown this base yet; nothing was built.");
        }
        else if (!quote.CanStart)
        {
            result = quote.status;
            SetFeedback(controller, StatusText(quote));
        }
        else
        {
            result = BaseConstruction.TryStartFlakConstruction(controller, shownFlakNumber);
            switch (result)
            {
                case FlakConstructionResult.Started:
                    guardBase = controller;
                    guardUntil = Time.realtimeSinceStartup + (config != null ? config.clickGuardSeconds : 0f);
                    // No level in the message: the turret is built at the base's level when it COMPLETES.
                    SetFeedback(controller, $"Started base turret #{quote.flakNumber}: " +
                                            $"{FlakBuildOptionConfig.FormatDuration(quote.cost.durationSeconds)}.");
                    break;
                case FlakConstructionResult.FlakNumberChanged:
                    SetFeedback(controller, "The next turret number had already changed; nothing was built.");
                    break;
                default:
                    FlakConstructionQuote after = BaseConstruction.EvaluateFlakConstruction(controller);
                    after.status = result;
                    SetFeedback(controller, "Construction not started: " + StatusText(after));
                    break;
            }
        }

        LastClickResult = result;
        shownKey = null;
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

    /// <summary>Shows, refreshes or hides the hover panel and the progress strip. Tests call it directly.</summary>
    public void UpdateInfo() => Refresh(true);

    /// <summary>
    /// Re-reads the selected base, its construction quote and queue and the shared resources;
    /// updates the progress strip and shows, refreshes or hides the hover panel.
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
        FlakConstructionQuote quote = BaseConstruction.EvaluateFlakConstruction(selectedBase);
        CurrentQuote = quote;

        if (!ReferenceEquals(selectedBase, shownBase) && !ReferenceEquals(feedbackBase, selectedBase))
        {
            feedback = null; // feedback belongs to the base it was made on
            feedbackBase = null;
        }

        if (!ReferenceEquals(selectedBase, shownBase) || quote.flakNumber != shownFlakNumber) shownChangedFrame = Time.frameCount;
        shownBase = selectedBase;
        shownFlakNumber = quote.flakNumber;
        hasShown = selectedBase != null;

        UpdateProgress(selectedBase);

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
        // Whole seconds in the key, so the countdown refreshes once per second.
        string key = $"{selected?.BaseId}|{quote.status}|{quote.flakNumber}|{quote.builtCount}|{quote.queuedCount}|{quote.flakLevel}|" +
                     $"{quote.concreteBalance}|{quote.ironBalance}|{quote.aluminiumBalance}|{(long)System.Math.Ceiling(quote.currentRemainingSeconds)}|" +
                     $"{feedback}|{panel}|{screen}";
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

    private void UpdateProgress(BaseController selectedBase)
    {
        if (progressRoot == null) return;
        BaseFlakConstruction queue = BaseConstruction.GetConstructionQueue(selectedBase);
        BaseFlakConstruction.Order current = queue != null ? queue.Current : null;
        bool active = current != null;
        if (progressRoot.gameObject.activeSelf != active) progressRoot.gameObject.SetActive(active);
        if (!active) return;

        RectTransform fillRect = progressFill.rectTransform;
        Vector2 max = fillRect.anchorMax;
        max.x = current.Progress;
        fillRect.anchorMax = max;
        progressText.text = ProgressLabel(current.FlakNumber, current.RemainingSeconds, queue.Count - 1);
    }

    /// <summary>"#3  5:12", "#4  1:02:30  +2". Pure; used by tests.</summary>
    public static string ProgressLabel(int flakNumber, double remainingSeconds, int waiting)
    {
        int s = (int)System.Math.Ceiling(System.Math.Max(0.0, remainingSeconds));
        string time = s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        return $"#{flakNumber}  {time}" + (waiting > 0 ? $"  +{waiting}" : string.Empty);
    }

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

    // ------------------------------------------------------------------ content (pure)

    private static string MissingList(FlakConstructionQuote q, bool amounts)
    {
        if (q.missing == null || q.missing.Count == 0) return string.Empty;
        var parts = new List<string>(q.missing.Count);
        foreach (KeyValuePair<ResourceType, long> m in q.missing)
        {
            string name = ResourceName(m.Key);
            parts.Add(amounts ? $"{ResourceHudFormat.Amount(m.Value)} {name}" : name);
        }

        return string.Join(", ", parts);
    }

    private static string StorageList(FlakConstructionQuote q, bool amounts)
    {
        if (q.exceedsStorage == null || q.exceedsStorage.Count == 0) return string.Empty;
        var parts = new List<string>(q.exceedsStorage.Count);
        foreach (KeyValuePair<ResourceType, long> m in q.exceedsStorage)
        {
            string name = ResourceName(m.Key);
            parts.Add(amounts ? $"{name} (+{ResourceHudFormat.Amount(m.Value)})" : name);
        }

        return string.Join(", ", parts);
    }

    private static string ResourceName(ResourceType r) =>
        r == ResourceType.Concrete ? "Concrete" : r == ResourceType.Iron ? "Iron" : r == ResourceType.Aluminium ? "Aluminium" : r.ToString();

    /// <summary>One-line status for a quote (descriptions and click feedback). Pure; used by tests.</summary>
    public static string StatusText(FlakConstructionQuote q)
    {
        switch (q.status)
        {
            case FlakConstructionResult.Available: return "Available: click to build.";
            case FlakConstructionResult.Started: return "Construction started.";
            case FlakConstructionResult.InsufficientResources: return "Not enough resources: missing " + MissingList(q, true) + ".";
            case FlakConstructionResult.MaxCapacity: return $"MAX CAPACITY: all {FlakConstructionRules.MaxFlaks} positions are built or under construction.";
            case FlakConstructionResult.BaseDestroyed: return "Base destroyed: it has no surviving turrets.";
            case FlakConstructionResult.InvalidBase: return "Base destroyed: it no longer exists.";
            case FlakConstructionResult.NotInitialised: return "Unavailable: this base has no Flak defence system.";
            case FlakConstructionResult.ModelMissing: return $"Unavailable: no L{q.flakLevel} Flak model is configured.";
            case FlakConstructionResult.NoResourceSystem: return "Unavailable: the resource system is not running.";
            case FlakConstructionResult.UpgradeInProgress: return "A Flak upgrade of this base is in progress.";
            case FlakConstructionResult.FlakNumberChanged: return "The next turret number has changed.";
            case FlakConstructionResult.SpendFailed: return "The resources could not be spent.";
            case FlakConstructionResult.ReservationFailed: return "No free position could be reserved; the resources were refunded.";
            case FlakConstructionResult.StorageInsufficient: return "Storage too small: this turret costs more than you can store. Expand storage with additional bases: " + StorageList(q, true) + ".";
            case FlakConstructionResult.RebuildNotEligible: return $"Turret #{FlakConstructionRules.RebuildFlak} can only be rebuilt when attacks have left the base with one turret.";
            default: return q.status.ToString();
        }
    }

    /// <summary>Short availability value for the hover panel's two-column row. Pure; used by tests.</summary>
    public static string AvailabilityText(FlakConstructionQuote q)
    {
        switch (q.status)
        {
            case FlakConstructionResult.Available: return "Click to build";
            case FlakConstructionResult.InsufficientResources: return "Missing " + MissingList(q, false);
            case FlakConstructionResult.MaxCapacity: return "MAX CAPACITY";
            case FlakConstructionResult.BaseDestroyed:
            case FlakConstructionResult.InvalidBase: return "Base destroyed";
            case FlakConstructionResult.UpgradeInProgress: return "Upgrade running";
            case FlakConstructionResult.ModelMissing: return "Model missing";
            case FlakConstructionResult.StorageInsufficient: return "Needs more storage";
            case FlakConstructionResult.RebuildNotEligible: return "Rebuild only";
            default: return "Unavailable";
        }
    }

    /// <summary>Hover text for a construction quote. Pure; used by tests.</summary>
    public static BaseMenuInfoPanel.Content BuildContent(FlakBuildOptionConfig config, FlakConstructionQuote q, string baseName,
        string baseId, string feedback, List<KeyValuePair<string, string>> rowBuffer)
    {
        rowBuffer.Clear();
        bool hasBase = q.status != FlakConstructionResult.InvalidBase && q.status != FlakConstructionResult.NotInitialised;
        string footer = hasBase ? $"Base turrets: {Mathf.Max(0, q.builtCount)} / {FlakConstructionRules.MaxFlaks}" : string.Empty;
        string identity = string.IsNullOrEmpty(baseName) ? baseId
            : string.IsNullOrEmpty(baseId) ? baseName
            : $"{baseName} ({baseId})";
        if (!string.IsNullOrEmpty(identity)) footer = string.IsNullOrEmpty(footer) ? identity : footer + "\n" + identity;
        if (!string.IsNullOrEmpty(feedback)) footer = feedback + (string.IsNullOrEmpty(footer) ? string.Empty : "\n" + footer);

        BaseMenuInfoPanel.Content content = new BaseMenuInfoPanel.Content
        {
            preview = config != null ? config.artwork : null,
            rows = rowBuffer,
            footer = footer
        };

        switch (q.status)
        {
            case FlakConstructionResult.InvalidBase:
                content.title = string.IsNullOrEmpty(baseId) ? "NO BASE SELECTED" : "BASE DESTROYED";
                content.description = string.IsNullOrEmpty(baseId) ? "Open the menu on a base to build turrets." : "This base no longer exists.";
                break;
            case FlakConstructionResult.BaseDestroyed:
                content.title = "BASE DESTROYED";
                content.description = "This base has no surviving turrets and has fallen. No further turrets can be built.";
                break;
            case FlakConstructionResult.MaxCapacity:
                content.title = "MAX CAPACITY";
                content.description = $"All {FlakConstructionRules.MaxFlaks} turret positions of this base are built or under construction.";
                break;
            case FlakConstructionResult.NotInitialised:
            case FlakConstructionResult.ModelMissing:
            case FlakConstructionResult.NoResourceSystem:
                content.title = "TURRET CONSTRUCTION UNAVAILABLE";
                content.description = StatusText(q);
                break;
            default:
                content.title = q.isRebuild
                    ? $"REBUILD {BaseFlakRoster.Ordinal(q.flakNumber)} BASE TURRET"
                    : $"BUILD {BaseFlakRoster.Ordinal(q.flakNumber)} BASE TURRET";
                content.description = config != null ? config.description : string.Empty;
                if (q.isRebuild) content.description = (string.IsNullOrEmpty(content.description) ? string.Empty : content.description + "\n") +
                    "Rebuild: attacks left this base with one turret.";
                if (q.status == FlakConstructionResult.StorageInsufficient)
                    content.description = (string.IsNullOrEmpty(content.description) ? string.Empty : content.description + "\n") + StatusText(q);
                break;
        }

        if (q.flakNumber > 0)
        {
            // Cost / available, per resource.
            rowBuffer.Add(new KeyValuePair<string, string>("Concrete", $"{ResourceHudFormat.Amount(q.cost.concrete)} / {ResourceHudFormat.Amount(q.concreteBalance)}"));
            rowBuffer.Add(new KeyValuePair<string, string>("Iron", $"{ResourceHudFormat.Amount(q.cost.iron)} / {ResourceHudFormat.Amount(q.ironBalance)}"));
            rowBuffer.Add(new KeyValuePair<string, string>("Aluminium", $"{ResourceHudFormat.Amount(q.cost.aluminium)} / {ResourceHudFormat.Amount(q.aluminiumBalance)}"));
            rowBuffer.Add(new KeyValuePair<string, string>("Construction time", FlakBuildOptionConfig.FormatDuration(q.cost.durationSeconds)));
        }

        if (hasBase)
        {
            rowBuffer.Add(new KeyValuePair<string, string>("Turrets", q.queuedCount > 0
                ? $"{q.builtCount} + {q.queuedCount} queued / {FlakConstructionRules.MaxFlaks}"
                : $"{q.builtCount} / {FlakConstructionRules.MaxFlaks}"));
            if (q.queuedCount > 0)
            {
                rowBuffer.Add(new KeyValuePair<string, string>("Building now", FlakBuildOptionConfig.FormatRemaining(q.currentRemainingSeconds) + " left"));
                if (q.queuedCount > 1)
                    rowBuffer.Add(new KeyValuePair<string, string>("Queue finished in", FlakBuildOptionConfig.FormatRemaining(q.queueRemainingSeconds)));
            }
        }

        rowBuffer.Add(new KeyValuePair<string, string>("Availability", AvailabilityText(q)));
        return content;
    }
}
