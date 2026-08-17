using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;

namespace Njulf.Rendering.Resources;

public enum MaterialTextureSemantic
{
    BaseColor,
    Normal,
    MetallicRoughness,
    Occlusion,
    Emissive,
    LinearScalar,
    LinearColor,
    SrgbColor
}

/// <summary>
/// Resolved image data supplied to the pure material compiler. Runtime and
/// cooker adapters translate their statistics schema into this small contract.
/// </summary>
public readonly record struct MaterialTextureTransportInput(
    int BindlessIndex,
    bool MeanValid,
    Vector4 LinearMean,
    bool AlphaCoverageValid,
    float AlphaCoverage,
    bool NormalVarianceValid,
    float NormalVariance,
    ulong SourceContentHash,
    bool EmissiveLuminanceMaximumValid = false,
    float EmissiveLuminanceMaximum = 0f)
{
    public static MaterialTextureTransportInput Constant(int bindlessIndex, Vector4 value) => new(
        bindlessIndex,
        true,
        value,
        true,
        value.W,
        true,
        0f,
        0,
        true,
        0.2126f * value.X + 0.7152f * value.Y + 0.0722f * value.Z);
}

public sealed record MaterialCompilationContext
{
    public const uint CurrentAlgorithmVersion = 5;

    public Func<MaterialTextureBinding, MaterialTextureSemantic, MaterialTextureTransportInput>? ResolveTexture { get; init; }
    public GiMaterialTransportProfile? PrimitiveProfile { get; init; }
    public uint AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;
    public uint ProfileRevision { get; init; } = 1;
    public bool AllowInvalidCompactFallback { get; init; } = true;
}

public sealed record CompiledMaterialTransport(
    MaterialDefinition Definition,
    GPUMaterialData GpuMaterial,
    GPUMaterialExtensionData? ExtensionData,
    GiMaterialTransportProfile TransportProfile,
    MaterialRenderMetadata Metadata,
    IReadOnlyList<TextureHandle> TextureDependencies,
    uint ProfileRevision,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Pure authored-to-runtime compiler. All material creation and editing paths
/// use this implementation so derived raster, compact-GI, metadata and
/// dependency data are published from one transaction.
/// </summary>
public static class MaterialTransportCompiler
{
    public static CompiledMaterialTransport Compile(
        MaterialDefinition source,
        MaterialCompilationContext? context = null)
    {
        context ??= new MaterialCompilationContext();
        MaterialDefinition material = MaterialDefinitionValidator.ValidateAndNormalize(source);
        float effectiveEmissiveScale = EmissivePhotometry.ResolveSceneLinearScale(material);
        if (material.EmissiveStrength != 1f ||
            material.EmissiveUnit != EmissivePhotometricUnit.SceneLinearRadiance ||
            material.EmissiveArtisticMultiplier != 1f)
        {
            material = material with
            {
                FeatureFlags = material.FeatureFlags | MaterialFeatureFlags.EmissiveStrength
            };
        }
        var diagnostics = new List<string>();
        if (material.EmissiveArtisticMultiplier != 1f)
        {
            diagnostics.Add(
                $"Artistic emission multiplier changes physical emissive energy by " +
                $"{material.EmissiveArtisticMultiplier:0.####}x after photometric conversion.");
        }
        if (material.EmissiveUnit == EmissivePhotometricUnit.LuminanceNits &&
            material.EmissiveStrength > 0f &&
            EmissivePhotometry.Luminance(material.EmissiveFactor) <=
            EmissivePhotometry.MinimumChromaticityLuminance)
        {
            diagnostics.Add(
                "A positive luminance was authored with a black emissive color; resolved radiance is zero because no chromaticity is defined.");
        }

        MaterialTextureTransportInput baseColor = Resolve(
            material.BaseColor,
            MaterialTextureSemantic.BaseColor,
            context,
            BindlessIndex.DefaultWhiteTexture,
            Vector4.One);
        MaterialTextureTransportInput normal = Resolve(
            material.Normal,
            MaterialTextureSemantic.Normal,
            context,
            BindlessIndex.DefaultNormalTexture,
            new Vector4(0.5f, 0.5f, 1f, 1f));
        MaterialTextureTransportInput metallicRoughness = Resolve(
            material.MetallicRoughness,
            MaterialTextureSemantic.MetallicRoughness,
            context,
            BindlessIndex.DefaultBlackTexture,
            Vector4.One);
        MaterialTextureTransportInput occlusion = Resolve(
            material.Occlusion,
            MaterialTextureSemantic.Occlusion,
            context,
            BindlessIndex.DefaultWhiteTexture,
            Vector4.One);
        MaterialTextureTransportInput emissive = Resolve(
            material.Emissive,
            MaterialTextureSemantic.Emissive,
            context,
            BindlessIndex.DefaultWhiteTexture,
            Vector4.One);

        // Extension textures that change diffuse path energy participate in
        // the compact profile just like the core base/MR textures. Directional
        // specular-only extensions (anisotropy, iridescence, dispersion and
        // volume thickness) stay outside diffuse probe transport by policy.
        MaterialTextureTransportInput clearcoatEnergy = ResolveExtensionStatistics(
            material,
            MaterialFeatureFlags.ClearcoatTexture,
            material.Extensions.Clearcoat,
            MaterialTextureSemantic.LinearScalar,
            context);
        MaterialTextureTransportInput sheenEnergy = ResolveExtensionStatistics(
            material,
            MaterialFeatureFlags.SheenColorTexture,
            material.Extensions.SheenColor,
            MaterialTextureSemantic.SrgbColor,
            context);
        MaterialTextureTransportInput transmissionEnergy = ResolveExtensionStatistics(
            material,
            MaterialFeatureFlags.TransmissionTexture,
            material.Extensions.Transmission,
            MaterialTextureSemantic.LinearScalar,
            context);
        MaterialTextureTransportInput specularEnergy = ResolveExtensionStatistics(
            material,
            MaterialFeatureFlags.SpecularTexture,
            material.Extensions.Specular,
            MaterialTextureSemantic.LinearScalar,
            context);
        MaterialTextureTransportInput specularColorEnergy = ResolveExtensionStatistics(
            material,
            MaterialFeatureFlags.SpecularColorTexture,
            material.Extensions.SpecularColor,
            MaterialTextureSemantic.SrgbColor,
            context);

        bool diffuseStatisticsValid =
            (!material.BaseColor.IsBound || baseColor.MeanValid) &&
            (!material.MetallicRoughness.IsBound || metallicRoughness.MeanValid) &&
            clearcoatEnergy.MeanValid &&
            sheenEnergy.MeanValid &&
            transmissionEnergy.MeanValid &&
            specularEnergy.MeanValid &&
            specularColorEnergy.MeanValid;
        bool emissionStatisticsValid = !material.Emissive.IsBound || emissive.MeanValid;
        bool alphaStatisticsValid = !material.BaseColor.IsBound ||
                                    baseColor.MeanValid && baseColor.AlphaCoverageValid;
        bool normalStatisticsValid = !material.Normal.IsBound ||
                                     normal.NormalVarianceValid;
        bool occlusionStatisticsValid = !material.Occlusion.IsBound || occlusion.MeanValid;

        Vector4 meanBase = Multiply(material.BaseColorFactor, baseColor.MeanValid ? baseColor.LinearMean : Vector4.One);
        float meanMetallic = Math.Clamp(
            material.MetallicFactor * (metallicRoughness.MeanValid ? metallicRoughness.LinearMean.Z : 1f),
            0f,
            1f);
        float meanRoughness = Math.Clamp(
            material.RoughnessFactor * (metallicRoughness.MeanValid ? metallicRoughness.LinearMean.Y : 1f),
            GiMaterialReferenceEvaluator.MinimumRoughness,
            1f);
        float meanOcclusion = GiMaterialReferenceEvaluator.EvaluateMaterialOcclusion(
            material.OcclusionStrength,
            occlusion.MeanValid ? occlusion.LinearMean.X : 1f);
        float alphaCoverage = ResolveAlphaCoverage(material, baseColor);

        bool clearcoatEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Clearcoat);
        bool sheenEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Sheen);
        bool transmissionEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Transmission);
        bool specularEnabled = material.FeatureFlags.HasFlag(MaterialFeatureFlags.Specular);
        bool iorEnabled =
            transmissionEnabled ||
            material.FeatureFlags.HasFlag(MaterialFeatureFlags.Ior);
        float meanClearcoat = clearcoatEnabled
            ? material.Extensions.ClearcoatFactor * (clearcoatEnergy.MeanValid ? clearcoatEnergy.LinearMean.X : 1f)
            : 0f;
        Vector3 meanSheenColor = sheenEnabled
            ? material.Extensions.SheenColorFactor * ToVector3(
                sheenEnergy.MeanValid ? sheenEnergy.LinearMean : Vector4.One)
            : Vector3.Zero;
        float meanTransmission = transmissionEnabled &&
            material.Extensions.TransmissionPolicy == GiTransmissionPolicy.ThinSurface
            ? material.Extensions.TransmissionFactor * (transmissionEnergy.MeanValid ? transmissionEnergy.LinearMean.X : 1f)
            : 0f;
        float meanSpecularFactor = specularEnabled
            ? material.Extensions.SpecularFactor * (specularEnergy.MeanValid ? specularEnergy.LinearMean.W : 1f)
            : 1f;
        Vector3 meanSpecularColor = specularEnabled
            ? material.Extensions.SpecularColorFactor * ToVector3(
                specularColorEnergy.MeanValid ? specularColorEnergy.LinearMean : Vector4.One)
            : Vector3.One;
        Vector3 meanDielectricF0 = material.ReflectsIndirectDiffuse
            ? GiMaterialReferenceEvaluator.EvaluateMaterialDielectricF0(
                iorEnabled ? material.Extensions.Ior : 1.5f,
                meanSpecularFactor,
                meanSpecularColor)
            : Vector3.Zero;

        Vector3 meanDiffuse = material.ReflectsIndirectDiffuse
            ? GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseReflectance(
                new Vector3(meanBase.X, meanBase.Y, meanBase.Z),
                meanMetallic,
                iorEnabled ? material.Extensions.Ior : 1.5f,
                meanSpecularFactor,
                meanSpecularColor,
                meanTransmission,
                meanClearcoat,
                meanSheenColor,
                1f)
            : Vector3.Zero;
        Vector3 meanTransmittedDiffuse =
            material.ReflectsIndirectDiffuse &&
            transmissionEnabled &&
            material.Extensions.TransmissionPolicy == GiTransmissionPolicy.ThinSurface
                ? GiMaterialReferenceEvaluator.EvaluateHemisphericalDiffuseTransmittance(
                    new Vector3(meanBase.X, meanBase.Y, meanBase.Z),
                    meanMetallic,
                    iorEnabled ? material.Extensions.Ior : 1.5f,
                    meanSpecularFactor,
                    meanSpecularColor,
                    meanTransmission,
                    material.Extensions.ThinTransmissionTint,
                    meanClearcoat,
                    meanSheenColor,
                    1f)
                : Vector3.Zero;

        Vector3 meanEmission = material.EmitsIntoGi
            ? EmissivePhotometry.EvaluateSceneLinearRadiance(
                material,
                emissive.MeanValid
                    ? new Vector3(emissive.LinearMean.X, emissive.LinearMean.Y, emissive.LinearMean.Z)
                    : Vector3.One)
            : Vector3.Zero;

        GiTransportProfileQuality quality = ResolveProfileQuality(
            material,
            context.PrimitiveProfile,
            diffuseStatisticsValid,
            emissionStatisticsValid,
            alphaStatisticsValid,
            occlusionStatisticsValid,
            normalStatisticsValid);
        GiMaterialTransportFlags flags = BuildTransportFlags(
            material,
            diffuseStatisticsValid,
            emissionStatisticsValid,
            alphaStatisticsValid,
            occlusionStatisticsValid,
            normalStatisticsValid,
            quality);

        if (context.PrimitiveProfile is { } primitive &&
            primitive.Quality == GiTransportProfileQuality.PrimitiveSurfaceSampling)
        {
            if (primitive.Has(GiMaterialTransportFlags.DiffuseProfileValid))
                meanDiffuse = primitive.MeanDiffuseReflectance;
            if (primitive.Has(GiMaterialTransportFlags.TransmissionProfileValid))
                meanTransmittedDiffuse = primitive.MeanTransmittedDiffuseReflectance;
            if (primitive.Has(GiMaterialTransportFlags.EmissionProfileValid))
                meanEmission = primitive.MeanEmissiveRadiance;
            if (primitive.Has(GiMaterialTransportFlags.AlphaProfileValid))
                alphaCoverage = primitive.AlphaCoverage;
            if (primitive.Has(GiMaterialTransportFlags.BaseStatisticsValid))
            {
                meanOcclusion = primitive.MeanMaterialOcclusion;
                meanMetallic = primitive.MeanMetallic;
                meanRoughness = primitive.MeanRoughness;
            }
            flags |= primitive.Flags &
                     (GiMaterialTransportFlags.BaseStatisticsValid |
                      GiMaterialTransportFlags.DiffuseProfileValid |
                      GiMaterialTransportFlags.EmissionProfileValid |
                      GiMaterialTransportFlags.AlphaProfileValid |
                      GiMaterialTransportFlags.NormalProfileValid |
                      GiMaterialTransportFlags.TransmissionProfileValid);
        }

        // Compact directional lighting needs the pre-Fresnel base share and
        // dielectric F0 independently. MeanDiffuseReflectance is authoritative
        // (including primitive-profile correlation), so reconstruct the base at
        // the profile's documented NdotV=1 reference. The reference contains
        // both the outgoing Schlick transmission and its cosine-weighted
        // incoming hemispherical average. Six binary16 values fit
        // the material ABI's existing 12-byte alignment region.
        Vector3 meanDirectionalDiffuseBase = material.ReflectsIndirectDiffuse
            ? RecoverDirectionalDiffuseBase(meanDiffuse, meanDielectricF0)
            : Vector3.Zero;

        bool compactProfileValid = IsCompactProfileValid(material, flags);
        if (!compactProfileValid &&
            context.AllowInvalidCompactFallback &&
            HasAnyDetailedGiTexture(material))
        {
            flags |= GiMaterialTransportFlags.CompactTextureFallback;
            diagnostics.Add(
                "Compact transport is incomplete; coarse DDGI hits will sample the detailed texture bindings as the configured correctness fallback.");
        }

        if (!diffuseStatisticsValid)
            diagnostics.Add("Diffuse compact statistics are invalid; detailed cascades must sample textures or use the configured correctness fallback.");
        if (!emissionStatisticsValid)
            diagnostics.Add("Emission compact statistics are invalid; zero is retained as a physical value and is not used as a missing-data sentinel.");
        if (!alphaStatisticsValid)
            diagnostics.Add("Alpha coverage statistics are invalid; compact visibility must use texture alpha.");
        if (!occlusionStatisticsValid)
            diagnostics.Add("Occlusion statistics are invalid; compact material AO falls back to neutral.");
        if (material.FeatureFlags.HasFlag(MaterialFeatureFlags.Transmission) &&
            material.Extensions.TransmissionFactor > 0f &&
            material.Extensions.TransmissionPolicy == GiTransmissionPolicy.Unsupported)
        {
            diagnostics.Add("Transmission GI is unsupported for this material; transmitted energy was removed from opaque diffuse transport.");
            flags |= GiMaterialTransportFlags.UnsupportedTransmission;
        }
        if (material.FeatureFlags.HasFlag(MaterialFeatureFlags.Anisotropy))
            diagnostics.Add("Anisotropy is classified as directional specular; diffuse probes preserve base diffuse energy and do not transport anisotropic lobes.");
        if (material.FeatureFlags.HasFlag(MaterialFeatureFlags.Iridescence))
            diagnostics.Add("Iridescence is classified as directional specular and remains owned by raster/reflection lighting.");
        if (material.FeatureFlags.HasFlag(MaterialFeatureFlags.Dispersion))
            diagnostics.Add("Dispersion is classified as directional transmission and is excluded from diffuse probe transport.");
        if (material.FeatureFlags.HasFlag(MaterialFeatureFlags.VolumeApproximation))
            diagnostics.Add("Volume thickness and attenuation are owned by the explicit transmission approximation, not opaque diffuse probes.");
        if (material.FeatureFlags.HasFlag(MaterialFeatureFlags.Subsurface))
            diagnostics.Add("Subsurface is a receiver-side bounded approximation; diffuse probes transport the canonical base-layer response only.");

        uint packedFlags = (uint)flags |
                           ((uint)quality << (int)GiMaterialTransportFlags.QualityShift);
        float preferredLod = HasAnyDetailedGiTexture(material) ? 0f : 0f;
        var gpu = new GPUMaterialData
        {
            Albedo = material.BaseColorFactor,
            Emissive = new Vector4(material.EmissiveFactor, 1f),
            NormalScaleBias = new Vector4(
                material.NormalScale,
                (float)material.AlphaMode,
                material.AlphaCutoff,
                material.DoubleSided ? 1f : 0f),
            MetallicRoughnessAO = new Vector4(
                material.MetallicFactor,
                material.RoughnessFactor,
                material.OcclusionStrength,
                BindingsEquivalent(material.MetallicRoughness, material.Occlusion) ? 1f : 0f),
            BaseColorOffsetScale = ToOffsetScale(material.BaseColor),
            NormalOffsetScale = ToOffsetScale(material.Normal),
            MetallicRoughnessOffsetScale = ToOffsetScale(material.MetallicRoughness),
            OcclusionOffsetScale = ToOffsetScale(material.Occlusion),
            EmissiveOffsetScale = ToOffsetScale(material.Emissive),
            TextureRotations = new Vector4(
                material.BaseColor.RotationRadians,
                material.Normal.RotationRadians,
                material.MetallicRoughness.RotationRadians,
                material.Emissive.RotationRadians),
            TextureTexCoordSets = new Vector4(
                material.BaseColor.TexCoordSet,
                material.Normal.TexCoordSet,
                material.MetallicRoughness.TexCoordSet,
                material.Emissive.TexCoordSet),
            OcclusionBinding = new Vector4(
                material.Occlusion.RotationRadians,
                material.Occlusion.TexCoordSet,
                0f,
                0f),
            AlbedoTextureIndex = baseColor.BindlessIndex,
            NormalTextureIndex = normal.BindlessIndex,
            MetallicRoughnessTextureIndex = metallicRoughness.BindlessIndex,
            OcclusionTextureIndex = occlusion.BindlessIndex,
            EmissiveTextureIndex = emissive.BindlessIndex,
            FeatureFlags = (uint)material.FeatureFlags,
            ExtensionDataIndex = -1,
            TransportFlags = packedFlags,
            TransportProfileRevision = context.ProfileRevision,
            PackedMeanMetallicRoughness = PackMeanMetallicRoughness(meanMetallic, meanRoughness),
            TransportProfileQuality = (uint)quality,
            MaterialRevision = 0,
            PackedMeanGiDirectionalDiffuseBaseRg =
                PackUnitHalf2(meanDirectionalDiffuseBase.X, meanDirectionalDiffuseBase.Y),
            PackedMeanGiDirectionalDiffuseBaseBAndF0R =
                PackUnitHalf2(meanDirectionalDiffuseBase.Z, meanDielectricF0.X),
            PackedMeanGiDielectricF0Gb =
                PackUnitHalf2(meanDielectricF0.Y, meanDielectricF0.Z),
            DdgiAverageAlbedo = new Vector4(meanDiffuse, meanOcclusion),
            DdgiAverageEmissive = new Vector4(meanEmission, Luminance(meanEmission)),
            DdgiAverageTransmission = new Vector4(meanTransmittedDiffuse, 0f),
            DdgiMaterialPolicy = new Vector4(
                (float)material.AlphaMode,
                preferredLod,
                alphaCoverage,
                normal.NormalVarianceValid ? Math.Max(normal.NormalVariance, 0f) : 0f)
        };

        GPUMaterialExtensionData? extensionData = material.FeatureFlags.RequiresExtensionData()
            ? CompileExtensions(material, context, effectiveEmissiveScale)
            : null;
        MaterialRenderMetadata metadata = CompileMetadata(material);
        ulong combinedHash = CombineHashes(
            baseColor.SourceContentHash,
            metallicRoughness.SourceContentHash,
            occlusion.SourceContentHash,
            emissive.SourceContentHash,
            normal.SourceContentHash,
            clearcoatEnergy.SourceContentHash,
            sheenEnergy.SourceContentHash,
            transmissionEnergy.SourceContentHash,
            specularEnergy.SourceContentHash,
            specularColorEnergy.SourceContentHash);
        var profile = new GiMaterialTransportProfile
        {
            AlgorithmVersion = context.AlgorithmVersion,
            SourceContentHash = combinedHash,
            PrimitiveContentHash = context.PrimitiveProfile?.PrimitiveContentHash ?? 0,
            Flags = flags,
            Quality = quality,
            MeanDiffuseReflectance = meanDiffuse,
            MeanTransmittedDiffuseReflectance = meanTransmittedDiffuse,
            MeanEmissiveRadiance = meanEmission,
            EmissiveImportance = Luminance(meanEmission),
            EmissiveUnit = material.EmissiveUnit,
            EffectiveEmissiveScale = effectiveEmissiveScale,
            EmissiveArtisticMultiplier = material.EmissiveArtisticMultiplier,
            AverageEmissiveLuminanceNits =
                EmissivePhotometry.SceneLinearLuminanceToNits(Luminance(meanEmission)),
            PeakEmissiveLuminanceNits = emissive.EmissiveLuminanceMaximumValid && material.EmitsIntoGi
                ? EmissivePhotometry.SceneLinearLuminanceToNits(
                    Math.Max(material.EmissiveFactor.X,
                        Math.Max(material.EmissiveFactor.Y, material.EmissiveFactor.Z)) *
                    effectiveEmissiveScale *
                    Math.Max(emissive.EmissiveLuminanceMaximum, 0f))
                : 0f,
            PeakEmissiveLuminanceValid = emissive.EmissiveLuminanceMaximumValid,
            MeanMaterialOcclusion = meanOcclusion,
            AlphaCoverage = alphaCoverage,
            MeanMetallic = meanMetallic,
            MeanRoughness = meanRoughness,
            NormalVariance = normal.NormalVarianceValid ? Math.Max(normal.NormalVariance, 0f) : 0f
        };

        return new CompiledMaterialTransport(
            material,
            gpu,
            extensionData,
            profile,
            metadata,
            CollectTextureDependencies(material),
            context.ProfileRevision,
            diagnostics);
    }

    public static MaterialChangeMask ClassifyChanges(MaterialDefinition before, MaterialDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before == after)
            return MaterialChangeMask.None;

        ArgumentNullException.ThrowIfNull(before.Extensions);
        ArgumentNullException.ThrowIfNull(after.Extensions);

        MaterialChangeMask mask = MaterialChangeMask.RasterAppearance;
        if (CoreDiffuseInputsChanged(before, after) ||
            ExtensionDiffuseInputsChanged(before.Extensions, after.Extensions))
        {
            mask |= MaterialChangeMask.DiffuseTransport;
        }
        if (EmissionInputsChanged(before, after))
            mask |= MaterialChangeMask.Emission;
        if (CoverageInputsChanged(before, after))
            mask |= MaterialChangeMask.AlphaCoverage | MaterialChangeMask.AccelerationStructure;
        if (before.DoubleSided != after.DoubleSided)
            mask |= MaterialChangeMask.Sidedness | MaterialChangeMask.AccelerationStructure;
        if (before.ShadingModel != after.ShadingModel)
        {
            mask |= MaterialChangeMask.ShadingModel |
                    MaterialChangeMask.DiffuseTransport |
                    MaterialChangeMask.Emission |
                    MaterialChangeMask.AccelerationStructure;
        }
        if (before.DiffuseGiParticipation != after.DiffuseGiParticipation)
            mask |= MaterialChangeMask.DiffuseTransport;
        if (before.EmissionGiParticipation != after.EmissionGiParticipation)
            mask |= MaterialChangeMask.Emission;
        if (before.Extensions.TransmissionPolicy != after.Extensions.TransmissionPolicy)
            mask |= MaterialChangeMask.AccelerationStructure;

        MaterialFeatureFlags changedFeatures = before.FeatureFlags ^ after.FeatureFlags;
        if (changedFeatures != MaterialFeatureFlags.None)
        {
            const MaterialFeatureFlags diffuseFeatures =
                MaterialFeatureFlags.Clearcoat |
                MaterialFeatureFlags.ClearcoatTexture |
                MaterialFeatureFlags.Sheen |
                MaterialFeatureFlags.SheenColorTexture |
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.TransmissionTexture |
                MaterialFeatureFlags.Specular |
                MaterialFeatureFlags.SpecularTexture |
                MaterialFeatureFlags.SpecularColorTexture |
                MaterialFeatureFlags.Foliage |
                MaterialFeatureFlags.CompressedNormalBc5 |
                MaterialFeatureFlags.NormalMapGreenInverted |
                MaterialFeatureFlags.Ior;
            if ((changedFeatures & diffuseFeatures) != 0)
                mask |= MaterialChangeMask.DiffuseTransport;
            if ((changedFeatures & MaterialFeatureFlags.EmissiveStrength) != 0)
                mask |= MaterialChangeMask.Emission;
            if ((changedFeatures & (MaterialFeatureFlags.Transmission |
                                    MaterialFeatureFlags.Foliage)) != 0)
            {
                mask |= MaterialChangeMask.AlphaCoverage |
                        MaterialChangeMask.ShadingModel |
                        MaterialChangeMask.AccelerationStructure;
            }
        }
        if (before.RenderBlendModeOverride != after.RenderBlendModeOverride)
        {
            mask |= MaterialChangeMask.AlphaCoverage |
                    MaterialChangeMask.AccelerationStructure;
        }
        if (before.IsGeometryDecal != after.IsGeometryDecal)
        {
            mask |= MaterialChangeMask.AlphaCoverage |
                    MaterialChangeMask.ShadingModel |
                    MaterialChangeMask.AccelerationStructure;
        }
        if (TextureBindingsChanged(before, after))
            mask |= MaterialChangeMask.TextureDependencies;
        if ((mask & (MaterialChangeMask.DiffuseTransport |
                     MaterialChangeMask.Emission |
                     MaterialChangeMask.AlphaCoverage |
                     MaterialChangeMask.Sidedness |
                     MaterialChangeMask.ShadingModel)) != 0)
        {
            mask |= MaterialChangeMask.FarField;
        }

        return mask;
    }

    private static MaterialTextureTransportInput Resolve(
        MaterialTextureBinding binding,
        MaterialTextureSemantic semantic,
        MaterialCompilationContext context,
        int fallbackIndex,
        Vector4 fallbackValue)
    {
        if (!binding.IsBound)
            return MaterialTextureTransportInput.Constant(fallbackIndex, fallbackValue);
        if (context.ResolveTexture == null)
        {
            throw new InvalidOperationException(
                $"Material texture '{semantic}' is bound, but no material texture resolver was supplied.");
        }

        MaterialTextureTransportInput resolved = context.ResolveTexture(binding, semantic);
        if (!BindlessIndex.IsTextureIndex(resolved.BindlessIndex))
        {
            throw new InvalidOperationException(
                $"Resolved {semantic} texture index {resolved.BindlessIndex} is outside the bindless texture range.");
        }
        ValidateFiniteStatistics(resolved, semantic);
        return resolved;
    }

    private static MaterialTextureTransportInput ResolveExtensionStatistics(
        MaterialDefinition material,
        MaterialFeatureFlags textureFeature,
        MaterialTextureBinding binding,
        MaterialTextureSemantic semantic,
        MaterialCompilationContext context)
    {
        if (!material.FeatureFlags.HasFlag(textureFeature) || !binding.IsBound)
        {
            return MaterialTextureTransportInput.Constant(
                BindlessIndex.DefaultWhiteTexture,
                Vector4.One);
        }

        return Resolve(
            binding,
            semantic,
            context,
            BindlessIndex.DefaultWhiteTexture,
            Vector4.One);
    }

    private static GPUMaterialExtensionData CompileExtensions(
        MaterialDefinition material,
        MaterialCompilationContext context,
        float effectiveEmissiveScale)
    {
        MaterialExtensionDefinition extension = material.Extensions;
        return new GPUMaterialExtensionData
        {
            Clearcoat = new Vector4(
                extension.ClearcoatFactor,
                extension.ClearcoatRoughness,
                extension.ClearcoatNormalScale,
                effectiveEmissiveScale),
            SheenColor = new Vector4(extension.SheenColorFactor, extension.SheenRoughness),
            Anisotropy = new Vector4(extension.AnisotropyStrength, extension.AnisotropyRotation, 0f, 0f),
            Transmission = new Vector4(
                extension.TransmissionFactor,
                extension.Ior,
                extension.ThicknessFactor,
                float.IsPositiveInfinity(extension.AttenuationDistance) ? 0f : extension.AttenuationDistance),
            AttenuationColor = new Vector4(extension.AttenuationColor, 0f),
            Subsurface = new Vector4(extension.SubsurfaceColor, extension.SubsurfaceStrength),
            SpecularColor = new Vector4(extension.SpecularColorFactor, extension.SpecularFactor),
            Iridescence = new Vector4(
                extension.IridescenceFactor,
                extension.IridescenceIor,
                extension.IridescenceThicknessMinimum,
                extension.IridescenceThicknessMaximum),
            // Spare extension lanes carry the renderer-owned thin-sheet tint;
            // volume attenuation remains in AttenuationColor.rgb.
            Dispersion = new Vector4(
                extension.Dispersion,
                extension.ThinTransmissionTint.X,
                extension.ThinTransmissionTint.Y,
                extension.ThinTransmissionTint.Z),
            ClearcoatOffsetScale = ToOffsetScale(extension.Clearcoat),
            ClearcoatRoughnessOffsetScale = ToOffsetScale(extension.ClearcoatRoughnessTexture),
            ClearcoatNormalOffsetScale = ToOffsetScale(extension.ClearcoatNormal),
            SheenColorOffsetScale = ToOffsetScale(extension.SheenColor),
            SheenRoughnessOffsetScale = ToOffsetScale(extension.SheenRoughnessTexture),
            AnisotropyOffsetScale = ToOffsetScale(extension.Anisotropy),
            TransmissionOffsetScale = ToOffsetScale(extension.Transmission),
            ThicknessOffsetScale = ToOffsetScale(extension.Thickness),
            SpecularOffsetScale = ToOffsetScale(extension.Specular),
            SpecularColorOffsetScale = ToOffsetScale(extension.SpecularColor),
            IridescenceOffsetScale = ToOffsetScale(extension.Iridescence),
            IridescenceThicknessOffsetScale = ToOffsetScale(extension.IridescenceThickness),
            SubsurfaceOffsetScale = ToOffsetScale(extension.Subsurface),
            ExtensionTextureRotations0 = new Vector4(
                extension.Clearcoat.RotationRadians,
                extension.ClearcoatRoughnessTexture.RotationRadians,
                extension.ClearcoatNormal.RotationRadians,
                extension.SheenColor.RotationRadians),
            ExtensionTextureRotations1 = new Vector4(
                extension.SheenRoughnessTexture.RotationRadians,
                extension.Anisotropy.RotationRadians,
                extension.Transmission.RotationRadians,
                extension.Thickness.RotationRadians),
            ExtensionTextureRotations2 = new Vector4(
                extension.Specular.RotationRadians,
                extension.SpecularColor.RotationRadians,
                extension.Iridescence.RotationRadians,
                extension.IridescenceThickness.RotationRadians),
            ExtensionTextureRotations3 = new Vector4(extension.Subsurface.RotationRadians, 0f, 0f, 0f),
            ExtensionTextureTexCoordSets0 = new Vector4(
                extension.Clearcoat.TexCoordSet,
                extension.ClearcoatRoughnessTexture.TexCoordSet,
                extension.ClearcoatNormal.TexCoordSet,
                extension.SheenColor.TexCoordSet),
            ExtensionTextureTexCoordSets1 = new Vector4(
                extension.SheenRoughnessTexture.TexCoordSet,
                extension.Anisotropy.TexCoordSet,
                extension.Transmission.TexCoordSet,
                extension.Thickness.TexCoordSet),
            ExtensionTextureTexCoordSets2 = new Vector4(
                extension.Specular.TexCoordSet,
                extension.SpecularColor.TexCoordSet,
                extension.Iridescence.TexCoordSet,
                extension.IridescenceThickness.TexCoordSet),
            ExtensionTextureTexCoordSets3 = new Vector4(extension.Subsurface.TexCoordSet, 0f, 0f, 0f),
            ClearcoatTextureIndex = ResolveExtensionIndex(extension.Clearcoat, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            ClearcoatRoughnessTextureIndex = ResolveExtensionIndex(extension.ClearcoatRoughnessTexture, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            ClearcoatNormalTextureIndex = ResolveExtensionIndex(extension.ClearcoatNormal, MaterialTextureSemantic.Normal, context, BindlessIndex.DefaultNormalTexture),
            SheenColorTextureIndex = ResolveExtensionIndex(extension.SheenColor, MaterialTextureSemantic.SrgbColor, context, BindlessIndex.DefaultWhiteTexture),
            SheenRoughnessTextureIndex = ResolveExtensionIndex(extension.SheenRoughnessTexture, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            AnisotropyTextureIndex = ResolveExtensionIndex(extension.Anisotropy, MaterialTextureSemantic.LinearColor, context, BindlessIndex.DefaultWhiteTexture),
            TransmissionTextureIndex = ResolveExtensionIndex(extension.Transmission, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            ThicknessTextureIndex = ResolveExtensionIndex(extension.Thickness, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            SubsurfaceTextureIndex = ResolveExtensionIndex(extension.Subsurface, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            SpecularTextureIndex = ResolveExtensionIndex(extension.Specular, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            SpecularColorTextureIndex = ResolveExtensionIndex(extension.SpecularColor, MaterialTextureSemantic.SrgbColor, context, BindlessIndex.DefaultWhiteTexture),
            IridescenceTextureIndex = ResolveExtensionIndex(extension.Iridescence, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            IridescenceThicknessTextureIndex = ResolveExtensionIndex(extension.IridescenceThickness, MaterialTextureSemantic.LinearScalar, context, BindlessIndex.DefaultWhiteTexture),
            Padding0 = 0,
            Padding1 = 0,
            Padding2 = 0,
            Padding3 = 0
        };
    }

    private static int ResolveExtensionIndex(
        MaterialTextureBinding binding,
        MaterialTextureSemantic semantic,
        MaterialCompilationContext context,
        int fallbackIndex)
    {
        return Resolve(binding, semantic, context, fallbackIndex, Vector4.One).BindlessIndex;
    }

    private static MaterialRenderMetadata CompileMetadata(MaterialDefinition material)
    {
        MaterialSurfaceFlags surfaceFlags = material.ReceivesShadows
            ? MaterialSurfaceFlags.ReceivesShadows
            : MaterialSurfaceFlags.None;
        if (material.DoubleSided)
            surfaceFlags |= MaterialSurfaceFlags.DoubleSided;
        if (material.IsGeometryDecal)
            surfaceFlags |= MaterialSurfaceFlags.GeometryDecal;

        return new MaterialRenderMetadata
        {
            BlendMode = material.RenderBlendModeOverride ??
                        (material.FeatureFlags.RequiresTransparentPass() &&
                         material.Extensions.TransmissionPolicy != GiTransmissionPolicy.ThinSurface
                            ? MaterialBlendMode.AlphaBlend
                            : material.AlphaMode switch
                            {
                                MaterialAlphaMode.Mask => MaterialBlendMode.Mask,
                                MaterialAlphaMode.Blend => MaterialBlendMode.AlphaBlend,
                                _ => MaterialBlendMode.Opaque
                            }),
            SurfaceFlags = surfaceFlags,
            AlphaCutoff = material.AlphaCutoff,
            ShadingModel = material.ShadingModel,
            DiffuseGiParticipation = material.DiffuseGiParticipation,
            EmissionGiParticipation = material.EmissionGiParticipation,
            TransmissionPolicy = material.Extensions.TransmissionPolicy,
            DecalLayer = material.DecalLayer,
            DecalDepthBias = material.DecalDepthBias
        };
    }

    private static float ResolveAlphaCoverage(
        MaterialDefinition material,
        MaterialTextureTransportInput baseColor)
    {
        if (material.AlphaMode == MaterialAlphaMode.Opaque)
            return 1f;
        if (!material.BaseColor.IsBound)
        {
            return GiMaterialReferenceEvaluator.EvaluateOpacity(
                material.BaseColorFactor.W,
                material.AlphaMode,
                material.AlphaCutoff)
                ? 1f
                : 0f;
        }
        if (!baseColor.AlphaCoverageValid)
            return 0f;
        return Math.Clamp(baseColor.AlphaCoverage, 0f, 1f);
    }

    private static GiMaterialTransportFlags BuildTransportFlags(
        MaterialDefinition material,
        bool diffuseValid,
        bool emissionValid,
        bool alphaValid,
        bool occlusionValid,
        bool normalValid,
        GiTransportProfileQuality quality)
    {
        GiMaterialTransportFlags flags = GiMaterialTransportFlags.None;
        if (diffuseValid)
            flags |= GiMaterialTransportFlags.DiffuseProfileValid;
        if (diffuseValid && occlusionValid)
            flags |= GiMaterialTransportFlags.BaseStatisticsValid;
        if (emissionValid)
            flags |= GiMaterialTransportFlags.EmissionProfileValid;
        if (alphaValid)
            flags |= GiMaterialTransportFlags.AlphaProfileValid;
        if (normalValid)
            flags |= GiMaterialTransportFlags.NormalProfileValid;
        if (material.ShadingModel == MaterialShadingModel.Unlit)
            flags |= GiMaterialTransportFlags.Unlit;
        if (material.DoubleSided)
            flags |= GiMaterialTransportFlags.DoubleSided;
        if (material.FeatureFlags.HasFlag(MaterialFeatureFlags.Transmission) &&
            material.Extensions.TransmissionFactor > 0f &&
            material.Extensions.TransmissionPolicy == GiTransmissionPolicy.ThinSurface)
        {
            // Only remove reflected opaque diffuse when the compiler can also
            // provide the supported transmitted lobe. Unsupported/volume
            // policies deliberately retain the opaque GI fallback instead of
            // becoming black on both sides.
            flags |= GiMaterialTransportFlags.TransmissionRemovesOpaqueDiffuse |
                GiMaterialTransportFlags.ThinSurfaceTransmission;
            if (diffuseValid)
                flags |= GiMaterialTransportFlags.TransmissionProfileValid;
            if (material.Extensions.Transmission.IsBound)
                flags |= GiMaterialTransportFlags.HasTransmissionTexture;
        }
        if (material.EmitsIntoGi)
            flags |= GiMaterialTransportFlags.EmitsIntoGi;
        if (material.ReceivesIndirectDiffuse)
            flags |= GiMaterialTransportFlags.ReceivesIndirectDiffuse;
        if (material.ReflectsIndirectDiffuse)
            flags |= GiMaterialTransportFlags.ReflectsIndirectDiffuse;
        if (material.BaseColor.IsBound)
            flags |= GiMaterialTransportFlags.HasBaseColorTexture;
        if (material.MetallicRoughness.IsBound)
            flags |= GiMaterialTransportFlags.HasMetallicRoughnessTexture;
        if (material.Occlusion.IsBound)
            flags |= GiMaterialTransportFlags.HasOcclusionTexture;
        if (material.Emissive.IsBound)
            flags |= GiMaterialTransportFlags.HasEmissiveTexture;
        if (material.IsGeometryDecal)
            flags |= GiMaterialTransportFlags.GeometryDecal;
        flags |= (GiMaterialTransportFlags)((uint)quality << (int)GiMaterialTransportFlags.QualityShift);
        return flags;
    }

    private static bool IsCompactProfileValid(
        MaterialDefinition material,
        GiMaterialTransportFlags flags)
    {
        bool diffuseValid = !material.ReflectsIndirectDiffuse ||
                            (flags & GiMaterialTransportFlags.DiffuseProfileValid) != 0;
        bool emissionValid = !material.EmitsIntoGi ||
                             (flags & GiMaterialTransportFlags.EmissionProfileValid) != 0;
        bool alphaValid = material.AlphaMode != MaterialAlphaMode.Mask ||
                          (flags & GiMaterialTransportFlags.AlphaProfileValid) != 0;
        bool occlusionValid = !material.Occlusion.IsBound ||
                              (flags & GiMaterialTransportFlags.BaseStatisticsValid) != 0;
        bool transmissionValid = material.Extensions.TransmissionPolicy != GiTransmissionPolicy.ThinSurface ||
                                 (flags & GiMaterialTransportFlags.TransmissionProfileValid) != 0;
        return diffuseValid && emissionValid && alphaValid && occlusionValid && transmissionValid;
    }

    private static GiTransportProfileQuality ResolveProfileQuality(
        MaterialDefinition material,
        GiMaterialTransportProfile? primitive,
        bool diffuseValid,
        bool emissionValid,
        bool alphaValid,
        bool occlusionValid,
        bool normalValid)
    {
        bool primitiveCoversActiveChannels =
            primitive is { Quality: GiTransportProfileQuality.PrimitiveSurfaceSampling } &&
            (!material.ReflectsIndirectDiffuse ||
             primitive.Has(GiMaterialTransportFlags.DiffuseProfileValid)) &&
            (!material.EmitsIntoGi ||
             primitive.Has(GiMaterialTransportFlags.EmissionProfileValid)) &&
            (material.AlphaMode != MaterialAlphaMode.Mask ||
             primitive.Has(GiMaterialTransportFlags.AlphaProfileValid)) &&
            ((!material.MetallicRoughness.IsBound && !material.Occlusion.IsBound) ||
             primitive.Has(GiMaterialTransportFlags.BaseStatisticsValid)) &&
            (material.Extensions.TransmissionPolicy != GiTransmissionPolicy.ThinSurface ||
             primitive.Has(GiMaterialTransportFlags.TransmissionProfileValid)) &&
            (!material.Normal.IsBound ||
             primitive.Has(GiMaterialTransportFlags.NormalProfileValid));
        if (primitiveCoversActiveChannels)
            return GiTransportProfileQuality.PrimitiveSurfaceSampling;
        bool hasTextures = HasAnyDetailedGiTexture(material);
        if (!hasTextures)
            return GiTransportProfileQuality.MaterialFactors;
        bool activeStatisticsValid =
            (!material.ReflectsIndirectDiffuse || diffuseValid) &&
            (!material.EmitsIntoGi || emissionValid) &&
            (material.AlphaMode != MaterialAlphaMode.Mask || alphaValid) &&
            (!material.Occlusion.IsBound || occlusionValid) &&
            (!material.Normal.IsBound || normalValid);
        return activeStatisticsValid
            ? GiTransportProfileQuality.TextureStatistics
            : GiTransportProfileQuality.Invalid;
    }

    private static bool CoreDiffuseInputsChanged(MaterialDefinition x, MaterialDefinition y) =>
        x.BaseColorFactor.X != y.BaseColorFactor.X ||
        x.BaseColorFactor.Y != y.BaseColorFactor.Y ||
        x.BaseColorFactor.Z != y.BaseColorFactor.Z ||
        x.MetallicFactor != y.MetallicFactor ||
        x.RoughnessFactor != y.RoughnessFactor ||
        x.OcclusionStrength != y.OcclusionStrength ||
        x.NormalScale != y.NormalScale ||
        x.BaseColor != y.BaseColor ||
        x.MetallicRoughness != y.MetallicRoughness ||
        x.Occlusion != y.Occlusion ||
        x.Normal != y.Normal;

    private static bool ExtensionDiffuseInputsChanged(
        MaterialExtensionDefinition x,
        MaterialExtensionDefinition y) =>
        x.ClearcoatFactor != y.ClearcoatFactor ||
        x.Clearcoat != y.Clearcoat ||
        x.SheenColorFactor != y.SheenColorFactor ||
        x.SheenColor != y.SheenColor ||
        x.TransmissionFactor != y.TransmissionFactor ||
        x.Ior != y.Ior ||
        x.Transmission != y.Transmission ||
        x.TransmissionPolicy != y.TransmissionPolicy ||
        x.ThinTransmissionTint != y.ThinTransmissionTint ||
        x.SpecularFactor != y.SpecularFactor ||
        x.SpecularColorFactor != y.SpecularColorFactor ||
        x.Specular != y.Specular ||
        x.SpecularColor != y.SpecularColor;

    private static bool EmissionInputsChanged(MaterialDefinition x, MaterialDefinition y) =>
        x.EmissiveFactor != y.EmissiveFactor ||
        x.EmissiveStrength != y.EmissiveStrength ||
        x.EmissiveUnit != y.EmissiveUnit ||
        x.EmissiveArtisticMultiplier != y.EmissiveArtisticMultiplier ||
        x.Emissive != y.Emissive ||
        x.EmissionGiParticipation != y.EmissionGiParticipation;

    private static bool CoverageInputsChanged(MaterialDefinition x, MaterialDefinition y) =>
        x.BaseColorFactor.W != y.BaseColorFactor.W ||
        x.BaseColor != y.BaseColor ||
        x.AlphaMode != y.AlphaMode ||
        x.AlphaCutoff != y.AlphaCutoff;

    private static bool TextureBindingsChanged(MaterialDefinition x, MaterialDefinition y) =>
        x.BaseColor != y.BaseColor ||
        x.Normal != y.Normal ||
        x.MetallicRoughness != y.MetallicRoughness ||
        x.Occlusion != y.Occlusion ||
        x.Emissive != y.Emissive ||
        ExtensionTextureBindingsChanged(x.Extensions, y.Extensions);

    private static bool ExtensionTextureBindingsChanged(
        MaterialExtensionDefinition x,
        MaterialExtensionDefinition y) =>
        x.Clearcoat != y.Clearcoat ||
        x.ClearcoatRoughnessTexture != y.ClearcoatRoughnessTexture ||
        x.ClearcoatNormal != y.ClearcoatNormal ||
        x.SheenColor != y.SheenColor ||
        x.SheenRoughnessTexture != y.SheenRoughnessTexture ||
        x.Anisotropy != y.Anisotropy ||
        x.Transmission != y.Transmission ||
        x.Thickness != y.Thickness ||
        x.Specular != y.Specular ||
        x.SpecularColor != y.SpecularColor ||
        x.Iridescence != y.Iridescence ||
        x.IridescenceThickness != y.IridescenceThickness ||
        x.Subsurface != y.Subsurface;

    private static IReadOnlyList<TextureHandle> CollectTextureDependencies(MaterialDefinition material)
    {
        MaterialExtensionDefinition e = material.Extensions;
        MaterialTextureBinding[] bindings =
        [
            material.BaseColor,
            material.Normal,
            material.MetallicRoughness,
            material.Occlusion,
            material.Emissive,
            e.Clearcoat,
            e.ClearcoatRoughnessTexture,
            e.ClearcoatNormal,
            e.SheenColor,
            e.SheenRoughnessTexture,
            e.Anisotropy,
            e.Transmission,
            e.Thickness,
            e.Specular,
            e.SpecularColor,
            e.Iridescence,
            e.IridescenceThickness,
            e.Subsurface
        ];
        return bindings
            .Where(binding => binding.IsBound)
            .Select(binding => binding.Texture)
            .ToArray();
    }

    private static bool HasAnyDetailedGiTexture(MaterialDefinition material) =>
        material.BaseColor.IsBound ||
        material.Normal.IsBound ||
        material.MetallicRoughness.IsBound ||
        material.Occlusion.IsBound ||
        material.Emissive.IsBound ||
        HasActiveExtensionTexture(material, MaterialFeatureFlags.ClearcoatTexture, material.Extensions.Clearcoat) ||
        HasActiveExtensionTexture(material, MaterialFeatureFlags.SheenColorTexture, material.Extensions.SheenColor) ||
        HasActiveExtensionTexture(material, MaterialFeatureFlags.TransmissionTexture, material.Extensions.Transmission) ||
        HasActiveExtensionTexture(material, MaterialFeatureFlags.SpecularTexture, material.Extensions.Specular) ||
        HasActiveExtensionTexture(material, MaterialFeatureFlags.SpecularColorTexture, material.Extensions.SpecularColor);

    private static bool HasActiveExtensionTexture(
        MaterialDefinition material,
        MaterialFeatureFlags textureFeature,
        MaterialTextureBinding binding) =>
        material.FeatureFlags.HasFlag(textureFeature) && binding.IsBound;

    private static bool BindingsEquivalent(MaterialTextureBinding left, MaterialTextureBinding right) =>
        left.IsBound && right.IsBound && left == right;

    private static Vector4 ToOffsetScale(MaterialTextureBinding binding) => new(
        binding.Offset.X,
        binding.Offset.Y,
        binding.Scale.X,
        binding.Scale.Y);

    private static Vector4 Multiply(Vector4 x, Vector4 y) => new(
        x.X * y.X,
        x.Y * y.Y,
        x.Z * y.Z,
        x.W * y.W);

    private static Vector3 ToVector3(Vector4 value) => new(value.X, value.Y, value.Z);

    private static float Luminance(Vector3 value) =>
        Math.Max(0f, value.X * 0.2126f + value.Y * 0.7152f + value.Z * 0.0722f);

    internal static uint PackMeanMetallicRoughness(float metallic, float roughness)
    {
        return PackUnitHalf2(metallic, roughness);
    }

    internal static uint PackUnitHalf2(float low, float high)
    {
        return BitConverter.HalfToUInt16Bits((Half)Math.Clamp(low, 0f, 1f)) |
               ((uint)BitConverter.HalfToUInt16Bits((Half)Math.Clamp(high, 0f, 1f)) << 16);
    }

    private static Vector3 RecoverDirectionalDiffuseBase(
        Vector3 hemisphericalDiffuseReflectance,
        Vector3 dielectricF0)
    {
        static float Recover(float reflectance, float f0)
        {
            float normalTransmission = 1f - Math.Clamp(f0, 0f, 1f);
            float referenceEnergy =
                GiMaterialReferenceEvaluator.SchlickCosineWeightedTransmission *
                normalTransmission *
                normalTransmission;
            if (referenceEnergy <= 1e-6f)
                return 0f;
            return Math.Clamp(reflectance, 0f, 1f) / referenceEnergy;
        }

        return Vector3.Clamp(
            new Vector3(
                Recover(hemisphericalDiffuseReflectance.X, dielectricF0.X),
                Recover(hemisphericalDiffuseReflectance.Y, dielectricF0.Y),
                Recover(hemisphericalDiffuseReflectance.Z, dielectricF0.Z)),
            Vector3.Zero,
            Vector3.One);
    }

    private static ulong CombineHashes(params ulong[] hashes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong value = offset;
        foreach (ulong hash in hashes)
        {
            value ^= hash;
            value *= prime;
        }
        return value;
    }

    private static void ValidateFiniteStatistics(
        MaterialTextureTransportInput value,
        MaterialTextureSemantic semantic)
    {
        if (value.MeanValid &&
            (!float.IsFinite(value.LinearMean.X) ||
             !float.IsFinite(value.LinearMean.Y) ||
             !float.IsFinite(value.LinearMean.Z) ||
             !float.IsFinite(value.LinearMean.W)))
        {
            throw new InvalidOperationException($"{semantic} texture statistics contain non-finite mean values.");
        }
        if (value.AlphaCoverageValid &&
            (!float.IsFinite(value.AlphaCoverage) || value.AlphaCoverage is < 0f or > 1f))
        {
            throw new InvalidOperationException($"{semantic} texture alpha coverage is outside [0, 1].");
        }
        if (value.NormalVarianceValid &&
            (!float.IsFinite(value.NormalVariance) || value.NormalVariance < 0f))
        {
                throw new InvalidOperationException($"{semantic} texture normal variance is invalid.");
        }
        if (value.EmissiveLuminanceMaximumValid &&
            (!float.IsFinite(value.EmissiveLuminanceMaximum) ||
             value.EmissiveLuminanceMaximum < 0f))
        {
            throw new InvalidOperationException(
                $"{semantic} texture maximum emissive luminance is invalid.");
        }
    }
}
