using UnityEngine;

/// <summary>
/// Level data attached to a generated main base (next to its BaseController).
/// Data only: no combat behaviour. The visual model is chosen separately
/// (see BaseLevelVisualSet), so levels never require their own prefabs.
/// </summary>
[DisallowMultipleComponent]
public sealed class BaseLevelInfo : MonoBehaviour
{
    [SerializeField] private string baseId;
    [SerializeField] private string baseType;
    [SerializeField] private BaseLevel level = BaseLevel.L1;
    [SerializeField] private Vector3 spawnPosition;
    [SerializeField] private float spawnYawDegrees;

    public string BaseId => baseId;
    public string BaseType => baseType;
    public BaseLevel Level => level;
    public int LevelNumber => (int)level;
    public Vector3 SpawnPosition => spawnPosition;
    public float SpawnYawDegrees => spawnYawDegrees;

    public void Initialize(GeneratedBaseRecord record)
    {
        baseId = record.baseId;
        baseType = record.baseType;
        level = record.level;
        spawnPosition = record.position;
        spawnYawDegrees = record.yawDegrees;
    }
}
