using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// The fixed command slots written by the GPU scheduler.  The slots are part
/// of the cross-language ABI: consumers must use the command produced for the
/// current frame and must never derive a dispatch count from a CPU queue.
/// </summary>
public enum SimpleDdgiSchedulerDispatchSlot : byte
{
    Reset = 0,
    Classify = 1,
    Prefix = 2,
    LaneBase = 3,
    Compact = 4,
    Admit = 5,
    Emit = 6,
    CommitLocal = 7,
    CommitPropagation = 8,
    Feedback = 9,
    Trace = 10,
    Relocate = 11,
    Transport = 12,
    Blend = 13,
    Publish = 14,
    Count = 15
}

/// <summary>One 16-byte aligned range in the resident scheduler arena.</summary>
public readonly record struct SimpleDdgiSchedulerArenaRegion(
    string Name,
    ulong Offset,
    ulong ByteSize,
    uint ElementStride,
    uint ElementCount)
{
    public ulong End => checked(Offset + ByteSize);
    public bool IsEmpty => ByteSize == 0 || ElementCount == 0;
    public uint OffsetWords => checked((uint)(Offset / sizeof(uint)));
    public uint ByteSize32 => checked((uint)ByteSize);
}

/// <summary>
/// Immutable capacity/layout description for the single GPU-resident
/// Simple-DDGI scheduler buffer.
///
/// The layout is deliberately a pure managed value graph.  It can therefore
/// be validated in unit tests without a Vulkan device, while the runtime uses
/// exactly the same offsets when it registers and binds the arena.
/// </summary>
public sealed class SimpleDdgiGpuSchedulerLayout
{
    public const ulong ArenaAlignmentBytes = 16;
    // The sparse-capable internal proposal adds two full-width physical owner
    // words. Keep the maximum shipping arena under an explicit 6.25 MiB gate;
    // this is concrete allocated capacity, not current scheduler occupancy.
    public const ulong ShippingArenaBudgetBytes = 25UL * 256UL * 1024UL;
    public const int ShippingFeedbackBytes = 4 * 1024;
    public const int MaxDirtyRegionCapacity = 1024;
    public const int SchedulerWorkgroupSize = 64;
    // Private scheduler state is distinct from the public 32-byte probe state.
    // It carries both dirty-latency start and the applied invalidation marker.
    public const int ProbeStateStrideBytes = 40;
    public const int CandidateStrideBytes = 32;
    // Source-ray cardinality is derived from (volume, transport category) at
    // admission: hard/routine source work always uses the policy's full-ray
    // count, while cached-solver work uses zero. The storage form therefore
    // needs only the first seven words; the public candidate ABI remains 32 B.
    public const int CandidateInputStorageStrideBytes = 28;
    // Compact output stores the deterministic input-candidate index rather
    // than copying the full 32-byte record. Admission dereferences that index
    // in CandidateInput, preserving the ABI while avoiding a second full-size
    // candidate pool.
    public const int CandidateCompactIndexStrideBytes = sizeof(uint);
    // Internal update/proposal storage has nine words. The scheduler carries
    // the small classification proposal in private flag bits until emit
    // strips them from the public queue ABI; relocation then reuses all seven
    // words for its transaction-private state proposal.
    public const int UpdateRecordStrideBytes = 36;
    public const int OutcomeStrideBytes = 60;
    public const int IndirectCommandStrideBytes = 16;
    // Ray-bucket metadata and commands are separate ABI regions.  The command
    // region is passed directly to vkCmdDispatchIndirect; metadata is read by
    // the trace/transport shaders and can therefore never be interpreted as a
    // dispatch dimension.
    public const int RayBucketMetadataStrideBytes = 16;
    public const int FrameBytes = 160;
    public const int VolumePolicyStrideBytes = 176;
    public const int DirtyRegionStrideBytes = 48;
    public const int LaneScalarStrideBytes = sizeof(uint);
    public const int CounterBytes = 256;
    // One 1 KiB epoch-stamped reduction record keeps the audit summary below
    // the plan's readback budget while leaving room for future counters.
    public const int AuditSummaryBytes = 1024;
    public const int AuditSummaryWordCount = AuditSummaryBytes / sizeof(uint);
    public const int MaxRayBucketCount = SimpleDdgiSchedulerAbi.MaxRayBucketCount;

    private readonly Dictionary<string, SimpleDdgiSchedulerArenaRegion> _regions;

    private SimpleDdgiGpuSchedulerLayout(
        int activeProbeCount,
        int activeVolumeCount,
        int requestCapacity,
        int dirtyRegionCapacity,
        bool validationEnabled,
        int activeLaneCount,
        ulong totalBytes,
        ulong validationReadbackBytes,
        IReadOnlyList<SimpleDdgiSchedulerArenaRegion> regions)
    {
        ActiveProbeCount = activeProbeCount;
        ActiveVolumeCount = activeVolumeCount;
        RequestCapacity = requestCapacity;
        DirtyRegionCapacity = dirtyRegionCapacity;
        ValidationEnabled = validationEnabled;
        ActiveLaneCount = activeLaneCount;
        TotalBytes = totalBytes;
        ValidationReadbackBytes = validationReadbackBytes;
        Regions = regions;
        _regions = new Dictionary<string, SimpleDdgiSchedulerArenaRegion>(
            regions.Count,
            StringComparer.Ordinal);
        foreach (SimpleDdgiSchedulerArenaRegion region in regions)
            _regions.Add(region.Name, region);
    }

    public int ActiveProbeCount { get; }
    public int ActiveVolumeCount { get; }
    public int RequestCapacity { get; }
    public int DirtyRegionCapacity { get; }
    public bool ValidationEnabled { get; }
    public int ActiveLaneCount { get; }
    public int LaneCapacity => SimpleDdgiSchedulerAbi.MaxLaneCount;
    public int CandidateGroupCount => checked((int)GroupsFor(ActiveProbeCount));
    public int CandidateGroupLaneCountWordCount => checked((int)(
        ((ulong)CandidateGroupCount * (ulong)LaneCapacity + 1UL) / 2UL));
    public ulong TotalBytes { get; }
    public ulong ValidationReadbackBytes { get; }
    public IReadOnlyList<SimpleDdgiSchedulerArenaRegion> Regions { get; }

    public SimpleDdgiSchedulerArenaRegion Frame => GetRegion(nameof(Frame));
    public SimpleDdgiSchedulerArenaRegion VolumePolicies => GetRegion(nameof(VolumePolicies));
    public SimpleDdgiSchedulerArenaRegion PreviousVolumePolicies => GetRegion(nameof(PreviousVolumePolicies));
    public SimpleDdgiSchedulerArenaRegion DirtyRegions => GetRegion(nameof(DirtyRegions));
    public SimpleDdgiSchedulerArenaRegion ProbeState => GetRegion(nameof(ProbeState));
    public SimpleDdgiSchedulerArenaRegion CandidateInput => GetRegion(nameof(CandidateInput));
    public SimpleDdgiSchedulerArenaRegion CandidateGroupLaneCounts => GetRegion(nameof(CandidateGroupLaneCounts));
    public SimpleDdgiSchedulerArenaRegion CandidateOutput => GetRegion(nameof(CandidateOutput));
    public SimpleDdgiSchedulerArenaRegion UpdateRecords => GetRegion(nameof(UpdateRecords));
    public SimpleDdgiSchedulerArenaRegion LaneCandidateCounts => GetRegion(nameof(LaneCandidateCounts));
    public SimpleDdgiSchedulerArenaRegion LanePrefixes => GetRegion(nameof(LanePrefixes));
    public SimpleDdgiSchedulerArenaRegion LaneTotals => GetRegion(nameof(LaneTotals));
    public SimpleDdgiSchedulerArenaRegion LaneCursors => GetRegion(nameof(LaneCursors));
    public SimpleDdgiSchedulerArenaRegion LaneAdmission => GetRegion(nameof(LaneAdmission));
    public SimpleDdgiSchedulerArenaRegion Counters => GetRegion(nameof(Counters));
    public SimpleDdgiSchedulerArenaRegion RayBucketMetadata => GetRegion(nameof(RayBucketMetadata));
    public SimpleDdgiSchedulerArenaRegion RayBucketCommands => GetRegion(nameof(RayBucketCommands));
    public SimpleDdgiSchedulerArenaRegion IndirectCommands => GetRegion(nameof(IndirectCommands));
    public SimpleDdgiSchedulerArenaRegion Outcomes => GetRegion(nameof(Outcomes));
    public SimpleDdgiSchedulerArenaRegion FeedbackSummary => GetRegion(nameof(FeedbackSummary));
    public SimpleDdgiSchedulerArenaRegion AuditSummary => GetRegion(nameof(AuditSummary));

    public SimpleDdgiSchedulerArenaRegion GetRegion(string name)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));
        return _regions.TryGetValue(name, out SimpleDdgiSchedulerArenaRegion region)
            ? region
            : throw new KeyNotFoundException($"Unknown Simple-DDGI scheduler arena region '{name}'.");
    }

    public SimpleDdgiSchedulerArenaRegion GetIndirectCommand(SimpleDdgiSchedulerDispatchSlot slot)
    {
        if ((uint)slot >= (uint)SimpleDdgiSchedulerDispatchSlot.Count)
            throw new ArgumentOutOfRangeException(nameof(slot));

        SimpleDdgiSchedulerArenaRegion commands = IndirectCommands;
        ulong offset = checked(commands.Offset +
            (ulong)slot * (ulong)IndirectCommandStrideBytes);
        return new SimpleDdgiSchedulerArenaRegion(
            $"Indirect.{slot}",
            offset,
            IndirectCommandStrideBytes,
            IndirectCommandStrideBytes,
            1);
    }

    /// <summary>
    /// Gets the Vulkan indirect-dispatch portion of a ray-bucket record. Its
    /// Y and Z dimensions are always one; queue offset/count/ray count live in
    /// the separate metadata portion so they cannot be interpreted as work.
    /// </summary>
    public SimpleDdgiSchedulerArenaRegion GetRayBucketIndirectCommand(int bucketIndex)
    {
        ValidateRayBucketIndex(bucketIndex);
        ulong offset = checked(RayBucketCommands.Offset +
            (ulong)bucketIndex * (ulong)IndirectCommandStrideBytes);
        return new SimpleDdgiSchedulerArenaRegion(
            $"RayBucket.{bucketIndex}.Indirect",
            offset,
            IndirectCommandStrideBytes,
            IndirectCommandStrideBytes,
            1);
    }

    /// <summary>
    /// Gets the scheduler-private ray-bucket metadata: queue offset, probe
    /// count, rays per probe, and a reserved word.
    /// </summary>
    public SimpleDdgiSchedulerArenaRegion GetRayBucketMetadata(int bucketIndex)
    {
        ValidateRayBucketIndex(bucketIndex);
        ulong offset = checked(RayBucketMetadata.Offset +
            (ulong)bucketIndex * (ulong)RayBucketMetadataStrideBytes);
        return new SimpleDdgiSchedulerArenaRegion(
            $"RayBucket.{bucketIndex}.Metadata",
            offset,
            RayBucketMetadataStrideBytes,
            RayBucketMetadataStrideBytes,
            1);
    }

    public static SimpleDdgiGpuSchedulerLayout Create(
        int activeProbeCount,
        int requestCapacity,
        int activeVolumeCount,
        int dirtyRegionCapacity = MaxDirtyRegionCapacity,
        bool validationEnabled = false,
        ulong maxStorageBufferRange = ulong.MaxValue,
        uint maxComputeWorkGroupCountX = 65_535)
    {
        if ((uint)activeProbeCount > GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount)
            throw new ArgumentOutOfRangeException(nameof(activeProbeCount));
        if ((uint)activeVolumeCount > GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            throw new ArgumentOutOfRangeException(nameof(activeVolumeCount));
        if (requestCapacity < 0 || requestCapacity > activeProbeCount)
            throw new ArgumentOutOfRangeException(nameof(requestCapacity));
        if (dirtyRegionCapacity < 0 || dirtyRegionCapacity > MaxDirtyRegionCapacity)
            throw new ArgumentOutOfRangeException(nameof(dirtyRegionCapacity));
        if (maxStorageBufferRange == 0)
            throw new ArgumentOutOfRangeException(nameof(maxStorageBufferRange));
        if (maxComputeWorkGroupCountX == 0)
            throw new ArgumentOutOfRangeException(nameof(maxComputeWorkGroupCountX));

        int activeLaneCount = checked(activeVolumeCount *
            (int)SimpleDdgiSchedulerWorkClass.Count *
            (int)SimpleDdgiSchedulerTransportCategory.Count *
            (int)SimpleDdgiSchedulerRayTier.Count);

        uint probeGroups = GroupsFor(activeProbeCount);
        uint requestGroups = GroupsFor(requestCapacity);
        uint laneGroups = GroupsFor(SimpleDdgiSchedulerAbi.MaxLaneCount);
        if (probeGroups > maxComputeWorkGroupCountX ||
            requestGroups > maxComputeWorkGroupCountX ||
            laneGroups > maxComputeWorkGroupCountX)
        {
            throw new InvalidOperationException(
                "The configured Simple-DDGI scheduler capacity exceeds the device compute dispatch limit.");
        }

        var regions = new List<SimpleDdgiSchedulerArenaRegion>(22);
        ulong cursor = 0;
        Add("Frame", FrameBytes, 1, FrameBytes);
        Add("VolumePolicies", checked((ulong)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * VolumePolicyStrideBytes),
            VolumePolicyStrideBytes, (uint)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);
        Add("PreviousVolumePolicies", checked((ulong)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount * VolumePolicyStrideBytes),
            VolumePolicyStrideBytes, (uint)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);
        Add("DirtyRegions", checked((ulong)dirtyRegionCapacity * DirtyRegionStrideBytes),
            DirtyRegionStrideBytes, checked((uint)dirtyRegionCapacity));
        Add("ProbeState", checked((ulong)activeProbeCount * ProbeStateStrideBytes),
            ProbeStateStrideBytes, checked((uint)activeProbeCount));
        Add("CandidateInput", checked((ulong)activeProbeCount * CandidateInputStorageStrideBytes),
            CandidateInputStorageStrideBytes, checked((uint)activeProbeCount));
        ulong candidateGroupLaneEntryCount = checked((ulong)probeGroups *
            (ulong)SimpleDdgiSchedulerAbi.MaxLaneCount);
        // Two 16-bit counters are packed into each 32-bit storage-buffer word.
        // The shader indexes this region in words, so reserve four bytes per
        // packed pair (not two bytes per logical counter). Under-allocation
        // here lets the prefix stage overwrite later arena regions, including
        // indirect command records.
        ulong candidateGroupLaneWordCount = checked((candidateGroupLaneEntryCount + 1UL) / 2UL);
        Add("CandidateGroupLaneCounts", checked(candidateGroupLaneWordCount * sizeof(uint)),
            sizeof(uint), checked((uint)candidateGroupLaneWordCount));
        Add("CandidateOutput", checked((ulong)activeProbeCount * CandidateCompactIndexStrideBytes),
            CandidateCompactIndexStrideBytes, checked((uint)activeProbeCount));
        // GPU mirror output is kept inside the scheduler arena. Resident mode
        // copies these records to the canonical consumer queue during emit;
        // mirror mode therefore cannot accidentally replace CPU authority.
        Add("UpdateRecords", checked((ulong)requestCapacity * UpdateRecordStrideBytes),
            UpdateRecordStrideBytes, checked((uint)requestCapacity));
        Add("LaneCandidateCounts", checked((ulong)SimpleDdgiSchedulerAbi.MaxLaneCount * LaneScalarStrideBytes),
            LaneScalarStrideBytes, (uint)SimpleDdgiSchedulerAbi.MaxLaneCount);
        Add("LanePrefixes", checked((ulong)SimpleDdgiSchedulerAbi.MaxLaneCount * LaneScalarStrideBytes),
            LaneScalarStrideBytes, (uint)SimpleDdgiSchedulerAbi.MaxLaneCount);
        Add("LaneTotals", checked((ulong)SimpleDdgiSchedulerAbi.MaxLaneCount * LaneScalarStrideBytes),
            LaneScalarStrideBytes, (uint)SimpleDdgiSchedulerAbi.MaxLaneCount);
        Add("LaneCursors", checked((ulong)SimpleDdgiSchedulerAbi.MaxLaneCount * LaneScalarStrideBytes),
            LaneScalarStrideBytes, (uint)SimpleDdgiSchedulerAbi.MaxLaneCount);
        Add("LaneAdmission", checked((ulong)SimpleDdgiSchedulerAbi.MaxLaneCount * LaneScalarStrideBytes),
            LaneScalarStrideBytes, (uint)SimpleDdgiSchedulerAbi.MaxLaneCount);
        Add("Counters", CounterBytes, 4, checked((uint)(CounterBytes / sizeof(uint))));
        Add("RayBucketMetadata", checked((ulong)MaxRayBucketCount * RayBucketMetadataStrideBytes),
            RayBucketMetadataStrideBytes, MaxRayBucketCount);
        Add("RayBucketCommands", checked((ulong)MaxRayBucketCount * IndirectCommandStrideBytes),
            IndirectCommandStrideBytes, MaxRayBucketCount);
        Add("IndirectCommands", checked((ulong)(int)SimpleDdgiSchedulerDispatchSlot.Count * IndirectCommandStrideBytes),
            IndirectCommandStrideBytes, (uint)SimpleDdgiSchedulerDispatchSlot.Count);
        Add("Outcomes", checked((ulong)requestCapacity * OutcomeStrideBytes),
            OutcomeStrideBytes, checked((uint)requestCapacity));
        Add("FeedbackSummary", ShippingFeedbackBytes, 4, checked((uint)(ShippingFeedbackBytes / sizeof(uint))));
        Add("AuditSummary", AuditSummaryBytes, sizeof(uint), AuditSummaryWordCount);

        ulong validationReadbackBytes = validationEnabled
            ? Align(ShippingFeedbackBytes, ArenaAlignmentBytes)
            : 0;
        if (cursor > maxStorageBufferRange)
        {
            throw new InvalidOperationException(
                $"The Simple-DDGI scheduler arena requires {cursor} bytes, above the device storage-buffer limit {maxStorageBufferRange}.");
        }
        if (validationReadbackBytes > ShippingFeedbackBytes)
            throw new InvalidOperationException("Scheduler validation readback exceeded the 4 KiB ABI limit.");

        return new SimpleDdgiGpuSchedulerLayout(
            activeProbeCount,
            activeVolumeCount,
            requestCapacity,
            dirtyRegionCapacity,
            validationEnabled,
            activeLaneCount,
            cursor,
            validationReadbackBytes,
            regions);

        void Add(string name, ulong byteSize, int elementStride, uint elementCount)
        {
            cursor = Align(cursor, ArenaAlignmentBytes);
            ulong alignedSize = byteSize == 0 ? 0 : Align(byteSize, ArenaAlignmentBytes);
            regions.Add(new SimpleDdgiSchedulerArenaRegion(
                name,
                cursor,
                alignedSize,
                checked((uint)elementStride),
                elementCount));
            cursor = checked(cursor + alignedSize);
        }
    }

    public static uint GroupsFor(int elementCount) => elementCount <= 0
        ? 0u
        : checked((uint)(((ulong)elementCount + SchedulerWorkgroupSize - 1) /
            SchedulerWorkgroupSize));

    public static ulong Align(ulong value, ulong alignment)
    {
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        return checked((value + alignment - 1) & ~(alignment - 1));
    }

    private static void ValidateRayBucketIndex(int bucketIndex)
    {
        if ((uint)bucketIndex >= MaxRayBucketCount)
            throw new ArgumentOutOfRangeException(nameof(bucketIndex));
    }
}
