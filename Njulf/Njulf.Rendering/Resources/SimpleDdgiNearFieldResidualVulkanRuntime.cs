using System;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
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
    BufferHandle HistoryMetadata0,
    BufferHandle HistoryMetadata1,
    BufferHandle SurfaceTable,
    BufferHandle ActiveTileAndIndirect,
    BufferHandle SchedulerHistory0,
    BufferHandle SchedulerHistory1,
    BufferHandle TileRecords,
    BufferHandle TraceFrameConstants0,
    BufferHandle TraceFrameConstants1,
    BufferHandle TelemetryReadback0,
    BufferHandle TelemetryReadback1)
{
    public bool IsComplete => HistoryMetadata0.IsValid && HistoryMetadata1.IsValid &&
        SurfaceTable.IsValid && ActiveTileAndIndirect.IsValid &&
        SchedulerHistory0.IsValid && SchedulerHistory1.IsValid &&
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

    public BufferHandle SchedulerHistory(int index) => index switch
    {
        0 => SchedulerHistory0,
        1 => SchedulerHistory1,
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
    Prepare = 2,
    Classify = 3,
    Trace = 4,
    Temporal = 5,
    Finalize = 6,
    Filtering = 7,
    FrequencySeparation = 8,
    Composite = 9,
    Invalid = 10
}

/// <summary>
/// Immutable view of the shared scene attachments and one complete C5 image
/// bank. A pending generation can build descriptors against this view without
/// publishing its images through <see cref="RenderTargetManager"/> first.
/// </summary>
internal sealed class SimpleDdgiNearFieldResidualTargetBinding
{
    private readonly RenderTargetManager _sharedTargets;
    private readonly SimpleDdgiNearFieldResidualRenderTargetGeneration
        _generation;

    public SimpleDdgiNearFieldResidualTargetBinding(
        RenderTargetManager sharedTargets,
        SimpleDdgiNearFieldResidualRenderTargetGeneration generation)
    {
        _sharedTargets = sharedTargets ??
            throw new ArgumentNullException(nameof(sharedTargets));
        _generation = generation ??
            throw new ArgumentNullException(nameof(generation));
    }

    public RenderTarget SceneColor => _sharedTargets.SceneColor;
    public RenderTarget SceneDepth => _sharedTargets.SceneDepth;
    public RenderTarget MotionVectors => _sharedTargets.MotionVectors;
    public int ResizeCount => _sharedTargets.ResizeCount;

    public RenderTarget NearFieldDirectSource => _generation.DirectSource;
    public RenderTarget NearFieldReceiverPayload => _generation.ReceiverPayload;
    public RenderTarget NearFieldResidualRaw => _generation.RawResidual;
    public RenderTarget NearFieldPreparedDepthFootprint =>
        _generation.PreparedDepthFootprint;
    public RenderTarget NearFieldPreparedReceiverPayload =>
        _generation.PreparedReceiverPayload;
    public RenderTarget NearFieldPreparedMotion => _generation.PreparedMotion;
    public RenderTarget NearFieldSourceLuminance =>
        _generation.SourceLuminance;
    public RenderTarget NearFieldResidualHistory0 => _generation.History0;
    public RenderTarget NearFieldResidualHistory1 => _generation.History1;
    public RenderTarget NearFieldResidualMoments0 => _generation.Moments0;
    public RenderTarget NearFieldResidualMoments1 => _generation.Moments1;
    public RenderTarget NearFieldResidualValidity0 => _generation.Validity0;
    public RenderTarget NearFieldResidualValidity1 => _generation.Validity1;
    public RenderTarget NearFieldResidualHistoryNormals0 =>
        _generation.HistoryNormals0;
    public RenderTarget NearFieldResidualHistoryNormals1 =>
        _generation.HistoryNormals1;
    public RenderTarget? NearFieldResidualFilterScratch0 =>
        _generation.FilterScratch0;
    public RenderTarget? NearFieldResidualFilterScratch1 =>
        _generation.FilterScratch1;
}

/// <summary>
/// Concrete C5 Vulkan lifetime and frame-transaction boundary. Images are
/// graph-owned; this object owns the metadata/tile buffers, fixed descriptor
/// sets, pipelines, history parity, and the exact sequence of stage records.
/// It is constructed only after immutable C5 evidence has admitted the graph.
/// </summary>
public sealed unsafe class SimpleDdgiNearFieldResidualVulkanRuntime : IDisposable
{
    internal const uint ConsecutiveTelemetryFailureRebuildThreshold = 3U;

    private readonly object _sync = new();
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly FoliageManager _foliageManager;
    private readonly SimpleDdgiNearFieldResidualTargetBinding _renderTargets;
    private readonly HiZDepthPyramid _hiZ;
    private readonly SimpleDdgiNearFieldResidualLayout _layout;
    private readonly SimpleDdgiNearFieldResidualGpuConfiguration _configuration;
    private readonly uint _b3OwnershipRevision;
    private readonly ulong _calibratedSourceCostUpperBoundMicroseconds;
    private readonly bool _sourceCostAuthoritative;
    private readonly SimpleDdgiNearFieldResidualCaptureIdentifiers
        _captureIdentifiers;
    private readonly SimpleDdgiNearFieldResidualGpuManager _manager = new();
    private readonly BorrowedGraphAllocationAdapter _allocationAdapter;
    private readonly SimpleDdgiNearFieldResidualVulkanBuffers _buffers;
    private readonly SimpleDdgiNearFieldResidualGpuCommandRecorder _recorder;
    private readonly SimpleDdgiNearFieldResidualAdaptiveResolution
        _resolutionGovernor;
    private readonly PendingReadback?[] _pendingReadbacks =
        new PendingReadback?[RenderingConstants.FramesInFlight];

    private SimpleDdgiNearFieldResidualGpuFrameToken _frameToken;
    private SimpleDdgiNearFieldResidualExecutionExtent _executionExtent;
    private SimpleDdgiNearFieldResidualExecutionExtent _recordedExecutionExtent;
    private SimpleDdgiNearFieldResidualRecordedStage _recordedStage;
    private int _recordedFilterIterations;
    private int _recordedFrameIndex = -1;
    private ulong _lastCameraCutSerial;
    private bool _hasCameraCutSerial;
    private bool _historyInputValid;
    private SimpleDdgiNearFieldResidualGpuHistoryRevision
        _recordedHistoryRevision;
    private uint _publishedSourceLightingEpoch;
    private bool _hasPublishedSourceLightingEpoch;
    private Matrix4x4 _previousViewProjection = Matrix4x4.Identity;
    private Matrix4x4 _previousInverseViewProjection = Matrix4x4.Identity;
    private bool _hasPreviousTraceMatrices;
    private SimpleDdgiNearFieldResidualCompletionWitness _lastCompletedWitness;
    private SimpleDdgiNearFieldResidualDiagnostics _diagnostics =
        SimpleDdgiNearFieldResidualDiagnostics.Disabled();
    private ulong _actualAllocationBytes;
    private ulong _peakAllocationBytes;
    private bool _resourcesReleased;
    private bool _active;
    private bool _frameAdmission = true;
    private uint _consecutiveTelemetryFailures;
    private bool _requiresGenerationRebuild;
    private string _lastTelemetryFailureReason = string.Empty;
    private bool _disposed;

    public SimpleDdgiNearFieldResidualVulkanRuntime(
        VulkanContext context,
        BufferManager bufferManager,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        HiZDepthPyramid hiZ,
        FoliageManager foliageManager,
        in SimpleDdgiNearFieldResidualLayout layout,
        in SimpleDdgiNearFieldResidualGpuConfiguration configuration,
        bool adaptiveResolutionEnabled,
        uint b3OwnershipRevision,
        ulong admittedBudgetBytes,
        ulong calibratedSourceCostUpperBoundMicroseconds = 0UL,
        bool sourceCostAuthoritative = false,
        SimpleDdgiNearFieldResidualExecutionScale? startingScale = null,
        bool promotionEnabled = true,
        SimpleDdgiNearFieldResidualCaptureIdentifiers captureIdentifiers =
            default,
        SimpleDdgiNearFieldResidualRenderTargetGeneration? targetGeneration =
            null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _foliageManager = foliageManager ??
            throw new ArgumentNullException(nameof(foliageManager));
        ArgumentNullException.ThrowIfNull(bindlessHeap);
        ArgumentNullException.ThrowIfNull(renderTargets);
        targetGeneration ??=
            renderTargets.CurrentNearFieldResidualGeneration ??
            throw new InvalidOperationException(
                "C5 requires a complete render-target generation.");
        if (targetGeneration.Layout != layout)
        {
            throw new ArgumentException(
                "The C5 render-target generation does not match the admitted layout.",
                nameof(targetGeneration));
        }
        _renderTargets = new SimpleDdgiNearFieldResidualTargetBinding(
            renderTargets,
            targetGeneration);
        _hiZ = hiZ ?? throw new ArgumentNullException(nameof(hiZ));
        _layout = layout;
        _configuration = configuration;
        _resolutionGovernor =
            new SimpleDdgiNearFieldResidualAdaptiveResolution(
                layout.TraceResolutionScale,
                adaptiveResolutionEnabled,
                startingScale,
                promotionEnabled && sourceCostAuthoritative);
        _executionExtent = _resolutionGovernor.CreateExtent(
            layout.SourceWidth,
            layout.SourceHeight);
        _b3OwnershipRevision = b3OwnershipRevision == 0u
            ? throw new ArgumentOutOfRangeException(nameof(b3OwnershipRevision))
            : b3OwnershipRevision;
        _calibratedSourceCostUpperBoundMicroseconds =
            calibratedSourceCostUpperBoundMicroseconds;
        _sourceCostAuthoritative = sourceCostAuthoritative &&
            calibratedSourceCostUpperBoundMicroseconds != 0UL;
        _captureIdentifiers = captureIdentifiers.Normalize();
        if (admittedBudgetBytes == 0UL || layout.TotalBytes > admittedBudgetBytes)
            throw new ArgumentOutOfRangeException(nameof(admittedBudgetBytes));

        ValidatePhysicalIntegration(
            _context,
            _renderTargets,
            _hiZ,
            _layout,
            _configuration);

        BufferHandle historyMetadata0 = BufferHandle.Invalid;
        BufferHandle historyMetadata1 = BufferHandle.Invalid;
        BufferHandle surfaceTable = BufferHandle.Invalid;
        BufferHandle activeTileAndIndirect = BufferHandle.Invalid;
        BufferHandle schedulerHistory0 = BufferHandle.Invalid;
        BufferHandle schedulerHistory1 = BufferHandle.Invalid;
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
            surfaceTable = _bufferManager.CreateDeviceBuffer(
                _layout.SurfaceTableBytes,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 Frame-Buffered Surface Table");
            activeTileAndIndirect = _bufferManager.CreateDeviceBuffer(
                _layout.ActiveTileAndIndirectBytes,
                usage | BufferUsageFlags.IndirectBufferBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 Active Tiles And Indirect Arguments");
            schedulerHistory0 = _bufferManager.CreateDeviceBuffer(
                _layout.SchedulerHistoryBytes / 2UL,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 Scheduler History 0");
            schedulerHistory1 = _bufferManager.CreateDeviceBuffer(
                _layout.SchedulerHistoryBytes / 2UL,
                usage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "C5 Scheduler History 1");
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
                historyMetadata0,
                historyMetadata1,
                surfaceTable,
                activeTileAndIndirect,
                schedulerHistory0,
                schedulerHistory1,
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
            _diagnostics =
                SimpleDdgiNearFieldResidualDiagnostics.PendingGpuReadback(
                    CreateMemoryTelemetry(),
                    "C5 renderer integration is active; awaiting the first frame-fence readback.")
                with
                {
                    AdaptiveResolution =
                        CreateAdaptiveResolutionTelemetry()
                };

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
            _allocationAdapter = new BorrowedGraphAllocationAdapter(_layout);
            SimpleDdgiNearFieldResidualGpuRuntimeSnapshot snapshot =
                _manager.Reconcile(
                    new SimpleDdgiNearFieldResidualGpuRuntimeRequest(
                        IsEffectivelyEnabled: true,
                        _layout,
                        _configuration,
                        CreateIntegratedCapabilities(
                            _layout,
                            recorder,
                            _actualAllocationBytes <= admittedBudgetBytes)),
                    _allocationAdapter);
            if (!snapshot.IsContractReadyForRendererIntegration)
            {
                throw new InvalidOperationException(
                    "C5 lifecycle admission failed: " + snapshot.Reason);
            }
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
            DestroyBuffer(schedulerHistory1);
            DestroyBuffer(schedulerHistory0);
            DestroyBuffer(activeTileAndIndirect);
            DestroyBuffer(surfaceTable);
            DestroyBuffer(historyMetadata1);
            DestroyBuffer(historyMetadata0);
            _manager.Dispose();
            throw;
        }
    }

    public SimpleDdgiNearFieldResidualGpuRuntimeSnapshot Snapshot =>
        _manager.Snapshot;

    internal bool LocalAdaptiveSchedulingEnabled =>
        _configuration.LocalAdaptiveSchedulingEnabled;

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
            ulong pendingFrameSerial,
            SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry
                adaptiveResolution = default)
    {
        SimpleDdgiNearFieldResidualDiagnostics lastPublished =
            lastCompleted.CompletedFrameSerial == 0UL
                ? SimpleDdgiNearFieldResidualDiagnostics.PendingGpuReadback(
                    memory,
                    "C5 commands are recorded and awaiting the frame-slot fence.")
                : SimpleDdgiNearFieldResidualDiagnostics
                    .CreateCounterReadbackPending(lastCompleted, memory);
        return CreatePendingReadbackDiagnostics(
            lastPublished,
            memory,
            pendingFrameSerial,
            adaptiveResolution);
    }

    internal static SimpleDdgiNearFieldResidualDiagnostics
        CreatePendingReadbackDiagnostics(
            SimpleDdgiNearFieldResidualDiagnostics lastPublished,
            SimpleDdgiNearFieldResidualMemoryTelemetry memory,
            ulong pendingFrameSerial,
            SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry
                adaptiveResolution = default)
    {
        ArgumentNullException.ThrowIfNull(lastPublished);
        ulong completedFrameSerial =
            lastPublished.Readback.CompletedFrameSerial;
        if (completedFrameSerial == 0UL)
        {
            return SimpleDdgiNearFieldResidualDiagnostics.PendingGpuReadback(
                memory,
                "C5 commands are recorded and awaiting the frame-slot fence.")
                with { AdaptiveResolution = adaptiveResolution };
        }

        ulong age = pendingFrameSerial > completedFrameSerial
            ? pendingFrameSerial - completedFrameSerial
            : 0UL;
        bool authoritative = lastPublished.IsAuthoritativeReadback;
        string reason = authoritative
            ? "Latest common-frame C5 counters and timings are authoritative; " +
              "a newer frame is awaiting readback."
            : lastPublished.Readback.Reason +
              " A newer frame is awaiting readback.";
        return (lastPublished with
        {
            Readback = lastPublished.Readback with
            {
                AgeFrames = checked((uint)Math.Min(age, uint.MaxValue)),
                Reason = reason
            },
            Memory = memory.Normalize(),
            AdaptiveResolution = adaptiveResolution.IsValid
                ? adaptiveResolution
                : lastPublished.AdaptiveResolution
        }).NormalizeForPersistence();
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

    internal bool RequiresGenerationRebuild
    {
        get
        {
            lock (_sync)
                return !_disposed && _requiresGenerationRebuild;
        }
    }

    internal uint ConsecutiveTelemetryFailureCount
    {
        get
        {
            lock (_sync)
                return _consecutiveTelemetryFailures;
        }
    }

    internal bool HasValidCompletionWitness
    {
        get
        {
            lock (_sync)
                return _lastCompletedWitness.CompletedFrameSerial != 0UL;
        }
    }

    public SimpleDdgiNearFieldResidualExecutionExtent ExecutionExtent
    {
        get
        {
            lock (_sync)
                return _executionExtent;
        }
    }

    internal SimpleDdgiNearFieldResidualVulkanBuffers Buffers => _buffers;

    internal bool CanExecute(SceneRenderingData sceneData)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        lock (_sync)
        {
            // Render-graph admission and record-time validation must use the
            // same predicate. Capture/debug variants can temporarily change
            // the scene-data contract without rebuilding the immutable C5
            // graph; skip the complete transaction instead of admitting reset
            // and then throwing from ValidateRecordStart.
            return _frameAdmission && CanExecuteNoLock(sceneData);
        }
    }

    internal void SetFrameAdmission(bool admitted, string? reason)
    {
        lock (_sync)
        {
            if (_disposed || !_active)
                return;
            if (_requiresGenerationRebuild)
            {
                _frameAdmission = false;
                return;
            }
            bool governorAvailable = _resolutionGovernor.AdvanceFrame();
            _frameAdmission = admitted && governorAvailable;
            if (!_frameAdmission)
            {
                _recordedStage =
                    SimpleDdgiNearFieldResidualRecordedStage.Idle;
                _recordedFrameIndex = -1;
                _diagnostics = SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                    !governorAvailable
                        ? "near-field-runtime-suspended-after-lowest-tier-budget-overrun"
                        : string.IsNullOrWhiteSpace(reason)
                            ? "near-field-runtime-content-binding-rejected"
                            : reason.Trim()) with
                    {
                        AdaptiveResolution =
                            CreateAdaptiveResolutionTelemetry()
                    };
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
            _recordedExecutionExtent = _executionExtent;
            var revision = CreateHistoryRevision(sceneData);
            _recordedHistoryRevision = revision;
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
                _recordedExecutionExtent,
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
                _configuration.LocalAdaptiveSchedulingEnabled
                    ? SimpleDdgiNearFieldResidualRecordedStage.Classify
                    : SimpleDdgiNearFieldResidualRecordedStage.Prepare);
            _recorder.RecordTrace(
                commandBuffer,
                frameIndex,
                _frameToken,
                _recordedExecutionExtent);
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

    internal void RecordPrepare(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        lock (_sync)
        {
            ValidateStage(commandBuffer, frameIndex, sceneData,
                SimpleDdgiNearFieldResidualRecordedStage.Reset);
            FoliageRuntimeBuffers foliage =
                _foliageManager.GetBuffers(frameIndex);
            _recorder.RecordPrepare(
                commandBuffer,
                frameIndex,
                _frameToken,
                _recordedExecutionExtent,
                sceneData.CaptureCameraNearPlane,
                sceneData.CaptureCameraFarPlane,
                sceneData.ObjectDataBuffer,
                sceneData.MaterialDataBuffer,
                foliage.PrototypeBuffer,
                foliage.PatchBuffer,
                foliage.ClusterBuffer,
                checked((uint)Math.Clamp(
                    sceneData.ObjectCount,
                    0,
                    (int)SimpleDdgiNearFieldResidualGpuAbi
                        .MaximumSurfaceTableEntryCount)),
                sceneData.MotionVectorsEnabled != 0 &&
                    sceneData.FoliageMotionVectorsEnabled);
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Prepare;
        }
    }

    internal void RecordClassify(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        lock (_sync)
        {
            ValidateStage(commandBuffer, frameIndex, sceneData,
                SimpleDdgiNearFieldResidualRecordedStage.Prepare);
            if (!_configuration.LocalAdaptiveSchedulingEnabled)
                throw new InvalidOperationException(
                    "C5 local classifier is disabled for this generation.");
            bool lightingChanged = !_hasPublishedSourceLightingEpoch ||
                _publishedSourceLightingEpoch !=
                    sceneData.SimpleDdgiSourceLightingGeneration;
            _recorder.RecordClassify(
                commandBuffer,
                _frameToken,
                _recordedExecutionExtent,
                _recordedHistoryRevision,
                _historyInputValid,
                lightingChanged,
                sceneData.DdgiFrameSerial == ulong.MaxValue
                    ? ulong.MaxValue
                    : sceneData.DdgiFrameSerial + 1UL);
            _recordedStage =
                SimpleDdgiNearFieldResidualRecordedStage.Classify;
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
                _recordedHistoryRevision,
                _historyInputValid,
                !_hasPublishedSourceLightingEpoch ||
                    _publishedSourceLightingEpoch !=
                    sceneData.SimpleDdgiSourceLightingGeneration,
                _recordedExecutionExtent);
            SimpleDdgiNearFieldResidualGpuStageResult completion =
                _manager.CompleteTemporal(
                    _frameToken,
                    new SimpleDdgiNearFieldResidualGpuTemporalCompletion(
                        QueueOrderedCommandsRecorded: true,
                        HistoryWritesContainOnlyValidCandidates: true,
                        HistoryBankFullyInitialized: true));
            RequireCompletion(completion);
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Finalize;
        }
    }

    internal void RecordFinalize(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        lock (_sync)
        {
            ValidateStage(commandBuffer, frameIndex, sceneData,
                SimpleDdgiNearFieldResidualRecordedStage.Finalize);
            _recorder.RecordFinalize(
                commandBuffer,
                frameIndex,
                _recordedExecutionExtent);
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

            _recorder.RecordFilter(
                commandBuffer,
                _frameToken,
                iteration,
                _recordedExecutionExtent);
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
                SimpleDdgiNearFieldResidualRecordedStage.FrequencySeparation);
            if (_configuration.FilterIterationCount != _recordedFilterIterations)
            {
                FailFrame("C5 composite observed incomplete filtering.");
                throw new InvalidOperationException(
                    "C5 composite cannot run before every admitted filter iteration.");
            }

            _recorder.RecordComposite(
                commandBuffer,
                _frameToken,
                _recordedExecutionExtent,
                sceneData.NearFieldResidualDebugView);
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
            _publishedSourceLightingEpoch =
                sceneData.SimpleDdgiSourceLightingGeneration;
            _hasPublishedSourceLightingEpoch = true;
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Composite;
            _recordedFrameIndex = -1;
        }
    }

    internal void RecordFrequencySeparation(
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
                FailFrame("C5 frequency separation observed incomplete filtering.");
                throw new InvalidOperationException(
                    "C5 frequency separation requires every admitted atrous pass.");
            }
            _recorder.RecordFrequencySeparation(
                commandBuffer,
                _frameToken,
                _recordedExecutionExtent,
                sceneData.NearFieldResidualDebugView);
            SimpleDdgiNearFieldResidualGpuStageResult completion =
                _manager.CompleteFrequencySeparation(
                    _frameToken,
                    new
                        SimpleDdgiNearFieldResidualGpuFrequencySeparationCompletion(
                            QueueOrderedCommandsRecorded: true,
                            B3FootprintSupportValidated: true,
                            PerIdentityConfidenceWeightedMeanRemoved: true,
                            InvalidResidualPayloadWasZero: true));
            RequireCompletion(completion);
            _recordedStage =
                SimpleDdgiNearFieldResidualRecordedStage.FrequencySeparation;
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
        => TryReadCompletedFrame(
            frameIndex,
            FrameTimingSnapshot.Empty,
            out witness);

    /// <summary>
    /// Joins the fence-complete counter stream to the timestamp snapshot from
    /// the same frame slot. A missing stage leaves the sample explicitly
    /// non-authoritative; whole-pass Forward+ time is never charged as C5's
    /// source-attachment cost.
    /// </summary>
    public bool TryReadCompletedFrame(
        int frameIndex,
        FrameTimingSnapshot frameTimings,
        out SimpleDdgiNearFieldResidualCompletionWitness witness)
    {
        ArgumentNullException.ThrowIfNull(frameTimings);
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
                RecordTelemetryFailure(
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
                        CreateExecutionLayout(pending.ExecutionExtent),
                        pending.CompletedFrameSerial,
                        out witness,
                        out string validationFailure))
                {
                    RecordTelemetryFailure(validationFailure);
                    return false;
                }

                _consecutiveTelemetryFailures = 0U;
                _requiresGenerationRebuild = false;
                _lastTelemetryFailureReason = string.Empty;
                _lastCompletedWitness = witness;
                bool timingsAvailable = TryResolveStageTimings(
                        frameTimings,
                        _configuration.FilterIterationCount,
                        _calibratedSourceCostUpperBoundMicroseconds,
                        _sourceCostAuthoritative,
                        _configuration.LocalAdaptiveSchedulingEnabled,
                        out SimpleDdgiNearFieldResidualStageTimings timings,
                        out string unavailableTimingPass);
                if (timingsAvailable)
                {
                    bool resolutionChanged =
                        _resolutionGovernor.ObserveAuthoritativeGpuTime(
                            timings.TotalMicroseconds);
                    if (resolutionChanged)
                    {
                        _executionExtent = _resolutionGovernor.CreateExtent(
                            _layout.SourceWidth,
                            _layout.SourceHeight);
                    }
                    _diagnostics =
                        SimpleDdgiNearFieldResidualDiagnostics.CreateAuthoritative(
                            witness.CompletedFrameSerial,
                            ageFrames: 0U,
                            CreateMemoryTelemetry(),
                            timings,
                            witness.Trace,
                            witness.History,
                            witness.ResidualEnergy,
                            witness.Tiles,
                            _captureIdentifiers)
                        with
                    {
                        AdaptiveResolution =
                            CreateAdaptiveResolutionTelemetry(
                                pending.ExecutionExtent,
                                resolutionChanged)
                    };
                }
                else
                {
                    _diagnostics =
                        SimpleDdgiNearFieldResidualDiagnostics.CreateCounterReadbackPending(
                        witness,
                        CreateMemoryTelemetry(),
                        reason: "C5 frame fence and detailed counter stream are valid; " +
                        "exclusive timing pass '" + unavailableTimingPass +
                        "' is unavailable in the common frame snapshot.")
                        with
                        {
                            AdaptiveResolution =
                                CreateAdaptiveResolutionTelemetry(
                                    pending.ExecutionExtent,
                                    resolutionChangedAfterSample: false)
                        };
                }
                return true;
            }
            catch (Exception exception)
            {
                RecordTelemetryFailure(
                    "near-field-telemetry-readback-failed:" +
                    exception.GetType().Name);
                return false;
            }
        }
    }

    internal static bool TryResolveStageTimings(
        FrameTimingSnapshot snapshot,
        int filterIterationCount,
        out SimpleDdgiNearFieldResidualStageTimings timings)
        => TryResolveStageTimings(
            snapshot,
            filterIterationCount,
            calibratedSourceMicroseconds: 0UL,
            sourceCostAuthoritative: false,
            out timings);

    internal static bool TryResolveStageTimings(
        FrameTimingSnapshot snapshot,
        int filterIterationCount,
        ulong calibratedSourceMicroseconds,
        bool sourceCostAuthoritative,
        out SimpleDdgiNearFieldResidualStageTimings timings)
        => TryResolveStageTimings(
            snapshot,
            filterIterationCount,
            calibratedSourceMicroseconds,
            sourceCostAuthoritative,
            out timings,
            out _);

    internal static bool TryResolveStageTimings(
        FrameTimingSnapshot snapshot,
        int filterIterationCount,
        ulong calibratedSourceMicroseconds,
        bool sourceCostAuthoritative,
        out SimpleDdgiNearFieldResidualStageTimings timings,
        out string unavailablePass)
        => TryResolveStageTimings(
            snapshot,
            filterIterationCount,
            calibratedSourceMicroseconds,
            sourceCostAuthoritative,
            localAdaptiveSchedulingEnabled: false,
            out timings,
            out unavailablePass);

    private static bool TryResolveStageTimings(
        FrameTimingSnapshot snapshot,
        int filterIterationCount,
        ulong calibratedSourceMicroseconds,
        bool sourceCostAuthoritative,
        bool localAdaptiveSchedulingEnabled,
        out SimpleDdgiNearFieldResidualStageTimings timings,
        out string unavailablePass)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        timings = SimpleDdgiNearFieldResidualStageTimings.Empty;
        unavailablePass = string.Empty;
        if (filterIterationCount is < 0 or > 8)
        {
            unavailablePass = "invalid-filter-iteration-count";
            return false;
        }

        if (!TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.Reset,
                out ulong reset,
                out unavailablePass) ||
            !TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.Prepare,
                out ulong prepare,
                out unavailablePass) ||
            !TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.Trace,
                out ulong trace,
                out unavailablePass) ||
            !TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.Temporal,
                out ulong temporal,
                out unavailablePass) ||
            !TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.Finalize,
                out ulong finalization,
                out unavailablePass) ||
            !TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.FrequencySeparation,
                out ulong frequencySeparation,
                out unavailablePass) ||
            !TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.Composite,
                out ulong composite,
                out unavailablePass))
        {
            return false;
        }

        ulong filter = 0UL;
        ulong classify = 0UL;
        if (localAdaptiveSchedulingEnabled &&
            !TryReadRequiredGpuMicroseconds(
                snapshot,
                SimpleDdgiNearFieldResidualGpuPassNames.Classify,
                out classify,
                out unavailablePass))
        {
            return false;
        }
        for (int iteration = 0; iteration < filterIterationCount; iteration++)
        {
            if (!TryReadRequiredGpuMicroseconds(
                    snapshot,
                    SimpleDdgiNearFieldResidualGpuPassNames.FilterIteration(iteration),
                    out ulong iterationMicroseconds,
                    out unavailablePass))
            {
                return false;
            }
            filter = SaturatingAdd(filter, iterationMicroseconds);
        }

        timings = new SimpleDdgiNearFieldResidualStageTimings(
            SourceMicroseconds: calibratedSourceMicroseconds,
            RawTraceMicroseconds: trace,
            TemporalMicroseconds: temporal,
            FilterMicroseconds: filter,
            CompositeMicroseconds: composite)
        {
            PrepareCompactionMicroseconds = SaturatingAdd(
                SaturatingAdd(reset, prepare), classify),
            FinalizationMicroseconds = finalization,
            FrequencySeparationMicroseconds = frequencySeparation,
            SourceCostAuthoritative = sourceCostAuthoritative &&
                calibratedSourceMicroseconds != 0UL
        };
        unavailablePass = string.Empty;
        return true;
    }

    private static bool TryReadRequiredGpuMicroseconds(
        FrameTimingSnapshot snapshot,
        string passName,
        out ulong microseconds,
        out string unavailablePass)
    {
        bool available = TryReadGpuMicroseconds(
            snapshot,
            passName,
            out microseconds);
        unavailablePass = available ? string.Empty : passName;
        return available;
    }

    private static bool TryReadGpuMicroseconds(
        FrameTimingSnapshot snapshot,
        string passName,
        out ulong microseconds)
    {
        microseconds = 0UL;
        if (!snapshot.TryGetPass(passName, out PassTiming pass) ||
            !pass.GpuAvailable || pass.GpuMicroseconds < 0L)
        {
            return false;
        }

        microseconds = checked((ulong)pass.GpuMicroseconds);
        return true;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    internal void OnRenderTargetsRecreated()
    {
        lock (_sync)
        {
            if (_disposed || !_active)
                return;
            if (!HasExactTargetExtents())
            {
                DisableAndReleaseAfterDeviceIdle(
                    "near-field-generation-target-extent-mismatch");
                return;
            }

            _recorder.RewriteDescriptors();
            _recordedStage = SimpleDdgiNearFieldResidualRecordedStage.Idle;
            _recordedFrameIndex = -1;
        }
    }

    /// <summary>
    /// Retires all runtime-owned C5 objects after the caller has supplied an
    /// external completion guarantee. Ordinary generation transactions use
    /// fence retirement; this boundary is retained for terminal shutdown and
    /// fail-closed renderer teardown. This method is idempotent.
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
        IsCompatibleDebugView(sceneData.NearFieldResidualDebugView) &&
        HasExactTargetExtents();

    private static bool IsCompatibleDebugView(uint debugView) =>
        debugView == (uint)GlobalIlluminationDebugView.None ||
        SimpleDdgiNearFieldResidualDebugViewContract.IsC5View(debugView);

    private bool HasExactTargetExtents()
    {
        uint sourceWidth = checked((uint)_layout.SourceWidth);
        uint sourceHeight = checked((uint)_layout.SourceHeight);
        uint traceWidth = checked((uint)_layout.TraceWidth);
        uint traceHeight = checked((uint)_layout.TraceHeight);
        return HasExtent(_renderTargets.NearFieldDirectSource, sourceWidth, sourceHeight) &&
            HasExtent(_renderTargets.NearFieldReceiverPayload,
                sourceWidth, sourceHeight) &&
            HasExtent(_renderTargets.NearFieldPreparedDepthFootprint,
                traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldPreparedReceiverPayload,
                traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldPreparedMotion,
                traceWidth, traceHeight) &&
            HasExtent(_renderTargets.NearFieldSourceLuminance,
                traceWidth, traceHeight) &&
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
            PreviousViewProjection = _hasPreviousTraceMatrices
                ? _previousViewProjection
                : sceneData.ViewProjectionMatrix,
            PreviousInverseViewProjection = _hasPreviousTraceMatrices
                ? _previousInverseViewProjection
                : sceneData.InverseViewProjectionMatrix,
            FullExtentAndInverse = new Vector4(
                _layout.SourceWidth,
                _layout.SourceHeight,
                1.0f / _layout.SourceWidth,
                1.0f / _layout.SourceHeight),
            ClipAndSequence = new Vector4(
                MathF.Max(sceneData.CaptureCameraNearPlane, 0.001f),
                MathF.Max(sceneData.CaptureCameraFarPlane,
                    MathF.Max(sceneData.CaptureCameraNearPlane, 0.001f) + 0.01f),
                BitConverter.UInt32BitsToSingle(sceneData.TemporalSampleIndex),
                BitConverter.UInt32BitsToSingle(
                    ForwardNearFieldDirectSourceContract.ShaderSemanticVersion))
        };
        _previousViewProjection = sceneData.ViewProjectionMatrix;
        _previousInverseViewProjection = sceneData.InverseViewProjectionMatrix;
        _hasPreviousTraceMatrices = true;
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
            StructuralProjectionRevision: HashStructuralProjection(sceneData),
            OriginRebaseRevision: 1u,
            SceneGeneration: NonZeroHash(
                unchecked((uint)sceneData.SceneContentRevision),
                unchecked((uint)(sceneData.SceneContentRevision >> 32)),
                0x4335u),
            TraceSourceContentRevision:
                _configuration.TraceSourceContract.SourceRevision,
            NearFieldLayoutRevision: NonZeroHash(
                _configuration.TraceSourceContract.LayoutRevision,
                _recordedExecutionExtent.Revision,
                checked((uint)_recordedExecutionExtent.Scale + 1U)),
            B3OwnershipRevision: _b3OwnershipRevision,
            TraceSourceLayoutRevision:
                _configuration.TraceSourceContract.LayoutRevision);
    }

    private static uint HashStructuralProjection(SceneRenderingData sceneData)
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

    private void RecordTelemetryFailure(string reason)
    {
        string detail = SimpleDdgiNearFieldResidualDiagnosticsText
            .NormalizeReason(reason);
        _consecutiveTelemetryFailures = _consecutiveTelemetryFailures ==
            uint.MaxValue
            ? uint.MaxValue
            : _consecutiveTelemetryFailures + 1U;
        _lastTelemetryFailureReason = detail;
        _requiresGenerationRebuild = _consecutiveTelemetryFailures >=
            ConsecutiveTelemetryFailureRebuildThreshold;
        if (_requiresGenerationRebuild)
            _frameAdmission = false;

        var recovery = new SimpleDdgiNearFieldResidualRecoveryTelemetry(
            _consecutiveTelemetryFailures,
            GenerationRebuildAttemptCount: 0U,
            GenerationRebuildPending: _requiresGenerationRebuild,
            ValidationDeadlineFrame: 0UL,
            LastFailureReason: detail);
        if (_lastCompletedWitness.CompletedFrameSerial != 0UL &&
            _diagnostics.Readback.CompletedFrameSerial != 0UL)
        {
            uint age = _diagnostics.Readback.AgeFrames == uint.MaxValue
                ? uint.MaxValue
                : _diagnostics.Readback.AgeFrames + 1U;
            _diagnostics = _diagnostics with
            {
                Readback = _diagnostics.Readback with
                {
                    AgeFrames = age,
                    Reason = "last valid C5 witness retained; rejected frame: " +
                        detail
                },
                Recovery = recovery
            };
        }
        else
        {
            _diagnostics = SimpleDdgiNearFieldResidualDiagnostics
                .PendingGpuReadback(
                    CreateMemoryTelemetry(),
                    "C5 telemetry frame rejected: " + detail) with
                {
                    AdaptiveResolution = CreateAdaptiveResolutionTelemetry(),
                    Recovery = recovery
                };
        }
    }

    internal void SuppressForFenceSafeRetirement(string reason)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            string detail = SimpleDdgiNearFieldResidualDiagnosticsText
                .NormalizeReason(reason);
            _frameAdmission = false;
            _requiresGenerationRebuild = false;
            _lastTelemetryFailureReason = detail;
            _diagnostics = SimpleDdgiNearFieldResidualDiagnostics.Faulted(
                CreateMemoryTelemetry(),
                detail) with
            {
                Recovery = new SimpleDdgiNearFieldResidualRecoveryTelemetry(
                    _consecutiveTelemetryFailures,
                    GenerationRebuildAttemptCount: 1U,
                    GenerationRebuildPending: false,
                    ValidationDeadlineFrame: 0UL,
                    LastFailureReason: detail)
            };
        }
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
            completedFrameSerial,
            _recordedExecutionExtent);
        _diagnostics = CreatePendingReadbackDiagnostics(
            _diagnostics,
            CreateMemoryTelemetry(),
            completedFrameSerial,
            CreateAdaptiveResolutionTelemetry(
                _recordedExecutionExtent,
                resolutionChangedAfterSample: false));
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

    private SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry
        CreateAdaptiveResolutionTelemetry() =>
            CreateAdaptiveResolutionTelemetry(
                sampledExtent: default,
                resolutionChangedAfterSample: false);

    private SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry
        CreateAdaptiveResolutionTelemetry(
            in SimpleDdgiNearFieldResidualExecutionExtent sampledExtent,
            bool resolutionChangedAfterSample) => new(
                SampledExtent: sampledExtent,
                ActiveExtent: _executionExtent,
                MaximumScale: _resolutionGovernor.MaximumScale,
                LastP95Microseconds: _resolutionGovernor.LastP95Microseconds,
                AuthoritativeTimingSampleCount:
                    _resolutionGovernor.AuthoritativeTimingSampleCount,
                WindowSampleCount: checked((uint)_resolutionGovernor.WindowSampleCount),
                PromotionWindowStreak:
                    checked((uint)_resolutionGovernor.PromotionWindowStreak),
                PromotionCount: _resolutionGovernor.PromotionCount,
                DemotionCount: _resolutionGovernor.DemotionCount,
                ResolutionChangedAfterSample: resolutionChangedAfterSample)
            {
                LowestTierOverBudgetEvaluationStreak = checked((uint)
                    _resolutionGovernor.LowestTierOverBudgetEvaluationStreak),
                SuspendedFramesRemaining = checked((uint)
                    _resolutionGovernor.SuspendedFramesRemaining),
                SuspensionCount = _resolutionGovernor.SuspensionCount,
                PromotionEnabled = _resolutionGovernor.PromotionEnabled
            };

    private void DestroyRuntimeBuffersNoLock()
    {
        DestroyBuffer(_buffers.TelemetryReadback1);
        DestroyBuffer(_buffers.TelemetryReadback0);
        DestroyBuffer(_buffers.TraceFrameConstants1);
        DestroyBuffer(_buffers.TraceFrameConstants0);
        DestroyBuffer(_buffers.TileRecords);
        DestroyBuffer(_buffers.SchedulerHistory1);
        DestroyBuffer(_buffers.SchedulerHistory0);
        DestroyBuffer(_buffers.ActiveTileAndIndirect);
        DestroyBuffer(_buffers.SurfaceTable);
        DestroyBuffer(_buffers.HistoryMetadata1);
        DestroyBuffer(_buffers.HistoryMetadata0);
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
            ImageBytes(_renderTargets.NearFieldPreparedDepthFootprint) +
            ImageBytes(_renderTargets.NearFieldPreparedReceiverPayload) +
            ImageBytes(_renderTargets.NearFieldPreparedMotion) +
            ImageBytes(_renderTargets.NearFieldSourceLuminance) +
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
            _bufferManager.GetBufferAllocationSize(_buffers.HistoryMetadata0) +
            _bufferManager.GetBufferAllocationSize(_buffers.HistoryMetadata1) +
            _bufferManager.GetBufferAllocationSize(_buffers.SurfaceTable) +
            _bufferManager.GetBufferAllocationSize(_buffers.ActiveTileAndIndirect) +
            _bufferManager.GetBufferAllocationSize(_buffers.SchedulerHistory0) +
            _bufferManager.GetBufferAllocationSize(_buffers.SchedulerHistory1) +
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

    private SimpleDdgiNearFieldResidualLayout CreateExecutionLayout(
        in SimpleDdgiNearFieldResidualExecutionExtent extent) => _layout with
    {
        TraceResolutionScale = extent.Scale switch
        {
            SimpleDdgiNearFieldResidualExecutionScale.Half => 0.5F,
            SimpleDdgiNearFieldResidualExecutionScale.Quarter => 0.25F,
            _ => 0.125F
        },
        TraceWidth = extent.Width,
        TraceHeight = extent.Height
    };

    private static void ValidatePhysicalIntegration(
        VulkanContext context,
        SimpleDdgiNearFieldResidualTargetBinding targets,
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

    private SimpleDdgiNearFieldResidualGpuIntegrationCapabilities
        CreateIntegratedCapabilities(
            in SimpleDdgiNearFieldResidualLayout layout,
            SimpleDdgiNearFieldResidualGpuCommandRecorder recorder,
            bool actualAllocationRequirementsValidated)
    {
        bool nativePipelines = recorder.ShaderPipelinesValidated;
        bool descriptorContract = recorder.DescriptorContractValidated;
        bool exactTargets = HasExactTargetExtents();
        bool historyBanks = _buffers.HistoryMetadata0.IsValid &&
            _buffers.HistoryMetadata1.IsValid &&
            layout.HistoryMetadataBytes != 0UL;
        bool sourceContract = _configuration.TraceSourceContract
            .TryValidateForLayout(layout, out _);
        return new SimpleDdgiNearFieldResidualGpuIntegrationCapabilities(
            TracePassRegistered: nativePipelines,
            TemporalPassRegistered: nativePipelines,
            FilterPassRegistered: nativePipelines,
            CompositePassRegistered: nativePipelines,
            DirectDiffuseEmissiveAttachmentAvailable:
                exactTargets && sourceContract,
            HiZAvailable: IsCompatibleHiZExtent(
                layout,
                _hiZ.Extent.Width,
                _hiZ.Extent.Height),
            ReceiverMetadataAvailable:
                exactTargets && layout.ReceiverPayloadBytes != 0UL,
            StableSampleRayInputAvailable:
                _buffers.TraceFrameConstants0.IsValid &&
                _buffers.TraceFrameConstants1.IsValid &&
                layout.PreparedReceiverPayloadBytes != 0UL,
            ReceiverBrdfPdfInputAvailable:
                layout.ReceiverPayloadBytes != 0UL,
            MotionVectorsAvailable: exactTargets,
            DoubleBufferedHistoryIdentityAvailable: historyBanks,
            HistoryIdentityMemoryBudgeted: historyBanks,
            TileRecordLayoutValidated:
                layout.TileBuffersBytes != 0UL &&
                _buffers.TileRecords.IsValid,
            RequiredImageFormatsValidated: exactTargets &&
                layout.SourceFormat ==
                    SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
            DescriptorAndBarrierContractValidated: descriptorContract,
            ShaderArtifactsValidated: nativePipelines,
            ResetPassRegistered: nativePipelines,
            PingPongBankBindingAndSynchronizationValidated:
                descriptorContract && historyBanks,
            DirectSourceVariantProvenanceValidated: sourceContract,
            GeometricAndShadingNormalHistoryAvailable:
                exactTargets && layout.HistoryNormalBytes != 0UL,
            HitUvAndSourceRevisionValidationAvailable:
                layout.HistoryMetadataBytes != 0UL &&
                SimpleDdgiNearFieldResidualGpuAbi.HitMetadataByteCount == 48U,
            TemporalVarianceClippingAndBoundedHistoryAvailable:
                exactTargets && layout.MomentBytes != 0UL &&
                _configuration.MaximumHistoryLength <=
                    SimpleDdgiNearFieldResidualGpuAbi
                        .MaximumTemporalHistoryLength,
            B3FootprintFrequencySeparationValidated:
                nativePipelines && layout.SourceLuminanceBytes != 0UL,
            MeasuredQualificationEvidenceVerified:
                _sourceCostAuthoritative,
            DeviceLimitsAndActualAllocationRequirementsValidated:
                actualAllocationRequirementsValidated)
        {
            PreparePassRegistered = nativePipelines,
            FinalizePassRegistered = nativePipelines,
            FrequencySeparationPassRegistered = nativePipelines,
            IndirectDispatchContractValidated =
                descriptorContract && _buffers.ActiveTileAndIndirect.IsValid,
            SurfaceTableAvailable = _buffers.SurfaceTable.IsValid &&
                layout.SurfaceTableBytes != 0UL
        };
    }

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
                SimpleDdgiNearFieldResidualGpuAllocation.ExpectedDescriptorCount(layout))
            {
                PreparedDepthFootprint = Resource(layout.PreparedDepthFootprintBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.PreparedDepthFootprint),
                PreparedReceiverPayload = Resource(layout.PreparedReceiverPayloadBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.PreparedReceiverPayload),
                PreparedMotion = Resource(layout.PreparedMotionBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.PreparedMotion),
                SourceLuminance = Resource(layout.SourceLuminanceBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.SourceLuminance),
                SurfaceTable = Resource(layout.SurfaceTableBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.SurfaceTable),
                ActiveTileAndIndirect = Resource(layout.ActiveTileAndIndirectBytes,
                    SimpleDdgiNearFieldResidualGpuResourceKind.ActiveTileAndIndirect),
                SchedulerHistory0 = Resource(layout.SchedulerHistoryBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.SchedulerHistory0),
                SchedulerHistory1 = Resource(layout.SchedulerHistoryBytes / 2UL,
                    SimpleDdgiNearFieldResidualGpuResourceKind.SchedulerHistory1)
            };
        }

        public void Retire(SimpleDdgiNearFieldResidualGpuAllocation allocation)
        {
            // Graph images and native buffers are retired by their concrete owners.
        }
    }

    private readonly record struct PendingReadback(
        SimpleDdgiNearFieldResidualGpuFrameToken Token,
        ulong CompletedFrameSerial,
        SimpleDdgiNearFieldResidualExecutionExtent ExecutionExtent);
}
