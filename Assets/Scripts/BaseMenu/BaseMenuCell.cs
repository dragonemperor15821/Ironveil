using System;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Base Management Menu (Phase 1D): one interactive area of the menu grid.
///
/// Sits, invisible, over a painted cell of the artwork. Future building or
/// vehicle icons go under <see cref="Content"/> as separate UI elements; the
/// cell is empty while Content has no children. A left click raises
/// <see cref="Clicked"/> (never after a drag); nothing listens for gameplay
/// yet, so clicking an empty cell does nothing.
///
/// Pointer enter / exit raise <see cref="HoverChanged"/> so menu options can
/// show hover information (also on disable, when the panel hides under the
/// pointer).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("")]
public sealed class BaseMenuCell : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
{
    /// <summary>Same limit as BasePointerGesture / the Explorer camera pan threshold.</summary>
    public const float MaxClickTravelPixels = 4f;

    [SerializeField] private int row;
    [SerializeField] private int column;
    [SerializeField] private BaseMenuCellKind kind;
    [SerializeField] private BaseMenuCategory category = BaseMenuCategory.None;
    [SerializeField] private BaseMenuRowKind rowKind;
    [SerializeField] private string rowLabel = string.Empty;
    [SerializeField] private RectTransform content;

    /// <summary>Raised on an intentional left click (press and release within MaxClickTravelPixels).</summary>
    public event Action<BaseMenuCell> Clicked;

    /// <summary>Raised when the pointer enters (true) or leaves (false) the cell.</summary>
    public event Action<BaseMenuCell, bool> HoverChanged;

    /// <summary>True while the pointer is over the cell.</summary>
    public bool IsHovered { get; private set; }

    public int Row => row;
    /// <summary>Grid column (for a header: 0, the first column it covers).</summary>
    public int Column => column;
    public BaseMenuCellKind Kind => kind;
    public BaseMenuCategory Category => category;
    public BaseMenuRowKind RowKind => rowKind;
    public string RowLabel => rowLabel;
    public RectTransform RectTransform => (RectTransform)transform;

    /// <summary>Parent for future icon / content elements (stretched over the cell).</summary>
    public RectTransform Content => content;

    /// <summary>True while no content element has been placed in the cell.</summary>
    public bool IsEmpty => content == null || content.childCount == 0;

    public void Configure(int rowIndex, int columnIndex, BaseMenuCellKind cellKind, BaseMenuRowDefinition definition, RectTransform contentRoot)
    {
        row = rowIndex;
        column = columnIndex;
        kind = cellKind;
        rowKind = definition.kind;
        category = definition.category;
        rowLabel = definition.label;
        content = contentRoot;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left)
        {
            return;
        }

        if ((eventData.position - eventData.pressPosition).sqrMagnitude > MaxClickTravelPixels * MaxClickTravelPixels)
        {
            return;
        }

        Click();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        SetHovered(true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        SetHovered(false);
    }

    private void OnDisable()
    {
        SetHovered(false); // the panel hid under the pointer: no exit event comes
    }

    /// <summary>Sets the hover state and raises HoverChanged on a change (also used by tests).</summary>
    public void SetHovered(bool hovered)
    {
        if (IsHovered == hovered)
        {
            return;
        }

        IsHovered = hovered;
        Action<BaseMenuCell, bool> handlers = HoverChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<BaseMenuCell, bool> handler in handlers.GetInvocationList())
        {
            try { handler(this, hovered); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    /// <summary>Raises Clicked (also used by tests). Handler exceptions are logged, not propagated.</summary>
    public void Click()
    {
        Action<BaseMenuCell> handlers = Clicked;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<BaseMenuCell> handler in handlers.GetInvocationList())
        {
            try { handler(this); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
    }

    public override string ToString()
    {
        return kind == BaseMenuCellKind.CategoryHeader
            ? $"Header r{row} '{rowLabel}'"
            : $"Slot r{row} c{column} ({rowLabel})";
    }
}
