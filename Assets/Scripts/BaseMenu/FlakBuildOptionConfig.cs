using System;
using UnityEngine;

/// <summary>Resources needed to build one Flak (display only; nothing is deducted yet).</summary>
[Serializable]
public struct FlakBuildCost
{
    [Tooltip("The turret number this price applies to (2..28).")]
    public int turretNumber;
    public int concrete;
    public int iron;
    public int aluminium;
    [Tooltip("Construction duration in seconds.")]
    public int durationSeconds;
}

/// <summary>
/// Base Management Menu: data for the "Build Additional Flaks" option
/// (Row 1, Box 2). The asset lives at
/// Assets/UI/Resources/IronveilFlakBuildOption.asset and references the
/// official artwork Assets/IRONVEIL/UI/Flak/Level 1 Base Flak.png.
///
/// One pricing table is shared by all nine base types. The default prices
/// are PLACEHOLDERS until the real values are supplied; edit them in the
/// asset (the Costs list) without touching code.
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

    [Header("Hover text")]
    [TextArea(2, 5)]
    public string description =
        "Construct an additional Base Flak turret to reinforce this base's defences. " +
        "Destroyed turrets can be rebuilt; the base falls when its last turret is lost.";

    [Header("Pricing (shared by all base types) - PLACEHOLDER values")]
    public FlakBuildCost[] costs = CreatePlaceholderCosts();

    /// <summary>Price of building turret <paramref name="turretNumber"/>; false when the table has no entry.</summary>
    public bool TryGetCost(int turretNumber, out FlakBuildCost cost)
    {
        if (costs != null)
        {
            for (int i = 0; i < costs.Length; i++)
            {
                if (costs[i].turretNumber == turretNumber)
                {
                    cost = costs[i];
                    return true;
                }
            }
        }

        cost = default;
        return false;
    }

    /// <summary>Placeholder table for turrets 2..28 (rising linearly). Replace with the real prices.</summary>
    public static FlakBuildCost[] CreatePlaceholderCosts()
    {
        FlakBuildCost[] table = new FlakBuildCost[BaseFlakRoster.MaxFlaks - 1];
        for (int i = 0; i < table.Length; i++)
        {
            int n = i + 2;
            int step = n - 2;
            table[i] = new FlakBuildCost
            {
                turretNumber = n,
                concrete = 400 + 100 * step,
                iron = 250 + 75 * step,
                aluminium = 100 + 50 * step,
                durationSeconds = 60 + 15 * step
            };
        }

        return table;
    }

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
}
