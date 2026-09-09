using Njulf.Core.Math;

namespace Njulf.Core.Foliage;

/// <summary>Offline-baked foliage impostor atlas metadata.</summary>
public sealed class FoliageImpostorAsset
{
    public const int MaximumViewCount = 64;

    public string AlbedoOpacityAtlasPath { get; init; } = string.Empty;
    public string NormalAtlasPath { get; init; } = string.Empty;
    public string DepthAtlasPath { get; init; } = string.Empty;
    public int ViewCount { get; init; }
    public int AtlasWidth { get; init; }
    public int AtlasHeight { get; init; }
    public Vector3[] ViewDirections { get; init; } = [];
    public Vector4[] AtlasRectangles { get; init; } = [];
    public BoundingBox SourceBounds { get; init; }
    public Vector3 Pivot { get; init; }
    public float Scale { get; init; } = 1f;
    public string? ContentHash { get; init; }

    /// <summary>
    /// Older scene documents implied a horizontal row of evenly spaced views.
    /// New baked assets carry their exact source directions and normalized
    /// atlas rectangles; the renderer synthesizes the legacy layout only when
    /// both arrays are absent.
    /// </summary>
    public bool HasExplicitViewLayout =>
        ViewDirections.Length == ViewCount &&
        AtlasRectangles.Length == ViewCount &&
        ViewCount > 0;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(AlbedoOpacityAtlasPath) &&
        !string.IsNullOrWhiteSpace(NormalAtlasPath) &&
        !string.IsNullOrWhiteSpace(DepthAtlasPath) &&
        ViewCount is > 0 and <= MaximumViewCount &&
        HasValidViewLayout() &&
        float.IsFinite(Scale) && Scale > 0f &&
        IsFinite(SourceBounds.Min) && IsFinite(SourceBounds.Max) &&
        IsFinite(Pivot) &&
        SourceBounds.Max.X > SourceBounds.Min.X &&
        SourceBounds.Max.Y > SourceBounds.Min.Y &&
        SourceBounds.Max.Z > SourceBounds.Min.Z;

    private bool HasValidViewLayout()
    {
        bool legacy = ViewDirections.Length == 0 &&
            AtlasRectangles.Length == 0;
        if (legacy)
            return true;
        if (!HasExplicitViewLayout || AtlasWidth <= 0 || AtlasHeight <= 0)
            return false;

        for (int index = 0; index < ViewCount; index++)
        {
            Vector3 direction = ViewDirections[index];
            Vector4 rectangle = AtlasRectangles[index];
            if (!IsFinite(direction) || direction.LengthSquared() <= 1e-8f ||
                !IsFinite(rectangle) ||
                rectangle.X < 0f || rectangle.Y < 0f ||
                rectangle.Z <= 0f || rectangle.W <= 0f ||
                rectangle.X + rectangle.Z > 1.00001f ||
                rectangle.Y + rectangle.W > 1.00001f)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
