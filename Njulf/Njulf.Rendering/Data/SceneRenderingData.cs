using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Rendering.Memory;

namespace Njulf.Rendering.Data
{
    public class SceneRenderingData : IDisposable
    {
        public int FrameIndex { get; set; }
        public uint ImageIndex { get; set; }
        public Vector4 ClearColor { get; set; } = new(0.2f, 0.2f, 0.2f, 1f);
        public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 ProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 ViewProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Vector3 CameraPosition { get; set; } = Vector3.Zero;
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
        public bool DepthPrePassEnabled { get; set; } = true;
        public bool HiZBuildEnabled { get; set; } = true;
        public bool TransparentPassEnabled { get; set; } = true;
        public TransparencyMode TransparencyMode { get; set; } = TransparencyMode.SortedAlphaBlend;
        public TransparencyDebugView TransparencyDebugView { get; set; } = TransparencyDebugView.None;
        public bool TransparentReceiveShadows { get; set; } = true;
        public DecalDebugView DecalDebugView { get; set; } = DecalDebugView.None;
        public bool GeometryDecalsEnabled { get; set; } = true;
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
        public long CpuTrailBeamRecordMicroseconds { get; set; }
        public long GpuParticleMicroseconds { get; set; }
        public long GpuTrailBeamMicroseconds { get; set; }
        public int ParticleDrawCallCount { get; set; }
        public BufferHandle ParticleInstanceBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle ParticleBatchBuffer { get; set; } = BufferHandle.Invalid;
        public ulong ParticleInstanceBufferSize { get; set; }
        public ulong ParticleBatchBufferSize { get; set; }
        public float OcclusionBias { get; set; } = 0.0005f;
        public uint DebugViewMode { get; set; }
        public int MaxLightsPerTile { get; set; }
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
        public long CpuLightCullRecordMicroseconds { get; set; }
        public long CpuForwardOpaqueRecordMicroseconds { get; set; }
        public long CpuTransparentRecordMicroseconds { get; set; }
        public long CpuBloomExtractRecordMicroseconds { get; set; }
        public long CpuBloomDownsampleRecordMicroseconds { get; set; }
        public long CpuBloomUpsampleRecordMicroseconds { get; set; }
        public long CpuFogRecordMicroseconds { get; set; }
        public long CpuCompositeRecordMicroseconds { get; set; }
        public long GpuDepthPrePassMicroseconds { get; set; }
        public long GpuHiZBuildMicroseconds { get; set; }
        public long GpuLightCullMicroseconds { get; set; }
        public long GpuForwardOpaqueMicroseconds { get; set; }
        public long GpuTransparentMicroseconds { get; set; }
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
        public int DepthTaskInvocations { get; set; }
        public int DepthFrustumCulledMeshletsGpu { get; set; }
        public int DepthEmittedMeshletsGpu { get; set; }
        public int ForwardTaskInvocations { get; set; }
        public int ForwardFrustumCulledMeshletsGpu { get; set; }
        public int ForwardOcclusionTestedMeshletsGpu { get; set; }
        public int ForwardOcclusionCulledMeshletsGpu { get; set; }
        public int ForwardEmittedMeshletsGpu { get; set; }
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
        public ulong TransparentMeshletDrawUploadBytes { get; set; }
        public ulong MaterialUploadBytes { get; set; }
        public ulong LightUploadBytes { get; set; }
        public uint HiZWidth { get; set; }
        public uint HiZHeight { get; set; }
        public bool BloomEnabled { get; set; }
        public bool DirectionalShadowPassEnabled { get; set; }
        public uint DirectionalShadowMapSize { get; set; }
        public int DirectionalShadowCascadeCount { get; set; }
        public int ShadowedDirectionalLightIndex { get; set; } = -1;
        public ShadowDebugView ShadowDebugView { get; set; } = ShadowDebugView.None;
        public float ShadowNormalBias { get; set; }
        public float ShadowSlopeScaledDepthBias { get; set; }
        public GPUShadowData ShadowData { get; set; }
        public bool SpotShadowsEnabled { get; set; }
        public int SpotShadowCandidateCount { get; set; }
        public int SpotShadowSelectedCount { get; set; }
        public int SpotShadowRejectedByBudgetCount { get; set; }
        public uint SpotShadowAtlasSize { get; set; }
        public uint SpotShadowTileSize { get; set; }
        public int SpotShadowAtlasCapacity { get; set; }
        public int SpotShadowAtlasUsedTiles { get; set; }
        public bool PointShadowsEnabled { get; set; }
        public int PointShadowCandidateCount { get; set; }
        public int PointShadowSelectedCount { get; set; }
        public int PointShadowRejectedByBudgetCount { get; set; }
        public uint PointShadowMapSize { get; set; }
        public int PointShadowRenderedFaceCount { get; set; }
        public int LocalShadowMeshletCount { get; set; }
        public GPUSpotShadow[] SpotShadowData { get; set; } = [];
        public GPUPointShadow[] PointShadowData { get; set; } = [];
        public GPULocalLightShadowIndex[] LocalLightShadowIndices { get; set; } = [];
        public int[] DirectionalShadowMeshletCounts { get; } = new int[ShadowSettings.MaxDirectionalCascades];
        public uint BloomMipCount { get; set; }
        public uint BloomBaseWidth { get; set; }
        public uint BloomBaseHeight { get; set; }
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
        public long GpuAntiAliasingMicroseconds { get; set; }
        public int SmaaLookupTexturesReady { get; set; }
        public int MotionVectorsEnabled { get; set; }
        public int JitterEnabled { get; set; }
        public float JitterX { get; set; }
        public float JitterY { get; set; }
        public ulong ObjectBufferSize { get; set; }
        public ulong MaterialBufferSize { get; set; }
        public ulong InstanceBufferSize { get; set; }
        public ulong MeshletDrawBufferSize { get; set; }
        public ulong SolidDepthMeshletDrawBufferSize { get; set; }
        public ulong MaskedDepthMeshletDrawBufferSize { get; set; }
        public ulong TransparentMeshletDrawBufferSize { get; set; }
        public ulong TiledLightHeaderBufferSize { get; set; }
        public ulong TiledLightIndexBufferSize { get; set; }
        public BufferHandle ObjectDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MaterialDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle InstanceBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle SolidDepthMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MaskedDepthMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TransparentMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightHeaderBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightIndexBuffer { get; set; } = BufferHandle.Invalid;
        public float Time { get; set; }
        
        public bool HasCpuSnapshots { get; set; }
        public List<GPUMeshletDrawCommand> MeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> OpaqueMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> SolidDepthMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> MaskedDepthMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> TransparentMeshletDrawCommands { get; } = new();
        public List<GPUObjectData> ObjectData { get; } = new();
        public List<GPUMaterialData> MaterialData { get; } = new();
        public List<GPUSkinningDispatch> SkinningDispatches { get; } = new();
        public List<GPUParticleBatch> ParticleBatches { get; } = new();
        
        private bool _disposed = false;
        
        public void Clear()
        {
            MeshletDrawCommands.Clear();
            OpaqueMeshletDrawCommands.Clear();
            SolidDepthMeshletDrawCommands.Clear();
            MaskedDepthMeshletDrawCommands.Clear();
            TransparentMeshletDrawCommands.Clear();
            ObjectData.Clear();
            MaterialData.Clear();
            SkinningDispatches.Clear();
            ParticleBatches.Clear();
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
            DecalDebugView = DecalDebugView.None;
            GeometryDecalsEnabled = true;
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
            CpuTrailBeamRecordMicroseconds = 0;
            GpuParticleMicroseconds = 0;
            GpuTrailBeamMicroseconds = 0;
            ParticleDrawCallCount = 0;
            ParticleInstanceBuffer = BufferHandle.Invalid;
            ParticleBatchBuffer = BufferHandle.Invalid;
            ParticleInstanceBufferSize = 0;
            ParticleBatchBufferSize = 0;
            DebugViewMode = 0;
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
            CpuLightCullRecordMicroseconds = 0;
            CpuForwardOpaqueRecordMicroseconds = 0;
            CpuTransparentRecordMicroseconds = 0;
            CpuBloomExtractRecordMicroseconds = 0;
            CpuBloomDownsampleRecordMicroseconds = 0;
            CpuBloomUpsampleRecordMicroseconds = 0;
            CpuFogRecordMicroseconds = 0;
            CpuCompositeRecordMicroseconds = 0;
            GpuDepthPrePassMicroseconds = 0;
            GpuHiZBuildMicroseconds = 0;
            GpuLightCullMicroseconds = 0;
            GpuForwardOpaqueMicroseconds = 0;
            GpuTransparentMicroseconds = 0;
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
            DepthTaskInvocations = 0;
            DepthFrustumCulledMeshletsGpu = 0;
            DepthEmittedMeshletsGpu = 0;
            ForwardTaskInvocations = 0;
            ForwardFrustumCulledMeshletsGpu = 0;
            ForwardOcclusionTestedMeshletsGpu = 0;
            ForwardOcclusionCulledMeshletsGpu = 0;
            ForwardEmittedMeshletsGpu = 0;
            MeshletCountTotal = 0;
            MeshletCountSubmittedCpu = 0;
            AvgTrianglesPerSubmittedMeshlet = 0;
            AvgVerticesPerSubmittedMeshlet = 0;
            SmallMeshletsUnder16Triangles = 0;
            SmallMeshletsUnder32Triangles = 0;
            ScenePayloadRebuilt = 0;
            ObjectUploadBytes = 0;
            InstanceUploadBytes = 0;
            MeshletDrawUploadBytes = 0;
            SolidDepthMeshletDrawUploadBytes = 0;
            MaskedDepthMeshletDrawUploadBytes = 0;
            TransparentMeshletDrawUploadBytes = 0;
            MaterialUploadBytes = 0;
            LightUploadBytes = 0;
            HiZWidth = 0;
            HiZHeight = 0;
            BloomEnabled = false;
            DirectionalShadowPassEnabled = false;
            DirectionalShadowMapSize = 0;
            DirectionalShadowCascadeCount = 0;
            ShadowedDirectionalLightIndex = -1;
            ShadowDebugView = ShadowDebugView.None;
            ShadowNormalBias = 0;
            ShadowSlopeScaledDepthBias = 0;
            ShadowData = default;
            SpotShadowsEnabled = false;
            SpotShadowCandidateCount = 0;
            SpotShadowSelectedCount = 0;
            SpotShadowRejectedByBudgetCount = 0;
            SpotShadowAtlasSize = 0;
            SpotShadowTileSize = 0;
            SpotShadowAtlasCapacity = 0;
            SpotShadowAtlasUsedTiles = 0;
            PointShadowsEnabled = false;
            PointShadowCandidateCount = 0;
            PointShadowSelectedCount = 0;
            PointShadowRejectedByBudgetCount = 0;
            PointShadowMapSize = 0;
            PointShadowRenderedFaceCount = 0;
            LocalShadowMeshletCount = 0;
            SpotShadowData = [];
            PointShadowData = [];
            LocalLightShadowIndices = [];
            Array.Clear(DirectionalShadowMeshletCounts, 0, DirectionalShadowMeshletCounts.Length);
            BloomMipCount = 0;
            BloomBaseWidth = 0;
            BloomBaseHeight = 0;
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
            GpuAntiAliasingMicroseconds = 0;
            SmaaLookupTexturesReady = 0;
            MotionVectorsEnabled = 0;
            JitterEnabled = 0;
            JitterX = 0;
            JitterY = 0;
            HasCpuSnapshots = false;
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
