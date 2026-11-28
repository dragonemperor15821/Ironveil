using System;
using System.Collections.Generic;

/// <summary>
/// Base Management Menu: surviving Flak (base turret) count of every base,
/// keyed by the base's own BaseController instance.
///
/// Not keyed by Base ID: IDs are BASE_<TYPE>_<index> and are reused every time
/// a world is regenerated, so an ID key would carry a count over to a
/// different base. A destroyed or regenerated base simply has no entry.
///
/// Stage 3A2: a base built from a modular foundation carries a bound
/// BaseFlakDefence; its REAL constructed Flak count is the source of truth and
/// is returned directly (2 at start, 3 after one construction, ...). Only bases
/// without one (original fully-built prefabs, e.g. test scenes) fall back to
/// the recorded / <see cref="StartingFlaks"/> value. Cleared at the start of
/// every Play session.
///
/// Rules (shared by all nine base types):
/// - every base starts with 2 Flaks, at most 28;
/// - destroyed Flaks can be rebuilt, so with N survivors the next build is
///   turret N+1 (1 survivor -> BUILD 2ND BASE TURRET);
/// - 0 survivors means the base is dead.
/// </summary>
public static class BaseFlakRoster
{
    public const int StartingFlaks = 2;
    public const int MaxFlaks = 28;

    private static readonly Dictionary<BaseController, int> counts = new Dictionary<BaseController, int>();
    private static readonly List<BaseController> pruneBuffer = new List<BaseController>();

    /// <summary>Raised with the base whenever a stored count changes.</summary>
    public static event Action<BaseController> Changed;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        counts.Clear();
        Changed = null;
    }

    static BaseFlakRoster()
    {
        HookConstruction();
    }

    // Runs after BaseFlakDefence's own SubsystemRegistration reset, so the
    // subscription survives into every Play session (also without domain reload).
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    private static void HookConstruction()
    {
        BaseFlakDefence.Changed -= OnDefenceChanged;
        BaseFlakDefence.Changed += OnDefenceChanged;
    }

    /// <summary>
    /// A base's constructed Flaks changed: tell menu listeners about THAT base and
    /// refresh its hover bounds so new or removed buildings are included.
    /// </summary>
    private static void OnDefenceChanged(BaseFlakDefence defence)
    {
        BaseController controller = defence != null ? defence.GetComponent<BaseController>() : null;
        if (controller == null)
        {
            return;
        }

        BaseInteractionTarget target = controller.GetComponent<BaseInteractionTarget>();
        if (target != null && target.Context != null)
        {
            target.Bind(controller, controller.GetComponent<BaseLevelInfo>());
        }

        Changed?.Invoke(controller);
    }

    /// <summary>
    /// Constructed Flaks of the base: the real BaseFlakDefence count when the base
    /// has one; otherwise the recorded value or <see cref="StartingFlaks"/>.
    /// </summary>
    public static int GetSurvivingFlaks(BaseController controller)
    {
        if (controller == null)
        {
            return StartingFlaks;
        }

        BaseFlakDefence defence = BaseConstruction.GetDefence(controller);
        if (defence != null)
        {
            return defence.ConstructedCount;
        }

        return counts.TryGetValue(controller, out int count) ? count : StartingFlaks;
    }

    public static int GetSurvivingFlaks(BaseContext context)
    {
        return GetSurvivingFlaks(context != null ? context.Controller : null);
    }

    /// <summary>Records the surviving count (clamped to 0..MaxFlaks). For future gameplay systems and tests.</summary>
    public static void SetSurvivingFlaks(BaseController controller, int count)
    {
        if (controller == null)
        {
            return;
        }

        PruneDestroyed();
        int clamped = Math.Max(0, Math.Min(MaxFlaks, count));
        if (counts.TryGetValue(controller, out int previous) && previous == clamped)
        {
            return;
        }

        counts[controller] = clamped;
        Changed?.Invoke(controller);
    }

    public static void SetSurvivingFlaks(BaseContext context, int count)
    {
        SetSurvivingFlaks(context != null ? context.Controller : null, count);
    }

    /// <summary>Drops entries whose base object has been destroyed.</summary>
    private static void PruneDestroyed()
    {
        pruneBuffer.Clear();
        foreach (BaseController key in counts.Keys)
        {
            if (key == null) pruneBuffer.Add(key);
        }

        for (int i = 0; i < pruneBuffer.Count; i++)
        {
            counts.Remove(pruneBuffer[i]);
        }

        pruneBuffer.Clear();
    }

    /// <summary>Forgets every recorded count (all bases back to StartingFlaks).</summary>
    public static void Clear()
    {
        counts.Clear();
    }

    /// <summary>A base with no surviving Flak is dead.</summary>
    public static bool IsDead(int surviving) => surviving <= 0;

    /// <summary>True while another Flak can be built (alive and below capacity).</summary>
    public static bool CanBuild(int surviving) => surviving > 0 && surviving < MaxFlaks;

    /// <summary>The turret number the next build creates (surviving + 1), or 0 when none can be built.</summary>
    public static int NextTurretNumber(int surviving) => CanBuild(surviving) ? surviving + 1 : 0;

    /// <summary>1ST, 2ND, 3RD, 4TH ... 11TH, 12TH, 13TH ... 21ST, 22ND, 23RD ...</summary>
    public static string Ordinal(int number)
    {
        int mod100 = number % 100;
        string suffix;
        if (mod100 >= 11 && mod100 <= 13)
        {
            suffix = "TH";
        }
        else
        {
            switch (number % 10)
            {
                case 1: suffix = "ST"; break;
                case 2: suffix = "ND"; break;
                case 3: suffix = "RD"; break;
                default: suffix = "TH"; break;
            }
        }

        return number + suffix;
    }
}
