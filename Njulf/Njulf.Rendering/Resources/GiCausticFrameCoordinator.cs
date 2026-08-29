using System;
using System.Collections.Generic;
using System.Numerics;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns the C4 plan, runtime, producer, semantic revision, and publication
/// state above the leaf Vulkan implementation.
/// </summary>
internal sealed unsafe class GiCausticFrameCoordinator : IDisposable
{
    internal const ulong ExperimentBudgetBytes = 96UL * 1024UL * 1024UL;

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly StagingRing _stagingRing;
    private GiTaggedCausticCacheConfiguration _configuredConfiguration;
    private GiTaggedCausticCacheConfiguration _requestedConfiguration;
    private GiCausticVulkanRuntime? _runtime;
    private GiCausticTaggedTransportGpuProducer? _producer;
    private GiCausticCacheRevision _currentRevision;
    private ulong _producerRevisionFingerprint;
    private bool _runtimeConfigured;
    private bool _frameAvailable;
    private bool _usesCandidateAuthorization;
    private bool _hasConfiguredConfiguration;
    private bool _disposed;
    private string _frameReason = "caustic-disabled";

    public GiTaggedCausticCachePlan Plan { get; private set; }

    public GiExperimentModeState<GiCausticMode> Mode { get; private set; } =
        GiExperimentModeState<GiCausticMode>.Disabled(GiCausticMode.Off);

    public ForwardGiCausticReceiverPipelineConfiguration
        ReceiverPipelineConfiguration { get; private set; } =
        ForwardGiCausticReceiverPipelineConfiguration.Disabled;

    public bool FrameAvailable => _frameAvailable;

    public bool RuntimeConfigured => _runtimeConfigured;

    public bool RequiresHeroSnapshot =>
        !_disposed &&
        _runtime is not null &&
        Plan.Active &&
        Mode.EffectiveMode is GiCausticMode.WorldCacheExperiment or
            GiCausticMode.AutoQualified;

    public GiCausticHeroExtractionProfile HeroExtractionProfile =>
        GiCausticHeroExtractionProfile.Reference with
        {
            MaximumHeroCount = _requestedConfiguration.MaximumHeroCount
        };

    public GiCausticVulkanRuntimeDiagnostics Diagnostics =>
        _runtime?.Diagnostics ?? GiCausticVulkanRuntimeDiagnostics.Disabled;

    public GiCausticFrameCoordinator(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
                         throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ??
                       throw new ArgumentNullException(nameof(stagingRing));
    }

    public void ConfigureEvidence(
        in GiCausticQualificationEvidence evidence,
        in GiCausticAdmissionContext admissionContext,
        in GiTaggedCausticCacheConfiguration configuration)
    {
        ThrowIfDisposed();
        if (!admissionContext.TryValidate(out string contextFailure))
            throw new ArgumentException(contextFailure, nameof(admissionContext));
        if (!configuration.Enabled || configuration.MemoryBudgetBytes == 0UL ||
            configuration.MemoryBudgetBytes > ExperimentBudgetBytes)
        {
            throw new ArgumentException(
                "C4 requires an enabled profile within its independent 96 MiB budget.",
                nameof(configuration));
        }

        if (!configuration.ScreenResolveProfile.TryValidate(
                out string profileFailure))
        {
            throw new ArgumentException(profileFailure, nameof(configuration));
        }

        GiTaggedCausticCachePlan candidate =
            GiTaggedCausticCacheExperiment.CreatePlan(
                configuration,
                new GiTaggedCausticCacheQualification(
                    SeparateOwnershipImplemented: true,
                    DiffuseTransportFeedDisabled: true,
                    ReferenceParityPassed:
                    evidence.CpuGpuPdfAndThroughputParity,
                    StabilityProofPassed:
                    evidence.PublicationAndMotionStabilityPassed,
                    QualityPerMillisecondImproved:
                    evidence.QualityPerMillisecondImproved),
                evidence,
                admissionContext);
        if (!candidate.Active)
            throw new ArgumentException(candidate.Status, nameof(evidence));

        _configuredConfiguration = configuration;
        _hasConfiguredConfiguration = true;
    }

    public void ClearConfiguredEvidence()
    {
        ThrowIfDisposed();
        _configuredConfiguration = default;
        _hasConfiguredConfiguration = false;
    }

    public GiCausticInitializationResult Initialize(
        in GiCausticInitializationRequest request)
    {
        ThrowIfDisposed();
        GiCausticMode productionMode =
            AdvancedGiActivationPolicy.NormalizeProductionMode(
                request.RequestedMode);
        bool cpuReference =
            productionMode == GiCausticMode.PhotonReference;
        bool gpuRequested = productionMode ==
            GiCausticMode.WorldCacheExperiment;
        AdvancedGiCausticCandidateDocument? candidate =
            request.CandidateAuthorized ? request.Candidate : null;
        _usesCandidateAuthorization = candidate is not null;

        GiTaggedCausticCacheConfiguration configuration =
            request.HasQualificationEvidence && _hasConfiguredConfiguration
                ? _configuredConfiguration with { Enabled = gpuRequested }
                : candidate is not null
                    ? candidate.Configuration with { Enabled = gpuRequested }
                    : new GiTaggedCausticCacheConfiguration(
                        Enabled: gpuRequested,
                        HeroMaterialCount: gpuRequested ? 1 : 0,
                        PhotonTaskCapacity: 4_096,
                        MaximumWorldCells: 4_096,
                        MaximumPhotonsPerCell: 8,
                        MemoryBudgetBytes: ExperimentBudgetBytes,
                        ScreenResolveProfile:
                        new GiCausticScreenResolveProfile(
                            checked((int)request.SceneRenderExtent.Width),
                            checked((int)request.SceneRenderExtent.Height)));
        GiCausticAdmissionContext admissionContext =
            request.HasQualificationEvidence
                ? request.QualificationAdmissionContext
                : candidate?.AdmissionContext ?? default;

        GiTaggedCausticCachePlan plan;
        bool publishAdmissionContext;
        if (candidate is not null)
        {
            plan = GiTaggedCausticCacheExperiment.CreateCandidatePlan(
                configuration,
                admissionContext,
                request.CandidateAuthorization);
            publishAdmissionContext = true;
        }
        else if (productionMode ==
                 GiCausticMode.WorldCacheExperiment)
        {
            admissionContext = default;
            plan = GiTaggedCausticCacheExperiment.CreateExplicitPlan(
                configuration);
            publishAdmissionContext = true;
        }
        else
        {
            GiCausticQualificationEvidence evidence =
                request.HasQualificationEvidence
                    ? request.QualificationEvidence
                    : default;
            plan = GiTaggedCausticCacheExperiment.CreatePlan(
                configuration,
                new GiTaggedCausticCacheQualification(
                    SeparateOwnershipImplemented: true,
                    DiffuseTransportFeedDisabled: true,
                    ReferenceParityPassed:
                    evidence.CpuGpuPdfAndThroughputParity,
                    StabilityProofPassed:
                    evidence.PublicationAndMotionStabilityPassed,
                    QualityPerMillisecondImproved:
                    evidence.QualityPerMillisecondImproved),
                evidence,
                admissionContext);
            publishAdmissionContext = false;
        }

        string preflightFailure = plan.Status;
        bool runtimeSupported = gpuRequested &&
                                TryValidateRuntimePreflight(
                                    plan,
                                    request.SceneRenderExtent,
                                    out preflightFailure);
        AdvancedGiQualificationGateResult qualification =
            EvaluateQualification(request, runtimeSupported);
        GiExperimentModeState<GiCausticMode> mode =
            AdvancedGiAdmissionCoordinator.ResolveMode(
                request.RequestedMode,
                GiCausticMode.Off,
                supported: cpuReference || runtimeSupported,
                prerequisiteGate: request.PrerequisiteGate,
                qualificationGate: qualification,
                resourcesComplete: cpuReference || plan.Active,
                request.ConfiguredQualificationId,
                cpuReference
                    ? "caustic-CPU-reference-mode"
                    : plan.Active
                        ? "valid"
                        : string.IsNullOrWhiteSpace(preflightFailure)
                            ? plan.Status
                            : preflightFailure);

        ForwardGiCausticReceiverPipelineConfiguration receiver =
            mode.EffectiveMode is GiCausticMode.WorldCacheExperiment or
                GiCausticMode.AutoQualified
                ? new ForwardGiCausticReceiverPipelineConfiguration(
                    IsC4EffectivelyEnabled: true,
                    configuration.ScreenResolveProfile,
                    plan.EvidenceValidation.BindingFingerprint,
                    GiCausticGpuAbi.Version,
                    GiCausticScreenGpuAbi.Version,
                    ForwardGiCausticReceiverContract.ShaderSemanticVersion)
                : ForwardGiCausticReceiverPipelineConfiguration.Disabled;

        _requestedConfiguration = configuration;
        Plan = plan;
        Mode = mode;
        ReceiverPipelineConfiguration = receiver;
        _frameReason = mode.FallbackDetail;
        return new GiCausticInitializationResult(
            mode,
            plan,
            receiver,
            admissionContext,
            publishAdmissionContext,
            _usesCandidateAuthorization,
            runtimeSupported,
            preflightFailure);
    }

    public void CreateRuntime(
        AccelerationStructureManager accelerationStructureManager,
        Action waitForDescriptorReaders,
        RenderTargetManager renderTargets,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(accelerationStructureManager);
        ArgumentNullException.ThrowIfNull(waitForDescriptorReaders);
        ArgumentNullException.ThrowIfNull(renderTargets);
        if (Mode.EffectiveMode is not (
            GiCausticMode.WorldCacheExperiment or
            GiCausticMode.AutoQualified))
        {
            return;
        }

        _runtime ??= new GiCausticVulkanRuntime(
            _context,
            _bufferManager,
            accelerationStructureManager,
            waitForDescriptorReaders,
            renderTargets,
            pipelineCacheService);
    }

    public bool TryRegisterDescriptors(
        BindlessHeap bindlessHeap,
        BufferHandle safeFallbackBuffer,
        ulong safeFallbackBufferBytes,
        out string reason)
    {
        ThrowIfDisposed();
        if (_runtime is null)
        {
            reason = "caustic-runtime-unavailable";
            return false;
        }

        return _runtime.TryRegisterDescriptors(
            bindlessHeap,
            safeFallbackBuffer,
            safeFallbackBufferBytes,
            out reason);
    }

    public GiCausticFrameResult PrepareFrame(
        in GiCausticFrameRequest request)
    {
        ThrowIfDisposed();
        _frameAvailable = false;
        if (!request.GraphUsesCausticWorldCache ||
            _runtime is null ||
            !Plan.Active)
        {
            _frameReason = "caustic-runtime-plan-or-graph-unavailable";
            return CaptureFrameResult();
        }

        bool requiresContentMatch =
            AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                request.RequestedMode,
                _usesCandidateAuthorization);
        if (requiresContentMatch && !request.RuntimeContentState.Matched)
            return RejectFrame(request.RuntimeContentState.Reason);

        GiCausticHeroSourceSnapshot? heroSnapshot = request.HeroSnapshot;
        if (heroSnapshot is null || !heroSnapshot.HasEligibleHeroes)
            return RejectFrame(request.HeroSnapshotReason);

        GiCausticHeroRevisionIdentity heroIdentity =
            GiCausticTaskGenerationCompiler.ComputeHeroRevisionIdentity(
                heroSnapshot);
        ulong punctualRevision = request.LightSnapshot.ContentRevision;
        ulong emissiveRevision =
            request.EmissiveSnapshot.Content.SourceRevision;
        bool requiresQualifiedIdentity =
            request.RequestedMode == GiCausticMode.AutoQualified ||
            _usesCandidateAuthorization;
        if (!heroIdentity.IsValid ||
            requiresQualifiedIdentity &&
            (heroSnapshot.SceneContentRevision !=
             request.AdmissionContext.ContentRevision ||
             punctualRevision !=
             request.AdmissionContext.LightDistributionRevision ||
             emissiveRevision !=
             request.AdmissionContext.EmissiveDistributionRevision ||
             heroIdentity.AggregateSourceRevision !=
             request.AdmissionContext.HeroSourceRevision ||
             heroSnapshot.TopLevelInstanceSignature !=
             request.AdmissionContext.CurrentPoseTlasSignature))
        {
            return RejectFrame(
                "caustic-live-content-or-source-revision-does-not-match-qualified-evidence");
        }

        ulong combinedEmitterRevision =
            GiCausticTaskGenerationCompiler.ComputeEmitterDistributionRevision(
                punctualRevision,
                emissiveRevision);
        var revision = new GiCausticCacheRevision(
            GiCausticGpuAbi.Version,
            heroIdentity.MaterialRevision,
            combinedEmitterRevision,
            heroIdentity.GeometryRevision,
            heroIdentity.TransformRevision,
            heroSnapshot.SceneContentRevision,
            heroIdentity.StableIdentityRevision);
        if (!revision.IsValid)
            return RejectFrame("caustic-live-cache-revision-invalid");

        ulong revisionFingerprint =
            GiCausticGpuAbi.ComputeRevisionFingerprint(revision);
        if (_producer is null ||
            _producerRevisionFingerprint != revisionFingerprint)
        {
            if (!TryCreateProducer(
                    request.LightSnapshot,
                    request.EmissiveSnapshot,
                    heroSnapshot,
                    revision,
                    out GiCausticTaggedTransportGpuProducer? producer,
                    out string producerReason) ||
                producer is null)
            {
                return RejectFrame(producerReason);
            }

            _producer = producer;
            _producerRevisionFingerprint = revisionFingerprint;
        }

        GiCausticTaggedTransportGpuProducer currentProducer = _producer;
        if (!_runtimeConfigured)
        {
            var runtimeRequest = new GiCausticGpuRuntimeRequest(
                IsEffectivelyEnabled: true,
                Plan.GpuLayout,
                new GiCausticGpuFeatureSupport(
                    ComputeSupported: true,
                    RayQuerySupported: _context.RayQuerySupported,
                    CurrentPoseAccelerationStructuresAvailable: true,
                    TaggedTransportBackendIntegrated: true,
                    DeterministicParallelCacheBuildIntegrated: true,
                    PublicationReadbackSupported: true,
                    DedicatedBindlessSlotsAvailable: true,
                    ScreenResolvePipelineIntegrated: true,
                    ScreenResolveResourcesAvailable: true));
            GiCausticGpuPipelineQualification qualification =
                request.RequestedMode ==
                GiCausticMode.WorldCacheExperiment
                    ? GiCausticGpuPipelineQualification.IntegratedExplicit
                    : new GiCausticGpuPipelineQualification(
                        TaggedFirstDiffuseTraceQualified:
                        request.QualificationEvidence
                            .CpuGpuPdfAndThroughputParity &&
                        request.QualificationEvidence
                            .MirrorAndDielectricEnergyConservation &&
                        request.QualificationEvidence
                            .DifferentialReferencePassed,
                        DeterministicParallelCacheBuildQualified:
                        request.QualificationEvidence
                            .BottomKUnbiasednessPassed);
            if (!_runtime.TryConfigure(
                    runtimeRequest,
                    qualification,
                    currentProducer,
                    out string configureReason))
            {
                return RejectFrame(configureReason);
            }

            _runtimeConfigured = true;
        }

        _runtime.Invalidate(
            revision,
            "caustic-live-semantic-revision-changed");
        _currentRevision = revision;
        _ = _runtime.TryPrepareScreenFrame(
            request.FrameIndex,
            revision,
            out _);
        bool readable = _runtime.IsReadableForRevision(revision);
        if (!readable &&
            !_runtime.TryPrepareGraphFrame(
                request.FrameIndex,
                revision,
                new Vector4(
                    0.0f,
                    0.0f,
                    0.0f,
                    _requestedConfiguration.WorldCellSize),
                currentProducer,
                out string prepareReason))
        {
            return RejectFrame(prepareReason);
        }

        _frameAvailable = true;
        _frameReason = readable
            ? "caustic-readable-cache-reused"
            : "caustic-frame-prepared";
        return CaptureFrameResult();
    }

    public void CompleteFrameAfterFence(int frameIndex, Fence frameFence)
    {
        ThrowIfDisposed();
        if (_runtime is not null)
        {
            _ = _runtime.TryReadCompletedFrame(
                frameIndex,
                frameFence,
                out _);
        }
    }

    public GiCausticExtentTransition
        DisableForIncompatibleExtentAfterDeviceIdle(
            Extent2D nextSceneExtent,
            string reason = "caustic-resize-requires-new-bound-evidence")
    {
        ThrowIfDisposed();
        if (Mode.EffectiveMode is not (
            GiCausticMode.WorldCacheExperiment or
            GiCausticMode.AutoQualified))
        {
            return GiCausticExtentTransition.Unchanged;
        }

        GiCausticScreenResolveLayout screen = Plan.GpuLayout.ScreenResolve;
        if (screen.IsValid &&
            nextSceneExtent.Width == (uint)screen.Width &&
            nextSceneExtent.Height == (uint)screen.Height)
        {
            return GiCausticExtentTransition.Unchanged;
        }

        string detail = string.IsNullOrWhiteSpace(reason)
            ? "caustic-resize-requires-new-bound-evidence"
            : reason.Trim();
        _frameAvailable = false;
        _runtimeConfigured = false;
        _producer = null;
        _producerRevisionFingerprint = 0UL;
        _currentRevision = default;
        _runtime?.DisableAndReleaseAfterDeviceIdle(detail);
        Plan = Plan with
        {
            Active = false,
            AllocatedBytes = 0UL,
            GpuLayout = GiCausticGpuResourceLayout.Empty(detail),
            Memory = SimpleDdgiAdvancedExperimentMemoryPlan
                .CreateCausticRejected(
                    GiExperimentFallbackReason.EvidenceBindingMismatch),
            Status = detail,
            Admission = new GiExperimentAdmission(
                "C4",
                Requested: true,
                CapabilitySupported: true,
                Active: false,
                Stage: GiExperimentStage.QualificationFailed,
                AllocatedBytes: 0UL,
                Status: detail)
        };
        Mode = Mode with
        {
            AdmittedMode = GiCausticMode.Off,
            EffectiveMode = GiCausticMode.Off,
            FallbackReason =
            GiExperimentFallbackReason.EvidenceBindingMismatch,
            FallbackDetail = detail
        };
        ReceiverPipelineConfiguration =
            ForwardGiCausticReceiverPipelineConfiguration.Disabled;
        _frameReason = detail;
        return new GiCausticExtentTransition(true, Mode, detail);
    }

    public GiCausticGraphResourceSnapshot CaptureGraphResources()
    {
        if (_disposed || _runtime is null)
            return default;
        return new GiCausticGraphResourceSnapshot(
            _runtime,
            _runtime.Buffers,
            _runtime.GetFrameConstantBuffer(0),
            _runtime.GetFrameConstantBuffer(1));
    }

    public GiCausticCoordinatorSnapshot CaptureSnapshot() => new(
        Plan,
        Mode,
        ReceiverPipelineConfiguration,
        Diagnostics,
        _requestedConfiguration,
        _currentRevision,
        _producerRevisionFingerprint,
        _runtimeConfigured,
        _frameAvailable,
        _usesCandidateAuthorization,
        _frameReason);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runtime?.Dispose();
        _runtime = null;
        _producer = null;
    }

    private AdvancedGiQualificationGateResult EvaluateQualification(
        in GiCausticInitializationRequest request,
        bool runtimeSupported)
    {
        if (!request.PrerequisiteGate.Passed)
        {
            return AdvancedGiQualificationGateResult.Reject(
                request.PrerequisiteGate.FailureDetail);
        }

        return request.QualificationManifest.Evaluate(
            AdvancedGiPrerequisiteFeature.TaggedCaustics,
            request.RuntimeQualificationContext with
            {
                FeatureSupported = runtimeSupported
            },
            request.PrerequisiteGate.QualificationId,
            request.ConfiguredQualificationId);
    }

    private bool TryValidateRuntimePreflight(
        in GiTaggedCausticCachePlan plan,
        Extent2D sceneRenderExtent,
        out string failure)
    {
        if (!plan.Active || !plan.GpuLayout.IsValid)
        {
            failure = plan.Status;
            return false;
        }

        GiCausticScreenResolveLayout screen = plan.GpuLayout.ScreenResolve;
        if (!screen.IsValid ||
            screen.Width != checked((int)sceneRenderExtent.Width) ||
            screen.Height != checked((int)sceneRenderExtent.Height))
        {
            failure = "caustic-screen-profile-does-not-match-scene-extent";
            return false;
        }

        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(
            _context.PhysicalDevice,
            &properties);
        ulong maximumStorageRange = properties.Limits.MaxStorageBufferRange;
        if (sceneRenderExtent.Width > properties.Limits.MaxImageDimension2D ||
            sceneRenderExtent.Height > properties.Limits.MaxImageDimension2D ||
            properties.Limits.MaxColorAttachments <
            ForwardGiCausticReceiverContract.ColorAttachmentCount ||
            properties.Limits.MaxPushConstantsSize <
            GiCausticScreenGpuAbi.PushConstantsBytes ||
            plan.GpuLayout.TaskQueueBytes > maximumStorageRange ||
            checked(plan.GpuLayout.CandidateStagingBytes +
                    plan.GpuLayout.PublishedPhotonBytes) >
            maximumStorageRange ||
            plan.GpuLayout.CacheBytes > maximumStorageRange ||
            plan.GpuLayout.ScratchBytes > maximumStorageRange)
        {
            failure = "caustic-device-image-MRT-push-or-storage-range-limit";
            return false;
        }

        if (!_context.RayQuerySupported ||
            _context.KhrAccelerationStructure is null ||
            !HasFormatFeatures(
                GiCausticScreenGpuAbi.ReceiverPayloadFormat,
                FormatFeatureFlags.ColorAttachmentBit |
                FormatFeatureFlags.SampledImageBit) ||
            !HasFormatFeatures(
                GiCausticScreenGpuAbi.RadianceFormat,
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit) ||
            !HasFormatFeatures(
                GiCausticScreenGpuAbi.MomentsFormat,
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit) ||
            !HasFormatFeatures(
                RenderTargetManager.SceneColorFormat,
                FormatFeatureFlags.ColorAttachmentBit |
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit))
        {
            failure =
                "caustic-ray-query-or-screen-format-capability-unavailable";
            return false;
        }

        failure = "valid";
        return true;
    }

    private bool TryCreateProducer(
        in LightFrameSnapshot lightSnapshot,
        in DdgiEmissiveTransportSnapshot emissiveSnapshot,
        GiCausticHeroSourceSnapshot heroSnapshot,
        in GiCausticCacheRevision revision,
        out GiCausticTaggedTransportGpuProducer? producer,
        out string reason)
    {
        producer = null;
        GiCausticEmitterSource[] punctualSources =
            Array.Empty<GiCausticEmitterSource>();
        if (lightSnapshot.Count > 0 &&
            !GiCausticTaskGenerationCompiler.TryCreatePunctualSources(
                lightSnapshot,
                _requestedConfiguration.DirectionalEmissionDiskRadius,
                _requestedConfiguration.TargetingMixtureProbability,
                out punctualSources,
                out reason))
        {
            return false;
        }

        GiCausticEmitterSource[] emissiveSources =
            Array.Empty<GiCausticEmitterSource>();
        if (emissiveSnapshot.Content.SourceCount > 0 &&
            !GiCausticTaskGenerationCompiler.TryCreateEmissiveTriangleSources(
                emissiveSnapshot.Sources.Span,
                emissiveSnapshot.Content.SourceRevision,
                _requestedConfiguration.TargetingMixtureProbability,
                out emissiveSources,
                out reason))
        {
            return false;
        }

        int emitterCount = checked(
            punctualSources.Length + emissiveSources.Length);
        if (emitterCount <= 0 ||
            emitterCount > _requestedConfiguration.MaximumEmitterCount)
        {
            reason = emitterCount <= 0
                ? "caustic-frame-has-no-exact-eligible-emitter"
                : "caustic-frame-emitter-capacity-exceeded";
            return false;
        }

        var emitters = new GiCausticEmitterSource[emitterCount];
        punctualSources.CopyTo(emitters, 0);
        emissiveSources.CopyTo(emitters, punctualSources.Length);
        var emitterIds = new HashSet<uint>();
        for (int index = 0; index < emitters.Length; index++)
        {
            if (!emitterIds.Add(emitters[index].StableSourceId))
            {
                reason =
                    "caustic-punctual-and-emissive-stable-ID-collision";
                return false;
            }
        }

        if (!GiCausticTaskGenerationCompiler.TryCompile(
                emitters,
                heroSnapshot.Heroes.Span,
                _requestedConfiguration.PhotonTaskCapacity,
                revision,
                out GiCausticTaskGenerationBatch? batch,
                out reason) ||
            batch is null)
        {
            return false;
        }

        if (batch.ProposalPairs.Length >
            _requestedConfiguration.MaximumProposalPairCount)
        {
            reason = "caustic-frame-proposal-pair-capacity-exceeded";
            return false;
        }

        try
        {
            producer = new GiCausticTaggedTransportGpuProducer(
                _context,
                _bufferManager,
                _stagingRing,
                Plan.GpuLayout,
                batch);
            reason = "valid";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                              InvalidOperationException or OverflowException)
        {
            reason = "caustic-tagged-transport-producer-creation-failed:" +
                     exception.GetType().Name;
            return false;
        }
    }

    private GiCausticFrameResult RejectFrame(string? reason)
    {
        _frameAvailable = false;
        _currentRevision = default;
        _frameReason = string.IsNullOrWhiteSpace(reason)
            ? "caustic-frame-input-rejected"
            : reason.Trim();
        _runtime?.Invalidate(default, _frameReason);
        return CaptureFrameResult();
    }

    private GiCausticFrameResult CaptureFrameResult() => new(
        _frameAvailable,
        _currentRevision,
        ReceiverPipelineConfiguration,
        _frameReason);

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

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

internal readonly record struct GiCausticInitializationRequest(
    GiCausticMode RequestedMode,
    string? ConfiguredQualificationId,
    Extent2D SceneRenderExtent,
    bool HasQualificationEvidence,
    GiCausticQualificationEvidence QualificationEvidence,
    GiCausticAdmissionContext QualificationAdmissionContext,
    bool CandidateAuthorized,
    AdvancedGiCausticCandidateDocument? Candidate,
    AdvancedGiCandidateAuthorization CandidateAuthorization,
    AdvancedGiPrerequisiteGateResult PrerequisiteGate,
    AdvancedGiQualificationManifest QualificationManifest,
    AdvancedGiRuntimeQualificationContext RuntimeQualificationContext,
    bool HybridReflectionsEnabled);

internal readonly record struct GiCausticInitializationResult(
    GiExperimentModeState<GiCausticMode> Mode,
    GiTaggedCausticCachePlan Plan,
    ForwardGiCausticReceiverPipelineConfiguration ReceiverConfiguration,
    GiCausticAdmissionContext AdmissionContext,
    bool PublishAdmissionContext,
    bool UsesCandidateAuthorization,
    bool RuntimeSupported,
    string PreflightReason);

internal readonly record struct GiCausticFrameRequest(
    bool GraphUsesCausticWorldCache,
    GiCausticMode RequestedMode,
    int FrameIndex,
    AdvancedGiRuntimeContentState RuntimeContentState,
    GiCausticAdmissionContext AdmissionContext,
    GiCausticQualificationEvidence QualificationEvidence,
    LightFrameSnapshot LightSnapshot,
    DdgiEmissiveTransportSnapshot EmissiveSnapshot,
    GiCausticHeroSourceSnapshot? HeroSnapshot,
    string HeroSnapshotReason);

internal readonly record struct GiCausticFrameResult(
    bool FrameAvailable,
    GiCausticCacheRevision Revision,
    ForwardGiCausticReceiverPipelineConfiguration ReceiverConfiguration,
    string Reason);

internal readonly record struct GiCausticGraphResourceSnapshot(
    GiCausticVulkanRuntime? Runtime,
    GiCausticVulkanBuffers Buffers,
    BufferHandle FrameConstants0,
    BufferHandle FrameConstants1)
{
    public bool IsComplete => Runtime is not null && Buffers.IsComplete &&
                              FrameConstants0.IsValid && FrameConstants1.IsValid;

    public BufferHandle GetFrameConstants(int frameIndex) => frameIndex switch
    {
        0 => FrameConstants0,
        1 => FrameConstants1,
        _ => throw new ArgumentOutOfRangeException(nameof(frameIndex))
    };
}

internal readonly record struct GiCausticCoordinatorSnapshot(
    GiTaggedCausticCachePlan Plan,
    GiExperimentModeState<GiCausticMode> Mode,
    ForwardGiCausticReceiverPipelineConfiguration ReceiverConfiguration,
    GiCausticVulkanRuntimeDiagnostics Runtime,
    GiTaggedCausticCacheConfiguration RequestedConfiguration,
    GiCausticCacheRevision CurrentRevision,
    ulong ProducerRevisionFingerprint,
    bool RuntimeConfigured,
    bool FrameAvailable,
    bool UsesCandidateAuthorization,
    string FrameReason);

internal readonly record struct GiCausticExtentTransition(
    bool Changed,
    GiExperimentModeState<GiCausticMode> Mode,
    string Reason)
{
    public static GiCausticExtentTransition Unchanged { get; } =
        new(false, default, string.Empty);
}
