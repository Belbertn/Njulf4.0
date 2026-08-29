using Njulf.Assets.Cooked;

namespace Njulf.Rendering.Resources;

public readonly record struct MeshletStreamingPackageId(long Value)
{
    public bool IsValid => Value > 0;

    public override string ToString() => Value.ToString();
}

public readonly record struct MeshletGlobalPageKey(
    MeshletStreamingPackageId PackageId,
    int LocalPageId);

internal sealed record MeshletStreamingSubMeshGpuBinding(
    int SubMeshIndex,
    uint VirtualMeshletBase,
    uint Lod0MeshletCount,
    uint Lod1MeshletCount,
    uint Lod2MeshletCount,
    uint HierarchyMeshletCount,
    uint Lod0RangeIndex,
    uint Lod1RangeIndex,
    uint Lod2RangeIndex,
    uint HierarchyRangeIndex);

public readonly record struct MeshletGlobalPageResolution(
    MeshletStreamingPackageId PackageId,
    int RequestedLocalPageId,
    int ResolvedLocalPageId,
    int ResolvedGlobalPageId,
    int PhysicalSlot,
    bool UsesFallback)
{
    public bool IsResident =>
        ResolvedLocalPageId >= 0 &&
        ResolvedGlobalPageId >= 0 &&
        PhysicalSlot >= 0;

    public static MeshletGlobalPageResolution Unavailable(
        MeshletStreamingPackageId packageId,
        int requestedLocalPageId) =>
        new(
            packageId,
            requestedLocalPageId,
            -1,
            -1,
            -1,
            false);
}

public sealed record MeshletStreamingRangeResolution(
    MeshletStreamingPackageId PackageId,
    int SubMeshIndex,
    MeshletStreamingPageFlags RequestedGeometry,
    bool IsComplete,
    bool UsesFallback,
    IReadOnlyList<MeshletGlobalPageResolution> Pages)
{
    public static MeshletStreamingRangeResolution Unavailable(
        MeshletStreamingPackageId packageId,
        int subMeshIndex,
        MeshletStreamingPageFlags requestedGeometry) =>
        new(
            packageId,
            subMeshIndex,
            requestedGeometry,
            false,
            false,
            Array.Empty<MeshletGlobalPageResolution>());
}

public sealed record MeshletStreamingCoordinatorFailure(
    MeshletStreamingPackageId PackageId,
    int LocalPageId,
    int GlobalPageId,
    ulong Serial,
    int ConsecutiveFailureCount,
    string Detail);

public sealed record MeshletStreamingCoordinatorSnapshot(
    bool Configured,
    bool Available,
    bool Active,
    bool Degraded,
    int PackageCount,
    int ActiveSubMeshCount,
    int ReferencedPackageCount,
    int PageCount,
    int PhysicalPageCapacity,
    int FreePhysicalPageCount,
    int RetiredPhysicalPageCount,
    int PinnedPageCount,
    int PinnedResidentPageCount,
    int ResidentPageCount,
    int QueuedPageCount,
    int ReadingPageCount,
    int UploadingPageCount,
    int FailedPageCount,
    long RequestCount,
    long ResidentHitCount,
    long FallbackHitCount,
    long DroppedRequestCount,
    long AdmissionCount,
    long EvictionCount,
    long RetryCount,
    long FailureCount,
    long UploadedBytes,
    long RequestOverflowCount,
    MeshletPhysicalBankSnapshot Banks,
    IReadOnlyDictionary<string, int> FallbackReasons,
    MeshletStreamingCoordinatorFailure? LastFailure);

/// <summary>
/// Reference-counted package handle. Local page IDs never escape through the
/// uploader: the coordinator translates them into globally unique IDs.
/// </summary>
public sealed class MeshletStreamingPackageHandle : IDisposable
{
    private MeshletStreamingResidencyCoordinator? _owner;

    internal MeshletStreamingPackageHandle(
        MeshletStreamingResidencyCoordinator owner,
        MeshletStreamingPackageId packageId,
        string packageKey,
        int globalPageBase,
        int pageCount)
    {
        _owner = owner;
        PackageId = packageId;
        PackageKey = packageKey;
        GlobalPageBase = globalPageBase;
        PageCount = pageCount;
    }

    public MeshletStreamingPackageId PackageId { get; }

    public string PackageKey { get; }

    public int GlobalPageBase { get; }

    public int PageCount { get; }

    internal bool IsPinnedBootstrapComplete =>
        RequireOwner().IsPinnedBootstrapComplete(PackageId);

    public int GetGlobalPageId(int localPageId)
    {
        if ((uint)localPageId >= (uint)PageCount)
            throw new ArgumentOutOfRangeException(nameof(localPageId));
        return checked(GlobalPageBase + localPageId);
    }

    internal MeshletStreamingSubMeshGpuBinding GetSubMeshGpuBinding(
        int subMeshIndex) =>
        RequireOwner().GetSubMeshGpuBinding(PackageId, subMeshIndex);

    internal void FinalizeSubMeshVertexOffset(
        int subMeshIndex,
        uint vertexOffset) =>
        RequireOwner().FinalizeSubMeshVertexOffset(
            PackageId,
            subMeshIndex,
            vertexOffset);

    internal IReadOnlyList<GPUMeshletVirtualMapping>
        GetVirtualMappings() =>
        RequireOwner().GetVirtualMappings(PackageId);

    internal IReadOnlyList<GPUMeshletStreamingRange>
        GetStreamingRanges() =>
        RequireOwner().GetStreamingRanges(PackageId);

    public MeshletGlobalPageResolution RequestPage(
        int localPageId,
        int priority,
        ulong serial) =>
        RequireOwner().RequestPage(
            PackageId,
            localPageId,
            priority,
            serial);

    public MeshletStreamingRangeResolution RequestRange(
        int subMeshIndex,
        MeshletStreamingPageFlags geometry,
        int priority,
        ulong serial) =>
        RequireOwner().RequestRange(
            PackageId,
            subMeshIndex,
            geometry,
            priority,
            serial);

    public MeshletGlobalPageResolution ResolvePage(int localPageId) =>
        RequireOwner().ResolvePage(PackageId, localPageId);

    public MeshletStreamingRangeResolution ResolveRange(
        int subMeshIndex,
        MeshletStreamingPageFlags geometry) =>
        RequireOwner().ResolveRange(
            PackageId,
            subMeshIndex,
            geometry);

    public bool CanDitherBetweenRanges(
        int subMeshIndex,
        MeshletStreamingPageFlags sourceGeometry,
        MeshletStreamingPageFlags targetGeometry) =>
        RequireOwner().CanDitherBetweenRanges(
            PackageId,
            subMeshIndex,
            sourceGeometry,
            targetGeometry);

    public void Dispose()
    {
        MeshletStreamingResidencyCoordinator? owner =
            Interlocked.Exchange(ref _owner, null);
        owner?.ReleasePackage(PackageId);
    }

    private MeshletStreamingResidencyCoordinator RequireOwner() =>
        Volatile.Read(ref _owner) ??
        throw new ObjectDisposedException(
            nameof(MeshletStreamingPackageHandle));
}

/// <summary>
/// Renderer-wide software residency engine. All packages share one page-ID
/// namespace, one physical-slot pool, and one global priority/LRU policy.
/// </summary>
public sealed class MeshletStreamingResidencyCoordinator : IDisposable
{
    public const int PinnedPriority = int.MaxValue;
    public const int VisiblePriority = 1_000_000;
    public const int PrefetchPriority = 100_000;

    private const MeshletStreamingPageFlags GeometryMask =
        MeshletStreamingPageFlags.Lod0 |
        MeshletStreamingPageFlags.Lod1 |
        MeshletStreamingPageFlags.Lod2 |
        MeshletStreamingPageFlags.HierarchyGeometry;

    private readonly object _lock = new();
    private readonly IMeshletStreamingPageUploader _uploader;
    private readonly MeshletStreamingResidencyOptions _options;
    private readonly MeshletPhysicalBankAllocator _banks;
    private readonly bool _ownsBanks;
    private readonly Dictionary<string, PackageRuntime> _packagesByKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<long, PackageRuntime> _packagesById = [];
    private readonly Dictionary<int, PageRuntime> _pagesByGlobalId = [];
    private readonly Dictionary<uint, PackageRuntime>
        _packagesByGlobalRangeIndex = [];
    private readonly HashSet<uint> _cpuRangeRequestKeys = [];
    private readonly Stack<int> _freeSlots;
    private readonly List<RetiredSlot> _retiredSlots = [];
    private readonly Dictionary<string, int> _fallbackReasons =
        new(StringComparer.Ordinal);
    private long _nextPackageId;
    private int _nextGlobalPageId;
    private uint _nextVirtualMeshletIndex;
    private uint _nextStreamingRangeIndex;
    private int _tickActive;
    private ulong _lastSubmissionSerial;
    private ulong _lastCompletedSerial;
    private ulong _requestWindowSerial = ulong.MaxValue;
    private int _requestWindowCount;
    private ulong _cpuRangeRequestWindowSerial = ulong.MaxValue;
    private long _requestCount;
    private long _residentHitCount;
    private long _fallbackHitCount;
    private long _droppedRequestCount;
    private long _admissionCount;
    private long _evictionCount;
    private long _retryCount;
    private long _failureCount;
    private long _uploadedBytes;
    private long _requestOverflowCount;
    private MeshletStreamingCoordinatorFailure? _lastFailure;
    private bool _disposed;

    public MeshletStreamingResidencyCoordinator(
        IMeshletStreamingPageUploader uploader,
        MeshletStreamingResidencyOptions? options = null,
        IMeshletPhysicalMemoryBudget? memoryBudget = null)
    {
        _uploader = uploader ??
            throw new ArgumentNullException(nameof(uploader));
        _options = options ?? new MeshletStreamingResidencyOptions();
        _options.Validate();
        if (_options.PhysicalPageCapacity >
            MeshletPhysicalBankAllocator.MaximumPageCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The managed cache supports at most sixteen 64 MiB banks.");
        }
        if (uploader is IMeshletPhysicalBankProvider provider)
        {
            if (provider.Banks.ConfiguredPageCapacity !=
                _options.PhysicalPageCapacity)
            {
                throw new ArgumentException(
                    "The uploader and coordinator physical-page capacities must match.",
                    nameof(uploader));
            }
            _banks = provider.Banks;
            _ownsBanks = false;
        }
        else
        {
            _banks = new MeshletPhysicalBankAllocator(
                _options.PhysicalPageCapacity,
                memoryBudget);
            _ownsBanks = true;
        }
        _freeSlots = new Stack<int>(_options.PhysicalPageCapacity);
        for (int slot = _options.PhysicalPageCapacity - 1;
             slot >= 0;
             slot--)
        {
            _freeSlots.Push(slot);
        }
    }

    public MeshletStreamingResidencyOptions Options => _options;

    public bool TryAcquirePackage(
        string packageKey,
        out MeshletStreamingPackageHandle? handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageKey);
        packageKey = packageKey.Trim();
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_packagesByKey.TryGetValue(
                    packageKey,
                    out PackageRuntime? package) ||
                package.Unloading)
            {
                handle = null;
                return false;
            }
            package.ReferenceCount = checked(package.ReferenceCount + 1);
            handle = CreateHandle(package);
            return true;
        }
    }

    public bool TryRegisterPackage(
        string packageKey,
        IMeshletStreamingPageSource source,
        out MeshletStreamingPackageHandle? handle,
        out string fallbackReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageKey);
        ArgumentNullException.ThrowIfNull(source);
        packageKey = packageKey.Trim();
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (_packagesByKey.TryGetValue(
                    packageKey,
                    out PackageRuntime? existing))
            {
                existing.ReferenceCount = checked(
                    existing.ReferenceCount + 1);
                DisposeRedundantSource(source, existing.Source);
                handle = CreateHandle(existing);
                fallbackReason = string.Empty;
                return true;
            }
        }

        try
        {
            source.Manifest.Validate(packageKey);
        }
        catch (Exception ex) when (
            ex is CookedAssetFormatException or InvalidDataException or
                ArgumentException)
        {
            handle = null;
            fallbackReason =
                $"meshlet-streaming-manifest-invalid:{ex.Message}";
            RecordFallback(fallbackReason);
            return false;
        }

        int pinned = source.Manifest.PinnedPageCount;
        int largestRange = source.Manifest.Pages
            .Where(static page =>
                (page.Flags & MeshletStreamingPageFlags.Streamable) != 0)
            .GroupBy(static page => new
            {
                page.SubMeshIndex,
                Geometry = page.Flags & GeometryMask
            })
            .Select(static group => group.Count())
            .DefaultIfEmpty(0)
            .Max();
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (_packagesByKey.TryGetValue(
                    packageKey,
                    out PackageRuntime? racedExisting))
            {
                racedExisting.ReferenceCount = checked(
                    racedExisting.ReferenceCount + 1);
                DisposeRedundantSource(source, racedExisting.Source);
                handle = CreateHandle(racedExisting);
                fallbackReason = string.Empty;
                return true;
            }
            int globalPinned = _packagesById.Values
                .Where(static package => !package.Unloading)
                .Sum(static package => package.Manifest.PinnedPageCount);
            if (globalPinned + pinned + largestRange >
                _options.PhysicalPageCapacity)
            {
                handle = null;
                fallbackReason =
                    "pinned-plus-largest-range-exceeds-global-cache";
                RecordFallbackNoLock(fallbackReason);
                return false;
            }
            int highestBootstrapSlot = globalPinned + pinned - 1;
            if (highestBootstrapSlot >= 0 &&
                !_banks.EnsureSlotAvailable(
                    highestBootstrapSlot,
                    out fallbackReason))
            {
                handle = null;
                fallbackReason =
                    $"physical-bank-allocation-rejected:{fallbackReason}";
                RecordFallbackNoLock(fallbackReason);
                return false;
            }
            if (_nextGlobalPageId >
                int.MaxValue - source.Manifest.Pages.Count)
            {
                handle = null;
                fallbackReason = "global-page-id-space-exhausted";
                RecordFallbackNoLock(fallbackReason);
                return false;
            }

            PackageGpuContracts gpuContracts = BuildGpuContracts(
                source.Manifest,
                _nextGlobalPageId,
                _nextVirtualMeshletIndex,
                _nextStreamingRangeIndex);
            if (_uploader is IMeshletGpuContractSink contractSink)
            {
                try
                {
                    contractSink.RegisterPackageContracts(
                        gpuContracts.VirtualMappingBase,
                        gpuContracts.VirtualMappings,
                        gpuContracts.StreamingRangeBase,
                        gpuContracts.Ranges);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                        ArgumentException)
                {
                    handle = null;
                    fallbackReason =
                        $"meshlet-virtual-table-publication-rejected:{ex.Message}";
                    RecordFallbackNoLock(fallbackReason);
                    return false;
                }
            }
            _nextVirtualMeshletIndex = checked(
                _nextVirtualMeshletIndex +
                (uint)gpuContracts.VirtualMappings.Length);
            _nextStreamingRangeIndex = checked(
                _nextStreamingRangeIndex +
                (uint)gpuContracts.Ranges.Length);
            var package = new PackageRuntime(
                new MeshletStreamingPackageId(
                    checked(++_nextPackageId)),
                packageKey,
                source,
                _nextGlobalPageId,
                gpuContracts);
            _nextGlobalPageId = checked(
                _nextGlobalPageId + source.Manifest.Pages.Count);
            foreach (MeshletStreamingPageRecord record in
                     source.Manifest.Pages)
            {
                int globalPageId = checked(
                    package.GlobalPageBase + record.PageId);
                bool isPinned =
                    (record.Flags & MeshletStreamingPageFlags.Pinned) != 0;
                var page = new PageRuntime(
                    package,
                    record,
                    globalPageId)
                {
                    State = isPinned
                        ? MeshletPageResidencyState.Queued
                        : MeshletPageResidencyState.Unloaded,
                    Requested = isPinned,
                    Priority = isPinned ? PinnedPriority : 0
                };
                package.Pages[record.PageId] = page;
                _pagesByGlobalId.Add(globalPageId, page);
            }
            _packagesByKey.Add(packageKey, package);
            _packagesById.Add(package.Id.Value, package);
            for (uint localRangeIndex = 0;
                 localRangeIndex < (uint)gpuContracts.Ranges.Length;
                 localRangeIndex++)
            {
                _packagesByGlobalRangeIndex.Add(
                    checked(gpuContracts.StreamingRangeBase +
                        localRangeIndex),
                    package);
            }
            handle = CreateHandle(package);
            fallbackReason = string.Empty;
            return true;
        }
    }

    public MeshletGlobalPageResolution RequestPage(
        MeshletStreamingPackageId packageId,
        int localPageId,
        int priority,
        ulong serial)
    {
        if (priority < 0)
            throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            PageRuntime page = RequirePageNoLock(package, localPageId);
            return RequestPageNoLock(
                package,
                page,
                priority,
                serial,
                enforceRequestWindow: true);
        }
    }

    /// <summary>
    /// Expands one fence-complete GPU range-demand key into page requests.
    /// The GPU append buffer already enforces the per-frame key cap, so the
    /// expanded pages do not consume that cap a second time.
    /// </summary>
    public bool RequestGlobalRange(
        uint globalRangeIndex,
        int priority,
        ulong serial)
    {
        if (priority < 0)
            throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_packagesByGlobalRangeIndex.TryGetValue(
                    globalRangeIndex,
                    out PackageRuntime? package) ||
                package.Unloading)
            {
                Interlocked.Increment(ref _droppedRequestCount);
                return false;
            }

            int localRangeIndex = checked((int)(
                globalRangeIndex -
                package.GpuContracts.StreamingRangeBase));
            GPUMeshletStreamingRange range =
                package.GpuContracts.Ranges[localRangeIndex];
            int firstLocalPage = checked(
                (int)range.FirstGlobalPageId -
                package.GlobalPageBase);
            int pageCount = checked((int)range.PageCount);
            if (pageCount <= 0 || firstLocalPage < 0 ||
                firstLocalPage > package.Pages.Length - pageCount)
            {
                Interlocked.Increment(ref _droppedRequestCount);
                return false;
            }

            for (int offset = 0; offset < pageCount; offset++)
            {
                PageRuntime page = package.Pages[firstLocalPage + offset];
                _ = RequestPageNoLock(
                    package,
                    page,
                    priority,
                    serial,
                    enforceRequestWindow: false);
            }
            return true;
        }
    }

    /// <summary>
    /// Accepts CPU-produced whole-range demand keys. Keys are deduplicated and
    /// budgeted once per serial; their expanded pages do not consume the key
    /// cap again.
    /// </summary>
    internal int RequestCpuRanges(
        ReadOnlySpan<uint> globalRangeIndices,
        int priority)
    {
        if (priority < 0)
            throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            ulong serial = _lastSubmissionSerial;
            if (_cpuRangeRequestWindowSerial != serial)
            {
                _cpuRangeRequestWindowSerial = serial;
                _cpuRangeRequestKeys.Clear();
            }

            int accepted = 0;
            foreach (uint globalRangeIndex in globalRangeIndices)
            {
                if (!_packagesByGlobalRangeIndex.TryGetValue(
                        globalRangeIndex,
                        out PackageRuntime? package) ||
                    package.Unloading)
                {
                    Interlocked.Increment(ref _droppedRequestCount);
                    continue;
                }
                if (_cpuRangeRequestKeys.Contains(globalRangeIndex))
                    continue;
                if (_cpuRangeRequestKeys.Count >=
                    _options.MaximumRequestsPerSerial)
                {
                    Interlocked.Increment(ref _droppedRequestCount);
                    Interlocked.Increment(ref _requestOverflowCount);
                    continue;
                }

                int localRangeIndex = checked((int)(
                    globalRangeIndex -
                    package.GpuContracts.StreamingRangeBase));
                GPUMeshletStreamingRange range =
                    package.GpuContracts.Ranges[localRangeIndex];
                int firstLocalPage = checked(
                    (int)range.FirstGlobalPageId -
                    package.GlobalPageBase);
                int pageCount = checked((int)range.PageCount);
                if (pageCount <= 0 || firstLocalPage < 0 ||
                    firstLocalPage > package.Pages.Length - pageCount)
                {
                    Interlocked.Increment(ref _droppedRequestCount);
                    continue;
                }

                _cpuRangeRequestKeys.Add(globalRangeIndex);
                accepted++;
                for (int offset = 0; offset < pageCount; offset++)
                {
                    _ = RequestPageNoLock(
                        package,
                        package.Pages[firstLocalPage + offset],
                        priority,
                        serial,
                        enforceRequestWindow: false);
                }
            }
            return accepted;
        }
    }

    internal bool TryGetGlobalRangeContract(
        uint globalRangeIndex,
        out GPUMeshletStreamingRange range)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_packagesByGlobalRangeIndex.TryGetValue(
                    globalRangeIndex,
                    out PackageRuntime? package) ||
                package.Unloading)
            {
                range = default;
                return false;
            }
            int localRangeIndex = checked((int)(
                globalRangeIndex -
                package.GpuContracts.StreamingRangeBase));
            range = package.GpuContracts.Ranges[localRangeIndex];
            return true;
        }
    }

    public MeshletStreamingRangeResolution RequestRange(
        MeshletStreamingPackageId packageId,
        int subMeshIndex,
        MeshletStreamingPageFlags geometry,
        int priority,
        ulong serial)
    {
        ValidateGeometry(geometry);
        if (subMeshIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(subMeshIndex));
        PageRuntime[] pages;
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            pages = SelectRangeNoLock(package, subMeshIndex, geometry);
        }
        foreach (PageRuntime page in pages)
        {
            RequestPage(packageId, page.Record.PageId, priority, serial);
        }
        return ResolveRange(packageId, subMeshIndex, geometry);
    }

    public MeshletGlobalPageResolution ResolvePage(
        MeshletStreamingPackageId packageId,
        int localPageId)
    {
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            return ResolvePageNoLock(
                RequirePageNoLock(package, localPageId),
                countHit: false);
        }
    }

    public MeshletStreamingRangeResolution ResolveRange(
        MeshletStreamingPackageId packageId,
        int subMeshIndex,
        MeshletStreamingPageFlags geometry)
    {
        ValidateGeometry(geometry);
        if (subMeshIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(subMeshIndex));
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            PageRuntime[] requested = SelectRangeNoLock(
                package,
                subMeshIndex,
                geometry);
            if (requested.Length == 0)
            {
                return MeshletStreamingRangeResolution.Unavailable(
                    packageId,
                    subMeshIndex,
                    geometry);
            }
            if (requested.All(static page =>
                    page.State == MeshletPageResidencyState.Resident))
            {
                return new MeshletStreamingRangeResolution(
                    packageId,
                    subMeshIndex,
                    geometry,
                    IsComplete: true,
                    UsesFallback: false,
                    requested.Select(page =>
                            CreateResolution(page, page, false))
                        .ToArray());
            }

            MeshletStreamingPageRecord first = requested[0].Record;
            var fallback = new List<MeshletGlobalPageResolution>(
                first.FallbackPageCount);
            for (int offset = 0;
                 offset < first.FallbackPageCount;
                 offset++)
            {
                PageRuntime page = package.Pages[
                    first.FallbackPageId + offset];
                if (page.State != MeshletPageResidencyState.Resident)
                {
                    return MeshletStreamingRangeResolution.Unavailable(
                        packageId,
                        subMeshIndex,
                        geometry);
                }
                fallback.Add(CreateResolution(
                    requested[0],
                    page,
                    usesFallback: true));
            }
            return new MeshletStreamingRangeResolution(
                packageId,
                subMeshIndex,
                geometry,
                IsComplete: true,
                UsesFallback: true,
                fallback);
        }
    }

    public bool CanDitherBetweenRanges(
        MeshletStreamingPackageId packageId,
        int subMeshIndex,
        MeshletStreamingPageFlags sourceGeometry,
        MeshletStreamingPageFlags targetGeometry)
    {
        MeshletStreamingRangeResolution source = ResolveRange(
            packageId,
            subMeshIndex,
            sourceGeometry);
        if (!source.IsComplete || source.UsesFallback)
            return false;
        MeshletStreamingRangeResolution target = ResolveRange(
            packageId,
            subMeshIndex,
            targetGeometry);
        return target.IsComplete && !target.UsesFallback;
    }

    public MeshletPageResidencyState GetState(
        MeshletStreamingPackageId packageId,
        int localPageId)
    {
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            return RequirePageNoLock(package, localPageId).State;
        }
    }

    internal MeshletStreamingSubMeshGpuBinding GetSubMeshGpuBinding(
        MeshletStreamingPackageId packageId,
        int subMeshIndex)
    {
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            if (!package.GpuContracts.Bindings.TryGetValue(
                    subMeshIndex,
                    out MeshletStreamingSubMeshGpuBinding? binding))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(subMeshIndex));
            }
            return binding;
        }
    }

    internal IReadOnlyList<GPUMeshletVirtualMapping> GetVirtualMappings(
        MeshletStreamingPackageId packageId)
    {
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            return package.GpuContracts.VirtualMappings;
        }
    }

    internal IReadOnlyList<GPUMeshletStreamingRange> GetStreamingRanges(
        MeshletStreamingPackageId packageId)
    {
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            return package.GpuContracts.Ranges;
        }
    }

    internal void FinalizeSubMeshVertexOffset(
        MeshletStreamingPackageId packageId,
        int subMeshIndex,
        uint vertexOffset)
    {
        MeshletStreamingSubMeshGpuBinding binding;
        uint mappingCount;
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            if (!package.GpuContracts.Bindings.TryGetValue(
                    subMeshIndex,
                    out MeshletStreamingSubMeshGpuBinding? resolved))
            {
                throw new ArgumentOutOfRangeException(nameof(subMeshIndex));
            }
            binding = resolved;
            mappingCount = checked(
                binding.Lod0MeshletCount +
                binding.Lod1MeshletCount +
                binding.Lod2MeshletCount +
                binding.HierarchyMeshletCount);
            if (package.FinalizedVertexOffsets.TryGetValue(
                    subMeshIndex,
                    out uint existing))
            {
                if (existing != vertexOffset)
                {
                    throw new InvalidOperationException(
                        "A paged submesh vertex offset was finalized more than once with different storage.");
                }
                return;
            }
            package.FinalizedVertexOffsets.Add(subMeshIndex, vertexOffset);
            for (uint offset = 0; offset < mappingCount; offset++)
            {
                int localIndex = checked((int)(
                    binding.VirtualMeshletBase -
                    package.GpuContracts.VirtualMappingBase + offset));
                package.GpuContracts.VirtualMappings[localIndex] =
                    package.GpuContracts.VirtualMappings[localIndex] with
                    {
                        VertexOffset = vertexOffset
                    };
            }
        }

        if (_uploader is IMeshletGpuContractSink sink)
        {
            sink.FinalizeVirtualMappingVertexOffset(
                binding.VirtualMeshletBase,
                mappingCount,
                vertexOffset);
        }
    }

    internal bool IsPinnedBootstrapComplete(
        MeshletStreamingPackageId packageId)
    {
        int[] pinnedPageIds;
        int[] pinnedRangeIndices;
        ulong completedSerial;
        lock (_lock)
        {
            PackageRuntime package = RequirePackageNoLock(packageId);
            PageRuntime[] pinned = package.Pages
                .Where(static page => page.IsPinned)
                .ToArray();
            if (pinned.Length == 0 || pinned.Any(static page =>
                    page.State != MeshletPageResidencyState.Resident))
            {
                return false;
            }
            pinnedPageIds = pinned
                .Select(static page => page.GlobalPageId)
                .ToArray();
            pinnedRangeIndices = package.GpuContracts.Ranges
                .Select((range, index) => (Range: range, Index: index))
                .Where(static item =>
                    item.Range.PageCount != 0 &&
                    (item.Range.Flags &
                     MeshletStreamingRangeFlags.PinnedFallback) != 0)
                .Select(item => checked(
                    (int)package.GpuContracts.StreamingRangeBase +
                    item.Index))
                .ToArray();
            completedSerial = _lastCompletedSerial;
        }

        if (_uploader is not MeshletPhysicalPageCacheUploader physical)
            return true;
        if (!physical.AreImmutableContractsReady(completedSerial))
            return false;
        foreach (int pageId in pinnedPageIds)
        {
            if (!physical.HasMapping(pageId, 0) ||
                !physical.HasMapping(pageId, 1))
            {
                return false;
            }
        }
        foreach (int rangeIndex in pinnedRangeIndices)
        {
            if (!physical.IsRangeReady(rangeIndex, 0) ||
                !physical.IsRangeReady(rangeIndex, 1))
            {
                return false;
            }
        }
        return true;
    }

    public async ValueTask TickAsync(
        ulong submissionSerial,
        ulong completedSerial,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _tickActive, 1) != 0)
        {
            throw new InvalidOperationException(
                "Global meshlet residency ticks may not overlap.");
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                ThrowIfDisposedNoLock();
                _lastSubmissionSerial = submissionSerial;
                _lastCompletedSerial = Math.Max(
                    _lastCompletedSerial,
                    completedSerial);
            }
            ProcessCompletedUploads(completedSerial, submissionSerial);
            ReclaimRetiredSlots(completedSerial, submissionSerial);
            ExpireStaleDemand(submissionSerial);
            BeginRequiredEviction(submissionSerial);

            int remainingAdmissions = _options.MaximumAdmissionsPerTick;
            int remainingUploadBytes = _options.MaximumUploadBytesPerTick;
            while (remainingAdmissions > 0 && remainingUploadBytes > 0)
            {
                List<ReadReservation> reservations = ReserveAdmissions(
                    submissionSerial,
                    remainingAdmissions,
                    remainingUploadBytes);
                if (reservations.Count == 0)
                    break;
                int reservedBytes = reservations.Sum(reservation =>
                    reservation.Page.Record.UncompressedBytes);
                remainingAdmissions -= reservations.Count;
                remainingUploadBytes -= reservedBytes;

                ReadResult[] reads = await Task.WhenAll(
                        reservations.Select(reservation =>
                            ReadReservationAsync(
                                reservation,
                                cancellationToken)))
                    .ConfigureAwait(false);
                foreach (ReadResult read in reads)
                {
                    if (read.Error is OperationCanceledException &&
                        cancellationToken.IsCancellationRequested)
                    {
                        CancelAdmission(read.Reservation, submissionSerial);
                        continue;
                    }
                    if (read.Error is not null)
                    {
                        if (read.Reservation.Page.Package.Unloading &&
                            read.Error is OperationCanceledException)
                        {
                            CancelAdmission(
                                read.Reservation,
                                submissionSerial);
                        }
                        else
                        {
                            FailAdmission(
                                read.Reservation,
                                submissionSerial,
                                read.Error);
                        }
                        continue;
                    }

                    PageRuntime reservedPage = read.Reservation.Page;
                    try
                    {
                        using CancellationTokenSource linked =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken,
                                reservedPage.Package.Cancellation.Token);
                        MeshletPageUploadTicket ticket = await _uploader
                            .BeginUploadAsync(
                                reservedPage.GlobalPageId,
                                read.Reservation.PhysicalSlot,
                                read.DecodedPage!,
                                submissionSerial,
                                linked.Token)
                            .ConfigureAwait(false);
                        if (!ticket.IsValid ||
                            ticket.PageId != reservedPage.GlobalPageId ||
                            ticket.PhysicalSlot !=
                                read.Reservation.PhysicalSlot)
                        {
                            throw new InvalidOperationException(
                                "The global meshlet uploader returned a mismatched ticket.");
                        }
                        lock (_lock)
                        {
                            RequireReservationNoLock(
                                reservedPage,
                                read.Reservation);
                            reservedPage.Ticket = ticket;
                            reservedPage.State =
                                MeshletPageResidencyState.Uploading;
                            Interlocked.Increment(ref _admissionCount);
                            Interlocked.Add(
                                ref _uploadedBytes,
                                read.DecodedPage!.Length);
                        }
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested ||
                        reservedPage.Package.Unloading)
                    {
                        CancelAdmission(
                            read.Reservation,
                            submissionSerial);
                    }
                    catch (Exception ex) when (
                        ex is not StackOverflowException and
                        not OutOfMemoryException)
                    {
                        FailAdmission(
                            read.Reservation,
                            submissionSerial,
                            ex);
                    }
                }
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
            }
            FinalizeUnloadedPackages();
        }
        finally
        {
            Volatile.Write(ref _tickActive, 0);
        }
    }

    public MeshletStreamingCoordinatorSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            int pinned = 0;
            int pinnedResident = 0;
            int resident = 0;
            int queued = 0;
            int reading = 0;
            int uploading = 0;
            int failed = 0;
            foreach (PageRuntime page in _pagesByGlobalId.Values)
            {
                if (page.IsPinned)
                    pinned++;
                switch (page.State)
                {
                    case MeshletPageResidencyState.Resident:
                        resident++;
                        if (page.IsPinned)
                            pinnedResident++;
                        break;
                    case MeshletPageResidencyState.Queued:
                        queued++;
                        break;
                    case MeshletPageResidencyState.Reading:
                        reading++;
                        break;
                    case MeshletPageResidencyState.Uploading:
                        uploading++;
                        break;
                    case MeshletPageResidencyState.Failed:
                        failed++;
                        break;
                }
            }
            bool degraded = failed != 0 ||
                _fallbackReasons.Count != 0;
            return new MeshletStreamingCoordinatorSnapshot(
                Configured: true,
                Available: !_disposed,
                Active: _packagesById.Values.Any(static package =>
                    !package.Unloading),
                Degraded: degraded,
                PackageCount: _packagesById.Values.Count(static package =>
                    !package.Unloading),
                ActiveSubMeshCount: _packagesById.Values
                    .Where(static package => !package.Unloading)
                    .Sum(static package =>
                        package.GpuContracts.Bindings.Count),
                ReferencedPackageCount: _packagesById.Values
                    .Where(static package => !package.Unloading)
                    .Sum(static package => package.ReferenceCount),
                PageCount: _pagesByGlobalId.Count,
                PhysicalPageCapacity: _options.PhysicalPageCapacity,
                FreePhysicalPageCount: _freeSlots.Count,
                RetiredPhysicalPageCount: _retiredSlots.Count,
                PinnedPageCount: pinned,
                PinnedResidentPageCount: pinnedResident,
                ResidentPageCount: resident,
                QueuedPageCount: queued,
                ReadingPageCount: reading,
                UploadingPageCount: uploading,
                FailedPageCount: failed,
                RequestCount: Interlocked.Read(ref _requestCount),
                ResidentHitCount: Interlocked.Read(
                    ref _residentHitCount),
                FallbackHitCount: Interlocked.Read(
                    ref _fallbackHitCount),
                DroppedRequestCount: Interlocked.Read(
                    ref _droppedRequestCount),
                AdmissionCount: Interlocked.Read(ref _admissionCount),
                EvictionCount: Interlocked.Read(ref _evictionCount),
                RetryCount: Interlocked.Read(ref _retryCount),
                FailureCount: Interlocked.Read(ref _failureCount),
                UploadedBytes: Interlocked.Read(ref _uploadedBytes),
                RequestOverflowCount: Interlocked.Read(
                    ref _requestOverflowCount),
                Banks: _banks.CreateSnapshot(),
                FallbackReasons: new Dictionary<string, int>(
                    _fallbackReasons,
                    StringComparer.Ordinal),
                LastFailure: _lastFailure);
        }
    }

    internal void ReleasePackage(MeshletStreamingPackageId packageId)
    {
        List<(PageRuntime Page, ulong RetireAfter)> unpublish = [];
        lock (_lock)
        {
            if (_disposed ||
                !_packagesById.TryGetValue(
                    packageId.Value,
                    out PackageRuntime? package))
            {
                return;
            }
            if (--package.ReferenceCount > 0)
                return;
            package.Unloading = true;
            package.Cancellation.Cancel();
            _packagesByKey.Remove(package.Key);
            ulong retireAfter = SaturatingAdd(
                _lastSubmissionSerial,
                _options.FramesInFlight);
            foreach (PageRuntime page in package.Pages)
            {
                page.Requested = false;
                page.Priority = 0;
                if (page.State == MeshletPageResidencyState.Queued ||
                    page.State == MeshletPageResidencyState.Unloaded ||
                    page.State == MeshletPageResidencyState.Failed)
                {
                    page.State = MeshletPageResidencyState.Unloaded;
                }
                else if (page.State == MeshletPageResidencyState.Resident)
                {
                    page.State = MeshletPageResidencyState.Evicting;
                    unpublish.Add((page, retireAfter));
                }
            }
        }

        foreach ((PageRuntime page, ulong retireAfter) in unpublish)
        {
            try
            {
                _uploader.UnpublishResident(
                    page.GlobalPageId,
                    page.PhysicalSlot,
                    retireAfter);
                lock (_lock)
                {
                    _retiredSlots.Add(new RetiredSlot(
                        page,
                        page.PhysicalSlot,
                        retireAfter));
                }
                PublishRangeReadiness(page.Package);
            }
            catch (Exception ex) when (
                ex is not StackOverflowException and
                not OutOfMemoryException)
            {
                lock (_lock)
                {
                    page.State = MeshletPageResidencyState.Resident;
                    RecordFailureNoLock(
                        page,
                        _lastSubmissionSerial,
                        ex);
                }
            }
        }
        FinalizeUnloadedPackages();
    }

    private void ProcessCompletedUploads(
        ulong completedSerial,
        ulong submissionSerial)
    {
        List<PageRuntime> completed;
        lock (_lock)
        {
            completed = _pagesByGlobalId.Values.Where(page =>
                    page.State == MeshletPageResidencyState.Uploading &&
                    IsUploadCompleteNoLock(
                        page.Ticket,
                        completedSerial))
                .ToList();
        }
        foreach (PageRuntime page in completed)
        {
            try
            {
                _uploader.PublishResident(
                    page.GlobalPageId,
                    page.PhysicalSlot);
                bool unload;
                lock (_lock)
                {
                    if (page.State !=
                            MeshletPageResidencyState.Uploading ||
                        page.Ticket.PageId != page.GlobalPageId)
                    {
                        throw new InvalidOperationException(
                            "A global upload completion changed concurrently.");
                    }
                    page.State = MeshletPageResidencyState.Resident;
                    page.ConsecutiveFailureCount = 0;
                    page.RetryAfterSerial = 0;
                    unload = page.Package.Unloading;
                }
                PublishRangeReadiness(page.Package);
                if (unload)
                    BeginUnloadEviction(page, submissionSerial);
            }
            catch (Exception ex) when (
                ex is not StackOverflowException and
                not OutOfMemoryException)
            {
                FailPublishedUpload(page, submissionSerial, ex);
            }
        }
    }

    private void ReclaimRetiredSlots(
        ulong completedSerial,
        ulong submissionSerial)
    {
        lock (_lock)
        {
            for (int index = _retiredSlots.Count - 1;
                 index >= 0;
                 index--)
            {
                RetiredSlot retired = _retiredSlots[index];
                if (retired.RetireAfterSerial > completedSerial)
                    continue;
                PageRuntime page = retired.Page;
                if (page.State != MeshletPageResidencyState.Evicting ||
                    page.PhysicalSlot != retired.PhysicalSlot)
                {
                    throw new InvalidOperationException(
                        "A retired global meshlet slot lost its owner.");
                }
                page.PhysicalSlot = -1;
                page.Ticket = default;
                page.State = page.Package.Unloading
                    ? MeshletPageResidencyState.Unloaded
                    : IsDemandActiveNoLock(page, submissionSerial)
                        ? MeshletPageResidencyState.Queued
                        : MeshletPageResidencyState.Unloaded;
                _freeSlots.Push(retired.PhysicalSlot);
                _retiredSlots.RemoveAt(index);
            }
        }
        FinalizeUnloadedPackages();
    }

    private void ExpireStaleDemand(ulong submissionSerial)
    {
        lock (_lock)
        {
            foreach (PageRuntime page in _pagesByGlobalId.Values)
            {
                if (page.Package.Unloading || page.IsPinned)
                    continue;
                if (page.Requested && IsAgeAtLeast(
                        submissionSerial,
                        page.LastRequestedSerial,
                        _options.DemandLifetimeSerials + 1))
                {
                    page.Requested = false;
                    page.Priority = 0;
                    if (page.State == MeshletPageResidencyState.Queued)
                        page.State = MeshletPageResidencyState.Unloaded;
                }
                if (page.State == MeshletPageResidencyState.Failed &&
                    submissionSerial >= page.RetryAfterSerial &&
                    IsDemandActiveNoLock(page, submissionSerial))
                {
                    page.State = MeshletPageResidencyState.Queued;
                    Interlocked.Increment(ref _retryCount);
                }
            }
        }
    }

    private void BeginRequiredEviction(ulong submissionSerial)
    {
        PageRuntime? victim;
        lock (_lock)
        {
            if (_freeSlots.Count != 0 ||
                !_pagesByGlobalId.Values.Any(page =>
                    page.State == MeshletPageResidencyState.Queued &&
                    IsDemandActiveNoLock(page, submissionSerial)))
            {
                return;
            }
            PageRuntime? queued = _pagesByGlobalId.Values
                .Where(page =>
                    !page.Package.Unloading &&
                    page.State == MeshletPageResidencyState.Queued &&
                    IsDemandActiveNoLock(page, submissionSerial) &&
                    submissionSerial >= page.RetryAfterSerial)
                .OrderByDescending(static page => page.Priority)
                .ThenByDescending(static page =>
                    page.LastRequestedSerial)
                .ThenBy(static page => page.GlobalPageId)
                .FirstOrDefault();
            if (queued is null)
                return;
            victim = _pagesByGlobalId.Values
                .Where(page =>
                    !page.Package.Unloading &&
                    !page.IsPinned &&
                    page.State == MeshletPageResidencyState.Resident &&
                    (page.Priority < queued.Priority ||
                     page.Priority == queued.Priority &&
                     page.LastRequestedSerial <
                        queued.LastRequestedSerial) &&
                    IsAgeAtLeast(
                        submissionSerial,
                        page.LastRequestedSerial,
                        _options.EvictionGraceSerials))
                .OrderBy(static page => page.LastRequestedSerial)
                .ThenBy(static page => page.Priority)
                .ThenBy(static page => page.GlobalPageId)
                .FirstOrDefault();
            if (victim is null)
                return;
            victim.State = MeshletPageResidencyState.Evicting;
        }

        ulong retireAfter = SaturatingAdd(
            submissionSerial,
            _options.FramesInFlight);
        try
        {
            _uploader.UnpublishResident(
                victim.GlobalPageId,
                victim.PhysicalSlot,
                retireAfter);
            lock (_lock)
            {
                _retiredSlots.Add(new RetiredSlot(
                    victim,
                    victim.PhysicalSlot,
                    retireAfter));
                Interlocked.Increment(ref _evictionCount);
            }
            PublishRangeReadiness(victim.Package);
        }
        catch
        {
            lock (_lock)
                victim.State = MeshletPageResidencyState.Resident;
            throw;
        }
    }

    private List<ReadReservation> ReserveAdmissions(
        ulong submissionSerial,
        int maximumAdmissions,
        int maximumUploadBytes)
    {
        lock (_lock)
        {
            IEnumerable<PageRuntime> candidates = _pagesByGlobalId.Values
                .Where(page =>
                    !page.Package.Unloading &&
                    page.State == MeshletPageResidencyState.Queued &&
                    IsDemandActiveNoLock(page, submissionSerial) &&
                    submissionSerial >= page.RetryAfterSerial)
                .OrderByDescending(static page => page.IsPinned)
                .ThenByDescending(static page => page.Priority)
                .ThenByDescending(static page =>
                    page.LastRequestedSerial)
                .ThenBy(static page => page.GlobalPageId);
            var reservations = new List<ReadReservation>(Math.Min(
                maximumAdmissions,
                _options.MaximumConcurrentReads));
            int uploadBytes = 0;
            foreach (PageRuntime page in candidates)
            {
                if (reservations.Count >= maximumAdmissions ||
                    reservations.Count >=
                        _options.MaximumConcurrentReads ||
                    _freeSlots.Count == 0)
                {
                    break;
                }
                int pageBytes = page.Record.UncompressedBytes;
                if (uploadBytes > maximumUploadBytes - pageBytes)
                    break;
                int slot = _freeSlots.Pop();
                if (!_banks.EnsureSlotAvailable(
                        slot,
                        out string failure))
                {
                    _freeSlots.Push(slot);
                    RecordFallbackNoLock(
                        $"physical-bank-allocation-rejected:{failure}");
                    break;
                }
                page.State = MeshletPageResidencyState.Reading;
                page.PhysicalSlot = slot;
                reservations.Add(new ReadReservation(page, slot));
                uploadBytes = checked(uploadBytes + pageBytes);
            }
            return reservations;
        }
    }

    private static async Task<ReadResult> ReadReservationAsync(
        ReadReservation reservation,
        CancellationToken externalCancellation)
    {
        PageRuntime page = reservation.Page;
        try
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    externalCancellation,
                    page.Package.Cancellation.Token);
            byte[] decoded = await page.Package.Source.ReadPageAsync(
                    page.Record.PageId,
                    linked.Token)
                .ConfigureAwait(false);
            if (decoded.Length != page.Record.UncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Global page {page.GlobalPageId} returned {decoded.Length} bytes; the manifest authenticated {page.Record.UncompressedBytes}.");
            }
            MeshletStreamingPagePayload payload =
                MeshletStreamingPageCodec.Decode(decoded);
            if (payload.Meshlets.Length != page.Record.MeshletCount)
            {
                throw new InvalidDataException(
                    $"Global page {page.GlobalPageId} contains an unexpected meshlet count.");
            }
            return new ReadResult(reservation, decoded, null);
        }
        catch (Exception ex) when (
            ex is not StackOverflowException and
            not OutOfMemoryException)
        {
            return new ReadResult(reservation, null, ex);
        }
    }

    private void CancelAdmission(
        ReadReservation reservation,
        ulong serial)
    {
        lock (_lock)
        {
            PageRuntime page = reservation.Page;
            if (page.State != MeshletPageResidencyState.Reading ||
                page.PhysicalSlot != reservation.PhysicalSlot)
            {
                return;
            }
            page.PhysicalSlot = -1;
            page.Ticket = default;
            page.State = page.Package.Unloading
                ? MeshletPageResidencyState.Unloaded
                : IsDemandActiveNoLock(page, serial)
                    ? MeshletPageResidencyState.Queued
                    : MeshletPageResidencyState.Unloaded;
            _freeSlots.Push(reservation.PhysicalSlot);
        }
    }

    private void FailAdmission(
        ReadReservation reservation,
        ulong serial,
        Exception error)
    {
        lock (_lock)
        {
            PageRuntime page = reservation.Page;
            RequireReservationNoLock(page, reservation);
            page.PhysicalSlot = -1;
            page.Ticket = default;
            _freeSlots.Push(reservation.PhysicalSlot);
            RecordFailureNoLock(page, serial, error);
        }
    }

    private void FailPublishedUpload(
        PageRuntime page,
        ulong serial,
        Exception error)
    {
        lock (_lock)
        {
            int physicalSlot = page.PhysicalSlot;
            page.PhysicalSlot = -1;
            page.Ticket = default;
            _freeSlots.Push(physicalSlot);
            RecordFailureNoLock(page, serial, error);
        }
    }

    private void RecordFailureNoLock(
        PageRuntime page,
        ulong serial,
        Exception error)
    {
        page.ConsecutiveFailureCount = checked(
            page.ConsecutiveFailureCount + 1);
        ulong exponent = checked((ulong)Math.Min(
            page.ConsecutiveFailureCount - 1,
            20));
        ulong multiplier = 1UL << (int)exponent;
        ulong delay = _options.RetryBaseSerials >
            _options.RetryMaximumSerials / multiplier
            ? _options.RetryMaximumSerials
            : Math.Min(
                _options.RetryMaximumSerials,
                _options.RetryBaseSerials * multiplier);
        page.RetryAfterSerial = SaturatingAdd(serial, delay);
        page.State = page.Package.Unloading
            ? MeshletPageResidencyState.Unloaded
            : MeshletPageResidencyState.Failed;
        Interlocked.Increment(ref _failureCount);
        _lastFailure = new MeshletStreamingCoordinatorFailure(
            page.Package.Id,
            page.Record.PageId,
            page.GlobalPageId,
            serial,
            page.ConsecutiveFailureCount,
            $"{error.GetType().Name}: {error.Message}");
    }

    private MeshletGlobalPageResolution RequestPageNoLock(
        PackageRuntime package,
        PageRuntime page,
        int priority,
        ulong serial,
        bool enforceRequestWindow)
    {
        if (!ReferenceEquals(page.Package, package))
        {
            throw new InvalidOperationException(
                "A meshlet page request crossed package ownership.");
        }

        Interlocked.Increment(ref _requestCount);
        bool pinned = page.IsPinned;
        if (enforceRequestWindow && !pinned &&
            page.LastObservedRequestSerial != serial)
        {
            if (_requestWindowSerial != serial)
            {
                _requestWindowSerial = serial;
                _requestWindowCount = 0;
            }
            if (_requestWindowCount >=
                _options.MaximumRequestsPerSerial)
            {
                Interlocked.Increment(ref _droppedRequestCount);
                Interlocked.Increment(ref _requestOverflowCount);
                return ResolvePageNoLock(page, countHit: true);
            }
            _requestWindowCount++;
            page.LastObservedRequestSerial = serial;
        }

        page.Requested = true;
        page.Priority = pinned
            ? PinnedPriority
            : Math.Max(page.Priority, priority);
        page.LastRequestedSerial = serial;
        if (page.State == MeshletPageResidencyState.Unloaded ||
            page.State == MeshletPageResidencyState.Failed &&
            serial >= page.RetryAfterSerial)
        {
            if (page.State == MeshletPageResidencyState.Failed)
                Interlocked.Increment(ref _retryCount);
            page.State = MeshletPageResidencyState.Queued;
        }
        return ResolvePageNoLock(page, countHit: true);
    }

    private MeshletGlobalPageResolution ResolvePageNoLock(
        PageRuntime requested,
        bool countHit)
    {
        if (requested.State == MeshletPageResidencyState.Resident)
        {
            if (countHit)
                Interlocked.Increment(ref _residentHitCount);
            return CreateResolution(requested, requested, false);
        }
        MeshletStreamingPageRecord record = requested.Record;
        bool complete = true;
        for (int offset = 0;
             offset < record.FallbackPageCount;
             offset++)
        {
            if (requested.Package.Pages[
                    record.FallbackPageId + offset].State !=
                MeshletPageResidencyState.Resident)
            {
                complete = false;
                break;
            }
        }
        PageRuntime fallback =
            requested.Package.Pages[record.FallbackPageId];
        if (complete &&
            fallback.State == MeshletPageResidencyState.Resident)
        {
            if (countHit)
                Interlocked.Increment(ref _fallbackHitCount);
            return CreateResolution(
                requested,
                fallback,
                fallback != requested);
        }
        return MeshletGlobalPageResolution.Unavailable(
            requested.Package.Id,
            requested.Record.PageId);
    }

    private static MeshletGlobalPageResolution CreateResolution(
        PageRuntime requested,
        PageRuntime resolved,
        bool usesFallback) =>
        new(
            requested.Package.Id,
            requested.Record.PageId,
            resolved.Record.PageId,
            resolved.GlobalPageId,
            resolved.PhysicalSlot,
            usesFallback);

    private void BeginUnloadEviction(
        PageRuntime page,
        ulong submissionSerial)
    {
        ulong retireAfter = SaturatingAdd(
            submissionSerial,
            _options.FramesInFlight);
        lock (_lock)
        {
            if (page.State != MeshletPageResidencyState.Resident)
                return;
            page.State = MeshletPageResidencyState.Evicting;
        }
        try
        {
            _uploader.UnpublishResident(
                page.GlobalPageId,
                page.PhysicalSlot,
                retireAfter);
            lock (_lock)
            {
                _retiredSlots.Add(new RetiredSlot(
                    page,
                    page.PhysicalSlot,
                    retireAfter));
            }
            PublishRangeReadiness(page.Package);
        }
        catch
        {
            lock (_lock)
                page.State = MeshletPageResidencyState.Resident;
            throw;
        }
    }

    private void FinalizeUnloadedPackages()
    {
        List<PackageRuntime> completed;
        int highestSlot;
        lock (_lock)
        {
            completed = _packagesById.Values.Where(package =>
                    package.Unloading && package.Pages.All(page =>
                        page.State is
                            MeshletPageResidencyState.Unloaded or
                            MeshletPageResidencyState.Failed))
                .ToList();
            foreach (PackageRuntime package in completed)
            {
                _packagesById.Remove(package.Id.Value);
                for (uint localRangeIndex = 0;
                     localRangeIndex <
                         (uint)package.GpuContracts.Ranges.Length;
                     localRangeIndex++)
                {
                    _packagesByGlobalRangeIndex.Remove(checked(
                        package.GpuContracts.StreamingRangeBase +
                        localRangeIndex));
                }
                foreach (PageRuntime page in package.Pages)
                    _pagesByGlobalId.Remove(page.GlobalPageId);
            }
            highestSlot = _pagesByGlobalId.Values
                .Where(static page => page.PhysicalSlot >= 0)
                .Select(static page => page.PhysicalSlot)
                .DefaultIfEmpty(-1)
                .Max();
        }
        foreach (PackageRuntime package in completed)
        {
            package.Cancellation.Dispose();
            if (package.Source is IDisposable disposable)
                disposable.Dispose();
        }
        _banks.ReleaseEmptyTrailingBanks(highestSlot);
    }

    private static PageRuntime[] SelectRangeNoLock(
        PackageRuntime package,
        int subMeshIndex,
        MeshletStreamingPageFlags geometry) =>
        package.Pages.Where(page =>
                page.Record.SubMeshIndex == subMeshIndex &&
                (page.Record.Flags & GeometryMask) == geometry)
            .OrderBy(static page => page.Record.PageId)
            .ToArray();

    private static PackageGpuContracts BuildGpuContracts(
        MeshletStreamingManifest manifest,
        int globalPageBase,
        uint virtualBase,
        uint rangeBase)
    {
        var mappings = new List<GPUMeshletVirtualMapping>();
        var ranges = new List<GPUMeshletStreamingRange>();
        var bindings = new Dictionary<
            int,
            MeshletStreamingSubMeshGpuBinding>();
        foreach (IGrouping<int, MeshletStreamingPageRecord> subMeshGroup in
                 manifest.Pages.GroupBy(static page => page.SubMeshIndex)
                     .OrderBy(static group => group.Key))
        {
            MeshletStreamingPageRecord[] pages = subMeshGroup
                .OrderBy(static page => page.PageId)
                .ToArray();
            uint subMeshVirtualBase = checked(
                virtualBase + (uint)mappings.Count);
            uint subMeshRangeBase = checked(
                rangeBase + (uint)ranges.Count);
            foreach (MeshletStreamingPageRecord page in pages)
            {
                MeshletStreamingPageFlags geometry =
                    page.Flags & GeometryMask;
                uint geometryOffset = geometry switch
                {
                    MeshletStreamingPageFlags.Lod0 => 0u,
                    MeshletStreamingPageFlags.Lod1 => 1u,
                    MeshletStreamingPageFlags.Lod2 => 2u,
                    MeshletStreamingPageFlags.HierarchyGeometry => 3u,
                    _ => throw new InvalidDataException(
                        "A streaming page has no unique geometry range.")
                };
                uint globalRangeIndex = checked(
                    subMeshRangeBase + geometryOffset);
                if (globalRangeIndex > 0x00ff_ffffu)
                {
                    throw new InvalidOperationException(
                        "The packed meshlet streaming range-key space is exhausted.");
                }
                uint packedMappingFlags =
                    ((uint)page.Flags & 0xffu) |
                    (globalRangeIndex << 8);
                for (int localMeshlet = 0;
                     localMeshlet < page.MeshletCount;
                     localMeshlet++)
                {
                    mappings.Add(new GPUMeshletVirtualMapping(
                        checked((uint)(globalPageBase + page.PageId)),
                        checked((uint)localMeshlet),
                        packedMappingFlags,
                        0));
                }
            }

            // Every managed submesh owns four consecutive range records in a
            // stable LOD0/L1/L2/hierarchy order. GPUMeshInfo therefore needs
            // only the LOD0 base index while shaders can select base + LOD.
            MeshletStreamingPageFlags[] geometryOrder =
            [
                MeshletStreamingPageFlags.Lod0,
                MeshletStreamingPageFlags.Lod1,
                MeshletStreamingPageFlags.Lod2,
                MeshletStreamingPageFlags.HierarchyGeometry
            ];
            var rangeIndices = new Dictionary<
                MeshletStreamingPageFlags,
                uint>();
            var rangeCounts = new Dictionary<
                MeshletStreamingPageFlags,
                uint>();
            int firstSubMeshRange = ranges.Count;
            foreach (MeshletStreamingPageFlags geometry in geometryOrder)
            {
                MeshletStreamingPageRecord[] rangePages = pages
                    .Where(page =>
                        (page.Flags & GeometryMask) == geometry)
                    .OrderBy(static page => page.PageId)
                    .ToArray();
                uint rangeIndex = checked(
                    rangeBase + (uint)ranges.Count);
                rangeIndices[geometry] = rangeIndex;
                uint meshletCount = checked((uint)rangePages.Sum(
                    static page => page.MeshletCount));
                rangeCounts[geometry] = meshletCount;
                MeshletStreamingRangeFlags flags =
                    rangePages.Length != 0 &&
                    rangePages.All(static page =>
                        (page.Flags & MeshletStreamingPageFlags.Pinned) != 0)
                        ? MeshletStreamingRangeFlags.PinnedFallback
                        : MeshletStreamingRangeFlags.None;
                if (geometry ==
                    MeshletStreamingPageFlags.HierarchyGeometry)
                {
                    flags |= MeshletStreamingRangeFlags.Hierarchy;
                }
                ranges.Add(rangePages.Length == 0
                    ? new GPUMeshletStreamingRange(
                        0,
                        0,
                        subMeshVirtualBase,
                        0,
                        flags,
                        uint.MaxValue,
                        0,
                        0)
                    : new GPUMeshletStreamingRange(
                        checked((uint)(globalPageBase +
                            rangePages[0].PageId)),
                        checked((uint)rangePages.Length),
                        checked(subMeshVirtualBase +
                            (uint)rangePages[0].LogicalFirstMeshlet),
                        meshletCount,
                        flags,
                        uint.MaxValue,
                        0,
                        0));
            }

            // Resolve fallback range indices after all four immutable records
            // exist. Empty authored ranges retain an invalid fallback index.
            for (int geometryIndex = 0;
                 geometryIndex < geometryOrder.Length;
                 geometryIndex++)
            {
                int localRangeIndex = firstSubMeshRange + geometryIndex;
                GPUMeshletStreamingRange range = ranges[localRangeIndex];
                if (range.PageCount == 0)
                    continue;
                int localFirstPage = checked(
                    (int)range.FirstGlobalPageId - globalPageBase);
                MeshletStreamingPageRecord? sourcePage = pages
                    .FirstOrDefault(page => page.PageId == localFirstPage);
                if (sourcePage is null)
                    continue;
                MeshletStreamingPageFlags fallbackGeometry = manifest.Pages[
                    sourcePage.FallbackPageId].Flags & GeometryMask;
                if (!rangeIndices.TryGetValue(
                        fallbackGeometry,
                        out uint fallbackRangeIndex))
                {
                    continue;
                }
                ranges[localRangeIndex] = range with
                {
                    FallbackRangeIndex = fallbackRangeIndex
                };
            }

            bindings.Add(
                subMeshGroup.Key,
                new MeshletStreamingSubMeshGpuBinding(
                    subMeshGroup.Key,
                    subMeshVirtualBase,
                    rangeCounts.GetValueOrDefault(
                        MeshletStreamingPageFlags.Lod0),
                    rangeCounts.GetValueOrDefault(
                        MeshletStreamingPageFlags.Lod1),
                    rangeCounts.GetValueOrDefault(
                        MeshletStreamingPageFlags.Lod2),
                    rangeCounts.GetValueOrDefault(
                        MeshletStreamingPageFlags.HierarchyGeometry),
                    rangeIndices.GetValueOrDefault(
                        MeshletStreamingPageFlags.Lod0,
                        uint.MaxValue),
                    rangeIndices.GetValueOrDefault(
                        MeshletStreamingPageFlags.Lod1,
                        uint.MaxValue),
                    rangeIndices.GetValueOrDefault(
                        MeshletStreamingPageFlags.Lod2,
                        uint.MaxValue),
                    rangeIndices.GetValueOrDefault(
                        MeshletStreamingPageFlags.HierarchyGeometry,
                        uint.MaxValue)));
        }
        if ((ulong)virtualBase + (uint)mappings.Count >
            (ulong)MeshletVirtualAddress.IndexMask + 1UL)
        {
            throw new InvalidOperationException(
                "The virtual meshlet address space is exhausted.");
        }
        return new PackageGpuContracts(
            virtualBase,
            mappings.ToArray(),
            rangeBase,
            ranges.ToArray(),
            bindings);
    }

    private static void ValidateGeometry(
        MeshletStreamingPageFlags geometry)
    {
        geometry &= GeometryMask;
        uint value = (uint)geometry;
        if (value == 0 || (value & (value - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(geometry),
                "Exactly one meshlet LOD/hierarchy range must be selected.");
        }
    }

    private PackageRuntime RequirePackageNoLock(
        MeshletStreamingPackageId id)
    {
        ThrowIfDisposedNoLock();
        if (!id.IsValid ||
            !_packagesById.TryGetValue(id.Value, out PackageRuntime? package) ||
            package.Unloading)
        {
            throw new ObjectDisposedException(
                nameof(MeshletStreamingPackageHandle));
        }
        return package;
    }

    private static PageRuntime RequirePageNoLock(
        PackageRuntime package,
        int localPageId)
    {
        if ((uint)localPageId >= (uint)package.Pages.Length)
            throw new ArgumentOutOfRangeException(nameof(localPageId));
        return package.Pages[localPageId];
    }

    private static void RequireReservationNoLock(
        PageRuntime page,
        ReadReservation reservation)
    {
        if (page.State != MeshletPageResidencyState.Reading ||
            page.PhysicalSlot != reservation.PhysicalSlot)
        {
            throw new InvalidOperationException(
                "A global meshlet admission reservation changed concurrently.");
        }
    }

    private bool IsDemandActiveNoLock(
        PageRuntime page,
        ulong submissionSerial) =>
        !page.Package.Unloading &&
        (page.IsPinned ||
         page.Requested && !IsAgeAtLeast(
             submissionSerial,
             page.LastRequestedSerial,
             _options.DemandLifetimeSerials + 1));

    private MeshletStreamingPackageHandle CreateHandle(
        PackageRuntime package) =>
        new(
            this,
            package.Id,
            package.Key,
            package.GlobalPageBase,
            package.Pages.Length);

    private void RecordFallback(string reason)
    {
        lock (_lock)
            RecordFallbackNoLock(reason);
    }

    private void RecordFallbackNoLock(string reason)
    {
        _fallbackReasons[reason] = checked(
            _fallbackReasons.GetValueOrDefault(reason) + 1);
    }

    private void ThrowIfDisposedNoLock() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static void DisposeRedundantSource(
        IMeshletStreamingPageSource candidate,
        IMeshletStreamingPageSource existing)
    {
        if (!ReferenceEquals(candidate, existing) &&
            candidate is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static bool IsAgeAtLeast(
        ulong current,
        ulong previous,
        ulong age) =>
        current >= previous && current - previous >= age;

    private bool IsUploadCompleteNoLock(
        in MeshletPageUploadTicket ticket,
        ulong completedSerial)
    {
        if (_uploader is IMeshletStreamingUploadCompletionSource source)
            return source.IsUploadComplete(ticket, completedSerial);
        return ticket.CompletionSerial <= completedSerial;
    }

    private void PublishRangeReadiness(PackageRuntime package)
    {
        if (_uploader is not IMeshletStreamingRangeStateSink sink)
            return;

        (int RangeIndex, bool Ready)[] readiness;
        lock (_lock)
        {
            if (_disposed || package.Unloading &&
                !_packagesById.ContainsKey(package.Id.Value))
            {
                return;
            }
            readiness = new (int, bool)[package.GpuContracts.Ranges.Length];
            for (int index = 0; index < readiness.Length; index++)
            {
                GPUMeshletStreamingRange range =
                    package.GpuContracts.Ranges[index];
                int firstLocalPage = checked(
                    (int)range.FirstGlobalPageId -
                    package.GlobalPageBase);
                int pageCount = checked((int)range.PageCount);
                bool ready = pageCount > 0;
                for (int pageOffset = 0;
                     pageOffset < pageCount;
                     pageOffset++)
                {
                    if (package.Pages[firstLocalPage + pageOffset].State !=
                        MeshletPageResidencyState.Resident)
                    {
                        ready = false;
                        break;
                    }
                }
                readiness[index] = (
                    checked((int)package.GpuContracts.StreamingRangeBase +
                        index),
                    ready);
            }
        }

        foreach ((int rangeIndex, bool ready) in readiness)
            sink.SetRangeReady(rangeIndex, ready);
    }

    private static ulong SaturatingAdd(ulong value, ulong addition) =>
        value > ulong.MaxValue - addition
            ? ulong.MaxValue
            : value + addition;

    public void Dispose()
    {
        List<PackageRuntime> packages;
        lock (_lock)
        {
            if (_disposed)
                return;
            if (Volatile.Read(ref _tickActive) != 0)
            {
                throw new InvalidOperationException(
                    "The meshlet residency coordinator cannot be disposed during a tick.");
            }
            _disposed = true;
            packages = _packagesById.Values.ToList();
            foreach (PackageRuntime package in packages)
            {
                package.Unloading = true;
                package.Cancellation.Cancel();
            }
            _packagesById.Clear();
            _packagesByKey.Clear();
            _pagesByGlobalId.Clear();
            _packagesByGlobalRangeIndex.Clear();
            _retiredSlots.Clear();
            _freeSlots.Clear();
        }
        foreach (PackageRuntime package in packages)
        {
            package.Cancellation.Dispose();
            if (package.Source is IDisposable disposable)
                disposable.Dispose();
        }
        if (_ownsBanks)
            _banks.Dispose();
    }

    private sealed class PackageRuntime
    {
        public PackageRuntime(
            MeshletStreamingPackageId id,
            string key,
            IMeshletStreamingPageSource source,
            int globalPageBase,
            PackageGpuContracts gpuContracts)
        {
            Id = id;
            Key = key;
            Source = source;
            Manifest = source.Manifest;
            GlobalPageBase = globalPageBase;
            GpuContracts = gpuContracts;
            Pages = new PageRuntime[Manifest.Pages.Count];
        }

        public MeshletStreamingPackageId Id { get; }
        public string Key { get; }
        public IMeshletStreamingPageSource Source { get; }
        public MeshletStreamingManifest Manifest { get; }
        public int GlobalPageBase { get; }
        public PackageGpuContracts GpuContracts { get; }
        public PageRuntime[] Pages { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public int ReferenceCount { get; set; } = 1;
        public bool Unloading { get; set; }
        public Dictionary<int, uint> FinalizedVertexOffsets { get; } = [];
    }

    private sealed class PageRuntime
    {
        public PageRuntime(
            PackageRuntime package,
            MeshletStreamingPageRecord record,
            int globalPageId)
        {
            Package = package;
            Record = record;
            GlobalPageId = globalPageId;
        }

        public PackageRuntime Package { get; }
        public MeshletStreamingPageRecord Record { get; }
        public int GlobalPageId { get; }
        public bool IsPinned =>
            (Record.Flags & MeshletStreamingPageFlags.Pinned) != 0;
        public MeshletPageResidencyState State;
        public int PhysicalSlot = -1;
        public bool Requested;
        public int Priority;
        public ulong LastRequestedSerial;
        public ulong LastObservedRequestSerial = ulong.MaxValue;
        public int ConsecutiveFailureCount;
        public ulong RetryAfterSerial;
        public MeshletPageUploadTicket Ticket;
    }

    private readonly record struct ReadReservation(
        PageRuntime Page,
        int PhysicalSlot);

    private readonly record struct ReadResult(
        ReadReservation Reservation,
        byte[]? DecodedPage,
        Exception? Error);

    private readonly record struct RetiredSlot(
        PageRuntime Page,
        int PhysicalSlot,
        ulong RetireAfterSerial);

    private sealed record PackageGpuContracts(
        uint VirtualMappingBase,
        GPUMeshletVirtualMapping[] VirtualMappings,
        uint StreamingRangeBase,
        GPUMeshletStreamingRange[] Ranges,
        Dictionary<int, MeshletStreamingSubMeshGpuBinding> Bindings);
}
