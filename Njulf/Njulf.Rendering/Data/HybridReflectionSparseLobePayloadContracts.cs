using System;

namespace Njulf.Rendering.Data;

/// <summary>
/// Frozen ABI for the optimized hybrid-reflection lobe sidecar. The buffer is
/// screen-linear and contains the exact two words formerly exported through
/// the R32G32_UINT forward attachment. It is cleared before raster, so pixels
/// whose material does not need anisotropy or clearcoat data remain zero.
/// </summary>
public static class HybridReflectionSparseLobePayloadAbi
{
    public const uint Version = 1u;
    public const uint WordsPerPixel = 2u;
    public const uint BytesPerPixel = WordsPerPixel * sizeof(uint);

    public static ulong ResolveBufferBytes(uint width, uint height)
    {
        if (width == 0u || height == 0u)
            return 0UL;
        return checked((ulong)width * height * BytesPerPixel);
    }

    public static uint ResolvePixelWordOffset(
        uint x,
        uint y,
        uint width,
        uint height)
    {
        if (width == 0u || height == 0u || x >= width || y >= height)
            throw new ArgumentOutOfRangeException(nameof(x));
        return checked((y * width + x) * WordsPerPixel);
    }
}
