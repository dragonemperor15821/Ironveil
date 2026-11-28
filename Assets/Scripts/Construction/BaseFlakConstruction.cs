using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-base Flak construction queue (Build Additional Flaks, Row 1 Box 2). Lives on the base
/// root next to BaseFlakDefence, so every base has its own queue that progresses independently
/// and disappears with the base (New World). Session state only: there is no save system.
///
/// Orders are started (paid and given a reserved position) by BaseConstruction.TryStartFlakConstruction.
/// They are built ONE AT A TIME in the order they were started: only the first order advances, the
/// others wait. Time is scaled gameplay time (Time.deltaTime, like ResourceManager), so pausing
/// (Time.timeScale = 0) pauses construction; left-over time after a completion carries into the next.
///
/// Completion places the Flak through BaseFlakDefence.ConstructReserved at the base's CURRENT Flak
/// level, so a construction that finishes after a collective upgrade is built at the new level and a
/// base never mixes levels. While an upgrade runs, a finished order simply waits for the next tick.
/// If a completion fails (e.g. no model), the position is released and the resources refunded.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public sealed class BaseFlakConstruction : MonoBehaviour
{
    /// <summary>One queued Flak construction.</summary>
    [Serializable]
    public sealed class Order
    {
        [SerializeField] private string slotId;
        [SerializeField] private int flakNumber;
        [SerializeField] private FlakConstructionCost cost;
        [SerializeField] private double elapsedSeconds;

        internal Order(string slotId, int flakNumber, FlakConstructionCost cost)
        {
            this.slotId = slotId;
            this.flakNumber = flakNumber;
            this.cost = cost;
        }

        /// <summary>The reserved position this Flak will be built in.</summary>
        public string SlotId => slotId;
        public int FlakNumber => flakNumber;
        /// <summary>What was paid (refunded on cancellation or failure).</summary>
        public FlakConstructionCost Cost => cost;
        public double DurationSeconds => cost.durationSeconds;
        public double ElapsedSeconds { get => elapsedSeconds; internal set => elapsedSeconds = value; }
        public double RemainingSeconds => Math.Max(0.0, cost.durationSeconds - elapsedSeconds);
        public bool IsFinished => elapsedSeconds >= cost.durationSeconds;
        /// <summary>0..1 progress of this order.</summary>
        public float Progress => cost.durationSeconds <= 0 ? 1f : (float)Math.Min(1.0, elapsedSeconds / cost.durationSeconds);
    }

    private readonly List<Order> queue = new List<Order>();

    /// <summary>Raised when this base's queue changes (started, completed, cancelled, failed).</summary>
    public static event Action<BaseFlakConstruction> Changed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        Changed = null;
    }

    public IReadOnlyList<Order> Queue => queue;
    public int Count => queue.Count;
    public bool IsBuilding => queue.Count > 0;

    /// <summary>The order being built now (the first one), or null.</summary>
    public Order Current => queue.Count > 0 ? queue[0] : null;

    /// <summary>Seconds left on the order being built (0 when idle).</summary>
    public double CurrentRemainingSeconds => queue.Count > 0 ? queue[0].RemainingSeconds : 0.0;

    /// <summary>Seconds until every queued order is finished (they are built one after another).</summary>
    public double QueueRemainingSeconds
    {
        get
        {
            double total = 0.0;
            for (int i = 0; i < queue.Count; i++) total += queue[i].RemainingSeconds;
            return total;
        }
    }

    /// <summary>Most recent completion failure on this base, or null.</summary>
    public string LastFailure { get; private set; }

    internal Order Enqueue(string slotId, int flakNumber, FlakConstructionCost cost)
    {
        Order order = new Order(slotId, flakNumber, cost);
        queue.Add(order);
        RaiseChanged();
        return order;
    }

    internal Order RemoveAt(int index)
    {
        if (index < 0 || index >= queue.Count) return null;
        Order removed = queue[index];
        queue.RemoveAt(index);
        RaiseChanged();
        return removed;
    }

    private void Update()
    {
        Tick(Time.deltaTime);
    }

    /// <summary>
    /// Advances construction by <paramref name="seconds"/> of gameplay time and completes every
    /// order that finishes. Update calls it with Time.deltaTime; tests call it directly.
    /// Returns the number of Flaks completed.
    /// </summary>
    public int Tick(double seconds)
    {
        if (queue.Count == 0 || !(seconds >= 0.0) || double.IsInfinity(seconds)) return 0;

        BaseFlakDefence defence = GetComponent<BaseFlakDefence>();
        int completed = 0;
        bool changed = false;
        double budget = seconds;
        while (queue.Count > 0)
        {
            Order head = queue[0];
            if (!head.IsFinished)
            {
                double need = head.RemainingSeconds;
                if (budget < need)
                {
                    head.ElapsedSeconds += budget;
                    break;
                }

                budget -= need;
                head.ElapsedSeconds = head.DurationSeconds;
            }

            if (defence != null && defence.IsUpgrading) break; // finish after the upgrade, at the new level

            if (TryComplete(defence, head))
            {
                completed++;
            }

            queue.RemoveAt(0);
            changed = true;
        }

        if (changed) RaiseChanged();
        return completed;
    }

    private bool TryComplete(BaseFlakDefence defence, Order order)
    {
        ConstructionSettings settings = ConstructionSettings.Load();
        BuildingDefinition flak = settings != null && settings.BuildingCatalog != null ? settings.BuildingCatalog.Get(BuildingType.Flak) : null;
        BuildingInstance built = defence != null ? defence.ConstructReserved(flak, order.SlotId, BaseConstruction.FindTerrainQuery()) : null;
        if (built != null) return true;

        // Could not place it: give the position and the resources back.
        LastFailure = $"Flak #{order.FlakNumber} could not be built in {order.SlotId}; the position was released and the resources refunded.";
        if (defence != null) defence.ReleaseReservation(order.SlotId);
        BaseConstruction.RefundConstruction(Ironveil.Economy.ResourceManager.Instance, order.Cost, this);
        Debug.LogWarning("BaseFlakConstruction: " + LastFailure, this);
        return false;
    }

    private void RaiseChanged()
    {
        Action<BaseFlakConstruction> handlers = Changed;
        if (handlers == null) return;
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try { ((Action<BaseFlakConstruction>)d)(this); }
            catch (Exception e) { Debug.LogException(e, this); }
        }
    }
}
