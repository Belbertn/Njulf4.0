namespace Njulf.Rendering.Resources;

public interface IMeshletPhysicalBankProvider
{
    MeshletPhysicalBankAllocator Banks { get; }
}

internal interface IMeshletStreamingUploadCompletionSource
{
    bool IsUploadComplete(
        in MeshletPageUploadTicket ticket,
        ulong completedSerial);
}

internal interface IMeshletStreamingRangeStateSink
{
    void SetRangeReady(int rangeIndex, bool ready);
}

internal interface IMeshletGpuContractSink
{
    void RegisterPackageContracts(
        uint virtualMappingBase,
        IReadOnlyList<GPUMeshletVirtualMapping> virtualMappings,
        uint streamingRangeBase,
        IReadOnlyList<GPUMeshletStreamingRange> streamingRanges);

    void FinalizeVirtualMappingVertexOffset(
        uint firstVirtualMapping,
        uint mappingCount,
        uint vertexOffset);
}

internal sealed record MeshletPackedPageUpload(
    int GlobalPageId,
    int PhysicalSlot,
    ReadOnlyMemory<byte> PageBytes);

public sealed record MeshletPhysicalPageCacheSnapshot(
    int WritableFrameSlot,
    int PackedPageCount,
    int PendingUploadCount,
    int Frame0MappingCount,
    int Frame1MappingCount,
    int Frame0ReadyRangeCount,
    int Frame1ReadyRangeCount,
    long InvalidMappingCount,
    long PublishedMappingCount,
    long UnpublishedMappingCount,
    MeshletPhysicalBankSnapshot Banks);

internal sealed record MeshletPhysicalFrameStateSnapshot(
    GPUMeshletPageTableEntry[] PageTable,
    uint[] RangeStateWords,
    ulong RangeStateRevision);

/// <summary>
/// Backend-neutral physical cache implementation used by tests and by Vulkan
/// staging integration. It owns exact packed pages plus two fence-safe mapping
/// and range-readiness copies; a Vulkan backend mirrors these byte arrays into
/// the stable bindless bank buffers.
/// </summary>
public sealed class MeshletPhysicalPageCacheUploader :
    IMeshletStreamingPageUploader,
    IMeshletPhysicalBankProvider,
    IMeshletGpuContractSink,
    IMeshletStreamingUploadCompletionSource,
    IMeshletStreamingRangeStateSink,
    IDisposable
{
    private readonly object _lock = new();
    private readonly Func<int, uint> _globalVertexOffsetResolver;
    private readonly Dictionary<int, PendingUpload> _pending = [];
    private readonly Dictionary<int, PackedSlot> _packedSlots = [];
    private readonly Dictionary<int, GPUMeshletPageTableEntry>[]
        _pageTables = [[], []];
    private readonly HashSet<int>[] _readyRanges = [[], []];
    private readonly ulong[] _rangeStateRevisions = new ulong[2];
    private readonly uint[][] _recordedRangeStateWords = [[], []];
    private readonly ulong[] _recordedRangeStateRevisions = new ulong[2];
    private readonly Dictionary<int, uint> _generations = [];
    private readonly List<GPUMeshletVirtualMapping> _virtualMappings = [];
    private readonly List<GPUMeshletStreamingRange> _streamingRanges = [];
    private bool _immutableContractsDirty;
    private ulong _immutableContractRevision;
    private ulong _recordedImmutableContractRevision;
    private ulong _immutableContractCompletionSerial;
    private long _nextTicketId;
    private long _invalidMappingCount;
    private long _publishedMappingCount;
    private long _unpublishedMappingCount;
    private ulong _nextRangeStateRevision;
    private int _writableFrameSlot;
    private bool _requiresExplicitGpuRecording;
    private bool _disposed;

    public MeshletPhysicalPageCacheUploader(
        int physicalPageCapacity,
        Func<int, uint>? globalVertexOffsetResolver = null,
        IMeshletPhysicalMemoryBudget? memoryBudget = null)
    {
        Banks = new MeshletPhysicalBankAllocator(
            physicalPageCapacity,
            memoryBudget);
        _globalVertexOffsetResolver = globalVertexOffsetResolver ??
            (static _ => 0u);
    }

    public MeshletPhysicalBankAllocator Banks { get; }

    public int WritableFrameSlot
    {
        get
        {
            lock (_lock)
                return _writableFrameSlot;
        }
    }

    internal void RequireExplicitGpuRecording()
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            _requiresExplicitGpuRecording = true;
        }
    }

    /// <summary>
    /// Starts a fence-safe table generation. The selected slot must already
    /// have completed on the GPU. It inherits the currently authoritative
    /// source slot, then accepts publications for the next frame.
    /// </summary>
    public void PrepareFrameSlot(int writableFrameSlot, int sourceFrameSlot)
    {
        ValidateFrameSlot(writableFrameSlot);
        ValidateFrameSlot(sourceFrameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (writableFrameSlot != sourceFrameSlot)
            {
                _pageTables[writableFrameSlot] = new Dictionary<
                    int,
                    GPUMeshletPageTableEntry>(
                    _pageTables[sourceFrameSlot]);
                _readyRanges[writableFrameSlot] = new HashSet<int>(
                    _readyRanges[sourceFrameSlot]);
                _rangeStateRevisions[writableFrameSlot] =
                    _rangeStateRevisions[sourceFrameSlot];
            }
            _writableFrameSlot = writableFrameSlot;
        }
    }

    void IMeshletGpuContractSink.RegisterPackageContracts(
        uint virtualMappingBase,
        IReadOnlyList<GPUMeshletVirtualMapping> virtualMappings,
        uint streamingRangeBase,
        IReadOnlyList<GPUMeshletStreamingRange> streamingRanges)
    {
        ArgumentNullException.ThrowIfNull(virtualMappings);
        ArgumentNullException.ThrowIfNull(streamingRanges);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (virtualMappingBase != (uint)_virtualMappings.Count ||
                streamingRangeBase != (uint)_streamingRanges.Count)
            {
                throw new InvalidOperationException(
                    "Immutable meshlet streaming tables must grow append-only.");
            }
            _virtualMappings.AddRange(virtualMappings);
            _streamingRanges.AddRange(streamingRanges);
            _immutableContractsDirty = true;
            _immutableContractRevision = checked(
                _immutableContractRevision + 1);
        }
    }

    void IMeshletGpuContractSink.FinalizeVirtualMappingVertexOffset(
        uint firstVirtualMapping,
        uint mappingCount,
        uint vertexOffset)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            ulong end = checked(
                (ulong)firstVirtualMapping + mappingCount);
            if (mappingCount == 0 || end > (ulong)_virtualMappings.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(firstVirtualMapping));
            }
            for (uint offset = 0; offset < mappingCount; offset++)
            {
                int index = checked((int)(firstVirtualMapping + offset));
                _virtualMappings[index] = _virtualMappings[index] with
                {
                    VertexOffset = vertexOffset
                };
            }
            _immutableContractsDirty = true;
            _immutableContractRevision = checked(
                _immutableContractRevision + 1);
        }
    }

    internal IReadOnlyList<MeshletPackedPageUpload>
        CaptureUnrecordedUploads()
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            var result = new List<MeshletPackedPageUpload>();
            foreach ((int pageId, PendingUpload pending) in _pending)
            {
                if (pending.Recorded)
                    continue;
                result.Add(new MeshletPackedPageUpload(
                    pageId,
                    pending.Ticket.PhysicalSlot,
                    pending.PageBytes));
            }
            return result;
        }
    }

    internal void MarkUploadRecorded(
        int pageId,
        int physicalSlot,
        ulong completionSerial)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_pending.TryGetValue(pageId, out PendingUpload? pending) ||
                pending.Ticket.PhysicalSlot != physicalSlot)
            {
                throw new InvalidOperationException(
                    "A recorded physical-page upload no longer matches its pending ticket.");
            }
            pending.Recorded = true;
            pending.RecordedCompletionSerial = completionSerial;
        }
    }

    internal void MarkUploadFailed(
        int pageId,
        int physicalSlot,
        Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_pending.TryGetValue(pageId, out PendingUpload? pending) ||
                pending.Ticket.PhysicalSlot != physicalSlot)
            {
                return;
            }
            pending.Recorded = true;
            pending.RecordedCompletionSerial = 0;
            pending.Failure = failure;
        }
    }

    bool IMeshletStreamingUploadCompletionSource.IsUploadComplete(
        in MeshletPageUploadTicket ticket,
        ulong completedSerial)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_pending.TryGetValue(
                    ticket.PageId,
                    out PendingUpload? pending) ||
                pending.Ticket.TicketId != ticket.TicketId)
            {
                return false;
            }
            if (!_requiresExplicitGpuRecording)
                return ticket.CompletionSerial <= completedSerial;
            return pending.Recorded &&
                pending.RecordedCompletionSerial <= completedSerial;
        }
    }

    internal GPUMeshletPageTableEntry[] CapturePageTable(
        int frameSlot)
    {
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            int count = _pageTables[frameSlot].Keys
                .DefaultIfEmpty(-1)
                .Max() + 1;
            var result = new GPUMeshletPageTableEntry[count];
            Array.Fill(result, GPUMeshletPageTableEntry.Unmapped);
            foreach ((int pageId, GPUMeshletPageTableEntry entry) in
                     _pageTables[frameSlot])
            {
                result[pageId] = entry;
            }
            return result;
        }
    }

    internal uint[] CaptureRangeStateWords(int frameSlot)
    {
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            int wordCount = _readyRanges[frameSlot]
                .DefaultIfEmpty(-1)
                .Max() / 32 + 1;
            if (_readyRanges[frameSlot].Count == 0)
                wordCount = 0;
            var result = new uint[wordCount];
            foreach (int range in _readyRanges[frameSlot])
                result[range >> 5] |= 1u << (range & 31);
            return result;
        }
    }

    /// <summary>
    /// Captures the page table and whole-range readiness under one lock and
    /// publishes the exact range snapshot that the current command buffer will
    /// consume. CPU submission must resolve against this recorded snapshot,
    /// not against worker mutations that occur later in the frame.
    /// </summary>
    internal MeshletPhysicalFrameStateSnapshot
        CaptureFrameStateForRecording(int frameSlot)
    {
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            GPUMeshletPageTableEntry[] pageTable =
                CapturePageTableNoLock(frameSlot);
            uint[] rangeState = CaptureRangeStateWordsNoLock(frameSlot);
            ulong revision = _rangeStateRevisions[frameSlot];
            _recordedRangeStateWords[frameSlot] = rangeState;
            _recordedRangeStateRevisions[frameSlot] = revision;
            return new MeshletPhysicalFrameStateSnapshot(
                pageTable,
                rangeState,
                revision);
        }
    }

    internal ulong GetRecordedRangeStateRevision(int frameSlot)
    {
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            return _recordedRangeStateRevisions[frameSlot];
        }
    }

    internal bool IsRecordedRangeReady(
        uint rangeIndex,
        int frameSlot)
    {
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            uint[] words = _recordedRangeStateWords[frameSlot];
            uint wordIndex = rangeIndex >> 5;
            return wordIndex < words.Length &&
                (words[wordIndex] &
                 (1u << (int)(rangeIndex & 31u))) != 0u;
        }
    }

    internal bool TryCaptureImmutableContracts(
        out GPUMeshletVirtualMapping[] virtualMappings,
        out GPUMeshletStreamingRange[] streamingRanges,
        out ulong revision)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_immutableContractsDirty)
            {
                virtualMappings = Array.Empty<GPUMeshletVirtualMapping>();
                streamingRanges = Array.Empty<GPUMeshletStreamingRange>();
                revision = _immutableContractRevision;
                return false;
            }
            virtualMappings = _virtualMappings.ToArray();
            streamingRanges = _streamingRanges.ToArray();
            revision = _immutableContractRevision;
            _immutableContractsDirty = false;
            return true;
        }
    }

    internal void MarkImmutableContractsRecorded(
        ulong revision,
        ulong completionSerial)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (revision == 0 || revision > _immutableContractRevision)
                throw new ArgumentOutOfRangeException(nameof(revision));
            if (revision >= _recordedImmutableContractRevision)
            {
                _recordedImmutableContractRevision = revision;
                _immutableContractCompletionSerial = completionSerial;
            }
        }
    }

    internal bool AreImmutableContractsReady(ulong completedSerial)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            return _immutableContractRevision == 0 ||
                !_immutableContractsDirty &&
                _recordedImmutableContractRevision ==
                    _immutableContractRevision &&
                _immutableContractCompletionSerial <= completedSerial;
        }
    }

    internal void RestoreImmutableContractsDirty()
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            _immutableContractsDirty = true;
        }
    }

    public ValueTask<MeshletPageUploadTicket> BeginUploadAsync(
        int pageId,
        int physicalSlot,
        ReadOnlyMemory<byte> decodedPage,
        ulong submissionSerial,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageId < 0)
            throw new ArgumentOutOfRangeException(nameof(pageId));
        if (!Banks.EnsureSlotAvailable(
                physicalSlot,
                out string rejectionReason))
        {
            throw new InvalidOperationException(
                $"A physical meshlet bank could not be committed: {rejectionReason}");
        }
        MeshletGpuPagePackResult packed = MeshletGpuPagePacker.Pack(
            decodedPage.Span,
            _globalVertexOffsetResolver(pageId));
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (_pending.ContainsKey(pageId))
            {
                throw new InvalidOperationException(
                    "A meshlet page upload is already pending.");
            }
            long ticketId = checked(++_nextTicketId);
            var ticket = new MeshletPageUploadTicket(
                ticketId,
                pageId,
                physicalSlot,
                SaturatingAdd(submissionSerial, 1));
            _pending.Add(
                pageId,
                new PendingUpload(ticket, packed.PageBytes));
            return ValueTask.FromResult(ticket);
        }
    }

    public void PublishResident(int pageId, int physicalSlot)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_pending.Remove(pageId, out PendingUpload? pending) ||
                pending is null ||
                pending.Ticket.PhysicalSlot != physicalSlot)
            {
                throw new InvalidOperationException(
                    "The physical page publication does not match a pending upload.");
            }
            if (pending.Failure is { } failure)
            {
                throw new InvalidOperationException(
                    "The physical page could not be recorded into its Vulkan bank.",
                    failure);
            }
            if (_packedSlots.TryGetValue(
                    physicalSlot,
                    out PackedSlot existing) &&
                existing.PageId != pageId &&
                _pageTables.Any(table => table.TryGetValue(
                    existing.PageId,
                    out GPUMeshletPageTableEntry entry) &&
                    entry.IsResident))
            {
                throw new InvalidOperationException(
                    "A physical slot is still visible in a frame-safe page table.");
            }
            _packedSlots[physicalSlot] = new PackedSlot(
                pageId,
                pending.PageBytes);
            uint generation = NextGeneration(
                _generations.GetValueOrDefault(pageId));
            _generations[pageId] = generation;
            (uint bank, uint pageInBank) =
                MeshletPhysicalBankAllocator.DecodeSlot(physicalSlot);
            _pageTables[_writableFrameSlot][pageId] =
                new GPUMeshletPageTableEntry(
                    bank,
                    pageInBank,
                    generation,
                    MeshletGpuPageTableFlags.Resident);
            Interlocked.Increment(ref _publishedMappingCount);
        }
    }

    public void UnpublishResident(
        int pageId,
        int physicalSlot,
        ulong retireAfterSerial)
    {
        _ = retireAfterSerial;
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_packedSlots.TryGetValue(
                    physicalSlot,
                    out PackedSlot slot) ||
                slot.PageId != pageId)
            {
                throw new InvalidOperationException(
                    "The physical page mapping does not match its slot.");
            }
            _pageTables[_writableFrameSlot].Remove(pageId);
            Interlocked.Increment(ref _unpublishedMappingCount);
        }
    }

    public bool TryResolve(
        int pageId,
        int frameSlot,
        out GPUMeshletPageTableEntry entry)
    {
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (_pageTables[frameSlot].TryGetValue(
                    pageId,
                    out entry) && entry.IsResident)
            {
                return true;
            }
            entry = GPUMeshletPageTableEntry.Unmapped;
            Interlocked.Increment(ref _invalidMappingCount);
            return false;
        }
    }

    internal bool HasMapping(int pageId, int frameSlot)
    {
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            return _pageTables[frameSlot].TryGetValue(
                       pageId,
                       out GPUMeshletPageTableEntry entry) &&
                   entry.IsResident;
        }
    }

    public ReadOnlyMemory<byte> GetPackedPage(int physicalSlot)
    {
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            if (!_packedSlots.TryGetValue(
                    physicalSlot,
                    out PackedSlot page))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(physicalSlot));
            }
            return page.PageBytes;
        }
    }

    public void SetRangeReady(int rangeIndex, bool ready)
    {
        if (rangeIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(rangeIndex));
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            bool changed = ready
                ? _readyRanges[_writableFrameSlot].Add(rangeIndex)
                : _readyRanges[_writableFrameSlot].Remove(rangeIndex);
            if (changed)
            {
                _nextRangeStateRevision = checked(
                    _nextRangeStateRevision + 1UL);
                if (_nextRangeStateRevision == 0)
                    _nextRangeStateRevision = 1;
                _rangeStateRevisions[_writableFrameSlot] =
                    _nextRangeStateRevision;
            }
        }
    }

    public bool IsRangeReady(int rangeIndex, int frameSlot)
    {
        if (rangeIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(rangeIndex));
        ValidateFrameSlot(frameSlot);
        lock (_lock)
        {
            ThrowIfDisposedNoLock();
            return _readyRanges[frameSlot].Contains(rangeIndex);
        }
    }

    public MeshletPhysicalPageCacheSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            return new MeshletPhysicalPageCacheSnapshot(
                _writableFrameSlot,
                _packedSlots.Count,
                _pending.Count,
                _pageTables[0].Count,
                _pageTables[1].Count,
                _readyRanges[0].Count,
                _readyRanges[1].Count,
                Interlocked.Read(ref _invalidMappingCount),
                Interlocked.Read(ref _publishedMappingCount),
                Interlocked.Read(ref _unpublishedMappingCount),
                Banks.CreateSnapshot());
        }
    }

    private static void ValidateFrameSlot(int frameSlot)
    {
        if ((uint)frameSlot >= 2u)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
    }

    private GPUMeshletPageTableEntry[] CapturePageTableNoLock(
        int frameSlot)
    {
        int count = _pageTables[frameSlot].Keys
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var result = new GPUMeshletPageTableEntry[count];
        Array.Fill(result, GPUMeshletPageTableEntry.Unmapped);
        foreach ((int pageId, GPUMeshletPageTableEntry entry) in
                 _pageTables[frameSlot])
        {
            result[pageId] = entry;
        }
        return result;
    }

    private uint[] CaptureRangeStateWordsNoLock(int frameSlot)
    {
        int wordCount = _readyRanges[frameSlot]
            .DefaultIfEmpty(-1)
            .Max() / 32 + 1;
        if (_readyRanges[frameSlot].Count == 0)
            wordCount = 0;
        var result = new uint[wordCount];
        foreach (int range in _readyRanges[frameSlot])
            result[range >> 5] |= 1u << (range & 31);
        return result;
    }

    private void ThrowIfDisposedNoLock() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static uint NextGeneration(uint current)
    {
        uint next = current + 1u;
        return next == 0 ? 1u : next;
    }

    private static ulong SaturatingAdd(ulong value, ulong addition) =>
        value > ulong.MaxValue - addition
            ? ulong.MaxValue
            : value + addition;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending.Clear();
            _packedSlots.Clear();
            _pageTables[0].Clear();
            _pageTables[1].Clear();
            _readyRanges[0].Clear();
            _readyRanges[1].Clear();
        }
        Banks.Dispose();
    }

    private sealed class PendingUpload
    {
        public PendingUpload(
            MeshletPageUploadTicket ticket,
            byte[] pageBytes)
        {
            Ticket = ticket;
            PageBytes = pageBytes;
        }

        public MeshletPageUploadTicket Ticket { get; }
        public byte[] PageBytes { get; }
        public bool Recorded { get; set; }
        public ulong RecordedCompletionSerial { get; set; }
        public Exception? Failure { get; set; }
    }

    private readonly record struct PackedSlot(
        int PageId,
        byte[] PageBytes);
}
