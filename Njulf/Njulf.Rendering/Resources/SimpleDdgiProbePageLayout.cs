using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>Integer coordinate used by the pure Simple-DDGI paging oracle.</summary>
public readonly record struct SimpleDdgiProbeGridCoordinate(int X, int Y, int Z);

/// <summary>
/// Immutable per-volume address description.  Virtual probe indices retain the
/// existing toroidal topology; payload indices are resolved independently.
/// </summary>
public readonly record struct SimpleDdgiVolumePageLayout(
    int VirtualFirstProbe,
    int PageTableFirst,
    int DensePhysicalFirstProbe,
    int SparsePoolFirstProbe,
    int GridCountX,
    int GridCountY,
    int GridCountZ,
    int PhysicalOffsetX,
    int PhysicalOffsetY,
    int PhysicalOffsetZ,
    SimpleDdgiProbeResidencyMode ResidencyMode)
{
    public int PageGridX => SimpleDdgiProbePageLayout.CeilDivide(
        GridCountX,
        SimpleDdgiProbePageLayout.PageDimensionX);

    public int PageGridY => SimpleDdgiProbePageLayout.CeilDivide(
        GridCountY,
        SimpleDdgiProbePageLayout.PageDimensionY);

    public int PageGridZ => SimpleDdgiProbePageLayout.CeilDivide(
        GridCountZ,
        SimpleDdgiProbePageLayout.PageDimensionZ);

    public int ProbeCount => checked(GridCountX * GridCountY * GridCountZ);
    public int VirtualPageCount => checked(PageGridX * PageGridY * PageGridZ);

    public void Validate()
    {
        if (VirtualFirstProbe < 0 || PageTableFirst < 0 ||
            DensePhysicalFirstProbe < 0 || SparsePoolFirstProbe < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(VirtualFirstProbe),
                "Simple-DDGI address bases must be non-negative.");
        }

        if (GridCountX <= 0 || GridCountY <= 0 || GridCountZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(GridCountX));
        if (!ResidencyMode.IsDefined())
            throw new ArgumentOutOfRangeException(nameof(ResidencyMode));

        _ = ProbeCount;
        _ = VirtualPageCount;
    }
}

/// <summary>Complete virtual-page result for one logical probe coordinate.</summary>
public readonly record struct SimpleDdgiVirtualPageAddress(
    int VirtualProbeIndex,
    int VirtualPageIndex,
    int PageLocalProbeIndex,
    SimpleDdgiProbeGridCoordinate ToroidalCoordinate,
    SimpleDdgiProbeGridCoordinate PageCoordinate,
    SimpleDdgiProbeGridCoordinate PageLocalCoordinate,
    bool ValidPayloadSlot);

/// <summary>
/// Receiver/update address.  A nonresident result never carries a usable
/// physical payload index.
/// </summary>
public readonly record struct SimpleDdgiProbeAddress(
    uint VirtualProbeIndex,
    uint PhysicalProbeIndex,
    uint PageMappingGeneration,
    bool Resident)
{
    public const uint InvalidPhysicalProbeIndex = uint.MaxValue;
    public const uint DenseMappingGeneration = uint.MaxValue;

    public static SimpleDdgiProbeAddress Dense(uint probeIndex) => new(
        probeIndex,
        probeIndex,
        DenseMappingGeneration,
        true);

    public static SimpleDdgiProbeAddress NonResident(uint virtualProbeIndex) => new(
        virtualProbeIndex,
        InvalidPhysicalProbeIndex,
        0u,
        false);
}

/// <summary>Pure page-table entry; zero physical-page-plus-one is invalid.</summary>
public readonly record struct SimpleDdgiPageTableEntry(
    uint PhysicalPagePlusOne,
    uint MappingGeneration,
    uint Flags,
    // Shadow-only opaque gather-oracle stamp; ignored by address validation.
    uint Reserved)
{
    public const uint ValidFlag = 1u << 0;
    public const uint InitializingFlag = 1u << 1;
    public const uint PublishedFlag = 1u << 2;
    public const uint SuppressedEmptyFlag = 1u << 3;

    public bool IsResident =>
        PhysicalPagePlusOne != 0u &&
        MappingGeneration != 0u &&
        (Flags & ValidFlag) != 0u;

    public int PhysicalPageIndex => IsResident
        ? checked((int)PhysicalPagePlusOne - 1)
        : -1;
}

/// <summary>Reverse owner identity for one compact physical page.</summary>
public readonly record struct SimpleDdgiPhysicalPageOwner(
    int VirtualPageIndex,
    uint MappingGeneration,
    uint ResidencyResourceGeneration,
    uint Flags,
    ulong LastRelevantFrame,
    ulong LastPublishedFrame)
{
    public const uint InFlightFlag = 1u << 0;
    public const uint PinnedFlag = 1u << 1;
    public bool IsOwned => VirtualPageIndex >= 0 && MappingGeneration != 0u;
}

/// <summary>
/// Checked append-only storage layout for the GPU residency arena.  Every
/// offset is 16-byte aligned and every byte is charged by the memory plan.
/// </summary>
public sealed class SimpleDdgiProbePageLayout
{
    public const int PageDimensionX = 2;
    public const int PageDimensionY = 2;
    public const int PageDimensionZ = 2;
    public const int ProbesPerPage = 8;
    public const uint DemandEpochMask = 0x00ff_ffffu;
    public const uint MappingGenerationWrapThreshold = uint.MaxValue - 65_535u;
    public const ulong RegionAlignment = 16UL;
    public const int MaxVolumeCount = GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount;

    public const ulong HeaderBytes = 64UL;
    public const ulong VolumePagingRecordBytes = 32UL;
    public const ulong PageTableEntryBytes = 16UL;
    public const ulong PageHistoryBytes = 16UL;
    // Mirrors GPUSimpleDdgiPhysicalPageMetadata and
    // SIMPLE_DDGI_PHYSICAL_PAGE_METADATA_WORDS. The final four words carry
    // allocation/schedule/publication latency witnesses; retaining the old
    // 32-byte stride would overlap adjacent owners and corrupt the page map.
    public const ulong PhysicalMetadataBytes = 48UL;
    public const ulong DemandCounterBytes = 256UL;
    public const int DevelopmentControlCounterWord = 32;
    public const uint DevelopmentControlValidFlag = 1u << 0;
    public const uint DevelopmentControlPinFlag = 1u << 1;
    public const ulong ClassificationScratchBytesPerPage = 16UL;
    public const ulong PrefixScratchBytesPerPage = 4UL;
    public const ulong CandidateScratchBytesPerPage = 8UL;
    public const ulong VictimScratchBytesPerPage = 8UL;
    public const ulong InitWorkBytesPerPage = 16UL;
    public const ulong IndirectCommandBytes = 64UL;
    public const ulong FeedbackSummaryBytes = 1_024UL;
    public const ulong CurrentProfileOverheadGateBytes = 512UL * 1024UL;
    public const ulong HardLimitOverheadGateBytes = 1UL * 1024UL * 1024UL;

    private SimpleDdgiProbePageLayout(
        int virtualPageCount,
        int sparsePhysicalPageCapacity,
        int maximumAdmissionsPerFrame,
        ulong headerOffset,
        ulong volumePagingOffset,
        ulong pageTableOffset,
        ulong pageHistoryOffset,
        ulong physicalMetadataOffset,
        ulong demandCountersOffset,
        ulong classificationScratchOffset,
        ulong prefixScratchOffset,
        ulong candidateScratchOffset,
        ulong victimScratchOffset,
        ulong initWorkOffset,
        ulong indirectCommandsOffset,
        ulong feedbackOffset,
        ulong totalBytes)
    {
        VirtualPageCount = virtualPageCount;
        SparsePhysicalPageCapacity = sparsePhysicalPageCapacity;
        MaximumAdmissionsPerFrame = maximumAdmissionsPerFrame;
        HeaderOffset = headerOffset;
        VolumePagingOffset = volumePagingOffset;
        PageTableOffset = pageTableOffset;
        PageHistoryOffset = pageHistoryOffset;
        PhysicalMetadataOffset = physicalMetadataOffset;
        DemandCountersOffset = demandCountersOffset;
        ClassificationScratchOffset = classificationScratchOffset;
        PrefixScratchOffset = prefixScratchOffset;
        CandidateScratchOffset = candidateScratchOffset;
        VictimScratchOffset = victimScratchOffset;
        InitWorkOffset = initWorkOffset;
        IndirectCommandsOffset = indirectCommandsOffset;
        FeedbackOffset = feedbackOffset;
        TotalBytes = totalBytes;
    }

    public int VirtualPageCount { get; }
    public int SparsePhysicalPageCapacity { get; }
    public int MaximumAdmissionsPerFrame { get; }
    public ulong HeaderOffset { get; }
    public ulong VolumePagingOffset { get; }
    public ulong PageTableOffset { get; }
    public ulong PageHistoryOffset { get; }
    public ulong PhysicalMetadataOffset { get; }
    public ulong DemandCountersOffset { get; }
    public ulong ClassificationScratchOffset { get; }
    public ulong PrefixScratchOffset { get; }
    public ulong CandidateScratchOffset { get; }
    public ulong VictimScratchOffset { get; }
    public ulong InitWorkOffset { get; }
    public ulong IndirectCommandsOffset { get; }
    public ulong FeedbackOffset { get; }
    public ulong TotalBytes { get; }

    public ulong PageTableBytes => checked(
        (ulong)VirtualPageCount * PageTableEntryBytes);

    public ulong PageHistoryRegionBytes => checked(
        (ulong)VirtualPageCount * PageHistoryBytes);

    public ulong PhysicalMetadataRegionBytes => checked(
        (ulong)SparsePhysicalPageCapacity * PhysicalMetadataBytes);

    public ulong FeedbackOffsetWords => FeedbackOffset / sizeof(uint);

    public static SimpleDdgiProbePageLayout Create(
        int virtualPageCount,
        int sparsePhysicalPageCapacity,
        int maximumAdmissionsPerFrame,
        ulong maxStorageBufferRange = ulong.MaxValue)
    {
        if (virtualPageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(virtualPageCount));
        if (sparsePhysicalPageCapacity < 0 ||
            sparsePhysicalPageCapacity > virtualPageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sparsePhysicalPageCapacity));
        }
        if (maximumAdmissionsPerFrame < 0 ||
            maximumAdmissionsPerFrame > sparsePhysicalPageCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAdmissionsPerFrame));
        }

        ulong cursor = 0UL;
        ulong header = Append(ref cursor, HeaderBytes);
        ulong volumePaging = Append(
            ref cursor,
            checked((ulong)MaxVolumeCount * VolumePagingRecordBytes));
        ulong pageTable = Append(
            ref cursor,
            checked((ulong)virtualPageCount * PageTableEntryBytes));
        ulong pageHistory = Append(
            ref cursor,
            checked((ulong)virtualPageCount * PageHistoryBytes));
        ulong physicalMetadata = Append(
            ref cursor,
            checked((ulong)sparsePhysicalPageCapacity * PhysicalMetadataBytes));
        ulong demandCounters = Append(ref cursor, DemandCounterBytes);
        ulong classificationScratch = Append(
            ref cursor,
            checked((ulong)virtualPageCount * ClassificationScratchBytesPerPage));
        ulong prefixScratch = Append(
            ref cursor,
            checked((ulong)virtualPageCount * PrefixScratchBytesPerPage));
        ulong candidateScratch = Append(
            ref cursor,
            checked((ulong)virtualPageCount * CandidateScratchBytesPerPage));
        ulong victimScratch = Append(
            ref cursor,
            checked((ulong)sparsePhysicalPageCapacity * VictimScratchBytesPerPage));
        ulong initWork = Append(
            ref cursor,
            checked((ulong)maximumAdmissionsPerFrame * InitWorkBytesPerPage));
        ulong indirectCommands = Append(ref cursor, IndirectCommandBytes);
        ulong feedback = Append(ref cursor, FeedbackSummaryBytes);
        ulong total = Align16(cursor);
        if (total > maxStorageBufferRange)
        {
            throw new InvalidOperationException(
                $"Simple-DDGI residency arena requires {total} bytes, exceeding the device storage-buffer range {maxStorageBufferRange}.");
        }

        return new SimpleDdgiProbePageLayout(
            virtualPageCount,
            sparsePhysicalPageCapacity,
            maximumAdmissionsPerFrame,
            header,
            volumePaging,
            pageTable,
            pageHistory,
            physicalMetadata,
            demandCounters,
            classificationScratch,
            prefixScratch,
            candidateScratch,
            victimScratch,
            initWork,
            indirectCommands,
            feedback,
            total);
    }

    public static int ResolveVirtualPageCount(int countX, int countY, int countZ)
    {
        if (countX <= 0 || countY <= 0 || countZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(countX));
        return checked(
            CeilDivide(countX, PageDimensionX) *
            CeilDivide(countY, PageDimensionY) *
            CeilDivide(countZ, PageDimensionZ));
    }

    public static SimpleDdgiVirtualPageAddress ResolveVirtualPageAddress(
        in SimpleDdgiVolumePageLayout volume,
        int logicalX,
        int logicalY,
        int logicalZ)
    {
        volume.Validate();
        if ((uint)logicalX >= (uint)volume.GridCountX ||
            (uint)logicalY >= (uint)volume.GridCountY ||
            (uint)logicalZ >= (uint)volume.GridCountZ)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalX));
        }

        int physicalX = PositiveModulo(
            logicalX + volume.PhysicalOffsetX,
            volume.GridCountX);
        int physicalY = PositiveModulo(
            logicalY + volume.PhysicalOffsetY,
            volume.GridCountY);
        int physicalZ = PositiveModulo(
            logicalZ + volume.PhysicalOffsetZ,
            volume.GridCountZ);

        int pageX = physicalX / PageDimensionX;
        int pageY = physicalY / PageDimensionY;
        int pageZ = physicalZ / PageDimensionZ;
        int localX = physicalX % PageDimensionX;
        int localY = physicalY % PageDimensionY;
        int localZ = physicalZ % PageDimensionZ;
        int localProbe = FlattenPageLocal(localX, localY, localZ);
        int localVirtualProbe = FlattenProbe(
            physicalX,
            physicalY,
            physicalZ,
            volume.GridCountX,
            volume.GridCountY);
        int localPage = FlattenProbe(
            pageX,
            pageY,
            pageZ,
            volume.PageGridX,
            volume.PageGridY);

        return new SimpleDdgiVirtualPageAddress(
            checked(volume.VirtualFirstProbe + localVirtualProbe),
            checked(volume.PageTableFirst + localPage),
            localProbe,
            new SimpleDdgiProbeGridCoordinate(physicalX, physicalY, physicalZ),
            new SimpleDdgiProbeGridCoordinate(pageX, pageY, pageZ),
            new SimpleDdgiProbeGridCoordinate(localX, localY, localZ),
            true);
    }

    public static bool TryResolveVirtualProbeFromPage(
        in SimpleDdgiVolumePageLayout volume,
        int volumeLocalPageIndex,
        int pageLocalProbeIndex,
        out int virtualProbeIndex)
    {
        volume.Validate();
        virtualProbeIndex = -1;
        if ((uint)volumeLocalPageIndex >= (uint)volume.VirtualPageCount ||
            (uint)pageLocalProbeIndex >= ProbesPerPage)
        {
            return false;
        }

        Unflatten(
            volumeLocalPageIndex,
            volume.PageGridX,
            volume.PageGridY,
            out int pageX,
            out int pageY,
            out int pageZ);
        UnflattenPageLocal(
            pageLocalProbeIndex,
            out int localX,
            out int localY,
            out int localZ);
        int physicalX = pageX * PageDimensionX + localX;
        int physicalY = pageY * PageDimensionY + localY;
        int physicalZ = pageZ * PageDimensionZ + localZ;
        if (physicalX >= volume.GridCountX ||
            physicalY >= volume.GridCountY ||
            physicalZ >= volume.GridCountZ)
        {
            return false;
        }

        virtualProbeIndex = checked(
            volume.VirtualFirstProbe +
            FlattenProbe(
                physicalX,
                physicalY,
                physicalZ,
                volume.GridCountX,
                volume.GridCountY));
        return true;
    }

    public static SimpleDdgiProbeAddress ResolveProbeAddress(
        in SimpleDdgiVolumePageLayout volume,
        in SimpleDdgiVirtualPageAddress virtualAddress,
        in SimpleDdgiPageTableEntry pageEntry,
        in SimpleDdgiPhysicalPageOwner reverseOwner,
        uint residencyResourceGeneration)
    {
        uint virtualProbe = checked((uint)virtualAddress.VirtualProbeIndex);
        if (!volume.ResidencyMode.UsesSparsePayloads())
        {
            uint physical = checked((uint)(
                volume.DensePhysicalFirstProbe +
                virtualAddress.VirtualProbeIndex -
                volume.VirtualFirstProbe));
            return new SimpleDdgiProbeAddress(
                virtualProbe,
                physical,
                SimpleDdgiProbeAddress.DenseMappingGeneration,
                true);
        }

        int physicalPage = pageEntry.PhysicalPageIndex;
        if (!pageEntry.IsResident || physicalPage < 0 ||
            !reverseOwner.IsOwned ||
            reverseOwner.VirtualPageIndex != virtualAddress.VirtualPageIndex ||
            reverseOwner.MappingGeneration != pageEntry.MappingGeneration ||
            reverseOwner.ResidencyResourceGeneration != residencyResourceGeneration)
        {
            return SimpleDdgiProbeAddress.NonResident(virtualProbe);
        }

        uint physicalProbe = checked((uint)(
            volume.SparsePoolFirstProbe +
            physicalPage * ProbesPerPage +
            virtualAddress.PageLocalProbeIndex));
        return new SimpleDdgiProbeAddress(
            virtualProbe,
            physicalProbe,
            pageEntry.MappingGeneration,
            true);
    }

    public static uint AdvanceNonZeroGeneration(uint generation)
    {
        uint next = generation + 1u;
        return next == 0u ? 1u : next;
    }

    public static uint DemandEpochForFrame(uint frameIndex) =>
        frameIndex % (DemandEpochMask - 1u) + 1u;

    /// <summary>
    /// The packed demand stamp has only 24 epoch bits. Reusing epoch one in an
    /// existing arena could let an ancient atomic-max winner masquerade as a
    /// current request, so the owner installs a cleared arena transaction at
    /// each wrap boundary before any producer records that frame.
    /// </summary>
    public static bool DemandEpochRequiresResourceTransaction(uint frameIndex) =>
        frameIndex != 0u && frameIndex % (DemandEpochMask - 1u) == 0u;

    public static int CeilDivide(int value, int divisor)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (divisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(divisor));
        return checked((value + divisor - 1) / divisor);
    }

    public static int PositiveModulo(int value, int modulus)
    {
        if (modulus <= 0)
            throw new ArgumentOutOfRangeException(nameof(modulus));
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static int FlattenProbe(
        int x,
        int y,
        int z,
        int countX,
        int countY) =>
        checked(x + y * countX + z * countX * countY);

    private static int FlattenPageLocal(int x, int y, int z) =>
        x + y * PageDimensionX + z * PageDimensionX * PageDimensionY;

    private static void Unflatten(
        int index,
        int countX,
        int countY,
        out int x,
        out int y,
        out int z)
    {
        int xy = checked(countX * countY);
        z = index / xy;
        int remainder = index - z * xy;
        y = remainder / countX;
        x = remainder - y * countX;
    }

    private static void UnflattenPageLocal(
        int index,
        out int x,
        out int y,
        out int z)
    {
        z = index / (PageDimensionX * PageDimensionY);
        int remainder = index - z * PageDimensionX * PageDimensionY;
        y = remainder / PageDimensionX;
        x = remainder - y * PageDimensionX;
    }

    private static ulong Append(ref ulong cursor, ulong bytes)
    {
        cursor = Align16(cursor);
        ulong offset = cursor;
        cursor = checked(cursor + bytes);
        return offset;
    }

    private static ulong Align16(ulong value) => checked(
        (value + RegionAlignment - 1UL) & ~(RegionAlignment - 1UL));
}
