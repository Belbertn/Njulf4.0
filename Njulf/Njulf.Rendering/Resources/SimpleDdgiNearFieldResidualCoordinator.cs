using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns C5 admission and the fence-backed active/pending/retired generation
/// state. Renderer-owned target, pipeline, and graph effects are exposed as
/// explicit publications and are never applied through a renderer callback.
/// </summary>
internal sealed unsafe class SimpleDdgiNearFieldResidualCoordinator :
    IDisposable
{
    internal const ulong ExperimentBudgetBytes = 96UL * 1024UL * 1024UL;
    internal const ulong HotSwapBudgetBytes = 192UL * 1024UL * 1024UL;
    internal const ulong RecoveryValidationWindowFrames = 8UL;

    private readonly VulkanContext _context;

    private SimpleDdgiNearFieldResidualProfile _requestedProfile =
        SimpleDdgiNearFieldResidualProfile.ForPreset(
            SimpleDdgiNearFieldResidualQualityPreset.Balanced,
            0.25f);

    private SimpleDdgiNearFieldResidualProfile _effectiveProfile =
        SimpleDdgiNearFieldResidualProfile.ForPreset(
            SimpleDdgiNearFieldResidualQualityPreset.Balanced,
            0.25f);

    private SimpleDdgiNearFieldResidualExecutionScale _startupScale =
        SimpleDdgiNearFieldResidualExecutionScale.Quarter;

    private SimpleDdgiNearFieldResidualVulkanRuntime? _runtime;

    private SimpleDdgiNearFieldResidualGenerationTransaction<
        SimpleDdgiNearFieldResidualVulkanGenerationResources>? _generations;

    private NearFieldResidualInitializationRequest _initialization;
    private SimpleDdgiNearFieldResidualPrerequisites _prerequisites;
    private SimpleDdgiNearFieldResidualQualificationEvidence _evidence;
    private SimpleDdgiNearFieldResidualAdmissionContext _admissionContext;
    private AdvancedGiNearFieldCandidateDocument? _candidate;
    private AdvancedGiCandidateAuthorization _candidateAuthorization;
    private bool _hasEvidence;
    private bool _usesCandidateAuthorization;
    private bool _recoveryRebuildAttempted;
    private uint _recoveryRebuildAttemptCount;
    private string _lastRecoveryFailureReason = string.Empty;
    private bool _recoveryRebuildPendingCommit;
    private bool _awaitingRecoveryWitness;
    private ulong _recoveryValidationDeadlineFrame;
    private ulong _recoveryGeneration;
    private bool _terminalRetirementPending;
    private string _terminalRetirementReason = string.Empty;
    private bool _disposed;
    private string _reason = "near-field-disabled";

    public SimpleDdgiNearFieldResidualPlan Plan { get; private set; }

    public GiExperimentModeState<SimpleDdgiNearFieldResidualMode> Mode { get; private set; } =
        GiExperimentModeState<SimpleDdgiNearFieldResidualMode>.Disabled(
            SimpleDdgiNearFieldResidualMode.Off);

    public SimpleDdgiNearFieldResidualProfile EffectiveProfile =>
        _effectiveProfile;

    public SimpleDdgiNearFieldResidualGpuConfiguration GpuConfiguration { get; private set; }

    public ForwardNearFieldDirectSourcePipelineConfiguration
        PipelineConfiguration { get; private set; } =
        ForwardNearFieldDirectSourcePipelineConfiguration.Disabled;

    public bool UsesCandidateAuthorization =>
        _usesCandidateAuthorization;

    public bool IsGenerationExecutable
    {
        get
        {
            if (_disposed || _runtime is not { IsActive: true } runtime ||
                _generations is not { } generations ||
                !generations.CanExecuteFor(Plan.Layout) ||
                !generations.TryGetActiveAllocation(out var allocation) ||
                allocation is null)
            {
                return false;
            }

            return ReferenceEquals(allocation.Resources.Runtime, runtime);
        }
    }

    public SimpleDdgiNearFieldResidualGpuRuntimeSnapshot RuntimeSnapshot =>
        _runtime?.Snapshot ?? default;

    public SimpleDdgiNearFieldResidualGenerationSnapshot GenerationSnapshot =>
        _generations?.Snapshot ?? default;

    public SimpleDdgiNearFieldResidualDiagnostics Diagnostics
    {
        get
        {
            if (_runtime is { } runtime)
            {
                SimpleDdgiNearFieldResidualDiagnostics diagnostics =
                    runtime.Diagnostics;
                if (_generations is not { } liveGenerations)
                    return diagnostics;

                SimpleDdgiNearFieldResidualGenerationSnapshot generation =
                    liveGenerations.Snapshot;
                return diagnostics with
                {
                    Memory = new SimpleDdgiNearFieldResidualMemoryTelemetry(
                        Plan.Layout.TotalBytes,
                        Plan.Active ? Plan.Layout.TotalBytes : 0UL,
                        generation.LiveBytes,
                        generation.PeakLiveBytes,
                        generation.RetiredBytes),
                    Recovery = diagnostics.Recovery with
                    {
                        GenerationRebuildAttemptCount =
                            _recoveryRebuildAttemptCount,
                        GenerationRebuildPending =
                            _awaitingRecoveryWitness ||
                            runtime.RequiresGenerationRebuild,
                        ValidationDeadlineFrame = _awaitingRecoveryWitness
                            ? _recoveryValidationDeadlineFrame
                            : 0UL,
                        LastFailureReason = string.IsNullOrWhiteSpace(
                            diagnostics.Recovery.LastFailureReason)
                                ? _lastRecoveryFailureReason
                                : diagnostics.Recovery.LastFailureReason
                    }
                };
            }

            if (_generations is { } generations)
            {
                SimpleDdgiNearFieldResidualGenerationSnapshot generation =
                    generations.Snapshot;
                var memory = new SimpleDdgiNearFieldResidualMemoryTelemetry(
                    Plan.Layout.TotalBytes,
                    Plan.Active ? Plan.Layout.TotalBytes : 0UL,
                    generation.LiveBytes,
                    generation.PeakLiveBytes,
                    generation.RetiredBytes);
                return generation.HasActive || generation.HasPending ||
                       generation.HasRetired
                    ? SimpleDdgiNearFieldResidualDiagnostics
                        .PendingGpuReadback(memory, generation.State)
                    : SimpleDdgiNearFieldResidualDiagnostics
                        .PendingRendererIntegration(memory, generation.State);
            }

            return Plan.Requested
                ? SimpleDdgiNearFieldResidualDiagnostics
                    .PendingRendererIntegration(
                        new SimpleDdgiNearFieldResidualMemoryTelemetry(
                            Plan.AllocatedBytes,
                            Plan.Active ? Plan.AllocatedBytes : 0UL,
                            0UL,
                            0UL,
                            0UL),
                        Plan.Status)
                : SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                    Plan.Status);
        }
    }

    public SimpleDdgiNearFieldResidualCoordinator(VulkanContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void ConfigureEvidenceProfile(
        in SimpleDdgiNearFieldResidualProfile profile,
        SimpleDdgiNearFieldResidualExecutionScale startupScale)
    {
        ThrowIfDisposed();
        _requestedProfile = profile;
        _startupScale = startupScale;
    }

    public void SetStartupScale(
        SimpleDdgiNearFieldResidualExecutionScale startupScale)
    {
        ThrowIfDisposed();
        _startupScale = startupScale;
    }

    public void ClearConfiguredEvidence()
    {
        ThrowIfDisposed();
        _requestedProfile = SimpleDdgiNearFieldResidualProfile.ForPreset(
            SimpleDdgiNearFieldResidualQualityPreset.Balanced,
            0.25f);
        _startupScale = SimpleDdgiNearFieldResidualExecutionScale.Quarter;
    }

    public NearFieldResidualInitializationResult Initialize(
        in NearFieldResidualInitializationRequest request)
    {
        ThrowIfDisposed();
        _initialization = request;
        _hasEvidence = request.HasQualificationEvidence;
        _evidence = request.HasQualificationEvidence
            ? request.QualificationEvidence
            : default;
        _admissionContext = request.HasQualificationEvidence
            ? request.QualificationAdmissionContext
            : request.Candidate?.AdmissionContext ?? default;
        _candidate = request.CandidateAuthorized ? request.Candidate : null;
        _candidateAuthorization = request.CandidateAuthorization;
        _usesCandidateAuthorization = _candidate is not null;
        _recoveryRebuildAttempted = false;
        _recoveryRebuildAttemptCount = 0U;
        _lastRecoveryFailureReason = string.Empty;
        _recoveryRebuildPendingCommit = false;
        _awaitingRecoveryWitness = false;
        _recoveryValidationDeadlineFrame = 0UL;
        _recoveryGeneration = 0UL;
        _terminalRetirementPending = false;
        _terminalRetirementReason = string.Empty;

        SimpleDdgiNearFieldResidualProfile profile =
            _candidate?.Configuration.Profile ??
            (request.RequestedMode ==
             SimpleDdgiNearFieldResidualMode.AutoQualified
                ? _requestedProfile
                : ResolveExplicitProfile(
                    request.SceneRenderExtent,
                    request.RequestedMode,
                    request.Settings));
        profile = ApplyAdvancedOverrides(profile, request.Settings);
        _effectiveProfile = profile;

        bool gpuRequested = IsGpuMode(request.RequestedMode);
        SimpleDdgiNearFieldResidualLayout preliminaryLayout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                checked((int)request.SceneRenderExtent.Width),
                checked((int)request.SceneRenderExtent.Height),
                profile,
                ExperimentBudgetBytes);
        SimpleDdgiNearFieldTraceSourceContract sourceContract =
            request.HasQualificationEvidence
                ? request.QualificationEvidence.Binding.TraceSourceContract
                    with
                    {
                        Extent = CreateScaledExtent(
                            preliminaryLayout,
                            profile.ResolutionScale)
                    }
                : _candidate is not null
                    ? _candidate.Configuration.SourceContract
                    : preliminaryLayout.IsValid
                        ? SimpleDdgiNearFieldTraceSourceContract
                            .CreatePreDdgiDirectDiffuseAndEmissive(
                                preliminaryLayout,
                                profile)
                        : default;
        SimpleDdgiNearFieldResidualConfiguration configuration =
            _candidate is not null
                ? _candidate.Configuration with { Enabled = gpuRequested }
                : new SimpleDdgiNearFieldResidualConfiguration(
                    Enabled: gpuRequested,
                    Width: checked((int)request.SceneRenderExtent.Width),
                    Height: checked((int)request.SceneRenderExtent.Height),
                    MemoryBudgetBytes: ExperimentBudgetBytes,
                    Profile: profile,
                    SourceContract: sourceContract);
        _prerequisites = CreatePrerequisites(
            request.PrerequisiteGate,
            request.HasQualificationEvidence,
            _evidence);

        bool publishAdmissionContext;
        if (_candidate is not null)
        {
            _requestedProfile = profile;
            Plan = SimpleDdgiNearFieldResidualExperiment.CreateCandidatePlan(
                configuration,
                _prerequisites,
                _admissionContext,
                _candidateAuthorization);
            publishAdmissionContext = true;
        }
        else if (request.RequestedMode is
                 SimpleDdgiNearFieldResidualMode
                     .HiZHalfResolutionExperiment or
                 SimpleDdgiNearFieldResidualMode.HiZAdaptive)
        {
            _admissionContext = default;
            _requestedProfile = profile;
            Plan = SimpleDdgiNearFieldResidualExperiment.CreateExplicitPlan(
                configuration,
                _prerequisites);
            publishAdmissionContext = true;
        }
        else
        {
            Plan = SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                _prerequisites,
                _evidence,
                _admissionContext);
            publishAdmissionContext = false;
        }

        bool runtimeSupported = TryValidateRuntimePreflight(
            Plan,
            out string preflightFailure);
        AdvancedGiQualificationGateResult qualification =
            EvaluateQualification(request, runtimeSupported);
        Mode = AdvancedGiAdmissionCoordinator.ResolveMode(
            request.RequestedMode,
            SimpleDdgiNearFieldResidualMode.Off,
            supported: runtimeSupported,
            prerequisiteGate: request.PrerequisiteGate,
            qualificationGate: qualification,
            resourcesComplete: Plan.Active,
            request.ConfiguredQualificationId,
            Plan.Active
                ? "valid"
                : string.IsNullOrWhiteSpace(preflightFailure)
                    ? Plan.Status
                    : preflightFailure);

        if (IsGpuMode(Mode.EffectiveMode))
            ConfigureGpuState(profile, sourceContract);
        else
            ClearGpuState();

        if (request.CausticEffective && IsGpuMode(Mode.EffectiveMode))
            EnforceCombinedForwardPreflight();

        _reason = Mode.IsEffective ? "valid" : Mode.FallbackDetail;
        return new NearFieldResidualInitializationResult(
            Mode,
            Plan,
            PipelineConfiguration,
            profile,
            _admissionContext,
            publishAdmissionContext,
            _usesCandidateAuthorization,
            runtimeSupported,
            preflightFailure);
    }

    public SimpleDdgiNearFieldResidualVulkanRuntime CreateRuntime(
        in NearFieldResidualRuntimeAllocationRequest request,
        in SimpleDdgiNearFieldResidualLayout layout,
        SimpleDdgiNearFieldResidualRenderTargetGeneration targetGeneration)
    {
        ThrowIfDisposed();
        if (!Plan.Layout.Equals(layout) ||
            !targetGeneration.Layout.Equals(layout))
        {
            throw new InvalidOperationException(
                "C5 runtime allocation does not match the requested generation layout.");
        }

        return new SimpleDdgiNearFieldResidualVulkanRuntime(
            _context,
            request.BufferManager,
            request.BindlessHeap,
            request.RenderTargets,
            request.HiZDepthPyramid,
            request.FoliageManager,
            layout,
            GpuConfiguration,
            _initialization.RequestedMode is
                SimpleDdgiNearFieldResidualMode.HiZAdaptive or
                SimpleDdgiNearFieldResidualMode.AutoQualified,
            _initialization.RequestedMode is
                SimpleDdgiNearFieldResidualMode
                    .HiZHalfResolutionExperiment or
                SimpleDdgiNearFieldResidualMode.HiZAdaptive
                ? SimpleDdgiNearFieldResidualEvidenceAbi.Version
                : _admissionContext.B3QualificationRevision,
            ExperimentBudgetBytes,
            calibratedSourceCostUpperBoundMicroseconds:
            _hasEvidence
                ? checked((ulong)Math.Ceiling(
                    _evidence.SourceMrtCostUpperBoundMilliseconds * 1000.0))
                : 0UL,
            sourceCostAuthoritative:
            _initialization.RequestedMode ==
            SimpleDdgiNearFieldResidualMode.AutoQualified &&
            _hasEvidence && _evidence.SourceCostAuthoritative,
            startingScale:
            _initialization.RequestedMode ==
            SimpleDdgiNearFieldResidualMode.AutoQualified &&
            _hasEvidence
                ? _startupScale
                : null,
            promotionEnabled:
            _initialization.RequestedMode ==
            SimpleDdgiNearFieldResidualMode.AutoQualified,
            captureIdentifiers: _hasEvidence
                ? new SimpleDdgiNearFieldResidualCaptureIdentifiers(
                    _evidence.BenchmarkCaptureId,
                    _evidence.ReferenceManifestId)
                : SimpleDdgiNearFieldResidualCaptureIdentifiers.None,
            targetGeneration: targetGeneration);
    }

    public NearFieldResidualPublication InitializeRuntime(
        ISimpleDdgiNearFieldResidualGenerationBackend<
            SimpleDdgiNearFieldResidualVulkanGenerationResources> backend)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(backend);
        var generations =
            new SimpleDdgiNearFieldResidualGenerationTransaction<
                SimpleDdgiNearFieldResidualVulkanGenerationResources>(
                backend,
                ExperimentBudgetBytes,
                HotSwapBudgetBytes);
        _generations = generations;
        if (!generations.TryInitialize(Plan.Layout, out string failure) ||
            !TryCaptureActivePublication(out NearFieldResidualPublication result,
                out failure))
        {
            return RejectAtStartup(failure);
        }

        return result;
    }

    public void SetFrameAdmission(
        in AdvancedGiRuntimeContentState runtimeContentState)
    {
        ThrowIfDisposed();
        bool requiresContentMatch =
            AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                _initialization.RequestedMode,
                _usesCandidateAuthorization);
        _runtime?.SetFrameAdmission(
            !requiresContentMatch || runtimeContentState.Matched,
            runtimeContentState.Reason);
    }

    public NearFieldResidualPublication CompleteFrameAfterFence(
        int frameIndex,
        in FrameTimingSnapshot timestamps,
        ulong completedGraphicsFenceValue,
        ulong currentFrame)
    {
        ThrowIfDisposed();
        bool readbackAccepted = false;
        if (_runtime is { } runtime)
        {
            readbackAccepted = runtime.TryReadCompletedFrame(
                frameIndex,
                timestamps,
                out _);
            string failureReason = runtime.Diagnostics.Recovery.LastFailureReason;
            if (!string.IsNullOrWhiteSpace(failureReason))
                _lastRecoveryFailureReason = failureReason;
        }

        if (_terminalRetirementPending)
        {
            return ContinueTerminalRetirement(
                completedGraphicsFenceValue,
                currentFrame);
        }

        if (_recoveryRebuildPendingCommit)
        {
            NearFieldResidualPublication publication =
                AdvanceGenerationAtFrameBoundary(
                    completedGraphicsFenceValue,
                    currentFrame);
            if (publication.Executable)
            {
                BeginRecoveryValidationWindow(currentFrame);
                return publication;
            }

            return publication.Changed
                ? publication
                : NearFieldResidualPublication.Suppress(
                    PipelineConfiguration,
                    "near-field-recovery-rebuild-awaiting-frame-boundary");
        }

        if (_awaitingRecoveryWitness)
        {
            bool generationMatches = GenerationSnapshot.ActiveGeneration ==
                _recoveryGeneration;
            if (generationMatches && readbackAccepted &&
                _runtime is { HasValidCompletionWitness: true })
            {
                _awaitingRecoveryWitness = false;
                _recoveryValidationDeadlineFrame = 0UL;
                _recoveryGeneration = 0UL;
                _recoveryRebuildAttempted = false;
            }
            else if (!generationMatches ||
                     _runtime is { RequiresGenerationRebuild: true } ||
                     currentFrame >= _recoveryValidationDeadlineFrame)
            {
                return BeginTerminalRetirement(
                    "near-field-rebuilt-generation-did-not-produce-valid-witness",
                    completedGraphicsFenceValue,
                    currentFrame);
            }
        }

        if (_runtime is { RequiresGenerationRebuild: true })
        {
            if (_recoveryRebuildAttempted)
            {
                return BeginTerminalRetirement(
                    "near-field-telemetry-failed-after-generation-rebuild",
                    completedGraphicsFenceValue,
                    currentFrame);
            }

            return RequestRecoveryRebuild(
                completedGraphicsFenceValue,
                currentFrame);
        }

        return AdvanceGenerationAtFrameBoundary(
            completedGraphicsFenceValue,
            currentFrame);
    }

    private NearFieldResidualPublication RequestRecoveryRebuild(
        ulong completedGraphicsFenceValue,
        ulong currentFrame)
    {
        if (_generations is not { } generations)
        {
            return BeginTerminalRetirement(
                "near-field-recovery-generation-controller-unavailable",
                completedGraphicsFenceValue,
                currentFrame);
        }

        SimpleDdgiNearFieldResidualGenerationRequestResult request =
            generations.RequestRebuild(
                Plan.Layout,
                ResolveReplacementEnvelope(Plan.Layout));
        _recoveryRebuildAttempted = true;
        _recoveryRebuildAttemptCount = _recoveryRebuildAttemptCount ==
            uint.MaxValue
                ? uint.MaxValue
                : _recoveryRebuildAttemptCount + 1U;
        if (!request.Accepted)
        {
            return BeginTerminalRetirement(
                "near-field-recovery-rebuild-rejected:" + request.Reason,
                completedGraphicsFenceValue,
                currentFrame);
        }
        if (!request.ReplacementReady && !string.Equals(
                request.Reason,
                "replacement-deferred-until-retirement",
                StringComparison.Ordinal))
        {
            return BeginTerminalRetirement(
                "near-field-recovery-rebuild-allocation-failed:" +
                request.Reason,
                completedGraphicsFenceValue,
                currentFrame);
        }

        _recoveryRebuildPendingCommit = true;
        NearFieldResidualPublication publication =
            AdvanceGenerationAtFrameBoundary(
                completedGraphicsFenceValue,
                currentFrame);
        if (publication.Executable)
        {
            BeginRecoveryValidationWindow(currentFrame);
            return publication;
        }

        return publication.Changed
            ? publication
            : NearFieldResidualPublication.Suppress(
                PipelineConfiguration,
                request.ReplacementReady
                    ? "near-field-recovery-rebuild-awaiting-frame-boundary"
                    : request.Reason);
    }

    private void BeginRecoveryValidationWindow(ulong currentFrame)
    {
        _recoveryRebuildPendingCommit = false;
        _awaitingRecoveryWitness = true;
        _recoveryGeneration = GenerationSnapshot.ActiveGeneration;
        _recoveryValidationDeadlineFrame = ulong.MaxValue - currentFrame <
            RecoveryValidationWindowFrames
            ? ulong.MaxValue
            : currentFrame + RecoveryValidationWindowFrames;
    }

    private NearFieldResidualPublication BeginTerminalRetirement(
        string reason,
        ulong completedGraphicsFenceValue,
        ulong currentFrame)
    {
        _terminalRetirementPending = true;
        _terminalRetirementReason = string.IsNullOrWhiteSpace(reason)
            ? "near-field-persistent-telemetry-failure"
            : reason.Trim();
        _awaitingRecoveryWitness = false;
        _recoveryRebuildPendingCommit = false;
        _runtime?.SuppressForFenceSafeRetirement(_terminalRetirementReason);
        return ContinueTerminalRetirement(
            completedGraphicsFenceValue,
            currentFrame);
    }

    private NearFieldResidualPublication ContinueTerminalRetirement(
        ulong completedGraphicsFenceValue,
        ulong currentFrame)
    {
        if (_generations is not { } generations)
        {
            return FinalizeTerminalRetirement();
        }

        var progress = new GpuCompletionProgress(
            completedGraphicsFenceValue,
            0UL,
            0UL);
        _ = generations.PollCompleted(progress, currentFrame);
        _ = generations.TryBeginTerminalRetirement(
            currentFrame,
            out _);
        _ = generations.PollCompleted(progress, currentFrame);
        if (generations.Snapshot.LiveBytes != 0UL)
        {
            return NearFieldResidualPublication.Suppress(
                PipelineConfiguration,
                _terminalRetirementReason);
        }

        generations.Dispose();
        _generations = null;
        _runtime = null;
        return FinalizeTerminalRetirement();
    }

    private NearFieldResidualPublication FinalizeTerminalRetirement()
    {
        int filterIterationCount = Math.Max(
            0,
            Plan.Layout.FilterIterationCount);
        string reason = _terminalRetirementReason;
        _terminalRetirementPending = false;
        InvalidateState(
            reason,
            GiExperimentFallbackReason.ResourceIncomplete);
        return NearFieldResidualPublication.Disable(
            reason,
            filterIterationCount,
            releaseUnmanagedTargets: false);
    }

    public NearFieldResidualRecreationPreparation
        PrepareTargetRecreationAfterDeviceIdle(Extent2D nextSceneExtent)
    {
        ThrowIfDisposed();
        if (!IsGpuMode(Mode.EffectiveMode))
            return NearFieldResidualRecreationPreparation.Unchanged;
        SimpleDdgiNearFieldResidualLayout current = Plan.Layout;
        if (current.IsValid &&
            nextSceneExtent.Width == (uint)current.SourceWidth &&
            nextSceneExtent.Height == (uint)current.SourceHeight)
        {
            return NearFieldResidualRecreationPreparation.Unchanged;
        }

        if (!TryCompileGeneration(
                nextSceneExtent,
                out SimpleDdgiNearFieldResidualPlan plan,
                out SimpleDdgiNearFieldResidualGpuConfiguration gpu,
                out ForwardNearFieldDirectSourcePipelineConfiguration pipeline,
                out string failure))
        {
            NearFieldResidualPublication disabled =
                DisableAfterDeviceIdle(failure);
            return new NearFieldResidualRecreationPreparation(
                true,
                false,
                disabled,
                failure);
        }

        Plan = plan;
        GpuConfiguration = gpu;
        PipelineConfiguration = pipeline;
        _reason = "near-field-generation-prepared";
        return new NearFieldResidualRecreationPreparation(
            true,
            true,
            NearFieldResidualPublication.Suppress(
                PipelineConfiguration,
                _reason),
            _reason);
    }

    public NearFieldResidualPublication CompleteTargetRecreation(
        ulong completedGraphicsFenceValue,
        ulong currentFrame)
    {
        ThrowIfDisposed();
        if (!IsGpuMode(Mode.EffectiveMode) ||
            _generations is not { } generations)
        {
            return NearFieldResidualPublication.Unchanged;
        }

        try
        {
            var progress = new GpuCompletionProgress(
                completedGraphicsFenceValue,
                0UL,
                0UL);
            _ = generations.PollCompleted(progress, currentFrame);
            if (!generations.CanExecuteFor(Plan.Layout))
            {
                SimpleDdgiNearFieldResidualGenerationRequestResult request =
                    generations.RequestReplacement(
                        Plan.Layout,
                        ResolveReplacementEnvelope(Plan.Layout));
                if (!request.Accepted || !request.ReplacementReady)
                {
                    _reason = request.Reason;
                    return NearFieldResidualPublication.Suppress(
                        PipelineConfiguration,
                        request.Reason);
                }
            }

            NearFieldResidualPublication publication =
                AdvanceGenerationAtFrameBoundary(
                    completedGraphicsFenceValue,
                    currentFrame);
            _ = generations.PollCompleted(progress, currentFrame);
            return IsGenerationExecutable
                ? publication
                : NearFieldResidualPublication.Suppress(
                    PipelineConfiguration,
                    generations.Snapshot.State);
        }
        catch (Exception exception)
        {
            return DisableAfterDeviceIdle(
                "near-field-generation-allocation-failed:" +
                exception.GetType().Name);
        }
    }

    public NearFieldResidualPublication AdvanceGenerationAtFrameBoundary(
        ulong completedGraphicsFenceValue,
        ulong currentFrame)
    {
        ThrowIfDisposed();
        if (_generations is not { } generations)
            return NearFieldResidualPublication.Unchanged;

        var progress = new GpuCompletionProgress(
            completedGraphicsFenceValue,
            0UL,
            0UL);
        _ = generations.PollCompleted(progress, currentFrame);
        if (!generations.Snapshot.HasPending)
            return NearFieldResidualPublication.Unchanged;
        if (!generations.TryCommitAtFrameBoundary(
                greatestReferencingFrameFenceValue: 0UL,
                currentFrame,
                out string failure))
        {
            _reason = failure;
            return string.Equals(
                failure,
                "near-field-generation-retirement-slot-occupied",
                StringComparison.Ordinal)
                ? NearFieldResidualPublication.Unchanged
                : NearFieldResidualPublication.Suppress(
                    PipelineConfiguration,
                    failure);
        }

        if (!TryCaptureActivePublication(
                out NearFieldResidualPublication publication,
                out failure))
        {
            throw new InvalidOperationException(failure);
        }

        _ = generations.PollCompleted(progress, currentFrame);
        return publication;
    }

    public void ObserveSuccessfulSubmission(ulong graphicsFenceValue)
    {
        ThrowIfDisposed();
        _generations?.RecordActiveReference(graphicsFenceValue);
    }

    public NearFieldResidualPublication DisableAfterDeviceIdle(string reason)
    {
        ThrowIfDisposed();
        string detail = string.IsNullOrWhiteSpace(reason)
            ? "near-field-generation-unavailable"
            : reason.Trim();
        int filterIterationCount = Math.Max(
            0,
            Plan.Layout.FilterIterationCount);
        bool releaseUnmanagedTargets = _generations is null;
        if (_generations is { } generations)
        {
            generations.Dispose();
            _generations = null;
            _runtime = null;
        }
        else
        {
            _runtime?.DisableAndReleaseAfterDeviceIdle(detail);
            _runtime = null;
        }

        InvalidateState(
            detail,
            GiExperimentFallbackReason.EvidenceBindingMismatch);
        return NearFieldResidualPublication.Disable(
            detail,
            filterIterationCount,
            releaseUnmanagedTargets);
    }

    public NearFieldResidualGraphResourceSnapshot CaptureGraphResources() =>
        IsGenerationExecutable && _runtime is { } runtime
            ? new NearFieldResidualGraphResourceSnapshot(
                runtime,
                runtime.Buffers)
            : default;

    public NearFieldResidualCoordinatorSnapshot CaptureSnapshot() => new(
        Plan,
        Mode,
        _requestedProfile,
        _effectiveProfile,
        _startupScale,
        GpuConfiguration,
        PipelineConfiguration,
        RuntimeSnapshot,
        GenerationSnapshot,
        _usesCandidateAuthorization,
        IsGenerationExecutable,
        _reason);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _generations?.Dispose();
        _generations = null;
        _runtime = null;
    }

    private bool TryCompileGeneration(
        Extent2D nextSceneExtent,
        out SimpleDdgiNearFieldResidualPlan plan,
        out SimpleDdgiNearFieldResidualGpuConfiguration gpu,
        out ForwardNearFieldDirectSourcePipelineConfiguration pipeline,
        out string failure)
    {
        plan = default;
        gpu = default;
        pipeline = ForwardNearFieldDirectSourcePipelineConfiguration.Disabled;
        failure = "near-field-generation-not-requested";
        if (!IsGpuMode(Mode.EffectiveMode))
            return false;

        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                checked((int)nextSceneExtent.Width),
                checked((int)nextSceneExtent.Height),
                _effectiveProfile,
                ExperimentBudgetBytes);
        if (!layout.IsValid)
        {
            failure = layout.FailureReason;
            return false;
        }

        SimpleDdgiNearFieldTraceSourceContract sourceContract =
            GpuConfiguration.TraceSourceContract with
            {
                Extent = CreateScaledExtent(
                    layout,
                    _effectiveProfile.ResolutionScale)
            };
        var configuration = new SimpleDdgiNearFieldResidualConfiguration(
            Enabled: true,
            Width: layout.SourceWidth,
            Height: layout.SourceHeight,
            MemoryBudgetBytes: ExperimentBudgetBytes,
            Profile: _effectiveProfile,
            SourceContract: sourceContract);
        if (_usesCandidateAuthorization && _candidate is not null)
        {
            plan = SimpleDdgiNearFieldResidualExperiment.CreateCandidatePlan(
                configuration,
                _prerequisites,
                _admissionContext,
                _candidateAuthorization);
        }
        else if (_initialization.RequestedMode is
                 SimpleDdgiNearFieldResidualMode
                     .HiZHalfResolutionExperiment or
                 SimpleDdgiNearFieldResidualMode.HiZAdaptive)
        {
            plan = SimpleDdgiNearFieldResidualExperiment.CreateExplicitPlan(
                configuration,
                _prerequisites);
        }
        else
        {
            plan = SimpleDdgiNearFieldResidualExperiment.CreatePlan(
                configuration,
                _prerequisites,
                _evidence,
                _admissionContext);
        }

        if (!plan.Active)
        {
            failure = plan.Status;
            return false;
        }

        if (!TryValidateRuntimePreflight(plan, out failure))
            return false;

        gpu = CreateGpuConfiguration(plan.Layout, sourceContract);
        if (!gpu.Validate(plan.Layout).IsValid)
        {
            failure = "near-field-generation-gpu-configuration-invalid";
            return false;
        }

        pipeline = new ForwardNearFieldDirectSourcePipelineConfiguration(
            IsC5EffectivelyEnabled: true,
            sourceContract,
            ForwardNearFieldDirectSourceContract.ShaderSemanticVersion);
        if (!ForwardNearFieldDirectSourceContract
                .TryValidatePipelineConfiguration(pipeline, out failure))
        {
            return false;
        }

        failure = "valid";
        return true;
    }

    private bool TryCaptureActivePublication(
        out NearFieldResidualPublication publication,
        out string failure)
    {
        publication = NearFieldResidualPublication.Unchanged;
        failure = "near-field-generation-controller-unavailable";
        if (_generations is not { } generations ||
            !generations.TryGetActiveAllocation(out var allocation) ||
            allocation is null)
        {
            return false;
        }

        SimpleDdgiNearFieldResidualVulkanGenerationResources resources =
            allocation.Resources;
        if (!allocation.Layout.Equals(Plan.Layout) ||
            !resources.Runtime.IsActive ||
            !ForwardNearFieldDirectSourceContract
                .TryValidatePipelineConfiguration(
                    PipelineConfiguration,
                    out failure))
        {
            failure = string.IsNullOrWhiteSpace(failure)
                ? "near-field-generation-publication-invalid"
                : failure;
            return false;
        }

        _runtime = resources.Runtime;
        _reason = "valid";
        publication = NearFieldResidualPublication.Publish(
            resources.Targets,
            resources.Runtime,
            PipelineConfiguration);
        failure = "valid";
        return true;
    }

    private NearFieldResidualPublication RejectAtStartup(string reason)
    {
        string detail = string.IsNullOrWhiteSpace(reason)
            ? "near-field-generation-startup-allocation-failed"
            : reason.Trim();
        _generations?.Dispose();
        _generations = null;
        _runtime = null;
        InvalidateState(
            detail,
            GiExperimentFallbackReason.ResourceIncomplete);
        return NearFieldResidualPublication.Disable(
            detail,
            Math.Max(0, Plan.Layout.FilterIterationCount),
            releaseUnmanagedTargets: true);
    }

    private void InvalidateState(
        string reason,
        GiExperimentFallbackReason fallbackReason)
    {
        Plan = SimpleDdgiNearFieldResidualExperiment.InvalidateRuntimePlan(
            Plan,
            reason,
            fallbackReason);
        Mode = Mode with
        {
            AdmittedMode = SimpleDdgiNearFieldResidualMode.Off,
            EffectiveMode = SimpleDdgiNearFieldResidualMode.Off,
            FallbackReason = fallbackReason,
            FallbackDetail = reason
        };
        ClearGpuState();
        _reason = reason;
    }

    private void ConfigureGpuState(
        in SimpleDdgiNearFieldResidualProfile profile,
        in SimpleDdgiNearFieldTraceSourceContract sourceContract)
    {
        GpuConfiguration = CreateGpuConfiguration(Plan.Layout, sourceContract);
        PipelineConfiguration =
            new ForwardNearFieldDirectSourcePipelineConfiguration(
                IsC5EffectivelyEnabled: true,
                sourceContract,
                ForwardNearFieldDirectSourceContract.ShaderSemanticVersion);
    }

    private SimpleDdgiNearFieldResidualGpuConfiguration
        CreateGpuConfiguration(
            in SimpleDdgiNearFieldResidualLayout layout,
            in SimpleDdgiNearFieldTraceSourceContract sourceContract) =>
        SimpleDdgiNearFieldResidualGpuConfiguration.CreateReference(
                layout,
                _effectiveProfile,
                sourceContract.AbiRevision,
                sourceContract.LayoutRevision,
                sourceContract.SourceRevision) with
            {
                TraceSourceContract = sourceContract,
                MaximumTraceSteps = _effectiveProfile.MaximumTraceSteps,
                MaximumMipVisits = _effectiveProfile.MaximumMipVisits,
                BinaryRefinementSteps =
                _effectiveProfile.BinaryRefinementSteps,
                FilterIterationCount =
                _effectiveProfile.FilterIterationCount,
                ResidualIntensity = _initialization.Settings
                    .AdvancedOverridesEnabled
                    ? _initialization.Settings.ResidualIntensity
                    : 1.0f
            };

    private void ClearGpuState()
    {
        GpuConfiguration = default;
        PipelineConfiguration =
            ForwardNearFieldDirectSourcePipelineConfiguration.Disabled;
    }

    private void EnforceCombinedForwardPreflight()
    {
        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(
            _context.PhysicalDevice,
            &properties);
        if (properties.Limits.MaxColorAttachments >=
            ForwardAdvancedGiCombinedContract.ColorAttachmentCount)
        {
            return;
        }

        const string reason =
            "combined-C4-C5-forward-path-requires-four-color-attachments";
        InvalidateState(
            reason,
            GiExperimentFallbackReason.VulkanLimitExceeded);
    }

    private AdvancedGiQualificationGateResult EvaluateQualification(
        in NearFieldResidualInitializationRequest request,
        bool runtimeSupported)
    {
        if (!request.PrerequisiteGate.Passed)
        {
            return AdvancedGiQualificationGateResult.Reject(
                request.PrerequisiteGate.FailureDetail);
        }

        return request.QualificationManifest.Evaluate(
            AdvancedGiPrerequisiteFeature.NearFieldResidual,
            request.RuntimeQualificationContext with
            {
                FeatureSupported = runtimeSupported
            },
            request.PrerequisiteGate.QualificationId,
            request.ConfiguredQualificationId);
    }

    private bool TryValidateRuntimePreflight(
        in SimpleDdgiNearFieldResidualPlan plan,
        out string failure)
    {
        if (!plan.Active)
        {
            failure = plan.Status;
            return false;
        }

        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(
            _context.PhysicalDevice,
            &properties);
        if ((uint)plan.Layout.SourceWidth >
            properties.Limits.MaxImageDimension2D ||
            (uint)plan.Layout.SourceHeight >
            properties.Limits.MaxImageDimension2D ||
            properties.Limits.MaxColorAttachments <
            ForwardNearFieldDirectSourceContract.ColorAttachmentCount ||
            properties.Limits.MaxPushConstantsSize <
            SimpleDdgiNearFieldResidualGpuAbi
                .TemporalPushConstantByteCount)
        {
            failure = "near-field-device-image-MRT-or-push-constant-limit";
            return false;
        }

        if (!HasFormatFeatures(
                Format.R16G16B16A16Sfloat,
                FormatFeatureFlags.ColorAttachmentBit |
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit) ||
            !HasFormatFeatures(
                Format.R32G32B32A32Uint,
                FormatFeatureFlags.ColorAttachmentBit |
                FormatFeatureFlags.SampledImageBit) ||
            !HasFormatFeatures(
                Format.R16G16Sfloat,
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit) ||
            !HasFormatFeatures(
                Format.R32Uint,
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit))
        {
            failure = "near-field-required-format-features-unavailable";
            return false;
        }

        failure = "valid";
        return true;
    }

    private SimpleDdgiNearFieldResidualExtentEnvelope ResolveReplacementEnvelope(
        in SimpleDdgiNearFieldResidualLayout layout)
    {
        bool requiresArchivedEnvelope = _initialization.RequestedMode ==
                                        SimpleDdgiNearFieldResidualMode.AutoQualified &&
                                        !_usesCandidateAuthorization;
        return requiresArchivedEnvelope && _hasEvidence
            ? _evidence.Binding.ExtentEnvelope
            : SimpleDdgiNearFieldResidualExtentEnvelope.Exact(layout);
    }

    private bool HasFormatFeatures(
        Format format,
        FormatFeatureFlags required)
    {
        FormatProperties properties = default;
        _context.Api.GetPhysicalDeviceFormatProperties(
            _context.PhysicalDevice,
            format,
            &properties);
        return (properties.OptimalTilingFeatures & required) == required;
    }

    private static SimpleDdgiNearFieldResidualProfile ResolveExplicitProfile(
        Extent2D sceneRenderExtent,
        SimpleDdgiNearFieldResidualMode mode,
        in NearFieldResidualSettings settings)
    {
        ReadOnlySpan<float> scales =
            mode == SimpleDdgiNearFieldResidualMode.HiZAdaptive
                ? [0.25f, 0.125f]
                : [0.5f, 0.25f, 0.125f];
        int width = checked((int)sceneRenderExtent.Width);
        int height = checked((int)sceneRenderExtent.Height);
        foreach (float scale in scales)
        {
            SimpleDdgiNearFieldResidualProfile profile =
                ApplyAdvancedOverrides(
                    SimpleDdgiNearFieldResidualProfile.ForPreset(
                        settings.QualityPreset,
                        scale),
                    settings);
            if (SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                    width,
                    height,
                    profile,
                    ExperimentBudgetBytes).IsValid)
            {
                return profile;
            }
        }

        return ApplyAdvancedOverrides(
            SimpleDdgiNearFieldResidualProfile.ForPreset(
                settings.QualityPreset,
                0.125f),
            settings);
    }

    private static SimpleDdgiNearFieldResidualProfile ApplyAdvancedOverrides(
        in SimpleDdgiNearFieldResidualProfile profile,
        in NearFieldResidualSettings settings)
    {
        if (!settings.AdvancedOverridesEnabled)
            return profile;
        return profile with
        {
            MaximumTraceDistanceMeters = settings.MaximumTraceDistanceMeters,
            FullWeightTraceDistanceMeters = MathF.Min(
                profile.FullWeightTraceDistanceMeters,
                settings.MaximumTraceDistanceMeters * 0.5f),
            MaximumRaysPerPixel = settings.RaysPerPixel,
            FilterIterationCount = settings.FilterIterationCount
        };
    }

    private static SimpleDdgiNearFieldResidualPrerequisites
        CreatePrerequisites(
            in AdvancedGiPrerequisiteGateResult gate,
            bool hasEvidence,
            in SimpleDdgiNearFieldResidualQualificationEvidence evidence) =>
        new(
            RefinementBricksActive: gate.Passed,
            RefinementQualityGatePassed: gate.Passed,
            RemainingContactScaleErrorMeasured: hasEvidence,
            SourceOwnershipImplemented: true,
            DisocclusionRejectionImplemented: true,
            CameraAndScreenEdgeStabilityPassed:
            evidence.TemporalStabilityVerified,
            ReferenceErrorPerMillisecondImproved:
            evidence.WholeFrameRegressionVerified,
            NoDoubleCountingOrFalseDarkening:
            evidence.SignedResidualEnergyVerified &&
            evidence.TraceSourceIndependenceVerified);

    private static SimpleDdgiNearFieldTraceSourceScaledExtent
        CreateScaledExtent(
            in SimpleDdgiNearFieldResidualLayout layout,
            float resolutionScale) => new(
        layout.SourceWidth,
        layout.SourceHeight,
        layout.TraceWidth,
        layout.TraceHeight,
        resolutionScale);

    private static bool IsGpuMode(SimpleDdgiNearFieldResidualMode mode) =>
        mode is
            SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment or
            SimpleDdgiNearFieldResidualMode.HiZAdaptive or
            SimpleDdgiNearFieldResidualMode.AutoQualified;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

internal readonly record struct NearFieldResidualSettings(
    SimpleDdgiNearFieldResidualQualityPreset QualityPreset,
    bool AdvancedOverridesEnabled,
    float MaximumTraceDistanceMeters,
    int RaysPerPixel,
    int FilterIterationCount,
    float ResidualIntensity);

internal readonly record struct NearFieldResidualInitializationRequest(
    SimpleDdgiNearFieldResidualMode RequestedMode,
    string? ConfiguredQualificationId,
    Extent2D SceneRenderExtent,
    NearFieldResidualSettings Settings,
    bool HasQualificationEvidence,
    SimpleDdgiNearFieldResidualQualificationEvidence QualificationEvidence,
    SimpleDdgiNearFieldResidualAdmissionContext QualificationAdmissionContext,
    bool CandidateAuthorized,
    AdvancedGiNearFieldCandidateDocument? Candidate,
    AdvancedGiCandidateAuthorization CandidateAuthorization,
    AdvancedGiPrerequisiteGateResult PrerequisiteGate,
    AdvancedGiQualificationManifest QualificationManifest,
    AdvancedGiRuntimeQualificationContext RuntimeQualificationContext,
    bool CausticEffective);

internal readonly record struct NearFieldResidualInitializationResult(
    GiExperimentModeState<SimpleDdgiNearFieldResidualMode> Mode,
    SimpleDdgiNearFieldResidualPlan Plan,
    ForwardNearFieldDirectSourcePipelineConfiguration PipelineConfiguration,
    SimpleDdgiNearFieldResidualProfile EffectiveProfile,
    SimpleDdgiNearFieldResidualAdmissionContext AdmissionContext,
    bool PublishAdmissionContext,
    bool UsesCandidateAuthorization,
    bool RuntimeSupported,
    string PreflightReason);

internal readonly record struct NearFieldResidualRuntimeAllocationRequest(
    BufferManager BufferManager,
    BindlessHeap BindlessHeap,
    RenderTargetManager RenderTargets,
    HiZDepthPyramid HiZDepthPyramid,
    FoliageManager FoliageManager);

internal readonly record struct NearFieldResidualRecreationPreparation(
    bool Changed,
    bool ReplacementPrepared,
    NearFieldResidualPublication Publication,
    string Reason)
{
    public static NearFieldResidualRecreationPreparation Unchanged =>
        new(false, false, NearFieldResidualPublication.Unchanged, string.Empty);
}

internal readonly record struct NearFieldResidualPublication(
    bool Changed,
    bool Executable,
    bool DisableFeature,
    bool ReleaseUnmanagedTargets,
    SimpleDdgiNearFieldResidualRenderTargetGeneration? Targets,
    SimpleDdgiNearFieldResidualVulkanRuntime? Runtime,
    ForwardNearFieldDirectSourcePipelineConfiguration PipelineConfiguration,
    int FilterIterationCount,
    string Reason)
{
    public static NearFieldResidualPublication Unchanged => default;

    public static NearFieldResidualPublication Publish(
        SimpleDdgiNearFieldResidualRenderTargetGeneration targets,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime,
        in ForwardNearFieldDirectSourcePipelineConfiguration pipeline) =>
        new(true, true, false, false, targets, runtime, pipeline, 0, "valid");

    public static NearFieldResidualPublication Suppress(
        in ForwardNearFieldDirectSourcePipelineConfiguration pipeline,
        string reason) =>
        new(true, false, false, false, null, null, pipeline, 0, reason);

    public static NearFieldResidualPublication Disable(
        string reason,
        int filterIterationCount,
        bool releaseUnmanagedTargets) =>
        new(
            true,
            false,
            true,
            releaseUnmanagedTargets,
            null,
            null,
            ForwardNearFieldDirectSourcePipelineConfiguration.Disabled,
            filterIterationCount,
            reason);
}

internal readonly record struct NearFieldResidualGraphResourceSnapshot(
    SimpleDdgiNearFieldResidualVulkanRuntime? Runtime,
    SimpleDdgiNearFieldResidualVulkanBuffers Buffers)
{
    public bool IsComplete => Runtime is not null && Buffers.IsComplete;
}

internal readonly record struct NearFieldResidualCoordinatorSnapshot(
    SimpleDdgiNearFieldResidualPlan Plan,
    GiExperimentModeState<SimpleDdgiNearFieldResidualMode> Mode,
    SimpleDdgiNearFieldResidualProfile RequestedProfile,
    SimpleDdgiNearFieldResidualProfile EffectiveProfile,
    SimpleDdgiNearFieldResidualExecutionScale StartupScale,
    SimpleDdgiNearFieldResidualGpuConfiguration GpuConfiguration,
    ForwardNearFieldDirectSourcePipelineConfiguration PipelineConfiguration,
    SimpleDdgiNearFieldResidualGpuRuntimeSnapshot Runtime,
    SimpleDdgiNearFieldResidualGenerationSnapshot Generations,
    bool UsesCandidateAuthorization,
    bool GenerationExecutable,
    string Reason);
