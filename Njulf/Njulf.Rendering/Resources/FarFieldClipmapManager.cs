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
        private BufferHandle _bakeVoxelBuffer;
        private BufferHandle _instanceBuffer;
        private ulong _voxelBufferBytes;
        private ulong _bakeVoxelBufferBytes;
        private ulong _instanceBufferBytes;
        private BindlessHeap? _registeredBindlessHeap;
        private GPUFarFieldClipmapParams _lastParams;
        private ulong _lastSignature;
        private int _activeVoxelBufferIndex = BindlessIndex.FarFieldClipmapVoxelBuffer;
        private int _bakeVoxelBufferIndex = BindlessIndex.FarFieldClipmapBakeVoxelBuffer;
        private Vector3 _clipmapOrigin;
        private bool _hasClipmapOrigin;
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
        public int BakeVoxelBufferIndex => _bakeVoxelBufferIndex;
        public GPUFarFieldClipmapParams LastParams => _lastParams;
        public ulong BufferBytes => ParamsSize + _voxelBufferBytes + _bakeVoxelBufferBytes + _instanceBufferBytes;

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

        public void MarkBakePublished()
        {
            (_activeVoxelBufferIndex, _bakeVoxelBufferIndex) = (_bakeVoxelBufferIndex, _activeVoxelBufferIndex);
            _lastParams.Diagnostics = new Vector4(_activeVoxelBufferIndex, _bakeVoxelBufferIndex, 0.0f, 0.0f);
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.FarFieldClipmapParamsBuffer, _bufferManager.GetBuffer(_paramsBuffer), 0, Math.Max(MinBufferSize, ParamsSize));
            RegisterIfValid(BindlessIndex.FarFieldClipmapVoxelBuffer, _voxelBuffer, _voxelBufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapBakeVoxelBuffer, _bakeVoxelBuffer, _bakeVoxelBufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapInstanceBuffer, _instanceBuffer, _instanceBufferBytes);
        }

        public void Upload(Scene scene, Vector3 cameraPosition, StagingRing stagingRing, CommandBuffer commandBuffer)
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
            _clipmapOrigin = ResolveSceneClampedOrigin(bounds.Min, bounds.Max, cubicExtent, voxelSize, cameraPosition, _clipmapOrigin, ref _hasClipmapOrigin, out bool recentered);
            if (recentered)
                _bakePending = true;

            _lastParams = new GPUFarFieldClipmapParams
            {
                OriginAndVoxelSize = new Vector4(_clipmapOrigin.X, _clipmapOrigin.Y, _clipmapOrigin.Z, voxelSize),
                ResolutionAndExtent = new Vector4(resolution, resolution, resolution, cubicExtent),
                TraceParams = new Vector4(gi.FarFieldStartDistance, gi.FarFieldMaxTraceSteps, gi.FarFieldClipmapEnabled ? 1.0f : 0.0f, gi.FarFieldForceAll ? 1.0f : 0.0f),
                BakeParams = new Vector4(_gpuInstances.Count, 0.0f, 0.0f, 0.0f),
                Diagnostics = new Vector4(_activeVoxelBufferIndex, _bakeVoxelBufferIndex, _bakePending ? 1.0f : 0.0f, 0.0f),
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

            ulong signature = CreateSignature(resolution, new BoundingBox(_clipmapOrigin, _clipmapOrigin + new Vector3(cubicExtent)), _gpuInstances);
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
            EnsureBuffer(ref _bakeVoxelBuffer, ref _bakeVoxelBufferBytes, requiredBytes, "Far Field Clipmap Bake Voxels");
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

        internal static Vector3 ResolveSceneClampedOrigin(
            Vector3 sceneMin,
            Vector3 sceneMax,
            float extent,
            float voxelSize,
            Vector3 cameraPosition,
            Vector3 currentOrigin,
            ref bool hasCurrentOrigin,
            out bool recentered)
        {
            Vector3 desiredOrigin = ResolveDesiredSceneClampedOrigin(sceneMin, sceneMax, extent, voxelSize, cameraPosition);
            if (!hasCurrentOrigin)
            {
                hasCurrentOrigin = true;
                recentered = false;
                return desiredOrigin;
            }

            if (ApproximatelyEqual(currentOrigin, desiredOrigin) ||
                !ShouldRecenter(cameraPosition, currentOrigin, extent, sceneMin, sceneMax))
            {
                recentered = false;
                return currentOrigin;
            }

            recentered = !ApproximatelyEqual(currentOrigin, desiredOrigin);
            return desiredOrigin;
        }

        private static Vector3 ResolveDesiredSceneClampedOrigin(
            Vector3 sceneMin,
            Vector3 sceneMax,
            float extent,
            float voxelSize,
            Vector3 cameraPosition)
        {
            return new Vector3(
                ResolveDesiredSceneClampedAxisOrigin(sceneMin.X, sceneMax.X, extent, voxelSize, cameraPosition.X),
                ResolveDesiredSceneClampedAxisOrigin(sceneMin.Y, sceneMax.Y, extent, voxelSize, cameraPosition.Y),
                ResolveDesiredSceneClampedAxisOrigin(sceneMin.Z, sceneMax.Z, extent, voxelSize, cameraPosition.Z));
        }

        private static float ResolveDesiredSceneClampedAxisOrigin(float sceneMin, float sceneMax, float extent, float voxelSize, float cameraPosition)
        {
            float sceneExtent = Math.Max(sceneMax - sceneMin, 0.0f);
            if (sceneExtent <= extent)
                return sceneMin - Math.Max(extent - sceneExtent, 0.0f) * 0.5f;

            float maxOrigin = sceneMax - extent;
            if (maxOrigin < sceneMin)
                return sceneMin - Math.Max(extent - sceneExtent, 0.0f) * 0.5f;

            float target = SnapScalar(cameraPosition - extent * 0.5f, voxelSize);
            return Math.Clamp(target, sceneMin, maxOrigin);
        }

        private static bool ShouldRecenter(Vector3 cameraPosition, Vector3 currentOrigin, float extent, Vector3 sceneMin, Vector3 sceneMax)
        {
            Vector3 e = new(extent);
            Vector3 quarter = e * 0.25f;
            Vector3 innerMin = currentOrigin + quarter;
            Vector3 innerMax = currentOrigin + e - quarter;
            return
                ShouldRecenterAxis(cameraPosition.X, innerMin.X, innerMax.X, extent, sceneMin.X, sceneMax.X) ||
                ShouldRecenterAxis(cameraPosition.Y, innerMin.Y, innerMax.Y, extent, sceneMin.Y, sceneMax.Y) ||
                ShouldRecenterAxis(cameraPosition.Z, innerMin.Z, innerMax.Z, extent, sceneMin.Z, sceneMax.Z);
        }

        private static bool ShouldRecenterAxis(float cameraPosition, float innerMin, float innerMax, float extent, float sceneMin, float sceneMax)
        {
            float sceneExtent = Math.Max(sceneMax - sceneMin, 0.0f);
            return sceneExtent > extent && (cameraPosition < innerMin || cameraPosition > innerMax);
        }

        private static float SnapScalar(float value, float voxelSize)
        {
            float s = Math.Max(voxelSize, 0.001f);
            return MathF.Floor(value / s) * s;
        }

        private static bool ApproximatelyEqual(Vector3 left, Vector3 right)
        {
            const float epsilon = 0.0001f;
            return MathF.Abs(left.X - right.X) <= epsilon &&
                MathF.Abs(left.Y - right.Y) <= epsilon &&
                MathF.Abs(left.Z - right.Z) <= epsilon;
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
            if (_bakeVoxelBuffer.IsValid)
                _bufferManager.DestroyBuffer(_bakeVoxelBuffer);
            if (_instanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_instanceBuffer);
        }
    }
}
