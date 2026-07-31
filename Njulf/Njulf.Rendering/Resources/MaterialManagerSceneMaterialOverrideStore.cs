using System;
using Njulf.Assets.Scenes;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Persists authored material overrides. Shared asset materials are split by
/// copy-on-write before editing so one scene object cannot accidentally mutate
/// every user of a deduplicated material.
/// </summary>
public sealed class MaterialManagerSceneMaterialOverrideStore : ISceneMaterialOverrideStore
{
    private readonly MaterialManager _materials;

    public MaterialManagerSceneMaterialOverrideStore(MaterialManager materials)
    {
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
    }

    public void Apply(RenderObject renderObject, SceneMaterialOverrideDocument source)
    {
        ArgumentNullException.ThrowIfNull(renderObject);
        ArgumentNullException.ThrowIfNull(source);
        if (renderObject.Material is not MaterialHandle handle)
            throw new InvalidOperationException($"Scene object '{renderObject.Name}' ({renderObject.Id}) has no material handle.");

        // Compile and normalize the authored override before copy-on-write
        // transfers the object's logical material reference. Invalid scene data
        // must not split a shared material or leave the object attached to an
        // unchanged private copy.
        MaterialDefinition material = _materials.GetMaterialDefinition(handle);
        MaterialDefinition updated = BuildValidatedOverride(material, source);
        if (updated == material)
            return;

        _materials.UpdateRenderObjectMaterialDefinition(
            renderObject,
            updated);
    }

    private static MaterialDefinition BuildValidatedOverride(
        MaterialDefinition material,
        SceneMaterialOverrideDocument source)
    {
        SceneColor? emissive = source.EmissiveColor ?? source.Emissive;
        MaterialAlphaMode alphaMode = ParseEnum(
            source.AlphaMode,
            material.AlphaMode,
            nameof(source.AlphaMode));
        MaterialShadingModel shadingModel = ParseEnum(
            source.ShadingModel,
            material.ShadingModel,
            nameof(source.ShadingModel));
        MaterialBlendMode? blendMode = ParseBlendMode(
            source.RenderBlendModeOverride,
            material.RenderBlendModeOverride);
        GiParticipationOverride emissionParticipation = ParseGiParticipation(
            source.EmissionGiParticipation,
            source.EmitsIntoGi,
            material.EmissionGiParticipation,
            nameof(source.EmissionGiParticipation));
        GiParticipationOverride diffuseParticipation = ParseGiParticipation(
            source.DiffuseGiParticipation,
            source.ReceivesDiffuseGi,
            material.DiffuseGiParticipation,
            nameof(source.DiffuseGiParticipation));
        GiTransmissionPolicy transmissionPolicy = ParseEnum(
            source.GiTransmissionPolicy,
            material.Extensions.TransmissionPolicy,
            nameof(source.GiTransmissionPolicy));
        SceneColor? transmissionTint = source.ThinTransmissionTint;
        MaterialExtensionDefinition extensions = material.Extensions with
        {
            TransmissionPolicy = transmissionPolicy,
            TransmissionFactor = source.ThinTransmissionFactor ?? material.Extensions.TransmissionFactor,
            ThinTransmissionTint = transmissionTint is { } tint
                ? new Vector3(tint.R, tint.G, tint.B)
                : material.Extensions.ThinTransmissionTint
        };
        MaterialFeatureFlags featureFlags = material.FeatureFlags;
        if (extensions.TransmissionFactor > 0f ||
            extensions.TransmissionPolicy != GiTransmissionPolicy.None)
        {
            featureFlags |= MaterialFeatureFlags.Transmission;
        }

        return MaterialDefinitionValidator.ValidateAndNormalize(material with
        {
            Name = source.Name ?? material.Name,
            BaseColorFactor = source.Albedo is { } albedo
                ? new Vector4(albedo.R, albedo.G, albedo.B, albedo.A)
                : material.BaseColorFactor,
            EmissiveFactor = emissive is { } emissiveColor
                ? new Vector3(emissiveColor.R, emissiveColor.G, emissiveColor.B)
                : material.EmissiveFactor,
            EmissiveStrength = source.EmissiveStrength ?? material.EmissiveStrength,
            MetallicFactor = source.Metallic ?? material.MetallicFactor,
            RoughnessFactor = source.Roughness ?? material.RoughnessFactor,
            OcclusionStrength = source.OcclusionStrength ?? material.OcclusionStrength,
            NormalScale = source.NormalScale ?? material.NormalScale,
            AlphaMode = alphaMode,
            AlphaCutoff = source.AlphaCutoff ?? material.AlphaCutoff,
            DoubleSided = source.DoubleSided ?? material.DoubleSided,
            ReceivesShadows = source.ReceivesShadows ?? material.ReceivesShadows,
            RenderBlendModeOverride = blendMode,
            ShadingModel = shadingModel,
            EmissionGiParticipation = emissionParticipation,
            DiffuseGiParticipation = diffuseParticipation,
            FeatureFlags = featureFlags,
            Extensions = extensions
        });
    }

    public SceneMaterialOverrideDocument? Capture(RenderObject renderObject)
    {
        ArgumentNullException.ThrowIfNull(renderObject);
        if (renderObject.Material is not MaterialHandle handle)
            return null;
        MaterialDefinition material = _materials.GetMaterialDefinition(handle);
        return new SceneMaterialOverrideDocument
        {
            Name = material.Name,
            Albedo = new SceneColor(
                material.BaseColorFactor.X,
                material.BaseColorFactor.Y,
                material.BaseColorFactor.Z,
                material.BaseColorFactor.W),
            EmissiveColor = new SceneColor(
                material.EmissiveFactor.X,
                material.EmissiveFactor.Y,
                material.EmissiveFactor.Z,
                1f),
            EmissiveStrength = material.EmissiveStrength,
            Metallic = material.MetallicFactor,
            Roughness = material.RoughnessFactor,
            OcclusionStrength = material.OcclusionStrength,
            NormalScale = material.NormalScale,
            AlphaMode = material.AlphaMode.ToString(),
            AlphaCutoff = material.AlphaCutoff,
            DoubleSided = material.DoubleSided,
            ReceivesShadows = material.ReceivesShadows,
            RenderBlendModeOverride = material.RenderBlendModeOverride?.ToString() ??
                SceneMaterialOverrideDocument.AutomaticBlendMode,
            ShadingModel = material.ShadingModel.ToString(),
            DiffuseGiParticipation = material.DiffuseGiParticipation.ToString(),
            EmissionGiParticipation = material.EmissionGiParticipation.ToString(),
            GiTransmissionPolicy = material.Extensions.TransmissionPolicy.ToString(),
            ThinTransmissionFactor = material.Extensions.TransmissionFactor,
            ThinTransmissionTint = new SceneColor(
                material.Extensions.ThinTransmissionTint.X,
                material.Extensions.ThinTransmissionTint.Y,
                material.Extensions.ThinTransmissionTint.Z,
                1f)
        };
    }

    private static T ParseEnum<T>(
        string? value,
        T fallback,
        string fieldName)
        where T : struct, Enum
    {
        if (value == null)
            return fallback;

        foreach (string name in Enum.GetNames<T>())
        {
            if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<T>(name);
        }

        throw new ArgumentOutOfRangeException(
            fieldName,
            value,
            $"Scene material {fieldName} value is invalid.");
    }

    private static MaterialBlendMode? ParseBlendMode(
        string? value,
        MaterialBlendMode? fallback)
    {
        if (value == null)
            return fallback;
        if (string.Equals(
                value,
                SceneMaterialOverrideDocument.AutomaticBlendMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return ParseEnum(
            value,
            MaterialBlendMode.Opaque,
            nameof(SceneMaterialOverrideDocument.RenderBlendModeOverride));
    }

    private static GiParticipationOverride ParseGiParticipation(
        string? value,
        bool? legacyValue,
        GiParticipationOverride fallback,
        string fieldName)
    {
        if (value != null)
            return ParseEnum(value, fallback, fieldName);

        return legacyValue switch
        {
            true => GiParticipationOverride.Enabled,
            false => GiParticipationOverride.Disabled,
            null => fallback
        };
    }
}
