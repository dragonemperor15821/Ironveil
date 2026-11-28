using UnityEngine;

namespace Ironveil.Economy
{
    /// <summary>Where the HUD bar sits.</summary>
    public enum ResourceHudPlacementMode
    {
        /// <summary>Edge-to-edge strip along the top of the screen (MainBuild, or any scene without the Explorer UI).</summary>
        FullWidthTop,
        /// <summary>World Explorer: framed bar to the right of the top-left panel and below the top-centre indicator.</summary>
        BesideExplorerPanel,
        /// <summary>World Explorer on narrow screens: framed full-width bar directly below the top-left panel.</summary>
        BelowExplorerPanel,
    }

    /// <summary>Result of <see cref="ResourceHudLayout.Place"/>. Rects are in canvas units with a TOP-LEFT origin.</summary>
    public struct ResourceHudPlacement
    {
        public ResourceHudPlacementMode mode;
        public Rect rect;
        /// <summary>Content scale relative to the 1,316-unit design width, clamped to [MinFit, MaxFit].</summary>
        public float fit;
        /// <summary>True when the available width is below the legible minimum, so text may be tight.</summary>
        public bool belowMinimum;
        public bool Framed => mode != ResourceHudPlacementMode.FullWidthTop;
    }

    /// <summary>Font sizes and row heights for one fit value. All integers, so text stays pixel-crisp.</summary>
    public struct ResourceHudMetrics
    {
        public float fit;
        public int labelFont, valueFont, captionFont;
        public int pad, topPad, labelRow, valueRow, gap, fillHeight, captionRow, icon;
        public int Height => topPad + labelRow + valueRow + gap + fillHeight + gap + captionRow + topPad;
    }

    /// <summary>
    /// Pure layout maths for the resource HUD, separated from Unity objects so it can be unit-tested at any screen size.
    ///
    /// Width is shared between the eight items by weight (primary items carry more text). Fonts scale with the
    /// available width but never drop below a legible minimum; no item is ever hidden.
    /// </summary>
    public static class ResourceHudLayout
    {
        public const float PrimaryWeight = 184f;
        public const float StrategicWeight = 140f;
        public const float EndPadding = 10f;
        /// <summary>4 x 184 + 4 x 140 + 2 x 10.</summary>
        public const float DesignWidth = 4 * PrimaryWeight + 4 * StrategicWeight + 2 * EndPadding;

        public const float MinFit = 0.70f;
        public const float MaxFit = 1.15f;

        /// <summary>Gap kept between the HUD and the Explorer panel / indicator (canvas units).</summary>
        public const float ExplorerGap = 8f;

        public static float Weight(ResourceType resource) =>
            ResourceHudFormat.IsPrimary(resource) ? PrimaryWeight : StrategicWeight;

        public static ResourceHudMetrics Metrics(float fit)
        {
            var m = new ResourceHudMetrics { fit = fit };
            m.labelFont = Mathf.Max(9, Mathf.RoundToInt(10f * fit));
            m.valueFont = Mathf.Max(12, Mathf.RoundToInt(16f * fit));
            m.captionFont = Mathf.Max(9, Mathf.RoundToInt(10f * fit));
            m.pad = Mathf.Max(6, Mathf.RoundToInt(9f * fit));
            m.topPad = Mathf.Max(4, Mathf.RoundToInt(6f * fit));
            m.labelRow = m.labelFont + 2;
            m.valueRow = m.valueFont + 2;
            m.gap = Mathf.Max(2, Mathf.RoundToInt(3f * fit));
            m.fillHeight = Mathf.Max(3, Mathf.RoundToInt(4f * fit));
            m.captionRow = m.captionFont + 2;
            m.icon = Mathf.Max(6, Mathf.RoundToInt(8f * fit));
            return m;
        }

        public static float FitForWidth(float width, out bool belowMinimum)
        {
            float raw = width / DesignWidth;
            belowMinimum = raw < MinFit - 1e-4f;
            return Mathf.Clamp(raw, MinFit, MaxFit);
        }

        /// <summary>
        /// Chooses the bar rectangle.
        /// </summary>
        /// <param name="viewport">Canvas size in units (screen pixels divided by the DPI scale).</param>
        /// <param name="explorerPanel">Explorer top-left panel, top-left origin, or null outside World Explorer.</param>
        /// <param name="explorerIndicator">Explorer top-centre indicator, top-left origin, or null.</param>
        public static ResourceHudPlacement Place(Vector2 viewport, Rect? explorerPanel, Rect? explorerIndicator)
        {
            var p = new ResourceHudPlacement();

            if (!explorerPanel.HasValue)
            {
                p.mode = ResourceHudPlacementMode.FullWidthTop;
                p.fit = FitForWidth(viewport.x, out p.belowMinimum);
                p.rect = new Rect(0f, 0f, viewport.x, Metrics(p.fit).Height);
                return p;
            }

            Rect panel = explorerPanel.Value;
            float margin = Mathf.Max(0f, panel.xMin); // mirror the Explorer's own screen margin on the right

            // Beside the panel, below the indicator (the indicator is centred, so a bar spanning from the panel to the
            // right edge always crosses its column).
            float top = explorerIndicator.HasValue ? explorerIndicator.Value.yMax + ExplorerGap : panel.yMin;
            float left = panel.xMax + ExplorerGap;
            float width = viewport.x - margin - left;
            float fit = FitForWidth(width, out bool tooNarrow);
            if (!tooNarrow)
            {
                p.mode = ResourceHudPlacementMode.BesideExplorerPanel;
                p.fit = fit;
                p.rect = new Rect(left, top, width, Metrics(fit).Height);
                return p;
            }

            // Narrow screens: keep all eight items in one bar, full width, directly below the panel.
            p.mode = ResourceHudPlacementMode.BelowExplorerPanel;
            width = viewport.x - 2f * margin;
            p.fit = FitForWidth(width, out p.belowMinimum);
            p.rect = new Rect(margin, panel.yMax + ExplorerGap, width, Metrics(p.fit).Height);
            return p;
        }
    }
}
