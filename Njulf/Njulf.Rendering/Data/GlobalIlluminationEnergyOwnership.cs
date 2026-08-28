using System;
using System.Numerics;

namespace Njulf.Rendering.Data;

/// <summary>
/// One receiver-side energy-ownership witness. Additive lighting domains are
/// deliberately not normalized against each other: direct light, emissive
/// radiance, and volumetric scattering describe different paths. The contract
/// instead proves that every exclusive estimator inside each domain is
/// normalized exactly once and that the surface BRDF remains passive.
/// </summary>
public readonly record struct GlobalIlluminationEnergyOwnershipSample(
    Vector3 DiffuseDirectionalAlbedo,
    Vector3 GlossyDirectionalAlbedo,
    float DirectSurfaceOwner,
    float DiffuseDdgiWeight,
    float DiffuseEnvironmentWeight,
    DdgiIndirectSpecularOwnership GlossyOwnership,
    float EmissiveSurfaceOwner,
    DdgiEmissiveEstimatorOwnership EmissiveTransportOwnership,
    float VolumetricDirectOwner,
    float VolumetricIndirectOwner,
    bool VolumetricOwnersSeparated);

public readonly record struct GlobalIlluminationEnergyOwnershipValidation(
    bool Passed,
    string FailureReason,
    float DiffuseOwnershipSum,
    float GlossyOwnershipSum,
    float MaximumSurfaceDirectionalAlbedo)
{
    public static GlobalIlluminationEnergyOwnershipValidation Failure(
        string reason,
        float diffuseSum = 0.0f,
        float glossySum = 0.0f,
        float maximumSurfaceAlbedo = 0.0f) => new(
            false,
            reason,
            diffuseSum,
            glossySum,
            maximumSurfaceAlbedo);
}

/// <summary>
/// CPU/reference qualification oracle for direct, diffuse-indirect,
/// glossy-indirect, emissive, and volumetric ownership. This is intentionally
/// independent of shader implementation so a duplicated source cannot make
/// both the renderer and its release gate pass in the same way.
/// </summary>
public static class GlobalIlluminationEnergyOwnershipContract
{
    public const float NormalizationTolerance = 1.0e-5f;

    public static GlobalIlluminationEnergyOwnershipValidation Validate(
        in GlobalIlluminationEnergyOwnershipSample sample)
    {
        if (!FiniteNonNegative(sample.DiffuseDirectionalAlbedo) ||
            !FiniteNonNegative(sample.GlossyDirectionalAlbedo))
        {
            return GlobalIlluminationEnergyOwnershipValidation.Failure(
                "surface-directional-albedo-non-finite-or-negative");
        }

        Vector3 totalDirectionalAlbedo =
            sample.DiffuseDirectionalAlbedo + sample.GlossyDirectionalAlbedo;
        float maximumSurfaceAlbedo = Math.Max(
            totalDirectionalAlbedo.X,
            Math.Max(totalDirectionalAlbedo.Y, totalDirectionalAlbedo.Z));
        if (!float.IsFinite(maximumSurfaceAlbedo) ||
            maximumSurfaceAlbedo > 1.0f + NormalizationTolerance)
        {
            return GlobalIlluminationEnergyOwnershipValidation.Failure(
                "surface-brdf-energy-exceeds-one",
                maximumSurfaceAlbedo: maximumSurfaceAlbedo);
        }

        if (!NormalizedOwner(sample.DirectSurfaceOwner))
        {
            return GlobalIlluminationEnergyOwnershipValidation.Failure(
                "direct-surface-owner-not-exclusive",
                maximumSurfaceAlbedo: maximumSurfaceAlbedo);
        }

        float diffuseSum = sample.DiffuseDdgiWeight +
            sample.DiffuseEnvironmentWeight;
        if (!UnitWeight(sample.DiffuseDdgiWeight) ||
            !UnitWeight(sample.DiffuseEnvironmentWeight) ||
            !NormalizedSum(diffuseSum))
        {
            return GlobalIlluminationEnergyOwnershipValidation.Failure(
                "diffuse-indirect-ownership-not-normalized",
                diffuseSum,
                maximumSurfaceAlbedo: maximumSurfaceAlbedo);
        }

        DdgiIndirectSpecularOwnership glossy = sample.GlossyOwnership;
        float glossySum = glossy.Sum;
        if (!UnitWeight(glossy.ScreenOrGeometricWeight) ||
            !UnitWeight(glossy.LocalReflectionProbeWeight) ||
            !UnitWeight(glossy.DdgiDirectionalRadianceWeight) ||
            !UnitWeight(glossy.EnvironmentWeight) ||
            !NormalizedSum(glossySum))
        {
            return GlobalIlluminationEnergyOwnershipValidation.Failure(
                "glossy-indirect-ownership-not-normalized",
                diffuseSum,
                glossySum,
                maximumSurfaceAlbedo);
        }

        if (!NormalizedOwner(sample.EmissiveSurfaceOwner) ||
            !DdgiEmissiveTransportContract.IsValid(
                sample.EmissiveTransportOwnership))
        {
            return GlobalIlluminationEnergyOwnershipValidation.Failure(
                "emissive-estimator-ownership-invalid",
                diffuseSum,
                glossySum,
                maximumSurfaceAlbedo);
        }

        if (!sample.VolumetricOwnersSeparated ||
            !NormalizedOwner(sample.VolumetricDirectOwner) ||
            !NormalizedOwner(sample.VolumetricIndirectOwner))
        {
            return GlobalIlluminationEnergyOwnershipValidation.Failure(
                "volumetric-direct-indirect-ownership-not-separated",
                diffuseSum,
                glossySum,
                maximumSurfaceAlbedo);
        }

        return new GlobalIlluminationEnergyOwnershipValidation(
            true,
            "valid",
            diffuseSum,
            glossySum,
            maximumSurfaceAlbedo);
    }

    private static bool FiniteNonNegative(Vector3 value) =>
        float.IsFinite(value.X) && value.X >= 0.0f &&
        float.IsFinite(value.Y) && value.Y >= 0.0f &&
        float.IsFinite(value.Z) && value.Z >= 0.0f;

    private static bool UnitWeight(float value) =>
        float.IsFinite(value) &&
        value >= -NormalizationTolerance &&
        value <= 1.0f + NormalizationTolerance;

    private static bool NormalizedOwner(float value) =>
        UnitWeight(value) && MathF.Abs(value - 1.0f) <= NormalizationTolerance;

    private static bool NormalizedSum(float value) =>
        float.IsFinite(value) &&
        MathF.Abs(value - 1.0f) <= NormalizationTolerance;
}
