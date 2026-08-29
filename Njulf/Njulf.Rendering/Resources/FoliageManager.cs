using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Assets.Cooked;
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
using StbImageSharp;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

public sealed class FoliageManager : IDisposable
{
    public const int DefaultDebugFallbackMaxInstancesPerPatch = 512;

    public const uint InstancesPerCluster = 16;
    public const ulong ProceduralIndirectDispatchOffset = 0;
    public static readonly ulong AuthoredIndirectDispatchOffset =
        (ulong)Marshal.SizeOf<GPUFoliageDispatchArgs>();
    public static readonly ulong AuthoredExpandIndirectDispatchOffset =
        2UL * (ulong)Marshal.SizeOf<GPUFoliageDispatchArgs>();
    private const uint IndirectDispatchCommandCount = 3;
    private const uint PatchFlagVisible = 1u << 0;
    private const uint PrototypeFlagCastShadows = 1u << 0;
    private const uint PrototypeFlagFarImpostor = 1u << 1;
    public static readonly ulong CounterStride = (ulong)Marshal.SizeOf<GPUFoliageCounters>();

    private static readonly UploadBarrierDescription FoliageUploadBarrier = new(
        PipelineStageFlags2.AllCommandsBit,
        AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

    private readonly VulkanContext? _context;
    private readonly BufferManager? _bufferManager;
    private readonly StagingRing? _stagingRing;
    private readonly MeshManager? _meshManager;
    private readonly MaterialManager? _materialManager;
    private readonly TextureManager? _textureManager;
    private readonly Dictionary<string, DensityMapRuntime> _densityMaps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _densityMapsUsedThisBuild =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImpostorRuntime> _impostors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _impostorsUsedThisBuild =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly FoliageStreamingManager _streamingManager = new();
    private readonly object _lock = new();
    private readonly List<StaticInstanceBatch> _debugFallbackBatches = new();
    private readonly List<FoliagePrototype> _prototypeScratch = new();
    private readonly List<GPUFoliagePrototype> _gpuPrototypeScratch = new();
    private readonly List<GPUFoliageImpostor> _gpuImpostorScratch = new();
    private readonly List<GPUFoliageImpostorView> _gpuImpostorViewScratch =
        new();
    private readonly List<GPUFoliagePatch> _gpuPatchScratch = new();
    private readonly List<GPUFoliageCluster> _gpuClusterScratch = new();
    private readonly List<GPUFoliageInstance> _gpuInstanceScratch = new();
    private RuntimeBuffer _prototypeBuffer;
    private RuntimeBuffer _impostorBuffer;
    private RuntimeBuffer _impostorViewBuffer;
    private RuntimeBuffer _patchBuffer;
    private RuntimeBuffer _clusterBuffer;
    private readonly RuntimeBuffer[] _instanceBuffers = new RuntimeBuffer[FramesInFlight];
    private readonly RuntimeBuffer[] _visibleClusterBuffers = new RuntimeBuffer[FramesInFlight];
    private readonly RuntimeBuffer[] _authoredInstanceCommandBuffers =
        new RuntimeBuffer[FramesInFlight];
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
    private int _lastMissingDensityTextureCount;
    private ulong _lastDensityTextureBytes;
    private int _lastMissingImpostorCount;
    private ulong _lastImpostorAtlasBytes;
    private ulong _streamingFrameSerial;
    private FoliageStreamingSnapshot _lastStreamingSnapshot;

    public FoliageManager()
    {
    }

    public FoliageManager(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing,
        MeshManager meshManager,
        MaterialManager materialManager,
        TextureManager textureManager)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
        _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
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
            if (settings.Enabled)
            {
                _streamingFrameSerial++;
                if (_streamingFrameSerial == 0UL)
                    _streamingFrameSerial = 1UL;
                _lastStreamingSnapshot = _streamingManager.Update(
                    scene.FoliagePatches,
                    sceneData.CameraPosition,
                    _streamingFrameSerial,
                    FoliageStreamingOptions.ForDrawDistance(
                        settings.MaxDrawDistance));
            }
            else
            {
                _streamingManager.Clear();
                _lastStreamingSnapshot = default;
            }
            ulong signature = ComputeContentSignature(scene, settings);
            signature = Hash(
                signature,
                checked((uint)_lastStreamingSnapshot.ResidencyGeneration));
            signature = Hash(
                signature,
                checked((uint)(_lastStreamingSnapshot
                    .ResidencyGeneration >> 32)));
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
                _authoredInstanceCommandBuffers[frameIndex].Handle,
                _meshletDrawBuffers[frameIndex].Handle,
                _counterBuffers[frameIndex].Handle,
                _indirectDispatchBuffers[frameIndex].Handle,
                _visibleClusterBuffers[frameIndex].ByteSize,
                _authoredInstanceCommandBuffers[frameIndex].ByteSize,
                _meshletDrawBuffers[frameIndex].ByteSize,
                _counterBuffers[frameIndex].ByteSize,
                _indirectDispatchBuffers[frameIndex].ByteSize,
                _lastGpuBuildSnapshot.ClusterCount,
                (int)Math.Min(_visibleClusterBuffers[frameIndex].ElementCapacity, int.MaxValue),
                (int)Math.Min(
                    _authoredInstanceCommandBuffers[frameIndex]
                        .ElementCapacity,
                    int.MaxValue),
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
        _gpuImpostorScratch.Clear();
        _gpuImpostorViewScratch.Clear();
        _gpuPatchScratch.Clear();
        _gpuClusterScratch.Clear();
        _gpuInstanceScratch.Clear();
        _lastGrassBladeEstimate = 0;
        _lastOverflowCount = 0;
        _lastAuthoredMeshletDrawCapacity = 0;
        _lastAuthoredClusterCount = 0;
        _lastAuthoredMeshletWorkItemCount = 0;
        _lastFirstAuthoredClusterIndex = uint.MaxValue;
        _lastMissingDensityTextureCount = 0;
        _lastDensityTextureBytes = 0;
        _densityMapsUsedThisBuild.Clear();
        _lastMissingImpostorCount = 0;
        _lastImpostorAtlasBytes = 0;
        _impostorsUsedThisBuild.Clear();

        foreach (FoliagePrototype prototype in scene.FoliagePrototypes)
            AddPrototypeIfMissing(_prototypeScratch, prototype);
        foreach (FoliagePatch patch in scene.FoliagePatches)
            AddPrototypeIfMissing(_prototypeScratch, patch.Prototype);

        foreach (FoliagePrototype prototype in _prototypeScratch)
        {
            ImpostorRuntime? impostor = ResolveImpostor(prototype, settings);
            uint impostorMetadataIndex = uint.MaxValue;
            if (impostor is { IsValid: true })
            {
                impostorMetadataIndex = checked((uint)_gpuImpostorScratch.Count);
                GPUFoliageImpostor metadata = impostor.Metadata;
                metadata.ViewDataOffset = checked(
                    (uint)_gpuImpostorViewScratch.Count);
                _gpuImpostorScratch.Add(metadata);
                _gpuImpostorViewScratch.AddRange(impostor.Views);
            }

            _gpuPrototypeScratch.Add(CreateGpuPrototype(
                prototype,
                settings,
                impostorMetadataIndex));
        }

        uint logicalFirstInstance = 0;
        uint clusterBudget = settings.MaxVisibleClusters <= 0 ? 0u : (uint)settings.MaxVisibleClusters;
        for (int patchIndex = 0;
             patchIndex < scene.FoliagePatches.Count;
             patchIndex++)
        {
            _gpuPatchScratch.Add(default);
        }

        // Procedural inputs precede authored instance candidates. This gives
        // the GPU authored expansion an O(1) cluster mapping and removes the
        // old work-item scan across unrelated clusters.
        for (int authoredPass = 0; authoredPass < 2; authoredPass++)
        for (int patchIndex = 0;
             patchIndex < scene.FoliagePatches.Count;
             patchIndex++)
        {
            FoliagePatch patch = scene.FoliagePatches[patchIndex];
            bool authored = patch.Prototype.GeometryMode ==
                FoliageGeometryMode.AuthoredMeshlets;
            if (authored != (authoredPass == 1))
                continue;
            uint prototypeIndex = (uint)GetPrototypeIndex(_prototypeScratch, patch.Prototype);
            GPUFoliagePrototype gpuPrototype =
                _gpuPrototypeScratch[checked((int)prototypeIndex)];
            MaterialHandle nearFieldMaterial =
                ResolveMaterialHandle(patch.Prototype);
            uint nearFieldMaterialRevision = _materialManager is null
                ? patch.Prototype.Revision
                : _materialManager.GetMaterialContentRevision(
                    checked((int)gpuPrototype.MaterialIndex));
            uint clusterOffset = (uint)_gpuClusterScratch.Count;
            DensityMapRuntime? densityMap = ResolveDensityMap(patch.DensityMap);
            GeneratePatchClusters(
                patch,
                prototypeIndex,
                (uint)patchIndex,
                settings,
                densityMap,
                ref logicalFirstInstance,
                ref clusterBudget);
            uint clusterCount = (uint)_gpuClusterScratch.Count - clusterOffset;

            _gpuPatchScratch[patchIndex] = new GPUFoliagePatch
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
                NearFieldStableObjectId = AccelerationStructureManager
                    .StableInstanceIdentity(patch.Id),
                Seed = patch.Seed,
                Flags = patch.Visible ? PatchFlagVisible : 0u,
                NearFieldStableMaterialId = SceneDataBuilder
                    .CreateNearFieldStableMaterialIdentity(nearFieldMaterial),
                NearFieldPackedObjectMaterialRevisions = SceneDataBuilder
                    .PackNearFieldRevisions(
                        patch.ContentRevision,
                        nearFieldMaterialRevision),
                DensityTextureIndex = densityMap?.BindlessIndex ?? uint.MaxValue,
                TerrainDescriptorIndex = uint.MaxValue,
                PlacementMode = (uint)patch.PlacementMode,
                ContentRevision = patch.ContentRevision,
                DensityUvScaleOffset = patch.DensityMap is { IsValid: true } densityReference
                    ? new Vector4(
                        densityReference.WorldToUvScale.X,
                        densityReference.WorldToUvScale.Y,
                        densityReference.WorldToUvOffset.X,
                        densityReference.WorldToUvOffset.Y)
                    : new Vector4(1f, 1f, 0f, 0f)
            };
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

    private GPUFoliagePrototype CreateGpuPrototype(
        FoliagePrototype prototype,
        FoliageSettings settings,
        uint impostorMetadataIndex)
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
            Flags = ResolvePrototypeFlags(
                prototype,
                settings,
                impostorMetadataIndex != uint.MaxValue),
            ImpostorMetadataIndex = impostorMetadataIndex,
            MeshletOutputClass = 0u,
            BladeHeight = prototype.CardHeight,
            BladeWidth = prototype.CardWidth,
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

    private static uint ResolvePrototypeFlags(
        FoliagePrototype prototype,
        FoliageSettings settings,
        bool impostorRuntimeReady)
    {
        uint flags = settings.CastShadows && prototype.CastShadows
            ? PrototypeFlagCastShadows
            : 0u;
        if (settings.FarImpostorsEnabled &&
            prototype.FarImpostorEnabled &&
            impostorRuntimeReady &&
            prototype.GeometryMode == FoliageGeometryMode.AuthoredMeshlets)
        {
            flags |= PrototypeFlagFarImpostor;
        }

        return flags;
    }

    private uint ResolveMaterialIndex(FoliagePrototype prototype)
    {
        MaterialHandle handle = ResolveMaterialHandle(prototype);
        if (_materialManager == null)
            return handle.IsValid ? checked((uint)handle.Index) : 0u;
        try
        {
            return (uint)_materialManager.ResolveMaterialIndex(handle);
        }
        catch (InvalidOperationException)
        {
            return (uint)_materialManager.ResolveMaterialIndex(_materialManager.DefaultMaterialHandle);
        }
    }

    private MaterialHandle ResolveMaterialHandle(FoliagePrototype prototype)
    {
        if (_materialManager == null)
        {
            return prototype.Material is MaterialHandle materialHandle &&
                materialHandle.IsValid
                    ? materialHandle
                    : MaterialHandle.Invalid;
        }

        return SceneDataBuilder.ResolveRenderObjectMaterialHandle(
            prototype.Material,
            _materialManager.DefaultMaterialHandle,
            prototype.Name);
    }

    private void GeneratePatchClusters(
        FoliagePatch patch,
        uint prototypeIndex,
        uint patchIndex,
        FoliageSettings settings,
        DensityMapRuntime? densityMap,
        ref uint logicalFirstInstance,
        ref uint remainingClusterBudget)
    {
        if (patch.Prototype.GeometryMode == FoliageGeometryMode.AuthoredMeshlets)
        {
            GenerateAuthoredMeshletClusters(
                patch,
                prototypeIndex,
                patchIndex,
                settings,
                densityMap,
                ref logicalFirstInstance,
                ref remainingClusterBudget);
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
            if (!_streamingManager.IsResident(
                    FoliageCellKey.FromWorld(patch, center)))
            {
                remainingClusterBudget++;
                continue;
            }
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
            if (!_streamingManager.IsResident(
                    FoliageCellKey.FromWorld(patch, center)))
            {
                remainingClusterBudget++;
                continue;
            }
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

    private void GenerateAuthoredMeshletClusters(
        FoliagePatch patch,
        uint prototypeIndex,
        uint patchIndex,
        FoliageSettings settings,
        DensityMapRuntime? densityMap,
        ref uint logicalFirstInstance,
        ref uint remainingClusterBudget)
    {
        GPUFoliagePrototype prototype = _gpuPrototypeScratch[(int)prototypeIndex];
        if (prototype.MeshletCount == 0)
            return;
        IReadOnlyList<FoliagePlacementCandidate> placements =
            FoliagePlacementBuilder.Build(
                patch,
                settings.DensityScale,
                densityMap == null
                    ? null
                    : worldPosition => densityMap.SampleWorld(
                        worldPosition,
                        patch.DensityMap!));
        if (!patch.Visible || placements.Count == 0)
            return;
        int emittedCount = checked((int)Math.Min(
            (uint)placements.Count,
            remainingClusterBudget));
        _lastOverflowCount += placements.Count - emittedCount;

        Vector3 localMin = patch.Bounds.Min - patch.InstancePosition;
        Vector3 localMax = patch.Bounds.Max - patch.InstancePosition;
        if (patch.Prototype.Mesh is MeshHandle meshHandle &&
            meshHandle.IsValid && _meshManager != null)
        {
            try
            {
                MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
                localMin = new Vector3(
                    meshInfo.BoundingBoxMin.X,
                    meshInfo.BoundingBoxMin.Y,
                    meshInfo.BoundingBoxMin.Z);
                localMax = new Vector3(
                    meshInfo.BoundingBoxMax.X,
                    meshInfo.BoundingBoxMax.Y,
                    meshInfo.BoundingBoxMax.Z);
            }
            catch (InvalidOperationException)
            {
                // Preserve the authored patch bounds as the conservative
                // fallback when a mesh lease is concurrently unavailable.
            }
        }
        Vector3 localCenter = (localMin + localMax) * 0.5f;
        float localRadius = (localMax - localMin).Length() * 0.5f;
        float windExpansion = patch.Prototype.Wind.Strength *
            (1f + patch.Prototype.Wind.Flutter) * 0.075f;
        uint transitionSafeMeshletCount = checked(
            prototype.MeshletCount +
            prototype.MeshletLod1Count +
            prototype.MeshletLod2Count);

        for (int placementIndex = 0;
             placementIndex < emittedCount;
             placementIndex++)
        {
            FoliagePlacementCandidate placement = placements[placementIndex];
            if (!_streamingManager.IsResident(
                    FoliageCellKey.FromWorld(
                        patch,
                        placement.Position)))
            {
                remainingClusterBudget++;
                continue;
            }
            uint instanceIndex = logicalFirstInstance++;
            uint clusterIndex = (uint)_gpuClusterScratch.Count;
            if (_lastFirstAuthoredClusterIndex == uint.MaxValue)
                _lastFirstAuthoredClusterIndex = clusterIndex;
            _lastAuthoredClusterCount++;
            float scale = Math.Max(0.0001f, placement.Scale);
            // Root-centred bounds remain conservative under arbitrary yaw and
            // optional terrain-normal alignment.
            Vector3 worldCenter = placement.Position;
            float radius = (localRadius + localCenter.Length()) * scale +
                windExpansion;
            Vector3 surfaceNormal = placement.SurfaceNormal.LengthSquared() >
                0.000001f
                    ? placement.SurfaceNormal.Normalized()
                    : Vector3.UnitY;

            _gpuInstanceScratch.Add(new GPUFoliageInstance
            {
                PositionScale = new Vector4(
                    placement.Position.X,
                    placement.Position.Y,
                    placement.Position.Z,
                    scale),
                RotationWind = new Vector4(
                    placement.YawRadians,
                    (placement.StableIdentity & 0xffffu) / 65535f,
                    patch.Placement.AlignToSurfaceNormal
                        ? surfaceNormal.X
                        : 0f,
                    patch.Placement.AlignToSurfaceNormal
                        ? surfaceNormal.Z
                        : 0f),
                ColorVariation = new Vector4(1f, 1f, 1f, 1f),
                PrototypeIndex = prototypeIndex,
                PatchIndex = patchIndex,
                ClusterIndex = clusterIndex,
                Flags = patch.Placement.AlignToSurfaceNormal ? 1u : 0u
            });

            _gpuClusterScratch.Add(new GPUFoliageCluster
            {
                WorldCenterRadius = new Vector4(
                    worldCenter.X,
                    worldCenter.Y,
                    worldCenter.Z,
                    radius),
                BoundsMinDensity = new Vector4(
                    worldCenter.X - radius,
                    worldCenter.Y - radius,
                    worldCenter.Z - radius,
                    patch.Density),
                BoundsMaxLod = new Vector4(
                    worldCenter.X + radius,
                    worldCenter.Y + radius,
                    worldCenter.Z + radius,
                    patch.Prototype.Lod.Lod2Distance),
                PatchIndex = patchIndex,
                FirstInstance = instanceIndex,
                InstanceCount = 1u,
                RandomSeed = placement.StableIdentity
            });
            _lastAuthoredMeshletDrawCapacity = checked(
                _lastAuthoredMeshletDrawCapacity +
                (int)transitionSafeMeshletCount);
        }
        remainingClusterBudget -= checked((uint)emittedCount);
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
        sceneData.FoliageImpostorAtlasBytes = _lastImpostorAtlasBytes;
        sceneData.FoliageMissingImpostorCount = _lastMissingImpostorCount;
        sceneData.FoliageDensityTextureBytes = _lastDensityTextureBytes;
        sceneData.FoliageMissingDensityTextureCount =
            _lastMissingDensityTextureCount;
        sceneData.FoliageResidentCellCount =
            _lastStreamingSnapshot.ResidentCellCount;
        sceneData.FoliagePendingCellCount =
            _lastStreamingSnapshot.PendingCellCount;
        sceneData.FoliageRetiringCellCount =
            _lastStreamingSnapshot.RetiringCellCount;
        sceneData.FoliageNearCellCount = _lastStreamingSnapshot.NearCellCount;
        sceneData.FoliageMidCellCount = _lastStreamingSnapshot.MidCellCount;
        sceneData.FoliageFarCellCount = _lastStreamingSnapshot.FarCellCount;
        sceneData.FoliageCellLoadsThisFrame =
            _lastStreamingSnapshot.LoadedThisFrame;
        sceneData.FoliageCellRetirementsThisFrame =
            _lastStreamingSnapshot.RetiredThisFrame;
        sceneData.FoliageCellStreamingUploadBytes =
            _lastStreamingSnapshot.ScheduledUploadBytes;
        sceneData.FoliageCellStreamingOverflowCount =
            _lastStreamingSnapshot.CandidateOverflowCount;
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
        EnsureCapacity(ref _impostorBuffer, CheckedCount(_gpuImpostorScratch.Count), (ulong)Marshal.SizeOf<GPUFoliageImpostor>(), "Foliage.ImpostorMetadataBuffer");
        EnsureCapacity(ref _impostorViewBuffer, CheckedCount(_gpuImpostorViewScratch.Count), (ulong)Marshal.SizeOf<GPUFoliageImpostorView>(), "Foliage.ImpostorViewBuffer");
        EnsureCapacity(ref _patchBuffer, CheckedCount(_gpuPatchScratch.Count), (ulong)Marshal.SizeOf<GPUFoliagePatch>(), "Foliage.PatchBuffer");
        EnsureCapacity(ref _clusterBuffer, CheckedCount(_gpuClusterScratch.Count), (ulong)Marshal.SizeOf<GPUFoliageCluster>(), "Foliage.ClusterBuffer");

        uint visibleClusterCapacity = CheckedCount(_gpuClusterScratch.Count);
        uint instanceCapacity = CheckedCount(_gpuInstanceScratch.Count);
        int requestedDrawCapacity = Math.Max(_gpuClusterScratch.Count, _lastAuthoredMeshletDrawCapacity);
        uint drawCapacity = CheckedCount(Math.Min(Math.Max(1, settings.MaxVisibleMeshletDraws), Math.Max(1, requestedDrawCapacity)));
        _lastAuthoredMeshletWorkItemCount = _lastAuthoredClusterCount;
        for (int i = 0; i < FramesInFlight; i++)
        {
            EnsureCapacity(ref _instanceBuffers[i], instanceCapacity, (ulong)Marshal.SizeOf<GPUFoliageInstance>(), $"Foliage.InstanceBuffer.Frame{i}");
            EnsureCapacity(ref _visibleClusterBuffers[i], visibleClusterCapacity, (ulong)Marshal.SizeOf<GPUFoliageProceduralDrawCommand>(), $"Foliage.VisibleClusterBuffer.Frame{i}");
            EnsureCapacity(
                ref _authoredInstanceCommandBuffers[i],
                CheckedCount(_lastAuthoredClusterCount),
                (ulong)Marshal.SizeOf<GPUFoliageAuthoredInstanceCommand>(),
                $"Foliage.AuthoredInstanceCommandBuffer.Frame{i}");
            EnsureCapacity(ref _meshletDrawBuffers[i], drawCapacity, (ulong)Marshal.SizeOf<GPUFoliageMeshletDrawCommand>(), $"Foliage.MeshletDrawBuffer.Frame{i}");
            EnsureCapacity(ref _counterBuffers[i], 1u, CounterStride, $"Foliage.CounterBuffer.Frame{i}");
            EnsureCapacity(ref _indirectDispatchBuffers[i], IndirectDispatchCommandCount, (ulong)Marshal.SizeOf<GPUFoliageDispatchArgs>(), $"Foliage.IndirectDispatchBuffer.Frame{i}", BufferUsageFlags.IndirectBufferBit);
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
        uploaded += UploadSpan(CollectionsMarshal.AsSpan(_gpuImpostorScratch), _impostorBuffer.Handle, commandBuffer);
        uploaded += UploadSpan(CollectionsMarshal.AsSpan(_gpuImpostorViewScratch), _impostorViewBuffer.Handle, commandBuffer);
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
        RegisterStorageBuffer(BindlessIndex.FoliageImpostorMetadataBuffer, _impostorBuffer.Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageImpostorViewBuffer, _impostorViewBuffer.Handle);
        RegisterStorageBuffer(BindlessIndex.FoliagePatchBuffer, _patchBuffer.Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageClusterBuffer, _clusterBuffer.Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageInstanceBufferBase, _instanceBuffers[0].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageInstanceBufferFrame1, _instanceBuffers[1].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageVisibleClusterBufferBase, _visibleClusterBuffers[0].Handle);
        RegisterStorageBuffer(BindlessIndex.FoliageVisibleClusterBufferFrame1, _visibleClusterBuffers[1].Handle);
        RegisterStorageBuffer(
            BindlessIndex.FoliageAuthoredInstanceCommandBufferBase,
            _authoredInstanceCommandBuffers[0].Handle);
        RegisterStorageBuffer(
            BindlessIndex.FoliageAuthoredInstanceCommandBufferFrame1,
            _authoredInstanceCommandBuffers[1].Handle);
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
            hash.Add(patch.DensityMap?.SourcePath);
            hash.Add(patch.DensityMap?.ContentHash);
            hash.Add(patch.PlacementMode);
            hash.Add(patch.Placement.Revision);
            hash.Add(patch.TerrainQuery?.Revision ?? 0UL);
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
        hash = Hash(hash, settings.FarImpostorsEnabled ? 1u : 0u);
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
        hash = Hash(hash, prototype.FarImpostorEnabled ? 1u : 0u);
        hash = Hash(hash, prototype.CastShadows ? 1u : 0u);
        hash = Hash(hash, prototype.TwoSided ? 1u : 0u);
        hash = Hash(hash, unchecked((uint)(prototype.Impostor?.ContentHash?.GetHashCode(
            StringComparison.Ordinal) ?? 0)));
        if (prototype.Impostor is { } impostor)
        {
            ulong layout = ComputeImpostorLayoutSignature(impostor);
            hash = Hash(hash, unchecked((uint)layout));
            hash = Hash(hash, unchecked((uint)(layout >> 32)));
        }
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

    private DensityMapRuntime? ResolveDensityMap(
        FoliageDensityMapReference? reference)
    {
        if (reference == null)
            return null;
        if (!reference.IsValid ||
            reference.Format != FoliageDensityMapFormat.R8UNorm)
        {
            _lastMissingDensityTextureCount++;
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(reference.SourcePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            _lastMissingDensityTextureCount++;
            return null;
        }

        string key = string.Concat(
            fullPath,
            "|",
            reference.ContentHash ?? string.Empty,
            "|",
            reference.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        if (!_densityMaps.TryGetValue(key, out DensityMapRuntime? runtime))
        {
            runtime = LoadDensityMap(fullPath, reference);
            _densityMaps.Add(key, runtime);
        }
        if (!runtime.IsValid)
        {
            _lastMissingDensityTextureCount++;
            return null;
        }
        if (_densityMapsUsedThisBuild.Add(key))
            _lastDensityTextureBytes = checked(
                _lastDensityTextureBytes + runtime.ByteCount);
        return runtime;
    }

    private DensityMapRuntime LoadDensityMap(
        string fullPath,
        FoliageDensityMapReference reference)
    {
        try
        {
            byte[] encoded = File.ReadAllBytes(fullPath);
            ImageResult image = ImageResult.FromMemory(
                encoded,
                ColorComponents.RedGreenBlueAlpha);
            if (image.Width != reference.Width ||
                image.Height != reference.Height ||
                image.Data.Length != checked(image.Width * image.Height * 4))
            {
                return DensityMapRuntime.Invalid;
            }

            TextureHandle handle = TextureHandle.Invalid;
            uint bindlessIndex = uint.MaxValue;
            if (_textureManager != null)
            {
                handle = _textureManager.LoadTextureFromFile(
                    fullPath,
                    generateMipmaps: false,
                    srgb: false,
                    requireWithinMemoryBudget: true,
                    semantic: TextureSemantic.Scalar);
                int resolvedIndex = _textureManager.GetBindlessTextureIndex(
                    handle);
                if (resolvedIndex < BindlessIndex.FirstDynamicTextureIndex)
                {
                    _textureManager.ReleaseTexture(handle);
                    return DensityMapRuntime.Invalid;
                }
                bindlessIndex = checked((uint)resolvedIndex);
            }

            return new DensityMapRuntime(
                image.Data,
                image.Width,
                image.Height,
                handle,
                bindlessIndex);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or
                NotSupportedException or VulkanException)
        {
            return DensityMapRuntime.Invalid;
        }
    }

    private ImpostorRuntime? ResolveImpostor(
        FoliagePrototype prototype,
        FoliageSettings settings)
    {
        if (!settings.FarImpostorsEnabled ||
            !prototype.FarImpostorEnabled ||
            prototype.GeometryMode != FoliageGeometryMode.AuthoredMeshlets)
        {
            return null;
        }

        FoliageImpostorAsset? asset = prototype.Impostor;
        if (_textureManager == null || asset is not { IsComplete: true })
        {
            _lastMissingImpostorCount++;
            return null;
        }

        string albedoPath;
        string normalPath;
        string depthPath;
        try
        {
            albedoPath = Path.GetFullPath(asset.AlbedoOpacityAtlasPath);
            normalPath = Path.GetFullPath(asset.NormalAtlasPath);
            depthPath = Path.GetFullPath(asset.DepthAtlasPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            _lastMissingImpostorCount++;
            return null;
        }

        string key = string.Concat(
            albedoPath,
            "|",
            normalPath,
            "|",
            depthPath,
            "|",
            asset.ContentHash ?? string.Empty,
            "|",
            asset.ViewCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "|",
            ComputeImpostorLayoutSignature(asset).ToString("x16",
                System.Globalization.CultureInfo.InvariantCulture));
        if (!_impostors.TryGetValue(key, out ImpostorRuntime? runtime))
        {
            runtime = LoadImpostor(asset, albedoPath, normalPath, depthPath);
            _impostors.Add(key, runtime);
        }

        if (!runtime.IsValid)
        {
            _lastMissingImpostorCount++;
            return null;
        }

        if (_impostorsUsedThisBuild.Add(key))
        {
            _lastImpostorAtlasBytes = checked(
                _lastImpostorAtlasBytes + runtime.ByteCount);
        }

        return runtime;
    }

    private ImpostorRuntime LoadImpostor(
        FoliageImpostorAsset asset,
        string albedoPath,
        string normalPath,
        string depthPath)
    {
        if (_textureManager == null)
            return ImpostorRuntime.Invalid;

        TextureHandle albedo = TextureHandle.Invalid;
        TextureHandle normal = TextureHandle.Invalid;
        TextureHandle depth = TextureHandle.Invalid;
        try
        {
            albedo = _textureManager.LoadTextureFromFile(
                albedoPath,
                generateMipmaps: true,
                srgb: true,
                requireWithinMemoryBudget: true,
                semantic: TextureSemantic.Color,
                mipPolicy: RuntimeTextureMipPolicy.AlphaMask(0.05f));
            normal = _textureManager.LoadTextureFromFile(
                normalPath,
                generateMipmaps: true,
                srgb: false,
                requireWithinMemoryBudget: true,
                semantic: TextureSemantic.Normal);
            depth = _textureManager.LoadTextureFromFile(
                depthPath,
                generateMipmaps: true,
                srgb: false,
                requireWithinMemoryBudget: true,
                semantic: TextureSemantic.Scalar);

            uint albedoIndex = ResolveDynamicTextureIndex(albedo);
            uint normalIndex = ResolveDynamicTextureIndex(normal);
            uint depthIndex = ResolveDynamicTextureIndex(depth);
            ulong byteCount = checked(
                EstimateTextureBytes(albedo) +
                EstimateTextureBytes(normal) +
                EstimateTextureBytes(depth));
            var metadata = new GPUFoliageImpostor
            {
                AlbedoOpacityTextureIndex = albedoIndex,
                NormalTextureIndex = normalIndex,
                DepthTextureIndex = depthIndex,
                ViewCount = checked((uint)asset.ViewCount),
                SourceBoundsMinScale = new Vector4(
                    asset.SourceBounds.Min.X,
                    asset.SourceBounds.Min.Y,
                    asset.SourceBounds.Min.Z,
                    asset.Scale),
                SourceBoundsMax = new Vector4(
                    asset.SourceBounds.Max.X,
                    asset.SourceBounds.Max.Y,
                    asset.SourceBounds.Max.Z,
                    0f),
                Pivot = new Vector3(
                    asset.Pivot.X,
                    asset.Pivot.Y,
                    asset.Pivot.Z),
                ViewDataOffset = 0u
            };
            GPUFoliageImpostorView[] views = CreateImpostorViews(asset);
            return new ImpostorRuntime(
                metadata,
                views,
                albedo,
                normal,
                depth,
                byteCount);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or
                NotSupportedException or OverflowException or VulkanException)
        {
            ReleaseTextureIfValid(albedo);
            ReleaseTextureIfValid(normal);
            ReleaseTextureIfValid(depth);
            return ImpostorRuntime.Invalid;
        }
    }

    private static GPUFoliageImpostorView[] CreateImpostorViews(
        FoliageImpostorAsset asset)
    {
        var views = new GPUFoliageImpostorView[asset.ViewCount];
        for (int index = 0; index < views.Length; index++)
        {
            Vector3 direction;
            Vector4 rectangle;
            if (asset.HasExplicitViewLayout)
            {
                direction = asset.ViewDirections[index].Normalized();
                rectangle = asset.AtlasRectangles[index];
            }
            else
            {
                float angle = index * (2f * MathF.PI / asset.ViewCount);
                direction = new Vector3(
                    MathF.Sin(angle),
                    0f,
                    MathF.Cos(angle));
                float width = 1f / asset.ViewCount;
                rectangle = new Vector4(index * width, 0f, width, 1f);
            }

            views[index] = new GPUFoliageImpostorView
            {
                Direction = new Vector4(
                    direction.X,
                    direction.Y,
                    direction.Z,
                    0f),
                AtlasRectangle = rectangle
            };
        }
        return views;
    }

    private static ulong ComputeImpostorLayoutSignature(
        FoliageImpostorAsset asset)
    {
        ulong hash = 14695981039346656037UL;
        hash = Hash(hash, checked((uint)Math.Max(asset.ViewCount, 0)));
        hash = Hash(hash, checked((uint)Math.Max(asset.AtlasWidth, 0)));
        hash = Hash(hash, checked((uint)Math.Max(asset.AtlasHeight, 0)));
        hash = HashVector(hash, asset.SourceBounds.Min);
        hash = HashVector(hash, asset.SourceBounds.Max);
        hash = HashVector(hash, asset.Pivot);
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(asset.Scale));
        foreach (Vector3 direction in asset.ViewDirections)
            hash = HashVector(hash, direction);
        foreach (Vector4 rectangle in asset.AtlasRectangles)
        {
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(rectangle.X));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(rectangle.Y));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(rectangle.Z));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(rectangle.W));
        }
        return hash;
    }

    private uint ResolveDynamicTextureIndex(TextureHandle handle)
    {
        if (_textureManager == null || !handle.IsValid)
        {
            throw new InvalidOperationException(
                "Foliage impostor texture did not produce a valid handle.");
        }

        int resolvedIndex = _textureManager.GetBindlessTextureIndex(handle);
        if (resolvedIndex < BindlessIndex.FirstDynamicTextureIndex)
        {
            throw new InvalidOperationException(
                "Foliage impostor texture did not receive a dynamic bindless index.");
        }

        return checked((uint)resolvedIndex);
    }

    private ulong EstimateTextureBytes(TextureHandle handle)
    {
        if (_textureManager == null)
            return 0;
        (_, _, Extent3D extent) = _textureManager.GetTextureInfo(handle);
        // Runtime foliage atlases generate a complete mip chain. Four bytes
        // per texel times 4/3 is a conservative, format-independent diagnostic.
        ulong baseBytes = checked((ulong)extent.Width * extent.Height * 4UL);
        return checked(baseBytes + (baseBytes + 2UL) / 3UL);
    }

    private void ReleaseTextureIfValid(TextureHandle handle)
    {
        if (_textureManager != null && handle.IsValid)
            _textureManager.ReleaseTexture(handle);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_lock)
        {
            if (_textureManager != null)
            {
                foreach (DensityMapRuntime densityMap in _densityMaps.Values)
                {
                    if (densityMap.Texture.IsValid)
                        _textureManager.ReleaseTexture(densityMap.Texture);
                }
            }
            _densityMaps.Clear();
            if (_textureManager != null)
            {
                foreach (ImpostorRuntime impostor in _impostors.Values)
                    impostor.Release(_textureManager);
            }
            _impostors.Clear();
            DestroyIfValid(_prototypeBuffer.Handle);
            DestroyIfValid(_impostorBuffer.Handle);
            DestroyIfValid(_impostorViewBuffer.Handle);
            DestroyIfValid(_patchBuffer.Handle);
            DestroyIfValid(_clusterBuffer.Handle);
            for (int i = 0; i < FramesInFlight; i++)
            {
                DestroyIfValid(_instanceBuffers[i].Handle);
                DestroyIfValid(_visibleClusterBuffers[i].Handle);
                DestroyIfValid(_authoredInstanceCommandBuffers[i].Handle);
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

    private sealed class DensityMapRuntime
    {
        public static DensityMapRuntime Invalid { get; } = new(
            Array.Empty<byte>(),
            0,
            0,
            TextureHandle.Invalid,
            uint.MaxValue);

        public DensityMapRuntime(
            byte[] rgbaPixels,
            int width,
            int height,
            TextureHandle texture,
            uint bindlessIndex)
        {
            RgbaPixels = rgbaPixels;
            Width = width;
            Height = height;
            Texture = texture;
            BindlessIndex = bindlessIndex;
        }

        public byte[] RgbaPixels { get; }
        public int Width { get; }
        public int Height { get; }
        public TextureHandle Texture { get; }
        public uint BindlessIndex { get; }
        public bool IsValid => Width > 0 && Height > 0 &&
            RgbaPixels.Length == Width * Height * 4;
        public ulong ByteCount => checked((ulong)RgbaPixels.Length);

        public float SampleWorld(
            Vector2 worldPosition,
            FoliageDensityMapReference reference)
        {
            float u = Math.Clamp(
                worldPosition.X * reference.WorldToUvScale.X +
                reference.WorldToUvOffset.X,
                0f,
                1f);
            float v = Math.Clamp(
                worldPosition.Y * reference.WorldToUvScale.Y +
                reference.WorldToUvOffset.Y,
                0f,
                1f);
            int x = Math.Clamp(
                (int)MathF.Round(u * Math.Max(0, Width - 1)),
                0,
                Width - 1);
            int y = Math.Clamp(
                (int)MathF.Round(v * Math.Max(0, Height - 1)),
                0,
                Height - 1);
            return RgbaPixels[(y * Width + x) * 4] / 255f;
        }
    }

    private sealed class ImpostorRuntime
    {
        public static ImpostorRuntime Invalid { get; } = new(
            default,
            [],
            TextureHandle.Invalid,
            TextureHandle.Invalid,
            TextureHandle.Invalid,
            0);

        public ImpostorRuntime(
            GPUFoliageImpostor metadata,
            GPUFoliageImpostorView[] views,
            TextureHandle albedoOpacity,
            TextureHandle normal,
            TextureHandle depth,
            ulong byteCount)
        {
            Metadata = metadata;
            Views = views;
            AlbedoOpacity = albedoOpacity;
            Normal = normal;
            Depth = depth;
            ByteCount = byteCount;
        }

        public GPUFoliageImpostor Metadata { get; }
        public GPUFoliageImpostorView[] Views { get; }
        public TextureHandle AlbedoOpacity { get; }
        public TextureHandle Normal { get; }
        public TextureHandle Depth { get; }
        public ulong ByteCount { get; }
        public bool IsValid =>
            AlbedoOpacity.IsValid && Normal.IsValid && Depth.IsValid &&
            Metadata.ViewCount > 0 &&
            Views.Length == Metadata.ViewCount &&
            Metadata.AlbedoOpacityTextureIndex >=
                BindlessIndex.FirstDynamicTextureIndex &&
            Metadata.NormalTextureIndex >=
                BindlessIndex.FirstDynamicTextureIndex &&
            Metadata.DepthTextureIndex >=
                BindlessIndex.FirstDynamicTextureIndex;

        public void Release(TextureManager textureManager)
        {
            if (AlbedoOpacity.IsValid)
                textureManager.ReleaseTexture(AlbedoOpacity);
            if (Normal.IsValid)
                textureManager.ReleaseTexture(Normal);
            if (Depth.IsValid)
                textureManager.ReleaseTexture(Depth);
        }
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
    BufferHandle AuthoredInstanceCommandBuffer,
    BufferHandle MeshletDrawBuffer,
    BufferHandle CounterBuffer,
    BufferHandle IndirectDispatchBuffer,
    ulong VisibleClusterBufferSize,
    ulong AuthoredInstanceCommandBufferSize,
    ulong MeshletDrawBufferSize,
    ulong CounterBufferSize,
    ulong IndirectDispatchBufferSize,
    int ClusterCount,
    int VisibleClusterCapacity,
    int AuthoredInstanceCommandCapacity,
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
    uint VisibleMeshletDrawCount,
    uint MeshletDrawOverflowCount,
    uint FarImpostorVisibleCount,
    uint DensityRejectedCount,
    uint InvalidCommandCount)
{
    public static FoliageCounterSnapshot Invalid { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

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
            counters.VisibleMeshletDrawCount,
            counters.MeshletDrawOverflowCount,
            counters.FarImpostorVisibleCount,
            counters.DensityRejectedCount,
            counters.InvalidCommandCount);
    }
}
