using System;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

public enum DdgiCookedEmissiveProfileRejection
{
    None,
    Missing,
    UnsupportedVersion,
    IncompleteSampling,
    Malformed,
    StaleTextureContent,
    StaleAlphaContract,
    StaleTextureBinding,
    StaleEmissionEligibility
}

/// <summary>
/// Authentication and factor-only scaling for cooked spatial emissive records.
/// Runtime never resamples a texture-wide mean onto individual triangles.
/// </summary>
public static class DdgiCookedEmissiveTransport
{
    public static bool TryValidateCompatibility(
        GiPrimitiveTransportProfile? profile,
        MaterialDefinition material,
        GiMaterialTransportProfile runtimeProfile,
        out DdgiCookedEmissiveProfileRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (profile is null)
        {
            rejection = DdgiCookedEmissiveProfileRejection.Missing;
            return false;
        }
        if (profile.SchemaVersion != GiPrimitiveTransportProfile.CurrentSchemaVersion ||
            profile.AlgorithmVersion != GiPrimitiveTransportProfile.CurrentAlgorithmVersion)
        {
            rejection = DdgiCookedEmissiveProfileRejection.UnsupportedVersion;
            return false;
        }
        if (!profile.EmissiveTriangleFlags.HasFlag(
                GiPrimitiveEmissiveTriangleFlags.SamplingComplete) ||
            !profile.EmissiveTriangleFlags.HasFlag(
                GiPrimitiveEmissiveTriangleFlags.Finite))
        {
            rejection = DdgiCookedEmissiveProfileRejection.IncompleteSampling;
            return false;
        }
        if (profile.Validate().Count != 0)
        {
            rejection = DdgiCookedEmissiveProfileRejection.Malformed;
            return false;
        }
        if (runtimeProfile.PrimitiveContentHash != profile.InputHash)
        {
            rejection = DdgiCookedEmissiveProfileRejection.Malformed;
            return false;
        }

        ulong expectedTextureHash = CombineTextureSourceHashes(profile.TextureSourceHashes);
        if (runtimeProfile.SourceContentHash != expectedTextureHash)
        {
            rejection = DdgiCookedEmissiveProfileRejection.StaleTextureContent;
            return false;
        }

        if ((ModelAlphaMode)material.AlphaMode != profile.CookedAlphaMode ||
            !SameFloat(material.BaseColorFactor.W, profile.CookedBaseAlphaFactor) ||
            !SameFloat(material.AlphaCutoff, profile.CookedAlphaCutoff))
        {
            rejection = DdgiCookedEmissiveProfileRejection.StaleAlphaContract;
            return false;
        }
        if (!BindingMatches(profile.BaseColorSamplingBinding, material.BaseColor) ||
            !BindingMatches(profile.EmissiveSamplingBinding, material.Emissive))
        {
            rejection = DdgiCookedEmissiveProfileRejection.StaleTextureBinding;
            return false;
        }
        if (profile.CookedEmissionEligible != material.EmitsIntoGi)
        {
            rejection = DdgiCookedEmissiveProfileRejection.StaleEmissionEligibility;
            return false;
        }

        rejection = DdgiCookedEmissiveProfileRejection.None;
        return true;
    }

    public static Vector3 EvaluateCoveredRadiance(
        GiPrimitiveEmissiveTriangleRecord record,
        MaterialDefinition material)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(material);
        float strength = EmissivePhotometry.ResolveSceneLinearScale(material);
        float coverage = (float)Math.Clamp(record.Coverage, 0.0, 1.0);
        return new Vector3(
            Math.Clamp(
                material.EmissiveFactor.X * strength *
                (float)record.CoveredMeanEmissiveTexture.X * coverage,
                0.0f,
                65504.0f),
            Math.Clamp(
                material.EmissiveFactor.Y * strength *
                (float)record.CoveredMeanEmissiveTexture.Y * coverage,
                0.0f,
                65504.0f),
            Math.Clamp(
                material.EmissiveFactor.Z * strength *
                (float)record.CoveredMeanEmissiveTexture.Z * coverage,
                0.0f,
                65504.0f));
    }

    /// <summary>
    /// Conservative current-energy upper bound for records omitted during
    /// cooking or runtime scanning. The factor-neutral cook importance uses
    /// luminance(texture); max(factor) bounds luminance(factor * texture).
    /// The Frobenius-squared transform bound covers arbitrary non-uniform
    /// scale and shear without under-reporting skipped emitted power.
    /// </summary>
    public static double BoundOmittedWorldImportance(
        double factorNeutralLocalImportance,
        MaterialDefinition material,
        Matrix4x4 worldMatrix,
        bool doubleSided)
    {
        if (!double.IsFinite(factorNeutralLocalImportance) ||
            factorNeutralLocalImportance <= 0.0)
        {
            return 0.0;
        }
        double maximumFactor = Math.Max(
            Math.Max(Math.Max(material.EmissiveFactor.X, 0.0f), Math.Max(material.EmissiveFactor.Y, 0.0f)),
            Math.Max(material.EmissiveFactor.Z, 0.0f));
        double strength = EmissivePhotometry.ResolveSceneLinearScale(material);
        double areaScaleUpperBound =
            worldMatrix.M11 * worldMatrix.M11 +
            worldMatrix.M12 * worldMatrix.M12 +
            worldMatrix.M13 * worldMatrix.M13 +
            worldMatrix.M21 * worldMatrix.M21 +
            worldMatrix.M22 * worldMatrix.M22 +
            worldMatrix.M23 * worldMatrix.M23 +
            worldMatrix.M31 * worldMatrix.M31 +
            worldMatrix.M32 * worldMatrix.M32 +
            worldMatrix.M33 * worldMatrix.M33;
        double sideWeight = doubleSided ? 2.0 : 1.0;
        double result = factorNeutralLocalImportance *
                        maximumFactor *
                        strength *
                        areaScaleUpperBound *
                        sideWeight;
        return double.IsFinite(result) ? Math.Max(result, 0.0) : double.MaxValue;
    }

    public static ulong CombineTextureSourceHashes(ReadOnlySpan<ulong> hashes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong combined = offset;
        foreach (ulong hash in hashes)
        {
            combined ^= hash;
            combined *= prime;
        }
        return combined;
    }

    private static bool BindingMatches(
        GiPrimitiveTextureBindingSnapshot expected,
        MaterialTextureBinding actual)
    {
        if (expected.IsBound != actual.IsBound)
            return false;
        if (!expected.IsBound)
            return true;
        return expected.TexCoordSet == actual.TexCoordSet &&
               expected.Offset.Equals(actual.Offset) &&
               expected.Scale.Equals(actual.Scale) &&
               SameFloat(expected.RotationRadians, actual.RotationRadians) &&
               expected.Sampler == actual.Sampler;
    }

    private static bool SameFloat(float value, double expected) =>
        double.IsFinite(expected) &&
        BitConverter.SingleToInt32Bits(value) ==
        BitConverter.SingleToInt32Bits((float)expected);
}
