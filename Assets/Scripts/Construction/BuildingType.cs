/// <summary>
/// Stage 3A: the independently constructible building types of a base.
/// Factory and Refinery are ONE building in IRONVEIL (FactoryRefinery).
/// Values are persisted in assets; append new types at the end only.
/// </summary>
public enum BuildingType
{
    BaseCommand = 0,
    MilitaryCentral = 1,
    PowerPlant = 2,
    FactoryRefinery = 3,
    Flak = 4,
    ResourceOptimiser = 5,
    GroupExtension = 6,
    MilitaryOffice = 7
}

/// <summary>Which base family a foundation layout belongs to.</summary>
public enum BaseLayoutFamily
{
    /// <summary>Circular arena without helipad (Tank, Naval, Rail).</summary>
    Ground = 0,
    /// <summary>Circular arena with the structural Helipad (Heli, Heli Naval, Heli Rail).</summary>
    GroundHeli = 1,
    /// <summary>Futuristic circular Air structure (Air, Air Naval, Air Rail).</summary>
    Air = 2
}
