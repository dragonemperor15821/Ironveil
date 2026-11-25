using UnityEngine;

/// <summary>
/// Base Management Menu (Phase 1A): identity of one generated base instance.
///
/// Identifies the actual spawned base (its unique Base ID and its own scene
/// objects), not the prefab type. Base ID and type are captured once at bind
/// time; name and level are read live from the base's components so later
/// stages (renaming, upgrades) are reflected without rebinding.
/// </summary>
public sealed class BaseContext
{
    private readonly BaseLevel fallbackLevel;

    public BaseContext(BaseController controller, BaseLevelInfo levelInfo)
    {
        Controller = controller;
        LevelInfo = levelInfo;
        BaseObject = controller != null ? controller.gameObject : null;
        BaseId = controller != null ? controller.BaseId : string.Empty;
        BaseType = ResolveBaseType(controller, levelInfo);
        HasLevel = levelInfo != null;
        fallbackLevel = levelInfo != null ? levelInfo.Level : BaseLevel.L1;
    }

    /// <summary>Unique generated identity, e.g. BASE_HELINAVAL_004.</summary>
    public string BaseId { get; }

    /// <summary>Spawner variant name: Tank, Heli, Air, Naval, Rail, Heli Naval, ...</summary>
    public string BaseType { get; }

    /// <summary>The main base object carrying the BaseController (never the hangar).</summary>
    public GameObject BaseObject { get; }

    public BaseController Controller { get; }

    /// <summary>Null when the spawner ran with level assignment disabled.</summary>
    public BaseLevelInfo LevelInfo { get; }

    /// <summary>False when no BaseLevelInfo exists; Level then reports L1.</summary>
    public bool HasLevel { get; }

    public string BaseName => Controller != null ? Controller.BaseName : string.Empty;

    public BaseLevel Level => LevelInfo != null ? LevelInfo.Level : fallbackLevel;

    /// <summary>False once the underlying base has been destroyed.</summary>
    public bool IsAlive => Controller != null && BaseObject != null;

    public override string ToString()
    {
        return HasLevel
            ? $"{BaseId} ({BaseType}, {Level})"
            : $"{BaseId} ({BaseType})";
    }

    private static string ResolveBaseType(BaseController controller, BaseLevelInfo levelInfo)
    {
        if (levelInfo != null && !string.IsNullOrEmpty(levelInfo.BaseType))
        {
            return levelInfo.BaseType;
        }

        // Without level data the spawner still names every base "<Type> Base".
        string name = controller != null ? controller.BaseName : string.Empty;
        const string suffix = " Base";
        return name != null && name.EndsWith(suffix, System.StringComparison.Ordinal)
            ? name.Substring(0, name.Length - suffix.Length)
            : name ?? string.Empty;
    }
}
