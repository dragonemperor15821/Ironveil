using System;
using UnityEngine;

/// <summary>Vehicle category of a Base Management Menu row (None for construction rows).</summary>
public enum BaseMenuCategory
{
    None = -1,
    Light = 0,
    Medium = 1,
    Heavy = 2,
    Artillery = 3,
    TankDestroyer = 4,
    Special = 5
}

/// <summary>What a menu row holds.</summary>
public enum BaseMenuRowKind
{
    /// <summary>Building slots across the full width (top rows of the artwork).</summary>
    Construction,
    /// <summary>A category folder followed by vehicle slots.</summary>
    Category
}

/// <summary>What a single interactive area is.</summary>
public enum BaseMenuCellKind
{
    /// <summary>A slot that may later hold a building or vehicle icon.</summary>
    Slot,
    /// <summary>The category folder and label painted at the start of a category row.</summary>
    CategoryHeader
}

/// <summary>A 1D extent in artwork pixels.</summary>
[Serializable]
public struct BaseMenuSpan
{
    public float min;
    public float max;

    public BaseMenuSpan(float min, float max)
    {
        this.min = min;
        this.max = max;
    }

    public float Size => max - min;
}

/// <summary>One row of the menu grid.</summary>
[Serializable]
public sealed class BaseMenuRowDefinition
{
    public string label = string.Empty;
    public BaseMenuRowKind kind = BaseMenuRowKind.Construction;
    public BaseMenuCategory category = BaseMenuCategory.None;

    [Tooltip("Vertical extent in artwork pixels, measured from the TOP of the image.")]
    public BaseMenuSpan vertical;

    [Tooltip("First column index holding slots. Columns before it form the category header (Category rows only).")]
    [Min(0)] public int firstSlotColumn;

    public BaseMenuRowDefinition() { }

    public BaseMenuRowDefinition(string label, BaseMenuRowKind kind, BaseMenuCategory category,
        float top, float bottom, int firstSlotColumn)
    {
        this.label = label;
        this.kind = kind;
        this.category = category;
        vertical = new BaseMenuSpan(top, bottom);
        this.firstSlotColumn = firstSlotColumn;
    }

    public bool HasHeader => kind == BaseMenuRowKind.Category && firstSlotColumn > 0;
}

/// <summary>
/// Base Management Menu (Phase 1D): where the interactive areas sit on the
/// supplied artwork (Assets/UI/IRONVEIL_BaseMenu.png).
///
/// Columns and rows are measured in artwork pixels (origin top-left, as in an
/// image editor) and converted to normalised anchors, so the transparent hit
/// areas stay locked to the painted cells at any panel size. Rows and
/// columns are plain arrays and can be edited in the skin asset; the default
/// matches the 1671x941 artwork: two construction rows of 10 slots, then six
/// category rows (folder header spanning the first two columns, 8 slots).
/// </summary>
[Serializable]
public sealed class BaseMenuGridLayout
{
    [Tooltip("Pixel size of the artwork the spans below were measured on.")]
    public Vector2 artworkSize = new Vector2(1671f, 941f);

    [Tooltip("Horizontal extent of each column, in artwork pixels from the left.")]
    public BaseMenuSpan[] columns = DefaultColumns();

    public BaseMenuRowDefinition[] rows = DefaultRows();

    public int ColumnCount => columns != null ? columns.Length : 0;
    public int RowCount => rows != null ? rows.Length : 0;

    /// <summary>The layout measured from IRONVEIL_BaseMenu.png (separator-to-separator cell interiors).</summary>
    public static BaseMenuGridLayout CreateDefault()
    {
        return new BaseMenuGridLayout();
    }

    private static BaseMenuSpan[] DefaultColumns()
    {
        return new[]
        {
            new BaseMenuSpan(92f, 225f),
            new BaseMenuSpan(232f, 372f),
            new BaseMenuSpan(379f, 529f),
            new BaseMenuSpan(535f, 682f),
            new BaseMenuSpan(688f, 833f),
            new BaseMenuSpan(839f, 985f),
            new BaseMenuSpan(992f, 1136f),
            new BaseMenuSpan(1143f, 1288f),
            new BaseMenuSpan(1294f, 1435f),
            new BaseMenuSpan(1442f, 1582f)
        };
    }

    private static BaseMenuRowDefinition[] DefaultRows()
    {
        const BaseMenuRowKind C = BaseMenuRowKind.Construction;
        const BaseMenuRowKind V = BaseMenuRowKind.Category;
        return new[]
        {
            new BaseMenuRowDefinition("Construction 1", C, BaseMenuCategory.None, 170f, 261f, 0),
            new BaseMenuRowDefinition("Construction 2", C, BaseMenuCategory.None, 267f, 346f, 0),
            new BaseMenuRowDefinition("Light", V, BaseMenuCategory.Light, 350f, 425f, 2),
            new BaseMenuRowDefinition("Medium", V, BaseMenuCategory.Medium, 430f, 502f, 2),
            new BaseMenuRowDefinition("Heavy", V, BaseMenuCategory.Heavy, 507f, 581f, 2),
            new BaseMenuRowDefinition("Artillery", V, BaseMenuCategory.Artillery, 586f, 659f, 2),
            new BaseMenuRowDefinition("Tank Destroyer", V, BaseMenuCategory.TankDestroyer, 664f, 740f, 2),
            new BaseMenuRowDefinition("Special", V, BaseMenuCategory.Special, 745f, 829f, 2)
        };
    }

    public float Aspect => artworkSize.y > 0f ? artworkSize.x / artworkSize.y : 1f;

    /// <summary>Slot rectangle in artwork pixels (origin top-left).</summary>
    public Rect GetSlotPixelRect(int row, int column)
    {
        BaseMenuSpan h = columns[column];
        BaseMenuSpan v = rows[row].vertical;
        return Rect.MinMaxRect(h.min, v.min, h.max, v.max);
    }

    /// <summary>Category header rectangle in artwork pixels: columns [0, firstSlotColumn).</summary>
    public Rect GetHeaderPixelRect(int row)
    {
        BaseMenuRowDefinition r = rows[row];
        BaseMenuSpan v = r.vertical;
        return Rect.MinMaxRect(columns[0].min, v.min, columns[r.firstSlotColumn - 1].max, v.max);
    }

    /// <summary>
    /// Converts an artwork pixel rect (top-left origin) into normalised RectTransform
    /// anchors (bottom-left origin, 0..1 of the panel).
    /// </summary>
    public void ToAnchors(Rect pixelRect, out Vector2 anchorMin, out Vector2 anchorMax)
    {
        float w = Mathf.Max(1f, artworkSize.x);
        float h = Mathf.Max(1f, artworkSize.y);
        anchorMin = new Vector2(pixelRect.xMin / w, 1f - pixelRect.yMax / h);
        anchorMax = new Vector2(pixelRect.xMax / w, 1f - pixelRect.yMin / h);
    }

    /// <summary>Number of slot cells (headers excluded).</summary>
    public int CountSlots()
    {
        int n = 0;
        for (int r = 0; r < RowCount; r++)
        {
            n += Mathf.Max(0, ColumnCount - rows[r].firstSlotColumn);
        }

        return n;
    }

    /// <summary>Rejects layouts that would place areas outside the artwork, overlap, or reference missing columns.</summary>
    public bool Validate(out string error)
    {
        error = null;
        if (artworkSize.x <= 0f || artworkSize.y <= 0f) { error = "artworkSize must be positive"; return false; }
        if (ColumnCount == 0) { error = "no columns"; return false; }
        if (RowCount == 0) { error = "no rows"; return false; }

        for (int c = 0; c < ColumnCount; c++)
        {
            BaseMenuSpan s = columns[c];
            if (s.min < 0f || s.max > artworkSize.x || s.Size <= 0f) { error = $"column {c} span invalid"; return false; }
            if (c > 0 && s.min < columns[c - 1].max) { error = $"column {c} overlaps column {c - 1}"; return false; }
        }

        for (int r = 0; r < RowCount; r++)
        {
            BaseMenuRowDefinition row = rows[r];
            if (row == null) { error = $"row {r} missing"; return false; }
            BaseMenuSpan s = row.vertical;
            if (s.min < 0f || s.max > artworkSize.y || s.Size <= 0f) { error = $"row {r} span invalid"; return false; }
            if (r > 0 && rows[r - 1] != null && s.min < rows[r - 1].vertical.max) { error = $"row {r} overlaps row {r - 1}"; return false; }
            if (row.firstSlotColumn < 0 || row.firstSlotColumn > ColumnCount) { error = $"row {r} firstSlotColumn out of range"; return false; }
            if (row.kind == BaseMenuRowKind.Category && row.category == BaseMenuCategory.None) { error = $"row {r} is a Category row without a category"; return false; }
        }

        return true;
    }
}
