namespace Njulf.Core.Foliage;

/// <summary>Defines how a foliage patch creates spatial candidates.</summary>
public enum FoliagePlacementMode : uint
{
    SingleInstance = 0,
    DeterministicScatter = 1,
    ProceduralSurface = 2
}
