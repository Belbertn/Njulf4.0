namespace Njulf.Rendering.Resources;

/// <summary>
/// Word-addressed ABI shared by particle draw commands, simulation dispatches,
/// and sort dispatches in the particle indirect-argument buffer.
/// </summary>
internal static class GpuParticleDispatchLayout
{
    internal const uint WorkgroupSize = 256u;
    internal const uint DrawCommandCount = 5u;
    internal const uint DrawCommandWordStride = 4u;
    internal const uint DispatchCommandWordStride = 4u;
    internal const uint SortedBucketCount = 3u;

    internal const uint ActiveListSelectorWord =
        DrawCommandCount * DrawCommandWordStride;
    internal const uint NextAliveCountWord = ActiveListSelectorWord + 1u;
    internal const uint CurrentAliveCountWord = ActiveListSelectorWord + 2u;
    internal const uint UpdateDispatchWord = 24u;
    internal const uint EmitDispatchWord = 28u;
    internal const uint SortWorkDispatchBaseWord = 32u;
    internal const uint SortPrefixDispatchBaseWord = 44u;
    internal const uint TotalWordCount = 56u;

    internal static ulong ByteOffset(uint wordOffset) =>
        checked((ulong)wordOffset * sizeof(uint));

    internal static uint SortWorkDispatchWord(uint sortedBucketOrdinal) =>
        checked(SortWorkDispatchBaseWord +
            sortedBucketOrdinal * DispatchCommandWordStride);

    internal static uint SortPrefixDispatchWord(uint sortedBucketOrdinal) =>
        checked(SortPrefixDispatchBaseWord +
            sortedBucketOrdinal * DispatchCommandWordStride);

    internal static uint GroupCount(uint itemCount) => itemCount == 0u
        ? 0u
        : checked(1u + (itemCount - 1u) / WorkgroupSize);

    internal static uint AliveIndexElementCount(uint particleCapacity) =>
        checked(particleCapacity * 2u);

    internal static uint SortKeyElementCount(uint particleCapacity) =>
        checked(particleCapacity * DrawCommandCount);

    internal static uint SortScratchRequiredWordCount(
        uint particleCapacity) => checked(
        particleCapacity * 4u +
        GroupCount(particleCapacity) * WorkgroupSize +
        WorkgroupSize);
}
