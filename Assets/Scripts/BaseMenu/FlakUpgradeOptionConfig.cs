using System;
using UnityEngine;

/// <summary>
/// Base Management Menu: data for the "Upgrade Flaks" option (Row 1, Box 1).
/// The asset lives at Assets/UI/Resources/IronveilFlakUpgradeOption.asset.
///
/// Holds presentation only: the cell, the artwork of each Flak level and the
/// hover description. Prices, the maximum level and upgrade availability are
/// NOT stored here; they come from FlakUpgradeRules through
/// BaseConstruction.EvaluateFlakUpgrade. Independent of Box 2
/// (FlakBuildOptionConfig).
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Flak Upgrade Option", fileName = "IronveilFlakUpgradeOption")]
public sealed class FlakUpgradeOptionConfig : ScriptableObject
{
    public const string ResourcePath = "IronveilFlakUpgradeOption";

    [Serializable]
    public struct LevelArtwork
    {
        [Min(1)] public int level;
        [Tooltip("Menu image of this Flak level (rendered from the real model, 357 x 281, like Level 1 Base Flak.png).")]
        public Sprite sprite;
    }

    [Tooltip("Artwork per Flak level. The cell shows the NEXT level (L2 for an L1 base, L3 for an L2 base, L4 for an L3 base, L4 at the maximum).")]
    public LevelArtwork[] artwork = new LevelArtwork[0];

    [Tooltip("Badge shown on the cell when the base's Flaks are at the maximum level.")]
    public string maxLevelLabel = "MAX LEVEL";

    [Header("Menu cell (0-based)")]
    [Tooltip("Grid row of the option. 0 = Row 1 (Construction 1).")]
    [Min(0)] public int row = 0;
    [Tooltip("Grid column of the option. 0 = Box 1 (Box 2 is Build Additional Flaks).")]
    [Min(0)] public int column = 0;
    [Tooltip("Gap between the artwork and the painted cell edge, as a fraction of the cell height.")]
    [Range(0f, 0.3f)] public float cellPadding = 0.08f;

    [Header("Click")]
    [Tooltip("After a purchase, further clicks on the same base are ignored for this long (unscaled seconds), so a double click never buys two levels.")]
    [Min(0f)] public float clickGuardSeconds = 0.35f;

    [Header("Hover text")]
    [TextArea(2, 5)]
    public string description =
        "Upgrade every Flak turret on this base to the next level in one purchase. " +
        "Turrets built later on this base are constructed at its current level.";

    /// <summary>Artwork registered for an exact Flak level, or null.</summary>
    public Sprite ArtworkForLevel(int level)
    {
        if (artwork == null) return null;
        for (int i = 0; i < artwork.Length; i++)
        {
            if (artwork[i].level == level && artwork[i].sprite != null) return artwork[i].sprite;
        }

        return null;
    }

    /// <summary>The level the cell shows for a base at <paramref name="currentLevel"/>: the next level, or the maximum.</summary>
    public static int DisplayedLevel(int currentLevel)
    {
        int next = FlakUpgradeRules.NextLevel(FlakUpgradeRules.Sanitize(currentLevel));
        return next != 0 ? next : FlakUpgradeRules.MaxLevel;
    }

    /// <summary>The cell artwork for a base at <paramref name="currentLevel"/> (L1 -> L2 image, L2 -> L3, L3 -> L4, L4 -> L4).</summary>
    public Sprite ArtworkForCurrentLevel(int currentLevel) => ArtworkForLevel(DisplayedLevel(currentLevel));
}
