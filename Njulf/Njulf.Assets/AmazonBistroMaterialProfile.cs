using System;
using System.IO;
using Njulf.Core.Math;

namespace Njulf.Assets;

/// <summary>
/// Physical corrections for Amazon Bistro materials whose FBX transport loses
/// renderer semantics. The profile is admitted only by the explicit
/// AmazonBistro import convention, an exact Bistro asset identity, and a
/// stable base-texture identity.
/// </summary>
internal static class AmazonBistroMaterialProfile
{
    internal const string ProfileRevision = "amazon-bistro-material-profile/v3";

    public static bool Apply(string modelPath, ModelMaterial material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(material);

        if (!IsBistroAsset(modelPath) ||
            !TryResolveBaseTextureIdentity(material, out string identity))
        {
            return false;
        }

        if (IsFoliageBaseColor(identity))
        {
            material.AlphaMode = ModelAlphaMode.Mask;
            material.AlphaCutoff = 0.5f;
            material.DoubleSided = true;
            material.FeatureFlags |= ModelMaterialFeatureBits.Foliage;
            return true;
        }

        if (!TryResolveThinGlassProfile(identity, out ThinGlassProfile profile))
            return false;

        material.IsThinGlass = true;
        material.AlphaMode = ModelAlphaMode.Blend;
        material.DoubleSided = true;
        material.Metallic = 0.0f;
        material.Roughness = profile.Roughness;
        material.TransmissionFactor = profile.Transmission;
        material.GiTransmissionPolicy = ModelGiTransmissionPolicy.ThinSurface;
        material.ThinTransmissionTint = profile.Tint;
        material.Ior = profile.Ior;
        material.ThicknessFactor = 0.0f;
        material.AttenuationDistance = float.PositiveInfinity;
        material.AttenuationColor = Vector4.One;
        material.SpecularFactor = 1.0f;
        material.FeatureFlags |=
            ModelMaterialFeatureBits.Transmission |
            ModelMaterialFeatureBits.Ior;
        return true;
    }

    private static bool IsBistroAsset(string modelPath)
    {
        string fileName = Path.GetFileName(modelPath);
        return fileName.Equals("BistroExterior.fbx", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("BistroInterior.fbx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveBaseTextureIdentity(
        ModelMaterial material,
        out string identity)
    {
        ModelTextureSource? source = material.BaseColorTexture?.Source;
        identity = source?.CacheIdentity ??
                   source?.FilePath ??
                   source?.DebugName ??
                   material.AlbedoTexturePath ??
                   string.Empty;
        return !string.IsNullOrWhiteSpace(identity);
    }

    private static bool IsFoliageBaseColor(string identity)
    {
        string stem = Path.GetFileNameWithoutExtension(identity);
        return stem.Equals("Foliage_Bux_Hedges46_BaseColor",
                   StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Foliage_Flowers_BaseColor",
                   StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Foliage_Ivy_leaf_a_BaseColor",
                   StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Foliage_Leaves_BaseColor",
                   StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Foliage_Linde_Tree_Large_Green_Leaves_BaseColor",
                   StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Foliage_Linde_Tree_Large_Orange_Leaves_BaseColor",
                   StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Plants_plants_BaseColor",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveThinGlassProfile(
        string identity,
        out ThinGlassProfile profile)
    {
        string stem = Path.GetFileNameWithoutExtension(identity);
        if (stem.Equals("MASTER_Glass_Exterior_BaseColor",
                StringComparison.OrdinalIgnoreCase))
        {
            profile = new ThinGlassProfile(
                Transmission: 0.94f,
                Roughness: 0.08f,
                Ior: 1.52f,
                Tint: new Vector4(0.94f, 0.98f, 1.0f, 1.0f));
            return true;
        }

        if (stem.Equals("TransparentGlass_BaseColor",
                StringComparison.OrdinalIgnoreCase))
        {
            profile = new ThinGlassProfile(
                Transmission: 0.96f,
                Roughness: 0.05f,
                Ior: 1.52f,
                Tint: new Vector4(0.97f, 0.99f, 1.0f, 1.0f));
            return true;
        }

        if (stem.Equals("MASTER_Glass_Dirty_BaseColor",
                StringComparison.OrdinalIgnoreCase))
        {
            profile = new ThinGlassProfile(
                Transmission: 0.78f,
                Roughness: 0.28f,
                Ior: 1.52f,
                Tint: new Vector4(0.90f, 0.94f, 0.92f, 1.0f));
            return true;
        }

        if (stem.Equals("MASTER_Glass_Dirty_MASKED_BaseColor",
                StringComparison.OrdinalIgnoreCase))
        {
            profile = new ThinGlassProfile(
                Transmission: 0.70f,
                Roughness: 0.34f,
                Ior: 1.52f,
                Tint: new Vector4(0.88f, 0.92f, 0.90f, 1.0f));
            return true;
        }

        if (stem.Equals("MASTER_Frosted_Glass_BaseColor",
                StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("MASTER_Interior_01_Frozen_Glass_BaseColor",
                StringComparison.OrdinalIgnoreCase))
        {
            profile = new ThinGlassProfile(
                Transmission: 0.58f,
                Roughness: 0.72f,
                Ior: 1.50f,
                Tint: new Vector4(0.92f, 0.96f, 1.0f, 1.0f));
            return true;
        }

        profile = default;
        return false;
    }

    private readonly record struct ThinGlassProfile(
        float Transmission,
        float Roughness,
        float Ior,
        Vector4 Tint);
}
