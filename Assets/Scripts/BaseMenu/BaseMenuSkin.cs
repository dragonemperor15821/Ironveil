using UnityEngine;

/// <summary>
/// Base Management Menu (Phase 1D): the menu's artwork, grid layout and
/// on-screen sizing. The project asset lives at
/// Assets/UI/Resources/IronveilBaseMenuSkin.asset and references
/// Assets/UI/IRONVEIL_BaseMenu.png; BaseMenuView loads it at runtime so no
/// scene needs editing.
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Base Menu Skin", fileName = "IronveilBaseMenuSkin")]
public sealed class BaseMenuSkin : ScriptableObject
{
    public const string ResourcePath = "IronveilBaseMenuSkin";

    [Tooltip("The approved menu artwork (Assets/UI/IRONVEIL_BaseMenu.png), imported as a Sprite.")]
    public Sprite artwork;

    public BaseMenuGridLayout layout = BaseMenuGridLayout.CreateDefault();

    [Header("Size on screen")]
    [Tooltip("Panel height as a fraction of the screen height.")]
    [Range(0.2f, 1f)] public float screenHeightFraction = 0.5f;

    [Tooltip("The panel never exceeds this fraction of the screen width or height (aspect kept, never cropped).")]
    [Range(0.2f, 1f)] public float maxScreenFraction = 0.94f;

    [Tooltip("Smallest panel height in pixels, unless the screen itself is too small to fit it.")]
    [Min(0f)] public float minHeightPixels = 360f;

    [Tooltip("Gap kept between the panel and the screen edges when the base is near an edge.")]
    [Min(0f)] public float screenMarginPixels = 12f;

    [Header("Canvas")]
    [Tooltip("Overlay canvas sorting order. Below the World Explorer UI (100) so its controls stay on top.")]
    public int sortingOrder = 90;
}
