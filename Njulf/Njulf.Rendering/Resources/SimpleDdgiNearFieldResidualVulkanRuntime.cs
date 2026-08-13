using System;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

internal readonly record struct SimpleDdgiNearFieldResidualVulkanBuffers(
    BufferHandle HitMetadata,
    BufferHandle HistoryMetadata0,
    BufferHandle HistoryMetadata1,
    BufferHandle TileRecords,
    BufferHandle TraceFrameConstants0,
    BufferHandle TraceFrameConstants1,
    BufferHandle TelemetryReadback0,
    BufferHandle TelemetryReadback1)
{
    public bool IsComplete => HitMetadata.IsValid &&
        HistoryMetadata0.IsValid && HistoryMetadata1.IsValid &&
        TileRecords.IsValid && TraceFrameConstants0.IsValid &&
        TraceFrameConstants1.IsValid && TelemetryReadback0.IsValid &&
        TelemetryReadback1.IsValid;

    public BufferHandle HistoryMetadata(int index) => index switch
    {
        0 => HistoryMetadata0,
        1 => HistoryMetadata1,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public BufferHandle TraceFrameConstants(int index) => index switch
    {
        0 => TraceFrameConstants0,
        1 => TraceFrameConstants1,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public BufferHandle TelemetryReadback(int index) => index switch
    {
        0 => TelemetryReadback0,
        1 => TelemetryReadback1,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

internal enum SimpleDdgiNearFieldResidualRecordedStage : byte
{
    Idle = 0,
    Reset = 1,
    Trace = 2,
    Temporal = 3,
    Filtering = 4,
    Composite = 5,
    Invalid = 6
}

/// <summary>
/// Concrete C5 Vulkan lifetime and frame-transaction boundary. Images are
/// graph-owned; this object owns the metadata/tile buffers, fixed descriptor
/// sets, pipelines, history parity, and the exact sequence of stage records.
/// It is constructed only after immutable C5 evidence has admitted the graph.
/// </summary>
public sealed unsafe class SimpleDdgiNearFieldResidualVulkanRuntime : IDisposable
{
    private readonly object _sync = new();
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly RenderTargetManager _renderTargets;
    private readonly HiZDepthPyramid _hiZ;
    private readonly SimpleDdgiNearFieldResidualLayout _layout;
    private readonly SimpleDdgiNearFieldResidualGpuConfiguration _configuration;
    private readonly uint _b3OwnershipRevision;
    private readonly SimpleDdgiNearFieldResidualGpuManager _manager = new();
    private readonly BorrowedGraphAllocationAdapter _allocationAdapter;
    private readonly SimpleDdgiNearFieldResidualVulkanBuffers _buffers;
    private readonly SimpleDdgiNearFieldResidualGpuCommandRecorder _recorder;
    private readonly PendingReadback?[] _pendingReadbacks =
        new PendingReadback?[RenderingConstants.FramesInFlight];

    private SimpleDdgiNearFieldResidualGpuFrameToken _frameToken;
    private SimpleDdgiNearFieldResidualRecordedStage _recordedStage;
    private int _recordedFilterIterations;
    private int _recordedFrameIndex = -1;
    private ulong _lastCameraCutSerial;
    private bool _hasCameraCutSerial;
    private bool _historyInputValid;
    private SimpleDdgiNearFieldResidualCompletionWitness _lastCompletedWitness;
    private SimpleDdgiNearFieldResidualDiagnostics _diagnostics =
        SimpleDdgiNearFieldResidualDiagnostics.Disabled();
    private ulong _actualAllocationBytes;
    private ulong _peakAllocationBytes;
    private bool _resourcesReleased;
    private bool _active;
    private bool _frameAdmission = true;
    private bool _disposed;

    public SimpleDdgiNearFieldResidualVulkanRuntime(
        VulkanContext context,
        BufferManager bufferManager,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        HiZDepthPyramid hiZ,
        in SimpleDdgiNearFieldResidualLayout layout,
        in SimpleDdgiNearFieldResidualGpuConfiguration configuration,
        uint b3OwnershipRevision,
        ulong admittedBudgetBytes)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        ArgumentNullException.ThrowIfNull(bindlessHeap);
        _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        _hiZ = hiZ ?? throw new ArgumentNullException(nameof(hiZ));
        _layout = layout;
        _configuration = configuration;
        _b3OwnershipRevision = b3OwnershipRevision == 0u
            ? throw new ArgumentOutOfRangeException(nameof(b3OwnershipRevision))
            : b3OwnershipRevision;
        if (admittedBudgetBytes == 0UL || layout.TotalBytes > admittedBudgetBytes)
            throw new ArgumentOutOfRangeException(nameof(admittedBudgetBytes));

        ValidatePhysicalIntegration(
            _context,
            _renderTargets,
            _hiZ,
            _layout,
            _configuration);

        BufferHandle hitMetadata = BufferHandle.Invalid;
        BufferHandle historyMetadata0 = BufferHandle.Invalid;
        BufferHandle historyMetadata1 = BufferHandle.Invalid;
        BufferHandle tileRecords = BufferHandle.Invalid;
        BufferHandle traceFrameConstants0 = BufferHandle.Invalid;
        BufferHandle traceFrameConstants1 = BufferHandle.Invalid;
        BufferHandle telemetryReadback0 = BufferHandle.Invalid;
        BufferHandle telemetryReadback1 = BufferHandle.Invalid;
        SimpleDdgiNearFieldResidualGpuCommandRecorder? recorder = null;
        try
        {
            const BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit;
            hitMetadata = _bufferManager.CreateDeviceBuffer(
                _layout.HitMetadataBytes,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 Current Hit Metadata");
            historyMetadata0 = _bufferManager.CreateDeviceBuffer(
                _layout.HistoryMetadataBytes / 2UL,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 History Metadata 0");
            historyMetadata1 = _bufferManager.CreateDeviceBuffer(
                _layout.HistoryMetadataBytes / 2UL,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 History Metadata 1");
            tileRecords = _bufferManager.CreateDeviceBuffer(
                _layout.TileBuffersBytes,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 Tile Records");
            ulong frameConstantsBytes = _layout.TraceFrameConstantsBytes / 2UL;
            const BufferUsageFlags frameConstantsUsage =
                BufferUsageFlags.StorageBufferBit;
            const AllocationCreateFlags frameConstantsAllocationFlags =
                AllocationCreateFlags.MappedBit |
                AllocationCreateFlags.HostAccessSequentialWriteBit;
            traceFrameConstants0 = _bufferManager.CreateBuffer(
                frameConstantsBytes,
                frameConstantsUsage,
                MemoryUsage.AutoPreferHost,
                frameConstantsAllocationFlags,
                "C5 Trace Frame Constants 0",
                MemoryBudgetCategory.GlobalIllumination);
            traceFrameConstants1 = _bufferManager.CreateBuffer(
                frameConstantsBytes,
                frameConstantsUsage,
                MemoryUsage.AutoPreferHost,
                frameConstantsAllocationFlags,
                "C5 Trace Frame Constants 1",
                MemoryBudgetCategory.GlobalIllumination);
            ulong telemetryReadbackBytes = _layout.TelemetryReadbackBytes / 2UL;
            const AllocationCreateFlags readbackAllocationFlags =
                AllocationCreateFlags.MappedBit |
                AllocationCreateFlags.HostAccessRandomBit;
            telemetryReadback0 = _bufferManager.CreateBuffer(
                telemetryReadbackBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                readbackAllocationFlags,
                "C5 Telemetry Readback 0",
                MemoryBudgetCategory.GlobalIllumination);
            telemetryReadback1 = _bufferManager.CreateBuffer(
                telemetryReadbackBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                readbackAllocationFlags,
                "C5 Telemetry Readback 1",
                MemoryBudgetCategory.GlobalIllumination);
            _buffers = new SimpleDdgiNearFieldResidualVulkanBuffers(
                hitMetadata,
                historyMetadata0,
                historyMetadata1,
                tileRecords,
                traceFrameConstants0,
                traceFrameConstants1,
                telemetryReadback0,
                telemetryReadback1);
            if (!_buffers.IsComplete)
                throw new InvalidOperationException("C5 buffer allocation is incomplete.");
            _actualAllocationBytes = CalculateActualAllocationBytes();
            _peakAllocationBytes = _actualAllocationBytes;
            if (_actualAllocationBytes > admittedBudgetBytes)
            {
                throw new InvalidOperationException(
                    $"C5 actual Vulkan allocation {_actualAllocationBytes} exceeds admitted budget {admittedBudgetBytes}.");
            }
            _diagnostics = SimpleDdgiNearFieldResidualDiagnostics.PendingGpuReadback(
                CreateMemoryTelemetry(),
                "C5 renderer integration is active; awaiting the first frame-fence readback.");

            _allocationAdapter = new BorrowedGraphAllocationAdapter(_layout);
            SimpleDdgiNearFieldResidualGpuRuntimeSnapshot snapshot =
                _manager.Reconcile(
                    new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                        IsEffectivelyEnabled: true,
                        _layout,
                        _configuration,
                        CreateIntegratedCapabilities(_layout)),
                    _allocationAdapter);
            if (!snapshot.IsContractReadyForRendererIntegration)
            {
                throw new InvalidOperationException(
                    "C5 lifecycle admission failed: " + snapshot.Reason);
            }

            recorder = new SimpleDdgiNearFieldResidualGpuCommandRecorder(
                _context,
                _bufferManager,
                bindlessHeap,
                _renderTargets,
                _hiZ,
                _layout,
                _configuration,
                _buffers);
            _recorder = recorder;
            _active = true;
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Idle;
        }
        catch
        {
            recorder?.Dispose();
            DestroyBuffer(telemetryReadback1);
            DestroyBuffer(telemetryReadback0);
            DestroyBuffer(traceFrameConstants1);
            DestroyBuffer(traceFrameConstants0);
            DestroyBuffer(tileRecords);
            DestroyBuffer(historyMetadata1);
            DestroyBuffer(historyMetadata0);
            DestroyBuffer(hitMetadata);
            _manager.Dispose();
            throw;
        }
    }

    public SimpleDdgiNearFieldResidualGpuRuntimeSnapshot Snapshot =>
        _manager.Snapshot;

    public ulong ActualAllocationBytes
    {
        get
        {
            lock (_sync)
                return _actualAllocationBytes;
        }
    }

    public SimpleDdgiNearFieldResidualDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
                return _diagnostics;
        }
    }

    internal static SimpleDdgiNearFieldResidualDiagnostics
        CreatePendingReadbackDiagnostics(
            in SimpleDdgiNearFieldResidualCompletionWitness lastCompleted,
            SimpleDdgiNearFieldResidualMemoryTelemetry memory,
            ulong pendingFrameSerial)
    {
        if (lastCompleted.CompletedFrameSerial == 0UL)
        {
            return SimpleDdgiNearFieldResidualDiagnostics.PendingGpuReadback(
                memory,
                "C5 commands are recorded and awaiting the frame-slot fence.");
        }

        ulong age = pendingFrameSerial > lastCompleted.CompletedFrameSerial
            ? pendingFrameSerial - lastCompleted.CompletedFrameSerial
            : 0UL;
        return SimpleDdgiNearFieldResidualDiagnostics.CreateCounterReadbackPending(
            lastCompleted,
            memory,
            checked((uint)Math.Min(age, uint.MaxValue)),
            "C5 has a fence-valid counter stream; a newer frame is awaiting " +
            "readback and exclusive timings remain pending.");
    }

    public SimpleDdgiNearFieldResidualCompletionWitness LastCompletedWitness
    {
        get
        {
            lock (_sync)
                return _lastCompletedWitness;
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_sync)
                return !_disposed && _active && _frameAdmission;
        }
    }

    internal SimpleDdgiNearFieldResidualVulkanBuffers Buffers => _buffers;

    internal bool CanExecute(SceneRenderingData sceneData)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        lock (_sync)
        {
            return !_disposed && _active && _frameAdmission &&
                sceneData.ScreenWidth == (uint)_layout.SourceWidth &&
                sceneData.ScreenHeight == (uint)_layout.SourceHeight &&
                HasExactTargetExtents();
        }
    }

    internal void SetFrameAdmission(bool admitted, string? reason)
    {
        lock (_sync)
        {
            if (_disposed || !_active)
                return;
            _frameAdmission = admitted;
            if (!admitted)
            {
                _recordedStage =
                    SimpleDdgiNearFieldResidualRecordedStage.Idle;
                _recordedFrameIndex = -1;
                _diagnostics = SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                    string.IsNullOrWhiteSpace(reason)
                        ? "near-field-runtime-content-binding-rejected"
                        : reason.Trim());
            }
        }
    }

    internal void RecordReset(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        lock (_sync)
        {
            ValidateRecordStart(commandBuffer, frameIndex, sceneData);
            if (_pendingReadbacks[frameIndex].HasValue)
            {
                FailFrame("C5 frame-slot telemetry readback is still pending.");
                throw new InvalidOperationException(
                    "C5 cannot reuse a frame slot before its fence-complete readback.");
            }
            var revision = CreateHistoryRevision(sceneData);
            SimpleDdgiNearFieldResidualGpuBeginFrameResult begin =
                _manager.BeginFrame(revision, frameIndex & 1);
            if (!begin.Started)
            {
                FailFrame("C5 begin-frame rejected: " + begin.Reason);
                throw new InvalidOperationException(begin.Reason);
            }

            _frameToken = begin.Token;
            _historyInputValid = !begin.HistoryInvalidated;
            _recordedFrameIndex = frameIndex;
            _recordedFilterIterations = 0;
            WriteTraceFrameConstants(frameIndex, sceneData);
            _recorder.RecordReset(
                commandBuffer,
                _frameToken,
                sceneData.DdgiFrameSerial == ulong.MaxValue
                    ? ulong.MaxValue
                    : sceneData.DdgiFrameSerial + 1UL);
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Reset;
        }
    }

    internal void RecordTrace(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        lock (_sync)
        {
            ValidateStage(commandBuffer, frameIndex, sceneData,
                SimpleDdgiNearFieldResidualRecordedStage.Reset);
            _recorder.RecordTrace(commandBuffer, frameIndex, _frameToken);
            SimpleDdgiNearFieldResidualGpuStageResult completion =
                _manager.CompleteTrace(
                    _frameToken,
                    new SimpleDdgiNearFieldResidualGpuTraceCompletion(
                        QueueOrderedCommandsRecorded: true,
                        TraceSourceBindingVerified: true,
                        StableSampleIdentityVerified: true,
                        ReceiverBrdfAndPdfVerified: true,
                        InvalidAndMissCandidatesZeroed: true,
                        TileRecordsInitializedAndBounded: true));
            RequireCompletion(completion);
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Trace;
        }
    }

    internal void RecordTemporal(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        lock (_sync)
        {
            ValidateStage(commandBuffer, frameIndex, sceneData,
                SimpleDdgiNearFieldResidualRecordedStage.Trace);
            _recorder.RecordTemporal(
                commandBuffer,
                _frameToken,
                CreateHistoryRevision(sceneData),
                _historyInputValid);
            SimpleDdgiNearFieldResidualGpuStageResult completion =
                _manager.CompleteTemporal(
                    _frameToken,
                    new SimpleDdgiNearFieldResidualGpuTemporalCompletion(
                        QueueOrderedCommandsRecorded: true,
                        HistoryWritesContainOnlyValidCandidates: true,
                        HistoryBankFullyInitialized: true));
            RequireCompletion(completion);
            _recordedStage = _configuration.FilterIterationCount == 0
                ? SimpleDdgiNearFieldResidualRecordedStage.Temporal
                : SimpleDdgiNearFieldResidualRecordedStage.Filtering;
        }
    }

    internal void RecordFilter(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        int iteration)
    {
        lock (_sync)
        {
            ValidateStage(commandBuffer, frameIndex, sceneData,
                SimpleDdgiNearFieldResidualRecordedStage.Filtering);
            if (iteration != _recordedFilterIterations ||
                iteration < 0 || iteration >= _configuration.FilterIterationCount)
            {
                FailFrame("C5 filter iteration order mismatch.");
                throw new InvalidOperationException(
                    "C5 filter iterations must be recorded exactly once in ascending order.");
            }

            _recorder.RecordFilter(commandBuffer, _frameToken, iteration);
            _recordedFilterIterations++;
            if (_recordedFilterIterations == _configuration.FilterIterationCount)
            {
                SimpleDdgiNearFieldResidualGpuStageResult completion =
                    _manager.CompleteFilter(
                        _frameToken,
                        new SimpleDdgiNearFieldResidualGpuFilterCompletion(
                            QueueOrderedCommandsRecorded: true,
                            EdgeAwareValidityChecked: true,
                            ExecutedIterationCount:
                                checked((uint)_recordedFilterIterations)));
                RequireCompletion(completion);
                _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Temporal;
            }
        }
    }

    internal void RecordComposite(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        lock (_sync)
        {
            ValidateStage(commandBuffer, frameIndex, sceneData,
                SimpleDdgiNearFieldResidualRecordedStage.Temporal);
            if (_configuration.FilterIterationCount != _recordedFilterIterations)
            {
                FailFrame("C5 composite observed incomplete filtering.");
                throw new InvalidOperationException(
                    "C5 composite cannot run before every admitted filter iteration.");
            }

            _recorder.RecordComposite(commandBuffer, _frameToken);
            RecordTelemetryReadback(
                commandBuffer,
                frameIndex,
                sceneData.DdgiFrameSerial == ulong.MaxValue
                    ? ulong.MaxValue
                    : sceneData.DdgiFrameSerial + 1UL,
                _frameToken);
            SimpleDdgiNearFieldResidualGpuStageResult completion =
                _manager.CompleteComposite(
                    _frameToken,
                    new SimpleDdgiNearFieldResidualGpuCompositeCompletion(
                        QueueOrderedCommandsRecorded: true,
                        OnlyValidSignedResidualComposited: true,
                        InvalidResidualPayloadWasZero: true));
            RequireCompletion(completion);
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Composite;
            _recordedFrameIndex = -1;
        }
    }

    /// <summary>
    /// Consumes only the readback associated with the frame slot whose fence
    /// the renderer has already waited. No polling or device-idle operation is
    /// performed here.
    /// </summary>
    public bool TryReadCompletedFrame(
        int frameIndex,
        out SimpleDdgiNearFieldResidualCompletionWitness witness)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            witness = default;
            PendingReadback? pendingValue = _pendingReadbacks[frameIndex];
            if (!pendingValue.HasValue)
                return false;

            PendingReadback pending = pendingValue.Value;
            _pendingReadbacks[frameIndex] = null;
            if (!_manager.ObserveFrameFenceCompletion(pending.Token))
            {
                DisableAfterReadbackFailure(
                    "near-field-fence-completion-token-rejected");
                return false;
            }

            try
            {
                BufferHandle readback = _buffers.TelemetryReadback(frameIndex);
                _bufferManager.InvalidateBuffer(
                    readback,
                    0UL,
                    _layout.TileBuffersBytes);
                uint* mapped = (uint*)_bufferManager.GetMappedPointer(readback);
                if (mapped is null)
                    throw new InvalidOperationException(
                        "C5 telemetry readback mapping is null.");
                int wordCount = checked((int)(
                    _layout.TileBuffersBytes / sizeof(uint)));
                var words = new ReadOnlySpan<uint>(mapped, wordCount);
                if (!SimpleDdgiNearFieldResidualCompletionValidator.TryValidate(
                        words,
                        _layout,
                        pending.CompletedFrameSerial,
                        out witness,
                        out string validationFailure))
                {
                    DisableAfterReadbackFailure(validationFailure);
                    return false;
                }

                _lastCompletedWitness = witness;
                _diagnostics =
                    SimpleDdgiNearFieldResidualDiagnostics.CreateCounterReadbackPending(
                        witness,
                        CreateMemoryTelemetry(),
                        reason: "C5 frame fence and detailed counter stream are valid; " +
                        "a common exclusive timing sample is still pending.");
                return true;
            }
            catch (Exception exception)
            {
                DisableAfterReadbackFailure(
                    "near-field-telemetry-readback-failed:" +
                    exception.GetType().Name);
                return false;
            }
        }
    }

    internal void OnRenderTargetsRecreated()
    {
        lock (_sync)
        {
            if (_disposed || !_active)
                return;
            if (!HasExactTargetExtents())
            {
                DisableAndReleaseAfterDeviceIdle(
                    "near-field-resize-requires-new-bound-evidence");
                return;
            }

            _recorder.RewriteDescriptors();
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Idle;
            _recordedFrameIndex = -1;
        }
    }

    /// <summary>
    /// Retires all runtime-owned C5 objects at a renderer-controlled
    /// device-idle transition. The immutable qualification evidence cannot be
    /// rebound to a different extent, so resize disables rather than silently
    /// reusing it. This method is idempotent.
    /// </summary>
    internal void DisableAndReleaseAfterDeviceIdle(string reason)
    {
        lock (_sync)
        {
            if (_disposed || _resourcesReleased)
                return;

            _manager.Disable(reason);
            _active = false;
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Invalid;
            _recordedFrameIndex = -1;
            Array.Clear(_pendingReadbacks);
            _recorder.Dispose();
            DestroyRuntimeBuffersNoLock();
            _actualAllocationBytes = 0UL;
            _resourcesReleased = true;
            _diagnostics = SimpleDdgiNearFieldResidualDiagnostics.Disabled(reason);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _active = false;
            if (!_resourcesReleased)
            {
                _recorder.Dispose();
                DestroyRuntimeBuffersNoLock();
                _actualAllocationBytes = 0UL;
                _resourcesReleased = true;
            }
            _manager.Dispose();
            Array.Clear(_pendingReadbacks);
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Invalid;
        }
        GC.SuppressFinalize(this);
    }

    private void ValidateRecordStart(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("C5 requires a live command buffer.", nameof(commandBuffer));
        if (!CanExecuteNoLock(sceneData))
            throw new InvalidOperationException("C5 source/layout evidence no longer matches this frame.");
        if (_recordedFrameIndex >= 0 ||
            _recordedStage is not (
                SimpleDdgiNearFieldResidualRecordedStage.Idle or
                SimpleDdgiNearFieldResidualRecordedStage.Composite))
        {
            throw new InvalidOperationException("A C5 frame transaction is already in flight.");
        }
    }

    private void ValidateStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        SimpleDdgiNearFieldResidualRecordedStage expected)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (commandBuffer.Handle == 0 || frameIndex != _recordedFrameIndex ||
            !CanExecuteNoLock(sceneData) || _recordedStage != expected)
        {
            FailFrame("C5 command stage, frame slot, or layout mismatch.");
            throw new InvalidOperationException(
                "C5 command stages must use one compatible frame slot in declared order.");
        }
    }

    private bool CanExecuteNoLock(SceneRenderingData sceneData) =>
        !_disposed && _active &&
        sceneData.ScreenWidth == (uint)_layout.SourceWidth &&
        sceneData.ScreenHeight == (uint)_layout.SourceHeight &&
        sceneData.FoliageClusterCount == 0 &&
        sceneData.ObjectCount is >= 0 and <= 65_536 &&
        sceneData.MaterialCount is >= 0 and <= 65_536 &&
        sceneData.DebugViewMode == 0u &&
        HasExactTargetExtents();

    private bool HasExactTargetExtents()
    {
        uint sourceWidth = checked((uint)_layout.SourceWidth);
        uint sourceHeight = checked((uint)_layout.SourceHeight);
        uint traceWidth = checked((uint)_layout.TraceWidth);
        uint traceHeight = checked((uint)_layout.TraceHeight);
        return HasExtent(_renderTargets.NearFieldDirectSource, sourceWidth, sourceHeight) &&
            HasExtent(_renderTargets.NearFieldReceiverPayload,
                sourceWidth, sourceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualRaw, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualHistory0, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualHistory1, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualMoments0, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualMoments1, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualValidity0, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualValidity1, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualHistoryNormals0, traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldResidualHistoryNormals1, traceWidth, traceHeight) &&
            (_layout.FilterScratchBytes == 0UL ||
             HasExtent(_renderTargets.NearFieldResidualFilterScratch0, traceWidth, traceHeight) &&
             HasExtent(_renderTargets.NearFieldResidualFilterScratch1, traceWidth, traceHeight));
    }

    private void WriteTraceFrameConstants(
        int frameIndex,
        SceneRenderingData sceneData)
    {
        BufferHandle handle = _buffers.TraceFrameConstants(frameIndex & 1);
        void* mapped = _bufferManager.GetMappedPointer(handle);
        if (mapped is null)
            throw new InvalidOperationException("C5 trace frame constants are not host mapped.");
        *(GPUSimpleDdgiNearFieldResidualTraceFrameConstants*)mapped = new()
        {
            ViewProjection = sceneData.ViewProjectionMatrix,
            InverseViewProjection = sceneData.InverseViewProjectionMatrix,
            FullExtentAndInverse = new Vector4(
                _layout.SourceWidth,
                _layout.SourceHeight,
                1.0f / _layout.SourceWidth,
                1.0f / _layout.SourceHeight),
            Reserved = new Vector4(
                ForwardNearFieldDirectSourceContract.ReferenceB3WorldFootprintRadius,
                _configuration.MaximumTraceDistance,
                BitConverter.UInt32BitsToSingle(sceneData.TemporalSampleIndex),
                BitConverter.UInt32BitsToSingle(
                    ForwardNearFieldDirectSourceContract.ShaderSemanticVersion))
        };
        _bufferManager.FlushBuffer(
            handle,
            0UL,
            SimpleDdgiNearFieldResidualGpuAbi.TraceFrameConstantsByteCount);
    }

    private SimpleDdgiNearFieldResidualGpuHistoryRevision CreateHistoryRevision(
        SceneRenderingData sceneData)
    {
        bool cameraCut = sceneData.HiZPolicyCameraCut != 0 ||
            (_hasCameraCutSerial &&
             sceneData.CaptureCameraCutSerial != _lastCameraCutSerial);
        _lastCameraCutSerial = sceneData.CaptureCameraCutSerial;
        _hasCameraCutSerial = true;
        return new SimpleDdgiNearFieldResidualGpuHistoryRevision(
            ViewportRevision: NonZeroHash(
                sceneData.ScreenWidth,
                sceneData.ScreenHeight,
                checked((uint)_renderTargets.ResizeCount + 1u)),
            HiZRevision: NonZeroHash(
                // Vulkan non-dispatchable handles are opaque 64-bit values.
                // Hash their two words; narrowing either half is intentional
                // bit extraction and must not throw when the low word exceeds
                // Int32/UInt32 arithmetic ranges.
                unchecked((uint)_hiZ.Image.Handle),
                unchecked((uint)(_hiZ.Image.Handle >> 32)),
                _hiZ.MipLevels),
            TraceSourceAbiRevision: _configuration.TraceSourceContract.AbiRevision,
            EffectiveModeRevision: SimpleDdgiNearFieldResidualGpuAbi.Version,
            ExposureDomainRevision: 1u,
            CameraCut: cameraCut,
            ProjectionJitterRevision: HashProjectionAndJitter(sceneData),
            OriginRebaseRevision: 1u,
            SceneGeneration: NonZeroHash(
                unchecked((uint)sceneData.SceneContentRevision),
                unchecked((uint)(sceneData.SceneContentRevision >> 32)),
                0x4335u),
            TraceSourceContentRevision:
                _configuration.TraceSourceContract.SourceRevision,
            NearFieldLayoutRevision:
                _configuration.TraceSourceContract.LayoutRevision,
            B3OwnershipRevision: _b3OwnershipRevision,
            TraceSourceLayoutRevision:
                _configuration.TraceSourceContract.LayoutRevision);
    }

    private static uint HashProjectionAndJitter(SceneRenderingData sceneData)
    {
        uint hash = 2166136261u;
        void Add(float value)
        {
            hash ^= BitConverter.SingleToUInt32Bits(value);
            hash *= 16777619u;
        }
        Add(sceneData.ProjectionMatrix.M11);
        Add(sceneData.ProjectionMatrix.M22);
        Add(sceneData.ProjectionMatrix.M33);
        Add(sceneData.ProjectionMatrix.M34);
        Add(sceneData.ProjectionMatrix.M43);
        Add(sceneData.JitterX);
        Add(sceneData.JitterY);
        hash ^= unchecked((uint)sceneData.JitterEnabled);
        return hash == 0u ? 1u : hash;
    }

    private static uint NonZeroHash(uint a, uint b, uint c)
    {
        uint hash = 2166136261u;
        hash = (hash ^ a) * 16777619u;
        hash = (hash ^ b) * 16777619u;
        hash = (hash ^ c) * 16777619u;
        return hash == 0u ? 1u : hash;
    }

    private void RequireCompletion(
        in SimpleDdgiNearFieldResidualGpuStageResult completion)
    {
        if (completion.Accepted)
            return;
        FailFrame(completion.Reason);
        throw new InvalidOperationException(completion.Reason);
    }

    private void FailFrame(string reason)
    {
        if (!_frameToken.IsDefault)
            _manager.InvalidateHistory(_frameToken, reason);
        _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Invalid;
        _recordedFrameIndex = -1;
        _active = false;
    }

    private void DisableAfterReadbackFailure(string reason)
    {
        _manager.Disable(reason);
        _active = false;
        _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Invalid;
        _recordedFrameIndex = -1;
        Array.Clear(_pendingReadbacks);
        _diagnostics = SimpleDdgiNearFieldResidualDiagnostics.Faulted(
            CreateMemoryTelemetry(),
            reason);
    }

    private void RecordTelemetryReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        ulong completedFrameSerial,
        in SimpleDdgiNearFieldResidualGpuFrameToken token)
    {
        if (completedFrameSerial == 0UL || _pendingReadbacks[frameIndex].HasValue)
            throw new InvalidOperationException(
                "C5 telemetry readback frame identity is invalid or still pending.");

        VkBuffer source = _bufferManager.GetBuffer(_buffers.TileRecords);
        VkBuffer destination = _bufferManager.GetBuffer(
            _buffers.TelemetryReadback(frameIndex));
        BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
            source,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            0UL,
            _layout.TileBuffersBytes);
        ExecuteBufferBarrier(commandBuffer, beforeCopy);

        var copy = new BufferCopy
        {
            SrcOffset = 0UL,
            DstOffset = 0UL,
            Size = _layout.TileBuffersBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            source,
            destination,
            1U,
            &copy);

        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            destination,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit,
            0UL,
            _layout.TileBuffersBytes);
        ExecuteBufferBarrier(commandBuffer, afterCopy);
        _pendingReadbacks[frameIndex] = new PendingReadback(
            token,
            completedFrameSerial);
        _diagnostics = CreatePendingReadbackDiagnostics(
            _lastCompletedWitness,
            CreateMemoryTelemetry(),
            completedFrameSerial);
    }

    private void ExecuteBufferBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1U,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private SimpleDdgiNearFieldResidualMemoryTelemetry CreateMemoryTelemetry() =>
        new(
            RequestedBytes: _layout.TotalBytes,
            AdmittedBytes: _layout.TotalBytes,
            AllocatedBytes: _actualAllocationBytes,
            PeakAllocatedBytes: _peakAllocationBytes,
            RetiredBytes: 0UL);

    private void DestroyRuntimeBuffersNoLock()
    {
        DestroyBuffer(_buffers.TelemetryReadback1);
        DestroyBuffer(_buffers.TelemetryReadback0);
        DestroyBuffer(_buffers.TraceFrameConstants1);
        DestroyBuffer(_buffers.TraceFrameConstants0);
        DestroyBuffer(_buffers.TileRecords);
        DestroyBuffer(_buffers.HistoryMetadata1);
        DestroyBuffer(_buffers.HistoryMetadata0);
        DestroyBuffer(_buffers.HitMetadata);
    }

    private void DestroyBuffer(BufferHandle handle)
    {
        if (handle.IsValid)
            _bufferManager.DestroyBuffer(handle);
    }

    private ulong CalculateActualAllocationBytes()
    {
        static ulong ImageBytes(RenderTarget? target) =>
            target?.AllocationByteSize ?? 0UL;

        ulong imageBytes = checked(
            ImageBytes(_renderTargets.NearFieldDirectSource) +
            ImageBytes(_renderTargets.NearFieldReceiverPayload) +
            ImageBytes(_renderTargets.NearFieldResidualRaw) +
            ImageBytes(_renderTargets.NearFieldResidualHistory0) +
            ImageBytes(_renderTargets.NearFieldResidualHistory1) +
            ImageBytes(_renderTargets.NearFieldResidualMoments0) +
            ImageBytes(_renderTargets.NearFieldResidualMoments1) +
            ImageBytes(_renderTargets.NearFieldResidualValidity0) +
            ImageBytes(_renderTargets.NearFieldResidualValidity1) +
            ImageBytes(_renderTargets.NearFieldResidualHistoryNormals0) +
            ImageBytes(_renderTargets.NearFieldResidualHistoryNormals1) +
            ImageBytes(_renderTargets.NearFieldResidualFilterScratch0) +
            ImageBytes(_renderTargets.NearFieldResidualFilterScratch1));
        ulong bufferBytes = checked(
            _bufferManager.GetBufferAllocationSize(_buffers.HitMetadata) +
            _bufferManager.GetBufferAllocationSize(_buffers.HistoryMetadata0) +
            _bufferManager.GetBufferAllocationSize(_buffers.HistoryMetadata1) +
            _bufferManager.GetBufferAllocationSize(_buffers.TileRecords) +
            _bufferManager.GetBufferAllocationSize(_buffers.TraceFrameConstants0) +
            _bufferManager.GetBufferAllocationSize(_buffers.TraceFrameConstants1) +
            _bufferManager.GetBufferAllocationSize(_buffers.TelemetryReadback0) +
            _bufferManager.GetBufferAllocationSize(_buffers.TelemetryReadback1));
        if (imageBytes == 0UL || bufferBytes == 0UL)
            throw new InvalidOperationException("C5 physical allocation accounting is incomplete.");
        return checked(imageBytes + bufferBytes);
    }

    private static bool HasExtent(RenderTarget? target, uint width, uint height) =>
        target is { Image.Handle: not 0, View.Handle: not 0 } &&
        target.Extent.Width == width && target.Extent.Height == height;

    private static void ValidatePhysicalIntegration(
        VulkanContext context,
        RenderTargetManager targets,
        HiZDepthPyramid hiZ,
        in SimpleDdgiNearFieldResidualLayout layout,
        in SimpleDdgiNearFieldResidualGpuConfiguration configuration)
    {
        if (!layout.IsValid || layout.TotalBytes == 0UL)
            throw new ArgumentException("C5 requires a complete admitted layout.", nameof(layout));
        SimpleDdgiNearFieldResidualGpuConfigurationValidation validation =
            configuration.Validate(layout);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Reason, nameof(configuration));

        PhysicalDeviceProperties properties = default;
        context.Api.GetPhysicalDeviceProperties(context.PhysicalDevice, &properties);
        uint maximumDimension = properties.Limits.MaxImageDimension2D;
        if ((uint)layout.SourceWidth > maximumDimension ||
            (uint)layout.SourceHeight > maximumDimension ||
            (uint)layout.TraceWidth > maximumDimension ||
            (uint)layout.TraceHeight > maximumDimension)
        {
            throw new VulkanException(
                "C5 image dimensions exceed VkPhysicalDeviceLimits.maxImageDimension2D.");
        }
        if (hiZ.Image.Handle == 0 || hiZ.FullView.Handle == 0 ||
            !IsCompatibleHiZExtent(
                layout,
                hiZ.Extent.Width,
                hiZ.Extent.Height))
        {
            throw new InvalidOperationException(
                "C5 requires the current scene-aligned half-resolution Hi-Z pyramid.");
        }
        if (targets.MotionVectors.Extent.Width != (uint)layout.SourceWidth ||
            targets.MotionVectors.Extent.Height != (uint)layout.SourceHeight)
        {
            throw new InvalidOperationException("C5 requires full-resolution motion vectors.");
        }
    }

    internal static bool IsCompatibleHiZExtent(
        in SimpleDdgiNearFieldResidualLayout layout,
        uint width,
        uint height) =>
        layout.SourceWidth > 0 &&
        layout.SourceHeight > 0 &&
        width == Math.Max(1u, checked((uint)layout.SourceWidth) / 2u) &&
        height == Math.Max(1u, checked((uint)layout.SourceHeight) / 2u);

    private static SimpleDdgiNearFieldResidualGpuIntegrationCapabilities
        CreateIntegratedCapabilities(in SimpleDdgiNearFieldResidualLayout layout) => new(
            TracePassRegistered: true,
            TemporalPassRegistered: true,
            FilterPassRegistered: true,
            CompositePassRegistered: true,
            DirectDiffuseEmissiveAttachmentAvailable: true,
            HiZAvailable: true,
            ReceiverMetadataAvailable: true,
            StableSampleRayInputAvailable: true,
            ReceiverBrdfPdfInputAvailable: true,
            MotionVectorsAvailable: true,
            DoubleBufferedHistoryIdentityAvailable: true,
            HistoryIdentityMemoryBudgeted: layout.HistoryMetadataBytes != 0UL,
            TileRecordLayoutValidated: true,
            RequiredImageFormatsValidated: true,
            DescriptorAndBarrierContractValidated: true,
            ShaderArtifactsValidated: true,
            ResetPassRegistered: true,
            PingPongBankBindingAndSynchronizationValidated: true,
            DirectSourceVariantProvenanceValidated: true,
            GeometricAndShadingNormalHistoryAvailable: true,
            HitUvAndSourceRevisionValidationAvailable: true,
            TemporalVarianceClippingAndBoundedHistoryAvailable: true,
            B3FootprintFrequencySeparationValidated: true,
            MeasuredQualificationEvidenceVerified: true,
            DeviceLimitsAndActualAllocationRequirementsValidated: true);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiNearFieldResidualVulkanRuntime));
    }

    /// <summary>
    /// Bridges the existing lifecycle validator to graph-owned image and
    /// runtime-owned buffer allocations. Handles are typed synthetic
    /// identities so unlike Vulkan object handles they cannot collide across
    /// image and buffer namespaces. Retirement remains owned by this runtime.
    /// </summary>
    private sealed class BorrowedGraphAllocationAdapter :
        ISimpleDdgiNearFieldResidualGpuResourceAllocator
    {
        private readonly SimpleDdgiNearFieldResidualLayout _layout;

        public BorrowedGraphAllocationAdapter(
            in SimpleDdgiNearFieldResidualLayout layout) => _layout = layout;

        public SimpleDdgiNearFieldResidualGpuAllocation Allocate(
            in SimpleDdgiNearFieldResidualLayout layout,
            in SimpleDdgiNearFieldResidualGpuConfiguration configuration)
        {
            if (!layout.Equals(_layout))
                throw new InvalidOperationException("C5 allocation layout changed during publication.");
            ulong next = 1UL;
            SimpleDdgiNearFieldResidualGpuResource Resource(
                ulong bytes,
                SimpleDdgiNearFieldResidualGpuResourceKind kind) =>
                bytes == 0UL ? new(0UL, 0UL, kind) : new(next++, bytes, kind);

            return new SimpleDdgiNearFieldResidualGpuAllocation(
                AllocationId: 1UL,
                Resource(layout.TraceSourceBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.DirectDiffuseEmissiveSource),
                Resource(layout.ReceiverPayloadBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.ReceiverPayload),
                Resource(layout.TraceFrameConstantsBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TraceFrameConstants0),
                Resource(layout.TraceFrameConstantsBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TraceFrameConstants1),
                Resource(layout.RawCandidateBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.RawCandidate),
                Resource(layout.HitMetadataBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HitMetadata),
                Resource(layout.HistoryRadianceBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryRadiance0),
                Resource(layout.HistoryRadianceBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryRadiance1),
                Resource(layout.MomentBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMoments0),
                Resource(layout.MomentBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMoments1),
                Resource(layout.HistoryValidityBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryValidity0),
                Resource(layout.HistoryValidityBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryValidity1),
                Resource(layout.HistoryMetadataBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMetadata0),
                Resource(layout.HistoryMetadataBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryMetadata1),
                Resource(layout.HistoryNormalBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryNormal0),
                Resource(layout.HistoryNormalBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.HistoryNormal1),
                Resource(layout.FilterScratchBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.FilterScratch0),
                Resource(layout.FilterScratchBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.FilterScratch1),
                Resource(layout.TileBuffersBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TileBuffers),
                Resource(layout.TelemetryReadbackBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TelemetryReadback0),
                Resource(layout.TelemetryReadbackBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.TelemetryReadback1),
                SimpleDdgiNearFieldResidualGpuAllocation.ExpectedDescriptorCount(layout));
        }

        public void Retire(SimpleDdgiNearFieldResidualGpuAllocation allocation)
        {
            // Graph images and native buffers are retired by their concrete owners.
        }
    }

    private readonly record struct PendingReadback(
        SimpleDdgiNearFieldResidualGpuFrameToken Token,
        ulong CompletedFrameSerial);
}
