using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// IRONVEIL World Explorer interface (Stage 3F presentation pass).
///
///  - Top-left: brand, world information (seed, 900 x 900 m, 350 bases), New World and Share World, status line.
///  - Bottom-left: collapsible camera-controls reference.
///  - Top-centre: small indicator while a new arrangement is generated, then a short confirmation.
///  - Full-screen loading cover until the requested world is ready. Terrain progress comes from
///    WorldGenerationPrototype.GenerationProgress (real chunk-row progress); later steps report no
///    progress, so they show phase text and an indeterminate bar, never a percentage.
///
/// Built at runtime with uGUI and the built-in font (no extra assets). Layout units are CSS pixels at
/// scale 1; the whole canvas scales with the device pixel ratio and shrinks for small canvases.
/// Per-frame work is limited to the loading cover, the indicator animation and a cheap size check.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ExplorerWorldManager))]
public sealed class ExplorerWorldUI : MonoBehaviour
{
    // Layout (CSS px at scale 1).
    private const float Margin = 14f;
    private const float PanelWidth = 276f;
    private const float Pad = 14f;
    private const float ControlsWidth = 214f;
    private const float ReferenceWidth = 960f;   // Default Web template canvas
    private const float ReferenceHeight = 600f;

    // Palette: charcoal and steel, one muted brass accent.
    private static readonly Color PanelColor = new Color(0.085f, 0.095f, 0.105f, 0.84f);
    private static readonly Color BorderColor = new Color(0.42f, 0.46f, 0.50f, 0.45f);
    private static readonly Color DividerColor = new Color(0.42f, 0.46f, 0.50f, 0.28f);
    private static readonly Color Accent = new Color(0.74f, 0.63f, 0.40f, 1f);
    private static readonly Color PrimaryButton = new Color(0.25f, 0.29f, 0.32f, 1f);
    private static readonly Color SecondaryButton = new Color(0.16f, 0.18f, 0.20f, 1f);
    private static readonly Color KeyCap = new Color(0.17f, 0.19f, 0.21f, 1f);
    private static readonly Color TextColor = new Color(0.89f, 0.91f, 0.93f, 1f);
    private static readonly Color MutedColor = new Color(0.60f, 0.64f, 0.68f, 1f);
    private static readonly Color OkColor = new Color(0.58f, 0.80f, 0.62f, 1f);
    private static readonly Color WarnColor = new Color(0.90f, 0.75f, 0.45f, 1f);
    private static readonly Color ErrorColor = new Color(0.93f, 0.52f, 0.47f, 1f);
    private static readonly Color CoverColor = new Color(0.055f, 0.06f, 0.067f, 1f);

    private ExplorerWorldManager manager;
    private Font font;
    private Canvas canvas;
    private CanvasScaler scaler;
    private float dpiScale = 1f;
    private int lastScreenW = -1, lastScreenH = -1;
    private float lastDpi = -1f;

    // Main panel.
    private RectTransform panel;
    private Text seedValue;
    private Text statusText;
    private Button newWorldButton, shareButton;
    private Text newWorldLabel, shareLabel;
    private float panelHeight;

    // Controls card.
    private RectTransform controlsCard;
    private GameObject controlsBody;
    private Text controlsToggleText;
    private bool controlsExpanded = true;
    private bool controlsAutoCollapsed;

    // New World indicator.
    private RectTransform indicator;
    private CanvasGroup indicatorGroup;
    private Text indicatorText;
    private RectTransform indicatorBar;
    private float indicatorHideAt = -1f;

    // Loading cover.
    private GameObject cover;
    private CanvasGroup coverGroup;
    private Text coverStatus;
    private Text coverPercent;
    private RectTransform coverBarFill;
    private RectTransform coverBarTrack;
    private float coverFadeStart = -1f;
    private int lastCoverKey = int.MinValue;

    /// <summary>Button rectangles, for tooling and layout checks.</summary>
    public RectTransform NewWorldButtonRect => newWorldButton != null ? (RectTransform)newWorldButton.transform : null;
    public RectTransform ShareButtonRect => shareButton != null ? (RectTransform)shareButton.transform : null;
    public RectTransform PanelRect => panel;
    public RectTransform ControlsRect => controlsCard;
    public bool ControlsExpanded => controlsExpanded;

    private void Awake()
    {
        manager = GetComponent<ExplorerWorldManager>();
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        EnsureEventSystem();
        Build();
        UpdateScale(true);
    }

    private void OnEnable()
    {
        if (manager != null) manager.Changed += Refresh;
        Refresh(manager);
    }

    private void OnDisable()
    {
        if (manager != null) manager.Changed -= Refresh;
    }

    private void Update()
    {
        UpdateScale(false);
        if (cover.activeSelf) UpdateCover();
        if (indicator.gameObject.activeSelf) UpdateIndicator();
    }

    // ------------------------------------------------------------------ scaling

    private void UpdateScale(bool force)
    {
        if (!force && Screen.width == lastScreenW && Screen.height == lastScreenH && Mathf.Approximately(Screen.dpi, lastDpi)) return;
        lastScreenW = Screen.width; lastScreenH = Screen.height; lastDpi = Screen.dpi;
        dpiScale = lastDpi > 1f ? Mathf.Clamp(lastDpi / 96f, 0.5f, 4f) : 1f;
        float cssW = Screen.width / dpiScale, cssH = Screen.height / dpiScale;
        float fit = Mathf.Clamp(Mathf.Min(cssW / ReferenceWidth, cssH / ReferenceHeight), 0.72f, 1f);
        scaler.scaleFactor = dpiScale * fit;
        // Small canvases: fold the controls card away once so it never meets the main panel.
        float available = Screen.height / scaler.scaleFactor;
        bool tight = Margin + panelHeight + 12f + ControlsExpandedHeight() + Margin > available;
        if (tight && controlsExpanded && !controlsAutoCollapsed) { controlsAutoCollapsed = true; SetControlsExpanded(false); }
        UpdateFallbackPosition();
    }

    private void UpdateFallbackPosition()
    {
        if (manager == null || scaler == null) return;
        float cssPerUnit = scaler.scaleFactor / dpiScale;
        manager.FallbackTopCssPx = Mathf.RoundToInt((Margin + panelHeight + 8f) * cssPerUnit);
        manager.FallbackLeftCssPx = Mathf.RoundToInt(Margin * cssPerUnit);
    }

    // ------------------------------------------------------------------ build

    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }

    private void Build()
    {
        var root = new GameObject("Explorer UI", typeof(RectTransform));
        root.transform.SetParent(transform, false);
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        root.AddComponent<GraphicRaycaster>();

        BuildPanel(root.transform);
        BuildControls(root.transform);
        BuildIndicator(root.transform);
        BuildCover(root.transform);
    }

    private void BuildPanel(Transform root)
    {
        panel = Framed("World Panel", root, PanelColor);
        Anchor(panel, new Vector2(0f, 1f), new Vector2(Margin, -Margin));

        float y = Pad - 2f;
        Text brand = Label("Brand", panel, "IRONVEIL", 24, TextColor, TextAnchor.UpperLeft, FontStyle.Bold);
        Place(brand.rectTransform, Pad, y, PanelWidth - 2 * Pad, 28f);
        y += 30f;
        RectTransform rule = Rect("Accent Rule", panel, Accent);
        Place(rule, Pad, y, 30f, 2f);
        Text sub = Label("Subtitle", panel, "W O R L D   E X P L O R E R", 10, MutedColor, TextAnchor.MiddleLeft, FontStyle.Normal);
        Place(sub.rectTransform, Pad + 38f, y - 6f, PanelWidth - 2 * Pad - 38f, 14f);
        y += 14f;

        Place(Rect("Divider", panel, DividerColor), Pad, y, PanelWidth - 2 * Pad, 1f);
        y += 10f;

        Text seedLabel = Label("Seed Label", panel, "WORLD SEED", 10, MutedColor, TextAnchor.MiddleLeft, FontStyle.Normal);
        Place(seedLabel.rectTransform, Pad, y, 90f, 20f);
        seedValue = Label("Seed", panel, "-", 16, TextColor, TextAnchor.MiddleRight, FontStyle.Bold);
        Place(seedValue.rectTransform, Pad + 80f, y, PanelWidth - 2 * Pad - 80f, 20f);
        y += 22f;

        Text facts = Label("Facts", panel, "900 × 900 m  ·  350 bases", 12, TextColor, TextAnchor.MiddleLeft, FontStyle.Normal);
        Place(facts.rectTransform, Pad, y, PanelWidth - 2 * Pad, 16f);
        y += 17f;
        Text note = Label("Note", panel, "Fixed terrain. Base arrangement is set by the seed.", 10, MutedColor, TextAnchor.MiddleLeft, FontStyle.Italic);
        Place(note.rectTransform, Pad, y, PanelWidth - 2 * Pad, 14f);
        y += 22f;

        float bw = (PanelWidth - 2 * Pad - 8f) * 0.5f;
        newWorldButton = MakeButton("New World Button", panel, "NEW WORLD", PrimaryButton, true, out newWorldLabel);
        Place((RectTransform)newWorldButton.transform, Pad, y, bw, 30f);
        newWorldButton.onClick.AddListener(() => manager.NewWorld());
        shareButton = MakeButton("Share World Button", panel, "SHARE WORLD", SecondaryButton, false, out shareLabel);
        Place((RectTransform)shareButton.transform, Pad + bw + 8f, y, bw, 30f);
        shareButton.onClick.AddListener(() => manager.ShareWorld());
        y += 38f;

        statusText = Label("Status", panel, string.Empty, 11, MutedColor, TextAnchor.UpperLeft, FontStyle.Normal);
        statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
        statusText.verticalOverflow = VerticalWrapMode.Overflow;
        Place(statusText.rectTransform, Pad, y, PanelWidth - 2 * Pad, 15f);
        statusTop = y;
        LayoutPanelHeight();
    }

    private float statusTop;

    private void LayoutPanelHeight()
    {
        float statusH = Mathf.Max(15f, statusText.preferredHeight);
        statusText.rectTransform.sizeDelta = new Vector2(PanelWidth - 2 * Pad, statusH);
        panelHeight = statusTop + statusH + Pad - 2f;
        panel.sizeDelta = new Vector2(PanelWidth, panelHeight);
        UpdateFallbackPosition();
    }

    private static readonly string[,] ControlRows =
    {
        { "WASD / Arrows", "Move" },
        { "Drag", "Pan" },
        { "Scroll", "Zoom to cursor" },
        { "Q / E", "Rotate" },
        { "R", "Reset view" },
        { "Shift", "Faster movement" },
    };

    private float ControlsExpandedHeight() => 30f + ControlRows.GetLength(0) * 21f + 8f;

    private void BuildControls(Transform root)
    {
        controlsCard = Framed("Controls Card", root, PanelColor);
        controlsCard.anchorMin = controlsCard.anchorMax = controlsCard.pivot = new Vector2(0f, 0f);
        controlsCard.anchoredPosition = new Vector2(Margin, Margin);

        Button toggle = MakeButton("Controls Toggle", controlsCard, string.Empty, new Color(0f, 0f, 0f, 0f), false, out _);
        var toggleRect = (RectTransform)toggle.transform;
        toggleRect.anchorMin = new Vector2(0f, 1f); toggleRect.anchorMax = new Vector2(1f, 1f); toggleRect.pivot = new Vector2(0.5f, 1f);
        toggleRect.anchoredPosition = Vector2.zero; toggleRect.sizeDelta = new Vector2(0f, 28f);
        toggle.onClick.AddListener(() => SetControlsExpanded(!controlsExpanded));
        Text title = Label("Title", toggleRect, "CONTROLS", 10, MutedColor, TextAnchor.MiddleLeft, FontStyle.Bold);
        Stretch(title.rectTransform, 12f, 0f, 44f, 0f);
        controlsToggleText = Label("Toggle", toggleRect, "HIDE", 9, MutedColor, TextAnchor.MiddleRight, FontStyle.Bold);
        Stretch(controlsToggleText.rectTransform, 0f, 0f, 12f, 0f);

        var body = new GameObject("Body", typeof(RectTransform));
        body.transform.SetParent(controlsCard, false);
        controlsBody = body;
        var bodyRect = (RectTransform)body.transform;
        bodyRect.anchorMin = bodyRect.anchorMax = bodyRect.pivot = new Vector2(0f, 1f);
        bodyRect.anchoredPosition = new Vector2(0f, -30f);
        bodyRect.sizeDelta = new Vector2(ControlsWidth, ControlRows.GetLength(0) * 21f);
        for (int i = 0; i < ControlRows.GetLength(0); i++)
        {
            float y = i * 21f;
            string key = ControlRows[i, 0];
            float keyWidth = 14f + key.Length * 6.4f;
            RectTransform cap = Framed("Key " + key, bodyRect, KeyCap);
            Place(cap, 12f, y, keyWidth, 17f);
            Text keyText = Label("Text", cap, key, 10, TextColor, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(keyText.rectTransform, 0f, 0f, 0f, 0f);
            Text action = Label("Action " + key, bodyRect, ControlRows[i, 1], 11, MutedColor, TextAnchor.MiddleLeft, FontStyle.Normal);
            Place(action.rectTransform, 12f + keyWidth + 8f, y, ControlsWidth - keyWidth - 32f, 17f);
        }
        SetControlsExpanded(true);
    }

    private void SetControlsExpanded(bool expanded)
    {
        controlsExpanded = expanded;
        controlsBody.SetActive(expanded);
        controlsToggleText.text = expanded ? "HIDE" : "SHOW";
        controlsCard.sizeDelta = new Vector2(expanded ? ControlsWidth : 132f, expanded ? ControlsExpandedHeight() : 28f);
    }

    private void BuildIndicator(Transform root)
    {
        indicator = Framed("New World Indicator", root, PanelColor);
        indicator.anchorMin = indicator.anchorMax = indicator.pivot = new Vector2(0.5f, 1f);
        indicator.anchoredPosition = new Vector2(0f, -Margin);
        indicator.sizeDelta = new Vector2(292f, 34f);
        indicatorGroup = indicator.gameObject.AddComponent<CanvasGroup>();
        indicatorGroup.blocksRaycasts = false;
        indicatorText = Label("Text", indicator, string.Empty, 12, TextColor, TextAnchor.MiddleCenter, FontStyle.Normal);
        Stretch(indicatorText.rectTransform, 10f, 3f, 10f, 0f);
        RectTransform track = Rect("Track", indicator, new Color(1f, 1f, 1f, 0.06f));
        track.anchorMin = new Vector2(0f, 0f); track.anchorMax = new Vector2(1f, 0f); track.pivot = new Vector2(0.5f, 0f);
        track.anchoredPosition = Vector2.zero; track.sizeDelta = new Vector2(0f, 2f);
        indicatorBar = Rect("Bar", track, Accent);
        indicatorBar.anchorMin = indicatorBar.anchorMax = indicatorBar.pivot = new Vector2(0f, 0.5f);
        indicatorBar.sizeDelta = new Vector2(70f, 2f);
        indicator.gameObject.SetActive(false);
    }

    private void BuildCover(Transform root)
    {
        RectTransform c = Rect("Generating Cover", root, CoverColor);
        Stretch(c, 0f, 0f, 0f, 0f);
        c.GetComponent<Image>().raycastTarget = true; // blocks clicks to the world while loading
        cover = c.gameObject;
        coverGroup = cover.AddComponent<CanvasGroup>();

        var block = new GameObject("Center", typeof(RectTransform));
        block.transform.SetParent(c, false);
        var b = (RectTransform)block.transform;
        b.anchorMin = b.anchorMax = b.pivot = new Vector2(0.5f, 0.5f);
        b.sizeDelta = new Vector2(420f, 170f);

        Text brand = Label("Brand", b, "IRONVEIL", 46, TextColor, TextAnchor.MiddleCenter, FontStyle.Bold);
        Place(brand.rectTransform, 0f, 0f, 420f, 54f);
        RectTransform rule = Rect("Accent Rule", b, Accent);
        Place(rule, 210f - 30f, 60f, 60f, 2f);
        Text sub = Label("Subtitle", b, "W O R L D   E X P L O R E R", 13, MutedColor, TextAnchor.MiddleCenter, FontStyle.Normal);
        Place(sub.rectTransform, 0f, 70f, 420f, 18f);

        coverStatus = Label("Status", b, "Preparing world...", 13, TextColor, TextAnchor.MiddleCenter, FontStyle.Normal);
        Place(coverStatus.rectTransform, 0f, 110f, 420f, 18f);
        coverBarTrack = Rect("Track", b, new Color(1f, 1f, 1f, 0.08f));
        Place(coverBarTrack, 90f, 136f, 240f, 3f);
        coverBarFill = Rect("Fill", coverBarTrack, Accent);
        coverBarFill.anchorMin = coverBarFill.anchorMax = coverBarFill.pivot = new Vector2(0f, 0.5f);
        coverBarFill.sizeDelta = new Vector2(0f, 3f);
        coverPercent = Label("Percent", b, string.Empty, 11, MutedColor, TextAnchor.MiddleCenter, FontStyle.Normal);
        Place(coverPercent.rectTransform, 0f, 145f, 420f, 16f);
    }

    // ------------------------------------------------------------------ state

    private void Refresh(ExplorerWorldManager m)
    {
        if (m == null || seedValue == null) return;
        ExplorerWorldManager.State state = m.CurrentState;

        bool initial = state == ExplorerWorldManager.State.WaitingForGeneration || state == ExplorerWorldManager.State.ApplyingSeed;
        bool fatal = state == ExplorerWorldManager.State.Failed && m.CurrentSeed == 0;
        if (initial || fatal)
        {
            cover.SetActive(true);
            coverGroup.alpha = 1f;
            coverGroup.blocksRaycasts = true;
            coverFadeStart = -1f;
            lastCoverKey = int.MinValue;
            UpdateCover();
        }
        else if (cover.activeSelf && coverFadeStart < 0f)
        {
            coverFadeStart = Time.unscaledTime;   // ready: fade the cover out
            coverGroup.blocksRaycasts = false;
        }

        seedValue.text = m.CurrentSeed > 0 ? m.CurrentSeed.ToString() : "-";
        newWorldButton.interactable = m.IsReady;
        shareButton.interactable = m.IsReady && !m.IsCopying;
        newWorldLabel.text = state == ExplorerWorldManager.State.Regenerating ? "GENERATING..." : "NEW WORLD";
        shareLabel.text = m.IsCopying ? "COPYING..." : "SHARE WORLD";

        // Top-centre indicator: shown while a new arrangement is generated, then a short confirmation.
        if (state == ExplorerWorldManager.State.Regenerating)
        {
            ShowIndicator("Generating a new base arrangement...", true, -1f);
            wasRegenerating = true;
        }
        else if (wasRegenerating && state == ExplorerWorldManager.State.Ready)
        {
            wasRegenerating = false;
            if (string.IsNullOrEmpty(m.LastError)) ShowIndicator("New world ready · seed " + m.CurrentSeed, false, 2.2f);
            else ShowIndicator("Generation failed · previous world kept", false, 3f);
        }

        string status;
        Color color = MutedColor;
        if (state == ExplorerWorldManager.State.Regenerating) status = "Generating a new base arrangement...";
        else if (!string.IsNullOrEmpty(m.LastError)) { status = m.LastError; color = ErrorColor; }
        else if (m.IsCopying) status = "Copying link...";
        else if (m.LastShareOutcome == ExplorerWorldManager.ShareOutcome.Copied ||
                 m.LastShareOutcome == ExplorerWorldManager.ShareOutcome.CopiedLegacy) { status = "Link copied to clipboard."; color = OkColor; }
        else if (m.LastShareOutcome == ExplorerWorldManager.ShareOutcome.FallbackShown) { status = "Clipboard unavailable. Copy the link below."; color = WarnColor; }
        else if (m.LastShareOutcome == ExplorerWorldManager.ShareOutcome.Failed) { status = "Copy failed. Link: " + m.ShareUrl; color = ErrorColor; }
        else if (!string.IsNullOrEmpty(m.CompatibilityNotice)) { status = m.CompatibilityNotice; color = WarnColor; }
        else if (!string.IsNullOrEmpty(m.SeedNotice)) { status = m.SeedNotice; color = WarnColor; }
        else status = m.StartupSeedFromUrl && m.CurrentSeed == m.StartupRequest.Seed ? "Shared world loaded." : "Share World copies a link to this exact world.";
        statusText.text = status;
        statusText.color = color;
        LayoutPanelHeight();
    }

    private bool wasRegenerating;

    private void UpdateCover()
    {
        if (coverFadeStart >= 0f)
        {
            float t = (Time.unscaledTime - coverFadeStart) / 0.45f;
            coverGroup.alpha = 1f - Mathf.Clamp01(t);
            if (t >= 1f) cover.SetActive(false);
            return;
        }

        ExplorerWorldManager.State state = manager.CurrentState;
        WorldGenerationPrototype gen = manager.Generator;
        int key;        // changes only when the visible text must change (no per-frame string work)
        float progress = -1f;
        if (state == ExplorerWorldManager.State.Failed) key = -3;
        else if (state == ExplorerWorldManager.State.ApplyingSeed) key = -2;
        else if (gen != null && gen.Phase == WorldGenerationPrototype.WorldGenerationPhase.Generating && gen.GenerationProgress < 1f)
        {
            progress = Mathf.Clamp01(gen.GenerationProgress);
            key = Mathf.FloorToInt(progress * 100f);
        }
        else key = -1;

        if (key != lastCoverKey)
        {
            lastCoverKey = key;
            if (key == -3) { coverStatus.text = manager.LastError; coverStatus.color = ErrorColor; coverPercent.text = string.Empty; }
            else if (key == -2)
            {
                coverStatus.color = TextColor;
                coverStatus.text = manager.StartupRequest.SeedStatus == ExplorerSeedUrl.SeedStatus.Valid
                    ? "Applying shared world " + manager.StartupRequest.Seed + "..."
                    : "Arranging a new world...";
                coverPercent.text = string.Empty;
            }
            else if (key >= 0) { coverStatus.color = TextColor; coverStatus.text = "Generating terrain..."; coverPercent.text = key + "%"; }
            else { coverStatus.color = TextColor; coverStatus.text = "Carving routes and placing bases..."; coverPercent.text = string.Empty; }
            coverBarTrack.gameObject.SetActive(key != -3);
        }

        float trackWidth = coverBarTrack.rect.width;
        if (progress >= 0f)
        {
            coverBarFill.anchoredPosition = Vector2.zero;
            coverBarFill.sizeDelta = new Vector2(trackWidth * progress, 3f);
        }
        else
        {
            // Indeterminate: a short segment sweeps across (no value is implied).
            float seg = trackWidth * 0.22f;
            float phase = Mathf.Repeat(Time.unscaledTime * 0.7f, 1f);
            coverBarFill.sizeDelta = new Vector2(seg, 3f);
            coverBarFill.anchoredPosition = new Vector2(Mathf.Lerp(-seg, trackWidth, phase), 0f);
        }
    }

    private void ShowIndicator(string text, bool busy, float seconds)
    {
        indicator.gameObject.SetActive(true);
        indicatorGroup.alpha = 1f;
        indicatorText.text = text;
        indicatorText.color = busy ? TextColor : (text.StartsWith("Generation failed") ? ErrorColor : OkColor);
        indicatorBar.parent.gameObject.SetActive(busy);
        indicatorHideAt = seconds > 0f ? Time.unscaledTime + seconds : -1f;
    }

    private void UpdateIndicator()
    {
        if (indicatorBar.parent.gameObject.activeSelf)
        {
            float w = ((RectTransform)indicatorBar.parent).rect.width;
            float phase = Mathf.Repeat(Time.unscaledTime * 0.9f, 1f);
            indicatorBar.anchoredPosition = new Vector2(Mathf.Lerp(-70f, w, phase), 0f);
        }
        if (indicatorHideAt > 0f)
        {
            float remaining = indicatorHideAt - Time.unscaledTime;
            indicatorGroup.alpha = Mathf.Clamp01(remaining / 0.4f);
            if (remaining <= 0f) { indicator.gameObject.SetActive(false); indicatorHideAt = -1f; }
        }
    }

    // ------------------------------------------------------------------ helpers

    private RectTransform Rect(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return (RectTransform)go.transform;
    }

    /// <summary>A panel with a 1 px steel border and a soft shadow; it blocks clicks to the world.</summary>
    private RectTransform Framed(string name, Transform parent, Color fill)
    {
        RectTransform r = Rect(name, parent, fill);
        r.GetComponent<Image>().raycastTarget = true;
        var outline = r.gameObject.AddComponent<Outline>();
        outline.effectColor = BorderColor;
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = false;
        var shadow = r.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.35f);
        shadow.effectDistance = new Vector2(0f, -3f);
        return r;
    }

    private Text Label(string name, Transform parent, string text, int size, Color color, TextAnchor anchor, FontStyle style)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var label = go.GetComponent<Text>();
        label.font = font;
        label.text = text;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = color;
        label.alignment = anchor;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        return label;
    }

    private Button MakeButton(string name, Transform parent, string text, Color color, bool accentEdge, out Text label)
    {
        RectTransform rect = Rect(name, parent, color);
        rect.GetComponent<Image>().raycastTarget = true;
        var button = rect.gameObject.AddComponent<Button>();
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.22f, 1.22f, 1.22f, 1f);
        colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        colors.selectedColor = Color.white;
        colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.65f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;
        var nav = button.navigation; nav.mode = Navigation.Mode.None; button.navigation = nav;
        if (color.a > 0f)
        {
            var outline = rect.gameObject.AddComponent<Outline>();
            outline.effectColor = accentEdge ? new Color(Accent.r, Accent.g, Accent.b, 0.55f) : BorderColor;
            outline.effectDistance = new Vector2(1f, -1f);
            outline.useGraphicAlpha = false;
        }
        label = Label("Label", rect, text, 11, TextColor, TextAnchor.MiddleCenter, FontStyle.Bold);
        Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
        return button;
    }

    private static void Anchor(RectTransform rect, Vector2 corner, Vector2 position)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = corner;
        rect.anchoredPosition = position;
    }

    /// <summary>Places a child at (x, y) from the parent's top-left corner.</summary>
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }
}
