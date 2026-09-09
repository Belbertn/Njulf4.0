using Njulf.Core.Math;

namespace Njulf.Core.Foliage;

public readonly record struct TerrainFoliageSample(
    float Height,
    Vector3 Normal,
    float SlopeDegrees,
    uint BiomeId,
    bool IsWater,
    bool IsRoad,
    bool IsExcluded);

/// <summary>Small CPU boundary used by deterministic authored placement.</summary>
public interface ITerrainFoliageQuery
{
    ulong Revision { get; }

    bool TrySample(float worldX, float worldZ, out TerrainFoliageSample sample);
}
