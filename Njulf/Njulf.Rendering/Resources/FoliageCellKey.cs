using System;
using Njulf.Core.Foliage;
using Njulf.Core.Math;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Stable world-cell identity for one foliage patch. Coordinates are global,
/// so unchanged candidates retain the same identity across unload/reload.
/// </summary>
public readonly record struct FoliageCellKey(
    Guid PatchId,
    int X,
    int Z,
    int CellSizeMillimeters) : IComparable<FoliageCellKey>
{
    public static FoliageCellKey FromWorld(
        FoliagePatch patch,
        Vector3 worldPosition)
    {
        ArgumentNullException.ThrowIfNull(patch);
        float cellSize = ResolveCellSize(patch);
        return new FoliageCellKey(
            patch.Id,
            checked((int)MathF.Floor(worldPosition.X / cellSize)),
            checked((int)MathF.Floor(worldPosition.Z / cellSize)),
            checked((int)MathF.Round(cellSize * 1000.0f)));
    }

    public BoundingBox ResolveBounds(FoliagePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        float cellSize = CellSizeMillimeters / 1000.0f;
        return new BoundingBox(
            new Vector3(
                X * cellSize,
                patch.Bounds.Min.Y,
                Z * cellSize),
            new Vector3(
                (X + 1) * cellSize,
                patch.Bounds.Max.Y,
                (Z + 1) * cellSize));
    }

    public ulong StableIdentity
    {
        get
        {
            Span<byte> bytes = stackalloc byte[16];
            PatchId.TryWriteBytes(bytes);
            ulong hash = 1469598103934665603UL;
            foreach (byte value in bytes)
                hash = (hash ^ value) * 1099511628211UL;
            hash = Mix(hash, unchecked((uint)X));
            hash = Mix(hash, unchecked((uint)Z));
            return Mix(hash, checked((uint)CellSizeMillimeters));
        }
    }

    public int CompareTo(FoliageCellKey other)
    {
        int patch = PatchId.CompareTo(other.PatchId);
        if (patch != 0)
            return patch;
        int size = CellSizeMillimeters.CompareTo(other.CellSizeMillimeters);
        if (size != 0)
            return size;
        int x = X.CompareTo(other.X);
        return x != 0 ? x : Z.CompareTo(other.Z);
    }

    internal static float ResolveCellSize(FoliagePatch patch)
    {
        float value = patch.Placement.CellSize;
        return float.IsFinite(value) && value >= 1.0f ? value : 32.0f;
    }

    private static ulong Mix(ulong seed, uint value)
    {
        seed ^= value;
        seed *= 1099511628211UL;
        return seed;
    }
}
