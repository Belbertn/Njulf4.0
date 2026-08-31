using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Address-space contract shared by CPU submission and mesh shaders. Direct
/// meshlet indices keep bits 31 and 30 clear. Paged candidates use bit 31 as
/// an immutable virtual-table selector; commands resolved by compaction use
/// bit 30 to address the frame-local resolved table.
/// </summary>
public static class MeshletVirtualAddress
{
    public const uint VirtualBit = 0x8000_0000u;
    public const uint ResolvedBit = 0x4000_0000u;
    public const uint TagMask = VirtualBit | ResolvedBit;
    public const uint IndexMask = ResolvedBit - 1u;

    public static uint Encode(uint virtualTableIndex)
    {
        if (virtualTableIndex > IndexMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualTableIndex));
        }
        return VirtualBit | virtualTableIndex;
    }

    public static bool IsVirtual(uint address) =>
        (address & TagMask) == VirtualBit;

    public static uint EncodeResolved(uint virtualTableIndex)
    {
        if (virtualTableIndex > IndexMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualTableIndex));
        }
        return ResolvedBit | virtualTableIndex;
    }

    public static bool IsResolved(uint address) =>
        (address & TagMask) == ResolvedBit;

    public static uint Decode(uint address)
    {
        if (!IsVirtual(address))
            throw new ArgumentException(
                "The meshlet address is direct, not virtual.",
                nameof(address));
        return address & IndexMask;
    }

    public static uint DecodeResolved(uint address)
    {
        if (!IsResolved(address))
            throw new ArgumentException(
                "The meshlet address is not resolved.",
                nameof(address));
        return address & IndexMask;
    }
}

[Flags]
public enum MeshletGpuPageTableFlags : uint
{
    None = 0,
    Resident = 1u << 0,
    Pinned = 1u << 1
}

/// <summary>One frame-safe GPU page-table entry.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GPUMeshletPageTableEntry(
    uint BankIndex,
    uint PageIndexInBank,
    uint Generation,
    MeshletGpuPageTableFlags Flags)
{
    public const uint InvalidIndex = uint.MaxValue;

    public static GPUMeshletPageTableEntry Unmapped => new(
        InvalidIndex,
        InvalidIndex,
        0,
        MeshletGpuPageTableFlags.None);

    public bool IsResident =>
        (Flags & MeshletGpuPageTableFlags.Resident) != 0 &&
        BankIndex != InvalidIndex &&
        PageIndexInBank != InvalidIndex;
}

/// <summary>Immutable mapping from a virtual meshlet to a page-local record.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GPUMeshletVirtualMapping(
    uint GlobalPageId,
    uint PageLocalMeshletIndex,
    uint Flags,
    uint VertexOffset);

/// <summary>
/// Frame-local mapping produced after page-table publication. Addresses pack
/// a four-bit physical bank and a 24-bit word offset, matching the mesh shader
/// local-index address ABI. An invalid record is all ones.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GPUMeshletResolvedMapping(
    uint MeshletRecordAddress,
    uint VertexSectionAddress,
    uint TriangleSectionAddress,
    uint VertexOffset)
{
    public const uint InvalidAddress = uint.MaxValue;
    public const uint BankShift = 24;
    public const uint BankMask = 0x0fu;
    public const uint WordMask = 0x00ff_ffffu;

    public static GPUMeshletResolvedMapping Invalid => new(
        InvalidAddress,
        InvalidAddress,
        InvalidAddress,
        0u);

    public bool IsValid => MeshletRecordAddress != InvalidAddress;

    public static uint PackAddress(uint bankIndex, uint wordOffset)
    {
        if (bankIndex > BankMask)
            throw new ArgumentOutOfRangeException(nameof(bankIndex));
        if (wordOffset > WordMask)
            throw new ArgumentOutOfRangeException(nameof(wordOffset));
        return (bankIndex << (int)BankShift) | wordOffset;
    }
}

[Flags]
public enum MeshletStreamingRangeFlags : uint
{
    None = 0,
    PinnedFallback = 1u << 0,
    Hierarchy = 1u << 1
}

/// <summary>
/// Immutable whole-range selection contract. A flat LOD is selectable only
/// when every page in this range is resident in the current frame table.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GPUMeshletStreamingRange(
    uint FirstGlobalPageId,
    uint PageCount,
    uint FirstVirtualMeshlet,
    uint MeshletCount,
    MeshletStreamingRangeFlags Flags,
    uint FallbackRangeIndex,
    uint Reserved0,
    uint Reserved1);

/// <summary>Header stored at the beginning of every exact 64 KiB GPU page.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GPUMeshletPhysicalPageHeader(
    uint Magic,
    uint Version,
    uint MeshletCount,
    uint VertexIndexCount,
    uint TriangleIndexCount,
    uint MeshletWordOffset,
    uint VertexIndexWordOffset,
    uint TriangleIndexWordOffset)
{
    public const uint ExpectedMagic = 0x3147_504Du; // MPG1
    public const uint CurrentVersion = 1;
    public const int SizeInBytes = 32;
}

public sealed record MeshletGpuPagePackResult(
    byte[] PageBytes,
    int MeshletCount,
    int VertexIndexCount,
    int TriangleIndexCount,
    int UsedBytes);

/// <summary>
/// Converts the authenticated sidecar representation into the fixed GPU page
/// ABI. The output is always exactly 64 KiB and unused bytes are zeroed.
/// </summary>
public static class MeshletGpuPagePacker
{
    public const int PageSizeBytes =
        MeshletStreamingManifest.ProductionPageSizeBytes;

    public static MeshletGpuPagePackResult Pack(
        ReadOnlySpan<byte> decodedSidecarPage,
        uint globalVertexOffset)
    {
        MeshletStreamingPagePayload payload =
            MeshletStreamingPageCodec.Decode(decodedSidecarPage);
        return Pack(payload, globalVertexOffset);
    }

    public static MeshletGpuPagePackResult Pack(
        MeshletStreamingPagePayload payload,
        uint globalVertexOffset)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Meshlets is null ||
            payload.LocalVertexIndices is null ||
            payload.LocalTriangleIndices is null)
        {
            throw new ArgumentException(
                "Meshlet page arrays must be present.",
                nameof(payload));
        }

        int meshletOffset = GPUMeshletPhysicalPageHeader.SizeInBytes;
        int meshletBytes = checked(
            payload.Meshlets.Length * Marshal.SizeOf<GPUPackedMeshlet>());
        int vertexOffset = AlignUp(
            checked(meshletOffset + meshletBytes),
            sizeof(uint));
        int vertexBytes = checked(
            payload.LocalVertexIndices.Length * sizeof(uint));
        int triangleOffset = AlignUp(
            checked(vertexOffset + vertexBytes),
            sizeof(uint));
        int triangleBytes = checked(
            payload.LocalTriangleIndices.Length * sizeof(uint));
        int usedBytes = checked(triangleOffset + triangleBytes);
        if (usedBytes > PageSizeBytes)
        {
            throw new InvalidDataException(
                $"Packed meshlet page requires {usedBytes} bytes; the physical page ABI permits {PageSizeBytes}.");
        }

        var packedMeshlets = new GPUPackedMeshlet[
            payload.Meshlets.Length];
        for (int index = 0; index < packedMeshlets.Length; index++)
        {
            Meshlet meshlet = payload.Meshlets[index];
            meshlet.VertexOffset = checked(
                meshlet.VertexOffset + globalVertexOffset);
            // These offsets are page-local. Shader helpers add the selected
            // page section offsets after resolving the virtual mapping.
            packedMeshlets[index] = GPUPackedMeshlet.Pack(meshlet);
        }

        var result = new byte[PageSizeBytes];
        Span<byte> header = result.AsSpan(
            0,
            GPUMeshletPhysicalPageHeader.SizeInBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[0..4],
            GPUMeshletPhysicalPageHeader.ExpectedMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[4..8],
            GPUMeshletPhysicalPageHeader.CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[8..12],
            checked((uint)packedMeshlets.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[12..16],
            checked((uint)payload.LocalVertexIndices.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[16..20],
            checked((uint)payload.LocalTriangleIndices.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[20..24],
            checked((uint)(meshletOffset / sizeof(uint))));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[24..28],
            checked((uint)(vertexOffset / sizeof(uint))));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..32],
            checked((uint)(triangleOffset / sizeof(uint))));

        MemoryMarshal.AsBytes(packedMeshlets.AsSpan()).CopyTo(
            result.AsSpan(meshletOffset, meshletBytes));
        MemoryMarshal.AsBytes(
                payload.LocalVertexIndices.AsSpan())
            .CopyTo(result.AsSpan(vertexOffset, vertexBytes));
        MemoryMarshal.AsBytes(
                payload.LocalTriangleIndices.AsSpan())
            .CopyTo(result.AsSpan(triangleOffset, triangleBytes));

        return new MeshletGpuPagePackResult(
            result,
            packedMeshlets.Length,
            payload.LocalVertexIndices.Length,
            payload.LocalTriangleIndices.Length,
            usedBytes);
    }

    public static GPUMeshletPhysicalPageHeader ReadHeader(
        ReadOnlySpan<byte> page)
    {
        if (page.Length != PageSizeBytes)
        {
            throw new InvalidDataException(
                "A physical meshlet page must be exactly 64 KiB.");
        }
        var header = new GPUMeshletPhysicalPageHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(page[0..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[20..24]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[24..28]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[28..32]));
        if (header.Magic !=
                GPUMeshletPhysicalPageHeader.ExpectedMagic ||
            header.Version !=
                GPUMeshletPhysicalPageHeader.CurrentVersion)
        {
            throw new InvalidDataException(
                "The physical meshlet page header is invalid.");
        }
        return header;
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

public interface IMeshletPhysicalMemoryBudget
{
    bool TryCommit(long bytes, out string rejectionReason);

    void Release(long bytes);
}

public sealed class UnboundedMeshletPhysicalMemoryBudget :
    IMeshletPhysicalMemoryBudget
{
    public static UnboundedMeshletPhysicalMemoryBudget Instance { get; } =
        new();

    private UnboundedMeshletPhysicalMemoryBudget()
    {
    }

    public bool TryCommit(long bytes, out string rejectionReason)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        rejectionReason = string.Empty;
        return true;
    }

    public void Release(long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
    }
}

public sealed record MeshletPhysicalBankSnapshot(
    int ConfiguredPageCapacity,
    int CommittedBankCount,
    int CommittedPageCapacity,
    long CommittedBytes,
    string LastAllocationFailure);

/// <summary>
/// Lazy topology for sixteen stable 64 MiB bindless banks. It accounts and
/// reserves whole banks while physical slots remain globally numbered.
/// </summary>
public sealed class MeshletPhysicalBankAllocator : IDisposable
{
    public const int PageSizeBytes =
        MeshletStreamingManifest.ProductionPageSizeBytes;
    public const int BankSizeBytes = 64 * 1024 * 1024;
    public const int PagesPerBank = BankSizeBytes / PageSizeBytes;
    public const int MaximumBankCount = 16;
    public const int MaximumPageCapacity =
        MaximumBankCount * PagesPerBank;

    private readonly object _lock = new();
    private readonly IMeshletPhysicalMemoryBudget _budget;
    private int _committedBankCount;
    private string _lastAllocationFailure = string.Empty;
    private bool _disposed;

    public MeshletPhysicalBankAllocator(
        int configuredPageCapacity,
        IMeshletPhysicalMemoryBudget? budget = null)
    {
        if (configuredPageCapacity is <= 0 or > MaximumPageCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredPageCapacity));
        }
        ConfiguredPageCapacity = configuredPageCapacity;
        _budget = budget ??
            UnboundedMeshletPhysicalMemoryBudget.Instance;
    }

    public int ConfiguredPageCapacity { get; }

    public bool EnsureSlotAvailable(
        int physicalSlot,
        out string rejectionReason)
    {
        if ((uint)physicalSlot >= (uint)ConfiguredPageCapacity)
            throw new ArgumentOutOfRangeException(nameof(physicalSlot));
        int requiredBanks = physicalSlot / PagesPerBank + 1;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_committedBankCount < requiredBanks)
            {
                if (!_budget.TryCommit(
                        BankSizeBytes,
                        out rejectionReason))
                {
                    _lastAllocationFailure = string.IsNullOrWhiteSpace(
                        rejectionReason)
                        ? "meshlet-physical-bank-memory-budget-rejected"
                        : rejectionReason;
                    rejectionReason = _lastAllocationFailure;
                    return false;
                }
                _committedBankCount++;
            }
            rejectionReason = string.Empty;
            return true;
        }
    }

    public void ReleaseEmptyTrailingBanks(int highestLivePhysicalSlot)
    {
        int requiredBanks = highestLivePhysicalSlot < 0
            ? 0
            : highestLivePhysicalSlot / PagesPerBank + 1;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_committedBankCount > requiredBanks)
            {
                _committedBankCount--;
                _budget.Release(BankSizeBytes);
            }
        }
    }

    public static (uint BankIndex, uint PageIndexInBank) DecodeSlot(
        int physicalSlot)
    {
        if (physicalSlot < 0 || physicalSlot >= MaximumPageCapacity)
            throw new ArgumentOutOfRangeException(nameof(physicalSlot));
        return (
            checked((uint)(physicalSlot / PagesPerBank)),
            checked((uint)(physicalSlot % PagesPerBank)));
    }

    public MeshletPhysicalBankSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            return new MeshletPhysicalBankSnapshot(
                ConfiguredPageCapacity,
                _committedBankCount,
                Math.Min(
                    ConfiguredPageCapacity,
                    checked(_committedBankCount * PagesPerBank)),
                checked((long)_committedBankCount * BankSizeBytes),
                _lastAllocationFailure);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_committedBankCount != 0)
            {
                _budget.Release(
                    checked((long)_committedBankCount * BankSizeBytes));
                _committedBankCount = 0;
            }
        }
    }
}

public sealed record MeshletStreamingSubMeshActivation(
    int SubMeshIndex,
    bool Active,
    int PinnedPageCount,
    int LargestSelectableRangePageCount,
    long FullResidentMeshletBytes,
    long IncrementalMetadataBytes,
    long EstimatedBytesAvoided,
    string FallbackReason);

public sealed record MeshletStreamingActivationPlan(
    bool Configured,
    bool Active,
    int ActiveSubMeshCount,
    int PinnedPageCount,
    int LargestSelectableRangePageCount,
    long FullResidentMeshletBytes,
    long IncrementalCommittedBytes,
    long EstimatedBytesAvoided,
    IReadOnlyList<MeshletStreamingSubMeshActivation> SubMeshes,
    string FallbackReason);

/// <summary>
/// Pure adaptive preflight used by immediate and cooperative upload paths.
/// Sidecar authentication and pinned-page packing happen before this result is
/// published; this planner owns capacity and exact positive-savings policy.
/// </summary>
public static class MeshletStreamingActivationPlanner
{
    private const int PageTableEntryBytes = 16;
    private const int VirtualMappingBytes = 16;
    private const int RangeEntryBytes = 32;

    public static MeshletStreamingActivationPlan Evaluate(
        CookedMeshPayload mesh,
        bool streamingEnabled,
        int configuredPhysicalPageCount,
        int alreadyRegisteredPageCount = 0,
        int alreadyCommittedBankCount = 0,
        bool requireCompleteWorkingSet = true)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (configuredPhysicalPageCount is <= 0 or >
            MeshletPhysicalBankAllocator.MaximumPageCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredPhysicalPageCount));
        }
        if (alreadyRegisteredPageCount < 0 ||
            alreadyRegisteredPageCount > configuredPhysicalPageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alreadyRegisteredPageCount));
        }
        if (alreadyCommittedBankCount is < 0 or >
            MeshletPhysicalBankAllocator.MaximumBankCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alreadyCommittedBankCount));
        }

        MeshletStreamingManifest? manifest = mesh.StreamingManifest;
        if (!streamingEnabled)
        {
            return Empty(
                configured: false,
                "meshlet-physical-residency-disabled");
        }
        if (manifest is null)
        {
            return Empty(
                configured: true,
                "meshlet-streaming-manifest-missing");
        }
        try
        {
            manifest.Validate("adaptive-meshlet-residency");
        }
        catch (Exception ex) when (
            ex is CookedAssetFormatException or InvalidDataException or
                ArgumentException)
        {
            return Empty(
                configured: true,
                $"meshlet-streaming-manifest-invalid:{ex.Message}");
        }

        var candidates = new List<MeshletStreamingSubMeshActivation>();
        var candidatePageCounts = new Dictionary<int, int>();
        for (int subMeshIndex = 0;
             subMeshIndex < mesh.SubMeshes.Count;
             subMeshIndex++)
        {
            CookedSubMeshRecord subMesh = mesh.SubMeshes[subMeshIndex];
            MeshletStreamingPageRecord[] pages = manifest.Pages
                .Where(page => page.SubMeshIndex == subMeshIndex)
                .ToArray();
            candidatePageCounts.Add(subMeshIndex, pages.Length);
            bool skinned = subMesh.SkinIndex >= 0 ||
                subMesh.SkinningCount != 0;
            int pinned = pages.Count(page =>
                (page.Flags & MeshletStreamingPageFlags.Pinned) != 0);
            MeshletStreamingPageRecord[] streamable = pages.Where(page =>
                    (page.Flags &
                     MeshletStreamingPageFlags.Streamable) != 0)
                .ToArray();
            int largestRange = pages
                .GroupBy(page => page.Flags & (
                    MeshletStreamingPageFlags.Lod0 |
                    MeshletStreamingPageFlags.Lod1 |
                    MeshletStreamingPageFlags.Lod2 |
                    MeshletStreamingPageFlags.HierarchyGeometry))
                .Select(group => group.Count())
                .DefaultIfEmpty(0)
                .Max();
            int meshletCount = checked(
                subMesh.MeshletCount +
                subMesh.MeshletLod1Count +
                subMesh.MeshletLod2Count +
                subMesh.HierarchyMeshletCount);
            long fullBytes = checked(
                (long)meshletCount *
                    Marshal.SizeOf<GPUPackedMeshlet>() +
                (long)subMesh.MeshletVertexCount * sizeof(uint) +
                (long)subMesh.MeshletTriangleCount * sizeof(uint));
            long metadataBytes = checked(
                (long)pages.Length * PageTableEntryBytes * 2L +
                (long)meshletCount * VirtualMappingBytes +
                4L * RangeEntryBytes);
            string reason = string.Empty;
            bool active = true;
            if (skinned)
            {
                active = false;
                reason = "skinned-submesh-full-resident";
            }
            else if (streamable.Length == 0)
            {
                active = false;
                reason = "submesh-has-no-streamable-pages";
            }
            else if (pinned == 0)
            {
                active = false;
                reason = "submesh-has-no-pinned-fallback";
            }
            candidates.Add(new MeshletStreamingSubMeshActivation(
                subMeshIndex,
                active,
                pinned,
                largestRange,
                fullBytes,
                metadataBytes,
                active ? fullBytes - metadataBytes : 0,
                reason));
        }

        // Positive contributors are considered as one cohort so bank growth is
        // charged once. Re-evaluate after excluding non-contributors.
        List<MeshletStreamingSubMeshActivation> positive = candidates
            .Where(candidate => candidate.Active &&
                candidate.FullResidentMeshletBytes >
                    candidate.IncrementalMetadataBytes)
            .ToList();
        int pinnedPages = positive.Sum(static candidate =>
            candidate.PinnedPageCount);
        int largestSelectable = positive
            .Select(static candidate =>
                candidate.LargestSelectableRangePageCount)
            .DefaultIfEmpty(0)
            .Max();
        int selectedPageCount = positive.Sum(candidate =>
            candidatePageCounts[candidate.SubMeshIndex]);
        bool capacityExceeded = requireCompleteWorkingSet
            ? selectedPageCount >
              configuredPhysicalPageCount - alreadyRegisteredPageCount
            : alreadyRegisteredPageCount + pinnedPages + largestSelectable >
              configuredPhysicalPageCount;
        if (capacityExceeded)
        {
            positive.Clear();
            selectedPageCount = 0;
            largestSelectable = 0;
            candidates = candidates.Select(candidate =>
                candidate.Active
                    ? candidate with
                    {
                        Active = false,
                        EstimatedBytesAvoided = 0,
                        FallbackReason = requireCompleteWorkingSet
                            ? "complete-working-set-exceeds-cache"
                            : "pinned-plus-largest-range-exceeds-cache"
                    }
                    : candidate).ToList();
        }

        pinnedPages = positive.Sum(static candidate =>
            candidate.PinnedPageCount);
        long fullResidentBytes = positive.Sum(static candidate =>
            candidate.FullResidentMeshletBytes);
        long metadata = positive.Sum(static candidate =>
            candidate.IncrementalMetadataBytes);
        int requiredPages = checked(
            alreadyRegisteredPageCount +
            (requireCompleteWorkingSet
                ? selectedPageCount
                : pinnedPages));
        int requiredBanks = requiredPages == 0
            ? alreadyCommittedBankCount
            : Math.Max(
                alreadyCommittedBankCount,
                (requiredPages +
                 MeshletPhysicalBankAllocator.PagesPerBank - 1) /
                MeshletPhysicalBankAllocator.PagesPerBank);
        long newBankBytes = checked(
            (long)Math.Max(
                0,
                requiredBanks - alreadyCommittedBankCount) *
            MeshletPhysicalBankAllocator.BankSizeBytes);
        long incrementalBytes = checked(newBankBytes + metadata);
        long savedBytes = fullResidentBytes - incrementalBytes;
        if (positive.Count != 0 && savedBytes <= 0)
        {
            HashSet<int> activeIndices = positive
                .Select(static candidate => candidate.SubMeshIndex)
                .ToHashSet();
            candidates = candidates.Select(candidate =>
                activeIndices.Contains(candidate.SubMeshIndex)
                    ? candidate with
                    {
                        Active = false,
                        EstimatedBytesAvoided = 0,
                        FallbackReason =
                            "physical-residency-does-not-reduce-vram"
                    }
                    : candidate).ToList();
            positive.Clear();
            pinnedPages = 0;
            largestSelectable = 0;
            fullResidentBytes = 0;
            incrementalBytes = 0;
            savedBytes = 0;
        }

        HashSet<int> selected = positive
            .Select(static candidate => candidate.SubMeshIndex)
            .ToHashSet();
        candidates = candidates.Select(candidate =>
        {
            if (selected.Contains(candidate.SubMeshIndex))
                return candidate;
            if (candidate.Active)
            {
                return candidate with
                {
                    Active = false,
                    EstimatedBytesAvoided = 0,
                    FallbackReason =
                        "submesh-does-not-contribute-positive-savings"
                };
            }
            return candidate;
        }).ToList();

        bool activePlan = selected.Count != 0;
        string fallback = activePlan
            ? string.Empty
            : candidates.Select(static candidate =>
                    candidate.FallbackReason)
                .FirstOrDefault(static reason =>
                    !string.IsNullOrWhiteSpace(reason)) ??
                "no-eligible-static-streaming-submesh";
        return new MeshletStreamingActivationPlan(
            Configured: true,
            Active: activePlan,
            ActiveSubMeshCount: selected.Count,
            PinnedPageCount: pinnedPages,
            LargestSelectableRangePageCount: largestSelectable,
            FullResidentMeshletBytes: fullResidentBytes,
            IncrementalCommittedBytes: incrementalBytes,
            EstimatedBytesAvoided: Math.Max(0, savedBytes),
            SubMeshes: candidates,
            FallbackReason: fallback);
    }

    private static MeshletStreamingActivationPlan Empty(
        bool configured,
        string reason) =>
        new(
            configured,
            Active: false,
            ActiveSubMeshCount: 0,
            PinnedPageCount: 0,
            LargestSelectableRangePageCount: 0,
            FullResidentMeshletBytes: 0,
            IncrementalCommittedBytes: 0,
            EstimatedBytesAvoided: 0,
            SubMeshes: Array.Empty<MeshletStreamingSubMeshActivation>(),
            FallbackReason: reason);
}
