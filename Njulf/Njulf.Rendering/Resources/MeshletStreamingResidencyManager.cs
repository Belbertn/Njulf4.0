using Njulf.Assets.Cooked;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public enum MeshletPageResidencyState
{
    Unloaded,
    Queued,
    Reading,
    Uploading,
    Resident,
    Evicting,
    Failed
}

public sealed record MeshletStreamingResidencyOptions(
    int PhysicalPageCapacity = 4096,
    int MaximumUploadBytesPerTick = 8 * 1024 * 1024,
    int MaximumAdmissionsPerTick = 128,
    int MaximumConcurrentReads = 4,
    int MaximumRequestsPerSerial = 4096,
    ulong EvictionGraceSerials = 120,
    ulong DemandLifetimeSerials = 2,
    ulong RetryBaseSerials = 30,
    ulong RetryMaximumSerials = 600,
    ulong FramesInFlight = 2)
{
    public const long ProductionPhysicalCacheBytes =
        4096L * MeshletStreamingManifest.ProductionPageSizeBytes;

    public void Validate()
    {
        if (PhysicalPageCapacity <= 0 ||
            MaximumUploadBytesPerTick <
            MeshletStreamingManifest.ProductionPageSizeBytes ||
            MaximumAdmissionsPerTick <= 0 ||
            MaximumConcurrentReads <= 0 ||
            MaximumConcurrentReads > MaximumAdmissionsPerTick ||
            MaximumRequestsPerSerial <= 0 ||
            DemandLifetimeSerials == 0 ||
            RetryBaseSerials == 0 ||
            RetryMaximumSerials < RetryBaseSerials ||
            FramesInFlight == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MeshletStreamingResidencyOptions),
                "Meshlet residency options violate the production bounds.");
        }
    }

    public static MeshletStreamingResidencyOptions FromSettings(
        SceneSubmissionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new MeshletStreamingResidencyOptions(
            PhysicalPageCapacity:
                settings.GpuMeshletStreamingPhysicalPageCount,
            MaximumUploadBytesPerTick: checked(
                settings.GpuMeshletStreamingUploadBudgetMiB *
                1024 * 1024),
            MaximumAdmissionsPerTick: 128,
            MaximumConcurrentReads:
                settings.GpuMeshletStreamingConcurrentReads,
            MaximumRequestsPerSerial:
                settings.GpuMeshletStreamingMaximumRequestsPerFrame);
    }
}

public readonly record struct MeshletPageUploadTicket(
    long TicketId,
    int PageId,
    int PhysicalSlot,
    ulong CompletionSerial)
{
    public bool IsValid =>
        TicketId > 0 && PageId >= 0 && PhysicalSlot >= 0;
}

/// <summary>
/// Provides authenticated, decoded 64 KiB meshlet pages. Implementations may
/// perform I/O concurrently; callers never trust a page until its manifest
/// length and internal payload structure have also been checked.
/// </summary>
public interface IMeshletStreamingPageSource
{
    MeshletStreamingManifest Manifest { get; }

    ValueTask<byte[]> ReadPageAsync(
        int pageId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Render-backend boundary for a fixed-slot GPU page cache. Publication and
/// unpublication must be transactional: an exception must leave the page table
/// unchanged. Upload memory is not made visible until <see cref="PublishResident"/>.
/// </summary>
public interface IMeshletStreamingPageUploader
{
    ValueTask<MeshletPageUploadTicket> BeginUploadAsync(
        int pageId,
        int physicalSlot,
        ReadOnlyMemory<byte> decodedPage,
        ulong submissionSerial,
        CancellationToken cancellationToken = default);

    void PublishResident(int pageId, int physicalSlot);

    void UnpublishResident(
        int pageId,
        int physicalSlot,
        ulong retireAfterSerial);
}

public sealed class MeshletStreamingPageFileSource :
    IMeshletStreamingPageSource,
    IDisposable
{
    private readonly MeshletStreamingPageFile _file;

    public MeshletStreamingPageFileSource(
        string meshPackagePath,
        MeshletStreamingManifest manifest)
    {
        _file = MeshletStreamingPageFile.Open(
            meshPackagePath,
            manifest);
    }

    public MeshletStreamingManifest Manifest => _file.Manifest;

    public ValueTask<byte[]> ReadPageAsync(
        int pageId,
        CancellationToken cancellationToken = default) =>
        _file.ReadPageAsync(pageId, cancellationToken);

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// Owns the page-file handle and residency state for one cooked mesh package.
/// Failure to open a session is explicitly recoverable because the ordinary
/// cooked mesh payload remains the full-resident correctness baseline.
/// </summary>
public sealed class MeshletStreamingResidencySession : IDisposable
{
    private readonly MeshletStreamingPageFileSource _source;
    private bool _disposed;

    private MeshletStreamingResidencySession(
        MeshletStreamingPageFileSource source,
        MeshletStreamingResidencyManager manager)
    {
        _source = source;
        Manager = manager;
    }

    public MeshletStreamingResidencyManager Manager { get; }

    public static bool TryOpen(
        CookedModelAsset model,
        IMeshletStreamingPageUploader uploader,
        MeshletStreamingResidencyOptions? options,
        out MeshletStreamingResidencySession? session,
        out string fallbackReason)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(uploader);
        session = null;
        MeshletStreamingManifest? manifest =
            model.Mesh.StreamingManifest;
        if (manifest is null)
        {
            fallbackReason = "meshlet-streaming-manifest-missing";
            return false;
        }
        if (string.IsNullOrWhiteSpace(model.MeshPackagePath))
        {
            fallbackReason = "meshlet-streaming-package-path-missing";
            return false;
        }

        MeshletStreamingPageFileSource? source = null;
        try
        {
            source = new MeshletStreamingPageFileSource(
                model.MeshPackagePath,
                manifest);
            var manager = new MeshletStreamingResidencyManager(
                source,
                uploader,
                options);
            session = new MeshletStreamingResidencySession(
                source,
                manager);
            fallbackReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                CookedAssetFormatException or InvalidDataException or
                InvalidOperationException or ArgumentException)
        {
            source?.Dispose();
            fallbackReason =
                $"meshlet-streaming-full-resident-fallback:{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.Dispose();
    }
}

public readonly record struct MeshletPageResolution(
    int RequestedPageId,
    int ResolvedPageId,
    int PhysicalSlot,
    bool UsesFallback)
{
    public static MeshletPageResolution Unavailable(int requestedPageId) =>
        new(requestedPageId, -1, -1, false);

    public bool IsResident => ResolvedPageId >= 0 && PhysicalSlot >= 0;
}

public sealed record MeshletStreamingFailure(
    int PageId,
    ulong Serial,
    int ConsecutiveFailureCount,
    string Detail);

public sealed record MeshletStreamingResidencySnapshot(
    int PageCount,
    int PhysicalPageCapacity,
    int FreePhysicalPageCount,
    int PinnedPageCount,
    int PinnedResidentPageCount,
    int ResidentPageCount,
    int QueuedPageCount,
    int ReadingPageCount,
    int UploadingPageCount,
    int EvictingPageCount,
    int FailedPageCount,
    long ResidentDecodedBytes,
    long RequestCount,
    long ResidentHitCount,
    long FallbackHitCount,
    long DroppedRequestCount,
    long AdmissionCount,
    long EvictionCount,
    long FailureCount,
    MeshletStreamingFailure? LastFailure);

/// <summary>
/// Deterministic residency controller for independently authenticated meshlet
/// pages. It parallelizes disk reads, serializes GPU publication, keeps coarse
/// and skinned pages pinned, and never reuses a slot until all in-flight GPU
/// frames that could reference its old mapping have completed.
/// </summary>
public sealed class MeshletStreamingResidencyManager
{
    public const int PinnedPriority = int.MaxValue;
    public const int VisiblePriority = 1_000_000;
    public const int PrefetchPriority = 100_000;

    private readonly object _lock = new();
    private readonly IMeshletStreamingPageSource _source;
    private readonly IMeshletStreamingPageUploader _uploader;
    private readonly MeshletStreamingResidencyOptions _options;
    private readonly PageRuntime[] _pages;
    private readonly Stack<int> _freeSlots;
    private readonly List<RetiredSlot> _retiredSlots = [];
    private int _tickActive;
    private long _requestCount;
    private long _residentHitCount;
    private long _fallbackHitCount;
    private long _droppedRequestCount;
    private long _admissionCount;
    private long _evictionCount;
    private long _failureCount;
    private MeshletStreamingFailure? _lastFailure;
    private ulong _requestWindowSerial;
    private int _requestWindowCount;

    public MeshletStreamingResidencyManager(
        IMeshletStreamingPageSource source,
        IMeshletStreamingPageUploader uploader,
        MeshletStreamingResidencyOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _options = options ?? new MeshletStreamingResidencyOptions();
        _options.Validate();
        _source.Manifest.Validate(nameof(source));
        if (_source.Manifest.PinnedPageCount >
            _options.PhysicalPageCapacity)
        {
            throw new InvalidOperationException(
                $"The mesh requires {_source.Manifest.PinnedPageCount} pinned pages, exceeding the {_options.PhysicalPageCapacity}-page physical cache.");
        }

        _pages = new PageRuntime[_source.Manifest.Pages.Count];
        for (int pageId = 0; pageId < _pages.Length; pageId++)
        {
            MeshletStreamingPageRecord record =
                _source.Manifest.Pages[pageId];
            bool pinned =
                (record.Flags & MeshletStreamingPageFlags.Pinned) != 0;
            _pages[pageId] = new PageRuntime
            {
                State = pinned
                    ? MeshletPageResidencyState.Queued
                    : MeshletPageResidencyState.Unloaded,
                PhysicalSlot = -1,
                Requested = pinned,
                Priority = pinned ? PinnedPriority : 0
            };
        }

        _freeSlots = new Stack<int>(_options.PhysicalPageCapacity);
        for (int slot = _options.PhysicalPageCapacity - 1;
             slot >= 0;
             slot--)
        {
            _freeSlots.Push(slot);
        }
    }

    public MeshletStreamingManifest Manifest => _source.Manifest;

    public MeshletStreamingResidencyOptions Options => _options;

    public MeshletPageResolution RequestPage(
        int pageId,
        int priority,
        ulong serial)
    {
        ValidatePageId(pageId);
        if (priority < 0)
            throw new ArgumentOutOfRangeException(nameof(priority));

        lock (_lock)
        {
            Interlocked.Increment(ref _requestCount);
            PageRuntime page = _pages[pageId];
            bool pinned = IsPinned(pageId);
            if (!pinned && page.LastObservedRequestSerial != serial)
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
                    return ResolveResidentNoLock(pageId, countHit: true);
                }
                _requestWindowCount++;
                page.LastObservedRequestSerial = serial;
            }
            page.Requested = true;
            page.Priority = pinned
                ? PinnedPriority
                : Math.Max(page.Priority, priority);
            page.LastRequestedSerial = serial;
            if (page.State is MeshletPageResidencyState.Unloaded ||
                page.State is MeshletPageResidencyState.Failed &&
                serial >= page.RetryAfterSerial)
            {
                page.State = MeshletPageResidencyState.Queued;
            }
            return ResolveResidentNoLock(pageId, countHit: true);
        }
    }

    public int RequestSubMeshPages(
        int subMeshIndex,
        MeshletStreamingPageFlags geometryFlags,
        int priority,
        ulong serial)
    {
        if (subMeshIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(subMeshIndex));
        const MeshletStreamingPageFlags allowed =
            MeshletStreamingPageFlags.Lod0 |
            MeshletStreamingPageFlags.Lod1 |
            MeshletStreamingPageFlags.Lod2 |
            MeshletStreamingPageFlags.HierarchyGeometry;
        geometryFlags &= allowed;
        if (geometryFlags == 0)
            throw new ArgumentOutOfRangeException(nameof(geometryFlags));

        int requested = 0;
        foreach (MeshletStreamingPageRecord page in Manifest.Pages)
        {
            if (page.SubMeshIndex == subMeshIndex &&
                (page.Flags & geometryFlags) != 0)
            {
                RequestPage(page.PageId, priority, serial);
                requested++;
            }
        }
        return requested;
    }

    public void ReleasePageDemand(int pageId)
    {
        ValidatePageId(pageId);
        if (IsPinned(pageId))
            return;
        lock (_lock)
        {
            PageRuntime page = _pages[pageId];
            page.Requested = false;
            page.Priority = 0;
            if (page.State == MeshletPageResidencyState.Queued)
                page.State = MeshletPageResidencyState.Unloaded;
        }
    }

    public MeshletPageResolution ResolveResident(int pageId)
    {
        ValidatePageId(pageId);
        lock (_lock)
            return ResolveResidentNoLock(pageId, countHit: false);
    }

    /// <summary>
    /// Resolves the complete pinned coarse cut for a page. An empty result
    /// means at least one required fallback page has not been published yet.
    /// </summary>
    public IReadOnlyList<MeshletPageResolution> ResolveFallbackGroup(
        int pageId)
    {
        ValidatePageId(pageId);
        lock (_lock)
        {
            MeshletStreamingPageRecord requested = Manifest.Pages[pageId];
            var result = new MeshletPageResolution[
                requested.FallbackPageCount];
            for (int offset = 0;
                 offset < requested.FallbackPageCount;
                 offset++)
            {
                int fallbackPageId =
                    requested.FallbackPageId + offset;
                PageRuntime fallback = _pages[fallbackPageId];
                if (fallback.State != MeshletPageResidencyState.Resident)
                    return Array.Empty<MeshletPageResolution>();
                result[offset] = new MeshletPageResolution(
                    pageId,
                    fallbackPageId,
                    fallback.PhysicalSlot,
                    fallbackPageId != pageId);
            }
            return result;
        }
    }

    public MeshletPageResidencyState GetState(int pageId)
    {
        ValidatePageId(pageId);
        lock (_lock)
            return _pages[pageId].State;
    }

    public async ValueTask TickAsync(
        ulong submissionSerial,
        ulong completedSerial,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _tickActive, 1) != 0)
        {
            throw new InvalidOperationException(
                "Meshlet residency ticks may not overlap.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessCompletedUploads(completedSerial, submissionSerial);
            ReclaimRetiredSlots(completedSerial, submissionSerial);
            ExpireStaleDemand(submissionSerial);
            BeginRequiredEvictions(submissionSerial);

            int remainingAdmissions = _options.MaximumAdmissionsPerTick;
            int remainingUploadBytes = _options.MaximumUploadBytesPerTick;
            while (remainingAdmissions > 0 && remainingUploadBytes > 0)
            {
                List<ReadReservation> reservations =
                    ReserveAdmissions(
                        submissionSerial,
                        remainingAdmissions,
                        remainingUploadBytes);
                if (reservations.Count == 0)
                    break;
                int reservedBytes = reservations.Sum(reservation =>
                    Manifest.Pages[reservation.PageId]
                        .UncompressedBytes);
                remainingAdmissions -= reservations.Count;
                remainingUploadBytes -= reservedBytes;

                ReadResult[] reads = await ReadReservationsAsync(
                        reservations,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (reads.Any(static read =>
                        read.Error is OperationCanceledException))
                {
                    foreach (ReadResult read in reads)
                    {
                        CancelAdmission(
                            read.Reservation,
                            submissionSerial);
                    }
                    throw new OperationCanceledException(cancellationToken);
                }
                foreach (ReadResult read in reads)
                {
                    if (read.Error is not null)
                    {
                        FailAdmission(
                            read.Reservation,
                            submissionSerial,
                            read.Error);
                        continue;
                    }

                    try
                    {
                        MeshletPageUploadTicket ticket = await _uploader
                            .BeginUploadAsync(
                                read.Reservation.PageId,
                                read.Reservation.PhysicalSlot,
                                read.DecodedPage!,
                                submissionSerial,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!ticket.IsValid ||
                            ticket.PageId != read.Reservation.PageId ||
                            ticket.PhysicalSlot !=
                            read.Reservation.PhysicalSlot)
                        {
                            throw new InvalidOperationException(
                                "The meshlet uploader returned a mismatched upload ticket.");
                        }
                        lock (_lock)
                        {
                            PageRuntime page = _pages[ticket.PageId];
                            RequireReservation(page, read.Reservation);
                            page.Ticket = ticket;
                            page.State = MeshletPageResidencyState.Uploading;
                            Interlocked.Increment(ref _admissionCount);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        CancelAdmission(
                            read.Reservation,
                            submissionSerial);
                        throw;
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
            }
        }
        finally
        {
            Volatile.Write(ref _tickActive, 0);
        }
    }

    public MeshletStreamingResidencySnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            int pinnedResident = 0;
            int resident = 0;
            int queued = 0;
            int reading = 0;
            int uploading = 0;
            int evicting = 0;
            int failed = 0;
            long residentBytes = 0;
            for (int pageId = 0; pageId < _pages.Length; pageId++)
            {
                switch (_pages[pageId].State)
                {
                    case MeshletPageResidencyState.Resident:
                        resident++;
                        residentBytes = checked(
                            residentBytes +
                            Manifest.Pages[pageId].UncompressedBytes);
                        if (IsPinned(pageId))
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
                    case MeshletPageResidencyState.Evicting:
                        evicting++;
                        break;
                    case MeshletPageResidencyState.Failed:
                        failed++;
                        break;
                }
            }
            return new MeshletStreamingResidencySnapshot(
                _pages.Length,
                _options.PhysicalPageCapacity,
                _freeSlots.Count,
                Manifest.PinnedPageCount,
                pinnedResident,
                resident,
                queued,
                reading,
                uploading,
                evicting,
                failed,
                residentBytes,
                Interlocked.Read(ref _requestCount),
                Interlocked.Read(ref _residentHitCount),
                Interlocked.Read(ref _fallbackHitCount),
                Interlocked.Read(ref _droppedRequestCount),
                Interlocked.Read(ref _admissionCount),
                Interlocked.Read(ref _evictionCount),
                Interlocked.Read(ref _failureCount),
                _lastFailure);
        }
    }

    private void ProcessCompletedUploads(
        ulong completedSerial,
        ulong submissionSerial)
    {
        List<(int PageId, int PhysicalSlot)> completed = [];
        lock (_lock)
        {
            for (int pageId = 0; pageId < _pages.Length; pageId++)
            {
                PageRuntime page = _pages[pageId];
                if (page.State == MeshletPageResidencyState.Uploading &&
                    page.Ticket.CompletionSerial <= completedSerial)
                {
                    completed.Add((pageId, page.PhysicalSlot));
                }
            }
        }

        foreach ((int pageId, int physicalSlot) in completed)
        {
            try
            {
                _uploader.PublishResident(pageId, physicalSlot);
                lock (_lock)
                {
                    PageRuntime page = _pages[pageId];
                    if (page.State != MeshletPageResidencyState.Uploading ||
                        page.PhysicalSlot != physicalSlot)
                    {
                        throw new InvalidOperationException(
                            "Meshlet upload completion changed concurrently.");
                    }
                    page.State = MeshletPageResidencyState.Resident;
                    page.ConsecutiveFailureCount = 0;
                    page.RetryAfterSerial = 0;
                }
            }
            catch (Exception ex) when (
                ex is not StackOverflowException and
                not OutOfMemoryException)
            {
                FailPublishedUpload(
                    pageId,
                    physicalSlot,
                    submissionSerial,
                    ex);
            }
        }
    }

    private void ReclaimRetiredSlots(
        ulong completedSerial,
        ulong submissionSerial)
    {
        lock (_lock)
        {
            for (int index = _retiredSlots.Count - 1; index >= 0; index--)
            {
                RetiredSlot retired = _retiredSlots[index];
                if (retired.RetireAfterSerial > completedSerial)
                    continue;
                PageRuntime page = _pages[retired.PageId];
                if (page.State != MeshletPageResidencyState.Evicting ||
                    page.PhysicalSlot != retired.PhysicalSlot)
                {
                    throw new InvalidOperationException(
                        "A retired meshlet slot lost its owning page.");
                }
                page.PhysicalSlot = -1;
                page.Ticket = default;
                page.State = IsDemandActive(page, retired.PageId,
                    submissionSerial)
                    ? MeshletPageResidencyState.Queued
                    : MeshletPageResidencyState.Unloaded;
                _freeSlots.Push(retired.PhysicalSlot);
                _retiredSlots.RemoveAt(index);
            }
        }
    }

    private void ExpireStaleDemand(ulong submissionSerial)
    {
        lock (_lock)
        {
            for (int pageId = 0; pageId < _pages.Length; pageId++)
            {
                if (IsPinned(pageId))
                    continue;
                PageRuntime page = _pages[pageId];
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
            }
            for (int pageId = 0; pageId < _pages.Length; pageId++)
            {
                PageRuntime page = _pages[pageId];
                if (page.State == MeshletPageResidencyState.Failed &&
                    submissionSerial >= page.RetryAfterSerial &&
                    IsDemandActive(page, pageId, submissionSerial))
                {
                    page.State = MeshletPageResidencyState.Queued;
                }
            }
        }
    }

    private void BeginRequiredEvictions(ulong submissionSerial)
    {
        while (true)
        {
            EvictionReservation? eviction;
            lock (_lock)
            {
                if (_freeSlots.Count != 0 ||
                    !HasQueuedAdmissionNoLock(submissionSerial))
                {
                    return;
                }
                eviction = SelectEvictionNoLock(submissionSerial);
                if (eviction is null)
                    return;
                PageRuntime page = _pages[eviction.Value.PageId];
                page.State = MeshletPageResidencyState.Evicting;
            }

            ulong retireAfter = SaturatingAdd(
                submissionSerial,
                _options.FramesInFlight);
            try
            {
                _uploader.UnpublishResident(
                    eviction.Value.PageId,
                    eviction.Value.PhysicalSlot,
                    retireAfter);
                lock (_lock)
                {
                    _retiredSlots.Add(new RetiredSlot(
                        eviction.Value.PageId,
                        eviction.Value.PhysicalSlot,
                        retireAfter));
                    Interlocked.Increment(ref _evictionCount);
                }
            }
            catch
            {
                lock (_lock)
                    _pages[eviction.Value.PageId].State =
                        MeshletPageResidencyState.Resident;
                throw;
            }

            // The unmapped slot remains in-flight and cannot satisfy this
            // frame's admission. More evictions would only increase churn.
            return;
        }
    }

    private List<ReadReservation> ReserveAdmissions(
        ulong submissionSerial,
        int maximumAdmissions,
        int maximumUploadBytes)
    {
        lock (_lock)
        {
            var candidates = Enumerable.Range(0, _pages.Length)
                .Where(pageId =>
                    _pages[pageId].State ==
                        MeshletPageResidencyState.Queued &&
                    IsDemandActive(
                        _pages[pageId], pageId, submissionSerial) &&
                    submissionSerial >=
                        _pages[pageId].RetryAfterSerial)
                .OrderByDescending(IsPinned)
                .ThenByDescending(pageId => _pages[pageId].Priority)
                .ThenByDescending(pageId =>
                    _pages[pageId].LastRequestedSerial)
                .ThenBy(static pageId => pageId);
            var reservations = new List<ReadReservation>(
                Math.Min(
                    maximumAdmissions,
                    _options.MaximumConcurrentReads));
            int uploadBytes = 0;
            foreach (int pageId in candidates)
            {
                if (reservations.Count >= maximumAdmissions ||
                    reservations.Count >=
                        _options.MaximumConcurrentReads ||
                    _freeSlots.Count == 0)
                {
                    break;
                }
                int pageBytes =
                    Manifest.Pages[pageId].UncompressedBytes;
                if (uploadBytes >
                    maximumUploadBytes - pageBytes)
                {
                    break;
                }
                int slot = _freeSlots.Pop();
                PageRuntime page = _pages[pageId];
                page.State = MeshletPageResidencyState.Reading;
                page.PhysicalSlot = slot;
                reservations.Add(new ReadReservation(pageId, slot));
                uploadBytes = checked(uploadBytes + pageBytes);
            }
            return reservations;
        }
    }

    private async Task<ReadResult[]> ReadReservationsAsync(
        IReadOnlyList<ReadReservation> reservations,
        CancellationToken cancellationToken)
    {
        Task<ReadResult>[] tasks = reservations
            .Select(reservation => ReadReservationAsync(
                reservation,
                cancellationToken))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ReadResult> ReadReservationAsync(
        ReadReservation reservation,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] decoded = await _source.ReadPageAsync(
                    reservation.PageId,
                    cancellationToken)
                .ConfigureAwait(false);
            MeshletStreamingPageRecord record =
                Manifest.Pages[reservation.PageId];
            if (decoded.Length != record.UncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Meshlet page {reservation.PageId} returned {decoded.Length} bytes; {record.UncompressedBytes} were authenticated by the manifest.");
            }
            MeshletStreamingPagePayload payload =
                MeshletStreamingPageCodec.Decode(decoded);
            if (payload.Meshlets.Length != record.MeshletCount)
            {
                throw new InvalidDataException(
                    $"Meshlet page {reservation.PageId} contains an unexpected meshlet count.");
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

    private void FailAdmission(
        ReadReservation reservation,
        ulong serial,
        Exception error)
    {
        lock (_lock)
        {
            PageRuntime page = _pages[reservation.PageId];
            RequireReservation(page, reservation);
            page.PhysicalSlot = -1;
            page.Ticket = default;
            _freeSlots.Push(reservation.PhysicalSlot);
            RecordFailureNoLock(reservation.PageId, serial, error);
        }
    }

    private void CancelAdmission(
        ReadReservation reservation,
        ulong serial)
    {
        lock (_lock)
        {
            PageRuntime page = _pages[reservation.PageId];
            RequireReservation(page, reservation);
            page.PhysicalSlot = -1;
            page.Ticket = default;
            page.State = IsDemandActive(
                page,
                reservation.PageId,
                serial)
                ? MeshletPageResidencyState.Queued
                : MeshletPageResidencyState.Unloaded;
            _freeSlots.Push(reservation.PhysicalSlot);
        }
    }

    private void FailPublishedUpload(
        int pageId,
        int physicalSlot,
        ulong serial,
        Exception error)
    {
        lock (_lock)
        {
            PageRuntime page = _pages[pageId];
            if (page.PhysicalSlot != physicalSlot)
            {
                throw new InvalidOperationException(
                    "A failed meshlet publication lost its reserved slot.",
                    error);
            }
            page.PhysicalSlot = -1;
            page.Ticket = default;
            _freeSlots.Push(physicalSlot);
            RecordFailureNoLock(pageId, serial, error);
        }
    }

    private void RecordFailureNoLock(
        int pageId,
        ulong serial,
        Exception error)
    {
        PageRuntime page = _pages[pageId];
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
        page.State = MeshletPageResidencyState.Failed;
        Interlocked.Increment(ref _failureCount);
        _lastFailure = new MeshletStreamingFailure(
            pageId,
            serial,
            page.ConsecutiveFailureCount,
            $"{error.GetType().Name}: {error.Message}");
    }

    private MeshletPageResolution ResolveResidentNoLock(
        int requestedPageId,
        bool countHit)
    {
        PageRuntime requested = _pages[requestedPageId];
        if (requested.State == MeshletPageResidencyState.Resident)
        {
            if (countHit)
                Interlocked.Increment(ref _residentHitCount);
            return new MeshletPageResolution(
                requestedPageId,
                requestedPageId,
                requested.PhysicalSlot,
                false);
        }

        MeshletStreamingPageRecord record =
            Manifest.Pages[requestedPageId];
        int fallbackPageId = record.FallbackPageId;
        PageRuntime fallback = _pages[fallbackPageId];
        bool fallbackGroupResident = true;
        for (int offset = 0;
             offset < record.FallbackPageCount;
             offset++)
        {
            if (_pages[fallbackPageId + offset].State !=
                MeshletPageResidencyState.Resident)
            {
                fallbackGroupResident = false;
                break;
            }
        }
        if (fallbackGroupResident &&
            fallback.State == MeshletPageResidencyState.Resident)
        {
            if (countHit)
                Interlocked.Increment(ref _fallbackHitCount);
            return new MeshletPageResolution(
                requestedPageId,
                fallbackPageId,
                fallback.PhysicalSlot,
                fallbackPageId != requestedPageId);
        }
        return MeshletPageResolution.Unavailable(requestedPageId);
    }

    private bool HasQueuedAdmissionNoLock(ulong submissionSerial) =>
        Enumerable.Range(0, _pages.Length).Any(pageId =>
            _pages[pageId].State == MeshletPageResidencyState.Queued &&
            IsDemandActive(_pages[pageId], pageId, submissionSerial) &&
            submissionSerial >= _pages[pageId].RetryAfterSerial);

    private EvictionReservation? SelectEvictionNoLock(
        ulong submissionSerial)
    {
        int queuedPageId = Enumerable.Range(0, _pages.Length)
            .Where(candidate =>
                _pages[candidate].State ==
                    MeshletPageResidencyState.Queued &&
                IsDemandActive(
                    _pages[candidate], candidate, submissionSerial) &&
                submissionSerial >=
                    _pages[candidate].RetryAfterSerial)
            .OrderByDescending(candidate => _pages[candidate].Priority)
            .ThenByDescending(candidate =>
                _pages[candidate].LastRequestedSerial)
            .ThenBy(static candidate => candidate)
            .DefaultIfEmpty(-1)
            .First();
        if (queuedPageId < 0)
            return null;
        PageRuntime queued = _pages[queuedPageId];
        int pageId = Enumerable.Range(0, _pages.Length)
            .Where(candidate =>
                !IsPinned(candidate) &&
                _pages[candidate].State ==
                    MeshletPageResidencyState.Resident &&
                (_pages[candidate].Priority < queued.Priority ||
                 _pages[candidate].Priority == queued.Priority &&
                 _pages[candidate].LastRequestedSerial <
                    queued.LastRequestedSerial) &&
                IsAgeAtLeast(
                    submissionSerial,
                    _pages[candidate].LastRequestedSerial,
                    _options.EvictionGraceSerials))
            .OrderBy(candidate => _pages[candidate].LastRequestedSerial)
            .ThenBy(candidate => _pages[candidate].Priority)
            .ThenBy(static candidate => candidate)
            .DefaultIfEmpty(-1)
            .First();
        return pageId < 0
            ? null
            : new EvictionReservation(
                pageId,
                _pages[pageId].PhysicalSlot);
    }

    private bool IsDemandActive(
        PageRuntime page,
        int pageId,
        ulong submissionSerial) =>
        IsPinned(pageId) ||
        page.Requested && !IsAgeAtLeast(
            submissionSerial,
            page.LastRequestedSerial,
            _options.DemandLifetimeSerials + 1);

    private bool IsPinned(int pageId) =>
        (Manifest.Pages[pageId].Flags &
         MeshletStreamingPageFlags.Pinned) != 0;

    private static bool IsAgeAtLeast(
        ulong current,
        ulong previous,
        ulong age) =>
        current >= previous && current - previous >= age;

    private static ulong SaturatingAdd(ulong value, ulong addition) =>
        value > ulong.MaxValue - addition
            ? ulong.MaxValue
            : value + addition;

    private static void RequireReservation(
        PageRuntime page,
        ReadReservation reservation)
    {
        if (page.State != MeshletPageResidencyState.Reading ||
            page.PhysicalSlot != reservation.PhysicalSlot)
        {
            throw new InvalidOperationException(
                "A meshlet page admission reservation changed concurrently.");
        }
    }

    private void ValidatePageId(int pageId)
    {
        if ((uint)pageId >= (uint)_pages.Length)
            throw new ArgumentOutOfRangeException(nameof(pageId));
    }

    private sealed class PageRuntime
    {
        public MeshletPageResidencyState State;
        public int PhysicalSlot;
        public bool Requested;
        public int Priority;
        public ulong LastRequestedSerial;
        public ulong LastObservedRequestSerial = ulong.MaxValue;
        public int ConsecutiveFailureCount;
        public ulong RetryAfterSerial;
        public MeshletPageUploadTicket Ticket;
    }

    private readonly record struct ReadReservation(
        int PageId,
        int PhysicalSlot);

    private readonly record struct ReadResult(
        ReadReservation Reservation,
        byte[]? DecodedPage,
        Exception? Error);

    private readonly record struct EvictionReservation(
        int PageId,
        int PhysicalSlot);

    private readonly record struct RetiredSlot(
        int PageId,
        int PhysicalSlot,
        ulong RetireAfterSerial);
}
