using UnityEngine;

/// <summary>
/// Runtime construction API for generated bases (no costs, timers, combat or
/// UI wiring; those are later stages). Every call works on ONE base's own
/// BaseFlakDefence; nothing is shared between bases.
///
///   InitialiseBase   - binds the base to its approved Flak layout and applies the
///                      starting state exactly once: 2 x L1 Flak (Flak_R1_01,
///                      Flak_L1_01) + 1 x L1 Refinery, grounded on the terrain.
///   TryConstructFlak - builds one L1 Flak in the next free approved position
///                      (construction order) or a given slot; the 29th is refused.
///   TryDemolishFlak  - removes a constructed Flak (a given slot, or the latest in
///                      construction order); its position becomes free again.
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

    /// <summary>Constructs one L1 Flak (next free approved position, or slotId). Null when refused.</summary>
    public static BuildingInstance TryConstructFlak(BaseController controller, string slotId = null)
    {
        BaseFlakDefence defence = GetDefence(controller);
        ConstructionSettings settings = ConstructionSettings.Load();
        if (defence == null || settings == null || !settings.IsComplete) return null;
        return defence.Construct(settings.BuildingCatalog.Get(BuildingType.Flak), 1, slotId, FindTerrainQuery());
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
}
