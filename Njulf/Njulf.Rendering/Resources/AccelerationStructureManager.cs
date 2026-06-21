using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class AccelerationStructureManager : IDisposable
    {
        internal const byte StaticOpaqueInstanceMask = 0x01;
        private const ulong MinResourceBufferSize = 16;
        private const ulong IndexStride = sizeof(uint);
        private static readonly ulong VertexPositionStride = (ulong)Marshal.SizeOf<GPUVertexPositionStream>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly MeshManager _meshManager;
        private readonly MaterialManager _materialManager;
        private readonly KhrAccelerationStructure? _khrAccelerationStructure;
        private readonly Dictionary<MeshHandle, BottomLevelAccelerationStructure> _blasCache = new();
        private readonly List<StaticOpaqueInstance> _instanceScratch = new();
        private readonly List<AccelerationStructureInstanceKHR> _gpuInstanceScratch = new();

        private TopLevelAccelerationStructure _tlas;
        private BufferHandle _instanceBuffer;
        private ulong _instanceBufferSize;
        private BufferHandle _scratchBuffer;
        private ulong _scratchBufferSize;
        private BufferHandle _lastVertexPositionBuffer;
        private BufferHandle _lastIndexBuffer;
        private bool _disposed;
        private string _lastFallbackReason = string.Empty;
        private long _lastBuildMicroseconds;

        public AccelerationStructureManager(
            VulkanContext context,
            BufferManager bufferManager,
            MeshManager meshManager,
            MaterialManager materialManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _khrAccelerationStructure = context.KhrAccelerationStructure;
            if (!context.RayQuerySupported)
                _lastFallbackReason = "Ray-query and acceleration-structure features are not supported by the selected Vulkan device.";
            else if (_khrAccelerationStructure == null)
                _lastFallbackReason = "VK_KHR_acceleration_structure could not be loaded.";
        }

        public bool Supported => _context.RayQuerySupported && _khrAccelerationStructure != null;
        public bool Active => Supported && _tlas.Handle.Handle != 0 && TopLevelInstanceCount > 0 && string.IsNullOrEmpty(_lastFallbackReason);
        public AccelerationStructureKHR TopLevelAccelerationStructureHandle => _tlas.Handle;
        public int BottomLevelCount => _blasCache.Count;
        public int TopLevelInstanceCount { get; private set; }
        public ulong AccelerationStructureBytes { get; private set; }
        public ulong ScratchBufferBytes => _scratchBufferSize;
        public ulong InstanceBufferBytes => _instanceBufferSize;
        public ulong TotalBytes => AccelerationStructureBytes + ScratchBufferBytes + InstanceBufferBytes;
        public string LastFallbackReason => _lastFallbackReason;
        public long LastBuildMicroseconds => _lastBuildMicroseconds;

        public AccelerationStructureFrameStats PrepareFrame(
            Scene scene,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            bool enabled)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required to build acceleration structures.", nameof(commandBuffer));

            long buildStart = Stopwatch.GetTimestamp();
            TopLevelInstanceCount = 0;
            _lastBuildMicroseconds = 0;

            if (!enabled)
            {
                _lastFallbackReason = string.Empty;
                return CreateStats(false);
            }

            if (!Supported)
                return CreateStats(false);

            try
            {
                InvalidateCachedStructuresIfMeshBuffersChanged();
                CollectStaticOpaqueInstances(scene, _instanceScratch);
                if (_instanceScratch.Count == 0)
                {
                    _lastFallbackReason = "No static opaque acceleration-structure instances were submitted.";
                    return CreateStats(false);
                }

                EnsureBottomLevelAccelerationStructures(_instanceScratch, commandBuffer);
                BuildTopLevelAccelerationStructure(_instanceScratch, stagingRing, commandBuffer);
                _lastFallbackReason = string.Empty;
                _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                return CreateStats(Active);
            }
            catch (Exception ex) when (ex is VulkanException or InvalidOperationException or ArgumentException or OverflowException)
            {
                _lastFallbackReason = ex.Message;
                TopLevelInstanceCount = 0;
                _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                return CreateStats(false);
            }
        }

        internal void CollectStaticOpaqueInstances(Scene scene, List<StaticOpaqueInstance> instances)
        {
            instances.Clear();

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!renderObject.Visible || !renderObject.Enabled)
                    continue;
                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;
                if (!TryGetStaticOpaqueMesh(meshHandle, renderObject.Material, renderObject.Name, out MeshInfo meshInfo))
                    continue;

                instances.Add(new StaticOpaqueInstance(meshHandle, meshInfo, renderObject.WorldMatrix));
            }

            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
            {
                if (!batch.Visible)
                    continue;
                if (batch.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;
                if (!TryGetStaticOpaqueMesh(meshHandle, batch.Material, batch.Name, out MeshInfo meshInfo))
                    continue;

                IReadOnlyList<CoreMatrix4x4> worldMatrices = batch.WorldMatrices;
                for (int i = 0; i < worldMatrices.Count; i++)
                    instances.Add(new StaticOpaqueInstance(meshHandle, meshInfo, worldMatrices[i]));
            }
        }

        private bool TryGetStaticOpaqueMesh(
            MeshHandle meshHandle,
            object? material,
            string? ownerName,
            out MeshInfo meshInfo)
        {
            meshInfo = default;
            try
            {
                meshInfo = _meshManager.GetMeshInfo(meshHandle);
                if (meshInfo.IsSkinned || meshInfo.VertexCount == 0 || meshInfo.IndexCount < 3)
                    return false;

                MaterialHandle materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    material,
                    _materialManager.DefaultMaterialHandle,
                    ownerName ?? string.Empty);
                MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
                return metadata.RenderMode == MaterialRenderMode.Opaque && !metadata.IsGeometryDecal;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void EnsureBottomLevelAccelerationStructures(
            IReadOnlyList<StaticOpaqueInstance> instances,
            CommandBuffer commandBuffer)
        {
            foreach (StaticOpaqueInstance instance in instances)
            {
                if (_blasCache.ContainsKey(instance.Mesh))
                    continue;

                BottomLevelAccelerationStructure blas = BuildBottomLevelAccelerationStructure(instance.Mesh, instance.MeshInfo, commandBuffer);
                _blasCache.Add(instance.Mesh, blas);
                AccelerationStructureBytes = checked(AccelerationStructureBytes + blas.Size);
                InsertAccelerationStructureBuildBarrier(commandBuffer);
            }
        }

        private void InvalidateCachedStructuresIfMeshBuffersChanged()
        {
            BufferHandle vertexPositionBuffer = _meshManager.VertexPositionBuffer;
            BufferHandle indexBuffer = _meshManager.IndexBuffer;
            if (_lastVertexPositionBuffer == vertexPositionBuffer && _lastIndexBuffer == indexBuffer)
                return;

            bool hasAccelerationStructures = _tlas.Handle.Handle != 0 || _blasCache.Count > 0;
            DestroyTopLevelAccelerationStructure(waitIdle: hasAccelerationStructures);
            DestroyBottomLevelAccelerationStructures(waitIdle: false);
            RecalculateAccelerationStructureBytes();
            _lastVertexPositionBuffer = vertexPositionBuffer;
            _lastIndexBuffer = indexBuffer;
        }

        private BottomLevelAccelerationStructure BuildBottomLevelAccelerationStructure(
            MeshHandle meshHandle,
            MeshInfo meshInfo,
            CommandBuffer commandBuffer)
        {
            uint primitiveCount = meshInfo.IndexCount / 3u;
            if (primitiveCount == 0)
                throw new InvalidOperationException($"Mesh {meshHandle.Index} does not contain triangle primitives for BLAS build.");

            AccelerationStructureGeometryKHR geometry = CreateBottomLevelGeometry(meshInfo);
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateBottomLevelBuildInfo(&geometry, default, default);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);

            BufferHandle storageBuffer = _bufferManager.CreateDeviceBuffer(
                Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize),
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                $"BLAS Mesh {meshHandle.Index}");

            AccelerationStructureKHR accelerationStructure = CreateAccelerationStructure(
                storageBuffer,
                sizes.AccelerationStructureSize,
                AccelerationStructureTypeKHR.BottomLevelKhr,
                $"BLAS Mesh {meshHandle.Index}");

            EnsureScratchCapacity(sizes.BuildScratchSize);
            geometry = CreateBottomLevelGeometry(meshInfo);
            buildInfo = CreateBottomLevelBuildInfo(
                &geometry,
                accelerationStructure,
                _bufferManager.GetBufferDeviceAddress(_scratchBuffer));

            var range = new AccelerationStructureBuildRangeInfoKHR
            {
                PrimitiveCount = primitiveCount,
                PrimitiveOffset = 0,
                FirstVertex = 0,
                TransformOffset = 0
            };
            AccelerationStructureBuildRangeInfoKHR* rangePtr = &range;
            _khrAccelerationStructure!.CmdBuildAccelerationStructures(commandBuffer, 1, &buildInfo, &rangePtr);

            return new BottomLevelAccelerationStructure(accelerationStructure, storageBuffer, sizes.AccelerationStructureSize);
        }

        private AccelerationStructureGeometryKHR CreateBottomLevelGeometry(MeshInfo meshInfo)
        {
            ulong vertexAddress = checked(_bufferManager.GetBufferDeviceAddress(_meshManager.VertexPositionBuffer) +
                (ulong)meshInfo.VertexOffset * VertexPositionStride);
            ulong indexAddress = checked(_bufferManager.GetBufferDeviceAddress(_meshManager.IndexBuffer) +
                (ulong)meshInfo.IndexOffset * IndexStride);

            var triangles = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress },
                VertexStride = VertexPositionStride,
                MaxVertex = meshInfo.VertexCount - 1u,
                IndexType = IndexType.Uint32,
                IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = indexAddress },
                TransformData = default
            };

            return new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Triangles = triangles },
                Flags = GeometryFlagsKHR.OpaqueBitKhr
            };
        }

        private static AccelerationStructureBuildGeometryInfoKHR CreateBottomLevelBuildInfo(
            AccelerationStructureGeometryKHR* geometry,
            AccelerationStructureKHR destination,
            ulong scratchAddress)
        {
            return new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                DstAccelerationStructure = destination,
                GeometryCount = 1,
                PGeometries = geometry,
                ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddress }
            };
        }

        private void BuildTopLevelAccelerationStructure(
            IReadOnlyList<StaticOpaqueInstance> instances,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            _gpuInstanceScratch.Clear();
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                BottomLevelAccelerationStructure blas = _blasCache[instance.Mesh];
                ulong blasAddress = GetAccelerationStructureDeviceAddress(blas.Handle);
                _gpuInstanceScratch.Add(CreateInstance(instance.WorldMatrix, blasAddress, (uint)i, StaticOpaqueInstanceMask));
            }

            EnsureInstanceBufferCapacity(_gpuInstanceScratch.Count);
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _instanceBuffer,
                CollectionsMarshal.AsSpan(_gpuInstanceScratch),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                    AccessFlags2.AccelerationStructureReadBitKhr));

            uint primitiveCount = (uint)_gpuInstanceScratch.Count;
            AccelerationStructureGeometryKHR geometry = CreateTopLevelGeometry();
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateTopLevelBuildInfo(&geometry, default, default);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);
            EnsureTopLevelAccelerationStructure(sizes.AccelerationStructureSize);
            EnsureScratchCapacity(sizes.BuildScratchSize);

            geometry = CreateTopLevelGeometry();
            buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                _tlas.Handle,
                _bufferManager.GetBufferDeviceAddress(_scratchBuffer));
            var range = new AccelerationStructureBuildRangeInfoKHR
            {
                PrimitiveCount = primitiveCount,
                PrimitiveOffset = 0,
                FirstVertex = 0,
                TransformOffset = 0
            };
            AccelerationStructureBuildRangeInfoKHR* rangePtr = &range;
            _khrAccelerationStructure!.CmdBuildAccelerationStructures(commandBuffer, 1, &buildInfo, &rangePtr);
            InsertAccelerationStructureBuildBarrier(commandBuffer);
            TopLevelInstanceCount = _gpuInstanceScratch.Count;
        }

        private AccelerationStructureGeometryKHR CreateTopLevelGeometry()
        {
            ulong instanceAddress = _instanceBuffer.IsValid ? _bufferManager.GetBufferDeviceAddress(_instanceBuffer) : 0;
            var instances = new AccelerationStructureGeometryInstancesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                ArrayOfPointers = false,
                Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instanceAddress }
            };

            return new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.InstancesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Instances = instances },
                Flags = GeometryFlagsKHR.OpaqueBitKhr
            };
        }

        private static AccelerationStructureBuildGeometryInfoKHR CreateTopLevelBuildInfo(
            AccelerationStructureGeometryKHR* geometry,
            AccelerationStructureKHR destination,
            ulong scratchAddress)
        {
            return new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                DstAccelerationStructure = destination,
                GeometryCount = 1,
                PGeometries = geometry,
                ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddress }
            };
        }

        private AccelerationStructureBuildSizesInfoKHR QueryBuildSizes(
            AccelerationStructureBuildGeometryInfoKHR buildInfo,
            uint primitiveCount)
        {
            var sizes = new AccelerationStructureBuildSizesInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildSizesInfoKhr
            };
            _khrAccelerationStructure!.GetAccelerationStructureBuildSizes(
                _context.Device,
                AccelerationStructureBuildTypeKHR.DeviceKhr,
                &buildInfo,
                &primitiveCount,
                &sizes);
            return sizes;
        }

        private AccelerationStructureKHR CreateAccelerationStructure(
            BufferHandle storageBuffer,
            ulong size,
            AccelerationStructureTypeKHR type,
            string debugName)
        {
            VkBuffer buffer = _bufferManager.GetBuffer(storageBuffer);
            var createInfo = new AccelerationStructureCreateInfoKHR
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = buffer,
                Size = size,
                Type = type
            };

            Result result = _khrAccelerationStructure!.CreateAccelerationStructure(
                _context.Device,
                &createInfo,
                null,
                out AccelerationStructureKHR accelerationStructure);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {debugName}.", result);

            _context.SetDebugName(accelerationStructure.Handle, ObjectType.AccelerationStructureKhr, debugName);
            return accelerationStructure;
        }

        private ulong GetAccelerationStructureDeviceAddress(AccelerationStructureKHR accelerationStructure)
        {
            var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
            {
                SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
                AccelerationStructure = accelerationStructure
            };
            return _khrAccelerationStructure!.GetAccelerationStructureDeviceAddress(_context.Device, &addressInfo);
        }

        private void EnsureTopLevelAccelerationStructure(ulong requiredSize)
        {
            requiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (_tlas.Handle.Handle != 0 && _tlas.Size >= requiredSize)
                return;

            DestroyTopLevelAccelerationStructure(waitIdle: _tlas.Handle.Handle != 0);
            BufferHandle storageBuffer = _bufferManager.CreateDeviceBuffer(
                requiredSize,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "Top Level Acceleration Structure");
            AccelerationStructureKHR tlas = CreateAccelerationStructure(
                storageBuffer,
                requiredSize,
                AccelerationStructureTypeKHR.TopLevelKhr,
                "Top Level Acceleration Structure");
            _tlas = new TopLevelAccelerationStructure(tlas, storageBuffer, requiredSize);
            RecalculateAccelerationStructureBytes();
        }

        private void EnsureScratchCapacity(ulong requiredSize)
        {
            requiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (_scratchBuffer.IsValid && _scratchBufferSize >= requiredSize)
                return;

            if (_scratchBuffer.IsValid)
            {
                _context.WaitIdle();
                _bufferManager.DestroyBuffer(_scratchBuffer);
            }

            _scratchBufferSize = requiredSize;
            _scratchBuffer = _bufferManager.CreateDeviceBuffer(
                _scratchBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "Acceleration Structure Scratch Buffer");
        }

        private void EnsureInstanceBufferCapacity(int instanceCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Max(0, instanceCount) * (ulong)sizeof(AccelerationStructureInstanceKHR)));
            if (_instanceBuffer.IsValid && _instanceBufferSize >= requiredSize)
                return;

            if (_instanceBuffer.IsValid)
            {
                _context.WaitIdle();
                _bufferManager.DestroyBuffer(_instanceBuffer);
            }

            _instanceBufferSize = requiredSize;
            _instanceBuffer = _bufferManager.CreateDeviceBuffer(
                _instanceBufferSize,
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "TLAS Instance Buffer");
        }

        private void InsertAccelerationStructureBuildBarrier(CommandBuffer commandBuffer)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                SrcAccessMask = AccessFlags2.AccelerationStructureWriteBitKhr,
                DstStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr | AccessFlags2.ShaderReadBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        internal static AccelerationStructureInstanceKHR CreateInstance(
            CoreMatrix4x4 worldMatrix,
            ulong blasAddress,
            uint instanceCustomIndex,
            byte mask)
        {
            return new AccelerationStructureInstanceKHR
            {
                Transform = CreateTransform(worldMatrix),
                InstanceCustomIndex = instanceCustomIndex & 0x00FF_FFFFu,
                Mask = mask,
                InstanceShaderBindingTableRecordOffset = 0,
                Flags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr,
                AccelerationStructureReference = blasAddress
            };
        }

        internal static TransformMatrixKHR CreateTransform(CoreMatrix4x4 matrix)
        {
            TransformMatrixKHR transform = default;
            float* values = transform.Matrix;
            values[0] = matrix.M11;
            values[1] = matrix.M21;
            values[2] = matrix.M31;
            values[3] = matrix.M41;
            values[4] = matrix.M12;
            values[5] = matrix.M22;
            values[6] = matrix.M32;
            values[7] = matrix.M42;
            values[8] = matrix.M13;
            values[9] = matrix.M23;
            values[10] = matrix.M33;
            values[11] = matrix.M43;

            return transform;
        }

        private AccelerationStructureFrameStats CreateStats(bool active)
        {
            return new AccelerationStructureFrameStats(
                Supported,
                active,
                BottomLevelCount,
                TopLevelInstanceCount,
                AccelerationStructureBytes,
                ScratchBufferBytes,
                InstanceBufferBytes,
                _lastBuildMicroseconds,
                _lastFallbackReason);
        }

        private void RecalculateAccelerationStructureBytes()
        {
            ulong bytes = _tlas.Size;
            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
                bytes = checked(bytes + blas.Size);
            AccelerationStructureBytes = bytes;
        }

        private void DestroyTopLevelAccelerationStructure(bool waitIdle)
        {
            if (_tlas.Handle.Handle == 0)
                return;

            if (waitIdle)
                _context.WaitIdle();

            _khrAccelerationStructure?.DestroyAccelerationStructure(_context.Device, _tlas.Handle, null);
            if (_tlas.StorageBuffer.IsValid)
                _bufferManager.DestroyBuffer(_tlas.StorageBuffer);
            _tlas = default;
        }

        private void DestroyBottomLevelAccelerationStructures(bool waitIdle)
        {
            if (_blasCache.Count == 0)
                return;

            if (waitIdle)
                _context.WaitIdle();

            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
            {
                _khrAccelerationStructure?.DestroyAccelerationStructure(_context.Device, blas.Handle, null);
                if (blas.StorageBuffer.IsValid)
                    _bufferManager.DestroyBuffer(blas.StorageBuffer);
            }
            _blasCache.Clear();
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DestroyTopLevelAccelerationStructure(waitIdle: false);
            DestroyBottomLevelAccelerationStructures(waitIdle: false);
            if (_scratchBuffer.IsValid)
                _bufferManager.DestroyBuffer(_scratchBuffer);
            if (_instanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_instanceBuffer);
        }

        internal readonly record struct StaticOpaqueInstance(
            MeshHandle Mesh,
            MeshInfo MeshInfo,
            CoreMatrix4x4 WorldMatrix);

        private readonly record struct BottomLevelAccelerationStructure(
            AccelerationStructureKHR Handle,
            BufferHandle StorageBuffer,
            ulong Size);

        private readonly record struct TopLevelAccelerationStructure(
            AccelerationStructureKHR Handle,
            BufferHandle StorageBuffer,
            ulong Size);
    }

    public readonly record struct AccelerationStructureFrameStats(
        bool Supported,
        bool Active,
        int BottomLevelCount,
        int TopLevelInstanceCount,
        ulong AccelerationStructureBytes,
        ulong ScratchBufferBytes,
        ulong InstanceBufferBytes,
        long BuildMicroseconds,
        string FallbackReason);
}
