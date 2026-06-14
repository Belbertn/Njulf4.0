using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record RenderGraphImageResourceInventory(
        string Name,
        string Format,
        string ResolutionClass,
        uint Width,
        uint Height,
        uint MipCount,
        uint ArrayLayers,
        string Usage,
        string Persistence,
        string Lifetime,
        IReadOnlyList<string> Producers,
        IReadOnlyList<string> Consumers,
        ulong EstimatedBytes);

    public sealed record RenderGraphBufferResourceInventory(
        string Name,
        ulong ByteSize,
        uint Stride,
        uint Count,
        string Usage,
        string Persistence,
        string Lifetime,
        IReadOnlyList<string> Producers,
        IReadOnlyList<string> Consumers);

    public sealed record RenderGraphResourceInventorySnapshot(
        IReadOnlyList<string> PassOrder,
        IReadOnlyList<RenderGraphImageResourceInventory> Images,
        IReadOnlyList<RenderGraphBufferResourceInventory> Buffers,
        ulong EstimatedImageBytes,
        ulong EstimatedBufferBytes)
    {
        public static RenderGraphResourceInventorySnapshot Empty { get; } = new(
            PassOrder: [],
            Images: [],
            Buffers: [],
            EstimatedImageBytes: 0,
            EstimatedBufferBytes: 0);
    }
}
