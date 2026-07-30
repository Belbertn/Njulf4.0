using System;

namespace Njulf.Rendering.Data;

/// <summary>
/// Authoritative CPU alpha-decision contract. Keep its comparison order
/// generated-equivalent to material_alpha.glsl.
/// </summary>
public static class MaterialAlphaCoverageContract
{
    /// <summary>
    /// Returns whether a sample contributes to a raster/compositing surface.
    /// BLEND remains visible for every strictly positive alpha.
    /// </summary>
    public static bool SurvivesRasterCoverage(
        float alpha,
        MaterialAlphaMode alphaMode,
        float alphaCutoff)
    {
        ValidateFinite(alpha, nameof(alpha));
        ValidateFinite(alphaCutoff, nameof(alphaCutoff));

        return alphaMode switch
        {
            MaterialAlphaMode.Mask => alpha >= alphaCutoff,
            MaterialAlphaMode.Blend => alpha > 0f,
            _ => true
        };
    }

    /// <summary>
    /// Returns whether a sample belongs to opaque geometric transport.
    /// Alpha-blended compositing surfaces are owned by the transparent path and
    /// therefore never become DDGI/far-field blockers.
    /// </summary>
    public static bool OccupiesOpaqueTransport(
        float alpha,
        MaterialAlphaMode alphaMode,
        float alphaCutoff)
    {
        ValidateFinite(alpha, nameof(alpha));
        ValidateFinite(alphaCutoff, nameof(alphaCutoff));

        return alphaMode switch
        {
            MaterialAlphaMode.Blend => false,
            MaterialAlphaMode.Mask => alpha >= alphaCutoff,
            _ => true
        };
    }

    private static void ValidateFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Material alpha inputs must be finite.");
    }
}
