using System;

namespace Njulf.Rendering.Data;

internal static class OpticalDenoisingGpuContract
{
    public const uint HeaderWords = 64;
    public const uint PixelWords = 5;
    public const uint RecordWords = 64;
    public const uint MaximumLayers = 4;
    public const uint MaximumHistory = 16;
    // Diagnostic bit 16 is a capture-layer bit only in reflection captures.
    // Optical export is set exclusively by the two main transparent passes.
    public const uint ExportFlag = 1u << 16;

    public static (ulong Bytes, uint Capacity) Allocation(uint width, uint height,
        int budgetMiB, ulong maximumStorageRange, int banks = 2, bool compactExport = false)
    {
        if (width == 0 || height == 0 || banks < 2) return default;
        ulong pixels = checked((ulong)width * height);
        uint layers = compactExport ? 8u : MaximumLayers;
        uint pixelWords = compactExport ? 9u : PixelWords;
        ulong prefix = checked((HeaderWords + pixels * pixelWords) * 4);
        ulong budget = checked((ulong)budgetMiB * 1024 * 1024 / (uint)banks);
        if (maximumStorageRange != 0) budget = Math.Min(budget, maximumStorageRange);
        budget = Math.Min(budget, uint.MaxValue);
        if (budget <= prefix + RecordWords * 4) return default;
        uint capacity = checked((uint)Math.Min(pixels * layers,
            (budget - prefix) / (RecordWords * 4)));
        return (prefix + (ulong)capacity * RecordWords * 4, capacity);
    }

}
