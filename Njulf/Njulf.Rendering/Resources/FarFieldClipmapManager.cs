using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed class FarFieldClipmapManager : IDisposable
    {
        private const ulong MinBufferSize = 16;
        private const ulong VoxelStride = 4;
        private static readonly ulong ParamsSize = (ulong)Marshal.SizeOf<GPUFarFieldClipmapParams>();
        private static readonly ulong InstanceStride = (ulong)Marshal.SizeOf<GPUFarFieldInstance>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly List<AccelerationStructureManager.StaticOpaqueInstance> _staticInstances = new();
        private readonly List<GPUFarFieldInstance> _gpuInstances = new();

        private BufferHandle _paramsBuffer;
        private BufferHandle _voxelBuffer;
        private BufferHandle _instanceBuffer;
        private ulong _voxelBufferBytes;
        private ulong _instanceBufferBytes;
        private BindlessHeap? _registeredBindlessHeap;
        private GPUFarFieldClipmapParams _lastParams;
        private ulong _lastSignature;
        private bool _bakePending;
        private bool _disposed;

        public FarFieldClipmapManager(
            VulkanContext context,
            BufferManager bufferManager,
            RenderSettings settings,
            AccelerationStructureManager accelerationStructureManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));

            _paramsBuffer = _bufferManager.CreateDeviceBuffer(
                Math.Max(MinBufferSize, ParamsSize),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: "Far Field Clipmap Params");
            EnsureVoxelCapacity(1);
            EnsureInstanceCapacity(0);
        }

        public int InstanceCount => _gpuInstances.Count;
        public int Resolution => _settings.GlobalIllumination.FarFieldClipmapResolution;
        public bool BakePending => _bakePending;
        public GPUFarFieldClipmapParams LastParams => _lastParams;
        public ulong BufferBytes => ParamsSize + _voxelBufferBytes + _instanceBufferBytes;

        public uint GetTriangleCount(int instanceIndex)
        {
            if ((uint)instanceIndex >= (uint)_gpuInstances.Count)
                return 0;

            return _gpuInstances[instanceIndex].IndexCount / 3u;
        }

        public bool ConsumeBakePending()
        {
            bool pending = _bakePending;
            _bakePending = false;
            return pending;
        }

        public void MarkBakePending()
        {
            _bakePending = true;
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.FarFieldClipmapParamsBuffer, _bufferManager.GetBuffer(_paramsBuffer), 0, Math.Max(MinBufferSize, ParamsSize));
            RegisterIfValid(BindlessIndex.FarFieldClipmapVoxelBuffer, _voxelBuffer, _voxelBufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapInstanceBuffer, _instanceBuffer, _instanceBufferBytes);
        }

        public void Upload(Scene scene, StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            int resolution = gi.FarFieldClipmapResolution;
            EnsureVoxelCapacity(resolution);

            _accelerationStructureManager.CollectStaticOpaqueInstances(scene, _staticInstances);
            _gpuInstances.Clear();
            foreach (AccelerationStructureManager.StaticOpaqueInstance instance in _staticInstances)
            {
                _gpuInstances.Add(new GPUFarFieldInstance
                {
                    VertexOffset = instance.MeshInfo.VertexOffset,
                    IndexOffset = instance.MeshInfo.IndexOffset,
                    IndexCount = instance.MeshInfo.IndexCount,
                    MaterialIndex = instance.MaterialIndex,
                    World = instance.WorldMatrix
                });
            }

            EnsureInstanceCapacity(_gpuInstances.Count);
            if (_gpuInstances.Count > 0)
            {
                GpuBufferUploader.UploadSpanToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _instanceBuffer,
                    CollectionsMarshal.AsSpan(_gpuInstances),
                    barrierDescription: new UploadBarrierDescription(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit));
            }

            BoundingBox bounds = ExpandBounds(DdgiFrameLayoutBuilder.EstimateSceneProbeBounds(scene), gi.SimpleDdgiProbeSpacing * 2.0f);
            Vector3 extent = bounds.Max - bounds.Min;
            float maxExtent = MathF.Max(MathF.Max(extent.X, extent.Y), extent.Z);
            float voxelSize = MathF.Max(maxExtent / Math.Max(1, resolution), 0.001f);
            float cubicExtent = voxelSize * resolution;

            _lastParams = new GPUFarFieldClipmapParams
            {
                OriginAndVoxelSize = new Vector4(bounds.Min.X, bounds.Min.Y, bounds.Min.Z, voxelSize),
                ResolutionAndExtent = new Vector4(resolution, resolution, resolution, cubicExtent),
                TraceParams = new Vector4(gi.FarFieldStartDistance, gi.FarFieldMaxTraceSteps, gi.FarFieldClipmapEnabled ? 1.0f : 0.0f, gi.FarFieldForceAll ? 1.0f : 0.0f),
                BakeParams = new Vector4(_gpuInstances.Count, 0.0f, 0.0f, 0.0f),
                Diagnostics = Vector4.Zero,
                Reserved0 = Vector4.Zero
            };

            GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _paramsBuffer,
                _lastParams,
                barrierDescription: new UploadBarrierDescription(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit));

            ulong signature = CreateSignature(resolution, bounds, _gpuInstances);
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                _bakePending = true;
            }
        }

        private void EnsureVoxelCapacity(int resolution)
        {
            ulong voxelCount = checked((ulong)Math.Max(1, resolution) * (ulong)Math.Max(1, resolution) * (ulong)Math.Max(1, resolution));
            ulong requiredBytes = Math.Max(MinBufferSize, checked(voxelCount * VoxelStride));
            EnsureBuffer(ref _voxelBuffer, ref _voxelBufferBytes, requiredBytes, "Far Field Clipmap Voxels");
        }

        private void EnsureInstanceCapacity(int instanceCount)
        {
            ulong requiredBytes = Math.Max(MinBufferSize, checked((ulong)Math.Max(1, instanceCount) * InstanceStride));
            EnsureBuffer(ref _instanceBuffer, ref _instanceBufferBytes, requiredBytes, "Far Field Clipmap Instances");
        }

        private void EnsureBuffer(ref BufferHandle handle, ref ulong currentBytes, ulong requiredBytes, string debugName)
        {
            if (handle.IsValid && currentBytes >= requiredBytes)
                return;

            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);

            handle = _bufferManager.CreateDeviceBuffer(
                requiredBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: debugName);
            currentBytes = requiredBytes;
            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
        }

        private void RegisterIfValid(int index, BufferHandle handle, ulong size)
        {
            if (_registeredBindlessHeap == null || !handle.IsValid)
                return;

            _registeredBindlessHeap.RegisterStorageBuffer(index, _bufferManager.GetBuffer(handle), 0, Math.Max(MinBufferSize, size));
        }

        private static BoundingBox ExpandBounds(BoundingBox bounds, float padding)
        {
            Vector3 p = new(Math.Max(padding, 0.0f));
            return new BoundingBox(bounds.Min - p, bounds.Max + p);
        }

        private static ulong CreateSignature(int resolution, BoundingBox bounds, IReadOnlyList<GPUFarFieldInstance> instances)
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, (uint)resolution);
            hash = HashAdd(hash, bounds.Min);
            hash = HashAdd(hash, bounds.Max);
            hash = HashAdd(hash, (uint)instances.Count);
            for (int i = 0; i < instances.Count; i++)
            {
                GPUFarFieldInstance instance = instances[i];
                hash = HashAdd(hash, instance.VertexOffset);
                hash = HashAdd(hash, instance.IndexOffset);
                hash = HashAdd(hash, instance.IndexCount);
                hash = HashAdd(hash, instance.MaterialIndex);
            }
            return hash;
        }

        private static ulong HashAdd(ulong hash, Vector3 value)
        {
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.X));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.Y));
            return HashAdd(hash, BitConverter.SingleToUInt32Bits(value.Z));
        }

        private static ulong HashAdd(ulong hash, uint value)
        {
            hash ^= value;
            return hash * 1099511628211UL;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_paramsBuffer.IsValid)
                _bufferManager.DestroyBuffer(_paramsBuffer);
            if (_voxelBuffer.IsValid)
                _bufferManager.DestroyBuffer(_voxelBuffer);
            if (_instanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_instanceBuffer);
        }
    }
}
