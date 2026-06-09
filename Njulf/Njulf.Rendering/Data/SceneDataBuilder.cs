using System;
using System.Collections.Generic;
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

        private BindlessHeap? _registeredBindlessHeap;
        private uint _lastTileCountX;
        private uint _lastTileCountY;
        private ulong _lastUploadedBytes;
        private int _opaqueObjectCount;
        private int _maskedObjectCount;
        private int _transparentObjectCount;
        private int _blendMaterialCount;
        private bool _disposed;

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
                int frameIndex = _stagingRing.CurrentFrameIndex;
                _objectData.Clear();
                _meshletDrawCommands.Clear();
                _transparentMeshletDrawCommands.Clear();
                _transparentSortScratch.Clear();
                _meshInfoCache.Clear();
                _lastUploadedBytes = 0;
                _opaqueObjectCount = 0;
                _maskedObjectCount = 0;
                _transparentObjectCount = 0;
                _blendMaterialCount = 0;

                Frustum frustum = ExtractFrustum(camera.ViewProjectionMatrix);
                BuildCpuScenePayload(scene, camera.Position, frustum);
                _materialManager.UploadMaterials(uploadCommandBuffer);

                uint tileCountX = Math.Max(1u, DivideRoundUp(screenWidth, TileSize));
                uint tileCountY = Math.Max(1u, DivideRoundUp(screenHeight, TileSize));
                uint totalTiles = checked(tileCountX * tileCountY);
                _lastTileCountX = tileCountX;
                _lastTileCountY = tileCountY;

                EnsureCapacity(ref _objectDataBuffer, CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer);
                EnsureCapacity(ref _instanceBuffers[frameIndex], CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer);
                EnsureCapacity(ref _meshletDrawBuffers[frameIndex], CheckedCount(_meshletDrawCommands.Count), MeshletDrawStride, uploadCommandBuffer);
                EnsureCapacity(ref _transparentMeshletDrawBuffers[frameIndex], CheckedCount(_transparentMeshletDrawCommands.Count), MeshletDrawStride, uploadCommandBuffer);
                if (useTiledLightCulling)
                {
                    EnsureCapacity(ref _tiledLightHeaderBuffer, totalTiles, TiledLightHeaderStride, uploadCommandBuffer);
                    EnsureCapacity(ref _tiledLightIndexBuffer, checked(totalTiles * MaxLightsPerTile), TiledLightIndexStride, uploadCommandBuffer);
                }

                UploadSpan(CollectionsMarshal.AsSpan(_objectData), _objectDataBuffer, uploadCommandBuffer);
                UploadSpan(CollectionsMarshal.AsSpan(_objectData), _instanceBuffers[frameIndex], uploadCommandBuffer);
                UploadSpan(CollectionsMarshal.AsSpan(_meshletDrawCommands), _meshletDrawBuffers[frameIndex], uploadCommandBuffer);
                UploadSpan(CollectionsMarshal.AsSpan(_transparentMeshletDrawCommands), _transparentMeshletDrawBuffers[frameIndex], uploadCommandBuffer);

                if (useTiledLightCulling)
                    ClearTiledLightBuffers(uploadCommandBuffer, totalTiles);

                RecordUploadReadBarriers(uploadCommandBuffer, frameIndex, useTiledLightCulling);
                UpdateRegisteredBindlessBuffers();

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
                    UploadedBytes = _lastUploadedBytes,
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

                sceneData.ObjectData.AddRange(_objectData);
                sceneData.MaterialData.AddRange(_materialManager.GetMaterialDataSnapshot());
                sceneData.MeshletDrawCommands.AddRange(_meshletDrawCommands);
                sceneData.OpaqueMeshletDrawCommands.AddRange(_meshletDrawCommands);
                sceneData.TransparentMeshletDrawCommands.AddRange(_transparentMeshletDrawCommands);

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
                if (!renderObject.Visible)
                    continue;

                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;

                MeshInfo meshInfo = GetValidatedMeshInfo(meshHandle);
                if (!IsVisible(renderObject, meshInfo, frustum))
                    continue;

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
                float transparentDistanceSquared = 0f;
                if (renderMode == MaterialRenderMode.Blend)
                {
                    Vector3 localCenter = (ToCoreVector(meshInfo.BoundingBoxMin) + ToCoreVector(meshInfo.BoundingBoxMax)) * 0.5f;
                    Vector3 worldCenter = TransformPoint(localCenter, renderObject.WorldMatrix);
                    transparentDistanceSquared = DistanceSquared(cameraPosition, worldCenter);
                }

                for (uint i = 0; i < meshInfo.MeshletCount; i++)
                {
                    var command = new GPUMeshletDrawCommand
                    {
                        MeshletIndex = meshInfo.MeshletOffset + i,
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

        private static bool IsVisible(RenderObject renderObject, MeshInfo meshInfo, Frustum frustum)
        {
            var localBounds = new BoundingBox(ToCoreVector(meshInfo.BoundingBoxMin), ToCoreVector(meshInfo.BoundingBoxMax));
            BoundingBox worldBounds = TransformBoundingBox(localBounds, renderObject.WorldMatrix);
            return IntersectsFrustum(worldBounds, frustum);
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

        private void EnsureCapacity(
            ref SceneBuffer buffer,
            uint requiredElementCount,
            ulong stride,
            CommandBuffer commandBuffer)
        {
            if (requiredElementCount <= buffer.ElementCapacity)
                return;

            WaitForOtherInFlightFrames();

            uint newCapacity = buffer.ElementCapacity;
            while (newCapacity < requiredElementCount)
                newCapacity = checked(newCapacity * 2);

            SceneBuffer oldBuffer = buffer;
            buffer = CreateSceneBuffer(newCapacity, stride);
            _bufferManager.DestroyBuffer(oldBuffer.Handle);
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

        private void UploadSpan<T>(ReadOnlySpan<T> data, SceneBuffer destination, CommandBuffer commandBuffer)
            where T : unmanaged
        {
            if (data.IsEmpty)
                return;

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

        public BufferHandle ObjectDataBuffer => _objectDataBuffer.Handle;
        public BufferHandle MaterialDataBuffer => _materialManager.MaterialBuffer;
        public BufferHandle TiledLightHeaderBuffer => _tiledLightHeaderBuffer.Handle;
        public BufferHandle TiledLightIndexBuffer => _tiledLightIndexBuffer.Handle;

        public ulong ObjectBufferSize => _objectDataBuffer.ByteSize;
        public ulong MaterialBufferSize => _materialManager.MaterialBufferSize;
        public ulong TiledLightHeaderBufferSize => _tiledLightHeaderBuffer.ByteSize;
        public ulong TiledLightIndexBufferSize => _tiledLightIndexBuffer.ByteSize;
        public ulong LastUploadedBytes => _lastUploadedBytes;
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
