using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using System.Collections.Generic;

namespace Njulf.Rendering.Data
{
    public sealed record TextureAssetMemoryEntry(
        string SourcePath,
        uint Width,
        uint Height,
        uint MipLevels,
        ulong EstimatedBytes,
        bool WasDownscaled)
    {
        public string SourceKind { get; init; } = string.Empty;
        public uint OriginalWidth { get; init; } = Width;
        public uint OriginalHeight { get; init; } = Height;
        public int EncodedByteLength { get; init; }
        public string Format { get; init; } = string.Empty;
        public bool IsCompressed { get; init; }
    }

    public sealed record DdgiVolumeDiagnosticsEntry(
        int VolumeIndex,
        DdgiProbeVolumeKind Kind,
        int CascadeIndex,
        int FirstProbeIndex,
        int ProbeCount,
        int RaysPerProbe,
        int MaxProbeUpdatesPerFrame,
        int ScheduledProbeUpdates,
        ulong ScheduledPrimaryRayCount,
        float MaxRayDistance)
    {
        public int LocalSlotIndex { get; init; } = -1;
        public int LocalSlotGeneration { get; init; }
        public int StreamingCellId { get; init; }
        public int QualityClass { get; init; }
        public int PhysicalProbeCapacity { get; init; }
    }

    public enum SceneSubmissionMode
    {
        Cpu,
        CpuFallback,
        GpuCompactedDirect,
        GpuCompactedIndirect
    }

    public sealed record RendererDiagnostics(
        int VisibleObjectCount,
        int VisibleMeshletCount,
        int OpaqueObjectCount,
        int MaskedObjectCount,
        int TransparentObjectCount,
        int OpaqueMeshletCount,
        int TransparentMeshletCount,
        int SubmittedOpaqueMeshlets,
        int FrustumCulledMeshletsGpu,
        int OcclusionCulledMeshlets,
        int ForwardMeshletCandidates,
        int ForwardMeshletVisibleAfterOcclusion,
        int BlendMaterialCount,
        ulong UploadedBytes,
        int LightCount,
        uint TileCountX,
        uint TileCountY,
        int MaterialCount,
        int TextureCount,
        int LoadedFileTextureCount,
        int MipmapFallbackCount,
        int DownscaledTextureCount,
        uint MaxLoadedTextureDimension,
        ulong EstimatedTextureBytes,
        string LoadedModelName,
        int ModelRenderObjectCount,
        int RegisteredMeshCount,
        int LoadedMaterialCount,
        int LoadedTextureCount,
        int DefaultWhiteSubstitutions,
        int DefaultNormalSubstitutions,
        int DefaultBlackSubstitutions,
        long CpuSceneBuildMicroseconds,
        long GpuDepthPrePassMicroseconds,
        long GpuHiZBuildMicroseconds,
        long GpuForwardOpaqueMicroseconds,
        long GpuTransparentMicroseconds,
        int SceneUploadCount,
        int SceneUploadSkipped,
        int ObjectCandidatesCpu,
        int ObjectFrustumCulledCpu,
        int MeshletCandidatesCpu,
        int MeshletFrustumCulledCpu,
        int MeshletLodSkippedCpu,
        int MeshletLod0SubmittedCpu,
        int MeshletLod1SubmittedCpu,
        int MeshletLod2SubmittedCpu,
        long CpuPayloadSignatureMicroseconds,
        long CpuObjectCullMicroseconds,
        long CpuMeshletCullMicroseconds,
        long CpuUploadMicroseconds,
        long CpuMaterialUploadMicroseconds,
        long CpuTotalDrawSceneMicroseconds,
        long CpuDirectionalShadowRecordMicroseconds,
        long CpuSpotShadowRecordMicroseconds,
        long CpuPointShadowRecordMicroseconds,
        long CpuDepthPrePassRecordMicroseconds,
        long CpuHiZBuildRecordMicroseconds,
        long CpuLightCullRecordMicroseconds,
        long CpuForwardOpaqueRecordMicroseconds,
        long CpuTransparentRecordMicroseconds,
        long CpuBloomExtractRecordMicroseconds,
        long CpuBloomDownsampleRecordMicroseconds,
        long CpuBloomUpsampleRecordMicroseconds,
        long CpuFogRecordMicroseconds,
        long CpuCompositeRecordMicroseconds,
        long GpuLightCullMicroseconds,
        int DepthTaskInvocations,
        int DepthFrustumCulledMeshletsGpu,
        int DepthEmittedMeshletsGpu,
        int ForwardTaskInvocations,
        int ForwardFrustumCulledMeshletsGpu,
        int ForwardOcclusionTestedMeshletsGpu,
        int ForwardEmittedMeshletsGpu,
        int MeshletCountTotal,
        int MeshletCountSubmittedCpu,
        float AvgTrianglesPerSubmittedMeshlet,
        float AvgVerticesPerSubmittedMeshlet,
        int SmallMeshletsUnder16Triangles,
        int SmallMeshletsUnder32Triangles,
        int ScenePayloadRebuilt,
        ulong ObjectUploadBytes,
        ulong InstanceUploadBytes,
        ulong MeshletDrawUploadBytes,
        ulong TransparentMeshletDrawUploadBytes,
        ulong MaterialUploadBytes,
        ulong LightUploadBytes,
        int DepthPrePassEnabled,
        int HiZEnabled,
        int OcclusionEnabled,
        uint HiZMipCount,
        uint HiZWidth,
        uint HiZHeight,
        int DirectionalShadowsEnabled,
        uint DirectionalShadowMapSize,
        int DirectionalShadowCascadeCount,
        int ShadowedDirectionalLightIndex,
        ShadowDebugView ShadowDebugView,
        float ShadowNormalBias,
        float ShadowSlopeScaledDepthBias,
        int DirectionalShadowPcfRadius,
        int SpotShadowPcfRadius,
        int PointShadowPcfRadius,
        int ForwardShadowReceiverMeshletCount,
        int SpotShadowsEnabled,
        int SpotShadowCandidateCount,
        int SpotShadowSelectedCount,
        int SpotShadowRejectedByBudgetCount,
        uint SpotShadowAtlasSize,
        uint SpotShadowTileSize,
        int SpotShadowAtlasCapacity,
        int SpotShadowAtlasUsedTiles,
        int PointShadowsEnabled,
        int PointShadowCandidateCount,
        int PointShadowSelectedCount,
        int PointShadowRejectedByBudgetCount,
        uint PointShadowMapSize,
        int PointShadowRenderedFaceCount,
        int HdrEnabled,
        string SceneColorFormat,
        float Exposure,
        ToneMapper ToneMapper,
        int BloomEnabled,
        uint BloomMipCount,
        uint BloomBaseWidth,
        uint BloomBaseHeight,
        string BloomFormat,
        float BloomIntensity,
        float BloomThreshold,
        float BloomKnee,
        float BloomRadius,
        BloomDebugView BloomDebugView,
        int BloomDebugMipLevel,
        int FogEnabled,
        FogMode FogMode,
        FogColorMode FogColorMode,
        FogDebugView FogDebugView,
        float FogDensity,
        float FogStartDistance,
        float FogEndDistance,
        float FogHeight,
        float FogHeightFalloff,
        float FogHeightDensity,
        float FogMaxOpacity,
        int FogDirectionalInscatteringEnabled,
        uint FogWidth,
        uint FogHeightPixels,
        string FogFormat,
        long GpuFogMicroseconds,
        int AmbientOcclusionEnabled,
        AmbientOcclusionMode AmbientOcclusionMode,
        AmbientOcclusionDebugView AmbientOcclusionDebugView,
        AmbientOcclusionForwardSamplingMode AmbientOcclusionForwardSamplingMode,
        int AmbientOcclusionForwardDepthAwareSamples,
        uint AmbientOcclusionWidth,
        uint AmbientOcclusionHeight,
        string AmbientOcclusionFormat,
        float AmbientOcclusionResolutionScale,
        float AmbientOcclusionRadius,
        float AmbientOcclusionIntensity,
        float AmbientOcclusionBias,
        int AmbientOcclusionSampleCount,
        int AmbientOcclusionBlurRadius,
        long CpuAmbientOcclusionRecordMicroseconds,
        long CpuAmbientOcclusionBlurRecordMicroseconds,
        long GpuAmbientOcclusionMicroseconds,
        long GpuAmbientOcclusionBlurMicroseconds,
        AntiAliasingMode AntiAliasingMode,
        AntiAliasingDebugView AntiAliasingDebugView,
        uint AntiAliasingWidth,
        uint AntiAliasingHeight,
        string AntiAliasingInputFormat,
        string AntiAliasingOutputFormat,
        long CpuFxaaRecordMicroseconds,
        long CpuSmaaEdgeRecordMicroseconds,
        long CpuSmaaBlendRecordMicroseconds,
        long CpuSmaaNeighborhoodRecordMicroseconds,
        long GpuAntiAliasingMicroseconds,
        int SmaaLookupTexturesReady,
        int MotionVectorsEnabled,
        int JitterEnabled,
        float JitterX,
        float JitterY,
        int EnvironmentEnabled,
        EnvironmentSourceKind EnvironmentSourceKind,
        string EnvironmentSourcePath,
        int EnvironmentUsesFallback,
        uint EnvironmentCubemapSize,
        uint IrradianceCubemapSize,
        uint PrefilteredEnvironmentSize,
        uint PrefilteredEnvironmentMipCount,
        uint BrdfLutSize,
        float SkyIntensity,
        float DiffuseIblIntensity,
        float SpecularIblIntensity,
        EnvironmentDebugView EnvironmentDebugView,
        int EnvironmentDebugMipLevel,
        ulong EnvironmentTextureBytes,
        int ReflectionsEnabled,
        ReflectionMode ReflectionMode,
        ReflectionDebugView ReflectionDebugView,
        int ReflectionProbeCount,
        int ReflectionProbeCapacity,
        int MaxReflectionProbesPerPixel,
        uint ReflectionProbeResolution,
        uint ReflectionProbeMipCount,
        ulong ReflectionProbeEstimatedBytes,
        int ReflectionProbeCapturesQueued,
        int ReflectionProbeCapturesCompleted,
        long CpuReflectionProbeUploadMicroseconds,
        long CpuReflectionProbeCaptureRecordMicroseconds,
        long CpuReflectionProbePrefilterRecordMicroseconds,
        long GpuReflectionProbeCaptureMicroseconds,
        long GpuReflectionProbePrefilterMicroseconds)
    {
        public IReadOnlyList<TextureAssetMemoryEntry> LargestTextureAssets { get; init; } = [];
        public IReadOnlyList<MeshletQualityEntry> MeshletQualityEntries { get; init; } = [];
        public ulong StableSceneInputUploadBytes { get; init; }
        public ulong CpuCandidateListUploadBytes { get; init; }
        public int CameraDrivenCpuDrawListRebuilt { get; init; }
        public int SolidObjectCount { get; init; }
        public int GeometryDecalObjectCount { get; init; }
        public int SolidMeshletCount { get; init; }
        public int MaskedMeshletCount { get; init; }
        public int GeometryDecalMeshletCount { get; init; }
        public int ForwardSimpleMeshletCount { get; init; }
        public int ForwardFullMaterialMeshletCount { get; init; }
        public int ForwardLocalProbeMeshletCount { get; init; }
        public int MaskMaterialCount { get; init; }
        public int GeometryDecalMaterialCount { get; init; }
        public int TransparentSortCandidateCount { get; init; }
        public long TransparentSortMicroseconds { get; init; }
        public int TransparentOverflowCount { get; init; }
        public int StaticInstanceBatchCount { get; init; }
        public int StaticInstanceCount { get; init; }
        public int VisibleStaticInstanceCount { get; init; }
        public int CulledStaticInstanceCount { get; init; }
        public int StaticBatchMeshletDrawCommandCount { get; init; }
        public long CpuStaticBatchBuildMicroseconds { get; init; }
        public TransparencyMode TransparencyMode { get; init; } = TransparencyMode.SortedAlphaBlend;
        public TransparencyDebugView TransparencyDebugView { get; init; } = TransparencyDebugView.None;
        public DecalDebugView DecalDebugView { get; init; } = DecalDebugView.None;
        public int TransparentReceiveShadows { get; init; }
        public int WeightedOitEnabled { get; init; }
        public ulong WeightedOitRenderTargetBytes { get; init; }
        public int WeightedOitRenderTargetCount { get; init; }
        public int GeometryDecalsEnabled { get; init; }
        public float GeometryDecalDepthBias { get; init; }
        public float GeometryDecalSlopeScaledDepthBias { get; init; }
        public ulong SolidDepthMeshletDrawUploadBytes { get; init; }
        public ulong MaskedDepthMeshletDrawUploadBytes { get; init; }
        public ulong MaterialExtensionUploadBytes { get; init; }
        public int MaterialExtensionDataCount { get; init; }
        public MaterialDebugView MaterialDebugView { get; init; } = MaterialDebugView.None;
        public int AutoExposureEnabled { get; init; }
        public float AutoExposureAverageLuminance { get; init; }
        public float AutoExposureTargetExposure { get; init; }
        public int AutoExposureSampleCount { get; init; }
        public long CpuAutoExposureRecordMicroseconds { get; init; }
        public long GpuAutoExposureMicroseconds { get; init; }
        public int AnimationEnabled { get; init; }
        public AnimationSkinningMode AnimationSkinningMode { get; init; } = AnimationSkinningMode.Disabled;
        public AnimationDebugView AnimationDebugView { get; init; } = AnimationDebugView.None;
        public int AnimatedModelCount { get; init; }
        public int SkinnedObjectCount { get; init; }
        public int SkeletonCount { get; init; }
        public int SkinCount { get; init; }
        public int AnimationClipCount { get; init; }
        public int ActiveAnimatorCount { get; init; }
        public int PlayingAnimatorCount { get; init; }
        public int PausedAnimatorCount { get; init; }
        public int SkinnedVertexCount { get; init; }
        public int SkinningDispatchCount { get; init; }
        public int JointMatrixCount { get; init; }
        public int MaxJointsPerSkeleton { get; init; }
        public long CpuAnimationSampleMicroseconds { get; init; }
        public long CpuSkinMatrixUploadMicroseconds { get; init; }
        public long CpuSkinningRecordMicroseconds { get; init; }
        public long GpuSkinningMicroseconds { get; init; }
        public ulong SkinningUploadBytes { get; init; }
        public ulong SkinMatrixBufferSize { get; init; }
        public ulong SkinnedVertexBufferSize { get; init; }
        public string AnimatedBoundsMode { get; init; } = string.Empty;
        public int ParticlesEnabled { get; init; }
        public ParticleSimulationMode ParticleSimulationMode { get; init; } = ParticleSimulationMode.Cpu;
        public ParticleDebugView ParticleDebugView { get; init; } = ParticleDebugView.None;
        public int ParticleEffectCount { get; init; }
        public int ParticleEmitterCount { get; init; }
        public int LiveParticleCount { get; init; }
        public int SimulatedParticleCount { get; init; }
        public int CulledParticleCount { get; init; }
        public int RenderedParticleCount { get; init; }
        public int ParticleBatchCount { get; init; }
        public int ParticleDdgiSampleCount { get; init; }
        public int VfxDdgiDirtyProbeEventCount { get; init; }
        public int AlphaParticleCount { get; init; }
        public int AdditiveParticleCount { get; init; }
        public int SoftParticleCount { get; init; }
        public int FlipbookParticleCount { get; init; }
        public int TrailCount { get; init; }
        public int TrailSegmentCount { get; init; }
        public int BeamCount { get; init; }
        public int ParticleBudgetExceeded { get; init; }
        public int ParticleUploadBudgetExceeded { get; init; }
        public ulong ParticleInstanceUploadBytes { get; init; }
        public ulong TrailBeamUploadBytes { get; init; }
        public long CpuParticleSimulationMicroseconds { get; init; }
        public long CpuParticleBuildMicroseconds { get; init; }
        public long CpuParticleRecordMicroseconds { get; init; }
        public long CpuGpuParticleResetRecordMicroseconds { get; init; }
        public long CpuGpuParticleEmitterUploadMicroseconds { get; init; }
        public long CpuGpuParticleSimulateRecordMicroseconds { get; init; }
        public long CpuTrailBeamRecordMicroseconds { get; init; }
        public long GpuParticleMicroseconds { get; init; }
        public long GpuTrailBeamMicroseconds { get; init; }
        public int ParticleDrawCallCount { get; init; }
        public ulong ParticleInstanceBufferSize { get; init; }
        public ulong ParticleBatchBufferSize { get; init; }
        public ulong ParticleFrameDataBufferSize { get; init; }
        public int GpuParticlesEnabled { get; init; }
        public int GpuParticleCapacity { get; init; }
        public int GpuParticleEmitterCapacity { get; init; }
        public int GpuParticleDrawCapacity { get; init; }
        public int GpuParticleResetRequired { get; init; }
        public int GpuParticleEmitterCount { get; init; }
        public int GpuParticleMaxSpawnPerEmitter { get; init; }
        public float GpuParticleDeltaSeconds { get; init; }
        public ulong GpuParticleEmitterUploadBytes { get; init; }
        public int GpuParticleCountersReadbackValid { get; init; }
        public uint GpuParticleAliveCount { get; init; }
        public uint GpuParticleDeadCount { get; init; }
        public uint GpuParticleSpawnedCount { get; init; }
        public uint GpuParticleKilledCount { get; init; }
        public uint GpuParticleCulledCount { get; init; }
        public uint GpuParticleRenderedCount { get; init; }
        public uint GpuParticleDroppedSpawnCount { get; init; }
        public uint GpuParticleBlendBucket0Count { get; init; }
        public uint GpuParticleBlendBucket1Count { get; init; }
        public uint GpuParticleBlendBucket2Count { get; init; }
        public uint GpuParticleBlendBucket3Count { get; init; }
        public uint GpuParticleBlendBucket4Count { get; init; }
        public int FoliagePatchCount { get; init; }
        public int FoliagePrototypeCount { get; init; }
        public int FoliageClusterCount { get; init; }
        public int FoliageVisibleClusterCount { get; init; }
        public int FoliageCulledClusterCount { get; init; }
        public int FoliageVisibleMeshletDrawCount { get; init; }
        public int FoliageDdgiSampleCount { get; init; }
        public int FoliageGrassBladeEstimate { get; init; }
        public int FoliageLod0VisibleCount { get; init; }
        public int FoliageLod1VisibleCount { get; init; }
        public int FoliageLod2VisibleCount { get; init; }
        public int FoliageHiZTestedCount { get; init; }
        public int FoliageHiZRejectedCount { get; init; }
        public int FoliageOverflowCount { get; init; }
        public int FoliageMeshletDrawOverflowCount { get; init; }
        public int FoliageFarImpostorVisibleCount { get; init; }
        public bool FoliageIndirectMeshletDispatchEnabled { get; init; } = true;
        public ulong FoliageInstanceBufferBytes { get; init; }
        public ulong FoliageClusterBufferBytes { get; init; }
        public ulong FoliageDrawBufferBytes { get; init; }
        public ulong FoliageImpostorAtlasBytes { get; init; }
        public long CpuFoliageBuildMicroseconds { get; init; }
        public long CpuFoliageUploadMicroseconds { get; init; }
        public long GpuFoliageCullMicroseconds { get; init; }
        public long GpuFoliageDepthMicroseconds { get; init; }
        public long GpuFoliageForwardMicroseconds { get; init; }
        public long GpuFoliageShadowMicroseconds { get; init; }
        public ulong GpuParticleStateBufferSize { get; init; }
        public ulong GpuParticleAliveIndexBufferSize { get; init; }
        public ulong GpuParticleDeadIndexBufferSize { get; init; }
        public ulong GpuParticleEmitterBufferSize { get; init; }
        public ulong GpuParticleCurveSampleBufferSize { get; init; }
        public ulong GpuParticleCounterBufferSize { get; init; }
        public ulong GpuParticleUnsortedRenderInstanceBufferSize { get; init; }
        public ulong GpuParticleRenderInstanceBufferSize { get; init; }
        public ulong GpuParticleIndirectDrawBufferSize { get; init; }
        public ulong GpuParticleSortKeyBufferSize { get; init; }
        public int DebugToolingEnabled { get; init; }
        public int DebugOverlayEnabled { get; init; }
        public DebugOverlayMode DebugOverlayMode { get; init; } = DebugOverlayMode.None;
        public int CpuDebugSnapshotsEnabled { get; init; }
        public int DebugSelectedObjectIndex { get; init; } = -1;
        public string DebugSelectedObjectName { get; init; } = string.Empty;
        public int DebugDrawEnabled { get; init; }
        public int DebugDrawLineCount { get; init; }
        public int DebugDrawPersistentLineCount { get; init; }
        public int DebugDrawDroppedLineCount { get; init; }
        public long CpuDebugDrawBuildMicroseconds { get; init; }
        public long CpuDebugDrawRecordMicroseconds { get; init; }
        public long GpuDebugDrawMicroseconds { get; init; }
        public long CpuDebugOverlayRecordMicroseconds { get; init; }
        public long GpuDebugOverlayMicroseconds { get; init; }
        public int DebugLightTileMaxCount { get; init; }
        public float DebugLightTileAverageCount { get; init; }
        public int DebugObjectBoundsDrawn { get; init; }
        public int DebugMeshletBoundsDrawn { get; init; }
        public int DebugMeshletBoundsDropped { get; init; }
        public int DebugReflectionProbeVolumesDrawn { get; init; }
        public int DebugDdgiProbeVolumesDrawn { get; init; }
        public int DebugDecalVolumesDrawn { get; init; }
        public int GpuTimingSupported { get; init; }
        public int GpuTimingEnabled { get; init; }
        public int GpuTimingPending { get; init; }
        public int GpuTimingValid { get; init; }
        public string GpuTimingUnavailableReason { get; init; } = string.Empty;
        public int GpuTimingFrameLatency { get; init; }
        public int ForwardMeshletsSubmittedCpu { get; init; }
        public int ForwardGpuOcclusionRejectedMeshlets { get; init; }
        public int ForwardGpuOcclusionCountersReconciled { get; init; }
        public string ForwardGpuOcclusionSanity { get; init; } = string.Empty;
        public HiZVisibilityPolicyStatus HiZPolicyStatus { get; init; } = HiZVisibilityPolicyStatus.Disabled;
        public string HiZPolicyReason { get; init; } = string.Empty;
        public int HiZPolicyWarmupFramesRemaining { get; init; }
        public int HiZPolicySceneChanged { get; init; }
        public int HiZPolicyCameraCut { get; init; }
        public int HiZPolicyPyramidInvalidated { get; init; }
        public int HiZPolicyAdaptiveSuppressed { get; init; }
        public int HiZPolicyAdaptiveProbe { get; init; }
        public int HiZPolicyAdaptiveProbeCountdown { get; init; }
        public int HiZPolicyAdaptiveMeasuredOcclusionTests { get; init; }
        public int HiZPolicyAdaptiveMeasuredOcclusionCulled { get; init; }
        public float HiZPolicyAdaptiveCullRate { get; init; }
        public long HiZPolicyAdaptiveEstimatedSavedMicroseconds { get; init; }
        public long HiZPolicyAdaptiveEstimatedCostMicroseconds { get; init; }
        public long HiZPolicyAdaptiveEstimatedNetMicroseconds { get; init; }
        public int HiZPolicyAdaptiveSuppressedFrameCount { get; init; }
        public string HiZPolicyAdaptiveStatus { get; init; } = string.Empty;
        public int GpuMeshletCountersEnabled { get; init; }
        public string GpuMeshletCountersStatus { get; init; } = "GPU meshlet counters disabled.";
        public SceneSubmissionMode SceneSubmissionActiveMode { get; init; } = SceneSubmissionMode.Cpu;
        public string SceneSubmissionForwardPath { get; init; } = SceneSubmissionDiagnosticsPolicy.ForwardPathCpu;
        public string SceneSubmissionForwardTaskShader { get; init; } = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderLegacyCull;
        public int SceneSubmissionCpuCandidateCount { get; init; }
        public int SceneSubmissionGpuEmittedCount { get; init; }
        public int SceneSubmissionIndirectTaskCount { get; init; }
        public int SceneSubmissionGpuCompactionEnabled { get; init; }
        public int SceneSubmissionIndirectMeshletDispatchEnabled { get; init; }
        public int SceneSubmissionGpuLodSelectionEnabled { get; init; }
        public int SceneSubmissionGpuShadowCompactionEnabled { get; init; }
        public int SceneSubmissionValidationCompareCpuGpuLists { get; init; }
        public int SceneSubmissionGpuCompactionActive { get; init; }
        public string SceneSubmissionCompactionSkipReason { get; init; } = string.Empty;
        public string SceneSubmissionIndirectDispatchSkipReason { get; init; } = string.Empty;
        public string SceneSubmissionFallbackReason { get; init; } = string.Empty;
        public int SceneSubmissionGpuOpaqueCandidateCount { get; init; }
        public int SceneSubmissionGpuCompactedOpaqueMeshletCount { get; init; }
        public int SceneSubmissionGpuOpaqueFrustumRejectedCount { get; init; }
        public int SceneSubmissionGpuOpaqueOverflowCount { get; init; }
        public int SceneSubmissionGpuIndirectMeshletTaskCount { get; init; }
        public int SceneSubmissionGpuCompactedShadowMeshletCount { get; init; }
        public int SceneSubmissionGpuCompactedOpaqueCapacity { get; init; }
        public int SceneSubmissionGpuDepthSolidCandidateCount { get; init; }
        public int SceneSubmissionGpuDepthMaskedCandidateCount { get; init; }
        public int SceneSubmissionGpuCompactedSolidDepthMeshletCount { get; init; }
        public int SceneSubmissionGpuCompactedMaskedDepthMeshletCount { get; init; }
        public int SceneSubmissionGpuCompactedSolidDepthCapacity { get; init; }
        public int SceneSubmissionGpuCompactedMaskedDepthCapacity { get; init; }
        public int SceneSubmissionGpuDepthOverflowCount { get; init; }
        public int SceneSubmissionGpuDirectionalShadowCandidateCount { get; init; }
        public int SceneSubmissionGpuCompactedDirectionalShadowMeshletCount { get; init; }
        public int SceneSubmissionGpuDirectionalShadowOverflowCount { get; init; }
        public string SceneSubmissionGpuDirectionalShadowCascadeSummary { get; init; } = string.Empty;
        public int SceneSubmissionLocalShadowGpuCompactionJustified { get; init; }
        public int SceneSubmissionSpotShadowGpuCompactionJustified { get; init; }
        public int SceneSubmissionPointShadowGpuCompactionJustified { get; init; }
        public long SceneSubmissionLocalShadowCpuRecordMicroseconds { get; init; }
        public int SceneSubmissionSpotShadowMeshletLightTests { get; init; }
        public int SceneSubmissionPointShadowMeshletFaceTests { get; init; }
        public string SceneSubmissionLocalShadowGpuCompactionStatus { get; init; } = string.Empty;
        public string SceneSubmissionLocalShadowOverflowSummary { get; init; } = string.Empty;
        public int SceneSubmissionGpuLod0EmittedCount { get; init; }
        public int SceneSubmissionGpuLod1EmittedCount { get; init; }
        public int SceneSubmissionGpuLod2EmittedCount { get; init; }
        public int SceneSubmissionGpuMissingLodFallbackCount { get; init; }
        public int SceneSubmissionValidationValid { get; init; }
        public string SceneSubmissionValidationStatus { get; init; } = string.Empty;
        public int SceneSubmissionValidationCpuOpaqueCount { get; init; }
        public int SceneSubmissionValidationGpuOpaqueCount { get; init; }
        public int SceneSubmissionValidationComparedSampleCount { get; init; }
        public int SceneSubmissionValidationMismatchCount { get; init; }
        public int SceneSubmissionValidationSampleLimit { get; init; }
        public string SceneSubmissionValidationFirstMismatch { get; init; } = string.Empty;
        public ulong SceneSubmissionOpaqueCompactedMeshletDrawBufferSize { get; init; }
        public ulong SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize { get; init; }
        public ulong SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize { get; init; }
        public ulong SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize { get; init; }
        public ulong SceneSubmissionCounterBufferSize { get; init; }
        public ulong SceneSubmissionOpaqueIndirectDispatchBufferSize { get; init; }
        public long GpuCompositeMicroseconds { get; init; }
        public long GpuBloomExtractMicroseconds { get; init; }
        public long GpuBloomDownsampleMicroseconds { get; init; }
        public long GpuBloomUpsampleMicroseconds { get; init; }
        public long GpuDirectionalShadowMicroseconds { get; init; }
        public long GpuSpotShadowMicroseconds { get; init; }
        public long GpuPointShadowMicroseconds { get; init; }
        public int DirectionalShadowRecordSkipped { get; init; }
        public int SpotShadowRecordSkipped { get; init; }
        public int PointShadowRecordSkipped { get; init; }
        public int ScreenshotRequested { get; init; }
        public int ScreenshotPendingCount { get; init; }
        public int ScreenshotCompletedCount { get; init; }
        public string LastScreenshotPath { get; init; } = string.Empty;
        public string LastScreenshotError { get; init; } = string.Empty;
        public int RenderDocAvailable { get; init; }
        public int RenderDocCaptureRequested { get; init; }
        public int RenderDocCaptureCompletedCount { get; init; }
        public string LastRenderDocCaptureMessage { get; init; } = string.Empty;
        public RenderBudgetProfileKind ActiveBudgetProfile { get; init; } = RenderBudgetProfileKind.Development;
        public string ActiveBudgetProfileName { get; init; } = RenderBudgetProfile.Development.Name;
        public RenderQualityPreset ActiveQualityPreset { get; init; } = RenderQualityPreset.DdgiHigh;
        public RenderFeatureIsolationMode ActiveFeatureIsolation { get; init; } = RenderFeatureIsolationMode.FullFrame;
        public int SkippedRenderPassCount { get; init; }
        public int GraphPlannedBarrierCount { get; init; }
        public int GraphExecutedBarrierCount { get; init; }
        public int GraphQueueOwnershipTransitionCount { get; init; }
        public string GraphBarrierSummary { get; init; } = string.Empty;
        public RenderGraphDiagnostics Graph { get; init; } = RenderGraphDiagnostics.Empty;
        public string ProductionPipelineName { get; init; } = string.Empty;
        public IReadOnlyList<string> ProductionPipelineDeclaredPasses { get; init; } = [];
        public int ProductionPipelineDeclaredPassCount { get; init; }
        public IReadOnlyList<string> ProductionPipelineActivePasses { get; init; } = [];
        public int ProductionPipelineActivePassCount { get; init; }
        public int SecondaryCommandBufferEnabled { get; init; }
        public int SecondaryCommandBufferPassCount { get; init; }
        public int AsyncComputeRequested { get; init; }
        public int AsyncComputeEnabled { get; init; }
        public int AsyncComputeSupported { get; init; }
        public int AsyncComputeCandidatePassCount { get; init; }
        public int AsyncComputeEnabledPassCount { get; init; }
        public int AsyncComputeQueueOwnershipTransitionCount { get; init; }
        public string AsyncComputeStatus { get; init; } = string.Empty;
        public IReadOnlyList<string> AsyncComputeCandidatePasses { get; init; } = [];
        public IReadOnlyList<string> AsyncComputeEnabledPasses { get; init; } = [];
        public long CpuPrimaryCommandRecordMicroseconds { get; init; }
        public long CpuSecondaryCommandRecordMicroseconds { get; init; }
        public RenderBudgetStatus BudgetOverallStatus { get; init; } = RenderBudgetStatus.Unknown;
        public RenderBudgetStatus CpuFrameBudgetStatus { get; init; } = RenderBudgetStatus.Unknown;
        public RenderBudgetStatus GpuFrameBudgetStatus { get; init; } = RenderBudgetStatus.Unknown;
        public RenderBudgetStatus GpuMemoryBudgetStatus { get; init; } = RenderBudgetStatus.Unknown;
        public RenderBudgetStatus UploadBudgetStatus { get; init; } = RenderBudgetStatus.Unknown;
        public ulong GpuMemoryBudgetBytes { get; init; }
        public ulong TrackedGpuMemoryBytes { get; init; }
        public int GpuMemoryBudgetQueryAvailable { get; init; }
        public ulong ActualGpuMemoryUsageBytes { get; init; }
        public ulong ActualGpuMemoryBudgetBytes { get; init; }
        public ulong ActualGpuMemoryAllocationBytes { get; init; }
        public ulong ActualGpuMemoryBlockBytes { get; init; }
        public float ActualGpuMemoryUtilization { get; init; }
        public int GpuMemoryHeapCount { get; init; }
        public IReadOnlyList<MemoryHeapBudgetEntry> GpuMemoryHeapBudgets { get; init; } = [];
        public ulong UnknownGpuMemoryBytes { get; init; }
        public ulong MeshBufferAllocatedBytes { get; init; }
        public ulong MeshBufferUsedBytes { get; init; }
        public float MeshBufferUtilization { get; init; }
        public int MeshBufferCompactionCount { get; init; }
        public ulong MeshBufferCompactedBytesSaved { get; init; }
        public int PointShadowSkippedFaceCount { get; init; }
        public ulong SceneBufferAllocatedBytes { get; init; }
        public ulong SceneBufferPeakBytes { get; init; }
        public int SceneBufferResizeCount { get; init; }
        public ulong SceneObjectBufferHighWaterBytes { get; init; }
        public ulong SceneOpaqueMeshletBufferHighWaterBytes { get; init; }
        public ulong SceneDepthMeshletBufferHighWaterBytes { get; init; }
        public ulong SceneTransparentMeshletBufferHighWaterBytes { get; init; }
        public ulong SceneShadowMeshletBufferHighWaterBytes { get; init; }
        public ulong MaterialBufferAllocatedBytes { get; init; }
        public float MaterialBufferUtilization { get; init; }
        public ulong LightBufferAllocatedBytes { get; init; }
        public ulong TiledLightBufferAllocatedBytes { get; init; }
        public ulong TiledLightHeaderBufferClearBytes { get; init; }
        public ulong TiledLightIndexBufferClearBytes { get; init; }
        public int LightTileSaturationCount { get; init; }
        public int MaxLightsInAnyTile { get; init; }
        public float AverageLightsPerNonEmptyTile { get; init; }
        public int LightCullRejectedPointCount { get; init; }
        public int LightCullRejectedSpotCount { get; init; }
        public ulong TextureAssetBytes { get; init; }
        public ulong DefaultTextureBytes { get; init; }
        public ulong FileTextureBytes { get; init; }
        public int TextureCacheEntryCount { get; init; }
        public int TextureBindlessUsedCount { get; init; }
        public int TextureBindlessFreeCount { get; init; }
        public TextureBudgetProfile ActiveTextureBudgetProfile { get; init; } = TextureBudgetProfile.Development;
        public ulong RenderTargetBytes { get; init; }
        public int RenderTargetCount { get; init; }
        public int RenderTargetResizeCount { get; init; }
        public float RequestedDynamicResolutionScale { get; init; } = 1.0f;
        public float CommittedRenderTargetScale { get; init; } = 1.0f;
        public string LastRenderTargetRecreateReason { get; init; } = string.Empty;
        public ulong BloomRenderTargetBytes { get; init; }
        public ulong AmbientOcclusionRenderTargetBytes { get; init; }
        public ulong AntiAliasingRenderTargetBytes { get; init; }
        public int GlobalIlluminationEnabled { get; init; }
        public GlobalIlluminationMode GlobalIlluminationMode { get; init; } = GlobalIlluminationMode.Disabled;
        public GlobalIlluminationDebugView GlobalIlluminationDebugView { get; init; } = GlobalIlluminationDebugView.None;
        public int GlobalIlluminationRayQuerySupported { get; init; }
        public int GlobalIlluminationRayQueryActive { get; init; }
        public int GlobalIlluminationSsgiActive { get; init; }
        public int GlobalIlluminationDdgiActive { get; init; }
        public uint SsgiWidth { get; init; }
        public uint SsgiHeight { get; init; }
        public float SsgiResolutionScale { get; init; }
        public int SsgiRayCount { get; init; }
        public int SsgiHistoryValid { get; init; }
        public int SsgiRejectedHistoryPixelCount { get; init; }
        public int DdgiProbeVolumeCount { get; init; }
        public int DdgiProbeCount { get; init; }
        public int DdgiActiveProbeCount { get; init; }
        public int DdgiProbesUpdated { get; init; }
        public int DdgiRaysPerProbe { get; init; }
        public int DdgiMaxActiveProbeBudget { get; init; }
        public int DdgiMaxProbeUpdatesPerFrame { get; init; }
        public int DdgiProbeUpdateRequestBudget { get; init; }
        public int DdgiProbeUpdatePrimaryRayBudget { get; init; }
        public int DdgiGatherTileCount { get; init; }
        public int DdgiGatherTileCountX { get; init; }
        public int DdgiGatherTileCountY { get; init; }
        public int DdgiGatherSelectedLocalTileCount { get; init; }
        public int DdgiGatherSelectedClipmapTileCount { get; init; }
        public int DdgiGatherFallbackTileCount { get; init; }
        public DdgiQualityTier DdgiQualityTier { get; init; } = DdgiQualityTier.DdgiHigh;
        public float DdgiAdaptiveBudgetScale { get; init; } = 1.0f;
        public int DdgiAdaptiveBudgetReduced { get; init; }
        public int DdgiEmergencyDegradeActive { get; init; }
        public int DdgiEffectiveMaxShadedLights { get; init; }
        public string DdgiAdaptiveBudgetReason { get; init; } = string.Empty;
        public int DdgiAsyncComputeEnabled { get; init; }
        public ulong DdgiAtlasMemoryBudgetBytes { get; init; }
        public int DdgiProbeRelocationCount { get; init; }
        public int DdgiProbeClassificationCount { get; init; }
        public int DdgiCascadeCount { get; init; }
        public int DdgiScrollCount { get; init; }
        public int DdgiNewProbeCount { get; init; }
        public int DdgiDirtyBoundsProbeUpdateCount { get; init; }
        public int DdgiVisibleFrustumProbeUpdateCount { get; init; }
        public int DdgiOutsideFrustumSafetyProbeUpdateCount { get; init; }
        public int DdgiAgeRefreshProbeUpdateCount { get; init; }
        public int DdgiHighVarianceProbeUpdateCount { get; init; }
        public int DdgiLowConfidenceProbeUpdateCount { get; init; }
        public int DdgiStableProbeUpdateCount { get; init; }
        public float DdgiAverageProbeVariability { get; init; }
        public float DdgiAverageProbeConfidence { get; init; }
        public ulong DdgiScheduledPrimaryRayCount { get; init; }
        public ulong DdgiEstimatedShadowRayUpperBound { get; init; }
        public ulong DdgiSelectedDirectionalHitCount { get; init; }
        public ulong DdgiSelectedLocalHitCount { get; init; }
        public ulong DdgiVisibilityRayCount { get; init; }
        public ulong DdgiSkippedLocalLightCount { get; init; }
        public string DdgiLightSelectionMode { get; init; } = string.Empty;
        public int DdgiEmissiveSourceCount { get; init; }
        public uint DdgiEmissiveSourceRevision { get; init; }
        public ulong DdgiCurrentIrradianceAtlasBytes { get; init; }
        public ulong DdgiCurrentVisibilityAtlasBytes { get; init; }
        public ulong DdgiRayScratchBytes { get; init; }
        public ulong DdgiUpdatedAtlasBytes { get; init; }
        public int DdgiPublishedCacheLatencyFrames { get; init; }
        public int DdgiStaleProbeCount { get; init; }
        public float DdgiAverageProbeAge { get; init; }
        public ulong DdgiMaxProbeAge { get; init; }
        public float DdgiFrustumUpdatePercentage { get; init; }
        public float DdgiOutsideFrustumUpdatePercentage { get; init; }
        public int DdgiResourceReinitializationCount { get; init; }
        public int DdgiTotalResourceReinitializationCount { get; init; }
        public int DdgiActiveLocalSlotCount { get; init; }
        public int DdgiLocalSlotGeneration { get; init; }
        public ulong DdgiLocalSlotInitBytes { get; init; }
        public string DdgiLocalVolumeEvictionReason { get; init; } = string.Empty;
        public string DdgiCacheClearReason { get; init; } = string.Empty;
        public DdgiCameraMovementClass DdgiCameraMovementClass { get; init; } = DdgiCameraMovementClass.None;
        public long CpuSsgiRecordMicroseconds { get; init; }
        public long CpuDdgiRecordMicroseconds { get; init; }
        public long CpuDdgiSchedulerMicroseconds { get; init; }
        public long CpuDdgiSchedulerP95Microseconds { get; init; }
        public int DdgiSchedulerTimingSampleCount { get; init; }
        public int DdgiSchedulerP95OverBudget { get; init; }
        public long GpuSsgiTraceMicroseconds { get; init; }
        public long GpuSsgiTemporalMicroseconds { get; init; }
        public long GpuSsgiDenoiseMicroseconds { get; init; }
        public long GpuDdgiTraceMicroseconds { get; init; }
        public long GpuDdgiBlendMicroseconds { get; init; }
        public long GpuDdgiRelocateClassifyMicroseconds { get; init; }
        public long GpuDdgiPublishMicroseconds { get; init; }
        public long GpuDdgiUpdateMicroseconds { get; init; }
        public long GpuGiCompositeMicroseconds { get; init; }
        public ulong GlobalIlluminationRenderTargetBytes { get; init; }
        public ulong SsgiRenderTargetBytes { get; init; }
        public ulong SceneSurfaceRenderTargetBytes { get; init; }
        public ulong DdgiTextureBytes { get; init; }
        public ulong DdgiBufferBytes { get; init; }
        public ulong AccelerationStructureBytes { get; init; }
        public ulong AccelerationStructureScratchBytes { get; init; }
        public ulong AccelerationStructureInstanceBufferBytes { get; init; }
        public ulong AccelerationStructureRayQueryMetadataBytes { get; init; }
        public int AccelerationStructureBottomLevelCount { get; init; }
        public int AccelerationStructureTopLevelInstanceCount { get; init; }
        public int AccelerationStructureBlasBuildCount { get; init; }
        public int AccelerationStructureTlasBuildCount { get; init; }
        public int AccelerationStructureTlasUpdateCount { get; init; }
        public int AccelerationStructureTlasSkipCount { get; init; }
        public ulong AccelerationStructureInstanceUploadBytes { get; init; }
        public ulong AccelerationStructureRayQueryMetadataUploadBytes { get; init; }
        public long CpuAccelerationStructureBuildMicroseconds { get; init; }
        public long CpuAccelerationStructureBlasBuildMicroseconds { get; init; }
        public long CpuAccelerationStructureTlasBuildMicroseconds { get; init; }
        public long CpuAccelerationStructureInstanceUploadMicroseconds { get; init; }
        public long GpuAccelerationStructureBlasMicroseconds { get; init; }
        public long GpuAccelerationStructureTlasMicroseconds { get; init; }
        public string AccelerationStructureFallbackReason { get; init; } = string.Empty;
        public ulong ShadowMapBytes { get; init; }
        public ulong DirectionalShadowBytes { get; init; }
        public ulong SpotShadowAtlasBytes { get; init; }
        public ulong PointShadowBytes { get; init; }
        public float SpotShadowAtlasUtilization { get; init; }
        public float PointShadowFaceUtilization { get; init; }
        public ulong EnvironmentMapBytes { get; init; }
        public ulong IrradianceMapBytes { get; init; }
        public ulong PrefilteredEnvironmentBytes { get; init; }
        public ulong BrdfLutBytes { get; init; }
        public ulong ReflectionProbeBytes { get; init; }
        public ulong ReflectionProbeCaptureTargetBytes { get; init; }
        public ulong ReflectionProbeCubemapArrayBytes { get; init; }
        public int ReflectionProbeCaptureBudgetUsed { get; init; }
        public int ReflectionProbeCaptureBudgetExceeded { get; init; }
        public ulong StagingBufferAllocatedBytes { get; init; }
        public ulong StagingBytesUsedThisFrame { get; init; }
        public ulong StagingBytesPeakThisSession { get; init; }
        public int StagingOverflowCount { get; init; }
        public int StagingOverflowCountThisFrame { get; init; }
        public int StagingRetainedOverflowBufferCount { get; init; }
        public ulong StagingRetainedOverflowBytes { get; init; }
        public ulong StagingPeakOverflowBytes { get; init; }
        public ulong StagingLargestOverflowAllocationBytes { get; init; }
        public int UploadBudgetExceeded { get; init; }
        public float UploadBudgetUtilization { get; init; }
        public ulong UploadBudgetBytesPerFrame { get; init; }
        public ulong SwapchainEstimatedBytes { get; init; }
        public int SwapchainImageCount { get; init; }
        public string SwapchainFormat { get; init; } = string.Empty;
        public long CpuAcquireImageMicroseconds { get; init; }
        public long CpuWaitForFrameFenceMicroseconds { get; init; }
        public long CpuQueueSubmitMicroseconds { get; init; }
        public long CpuPresentMicroseconds { get; init; }
        public long CpuFenceResetMicroseconds { get; init; }
        public long RuntimeStallMicrosecondsThisFrame { get; init; }
        public long RuntimeWorstStallMicroseconds { get; init; }
        public RuntimeStallReason RuntimeWorstStallReason { get; init; } = RuntimeStallReason.Unknown;
        public int RuntimeDeviceWaitIdleCount { get; init; }
        public long GpuFrameMicroseconds { get; init; }
        public RendererValidationMode ValidationMode { get; init; } = RendererValidationMode.Off;
        public int ValidationVerboseMessageCount { get; init; }
        public int ValidationInfoMessageCount { get; init; }
        public int ValidationWarningMessageCount { get; init; }
        public int ValidationErrorMessageCount { get; init; }
        public IReadOnlyList<DdgiVolumeDiagnosticsEntry> DdgiVolumes { get; init; } = [];

        public static RendererDiagnostics Empty { get; } = new(
            VisibleObjectCount: 0,
            VisibleMeshletCount: 0,
            OpaqueObjectCount: 0,
            MaskedObjectCount: 0,
            TransparentObjectCount: 0,
            OpaqueMeshletCount: 0,
            TransparentMeshletCount: 0,
            SubmittedOpaqueMeshlets: 0,
            FrustumCulledMeshletsGpu: 0,
            OcclusionCulledMeshlets: 0,
            ForwardMeshletCandidates: 0,
            ForwardMeshletVisibleAfterOcclusion: 0,
            BlendMaterialCount: 0,
            UploadedBytes: 0,
            LightCount: 0,
            TileCountX: 0,
            TileCountY: 0,
            MaterialCount: 0,
            TextureCount: 0,
            LoadedFileTextureCount: 0,
            MipmapFallbackCount: 0,
            DownscaledTextureCount: 0,
            MaxLoadedTextureDimension: 0,
            EstimatedTextureBytes: 0,
            LoadedModelName: string.Empty,
            ModelRenderObjectCount: 0,
            RegisteredMeshCount: 0,
            LoadedMaterialCount: 0,
            LoadedTextureCount: 0,
            DefaultWhiteSubstitutions: 0,
            DefaultNormalSubstitutions: 0,
            DefaultBlackSubstitutions: 0,
            CpuSceneBuildMicroseconds: 0,
            GpuDepthPrePassMicroseconds: 0,
            GpuHiZBuildMicroseconds: 0,
            GpuForwardOpaqueMicroseconds: 0,
            GpuTransparentMicroseconds: 0,
            SceneUploadCount: 0,
            SceneUploadSkipped: 0,
            ObjectCandidatesCpu: 0,
            ObjectFrustumCulledCpu: 0,
            MeshletCandidatesCpu: 0,
            MeshletFrustumCulledCpu: 0,
            MeshletLodSkippedCpu: 0,
            MeshletLod0SubmittedCpu: 0,
            MeshletLod1SubmittedCpu: 0,
            MeshletLod2SubmittedCpu: 0,
            CpuPayloadSignatureMicroseconds: 0,
            CpuObjectCullMicroseconds: 0,
            CpuMeshletCullMicroseconds: 0,
            CpuUploadMicroseconds: 0,
            CpuMaterialUploadMicroseconds: 0,
            CpuTotalDrawSceneMicroseconds: 0,
            CpuDirectionalShadowRecordMicroseconds: 0,
            CpuSpotShadowRecordMicroseconds: 0,
            CpuPointShadowRecordMicroseconds: 0,
            CpuDepthPrePassRecordMicroseconds: 0,
            CpuHiZBuildRecordMicroseconds: 0,
            CpuLightCullRecordMicroseconds: 0,
            CpuForwardOpaqueRecordMicroseconds: 0,
            CpuTransparentRecordMicroseconds: 0,
            CpuBloomExtractRecordMicroseconds: 0,
            CpuBloomDownsampleRecordMicroseconds: 0,
            CpuBloomUpsampleRecordMicroseconds: 0,
            CpuFogRecordMicroseconds: 0,
            CpuCompositeRecordMicroseconds: 0,
            GpuLightCullMicroseconds: 0,
            DepthTaskInvocations: 0,
            DepthFrustumCulledMeshletsGpu: 0,
            DepthEmittedMeshletsGpu: 0,
            ForwardTaskInvocations: 0,
            ForwardFrustumCulledMeshletsGpu: 0,
            ForwardOcclusionTestedMeshletsGpu: 0,
            ForwardEmittedMeshletsGpu: 0,
            MeshletCountTotal: 0,
            MeshletCountSubmittedCpu: 0,
            AvgTrianglesPerSubmittedMeshlet: 0f,
            AvgVerticesPerSubmittedMeshlet: 0f,
            SmallMeshletsUnder16Triangles: 0,
            SmallMeshletsUnder32Triangles: 0,
            ScenePayloadRebuilt: 0,
            ObjectUploadBytes: 0,
            InstanceUploadBytes: 0,
            MeshletDrawUploadBytes: 0,
            TransparentMeshletDrawUploadBytes: 0,
            MaterialUploadBytes: 0,
            LightUploadBytes: 0,
            DepthPrePassEnabled: 0,
            HiZEnabled: 0,
            OcclusionEnabled: 0,
            HiZMipCount: 0,
            HiZWidth: 0,
            HiZHeight: 0,
            DirectionalShadowsEnabled: 0,
            DirectionalShadowMapSize: 0,
            DirectionalShadowCascadeCount: 0,
            ShadowedDirectionalLightIndex: -1,
            ShadowDebugView: ShadowDebugView.None,
            ShadowNormalBias: 0f,
            ShadowSlopeScaledDepthBias: 0f,
            DirectionalShadowPcfRadius: 0,
            SpotShadowPcfRadius: 0,
            PointShadowPcfRadius: 0,
            ForwardShadowReceiverMeshletCount: 0,
            SpotShadowsEnabled: 0,
            SpotShadowCandidateCount: 0,
            SpotShadowSelectedCount: 0,
            SpotShadowRejectedByBudgetCount: 0,
            SpotShadowAtlasSize: 0,
            SpotShadowTileSize: 0,
            SpotShadowAtlasCapacity: 0,
            SpotShadowAtlasUsedTiles: 0,
            PointShadowsEnabled: 0,
            PointShadowCandidateCount: 0,
            PointShadowSelectedCount: 0,
            PointShadowRejectedByBudgetCount: 0,
            PointShadowMapSize: 0,
            PointShadowRenderedFaceCount: 0,
            HdrEnabled: 0,
            SceneColorFormat: string.Empty,
            Exposure: 0f,
            ToneMapper: ToneMapper.AcesFitted,
            BloomEnabled: 0,
            BloomMipCount: 0,
            BloomBaseWidth: 0,
            BloomBaseHeight: 0,
            BloomFormat: string.Empty,
            BloomIntensity: 0f,
            BloomThreshold: 0f,
            BloomKnee: 0f,
            BloomRadius: 0f,
            BloomDebugView: BloomDebugView.None,
            BloomDebugMipLevel: 0,
            FogEnabled: 0,
            FogMode: FogMode.Disabled,
            FogColorMode: FogColorMode.ConstantColor,
            FogDebugView: FogDebugView.None,
            FogDensity: 0f,
            FogStartDistance: 0f,
            FogEndDistance: 0f,
            FogHeight: 0f,
            FogHeightFalloff: 0f,
            FogHeightDensity: 0f,
            FogMaxOpacity: 0f,
            FogDirectionalInscatteringEnabled: 0,
            FogWidth: 0,
            FogHeightPixels: 0,
            FogFormat: string.Empty,
            GpuFogMicroseconds: 0,
            AmbientOcclusionEnabled: 0,
            AmbientOcclusionMode: AmbientOcclusionMode.Disabled,
            AmbientOcclusionDebugView: AmbientOcclusionDebugView.None,
            AmbientOcclusionForwardSamplingMode: AmbientOcclusionForwardSamplingMode.Disabled,
            AmbientOcclusionForwardDepthAwareSamples: 0,
            AmbientOcclusionWidth: 0,
            AmbientOcclusionHeight: 0,
            AmbientOcclusionFormat: string.Empty,
            AmbientOcclusionResolutionScale: 0f,
            AmbientOcclusionRadius: 0f,
            AmbientOcclusionIntensity: 0f,
            AmbientOcclusionBias: 0f,
            AmbientOcclusionSampleCount: 0,
            AmbientOcclusionBlurRadius: 0,
            CpuAmbientOcclusionRecordMicroseconds: 0,
            CpuAmbientOcclusionBlurRecordMicroseconds: 0,
            GpuAmbientOcclusionMicroseconds: 0,
            GpuAmbientOcclusionBlurMicroseconds: 0,
            AntiAliasingMode: AntiAliasingMode.None,
            AntiAliasingDebugView: AntiAliasingDebugView.None,
            AntiAliasingWidth: 0,
            AntiAliasingHeight: 0,
            AntiAliasingInputFormat: string.Empty,
            AntiAliasingOutputFormat: string.Empty,
            CpuFxaaRecordMicroseconds: 0,
            CpuSmaaEdgeRecordMicroseconds: 0,
            CpuSmaaBlendRecordMicroseconds: 0,
            CpuSmaaNeighborhoodRecordMicroseconds: 0,
            GpuAntiAliasingMicroseconds: 0,
            SmaaLookupTexturesReady: 0,
            MotionVectorsEnabled: 0,
            JitterEnabled: 0,
            JitterX: 0f,
            JitterY: 0f,
            EnvironmentEnabled: 0,
            EnvironmentSourceKind: EnvironmentSourceKind.ProceduralSky,
            EnvironmentSourcePath: string.Empty,
            EnvironmentUsesFallback: 0,
            EnvironmentCubemapSize: 0,
            IrradianceCubemapSize: 0,
            PrefilteredEnvironmentSize: 0,
            PrefilteredEnvironmentMipCount: 0,
            BrdfLutSize: 0,
            SkyIntensity: 0f,
            DiffuseIblIntensity: 0f,
            SpecularIblIntensity: 0f,
            EnvironmentDebugView: EnvironmentDebugView.None,
            EnvironmentDebugMipLevel: 0,
            EnvironmentTextureBytes: 0,
            ReflectionsEnabled: 0,
            ReflectionMode: ReflectionMode.Disabled,
            ReflectionDebugView: ReflectionDebugView.None,
            ReflectionProbeCount: 0,
            ReflectionProbeCapacity: 0,
            MaxReflectionProbesPerPixel: 0,
            ReflectionProbeResolution: 0,
            ReflectionProbeMipCount: 0,
            ReflectionProbeEstimatedBytes: 0,
            ReflectionProbeCapturesQueued: 0,
            ReflectionProbeCapturesCompleted: 0,
            CpuReflectionProbeUploadMicroseconds: 0,
            CpuReflectionProbeCaptureRecordMicroseconds: 0,
            CpuReflectionProbePrefilterRecordMicroseconds: 0,
            GpuReflectionProbeCaptureMicroseconds: 0,
            GpuReflectionProbePrefilterMicroseconds: 0);

        public uint TileCount => TileCountX * TileCountY;
    }
}
