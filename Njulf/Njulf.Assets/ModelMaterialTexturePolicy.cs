namespace Njulf.Assets;

/// <summary>
/// Resolved mip-generation policy for an authored material texture slot.
/// </summary>
public readonly record struct ModelTextureMipPolicy(
    bool PreserveAlphaCoverage,
    float AlphaCutoff)
{
    public static ModelTextureMipPolicy Standard { get; } = new(false, 0.5f);
}

/// <summary>
/// Defines the material semantics that affect texture cooking and runtime
/// texture authentication. Explicit foliage assets retain source alpha
/// coverage even when an importer reported the material as opaque; foliage
/// rasterization owns its own card/leaf coverage contract.
/// </summary>
public static class ModelMaterialTexturePolicy
{
    public static ModelTextureMipPolicy ResolveBaseColorMipPolicy(
        ModelMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        bool explicitFoliage =
            (material.FeatureFlags & ModelMaterialFeatureBits.Foliage) != 0;
        if (material.AlphaMode != ModelAlphaMode.Mask && !explicitFoliage)
            return ModelTextureMipPolicy.Standard;

        if (!float.IsFinite(material.AlphaCutoff) || material.AlphaCutoff < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                "Masked or foliage material alpha cutoff must be finite and non-negative.");
        }

        return new ModelTextureMipPolicy(
            PreserveAlphaCoverage: true,
            AlphaCutoff: material.AlphaCutoff);
    }
}
