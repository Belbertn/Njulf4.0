using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

public interface ISimpleDdgiReceiverFeedbackCapture
{
    bool IsOwnedCaptureReady { get; }

    bool TryBeginOwnedCapture(
        CommandBuffer commandBuffer,
        int frameIndex,
        uint viewportGeneration,
        ulong frameSerial,
        uint volumeTableGeneration,
        uint requiredProducerMask,
        out SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason);

    bool TryRecordOwnedProducerCompletion(
        CommandBuffer commandBuffer,
        int frameIndex,
        SimpleDdgiReceiverFeedbackProducer completedProducer,
        out string reason);

    bool HasPendingOwnedCapture(int frameIndex);

    bool IsPendingOwnedProducerRequired(
        int frameIndex,
        SimpleDdgiReceiverFeedbackProducer producer);

    void AbortCapture(
        string reason = "receiver-feedback-capture-aborted");
}

/// <summary>
/// Owns the renderer-level B1 plan, configuration identity, Vulkan runtime,
/// scheduling handshake, and capture finalization boundary.
/// </summary>
internal sealed class SimpleDdgiReceiverFeedbackCoordinator :
    ISimpleDdgiReceiverFeedbackCapture,
    IDisposable
{
    internal const ulong ExperimentBudgetBytes = 32UL * 1024UL * 1024UL;

    private readonly SimpleDdgiReceiverFeedbackVulkanRuntime _runtime;
    private SimpleDdgiReceiverFeedbackConfigurationKey? _configurationKey;
    private bool _disposed;

    public SimpleDdgiReceiverFeedbackPlan Plan { get; private set; } =
        SimpleDdgiReceiverFeedbackPlan.Disabled();

    public bool GraphicsPipelinesRequested { get; }

    public bool IsOwnedCaptureReady =>
        !_disposed && _runtime.IsOwnedCaptureReady;

    public SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics Diagnostics =>
        _disposed
            ? SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics.Disabled
            : _runtime.Diagnostics;

    internal void SetPipelineCacheService(
        GiPipelineCacheService pipelineCacheService) =>
        _runtime.SetPipelineCacheService(pipelineCacheService);

    public SimpleDdgiReceiverFeedbackCoordinator(
        VulkanContext context,
        BufferManager bufferManager,
        Action waitForDescriptorReaders,
        AdvancedGiTransientBufferArena transientBufferArena,
        GlobalIlluminationSettings settings,
        in AdvancedGiPrerequisiteGateResult prerequisiteGate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GraphicsPipelinesRequested = ShouldCreateGraphicsPipelines(
            settings,
            prerequisiteGate);
        _runtime = new SimpleDdgiReceiverFeedbackVulkanRuntime(
            context,
            bufferManager,
            waitForDescriptorReaders,
            transientBufferArena);
    }

    public static bool ShouldCreateGraphicsPipelines(
        GlobalIlluminationSettings settings,
        in AdvancedGiPrerequisiteGateResult prerequisiteGate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EffectiveUseDdgi)
            return false;
        return settings.SimpleDdgiReceiverFeedbackMode ==
               SimpleDdgiReceiverFeedbackMode.ExactCompacted ||
               settings.SimpleDdgiReceiverFeedbackMode ==
               SimpleDdgiReceiverFeedbackMode.AutoQualified &&
               prerequisiteGate.Passed;
    }

    public static SimpleDdgiReceiverFeedbackProductionWorkload
        CompileProductionWorkload(
            RenderSettings settings,
            Extent2D extent,
            ulong screenTileCount)
    {
        ArgumentNullException.ThrowIfNull(settings);

        static ulong DivideRoundUp(ulong value, ulong divisor) =>
            value == 0UL ? 0UL : 1UL + (value - 1UL) / divisor;

        ulong fogWorkgroups = settings.Fog.Enabled
            ? checked(
                DivideRoundUp(extent.Width, 8UL) *
                DivideRoundUp(extent.Height, 8UL))
            : 0UL;
        uint maximumParticleCount =
            settings.Particles.Enabled &&
            settings.GlobalIllumination.SimpleDdgiParticlesEnabled
                ? checked((uint)settings.Particles.MaxParticles)
                : 0u;
        ulong reflectionCaptureTiles = 0UL;
        if (settings.Reflections.Enabled &&
            settings.Reflections.CaptureIncludesDdgi &&
            settings.Reflections.MaxProbeCapturesPerFrame > 0 &&
            settings.Reflections.MaxProbeCaptureFacesPerFrame > 0)
        {
            ulong faceTiles = checked(
                DivideRoundUp(
                    settings.Reflections.ProbeResolution,
                    ForwardPlusPass.SimpleDdgiReceiverGatherScale) *
                DivideRoundUp(
                    settings.Reflections.ProbeResolution,
                    ForwardPlusPass.SimpleDdgiReceiverGatherScale));
            reflectionCaptureTiles = checked(
                faceTiles *
                (ulong)settings.Reflections.MaxProbeCapturesPerFrame *
                (ulong)settings.Reflections.MaxProbeCaptureFacesPerFrame);
        }

        return new SimpleDdgiReceiverFeedbackProductionWorkload(
            screenTileCount,
            fogWorkgroups,
            maximumParticleCount,
            reflectionCaptureTiles,
            SimpleDdgiReceiverFeedbackProductionWorkload
                .DefaultMaximumTransparentLayersPerTile);
    }

    public SimpleDdgiReceiverFeedbackDesiredState CompileDesiredState(
        in SimpleDdgiReceiverFeedbackRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SimpleDdgiReceiverFeedbackMode requestedMode = request.SimpleDdgiActive
            ? request.RequestedMode
            : SimpleDdgiReceiverFeedbackMode.Off;
        bool prerequisitesSatisfied =
            AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                requestedMode,
                request.PrerequisiteGate);
        AdvancedGiQualificationGateResult qualification =
            request.QualificationGate;
        if (AdvancedGiActivationPolicy.RequiresQualification(requestedMode) &&
            !request.RuntimeContentMatched)
        {
            qualification = AdvancedGiQualificationGateResult.Reject(
                request.RuntimeContentMismatchReason,
                qualification.QualificationId);
        }

        var key = new SimpleDdgiReceiverFeedbackConfigurationKey(
            requestedMode,
            request.SimpleDdgiActive,
            request.PhysicalProbeCapacity,
            request.ProducerWorkload,
            request.PageGeneration,
            request.MaximumStorageBufferRange,
            prerequisitesSatisfied,
            request.PrerequisiteGate.QualificationId,
            qualification.Passed,
            qualification.QualificationId,
            request.RuntimeContentMatched,
            request.MemoryHeadroom >= ExperimentBudgetBytes);
        SimpleDdgiReceiverFeedbackPlan plan = CompilePlan(
            requestedMode,
            request.PhysicalProbeCapacity,
            request.ProducerWorkload,
            request.PageGeneration,
            request.MaximumStorageBufferRange,
            request.MemoryHeadroom,
            request.PrerequisiteGate,
            qualification,
            request.ConfiguredQualificationId);
        return new SimpleDdgiReceiverFeedbackDesiredState(
            plan,
            key,
            prerequisitesSatisfied,
            ConfigurationChanged: _configurationKey != key);
    }

    public bool TryApplyConfiguration(
        in SimpleDdgiReceiverFeedbackDesiredState desired,
        bool arenaReady,
        string arenaFailure,
        out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SimpleDdgiReceiverFeedbackPlan plan = desired.Plan;
        if (!arenaReady)
            plan = RejectForTransientArena(plan, arenaFailure);

        if (!_runtime.TryConfigureOwned(
                plan,
                desired.PrerequisitesSatisfied,
                out string runtimeFailure) &&
            plan.UsesExactCompacted)
        {
            plan = RejectForTransientArena(
                plan,
                string.IsNullOrWhiteSpace(runtimeFailure)
                    ? "receiver-feedback-owned-runtime-rejected"
                    : runtimeFailure);
            _ = _runtime.TryConfigureOwned(
                plan,
                desired.PrerequisitesSatisfied,
                out _);
        }

        Plan = plan;
        _configurationKey = desired.Key;
        reason = plan.Mode.FallbackDetail;
        return plan.UsesExactCompacted;
    }

    public bool TryRegisterDescriptors(
        BindlessHeap bindlessHeap,
        BufferHandle safeFallbackBuffer,
        ulong safeFallbackBufferBytes,
        out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _runtime.TryRegisterDescriptors(
            bindlessHeap,
            safeFallbackBuffer,
            safeFallbackBufferBytes,
            out reason);
    }

    public bool TryGetPublishedRefinementWitness(
        uint viewportGeneration,
        uint volumeTableGeneration,
        ulong schedulingFrameSerial,
        out SimpleDdgiReceiverFeedbackRefinementWitness witness)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _runtime.TryGetPublishedRefinementWitness(
            viewportGeneration,
            volumeTableGeneration,
            schedulingFrameSerial,
            out witness);
    }

    public SimpleDdgiReceiverFeedbackGpuSchedulingBinding
        AcquireSchedulingBinding(
            in SimpleDdgiReceiverFeedbackSchedulingRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.ViewportGeneration == 0u)
        {
            return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                "receiver-feedback-runtime-or-render-targets-unavailable");
        }

        SimpleDdgiReceiverFeedbackGpuSchedulingBinding binding =
            _runtime.AcquirePendingForGpuScheduling(
                request.ViewportGeneration,
                request.FrameSerial);
        if (binding.UseFeedback &&
            !_runtime.TryRecordSchedulingReadBarrier(
                request.CommandBuffer,
                binding,
                out string reason))
        {
            return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                reason);
        }

        return binding;
    }

    public bool TryBeginOwnedCapture(
        CommandBuffer commandBuffer,
        int frameIndex,
        uint viewportGeneration,
        ulong frameSerial,
        uint volumeTableGeneration,
        uint requiredProducerMask,
        out SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _runtime.TryBeginOwnedCapture(
            commandBuffer,
            frameIndex,
            viewportGeneration,
            frameSerial,
            volumeTableGeneration,
            requiredProducerMask,
            out producer,
            out reason);
    }

    public bool TryRecordOwnedProducerCompletion(
        CommandBuffer commandBuffer,
        int frameIndex,
        SimpleDdgiReceiverFeedbackProducer completedProducer,
        out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _runtime.TryRecordOwnedProducerCompletion(
            commandBuffer,
            frameIndex,
            completedProducer,
            out reason);
    }

    public bool HasPendingOwnedCapture(int frameIndex) =>
        !_disposed && _runtime.HasPendingOwnedCapture(frameIndex);

    public bool IsPendingOwnedProducerRequired(
        int frameIndex,
        SimpleDdgiReceiverFeedbackProducer producer) =>
        !_disposed &&
        _runtime.IsPendingOwnedProducerRequired(frameIndex, producer);

    public void FinalizeAfterAllReceiverProducers(
        CommandBuffer commandBuffer,
        int frameIndex,
        GpuTimestampRecorder timestamps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(timestamps);
        if (!_runtime.HasPendingOwnedCapture(frameIndex))
            return;

        if (_runtime.IsPendingOwnedProducerRequired(
                frameIndex,
                SimpleDdgiReceiverFeedbackProducer
                    .RefinementOrBaseFallback) &&
            !_runtime.TryRecordOwnedProducerCompletion(
                commandBuffer,
                frameIndex,
                SimpleDdgiReceiverFeedbackProducer
                    .RefinementOrBaseFallback,
                out string refinementReason))
        {
            _runtime.AbortCapture(
                "receiver-feedback-refinement-completion-failed:" +
                refinementReason);
            return;
        }

        if (!_runtime.TryRecordPendingOwnedReduction(
                commandBuffer,
                frameIndex,
                timestamps,
                out string reason))
        {
            _runtime.AbortCapture(string.IsNullOrWhiteSpace(reason)
                ? "receiver-feedback-late-frame-reduction-rejected"
                : reason);
        }
    }

    public void CompleteFrameAfterFence(
        int frameIndex,
        ulong expectedFrameSerial)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = _runtime.TryReadCompletedFrame(
            frameIndex,
            expectedFrameSerial,
            out _);
    }

    public void AbortCapture(
        string reason = "receiver-feedback-capture-aborted")
    {
        if (!_disposed)
            _runtime.AbortCapture(reason);
    }

    public SimpleDdgiReceiverFeedbackSnapshot CaptureSnapshot() => new(
        Plan,
        Diagnostics,
        GraphicsPipelinesRequested,
        IsOwnedCaptureReady);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runtime.Dispose();
    }

    public static bool ShouldReconcileAfterUpload(
        bool currentCommandBufferReferencesSummaryBank) =>
        !currentCommandBufferReferencesSummaryBank;

    private static SimpleDdgiReceiverFeedbackPlan RejectForTransientArena(
        in SimpleDdgiReceiverFeedbackPlan plan,
        string failure)
    {
        GiExperimentModeState<SimpleDdgiReceiverFeedbackMode> mode =
            plan.Mode with
            {
                EffectiveMode = SimpleDdgiReceiverFeedbackMode.Off,
                FallbackReason =
                GiExperimentFallbackReason.ResourceIncomplete,
                FallbackDetail = string.IsNullOrWhiteSpace(failure)
                    ? "advanced-gi-transient-arena-rejected"
                    : failure
            };
        return new SimpleDdgiReceiverFeedbackPlan(
            mode,
            SimpleDdgiReceiverFeedbackLayout.Empty,
            SimpleDdgiAdvancedExperimentMemoryPlan
                .CreateReceiverFeedbackRejected(
                    GiExperimentFallbackReason.ResourceIncomplete));
    }

    private static SimpleDdgiReceiverFeedbackPlan CompilePlan(
        SimpleDdgiReceiverFeedbackMode requestedMode,
        int physicalProbeCapacity,
        in SimpleDdgiReceiverFeedbackProductionWorkload producerWorkload,
        uint maximumPagePublicationGeneration,
        ulong maximumStorageBufferRange,
        ulong rendererMemoryHeadroom,
        in AdvancedGiPrerequisiteGateResult prerequisiteGate,
        in AdvancedGiQualificationGateResult qualificationGate,
        string? configuredQualificationId)
    {
        if (requestedMode is SimpleDdgiReceiverFeedbackMode.Off or
            SimpleDdgiReceiverFeedbackMode.LegacyPackedReference)
        {
            return SimpleDdgiReceiverFeedbackPlanner.Compile(
                requestedMode,
                default,
                ReadOnlySpan<SimpleDdgiReceiverFeedbackProducerQuota>.Empty,
                default);
        }

        SimpleDdgiReceiverFeedbackPlan lastPlan =
            SimpleDdgiReceiverFeedbackPlan.Disabled();
        ReadOnlySpan<double> samplingProbabilities =
        [
            1.0 / 8.0,
            1.0 / 16.0,
            1.0 / 32.0,
            1.0 / 64.0,
            1.0 / 128.0,
            1.0 / 256.0,
            1.0 / 512.0,
            1.0 / 1024.0,
            1.0 / 2048.0,
            1.0 / 4096.0
        ];
        Span<SimpleDdgiReceiverFeedbackProducerQuota> producerQuotas =
            stackalloc SimpleDdgiReceiverFeedbackProducerQuota[
                SimpleDdgiReceiverFeedbackProductionQuotaPlan
                    .NonOpaqueQuotaCount];
        foreach (double samplingProbability in samplingProbabilities)
        {
            if (!SimpleDdgiReceiverFeedbackProductionQuotaPlanner.TryCompile(
                    producerWorkload,
                    samplingProbability,
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi
                        .MaximumUniqueGatherOwnersPerTile,
                    out SimpleDdgiReceiverFeedbackProductionQuotaPlan quotaPlan,
                    out string quotaFailure))
            {
                return new SimpleDdgiReceiverFeedbackPlan(
                    new GiExperimentModeState<
                        SimpleDdgiReceiverFeedbackMode>(
                        requestedMode,
                        SimpleDdgiReceiverFeedbackMode.Off,
                        SimpleDdgiReceiverFeedbackMode.Off,
                        SimpleDdgiReceiverFeedbackMode.Off,
                        GiExperimentFallbackReason.InvalidConfiguration,
                        quotaFailure,
                        prerequisiteGate.QualificationId),
                    SimpleDdgiReceiverFeedbackLayout.Empty,
                    SimpleDdgiAdvancedExperimentMemoryPlan
                        .CreateReceiverFeedbackRejected(
                            GiExperimentFallbackReason
                                .InvalidConfiguration));
            }

            quotaPlan.WriteNonOpaqueQuotas(producerQuotas);
            var layoutRequest =
                new SimpleDdgiReceiverFeedbackLayoutRequest(
                    physicalProbeCapacity,
                    producerWorkload.SourceScreenTileCount,
                    samplingProbability,
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi
                        .MaximumUniqueGatherOwnersPerTile,
                    SafetyMarginRecords: quotaPlan.SafetyMarginRecords,
                    WorkgroupSize:
                    SimpleDdgiReceiverFeedbackGpuSortAbi.WorkgroupSize,
                    SortScratchBytesPerRecord: 0UL,
                    IndependentMemoryBudgetBytes: ExperimentBudgetBytes,
                    RendererMemoryHeadroomBytes: rendererMemoryHeadroom,
                    MaximumStorageBufferRange: maximumStorageBufferRange,
                    MaximumPagePublicationGeneration:
                    maximumPagePublicationGeneration);
            lastPlan = SimpleDdgiReceiverFeedbackPlanner.Compile(
                requestedMode,
                layoutRequest,
                producerQuotas,
                new SimpleDdgiReceiverFeedbackPrerequisites(
                    ExactBackendSupported:
                    physicalProbeCapacity > 0 &&
                    producerWorkload.SourceScreenTileCount > 0UL &&
                    maximumStorageBufferRange > 0UL,
                    PrerequisitesSatisfied:
                    AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                        requestedMode,
                        prerequisiteGate),
                    ExactQualificationPassed: qualificationGate.Passed,
                    QualificationId:
                    AdvancedGiQualificationContract.NormalizeSha256(
                        configuredQualificationId),
                    ResourcesComplete: true));
            if (lastPlan.UsesExactCompacted ||
                lastPlan.Mode.FallbackReason is not (
                    GiExperimentFallbackReason
                        .IndependentMemoryBudgetExceeded or
                    GiExperimentFallbackReason
                        .RendererMemoryHeadroomExceeded))
            {
                break;
            }
        }

        return lastPlan;
    }
}

internal readonly record struct SimpleDdgiReceiverFeedbackConfigurationKey(
    SimpleDdgiReceiverFeedbackMode RequestedMode,
    bool SimpleDdgiActive,
    int PhysicalProbeCapacity,
    SimpleDdgiReceiverFeedbackProductionWorkload ProducerWorkload,
    uint PageGeneration,
    ulong MaximumStorageBufferRange,
    bool PrerequisiteGatePassed,
    string PrerequisiteQualificationId,
    bool QualificationPassed,
    string QualificationId,
    bool RuntimeContentMatched,
    bool ExperimentBudgetHeadroomAvailable);

internal readonly record struct SimpleDdgiReceiverFeedbackRequest(
    bool SimpleDdgiActive,
    SimpleDdgiReceiverFeedbackMode RequestedMode,
    int PhysicalProbeCapacity,
    SimpleDdgiReceiverFeedbackProductionWorkload ProducerWorkload,
    uint PageGeneration,
    ulong MaximumStorageBufferRange,
    ulong MemoryHeadroom,
    AdvancedGiPrerequisiteGateResult PrerequisiteGate,
    AdvancedGiQualificationGateResult QualificationGate,
    string? ConfiguredQualificationId,
    bool RuntimeContentMatched,
    string RuntimeContentMismatchReason);

internal readonly record struct SimpleDdgiReceiverFeedbackDesiredState(
    SimpleDdgiReceiverFeedbackPlan Plan,
    SimpleDdgiReceiverFeedbackConfigurationKey Key,
    bool PrerequisitesSatisfied,
    bool ConfigurationChanged);

internal readonly record struct SimpleDdgiReceiverFeedbackSchedulingRequest(
    CommandBuffer CommandBuffer,
    uint ViewportGeneration,
    ulong FrameSerial);

internal readonly record struct SimpleDdgiReceiverFeedbackSnapshot(
    SimpleDdgiReceiverFeedbackPlan Plan,
    SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics Diagnostics,
    bool GraphicsPipelinesRequested,
    bool IsOwnedCaptureReady);
