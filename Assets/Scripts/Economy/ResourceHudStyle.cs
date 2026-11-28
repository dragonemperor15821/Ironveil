using UnityEngine;

namespace Ironveil.Economy
{
    /// <summary>
    /// Palette for the resource HUD: metallic slate blue-grey that matches the Base Management Menu's cell slate and
    /// gunmetal frame, with icy blue indicators. Kept separate so a later skin asset can replace it.
    /// </summary>
    public static class ResourceHudStyle
    {
        // Metallic background gradient (top to bottom).
        public static readonly Color BackgroundTop = new Color(0.200f, 0.250f, 0.310f, 0.96f);
        public static readonly Color BackgroundMid = new Color(0.125f, 0.160f, 0.205f, 0.96f);
        public static readonly Color BackgroundBottom = new Color(0.075f, 0.095f, 0.130f, 0.96f);

        public static readonly Color StrategicTint = new Color(0.46f, 0.62f, 0.86f, 0.07f);
        public static readonly Color Highlight = new Color(0.72f, 0.84f, 0.95f, 0.34f);
        public static readonly Color BottomEdge = new Color(0.46f, 0.77f, 0.96f, 0.72f);
        public static readonly Color BottomShadow = new Color(0.02f, 0.03f, 0.05f, 0.80f);
        public static readonly Color Separator = new Color(0.42f, 0.52f, 0.62f, 0.42f);
        public static readonly Color SeparatorShadow = new Color(0.02f, 0.03f, 0.05f, 0.55f);
        public static readonly Color GroupDivider = new Color(0.50f, 0.79f, 0.97f, 0.62f);
        public static readonly Color Border = new Color(0.44f, 0.58f, 0.72f, 0.60f);

        public static readonly Color Label = new Color(0.63f, 0.76f, 0.87f, 1f);
        public static readonly Color Value = new Color(0.94f, 0.97f, 1.00f, 1f);
        public static readonly Color Muted = new Color(0.53f, 0.61f, 0.70f, 1f);
        public static readonly Color Unavailable = new Color(0.47f, 0.54f, 0.62f, 1f);
        public static readonly Color TextShadow = new Color(0f, 0f, 0f, 0.55f);

        public static readonly Color Icy = new Color(0.52f, 0.84f, 1.00f, 1f);
        public static readonly Color IcyFull = new Color(0.82f, 0.96f, 1.00f, 1f);
        public static readonly Color Steel = new Color(0.62f, 0.70f, 0.79f, 1f);
        public static readonly Color Track = new Color(0.035f, 0.050f, 0.075f, 0.92f);
        public static readonly Color TrackEdge = new Color(0.36f, 0.46f, 0.56f, 0.50f);
        public static readonly Color IconHollow = new Color(0.10f, 0.13f, 0.17f, 1f);
    }
}
