using System;
using UnityEngine;
using UnityEngine.UI;

namespace Ironveil.Economy
{
    /// <summary>
    /// One of the eight HUD items. Plain C# view built and owned by <see cref="ResourceHud"/>; it holds no balances.
    /// Every value it shows is pushed in from the live ledger by <see cref="ResourceHud"/>.
    ///
    ///   [icon] LABEL ................ ×cycles
    ///   VALUE ................. / CAPACITY
    ///   [=========== storage fill ======   ]
    ///   CAPTION ................... STATUS
    /// </summary>
    public sealed class ResourceHudItem
    {
        public ResourceType Resource { get; }
        public bool IsPrimary { get; }
        public RectTransform Root { get; }

        public Text Label { get; }
        public Text Cycles { get; }
        public Text Value { get; }
        public Text Capacity { get; }
        public Text Caption { get; }
        public Text Status { get; }

        private readonly RectTransform icon;
        private readonly RectTransform iconInner;
        private readonly RectTransform track;
        private readonly RectTransform fill;
        private readonly Image fillImage;

        // Last pushed values, so unchanged frames rebuild no text meshes.
        private int lastTenths = -1;
        private int lastCycles = -1;

        /// <summary>Storage fill fraction in [0, 1]; meaningful only while <see cref="FillVisible"/>.</summary>
        public float FillFraction { get; private set; }
        public bool FillVisible => track.gameObject.activeSelf;
        public bool IsFull { get; private set; }

        internal ResourceHudItem(ResourceType resource, Transform parent, Font font)
        {
            Resource = resource;
            IsPrimary = ResourceHudFormat.IsPrimary(resource);

            Root = NewRect("Item " + resource, parent);

            // Primary: filled icy diamond. Strategic: hollow steel diamond.
            icon = NewRect("Icon", Root);
            icon.localRotation = Quaternion.Euler(0f, 0f, 45f);
            AddImage(icon, IsPrimary ? ResourceHudStyle.Icy : ResourceHudStyle.Steel);
            iconInner = NewRect("Inner", icon);
            iconInner.anchorMin = Vector2.zero; iconInner.anchorMax = Vector2.one;
            AddImage(iconInner, ResourceHudStyle.IconHollow);
            iconInner.gameObject.SetActive(!IsPrimary);

            Label = NewText("Label", Root, font, FontStyle.Bold, TextAnchor.MiddleLeft, ResourceHudStyle.Label, false);
            Label.text = ResourceHudFormat.DisplayName(resource);
            Cycles = NewText("Cycles", Root, font, FontStyle.Bold, TextAnchor.MiddleRight, ResourceHudStyle.Icy, false);
            Value = NewText("Value", Root, font, FontStyle.Bold, TextAnchor.MiddleLeft, ResourceHudStyle.Value, true);
            Capacity = NewText("Capacity", Root, font, FontStyle.Normal, TextAnchor.MiddleRight, ResourceHudStyle.Muted, false);

            track = NewRect("Storage Track", Root);
            AddImage(track, ResourceHudStyle.Track);
            Outline edge = track.gameObject.AddComponent<Outline>();
            edge.effectColor = ResourceHudStyle.TrackEdge;
            edge.effectDistance = new Vector2(1f, -1f);
            edge.useGraphicAlpha = false;
            fill = NewRect("Fill", track);
            fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f);
            fill.offsetMin = fill.offsetMax = Vector2.zero;
            fillImage = AddImage(fill, ResourceHudStyle.Icy);

            Caption = NewText("Caption", Root, font, FontStyle.Normal, TextAnchor.MiddleLeft, ResourceHudStyle.Muted, false);
            Status = NewText("Status", Root, font, FontStyle.Bold, TextAnchor.MiddleRight, ResourceHudStyle.Icy, false);
        }

        // ------------------------------------------------------------ content

        /// <summary>Not configured in the ledger (Group Limit today): an em dash and no storage indicator.</summary>
        internal void ShowUnconfigured()
        {
            SetText(Value, ResourceHudFormat.Unavailable);
            Value.color = ResourceHudStyle.Unavailable;
            SetText(Capacity, string.Empty);
            SetFillVisible(false);
            SetText(Caption, "NOT CONFIGURED");
            SetText(Status, string.Empty);
            SetCycles(0);
            lastTenths = -1;
            IsFull = false;
        }

        /// <summary>Shows a live balance. A null capacity (undecided) shows no capacity and no storage indicator.</summary>
        internal void ShowBalance(long balance, long? capacity)
        {
            Value.color = ResourceHudStyle.Value;
            if (capacity.HasValue)
            {
                long cap = capacity.Value;
                // Stage 8: after a storage decrease the balance may exceed the limit (excess preserved). Show the real
                // balance; the bar is clamped full.
                long shown = balance;
                SetText(Value, ResourceHudFormat.Amount(shown));
                SetText(Capacity, ResourceHudFormat.Capacity(cap));
                IsFull = cap > 0 && shown >= cap;
                FillFraction = cap > 0 ? Mathf.Clamp01((float)((double)shown / cap)) : 0f;
                SetFillVisible(true);
                fill.anchorMax = new Vector2(FillFraction, 1f);
                fillImage.color = IsFull ? ResourceHudStyle.IcyFull : ResourceHudStyle.Icy;
            }
            else
            {
                SetText(Value, ResourceHudFormat.Amount(balance));
                SetText(Capacity, string.Empty);
                SetFillVisible(false);
                IsFull = false;
            }
        }

        /// <summary>Caption for resources that do not replenish.</summary>
        internal void ShowStaticCaption(bool hasCapacity)
        {
            SetText(Caption, hasCapacity ? "NO REFILL" : "CAPACITY " + ResourceHudFormat.Unavailable);
            SetText(Status, string.Empty);
            SetCycles(0);
            lastTenths = -1;
        }

        /// <summary>Replenishment rate caption, "+30,000 / 5s".</summary>
        internal void ShowReplenishment(long amount, TimeSpan interval)
        {
            SetText(Caption, ResourceHudFormat.Replenishment(amount, interval));
        }

        /// <summary>Countdown to the next scheduled tick and the active cycle count; FULL or IDLE when no cycle runs.</summary>
        internal void ShowCycleState(int activeCycles, bool hasNextTick, TimeSpan untilNextTick)
        {
            SetCycles(activeCycles);
            if (hasNextTick)
            {
                int tenths = ResourceHudFormat.CountdownTenths(untilNextTick);
                if (tenths != lastTenths)
                {
                    lastTenths = tenths;
                    Status.text = ResourceHudFormat.Countdown(tenths);
                }
                Status.color = ResourceHudStyle.Icy;
                return;
            }

            lastTenths = -1;
            SetText(Status, IsFull ? "FULL" : "IDLE");
            Status.color = IsFull ? ResourceHudStyle.IcyFull : ResourceHudStyle.Muted;
        }

        private void SetCycles(int count)
        {
            if (count == lastCycles) return;
            lastCycles = count;
            Cycles.text = ResourceHudFormat.Cycles(count);
        }

        private void SetFillVisible(bool visible)
        {
            if (track.gameObject.activeSelf != visible) track.gameObject.SetActive(visible);
            if (!visible) FillFraction = 0f;
        }

        private static void SetText(Text t, string s)
        {
            if (t.text != s) t.text = s;
        }

        // ------------------------------------------------------------ layout

        /// <summary>Places the item at <paramref name="x"/> (bar units) with the given width and metrics.</summary>
        internal void Layout(float x, float width, ResourceHudMetrics m)
        {
            Place(Root, x, 0f, width, m.Height);
            float inner = width - 2f * m.pad;
            float y = m.topPad;

            // Row 1: icon + label, cycles badge on the right.
            float iconBox = m.icon * 1.41421356f; // rotated square's bounding box
            Place(icon, m.pad + (iconBox - m.icon) * 0.5f, y + (m.labelRow - m.icon) * 0.5f, m.icon, m.icon);
            icon.pivot = new Vector2(0.5f, 0.5f);
            icon.anchoredPosition = new Vector2(m.pad + iconBox * 0.5f, -(y + m.labelRow * 0.5f));
            float iconInset = Mathf.Max(1.5f, m.icon * 0.22f);
            iconInner.offsetMin = new Vector2(iconInset, iconInset);
            iconInner.offsetMax = new Vector2(-iconInset, -iconInset);
            float labelX = m.pad + iconBox + Mathf.Max(3f, 5f * m.fit);
            PlaceText(Label, labelX, y, inner - (labelX - m.pad), m.labelRow, m.labelFont);
            PlaceText(Cycles, m.pad, y, inner, m.labelRow, m.labelFont);
            y += m.labelRow;

            // Row 2: value left, capacity right.
            PlaceText(Value, m.pad, y, inner, m.valueRow, m.valueFont);
            PlaceText(Capacity, m.pad, y + 1f, inner, m.valueRow, m.captionFont);
            y += m.valueRow + m.gap;

            // Row 3: storage fill.
            Place(track, m.pad, y, inner, m.fillHeight);
            y += m.fillHeight + m.gap;

            // Row 4: caption left, status right.
            PlaceText(Caption, m.pad, y, inner, m.captionRow, m.captionFont);
            PlaceText(Status, m.pad, y, inner, m.captionRow, m.captionFont);
        }

        /// <summary>Widest text in this item relative to the width available to it, for overflow checks (≤ 1 fits).</summary>
        public float WorstFillRatio()
        {
            float inner = Value.rectTransform.rect.width;
            if (inner <= 0f) return 0f;
            float row1 = (Label.rectTransform.anchoredPosition.x - Value.rectTransform.anchoredPosition.x) + Label.preferredWidth + Gap(Cycles) + Cycles.preferredWidth;
            float row2 = Value.preferredWidth + Gap(Capacity) + Capacity.preferredWidth;
            float row4 = Caption.preferredWidth + Gap(Status) + Status.preferredWidth;
            return Mathf.Max(row1, Mathf.Max(row2, row4)) / inner;
        }

        private static float Gap(Text t) => string.IsNullOrEmpty(t.text) ? 0f : 6f;

        private static void PlaceText(Text t, float x, float y, float w, float h, int size)
        {
            if (t.fontSize != size) t.fontSize = size;
            Place(t.rectTransform, x, y, w, h);
        }

        /// <summary>Top-left anchored placement in parent units (y grows downwards).</summary>
        internal static void Place(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(Mathf.Round(x), -Mathf.Round(y));
            r.sizeDelta = new Vector2(Mathf.Round(w), Mathf.Round(h));
        }

        // ------------------------------------------------------------ construction helpers

        internal static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5; // UI
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            return r;
        }

        internal static Image AddImage(RectTransform r, Color color)
        {
            Image image = r.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false; // only the bar background blocks the pointer
            return image;
        }

        private static Text NewText(string name, Transform parent, Font font, FontStyle style, TextAnchor anchor, Color color, bool shadow)
        {
            RectTransform r = NewRect(name, parent);
            Text t = r.gameObject.AddComponent<Text>();
            t.font = font;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = color;
            t.raycastTarget = false;
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.text = string.Empty;
            if (shadow)
            {
                Shadow s = r.gameObject.AddComponent<Shadow>();
                s.effectColor = ResourceHudStyle.TextShadow;
                s.effectDistance = new Vector2(1f, -1f);
            }
            return t;
        }
    }
}
