using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class MeshSdfManager : IDisposable
    {
        public const int InitialMeshSdfCapacity = 256;

        private static readonly ulong MeshSdfStride = (ulong)Marshal.SizeOf<GPUMeshSdf>();
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly MeshManager _meshManager;
        private readonly object _lock = new();
        private readonly List<MeshSdfRecord> _records = new();
        private readonly Dictionary<MeshHandle, MeshSdfRecord> _recordsByMesh = new();
        private readonly List<GPUMeshSdf> _activeInstanceRecords = new();
        private BufferHandle _meshSdfBuffer;
        private int _capacity;
        private bool _disposed;

        public MeshSdfManager(
            VulkanContext context,
            BufferManager bufferManager,
            BindlessHeap bindlessHeap,
            MeshManager meshManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            EnsureCapacity(InitialMeshSdfCapacity);
        }

        public int BakedMeshCount
        {
            get
            {
                lock (_lock)
                    return _records.Count;
            }
        }

        public int PendingBakeCount => _meshManager.PendingMeshSdfBakeCount;
        public ulong MeshSdfBufferBytes => (ulong)_capacity * MeshSdfStride;
        public ulong MeshSdfTextureBytes { get; private set; }
        public int LastFrameBakedMeshCount { get; private set; }
        public int LastFrameUnsignedFallbackMeshCount { get; private set; }
        public int TotalUnsignedFallbackMeshCount { get; private set; }
        public int LastFrameQueuedMeshCount { get; private set; }
        public ulong LastFrameBakeVoxelCount { get; private set; }
        public ulong LastFrameAllocatedBytes { get; private set; }
        public int ActiveInstanceSdfCount { get; private set; }
        public int LastFrameSkippedInstanceSdfCount { get; private set; }

        public IReadOnlyList<MeshSdfBakeJob> PrepareBakeJobs(int maxCount)
        {
            if (maxCount < 0)
                throw new ArgumentOutOfRangeException(nameof(maxCount));

            LastFrameBakedMeshCount = 0;
            LastFrameUnsignedFallbackMeshCount = 0;
            LastFrameQueuedMeshCount = PendingBakeCount;
            LastFrameBakeVoxelCount = 0;
            LastFrameAllocatedBytes = 0;

            IReadOnlyList<MeshSdfBakeRequest> requests = _meshManager.DequeueMeshSdfBakeRequests(maxCount);
            if (requests.Count == 0)
                return Array.Empty<MeshSdfBakeJob>();

            var jobs = new List<MeshSdfBakeJob>(requests.Count);
            lock (_lock)
            {
                EnsureCapacity(_records.Count + requests.Count);
                for (int i = 0; i < requests.Count; i++)
                {
                    MeshSdfBakeRequest request = requests[i];
                    MeshSdfBakeDescriptor descriptor = request.Descriptor;
                    var volume = new VolumeTexture(
                        _context,
                        $"Mesh SDF {request.Mesh.Index}:{request.Mesh.Generation}",
                        Format.R16Sfloat,
                        descriptor.Extent,
                        new VolumeTextureDescriptor(sampled: true, storage: true));

                    int bindlessIndex = _bindlessHeap.AllocateStorageImageIndex(volume.View, ImageLayout.General);
                    _bindlessHeap.RegisterTexture(bindlessIndex, volume.View, imageLayout: ImageLayout.ShaderReadOnlyOptimal);

                    uint meshSdfIndex = checked((uint)_records.Count);
                    GPUMeshSdf gpuRecord = CreateGpuRecord(request, descriptor, bindlessIndex);
                    if ((gpuRecord.Flags & MeshSdfBakePlanner.MeshSdfFlagUnsignedFallback) != 0)
                    {
                        LastFrameUnsignedFallbackMeshCount++;
                        TotalUnsignedFallbackMeshCount++;
                    }

                    var record = new MeshSdfRecord(request.Mesh, volume, bindlessIndex, gpuRecord, descriptor.EstimatedByteSize);
                    _records.Add(record);
                    _recordsByMesh[request.Mesh] = record;
                    MeshSdfTextureBytes = checked(MeshSdfTextureBytes + descriptor.EstimatedByteSize);
                    LastFrameAllocatedBytes = checked(LastFrameAllocatedBytes + descriptor.EstimatedByteSize);
                    LastFrameBakeVoxelCount = checked(LastFrameBakeVoxelCount + (ulong)descriptor.Extent.Width * descriptor.Extent.Height * descriptor.Extent.Depth);

                    jobs.Add(new MeshSdfBakeJob(request, meshSdfIndex, bindlessIndex, volume, CreatePushConstants(gpuRecord, meshSdfIndex, bindlessIndex)));
                }
            }

            LastFrameBakedMeshCount = jobs.Count;
            return jobs;
        }

        internal int PrepareInstanceRecords(
            IReadOnlyList<AccelerationStructureManager.StaticOpaqueInstance> instances,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            if (instances == null)
                throw new ArgumentNullException(nameof(instances));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for mesh SDF instance upload.", nameof(commandBuffer));

            lock (_lock)
            {
                EnsureCapacity(instances.Count);

                int activeCount = 0;
                int skippedCount = 0;
                _activeInstanceRecords.Clear();
                for (int i = 0; i < instances.Count; i++)
                {
                    AccelerationStructureManager.StaticOpaqueInstance instance = instances[i];
                    if (!_recordsByMesh.TryGetValue(instance.Mesh, out MeshSdfRecord? bakedRecord))
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!TryCreateInstanceGpuRecord(bakedRecord.GpuRecord, instance.WorldMatrix, out GPUMeshSdf instanceRecord))
                    {
                        skippedCount++;
                        continue;
                    }

                    _activeInstanceRecords.Add(instanceRecord);
                    activeCount++;
                }

                if (_activeInstanceRecords.Count > 0)
                {
                    GpuBufferUploader.UploadSpanToBuffer(
                        _context,
                        _bufferManager,
                        stagingRing,
                        commandBuffer,
                        _meshSdfBuffer,
                        CollectionsMarshal.AsSpan(_activeInstanceRecords),
                        barrierDescription: new UploadBarrierDescription(
                            PipelineStageFlags2.ComputeShaderBit,
                            AccessFlags2.ShaderStorageReadBit));
                }

                ActiveInstanceSdfCount = activeCount;
                LastFrameSkippedInstanceSdfCount = skippedCount;
                return activeCount;
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.MeshSdfBuffer,
                _bufferManager.GetBuffer(_meshSdfBuffer),
                0,
                MeshSdfBufferBytes);
        }

        private void EnsureCapacity(int requiredCount)
        {
            if (_capacity >= requiredCount && _meshSdfBuffer.IsValid)
                return;

            int nextCapacity = Math.Max(InitialMeshSdfCapacity, _capacity);
            while (nextCapacity < requiredCount)
                nextCapacity *= 2;

            BufferHandle oldBuffer = _meshSdfBuffer;
            _meshSdfBuffer = _bufferManager.CreateBuffer(
                checked((ulong)nextCapacity * MeshSdfStride),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferDevice,
                default,
                $"Mesh SDF Metadata Buffer ({nextCapacity} records)",
                MemoryBudgetCategory.RenderTargets);
            _capacity = nextCapacity;
            RegisterBuffers(_bindlessHeap);

            if (oldBuffer.IsValid)
                _bufferManager.DestroyBuffer(oldBuffer);
        }

        private static GPUMeshSdf CreateGpuRecord(MeshSdfBakeRequest request, MeshSdfBakeDescriptor descriptor, int bindlessIndex)
        {
            MeshInfo meshInfo = request.MeshInfo;
            Vector3 localMin = ToCoreVector3(descriptor.BoundsMin);
            Vector3 localExtent = ToCoreVector3(descriptor.BoundsExtent);
            Vector3 localMax = localMin + localExtent;
            return new GPUMeshSdf
            {
                LocalBoundsMinAndVoxelSize = new Vector4(localMin.X, localMin.Y, localMin.Z, descriptor.VoxelSize),
                LocalBoundsExtentAndInvVoxelSize = new Vector4(localExtent.X, localExtent.Y, localExtent.Z, descriptor.InvVoxelSize),
                WorldBoundsMinAndDistanceScale = new Vector4(localMin.X, localMin.Y, localMin.Z, 1.0f),
                WorldBoundsMaxAndInvDistanceScale = new Vector4(localMax.X, localMax.Y, localMax.Z, 1.0f),
                WorldToLocalRow0 = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
                WorldToLocalRow1 = new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
                WorldToLocalRow2 = new Vector4(0.0f, 0.0f, 1.0f, 0.0f),
                TextureIndex = checked((uint)bindlessIndex),
                ResolutionX = descriptor.Extent.Width,
                ResolutionY = descriptor.Extent.Height,
                ResolutionZ = descriptor.Extent.Depth,
                VertexOffset = meshInfo.VertexOffset,
                VertexCount = meshInfo.VertexCount,
                IndexOffset = meshInfo.IndexOffset,
                IndexCount = meshInfo.IndexCount,
                MeshIndex = checked((uint)request.Mesh.Index),
                Flags = request.Flags,
                Padding0 = 0,
                Padding1 = 0
            };
        }

        internal static bool TryCreateInstanceGpuRecord(GPUMeshSdf bakedRecord, Matrix4x4 worldMatrix, out GPUMeshSdf instanceRecord)
        {
            instanceRecord = default;

            Matrix4x4 worldToLocal;
            try
            {
                worldToLocal = worldMatrix.Invert();
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            float distanceScale = ComputeConservativeDistanceScale(worldToLocal);
            if (!float.IsFinite(distanceScale) || distanceScale <= 0.0f)
                return false;

            distanceScale = MathF.Max(distanceScale, 0.0001f);

            Vector3 localMin = new(
                bakedRecord.LocalBoundsMinAndVoxelSize.X,
                bakedRecord.LocalBoundsMinAndVoxelSize.Y,
                bakedRecord.LocalBoundsMinAndVoxelSize.Z);
            Vector3 localExtent = new(
                bakedRecord.LocalBoundsExtentAndInvVoxelSize.X,
                bakedRecord.LocalBoundsExtentAndInvVoxelSize.Y,
                bakedRecord.LocalBoundsExtentAndInvVoxelSize.Z);
            Vector3 localMax = localMin + localExtent;
            BoundingBox worldBounds = SceneDataBuilder.TransformBoundingBox(new BoundingBox(localMin, localMax), worldMatrix);
            if (!IsFinite(worldBounds.Min) || !IsFinite(worldBounds.Max))
                return false;

            instanceRecord = bakedRecord;
            instanceRecord.WorldBoundsMinAndDistanceScale = new Vector4(worldBounds.Min.X, worldBounds.Min.Y, worldBounds.Min.Z, distanceScale);
            instanceRecord.WorldBoundsMaxAndInvDistanceScale = new Vector4(worldBounds.Max.X, worldBounds.Max.Y, worldBounds.Max.Z, 1.0f / distanceScale);
            instanceRecord.WorldToLocalRow0 = new Vector4(worldToLocal.M11, worldToLocal.M12, worldToLocal.M13, worldToLocal.M41);
            instanceRecord.WorldToLocalRow1 = new Vector4(worldToLocal.M21, worldToLocal.M22, worldToLocal.M23, worldToLocal.M42);
            instanceRecord.WorldToLocalRow2 = new Vector4(worldToLocal.M31, worldToLocal.M32, worldToLocal.M33, worldToLocal.M43);
            return true;
        }

        private static Vector3 ToCoreVector3(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);

        private static float ComputeConservativeDistanceScale(Matrix4x4 worldToLocal)
        {
            float c00 = worldToLocal.M11 * worldToLocal.M11 + worldToLocal.M21 * worldToLocal.M21 + worldToLocal.M31 * worldToLocal.M31;
            float c01 = worldToLocal.M11 * worldToLocal.M12 + worldToLocal.M21 * worldToLocal.M22 + worldToLocal.M31 * worldToLocal.M32;
            float c02 = worldToLocal.M11 * worldToLocal.M13 + worldToLocal.M21 * worldToLocal.M23 + worldToLocal.M31 * worldToLocal.M33;
            float c11 = worldToLocal.M12 * worldToLocal.M12 + worldToLocal.M22 * worldToLocal.M22 + worldToLocal.M32 * worldToLocal.M32;
            float c12 = worldToLocal.M12 * worldToLocal.M13 + worldToLocal.M22 * worldToLocal.M23 + worldToLocal.M32 * worldToLocal.M33;
            float c22 = worldToLocal.M13 * worldToLocal.M13 + worldToLocal.M23 * worldToLocal.M23 + worldToLocal.M33 * worldToLocal.M33;

            float rowSum0 = MathF.Abs(c00) + MathF.Abs(c01) + MathF.Abs(c02);
            float rowSum1 = MathF.Abs(c01) + MathF.Abs(c11) + MathF.Abs(c12);
            float rowSum2 = MathF.Abs(c02) + MathF.Abs(c12) + MathF.Abs(c22);
            float spectralUpperBound = MathF.Max(rowSum0, MathF.Max(rowSum1, rowSum2));
            return spectralUpperBound > 0.0f ? 1.0f / MathF.Sqrt(spectralUpperBound) : float.NaN;
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) &&
            float.IsFinite(value.Y) &&
            float.IsFinite(value.Z);

        private static GPUMeshSdfBakeConstants CreatePushConstants(GPUMeshSdf record, uint meshSdfIndex, int bindlessIndex)
        {
            return new GPUMeshSdfBakeConstants
            {
                MeshSdfBufferIndex = BindlessIndex.MeshSdfBuffer,
                MeshSdfIndex = meshSdfIndex,
                VertexPositionBufferIndex = BindlessIndex.VertexPositionBuffer,
                IndexBufferIndex = BindlessIndex.IndexBuffer,
                StorageImageIndex = checked((uint)bindlessIndex),
                TriangleCount = record.IndexCount / 3u,
                VertexOffset = record.VertexOffset,
                IndexOffset = record.IndexOffset,
                FrameIndex = 0,
                Flags = record.Flags,
                Padding0 = 0,
                Padding1 = 0,
                LocalBoundsMinAndVoxelSize = record.LocalBoundsMinAndVoxelSize,
                LocalBoundsExtentAndInvVoxelSize = record.LocalBoundsExtentAndInvVoxelSize
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            lock (_lock)
            {
                foreach (MeshSdfRecord record in _records)
                {
                    _bindlessHeap.FreeTextureIndex(record.BindlessTextureIndex);
                    record.Volume.Dispose();
                }

                _records.Clear();
                _recordsByMesh.Clear();
                ActiveInstanceSdfCount = 0;
                LastFrameSkippedInstanceSdfCount = 0;
                if (_meshSdfBuffer.IsValid)
                {
                    _bufferManager.DestroyBuffer(_meshSdfBuffer);
                    _meshSdfBuffer = BufferHandle.Invalid;
                }
            }

            GC.SuppressFinalize(this);
        }

        private sealed record MeshSdfRecord(
            MeshHandle Mesh,
            VolumeTexture Volume,
            int BindlessTextureIndex,
            GPUMeshSdf GpuRecord,
            ulong TextureBytes);
    }

    public sealed record MeshSdfBakeJob(
        MeshSdfBakeRequest Request,
        uint MeshSdfIndex,
        int BindlessTextureIndex,
        VolumeTexture Volume,
        GPUMeshSdfBakeConstants PushConstants);
}
