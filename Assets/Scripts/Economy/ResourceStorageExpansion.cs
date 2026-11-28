using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ironveil.Economy
{
    /// <summary>
    /// Stage 8: the approved per-base storage contribution of a player-owned base, by the level of its
    /// Factory / Refinery (one building, BuildingType.FactoryRefinery). The single source of these values.
    ///
    ///   Resource    L1 contribution   additional L5 bonus   (L5 total)
    ///   Concrete        +10,000            +20,500            30,500
    ///   Iron             +5,000            +16,100            21,100
    ///   Aluminium        +2,000             +7,500             9,500
    ///   Fuel             +1,000             +5,000             6,000
    ///
    /// Only L1 and L5 are defined. L2..L4 have no approved values and no models in FactoryRefinery.asset, so a
    /// Factory / Refinery at any other level contributes nothing and is reported (never guessed).
    ///
    /// Stage 8B: the player's designated Home Base (PlayerHomeBase) also gets a SPECIAL bonus, only while its
    /// Factory / Refinery is at L5, on top of its normal L1 + L5 contribution:
    ///   Concrete +50,000   Iron +30,000   Aluminium +20,000   Fuel +5,000
    /// Ordinary owned bases never get it. Gold, Economy and Military storage are not affected. The starting storage
    /// is the capacity in HomeBaseResourceConfig (maps with a Home Base).
    /// </summary>
    public static class BaseStorageRules
    {
        public const int L1 = 1;
        public const int L5 = 5;

        /// <summary>The resources whose storage grows with owned bases, in application order.</summary>
        public static readonly IReadOnlyList<ResourceType> Resources = new[]
        {
            ResourceType.Concrete, ResourceType.Iron, ResourceType.Aluminium, ResourceType.Fuel,
        };

        /// <summary>True for the levels with approved values (L1, L5).</summary>
        public static bool IsSupportedLevel(int level) => level == L1 || level == L5;

        public static long L1Contribution(ResourceType resource)
        {
            switch (resource)
            {
                case ResourceType.Concrete: return 10_000;
                case ResourceType.Iron: return 5_000;
                case ResourceType.Aluminium: return 2_000;
                case ResourceType.Fuel: return 1_000;
                default: return 0;
            }
        }

        public static long L5Bonus(ResourceType resource)
        {
            switch (resource)
            {
                case ResourceType.Concrete: return 20_500;
                case ResourceType.Iron: return 16_100;
                case ResourceType.Aluminium: return 7_500;
                case ResourceType.Fuel: return 5_000;
                default: return 0;
            }
        }

        /// <summary>Special Home Base bonus (Home Base with its Factory / Refinery at L5 only).</summary>
        public static long HomeBaseL5Bonus(ResourceType resource)
        {
            switch (resource)
            {
                case ResourceType.Concrete: return 50_000;
                case ResourceType.Iron: return 30_000;
                case ResourceType.Aluminium: return 20_000;
                case ResourceType.Fuel: return 5_000;
                default: return 0;
            }
        }

        /// <summary>Storage one base adds with a Factory / Refinery at <paramref name="level"/>; false (0) for an unsupported level.</summary>
        public static bool TryGetContribution(int level, ResourceType resource, out long amount)
        {
            if (level == L1) { amount = L1Contribution(resource); return true; }
            if (level == L5) { amount = L1Contribution(resource) + L5Bonus(resource); return true; }
            amount = 0;
            return false;
        }
    }

    /// <summary>One base as the storage calculation sees it.</summary>
    public struct BaseStorageSource
    {
        public string baseId;
        public bool playerOwned;
        /// <summary>Highest level of the base's Factory / Refinery, or 0 when it is destroyed or absent.</summary>
        public int refineryLevel;
        /// <summary>Number of Factory / Refinery buildings found on the base (normally 0 or 1).</summary>
        public int refineryCount;
        /// <summary>True for the player's designated Home Base (special L5 bonus).</summary>
        public bool isHomeBase;

        public BaseStorageSource(string baseId, bool playerOwned, int refineryLevel, int refineryCount = -1, bool isHomeBase = false)
        {
            this.baseId = baseId;
            this.playerOwned = playerOwned;
            this.refineryLevel = refineryLevel;
            this.refineryCount = refineryCount < 0 ? (refineryLevel > 0 ? 1 : 0) : refineryCount;
            this.isHomeBase = isHomeBase;
        }
    }

    /// <summary>Result of one storage calculation (and, after Recalculate, of applying it).</summary>
    public sealed class StorageCalculation
    {
        private readonly long[] baseCapacity = new long[ResourceTypeRules.Count];
        private readonly long[] contribution = new long[ResourceTypeRules.Count];
        private readonly bool[] hasBase = new bool[ResourceTypeRules.Count];
        private readonly ResourceTransactionResult[] applyResult = new ResourceTransactionResult[ResourceTypeRules.Count];
        private readonly bool[] applied = new bool[ResourceTypeRules.Count];

        /// <summary>Player-owned bases counted, by base ID (each exactly once), in ordinal order.</summary>
        public readonly List<string> contributingBaseIds = new List<string>();
        /// <summary>Of those, the bases counted at L5.</summary>
        public int l5Count;
        /// <summary>Player-owned bases with no Factory / Refinery (destroyed or absent): no contribution.</summary>
        public readonly List<string> ownedWithoutRefinery = new List<string>();
        /// <summary>Player-owned bases whose Factory / Refinery level has no approved value: no contribution.</summary>
        public readonly List<string> unsupportedLevel = new List<string>();
        /// <summary>Base IDs seen more than once (counted once only).</summary>
        public readonly List<string> duplicateBaseIds = new List<string>();
        /// <summary>Bases with more than one Factory / Refinery (the highest supported level was counted).</summary>
        public readonly List<string> multipleRefineries = new List<string>();
        /// <summary>Bases without a usable base ID (not counted).</summary>
        public int missingIdCount;
        /// <summary>The Home Base counted (null when none is owned).</summary>
        public string homeBaseId;
        /// <summary>True when the special Home Base L5 bonus is included.</summary>
        public bool homeBaseBonusApplied;
        /// <summary>Further sources claiming to be the Home Base (ignored for the special bonus; at most one Home Base).</summary>
        public readonly List<string> extraHomeBaseIds = new List<string>();

        public int ContributingCount => contributingBaseIds.Count;

        internal void SetBase(ResourceType r, long value) { baseCapacity[(int)r] = value; hasBase[(int)r] = true; }
        internal void Add(ResourceType r, long value) => contribution[(int)r] += value;
        internal void SetApplied(ResourceType r, bool ok, ResourceTransactionResult result) { applied[(int)r] = ok; applyResult[(int)r] = result; }

        public bool HasBaseCapacity(ResourceType r) => hasBase[(int)r];
        public long BaseCapacity(ResourceType r) => baseCapacity[(int)r];
        public long Contribution(ResourceType r) => contribution[(int)r];
        /// <summary>The storage limit the player should have: Home Base starting capacity + every contribution.</summary>
        public long TargetCapacity(ResourceType r) => baseCapacity[(int)r] + contribution[(int)r];
        public bool Applied(ResourceType r) => applied[(int)r];
        public ResourceTransactionResult ApplyResult(ResourceType r) => applyResult[(int)r];

        public override string ToString()
        {
            var parts = new List<string>();
            foreach (ResourceType r in BaseStorageRules.Resources)
                parts.Add($"{r} {TargetCapacity(r):N0}");
            return $"{ContributingCount} owned bases ({l5Count} at L5{(homeBaseBonusApplied ? ", Home Base L5 bonus" : string.Empty)}): " + string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Stage 8: shared player storage = Home Base starting capacity (HomeBaseResourceConfig) + the
    /// BaseStorageRules contribution of every PLAYER-owned base (BaseController.OwnerId, counted once per
    /// BaseId) according to its Factory / Refinery (BaseBuildingState slot occupant). Applied to the ONE shared
    /// ResourceManager ledger through TrySetCapacity; balances are never changed by a recalculation.
    ///
    /// Recalculation is deterministic and idempotent (it always rebuilds the totals from the current scene) and
    /// runs on: BaseController.OwnerChanged, BaseController.BaseDestroyed (New World), a FactoryRefinery placed or
    /// removed through BuildingPlacement, and every new ledger (ResourceManager.LedgerAttached). Events about
    /// bases that are not player-owned are ignored: they cannot change the total.
    ///
    /// Limits are always applied. Storage behaviour (ResourceLedger.TrySetCapacity): a decrease preserves any excess
    /// balance and stops replenishment until spending brings the balance below the limit; an increase starts
    /// replenishment towards the new limit immediately.
    /// </summary>
    public static class ResourceStorageExpansion
    {
        public static bool IsAttached { get; private set; }
        public static StorageCalculation LastCalculation { get; private set; }
        public static int RecalculationCount { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            IsAttached = false;
            LastCalculation = null;
            RecalculationCount = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AttachForPlaySession() => Attach();

        /// <summary>Subscribes to the recalculation triggers (idempotent). Play sessions attach automatically.</summary>
        public static void Attach()
        {
            Detach();
            BaseController.OwnerChanged += OnOwnerChanged;
            BaseController.BaseDestroyed += OnBaseDestroyed;
            BuildingPlacement.Changed += OnBuildingChanged;
            ResourceManager.LedgerAttached += OnLedgerAttached;
            PlayerHomeBase.Changed += OnHomeBaseChanged;
            IsAttached = true;
        }

        public static void Detach()
        {
            BaseController.OwnerChanged -= OnOwnerChanged;
            BaseController.BaseDestroyed -= OnBaseDestroyed;
            BuildingPlacement.Changed -= OnBuildingChanged;
            ResourceManager.LedgerAttached -= OnLedgerAttached;
            PlayerHomeBase.Changed -= OnHomeBaseChanged;
            IsAttached = false;
        }

        private static void OnOwnerChanged(BaseController b, string from, string to)
        {
            if (from == BaseController.PlayerOwnerId || to == BaseController.PlayerOwnerId) Recalculate();
        }

        private static void OnBaseDestroyed(BaseController b)
        {
            if (b != null && b.IsPlayerOwned) Recalculate(ResourceManager.Instance, b);
        }

        private static void OnBuildingChanged(BuildingSlot slot, BuildingType type)
        {
            if (type != BuildingType.FactoryRefinery || slot == null) return;
            BaseController owner = slot.GetComponentInParent<BaseController>(true);
            if (owner != null && owner.IsPlayerOwned) Recalculate();
        }

        private static void OnLedgerAttached(ResourceManager manager) => Recalculate(manager);

        private static void OnHomeBaseChanged(BaseController homeBase) => Recalculate();

        // ------------------------------------------------------------ calculation

        /// <summary>Reads one base: its owner and its Factory / Refinery (highest level, 0 when none).</summary>
        public static BaseStorageSource ReadBase(BaseController controller)
        {
            int level = 0, count = 0;
            BaseBuildingState state = controller.GetComponent<BaseBuildingState>();
            if (state != null)
            {
                foreach (BuildingSlot slot in state.Slots)
                {
                    if (slot == null || !slot.IsOccupied || slot.Occupant.Type != BuildingType.FactoryRefinery) continue;
                    count++;
                    level = Better(level, slot.Occupant.BuildingLevel);
                }
            }
            return new BaseStorageSource(controller.BaseId, controller.IsPlayerOwned, level, count, PlayerHomeBase.IsHomeBase(controller));
        }

        // Prefer a supported level, then the higher one.
        private static int Better(int a, int b)
        {
            bool sa = BaseStorageRules.IsSupportedLevel(a), sb = BaseStorageRules.IsSupportedLevel(b);
            if (sa != sb) return sa ? a : b;
            return Math.Max(a, b);
        }

        /// <summary>
        /// Pure calculation over <paramref name="sources"/> on top of <paramref name="config"/>'s capacities (the Home
        /// Base starting storage). Order-independent: bases are counted once per ID.
        /// </summary>
        public static StorageCalculation Calculate(HomeBaseResourceConfig config, IEnumerable<BaseStorageSource> sources)
        {
            var result = new StorageCalculation();
            if (config != null)
            {
                foreach (ResourceConfigEntry entry in config.Entries)
                {
                    if (entry.HasCapacity && Contains(BaseStorageRules.Resources, entry.Resource))
                        result.SetBase(entry.Resource, entry.Capacity);
                }
            }

            // One record per base ID (duplicates merged deterministically, reported).
            var byId = new SortedDictionary<string, BaseStorageSource>(StringComparer.Ordinal);
            if (sources != null)
            {
                foreach (BaseStorageSource s in sources)
                {
                    if (!s.playerOwned) continue;
                    if (string.IsNullOrWhiteSpace(s.baseId)) { result.missingIdCount++; continue; }
                    if (byId.TryGetValue(s.baseId, out BaseStorageSource seen))
                    {
                        if (!result.duplicateBaseIds.Contains(s.baseId)) result.duplicateBaseIds.Add(s.baseId);
                        seen.refineryLevel = Better(seen.refineryLevel, s.refineryLevel);
                        seen.refineryCount = Math.Max(seen.refineryCount, s.refineryCount);
                        seen.isHomeBase |= s.isHomeBase;
                        byId[s.baseId] = seen;
                    }
                    else byId.Add(s.baseId, s);
                }
            }

            foreach (KeyValuePair<string, BaseStorageSource> kv in byId)
            {
                BaseStorageSource s = kv.Value;
                if (s.refineryCount > 1) result.multipleRefineries.Add(kv.Key);
                if (s.refineryLevel <= 0) { result.ownedWithoutRefinery.Add(kv.Key); continue; }
                if (!BaseStorageRules.IsSupportedLevel(s.refineryLevel)) { result.unsupportedLevel.Add(kv.Key); continue; }

                result.contributingBaseIds.Add(kv.Key);
                if (s.refineryLevel == BaseStorageRules.L5) result.l5Count++;
                foreach (ResourceType r in BaseStorageRules.Resources)
                {
                    BaseStorageRules.TryGetContribution(s.refineryLevel, r, out long amount);
                    result.Add(r, amount);
                }
            }

            // Special Home Base bonus: the one designated Home Base, owned, Factory / Refinery at L5.
            foreach (KeyValuePair<string, BaseStorageSource> kv in byId)
            {
                if (!kv.Value.isHomeBase) continue;
                if (result.homeBaseId != null) { result.extraHomeBaseIds.Add(kv.Key); continue; }
                result.homeBaseId = kv.Key;
                if (kv.Value.refineryLevel != BaseStorageRules.L5) continue;
                result.homeBaseBonusApplied = true;
                foreach (ResourceType r in BaseStorageRules.Resources) result.Add(r, BaseStorageRules.HomeBaseL5Bonus(r));
            }
            return result;
        }

        /// <summary>Calculation over every loaded base (excluding <paramref name="exclude"/>, e.g. one being destroyed).</summary>
        public static StorageCalculation CalculateFromScene(HomeBaseResourceConfig config, BaseController exclude = null)
        {
            BaseController[] all = UnityEngine.Object.FindObjectsByType<BaseController>(FindObjectsInactive.Include);
            var sources = new List<BaseStorageSource>(all.Length);
            foreach (BaseController c in all)
            {
                if (c == null || ReferenceEquals(c, exclude) || !c.IsPlayerOwned) continue;
                sources.Add(ReadBase(c));
            }
            return Calculate(config, sources);
        }

        /// <summary>
        /// Rebuilds the shared storage limits from the scene and applies them to <paramref name="manager"/> (default:
        /// the session's ResourceManager). Returns null when there is no initialised manager.
        /// </summary>
        public static StorageCalculation Recalculate(ResourceManager manager = null, BaseController exclude = null)
        {
            if (manager == null) manager = ResourceManager.Instance;
            if (manager == null || !manager.IsInitialized || manager.Config == null) return null;

            StorageCalculation calc = CalculateFromScene(manager.Config, exclude);
            Apply(manager, calc);
            LastCalculation = calc;
            RecalculationCount++;
            return calc;
        }

        /// <summary>Applies a calculation's targets to the manager's ledger (balances untouched).</summary>
        public static void Apply(ResourceManager manager, StorageCalculation calc)
        {
            foreach (ResourceType r in BaseStorageRules.Resources)
            {
                if (!calc.HasBaseCapacity(r) || !manager.IsConfigured(r)) continue;
                bool ok = manager.TrySetCapacity(r, calc.TargetCapacity(r), out ResourceTransactionResult result);
                calc.SetApplied(r, ok, result);
                if (!ok) Debug.LogWarning($"Storage expansion: {r} limit {calc.TargetCapacity(r):N0} not applied ({result}).", manager);
            }
        }

        private static bool Contains(IReadOnlyList<ResourceType> list, ResourceType r)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == r) return true;
            return false;
        }
    }
}
