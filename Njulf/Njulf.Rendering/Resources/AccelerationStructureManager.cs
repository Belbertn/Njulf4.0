using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
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
        private const ulong HashStart = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        private static readonly ulong VertexPositionStride = (ulong)Marshal.SizeOf<GPUVertexPositionStream>();
        private static readonly ulong RayQueryInstanceMetadataStride = (ulong)Marshal.SizeOf<GPUDdgiRayQueryInstance>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly MeshManager _meshManager;
        private readonly MaterialManager _materialManager;
        private readonly KhrAccelerationStructure? _khrAccelerationStructure;
        private readonly Dictionary<MeshHandle, BottomLevelAccelerationStructure> _blasCache = new();
        private readonly List<StaticOpaqueInstance> _instanceScratch = new();
        private readonly List<AccelerationStructureInstanceKHR> _gpuInstanceScratch = new();
        private readonly List<GPUDdgiRayQueryInstance> _rayQueryInstanceScratch = new();
        private readonly List<RetiredAccelerationStructureResource> _retiredAccelerationStructures = new();
        private readonly List<RetiredBufferResource> _retiredBuffers = new();

        private TopLevelAccelerationStructure _tlas;
        private BufferHandle _instanceBuffer;
        private ulong _instanceBufferSize;
        private BufferHandle _rayQueryInstanceBuffer;
        private ulong _rayQueryInstanceBufferSize;
        private BufferHandle _scratchBuffer;
        private ulong _scratchBufferSize;
        private BufferHandle _lastVertexPositionBuffer;
        private BufferHandle _lastIndexBuffer;
        private bool _disposed;
        private BindlessHeap? _registeredBindlessHeap;
        private string _lastFallbackReason = string.Empty;
        private long _lastBuildMicroseconds;
        private long _lastBlasBuildMicroseconds;
        private long _lastTlasBuildMicroseconds;
        private long _lastInstanceUploadMicroseconds;
        private int _lastBlasBuildCount;
        private int _lastTlasBuildCount;
        private int _lastTlasUpdateCount;
        private int _lastTlasSkipCount;
        private ulong _lastInstanceUploadBytes;
        private ulong _lastRayQueryInstanceMetadataUploadBytes;
        private ulong _lastTlasInstanceSignature;
        private bool _hasTlasInstanceSignature;
        private int _lastTlasInstanceCount;
        private ulong _frameSerial;

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
            EnsureRayQueryInstanceMetadataCapacity(0);
        }

        public bool Supported => _context.RayQuerySupported && _khrAccelerationStructure != null;
        public bool Active => Supported && _tlas.Handle.Handle != 0 && TopLevelInstanceCount > 0 && string.IsNullOrEmpty(_lastFallbackReason);
        public AccelerationStructureKHR TopLevelAccelerationStructureHandle => _tlas.Handle;
        public int BottomLevelCount => _blasCache.Count;
        public int TopLevelInstanceCount { get; private set; }
        public ulong AccelerationStructureBytes { get; private set; }
        public ulong ScratchBufferBytes => _scratchBufferSize;
        public ulong InstanceBufferBytes => _instanceBufferSize;
        public ulong RayQueryInstanceMetadataBufferBytes => _rayQueryInstanceBufferSize;
        public ulong TotalBytes => AccelerationStructureBytes + ScratchBufferBytes + InstanceBufferBytes + RayQueryInstanceMetadataBufferBytes;
        public string LastFallbackReason => _lastFallbackReason;
        public long LastBuildMicroseconds => _lastBuildMicroseconds;

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            RegisterRayQueryInstanceMetadataBuffer();
        }

        public AccelerationStructureFrameStats PrepareFrame(
            Scene scene,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            bool enabled,
            GpuTimestampRecorder? gpuTimestamps = null,
            int frameIndex = 0)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required to build acceleration structures.", nameof(commandBuffer));

            long buildStart = Stopwatch.GetTimestamp();
            TopLevelInstanceCount = 0;
            ResetFrameDiagnostics();
            BeginFrameResourceRetirement();

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

                bool missingBlas = HasMissingBottomLevelAccelerationStructures(_instanceScratch);
                if (missingBlas)
                    gpuTimestamps?.BeginPass(commandBuffer, frameIndex, "AccelerationStructureBlasPass");
                try
                {
                    EnsureBottomLevelAccelerationStructures(_instanceScratch, commandBuffer);
                }
                finally
                {
                    if (missingBlas)
                        gpuTimestamps?.EndPass(commandBuffer, frameIndex);
                }

                ulong instanceSignature = CreateInstanceSignature(_instanceScratch);
                TopLevelAccelerationStructureBuildAction buildAction = SelectTopLevelBuildAction(
                    _tlas.Handle.Handle != 0,
                    _hasTlasInstanceSignature,
                    _lastTlasInstanceCount,
                    _lastTlasInstanceSignature,
                    _instanceScratch.Count,
                    instanceSignature);
                if (buildAction == TopLevelAccelerationStructureBuildAction.Skip)
                {
                    TopLevelInstanceCount = _instanceScratch.Count;
                    _lastTlasSkipCount = 1;
                }
                else
                {
                    gpuTimestamps?.BeginPass(commandBuffer, frameIndex, "AccelerationStructureTlasPass");
                    try
                    {
                        BuildTopLevelAccelerationStructure(_instanceScratch, stagingRing, commandBuffer, buildAction, instanceSignature);
                    }
                    finally
                    {
                        gpuTimestamps?.EndPass(commandBuffer, frameIndex);
                    }
                }

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
                if (!TryGetRayQueryMesh(
                    meshHandle,
                    renderObject.Material,
                    renderObject.Name,
                    AccelerationStructureGeometryDomain.Dynamic,
                    out MeshInfo meshInfo,
                    out uint materialIndex))
                    continue;

                instances.Add(new StaticOpaqueInstance(meshHandle, meshInfo, materialIndex, renderObject.WorldMatrix));
            }

            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
            {
                if (!batch.Visible)
                    continue;
                if (batch.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;
                if (!TryGetRayQueryMesh(
                    meshHandle,
                    batch.Material,
                    batch.Name,
                    AccelerationStructureGeometryDomain.Static,
                    out MeshInfo meshInfo,
                    out uint materialIndex))
                    continue;

                IReadOnlyList<CoreMatrix4x4> worldMatrices = batch.WorldMatrices;
                for (int i = 0; i < worldMatrices.Count; i++)
                    instances.Add(new StaticOpaqueInstance(meshHandle, meshInfo, materialIndex, worldMatrices[i]));
            }
        }

        private bool TryGetRayQueryMesh(
            MeshHandle meshHandle,
            object? material,
            string? ownerName,
            AccelerationStructureGeometryDomain domain,
            out MeshInfo meshInfo,
            out uint materialIndex)
        {
            meshInfo = default;
            materialIndex = 0;
            try
            {
                meshInfo = _meshManager.GetMeshInfo(meshHandle);
                if (meshInfo.VertexCount == 0 || meshInfo.IndexCount < 3)
                    return false;

                MaterialHandle materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    material,
                    _materialManager.DefaultMaterialHandle,
                    ownerName ?? string.Empty);
                MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
                DdgiAccelerationStructureGeometryPolicy policy = ResolveGeometryPolicy(
                    meshInfo.IsSkinned,
                    metadata.RenderMode,
                    metadata.IsGeometryDecal,
                    domain);
                if (!policy.Include)
                    return false;

                materialIndex = checked((uint)Math.Max(materialHandle.Index, 0));
                return true;
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

                long blasStart = Stopwatch.GetTimestamp();
                BottomLevelAccelerationStructure blas = BuildBottomLevelAccelerationStructure(instance.Mesh, instance.MeshInfo, commandBuffer);
                _lastBlasBuildMicroseconds += ElapsedMicroseconds(blasStart);
                _lastBlasBuildCount++;
                _blasCache.Add(instance.Mesh, blas);
                AccelerationStructureBytes = checked(AccelerationStructureBytes + blas.Size);
                InsertAccelerationStructureBuildBarrier(commandBuffer);
            }
        }

        private bool HasMissingBottomLevelAccelerationStructures(IReadOnlyList<StaticOpaqueInstance> instances)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                if (!_blasCache.ContainsKey(instances[i].Mesh))
                    return true;
            }

            return false;
        }

        private void InvalidateCachedStructuresIfMeshBuffersChanged()
        {
            BufferHandle vertexPositionBuffer = _meshManager.VertexPositionBuffer;
            BufferHandle indexBuffer = _meshManager.IndexBuffer;
            if (_lastVertexPositionBuffer == vertexPositionBuffer && _lastIndexBuffer == indexBuffer)
                return;

            DestroyTopLevelAccelerationStructure(defer: true);
            DestroyBottomLevelAccelerationStructures(defer: true);
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
            CommandBuffer commandBuffer,
            TopLevelAccelerationStructureBuildAction requestedAction,
            ulong instanceSignature)
        {
            long tlasStart = Stopwatch.GetTimestamp();
            _gpuInstanceScratch.Clear();
            _rayQueryInstanceScratch.Clear();
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                BottomLevelAccelerationStructure blas = _blasCache[instance.Mesh];
                ulong blasAddress = GetAccelerationStructureDeviceAddress(blas.Handle);
                _gpuInstanceScratch.Add(CreateInstance(instance.WorldMatrix, blasAddress, (uint)i, StaticOpaqueInstanceMask));
                _rayQueryInstanceScratch.Add(CreateRayQueryInstanceMetadata(instance));
            }

            EnsureInstanceBufferCapacity(_gpuInstanceScratch.Count);
            EnsureRayQueryInstanceMetadataCapacity(_rayQueryInstanceScratch.Count);
            _lastInstanceUploadBytes = checked((ulong)_gpuInstanceScratch.Count * (ulong)sizeof(AccelerationStructureInstanceKHR));
            _lastRayQueryInstanceMetadataUploadBytes = checked((ulong)_rayQueryInstanceScratch.Count * RayQueryInstanceMetadataStride);
            long uploadStart = Stopwatch.GetTimestamp();
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
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _rayQueryInstanceBuffer,
                CollectionsMarshal.AsSpan(_rayQueryInstanceScratch),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _lastInstanceUploadMicroseconds = ElapsedMicroseconds(uploadStart);

            uint primitiveCount = (uint)_gpuInstanceScratch.Count;
            AccelerationStructureGeometryKHR geometry = CreateTopLevelGeometry();
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                default,
                default,
                default,
                BuildAccelerationStructureModeKHR.BuildKhr);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);
            bool tlasRecreated = EnsureTopLevelAccelerationStructure(sizes.AccelerationStructureSize);
            bool useUpdate = requestedAction == TopLevelAccelerationStructureBuildAction.Update && !tlasRecreated;
            ulong scratchSize = useUpdate && sizes.UpdateScratchSize > 0 ? sizes.UpdateScratchSize : sizes.BuildScratchSize;
            EnsureScratchCapacity(scratchSize);

            geometry = CreateTopLevelGeometry();
            AccelerationStructureKHR source = useUpdate ? _tlas.Handle : default;
            buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                _tlas.Handle,
                source,
                _bufferManager.GetBufferDeviceAddress(_scratchBuffer),
                useUpdate ? BuildAccelerationStructureModeKHR.UpdateKhr : BuildAccelerationStructureModeKHR.BuildKhr);
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
            if (useUpdate)
                _lastTlasUpdateCount = 1;
            else
                _lastTlasBuildCount = 1;
            _lastTlasInstanceSignature = instanceSignature;
            _hasTlasInstanceSignature = true;
            _lastTlasInstanceCount = _gpuInstanceScratch.Count;
            _lastTlasBuildMicroseconds = ElapsedMicroseconds(tlasStart);
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
            AccelerationStructureKHR source,
            ulong scratchAddress,
            BuildAccelerationStructureModeKHR mode)
        {
            return new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr | BuildAccelerationStructureFlagsKHR.AllowUpdateBitKhr,
                Mode = mode,
                SrcAccelerationStructure = source,
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

        private bool EnsureTopLevelAccelerationStructure(ulong requiredSize)
        {
            requiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (_tlas.Handle.Handle != 0 && _tlas.Size >= requiredSize)
                return false;

            DestroyTopLevelAccelerationStructure(defer: true);
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
            return true;
        }

        private void EnsureScratchCapacity(ulong requiredSize)
        {
            requiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (_scratchBuffer.IsValid && _scratchBufferSize >= requiredSize)
                return;

            if (_scratchBuffer.IsValid)
                RetireBufferResource(_scratchBuffer);

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
                RetireBufferResource(_instanceBuffer);

            _instanceBufferSize = requiredSize;
            _instanceBuffer = _bufferManager.CreateDeviceBuffer(
                _instanceBufferSize,
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "TLAS Instance Buffer");
        }

        private void EnsureRayQueryInstanceMetadataCapacity(int instanceCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Max(0, instanceCount) * RayQueryInstanceMetadataStride));
            if (_rayQueryInstanceBuffer.IsValid && _rayQueryInstanceBufferSize >= requiredSize)
                return;

            if (_rayQueryInstanceBuffer.IsValid)
                RetireBufferResource(_rayQueryInstanceBuffer);

            _rayQueryInstanceBufferSize = requiredSize;
            _rayQueryInstanceBuffer = _bufferManager.CreateDeviceBuffer(
                _rayQueryInstanceBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "DDGI Ray Query Instance Metadata Buffer");
            RegisterRayQueryInstanceMetadataBuffer();
        }

        private void RegisterRayQueryInstanceMetadataBuffer()
        {
            if (_registeredBindlessHeap == null || !_rayQueryInstanceBuffer.IsValid)
                return;

            _registeredBindlessHeap.RegisterStorageBuffer(
                BindlessIndex.DdgiRayQueryInstanceBuffer,
                _bufferManager.GetBuffer(_rayQueryInstanceBuffer),
                0,
                _rayQueryInstanceBufferSize);
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

        internal static GPUDdgiRayQueryInstance CreateRayQueryInstanceMetadata(StaticOpaqueInstance instance)
        {
            return new GPUDdgiRayQueryInstance
            {
                VertexOffset = instance.MeshInfo.VertexOffset,
                IndexOffset = instance.MeshInfo.IndexOffset,
                MaterialIndex = instance.MaterialIndex,
                Padding0 = 0,
                WorldMatrixInverseTranspose = instance.WorldMatrix.Invert().Transpose()
            };
        }

        internal static DdgiAccelerationStructureGeometryPolicy ResolveGeometryPolicy(
            bool isSkinned,
            MaterialRenderMode renderMode,
            bool isGeometryDecal,
            AccelerationStructureGeometryDomain domain)
        {
            if (isGeometryDecal)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    false,
                    0,
                    default,
                    DdgiAccelerationStructureVisibilityPolicy.ExcludedGeometryDecal,
                    "geometry decals are excluded from DDGI ray-query visibility");
            }

            if (domain == AccelerationStructureGeometryDomain.Foliage)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    false,
                    0,
                    default,
                    DdgiAccelerationStructureVisibilityPolicy.FoliageProxyPending,
                    "foliage uses clustered alpha geometry and needs explicit DDGI proxy cards/clusters");
            }

            if (renderMode == MaterialRenderMode.Blend)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    false,
                    0,
                    default,
                    DdgiAccelerationStructureVisibilityPolicy.ExcludedTransparent,
                    "transparent blended materials are excluded from DDGI ray-query occlusion");
            }

            if (isSkinned || domain == AccelerationStructureGeometryDomain.Skinned)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    GeometryInstanceFlagsKHR.ForceOpaqueBitKhr,
                    DdgiAccelerationStructureVisibilityPolicy.SkinnedBindPoseProxy,
                    "skinned meshes contribute a bind-pose triangle proxy until animated proxy geometry is available");
            }

            if (renderMode == MaterialRenderMode.Mask)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    GeometryInstanceFlagsKHR.ForceOpaqueBitKhr,
                    DdgiAccelerationStructureVisibilityPolicy.AlphaMaskApproximateOpaque,
                    "alpha-masked geometry is approximated as opaque for stable DDGI visibility");
            }

            return new DdgiAccelerationStructureGeometryPolicy(
                true,
                StaticOpaqueInstanceMask,
                GeometryInstanceFlagsKHR.ForceOpaqueBitKhr,
                DdgiAccelerationStructureVisibilityPolicy.OpaqueTriangles,
                domain == AccelerationStructureGeometryDomain.Dynamic
                    ? "dynamic opaque geometry participates with TLAS updates"
                    : "static opaque geometry participates with cached BLAS/TLAS");
        }

        internal static TopLevelAccelerationStructureBuildAction SelectTopLevelBuildAction(
            bool hasTopLevelAccelerationStructure,
            bool hasPreviousSignature,
            int previousInstanceCount,
            ulong previousSignature,
            int currentInstanceCount,
            ulong currentSignature)
        {
            if (!hasTopLevelAccelerationStructure || !hasPreviousSignature)
                return TopLevelAccelerationStructureBuildAction.Build;

            if (previousInstanceCount == currentInstanceCount && previousSignature == currentSignature)
                return TopLevelAccelerationStructureBuildAction.Skip;

            if (previousInstanceCount == currentInstanceCount)
                return TopLevelAccelerationStructureBuildAction.Update;

            return TopLevelAccelerationStructureBuildAction.Build;
        }

        internal static ulong CreateInstanceSignature(IReadOnlyList<StaticOpaqueInstance> instances)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, instances.Count);
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                hash = HashAdd(hash, instance.Mesh.Index);
                hash = HashAdd(hash, instance.Mesh.Generation);
                hash = HashAdd(hash, instance.MeshInfo.VertexOffset);
                hash = HashAdd(hash, instance.MeshInfo.IndexOffset);
                hash = HashAdd(hash, instance.MeshInfo.VertexCount);
                hash = HashAdd(hash, instance.MeshInfo.IndexCount);
                hash = HashAdd(hash, instance.MaterialIndex);
                hash = HashAdd(hash, instance.WorldMatrix);
            }

            return hash;
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

        private static ulong HashAdd(ulong hash, CoreMatrix4x4 matrix)
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
                RayQueryInstanceMetadataBufferBytes,
                _lastBuildMicroseconds,
                _lastBlasBuildMicroseconds,
                _lastTlasBuildMicroseconds,
                _lastInstanceUploadMicroseconds,
                _lastBlasBuildCount,
                _lastTlasBuildCount,
                _lastTlasUpdateCount,
                _lastTlasSkipCount,
                _lastInstanceUploadBytes,
                _lastRayQueryInstanceMetadataUploadBytes,
                _lastFallbackReason);
        }

        private void ResetFrameDiagnostics()
        {
            _lastBuildMicroseconds = 0;
            _lastBlasBuildMicroseconds = 0;
            _lastTlasBuildMicroseconds = 0;
            _lastInstanceUploadMicroseconds = 0;
            _lastBlasBuildCount = 0;
            _lastTlasBuildCount = 0;
            _lastTlasUpdateCount = 0;
            _lastTlasSkipCount = 0;
            _lastInstanceUploadBytes = 0;
            _lastRayQueryInstanceMetadataUploadBytes = 0;
        }

        private void RecalculateAccelerationStructureBytes()
        {
            ulong bytes = _tlas.Size;
            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
                bytes = checked(bytes + blas.Size);
            AccelerationStructureBytes = bytes;
        }

        private void DestroyTopLevelAccelerationStructure(bool defer)
        {
            if (_tlas.Handle.Handle == 0)
                return;

            if (defer)
                RetireAccelerationStructureResource(_tlas.Handle, _tlas.StorageBuffer);
            else
                DestroyAccelerationStructureResource(_tlas.Handle, _tlas.StorageBuffer);
            _tlas = default;
            _hasTlasInstanceSignature = false;
            _lastTlasInstanceSignature = 0;
            _lastTlasInstanceCount = 0;
        }

        private void DestroyBottomLevelAccelerationStructures(bool defer)
        {
            if (_blasCache.Count == 0)
                return;

            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
            {
                if (defer)
                    RetireAccelerationStructureResource(blas.Handle, blas.StorageBuffer);
                else
                    DestroyAccelerationStructureResource(blas.Handle, blas.StorageBuffer);
            }
            _blasCache.Clear();
        }

        private void BeginFrameResourceRetirement()
        {
            _frameSerial++;
            DrainRetiredResources(force: false);
        }

        private void RetireAccelerationStructureResource(AccelerationStructureKHR accelerationStructure, BufferHandle storageBuffer)
        {
            _retiredAccelerationStructures.Add(new RetiredAccelerationStructureResource(
                accelerationStructure,
                storageBuffer,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL));
        }

        private void RetireBufferResource(BufferHandle buffer)
        {
            _retiredBuffers.Add(new RetiredBufferResource(
                buffer,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL));
        }

        private void DrainRetiredResources(bool force)
        {
            for (int i = _retiredAccelerationStructures.Count - 1; i >= 0; i--)
            {
                RetiredAccelerationStructureResource retired = _retiredAccelerationStructures[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                DestroyAccelerationStructureResource(retired.AccelerationStructure, retired.StorageBuffer);
                _retiredAccelerationStructures.RemoveAt(i);
            }

            for (int i = _retiredBuffers.Count - 1; i >= 0; i--)
            {
                RetiredBufferResource retired = _retiredBuffers[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                if (retired.Buffer.IsValid)
                    _bufferManager.DestroyBuffer(retired.Buffer);
                _retiredBuffers.RemoveAt(i);
            }
        }

        private void DestroyAccelerationStructureResource(AccelerationStructureKHR accelerationStructure, BufferHandle storageBuffer)
        {
            if (accelerationStructure.Handle != 0)
                _khrAccelerationStructure?.DestroyAccelerationStructure(_context.Device, accelerationStructure, null);
            if (storageBuffer.IsValid)
                _bufferManager.DestroyBuffer(storageBuffer);
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
            DestroyTopLevelAccelerationStructure(defer: false);
            DestroyBottomLevelAccelerationStructures(defer: false);
            if (_scratchBuffer.IsValid)
                _bufferManager.DestroyBuffer(_scratchBuffer);
            if (_instanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_instanceBuffer);
            if (_rayQueryInstanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_rayQueryInstanceBuffer);
            DrainRetiredResources(force: true);
        }

        internal readonly record struct StaticOpaqueInstance(
            MeshHandle Mesh,
            MeshInfo MeshInfo,
            uint MaterialIndex,
            CoreMatrix4x4 WorldMatrix);

        private readonly record struct BottomLevelAccelerationStructure(
            AccelerationStructureKHR Handle,
            BufferHandle StorageBuffer,
            ulong Size);

        private readonly record struct TopLevelAccelerationStructure(
            AccelerationStructureKHR Handle,
            BufferHandle StorageBuffer,
            ulong Size);

        private readonly record struct RetiredAccelerationStructureResource(
            AccelerationStructureKHR AccelerationStructure,
            BufferHandle StorageBuffer,
            ulong RetireAfterFrameSerial);

        private readonly record struct RetiredBufferResource(
            BufferHandle Buffer,
            ulong RetireAfterFrameSerial);
    }

    internal enum AccelerationStructureGeometryDomain
    {
        Static = 0,
        Dynamic = 1,
        Skinned = 2,
        Foliage = 3
    }

    internal enum DdgiAccelerationStructureVisibilityPolicy
    {
        OpaqueTriangles = 0,
        AlphaMaskApproximateOpaque = 1,
        ExcludedTransparent = 2,
        ExcludedGeometryDecal = 3,
        SkinnedBindPoseProxy = 4,
        FoliageProxyPending = 5
    }

    internal enum TopLevelAccelerationStructureBuildAction
    {
        Build = 0,
        Update = 1,
        Skip = 2
    }

    internal readonly record struct DdgiAccelerationStructureGeometryPolicy(
        bool Include,
        byte InstanceMask,
        GeometryInstanceFlagsKHR InstanceFlags,
        DdgiAccelerationStructureVisibilityPolicy VisibilityPolicy,
        string Reason);

    public readonly record struct AccelerationStructureFrameStats(
        bool Supported,
        bool Active,
        int BottomLevelCount,
        int TopLevelInstanceCount,
        ulong AccelerationStructureBytes,
        ulong ScratchBufferBytes,
        ulong InstanceBufferBytes,
        ulong RayQueryInstanceMetadataBufferBytes,
        long BuildMicroseconds,
        long BlasBuildMicroseconds,
        long TlasBuildMicroseconds,
        long InstanceUploadMicroseconds,
        int BlasBuildCount,
        int TlasBuildCount,
        int TlasUpdateCount,
        int TlasSkipCount,
        ulong InstanceUploadBytes,
        ulong RayQueryInstanceMetadataUploadBytes,
        string FallbackReason);
}
