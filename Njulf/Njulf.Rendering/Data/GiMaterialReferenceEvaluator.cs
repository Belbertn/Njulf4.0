using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Deterministic CPU oracle for the supported glTF diffuse-GI contract. Keep
/// constants and operation order synchronized with gi_material_transport.glsl.
/// </summary>
public static class GiMaterialReferenceEvaluator
{
    public const float Pi = 3.14159265358979323846f;
    // For Schlick Fresnel, the cosine-weighted hemispherical average of
    // 1 - F(NdotL) is (20 / 21) * (1 - F0).
    public const float SchlickCosineWeightedTransmission = 20f / 21f;
    public const float MinimumRoughness = 0.04f;
    public const float MaximumFiniteRadiance = 65_504f;

    public static GiSurfaceSample EvaluateSurface(
        MaterialDefinition definition,
        in GiMaterialSampleInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(definition);
        MaterialDefinition material = MaterialDefinitionValidator.ValidateAndNormalize(definition);

        Vector4 baseColor = Multiply(material.BaseColorFactor, inputs.BaseColorTexture, inputs.VertexColor);
        float metallic = Saturate(material.MetallicFactor * inputs.MetallicRoughnessTexture.Z);
        float roughness = Math.Clamp(
            material.RoughnessFactor * inputs.MetallicRoughnessTexture.Y,
            MinimumRoughness,
            1f);
        float occlusion = EvaluateMaterialOcclusion(material.OcclusionStrength, inputs.OcclusionTexture);
        float opacity = Saturate(baseColor.W);

        bool clearcoatEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Clearcoat);
        bool sheenEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Sheen);
        bool transmissionEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Transmission);
        bool specularEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Specular);
        bool iorEnabled =
            transmissionEnabled ||
            material.FeatureFlags.HasFlag(MaterialFeatureFlags.Ior);
        Vector3 directionalDiffuseBase = material.ReflectsIndirectDiffuse
            ? EvaluateDirectionalDiffuseBase(
                new Vector3(baseColor.X, baseColor.Y, baseColor.Z),
                metallic,
                transmissionEnabled ? material.Extensions.TransmissionFactor : 0f,
                clearcoatEnabled ? material.Extensions.ClearcoatFactor : 0f,
                sheenEnabled ? material.Extensions.SheenColorFactor : Vector3.Zero)
            : Vector3.Zero;
        Vector3 dielectricF0 = material.ReflectsIndirectDiffuse
            ? EvaluateMaterialDielectricF0(
                iorEnabled ? material.Extensions.Ior : 1.5f,
                specularEnabled ? material.Extensions.SpecularFactor : 1f,
                specularEnabled ? material.Extensions.SpecularColorFactor : Vector3.One)
            : Vector3.Zero;
        Vector3 diffuse = material.ReflectsIndirectDiffuse
            ? EvaluateHemisphericalDiffuseReflectance(
                new Vector3(baseColor.X, baseColor.Y, baseColor.Z),
                metallic,
                iorEnabled ? material.Extensions.Ior : 1.5f,
                specularEnabled ? material.Extensions.SpecularFactor : 1f,
                specularEnabled ? material.Extensions.SpecularColorFactor : Vector3.One,
                transmissionEnabled ? material.Extensions.TransmissionFactor : 0f,
                clearcoatEnabled ? material.Extensions.ClearcoatFactor : 0f,
                sheenEnabled ? material.Extensions.SheenColorFactor : Vector3.Zero,
                inputs.NdotV)
            : Vector3.Zero;
        Vector3 transmittedDiffuse = material.ReflectsIndirectDiffuse &&
                                     transmissionEnabled &&
                                     material.Extensions.TransmissionPolicy == GiTransmissionPolicy.ThinSurface
            ? EvaluateHemisphericalDiffuseTransmittance(
                new Vector3(baseColor.X, baseColor.Y, baseColor.Z),
                metallic,
                iorEnabled ? material.Extensions.Ior : 1.5f,
                specularEnabled ? material.Extensions.SpecularFactor : 1f,
                specularEnabled ? material.Extensions.SpecularColorFactor : Vector3.One,
                material.Extensions.TransmissionFactor,
                material.Extensions.ThinTransmissionTint,
                clearcoatEnabled ? material.Extensions.ClearcoatFactor : 0f,
                sheenEnabled ? material.Extensions.SheenColorFactor : Vector3.Zero,
                inputs.NdotV)
            : Vector3.Zero;

        Vector3 emission = material.EmitsIntoGi
            ? EvaluateEmission(material.EmissiveFactor, inputs.EmissiveTexture, material.EmissiveStrength)
            : Vector3.Zero;

        Vector3 geometricNormal = SafeNormal(inputs.GeometricNormal, Vector3.UnitY);
        Vector3 shadingNormal = SafeNormal(inputs.ShadingNormal, geometricNormal);
        shadingNormal = CorrectShadingNormal(geometricNormal, shadingNormal);

        GiMaterialTransportFlags flags = GiMaterialTransportFlags.DiffuseProfileValid |
                                         GiMaterialTransportFlags.EmissionProfileValid |
                                         GiMaterialTransportFlags.AlphaProfileValid;
        if (material.ShadingModel == MaterialShadingModel.Unlit)
            flags |= GiMaterialTransportFlags.Unlit;
        if (material.DoubleSided)
            flags |= GiMaterialTransportFlags.DoubleSided;
        if (material.EmitsIntoGi)
            flags |= GiMaterialTransportFlags.EmitsIntoGi;
        if (material.ReceivesIndirectDiffuse)
            flags |= GiMaterialTransportFlags.ReceivesIndirectDiffuse;
        if (material.ReflectsIndirectDiffuse)
            flags |= GiMaterialTransportFlags.ReflectsIndirectDiffuse;
        if (material.Extensions.TransmissionPolicy == GiTransmissionPolicy.ThinSurface &&
            material.Extensions.TransmissionFactor > 0f)
        {
            flags |= GiMaterialTransportFlags.ThinSurfaceTransmission |
                     GiMaterialTransportFlags.TransmissionProfileValid;
        }

        return new GiSurfaceSample(
            geometricNormal,
            geometricNormal,
            shadingNormal,
            directionalDiffuseBase,
            dielectricF0,
            Clamp01(diffuse),
            Clamp01(transmittedDiffuse),
            emission,
            occlusion,
            opacity,
            metallic,
            roughness,
            flags);
    }

    public static Vector3 EvaluateHemisphericalDiffuseTransmittance(
        Vector3 linearBaseColor,
        float metallic,
        float ior,
        float specularFactor,
        Vector3? specularColor,
        float transmission,
        Vector3 transmissionTint,
        float clearcoat = 0f,
        Vector3? sheenColor = null,
        float nDotV = 1f)
    {
        transmission = Saturate(transmission);
        if (transmission <= 0f)
            return Vector3.Zero;

        // The same canonical passive budget feeds both lobes. Reflection owns
        // (1-T), transmission owns T*tint, so their component-wise sum cannot
        // exceed the equivalent opaque response.
        Vector3 available = EvaluateHemisphericalDiffuseReflectance(
            linearBaseColor,
            metallic,
            ior,
            specularFactor,
            specularColor,
            0f,
            clearcoat,
            sheenColor,
            nDotV);
        return Clamp01(available * transmission * Clamp01(transmissionTint));
    }

    public static Vector3 EvaluateHemisphericalDiffuseReflectance(
        Vector3 linearBaseColor,
        float metallic,
        float ior = 1.5f,
        float specularFactor = 1f,
        Vector3? specularColor = null,
        float transmission = 0f,
        float clearcoat = 0f,
        Vector3? sheenColor = null,
        float nDotV = 1f)
    {
        nDotV = Saturate(nDotV);

        Vector3 directionalDiffuseBase = EvaluateDirectionalDiffuseBase(
            linearBaseColor,
            metallic,
            transmission,
            clearcoat,
            sheenColor);
        Vector3 f0 = EvaluateMaterialDielectricF0(
            ior,
            specularFactor,
            specularColor);
        Vector3 outgoingEnergy = Vector3.One - FresnelSchlick(f0, nDotV);
        Vector3 incomingHemisphericalEnergy =
            (Vector3.One - f0) * SchlickCosineWeightedTransmission;

        return Clamp01(
            directionalDiffuseBase *
            incomingHemisphericalEnergy *
            outgoingEnergy);
    }

    public static Vector3 EvaluateDirectionalDiffuseBase(
        Vector3 linearBaseColor,
        float metallic,
        float transmission = 0f,
        float clearcoat = 0f,
        Vector3? sheenColor = null)
    {
        linearBaseColor = Clamp01(linearBaseColor);
        metallic = Saturate(metallic);
        transmission = Saturate(transmission);
        clearcoat = Saturate(clearcoat);

        float clearcoatEnergy = 1f - clearcoat * 0.04f;
        Vector3 sheenEnergy = Vector3.One - Clamp01(sheenColor ?? Vector3.Zero);
        return Clamp01(
            linearBaseColor *
            (1f - metallic) *
            (1f - transmission) *
            clearcoatEnergy *
            sheenEnergy);
    }

    public static Vector3 EvaluateMaterialDielectricF0(
        float ior,
        float specularFactor = 1f,
        Vector3? specularColor = null)
    {
        specularFactor = Saturate(specularFactor);
        Vector3 tint = Clamp01(specularColor ?? Vector3.One);
        float dielectric = DielectricF0(Math.Clamp(ior, 1f, 3f)) * specularFactor;
        return Clamp01(tint * dielectric);
    }

    public static Vector3 EvaluateDiffuseBrdf(
        Vector3 linearBaseColor,
        float metallic,
        float nDotL,
        float nDotV,
        float ior = 1.5f,
        float specularFactor = 1f,
        Vector3? specularColor = null,
        float transmission = 0f,
        float clearcoat = 0f,
        Vector3? sheenColor = null)
    {
        if (nDotL <= 0f || nDotV <= 0f)
            return Vector3.Zero;

        Vector3 directionalDiffuseBase = EvaluateDirectionalDiffuseBase(
            linearBaseColor,
            metallic,
            transmission,
            clearcoat,
            sheenColor);
        Vector3 dielectricF0 = EvaluateMaterialDielectricF0(
            ior,
            specularFactor,
            specularColor);
        return EvaluateDiffuseBrdf(
            directionalDiffuseBase,
            dielectricF0,
            nDotL,
            nDotV);
    }

    public static Vector3 EvaluateDiffuseBrdf(
        Vector3 directionalDiffuseBase,
        Vector3 dielectricF0,
        float nDotL,
        float nDotV)
    {
        if (nDotL <= 0f || nDotV <= 0f)
            return Vector3.Zero;

        directionalDiffuseBase = Clamp01(directionalDiffuseBase);
        dielectricF0 = Clamp01(dielectricF0);
        Vector3 incomingEnergy =
            Vector3.One - FresnelSchlick(dielectricF0, nDotL);
        Vector3 outgoingEnergy =
            Vector3.One - FresnelSchlick(dielectricF0, nDotV);
        return Clamp01(
            directionalDiffuseBase * incomingEnergy * outgoingEnergy) / Pi;
    }

    public static Vector3 EvaluateDiffuseFromIrradiance(Vector3 irradiance, Vector3 diffuseReflectance)
    {
        return MaxZero(irradiance) * Clamp01(diffuseReflectance) / Pi;
    }

    public static Vector3 EvaluateEmission(Vector3 factor, Vector3 linearTexture, float strength)
    {
        EnsureFinite(factor, nameof(factor));
        EnsureFinite(linearTexture, nameof(linearTexture));
        EnsureFinite(strength, nameof(strength));
        if (strength < 0f)
            throw new ArgumentOutOfRangeException(nameof(strength), "Emissive strength must be non-negative.");

        Vector3 radiance = MaxZero(factor) * MaxZero(linearTexture) * strength;
        if (radiance.X > MaximumFiniteRadiance ||
            radiance.Y > MaximumFiniteRadiance ||
            radiance.Z > MaximumFiniteRadiance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strength),
                $"Compiled emissive radiance exceeds the format-safe {MaximumFiniteRadiance} bound.");
        }

        return radiance;
    }

    public static float EvaluateMaterialOcclusion(float strength, float sampleRed)
    {
        EnsureFinite(strength, nameof(strength));
        EnsureFinite(sampleRed, nameof(sampleRed));
        strength = Saturate(strength);
        sampleRed = Saturate(sampleRed);
        return 1f + strength * (sampleRed - 1f);
    }

    public static bool EvaluateOpacity(float alpha, MaterialAlphaMode alphaMode, float alphaCutoff)
    {
        return MaterialAlphaCoverageContract.SurvivesRasterCoverage(
            alpha,
            alphaMode,
            alphaCutoff);
    }

    public static bool EvaluateSidedness(bool doubleSided, bool frontFacing)
    {
        return doubleSided || frontFacing;
    }

    public static Vector3 CorrectShadingNormal(Vector3 geometricNormal, Vector3 shadingNormal)
    {
        geometricNormal = SafeNormal(geometricNormal, Vector3.UnitY);
        shadingNormal = SafeNormal(shadingNormal, geometricNormal);
        float hemisphere = Vector3.Dot(geometricNormal, shadingNormal);
        if (hemisphere <= 0f)
            return geometricNormal;

        // Limit grazing normal-map amplification while preserving the authored
        // direction over the valid geometric hemisphere.
        float minimumCosine = 0.1f;
        if (hemisphere >= minimumCosine)
            return shadingNormal;
        float blend = hemisphere / minimumCosine;
        return SafeNormal(geometricNormal * (1f - blend) + shadingNormal * blend, geometricNormal);
    }

    public static float DielectricF0(float ior)
    {
        EnsureFinite(ior, nameof(ior));
        ior = Math.Clamp(ior, 1f, 3f);
        float ratio = (ior - 1f) / (ior + 1f);
        return ratio * ratio;
    }

    private static Vector3 FresnelSchlick(Vector3 f0, float cosine)
    {
        float oneMinus = 1f - Saturate(cosine);
        float factor = oneMinus * oneMinus;
        factor *= factor * oneMinus;
        return f0 + (Vector3.One - f0) * factor;
    }

    private static Vector4 Multiply(Vector4 left, Vector4 middle, Vector4 right)
    {
        return new Vector4(
            left.X * middle.X * right.X,
            left.Y * middle.Y * right.Y,
            left.Z * middle.Z * right.Z,
            left.W * middle.W * right.W);
    }

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        EnsureFinite(value, nameof(value));
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : fallback;
    }

    private static Vector3 MaxZero(Vector3 value) => new(
        Math.Max(value.X, 0f),
        Math.Max(value.Y, 0f),
        Math.Max(value.Z, 0f));

    private static Vector3 Clamp01(Vector3 value) => Vector3.Clamp(value, Vector3.Zero, Vector3.One);

    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);

    private static void EnsureFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Material transport inputs must be finite.");
    }

    private static void EnsureFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(name, "Material transport inputs must be finite.");
    }
}

public readonly record struct GiMaterialSampleInputs(
    Vector4 BaseColorTexture,
    Vector4 VertexColor,
    Vector3 MetallicRoughnessTexture,
    float OcclusionTexture,
    Vector3 EmissiveTexture,
    Vector3 GeometricNormal,
    Vector3 ShadingNormal,
    float NdotV)
{
    public static GiMaterialSampleInputs Defaults { get; } = new(
        Vector4.One,
        Vector4.One,
        new Vector3(1f, 1f, 1f),
        1f,
        Vector3.One,
        Vector3.UnitY,
        Vector3.UnitY,
        1f);
}

public static class MaterialDefinitionValidator
{
    public static MaterialDefinition ValidateAndNormalize(MaterialDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateFinite(source);
        if (source.AlphaCutoff < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                "Material alpha cutoff must be non-negative.");
        }

        MaterialExtensionDefinition extension = source.Extensions with
        {
            ClearcoatFactor = Saturate(source.Extensions.ClearcoatFactor),
            ClearcoatRoughness = Saturate(source.Extensions.ClearcoatRoughness),
            ClearcoatNormalScale = Math.Clamp(source.Extensions.ClearcoatNormalScale, 0f, 4f),
            Clearcoat = ValidateBinding(source.Extensions.Clearcoat, "Extensions.Clearcoat"),
            ClearcoatRoughnessTexture = ValidateBinding(
                source.Extensions.ClearcoatRoughnessTexture,
                "Extensions.ClearcoatRoughnessTexture"),
            ClearcoatNormal = ValidateBinding(
                source.Extensions.ClearcoatNormal,
                "Extensions.ClearcoatNormal"),
            SheenColorFactor = Clamp01(source.Extensions.SheenColorFactor),
            SheenRoughness = Saturate(source.Extensions.SheenRoughness),
            SheenColor = ValidateBinding(source.Extensions.SheenColor, "Extensions.SheenColor"),
            SheenRoughnessTexture = ValidateBinding(
                source.Extensions.SheenRoughnessTexture,
                "Extensions.SheenRoughnessTexture"),
            AnisotropyStrength = Saturate(source.Extensions.AnisotropyStrength),
            Anisotropy = ValidateBinding(source.Extensions.Anisotropy, "Extensions.Anisotropy"),
            TransmissionFactor = Saturate(source.Extensions.TransmissionFactor),
            TransmissionPolicy = source.Extensions.TransmissionFactor <= 0f
                ? GiTransmissionPolicy.None
                : source.Extensions.TransmissionPolicy,
            ThinTransmissionTint = Clamp01(source.Extensions.ThinTransmissionTint),
            Ior = Math.Clamp(source.Extensions.Ior, 1f, 3f),
            ThicknessFactor = Math.Max(source.Extensions.ThicknessFactor, 0f),
            AttenuationDistance = float.IsPositiveInfinity(source.Extensions.AttenuationDistance)
                ? float.PositiveInfinity
                : Math.Max(source.Extensions.AttenuationDistance, 0f),
            AttenuationColor = Clamp01(source.Extensions.AttenuationColor),
            Transmission = ValidateBinding(source.Extensions.Transmission, "Extensions.Transmission"),
            Thickness = ValidateBinding(source.Extensions.Thickness, "Extensions.Thickness"),
            SpecularFactor = Saturate(source.Extensions.SpecularFactor),
            SpecularColorFactor = Clamp01(source.Extensions.SpecularColorFactor),
            Specular = ValidateBinding(source.Extensions.Specular, "Extensions.Specular"),
            SpecularColor = ValidateBinding(source.Extensions.SpecularColor, "Extensions.SpecularColor"),
            IridescenceFactor = Saturate(source.Extensions.IridescenceFactor),
            IridescenceIor = Math.Clamp(source.Extensions.IridescenceIor, 1f, 3f),
            IridescenceThicknessMinimum = Math.Max(source.Extensions.IridescenceThicknessMinimum, 0f),
            IridescenceThicknessMaximum = Math.Max(
                source.Extensions.IridescenceThicknessMaximum,
                Math.Max(source.Extensions.IridescenceThicknessMinimum, 0f)),
            Iridescence = ValidateBinding(source.Extensions.Iridescence, "Extensions.Iridescence"),
            IridescenceThickness = ValidateBinding(
                source.Extensions.IridescenceThickness,
                "Extensions.IridescenceThickness"),
            Dispersion = Math.Max(source.Extensions.Dispersion, 0f),
            SubsurfaceColor = Clamp01(source.Extensions.SubsurfaceColor),
            SubsurfaceStrength = Saturate(source.Extensions.SubsurfaceStrength),
            Subsurface = ValidateBinding(source.Extensions.Subsurface, "Extensions.Subsurface")
        };

        return source with
        {
            BaseColorFactor = Clamp01(source.BaseColorFactor),
            EmissiveFactor = Clamp01(source.EmissiveFactor),
            EmissiveStrength = Math.Max(source.EmissiveStrength, 0f),
            MetallicFactor = Saturate(source.MetallicFactor),
            // glTF authoring permits the full [0, 1] roughness range. Preserve
            // that authored value; the BRDF/transport evaluators apply their
            // numerical microfacet floor at the point of evaluation.
            RoughnessFactor = Saturate(source.RoughnessFactor),
            OcclusionStrength = Saturate(source.OcclusionStrength),
            NormalScale = Math.Clamp(source.NormalScale, 0f, 4f),
            // glTF MASK compares authored alpha against the authored cutoff
            // verbatim. Reject values below zero while preserving legal values
            // above one, which reject all normalized alpha.
            AlphaCutoff = source.AlphaCutoff,
            DecalDepthBias = Math.Clamp(source.DecalDepthBias, 0f, 0.01f),
            Extensions = extension,
            BaseColor = ValidateBinding(source.BaseColor, nameof(source.BaseColor)),
            Normal = ValidateBinding(source.Normal, nameof(source.Normal)),
            MetallicRoughness = ValidateBinding(source.MetallicRoughness, nameof(source.MetallicRoughness)),
            Occlusion = ValidateBinding(source.Occlusion, nameof(source.Occlusion)),
            Emissive = ValidateBinding(source.Emissive, nameof(source.Emissive))
        };
    }

    private static MaterialTextureBinding ValidateBinding(MaterialTextureBinding binding, string name)
    {
        ArgumentNullException.ThrowIfNull(binding, name);
        EnsureFinite(binding.Offset, $"{name}.Offset");
        EnsureFinite(binding.Scale, $"{name}.Scale");
        EnsureFinite(binding.RotationRadians, $"{name}.RotationRadians");
        if (binding.TexCoordSet is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name, "Only TEXCOORD_0 and TEXCOORD_1 are supported.");
        return binding;
    }

    private static void ValidateFinite(MaterialDefinition source)
    {
        EnsureFinite(source.BaseColorFactor, nameof(source.BaseColorFactor));
        EnsureFinite(source.EmissiveFactor, nameof(source.EmissiveFactor));
        EnsureFinite(source.EmissiveStrength, nameof(source.EmissiveStrength));
        EnsureFinite(source.MetallicFactor, nameof(source.MetallicFactor));
        EnsureFinite(source.RoughnessFactor, nameof(source.RoughnessFactor));
        EnsureFinite(source.OcclusionStrength, nameof(source.OcclusionStrength));
        EnsureFinite(source.NormalScale, nameof(source.NormalScale));
        EnsureFinite(source.AlphaCutoff, nameof(source.AlphaCutoff));
        EnsureFinite(source.DecalDepthBias, nameof(source.DecalDepthBias));

        MaterialExtensionDefinition extension = source.Extensions ??
            throw new ArgumentException("Material extension definition cannot be null.", nameof(source));
        EnsureFinite(extension.ClearcoatFactor, nameof(extension.ClearcoatFactor));
        EnsureFinite(extension.ClearcoatRoughness, nameof(extension.ClearcoatRoughness));
        EnsureFinite(extension.ClearcoatNormalScale, nameof(extension.ClearcoatNormalScale));
        EnsureFinite(extension.SheenColorFactor, nameof(extension.SheenColorFactor));
        EnsureFinite(extension.SheenRoughness, nameof(extension.SheenRoughness));
        EnsureFinite(extension.AnisotropyStrength, nameof(extension.AnisotropyStrength));
        EnsureFinite(extension.AnisotropyRotation, nameof(extension.AnisotropyRotation));
        EnsureFinite(extension.TransmissionFactor, nameof(extension.TransmissionFactor));
        EnsureFinite(extension.ThinTransmissionTint, nameof(extension.ThinTransmissionTint));
        EnsureFinite(extension.Ior, nameof(extension.Ior));
        EnsureFinite(extension.ThicknessFactor, nameof(extension.ThicknessFactor));
        if (!float.IsFinite(extension.AttenuationDistance) && !float.IsPositiveInfinity(extension.AttenuationDistance))
            throw new ArgumentOutOfRangeException(nameof(extension.AttenuationDistance), "Attenuation distance must be finite or positive infinity.");
        EnsureFinite(extension.AttenuationColor, nameof(extension.AttenuationColor));
        EnsureFinite(extension.SpecularFactor, nameof(extension.SpecularFactor));
        EnsureFinite(extension.SpecularColorFactor, nameof(extension.SpecularColorFactor));
        EnsureFinite(extension.IridescenceFactor, nameof(extension.IridescenceFactor));
        EnsureFinite(extension.IridescenceIor, nameof(extension.IridescenceIor));
        EnsureFinite(extension.IridescenceThicknessMinimum, nameof(extension.IridescenceThicknessMinimum));
        EnsureFinite(extension.IridescenceThicknessMaximum, nameof(extension.IridescenceThicknessMaximum));
        EnsureFinite(extension.Dispersion, nameof(extension.Dispersion));
        EnsureFinite(extension.SubsurfaceColor, nameof(extension.SubsurfaceColor));
        EnsureFinite(extension.SubsurfaceStrength, nameof(extension.SubsurfaceStrength));
    }

    private static Vector4 Clamp01(Vector4 value) => new(
        Saturate(value.X),
        Saturate(value.Y),
        Saturate(value.Z),
        Saturate(value.W));

    private static Vector3 Clamp01(Vector3 value) => Vector3.Clamp(value, Vector3.Zero, Vector3.One);
    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);

    private static void EnsureFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Material authored values must be finite.");
    }

    private static void EnsureFinite(Vector2 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(name, "Material authored values must be finite.");
    }

    private static void EnsureFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(name, "Material authored values must be finite.");
    }

    private static void EnsureFinite(Vector4 value, string name)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) ||
            !float.IsFinite(value.W))
        {
            throw new ArgumentOutOfRangeException(name, "Material authored values must be finite.");
        }
    }
}
