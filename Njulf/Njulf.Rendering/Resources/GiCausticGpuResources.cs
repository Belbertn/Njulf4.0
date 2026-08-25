using System;
using System.Numerics;
using Njulf.Rendering.Core;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Strict GPU allocation request layered over the CPU/reference C4 layout.
/// The reference layout accounts retained photon banks, cell tables, and sort
/// scratch; this layer additionally accounts the task queue, a separate
/// candidate staging bank, and publication headers.  Those bytes must never
/// be silently borrowed from a readable cache bank.
/// </summary>
public readonly record struct GiCausticGpuResourceLayoutRequest(
    GiCausticCacheLayout CacheLayout,
    ulong IndependentMemoryBudgetBytes,
    ulong MaximumStorageBufferRange = ulong.MaxValue,
    int MaximumEmitterCount = 64,
    int MaximumHeroCount = 64,
    int MaximumProposalPairCount = 4_096,
    GiCausticScreenResolveProfile ScreenResolveProfile = default);

/// <summary>
/// Exact bytes and bank topology for the C4 GPU workload.  Photon storage is
/// arranged as one transient candidate staging region followed by two
/// immutable-on-read cache photon banks.  The separate staging region is what
/// makes deterministic bottom-K compaction safe while a previous cache is
/// still readable.
/// </summary>
public readonly record struct GiCausticGpuResourceLayout(
    GiCausticCacheLayout SourceLayout,
    int TaskCapacity,
    int PhotonCapacity,
    int PhotonRecordStride,
    int PhotonBankCount,
    int CacheBankCount,
    int CellTableCapacity,
    int MaximumPhotonsPerCell,
    int EmitterCapacity,
    int HeroCapacity,
    int ProposalPairCapacity,
    ulong TaskRecordOffsetBytes,
    ulong EmitterRecordOffsetBytes,
    ulong HeroRecordOffsetBytes,
    ulong ProposalPairRecordOffsetBytes,
    ulong TaskQueueBytes,
    ulong CandidateStagingBytes,
    ulong PublishedPhotonBankBytes,
    ulong PublishedPhotonBytes,
    ulong CacheTableBytes,
    ulong CacheHistoryBytes,
    ulong PublicationHeaderBytes,
    ulong CacheBytes,
    ulong ScratchBytes,
    ulong TotalBytes,
    bool IsValid,
    string FailureReason)
{
    public GiCausticScreenResolveLayout ScreenResolve { get; init; } =
        GiCausticScreenResolveLayout.Empty("disabled");

    /// <summary>
    /// Host-visible frame constants and fence-owned publication headers. They
    /// are C4-owned even though they are not published through bindless slots.
    /// </summary>
    public ulong RuntimeMetadataBytes { get; init; }

    public static GiCausticGpuResourceLayout Empty(string reason) => new(
        GiCausticCacheLayout.Empty(reason),
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0UL, 0UL, 0UL, 0UL,
        0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL,
        false, reason)
    {
        ScreenResolve = GiCausticScreenResolveLayout.Empty(reason)
    };

    /// <summary>Exact bytes owned by the four bindless C4 buffers.</summary>
    public ulong BufferTotalBytes => checked(
        TaskQueueBytes + CandidateStagingBytes + PublishedPhotonBytes +
        CacheBytes + ScratchBytes);

    public ulong CacheHeaderBytesPerBank =>
        CacheBankCount == 0 ? 0UL : PublicationHeaderBytes / (ulong)CacheBankCount;

    public ulong CacheTableBytesPerBank =>
        CacheBankCount == 0 ? 0UL : CacheTableBytes / (ulong)CacheBankCount;

    public GiCausticGpuMemoryRequirements CreateMemoryRequirements(
        bool admitted,
        bool allocated,
        GiExperimentFallbackReason fallbackReason = GiExperimentFallbackReason.None) =>
        GiCausticGpuMemoryRequirements.FromLayout(
            this, admitted, allocated, fallbackReason);
}

/// <summary>
/// C4's contribution to the central independently-admitted memory schema.
/// The aggregate categories intentionally retain their existing public names;
/// the detailed properties identify which bytes are task, candidate, retained,
/// table, scratch, and header memory.
/// </summary>
public readonly record struct GiCausticGpuMemoryRequirements(
    SimpleDdgiAdvancedMemoryUsage PhotonRecords,
    SimpleDdgiAdvancedMemoryUsage CellTableAndSortScratch,
    SimpleDdgiAdvancedMemoryUsage History,
    ulong TaskQueueBytes,
    ulong CandidateStagingBytes,
    ulong PublishedPhotonBytes,
    ulong PublicationHeaderBytes)
{
    public static GiCausticGpuMemoryRequirements Empty { get; } = new(
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords),
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch),
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.CausticHistory),
        0UL, 0UL, 0UL, 0UL);

    public ulong RequiredBytes => checked(
        PhotonRecords.RequiredBytes +
        CellTableAndSortScratch.RequiredBytes +
        History.RequiredBytes);

    public ulong AllocatedBytes => checked(
        PhotonRecords.AllocatedBytes +
        CellTableAndSortScratch.AllocatedBytes +
        History.AllocatedBytes);

    public static GiCausticGpuMemoryRequirements FromLayout(
        in GiCausticGpuResourceLayout layout,
        bool admitted,
        bool allocated,
        GiExperimentFallbackReason fallbackReason)
    {
        if (!layout.IsValid || !admitted)
            return EmptyWithFallback(fallbackReason);

        ulong photonBytes = checked(
            layout.TaskQueueBytes + layout.CandidateStagingBytes +
            layout.PublishedPhotonBytes);
        // The published cell table is retained by the readable cache bank and
        // therefore belongs to persistent C4 history.  Only deterministic
        // sort/build scratch may enter the transient alias arena.
        ulong tableAndScratchBytes = layout.ScratchBytes;
        ulong historyBytes = checked(
            layout.CacheTableBytes +
            layout.CacheHistoryBytes +
            layout.PublicationHeaderBytes +
            layout.ScreenResolve.PersistentImageBytes +
            layout.RuntimeMetadataBytes);
        return new GiCausticGpuMemoryRequirements(
            CreateUsage(
                SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords,
                photonBytes, allocated),
            CreateUsage(
                SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
                tableAndScratchBytes, allocated),
            CreateUsage(
                SimpleDdgiAdvancedMemoryCategory.CausticHistory,
                historyBytes, allocated),
            layout.TaskQueueBytes,
            layout.CandidateStagingBytes,
            layout.PublishedPhotonBytes,
            layout.PublicationHeaderBytes);
    }

    private static GiCausticGpuMemoryRequirements EmptyWithFallback(
        GiExperimentFallbackReason fallbackReason) => new(
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords,
            fallbackReason),
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch,
            fallbackReason),
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.CausticHistory,
            fallbackReason),
        0UL, 0UL, 0UL, 0UL);

    private static SimpleDdgiAdvancedMemoryUsage CreateUsage(
        SimpleDdgiAdvancedMemoryCategory category,
        ulong requiredBytes,
        bool allocated) => new(
        category,
        requiredBytes,
        requiredBytes,
        requiredBytes,
        allocated ? requiredBytes : 0UL,
        allocated ? requiredBytes : 0UL,
        0UL,
        0UL,
        GiExperimentFallbackReason.None);
}

/// <summary>Compiles a no-hidden-bytes C4 allocation topology.</summary>
public static class GiCausticGpuResourceLayoutCompiler
{
    public const int RequiredPhotonBankCount = 2;
    public const int RequiredCacheBankCount = 2;

    public static GiCausticGpuResourceLayout Compile(
        in GiCausticGpuResourceLayoutRequest request)
    {
        GiCausticCacheLayout source = request.CacheLayout;
        if (!source.IsValid)
            return GiCausticGpuResourceLayout.Empty("caustic-reference-layout-invalid");
        if (request.IndependentMemoryBudgetBytes == 0UL)
            return GiCausticGpuResourceLayout.Empty("caustic-gpu-independent-budget-missing");
        if (request.MaximumStorageBufferRange == 0UL)
            return GiCausticGpuResourceLayout.Empty("caustic-gpu-storage-range-missing");
        if (source.WriteBankCount != RequiredPhotonBankCount)
        {
            return GiCausticGpuResourceLayout.Empty(
                "caustic-gpu-requires-two-readable-photon-banks");
        }
        if (source.CacheBankCount != RequiredCacheBankCount)
        {
            return GiCausticGpuResourceLayout.Empty(
                "caustic-gpu-requires-two-cache-table-banks");
        }
        if (source.PhotonRecordStride != GiCausticGpuAbi.PhotonRecordBytes)
        {
            return GiCausticGpuResourceLayout.Empty(
                "caustic-gpu-photon-record-abi-stride-mismatch");
        }
        if (source.PhotonTaskCapacity <= 0 || source.CellTableCapacity <= 0 ||
            source.MaximumPhotonsPerCell <= 0)
        {
            return GiCausticGpuResourceLayout.Empty(
                "caustic-gpu-capacity-is-empty");
        }
        if (request.MaximumEmitterCount is <= 0 or >
                GiCausticGpuTaskGenerationFlags.MaximumEmitterCount ||
            request.MaximumHeroCount is <= 0 or >
                GiCausticGpuTaskGenerationFlags.MaximumHeroCount ||
            request.MaximumProposalPairCount is <= 0 or >
                GiCausticGpuTaskGenerationFlags.MaximumProposalPairCount ||
            request.MaximumProposalPairCount >
                request.MaximumEmitterCount * request.MaximumHeroCount)
        {
            return GiCausticGpuResourceLayout.Empty(
                "caustic-gpu-task-generation-capacity-invalid");
        }
        if (!IsPowerOfTwo(source.CellTableCapacity))
        {
            return GiCausticGpuResourceLayout.Empty(
                "caustic-gpu-cell-table-must-be-power-of-two");
        }
        if (!GiCausticDeterministicBuildScratchLayout.TryCreate(
                source.PhotonTaskCapacity,
                out GiCausticDeterministicBuildScratchLayout scratchLayout) ||
            source.SortScratchBytes != scratchLayout.RequiredBytes)
        {
            return GiCausticGpuResourceLayout.Empty(
                "caustic-gpu-deterministic-build-scratch-layout-mismatch");
        }

        GiCausticScreenResolveLayout screenResolve =
            GiCausticScreenResolveLayoutCompiler.Compile(
                request.ScreenResolveProfile);
        if (!screenResolve.IsValid)
        {
            return GiCausticGpuResourceLayout.Empty(
                screenResolve.FailureReason);
        }

        try
        {
            ulong taskRecordOffsetBytes = GiCausticGpuAbi.TaskDispatchHeaderBytes;
            ulong emitterRecordOffsetBytes = checked(
                taskRecordOffsetBytes +
                (ulong)source.PhotonTaskCapacity * GiCausticGpuAbi.TaskRecordBytes);
            ulong heroRecordOffsetBytes = checked(
                emitterRecordOffsetBytes +
                (ulong)request.MaximumEmitterCount * GiCausticGpuAbi.EmitterRecordBytes);
            ulong proposalPairRecordOffsetBytes = checked(
                heroRecordOffsetBytes +
                (ulong)request.MaximumHeroCount * GiCausticGpuAbi.HeroRecordBytes);
            ulong taskQueueBytes = checked(
                proposalPairRecordOffsetBytes +
                (ulong)request.MaximumProposalPairCount *
                    GiCausticGpuAbi.ProposalPairRecordBytes);
            ulong candidateStagingBytes = checked(
                (ulong)source.PhotonTaskCapacity *
                (ulong)GiCausticGpuAbi.PhotonRecordBytes);
            ulong expectedPublishedPhotonBytes = checked(
                candidateStagingBytes * (ulong)RequiredPhotonBankCount);
            if (source.PhotonRecordBytes != expectedPublishedPhotonBytes)
            {
                return GiCausticGpuResourceLayout.Empty(
                    "caustic-gpu-photon-bank-layout-mismatch");
            }

            ulong expectedCellTableBytes = checked(
                (ulong)source.CellTableCapacity *
                (ulong)GiCausticGpuAbi.CellEntryBytes *
                (ulong)RequiredCacheBankCount);
            if (source.CellTableBytes != expectedCellTableBytes)
            {
                return GiCausticGpuResourceLayout.Empty(
                    "caustic-gpu-cell-table-layout-mismatch");
            }

            ulong publicationHeaderBytes = checked(
                (ulong)GiCausticGpuAbi.CacheHeaderBytes *
                (ulong)RequiredCacheBankCount);
            ulong cacheBytes = checked(
                source.CellTableBytes + source.HistoryBytes +
                publicationHeaderBytes);
            ulong scratchBytes = Math.Max(
                source.SortScratchBytes,
                screenResolve.TileScratchBytes);
            ulong runtimeMetadataBytes = checked(
                (ulong)RenderingConstants.FramesInFlight *
                ((ulong)GiCausticScreenGpuAbi.FrameConstantsBytes +
                 (ulong)GiCausticGpuAbi.CacheHeaderBytes));
            ulong totalBytes = checked(
                taskQueueBytes + candidateStagingBytes + source.PhotonRecordBytes +
                cacheBytes + scratchBytes + screenResolve.PersistentImageBytes +
                runtimeMetadataBytes);

            if (taskQueueBytes > request.MaximumStorageBufferRange ||
                candidateStagingBytes + source.PhotonRecordBytes >
                    request.MaximumStorageBufferRange ||
                cacheBytes > request.MaximumStorageBufferRange ||
                scratchBytes > request.MaximumStorageBufferRange)
            {
                return GiCausticGpuResourceLayout.Empty(
                    "caustic-gpu-storage-buffer-range-exceeded");
            }
            if (totalBytes > request.IndependentMemoryBudgetBytes)
            {
                return GiCausticGpuResourceLayout.Empty(
                    "caustic-gpu-independent-memory-budget-exceeded");
            }

            return new GiCausticGpuResourceLayout(
                source,
                source.PhotonTaskCapacity,
                source.PhotonTaskCapacity,
                source.PhotonRecordStride,
                RequiredPhotonBankCount,
                RequiredCacheBankCount,
                source.CellTableCapacity,
                source.MaximumPhotonsPerCell,
                request.MaximumEmitterCount,
                request.MaximumHeroCount,
                request.MaximumProposalPairCount,
                taskRecordOffsetBytes,
                emitterRecordOffsetBytes,
                heroRecordOffsetBytes,
                proposalPairRecordOffsetBytes,
                taskQueueBytes,
                candidateStagingBytes,
                source.PhotonRecordBytes / RequiredPhotonBankCount,
                source.PhotonRecordBytes,
                source.CellTableBytes,
                source.HistoryBytes,
                publicationHeaderBytes,
                cacheBytes,
                scratchBytes,
                totalBytes,
                true,
                "valid")
            {
                ScreenResolve = screenResolve,
                RuntimeMetadataBytes = runtimeMetadataBytes
            };
        }
        catch (OverflowException)
        {
            return GiCausticGpuResourceLayout.Empty("caustic-gpu-layout-overflow");
        }
    }

    private static bool IsPowerOfTwo(int value) =>
        value > 0 && (value & (value - 1)) == 0;
}

/// <summary>
/// Explicit capability boundary for C4.  The existing renderer exposes ray
/// query infrastructure, but C4 remains disabled until a tagged first-diffuse
/// transport adapter and a deterministic parallel cache builder have each
/// been integrated and qualified.
/// </summary>
public readonly record struct GiCausticGpuFeatureSupport(
    bool ComputeSupported,
    bool RayQuerySupported,
    bool CurrentPoseAccelerationStructuresAvailable,
    bool TaggedTransportBackendIntegrated,
    bool DeterministicParallelCacheBuildIntegrated,
    bool PublicationReadbackSupported,
    bool DedicatedBindlessSlotsAvailable,
    bool ScreenResolvePipelineIntegrated = false,
    bool ScreenResolveResourcesAvailable = false)
{
    public bool IsSupported => ComputeSupported && RayQuerySupported &&
        CurrentPoseAccelerationStructuresAvailable &&
        TaggedTransportBackendIntegrated &&
        DeterministicParallelCacheBuildIntegrated &&
        PublicationReadbackSupported && DedicatedBindlessSlotsAvailable &&
        ScreenResolvePipelineIntegrated && ScreenResolveResourcesAvailable;

    public string FailureReason => !ComputeSupported
        ? "caustic-compute-capability-unavailable"
        : !RayQuerySupported
            ? "caustic-ray-query-capability-unavailable"
            : !CurrentPoseAccelerationStructuresAvailable
                ? "caustic-current-pose-acceleration-structure-unavailable"
                : !TaggedTransportBackendIntegrated
                    ? "caustic-tagged-first-diffuse-transport-backend-unavailable"
                    : !DeterministicParallelCacheBuildIntegrated
                        ? "caustic-deterministic-parallel-cache-builder-unavailable"
                        : !PublicationReadbackSupported
                            ? "caustic-publication-readback-unavailable"
                        : !DedicatedBindlessSlotsAvailable
                            ? "caustic-dedicated-bindless-slots-unavailable"
                            : !ScreenResolvePipelineIntegrated
                                ? "caustic-screen-resolve-pipeline-unavailable"
                                : !ScreenResolveResourcesAvailable
                                    ? "caustic-screen-resolve-resources-unavailable"
                                    : "supported";

    public static GiCausticGpuFeatureSupport Unsupported { get; } = new(
        false, false, false, false, false, false, false, false, false);
}

/// <summary>Only an already-admitted effective mode is allowed to allocate C4 resources.</summary>
public readonly record struct GiCausticGpuRuntimeRequest(
    bool IsEffectivelyEnabled,
    GiCausticGpuResourceLayout Layout,
    GiCausticGpuFeatureSupport FeatureSupport);

/// <summary>Backend-native buffer handle used behind the testable C4 lifecycle boundary.</summary>
public readonly record struct GiCausticGpuBuffer(ulong Handle, ulong Bytes)
{
    public bool IsAllocated => Handle != 0UL && Bytes != 0UL;
}

/// <summary>All four C4 buffers must be complete before their descriptors are published.</summary>
public sealed record GiCausticGpuAllocation(
    ulong AllocationId,
    GiCausticGpuBuffer Tasks,
    GiCausticGpuBuffer Photons,
    GiCausticGpuBuffer Cache,
    GiCausticGpuBuffer Scratch,
    uint DescriptorCount)
{
    public ulong TotalBytes => checked(
        Tasks.Bytes + Photons.Bytes + Cache.Bytes + Scratch.Bytes);

    public void Validate(in GiCausticGpuResourceLayout layout)
    {
        if (!layout.IsValid || AllocationId == 0UL)
            throw new ArgumentException("C4 allocation requires a valid nonzero layout and allocation ID.");
        ValidateBuffer(Tasks, layout.TaskQueueBytes, nameof(Tasks));
        ValidateBuffer(Photons, checked(layout.CandidateStagingBytes + layout.PublishedPhotonBytes),
            nameof(Photons));
        ValidateBuffer(Cache, layout.CacheBytes, nameof(Cache));
        ValidateBuffer(Scratch, layout.ScratchBytes, nameof(Scratch));
        if (DescriptorCount != GiCausticGpuAbi.DescriptorCount)
        {
            throw new ArgumentException(
                $"C4 requires exactly {GiCausticGpuAbi.DescriptorCount} storage descriptors.",
                nameof(DescriptorCount));
        }
        if (Tasks.Handle == Photons.Handle || Tasks.Handle == Cache.Handle ||
            Tasks.Handle == Scratch.Handle || Photons.Handle == Cache.Handle ||
            Photons.Handle == Scratch.Handle || Cache.Handle == Scratch.Handle)
        {
            throw new ArgumentException("C4 buffer handles must be distinct.");
        }
    }

    private static void ValidateBuffer(
        in GiCausticGpuBuffer buffer,
        ulong expectedBytes,
        string parameterName)
    {
        if (!buffer.IsAllocated || buffer.Bytes != expectedBytes)
        {
            throw new ArgumentException(
                $"C4 buffer must have exactly {expectedBytes} bytes.", parameterName);
        }
    }
}

/// <summary>
/// Native allocation/retirement boundary.  A Vulkan implementation must clear
/// or replace descriptors before retiring a resource and defer native disposal
/// until every submission that can read it has completed.
/// </summary>
public interface IGiCausticGpuResourceAllocator
{
    GiCausticGpuAllocation Allocate(in GiCausticGpuResourceLayout layout);

    void Retire(GiCausticGpuAllocation allocation);
}

public enum GiCausticGpuResourceState : byte
{
    Disabled = 0,
    ReadyForBuild = 1,
    Building = 2,
    Readable = 3,
    Invalidated = 4,
    Faulted = 5
}

/// <summary>Stable names used by the graph/pass integration layer.</summary>
public static class GiCausticGpuPassNames
{
    public const string Task = "GiCausticTaskPass";
    public const string Trace = "GiCausticTracePass";
    public const string CacheBuild = "GiCausticCacheBuildPass";
    public const string Resolve = "GiCausticResolvePass";
    public const string Composite = "GiCausticCompositePass";

    public const string TaskShader = "gi_caustic_tasks.comp.spv";
    public const string TraceShader = "gi_caustic_trace.comp.spv";
    public const string CacheBuildShader = "gi_caustic_cache_build.comp.spv";
    public const string ResolveShader = "gi_caustic_resolve.comp.spv";
    public const string ScreenResetShader = "gi_caustic_screen_reset.comp.spv";
    public const string ScreenClassifyShader = "gi_caustic_screen_classify.comp.spv";
    public const string ScreenResolveShader = "gi_caustic_screen_resolve.comp.spv";
    public const string ScreenCompositeShader = "gi_caustic_screen_composite.comp.spv";
}

public readonly record struct GiCausticGpuRuntimeSnapshot(
    GiCausticGpuResourceState State,
    bool IsEffectivelyEnabled,
    ulong AllocationEpoch,
    ulong AllocatedBytes,
    uint DescriptorCount,
    int PhotonReadBankIndex,
    int PhotonWriteBankIndex,
    int CacheReadBankIndex,
    int CacheWriteBankIndex,
    uint ReadableGeneration,
    uint PendingGeneration,
    ulong PublicationFailureCount,
    ulong InvalidationCount,
    ulong AllocationFailureCount,
    GiCausticGpuMemoryRequirements MemoryRequirements,
    string Reason)
{
    public bool HasReadableCache =>
        (State is GiCausticGpuResourceState.Readable or
            GiCausticGpuResourceState.Building) &&
        PhotonReadBankIndex is 0 or 1 && CacheReadBankIndex is 0 or 1;
}

/// <summary>Immutable token that binds recorded GPU work to one allocation/revision/bank tuple.</summary>
public readonly record struct GiCausticGpuBuildToken(
    ulong AllocationEpoch,
    int PhotonReadBankIndex,
    int PhotonWriteBankIndex,
    int CacheReadBankIndex,
    int CacheWriteBankIndex,
    uint CacheGeneration,
    ulong RevisionFingerprint,
    GiCausticCacheRevision Revision,
    int TaskCount,
    Vector4 CellOriginAndSize)
{
    public bool IsDefault => AllocationEpoch == 0UL;
}

public readonly record struct GiCausticGpuBuildBeginResult(
    bool Started,
    GiCausticGpuBuildToken Token,
    string Reason);

public enum GiCausticGpuPublicationFailure : byte
{
    None = 0,
    NotEnabled = 1,
    NoBuildInFlight = 2,
    TokenMismatch = 3,
    GpuWorkIncomplete = 4,
    RevisionInvalid = 5,
    HeaderAbiMismatch = 6,
    HeaderGenerationMismatch = 7,
    HeaderRevisionMismatch = 8,
    HeaderLayoutMismatch = 9,
    HeaderBankMismatch = 10,
    HeaderNotComplete = 11,
    Overflow = 12,
    HeaderInvalidated = 13
}

public readonly record struct GiCausticGpuPublicationResult(
    bool Published,
    GiCausticGpuPublicationFailure Failure,
    string Reason)
{
    public static GiCausticGpuPublicationResult Success { get; } = new(
        true, GiCausticGpuPublicationFailure.None, "published");
}

/// <summary>
/// Transactional C4 lifetime and publication authority.  A caller records the
/// task/trace/build work, waits for its completion, reads back the cache header
/// for the exact pending token, and only then calls <see cref="CompleteBuild"/>.
/// No partial, overflowed, stale, or capability-incomplete cache is readable.
/// </summary>
public sealed class GiCausticGpuResourceManager : IDisposable
{
    private readonly object _sync = new();
    private IGiCausticGpuResourceAllocator? _allocator;
    private GiCausticGpuAllocation? _allocation;
    private GiCausticGpuResourceLayout _layout;
    private GiCausticGpuResourceState _state;
    private ulong _allocationEpoch;
    private uint _nextGeneration;
    private uint _readableGeneration;
    private int _photonReadBankIndex = -1;
    private int _photonWriteBankIndex;
    private int _cacheReadBankIndex = -1;
    private int _cacheWriteBankIndex;
    private GiCausticCacheRevision _publishedRevision;
    private GPUCausticCacheHeaderV1 _publishedHeader;
    private GiCausticGpuBuildToken? _pendingBuild;
    private string _reason = "disabled";
    private bool _disposed;

    public GiCausticGpuResourceManager()
    {
        GiCausticGpuAbi.VerifyManagedLayout();
    }

    public ulong PublicationFailureCount { get; private set; }

    public ulong InvalidationCount { get; private set; }

    public ulong AllocationFailureCount { get; private set; }

    public GiCausticGpuRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Returns the exact native-allocation identity and immutable byte layout
    /// owned by the current C4 epoch.  This deliberately exposes neither a
    /// writable bank nor a descriptor: Vulkan integration uses it only to
    /// locate the native buffers that correspond to a manager-owned token.
    /// </summary>
    public bool TryGetActiveAllocation(
        out GiCausticGpuAllocation allocation,
        out GiCausticGpuResourceLayout layout)
    {
        lock (_sync)
        {
            allocation = default!;
            layout = default;
            if (_disposed || _allocation is null || !_layout.IsValid)
                return false;

            allocation = _allocation;
            layout = _layout;
            return true;
        }
    }

    /// <summary>
    /// Reconciles only the effective mode.  A rejected request is a hard
    /// zero-resource transition, even if the persisted user preference still
    /// asks for C4.
    /// </summary>
    public GiCausticGpuRuntimeSnapshot Reconcile(
        in GiCausticGpuRuntimeRequest request,
        IGiCausticGpuResourceAllocator? allocator)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!request.IsEffectivelyEnabled)
            {
                DisableNoLock("caustic-effective-mode-disabled");
                return CreateSnapshotNoLock();
            }
            if (!request.FeatureSupport.IsSupported)
            {
                DisableNoLock(request.FeatureSupport.FailureReason);
                return CreateSnapshotNoLock();
            }
            if (!request.Layout.IsValid)
            {
                DisableNoLock("caustic-gpu-layout-invalid:" + request.Layout.FailureReason);
                return CreateSnapshotNoLock();
            }
            if (allocator is null)
            {
                DisableNoLock("caustic-gpu-resource-allocator-unavailable");
                return CreateSnapshotNoLock();
            }
            if (_allocation is not null && _layout.Equals(request.Layout))
                return CreateSnapshotNoLock();

            GiCausticGpuAllocation? replacement = null;
            try
            {
                replacement = allocator.Allocate(request.Layout) ??
                    throw new InvalidOperationException("C4 allocator returned null.");
                replacement.Validate(request.Layout);
            }
            catch (Exception exception)
            {
                AllocationFailureCount++;
                if (replacement is not null)
                    allocator.Retire(replacement);
                DisableNoLock("caustic-gpu-allocation-rejected:" + exception.GetType().Name);
                return CreateSnapshotNoLock();
            }

            try
            {
                RetireActiveNoLock();
            }
            catch
            {
                allocator.Retire(replacement);
                ClearNoLock("caustic-prior-allocation-retirement-failed");
                throw;
            }

            _allocator = allocator;
            _allocation = replacement;
            _layout = request.Layout;
            _allocationEpoch = NextNonZero(_allocationEpoch);
            _nextGeneration = 0u;
            _readableGeneration = 0u;
            _photonReadBankIndex = -1;
            _photonWriteBankIndex = 0;
            _cacheReadBankIndex = -1;
            _cacheWriteBankIndex = 0;
            _publishedRevision = default;
            _publishedHeader = default;
            _pendingBuild = null;
            _state = GiCausticGpuResourceState.ReadyForBuild;
            _reason = "caustic-gpu-allocated-awaiting-build";
            return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Starts a cache-bank transaction.  The caller must upload a bounded task
    /// list and use this exact token in all task/trace/build push constants.
    /// </summary>
    public GiCausticGpuBuildBeginResult BeginBuild(
        in GiCausticCacheRevision revision,
        int taskCount,
        Vector4 cellOriginAndSize)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null || _state is GiCausticGpuResourceState.Disabled or
                GiCausticGpuResourceState.Faulted)
            {
                return new(false, default, "caustic-gpu-not-effectively-enabled");
            }
            if (_pendingBuild.HasValue)
                return new(false, default, "caustic-gpu-build-already-in-flight");
            if (!revision.IsValid)
                return new(false, default, "caustic-cache-revision-invalid");
            if (taskCount <= 0 || taskCount > _layout.TaskCapacity)
                return new(false, default, "caustic-task-count-out-of-bounds");
            if (!IsValidCellSpace(cellOriginAndSize))
                return new(false, default, "caustic-cell-space-invalid");

            int photonWrite = _photonReadBankIndex == 0 ? 1 : 0;
            int cacheWrite = _cacheReadBankIndex == 0 ? 1 : 0;
            uint generation = NextNonZero(_nextGeneration);
            _nextGeneration = generation;
            var token = new GiCausticGpuBuildToken(
                _allocationEpoch,
                _photonReadBankIndex,
                photonWrite,
                _cacheReadBankIndex,
                cacheWrite,
                generation,
                GiCausticGpuAbi.ComputeRevisionFingerprint(revision),
                revision,
                taskCount,
                cellOriginAndSize);
            _pendingBuild = token;
            _photonWriteBankIndex = photonWrite;
            _cacheWriteBankIndex = cacheWrite;
            _state = GiCausticGpuResourceState.Building;
            _reason = "caustic-gpu-building-write-banks";
            return new(true, token, "started");
        }
    }

    /// <summary>
    /// Cancels a token whose upload or Vulkan command recording failed before
    /// submission.  A previous published bank remains readable; the aborted
    /// write bank is never promoted and the next build receives a new
    /// generation.  This is intentionally token-bound so a late caller
    /// cannot cancel a newer transaction.
    /// </summary>
    public bool AbortBuild(
        in GiCausticGpuBuildToken token,
        string reason = "caustic-gpu-build-recording-aborted")
    {
        lock (_sync)
        {
            if (_disposed || !_pendingBuild.HasValue ||
                !_pendingBuild.Value.Equals(token) ||
                token.AllocationEpoch != _allocationEpoch)
            {
                return false;
            }

            _pendingBuild = null;
            _state = _photonReadBankIndex is 0 or 1 &&
                _cacheReadBankIndex is 0 or 1
                ? GiCausticGpuResourceState.Readable
                : GiCausticGpuResourceState.ReadyForBuild;
            _reason = string.IsNullOrWhiteSpace(reason)
                ? "caustic-gpu-build-recording-aborted"
                : reason.Trim();
            PublicationFailureCount++;
            return true;
        }
    }

    /// <summary>
    /// Creates the exact common push constants for a recorded C4 dispatch.
    /// The native adapter supplies only the bounded scratch/resolve offsets;
    /// descriptor indices are fixed by the C4 ABI and cannot alias DDGI.
    /// </summary>
    public GPUCausticPushConstantsV1 CreatePushConstants(
        in GiCausticGpuBuildToken token,
        uint scratchWordCapacity,
        uint buildPhase,
        uint flags = 0u,
        uint resolveRequestWordOffset = 0u,
        uint resolveRequestCount = 0u)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null || !_pendingBuild.HasValue ||
                !_pendingBuild.Value.Equals(token))
            {
                throw new InvalidOperationException(
                    "C4 push constants require the current pending build token.");
            }
            if (scratchWordCapacity > _layout.ScratchBytes / sizeof(uint))
                throw new ArgumentOutOfRangeException(nameof(scratchWordCapacity));
            if (resolveRequestWordOffset > scratchWordCapacity ||
                resolveRequestCount >
                (scratchWordCapacity - resolveRequestWordOffset) /
                ((uint)((GiCausticGpuAbi.ResolveRequestBytes +
                         GiCausticGpuAbi.ResolveResultBytes) / sizeof(uint))))
            {
                throw new ArgumentOutOfRangeException(nameof(resolveRequestCount));
            }

            GiCausticGpuBindlessSlots slots = GiCausticGpuAbi.BindlessSlots;
            return new GPUCausticPushConstantsV1
            {
                AbiVersion = GiCausticGpuAbi.Version,
                TaskBufferIndex = checked((uint)slots.TaskBufferIndex),
                PhotonBufferIndex = checked((uint)slots.PhotonBufferIndex),
                CacheBufferIndex = checked((uint)slots.CacheBufferIndex),
                ScratchBufferIndex = checked((uint)slots.ScratchBufferIndex),
                TaskCount = checked((uint)token.TaskCount),
                PhotonCapacity = checked((uint)_layout.PhotonCapacity),
                PhotonRecordStrideWords = checked((uint)(_layout.PhotonRecordStride / sizeof(uint))),
                CellTableCapacity = checked((uint)_layout.CellTableCapacity),
                MaximumPhotonsPerCell = checked((uint)_layout.MaximumPhotonsPerCell),
                CacheGeneration = token.CacheGeneration,
                RevisionFingerprintLow = GiCausticGpuAbi.Low32(token.RevisionFingerprint),
                RevisionFingerprintHigh = GiCausticGpuAbi.High32(token.RevisionFingerprint),
                CandidateStagingWordOffset = 0u,
                CachePhotonBankBaseWord = checked((uint)(
                    _layout.CandidateStagingBytes / sizeof(uint))),
                PhotonReadBankIndex = checked((uint)Math.Max(token.PhotonReadBankIndex, 0)),
                PhotonWriteBankIndex = checked((uint)token.PhotonWriteBankIndex),
                CacheReadBankIndex = checked((uint)Math.Max(token.CacheReadBankIndex, 0)),
                CacheWriteBankIndex = checked((uint)token.CacheWriteBankIndex),
                CacheBankHeaderWordOffset = checked((uint)(
                    (_layout.CacheTableBytes + _layout.CacheHistoryBytes) /
                    sizeof(uint) +
                    (ulong)token.CacheWriteBankIndex *
                    GiCausticGpuAbi.CacheHeaderBytes / sizeof(uint))),
                CacheBankTableWordOffset = checked((uint)(
                    (ulong)token.CacheWriteBankIndex *
                    _layout.CacheTableBytesPerBank / sizeof(uint))),
                ScratchWordCapacity = scratchWordCapacity,
                Flags = flags,
                BuildPhase = buildPhase,
                ResolveRequestWordOffset = resolveRequestWordOffset,
                ResolveRequestCount = resolveRequestCount,
                TransportAbiVersion = token.Revision.TransportAbi,
                MaximumOccupiedCells = checked((uint)_layout.SourceLayout.MaximumOccupiedCells),
                CellOriginAndSize = token.CellOriginAndSize
            };
        }
    }

    /// <summary>
    /// Creates read-only cache resolve constants after publication. The method
    /// refuses a changed revision instead of allowing a resolve to sample a
    /// world-space cache whose placement belongs to older content.
    /// </summary>
    public GPUCausticPushConstantsV1 CreateResolvePushConstants(
        in GiCausticCacheRevision revision,
        uint scratchWordCapacity,
        uint resolveRequestWordOffset,
        uint resolveRequestCount,
        uint flags = 0u)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null ||
                _state is not (GiCausticGpuResourceState.Readable or
                    GiCausticGpuResourceState.Building) ||
                !_publishedRevision.Equals(revision))
            {
                throw new InvalidOperationException(
                    "C4 resolve requires a readable cache with the exact current revision.");
            }
            if (scratchWordCapacity > _layout.ScratchBytes / sizeof(uint))
                throw new ArgumentOutOfRangeException(nameof(scratchWordCapacity));
            if (resolveRequestWordOffset > scratchWordCapacity ||
                resolveRequestCount >
                (scratchWordCapacity - resolveRequestWordOffset) /
                ((uint)((GiCausticGpuAbi.ResolveRequestBytes +
                         GiCausticGpuAbi.ResolveResultBytes) / sizeof(uint))))
            {
                throw new ArgumentOutOfRangeException(nameof(resolveRequestCount));
            }

            GiCausticGpuBindlessSlots slots = GiCausticGpuAbi.BindlessSlots;
            ulong fingerprint = GiCausticGpuAbi.ComputeRevisionFingerprint(revision);
            return new GPUCausticPushConstantsV1
            {
                AbiVersion = GiCausticGpuAbi.Version,
                TaskBufferIndex = checked((uint)slots.TaskBufferIndex),
                PhotonBufferIndex = checked((uint)slots.PhotonBufferIndex),
                CacheBufferIndex = checked((uint)slots.CacheBufferIndex),
                ScratchBufferIndex = checked((uint)slots.ScratchBufferIndex),
                TaskCount = 0u,
                PhotonCapacity = checked((uint)_layout.PhotonCapacity),
                PhotonRecordStrideWords = checked((uint)(_layout.PhotonRecordStride / sizeof(uint))),
                CellTableCapacity = checked((uint)_layout.CellTableCapacity),
                MaximumPhotonsPerCell = checked((uint)_layout.MaximumPhotonsPerCell),
                CacheGeneration = _readableGeneration,
                RevisionFingerprintLow = GiCausticGpuAbi.Low32(fingerprint),
                RevisionFingerprintHigh = GiCausticGpuAbi.High32(fingerprint),
                CandidateStagingWordOffset = 0u,
                CachePhotonBankBaseWord = checked((uint)(
                    _layout.CandidateStagingBytes / sizeof(uint))),
                PhotonReadBankIndex = checked((uint)_photonReadBankIndex),
                PhotonWriteBankIndex = checked((uint)_photonReadBankIndex),
                CacheReadBankIndex = checked((uint)_cacheReadBankIndex),
                CacheWriteBankIndex = checked((uint)_cacheReadBankIndex),
                CacheBankHeaderWordOffset = checked((uint)(
                    (_layout.CacheTableBytes + _layout.CacheHistoryBytes) /
                    sizeof(uint) +
                    (ulong)_cacheReadBankIndex *
                    GiCausticGpuAbi.CacheHeaderBytes / sizeof(uint))),
                CacheBankTableWordOffset = checked((uint)(
                    (ulong)_cacheReadBankIndex *
                    _layout.CacheTableBytesPerBank / sizeof(uint))),
                ScratchWordCapacity = scratchWordCapacity,
                Flags = flags,
                BuildPhase = 0u,
                ResolveRequestWordOffset = resolveRequestWordOffset,
                ResolveRequestCount = resolveRequestCount,
                TransportAbiVersion = revision.TransportAbi,
                MaximumOccupiedCells = checked((uint)_layout.SourceLayout.MaximumOccupiedCells),
                CellOriginAndSize = _publishedHeader.CellOriginAndSize
            };
        }
    }

    /// <summary>
    /// Atomically promotes a verified completed cache.  Any rejected header
    /// preserves the prior readable bank and clears only the pending build.
    /// </summary>
    public GiCausticGpuPublicationResult CompleteBuild(
        in GiCausticGpuBuildToken token,
        bool gpuWorkCompleted,
        in GPUCausticCacheHeaderV1 header)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null || _state is GiCausticGpuResourceState.Disabled or
                GiCausticGpuResourceState.Faulted)
            {
                return RejectNoLock(GiCausticGpuPublicationFailure.NotEnabled,
                    "caustic-gpu-not-effectively-enabled");
            }
            if (!_pendingBuild.HasValue)
            {
                return RejectNoLock(GiCausticGpuPublicationFailure.NoBuildInFlight,
                    "caustic-gpu-no-build-in-flight");
            }
            if (!_pendingBuild.Value.Equals(token) ||
                token.AllocationEpoch != _allocationEpoch)
            {
                return RejectNoLock(GiCausticGpuPublicationFailure.TokenMismatch,
                    "caustic-gpu-build-token-mismatch", clearPending: false);
            }
            if (!gpuWorkCompleted)
            {
                return RejectNoLock(GiCausticGpuPublicationFailure.GpuWorkIncomplete,
                    "caustic-gpu-work-not-complete");
            }

            GiCausticGpuPublicationResult validation = ValidateHeaderNoLock(token, header);
            if (!validation.Published)
                return RejectNoLock(validation.Failure, validation.Reason);

            _photonReadBankIndex = token.PhotonWriteBankIndex;
            _photonWriteBankIndex = _photonReadBankIndex == 0 ? 1 : 0;
            _cacheReadBankIndex = token.CacheWriteBankIndex;
            _cacheWriteBankIndex = _cacheReadBankIndex == 0 ? 1 : 0;
            _readableGeneration = token.CacheGeneration;
            _publishedRevision = token.Revision;
            _publishedHeader = header;
            _pendingBuild = null;
            _state = GiCausticGpuResourceState.Readable;
            _reason = "published";
            return GiCausticGpuPublicationResult.Success;
        }
    }

    /// <summary>
    /// Suppresses stale placement immediately on any relevant revision change.
    /// Resource memory remains allocated for a later rebuild, but no resolve
    /// call can observe the invalidated bank.
    /// </summary>
    public void Invalidate(in GiCausticCacheRevision currentRevision, string reason)
    {
        lock (_sync)
        {
            if (_disposed || _allocation is null)
                return;

            bool changed = false;
            if (_state == GiCausticGpuResourceState.Readable &&
                !_publishedRevision.Equals(currentRevision))
            {
                _state = GiCausticGpuResourceState.Invalidated;
                _reason = string.IsNullOrWhiteSpace(reason)
                    ? "caustic-published-revision-invalidated"
                    : reason.Trim();
                changed = true;
            }
            if (_pendingBuild.HasValue &&
                !_pendingBuild.Value.Revision.Equals(currentRevision))
            {
                _pendingBuild = null;
                _state = _photonReadBankIndex is 0 or 1 &&
                    _cacheReadBankIndex is 0 or 1
                    ? GiCausticGpuResourceState.Invalidated
                    : GiCausticGpuResourceState.ReadyForBuild;
                _reason = string.IsNullOrWhiteSpace(reason)
                    ? "caustic-build-revision-invalidated"
                    : reason.Trim();
                changed = true;
            }
            if (changed)
                InvalidationCount++;
        }
    }

    /// <summary>Returns a bank only when its full CPU revision still matches.</summary>
    public bool TryGetReadable(
        in GiCausticCacheRevision revision,
        out int photonBankIndex,
        out int cacheBankIndex,
        out GPUCausticCacheHeaderV1 header)
    {
        lock (_sync)
        {
            photonBankIndex = -1;
            cacheBankIndex = -1;
            header = default;
            if (_state is not (GiCausticGpuResourceState.Readable or
                    GiCausticGpuResourceState.Building) ||
                !_publishedRevision.Equals(revision))
            {
                if (_state is GiCausticGpuResourceState.Readable or
                    GiCausticGpuResourceState.Building)
                {
                    _pendingBuild = null;
                    _state = GiCausticGpuResourceState.Invalidated;
                    _reason = "caustic-resolve-revision-mismatch";
                    InvalidationCount++;
                }
                return false;
            }

            photonBankIndex = _photonReadBankIndex;
            cacheBankIndex = _cacheReadBankIndex;
            header = _publishedHeader;
            return photonBankIndex is 0 or 1 && cacheBankIndex is 0 or 1;
        }
    }

    public void Disable(string reason = "disabled")
    {
        lock (_sync)
        {
            if (!_disposed)
                DisableNoLock(reason);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            DisableNoLock("disposed");
            _disposed = true;
        }
    }

    private GiCausticGpuPublicationResult ValidateHeaderNoLock(
        in GiCausticGpuBuildToken token,
        in GPUCausticCacheHeaderV1 header)
    {
        if (header.AbiVersion != GiCausticGpuAbi.Version ||
            header.TransportAbiVersion != token.Revision.TransportAbi)
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderAbiMismatch,
                "caustic-cache-header-abi-mismatch");
        }
        if (header.CacheGeneration != token.CacheGeneration)
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderGenerationMismatch,
                "caustic-cache-header-generation-mismatch");
        }
        if (header.RevisionFingerprint != token.RevisionFingerprint)
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderRevisionMismatch,
                "caustic-cache-header-revision-fingerprint-mismatch");
        }
        if (header.TaskCapacity != (uint)_layout.TaskCapacity ||
            header.PhotonCapacity != (uint)_layout.PhotonCapacity ||
            header.PhotonRecordStrideBytes != (uint)_layout.PhotonRecordStride ||
            header.CellTableCapacity != (uint)_layout.CellTableCapacity ||
            header.MaximumPhotonsPerCell != (uint)_layout.MaximumPhotonsPerCell ||
            !header.CellOriginAndSize.Equals(token.CellOriginAndSize))
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderLayoutMismatch,
                "caustic-cache-header-layout-mismatch");
        }
        if (header.CacheBankIndex != (uint)token.CacheWriteBankIndex ||
            header.PhotonBankIndex != (uint)token.PhotonWriteBankIndex)
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderBankMismatch,
                "caustic-cache-header-bank-mismatch");
        }
        if ((header.PublicationFlags & GiCausticGpuCachePublicationFlags.Invalidated) != 0 ||
            (header.PublicationFlags & GiCausticGpuCachePublicationFlags.Invalid) != 0)
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderInvalidated,
                "caustic-cache-header-invalidated");
        }
        if (header.IsOverflowed)
        {
            return new(false, GiCausticGpuPublicationFailure.Overflow,
                "caustic-cache-build-overflowed");
        }
        if ((header.PublicationFlags & GiCausticGpuCachePublicationFlags.Initialized) == 0 ||
            !header.IsBuildComplete ||
            (header.PublicationFlags &
                GiCausticGpuCachePublicationFlags.DeterministicBuildBackendUnavailable) != 0)
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderNotComplete,
                "caustic-cache-header-not-complete");
        }
        if (header.CandidateInputCount > (uint)token.TaskCount ||
            header.CandidateCount > (uint)_layout.PhotonCapacity ||
            header.RetainedPhotonCount > header.CandidateCount ||
            header.RetainedPhotonCount > (uint)_layout.PhotonCapacity ||
            header.OccupiedCellCount > (uint)_layout.SourceLayout.MaximumOccupiedCells)
        {
            return new(false, GiCausticGpuPublicationFailure.HeaderLayoutMismatch,
                "caustic-cache-header-count-out-of-bounds");
        }
        return GiCausticGpuPublicationResult.Success;
    }

    private GiCausticGpuPublicationResult RejectNoLock(
        GiCausticGpuPublicationFailure failure,
        string reason,
        bool clearPending = true)
    {
        PublicationFailureCount++;
        if (clearPending)
        {
            _pendingBuild = null;
            _state = _photonReadBankIndex is 0 or 1 &&
                _cacheReadBankIndex is 0 or 1
                ? GiCausticGpuResourceState.Readable
                : GiCausticGpuResourceState.ReadyForBuild;
            _reason = reason;
        }
        return new(false, failure, reason);
    }

    private void DisableNoLock(string reason)
    {
        try
        {
            RetireActiveNoLock();
        }
        finally
        {
            ClearNoLock(reason);
        }
    }

    private void RetireActiveNoLock()
    {
        if (_allocation is null)
            return;
        IGiCausticGpuResourceAllocator? allocator = _allocator;
        GiCausticGpuAllocation allocation = _allocation;
        _allocation = null;
        _allocator = null;
        allocator?.Retire(allocation);
    }

    private void ClearNoLock(string reason)
    {
        _layout = default;
        _state = GiCausticGpuResourceState.Disabled;
        _readableGeneration = 0u;
        _nextGeneration = 0u;
        _photonReadBankIndex = -1;
        _photonWriteBankIndex = 0;
        _cacheReadBankIndex = -1;
        _cacheWriteBankIndex = 0;
        _publishedRevision = default;
        _publishedHeader = default;
        _pendingBuild = null;
        _reason = string.IsNullOrWhiteSpace(reason) ? "disabled" : reason.Trim();
    }

    private GiCausticGpuRuntimeSnapshot CreateSnapshotNoLock()
    {
        bool enabled = _allocation is not null &&
            _state != GiCausticGpuResourceState.Disabled;
        return new GiCausticGpuRuntimeSnapshot(
            _state,
            enabled,
            _allocationEpoch,
            enabled ? _layout.TotalBytes : 0UL,
            enabled ? _allocation!.DescriptorCount : 0u,
            _photonReadBankIndex,
            _photonWriteBankIndex,
            _cacheReadBankIndex,
            _cacheWriteBankIndex,
            _readableGeneration,
            _pendingBuild?.CacheGeneration ?? 0u,
            PublicationFailureCount,
            InvalidationCount,
            AllocationFailureCount,
            enabled
                ? _layout.CreateMemoryRequirements(admitted: true, allocated: true)
                : GiCausticGpuMemoryRequirements.Empty,
            _reason);
    }

    private static bool IsValidCellSpace(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W) && value.W > 0.0f;

    private static uint NextNonZero(uint value)
    {
        uint next = unchecked(value + 1u);
        return next == 0u ? 1u : next;
    }

    private static ulong NextNonZero(ulong value)
    {
        ulong next = unchecked(value + 1UL);
        return next == 0UL ? 1UL : next;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GiCausticGpuResourceManager));
    }
}
