using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data
{
    public class SceneRenderingData : IDisposable
    {
        public int FrameIndex { get; set; }
        public uint TemporalSampleIndex { get; set; }
        public ulong DdgiFrameSerial { get; set; }
        /// <summary>
        /// Monotonic revision of the static scene payload. It changes only when scene content
        /// that can affect rendered geometry/material data changes, and is intended for capture
        /// correlation rather than frame-to-frame timing.
        /// </summary>
        public ulong SceneContentRevision { get; set; }
        /// <summary>
        /// Monotonic revision of material aspects consumed by SSGI history:
        /// receiver diffuse response/AO, alpha coverage, and shading model.
        /// </summary>
        public uint SsgiMaterialRevision { get; set; }
        public uint DdgiFrameSerialLow32 => unchecked((uint)DdgiFrameSerial);
        public uint ImageIndex { get; set; }
        public Vector4 ClearColor { get; set; } = new(0.2f, 0.2f, 0.2f, 1f);
        public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 ProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 ViewProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 InverseViewMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 InverseProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 InverseViewProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Vector3 CameraPosition { get; set; } = Vector3.Zero;
        // Capture-only camera context. These are populated from ICamera by VulkanRenderer and
        // deliberately live next to the matrices so exported performance snapshots can be
        // replayed without relying on application-side diagnostics.
        public float CaptureCameraYawRadians { get; set; }
        public float CaptureCameraPitchRadians { get; set; }
        public float CaptureCameraFieldOfViewRadians { get; set; }
        public float CaptureCameraNearPlane { get; set; }
        public float CaptureCameraFarPlane { get; set; }
        public ulong CaptureCameraCutSerial { get; set; }
        public ulong CaptureFramesSinceSceneLoad { get; set; }
        public string CaptureSceneName { get; set; } = "unknown-scene";
        /// <summary>
        /// Optional application-owned identifier for the active benchmark or validation
        /// scenario. The renderer never infers this from a scene name: an omitted value is
        /// exported as explicitly unavailable so a capture cannot be mistaken for a known
        /// scenario.
        /// </summary>
        public string CaptureScenario { get; set; } = string.Empty;
        public int ObjectCount { get; set; }
        public int MeshletCount { get; set; }
        public int StaticInstanceBatchCount { get; set; }
        public int StaticInstanceCount { get; set; }
        public int VisibleStaticInstanceCount { get; set; }
        public int CulledStaticInstanceCount { get; set; }
        public int StaticBatchMeshletDrawCommandCount { get; set; }
        public long CpuStaticBatchBuildMicroseconds { get; set; }
        public int OpaqueObjectCount { get; set; }
        public int SolidObjectCount { get; set; }
        public int MaskedObjectCount { get; set; }
        public int TransparentObjectCount { get; set; }
        public int GeometryDecalObjectCount { get; set; }
        public int OpaqueMeshletCount { get; set; }
        public int SimpleOpaqueMeshletCount { get; set; }
        public int SimpleNormalOpaqueMeshletCount { get; set; }
        public int FullOpaqueMeshletCount { get; set; }
        public int ForwardSimpleMeshletCount { get; set; }
        public int ForwardFullMaterialMeshletCount { get; set; }
        public int ForwardLocalProbeMeshletCount { get; set; }
        public int SolidMeshletCount { get; set; }
        public int MaskedMeshletCount { get; set; }
        public int TransparentMeshletCount { get; set; }
        public int GeometryDecalMeshletCount { get; set; }
        public int BlendMaterialCount { get; set; }
        public int MaskMaterialCount { get; set; }
        public int GeometryDecalMaterialCount { get; set; }
        public int TransparentSortCandidateCount { get; set; }
        public long TransparentSortMicroseconds { get; set; }
        public int TransparentOverflowCount { get; set; }
        public int MaterialCount { get; set; }
        public int LightCount { get; set; }
        public int DirectionalLightCount { get; set; }
        public int LocalLightCount { get; set; }
        public int TextureCount { get; set; }
        public uint CurrentFrameIndex { get; set; }
        public uint ScreenWidth { get; set; }
        public uint ScreenHeight { get; set; }
        public uint TileCountX { get; set; }
        public uint TileCountY { get; set; }
        public uint HiZMipCount { get; set; }
        public bool OcclusionCullingEnabled { get; set; } = true;
        public HiZTestMode HiZTestMode { get; set; } = HiZTestMode.Bounds4Tap;
        public bool PreviousHiZFrameValid { get; set; }
        public int PreviousHiZUvPaddingPixels { get; set; } = 8;
        public int PreviousHiZSkippedInvalidHistory { get; set; }
        public int PreviousHiZSkippedCameraMotion { get; set; }
        public int PreviousHiZTested { get; set; }
        public int PreviousHiZCulled { get; set; }
        public bool DepthPrePassEnabled { get; set; } = true;
        /// <summary>
        /// Set only after <c>DepthPrePass</c> has recorded this frame's clear and opaque depth.
        /// Consumers use this provenance instead of accepting a depth image left over from a
        /// previous frame or a partially configured render path.
        /// </summary>
        public bool DepthPrePassCompleted { get; set; }
        public ulong DepthPrePassFrameSerial { get; set; }
        public bool HasCurrentDepthPrePass =>
            DepthPrePassCompleted && DepthPrePassFrameSerial == DdgiFrameSerial;
        /// <summary>
        /// Set after tiled local-light culling has consumed the current prepass depth.
        /// </summary>
        public bool TiledLightCullingCompleted { get; set; }
        public ulong TiledLightCullingFrameSerial { get; set; }
        public bool HasCurrentTiledLightCulling =>
            TiledLightCullingCompleted && TiledLightCullingFrameSerial == DdgiFrameSerial;
        public bool HiZBuildEnabled { get; set; } = true;
        public bool ForwardVisibilityCompactionEnabled { get; set; }
        public bool ForwardVisibilityCompactionActive { get; set; }
        public string ForwardVisibilityCompactionSkipReason { get; set; } = string.Empty;
        public int ForwardVisibilitySimpleCapacity { get; set; }
        public int ForwardVisibilitySimpleNormalCapacity { get; set; }
        public int ForwardVisibilityFullCapacity { get; set; }
        public BufferHandle ForwardVisibilityCounterBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle ForwardVisibilityIndirectDispatchBuffer { get; set; } = BufferHandle.Invalid;
        public ulong ForwardVisibilityBufferBytes { get; set; }
        public int CurrentFrameHiZTested { get; set; }
        public int CurrentFrameHiZCulled { get; set; }
        public int HiZConsumerCount { get; set; }
        public string HiZConsumerSummary { get; set; } = string.Empty;
        public bool HiZBuildSkippedBecauseNoConsumer { get; set; }
        public HiZCounterSource HiZCounterSource { get; set; } = HiZCounterSource.Unavailable;
        public int ForwardHiZTestedCount { get; set; }
        public int ForwardHiZCulledCount { get; set; }
        public float ForwardHiZCullRate { get; set; }
        public string HiZFallbackPath { get; set; } = HiZFallbackPaths.Disabled;
        public string HiZFallbackReason { get; set; } = string.Empty;
        public bool HiZValidateAgainstLegacyPath { get; set; }
        public HiZVisibilityPolicyStatus HiZPolicyStatus { get; set; } = HiZVisibilityPolicyStatus.Disabled;
        public string HiZPolicyReason { get; set; } = string.Empty;
        public int HiZPolicyWarmupFramesRemaining { get; set; }
        public int HiZPolicySceneChanged { get; set; }
        public int HiZPolicyCameraCut { get; set; }
        public int HiZPolicyPyramidInvalidated { get; set; }
        public int HiZPolicyAdaptiveSuppressed { get; set; }
        public int HiZPolicyAdaptiveProbe { get; set; }
        public int HiZPolicyAdaptiveProbeCountdown { get; set; }
        public int HiZPolicyAdaptiveMeasuredOcclusionTests { get; set; }
        public int HiZPolicyAdaptiveMeasuredOcclusionCulled { get; set; }
        public float HiZPolicyAdaptiveCullRate { get; set; }
        public HiZCounterSource HiZPolicyCounterSource
        {
            get => HiZCounterSource;
            set => HiZCounterSource = value;
        }
        public long HiZPolicyAdaptiveEstimatedSavedMicroseconds { get; set; }
        public long HiZPolicyAdaptiveEstimatedCostMicroseconds { get; set; }
        public long HiZPolicyAdaptiveEstimatedNetMicroseconds { get; set; }
        public float HiZPolicyAdaptiveSmoothedCullRate { get; set; }
        public float HiZPolicyAdaptiveSmoothedSavedToCostRatio { get; set; }
        public int HiZPolicyAdaptiveSuppressedFrameCount { get; set; }
        public string HiZPolicyAdaptiveStatus { get; set; } = string.Empty;
        public bool TransparentPassEnabled { get; set; } = true;
        public TransparencyMode TransparencyMode { get; set; } = TransparencyMode.SortedAlphaBlend;
        public TransparencyDebugView TransparencyDebugView { get; set; } = TransparencyDebugView.None;
        public bool TransparentReceiveShadows { get; set; } = true;
        public bool TransparentReceiveGlobalIllumination { get; set; } = true;
        public bool TransparentDdgiReceiverCountersEnabled { get; set; }
        public DecalDebugView DecalDebugView { get; set; } = DecalDebugView.None;
        public bool GeometryDecalsEnabled { get; set; } = true;
        public bool DecalReceiveGlobalIllumination { get; set; } = true;
        public float GeometryDecalDepthBias { get; set; } = 0.0005f;
        public float GeometryDecalSlopeScaledDepthBias { get; set; }
        public bool AnimationEnabled { get; set; }
        public AnimationSkinningMode AnimationSkinningMode { get; set; } = AnimationSkinningMode.Disabled;
        public AnimationDebugView AnimationDebugView { get; set; } = AnimationDebugView.None;
        public int AnimatedModelCount { get; set; }
        public int SkinnedObjectCount { get; set; }
        public int SkeletonCount { get; set; }
        public int SkinCount { get; set; }
        public int AnimationClipCount { get; set; }
        public int ActiveAnimatorCount { get; set; }
        public int PlayingAnimatorCount { get; set; }
        public int PausedAnimatorCount { get; set; }
        public int SkinnedVertexCount { get; set; }
        public int SkinningDispatchCount { get; set; }
        public int JointMatrixCount { get; set; }
        public int MaxJointsPerSkeleton { get; set; }
        public long CpuAnimationSampleMicroseconds { get; set; }
        public long CpuSkinMatrixUploadMicroseconds { get; set; }
        public long CpuSkinningRecordMicroseconds { get; set; }
        public long GpuSkinningMicroseconds { get; set; }
        public ulong SkinningUploadBytes { get; set; }
        public ulong SkinMatrixBufferSize { get; set; }
        public ulong SkinnedVertexBufferSize { get; set; }
        public string AnimatedBoundsMode { get; set; } = string.Empty;
        public bool ParticlesEnabled { get; set; }
        public ParticleSimulationMode ParticleSimulationMode { get; set; } = ParticleSimulationMode.Cpu;
        public ParticleDebugView ParticleDebugView { get; set; } = ParticleDebugView.None;
        public int ParticleEffectCount { get; set; }
        public int ParticleEmitterCount { get; set; }
        public int LiveParticleCount { get; set; }
        public int SimulatedParticleCount { get; set; }
        public int CulledParticleCount { get; set; }
        public int RenderedParticleCount { get; set; }
        public int ParticleBatchCount { get; set; }
        public int ParticleDdgiSampleCount { get; set; }
        public int VfxDdgiDirtyProbeEventCount { get; set; }
        public int AlphaParticleCount { get; set; }
        public int AdditiveParticleCount { get; set; }
        public int SoftParticleCount { get; set; }
        public int FlipbookParticleCount { get; set; }
        public int TrailCount { get; set; }
        public int TrailSegmentCount { get; set; }
        public int BeamCount { get; set; }
        public int ParticleBudgetExceeded { get; set; }
        public int ParticleUploadBudgetExceeded { get; set; }
        public ulong ParticleInstanceUploadBytes { get; set; }
        public ulong TrailBeamUploadBytes { get; set; }
        public long CpuParticleSimulationMicroseconds { get; set; }
        public long CpuParticleBuildMicroseconds { get; set; }
        public long CpuParticleRecordMicroseconds { get; set; }
        public long CpuGpuParticleResetRecordMicroseconds { get; set; }
        public long CpuGpuParticleEmitterUploadMicroseconds { get; set; }
        public long CpuGpuParticleSimulateRecordMicroseconds { get; set; }
        public long CpuTrailBeamRecordMicroseconds { get; set; }
        public long GpuParticleMicroseconds { get; set; }
        public long GpuTrailBeamMicroseconds { get; set; }
        public int ParticleDrawCallCount { get; set; }
        public BufferHandle ParticleInstanceBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle ParticleBatchBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle ParticleFrameDataBuffer { get; set; } = BufferHandle.Invalid;
        public ulong ParticleInstanceBufferSize { get; set; }
        public ulong ParticleBatchBufferSize { get; set; }
        public ulong ParticleFrameDataBufferSize { get; set; }
        public int GpuParticlesEnabled { get; set; }
        public int GpuParticleCapacity { get; set; }
        public int GpuParticleEmitterCapacity { get; set; }
        public int GpuParticleDrawCapacity { get; set; }
        public int GpuParticleResetRequired { get; set; }
        public int GpuParticleEmitterCount { get; set; }
        public int GpuParticleMaxSpawnPerEmitter { get; set; }
        public float GpuParticleDeltaSeconds { get; set; }
        public float GpuParticleTimeSeconds { get; set; }
        public ulong GpuParticleEmitterUploadBytes { get; set; }
        public int GpuParticleCountersReadbackValid { get; set; }
        public uint GpuParticleAliveCount { get; set; }
        public uint GpuParticleDeadCount { get; set; }
        public uint GpuParticleSpawnedCount { get; set; }
        public uint GpuParticleKilledCount { get; set; }
        public uint GpuParticleCulledCount { get; set; }
        public uint GpuParticleRenderedCount { get; set; }
        public uint GpuParticleDroppedSpawnCount { get; set; }
        public uint GpuParticleBlendBucket0Count { get; set; }
        public uint GpuParticleBlendBucket1Count { get; set; }
        public uint GpuParticleBlendBucket2Count { get; set; }
        public uint GpuParticleBlendBucket3Count { get; set; }
        public uint GpuParticleBlendBucket4Count { get; set; }
        public int FoliagePatchCount { get; set; }
        public int FoliagePrototypeCount { get; set; }
        public int FoliageClusterCount { get; set; }
        public int FoliageVisibleClusterCount { get; set; }
        public int FoliageCulledClusterCount { get; set; }
        public int FoliageVisibleMeshletDrawCount { get; set; }
        public int FoliageDdgiSampleCount { get; set; }
        public int FoliageGrassBladeEstimate { get; set; }
        public int FoliageLod0VisibleCount { get; set; }
        public int FoliageLod1VisibleCount { get; set; }
        public int FoliageLod2VisibleCount { get; set; }
        public int FoliageHiZTestedCount { get; set; }
        public int FoliageHiZRejectedCount { get; set; }
        public int FoliageOverflowCount { get; set; }
        public int FoliageMeshletDrawOverflowCount { get; set; }
        public int FoliageFarImpostorVisibleCount { get; set; }
        public uint FoliageDebugView { get; set; }
        public bool FoliageIndirectMeshletDispatchEnabled { get; set; } = true;
        public bool FoliageCastShadows { get; set; } = true;
        public bool FoliageMotionVectorsEnabled { get; set; }
        public bool FoliageLocalShadowsEnabled { get; set; }
        public float FoliageGrassShadowDensityScale { get; set; } = 0.5f;
        public int FoliageMaxLocalShadowedSpotLights { get; set; } = 1;
        public int FoliageMaxLocalShadowedPointLights { get; set; }
        public int FoliageLocalShadowClusterBudget { get; set; } = 4096;
        public int FoliageLocalShadowMeshletDrawBudget { get; set; } = 8192;
        public ulong FoliageInstanceBufferBytes { get; set; }
        public ulong FoliageClusterBufferBytes { get; set; }
        public ulong FoliageDrawBufferBytes { get; set; }
        public ulong FoliageImpostorAtlasBytes { get; set; }
        public long CpuFoliageBuildMicroseconds { get; set; }
        public long CpuFoliageUploadMicroseconds { get; set; }
        public long GpuFoliageCullMicroseconds { get; set; }
        public long GpuFoliageDepthMicroseconds { get; set; }
        public long GpuFoliageForwardMicroseconds { get; set; }
        public long GpuFoliageShadowMicroseconds { get; set; }
        public BufferHandle GpuParticleRenderInstanceBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle GpuParticleIndirectDrawBuffer { get; set; } = BufferHandle.Invalid;
        public ulong GpuParticleStateBufferSize { get; set; }
        public ulong GpuParticleAliveIndexBufferSize { get; set; }
        public ulong GpuParticleDeadIndexBufferSize { get; set; }
        public ulong GpuParticleEmitterBufferSize { get; set; }
        public ulong GpuParticleCurveSampleBufferSize { get; set; }
        public ulong GpuParticleCounterBufferSize { get; set; }
        public ulong GpuParticleUnsortedRenderInstanceBufferSize { get; set; }
        public ulong GpuParticleRenderInstanceBufferSize { get; set; }
        public ulong GpuParticleIndirectDrawBufferSize { get; set; }
        public ulong GpuParticleSortKeyBufferSize { get; set; }
        public float OcclusionBias { get; set; } = 0.0005f;
        public uint DebugViewMode { get; set; }
        public int MaxLightsPerTile { get; set; }
        public int MaxLightsInAnyTile { get; set; }
        public float AverageLightsPerNonEmptyTile { get; set; }
        public int LightTileSaturationCount { get; set; }
        public int LightCullRejectedPointCount { get; set; }
        public int LightCullRejectedSpotCount { get; set; }
        public ulong UploadedBytes { get; set; }
        public long CpuSceneBuildMicroseconds { get; set; }
        public long CpuPayloadSignatureMicroseconds { get; set; }
        public long CpuObjectCullMicroseconds { get; set; }
        public long CpuMeshletCullMicroseconds { get; set; }
        public long CpuUploadMicroseconds { get; set; }
        public long CpuMaterialUploadMicroseconds { get; set; }
        public long CpuTotalDrawSceneMicroseconds { get; set; }
        public long CpuDepthPrePassRecordMicroseconds { get; set; }
        public long CpuDirectionalShadowRecordMicroseconds { get; set; }
        public long CpuSpotShadowRecordMicroseconds { get; set; }
        public long CpuPointShadowRecordMicroseconds { get; set; }
        public long CpuHiZBuildRecordMicroseconds { get; set; }
        public long CpuHiZDepthTransitionMicroseconds { get; set; }
        public long CpuHiZPyramidTransitionMicroseconds { get; set; }
        public long CpuHiZDescriptorBindMicroseconds { get; set; }
        public long CpuHiZPushDispatchMicroseconds { get; set; }
        public long CpuHiZFinalBarrierMicroseconds { get; set; }
        public long CpuLightCullRecordMicroseconds { get; set; }
        public long CpuForwardOpaqueRecordMicroseconds { get; set; }
        public long CpuTransparentRecordMicroseconds { get; set; }
        public long CpuBloomExtractRecordMicroseconds { get; set; }
        public long CpuBloomDownsampleRecordMicroseconds { get; set; }
        public long CpuBloomUpsampleRecordMicroseconds { get; set; }
        public long CpuFogRecordMicroseconds { get; set; }
        public long CpuAutoExposureRecordMicroseconds { get; set; }
        public long CpuCompositeRecordMicroseconds { get; set; }
        public int SecondaryCommandBufferEnabled { get; set; }
        public int SecondaryCommandBufferPassCount { get; set; }
        public RenderFeatureIsolationMode ActiveFeatureIsolation { get; set; } = RenderFeatureIsolationMode.FullFrame;
        public int SkippedRenderPassCount { get; set; }
        public int GraphPlannedBarrierCount { get; set; }
        public int GraphExecutedBarrierCount { get; set; }
        public int GraphQueueOwnershipTransitionCount { get; set; }
        public string GraphBarrierSummary { get; set; } = string.Empty;
        public int AsyncComputeOwnershipTransferCount { get; set; }
        public long AsyncComputeEstimatedOverlapMicroseconds { get; set; }
        public long CpuPrimaryCommandRecordMicroseconds { get; set; }
        public long CpuSecondaryCommandRecordMicroseconds { get; set; }
        public long GpuDepthPrePassMicroseconds { get; set; }
        public long GpuHiZBuildMicroseconds { get; set; }
        public long GpuLightCullMicroseconds { get; set; }
        public long GpuForwardOpaqueMicroseconds { get; set; }
        /// <summary>
        /// Inclusive timestamp for the forward draw scope while DDGI gather is
        /// active. GPU timestamps cannot isolate fragment-shader instructions, so
        /// <see cref="GpuForwardGiGatherTimingCoverage"/> documents this boundary.
        /// </summary>
        public long GpuForwardGiGatherMicroseconds { get; set; }
        /// <summary>0 unavailable; 1 inclusive forward draw scope containing GI gather.</summary>
        public int GpuForwardGiGatherTimingCoverage { get; set; }
        public long GpuTransparentMicroseconds { get; set; }
        public long GpuDirectionalShadowMicroseconds { get; set; }
        public long GpuSpotShadowMicroseconds { get; set; }
        public long GpuPointShadowMicroseconds { get; set; }
        public long GpuBloomExtractMicroseconds { get; set; }
        public long GpuBloomDownsampleMicroseconds { get; set; }
        public long GpuBloomUpsampleMicroseconds { get; set; }
        public long GpuAutoExposureMicroseconds { get; set; }
        public long GpuCompositeMicroseconds { get; set; }
        public int SceneUploadCount { get; set; }
        public int SceneUploadSkipped { get; set; }
        public int ObjectCandidatesCpu { get; set; }
        public int ObjectFrustumCulledCpu { get; set; }
        public int MeshletCandidatesCpu { get; set; }
        public int MeshletFrustumCulledCpu { get; set; }
        public int MeshletLodSkippedCpu { get; set; }
        public int MeshletLod0SubmittedCpu { get; set; }
        public int MeshletLod1SubmittedCpu { get; set; }
        public int MeshletLod2SubmittedCpu { get; set; }
        public ulong StableSceneInputUploadBytes { get; set; }
        public ulong CpuCandidateListUploadBytes { get; set; }
        public int CameraDrivenCpuDrawListRebuilt { get; set; }
        public int DepthTaskInvocations { get; set; }
        public int DepthFrustumCulledMeshletsGpu { get; set; }
        public int DepthEmittedMeshletsGpu { get; set; }
        public int ForwardTaskInvocations { get; set; }
        public int ForwardFrustumCulledMeshletsGpu { get; set; }
        public int ForwardOcclusionTestedMeshletsGpu { get; set; }
        public int ForwardOcclusionCulledMeshletsGpu { get; set; }
        public int ForwardEmittedMeshletsGpu { get; set; }
        public bool SceneSubmissionGpuCompactionEnabled { get; set; }
        public bool SceneSubmissionIndirectMeshletDispatchEnabled { get; set; }
        public bool SceneSubmissionGpuLodSelectionEnabled { get; set; }
        public float SceneSubmissionGpuLod1DistanceRatio { get; set; } = SceneSubmissionSettings.DefaultGpuLod1DistanceRatio;
        public float SceneSubmissionGpuLod2DistanceRatio { get; set; } = SceneSubmissionSettings.DefaultGpuLod2DistanceRatio;
        public bool SceneSubmissionGpuShadowCompactionEnabled { get; set; }
        public int SceneSubmissionGpuShadowLodBias { get; set; } = SceneSubmissionSettings.DefaultGpuShadowLodBias;
        public bool SceneSubmissionValidationCompareCpuGpuLists { get; set; }
        public bool SceneSubmissionGpuCompactionActive { get; set; }
        public string SceneSubmissionForwardPath { get; set; } = SceneSubmissionDiagnosticsPolicy.ForwardPathCpu;
        public string SceneSubmissionForwardTaskShader { get; set; } = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderLegacyCull;
        public string SceneSubmissionCompactionSkipReason { get; set; } = string.Empty;
        public string SceneSubmissionIndirectDispatchSkipReason { get; set; } = string.Empty;
        public string SceneSubmissionFallbackReason { get; set; } = string.Empty;
        public int SceneSubmissionGpuOpaqueCandidateCount { get; set; }
        public int SceneSubmissionGpuCompactedOpaqueMeshletCount { get; set; }
        public int SceneSubmissionGpuOpaqueFrustumRejectedCount { get; set; }
        public int SceneSubmissionGpuOpaqueOverflowCount { get; set; }
        public int SceneSubmissionGpuIndirectMeshletTaskCount { get; set; }
        public int SceneSubmissionGpuCompactedShadowMeshletCount { get; set; }
        public int SceneSubmissionGpuCompactedOpaqueCapacity { get; set; }
        public int SceneSubmissionGpuDepthSolidCandidateCount { get; set; }
        public int SceneSubmissionGpuDepthMaskedCandidateCount { get; set; }
        public int SceneSubmissionGpuCompactedSolidDepthMeshletCount { get; set; }
        public int SceneSubmissionGpuCompactedMaskedDepthMeshletCount { get; set; }
        public int SceneSubmissionGpuCompactedSolidDepthCapacity { get; set; }
        public int SceneSubmissionGpuCompactedMaskedDepthCapacity { get; set; }
        public int SceneSubmissionGpuDepthOverflowCount { get; set; }
        public int SceneSubmissionGpuDirectionalShadowCandidateCount { get; set; }
        public int SceneSubmissionGpuCompactedDirectionalShadowMeshletCount { get; set; }
        public int SceneSubmissionGpuDirectionalShadowOverflowCount { get; set; }
        public int SceneSubmissionGpuDirectionalShadowLodFallbackCount { get; set; }
        public int SceneSubmissionGpuLod0EmittedCount { get; set; }
        public int SceneSubmissionGpuLod1EmittedCount { get; set; }
        public int SceneSubmissionGpuLod2EmittedCount { get; set; }
        public int SceneSubmissionGpuMissingLodFallbackCount { get; set; }
        public int SceneSubmissionValidationValid { get; set; }
        public string SceneSubmissionValidationStatus { get; set; } = string.Empty;
        public int SceneSubmissionValidationCpuOpaqueCount { get; set; }
        public int SceneSubmissionValidationGpuOpaqueCount { get; set; }
        public int SceneSubmissionValidationComparedSampleCount { get; set; }
        public int SceneSubmissionValidationMismatchCount { get; set; }
        public int SceneSubmissionValidationSampleLimit { get; set; }
        public string SceneSubmissionValidationFirstMismatch { get; set; } = string.Empty;
        public BufferHandle SceneSubmissionOpaqueCompactedMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle SceneSubmissionSolidDepthCompactedMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle SceneSubmissionMaskedDepthCompactedMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle SceneSubmissionCounterBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle SceneSubmissionOpaqueIndirectDispatchBuffer { get; set; } = BufferHandle.Invalid;
        public ulong SceneSubmissionOpaqueCompactedMeshletDrawBufferSize { get; set; }
        public ulong SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize { get; set; }
        public ulong SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize { get; set; }
        public ulong SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize { get; set; }
        public ulong SceneSubmissionCounterBufferSize { get; set; }
        public ulong SceneSubmissionOpaqueIndirectDispatchBufferSize { get; set; }
        public int MeshletCountTotal { get; set; }
        public int MeshletCountSubmittedCpu { get; set; }
        public float AvgTrianglesPerSubmittedMeshlet { get; set; }
        public float AvgVerticesPerSubmittedMeshlet { get; set; }
        public int SmallMeshletsUnder16Triangles { get; set; }
        public int SmallMeshletsUnder32Triangles { get; set; }
        public int ScenePayloadRebuilt { get; set; }
        public ulong ObjectUploadBytes { get; set; }
        public ulong InstanceUploadBytes { get; set; }
        public ulong MeshletDrawUploadBytes { get; set; }
        public ulong SolidDepthMeshletDrawUploadBytes { get; set; }
        public ulong MaskedDepthMeshletDrawUploadBytes { get; set; }
        public ulong PackedMeshletDrawUploadBytes { get; set; }
        public ulong PackedSolidDepthMeshletDrawUploadBytes { get; set; }
        public ulong PackedMaskedDepthMeshletDrawUploadBytes { get; set; }
        public ulong TransparentMeshletDrawUploadBytes { get; set; }
        public ulong MaterialUploadBytes { get; set; }
        public ulong MaterialExtensionUploadBytes { get; set; }
        public ulong LightUploadBytes { get; set; }
        public uint HiZWidth { get; set; }
        public uint HiZHeight { get; set; }
        public bool BloomEnabled { get; set; }
        public bool DirectionalShadowPassEnabled { get; set; }
        public bool DirectionalShadowRecordSkipped { get; set; }
        public uint DirectionalShadowMapSize { get; set; }
        public int DirectionalShadowCascadeCount { get; set; }
        public float DirectionalShadowMaxDistance { get; set; }
        public float DirectionalShadowCascadeBlendFraction { get; set; }
        public int ShadowedDirectionalLightIndex { get; set; } = -1;
        public ShadowDebugView ShadowDebugView { get; set; } = ShadowDebugView.None;
        public int DirectionalShadowPreviewCascade { get; set; }
        public float ShadowNormalBias { get; set; }
        public float ShadowSlopeScaledDepthBias { get; set; }
        public int DirectionalShadowPcfRadius { get; set; }
        public int SpotShadowPcfRadius { get; set; }
        public int PointShadowPcfRadius { get; set; }
        public int ForwardShadowReceiverMeshletCount { get; set; }
        public int DirectionalShadowStaticCacheActiveMask { get; set; }
        public int DirectionalShadowStaticCacheValidMask { get; set; }
        public int DirectionalShadowStaticCacheRefreshMask { get; set; }
        public int DirectionalShadowStaticCacheReuseMask { get; set; }
        public int DirectionalShadowReceiverCountersReadbackValid { get; set; }
        public int DirectionalShadowReceiverUnresolvedCount { get; set; }
        public GPUShadowData ShadowData { get; set; }
        public bool SpotShadowsEnabled { get; set; }
        public bool SpotShadowRecordSkipped { get; set; }
        public int SpotShadowCandidateCount { get; set; }
        public int SpotShadowSelectedCount { get; set; }
        public int SpotShadowRejectedByBudgetCount { get; set; }
        public uint SpotShadowAtlasSize { get; set; }
        public uint SpotShadowTileSize { get; set; }
        public int SpotShadowAtlasCapacity { get; set; }
        public int SpotShadowAtlasUsedTiles { get; set; }
        public bool PointShadowsEnabled { get; set; }
        public bool PointShadowRecordSkipped { get; set; }
        public int PointShadowCandidateCount { get; set; }
        public int PointShadowSelectedCount { get; set; }
        public int PointShadowRejectedByBudgetCount { get; set; }
        public uint PointShadowMapSize { get; set; }
        public int PointShadowRenderedFaceCount { get; set; }
        public int PointShadowSkippedFaceCount { get; set; }
        public int LocalShadowMeshletCount { get; set; }
        public int DirectionalStaticShadowMeshletCount { get; set; }
        public int DirectionalDynamicShadowMeshletCount { get; set; }
        public int LocalStaticShadowMeshletCount { get; set; }
        public int LocalDynamicShadowMeshletCount { get; set; }
        public int DirectionalShadowSkinnedObjectCount { get; set; }
        public int LocalShadowSkinnedObjectCount { get; set; }
        public ulong DirectionalShadowMeshletDrawSignature { get; set; }
        public ulong LocalShadowMeshletDrawSignature { get; set; }
        public ulong DirectionalStaticShadowMeshletDrawSignature { get; set; }
        public ulong DirectionalDynamicShadowMeshletDrawSignature { get; set; }
        public ulong LocalStaticShadowMeshletDrawSignature { get; set; }
        public ulong LocalDynamicShadowMeshletDrawSignature { get; set; }
        public GPUSpotShadow[] SpotShadowData { get; set; } = [];
        public GPUPointShadow[] PointShadowData { get; set; } = [];
        public int[] PointShadowFaceMasks { get; set; } = [];
        public GPULocalLightShadowIndex[] LocalLightShadowIndices { get; set; } = [];
        public int[] DirectionalShadowMeshletCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverPrimarySelectionCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverProjectionRejectedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverUvDepthRejectedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverFallbackCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverTransitionBlendCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverPrimaryResolvedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverClearDepthFootprintCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverPrimaryFullyLitCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverPrimaryPartiallyShadowedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverPrimaryFullyShadowedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverFinalFullyLitCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverFinalPartiallyShadowedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] DirectionalShadowReceiverFinalFullyShadowedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public float[] DirectionalShadowReceiverAverageDepths { get; } = new float[ShadowSettings.MaxDirectionalCascades];
        public float[] DirectionalShadowReceiverAverageMinimumSampledDepths { get; } = new float[ShadowSettings.MaxDirectionalCascades];
        public float[] DirectionalShadowReceiverAverageMaximumSampledDepths { get; } = new float[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalStaticShadowCandidateCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalStaticShadowEmittedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalStaticShadowRejectedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalStaticShadowOverflowCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalStaticShadowCapacities { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalDynamicShadowRejectedCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalDynamicShadowOverflowCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public int[] SceneSubmissionGpuDirectionalDynamicShadowCapacities { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public uint BloomMipCount { get; set; }
        public uint BloomBaseWidth { get; set; }
        public uint BloomBaseHeight { get; set; }
        public bool AutoExposureEnabled { get; set; }
        public float EffectiveExposure { get; set; } = 1.0f;
        public float AutoExposureAverageLuminance { get; set; }
        public float AutoExposureTargetExposure { get; set; }
        public int AutoExposureSampleCount { get; set; }
        public int AutoExposureStateBufferIndex { get; set; }
        public int ActiveSceneColorTextureIndex { get; set; }
        public bool FogEnabled { get; set; }
        public FogMode FogMode { get; set; } = FogMode.Disabled;
        public FogColorMode FogColorMode { get; set; } = FogColorMode.ConstantColor;
        public FogDebugView FogDebugView { get; set; } = FogDebugView.None;
        public float FogDensity { get; set; }
        public float FogStartDistance { get; set; }
        public float FogEndDistance { get; set; }
        public float FogHeight { get; set; }
        public float FogHeightFalloff { get; set; }
        public float FogHeightDensity { get; set; }
        public float FogMaxOpacity { get; set; }
        public int FogDirectionalInscatteringEnabled { get; set; }
        public Vector3 FogDirectionalInscatteringDirection { get; set; } = Vector3.Zero;
        public uint FogWidth { get; set; }
        public uint FogHeightPixels { get; set; }
        public string FogFormat { get; set; } = string.Empty;
        public long GpuFogMicroseconds { get; set; }
        public bool ReflectionsEnabled { get; set; }
        public ReflectionMode ReflectionMode { get; set; } = ReflectionMode.Disabled;
        public ReflectionDebugView ReflectionDebugView { get; set; } = ReflectionDebugView.None;
        public int ReflectionProbeCount { get; set; }
        public int ReflectionProbeCapacity { get; set; }
        public int MaxReflectionProbesPerPixel { get; set; }
        public uint ReflectionProbeResolution { get; set; }
        public uint ReflectionProbeMipCount { get; set; }
        public ulong ReflectionProbeEstimatedBytes { get; set; }
        public int ReflectionProbeCapturesQueued { get; set; }
        public int ReflectionProbeCapturesCompleted { get; set; }
        public long CpuReflectionProbeUploadMicroseconds { get; set; }
        public long CpuReflectionProbeCaptureRecordMicroseconds { get; set; }
        public long CpuReflectionProbePrefilterRecordMicroseconds { get; set; }
        public long GpuReflectionProbeCaptureMicroseconds { get; set; }
        public long GpuReflectionProbePrefilterMicroseconds { get; set; }
        public bool AmbientOcclusionEnabled { get; set; }
        public AmbientOcclusionMode AmbientOcclusionMode { get; set; } = AmbientOcclusionMode.Disabled;
        public AmbientOcclusionDebugView AmbientOcclusionDebugView { get; set; } = AmbientOcclusionDebugView.None;
        public AmbientOcclusionForwardSamplingMode AmbientOcclusionForwardSamplingMode { get; set; } =
            AmbientOcclusionForwardSamplingMode.Disabled;
        public int AmbientOcclusionForwardDepthAwareSamples { get; set; }
        public uint AmbientOcclusionWidth { get; set; }
        public uint AmbientOcclusionHeight { get; set; }
        public string AmbientOcclusionFormat { get; set; } = string.Empty;
        public float AmbientOcclusionResolutionScale { get; set; }
        public float AmbientOcclusionRadius { get; set; }
        public float AmbientOcclusionIntensity { get; set; }
        public float AmbientOcclusionBias { get; set; }
        public int AmbientOcclusionSampleCount { get; set; }
        public int AmbientOcclusionBlurRadius { get; set; }
        public long CpuAmbientOcclusionRecordMicroseconds { get; set; }
        public long CpuAmbientOcclusionBlurRecordMicroseconds { get; set; }
        public long GpuAmbientOcclusionMicroseconds { get; set; }
        public long GpuAmbientOcclusionBlurMicroseconds { get; set; }
        public long CpuSsgiRecordMicroseconds { get; set; }
        public long CpuDdgiRecordMicroseconds { get; set; }
        public long CpuSimpleDdgiRecordMicroseconds { get; set; }
        public long CpuFarFieldRecordMicroseconds { get; set; }
        public long CpuGlobalIlluminationRecordMicroseconds { get; set; }
        public long CpuGlobalIlluminationRecordP95Microseconds { get; set; }
        public int GlobalIlluminationCpuTimingSampleCount { get; set; }
        public long CpuDdgiSchedulerMicroseconds { get; set; }
        public long CpuDdgiSchedulerP95Microseconds { get; set; }
        public long CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds { get; set; }
        public long CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds { get; set; }
        public long CpuDdgiSchedulerPhaseUninitializedMicroseconds { get; set; }
        public long CpuDdgiSchedulerPhaseFrustumMicroseconds { get; set; }
        public long CpuDdgiSchedulerPhaseSafetyMicroseconds { get; set; }
        public long CpuDdgiSchedulerPhaseRoundRobinMicroseconds { get; set; }
        public int CpuDdgiSchedulerCandidateInsertCount { get; set; }
        public int CpuDdgiSchedulerCandidateMaxShiftCount { get; set; }
        public int DdgiSchedulerTimingSampleCount { get; set; }
        public int DdgiSchedulerP95OverBudget { get; set; }
        public int SsgiHistoryValid { get; set; }
        public int SsgiRejectedHistoryPixelCount { get; set; }
        public int DdgiProbeVolumeCount { get; set; }
        public int DdgiProbeCount { get; set; }
        public int DdgiActiveProbeCount { get; set; }
        public int DdgiProbesUpdated { get; set; }
        public int DdgiRaysPerProbe { get; set; }
        public int DdgiMaxActiveProbeBudget { get; set; }
        public int DdgiMaxProbeUpdatesPerFrame { get; set; }
        public int DdgiProbeUpdateRequestBudget { get; set; }
        public int DdgiProbeUpdatePrimaryRayBudget { get; set; }
        public int DdgiScheduledRequestBudget { get; set; }
        public int DdgiScheduledPrimaryRayBudget { get; set; }
        public int DdgiGpuSchedulerPredictedRequestUpperBound { get; set; }
        public uint DdgiGpuSchedulerActualRequestCount { get; set; }
        public uint DdgiGpuSchedulerActualPrimaryRayCount { get; set; }
        public int DdgiGatherTileCount { get; set; }
        public int DdgiGatherTileCountX { get; set; }
        public int DdgiGatherTileCountY { get; set; }
        public int DdgiGatherSelectedLocalTileCount { get; set; }
        public int DdgiGatherSelectedClipmapTileCount { get; set; }
        public int DdgiGatherFallbackTileCount { get; set; }
        public float DdgiGatherSelectedLocalTileFraction { get; set; }
        public float DdgiGatherSelectedClipmapTileFraction { get; set; }
        public float DdgiGatherFallbackTileFraction { get; set; }
        public int DdgiForwardGatherFallbackUsed { get; set; }
        public int DdgiForwardGatherFallbackDisabled { get; set; }
        public int DdgiForwardGatherTileEmpty { get; set; }
        public float DdgiAverageSpatialCoverageEstimate { get; set; }
        public float DdgiAverageSupportCoverageEstimate { get; set; }
        public float DdgiAverageDataConfidenceEstimate { get; set; }
        public float DdgiAverageVisibilityConfidenceEstimate { get; set; }
        public float DdgiAverageLeakAttenuationEstimate { get; set; }
        public float DdgiAverageEffectiveContributionEstimate { get; set; }
        public float DdgiAverageOwnershipConsumedEstimate { get; set; }
        public DdgiRuntimeWarmupState DdgiWarmupState { get; set; } = DdgiRuntimeWarmupState.Disabled;
        public float DdgiWarmedVisibleProbeFraction { get; set; }
        public float DdgiWarmedLocalProbeFraction { get; set; }
        public float DdgiWarmedCascade0ProbeFraction { get; set; }
        public int DdgiForwardEstimateCountersReadbackValid { get; set; }
        public uint DdgiForwardEstimateSampleCount { get; set; }
        public uint DdgiForwardEstimateZeroVisibleButCoveredCount { get; set; }
        public uint DdgiForwardEstimateZeroEffectiveButCoveredCount { get; set; }
        public uint DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount { get; set; }
        public float DdgiForwardEstimateSampledIrradianceLuminance { get; set; }
        public float DdgiForwardEstimateRawDiffuseLuminance { get; set; }
        public float DdgiForwardEstimateFinalDiffuseLuminance { get; set; }
        public float DdgiForwardEstimateEnvironmentFallbackWeight { get; set; }
        public uint DdgiSupportRejectedInactiveCount { get; set; }
        public uint DdgiSupportRejectedZeroIrradianceAlphaCount { get; set; }
        public uint DdgiSupportRejectedLowQualityCount { get; set; }
        public float DdgiProbeIrradianceAlphaAverage { get; set; }
        public float DdgiProbeQualityXAverage { get; set; }
        public float DdgiProbeQualityYAverage { get; set; }
        public float DdgiProbeQualityZAverage { get; set; }
        public uint DdgiProbeQualitySampleCount { get; set; }
        public uint DdgiSampledProbeCurrentFrustumCount { get; set; }
        public uint DdgiSampledProbeSideRearCount { get; set; }
        public uint DdgiSampledProbeStaleAgeCount { get; set; }
        public uint DdgiClipmapInfoPrimaryAttemptCount { get; set; }
        public uint DdgiClipmapInfoPrimaryOkCount { get; set; }
        public uint DdgiClipmapInfoPrimaryFailedCount { get; set; }
        public float DdgiClipmapInfoPrimaryEdgeFadeAverage { get; set; }
        public float DdgiClipmapInfoPrimaryBlendWeightAverage { get; set; }
        public uint DdgiFastGatherAttemptCount { get; set; }
        public uint DdgiFastGatherAcceptedCount { get; set; }
        public uint DdgiFastGatherRejectedZeroSpatialCount { get; set; }
        public uint DdgiFastGatherRejectedZeroSupportCount { get; set; }
        public uint DdgiFastGatherRejectedZeroDataCount { get; set; }
        public uint DdgiFastGatherRejectedZeroOwnershipCount { get; set; }
        public uint DdgiShaderGatherFallbackAttemptCount { get; set; }
        public uint DdgiShaderGatherFallbackAcceptedCount { get; set; }
        public uint DdgiShaderGatherFallbackEmptyCount { get; set; }
        public uint DdgiTraceEnergySampleCount { get; set; }
        public uint DdgiTraceEnergyHitCount { get; set; }
        public uint DdgiTraceEnergyMissCount { get; set; }
        public float DdgiTraceEnergyRayLuminanceAverage { get; set; }
        public float DdgiTraceEnergyDirectLuminanceAverage { get; set; }
        public float DdgiTraceEnergyEmissiveLuminanceAverage { get; set; }
        public float DdgiTraceEnergyStableLuminanceAverage { get; set; }
        public float DdgiTraceEnergySkyLuminanceAverage { get; set; }
        public uint DdgiTraceEnergyHitZeroDirectCount { get; set; }
        public uint DdgiTraceEnergyHitWithDirectCount { get; set; }
        public float DdgiTraceEnergyDirectNoShadowLuminanceAverage { get; set; }
        public uint DdgiShadowVisibilityRayCount { get; set; }
        public uint DdgiShadowVisibilityOccludedCount { get; set; }
        public uint DdgiShadowVisibilityNearHitCount { get; set; }
        public float DdgiShadowVisibilityCommittedHitDistanceAverage { get; set; }
        public uint DdgiTraceEarlyOutDisabledCount { get; set; }
        public uint DdgiTraceEarlyOutBeyondRequestCount { get; set; }
        public uint DdgiTraceEarlyOutResolveBoundsCount { get; set; }
        public uint DdgiTraceEarlyOutResolveProbeRangeCount { get; set; }
        public uint DdgiTraceEarlyOutResolveClipmapCellCount { get; set; }
        public uint DdgiTraceEarlyOutResolveClipmapRingCount { get; set; }
        public uint DdgiTraceRingMismatchCorrectedCount { get; set; }
        public string DdgiTraceRingMismatchSample { get; set; } = string.Empty;
        public uint DdgiBlendEnergySampleCount { get; set; }
        public float DdgiBlendEnergyIrradianceLuminanceAverage { get; set; }
        public float DdgiBlendEnergyConfidenceAverage { get; set; }
        public uint DdgiBlendEnergyLowConfidenceCount { get; set; }
        public uint DdgiBlendEnergyNonzeroIrradianceCount { get; set; }
        public uint DdgiBlendEnergyNonFiniteIrradianceCount { get; set; }
        public uint DdgiBlendEnergyFireflySuppressedCount { get; set; }
        public uint SimpleDdgiTransportEnergySampleCount { get; set; }
        public uint SimpleDdgiTransportSourceCacheHitCount { get; set; }
        public uint SimpleDdgiTransportSourceCacheMissCount { get; set; }
        public float SimpleDdgiTransportBounceLuminanceAverage { get; set; }
        public float SimpleDdgiTransportSourceLuminanceAverage { get; set; }
        public float SimpleDdgiTransportTotalLuminanceAverage { get; set; }
        public uint DdgiTransparentReceiverSampleCount { get; set; }
        public float DdgiTransparentReceiverIrradianceLuminanceAverage { get; set; }
        public float DdgiTransparentReceiverFinalLuminanceAverage { get; set; }
        public uint DdgiDecalReceiverSampleCount { get; set; }
        public float DdgiDecalReceiverIrradianceLuminanceAverage { get; set; }
        public float DdgiDecalReceiverFinalLuminanceAverage { get; set; }
        public float DdgiVisibilityMomentMeanAverage { get; set; }
        public float DdgiVisibilityMomentVarianceAverage { get; set; }
        public float DdgiVisibilityProbeDistanceAverage { get; set; }
        public uint DdgiVisibilityMomentSampleCount { get; set; }
        public uint DdgiVisibilityLargeDistanceMarginCount { get; set; }
        public uint DdgiVisibilityZeroTransportCount { get; set; }
        public uint DdgiVisibilityZeroTransportWithIrradianceCount { get; set; }
        public float DdgiAverageRelocationFractionEstimate { get; set; }
        public int DdgiClassifiedInactiveProbeCountEstimate { get; set; }
        public DdgiQualityTier DdgiQualityTier { get; set; } = DdgiQualityTier.DdgiHigh;
        public float DdgiAdaptiveBudgetScale { get; set; } = 1.0f;
        public int DdgiAdaptiveBudgetReduced { get; set; }
        public int DdgiEmergencyDegradeActive { get; set; }
        public int DdgiEffectiveMaxShadedLights { get; set; }
        public string DdgiAdaptiveBudgetReason { get; set; } = string.Empty;
        public int GlobalIlluminationSsgiActive { get; set; }
        public int GlobalIlluminationDdgiActive { get; set; }
        public int SimpleDdgiActive { get; set; }
        public int SimpleDdgiProbeCount { get; set; }
        public int SimpleDdgiProbesUpdated { get; set; }
        public ulong SimpleDdgiRaysPerFrame { get; set; }
        /// <summary>V2 uses cached source radiance plus an explicit Jacobi transport solve.</summary>
        public int SimpleDdgiTransportV2Active { get; set; }
        public int SimpleDdgiAutomaticProbeDensityActive { get; set; }
        public int SimpleDdgiTransportSourceRefreshProbeCount { get; set; }
        public int SimpleDdgiTransportSourceCacheReuseProbeCount { get; set; }
        public ulong SimpleDdgiTransportSourceRayCount { get; set; }
        public ulong SimpleDdgiTransportSolveRayCount { get; set; }
        public int SimpleDdgiTransportPublishedProbeCount { get; set; }
        public int SimpleDdgiTransportPublishRegionCount { get; set; }
        public ulong SimpleDdgiTransportPublishedProbeTotal { get; set; }
        public ulong SimpleDdgiTransportPublishRegionTotal { get; set; }
        public ulong SimpleDdgiUpdateTransactionAbortCount { get; set; }
        public ulong SimpleDdgiTransportSourceCacheInvalidationCount { get; set; }
        public int SimpleDdgiTransportSolverInvalidationCount { get; set; }
        public float SimpleDdgiTransportSolverInvalidationsPerSourceRefresh { get; set; }
        public uint SimpleDdgiSourceLightingGeneration { get; set; }
        public uint SimpleDdgiTransportGeneration { get; set; }
        public int SimpleDdgiTransportSourceReadyProbeCount { get; set; }
        public int SimpleDdgiTransportSourceStaleProbeCount { get; set; }
        public int SimpleDdgiTransportConvergedProbeCount { get; set; }
        public int SimpleDdgiTransportPendingSolverProbeCount { get; set; }
        /// <summary>Global V2 warmup keeps every probe solving until the configured bounce floor is reached.</summary>
        public int SimpleDdgiTransportGlobalConvergencePending { get; set; }
        /// <summary>Age of the active field-wide V2 warmup; zero once local convergence policy resumes.</summary>
        public int SimpleDdgiTransportGlobalConvergenceElapsedFrames { get; set; }
        /// <summary>Monotonic live source/solver calibration changes that restarted V2 convergence.</summary>
        public ulong SimpleDdgiTransportCalibrationChangeCount { get; set; }
        public ulong SimpleDdgiTransportIrradianceAtlasBytes { get; set; }
        public ulong SimpleDdgiTransportSourceCacheBytes { get; set; }
        public float SimpleDdgiTransportSolverRelaxation { get; set; }
        public float SimpleDdgiTransportAlbedoClamp { get; set; }
        public float SimpleDdgiTransportResidualThreshold { get; set; }
        public int SimpleDdgiTransportMaximumSolverGenerations { get; set; }
        public int SimpleDdgiTransportSourceRefreshFrames { get; set; }
        public int SimpleDdgiInactiveProbeCount { get; set; }
        public int SimpleDdgiInactiveProbeSkipCount { get; set; }
        public ulong SimpleDdgiSavedRaysPerFrame { get; set; }
        public int SimpleDdgiLightingDirtyFrames { get; set; }
        public int SimpleDdgiLightingDirtyBoostedCapacity { get; set; }
        public uint SimpleDdgiDirtyReasonFlags { get; set; }
        public int SimpleDdgiFullRayProbeUpdateCount { get; set; }
        public int SimpleDdgiMaintenanceRayProbeUpdateCount { get; set; }
        public ulong SimpleDdgiAdaptiveRaySavedRaysPerFrame { get; set; }
        public int SimpleDdgiNearFullRayProbeUpdateCount { get; set; }
        public int SimpleDdgiMidFullRayProbeUpdateCount { get; set; }
        public int SimpleDdgiFarFullRayProbeUpdateCount { get; set; }
        public int SimpleDdgiNearMaintenanceRayProbeUpdateCount { get; set; }
        public int SimpleDdgiMidMaintenanceRayProbeUpdateCount { get; set; }
        public int SimpleDdgiFarMaintenanceRayProbeUpdateCount { get; set; }
        public ulong SimpleDdgiNearScheduledPrimaryRayCount { get; set; }
        public ulong SimpleDdgiMidScheduledPrimaryRayCount { get; set; }
        public ulong SimpleDdgiFarScheduledPrimaryRayCount { get; set; }
        public int SimpleDdgiDirtyFirstUpdateLatencySampleCount { get; set; }
        public int SimpleDdgiDirtyFirstUpdateLatencyP50Frames { get; set; }
        public int SimpleDdgiDirtyFirstUpdateLatencyP95Frames { get; set; }
        public int SimpleDdgiDirtyFirstUpdateLatencyMaxFrames { get; set; }
        public uint SimpleDdgiOldestVisibleUnsupportedProbeAge { get; set; }
        public int SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget { get; set; }
        public int SimpleDdgiVisibleZeroSupportRepairUpdateCount { get; set; }
        public int SimpleDdgiProbeLifecycleLatencyTargetFrames { get; set; }
        public uint SimpleDdgiMaximumFreshProbeAge { get; set; }
        public uint SimpleDdgiMaximumScrollExposedProbeAge { get; set; }
        public uint SimpleDdgiMaximumRelocationPendingProbeAge { get; set; }
        public uint SimpleDdgiMaximumUnpublishedProbeAge { get; set; }
        public int SimpleDdgiProbeLifecycleBoundExceededCount { get; set; }
        public int SimpleDdgiDirtyConvergenceLatencySampleCount { get; set; }
        public int SimpleDdgiDirtyConvergenceLatencyP50Frames { get; set; }
        public int SimpleDdgiDirtyConvergenceLatencyP95Frames { get; set; }
        public int SimpleDdgiDirtyConvergenceLatencyMaxFrames { get; set; }
        public ulong SimpleDdgiAtlasBytes { get; set; }
        public int SimpleDdgiSampledAtlasRequested { get; set; }
        public int SimpleDdgiSampledAtlasActive { get; set; }
        public int SimpleDdgiSampledAtlasGroupCount { get; set; }
        public int SimpleDdgiSampledAtlasLayersPerTexture { get; set; }
        public ulong SimpleDdgiSampledAtlasImageBytes { get; set; }
        public string SimpleDdgiSampledAtlasFallbackReason { get; set; } = string.Empty;
        public int FarFieldPagedMode { get; set; }
        public int FarFieldPagePoolCapacity { get; set; }
        public int FarFieldResidentPageCount { get; set; }
        public int FarFieldPendingPageCount { get; set; }
        public int FarFieldPageRequestCount { get; set; }
        public int FarFieldPageMissCount { get; set; }
        public int FarFieldPageRebuildCount { get; set; }
        public int FarFieldPageEvictionCount { get; set; }
        public int FarFieldScheduledPageBakeCount { get; set; }
        public ulong FarFieldCacheBytes { get; set; }
        public ulong FarFieldMemoryBudgetBytes { get; set; }
        public ulong FarFieldInstanceBufferBytes { get; set; }
        public ulong FarFieldPageTableBytes { get; set; }
        public int SimpleDdgiRecentered { get; set; }
        public int SimpleDdgiAtlasPreservedOnRecenter { get; set; }
        public int SimpleDdgiAtlasCleared { get; set; }
        public int SimpleDdgiAtlasFresh { get; set; }
        public int SimpleDdgiRecenterCount { get; set; }
        public int SimpleDdgiAtlasClearCount { get; set; }
        public int SimpleDdgiAtlasPreserveOnRecenterCount { get; set; }
        public int SimpleDdgiFramesSinceLastClear { get; set; }
        public int SimpleDdgiFramesSinceLastRecenter { get; set; }
        public int DdgiInvestigationCountersReadbackValid { get; set; }
        public uint SimpleDdgiFreshAtlasForwardSampleCount { get; set; }
        public uint SimpleDdgiZeroIrradianceSampleCount { get; set; }
        public uint SimpleDdgiNonzeroIrradianceSampleCount { get; set; }
        public float SimpleDdgiAverageSampledIrradianceLuminance { get; set; }
        public float SimpleDdgiAverageVisibility { get; set; }
        public uint SimpleDdgiLowVisibilitySampleCount { get; set; }
        public uint SimpleDdgiGatherSampleCount { get; set; }
        public uint SimpleDdgiSecondVolumeGatherCount { get; set; }
        public IReadOnlyList<uint> SimpleDdgiGatherPrimaryRejectionCounts { get; set; } = Array.Empty<uint>();
        public IReadOnlyList<uint> SimpleDdgiGatherFallbackRejectionCounts { get; set; } = Array.Empty<uint>();
        public IReadOnlyList<uint> SimpleDdgiGatherRecoveryRejectionCounts { get; set; } = Array.Empty<uint>();
        public uint SimpleDdgiGatherPrimaryAllFailedCount { get; set; }
        public uint SimpleDdgiGatherFallbackAllFailedCount { get; set; }
        public uint SimpleDdgiGatherRecoveryAllFailedCount { get; set; }
        public int DdgiFullRefreshFrameCount { get; set; }
        public int DdgiPartialRefreshFrameCount { get; set; }
        public float DdgiUpdatedProbeFraction { get; set; }
        public int DdgiProbeUpdateStartIndex { get; set; }
        public int DdgiProbeUpdateEndIndex { get; set; }
        public int DdgiSkippedProbeCount { get; set; }
        public float DdgiFramesSinceProbeUpdatedP50 { get; set; }
        public float DdgiFramesSinceProbeUpdatedP95 { get; set; }
        public float DdgiFramesSinceProbeUpdatedMax { get; set; }
        public int DdgiNewlyInvalidatedProbeCount { get; set; }
        public int DdgiRefreshReasonRecenterProbeCount { get; set; }
        public int DdgiRefreshReasonDirtyProbeCount { get; set; }
        public int DdgiRefreshReasonAgeProbeCount { get; set; }
        public int DdgiRefreshReasonVisibilityProbeCount { get; set; }
        public int DdgiRefreshReasonFullRefreshProbeCount { get; set; }
        public uint DdgiForwardSimplePathSampleCount { get; set; }
        public uint DdgiForwardLegacyPathSampleCount { get; set; }
        public uint DdgiForwardZeroFinalIndirectCount { get; set; }
        public uint DdgiForwardZeroDdgiButNonzeroIblCount { get; set; }
        public uint DdgiForwardZeroDdgiAndZeroIblCount { get; set; }
        public uint DdgiForwardOutOfGridSampleCount { get; set; }
        public uint DdgiForwardClampedProbeSampleCount { get; set; }
        public uint DdgiForwardNanOrInfSampleCount { get; set; }
        public uint DdgiIrradianceAtlasZeroTexelSampleCount { get; set; }
        public uint DdgiVisibilityAtlasZeroMomentSampleCount { get; set; }
        public uint DdgiAtlasWriteProbeCount { get; set; }
        public uint DdgiAtlasWriteTexelCount { get; set; }
        public uint DdgiBlendZeroRayWeightProbeCount { get; set; }
        public uint DdgiBlendNonzeroIrradianceProbeCount { get; set; }
        public uint DdgiBlendPreviousAtlasUsedCount { get; set; }
        public uint DdgiBlendHysteresisZeroFrameCount { get; set; }
        public uint DdgiSimpleTraceHitCount { get; set; }
        public uint DdgiSimpleTraceMissCount { get; set; }
        public uint DdgiSimpleTraceZeroRadianceHitCount { get; set; }
        public uint DdgiSimpleTraceDirectLightHitCount { get; set; }
        public uint DdgiSimpleTraceEmissiveHitCount { get; set; }
        public uint DdgiSimpleTraceFarFieldHitCount { get; set; }
        public uint DdgiSimpleTraceFarFieldMissCount { get; set; }
        public uint DdgiSimpleTraceTlasUnavailableFrameCount { get; set; }
        public uint SimpleDdgiSkyVisibilitySampleCount { get; set; }
        public float SimpleDdgiAverageSkyVisibility { get; set; }
        public uint FarFieldSunShadowSampleCount { get; set; }
        public uint FarFieldSunShadowOccludedCount { get; set; }
        public uint SimpleDdgiRoughSpecularSampleCount { get; set; }
        public uint SimpleDdgiRoughSpecularNonzeroCount { get; set; }
        public uint DdgiSimpleTraceFarFieldStepBucket0Count { get; set; }
        public uint DdgiSimpleTraceFarFieldStepBucket1Count { get; set; }
        public uint DdgiSimpleTraceFarFieldStepBucket2Count { get; set; }
        public uint DdgiSimpleTraceFarFieldStepBucket3Count { get; set; }
        public uint DdgiSimpleTraceFarFieldStepBucket4Count { get; set; }
        /// <summary>
        /// Fence-complete sparse weighted transport-hit provenance from the
        /// previous use of this frame slot. These counters are safe to consume
        /// from a graphics diagnostic pass without a live compute-buffer hazard.
        /// </summary>
        public uint MaterialDetailedTransportHitCount { get; set; }
        public uint MaterialCompactTransportHitCount { get; set; }
        public uint MaterialCorrectnessFallbackHitCount { get; set; }
        public uint MaterialFarFieldTransportHitCount { get; set; }
        public int DdgiBlackFrameSuspect { get; set; }
        public int DdgiBlackFrameAfterRecenter { get; set; }
        public int DdgiBlackFrameAfterAtlasClear { get; set; }
        public int DdgiBlackFrameDuringFreshAtlas { get; set; }
        public DdgiCameraMovementClass DdgiBlackFrameMovementClass { get; set; } = DdgiCameraMovementClass.None;
        public int DdgiAsyncComputeEnabled { get; set; }
        public ulong DdgiAtlasMemoryBudgetBytes { get; set; }
        public int DdgiProbeRelocationCount { get; set; }
        public int DdgiProbeClassificationCount { get; set; }
        public int DdgiCascadeCount { get; set; }
        public int DdgiScrollCount { get; set; }
        public int DdgiNewProbeCount { get; set; }
        public int DdgiDirtyBoundsProbeUpdateCount { get; set; }
        public int DdgiVisibleFrustumProbeUpdateCount { get; set; }
        public int DdgiOutsideFrustumSafetyProbeUpdateCount { get; set; }
        public int DdgiAgeRefreshProbeUpdateCount { get; set; }
        public int DdgiHighVarianceProbeUpdateCount { get; set; }
        public int DdgiLowConfidenceProbeUpdateCount { get; set; }
        public int DdgiStableProbeUpdateCount { get; set; }
        public float DdgiAverageProbeVariability { get; set; }
        public float DdgiAverageProbeConfidence { get; set; }
        public ulong DdgiScheduledPrimaryRayCount { get; set; }
        public ulong DdgiEstimatedShadowRayUpperBound { get; set; }
        public ulong DdgiSelectedDirectionalHitCount { get; set; }
        public ulong DdgiSelectedLocalHitCount { get; set; }
        public ulong DdgiVisibilityRayCount { get; set; }
        public ulong DdgiSkippedLocalLightCount { get; set; }
        public string DdgiLightSelectionMode { get; set; } = string.Empty;
        public int DdgiPrimaryDirectionalLightIndex { get; set; } = -1;
        public int DdgiSelectedLocalLightIndex { get; set; } = -1;
        public float DdgiSelectedLocalLightEnergyScale { get; set; } = 1.0f;
        public int DdgiEmissiveSourceCount { get; set; }
        public uint DdgiEmissiveSourceRevision { get; set; }
        public string DdgiEmissiveSamplingMode { get; set; } = string.Empty;
        public int DdgiEmissiveTriangleCandidateCount { get; set; }
        public int DdgiEmissiveTriangleBudget { get; set; }
        public float DdgiEmissiveSkippedEnergyFraction { get; set; }
        public int DdgiEmissiveSkippedSkinnedObjectCount { get; set; }
        public double DdgiEmissiveSkippedSkinnedImportance { get; set; }
        public int DdgiEmissiveTableCacheHit { get; set; }
        public ulong DdgiEmissiveTableCacheHitCount { get; set; }
        public ulong DdgiEmissiveTableCacheMissCount { get; set; }
        public ulong DdgiEmissiveTableRebuildCount { get; set; }
        public ulong DdgiEmissiveTableInvalidationCount { get; set; }
        public ulong DdgiEmissiveTableUploadCount { get; set; }
        public ulong DdgiProbeVolumeBufferBytes { get; set; }
        public ulong DdgiProbeStateBufferBytes { get; set; }
        public ulong DdgiProbeUpdateQueueBytes { get; set; }
        public ulong DdgiProbeRelocationClassificationBytes { get; set; }
        public ulong DdgiCurrentIrradianceAtlasBytes { get; set; }
        public ulong DdgiCurrentVisibilityAtlasBytes { get; set; }
        public ulong DdgiGatherTileBufferBytes { get; set; }
        public ulong DdgiLocalSlotReservedPoolBytes { get; set; }
        public ulong DdgiGpuSchedulerBufferBytes { get; set; }
        public int DdgiGpuSchedulerDirtyRegionCapacity { get; set; }
        public int DdgiGpuSchedulerCandidateCapacity { get; set; }
        public int DdgiGpuSchedulerGroupCountCapacity { get; set; }
        public int DdgiGpuSchedulerPrefixCapacity { get; set; }
        public int DdgiGpuSchedulerDirtyRegionCount { get; set; }
        public int DdgiGpuSchedulerDirtyRegionOverflowCount { get; set; }
        public int DdgiGpuSchedulerResourceReinitializationCount { get; set; }
        public int DdgiGpuSchedulerTotalResourceReinitializationCount { get; set; }
        public ulong DdgiGpuSchedulerUploadBytes { get; set; }
        public int DdgiGpuSchedulerReadbackValid { get; set; }
        public int DdgiGpuSchedulerReadbackLatencyFrames { get; set; }
        public int DdgiGpuSchedulerFallbackActive { get; set; }
        public string DdgiGpuSchedulerFallbackReason { get; set; } = string.Empty;
        public int DdgiGpuSchedulerConsideredProbeCount { get; set; }
        public uint DdgiGpuSchedulerRequestCount { get; set; }
        public uint DdgiGpuSchedulerPrimaryRayCount { get; set; }
        public uint DdgiGpuSchedulerCandidateCount { get; set; }
        public uint DdgiGpuSchedulerOverflowCount { get; set; }
        public uint DdgiGpuSchedulerCandidateBufferOverflowCount { get; set; }
        public uint DdgiGpuSchedulerPerBucketOverflowCount { get; set; }
        public uint DdgiGpuSchedulerDuplicateRequestCount { get; set; }
        public uint DdgiGpuSchedulerBudgetRejectedCount { get; set; }
        public uint DdgiGpuSchedulerRequestBudgetRejectedCount { get; set; }
        public uint DdgiGpuSchedulerPrimaryRayBudgetRejectedCount { get; set; }
        public uint DdgiGpuSchedulerInvalidProbeCount { get; set; }
        public int DdgiGpuSchedulerCandidateOutputCapacity { get; set; }
        public int DdgiGpuSchedulerFullScan { get; set; }
        public uint DdgiGpuSchedulerVisibleFrustumCandidateCount { get; set; }
        public uint DdgiGpuSchedulerSafetyShellCandidateCount { get; set; }
        public uint DdgiGpuSchedulerAgeRefreshCandidateCount { get; set; }
        public uint DdgiGpuSchedulerHighVarianceCandidateCount { get; set; }
        public uint DdgiGpuSchedulerLowConfidenceCandidateCount { get; set; }
        public uint DdgiGpuSchedulerStableSkippedCount { get; set; }
        public uint DdgiGpuSchedulerPriority0RequestCount { get; set; }
        public uint DdgiGpuSchedulerPriority1RequestCount { get; set; }
        public uint DdgiGpuSchedulerPriority2RequestCount { get; set; }
        public uint DdgiGpuSchedulerPriority3RequestCount { get; set; }
        public uint DdgiGpuSchedulerPriorityBucketMismatchSkipCount { get; set; }
        public int DdgiGpuSchedulerRequestBudgetSaturated { get; set; }
        public int DdgiGpuSchedulerPrimaryRayBudgetSaturated { get; set; }
        public int DdgiGpuSchedulerValidationValid { get; set; }
        public string DdgiGpuSchedulerValidationStatus { get; set; } = string.Empty;
        public int DdgiGpuSchedulerValidationCpuRequestCount { get; set; }
        public uint DdgiGpuSchedulerValidationGpuRequestCount { get; set; }
        public int DdgiGpuSchedulerValidationComparedRequestCount { get; set; }
        public int DdgiGpuSchedulerValidationMismatchCount { get; set; }
        public int DdgiGpuSchedulerValidationSampleLimit { get; set; }
        public string DdgiGpuSchedulerValidationFirstMismatch { get; set; } = string.Empty;
        public uint DdgiTraceDispatchGroupCount { get; set; }
        public uint DdgiTraceProbeCount { get; set; }
        public uint DdgiTraceRayCount { get; set; }
        public uint DdgiBlendProbeCount { get; set; }
        public uint DdgiRelocateClassifyProbeCount { get; set; }
        public uint DdgiPublishProbeCount { get; set; }
        public int DdgiUpdateExecuted { get; set; }
        public string DdgiUpdateSkipReason { get; set; } = string.Empty;
        public ulong DdgiRayScratchBytes { get; set; }
        public ulong DdgiUpdatedAtlasBytes { get; set; }
        public int DdgiPublishExecuted { get; set; }
        public string DdgiPublishSkipReason { get; set; } = string.Empty;
        public int DdgiPublishedCacheLatencyFrames { get; set; }
        public uint DdgiCacheGeneration { get; set; }
        public ulong DdgiLastUpdatedFrameSerial { get; set; }
        public DdgiRuntimeWarmupState DdgiCacheWarmupState { get; set; } = DdgiRuntimeWarmupState.Disabled;
        public int DdgiStaleProbeCount { get; set; }
        public float DdgiAverageProbeAge { get; set; }
        public ulong DdgiMaxProbeAge { get; set; }
        public float DdgiFrustumUpdatePercentage { get; set; }
        public float DdgiOutsideFrustumUpdatePercentage { get; set; }
        public int DdgiResourceReinitializationCount { get; set; }
        public int DdgiTotalResourceReinitializationCount { get; set; }
        public int DdgiActiveLocalSlotCount { get; set; }
        public int DdgiLocalSlotGeneration { get; set; }
        public ulong DdgiLocalSlotInitBytes { get; set; }
        public string DdgiLocalVolumeEvictionReason { get; set; } = string.Empty;
        public string DdgiCacheClearReason { get; set; } = string.Empty;
        public DdgiCameraMovementClass DdgiCameraMovementClass { get; set; } = DdgiCameraMovementClass.None;
        public ulong DdgiTextureBytes { get; set; }
        public ulong DdgiBufferBytes { get; set; }
        public long GpuSsgiTraceMicroseconds { get; set; }
        public long GpuSsgiTemporalMicroseconds { get; set; }
        public long GpuSsgiDenoiseMicroseconds { get; set; }
        public long GpuDdgiScheduleMicroseconds { get; set; }
        public long GpuDdgiScheduleP95Microseconds { get; set; }
        public int GpuDdgiScheduleOverBudget { get; set; }
        public long GpuDdgiScheduleResetMicroseconds { get; set; }
        public long GpuDdgiScheduleScoreMicroseconds { get; set; }
        public long GpuDdgiSchedulePrefixMicroseconds { get; set; }
        public long GpuDdgiScheduleCompactMicroseconds { get; set; }
        public long GpuDdgiScheduleFinalizeMicroseconds { get; set; }
        public long GpuDdgiScheduleReadbackMicroseconds { get; set; }
        public long GpuDdgiScheduleBarrierMicroseconds { get; set; }
        public long GpuDdgiTraceMicroseconds { get; set; }
        public long GpuDdgiBlendMicroseconds { get; set; }
        public long GpuDdgiRelocateClassifyMicroseconds { get; set; }
        public long GpuDdgiPublishMicroseconds { get; set; }
        public long GpuDdgiUpdateMicroseconds { get; set; }
        public long GpuSimpleDdgiTraceMicroseconds { get; set; }
        public long GpuSimpleDdgiTransportMicroseconds { get; set; }
        public long GpuSimpleDdgiBlendMicroseconds { get; set; }
        public long GpuFarFieldUpdateMicroseconds { get; set; }
        public int GpuFarFieldUpdateTimingValid { get; set; }
        public long GpuGiCompositeMicroseconds { get; set; }
        public long CpuAccelerationStructureBuildMicroseconds { get; set; }
        public long CpuAccelerationStructureBlasBuildMicroseconds { get; set; }
        public long CpuAccelerationStructureTlasBuildMicroseconds { get; set; }
        public long CpuAccelerationStructureInstanceUploadMicroseconds { get; set; }
        public long GpuAccelerationStructureBlasMicroseconds { get; set; }
        public long GpuAccelerationStructureTlasMicroseconds { get; set; }
        public int AccelerationStructureBottomLevelCount { get; set; }
        public int AccelerationStructureTopLevelInstanceCount { get; set; }
        public int AccelerationStructureBlasBuildCount { get; set; }
        public int AccelerationStructureTlasBuildCount { get; set; }
        public int AccelerationStructureTlasUpdateCount { get; set; }
        public int AccelerationStructureTlasSkipCount { get; set; }
        public int AccelerationStructureStreamingEnabled { get; set; }
        public int AccelerationStructureStaticInstanceCandidateCount { get; set; }
        public int AccelerationStructureStaticInstanceResidentCount { get; set; }
        public int AccelerationStructureStaticInstanceCulledCount { get; set; }
        public int AccelerationStructureBlasEvictionCount { get; set; }
        public ulong AccelerationStructureBlasEvictionBytes { get; set; }
        public int AccelerationStructureBlasBudgetRejectedCount { get; set; }
        public ulong AccelerationStructureBlasBytes { get; set; }
        public ulong AccelerationStructureTlasBytes { get; set; }
        public ulong AccelerationStructureRetiredBytes { get; set; }
        public ulong AccelerationStructureResidentBytes { get; set; }
        public ulong AccelerationStructureMemoryBudgetBytes { get; set; }
        public ulong AccelerationStructureBytes { get; set; }
        public ulong AccelerationStructureScratchBytes { get; set; }
        public ulong AccelerationStructureInstanceBufferBytes { get; set; }
        public ulong AccelerationStructureRayQueryMetadataBytes { get; set; }
        public ulong AccelerationStructureInstanceUploadBytes { get; set; }
        public ulong AccelerationStructureRayQueryMetadataUploadBytes { get; set; }
        public string AccelerationStructureFallbackReason { get; set; } = string.Empty;
        public AntiAliasingMode AntiAliasingMode { get; set; } = AntiAliasingMode.None;
        public AntiAliasingDebugView AntiAliasingDebugView { get; set; } = AntiAliasingDebugView.None;
        public uint AntiAliasingWidth { get; set; }
        public uint AntiAliasingHeight { get; set; }
        public string AntiAliasingInputFormat { get; set; } = string.Empty;
        public string AntiAliasingOutputFormat { get; set; } = string.Empty;
        public long CpuFxaaRecordMicroseconds { get; set; }
        public long CpuSmaaEdgeRecordMicroseconds { get; set; }
        public long CpuSmaaBlendRecordMicroseconds { get; set; }
        public long CpuSmaaNeighborhoodRecordMicroseconds { get; set; }
        public long CpuMotionVectorRecordMicroseconds { get; set; }
        public long GpuMotionVectorMicroseconds { get; set; }
        public long GpuAntiAliasingMicroseconds { get; set; }
        public int SmaaLookupTexturesReady { get; set; }
        public int MotionVectorsEnabled { get; set; }
        public int JitterEnabled { get; set; }
        public float JitterX { get; set; }
        public float JitterY { get; set; }
        public ulong ObjectBufferSize { get; set; }
        public ulong MaterialBufferSize { get; set; }
        public ulong MaterialExtensionBufferSize { get; set; }
        public ulong InstanceBufferSize { get; set; }
        public ulong MeshletDrawBufferSize { get; set; }
        public ulong FullOpaqueMeshletDrawBufferSize { get; set; }
        public ulong SimpleNormalOpaqueMeshletDrawBufferSize { get; set; }
        public ulong SolidDepthMeshletDrawBufferSize { get; set; }
        public ulong MaskedDepthMeshletDrawBufferSize { get; set; }
        public ulong PackedMeshletDrawBufferSize { get; set; }
        public ulong PackedFullOpaqueMeshletDrawBufferSize { get; set; }
        public ulong PackedSimpleNormalOpaqueMeshletDrawBufferSize { get; set; }
        public ulong PackedSolidDepthMeshletDrawBufferSize { get; set; }
        public ulong PackedMaskedDepthMeshletDrawBufferSize { get; set; }
        public ulong MeshletTaskFrameDataBufferSize { get; set; }
        public ulong TransparentMeshletDrawBufferSize { get; set; }
        public ulong DirectionalShadowMeshletDrawBufferSize { get; set; }
        public ulong LocalShadowMeshletDrawBufferSize { get; set; }
        public ulong TiledLightHeaderBufferSize { get; set; }
        public ulong TiledLightIndexBufferSize { get; set; }
        public ulong TiledLightHeaderBufferClearBytes { get; set; }
        public ulong TiledLightIndexBufferClearBytes { get; set; }
        public BufferHandle ObjectDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MaterialDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MaterialExtensionDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle InstanceBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle FullOpaqueMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle SimpleNormalOpaqueMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle SolidDepthMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MaskedDepthMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle PackedMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle PackedFullOpaqueMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle PackedSimpleNormalOpaqueMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle PackedSolidDepthMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle PackedMaskedDepthMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MeshletTaskFrameDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TransparentMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightHeaderBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightIndexBuffer { get; set; } = BufferHandle.Invalid;
        public float Time { get; set; }
        public bool DebugToolingEnabled { get; set; }
        public DebugOverlayMode DebugOverlayMode { get; set; } = DebugOverlayMode.None;
        public bool CpuDebugSnapshotsEnabled { get; set; }
        public int DebugSelectedObjectIndex { get; set; } = -1;
        public string DebugSelectedObjectName { get; set; } = string.Empty;
        public DebugDrawFrameSnapshot DebugDrawSnapshot { get; set; } = DebugDrawFrameSnapshot.Empty;
        public long CpuDebugDrawBuildMicroseconds { get; set; }
        public long CpuDebugDrawRecordMicroseconds { get; set; }
        public long GpuDebugDrawMicroseconds { get; set; }
        public long CpuDebugOverlayRecordMicroseconds { get; set; }
        public long GpuDebugOverlayMicroseconds { get; set; }
        public int DebugObjectBoundsDrawn { get; set; }
        public int DebugMeshletBoundsDrawn { get; set; }
        public int DebugMeshletBoundsDropped { get; set; }
        public int DebugReflectionProbeVolumesDrawn { get; set; }
        public int DebugDdgiProbeVolumesDrawn { get; set; }
        public int DebugDecalVolumesDrawn { get; set; }

        public bool HasCpuSnapshots { get; set; }
        public List<GPUMeshletDrawCommand> MeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> OpaqueMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> FullOpaqueMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> SimpleNormalOpaqueMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> SolidDepthMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> MaskedDepthMeshletDrawCommands { get; } = new();
        public List<GPUPackedMeshletDrawCommand> PackedMeshletDrawCommands { get; } = new();
        public List<GPUPackedMeshletDrawCommand> PackedFullOpaqueMeshletDrawCommands { get; } = new();
        public List<GPUPackedMeshletDrawCommand> PackedSimpleNormalOpaqueMeshletDrawCommands { get; } = new();
        public List<GPUPackedMeshletDrawCommand> PackedSolidDepthMeshletDrawCommands { get; } = new();
        public List<GPUPackedMeshletDrawCommand> PackedMaskedDepthMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> TransparentMeshletDrawCommands { get; } = new();
        public List<GPUObjectData> ObjectData { get; } = new();
        public List<GPUMaterialData> MaterialData { get; } = new();
        public List<GPUMaterialExtensionData> MaterialExtensionData { get; } = new();
        public List<GPUSkinningDispatch> SkinningDispatches { get; } = new();
        public List<GPUParticleBatch> ParticleBatches { get; } = new();
        public List<DdgiVolumeDiagnosticsEntry> DdgiVolumeDiagnostics { get; } = new();
        public List<ObjectDebugSnapshot> ObjectDebugSnapshots { get; } = new();

        private bool _disposed = false;

        public void Clear()
        {
            MeshletDrawCommands.Clear();
            OpaqueMeshletDrawCommands.Clear();
            FullOpaqueMeshletDrawCommands.Clear();
            SimpleNormalOpaqueMeshletDrawCommands.Clear();
            SolidDepthMeshletDrawCommands.Clear();
            MaskedDepthMeshletDrawCommands.Clear();
            PackedMeshletDrawCommands.Clear();
            PackedFullOpaqueMeshletDrawCommands.Clear();
            PackedSimpleNormalOpaqueMeshletDrawCommands.Clear();
            PackedSolidDepthMeshletDrawCommands.Clear();
            PackedMaskedDepthMeshletDrawCommands.Clear();
            TransparentMeshletDrawCommands.Clear();
            ObjectData.Clear();
            MaterialData.Clear();
            MaterialExtensionData.Clear();
            SkinningDispatches.Clear();
            ParticleBatches.Clear();
            ObjectDebugSnapshots.Clear();
            FrameIndex = 0;
            TemporalSampleIndex = 0;
            DdgiFrameSerial = 0;
            SceneContentRevision = 0;
            CaptureCameraYawRadians = 0;
            CaptureCameraPitchRadians = 0;
            CaptureCameraFieldOfViewRadians = 0;
            CaptureCameraNearPlane = 0;
            CaptureCameraFarPlane = 0;
            CaptureCameraCutSerial = 0;
            CaptureFramesSinceSceneLoad = 0;
            CaptureSceneName = "unknown-scene";
            CaptureScenario = string.Empty;
            ImageIndex = 0;
            ObjectCount = 0;
            MeshletCount = 0;
            StaticInstanceBatchCount = 0;
            StaticInstanceCount = 0;
            VisibleStaticInstanceCount = 0;
            CulledStaticInstanceCount = 0;
            StaticBatchMeshletDrawCommandCount = 0;
            CpuStaticBatchBuildMicroseconds = 0;
            OpaqueObjectCount = 0;
            SolidObjectCount = 0;
            MaskedObjectCount = 0;
            TransparentObjectCount = 0;
            GeometryDecalObjectCount = 0;
            OpaqueMeshletCount = 0;
            SolidMeshletCount = 0;
            MaskedMeshletCount = 0;
            TransparentMeshletCount = 0;
            GeometryDecalMeshletCount = 0;
            BlendMaterialCount = 0;
            MaskMaterialCount = 0;
            GeometryDecalMaterialCount = 0;
            TransparentSortCandidateCount = 0;
            TransparentSortMicroseconds = 0;
            TransparentOverflowCount = 0;
            MaterialCount = 0;
            LightCount = 0;
            DirectionalLightCount = 0;
            LocalLightCount = 0;
            TextureCount = 0;
            TransparentPassEnabled = true;
            TransparencyMode = TransparencyMode.SortedAlphaBlend;
            TransparencyDebugView = TransparencyDebugView.None;
            TransparentReceiveShadows = true;
            TransparentReceiveGlobalIllumination = true;
            TransparentDdgiReceiverCountersEnabled = false;
            DecalDebugView = DecalDebugView.None;
            GeometryDecalsEnabled = true;
            DecalReceiveGlobalIllumination = true;
            GeometryDecalDepthBias = 0.0005f;
            GeometryDecalSlopeScaledDepthBias = 0f;
            AnimationEnabled = false;
            AnimationSkinningMode = AnimationSkinningMode.Disabled;
            AnimationDebugView = AnimationDebugView.None;
            AnimatedModelCount = 0;
            SkinnedObjectCount = 0;
            SkeletonCount = 0;
            SkinCount = 0;
            AnimationClipCount = 0;
            ActiveAnimatorCount = 0;
            PlayingAnimatorCount = 0;
            PausedAnimatorCount = 0;
            SkinnedVertexCount = 0;
            SkinningDispatchCount = 0;
            JointMatrixCount = 0;
            MaxJointsPerSkeleton = 0;
            CpuAnimationSampleMicroseconds = 0;
            CpuSkinMatrixUploadMicroseconds = 0;
            CpuSkinningRecordMicroseconds = 0;
            GpuSkinningMicroseconds = 0;
            SkinningUploadBytes = 0;
            SkinMatrixBufferSize = 0;
            SkinnedVertexBufferSize = 0;
            AnimatedBoundsMode = string.Empty;
            ParticlesEnabled = false;
            ParticleSimulationMode = ParticleSimulationMode.Cpu;
            ParticleDebugView = ParticleDebugView.None;
            ParticleEffectCount = 0;
            ParticleEmitterCount = 0;
            LiveParticleCount = 0;
            SimulatedParticleCount = 0;
            CulledParticleCount = 0;
            RenderedParticleCount = 0;
            ParticleBatchCount = 0;
            ParticleDdgiSampleCount = 0;
            VfxDdgiDirtyProbeEventCount = 0;
            AlphaParticleCount = 0;
            AdditiveParticleCount = 0;
            SoftParticleCount = 0;
            FlipbookParticleCount = 0;
            TrailCount = 0;
            TrailSegmentCount = 0;
            BeamCount = 0;
            ParticleBudgetExceeded = 0;
            ParticleUploadBudgetExceeded = 0;
            ParticleInstanceUploadBytes = 0;
            TrailBeamUploadBytes = 0;
            CpuParticleSimulationMicroseconds = 0;
            CpuParticleBuildMicroseconds = 0;
            CpuParticleRecordMicroseconds = 0;
            CpuGpuParticleResetRecordMicroseconds = 0;
            CpuGpuParticleEmitterUploadMicroseconds = 0;
            CpuGpuParticleSimulateRecordMicroseconds = 0;
            CpuTrailBeamRecordMicroseconds = 0;
            GpuParticleMicroseconds = 0;
            GpuTrailBeamMicroseconds = 0;
            ParticleDrawCallCount = 0;
            ParticleInstanceBuffer = BufferHandle.Invalid;
            ParticleBatchBuffer = BufferHandle.Invalid;
            ParticleFrameDataBuffer = BufferHandle.Invalid;
            ParticleInstanceBufferSize = 0;
            ParticleBatchBufferSize = 0;
            ParticleFrameDataBufferSize = 0;
            GpuParticlesEnabled = 0;
            GpuParticleCapacity = 0;
            GpuParticleEmitterCapacity = 0;
            GpuParticleDrawCapacity = 0;
            GpuParticleResetRequired = 0;
            GpuParticleEmitterCount = 0;
            GpuParticleMaxSpawnPerEmitter = 0;
            GpuParticleDeltaSeconds = 0.0f;
            GpuParticleTimeSeconds = 0.0f;
            GpuParticleEmitterUploadBytes = 0;
            GpuParticleCountersReadbackValid = 0;
            GpuParticleAliveCount = 0;
            GpuParticleDeadCount = 0;
            GpuParticleSpawnedCount = 0;
            GpuParticleKilledCount = 0;
            GpuParticleCulledCount = 0;
            GpuParticleRenderedCount = 0;
            GpuParticleDroppedSpawnCount = 0;
            GpuParticleBlendBucket0Count = 0;
            GpuParticleBlendBucket1Count = 0;
            GpuParticleBlendBucket2Count = 0;
            GpuParticleBlendBucket3Count = 0;
            GpuParticleBlendBucket4Count = 0;
            FoliagePatchCount = 0;
            FoliagePrototypeCount = 0;
            FoliageClusterCount = 0;
            FoliageVisibleClusterCount = 0;
            FoliageCulledClusterCount = 0;
            FoliageVisibleMeshletDrawCount = 0;
            FoliageDdgiSampleCount = 0;
            FoliageGrassBladeEstimate = 0;
            FoliageLod0VisibleCount = 0;
            FoliageLod1VisibleCount = 0;
            FoliageLod2VisibleCount = 0;
            FoliageHiZTestedCount = 0;
            FoliageHiZRejectedCount = 0;
            FoliageOverflowCount = 0;
            FoliageMeshletDrawOverflowCount = 0;
            FoliageFarImpostorVisibleCount = 0;
            FoliageDebugView = 0;
            FoliageIndirectMeshletDispatchEnabled = true;
            FoliageCastShadows = true;
            FoliageMotionVectorsEnabled = false;
            FoliageLocalShadowsEnabled = false;
            FoliageGrassShadowDensityScale = 0.5f;
            FoliageMaxLocalShadowedSpotLights = 1;
            FoliageMaxLocalShadowedPointLights = 0;
            FoliageLocalShadowClusterBudget = 4096;
            FoliageLocalShadowMeshletDrawBudget = 8192;
            FoliageInstanceBufferBytes = 0;
            FoliageClusterBufferBytes = 0;
            FoliageDrawBufferBytes = 0;
            FoliageImpostorAtlasBytes = 0;
            CpuFoliageBuildMicroseconds = 0;
            CpuFoliageUploadMicroseconds = 0;
            GpuFoliageCullMicroseconds = 0;
            GpuFoliageDepthMicroseconds = 0;
            GpuFoliageForwardMicroseconds = 0;
            GpuFoliageShadowMicroseconds = 0;
            GpuParticleRenderInstanceBuffer = BufferHandle.Invalid;
            GpuParticleIndirectDrawBuffer = BufferHandle.Invalid;
            GpuParticleStateBufferSize = 0;
            GpuParticleAliveIndexBufferSize = 0;
            GpuParticleDeadIndexBufferSize = 0;
            GpuParticleEmitterBufferSize = 0;
            GpuParticleCurveSampleBufferSize = 0;
            GpuParticleCounterBufferSize = 0;
            GpuParticleUnsortedRenderInstanceBufferSize = 0;
            GpuParticleRenderInstanceBufferSize = 0;
            GpuParticleIndirectDrawBufferSize = 0;
            GpuParticleSortKeyBufferSize = 0;
            DebugViewMode = 0;
            MaxLightsPerTile = 0;
            MaxLightsInAnyTile = 0;
            AverageLightsPerNonEmptyTile = 0.0f;
            LightTileSaturationCount = 0;
            LightCullRejectedPointCount = 0;
            LightCullRejectedSpotCount = 0;
            UploadedBytes = 0;
            CpuSceneBuildMicroseconds = 0;
            CpuPayloadSignatureMicroseconds = 0;
            CpuObjectCullMicroseconds = 0;
            CpuMeshletCullMicroseconds = 0;
            CpuUploadMicroseconds = 0;
            CpuMaterialUploadMicroseconds = 0;
            CpuTotalDrawSceneMicroseconds = 0;
            CpuDepthPrePassRecordMicroseconds = 0;
            CpuDirectionalShadowRecordMicroseconds = 0;
            CpuSpotShadowRecordMicroseconds = 0;
            CpuPointShadowRecordMicroseconds = 0;
            CpuHiZBuildRecordMicroseconds = 0;
            CpuHiZDepthTransitionMicroseconds = 0;
            CpuHiZPyramidTransitionMicroseconds = 0;
            CpuHiZDescriptorBindMicroseconds = 0;
            CpuHiZPushDispatchMicroseconds = 0;
            CpuHiZFinalBarrierMicroseconds = 0;
            CpuLightCullRecordMicroseconds = 0;
            CpuForwardOpaqueRecordMicroseconds = 0;
            CpuTransparentRecordMicroseconds = 0;
            CpuBloomExtractRecordMicroseconds = 0;
            CpuBloomDownsampleRecordMicroseconds = 0;
            CpuBloomUpsampleRecordMicroseconds = 0;
            CpuFogRecordMicroseconds = 0;
            CpuAutoExposureRecordMicroseconds = 0;
            CpuCompositeRecordMicroseconds = 0;
            SecondaryCommandBufferEnabled = 0;
            SecondaryCommandBufferPassCount = 0;
            ActiveFeatureIsolation = RenderFeatureIsolationMode.FullFrame;
            SkippedRenderPassCount = 0;
            GraphPlannedBarrierCount = 0;
            GraphExecutedBarrierCount = 0;
            GraphQueueOwnershipTransitionCount = 0;
            GraphBarrierSummary = string.Empty;
            AsyncComputeOwnershipTransferCount = 0;
            AsyncComputeEstimatedOverlapMicroseconds = 0;
            CpuPrimaryCommandRecordMicroseconds = 0;
            CpuSecondaryCommandRecordMicroseconds = 0;
            GpuDepthPrePassMicroseconds = 0;
            GpuHiZBuildMicroseconds = 0;
            GpuLightCullMicroseconds = 0;
            GpuForwardOpaqueMicroseconds = 0;
            GpuForwardGiGatherMicroseconds = 0;
            GpuForwardGiGatherTimingCoverage = 0;
            GpuTransparentMicroseconds = 0;
            GpuDirectionalShadowMicroseconds = 0;
            GpuSpotShadowMicroseconds = 0;
            GpuPointShadowMicroseconds = 0;
            GpuBloomExtractMicroseconds = 0;
            GpuBloomDownsampleMicroseconds = 0;
            GpuBloomUpsampleMicroseconds = 0;
            GpuAutoExposureMicroseconds = 0;
            GpuCompositeMicroseconds = 0;
            SceneUploadCount = 0;
            SceneUploadSkipped = 0;
            ObjectCandidatesCpu = 0;
            ObjectFrustumCulledCpu = 0;
            MeshletCandidatesCpu = 0;
            MeshletFrustumCulledCpu = 0;
            MeshletLodSkippedCpu = 0;
            MeshletLod0SubmittedCpu = 0;
            MeshletLod1SubmittedCpu = 0;
            MeshletLod2SubmittedCpu = 0;
            StableSceneInputUploadBytes = 0;
            CpuCandidateListUploadBytes = 0;
            CameraDrivenCpuDrawListRebuilt = 0;
            HiZTestMode = HiZTestMode.Bounds4Tap;
            PreviousHiZFrameValid = false;
            PreviousHiZUvPaddingPixels = 8;
            PreviousHiZSkippedInvalidHistory = 0;
            PreviousHiZSkippedCameraMotion = 0;
            PreviousHiZTested = 0;
            PreviousHiZCulled = 0;
            DepthPrePassCompleted = false;
            DepthPrePassFrameSerial = 0;
            TiledLightCullingCompleted = false;
            TiledLightCullingFrameSerial = 0;
            ForwardVisibilityCompactionEnabled = false;
            ForwardVisibilityCompactionActive = false;
            ForwardVisibilityCompactionSkipReason = string.Empty;
            ForwardVisibilitySimpleCapacity = 0;
            ForwardVisibilitySimpleNormalCapacity = 0;
            ForwardVisibilityFullCapacity = 0;
            ForwardVisibilityCounterBuffer = BufferHandle.Invalid;
            ForwardVisibilityIndirectDispatchBuffer = BufferHandle.Invalid;
            ForwardVisibilityBufferBytes = 0;
            CurrentFrameHiZTested = 0;
            CurrentFrameHiZCulled = 0;
            HiZConsumerCount = 0;
            HiZConsumerSummary = string.Empty;
            HiZBuildSkippedBecauseNoConsumer = false;
            HiZCounterSource = HiZCounterSource.Unavailable;
            ForwardHiZTestedCount = 0;
            ForwardHiZCulledCount = 0;
            ForwardHiZCullRate = 0.0f;
            HiZFallbackPath = HiZFallbackPaths.Disabled;
            HiZFallbackReason = string.Empty;
            HiZValidateAgainstLegacyPath = false;
            HiZPolicyStatus = HiZVisibilityPolicyStatus.Disabled;
            HiZPolicyReason = string.Empty;
            HiZPolicyWarmupFramesRemaining = 0;
            HiZPolicySceneChanged = 0;
            HiZPolicyCameraCut = 0;
            HiZPolicyPyramidInvalidated = 0;
            HiZPolicyAdaptiveSuppressed = 0;
            HiZPolicyAdaptiveProbe = 0;
            HiZPolicyAdaptiveProbeCountdown = 0;
            HiZPolicyAdaptiveMeasuredOcclusionTests = 0;
            HiZPolicyAdaptiveMeasuredOcclusionCulled = 0;
            HiZPolicyAdaptiveCullRate = 0.0f;
            HiZPolicyCounterSource = HiZCounterSource.Unavailable;
            HiZPolicyAdaptiveEstimatedSavedMicroseconds = 0;
            HiZPolicyAdaptiveEstimatedCostMicroseconds = 0;
            HiZPolicyAdaptiveEstimatedNetMicroseconds = 0;
            HiZPolicyAdaptiveSmoothedCullRate = 0;
            HiZPolicyAdaptiveSmoothedSavedToCostRatio = 0;
            HiZPolicyAdaptiveSuppressedFrameCount = 0;
            HiZPolicyAdaptiveStatus = string.Empty;
            DepthTaskInvocations = 0;
            DepthFrustumCulledMeshletsGpu = 0;
            DepthEmittedMeshletsGpu = 0;
            ForwardTaskInvocations = 0;
            ForwardFrustumCulledMeshletsGpu = 0;
            ForwardOcclusionTestedMeshletsGpu = 0;
            ForwardOcclusionCulledMeshletsGpu = 0;
            ForwardEmittedMeshletsGpu = 0;
            SceneSubmissionGpuCompactionEnabled = false;
            SceneSubmissionIndirectMeshletDispatchEnabled = false;
            SceneSubmissionGpuLodSelectionEnabled = false;
            SceneSubmissionGpuLod1DistanceRatio = SceneSubmissionSettings.DefaultGpuLod1DistanceRatio;
            SceneSubmissionGpuLod2DistanceRatio = SceneSubmissionSettings.DefaultGpuLod2DistanceRatio;
            SceneSubmissionGpuShadowCompactionEnabled = false;
            SceneSubmissionGpuShadowLodBias = SceneSubmissionSettings.DefaultGpuShadowLodBias;
            SceneSubmissionValidationCompareCpuGpuLists = false;
            SceneSubmissionGpuCompactionActive = false;
            SceneSubmissionForwardPath = SceneSubmissionDiagnosticsPolicy.ForwardPathCpu;
            SceneSubmissionForwardTaskShader = SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderLegacyCull;
            SceneSubmissionCompactionSkipReason = string.Empty;
            SceneSubmissionIndirectDispatchSkipReason = string.Empty;
            SceneSubmissionFallbackReason = string.Empty;
            SceneSubmissionGpuOpaqueCandidateCount = 0;
            SceneSubmissionGpuCompactedOpaqueMeshletCount = 0;
            SceneSubmissionGpuOpaqueFrustumRejectedCount = 0;
            SceneSubmissionGpuOpaqueOverflowCount = 0;
            SceneSubmissionGpuIndirectMeshletTaskCount = 0;
            SceneSubmissionGpuCompactedShadowMeshletCount = 0;
            SceneSubmissionGpuCompactedOpaqueCapacity = 0;
            SceneSubmissionGpuDepthSolidCandidateCount = 0;
            SceneSubmissionGpuDepthMaskedCandidateCount = 0;
            SceneSubmissionGpuCompactedSolidDepthMeshletCount = 0;
            SceneSubmissionGpuCompactedMaskedDepthMeshletCount = 0;
            SceneSubmissionGpuCompactedSolidDepthCapacity = 0;
            SceneSubmissionGpuCompactedMaskedDepthCapacity = 0;
            SceneSubmissionGpuDepthOverflowCount = 0;
            SceneSubmissionGpuDirectionalShadowCandidateCount = 0;
            SceneSubmissionGpuCompactedDirectionalShadowMeshletCount = 0;
            SceneSubmissionGpuDirectionalShadowOverflowCount = 0;
            SceneSubmissionGpuDirectionalShadowLodFallbackCount = 0;
            SceneSubmissionGpuLod0EmittedCount = 0;
            SceneSubmissionGpuLod1EmittedCount = 0;
            SceneSubmissionGpuLod2EmittedCount = 0;
            SceneSubmissionGpuMissingLodFallbackCount = 0;
            SceneSubmissionValidationValid = 0;
            SceneSubmissionValidationStatus = string.Empty;
            SceneSubmissionValidationCpuOpaqueCount = 0;
            SceneSubmissionValidationGpuOpaqueCount = 0;
            SceneSubmissionValidationComparedSampleCount = 0;
            SceneSubmissionValidationMismatchCount = 0;
            SceneSubmissionValidationSampleLimit = 0;
            SceneSubmissionValidationFirstMismatch = string.Empty;
            SceneSubmissionOpaqueCompactedMeshletDrawBuffer = BufferHandle.Invalid;
            SceneSubmissionSolidDepthCompactedMeshletDrawBuffer = BufferHandle.Invalid;
            SceneSubmissionMaskedDepthCompactedMeshletDrawBuffer = BufferHandle.Invalid;
            SceneSubmissionCounterBuffer = BufferHandle.Invalid;
            SceneSubmissionOpaqueIndirectDispatchBuffer = BufferHandle.Invalid;
            SceneSubmissionOpaqueCompactedMeshletDrawBufferSize = 0;
            SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize = 0;
            SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize = 0;
            SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize = 0;
            SceneSubmissionCounterBufferSize = 0;
            SceneSubmissionOpaqueIndirectDispatchBufferSize = 0;
            MeshletCountTotal = 0;
            MeshletCountSubmittedCpu = 0;
            AvgTrianglesPerSubmittedMeshlet = 0;
            AvgVerticesPerSubmittedMeshlet = 0;
            SmallMeshletsUnder16Triangles = 0;
            SmallMeshletsUnder32Triangles = 0;
            SimpleOpaqueMeshletCount = 0;
            SimpleNormalOpaqueMeshletCount = 0;
            FullOpaqueMeshletCount = 0;
            ForwardSimpleMeshletCount = 0;
            ForwardFullMaterialMeshletCount = 0;
            ForwardLocalProbeMeshletCount = 0;
            ScenePayloadRebuilt = 0;
            ObjectUploadBytes = 0;
            InstanceUploadBytes = 0;
            MeshletDrawUploadBytes = 0;
            SolidDepthMeshletDrawUploadBytes = 0;
            MaskedDepthMeshletDrawUploadBytes = 0;
            PackedMeshletDrawUploadBytes = 0;
            PackedSolidDepthMeshletDrawUploadBytes = 0;
            PackedMaskedDepthMeshletDrawUploadBytes = 0;
            TransparentMeshletDrawUploadBytes = 0;
            MaterialUploadBytes = 0;
            MaterialExtensionUploadBytes = 0;
            MeshletDrawBufferSize = 0;
            FullOpaqueMeshletDrawBufferSize = 0;
            SimpleNormalOpaqueMeshletDrawBufferSize = 0;
            SolidDepthMeshletDrawBufferSize = 0;
            MaskedDepthMeshletDrawBufferSize = 0;
            PackedMeshletDrawBufferSize = 0;
            PackedFullOpaqueMeshletDrawBufferSize = 0;
            PackedSimpleNormalOpaqueMeshletDrawBufferSize = 0;
            PackedSolidDepthMeshletDrawBufferSize = 0;
            PackedMaskedDepthMeshletDrawBufferSize = 0;
            MeshletTaskFrameDataBufferSize = 0;
            DirectionalShadowMeshletDrawBufferSize = 0;
            LocalShadowMeshletDrawBufferSize = 0;
            LightUploadBytes = 0;
            HiZWidth = 0;
            HiZHeight = 0;
            BloomEnabled = false;
            DirectionalShadowPassEnabled = false;
            DirectionalShadowRecordSkipped = false;
            DirectionalShadowMapSize = 0;
            DirectionalShadowCascadeCount = 0;
            DirectionalShadowMaxDistance = 0;
            DirectionalShadowCascadeBlendFraction = 0;
            ShadowedDirectionalLightIndex = -1;
            ShadowDebugView = ShadowDebugView.None;
            DirectionalShadowPreviewCascade = 0;
            ShadowNormalBias = 0;
            ShadowSlopeScaledDepthBias = 0;
            DirectionalShadowPcfRadius = 0;
            SpotShadowPcfRadius = 0;
            PointShadowPcfRadius = 0;
            ForwardShadowReceiverMeshletCount = 0;
            DirectionalShadowStaticCacheActiveMask = 0;
            DirectionalShadowStaticCacheValidMask = 0;
            DirectionalShadowStaticCacheRefreshMask = 0;
            DirectionalShadowStaticCacheReuseMask = 0;
            DirectionalShadowReceiverCountersReadbackValid = 0;
            DirectionalShadowReceiverUnresolvedCount = 0;
            ShadowData = default;
            SpotShadowsEnabled = false;
            SpotShadowRecordSkipped = false;
            SpotShadowCandidateCount = 0;
            SpotShadowSelectedCount = 0;
            SpotShadowRejectedByBudgetCount = 0;
            SpotShadowAtlasSize = 0;
            SpotShadowTileSize = 0;
            SpotShadowAtlasCapacity = 0;
            SpotShadowAtlasUsedTiles = 0;
            PointShadowsEnabled = false;
            PointShadowRecordSkipped = false;
            PointShadowCandidateCount = 0;
            PointShadowSelectedCount = 0;
            PointShadowRejectedByBudgetCount = 0;
            PointShadowMapSize = 0;
            PointShadowRenderedFaceCount = 0;
            PointShadowSkippedFaceCount = 0;
            LocalShadowMeshletCount = 0;
            DirectionalStaticShadowMeshletCount = 0;
            DirectionalDynamicShadowMeshletCount = 0;
            LocalStaticShadowMeshletCount = 0;
            LocalDynamicShadowMeshletCount = 0;
            DirectionalShadowSkinnedObjectCount = 0;
            LocalShadowSkinnedObjectCount = 0;
            DirectionalShadowMeshletDrawSignature = 0;
            LocalShadowMeshletDrawSignature = 0;
            DirectionalStaticShadowMeshletDrawSignature = 0;
            DirectionalDynamicShadowMeshletDrawSignature = 0;
            LocalStaticShadowMeshletDrawSignature = 0;
            LocalDynamicShadowMeshletDrawSignature = 0;
            SpotShadowData = [];
            PointShadowData = [];
            PointShadowFaceMasks = [];
            LocalLightShadowIndices = [];
            TiledLightHeaderBufferClearBytes = 0;
            TiledLightIndexBufferClearBytes = 0;
            Array.Clear(DirectionalShadowMeshletCounts, 0, DirectionalShadowMeshletCounts.Length);
            Array.Clear(DirectionalShadowReceiverPrimarySelectionCounts, 0, DirectionalShadowReceiverPrimarySelectionCounts.Length);
            Array.Clear(DirectionalShadowReceiverProjectionRejectedCounts, 0, DirectionalShadowReceiverProjectionRejectedCounts.Length);
            Array.Clear(DirectionalShadowReceiverUvDepthRejectedCounts, 0, DirectionalShadowReceiverUvDepthRejectedCounts.Length);
            Array.Clear(DirectionalShadowReceiverFallbackCounts, 0, DirectionalShadowReceiverFallbackCounts.Length);
            Array.Clear(DirectionalShadowReceiverTransitionBlendCounts, 0, DirectionalShadowReceiverTransitionBlendCounts.Length);
            Array.Clear(DirectionalShadowReceiverPrimaryResolvedCounts, 0, DirectionalShadowReceiverPrimaryResolvedCounts.Length);
            Array.Clear(DirectionalShadowReceiverClearDepthFootprintCounts, 0, DirectionalShadowReceiverClearDepthFootprintCounts.Length);
            Array.Clear(DirectionalShadowReceiverPrimaryFullyLitCounts, 0, DirectionalShadowReceiverPrimaryFullyLitCounts.Length);
            Array.Clear(DirectionalShadowReceiverPrimaryPartiallyShadowedCounts, 0, DirectionalShadowReceiverPrimaryPartiallyShadowedCounts.Length);
            Array.Clear(DirectionalShadowReceiverPrimaryFullyShadowedCounts, 0, DirectionalShadowReceiverPrimaryFullyShadowedCounts.Length);
            Array.Clear(DirectionalShadowReceiverFinalFullyLitCounts, 0, DirectionalShadowReceiverFinalFullyLitCounts.Length);
            Array.Clear(DirectionalShadowReceiverFinalPartiallyShadowedCounts, 0, DirectionalShadowReceiverFinalPartiallyShadowedCounts.Length);
            Array.Clear(DirectionalShadowReceiverFinalFullyShadowedCounts, 0, DirectionalShadowReceiverFinalFullyShadowedCounts.Length);
            Array.Clear(DirectionalShadowReceiverAverageDepths, 0, DirectionalShadowReceiverAverageDepths.Length);
            Array.Clear(DirectionalShadowReceiverAverageMinimumSampledDepths, 0, DirectionalShadowReceiverAverageMinimumSampledDepths.Length);
            Array.Clear(DirectionalShadowReceiverAverageMaximumSampledDepths, 0, DirectionalShadowReceiverAverageMaximumSampledDepths.Length);
            Array.Clear(SceneSubmissionGpuDirectionalStaticShadowCandidateCounts, 0, SceneSubmissionGpuDirectionalStaticShadowCandidateCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalStaticShadowEmittedCounts, 0, SceneSubmissionGpuDirectionalStaticShadowEmittedCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalStaticShadowRejectedCounts, 0, SceneSubmissionGpuDirectionalStaticShadowRejectedCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalStaticShadowOverflowCounts, 0, SceneSubmissionGpuDirectionalStaticShadowOverflowCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalStaticShadowCapacities, 0, SceneSubmissionGpuDirectionalStaticShadowCapacities.Length);
            Array.Clear(SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts, 0, SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts, 0, SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalDynamicShadowRejectedCounts, 0, SceneSubmissionGpuDirectionalDynamicShadowRejectedCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalDynamicShadowOverflowCounts, 0, SceneSubmissionGpuDirectionalDynamicShadowOverflowCounts.Length);
            Array.Clear(SceneSubmissionGpuDirectionalDynamicShadowCapacities, 0, SceneSubmissionGpuDirectionalDynamicShadowCapacities.Length);
            BloomMipCount = 0;
            BloomBaseWidth = 0;
            BloomBaseHeight = 0;
            AutoExposureEnabled = false;
            EffectiveExposure = 1.0f;
            AutoExposureAverageLuminance = 0;
            AutoExposureTargetExposure = 0;
            AutoExposureSampleCount = 0;
            AutoExposureStateBufferIndex = 0;
            ActiveSceneColorTextureIndex = 0;
            FogEnabled = false;
            FogMode = FogMode.Disabled;
            FogColorMode = FogColorMode.ConstantColor;
            FogDebugView = FogDebugView.None;
            FogDensity = 0;
            FogStartDistance = 0;
            FogEndDistance = 0;
            FogHeight = 0;
            FogHeightFalloff = 0;
            FogHeightDensity = 0;
            FogMaxOpacity = 0;
            FogDirectionalInscatteringEnabled = 0;
            FogDirectionalInscatteringDirection = Vector3.Zero;
            FogWidth = 0;
            FogHeightPixels = 0;
            FogFormat = string.Empty;
            GpuFogMicroseconds = 0;
            ReflectionsEnabled = false;
            ReflectionMode = ReflectionMode.Disabled;
            ReflectionDebugView = ReflectionDebugView.None;
            ReflectionProbeCount = 0;
            ReflectionProbeCapacity = 0;
            MaxReflectionProbesPerPixel = 0;
            ReflectionProbeResolution = 0;
            ReflectionProbeMipCount = 0;
            ReflectionProbeEstimatedBytes = 0;
            ReflectionProbeCapturesQueued = 0;
            ReflectionProbeCapturesCompleted = 0;
            CpuReflectionProbeUploadMicroseconds = 0;
            CpuReflectionProbeCaptureRecordMicroseconds = 0;
            CpuReflectionProbePrefilterRecordMicroseconds = 0;
            GpuReflectionProbeCaptureMicroseconds = 0;
            GpuReflectionProbePrefilterMicroseconds = 0;
            AmbientOcclusionEnabled = false;
            AmbientOcclusionMode = AmbientOcclusionMode.Disabled;
            AmbientOcclusionDebugView = AmbientOcclusionDebugView.None;
            AmbientOcclusionForwardSamplingMode = AmbientOcclusionForwardSamplingMode.Disabled;
            AmbientOcclusionForwardDepthAwareSamples = 0;
            AmbientOcclusionWidth = 0;
            AmbientOcclusionHeight = 0;
            AmbientOcclusionFormat = string.Empty;
            AmbientOcclusionResolutionScale = 0;
            AmbientOcclusionRadius = 0;
            AmbientOcclusionIntensity = 0;
            AmbientOcclusionBias = 0;
            AmbientOcclusionSampleCount = 0;
            AmbientOcclusionBlurRadius = 0;
            CpuAmbientOcclusionRecordMicroseconds = 0;
            CpuAmbientOcclusionBlurRecordMicroseconds = 0;
            GpuAmbientOcclusionMicroseconds = 0;
            GpuAmbientOcclusionBlurMicroseconds = 0;
            CpuSsgiRecordMicroseconds = 0;
            CpuDdgiRecordMicroseconds = 0;
            CpuSimpleDdgiRecordMicroseconds = 0;
            CpuFarFieldRecordMicroseconds = 0;
            CpuGlobalIlluminationRecordMicroseconds = 0;
            CpuGlobalIlluminationRecordP95Microseconds = 0;
            GlobalIlluminationCpuTimingSampleCount = 0;
            CpuDdgiSchedulerMicroseconds = 0;
            CpuDdgiSchedulerP95Microseconds = 0;
            CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds = 0;
            CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds = 0;
            CpuDdgiSchedulerPhaseUninitializedMicroseconds = 0;
            CpuDdgiSchedulerPhaseFrustumMicroseconds = 0;
            CpuDdgiSchedulerPhaseSafetyMicroseconds = 0;
            CpuDdgiSchedulerPhaseRoundRobinMicroseconds = 0;
            CpuDdgiSchedulerCandidateInsertCount = 0;
            CpuDdgiSchedulerCandidateMaxShiftCount = 0;
            DdgiSchedulerTimingSampleCount = 0;
            DdgiSchedulerP95OverBudget = 0;
            SsgiHistoryValid = 0;
            SsgiRejectedHistoryPixelCount = 0;
            DdgiProbeVolumeCount = 0;
            DdgiProbeCount = 0;
            DdgiActiveProbeCount = 0;
            DdgiProbesUpdated = 0;
            DdgiRaysPerProbe = 0;
            DdgiMaxActiveProbeBudget = 0;
            DdgiMaxProbeUpdatesPerFrame = 0;
            DdgiProbeUpdateRequestBudget = 0;
            DdgiProbeUpdatePrimaryRayBudget = 0;
            DdgiScheduledRequestBudget = 0;
            DdgiScheduledPrimaryRayBudget = 0;
            DdgiGpuSchedulerPredictedRequestUpperBound = 0;
            DdgiGpuSchedulerActualRequestCount = 0;
            DdgiGpuSchedulerActualPrimaryRayCount = 0;
            DdgiGatherTileCount = 0;
            DdgiGatherTileCountX = 0;
            DdgiGatherTileCountY = 0;
            DdgiGatherSelectedLocalTileCount = 0;
            DdgiGatherSelectedClipmapTileCount = 0;
            DdgiGatherFallbackTileCount = 0;
            DdgiGatherSelectedLocalTileFraction = 0;
            DdgiGatherSelectedClipmapTileFraction = 0;
            DdgiGatherFallbackTileFraction = 0;
            DdgiForwardGatherFallbackUsed = 0;
            DdgiForwardGatherFallbackDisabled = 0;
            DdgiForwardGatherTileEmpty = 0;
            DdgiAverageSpatialCoverageEstimate = 0;
            DdgiAverageSupportCoverageEstimate = 0;
            DdgiAverageDataConfidenceEstimate = 0;
            DdgiAverageVisibilityConfidenceEstimate = 0;
            DdgiAverageLeakAttenuationEstimate = 0;
            DdgiAverageEffectiveContributionEstimate = 0;
            DdgiAverageOwnershipConsumedEstimate = 0;
            DdgiWarmupState = DdgiRuntimeWarmupState.Disabled;
            DdgiWarmedVisibleProbeFraction = 0;
            DdgiWarmedLocalProbeFraction = 0;
            DdgiWarmedCascade0ProbeFraction = 0;
            DdgiForwardEstimateCountersReadbackValid = 0;
            DdgiForwardEstimateSampleCount = 0;
            DdgiForwardEstimateZeroVisibleButCoveredCount = 0;
            DdgiForwardEstimateZeroEffectiveButCoveredCount = 0;
            DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount = 0;
            DdgiForwardEstimateSampledIrradianceLuminance = 0;
            DdgiForwardEstimateRawDiffuseLuminance = 0;
            DdgiForwardEstimateFinalDiffuseLuminance = 0;
            DdgiForwardEstimateEnvironmentFallbackWeight = 0;
            DdgiSupportRejectedInactiveCount = 0;
            DdgiSupportRejectedZeroIrradianceAlphaCount = 0;
            DdgiSupportRejectedLowQualityCount = 0;
            DdgiProbeIrradianceAlphaAverage = 0;
            DdgiProbeQualityXAverage = 0;
            DdgiProbeQualityYAverage = 0;
            DdgiProbeQualityZAverage = 0;
            DdgiProbeQualitySampleCount = 0;
            DdgiSampledProbeCurrentFrustumCount = 0;
            DdgiSampledProbeSideRearCount = 0;
            DdgiSampledProbeStaleAgeCount = 0;
            DdgiClipmapInfoPrimaryAttemptCount = 0;
            DdgiClipmapInfoPrimaryOkCount = 0;
            DdgiClipmapInfoPrimaryFailedCount = 0;
            DdgiClipmapInfoPrimaryEdgeFadeAverage = 0;
            DdgiClipmapInfoPrimaryBlendWeightAverage = 0;
            DdgiFastGatherAttemptCount = 0;
            DdgiFastGatherAcceptedCount = 0;
            DdgiFastGatherRejectedZeroSpatialCount = 0;
            DdgiFastGatherRejectedZeroSupportCount = 0;
            DdgiFastGatherRejectedZeroDataCount = 0;
            DdgiFastGatherRejectedZeroOwnershipCount = 0;
            DdgiShaderGatherFallbackAttemptCount = 0;
            DdgiShaderGatherFallbackAcceptedCount = 0;
            DdgiShaderGatherFallbackEmptyCount = 0;
            DdgiTraceEnergySampleCount = 0;
            DdgiTraceEnergyHitCount = 0;
            DdgiTraceEnergyMissCount = 0;
            DdgiTraceEnergyRayLuminanceAverage = 0;
            DdgiTraceEnergyDirectLuminanceAverage = 0;
            DdgiTraceEnergyEmissiveLuminanceAverage = 0;
            DdgiTraceEnergyStableLuminanceAverage = 0;
            DdgiTraceEnergySkyLuminanceAverage = 0;
            DdgiTraceEnergyHitZeroDirectCount = 0;
            DdgiTraceEnergyHitWithDirectCount = 0;
            DdgiTraceEnergyDirectNoShadowLuminanceAverage = 0;
            DdgiShadowVisibilityRayCount = 0;
            DdgiShadowVisibilityOccludedCount = 0;
            DdgiShadowVisibilityNearHitCount = 0;
            DdgiShadowVisibilityCommittedHitDistanceAverage = 0;
            DdgiTraceEarlyOutDisabledCount = 0;
            DdgiTraceEarlyOutBeyondRequestCount = 0;
            DdgiTraceEarlyOutResolveBoundsCount = 0;
            DdgiTraceEarlyOutResolveProbeRangeCount = 0;
            DdgiTraceEarlyOutResolveClipmapCellCount = 0;
            DdgiTraceEarlyOutResolveClipmapRingCount = 0;
            DdgiTraceRingMismatchCorrectedCount = 0;
            DdgiTraceRingMismatchSample = string.Empty;
            DdgiBlendEnergySampleCount = 0;
            DdgiBlendEnergyIrradianceLuminanceAverage = 0;
            DdgiBlendEnergyConfidenceAverage = 0;
            DdgiBlendEnergyLowConfidenceCount = 0;
            DdgiBlendEnergyNonzeroIrradianceCount = 0;
            DdgiBlendEnergyNonFiniteIrradianceCount = 0;
            DdgiBlendEnergyFireflySuppressedCount = 0;
            SimpleDdgiTransportEnergySampleCount = 0;
            SimpleDdgiTransportSourceCacheHitCount = 0;
            SimpleDdgiTransportSourceCacheMissCount = 0;
            SimpleDdgiTransportBounceLuminanceAverage = 0;
            SimpleDdgiTransportSourceLuminanceAverage = 0;
            SimpleDdgiTransportTotalLuminanceAverage = 0;
            DdgiTransparentReceiverSampleCount = 0;
            DdgiTransparentReceiverIrradianceLuminanceAverage = 0;
            DdgiTransparentReceiverFinalLuminanceAverage = 0;
            DdgiDecalReceiverSampleCount = 0;
            DdgiDecalReceiverIrradianceLuminanceAverage = 0;
            DdgiDecalReceiverFinalLuminanceAverage = 0;
            DdgiVisibilityMomentMeanAverage = 0;
            DdgiVisibilityMomentVarianceAverage = 0;
            DdgiVisibilityProbeDistanceAverage = 0;
            DdgiVisibilityMomentSampleCount = 0;
            DdgiVisibilityLargeDistanceMarginCount = 0;
            DdgiVisibilityZeroTransportCount = 0;
            DdgiVisibilityZeroTransportWithIrradianceCount = 0;
            DdgiAverageRelocationFractionEstimate = 0;
            DdgiClassifiedInactiveProbeCountEstimate = 0;
            DdgiQualityTier = DdgiQualityTier.DdgiHigh;
            DdgiAdaptiveBudgetScale = 1.0f;
            DdgiAdaptiveBudgetReduced = 0;
            DdgiEmergencyDegradeActive = 0;
            DdgiEffectiveMaxShadedLights = 0;
            DdgiAdaptiveBudgetReason = string.Empty;
            GlobalIlluminationSsgiActive = 0;
            GlobalIlluminationDdgiActive = 0;
            SimpleDdgiActive = 0;
            SimpleDdgiProbeCount = 0;
            SimpleDdgiProbesUpdated = 0;
            SimpleDdgiRaysPerFrame = 0;
            SimpleDdgiTransportV2Active = 0;
            SimpleDdgiAutomaticProbeDensityActive = 0;
            SimpleDdgiTransportSourceRefreshProbeCount = 0;
            SimpleDdgiTransportSourceCacheReuseProbeCount = 0;
            SimpleDdgiTransportSourceRayCount = 0;
            SimpleDdgiTransportSolveRayCount = 0;
            SimpleDdgiTransportPublishedProbeCount = 0;
            SimpleDdgiTransportPublishRegionCount = 0;
            SimpleDdgiTransportPublishedProbeTotal = 0;
            SimpleDdgiTransportPublishRegionTotal = 0;
            SimpleDdgiUpdateTransactionAbortCount = 0;
            SimpleDdgiTransportSourceCacheInvalidationCount = 0;
            SimpleDdgiTransportSolverInvalidationCount = 0;
            SimpleDdgiTransportSolverInvalidationsPerSourceRefresh = 0;
            SimpleDdgiSourceLightingGeneration = 0;
            SimpleDdgiTransportGeneration = 0;
            SimpleDdgiTransportSourceReadyProbeCount = 0;
            SimpleDdgiTransportSourceStaleProbeCount = 0;
            SimpleDdgiTransportConvergedProbeCount = 0;
            SimpleDdgiTransportPendingSolverProbeCount = 0;
            SimpleDdgiTransportGlobalConvergencePending = 0;
            SimpleDdgiTransportGlobalConvergenceElapsedFrames = 0;
            SimpleDdgiTransportCalibrationChangeCount = 0;
            SimpleDdgiTransportIrradianceAtlasBytes = 0;
            SimpleDdgiTransportSourceCacheBytes = 0;
            SimpleDdgiTransportSolverRelaxation = 0;
            SimpleDdgiTransportAlbedoClamp = 0;
            SimpleDdgiTransportResidualThreshold = 0;
            SimpleDdgiTransportMaximumSolverGenerations = 0;
            SimpleDdgiTransportSourceRefreshFrames = 0;
            SimpleDdgiInactiveProbeCount = 0;
            SimpleDdgiInactiveProbeSkipCount = 0;
            SimpleDdgiSavedRaysPerFrame = 0;
            SimpleDdgiLightingDirtyFrames = 0;
            SimpleDdgiLightingDirtyBoostedCapacity = 0;
            SimpleDdgiDirtyReasonFlags = 0;
            SimpleDdgiFullRayProbeUpdateCount = 0;
            SimpleDdgiMaintenanceRayProbeUpdateCount = 0;
            SimpleDdgiAdaptiveRaySavedRaysPerFrame = 0;
            SimpleDdgiNearFullRayProbeUpdateCount = 0;
            SimpleDdgiMidFullRayProbeUpdateCount = 0;
            SimpleDdgiFarFullRayProbeUpdateCount = 0;
            SimpleDdgiNearMaintenanceRayProbeUpdateCount = 0;
            SimpleDdgiMidMaintenanceRayProbeUpdateCount = 0;
            SimpleDdgiFarMaintenanceRayProbeUpdateCount = 0;
            SimpleDdgiNearScheduledPrimaryRayCount = 0;
            SimpleDdgiMidScheduledPrimaryRayCount = 0;
            SimpleDdgiFarScheduledPrimaryRayCount = 0;
            SimpleDdgiDirtyFirstUpdateLatencySampleCount = 0;
            SimpleDdgiDirtyFirstUpdateLatencyP50Frames = 0;
            SimpleDdgiDirtyFirstUpdateLatencyP95Frames = 0;
            SimpleDdgiDirtyFirstUpdateLatencyMaxFrames = 0;
            SimpleDdgiOldestVisibleUnsupportedProbeAge = 0;
            SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget = 0;
            SimpleDdgiVisibleZeroSupportRepairUpdateCount = 0;
            SimpleDdgiProbeLifecycleLatencyTargetFrames = 0;
            SimpleDdgiMaximumFreshProbeAge = 0;
            SimpleDdgiMaximumScrollExposedProbeAge = 0;
            SimpleDdgiMaximumRelocationPendingProbeAge = 0;
            SimpleDdgiMaximumUnpublishedProbeAge = 0;
            SimpleDdgiProbeLifecycleBoundExceededCount = 0;
            SimpleDdgiDirtyConvergenceLatencySampleCount = 0;
            SimpleDdgiDirtyConvergenceLatencyP50Frames = 0;
            SimpleDdgiDirtyConvergenceLatencyP95Frames = 0;
            SimpleDdgiDirtyConvergenceLatencyMaxFrames = 0;
            SimpleDdgiAtlasBytes = 0;
            SimpleDdgiSampledAtlasRequested = 0;
            SimpleDdgiSampledAtlasActive = 0;
            SimpleDdgiSampledAtlasGroupCount = 0;
            SimpleDdgiSampledAtlasLayersPerTexture = 0;
            SimpleDdgiSampledAtlasImageBytes = 0;
            SimpleDdgiSampledAtlasFallbackReason = string.Empty;
            FarFieldPagedMode = 0;
            FarFieldPagePoolCapacity = 0;
            FarFieldResidentPageCount = 0;
            FarFieldPendingPageCount = 0;
            FarFieldPageRequestCount = 0;
            FarFieldPageMissCount = 0;
            FarFieldPageRebuildCount = 0;
            FarFieldPageEvictionCount = 0;
            FarFieldScheduledPageBakeCount = 0;
            FarFieldCacheBytes = 0;
            FarFieldMemoryBudgetBytes = 0;
            FarFieldInstanceBufferBytes = 0;
            FarFieldPageTableBytes = 0;
            SimpleDdgiRecentered = 0;
            SimpleDdgiAtlasPreservedOnRecenter = 0;
            SimpleDdgiAtlasCleared = 0;
            SimpleDdgiAtlasFresh = 0;
            SimpleDdgiRecenterCount = 0;
            SimpleDdgiAtlasClearCount = 0;
            SimpleDdgiAtlasPreserveOnRecenterCount = 0;
            SimpleDdgiFramesSinceLastClear = 0;
            SimpleDdgiFramesSinceLastRecenter = 0;
            DdgiInvestigationCountersReadbackValid = 0;
            SimpleDdgiFreshAtlasForwardSampleCount = 0;
            SimpleDdgiZeroIrradianceSampleCount = 0;
            SimpleDdgiNonzeroIrradianceSampleCount = 0;
            SimpleDdgiAverageSampledIrradianceLuminance = 0;
            SimpleDdgiAverageVisibility = 0;
            SimpleDdgiLowVisibilitySampleCount = 0;
            SimpleDdgiGatherSampleCount = 0;
            SimpleDdgiSecondVolumeGatherCount = 0;
            SimpleDdgiGatherPrimaryRejectionCounts = Array.Empty<uint>();
            SimpleDdgiGatherFallbackRejectionCounts = Array.Empty<uint>();
            SimpleDdgiGatherRecoveryRejectionCounts = Array.Empty<uint>();
            SimpleDdgiGatherPrimaryAllFailedCount = 0;
            SimpleDdgiGatherFallbackAllFailedCount = 0;
            SimpleDdgiGatherRecoveryAllFailedCount = 0;
            DdgiFullRefreshFrameCount = 0;
            DdgiPartialRefreshFrameCount = 0;
            DdgiUpdatedProbeFraction = 0;
            DdgiProbeUpdateStartIndex = 0;
            DdgiProbeUpdateEndIndex = 0;
            DdgiSkippedProbeCount = 0;
            DdgiFramesSinceProbeUpdatedP50 = 0;
            DdgiFramesSinceProbeUpdatedP95 = 0;
            DdgiFramesSinceProbeUpdatedMax = 0;
            DdgiNewlyInvalidatedProbeCount = 0;
            DdgiRefreshReasonRecenterProbeCount = 0;
            DdgiRefreshReasonDirtyProbeCount = 0;
            DdgiRefreshReasonAgeProbeCount = 0;
            DdgiRefreshReasonVisibilityProbeCount = 0;
            DdgiRefreshReasonFullRefreshProbeCount = 0;
            DdgiForwardSimplePathSampleCount = 0;
            DdgiForwardLegacyPathSampleCount = 0;
            DdgiForwardZeroFinalIndirectCount = 0;
            DdgiForwardZeroDdgiButNonzeroIblCount = 0;
            DdgiForwardZeroDdgiAndZeroIblCount = 0;
            DdgiForwardOutOfGridSampleCount = 0;
            DdgiForwardClampedProbeSampleCount = 0;
            DdgiForwardNanOrInfSampleCount = 0;
            DdgiIrradianceAtlasZeroTexelSampleCount = 0;
            DdgiVisibilityAtlasZeroMomentSampleCount = 0;
            DdgiAtlasWriteProbeCount = 0;
            DdgiAtlasWriteTexelCount = 0;
            DdgiBlendZeroRayWeightProbeCount = 0;
            DdgiBlendNonzeroIrradianceProbeCount = 0;
            DdgiBlendPreviousAtlasUsedCount = 0;
            DdgiBlendHysteresisZeroFrameCount = 0;
            DdgiSimpleTraceHitCount = 0;
            DdgiSimpleTraceMissCount = 0;
            DdgiSimpleTraceZeroRadianceHitCount = 0;
            DdgiSimpleTraceDirectLightHitCount = 0;
            DdgiSimpleTraceEmissiveHitCount = 0;
            DdgiSimpleTraceFarFieldHitCount = 0;
            DdgiSimpleTraceFarFieldMissCount = 0;
            DdgiSimpleTraceTlasUnavailableFrameCount = 0;
            SimpleDdgiSkyVisibilitySampleCount = 0;
            SimpleDdgiAverageSkyVisibility = 0;
            FarFieldSunShadowSampleCount = 0;
            FarFieldSunShadowOccludedCount = 0;
            SimpleDdgiRoughSpecularSampleCount = 0;
            SimpleDdgiRoughSpecularNonzeroCount = 0;
            DdgiSimpleTraceFarFieldStepBucket0Count = 0;
            DdgiSimpleTraceFarFieldStepBucket1Count = 0;
            DdgiSimpleTraceFarFieldStepBucket2Count = 0;
            DdgiSimpleTraceFarFieldStepBucket3Count = 0;
            DdgiSimpleTraceFarFieldStepBucket4Count = 0;
            MaterialDetailedTransportHitCount = 0;
            MaterialCompactTransportHitCount = 0;
            MaterialCorrectnessFallbackHitCount = 0;
            MaterialFarFieldTransportHitCount = 0;
            DdgiBlackFrameSuspect = 0;
            DdgiBlackFrameAfterRecenter = 0;
            DdgiBlackFrameAfterAtlasClear = 0;
            DdgiBlackFrameDuringFreshAtlas = 0;
            DdgiBlackFrameMovementClass = DdgiCameraMovementClass.None;
            DdgiAsyncComputeEnabled = 0;
            DdgiAtlasMemoryBudgetBytes = 0;
            DdgiProbeRelocationCount = 0;
            DdgiProbeClassificationCount = 0;
            DdgiCascadeCount = 0;
            DdgiScrollCount = 0;
            DdgiNewProbeCount = 0;
            DdgiDirtyBoundsProbeUpdateCount = 0;
            DdgiVisibleFrustumProbeUpdateCount = 0;
            DdgiOutsideFrustumSafetyProbeUpdateCount = 0;
            DdgiAgeRefreshProbeUpdateCount = 0;
            DdgiHighVarianceProbeUpdateCount = 0;
            DdgiLowConfidenceProbeUpdateCount = 0;
            DdgiStableProbeUpdateCount = 0;
            DdgiAverageProbeVariability = 0;
            DdgiAverageProbeConfidence = 0;
            DdgiScheduledPrimaryRayCount = 0;
            DdgiEstimatedShadowRayUpperBound = 0;
            DdgiSelectedDirectionalHitCount = 0;
            DdgiSelectedLocalHitCount = 0;
            DdgiVisibilityRayCount = 0;
            DdgiSkippedLocalLightCount = 0;
            DdgiLightSelectionMode = string.Empty;
            DdgiPrimaryDirectionalLightIndex = -1;
            DdgiSelectedLocalLightIndex = -1;
            DdgiSelectedLocalLightEnergyScale = 1.0f;
            DdgiEmissiveSourceCount = 0;
            DdgiEmissiveSourceRevision = 0;
            DdgiEmissiveSamplingMode = string.Empty;
            DdgiEmissiveTriangleCandidateCount = 0;
            DdgiEmissiveTriangleBudget = 0;
            DdgiEmissiveSkippedEnergyFraction = 0.0f;
            DdgiEmissiveSkippedSkinnedObjectCount = 0;
            DdgiEmissiveSkippedSkinnedImportance = 0.0;
            DdgiEmissiveTableCacheHit = 0;
            DdgiEmissiveTableCacheHitCount = 0;
            DdgiEmissiveTableCacheMissCount = 0;
            DdgiEmissiveTableRebuildCount = 0;
            DdgiEmissiveTableInvalidationCount = 0;
            DdgiEmissiveTableUploadCount = 0;
            DdgiProbeVolumeBufferBytes = 0;
            DdgiProbeStateBufferBytes = 0;
            DdgiProbeUpdateQueueBytes = 0;
            DdgiProbeRelocationClassificationBytes = 0;
            DdgiCurrentIrradianceAtlasBytes = 0;
            DdgiCurrentVisibilityAtlasBytes = 0;
            DdgiGatherTileBufferBytes = 0;
            DdgiLocalSlotReservedPoolBytes = 0;
            DdgiGpuSchedulerBufferBytes = 0;
            DdgiGpuSchedulerDirtyRegionCapacity = 0;
            DdgiGpuSchedulerCandidateCapacity = 0;
            DdgiGpuSchedulerGroupCountCapacity = 0;
            DdgiGpuSchedulerPrefixCapacity = 0;
            DdgiGpuSchedulerDirtyRegionCount = 0;
            DdgiGpuSchedulerDirtyRegionOverflowCount = 0;
            DdgiGpuSchedulerResourceReinitializationCount = 0;
            DdgiGpuSchedulerTotalResourceReinitializationCount = 0;
            DdgiGpuSchedulerUploadBytes = 0;
            DdgiGpuSchedulerReadbackValid = 0;
            DdgiGpuSchedulerReadbackLatencyFrames = 0;
            DdgiGpuSchedulerFallbackActive = 0;
            DdgiGpuSchedulerFallbackReason = string.Empty;
            DdgiGpuSchedulerConsideredProbeCount = 0;
            DdgiGpuSchedulerRequestCount = 0;
            DdgiGpuSchedulerPrimaryRayCount = 0;
            DdgiGpuSchedulerCandidateCount = 0;
            DdgiGpuSchedulerOverflowCount = 0;
            DdgiGpuSchedulerCandidateBufferOverflowCount = 0;
            DdgiGpuSchedulerPerBucketOverflowCount = 0;
            DdgiGpuSchedulerDuplicateRequestCount = 0;
            DdgiGpuSchedulerBudgetRejectedCount = 0;
            DdgiGpuSchedulerRequestBudgetRejectedCount = 0;
            DdgiGpuSchedulerPrimaryRayBudgetRejectedCount = 0;
            DdgiGpuSchedulerInvalidProbeCount = 0;
            DdgiGpuSchedulerCandidateOutputCapacity = 0;
            DdgiGpuSchedulerFullScan = 0;
            DdgiGpuSchedulerVisibleFrustumCandidateCount = 0;
            DdgiGpuSchedulerSafetyShellCandidateCount = 0;
            DdgiGpuSchedulerAgeRefreshCandidateCount = 0;
            DdgiGpuSchedulerHighVarianceCandidateCount = 0;
            DdgiGpuSchedulerLowConfidenceCandidateCount = 0;
            DdgiGpuSchedulerStableSkippedCount = 0;
            DdgiGpuSchedulerPriority0RequestCount = 0;
            DdgiGpuSchedulerPriority1RequestCount = 0;
            DdgiGpuSchedulerPriority2RequestCount = 0;
            DdgiGpuSchedulerPriority3RequestCount = 0;
            DdgiGpuSchedulerPriorityBucketMismatchSkipCount = 0;
            DdgiGpuSchedulerRequestBudgetSaturated = 0;
            DdgiGpuSchedulerPrimaryRayBudgetSaturated = 0;
            DdgiGpuSchedulerValidationValid = 0;
            DdgiGpuSchedulerValidationStatus = string.Empty;
            DdgiGpuSchedulerValidationCpuRequestCount = 0;
            DdgiGpuSchedulerValidationGpuRequestCount = 0;
            DdgiGpuSchedulerValidationComparedRequestCount = 0;
            DdgiGpuSchedulerValidationMismatchCount = 0;
            DdgiGpuSchedulerValidationSampleLimit = 0;
            DdgiGpuSchedulerValidationFirstMismatch = string.Empty;
            DdgiTraceDispatchGroupCount = 0;
            DdgiTraceProbeCount = 0;
            DdgiTraceRayCount = 0;
            DdgiBlendProbeCount = 0;
            DdgiRelocateClassifyProbeCount = 0;
            DdgiPublishProbeCount = 0;
            DdgiUpdateExecuted = 0;
            DdgiUpdateSkipReason = string.Empty;
            DdgiRayScratchBytes = 0;
            DdgiUpdatedAtlasBytes = 0;
            DdgiPublishExecuted = 0;
            DdgiPublishSkipReason = string.Empty;
            DdgiPublishedCacheLatencyFrames = 0;
            DdgiCacheGeneration = 0;
            DdgiLastUpdatedFrameSerial = 0;
            DdgiCacheWarmupState = DdgiRuntimeWarmupState.Disabled;
            DdgiStaleProbeCount = 0;
            DdgiAverageProbeAge = 0;
            DdgiMaxProbeAge = 0;
            DdgiFrustumUpdatePercentage = 0;
            DdgiOutsideFrustumUpdatePercentage = 0;
            DdgiResourceReinitializationCount = 0;
            DdgiTotalResourceReinitializationCount = 0;
            DdgiActiveLocalSlotCount = 0;
            DdgiLocalSlotGeneration = 0;
            DdgiLocalSlotInitBytes = 0;
            DdgiLocalVolumeEvictionReason = string.Empty;
            DdgiCacheClearReason = string.Empty;
            DdgiCameraMovementClass = DdgiCameraMovementClass.None;
            DdgiTextureBytes = 0;
            DdgiBufferBytes = 0;
            GpuSsgiTraceMicroseconds = 0;
            GpuSsgiTemporalMicroseconds = 0;
            GpuSsgiDenoiseMicroseconds = 0;
            GpuDdgiScheduleMicroseconds = 0;
            GpuDdgiScheduleP95Microseconds = 0;
            GpuDdgiScheduleOverBudget = 0;
            GpuDdgiScheduleResetMicroseconds = 0;
            GpuDdgiScheduleScoreMicroseconds = 0;
            GpuDdgiSchedulePrefixMicroseconds = 0;
            GpuDdgiScheduleCompactMicroseconds = 0;
            GpuDdgiScheduleFinalizeMicroseconds = 0;
            GpuDdgiScheduleReadbackMicroseconds = 0;
            GpuDdgiScheduleBarrierMicroseconds = 0;
            GpuDdgiTraceMicroseconds = 0;
            GpuDdgiBlendMicroseconds = 0;
            GpuDdgiRelocateClassifyMicroseconds = 0;
            GpuDdgiPublishMicroseconds = 0;
            GpuDdgiUpdateMicroseconds = 0;
            GpuSimpleDdgiTraceMicroseconds = 0;
            GpuSimpleDdgiTransportMicroseconds = 0;
            GpuSimpleDdgiBlendMicroseconds = 0;
            GpuFarFieldUpdateMicroseconds = 0;
            GpuFarFieldUpdateTimingValid = 0;
            GpuGiCompositeMicroseconds = 0;
            CpuAccelerationStructureBuildMicroseconds = 0;
            CpuAccelerationStructureBlasBuildMicroseconds = 0;
            CpuAccelerationStructureTlasBuildMicroseconds = 0;
            CpuAccelerationStructureInstanceUploadMicroseconds = 0;
            GpuAccelerationStructureBlasMicroseconds = 0;
            GpuAccelerationStructureTlasMicroseconds = 0;
            AccelerationStructureBottomLevelCount = 0;
            AccelerationStructureTopLevelInstanceCount = 0;
            AccelerationStructureBlasBuildCount = 0;
            AccelerationStructureTlasBuildCount = 0;
            AccelerationStructureTlasUpdateCount = 0;
            AccelerationStructureTlasSkipCount = 0;
            AccelerationStructureStreamingEnabled = 0;
            AccelerationStructureStaticInstanceCandidateCount = 0;
            AccelerationStructureStaticInstanceResidentCount = 0;
            AccelerationStructureStaticInstanceCulledCount = 0;
            AccelerationStructureBlasEvictionCount = 0;
            AccelerationStructureBlasEvictionBytes = 0;
            AccelerationStructureBlasBudgetRejectedCount = 0;
            AccelerationStructureBlasBytes = 0;
            AccelerationStructureTlasBytes = 0;
            AccelerationStructureRetiredBytes = 0;
            AccelerationStructureResidentBytes = 0;
            AccelerationStructureMemoryBudgetBytes = 0;
            AccelerationStructureBytes = 0;
            AccelerationStructureScratchBytes = 0;
            AccelerationStructureInstanceBufferBytes = 0;
            AccelerationStructureRayQueryMetadataBytes = 0;
            AccelerationStructureInstanceUploadBytes = 0;
            AccelerationStructureRayQueryMetadataUploadBytes = 0;
            AccelerationStructureFallbackReason = string.Empty;
            DdgiVolumeDiagnostics.Clear();
            AntiAliasingMode = AntiAliasingMode.None;
            AntiAliasingDebugView = AntiAliasingDebugView.None;
            AntiAliasingWidth = 0;
            AntiAliasingHeight = 0;
            AntiAliasingInputFormat = string.Empty;
            AntiAliasingOutputFormat = string.Empty;
            CpuFxaaRecordMicroseconds = 0;
            CpuSmaaEdgeRecordMicroseconds = 0;
            CpuSmaaBlendRecordMicroseconds = 0;
            CpuSmaaNeighborhoodRecordMicroseconds = 0;
            CpuMotionVectorRecordMicroseconds = 0;
            GpuMotionVectorMicroseconds = 0;
            GpuAntiAliasingMicroseconds = 0;
            SmaaLookupTexturesReady = 0;
            MotionVectorsEnabled = 0;
            JitterEnabled = 0;
            JitterX = 0;
            JitterY = 0;
            DebugToolingEnabled = false;
            DebugOverlayMode = DebugOverlayMode.None;
            CpuDebugSnapshotsEnabled = false;
            DebugSelectedObjectIndex = -1;
            DebugSelectedObjectName = string.Empty;
            DebugDrawSnapshot = DebugDrawFrameSnapshot.Empty;
            CpuDebugDrawBuildMicroseconds = 0;
            CpuDebugDrawRecordMicroseconds = 0;
            GpuDebugDrawMicroseconds = 0;
            CpuDebugOverlayRecordMicroseconds = 0;
            GpuDebugOverlayMicroseconds = 0;
            DebugObjectBoundsDrawn = 0;
            DebugMeshletBoundsDrawn = 0;
            DebugMeshletBoundsDropped = 0;
            DebugReflectionProbeVolumesDrawn = 0;
            DebugDdgiProbeVolumesDrawn = 0;
            DebugDecalVolumesDrawn = 0;
            HasCpuSnapshots = false;
            MaterialExtensionBufferSize = 0;
            MaterialExtensionDataBuffer = BufferHandle.Invalid;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Clear();
                _disposed = true;
            }
        }
    }
}
