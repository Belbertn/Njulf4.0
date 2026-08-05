using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const ulong LegacyVoxelStride = sizeof(uint);
        private const ulong MaterialV2VoxelStride =
            FarFieldMaterialPayloadV2.VoxelStrideWords * sizeof(uint);
        // The far-field cache owns a fixed share of the tier cap while static
        // page-bake input has its own bounded reserve. Keeping this reserve out
        // of page-pool admission prevents large scene input from turning a hard
        // cache cap into an overrun.
        private const ulong InstanceInputBudgetDivisor = 8;
        private static readonly ulong ParamsSize = (ulong)Marshal.SizeOf<GPUFarFieldClipmapParams>();
        private static readonly ulong InstanceStride = (ulong)Marshal.SizeOf<GPUFarFieldInstance>();
        private static readonly ulong PageTableEntryStride = (ulong)Marshal.SizeOf<GPUFarFieldPageTableEntry>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly FenceBasedDeleter _deleter;
        private readonly SynchronizationManager _synchronizationManager;
        private readonly RenderSettings _settings;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly MaterialManager _materialManager;
        private readonly List<AccelerationStructureManager.StaticOpaqueInstance> _staticInstances = new();
        private readonly List<GPUFarFieldInstance> _gpuInstances = new();
        private readonly List<BoundingBox> _instanceBounds = new();
        // Parallel CPU-only revisions keep the shader-facing instance record compact while
        // allowing page invalidation to observe mesh/material content replacement.
        private readonly List<ulong> _instanceSourceRevisions = new();
        private readonly List<FarFieldPageBakeWork> _pageBakeQueue = new();
        private readonly List<int> _pageBakeInstanceIndexScratch = new();
        private readonly List<int[]> _rentedPageBakeInstanceIndexArrays = new();
        private readonly FarFieldPageCache _pageCache = new();

        private BufferHandle _paramsBuffer;
        private BufferHandle _voxelBuffer;
        private BufferHandle _bakeVoxelBuffer;
        private BufferHandle _distanceBuffer;
        private BufferHandle _jumpFloodScratch0Buffer;
        private BufferHandle _jumpFloodScratch1Buffer;
        private BufferHandle _instanceBuffer;
        private BufferHandle _pageTableBuffer;
        private ulong _voxelBufferBytes;
        private ulong _bakeVoxelBufferBytes;
        private ulong _distanceBufferBytes;
        private ulong _jumpFloodScratch0BufferBytes;
        private ulong _jumpFloodScratch1BufferBytes;
        private ulong _instanceBufferBytes;
        private ulong _pageTableBufferBytes;
        private BindlessHeap? _registeredBindlessHeap;
        private GPUFarFieldClipmapParams _lastParams;
        private ulong _lastSignature;
        private int _activeVoxelBufferIndex = BindlessIndex.FarFieldClipmapVoxelBuffer;
        private int _bakeVoxelBufferIndex = BindlessIndex.FarFieldClipmapBakeVoxelBuffer;
        private bool _distanceFieldValid;
        private Vector3 _clipmapOrigin;
        private bool _hasClipmapOrigin;
        private bool _bakePending;
        private bool _pagedMode;
        // The legacy clipmap may be reduced below the requested resolution to
        // honour the same hard far-field cache cap used by the paged path.
        // Keep the resolved value because bake dispatch and shader parameters
        // must agree with the allocation, not merely the user request.
        private int _legacyResolution = 1;
        private int _pageResolution;
        private int _pagePoolCapacity;
        private int _pageTableCapacity;
        private GPUFarFieldPageTableEntry[] _pageTableScratch = Array.Empty<GPUFarFieldPageTableEntry>();
        private ulong _pagingFrameSerial;
        private ulong _lastPagedSettingsSignature;
        private ulong _lastPagedSceneSignature;
        private ulong _lastPagedStableFrameSignature;
        private bool _hasPagedStableFrameSignature;
        private bool _pagedGpuStateDirty;
        private Scene? _staticInstanceScene;
        private ulong _staticInstanceSceneContentRevision;
        private bool _hasStaticInstanceSnapshot;
        private bool _hasPagedSceneSignature;
        // Latched at the start of Upload so allocation, shader parameters, and
        // bake dispatches cannot observe different ABIs if settings are edited
        // while a frame is being assembled.
        private bool _materialV2Enabled;
        private int _lastPageRequestCount;
        private int _lastPageMissCount;
        private int _lastPageRebuildCount;
        private int _lastPageEvictionCount;
        private int _lastScheduledPageBakeCount;
        private long _lastUploadMicroseconds;
        private bool _disposed;

        public FarFieldClipmapManager(
            VulkanContext context,
            BufferManager bufferManager,
            FenceBasedDeleter deleter,
            SynchronizationManager synchronizationManager,
            RenderSettings settings,
            AccelerationStructureManager accelerationStructureManager,
            MaterialManager materialManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
            _synchronizationManager = synchronizationManager ??
                throw new ArgumentNullException(nameof(synchronizationManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _materialV2Enabled =
                settings.GlobalIllumination.EffectiveGiFarFieldMaterialV2;

            _paramsBuffer = _bufferManager.CreateDeviceBuffer(
                Math.Max(MinBufferSize, ParamsSize),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: "Far Field Clipmap Params");
            EnsureVoxelCapacity(1);
            EnsureInstanceCapacity(0);
        }

        public int InstanceCount => _gpuInstances.Count;
        public int Resolution => _pagedMode ? Math.Max(_pageResolution, 1) : Math.Max(_legacyResolution, 1);
        public bool BakePending => _bakePending;
        /// <summary>
        /// True only while the requested paged representation has an allocated
        /// physical pool and far-field tracing is enabled.  A requested-but-disabled
        /// feature must remain a fallback in diagnostics rather than being reported
        /// as active with an empty pool.
        /// </summary>
        public bool PagedMode => _pagedMode &&
            _settings.GlobalIllumination.FarFieldPagedEnabled &&
            _settings.GlobalIllumination.FarFieldClipmapEnabled &&
            _pagePoolCapacity > 0;
        /// <summary>
        /// True only when far-field data is safe to use as authoritative ray
        /// coverage. Allocation alone is insufficient: paged data must finish
        /// every pending bake, and the legacy distance field must be published.
        /// </summary>
        public bool CoverageReady => _settings.GlobalIllumination.FarFieldClipmapEnabled &&
            (_pagedMode
                ? PagedMode && _pageCache.ResidentCount > 0 && _pageCache.PendingCount == 0
                : _lastParams.TraceParams.Z > 0.5f && !_bakePending && _distanceFieldValid);
        public int PagePoolCapacity => PagedMode ? _pagePoolCapacity : 0;
        public int ResidentPageCount => PagedMode ? _pageCache.ResidentCount : 0;
        public int PendingPageCount => PagedMode ? _pageCache.PendingCount : 0;
        public int PageEvictionCount => PagedMode ? _pageCache.EvictionCount : 0;
        public int PageBakeCount => _pagedMode ? _pageBakeQueue.Count : (_bakePending ? 1 : 0);
        public int PageRequestCount => _lastPageRequestCount;
        public int PageMissCount => _lastPageMissCount;
        public int PageRebuildCount => _lastPageRebuildCount;
        public int PageEvictionsThisFrame => _lastPageEvictionCount;
        public int ScheduledPageBakeCount => _lastScheduledPageBakeCount;
        public int StalePublicationRejectCount => _pageCache.StalePublicationRejectCount;
        public long LastUploadMicroseconds => _lastUploadMicroseconds;
        public bool MaterialV2Enabled => _materialV2Enabled;
        public uint MaterialPayloadVersion => MaterialV2Enabled
            ? FarFieldMaterialPayloadV2.PayloadVersion
            : 1u;
        public uint MaterialPayloadStrideWords => MaterialV2Enabled
            ? FarFieldMaterialPayloadV2.VoxelStrideWords
            : 1u;
        public int BakeVoxelBufferIndex => _pagedMode ? BindlessIndex.FarFieldClipmapVoxelBuffer : _bakeVoxelBufferIndex;
        public int DistanceBufferIndex => BindlessIndex.FarFieldClipmapDistanceBuffer;
        public int JumpFloodScratch0BufferIndex => BindlessIndex.FarFieldClipmapJumpFloodScratch0Buffer;
        public int JumpFloodScratch1BufferIndex => BindlessIndex.FarFieldClipmapJumpFloodScratch1Buffer;
        public int PageTableBufferIndex => BindlessIndex.FarFieldClipmapPageTableBuffer;
        public GPUFarFieldClipmapParams LastParams => _lastParams;
        public ulong BufferBytes => ParamsSize + _voxelBufferBytes + _bakeVoxelBufferBytes + _distanceBufferBytes + _jumpFloodScratch0BufferBytes + _jumpFloodScratch1BufferBytes + _instanceBufferBytes + _pageTableBufferBytes;
        public ulong PageCacheBytes =>
            _voxelBufferBytes +
            _bakeVoxelBufferBytes +
            _distanceBufferBytes +
            _jumpFloodScratch0BufferBytes +
            _jumpFloodScratch1BufferBytes +
            _pageTableBufferBytes;
        public ulong InstanceBufferBytes => _instanceBufferBytes;
        public ulong PageTableBufferBytes => _pageTableBufferBytes;
        // Allocation-level accessors used by the render graph. The active/bake indices are
        // descriptor indirections and can swap without changing which concrete Vulkan buffers
        // must be protected during a bake.
        public BufferHandle ParamsBuffer => _paramsBuffer;
        public BufferHandle VoxelBuffer => _voxelBuffer;
        public BufferHandle BakeVoxelBuffer => _pagedMode ? _voxelBuffer : _bakeVoxelBuffer;
        public BufferHandle DistanceBuffer => _distanceBuffer;
        public BufferHandle JumpFloodScratch0Buffer => _jumpFloodScratch0Buffer;
        public BufferHandle JumpFloodScratch1Buffer => _jumpFloodScratch1Buffer;
        public BufferHandle InstanceBuffer => _instanceBuffer;
        public BufferHandle PageTableBuffer => _pageTableBuffer;

        /// <summary>
        /// Returns whether two descriptor roles resolve to the same live buffer allocation.
        /// Generation participates in <see cref="BufferHandle"/> equality, so a recycled slot is
        /// never mistaken for the allocation it replaced.
        /// </summary>
        internal static bool SharesVoxelAllocation(BufferHandle first, BufferHandle second) =>
            first.IsValid && second.IsValid && first == second;

        public uint GetTriangleCount(int instanceIndex)
        {
            if ((uint)instanceIndex >= (uint)_gpuInstances.Count)
                return 0;

            return _gpuInstances[instanceIndex].IndexCount / 3u;
        }

        internal FarFieldPageBakeWork GetPageBakeWork(int index)
        {
            if ((uint)index >= (uint)_pageBakeQueue.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _pageBakeQueue[index];
        }

        internal uint GetPageVoxelOffset(FarFieldPageBakeRequest request)
        {
            ulong offset = checked((ulong)request.PhysicalPageIndex * GetPageVoxelCount(_pageResolution));
            return checked((uint)offset);
        }

        internal uint GetPageDistanceWordOffset(FarFieldPageBakeRequest request)
        {
            ulong voxelOffset = checked((ulong)request.PhysicalPageIndex * GetPageVoxelCount(_pageResolution));
            return checked((uint)(voxelOffset / 2UL));
        }

        public bool ConsumeBakePending()
        {
            bool pending = _bakePending;
            _bakePending = false;
            return pending;
        }

        public void MarkBakePending()
        {
            if (_pagedMode)
            {
                // A page cache never promotes a global rebake.  Advancing the
                // page source revision makes only requested resident pages stale
                // on their next scheduling pass.
                _lastPagedSettingsSignature = 0;
                _lastPagedSceneSignature = 0;
                return;
            }

            _bakePending = true;
            _distanceFieldValid = false;
        }

        public void MarkBakePublished()
        {
            if (_pagedMode)
            {
                // Paged bakes are completed individually so their generation
                // guards can reject a stale publication.  This overload remains
                // for the legacy single-cube path only.
                _bakePending = false;
                return;
            }

            (_activeVoxelBufferIndex, _bakeVoxelBufferIndex) = (_bakeVoxelBufferIndex, _activeVoxelBufferIndex);
            _distanceFieldValid = true;
            _lastParams.Diagnostics = new Vector4(_activeVoxelBufferIndex, _bakeVoxelBufferIndex, 0.0f, 0.0f);
            _lastParams.Reserved0 = new Vector4(DistanceBufferIndex, JumpFloodScratch0BufferIndex, JumpFloodScratch1BufferIndex, 1.0f);
        }

        internal void MarkPageBakePublished(FarFieldPageBakeRequest request)
        {
            if (!_pagedMode)
                throw new InvalidOperationException("Paged far-field publication was requested while the legacy clipmap is active.");

            _pageCache.MarkBakePublished(request);
            _pagedGpuStateDirty = true;
        }

        internal void MarkPageBakeFailed(FarFieldPageBakeRequest request)
        {
            if (_pagedMode)
            {
                _pageCache.MarkBakeFailed(request);
                _pagedGpuStateDirty = true;
            }
        }

        internal void CompletePagedBakeBatch()
        {
            ClearPageBakeQueue();
            _bakePending = false;
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.FarFieldClipmapParamsBuffer, _bufferManager.GetBuffer(_paramsBuffer), 0, Math.Max(MinBufferSize, ParamsSize));
            RegisterIfValid(BindlessIndex.FarFieldClipmapVoxelBuffer, _voxelBuffer, _voxelBufferBytes);
            RegisterIfValid(
                BindlessIndex.FarFieldClipmapBakeVoxelBuffer,
                _pagedMode ? _voxelBuffer : _bakeVoxelBuffer,
                _pagedMode ? _voxelBufferBytes : _bakeVoxelBufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapDistanceBuffer, _distanceBuffer, _distanceBufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapJumpFloodScratch0Buffer, _jumpFloodScratch0Buffer, _jumpFloodScratch0BufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapJumpFloodScratch1Buffer, _jumpFloodScratch1Buffer, _jumpFloodScratch1BufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapInstanceBuffer, _instanceBuffer, _instanceBufferBytes);
            RegisterIfValid(BindlessIndex.FarFieldClipmapPageTableBuffer, _pageTableBuffer, _pageTableBufferBytes);
        }

        public void Upload(Scene scene, Vector3 cameraPosition, StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            // Preserve the public compatibility overload for integrations that do
            // not yet provide a scene revision. A zero revision deliberately
            // refreshes the static snapshot, which is safe but less efficient.
            Upload(scene, cameraPosition, stagingRing, commandBuffer, sceneContentRevision: 0);
        }

        /// <summary>
        /// Updates the far-field cache using a stable scene-content revision.
        /// Stable frames reuse their static instance CPU snapshot and GPU buffer;
        /// camera movement then only updates the bounded page working set.
        /// </summary>
        public void Upload(
            Scene scene,
            Vector3 cameraPosition,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            ulong sceneContentRevision)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));

            long uploadStart = Stopwatch.GetTimestamp();
            try
            {
                GlobalIlluminationSettings gi = _settings.GlobalIllumination;
                _materialV2Enabled = gi.EffectiveGiFarFieldMaterialV2;
                ResetPageFrameStats();
                if (gi.FarFieldPagedEnabled)
                {
                    UploadPaged(scene, cameraPosition, stagingRing, commandBuffer, gi, sceneContentRevision);
                    return;
                }

                if (!gi.FarFieldClipmapEnabled)
                {
                    // A disabled legacy clipmap must not allocate/rebuild a scene-sized
                    // cache merely because Simple DDGI remains active. Publish an
                    // explicit disabled parameter block so old valid pages cannot be
                    // sampled after a runtime toggle.
                    _pagedMode = false;
                    _hasPagedStableFrameSignature = false;
                    _bakePending = false;
                    _distanceFieldValid = false;
                    _hasClipmapOrigin = false;
                    _lastSignature = 0;
                    _legacyResolution = 1;
                    _lastParams = CreateDisabledLegacyParams(gi, cameraPosition);
                    UploadParams(stagingRing, commandBuffer);
                    return;
                }

                _pagedMode = false;
                _hasPagedStableFrameSignature = false;
                int resolution = ResolveLegacyClipmapResolution(gi);
                _legacyResolution = resolution;
                EnsureVoxelCapacity(resolution);

                EnsureStaticInstances(scene, sceneContentRevision, stagingRing, commandBuffer);

                BoundingBox bounds = ExpandBounds(SimpleDdgiSceneBounds.Estimate(scene), gi.SimpleDdgiProbeSpacing * 2.0f);
                Vector3 extent = bounds.Max - bounds.Min;
                float maxExtent = MathF.Max(MathF.Max(extent.X, extent.Y), extent.Z);
                float voxelSize = MathF.Max(maxExtent / Math.Max(1, resolution), 0.001f);
                float cubicExtent = voxelSize * resolution;
                _clipmapOrigin = ResolveSceneClampedOrigin(bounds.Min, bounds.Max, cubicExtent, voxelSize, cameraPosition, _clipmapOrigin, ref _hasClipmapOrigin, out bool recentered);
                if (recentered)
                {
                    _bakePending = true;
                    _distanceFieldValid = false;
                }

                ulong signature = CreateSignature(
                    resolution,
                    new BoundingBox(_clipmapOrigin, _clipmapOrigin + new Vector3(cubicExtent)),
                    _gpuInstances,
                    _instanceSourceRevisions,
                    MaterialPayloadVersion);
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    _bakePending = true;
                    _distanceFieldValid = false;
                }

                _lastParams = new GPUFarFieldClipmapParams
                {
                    OriginAndVoxelSize = new Vector4(_clipmapOrigin.X, _clipmapOrigin.Y, _clipmapOrigin.Z, voxelSize),
                    ResolutionAndExtent = new Vector4(resolution, resolution, resolution, cubicExtent),
                    TraceParams = new Vector4(gi.FarFieldStartDistance, gi.FarFieldMaxTraceSteps, gi.FarFieldClipmapEnabled ? 1.0f : 0.0f, gi.FarFieldForceAll ? 1.0f : 0.0f),
                    BakeParams = new Vector4(_gpuInstances.Count, 0.0f, 0.0f, 0.0f),
                    Diagnostics = new Vector4(_activeVoxelBufferIndex, _bakeVoxelBufferIndex, _bakePending ? 1.0f : 0.0f, 0.0f),
                    Reserved0 = new Vector4(DistanceBufferIndex, JumpFloodScratch0BufferIndex, JumpFloodScratch1BufferIndex, _distanceFieldValid ? 1.0f : 0.0f),
                    MaterialPayload = CreateMaterialPayloadParams()
                };

                GpuBufferUploader.UploadValueToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _paramsBuffer,
                    _lastParams,
                    barrierDescription: new UploadBarrierDescription(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit));
            }
            finally
            {
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
            }
        }

        /// <summary>
        /// Schedules a bounded, world-keyed far-field cache.  The physical page
        /// pool is deliberately independent of scene extent: travelling through a
        /// ten kilometre world changes virtual keys, not allocation size.
        /// </summary>
        private void UploadPaged(
            Scene scene,
            Vector3 cameraPosition,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            GlobalIlluminationSettings gi,
            ulong sceneContentRevision)
        {
            _pagedMode = true;

            if (!gi.FarFieldClipmapEnabled)
            {
                _hasPagedStableFrameSignature = false;
                ClearPageBakeQueue();
                _bakePending = false;
                _lastParams = CreateDisabledPagedParams(gi, cameraPosition);
                UploadParams(stagingRing, commandBuffer);
                return;
            }

            int pageResolution = ResolvePagedPageResolution(gi);
            int pagePoolCapacity = ResolvePagedPageCapacity(gi, pageResolution);
            if (pagePoolCapacity <= 0)
            {
                _hasPagedStableFrameSignature = false;
                // Keep the descriptor-safe disabled parameter block, but do not
                // pretend a page cache exists when the tier cannot fund even one
                // physical page at the resolved resolution.
                ClearPageBakeQueue();
                _pageCache.Clear();
                _pageResolution = pageResolution;
                _pagePoolCapacity = 0;
                _pageTableCapacity = 0;
                _bakePending = false;
                _lastParams = CreateDisabledPagedParams(gi, cameraPosition);
                UploadParams(stagingRing, commandBuffer);
                return;
            }
            ulong settingsSignature = CreatePagedSettingsSignature(gi, pageResolution, pagePoolCapacity);
            bool pageLayoutChanged = _pageResolution != pageResolution || _pagePoolCapacity != pagePoolCapacity;
            bool settingsChanged = pageLayoutChanged ||
                settingsSignature != _lastPagedSettingsSignature;

            _pageResolution = pageResolution;
            _pagePoolCapacity = pagePoolCapacity;
            EnsurePagedCapacity(pageResolution, pagePoolCapacity);
            if (_pageCache.Capacity != pagePoolCapacity)
                _pageCache.Configure(pagePoolCapacity);
            if (settingsChanged)
            {
                _pageCache.Clear();
                ClearPageBakeQueue();
                _lastPagedSettingsSignature = settingsSignature;
                _lastPagedSceneSignature = 0;
                _hasPagedSceneSignature = false;
                _hasPagedStableFrameSignature = false;
                _pagedGpuStateDirty = true;
            }

            _pageTableCapacity = _pageCache.RequiredGpuTableCapacity;
            if (_pageTableScratch.Length != _pageTableCapacity)
                _pageTableScratch = new GPUFarFieldPageTableEntry[_pageTableCapacity];

            bool staticInstancesChanged = EnsureStaticInstances(
                scene,
                sceneContentRevision,
                stagingRing,
                commandBuffer);
            bool sceneStateChanged = staticInstancesChanged ||
                !_hasPagedSceneSignature;
            if (sceneStateChanged)
            {
                ulong sceneSignature = CreatePagedSceneSignature(
                    _gpuInstances,
                    _instanceBounds,
                    _instanceSourceRevisions);
                _lastPagedSceneSignature = sceneSignature;
                _hasPagedSceneSignature = true;
            }

            ulong stableFrameSignature = CreatePagedStableFrameSignature(
                gi,
                cameraPosition,
                settingsSignature,
                _lastPagedSceneSignature,
                _gpuInstances.Count,
                _pageTableCapacity);
            bool stableGpuState =
                _hasPagedStableFrameSignature &&
                stableFrameSignature == _lastPagedStableFrameSignature &&
                !settingsChanged &&
                !sceneStateChanged &&
                !_pagedGpuStateDirty &&
                !_bakePending &&
                _pageBakeQueue.Count == 0 &&
                _pageCache.PendingCount == 0;
            if (stableGpuState)
            {
                // No camera, settings, static-scene, residency, or publication
                // state changed. The immutable page table and params buffer are
                // already resident, so avoid rebuilding and uploading both on
                // every settled frame.
                return;
            }

            _pageCache.BeginFrame(AdvancePagingFrameSerial());

            int evictionCountBeforeRequests = _pageCache.EvictionCount;
            RequestCameraPages(cameraPosition, gi, settingsSignature);
            _lastPageEvictionCount = Math.Max(0, _pageCache.EvictionCount - evictionCountBeforeRequests);
            // Bake selection happens after every request is resident in the CPU
            // cache.  The table is built twice: first to establish stable
            // open-addressed locations, then after selections to upload exactly
            // the invalid/baking state consumed by the compute pass.
            _pageCache.BuildGpuTable(_pageTableScratch);
            ClearPageBakeQueue();
            int maxPageBakes = Math.Min(gi.FarFieldPageUpdatesPerFrame, _pagePoolCapacity);
            for (int bake = 0; bake < maxPageBakes && _pageCache.TryBeginBake(out FarFieldPageBakeRequest request); bake++)
            {
                request = _pageCache.WithGpuTableEntryIndex(request);
                if (request.GpuTableEntryIndex < 0)
                {
                    _pageCache.MarkBakeFailed(request);
                    break;
                }

                FarFieldPageBakeInstanceIndices pageInstances = BuildPageBakeInstanceIndices(request.Key, gi);
                _pageBakeQueue.Add(new FarFieldPageBakeWork(
                    request,
                    pageInstances.Indices,
                    pageInstances.Count));
            }
            _lastScheduledPageBakeCount = _pageBakeQueue.Count;

            _pageCache.BuildGpuTable(_pageTableScratch);
            UploadPageTable(stagingRing, commandBuffer);

            _bakePending = _pageBakeQueue.Count > 0;
            _lastParams = CreatePagedParams(gi, cameraPosition);
            UploadParams(stagingRing, commandBuffer);
            _lastPagedStableFrameSignature = stableFrameSignature;
            _hasPagedStableFrameSignature = true;
            _pagedGpuStateDirty = false;
        }

        private bool EnsureStaticInstances(
            Scene scene,
            ulong sceneContentRevision,
            StagingRing stagingRing,
            CommandBuffer commandBuffer)
        {
            bool sameScene = ReferenceEquals(scene, _staticInstanceScene);
            bool snapshotRefreshRequired = ShouldRefreshStaticInstanceSnapshot(
                _hasStaticInstanceSnapshot,
                sameScene,
                _staticInstanceSceneContentRevision,
                sceneContentRevision);
            if (!snapshotRefreshRequired && !HasStaticMaterialRevisionChanges())
            {
                return false;
            }

            _accelerationStructureManager.CollectStaticOpaqueInstances(scene, _staticInstances);
            _gpuInstances.Clear();
            _instanceBounds.Clear();
            _instanceSourceRevisions.Clear();
            ulong primitiveKeyBase = 0;
            foreach (AccelerationStructureManager.StaticOpaqueInstance instance in _staticInstances)
            {
                // The paged far field is a static streamed representation. Dynamic
                // render objects stay authoritative in the near/mid TLAS and are
                // intentionally excluded so a stale coarse page can never become a
                // second, conflicting representation of moving geometry.
                if (instance.Domain != AccelerationStructureGeometryDomain.Static)
                    continue;

                uint triangleCount = instance.MeshInfo.IndexCount / 3u;
                if (primitiveKeyBase + triangleCount > uint.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The far-field V2 primitive-key domain exceeded its 32-bit deterministic resolve capacity.");
                }

                MaterialAspectRevisions materialRevisions =
                    _materialManager.GetMaterialAspectRevisions(
                        unchecked((int)instance.MaterialIndex));
                uint transportProfileRevision =
                    _materialManager.GetMaterialTransportProfileRevision(
                        unchecked((int)instance.MaterialIndex));
                _gpuInstances.Add(new GPUFarFieldInstance
                {
                    VertexOffset = instance.MeshInfo.VertexOffset,
                    IndexOffset = instance.MeshInfo.IndexOffset,
                    IndexCount = instance.MeshInfo.IndexCount,
                    MaterialIndex = instance.MaterialIndex,
                    World = instance.WorldMatrix,
                    PrimitiveKeyBase = checked((uint)primitiveKeyBase),
                    MaterialRevision = materialRevisions.Material,
                    FarFieldRevision = materialRevisions.FarField,
                    Reserved0 = transportProfileRevision
                });
                _instanceSourceRevisions.Add(
                    CreateInstanceSourceRevision(
                        instance,
                        materialRevisions,
                        transportProfileRevision));
                primitiveKeyBase += triangleCount;

                BoundingBox localBounds = new(
                    new Vector3(instance.MeshInfo.BoundingBoxMin.X, instance.MeshInfo.BoundingBoxMin.Y, instance.MeshInfo.BoundingBoxMin.Z),
                    new Vector3(instance.MeshInfo.BoundingBoxMax.X, instance.MeshInfo.BoundingBoxMax.Y, instance.MeshInfo.BoundingBoxMax.Z));
                _instanceBounds.Add(BoundingBox.Transform(localBounds, instance.WorldMatrix));
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

            _staticInstanceScene = scene;
            _staticInstanceSceneContentRevision = sceneContentRevision;
            _hasStaticInstanceSnapshot = true;
            return true;
        }

        private bool HasStaticMaterialRevisionChanges()
        {
            for (int i = 0; i < _gpuInstances.Count; i++)
            {
                GPUFarFieldInstance instance = _gpuInstances[i];
                MaterialAspectRevisions current =
                    _materialManager.GetMaterialAspectRevisions(
                        unchecked((int)instance.MaterialIndex));
                uint currentTransportProfileRevision =
                    _materialManager.GetMaterialTransportProfileRevision(
                        unchecked((int)instance.MaterialIndex));
                // MaterialRevision is bake-time provenance. Only the far-field
                // aspect is a semantic invalidation gate; raster-only edits must
                // not fan out into page rebuilds.
                if (current.FarField != instance.FarFieldRevision ||
                    currentTransportProfileRevision != instance.Reserved0)
                    return true;
            }

            return false;
        }

        internal static bool ShouldRefreshStaticInstanceSnapshot(
            bool hasSnapshot,
            bool sameScene,
            ulong previousSceneContentRevision,
            ulong sceneContentRevision)
        {
            return !hasSnapshot ||
                !sameScene ||
                sceneContentRevision == 0 ||
                sceneContentRevision != previousSceneContentRevision;
        }

        private void RequestCameraPages(
            Vector3 cameraPosition,
            GlobalIlluminationSettings gi,
            ulong settingsSignature)
        {
            int radius = gi.FarFieldPageRequestRadius;
            for (int cascade = 0; cascade < gi.FarFieldCascadeCount; cascade++)
            {
                float voxelSize = GetCascadeVoxelSize(cascade, gi);
                float pageExtent = Math.Max(voxelSize * _pageResolution, voxelSize);
                int centerX = FloorToInt(cameraPosition.X / pageExtent);
                int centerY = FloorToInt(cameraPosition.Y / pageExtent);
                int centerZ = FloorToInt(cameraPosition.Z / pageExtent);
                int cascadePriority = (gi.FarFieldCascadeCount - cascade) * 1_000_000;

                for (int z = -radius; z <= radius; z++)
                    for (int y = -radius; y <= radius; y++)
                        for (int x = -radius; x <= radius; x++)
                        {
                            int manhattan = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
                            FarFieldPageKey key = new(cascade, centerX + x, centerY + y, centerZ + z);
                            _lastPageRequestCount++;
                            bool wasResident = _pageCache.IsResident(key);
                            bool hasCachedRevision = _pageCache.TryGetSourceRevision(key, out ulong cachedRevision);
                            bool validationCurrent =
                                _pageCache.TryGetValidationRevision(key, out ulong validationRevision) &&
                                validationRevision == _lastPagedSceneSignature;
                            ulong sourceRevision;
                            if (hasCachedRevision && validationCurrent)
                            {
                                sourceRevision = cachedRevision;
                            }
                            else
                            {
                                sourceRevision = CreatePagedPageSignature(key, gi, settingsSignature);
                            }

                            if (!wasResident)
                                _lastPageMissCount++;
                            if (!hasCachedRevision || cachedRevision != sourceRevision)
                                _lastPageRebuildCount++;

                            // Near cascades win first, then pages closest to the camera.
                            // The stable coordinate tie-break in FarFieldPageCache prevents
                            // allocation order from changing the selected working set.
                            int priority = cascadePriority + Math.Max(0, radius * 3 - manhattan) * 1_000;
                            _pageCache.Request(
                                key,
                                sourceRevision,
                                priority,
                                validationRevision: _lastPagedSceneSignature);
                        }
            }
        }

        private void ResetPageFrameStats()
        {
            _lastPageRequestCount = 0;
            _lastPageMissCount = 0;
            _lastPageRebuildCount = 0;
            _lastPageEvictionCount = 0;
            _lastScheduledPageBakeCount = 0;
        }

        private FarFieldPageBakeInstanceIndices BuildPageBakeInstanceIndices(FarFieldPageKey key, GlobalIlluminationSettings gi)
        {
            BoundingBox pageBounds = GetPageBounds(key, gi);
            _pageBakeInstanceIndexScratch.Clear();
            for (int i = 0; i < _instanceBounds.Count; i++)
            {
                if (!_instanceBounds[i].Intersects(pageBounds))
                    continue;

                _pageBakeInstanceIndexScratch.Add(i);
            }

            int count = _pageBakeInstanceIndexScratch.Count;
            if (count == 0)
                return new FarFieldPageBakeInstanceIndices(Array.Empty<int>(), 0);

            // Page bakes are queued until the graph pass consumes them, so each
            // work item owns a short-lived pooled array.  Returning them as one
            // batch after execution eliminates page-motion GC churn without
            // retaining a full-world candidate array per resident page.
            int[] indices = ArrayPool<int>.Shared.Rent(count);
            CollectionsMarshal.AsSpan(_pageBakeInstanceIndexScratch).CopyTo(indices.AsSpan(0, count));
            _rentedPageBakeInstanceIndexArrays.Add(indices);
            return new FarFieldPageBakeInstanceIndices(indices, count);
        }

        private void ClearPageBakeQueue()
        {
            _pageBakeQueue.Clear();
            foreach (int[] indices in _rentedPageBakeInstanceIndexArrays)
                ArrayPool<int>.Shared.Return(indices, clearArray: false);
            _rentedPageBakeInstanceIndexArrays.Clear();
        }

        private readonly record struct FarFieldPageBakeInstanceIndices(int[] Indices, int Count);

        private GPUFarFieldClipmapParams CreatePagedParams(GlobalIlluminationSettings gi, Vector3 cameraPosition)
        {
            float voxelSize = GetCascadeVoxelSize(0, gi);
            float pageExtent = Math.Max(voxelSize * _pageResolution, voxelSize);
            FarFieldPageKey cameraPage = new(
                0,
                FloorToInt(cameraPosition.X / pageExtent),
                FloorToInt(cameraPosition.Y / pageExtent),
                FloorToInt(cameraPosition.Z / pageExtent));
            Vector3 origin = GetPageBounds(cameraPage, gi).Min;

            return new GPUFarFieldClipmapParams
            {
                OriginAndVoxelSize = new Vector4(origin.X, origin.Y, origin.Z, voxelSize),
                ResolutionAndExtent = new Vector4(_pageResolution, _pageResolution, _pageResolution, pageExtent),
                TraceParams = new Vector4(gi.FarFieldStartDistance, gi.FarFieldMaxTraceSteps, 1.0f, gi.FarFieldForceAll ? 1.0f : 0.0f),
                BakeParams = new Vector4(_gpuInstances.Count, _pageBakeQueue.Count, 0.0f, 0.0f),
                Diagnostics = new Vector4(BindlessIndex.FarFieldClipmapVoxelBuffer, BindlessIndex.FarFieldClipmapVoxelBuffer, _bakePending ? 1.0f : 0.0f, 0.0f),
                Reserved0 = new Vector4(DistanceBufferIndex, JumpFloodScratch0BufferIndex, JumpFloodScratch1BufferIndex, 0.0f),
                PagingParams = new Vector4(PageTableBufferIndex, _pageTableCapacity, _pagePoolCapacity, gi.FarFieldCascadeCount),
                PagingLayout = new Vector4(_pageResolution, gi.FarFieldBaseVoxelSize, gi.FarFieldCascadeVoxelScale, 1.0f),
                CameraAndBakePage = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, -1.0f),
                MaterialPayload = CreateMaterialPayloadParams()
            };
        }

        private GPUFarFieldClipmapParams CreateDisabledPagedParams(GlobalIlluminationSettings gi, Vector3 cameraPosition)
        {
            int resolution = Math.Max(_pageResolution, 1);
            float voxelSize = Math.Max(gi.FarFieldBaseVoxelSize, 0.0001f);
            return new GPUFarFieldClipmapParams
            {
                OriginAndVoxelSize = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, voxelSize),
                ResolutionAndExtent = new Vector4(resolution, resolution, resolution, voxelSize * resolution),
                TraceParams = new Vector4(gi.FarFieldStartDistance, gi.FarFieldMaxTraceSteps, 0.0f, gi.FarFieldForceAll ? 1.0f : 0.0f),
                Diagnostics = new Vector4(BindlessIndex.FarFieldClipmapVoxelBuffer, BindlessIndex.FarFieldClipmapVoxelBuffer, 0.0f, 0.0f),
                Reserved0 = new Vector4(DistanceBufferIndex, JumpFloodScratch0BufferIndex, JumpFloodScratch1BufferIndex, 0.0f),
                PagingParams = new Vector4(PageTableBufferIndex, _pageTableCapacity, _pagePoolCapacity, gi.FarFieldCascadeCount),
                PagingLayout = new Vector4(resolution, gi.FarFieldBaseVoxelSize, gi.FarFieldCascadeVoxelScale, 1.0f),
                CameraAndBakePage = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, -1.0f),
                MaterialPayload = CreateMaterialPayloadParams()
            };
        }

        private GPUFarFieldClipmapParams CreateDisabledLegacyParams(GlobalIlluminationSettings gi, Vector3 cameraPosition)
        {
            int resolution = Math.Max(_legacyResolution, 1);
            float voxelSize = Math.Max(gi.FarFieldBaseVoxelSize, 0.0001f);
            return new GPUFarFieldClipmapParams
            {
                OriginAndVoxelSize = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, voxelSize),
                ResolutionAndExtent = new Vector4(resolution, resolution, resolution, voxelSize * resolution),
                TraceParams = new Vector4(gi.FarFieldStartDistance, gi.FarFieldMaxTraceSteps, 0.0f, gi.FarFieldForceAll ? 1.0f : 0.0f),
                Diagnostics = new Vector4(_activeVoxelBufferIndex, _bakeVoxelBufferIndex, 0.0f, 0.0f),
                Reserved0 = new Vector4(DistanceBufferIndex, JumpFloodScratch0BufferIndex, JumpFloodScratch1BufferIndex, 0.0f),
                PagingParams = Vector4.Zero,
                PagingLayout = Vector4.Zero,
                CameraAndBakePage = new Vector4(cameraPosition.X, cameraPosition.Y, cameraPosition.Z, -1.0f),
                MaterialPayload = CreateMaterialPayloadParams()
            };
        }

        private Vector4 CreateMaterialPayloadParams() =>
            new(MaterialPayloadVersion, MaterialPayloadStrideWords, 0.0f, 0.0f);

        private void UploadParams(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _paramsBuffer,
                _lastParams,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        private void UploadPageTable(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (_pageTableScratch.Length == 0 || !_pageTableBuffer.IsValid)
                return;

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _pageTableBuffer,
                _pageTableScratch,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        private void EnsurePagedCapacity(int resolution, int pagePoolCapacity)
        {
            ulong pageCacheBudgetBytes = ResolveFarFieldPageCacheBudgetBytes(
                _settings.GlobalIllumination.FarFieldMemoryBudgetBytes);
            if (CalculatePagedCacheBytes(resolution, pagePoolCapacity, MaterialV2Enabled) > pageCacheBudgetBytes)
            {
                throw new InvalidOperationException(
                    "The resolved far-field page pool exceeds its tier cache budget before allocation.");
            }

            ulong pageVoxelCount = GetPageVoxelCount(resolution);
            ulong poolVoxelCount = checked(pageVoxelCount * (ulong)pagePoolCapacity);
            ulong voxelBytes = Math.Max(
                MinBufferSize,
                checked(poolVoxelCount * GetVoxelStrideBytes(MaterialV2Enabled)));
            ulong distanceBytes = Math.Max(MinBufferSize, checked(((poolVoxelCount + 1UL) / 2UL) * sizeof(uint)));
            // Jump-flood scratch is deliberately one page, not one allocation per
            // resident page: page bakes execute serially inside the bounded update
            // budget and reuse this workspace.
            ulong scratchBytes = Math.Max(MinBufferSize, checked(pageVoxelCount * sizeof(uint)));
            int tableCapacity = NextPowerOfTwo(Math.Max(2, pagePoolCapacity * 2));
            ulong tableBytes = Math.Max(MinBufferSize, checked((ulong)tableCapacity * PageTableEntryStride));

            ResizePagedBuffer(ref _voxelBuffer, ref _voxelBufferBytes, voxelBytes, "Far Field Page Pool Voxels");
            if (_bakeVoxelBuffer.IsValid)
            {
                RetireReplacedBuffer(_bakeVoxelBuffer);
                _bakeVoxelBuffer = BufferHandle.Invalid;
                _bakeVoxelBufferBytes = 0;
            }

            ResizePagedBuffer(ref _distanceBuffer, ref _distanceBufferBytes, distanceBytes, "Far Field Page Pool Distance Field R16");
            ResizePagedBuffer(ref _jumpFloodScratch0Buffer, ref _jumpFloodScratch0BufferBytes, scratchBytes, "Far Field Page Jump Flood Scratch 0");
            ResizePagedBuffer(ref _jumpFloodScratch1Buffer, ref _jumpFloodScratch1BufferBytes, scratchBytes, "Far Field Page Jump Flood Scratch 1");
            ResizePagedBuffer(ref _pageTableBuffer, ref _pageTableBufferBytes, tableBytes, "Far Field Page Table");
            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
        }

        internal static int ResolveLegacyClipmapResolution(GlobalIlluminationSettings gi)
        {
            if (gi == null)
                throw new ArgumentNullException(nameof(gi));

            ulong pageCacheBudgetBytes = ResolveFarFieldPageCacheBudgetBytes(gi.FarFieldMemoryBudgetBytes);
            int resolution = gi.FarFieldClipmapResolution;
            while (resolution > 16 &&
                   CalculateLegacyCacheBytes(
                       resolution,
                       gi.EffectiveGiFarFieldMaterialV2) >
                   pageCacheBudgetBytes)
                resolution = Math.Max(16, resolution / 2);
            return resolution;
        }

        internal static int ResolvePagedPageResolution(GlobalIlluminationSettings gi)
        {
            if (gi == null)
                throw new ArgumentNullException(nameof(gi));

            ulong pageCacheBudgetBytes = ResolveFarFieldPageCacheBudgetBytes(gi.FarFieldMemoryBudgetBytes);
            int resolution = gi.FarFieldPageResolution;
            while (resolution > 16 &&
                   CalculatePagedCacheBytes(
                       resolution,
                       1,
                       gi.EffectiveGiFarFieldMaterialV2) >
                   pageCacheBudgetBytes)
                resolution = Math.Max(16, resolution / 2);
            return resolution;
        }

        internal static int ResolvePagedPageCapacity(GlobalIlluminationSettings gi, int resolution)
        {
            if (gi == null)
                throw new ArgumentNullException(nameof(gi));

            ulong pageCacheBudgetBytes = ResolveFarFieldPageCacheBudgetBytes(gi.FarFieldMemoryBudgetBytes);
            if (CalculatePagedCacheBytes(
                    resolution,
                    1,
                    gi.EffectiveGiFarFieldMaterialV2) >
                pageCacheBudgetBytes)
                return 0;

            int capacity = gi.FarFieldResidentPageBudget;
            while (capacity > 1 &&
                   CalculatePagedCacheBytes(
                       resolution,
                       capacity,
                       gi.EffectiveGiFarFieldMaterialV2) >
                   pageCacheBudgetBytes)
                capacity--;
            return capacity;
        }

        /// <summary>
        /// Static page-bake source data is bounded separately from physical page
        /// cache residency. This reserve also protects the small persistent
        /// parameter allocation used by every far-field mode.
        /// </summary>
        internal static ulong ResolveFarFieldInstanceInputBudgetBytes(ulong totalBudgetBytes)
        {
            if (totalBudgetBytes == 0)
                return 0;

            return Math.Max(MinBufferSize, totalBudgetBytes / InstanceInputBudgetDivisor);
        }

        internal static ulong ResolveFarFieldPageCacheBudgetBytes(ulong totalBudgetBytes)
        {
            ulong instanceInputBudgetBytes = ResolveFarFieldInstanceInputBudgetBytes(totalBudgetBytes);
            ulong reservedBytes = SaturatingAdd(ParamsSize, instanceInputBudgetBytes);
            return totalBudgetBytes > reservedBytes
                ? totalBudgetBytes - reservedBytes
                : 0;
        }

        /// <summary>
        /// Bytes in the page pool, packed distance field, reusable jump-flood
        /// workspace, and GPU page table.  Instance data is deliberately reported
        /// separately because it is scene input rather than page-cache residency.
        /// </summary>
        internal static ulong CalculatePagedCacheBytes(int resolution, int capacity)
        {
            return CalculatePagedCacheBytes(resolution, capacity, materialV2Enabled: false);
        }

        internal static ulong CalculatePagedCacheBytes(
            int resolution,
            int capacity,
            bool materialV2Enabled)
        {
            ulong pageVoxelCount = GetPageVoxelCount(resolution);
            ulong poolVoxelCount = checked(pageVoxelCount * (ulong)Math.Max(capacity, 1));
            ulong voxelBytes = checked(poolVoxelCount * GetVoxelStrideBytes(materialV2Enabled));
            ulong distanceBytes = checked(((poolVoxelCount + 1UL) / 2UL) * sizeof(uint));
            ulong scratchBytes = checked(pageVoxelCount * sizeof(uint) * 2UL);
            ulong tableBytes = checked((ulong)NextPowerOfTwo(Math.Max(2, capacity * 2)) * PageTableEntryStride);
            return checked(voxelBytes + distanceBytes + scratchBytes + tableBytes);
        }

        /// <summary>
        /// Legacy clipmap allocation footprint.  The active and bake voxel buffers
        /// are distinct; the distance field and two jump-flood work buffers are
        /// shared by that double-buffered transaction.
        /// </summary>
        internal static ulong CalculateLegacyCacheBytes(int resolution)
        {
            return CalculateLegacyCacheBytes(resolution, materialV2Enabled: false);
        }

        internal static ulong CalculateLegacyCacheBytes(int resolution, bool materialV2Enabled)
        {
            ulong voxelCount = GetPageVoxelCount(resolution);
            ulong voxelBytes = Math.Max(
                MinBufferSize,
                checked(voxelCount * GetVoxelStrideBytes(materialV2Enabled)));
            ulong distanceBytes = Math.Max(MinBufferSize, checked(((voxelCount + 1UL) / 2UL) * sizeof(uint)));
            ulong scratchBytes = Math.Max(MinBufferSize, checked(voxelCount * sizeof(uint)));
            return checked(voxelBytes * 2UL + distanceBytes + scratchBytes * 2UL);
        }

        internal static ulong GetVoxelStrideBytes(bool materialV2Enabled) =>
            materialV2Enabled ? MaterialV2VoxelStride : LegacyVoxelStride;

        private static ulong GetPageVoxelCount(int resolution)
        {
            ulong side = (ulong)Math.Max(1, resolution);
            return checked(side * side * side);
        }

        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value && result < 1 << 30)
                result <<= 1;
            return result;
        }

        private static ulong SaturatingAdd(ulong left, ulong right) =>
            ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private BoundingBox GetPageBounds(FarFieldPageKey key, GlobalIlluminationSettings gi)
        {
            float voxelSize = GetCascadeVoxelSize(key.Cascade, gi);
            float extent = Math.Max(voxelSize * _pageResolution, voxelSize);
            Vector3 min = new(key.X * extent, key.Y * extent, key.Z * extent);
            return new BoundingBox(min, min + new Vector3(extent));
        }

        private static float GetCascadeVoxelSize(int cascade, GlobalIlluminationSettings gi)
        {
            return Math.Max(gi.FarFieldBaseVoxelSize * MathF.Pow(gi.FarFieldCascadeVoxelScale, Math.Max(cascade, 0)), 0.0001f);
        }

        private ulong CreatePagedPageSignature(FarFieldPageKey key, GlobalIlluminationSettings gi, ulong settingsSignature)
        {
            BoundingBox pageBounds = GetPageBounds(key, gi);
            ulong hash = settingsSignature;
            hash = HashAdd(hash, unchecked((uint)key.Cascade));
            hash = HashAdd(hash, unchecked((uint)key.X));
            hash = HashAdd(hash, unchecked((uint)key.Y));
            hash = HashAdd(hash, unchecked((uint)key.Z));
            for (int i = 0; i < _gpuInstances.Count; i++)
            {
                if (!_instanceBounds[i].Intersects(pageBounds))
                    continue;

                GPUFarFieldInstance instance = _gpuInstances[i];
                hash = HashAdd(hash, instance.VertexOffset);
                hash = HashAdd(hash, instance.IndexOffset);
                hash = HashAdd(hash, instance.IndexCount);
                hash = HashAdd(hash, instance.MaterialIndex);
                hash = HashAdd(hash, instance.World);
                hash = HashAdd(hash, instance.PrimitiveKeyBase);
                hash = HashAdd(hash, instance.FarFieldRevision);
                hash = HashAdd(hash, instance.Reserved0);
                hash = HashAdd(hash, GetInstanceSourceRevision(i));
            }

            return hash;
        }

        internal static ulong CreatePagedStableFrameSignature(
            GlobalIlluminationSettings gi,
            Vector3 cameraPosition,
            ulong settingsSignature,
            ulong sceneSignature,
            int instanceCount,
            int pageTableCapacity)
        {
            ArgumentNullException.ThrowIfNull(gi);
            ulong hash = settingsSignature;
            hash = HashAdd(hash, sceneSignature);
            hash = HashAdd(hash, cameraPosition);
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(gi.FarFieldStartDistance));
            hash = HashAdd(hash, unchecked((uint)gi.FarFieldMaxTraceSteps));
            hash = HashAdd(hash, gi.FarFieldForceAll ? 1u : 0u);
            hash = HashAdd(hash, unchecked((uint)gi.FarFieldPageRequestRadius));
            hash = HashAdd(hash, unchecked((uint)gi.FarFieldPageUpdatesPerFrame));
            hash = HashAdd(hash, unchecked((uint)Math.Max(instanceCount, 0)));
            return HashAdd(hash, unchecked((uint)Math.Max(pageTableCapacity, 0)));
        }

        private static ulong CreatePagedSettingsSignature(GlobalIlluminationSettings gi, int resolution, int capacity)
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, (uint)resolution);
            hash = HashAdd(hash, (uint)capacity);
            hash = HashAdd(hash, (uint)gi.FarFieldCascadeCount);
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(gi.FarFieldBaseVoxelSize));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(gi.FarFieldCascadeVoxelScale));
            hash = HashAdd(
                hash,
                gi.EffectiveGiFarFieldMaterialV2
                    ? FarFieldMaterialPayloadV2.PayloadVersion
                    : 1u);
            return hash;
        }

        private static ulong CreatePagedSceneSignature(
            IReadOnlyList<GPUFarFieldInstance> instances,
            IReadOnlyList<BoundingBox> bounds,
            IReadOnlyList<ulong> sourceRevisions)
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, (uint)instances.Count);
            for (int i = 0; i < instances.Count; i++)
            {
                GPUFarFieldInstance instance = instances[i];
                hash = HashAdd(hash, instance.VertexOffset);
                hash = HashAdd(hash, instance.IndexOffset);
                hash = HashAdd(hash, instance.IndexCount);
                hash = HashAdd(hash, instance.MaterialIndex);
                hash = HashAdd(hash, instance.World);
                hash = HashAdd(hash, instance.PrimitiveKeyBase);
                hash = HashAdd(hash, instance.FarFieldRevision);
                hash = HashAdd(hash, instance.Reserved0);
                if ((uint)i < (uint)sourceRevisions.Count)
                    hash = HashAdd(hash, sourceRevisions[i]);
                if ((uint)i < (uint)bounds.Count)
                {
                    hash = HashAdd(hash, bounds[i].Min);
                    hash = HashAdd(hash, bounds[i].Max);
                }
            }

            return hash;
        }

        private ulong AdvancePagingFrameSerial()
        {
            _pagingFrameSerial++;
            if (_pagingFrameSerial == 0)
                _pagingFrameSerial = 1;
            return _pagingFrameSerial;
        }

        private static int FloorToInt(float value)
        {
            float floored = MathF.Floor(value);
            if (floored <= int.MinValue)
                return int.MinValue;
            if (floored >= int.MaxValue)
                return int.MaxValue;
            return (int)floored;
        }

        private void EnsureVoxelCapacity(int resolution)
        {
            ulong pageCacheBudgetBytes = ResolveFarFieldPageCacheBudgetBytes(
                _settings.GlobalIllumination.FarFieldMemoryBudgetBytes);
            if (CalculateLegacyCacheBytes(resolution, MaterialV2Enabled) > pageCacheBudgetBytes)
            {
                throw new InvalidOperationException(
                    "The resolved legacy far-field clipmap exceeds its tier cache budget before allocation.");
            }

            ulong voxelCount = checked((ulong)Math.Max(1, resolution) * (ulong)Math.Max(1, resolution) * (ulong)Math.Max(1, resolution));
            ulong requiredBytes = Math.Max(
                MinBufferSize,
                checked(voxelCount * GetVoxelStrideBytes(MaterialV2Enabled)));
            ulong packedDistanceBytes = Math.Max(MinBufferSize, checked(((voxelCount + 1UL) / 2UL) * sizeof(uint)));
            ulong seedBytes = Math.Max(MinBufferSize, checked(voxelCount * sizeof(uint)));
            EnsureBuffer(ref _voxelBuffer, ref _voxelBufferBytes, requiredBytes, "Far Field Clipmap Voxels");
            EnsureBuffer(ref _bakeVoxelBuffer, ref _bakeVoxelBufferBytes, requiredBytes, "Far Field Clipmap Bake Voxels");
            EnsureBuffer(ref _distanceBuffer, ref _distanceBufferBytes, packedDistanceBytes, "Far Field Clipmap Distance Field R16");
            EnsureBuffer(ref _jumpFloodScratch0Buffer, ref _jumpFloodScratch0BufferBytes, seedBytes, "Far Field Clipmap Jump Flood Scratch 0");
            EnsureBuffer(ref _jumpFloodScratch1Buffer, ref _jumpFloodScratch1BufferBytes, seedBytes, "Far Field Clipmap Jump Flood Scratch 1");
        }

        private void EnsureInstanceCapacity(int instanceCount)
        {
            ulong requiredBytes = Math.Max(MinBufferSize, checked((ulong)Math.Max(1, instanceCount) * InstanceStride));
            ulong instanceInputBudgetBytes = ResolveFarFieldInstanceInputBudgetBytes(
                _settings.GlobalIllumination.FarFieldMemoryBudgetBytes);
            if (requiredBytes > instanceInputBudgetBytes)
            {
                throw new InvalidOperationException(
                    $"Far-field static instance input ({requiredBytes} bytes) exceeds its tier reserve ({instanceInputBudgetBytes} bytes).");
            }
            EnsureBuffer(ref _instanceBuffer, ref _instanceBufferBytes, requiredBytes, "Far Field Clipmap Instances");
        }

        private void EnsureBuffer(ref BufferHandle handle, ref ulong currentBytes, ulong requiredBytes, string debugName)
        {
            if (handle.IsValid && currentBytes >= requiredBytes)
                return;

            if (handle.IsValid)
                RetireReplacedBuffer(handle);

            handle = _bufferManager.CreateDeviceBuffer(
                requiredBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: debugName);
            currentBytes = requiredBytes;
            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
        }

        private void ResizePagedBuffer(ref BufferHandle handle, ref ulong currentBytes, ulong requiredBytes, string debugName)
        {
            if (handle.IsValid && currentBytes == requiredBytes)
                return;

            if (handle.IsValid)
                RetireReplacedBuffer(handle);
            handle = BufferHandle.Invalid;
            currentBytes = 0;
            EnsureBuffer(ref handle, ref currentBytes, requiredBytes, debugName);
        }

        private void RetireReplacedBuffer(BufferHandle handle)
        {
            if (!handle.IsValid)
                return;

            // Bindless descriptors are update-after-bind. A buffer replaced while
            // assembling the current frame can therefore still be named by an
            // earlier in-flight submission. Retire it against the current frame's
            // terminal fence; same-queue ordering guarantees that fence completes
            // after every older descriptor consumer as well.
            _deleter.QueueBufferDeletion(
                _synchronizationManager.GetInFlightFence(),
                handle,
                _bufferManager);
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

        private static ulong CreateSignature(
            int resolution,
            BoundingBox bounds,
            IReadOnlyList<GPUFarFieldInstance> instances,
            IReadOnlyList<ulong> sourceRevisions,
            uint payloadVersion)
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, (uint)resolution);
            hash = HashAdd(hash, bounds.Min);
            hash = HashAdd(hash, bounds.Max);
            hash = HashAdd(hash, payloadVersion);
            hash = HashAdd(hash, (uint)instances.Count);
            for (int i = 0; i < instances.Count; i++)
            {
                GPUFarFieldInstance instance = instances[i];
                hash = HashAdd(hash, instance.VertexOffset);
                hash = HashAdd(hash, instance.IndexOffset);
                hash = HashAdd(hash, instance.IndexCount);
                hash = HashAdd(hash, instance.MaterialIndex);
                // Static instances may keep the same mesh and material while their
                // world transform changes.  Omitting this made the baked field
                // silently stale until an unrelated scene change forced a bake.
                hash = HashAdd(hash, instance.World);
                hash = HashAdd(hash, instance.PrimitiveKeyBase);
                hash = HashAdd(hash, instance.FarFieldRevision);
                hash = HashAdd(hash, instance.Reserved0);
                if ((uint)i < (uint)sourceRevisions.Count)
                    hash = HashAdd(hash, sourceRevisions[i]);
            }
            return hash;
        }

        private static ulong CreateInstanceSourceRevision(
            AccelerationStructureManager.StaticOpaqueInstance instance,
            MaterialAspectRevisions materialRevisions,
            uint transportProfileRevision)
        {
            return CreateStaticInstanceSourceRevision(
                instance.Mesh,
                materialRevisions.FarField,
                transportProfileRevision);
        }

        internal static ulong CreateStaticInstanceSourceRevision(MeshHandle mesh, uint materialContentRevision)
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, unchecked((uint)mesh.Index));
            hash = HashAdd(hash, mesh.Generation);
            hash = HashAdd(hash, materialContentRevision);
            return hash;
        }

        internal static ulong CreateStaticInstanceSourceRevision(
            MeshHandle mesh,
            uint farFieldRevision,
            uint transportProfileRevision)
        {
            ulong hash = CreateStaticInstanceSourceRevision(mesh, farFieldRevision);
            return HashAdd(hash, transportProfileRevision);
        }

        private ulong GetInstanceSourceRevision(int index)
        {
            return (uint)index < (uint)_instanceSourceRevisions.Count
                ? _instanceSourceRevisions[index]
                : 0UL;
        }

        private static ulong HashAdd(ulong hash, Vector3 value)
        {
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.X));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.Y));
            return HashAdd(hash, BitConverter.SingleToUInt32Bits(value.Z));
        }

        private static ulong HashAdd(ulong hash, Matrix4x4 value)
        {
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M11));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M12));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M13));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M14));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M21));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M22));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M23));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M24));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M31));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M32));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M33));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M34));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M41));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M42));
            hash = HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M43));
            return HashAdd(hash, BitConverter.SingleToUInt32Bits(value.M44));
        }

        private static ulong HashAdd(ulong hash, uint value)
        {
            hash ^= value;
            return hash * 1099511628211UL;
        }

        private static ulong HashAdd(ulong hash, ulong value)
        {
            hash = HashAdd(hash, unchecked((uint)value));
            return HashAdd(hash, unchecked((uint)(value >> 32)));
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
            ClearPageBakeQueue();
            if (_paramsBuffer.IsValid)
                _bufferManager.DestroyBuffer(_paramsBuffer);
            if (_voxelBuffer.IsValid)
                _bufferManager.DestroyBuffer(_voxelBuffer);
            if (_bakeVoxelBuffer.IsValid)
                _bufferManager.DestroyBuffer(_bakeVoxelBuffer);
            if (_distanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_distanceBuffer);
            if (_jumpFloodScratch0Buffer.IsValid)
                _bufferManager.DestroyBuffer(_jumpFloodScratch0Buffer);
            if (_jumpFloodScratch1Buffer.IsValid)
                _bufferManager.DestroyBuffer(_jumpFloodScratch1Buffer);
            if (_instanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_instanceBuffer);
            if (_pageTableBuffer.IsValid)
                _bufferManager.DestroyBuffer(_pageTableBuffer);
        }
    }
}
