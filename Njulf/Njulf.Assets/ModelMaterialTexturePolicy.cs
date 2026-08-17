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
/// texture authentication. Foliage shading is intentionally independent from
/// alpha coverage: only alpha-tested materials need coverage-preserving mips.
/// </summary>
public static class ModelMaterialTexturePolicy
{
    public static ModelTextureMipPolicy ResolveBaseColorMipPolicy(
        ModelMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (material.AlphaMode != ModelAlphaMode.Mask)
            return ModelTextureMipPolicy.Standard;

        if (!float.IsFinite(material.AlphaCutoff) || material.AlphaCutoff < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                "Masked material alpha cutoff must be finite and non-negative.");
        }

        return new ModelTextureMipPolicy(
            PreserveAlphaCoverage: true,
            AlphaCutoff: material.AlphaCutoff);
    }
}
