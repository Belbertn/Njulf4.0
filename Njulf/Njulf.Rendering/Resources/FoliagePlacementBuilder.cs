using Njulf.Core.Foliage;
using Njulf.Core.Math;

namespace Njulf.Rendering.Resources;

public readonly record struct FoliagePlacementCandidate(
    Vector3 Position,
    Vector3 SurfaceNormal,
    float Scale,
    float YawRadians,
    uint StableIdentity);

/// <summary>
/// Stateless stratified-jitter builder. Candidate identity is derived only
/// from patch/cell/prototype/seed inputs, never frame state.
/// </summary>
public static class FoliagePlacementBuilder
{
    public static IReadOnlyList<FoliagePlacementCandidate> Build(
        FoliagePatch patch,
        float globalDensityScale = 1f,
        Func<Vector2, float>? densitySampler = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.Visible || globalDensityScale <= 0f)
            return Array.Empty<FoliagePlacementCandidate>();
        if (patch.PlacementMode == FoliagePlacementMode.SingleInstance)
        {
            return
            [
                new FoliagePlacementCandidate(
                    patch.InstancePosition,
                    Vector3.UnitY,
                    patch.InstanceScale,
                    0f,
                    Hash(GuidHash(patch.Id), patch.Seed))
            ];
        }
        if (patch.PlacementMode != FoliagePlacementMode.DeterministicScatter)
            return Array.Empty<FoliagePlacementCandidate>();

        FoliagePlacementSettings settings = patch.Placement;
        float density = patch.Density * settings.Density * globalDensityScale;
        Vector3 size = patch.Bounds.Size;
        if (density <= 0f || size.X <= 0f || size.Z <= 0f)
            return Array.Empty<FoliagePlacementCandidate>();

        float densitySpacing = 1f / MathF.Sqrt(Math.Max(density, 0.000001f));
        float spacing = Math.Max(settings.MinimumSpacing, densitySpacing);
        int columns = Math.Max(1, (int)MathF.Ceiling(size.X / spacing));
        int rows = Math.Max(1, (int)MathF.Ceiling(size.Z / spacing));
        float cellX = size.X / columns;
        float cellZ = size.Z / rows;
        float acceptance = Math.Clamp(density * cellX * cellZ, 0f, 1f);
        long candidateCount = (long)columns * rows;
        var result = new List<FoliagePlacementCandidate>(
            (int)Math.Min(candidateCount, 1_000_000L));
        uint patchHash = GuidHash(patch.Id);

        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            uint cell = checked((uint)(row * columns + column));
            uint identity = Hash(
                Hash(patchHash, checked((uint)patch.Prototype.Id.GetHashCode())),
                Hash(settings.Seed ^ patch.Seed, cell));
            if (UnitFloat(Hash(identity, 0x9e3779b9u)) >= acceptance)
                continue;

            float jitterX = UnitFloat(Hash(identity, 1u));
            float jitterZ = UnitFloat(Hash(identity, 2u));
            float worldX = patch.Bounds.Min.X + (column + jitterX) * cellX;
            float worldZ = patch.Bounds.Min.Z + (row + jitterZ) * cellZ;
            if (densitySampler != null)
            {
                float sampledDensity = Math.Clamp(
                    densitySampler(new Vector2(worldX, worldZ)),
                    0f,
                    1f);
                if (UnitFloat(Hash(identity, 0x51ed270bu)) >= sampledDensity)
                    continue;
            }
            float worldY = patch.Bounds.Min.Y;
            Vector3 normal = Vector3.UnitY;
            if (patch.TerrainQuery is { } terrain &&
                terrain.TrySample(worldX, worldZ, out TerrainFoliageSample sample))
            {
                if (!PassesTerrain(settings, sample))
                    continue;
                worldY = sample.Height;
                normal = sample.Normal.LengthSquared() > 0.000001f
                    ? sample.Normal.Normalized()
                    : Vector3.UnitY;
            }

            float scale = Lerp(
                settings.ScaleRange.X,
                settings.ScaleRange.Y,
                UnitFloat(Hash(identity, 3u)));
            float yawDegrees = Lerp(
                settings.YawRangeDegrees.X,
                settings.YawRangeDegrees.Y,
                UnitFloat(Hash(identity, 4u)));
            result.Add(new FoliagePlacementCandidate(
                new Vector3(worldX, worldY, worldZ),
                normal,
                scale,
                yawDegrees * (MathF.PI / 180f),
                identity));
        }
        return result;
    }

    private static bool PassesTerrain(
        FoliagePlacementSettings settings,
        TerrainFoliageSample sample)
    {
        if (!float.IsFinite(sample.Height) ||
            sample.Height < settings.AltitudeRange.X ||
            sample.Height > settings.AltitudeRange.Y ||
            sample.SlopeDegrees < settings.SlopeRangeDegrees.X ||
            sample.SlopeDegrees > settings.SlopeRangeDegrees.Y)
            return false;
        if (sample.BiomeId < 32u &&
            (settings.BiomeMask & (1u << (int)sample.BiomeId)) == 0u)
            return false;
        return (settings.AllowWater || !sample.IsWater) &&
               (settings.AllowRoads || !sample.IsRoad) &&
               (!settings.RespectExclusions || !sample.IsExcluded);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float UnitFloat(uint value) =>
        (value & 0x00ff_ffffu) / 16_777_216f;

    private static uint GuidHash(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        uint hash = 2166136261u;
        foreach (byte item in bytes)
            hash = unchecked((hash ^ item) * 16777619u);
        return hash;
    }

    private static uint Hash(uint seed, uint value)
    {
        uint hash = unchecked((seed ^ value) * 0x9e3779b9u);
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x7feb352du);
        hash ^= hash >> 15;
        hash = unchecked(hash * 0x846ca68bu);
        return hash ^ (hash >> 16);
    }
}
