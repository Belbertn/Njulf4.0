using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

public sealed class FoliageManager : IDisposable
{
    public const int DefaultDebugFallbackMaxInstancesPerPatch = 512;

    private const uint InstancesPerCluster = 64;
    private const uint PatchFlagVisible = 1u << 0;
    private const uint PrototypeFlagCastShadows = 1u << 0;
    private const uint InvalidTextureIndex = uint.MaxValue;
    public const ulong CounterStride = 32;

    private static readonly UploadBarrierDescription FoliageUploadBarrier = new(
        PipelineStageFlags2.AllCommandsBit,
        AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

    private readonly VulkanContext? _context;
    private readonly BufferManager? _bufferManager;
    private readonly StagingRing? _stagingRing;
    private readonly MeshManager? _meshManager;
    private readonly MaterialManager? _materialManager;
    private readonly object _lock = new();
    private readonly List<StaticInstanceBatch> _debugFallbackBatches = new();
    private readonly List<FoliagePrototype> _prototypeScratch = new();
    private readonly List<GPUFoliagePrototype> _gpuPrototypeScratch = new();
    private readonly List<GPUFoliagePatch> _gpuPatchScratch = new();
    private readonly List<GPUFoliageCluster> _gpuClusterScratch = new();
    private readonly List<GPUFoliageInstance> _gpuInstanceScratch = new();
    private RuntimeBuffer _prototypeBuffer;
    private RuntimeBuffer _patchBuffer;
    private RuntimeBuffer _clusterBuffer;
    private readonly RuntimeBuffer[] _instanceBuffers = new RuntimeBuffer[FramesInFlight];
    private readonly RuntimeBuffer[] _visibleClusterBuffers = new RuntimeBuffer[FramesInFlight];
    private readonly RuntimeBuffer[] _meshletDrawBuffers = new RuntimeBuffer[FramesInFlight];
    private readonly RuntimeBuffer[] _counterBuffers = new RuntimeBuffer[FramesInFlight];
    private readonly RuntimeBuffer[] _indirectDispatchBuffers = new RuntimeBuffer[FramesInFlight];
    private readonly BufferHandle[] _counterReadbackBuffers = new BufferHandle[FramesInFlight];
    private readonly FoliageCounterSnapshot[] _lastCompletedCounterSnapshots =
    {
        FoliageCounterSnapshot.Invalid,
        FoliageCounterSnapshot.Invalid
    };
    private readonly bool[] _counterReadbackRecorded = new bool[FramesInFlight];
    private BindlessHeap? _registeredBindlessHeap;
    private FoliageSceneRegistrationSnapshot _lastSnapshot;
    private FoliageGpuBuildSnapshot _lastGpuBuildSnapshot;
    private ulong _lastContentSignature;
    private bool _hasContentSignature;
    private bool _hasUploadedGpuContent;
    private bool _disposed;
    private ulong _lastUploadBytes;
    private long _lastBuildMicroseconds;
    private long _lastUploadMicroseconds;
    private int _lastGrassBladeEstimate;
    private int _lastOverflowCount;
    private bool _lastContentChanged;
    private int _lastAuthoredMeshletDrawCapacity;
    private int _lastAuthoredClusterCount;
    private int _lastAuthoredMeshletWorkItemCount;
    private uint _lastFirstAuthoredClusterIndex = uint.MaxValue;

    public FoliageManager()
    {
    }

    public FoliageManager(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing,
        MeshManager meshManager,
        MaterialManager materialManager)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
        _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
    }

    public FoliageSceneRegistrationSnapshot LastSnapshot => _lastSnapshot;
    public FoliageGpuBuildSnapshot LastGpuBuildSnapshot => _lastGpuBuildSnapshot;
    public IReadOnlyList<StaticInstanceBatch> DebugFallbackBatches => _debugFallbackBatches;
    public ulong LastUploadBytes => _lastUploadBytes;
    public long LastBuildMicroseconds => _lastBuildMicroseconds;
    public long LastUploadMicroseconds => _lastUploadMicroseconds;
    public bool LastContentChanged => _lastContentChanged;
    public int ClusterDrawCapacity => _clusterBuffer.ElementCapacity > int.MaxValue ? int.MaxValue : (int)_clusterBuffer.ElementCapacity;

    public FoliageSceneRegistrationSnapshot RegisterScene(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        _lastSnapshot = CreateSnapshot(scene);
        return _lastSnapshot;
    }

    public FoliageGpuBuildSnapshot PrepareFrame(
        Scene scene,
        FoliageSettings settings,
        CommandBuffer commandBuffer,
        SceneRenderingData sceneData)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (sceneData == null)
            throw new ArgumentNullException(nameof(sceneData));

        lock (_lock)
        {
            _lastUploadBytes = 0;
            _lastBuildMicroseconds = 0;
            _lastUploadMicroseconds = 0;
            _lastContentChanged = false;

            FoliageSceneRegistrationSnapshot snapshot = RegisterScene(scene);
            ulong signature = ComputeContentSignature(scene, settings);
            bool contentChanged = !_hasContentSignature || signature != _lastContentSignature;
            _lastContentChanged = contentChanged;

            if (!settings.Enabled)
            {
                _lastGpuBuildSnapshot = new FoliageGpuBuildSnapshot(
                    snapshot.PrototypeCount,
                    snapshot.PatchCount,
                    0,
                    0,
                    0,
                    signature,
                    0);
                PopulateSceneData(sceneData);
                return _lastGpuBuildSnapshot;
            }

            if (contentChanged)
            {
                long buildStart = Stopwatch.GetTimestamp();
                BuildGpuRecords(scene, settings, signature);
                _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                _lastContentSignature = signature;
                _hasContentSignature = true;
            }

            PopulateSceneData(sceneData);

            if (!CanUpload(commandBuffer))
                return _lastGpuBuildSnapshot;

            EnsureGpuBuffers(settings);
            PopulateSceneData(sceneData);
            bool uploadRequired = contentChanged || !_hasUploadedGpuContent;
            if (uploadRequired)
            {
                long uploadStart = Stopwatch.GetTimestamp();
                _lastUploadBytes = UploadGpuRecords(commandBuffer);
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
                _hasUploadedGpuContent = true;
                sceneData.UploadedBytes += _lastUploadBytes;
            }

            sceneData.CpuFoliageUploadMicroseconds = _lastUploadMicroseconds;
            UpdateRegisteredBindlessBuffers();
            PopulateSceneData(sceneData);
            return _lastGpuBuildSnapshot;
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

    public FoliageRuntimeBuffers GetBuffers(int frameIndex)
    {
        ValidateFrameIndex(frameIndex);

        lock (_lock)
        {
            return new FoliageRuntimeBuffers(
                _prototypeBuffer.Handle,
                _patchBuffer.Handle,
                _clusterBuffer.Handle,
                _instanceBuffers[frameIndex].Handle,
                _visibleClusterBuffers[frameIndex].Handle,
                _meshletDrawBuffers[frameIndex].Handle,
                _counterBuffers[frameIndex].Handle,
                _indirectDispatchBuffers[frameIndex].Handle,
                _visibleClusterBuffers[frameIndex].ByteSize,
                _meshletDrawBuffers[frameIndex].ByteSize,
                _counterBuffers[frameIndex].ByteSize,
                _indirectDispatchBuffers[frameIndex].ByteSize,
                _lastGpuBuildSnapshot.ClusterCount,
                (int)Math.Min(_visibleClusterBuffers[frameIndex].ElementCapacity, int.MaxValue),
                (int)Math.Min(_meshletDrawBuffers[frameIndex].ElementCapacity, int.MaxValue),
                _lastAuthoredMeshletWorkItemCount,
                _lastFirstAuthoredClusterIndex,
                _lastAuthoredClusterCount);
        }
    }

    public void ReadCompletedFrame(int frameIndex)
    {
        ValidateFrameIndex(frameIndex);

        lock (_lock)
        {
            if (!_counterReadbackRecorded[frameIndex] || !_counterReadbackBuffers[frameIndex].IsValid)
            {
                _lastCompletedCounterSnapshots[frameIndex] = FoliageCounterSnapshot.Invalid;
                return;
            }

            _bufferManager!.InvalidateBuffer(_counterReadbackBuffers[frameIndex], 0, CounterStride);
            unsafe
            {
                GPUFoliageCounters* counters = (GPUFoliageCounters*)_bufferManager.GetMappedPointer(_counterReadbackBuffers[frameIndex]);
                _lastCompletedCounterSnapshots[frameIndex] = FoliageCounterSnapshot.FromCounters(*counters);
            }

            _counterReadbackRecorded[frameIndex] = false;
        }
    }

    public FoliageCounterSnapshot GetLastCompletedCounters(int frameIndex)
    {
        ValidateFrameIndex(frameIndex);

        lock (_lock)
            return _lastCompletedCounterSnapshots[frameIndex];
    }

    public unsafe void RecordCounterReadback(CommandBuffer commandBuffer, int frameIndex)
    {
        ValidateFrameIndex(frameIndex);
        if (_context == null || _bufferManager == null)
            return;
        if (commandBuffer.Handle == 0)
            return;

        lock (_lock)
        {
            BufferHandle counterBuffer = _counterBuffers[frameIndex].Handle;
            if (!counterBuffer.IsValid)
                return;

            EnsureCounterReadbackBuffer(frameIndex);
            VkBuffer source = _bufferManager.GetBuffer(counterBuffer);
            VkBuffer destination = _bufferManager.GetBuffer(_counterReadbackBuffers[frameIndex]);

            BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                source,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                0,
                CounterStride);
            ExecuteBufferBarrier(commandBuffer, beforeCopy);

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = CounterStride
            };
            _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                CounterStride);
            ExecuteBufferBarrier(commandBuffer, afterCopy);

            _counterReadbackRecorded[frameIndex] = true;
        }
    }

    public FoliageDebugFallbackResult ApplyDebugFallback(Scene scene, FoliageDebugFallbackOptions? options = null)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        options ??= new FoliageDebugFallbackOptions
        {
            MaxInstancesPerPatch = DefaultDebugFallbackMaxInstancesPerPatch
        };

        long start = Stopwatch.GetTimestamp();
        ClearDebugFallback(scene);
        FoliageSceneRegistrationSnapshot snapshot = RegisterScene(scene);

        int generated = 0;
        int dropped = 0;
        foreach (FoliagePatch patch in scene.FoliagePatches)
        {
            if (!options.IncludeHiddenPatches && !patch.Visible)
                continue;
            if (patch.Density <= 0f || patch.Prototype.Mesh == null)
                continue;

            int requested = EstimateFallbackInstanceCount(patch);
            int emitted = Math.Min(requested, options.MaxInstancesPerPatch);
            if (emitted <= 0)
            {
                dropped += requested;
                continue;
            }

            var batch = new StaticInstanceBatch(GenerateFallbackMatrices(patch, emitted, options.InstanceScale))
            {
                Name = $"FoliageDebugFallback.{patch.Name}",
                Mesh = patch.Prototype.Mesh,
                Material = patch.Prototype.Material,
                Visible = patch.Visible || options.IncludeHiddenPatches
            };

            scene.Add(batch);
            _debugFallbackBatches.Add(batch);
            generated += emitted;
            dropped += requested - emitted;
        }

        long buildMicroseconds = ElapsedMicroseconds(start);
        return new FoliageDebugFallbackResult(
            _debugFallbackBatches.AsReadOnly(),
            snapshot.PatchCount,
            generated,
            dropped,
            buildMicroseconds);
    }

    public void ClearDebugFallback(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        foreach (StaticInstanceBatch batch in _debugFallbackBatches)
            scene.Remove(batch);
        _debugFallbackBatches.Clear();
    }

    private void BuildGpuRecords(Scene scene, FoliageSettings settings, ulong contentSignature)
    {
        _prototypeScratch.Clear();
        _gpuPrototypeScratch.Clear();
        _gpuPatchScratch.Clear();
        _gpuClusterScratch.Clear();
        _gpuInstanceScratch.Clear();
        _lastGrassBladeEstimate = 0;
        _lastOverflowCount = 0;
        _lastAuthoredMeshletDrawCapacity = 0;
        _lastAuthoredClusterCount = 0;
        _lastAuthoredMeshletWorkItemCount = 0;
        _lastFirstAuthoredClusterIndex = uint.MaxValue;

        foreach (FoliagePrototype prototype in scene.FoliagePrototypes)
            AddPrototypeIfMissing(_prototypeScratch, prototype);
        foreach (FoliagePatch patch in scene.FoliagePatches)
            AddPrototypeIfMissing(_prototypeScratch, patch.Prototype);

        foreach (FoliagePrototype prototype in _prototypeScratch)
            _gpuPrototypeScratch.Add(CreateGpuPrototype(prototype, settings));

        uint logicalFirstInstance = 0;
        uint clusterBudget = settings.MaxVisibleClusters <= 0 ? 0u : (uint)settings.MaxVisibleClusters;
        for (int patchIndex = 0; patchIndex < scene.FoliagePatches.Count; patchIndex++)
        {
            FoliagePatch patch = scene.FoliagePatches[patchIndex];
            uint prototypeIndex = (uint)GetPrototypeIndex(_prototypeScratch, patch.Prototype);
            uint clusterOffset = (uint)_gpuClusterScratch.Count;
            uint firstInstance = logicalFirstInstance;
            GeneratePatchClusters(patch, prototypeIndex, (uint)patchIndex, settings, ref logicalFirstInstance, ref clusterBudget);
            uint clusterCount = (uint)_gpuClusterScratch.Count - clusterOffset;

            _gpuPatchScratch.Add(new GPUFoliagePatch
            {
                BoundsMinDensity = new Vector4(
                    patch.Bounds.Min.X,
                    patch.Bounds.Min.Y,
                    patch.Bounds.Min.Z,
                    patch.Density * settings.DensityScale),
                BoundsMaxSeed = new Vector4(
                    patch.Bounds.Max.X,
                    patch.Bounds.Max.Y,
                    patch.Bounds.Max.Z,
                    patch.Seed),
                PrototypeIndex = prototypeIndex,
                ClusterOffset = clusterOffset,
                ClusterCount = clusterCount,
                DensityTextureIndex = InvalidTextureIndex,
                Seed = patch.Seed,
                Flags = patch.Visible ? PatchFlagVisible : 0u,
                Padding0 = firstInstance,
                Padding1 = 0
            });
        }

        _lastGpuBuildSnapshot = new FoliageGpuBuildSnapshot(
            _gpuPrototypeScratch.Count,
            _gpuPatchScratch.Count,
            _gpuClusterScratch.Count,
            _gpuInstanceScratch.Count,
            _lastGrassBladeEstimate,
            contentSignature,
            ComputeClusterSignature());
    }

    private GPUFoliagePrototype CreateGpuPrototype(FoliagePrototype prototype, FoliageSettings settings)
    {
        MeshInfo meshInfo = default;
        if (prototype.Mesh is MeshHandle meshHandle && meshHandle.IsValid && _meshManager != null)
        {
            try
            {
                meshInfo = _meshManager.GetMeshInfo(meshHandle);
            }
            catch (InvalidOperationException)
            {
                meshInfo = default;
            }
        }

        return new GPUFoliagePrototype
        {
            MeshMetadataIndex = meshInfo.MeshMetadataOffset,
            MeshletOffset = meshInfo.MeshletOffset,
            MeshletCount = meshInfo.MeshletCount,
            MeshletLod1Offset = meshInfo.MeshletLod1Offset,
            MeshletLod1Count = meshInfo.MeshletLod1Count,
            MeshletLod2Offset = meshInfo.MeshletLod2Offset,
            MeshletLod2Count = meshInfo.MeshletLod2Count,
            MaterialIndex = ResolveMaterialIndex(prototype),
            GeometryMode = (uint)prototype.GeometryMode,
            Flags = settings.CastShadows ? PrototypeFlagCastShadows : 0u,
            BladeHeight = prototype.CardHeight,
            BladeWidth = prototype.GeometryMode == FoliageGeometryMode.AuthoredMeshlets
                ? Math.Max(1u, prototype.AuthoredMeshletStride)
                : prototype.CardWidth,
            LodDistances = new Vector4(
                prototype.Lod.Lod0Distance,
                prototype.Lod.Lod1Distance,
                prototype.Lod.Lod2Distance,
                settings.MaxDrawDistance),
            WindParams = new Vector4(
                prototype.Wind.Strength,
                prototype.Wind.Frequency,
                prototype.Wind.Flutter,
                0f),
            LightingParams = new Vector4(
                prototype.Lighting.WrapDiffuse,
                prototype.Lighting.Backlight,
                prototype.Lighting.NormalBend,
                settings.GrassShadowDistance)
        };
    }

    private uint ResolveMaterialIndex(FoliagePrototype prototype)
    {
        if (_materialManager == null)
            return prototype.Material is MaterialHandle materialHandle && materialHandle.IsValid
                ? (uint)materialHandle.Index
                : 0u;

        MaterialHandle handle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
            prototype.Material,
            _materialManager.DefaultMaterialHandle,
            prototype.Name);
        try
        {
            return (uint)_materialManager.ResolveMaterialIndex(handle);
        }
        catch (InvalidOperationException)
        {
            return (uint)_materialManager.ResolveMaterialIndex(_materialManager.DefaultMaterialHandle);
        }
    }

    private void GeneratePatchClusters(
        FoliagePatch patch,
        uint prototypeIndex,
        uint patchIndex,
        FoliageSettings settings,
        ref uint logicalFirstInstance,
        ref uint remainingClusterBudget)
    {
        if (patch.Prototype.GeometryMode == FoliageGeometryMode.AuthoredMeshlets)
        {
            GenerateAuthoredMeshletCluster(patch, prototypeIndex, patchIndex, ref logicalFirstInstance, ref remainingClusterBudget);
            return;
        }

        if (patch.Prototype.GeometryMode == FoliageGeometryMode.BillboardCards)
        {
            GenerateBillboardCardClusters(patch, prototypeIndex, patchIndex, settings, ref logicalFirstInstance, ref remainingClusterBudget);
            return;
        }

        uint requestedInstances = EstimateGpuInstanceCount(patch, settings.DensityScale);
        if (requestedInstances == 0)
            return;

        uint requestedClusters = DivideRoundUp(requestedInstances, InstancesPerCluster);
        if (!patch.Visible || remainingClusterBudget == 0)
        {
            _lastOverflowCount += checked((int)requestedClusters);
            return;
        }

        uint emittedClusters = Math.Min(requestedClusters, remainingClusterBudget);
        if (emittedClusters < requestedClusters)
            _lastOverflowCount += checked((int)(requestedClusters - emittedClusters));
        remainingClusterBudget -= emittedClusters;

        Vector3 size = patch.Bounds.Size;
        uint columns = Math.Max(1u, (uint)Math.Ceiling(Math.Sqrt(emittedClusters)));
        uint rows = DivideRoundUp(emittedClusters, columns);
        float cellX = columns == 0 ? 0f : size.X / columns;
        float cellZ = rows == 0 ? 0f : size.Z / rows;
        float height = Math.Max(0f, size.Y);

        for (uint i = 0; i < emittedClusters; i++)
        {
            uint x = i % columns;
            uint z = i / columns;
            uint instanceCount = Math.Min(InstancesPerCluster, requestedInstances - i * InstancesPerCluster);
            float minX = patch.Bounds.Min.X + x * cellX;
            float maxX = x + 1u == columns ? patch.Bounds.Max.X : minX + cellX;
            float minZ = patch.Bounds.Min.Z + z * cellZ;
            float maxZ = z + 1u == rows ? patch.Bounds.Max.Z : minZ + cellZ;
            Vector3 center = new(
                (minX + maxX) * 0.5f,
                (patch.Bounds.Min.Y + patch.Bounds.Max.Y) * 0.5f,
                (minZ + maxZ) * 0.5f);
            float radius = MathF.Sqrt(cellX * cellX + height * height + cellZ * cellZ) * 0.5f;

            _gpuClusterScratch.Add(new GPUFoliageCluster
            {
                WorldCenterRadius = new Vector4(center.X, center.Y, center.Z, radius),
                BoundsMinDensity = new Vector4(minX, patch.Bounds.Min.Y, minZ, patch.Density * settings.DensityScale),
                BoundsMaxLod = new Vector4(maxX, patch.Bounds.Max.Y, maxZ, patch.Prototype.Lod.Lod2Distance),
                PatchIndex = patchIndex,
                FirstInstance = logicalFirstInstance,
                InstanceCount = instanceCount,
                RandomSeed = Hash(patch.Seed, i ^ prototypeIndex)
            });

            logicalFirstInstance += instanceCount;
            _lastGrassBladeEstimate = checked(_lastGrassBladeEstimate + (int)instanceCount);
        }
    }

    private void GenerateBillboardCardClusters(
        FoliagePatch patch,
        uint prototypeIndex,
        uint patchIndex,
        FoliageSettings settings,
        ref uint logicalFirstInstance,
        ref uint remainingClusterBudget)
    {
        Vector3 size = patch.Bounds.Size;
        float area = Math.Max(0f, size.X) * Math.Max(0f, size.Y);
        if (area <= 0f || patch.Density <= 0f || settings.DensityScale <= 0f)
            return;

        double requestedDouble = Math.Ceiling(area * patch.Density * settings.DensityScale);
        if (requestedDouble <= 0.0)
            return;

        uint requestedInstances = requestedDouble >= uint.MaxValue ? uint.MaxValue : (uint)requestedDouble;
        uint requestedClusters = DivideRoundUp(requestedInstances, InstancesPerCluster);
        if (!patch.Visible || remainingClusterBudget == 0)
        {
            _lastOverflowCount += checked((int)Math.Min(requestedClusters, int.MaxValue));
            return;
        }

        uint emittedClusters = Math.Min(requestedClusters, remainingClusterBudget);
        if (emittedClusters < requestedClusters)
            _lastOverflowCount += checked((int)Math.Min(requestedClusters - emittedClusters, int.MaxValue));
        remainingClusterBudget -= emittedClusters;

        uint columns = Math.Max(1u, (uint)Math.Ceiling(Math.Sqrt(emittedClusters)));
        uint rows = DivideRoundUp(emittedClusters, columns);
        float cellX = columns == 0 ? 0f : size.X / columns;
        float cellY = rows == 0 ? 0f : size.Y / rows;
        float depth = Math.Max(0.001f, size.Z);

        for (uint i = 0; i < emittedClusters; i++)
        {
            uint x = i % columns;
            uint y = i / columns;
            uint instanceCount = Math.Min(InstancesPerCluster, requestedInstances - i * InstancesPerCluster);
            float minX = patch.Bounds.Min.X + x * cellX;
            float maxX = x + 1u == columns ? patch.Bounds.Max.X : minX + cellX;
            float minY = patch.Bounds.Min.Y + y * cellY;
            float maxY = y + 1u == rows ? patch.Bounds.Max.Y : minY + cellY;
            Vector3 center = new(
                (minX + maxX) * 0.5f,
                (minY + maxY) * 0.5f,
                (patch.Bounds.Min.Z + patch.Bounds.Max.Z) * 0.5f);
            float radius = MathF.Sqrt(cellX * cellX + cellY * cellY + depth * depth) * 0.5f;

            _gpuClusterScratch.Add(new GPUFoliageCluster
            {
                WorldCenterRadius = new Vector4(center.X, center.Y, center.Z, radius),
                BoundsMinDensity = new Vector4(minX, minY, patch.Bounds.Min.Z, patch.Density * settings.DensityScale),
                BoundsMaxLod = new Vector4(maxX, maxY, patch.Bounds.Max.Z, patch.Prototype.Lod.Lod2Distance),
                PatchIndex = patchIndex,
                FirstInstance = logicalFirstInstance,
                InstanceCount = instanceCount,
                RandomSeed = Hash(patch.Seed, i ^ prototypeIndex)
            });

            logicalFirstInstance += instanceCount;
            _lastGrassBladeEstimate = checked(_lastGrassBladeEstimate + (int)Math.Min(instanceCount, int.MaxValue));
        }
    }

    private void GenerateAuthoredMeshletCluster(
        FoliagePatch patch,
        uint prototypeIndex,
        uint patchIndex,
        ref uint logicalFirstInstance,
        ref uint remainingClusterBudget)
    {
        if (!patch.Visible || remainingClusterBudget == 0)
        {
            _lastOverflowCount++;
            return;
        }

        GPUFoliagePrototype prototype = _gpuPrototypeScratch[(int)prototypeIndex];
        if (prototype.MeshletCount == 0)
            return;

        Vector3 center = (patch.Bounds.Min + patch.Bounds.Max) * 0.5f;
        Vector3 size = patch.Bounds.Size;
        float radius = size.Length() * 0.5f;
        uint instanceIndex = logicalFirstInstance++;
        uint clusterIndex = (uint)_gpuClusterScratch.Count;
        if (_lastFirstAuthoredClusterIndex == uint.MaxValue)
            _lastFirstAuthoredClusterIndex = clusterIndex;
        _lastAuthoredClusterCount++;

        _gpuInstanceScratch.Add(new GPUFoliageInstance
        {
            PositionScale = new Vector4(
                patch.InstancePosition.X,
                patch.InstancePosition.Y,
                patch.InstancePosition.Z,
                Math.Max(0.0001f, patch.InstanceScale)),
            RotationWind = new Vector4(0f, 0f, 0f, patch.Prototype.Wind.Strength),
            ColorVariation = new Vector4(1f, 1f, 1f, 1f),
            PrototypeIndex = prototypeIndex,
            PatchIndex = patchIndex,
            ClusterIndex = clusterIndex,
            Flags = 0u
        });

        _gpuClusterScratch.Add(new GPUFoliageCluster
        {
            WorldCenterRadius = new Vector4(center.X, center.Y, center.Z, radius),
            BoundsMinDensity = new Vector4(patch.Bounds.Min.X, patch.Bounds.Min.Y, patch.Bounds.Min.Z, patch.Density),
            BoundsMaxLod = new Vector4(patch.Bounds.Max.X, patch.Bounds.Max.Y, patch.Bounds.Max.Z, patch.Prototype.Lod.Lod2Distance),
            PatchIndex = patchIndex,
            FirstInstance = instanceIndex,
            InstanceCount = 1u,
            RandomSeed = Hash(patch.Seed, prototypeIndex)
        });

        remainingClusterBudget--;
        uint maxLodMeshletCount = Math.Max(
            prototype.MeshletCount,
            Math.Max(prototype.MeshletLod1Count, prototype.MeshletLod2Count));
        uint meshletStride = Math.Max(1u, (uint)MathF.Round(prototype.BladeWidth));
        uint maxSubmittedMeshletCount = DivideRoundUp(maxLodMeshletCount, meshletStride);
        _lastAuthoredMeshletDrawCapacity = checked(_lastAuthoredMeshletDrawCapacity + (int)maxSubmittedMeshletCount);
    }

    private void PopulateSceneData(SceneRenderingData sceneData)
    {
        sceneData.FoliagePatchCount = _lastGpuBuildSnapshot.PatchCount;
        sceneData.FoliagePrototypeCount = _lastGpuBuildSnapshot.PrototypeCount;
        sceneData.FoliageClusterCount = _lastGpuBuildSnapshot.ClusterCount;
        sceneData.FoliageGrassBladeEstimate = _lastGpuBuildSnapshot.GrassBladeEstimate;
        sceneData.FoliageOverflowCount = _lastOverflowCount;
        sceneData.FoliageInstanceBufferBytes = MaxByteSize(_instanceBuffers);
        sceneData.FoliageClusterBufferBytes = _clusterBuffer.ByteSize;
        sceneData.FoliageDrawBufferBytes = MaxByteSize(_meshletDrawBuffers);
        sceneData.CpuFoliageBuildMicroseconds = _lastBuildMicroseconds;
        sceneData.CpuFoliageUploadMicroseconds = _lastUploadMicroseconds;
    }

    private bool CanUpload(CommandBuffer commandBuffer)
    {
        return _context != null &&
               _bufferManager != null &&
               _stagingRing != null &&
               commandBuffer.Handle != 0;
    }

    private void EnsureGpuBuffers(FoliageSettings settings)
    {
        EnsureCapacity(ref _prototypeBuffer, CheckedCount(_gpuPrototypeScratch.Count), (ulong)Marshal.SizeOf<GPUFoliagePrototype>(), "Foliage.PrototypeBuffer");
        EnsureCapacity(ref _patchBuffer, CheckedCount(_gpuPatchScratch.Count), (ulong)Marshal.SizeOf<GPUFoliagePatch>(), "Foliage.PatchBuffer");
        EnsureCapacity(ref _clusterBuffer, CheckedCount(_gpuClusterScratch.Count), (ulong)Marshal.SizeOf<GPUFoliageCluster>(), "Foliage.ClusterBuffer");

        uint visibleClusterCapacity = CheckedCount(_gpuClusterScratch.Count);
        uint instanceCapacity = CheckedCount(_gpuInstanceScratch.Count);
        int requestedDrawCapacity = Math.Max(_gpuClusterScratch.Count, _lastAuthoredMeshletDrawCapacity);
        uint drawCapacity = CheckedCount(Math.Min(Math.Max(1, settings.MaxVisibleMeshletDraws), Math.Max(1, requestedDrawCapacity)));
        _lastAuthoredMeshletWorkItemCount = _lastAuthoredMeshletDrawCapacity <= 0
            ? 0
            : Math.Min(_lastAuthoredMeshletDrawCapacity, (int)Math.Min(drawCapacity, int.MaxValue));
        for (int i = 0; i < FramesInFlight; i++)
        {
            EnsureCapacity(ref _instanceBuffers[i], instanceCapacity, (ulong)Marshal.SizeOf<GPUFoliageInstance>(), $"Foliage.InstanceBuffer.Frame{i}");
            EnsureCapacity(ref _visibleClusterBuffers[i], visibleClusterCapacity, sizeof(uint), $"Foliage.VisibleClusterBuffer.Frame{i}");
            EnsureCapacity(ref _meshletDrawBuffers[i], drawCapacity, (ulong)Marshal.SizeOf<GPUFoliageMeshletDrawCommand>(), $"Foliage.MeshletDrawBuffer.Frame{i}");
            EnsureCapacity(ref _counterBuffers[i], 1u, CounterStride, $"Foliage.CounterBuffer.Frame{i}");
            EnsureCapacity(ref _indirectDispatchBuffers[i], 1u, 16, $"Foliage.IndirectDispatchBuffer.Frame{i}", BufferUsageFlags.IndirectBufferBit);
        }
    }

    private void EnsureCapacity(
        ref RuntimeBuffer buffer,
        uint requiredElements,
        ulong stride,
        string debugName,
        BufferUsageFlags extraUsage = 0)
    {
        if (_context == null || _bufferManager == null)
            return;

        uint required = Math.Max(1u, requiredElements);
        if (buffer.Handle.IsValid && required <= buffer.ElementCapacity)
            return;

        uint newCapacity = buffer.Handle.IsValid ? buffer.ElementCapacity : 1u;
        while (newCapacity < required)
            newCapacity = checked(newCapacity * 2);

        DestroyIfValid(buffer.Handle);
        ulong byteSize = checked(newCapacity * stride);
        BufferHandle handle = _bufferManager.CreateDeviceBuffer(
            byteSize,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | extraUsage,
            requireDeviceAddress: false,
            MemoryBudgetCategory.ObjectAndInstanceBuffers,
            $"{debugName} ({newCapacity} elements)");
        _context.SetDebugName(_bufferManager.GetBuffer(handle).Handle, ObjectType.Buffer, debugName);
        buffer = new RuntimeBuffer(handle, newCapacity, byteSize);
    }

    private ulong UploadGpuRecords(CommandBuffer commandBuffer)
    {
        ulong uploaded = 0;
        uploaded += UploadSpan(CollectionsMarshal.AsSpan(_gpuPrototypeScratch), _prototypeBuffer.Handle, commandBuffer);
        uploaded += UploadSpan(CollectionsMarshal.AsSpan(_gpuPatchScratch), _patchBuffer.Handle, commandBuffer);
        uploaded += UploadSpan(CollectionsMarshal.AsSpan(_gpuClusterScratch), _clusterBuffer.Handle, commandBuffer);
        for (int i = 0; i < FramesInFlight; i++)
            uploaded += UploadSpan(CollectionsMarshal.AsSpan(_gpuInstanceScratch), _instanceBuffers[i].Handle, commandBuffer);
        return uploaded;
    }

    private ulong UploadSpan<T>(ReadOnlySpan<T> data, BufferHandle destination, CommandBuffer commandBuffer)
        where T : unmanaged
    {
        if (data.IsEmpty || _context == null || _bufferManager == null || _stagingRing == null)
            return 0;

        return GpuBufferUploader.UploadSpanToBuffer(
            _context,
            _bufferManager,
            _stagingRing,
            commandBuffer,
            destination,
            data,
            barrierDescription: FoliageUploadBarrier).ByteCount;
    }

    private void UpdateRegisteredBindlessBuffers()
    {
        if (_registeredBindlessHeap == null)
            return;

        RegisterStorageBuffer(BindlessIndex.FoliagePrototypeBuffer, _prototypeBuffer.Handle);
        RegisterStorageBuffer(BindlessIndex.FoliagePatchBuffer, _patchBuffer.Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageClusterBuffer, _clusterBuffer.Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageInstanceBufferBase, _instanceBuffers[0].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageInstanceBufferFrame1, _instanceBuffers[1].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageVisibleClusterBufferBase, _visibleClusterBuffers[0].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageVisibleClusterBufferFrame1, _visibleClusterBuffers[1].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageMeshletDrawBufferBase, _meshletDrawBuffers[0].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageMeshletDrawBufferFrame1, _meshletDrawBuffers[1].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageCounterBufferBase, _counterBuffers[0].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageCounterBufferFrame1, _counterBuffers[1].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageIndirectDispatchBufferBase, _indirectDispatchBuffers[0].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageIndirectDispatchBufferFrame1, _indirectDispatchBuffers[1].Handle);
    }

    private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
    {
        if (!handle.IsValid || _bufferManager == null || _registeredBindlessHeap == null)
            return;

        VkBuffer buffer = _bufferManager.GetBuffer(handle);
        _registeredBindlessHeap.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
    }

    private void DestroyIfValid(BufferHandle handle)
    {
        if (handle.IsValid && _bufferManager != null)
            _bufferManager.DestroyBuffer(handle);
    }

    private void EnsureCounterReadbackBuffer(int frameIndex)
    {
        if (_bufferManager == null)
            return;
        if (_counterReadbackBuffers[frameIndex].IsValid)
            return;

        _counterReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
            CounterStride,
            BufferUsageFlags.TransferDstBit,
            Vma.MemoryUsage.AutoPreferHost,
            Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
            $"Foliage.CounterReadback.Frame{frameIndex}",
            MemoryBudgetCategory.DiagnosticsAndDebug);
    }

    private unsafe void ExecuteBufferBarrier(CommandBuffer commandBuffer, BufferMemoryBarrier2 barrier)
    {
        if (_context == null)
            return;

        var dependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier
        };

        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
    }

    private ulong ComputeClusterSignature()
    {
        ulong hash = 14695981039346656037UL;
        hash = Hash(hash, (uint)_gpuClusterScratch.Count);
        foreach (GPUFoliageCluster cluster in _gpuClusterScratch)
        {
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(cluster.WorldCenterRadius.X));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(cluster.WorldCenterRadius.Y));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(cluster.WorldCenterRadius.Z));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(cluster.WorldCenterRadius.W));
            hash = Hash(hash, cluster.PatchIndex);
            hash = Hash(hash, cluster.FirstInstance);
            hash = Hash(hash, cluster.InstanceCount);
            hash = Hash(hash, cluster.RandomSeed);
        }

        return hash;
    }

    private static FoliageSceneRegistrationSnapshot CreateSnapshot(Scene scene)
    {
        var hash = new HashCode();
        var prototypes = new List<FoliagePrototype>(scene.FoliagePrototypes.Count + scene.FoliagePatches.Count);

        foreach (FoliagePrototype prototype in scene.FoliagePrototypes)
            AddPrototypeIfMissing(prototypes, prototype);
        foreach (FoliagePatch patch in scene.FoliagePatches)
            AddPrototypeIfMissing(prototypes, patch.Prototype);

        hash.Add(prototypes.Count);
        foreach (FoliagePrototype prototype in prototypes)
        {
            hash.Add(RuntimeHelpers.GetHashCode(prototype));
            hash.Add(prototype.Revision);
            hash.Add(prototype.Mesh);
            hash.Add(prototype.Material);
            hash.Add(prototype.GeometryMode);
        }

        int visiblePatchCount = 0;
        hash.Add(scene.FoliagePatches.Count);
        foreach (FoliagePatch patch in scene.FoliagePatches)
        {
            if (patch.Visible)
                visiblePatchCount++;
            hash.Add(RuntimeHelpers.GetHashCode(patch));
            hash.Add(RuntimeHelpers.GetHashCode(patch.Prototype));
            hash.Add(patch.ContentRevision);
            hash.Add(patch.Bounds);
            hash.Add(patch.InstancePosition);
            hash.Add(patch.InstanceScale);
            hash.Add(patch.Density);
            hash.Add(patch.Seed);
            hash.Add(patch.Visible);
            hash.Add(patch.DensityTexture);
        }

        return new FoliageSceneRegistrationSnapshot(
            prototypes.Count,
            scene.FoliagePatches.Count,
            visiblePatchCount,
            unchecked((uint)hash.ToHashCode()));
    }

    private static ulong ComputeContentSignature(Scene scene, FoliageSettings settings)
    {
        ulong hash = 14695981039346656037UL;
        hash = Hash(hash, (uint)scene.FoliagePrototypes.Count);
        hash = Hash(hash, (uint)scene.FoliagePatches.Count);
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(settings.DensityScale));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(settings.MaxDrawDistance));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(settings.GrassShadowDistance));
        hash = Hash(hash, settings.CastShadows ? 1u : 0u);
        hash = Hash(hash, (uint)Math.Max(0, settings.MaxVisibleClusters));

        foreach (FoliagePrototype prototype in scene.FoliagePrototypes)
            hash = HashPrototype(hash, prototype);
        foreach (FoliagePatch patch in scene.FoliagePatches)
        {
            hash = HashPrototype(hash, patch.Prototype);
            hash = Hash(hash, patch.ContentRevision);
            hash = Hash(hash, patch.Seed);
            hash = Hash(hash, patch.Visible ? 1u : 0u);
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(patch.Density));
            hash = HashVector(hash, patch.Bounds.Min);
            hash = HashVector(hash, patch.Bounds.Max);
            hash = HashVector(hash, patch.InstancePosition);
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(patch.InstanceScale));
        }

        return hash;
    }

    private static ulong HashPrototype(ulong hash, FoliagePrototype prototype)
    {
        hash = Hash(hash, prototype.Revision);
        hash = Hash(hash, (uint)prototype.GeometryMode);
        hash = Hash(hash, prototype.Mesh?.GetHashCode() is int meshHash ? unchecked((uint)meshHash) : 0u);
        hash = Hash(hash, prototype.Material?.GetHashCode() is int materialHash ? unchecked((uint)materialHash) : 0u);
        hash = Hash(hash, prototype.AuthoredMeshletStride);
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.CardHeight));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.CardWidth));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Lod.Lod0Distance));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Lod.Lod1Distance));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Lod.Lod2Distance));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Wind.Strength));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Wind.Frequency));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Wind.Flutter));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Lighting.WrapDiffuse));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Lighting.Backlight));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(prototype.Lighting.NormalBend));
        return hash;
    }

    private static ulong HashVector(ulong hash, Vector3 value)
    {
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(value.X));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(value.Y));
        return Hash(hash, BitConverter.SingleToUInt32Bits(value.Z));
    }

    private static ulong Hash(ulong hash, uint value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 1099511628211UL;
            return hash;
        }
    }

    private static void AddPrototypeIfMissing(List<FoliagePrototype> prototypes, FoliagePrototype prototype)
    {
        for (int i = 0; i < prototypes.Count; i++)
        {
            if (ReferenceEquals(prototypes[i], prototype))
                return;
        }

        prototypes.Add(prototype);
    }

    private static int GetPrototypeIndex(List<FoliagePrototype> prototypes, FoliagePrototype prototype)
    {
        for (int i = 0; i < prototypes.Count; i++)
        {
            if (ReferenceEquals(prototypes[i], prototype))
                return i;
        }

        throw new InvalidOperationException("Foliage patch references a prototype that was not registered.");
    }

    private static int EstimateFallbackInstanceCount(FoliagePatch patch)
    {
        uint estimate = EstimateGpuInstanceCount(patch, 1f);
        return estimate >= int.MaxValue ? int.MaxValue : (int)estimate;
    }

    private static uint EstimateGpuInstanceCount(FoliagePatch patch, float densityScale)
    {
        Vector3 size = patch.Bounds.Size;
        float area = Math.Max(0f, size.X) * Math.Max(0f, size.Z);
        if (area <= 0f || patch.Density <= 0f || densityScale <= 0f)
            return 0;

        double requested = Math.Ceiling(area * patch.Density * densityScale);
        if (requested <= 0.0)
            return 0;
        return requested >= uint.MaxValue ? uint.MaxValue : (uint)requested;
    }

    private static IEnumerable<Matrix4x4> GenerateFallbackMatrices(FoliagePatch patch, int count, float instanceScale)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        Vector3 size = patch.Bounds.Size;
        float cellX = side == 0 ? 0f : size.X / side;
        float cellZ = side == 0 ? 0f : size.Z / side;

        for (int i = 0; i < count; i++)
        {
            int x = i % side;
            int z = i / side;
            uint random = Hash(patch.Seed, (uint)i);
            float jitterX = (((random >> 0) & 0xFF) / 255f - 0.5f) * 0.6f;
            float jitterZ = (((random >> 8) & 0xFF) / 255f - 0.5f) * 0.6f;
            float yaw = (((random >> 16) & 0xFFFF) / 65535f) * MathF.Tau;
            float scale = instanceScale * (0.85f + ((random >> 24) & 0xFF) / 255f * 0.3f);

            Vector3 position = new(
                patch.Bounds.Min.X + (x + 0.5f + jitterX) * cellX,
                patch.Bounds.Min.Y,
                patch.Bounds.Min.Z + (z + 0.5f + jitterZ) * cellZ);
            yield return Matrix4x4.CreateScale(new Vector3(scale)) *
                         Matrix4x4.CreateRotationY(yaw) *
                         Matrix4x4.CreateTranslation(position);
        }
    }

    private static uint Hash(uint seed, uint value)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ seed) * 16777619u;
            hash = (hash ^ value) * 16777619u;
            hash ^= hash >> 16;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            hash *= 3266489917u;
            hash ^= hash >> 16;
            return hash;
        }
    }

    private static uint DivideRoundUp(uint value, uint divisor)
    {
        return divisor == 0 ? 0 : (value + divisor - 1) / divisor;
    }

    private static uint CheckedCount(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        return Math.Max(1u, (uint)count);
    }

    private static ulong MaxByteSize(RuntimeBuffer[] buffers)
    {
        ulong max = 0;
        foreach (RuntimeBuffer buffer in buffers)
            max = Math.Max(max, buffer.ByteSize);
        return max;
    }

    private static void ValidateFrameIndex(int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= FramesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
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

        lock (_lock)
        {
            DestroyIfValid(_prototypeBuffer.Handle);
            DestroyIfValid(_patchBuffer.Handle);
            DestroyIfValid(_clusterBuffer.Handle);
            for (int i = 0; i < FramesInFlight; i++)
            {
                DestroyIfValid(_instanceBuffers[i].Handle);
                DestroyIfValid(_visibleClusterBuffers[i].Handle);
                DestroyIfValid(_meshletDrawBuffers[i].Handle);
                DestroyIfValid(_counterBuffers[i].Handle);
                DestroyIfValid(_indirectDispatchBuffers[i].Handle);
                DestroyIfValid(_counterReadbackBuffers[i]);
            }
        }
    }

    private readonly struct RuntimeBuffer
    {
        public RuntimeBuffer(BufferHandle handle, uint elementCapacity, ulong byteSize)
        {
            Handle = handle;
            ElementCapacity = elementCapacity;
            ByteSize = byteSize;
        }

        public BufferHandle Handle { get; }
        public uint ElementCapacity { get; }
        public ulong ByteSize { get; }
    }
}

public readonly record struct FoliageGpuBuildSnapshot(
    int PrototypeCount,
    int PatchCount,
    int ClusterCount,
    int InstanceCount,
    int GrassBladeEstimate,
    ulong ContentSignature,
    ulong ClusterSignature);

public readonly record struct FoliageRuntimeBuffers(
    BufferHandle PrototypeBuffer,
    BufferHandle PatchBuffer,
    BufferHandle ClusterBuffer,
    BufferHandle InstanceBuffer,
    BufferHandle VisibleClusterBuffer,
    BufferHandle MeshletDrawBuffer,
    BufferHandle CounterBuffer,
    BufferHandle IndirectDispatchBuffer,
    ulong VisibleClusterBufferSize,
    ulong MeshletDrawBufferSize,
    ulong CounterBufferSize,
    ulong IndirectDispatchBufferSize,
    int ClusterCount,
    int VisibleClusterCapacity,
    int MeshletDrawCapacity,
    int AuthoredMeshletWorkItemCount,
    uint FirstAuthoredClusterIndex,
    int AuthoredClusterCount);

public readonly record struct FoliageCounterSnapshot(
    int Valid,
    uint VisibleClusterCount,
    uint CulledClusterCount,
    uint Lod0VisibleCount,
    uint Lod1VisibleCount,
    uint Lod2VisibleCount,
    uint HiZTestedCount,
    uint HiZRejectedCount,
    uint VisibleMeshletDrawCount)
{
    public static FoliageCounterSnapshot Invalid { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static FoliageCounterSnapshot FromCounters(GPUFoliageCounters counters)
    {
        return new FoliageCounterSnapshot(
            1,
            counters.VisibleClusterCount,
            counters.CulledClusterCount,
            counters.Lod0VisibleCount,
            counters.Lod1VisibleCount,
            counters.Lod2VisibleCount,
            counters.HiZTestedCount,
            counters.HiZRejectedCount,
            counters.VisibleMeshletDrawCount);
    }
}
