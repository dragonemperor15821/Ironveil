using System;
using System.Collections.Generic;
using Ironveil.Economy;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Runtime construction API for generated bases (no costs, timers, combat or
/// UI wiring; those are later stages). Every call works on ONE base's own
/// BaseFlakDefence; nothing is shared between bases.
///
///   InitialiseBase   - binds the base to its approved Flak layout and applies the
///                      starting state exactly once: 2 x L1 Flak (Flak_R1_01,
///                      Flak_L1_01) + 1 x L1 Refinery, grounded on the terrain.
///   TryConstructFlak - builds one Flak at the base's current Flak level in the
///                      next free approved position (construction order) or a
///                      given slot; the 29th is refused.
///   TryDemolishFlak  - removes a constructed Flak (a given slot, or the latest in
///                      construction order); its position becomes free again.
///   TryUpgradeFlaks  - buys the collective Flak upgrade of one base (L1 -> L2 -> L3 -> L4,
///                      FlakUpgradeRules) with the shared player-wide Gold; all or nothing.
///   TryStartFlakConstruction - pays one queued Flak construction (FlakConstructionRules)
///                      with the shared resources and reserves its position; the
///                      base's BaseFlakConstruction queue builds it over time.
/// </summary>
public static class BaseConstruction
{
    public enum InitResult { Initialised, AlreadyInitialised, NotAFoundation, NoLayout, Failed }

    /// <summary>The terrain query BaseSpawner uses (WorldTerrainQueryProvider), or null when absent.</summary>
    public static FlakGrounding.HeightQuery FindTerrainQuery()
    {
        WorldTerrainQueryProvider provider = Object.FindAnyObjectByType<WorldTerrainQueryProvider>();
        return provider != null && provider.IsReady ? FlakGrounding.FromProvider(provider) : null;
    }

    public static BaseFlakDefence GetDefence(BaseController controller)
    {
        if (controller == null) return null;
        BaseFlakDefence defence = controller.GetComponent<BaseFlakDefence>();
        return defence != null && defence.IsBound ? defence : null;
    }

    /// <summary>
    /// Applies the starting state to one generated base (idempotent: a base that
    /// is already initialised, or already has constructed buildings, is never
    /// populated twice). Bases spawned from the original fully-built prefabs
    /// (no BaseFoundation) are left untouched.
    /// </summary>
    public static InitResult InitialiseBase(BaseController controller, ConstructionSettings settings,
        FlakGrounding.HeightQuery ground, out string error)
    {
        error = string.Empty;
        if (controller == null || controller.GetComponent<BaseFoundation>() == null || controller.GetComponent<BaseBuildingState>() == null)
        {
            return InitResult.NotAFoundation;
        }

        if (settings == null || !settings.IsComplete)
        {
            error = "construction settings missing";
            return InitResult.Failed;
        }

        BaseFoundation foundation = controller.GetComponent<BaseFoundation>();
        BaseBuildingState state = controller.GetComponent<BaseBuildingState>();
        BaseFlakDefence defence = controller.GetComponent<BaseFlakDefence>();
        if (defence != null && defence.InitialStateApplied)
        {
            return InitResult.AlreadyInitialised;
        }

        FlakDefensiveLayout layout = settings.FlakLayouts.ForBaseType(foundation.BaseType);
        if (layout == null)
        {
            error = "no approved Flak layout for " + foundation.BaseType;
            return InitResult.NoLayout;
        }

        if (defence == null) defence = controller.gameObject.AddComponent<BaseFlakDefence>();
        if (!defence.Bind(layout, out error))
        {
            return InitResult.Failed;
        }

        if (state.OccupiedCount == 0)
        {
            if (!state.ApplyInitialConfiguration(settings.InitialConfig, settings.BuildingCatalog, out error))
            {
                return InitResult.Failed;
            }

            if (ground != null)
            {
                foreach (BuildingSlot s in state.Slots)
                {
                    if (s != null && s.IsOccupied && s.Accepts(BuildingType.Flak))
                    {
                        FlakGrounding.Ground(s, controller.transform, ground, out _);
                    }
                }
            }
        }

        defence.MarkInitialStateApplied();
        defence.NotifyChanged();
        return InitResult.Initialised;
    }

    /// <summary>
    /// Constructs one Flak at the base's current Flak level (L1..L4: new Flaks inherit
    /// the level) in the next free approved position, or slotId. Null when refused.
    /// </summary>
    public static BuildingInstance TryConstructFlak(BaseController controller, string slotId = null)
    {
        BaseFlakDefence defence = GetDefence(controller);
        ConstructionSettings settings = ConstructionSettings.Load();
        if (defence == null || settings == null || !settings.IsComplete) return null;
        return defence.ConstructAtFlakLevel(settings.BuildingCatalog.Get(BuildingType.Flak), slotId, FindTerrainQuery());
    }

    /// <summary>Demolishes a constructed Flak (slotId, or the latest in construction order).</summary>
    public static bool TryDemolishFlak(BaseController controller, string slotId = null)
    {
        BaseFlakDefence defence = GetDefence(controller);
        if (defence == null) return false;
        if (slotId == null)
        {
            BuildingSlot latest = defence.LatestConstructedSlot();
            if (latest == null) return false;
            slotId = latest.SlotId;
        }

        return defence.Demolish(slotId);
    }

    // ------------------------------------------------------------- Flak upgrade

    /// <summary>
    /// What a collective Flak upgrade of this base would do right now, using the
    /// project's Flak definition and the shared ResourceManager. Read-only.
    /// </summary>
    public static FlakUpgradeQuote EvaluateFlakUpgrade(BaseController controller)
    {
        ConstructionSettings settings = ConstructionSettings.Load();
        BuildingDefinition flak = settings != null && settings.BuildingCatalog != null ? settings.BuildingCatalog.Get(BuildingType.Flak) : null;
        return EvaluateFlakUpgrade(controller, flak, ResourceManager.Instance);
    }

    /// <summary>
    /// Same as <see cref="EvaluateFlakUpgrade(BaseController)"/> with explicit dependencies.
    /// Checks, in order: base, initialisation, an upgrade already running, at least one
    /// Flak, below L4, next-level model registered, next upgrade's price approved,
    /// resource system, shared Gold.
    /// </summary>
    public static FlakUpgradeQuote EvaluateFlakUpgrade(BaseController controller, BuildingDefinition flakDefinition,
        ResourceManager resources)
    {
        FlakUpgradeQuote quote = new FlakUpgradeQuote { currentLevel = FlakUpgradeRules.StartingLevel };
        if (controller == null)
        {
            quote.status = FlakUpgradeResult.InvalidBase;
            return quote;
        }

        BaseFlakDefence defence = GetDefence(controller);
        if (defence == null)
        {
            quote.status = FlakUpgradeResult.NotInitialised;
            return quote;
        }

        quote.currentLevel = defence.FlakLevel;
        quote.flakCount = defence.ConstructedCount;
        quote.nextLevel = FlakUpgradeRules.NextLevel(quote.currentLevel);
        quote.goldCostKnown = FlakUpgradeRules.TryGetUpgradeCost(quote.currentLevel, out quote.goldCost);
        if (resources != null && resources.IsInitialized) resources.TryGetBalance(ResourceType.Gold, out quote.goldBalance);

        if (defence.IsUpgrading) quote.status = FlakUpgradeResult.UpgradeInProgress;
        else if (quote.flakCount <= 0) quote.status = FlakUpgradeResult.NoFlaks;
        else if (!FlakUpgradeRules.CanUpgradeFrom(quote.currentLevel)) quote.status = FlakUpgradeResult.AtMaximumLevel;
        else if (flakDefinition == null || flakDefinition.Type != BuildingType.Flak || !flakDefinition.HasLevel(quote.nextLevel))
            quote.status = FlakUpgradeResult.ModelMissing;
        else if (!quote.goldCostKnown) quote.status = FlakUpgradeResult.PriceNotSet;
        else if (resources == null || !resources.IsInitialized || !resources.IsConfigured(ResourceType.Gold))
            quote.status = FlakUpgradeResult.NoResourceSystem;
        else if (!resources.CanSpend(ResourceType.Gold, quote.goldCost)) quote.status = FlakUpgradeResult.InsufficientGold;
        else quote.status = FlakUpgradeResult.Available;
        return quote;
    }

    /// <summary>
    /// Buys the collective Flak upgrade of ONE base (L1 -> L2 -> L3 -> L4 at the prices in
    /// FlakUpgradeRules; L3 -> L4 is refused with PriceNotSet until its price is approved)
    /// from the shared player-wide Gold, using the project's Flak definition and
    /// ResourceManager.Instance.
    /// </summary>
    /// <param name="expectedCurrentLevel">
    /// Optional purchase guard: the level the caller showed the player. When it is not 0 and
    /// the base is no longer at that level (a repeated click after the upgrade already
    /// happened), nothing is bought and <see cref="FlakUpgradeResult.LevelChanged"/> is returned.
    /// </param>
    public static FlakUpgradeResult TryUpgradeFlaks(BaseController controller, int expectedCurrentLevel = 0)
    {
        ConstructionSettings settings = ConstructionSettings.Load();
        BuildingDefinition flak = settings != null && settings.BuildingCatalog != null ? settings.BuildingCatalog.Get(BuildingType.Flak) : null;
        return TryUpgradeFlaks(controller, flak, ResourceManager.Instance, expectedCurrentLevel);
    }

    /// <summary>
    /// The upgrade transaction with explicit dependencies (all or nothing):
    ///   1. validate (EvaluateFlakUpgrade) and the optional expected level;
    ///   2. mark the base as upgrading (re-entrant or duplicate purchases are refused);
    ///   3. spend the Gold from the shared ledger;
    ///   4. replace every Flak in place (BaseFlakDefence.ReplaceAllFlaks), which rolls
    ///      itself back completely on any failure;
    ///   5. on failure refund the Gold; on success raise BaseFlakDefence.Changed.
    /// </summary>
    public static FlakUpgradeResult TryUpgradeFlaks(BaseController controller, BuildingDefinition flakDefinition,
        ResourceManager resources, int expectedCurrentLevel = 0)
    {
        FlakUpgradeQuote quote = EvaluateFlakUpgrade(controller, flakDefinition, resources);
        if (quote.status == FlakUpgradeResult.InvalidBase || quote.status == FlakUpgradeResult.NotInitialised ||
            quote.status == FlakUpgradeResult.UpgradeInProgress)
        {
            return quote.status;
        }

        if (expectedCurrentLevel != 0 && expectedCurrentLevel != quote.currentLevel) return FlakUpgradeResult.LevelChanged;
        if (quote.status != FlakUpgradeResult.Available) return quote.status;

        BaseFlakDefence defence = GetDefence(controller);
        defence.IsUpgrading = true;
        bool spent = false;
        try
        {
            if (!resources.TrySpend(ResourceType.Gold, quote.goldCost, out ResourceTransactionResult spendResult))
            {
                Debug.LogWarning($"Flak upgrade of {controller.BaseId}: Gold spend refused ({spendResult}).", controller);
                return FlakUpgradeResult.SpendFailed;
            }

            spent = true;
            if (!defence.ReplaceAllFlaks(flakDefinition, quote.nextLevel, out int replaced, out string error))
            {
                Refund(resources, quote.goldCost, controller);
                spent = false;
                Debug.LogWarning($"Flak upgrade of {controller.BaseId} to L{quote.nextLevel} failed and was rolled back (Gold refunded): {error}", controller);
                return FlakUpgradeResult.ReplacementFailed;
            }

            spent = false; // committed
            defence.NotifyChanged(); // still marked upgrading: listeners cannot buy again re-entrantly
            return FlakUpgradeResult.Upgraded;
        }
        catch (System.Exception exception)
        {
            if (spent) Refund(resources, quote.goldCost, controller);
            Debug.LogException(exception, controller);
            return FlakUpgradeResult.ReplacementFailed;
        }
        finally
        {
            defence.IsUpgrading = false;
        }
    }

    private static void Refund(ResourceManager resources, long gold, BaseController controller)
    {
        // The amount was just spent, so it always fits under the capacity again.
        if (resources.Ledger == null || !resources.Ledger.TryAdd(ResourceType.Gold, gold, out ResourceTransactionResult result))
        {
            Debug.LogError($"Flak upgrade of {(controller != null ? controller.BaseId : "?")}: refund of {gold} Gold FAILED.", controller);
        }
    }

    // ------------------------------------------------------------- Flak construction (queued)

    /// <summary>The base's construction queue, or null when it has none yet.</summary>
    public static BaseFlakConstruction GetConstructionQueue(BaseController controller)
    {
        return controller != null ? controller.GetComponent<BaseFlakConstruction>() : null;
    }

    private static BuildingDefinition ProjectFlakDefinition()
    {
        ConstructionSettings settings = ConstructionSettings.Load();
        return settings != null && settings.BuildingCatalog != null ? settings.BuildingCatalog.Get(BuildingType.Flak) : null;
    }

    /// <summary>What starting one Flak construction on this base would do right now (read-only).</summary>
    public static FlakConstructionQuote EvaluateFlakConstruction(BaseController controller)
    {
        return EvaluateFlakConstruction(controller, ProjectFlakDefinition(), ResourceManager.Instance);
    }

    /// <summary>
    /// Same with explicit dependencies. Checks, in order: base, initialisation, an upgrade running,
    /// at least one Flak (a base with none has fallen), capacity (built + queued below 28 and a free
    /// position), the Flak number (#2 only as the conditional rebuild), a model for the base's Flak
    /// level, the resource system, then every resource against BOTH the player's storage capacity
    /// (#13..#28 need storage expanded by additional bases) and the current balance. Capacity is never
    /// spent; it only decides whether the price can ever be held. StorageInsufficient is reported in
    /// preference to InsufficientResources because waiting for replenishment cannot fix it.
    /// </summary>
    public static FlakConstructionQuote EvaluateFlakConstruction(BaseController controller, BuildingDefinition flakDefinition,
        ResourceManager resources)
    {
        FlakConstructionQuote quote = new FlakConstructionQuote
        {
            missing = new List<KeyValuePair<ResourceType, long>>(3),
            exceedsStorage = new List<KeyValuePair<ResourceType, long>>(3),
            flakLevel = FlakUpgradeRules.StartingLevel
        };

        if (controller == null)
        {
            quote.status = FlakConstructionResult.InvalidBase;
            return quote;
        }

        BaseFlakDefence defence = GetDefence(controller);
        if (defence == null)
        {
            quote.status = FlakConstructionResult.NotInitialised;
            return quote;
        }

        BaseFlakConstruction queue = controller.GetComponent<BaseFlakConstruction>();
        quote.builtCount = defence.ConstructedCount;
        quote.queuedCount = queue != null ? queue.Count : 0;
        quote.flakLevel = defence.FlakLevel;
        quote.currentRemainingSeconds = queue != null ? queue.CurrentRemainingSeconds : 0.0;
        quote.queueRemainingSeconds = queue != null ? queue.QueueRemainingSeconds : 0.0;
        if (resources != null && resources.IsInitialized)
        {
            resources.TryGetBalance(ResourceType.Concrete, out quote.concreteBalance);
            resources.TryGetBalance(ResourceType.Iron, out quote.ironBalance);
            resources.TryGetBalance(ResourceType.Aluminium, out quote.aluminiumBalance);
        }

        if (defence.IsUpgrading)
        {
            quote.status = FlakConstructionResult.UpgradeInProgress;
            return quote;
        }

        if (quote.builtCount <= 0)
        {
            quote.status = FlakConstructionResult.BaseDestroyed;
            return quote;
        }

        if (quote.builtCount + quote.queuedCount >= FlakConstructionRules.MaxFlaks || defence.AvailableCount <= 0)
        {
            quote.status = FlakConstructionResult.MaxCapacity;
            return quote;
        }

        quote.flakNumber = quote.builtCount + quote.queuedCount + 1;
        quote.isRebuild = FlakConstructionRules.IsRebuild(quote.flakNumber);
        if (!FlakConstructionRules.TryGetCost(quote.flakNumber, out quote.cost))
        {
            quote.status = FlakConstructionResult.MaxCapacity; // no cost outside #2..#28
            return quote;
        }

        // #2 is never an ordinary expansion: only a base reduced to exactly one Flak, nothing queued.
        if (quote.isRebuild && !FlakConstructionRules.IsRebuildEligible(quote.builtCount, quote.queuedCount))
        {
            quote.status = FlakConstructionResult.RebuildNotEligible;
            return quote;
        }

        if (flakDefinition == null || flakDefinition.Type != BuildingType.Flak || !flakDefinition.HasLevel(quote.flakLevel))
        {
            quote.status = FlakConstructionResult.ModelMissing;
            return quote;
        }

        if (resources == null || !resources.IsInitialized)
        {
            quote.status = FlakConstructionResult.NoResourceSystem;
            return quote;
        }

        foreach (KeyValuePair<ResourceType, long> amount in quote.cost.Amounts())
        {
            if (amount.Value <= 0) continue;
            if (!resources.IsConfigured(amount.Key))
            {
                quote.status = FlakConstructionResult.NoResourceSystem;
                return quote;
            }

            // Storage progression: the price must fit the current capacity (not deducted, only compared).
            if (resources.TryGetCapacity(amount.Key, out long capacity) && amount.Value > capacity)
            {
                quote.exceedsStorage.Add(new KeyValuePair<ResourceType, long>(amount.Key, amount.Value - capacity));
            }

            if (!resources.CanSpend(amount.Key, amount.Value))
            {
                quote.missing.Add(new KeyValuePair<ResourceType, long>(amount.Key, Math.Max(1, amount.Value - quote.Balance(amount.Key))));
            }
        }

        quote.status = quote.exceedsStorage.Count > 0 ? FlakConstructionResult.StorageInsufficient
            : quote.missing.Count > 0 ? FlakConstructionResult.InsufficientResources
            : FlakConstructionResult.Available;
        return quote;
    }

    /// <summary>
    /// Starts ONE Flak construction on the base with the shared resources and the project's Flak
    /// definition (see the overload).
    /// </summary>
    public static FlakConstructionResult TryStartFlakConstruction(BaseController controller, int expectedFlakNumber = 0)
    {
        return TryStartFlakConstruction(controller, ProjectFlakDefinition(), ResourceManager.Instance, expectedFlakNumber);
    }

    /// <summary>
    /// Starts ONE Flak construction (all or nothing): validates (EvaluateFlakConstruction), spends
    /// the Concrete, Iron and Aluminium of the next Flak number from the shared ledger, reserves the
    /// next free position in the approved construction order and queues the order on the base's
    /// BaseFlakConstruction. Any failure after spending refunds what was spent.
    /// </summary>
    /// <param name="expectedFlakNumber">
    /// Optional purchase guard: the Flak number the caller showed. When not 0 and the next number
    /// differs (a repeated click that was already served), nothing is bought.
    /// </param>
    public static FlakConstructionResult TryStartFlakConstruction(BaseController controller, BuildingDefinition flakDefinition,
        ResourceManager resources, int expectedFlakNumber = 0)
    {
        FlakConstructionQuote quote = EvaluateFlakConstruction(controller, flakDefinition, resources);
        if (quote.status == FlakConstructionResult.InvalidBase || quote.status == FlakConstructionResult.NotInitialised ||
            quote.status == FlakConstructionResult.UpgradeInProgress)
        {
            return quote.status;
        }

        if (expectedFlakNumber != 0 && quote.flakNumber != 0 && expectedFlakNumber != quote.flakNumber)
        {
            return FlakConstructionResult.FlakNumberChanged;
        }

        if (quote.status != FlakConstructionResult.Available) return quote.status;

        // Spend every resource, or nothing.
        var spent = new List<KeyValuePair<ResourceType, long>>(3);
        foreach (KeyValuePair<ResourceType, long> amount in quote.cost.Amounts())
        {
            if (amount.Value <= 0) continue;
            if (!resources.TrySpend(amount.Key, amount.Value, out ResourceTransactionResult spendResult))
            {
                foreach (KeyValuePair<ResourceType, long> back in spent) RefundAmount(resources, back.Key, back.Value, controller);
                Debug.LogWarning($"Flak construction on {controller.BaseId}: {amount.Key} spend refused ({spendResult}); nothing was taken.", controller);
                return FlakConstructionResult.SpendFailed;
            }

            spent.Add(amount);
        }

        BaseFlakDefence defence = GetDefence(controller);
        BuildingSlot slot = defence.ReserveNextFreeSlot();
        if (slot == null)
        {
            RefundConstruction(resources, quote.cost, controller);
            return FlakConstructionResult.ReservationFailed;
        }

        BaseFlakConstruction queue = controller.GetComponent<BaseFlakConstruction>();
        if (queue == null) queue = controller.gameObject.AddComponent<BaseFlakConstruction>();
        queue.Enqueue(slot.SlotId, quote.flakNumber, quote.cost);
        return FlakConstructionResult.Started;
    }

    /// <summary>
    /// Cancels a queued construction (default: the most recently started one), releases its reserved
    /// position and refunds what it cost to the shared ledger (up to each resource's capacity).
    /// </summary>
    public static bool TryCancelFlakConstruction(BaseController controller, int queueIndex = -1, ResourceManager resources = null)
    {
        BaseFlakDefence defence = GetDefence(controller);
        BaseFlakConstruction queue = GetConstructionQueue(controller);
        if (defence == null || queue == null || queue.Count == 0) return false;
        if (queueIndex < 0) queueIndex = queue.Count - 1;
        BaseFlakConstruction.Order removed = queue.RemoveAt(queueIndex);
        if (removed == null) return false;
        defence.ReleaseReservation(removed.SlotId);
        RefundConstruction(resources ?? ResourceManager.Instance, removed.Cost, controller);
        return true;
    }

    /// <summary>Gives a construction's resources back (each clamped to the free capacity).</summary>
    public static void RefundConstruction(ResourceManager resources, FlakConstructionCost cost, Object context)
    {
        foreach (KeyValuePair<ResourceType, long> amount in cost.Amounts())
        {
            if (amount.Value > 0) RefundAmount(resources, amount.Key, amount.Value, context);
        }
    }

    private static void RefundAmount(ResourceManager resources, ResourceType resource, long amount, Object context)
    {
        if (resources == null || resources.Ledger == null) return;
        long room = amount;
        if (resources.TryGetCapacity(resource, out long capacity) && resources.TryGetBalance(resource, out long balance))
        {
            room = Math.Min(amount, Math.Max(0, capacity - balance));
        }

        if (room > 0 && !resources.Ledger.TryAdd(resource, room, out ResourceTransactionResult result))
        {
            Debug.LogError($"Flak construction refund of {room} {resource} failed ({result}).", context);
        }
        else if (room < amount)
        {
            Debug.LogWarning($"Flak construction refund: {amount - room} {resource} exceeded the capacity and was not returned.", context);
        }
    }
}
