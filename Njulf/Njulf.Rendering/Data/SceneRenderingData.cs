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
        public int OpaqueObjectCount { get; set; }
        public int MaskedObjectCount { get; set; }
        public int TransparentObjectCount { get; set; }
        public int OpaqueMeshletCount { get; set; }
        public int TransparentMeshletCount { get; set; }
        public int BlendMaterialCount { get; set; }
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
        public ulong ObjectBufferSize { get; set; }
        public ulong MaterialBufferSize { get; set; }
        public ulong InstanceBufferSize { get; set; }
        public ulong MeshletDrawBufferSize { get; set; }
        public ulong TransparentMeshletDrawBufferSize { get; set; }
        public ulong TiledLightHeaderBufferSize { get; set; }
        public ulong TiledLightIndexBufferSize { get; set; }
        public BufferHandle ObjectDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MaterialDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle InstanceBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TransparentMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightHeaderBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightIndexBuffer { get; set; } = BufferHandle.Invalid;
        public float Time { get; set; }
        
        public bool HasCpuSnapshots { get; set; }
        public List<GPUMeshletDrawCommand> MeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> OpaqueMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> TransparentMeshletDrawCommands { get; } = new();
        public List<GPUObjectData> ObjectData { get; } = new();
        public List<GPUMaterialData> MaterialData { get; } = new();
        
        private bool _disposed = false;
        
        public void Clear()
        {
            MeshletDrawCommands.Clear();
            OpaqueMeshletDrawCommands.Clear();
            TransparentMeshletDrawCommands.Clear();
            ObjectData.Clear();
            MaterialData.Clear();
            ObjectCount = 0;
            MeshletCount = 0;
            OpaqueObjectCount = 0;
            MaskedObjectCount = 0;
            TransparentObjectCount = 0;
            OpaqueMeshletCount = 0;
            TransparentMeshletCount = 0;
            BlendMaterialCount = 0;
            MaterialCount = 0;
            LightCount = 0;
            DirectionalLightCount = 0;
            LocalLightCount = 0;
            TextureCount = 0;
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
