using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base Management Menu (Phase 1A): registry of interactable generated bases,
/// keyed by unique Base ID.
///
/// Event-driven: targets register in OnEnable/Bind and unregister in
/// OnDisable/OnDestroy. Entries whose Unity object was destroyed without a
/// callback (edit-mode DestroyImmediate) are pruned on lookup, enumeration
/// and re-registration, so no destroyed reference is ever handed out and an
/// ID freed by regeneration can be reused by the new base.
///
/// Static state is reset at the start of every Play session because this
/// project runs with domain reload disabled.
/// </summary>
public static class BaseRegistry
{
    private static readonly Dictionary<string, BaseInteractionTarget> targets =
        new Dictionary<string, BaseInteractionTarget>(512, StringComparer.Ordinal);

    private static readonly List<string> pruneBuffer = new List<string>();

    /// <summary>Raised after a base is added.</summary>
    public static event Action<BaseInteractionTarget> Registered;

    /// <summary>Raised after a base is removed (the target may already be destroyed).</summary>
    public static event Action<string> Unregistered;

    /// <summary>Number of entries, including any not yet pruned. Call PruneDestroyed first for an exact live count.</summary>
    public static int Count => targets.Count;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetForPlaySession()
    {
        targets.Clear();
        Registered = null;
        Unregistered = null;
    }

    /// <summary>
    /// Adds the target under its Base ID. Re-registering the same target is a
    /// no-op. A different live target with the same ID is rejected (logged);
    /// a destroyed one is replaced.
    /// </summary>
    public static bool Register(BaseInteractionTarget target)
    {
        if (target == null || target.Context == null)
        {
            return false;
        }

        string id = target.Context.BaseId;
        if (string.IsNullOrEmpty(id))
        {
            Debug.LogWarning("BaseRegistry: refused a base without a Base ID.", target);
            return false;
        }

        if (targets.TryGetValue(id, out BaseInteractionTarget existing))
        {
            if (existing == target)
            {
                return true;
            }

            if (existing != null)
            {
                Debug.LogWarning(
                    $"BaseRegistry: duplicate Base ID '{id}' on '{target.name}'; " +
                    $"'{existing.name}' stays registered.", target);
                return false;
            }
        }

        targets[id] = target;
        Registered?.Invoke(target);
        return true;
    }

    /// <summary>Removes the target only if it is the one registered under its ID.</summary>
    public static bool Unregister(BaseInteractionTarget target)
    {
        if (ReferenceEquals(target, null) || target.Context == null)
        {
            return false;
        }

        string id = target.Context.BaseId;
        if (string.IsNullOrEmpty(id) ||
            !targets.TryGetValue(id, out BaseInteractionTarget existing) ||
            !ReferenceEquals(existing, target))
        {
            return false;
        }

        targets.Remove(id);
        Unregistered?.Invoke(id);
        return true;
    }

    /// <summary>Looks up a live base; a destroyed entry is pruned and reported as missing.</summary>
    public static bool TryGet(string baseId, out BaseInteractionTarget target)
    {
        target = null;
        if (string.IsNullOrEmpty(baseId) || !targets.TryGetValue(baseId, out BaseInteractionTarget found))
        {
            return false;
        }

        if (found == null)
        {
            targets.Remove(baseId);
            Unregistered?.Invoke(baseId);
            return false;
        }

        target = found;
        return true;
    }

    public static bool Contains(string baseId) => TryGet(baseId, out _);

    /// <summary>
    /// Fills <paramref name="results"/> (cleared first) with every live base.
    /// No allocation when the list has capacity. Order is unspecified.
    /// </summary>
    public static void GetAll(List<BaseInteractionTarget> results)
    {
        if (results == null)
        {
            throw new ArgumentNullException(nameof(results));
        }

        PruneDestroyed();
        results.Clear();
        foreach (BaseInteractionTarget target in targets.Values)
        {
            results.Add(target);
        }
    }

    /// <summary>Removes entries whose Unity object no longer exists. Returns how many were removed.</summary>
    public static int PruneDestroyed()
    {
        pruneBuffer.Clear();
        foreach (KeyValuePair<string, BaseInteractionTarget> pair in targets)
        {
            if (pair.Value == null)
            {
                pruneBuffer.Add(pair.Key);
            }
        }

        foreach (string id in pruneBuffer)
        {
            targets.Remove(id);
            Unregistered?.Invoke(id);
        }

        int removed = pruneBuffer.Count;
        pruneBuffer.Clear();
        return removed;
    }

    /// <summary>Drops every entry without touching the bases themselves.</summary>
    public static void Clear()
    {
        targets.Clear();
    }
}
