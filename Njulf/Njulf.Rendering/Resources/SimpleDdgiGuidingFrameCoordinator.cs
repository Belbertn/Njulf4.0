using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Safe-transition inputs for the live C3 frame integration. The runtime and
/// source-cache layouts must describe the same admitted physical prefix.
/// </summary>
internal readonly record struct SimpleDdgiGuidingFrameConfiguration(
    SimpleDdgiGuidingRuntimeRequest RuntimeRequest,
    SimpleDdgiGuidingSourceCacheLayout SourceCacheLayout,
    bool GlobalPrerequisiteGateAdmitted,
    SimpleDdgiGuidingProposalPolicy ProposalPolicy)
{
    public bool IsEnabled => GlobalPrerequisiteGateAdmitted &&
        RuntimeRequest.IsEffectivelyEnabled && SourceCacheLayout.IsAdmitted;

    public static SimpleDdgiGuidingFrameConfiguration Disabled { get; } = new(
        new SimpleDdgiGuidingRuntimeRequest(false, default),
        SimpleDdgiGuidingSourceCacheLayout.Disabled,
        false,
        SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
}

public readonly record struct SimpleDdgiGuidingFrameCoordinatorDiagnostics(
    bool Configured,
    bool FramePrepared,
    bool SampleRecorded,
    bool TrainRecorded,
    bool BuildRecorded,
    bool ValidateRecorded,
    ulong FrameSerial,
    int GuidedProbeCount,
    uint TrainingRecordCount,
    int SampleRequestCount,
    ulong UploadedBytes,
    ulong WorkspaceBytes,
    string State)
{
    /// <summary>Last frame whose C3 readback was consumed after its fence.</summary>
    public ulong CompletedFrameSerial { get; init; }

    /// <summary>True only for a fence-complete, structurally valid sample counter readback.</summary>
    public bool SampleReadbackValid { get; init; }

    public int CompletedSampleCount { get; init; }

    public SimpleDdgiGuidingValidationCounters SampleValidationCounters
    {
        get;
        init;
    }

    public SimpleDdgiGuidingSampleTelemetry SampleTelemetry { get; init; }

    public bool DistributionPublicationSucceeded { get; init; }

    public static SimpleDdgiGuidingFrameCoordinatorDiagnostics Disabled { get; } =
        new(false, false, false, false, false, false, 0UL, 0, 0u, 0,
            0UL, 0UL, "disabled");
}

/// <summary>
/// Complete physical C3 resource view used by render-graph synchronization.
/// Each range retains its owning generation so a cached plan can never survive
/// a bank, arena, or source-cache replacement.
/// </summary>
internal readonly record struct SimpleDdgiGuidingGraphResourceSnapshot(
    SimpleDdgiGuidingDistributionResourceSnapshot Distributions,
    BufferHandle WorkspaceBuffer,
    ulong WorkspaceOffsetBytes,
    ulong WorkspaceBytes,
    ulong WorkspaceGeneration,
    BufferHandle DirectionPayloadSidecar,
    ulong DirectionPayloadBytes,
    ulong DirectionPayloadGeneration)
{
    public bool IsComplete => Distributions.IsComplete &&
        WorkspaceBuffer.IsValid && WorkspaceBytes > 0UL &&
        WorkspaceGeneration > 0UL && DirectionPayloadSidecar.IsValid &&
        DirectionPayloadBytes > 0UL && DirectionPayloadGeneration > 0UL;
}

/// <summary>
/// Connects the CPU-authoritative DDGI update queue to the source-cache-owned
/// direction/PDF sidecar and the staged C3 Vulkan runtime. All large GPU work
/// records are packed into the centrally planned transient arena; only bounded
/// reusable CPU arrays are retained. One global proposal transaction may be
/// in flight, matching the distribution manager's atomic publication model.
/// </summary>
internal sealed class SimpleDdgiGuidingFrameCoordinator : IDisposable
{
    private const AccessFlags2 TransferWriteAccess = AccessFlags2.TransferWriteBit;
    private readonly object _sync = new();
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly StagingRing _stagingRing;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private readonly AdvancedGiTransientBufferArena _transientArena;
    private readonly SimpleDdgiGuidingSourceCacheSidecar _sourceCacheSidecar;
    private readonly SimpleDdgiGuidingVulkanRuntime _runtime;
    private readonly PendingFrame?[] _frames =
        new PendingFrame?[RenderingConstants.FramesInFlight];

    private SimpleDdgiGuidingFrameConfiguration _configuration;
    private AdvancedGiTransientBufferSlice _workspace;
    private SimpleDdgiGuidingWorkloadPlanner? _workloadPlanner;
    private SimpleDdgiGuidingProposalEpochController? _epochController;
    private SimpleDdgiGuidingFrameProbe[] _frameProbes = [];
    private SimpleDdgiGuidingFrameProbe[] _selectedProbes = [];
    private GPUSimpleDdgiGuidingTrainingWorkItem[] _trainingWorkItems = [];
    private GPUSimpleDdgiGuidingBuildWorkItem[] _buildWorkItems = [];
    private SimpleDdgiGuidingExpectedProbeHeader[] _expectedHeaders = [];
    private GPUSimpleDdgiGuidingSampleRequest[] _sampleRequests = [];
    private SimpleDdgiGuidingSampleCommit[] _sampleCommits = [];
    private ulong _publishedSourceSignature;
    private bool _disposed;

    public SimpleDdgiGuidingFrameCoordinator(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing,
        SimpleDdgiVolumeManager volumeManager,
        AdvancedGiTransientBufferArena transientArena,
        SimpleDdgiGuidingSourceCacheSidecar sourceCacheSidecar,
        SimpleDdgiGuidingVulkanRuntime runtime)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
        _volumeManager = volumeManager ??
            throw new ArgumentNullException(nameof(volumeManager));
        _transientArena = transientArena ??
            throw new ArgumentNullException(nameof(transientArena));
        _sourceCacheSidecar = sourceCacheSidecar ??
            throw new ArgumentNullException(nameof(sourceCacheSidecar));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Diagnostics = SimpleDdgiGuidingFrameCoordinatorDiagnostics.Disabled;
    }

    public SimpleDdgiGuidingFrameCoordinatorDiagnostics Diagnostics { get; private set; }

    public bool IsConfigured
    {
        get
        {
            lock (_sync)
                return !_disposed && _configuration.IsEnabled &&
                    _workloadPlanner is not null && _workspace.IsValid;
        }
    }

    /// <summary>
    /// Returns exact owned Vulkan ranges only for one completely configured
    /// C3 transaction domain. A partial view is never exposed to graph
    /// planning because that could transfer ownership for stale allocations.
    /// </summary>
    public bool TryGetGraphResourceSnapshot(
        out SimpleDdgiGuidingGraphResourceSnapshot snapshot)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            SimpleDdgiGuidingSourceCacheSnapshot source =
                _sourceCacheSidecar.Snapshot;
            if (!_configuration.IsEnabled || !_workspace.IsValid ||
                !source.IsReady ||
                !_runtime.TryGetDistributionResourceSnapshot(
                    out SimpleDdgiGuidingDistributionResourceSnapshot distributions))
            {
                snapshot = default;
                return false;
            }

            snapshot = new SimpleDdgiGuidingGraphResourceSnapshot(
                distributions,
                _workspace.Buffer,
                _workspace.Offset,
                _workspace.Bytes,
                _workspace.ArenaGeneration,
                source.Buffer,
                source.Layout.AllocatedBytes,
                source.ResourceGeneration);
            return snapshot.IsComplete;
        }
    }

    public bool TryConfigure(
        in SimpleDdgiGuidingFrameConfiguration configuration,
        CommandBuffer commandBuffer,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.",
                    nameof(commandBuffer));
            if (HasPendingFrameNoLock())
            {
                reason = "guiding-safe-transition-has-pending-frame";
                return false;
            }

            if (!configuration.IsEnabled)
            {
                _ = _runtime.TryConfigure(
                    new SimpleDdgiGuidingRuntimeRequest(false, default),
                    configuration.GlobalPrerequisiteGateAdmitted,
                    SimpleDdgiGuidingSourceCacheHandshake.Unavailable,
                    out _);
                _ = _sourceCacheSidecar.TryReconcile(
                    SimpleDdgiGuidingSourceCacheLayout.Disabled,
                    commandBuffer,
                    out _);
                ResetNoLock();
                reason = "directional-guiding-disabled";
                return true;
            }
            if (!TryValidateConfiguration(configuration, out reason))
                return false;
            if (_configuration.Equals(configuration) &&
                _sourceCacheSidecar.Snapshot.IsReady && _workspace.IsValid)
            {
                reason = "guiding-frame-coordinator-configuration-reused";
                return true;
            }

            SimpleDdgiGuidingLayout layout = configuration.RuntimeRequest.Layout;
            if (!_transientArena.TryGetSlice(
                    SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                    layout.TransientWorkspace.TotalBytes,
                    layout.StorageAlignmentBytes,
                    out AdvancedGiTransientBufferSlice workspace,
                    out reason))
            {
                return false;
            }
            if (!_sourceCacheSidecar.TryReconcile(
                    configuration.SourceCacheLayout,
                    commandBuffer,
                    out reason))
            {
                return false;
            }
            SimpleDdgiGuidingSourceCacheHandshake handshake =
                _sourceCacheSidecar.CreateHandshake();
            if (!_runtime.TryConfigure(
                    configuration.RuntimeRequest,
                    configuration.GlobalPrerequisiteGateAdmitted,
                    handshake,
                    out reason))
            {
                _ = _sourceCacheSidecar.TryReconcile(
                    SimpleDdgiGuidingSourceCacheLayout.Disabled,
                    commandBuffer,
                    out _);
                return false;
            }

            try
            {
                int scheduled = layout.ScheduledGuidedProbeCapacity;
                int sampleCapacity = checked(
                    scheduled * layout.DirectionSlotsPerProbe);
                _frameProbes = new SimpleDdgiGuidingFrameProbe[
                    Math.Max(_volumeManager.PhysicalProbeCapacity, scheduled)];
                _selectedProbes = new SimpleDdgiGuidingFrameProbe[scheduled];
                _trainingWorkItems =
                    new GPUSimpleDdgiGuidingTrainingWorkItem[scheduled];
                _buildWorkItems = new GPUSimpleDdgiGuidingBuildWorkItem[scheduled];
                _expectedHeaders =
                    new SimpleDdgiGuidingExpectedProbeHeader[scheduled];
                _sampleRequests =
                    new GPUSimpleDdgiGuidingSampleRequest[sampleCapacity];
                _sampleCommits = new SimpleDdgiGuidingSampleCommit[scheduled];
                _workloadPlanner = new SimpleDdgiGuidingWorkloadPlanner(
                    layout,
                    configuration.ProposalPolicy);
                _epochController = new SimpleDdgiGuidingProposalEpochController(
                    configuration.ProposalPolicy);
            }
            catch
            {
                _runtime.AbortBuild("guiding-coordinator-cpu-allocation-failed");
                throw;
            }

            _configuration = configuration;
            _workspace = workspace;
            _publishedSourceSignature = 0UL;
            Diagnostics = new(
                true, false, false, false, false, false, 0UL, 0, 0u, 0,
                0UL, workspace.Bytes, "configured");
            reason = "guiding-frame-coordinator-configured";
            return true;
        }
    }

    /// <summary>
    /// Compiles deterministic work and records only transfer uploads. The
    /// trace-completion witness is intentionally attached later by the Train
    /// graph node, after the ordinary DDGI trace has actually executed.
    /// </summary>
    public bool TryPrepareFrame(
        int frameIndex,
        ulong frameSerial,
        CommandBuffer commandBuffer,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_configuration.IsEnabled || _workloadPlanner is null ||
                _epochController is null || !_workspace.IsValid)
            {
                reason = "guiding-frame-coordinator-not-configured";
                return false;
            }
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.",
                    nameof(commandBuffer));
            if (_frames[frameIndex] is not null)
            {
                reason = "guiding-frame-slot-not-fence-reclaimed";
                return false;
            }
            if (_volumeManager.RaysPerProbe !=
                    _configuration.RuntimeRequest.Layout.DirectionSlotsPerProbe)
            {
                reason = "guiding-frame-ddgi-ray-layout-incompatible";
                return false;
            }
            if (_volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                return TryPrepareGpuResidentFrameNoLock(
                    frameIndex,
                    frameSerial,
                    commandBuffer,
                    out reason);
            }
            if (!_volumeManager.TryCopyGuidingFrameProbes(
                    _frameProbes,
                    out int frameProbeCount,
                    out reason))
            {
                return false;
            }

            for (int index = 0; index < frameProbeCount; index++)
            {
                SimpleDdgiGuidingFrameProbe probe = _frameProbes[index];
                if (_runtime.TryGetReadableProbeIdentity(
                        probe.PhysicalProbeIndex,
                        probe.VirtualProbeId,
                        probe.PageGeneration,
                        out SimpleDdgiGuidingReadableProbeIdentity readable))
                {
                    _frameProbes[index] = probe with { ReadableGuide = readable };
                }
            }

            ulong sourceSignature = ComputeSourceSignature(
                _frameProbes.AsSpan(0, frameProbeCount));
            float totalVariationWitness = _publishedSourceSignature != 0UL &&
                sourceSignature != _publishedSourceSignature
                    ? 1.0f
                    : 0.0f;
            if (!_epochController.TryPlan(
                    frameSerial,
                    totalVariationWitness,
                    out SimpleDdgiGuidingProposalEpochPlan epochPlan,
                    out reason))
            {
                return false;
            }
            if (!_runtime.TryReserveBuild(
                    epochPlan.TargetEpoch,
                    out SimpleDdgiGuidingBuildToken token,
                    out SimpleDdgiGuidingLayout runtimeLayout,
                    out reason))
            {
                _epochController.Abort(epochPlan);
                return false;
            }
            if (!runtimeLayout.Equals(_configuration.RuntimeRequest.Layout))
            {
                _runtime.AbortReservedBuild(token,
                    "guiding-runtime-layout-changed-during-prepare");
                _epochController.Abort(epochPlan);
                reason = "guiding-runtime-layout-changed-during-prepare";
                return false;
            }

            SimpleDdgiGuidingWorkloadCompileResult compiled =
                _workloadPlanner.TryCompile(
                    token,
                    _frameProbes.AsSpan(0, frameProbeCount),
                    _selectedProbes,
                    _trainingWorkItems,
                    _buildWorkItems,
                    _expectedHeaders,
                    _sampleRequests,
                    _sampleCommits);
            if (!compiled.Compiled)
            {
                _runtime.AbortReservedBuild(token, compiled.Reason);
                _epochController.Abort(epochPlan);
                reason = compiled.Reason;
                return false;
            }

            try
            {
                PendingFrame pending = CreateAndUploadFrameNoLock(
                    frameIndex,
                    frameSerial,
                    commandBuffer,
                    token,
                    epochPlan,
                    sourceSignature,
                    compiled.Counts);
                if (compiled.Counts.SampleRequestCount > 0 &&
                    !_sourceCacheSidecar.TryPublishForSampling(out reason))
                {
                    _runtime.AbortReservedBuild(token, reason);
                    _epochController.Abort(epochPlan);
                    return false;
                }
                _frames[frameIndex] = pending;
                UpdateDiagnosticsNoLock(pending, "prepared");
                reason = "guiding-frame-workloads-prepared";
                return true;
            }
            catch (Exception exception)
            {
                reason = "guiding-frame-workload-upload-failed:" +
                    exception.GetType().Name;
                _runtime.AbortReservedBuild(token, reason);
                _epochController.Abort(epochPlan);
                return false;
            }
        }
    }

    public bool CanExecuteSample(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
            return !_disposed && _frames[frameIndex] is { } frame &&
                frame.Counts.SampleRequestCount > 0 && !frame.SampleRecorded &&
                _runtime.Diagnostics.Resource.HasReadableDistribution;
    }

    public bool CanExecuteTrain(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
            return !_disposed && _frames[frameIndex] is { BuildFailed: false,
                TrainRecorded: false };
    }

    public bool CanExecuteHierarchyBuild(int frameIndex) =>
        _runtime.CanRecordHierarchyBuildStage(frameIndex);

    public bool CanExecuteValidate(int frameIndex) =>
        _runtime.CanRecordValidateStage(frameIndex);

    public bool TryRecordSample(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_frames[frameIndex] is not { } frame ||
                frame.Counts.SampleRequestCount <= 0 || frame.SampleRecorded)
            {
                reason = "guiding-sample-frame-not-prepared";
                return false;
            }
            SimpleDdgiGuidingSourceCacheHandshake handshake =
                _sourceCacheSidecar.CreateHandshake();
            if (!_runtime.TryRecordSample(
                    commandBuffer,
                    frameIndex,
                    handshake,
                    frame.SampleWorkload,
                    out reason))
            {
                frame.SampleFailed = true;
                UpdateDiagnosticsNoLock(frame, reason);
                return false;
            }
            frame.SampleRecorded = true;
            UpdateDiagnosticsNoLock(frame, reason);
            return true;
        }
    }

    public bool TryRecordTrain(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_frames[frameIndex] is not { } frame || frame.BuildFailed ||
                frame.TrainRecorded)
            {
                reason = "guiding-train-frame-not-prepared";
                return false;
            }
            SimpleDdgiGuidingBuildWorkload workload = frame.BuildWorkload with
            {
                TraceTrainingSource = CreateTraceTrainingSourceNoLock()
            };
            if (!_runtime.TryPrepareBuildFrame(
                    frameIndex,
                    _sourceCacheSidecar.CreateHandshake(),
                    workload,
                    out reason) ||
                !_runtime.TryRecordTrainStage(
                    commandBuffer,
                    frameIndex,
                    out reason))
            {
                FailBuildNoLock(frame, reason);
                return false;
            }
            frame.BuildWorkload = workload;
            frame.TrainRecorded = true;
            UpdateDiagnosticsNoLock(frame, reason);
            return true;
        }
    }

    public bool TryRecordHierarchyBuild(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_frames[frameIndex] is not { } frame || frame.BuildFailed ||
                !frame.TrainRecorded || frame.BuildRecorded)
            {
                reason = "guiding-hierarchy-build-frame-order-invalid";
                return false;
            }
            if (!_runtime.TryRecordHierarchyBuildStage(
                    commandBuffer, frameIndex, out reason))
            {
                FailBuildNoLock(frame, reason);
                return false;
            }
            frame.BuildRecorded = true;
            UpdateDiagnosticsNoLock(frame, reason);
            return true;
        }
    }

    public bool TryRecordValidate(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_frames[frameIndex] is not { } frame || frame.BuildFailed ||
                !frame.BuildRecorded || frame.ValidateRecorded)
            {
                reason = "guiding-validate-frame-order-invalid";
                return false;
            }
            if (!_runtime.TryRecordValidateStage(
                    commandBuffer, frameIndex, out reason))
            {
                FailBuildNoLock(frame, reason);
                return false;
            }
            frame.ValidateRecorded = true;
            UpdateDiagnosticsNoLock(frame, reason);
            return true;
        }
    }

    /// <summary>Consumes only the ring slot whose frame fence is already complete.</summary>
    public void CompleteFrameAfterFence(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            if (_disposed)
                return;
            PendingFrame? frame = _frames[frameIndex];
            bool consumed = _runtime.TryReadCompletedFrame(
                frameIndex,
                out SimpleDdgiGuidingPublicationResult publication,
                out SimpleDdgiGuidingSampleCompletion sampleCompletion);
            if (frame is null)
                return;

            string state;
            if (frame.ValidateRecorded && consumed && publication.Published)
            {
                if (!_epochController!.Commit(frame.EpochPlan, out state))
                    state = "guiding-proposal-epoch-commit-failed";
                else
                    _publishedSourceSignature = frame.SourceSignature;
            }
            else
            {
                _runtime.AbortReservedBuild(
                    frame.BuildToken,
                    publication.Reason);
                _epochController!.Abort(frame.EpochPlan);
                state = frame.BuildFailed
                    ? "guiding-build-failed"
                    : "guiding-build-not-published";
            }

            if (frame.SampleRecorded && !frame.BuildWorkload.UsesGpuResidentWork)
            {
                bool committed = _workloadPlanner!.TryCommitSamples(
                    sampleCompletion.Commits.Span,
                    sampleCompletion.FenceCompleted,
                    sampleCompletion.ReadbackValid &&
                        sampleCompletion.ValidationCounters.AreZero,
                    out string sampleState);
                if (!committed)
                    state += ";" + sampleState;
            }
            _frames[frameIndex] = null;
            Diagnostics = Diagnostics with
            {
                FramePrepared = false,
                CompletedFrameSerial = frame.FrameSerial,
                SampleReadbackValid = frame.SampleRecorded &&
                    sampleCompletion.FenceCompleted &&
                    sampleCompletion.ReadbackValid,
                CompletedSampleCount = frame.SampleRecorded &&
                    sampleCompletion.ReadbackValid
                        ? checked((int)sampleCompletion.Telemetry.RequestCount)
                        : 0,
                SampleValidationCounters = frame.SampleRecorded
                    ? sampleCompletion.ValidationCounters
                    : default,
                SampleTelemetry = frame.SampleRecorded
                    ? sampleCompletion.Telemetry
                    : SimpleDdgiGuidingSampleTelemetry.Empty,
                DistributionPublicationSucceeded =
                    frame.ValidateRecorded && publication.Published,
                State = state
            };
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _runtime.AbortBuild("guiding-frame-coordinator-disposed");
            Array.Clear(_frames);
            ResetNoLock();
            _disposed = true;
            Diagnostics = SimpleDdgiGuidingFrameCoordinatorDiagnostics.Disabled
                with { State = "disposed" };
        }
    }

    private PendingFrame CreateAndUploadFrameNoLock(
        int frameIndex,
        ulong frameSerial,
        CommandBuffer commandBuffer,
        in SimpleDdgiGuidingBuildToken token,
        in SimpleDdgiGuidingProposalEpochPlan epochPlan,
        ulong sourceSignature,
        in SimpleDdgiGuidingWorkloadCounts counts)
    {
        SimpleDdgiGuidingLayout layout = _configuration.RuntimeRequest.Layout;
        SimpleDdgiGuidingTransientWorkspace map = layout.TransientWorkspace;
        ulong baseOffset = _workspace.Offset;
        BufferHandle buffer = _workspace.Buffer;
        ulong uploadedBytes = 0UL;

        uploadedBytes = checked(uploadedBytes + Upload(
            commandBuffer,
            buffer,
            checked(baseOffset + map.TrainingWorkItemsOffsetBytes),
            _trainingWorkItems.AsSpan(0, counts.TrainingWorkItemCount)));
        uploadedBytes = checked(uploadedBytes + Upload(
            commandBuffer,
            buffer,
            checked(baseOffset + map.BuildWorkItemsOffsetBytes),
            _buildWorkItems.AsSpan(0, counts.BuildWorkItemCount)));
        if (counts.SampleRequestCount > 0)
        {
            uploadedBytes = checked(uploadedBytes + Upload(
                commandBuffer,
                buffer,
                checked(baseOffset + map.SampleRequestsOffsetBytes),
                _sampleRequests.AsSpan(0, counts.SampleRequestCount)));
        }

        var records = CreateArenaRange(
            map.TrainingRecordsOffsetBytes,
            checked((ulong)counts.TrainingRecordCount *
                SimpleDdgiGuidingGpuAbi.TrainingRecordByteCount),
            counts.TrainingRecordCount,
            SimpleDdgiGuidingGpuAbi.TrainingRecordByteCount);
        var trainingItems = CreateArenaRange(
            map.TrainingWorkItemsOffsetBytes,
            checked((ulong)counts.TrainingWorkItemCount *
                SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount),
            checked((uint)counts.TrainingWorkItemCount),
            SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount);
        var buildItems = CreateArenaRange(
            map.BuildWorkItemsOffsetBytes,
            checked((ulong)counts.BuildWorkItemCount *
                SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount),
            checked((uint)counts.BuildWorkItemCount),
            SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount);
        var counters = CreateArenaRange(
            map.ValidationCountersOffsetBytes,
            map.ValidationCountersBytes,
            SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount,
            sizeof(uint));

        var buildWorkload = new SimpleDdgiGuidingBuildWorkload(
            token.TargetProposalEpoch,
            records,
            trainingItems,
            buildItems,
            counters,
            _expectedHeaders.AsMemory(0, counts.ExpectedHeaderCount));
        SimpleDdgiGuidingSampleWorkload sampleWorkload = default;
        if (counts.SampleRequestCount > 0)
        {
            sampleWorkload = new SimpleDdgiGuidingSampleWorkload(
                CreateArenaRange(
                    map.SampleRequestsOffsetBytes,
                    checked((ulong)counts.SampleRequestCount *
                        SimpleDdgiGuidingGpuAbi.SampleRequestByteCount),
                    checked((uint)counts.SampleRequestCount),
                    SimpleDdgiGuidingGpuAbi.SampleRequestByteCount),
                counters)
            {
                DestinationsAreUnique = true,
                ExpectedCommits = _sampleCommits.AsMemory(
                    0,
                    counts.SampleCommitCount)
            };
        }

        return new PendingFrame(
            frameIndex,
            frameSerial,
            token,
            epochPlan,
            sourceSignature,
            counts,
            buildWorkload,
            sampleWorkload,
            uploadedBytes);
    }

    private bool TryPrepareGpuResidentFrameNoLock(
        int frameIndex,
        ulong frameSerial,
        CommandBuffer commandBuffer,
        out string reason)
    {
        if (!_volumeManager.TryGetGuidingGpuResidentWorkSource(
                out SimpleDdgiGuidingGpuResidentWorkSource source,
                out reason))
        {
            return false;
        }

        SimpleDdgiGuidingLayout layout = _configuration.RuntimeRequest.Layout;
        if (!source.TryValidate(layout, out reason))
            return false;

        ulong sourceSignature = source.SceneContentRevision;
        float variation = _publishedSourceSignature != 0UL &&
            _publishedSourceSignature != sourceSignature
                ? 1.0f
                : 0.0f;
        if (!_epochController!.TryPlan(
                frameSerial,
                variation,
                out SimpleDdgiGuidingProposalEpochPlan epochPlan,
                out reason))
        {
            return false;
        }
        if (!_runtime.TryReserveBuild(
                epochPlan.TargetEpoch,
                out SimpleDdgiGuidingBuildToken token,
                out SimpleDdgiGuidingLayout runtimeLayout,
                out reason))
        {
            _epochController.Abort(epochPlan);
            return false;
        }
        if (!runtimeLayout.Equals(layout))
        {
            _runtime.AbortReservedBuild(
                token,
                "guiding-runtime-layout-changed-during-gpu-prepare");
            _epochController.Abort(epochPlan);
            reason = "guiding-runtime-layout-changed-during-gpu-prepare";
            return false;
        }

        try
        {
            int scheduled = layout.ScheduledGuidedProbeCapacity;
            int sampleCapacity = checked(
                scheduled * layout.DirectionSlotsPerProbe);
            uint recordCapacity = checked((uint)sampleCapacity);
            SimpleDdgiGuidingTransientWorkspace map = layout.TransientWorkspace;
            var records = CreateArenaRange(
                map.TrainingRecordsOffsetBytes,
                checked((ulong)recordCapacity *
                    SimpleDdgiGuidingGpuAbi.TrainingRecordByteCount),
                recordCapacity,
                SimpleDdgiGuidingGpuAbi.TrainingRecordByteCount,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
            var trainingItems = CreateArenaRange(
                map.TrainingWorkItemsOffsetBytes,
                checked((ulong)scheduled *
                    SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount),
                checked((uint)scheduled),
                SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
            var buildItems = CreateArenaRange(
                map.BuildWorkItemsOffsetBytes,
                checked((ulong)scheduled *
                    SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount),
                checked((uint)scheduled),
                SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
            var counters = CreateArenaRange(
                map.ValidationCountersOffsetBytes,
                map.ValidationCountersBytes,
                SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount,
                sizeof(uint),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
            SimpleDdgiGuidingTraceTrainingSource traceSource =
                CreateTraceTrainingSourceNoLock();
            var buildWorkload = new SimpleDdgiGuidingBuildWorkload(
                token.TargetProposalEpoch,
                records,
                trainingItems,
                buildItems,
                counters,
                ReadOnlyMemory<SimpleDdgiGuidingExpectedProbeHeader>.Empty)
            {
                TraceTrainingSource = traceSource,
                GpuResidentSource = source
            };
            var sampleWorkload = new SimpleDdgiGuidingSampleWorkload(
                CreateArenaRange(
                    map.SampleRequestsOffsetBytes,
                    checked((ulong)sampleCapacity *
                        SimpleDdgiGuidingGpuAbi.SampleRequestByteCount),
                    checked((uint)sampleCapacity),
                    SimpleDdgiGuidingGpuAbi.SampleRequestByteCount,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit),
                counters)
            {
                DestinationsAreUnique = true,
                ExpectedCommits =
                    ReadOnlyMemory<SimpleDdgiGuidingSampleCommit>.Empty,
                GpuResidentSource = source,
                UniformMixtureFraction =
                    _configuration.ProposalPolicy.UniformMixtureFraction,
                TraceTrainingSource = traceSource,
                TrainingWorkItems = trainingItems,
                BuildWorkItems = buildItems
            };
            var counts = new SimpleDdgiGuidingWorkloadCounts(
                scheduled,
                recordCapacity,
                scheduled,
                scheduled,
                0,
                sampleCapacity,
                0,
                0,
                0);
            if (!buildWorkload.TryValidate(layout, out reason) ||
                !sampleWorkload.TryValidate(
                    _sourceCacheSidecar.CreateHandshake(),
                    out reason) ||
                !_sourceCacheSidecar.TryPublishForSampling(out reason))
            {
                _runtime.AbortReservedBuild(token, reason);
                _epochController.Abort(epochPlan);
                return false;
            }

            var pending = new PendingFrame(
                frameIndex,
                frameSerial,
                token,
                epochPlan,
                sourceSignature,
                counts,
                buildWorkload,
                sampleWorkload,
                0UL);
            _frames[frameIndex] = pending;
            UpdateDiagnosticsNoLock(pending, "prepared-gpu-resident");
            reason = "guiding-gpu-resident-workloads-prepared";
            return true;
        }
        catch (Exception exception)
        {
            reason = "guiding-gpu-resident-workload-prepare-failed:" +
                exception.GetType().Name;
            _runtime.AbortReservedBuild(token, reason);
            _epochController.Abort(epochPlan);
            return false;
        }
    }

    private SimpleDdgiGuidingTraceTrainingSource CreateTraceTrainingSourceNoLock()
    {
        SimpleDdgiStoragePackingMode packing =
            _configuration.RuntimeRequest.SourceStoragePackingMode;
        uint rayStride = packing == SimpleDdgiStoragePackingMode.Packed
            ? 20u
            : 32u;
        ulong paramsBytes = _bufferManager.GetBufferSize(_volumeManager.ParamsBuffer);
        ulong rayBytes = _volumeManager.RayScratchBytes;
        ulong queueBytes = _volumeManager.ProbeUpdateQueueBytes;
        return new SimpleDdgiGuidingTraceTrainingSource(
            IsAvailable: true,
            TraceDispatchCompleted: true,
            packing,
            (uint)BindlessIndex.SimpleDdgiParamsBuffer,
            (uint)BindlessIndex.SimpleDdgiRayResultScratchBuffer,
            (uint)BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
            new SimpleDdgiGuidingExternalBuffer(
                _volumeManager.ParamsBuffer,
                0UL,
                paramsBytes,
                ToElementCount(paramsBytes, sizeof(uint)),
                sizeof(uint),
                PipelineStageFlags2.TransferBit,
                TransferWriteAccess),
            new SimpleDdgiGuidingExternalBuffer(
                _volumeManager.RayResultScratchBuffer,
                0UL,
                rayBytes,
                ToElementCount(rayBytes, rayStride),
                rayStride,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit),
            new SimpleDdgiGuidingExternalBuffer(
                _volumeManager.ProbeUpdateQueueBuffer,
                0UL,
                queueBytes,
                ToElementCount(
                    queueBytes,
                    checked((uint)SimpleDdgiMemoryPlan.ProbeUpdateBytes)),
                checked((uint)SimpleDdgiMemoryPlan.ProbeUpdateBytes),
                PipelineStageFlags2.TransferBit |
                    PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.TransferWriteBit |
                    AccessFlags2.ShaderStorageWriteBit));
    }

    private SimpleDdgiGuidingExternalBuffer CreateArenaRange(
        ulong relativeOffset,
        ulong bytes,
        uint elementCount,
        uint stride,
        PipelineStageFlags2 lastWriterStage = PipelineStageFlags2.TransferBit,
        AccessFlags2 lastWriterAccess = TransferWriteAccess)
    {
        ulong absoluteOffset = checked(_workspace.Offset + relativeOffset);
        ulong end = checked(absoluteOffset + bytes);
        ulong workspaceEnd = checked(_workspace.Offset + _workspace.Bytes);
        if (bytes == 0UL || end > workspaceEnd)
            throw new InvalidOperationException("C3 frame range exceeds its transient workspace.");
        return new SimpleDdgiGuidingExternalBuffer(
            _workspace.Buffer,
            absoluteOffset,
            bytes,
            elementCount,
            stride,
            lastWriterStage,
            lastWriterAccess);
    }

    private ulong Upload<T>(
        CommandBuffer commandBuffer,
        BufferHandle destination,
        ulong destinationOffset,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        UploadResult result = GpuBufferUploader.UploadSpanToBuffer(
            _context,
            _bufferManager,
            _stagingRing,
            commandBuffer,
            destination,
            values,
            destinationOffset);
        if (!result.Recorded)
            throw new InvalidOperationException("C3 nonempty workload upload was not recorded.");
        return result.ByteCount;
    }

    private void FailBuildNoLock(PendingFrame frame, string reason)
    {
        frame.BuildFailed = true;
        _runtime.AbortReservedBuild(frame.BuildToken, reason);
        _epochController?.Abort(frame.EpochPlan);
        UpdateDiagnosticsNoLock(frame, reason);
    }

    private void UpdateDiagnosticsNoLock(PendingFrame frame, string state)
    {
        Diagnostics = new(
            true,
            true,
            frame.SampleRecorded,
            frame.TrainRecorded,
            frame.BuildRecorded,
            frame.ValidateRecorded,
            frame.FrameSerial,
            frame.Counts.GuidedProbeCount,
            frame.Counts.TrainingRecordCount,
            frame.Counts.SampleRequestCount,
            frame.UploadedBytes,
            _workspace.Bytes,
            string.IsNullOrWhiteSpace(state) ? "unknown" : state.Trim());
    }

    private bool TryValidateConfiguration(
        in SimpleDdgiGuidingFrameConfiguration configuration,
        out string reason)
    {
        try
        {
            SimpleDdgiGuidingLayout layout = configuration.RuntimeRequest.Layout;
            configuration.ProposalPolicy.Validate();
            bool valid = layout.AbiVersion == SimpleDdgiGuidingGpuAbi.Version &&
                layout.HasTransportSidecar && layout.TransientWorkspace.IsComplete &&
                layout.PhysicalProbeCapacity ==
                    configuration.SourceCacheLayout.AdmittedGuidedPhysicalProbeCapacity &&
                layout.DirectionSlotsPerProbe ==
                    configuration.SourceCacheLayout.DirectionSlotsPerProbe &&
                layout.DirectionPayloadCapacity ==
                    configuration.SourceCacheLayout.PayloadCapacity &&
                layout.DirectionPdfSidecarBytes ==
                    configuration.SourceCacheLayout.AllocatedBytes &&
                layout.DirectionPayloadCapacity <= int.MaxValue &&
                layout.DirectionSlotsPerProbe == _volumeManager.RaysPerProbe;
            reason = valid
                ? string.Empty
                : "guiding-frame-configuration-layout-mismatch";
            return valid;
        }
        catch (Exception exception) when (exception is ArgumentException or
            OverflowException)
        {
            reason = "guiding-frame-configuration-invalid:" +
                exception.GetType().Name;
            return false;
        }
    }

    private bool HasPendingFrameNoLock()
    {
        foreach (PendingFrame? frame in _frames)
        {
            if (frame is not null)
                return true;
        }
        return false;
    }

    private void ResetNoLock()
    {
        _configuration = default;
        _workspace = default;
        _workloadPlanner = null;
        _epochController = null;
        _frameProbes = [];
        _selectedProbes = [];
        _trainingWorkItems = [];
        _buildWorkItems = [];
        _expectedHeaders = [];
        _sampleRequests = [];
        _sampleCommits = [];
        _publishedSourceSignature = 0UL;
        Diagnostics = SimpleDdgiGuidingFrameCoordinatorDiagnostics.Disabled;
    }

    private static uint ToElementCount(ulong bytes, uint stride)
    {
        if (stride == 0u || bytes < stride || bytes % stride != 0UL)
            throw new InvalidOperationException("C3 external buffer stride is invalid.");
        return checked((uint)(bytes / stride));
    }

    private static ulong ComputeSourceSignature(
        ReadOnlySpan<SimpleDdgiGuidingFrameProbe> probes)
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        ulong hash = Offset;
        foreach (ref readonly SimpleDdgiGuidingFrameProbe probe in probes)
        {
            Mix(ref hash, probe.VirtualProbeId, Prime);
            Mix(ref hash, probe.PhysicalProbeIndex, Prime);
            Mix(ref hash, probe.PageGeneration, Prime);
            Mix(ref hash, probe.SourceEpoch, Prime);
            Mix(ref hash, probe.SourceLightingGeneration, Prime);
            Mix(ref hash, probe.ContentRevision, Prime);
        }
        return hash == 0UL ? 1UL : hash;
    }

    private static void Mix(ref ulong hash, uint value, ulong prime)
    {
        hash ^= value;
        hash *= prime;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PendingFrame
    {
        public PendingFrame(
            int frameIndex,
            ulong frameSerial,
            SimpleDdgiGuidingBuildToken buildToken,
            SimpleDdgiGuidingProposalEpochPlan epochPlan,
            ulong sourceSignature,
            SimpleDdgiGuidingWorkloadCounts counts,
            SimpleDdgiGuidingBuildWorkload buildWorkload,
            SimpleDdgiGuidingSampleWorkload sampleWorkload,
            ulong uploadedBytes)
        {
            FrameIndex = frameIndex;
            FrameSerial = frameSerial;
            BuildToken = buildToken;
            EpochPlan = epochPlan;
            SourceSignature = sourceSignature;
            Counts = counts;
            BuildWorkload = buildWorkload;
            SampleWorkload = sampleWorkload;
            UploadedBytes = uploadedBytes;
        }

        public int FrameIndex { get; }
        public ulong FrameSerial { get; }
        public SimpleDdgiGuidingBuildToken BuildToken { get; }
        public SimpleDdgiGuidingProposalEpochPlan EpochPlan { get; }
        public ulong SourceSignature { get; }
        public SimpleDdgiGuidingWorkloadCounts Counts { get; }
        public SimpleDdgiGuidingBuildWorkload BuildWorkload { get; set; }
        public SimpleDdgiGuidingSampleWorkload SampleWorkload { get; }
        public ulong UploadedBytes { get; }
        public bool SampleRecorded { get; set; }
        public bool SampleFailed { get; set; }
        public bool TrainRecorded { get; set; }
        public bool BuildRecorded { get; set; }
        public bool ValidateRecorded { get; set; }
        public bool BuildFailed { get; set; }
    }
}
