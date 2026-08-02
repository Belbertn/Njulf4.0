using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using CoreBoundingBox = Njulf.Core.Math.BoundingBox;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class AccelerationStructureManager : IDisposable
    {
        public const string FoliageDdgiExclusionReason =
            "foliage uses clustered alpha geometry and requires explicit DDGI proxy cards or clusters";
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
        private readonly ulong _scratchBufferAddressAlignment;
        private readonly Dictionary<MeshHandle, BottomLevelAccelerationStructure> _blasCache = new();
        // Vulkan build-size queries are stable for a mesh-buffer generation but
        // expensive enough to dominate a no-build frame when hundreds of meshes
        // are reconsidered by the residency policy.
        private readonly Dictionary<MeshHandle, ulong> _blasSizeEstimateCache = new();
        private readonly List<AccelerationStructureStorageBuffer> _rayQueryStorageScratch = new();
        private readonly ReadOnlyCollection<AccelerationStructureStorageBuffer> _rayQueryStorageView;
        private readonly List<StaticOpaqueInstance> _instanceScratch = new();
        private readonly List<StaticOpaqueInstance> _residentInstanceScratch = new();
        private readonly List<StaticOpaqueInstance> _memoryResidentInstanceScratch = new();
        private readonly List<StaticResidencyCandidate> _staticResidencyCandidateScratch = new();
        private readonly HashSet<MeshHandle> _activeMeshScratch = new();
        private readonly HashSet<MeshHandle> _budgetMeshScratch = new();
        private readonly HashSet<MeshHandle> _unavailableMeshScratch = new();
        private readonly List<AccelerationStructureInstanceKHR> _gpuInstanceScratch = new();
        private readonly List<GPUDdgiRayQueryInstance> _rayQueryInstanceScratch = new();
        private readonly List<RetiredAccelerationStructureResource> _retiredAccelerationStructures = new();
        private readonly List<RetiredBufferResource> _retiredBuffers = new();
        private ulong _retiredAccelerationStructureBytes;
        private ulong _retiredBufferBytes;

        private TopLevelAccelerationStructure _tlas;
        private BufferHandle _instanceBuffer;
        private ulong _instanceBufferSize;
        private BufferHandle _rayQueryInstanceBuffer;
        private ulong _rayQueryInstanceBufferSize;
        private BufferHandle _scratchBuffer;
        private ulong _scratchBufferSize;
        private ulong _scratchBufferCapacity;
        private ulong _scratchBufferDeviceAddress;
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
        private int _lastStaticInstanceCandidateCount;
        private int _lastStaticInstanceResidentCount;
        private int _lastStaticInstanceCulledCount;
        private int _lastBlasEvictionCount;
        private ulong _lastBlasEvictionBytes;
        private int _lastBlasBudgetRejectedCount;
        private AccelerationStructureResidencyPolicy _residencyPolicy;
        private ulong _lastTlasInstanceSignature;
        private bool _hasTlasInstanceSignature;
        private int _lastTlasInstanceCount;
        private ulong _frameSerial;
        private ulong _resourceGeneration;

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
            _rayQueryStorageView = _rayQueryStorageScratch.AsReadOnly();
            _khrAccelerationStructure = context.KhrAccelerationStructure;
            _scratchBufferAddressAlignment = context.RayQuerySupported && _khrAccelerationStructure != null
                ? QueryScratchBufferAddressAlignment(context)
                : 1UL;
            if (!context.RayQuerySupported)
                _lastFallbackReason = "Ray-query and acceleration-structure features are not supported by the selected Vulkan device.";
            else if (_khrAccelerationStructure == null)
                _lastFallbackReason = "VK_KHR_acceleration_structure could not be loaded.";
            EnsureRayQueryInstanceMetadataCapacity(0);
        }

        public bool Supported => _context.RayQuerySupported && _khrAccelerationStructure != null;
        public bool Active => Supported && _tlas.Handle.Handle != 0 && TopLevelInstanceCount > 0 && string.IsNullOrEmpty(_lastFallbackReason);
        public AccelerationStructureKHR TopLevelAccelerationStructureHandle => _tlas.Handle;
        /// <summary>Backing allocation for the TLAS; required for queue ownership handoffs.</summary>
        public BufferHandle TopLevelAccelerationStructureStorageBuffer => _tlas.StorageBuffer;
        public ulong TopLevelAccelerationStructureStorageBufferBytes => _tlas.Size;
        public BufferHandle RayQueryInstanceMetadataBuffer => _rayQueryInstanceBuffer;
        public int BottomLevelCount => _blasCache.Count;
        public int TopLevelInstanceCount { get; private set; }
        public ulong AccelerationStructureBytes { get; private set; }
        public ulong BottomLevelAccelerationStructureBytes { get; private set; }
        public ulong TopLevelAccelerationStructureBytes => _tlas.Size;
        /// <summary>
        /// Bytes retained only until all in-flight frames can no longer reference a
        /// replaced BLAS/TLAS. These allocations are physically live and are reported
        /// separately from the current resident working set.
        /// </summary>
        public ulong RetiredAccelerationStructureBytes => _retiredAccelerationStructureBytes;
        /// <summary>
        /// All acceleration-structure-associated resources retained until in-flight
        /// work drains. The frame-stat contract uses this aggregate so deferred
        /// instance/scratch buffers cannot disappear from residency telemetry.
        /// </summary>
        public ulong RetiredResourceBytes => SaturatingAdd(
            _retiredAccelerationStructureBytes,
            _retiredBufferBytes);
        public ulong LiveAccelerationStructureBytes => checked(AccelerationStructureBytes + _retiredAccelerationStructureBytes);
        public ulong ScratchBufferBytes => _scratchBufferSize;
        public ulong InstanceBufferBytes => _instanceBufferSize;
        public ulong RayQueryInstanceMetadataBufferBytes => _rayQueryInstanceBufferSize;
        /// <summary>
        /// Temporary physical residency retained while in-flight frames drain.  This
        /// is intentionally distinct from the active BLAS/TLAS working-set cap.
        /// </summary>
        public ulong TransientBytes => SaturatingAdd(
            SaturatingAdd(_retiredAccelerationStructureBytes, _retiredBufferBytes),
            SaturatingAdd(SaturatingAdd(ScratchBufferBytes, InstanceBufferBytes), RayQueryInstanceMetadataBufferBytes));
        public ulong RetiredBufferBytes => _retiredBufferBytes;
        public ulong TotalBytes => SaturatingAdd(LiveAccelerationStructureBytes, SaturatingAdd(
            SaturatingAdd(ScratchBufferBytes, InstanceBufferBytes),
            SaturatingAdd(RayQueryInstanceMetadataBufferBytes, _retiredBufferBytes)));
        public string LastFallbackReason => _lastFallbackReason;
        public long LastBuildMicroseconds => _lastBuildMicroseconds;
        /// <summary>Changes when a ray-query-visible backing allocation is added or replaced.</summary>
        public ulong ResourceGeneration => _resourceGeneration;

        /// <summary>
        /// Resolves every backing allocation traversed by a ray query. A TLAS descriptor reaches
        /// its BLAS children indirectly, but Vulkan queue ownership still applies to each backing
        /// buffer; exposing only the TLAS storage would leave those reads unsynchronized.
        /// The returned read-only view is reused by the manager and is valid until the next call.
        /// </summary>
        public IReadOnlyList<AccelerationStructureStorageBuffer> GetRayQueryStorageBuffers()
        {
            // This is queried once for every async-plan refresh. Reuse a private scratch list so
            // a scene with many BLAS allocations does not create a per-frame managed allocation.
            // Callers consume it synchronously while building the immutable binding snapshot.
            List<AccelerationStructureStorageBuffer> buffers = _rayQueryStorageScratch;
            buffers.Clear();
            if (buffers.Capacity < _blasCache.Count + 1)
                buffers.Capacity = _blasCache.Count + 1;
            if (_tlas.StorageBuffer.IsValid && _tlas.Size > 0)
            {
                buffers.Add(new AccelerationStructureStorageBuffer(
                    _tlas.StorageBuffer,
                    _tlas.Size,
                    "TLAS storage"));
            }

            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
            {
                if (!blas.StorageBuffer.IsValid || blas.Size == 0)
                    continue;

                buffers.Add(new AccelerationStructureStorageBuffer(
                    blas.StorageBuffer,
                    blas.Size,
                    "BLAS storage"));
            }

            return _rayQueryStorageView;
        }

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
            int frameIndex = 0,
            AccelerationStructureResidencyPolicy? residencyPolicy = null,
            bool alphaMaskedTransportEnabled = true)
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
            _residencyPolicy = residencyPolicy ?? AccelerationStructureResidencyPolicy.Disabled;

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
                CollectStaticOpaqueInstances(scene, _instanceScratch, alphaMaskedTransportEnabled);
                ApplyResidencyPolicy(_instanceScratch);
                if (!ApplyMemoryResidencyPolicy(_instanceScratch))
                {
                    _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                    return CreateStats(false);
                }
                if (_instanceScratch.Count == 0)
                {
                    _lastFallbackReason = "No opaque acceleration-structure instances were submitted.";
                    return CreateStats(false);
                }

                BuildActiveMeshSet(_instanceScratch);
                PruneUnusedBottomLevelAccelerationStructures();

                bool missingBlas = HasMissingBottomLevelAccelerationStructures(_instanceScratch);
                ulong additionalTlasBudgetReservation = 0;
                if (missingBlas)
                {
                    EnsureInstanceBufferCapacity(_instanceScratch.Count);
                    ulong estimatedTlasBytes = EstimateTopLevelAccelerationStructureBytes(_instanceScratch.Count);
                    additionalTlasBudgetReservation = CalculateAdditionalTopLevelReservation(
                        estimatedTlasBytes,
                        _tlas.Size);
                }
                if (missingBlas)
                    gpuTimestamps?.BeginPass(commandBuffer, frameIndex, "AccelerationStructureBlasPass");
                try
                {
                    EnsureBottomLevelAccelerationStructures(
                        _instanceScratch,
                        commandBuffer,
                        additionalTlasBudgetReservation);
                }
                finally
                {
                    if (missingBlas)
                        gpuTimestamps?.EndPass(commandBuffer, frameIndex);
                }

                if (_unavailableMeshScratch.Count > 0)
                {
                    // A hole-punched TLAS is not a valid lighting representation:
                    // probe and shadow rays would pass through rasterized geometry.
                    // Keep the previous resource alive for in-flight work, but mark
                    // this frame inactive and retry transactionally next frame.
                    _lastFallbackReason =
                        "GI acceleration-structure admission could not build every mesh in the resolved resident set; no partial TLAS was published.";
                    _lastStaticInstanceCulledCount = checked(
                        _lastStaticInstanceCulledCount + _lastStaticInstanceResidentCount);
                    _lastStaticInstanceResidentCount = 0;
                    TopLevelInstanceCount = 0;
                    _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                    return CreateStats(false);
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

        internal void CollectStaticOpaqueInstances(
            Scene scene,
            List<StaticOpaqueInstance> instances,
            bool alphaMaskedTransportEnabled = true)
        {
            instances.Clear();

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!renderObject.Visible || !renderObject.Enabled)
                    continue;
                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;
                AccelerationStructureGeometryDomain requestedDomain = renderObject.IsStatic
                    ? AccelerationStructureGeometryDomain.Static
                    : AccelerationStructureGeometryDomain.Dynamic;
                if (renderObject is SkinnedRenderObject)
                    requestedDomain = AccelerationStructureGeometryDomain.Skinned;
                if (!TryGetRayQueryMesh(
                    meshHandle,
                    renderObject.Material,
                    renderObject.Name,
                    requestedDomain,
                    out MeshInfo meshInfo,
                    out uint materialIndex,
                    out GeometryInstanceFlagsKHR instanceFlags,
                    alphaMaskedTransportEnabled))
                    continue;

                AccelerationStructureGeometryDomain domain = meshInfo.IsSkinned
                    ? AccelerationStructureGeometryDomain.Skinned
                    : requestedDomain;

                instances.Add(new StaticOpaqueInstance(
                    meshHandle,
                    meshInfo,
                    materialIndex,
                    renderObject.WorldMatrix,
                    domain,
                    instanceFlags));
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
                    out uint materialIndex,
                    out GeometryInstanceFlagsKHR instanceFlags,
                    alphaMaskedTransportEnabled))
                    continue;

                IReadOnlyList<CoreMatrix4x4> worldMatrices = batch.WorldMatrices;
                for (int i = 0; i < worldMatrices.Count; i++)
                    instances.Add(new StaticOpaqueInstance(
                        meshHandle,
                        meshInfo,
                        materialIndex,
                        worldMatrices[i],
                        AccelerationStructureGeometryDomain.Static,
                        instanceFlags));
            }
        }

        /// <summary>
        /// Keeps dynamic objects authoritative while selecting a stable, nearest-first
        /// working set of static batches.  Static geometry is the streamable domain;
        /// a normal render object is deliberately never silently culled from the
        /// detailed ray-query representation by this policy.
        /// </summary>
        private void ApplyResidencyPolicy(List<StaticOpaqueInstance> instances)
        {
            _lastStaticInstanceCandidateCount = 0;
            _lastStaticInstanceResidentCount = 0;
            _lastStaticInstanceCulledCount = 0;

            if (!_residencyPolicy.Enabled)
            {
                for (int i = 0; i < instances.Count; i++)
                {
                    if (instances[i].Domain == AccelerationStructureGeometryDomain.Static)
                        _lastStaticInstanceCandidateCount++;
                }

                _lastStaticInstanceResidentCount = _lastStaticInstanceCandidateCount;
                return;
            }

            _residentInstanceScratch.Clear();
            _staticResidencyCandidateScratch.Clear();
            float residentDistance = Math.Max(0.0f, _residencyPolicy.StaticResidentDistance);
            float residentDistanceSquared = residentDistance >= MathF.Sqrt(float.MaxValue)
                ? float.MaxValue
                : residentDistance * residentDistance;

            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (instance.Domain != AccelerationStructureGeometryDomain.Static)
                {
                    _residentInstanceScratch.Add(instance);
                    continue;
                }

                _lastStaticInstanceCandidateCount++;
                float distanceSquared = DistanceSquaredToBounds(
                    _residencyPolicy.CameraPosition,
                    GetInstanceWorldBounds(instance));
                if (!float.IsFinite(distanceSquared) || distanceSquared > residentDistanceSquared)
                {
                    _lastStaticInstanceCulledCount++;
                    continue;
                }

                _staticResidencyCandidateScratch.Add(new StaticResidencyCandidate(instance, distanceSquared));
            }

            _staticResidencyCandidateScratch.Sort(CompareStaticResidencyCandidates);
            int staticResidentCount = Math.Min(
                _staticResidencyCandidateScratch.Count,
                Math.Max(0, _residencyPolicy.MaximumStaticInstances));
            for (int i = 0; i < staticResidentCount; i++)
                _residentInstanceScratch.Add(_staticResidencyCandidateScratch[i].Instance);

            _lastStaticInstanceResidentCount = staticResidentCount;
            _lastStaticInstanceCulledCount += _staticResidencyCandidateScratch.Count - staticResidentCount;
            instances.Clear();
            instances.AddRange(_residentInstanceScratch);
        }

        /// <summary>
        /// Resolves the memory cap before any BLAS allocation. High-quality tiers
        /// require the entire distance/count-resident set; constrained tiers may
        /// trim only a coherent nearest-first static tail, which the far-field
        /// representation owns. Dynamic/skinned instances are never memory-culled.
        /// </summary>
        private bool ApplyMemoryResidencyPolicy(List<StaticOpaqueInstance> instances)
        {
            if (!_residencyPolicy.Enabled || instances.Count == 0)
                return true;

            EnsureInstanceBufferCapacity(instances.Count);
            ulong topLevelReservation = EstimateTopLevelAccelerationStructureBytes(instances.Count);
            ulong requiredWorkingSet = EstimateResidentWorkingSetBytes(
                instances,
                topLevelReservation,
                out int missingMeshCount);
            ulong budgetBytes = _residencyPolicy.EffectiveMemoryBudgetBytes;
            if (requiredWorkingSet <= budgetBytes)
                return true;

            if (!_residencyPolicy.AllowStaticMemoryCulling)
            {
                _lastBlasBudgetRejectedCount = Math.Max(1, missingMeshCount);
                _lastStaticInstanceCulledCount = checked(
                    _lastStaticInstanceCulledCount + _lastStaticInstanceResidentCount);
                _lastStaticInstanceResidentCount = 0;
                _lastFallbackReason =
                    $"The complete GI ray-query resident set requires at least {requiredWorkingSet} bytes, " +
                    $"but the active quality tier provides {budgetBytes} bytes; no partial TLAS was published.";
                TopLevelInstanceCount = 0;
                return false;
            }

            _memoryResidentInstanceScratch.Clear();
            _budgetMeshScratch.Clear();
            ulong selectedBytes = topLevelReservation;

            // ApplyResidencyPolicy emits mandatory non-static instances first.
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (instance.Domain == AccelerationStructureGeometryDomain.Static)
                    continue;

                ulong additionalBytes = ResolveUniqueMeshAdmissionBytes(instance);
                if (WouldExceedBudget(selectedBytes, additionalBytes, budgetBytes))
                {
                    _lastBlasBudgetRejectedCount = Math.Max(1, missingMeshCount);
                    _lastFallbackReason =
                        "The GI acceleration-structure budget cannot represent the mandatory dynamic/skinned resident set; no partial TLAS was published.";
                    TopLevelInstanceCount = 0;
                    return false;
                }

                selectedBytes = checked(selectedBytes + additionalBytes);
                _memoryResidentInstanceScratch.Add(instance);
            }

            bool staticTailCulled = false;
            int selectedStaticCount = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (instance.Domain != AccelerationStructureGeometryDomain.Static)
                    continue;

                if (staticTailCulled)
                    continue;

                ulong additionalBytes = ResolveUniqueMeshAdmissionBytes(instance);
                if (WouldExceedBudget(selectedBytes, additionalBytes, budgetBytes))
                {
                    // Stop at the first non-fitting nearest-first candidate. Skipping
                    // holes and admitting cheaper distant meshes would make ray-scene
                    // topology depend on mesh allocation size rather than distance.
                    staticTailCulled = true;
                    continue;
                }

                selectedBytes = checked(selectedBytes + additionalBytes);
                selectedStaticCount++;
                _memoryResidentInstanceScratch.Add(instance);
            }

            int memoryCulledStaticCount = Math.Max(0, _lastStaticInstanceResidentCount - selectedStaticCount);
            _lastStaticInstanceResidentCount = selectedStaticCount;
            _lastStaticInstanceCulledCount = checked(_lastStaticInstanceCulledCount + memoryCulledStaticCount);
            instances.Clear();
            instances.AddRange(_memoryResidentInstanceScratch);
            if (instances.Count > 0)
                return true;

            _lastFallbackReason =
                "The GI acceleration-structure budget cannot hold the nearest static resident mesh; no partial TLAS was published.";
            return false;
        }

        private ulong EstimateResidentWorkingSetBytes(
            IReadOnlyList<StaticOpaqueInstance> instances,
            ulong topLevelReservation,
            out int missingMeshCount)
        {
            _budgetMeshScratch.Clear();
            ulong bytes = topLevelReservation;
            missingMeshCount = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (!_budgetMeshScratch.Add(instance.Mesh))
                    continue;

                if (_blasCache.TryGetValue(instance.Mesh, out BottomLevelAccelerationStructure? cachedBlas))
                {
                    bytes = SaturatingAdd(bytes, cachedBlas.Size);
                    continue;
                }

                bytes = SaturatingAdd(bytes, EstimateBottomLevelAccelerationStructureBytes(instance.Mesh, instance.MeshInfo));
                missingMeshCount++;
            }

            return bytes;
        }

        private ulong ResolveUniqueMeshAdmissionBytes(StaticOpaqueInstance instance)
        {
            if (!_budgetMeshScratch.Add(instance.Mesh))
                return 0;
            return _blasCache.TryGetValue(instance.Mesh, out BottomLevelAccelerationStructure? cachedBlas)
                ? cachedBlas.Size
                : EstimateBottomLevelAccelerationStructureBytes(instance.Mesh, instance.MeshInfo);
        }

        private static int CompareStaticResidencyCandidates(
            StaticResidencyCandidate left,
            StaticResidencyCandidate right)
        {
            int comparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
            if (comparison != 0)
                return comparison;

            comparison = left.Instance.Mesh.Index.CompareTo(right.Instance.Mesh.Index);
            if (comparison != 0)
                return comparison;
            comparison = left.Instance.Mesh.Generation.CompareTo(right.Instance.Mesh.Generation);
            if (comparison != 0)
                return comparison;
            comparison = left.Instance.MaterialIndex.CompareTo(right.Instance.MaterialIndex);
            if (comparison != 0)
                return comparison;

            CoreMatrix4x4 leftMatrix = left.Instance.WorldMatrix;
            CoreMatrix4x4 rightMatrix = right.Instance.WorldMatrix;
            comparison = leftMatrix.M41.CompareTo(rightMatrix.M41);
            if (comparison != 0)
                return comparison;
            comparison = leftMatrix.M42.CompareTo(rightMatrix.M42);
            if (comparison != 0)
                return comparison;
            return leftMatrix.M43.CompareTo(rightMatrix.M43);
        }

        private static CoreBoundingBox GetInstanceWorldBounds(StaticOpaqueInstance instance)
        {
            CoreBoundingBox localBounds = new(
                new CoreVector3(
                    instance.MeshInfo.BoundingBoxMin.X,
                    instance.MeshInfo.BoundingBoxMin.Y,
                    instance.MeshInfo.BoundingBoxMin.Z),
                new CoreVector3(
                    instance.MeshInfo.BoundingBoxMax.X,
                    instance.MeshInfo.BoundingBoxMax.Y,
                    instance.MeshInfo.BoundingBoxMax.Z));
            return CoreBoundingBox.Transform(localBounds, instance.WorldMatrix);
        }

        private static float DistanceSquaredToBounds(CoreVector3 position, CoreBoundingBox bounds)
        {
            float x = position.X < bounds.Min.X
                ? bounds.Min.X - position.X
                : position.X > bounds.Max.X ? position.X - bounds.Max.X : 0.0f;
            float y = position.Y < bounds.Min.Y
                ? bounds.Min.Y - position.Y
                : position.Y > bounds.Max.Y ? position.Y - bounds.Max.Y : 0.0f;
            float z = position.Z < bounds.Min.Z
                ? bounds.Min.Z - position.Z
                : position.Z > bounds.Max.Z ? position.Z - bounds.Max.Z : 0.0f;
            return x * x + y * y + z * z;
        }

        private void BuildActiveMeshSet(IReadOnlyList<StaticOpaqueInstance> instances)
        {
            _activeMeshScratch.Clear();
            for (int i = 0; i < instances.Count; i++)
                _activeMeshScratch.Add(instances[i].Mesh);
        }

        private bool TryGetRayQueryMesh(
            MeshHandle meshHandle,
            object? material,
            string? ownerName,
            AccelerationStructureGeometryDomain domain,
            out MeshInfo meshInfo,
            out uint materialIndex,
            out GeometryInstanceFlagsKHR instanceFlags,
            bool alphaMaskedTransportEnabled)
        {
            meshInfo = default;
            materialIndex = 0;
            instanceFlags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr;
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
                    domain,
                    metadata.DoubleSided,
                    metadata.TransmissionPolicy);
                if (!policy.Include)
                    return false;

                materialIndex = checked((uint)Math.Max(materialHandle.Index, 0));
                instanceFlags = policy.InstanceFlags;
                bool alphaTested =
                    policy.VisibilityPolicy == DdgiAccelerationStructureVisibilityPolicy.AlphaMaskTested ||
                    policy.VisibilityPolicy == DdgiAccelerationStructureVisibilityPolicy.SkinnedAlphaMaskTestedProxy;
                if (alphaTested && !alphaMaskedTransportEnabled)
                    instanceFlags |= GeometryInstanceFlagsKHR.ForceOpaqueBitKhr;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void EnsureBottomLevelAccelerationStructures(
            IReadOnlyList<StaticOpaqueInstance> instances,
            CommandBuffer commandBuffer,
            ulong additionalTlasBudgetReservation)
        {
            _unavailableMeshScratch.Clear();
            foreach (StaticOpaqueInstance instance in instances)
            {
                if (_unavailableMeshScratch.Contains(instance.Mesh))
                    continue;

                if (_blasCache.TryGetValue(instance.Mesh, out BottomLevelAccelerationStructure? cachedBlas))
                {
                    cachedBlas.LastUsedFrameSerial = _frameSerial;
                    continue;
                }

                ulong requiredBytes = EstimateBottomLevelAccelerationStructureBytes(instance.Mesh, instance.MeshInfo);
                if (!EnsureBottomLevelResidencyBudget(requiredBytes, additionalTlasBudgetReservation))
                {
                    _unavailableMeshScratch.Add(instance.Mesh);
                    _lastBlasBudgetRejectedCount++;
                    continue;
                }

                long blasStart = Stopwatch.GetTimestamp();
                BottomLevelAccelerationStructure blas = BuildBottomLevelAccelerationStructure(instance.Mesh, instance.MeshInfo, commandBuffer);
                blas.LastUsedFrameSerial = _frameSerial;
                _lastBlasBuildMicroseconds += ElapsedMicroseconds(blasStart);
                _lastBlasBuildCount++;
                _blasCache.Add(instance.Mesh, blas);
                AdvanceResourceGeneration();
                AccelerationStructureBytes = checked(AccelerationStructureBytes + blas.Size);
                InsertAccelerationStructureBuildBarrier(commandBuffer);
            }
        }

        private ulong EstimateBottomLevelAccelerationStructureBytes(MeshHandle meshHandle, MeshInfo meshInfo)
        {
            if (_blasSizeEstimateCache.TryGetValue(meshHandle, out ulong cachedSize))
                return cachedSize;

            uint primitiveCount = meshInfo.IndexCount / 3u;
            if (primitiveCount == 0)
                throw new InvalidOperationException("Cannot reserve BLAS memory for a mesh with no triangle primitives.");

            AccelerationStructureGeometryKHR geometry = CreateBottomLevelGeometry(meshInfo);
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateBottomLevelBuildInfo(&geometry, default, default);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);
            ulong estimatedSize = Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize);
            _blasSizeEstimateCache[meshHandle] = estimatedSize;
            return estimatedSize;
        }

        private bool EnsureBottomLevelResidencyBudget(
            ulong requiredBytes,
            ulong additionalTlasBudgetReservation)
        {
            if (!_residencyPolicy.Enabled)
                return true;

            ulong budgetBytes = _residencyPolicy.EffectiveMemoryBudgetBytes;
            ulong requiredAndReservedBytes = checked(requiredBytes + additionalTlasBudgetReservation);
            if (requiredAndReservedBytes > budgetBytes)
                return false;

            while (WouldExceedBudget(AccelerationStructureBytes, requiredAndReservedBytes, budgetBytes))
            {
                if (!TryEvictBottomLevelAccelerationStructure(force: true))
                    return false;
            }

            return true;
        }

        private ulong EstimateTopLevelAccelerationStructureBytes(int instanceCount)
        {
            uint primitiveCount = checked((uint)Math.Max(0, instanceCount));
            AccelerationStructureGeometryKHR geometry = CreateTopLevelGeometry();
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                default,
                default,
                default,
                BuildAccelerationStructureModeKHR.BuildKhr);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);
            return Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize);
        }

        internal static ulong CalculateAdditionalTopLevelReservation(
            ulong estimatedTopLevelBytes,
            ulong currentTopLevelBytes) =>
            estimatedTopLevelBytes > currentTopLevelBytes
                ? estimatedTopLevelBytes - currentTopLevelBytes
                : 0;

        private void PruneUnusedBottomLevelAccelerationStructures()
        {
            if (!_residencyPolicy.Enabled || _blasCache.Count == 0)
                return;

            // The hard cap is enforced before every new allocation.  This pass also
            // releases aged streamed chunks when memory pressure is absent, avoiding
            // a cache that is bounded in theory but permanently retains travelled
            // world content in practice.
            while (TryEvictBottomLevelAccelerationStructure(force: false))
            {
            }
        }

        private bool TryEvictBottomLevelAccelerationStructure(bool force)
        {
            if (_blasCache.Count == 0)
                return false;

            MeshHandle selectedMesh = default;
            BottomLevelAccelerationStructure? selectedBlas = null;
            ulong minimumLastUsedFrame = ulong.MaxValue;
            ulong graceFrames = (ulong)Math.Max(0, _residencyPolicy.EvictionGraceFrames);
            foreach (KeyValuePair<MeshHandle, BottomLevelAccelerationStructure> pair in _blasCache)
            {
                if (_activeMeshScratch.Contains(pair.Key))
                    continue;

                BottomLevelAccelerationStructure candidate = pair.Value;
                bool oldEnough = _frameSerial >= candidate.LastUsedFrameSerial &&
                    _frameSerial - candidate.LastUsedFrameSerial >= graceFrames;
                if (!force && !oldEnough)
                    continue;
                if (selectedBlas != null && candidate.LastUsedFrameSerial >= minimumLastUsedFrame)
                    continue;

                selectedMesh = pair.Key;
                selectedBlas = candidate;
                minimumLastUsedFrame = candidate.LastUsedFrameSerial;
            }

            if (selectedBlas == null)
                return false;

            // Eviction removes the BLAS from the active working set immediately,
            // but Vulkan still requires its storage to survive the in-flight
            // frames that may reference the old TLAS. Do not turn an active-cap
            // save into unbounded transient growth.
            if (!CanReserveTransientBytes(selectedBlas.Size))
                return false;

            _blasCache.Remove(selectedMesh);
            AdvanceResourceGeneration();
            RetireAccelerationStructureResource(selectedBlas.Handle, selectedBlas.StorageBuffer, selectedBlas.Size);
            _lastBlasEvictionCount++;
            _lastBlasEvictionBytes = checked(_lastBlasEvictionBytes + selectedBlas.Size);
            RecalculateAccelerationStructureBytes();
            return true;
        }

        private static bool WouldExceedBudget(ulong currentBytes, ulong additionalBytes, ulong budgetBytes)
        {
            return currentBytes > budgetBytes || additionalBytes > budgetBytes - currentBytes;
        }

        internal static ulong CalculateScratchMemoryBudgetBytes(ulong activeAccelerationStructureBudgetBytes)
        {
            if (activeAccelerationStructureBudgetBytes == 0)
                return 0;
            if (activeAccelerationStructureBudgetBytes == ulong.MaxValue)
                return ulong.MaxValue;

            // A build scratch buffer is reusable, but it must still have a hard
            // tier-derived limit. Half of the active working-set cap is ample for
            // the supported geometry budget while 128 MiB prevents one large BLAS
            // from silently redefining a tier.
            const ulong minimumBytes = 16UL * 1024UL * 1024UL;
            const ulong maximumBytes = 128UL * 1024UL * 1024UL;
            ulong proportionalBytes = activeAccelerationStructureBudgetBytes / 2UL;
            return Math.Min(maximumBytes, Math.Max(minimumBytes, proportionalBytes));
        }

        internal static ulong CalculateTransientMemoryBudgetBytes(ulong activeAccelerationStructureBudgetBytes)
        {
            if (activeAccelerationStructureBudgetBytes == 0)
                return 0;
            if (activeAccelerationStructureBudgetBytes == ulong.MaxValue)
                return ulong.MaxValue;

            // A content reload may retain one complete previous BLAS/TLAS working
            // set while the next generation is built. Reserve that bounded handoff
            // plus the explicit scratch allowance, then reject further churn until
            // in-flight resources drain.
            return SaturatingAdd(
                activeAccelerationStructureBudgetBytes,
                CalculateScratchMemoryBudgetBytes(activeAccelerationStructureBudgetBytes));
        }

        private static ulong SaturatingAdd(ulong left, ulong right) =>
            ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private bool CanReserveTransientBytes(ulong additionalBytes)
        {
            if (!_residencyPolicy.Enabled)
                return true;

            ulong transientBudget = _residencyPolicy.EffectiveTransientMemoryBudgetBytes;
            return !WouldExceedBudget(TransientBytes, additionalBytes, transientBudget);
        }

        private void EnsureTransientAllocationBudget(ulong additionalBytes, string resourceName)
        {
            if (additionalBytes == 0 || !_residencyPolicy.Enabled)
                return;

            ulong transientBudget = _residencyPolicy.EffectiveTransientMemoryBudgetBytes;
            if (CanReserveTransientBytes(additionalBytes))
                return;

            throw new InvalidOperationException(
                $"GI acceleration-structure transient budget ({transientBudget} bytes) cannot accommodate {resourceName} " +
                $"({additionalBytes} bytes) while {TransientBytes} bytes are still live.");
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

            _blasSizeEstimateCache.Clear();

            // Replacing every BLAS/TLAS at once temporarily retains the previous
            // generation until all in-flight ray queries have completed. Reserve
            // that physical residency before invalidating anything so a content
            // reload degrades to the safe non-ray-query path instead of silently
            // violating the transient cap.
            EnsureTransientAllocationBudget(
                AccelerationStructureBytes,
                "mesh-buffer acceleration-structure replacement");
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

            // Reserve reusable scratch before allocating the BLAS storage.  A
            // transient-cap failure therefore cannot leak a partially-created
            // BLAS allocation or leave a half-built cache entry behind.
            EnsureScratchCapacity(sizes.BuildScratchSize);

            BufferHandle storageBuffer = _bufferManager.CreateDeviceBuffer(
                Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize),
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                $"BLAS Mesh {meshHandle.Index}");
            AccelerationStructureKHR accelerationStructure = default;
            try
            {
                accelerationStructure = CreateAccelerationStructure(
                    storageBuffer,
                    sizes.AccelerationStructureSize,
                    AccelerationStructureTypeKHR.BottomLevelKhr,
                    $"BLAS Mesh {meshHandle.Index}");

                geometry = CreateBottomLevelGeometry(meshInfo);
                buildInfo = CreateBottomLevelBuildInfo(
                    &geometry,
                    accelerationStructure,
                    _scratchBufferDeviceAddress);

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
            catch
            {
                DestroyAccelerationStructureResource(accelerationStructure, storageBuffer);
                throw;
            }
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
                // Opaqueness is selected per TLAS instance.  Opaque instances use
                // ForceOpaqueBitKhr, while alpha-mask instances expose candidates
                // to the DDGI shader for the material cutoff test.
                Flags = default
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
                _gpuInstanceScratch.Add(CreateInstance(
                    instance.WorldMatrix,
                    blasAddress,
                    (uint)i,
                    StaticOpaqueInstanceMask,
                    instance.InstanceFlags));
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
            bool willRecreateTlas = _tlas.Handle.Handle == 0 || _tlas.Size < Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize);
            bool useUpdate = requestedAction == TopLevelAccelerationStructureBuildAction.Update && !willRecreateTlas;
            ulong scratchSize = useUpdate && sizes.UpdateScratchSize > 0 ? sizes.UpdateScratchSize : sizes.BuildScratchSize;
            EnsureScratchCapacity(scratchSize);
            bool tlasRecreated = EnsureTopLevelAccelerationStructure(sizes.AccelerationStructureSize);
            // The allocation predicate above is intentionally mirrored here. A
            // mismatch would mean the selected update scratch size is unsafe.
            if (tlasRecreated != willRecreateTlas)
                throw new InvalidOperationException("TLAS allocation state changed while resolving the acceleration-structure build scratch requirement.");

            geometry = CreateTopLevelGeometry();
            AccelerationStructureKHR source = useUpdate ? _tlas.Handle : default;
            buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                _tlas.Handle,
                source,
                _scratchBufferDeviceAddress,
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

            if (_residencyPolicy.Enabled)
            {
                ulong budgetBytes = _residencyPolicy.EffectiveMemoryBudgetBytes;
                ulong existingTlasBytes = _tlas.Size;
                ulong activeBytesWithoutTlas = AccelerationStructureBytes >= existingTlasBytes
                    ? AccelerationStructureBytes - existingTlasBytes
                    : 0;
                while (WouldExceedBudget(activeBytesWithoutTlas, requiredSize, budgetBytes))
                {
                    if (!TryEvictBottomLevelAccelerationStructure(force: true))
                    {
                        throw new InvalidOperationException(
                            $"GI acceleration-structure residency budget ({budgetBytes} bytes) cannot accommodate the active TLAS and BLAS working set.");
                    }

                    activeBytesWithoutTlas = AccelerationStructureBytes >= existingTlasBytes
                        ? AccelerationStructureBytes - existingTlasBytes
                        : 0;
                }

                // Replacing a TLAS defers the old storage through the frames in
                // flight. Budget that transition before mutating the active TLAS
                // so a resize cannot escape the separately declared transient cap.
                EnsureTransientAllocationBudget(existingTlasBytes, "retired top-level acceleration structure");
            }

            DestroyTopLevelAccelerationStructure(defer: true);
            BufferHandle storageBuffer = _bufferManager.CreateDeviceBuffer(
                requiredSize,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "Top Level Acceleration Structure");
            AccelerationStructureKHR tlas = default;
            try
            {
                tlas = CreateAccelerationStructure(
                    storageBuffer,
                    requiredSize,
                    AccelerationStructureTypeKHR.TopLevelKhr,
                    "Top Level Acceleration Structure");
                _tlas = new TopLevelAccelerationStructure(tlas, storageBuffer, requiredSize);
                AdvanceResourceGeneration();
            }
            catch
            {
                DestroyAccelerationStructureResource(tlas, storageBuffer);
                throw;
            }
            RecalculateAccelerationStructureBytes();
            return true;
        }

        private void EnsureScratchCapacity(ulong requiredSize)
        {
            requiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (_scratchBuffer.IsValid && _scratchBufferCapacity >= requiredSize)
                return;

            ulong allocationSize = CalculateScratchBufferAllocationSize(
                requiredSize,
                _scratchBufferAddressAlignment);
            ulong scratchBudget = _residencyPolicy.EffectiveScratchMemoryBudgetBytes;
            if (allocationSize > scratchBudget)
            {
                throw new InvalidOperationException(
                    $"GI acceleration-structure scratch budget ({scratchBudget} bytes) cannot accommodate the requested build scratch ({allocationSize} bytes).");
            }
            EnsureTransientAllocationBudget(allocationSize, "acceleration-structure scratch buffer");

            if (_scratchBuffer.IsValid)
                RetireBufferResource(_scratchBuffer);

            _scratchBufferCapacity = requiredSize;
            _scratchBufferSize = allocationSize;
            _scratchBuffer = _bufferManager.CreateDeviceBuffer(
                _scratchBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "Acceleration Structure Scratch Buffer");

            ulong baseAddress = _bufferManager.GetBufferDeviceAddress(_scratchBuffer);
            _scratchBufferDeviceAddress = AlignScratchBufferAddress(
                baseAddress,
                _scratchBufferAddressAlignment);
            ulong alignedOffset = checked(_scratchBufferDeviceAddress - baseAddress);
            if (alignedOffset > _scratchBufferSize || _scratchBufferSize - alignedOffset < requiredSize)
            {
                throw new InvalidOperationException(
                    "The aligned acceleration-structure scratch address does not leave enough usable buffer capacity.");
            }
        }

        private static ulong QueryScratchBufferAddressAlignment(VulkanContext context)
        {
            var accelerationStructureProperties = new PhysicalDeviceAccelerationStructurePropertiesKHR
            {
                SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr
            };
            var properties = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &accelerationStructureProperties
            };
            context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
            return Math.Max(
                1UL,
                accelerationStructureProperties.MinAccelerationStructureScratchOffsetAlignment);
        }

        internal static ulong AlignScratchBufferAddress(ulong address, ulong alignment)
        {
            if (alignment == 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));

            ulong remainder = address % alignment;
            return remainder == 0
                ? address
                : checked(address + alignment - remainder);
        }

        internal static ulong CalculateScratchBufferAllocationSize(ulong requiredSize, ulong alignment)
        {
            if (alignment == 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));

            return checked(requiredSize + alignment - 1UL);
        }

        private void EnsureInstanceBufferCapacity(int instanceCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Max(0, instanceCount) * (ulong)sizeof(AccelerationStructureInstanceKHR)));
            if (_instanceBuffer.IsValid && _instanceBufferSize >= requiredSize)
                return;

            EnsureTransientAllocationBudget(requiredSize, "TLAS instance buffer");
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

            EnsureTransientAllocationBudget(requiredSize, "ray-query instance metadata buffer");
            if (_rayQueryInstanceBuffer.IsValid)
                RetireBufferResource(_rayQueryInstanceBuffer);

            _rayQueryInstanceBufferSize = requiredSize;
            _rayQueryInstanceBuffer = _bufferManager.CreateDeviceBuffer(
                _rayQueryInstanceBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "DDGI Ray Query Instance Metadata Buffer");
            AdvanceResourceGeneration();
            RegisterRayQueryInstanceMetadataBuffer();
        }

        private void AdvanceResourceGeneration()
        {
            _resourceGeneration++;
            if (_resourceGeneration == 0)
                _resourceGeneration = 1;
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
            byte mask,
            GeometryInstanceFlagsKHR flags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr)
        {
            if (worldMatrix.Determinant() < 0.0f)
                flags |= GeometryInstanceFlagsKHR.TriangleFlipFacingBitKhr;
            return new AccelerationStructureInstanceKHR
            {
                Transform = CreateTransform(worldMatrix),
                InstanceCustomIndex = instanceCustomIndex & 0x00FF_FFFFu,
                Mask = mask,
                InstanceShaderBindingTableRecordOffset = 0,
                Flags = flags,
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
            AccelerationStructureGeometryDomain domain,
            bool doubleSided = false,
            GiTransmissionPolicy transmissionPolicy = GiTransmissionPolicy.None)
        {
            GeometryInstanceFlagsKHR sidednessFlags = doubleSided
                ? GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr
                : default;
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
                    FoliageDdgiExclusionReason);
            }

            bool thinSurface = transmissionPolicy == GiTransmissionPolicy.ThinSurface;
            if (renderMode == MaterialRenderMode.Blend && !thinSurface)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    false,
                    0,
                    default,
                    DdgiAccelerationStructureVisibilityPolicy.ExcludedTransparent,
                    "transparent blended materials are excluded from DDGI ray-query occlusion");
            }

            if (thinSurface)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    sidednessFlags,
                    DdgiAccelerationStructureVisibilityPolicy.ThinSurfaceCandidateTested,
                    renderMode == MaterialRenderMode.Blend
                        ? "explicit blended thin surfaces participate in DDGI candidate transport"
                        : "thin surfaces remain candidate-tested for reflected/transmitted transport");
            }

            if (isSkinned || domain == AccelerationStructureGeometryDomain.Skinned)
            {
                if (renderMode == MaterialRenderMode.Mask)
                {
                    return new DdgiAccelerationStructureGeometryPolicy(
                        true,
                        StaticOpaqueInstanceMask,
                        sidednessFlags,
                        DdgiAccelerationStructureVisibilityPolicy.SkinnedAlphaMaskTestedProxy,
                        "skinned alpha-masked meshes use bind-pose triangles while preserving authored alpha coverage");
                }

                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    GeometryInstanceFlagsKHR.ForceOpaqueBitKhr | sidednessFlags,
                    DdgiAccelerationStructureVisibilityPolicy.SkinnedBindPoseProxy,
                    "skinned meshes contribute a bind-pose triangle proxy until animated proxy geometry is available");
            }

            if (renderMode == MaterialRenderMode.Mask)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    sidednessFlags,
                    DdgiAccelerationStructureVisibilityPolicy.AlphaMaskTested,
                    "alpha-masked geometry is evaluated at ray-query candidates using the glTF cutoff");
            }

            return new DdgiAccelerationStructureGeometryPolicy(
                true,
                StaticOpaqueInstanceMask,
                GeometryInstanceFlagsKHR.ForceOpaqueBitKhr | sidednessFlags,
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
                hash = HashAdd(hash, (int)instance.Domain);
                hash = HashAdd(hash, (uint)instance.InstanceFlags);
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
                _lastStaticInstanceCandidateCount,
                _lastStaticInstanceResidentCount,
                _lastStaticInstanceCulledCount,
                _lastBlasEvictionCount,
                _lastBlasEvictionBytes,
                _lastBlasBudgetRejectedCount,
                BottomLevelAccelerationStructureBytes,
                TopLevelAccelerationStructureBytes,
                // The persisted frame-stat field predates deferred buffer
                // tracking. Keep its shape stable, but report every retired
                // acceleration-structure-associated allocation through it.
                RetiredResourceBytes,
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
            _lastStaticInstanceCandidateCount = 0;
            _lastStaticInstanceResidentCount = 0;
            _lastStaticInstanceCulledCount = 0;
            _lastBlasEvictionCount = 0;
            _lastBlasEvictionBytes = 0;
            _lastBlasBudgetRejectedCount = 0;
        }

        private void RecalculateAccelerationStructureBytes()
        {
            ulong bytes = _tlas.Size;
            ulong bottomLevelBytes = 0;
            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
            {
                bottomLevelBytes = checked(bottomLevelBytes + blas.Size);
                bytes = checked(bytes + blas.Size);
            }
            BottomLevelAccelerationStructureBytes = bottomLevelBytes;
            AccelerationStructureBytes = bytes;
        }

        private void DestroyTopLevelAccelerationStructure(bool defer)
        {
            if (_tlas.Handle.Handle == 0)
                return;

            if (defer)
                RetireAccelerationStructureResource(_tlas.Handle, _tlas.StorageBuffer, _tlas.Size);
            else
                DestroyAccelerationStructureResource(_tlas.Handle, _tlas.StorageBuffer);
            _tlas = default;
            AdvanceResourceGeneration();
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
                    RetireAccelerationStructureResource(blas.Handle, blas.StorageBuffer, blas.Size);
                else
                    DestroyAccelerationStructureResource(blas.Handle, blas.StorageBuffer);
            }
            _blasCache.Clear();
            AdvanceResourceGeneration();
        }

        private void BeginFrameResourceRetirement()
        {
            _frameSerial++;
            DrainRetiredResources(force: false);
        }

        private void RetireAccelerationStructureResource(
            AccelerationStructureKHR accelerationStructure,
            BufferHandle storageBuffer,
            ulong size)
        {
            _retiredAccelerationStructures.Add(new RetiredAccelerationStructureResource(
                accelerationStructure,
                storageBuffer,
                size,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL));
            _retiredAccelerationStructureBytes = checked(_retiredAccelerationStructureBytes + size);
        }

        private void RetireBufferResource(BufferHandle buffer)
        {
            ulong size = 0;
            if (buffer.IsValid)
                size = _bufferManager.GetBufferSize(buffer);
            _retiredBuffers.Add(new RetiredBufferResource(
                buffer,
                size,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL));
            _retiredBufferBytes = SaturatingAdd(_retiredBufferBytes, size);
        }

        private void DrainRetiredResources(bool force)
        {
            for (int i = _retiredAccelerationStructures.Count - 1; i >= 0; i--)
            {
                RetiredAccelerationStructureResource retired = _retiredAccelerationStructures[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                DestroyAccelerationStructureResource(retired.AccelerationStructure, retired.StorageBuffer);
                _retiredAccelerationStructureBytes = _retiredAccelerationStructureBytes >= retired.Size
                    ? _retiredAccelerationStructureBytes - retired.Size
                    : 0;
                _retiredAccelerationStructures.RemoveAt(i);
            }

            for (int i = _retiredBuffers.Count - 1; i >= 0; i--)
            {
                RetiredBufferResource retired = _retiredBuffers[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                if (retired.Buffer.IsValid)
                    _bufferManager.DestroyBuffer(retired.Buffer);
                _retiredBufferBytes = _retiredBufferBytes >= retired.Size
                    ? _retiredBufferBytes - retired.Size
                    : 0;
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
            CoreMatrix4x4 WorldMatrix,
            AccelerationStructureGeometryDomain Domain = AccelerationStructureGeometryDomain.Static,
            GeometryInstanceFlagsKHR InstanceFlags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr);

        private sealed class BottomLevelAccelerationStructure
        {
            public BottomLevelAccelerationStructure(
                AccelerationStructureKHR handle,
                BufferHandle storageBuffer,
                ulong size)
            {
                Handle = handle;
                StorageBuffer = storageBuffer;
                Size = size;
            }

            public AccelerationStructureKHR Handle { get; }
            public BufferHandle StorageBuffer { get; }
            public ulong Size { get; }
            public ulong LastUsedFrameSerial { get; set; }
        }

        private readonly record struct StaticResidencyCandidate(
            StaticOpaqueInstance Instance,
            float DistanceSquared);

        private readonly record struct TopLevelAccelerationStructure(
            AccelerationStructureKHR Handle,
            BufferHandle StorageBuffer,
            ulong Size);

        private readonly record struct RetiredAccelerationStructureResource(
            AccelerationStructureKHR AccelerationStructure,
            BufferHandle StorageBuffer,
            ulong Size,
            ulong RetireAfterFrameSerial);

        private readonly record struct RetiredBufferResource(
            BufferHandle Buffer,
            ulong Size,
            ulong RetireAfterFrameSerial);
    }

    /// <summary>One acceleration-structure backing allocation used by ray queries.</summary>
    public readonly record struct AccelerationStructureStorageBuffer(
        BufferHandle Handle,
        ulong ByteSize,
        string DebugName);

    /// <summary>
    /// Bounds the streamable static portion of the GI ray-query representation.
    /// Dynamic render objects bypass distance trimming so their detailed geometry
    /// remains authoritative while they are present in the scene.
    /// </summary>
    public readonly record struct AccelerationStructureResidencyPolicy(
        bool Enabled,
        Njulf.Core.Math.Vector3 CameraPosition,
        ulong MemoryBudgetBytes,
        float StaticResidentDistance,
        int MaximumStaticInstances,
        int EvictionGraceFrames,
        bool AllowStaticMemoryCulling = true)
    {
        public static AccelerationStructureResidencyPolicy Disabled => new(
            false,
            Njulf.Core.Math.Vector3.Zero,
            ulong.MaxValue,
            float.MaxValue,
            int.MaxValue,
            0,
            false);

        internal ulong EffectiveMemoryBudgetBytes => Enabled
            ? Math.Max(MemoryBudgetBytes, 16UL)
            : ulong.MaxValue;

        internal ulong EffectiveTransientMemoryBudgetBytes => Enabled
            ? AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(EffectiveMemoryBudgetBytes)
            : ulong.MaxValue;

        internal ulong EffectiveScratchMemoryBudgetBytes => Enabled
            ? AccelerationStructureManager.CalculateScratchMemoryBudgetBytes(EffectiveMemoryBudgetBytes)
            : ulong.MaxValue;
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
        AlphaMaskTested = 1,
        ExcludedTransparent = 2,
        ExcludedGeometryDecal = 3,
        SkinnedBindPoseProxy = 4,
        FoliageProxyPending = 5,
        SkinnedAlphaMaskTestedProxy = 6,
        ThinSurfaceCandidateTested = 7
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
        int StaticInstanceCandidateCount,
        int StaticInstanceResidentCount,
        int StaticInstanceCulledCount,
        int BlasEvictionCount,
        ulong BlasEvictionBytes,
        int BlasBudgetRejectedCount,
        ulong BottomLevelAccelerationStructureBytes,
        ulong TopLevelAccelerationStructureBytes,
        ulong RetiredAccelerationStructureBytes,
        string FallbackReason);
}
