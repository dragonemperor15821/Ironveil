using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Base Management Menu: the hover information panel shown next to the menu
/// while the pointer is over an option (preview artwork, title, description,
/// requirement rows, footer).
///
/// One shared instance, built in code inside the menu's own canvas
/// (BaseMenuView.Canvas) and drawn above the menu artwork. It never takes
/// pointer input (no raycast targets), so it cannot steal hover from the
/// menu cells or block the camera. Styled to match the artwork: dark steel
/// body inside a thin gold frame.
/// </summary>
public sealed class BaseMenuInfoPanel
{
    /// <summary>What the panel shows.</summary>
    public struct Content
    {
        public string title;
        public Sprite preview;
        public string description;
        public IList<KeyValuePair<string, string>> rows;
        public string footer;
    }

    public const string ObjectName = "Base Menu Info Panel";

    public static readonly Color Gold = new Color(0.80f, 0.65f, 0.32f, 1f);
    public static readonly Color Steel = new Color(0.07f, 0.10f, 0.13f, 0.96f);
    public static readonly Color PreviewWell = new Color(0.145f, 0.21f, 0.25f, 1f);
    public static readonly Color BodyText = new Color(0.86f, 0.88f, 0.90f, 1f);
    public static readonly Color MutedText = new Color(0.62f, 0.66f, 0.70f, 1f);

    private const float FramePixels = 2f;
    private const float GapPixels = 10f;
    private const float MarginPixels = 12f;

    private readonly RectTransform root;
    private readonly VerticalLayoutGroup rootLayout;
    private readonly Text title;
    private readonly RectTransform previewWell;
    private readonly LayoutElement previewElement;
    private readonly Image preview;
    private readonly Text description;
    private readonly RectTransform rowsRoot;
    private readonly Text footer;
    private readonly List<Text[]> rowTexts = new List<Text[]>();
    private readonly Font font;

    public RectTransform Root => root;
    public bool IsShown => root != null && root.gameObject.activeSelf;
    public Text Title => title;
    public Image Preview => preview;
    public Text Description => description;
    public Text Footer => footer;

    /// <summary>Text of requirement row i: [0] label, [1] value.</summary>
    public IReadOnlyList<Text[]> Rows => rowTexts;

    /// <summary>Screen rectangle (bottom-left origin) of the panel after the last Show.</summary>
    public Rect ScreenRect { get; private set; }

    public BaseMenuInfoPanel(Transform canvas)
    {
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        root = NewRect(ObjectName, canvas);
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.zero;
        root.pivot = Vector2.zero;
        Image frame = root.gameObject.AddComponent<Image>();
        frame.color = Gold;
        frame.raycastTarget = false;
        CanvasGroup group = root.gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        rootLayout = root.gameObject.AddComponent<VerticalLayoutGroup>();
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;
        rootLayout.childAlignment = TextAnchor.UpperCenter;
        ContentSizeFitter fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        RectTransform body = NewRect("Body", root);
        body.anchorMin = Vector2.zero;
        body.anchorMax = Vector2.one;
        body.offsetMin = new Vector2(FramePixels, FramePixels);
        body.offsetMax = new Vector2(-FramePixels, -FramePixels);
        body.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        Image bodyImage = body.gameObject.AddComponent<Image>();
        bodyImage.color = Steel;
        bodyImage.raycastTarget = false;

        title = NewText("Title", root, Gold, FontStyle.Bold, TextAnchor.MiddleCenter);

        previewWell = NewRect("Preview Well", root);
        Image wellImage = previewWell.gameObject.AddComponent<Image>();
        wellImage.color = PreviewWell;
        wellImage.raycastTarget = false;
        previewElement = previewWell.gameObject.AddComponent<LayoutElement>();
        RectTransform previewRect = NewRect("Preview", previewWell);
        previewRect.anchorMin = Vector2.zero;
        previewRect.anchorMax = Vector2.one;
        previewRect.offsetMin = new Vector2(4f, 4f);
        previewRect.offsetMax = new Vector2(-4f, -4f);
        preview = previewRect.gameObject.AddComponent<Image>();
        preview.preserveAspect = true;
        preview.raycastTarget = false;

        description = NewText("Description", root, BodyText, FontStyle.Normal, TextAnchor.UpperLeft);

        RectTransform divider = NewRect("Divider", root);
        Image dividerImage = divider.gameObject.AddComponent<Image>();
        dividerImage.color = new Color(Gold.r, Gold.g, Gold.b, 0.6f);
        dividerImage.raycastTarget = false;
        LayoutElement dividerElement = divider.gameObject.AddComponent<LayoutElement>();
        dividerElement.minHeight = 1f;
        dividerElement.preferredHeight = 1f;

        rowsRoot = NewRect("Requirements", root);
        VerticalLayoutGroup rowsLayout = rowsRoot.gameObject.AddComponent<VerticalLayoutGroup>();
        rowsLayout.childControlWidth = true;
        rowsLayout.childControlHeight = true;
        rowsLayout.childForceExpandWidth = true;
        rowsLayout.childForceExpandHeight = false;
        rowsLayout.spacing = 2f;

        footer = NewText("Footer", root, MutedText, FontStyle.Italic, TextAnchor.MiddleLeft);

        root.gameObject.SetActive(false);
    }

    /// <summary>
    /// Fills and shows the panel beside <paramref name="avoid"/> (the menu),
    /// top-aligned with <paramref name="anchor"/> (the hovered cell). Both in
    /// screen pixels, bottom-left origin. <paramref name="width"/> in pixels.
    /// </summary>
    public void Show(Content content, Rect anchor, Rect avoid, Vector2 screen, float width)
    {
        if (root == null)
        {
            return;
        }

        width = Mathf.Round(width);
        int titleSize = Mathf.Max(12, Mathf.RoundToInt(width * 0.058f));
        int bodySize = Mathf.Max(10, Mathf.RoundToInt(width * 0.042f));
        int pad = Mathf.RoundToInt(FramePixels + width * 0.04f);
        rootLayout.padding = new RectOffset(pad, pad, pad, pad);
        rootLayout.spacing = Mathf.Round(width * 0.025f);

        title.fontSize = titleSize;
        title.text = content.title ?? string.Empty;

        bool hasPreview = content.preview != null;
        previewWell.gameObject.SetActive(hasPreview);
        if (hasPreview)
        {
            preview.sprite = content.preview;
            Rect r = content.preview.rect;
            float inner = width - 2f * pad;
            float height = Mathf.Round(Mathf.Min(inner * (r.height / Mathf.Max(1f, r.width)), inner * 0.85f) + 8f);
            previewElement.minHeight = height;
            previewElement.preferredHeight = height;
        }

        description.fontSize = bodySize;
        description.text = content.description ?? string.Empty;
        description.gameObject.SetActive(!string.IsNullOrEmpty(description.text));

        int rowCount = content.rows != null ? content.rows.Count : 0;
        while (rowTexts.Count < rowCount)
        {
            rowTexts.Add(NewRow(rowTexts.Count));
        }

        for (int i = 0; i < rowTexts.Count; i++)
        {
            bool active = i < rowCount;
            rowTexts[i][0].transform.parent.gameObject.SetActive(active);
            if (!active) continue;
            rowTexts[i][0].fontSize = bodySize;
            rowTexts[i][1].fontSize = bodySize;
            rowTexts[i][0].text = content.rows[i].Key;
            rowTexts[i][1].text = content.rows[i].Value;
        }

        rowsRoot.gameObject.SetActive(rowCount > 0);

        footer.fontSize = Mathf.Max(10, bodySize - 1);
        footer.text = content.footer ?? string.Empty;
        footer.gameObject.SetActive(!string.IsNullOrEmpty(footer.text));

        root.sizeDelta = new Vector2(width, root.sizeDelta.y);
        if (!root.gameObject.activeSelf)
        {
            root.gameObject.SetActive(true);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(root);
        Vector2 size = new Vector2(width, Mathf.Ceil(LayoutUtility.GetPreferredHeight(root)));
        root.sizeDelta = size;

        Rect placed = Place(screen, size, anchor, avoid, MarginPixels, GapPixels);
        root.anchoredPosition = placed.position;
        root.SetAsLastSibling();
        ScreenRect = placed;
    }

    public void Hide()
    {
        if (root != null && root.gameObject.activeSelf)
        {
            root.gameObject.SetActive(false);
        }
    }

    public void Destroy()
    {
        if (root == null) return;
        if (Application.isPlaying) Object.Destroy(root.gameObject);
        else Object.DestroyImmediate(root.gameObject);
    }

    /// <summary>
    /// Panel rectangle: beside <paramref name="avoid"/> on the right if it fits,
    /// else on the left, else just right of <paramref name="anchor"/>; top
    /// aligned with the anchor; always clamped inside the screen margin.
    /// </summary>
    public static Rect Place(Vector2 screen, Vector2 size, Rect anchor, Rect avoid, float margin, float gap)
    {
        float x;
        float right = avoid.xMax + gap;
        float left = avoid.xMin - gap - size.x;
        if (right + size.x <= screen.x - margin)
        {
            x = right;
        }
        else if (left >= margin)
        {
            x = left;
        }
        else
        {
            x = anchor.xMax + gap;
        }

        x = Mathf.Clamp(x, margin, Mathf.Max(margin, screen.x - margin - size.x));
        float y = anchor.yMax - size.y;
        y = Mathf.Clamp(y, margin, Mathf.Max(margin, screen.y - margin - size.y));
        return new Rect(Mathf.Round(x), Mathf.Round(y), size.x, size.y);
    }

    // ------------------------------------------------------------- building

    private Text[] NewRow(int index)
    {
        RectTransform row = NewRect($"Requirement {index}", rowsRoot);
        HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.spacing = 8f;

        Text label = NewText("Label", row, MutedText, FontStyle.Normal, TextAnchor.MiddleLeft);
        label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        Text value = NewText("Value", row, BodyText, FontStyle.Bold, TextAnchor.MiddleRight);
        return new[] { label, value };
    }

    private Text NewText(string name, Transform parent, Color color, FontStyle style, TextAnchor alignment)
    {
        RectTransform rect = NewRect(name, parent);
        Text text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.color = color;
        text.fontStyle = style;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = false;
        text.raycastTarget = false;
        return text;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = 5; // UI
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }
}
