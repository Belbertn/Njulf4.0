using System;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
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
/// Records the production froxel pipeline as one render-graph node. All
/// private resources are graphics-queue serialized; explicit compute barriers
/// define the twelve internal stages without expanding the public graph ABI.
/// </summary>
internal sealed unsafe class FroxelFogRenderer : IDisposable
{
    private const ulong L2RecordBytes = 64UL;
    private const int SourceCullPipelineIndex = 1;
    private const int SourceDispatchPipelineIndex = 12;
    private static readonly ulong DiagnosticBytes = checked(
        (ulong)Marshal.SizeOf<GPUVolumetricFogDiagnostics>());
    private const string EntryPoint = "main";

    private static readonly string[] ShaderNames =
    [
        "froxel_noise.comp.spv",
        "froxel_source_cull.comp.spv",
        "froxel_medium.comp.spv",
        "froxel_transmittance.comp.spv",
        "froxel_ddgi_bounce_l2.comp.spv",
        "froxel_lighting.comp.spv",
        "froxel_indirect.comp.spv",
        "froxel_multiple_scatter.comp.spv",
        "froxel_temporal.comp.spv",
        "froxel_integrate.comp.spv",
        "froxel_resolve.comp.spv",
        "froxel_composite.comp.spv",
        "froxel_source_dispatch.comp.spv"
    ];
    private static readonly string[] StageTimingNames =
    [
        "Fog.FroxelNoise",
        "Fog.SourceCull",
        "Fog.Medium",
        "Fog.Transmittance",
        "Fog.DdgiBounce",
        "Fog.DirectLightingCache",
        "Fog.IndirectLightingCache",
        "Fog.MultipleScattering",
        "Fog.Temporal",
        "Fog.Integrate",
        "Fog.Resolve",
        "Fog.Composite",
        "Fog.SourceDispatch"
    ];
    // Fine-grained bottom-of-pipe timestamp writes can serialize this many compute dispatches on
    // some Vulkan drivers. Keep the scopes available for focused diagnostics, while RenderDoc
    // debug labels remain active in every build without perturbing normal GPU timing.
    private static readonly bool StageGpuTimestampsEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("NJULF_FOG_STAGE_GPU_TIMING"),
            "1",
            StringComparison.Ordinal);

    private readonly VulkanContext _context;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferManager _bufferManager;
    private readonly RenderTargetManager _renderTargets;
    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager? _ddgi;
    private readonly RaySceneDescriptorBank _raySceneDescriptors;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly BufferHandle[] _frameBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _volumeBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _clusterCountBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _clusterReferenceBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _diagnosticBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly BufferHandle[] _diagnosticReadbackBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly bool[] _diagnosticReadbackRecorded =
        new bool[RenderingConstants.FramesInFlight];
    private readonly GPUVolumetricFogDiagnostics[] _completedDiagnostics =
        new GPUVolumetricFogDiagnostics[RenderingConstants.FramesInFlight];
    private readonly bool[] _completedDiagnosticsValid =
        new bool[RenderingConstants.FramesInFlight];
    private readonly DescriptorSet[] _descriptorSets =
        new DescriptorSet[RenderingConstants.FramesInFlight];
    private readonly VkPipeline[] _pipelines = new VkPipeline[ShaderNames.Length];
    private readonly nint _entryPointName;

    private VolumetricFogResources? _resources;
    private VolumetricFogGridLayout _layout;
    private VolumetricFogQualityProfile _profile;
    private BufferHandle _bounceRadianceBuffer = BufferHandle.Invalid;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private Sampler _noiseSampler;
    private Sampler _linearClampSampler;
    private Matrix4x4 _previousViewProjection = Matrix4x4.Identity;
    private Vector3 _previousCameraPosition;
    private ulong _previousCameraCutSerial = ulong.MaxValue;
    private ulong _allocatedBytes;
    private int _bounceProbeCapacity;
    private uint _previousDdgiOwnershipGeneration;
    private bool _historyValid;
    private bool _noiseInitialized;
    private bool _sidecarCleared;
    private bool _outputFailureLatched;
    private string _outputFailureStatus = string.Empty;
    private bool _initialized;
    private bool _disposed;
    private string _initializationStatus = "froxel-pipeline-not-initialized";
    private GpuTimestampRecorder? _activeTimestamps;
    private int _activeTimestampFrameIndex;
    private bool _activeTimestampComputeQueue;

    internal FroxelFogRenderer(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        BufferManager bufferManager,
        RenderTargetManager renderTargets,
        RenderSettings settings,
        SimpleDdgiVolumeManager? ddgi,
        RaySceneDescriptorBank raySceneDescriptors,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ddgi = ddgi;
        _raySceneDescriptors = raySceneDescriptors ??
            throw new ArgumentNullException(nameof(raySceneDescriptors));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
    }

    internal bool IsActive { get; private set; }
    internal bool DirectionalL2Active => IsActive && _ddgi is
        { DirectionalRadianceMode: SimpleDdgiDirectionalRadianceMode.L2 };

    internal static bool RequiresDdgiSidecarReset(
        uint previousPhysicalOwnershipGeneration,
        uint currentPhysicalOwnershipGeneration) =>
        previousPhysicalOwnershipGeneration !=
        currentPhysicalOwnershipGeneration;

    internal void Initialize()
    {
        if (!_context.RayQuerySupported || !_raySceneDescriptors.IsAvailable)
        {
            _initializationStatus =
                "froxel-ray-query-descriptor-capability-missing";
            return;
        }

        try
        {
            CreateDescriptorSetLayout();
            CreatePipelineCache();
            CreatePipelineLayout();
            CreateNoiseSampler();
            CreateLinearClampSampler();
            for (int i = 0; i < ShaderNames.Length; i++)
                _pipelines[i] = CreatePipeline(ShaderNames[i]);
            _initialized = true;
            _initializationStatus = "ready";
        }
        catch (Exception exception)
        {
            DestroyPipelineResources();
            _initialized = false;
            _initializationStatus =
                "froxel-pipeline-initialization-failed:" +
                exception.GetType().Name;
        }
    }

    internal bool TryExecute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        GpuTimestampRecorder? timestamps = null,
        bool timestampComputeQueue = false)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        FogSettings fog = _settings.Fog;
        VolumetricFogQualityProfile profile =
            VolumetricFogQualityProfile.ForPreset(_settings.QualityPreset);
        VolumetricFogGridLayout layout = VolumetricFogGridLayout.Create(
            _renderTargets.FoggedSceneColor.Extent.Width,
            _renderTargets.FoggedSceneColor.Extent.Height,
            sceneData.CaptureCameraNearPlane,
            sceneData.CaptureCameraFarPlane,
            fog.Volumetric.MaxDistance,
            profile);
        ulong sidecarBytes = checked(
            (ulong)GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount *
            L2RecordBytes);
        ulong plannedBytes = layout.FroxelCount == 0
            ? 0UL
            : checked(VolumetricFogMemoryPlan.Create(layout, profile).TotalBytes +
                sidecarBytes);
        ulong budgetBytes = VolumetricBudgetBytes(_settings.QualityPreset);
        var capabilities = new VolumetricFogCapabilities(
            LayeredStorageImages: true,
            RequiredFormats: true,
            RayQueryEmissiveVisibility: _context.RayQuerySupported &&
                _initialized && _raySceneDescriptors.IsAvailable,
            QualifiedProfile: fog.Volumetric.SingleScatteringQualified);
        VolumetricFogAdmission admission = VolumetricFogAdmission.Evaluate(
            fog.Enabled && fog.Mode != FogMode.Disabled,
            fog.Technique,
            _settings.QualityPreset,
            capabilities,
            plannedBytes,
            budgetBytes);
        sceneData.FogRequestedTechnique = fog.Technique;
        sceneData.FogEffectiveTechnique = admission.Effective;
        sceneData.VolumetricFogStatus = admission.Status;
        IsActive = false;
        if (!admission.Active)
        {
            _historyValid = false;
            if (!_initialized && fog.Technique != FogTechnique.Analytic &&
                profile.Enabled)
            {
                sceneData.VolumetricFogStatus = _initializationStatus;
            }
            return false;
        }

        if (_outputFailureLatched)
            return FallBackToAnalytic(sceneData, _outputFailureStatus);

        if (_ddgi is null || _ddgi.PhysicalProbeCapacity <= 0 ||
            _ddgi.DirectionalRadianceMode != SimpleDdgiDirectionalRadianceMode.L2)
        {
            _historyValid = false;
            sceneData.FogEffectiveTechnique = FogTechnique.Analytic;
            sceneData.VolumetricFogStatus = "froxel-ddgi-l2-sidecar-unavailable";
            return false;
        }
        if (!_raySceneDescriptors.TryUpdate(frameIndex,
                out string raySceneFailure))
        {
            _historyValid = false;
            sceneData.FogEffectiveTechnique = FogTechnique.Analytic;
            sceneData.VolumetricFogStatus =
                "froxel-ray-scene-unavailable:" + raySceneFailure;
            return false;
        }

        try
        {
            EnsureResources(layout, profile, budgetBytes);
        }
        catch (Exception exception) when (exception is VulkanException or
            BufferAllocationException or InvalidOperationException)
        {
            ReleaseResources();
            sceneData.FogEffectiveTechnique = FogTechnique.Analytic;
            sceneData.VolumetricFogStatus =
                "froxel-resource-admission-failed:" +
                exception.GetType().Name + ":" + exception.Message;
            return false;
        }

        ReadCompletedDiagnostics(frameIndex);
        VolumetricFogOutputEvidence completedEvidence =
            ApplyCompletedDiagnostics(frameIndex, sceneData);
        if (completedEvidence.RequiresAnalyticFallback)
        {
            _outputFailureLatched = true;
            _outputFailureStatus = completedEvidence.NonFiniteCount > 0
                ? "froxel-non-finite-output-fallback-analytic"
                : "froxel-diagnostic-contract-empty-fallback-analytic";
            return FallBackToAnalytic(sceneData, _outputFailureStatus);
        }

        UploadFrameData(frameIndex, sceneData, fog, layout, profile);
        int localVolumeCount = UploadLocalVolumes(frameIndex, sceneData, profile);
        bool cameraCut = !_historyValid ||
            _previousCameraCutSerial != sceneData.CaptureCameraCutSerial;
        uint flags = 0u;
        if (_historyValid)
            flags |= 1u << 0;
        if (cameraCut)
            flags |= 1u << 1;
        flags |= 1u << 2;

        int multipleScatteringIterations = fog.Volumetric.MultipleScatteringQualified
            ? Math.Min(
                fog.Volumetric.MultipleScatteringIterations,
                checked((int)profile.MultipleScatteringIterations))
            : 0;
        if (multipleScatteringIterations > 0)
            flags |= 1u << 3;

        if (RequiresDdgiSidecarReset(
                _previousDdgiOwnershipGeneration,
                _ddgi.PhysicalOwnershipGeneration))
        {
            _sidecarCleared = false;
            _previousDdgiOwnershipGeneration = _ddgi.PhysicalOwnershipGeneration;
        }

        Record(commandBuffer, frameIndex, multipleScatteringIterations,
            flags, timestamps,
            timestampComputeQueue);

        _previousViewProjection = sceneData.ViewProjectionMatrix;
        _previousCameraPosition = sceneData.CameraPosition;
        _previousCameraCutSerial = sceneData.CaptureCameraCutSerial;
        _historyValid = true;
        IsActive = true;
        sceneData.FogEffectiveTechnique = FogTechnique.Froxel;
        sceneData.VolumetricFogStatus = sceneData.VolumetricFogOutputReadbackValid
            ? sceneData.VolumetricFogOutputProduced
                ? "active-output-verified"
                : "active-output-empty"
            : "active-output-pending";
        sceneData.VolumetricFogGridWidth = layout.Width;
        sceneData.VolumetricFogGridHeight = layout.Height;
        sceneData.VolumetricFogGridDepth = layout.Depth;
        sceneData.VolumetricFogClusterCount = checked((uint)layout.ClusterCount);
        sceneData.VolumetricFogAllocatedBytes = _allocatedBytes;
        sceneData.VolumetricFogLocalVolumeCount = localVolumeCount;
        ResolveParticleSourceCounts(sceneData, profile,
            out uint cpuParticleCount, out uint gpuParticleCapacity);
        uint gpuParticleCandidates = Math.Min(
            sceneData.GpuParticleRenderedCount,
            gpuParticleCapacity);
        sceneData.VolumetricFogParticleCandidateCount = checked((int)(
            cpuParticleCount + gpuParticleCandidates));
        int cpuEligible = Math.Min(
            Math.Max(sceneData.CpuVolumetricParticleSourceCount, 0),
            checked((int)cpuParticleCount));
        sceneData.VolumetricFogParticleAdmittedCount =
            sceneData.VolumetricFogOutputReadbackValid
                ? Math.Max(
                    SaturatingInt(_completedDiagnostics[frameIndex]
                        .AdmittedSourceCount) - localVolumeCount,
                    0)
                : cpuEligible;
        sceneData.VolumetricFogParticleSourceCount =
            sceneData.VolumetricFogParticleAdmittedCount;
        sceneData.VolumetricFogMultipleScatteringIterations =
            multipleScatteringIterations;
        sceneData.VolumetricFogHistoryValid = !cameraCut;
        sceneData.VolumetricFogHistoryRejected = cameraCut;
        sceneData.VolumetricFogDirectionalL2Active = true;
        sceneData.VolumetricFogEnergyOwnershipSeparated = true;
        return true;
    }

    internal void OnSwapchainRecreated()
    {
        ReleaseResources();
        _historyValid = false;
        _noiseInitialized = false;
    }

    internal void InvalidateHistory()
    {
        _historyValid = false;
        IsActive = false;
    }

    private void EnsureResources(
        VolumetricFogGridLayout layout,
        VolumetricFogQualityProfile profile,
        ulong budgetBytes)
    {
        // DDGI residency can resize its current physical page population as
        // refinement bricks enter or leave. This sidecar is indexed by a
        // physical address, so reserve the ABI maximum once; tying the large
        // froxel allocation shape to the live page count would destroy valid
        // temporal history whenever paging changes by even one probe.
        int bounceProbeCapacity =
            GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount;
        if (_resources is not null &&
            HasSameAllocationShape(_layout, layout) &&
            _profile == profile &&
            _bounceProbeCapacity == bounceProbeCapacity)
        {
            _layout = layout;
            return;
        }
        ReleaseResources();
        _layout = layout;
        _profile = profile;
        _bounceProbeCapacity = bounceProbeCapacity;
        _resources = new VolumetricFogResources(_context, layout, profile);
        ulong frameBytes = checked((ulong)Marshal.SizeOf<GPUVolumetricFogFrameData>());
        ulong volumeBytes = checked(Math.Max(1UL,
            (ulong)profile.MaximumLocalVolumes *
            (ulong)Marshal.SizeOf<GPUVolumetricDensityVolume>()));
        ulong countBytes = FroxelSourceDispatchLayout.BufferByteSize(
            layout.ClusterCount);
        ulong referenceBytes = checked(layout.ClusterCount *
            profile.ClusterReferenceCapacity * sizeof(uint));
        for (int frame = 0; frame < RenderingConstants.FramesInFlight; frame++)
        {
            _frameBuffers[frame] = CreateMappedBuffer(frameBytes,
                $"Froxel Frame Data {frame}");
            _volumeBuffers[frame] = CreateMappedBuffer(volumeBytes,
                $"Froxel Local Volumes {frame}");
            _clusterCountBuffers[frame] = CreateDeviceBuffer(countBytes,
                $"Froxel Cluster Counts {frame}", indirect: true);
            _clusterReferenceBuffers[frame] = CreateDeviceBuffer(referenceBytes,
                $"Froxel Cluster References {frame}");
            _diagnosticBuffers[frame] = CreateDeviceBuffer(DiagnosticBytes,
                $"Froxel Diagnostics {frame}", transferSource: true);
            _diagnosticReadbackBuffers[frame] = _bufferManager.CreateBuffer(
                DiagnosticBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit |
                    AllocationCreateFlags.HostAccessRandomBit,
                $"Froxel Diagnostics Readback {frame}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
        }
        ulong bounceBytes = checked(
            (ulong)_bounceProbeCapacity * L2RecordBytes);
        _bounceRadianceBuffer = CreateDeviceBuffer(bounceBytes,
            "Froxel Bounce-only DDGI L2 Sidecar");

        ulong imageAllocationBytes = _resources.AllocationByteSize;
        ulong frameAllocationBytes = 0UL;
        ulong volumeAllocationBytes = 0UL;
        ulong clusterCountAllocationBytes = 0UL;
        ulong clusterReferenceAllocationBytes = 0UL;
        ulong diagnosticAllocationBytes = 0UL;
        ulong diagnosticReadbackAllocationBytes = 0UL;
        foreach (BufferHandle handle in _frameBuffers)
            frameAllocationBytes = checked(frameAllocationBytes +
                _bufferManager.GetBufferAllocationSize(handle));
        foreach (BufferHandle handle in _volumeBuffers)
            volumeAllocationBytes = checked(volumeAllocationBytes +
                _bufferManager.GetBufferAllocationSize(handle));
        foreach (BufferHandle handle in _clusterCountBuffers)
            clusterCountAllocationBytes = checked(clusterCountAllocationBytes +
                _bufferManager.GetBufferAllocationSize(handle));
        foreach (BufferHandle handle in _clusterReferenceBuffers)
            clusterReferenceAllocationBytes = checked(
                clusterReferenceAllocationBytes +
                _bufferManager.GetBufferAllocationSize(handle));
        foreach (BufferHandle handle in _diagnosticBuffers)
            diagnosticAllocationBytes = checked(diagnosticAllocationBytes +
                _bufferManager.GetBufferAllocationSize(handle));
        foreach (BufferHandle handle in _diagnosticReadbackBuffers)
            diagnosticReadbackAllocationBytes = checked(
                diagnosticReadbackAllocationBytes +
                _bufferManager.GetBufferAllocationSize(handle));
        ulong bounceAllocationBytes =
            _bufferManager.GetBufferAllocationSize(_bounceRadianceBuffer);
        ulong actual = checked(
            imageAllocationBytes + frameAllocationBytes +
            volumeAllocationBytes + clusterCountAllocationBytes +
            clusterReferenceAllocationBytes + diagnosticAllocationBytes +
            diagnosticReadbackAllocationBytes + bounceAllocationBytes);
        if (actual > budgetBytes)
        {
            string imageBreakdown = string.Join(",",
                _resources.Images.Select(image =>
                    $"{image.Name}={image.AllocationByteSize}"));
            throw new InvalidOperationException(
                $"Froxel allocation {actual} bytes exceeds its dedicated " +
                $"budget of {budgetBytes} bytes " +
                $"(images={imageAllocationBytes}, frame={frameAllocationBytes}, " +
                $"volumes={volumeAllocationBytes}, " +
                $"clusterCounts={clusterCountAllocationBytes}, " +
                $"clusterReferences={clusterReferenceAllocationBytes}, " +
                $"diagnostics={diagnosticAllocationBytes}, " +
                $"readback={diagnosticReadbackAllocationBytes}, " +
                $"bounce={bounceAllocationBytes}; {imageBreakdown}).");
        }
        _allocatedBytes = actual;

        _bindlessHeap.RegisterStorageBuffer(
            BindlessIndex.VolumetricFogBounceRadianceBuffer,
            _bufferManager.GetBuffer(_bounceRadianceBuffer),
            0UL,
            bounceBytes);
        CreateDescriptorPoolAndSets();
        WriteDescriptorSets();
        _historyValid = false;
        _noiseInitialized = false;
        _sidecarCleared = false;
    }

    private BufferHandle CreateMappedBuffer(ulong bytes, string name) =>
        _bufferManager.CreateBuffer(
            bytes,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.AutoPreferHost,
            AllocationCreateFlags.MappedBit |
                AllocationCreateFlags.HostAccessSequentialWriteBit,
            name,
            MemoryBudgetCategory.RenderTargets);

    private BufferHandle CreateDeviceBuffer(
        ulong bytes,
        string name,
        bool transferSource = false,
        bool indirect = false) =>
        _bufferManager.CreateBuffer(
            bytes,
            BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                (transferSource ? BufferUsageFlags.TransferSrcBit : 0) |
                (indirect ? BufferUsageFlags.IndirectBufferBit : 0),
            MemoryUsage.AutoPreferDevice,
            default,
            name,
            MemoryBudgetCategory.RenderTargets);

    private void UploadFrameData(
        int frameIndex,
        SceneRenderingData sceneData,
        FogSettings fog,
        VolumetricFogGridLayout layout,
        VolumetricFogQualityProfile profile)
    {
        float deltaTime = sceneData.GpuParticleDeltaSeconds > 0.0f
            ? sceneData.GpuParticleDeltaSeconds
            : 1.0f / 60.0f;
        bool cameraCut = !_historyValid ||
            _previousCameraCutSerial != sceneData.CaptureCameraCutSerial;
        uint samplePhase = (sceneData.TemporalSampleIndex & 15u) + 1u;
        float sampleX = RadicalInverse(samplePhase, 2u);
        float sampleY = RadicalInverse(samplePhase, 3u);
        float sampleZ = RadicalInverse(samplePhase, 5u);
        int localVolumeCount = Math.Min(sceneData.VolumetricDensityVolumes.Length,
            checked((int)profile.MaximumLocalVolumes));
        ResolveParticleSourceCounts(sceneData, profile,
            out uint cpuParticleCount, out uint gpuParticleCapacity);
        Vector3 sunDirection = sceneData.FogDirectionalInscatteringDirection.LengthSquared() >
            0.000001f
                ? sceneData.FogDirectionalInscatteringDirection.Normalized()
                : new Vector3(-0.35f, -0.75f, -0.55f).Normalized();
        // The bounded approximation is a post-qualification extension of the
        // single-scattering path. Explicit Froxel remains useful for bringing
        // up an unqualified profile, but cannot silently enable this stage.
        int multipleIterations = fog.Volumetric.MultipleScatteringQualified
            ? Math.Min(
                fog.Volumetric.MultipleScatteringIterations,
                checked((int)profile.MultipleScatteringIterations))
            : 0;
        var data = new GPUVolumetricFogFrameData
        {
            ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            PreviousViewProjectionMatrix = cameraCut
                ? sceneData.ViewProjectionMatrix
                : _previousViewProjection,
            CameraPositionAndTime = new Vector4(sceneData.CameraPosition, sceneData.Time),
            PreviousCameraPositionAndDeltaTime = new Vector4(
                cameraCut ? sceneData.CameraPosition : _previousCameraPosition,
                deltaTime),
            ScreenDimensions = new Vector4(
                _renderTargets.FoggedSceneColor.Extent.Width,
                _renderTargets.FoggedSceneColor.Extent.Height,
                1.0f / _renderTargets.FoggedSceneColor.Extent.Width,
                1.0f / _renderTargets.FoggedSceneColor.Extent.Height),
            GridDimensions = new Vector4(
                layout.Width, layout.Height, layout.Depth, layout.PixelSize),
            SourceClusterDimensions = new Vector4(
                layout.ClusterWidth, layout.ClusterHeight, layout.ClusterDepth,
                profile.ClusterReferenceCapacity),
            LightingGridDimensions = new Vector4(
                layout.LightingWidth,
                layout.LightingHeight,
                layout.LightingDepth,
                profile.ResolveDivisor),
            SourceClusterCellDimensions = new Vector4(
                profile.ClusterWidth,
                profile.ClusterHeight,
                profile.ClusterDepth,
                0.0f),
            LightingCellDimensions = new Vector4(
                profile.LightingWidth,
                profile.LightingHeight,
                profile.LightingDepth,
                0.0f),
            DepthParameters = new Vector4(
                layout.NearDistance, layout.FarDistance,
                MathF.Log(layout.FarDistance / layout.NearDistance),
                layout.GuardBandPixels),
            GlobalExtinction = new Vector4(
                fog.Volumetric.BaseExtinctionPerMeter,
                fog.Volumetric.HeightExtinctionPerMeter,
                fog.Volumetric.Height,
                fog.Volumetric.HeightFalloff),
            GlobalScatteringAlbedoAndAnisotropy = new Vector4(
                fog.Volumetric.ScatteringAlbedo,
                fog.Volumetric.Anisotropy),
            WindAndNoiseScale = new Vector4(
                fog.Volumetric.GlobalWind,
                fog.Volumetric.NoiseScale),
            NoiseSelfShadowAndHistory = new Vector4(
                fog.Volumetric.NoiseStrength,
                fog.Volumetric.NoiseContrast,
                fog.Volumetric.SelfShadowDistance,
                Math.Min(fog.Volumetric.TemporalHistoryWeight,
                    profile.MaximumHistoryWeight)),
            TemporalSampleAndReset = new Vector4(
                sampleX, sampleY, sampleZ, cameraCut ? 1.0f : 0.0f),
            CountsAndDebug = new Vector4(
                localVolumeCount,
                cpuParticleCount,
                gpuParticleCapacity,
                (uint)fog.DebugView),
            MultipleScattering = new Vector4(
                multipleIterations,
                fog.Volumetric.MultipleScatteringEnergyLimit,
                (uint)fog.Volumetric.DebugProjection,
                Math.Max(_ddgi?.ProbesToUpdate ?? 0, 0)),
            LightCounts = new Vector4(
                Math.Min(Math.Max(sceneData.LightCount, 0),
                    checked((int)profile.MaximumLightsPerCluster)),
                Math.Max(sceneData.DirectionalLightCount, 0),
                Math.Max(sceneData.LocalLightCount, 0),
                Math.Min(Math.Max(sceneData.DdgiEmissiveSourceCount, 0),
                    checked((int)profile.EmissiveCandidatesPerCluster))),
            FogColorAndOpacity = new Vector4(fog.Color, fog.MaxOpacity),
            GridProjection = new Vector4(
                MathF.Abs(sceneData.ProjectionMatrix.M11) * 0.5f,
                MathF.Abs(sceneData.ProjectionMatrix.M22) * 0.5f,
                fog.StartDistance,
                fog.Volumetric.DebugSlice),
            SunDirectionAndFlags = new Vector4(sunDirection, (uint)fog.Mode)
        };
        void* mapped = _bufferManager.GetMappedPointer(_frameBuffers[frameIndex]);
        if (mapped is null)
            throw new InvalidOperationException("Froxel frame data is not mapped.");
        *(GPUVolumetricFogFrameData*)mapped = data;
        _bufferManager.FlushBuffer(_frameBuffers[frameIndex], 0UL,
            checked((ulong)Marshal.SizeOf<GPUVolumetricFogFrameData>()));
    }

    private int UploadLocalVolumes(
        int frameIndex,
        SceneRenderingData sceneData,
        VolumetricFogQualityProfile profile)
    {
        int count = Math.Min(sceneData.VolumetricDensityVolumes.Length,
            checked((int)profile.MaximumLocalVolumes));
        if (count == 0)
            return 0;
        void* mapped = _bufferManager.GetMappedPointer(_volumeBuffers[frameIndex]);
        if (mapped is null)
            throw new InvalidOperationException("Froxel local-volume data is not mapped.");
        var destination = new Span<GPUVolumetricDensityVolume>(mapped, count);
        for (int index = 0; index < count; index++)
            destination[index] = PackVolume(sceneData.VolumetricDensityVolumes[index]);
        ulong bytes = checked((ulong)count *
            (ulong)Marshal.SizeOf<GPUVolumetricDensityVolume>());
        _bufferManager.FlushBuffer(_volumeBuffers[frameIndex], 0UL, bytes);
        return count;
    }

    private static GPUVolumetricDensityVolume PackVolume(
        VolumetricDensityVolume volume)
    {
        Span<byte> identity = stackalloc byte[16];
        volume.Id.TryWriteBytes(identity);
        return new GPUVolumetricDensityVolume
        {
            PositionAndShape = new Vector4(volume.Position, (uint)volume.Shape),
            Rotation = new Vector4(
                volume.Rotation.X, volume.Rotation.Y,
                volume.Rotation.Z, volume.Rotation.W),
            BoxExtentsAndRadius = new Vector4(volume.BoxExtents, volume.Radius),
            ScatteringAlbedoAndDensity = new Vector4(
                volume.ScatteringAlbedo, volume.DensityMultiplier),
            ExtinctionEdgeAnisotropyPriority = new Vector4(
                volume.ExtinctionPerMeter, volume.EdgeFade,
                volume.Anisotropy, volume.Priority),
            NoiseParameters = new Vector4(
                volume.NoiseScale, volume.NoiseStrength,
                volume.NoiseContrast, 0.0f),
            FlowVelocityAndSeed = new Vector4(
                volume.FlowVelocity,
                (float)(volume.NoiseSeed & 0x00ffffffu)),
            StableIdentityLow = BitConverter.ToUInt32(identity[..4]),
            StableIdentityHigh = BitConverter.ToUInt32(identity.Slice(4, 4)),
            Enabled = volume.Enabled ? 1u : 0u
        };
    }

    private void Record(
        CommandBuffer commandBuffer,
        int frameIndex,
        int multipleScatteringIterations,
        uint flags,
        GpuTimestampRecorder? timestamps,
        bool timestampComputeQueue)
    {
        _activeTimestamps = timestamps;
        _activeTimestampFrameIndex = frameIndex;
        _activeTimestampComputeQueue = timestampComputeQueue;
        try
        {
        VolumetricFogResources resources = _resources!;
        _renderTargets.SceneColor.TransitionToComputeShaderRead(commandBuffer);
        _renderTargets.SceneDepth.TransitionToComputeDepthReadOnly(commandBuffer);
        _renderTargets.FoggedSceneColor.TransitionToComputeStorageWrite(commandBuffer);
        foreach (VolumetricImage image in resources.Images)
        {
            image.TransitionToLayout(commandBuffer, ImageLayout.General,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit,
                force: image.Layout == ImageLayout.General);
        }

        VkBuffer clusterCounts = _bufferManager.GetBuffer(
            _clusterCountBuffers[frameIndex]);
        _context.Api.CmdFillBuffer(commandBuffer, clusterCounts, 0UL,
            _bufferManager.GetBufferSize(_clusterCountBuffers[frameIndex]), 0u);
        _context.Api.CmdFillBuffer(commandBuffer,
            _bufferManager.GetBuffer(_diagnosticBuffers[frameIndex]), 0UL,
            DiagnosticBytes, 0u);
        if (!_sidecarCleared)
        {
            _context.Api.CmdFillBuffer(commandBuffer,
                _bufferManager.GetBuffer(_bounceRadianceBuffer), 0UL,
                _bufferManager.GetBufferSize(_bounceRadianceBuffer), 0u);
            _sidecarCleared = true;
        }
        RecordTransferToComputeBarrier(commandBuffer);

        BindSets(commandBuffer, frameIndex);
        if (!_noiseInitialized)
        {
            Dispatch(commandBuffer, 0, 16u, 16u, 16u, frameIndex, flags);
            RecordComputeBarrier(commandBuffer);
            _noiseInitialized = true;
        }

        Dispatch(commandBuffer, SourceDispatchPipelineIndex,
            1u, 1u, 1u, frameIndex, flags);
        RecordSourceDispatchBarrier(commandBuffer, frameIndex);
        DispatchIndirect(commandBuffer, SourceCullPipelineIndex,
            _bufferManager.GetBuffer(_clusterCountBuffers[frameIndex]),
            FroxelSourceDispatchLayout.CommandOffsetBytes(
                _layout.ClusterCount),
            frameIndex,
            flags);
        RecordComputeBarrier(commandBuffer);

        DispatchGrid(commandBuffer, 2, 4u, 4u, 4u, frameIndex, flags);
        RecordComputeBarrier(commandBuffer);
        Dispatch(commandBuffer, 3,
            DivideRoundUp(_layout.ClusterWidth, 4u),
            DivideRoundUp(_layout.ClusterHeight, 4u),
            1u, frameIndex, flags);
        RecordComputeBarrier(commandBuffer);

        uint probeCount = checked((uint)Math.Max(_ddgi!.ProbeCount, 0));
        uint probesToUpdate = Math.Min(
            checked((uint)Math.Max(_ddgi.ProbesToUpdate, 0)),
            probeCount);
        if (probesToUpdate > 0u)
        {
            Dispatch(commandBuffer, 4, DivideRoundUp(probesToUpdate, 64u),
                1u, 1u, frameIndex, flags);
            RecordComputeBarrier(commandBuffer);
        }

        Dispatch(commandBuffer, 5,
            _layout.LightingWidth,
            _layout.LightingHeight,
            _layout.LightingDepth,
            frameIndex,
            flags);
        RecordComputeBarrier(commandBuffer);
        Dispatch(commandBuffer, 6,
            DivideRoundUp(_layout.LightingWidth, 8u),
            DivideRoundUp(_layout.LightingHeight, 8u),
            _layout.LightingDepth,
            frameIndex,
            flags);
        RecordComputeBarrier(commandBuffer);
        for (uint iteration = 0u;
             iteration < (uint)multipleScatteringIterations;
             iteration++)
        {
            Dispatch(commandBuffer, 7,
                DivideRoundUp(_layout.LightingWidth, 4u),
                DivideRoundUp(_layout.LightingHeight, 4u),
                DivideRoundUp(_layout.LightingDepth, 4u),
                frameIndex,
                flags,
                iteration);
            RecordComputeBarrier(commandBuffer);
        }
        DispatchGrid(commandBuffer, 8, 4u, 4u, 4u, frameIndex, flags);
        RecordComputeBarrier(commandBuffer);
        Dispatch(commandBuffer, 9,
            DivideRoundUp(_layout.Width, 8u),
            DivideRoundUp(_layout.Height, 8u),
            1u, frameIndex, flags);
        RecordComputeBarrier(commandBuffer);
        Dispatch(commandBuffer, 10,
            DivideRoundUp(_layout.ResolveWidth, 8u),
            DivideRoundUp(_layout.ResolveHeight, 8u),
            1u, frameIndex, flags);
        RecordComputeBarrier(commandBuffer);
        Extent2D extent = _renderTargets.FoggedSceneColor.Extent;
        Dispatch(commandBuffer, 11,
            DivideRoundUp(extent.Width, 8u),
            DivideRoundUp(extent.Height, 8u),
            1u, frameIndex, flags);
        RecordDiagnosticReadback(commandBuffer, frameIndex);
        _renderTargets.FoggedSceneColor.TransitionToComputeShaderRead(commandBuffer);
        }
        finally
        {
            _activeTimestamps = null;
            _activeTimestampFrameIndex = 0;
            _activeTimestampComputeQueue = false;
        }
    }

    private void ReadCompletedDiagnostics(int frameIndex)
    {
        if (!_diagnosticReadbackRecorded[frameIndex] ||
            !_diagnosticReadbackBuffers[frameIndex].IsValid)
        {
            _completedDiagnosticsValid[frameIndex] = false;
            return;
        }

        _bufferManager.InvalidateBuffer(
            _diagnosticReadbackBuffers[frameIndex], 0UL, DiagnosticBytes);
        void* mapped = _bufferManager.GetMappedPointer(
            _diagnosticReadbackBuffers[frameIndex]);
        if (mapped is null)
        {
            _completedDiagnosticsValid[frameIndex] = false;
            _diagnosticReadbackRecorded[frameIndex] = false;
            return;
        }

        _completedDiagnostics[frameIndex] =
            *(GPUVolumetricFogDiagnostics*)mapped;
        _completedDiagnosticsValid[frameIndex] = true;
        _diagnosticReadbackRecorded[frameIndex] = false;
    }

    private VolumetricFogOutputEvidence ApplyCompletedDiagnostics(
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!_completedDiagnosticsValid[frameIndex])
            return default;

        VolumetricFogOutputEvidence evidence =
            VolumetricFogOutputEvidence.FromGpuCounters(
                _completedDiagnostics[frameIndex],
                readbackValid: true);
        sceneData.VolumetricFogOutputReadbackValid = evidence.ReadbackValid;
        sceneData.VolumetricFogOutputProduced = evidence.Produced;
        sceneData.VolumetricFogDiagnosticSampleCount =
            evidence.DiagnosticSampleCount;
        sceneData.VolumetricFogMediumNonEmptyFroxelCount =
            evidence.MediumNonEmptyFroxelCount;
        sceneData.VolumetricFogDirectNonZeroFroxelCount =
            evidence.DirectNonZeroFroxelCount;
        sceneData.VolumetricFogIndirectNonZeroFroxelCount =
            evidence.IndirectNonZeroFroxelCount;
        sceneData.VolumetricFogDdgiSupportedFroxelCount =
            evidence.DdgiSupportedFroxelCount;
        sceneData.VolumetricFogHistoryAcceptedFroxelCount =
            evidence.HistoryAcceptedFroxelCount;
        sceneData.VolumetricFogHistoryRejectedFroxelCount =
            evidence.HistoryRejectedFroxelCount;
        sceneData.VolumetricFogHistoryRejectedInvalidFroxelCount =
            evidence.HistoryRejectedInvalidFroxelCount;
        sceneData.VolumetricFogHistoryRejectedBoundsFroxelCount =
            evidence.HistoryRejectedBoundsFroxelCount;
        sceneData.VolumetricFogHistoryRejectedExtinctionFroxelCount =
            evidence.HistoryRejectedExtinctionFroxelCount;
        sceneData.VolumetricFogHistoryRejectedRadianceFroxelCount =
            evidence.HistoryRejectedRadianceFroxelCount;
        sceneData.VolumetricFogHistoryRejectedVelocityFroxelCount =
            evidence.HistoryRejectedVelocityFroxelCount;
        sceneData.VolumetricFogClusterOverflowCount =
            evidence.ClusterOverflowCount;
        sceneData.VolumetricFogNonFiniteCount =
            evidence.NonFiniteCount;
        sceneData.VolumetricFogMaximumExtinction = evidence.MaximumExtinction;
        sceneData.VolumetricFogMeanExtinction = evidence.MeanExtinction;
        sceneData.VolumetricFogMaximumDirectLuminance =
            evidence.MaximumDirectLuminance;
        sceneData.VolumetricFogMeanDirectLuminance =
            evidence.MeanDirectLuminance;
        sceneData.VolumetricFogMaximumIndirectLuminance =
            evidence.MaximumIndirectLuminance;
        sceneData.VolumetricFogMeanIndirectLuminance =
            evidence.MeanIndirectLuminance;
        sceneData.VolumetricFogMinimumTransmittance =
            evidence.MinimumTransmittance;
        sceneData.VolumetricFogMeanTransmittance = evidence.MeanTransmittance;
        return evidence;
    }

    private bool FallBackToAnalytic(
        SceneRenderingData sceneData,
        string status)
    {
        _historyValid = false;
        IsActive = false;
        sceneData.FogEffectiveTechnique = FogTechnique.Analytic;
        sceneData.VolumetricFogStatus = status;
        return false;
    }

    private void RecordDiagnosticReadback(
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        VkBuffer source = _bufferManager.GetBuffer(
            _diagnosticBuffers[frameIndex]);
        VkBuffer destination = _bufferManager.GetBuffer(
            _diagnosticReadbackBuffers[frameIndex]);
        BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
            source,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            0UL,
            DiagnosticBytes);
        RecordBufferBarrier(commandBuffer, beforeCopy);
        var copy = new BufferCopy
        {
            SrcOffset = 0UL,
            DstOffset = 0UL,
            Size = DiagnosticBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer, source, destination, 1u, &copy);
        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            destination,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit,
            0UL,
            DiagnosticBytes);
        RecordBufferBarrier(commandBuffer, afterCopy);
        _diagnosticReadbackRecorded[frameIndex] = true;
    }

    private void RecordBufferBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private static int SaturatingInt(uint value) =>
        value > int.MaxValue ? int.MaxValue : checked((int)value);

    private void DispatchGrid(
        CommandBuffer commandBuffer,
        int pipelineIndex,
        uint localX,
        uint localY,
        uint localZ,
        int frameIndex,
        uint flags,
        uint iteration = 0u) => Dispatch(
            commandBuffer,
            pipelineIndex,
            DivideRoundUp(_layout.Width, localX),
            DivideRoundUp(_layout.Height, localY),
            DivideRoundUp(_layout.Depth, localZ),
            frameIndex,
            flags,
            iteration);

    private void Dispatch(
        CommandBuffer commandBuffer,
        int pipelineIndex,
        uint groupX,
        uint groupY,
        uint groupZ,
        int frameIndex,
        uint flags,
        uint iteration = 0u)
    {
        string timingName = StageTimingNames[pipelineIndex];
        if (pipelineIndex == 7)
            timingName += $".{iteration}";
        bool timestampStarted = StageGpuTimestampsEnabled &&
            _activeTimestamps is not null;
        if (timestampStarted)
        {
            if (_activeTimestampComputeQueue)
                _activeTimestamps!.BeginComputePass(
                    commandBuffer, _activeTimestampFrameIndex, timingName);
            else
                _activeTimestamps!.BeginPass(
                    commandBuffer, _activeTimestampFrameIndex, timingName);
        }
        _context.BeginDebugLabel(commandBuffer, timingName);
        try
        {
            _context.Api.CmdBindPipeline(commandBuffer,
                PipelineBindPoint.Compute, _pipelines[pipelineIndex]);
            var push = new GPUVolumetricFogPushConstants
            {
                FrameIndex = checked((uint)frameIndex),
                Stage = checked((uint)pipelineIndex),
                HistoryReadBank = checked((uint)(1 - frameIndex)),
                HistoryWriteBank = checked((uint)frameIndex),
                MultipleScatteringIteration = iteration,
                Flags = flags
            };
            _context.Api.CmdPushConstants(commandBuffer, _pipelineLayout,
                ShaderStageFlags.ComputeBit, 0u,
                checked((uint)Marshal.SizeOf<GPUVolumetricFogPushConstants>()),
                &push);
            _context.Api.CmdDispatch(commandBuffer, groupX, groupY, groupZ);
        }
        finally
        {
            _context.EndDebugLabel(commandBuffer);
            if (timestampStarted)
                _activeTimestamps!.EndPass(
                    commandBuffer, _activeTimestampFrameIndex);
        }
    }

    private void DispatchIndirect(
        CommandBuffer commandBuffer,
        int pipelineIndex,
        VkBuffer indirectBuffer,
        ulong indirectOffset,
        int frameIndex,
        uint flags)
    {
        string timingName = StageTimingNames[pipelineIndex];
        bool timestampStarted = StageGpuTimestampsEnabled &&
            _activeTimestamps is not null;
        if (timestampStarted)
        {
            if (_activeTimestampComputeQueue)
                _activeTimestamps!.BeginComputePass(
                    commandBuffer, _activeTimestampFrameIndex, timingName);
            else
                _activeTimestamps!.BeginPass(
                    commandBuffer, _activeTimestampFrameIndex, timingName);
        }
        _context.BeginDebugLabel(commandBuffer, timingName);
        try
        {
            _context.Api.CmdBindPipeline(commandBuffer,
                PipelineBindPoint.Compute, _pipelines[pipelineIndex]);
            var push = new GPUVolumetricFogPushConstants
            {
                FrameIndex = checked((uint)frameIndex),
                Stage = checked((uint)pipelineIndex),
                HistoryReadBank = checked((uint)(1 - frameIndex)),
                HistoryWriteBank = checked((uint)frameIndex),
                Flags = flags
            };
            _context.Api.CmdPushConstants(commandBuffer, _pipelineLayout,
                ShaderStageFlags.ComputeBit, 0u,
                checked((uint)Marshal.SizeOf<GPUVolumetricFogPushConstants>()),
                &push);
            _context.Api.CmdDispatchIndirect(
                commandBuffer, indirectBuffer, indirectOffset);
        }
        finally
        {
            _context.EndDebugLabel(commandBuffer);
            if (timestampStarted)
                _activeTimestamps!.EndPass(
                    commandBuffer, _activeTimestampFrameIndex);
        }
    }

    private void BindSets(CommandBuffer commandBuffer, int frameIndex)
    {
        DescriptorSet* sets = stackalloc DescriptorSet[2];
        sets[0] = _bindlessHeap.StorageBufferSet;
        sets[1] = _bindlessHeap.TextureSamplerSet;
        _context.Api.CmdBindDescriptorSets(commandBuffer,
            PipelineBindPoint.Compute, _pipelineLayout, 0u, 2u, sets, 0u, null);
        _raySceneDescriptors.Bind(commandBuffer,
            PipelineBindPoint.Compute, _pipelineLayout, frameIndex);
        DescriptorSet froxelSet = _descriptorSets[frameIndex];
        _context.Api.CmdBindDescriptorSets(commandBuffer,
            PipelineBindPoint.Compute, _pipelineLayout, 3u, 1u,
            &froxelSet, 0u, null);
    }

    private void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding* bindings =
            stackalloc DescriptorSetLayoutBinding[28];
        for (uint binding = 0u; binding < 28u; binding++)
        {
            DescriptorType type = binding switch
            {
                >= 1u and <= 4u => DescriptorType.StorageBuffer,
                20u => DescriptorType.StorageBuffer,
                15u or >= 21u => DescriptorType.CombinedImageSampler,
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
            BindingCount = 28u,
            PBindings = bindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device, &info, null, out _descriptorSetLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create froxel descriptor layout.", result);
        _context.SetDebugName(_descriptorSetLayout.Handle,
            ObjectType.DescriptorSetLayout, "Froxel Fog Descriptor Set Layout");
    }

    private void CreateDescriptorPoolAndSets()
    {
        DestroyDescriptorPool();
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[3];
        sizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = 30u
        };
        sizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = 10u
        };
        sizes[2] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 16u
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
            throw new VulkanException("Failed to create froxel descriptor pool.", result);
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[
            RenderingConstants.FramesInFlight];
        for (int i = 0; i < RenderingConstants.FramesInFlight; i++)
            layouts[i] = _descriptorSetLayout;
        fixed (DescriptorSet* sets = _descriptorSets)
        {
            var allocation = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = RenderingConstants.FramesInFlight,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device, &allocation, sets);
        }
        if (result != Result.Success)
            throw new VulkanException("Failed to allocate froxel descriptor sets.", result);
    }

    private void WriteDescriptorSets()
    {
        VolumetricFogResources resources = _resources!;
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[23];
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[5];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[28];
        for (int frame = 0; frame < RenderingConstants.FramesInFlight; frame++)
        {
            int image = 0;
            int buffer = 0;

            images[image] = Storage(_renderTargets.FoggedSceneColor.View);
            writes[0] = ImageWrite(_descriptorSets[frame], 0u,
                DescriptorType.StorageImage, &images[image++]);
            BufferHandle[] handles =
            [
                _frameBuffers[frame],
                _volumeBuffers[frame],
                _clusterCountBuffers[frame],
                _clusterReferenceBuffers[frame]
            ];
            for (uint binding = 1u; binding <= 4u; binding++)
            {
                BufferHandle handle = handles[binding - 1u];
                buffers[buffer] = new DescriptorBufferInfo
                {
                    Buffer = _bufferManager.GetBuffer(handle),
                    Offset = 0UL,
                    Range = _bufferManager.GetBufferSize(handle)
                };
                writes[binding] = BufferWrite(_descriptorSets[frame], binding,
                    &buffers[buffer++]);
            }

            VolumetricImage historyRead = resources.PreviousHistory(frame);
            VolumetricImage historyWrite = resources.CurrentHistory(frame);
            VolumetricImage confidenceRead =
                resources.PreviousHistoryConfidence(frame);
            VolumetricImage confidenceWrite =
                resources.CurrentHistoryConfidence(frame);
            VolumetricImage multiple0 = resources.MultipleScattering0 ??
                resources.DirectRadiance;
            VolumetricImage multiple1 = resources.MultipleScattering1 ??
                resources.IndirectRadiance;
            VolumetricImage[] storageImages =
            [
                resources.MediumCoefficients,
                resources.MediumAuxiliary,
                resources.DirectRadiance,
                resources.IndirectRadiance,
                historyRead,
                historyWrite,
                resources.ResolvedHalf,
                resources.LightingMedium,
                resources.CoarseTransmittance,
                resources.Noise
            ];
            for (uint binding = 5u; binding <= 14u; binding++)
            {
                images[image] = Storage(storageImages[binding - 5u].MipViews[0]);
                writes[binding] = ImageWrite(_descriptorSets[frame], binding,
                    DescriptorType.StorageImage, &images[image++]);
            }
            images[image] = new DescriptorImageInfo
            {
                Sampler = _noiseSampler,
                ImageView = resources.Noise.FullView,
                ImageLayout = ImageLayout.General
            };
            writes[15] = ImageWrite(_descriptorSets[frame], 15u,
                DescriptorType.CombinedImageSampler, &images[image++]);
            VolumetricImage[] tailImages =
            [
                multiple0,
                multiple1,
                confidenceRead,
                confidenceWrite
            ];
            for (uint binding = 16u; binding <= 19u; binding++)
            {
                images[image] = Storage(tailImages[binding - 16u].MipViews[0]);
                writes[binding] = ImageWrite(_descriptorSets[frame], binding,
                    DescriptorType.StorageImage, &images[image++]);
            }
            BufferHandle diagnosticHandle = _diagnosticBuffers[frame];
            buffers[buffer] = new DescriptorBufferInfo
            {
                Buffer = _bufferManager.GetBuffer(diagnosticHandle),
                Offset = 0UL,
                Range = _bufferManager.GetBufferSize(diagnosticHandle)
            };
            writes[20] = BufferWrite(_descriptorSets[frame], 20u,
                &buffers[buffer]);
            VolumetricImage[] sampledImages =
            [
                resources.DirectRadiance,
                resources.IndirectRadiance,
                multiple0,
                multiple1,
                resources.MediumCoefficients
            ];
            for (uint binding = 21u; binding <= 25u; binding++)
            {
                images[image] = new DescriptorImageInfo
                {
                    Sampler = _linearClampSampler,
                    ImageView = sampledImages[binding - 21u].FullView,
                    ImageLayout = ImageLayout.General
                };
                writes[binding] = ImageWrite(
                    _descriptorSets[frame],
                    binding,
                    DescriptorType.CombinedImageSampler,
                    &images[image++]);
            }
            VolumetricImage[] sampledHistoryImages =
            [
                historyRead,
                confidenceRead
            ];
            for (uint binding = 26u; binding <= 27u; binding++)
            {
                images[image] = new DescriptorImageInfo
                {
                    Sampler = _linearClampSampler,
                    ImageView = sampledHistoryImages[binding - 26u].FullView,
                    ImageLayout = ImageLayout.General
                };
                writes[binding] = ImageWrite(
                    _descriptorSets[frame],
                    binding,
                    DescriptorType.CombinedImageSampler,
                    &images[image++]);
            }
            _context.Api.UpdateDescriptorSets(_context.Device, 28u,
                writes, 0u, null);
        }
    }

    private static DescriptorImageInfo Storage(ImageView view) => new()
    {
        ImageView = view,
        ImageLayout = ImageLayout.General
    };

    private static WriteDescriptorSet ImageWrite(
        DescriptorSet set,
        uint binding,
        DescriptorType type,
        DescriptorImageInfo* image) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1u,
        DescriptorType = type,
        PImageInfo = image
    };

    private static WriteDescriptorSet BufferWrite(
        DescriptorSet set,
        uint binding,
        DescriptorBufferInfo* buffer) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1u,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = buffer
    };

    private void CreatePipelineCache()
    {
        if (_pipelineCacheService != null)
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
            throw new VulkanException("Failed to create froxel pipeline cache.", result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[4];
        layouts[0] = _bindlessHeap.StorageBufferSetLayout;
        layouts[1] = _bindlessHeap.TextureSamplerSetLayout;
        layouts[2] = _raySceneDescriptors.Layout;
        layouts[3] = _descriptorSetLayout;
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = checked((uint)Marshal.SizeOf<GPUVolumetricFogPushConstants>())
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 4u,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &range
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device, &info, null, out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create froxel pipeline layout.", result);
    }

    private VkPipeline CreatePipeline(string shaderName)
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
                Layout = _pipelineLayout,
                BasePipelineIndex = -1
            };
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId(
                        $"Fog.Froxel:{shaderName}"),
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
                    $"Failed to create froxel pipeline '{shaderName}'.", result);
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline,
                "Froxel " + shaderName);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

    private void CreateNoiseSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MinLod = 0.0f,
            MaxLod = 0.0f,
            MaxAnisotropy = 1.0f
        };
        Result result = _context.Api.CreateSampler(
            _context.Device, &info, null, out _noiseSampler);
        if (result != Result.Success)
            throw new VulkanException("Failed to create froxel noise sampler.", result);
    }

    private void CreateLinearClampSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0.0f,
            MaxLod = 0.0f,
            MaxAnisotropy = 1.0f
        };
        Result result = _context.Api.CreateSampler(
            _context.Device, &info, null, out _linearClampSampler);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create froxel linear clamp sampler.", result);
    }

    private void RecordTransferToComputeBarrier(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void RecordComputeBarrier(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void RecordSourceDispatchBarrier(
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(_clusterCountBuffers[frameIndex]),
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.IndirectCommandReadBit,
            FroxelSourceDispatchLayout.CommandOffsetBytes(
                _layout.ClusterCount),
            FroxelSourceDispatchLayout.DispatchCommandByteSize);
        RecordBufferBarrier(commandBuffer, barrier);
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);

    private static bool HasSameAllocationShape(
        VolumetricFogGridLayout left,
        VolumetricFogGridLayout right) =>
        left.Width == right.Width &&
        left.Height == right.Height &&
        left.Depth == right.Depth &&
        left.ClusterWidth == right.ClusterWidth &&
        left.ClusterHeight == right.ClusterHeight &&
        left.ClusterDepth == right.ClusterDepth &&
        left.LightingWidth == right.LightingWidth &&
        left.LightingHeight == right.LightingHeight &&
        left.LightingDepth == right.LightingDepth &&
        left.ResolveWidth == right.ResolveWidth &&
        left.ResolveHeight == right.ResolveHeight;

    private static float RadicalInverse(uint index, uint radix)
    {
        float inverse = 1.0f / radix;
        float factor = inverse;
        float result = 0.0f;
        while (index > 0u)
        {
            result += (index % radix) * factor;
            index /= radix;
            factor *= inverse;
        }
        return result;
    }

    private static void ResolveParticleSourceCounts(
        SceneRenderingData sceneData,
        VolumetricFogQualityProfile profile,
        out uint cpuParticleCount,
        out uint gpuParticleCapacity)
    {
        uint budget = profile.MaximumParticleSources;
        cpuParticleCount = Math.Min(
            checked((uint)Math.Max(sceneData.RenderedParticleCount, 0)),
            budget);
        uint remaining = budget - cpuParticleCount;
        gpuParticleCapacity = Math.Min(
            checked((uint)Math.Max(sceneData.GpuParticleCapacity, 0)),
            remaining);
    }

    private static ulong VolumetricBudgetBytes(RenderQualityPreset preset) =>
        preset switch
        {
            RenderQualityPreset.Ultra =>
                RenderBudgetProfile.Ultra4k60.VolumetricFogMemoryBudgetBytes,
            RenderQualityPreset.High or RenderQualityPreset.DdgiHigh =>
                RenderBudgetProfile.HighSpec1440p60.VolumetricFogMemoryBudgetBytes,
            _ => 0UL
        };

    private void ReleaseResources()
    {
        DestroyDescriptorPool();
        _resources?.Dispose();
        _resources = null;
        DestroyBuffers(_frameBuffers);
        DestroyBuffers(_volumeBuffers);
        DestroyBuffers(_clusterCountBuffers);
        DestroyBuffers(_clusterReferenceBuffers);
        DestroyBuffers(_diagnosticBuffers);
        DestroyBuffers(_diagnosticReadbackBuffers);
        Array.Clear(_diagnosticReadbackRecorded);
        Array.Clear(_completedDiagnostics);
        Array.Clear(_completedDiagnosticsValid);
        if (_bounceRadianceBuffer.IsValid)
        {
            _bufferManager.DestroyBuffer(_bounceRadianceBuffer);
            _bounceRadianceBuffer = BufferHandle.Invalid;
        }
        _allocatedBytes = 0UL;
        _bounceProbeCapacity = 0;
        _layout = default;
        _profile = default;
        _historyValid = false;
        _noiseInitialized = false;
        _sidecarCleared = false;
        _outputFailureLatched = false;
        _outputFailureStatus = string.Empty;
        _previousDdgiOwnershipGeneration = 0u;
        IsActive = false;
    }

    private void DestroyBuffers(BufferHandle[] handles)
    {
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i].IsValid)
                _bufferManager.DestroyBuffer(handles[i]);
            handles[i] = BufferHandle.Invalid;
        }
    }

    private void DestroyDescriptorPool()
    {
        if (_descriptorPool.Handle != 0)
        {
            _context.Api.DestroyDescriptorPool(
                _context.Device, _descriptorPool, null);
            _descriptorPool = default;
            Array.Clear(_descriptorSets);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FroxelFogRenderer));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseResources();
        DestroyPipelineResources();
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);
    }

    private void DestroyPipelineResources()
    {
        for (int i = 0; i < _pipelines.Length; i++)
        {
            if (_pipelines[i].Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _pipelines[i], null);
            _pipelines[i] = default;
        }
        if (_noiseSampler.Handle != 0)
            _context.Api.DestroySampler(_context.Device, _noiseSampler, null);
        if (_linearClampSampler.Handle != 0)
            _context.Api.DestroySampler(
                _context.Device, _linearClampSampler, null);
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_descriptorSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device, _descriptorSetLayout, null);
        _noiseSampler = default;
        _linearClampSampler = default;
        _pipelineLayout = default;
        _pipelineCache = default;
        _descriptorSetLayout = default;
        _initialized = false;
    }
}
