using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Owns the SSR-first reflection compute chain. The graph exposes each stage
/// independently while this object retains the shared descriptor ABI, bounded
/// per-frame ray queues, history revision, and pipeline lifetime.
/// </summary>
internal sealed unsafe class HybridReflectionVulkanRuntime : IDisposable
{
    private const uint WorkgroupSize = HybridReflectionGpuContract.ScreenTileSize;
    private const ulong TaskHeaderBytes = 16UL;
    private const ulong TaskBytes =
        HybridReflectionGpuContract.TaskWords * sizeof(uint);
    private const ulong TileHeaderBytes = 16UL;
    private const ulong TileBytes = 16UL;
    private const ulong CounterBytes =
        HybridReflectionGpuContract.CounterWords * sizeof(uint);
    private const ulong IndirectBytes =
        HybridReflectionGpuContract.IndirectArgumentWords * sizeof(uint);
    private const ulong RayIndirectOffset = 0UL;
    private const ulong SsrIndirectOffset = 3UL * sizeof(uint);
    private const ulong DdgiExactMissIndirectOffset =
        HybridReflectionGpuContract.ExactMissIndirectWordOffset * sizeof(uint);

    private readonly VulkanContext _context;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferManager _bufferManager;
    private readonly RenderTargetManager _renderTargets;
    private readonly RenderSettings _settings;
    private readonly AccelerationStructureManager _accelerationStructures;
    private readonly RaySceneDescriptorBank _raySceneDescriptors;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly BufferHandle[] _taskBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _counterBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _counterReadbackBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _indirectBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _tileBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly DescriptorSet[] _descriptorSets =
        new DescriptorSet[RenderingConstants.FramesInFlight];
    private readonly bool[] _counterFrameSubmitted =
        new bool[RenderingConstants.FramesInFlight];
    private readonly HybridReflectionCounterSnapshot[] _completedCounters =
        new HybridReflectionCounterSnapshot[RenderingConstants.FramesInFlight];
    private readonly object _initializationGate = new();
    private readonly HybridReflectionBudgetController _budgetController =
        new();
    private int _completedBudgetSamplesToSkip;

    private nint _entryPointName;
    private DescriptorSetLayout _localSetLayout;
    private DescriptorSetLayout _emptyRaySetLayout;
    private DescriptorSetLayout _colorMipSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorPool _colorMipDescriptorPool;
    private DescriptorSet _opaqueColorMipBaseSet;
    private readonly DescriptorSet[] _transparentColorMipBaseSets =
        new DescriptorSet[RenderingConstants.FramesInFlight];
    private DescriptorSet[] _colorMipDownsampleSets =
        Array.Empty<DescriptorSet>();
    private PipelineLayout _pipelineLayout;
    private PipelineLayout _colorMipPipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _ssrPipeline;
    private VkPipeline _classifyPipeline;
    private VkPipeline _rayPipeline;
    private VkPipeline _ddgiCohortPipeline;
    private VkPipeline _ddgiReconstructPipeline;
    private VkPipeline _ddgiExactMissPipeline;
    private VkPipeline _resolvePipeline;
    private VkPipeline _temporalPipeline;
    private VkPipeline _spatialPipeline;
    private VkPipeline _compositePipeline;
    private VkPipeline _opaqueSceneColorSnapshotPipeline;
    private VkPipeline _colorMipPipeline;
    private uint _allocatedWidth;
    private uint _allocatedHeight;
    private uint _allocatedTaskCapacity;
    private uint _allocatedTileCapacity;
    private ulong _descriptorSignature;
    // 0 = not started, 1 = running, 2 = completed (successfully or degraded).
    // A transition may compile these pipelines away from the render host; the
    // explicit state keeps frame preparation from observing partially-created
    // Vulkan objects or joining the expensive driver call.
    private int _initializationState;
    private TaskCompletionSource<bool>? _initializationCompletion;
    private Func<bool>? _publicationPreparation;
    private bool _backgroundInitializationStarted;
    private TaskCompletionSource<bool>? _publicationCompletion;
    private bool _publicationDeferred;
    private bool _backgroundPublicationStarted;
    private int _screenPipelinesAvailable;
    private int _rayPipelineAvailable;
    private bool _disposeRequested;
    private bool _disposed;
    private bool _historyValid;
    private HybridReflectionHistoryRevision _previousRevision;
    private HybridReflectionHistoryRevision _currentRevision;
    private ReflectionHistoryResetReason _currentResetReasons;
    private ReflectionHistorySourceInvalidation _currentSourceInvalidations;
    private int _preparedHistoryInputValid;
    private ulong _preparedFrameSerial = ulong.MaxValue;
    private uint _preparedTemporalSample = uint.MaxValue;
    private ulong _recordInitializedFrameSerial = ulong.MaxValue;
    private int _recordInitializedBank = -1;

    public HybridReflectionVulkanRuntime(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        BufferManager bufferManager,
        RenderTargetManager renderTargets,
        RenderSettings settings,
        AccelerationStructureManager accelerationStructures,
        RaySceneDescriptorBank raySceneDescriptors,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bindlessHeap = bindlessHeap ??
            throw new ArgumentNullException(nameof(bindlessHeap));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _renderTargets = renderTargets ??
            throw new ArgumentNullException(nameof(renderTargets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accelerationStructures = accelerationStructures ??
            throw new ArgumentNullException(nameof(accelerationStructures));
        _raySceneDescriptors = raySceneDescriptors ??
            throw new ArgumentNullException(nameof(raySceneDescriptors));
        _pipelineCacheService = pipelineCacheService;
        Array.Fill(_taskBuffers, BufferHandle.Invalid);
        Array.Fill(_counterBuffers, BufferHandle.Invalid);
        Array.Fill(_counterReadbackBuffers, BufferHandle.Invalid);
        Array.Fill(_indirectBuffers, BufferHandle.Invalid);
        Array.Fill(_tileBuffers, BufferHandle.Invalid);
    }

    public bool ScreenPipelinesAvailable
    {
        get => Volatile.Read(ref _screenPipelinesAvailable) != 0;
        private set => Volatile.Write(
            ref _screenPipelinesAvailable,
            value ? 1 : 0);
    }

    public bool RayPipelineAvailable
    {
        get => Volatile.Read(ref _rayPipelineAvailable) != 0;
        private set => Volatile.Write(
            ref _rayPipelineAvailable,
            value ? 1 : 0);
    }

    public bool InitializationInProgress =>
        Volatile.Read(ref _initializationState) == 1;
    public bool InitializationCompleted =>
        Volatile.Read(ref _initializationState) == 2;
    public string FailureDetail { get; private set; } =
        "hybrid reflection runtime has not been initialized";
    public uint RayTaskCapacity => _allocatedTaskCapacity;
    public ulong BufferBytes => checked(
        (TaskHeaderBytes + (ulong)_allocatedTaskCapacity * TaskBytes +
         TileHeaderBytes + (ulong)_allocatedTileCapacity * TileBytes +
         CounterBytes * 2UL + IndirectBytes) *
        RenderingConstants.FramesInFlight);

    public BufferHandle GetTaskBuffer(int frameIndex) =>
        GetFrameBuffer(_taskBuffers, frameIndex);

    public BufferHandle GetCounterBuffer(int frameIndex) =>
        GetFrameBuffer(_counterBuffers, frameIndex);

    public BufferHandle GetIndirectBuffer(int frameIndex) =>
        GetFrameBuffer(_indirectBuffers, frameIndex);

    public BufferHandle GetTileBuffer(int frameIndex) =>
        GetFrameBuffer(_tileBuffers, frameIndex);

    public void ReadCompletedFrame(int frameIndex)
    {
        int bank = ValidateFrameIndex(frameIndex);
        if (!_counterFrameSubmitted[bank] ||
            !_counterReadbackBuffers[bank].IsValid)
        {
            _completedCounters[bank] = HybridReflectionCounterSnapshot.Empty;
            return;
        }

        _bufferManager.InvalidateBuffer(_counterReadbackBuffers[bank], 0UL,
            CounterBytes);
        uint* values = (uint*)_bufferManager.GetMappedPointer(
            _counterReadbackBuffers[bank]);
        _completedCounters[bank] = new HybridReflectionCounterSnapshot(
            ReadbackValid: 1,
            SsrHits: values[0],
            RayRequests: values[1],
            RayQueries: values[2],
            RayOverflows: values[3],
            RayHits: values[4],
            RayMisses: values[5],
            DdgiFallbacks: values[6],
            ProbeFallbacks: values[7],
            EnvironmentFallbacks: values[8],
            FullRateTiles: values[9],
            HalfRateTiles: values[10],
            QuarterRateTiles: values[11],
            AnalyticTiles: values[12],
            ReuseTiles: values[13],
            ActiveTiles: values[14],
            TileOverflows: values[15]);
        _counterFrameSubmitted[bank] = false;
    }

    public HybridReflectionCounterSnapshot GetLastCompletedCounters(
        int frameIndex) =>
        _completedCounters[ValidateFrameIndex(frameIndex)];

    public HybridReflectionBudgetDecision BudgetDecision =>
        _budgetController.Current;

    public HybridReflectionBudgetDecision ObserveCompletedBudgetSample(
        in HybridReflectionBudgetSample sample)
    {
        if (_completedBudgetSamplesToSkip > 0)
        {
            _completedBudgetSamplesToSkip--;
            return _budgetController.Current;
        }
        return _budgetController.Observe(sample);
    }

    public void Initialize()
    {
        lock (_initializationGate)
        {
            ThrowIfDisposingLocked();
            if (_initializationState != 0)
                return;
            ClaimInitializationLocked();
        }

        InitializeClaimed();
    }

    /// <summary>
    /// Withdraws optional screen-pipeline publication without starting driver
    /// work. Before initialization this reserves the work; after initialization
    /// it temporarily restores the reflection fallback until every scene-specific
    /// receiver variant has been prepared in the background.
    /// </summary>
    public void DeferInitialize()
    {
        lock (_initializationGate)
        {
            ThrowIfDisposingLocked();
            if (_initializationState == 0)
            {
                ClaimInitializationLocked();
                return;
            }

            if (_initializationState == 2 &&
                ScreenPipelinesAvailable &&
                !_publicationDeferred)
            {
                // The runtime may already have been initialized by the source
                // scene. Withdraw publication while a later scene streams so
                // its first receiver-cache variant cannot be compiled from the
                // render thread.
                ScreenPipelinesAvailable = false;
                FailureDetail =
                    "hybrid reflection receiver pipelines are deferred " +
                    "until scene streaming completes";
                _publicationDeferred = true;
                _publicationCompletion =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>
    /// Claims initialization before queueing driver work so the render host
    /// can use the existing reflection fallback instead of racing into the
    /// same cold pipeline compilation.
    /// </summary>
    public Task BeginInitializeAsync(
        Func<bool>? publicationPreparation = null)
    {
        lock (_initializationGate)
        {
            ThrowIfDisposingLocked();
            if (_initializationState == 2)
            {
                if (!_publicationDeferred ||
                    publicationPreparation == null)
                {
                    return Task.CompletedTask;
                }

                Task publication = _publicationCompletion!.Task;
                if (_backgroundPublicationStarted)
                    return publication;

                _backgroundPublicationStarted = true;
                try
                {
                    _ = Task.Run(() =>
                        PrepareDeferredPublication(
                            publicationPreparation));
                }
                catch
                {
                    _backgroundPublicationStarted = false;
                    throw;
                }

                return publication;
            }
            if (_initializationState == 0)
                ClaimInitializationLocked();
            Task completion = _initializationCompletion!.Task;
            if (_backgroundInitializationStarted)
                return completion;

            _publicationPreparation = publicationPreparation;
            _backgroundInitializationStarted = true;
            try
            {
                _ = Task.Run(InitializeClaimed);
            }
            catch
            {
                _backgroundInitializationStarted = false;
                _publicationPreparation = null;
                _initializationState = 0;
                _initializationCompletion = null;
                throw;
            }

            return completion;
        }
    }

    private void PrepareDeferredPublication(
        Func<bool> publicationPreparation)
    {
        try
        {
            if (!publicationPreparation())
            {
                throw new InvalidOperationException(
                    "hybrid reflection receiver pipelines could not be prepared");
            }

            FailureDetail = string.Empty;
            ScreenPipelinesAvailable = true;
        }
        catch (Exception exception)
        {
            ScreenPipelinesAvailable = false;
            FailureDetail =
                "hybrid reflection receiver pipeline preparation failed: " +
                exception.Message;
        }
        finally
        {
            TaskCompletionSource<bool>? completion;
            lock (_initializationGate)
            {
                _publicationDeferred = false;
                _backgroundPublicationStarted = false;
                completion = _publicationCompletion;
            }
            completion?.TrySetResult(true);
        }
    }

    private void ClaimInitializationLocked()
    {
        _initializationState = 1;
        _initializationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void InitializeClaimed()
    {
        try
        {
            ValidatePushConstants();
            _entryPointName = SilkMarshal.StringToPtr("main");
            CreateLocalSetLayout();
            CreateEmptyRaySetLayoutIfNeeded();
            CreateColorMipSetLayout();
            CreatePipelineCache();
            CreatePipelineLayout();
            CreateColorMipPipelineLayout();
            _ssrPipeline = CreatePipeline(
                _settings.IsPerformanceOptimizationEnabled(
                    PerformanceOptimizationFeature.SparseHybridLobePayload)
                    ? "hybrid_reflection_ssr_sparse_lobe.comp.spv"
                    : "hybrid_reflection_ssr.comp.spv");
            _classifyPipeline = CreatePipeline(
                "hybrid_reflection_classify.comp.spv");
            _ddgiCohortPipeline = CreatePipeline(
                "hybrid_reflection_ddgi_cohort.comp.spv");
            _ddgiReconstructPipeline = CreatePipeline(
                "hybrid_reflection_ddgi_reconstruct.comp.spv");
            _ddgiExactMissPipeline = CreatePipeline(
                "hybrid_reflection_ddgi_exact_miss.comp.spv");
            _resolvePipeline = CreatePipeline(
                "hybrid_reflection_resolve.comp.spv");
            _temporalPipeline = CreatePipeline(
                "hybrid_reflection_temporal.comp.spv");
            _spatialPipeline = CreatePipeline(
                "hybrid_reflection_spatial.comp.spv");
            _compositePipeline = CreatePipeline(
                "hybrid_reflection_composite.comp.spv");
            _opaqueSceneColorSnapshotPipeline = CreatePipeline(
                "opaque_scene_color_snapshot.comp.spv");
            _colorMipPipeline = CreatePipeline(
                "bloom_downsample.comp.spv",
                _colorMipPipelineLayout);
            FailureDetail = string.Empty;
            TryCreateRayPipeline();
            EnsureResources();
            if (_publicationPreparation != null &&
                !_publicationPreparation())
            {
                throw new InvalidOperationException(
                    "hybrid reflection receiver pipelines could not be prepared");
            }
            ScreenPipelinesAvailable = true;
        }
        catch (Exception exception)
        {
            ScreenPipelinesAvailable = false;
            RayPipelineAvailable = false;
            FailureDetail = "hybrid reflection initialization failed: " +
                exception.Message;
            try
            {
                CleanupNative();
            }
            catch (Exception cleanupFailure)
            {
                FailureDetail += "; cleanup failed: " +
                    cleanupFailure.Message;
            }
        }
        finally
        {
            TaskCompletionSource<bool>? completion;
            lock (_initializationGate)
            {
                Volatile.Write(ref _initializationState, 2);
                completion = _initializationCompletion;
            }
            completion?.TrySetResult(true);
        }
    }

    public bool PrepareFrame(SceneRenderingData sceneData)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        if (sceneData.EffectiveReflectionMode is not
                (ReflectionMode.StaticProbesAndSsr or
                 ReflectionMode.StaticProbesAndPlanar or
                 ReflectionMode.HybridRayQuery))
        {
            InvalidateHistory();
            sceneData.HybridReflectionPassEnabled = false;
            return false;
        }

        if (Volatile.Read(ref _initializationState) == 0)
            Initialize();
        if (Volatile.Read(ref _initializationState) != 2 ||
            !ScreenPipelinesAvailable)
        {
            InvalidateHistory();
            sceneData.HybridReflectionPassEnabled = false;
            return false;
        }

        try
        {
            EnsureResources();
        }
        catch (Exception exception)
        {
            ScreenPipelinesAvailable = false;
            RayPipelineAvailable = false;
            FailureDetail = "hybrid reflection resource allocation failed: " +
                exception.Message;
            DestroyDescriptorPool();
            DestroyBuffers();
            sceneData.HybridReflectionPassEnabled = false;
            return false;
        }
        if (!ResourcesMatchScene(sceneData))
        {
            InvalidateHistory();
            sceneData.HybridReflectionPassEnabled = false;
            return false;
        }

        if (_preparedFrameSerial != sceneData.DdgiFrameSerial ||
            _preparedTemporalSample != sceneData.TemporalSampleIndex)
        {
            _preparedFrameSerial = sceneData.DdgiFrameSerial;
            _preparedTemporalSample = sceneData.TemporalSampleIndex;
            _currentRevision = new HybridReflectionHistoryRevision(
                _allocatedWidth,
                _allocatedHeight,
                sceneData.EffectiveReflectionMode,
                ReflectionSettings.ReceiverPayloadAbiVersion,
                _settings.Reflections.SsrFullResolutionRoughness,
                _settings.Reflections.SsrHalfResolutionRoughness,
                _settings.Reflections.SsrQuarterResolutionRoughness,
                sceneData.RaySceneReadiness.ResourceGeneration,
                // Normal probe publication advances the radiometric generation
                // continuously and must not destroy temporal accumulation.
                // Only an incompatible physical-ownership topology change
                // invalidates every DDGI reflection-history receiver.
                sceneData.SimpleDdgiTransportTopologyGeneration,
                sceneData.GiTransportMaterialRevision,
                sceneData.ReflectionProbeContentRevision,
                sceneData.ReflectionEnvironmentGeneration,
                sceneData.CaptureCameraCutSerial,
                sceneData.EffectiveReflectionImplementation,
                ReflectionSettings.HistoryMetadataAbiVersion,
                sceneData.AutomaticPlanarCaptureGeneration);
            _currentResetReasons = _currentRevision.ResolveResetReasons(
                _previousRevision, _historyValid);
            _currentSourceInvalidations =
                _currentRevision.ResolveSourceInvalidations(
                    _previousRevision,
                    _historyValid);
            if ((_currentResetReasons &
                    ReflectionHistoryResetReason.CameraCut) != 0)
            {
                _budgetController.Reset();
            }
            _preparedHistoryInputValid =
                _historyValid && _currentResetReasons ==
                    ReflectionHistoryResetReason.None
                    ? 1
                    : 0;
        }

        sceneData.HybridReflectionPassEnabled = true;
        sceneData.HybridReflectionWidth = _allocatedWidth;
        sceneData.HybridReflectionHeight = _allocatedHeight;
        sceneData.HybridReflectionRayQueryCapacity =
            sceneData.EffectiveReflectionMode == ReflectionMode.HybridRayQuery &&
            RayPipelineAvailable
                ? _allocatedTaskCapacity
                : 0u;
        // RecordTemporal publishes the output history before later graph stages
        // ask PrepareFrame again. Keep the input-history decision latched for
        // the whole submission instead of reporting that newly-written output
        // as if it had been available to the same frame.
        sceneData.HybridReflectionHistoryValid = _preparedHistoryInputValid;
        sceneData.HybridReflectionHistoryResetReason = _currentResetReasons;
        sceneData.HybridReflectionSourceInvalidation =
            _currentSourceInvalidations;
        return true;
    }

    public void InvalidateHistory()
    {
        _historyValid = false;
        _preparedHistoryInputValid = 0;
        _preparedFrameSerial = ulong.MaxValue;
        _preparedTemporalSample = uint.MaxValue;
    }

    public void SynchronizeDeterministicCapturePhase()
    {
        InvalidateHistory();
        _budgetController.Reset();
        // Completed timestamp/counter samples lag the current frame. Do not
        // seed the canonical quality route with pre-synchronization feedback.
        _completedBudgetSamplesToSkip = RenderingConstants.FramesInFlight;
    }

    public bool ShouldTraceRays(SceneRenderingData sceneData) =>
        PrepareFrame(sceneData) &&
        sceneData.EffectiveReflectionMode == ReflectionMode.HybridRayQuery &&
        RayPipelineAvailable && _allocatedTaskCapacity != 0u &&
        _accelerationStructures.Active &&
        sceneData.RaySceneReadiness.IsReady(
            RaySceneConsumer.Reflection,
            RaySceneRequirement.ForReflections(_settings.Reflections)
                .RequiredCategories);

    public bool ShouldEvaluateDdgiBase(SceneRenderingData sceneData) =>
        PrepareFrame(sceneData) &&
        sceneData.EffectiveReflectionMode !=
            ReflectionMode.StaticProbesAndPlanar &&
        sceneData.SimpleDdgiActive != 0 &&
        _settings.GlobalIllumination.EffectiveSimpleDdgiDirectionalRadianceMode !=
            SimpleDdgiDirectionalRadianceMode.Off &&
        _settings.GlobalIllumination.EffectiveSimpleDdgiGlossyTransportMode !=
            SimpleDdgiGlossyTransportMode.Off;

    public bool ShouldSnapshotOpaqueSceneColor(
        SceneRenderingData sceneData) =>
        PrepareFrame(sceneData) &&
        sceneData.TransparentPassEnabled &&
        sceneData.TransparentSampleReflections &&
        sceneData.HasTransparentReflectionReceivers;

    public void RecordSsr(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!PrepareFrame(sceneData))
            return;
        int bank = ValidateFrameIndex(frameIndex);
        bool traceRays = ShouldTraceRays(sceneData);
        BeginFrameWork(commandBuffer, bank, sceneData);
        if (sceneData.EffectiveReflectionMode ==
            ReflectionMode.StaticProbesAndPlanar)
        {
            // Resolve owns every pixel in planar-only mode. The frame reset
            // above still establishes fresh task/counter state and resource
            // layouts, while no SSR work or color-mip build is submitted.
            return;
        }
        RecordColorMipTail(
            commandBuffer,
            _renderTargets.SceneColor,
            _opaqueColorMipBaseSet,
            "Hybrid Reflection Opaque HDR Mip Build");

        ReflectionSettings reflection = _settings.Reflections;
        bool adaptive = sceneData.EffectiveReflectionImplementation ==
            ReflectionImplementationMode.Adaptive;
        if (adaptive)
        {
            BindPipelineAndDescriptors(
                commandBuffer,
                _classifyPipeline,
                bank,
                bindRayScene: false);
            var classifyPush = new GPUHybridReflectionClassifyPushConstants
            {
                ScreenWidth = _allocatedWidth,
                ScreenHeight = _allocatedHeight,
                FullResolutionRoughness = reflection
                    .SsrFullResolutionRoughness,
                HalfResolutionRoughness = reflection
                    .SsrHalfResolutionRoughness,
                QuarterResolutionRoughness = reflection
                    .SsrQuarterResolutionRoughness,
                MaximumReuseMotionPixels = 0.25f,
                HistoryValid = sceneData.HybridReflectionHistoryValid != 0
                    ? 1u
                    : 0u,
                SourceInvalidations = (uint)_currentSourceInvalidations
            };
            Push(commandBuffer, classifyPush);
            DispatchScreen(commandBuffer);
            PublishComputeWrites(commandBuffer);
        }

        BindPipelineAndDescriptors(commandBuffer, _ssrPipeline, bank,
            bindRayScene: false);
        HybridReflectionCounterSnapshot requestFeedback =
            _completedCounters[bank];
        bool requestFeedbackValid =
            requestFeedback.ReadbackValid != 0 &&
            _currentResetReasons == ReflectionHistoryResetReason.None;
        uint rayAdmissionThreshold = traceRays
            ? HybridReflectionBudgetPlanner.ResolveRayQueryAdmissionThreshold(
                _allocatedTaskCapacity,
                _allocatedWidth,
                _allocatedHeight,
                requestFeedback.RayRequests,
                requestFeedbackValid)
            : 0u;
        rayAdmissionThreshold = ScaleAdmissionThreshold(
            rayAdmissionThreshold,
            _budgetController.Current.LowImportanceRayAdmissionScale);
        var push = new GPUHybridReflectionSsrPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndMaximumDistance = new Vector4(
                sceneData.CameraPosition, reflection.SsrMaxDistance),
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            MaximumSteps = checked((uint)reflection.SsrMaxSteps),
            HiZMipCount = PackHiZAndColorMipCounts(
                sceneData.HiZMipCount,
                _renderTargets.BloomMipCount),
            FullResolutionRoughness = reflection.SsrFullResolutionRoughness,
            HalfResolutionRoughness = reflection.SsrHalfResolutionRoughness,
            QuarterResolutionRoughness = reflection.SsrQuarterResolutionRoughness,
            ConfidenceThreshold = reflection.SsrConfidenceThreshold,
            TemporalSampleIndex = sceneData.TemporalSampleIndex,
            HistoryValidAndCurrentFrameIndex =
                (checked((uint)bank) << 1) |
                (sceneData.HybridReflectionHistoryValid != 0 ? 1u : 0u) |
                (adaptive ? 0x80000000u : 0u),
            RayQueriesEnabled = traceRays ? 1u : 0u,
            RayAdmissionThreshold = rayAdmissionThreshold
        };
        Push(commandBuffer, push);
        if (adaptive)
        {
            _context.Api.CmdDispatchIndirect(
                commandBuffer,
                _bufferManager.GetBuffer(_indirectBuffers[bank]),
                SsrIndirectOffset);
        }
        else
        {
            DispatchScreen(commandBuffer);
        }
        PublishComputeWrites(commandBuffer);
    }

    public void RecordRayQuery(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!ShouldTraceRays(sceneData))
            return;
        int bank = ValidateFrameIndex(frameIndex);
        BindPipelineAndDescriptors(commandBuffer, _rayPipeline, bank,
            bindRayScene: true);
        var push = new GPUHybridReflectionRayPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndMaximumDistance = new Vector4(
                sceneData.CameraPosition,
                _settings.Reflections.SsrMaxDistance),
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            TaskCapacity = _allocatedTaskCapacity,
            LightCount = checked((uint)Math.Max(0, sceneData.LightCount)),
            DirectionalLightCount = checked((uint)Math.Max(
                0, sceneData.DirectionalLightCount)),
            LocalLightCount = checked((uint)Math.Max(
                0, sceneData.LocalLightCount)),
            MaximumShadedLights = checked((uint)_settings.Reflections
                .RayQueryHitLightLimit),
            DdgiEnabled = sceneData.SimpleDdgiActive != 0 ? 1u : 0u,
            CurrentFrameIndex = checked((uint)bank),
            TemporalSampleIndex = sceneData.TemporalSampleIndex
        };
        Push(commandBuffer, push);
        _context.Api.CmdDispatchIndirect(commandBuffer,
            _bufferManager.GetBuffer(_indirectBuffers[bank]),
            RayIndirectOffset);
        PublishComputeWrites(commandBuffer);
    }

    public void RecordDdgiBase(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!ShouldEvaluateDdgiBase(sceneData))
            return;
        int bank = ValidateFrameIndex(frameIndex);
        BeginFrameWork(commandBuffer, bank, sceneData);
        Required(_renderTargets.HybridReflectionDdgiCohorts,
                "DDGI cohorts")
            .TransitionToStorageReadWrite(commandBuffer);
        HistoryTarget(bank).TransitionToStorageReadWrite(commandBuffer);
        bool fullResolutionOracle = _settings.Reflections
            .DdgiReflectionFullResolutionOracle;
        uint receiverScale = fullResolutionOracle
            ? 1u
            : _settings.QualityPreset == RenderQualityPreset.Ultra
                ? 2u
                : 4u;
        var push = new GPUHybridReflectionDdgiPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndPadding = new Vector4(sceneData.CameraPosition, 0.0f),
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            ReceiverScale = receiverScale,
            // A representative structured gather is broadcast only to the
            // same receiver identity, normal, roughness, and world-depth
            // cohort. A second cohort closes real material/depth edges without
            // multiplying the common single-surface tile cost; the explicit
            // oracle remains one gather per pixel because its scale is one.
            MaximumSurfaceGroupsPerTile = fullResolutionOracle ? 1u : 2u,
            MinimumConfidence = 0.02f,
            NormalDotThreshold = HybridReflectionGpuContract
                .NormalHistoryDotThreshold,
            MinimumWorldDepthTolerance = HybridReflectionGpuContract
                .MinimumHistoryDepthToleranceMeters,
            RelativeWorldDepthTolerance = HybridReflectionGpuContract
                .RelativeHistoryDepthTolerance,
            Reserved = 0u,
            ForceExactReconstruction = fullResolutionOracle ||
                sceneData.HybridReflectionHistoryValid == 0
                    ? 1u
                    : 0u
        };
        if (push.ForceExactReconstruction == 0u)
        {
            _context.BeginDebugLabel(
                commandBuffer, "Hybrid DDGI Cohort Production");
            try
            {
                BindPipelineAndDescriptors(
                    commandBuffer,
                    _ddgiCohortPipeline,
                    bank,
                    bindRayScene: false);
                Push(commandBuffer, push);
                DispatchScreen(commandBuffer, receiverScale);
            }
            finally
            {
                _context.EndDebugLabel(commandBuffer);
            }
            PublishComputeWrites(commandBuffer);
        }

        _context.BeginDebugLabel(
            commandBuffer, "Hybrid DDGI Cached Reconstruction");
        try
        {
            BindPipelineAndDescriptors(
                commandBuffer,
                _ddgiReconstructPipeline,
                bank,
                bindRayScene: false);
            Push(commandBuffer, push);
            DispatchScreen(commandBuffer);
        }
        finally
        {
            _context.EndDebugLabel(commandBuffer);
        }
        PublishComputeWrites(commandBuffer);

        _context.BeginDebugLabel(
            commandBuffer, "Hybrid DDGI Exact Misses");
        try
        {
            BindPipelineAndDescriptors(
                commandBuffer,
                _ddgiExactMissPipeline,
                bank,
                bindRayScene: false);
            Push(commandBuffer, push);
            _context.Api.CmdDispatchIndirect(
                commandBuffer,
                _bufferManager.GetBuffer(_indirectBuffers[bank]),
                DdgiExactMissIndirectOffset);
        }
        finally
        {
            _context.EndDebugLabel(commandBuffer);
        }
        PublishComputeWrites(commandBuffer);

        // DDGI runs before SSR and borrows the full-capacity scheduler only
        // within this pass. Preserve the payload bytes but restore the header
        // so SSR observes an empty list with the original capacity.
        ResetTileSchedulerAfterDdgi(commandBuffer, bank);
    }

    public void RecordResolve(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!PrepareFrame(sceneData))
            return;
        int bank = ValidateFrameIndex(frameIndex);
        BindPipelineAndDescriptors(commandBuffer, _resolvePipeline, bank,
            bindRayScene: false);
        var push = new GPUHybridReflectionResolvePushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndIntensity = new Vector4(
                sceneData.CameraPosition,
                _settings.Reflections.Intensity),
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            MaximumProbesPerPixel = checked((uint)Math.Max(
                1, _settings.Reflections.MaxProbesPerPixel)),
            ReflectionDebugView = (uint)sceneData.ReflectionDebugView,
            SsrConfidenceThreshold = _settings.Reflections
                .SsrConfidenceThreshold,
            AnalyticTransitionStartRoughness = _settings.Reflections
                .SsrHalfResolutionRoughness,
            AnalyticTransitionEndRoughness = _settings.Reflections
                .SsrQuarterResolutionRoughness,
            DdgiBaseAvailable = ShouldEvaluateDdgiBase(sceneData) ? 1u : 0u,
            CurrentFrameIndex = checked((uint)bank),
            EffectiveReflectionMode =
                (uint)sceneData.EffectiveReflectionMode
        };
        Push(commandBuffer, push);
        DispatchScreen(commandBuffer);
        PublishComputeWrites(commandBuffer);
    }

    public void RecordTemporal(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!PrepareFrame(sceneData))
            return;
        int bank = ValidateFrameIndex(frameIndex);
        TransitionTemporalResources(commandBuffer, bank);
        BindPipelineAndDescriptors(commandBuffer, _temporalPipeline, bank,
            bindRayScene: false);
        var push = new GPUHybridReflectionTemporalPushConstants
        {
            InverseViewProjectionMatrix =
                sceneData.InverseViewProjectionMatrix,
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            MaximumHistoryLength = checked((uint)_settings.Reflections
                .TemporalHistoryLength),
            ResetReasons = (uint)_currentResetReasons,
            MaximumHistoryWeight = 1.0f - 1.0f /
                Math.Max(1, _settings.Reflections.TemporalHistoryLength),
            SourceTransitionWeightScale = HybridReflectionGpuContract
                .SsrToRayQueryHistoryWeightScale,
            VarianceGamma = 2.0f,
            CurrentFrameIndex = checked((uint)bank),
            CameraOnlyReprojection = checked((uint)Math.Max(
                0,
                sceneData.CameraOnlyMotionReprojectionEnabled)),
            SourceInvalidations = (uint)_currentSourceInvalidations
        };
        Push(commandBuffer, push);
        DispatchScreen(commandBuffer);
        PublishComputeWrites(commandBuffer);
        _historyValid = true;
    }

    public void RecordSpatial(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!PrepareFrame(sceneData))
            return;
        int bank = ValidateFrameIndex(frameIndex);
        int passCount = ResolveSpatialPassCount(sceneData);
        if (passCount <= 0)
            return;
        Required(_renderTargets.HybridReflectionRawRadiance,
                "raw radiance")
            .TransitionToStorageReadWrite(commandBuffer);
        HistoryTarget(1 - bank).TransitionToStorageReadWrite(commandBuffer);
        for (int iteration = 0; iteration < passCount; iteration++)
        {
            BindPipelineAndDescriptors(commandBuffer, _spatialPipeline, bank,
                bindRayScene: false);
            var push = new GPUHybridReflectionSpatialPushConstants
            {
                ScreenWidth = _allocatedWidth,
                ScreenHeight = _allocatedHeight,
                Iteration = checked((uint)iteration),
                ReadScratch = (iteration & 1) != 0 ? 1u : 0u,
                NormalPower = 32.0f,
                DepthSigma = 0.0025f * (iteration + 1),
                RoughnessSigma = 0.12f,
                Padding0 = _budgetController.Current
                    .SecondSpatialVarianceThreshold
            };
            Push(commandBuffer, push);
            DispatchScreen(commandBuffer);
            PublishComputeWrites(commandBuffer);
        }
    }

    public void RecordComposite(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!PrepareFrame(sceneData))
            return;
        int bank = ValidateFrameIndex(frameIndex);
        _renderTargets.SceneColor.TransitionToStorageReadWrite(commandBuffer);
        BindPipelineAndDescriptors(commandBuffer, _compositePipeline, bank,
            bindRayScene: false);
        var push = new GPUHybridReflectionCompositePushConstants
        {
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            SpatialPassCount = checked((uint)ResolveSpatialPassCount(
                sceneData)),
            DebugView = (uint)sceneData.ReflectionDebugView,
            FullResolutionRoughness = _settings.Reflections
                .SsrFullResolutionRoughness,
            HalfResolutionRoughness = _settings.Reflections
                .SsrHalfResolutionRoughness,
            QuarterResolutionRoughness = _settings.Reflections
                .SsrQuarterResolutionRoughness
        };
        Push(commandBuffer, push);
        DispatchScreen(commandBuffer);
        PublishComputeWrites(commandBuffer);
        RecordCounterReadback(commandBuffer, bank);
        _renderTargets.SceneColor.TransitionToColorAttachment(commandBuffer);
        _counterFrameSubmitted[bank] = true;
        _previousRevision = _currentRevision;
        _currentResetReasons = ReflectionHistoryResetReason.None;
        _currentSourceInvalidations =
            ReflectionHistorySourceInvalidation.None;
    }

    public void RecordOpaqueSceneColorSnapshot(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        sceneData.OpaqueSceneColorSnapshotAvailable = false;
        if (!ShouldSnapshotOpaqueSceneColor(sceneData))
            return;

        int bank = ValidateFrameIndex(frameIndex);
        RenderTarget snapshot = Required(
            HistoryTarget(1 - bank),
            "opaque SceneColor snapshot");
        _renderTargets.SceneColor.TransitionToStorageReadWrite(commandBuffer);
        snapshot.TransitionToStorageWrite(commandBuffer);
        BindPipelineAndDescriptors(
            commandBuffer,
            _opaqueSceneColorSnapshotPipeline,
            bank,
            bindRayScene: false);
        DispatchScreen(commandBuffer);
        PublishComputeWrites(commandBuffer);
        snapshot.TransitionToShaderRead(commandBuffer);
        _bindlessHeap.RegisterTexture(
            BindlessIndex.OpaqueSceneColorSnapshotTexture,
            snapshot.View,
            _bindlessHeap.ScreenSampler,
            imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        RecordColorMipTail(
            commandBuffer,
            snapshot,
            _transparentColorMipBaseSets[bank],
            "Hybrid Reflection Transparent HDR Mip Build");
        _renderTargets.SceneColor.TransitionToColorAttachment(commandBuffer);
        sceneData.OpaqueSceneColorSnapshotAvailable = true;
    }

    public void OnTargetsRecreated()
    {
        if (Volatile.Read(ref _initializationState) != 2 ||
            !ScreenPipelinesAvailable)
            return;
        try
        {
            EnsureResources();
        }
        catch (Exception exception)
        {
            ScreenPipelinesAvailable = false;
            RayPipelineAvailable = false;
            FailureDetail = "hybrid reflection resize failed: " +
                exception.Message;
            DestroyDescriptorPool();
            DestroyBuffers();
        }
        _historyValid = false;
        _preparedFrameSerial = ulong.MaxValue;
    }

    private bool ResourcesMatchScene(SceneRenderingData sceneData) =>
        _allocatedWidth == sceneData.ScreenWidth &&
        _allocatedHeight == sceneData.ScreenHeight &&
        _allocatedWidth > 1u && _allocatedHeight > 1u &&
        _descriptorPool.Handle != 0 &&
        _colorMipDescriptorPool.Handle != 0;

    private void EnsureResources()
    {
        RenderTarget receiver = Required(
            _renderTargets.HybridReflectionReceiverPayload,
            "receiver payload");
        uint width = receiver.Extent.Width;
        uint height = receiver.Extent.Height;
        uint capacity = HybridReflectionBudgetPlanner.ResolveRayQueryCapacity(
            _settings.Reflections, width, height);
        uint tileCapacity = HybridReflectionGpuContract
            .CalculateScreenTileCapacity(width, height);
        ulong signature = ComputeDescriptorSignature();
        bool buffersValid = AllBuffersValid();
        if (width == _allocatedWidth && height == _allocatedHeight &&
            capacity == _allocatedTaskCapacity && buffersValid &&
            tileCapacity == _allocatedTileCapacity &&
            signature == _descriptorSignature && _descriptorPool.Handle != 0)
        {
            return;
        }

        DestroyDescriptorPool();
        DestroyBuffers();
        _allocatedWidth = width;
        _allocatedHeight = height;
        _allocatedTaskCapacity = capacity;
        _allocatedTileCapacity = tileCapacity;
        AllocateBuffers();
        CreateDescriptorPoolAndSets();
        WriteDescriptorSets();
        CreateColorMipDescriptorSets();
        _descriptorSignature = ComputeDescriptorSignature();
        _historyValid = false;
        _preparedFrameSerial = ulong.MaxValue;
    }

    private void AllocateBuffers()
    {
        ulong taskBytes = checked(TaskHeaderBytes +
            (ulong)_allocatedTaskCapacity * TaskBytes);
        ulong tileBytes = checked(TileHeaderBytes +
            (ulong)_allocatedTileCapacity * TileBytes);
        for (int frameIndex = 0;
             frameIndex < RenderingConstants.FramesInFlight;
             frameIndex++)
        {
            _taskBuffers[frameIndex] = _bufferManager.CreateBuffer(
                taskBytes,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferDevice,
                debugName: $"Hybrid Reflection Ray Tasks Frame {frameIndex}",
                category: MemoryBudgetCategory.RenderTargets);
            _counterBuffers[frameIndex] = _bufferManager.CreateBuffer(
                CounterBytes,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.TransferSrcBit,
                MemoryUsage.AutoPreferDevice,
                debugName: $"Hybrid Reflection Counters Frame {frameIndex}",
                category: MemoryBudgetCategory.RenderTargets);
            _counterReadbackBuffers[frameIndex] =
                _bufferManager.CreateBuffer(
                CounterBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit |
                AllocationCreateFlags.HostAccessRandomBit,
                debugName:
                    $"Hybrid Reflection Counter Readback Frame {frameIndex}",
                category: MemoryBudgetCategory.DiagnosticsAndDebug);
            _indirectBuffers[frameIndex] = _bufferManager.CreateBuffer(
                IndirectBytes,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.IndirectBufferBit,
                MemoryUsage.AutoPreferDevice,
                debugName:
                    $"Hybrid Reflection Indirect Arguments Frame {frameIndex}",
                category: MemoryBudgetCategory.RenderTargets);
            _tileBuffers[frameIndex] = _bufferManager.CreateBuffer(
                tileBytes,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferDevice,
                debugName:
                    $"Hybrid Reflection Active Tiles Frame {frameIndex}",
                category: MemoryBudgetCategory.RenderTargets);
        }
    }

    private void TransitionSsrResources(CommandBuffer commandBuffer, int bank)
    {
        Required(_renderTargets.HybridReflectionReceiverPayload,
            "receiver payload").TransitionToShaderRead(commandBuffer);
        _renderTargets.SceneColor.TransitionToShaderRead(commandBuffer);
        _renderTargets.SceneDepth.TransitionToDepthReadOnly(commandBuffer);
        _renderTargets.MotionVectors.TransitionToShaderRead(commandBuffer);
        Required(_renderTargets.HybridReflectionRawRadiance,
            "raw radiance").TransitionToStorageReadWrite(commandBuffer);
        CurrentMetadataTarget(raw: true, bank)
            .TransitionToStorageReadWrite(commandBuffer);
        HistoryTarget(1 - bank).TransitionToStorageReadWrite(commandBuffer);
        HistoryTarget(bank).TransitionToStorageReadWrite(commandBuffer);
        MetadataTarget(1 - bank).TransitionToStorageReadWrite(commandBuffer);
        Required(_renderTargets.HybridReflectionDdgiCohorts,
                "DDGI cohorts/secondary lobe")
            .TransitionToStorageReadWrite(commandBuffer);
    }

    private void BeginFrameWork(
        CommandBuffer commandBuffer,
        int bank,
        SceneRenderingData sceneData)
    {
        if (_recordInitializedFrameSerial == sceneData.DdgiFrameSerial &&
            _recordInitializedBank == bank)
        {
            return;
        }

        SynchronizePreviousHybridFrame(commandBuffer);
        TransitionSsrResources(commandBuffer, bank);
        ResetTaskAndCounterBuffers(commandBuffer, bank);
        if (_currentResetReasons != ReflectionHistoryResetReason.None)
            ClearHistoryResources(commandBuffer);
        _recordInitializedFrameSerial = sceneData.DdgiFrameSerial;
        _recordInitializedBank = bank;
    }

    private void TransitionTemporalResources(
        CommandBuffer commandBuffer,
        int bank)
    {
        HistoryTarget(1 - bank).TransitionToStorageReadWrite(commandBuffer);
        HistoryTarget(bank).TransitionToStorageReadWrite(commandBuffer);
        MomentsTarget(1 - bank).TransitionToStorageReadWrite(commandBuffer);
        MomentsTarget(bank).TransitionToStorageReadWrite(commandBuffer);
        MetadataTarget(1 - bank).TransitionToStorageReadWrite(commandBuffer);
        MetadataTarget(bank).TransitionToStorageReadWrite(commandBuffer);
    }

    private void ClearHistoryResources(CommandBuffer commandBuffer)
    {
        ClearStorageTarget(commandBuffer, HistoryTarget(0));
        ClearStorageTarget(commandBuffer, HistoryTarget(1));
        ClearStorageTarget(commandBuffer, MomentsTarget(0));
        ClearStorageTarget(commandBuffer, MomentsTarget(1));
        ClearStorageTarget(commandBuffer, MetadataTarget(0));
        ClearStorageTarget(commandBuffer, MetadataTarget(1));
        PublishTransferWrites(commandBuffer);
    }

    private void ClearStorageTarget(
        CommandBuffer commandBuffer,
        RenderTarget target)
    {
        target.TransitionToLayout(
            commandBuffer,
            ImageLayout.General,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            force: true);
        ClearColorValue zero = default;
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            LevelCount = 1u,
            LayerCount = 1u
        };
        _context.Api.CmdClearColorImage(commandBuffer, target.Image,
            ImageLayout.General, &zero, 1u, &range);
    }

    private void ResetTaskAndCounterBuffers(
        CommandBuffer commandBuffer,
        int bank)
    {
        VkBuffer task = _bufferManager.GetBuffer(_taskBuffers[bank]);
        VkBuffer counters = _bufferManager.GetBuffer(_counterBuffers[bank]);
        VkBuffer indirect = _bufferManager.GetBuffer(_indirectBuffers[bank]);
        VkBuffer tiles = _bufferManager.GetBuffer(_tileBuffers[bank]);
        Span<BufferMemoryBarrier2> beforeReset =
            stackalloc BufferMemoryBarrier2[4];
        beforeReset[0] = BarrierBuilder.BufferBarrier(
            task,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            TaskHeaderBytes);
        beforeReset[1] = BarrierBuilder.BufferBarrier(
            counters,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.CopyBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.TransferReadBit,
            PipelineStageFlags2.ClearBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            CounterBytes);
        beforeReset[2] = BarrierBuilder.BufferBarrier(
            indirect,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.IndirectCommandReadBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            IndirectBytes);
        beforeReset[3] = BarrierBuilder.BufferBarrier(
            tiles,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            _bufferManager.GetBufferSize(_tileBuffers[bank]));
        ExecuteBufferBarriers(commandBuffer, beforeReset);

        // Stamp each heterogeneous header in one update. Overlapping fills do
        // not establish ordering and previously produced transfer WAW hazards.
        Span<uint> taskHeader = stackalloc uint[4]
        {
            0u,
            _allocatedTaskCapacity,
            0u,
            0u
        };
        _context.Api.CmdUpdateBuffer(commandBuffer, task, 0UL, taskHeader);
        _context.Api.CmdFillBuffer(commandBuffer, counters, 0UL,
            CounterBytes, 0u);
        Span<uint> indirectHeader = stackalloc uint[]
        {
            0u,
            1u,
            1u,
            0u,
            1u,
            1u,
            0u,
            1u,
            1u
        };
        _context.Api.CmdUpdateBuffer(
            commandBuffer, indirect, 0UL, indirectHeader);
        Span<uint> tileHeader = stackalloc uint[4]
        {
            0u,
            _allocatedTileCapacity,
            0u,
            0u
        };
        _context.Api.CmdUpdateBuffer(
            commandBuffer,
            tiles,
            0UL,
            tileHeader);

        Span<BufferMemoryBarrier2> afterReset =
            stackalloc BufferMemoryBarrier2[4];
        afterReset[0] = BarrierBuilder.BufferBarrier(
            task,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            0UL,
            TaskHeaderBytes);
        afterReset[1] = BarrierBuilder.BufferBarrier(
            counters,
            PipelineStageFlags2.ClearBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            0UL,
            CounterBytes);
        afterReset[2] = BarrierBuilder.BufferBarrier(
            indirect,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.IndirectCommandReadBit,
            0UL,
            IndirectBytes);
        afterReset[3] = BarrierBuilder.BufferBarrier(
            tiles,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit,
            0UL,
            _bufferManager.GetBufferSize(_tileBuffers[bank]));
        ExecuteBufferBarriers(commandBuffer, afterReset);
    }

    private void BindPipelineAndDescriptors(
        CommandBuffer commandBuffer,
        VkPipeline pipeline,
        int bank,
        bool bindRayScene)
    {
        _context.Api.CmdBindPipeline(commandBuffer,
            PipelineBindPoint.Compute, pipeline);
        DescriptorSet storage = _bindlessHeap.StorageBufferSet;
        DescriptorSet textures = _bindlessHeap.TextureSamplerSet;
        DescriptorSet local = _descriptorSets[bank];
        _context.Api.CmdBindDescriptorSets(commandBuffer,
            PipelineBindPoint.Compute, _pipelineLayout,
            0u, 1u, &storage, 0u, null);
        _context.Api.CmdBindDescriptorSets(commandBuffer,
            PipelineBindPoint.Compute, _pipelineLayout,
            1u, 1u, &textures, 0u, null);
        if (bindRayScene)
            _raySceneDescriptors.Bind(commandBuffer,
                PipelineBindPoint.Compute, _pipelineLayout, bank);
        _context.Api.CmdBindDescriptorSets(commandBuffer,
            PipelineBindPoint.Compute, _pipelineLayout,
            3u, 1u, &local, 0u, null);
    }

    private void ResetTileSchedulerAfterDdgi(
        CommandBuffer commandBuffer,
        int bank)
    {
        VkBuffer tiles = _bufferManager.GetBuffer(_tileBuffers[bank]);
        Span<BufferMemoryBarrier2> beforeReset =
            stackalloc BufferMemoryBarrier2[1];
        beforeReset[0] = BarrierBuilder.BufferBarrier(
            tiles,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            TileHeaderBytes);
        ExecuteBufferBarriers(commandBuffer, beforeReset);

        Span<uint> tileHeader = stackalloc uint[4]
        {
            0u,
            _allocatedTileCapacity,
            0u,
            0u
        };
        _context.Api.CmdUpdateBuffer(
            commandBuffer,
            tiles,
            0UL,
            tileHeader);

        Span<BufferMemoryBarrier2> afterReset =
            stackalloc BufferMemoryBarrier2[1];
        afterReset[0] = BarrierBuilder.BufferBarrier(
            tiles,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            0UL,
            TileHeaderBytes);
        ExecuteBufferBarriers(commandBuffer, afterReset);
    }

    private void RecordColorMipTail(
        CommandBuffer commandBuffer,
        RenderTarget source,
        DescriptorSet baseSet,
        string label)
    {
        int mipCount = _renderTargets.BloomMipCount;
        if (mipCount == 0 || baseSet.Handle == 0 ||
            _colorMipPipeline.Handle == 0)
        {
            return;
        }

        _context.BeginDebugLabel(commandBuffer, label);
        try
        {
            source.TransitionToShaderRead(commandBuffer);
            _context.Api.CmdBindPipeline(
                commandBuffer,
                PipelineBindPoint.Compute,
                _colorMipPipeline);
            for (int mip = 0; mip < mipCount; mip++)
            {
                RenderTarget input = mip == 0
                    ? source
                    : _renderTargets.BloomMipChain[mip - 1];
                RenderTarget destination =
                    _renderTargets.BloomMipChain[mip];
                DescriptorSet descriptorSet = mip == 0
                    ? baseSet
                    : _colorMipDownsampleSets[mip - 1];
                destination.TransitionToStorageWrite(commandBuffer);
                _context.Api.CmdBindDescriptorSets(
                    commandBuffer,
                    PipelineBindPoint.Compute,
                    _colorMipPipelineLayout,
                    0u,
                    1u,
                    &descriptorSet,
                    0u,
                    null);
                var push = new GPUBloomPushConstants
                {
                    SourceDimensions = new Vector2(
                        input.Extent.Width,
                        input.Extent.Height),
                    DestinationDimensions = new Vector2(
                        destination.Extent.Width,
                        destination.Extent.Height),
                    Threshold = 0.0f,
                    Knee = 0.0f,
                    Radius = 1.0f,
                    Mode = 0u
                };
                _context.Api.CmdPushConstants(
                    commandBuffer,
                    _colorMipPipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0u,
                    checked((uint)Marshal.SizeOf<GPUBloomPushConstants>()),
                    &push);
                _context.Api.CmdDispatch(
                    commandBuffer,
                    (destination.Extent.Width + WorkgroupSize - 1u) /
                        WorkgroupSize,
                    (destination.Extent.Height + WorkgroupSize - 1u) /
                        WorkgroupSize,
                    1u);
                destination.TransitionToShaderRead(commandBuffer);
            }
        }
        finally
        {
            _context.EndDebugLabel(commandBuffer);
        }
    }

    private void DispatchScreen(
        CommandBuffer commandBuffer,
        uint receiverScale = 1u)
    {
        uint scale = Math.Clamp(receiverScale, 1u, 4u);
        uint width = (_allocatedWidth + scale - 1u) / scale;
        uint height = (_allocatedHeight + scale - 1u) / scale;
        _context.Api.CmdDispatch(commandBuffer,
            (width + WorkgroupSize - 1u) / WorkgroupSize,
            (height + WorkgroupSize - 1u) / WorkgroupSize,
            1u);
    }

    private int ResolveSpatialPassCount(SceneRenderingData sceneData)
    {
        int configured = _settings.Reflections.SpatialFilterPassCount;
        return sceneData.EffectiveReflectionImplementation ==
            ReflectionImplementationMode.Adaptive
                ? Math.Min(configured, 2)
                : configured;
    }

    private static uint ScaleAdmissionThreshold(
        uint threshold,
        float scale)
    {
        if (threshold == 0u)
            return 0u;
        double sanitized = float.IsFinite(scale)
            ? Math.Clamp(scale, 0.0f, 1.0f)
            : 1.0;
        return checked((uint)Math.Clamp(
            Math.Floor(threshold * sanitized),
            1.0,
            uint.MaxValue));
    }

    private static uint PackHiZAndColorMipCounts(
        uint hiZMipCount,
        int colorMipCount) =>
        Math.Min(hiZMipCount, ushort.MaxValue) |
        (checked((uint)Math.Clamp(
            colorMipCount,
            0,
            ushort.MaxValue)) << 16);

    private void Push<T>(CommandBuffer commandBuffer, T push)
        where T : unmanaged
    {
        uint size = checked((uint)Marshal.SizeOf<T>());
        _context.Api.CmdPushConstants(commandBuffer, _pipelineLayout,
            ShaderStageFlags.ComputeBit, 0u, size, &push);
    }

    private void CreateLocalSetLayout()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[18];
        for (uint binding = 0u; binding < 18u; binding++)
        {
            DescriptorType type = binding switch
            {
                0u or 11u or 12u => DescriptorType.CombinedImageSampler,
                13u or 14u or 15u or 17u =>
                    DescriptorType.StorageBuffer,
                _ => DescriptorType.StorageImage
            };
            bindings[binding] = new DescriptorSetLayoutBinding
            {
                Binding = binding,
                DescriptorType = type,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 18u,
            PBindings = bindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device, &info, null, out _localSetLayout);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create hybrid reflection descriptor layout",
                result);
    }

    private void CreateColorMipSetLayout()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0u,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 2u,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1u,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1u,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2u,
            PBindings = bindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device, &info, null, out _colorMipSetLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create hybrid reflection color-mip layout",
                result);
        }
    }

    private void CreateEmptyRaySetLayoutIfNeeded()
    {
        if (_raySceneDescriptors.Layout.Handle != 0)
            return;
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device, &info, null, out _emptyRaySetLayout);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create hybrid reflection empty ray layout",
                result);
    }

    private void CreatePipelineCache()
    {
        if (_pipelineCacheService is not null)
        {
            _pipelineCache = _pipelineCacheService.Cache;
            return;
        }
        var info = new PipelineCacheCreateInfo
        {
            SType = StructureType.PipelineCacheCreateInfo
        };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device, &info, null, out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create hybrid reflection pipeline cache", result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout rayLayout =
            _raySceneDescriptors.Layout.Handle != 0
                ? _raySceneDescriptors.Layout
                : _emptyRaySetLayout;
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[4]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout,
            rayLayout,
            _localSetLayout
        };
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = HybridReflectionGpuContract.MaximumPushConstantBytes
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 4u,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &pushRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device, &info, null, out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create hybrid reflection pipeline layout", result);
    }

    private void CreateColorMipPipelineLayout()
    {
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = checked((uint)Marshal.SizeOf<GPUBloomPushConstants>())
        };
        DescriptorSetLayout layout = _colorMipSetLayout;
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1u,
            PSetLayouts = &layout,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &pushRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device, &info, null, out _colorMipPipelineLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create hybrid reflection color-mip pipeline layout",
                result);
        }
    }

    private VkPipeline CreatePipeline(string shaderName) =>
        CreatePipeline(shaderName, _pipelineLayout);

    private VkPipeline CreatePipeline(
        string shaderName,
        PipelineLayout pipelineLayout)
    {
        ShaderModule module = default;
        try
        {
            module = ShaderModuleLoader.Load(_context, shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)_entryPointName
            };
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = pipelineLayout,
                BasePipelineIndex = -1
            };
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId(
                        $"HybridReflection:{shaderName}"),
                    &info,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1u,
                    &info,
                    null,
                    out pipeline);
            if (result != Result.Success)
                throw new VulkanException(
                    $"Failed to create {shaderName}", result);
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline,
                shaderName);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(
                    _context.Device, module, null);
        }
    }

    private void TryCreateRayPipeline()
    {
        if (!_context.RayQuerySupported ||
            !_accelerationStructures.Supported ||
            !_raySceneDescriptors.IsAvailable)
        {
            RayPipelineAvailable = false;
            return;
        }
        try
        {
            _rayPipeline = CreatePipeline(
                "hybrid_reflection_ray_query.comp.spv");
            RayPipelineAvailable = true;
        }
        catch (Exception exception)
        {
            RayPipelineAvailable = false;
            FailureDetail = "hybrid reflection ray pipeline unavailable: " +
                exception.Message;
        }
    }

    private void CreateDescriptorPoolAndSets()
    {
        var sizes = stackalloc DescriptorPoolSize[3]
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = 6u
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = 22u
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = 8u
            }
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3u,
            PPoolSizes = sizes,
            MaxSets = RenderingConstants.FramesInFlight
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device, &poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create hybrid reflection descriptor pool", result);

        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[
            RenderingConstants.FramesInFlight];
        for (int index = 0; index < RenderingConstants.FramesInFlight; index++)
            layouts[index] = _localSetLayout;
        fixed (DescriptorSet* sets = _descriptorSets)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = RenderingConstants.FramesInFlight,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device, &allocInfo, sets);
        }
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to allocate hybrid reflection descriptor sets", result);
    }

    private void CreateColorMipDescriptorSets()
    {
        int mipCount = _renderTargets.BloomMipCount;
        if (mipCount == 0)
            return;
        int baseSetCount = 1 + RenderingConstants.FramesInFlight;
        int downsampleCount = Math.Max(0, mipCount - 1);
        int setCount = baseSetCount + downsampleCount;
        var sizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = checked((uint)(setCount * 2))
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = checked((uint)setCount)
            }
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2u,
            PPoolSizes = sizes,
            MaxSets = checked((uint)setCount)
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _colorMipDescriptorPool);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create hybrid reflection color-mip descriptor pool",
                result);
        }

        var layouts = new DescriptorSetLayout[setCount];
        var sets = new DescriptorSet[setCount];
        Array.Fill(layouts, _colorMipSetLayout);
        fixed (DescriptorSetLayout* layoutPointer = layouts)
        fixed (DescriptorSet* setPointer = sets)
        {
            var allocation = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _colorMipDescriptorPool,
                DescriptorSetCount = checked((uint)setCount),
                PSetLayouts = layoutPointer
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocation,
                setPointer);
        }
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to allocate hybrid reflection color-mip descriptor sets",
                result);
        }

        int index = 0;
        _opaqueColorMipBaseSet = sets[index++];
        for (int bank = 0;
             bank < RenderingConstants.FramesInFlight;
             bank++)
        {
            _transparentColorMipBaseSets[bank] = sets[index++];
        }
        _colorMipDownsampleSets = sets[index..];
        WriteColorMipDescriptorSet(
            _opaqueColorMipBaseSet,
            _renderTargets.SceneColor.View,
            _renderTargets.BloomMipChain[0].View);
        for (int bank = 0;
             bank < RenderingConstants.FramesInFlight;
             bank++)
        {
            WriteColorMipDescriptorSet(
                _transparentColorMipBaseSets[bank],
                HistoryTarget(1 - bank).View,
                _renderTargets.BloomMipChain[0].View);
        }
        for (int mip = 1; mip < mipCount; mip++)
        {
            WriteColorMipDescriptorSet(
                _colorMipDownsampleSets[mip - 1],
                _renderTargets.BloomMipChain[mip - 1].View,
                _renderTargets.BloomMipChain[mip].View);
        }
    }

    private void WriteColorMipDescriptorSet(
        DescriptorSet set,
        ImageView source,
        ImageView destination)
    {
        var sourceInfos = stackalloc DescriptorImageInfo[2];
        for (int index = 0; index < 2; index++)
        {
            sourceInfos[index] = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.ScreenSampler,
                ImageView = source,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
        }
        var destinationInfo = new DescriptorImageInfo
        {
            ImageView = destination,
            ImageLayout = ImageLayout.General
        };
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0u,
            DescriptorCount = 2u,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = sourceInfos
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1u,
            DescriptorCount = 1u,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &destinationInfo
        };
        _context.Api.UpdateDescriptorSets(
            _context.Device, 2u, writes, 0u, null);
    }

    private void WriteDescriptorSets()
    {
        var imageInfos = stackalloc DescriptorImageInfo[14];
        var bufferInfos = stackalloc DescriptorBufferInfo[4];
        var writes = stackalloc WriteDescriptorSet[18];
        for (int bank = 0; bank < RenderingConstants.FramesInFlight; bank++)
        {
            RenderTarget[] imageTargets =
            [
                Required(_renderTargets.HybridReflectionReceiverPayload,
                    "receiver payload"),
                Required(_renderTargets.HybridReflectionRawRadiance,
                    "raw radiance"),
                CurrentMetadataTarget(raw: true, bank),
                HistoryTarget(1 - bank),
                HistoryTarget(bank),
                MomentsTarget(1 - bank),
                MomentsTarget(bank),
                MetadataTarget(1 - bank),
                MetadataTarget(bank),
                HistoryTarget(1 - bank),
                _renderTargets.SceneColor,
                _renderTargets.MotionVectors,
                _renderTargets.SceneDepth
            ];
            for (int binding = 0; binding < imageTargets.Length; binding++)
            {
                bool combined = binding is 0 or 11 or 12;
                imageInfos[binding] = new DescriptorImageInfo
                {
                    Sampler = combined
                        ? binding == 11
                            ? _bindlessHeap.ScreenSampler
                            : _bindlessHeap.HiZSampler
                        : default,
                    ImageView = imageTargets[binding].View,
                    ImageLayout = binding == 0 || binding == 11
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : binding == 12
                            ? ImageLayout.DepthStencilReadOnlyOptimal
                            : ImageLayout.General
                };
            }
            bufferInfos[0] = new DescriptorBufferInfo
            {
                Buffer = _bufferManager.GetBuffer(_taskBuffers[bank]),
                Range = _bufferManager.GetBufferSize(_taskBuffers[bank])
            };
            bufferInfos[1] = new DescriptorBufferInfo
            {
                Buffer = _bufferManager.GetBuffer(_counterBuffers[bank]),
                Range = CounterBytes
            };
            bufferInfos[2] = new DescriptorBufferInfo
            {
                Buffer = _bufferManager.GetBuffer(_indirectBuffers[bank]),
                Range = IndirectBytes
            };
            bufferInfos[3] = new DescriptorBufferInfo
            {
                Buffer = _bufferManager.GetBuffer(_tileBuffers[bank]),
                Range = _bufferManager.GetBufferSize(_tileBuffers[bank])
            };
            for (uint binding = 0u; binding < 13u; binding++)
            {
                writes[binding] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _descriptorSets[bank],
                    DstBinding = binding,
                    DescriptorCount = 1u,
                    DescriptorType = binding is 0u or 11u or 12u
                        ? DescriptorType.CombinedImageSampler
                        : DescriptorType.StorageImage,
                    PImageInfo = &imageInfos[binding]
                };
            }
            for (uint binding = 13u; binding < 16u; binding++)
            {
                writes[binding] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _descriptorSets[bank],
                    DstBinding = binding,
                    DescriptorCount = 1u,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfos[binding - 13u]
                };
            }
            imageInfos[13] = new DescriptorImageInfo
            {
                ImageView = Required(
                    _renderTargets.HybridReflectionDdgiCohorts,
                    "DDGI cohorts").View,
                ImageLayout = ImageLayout.General
            };
            writes[16] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[bank],
                DstBinding = 16u,
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &imageInfos[13]
            };
            writes[17] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSets[bank],
                DstBinding = 17u,
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufferInfos[3]
            };
            _context.Api.UpdateDescriptorSets(
                _context.Device, 18u, writes, 0u, null);
        }
    }

    private void ValidatePushConstants()
    {
        int maximum = Math.Max(
            Marshal.SizeOf<GPUHybridReflectionSsrPushConstants>(),
            Math.Max(
                Marshal.SizeOf<GPUHybridReflectionRayPushConstants>(),
                Marshal.SizeOf<GPUHybridReflectionResolvePushConstants>()));
        maximum = Math.Max(maximum,
            Marshal.SizeOf<GPUHybridReflectionTemporalPushConstants>());
        maximum = Math.Max(maximum,
            Marshal.SizeOf<GPUHybridReflectionSpatialPushConstants>());
        maximum = Math.Max(maximum,
            Marshal.SizeOf<GPUHybridReflectionCompositePushConstants>());
        maximum = Math.Max(maximum,
            Marshal.SizeOf<GPUHybridReflectionDdgiPushConstants>());
        maximum = Math.Max(maximum,
            Marshal.SizeOf<GPUHybridReflectionClassifyPushConstants>());
        if (maximum > HybridReflectionGpuContract.MaximumPushConstantBytes)
        {
            throw new InvalidOperationException(
                $"Hybrid reflection push constants require {maximum} bytes.");
        }
    }

    private ulong ComputeDescriptorSignature()
    {
        ulong hash = 1469598103934665603UL;
        void Mix(ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        Mix(Required(_renderTargets.HybridReflectionReceiverPayload,
            "receiver payload").View.Handle);
        Mix(Required(_renderTargets.HybridReflectionRawRadiance,
            "raw radiance").View.Handle);
        Mix(Required(_renderTargets.HybridReflectionRawMetadata,
            "raw metadata").View.Handle);
        Mix(HistoryTarget(0).View.Handle);
        Mix(HistoryTarget(1).View.Handle);
        Mix(MomentsTarget(0).View.Handle);
        Mix(MomentsTarget(1).View.Handle);
        Mix(MetadataTarget(0).View.Handle);
        Mix(MetadataTarget(1).View.Handle);
        Mix(Required(_renderTargets.HybridReflectionDdgiCohorts,
            "DDGI cohorts").View.Handle);
        Mix(_renderTargets.SceneColor.View.Handle);
        Mix(_renderTargets.MotionVectors.View.Handle);
        Mix(_renderTargets.SceneDepth.View.Handle);
        Mix(checked((ulong)_renderTargets.BloomMipCount));
        foreach (RenderTarget target in _renderTargets.BloomMipChain)
            Mix(target.View.Handle);
        return hash;
    }

    private bool AllBuffersValid()
    {
        for (int index = 0; index < RenderingConstants.FramesInFlight; index++)
        {
            if (!_taskBuffers[index].IsValid ||
                !_counterBuffers[index].IsValid ||
                !_counterReadbackBuffers[index].IsValid ||
                !_indirectBuffers[index].IsValid ||
                !_tileBuffers[index].IsValid)
            {
                return false;
            }
        }
        return true;
    }

    private RenderTarget HistoryTarget(int bank) => bank == 0
        ? Required(_renderTargets.HybridReflectionHistory0, "history 0")
        : Required(_renderTargets.HybridReflectionHistory1, "history 1");

    private RenderTarget MomentsTarget(int bank) => bank == 0
        ? Required(_renderTargets.HybridReflectionMoments0, "moments 0")
        : Required(_renderTargets.HybridReflectionMoments1, "moments 1");

    private RenderTarget MetadataTarget(int bank) => bank == 0
        ? Required(_renderTargets.HybridReflectionHistoryMetadata0,
            "history metadata 0")
        : Required(_renderTargets.HybridReflectionHistoryMetadata1,
            "history metadata 1");

    private RenderTarget CurrentMetadataTarget(bool raw, int bank) => raw
        ? Required(_renderTargets.HybridReflectionRawMetadata, "raw metadata")
        : MetadataTarget(bank);

    private static RenderTarget Required(RenderTarget? target, string role) =>
        target ?? throw new InvalidOperationException(
            $"Hybrid reflection {role} target is unavailable.");

    private static BufferHandle GetFrameBuffer(
        BufferHandle[] buffers,
        int frameIndex) => buffers[ValidateFrameIndex(frameIndex)];

    private static int ValidateFrameIndex(int frameIndex)
    {
        if ((uint)frameIndex >= RenderingConstants.FramesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return frameIndex;
    }

    private void PublishComputeWrites(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.ShaderSampledReadBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.DrawIndirectBit |
                PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.ShaderSampledReadBit |
                AccessFlags2.IndirectCommandReadBit |
                AccessFlags2.ColorAttachmentReadBit |
                AccessFlags2.ColorAttachmentWriteBit
        };
        ExecuteMemoryBarrier(commandBuffer, barrier);
    }

    private void SynchronizePreviousHybridFrame(CommandBuffer commandBuffer)
    {
        // Hybrid history and scratch images are shared by the frame-in-flight
        // banks. Make both the prior frame's readers and writers complete before
        // this frame transitions or overwrites any of those resources. This is
        // especially important for the history read -> next-frame write WAR
        // dependency, which a write-only source scope does not express.
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.ShaderSampledReadBit |
                AccessFlags2.ColorAttachmentReadBit |
                AccessFlags2.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.TransferBit |
                PipelineStageFlags2.DrawIndirectBit |
                PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.ShaderSampledReadBit |
                AccessFlags2.TransferWriteBit |
                AccessFlags2.IndirectCommandReadBit |
                AccessFlags2.ColorAttachmentReadBit |
                AccessFlags2.ColorAttachmentWriteBit
        };
        ExecuteMemoryBarrier(commandBuffer, barrier);
    }

    private void PublishTransferWrites(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.IndirectCommandReadBit
        };
        ExecuteMemoryBarrier(commandBuffer, barrier);
    }

    private void RecordCounterReadback(
        CommandBuffer commandBuffer,
        int bank)
    {
        var countersToTransfer = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.TransferBit,
            DstAccessMask = AccessFlags2.TransferReadBit
        };
        ExecuteMemoryBarrier(commandBuffer, countersToTransfer);

        VkBuffer source = _bufferManager.GetBuffer(_counterBuffers[bank]);
        VkBuffer destination = _bufferManager.GetBuffer(
            _counterReadbackBuffers[bank]);
        var copy = new BufferCopy
        {
            SrcOffset = 0UL,
            DstOffset = 0UL,
            Size = CounterBytes
        };
        _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1u,
            &copy);

        var transferToHost = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit
        };
        ExecuteMemoryBarrier(commandBuffer, transferToHost);
    }

    private void ExecuteMemoryBarrier(
        CommandBuffer commandBuffer,
        MemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void ExecuteBufferBarriers(
        CommandBuffer commandBuffer,
        ReadOnlySpan<BufferMemoryBarrier2> barriers)
    {
        fixed (BufferMemoryBarrier2* barrierPointer = barriers)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = checked((uint)barriers.Length),
                PBufferMemoryBarriers = barrierPointer
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }
    }

    private void DestroyDescriptorPool()
    {
        Array.Clear(_descriptorSets);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(
                _context.Device, _descriptorPool, null);
        _descriptorPool = default;
        if (_colorMipDescriptorPool.Handle != 0)
        {
            _context.Api.DestroyDescriptorPool(
                _context.Device,
                _colorMipDescriptorPool,
                null);
        }
        _colorMipDescriptorPool = default;
        _opaqueColorMipBaseSet = default;
        Array.Clear(_transparentColorMipBaseSets);
        _colorMipDownsampleSets = Array.Empty<DescriptorSet>();
    }

    private void DestroyBuffers()
    {
        DestroyBufferArray(_taskBuffers);
        DestroyBufferArray(_counterBuffers);
        DestroyBufferArray(_counterReadbackBuffers);
        DestroyBufferArray(_indirectBuffers);
        DestroyBufferArray(_tileBuffers);
        Array.Fill(_counterFrameSubmitted, false);
        Array.Fill(_completedCounters, HybridReflectionCounterSnapshot.Empty);
    }

    private void DestroyBufferArray(BufferHandle[] buffers)
    {
        for (int index = 0; index < buffers.Length; index++)
        {
            if (buffers[index].IsValid)
                _bufferManager.DestroyBuffer(buffers[index]);
            buffers[index] = BufferHandle.Invalid;
        }
    }

    private void CleanupNative()
    {
        DestroyDescriptorPool();
        DestroyBuffers();
        DestroyPipeline(ref _ssrPipeline);
        DestroyPipeline(ref _classifyPipeline);
        DestroyPipeline(ref _rayPipeline);
        DestroyPipeline(ref _ddgiCohortPipeline);
        DestroyPipeline(ref _ddgiReconstructPipeline);
        DestroyPipeline(ref _ddgiExactMissPipeline);
        DestroyPipeline(ref _resolvePipeline);
        DestroyPipeline(ref _temporalPipeline);
        DestroyPipeline(ref _spatialPipeline);
        DestroyPipeline(ref _compositePipeline);
        DestroyPipeline(ref _opaqueSceneColorSnapshotPipeline);
        DestroyPipeline(ref _colorMipPipeline);
        if (_colorMipPipelineLayout.Handle != 0)
        {
            _context.Api.DestroyPipelineLayout(
                _context.Device,
                _colorMipPipelineLayout,
                null);
        }
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(
                _context.Device, _pipelineLayout, null);
        if (_pipelineCacheService is null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(
                _context.Device, _pipelineCache, null);
        if (_emptyRaySetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device, _emptyRaySetLayout, null);
        if (_localSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device, _localSetLayout, null);
        if (_colorMipSetLayout.Handle != 0)
        {
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device,
                _colorMipSetLayout,
                null);
        }
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipelineLayout = default;
        _colorMipPipelineLayout = default;
        _pipelineCache = default;
        _emptyRaySetLayout = default;
        _localSetLayout = default;
        _colorMipSetLayout = default;
    }

    private void DestroyPipeline(ref VkPipeline pipeline)
    {
        if (pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        pipeline = default;
    }

    private void ThrowIfDisposingLocked()
    {
        if (_disposed || _disposeRequested)
        {
            throw new ObjectDisposedException(
                nameof(HybridReflectionVulkanRuntime));
        }
    }

    public void Dispose()
    {
        Task? initialization = null;
        Task? publication = null;
        lock (_initializationGate)
        {
            if (_disposed || _disposeRequested)
                return;
            _disposeRequested = true;
            if (_initializationState == 1)
            {
                if (_backgroundInitializationStarted)
                {
                    initialization = _initializationCompletion?.Task;
                }
                else
                {
                    // A deferred scene was never presented. Complete the
                    // unstarted claim so shutdown cannot wait on work that was
                    // intentionally never queued.
                    _initializationState = 2;
                    _initializationCompletion?.TrySetResult(true);
                }
            }
            if (_backgroundPublicationStarted)
            {
                publication = _publicationCompletion?.Task;
            }
            else if (_publicationDeferred)
            {
                // Publication was withdrawn but its optional preparation was
                // never queued. Complete the dormant claim for shutdown.
                _publicationDeferred = false;
                _publicationCompletion?.TrySetResult(true);
            }
        }

        // Native pipeline creation must finish before the device-owned cache,
        // layouts, and buffers are destroyed. This wait is shutdown-only; the
        // render host never waits for background transition preparation.
        initialization?.GetAwaiter().GetResult();
        publication?.GetAwaiter().GetResult();

        lock (_initializationGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        ScreenPipelinesAvailable = false;
        RayPipelineAvailable = false;
        CleanupNative();
        GC.SuppressFinalize(this);
    }
}
