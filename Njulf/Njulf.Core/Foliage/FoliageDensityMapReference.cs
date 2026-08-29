using Njulf.Core.Math;

namespace Njulf.Core.Foliage;

public enum FoliageDensityMapFormat : uint
{
    R8UNorm = 0,
    R16UNorm = 1
}

/// <summary>Serializable identity and sampling metadata for a density asset.</summary>
public sealed class FoliageDensityMapReference
{
    public string SourcePath { get; init; } = string.Empty;
    public string? ContentHash { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public FoliageDensityMapFormat Format { get; init; } = FoliageDensityMapFormat.R8UNorm;
    public Vector2 WorldToUvScale { get; init; } = Vector2.One;
    public Vector2 WorldToUvOffset { get; init; } = Vector2.Zero;
    public uint Revision { get; init; } = 1;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(SourcePath) &&
        Width > 0 && Height > 0 &&
        float.IsFinite(WorldToUvScale.X) &&
        float.IsFinite(WorldToUvScale.Y) &&
        float.IsFinite(WorldToUvOffset.X) &&
        float.IsFinite(WorldToUvOffset.Y);
}
