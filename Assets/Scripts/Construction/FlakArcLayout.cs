using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stage 3A: definitive 28-position Flak layout (approved option "arcs outside
/// the wall"). Pure base-local math, no Unity objects.
///
/// Convention (unchanged from the original prefabs): the base front/entrance
/// is local +Z, the side the "Base Flaks" group sits on and the side
/// BaseSpawner turns towards a Naval/Rail hangar. Viewed from the base looking
/// out of the entrance, Right = +X and Left = -X.
///
/// Layout: on each side of the entrance two concentric rows (Row 1 inner,
/// Row 2 outer) of seven Flaks follow the outside of the wall. The first Flak
/// of every row sits at the smallest angle whose footprint stays out of the
/// front clear zone (Stage 3A.1: the hangar approach corridor |x - cx| <
/// clearHalfWidth, and nothing beyond frontLimitZ, the original footprint
/// front), and consecutive Flaks are exactly chordPitch apart (straight-line
/// distance). Rows are rowSeparation apart
/// radially. Left = exact mirror of Right about the entrance axis
/// x = centre.x (x mirrored, z equal, yaw negated). Each Flak is yawed to face
/// radially outward, which keeps the original turret orientation (facing the
/// front) rotated along the arc; the two entrance Flaks face about 12 degrees
/// off the front axis.
/// </summary>
public static class FlakArcLayout
{
    public const int SidesCount = 2;
    public const int RowsPerSide = 2;
    public const int FlaksPerRow = 7;
    public const int TotalPositions = SidesCount * RowsPerSide * FlaksPerRow; // 28

    [Serializable]
    public struct Parameters
    {
        [Tooltip("Arc centre in base-root XZ (the foundation structure's centre).")]
        public Vector2 centre;
        [Tooltip("Radius of Row 1 (inner) Flak centres, base-root units.")]
        public float innerRadius;
        [Tooltip("Radial distance between Row 1 and Row 2.")]
        public float rowSeparation;
        [Tooltip("Straight-line distance between consecutive Flak centres in a row.")]
        public float chordPitch;
        [Tooltip("|x - centre.x| of the first Flak of every row (minimum; the front clear zone may push rows further out).")]
        public float corridorOffset;

        [Header("Stage 3A.1 front clear zone (0 = disabled)")]
        [Tooltip("Half-width of the entrance/approach corridor that no Flak footprint may enter " +
                 "(covers every generated Naval/Rail hangar's full width plus margin).")]
        public float clearHalfWidth;
        [Tooltip("Base-root Z that no Flak footprint may pass (the original prefab footprint front, " +
                 "so no Flak enters the gap before a Naval/Rail hangar).")]
        public float frontLimitZ;
        [Tooltip("Half size of the (square) Flak slot footprint used by the clear-zone test.")]
        public float flakHalfExtent;
    }

    /// <summary>Half extent along X and Z of a square footprint of half size s yawed by phi.</summary>
    public static double RotatedHalfExtent(double s, double phi) => s * (Math.Abs(Math.Cos(phi)) + Math.Abs(Math.Sin(phi)));

    /// <summary>True when a Flak at angle phi on radius r keeps its footprint outside the front clear zone.</summary>
    public static bool ClearOfFrontZone(Parameters p, double r, double phi)
    {
        double e = RotatedHalfExtent(p.flakHalfExtent, phi);
        bool lateral = p.clearHalfWidth <= 0f || r * Math.Sin(phi) - e >= p.clearHalfWidth;
        bool front = p.frontLimitZ <= 0f || p.centre.y + r * Math.Cos(phi) + e <= p.frontLimitZ;
        return lateral && front;
    }

    /// <summary>Smallest angle from the front axis for the first Flak of a row on radius r.</summary>
    public static double FirstAngle(Parameters p, double r)
    {
        double phi = Math.Asin(Math.Min(1.0, p.corridorOffset / r));
        if (ClearOfFrontZone(p, r, phi)) return phi;
        // Both conditions only improve as phi grows towards the side; bisect to 1e-9 rad.
        double lo = phi, hi = Math.PI * 0.5;
        if (!ClearOfFrontZone(p, r, hi)) return double.NaN;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (ClearOfFrontZone(p, r, mid)) hi = mid; else lo = mid;
        }

        return hi;
    }

    public struct Position
    {
        public string slotId;
        public bool right;
        public int row;      // 1 = inner, 2 = outer
        public int index;    // 1..7, 1 nearest the entrance
        public Vector2 xz;   // base-root XZ
        public float yawDegrees;
        public float radius;
        public float angleFromFrontDegrees;
    }

    public static float RowRadius(Parameters p, int row) => p.innerRadius + (row - 1) * p.rowSeparation;

    public static string SlotId(bool right, int row, int index) =>
        string.Format("Flak_{0}{1}_{2:00}", right ? "R" : "L", row, index);

    /// <summary>All 28 positions: R1, R2, L1, L2, each ordered from the entrance outwards.</summary>
    public static List<Position> Generate(Parameters p)
    {
        List<Position> result = new List<Position>(TotalPositions);
        foreach (bool right in new[] { true, false })
        {
            for (int row = 1; row <= RowsPerSide; row++)
            {
                double r = RowRadius(p, row);
                double phi0 = FirstAngle(p, r);
                double step = 2.0 * Math.Asin(p.chordPitch / (2.0 * r));
                for (int k = 0; k < FlaksPerRow; k++)
                {
                    double phi = phi0 + k * step;
                    double side = right ? 1.0 : -1.0;
                    float x = (float)(p.centre.x + side * r * Math.Sin(phi));
                    float z = (float)(p.centre.y + r * Math.Cos(phi));
                    float deg = (float)(phi * 180.0 / Math.PI);
                    result.Add(new Position
                    {
                        slotId = SlotId(right, row, k + 1),
                        right = right,
                        row = row,
                        index = k + 1,
                        xz = new Vector2(x, z),
                        yawDegrees = right ? deg : -deg,
                        radius = (float)r,
                        angleFromFrontDegrees = deg
                    });
                }
            }
        }

        return result;
    }
}
