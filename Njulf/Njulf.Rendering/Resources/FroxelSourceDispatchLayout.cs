namespace Njulf.Rendering.Resources;

internal static class FroxelSourceDispatchLayout
{
    internal const ulong DispatchCommandByteSize = 3UL * sizeof(uint);

    internal static ulong CommandOffsetBytes(ulong clusterCount) =>
        checked((clusterCount + 1UL) * sizeof(uint));

    internal static ulong BufferByteSize(ulong clusterCount) =>
        checked(CommandOffsetBytes(clusterCount) + DispatchCommandByteSize);
}
