using System.Runtime.InteropServices;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

internal readonly record struct MeshletResolvedMappingDirtyRange(
    int FirstMapping,
    int MappingCount);

internal sealed record MeshletResolvedMappingUpdate(
    GPUMeshletResolvedMapping[] Mappings,
    IReadOnlyList<MeshletResolvedMappingDirtyRange> DirtyRanges,
    uint[] PublishedRangeStateWords,
    IReadOnlyList<int> InvalidReadyRanges);

/// <summary>
/// Incrementally materializes the frame-local physical address table consumed
/// by compacted commands. Only mappings owned by a changed physical page are
/// rebuilt, so steady frames perform no mapping upload.
/// </summary>
internal sealed class MeshletResolvedMappingTable
{
    private const int FrameCount = 2;

    private readonly FrameState[] _frames =
        [new FrameState(), new FrameState()];
    private GPUMeshletVirtualMapping[] _virtualMappings = [];
    private GPUMeshletStreamingRange[] _streamingRanges = [];
    private int[][] _mappingIndicesByPage = [];
    private int[][] _rangeIndicesByPage = [];

    public void SetContracts(
        GPUMeshletVirtualMapping[] virtualMappings,
        GPUMeshletStreamingRange[] streamingRanges)
    {
        ArgumentNullException.ThrowIfNull(virtualMappings);
        ArgumentNullException.ThrowIfNull(streamingRanges);
        if ((ulong)virtualMappings.Length >
            (ulong)MeshletVirtualAddress.IndexMask + 1UL)
        {
            throw new InvalidOperationException(
                "Resolved meshlet mappings exceed the tagged address space.");
        }

        _virtualMappings = (GPUMeshletVirtualMapping[])virtualMappings.Clone();
        _streamingRanges =
            (GPUMeshletStreamingRange[])streamingRanges.Clone();
        int pageCount = CalculatePageCount(
            _virtualMappings,
            _streamingRanges);
        _mappingIndicesByPage = BuildMappingPageIndex(
            _virtualMappings,
            pageCount);
        _rangeIndicesByPage = BuildRangePageIndex(
            _streamingRanges,
            pageCount);
        foreach (FrameState frame in _frames)
            frame.ContractsDirty = true;
    }

    public MeshletResolvedMappingUpdate Update(
        int frameSlot,
        ReadOnlySpan<GPUMeshletPageTableEntry> pageTable,
        ReadOnlySpan<uint> rangeStateWords,
        Func<int, int, ReadOnlyMemory<byte>> packedPageResolver)
    {
        if ((uint)frameSlot >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        ArgumentNullException.ThrowIfNull(packedPageResolver);

        FrameState frame = _frames[frameSlot];
        bool contractsDirty = frame.ContractsDirty;
        EnsureFrameCapacity(frame, pageTable.Length);

        int comparedPageCount = Math.Max(
            Math.Max(frame.PageTable.Length, pageTable.Length),
            _mappingIndicesByPage.Length);
        var dirtyPages = new List<int>();
        for (int pageIndex = 0; pageIndex < comparedPageCount; pageIndex++)
        {
            GPUMeshletPageTableEntry previous = pageIndex <
                frame.PageTable.Length
                    ? frame.PageTable[pageIndex]
                    : GPUMeshletPageTableEntry.Unmapped;
            GPUMeshletPageTableEntry current = pageIndex < pageTable.Length
                ? pageTable[pageIndex]
                : GPUMeshletPageTableEntry.Unmapped;
            if (contractsDirty || previous != current)
                dirtyPages.Add(pageIndex);
        }

        var dirtyMappings = new List<int>();
        var dirtyRanges = new HashSet<int>();
        foreach (int pageIndex in dirtyPages)
        {
            ResolvePage(
                frame,
                pageIndex,
                pageTable,
                packedPageResolver,
                dirtyMappings);
            if ((uint)pageIndex < (uint)_rangeIndicesByPage.Length)
            {
                foreach (int rangeIndex in _rangeIndicesByPage[pageIndex])
                    dirtyRanges.Add(rangeIndex);
            }
        }

        if (contractsDirty)
        {
            for (int rangeIndex = 0;
                 rangeIndex < _streamingRanges.Length;
                 rangeIndex++)
            {
                dirtyRanges.Add(rangeIndex);
            }
        }

        foreach (int rangeIndex in dirtyRanges)
        {
            frame.RangeValid[rangeIndex] = ValidateRange(
                frame,
                _streamingRanges[rangeIndex]);
        }

        frame.PageTable = pageTable.ToArray();
        frame.ContractsDirty = false;

        uint[] publishedRangeState = rangeStateWords.ToArray();
        var invalidReadyRanges = new List<int>();
        for (int wordIndex = 0;
             wordIndex < publishedRangeState.Length;
             wordIndex++)
        {
            uint ready = publishedRangeState[wordIndex];
            while (ready != 0u)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(ready);
                int rangeIndex = checked(wordIndex * 32 + bit);
                bool valid = (uint)rangeIndex < (uint)frame.RangeValid.Length &&
                    frame.RangeValid[rangeIndex];
                if (!valid)
                {
                    publishedRangeState[wordIndex] &= ~(1u << bit);
                    invalidReadyRanges.Add(rangeIndex);
                }
                ready &= ready - 1u;
            }
        }

        dirtyMappings.Sort();
        return new MeshletResolvedMappingUpdate(
            frame.Mappings,
            CoalesceDirtyMappings(dirtyMappings),
            publishedRangeState,
            invalidReadyRanges);
    }

    private void ResolvePage(
        FrameState frame,
        int pageIndex,
        ReadOnlySpan<GPUMeshletPageTableEntry> pageTable,
        Func<int, int, ReadOnlyMemory<byte>> packedPageResolver,
        List<int> dirtyMappings)
    {
        int[] mappingIndices = (uint)pageIndex <
            (uint)_mappingIndicesByPage.Length
                ? _mappingIndicesByPage[pageIndex]
                : [];
        foreach (int mappingIndex in mappingIndices)
        {
            frame.Mappings[mappingIndex] =
                GPUMeshletResolvedMapping.Invalid;
            dirtyMappings.Add(mappingIndex);
        }

        if ((uint)pageIndex >= (uint)frame.PageValid.Length)
            return;
        frame.PageValid[pageIndex] = false;
        if ((uint)pageIndex >= (uint)pageTable.Length)
            return;

        GPUMeshletPageTableEntry tableEntry = pageTable[pageIndex];
        if (!tableEntry.IsResident ||
            tableEntry.BankIndex >=
                MeshletPhysicalBankAllocator.MaximumBankCount ||
            tableEntry.PageIndexInBank >=
                MeshletPhysicalBankAllocator.PagesPerBank)
        {
            return;
        }

        int physicalSlot = checked(
            (int)tableEntry.BankIndex *
                MeshletPhysicalBankAllocator.PagesPerBank +
            (int)tableEntry.PageIndexInBank);
        try
        {
            ReadOnlyMemory<byte> packedPage =
                packedPageResolver(pageIndex, physicalSlot);
            GPUMeshletPhysicalPageHeader header =
                MeshletGpuPagePacker.ReadHeader(packedPage.Span);
            uint pageBaseWord = checked(
                tableEntry.PageIndexInBank *
                (uint)(MeshletPhysicalBankAllocator.PageSizeBytes /
                    sizeof(uint)));
            uint meshletSectionWord = checked(
                pageBaseWord + header.MeshletWordOffset);
            uint vertexSectionWord = checked(
                pageBaseWord + header.VertexIndexWordOffset);
            uint triangleSectionWord = checked(
                pageBaseWord + header.TriangleIndexWordOffset);
            bool pageValid =
                meshletSectionWord <= GPUMeshletResolvedMapping.WordMask &&
                vertexSectionWord <= GPUMeshletResolvedMapping.WordMask &&
                triangleSectionWord <= GPUMeshletResolvedMapping.WordMask;

            foreach (int mappingIndex in mappingIndices)
            {
                GPUMeshletVirtualMapping mapping =
                    _virtualMappings[mappingIndex];
                if (mapping.PageLocalMeshletIndex >= header.MeshletCount)
                {
                    pageValid = false;
                    continue;
                }

                int recordByteOffset = checked(
                    (int)header.MeshletWordOffset * sizeof(uint) +
                    (int)mapping.PageLocalMeshletIndex *
                        Marshal.SizeOf<GPUPackedMeshlet>());
                if (recordByteOffset < 0 ||
                    recordByteOffset + Marshal.SizeOf<GPUPackedMeshlet>() >
                        packedPage.Length)
                {
                    pageValid = false;
                    continue;
                }

                GPUPackedMeshlet meshlet = MemoryMarshal.Read<GPUPackedMeshlet>(
                    packedPage.Span.Slice(
                        recordByteOffset,
                        Marshal.SizeOf<GPUPackedMeshlet>()));
                uint recordWord = checked(
                    meshletSectionWord +
                    mapping.PageLocalMeshletIndex *
                        (uint)(Marshal.SizeOf<GPUPackedMeshlet>() /
                            sizeof(uint)));
                if (recordWord > GPUMeshletResolvedMapping.WordMask ||
                    vertexSectionWord + meshlet.LocalVertexOffset >
                        GPUMeshletResolvedMapping.WordMask ||
                    triangleSectionWord + meshlet.LocalTriangleOffset >
                        GPUMeshletResolvedMapping.WordMask)
                {
                    pageValid = false;
                    continue;
                }

                frame.Mappings[mappingIndex] = new GPUMeshletResolvedMapping(
                    GPUMeshletResolvedMapping.PackAddress(
                        tableEntry.BankIndex,
                        recordWord),
                    GPUMeshletResolvedMapping.PackAddress(
                        tableEntry.BankIndex,
                        vertexSectionWord),
                    GPUMeshletResolvedMapping.PackAddress(
                        tableEntry.BankIndex,
                        triangleSectionWord),
                    mapping.VertexOffset);
            }

            frame.PageValid[pageIndex] = pageValid &&
                mappingIndices.All(index => frame.Mappings[index].IsValid);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            frame.PageValid[pageIndex] = false;
        }
    }

    private bool ValidateRange(
        FrameState frame,
        GPUMeshletStreamingRange range)
    {
        if (range.PageCount == 0u || range.MeshletCount == 0u)
            return false;
        ulong pageEnd = checked((ulong)range.FirstGlobalPageId +
            range.PageCount);
        ulong mappingEnd = checked((ulong)range.FirstVirtualMeshlet +
            range.MeshletCount);
        if (pageEnd > (ulong)frame.PageValid.Length ||
            mappingEnd > (ulong)frame.Mappings.Length)
        {
            return false;
        }
        for (uint offset = 0; offset < range.PageCount; offset++)
        {
            if (!frame.PageValid[checked(
                    (int)(range.FirstGlobalPageId + offset))])
            {
                return false;
            }
        }
        for (uint offset = 0; offset < range.MeshletCount; offset++)
        {
            if (!frame.Mappings[checked(
                    (int)(range.FirstVirtualMeshlet + offset))].IsValid)
            {
                return false;
            }
        }
        return true;
    }

    private void EnsureFrameCapacity(FrameState frame, int pageTableLength)
    {
        if (frame.Mappings.Length != _virtualMappings.Length)
        {
            var mappings = new GPUMeshletResolvedMapping[
                _virtualMappings.Length];
            Array.Fill(mappings, GPUMeshletResolvedMapping.Invalid);
            frame.Mappings = mappings;
        }
        int pageCount = Math.Max(pageTableLength, _mappingIndicesByPage.Length);
        if (frame.PageValid.Length != pageCount)
            Array.Resize(ref frame.PageValid, pageCount);
        if (frame.RangeValid.Length != _streamingRanges.Length)
            Array.Resize(ref frame.RangeValid, _streamingRanges.Length);
    }

    private static IReadOnlyList<MeshletResolvedMappingDirtyRange>
        CoalesceDirtyMappings(List<int> dirtyMappings)
    {
        if (dirtyMappings.Count == 0)
            return Array.Empty<MeshletResolvedMappingDirtyRange>();
        var result = new List<MeshletResolvedMappingDirtyRange>();
        int first = dirtyMappings[0];
        int previous = first;
        for (int index = 1; index < dirtyMappings.Count; index++)
        {
            int current = dirtyMappings[index];
            if (current == previous)
                continue;
            if (current != previous + 1)
            {
                result.Add(new MeshletResolvedMappingDirtyRange(
                    first,
                    previous - first + 1));
                first = current;
            }
            previous = current;
        }
        result.Add(new MeshletResolvedMappingDirtyRange(
            first,
            previous - first + 1));
        return result;
    }

    private static int CalculatePageCount(
        ReadOnlySpan<GPUMeshletVirtualMapping> mappings,
        ReadOnlySpan<GPUMeshletStreamingRange> ranges)
    {
        ulong maximum = 0;
        foreach (GPUMeshletVirtualMapping mapping in mappings)
            maximum = Math.Max(maximum, (ulong)mapping.GlobalPageId + 1UL);
        foreach (GPUMeshletStreamingRange range in ranges)
        {
            maximum = Math.Max(
                maximum,
                (ulong)range.FirstGlobalPageId + range.PageCount);
        }
        if (maximum > int.MaxValue)
            throw new InvalidOperationException("Meshlet page IDs exceed managed indexing limits.");
        return checked((int)maximum);
    }

    private static int[][] BuildMappingPageIndex(
        ReadOnlySpan<GPUMeshletVirtualMapping> mappings,
        int pageCount)
    {
        var counts = new int[pageCount];
        foreach (GPUMeshletVirtualMapping mapping in mappings)
            counts[checked((int)mapping.GlobalPageId)]++;
        var result = new int[pageCount][];
        for (int page = 0; page < pageCount; page++)
            result[page] = new int[counts[page]];
        Array.Clear(counts);
        for (int index = 0; index < mappings.Length; index++)
        {
            int page = checked((int)mappings[index].GlobalPageId);
            result[page][counts[page]++] = index;
        }
        return result;
    }

    private static int[][] BuildRangePageIndex(
        ReadOnlySpan<GPUMeshletStreamingRange> ranges,
        int pageCount)
    {
        var lists = new List<int>?[pageCount];
        for (int rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
        {
            GPUMeshletStreamingRange range = ranges[rangeIndex];
            for (uint offset = 0; offset < range.PageCount; offset++)
            {
                int page = checked((int)(range.FirstGlobalPageId + offset));
                (lists[page] ??= []).Add(rangeIndex);
            }
        }
        var result = new int[pageCount][];
        for (int page = 0; page < pageCount; page++)
            result[page] = lists[page]?.ToArray() ?? [];
        return result;
    }

    private sealed class FrameState
    {
        public GPUMeshletResolvedMapping[] Mappings = [];
        public GPUMeshletPageTableEntry[] PageTable = [];
        public bool[] PageValid = [];
        public bool[] RangeValid = [];
        public bool ContractsDirty = true;
    }
}
