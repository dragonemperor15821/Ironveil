using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Ironveil.Economy
{
    /// <summary>
    /// Stage 4 visual resource HUD: one continuous bar of eight items across the top of the screen
    /// (Concrete, Iron, Aluminium, Fuel | Economy, Military, Gold, Group Limit).
    ///
    /// Lifetime
    /// - One per Play session, on its own DontDestroyOnLoad host, like <see cref="ResourceManager"/>. After every scene
    ///   load (which runs after that scene's <see cref="ResourceHudBootstrap"/> Awake), <see cref="EnsureForLoadedScene"/>
    ///   gets or creates the single HUD and binds it to <see cref="ResourceManager.Instance"/>. Scenes are not modified.
    /// - Never creates a ResourceManager or ledger, and never stores balances: every value is read from the live ledger
    ///   (<see cref="ResourceManager.Ledger"/>), and replenishment rates from the asset that ledger was built from.
    ///
    /// Updates
    /// - Balances, capacities and cycle counts refresh on <see cref="ResourceManager.ResourceChanged"/> (spend, add,
    ///   replenish) and <see cref="ResourceManager.LedgerReset"/>. Subscriptions are made once per manager and removed
    ///   on destroy or when the session's manager changes.
    /// - Countdowns poll the ledger's read-only next-tick time each frame (text rebuilt only when the tenth changes).
    ///   No timer of its own exists; replenishment stays entirely inside the ledger.
    ///
    /// Layout
    /// - Screen-space overlay canvas, sorting order 80: below the Base Management Menu (90) and the Explorer UI (100).
    /// - MainBuild: edge-to-edge top strip. World Explorer: placed beside its top-left panel and below its top-centre
    ///   indicator, or below the panel on narrow screens (<see cref="ResourceHudLayout"/>). ExplorerWorldUI is only read.
    /// - The bar background is a raycast target, so the existing "pointer over UI" checks in BaseHoverController,
    ///   BaseMenuController, RTSMainCamera and ExplorerCameraController ignore the pointer over the HUD. An EventSystem is
    ///   ensured in the active scene the same way BaseMenuView and ExplorerWorldUI do it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ResourceHud : MonoBehaviour
    {
        public const string HostName = "Resource HUD";
        public const string CanvasName = "Resource HUD Canvas";
        public const string BarName = "Resource Bar";
        public const int SortingOrder = 80;

        /// <summary>Name of ExplorerWorldUI's top-centre indicator object (read only, never modified).</summary>
        public const string ExplorerIndicatorName = "New World Indicator";

        public static ResourceHud Instance { get; private set; }

        // ------------------------------------------------------------ state

        private Canvas canvas;
        private CanvasScaler scaler;
        private RectTransform bar;
        private Image background;
        private Outline frameOutline;
        private Shadow frameShadow;
        private RectTransform highlight, bottomEdge, bottomShadow, strategicTint;
        private readonly List<RectTransform> separators = new List<RectTransform>();
        private readonly ResourceHudItem[] items = new ResourceHudItem[ResourceTypeRules.Count];
        private Texture2D gradientTexture;
        private Sprite gradientSprite;
        private Font font;

        private ResourceManager bound;
        private readonly long[] replenishAmount = new long[ResourceTypeRules.Count];
        private readonly TimeSpan[] replenishInterval = new TimeSpan[ResourceTypeRules.Count];

        private ExplorerWorldUI explorerUi;
        private RectTransform explorerIndicator;
        private bool explorerSearched;

        private ResourceHudPlacement placement;
        private bool hasPlacement;

        public bool IsBuilt => bar != null;
        public Canvas Canvas => canvas;
        public RectTransform Bar => bar;
        public ResourceManager BoundManager => bound;
        public ResourceHudPlacement Placement => placement;
        public IReadOnlyList<ResourceHudItem> Items => items;

        /// <summary>Diagnostics/tests: events handled since creation.</summary>
        public int ResourceEventsReceived { get; private set; }
        public int LedgerResetsReceived { get; private set; }
        public int FullRefreshCount { get; private set; }

        /// <summary>Harness/test hooks: replace Screen size (pixels) and DPI. Null = live Screen values.</summary>
        public Vector2? ViewportOverride { get; set; }
        public float? DpiOverride { get; set; }

        // ------------------------------------------------------------ session wiring

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            // Domain reload is disabled in this project: clear statics and the static scene hook.
            Instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallSceneHook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded; // never twice
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureForLoadedScene(); // the first scene loaded before this hook existed
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureForLoadedScene();

        /// <summary>
        /// Gets or creates the session's single HUD and binds it to the initialized ResourceManager. Does nothing (and
        /// creates nothing) while no initialized manager exists, e.g. in a scene without a bootstrap.
        /// </summary>
        public static ResourceHud EnsureForLoadedScene()
        {
            if (!Application.isPlaying) return null;
            ResourceManager manager = ResourceManager.Instance;
            if (manager == null || !manager.IsInitialized) return Instance;

            ResourceHud hud = GetOrCreate();
            hud.Bind(manager);
            hud.OnSceneChanged();
            return hud;
        }

        /// <summary>Returns the session's HUD, creating its persistent host if none exists. Never creates a second one.</summary>
        public static ResourceHud GetOrCreate()
        {
            if (Instance != null) return Instance;

            ResourceHud existing = FindAnyObjectByType<ResourceHud>(FindObjectsInactive.Include);
            if (existing != null)
            {
                Instance = existing;
                existing.EnsureBuilt();
                return existing;
            }

            var host = new GameObject(HostName);
            if (Application.isPlaying) DontDestroyOnLoad(host);
            ResourceHud hud = host.AddComponent<ResourceHud>(); // Awake (Play Mode) sets Instance and builds
            Instance = hud;
            hud.EnsureBuilt();
            return hud;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"ResourceHud: a second HUD on '{name}' was removed; the session already has one on '{Instance.name}'.", this);
                Destroy(this);
                return;
            }
            Instance = this;
            EnsureBuilt();
        }

        private void OnDestroy()
        {
            Unbind();
            if (gradientSprite != null) DestroyAsset(gradientSprite);
            if (gradientTexture != null) DestroyAsset(gradientTexture);
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------ binding

        /// <summary>
        /// Subscribes to <paramref name="manager"/> (unsubscribing from any previous one) and redraws everything.
        /// Binding the same manager again is a no-op, so subscriptions can never be duplicated.
        /// </summary>
        /// <returns>True if the binding changed.</returns>
        public bool Bind(ResourceManager manager)
        {
            EnsureBuilt();
            if (ReferenceEquals(bound, manager) && manager != null) return false;

            Unbind();
            bound = manager;
            if (manager != null)
            {
                manager.ResourceChanged += OnResourceChanged;
                manager.LedgerReset += OnLedgerReset;
            }
            RefreshAll();
            return true;
        }

        /// <summary>Removes this HUD's subscriptions. Safe to call repeatedly.</summary>
        public void Unbind()
        {
            if (ReferenceEquals(bound, null)) return;
            // The manager may already be destroyed (Unity-null); its C# events can still be unsubscribed.
            bound.ResourceChanged -= OnResourceChanged;
            bound.LedgerReset -= OnLedgerReset;
            bound = null;
        }

        private void OnResourceChanged(ResourceChange change)
        {
            ResourceEventsReceived++;
            RefreshItem(change.Resource);
        }

        private void OnLedgerReset()
        {
            LedgerResetsReceived++;
            RefreshAll();
        }

        // ------------------------------------------------------------ per frame

        private void LateUpdate()
        {
            // Follow the session's manager if it was replaced (never creates one).
            ResourceManager current = ResourceManager.Instance;
            if (!ReferenceEquals(current, bound) && current != null) Bind(current);
            else if (bound == null && !ReferenceEquals(bound, null)) Bind(null); // bound manager destroyed

            UpdateLayout(false);
            RefreshCountdowns();
        }

        /// <summary>Re-reads every item from the live ledger.</summary>
        public void RefreshAll()
        {
            EnsureBuilt();
            FullRefreshCount++;
            CacheReplenishSettings();
            for (int i = 0; i < ResourceHudFormat.DisplayOrder.Length; i++)
                RefreshItem(ResourceHudFormat.DisplayOrder[i]);
        }

        /// <summary>Re-reads one item's balance, capacity, rate and cycle state from the live ledger.</summary>
        public void RefreshItem(ResourceType resource)
        {
            if (!ResourceTypeRules.IsDefined(resource) || !IsBuilt) return;
            ResourceHudItem item = items[(int)resource];
            ResourceLedger ledger = bound != null ? bound.Ledger : null;

            if (ledger == null || !ledger.IsConfigured(resource))
            {
                item.ShowUnconfigured();
                return;
            }

            long? capacity = ledger.GetCapacity(resource);
            item.ShowBalance(ledger.GetBalance(resource), capacity);

            if (ledger.IsReplenishing(resource))
            {
                item.ShowReplenishment(replenishAmount[(int)resource], replenishInterval[(int)resource]);
                RefreshCycleState(item, ledger);
            }
            else
            {
                item.ShowStaticCaption(capacity.HasValue);
            }
        }

        /// <summary>Updates countdowns and cycle badges from the ledger's read-only cycle state.</summary>
        public void RefreshCountdowns()
        {
            if (!IsBuilt || bound == null) return;
            ResourceLedger ledger = bound.Ledger;
            if (ledger == null) return;
            for (int i = 0; i < ResourceTypeRules.Count; i++)
            {
                if (ledger.IsReplenishing((ResourceType)i)) RefreshCycleState(items[i], ledger);
            }
        }

        private static void RefreshCycleState(ResourceHudItem item, ResourceLedger ledger)
        {
            int cycles = ledger.GetActiveCycleCount(item.Resource);
            bool hasNext = ledger.TryGetNextReplenishTime(item.Resource, out TimeSpan next);
            item.ShowCycleState(cycles, hasNext, hasNext ? next - ledger.CurrentTime : TimeSpan.Zero);
        }

        private void CacheReplenishSettings()
        {
            Array.Clear(replenishAmount, 0, replenishAmount.Length);
            Array.Clear(replenishInterval, 0, replenishInterval.Length);
            HomeBaseResourceConfig config = bound != null ? bound.Config : null;
            if (config == null) return;
            foreach (ResourceConfigEntry entry in config.Entries)
            {
                if (!entry.Replenishes || !ResourceTypeRules.IsDefined(entry.Resource)) continue;
                replenishAmount[(int)entry.Resource] = entry.ReplenishAmount;
                replenishInterval[(int)entry.Resource] = TimeSpan.FromMilliseconds(entry.ReplenishIntervalMilliseconds);
            }
        }

        // ------------------------------------------------------------ scene and layout

        /// <summary>Called after each scene load: forget the previous scene's Explorer UI and lay out again.</summary>
        public void OnSceneChanged()
        {
            explorerSearched = false;
            explorerUi = null;
            explorerIndicator = null;
            if (Application.isPlaying) EnsureEventSystem();
            UpdateLayout(true);
        }

        /// <summary>Recomputes placement; applies it only when something changed (cheap every frame).</summary>
        public void UpdateLayout(bool force)
        {
            if (!IsBuilt) return;

            if (!explorerSearched || (explorerUi == null && !ReferenceEquals(explorerUi, null)))
            {
                explorerSearched = true;
                explorerUi = FindAnyObjectByType<ExplorerWorldUI>();
                explorerIndicator = null;
                if (explorerUi != null && explorerUi.PanelRect != null && explorerUi.PanelRect.parent != null)
                    explorerIndicator = explorerUi.PanelRect.parent.Find(ExplorerIndicatorName) as RectTransform;
            }

            Vector2 screenPx = ViewportOverride ?? new Vector2(Screen.width, Screen.height);
            float dpi = DpiOverride ?? Screen.dpi;
            float dpiScale = dpi > 1f ? Mathf.Clamp(dpi / 96f, 0.5f, 4f) : 1f; // same rule as ExplorerWorldUI
            Vector2 viewport = screenPx / dpiScale;

            Rect? panelRect = null, indicatorRect = null;
            if (explorerUi != null && explorerUi.PanelRect != null)
            {
                panelRect = ToTopLeftUnits(explorerUi.PanelRect, screenPx.y, dpiScale);
                if (explorerIndicator != null) indicatorRect = ToTopLeftUnits(explorerIndicator, screenPx.y, dpiScale);
            }

            ResourceHudPlacement next = ResourceHudLayout.Place(viewport, panelRect, indicatorRect);
            if (!force && hasPlacement && SamePlacement(next, placement) && Mathf.Approximately(scaler.scaleFactor, dpiScale)) return;

            placement = next;
            hasPlacement = true;
            scaler.scaleFactor = dpiScale;
            ApplyPlacement(next);
        }

        private static bool SamePlacement(ResourceHudPlacement a, ResourceHudPlacement b) =>
            a.mode == b.mode && a.rect == b.rect && Mathf.Approximately(a.fit, b.fit);

        private static readonly Vector3[] CornerBuffer = new Vector3[4];

        /// <summary>Screen rectangle of a UI element in HUD canvas units, top-left origin. Works for overlay and camera canvases.</summary>
        private static Rect ToTopLeftUnits(RectTransform rt, float screenHeightPx, float dpiScale)
        {
            Canvas c = rt.GetComponentInParent<Canvas>();
            Camera cam = c != null && c.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? c.rootCanvas.worldCamera : null;
            Vector3[] corners = CornerBuffer;
            rt.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 s = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
                minX = Mathf.Min(minX, s.x); maxX = Mathf.Max(maxX, s.x);
                minY = Mathf.Min(minY, s.y); maxY = Mathf.Max(maxY, s.y);
            }
            return Rect.MinMaxRect(minX / dpiScale, (screenHeightPx - maxY) / dpiScale, maxX / dpiScale, (screenHeightPx - minY) / dpiScale);
        }

        private void ApplyPlacement(ResourceHudPlacement p)
        {
            ResourceHudMetrics m = ResourceHudLayout.Metrics(p.fit);
            float w = Mathf.Round(p.rect.width);
            float h = m.Height;
            ResourceHudItem.Place(bar, p.rect.x, p.rect.y, w, h);

            frameOutline.enabled = p.Framed;
            frameShadow.enabled = true;

            ResourceHudItem.Place(highlight, 0f, 0f, w, 1f);
            ResourceHudItem.Place(bottomShadow, 0f, h - 3f, w, 1f);
            ResourceHudItem.Place(bottomEdge, 0f, h - 2f, w, 2f);

            float endPad = Mathf.Round(ResourceHudLayout.EndPadding * p.fit);
            float usable = w - 2f * endPad;
            float totalWeight = ResourceHudLayout.DesignWidth - 2f * ResourceHudLayout.EndPadding;

            float x = endPad;
            int sep = 0;
            float strategicStart = -1f;
            for (int i = 0; i < ResourceHudFormat.DisplayOrder.Length; i++)
            {
                ResourceType r = ResourceHudFormat.DisplayOrder[i];
                float slot = Mathf.Round(usable * ResourceHudLayout.Weight(r) / totalWeight);
                if (i == ResourceHudFormat.DisplayOrder.Length - 1) slot = w - endPad - x; // absorb rounding
                if (!ResourceHudFormat.IsPrimary(r) && strategicStart < 0f) strategicStart = x;
                items[(int)r].Layout(x, slot, m);
                x += slot;

                if (i < ResourceHudFormat.DisplayOrder.Length - 1)
                {
                    bool groupBoundary = ResourceHudFormat.IsPrimary(r) != ResourceHudFormat.IsPrimary(ResourceHudFormat.DisplayOrder[i + 1]);
                    RectTransform line = separators[sep++];
                    RectTransform shade = separators[sep++];
                    float inset = groupBoundary ? 4f : Mathf.Round(m.topPad + 2f);
                    float lw = groupBoundary ? 2f : 1f;
                    ResourceHudItem.Place(line, x - lw, inset, lw, h - 2f * inset);
                    ResourceHudItem.Place(shade, x, inset, 1f, h - 2f * inset);
                    line.GetComponent<Image>().color = groupBoundary ? ResourceHudStyle.GroupDivider : ResourceHudStyle.Separator;
                }
            }

            ResourceHudItem.Place(strategicTint, strategicStart, 1f, w - strategicStart, h - 3f);
        }

        // ------------------------------------------------------------ construction

        /// <summary>Builds the canvas and bar once. Called from Awake, and lazily in Edit Mode where Awake does not run.</summary>
        public void EnsureBuilt()
        {
            if (IsBuilt) return;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); // same font as BaseMenuInfoPanel and ExplorerWorldUI

            var canvasObject = new GameObject(CanvasName, typeof(RectTransform));
            canvasObject.layer = 5;
            canvasObject.transform.SetParent(transform, false);
            canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            canvas.pixelPerfect = false;
            scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasObject.AddComponent<GraphicRaycaster>();

            bar = ResourceHudItem.NewRect(BarName, canvasObject.transform);
            background = bar.gameObject.AddComponent<Image>();
            background.sprite = CreateGradientSprite();
            background.type = Image.Type.Simple;
            background.color = Color.white;
            background.raycastTarget = true; // blocks base hover, menu clicks-through and camera input under the HUD
            frameOutline = bar.gameObject.AddComponent<Outline>();
            frameOutline.effectColor = ResourceHudStyle.Border;
            frameOutline.effectDistance = new Vector2(1f, -1f);
            frameOutline.useGraphicAlpha = false;
            frameShadow = bar.gameObject.AddComponent<Shadow>();
            frameShadow.effectColor = new Color(0f, 0f, 0f, 0.40f);
            frameShadow.effectDistance = new Vector2(0f, -3f);

            strategicTint = Decoration("Strategic Group Tint", ResourceHudStyle.StrategicTint);
            highlight = Decoration("Top Highlight", ResourceHudStyle.Highlight);
            bottomShadow = Decoration("Bottom Shadow", ResourceHudStyle.BottomShadow);
            bottomEdge = Decoration("Bottom Edge", ResourceHudStyle.BottomEdge);

            for (int i = 0; i < ResourceHudFormat.DisplayOrder.Length; i++)
            {
                ResourceType r = ResourceHudFormat.DisplayOrder[i];
                items[(int)r] = new ResourceHudItem(r, bar, font);
                if (i < ResourceHudFormat.DisplayOrder.Length - 1)
                {
                    separators.Add(Decoration("Separator " + i, ResourceHudStyle.Separator));
                    separators.Add(Decoration("Separator Shade " + i, ResourceHudStyle.SeparatorShadow));
                }
            }

            hasPlacement = false;
            UpdateLayout(true);
            RefreshAll();
        }

        private RectTransform Decoration(string name, Color color)
        {
            RectTransform r = ResourceHudItem.NewRect(name, bar);
            ResourceHudItem.AddImage(r, color);
            return r;
        }

        private Sprite CreateGradientSprite()
        {
            const int h = 32;
            gradientTexture = new Texture2D(1, h, TextureFormat.RGBA32, false)
            {
                name = "Resource HUD Gradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            for (int y = 0; y < h; y++)
            {
                float t = y / (h - 1f); // 0 = bottom row, 1 = top row
                Color c = t > 0.5f
                    ? Color.Lerp(ResourceHudStyle.BackgroundMid, ResourceHudStyle.BackgroundTop, (t - 0.5f) * 2f)
                    : Color.Lerp(ResourceHudStyle.BackgroundBottom, ResourceHudStyle.BackgroundMid, t * 2f);
                gradientTexture.SetPixel(0, y, c);
            }
            gradientTexture.Apply(false, true);
            gradientSprite = Sprite.Create(gradientTexture, new Rect(0, 0, 1, h), new Vector2(0.5f, 0.5f), 100f);
            gradientSprite.name = "Resource HUD Gradient";
            gradientSprite.hideFlags = HideFlags.DontSave;
            return gradientSprite;
        }

        /// <summary>
        /// uGUI needs an EventSystem for the "pointer over UI" checks to see the HUD. World Explorer already has one;
        /// MainBuild has none until the base menu first opens, so one is added to the active scene here, exactly as
        /// BaseMenuView.EnsureEventSystem would.
        /// </summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        private static void DestroyAsset(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
