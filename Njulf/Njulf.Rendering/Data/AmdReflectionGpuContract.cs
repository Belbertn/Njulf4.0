using System;

namespace Njulf.Rendering.Data;

/// <summary>Word offsets shared with amd_reflection_callbacks.glsl.</summary>
internal static class AmdReflectionGpuContract
{
    internal const uint HeaderWords = 128;
    internal const uint IndirectOffset = 52 * 4;
    internal const uint ReducedFlag = 512;

    internal readonly record struct Allocation(uint Width, uint Height, uint PlaneCount,
        uint HitOffset, uint TileOffset, ulong Bytes);

    internal static Allocation Layout(uint width, uint height, bool reduced, int bank)
    {
        if (width == 0 || height == 0 || bank is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(width));
        uint w = reduced ? width / 2 + width % 2 : width;
        uint h = reduced ? height / 2 + height % 2 : height;
        ulong pixels = (ulong)w * h;
        ulong tiles = ((ulong)w + 7) / 8 * (((ulong)h + 7) / 8);
        uint planes = reduced ? (bank == 0 ? 21u : 12u) : (bank == 0 ? 16u : 7u);
        ulong end = HeaderWords + pixels * planes + (bank == 0 ? tiles * 2 : 0);
        uint hits = reduced ? checked((uint)end) : HeaderWords;
        if (reduced) end += (ulong)width * height;
        uint tileOffset = checked((uint)end);
        if (reduced) end += tiles;
        return new(w, h, planes, hits, tileOffset, checked(end * 4));
    }
}
