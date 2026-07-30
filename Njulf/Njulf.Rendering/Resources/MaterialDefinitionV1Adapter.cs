using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Read-only compatibility conversion for callers and cooked packages that
/// still provide a raw V1 GPU payload. V1 values are never promoted to valid
/// compact statistics merely because a vector happens to be non-zero.
/// </summary>
public static class MaterialDefinitionV1Adapter
{
    public static MaterialDefinition FromGpuMaterial(
        GPUMaterialData material,
        GPUMaterialExtensionData? extension,
        MaterialRenderMetadata metadata,
        IReadOnlyList<TextureHandle>? textureHandles = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        MaterialFeatureFlags featureFlags = (MaterialFeatureFlags)material.FeatureFlags;
        float emissiveStrength = featureFlags.HasFlag(MaterialFeatureFlags.EmissiveStrength) &&
                                 extension.HasValue
            ? Math.Max(extension.Value.Clearcoat.W, 0f)
            : 1f;

        return MaterialDefinitionValidator.ValidateAndNormalize(new MaterialDefinition
        {
            Name = "V1CompatibilityMaterial",
            BaseColorFactor = material.Albedo,
            EmissiveFactor = new Vector3(
                Math.Max(material.Emissive.X, 0f),
                Math.Max(material.Emissive.Y, 0f),
                Math.Max(material.Emissive.Z, 0f)),
            EmissiveStrength = emissiveStrength,
            MetallicFactor = material.MetallicRoughnessAO.X,
            RoughnessFactor = material.MetallicRoughnessAO.Y,
            OcclusionStrength = material.MetallicRoughnessAO.Z,
            NormalScale = material.NormalScaleBias.X,
            BaseColor = CreateBinding(
                textureHandles,
                0,
                material.AlbedoTextureIndex,
                material.BaseColorOffsetScale,
                material.TextureRotations.X,
                material.TextureTexCoordSets.X),
            Normal = CreateBinding(
                textureHandles,
                1,
                material.NormalTextureIndex,
                material.NormalOffsetScale,
                material.TextureRotations.Y,
                material.TextureTexCoordSets.Y),
            MetallicRoughness = CreateBinding(
                textureHandles,
                2,
                material.MetallicRoughnessTextureIndex,
                material.MetallicRoughnessOffsetScale,
                material.TextureRotations.Z,
                material.TextureTexCoordSets.Z),
            Occlusion = CreateOcclusionBinding(textureHandles, material),
            Emissive = CreateBinding(
                textureHandles,
                3,
                material.EmissiveTextureIndex,
                material.EmissiveOffsetScale,
                material.TextureRotations.W,
                material.TextureTexCoordSets.W),
            AlphaMode = DecodeAlphaMode(material.NormalScaleBias.Y),
            // ValidateAndNormalize below rejects non-finite/negative values
            // but preserves the authored upper-unclamped threshold verbatim.
            AlphaCutoff = material.NormalScaleBias.Z,
            DoubleSided = metadata.DoubleSided,
            ShadingModel = metadata.ShadingModel,
            FeatureFlags = featureFlags,
            Extensions = CreateExtensions(extension),
            DiffuseGiParticipation = metadata.DiffuseGiParticipation,
            EmissionGiParticipation = metadata.EmissionGiParticipation,
            IsGeometryDecal = metadata.IsGeometryDecal,
            DecalLayer = metadata.DecalLayer,
            DecalDepthBias = metadata.DecalDepthBias
        });
    }

    public static GiMaterialTransportProfile CreateTransportProfile(GPUMaterialData material)
    {
        GiMaterialTransportFlags flags = GiMaterialTransportFlags.LegacyV1Fallback;
        if (material.NormalScaleBias.W >= 0.5f)
            flags |= GiMaterialTransportFlags.DoubleSided;
        if (((GiMaterialTransportFlags)material.TransportFlags).HasFlag(GiMaterialTransportFlags.Unlit))
            flags |= GiMaterialTransportFlags.Unlit;

        return new GiMaterialTransportProfile
        {
            AlgorithmVersion = 1,
            Flags = flags,
            Quality = GiTransportProfileQuality.Invalid,
            MeanDiffuseReflectance = new Vector3(
                Math.Clamp(material.DdgiAverageAlbedo.X, 0f, 1f),
                Math.Clamp(material.DdgiAverageAlbedo.Y, 0f, 1f),
                Math.Clamp(material.DdgiAverageAlbedo.Z, 0f, 1f)),
            MeanEmissiveRadiance = new Vector3(
                Math.Max(material.DdgiAverageEmissive.X, 0f),
                Math.Max(material.DdgiAverageEmissive.Y, 0f),
                Math.Max(material.DdgiAverageEmissive.Z, 0f)),
            EmissiveImportance = Math.Max(material.DdgiAverageEmissive.W, 0f),
            MeanMaterialOcclusion = 1f,
            AlphaCoverage = 0f,
            MeanMetallic = Math.Clamp(material.MetallicRoughnessAO.X, 0f, 1f),
            MeanRoughness = Math.Clamp(material.MetallicRoughnessAO.Y, 0f, 1f)
        };
    }

    private static MaterialExtensionDefinition CreateExtensions(GPUMaterialExtensionData? value)
    {
        if (!value.HasValue)
            return MaterialExtensionDefinition.None;

        GPUMaterialExtensionData e = value.Value;
        return new MaterialExtensionDefinition
        {
            ClearcoatFactor = e.Clearcoat.X,
            ClearcoatRoughness = e.Clearcoat.Y,
            ClearcoatNormalScale = e.Clearcoat.Z,
            SheenColorFactor = new Vector3(e.SheenColor.X, e.SheenColor.Y, e.SheenColor.Z),
            SheenRoughness = e.SheenColor.W,
            AnisotropyStrength = e.Anisotropy.X,
            AnisotropyRotation = e.Anisotropy.Y,
            TransmissionFactor = e.Transmission.X,
            Ior = e.Transmission.Y,
            ThicknessFactor = e.Transmission.Z,
            AttenuationDistance = e.Transmission.W > 0f ? e.Transmission.W : float.PositiveInfinity,
            AttenuationColor = new Vector3(
                e.AttenuationColor.X,
                e.AttenuationColor.Y,
                e.AttenuationColor.Z),
            SpecularFactor = e.SpecularColor.W,
            SpecularColorFactor = new Vector3(
                e.SpecularColor.X,
                e.SpecularColor.Y,
                e.SpecularColor.Z),
            IridescenceFactor = e.Iridescence.X,
            IridescenceIor = e.Iridescence.Y,
            IridescenceThicknessMinimum = e.Iridescence.Z,
            IridescenceThicknessMaximum = e.Iridescence.W,
            Dispersion = e.Dispersion.X,
            SubsurfaceColor = new Vector3(e.Subsurface.X, e.Subsurface.Y, e.Subsurface.Z),
            SubsurfaceStrength = e.Subsurface.W
        };
    }

    private static MaterialTextureBinding CreateOcclusionBinding(
        IReadOnlyList<TextureHandle>? handles,
        GPUMaterialData material)
    {
        int handleIndex = handles is { Count: > 4 } ? handles.Count - 1 : -1;
        return CreateBinding(
            handles,
            handleIndex,
            material.OcclusionTextureIndex,
            material.OcclusionOffsetScale,
            material.OcclusionBinding.X,
            material.OcclusionBinding.Y);
    }

    private static MaterialTextureBinding CreateBinding(
        IReadOnlyList<TextureHandle>? handles,
        int handleIndex,
        int textureIndex,
        Vector4 offsetScale,
        float rotation,
        float texCoordSet)
    {
        TextureHandle handle = handles != null &&
                               handleIndex >= 0 &&
                               handleIndex < handles.Count
            ? handles[handleIndex]
            : TextureHandle.Invalid;
        if (!handle.IsValid)
            return MaterialTextureBinding.Missing;

        return new MaterialTextureBinding
        {
            Texture = handle,
            TexCoordSet = Math.Clamp((int)MathF.Round(texCoordSet), 0, 1),
            Offset = new Vector2(offsetScale.X, offsetScale.Y),
            Scale = new Vector2(offsetScale.Z, offsetScale.W),
            RotationRadians = rotation
        };
    }

    private static MaterialAlphaMode DecodeAlphaMode(float value)
    {
        int code = (int)MathF.Round(value);
        return code switch
        {
            1 => MaterialAlphaMode.Mask,
            2 => MaterialAlphaMode.Blend,
            _ => MaterialAlphaMode.Opaque
        };
    }
}
