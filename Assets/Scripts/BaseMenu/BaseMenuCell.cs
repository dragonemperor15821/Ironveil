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
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("")]
public sealed class BaseMenuCell : MonoBehaviour, IPointerClickHandler
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
