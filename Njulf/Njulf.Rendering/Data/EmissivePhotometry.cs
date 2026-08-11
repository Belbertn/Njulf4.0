using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Authoring convention for emissive strength. Values are converted to the
/// renderer's exposure-independent, scene-linear radiance before they reach
/// raster lighting, DDGI, or emissive-source selection.
/// </summary>
public enum EmissivePhotometricUnit
{
    /// <summary>
    /// Backwards-compatible glTF convention: strength directly multiplies the
    /// emissive factor and texture in scene-linear radiance units.
    /// </summary>
    SceneLinearRadiance = 0,

    /// <summary>
    /// Strength is the luminance, in cd/m² (nits), produced by the authored
    /// emissive factor when the emissive texture is white.
    /// </summary>
    LuminanceNits = 1
}

/// <summary>
/// Single source of truth for converting authored emission into the renderer's
/// linear-radiance convention. Exposure is deliberately absent from this API.
/// </summary>
public static class EmissivePhotometry
{
    /// <summary>
    /// Display-referred reference white used only as a unit bridge. One
    /// scene-linear luminance unit represents this many cd/m².
    /// </summary>
    public const float ReferenceWhiteLuminanceNits = 100f;

    public const float MinimumChromaticityLuminance = 1e-6f;

    public static float ResolveSceneLinearScale(MaterialDefinition material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return ResolveSceneLinearScale(
            material.EmissiveFactor,
            material.EmissiveStrength,
            material.EmissiveUnit,
            material.EmissiveArtisticMultiplier);
    }

    public static float ResolveSceneLinearScale(
        Vector3 emissiveFactor,
        float strength,
        EmissivePhotometricUnit unit,
        float artisticMultiplier)
    {
        ValidateFiniteNonNegative(strength, nameof(strength));
        ValidateFiniteNonNegative(artisticMultiplier, nameof(artisticMultiplier));
        if (!Enum.IsDefined(unit))
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown emissive photometric unit.");

        float authoredScale = unit switch
        {
            EmissivePhotometricUnit.SceneLinearRadiance => strength,
            EmissivePhotometricUnit.LuminanceNits => ResolveNitsScale(emissiveFactor, strength),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown emissive photometric unit.")
        };

        double resolved = (double)authoredScale * artisticMultiplier;
        if (!double.IsFinite(resolved) || resolved > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(artisticMultiplier),
                "The combined emissive photometric scale is not finite.");
        }
        return (float)resolved;
    }

    public static Vector3 EvaluateSceneLinearRadiance(
        MaterialDefinition material,
        Vector3 linearTexture)
    {
        ArgumentNullException.ThrowIfNull(material);
        return GiMaterialReferenceEvaluator.EvaluateEmission(
            material.EmissiveFactor,
            linearTexture,
            ResolveSceneLinearScale(material));
    }

    public static float SceneLinearLuminanceToNits(float luminance)
    {
        if (!float.IsFinite(luminance))
            throw new ArgumentOutOfRangeException(nameof(luminance), "Luminance must be finite.");
        return Math.Max(luminance, 0f) * ReferenceWhiteLuminanceNits;
    }

    public static float Luminance(Vector3 value) =>
        0.2126f * value.X + 0.7152f * value.Y + 0.0722f * value.Z;

    private static float ResolveNitsScale(Vector3 emissiveFactor, float luminanceNits)
    {
        if (!float.IsFinite(emissiveFactor.X) ||
            !float.IsFinite(emissiveFactor.Y) ||
            !float.IsFinite(emissiveFactor.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(emissiveFactor), "Emissive factor must be finite.");
        }

        float chromaticityLuminance = Luminance(new Vector3(
            Math.Max(emissiveFactor.X, 0f),
            Math.Max(emissiveFactor.Y, 0f),
            Math.Max(emissiveFactor.Z, 0f)));
        if (luminanceNits <= 0f || chromaticityLuminance <= MinimumChromaticityLuminance)
            return 0f;

        return luminanceNits /
               (ReferenceWhiteLuminanceNits * chromaticityLuminance);
    }

    private static void ValidateFiniteNonNegative(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Emissive photometric values must be finite and non-negative.");
        }
    }
}
