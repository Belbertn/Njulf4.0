using System;

namespace Njulf.Rendering.Diagnostics;

public sealed record DescriptorPressureSnapshot(
    int TextureCapacity,
    int TextureUsed,
    int TextureHighWater,
    int SamplerCapacity,
    int SamplerUsed,
    int SamplerHighWater,
    int DescriptorWrites)
{
    public float TextureUsageRatio => TextureCapacity <= 0 ? 0f : (float)TextureUsed / TextureCapacity;
    public float SamplerUsageRatio => SamplerCapacity <= 0 ? 0f : (float)SamplerUsed / SamplerCapacity;
    public bool IsTextureExhausted => TextureCapacity > 0 && TextureUsed >= TextureCapacity;
    public bool IsSamplerExhausted => SamplerCapacity > 0 && SamplerUsed >= SamplerCapacity;

    public string FormatExhaustionFailure(string poolName, int requestedCount)
    {
        return $"{poolName} descriptor capacity exhausted. Requested={requestedCount}, " +
            $"textures={TextureUsed}/{TextureCapacity} highWater={TextureHighWater}, " +
            $"samplers={SamplerUsed}/{SamplerCapacity} highWater={SamplerHighWater}.";
    }
}
