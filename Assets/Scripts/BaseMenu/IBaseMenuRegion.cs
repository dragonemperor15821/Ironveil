using UnityEngine;

/// <summary>
/// Base Management Menu (Phase 1C): the menu's interactive screen area.
///
/// The interaction state machine asks this whether the pointer is over the
/// menu. Phase 1D's visual menu implements it (e.g. with
/// RectTransformUtility.RectangleContainsScreenPoint on its panel); until
/// then BaseMenuController uses a placeholder rectangle centred on the
/// selected base's projected anchor.
/// </summary>
public interface IBaseMenuRegion
{
    /// <summary>True when <paramref name="screenPosition"/> (pixels, bottom-left origin) is over the menu.</summary>
    bool ContainsScreenPoint(Vector2 screenPosition);
}
