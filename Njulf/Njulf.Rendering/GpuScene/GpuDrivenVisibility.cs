using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.GpuScene;

[Flags]
public enum GpuVisibilityOverflowFlags : uint
{
    None = 0,
    Opaque = 1u << 0,
    Masked = 1u << 1,
    Transparent = 1u << 2,
    Shadow = 1u << 3
}

public readonly record struct GpuVisibilityCapacity(
    int OpaqueMeshlets,
    int MaskedMeshlets,
    int TransparentMeshlets,
    int ShadowMeshlets)
{
    public static GpuVisibilityCapacity Initial { get; } = new(65536, 32768, 32768, 65536);
}

public enum GpuVisibilityIndirectList : int
{
    SolidDepth = 0,
    MaskedDepth = 1,
    Opaque = 2,
    Transparent = 3,
    DirectionalShadow0 = 4,
    LocalShadow0 = 8,
    Count = 64
}

public static class GpuVisibilityLayout
{
    public const int ShadowSettingsMaxDirectionalCascades = 4;
    public const int MaxSpotShadowLists = 32;
    public const int MaxPointShadowLists = 4;
    public const int PointShadowFaces = 6;
    public const int MaxLocalShadowLists = MaxSpotShadowLists + MaxPointShadowLists * PointShadowFaces;
    public const int IndirectListCount = 4 + ShadowSettingsMaxDirectionalCascades + MaxLocalShadowLists;
    public const int IndirectCommandStride = 12;
    public const int PerListCounterCount = ShadowSettingsMaxDirectionalCascades + MaxLocalShadowLists;

    public static int DirectionalShadowListIndex(int cascade) => 4 + cascade;
    public static int SpotShadowListIndex(int spotIndex) => 4 + ShadowSettingsMaxDirectionalCascades + spotIndex;
    public static int PointShadowFaceListIndex(int pointIndex, int faceIndex) =>
        4 + ShadowSettingsMaxDirectionalCascades + MaxSpotShadowLists + pointIndex * PointShadowFaces + faceIndex;
}

public sealed class GpuVisibilityCapacityPlanner
{
    public GpuVisibilityCapacity Capacity { get; private set; }
    public int ResizeCount { get; private set; }

    public GpuVisibilityCapacityPlanner(GpuVisibilityCapacity? initialCapacity = null)
    {
        Capacity = initialCapacity ?? GpuVisibilityCapacity.Initial;
    }

    public bool ApplyCounters(GPUVisibilityCounters counters)
    {
        GpuVisibilityCapacity next = Capacity;
        bool resized = false;
        resized |= GrowIfNeeded(Math.Max(counters.RequiredOpaqueCapacity, counters.RequiredSolidDepthCapacity), Capacity.OpaqueMeshlets, value => next = next with { OpaqueMeshlets = value });
        resized |= GrowIfNeeded(counters.RequiredMaskedCapacity, Capacity.MaskedMeshlets, value => next = next with { MaskedMeshlets = value });
        resized |= GrowIfNeeded(counters.RequiredTransparentCapacity, Capacity.TransparentMeshlets, value => next = next with { TransparentMeshlets = value });
        resized |= GrowIfNeeded(Math.Max(counters.RequiredShadowCapacity, Math.Max(counters.RequiredDirectionalShadowCapacity, counters.RequiredLocalShadowCapacity)), Capacity.ShadowMeshlets, value => next = next with { ShadowMeshlets = value });
        if (!resized)
            return false;

        Capacity = next;
        ResizeCount++;
        return true;
    }

    private static bool GrowIfNeeded(uint required, int current, Action<int> setValue)
    {
        if (required <= current)
            return false;

        int next = Math.Max(1, current);
        while (next < required)
            next = checked(next * 2);
        setValue(next);
        return true;
    }
}

public readonly record struct GpuVisibilityListSignature(int Count, ulong Hash)
{
    public static GpuVisibilityListSignature FromDrawCommands(IReadOnlyList<GPUMeshletDrawCommand> commands)
    {
        if (commands == null)
            throw new ArgumentNullException(nameof(commands));

        ulong hash = 14695981039346656037ul;
        foreach (GPUMeshletDrawCommand command in commands)
        {
            hash = Mix(hash, command.MeshletIndex);
            hash = Mix(hash, command.InstanceId);
            hash = Mix(hash, command.MaterialIndex);
        }

        return new GpuVisibilityListSignature(commands.Count, hash);
    }

    private static ulong Mix(ulong hash, uint value)
    {
        hash ^= value;
        return hash * 1099511628211ul;
    }
}

public sealed record GpuVisibilityComparisonResult(
    string ListName,
    GpuVisibilityListSignature Cpu,
    GpuVisibilityListSignature Gpu)
{
    public bool Matches => Cpu.Equals(Gpu);
    public string Diagnostic => Matches
        ? $"{ListName}: match count={Cpu.Count} hash=0x{Cpu.Hash:X16}"
        : $"{ListName}: mismatch cpu(count={Cpu.Count}, hash=0x{Cpu.Hash:X16}) gpu(count={Gpu.Count}, hash=0x{Gpu.Hash:X16})";
}

public static class GpuVisibilitySortKeyPacker
{
    public static GPUVisibilitySortKey PackTransparentKey(
        float depth,
        uint materialLayer,
        uint materialIndex,
        uint objectId,
        uint meshletIndex,
        uint drawIndex)
    {
        uint depthKey = PackReverseDepth(depth);
        ulong key =
            ((ulong)depthKey << 32) |
            ((ulong)(materialLayer & 0xFFu) << 24) |
            ((ulong)(materialIndex & 0xFFFu) << 12) |
            ((ulong)((objectId ^ meshletIndex) & 0xFFFu));
        return new GPUVisibilitySortKey { Key = key, DrawIndex = drawIndex };
    }

    private static uint PackReverseDepth(float depth)
    {
        if (!float.IsFinite(depth))
            depth = 0f;

        float clamped = Math.Clamp(depth, 0f, 1f);
        return uint.MaxValue - (uint)MathF.Round(clamped * uint.MaxValue);
    }
}
