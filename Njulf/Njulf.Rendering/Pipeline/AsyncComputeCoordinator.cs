using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

internal sealed class AsyncComputeCoordinator
{
    private readonly RenderGraph _renderGraph;
    private readonly AsyncComputeScheduler _scheduler = new();
    private readonly AsyncComputeTimingPolicy _timingPolicy = new();
    private readonly AsyncComputeTimingFrame?[] _timingFrames;
    private readonly AsyncComputeResourceStateProjection _stateProjection =
        new(4096);
    private readonly AsyncComputePlanVariantCache _planVariantCache = new(16);
    private readonly AsyncComputeValidationLedger _validationLedger =
        new(32, 64);
    private readonly AsyncComputeRecoverablePlanRetryGate _recoverableRetryGate =
        new();
    private readonly List<AsyncComputeTimelineWait> _terminalWaits = new();

    private bool _planRecordedThisFrame;
    private int _submittedGraphicsSegmentsThisFrame;
    private int _submittedComputeSegmentsThisFrame;
    private int _ownershipTransferCountThisFrame;
    private int _plannedReleaseBarrierCountThisFrame;
    private int _plannedAcquireBarrierCountThisFrame;
    private int _emittedReleaseBarrierCountThisFrame;
    private int _emittedAcquireBarrierCountThisFrame;
    private long _barrierRecordMicrosecondsThisFrame;
    private ulong _transferredBytesThisFrame;
    private int _transferredImageSubresourcesThisFrame;
    private long _lastAsyncSubmitMicroseconds;
    private ulong _nextTimelineValue = 1UL;
    private ulong _timelineOffsetThisFrame;
    private int _nextAutoTimingProbePath;
    private AsyncComputeMode? _lastTimingMode;
    private bool _emergencyFallbackLatched;
    private string _lastFallbackReason = string.Empty;
    private int _validationFallbackCount;
    private int _validationErrorCountAtPreviousFrameBoundary;
    private ulong _currentSettingsSignature;

    internal AsyncComputeCoordinator(RenderGraph renderGraph, int framesInFlight)
    {
        _renderGraph = renderGraph ??
            throw new ArgumentNullException(nameof(renderGraph));
        if (framesInFlight <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesInFlight));

        _timingFrames = new AsyncComputeTimingFrame?[framesInFlight];
    }

    internal AsyncComputeFramePlan? CurrentPlan { get; private set; }

    internal IReadOnlyList<AsyncComputeTimelineWait> TerminalWaits =>
        _terminalWaits;

    internal bool RequiresConcreteResourceBindings(
        AsyncComputeMode requestedMode,
        bool independentQueueAvailable,
        bool timelineSemaphoreAvailable)
    {
        AsyncComputeMode effectiveMode = _emergencyFallbackLatched
            ? AsyncComputeMode.Disabled
            : requestedMode;
        return independentQueueAvailable &&
               timelineSemaphoreAvailable &&
               effectiveMode != AsyncComputeMode.Disabled;
    }

    internal void ConsumeCompletedTiming(
        int frameIndex,
        FrameTimingSnapshot timings)
    {
        ValidateFrameIndex(frameIndex);
        ArgumentNullException.ThrowIfNull(timings);

        AsyncComputeTimingFrame? timingFrame = _timingFrames[frameIndex];
        _timingFrames[frameIndex] = null;
        if (timingFrame == null)
            return;

        AsyncComputeSubmissionPlan plan = timingFrame.Plan.SubmissionPlan;
        long totalPassGpuMicroseconds = 0;
        foreach (PassTiming timing in timings.Passes)
        {
            if (timing.GpuAvailable && timing.GpuMicroseconds > 0)
            {
                totalPassGpuMicroseconds = checked(
                    totalPassGpuMicroseconds + timing.GpuMicroseconds);
            }
        }

        long frameGpuMicroseconds = plan.ContainsAsyncCompute
            ? Math.Max(
                0,
                totalPassGpuMicroseconds -
                EstimateOverlapMicroseconds(plan, timings))
            : totalPassGpuMicroseconds;
        if (frameGpuMicroseconds <= 0)
            return;

        double frameMilliseconds = frameGpuMicroseconds / 1000.0;
        if (!plan.ContainsAsyncCompute)
        {
            foreach ((AsyncComputePath _, AsyncComputeTimingKey key) in
                     timingFrame.Keys)
            {
                _timingPolicy.RecordGraphicsOnly(
                    key,
                    frameMilliseconds,
                    timingFrame.CpuSubmitMicroseconds / 1000.0);
            }

            return;
        }

        foreach (AsyncComputePathRuntimeStatus path in plan.Paths)
        {
            if (!path.Active ||
                !timingFrame.Keys.TryGetValue(
                    path.Path,
                    out AsyncComputeTimingKey key))
            {
                continue;
            }

            long dispatchMicroseconds = 0;
            foreach (string passName in path.Passes)
            {
                dispatchMicroseconds = checked(
                    dispatchMicroseconds +
                    timings.GetGpuMicrosecondsOrZero(passName));
            }

            _timingPolicy.RecordAsync(
                key,
                frameMilliseconds,
                dispatchMicroseconds / 1000.0,
                transferBarrierMilliseconds:
                    timingFrame.CpuBarrierRecordMicroseconds / 1000.0,
                graphicsWaitMilliseconds:
                    EstimateFirstConsumerWaitMicroseconds(plan, timings) /
                    1000.0,
                cpuSubmitMilliseconds:
                    timingFrame.CpuSubmitMicroseconds / 1000.0);
        }
    }

    internal void BeginFrame(in AsyncComputeFrameBoundaryInput input)
    {
        _validationLedger.BeginFrame(input.FrameSerial);
        if (!_emergencyFallbackLatched &&
            _submittedComputeSegmentsThisFrame > 0 &&
            input.ValidationErrorCount >
                _validationErrorCountAtPreviousFrameBoundary)
        {
            _validationLedger.RecordError(
                segmentId: -1,
                resourceHandle: 0UL,
                labelId: 0,
                out _);
            LatchEmergencyFallback(
                $"Quarantined after {input.ValidationErrorCount - _validationErrorCountAtPreviousFrameBoundary} " +
                "Vulkan validation error(s) were observed while an async segment was active.");
        }

        _validationErrorCountAtPreviousFrameBoundary =
            input.ValidationErrorCount;
        _planRecordedThisFrame = false;
        _submittedGraphicsSegmentsThisFrame = 0;
        _submittedComputeSegmentsThisFrame = 0;
        _terminalWaits.Clear();
        _ownershipTransferCountThisFrame = 0;
        _plannedReleaseBarrierCountThisFrame = 0;
        _plannedAcquireBarrierCountThisFrame = 0;
        _emittedReleaseBarrierCountThisFrame = 0;
        _emittedAcquireBarrierCountThisFrame = 0;
        _barrierRecordMicrosecondsThisFrame = 0;
        _transferredBytesThisFrame = 0UL;
        _transferredImageSubresourcesThisFrame = 0;
        _timelineOffsetThisFrame = 0UL;
        _lastAsyncSubmitMicroseconds = 0;
        CurrentPlan = null;
    }

    internal AsyncComputeFramePlan PlanFrame(
        in AsyncComputePlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input.Settings);
        ArgumentNullException.ThrowIfNull(input.SceneData);
        ValidateFrameIndex(input.FrameIndex);

        RenderSettings settings = input.Settings;
        SceneRenderingData sceneData = input.SceneData;
        if (sceneData.ScenePayloadRebuilt != 0 ||
            sceneData.HiZPolicySceneChanged != 0 ||
            sceneData.HiZPolicyCameraCut != 0)
        {
            ResetTimingHistory(AsyncComputeTimingResetKind.SceneOrMode);
        }

        AsyncComputeMode requestedMode = settings.AsyncCompute.Mode;
        if (_lastTimingMode != requestedMode)
        {
            ResetTimingHistory(AsyncComputeTimingResetKind.SceneOrMode);
            _lastTimingMode = requestedMode;
        }

        bool supported = input.IndependentQueueAvailable &&
            input.TimelineSemaphoreAvailable;
        if (requestedMode == AsyncComputeMode.Disabled)
        {
            CurrentPlan = CreateDisabledPlan(
                sceneData,
                supported,
                requestedMode);
            return CurrentPlan;
        }

        AsyncComputeMode effectiveMode = _emergencyFallbackLatched
            ? AsyncComputeMode.Disabled
            : requestedMode;
        ulong settingsSignature =
            ComputeSettingsSignature(settings.AsyncCompute);
        _currentSettingsSignature = settingsSignature;

        bool asyncProjectionReady =
            effectiveMode != AsyncComputeMode.Disabled &&
            _stateProjection.Begin(_renderGraph.ConcreteResourceBindings);
        _timelineOffsetThisFrame = effectiveMode != AsyncComputeMode.Disabled
            ? checked(_nextTimelineValue - 1UL)
            : 0UL;
        var retryScope = new AsyncComputePlanRetryScope(
            _renderGraph.ConcreteResourceBindings.Generation,
            settingsSignature);
        ObserveRetryScope(retryScope);
        var capabilities = new AsyncComputeQueueCapabilities(
            supported,
            input.GraphicsQueueFamily,
            input.ComputeQueueFamily,
            input.GraphicsQueueFlags,
            input.ComputeQueueFlags);

        AsyncComputePath[] allPaths = Enum.GetValues<AsyncComputePath>();
        var requestedByFeature =
            new Dictionary<AsyncComputePath, bool>(allPaths.Length);
        var timingDecisions =
            new Dictionary<AsyncComputePath, AsyncComputeTimingDecision>(
                allPaths.Length);
        foreach (AsyncComputePath path in allPaths)
        {
            requestedByFeature.Add(
                path,
                settings.AsyncCompute.IsEnabledBy(path) &&
                RenderFeatureIsolationPolicy.ShouldExecutePass(
                    sceneData.ActiveFeatureIsolation,
                    AsyncComputePassCatalog.GetRepresentativePass(path)) &&
                IsPathFeatureActive(path, input));
            timingDecisions.Add(
                path,
                GetTimingDecision(path, input));
        }

        AsyncComputePath? autoEnabledPath =
            effectiveMode == AsyncComputeMode.Auto
                ? SelectAutoEnabledPath(
                    input,
                    requestedByFeature,
                    timingDecisions)
                : null;
        AsyncComputePath? autoTimingProbe =
            effectiveMode == AsyncComputeMode.Auto &&
            !autoEnabledPath.HasValue
                ? SelectAutoTimingProbe(input, requestedByFeature)
                : null;
        AsyncComputePath? autoIsolatedPath =
            autoEnabledPath ?? autoTimingProbe;
        var paths = new List<AsyncComputePathEligibility>(allPaths.Length);
        foreach (AsyncComputePath path in allPaths)
        {
            bool requested = requestedByFeature[path];
            AsyncComputeTimingDecision timing = timingDecisions[path];
            bool isSelectedAutoPath = autoIsolatedPath == path;
            bool isProbe = autoTimingProbe == path;
            bool pauseForAutoIsolation =
                effectiveMode == AsyncComputeMode.Auto &&
                requested &&
                autoIsolatedPath.HasValue &&
                !isSelectedAutoPath;
            bool timingEligible = effectiveMode == AsyncComputeMode.Auto
                ? isSelectedAutoPath && timing.Eligible
                : timing.Eligible;
            AsyncComputePathStatus timingStatus = pauseForAutoIsolation
                ? timing.Status == AsyncComputePathStatus.Enabled
                    ? AsyncComputePathStatus.PendingWarmup
                    : timing.Status
                : timing.Status;
            string reason = !requested
                ? DescribeInactivePath(path, input)
                : isProbe
                    ? $"Collecting isolated Auto timing samples for {path}; no other async path is scheduled this frame."
                    : pauseForAutoIsolation
                        ? $"Waiting while {autoIsolatedPath!.Value} is the sole isolated Auto path for this workload."
                        : effectiveMode == AsyncComputeMode.Auto &&
                          !autoIsolatedPath.HasValue
                            ? "No complete, independently validated Auto path is available for this workload."
                            : timing.Reason;
            paths.Add(new AsyncComputePathEligibility(
                path,
                requested,
                timingEligible,
                timingStatus,
                reason,
                IsAutoTimingProbe: isProbe,
                CorrectnessCertified:
                    AsyncComputePassCatalog.IsCorrectnessCertified(path),
                ForceValidationAuthorized:
                    effectiveMode ==
                        AsyncComputeMode.ForceEnabledForValidation &&
                    settings.AsyncCompute.ForceValidationPath == path &&
                    _validationLedger.IsAutoTimingAllowed(path)));
        }

        var passes = new List<AsyncComputePassRequest>(
            _renderGraph.PassNames.Count);
        foreach (string passName in _renderGraph.PassNames)
        {
            AsyncComputePath? path =
                AsyncComputePassCatalog.TryGetPath(passName, out var mapped)
                    ? mapped
                    : null;
            bool enabledByFeatureIsolation =
                RenderFeatureIsolationPolicy.ShouldExecutePass(
                    sceneData.ActiveFeatureIsolation,
                    passName);
            passes.Add(new AsyncComputePassRequest(
                passName,
                path,
                _renderGraph.GetPassResourceUsages(passName),
                enabledByFeatureIsolation,
                path?.ToString() ?? string.Empty,
                WillExecute: enabledByFeatureIsolation &&
                    _renderGraph.WillExecutePass(
                        passName,
                        input.FrameIndex,
                        sceneData)));
        }

        if (!_recoverableRetryGate.CanAttempt(retryScope))
        {
            CurrentPlan = CreateGenerationScopedGraphicsFallbackPlan(
                requestedMode,
                supported,
                retryScope,
                paths,
                passes,
                sceneData);
            return CurrentPlan;
        }

        AsyncComputePlanVariantKey variantKey = CreatePlanVariantKey(
            settingsSignature,
            capabilities,
            paths,
            passes,
            _renderGraph.ConcreteResourceBindings,
            timelineValueBase: 0UL);
        AsyncComputeSubmissionPlan? cachedPlan = null;
        bool cachedVariant = asyncProjectionReady &&
            _planVariantCache.TryGet(variantKey, out cachedPlan);
        AsyncComputeSubmissionPlan submissionPlan = cachedVariant
            ? cachedPlan!
            : _scheduler.Compile(new AsyncComputeSchedulerInput(
                effectiveMode,
                capabilities,
                _renderGraph.ConcreteResourceBindings,
                paths,
                passes,
                input.FrameIndex,
                FirstTimelineValue: 1UL));
        if (submissionPlan.Accepted &&
            !asyncProjectionReady &&
            submissionPlan.ContainsAsyncCompute)
        {
            submissionPlan = submissionPlan with
            {
                Accepted = false,
                FailureReason =
                    "Concrete async resource-state projection capacity was unavailable.",
                Segments = Array.Empty<AsyncComputeSubmissionSegment>(),
                Transfers = Array.Empty<QueueOwnershipTransfer>()
            };
        }

        if (submissionPlan.Accepted &&
            asyncProjectionReady &&
            !ProjectSubmission(
                submissionPlan,
                input.FrameIndex,
                input.GraphicsQueueFamily,
                input.ComputeQueueFamily,
                out string projectionFailure))
        {
            submissionPlan = submissionPlan with
            {
                Accepted = false,
                FailureReason = projectionFailure,
                Segments = Array.Empty<AsyncComputeSubmissionSegment>(),
                Transfers = Array.Empty<QueueOwnershipTransfer>()
            };
        }

        if (!cachedVariant && submissionPlan.Accepted)
            _planVariantCache.Add(variantKey, submissionPlan);
        if (!submissionPlan.Accepted)
        {
            RecordRecoverableFallback(
                retryScope,
                submissionPlan.FailureReason);
            effectiveMode = AsyncComputeMode.Disabled;
        }
        else
        {
            RecordValidatedPlan(retryScope);
        }

        RenderGraphDiagnostics graphDiagnostics =
            _renderGraph.CreateDiagnostics(
                sceneData.ActiveFeatureIsolation,
                asyncComputeEnabled: false,
                sceneData: sceneData);
        string status = _emergencyFallbackLatched
            ? $"emergency fallback latched: {_lastFallbackReason}"
            : requestedMode == AsyncComputeMode.Disabled
                ? "disabled by policy; graphics-only execution is active."
                : !supported
                    ? "requested but inactive: no independent compute queue is available; using graphics-only execution."
                    : !submissionPlan.Accepted
                        ? $"validation fallback: {submissionPlan.FailureReason}"
                        : submissionPlan.ContainsAsyncCompute
                            ? "enabled: generic async segment plan compiled with validated concrete handoffs."
                            : "no async path was eligible for this frame.";

        AsyncComputeFramePlan result = new(
            requestedMode,
            effectiveMode,
            supported,
            submissionPlan,
            graphDiagnostics,
            status);
        if (!string.IsNullOrWhiteSpace(
                input.GraphicsOnlyConstraintReason) &&
            result.SubmissionPlan.ContainsAsyncCompute)
        {
            _stateProjection.Discard();
            result = CreateGraphicsFallbackPlan(
                result,
                input.GraphicsOnlyConstraintReason);
        }

        CurrentPlan = result;
        return result;
    }

    internal AsyncComputeRecordingDecision ValidateForRecording(
        AsyncComputeFramePlan plan,
        ulong currentResourcePlanGeneration)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.SubmissionPlan.Accepted ||
            !plan.SubmissionPlan.ContainsAsyncCompute)
        {
            CurrentPlan = plan;
            return new AsyncComputeRecordingDecision(
                plan,
                RecordAsync: false,
                plan.SubmissionPlan.FailureReason);
        }

        AsyncComputeSubmissionPlan submissionPlan = plan.SubmissionPlan;
        if (submissionPlan.ResourcePlanGeneration !=
            currentResourcePlanGeneration)
        {
            _stateProjection.Discard();
            _renderGraph.ConcreteResourceBindings
                .RecordStalePlanRejection();
            const string reason =
                "The concrete resource-binding generation changed after plan compilation.";
            RecordRecoverableFallback(
                new AsyncComputePlanRetryScope(
                    submissionPlan.ResourcePlanGeneration,
                    _currentSettingsSignature),
                reason);
            AsyncComputeFramePlan fallback =
                CreateGraphicsFallbackPlan(plan, reason);
            CurrentPlan = fallback;
            return new AsyncComputeRecordingDecision(
                fallback,
                RecordAsync: false,
                reason);
        }

        AsyncComputeSubmissionSegment? earlySwapchainSegment = null;
        for (int index = 0;
             index < submissionPlan.Segments.Count;
             index++)
        {
            AsyncComputeSubmissionSegment candidate =
                submissionPlan.Segments[index];
            if (candidate.AccessesSwapchain &&
                !candidate.IsTerminalGraphicsSegment)
            {
                earlySwapchainSegment = candidate;
                break;
            }
        }

        if (earlySwapchainSegment != null)
        {
            _stateProjection.Discard();
            string reason =
                $"Async compute plan segment {earlySwapchainSegment.Id} ({earlySwapchainSegment.Queue}: " +
                $"{string.Join(", ", earlySwapchainSegment.Passes)}) would access the acquired swapchain image " +
                "before the terminal graphics submission.";
            RecordRecoverableFallback(
                new AsyncComputePlanRetryScope(
                    submissionPlan.ResourcePlanGeneration,
                    _currentSettingsSignature),
                reason);
            AsyncComputeFramePlan fallback =
                CreateGraphicsFallbackPlan(plan, reason);
            CurrentPlan = fallback;
            return new AsyncComputeRecordingDecision(
                fallback,
                RecordAsync: false,
                reason);
        }

        CurrentPlan = plan;
        return new AsyncComputeRecordingDecision(
            plan,
            RecordAsync: true,
            string.Empty);
    }

    internal ulong ResolveTimelineValue(ulong relativeValue)
    {
        if (relativeValue == 0UL)
            return 0UL;
        return checked(relativeValue + _timelineOffsetThisFrame);
    }

    internal void RegisterComputeSegment(
        int segmentId,
        AsyncComputePath path,
        ulong commandBufferIdentity) =>
        _validationLedger.RegisterSegment(
            segmentId,
            path,
            commandBufferIdentity);

    internal AsyncComputeRecordingPublication CommitRecording(
        AsyncComputeFramePlan plan,
        in AsyncComputeRecordingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(plan);
        AsyncComputeSubmissionPlan submissionPlan = plan.SubmissionPlan;
        if (!_stateProjection.Commit(
                submissionPlan.ResourcePlanGeneration))
        {
            const string failure =
                "Async projected state could not commit the validated resource-plan generation.";
            _stateProjection.Discard();
            return new AsyncComputeRecordingPublication(
                Succeeded: false,
                failure,
                0,
                string.Empty);
        }

        foreach (QueueOwnershipTransfer transfer in
                 submissionPlan.Transfers)
        {
            if (!transfer.IsConcurrentResource)
            {
                foreach (RenderGraphConcreteResourceBinding binding in
                         transfer.AllBindings)
                {
                    _renderGraph.ConcreteResourceBindings.CommitOwner(
                        binding,
                        transfer.DestinationQueueFamily);
                }
            }

            if (transfer.IsImage)
            {
                foreach (RenderGraphConcreteResourceBinding binding in
                         transfer.AllBindings)
                {
                    _renderGraph.ConcreteResourceBindings.CommitLayout(
                        binding,
                        transfer.NewLayout);
                }
            }
        }

        _terminalWaits.Clear();
        ulong maxTimelineValue = 0UL;
        for (int index = 0;
             index < submissionPlan.Segments.Count;
             index++)
        {
            AsyncComputeSubmissionSegment segment =
                submissionPlan.Segments[index];
            ulong signal = ResolveTimelineValue(
                segment.TimelineSignalValue.GetValueOrDefault());
            if (signal > maxTimelineValue)
                maxTimelineValue = signal;
            if (!segment.IsTerminalGraphicsSegment)
                continue;

            for (int waitIndex = 0;
                 waitIndex < segment.TimelineWaits.Count;
                 waitIndex++)
            {
                AsyncComputeTimelineWait wait =
                    segment.TimelineWaits[waitIndex];
                _terminalWaits.Add(wait with
                {
                    Value = ResolveTimelineValue(wait.Value)
                });
            }
        }

        if (maxTimelineValue >= _nextTimelineValue)
            _nextTimelineValue = checked(maxTimelineValue + 1UL);

        _planRecordedThisFrame = true;
        _plannedReleaseBarrierCountThisFrame =
            submissionPlan.PlannedReleaseBarrierCount;
        _plannedAcquireBarrierCountThisFrame =
            submissionPlan.PlannedAcquireBarrierCount;
        _emittedReleaseBarrierCountThisFrame =
            summary.EmittedReleaseBarrierCount;
        _emittedAcquireBarrierCountThisFrame =
            summary.EmittedAcquireBarrierCount;
        _ownershipTransferCountThisFrame =
            summary.OwnershipTransferCount;
        _barrierRecordMicrosecondsThisFrame =
            summary.BarrierRecordMicroseconds;
        _transferredBytesThisFrame = submissionPlan.TransferBytes;
        _transferredImageSubresourcesThisFrame =
            submissionPlan.TransferImageSubresources;
        CurrentPlan = plan;

        string graphBarrierSummary =
            $"async plan: {submissionPlan.GraphicsSegmentCount} graphics segments, " +
            $"{submissionPlan.ComputeSegmentCount} compute segments, " +
            $"{submissionPlan.QueueFamilyOwnershipTransferCount} queue-family handoffs";
        return new AsyncComputeRecordingPublication(
            Succeeded: true,
            string.Empty,
            _ownershipTransferCountThisFrame,
            graphBarrierSummary);
    }

    internal void RecordSubmittedNonTerminalSegment(
        AsyncComputeQueue queue,
        long cpuSubmitMicroseconds)
    {
        RecordSubmittedSegment(queue);
        if (queue == AsyncComputeQueue.Compute)
        {
            _lastAsyncSubmitMicroseconds = checked(
                _lastAsyncSubmitMicroseconds + cpuSubmitMicroseconds);
        }
    }

    internal void CaptureTiming(
        int frameIndex,
        AsyncComputeFramePlan plan,
        in AsyncComputeTimingCaptureInput input)
    {
        ValidateFrameIndex(frameIndex);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(input.Settings);
        ArgumentNullException.ThrowIfNull(input.SceneData);

        if (plan.RequestedMode is AsyncComputeMode.Disabled or
            AsyncComputeMode.ForceEnabledForValidation)
        {
            _timingFrames[frameIndex] = null;
            return;
        }

        var keys = new Dictionary<
            AsyncComputePath,
            AsyncComputeTimingKey>();
        foreach (AsyncComputePathRuntimeStatus path in
                 plan.SubmissionPlan.Paths)
        {
            if (path.Requested)
            {
                keys.Add(
                    path.Path,
                    CreateTimingKey(
                        path.Path,
                        input.Settings,
                        input.SceneData,
                        input.DeviceName,
                        input.DriverVersion));
            }
        }

        _timingFrames[frameIndex] =
            new AsyncComputeTimingFrame(plan, keys);
    }

    internal AsyncComputeSubmissionPatch CompleteTerminalSubmission(
        int frameIndex,
        long totalCpuSubmitMicroseconds)
    {
        ValidateFrameIndex(frameIndex);
        if (_planRecordedThisFrame)
            RecordSubmittedSegment(AsyncComputeQueue.Graphics);

        if (_timingFrames[frameIndex] is { } timingFrame)
        {
            timingFrame.CpuSubmitMicroseconds =
                totalCpuSubmitMicroseconds;
            timingFrame.CpuBarrierRecordMicroseconds =
                _barrierRecordMicrosecondsThisFrame;
        }

        return new AsyncComputeSubmissionPatch(
            _submittedGraphicsSegmentsThisFrame,
            _submittedComputeSegmentsThisFrame);
    }

    internal void ResetTimingHistory(AsyncComputeTimingResetKind kind)
    {
        _timingPolicy.Clear();
        Array.Clear(_timingFrames);
        if (kind == AsyncComputeTimingResetKind.SceneOrMode)
            _nextAutoTimingProbePath = 0;
    }

    internal void LatchEmergencyFallback(string reason)
    {
        _emergencyFallbackLatched = true;
        _lastFallbackReason = string.IsNullOrWhiteSpace(reason)
            ? "Async compute synchronization-plan failure."
            : reason;
        _validationFallbackCount++;
        _stateProjection.Discard();
        _planVariantCache.Clear();
    }

    internal void AbortFrame(int frameIndex)
    {
        ValidateFrameIndex(frameIndex);
        _timingFrames[frameIndex] = null;
        _stateProjection.Discard();
    }

    internal long EstimateOverlapMicroseconds(
        AsyncComputeFramePlan plan,
        FrameTimingSnapshot timings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(timings);
        return EstimateOverlapMicroseconds(
            plan.SubmissionPlan,
            timings);
    }

    internal AsyncComputeDiagnosticsSnapshot CreateDiagnosticsSnapshot(
        FrameTimingSnapshot completedTimings,
        in AsyncComputeDiagnosticsContext context)
    {
        ArgumentNullException.ThrowIfNull(completedTimings);
        AsyncComputeFramePlan plan = CurrentPlan ??
            throw new InvalidOperationException(
                "Async compute diagnostics require a current frame plan.");
        AsyncComputeSubmissionPlan submissionPlan = plan.SubmissionPlan;
        AsyncComputePathDiagnostic[] pathDiagnostics =
            submissionPlan.Paths
                .Select(path => new AsyncComputePathDiagnostic(
                    path.Path,
                    path.Requested,
                    path.Supported,
                    path.Eligible,
                    path.Active,
                    path.Status,
                    path.Reason,
                    path.Passes,
                    AsyncComputePassCatalog
                        .GetCertificationEvidenceRevision(path.Path)))
                .ToArray();
        AsyncComputeSegmentDiagnostic[] segmentDiagnostics =
            submissionPlan.Segments
                .Select(segment => new AsyncComputeSegmentDiagnostic(
                    segment.Id,
                    segment.Queue.ToString(),
                    segment.Passes,
                    segment.TimelineWaits
                        .Select(wait =>
                            ResolveTimelineValue(wait.Value))
                        .ToArray(),
                    segment.TimelineSignalValue.HasValue
                        ? ResolveTimelineValue(
                            segment.TimelineSignalValue.Value)
                        : null,
                    segment.AcquireTransfers.Count,
                    segment.ReleaseTransfers.Count,
                    segment.AccessesSwapchain,
                    segment.IsTerminalGraphicsSegment))
                .ToArray();

        return new AsyncComputeDiagnosticsSnapshot(
            plan,
            context.IndependentQueueAvailable,
            context.DedicatedQueueFamilyAvailable,
            context.GraphicsQueueFamily,
            context.ComputeQueueFamily,
            submissionPlan.GraphicsSegmentCount,
            submissionPlan.ComputeSegmentCount,
            _submittedGraphicsSegmentsThisFrame,
            _submittedComputeSegmentsThisFrame,
            _plannedReleaseBarrierCountThisFrame,
            _plannedAcquireBarrierCountThisFrame,
            _emittedReleaseBarrierCountThisFrame,
            _emittedAcquireBarrierCountThisFrame,
            _ownershipTransferCountThisFrame,
            _barrierRecordMicrosecondsThisFrame,
            _transferredBytesThisFrame,
            _transferredImageSubresourcesThisFrame,
            _validationFallbackCount,
            _lastFallbackReason,
            submissionPlan.ResourcePlanGeneration,
            _renderGraph.ConcreteResourceBindings.StalePlanRejectionCount,
            EstimateQueueBusyMicroseconds(
                submissionPlan,
                completedTimings),
            EstimateOverlapMicroseconds(
                submissionPlan,
                completedTimings),
            EstimateFirstConsumerWaitMicroseconds(
                submissionPlan,
                completedTimings),
            pathDiagnostics,
            segmentDiagnostics);
    }

    private AsyncComputeFramePlan CreateDisabledPlan(
        SceneRenderingData sceneData,
        bool supported,
        AsyncComputeMode requestedMode)
    {
        AsyncComputePathRuntimeStatus[] paths =
            Enum.GetValues<AsyncComputePath>()
                .Select(path => new AsyncComputePathRuntimeStatus(
                    path,
                    Requested: false,
                    Supported: supported,
                    Eligible: false,
                    Active: false,
                    AsyncComputePathStatus.DisabledByPolicy,
                    "Async compute mode is Disabled.",
                    _renderGraph.PassNames
                        .Where(passName =>
                            AsyncComputePassCatalog.TryGetPath(
                                passName,
                                out AsyncComputePath mapped) &&
                            mapped == path)
                        .ToArray()))
                .ToArray();
        var submissionPlan = new AsyncComputeSubmissionPlan(
            Accepted: true,
            FailureReason: string.Empty,
            _renderGraph.ConcreteResourceBindings.Generation,
            Array.Empty<AsyncComputeSubmissionSegment>(),
            Array.Empty<QueueOwnershipTransfer>(),
            paths);
        return new AsyncComputeFramePlan(
            requestedMode,
            AsyncComputeMode.Disabled,
            supported,
            submissionPlan,
            _renderGraph.CreateDiagnostics(
                sceneData.ActiveFeatureIsolation,
                asyncComputeEnabled: false),
            _emergencyFallbackLatched
                ? $"emergency fallback latched: {_lastFallbackReason}"
                : "disabled by policy; graphics-only execution is active.");
    }

    private static AsyncComputeFramePlan CreateGraphicsFallbackPlan(
        AsyncComputeFramePlan source,
        string reason)
    {
        string fallbackReason = string.IsNullOrWhiteSpace(reason)
            ? "Async command recording was rejected before submission."
            : reason;
        IReadOnlyList<AsyncComputePathRuntimeStatus> paths =
            source.SubmissionPlan.Paths
                .Select(path => path.Active || path.Eligible
                    ? path with
                    {
                        Active = false,
                        Eligible = false,
                        Status =
                            AsyncComputePathStatus.ValidationFallback,
                        Reason = fallbackReason
                    }
                    : path)
                .ToArray();
        var submissionPlan = new AsyncComputeSubmissionPlan(
            Accepted: false,
            FailureReason: fallbackReason,
            source.SubmissionPlan.ResourcePlanGeneration,
            Array.Empty<AsyncComputeSubmissionSegment>(),
            Array.Empty<QueueOwnershipTransfer>(),
            paths);
        return source with
        {
            EffectiveMode = AsyncComputeMode.Disabled,
            SubmissionPlan = submissionPlan,
            Status = $"validation fallback: {fallbackReason}"
        };
    }

    private AsyncComputeFramePlan
        CreateGenerationScopedGraphicsFallbackPlan(
            AsyncComputeMode requestedMode,
            bool supported,
            AsyncComputePlanRetryScope scope,
            IReadOnlyList<AsyncComputePathEligibility> eligibility,
            IReadOnlyList<AsyncComputePassRequest> passes,
            SceneRenderingData sceneData)
    {
        string reason = string.IsNullOrWhiteSpace(
            _recoverableRetryGate.Reason)
                ? "Async compute plan validation failed for this graph/settings scope."
                : _recoverableRetryGate.Reason;
        AsyncComputePathRuntimeStatus[] paths = eligibility
            .Select(path =>
            {
                bool requested =
                    requestedMode != AsyncComputeMode.Disabled &&
                    path.RequestedByFeature;
                IReadOnlyList<string> pathPasses = passes
                    .Where(pass => pass.Path == path.Path)
                    .Select(pass => pass.Name)
                    .ToArray();
                return new AsyncComputePathRuntimeStatus(
                    path.Path,
                    requested,
                    supported,
                    Eligible: false,
                    Active: false,
                    requested
                        ? AsyncComputePathStatus.ValidationFallback
                        : AsyncComputePathStatus.DisabledByFeature,
                    requested ? reason : path.Reason,
                    pathPasses);
            })
            .ToArray();
        var submissionPlan = new AsyncComputeSubmissionPlan(
            Accepted: false,
            FailureReason: reason,
            scope.ResourcePlanGeneration,
            Array.Empty<AsyncComputeSubmissionSegment>(),
            Array.Empty<QueueOwnershipTransfer>(),
            paths);
        return new AsyncComputeFramePlan(
            requestedMode,
            AsyncComputeMode.Disabled,
            supported,
            submissionPlan,
            _renderGraph.CreateDiagnostics(
                sceneData.ActiveFeatureIsolation,
                asyncComputeEnabled: false,
                sceneData: sceneData),
            $"validation fallback retained for plan generation {scope.ResourcePlanGeneration}: {reason}");
    }

    private AsyncComputePlanVariantKey CreatePlanVariantKey(
        ulong settingsSignature,
        AsyncComputeQueueCapabilities capabilities,
        IReadOnlyList<AsyncComputePathEligibility> paths,
        IReadOnlyList<AsyncComputePassRequest> passes,
        RenderGraphResourceBindings bindings,
        ulong timelineValueBase)
    {
        ulong capabilitySignature = 14695981039346656037UL;
        capabilitySignature = HashUInt64(
            capabilitySignature,
            capabilities.HasIndependentComputeQueue ? 1UL : 0UL);
        capabilitySignature = HashUInt64(
            capabilitySignature,
            capabilities.GraphicsQueueFamily);
        capabilitySignature = HashUInt64(
            capabilitySignature,
            capabilities.ComputeQueueFamily);
        capabilitySignature = HashUInt64(
            capabilitySignature,
            (ulong)capabilities.GraphicsQueueFlags);
        capabilitySignature = HashUInt64(
            capabilitySignature,
            (ulong)capabilities.ComputeQueueFlags);

        ulong pathMask = 0UL;
        for (int index = 0; index < paths.Count; index++)
        {
            AsyncComputePathEligibility path = paths[index];
            int shift = checked((int)path.Path * 5);
            ulong flags = 0UL;
            if (path.RequestedByFeature)
                flags |= 1UL;
            if (path.AutoTimingEligible)
                flags |= 1UL << 1;
            if (path.IsAutoTimingProbe)
                flags |= 1UL << 2;
            if (path.CorrectnessCertified)
                flags |= 1UL << 3;
            if (path.ForceValidationAuthorized)
                flags |= 1UL << 4;
            pathMask |= flags << shift;
        }

        ulong passSignature = 14695981039346656037UL;
        for (int index = 0; index < passes.Count; index++)
        {
            AsyncComputePassRequest pass = passes[index];
            passSignature = HashString(passSignature, pass.Name);
            passSignature = HashUInt64(
                passSignature,
                pass.Path.HasValue
                    ? (ulong)(int)pass.Path.Value + 1UL
                    : 0UL);
            passSignature = HashUInt64(
                passSignature,
                pass.EnabledByFeatureIsolation ? 1UL : 0UL);
            passSignature = HashUInt64(
                passSignature,
                pass.WillExecute ? 1UL : 0UL);
        }

        return new AsyncComputePlanVariantKey(
            bindings.Generation,
            settingsSignature,
            capabilitySignature,
            passSignature,
            pathMask,
            bindings.SynchronizationStateGeneration,
            timelineValueBase);
    }

    private bool ProjectSubmission(
        AsyncComputeSubmissionPlan plan,
        int frameIndex,
        uint graphicsQueueFamily,
        uint computeQueueFamily,
        out string failure)
    {
        for (int transferIndex = 0;
             transferIndex < plan.Transfers.Count;
             transferIndex++)
        {
            QueueOwnershipTransfer transfer = plan.Transfers[transferIndex];
            IReadOnlyList<RenderGraphConcreteResourceBinding> bindings =
                transfer.AllBindings;
            for (int bindingIndex = 0;
                 bindingIndex < bindings.Count;
                 bindingIndex++)
            {
                RenderGraphConcreteResourceBinding binding =
                    bindings[bindingIndex];
                if (!_stateProjection.TryTransition(
                        binding.AllocationIdentity,
                        plan.ResourcePlanGeneration,
                        transfer.DestinationQueue,
                        transfer.DestinationQueueFamily,
                        transfer.NewLayout,
                        transfer.DestinationStageMask,
                        transfer.DestinationAccessMask,
                        out AsyncComputeProjectionFailure projectionFailure))
                {
                    failure =
                        $"Async projected state rejected transfer {transfer.Id} for '{binding.Name}': {projectionFailure}.";
                    _stateProjection.Discard();
                    return false;
                }
            }
        }

        foreach (AsyncComputeSubmissionSegment segment in plan.Segments)
        {
            AsyncComputeQueue queue = segment.Queue;
            uint queueFamily = queue == AsyncComputeQueue.Compute
                ? computeQueueFamily
                : graphicsQueueFamily;
            for (int passIndex = 0;
                 passIndex < segment.Passes.Count;
                 passIndex++)
            {
                IReadOnlyList<RenderGraphResourceUsage> usages =
                    _renderGraph.GetPassResourceUsages(
                        segment.Passes[passIndex]);
                for (int usageIndex = 0;
                     usageIndex < usages.Count;
                     usageIndex++)
                {
                    RenderGraphResourceUsage usage = usages[usageIndex];
                    IReadOnlyList<RenderGraphConcreteResourceBinding>
                        bindings =
                            _renderGraph.ConcreteResourceBindings.GetBindings(
                                usage.Resource,
                                frameIndex,
                                usage.HistoryBinding);
                    for (int bindingIndex = 0;
                         bindingIndex < bindings.Count;
                         bindingIndex++)
                    {
                        RenderGraphConcreteResourceBinding binding =
                            bindings[bindingIndex];
                        ImageLayout layout =
                            binding.Kind ==
                                RenderGraphConcreteResourceKind.Image
                                ? usage.FinalImageLayout !=
                                    ImageLayout.Undefined
                                    ? usage.FinalImageLayout
                                    : usage.ImageLayout
                                : ImageLayout.Undefined;
                        if (!_stateProjection.TryTransition(
                                binding.AllocationIdentity,
                                plan.ResourcePlanGeneration,
                                queue,
                                queueFamily,
                                layout,
                                usage.StageMask,
                                usage.AccessMask,
                                out AsyncComputeProjectionFailure
                                    projectionFailure))
                        {
                            failure =
                                $"Async projected state rejected pass '{segment.Passes[passIndex]}' for " +
                                $"'{binding.Name}': {projectionFailure}.";
                            _stateProjection.Discard();
                            return false;
                        }
                    }
                }
            }
        }

        failure = string.Empty;
        return true;
    }

    private bool IsPathFeatureActive(
        AsyncComputePath path,
        in AsyncComputePlanningInput input)
    {
        RenderSettings settings = input.Settings;
        SceneRenderingData sceneData = input.SceneData;
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        bool animationDebugActive =
            sceneData.AnimationDebugView != AnimationDebugView.None;
        bool fogWillExecute =
            settings.Fog.Enabled &&
            settings.Fog.Mode != FogMode.Disabled &&
            !animationDebugActive;
        return path switch
        {
            AsyncComputePath.SimpleDdgiUpdate =>
                gi.EffectiveUseDdgi &&
                gi.DdgiAsyncComputeEnabled &&
                sceneData.SimpleDdgiActive != 0 &&
                (sceneData.SimpleDdgiProbesUpdated > 0 ||
                 (sceneData.SimpleDdgiSchedulerMode.IsGpuMode() &&
                  sceneData.SimpleDdgiSchedulerReady != 0)) &&
                sceneData.SimpleDdgiSampledAtlasActive == 0,
            AsyncComputePath.FarFieldClipmapBake =>
                gi.EffectiveUseDdgi &&
                gi.FarFieldClipmapEnabled &&
                input.FarFieldBakePending,
            AsyncComputePath.AmbientOcclusionBlur =>
                settings.AmbientOcclusion.Enabled &&
                sceneData.DepthPrePassEnabled &&
                settings.AmbientOcclusion.BlurRadius > 0,
            AsyncComputePath.HiZBuild => sceneData.HiZBuildEnabled,
            AsyncComputePath.Fog =>
                fogWillExecute &&
                !FroxelFogMayExecute(settings) &&
                sceneData.SimpleDdgiSampledAtlasActive == 0,
            AsyncComputePath.Bloom =>
                settings.Bloom.Enabled &&
                input.BloomMipCount > 0 &&
                !(fogWillExecute &&
                  settings.Fog.DebugView != FogDebugView.None),
            AsyncComputePath.GpuParticles =>
                sceneData.GpuParticlesEnabled != 0 &&
                sceneData.GpuParticleEmitterCount > 0 &&
                sceneData.GpuParticleCapacity > 0,
            _ => false
        };
    }

    private AsyncComputeTimingDecision GetTimingDecision(
        AsyncComputePath path,
        in AsyncComputePlanningInput input)
    {
        if (input.Settings.AsyncCompute.Mode != AsyncComputeMode.Auto)
        {
            return new AsyncComputeTimingDecision(
                AsyncComputePathStatus.Enabled,
                Eligible: true,
                Active: true,
                "Timing gate is bypassed only by ForceEnabledForValidation; resource and capability validation still apply.",
                new AsyncComputeTimingStats(0, 0, 0, 0),
                new AsyncComputeTimingStats(0, 0, 0, 0));
        }

        if (!AsyncComputePassCatalog.IsCorrectnessCertified(path))
        {
            return new AsyncComputeTimingDecision(
                AsyncComputePathStatus.Uncertified,
                Eligible: false,
                Active: false,
                "Path is quarantined from Auto until correctness certification is reviewed.",
                new AsyncComputeTimingStats(0, 0, 0, 0),
                new AsyncComputeTimingStats(0, 0, 0, 0));
        }

        return _timingPolicy.Evaluate(
            CreateTimingKey(path, input),
            input.Settings.AsyncCompute,
            ClampTimingFrameNumber(input.TimingFrameNumber));
    }

    private AsyncComputePath? SelectAutoEnabledPath(
        in AsyncComputePlanningInput input,
        IReadOnlyDictionary<AsyncComputePath, bool> requestedByFeature,
        IReadOnlyDictionary<AsyncComputePath, AsyncComputeTimingDecision>
            timingDecisions)
    {
        AsyncComputePlanningInput capturedInput = input;
        AsyncComputePath[] candidates = Enum.GetValues<AsyncComputePath>();
        return candidates
            .Where(path =>
                requestedByFeature.TryGetValue(path, out bool requested) &&
                requested)
            .Where(path =>
                timingDecisions.TryGetValue(
                    path,
                    out AsyncComputeTimingDecision? timing) &&
                timing.Eligible)
            .OrderByDescending(path =>
            {
                AsyncComputeTimingDecision timing = timingDecisions[path];
                return timing.GraphicsOnly.MeanMilliseconds -
                       timing.Async.MeanMilliseconds;
            })
            .ThenBy(path => (int)path)
            .Select(path => (AsyncComputePath?)path)
            .FirstOrDefault(path =>
                path.HasValue &&
                HasCompleteResourcePlan(path.Value, capturedInput));
    }

    private AsyncComputePath? SelectAutoTimingProbe(
        in AsyncComputePlanningInput input,
        IReadOnlyDictionary<AsyncComputePath, bool> requestedByFeature)
    {
        int frameNumber = ClampTimingFrameNumber(input.TimingFrameNumber);
        AsyncComputePath[] allPaths = Enum.GetValues<AsyncComputePath>();
        for (int offset = 0; offset < allPaths.Length; offset++)
        {
            int index = (_nextAutoTimingProbePath + offset) %
                allPaths.Length;
            AsyncComputePath path = allPaths[index];
            if (!AsyncComputePassCatalog.IsCorrectnessCertified(path))
                continue;
            if (!requestedByFeature.TryGetValue(
                    path,
                    out bool requested) ||
                !requested)
            {
                continue;
            }

            if (!_timingPolicy.CanCollectAsyncProbe(
                    CreateTimingKey(path, input),
                    input.Settings.AsyncCompute,
                    frameNumber))
            {
                continue;
            }

            if (!HasCompleteResourcePlan(path, input))
                continue;

            _nextAutoTimingProbePath = (index + 1) % allPaths.Length;
            return path;
        }

        return null;
    }

    private bool HasCompleteResourcePlan(
        AsyncComputePath path,
        in AsyncComputePlanningInput input)
    {
        int frameIndex = input.FrameIndex;
        SceneRenderingData sceneData = input.SceneData;
        string[] passNames = _renderGraph.PassNames
            .Where(passName =>
                AsyncComputePassCatalog.TryGetPath(
                    passName,
                    out AsyncComputePath mapped) &&
                mapped == path)
            .Where(passName =>
                _renderGraph.WillExecutePass(
                    passName,
                    frameIndex,
                    sceneData))
            .ToArray();
        return passNames.Length > 0 &&
               _renderGraph.ValidateConcreteResourcePlan(
                   passNames,
                   frameIndex).Count == 0;
    }

    private static string DescribeInactivePath(
        AsyncComputePath path,
        in AsyncComputePlanningInput input)
    {
        if (!input.Settings.AsyncCompute.IsEnabledBy(path))
            return "Disabled by the per-path async-compute setting.";
        if (!RenderFeatureIsolationPolicy.ShouldExecutePass(
                input.SceneData.ActiveFeatureIsolation,
                AsyncComputePassCatalog.GetRepresentativePass(path)))
        {
            return "Disabled by active feature-isolation mode.";
        }

        return path switch
        {
            AsyncComputePath.SimpleDdgiUpdate =>
                "Simple DDGI is inactive for this frame.",
            AsyncComputePath.FarFieldClipmapBake =>
                "No far-field clipmap bake is pending.",
            AsyncComputePath.AmbientOcclusionBlur =>
                "Ambient occlusion blur is inactive for this frame.",
            AsyncComputePath.HiZBuild =>
                "Hi-Z build is inactive for this frame.",
            AsyncComputePath.Fog =>
                "Fog is disabled for this frame.",
            AsyncComputePath.Bloom =>
                "Bloom is disabled or has no mip chain.",
            AsyncComputePath.GpuParticles =>
                "GPU particle emitters are inactive for this frame.",
            _ => "The feature is inactive for this frame."
        };
    }

    private AsyncComputeTimingKey CreateTimingKey(
        AsyncComputePath path,
        in AsyncComputePlanningInput input) =>
        CreateTimingKey(
            path,
            input.Settings,
            input.SceneData,
            input.DeviceName,
            input.DriverVersion);

    private static AsyncComputeTimingKey CreateTimingKey(
        AsyncComputePath path,
        RenderSettings settings,
        SceneRenderingData sceneData,
        string deviceName,
        string driverVersion) =>
        new(
            string.IsNullOrWhiteSpace(deviceName)
                ? "unknown-device"
                : deviceName,
            string.IsNullOrWhiteSpace(driverVersion)
                ? "unknown-driver"
                : driverVersion,
            BuildWorkloadIdentity(settings, sceneData),
            path);

    private static string BuildWorkloadIdentity(
        RenderSettings settings,
        SceneRenderingData sceneData) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{sceneData.ScreenWidth}x{sceneData.ScreenHeight}|{sceneData.ActiveFeatureIsolation}|{sceneData.ObjectCount}|{sceneData.MeshletCount}|{sceneData.LightCount}|{sceneData.DepthPrePassEnabled}|{sceneData.HiZBuildEnabled}|{sceneData.GpuParticlesEnabled}|{sceneData.GpuParticleEmitterCount}|{sceneData.FoliageClusterCount}|{settings.GlobalIllumination.Mode}|{settings.GlobalIllumination.EffectiveUseDdgi}|{settings.GlobalIllumination.SimpleDdgiSchedulerMode}|{sceneData.SimpleDdgiSchedulerMode}|{settings.AmbientOcclusion.Enabled}|{settings.AmbientOcclusion.Mode}|{settings.AmbientOcclusion.GtaoQualityPreset}|{settings.AmbientOcclusion.EffectiveBentNormalMode}|{settings.AmbientOcclusion.BlurRadius}|{settings.Fog.Enabled}|{settings.Fog.Mode}|{settings.Bloom.Enabled}|{settings.Bloom.MipCount}|{settings.AntiAliasing.EffectiveMode}");

    private static bool FroxelFogMayExecute(RenderSettings settings)
    {
        FogTechnique technique = settings.Fog.Technique;
        if (technique == FogTechnique.Froxel)
            return true;
        if (technique != FogTechnique.Auto ||
            !settings.Fog.Volumetric.SingleScatteringQualified)
        {
            return false;
        }

        return settings.QualityPreset is RenderQualityPreset.High or
            RenderQualityPreset.DdgiHigh or RenderQualityPreset.Ultra;
    }

    private static ulong ComputeSettingsSignature(
        AsyncComputeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        const ulong offsetBasis = 14695981039346656037UL;
        ulong signature = offsetBasis;
        signature = MixSettingsSignature(signature, (ulong)settings.Mode);
        signature = MixSettingsSignature(
            signature,
            settings.HiZBuildEnabled ? 1UL : 0UL);
        signature = MixSettingsSignature(
            signature,
            settings.AmbientOcclusionBlurEnabled ? 1UL : 0UL);
        signature = MixSettingsSignature(
            signature,
            settings.FogEnabled ? 1UL : 0UL);
        signature = MixSettingsSignature(
            signature,
            settings.BloomEnabled ? 1UL : 0UL);
        signature = MixSettingsSignature(
            signature,
            settings.SimpleDdgiUpdateEnabled ? 1UL : 0UL);
        signature = MixSettingsSignature(
            signature,
            settings.FarFieldClipmapBakeEnabled ? 1UL : 0UL);
        signature = MixSettingsSignature(
            signature,
            settings.GpuParticlesEnabled ? 1UL : 0UL);
        signature = MixSettingsSignature(
            signature,
            unchecked((uint)settings.AutoMinimumSampleCount));
        signature = MixSettingsSignature(
            signature,
            unchecked((uint)settings.AutoWarmupFrameCount));
        signature = MixSettingsSignature(
            signature,
            BitConverter.SingleToUInt32Bits(
                settings.AutoMinimumAbsoluteBenefitMilliseconds));
        signature = MixSettingsSignature(
            signature,
            BitConverter.SingleToUInt32Bits(
                settings.AutoMinimumRelativeBenefit));
        return MixSettingsSignature(
            signature,
            unchecked((uint)settings.AutoDecisionCooldownFrames));
    }

    private static ulong MixSettingsSignature(
        ulong signature,
        ulong value)
    {
        const ulong prime = 1099511628211UL;
        return (signature ^ value) * prime;
    }

    private void RecordRecoverableFallback(
        AsyncComputePlanRetryScope scope,
        string reason)
    {
        if (_recoverableRetryGate.RecordRejected(scope, reason))
            _validationFallbackCount++;
        _lastFallbackReason = _recoverableRetryGate.Reason;
    }

    private void ObserveRetryScope(AsyncComputePlanRetryScope scope)
    {
        if (_recoverableRetryGate.ObserveScope(scope) &&
            !_emergencyFallbackLatched)
        {
            _lastFallbackReason = string.Empty;
        }
    }

    private void RecordValidatedPlan(AsyncComputePlanRetryScope scope)
    {
        _recoverableRetryGate.RecordValidatedPlan(scope);
        if (!_emergencyFallbackLatched &&
            !_recoverableRetryGate.RejectedScope.HasValue)
        {
            _lastFallbackReason = string.Empty;
        }
    }

    private void RecordSubmittedSegment(AsyncComputeQueue queue)
    {
        if (queue == AsyncComputeQueue.Compute)
            _submittedComputeSegmentsThisFrame++;
        else
            _submittedGraphicsSegmentsThisFrame++;
    }

    private static ulong HashString(ulong hash, string value)
    {
        for (int index = 0; index < value.Length; index++)
            hash = HashUInt64(hash, value[index]);
        return HashUInt64(hash, 0UL);
    }

    private static ulong HashUInt64(ulong hash, ulong value) =>
        (hash ^ value) * 1099511628211UL;

    private static long EstimateQueueBusyMicroseconds(
        AsyncComputeSubmissionPlan plan,
        FrameTimingSnapshot timings)
    {
        if (!plan.ContainsAsyncCompute)
            return 0;

        long total = 0;
        foreach (AsyncComputeSubmissionSegment segment in plan.Segments)
        {
            if (segment.Queue != AsyncComputeQueue.Compute)
                continue;
            foreach (string pass in segment.Passes)
            {
                total = checked(
                    total + timings.GetGpuMicrosecondsOrZero(pass));
            }
        }

        return total;
    }

    private static long EstimateOverlapMicroseconds(
        AsyncComputeSubmissionPlan plan,
        FrameTimingSnapshot timings)
    {
        if (!plan.ContainsAsyncCompute)
            return 0;

        long totalOverlap = 0;
        for (int segmentIndex = 0;
             segmentIndex < plan.Segments.Count;
             segmentIndex++)
        {
            AsyncComputeSubmissionSegment compute =
                plan.Segments[segmentIndex];
            if (compute.Queue != AsyncComputeQueue.Compute)
                continue;

            long computeBusy = SumGpuPassMicroseconds(
                compute.Passes,
                timings);
            long graphicsWindow = 0;
            for (int next = segmentIndex + 1;
                 next < plan.Segments.Count;
                 next++)
            {
                AsyncComputeSubmissionSegment candidate =
                    plan.Segments[next];
                if (candidate.Queue != AsyncComputeQueue.Graphics)
                    continue;
                graphicsWindow = SumGpuPassMicroseconds(
                    candidate.Passes,
                    timings);
                break;
            }

            totalOverlap = checked(
                totalOverlap +
                Math.Min(computeBusy, graphicsWindow));
        }

        return Math.Max(0, totalOverlap);
    }

    private static long EstimateFirstConsumerWaitMicroseconds(
        AsyncComputeSubmissionPlan plan,
        FrameTimingSnapshot timings)
    {
        if (!plan.ContainsAsyncCompute)
            return 0;

        long totalWait = 0;
        for (int segmentIndex = 0;
             segmentIndex < plan.Segments.Count;
             segmentIndex++)
        {
            AsyncComputeSubmissionSegment compute =
                plan.Segments[segmentIndex];
            if (compute.Queue != AsyncComputeQueue.Compute)
                continue;

            long computeBusy = SumGpuPassMicroseconds(
                compute.Passes,
                timings);
            long graphicsWindow = 0;
            for (int next = segmentIndex + 1;
                 next < plan.Segments.Count;
                 next++)
            {
                AsyncComputeSubmissionSegment candidate =
                    plan.Segments[next];
                if (candidate.Queue != AsyncComputeQueue.Graphics)
                    continue;
                graphicsWindow = SumGpuPassMicroseconds(
                    candidate.Passes,
                    timings);
                break;
            }

            totalWait = checked(
                totalWait +
                Math.Max(0, computeBusy - graphicsWindow));
        }

        return totalWait;
    }

    private static long SumGpuPassMicroseconds(
        IReadOnlyList<string> passNames,
        FrameTimingSnapshot timings)
    {
        long total = 0;
        foreach (string passName in passNames)
        {
            total = checked(
                total + timings.GetGpuMicrosecondsOrZero(passName));
        }

        return total;
    }

    private static int ClampTimingFrameNumber(uint frameNumber) =>
        frameNumber > int.MaxValue ? int.MaxValue : (int)frameNumber;

    private void ValidateFrameIndex(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)_timingFrames.Length)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
    }

    private sealed class AsyncComputeTimingFrame
    {
        internal AsyncComputeTimingFrame(
            AsyncComputeFramePlan plan,
            IReadOnlyDictionary<AsyncComputePath, AsyncComputeTimingKey> keys)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            Keys = keys ?? throw new ArgumentNullException(nameof(keys));
        }

        internal AsyncComputeFramePlan Plan { get; }
        internal IReadOnlyDictionary<AsyncComputePath, AsyncComputeTimingKey>
            Keys { get; }
        internal long CpuSubmitMicroseconds { get; set; }
        internal long CpuBarrierRecordMicroseconds { get; set; }
    }
}
