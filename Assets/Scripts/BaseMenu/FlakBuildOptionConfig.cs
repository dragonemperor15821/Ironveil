using UnityEngine;

/// <summary>
/// Base Management Menu: data for the "Build Additional Flaks" option
/// (Row 1, Box 2). The asset lives at
/// Assets/UI/Resources/IronveilFlakBuildOption.asset and references the
/// official artwork Assets/IRONVEIL/UI/Flak/Level 1 Base Flak.png.
///
/// Presentation only. Construction prices, build times and the 28-Flak
/// capacity come from FlakConstructionRules through
/// BaseConstruction.EvaluateFlakConstruction (the Version 1 schedule shared by
/// all nine base types); nothing about pricing is stored here.
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Flak Build Option", fileName = "IronveilFlakBuildOption")]
public sealed class FlakBuildOptionConfig : ScriptableObject
{
    public const string ResourcePath = "IronveilFlakBuildOption";

    [Tooltip("Official Level 1 Base Flak artwork, shown unmodified in the cell and in the hover panel.")]
    public Sprite artwork;

    [Header("Menu cell (0-based)")]
    [Tooltip("Grid row of the option. 0 = Row 1 (Construction 1).")]
    [Min(0)] public int row = 0;
    [Tooltip("Grid column of the option. 1 = Box 2 (Box 1 is reserved for Upgrade Flaks).")]
    [Min(0)] public int column = 1;
    [Tooltip("Gap between the artwork and the painted cell edge, as a fraction of the cell height.")]
    [Range(0f, 0.3f)] public float cellPadding = 0.08f;

    [Header("Click")]
    [Tooltip("After a construction is started, further clicks on the same base are ignored for this long (unscaled seconds), so a double click never starts two.")]
    [Min(0f)] public float clickGuardSeconds = 0.35f;

    [Header("Hover text")]
    [TextArea(2, 5)]
    public string description =
        "Construct an additional Base Flak turret to reinforce this base's defences. " +
        "Destroyed turrets can be rebuilt; the base falls when its last turret is lost.";

    /// <summary>"45s", "2m 30s", "1h 05m".</summary>
    public static string FormatDuration(int seconds)
    {
        seconds = Mathf.Max(0, seconds);
        if (seconds < 60) return seconds + "s";
        if (seconds < 3600)
        {
            int s = seconds % 60;
            return s == 0 ? $"{seconds / 60}m" : $"{seconds / 60}m {s:00}s";
        }

        return $"{seconds / 3600}h {(seconds % 3600) / 60:00}m";
    }

    /// <summary>Remaining time, rounded UP to whole seconds (a countdown never shows 0 before it finishes).</summary>
    public static string FormatRemaining(double seconds) => FormatDuration((int)System.Math.Ceiling(System.Math.Max(0.0, seconds)));
}
