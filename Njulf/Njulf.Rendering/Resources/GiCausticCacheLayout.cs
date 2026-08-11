using System;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Checked byte plan for the isolated C4 cache.  These bytes are deliberately
/// not folded into any DDGI atlas/source-cache category.
/// </summary>
public readonly record struct GiCausticCacheLayout(
    int PhotonTaskCapacity,
    int PhotonRecordStride,
    int WriteBankCount,
    int CacheBankCount,
    int MaximumPhotonsPerCell,
    int MaximumOccupiedCells,
    int CellTableCapacity,
    ulong PhotonRecordBytes,
    ulong CellTableBytes,
    ulong SortScratchBytes,
    ulong HistoryBytes,
    ulong TotalBytes,
    bool IsValid,
    string FailureReason)
{
    public const int ReferencePhotonStride = 80;
    public const int CellTableEntryStride = 32;
    public const int SortKeyAndIndexStride = 16;

    public static GiCausticCacheLayout Empty(string reason = "disabled") => new(
        0, 0, 0, 0, 0, 0, 0, 0UL, 0UL, 0UL, 0UL, 0UL, false, reason);

    public ulong PersistentBytes => checked(PhotonRecordBytes + CellTableBytes + HistoryBytes);
    public ulong ScratchBytes => SortScratchBytes;
}

/// <summary>
/// Exact word-addressed scratch layout used by the deterministic C4 GPU
/// radix/compaction builder.  The two index banks are followed by per-workgroup
/// 256-bin histograms and prefixes plus one global bin-base table.  Once radix
/// sorting completes, the second index bank and histogram region are reused by
/// the two deterministic compaction scans; no hidden allocation is required.
/// </summary>
public readonly record struct GiCausticDeterministicBuildScratchLayout(
    uint PhotonCapacity,
    uint WorkgroupCount,
    uint IndexBank0WordOffset,
    uint IndexBank1WordOffset,
    uint HistogramWordOffset,
    uint GroupPrefixWordOffset,
    uint BinBaseWordOffset,
    uint RequiredWordCount,
    ulong RequiredBytes)
{
    public const uint AbiRevision = 0xC4_02_0001u;
    public const uint WorkgroupSize = 128u;
    public const uint RadixBinCount = 256u;
    public const uint ScratchHeaderWords = 16u;
    public const uint RadixKeyCount = 7u;
    public const uint RadixBytesPerKey = 4u;
    public const uint RadixPassCount = RadixKeyCount * RadixBytesPerKey;

    public uint CompactPhotonPrefixWordOffset => IndexBank1WordOffset;
    public uint CompactCellPrefixWordOffset => GroupPrefixWordOffset;
    public uint CompactPhotonGroupSumWordOffset => HistogramWordOffset;
    public uint CompactPhotonGroupOffsetWordOffset => checked(
        HistogramWordOffset + WorkgroupCount);
    public uint CompactCellGroupSumWordOffset => checked(
        HistogramWordOffset + 2u * WorkgroupCount);
    public uint CompactCellGroupOffsetWordOffset => checked(
        HistogramWordOffset + 3u * WorkgroupCount);

    public static bool TryCreate(
        int photonCapacity,
        out GiCausticDeterministicBuildScratchLayout layout)
    {
        layout = default;
        if (photonCapacity <= 0)
            return false;

        try
        {
            ulong capacity = checked((uint)photonCapacity);
            ulong groups = 1UL + (capacity - 1UL) / WorkgroupSize;
            ulong bank0 = ScratchHeaderWords;
            ulong bank1 = checked(bank0 + capacity);
            ulong histogram = checked(bank1 + capacity);
            ulong groupPrefix = checked(histogram + groups * RadixBinCount);
            ulong binBase = checked(groupPrefix + groups * RadixBinCount);
            ulong words = checked(binBase + RadixBinCount);
            if (groups > uint.MaxValue || words > uint.MaxValue)
                return false;

            // Storage allocations and descriptor ranges are kept naturally
            // aligned even though every shader address is a 32-bit word.
            ulong bytes = AlignUp(checked(words * sizeof(uint)), 16UL);
            layout = new GiCausticDeterministicBuildScratchLayout(
                checked((uint)capacity),
                checked((uint)groups),
                checked((uint)bank0),
                checked((uint)bank1),
                checked((uint)histogram),
                checked((uint)groupPrefix),
                checked((uint)binBase),
                checked((uint)words),
                bytes);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment) => checked(
        (value + alignment - 1UL) / alignment * alignment);
}

public static class GiCausticCacheLayoutCompiler
{
    /// <summary>
    /// Produces a complete all-or-nothing layout. A cache build writes at most
    /// one endpoint per task, so task capacity is also a strict append bound.
    /// </summary>
    public static GiCausticCacheLayout Compile(
        int photonTaskCapacity,
        int maximumPhotonsPerCell,
        int maximumOccupiedCells,
        int recordStride,
        int writeBankCount,
        int cacheBankCount,
        float targetLoadFactor,
        ulong historyBytes,
        ulong budgetBytes)
    {
        if (photonTaskCapacity <= 0 || maximumPhotonsPerCell <= 0 ||
            maximumOccupiedCells <= 0 || recordStride < GiCausticCacheLayout.ReferencePhotonStride ||
            (recordStride & 15) != 0 || writeBankCount is < 1 or > 2 ||
            cacheBankCount is < 1 or > 2 || !float.IsFinite(targetLoadFactor) ||
            targetLoadFactor <= 0.0f || targetLoadFactor > 0.5f || budgetBytes == 0UL)
        {
            return GiCausticCacheLayout.Empty("invalid-caustic-cache-layout-input");
        }

        try
        {
            if (!GiCausticPhotonCacheReference.TryNextPowerOfTwo(
                    checked((int)Math.Ceiling(maximumOccupiedCells / (double)targetLoadFactor)),
                    out int tableCapacity))
            {
                return GiCausticCacheLayout.Empty("cell-table-capacity-overflow");
            }

            // Write records are retained only once a bank is complete. During a
            // multi-frame construction, cache banks describe the previously
            // published/pending generations and write banks own append data.
            ulong photonRecordBytes = checked(
                (ulong)photonTaskCapacity * (ulong)recordStride * (ulong)writeBankCount);
            ulong cellTableBytes = checked(
                (ulong)tableCapacity * GiCausticCacheLayout.CellTableEntryStride *
                (ulong)cacheBankCount);
            if (!GiCausticDeterministicBuildScratchLayout.TryCreate(
                    photonTaskCapacity,
                    out GiCausticDeterministicBuildScratchLayout scratchLayout))
            {
                return GiCausticCacheLayout.Empty(
                    "caustic-deterministic-build-scratch-overflow");
            }
            ulong sortScratchBytes = scratchLayout.RequiredBytes;
            ulong total = checked(photonRecordBytes + cellTableBytes + sortScratchBytes + historyBytes);
            if (total > budgetBytes)
                return GiCausticCacheLayout.Empty("independent-caustic-memory-budget");

            return new GiCausticCacheLayout(
                photonTaskCapacity,
                recordStride,
                writeBankCount,
                cacheBankCount,
                maximumPhotonsPerCell,
                maximumOccupiedCells,
                tableCapacity,
                photonRecordBytes,
                cellTableBytes,
                sortScratchBytes,
                historyBytes,
                total,
                true,
                "valid");
        }
        catch (OverflowException)
        {
            return GiCausticCacheLayout.Empty("caustic-cache-layout-overflow");
        }
    }
}
