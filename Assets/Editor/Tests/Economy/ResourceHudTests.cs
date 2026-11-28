using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Ironveil.Economy.Tests
{
    /// <summary>
    /// Edit-mode tests for the Stage 4 resource HUD: content, live updates through ResourceManager events, subscription
    /// hygiene, the ledger's read-only next-tick query, formatting and responsive layout maths.
    /// Scene integration and Play Mode lifetime are in <see cref="ResourceHudPlayModeTests"/>.
    /// </summary>
    public class ResourceHudTests
    {
        private ResourceManager manager;
        private ResourceHud hud;

        private static readonly string[] ExpectedLabels =
            { "CONCRETE", "IRON", "ALUMINIUM", "FUEL", "ECONOMY", "MILITARY", "GOLD", "GROUP LIMIT" };

        [SetUp]
        public void SetUp()
        {
            manager = ResourceManager.GetOrCreate();
            Assert.IsTrue(manager.Initialize(ResourceManagerTests.LoadConfig()), manager.LastError);
            hud = ResourceHud.GetOrCreate();
            hud.ViewportOverride = new Vector2(1920, 1080);
            hud.DpiOverride = 96f;
            hud.Bind(manager);
            hud.UpdateLayout(true);
        }

        [TearDown]
        public void TearDown()
        {
            var doomed = new List<GameObject>();
            foreach (ResourceHud h in Object.FindObjectsByType<ResourceHud>(FindObjectsInactive.Include)) doomed.Add(h.gameObject);
            foreach (ResourceManager m in Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include)) doomed.Add(m.gameObject);
            if (hud != null) hud.Unbind();
            foreach (GameObject go in doomed) if (go != null) Object.DestroyImmediate(go);
        }

        private ResourceHudItem Item(ResourceType r) => hud.Items[(int)r];

        // ------------------------------------------------------------ 1. order

        [Test]
        public void EightItems_AreDisplayedInTheRequiredOrder()
        {
            Assert.AreEqual(8, hud.Items.Count);
            CollectionAssert.AreEqual(
                new[] { ResourceType.Concrete, ResourceType.Iron, ResourceType.Aluminium, ResourceType.Fuel,
                        ResourceType.Economy, ResourceType.Military, ResourceType.Gold, ResourceType.GroupLimit },
                ResourceHudFormat.DisplayOrder);

            float previousX = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                ResourceHudItem item = Item(ResourceHudFormat.DisplayOrder[i]);
                Assert.AreEqual(ExpectedLabels[i], item.Label.text);
                Assert.Greater(item.Root.anchoredPosition.x, previousX, $"{item.Resource} must be right of the previous item.");
                previousX = item.Root.anchoredPosition.x;
                Assert.AreSame(hud.Bar, item.Root.parent, "All items live in the one continuous bar.");
            }
            Assert.AreEqual(4, hud.Items.Count(i => i.IsPrimary));
            CollectionAssert.AreEqual(new[] { true, true, true, true, false, false, false, false },
                ResourceHudFormat.DisplayOrder.Select(ResourceHudFormat.IsPrimary).ToArray());
        }

        // ------------------------------------------------------------ 2-5. initial content

        [Test]
        public void InitialBalances_AreTheLiveLedgerValues()
        {
            Assert.AreEqual("1,500,000", Item(ResourceType.Concrete).Value.text);
            Assert.AreEqual("900,000", Item(ResourceType.Iron).Value.text);
            Assert.AreEqual("400,000", Item(ResourceType.Aluminium).Value.text);
            Assert.AreEqual("100,000", Item(ResourceType.Fuel).Value.text);
            Assert.AreEqual("1,000", Item(ResourceType.Economy).Value.text);
            Assert.AreEqual("1,000", Item(ResourceType.Military).Value.text);
            Assert.AreEqual("10,000", Item(ResourceType.Gold).Value.text);
            foreach (ResourceType r in ResourceHudFormat.DisplayOrder)
            {
                if (!manager.TryGetBalance(r, out long b)) continue;
                Assert.AreEqual(ResourceHudFormat.Amount(b), Item(r).Value.text, $"{r} must mirror the ledger.");
            }
        }

        [Test]
        public void Capacities_AndReplenishmentRates_AreDisplayed()
        {
            Assert.AreEqual("/ 1,500,000", Item(ResourceType.Concrete).Capacity.text);
            Assert.AreEqual("/ 900,000", Item(ResourceType.Iron).Capacity.text);
            Assert.AreEqual("/ 400,000", Item(ResourceType.Aluminium).Capacity.text);
            Assert.AreEqual("/ 100,000", Item(ResourceType.Fuel).Capacity.text);
            Assert.AreEqual("/ 10,000", Item(ResourceType.Gold).Capacity.text);

            Assert.AreEqual("+30,000 / 5s", Item(ResourceType.Concrete).Caption.text);
            Assert.AreEqual("+20,000 / 5s", Item(ResourceType.Iron).Caption.text);
            Assert.AreEqual("+10,000 / 5s", Item(ResourceType.Aluminium).Caption.text);
            Assert.AreEqual("+5,000 / 5s", Item(ResourceType.Fuel).Caption.text);
            Assert.AreEqual("NO REFILL", Item(ResourceType.Gold).Caption.text, "Gold has no replenishment.");

            foreach (ResourceType r in new[] { ResourceType.Concrete, ResourceType.Iron, ResourceType.Aluminium, ResourceType.Fuel, ResourceType.Gold })
            {
                Assert.IsTrue(Item(r).FillVisible, $"{r} has a configured capacity and a storage indicator.");
                Assert.AreEqual(1f, Item(r).FillFraction, 1e-6f);
                Assert.IsTrue(Item(r).IsFull);
            }
        }

        [Test]
        public void EconomyAndMilitary_ShowNoInventedCapacity()
        {
            foreach (ResourceType r in new[] { ResourceType.Economy, ResourceType.Military })
            {
                ResourceHudItem item = Item(r);
                Assert.AreEqual(string.Empty, item.Capacity.text, $"{r} capacity is undecided.");
                Assert.IsFalse(item.FillVisible, $"{r} must not get a storage indicator.");
                Assert.AreEqual("CAPACITY —", item.Caption.text);
                Assert.AreEqual(string.Empty, item.Status.text);
                Assert.AreEqual(string.Empty, item.Cycles.text);
            }
        }

        [Test]
        public void GroupLimit_IsShownAsUnavailable()
        {
            Assert.IsFalse(manager.IsConfigured(ResourceType.GroupLimit));
            ResourceHudItem item = Item(ResourceType.GroupLimit);
            Assert.AreEqual("—", item.Value.text);
            Assert.AreEqual(string.Empty, item.Capacity.text);
            Assert.IsFalse(item.FillVisible);
            Assert.AreEqual("NOT CONFIGURED", item.Caption.text);
        }

        [Test]
        public void GroupLimit_ShowsItsValue_OnceConfigured()
        {
            // A future config that includes Group Limit: the HUD must pick it up generically, without code changes.
            var entries = new List<ResourceConfigEntry>(ResourceManagerTests.LoadConfig().Entries)
            {
                ResourceConfigEntry.FromConfig(ResourceConfig.Fixed(ResourceType.GroupLimit, 12, null)),
            };
            var config = HomeBaseResourceConfig.CreateRuntime(entries);
            try
            {
                if (!config.IsValid()) Assert.Ignore("The Stage 2 config validator does not accept Group Limit yet.");
                Object.DestroyImmediate(manager.gameObject);
                manager = ResourceManager.GetOrCreate();
                Assert.IsTrue(manager.Initialize(config), manager.LastError);
                hud.Bind(manager);
                Assert.AreEqual("12", Item(ResourceType.GroupLimit).Value.text);
                Assert.IsFalse(Item(ResourceType.GroupLimit).FillVisible);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        // ------------------------------------------------------------ 6-10. live updates

        [Test]
        public void Spending_UpdatesTheHud()
        {
            Assert.IsTrue(manager.TrySpend(ResourceType.Concrete, 300_000));
            ResourceHudItem item = Item(ResourceType.Concrete);
            Assert.AreEqual("1,200,000", item.Value.text);
            Assert.AreEqual("/ 1,500,000", item.Capacity.text);
            Assert.AreEqual(0.8f, item.FillFraction, 1e-5f);
            Assert.IsFalse(item.IsFull);
            Assert.AreEqual("×1", item.Cycles.text);
            Assert.AreEqual("NEXT 5.0s", item.Status.text);

            Assert.IsTrue(manager.TrySpend(ResourceType.Gold, 2_500));
            Assert.AreEqual("7,500", Item(ResourceType.Gold).Value.text);
            Assert.AreEqual(0.75f, Item(ResourceType.Gold).FillFraction, 1e-5f);

            Assert.IsFalse(manager.TrySpend(ResourceType.Fuel, 100_001), "Rejected spend.");
            Assert.AreEqual("100,000", Item(ResourceType.Fuel).Value.text);
        }

        [Test]
        public void Replenishment_UpdatesTheHud_AndCountdown()
        {
            manager.TrySpend(ResourceType.Concrete, 300_000);
            manager.Tick(2.5f);
            hud.RefreshCountdowns();
            Assert.AreEqual("NEXT 2.5s", Item(ResourceType.Concrete).Status.text);
            Assert.AreEqual("1,200,000", Item(ResourceType.Concrete).Value.text);

            manager.Tick(2.5f);
            Assert.AreEqual("1,230,000", Item(ResourceType.Concrete).Value.text, "Updated by the Replenished event.");
            hud.RefreshCountdowns();
            Assert.AreEqual("NEXT 5.0s", Item(ResourceType.Concrete).Status.text, "Next tick of the same cycle.");
            Assert.AreEqual("×1", Item(ResourceType.Concrete).Cycles.text);
        }

        [Test]
        public void MultipleCycles_ShowCountAndEarliestNextTick()
        {
            manager.TrySpend(ResourceType.Iron, 100_000); // cycle A ticks at 5, 10, ...
            manager.Tick(2f);
            manager.TrySpend(ResourceType.Iron, 100_000); // cycle B ticks at 7, 12, ...
            manager.Tick(1f);
            manager.TrySpend(ResourceType.Iron, 100_000); // cycle C ticks at 8, 13, ...
            hud.RefreshCountdowns();
            ResourceHudItem iron = Item(ResourceType.Iron);
            Assert.AreEqual("×3", iron.Cycles.text);
            Assert.AreEqual("600,000", iron.Value.text);
            Assert.AreEqual("NEXT 2.0s", iron.Status.text, "Earliest cycle (A) at t=5, now t=3.");

            manager.Tick(2f); // t=5: A ticks
            hud.RefreshCountdowns();
            Assert.AreEqual("620,000", iron.Value.text);
            Assert.AreEqual("NEXT 2.0s", iron.Status.text, "B is next at t=7.");

            manager.Tick(2f); // t=7: B ticks
            hud.RefreshCountdowns();
            Assert.AreEqual("640,000", iron.Value.text);
            Assert.AreEqual("NEXT 1.0s", iron.Status.text, "C is next at t=8.");
            Assert.AreEqual("×3", iron.Cycles.text);
        }

        [Test]
        public void FullCapacity_IsShown_AndNeverExceeded()
        {
            manager.TrySpend(ResourceType.Fuel, 5_000);
            manager.TrySpend(ResourceType.Fuel, 1);
            ResourceHudItem fuel = Item(ResourceType.Fuel);
            Assert.AreEqual("94,999", fuel.Value.text);
            Assert.AreEqual("×2", fuel.Cycles.text);

            manager.Tick(5f); // two cycles x 5,000 would be 104,999: clamped to capacity, cycles removed
            hud.RefreshCountdowns();
            Assert.AreEqual("100,000", fuel.Value.text);
            Assert.AreEqual(1f, fuel.FillFraction, 1e-6f);
            Assert.IsTrue(fuel.IsFull);
            Assert.AreEqual("FULL", fuel.Status.text);
            Assert.AreEqual(string.Empty, fuel.Cycles.text, "No active cycles at capacity.");
        }

        [Test]
        public void LedgerReset_RedrawsInitialValues()
        {
            manager.TrySpend(ResourceType.Concrete, 500_000);
            manager.TrySpend(ResourceType.Gold, 1_000);
            Assert.AreEqual("1,000,000", Item(ResourceType.Concrete).Value.text);

            Assert.IsTrue(manager.ResetToInitialConfiguration());
            Assert.AreEqual(1, hud.LedgerResetsReceived);
            Assert.AreEqual("1,500,000", Item(ResourceType.Concrete).Value.text);
            Assert.AreEqual("10,000", Item(ResourceType.Gold).Value.text);
            Assert.AreEqual(string.Empty, Item(ResourceType.Concrete).Cycles.text);
            Assert.AreEqual("FULL", Item(ResourceType.Concrete).Status.text);

            // Still live against the NEW ledger.
            manager.TrySpend(ResourceType.Concrete, 1);
            Assert.AreEqual("1,499,999", Item(ResourceType.Concrete).Value.text);
        }

        // ------------------------------------------------------------ 11. subscriptions

        [Test]
        public void DuplicateSubscriptions_ArePrevented()
        {
            Assert.IsFalse(hud.Bind(manager), "Binding the same manager again must be a no-op.");
            Assert.IsFalse(hud.Bind(manager));
            Assert.AreSame(hud, ResourceHud.GetOrCreate(), "No second HUD.");
            Assert.AreEqual(1, Object.FindObjectsByType<ResourceHud>(FindObjectsInactive.Include).Length);

            Assert.AreEqual(1, CountHudHandlers(manager, "ResourceChanged"));
            Assert.AreEqual(1, CountHudHandlers(manager, "LedgerReset"));

            int before = hud.ResourceEventsReceived;
            manager.TrySpend(ResourceType.Iron, 10);
            Assert.AreEqual(before + 1, hud.ResourceEventsReceived, "Exactly one notification per change.");

            hud.Unbind();
            Assert.AreEqual(0, CountHudHandlers(manager, "ResourceChanged"));
            Assert.AreEqual(0, CountHudHandlers(manager, "LedgerReset"));
            hud.Unbind(); // idempotent
        }

        [Test]
        public void Hud_NeverCreatesAManagerOrLedger()
        {
            int ledgers = ResourceManager.LedgersCreatedThisSession;
            hud.RefreshAll();
            hud.UpdateLayout(true);
            hud.RefreshCountdowns();
            Assert.AreEqual(1, Object.FindObjectsByType<ResourceManager>(FindObjectsInactive.Include).Length);
            Assert.AreEqual(ledgers, ResourceManager.LedgersCreatedThisSession);
            Assert.AreSame(manager.Ledger, hud.BoundManager.Ledger);
            Assert.IsNull(ResourceHud.EnsureForLoadedScene(), "Auto-creation only runs in Play Mode.");
        }

        [Test]
        public void Unbound_ShowsDashes_NotNumbers()
        {
            hud.Bind(null);
            foreach (ResourceHudItem item in hud.Items)
            {
                Assert.AreEqual("—", item.Value.text, item.Resource.ToString());
                Assert.IsFalse(item.FillVisible);
            }
        }

        internal static int CountHudHandlers(ResourceManager m, string eventName)
        {
            FieldInfo field = typeof(ResourceManager).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, eventName);
            var d = field.GetValue(m) as Delegate;
            return d == null ? 0 : d.GetInvocationList().Count(h => h.Target is ResourceHud);
        }

        // ------------------------------------------------------------ canvas and pointer blocking

        [Test]
        public void Canvas_IsOverlayAtOrder80_AndOnlyTheBarBlocksThePointer()
        {
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, hud.Canvas.renderMode);
            Assert.AreEqual(80, hud.Canvas.sortingOrder);
            Assert.Less(hud.Canvas.sortingOrder, 90, "Below the Base Management Menu.");
            Assert.Less(hud.Canvas.sortingOrder, 100, "Below the Explorer UI.");
            Assert.IsNotNull(hud.Canvas.GetComponent<GraphicRaycaster>());

            var targets = hud.Canvas.GetComponentsInChildren<Graphic>(true).Where(g => g.raycastTarget).ToArray();
            Assert.AreEqual(1, targets.Length, "Exactly one raycast target: the bar background.");
            Assert.AreSame(hud.Bar.gameObject, targets[0].gameObject);
        }

        // ------------------------------------------------------------ ledger read-only API

        [Test]
        public void Ledger_NextReplenishTime_IsReadOnly()
        {
            ResourceLedger ledger = HomeBaseResourceProfile.CreateSinglePlayerLedger();
            Assert.IsFalse(ledger.TryGetNextReplenishTime(ResourceType.Concrete, out _), "No cycles yet.");
            Assert.IsFalse(ledger.TryGetNextReplenishTime(ResourceType.GroupLimit, out _), "Unconfigured.");
            Assert.IsFalse(ledger.TryGetNextReplenishTime(ResourceType.Gold, out _), "Never replenishes.");

            ledger.TrySpend(ResourceType.Concrete, 100_000);
            ledger.Advance(TimeSpan.FromSeconds(3));
            ledger.TrySpend(ResourceType.Concrete, 100_000);
            Assert.IsTrue(ledger.TryGetNextReplenishTime(ResourceType.Concrete, out TimeSpan next));
            Assert.AreEqual(TimeSpan.FromSeconds(5), next, "Earliest of t=5 and t=8.");

            // Querying changes nothing.
            for (int i = 0; i < 5; i++) ledger.TryGetNextReplenishTime(ResourceType.Concrete, out _);
            Assert.AreEqual(1_300_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.AreEqual(2, ledger.GetActiveCycleCount(ResourceType.Concrete));
            Assert.AreEqual(TimeSpan.FromSeconds(3), ledger.CurrentTime);

            ledger.Advance(TimeSpan.FromSeconds(2)); // t=5: first cycle ticks, rescheduled to 10
            Assert.AreEqual(1_330_000, ledger.GetBalance(ResourceType.Concrete));
            Assert.IsTrue(ledger.TryGetNextReplenishTime(ResourceType.Concrete, out next));
            Assert.AreEqual(TimeSpan.FromSeconds(8), next);

            ledger.Advance(TimeSpan.FromSeconds(100)); // refills to capacity: all cycles end
            Assert.IsFalse(ledger.TryGetNextReplenishTime(ResourceType.Concrete, out _));
        }

        // ------------------------------------------------------------ formatting

        [Test]
        public void Numbers_UseFullCommaSeparatedValues()
        {
            Assert.AreEqual("1,500,000", ResourceHudFormat.Amount(1_500_000));
            Assert.AreEqual("900,000", ResourceHudFormat.Amount(900_000));
            Assert.AreEqual("400,000", ResourceHudFormat.Amount(400_000));
            Assert.AreEqual("100,000", ResourceHudFormat.Amount(100_000));
            Assert.AreEqual("0", ResourceHudFormat.Amount(0));
            Assert.AreEqual("+30,000 / 5s", ResourceHudFormat.Replenishment(30_000, TimeSpan.FromSeconds(5)));
            Assert.AreEqual("2.5s", ResourceHudFormat.Interval(TimeSpan.FromMilliseconds(2500)));
            foreach (ResourceHudItem item in hud.Items)
            {
                StringAssert.DoesNotContain("M", item.Value.text);
                StringAssert.DoesNotContain("K", item.Value.text);
            }
        }

        [Test]
        public void Countdown_RoundsUpToTheTenth()
        {
            Assert.AreEqual(50, ResourceHudFormat.CountdownTenths(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, ResourceHudFormat.CountdownTenths(TimeSpan.FromTicks(1)));
            Assert.AreEqual(0, ResourceHudFormat.CountdownTenths(TimeSpan.Zero));
            Assert.AreEqual(43, ResourceHudFormat.CountdownTenths(TimeSpan.FromSeconds(4.21)));
            Assert.AreEqual("NEXT 4.3s", ResourceHudFormat.Countdown(43));
            Assert.AreEqual("NEXT 0.1s", ResourceHudFormat.Countdown(1));
        }

        [Test]
        public void ChangingValues_DoNotMoveTheLayout()
        {
            Vector2[] before = hud.Items.Select(i => i.Value.rectTransform.anchoredPosition).ToArray();
            Vector2[] sizes = hud.Items.Select(i => i.Root.sizeDelta).ToArray();
            manager.TrySpend(ResourceType.Concrete, 1_499_999);
            manager.TrySpend(ResourceType.Iron, 7);
            manager.Tick(1.3f);
            hud.UpdateLayout(false);
            CollectionAssert.AreEqual(before, hud.Items.Select(i => i.Value.rectTransform.anchoredPosition).ToArray());
            CollectionAssert.AreEqual(sizes, hud.Items.Select(i => i.Root.sizeDelta).ToArray());
        }

        // ------------------------------------------------------------ responsiveness

        internal static readonly Vector2Int[] Resolutions =
        {
            new Vector2Int(1920, 1080), new Vector2Int(1600, 900), new Vector2Int(1366, 768),
            new Vector2Int(1280, 720), new Vector2Int(1024, 640), new Vector2Int(960, 600),
        };

        [Test]
        public void MainBuildLayout_FitsAllEightItems_AtEverySupportedResolution()
        {
            // Worst-case strings: full capacities, a multi-cycle badge and a countdown on every primary item.
            foreach (ResourceType r in new[] { ResourceType.Concrete, ResourceType.Iron, ResourceType.Aluminium, ResourceType.Fuel })
                for (int k = 0; k < 12; k++) manager.TrySpend(r, 1);
            hud.RefreshCountdowns();

            foreach (Vector2Int res in Resolutions)
            {
                hud.ViewportOverride = res;
                hud.UpdateLayout(true);
                ResourceHudPlacement p = hud.Placement;
                Assert.AreEqual(ResourceHudPlacementMode.FullWidthTop, p.mode, res.ToString());
                Assert.AreEqual(res.x, p.rect.width, 0.5f, $"{res}: one continuous bar across the screen.");
                Assert.AreEqual(0f, p.rect.y, $"{res}: at the top edge.");
                Assert.IsFalse(p.belowMinimum, $"{res}: fit {p.fit} is below the legible minimum.");
                Assert.LessOrEqual(p.rect.height, 76f, $"{res}: bar should stay compact.");

                float right = 0f;
                foreach (ResourceHudItem item in hud.Items)
                {
                    Assert.IsTrue(item.Root.gameObject.activeSelf, $"{res}: {item.Resource} must never be hidden.");
                    float ratio = item.WorstFillRatio();
                    Assert.LessOrEqual(ratio, 1f, $"{res}: {item.Resource} text overflows its slot ({ratio:0.00}).");
                    Assert.GreaterOrEqual(item.Value.fontSize, 12);
                    Assert.GreaterOrEqual(item.Label.fontSize, 9);
                    right = Mathf.Max(right, item.Root.anchoredPosition.x + item.Root.sizeDelta.x);
                }
                Assert.LessOrEqual(right, p.rect.width + 0.5f, $"{res}: items stay inside the bar.");
            }
        }

        [Test]
        public void ExplorerLayout_NeverOverlapsPanelOrIndicator()
        {
            foreach (Vector2Int res in Resolutions)
            {
                // ExplorerWorldUI geometry (CSS px): scale = clamp(min(w/960, h/600), 0.72, 1); panel 276 wide at 14 px
                // margin, ~260 tall; indicator 292 x 34 at the top centre, 14 px down.
                float s = Mathf.Clamp(Mathf.Min(res.x / 960f, res.y / 600f), 0.72f, 1f);
                var panel = new Rect(14f * s, 14f * s, 276f * s, 260f * s);
                var indicator = new Rect(res.x * 0.5f - 146f * s, 14f * s, 292f * s, 34f * s);
                ResourceHudPlacement p = ResourceHudLayout.Place(res, panel, indicator);

                Assert.IsFalse(p.rect.Overlaps(panel), $"{res}: overlaps the Explorer panel ({p.mode} {p.rect}).");
                Assert.IsFalse(p.rect.Overlaps(indicator), $"{res}: overlaps the Explorer indicator ({p.mode} {p.rect}).");
                Assert.GreaterOrEqual(p.rect.xMin, 0f);
                Assert.LessOrEqual(p.rect.xMax, res.x + 0.01f);
                Assert.IsFalse(p.belowMinimum, $"{res}: fit {p.fit} below the legible minimum.");
                Assert.IsTrue(p.Framed);
                if (res.x >= 1280) Assert.AreEqual(ResourceHudPlacementMode.BesideExplorerPanel, p.mode, res.ToString());
                else Assert.AreEqual(ResourceHudPlacementMode.BelowExplorerPanel, p.mode, res.ToString());
            }
        }

        [Test]
        public void HighDpi_ScalesTheCanvas_NotTheLayoutUnits()
        {
            hud.ViewportOverride = new Vector2(3840, 2160);
            hud.DpiOverride = 192f;
            hud.UpdateLayout(true);
            Assert.AreEqual(2f, hud.Canvas.GetComponent<CanvasScaler>().scaleFactor, 1e-5f);
            Assert.AreEqual(1920f, hud.Placement.rect.width, 0.5f);
        }
    }
}
