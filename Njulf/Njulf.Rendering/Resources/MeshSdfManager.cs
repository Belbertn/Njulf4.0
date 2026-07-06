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

        private const ulong HashStart = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        private static readonly ulong MeshSdfStride = (ulong)Marshal.SizeOf<GPUMeshSdf>();
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly MeshManager _meshManager;
        private readonly object _lock = new();
        private readonly List<MeshSdfRecord> _records = new();
        private readonly Dictionary<MeshHandle, MeshSdfRecord> _recordsByMesh = new();
        private readonly List<GPUMeshSdf> _activeInstanceRecords = new();
        private readonly List<BoundingBox> _activeInstanceBounds = new();
        private readonly List<MeshHandle> _newlyBakedMeshes = new();
        private BufferHandle _meshSdfBuffer;
        private int _capacity;
        private ulong _lastUploadedInstanceSignature;
        private int _lastUploadedInstanceCount;
        private bool _hasUploadedInstanceRecords;
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
        public ulong LastFrameInstanceUploadBytes { get; private set; }
        public int LastFrameInstanceUploadSkipped { get; private set; }

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
                    _bindlessHeap.RegisterTexture(
                        bindlessIndex,
                        volume.View,
                        _bindlessHeap.VolumeClampSampler,
                        ImageLayout.ShaderReadOnlyOptimal);

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
                    _newlyBakedMeshes.Add(request.Mesh);
                    MeshSdfTextureBytes = checked(MeshSdfTextureBytes + descriptor.EstimatedByteSize);
                    LastFrameAllocatedBytes = checked(LastFrameAllocatedBytes + descriptor.EstimatedByteSize);
                    LastFrameBakeVoxelCount = checked(LastFrameBakeVoxelCount + (ulong)descriptor.Extent.Width * descriptor.Extent.Height * descriptor.Extent.Depth);

                    jobs.Add(new MeshSdfBakeJob(request, meshSdfIndex, bindlessIndex, volume, CreatePushConstants(gpuRecord, meshSdfIndex, bindlessIndex)));
                }
            }

            LastFrameBakedMeshCount = jobs.Count;
            return jobs;
        }

        internal int MarkNewlyBakedInstanceBoundsDirty(
            IReadOnlyList<AccelerationStructureManager.StaticOpaqueInstance> instances,
            GlobalSdfManager globalSdfManager)
        {
            if (instances == null)
                throw new ArgumentNullException(nameof(instances));
            if (globalSdfManager == null)
                throw new ArgumentNullException(nameof(globalSdfManager));

            lock (_lock)
            {
                if (_newlyBakedMeshes.Count == 0 || instances.Count == 0)
                    return 0;

                int markedCount = 0;
                for (int meshIndex = 0; meshIndex < _newlyBakedMeshes.Count; meshIndex++)
                {
                    MeshHandle mesh = _newlyBakedMeshes[meshIndex];
                    if (!_recordsByMesh.TryGetValue(mesh, out MeshSdfRecord? bakedRecord))
                        continue;

                    for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
                    {
                        AccelerationStructureManager.StaticOpaqueInstance instance = instances[instanceIndex];
                        if (instance.Mesh != mesh)
                            continue;

                        if (!TryCreateInstanceGpuRecord(bakedRecord.GpuRecord, instance.WorldMatrix, out GPUMeshSdf instanceRecord))
                            continue;

                        globalSdfManager.MarkDirtyWorldBounds(CreateWorldBounds(instanceRecord));
                        markedCount++;
                    }
                }

                _newlyBakedMeshes.Clear();
                return markedCount;
            }
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
                ulong instanceSignature = HashStart;
                _activeInstanceRecords.Clear();
                _activeInstanceBounds.Clear();
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
                    _activeInstanceBounds.Add(CreateWorldBounds(instanceRecord));
                    instanceSignature = HashAdd(instanceSignature, instance.Mesh.Index);
                    instanceSignature = HashAdd(instanceSignature, instance.Mesh.Generation);
                    instanceSignature = HashAdd(instanceSignature, bakedRecord.BindlessTextureIndex);
                    instanceSignature = HashAdd(instanceSignature, bakedRecord.GpuRecord.Flags);
                    instanceSignature = HashAdd(instanceSignature, instance.WorldMatrix);
                    activeCount++;
                }

                instanceSignature = HashAdd(instanceSignature, activeCount);
                bool uploadRequired = _activeInstanceRecords.Count > 0 &&
                    (!_hasUploadedInstanceRecords ||
                        _lastUploadedInstanceCount != activeCount ||
                        _lastUploadedInstanceSignature != instanceSignature);
                LastFrameInstanceUploadBytes = 0;
                LastFrameInstanceUploadSkipped = _activeInstanceRecords.Count > 0 && !uploadRequired ? 1 : 0;

                if (uploadRequired)
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
                    _lastUploadedInstanceSignature = instanceSignature;
                    _lastUploadedInstanceCount = activeCount;
                    _hasUploadedInstanceRecords = true;
                    LastFrameInstanceUploadBytes = checked((ulong)_activeInstanceRecords.Count * MeshSdfStride);
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
            _hasUploadedInstanceRecords = false;
            _lastUploadedInstanceSignature = 0;
            _lastUploadedInstanceCount = 0;

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
                WorldBoundsMinAndLocalScaleX = new Vector4(localMin.X, localMin.Y, localMin.Z, 1.0f),
                WorldBoundsMaxAndLocalScaleY = new Vector4(localMax.X, localMax.Y, localMax.Z, 1.0f),
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

        private static ulong HashAdd(ulong hash, int value)
        {
            return HashAdd(hash, unchecked((uint)value));
        }

        private static ulong HashAdd(ulong hash, uint value)
        {
            hash ^= value;
            return hash * HashPrime;
        }

        private static ulong HashAdd(ulong hash, float value)
        {
            return HashAdd(hash, BitConverter.SingleToUInt32Bits(value));
        }

        private static ulong HashAdd(ulong hash, Matrix4x4 matrix)
        {
            hash = HashAdd(hash, matrix.M11);
            hash = HashAdd(hash, matrix.M12);
            hash = HashAdd(hash, matrix.M13);
            hash = HashAdd(hash, matrix.M14);
            hash = HashAdd(hash, matrix.M21);
            hash = HashAdd(hash, matrix.M22);
            hash = HashAdd(hash, matrix.M23);
            hash = HashAdd(hash, matrix.M24);
            hash = HashAdd(hash, matrix.M31);
            hash = HashAdd(hash, matrix.M32);
            hash = HashAdd(hash, matrix.M33);
            hash = HashAdd(hash, matrix.M34);
            hash = HashAdd(hash, matrix.M41);
            hash = HashAdd(hash, matrix.M42);
            hash = HashAdd(hash, matrix.M43);
            hash = HashAdd(hash, matrix.M44);
            return hash;
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

            Vector3 localToWorldScale = ComputeLocalToWorldAxisScales(worldToLocal);
            if (!IsFinite(localToWorldScale) || localToWorldScale.X <= 0.0f || localToWorldScale.Y <= 0.0f || localToWorldScale.Z <= 0.0f)
                return false;

            localToWorldScale = new Vector3(
                MathF.Max(localToWorldScale.X, 0.0001f),
                MathF.Max(localToWorldScale.Y, 0.0001f),
                MathF.Max(localToWorldScale.Z, 0.0001f));

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

            float maxAxisScale = MathF.Max(localToWorldScale.X, MathF.Max(localToWorldScale.Y, localToWorldScale.Z));
            float meshSdfWorldVoxelSize = MathF.Max(bakedRecord.LocalBoundsMinAndVoxelSize.W * maxAxisScale, 0.0f);
            Vector3 boundsInflation = new(meshSdfWorldVoxelSize);
            worldBounds = new BoundingBox(worldBounds.Min - boundsInflation, worldBounds.Max + boundsInflation);

            instanceRecord = bakedRecord;
            instanceRecord.WorldBoundsMinAndLocalScaleX = new Vector4(worldBounds.Min.X, worldBounds.Min.Y, worldBounds.Min.Z, localToWorldScale.X);
            instanceRecord.WorldBoundsMaxAndLocalScaleY = new Vector4(worldBounds.Max.X, worldBounds.Max.Y, worldBounds.Max.Z, localToWorldScale.Y);
            instanceRecord.WorldToLocalRow0 = new Vector4(worldToLocal.M11, worldToLocal.M12, worldToLocal.M13, worldToLocal.M41);
            instanceRecord.WorldToLocalRow1 = new Vector4(worldToLocal.M21, worldToLocal.M22, worldToLocal.M23, worldToLocal.M42);
            instanceRecord.WorldToLocalRow2 = new Vector4(worldToLocal.M31, worldToLocal.M32, worldToLocal.M33, worldToLocal.M43);
            instanceRecord.WorldToLocalAxisScale = new Vector4(localToWorldScale.X, localToWorldScale.Y, localToWorldScale.Z, maxAxisScale);
            return true;
        }

        private static Vector3 ToCoreVector3(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);

        private static Vector3 ComputeLocalToWorldAxisScales(Matrix4x4 worldToLocal) =>
            new(
                1.0f / MathF.Max(Length(worldToLocal.M11, worldToLocal.M21, worldToLocal.M31), 0.0001f),
                1.0f / MathF.Max(Length(worldToLocal.M12, worldToLocal.M22, worldToLocal.M32), 0.0001f),
                1.0f / MathF.Max(Length(worldToLocal.M13, worldToLocal.M23, worldToLocal.M33), 0.0001f));

        private static float Length(float x, float y, float z) => MathF.Sqrt(x * x + y * y + z * z);

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) &&
            float.IsFinite(value.Y) &&
            float.IsFinite(value.Z);

        private static BoundingBox CreateWorldBounds(GPUMeshSdf instanceRecord)
        {
            Vector3 min = new(
                instanceRecord.WorldBoundsMinAndLocalScaleX.X,
                instanceRecord.WorldBoundsMinAndLocalScaleX.Y,
                instanceRecord.WorldBoundsMinAndLocalScaleX.Z);
            Vector3 max = new(
                instanceRecord.WorldBoundsMaxAndLocalScaleY.X,
                instanceRecord.WorldBoundsMaxAndLocalScaleY.Y,
                instanceRecord.WorldBoundsMaxAndLocalScaleY.Z);
            return new BoundingBox(min, max);
        }

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
                _activeInstanceBounds.Clear();
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
