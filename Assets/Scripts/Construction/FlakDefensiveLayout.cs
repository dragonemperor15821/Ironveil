using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The APPROVED 28-position Flak defensive layout of one base layout, frozen
/// exactly as previewed and approved (2 rows x 7 on each side of the entrance).
/// This is planned capacity only: it never instantiates anything. Constructed
/// Flaks are tracked by BaseFlakDefence / BaseBuildingState.
///
/// Coordinate convention (identical to the foundation prefabs):
///   - base-ROOT local space, in base-root units (the base root keeps its
///     runtime scale, 6.5 in MainBuild: world = local x 6.5 when unrotated);
///   - +Z = base front / entrance (towards a Naval/Rail hangar), +X = right
///     when looking out of the entrance, +Y = up;
///   - yawDegrees = rotation about +Y of the Flak's facing (radially outward);
///   - localPosition.y = foundation surface under the slot (0 = base-root
///     plane; final height comes from FlakGrounding at construction time).
/// One asset is shared by every base type with an identical layout.
/// </summary>
[CreateAssetMenu(menuName = "IRONVEIL/Construction/Flak Defensive Layout", fileName = "FlakDefensiveLayout")]
public sealed class FlakDefensiveLayout : ScriptableObject
{
    public const int RequiredPositions = 28;
    public const int RequiredPerSide = 14;
    public const int RequiredPerRow = 7;

    [Serializable]
    public struct Position
    {
        public string slotId;
        public bool right;
        [Range(1, 2)] public int row;            // 1 = inner, 2 = outer
        [Range(1, 7)] public int index;          // 1 = nearest the entrance
        public Vector3 localPosition;
        public float yawDegrees;
        [Tooltip("Exact approved slot rotation (yaw only); authoritative for rebuilds so they stay bit-identical.")]
        public Quaternion localRotation;
        [Tooltip("Default construction order 1..28 (inner rows first, entrance outwards, alternating right/left). " +
                 "Positions 1 and 2 are the two starting Flaks.")]
        public int constructionOrder;
    }

    [SerializeField] private string layoutName;
    [SerializeField] private string[] baseTypes = new string[0];
    [Tooltip("Generator parameters these positions were produced with (reference only; positions are authoritative).")]
    [SerializeField] private FlakArcLayout.Parameters sourceParameters;
    [SerializeField] private float instanceScale = 0.05f;
    [SerializeField] private Vector2 flakFootprint = new Vector2(0.05f, 0.05f);
    [SerializeField] private List<Position> positions = new List<Position>();

    public string LayoutName => layoutName;
    public IReadOnlyList<string> BaseTypes => baseTypes;
    public FlakArcLayout.Parameters SourceParameters => sourceParameters;
    public float InstanceScale => instanceScale;
    public Vector2 FlakFootprint => flakFootprint;
    public IReadOnlyList<Position> Positions => positions;
    public int Count => positions.Count;

    public bool TryGet(string slotId, out Position position)
    {
        for (int i = 0; i < positions.Count; i++)
        {
            if (positions[i].slotId == slotId)
            {
                position = positions[i];
                return true;
            }
        }

        position = default(Position);
        return false;
    }

    public int CountSide(bool right)
    {
        int n = 0;
        foreach (Position p in positions) if (p.right == right) n++;
        return n;
    }

    public int CountRow(bool right, int row)
    {
        int n = 0;
        foreach (Position p in positions) if (p.right == right && p.row == row) n++;
        return n;
    }

    /// <summary>Positions sorted by default construction order (1 first).</summary>
    public List<Position> InConstructionOrder()
    {
        List<Position> list = new List<Position>(positions);
        list.Sort((a, b) => a.constructionOrder.CompareTo(b.constructionOrder));
        return list;
    }

    public bool AppliesTo(string baseType)
    {
        foreach (string t in baseTypes) if (string.Equals(t, baseType, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Structural self-check: 28 unique positions, 14 per side, 7 per row, orders 1..28.</summary>
    public bool Validate(out string error)
    {
        error = string.Empty;
        if (positions.Count != RequiredPositions) { error = $"{positions.Count} positions (expected {RequiredPositions})"; return false; }
        HashSet<string> ids = new HashSet<string>();
        bool[] orders = new bool[RequiredPositions + 1];
        foreach (Position p in positions)
        {
            if (string.IsNullOrEmpty(p.slotId) || !ids.Add(p.slotId)) { error = "duplicate or empty slot id " + p.slotId; return false; }
            if (p.constructionOrder < 1 || p.constructionOrder > RequiredPositions || orders[p.constructionOrder])
            {
                error = "invalid construction order on " + p.slotId;
                return false;
            }

            orders[p.constructionOrder] = true;
        }

        foreach (bool right in new[] { true, false })
        {
            if (CountSide(right) != RequiredPerSide) { error = (right ? "right" : "left") + " side count " + CountSide(right); return false; }
            for (int row = 1; row <= 2; row++)
            {
                if (CountRow(right, row) != RequiredPerRow) { error = $"{(right ? "R" : "L")}{row} count {CountRow(right, row)}"; return false; }
            }
        }

        return true;
    }

#if UNITY_EDITOR
    public void EditorInitialize(string newName, string[] newBaseTypes, FlakArcLayout.Parameters newParameters,
        float newInstanceScale, Vector2 newFootprint, List<Position> newPositions)
    {
        layoutName = newName;
        baseTypes = newBaseTypes;
        sourceParameters = newParameters;
        instanceScale = newInstanceScale;
        flakFootprint = newFootprint;
        positions = newPositions;
    }
#endif
}
