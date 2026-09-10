using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Graphics;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Shaders;
using static Njulf.Rendering.RenderingConstants;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Buffer = Silk.NET.Vulkan.Buffer;
using ICamera = Njulf.Core.Interfaces.ICamera;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Njulf.Rendering.Pipeline;

/// <summary>Owns production pipeline assembly and unregistered passes; registered passes belong to RenderGraph.</summary>
internal sealed class ProductionPipelineOwner
{
    private ProductionPipelineDependencies _dependencies = null!;
    private readonly ProductionPassOwnership _passOwnership = new();
    private StagedDisposalPlan? _reflectionCleanup;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _initializationFailure;
    private bool _initializationStarted, _cleanupStarted;
    internal bool IsReady { get; private set; }
    internal bool HybridReflectionReceiversPrepared => Volatile.Read(ref _hybridReflectionReceiverPipelinesPrepared) != 0;

    // These operations run only after the renderer's existing completion/idle boundary.
    internal bool RequiresDiagnosticRecreation => _dependencies != null &&
        ((_meshPipeline != null && _meshPipeline.GpuMeshletCountersEnabled != _dependencies.Settings.Diagnostics.GpuMeshletCountersEnabled) ||
         (_foliagePipeline is { IsPrepared: true } && _foliagePipeline.GpuMeshletCountersEnabled != _dependencies.Settings.Diagnostics.GpuMeshletCountersEnabled));

    internal void RecreateDiagnosticVariants()
    {
        bool enabled = _dependencies.Settings.Diagnostics.GpuMeshletCountersEnabled;
        if (_meshPipeline != null && _meshPipeline.GpuMeshletCountersEnabled != enabled)
            _meshPipeline.Recreate(RenderTargetManager.SceneColorFormat, _dependencies.Swapchain.DepthFormat);
        if (_foliagePipeline is { IsPrepared: true } && _foliagePipeline.GpuMeshletCountersEnabled != enabled)
            _foliagePipeline.Recreate(RenderTargetManager.SceneColorFormat, RenderTargetManager.MotionVectorFormat, _dependencies.Swapchain.DepthFormat);
    }

    internal void RecreateForAttachmentProfile()
    {
        _meshPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _dependencies.Swapchain.DepthFormat);
        _foliagePipeline?.Recreate(RenderTargetManager.SceneColorFormat, RenderTargetManager.MotionVectorFormat, _dependencies.Swapchain.DepthFormat);
    }

    internal void RecreateForSwapchain()
    {
        RecreateForAttachmentProfile();
        _compositePipeline?.Recreate(_dependencies.Swapchain.SurfaceFormat);
        _ldrCompositePipeline?.Recreate(RenderTargetManager.LdrSceneColorFormat);
        _weightedOitCompositePipeline?.Recreate(RenderTargetManager.SceneColorFormat);
        _skyboxPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _dependencies.Swapchain.DepthFormat);
    }

    internal void CleanupGraph(RenderGraph graph)
    {
        _cleanupStarted = true;
        IsReady = false;
        _passOwnership.CleanupUnregistered();
        graph.Cleanup();
    }

    // Called only when assembling the renderer's cold-path staged disposal plan.
    // Keeping each named stage separate preserves retry and cross-owner dependencies.
    internal Action GetDisposalAction(string name) => name switch
    {
        "reflection-probe-passes" => CleanupReflectionPasses,
        "mesh-pipeline" => () => _meshPipeline?.Dispose(),
        "compute-pipeline" => () => _computePipeline?.Dispose(),
        "skinning-pass" => () => _skinningPass?.Dispose(),
        "ddgi-foliage-proxy-generation-pass" => () => _ddgiFoliageProxyGenerationPass?.Dispose(),
        "gpu-particle-reset-pass" => () => _gpuParticleResetPass?.Dispose(),
        "gpu-particle-simulate-pass" => () => _gpuParticleSimulatePass?.Dispose(),
        "gpu-particle-sort-pass" => () => _gpuParticleSortPass?.Dispose(),
        "foliage-cull-pass" => () => _foliageCullPass?.Dispose(),
        "foliage-pipeline" => () => _foliagePipeline?.Dispose(),
        "composite-pipeline" => () => _compositePipeline?.Dispose(),
        "ldr-composite-pipeline" => () => _ldrCompositePipeline?.Dispose(),
        "weighted-oit-composite-pipeline" => () => _weightedOitCompositePipeline?.Dispose(),
        "skybox-pipeline" => () => _skyboxPipeline?.Dispose(),
        "particle-pipeline" => () => _particlePipeline?.Dispose(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown production pipeline disposal stage.")
    };

    private void CleanupReflectionPasses()
    {
        _reflectionCleanup ??= new StagedDisposalPlan([
            new("reflection-publish", () => { _reflectionProbePublishPass?.Dispose(); _reflectionProbePublishPass = null; }),
            new("reflection-prefilter", () => { _reflectionProbePrefilterPass?.Dispose(); _reflectionProbePrefilterPass = null; }),
            new("reflection-capture", () => { _reflectionProbeCapturePass?.Dispose(); _reflectionProbeCapturePass = null; })]);
        if (_reflectionCleanup.TryDrain() is { } failure) throw failure;
    }
    private ForwardPlusPass? _forwardPlusPass;
    private ReflectionProbeCapturePass? _reflectionProbeCapturePass;
    private ReflectionProbePrefilterPass? _reflectionProbePrefilterPass;
    private ReflectionProbePublishPass? _reflectionProbePublishPass;
    private readonly ReflectionProbeCompletionValueProvider _reflectionProbeCompletionValues = new();
    private int _hybridReflectionReceiverPipelinesPrepared;
    private int _hybridReflectionReceiverPerformancePipelinesPrepared;

    // Pipelines
    private MeshPipeline _meshPipeline = null!;
    private ComputePipeline _computePipeline = null!;
    private CompositePipeline _compositePipeline = null!;
    private CompositePipeline _ldrCompositePipeline = null!;
    private WeightedOitCompositePipeline _weightedOitCompositePipeline = null!;
    private SkyboxPipeline _skyboxPipeline = null!;
    private ParticlePipeline _particlePipeline = null!;
    private FogPass _fogPass = null!;
    private SimpleDdgiTracePass? _simpleDdgiTracePass;
    private SimpleDdgiSchedulePass? _simpleDdgiSchedulePass;
    private SimpleDdgiPageDemandPass? _simpleDdgiPageDemandPass;
    private SimpleDdgiPageResidencyPass? _simpleDdgiPageResidencyPass;
    private SimpleDdgiPageFeedbackPass? _simpleDdgiPageFeedbackPass;
    private SimpleDdgiRelocateClassifyPass? _simpleDdgiRelocateClassifyPass;

    private SimpleDdgiDirectionalRadiancePass?
        _simpleDdgiDirectionalRadiancePass;

    private SimpleDdgiAcceleratedSolvePass? _simpleDdgiAcceleratedSolvePass;
    private SimpleDdgiTransportPass? _simpleDdgiTransportPass;
    private SimpleDdgiBlendPass? _simpleDdgiBlendPass;
    private SimpleDdgiPublishPass? _simpleDdgiPublishPass;
    private SimpleDdgiTransportAuditPass? _simpleDdgiTransportAuditPass;
    private SimpleDdgiSchedulerCommitPass? _simpleDdgiSchedulerCommitPass;
    private SkinningPass _skinningPass = null!;

    private DdgiFoliageProxyGenerationPass?
        _ddgiFoliageProxyGenerationPass;

    private GpuParticleResetPass _gpuParticleResetPass = null!;
    private GpuParticleSimulatePass _gpuParticleSimulatePass = null!;
    private GpuParticleSortPass _gpuParticleSortPass = null!;
    private GpuParticleResetGraphPass _gpuParticleResetGraphPass = null!;
    private GpuParticleSimulateGraphPass _gpuParticleSimulateGraphPass = null!;
    private GpuParticleSortGraphPass _gpuParticleSortGraphPass = null!;
    private FoliagePipeline _foliagePipeline = null!;
    private FoliageCullPass _foliageCullPass = null!;
    private SceneOpaqueCompactionPass _sceneOpaqueCompactionPass = null!;
    private ForwardVisibilityCompactionPass _forwardVisibilityCompactionPass = null!;
    private DirectionalShadowPass? _directionalShadowPass;
    private DirectionalRayShadowPass? _directionalRayShadowPass;
    private AreaRayShadowPass? _areaRayShadowPass;
    internal ForwardPlusPass? ForwardPlusPass => _forwardPlusPass;
    internal ReflectionProbeCapturePass? ReflectionProbeCapturePass => _reflectionProbeCapturePass;
    internal ReflectionProbePrefilterPass? ReflectionProbePrefilterPass => _reflectionProbePrefilterPass;
    internal ReflectionProbePublishPass? ReflectionProbePublishPass => _reflectionProbePublishPass;
    internal ReflectionProbeCompletionValueProvider ReflectionProbeCompletionValues => _reflectionProbeCompletionValues;
    internal MeshPipeline MeshPipeline => _meshPipeline;
    internal ComputePipeline ComputePipeline => _computePipeline;
    internal CompositePipeline CompositePipeline => _compositePipeline;
    internal CompositePipeline LdrCompositePipeline => _ldrCompositePipeline;
    internal WeightedOitCompositePipeline WeightedOitCompositePipeline => _weightedOitCompositePipeline;
    internal SkyboxPipeline SkyboxPipeline => _skyboxPipeline;
    internal ParticlePipeline ParticlePipeline => _particlePipeline;
    internal FogPass FogPass => _fogPass;
    internal SimpleDdgiTracePass? SimpleDdgiTracePass => _simpleDdgiTracePass;
    internal SimpleDdgiSchedulePass? SimpleDdgiSchedulePass => _simpleDdgiSchedulePass;
    internal SimpleDdgiPageDemandPass? SimpleDdgiPageDemandPass => _simpleDdgiPageDemandPass;
    internal SimpleDdgiPageResidencyPass? SimpleDdgiPageResidencyPass => _simpleDdgiPageResidencyPass;
    internal SimpleDdgiPageFeedbackPass? SimpleDdgiPageFeedbackPass => _simpleDdgiPageFeedbackPass;
    internal SimpleDdgiRelocateClassifyPass? SimpleDdgiRelocateClassifyPass => _simpleDdgiRelocateClassifyPass;
    internal SimpleDdgiDirectionalRadiancePass? SimpleDdgiDirectionalRadiancePass => _simpleDdgiDirectionalRadiancePass;
    internal SimpleDdgiAcceleratedSolvePass? SimpleDdgiAcceleratedSolvePass => _simpleDdgiAcceleratedSolvePass;
    internal SimpleDdgiTransportPass? SimpleDdgiTransportPass => _simpleDdgiTransportPass;
    internal SimpleDdgiBlendPass? SimpleDdgiBlendPass => _simpleDdgiBlendPass;
    internal SimpleDdgiPublishPass? SimpleDdgiPublishPass => _simpleDdgiPublishPass;
    internal SimpleDdgiTransportAuditPass? SimpleDdgiTransportAuditPass => _simpleDdgiTransportAuditPass;
    internal SimpleDdgiSchedulerCommitPass? SimpleDdgiSchedulerCommitPass => _simpleDdgiSchedulerCommitPass;
    internal SkinningPass SkinningPass => _skinningPass;
    internal DdgiFoliageProxyGenerationPass? DdgiFoliageProxyGenerationPass => _ddgiFoliageProxyGenerationPass;
    internal GpuParticleResetPass GpuParticleResetPass => _gpuParticleResetPass;
    internal GpuParticleSimulatePass GpuParticleSimulatePass => _gpuParticleSimulatePass;
    internal GpuParticleSortPass GpuParticleSortPass => _gpuParticleSortPass;
    internal GpuParticleResetGraphPass GpuParticleResetGraphPass => _gpuParticleResetGraphPass;
    internal GpuParticleSimulateGraphPass GpuParticleSimulateGraphPass => _gpuParticleSimulateGraphPass;
    internal GpuParticleSortGraphPass GpuParticleSortGraphPass => _gpuParticleSortGraphPass;
    internal FoliagePipeline FoliagePipeline => _foliagePipeline;
    internal FoliageCullPass FoliageCullPass => _foliageCullPass;
    internal SceneOpaqueCompactionPass SceneOpaqueCompactionPass => _sceneOpaqueCompactionPass;
    internal ForwardVisibilityCompactionPass ForwardVisibilityCompactionPass => _forwardVisibilityCompactionPass;
    internal DirectionalShadowPass? DirectionalShadowPass => _directionalShadowPass;
    internal DirectionalRayShadowPass? DirectionalRayShadowPass => _directionalRayShadowPass;
    internal AreaRayShadowPass? AreaRayShadowPass => _areaRayShadowPass;

    internal sealed class ReflectionProbeCompletionValueProvider :
        IReflectionProbeCompletionValueProvider
    {
        private ulong _completionValue;

        public void SetFrameSerial(ulong frameSerial)
        {
            _completionValue = frameSerial > ulong.MaxValue -
                (ulong)FramesInFlight - 1UL
                    ? ulong.MaxValue
                    : frameSerial + (ulong)FramesInFlight + 1UL;
        }

        public ulong GetCompletionValue(int frameIndex) => _completionValue;
    }

    internal void Initialize(ProductionPipelineDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _dependencies ??= dependencies;
        InitializeStages(
            () => dependencies.Lifetime.RunStartupStep("VulkanRenderer.CreatePipelines", CreatePipelineObjects),
            () => dependencies.Lifetime.RunStartupStep("VulkanRenderer.InitializeRenderGraph", InitializeRenderGraph));
    }

    // The two real initialization stages also provide a CPU-only failure/publication seam.
    // A failed assembly remains owned for the renderer's worker-drain/device-idle shutdown.
    internal void InitializeStages(Action createPipelines, Action initializeGraph)
    {
        ObjectDisposedException.ThrowIf(_cleanupStarted, this);
        if (IsReady) return;
        _initializationFailure?.Throw();
        if (_initializationStarted) throw new InvalidOperationException("Production pipeline initialization is already in progress.");
        _initializationStarted = true;
        try
        {
            createPipelines();
            initializeGraph();
            IsReady = true;
        }
        catch (Exception failure)
        {
            _initializationFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
            throw;
        }
    }
    private void CreatePipelineObjects()
    {
        System.Diagnostics.Debug.WriteLine("Creating pipelines...");
        bool receiverFeedbackGraphicsPipelinesRequested =
            (_dependencies.SimpleDdgiReceiverFeedback ??
             throw new InvalidOperationException(
                 "The receiver-feedback coordinator must exist before pipeline creation."))
            .GraphicsPipelinesRequested;

        // Create mesh pipeline for depth prepass and forward pass
        _dependencies.Lifetime.RunStartupStep("Pipeline.Create.Mesh", () =>
        {
            _meshPipeline = new MeshPipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                RenderTargetManager.SceneColorFormat,
                _dependencies.Swapchain.DepthFormat,
                _dependencies.Settings,
                _dependencies.NearFieldResidual.PipelineConfiguration,
                _dependencies.GiCaustic.ReceiverPipelineConfiguration,
                ForwardHybridReflectionReceiverPipelineConfiguration
                    .Production,
                receiverFeedbackGraphicsPipelinesRequested,
                _dependencies.RaySceneDescriptorBank,
                _dependencies.GiPipelineCacheService,
                _dependencies.Lifetime.RunStartupStep);
        });
        _dependencies.Lifetime.RunStartupStep("Pipeline.Create.Foliage", () =>
        {
            _foliagePipeline = new FoliagePipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                RenderTargetManager.SceneColorFormat,
                RenderTargetManager.MotionVectorFormat,
                _dependencies.Swapchain.DepthFormat,
                _dependencies.Settings,
                receiverFeedbackGraphicsPipelinesRequested,
                _dependencies.NearFieldResidual.PipelineConfiguration,
                _dependencies.GiCaustic.ReceiverPipelineConfiguration,
                ForwardHybridReflectionReceiverPipelineConfiguration
                    .Production,
                _dependencies.GiPipelineCacheService,
                createPipelines: false);
        });

        // Create compute pipeline for light culling
        _dependencies.Lifetime.RunStartupStep(
            "Pipeline.Create.LightCulling",
            () => _computePipeline = new ComputePipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                _dependencies.GiPipelineCacheService));

        _dependencies.Lifetime.RunStartupStep("Pipeline.Create.Composite", () =>
        {
            _compositePipeline = new CompositePipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                _dependencies.Swapchain.SurfaceFormat,
                _dependencies.GiPipelineCacheService);
            _ldrCompositePipeline = new CompositePipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                RenderTargetManager.LdrSceneColorFormat,
                _dependencies.GiPipelineCacheService);
            _weightedOitCompositePipeline = new WeightedOitCompositePipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                RenderTargetManager.SceneColorFormat,
                _dependencies.GiPipelineCacheService,
                createPipeline:
                    RendererBuildConfiguration.PipelineStartupMode ==
                        RendererPipelineStartupMode.Exhaustive ||
                    _dependencies.Settings.Transparency.Enabled &&
                    _dependencies.Settings.Transparency.Mode ==
                        TransparencyMode.WeightedBlendedOit);
        });
        _dependencies.Lifetime.RunStartupStep("Pipeline.Create.Skybox", () =>
        {
            _skyboxPipeline = new SkyboxPipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                RenderTargetManager.SceneColorFormat,
                _dependencies.Swapchain.DepthFormat,
                _dependencies.GiPipelineCacheService,
                createPipeline:
                    RendererBuildConfiguration.PipelineStartupMode ==
                        RendererPipelineStartupMode.Exhaustive ||
                    _dependencies.Settings.Environment.Enabled);
        });
        _dependencies.Lifetime.RunStartupStep("Pipeline.Create.Particle", () =>
        {
            _particlePipeline = new ParticlePipeline(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                RenderTargetManager.SceneColorFormat,
                _dependencies.Swapchain.DepthFormat,
                receiverFeedbackGraphicsPipelinesRequested,
                _dependencies.GiPipelineCacheService,
                createPipelines: false);
        });
        _skinningPass = new SkinningPass(
            _dependencies.Context,
            _dependencies.BindlessHeap,
            _dependencies.BufferManager,
            _dependencies.SkinningManager,
            _dependencies.GiPipelineCacheService);
        _ddgiFoliageProxyGenerationPass =
            new DdgiFoliageProxyGenerationPass(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                _dependencies.BufferManager,
                _dependencies.GiPipelineCacheService);
        _gpuParticleResetPass =
            new GpuParticleResetPass(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                _dependencies.BufferManager,
                _dependencies.GpuParticleRuntimeManager,
                _dependencies.GiPipelineCacheService);
        _gpuParticleSimulatePass =
            new GpuParticleSimulatePass(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                _dependencies.BufferManager,
                _dependencies.GpuParticleRuntimeManager,
                _dependencies.GiPipelineCacheService);
        _gpuParticleSortPass =
            new GpuParticleSortPass(
                _dependencies.Context,
                _dependencies.BindlessHeap,
                _dependencies.BufferManager,
                _dependencies.GpuParticleRuntimeManager,
                _dependencies.GiPipelineCacheService);
        _gpuParticleResetGraphPass =
            _passOwnership.Track(new GpuParticleResetGraphPass(_dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, _gpuParticleResetPass));
        _gpuParticleSimulateGraphPass =
            _passOwnership.Track(new GpuParticleSimulateGraphPass(_dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, _gpuParticleSimulatePass));
        _gpuParticleSortGraphPass = _passOwnership.Track(new GpuParticleSortGraphPass(_dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            _gpuParticleSortPass, _dependencies.GpuParticleRuntimeManager));
        _foliageCullPass =
            new FoliageCullPass(_dependencies.Context, _dependencies.BindlessHeap, _dependencies.BufferManager, _dependencies.FoliageManager, _foliagePipeline);
        _sceneOpaqueCompactionPass =
            _passOwnership.Track(new SceneOpaqueCompactionPass(
                _dependencies.Context,
                _dependencies.Swapchain,
                _dependencies.BindlessHeap,
                _meshPipeline,
                _dependencies.BufferManager,
                _dependencies.Deleter,
                _dependencies.Sync,
                _dependencies.Settings.IsPerformanceOptimizationEnabled(
                    PerformanceOptimizationFeature
                        .AsymmetricSidedDrawStreams)));
        _forwardVisibilityCompactionPass = _passOwnership.Track(new ForwardVisibilityCompactionPass(_dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            _meshPipeline, _dependencies.BufferManager));

        System.Diagnostics.Debug.WriteLine("Pipelines created.");
    }
    private void InitializeRenderGraph()
    {
        System.Diagnostics.Debug.WriteLine("Initializing render graph...");

        // The selected state is computed only from effective modes and is
        // frozen for this renderer lifetime.  A mode transition therefore
        // cannot leave a graph declaration, resource descriptor, or pass
        // instance from a previous generation alive.
        ProductionRenderPipelineDeclaration.Instance.DeclarePassResources(
            _dependencies.RenderGraph,
            _dependencies.AdvancedGiAdmission.GraphModes,
            _dependencies.Settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled);

        var passInstances = new Dictionary<string, RenderPassBase>(StringComparer.Ordinal);

        void AddPassInstance(RenderPassBase pass)
        {
            passInstances.Add(pass.Name, pass);
        }

        AddPassInstance(_sceneOpaqueCompactionPass);

        var directionalShadowPass = _passOwnership.Track(new DirectionalShadowPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _meshPipeline,
            _dependencies.DirectionalShadowResources!,
            _dependencies.Settings.Shadows,
            _foliagePipeline,
            _dependencies.BufferManager,
            _dependencies.FoliageManager));
        _directionalShadowPass = directionalShadowPass;
        _sceneOpaqueCompactionPass.SetDirectionalStaticShadowRefreshQuery(directionalShadowPass
            .GetStaticCacheRefreshMask);
        AddPassInstance(directionalShadowPass);

        var spotShadowPass = _passOwnership.Track(new SpotShadowPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _meshPipeline,
            _dependencies.SpotShadowAtlas!,
            _dependencies.Settings.Shadows,
            _foliagePipeline,
            _dependencies.FoliageManager));
        AddPassInstance(spotShadowPass);

        var pointShadowPass = _passOwnership.Track(new PointShadowPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _meshPipeline,
            _dependencies.PointShadowCubemapArray!,
            _dependencies.Settings.Shadows,
            _foliagePipeline,
            _dependencies.FoliageManager));
        AddPassInstance(pointShadowPass);

        // Create depth pre-pass
        var depthPrePass = _passOwnership.Track(new DepthPrePass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, _meshPipeline, _dependencies.RenderTargets!, _foliagePipeline, _dependencies.BufferManager,
            _dependencies.FoliageManager));
        AddPassInstance(depthPrePass);

        var motionVectorPass = _passOwnership.Track(new MotionVectorPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _meshPipeline,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _foliagePipeline,
            _dependencies.BufferManager,
            _dependencies.FoliageManager,
            _dependencies.ResolveSurfaceHistoryConsumers,
            _dependencies.TemporalSurfaceValidityResources));
        motionVectorPass.DirectionalHistoryResources = _dependencies.DirectionalShadowHistoryResources;
        depthPrePass.MotionVectorProducer = motionVectorPass;
        AddPassInstance(motionVectorPass);

        var hizBuildPass = _passOwnership.Track(new HiZBuildPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.HizDepthPyramid!,
            _dependencies.RenderTargets!,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(hizBuildPass);

        var directionalRayShadowPass = _passOwnership.Track(new DirectionalRayShadowPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings.Shadows,
            _dependencies.BufferManager,
            _dependencies.AccelerationStructureManager!,
            _dependencies.RaySceneDescriptorBank!,
            _dependencies.DirectionalShadowHistoryResources!,
            _dependencies.GiPipelineCacheService));
        _directionalRayShadowPass = directionalRayShadowPass;
        AddPassInstance(directionalRayShadowPass);

        var areaRayShadowPass = _passOwnership.Track(new AreaRayShadowPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings.Shadows,
            _dependencies.BufferManager,
            _dependencies.AccelerationStructureManager!,
            _dependencies.RaySceneDescriptorBank!,
            _dependencies.GiPipelineCacheService));
        _areaRayShadowPass = areaRayShadowPass;
        AddPassInstance(areaRayShadowPass);

        AddPassInstance(_passOwnership.Track(new DirectionalShadowTemporalPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.DirectionalShadowHistoryResources!,
            _dependencies.TemporalSurfaceValidityResources!,
            _dependencies.Settings.Shadows,
            _dependencies.BufferManager,
            _dependencies.GiPipelineCacheService)));
        AddPassInstance(_passOwnership.Track(new DirectionalShadowSpatialPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.DirectionalShadowHistoryResources!,
            directionalRayShadowPass,
            _dependencies.Settings.Shadows,
            _dependencies.BufferManager,
            _dependencies.GiPipelineCacheService)));

        AddPassInstance(_forwardVisibilityCompactionPass);

        var ambientOcclusionPass = _passOwnership.Track(new AmbientOcclusionPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _dependencies.GtaoRuntimeSupported,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(ambientOcclusionPass);

        var ambientOcclusionBlurPass = _passOwnership.Track(new AmbientOcclusionBlurPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(ambientOcclusionBlurPass);

        var gtaoHistoryState = new GtaoHistoryState();
        AddPassInstance(_passOwnership.Track(new GtaoPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.HizDepthPyramid!,
            _dependencies.Settings,
            _dependencies.GiPipelineCacheService)));
        AddPassInstance(_passOwnership.Track(new GtaoTemporalPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            gtaoHistoryState,
            _dependencies.GiPipelineCacheService)));
        AddPassInstance(_passOwnership.Track(new GtaoSpatialPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _dependencies.GiPipelineCacheService)));

        // Create tiled light culling pass
        var lightCullingPass = _passOwnership.Track(new TiledLightCullingPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, _computePipeline, _dependencies.BufferManager, _dependencies.RenderTargets!));
        AddPassInstance(lightCullingPass);

        AddPassInstance(_passOwnership.Track(new VariableRateShadingPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _dependencies.HasIncompatibleVariableRateShadingForwardOutput,
            _dependencies.GiPipelineCacheService)));

        AddPassInstance(_passOwnership.Track(new EnvironmentPrefilterPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.EnvironmentManager!,
            _dependencies.GiPipelineCacheService)));

        AddPassInstance(_passOwnership.Track(new SimpleDdgiLightTreePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.SimpleDdgiLightTreeResources!,
            _dependencies.GiPipelineCacheService)));

        // Create forward+ rendering pass
        ForwardNearFieldDirectSourceAttachmentBinding?
            nearFieldDirectSourceBinding =
                _dependencies.AdvancedGiAdmission.GraphModes.UsesNearFieldHiZResidual
                    ? new ForwardNearFieldDirectSourceAttachmentBinding(
                        _dependencies.RenderTargets!.NearFieldDirectSource!,
                        _dependencies.RenderTargets.NearFieldReceiverPayload!,
                        _dependencies.RenderTargets.NearFieldTraceRasterDepth,
                        _dependencies.NearFieldResidual.PipelineConfiguration)
                    : null;
        ForwardGiCausticReceiverAttachmentBinding?
            giCausticReceiverBinding =
                _dependencies.AdvancedGiAdmission.GraphModes.UsesCausticWorldCache
                    ? new ForwardGiCausticReceiverAttachmentBinding(
                        _dependencies.RenderTargets!.GiCausticReceiverPayload!,
                        _dependencies.GiCaustic.ReceiverPipelineConfiguration)
                    : null;
        var hybridReflectionReceiverBinding =
            new ForwardHybridReflectionReceiverAttachmentBinding(
                _dependencies.RenderTargets!.HybridReflectionReceiverPayload!,
                _dependencies.RenderTargets.HybridReflectionRawMetadata!,
                ForwardHybridReflectionReceiverPipelineConfiguration
                    .Production);
        var forwardPass = _passOwnership.Track(new ForwardPlusPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _meshPipeline,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _foliagePipeline,
            _dependencies.BufferManager,
            _dependencies.FoliageManager,
            _skyboxPipeline,
            _dependencies.GiPipelineCacheService,
            nearFieldDirectSourceBinding,
            nearFieldDirectSourceRuntimeAvailable: () =>
                _dependencies.NearFieldResidual.IsGenerationExecutable,
            giCausticReceiverBinding: giCausticReceiverBinding,
            giCausticRuntimeAvailable: () =>
                _dependencies.GiCaustic.FrameAvailable,
            hybridReflectionReceiverBinding:
            hybridReflectionReceiverBinding,
            simpleDdgiReceiverFeedbackRuntime:
            _dependencies.SimpleDdgiReceiverFeedback,
            nearFieldDirectSourceExecutionExtent: () =>
                _dependencies.NearFieldResidual.CaptureGraphResources().Runtime?
                    .ExecutionExtent ?? default));
        _forwardPlusPass = forwardPass;
        forwardPass.ConfigureSecondaryViews(_dependencies.SceneDataBuilder, _foliageCullPass, _dependencies.AutomaticPlanarReflectionManager);
        AddPassInstance(forwardPass);

        if (_dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding)
        {
            SimpleDdgiGuidingFrameCoordinator guidingCoordinator =
                _dependencies.SimpleDdgiGuidingFrameCoordinator ??
                throw new InvalidOperationException(
                    "The admitted C3 graph has no frame coordinator.");
            AddPassInstance(_passOwnership.Track(new SimpleDdgiGuidingSampleGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, guidingCoordinator)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiGuidingTrainGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, guidingCoordinator)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiGuidingBuildGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, guidingCoordinator)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiGuidingValidateGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, guidingCoordinator)));
        }

        if (_dependencies.AdvancedGiAdmission.GraphModes.UsesCausticWorldCache)
        {
            GiCausticVulkanRuntime causticRuntime =
                _dependencies.GiCaustic.CaptureGraphResources().Runtime ??
                throw new InvalidOperationException(
                    "The admitted C4 graph has no concrete Vulkan runtime.");
            AddPassInstance(_passOwnership.Track(new GiCausticTaskGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, causticRuntime)));
            AddPassInstance(_passOwnership.Track(new GiCausticTraceGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, causticRuntime)));
            AddPassInstance(_passOwnership.Track(new GiCausticCacheBuildGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, causticRuntime)));
            AddPassInstance(_passOwnership.Track(new GiCausticResolveGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, causticRuntime)));
            AddPassInstance(_passOwnership.Track(new GiCausticCompositeGraphPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, causticRuntime)));
        }

        if (_dependencies.AdvancedGiAdmission.GraphModes.UsesNearFieldHiZResidual)
        {
            NearFieldResidualGraphResourceSnapshot nearFieldResources =
                _dependencies.NearFieldResidual.CaptureGraphResources();
            if (nearFieldResources.Runtime is null)
                throw new InvalidOperationException(
                    "The admitted C5 graph has no concrete Vulkan runtime.");
            Func<SimpleDdgiNearFieldResidualVulkanRuntime?>
                nearFieldRuntimeProvider = () =>
                    _dependencies.NearFieldResidual.CaptureGraphResources().Runtime;
            AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualResetPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                nearFieldRuntimeProvider)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualPreparePass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                nearFieldRuntimeProvider)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualClassifyPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                nearFieldRuntimeProvider)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualTracePass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                nearFieldRuntimeProvider)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualTemporalPass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                nearFieldRuntimeProvider)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualFinalizePass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                nearFieldRuntimeProvider)));
            for (int iteration = 0;
                 iteration < _dependencies.NearFieldResidual.Plan.Layout
                     .FilterIterationCount;
                 iteration++)
            {
                AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualFilterPass(
                    _dependencies.Context,
                    _dependencies.Swapchain,
                    _dependencies.BindlessHeap,
                    nearFieldRuntimeProvider,
                    iteration)));
            }

            AddPassInstance(
                _passOwnership.Track(new SimpleDdgiNearFieldResidualFrequencySeparationPass(
                    _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                    nearFieldRuntimeProvider)));
            AddPassInstance(_passOwnership.Track(new SimpleDdgiNearFieldResidualCompositePass(
                _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
                nearFieldRuntimeProvider)));
        }

        var simpleDdgiPageDemandPass = _passOwnership.Track(new SimpleDdgiPageDemandPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.RenderTargets!,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiPageDemandPass = simpleDdgiPageDemandPass;
        AddPassInstance(simpleDdgiPageDemandPass);

        var simpleDdgiPageResidencyPass = _passOwnership.Track(new SimpleDdgiPageResidencyPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiPageResidencyPass = simpleDdgiPageResidencyPass;
        AddPassInstance(simpleDdgiPageResidencyPass);

        // Reflection work is graphics-only and is recorded after the main graph has consumed
        // the old published layer. It has its own conditional pass trio because the graph's
        // fixed production order remains the latency-critical main-view contract.
        _reflectionProbeCapturePass = new ReflectionProbeCapturePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.ReflectionProbeManager!,
            _dependencies.Settings.Reflections,
            new ForwardPlusReflectionProbeCaptureSceneRenderer(forwardPass));
        _reflectionProbePrefilterPass = new ReflectionProbePrefilterPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.ReflectionProbeManager!,
            _dependencies.Settings.Reflections,
            _dependencies.GiPipelineCacheService);
        _reflectionProbePublishPass = new ReflectionProbePublishPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.ReflectionProbeManager!,
            _dependencies.Settings.Reflections,
            _reflectionProbeCompletionValues);
        _dependencies.Lifetime.RunStartupStep(
            "RenderPass.Initialize.ReflectionProbeCapturePass",
            _reflectionProbeCapturePass.Initialize);
        _dependencies.Lifetime.RunStartupStep(
            "RenderPass.Initialize.ReflectionProbePrefilterPass",
            _reflectionProbePrefilterPass.Initialize);
        _dependencies.Lifetime.RunStartupStep(
            "RenderPass.Initialize.ReflectionProbePublishPass",
            _reflectionProbePublishPass.Initialize);

        var farFieldClipmapBakePass = _passOwnership.Track(new FarFieldClipmapBakePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.FarFieldClipmapManager!,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(farFieldClipmapBakePass);

        var simpleDdgiSchedulePass = _passOwnership.Track(new SimpleDdgiSchedulePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiSchedulePass = simpleDdgiSchedulePass;
        AddPassInstance(simpleDdgiSchedulePass);

        var simpleDdgiTracePass = _passOwnership.Track(new SimpleDdgiTracePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.FarFieldClipmapManager!,
            _dependencies.AccelerationStructureManager!,
            _dependencies.SimpleDdgiLightTreeResources!,
            _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiTracePass = simpleDdgiTracePass;
        AddPassInstance(simpleDdgiTracePass);

        var simpleDdgiRelocateClassifyPass = _passOwnership.Track(new SimpleDdgiRelocateClassifyPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.FarFieldClipmapManager!,
            _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiRelocateClassifyPass = simpleDdgiRelocateClassifyPass;
        AddPassInstance(simpleDdgiRelocateClassifyPass);

        var simpleDdgiAcceleratedSolvePass = _passOwnership.Track(new SimpleDdgiAcceleratedSolvePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiAcceleratedSolvePass = simpleDdgiAcceleratedSolvePass;
        AddPassInstance(simpleDdgiAcceleratedSolvePass);

        var simpleDdgiTransportPass = _passOwnership.Track(new SimpleDdgiTransportPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.FarFieldClipmapManager!,
            _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiTransportPass = simpleDdgiTransportPass;
        AddPassInstance(simpleDdgiTransportPass);

        var simpleDdgiBlendPass = _passOwnership.Track(new SimpleDdgiBlendPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.FarFieldClipmapManager!,
            _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiBlendPass = simpleDdgiBlendPass;
        AddPassInstance(simpleDdgiBlendPass);

        var simpleDdgiDirectionalRadiancePass =
            _passOwnership.Track(new SimpleDdgiDirectionalRadiancePass(
                _dependencies.Context,
                _dependencies.Swapchain,
                _dependencies.BindlessHeap,
                _dependencies.Settings,
                _dependencies.SimpleDdgiVolumeManager!,
                _dependencies.FarFieldClipmapManager!,
                _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                _dependencies.GiPipelineCacheService));
        _simpleDdgiDirectionalRadiancePass =
            simpleDdgiDirectionalRadiancePass;
        AddPassInstance(simpleDdgiDirectionalRadiancePass);

        var simpleDdgiPublishPass = _passOwnership.Track(new SimpleDdgiPublishPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiPublishPass = simpleDdgiPublishPass;
        AddPassInstance(simpleDdgiPublishPass);

        var simpleDdgiTransportAuditPass = _passOwnership.Track(new SimpleDdgiTransportAuditPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiTransportAuditPass = simpleDdgiTransportAuditPass;
        AddPassInstance(simpleDdgiTransportAuditPass);

        var simpleDdgiSchedulerCommitPass = _passOwnership.Track(new SimpleDdgiSchedulerCommitPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiSchedulerCommitPass = simpleDdgiSchedulerCommitPass;
        AddPassInstance(simpleDdgiSchedulerCommitPass);

        AddPassInstance(_passOwnership.Track(new SimpleDdgiUrgentRelightPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            simpleDdgiSchedulePass,
            simpleDdgiTracePass,
            simpleDdgiRelocateClassifyPass,
            simpleDdgiDirectionalRadiancePass,
            simpleDdgiAcceleratedSolvePass,
            simpleDdgiTransportPass,
            simpleDdgiBlendPass,
            simpleDdgiPublishPass,
            simpleDdgiSchedulerCommitPass)));

        var simpleDdgiPageFeedbackPass = _passOwnership.Track(new SimpleDdgiPageFeedbackPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager!,
            _dependencies.GiPipelineCacheService));
        _simpleDdgiPageFeedbackPass = simpleDdgiPageFeedbackPass;
        AddPassInstance(simpleDdgiPageFeedbackPass);

        var skyboxPass = _passOwnership.Track(new SkyboxPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, _skyboxPipeline, _dependencies.RenderTargets!, _dependencies.Settings));
        AddPassInstance(skyboxPass);

        AddPassInstance(_passOwnership.Track(new AutomaticPlanarReflectionPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.AutomaticPlanarReflectionManager,
            forwardPass,
            _dependencies.GiPipelineCacheService)));

        HybridReflectionVulkanRuntime hybridReflectionRuntime =
            _dependencies.HybridReflectionRuntime ?? throw new InvalidOperationException(
                "The hybrid reflection graph requires its shared runtime.");
        AddPassInstance(_passOwnership.Track(new HybridReflectionClassifyPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new HybridReflectionDdgiBasePass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new HybridReflectionSsrPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new HybridReflectionRayQueryPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new HybridReflectionResolvePass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new HybridReflectionTemporalPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new HybridReflectionSpatialPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new HybridReflectionCompositePass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));
        AddPassInstance(_passOwnership.Track(new OpaqueSceneColorSnapshotPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            hybridReflectionRuntime)));

        var transparentForwardPass = _passOwnership.Track(new TransparentForwardPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _meshPipeline,
            _dependencies.RenderTargets!,
            forwardPass,
            _dependencies.RaySceneDescriptorBank,
            _dependencies.SimpleDdgiReceiverFeedback));
        AddPassInstance(transparentForwardPass);

        var weightedTransparentPass = _passOwnership.Track(new WeightedTransparentPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _meshPipeline,
            _dependencies.RenderTargets!,
            forwardPass,
            _dependencies.RaySceneDescriptorBank,
            _dependencies.SimpleDdgiReceiverFeedback));
        AddPassInstance(weightedTransparentPass);

        var weightedOitCompositePass = _passOwnership.Track(new WeightedOitCompositePass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, _weightedOitCompositePipeline, _dependencies.RenderTargets!));
        AddPassInstance(weightedOitCompositePass);

        var particlePass = _passOwnership.Track(new ParticlePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _particlePipeline,
            _dependencies.BufferManager,
            _dependencies.RenderTargets!,
            _dependencies.Settings.Particles,
            _dependencies.SimpleDdgiReceiverFeedback));
        AddPassInstance(_gpuParticleResetGraphPass);
        AddPassInstance(_gpuParticleSimulateGraphPass);
        AddPassInstance(_gpuParticleSortGraphPass);
        AddPassInstance(particlePass);

        var simpleDdgiProbeDebugPass = _passOwnership.Track(new SimpleDdgiProbeDebugPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.BufferManager,
            _dependencies.StagingRing,
            _dependencies.RenderTargets!,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(simpleDdgiProbeDebugPass);

        var debugDrawPass = _passOwnership.Track(new DebugDrawPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.BufferManager,
            _dependencies.StagingRing,
            _dependencies.RenderTargets!,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(debugDrawPass);

        var debugOverlayPass = _passOwnership.Track(new DebugOverlayPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(debugOverlayPass);

        var fogPass = _passOwnership.Track(new FogPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.BufferManager,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _dependencies.SimpleDdgiVolumeManager,
            _dependencies.RaySceneDescriptorBank!,
            _dependencies.SimpleDdgiReceiverFeedback,
            _dependencies.GiPipelineCacheService));
        _fogPass = fogPass;
        AddPassInstance(fogPass);

        var autoExposurePass = _passOwnership.Track(new AutoExposurePass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _dependencies.AutoExposureManager!,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(autoExposurePass);

        var bloomPass = _passOwnership.Track(new BloomPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(bloomPass);

        var toneMapCompositePass = _passOwnership.Track(new ToneMapCompositePass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap, _compositePipeline, _ldrCompositePipeline, _dependencies.RenderTargets!,
            _dependencies.Settings));
        AddPassInstance(toneMapCompositePass);

        var antiAliasingPass = _passOwnership.Track(new AntiAliasingPass(
            _dependencies.Context,
            _dependencies.Swapchain,
            _dependencies.BindlessHeap,
            _dependencies.RenderTargets!,
            _dependencies.Settings,
            () => _dependencies.SmaaResources?.IsReady == true,
            _dependencies.GiPipelineCacheService));
        AddPassInstance(antiAliasingPass);
        // Keep the public AfterPostProcessing insertion anchor. Its source stays empty
        // during scene recording; the actual overlay is recorded once by EndFrame.
        AddPassInstance(_passOwnership.Track(new ImGuiRenderPass(
            _dependencies.Context, _dependencies.Swapchain, _dependencies.BindlessHeap,
            _dependencies.BufferManager, _dependencies.StagingRing, _dependencies.OverlayDrawData,
            _dependencies.GiPipelineCacheService)));
        ProductionRenderPipelineDeclaration.Instance.RegisterPasses(
            _dependencies.RenderGraph,
            passInstances,
            _dependencies.AdvancedGiAdmission.GraphModes, _passOwnership.Register);
        foreach (RenderPassBase asyncCandidate in passInstances.Values.Where(pass => pass.SupportsAsyncCompute))
        {
            if (!AsyncComputePassCatalog.IsProductionCandidate(asyncCandidate.Name))
            {
                throw new InvalidOperationException(
                    $"Async-capable pass '{asyncCandidate.Name}' has no production async-compute audit classification.");
            }
        }

        ProductionRenderPipelineDeclaration.Instance.ValidatePassOrder(
            _dependencies.RenderGraph.PassNames,
            _dependencies.AdvancedGiAdmission.GraphModes);

        _dependencies.RenderGraph.Initialize(_dependencies.Lifetime.RunStartupStep);
        System.Diagnostics.Debug.WriteLine("Render graph initialized.");
    }
    internal bool PrepareHybridReflectionReceiverPipelines()
    {
        // Screen-pipeline publication waits only for the exact opaque and
        // foliage receivers. Cache-specialized lanes preserve identical
        // output but are not quality-critical and are prepared after the
        // first full-quality present in progressive startup.
        if (!TryPrepareHybridReflectionExactReceiverCombination(
                nearFieldDirectSource: false,
                giCausticReceiver: false))
        {
            return false;
        }

        bool nearFieldDirectSource =
            _dependencies.AdvancedGiAdmission.GraphModes.UsesNearFieldHiZResidual &&
            _meshPipeline.NearFieldDirectSourceConfiguration
                .SourceProducerMode ==
            SimpleDdgiNearFieldSourceProducerMode.ForwardMrt;
        bool giCausticReceiver =
            _dependencies.AdvancedGiAdmission.GraphModes.UsesCausticWorldCache;
        if (nearFieldDirectSource && giCausticReceiver &&
            !_meshPipeline.CombinedAdvancedGiAttachmentEnabled)
        {
            giCausticReceiver = false;
        }

        if ((nearFieldDirectSource || giCausticReceiver) &&
            !TryPrepareHybridReflectionExactReceiverCombination(
                nearFieldDirectSource,
                giCausticReceiver))
        {
            return false;
        }

        Volatile.Write(
            ref _hybridReflectionReceiverPipelinesPrepared,
            1);
        return true;
    }
    internal bool TryPrepareHybridReflectionExactReceiverCombination(
                bool nearFieldDirectSource,
                bool giCausticReceiver)
    {
        if (!_meshPipeline.TryPrepareHybridReflectionExactPipelines(
                nearFieldDirectSource,
                giCausticReceiver))
        {
            return false;
        }

        return !_foliagePipeline.IsPrepared ||
            _foliagePipeline.TryPrepareHybridReflectionExactPipelines(
                nearFieldDirectSource,
                giCausticReceiver);
    }
    internal bool PrepareHybridReflectionReceiverPerformancePipelines()
    {
        if (!_meshPipeline
                .TryPrepareHybridReflectionPerformancePipelineBank() ||
            !_meshPipeline
                .AreHybridReflectionPerformancePipelineBankReady())
        {
            return false;
        }

        Volatile.Write(
            ref _hybridReflectionReceiverPerformancePipelinesPrepared,
            1);
        return true;
    }
    internal bool AreReceiverCachePerformancePipelinesRequested()
    {
        SimpleDdgiReceiverCacheMode requestedMode =
            SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                _dependencies.Settings.GlobalIllumination.SimpleDdgiReceiverCacheMode,
                _dependencies.Settings.Diagnostics.ForceForwardGiReceiverCacheForBenchmark,
                _dependencies.Settings.Diagnostics.ForceExactForwardGiGatherForBenchmark);
        return requestedMode.UsesCache();
    }
    internal void PrepareHybridReflectionsForFullQuality()
    {
        HybridReflectionVulkanRuntime runtime =
            _dependencies.HybridReflectionRuntime ?? throw new InvalidOperationException(
                "Hybrid reflection runtime is not initialized.");

        // BeginInitializeAsync also releases a claim made by
        // DeferInitialize. Exact receiver readiness is the quality gate;
        // the cache-specialized lanes are output-equivalent acceleration
        // paths and may safely retain the exact opaque fallback.
        runtime.BeginInitializeAsync(
                PrepareHybridReflectionReceiverPipelines)
            .GetAwaiter()
            .GetResult();

        // An already-published runtime does not invoke the callback. Run
        // the idempotent preparation once more so newly prepared foliage
        // families on a scene transition are covered as well.
        if (!PrepareHybridReflectionReceiverPipelines() ||
            !runtime.ScreenPipelinesAvailable)
        {
            throw new InvalidOperationException(
                "Hybrid reflections were requested, but their exact " +
                "screen and receiver pipeline bank is unavailable. " +
                $"Runtime: {runtime.FailureDetail}; mesh: " +
                $"{_meshPipeline.HybridReflectionFailureReason}; foliage: " +
                _foliagePipeline.HybridReflectionPipelineFailureReason);
        }

        if (!RendererBuildConfiguration.ProgressivePipelineStartup &&
            !PrepareHybridReflectionReceiverPerformancePipelines())
        {
            throw new InvalidOperationException(
                "Hybrid reflections were requested in blocking startup, " +
                "but their cache-specialized receiver pipeline bank is unavailable. " +
                _meshPipeline.HybridReflectionFailureReason);
        }
    }
    internal bool TryPreparePostFirstPresentFamily(
                string stepName,
                Func<bool> prepare)
    {
        try
        {
            bool prepared = false;
            _dependencies.Lifetime.RunStartupStep(
                stepName,
                () => prepared = prepare());
            return prepared;
        }
        catch (Exception exception) when (
            exception is VulkanException or IOException or
                InvalidOperationException or ArgumentException or
                OverflowException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"{stepName} failed; exact canonical rendering retained: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }
    internal bool AreSceneReceiverFeedbackPipelinesReady(
                ScenePipelineManifest manifest,
                TransparencyMode transparencyMode,
                bool rayVariantsRequired,
                bool foliageRequired,
                bool particlePreparationRequired,
                bool fogRequired)
    {
        bool maskedReady =
            !manifest.Requires(SceneMaterialPipelineKinds.Masked) ||
            _meshPipeline.AlphaMaskReceiverFeedbackPipelinesAvailable;
        bool transparentReady = !manifest.HasTransparentSurface ||
            (transparencyMode == TransparencyMode.WeightedBlendedOit
                ? _meshPipeline.WeightedOitReceiverFeedbackPipeline.Handle != 0
                : _meshPipeline.TransparentReceiverFeedbackPipeline.Handle != 0 &&
                  (!manifest.Requires(SceneMaterialPipelineKinds.ThinGlass) ||
                   _meshPipeline.ThinGlassReceiverFeedbackPipeline.Handle != 0));
        bool rayFeedbackRequired = rayVariantsRequired &&
            !manifest.Requires(
                SceneMaterialPipelineKinds.ThickTransmission);
        bool rayReady = !rayFeedbackRequired ||
            (transparencyMode == TransparencyMode.WeightedBlendedOit
                ? _meshPipeline.RayWeightedOitReceiverFeedbackPipeline.Handle != 0
                : _meshPipeline.RayTransparentReceiverFeedbackPipeline.Handle != 0);
        return maskedReady && transparentReady && rayReady &&
            (!foliageRequired ||
             _foliagePipeline.ReceiverFeedbackPipelinesAvailable) &&
            (!particlePreparationRequired ||
             _particlePipeline.ReceiverFeedbackPipelinesAvailable) &&
            (!fogRequired ||
             _fogPass.ReceiverFeedbackPipelineAvailable);
    }
    internal Action? PrepareScenePipelines(ScenePipelineManifest pipelineManifest, bool hasFoliage, IReadOnlyCollection<ParticleBlendMode> particleBlendModes, bool initialScenePreparation, bool firstPresentCriticalOnly, bool deferPostFirstPresentSpecializations, Action publishSpecializations)
    {
        Action? deferredPreparation = null;

        bool exhaustive = RendererBuildConfiguration.PipelineStartupMode ==
                          RendererPipelineStartupMode.Exhaustive;
        bool receiverFeedbackRequired =
            _dependencies.SimpleDdgiReceiverFeedback?.GraphicsPipelinesRequested == true;
        bool receiverCacheRequired =
            _forwardPlusPass is not null &&
            SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                    _dependencies.Settings.GlobalIllumination
                        .SimpleDdgiReceiverCacheMode,
                    _dependencies.Settings.Diagnostics
                        .ForceForwardGiReceiverCacheForBenchmark,
                    _dependencies.Settings.Diagnostics
                        .ForceExactForwardGiGatherForBenchmark)
                .UsesCache();
        bool transparentRayVariantsRequired =
            pipelineManifest.HasRealTransparentSurface &&
            _dependencies.Settings.Transparency.Enabled &&
            (_dependencies.Settings.Transparency.ReceiveShadows &&
             pipelineManifest.HasRealTransparentShadowReceiver ||
             pipelineManifest.Requires(
                 SceneMaterialPipelineKinds.ThickTransmission) &&
             _dependencies.Settings.Transparency.ThickTransmissionMode ==
                 ThickTransmissionMode.RayQuery ||
             _dependencies.Settings.Transparency.SampleReflections &&
             pipelineManifest.HasTransparentReflectionReceiver &&
             _dependencies.Settings.Reflections.Enabled &&
             _dependencies.Settings.Reflections.Mode == ReflectionMode.HybridRayQuery);
        bool decalRayVariantsRequired =
            pipelineManifest.HasGeometryDecalShadowReceiver &&
            _dependencies.Settings.Transparency.Enabled &&
            _dependencies.Settings.Decals.ReceiveShadows;
        TransparencyMode transparencyMode =
            _dependencies.Settings.Transparency.Mode;
        bool partitioningEnabled =
            _dependencies.Settings.Transparency.Enabled &&
            _dependencies.Settings.Transparency.PipelinePartitioningEnabled;
        bool rayVariantsRequired =
            _dependencies.Context.RayQuerySupported &&
            (transparentRayVariantsRequired ||
             decalRayVariantsRequired);
        bool decalReceiverCacheRequired =
            receiverFeedbackRequired &&
            _dependencies.Settings.Decals.ReceiveGlobalIllumination;
        if (receiverFeedbackRequired &&
            !deferPostFirstPresentSpecializations)
        {
            _dependencies.Lifetime.RunStartupStep(
                "Pipeline.Prepare.DdgiReceiverFeedback",
                _dependencies.SimpleDdgiReceiverFeedback!.PreparePipelines);
        }

        bool directionalGuidingRequired =
            _dependencies.Settings.GlobalIllumination
                .SimpleDdgiDirectionalGuidingMode !=
                SimpleDdgiDirectionalGuidingMode.Off &&
            _dependencies.AdvancedGiAdmission.GraphModes.UsesDirectionalGuiding;
        if (directionalGuidingRequired)
        {
            SimpleDdgiStoragePackingMode storagePackingMode =
                _dependencies.Settings.GlobalIllumination.SimpleDdgiStoragePackingMode
                    .Sanitize();
            _dependencies.Lifetime.RunStartupStep(
                "Pipeline.Prepare.DdgiDirectionalGuiding",
                () => _dependencies.SimpleDdgiGuidingRuntime!.PreparePipelines(
                    storagePackingMode));
        }

        _dependencies.Lifetime.RunStartupStep(
            "Pipeline.Prepare.FirstPresentForwardOpaque",
            _meshPipeline.PrepareFirstPresentForwardOpaquePipeline);

        ScenePipelinePreparationScope preparationScope =
            firstPresentCriticalOnly
                ? ScenePipelinePreparationScope.FirstPresentCritical
                : ScenePipelinePreparationScope.Complete;
        _dependencies.Lifetime.RunStartupStep(
            "Pipeline.Prepare.SceneManifest",
            () => _meshPipeline.PrepareScenePipelineManifest(
                pipelineManifest,
                transparencyMode,
                partitioningEnabled,
                receiverFeedbackRequired,
                rayVariantsRequired,
                decalReceiverCacheRequired,
                preparationScope));

        bool foliageRequired = exhaustive ||
                               _dependencies.Settings.Foliage.Enabled &&
                               hasFoliage;
        if (foliageRequired && !_foliagePipeline.IsPrepared)
        {
            _dependencies.Lifetime.RunStartupStep(
                "Pipeline.Prepare.Foliage",
                _foliagePipeline.Prepare);
        }

        if (_dependencies.Settings.Reflections.MaxProbes > 0)
            _meshPipeline.PrepareAutomaticPlanarCapturePipelines(
                preparationScope == ScenePipelinePreparationScope.Complete);
        if (foliageRequired && (_dependencies.Settings.Reflections.MaxProbes > 0 || pipelineManifest.Requires(
                SceneMaterialPipelineKinds.AutomaticPlanarReceiver)))
            _foliagePipeline.PrepareAutomaticPlanarCapturePipelines();


        bool particlePreparationRequired = exhaustive
            ? !_particlePipeline.IsPrepared
            : _dependencies.Settings.Particles.Enabled &&
              particleBlendModes.Count > 0 &&
              _particlePipeline.RequiresPreparation(
                  particleBlendModes);
        if (particlePreparationRequired)
        {
            _dependencies.Lifetime.RunStartupStep(
                "Pipeline.Prepare.Particle",
                () =>
                {
                    if (exhaustive)
                        _particlePipeline.PrepareAll();
                    else
                        _particlePipeline.Prepare(
                            particleBlendModes);
                });
        }

        bool fogRequired = exhaustive ||
                           _dependencies.Settings.Fog.Enabled &&
                           _dependencies.Settings.Fog.Mode != FogMode.Disabled;
        if (fogRequired && !_fogPass.IsPrepared)
        {
            _dependencies.Lifetime.RunStartupStep(
                "Pipeline.Prepare.Fog",
                _fogPass.PreparePipelines);
        }

        bool hybridReflectionsRequested =
            _dependencies.Settings.Reflections.Enabled &&
            _dependencies.Settings.Reflections.Mode is
                (ReflectionMode.StaticProbesAndSsr or
                 ReflectionMode.StaticProbesAndPlanar or
                 ReflectionMode.HybridRayQuery);
        bool hybridReflectionsRequired = exhaustive ||
            hybridReflectionsRequested;
        if (hybridReflectionsRequested &&
            !_meshPipeline.HybridReflectionAttachmentEnabled)
        {
            throw new InvalidOperationException(
                "Hybrid reflections were requested without a valid " +
                "forward receiver attachment: " +
                _meshPipeline.HybridReflectionFailureReason);
        }
        if (hybridReflectionsRequired &&
            _meshPipeline.HybridReflectionAttachmentEnabled)
        {
            _dependencies.Lifetime.RunStartupStep(
                "Pipeline.Prepare.HybridReflections.FullQuality",
                PrepareHybridReflectionsForFullQuality);
        }

        if (initialScenePreparation && firstPresentCriticalOnly)
        {
            deferredPreparation = () =>
                PreparePostFirstPresentPipelineBank(
                    pipelineManifest,
                    transparencyMode,
                    partitioningEnabled,
                    receiverFeedbackRequired,
                    receiverCacheRequired,
                    rayVariantsRequired,
                    decalReceiverCacheRequired,
                    foliageRequired,
                    particlePreparationRequired,
                    fogRequired,
                    hybridReflectionsRequired, publishSpecializations);

        }
        else if (receiverFeedbackRequired || receiverCacheRequired)
        {
            bool forwardReady = _forwardPlusPass?
                .PrepareSimpleDdgiReceiverPipelineBank(
                    receiverFeedbackRequired) == true;
            bool complete = receiverFeedbackRequired &&
                forwardReady &&
                AreSceneReceiverFeedbackPipelinesReady(
                    pipelineManifest,
                    transparencyMode,
                    rayVariantsRequired,
                    foliageRequired,
                    particlePreparationRequired,
                    fogRequired);
            if (receiverFeedbackRequired)
            {
                _dependencies.SimpleDdgiReceiverFeedback!.PublishPipelineBank(
                    complete,
                    complete
                        ? "receiver-feedback-pipeline-bank-ready"
                        : "receiver-feedback-pipeline-bank-incomplete");
            }
        }

        return deferredPreparation;
    }

    private void PreparePostFirstPresentPipelineBank(
        ScenePipelineManifest pipelineManifest,
        TransparencyMode transparencyMode,
        bool partitioningEnabled,
        bool receiverFeedbackRequired,
        bool receiverCacheRequired,
        bool rayVariantsRequired,
        bool decalReceiverCacheRequired,
        bool foliageRequired,
        bool particlePreparationRequired,
        bool fogRequired,
        bool hybridReflectionsRequired, Action publishSpecializations)
    {
        bool hybridReceiverPerformanceRequired =
            hybridReflectionsRequired &&
            _meshPipeline.HybridReflectionAttachmentEnabled &&
            AreReceiverCachePerformancePipelinesRequested();
        bool meshReady = TryPreparePostFirstPresentFamily(
            "Pipeline.Prepare.PostFirstPresentSpecializations",
            () =>
            {
                _meshPipeline.PrepareScenePipelineManifest(
                    pipelineManifest,
                    transparencyMode,
                    partitioningEnabled,
                    receiverFeedbackRequired,
                    rayVariantsRequired,
                    decalReceiverCacheRequired,
                    ScenePipelinePreparationScope.Complete);
                return true;
            });
        // Cache consumers are siblings of the active opaque families.
        // Prepare them only after that family set is known and publish
        // readiness only after every required lane has a live handle.
        bool hybridReady = !hybridReceiverPerformanceRequired ||
            meshReady && TryPreparePostFirstPresentFamily(
                "Pipeline.Prepare.PostFirstPresentHybridReflectionSpecializations",
                PrepareHybridReflectionReceiverPerformancePipelines);
        if (meshReady)
        {
            // Scene-specialized variants are independent of receiver-cache
            // publication. Make them available immediately so a slow B1
            // native compile cannot hold transparent partitioning and the
            // other post-present paths behind it.
            publishSpecializations();
        }
        bool foliageReady = !receiverFeedbackRequired ||
            !foliageRequired ||
            TryPreparePostFirstPresentFamily(
                "Pipeline.Prepare.PostFirstPresentFoliageFeedback",
                _foliagePipeline.PrepareReceiverFeedbackPipelines);
        bool particleReady = !receiverFeedbackRequired ||
            !particlePreparationRequired ||
            TryPreparePostFirstPresentFamily(
                "Pipeline.Prepare.PostFirstPresentParticleFeedback",
                _particlePipeline.PrepareReceiverFeedbackPipelines);
        bool fogReady = !receiverFeedbackRequired ||
            !fogRequired ||
            TryPreparePostFirstPresentFamily(
                "Pipeline.Prepare.PostFirstPresentFogFeedback",
                _fogPass.PrepareReceiverFeedbackPipeline);
        bool runtimeReady = !receiverFeedbackRequired ||
            TryPreparePostFirstPresentFamily(
                "Pipeline.Prepare.PostFirstPresentReceiverRuntime",
                () =>
                {
                    _dependencies.SimpleDdgiReceiverFeedback!.PreparePipelines();
                    return true;
                });

        // Build the requirement-complete receiver bank last. A cache-only
        // isolation workload publishes its canonical gather without
        // entering unrelated B1/adaptive native compilation.
        bool forwardReady =
            !receiverFeedbackRequired && !receiverCacheRequired ||
            TryPreparePostFirstPresentFamily(
                "Pipeline.Prepare.PostFirstPresentReceiverComputeBank",
                () => _forwardPlusPass?
                    .PrepareSimpleDdgiReceiverPipelineBank(
                        receiverFeedbackRequired) == true);

        bool complete = receiverFeedbackRequired &&
            hybridReady && meshReady && foliageReady && particleReady && fogReady &&
            runtimeReady && forwardReady &&
            AreSceneReceiverFeedbackPipelinesReady(
                pipelineManifest,
                transparencyMode,
                rayVariantsRequired,
                foliageRequired,
                particlePreparationRequired,
                fogRequired);
        if (receiverFeedbackRequired)
        {
            _dependencies.SimpleDdgiReceiverFeedback!.PublishPipelineBank(
                complete,
                complete
                    ? "receiver-feedback-pipeline-bank-ready"
                    : "receiver-feedback-pipeline-bank-incomplete");
        }
    }

}
