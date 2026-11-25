using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base level L1..L6. Level is gameplay data, kept independent of the visual
/// model: every level may currently use the same imported (L6) model.
/// </summary>
public enum BaseLevel
{
    L1 = 1,
    L2 = 2,
    L3 = 3,
    L4 = 4,
    L5 = 5,
    L6 = 6
}

/// <summary>
/// Read-only record of one generated base (identity, type, level, pose).
/// </summary>
[Serializable]
public struct GeneratedBaseRecord
{
    public string baseId;
    public string baseType;
    public BaseLevel level;
    public Vector3 position;
    public float yawDegrees;
}

/// <summary>
/// Deterministic, seed-driven level assignment.
///
/// The assignment is a pure function of (seed, set of stable base IDs,
/// target counts). It never touches UnityEngine.Random, so terrain, routes
/// and base placement draw exactly the same random streams with or without
/// level assignment. Because it depends only on stable IDs (not on placement
/// order or positions), levels can be known before placement, which lets a
/// future per-level visual model influence its own footprint.
/// </summary>
public static class BaseLevelAssignment
{
    public const int LevelCount = 6;

    /// <summary>Balanced Stage 1 distribution for exactly 350 bases.</summary>
    public static readonly int[] DefaultTargetCounts350 = { 59, 58, 58, 59, 58, 58 };

    // Versioned salt: changing the algorithm must change this string so a
    // seed never silently maps to a different assignment under the same name.
    private const string AssignmentSalt = "IRONVEIL.BaseLevels.v1";

    /// <summary>
    /// Returns per-level target counts that sum to <paramref name="total"/>.
    /// When the template already sums to the total it is returned unchanged;
    /// otherwise it is scaled with a deterministic largest-remainder rule.
    /// </summary>
    public static int[] ResolveTargetCounts(int total, int[] template)
    {
        int[] counts = new int[LevelCount];
        if (total <= 0)
        {
            return counts;
        }

        int[] source = template != null && template.Length == LevelCount
            ? template
            : DefaultTargetCounts350;

        long templateSum = 0;
        for (int i = 0; i < LevelCount; i++)
        {
            templateSum += Math.Max(0, source[i]);
        }

        if (templateSum <= 0)
        {
            source = DefaultTargetCounts350;
            templateSum = 350;
        }

        if (templateSum == total)
        {
            for (int i = 0; i < LevelCount; i++)
            {
                counts[i] = Math.Max(0, source[i]);
            }

            return counts;
        }

        long[] remainders = new long[LevelCount];
        int assigned = 0;
        for (int i = 0; i < LevelCount; i++)
        {
            long scaled = (long)Math.Max(0, source[i]) * total;
            counts[i] = (int)(scaled / templateSum);
            remainders[i] = scaled % templateSum;
            assigned += counts[i];
        }

        while (assigned < total)
        {
            int best = 0;
            for (int i = 1; i < LevelCount; i++)
            {
                if (remainders[i] > remainders[best])
                {
                    best = i;
                }
            }

            counts[best]++;
            remainders[best] = -1;
            assigned++;
        }

        return counts;
    }

    /// <summary>
    /// Assigns a level to every ID. IDs are sorted ordinally first, so the
    /// result does not depend on the order in which they are supplied.
    /// </summary>
    public static Dictionary<string, BaseLevel> Assign(
        int seed,
        IEnumerable<string> stableBaseIds,
        int[] targetCounts)
    {
        List<string> ids = new List<string>(stableBaseIds);
        ids.Sort(StringComparer.Ordinal);

        int[] counts = ResolveTargetCounts(ids.Count, targetCounts);
        BaseLevel[] pool = new BaseLevel[ids.Count];
        int cursor = 0;
        for (int level = 0; level < LevelCount; level++)
        {
            for (int n = 0; n < counts[level]; n++)
            {
                pool[cursor++] = (BaseLevel)(level + 1);
            }
        }

        // Fisher-Yates with a private SplitMix64 stream (platform independent).
        ulong state = MixSeed(seed);
        for (int i = pool.Length - 1; i > 0; i--)
        {
            int j = (int)(NextUInt64(ref state) % (ulong)(i + 1));
            BaseLevel temp = pool[i];
            pool[i] = pool[j];
            pool[j] = temp;
        }

        Dictionary<string, BaseLevel> result =
            new Dictionary<string, BaseLevel>(ids.Count, StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++)
        {
            result[ids[i]] = pool[i];
        }

        return result;
    }

    private static ulong MixSeed(int seed)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < AssignmentSalt.Length; i++)
            {
                hash = (hash ^ AssignmentSalt[i]) * 1099511628211UL;
            }

            hash = (hash ^ (uint)seed) * 1099511628211UL;
            return hash;
        }
    }

    private static ulong NextUInt64(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
