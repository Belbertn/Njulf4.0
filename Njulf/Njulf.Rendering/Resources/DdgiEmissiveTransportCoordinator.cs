using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns the complete renderer-level DDGI emissive source-table transaction,
/// including deterministic CPU selection, GPU buffers, diagnostics, and B3
/// refinement demands. Per-frame inputs are borrowed synchronously only.
/// </summary>
internal sealed class DdgiEmissiveTransportCoordinator : IDisposable
{
    private const ulong HashStart = 14695981039346656037UL;
    private const ulong HashPrime = 1099511628211UL;

    private const int MaxDdgiEmissiveMeshSourceCount =
        GlobalIlluminationSettings.MaxDdgiEmissiveTriangleBudget;

    private const int MaxDdgiEmissiveSourceCount =
        MaxDdgiEmissiveMeshSourceCount +
        DdgiVfxMacroEmitterReducer.DefaultMaximumSourceCount;

    private const int MaximumDdgiEmissiveRuntimeRecordScans = 262144;

    private static readonly ulong DdgiEmissiveSourceStride =
        (ulong)Marshal.SizeOf<GPUDdgiEmissiveSource>();

    private static readonly ulong DdgiEmissiveSurfaceStride =
        (ulong)Marshal.SizeOf<GPUDdgiEmissiveSurface>();

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly MeshManager _meshManager;
    private readonly MaterialManager _materialManager;

    private readonly GPUDdgiEmissiveSource[] _ddgiEmissiveSourceScratch =
        new GPUDdgiEmissiveSource[MaxDdgiEmissiveSourceCount];

    private readonly GPUDdgiEmissiveSurface[] _ddgiEmissiveSurfaceScratch =
        new GPUDdgiEmissiveSurface[MaxDdgiEmissiveSourceCount];

    private readonly float[] _ddgiEmissiveSourceImportanceScratch =
        new float[MaxDdgiEmissiveSourceCount];

    private readonly DdgiEmissiveTableCache _ddgiEmissiveTableCache =
        new(MaxDdgiEmissiveMeshSourceCount);

    private readonly DdgiEmissiveSourceSetBuilder
        _ddgiEmissiveSourceSetBuilder = new(MaxDdgiEmissiveSourceCount);

    private readonly double[] _ddgiEmissiveCombinedImportanceScratch =
        new double[MaxDdgiEmissiveSourceCount];

    private readonly DdgiEmissiveSpatialHierarchy
        _ddgiEmissiveSpatialHierarchy = new(MaxDdgiEmissiveSourceCount);

    private readonly DdgiVfxMacroEmitterReducer
        _ddgiVfxMacroEmitterReducer = new();

    private readonly DdgiVfxMacroEmitter[] _ddgiVfxMacroEmitterScratch =
        new DdgiVfxMacroEmitter[
            DdgiVfxMacroEmitterReducer.DefaultMaximumSourceCount];

    private readonly List<SimpleDdgiRefinementDemand>
        _simpleDdgiRefinementEmissiveDemandScratch = new(
            SimpleDdgiRefinementEmissiveDemandBuilder.MaximumDemandCount);

    private DdgiVfxMacroReductionResult _ddgiVfxMacroReductionResult;
    private BufferHandle _ddgiEmissiveSourceBuffer = BufferHandle.Invalid;
    private BufferHandle _ddgiEmissiveSurfaceBuffer = BufferHandle.Invalid;
    private ulong _ddgiEmissiveSourceBufferSize;
    private ulong _ddgiEmissiveSurfaceBufferSize;
    private bool _ddgiEmissiveSourceBufferContentValid;
    private ulong _ddgiEmissiveSourceUploadCount;
    private int _ddgiEmissiveSourceCount;
    private int _ddgiEmissiveHierarchyNodeCount;
    private uint _ddgiEmissiveSourceRevision;
    private ulong _lastDdgiEmissiveSourceSignature;
    private ulong _lastDdgiEmissiveBasePayloadSignature;
    private ulong _lastDdgiVfxMacroRevision;
    private DdgiEmissiveTriangleTableStats _ddgiEmissiveTriangleTableStats;
    private int _ddgiEmissiveSkippedSkinnedObjectCount;
    private double _ddgiEmissiveSkippedSkinnedImportance;
    private int _ddgiEmissiveExcludedCandidateCount;
    private double _ddgiEmissiveExcludedImportance;
    private int _ddgiEmissiveRuntimeRecordScanCount;
    private DdgiEmissiveEnergyDiagnostics _ddgiEmissiveEnergyDiagnostics;
    private bool _hasDdgiEmissiveEnergyDiagnostics;
    private ulong _ddgiEmissiveEnergyWarningCount;
    private string _lastDdgiEmissiveEnergyWarning = string.Empty;

    private SimpleDdgiRefinementEmissiveDemandDiagnostics
        _simpleDdgiRefinementEmissiveDemandDiagnostics;

    private bool _hasSimpleDdgiRefinementEmissiveDemandSignature;
    private ulong _simpleDdgiRefinementEmissiveDemandSignature;
    private bool _disposed;

    public DdgiEmissiveTransportSnapshot Snapshot { get; private set; }

    public DdgiEmissiveTransportCoordinator(
        VulkanContext context,
        BufferManager bufferManager,
        MeshManager meshManager,
        MaterialManager materialManager)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
                         throw new ArgumentNullException(nameof(bufferManager));
        _meshManager = meshManager ??
                       throw new ArgumentNullException(nameof(meshManager));
        _materialManager = materialManager ??
                           throw new ArgumentNullException(nameof(materialManager));

        try
        {
            _ddgiEmissiveSourceBuffer = CreateDdgiEmissiveSourceBuffer();
            _ddgiEmissiveSurfaceBuffer = CreateDdgiEmissiveSurfaceBuffer();
        }
        catch
        {
            DestroyBuffers();
            throw;
        }

        Snapshot = CaptureSnapshot(
            active: false,
            triangleSampling: false,
            triangleBudget: 0);
    }

    public void Register(BindlessHeap bindlessHeap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bindlessHeap);
        if (!_ddgiEmissiveSourceBuffer.IsValid)
            return;

        bindlessHeap.RegisterStorageBuffer(
            BindlessIndex.SimpleDdgiEmissiveSourceBuffer,
            _bufferManager.GetBuffer(_ddgiEmissiveSourceBuffer),
            0,
            _ddgiEmissiveSourceBufferSize);
        if (_ddgiEmissiveSurfaceBuffer.IsValid)
        {
            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.SimpleDdgiEmissiveSurfaceBuffer,
                _bufferManager.GetBuffer(_ddgiEmissiveSurfaceBuffer),
                0,
                _ddgiEmissiveSurfaceBufferSize);
        }
    }

    public DdgiEmissiveTransportSnapshot PrepareFrame(
        in DdgiEmissiveFrameRequest request,
        StagingRing stagingRing,
        CommandBuffer commandBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request.Scene);
        GlobalIlluminationSettings gi = request.Settings ??
                                        throw new ArgumentNullException(nameof(request.Settings));
        ArgumentNullException.ThrowIfNull(stagingRing);
        bool triangleSampling =
            gi.EffectiveGiEmissiveMeshSampling;
        int triangleBudget = triangleSampling
            ? Math.Clamp(
                gi.DdgiEmissiveTriangleBudget,
                1,
                MaxDdgiEmissiveMeshSourceCount)
            : MaxDdgiEmissiveMeshSourceCount;

        if (!request.RayUpdateActive ||
            !_ddgiEmissiveSourceBuffer.IsValid ||
            !_ddgiEmissiveSurfaceBuffer.IsValid)
        {
            _ddgiEmissiveSourceCount = 0;
            _ddgiEmissiveTriangleTableStats = default;
            _ddgiEmissiveSkippedSkinnedObjectCount = 0;
            _ddgiEmissiveSkippedSkinnedImportance = 0.0;
            _ddgiEmissiveEnergyDiagnostics = default;
            ResolveSimpleDdgiRefinementEmissiveDemands(gi);
            Snapshot = CaptureSnapshot(
                active: false,
                triangleSampling,
                triangleSampling ? triangleBudget : 0);
            return Snapshot;
        }

        var cacheKey = new DdgiEmissiveTableCacheKey(
            request.Scene.Id,
            request.SceneContentRevision,
            _materialManager.MaterialDataRevision,
            triangleSampling,
            triangleBudget);
        bool cacheHit = _ddgiEmissiveTableCache.TryGet(
            cacheKey,
            out DdgiEmissiveTableBuildResult buildResult);
        if (!cacheHit)
        {
            int rebuiltCount = BuildDdgiEmissiveSources(
                request.Scene,
                gi,
                out ulong rebuiltSignature);
            buildResult = new DdgiEmissiveTableBuildResult(
                rebuiltCount,
                rebuiltSignature,
                _ddgiEmissiveTriangleTableStats,
                _ddgiEmissiveSkippedSkinnedObjectCount,
                _ddgiEmissiveSkippedSkinnedImportance);
            _ddgiEmissiveTableCache.Store(
                cacheKey,
                _ddgiEmissiveSourceScratch.AsSpan(0, rebuiltCount),
                _ddgiEmissiveSurfaceScratch.AsSpan(0, rebuiltCount),
                buildResult);
        }

        _ddgiEmissiveTriangleTableStats = buildResult.TriangleStats;
        _ddgiEmissiveSkippedSkinnedObjectCount =
            buildResult.SkippedSkinnedObjectCount;
        _ddgiEmissiveSkippedSkinnedImportance =
            buildResult.SkippedSkinnedImportance;

        _ddgiVfxMacroReductionResult = triangleSampling
            ? _ddgiVfxMacroEmitterReducer.Reduce(
                request.Scene,
                request.GpuParticleDeltaSeconds,
                _ddgiVfxMacroEmitterScratch)
            : default;
        bool compositionUnchanged =
            cacheHit &&
            _ddgiEmissiveSourceBufferContentValid &&
            buildResult.PayloadSignature ==
            _lastDdgiEmissiveBasePayloadSignature &&
            _ddgiVfxMacroReductionResult.Revision ==
            _lastDdgiVfxMacroRevision;
        if (compositionUnchanged)
        {
            ResolveSimpleDdgiRefinementEmissiveDemands(gi);
            Snapshot = CaptureSnapshot(
                active: true,
                triangleSampling,
                triangleSampling ? triangleBudget : 0);
            return Snapshot;
        }

        // A cache hit may leave scratch storage containing the previous
        // heterogeneous composition. Restore the immutable mesh prefix.
        if (cacheHit)
        {
            _ddgiEmissiveTableCache.CopyPayloadTo(
                _ddgiEmissiveSourceScratch);
            _ddgiEmissiveTableCache.CopySurfacePayloadTo(
                _ddgiEmissiveSurfaceScratch);
        }

        int meshSourceCount = buildResult.Count;
        int macroSourceCount = triangleSampling
            ? _ddgiVfxMacroReductionResult.SourceCount
            : 0;
        int count = checked(meshSourceCount + macroSourceCount);
        if (count > MaxDdgiEmissiveSourceCount)
        {
            throw new InvalidOperationException(
                "DDGI heterogeneous emissive source capacity was exceeded.");
        }

        double meshImportanceScale =
            buildResult.TriangleStats.SelectedImportance > 0.0
                ? buildResult.TriangleStats.SelectedImportance
                : 1.0;
        for (int index = 0; index < meshSourceCount; index++)
        {
            _ddgiEmissiveCombinedImportanceScratch[index] = Math.Max(
                _ddgiEmissiveSourceScratch[index]
                    .RadianceSelectionProbability.W * meshImportanceScale,
                1e-20);
        }

        for (int macroIndex = 0;
             macroIndex < macroSourceCount;
             macroIndex++)
        {
            DdgiVfxMacroEmitter macro =
                _ddgiVfxMacroEmitterScratch[macroIndex];
            int destinationIndex = meshSourceCount + macroIndex;
            _ddgiEmissiveSourceScratch[destinationIndex] =
                DdgiVfxMacroEmitterReducer.PackSource(macro);
            _ddgiEmissiveSurfaceScratch[destinationIndex] = default;
            _ddgiEmissiveCombinedImportanceScratch[destinationIndex] =
                Math.Max(
                    DdgiVfxMacroEmitterReducer.MeasureImportance(macro),
                    1e-20);
        }

        if (triangleSampling && count > 0)
        {
            _ddgiEmissiveSourceSetBuilder.OrderAndRebuildAlias(
                _ddgiEmissiveSourceScratch.AsSpan(0, count),
                _ddgiEmissiveSurfaceScratch.AsSpan(0, count),
                _ddgiEmissiveCombinedImportanceScratch.AsSpan(0, count));
            _ddgiEmissiveSpatialHierarchy.BuildOrRefit(
                _ddgiEmissiveSourceScratch.AsSpan(0, count));
        }
        else
        {
            _ddgiEmissiveSpatialHierarchy.Clear();
        }

        UpdateDdgiEmissiveEnergyDiagnostics(
            _ddgiEmissiveSourceScratch.AsSpan(0, count),
            buildResult.TriangleStats);

        ulong signature = HashAdd(
            HashStart,
            buildResult.PayloadSignature);
        signature = HashAdd(
            signature,
            _ddgiVfxMacroReductionResult.Revision);
        signature = HashAdd(signature, count);
        for (int index = 0; index < count; index++)
        {
            signature = HashAdd(
                signature,
                _ddgiEmissiveSourceScratch[index].Vertex0Area);
            signature = HashAdd(
                signature,
                _ddgiEmissiveSourceScratch[index]
                    .Edge1AliasProbability);
            signature = HashAdd(
                signature,
                _ddgiEmissiveSourceScratch[index].Edge2AliasFlags);
            signature = HashAdd(
                signature,
                _ddgiEmissiveSourceScratch[index]
                    .RadianceSelectionProbability);
            signature = HashAdd(
                signature,
                _ddgiEmissiveSurfaceScratch[index].Uv0Vertex01);
            signature = HashAdd(
                signature,
                _ddgiEmissiveSurfaceScratch[index]
                    .Uv0Vertex2Uv1Vertex0);
            signature = HashAdd(
                signature,
                _ddgiEmissiveSurfaceScratch[index].Uv1Vertex12);
            signature = HashAdd(
                signature,
                _ddgiEmissiveSurfaceScratch[index]
                    .MaterialAndVertexAlpha);
        }

        bool signatureChanged =
            signature != _lastDdgiEmissiveSourceSignature;
        if (signatureChanged)
        {
            _ddgiEmissiveSourceRevision++;
            if (_ddgiEmissiveSourceRevision == 0)
                _ddgiEmissiveSourceRevision = 1;
        }

        _lastDdgiEmissiveSourceSignature = signature;
        _lastDdgiEmissiveBasePayloadSignature =
            buildResult.PayloadSignature;
        _lastDdgiVfxMacroRevision =
            _ddgiVfxMacroReductionResult.Revision;

        _ddgiEmissiveSourceCount = count;
        _ddgiEmissiveHierarchyNodeCount = triangleSampling
            ? _ddgiEmissiveSpatialHierarchy.NodeCount
            : 0;

        if (count > 0 &&
            (signatureChanged ||
             !_ddgiEmissiveSourceBufferContentValid))
        {
            bool uploadHierarchy = triangleSampling &&
                                   _ddgiEmissiveHierarchyNodeCount > 0;
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _ddgiEmissiveSourceBuffer,
                _ddgiEmissiveSourceScratch.AsSpan(0, count),
                barrierDescription: uploadHierarchy
                    ? null
                    : new UploadBarrierDescription(
                        PipelineStageFlags2.ComputeShaderBit,
                        AccessFlags2.ShaderStorageReadBit));
            if (uploadHierarchy)
            {
                GpuBufferUploader.UploadSpanToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _ddgiEmissiveSourceBuffer,
                    _ddgiEmissiveSpatialHierarchy.Nodes,
                    destinationOffset: checked(
                        (ulong)count * DdgiEmissiveSourceStride),
                    barrierDescription: new UploadBarrierDescription(
                        PipelineStageFlags2.ComputeShaderBit,
                        AccessFlags2.ShaderStorageReadBit));
            }

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _ddgiEmissiveSurfaceBuffer,
                _ddgiEmissiveSurfaceScratch.AsSpan(0, count),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _ddgiEmissiveSourceBufferContentValid = true;
            _ddgiEmissiveSourceUploadCount++;
        }

        ResolveSimpleDdgiRefinementEmissiveDemands(gi);
        Snapshot = CaptureSnapshot(
            active: true,
            triangleSampling,
            triangleSampling ? triangleBudget : 0);
        return Snapshot;
    }

    public void ResetSceneTracking()
    {
        _ddgiEmissiveTableCache.Clear();
        _ddgiEmissiveSourceBufferContentValid = false;
        _ddgiEmissiveEnergyDiagnostics = default;
        _hasDdgiEmissiveEnergyDiagnostics = false;
        _lastDdgiEmissiveEnergyWarning = string.Empty;
        _simpleDdgiRefinementEmissiveDemandScratch.Clear();
        _simpleDdgiRefinementEmissiveDemandDiagnostics = default;
        _hasSimpleDdgiRefinementEmissiveDemandSignature = false;
        _simpleDdgiRefinementEmissiveDemandSignature = 0UL;
        Snapshot = Snapshot with
        {
            Content = Snapshot.Content with
            {
                BufferContentValid = false
            },
            RefinementDemands =
            _simpleDdgiRefinementEmissiveDemandScratch,
            RefinementDiagnostics = default,
            RefinementSignature = 0UL
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ResetSceneTracking();
        DestroyBuffers();
        GC.SuppressFinalize(this);
    }

    private BufferHandle CreateDdgiEmissiveSourceBuffer()
    {
        _ddgiEmissiveTableCache.Clear();
        _ddgiEmissiveSpatialHierarchy.Clear();
        _ddgiEmissiveHierarchyNodeCount = 0;
        _ddgiEmissiveSourceBufferContentValid = false;
        int maximumHierarchyNodeCount =
            DdgiEmissiveSpatialHierarchy.GetNodeCapacity(
                MaxDdgiEmissiveSourceCount);
        _ddgiEmissiveSourceBufferSize = checked(
            (ulong)(MaxDdgiEmissiveSourceCount +
                    maximumHierarchyNodeCount) *
            DdgiEmissiveSourceStride);
        return _bufferManager.CreateDeviceBuffer(
            _ddgiEmissiveSourceBufferSize,
            BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.TransferDstBit,
            requireDeviceAddress: false,
            MemoryBudgetCategory.GlobalIllumination,
            "DDGI Emissive Source Buffer");
    }

    private BufferHandle CreateDdgiEmissiveSurfaceBuffer()
    {
        _ddgiEmissiveSurfaceBufferSize = checked(
            (ulong)MaxDdgiEmissiveSourceCount *
            DdgiEmissiveSurfaceStride);
        return _bufferManager.CreateDeviceBuffer(
            _ddgiEmissiveSurfaceBufferSize,
            BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.TransferDstBit,
            requireDeviceAddress: false,
            MemoryBudgetCategory.GlobalIllumination,
            "DDGI Emissive Surface Sidecar Buffer");
    }

    private void DestroyBuffers()
    {
        if (_ddgiEmissiveSourceBuffer.IsValid)
        {
            _bufferManager.DestroyBuffer(_ddgiEmissiveSourceBuffer);
            _ddgiEmissiveSourceBuffer = BufferHandle.Invalid;
        }

        if (_ddgiEmissiveSurfaceBuffer.IsValid)
        {
            _bufferManager.DestroyBuffer(_ddgiEmissiveSurfaceBuffer);
            _ddgiEmissiveSurfaceBuffer = BufferHandle.Invalid;
        }

        _ddgiEmissiveSourceBufferSize = 0UL;
        _ddgiEmissiveSurfaceBufferSize = 0UL;
    }

    private DdgiEmissiveTransportSnapshot CaptureSnapshot(
        bool active,
        bool triangleSampling,
        int triangleBudget) =>
        new(
            new DdgiEmissiveBufferSnapshot(
                _ddgiEmissiveSourceBuffer,
                _ddgiEmissiveSurfaceBuffer,
                _ddgiEmissiveSourceBufferSize,
                _ddgiEmissiveSurfaceBufferSize),
            new DdgiEmissiveContentSnapshot(
                active,
                triangleSampling,
                triangleBudget,
                _ddgiEmissiveSourceBufferContentValid,
                _ddgiEmissiveSourceCount,
                _ddgiEmissiveHierarchyNodeCount,
                _ddgiEmissiveSourceRevision,
                _lastDdgiEmissiveSourceSignature,
                _lastDdgiEmissiveBasePayloadSignature,
                _ddgiEmissiveSourceUploadCount),
            new DdgiEmissiveDiagnosticSnapshot(
                _ddgiEmissiveTriangleTableStats,
                _ddgiEmissiveSkippedSkinnedObjectCount,
                _ddgiEmissiveSkippedSkinnedImportance,
                _ddgiEmissiveExcludedCandidateCount,
                _ddgiEmissiveExcludedImportance,
                _ddgiEmissiveRuntimeRecordScanCount,
                active ? _ddgiEmissiveEnergyDiagnostics : default,
                _ddgiEmissiveEnergyWarningCount,
                _lastDdgiEmissiveEnergyWarning,
                _ddgiEmissiveTableCache.Diagnostics,
                _ddgiEmissiveSpatialHierarchy.Diagnostics,
                _ddgiVfxMacroReductionResult),
            _simpleDdgiRefinementEmissiveDemandScratch,
            _simpleDdgiRefinementEmissiveDemandDiagnostics,
            _simpleDdgiRefinementEmissiveDemandSignature,
            new ReadOnlyMemory<GPUDdgiEmissiveSource>(
                _ddgiEmissiveSourceScratch,
                0,
                active ? _ddgiEmissiveSourceCount : 0));

    private void UpdateDdgiEmissiveEnergyDiagnostics(
        ReadOnlySpan<GPUDdgiEmissiveSource> sources,
        DdgiEmissiveTriangleTableStats meshStats)
    {
        DdgiEmissiveEnergyDiagnostics next =
            DdgiEmissiveEnergyDiagnostics.Calculate(sources, meshStats);
        if (_hasDdgiEmissiveEnergyDiagnostics)
        {
            DdgiEmissiveEnergyChangeWarning warning =
                DdgiEmissiveEnergyChangeEvaluator.Evaluate(
                    _ddgiEmissiveEnergyDiagnostics,
                    next);
            if (warning.HasWarning)
            {
                _ddgiEmissiveEnergyWarningCount++;
                _lastDdgiEmissiveEnergyWarning = warning.Message;
            }
        }

        _ddgiEmissiveEnergyDiagnostics = next;
        _hasDdgiEmissiveEnergyDiagnostics = sources.Length > 0;
    }

    private IReadOnlyList<SimpleDdgiRefinementDemand>
        ResolveSimpleDdgiRefinementEmissiveDemands(
            GlobalIlluminationSettings gi)
    {
        bool enabled = gi.SimpleDdgiRefinementBricksEnabled &&
                       gi.SimpleDdgiTransportV2Enabled &&
                       gi.SimpleDdgiTransportTailCertificationEnabled &&
                       gi.SimpleDdgiRefinementMaximumBricks > 0;
        if (!enabled || _ddgiEmissiveSourceCount <= 0)
        {
            _simpleDdgiRefinementEmissiveDemandScratch.Clear();
            _simpleDdgiRefinementEmissiveDemandDiagnostics = default;
            _hasSimpleDdgiRefinementEmissiveDemandSignature = false;
            return _simpleDdgiRefinementEmissiveDemandScratch;
        }

        ulong signature = HashStart;
        signature = HashAdd(signature, _lastDdgiEmissiveSourceSignature);
        signature = HashAdd(signature, _ddgiEmissiveSourceCount);
        signature = HashAdd(
            signature,
            gi.SimpleDdgiRefinementMinimumEmissiveLuminanceNits);
        signature = HashAdd(
            signature,
            gi.SimpleDdgiRefinementMaximumEmitterAreaSquareMeters);
        if (_hasSimpleDdgiRefinementEmissiveDemandSignature &&
            signature == _simpleDdgiRefinementEmissiveDemandSignature)
        {
            return _simpleDdgiRefinementEmissiveDemandScratch;
        }

        _simpleDdgiRefinementEmissiveDemandDiagnostics =
            SimpleDdgiRefinementEmissiveDemandBuilder.Build(
                _ddgiEmissiveSourceScratch.AsSpan(
                    0,
                    _ddgiEmissiveSourceCount),
                new SimpleDdgiRefinementEmissiveDemandConfiguration(
                    gi.SimpleDdgiRefinementMinimumEmissiveLuminanceNits,
                    gi.SimpleDdgiRefinementMaximumEmitterAreaSquareMeters,
                    MaximumDemandCount: 32),
                _simpleDdgiRefinementEmissiveDemandScratch);
        _simpleDdgiRefinementEmissiveDemandSignature = signature;
        _hasSimpleDdgiRefinementEmissiveDemandSignature = true;
        return _simpleDdgiRefinementEmissiveDemandScratch;
    }

    private int BuildDdgiEmissiveSources(
        Scene scene,
        GlobalIlluminationSettings gi,
        out ulong signature)
    {
        if (gi.EffectiveGiEmissiveMeshSampling)
            return BuildDdgiEmissiveTriangleSources(scene, gi, out signature);

        // Explicit rollback: legacy bounds proxies and triangle sampling are
        // mutually exclusive, so disabling the feature cannot double energy.
        int count = 0;
        _ddgiEmissiveTriangleTableStats = default;
        Array.Clear(_ddgiEmissiveSurfaceScratch, 0, MaxDdgiEmissiveMeshSourceCount);
        _ddgiEmissiveSpatialHierarchy.Clear();
        _ddgiEmissiveHierarchyNodeCount = 0;
        _ddgiEmissiveSkippedSkinnedObjectCount = 0;
        _ddgiEmissiveSkippedSkinnedImportance = 0.0;
        signature = HashStart;
        foreach (RenderObject renderObject in scene.RenderObjects)
        {
            if (!TryCreateDdgiEmissiveSource(renderObject, out GPUDdgiEmissiveSource source, out float importance,
                    out ulong sourceSignature))
                continue;

            InsertDdgiEmissiveSource(source, importance, ref count);
            signature = HashAdd(signature, sourceSignature);
        }

        SortDdgiEmissiveSourcesByImportance(count);
        signature = HashAdd(signature, count);
        for (int i = 0; i < count; i++)
        {
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Vertex0Area);
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge1AliasProbability);
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge2AliasFlags);
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].RadianceSelectionProbability);
        }

        return count;
    }

    private int BuildDdgiEmissiveTriangleSources(
        Scene scene,
        GlobalIlluminationSettings gi,
        out ulong signature)
    {
        _ddgiEmissiveSkippedSkinnedObjectCount = 0;
        _ddgiEmissiveSkippedSkinnedImportance = 0.0;
        _ddgiEmissiveExcludedCandidateCount = 0;
        _ddgiEmissiveExcludedImportance = 0.0;
        _ddgiEmissiveRuntimeRecordScanCount = 0;
        int budget = Math.Clamp(
            gi.DdgiEmissiveTriangleBudget,
            1,
            MaxDdgiEmissiveMeshSourceCount);
        DdgiEmissiveTriangleTableStats retainedStats = DdgiEmissiveTriangleTable.Build(
            EnumerateDdgiEmissiveTriangles(scene),
            _ddgiEmissiveSourceScratch.AsSpan(0, budget),
            _ddgiEmissiveSurfaceScratch.AsSpan(0, budget));
        _ddgiEmissiveTriangleTableStats = DdgiEmissiveTriangleTable.IncludeExcluded(
            retainedStats,
            _ddgiEmissiveExcludedCandidateCount,
            _ddgiEmissiveExcludedImportance);

        int count = _ddgiEmissiveTriangleTableStats.SelectedCount;
        for (int index = 0; index < count; index++)
        {
            _ddgiEmissiveCombinedImportanceScratch[index] = Math.Max(
                _ddgiEmissiveSourceScratch[index].RadianceSelectionProbability.W,
                1e-20f);
        }

        _ddgiEmissiveSourceSetBuilder.OrderAndRebuildAlias(
            _ddgiEmissiveSourceScratch.AsSpan(0, count),
            _ddgiEmissiveSurfaceScratch.AsSpan(0, count),
            _ddgiEmissiveCombinedImportanceScratch.AsSpan(0, count));
        _ddgiEmissiveSpatialHierarchy.BuildOrRefit(
            _ddgiEmissiveSourceScratch.AsSpan(0, count));
        _ddgiEmissiveHierarchyNodeCount = _ddgiEmissiveSpatialHierarchy.NodeCount;
        signature = HashAdd(HashStart, 1u);
        signature = HashAdd(signature, budget);
        signature = HashAdd(signature, _ddgiEmissiveTriangleTableStats.CandidateCount);
        signature = HashAdd(signature, _ddgiEmissiveTriangleTableStats.SkippedEnergyFraction);
        signature = HashAdd(signature, _ddgiEmissiveSkippedSkinnedObjectCount);
        signature = HashAdd(signature, (float)_ddgiEmissiveSkippedSkinnedImportance);
        signature = HashAdd(signature, _ddgiEmissiveHierarchyNodeCount);
        for (int i = 0; i < count; i++)
        {
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Vertex0Area);
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge1AliasProbability);
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge2AliasFlags);
            signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].RadianceSelectionProbability);
        }

        return count;
    }

    private IEnumerable<DdgiEmissiveTriangleCandidate> EnumerateDdgiEmissiveTriangles(Scene scene)
    {
        for (int objectIndex = 0; objectIndex < scene.RenderObjects.Count; objectIndex++)
        {
            RenderObject renderObject = scene.RenderObjects[objectIndex];
            if (!renderObject.Enabled ||
                !renderObject.Visible ||
                renderObject.Mesh is not MeshHandle meshHandle ||
                !meshHandle.IsValid ||
                !TryResolveDdgiEmissiveMaterial(
                    renderObject.Material,
                    renderObject.Name,
                    out DdgiResolvedEmissiveMaterial material,
                    out DdgiEmissiveSourceFlags sourceFlags))
            {
                continue;
            }

            if (!TryGetDdgiEmissiveGeometry(meshHandle, out MeshTransportGeometry geometry))
            {
                AddDdgiEmissiveExclusion(1, 1e-12);
                continue;
            }

            bool skinned = geometry.IsSkinned || renderObject is SkinnedRenderObject;
            if (skinned)
            {
                _ddgiEmissiveSkippedSkinnedObjectCount++;
                double skippedImportance = EstimateExcludedDdgiEmissiveImportance(
                    geometry,
                    renderObject.WorldMatrix,
                    material,
                    sourceFlags);
                _ddgiEmissiveSkippedSkinnedImportance = SaturatingImportanceAdd(
                    _ddgiEmissiveSkippedSkinnedImportance,
                    skippedImportance);
                AddDdgiEmissiveExclusion(
                    EstimateExcludedDdgiEmissiveCandidateCount(geometry),
                    skippedImportance);
                continue;
            }

            if (!renderObject.IsStatic)
                sourceFlags |= DdgiEmissiveSourceFlags.DynamicTransform;
            ulong stableKey = HashAdd(HashAdd(HashStart, objectIndex), renderObject.WorldMatrix);
            foreach (DdgiEmissiveTriangleCandidate candidate in EnumerateDdgiEmissiveInstanceTriangles(
                         geometry,
                         renderObject.WorldMatrix,
                         material,
                         sourceFlags,
                         stableKey))
            {
                yield return candidate;
            }
        }

        for (int batchIndex = 0; batchIndex < scene.StaticInstanceBatches.Count; batchIndex++)
        {
            StaticInstanceBatch batch = scene.StaticInstanceBatches[batchIndex];
            if (!batch.Visible ||
                batch.Mesh is not MeshHandle meshHandle ||
                !meshHandle.IsValid ||
                !TryResolveDdgiEmissiveMaterial(
                    batch.Material,
                    batch.Name,
                    out DdgiResolvedEmissiveMaterial material,
                    out DdgiEmissiveSourceFlags sourceFlags))
            {
                continue;
            }

            if (!TryGetDdgiEmissiveGeometry(meshHandle, out MeshTransportGeometry geometry))
            {
                int invalidInstanceCount = Math.Max(batch.WorldMatrices.Count, 1);
                AddDdgiEmissiveExclusion(invalidInstanceCount, invalidInstanceCount * 1e-12);
                continue;
            }

            for (int instanceIndex = 0; instanceIndex < batch.WorldMatrices.Count; instanceIndex++)
            {
                Matrix4x4 worldMatrix = batch.WorldMatrices[instanceIndex];
                if (geometry.IsSkinned)
                {
                    _ddgiEmissiveSkippedSkinnedObjectCount++;
                    double skippedImportance = EstimateExcludedDdgiEmissiveImportance(
                        geometry,
                        worldMatrix,
                        material,
                        sourceFlags);
                    _ddgiEmissiveSkippedSkinnedImportance = SaturatingImportanceAdd(
                        _ddgiEmissiveSkippedSkinnedImportance,
                        skippedImportance);
                    AddDdgiEmissiveExclusion(
                        EstimateExcludedDdgiEmissiveCandidateCount(geometry),
                        skippedImportance);
                    continue;
                }

                ulong stableKey = HashAdd(
                    HashAdd(HashAdd(HashStart, batchIndex), instanceIndex),
                    worldMatrix);
                foreach (DdgiEmissiveTriangleCandidate candidate in EnumerateDdgiEmissiveInstanceTriangles(
                             geometry,
                             worldMatrix,
                             material,
                             sourceFlags,
                             stableKey))
                {
                    yield return candidate;
                }
            }
        }
    }

    private IEnumerable<DdgiEmissiveTriangleCandidate> EnumerateDdgiEmissiveInstanceTriangles(
        MeshTransportGeometry geometry,
        Matrix4x4 worldMatrix,
        DdgiResolvedEmissiveMaterial material,
        DdgiEmissiveSourceFlags sourceFlags,
        ulong stableKey)
    {
        ReadOnlyMemory<GPUVertexPositionStream> vertices = geometry.VertexPositions;
        ReadOnlyMemory<GPUVertexUvColorStream> vertexUvColors = geometry.VertexUvColors;
        ReadOnlyMemory<uint> indices = geometry.Indices;
        GiPrimitiveTransportProfile? profile = geometry.PrimitiveTransportProfile;
        bool compatible = DdgiCookedEmissiveTransport.TryValidateCompatibility(
            profile,
            material.Definition,
            material.TransportProfile,
            out _);
        if (!compatible &&
            !material.DynamicEmissiveTexture &&
            !CanUseUniformAnalyticEmission(material.Definition))
        {
            AddDdgiEmissiveExclusion(
                EstimateExcludedDdgiEmissiveCandidateCount(geometry),
                EstimateExcludedDdgiEmissiveImportance(
                    geometry,
                    worldMatrix,
                    material,
                    sourceFlags));
            yield break;
        }

        if (compatible &&
            !material.DynamicEmissiveTexture &&
            profile is not null &&
            (profile.EmissiveCandidateTriangleCount > 0 ||
             !CanUseUniformAnalyticEmission(material.Definition)))
        {
            int cookOmittedCount = Math.Max(
                profile.EmissiveCandidateTriangleCount - profile.EmissiveTriangles.Length,
                0);
            AddDdgiEmissiveExclusion(
                cookOmittedCount,
                DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                    profile.EmissiveOmittedCookedImportance,
                    material.Definition,
                    worldMatrix,
                    material.DoubleSided));

            int remainingScanCapacity = Math.Max(
                MaximumDdgiEmissiveRuntimeRecordScans -
                _ddgiEmissiveRuntimeRecordScanCount,
                0);
            int scanCount = Math.Min(profile.EmissiveTriangles.Length, remainingScanCapacity);
            double scannedNeutralImportance = 0.0;
            for (int recordIndex = 0; recordIndex < scanCount; recordIndex++)
            {
                GiPrimitiveEmissiveTriangleRecord record = profile.EmissiveTriangles[recordIndex];
                scannedNeutralImportance += record.CookedImportance;
                _ddgiEmissiveRuntimeRecordScanCount++;
                if (!TryBuildDdgiEmissiveTriangleCandidate(
                        vertices,
                        vertexUvColors,
                        indices,
                        record.TriangleIndex,
                        worldMatrix,
                        DdgiCookedEmissiveTransport.EvaluateCoveredRadiance(
                            record,
                            material.Definition),
                        sourceFlags,
                        HashAdd(stableKey, record.TriangleIndex),
                        material.MaterialIndex,
                        out DdgiEmissiveTriangleCandidate candidate))
                {
                    AddDdgiEmissiveExclusion(
                        1,
                        DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                            record.CookedImportance,
                            material.Definition,
                            worldMatrix,
                            material.DoubleSided));
                    continue;
                }

                yield return candidate;
            }

            int runtimeOmittedCount = profile.EmissiveTriangles.Length - scanCount;
            if (runtimeOmittedCount > 0)
            {
                double runtimeOmittedNeutralImportance = Math.Max(
                    profile.EmissiveRetainedCookedImportance - scannedNeutralImportance,
                    0.0);
                AddDdgiEmissiveExclusion(
                    runtimeOmittedCount,
                    DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                        runtimeOmittedNeutralImportance,
                        material.Definition,
                        worldMatrix,
                        material.DoubleSided));
            }

            yield break;
        }

        Vector3 radiance = material.DynamicEmissiveTexture
            ? Vector3.Max(
                material.AverageCoveredRadiance,
                EmissivePhotometry.EvaluateSceneLinearRadiance(
                    material.Definition,
                    Vector3.One) *
                1e-4f)
            : EvaluateUniformCoveredRadiance(material.Definition);
        int analyticScanCount = Math.Min(
            geometry.TriangleCount,
            Math.Max(
                MaximumDdgiEmissiveRuntimeRecordScans -
                _ddgiEmissiveRuntimeRecordScanCount,
                0));
        for (int triangleIndex = 0; triangleIndex < analyticScanCount; triangleIndex++)
        {
            _ddgiEmissiveRuntimeRecordScanCount++;
            if (TryBuildDdgiEmissiveTriangleCandidate(
                    vertices,
                    vertexUvColors,
                    indices,
                    triangleIndex,
                    worldMatrix,
                    radiance,
                    sourceFlags,
                    HashAdd(stableKey, triangleIndex),
                    material.MaterialIndex,
                    out DdgiEmissiveTriangleCandidate candidate))
            {
                yield return candidate;
            }
        }

        int analyticOmittedCount = geometry.TriangleCount - analyticScanCount;
        if (analyticOmittedCount > 0)
        {
            double omittedArea = geometry.TriangleCount > 0
                ? geometry.LocalSurfaceArea * analyticOmittedCount / geometry.TriangleCount
                : 0.0;
            AddDdgiEmissiveExclusion(
                analyticOmittedCount,
                BoundUniformWorldImportance(
                    omittedArea,
                    material.Definition,
                    worldMatrix,
                    material.DoubleSided));
        }
    }

    private static bool TryBuildDdgiEmissiveTriangleCandidate(
        ReadOnlyMemory<GPUVertexPositionStream> vertices,
        ReadOnlyMemory<GPUVertexUvColorStream> vertexUvColors,
        ReadOnlyMemory<uint> indices,
        int triangleIndex,
        Matrix4x4 worldMatrix,
        Vector3 radiance,
        DdgiEmissiveSourceFlags sourceFlags,
        ulong stableKey,
        int materialIndex,
        out DdgiEmissiveTriangleCandidate candidate)
    {
        candidate = default;
        int indexBase = triangleIndex * 3;
        if (triangleIndex < 0 || indexBase > indices.Length - 3)
            return false;
        uint i0 = indices.Span[indexBase];
        uint i1 = indices.Span[indexBase + 1];
        uint i2 = indices.Span[indexBase + 2];
        if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length ||
            i0 >= vertexUvColors.Length || i1 >= vertexUvColors.Length || i2 >= vertexUvColors.Length ||
            materialIndex < 0)
            return false;

        Vector4 p0 = vertices.Span[(int)i0].Position;
        Vector4 p1 = vertices.Span[(int)i1].Position;
        Vector4 p2 = vertices.Span[(int)i2].Position;
        Vector3 v0 = new Vector3(p0.X, p0.Y, p0.Z) * worldMatrix;
        Vector3 v1 = new Vector3(p1.X, p1.Y, p1.Z) * worldMatrix;
        Vector3 v2 = new Vector3(p2.X, p2.Y, p2.Z) * worldMatrix;
        GPUVertexUvColorStream uv0 = vertexUvColors.Span[(int)i0];
        GPUVertexUvColorStream uv1 = vertexUvColors.Span[(int)i1];
        GPUVertexUvColorStream uv2 = vertexUvColors.Span[(int)i2];
        var surface = new GPUDdgiEmissiveSurface
        {
            Uv0Vertex01 = new Vector4(
                uv0.TexCoord.X,
                uv0.TexCoord.Y,
                uv1.TexCoord.X,
                uv1.TexCoord.Y),
            Uv0Vertex2Uv1Vertex0 = new Vector4(
                uv2.TexCoord.X,
                uv2.TexCoord.Y,
                uv0.TexCoord2.X,
                uv0.TexCoord2.Y),
            Uv1Vertex12 = new Vector4(
                uv1.TexCoord2.X,
                uv1.TexCoord2.Y,
                uv2.TexCoord2.X,
                uv2.TexCoord2.Y),
            MaterialAndVertexAlpha = new Vector4(
                BitConverter.UInt32BitsToSingle(checked((uint)materialIndex)),
                uv0.Color.W,
                uv1.Color.W,
                uv2.Color.W)
        };
        candidate = new DdgiEmissiveTriangleCandidate(
            v0,
            v1,
            v2,
            radiance,
            sourceFlags | DdgiEmissiveSourceFlags.Triangle,
            stableKey,
            surface);
        return true;
    }

    private bool TryResolveDdgiEmissiveMaterial(
        object? materialReference,
        string ownerName,
        out DdgiResolvedEmissiveMaterial resolved,
        out DdgiEmissiveSourceFlags flags)
    {
        resolved = default;
        flags = DdgiEmissiveSourceFlags.None;
        try
        {
            MaterialHandle materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                materialReference,
                _materialManager.DefaultMaterialHandle,
                ownerName);
            MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
            if (metadata.RenderMode == MaterialRenderMode.Blend ||
                metadata.IsGeometryDecal ||
                !metadata.EmitsIntoGi)
            {
                return false;
            }

            GPUMaterialData gpuMaterial = _materialManager.GetMaterialData(materialHandle);
            GiMaterialTransportFlags transportFlags =
                (GiMaterialTransportFlags)gpuMaterial.TransportFlags;
            if ((transportFlags & GiMaterialTransportFlags.EmitsIntoGi) == 0)
            {
                return false;
            }

            MaterialDefinition definition = _materialManager.GetMaterialDefinition(materialHandle);
            GiMaterialTransportProfile transportProfile =
                _materialManager.GetMaterialTransportProfile(materialHandle);
            float alphaCoverage = metadata.RenderMode == MaterialRenderMode.Mask
                ? Math.Clamp(gpuMaterial.DdgiMaterialPolicy.Z, 0.0f, 1.0f)
                : 1.0f;
            Vector3 averageCoveredRadiance = new(
                Math.Max(gpuMaterial.DdgiAverageEmissive.X, 0.0f),
                Math.Max(gpuMaterial.DdgiAverageEmissive.Y, 0.0f),
                Math.Max(gpuMaterial.DdgiAverageEmissive.Z, 0.0f));
            averageCoveredRadiance *= alphaCoverage;
            Vector3 factorRadiance = EmissivePhotometry.EvaluateSceneLinearRadiance(
                definition,
                Vector3.One);
            float luminance =
                0.2126f * factorRadiance.X +
                0.7152f * factorRadiance.Y +
                0.0722f * factorRadiance.Z;
            if (!float.IsFinite(luminance) || luminance <= 0.000001f)
                return false;

            if (metadata.DoubleSided)
                flags |= DdgiEmissiveSourceFlags.DoubleSided;
            if (metadata.RenderMode == MaterialRenderMode.Mask)
                flags |= DdgiEmissiveSourceFlags.AlphaCoverageApproximation;
            bool dynamicEmissiveTexture = definition.Emissive.IsBound;
            if (dynamicEmissiveTexture)
                flags |= DdgiEmissiveSourceFlags.DynamicEmissiveTexture;
            resolved = new DdgiResolvedEmissiveMaterial(
                definition,
                transportProfile,
                averageCoveredRadiance,
                metadata.DoubleSided,
                materialHandle.Index,
                dynamicEmissiveTexture);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryGetDdgiEmissiveGeometry(
        MeshHandle meshHandle,
        out MeshTransportGeometry geometry)
    {
        try
        {
            geometry = _meshManager.GetTransportGeometry(meshHandle);
            return geometry.IsValid;
        }
        catch (InvalidOperationException)
        {
            geometry = default;
            return false;
        }
    }

    private static int EstimateExcludedDdgiEmissiveCandidateCount(
        MeshTransportGeometry geometry) =>
        geometry.PrimitiveTransportProfile?.EmissiveCandidateTriangleCount ??
        geometry.TriangleCount;

    private static double EstimateExcludedDdgiEmissiveImportance(
        MeshTransportGeometry geometry,
        Matrix4x4 worldMatrix,
        DdgiResolvedEmissiveMaterial material,
        DdgiEmissiveSourceFlags flags)
    {
        GiPrimitiveTransportProfile? profile = geometry.PrimitiveTransportProfile;
        if (DdgiCookedEmissiveTransport.TryValidateCompatibility(
                profile,
                material.Definition,
                material.TransportProfile,
                out _) &&
            profile is not null)
        {
            return DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                profile.EmissiveTotalCookedImportance,
                material.Definition,
                worldMatrix,
                material.DoubleSided);
        }

        // An incompatible or absent texture profile cannot safely reuse a
        // texture-wide mean. Unit texture luminance over the full local
        // area is a conservative, observable skipped-energy bound.
        return BoundUniformWorldImportance(
            geometry.LocalSurfaceArea,
            material.Definition,
            worldMatrix,
            (flags & DdgiEmissiveSourceFlags.DoubleSided) != 0);
    }

    private static bool CanUseUniformAnalyticEmission(MaterialDefinition material) =>
        !material.Emissive.IsBound &&
        (!material.BaseColor.IsBound || material.AlphaMode == MaterialAlphaMode.Opaque);

    private static Vector3 EvaluateUniformCoveredRadiance(MaterialDefinition material)
    {
        float coverage = material.AlphaMode switch
        {
            MaterialAlphaMode.Mask =>
                material.BaseColorFactor.W >= material.AlphaCutoff ? 1.0f : 0.0f,
            MaterialAlphaMode.Opaque => 1.0f,
            _ => 0.0f
        };
        return EmissivePhotometry.EvaluateSceneLinearRadiance(material, Vector3.One) *
               coverage;
    }

    private static double BoundUniformWorldImportance(
        double localArea,
        MaterialDefinition material,
        Matrix4x4 worldMatrix,
        bool doubleSided) =>
        DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
            localArea,
            material,
            worldMatrix,
            doubleSided);

    private void AddDdgiEmissiveExclusion(int candidateCount, double importance)
    {
        if (candidateCount > 0)
        {
            _ddgiEmissiveExcludedCandidateCount = (int)Math.Min(
                (long)_ddgiEmissiveExcludedCandidateCount + candidateCount,
                int.MaxValue);
        }

        _ddgiEmissiveExcludedImportance = SaturatingImportanceAdd(
            _ddgiEmissiveExcludedImportance,
            importance);
    }

    private static double SaturatingImportanceAdd(double left, double right)
    {
        if (left == double.MaxValue || right == double.MaxValue)
            return double.MaxValue;
        double result = left + right;
        return double.IsFinite(result) ? Math.Max(result, 0.0) : double.MaxValue;
    }

    private readonly record struct DdgiResolvedEmissiveMaterial(
        MaterialDefinition Definition,
        GiMaterialTransportProfile TransportProfile,
        Vector3 AverageCoveredRadiance,
        bool DoubleSided,
        int MaterialIndex,
        bool DynamicEmissiveTexture);

    private bool TryCreateDdgiEmissiveSource(
        RenderObject renderObject,
        out GPUDdgiEmissiveSource source,
        out float importance,
        out ulong sourceSignature)
    {
        source = default;
        importance = 0.0f;
        sourceSignature = 0UL;

        if (!renderObject.Enabled ||
            !renderObject.Visible ||
            renderObject.Mesh is not MeshHandle meshHandle ||
            !meshHandle.IsValid)
        {
            return false;
        }

        MaterialHandle materialHandle;
        try
        {
            MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
            if (meshInfo.VertexCount == 0 || meshInfo.IndexCount < 3)
                return false;
            materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                renderObject.Material,
                _materialManager.DefaultMaterialHandle,
                renderObject.Name);
            GPUMaterialData material = _materialManager.GetMaterialData(materialHandle);
            MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
            if (metadata.RenderMode == MaterialRenderMode.Blend ||
                metadata.IsGeometryDecal ||
                !metadata.EmitsIntoGi)
                return false;
            GiMaterialTransportFlags transportFlags =
                (GiMaterialTransportFlags)material.TransportFlags;
            if ((transportFlags & GiMaterialTransportFlags.EmissionProfileValid) == 0 ||
                (transportFlags & GiMaterialTransportFlags.EmitsIntoGi) == 0)
            {
                return false;
            }

            Vector3 radiance = new(
                MathF.Max(material.DdgiAverageEmissive.X, 0.0f),
                MathF.Max(material.DdgiAverageEmissive.Y, 0.0f),
                MathF.Max(material.DdgiAverageEmissive.Z, 0.0f));
            importance = MathF.Max(material.DdgiAverageEmissive.W, 0.0f);
            if (importance <= 0.0001f)
                return false;

            BoundingBox localBounds = new(
                ToCoreVector(meshInfo.BoundingBoxMin),
                ToCoreVector(meshInfo.BoundingBoxMax));
            BoundingBox bounds = SceneDataBuilder.TransformBoundingBox(
                localBounds,
                renderObject.WorldMatrix);
            Vector3 center = bounds.Center;
            Vector3 size = bounds.Size;
            float objectRadius = MathF.Max(0.05f, size.Length() * 0.5f);
            float affectedRadius = MathF.Max(objectRadius, MathF.Sqrt(importance) * 4.0f);
            source = new GPUDdgiEmissiveSource
            {
                Vertex0Area = new Vector4(center.X, center.Y, center.Z, affectedRadius),
                Edge1AliasProbability = new Vector4(radiance.X, radiance.Y, radiance.Z, importance),
                Edge2AliasFlags = new Vector4(
                    bounds.Min.X,
                    bounds.Min.Y,
                    bounds.Min.Z,
                    BitConverter.UInt32BitsToSingle(
                        (uint)DdgiEmissiveSourceFlags.ProxyRollback <<
                        DdgiEmissiveTriangleTable.FlagsShift)),
                RadianceSelectionProbability = new Vector4(
                    bounds.Max.X,
                    bounds.Max.Y,
                    bounds.Max.Z,
                    0.0f)
            };

            MaterialAspectRevisions aspectRevisions =
                _materialManager.GetMaterialAspectRevisions(materialHandle);
            uint profileRevision =
                _materialManager.GetMaterialTransportProfileRevision(
                    materialHandle.Index);
            ulong emissiveSignature =
                CreateDdgiEmissiveMaterialSignature(
                    material,
                    aspectRevisions.Emission,
                    profileRevision);
            sourceSignature = HashAdd(
                HashAdd(emissiveSignature, materialHandle.Index),
                materialHandle.Generation);
            sourceSignature = HashAdd(sourceSignature, source.Vertex0Area);
            sourceSignature = HashAdd(sourceSignature, source.Edge1AliasProbability);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void InsertDdgiEmissiveSource(GPUDdgiEmissiveSource source, float importance, ref int count)
    {
        if (count < MaxDdgiEmissiveMeshSourceCount)
        {
            _ddgiEmissiveSourceScratch[count] = source;
            _ddgiEmissiveSourceImportanceScratch[count] = importance;
            count++;
            return;
        }

        int weakestIndex = 0;
        float weakestImportance = _ddgiEmissiveSourceImportanceScratch[0];
        for (int i = 1; i < MaxDdgiEmissiveMeshSourceCount; i++)
        {
            if (_ddgiEmissiveSourceImportanceScratch[i] >= weakestImportance)
                continue;

            weakestImportance = _ddgiEmissiveSourceImportanceScratch[i];
            weakestIndex = i;
        }

        if (importance <= weakestImportance)
            return;

        _ddgiEmissiveSourceScratch[weakestIndex] = source;
        _ddgiEmissiveSourceImportanceScratch[weakestIndex] = importance;
    }

    private void SortDdgiEmissiveSourcesByImportance(int count)
    {
        for (int i = 1; i < count; i++)
        {
            GPUDdgiEmissiveSource source = _ddgiEmissiveSourceScratch[i];
            float importance = _ddgiEmissiveSourceImportanceScratch[i];
            int j = i - 1;
            while (j >= 0 && _ddgiEmissiveSourceImportanceScratch[j] < importance)
            {
                _ddgiEmissiveSourceScratch[j + 1] = _ddgiEmissiveSourceScratch[j];
                _ddgiEmissiveSourceImportanceScratch[j + 1] = _ddgiEmissiveSourceImportanceScratch[j];
                j--;
            }

            _ddgiEmissiveSourceScratch[j + 1] = source;
            _ddgiEmissiveSourceImportanceScratch[j + 1] = importance;
        }
    }


    private static ulong CreateDdgiEmissiveMaterialSignature(
        GPUMaterialData materialData,
        uint emissionRevision,
        uint profileRevision)
    {
        ulong hash = HashStart;
        hash = HashAdd(hash, emissionRevision);
        hash = HashAdd(hash, profileRevision);
        hash = HashAdd(hash, materialData.Emissive);
        hash = HashAdd(hash, materialData.DdgiAverageEmissive);
        hash = HashAdd(hash, materialData.EmissiveTextureIndex);
        return hash;
    }

    private static ulong HashAdd(ulong hash, Matrix4x4 value)
    {
        hash = HashAdd(hash, value.M11);
        hash = HashAdd(hash, value.M12);
        hash = HashAdd(hash, value.M13);
        hash = HashAdd(hash, value.M14);
        hash = HashAdd(hash, value.M21);
        hash = HashAdd(hash, value.M22);
        hash = HashAdd(hash, value.M23);
        hash = HashAdd(hash, value.M24);
        hash = HashAdd(hash, value.M31);
        hash = HashAdd(hash, value.M32);
        hash = HashAdd(hash, value.M33);
        hash = HashAdd(hash, value.M34);
        hash = HashAdd(hash, value.M41);
        hash = HashAdd(hash, value.M42);
        hash = HashAdd(hash, value.M43);
        return HashAdd(hash, value.M44);
    }

    private static ulong HashAdd(ulong hash, Vector3 value)
    {
        hash = HashAdd(hash, value.X);
        hash = HashAdd(hash, value.Y);
        return HashAdd(hash, value.Z);
    }

    private static ulong HashAdd(ulong hash, Vector4 value)
    {
        hash = HashAdd(hash, value.X);
        hash = HashAdd(hash, value.Y);
        hash = HashAdd(hash, value.Z);
        return HashAdd(hash, value.W);
    }

    private static ulong HashAdd(ulong hash, int value) =>
        HashAdd(hash, unchecked((uint)value));

    private static ulong HashAdd(ulong hash, float value) =>
        HashAdd(hash, BitConverter.SingleToUInt32Bits(value));

    private static ulong HashAdd(ulong hash, uint value)
    {
        unchecked
        {
            hash ^= value & 0xFFu;
            hash *= HashPrime;
            hash ^= (value >> 8) & 0xFFu;
            hash *= HashPrime;
            hash ^= (value >> 16) & 0xFFu;
            hash *= HashPrime;
            hash ^= (value >> 24) & 0xFFu;
            return hash * HashPrime;
        }
    }

    private static ulong HashAdd(ulong hash, ulong value)
    {
        hash = HashAdd(hash, unchecked((uint)value));
        return HashAdd(hash, unchecked((uint)(value >> 32)));
    }

    private static Vector3 ToCoreVector(System.Numerics.Vector3 value) =>
        new(value.X, value.Y, value.Z);


    // Emissive selection and signature helpers are kept mechanically identical
    // to the former renderer implementation below this boundary.
}

internal readonly record struct DdgiEmissiveFrameRequest(
    Scene Scene,
    GlobalIlluminationSettings Settings,
    ulong SceneContentRevision,
    float GpuParticleDeltaSeconds,
    bool RayUpdateActive);

internal readonly record struct DdgiEmissiveBufferSnapshot(
    BufferHandle SourceBuffer,
    BufferHandle SurfaceBuffer,
    ulong SourceBufferBytes,
    ulong SurfaceBufferBytes);

internal readonly record struct DdgiEmissiveContentSnapshot(
    bool Active,
    bool TriangleSampling,
    int TriangleBudget,
    bool BufferContentValid,
    int SourceCount,
    int HierarchyNodeCount,
    uint SourceRevision,
    ulong SourceSignature,
    ulong BasePayloadSignature,
    ulong UploadCount);

internal readonly record struct DdgiEmissiveDiagnosticSnapshot(
    DdgiEmissiveTriangleTableStats TriangleStats,
    int SkippedSkinnedObjectCount,
    double SkippedSkinnedImportance,
    int ExcludedCandidateCount,
    double ExcludedImportance,
    int RuntimeRecordScanCount,
    DdgiEmissiveEnergyDiagnostics Energy,
    ulong EnergyWarningCount,
    string LastEnergyWarning,
    DdgiEmissiveTableCacheDiagnostics Cache,
    DdgiEmissiveHierarchyDiagnostics Hierarchy,
    DdgiVfxMacroReductionResult Vfx);

internal readonly record struct DdgiEmissiveTransportSnapshot(
    DdgiEmissiveBufferSnapshot Buffers,
    DdgiEmissiveContentSnapshot Content,
    DdgiEmissiveDiagnosticSnapshot Diagnostics,
    IReadOnlyList<SimpleDdgiRefinementDemand> RefinementDemands,
    SimpleDdgiRefinementEmissiveDemandDiagnostics RefinementDiagnostics,
    ulong RefinementSignature,
    ReadOnlyMemory<GPUDdgiEmissiveSource> Sources);