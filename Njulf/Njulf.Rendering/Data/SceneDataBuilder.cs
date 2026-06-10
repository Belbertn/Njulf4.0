using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Owns scene-level GPU buffers and builds per-frame scene payloads.
    /// This type owns allocation, upload, bindless registration, growth, and synchronization
    /// for the scene buffer contract.
    /// </summary>
    public sealed unsafe class SceneDataBuilder : IDisposable
    {
        private const int TileSize = 16;
        private const int MaxLightsPerTile = 128;

        private const uint InitialObjectCapacity = 4096;
        private const uint InitialInstanceCapacity = 4096;
        private const uint InitialMeshletDrawCapacity = 65536;
        private const uint InitialTileCapacity = 4096;
        private const uint CpuMeshletCullingThreshold = 128;
        private const float MeshletLod1DistanceRatio = 12f;
        private const float MeshletLod2DistanceRatio = 32f;

        private static readonly ulong ObjectStride = (ulong)Marshal.SizeOf<GPUObjectData>();
        private static readonly ulong MeshletDrawStride = (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>();
        private static readonly ulong TiledLightHeaderStride = (ulong)Marshal.SizeOf<GPUTiledLightHeader>();
        private static readonly ulong TiledLightIndexStride = (ulong)Marshal.SizeOf<GPULightIndex>();

        private readonly VulkanContext _context;
        private readonly MeshManager _meshManager;
        private readonly BufferManager _bufferManager;
        private readonly StagingRing _stagingRing;
        private readonly SynchronizationManager _sync;
        private readonly MaterialManager _materialManager;
        private readonly TextureManager? _textureManager;
        private readonly object _lock = new object();

        private SceneBuffer _objectDataBuffer;
        private readonly SceneBuffer[] _instanceBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _meshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _transparentMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private SceneBuffer _tiledLightHeaderBuffer;
        private SceneBuffer _tiledLightIndexBuffer;

        private readonly List<GPUObjectData> _objectData = new List<GPUObjectData>();
        private readonly List<GPUMeshletDrawCommand> _meshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _transparentMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<TransparentMeshletDraw> _transparentSortScratch = new List<TransparentMeshletDraw>();
        private readonly Dictionary<MeshHandle, MeshInfo> _meshInfoCache = new Dictionary<MeshHandle, MeshInfo>();
        private readonly UploadState[] _instanceUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _meshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _transparentMeshletDrawUploadStates = new UploadState[FramesInFlight];

        private BindlessHeap? _registeredBindlessHeap;
        private UploadState _objectUploadState;
        private ScenePayloadSignature _lastPayloadSignature;
        private bool _hasCachedPayload;
        private uint _lastTileCountX;
        private uint _lastTileCountY;
        private ulong _lastUploadedBytes;
        private int _lastSceneUploadCount;
        private int _lastSceneUploadSkipped;
        private long _lastBuildMicroseconds;
        private int _opaqueObjectCount;
        private int _maskedObjectCount;
        private int _transparentObjectCount;
        private int _blendMaterialCount;
        private int _objectCandidatesCpu;
        private int _objectFrustumCulledCpu;
        private int _meshletCandidatesCpu;
        private int _meshletFrustumCulledCpu;
        private int _meshletLodSkippedCpu;
        private int _meshletLod0SubmittedCpu;
        private int _meshletLod1SubmittedCpu;
        private int _meshletLod2SubmittedCpu;
        private long _lastPayloadSignatureMicroseconds;
        private long _lastObjectCullMicroseconds;
        private long _lastMeshletCullMicroseconds;
        private long _lastUploadMicroseconds;
        private int _lastScenePayloadRebuilt;
        private ulong _lastObjectUploadBytes;
        private ulong _lastInstanceUploadBytes;
        private ulong _lastMeshletDrawUploadBytes;
        private ulong _lastTransparentMeshletDrawUploadBytes;
        private int _submittedMeshletCountCpu;
        private ulong _submittedMeshletTriangleSum;
        private ulong _submittedMeshletVertexSum;
        private int _submittedSmallMeshletsUnder16Triangles;
        private int _submittedSmallMeshletsUnder32Triangles;
        private bool _disposed;

        public bool CaptureCpuSnapshots { get; set; }

        public SceneDataBuilder(
            VulkanContext context,
            MeshManager meshManager,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync)
            : this(
                context,
                meshManager,
                bufferManager,
                stagingRing,
                sync,
                new MaterialManager(context, bufferManager, stagingRing, sync),
                textureManager: null)
        {
        }

        public SceneDataBuilder(
            VulkanContext context,
            MeshManager meshManager,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            TextureManager? textureManager)
            : this(
                context,
                meshManager,
                bufferManager,
                stagingRing,
                sync,
                CreateMaterialManager(context, bufferManager, stagingRing, sync, textureManager),
                textureManager)
        {
        }

        public SceneDataBuilder(
            VulkanContext context,
            MeshManager meshManager,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            MaterialManager materialManager,
            TextureManager? textureManager = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _sync = sync ?? throw new ArgumentNullException(nameof(sync));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _textureManager = textureManager;

            _objectDataBuffer = CreateSceneBuffer(InitialObjectCapacity, ObjectStride);

            for (int i = 0; i < FramesInFlight; i++)
            {
                _instanceBuffers[i] = CreateSceneBuffer(InitialInstanceCapacity, ObjectStride);
                _meshletDrawBuffers[i] = CreateSceneBuffer(InitialMeshletDrawCapacity, MeshletDrawStride);
                _transparentMeshletDrawBuffers[i] = CreateSceneBuffer(InitialMeshletDrawCapacity, MeshletDrawStride);
            }

            _tiledLightHeaderBuffer = CreateSceneBuffer(InitialTileCapacity, TiledLightHeaderStride);
            _tiledLightIndexBuffer = CreateSceneBuffer(InitialTileCapacity * MaxLightsPerTile, TiledLightIndexStride);

            Console.WriteLine("Scene data builder created.");
        }

        private static MaterialManager CreateMaterialManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            TextureManager? textureManager)
        {
            return textureManager == null
                ? new MaterialManager(context, bufferManager, stagingRing, sync)
                : new MaterialManager(context, bufferManager, stagingRing, sync, textureManager);
        }

        public SceneRenderingData Build(
            Scene scene,
            ICamera camera,
            uint screenWidth,
            uint screenHeight,
            CommandBuffer uploadCommandBuffer,
            bool useTiledLightCulling = true)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            if (uploadCommandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for scene data upload.", nameof(uploadCommandBuffer));

            lock (_lock)
            {
                long buildStart = Stopwatch.GetTimestamp();
                int frameIndex = _stagingRing.CurrentFrameIndex;
                _lastUploadedBytes = 0;
                _lastSceneUploadCount = 0;
                _lastSceneUploadSkipped = 0;
                _lastPayloadSignatureMicroseconds = 0;
                _lastObjectCullMicroseconds = 0;
                _lastMeshletCullMicroseconds = 0;
                _lastUploadMicroseconds = 0;
                _lastScenePayloadRebuilt = 0;
                _lastObjectUploadBytes = 0;
                _lastInstanceUploadBytes = 0;
                _lastMeshletDrawUploadBytes = 0;
                _lastTransparentMeshletDrawUploadBytes = 0;

                Frustum frustum = ExtractFrustum(camera.ViewProjectionMatrix);
                long signatureStart = Stopwatch.GetTimestamp();
                ScenePayloadSignature payloadSignature = ScenePayloadSignature.Create(
                    scene,
                    camera.ViewProjectionMatrix,
                    _materialManager.MaterialDataRevision);
                _lastPayloadSignatureMicroseconds = ElapsedMicroseconds(signatureStart);
                bool payloadRebuilt = false;
                if (!_hasCachedPayload || !_lastPayloadSignature.Equals(payloadSignature))
                {
                    _objectData.Clear();
                    _meshletDrawCommands.Clear();
                    _transparentMeshletDrawCommands.Clear();
                    _transparentSortScratch.Clear();
                    _opaqueObjectCount = 0;
                    _maskedObjectCount = 0;
                    _transparentObjectCount = 0;
                    _blendMaterialCount = 0;
                    _objectCandidatesCpu = 0;
                    _objectFrustumCulledCpu = 0;
                    _meshletCandidatesCpu = 0;
                    _meshletFrustumCulledCpu = 0;
                    _meshletLodSkippedCpu = 0;
                    _meshletLod0SubmittedCpu = 0;
                    _meshletLod1SubmittedCpu = 0;
                    _meshletLod2SubmittedCpu = 0;
                    _submittedMeshletCountCpu = 0;
                    _submittedMeshletTriangleSum = 0;
                    _submittedMeshletVertexSum = 0;
                    _submittedSmallMeshletsUnder16Triangles = 0;
                    _submittedSmallMeshletsUnder32Triangles = 0;

                    BuildCpuScenePayload(scene, camera.Position, frustum);
                    _lastPayloadSignature = payloadSignature;
                    _hasCachedPayload = true;
                    payloadRebuilt = true;
                    _lastScenePayloadRebuilt = 1;
                }

                long materialUploadStart = Stopwatch.GetTimestamp();
                _materialManager.UploadMaterials(uploadCommandBuffer);
                long materialUploadMicroseconds = ElapsedMicroseconds(materialUploadStart);

                uint tileCountX = Math.Max(1u, DivideRoundUp(screenWidth, TileSize));
                uint tileCountY = Math.Max(1u, DivideRoundUp(screenHeight, TileSize));
                uint totalTiles = checked(tileCountX * tileCountY);
                _lastTileCountX = tileCountX;
                _lastTileCountY = tileCountY;

                long uploadStart = Stopwatch.GetTimestamp();
                if (EnsureCapacity(ref _objectDataBuffer, CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer))
                    _objectUploadState = default;
                if (EnsureCapacity(ref _instanceBuffers[frameIndex], CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer))
                    _instanceUploadStates[frameIndex] = default;
                if (EnsureCapacity(ref _meshletDrawBuffers[frameIndex], CheckedCount(_meshletDrawCommands.Count), MeshletDrawStride, uploadCommandBuffer))
                    _meshletDrawUploadStates[frameIndex] = default;
                if (EnsureCapacity(ref _transparentMeshletDrawBuffers[frameIndex], CheckedCount(_transparentMeshletDrawCommands.Count), MeshletDrawStride, uploadCommandBuffer))
                    _transparentMeshletDrawUploadStates[frameIndex] = default;
                if (useTiledLightCulling)
                {
                    EnsureCapacity(ref _tiledLightHeaderBuffer, totalTiles, TiledLightHeaderStride, uploadCommandBuffer);
                    EnsureCapacity(ref _tiledLightIndexBuffer, checked(totalTiles * MaxLightsPerTile), TiledLightIndexStride, uploadCommandBuffer);
                }

                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_objectData), _objectDataBuffer, ref _objectUploadState, payloadRebuilt, uploadCommandBuffer, SceneUploadCategory.Object);
                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_objectData), _instanceBuffers[frameIndex], ref _instanceUploadStates[frameIndex], payloadRebuilt, uploadCommandBuffer, SceneUploadCategory.Instance);
                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_meshletDrawCommands), _meshletDrawBuffers[frameIndex], ref _meshletDrawUploadStates[frameIndex], payloadRebuilt, uploadCommandBuffer, SceneUploadCategory.MeshletDraw);
                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_transparentMeshletDrawCommands), _transparentMeshletDrawBuffers[frameIndex], ref _transparentMeshletDrawUploadStates[frameIndex], payloadRebuilt, uploadCommandBuffer, SceneUploadCategory.TransparentMeshletDraw);

                if (useTiledLightCulling)
                    ClearTiledLightBuffers(uploadCommandBuffer, totalTiles);

                RecordUploadReadBarriers(uploadCommandBuffer, frameIndex, useTiledLightCulling);
                UpdateRegisteredBindlessBuffers();
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
                _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                MeshletQualityStats globalMeshletStats = _meshManager.GetMeshletQualityStats();
                float avgSubmittedTriangles = _submittedMeshletCountCpu == 0
                    ? 0f
                    : (float)_submittedMeshletTriangleSum / _submittedMeshletCountCpu;
                float avgSubmittedVertices = _submittedMeshletCountCpu == 0
                    ? 0f
                    : (float)_submittedMeshletVertexSum / _submittedMeshletCountCpu;
                ulong materialUploadBytes = _materialManager.LastUploadBytes;
                ulong uploadedBytes = _lastUploadedBytes + materialUploadBytes;

                var sceneData = new SceneRenderingData
                {
                    ObjectCount = _objectData.Count,
                    MeshletCount = _meshletDrawCommands.Count + _transparentMeshletDrawCommands.Count,
                    OpaqueObjectCount = _opaqueObjectCount,
                    MaskedObjectCount = _maskedObjectCount,
                    TransparentObjectCount = _transparentObjectCount,
                    OpaqueMeshletCount = _meshletDrawCommands.Count,
                    TransparentMeshletCount = _transparentMeshletDrawCommands.Count,
                    BlendMaterialCount = _blendMaterialCount,
                    MaterialCount = _materialManager.RegisteredMaterialCount,
                    LightCount = 0,
                    TextureCount = _textureManager?.TextureCount ?? 0,
                    CurrentFrameIndex = (uint)frameIndex,
                    ViewMatrix = camera.ViewMatrix,
                    ProjectionMatrix = camera.ProjectionMatrix,
                    ViewProjectionMatrix = camera.ViewProjectionMatrix,
                    CameraPosition = camera.Position,
                    ScreenWidth = screenWidth,
                    ScreenHeight = screenHeight,
                    TileCountX = tileCountX,
                    TileCountY = tileCountY,
                    MaxLightsPerTile = MaxLightsPerTile,
                    UploadedBytes = uploadedBytes,
                    CpuSceneBuildMicroseconds = _lastBuildMicroseconds,
                    CpuPayloadSignatureMicroseconds = _lastPayloadSignatureMicroseconds,
                    CpuObjectCullMicroseconds = _lastObjectCullMicroseconds,
                    CpuMeshletCullMicroseconds = _lastMeshletCullMicroseconds,
                    CpuUploadMicroseconds = _lastUploadMicroseconds,
                    CpuMaterialUploadMicroseconds = materialUploadMicroseconds,
                    SceneUploadCount = _lastSceneUploadCount,
                    SceneUploadSkipped = _lastSceneUploadSkipped,
                    ObjectCandidatesCpu = _objectCandidatesCpu,
                    ObjectFrustumCulledCpu = _objectFrustumCulledCpu,
                    MeshletCandidatesCpu = _meshletCandidatesCpu,
                    MeshletFrustumCulledCpu = _meshletFrustumCulledCpu,
                    MeshletLodSkippedCpu = _meshletLodSkippedCpu,
                    MeshletLod0SubmittedCpu = _meshletLod0SubmittedCpu,
                    MeshletLod1SubmittedCpu = _meshletLod1SubmittedCpu,
                    MeshletLod2SubmittedCpu = _meshletLod2SubmittedCpu,
                    MeshletCountTotal = globalMeshletStats.MeshletCount,
                    MeshletCountSubmittedCpu = _submittedMeshletCountCpu,
                    AvgTrianglesPerSubmittedMeshlet = avgSubmittedTriangles,
                    AvgVerticesPerSubmittedMeshlet = avgSubmittedVertices,
                    SmallMeshletsUnder16Triangles = _submittedSmallMeshletsUnder16Triangles,
                    SmallMeshletsUnder32Triangles = _submittedSmallMeshletsUnder32Triangles,
                    ScenePayloadRebuilt = _lastScenePayloadRebuilt,
                    ObjectUploadBytes = _lastObjectUploadBytes,
                    InstanceUploadBytes = _lastInstanceUploadBytes,
                    MeshletDrawUploadBytes = _lastMeshletDrawUploadBytes,
                    TransparentMeshletDrawUploadBytes = _lastTransparentMeshletDrawUploadBytes,
                    MaterialUploadBytes = materialUploadBytes,
                    ObjectBufferSize = _objectDataBuffer.ByteSize,
                    MaterialBufferSize = _materialManager.MaterialBufferSize,
                    InstanceBufferSize = _instanceBuffers[frameIndex].ByteSize,
                    MeshletDrawBufferSize = _meshletDrawBuffers[frameIndex].ByteSize,
                    TransparentMeshletDrawBufferSize = _transparentMeshletDrawBuffers[frameIndex].ByteSize,
                    TiledLightHeaderBufferSize = _tiledLightHeaderBuffer.ByteSize,
                    TiledLightIndexBufferSize = _tiledLightIndexBuffer.ByteSize,
                    ObjectDataBuffer = _objectDataBuffer.Handle,
                    MaterialDataBuffer = _materialManager.MaterialBuffer,
                    InstanceBuffer = _instanceBuffers[frameIndex].Handle,
                    MeshletDrawBuffer = _meshletDrawBuffers[frameIndex].Handle,
                    TransparentMeshletDrawBuffer = _transparentMeshletDrawBuffers[frameIndex].Handle,
                    TiledLightHeaderBuffer = _tiledLightHeaderBuffer.Handle,
                    TiledLightIndexBuffer = _tiledLightIndexBuffer.Handle
                };

                if (CaptureCpuSnapshots)
                {
                    sceneData.HasCpuSnapshots = true;
                    sceneData.ObjectData.AddRange(_objectData);
                    sceneData.MaterialData.AddRange(_materialManager.GetMaterialDataSnapshot());
                    sceneData.MeshletDrawCommands.AddRange(_meshletDrawCommands);
                    sceneData.OpaqueMeshletDrawCommands.AddRange(_meshletDrawCommands);
                    sceneData.TransparentMeshletDrawCommands.AddRange(_transparentMeshletDrawCommands);
                }

                return sceneData;
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            lock (_lock)
            {
                _registeredBindlessHeap = bindlessHeap;
                UpdateRegisteredBindlessBuffers();
            }
        }

        private void BuildCpuScenePayload(Scene scene, Vector3 cameraPosition, Frustum frustum)
        {
            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                long objectStart = Stopwatch.GetTimestamp();
                _objectCandidatesCpu++;

                if (!renderObject.Visible)
                {
                    _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);
                    continue;
                }

                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                {
                    _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);
                    continue;
                }

                MeshInfo meshInfo = GetValidatedMeshInfo(meshHandle);
                if (!IsVisible(renderObject, meshInfo, frustum, out bool objectFullyInsideFrustum))
                {
                    _objectFrustumCulledCpu++;
                    _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);
                    continue;
                }

                MaterialHandle materialHandle = ResolveRenderObjectMaterialHandle(
                    renderObject.Material,
                    _materialManager.DefaultMaterialHandle,
                    renderObject.Name);
                int materialIndex = _materialManager.ResolveMaterialIndex(materialHandle);
                GPUMaterialData material = _materialManager.GetMaterialData(materialHandle);
                MaterialRenderMode renderMode = MaterialRenderModeExtensions.FromGpuMaterial(material);

                switch (renderMode)
                {
                    case MaterialRenderMode.Blend:
                        _transparentObjectCount++;
                        break;
                    case MaterialRenderMode.Mask:
                        _maskedObjectCount++;
                        break;
                    default:
                        _opaqueObjectCount++;
                        break;
                }

                Matrix4x4 worldInverseTranspose;
                try
                {
                    worldInverseTranspose = renderObject.WorldMatrix.Invert().Transpose();
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(
                        $"Render object '{renderObject.Name}' has a non-invertible world matrix and cannot be uploaded.",
                        ex);
                }

                _objectData.Add(new GPUObjectData
                {
                    WorldMatrix = renderObject.WorldMatrix,
                    WorldMatrixInverseTranspose = worldInverseTranspose,
                    MeshIndex = meshHandle.Index,
                    MaterialIndex = materialIndex,
                    Padding0 = 0,
                    Padding1 = 0
                });

                uint instanceId = (uint)(_objectData.Count - 1);
                Vector3 localCenter = (ToCoreVector(meshInfo.BoundingBoxMin) + ToCoreVector(meshInfo.BoundingBoxMax)) * 0.5f;
                Vector3 worldCenter = TransformPoint(localCenter, renderObject.WorldMatrix);
                float localRadius = Distance(ToCoreVector(meshInfo.BoundingBoxMin), localCenter);
                float worldRadius = localRadius * GetMaxScale(renderObject.WorldMatrix);
                float transparentDistanceSquared = 0f;
                if (renderMode == MaterialRenderMode.Blend)
                    transparentDistanceSquared = DistanceSquared(cameraPosition, worldCenter);

                _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);
                long meshletStart = Stopwatch.GetTimestamp();
                int lodLevel = SelectMeshletLodLevel(cameraPosition, worldCenter, worldRadius);
                MeshletLodRange meshletRange = GetMeshletLodRange(meshInfo, lodLevel, out int effectiveLodLevel);
                if (meshInfo.MeshletCount > meshletRange.Count)
                    _meshletLodSkippedCpu += checked((int)(meshInfo.MeshletCount - meshletRange.Count));

                for (uint i = 0; i < meshletRange.Count; i++)
                {
                    _meshletCandidatesCpu++;
                    uint meshletIndex = meshletRange.Offset + i;
                    if (!objectFullyInsideFrustum &&
                        meshletRange.Count >= CpuMeshletCullingThreshold &&
                        !MeshletIntersectsFrustum(meshletIndex, renderObject.WorldMatrix, frustum))
                    {
                        _meshletFrustumCulledCpu++;
                        continue;
                    }

                    Meshlet meshlet = _meshManager.GetMeshlet(meshletIndex);
                    RecordSubmittedMeshlet(meshlet);
                    RecordSubmittedMeshletLod(effectiveLodLevel);
                    var command = new GPUMeshletDrawCommand
                    {
                        MeshletIndex = meshletIndex,
                        InstanceId = instanceId,
                        MaterialIndex = (uint)materialIndex,
                        Padding = 0
                    };

                    if (renderMode == MaterialRenderMode.Blend)
                    {
                        _transparentSortScratch.Add(new TransparentMeshletDraw(command, transparentDistanceSquared));
                    }
                    else
                    {
                        _meshletDrawCommands.Add(command);
                    }
                }
                _lastMeshletCullMicroseconds += ElapsedMicroseconds(meshletStart);
            }

            _transparentSortScratch.Sort(CompareTransparentMeshlets);
            foreach (TransparentMeshletDraw draw in _transparentSortScratch)
                _transparentMeshletDrawCommands.Add(draw.Command);

            foreach (GPUMaterialData material in _materialManager.GetMaterialDataSnapshot())
            {
                if (MaterialRenderModeExtensions.FromGpuMaterial(material) == MaterialRenderMode.Blend)
                    _blendMaterialCount++;
            }
        }

        private MeshInfo GetValidatedMeshInfo(MeshHandle meshHandle)
        {
            if (_meshInfoCache.TryGetValue(meshHandle, out MeshInfo cached))
                return cached;

            MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
            _meshManager.ValidateMeshInfoRanges(meshInfo);
            _meshInfoCache.Add(meshHandle, meshInfo);
            return meshInfo;
        }

        private static bool IsVisible(RenderObject renderObject, MeshInfo meshInfo, Frustum frustum, out bool fullyInsideFrustum)
        {
            var localBounds = new BoundingBox(ToCoreVector(meshInfo.BoundingBoxMin), ToCoreVector(meshInfo.BoundingBoxMax));
            BoundingBox worldBounds = TransformBoundingBox(localBounds, renderObject.WorldMatrix);
            if (!IntersectsFrustum(worldBounds, frustum))
            {
                fullyInsideFrustum = false;
                return false;
            }

            fullyInsideFrustum = ContainsFrustum(worldBounds, frustum);
            return true;
        }

        private bool MeshletIntersectsFrustum(uint meshletIndex, Matrix4x4 worldMatrix, Frustum frustum)
        {
            Meshlet meshlet = _meshManager.GetMeshlet(meshletIndex);
            Vector3 worldCenter = TransformPoint(ToCoreVector(meshlet.BoundingSphereCenter), worldMatrix);
            float worldRadius = meshlet.BoundingSphereRadius * GetMaxScale(worldMatrix);
            return IntersectsFrustum(new BoundingSphere(worldCenter, worldRadius), frustum);
        }

        private void RecordSubmittedMeshlet(Meshlet meshlet)
        {
            _submittedMeshletCountCpu++;
            _submittedMeshletTriangleSum += meshlet.LocalTriangleCount;
            _submittedMeshletVertexSum += meshlet.LocalVertexCount;
            if (meshlet.LocalTriangleCount < 16)
                _submittedSmallMeshletsUnder16Triangles++;
            if (meshlet.LocalTriangleCount < 32)
                _submittedSmallMeshletsUnder32Triangles++;
        }

        private void RecordSubmittedMeshletLod(int lodLevel)
        {
            switch (lodLevel)
            {
                case 0:
                    _meshletLod0SubmittedCpu++;
                    break;
                case 1:
                    _meshletLod1SubmittedCpu++;
                    break;
                default:
                    _meshletLod2SubmittedCpu++;
                    break;
            }
        }

        internal static int SelectMeshletLodLevel(Vector3 cameraPosition, Vector3 worldCenter, float worldRadius)
        {
            float effectiveRadius = Math.Max(worldRadius, 1f);
            float distanceFromSurface = Math.Max(0f, Distance(cameraPosition, worldCenter) - effectiveRadius);
            float distanceRatio = distanceFromSurface / effectiveRadius;

            if (distanceRatio >= MeshletLod2DistanceRatio)
                return 2;
            if (distanceRatio >= MeshletLod1DistanceRatio)
                return 1;
            return 0;
        }

        private static MeshletLodRange GetMeshletLodRange(MeshInfo meshInfo, int lodLevel, out int effectiveLodLevel)
        {
            MeshletLodRange range = lodLevel switch
            {
                2 => new MeshletLodRange(meshInfo.MeshletLod2Offset, meshInfo.MeshletLod2Count),
                1 => new MeshletLodRange(meshInfo.MeshletLod1Offset, meshInfo.MeshletLod1Count),
                _ => new MeshletLodRange(meshInfo.MeshletOffset, meshInfo.MeshletCount)
            };

            if (range.Count > 0)
            {
                effectiveLodLevel = lodLevel;
                return range;
            }

            if (meshInfo.MeshletCount > 0)
            {
                effectiveLodLevel = 0;
                return new MeshletLodRange(meshInfo.MeshletOffset, meshInfo.MeshletCount);
            }
            if (meshInfo.MeshletLod1Count > 0)
            {
                effectiveLodLevel = 1;
                return new MeshletLodRange(meshInfo.MeshletLod1Offset, meshInfo.MeshletLod1Count);
            }

            effectiveLodLevel = 2;
            return new MeshletLodRange(meshInfo.MeshletLod2Offset, meshInfo.MeshletLod2Count);
        }

        private static Vector3 ToCoreVector(System.Numerics.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static int CompareTransparentMeshlets(TransparentMeshletDraw left, TransparentMeshletDraw right)
        {
            int distanceComparison = right.DistanceSquared.CompareTo(left.DistanceSquared);
            if (distanceComparison != 0)
                return distanceComparison;

            int materialComparison = left.Command.MaterialIndex.CompareTo(right.Command.MaterialIndex);
            if (materialComparison != 0)
                return materialComparison;

            int instanceComparison = left.Command.InstanceId.CompareTo(right.Command.InstanceId);
            if (instanceComparison != 0)
                return instanceComparison;

            return left.Command.MeshletIndex.CompareTo(right.Command.MeshletIndex);
        }

        internal static Vector3 TransformPoint(Vector3 position, Matrix4x4 transform)
        {
            return new Vector3(
                position.X * transform.M11 + position.Y * transform.M21 + position.Z * transform.M31 + transform.M41,
                position.X * transform.M12 + position.Y * transform.M22 + position.Z * transform.M32 + transform.M42,
                position.X * transform.M13 + position.Y * transform.M23 + position.Z * transform.M33 + transform.M43);
        }

        internal static float DistanceSquared(Vector3 left, Vector3 right)
        {
            float x = left.X - right.X;
            float y = left.Y - right.Y;
            float z = left.Z - right.Z;
            return x * x + y * y + z * z;
        }

        private static float Distance(Vector3 left, Vector3 right)
        {
            return MathF.Sqrt(DistanceSquared(left, right));
        }

        private static float GetMaxScale(Matrix4x4 matrix)
        {
            Vector3 scale = matrix.Scale;
            return Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
        }

        internal static MaterialHandle ResolveRenderObjectMaterialHandle(
            object? material,
            MaterialHandle defaultMaterialHandle,
            string objectName)
        {
            if (material == null)
                return defaultMaterialHandle;

            if (material is MaterialHandle materialHandle)
                return materialHandle;

            if (material is int materialIndex && materialIndex == 0)
                return defaultMaterialHandle;

            string materialType = material.GetType().FullName ?? material.GetType().Name;
            throw new InvalidOperationException(
                $"Render object '{objectName}' has unsupported material type '{materialType}'. " +
                $"Production scene upload expects {nameof(MaterialHandle)} or null for the default material.");
        }

        public static Frustum ExtractFrustum(Matrix4x4 viewProjection)
        {
            return new Frustum
            {
                Left = NormalizePlane(new Vector4(
                    viewProjection.M11 + viewProjection.M14,
                    viewProjection.M21 + viewProjection.M24,
                    viewProjection.M31 + viewProjection.M34,
                    viewProjection.M41 + viewProjection.M44)),
                Right = NormalizePlane(new Vector4(
                    -viewProjection.M11 + viewProjection.M14,
                    -viewProjection.M21 + viewProjection.M24,
                    -viewProjection.M31 + viewProjection.M34,
                    -viewProjection.M41 + viewProjection.M44)),
                Bottom = NormalizePlane(new Vector4(
                    viewProjection.M12 + viewProjection.M14,
                    viewProjection.M22 + viewProjection.M24,
                    viewProjection.M32 + viewProjection.M34,
                    viewProjection.M42 + viewProjection.M44)),
                Top = NormalizePlane(new Vector4(
                    -viewProjection.M12 + viewProjection.M14,
                    -viewProjection.M22 + viewProjection.M24,
                    -viewProjection.M32 + viewProjection.M34,
                    -viewProjection.M42 + viewProjection.M44)),
                Near = NormalizePlane(new Vector4(
                    viewProjection.M13,
                    viewProjection.M23,
                    viewProjection.M33,
                    viewProjection.M43)),
                Far = NormalizePlane(new Vector4(
                    -viewProjection.M13 + viewProjection.M14,
                    -viewProjection.M23 + viewProjection.M24,
                    -viewProjection.M33 + viewProjection.M34,
                    -viewProjection.M43 + viewProjection.M44))
            };
        }

        public static bool IntersectsFrustum(BoundingBox bounds, Frustum frustum)
        {
            return IntersectsPlane(bounds, frustum.Left) &&
                   IntersectsPlane(bounds, frustum.Right) &&
                   IntersectsPlane(bounds, frustum.Bottom) &&
                   IntersectsPlane(bounds, frustum.Top) &&
                   IntersectsPlane(bounds, frustum.Near) &&
                   IntersectsPlane(bounds, frustum.Far);
        }

        public static bool ContainsFrustum(BoundingBox bounds, Frustum frustum)
        {
            return InsidePlane(bounds, frustum.Left) &&
                   InsidePlane(bounds, frustum.Right) &&
                   InsidePlane(bounds, frustum.Bottom) &&
                   InsidePlane(bounds, frustum.Top) &&
                   InsidePlane(bounds, frustum.Near) &&
                   InsidePlane(bounds, frustum.Far);
        }

        public static bool IntersectsFrustum(BoundingSphere sphere, Frustum frustum)
        {
            return IntersectsPlane(sphere, frustum.Left) &&
                   IntersectsPlane(sphere, frustum.Right) &&
                   IntersectsPlane(sphere, frustum.Bottom) &&
                   IntersectsPlane(sphere, frustum.Top) &&
                   IntersectsPlane(sphere, frustum.Near) &&
                   IntersectsPlane(sphere, frustum.Far);
        }

        public static BoundingBox TransformBoundingBox(BoundingBox bounds, Matrix4x4 transform)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            corners[0] = bounds.Min;
            corners[1] = new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Min.Z);
            corners[2] = new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Min.Z);
            corners[3] = new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Min.Z);
            corners[4] = new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Max.Z);
            corners[5] = new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Max.Z);
            corners[6] = new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Max.Z);
            corners[7] = bounds.Max;

            Vector3 min = corners[0] * transform;
            Vector3 max = min;

            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 transformed = corners[i] * transform;
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            return new BoundingBox(min, max);
        }

        private static bool IntersectsPlane(BoundingBox bounds, Vector4 plane)
        {
            Vector3 positiveVertex = new Vector3(
                plane.X >= 0f ? bounds.Max.X : bounds.Min.X,
                plane.Y >= 0f ? bounds.Max.Y : bounds.Min.Y,
                plane.Z >= 0f ? bounds.Max.Z : bounds.Min.Z);

            return PlaneDistance(plane, positiveVertex) >= 0f;
        }

        private static bool InsidePlane(BoundingBox bounds, Vector4 plane)
        {
            Vector3 negativeVertex = new Vector3(
                plane.X >= 0f ? bounds.Min.X : bounds.Max.X,
                plane.Y >= 0f ? bounds.Min.Y : bounds.Max.Y,
                plane.Z >= 0f ? bounds.Min.Z : bounds.Max.Z);

            return PlaneDistance(plane, negativeVertex) >= 0f;
        }

        private static bool IntersectsPlane(BoundingSphere sphere, Vector4 plane)
        {
            return PlaneDistance(plane, sphere.Center) >= -sphere.Radius;
        }

        private static float PlaneDistance(Vector4 plane, Vector3 point)
        {
            return plane.X * point.X + plane.Y * point.Y + plane.Z * point.Z + plane.W;
        }

        private static Vector4 NormalizePlane(Vector4 plane)
        {
            float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
            if (length <= float.Epsilon)
                throw new InvalidOperationException("Cannot normalize a frustum plane with a zero-length normal.");

            return plane / length;
        }

        private bool EnsureCapacity(
            ref SceneBuffer buffer,
            uint requiredElementCount,
            ulong stride,
            CommandBuffer commandBuffer)
        {
            if (requiredElementCount <= buffer.ElementCapacity)
                return false;

            WaitForOtherInFlightFrames();

            uint newCapacity = buffer.ElementCapacity;
            while (newCapacity < requiredElementCount)
                newCapacity = checked(newCapacity * 2);

            SceneBuffer oldBuffer = buffer;
            buffer = CreateSceneBuffer(newCapacity, stride);
            _bufferManager.DestroyBuffer(oldBuffer.Handle);
            return true;
        }

        private void WaitForOtherInFlightFrames()
        {
            int currentFrame = _stagingRing.CurrentFrameIndex;
            for (int i = 0; i < FramesInFlight; i++)
            {
                if (i != currentFrame)
                    _sync.WaitForFence(i);
            }
        }

        private SceneBuffer CreateSceneBuffer(uint elementCapacity, ulong stride)
        {
            if (elementCapacity == 0)
                throw new ArgumentOutOfRangeException(nameof(elementCapacity));
            if (stride == 0)
                throw new ArgumentOutOfRangeException(nameof(stride));

            ulong byteSize = checked(elementCapacity * stride);
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.TransferSrcBit,
                true);

            return new SceneBuffer(handle, elementCapacity, byteSize);
        }

        private ulong UploadSpan<T>(ReadOnlySpan<T> data, SceneBuffer destination, CommandBuffer commandBuffer, SceneUploadCategory category)
            where T : unmanaged
        {
            if (data.IsEmpty)
                return 0;

            ulong dataSize = checked((ulong)data.Length * (ulong)sizeof(T));
            if (dataSize > destination.ByteSize)
                throw new InvalidOperationException("Scene upload exceeds destination buffer capacity.");

            var (stagingBuffer, stagingOffset) = _stagingRing.Allocate(dataSize);
            void* mappedData = _bufferManager.GetMappedPointer(stagingBuffer);

            fixed (T* source = data)
            {
                global::System.Buffer.MemoryCopy(
                    source,
                    (byte*)mappedData + stagingOffset,
                    dataSize,
                    dataSize);
            }

            _bufferManager.FlushBuffer(stagingBuffer, stagingOffset, dataSize);

            var copy = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = 0,
                Size = dataSize
            };

            _context.Api.CmdCopyBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(stagingBuffer),
                _bufferManager.GetBuffer(destination.Handle),
                1,
                &copy);

            _lastUploadedBytes += dataSize;
            _lastSceneUploadCount++;
            AddUploadBytes(category, dataSize);
            return dataSize;
        }

        private void UploadSpanIfNeeded<T>(
            ReadOnlySpan<T> data,
            SceneBuffer destination,
            ref UploadState uploadState,
            bool contentChanged,
            CommandBuffer commandBuffer,
            SceneUploadCategory category)
            where T : unmanaged
        {
            if (!contentChanged && uploadState.Matches(data.Length))
            {
                _lastSceneUploadSkipped++;
                return;
            }

            UploadSpan(data, destination, commandBuffer, category);
            uploadState = UploadState.Valid(data.Length);
        }

        private void AddUploadBytes(SceneUploadCategory category, ulong dataSize)
        {
            switch (category)
            {
                case SceneUploadCategory.Object:
                    _lastObjectUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.Instance:
                    _lastInstanceUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.MeshletDraw:
                    _lastMeshletDrawUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.TransparentMeshletDraw:
                    _lastTransparentMeshletDrawUploadBytes += dataSize;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(category), category, null);
            }
        }

        private void ClearTiledLightBuffers(CommandBuffer commandBuffer, uint totalTiles)
        {
            ulong headerBytes = checked(totalTiles * TiledLightHeaderStride);
            ulong indexBytes = checked(totalTiles * MaxLightsPerTile * TiledLightIndexStride);

            if (headerBytes > 0)
                _context.Api.CmdFillBuffer(commandBuffer, _bufferManager.GetBuffer(_tiledLightHeaderBuffer.Handle), 0, headerBytes, 0);
            if (indexBytes > 0)
                _context.Api.CmdFillBuffer(commandBuffer, _bufferManager.GetBuffer(_tiledLightIndexBuffer.Handle), 0, indexBytes, 0);
        }

        private void RecordUploadReadBarriers(CommandBuffer commandBuffer, int frameIndex, bool includeTiledLightBuffers)
        {
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[6];
            barriers[0] = CreateShaderReadBarrier(_objectDataBuffer.Handle);
            barriers[1] = CreateShaderReadBarrier(_instanceBuffers[frameIndex].Handle);
            barriers[2] = CreateShaderReadBarrier(_meshletDrawBuffers[frameIndex].Handle);
            barriers[3] = CreateShaderReadBarrier(_transparentMeshletDrawBuffers[frameIndex].Handle);

            uint barrierCount = 4;
            if (includeTiledLightBuffers)
            {
                barriers[4] = CreateComputeWriteBarrier(_tiledLightHeaderBuffer.Handle);
                barriers[5] = CreateComputeWriteBarrier(_tiledLightIndexBuffer.Handle);
                barrierCount = 6;
            }

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = barrierCount,
                PBufferMemoryBarriers = barriers
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private BufferMemoryBarrier2 CreateShaderReadBarrier(BufferHandle handle)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.TaskShaderBitExt |
                               PipelineStageFlags2.MeshShaderBitExt |
                               PipelineStageFlags2.VertexShaderBit |
                               PipelineStageFlags2.FragmentShaderBit |
                               PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(handle),
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        private BufferMemoryBarrier2 CreateComputeWriteBarrier(BufferHandle handle)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(handle),
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        private void UpdateRegisteredBindlessBuffers()
        {
            if (_registeredBindlessHeap == null)
                return;

            RegisterStorageBuffer(BindlessIndex.ObjectDataBuffer, _objectDataBuffer.Handle);
            RegisterStorageBuffer(BindlessIndex.InstanceBufferBase, _instanceBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.InstanceBufferFrame1, _instanceBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.MeshletDrawBufferBase, _meshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.MeshletDrawBufferFrame1, _meshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.TransparentMeshletDrawBufferBase, _transparentMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.TransparentMeshletDrawBufferFrame1, _transparentMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.TiledLightHeaderBuffer, _tiledLightHeaderBuffer.Handle);
            RegisterStorageBuffer(BindlessIndex.TiledLightIndicesBuffer, _tiledLightIndexBuffer.Handle);
        }

        private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
        {
            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _registeredBindlessHeap!.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private static uint CheckedCount(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return (uint)count;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        public BufferHandle ObjectDataBuffer => _objectDataBuffer.Handle;
        public BufferHandle MaterialDataBuffer => _materialManager.MaterialBuffer;
        public BufferHandle TiledLightHeaderBuffer => _tiledLightHeaderBuffer.Handle;
        public BufferHandle TiledLightIndexBuffer => _tiledLightIndexBuffer.Handle;

        public ulong ObjectBufferSize => _objectDataBuffer.ByteSize;
        public ulong MaterialBufferSize => _materialManager.MaterialBufferSize;
        public ulong TiledLightHeaderBufferSize => _tiledLightHeaderBuffer.ByteSize;
        public ulong TiledLightIndexBufferSize => _tiledLightIndexBuffer.ByteSize;
        public ulong LastUploadedBytes => _lastUploadedBytes;
        public int LastSceneUploadCount => _lastSceneUploadCount;
        public int LastSceneUploadSkipped => _lastSceneUploadSkipped;
        public long LastBuildMicroseconds => _lastBuildMicroseconds;
        public uint LastTileCountX => _lastTileCountX;
        public uint LastTileCountY => _lastTileCountY;

        public BufferHandle GetInstanceBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _instanceBuffers[frameIndex].Handle;
        }

        public BufferHandle GetMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _meshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetTransparentMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _transparentMeshletDrawBuffers[frameIndex].Handle;
        }

        public ulong GetInstanceBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _instanceBuffers[frameIndex].ByteSize;
        }

        public ulong GetMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _meshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetTransparentMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _transparentMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;

            lock (_lock)
            {
                DestroyIfValid(_objectDataBuffer.Handle);
                DestroyIfValid(_tiledLightHeaderBuffer.Handle);
                DestroyIfValid(_tiledLightIndexBuffer.Handle);

                for (int i = 0; i < FramesInFlight; i++)
                {
                    DestroyIfValid(_instanceBuffers[i].Handle);
                    DestroyIfValid(_meshletDrawBuffers[i].Handle);
                    DestroyIfValid(_transparentMeshletDrawBuffers[i].Handle);
                }

                _objectData.Clear();
                _meshletDrawCommands.Clear();
                _transparentMeshletDrawCommands.Clear();
                _transparentSortScratch.Clear();
                _hasCachedPayload = false;
            }

            Console.WriteLine("Scene data builder disposed.");
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        ~SceneDataBuilder()
        {
            Dispose(false);
        }

        private readonly struct SceneBuffer
        {
            public SceneBuffer(BufferHandle handle, uint elementCapacity, ulong byteSize)
            {
                Handle = handle;
                ElementCapacity = elementCapacity;
                ByteSize = byteSize;
            }

            public BufferHandle Handle { get; }
            public uint ElementCapacity { get; }
            public ulong ByteSize { get; }
        }

        private readonly struct TransparentMeshletDraw
        {
            public TransparentMeshletDraw(GPUMeshletDrawCommand command, float distanceSquared)
            {
                Command = command;
                DistanceSquared = distanceSquared;
            }

            public GPUMeshletDrawCommand Command { get; }
            public float DistanceSquared { get; }
        }

        private readonly struct MeshletLodRange
        {
            public MeshletLodRange(uint offset, uint count)
            {
                Offset = offset;
                Count = count;
            }

            public uint Offset { get; }
            public uint Count { get; }
        }

        private readonly struct UploadState
        {
            private readonly int _elementCount;
            private readonly bool _valid;

            private UploadState(int elementCount, bool valid)
            {
                _elementCount = elementCount;
                _valid = valid;
            }

            public static UploadState Valid(int elementCount)
            {
                return new UploadState(elementCount, valid: true);
            }

            public bool Matches(int elementCount)
            {
                return _valid && _elementCount == elementCount;
            }
        }

        private enum SceneUploadCategory
        {
            Object,
            Instance,
            MeshletDraw,
            TransparentMeshletDraw
        }

        private readonly struct ScenePayloadSignature : IEquatable<ScenePayloadSignature>
        {
            private readonly int _objectCount;
            private readonly int _hash;

            private ScenePayloadSignature(int objectCount, int hash)
            {
                _objectCount = objectCount;
                _hash = hash;
            }

            public static ScenePayloadSignature Create(Scene scene, Matrix4x4 viewProjection, uint materialDataRevision)
            {
                var hash = new HashCode();
                hash.Add(viewProjection);
                hash.Add(materialDataRevision);
                hash.Add(scene.RenderObjects.Count);

                foreach (RenderObject renderObject in scene.RenderObjects)
                {
                    hash.Add(RuntimeHelpers.GetHashCode(renderObject));
                    hash.Add(renderObject.Visible);
                    hash.Add(renderObject.WorldMatrix);
                    hash.Add(renderObject.Mesh);
                    hash.Add(renderObject.Material);
                }

                return new ScenePayloadSignature(scene.RenderObjects.Count, hash.ToHashCode());
            }

            public bool Equals(ScenePayloadSignature other)
            {
                return _objectCount == other._objectCount && _hash == other._hash;
            }

            public override bool Equals(object? obj)
            {
                return obj is ScenePayloadSignature other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_objectCount, _hash);
            }
        }
    }

    public struct Frustum
    {
        public Vector4 Left;
        public Vector4 Right;
        public Vector4 Bottom;
        public Vector4 Top;
        public Vector4 Near;
        public Vector4 Far;
    }
}
