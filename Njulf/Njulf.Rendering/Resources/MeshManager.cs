using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using CoreVector4 = Njulf.Core.Math.Vector4;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Meshlet
    {
        public Vector3 BoundingSphereCenter;
        public float BoundingSphereRadius;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint LocalVertexOffset;
        public uint LocalVertexCount;
        public uint LocalTriangleOffset;
        public uint LocalTriangleCount;
    }

    public struct MeshInfo
    {
        public Vector3 BoundingBoxMin;
        public Vector3 BoundingBoxMax;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint MeshMetadataOffset;
        public uint MeshletOffset;
        public uint MeshletCount;
        public uint MeshletLod1Offset;
        public uint MeshletLod1Count;
        public uint MeshletLod2Offset;
        public uint MeshletLod2Count;
        public uint MeshletLodGeneratedCount;
        public uint LocalVertexIndexOffset;
        public uint LocalVertexIndexCount;
        public uint LocalTriangleIndexOffset;
        public uint LocalTriangleIndexCount;
        public uint MeshletTriangleSum;
        public uint MeshletVertexSum;
        public uint SmallMeshletsUnder16Triangles;
        public uint SmallMeshletsUnder32Triangles;
    }

    public sealed unsafe class MeshManager : IDisposable
    {
        private const int MaxVerticesPerMeshlet = 64;
        private const int MaxTrianglesPerMeshlet = 126;
        private const int Lod0MaxVerticesPerMeshlet = 32;
        private const int Lod0MaxTrianglesPerMeshlet = 32;
        private const int Lod1MaxVerticesPerMeshlet = 48;
        private const int Lod1MaxTrianglesPerMeshlet = 64;
        private const int Lod2MaxVerticesPerMeshlet = MaxVerticesPerMeshlet;
        private const int Lod2MaxTrianglesPerMeshlet = MaxTrianglesPerMeshlet;
        private const int GreedyFallbackTriangleSearchWindow = 512;
        private const ulong InitialBufferSize = 16 * 1024 * 1024;
        private const ulong BufferGrowthFactor = 2;
        private const ulong UploadStagingAlignment = StagingRing.DefaultMinAlignment;

        private static readonly ulong VertexStride = (ulong)Marshal.SizeOf<GPUVertex>();
        private static readonly ulong IndexStride = sizeof(uint);
        private static readonly ulong MeshMetadataStride = (ulong)Marshal.SizeOf<GPUMeshInfo>();
        private static readonly ulong MeshletStride = (ulong)Marshal.SizeOf<Meshlet>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly StagingRing? _stagingRing;
        private readonly FenceBasedDeleter? _deleter;
        private readonly object _lock = new object();

        private BufferHandle _vertexBuffer;
        private BufferHandle _indexBuffer;
        private BufferHandle _meshMetadataBuffer;
        private BufferHandle _meshletBuffer;
        private BufferHandle _meshletVertexIndexBuffer;
        private BufferHandle _meshletTriangleIndexBuffer;

        private ulong _vertexBytesUsed;
        private ulong _indexBytesUsed;
        private ulong _meshMetadataBytesUsed;
        private ulong _meshletBytesUsed;
        private ulong _meshletVertexIndexBytesUsed;
        private ulong _meshletTriangleIndexBytesUsed;

        private readonly List<MeshInfo> _meshes = new List<MeshInfo>();
        private readonly List<Meshlet> _meshlets = new List<Meshlet>();
        private readonly List<uint> _meshGenerations = new List<uint>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private BindlessHeap? _registeredBindlessHeap;
        private bool _disposed;

        public MeshManager(VulkanContext context, BufferManager bufferManager)
            : this(context, bufferManager, stagingRing: null, deleter: null, allowMissingUploadServices: true)
        {
        }

        public MeshManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            FenceBasedDeleter deleter)
            : this(context, bufferManager, stagingRing, deleter, allowMissingUploadServices: false)
        {
        }

        private MeshManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing? stagingRing,
            FenceBasedDeleter? deleter,
            bool allowMissingUploadServices)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing;
            _deleter = deleter;

            CreateConsolidatedBuffers(InitialBufferSize);
            Console.WriteLine("Mesh manager created");
        }

        private void CreateConsolidatedBuffers(ulong size)
        {
            _vertexBuffer = CreateMeshBuffer(size, BufferUsageFlags.StorageBufferBit);
            _indexBuffer = CreateMeshBuffer(size, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndexBufferBit);
            _meshMetadataBuffer = CreateMeshBuffer(size, BufferUsageFlags.StorageBufferBit);
            _meshletBuffer = CreateMeshBuffer(size, BufferUsageFlags.StorageBufferBit);
            _meshletVertexIndexBuffer = CreateMeshBuffer(size, BufferUsageFlags.StorageBufferBit);
            _meshletTriangleIndexBuffer = CreateMeshBuffer(size, BufferUsageFlags.StorageBufferBit);
        }

        private BufferHandle CreateMeshBuffer(ulong size, BufferUsageFlags usage)
        {
            return _bufferManager.CreateDeviceBuffer(
                size,
                usage | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                true);
        }

        public MeshHandle RegisterMesh(
            Vector3[] vertices,
            uint[] indices,
            bool generateMeshlets = true)
        {
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            ValidateMeshInput(vertices, indices);
            GPUVertex[] gpuVertices = BuildGpuVertices(vertices, indices);
            return RegisterMeshInternal(gpuVertices, vertices, indices, generateMeshlets);
        }

        public MeshHandle RegisterMesh(
            GPUVertex[] vertices,
            uint[] indices,
            bool generateMeshlets = true)
        {
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            Vector3[] positions = ExtractPositions(vertices);
            ValidateMeshInput(positions, indices);
            return RegisterMeshInternal(vertices, positions, indices, generateMeshlets);
        }

        private MeshHandle RegisterMeshInternal(
            GPUVertex[] gpuVertices,
            Vector3[] positions,
            uint[] indices,
            bool generateMeshlets)
        {
            lock (_lock)
            {
                int meshIndex = _freeIndices.Count > 0 ? _freeIndices.Pop() : _meshes.Count;
                uint generation = AllocateGeneration(meshIndex);

                var meshInfo = CreateMeshInfo(meshIndex, positions, indices);
                List<Meshlet> meshlets = new List<Meshlet>();
                List<uint> localVertexIndices = new List<uint>();
                List<uint> localTriangleIndices = new List<uint>();

                if (generateMeshlets)
                {
                    BuildMeshletLods(
                        ref meshInfo,
                        positions,
                        indices,
                        meshlets,
                        localVertexIndices,
                        localTriangleIndices);
                    ApplyMeshletQualityStats(ref meshInfo, meshlets);
                    ApplyGlobalMeshletOffsets(meshlets, meshInfo);
                    ValidateMeshletRanges(ref meshInfo, meshlets, localVertexIndices, localTriangleIndices);
                }

                var meshMetadata = CreateGpuMeshInfo(meshInfo);
                var retiredBuffers = new List<BufferHandle>();
                ulong uploadStagingBytes = CalculateUploadStagingBytes(
                    CheckedByteSize(gpuVertices.Length, VertexStride),
                    CheckedByteSize(indices.Length, IndexStride),
                    MeshMetadataStride,
                    CheckedByteSize(meshlets.Count, MeshletStride),
                    CheckedByteSize(localVertexIndices.Count, IndexStride),
                    CheckedByteSize(localTriangleIndices.Count, IndexStride));
                UploadCommandContext upload = BeginUploadCommands(uploadStagingBytes);

                try
                {
                    EnsureBufferCapacity(
                        ref _vertexBuffer,
                        _vertexBytesUsed,
                        _vertexBytesUsed + CheckedByteSize(gpuVertices.Length, VertexStride),
                        BufferUsageFlags.StorageBufferBit,
                        upload.CommandBuffer,
                        retiredBuffers);

                    EnsureBufferCapacity(
                        ref _indexBuffer,
                        _indexBytesUsed,
                        _indexBytesUsed + CheckedByteSize(indices.Length, IndexStride),
                        BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndexBufferBit,
                        upload.CommandBuffer,
                        retiredBuffers);

                    EnsureBufferCapacity(
                        ref _meshMetadataBuffer,
                        _meshMetadataBytesUsed,
                        ((ulong)meshIndex + 1) * MeshMetadataStride,
                        BufferUsageFlags.StorageBufferBit,
                        upload.CommandBuffer,
                        retiredBuffers);

                    EnsureBufferCapacity(
                        ref _meshletBuffer,
                        _meshletBytesUsed,
                        _meshletBytesUsed + CheckedByteSize(meshlets.Count, MeshletStride),
                        BufferUsageFlags.StorageBufferBit,
                        upload.CommandBuffer,
                        retiredBuffers);

                    EnsureBufferCapacity(
                        ref _meshletVertexIndexBuffer,
                        _meshletVertexIndexBytesUsed,
                        _meshletVertexIndexBytesUsed + CheckedByteSize(localVertexIndices.Count, IndexStride),
                        BufferUsageFlags.StorageBufferBit,
                        upload.CommandBuffer,
                        retiredBuffers);

                    EnsureBufferCapacity(
                        ref _meshletTriangleIndexBuffer,
                        _meshletTriangleIndexBytesUsed,
                        _meshletTriangleIndexBytesUsed + CheckedByteSize(localTriangleIndices.Count, IndexStride),
                        BufferUsageFlags.StorageBufferBit,
                        upload.CommandBuffer,
                        retiredBuffers);

                    UploadSpan(gpuVertices, _vertexBuffer, _vertexBytesUsed, upload);
                    UploadSpan(indices, _indexBuffer, _indexBytesUsed, upload);
                    Span<GPUMeshInfo> meshMetadataSpan = stackalloc GPUMeshInfo[1];
                    meshMetadataSpan[0] = meshMetadata;
                    UploadSpan(meshMetadataSpan, _meshMetadataBuffer, meshInfo.MeshMetadataOffset * MeshMetadataStride, upload);

                    if (meshlets.Count > 0)
                        UploadSpan(CollectionsMarshal.AsSpan(meshlets), _meshletBuffer, _meshletBytesUsed, upload);
                    if (localVertexIndices.Count > 0)
                        UploadSpan(CollectionsMarshal.AsSpan(localVertexIndices), _meshletVertexIndexBuffer, _meshletVertexIndexBytesUsed, upload);
                    if (localTriangleIndices.Count > 0)
                        UploadSpan(CollectionsMarshal.AsSpan(localTriangleIndices), _meshletTriangleIndexBuffer, _meshletTriangleIndexBytesUsed, upload);

                    RecordUploadShaderReadBarriers(upload.CommandBuffer);
                    Fence uploadFence = EndUploadCommands(upload);

                    _vertexBytesUsed += CheckedByteSize(gpuVertices.Length, VertexStride);
                    _indexBytesUsed += CheckedByteSize(indices.Length, IndexStride);
                    _meshMetadataBytesUsed = Math.Max(_meshMetadataBytesUsed, ((ulong)meshIndex + 1) * MeshMetadataStride);
                    _meshletBytesUsed += CheckedByteSize(meshlets.Count, MeshletStride);
                    _meshletVertexIndexBytesUsed += CheckedByteSize(localVertexIndices.Count, IndexStride);
                    _meshletTriangleIndexBytesUsed += CheckedByteSize(localTriangleIndices.Count, IndexStride);

                    AppendCpuMeshlets(meshInfo, meshlets);
                    StoreMeshInfo(meshIndex, generation, meshInfo);
                    UpdateRegisteredBindlessBuffers();
                    RetireReplacedBuffers(retiredBuffers, uploadFence);
                    DestroyUploadFence(uploadFence);

                    return new MeshHandle(meshIndex, generation);
                }
                catch
                {
                    CleanupUploadCommands(upload);
                    foreach (var retired in retiredBuffers)
                    {
                        if (retired.IsValid)
                            _bufferManager.DestroyBuffer(retired);
                    }

                    throw;
                }
            }
        }

        private MeshInfo CreateMeshInfo(int meshIndex, Vector3[] vertices, uint[] indices)
        {
            if (_vertexBytesUsed % VertexStride != 0 ||
                _indexBytesUsed % IndexStride != 0 ||
                _meshletBytesUsed % MeshletStride != 0 ||
                _meshletVertexIndexBytesUsed % IndexStride != 0 ||
                _meshletTriangleIndexBytesUsed % IndexStride != 0)
            {
                throw new InvalidOperationException("Mesh buffer append offsets are not aligned to their element strides.");
            }

            var meshInfo = new MeshInfo
            {
                VertexOffset = CheckedElementOffset(_vertexBytesUsed, VertexStride),
                VertexCount = CheckedCount(vertices.Length),
                IndexOffset = CheckedElementOffset(_indexBytesUsed, IndexStride),
                IndexCount = CheckedCount(indices.Length),
                MeshMetadataOffset = CheckedCount(meshIndex),
                MeshletOffset = CheckedElementOffset(_meshletBytesUsed, MeshletStride),
                LocalVertexIndexOffset = CheckedElementOffset(_meshletVertexIndexBytesUsed, IndexStride),
                LocalTriangleIndexOffset = CheckedElementOffset(_meshletTriangleIndexBytesUsed, IndexStride)
            };

            meshInfo.BoundingBoxMin = vertices[0];
            meshInfo.BoundingBoxMax = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                meshInfo.BoundingBoxMin = Vector3.Min(meshInfo.BoundingBoxMin, vertices[i]);
                meshInfo.BoundingBoxMax = Vector3.Max(meshInfo.BoundingBoxMax, vertices[i]);
            }

            return meshInfo;
        }

        private static GPUMeshInfo CreateGpuMeshInfo(MeshInfo meshInfo)
        {
            Vector3 center = (meshInfo.BoundingBoxMin + meshInfo.BoundingBoxMax) * 0.5f;
            float radius = Vector3.Distance(center, meshInfo.BoundingBoxMin);

            return new GPUMeshInfo
            {
                BoundingSphere = new CoreVector4(center.X, center.Y, center.Z, radius),
                Padding0 = CoreVector4.Zero
            };
        }

        private static GPUVertex[] BuildGpuVertices(Vector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions.Length];

            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i + 0];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];

                Vector3 edge0 = positions[i1] - positions[i0];
                Vector3 edge1 = positions[i2] - positions[i0];
                Vector3 faceNormal = Vector3.Cross(edge0, edge1);
                if (faceNormal.LengthSquared() > 0f)
                    faceNormal = Vector3.Normalize(faceNormal);

                normals[i0] += faceNormal;
                normals[i1] += faceNormal;
                normals[i2] += faceNormal;
            }

            var vertices = new GPUVertex[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 normal = normals[i].LengthSquared() > 0f
                    ? Vector3.Normalize(normals[i])
                    : Vector3.UnitZ;

                vertices[i] = new GPUVertex
                {
                    Position = ToCoreVector(positions[i]),
                    Padding0 = 0f,
                    Normal = ToCoreVector(normal),
                    Padding1 = 0f,
                    TexCoord = Njulf.Core.Math.Vector2.Zero,
                    TexCoord2 = Njulf.Core.Math.Vector2.Zero,
                    Tangent = new CoreVector4(1f, 0f, 0f, 1f)
                };
            }

            return vertices;
        }

        private static Njulf.Core.Math.Vector3 ToCoreVector(Vector3 value)
        {
            return new Njulf.Core.Math.Vector3(value.X, value.Y, value.Z);
        }

        private static Vector3 FromCoreVector(Njulf.Core.Math.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static Vector3[] ExtractPositions(GPUVertex[] vertices)
        {
            var positions = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                positions[i] = FromCoreVector(vertices[i].Position);

            return positions;
        }

        private static void ApplyGlobalMeshletOffsets(List<Meshlet> meshlets, MeshInfo meshInfo)
        {
            for (int i = 0; i < meshlets.Count; i++)
            {
                Meshlet meshlet = meshlets[i];
                meshlet.VertexOffset = CheckedAdd(meshInfo.VertexOffset, meshlet.VertexOffset);
                meshlet.IndexOffset = CheckedAdd(meshInfo.IndexOffset, meshlet.IndexOffset);
                meshlet.LocalVertexOffset = CheckedAdd(meshInfo.LocalVertexIndexOffset, meshlet.LocalVertexOffset);
                meshlet.LocalTriangleOffset = CheckedAdd(meshInfo.LocalTriangleIndexOffset, meshlet.LocalTriangleOffset);
                meshlets[i] = meshlet;
            }
        }

        private void ValidateMeshletRanges(
            ref MeshInfo meshInfo,
            List<Meshlet> meshlets,
            List<uint> localVertexIndices,
            List<uint> localTriangleIndices)
        {
            meshInfo.LocalVertexIndexCount = CheckedCount(localVertexIndices.Count);
            meshInfo.LocalTriangleIndexCount = CheckedCount(localTriangleIndices.Count);

            uint vertexEnd = CheckedAdd(meshInfo.VertexOffset, meshInfo.VertexCount);
            uint indexEnd = CheckedAdd(meshInfo.IndexOffset, meshInfo.IndexCount);
            uint localVertexEnd = CheckedAdd(meshInfo.LocalVertexIndexOffset, meshInfo.LocalVertexIndexCount);
            uint localTriangleEnd = CheckedAdd(meshInfo.LocalTriangleIndexOffset, meshInfo.LocalTriangleIndexCount);

            foreach (Meshlet meshlet in meshlets)
            {
                if (meshlet.VertexOffset < meshInfo.VertexOffset ||
                    CheckedAdd(meshlet.VertexOffset, meshlet.VertexCount) > vertexEnd)
                {
                    throw new InvalidOperationException("Generated meshlet vertex range is outside its mesh vertex range.");
                }

                if (meshlet.IndexOffset < meshInfo.IndexOffset ||
                    CheckedAdd(meshlet.IndexOffset, meshlet.IndexCount) > indexEnd)
                {
                    throw new InvalidOperationException("Generated meshlet index range is outside its mesh index range.");
                }

                if (meshlet.LocalVertexOffset < meshInfo.LocalVertexIndexOffset ||
                    CheckedAdd(meshlet.LocalVertexOffset, meshlet.LocalVertexCount) > localVertexEnd)
                {
                    throw new InvalidOperationException("Generated meshlet local vertex range is outside the local vertex index buffer.");
                }

                uint localTriangleScalarCount = meshlet.LocalTriangleCount * 3;
                if (meshlet.LocalTriangleOffset < meshInfo.LocalTriangleIndexOffset ||
                    CheckedAdd(meshlet.LocalTriangleOffset, localTriangleScalarCount) > localTriangleEnd)
                {
                    throw new InvalidOperationException("Generated meshlet local triangle range is outside the local triangle index buffer.");
                }
            }

            for (int i = 0; i < localVertexIndices.Count; i++)
            {
                if (localVertexIndices[i] >= meshInfo.VertexCount)
                    throw new InvalidOperationException($"Meshlet local vertex index {localVertexIndices[i]} is outside mesh vertex count {meshInfo.VertexCount}.");
            }

            for (int i = 0; i < localTriangleIndices.Count; i++)
            {
                if (localTriangleIndices[i] >= MaxVerticesPerMeshlet)
                    throw new InvalidOperationException($"Meshlet local triangle vertex index {localTriangleIndices[i]} exceeds meshlet vertex limit {MaxVerticesPerMeshlet}.");
            }
        }

        private void EnsureBufferCapacity(
            ref BufferHandle buffer,
            ulong usedBytes,
            ulong requiredBytes,
            BufferUsageFlags usage,
            CommandBuffer commandBuffer,
            List<BufferHandle> retiredBuffers)
        {
            ulong currentSize = _bufferManager.GetBufferSize(buffer);
            if (requiredBytes <= currentSize)
                return;

            ulong newSize = currentSize;
            do
            {
                newSize = checked(newSize * BufferGrowthFactor);
            }
            while (newSize < requiredBytes);

            BufferHandle oldBuffer = buffer;
            BufferHandle newBuffer = CreateMeshBuffer(newSize, usage);

            if (usedBytes > 0)
            {
                var copy = new BufferCopy
                {
                    SrcOffset = 0,
                    DstOffset = 0,
                    Size = usedBytes
                };

                _context.Api.CmdCopyBuffer(
                    commandBuffer,
                    _bufferManager.GetBuffer(oldBuffer),
                    _bufferManager.GetBuffer(newBuffer),
                    1,
                    &copy);
            }

            buffer = newBuffer;
            retiredBuffers.Add(oldBuffer);
        }

        private void UploadSpan<T>(
            ReadOnlySpan<T> data,
            BufferHandle destination,
            ulong destinationOffset,
            UploadCommandContext upload)
            where T : unmanaged
        {
            if (data.IsEmpty)
                return;

            ulong dataSize = checked((ulong)data.Length * (ulong)sizeof(T));
            (BufferHandle stagingHandle, ulong stagingOffset) = AllocateUploadStaging(upload, dataSize);

            void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
            fixed (T* source = data)
            {
                global::System.Buffer.MemoryCopy(
                    source,
                    (byte*)mappedData + stagingOffset,
                    dataSize,
                    dataSize);
            }

            _bufferManager.FlushBuffer(stagingHandle, stagingOffset, dataSize);

            var copy = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = destinationOffset,
                Size = dataSize
            };

            _context.Api.CmdCopyBuffer(
                upload.CommandBuffer,
                _bufferManager.GetBuffer(stagingHandle),
                _bufferManager.GetBuffer(destination),
                1,
                &copy);
        }

        private (BufferHandle Buffer, ulong Offset) AllocateUploadStaging(UploadCommandContext upload, ulong size)
        {
            if (!upload.StagingBuffer.IsValid)
                throw new InvalidOperationException("Mesh upload staging buffer has not been created.");

            ulong offset = AlignUp(upload.StagingOffset, UploadStagingAlignment);
            if (offset + size > upload.StagingBufferSize)
            {
                throw new InvalidOperationException(
                    $"Mesh upload staging overflow: trying to allocate {size} bytes at offset {offset}, " +
                    $"buffer size is {upload.StagingBufferSize}.");
            }

            upload.StagingOffset = offset + size;
            return (upload.StagingBuffer, offset);
        }

        private void RecordUploadShaderReadBarriers(CommandBuffer commandBuffer)
        {
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[6];

            barriers[0] = CreateUploadReadBarrier(_vertexBuffer);
            barriers[1] = CreateUploadReadBarrier(_indexBuffer);
            barriers[2] = CreateUploadReadBarrier(_meshMetadataBuffer);
            barriers[3] = CreateUploadReadBarrier(_meshletBuffer);
            barriers[4] = CreateUploadReadBarrier(_meshletVertexIndexBuffer);
            barriers[5] = CreateUploadReadBarrier(_meshletTriangleIndexBuffer);

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 6,
                PBufferMemoryBarriers = barriers
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private BufferMemoryBarrier2 CreateUploadReadBarrier(BufferHandle handle)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.AllCommandsBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(handle),
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        private UploadCommandContext BeginUploadCommands(ulong stagingBytes)
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _context.GraphicsQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.TransientBit
            };

            Result result = _context.Api.CreateCommandPool(
                _context.Device,
                &poolInfo,
                null,
                out CommandPool commandPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh upload command pool", result);

            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            result = _context.Api.AllocateCommandBuffers(
                _context.Device,
                &allocInfo,
                out CommandBuffer commandBuffer);
            if (result != Result.Success)
            {
                _context.Api.DestroyCommandPool(_context.Device, commandPool, null);
                throw new VulkanException("Failed to allocate mesh upload command buffer", result);
            }

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };

            result = _context.Api.BeginCommandBuffer(commandBuffer, &beginInfo);
            if (result != Result.Success)
            {
                _context.Api.FreeCommandBuffers(_context.Device, commandPool, 1, &commandBuffer);
                _context.Api.DestroyCommandPool(_context.Device, commandPool, null);
                throw new VulkanException("Failed to begin mesh upload command buffer", result);
            }

            try
            {
                BufferHandle stagingBuffer = _bufferManager.CreateStagingBuffer(stagingBytes);
                return new UploadCommandContext(commandPool, commandBuffer, stagingBuffer, stagingBytes);
            }
            catch
            {
                _context.Api.FreeCommandBuffers(_context.Device, commandPool, 1, &commandBuffer);
                _context.Api.DestroyCommandPool(_context.Device, commandPool, null);
                throw;
            }
        }

        private Fence EndUploadCommands(UploadCommandContext upload)
        {
            Result result = _context.Api.EndCommandBuffer(upload.CommandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to end mesh upload command buffer", result);

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo
            };

            result = _context.Api.CreateFence(_context.Device, &fenceInfo, null, out Fence fence);
            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh upload fence", result);

            var commandBuffer = upload.CommandBuffer;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer
            };

            result = _context.Api.QueueSubmit(_context.GraphicsQueue, 1, &submitInfo, fence);
            if (result != Result.Success)
            {
                _context.Api.DestroyFence(_context.Device, fence, null);
                throw new VulkanException("Failed to submit mesh upload commands", result);
            }

            result = _context.Api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue);
            if (result != Result.Success)
            {
                _context.Api.DestroyFence(_context.Device, fence, null);
                throw new VulkanException("Failed to wait for mesh upload fence", result);
            }

            CommandBuffer commandBufferToFree = upload.CommandBuffer;
            _context.Api.FreeCommandBuffers(_context.Device, upload.CommandPool, 1, &commandBufferToFree);
            _context.Api.DestroyCommandPool(_context.Device, upload.CommandPool, null);
            DestroyUploadStaging(upload);
            upload.MarkCompleted();
            return fence;
        }

        private void CleanupUploadCommands(UploadCommandContext upload)
        {
            if (upload.Completed)
                return;

            if (upload.CommandBuffer.Handle != 0)
            {
                CommandBuffer commandBufferToFree = upload.CommandBuffer;
                _context.Api.FreeCommandBuffers(_context.Device, upload.CommandPool, 1, &commandBufferToFree);
            }
            if (upload.CommandPool.Handle != 0)
                _context.Api.DestroyCommandPool(_context.Device, upload.CommandPool, null);
            DestroyUploadStaging(upload);
            upload.MarkCompleted();
        }

        private void DestroyUploadStaging(UploadCommandContext upload)
        {
            if (upload.StagingBuffer.IsValid)
            {
                _bufferManager.DestroyBuffer(upload.StagingBuffer);
                upload.StagingBuffer = default;
                upload.StagingBufferSize = 0;
                upload.StagingOffset = 0;
            }
        }

        private void RetireReplacedBuffers(IReadOnlyList<BufferHandle> retiredBuffers, Fence uploadFence)
        {
            if (retiredBuffers.Count == 0)
                return;

            if (_deleter == null)
            {
                foreach (BufferHandle buffer in retiredBuffers)
                    _bufferManager.DestroyBuffer(buffer);
                return;
            }

            foreach (BufferHandle buffer in retiredBuffers)
                _deleter.QueueBufferDeletion(uploadFence, buffer, _bufferManager);

            _deleter.ProcessCompletedFrame(uploadFence);
        }

        private void DestroyUploadFence(Fence uploadFence)
        {
            if (uploadFence.Handle != 0)
                _context.Api.DestroyFence(_context.Device, uploadFence, null);
        }

        private void UpdateRegisteredBindlessBuffers()
        {
            if (_registeredBindlessHeap != null)
                RegisterBuffers(_registeredBindlessHeap);
        }

        private uint AllocateGeneration(int meshIndex)
        {
            if (meshIndex == _meshGenerations.Count)
            {
                _meshGenerations.Add(1);
                return 1;
            }

            uint generation = _meshGenerations[meshIndex] + 1;
            if (generation == 0)
                generation = 1;
            _meshGenerations[meshIndex] = generation;
            return generation;
        }

        private void StoreMeshInfo(int meshIndex, uint generation, MeshInfo meshInfo)
        {
            if (meshIndex == _meshes.Count)
                _meshes.Add(meshInfo);
            else
                _meshes[meshIndex] = meshInfo;

            _meshGenerations[meshIndex] = generation;
        }

        private void AppendCpuMeshlets(MeshInfo meshInfo, IReadOnlyList<Meshlet> meshlets)
        {
            ulong requiredCount = (ulong)meshInfo.MeshletOffset + meshInfo.MeshletLodGeneratedCount;
            if (requiredCount > int.MaxValue)
                throw new InvalidOperationException("CPU meshlet cache exceeded supported element count.");

            while (_meshlets.Count < (int)requiredCount)
                _meshlets.Add(default);

            for (int i = 0; i < meshlets.Count; i++)
                _meshlets[(int)meshInfo.MeshletOffset + i] = meshlets[i];
        }

        private static void ValidateMeshInput(Vector3[] vertices, uint[] indices)
        {
            if (vertices.Length == 0)
                throw new ArgumentException("A mesh must contain at least one vertex.", nameof(vertices));
            if (indices.Length == 0)
                throw new ArgumentException("A mesh must contain at least one index.", nameof(indices));
            if (indices.Length % 3 != 0)
                throw new ArgumentException("Mesh index count must be divisible by 3.", nameof(indices));

            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= vertices.Length)
                    throw new ArgumentOutOfRangeException(nameof(indices), $"Index {i} references vertex {indices[i]}, but vertex count is {vertices.Length}.");
            }
        }

        private void BuildMeshletLods(
            ref MeshInfo meshInfo,
            Vector3[] vertices,
            uint[] indices,
            List<Meshlet> meshlets,
            List<uint> localVertexIndices,
            List<uint> localTriangleIndices)
        {
            uint baseMeshletOffset = meshInfo.MeshletOffset;

            meshInfo.MeshletOffset = baseMeshletOffset;
            int lod0Start = meshlets.Count;
            GenerateMeshlets(
                vertices,
                indices,
                Lod0MaxVerticesPerMeshlet,
                Lod0MaxTrianglesPerMeshlet,
                meshlets,
                localVertexIndices,
                localTriangleIndices);
            meshInfo.MeshletCount = CheckedCount(meshlets.Count - lod0Start);

            meshInfo.MeshletLod1Offset = CheckedAdd(baseMeshletOffset, meshInfo.MeshletCount);
            int lod1Start = meshlets.Count;
            GenerateMeshlets(
                vertices,
                indices,
                Lod1MaxVerticesPerMeshlet,
                Lod1MaxTrianglesPerMeshlet,
                meshlets,
                localVertexIndices,
                localTriangleIndices);
            meshInfo.MeshletLod1Count = CheckedCount(meshlets.Count - lod1Start);

            meshInfo.MeshletLod2Offset = CheckedAdd(meshInfo.MeshletLod1Offset, meshInfo.MeshletLod1Count);
            int lod2Start = meshlets.Count;
            GenerateMeshlets(
                vertices,
                indices,
                Lod2MaxVerticesPerMeshlet,
                Lod2MaxTrianglesPerMeshlet,
                meshlets,
                localVertexIndices,
                localTriangleIndices);
            meshInfo.MeshletLod2Count = CheckedCount(meshlets.Count - lod2Start);
            meshInfo.MeshletLodGeneratedCount = CheckedCount(meshlets.Count);
        }

        private void GenerateMeshlets(
            Vector3[] vertices,
            uint[] indices,
            int maxVerticesPerMeshlet,
            int maxTrianglesPerMeshlet,
            List<Meshlet> meshlets,
            List<uint> localVertexIndices,
            List<uint> localTriangleIndices)
        {
            int triangleCount = indices.Length / 3;
            List<int>[] vertexToTriangles = BuildVertexTriangleAdjacency(indices, vertices.Length);
            bool[] assignedTriangles = new bool[triangleCount];
            int assignedTriangleCount = 0;
            int nextSeedTriangle = 0;

            while (assignedTriangleCount < triangleCount)
            {
                while (nextSeedTriangle < triangleCount && assignedTriangles[nextSeedTriangle])
                    nextSeedTriangle++;

                if (nextSeedTriangle >= triangleCount)
                    break;

                var localVertexMap = new Dictionary<int, int>(maxVerticesPerMeshlet);
                var meshletVertexIndices = new List<int>(maxVerticesPerMeshlet);
                var meshletTriangles = new List<int>(maxTrianglesPerMeshlet);
                var candidateTriangles = new HashSet<int>();
                int minTriangle = nextSeedTriangle;
                int maxTriangle = nextSeedTriangle;

                AddTriangleToMeshlet(
                    nextSeedTriangle,
                    indices,
                    vertexToTriangles,
                    assignedTriangles,
                    localVertexMap,
                    meshletVertexIndices,
                    meshletTriangles,
                    candidateTriangles,
                    maxVerticesPerMeshlet,
                    ref assignedTriangleCount,
                    ref minTriangle,
                    ref maxTriangle);

                while (meshletTriangles.Count < maxTrianglesPerMeshlet)
                {
                    int bestCandidate = SelectBestMeshletCandidate(
                        candidateTriangles,
                        assignedTriangles,
                        indices,
                        localVertexMap,
                        maxVerticesPerMeshlet);

                    if (bestCandidate < 0)
                    {
                        bestCandidate = SelectBestSequentialFallbackCandidate(
                            nextSeedTriangle,
                            assignedTriangles,
                            indices,
                            localVertexMap,
                            maxVerticesPerMeshlet);
                        if (bestCandidate < 0)
                            break;
                    }

                    candidateTriangles.Remove(bestCandidate);
                    AddTriangleToMeshlet(
                        bestCandidate,
                        indices,
                        vertexToTriangles,
                        assignedTriangles,
                        localVertexMap,
                        meshletVertexIndices,
                        meshletTriangles,
                        candidateTriangles,
                        maxVerticesPerMeshlet,
                        ref assignedTriangleCount,
                        ref minTriangle,
                        ref maxTriangle);
                }

                BoundingSphere boundingSphere = CalculateBoundingSphere(vertices, meshletVertexIndices);
                int localVertexStart = localVertexIndices.Count;
                foreach (int vertexIndex in meshletVertexIndices)
                    localVertexIndices.Add((uint)vertexIndex);

                int localTriangleStart = localTriangleIndices.Count;
                foreach (int triangleIndex in meshletTriangles)
                {
                    int i0 = (int)indices[triangleIndex * 3 + 0];
                    int i1 = (int)indices[triangleIndex * 3 + 1];
                    int i2 = (int)indices[triangleIndex * 3 + 2];

                    localTriangleIndices.Add((uint)localVertexMap[i0]);
                    localTriangleIndices.Add((uint)localVertexMap[i1]);
                    localTriangleIndices.Add((uint)localVertexMap[i2]);
                }

                meshlets.Add(new Meshlet
                {
                    BoundingSphereCenter = boundingSphere.Center,
                    BoundingSphereRadius = boundingSphere.Radius,
                    VertexOffset = 0,
                    VertexCount = (uint)meshletVertexIndices.Count,
                    IndexOffset = (uint)(minTriangle * 3),
                    IndexCount = (uint)((maxTriangle - minTriangle + 1) * 3),
                    LocalVertexOffset = (uint)localVertexStart,
                    LocalVertexCount = (uint)(localVertexIndices.Count - localVertexStart),
                    LocalTriangleOffset = (uint)localTriangleStart,
                    LocalTriangleCount = (uint)((localTriangleIndices.Count - localTriangleStart) / 3)
                });
            }
        }

        private static List<int>[] BuildVertexTriangleAdjacency(uint[] indices, int vertexCount)
        {
            var vertexToTriangles = new List<int>[vertexCount];
            int triangleCount = indices.Length / 3;

            for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
            {
                int i0 = (int)indices[triangleIndex * 3 + 0];
                int i1 = (int)indices[triangleIndex * 3 + 1];
                int i2 = (int)indices[triangleIndex * 3 + 2];

                AddTriangleReference(vertexToTriangles, i0, triangleIndex);
                if (i1 != i0)
                    AddTriangleReference(vertexToTriangles, i1, triangleIndex);
                if (i2 != i0 && i2 != i1)
                    AddTriangleReference(vertexToTriangles, i2, triangleIndex);
            }

            return vertexToTriangles;
        }

        private static void AddTriangleReference(List<int>[] vertexToTriangles, int vertexIndex, int triangleIndex)
        {
            List<int>? triangles = vertexToTriangles[vertexIndex];
            if (triangles == null)
            {
                triangles = new List<int>();
                vertexToTriangles[vertexIndex] = triangles;
            }

            triangles.Add(triangleIndex);
        }

        private static void AddTriangleToMeshlet(
            int triangleIndex,
            uint[] indices,
            List<int>[] vertexToTriangles,
            bool[] assignedTriangles,
            Dictionary<int, int> localVertexMap,
            List<int> meshletVertexIndices,
            List<int> meshletTriangles,
            HashSet<int> candidateTriangles,
            int maxVerticesPerMeshlet,
            ref int assignedTriangleCount,
            ref int minTriangle,
            ref int maxTriangle)
        {
            if (assignedTriangles[triangleIndex])
                return;

            int i0 = (int)indices[triangleIndex * 3 + 0];
            int i1 = (int)indices[triangleIndex * 3 + 1];
            int i2 = (int)indices[triangleIndex * 3 + 2];

            AddLocalVertex(i0, localVertexMap, meshletVertexIndices, maxVerticesPerMeshlet);
            AddLocalVertex(i1, localVertexMap, meshletVertexIndices, maxVerticesPerMeshlet);
            AddLocalVertex(i2, localVertexMap, meshletVertexIndices, maxVerticesPerMeshlet);

            assignedTriangles[triangleIndex] = true;
            assignedTriangleCount++;
            meshletTriangles.Add(triangleIndex);
            minTriangle = Math.Min(minTriangle, triangleIndex);
            maxTriangle = Math.Max(maxTriangle, triangleIndex);

            AddCandidateTriangles(i0, vertexToTriangles, assignedTriangles, candidateTriangles);
            AddCandidateTriangles(i1, vertexToTriangles, assignedTriangles, candidateTriangles);
            AddCandidateTriangles(i2, vertexToTriangles, assignedTriangles, candidateTriangles);
            candidateTriangles.Remove(triangleIndex);
        }

        private static void AddLocalVertex(
            int vertexIndex,
            Dictionary<int, int> localVertexMap,
            List<int> meshletVertexIndices,
            int maxVerticesPerMeshlet)
        {
            if (localVertexMap.ContainsKey(vertexIndex))
                return;

            if (localVertexMap.Count >= maxVerticesPerMeshlet)
                throw new InvalidOperationException("Generated meshlet exceeded the local vertex limit.");

            localVertexMap.Add(vertexIndex, localVertexMap.Count);
            meshletVertexIndices.Add(vertexIndex);
        }

        private static void AddCandidateTriangles(
            int vertexIndex,
            List<int>[] vertexToTriangles,
            bool[] assignedTriangles,
            HashSet<int> candidateTriangles)
        {
            List<int>? adjacentTriangles = vertexToTriangles[vertexIndex];
            if (adjacentTriangles == null)
                return;

            foreach (int adjacentTriangle in adjacentTriangles)
            {
                if (!assignedTriangles[adjacentTriangle])
                    candidateTriangles.Add(adjacentTriangle);
            }
        }

        private static int SelectBestMeshletCandidate(
            HashSet<int> candidateTriangles,
            bool[] assignedTriangles,
            uint[] indices,
            Dictionary<int, int> localVertexMap,
            int maxVerticesPerMeshlet)
        {
            int bestCandidate = -1;
            int bestScore = int.MinValue;
            Span<int> staleCandidates = stackalloc int[Math.Min(candidateTriangles.Count, 64)];
            int staleCount = 0;

            foreach (int candidate in candidateTriangles)
            {
                if (assignedTriangles[candidate])
                {
                    if (staleCount < staleCandidates.Length)
                        staleCandidates[staleCount++] = candidate;
                    continue;
                }

                int newVertexCount = CountNewTriangleVertices(candidate, indices, localVertexMap);
                if (localVertexMap.Count + newVertexCount > maxVerticesPerMeshlet)
                    continue;

                int sharedVertexCount = 3 - newVertexCount;
                if (sharedVertexCount <= 0)
                    continue;

                int score = sharedVertexCount * 1000 - newVertexCount * 10;
                if (score > bestScore || (score == bestScore && candidate < bestCandidate))
                {
                    bestScore = score;
                    bestCandidate = candidate;
                }
            }

            for (int i = 0; i < staleCount; i++)
                candidateTriangles.Remove(staleCandidates[i]);

            return bestCandidate;
        }

        private static int SelectBestSequentialFallbackCandidate(
            int seedTriangle,
            bool[] assignedTriangles,
            uint[] indices,
            Dictionary<int, int> localVertexMap,
            int maxVerticesPerMeshlet)
        {
            int triangleCount = assignedTriangles.Length;
            int searchEnd = Math.Min(triangleCount, seedTriangle + GreedyFallbackTriangleSearchWindow);
            int bestCandidate = -1;
            int bestScore = int.MinValue;

            for (int triangleIndex = seedTriangle + 1; triangleIndex < searchEnd; triangleIndex++)
            {
                if (assignedTriangles[triangleIndex])
                    continue;

                int newVertexCount = CountNewTriangleVertices(triangleIndex, indices, localVertexMap);
                if (localVertexMap.Count + newVertexCount > maxVerticesPerMeshlet)
                    continue;

                int sharedVertexCount = 3 - newVertexCount;
                int score = sharedVertexCount * 1000 - newVertexCount * 100 - (triangleIndex - seedTriangle);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = triangleIndex;
                }
            }

            return bestCandidate;
        }

        private static int CountNewTriangleVertices(
            int triangleIndex,
            uint[] indices,
            Dictionary<int, int> localVertexMap)
        {
            int count = 0;
            int i0 = (int)indices[triangleIndex * 3 + 0];
            int i1 = (int)indices[triangleIndex * 3 + 1];
            int i2 = (int)indices[triangleIndex * 3 + 2];

            if (!localVertexMap.ContainsKey(i0))
                count++;
            if (i1 != i0 && !localVertexMap.ContainsKey(i1))
                count++;
            if (i2 != i0 && i2 != i1 && !localVertexMap.ContainsKey(i2))
                count++;

            return count;
        }

        private struct BoundingSphere
        {
            public Vector3 Center;
            public float Radius;
        }

        private static BoundingSphere CalculateBoundingSphere(Vector3[] vertices, IReadOnlyList<int> vertexIndices)
        {
            if (vertexIndices.Count == 0)
                return new BoundingSphere { Center = Vector3.Zero, Radius = 0 };

            using var enumerator = vertexIndices.GetEnumerator();
            enumerator.MoveNext();
            Vector3 min = vertices[enumerator.Current];
            Vector3 max = min;

            foreach (int idx in vertexIndices)
            {
                Vector3 v = vertices[idx];
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            Vector3 center = (min + max) * 0.5f;
            float radius = 0;

            foreach (int idx in vertexIndices)
            {
                float dist = Vector3.Distance(center, vertices[idx]);
                if (dist > radius)
                    radius = dist;
            }

            return new BoundingSphere { Center = center, Radius = radius };
        }

        public BufferHandle VertexBuffer => _vertexBuffer;
        public BufferHandle IndexBuffer => _indexBuffer;
        public BufferHandle MeshMetadataBuffer => _meshMetadataBuffer;
        public BufferHandle MeshletBuffer => _meshletBuffer;
        public BufferHandle MeshletVertexIndexBuffer => _meshletVertexIndexBuffer;
        public BufferHandle MeshletTriangleIndexBuffer => _meshletTriangleIndexBuffer;

        public ulong VertexBytesUsed => _vertexBytesUsed;
        public ulong IndexBytesUsed => _indexBytesUsed;
        public ulong MeshMetadataBytesUsed => _meshMetadataBytesUsed;
        public ulong MeshletBytesUsed => _meshletBytesUsed;
        public ulong MeshletVertexIndexBytesUsed => _meshletVertexIndexBytesUsed;
        public ulong MeshletTriangleIndexBytesUsed => _meshletTriangleIndexBytesUsed;

        public void ValidateMeshInfoRanges(MeshInfo meshInfo)
        {
            lock (_lock)
            {
                ValidateElementRange(nameof(meshInfo.VertexOffset), meshInfo.VertexOffset, meshInfo.VertexCount, _vertexBytesUsed / VertexStride);
                ValidateElementRange(nameof(meshInfo.IndexOffset), meshInfo.IndexOffset, meshInfo.IndexCount, _indexBytesUsed / IndexStride);
                ValidateElementRange(nameof(meshInfo.MeshMetadataOffset), meshInfo.MeshMetadataOffset, 1, _meshMetadataBytesUsed / MeshMetadataStride);
                ValidateElementRange(nameof(meshInfo.MeshletOffset), meshInfo.MeshletOffset, meshInfo.MeshletLodGeneratedCount, _meshletBytesUsed / MeshletStride);
                ValidateElementRange(nameof(meshInfo.LocalVertexIndexOffset), meshInfo.LocalVertexIndexOffset, meshInfo.LocalVertexIndexCount, _meshletVertexIndexBytesUsed / IndexStride);
                ValidateElementRange(nameof(meshInfo.LocalTriangleIndexOffset), meshInfo.LocalTriangleIndexOffset, meshInfo.LocalTriangleIndexCount, _meshletTriangleIndexBytesUsed / IndexStride);
            }
        }

        private static void ValidateElementRange(string name, uint offset, uint count, ulong availableCount)
        {
            ulong end = (ulong)offset + count;
            if (end > availableCount)
            {
                throw new InvalidOperationException(
                    $"{name} range [{offset}, {end}) exceeds uploaded mesh buffer element count {availableCount}.");
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;

            RegisterStorageBuffer(bindlessHeap, BindlessIndex.SceneMeshMetadataBuffer, _meshMetadataBuffer);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.VertexBuffer, _vertexBuffer);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.IndexBuffer, _indexBuffer);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.MeshletBuffer, _meshletBuffer);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.MeshletVertexIndexBuffer, _meshletVertexIndexBuffer);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.MeshletTriangleIndexBuffer, _meshletTriangleIndexBuffer);
        }

        private void RegisterStorageBuffer(BindlessHeap bindlessHeap, int bindlessIndex, BufferHandle handle)
        {
            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            bindlessHeap.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        public MeshInfo GetMeshInfo(MeshHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _meshes.Count)
                    throw new InvalidOperationException("Invalid mesh handle");
                if (_meshGenerations[handle.Index] != handle.Generation)
                    throw new InvalidOperationException("Mesh handle generation mismatch");

                return _meshes[handle.Index];
            }
        }

        public Meshlet GetMeshlet(uint meshletIndex)
        {
            lock (_lock)
            {
                if (meshletIndex >= _meshlets.Count)
                    throw new InvalidOperationException("Invalid meshlet index.");

                return _meshlets[(int)meshletIndex];
            }
        }

        public MeshletQualityStats GetMeshletQualityStats()
        {
            lock (_lock)
            {
                int meshletCount = 0;
                ulong triangleSum = 0;
                ulong vertexSum = 0;
                int smallUnder16 = 0;
                int smallUnder32 = 0;

                foreach (MeshInfo meshInfo in _meshes)
                {
                    meshletCount += checked((int)meshInfo.MeshletLodGeneratedCount);
                    triangleSum += meshInfo.MeshletTriangleSum;
                    vertexSum += meshInfo.MeshletVertexSum;
                    smallUnder16 += checked((int)meshInfo.SmallMeshletsUnder16Triangles);
                    smallUnder32 += checked((int)meshInfo.SmallMeshletsUnder32Triangles);
                }

                return new MeshletQualityStats(meshletCount, triangleSum, vertexSum, smallUnder16, smallUnder32);
            }
        }

        private static void ApplyMeshletQualityStats(ref MeshInfo meshInfo, IReadOnlyList<Meshlet> meshlets)
        {
            uint triangleSum = 0;
            uint vertexSum = 0;
            uint smallUnder16 = 0;
            uint smallUnder32 = 0;

            foreach (Meshlet meshlet in meshlets)
            {
                triangleSum = CheckedAdd(triangleSum, meshlet.LocalTriangleCount);
                vertexSum = CheckedAdd(vertexSum, meshlet.LocalVertexCount);
                if (meshlet.LocalTriangleCount < 16)
                    smallUnder16++;
                if (meshlet.LocalTriangleCount < 32)
                    smallUnder32++;
            }

            meshInfo.MeshletTriangleSum = triangleSum;
            meshInfo.MeshletVertexSum = vertexSum;
            meshInfo.SmallMeshletsUnder16Triangles = smallUnder16;
            meshInfo.SmallMeshletsUnder32Triangles = smallUnder32;
        }

        private static ulong CheckedByteSize(int count, ulong stride)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return checked((ulong)count * stride);
        }

        private static ulong CalculateUploadStagingBytes(params ulong[] uploadSizes)
        {
            ulong offset = 0;
            foreach (ulong size in uploadSizes)
            {
                if (size == 0)
                    continue;

                offset = AlignUp(offset, UploadStagingAlignment);
                offset = checked(offset + size);
            }

            return Math.Max(offset, UploadStagingAlignment);
        }

        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        private static uint CheckedElementOffset(ulong byteOffset, ulong stride)
        {
            if (stride == 0 || byteOffset % stride != 0)
                throw new InvalidOperationException("Byte offset is not aligned to element stride.");

            ulong elementOffset = byteOffset / stride;
            if (elementOffset > uint.MaxValue)
                throw new InvalidOperationException("Mesh buffer element offset exceeds uint range.");

            return (uint)elementOffset;
        }

        private static uint CheckedCount(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return (uint)count;
        }

        private static uint CheckedAdd(uint left, uint right)
        {
            ulong value = (ulong)left + right;
            if (value > uint.MaxValue)
                throw new InvalidOperationException("Mesh offset arithmetic exceeded uint range.");

            return (uint)value;
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
                DestroyIfValid(_vertexBuffer);
                DestroyIfValid(_indexBuffer);
                DestroyIfValid(_meshMetadataBuffer);
                DestroyIfValid(_meshletBuffer);
                DestroyIfValid(_meshletVertexIndexBuffer);
                DestroyIfValid(_meshletTriangleIndexBuffer);

                _meshes.Clear();
                _meshlets.Clear();
                _meshGenerations.Clear();
                _freeIndices.Clear();
            }

            Console.WriteLine("Mesh manager disposed.");
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        ~MeshManager()
        {
            Dispose(false);
        }

        private sealed class UploadCommandContext
        {
            public UploadCommandContext(
                CommandPool commandPool,
                CommandBuffer commandBuffer,
                BufferHandle stagingBuffer,
                ulong stagingBufferSize)
            {
                CommandPool = commandPool;
                CommandBuffer = commandBuffer;
                StagingBuffer = stagingBuffer;
                StagingBufferSize = stagingBufferSize;
            }

            public CommandPool CommandPool;
            public CommandBuffer CommandBuffer;
            public BufferHandle StagingBuffer;
            public ulong StagingBufferSize;
            public ulong StagingOffset;
            public bool Completed { get; private set; }

            public void MarkCompleted()
            {
                Completed = true;
            }
        }
    }

    public readonly record struct MeshletQualityStats(
        int MeshletCount,
        ulong TriangleSum,
        ulong VertexSum,
        int SmallMeshletsUnder16Triangles,
        int SmallMeshletsUnder32Triangles);
}
