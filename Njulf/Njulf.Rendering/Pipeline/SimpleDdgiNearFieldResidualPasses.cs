using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

public static class SimpleDdgiNearFieldResidualGpuPassNames
{
    public const string Reset = "SimpleDdgiNearFieldResidualResetPass";
    public const string Prepare = "SimpleDdgiNearFieldResidualPreparePass";
    public const string Classify = "SimpleDdgiNearFieldResidualClassifyPass";
    public const string Trace = "SimpleDdgiNearFieldResidualTracePass";
    public const string Temporal = "SimpleDdgiNearFieldResidualTemporalPass";
    public const string Finalize = "SimpleDdgiNearFieldResidualFinalizePass";
    public const string Filter = "SimpleDdgiNearFieldResidualFilterPass";
    public const string FrequencySeparation =
        "SimpleDdgiNearFieldResidualFrequencySeparationPass";
    public const string Composite = "SimpleDdgiNearFieldResidualCompositePass";

    public const string ResetShader = "ddgi_near_field_residual_reset.comp.spv";
    public const string PrepareShader = "ddgi_near_field_residual_prepare.comp.spv";
    public const string ClassifyShader = "ddgi_near_field_residual_classify.comp.spv";
    public const string TraceShader = "ddgi_near_field_residual_trace.comp.spv";
    public const string TemporalShader = "ddgi_near_field_residual_temporal.comp.spv";
    public const string FinalizeShader = "ddgi_near_field_residual_finalize.comp.spv";
    public const string FilterShader = "ddgi_near_field_residual_filter.comp.spv";
    public const string FrequencySeparationShader =
        "ddgi_near_field_residual_frequency.comp.spv";
    public const string CompositeShader = "ddgi_near_field_residual_composite.comp.spv";

    public static string FilterIteration(int iteration) => iteration switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(iteration)),
        0 => Filter,
        _ => Filter + iteration
    };
}

internal enum SimpleDdgiNearFieldResidualPassKind : byte
{
    Reset,
    Prepare,
    Classify,
    Trace,
    Temporal,
    Finalize,
    Filter,
    FrequencySeparation,
    Composite
}

/// <summary>
/// Fixed C5 descriptor/pipeline recorder. All sets are created and populated
/// at admission/resize time; command recording performs no descriptor writes
/// and allocates no managed objects.
/// </summary>
internal sealed unsafe class SimpleDdgiNearFieldResidualGpuCommandRecorder : IDisposable
{
    private const uint ComputeLocalSize = 8u;
    private const uint ResetLocalSize = 64u;
    private const int MaximumDescriptorSets = 32;

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly SimpleDdgiNearFieldResidualTargetBinding _targets;
    private readonly HiZDepthPyramid _hiZ;
    private readonly SimpleDdgiNearFieldResidualLayout _layout;
    private readonly SimpleDdgiNearFieldResidualGpuConfiguration _configuration;
    private readonly SimpleDdgiNearFieldResidualVulkanBuffers _buffers;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly nint _entryPointName;

    private DescriptorSetLayout _resetSetLayout;
    private DescriptorSetLayout _prepareSetLayout;
    private DescriptorSetLayout _classifySetLayout;
    private DescriptorSetLayout _traceSetLayout;
    private DescriptorSetLayout _temporalSetLayout;
    private DescriptorSetLayout _finalizeSetLayout;
    private DescriptorSetLayout _filterSetLayout;
    private DescriptorSetLayout _frequencySetLayout;
    private DescriptorSetLayout _compositeSetLayout;
    private DescriptorPool _descriptorPool;
    private readonly DescriptorSet[] _resetSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _prepareSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _classifySets = new DescriptorSet[2];
    private readonly DescriptorSet[] _traceSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _temporalSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _finalizeSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _filterSets;
    private readonly DescriptorSet[] _frequencySets = new DescriptorSet[2];
    private readonly DescriptorSet[] _compositeSets = new DescriptorSet[2];

    private PipelineLayout _resetPipelineLayout;
    private PipelineLayout _preparePipelineLayout;
    private PipelineLayout _classifyPipelineLayout;
    private PipelineLayout _tracePipelineLayout;
    private PipelineLayout _temporalPipelineLayout;
    private PipelineLayout _finalizePipelineLayout;
    private PipelineLayout _filterPipelineLayout;
    private PipelineLayout _frequencyPipelineLayout;
    private PipelineLayout _compositePipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _resetPipeline;
    private VkPipeline _preparePipeline;
    private VkPipeline _classifyPipeline;
    private VkPipeline _tracePipeline;
    private VkPipeline _temporalPipeline;
    private VkPipeline _finalizePipeline;
    private VkPipeline _filterPipeline;
    private VkPipeline _frequencyPipeline;
    private VkPipeline _compositePipeline;
    private bool _disposed;

    public SimpleDdgiNearFieldResidualGpuCommandRecorder(
        VulkanContext context,
        BufferManager bufferManager,
        BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualTargetBinding targets,
        HiZDepthPyramid hiZ,
        in SimpleDdgiNearFieldResidualLayout layout,
        in SimpleDdgiNearFieldResidualGpuConfiguration configuration,
        in SimpleDdgiNearFieldResidualVulkanBuffers buffers,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _hiZ = hiZ ?? throw new ArgumentNullException(nameof(hiZ));
        _layout = layout;
        _configuration = configuration;
        _buffers = buffers;
        _pipelineCacheService = pipelineCacheService;
        _filterSets = new DescriptorSet[checked(2 * _configuration.FilterIterationCount)];
        _entryPointName = SilkMarshal.StringToPtr("main");

        try
        {
            SimpleDdgiNearFieldResidualGpuAbi.VerifyManagedLayout();
            ValidatePushConstantLimit();
            CreateDescriptorSetLayouts();
            CreateDescriptorPoolAndSets();
            CreatePipelineCache();
            _resetPipelineLayout = CreatePipelineLayout(
                _resetSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.ResetPushConstantByteCount,
                "C5 Reset Pipeline Layout");
            _preparePipelineLayout = CreatePipelineLayout(
                _prepareSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.PreparePushConstantByteCount,
                "C5 Prepare Pipeline Layout");
            _classifyPipelineLayout = CreatePipelineLayout(
                _classifySetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.ClassifyPushConstantByteCount,
                "C5 Classify Pipeline Layout");
            _tracePipelineLayout = CreatePipelineLayout(
                _traceSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.TracePushConstantByteCount,
                "C5 Trace Pipeline Layout");
            _temporalPipelineLayout = CreatePipelineLayout(
                _temporalSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.TemporalPushConstantByteCount,
                "C5 Temporal Pipeline Layout");
            _finalizePipelineLayout = CreatePipelineLayout(
                _finalizeSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.FinalizePushConstantByteCount,
                "C5 Finalize Pipeline Layout");
            _filterPipelineLayout = CreatePipelineLayout(
                _filterSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.FilterPushConstantByteCount,
                "C5 Filter Pipeline Layout");
            _frequencyPipelineLayout = CreatePipelineLayout(
                _frequencySetLayout,
                SimpleDdgiNearFieldResidualGpuAbi
                    .FrequencySeparationPushConstantByteCount,
                "C5 Frequency Separation Pipeline Layout");
            _compositePipelineLayout = CreatePipelineLayout(
                _compositeSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.CompositePushConstantByteCount,
                "C5 Composite Pipeline Layout");
            _resetPipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.ResetShader,
                _resetPipelineLayout, "C5 Reset Pipeline");
            _preparePipeline = CreatePipeline(
                SimpleDdgiNearFieldResidualGpuPassNames.PrepareShader,
                _preparePipelineLayout, "C5 Prepare Pipeline");
            _classifyPipeline = CreatePipeline(
                SimpleDdgiNearFieldResidualGpuPassNames.ClassifyShader,
                _classifyPipelineLayout, "C5 Classify Pipeline");
            _tracePipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.TraceShader,
                _tracePipelineLayout, "C5 Trace Pipeline");
            _temporalPipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.TemporalShader,
                _temporalPipelineLayout, "C5 Temporal Pipeline");
            _finalizePipeline = CreatePipeline(
                SimpleDdgiNearFieldResidualGpuPassNames.FinalizeShader,
                _finalizePipelineLayout, "C5 Finalize Pipeline");
            _filterPipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.FilterShader,
                _filterPipelineLayout, "C5 Filter Pipeline");
            _frequencyPipeline = CreatePipeline(
                SimpleDdgiNearFieldResidualGpuPassNames.FrequencySeparationShader,
                _frequencyPipelineLayout, "C5 Frequency Separation Pipeline");
            _compositePipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.CompositeShader,
                _compositePipelineLayout, "C5 Composite Pipeline");
            RewriteDescriptors();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool ShaderPipelinesValidated => !_disposed &&
        _resetPipeline.Handle != 0UL && _preparePipeline.Handle != 0UL &&
        _classifyPipeline.Handle != 0UL &&
        _tracePipeline.Handle != 0UL && _temporalPipeline.Handle != 0UL &&
        _finalizePipeline.Handle != 0UL &&
        _filterPipeline.Handle != 0UL && _frequencyPipeline.Handle != 0UL &&
        _compositePipeline.Handle != 0UL;

    internal bool DescriptorContractValidated => !_disposed &&
        _descriptorPool.Handle != 0UL &&
        _resetSetLayout.Handle != 0UL && _prepareSetLayout.Handle != 0UL &&
        _classifySetLayout.Handle != 0UL &&
        _traceSetLayout.Handle != 0UL && _temporalSetLayout.Handle != 0UL &&
        _finalizeSetLayout.Handle != 0UL &&
        _filterSetLayout.Handle != 0UL && _frequencySetLayout.Handle != 0UL &&
        _compositeSetLayout.Handle != 0UL &&
        AllSetsValid(_resetSets) && AllSetsValid(_prepareSets) &&
        AllSetsValid(_classifySets) &&
        AllSetsValid(_traceSets) && AllSetsValid(_temporalSets) &&
        AllSetsValid(_finalizeSets) &&
        AllSetsValid(_filterSets) && AllSetsValid(_frequencySets) &&
        AllSetsValid(_compositeSets);

    private static bool AllSetsValid(ReadOnlySpan<DescriptorSet> sets)
    {
        foreach (DescriptorSet set in sets)
        {
            if (set.Handle == 0UL)
                return false;
        }
        return true;
    }

    internal void RewriteDescriptors()
    {
        ThrowIfDisposed();
        for (int writeBank = 0; writeBank < 2; writeBank++)
            WriteResetDescriptorSet(writeBank);
        for (int frameSlot = 0; frameSlot < 2; frameSlot++)
            WritePrepareDescriptorSet(frameSlot);
        for (int writeBank = 0; writeBank < 2; writeBank++)
            WriteClassifyDescriptorSet(writeBank);
        for (int frameSlot = 0; frameSlot < 2; frameSlot++)
            WriteTraceDescriptorSet(frameSlot);
        for (int frameSlot = 0; frameSlot < 2; frameSlot++)
            WriteFinalizeDescriptorSet(frameSlot);
        for (int writeBank = 0; writeBank < 2; writeBank++)
        {
            WriteTemporalDescriptorSet(writeBank);
            for (int iteration = 0;
                 iteration < _configuration.FilterIterationCount;
                 iteration++)
            {
                WriteFilterDescriptorSet(writeBank, iteration);
            }
            WriteFrequencyDescriptorSet(writeBank);
            WriteCompositeDescriptorSet(writeBank);
        }
    }

    internal void RecordReset(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualExecutionExtent extent,
        ulong completedFrameSerial)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        var activeTileBuffer =
            _bufferManager.GetBuffer(_buffers.ActiveTileAndIndirect);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            activeTileBuffer,
            0UL,
            _bufferManager.GetBufferSize(_buffers.ActiveTileAndIndirect),
            0u);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(
                _buffers.HistoryMetadata(token.HistoryWriteIndex)),
            0UL,
            _bufferManager.GetBufferSize(
                _buffers.HistoryMetadata(token.HistoryWriteIndex)),
            0u);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(_buffers.TileRecords),
            0UL,
            _bufferManager.GetBufferSize(_buffers.TileRecords),
            0u);
        if (_configuration.LocalAdaptiveSchedulingEnabled)
        {
            _context.Api.CmdFillBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(
                    _buffers.SchedulerHistory(token.HistoryWriteIndex)),
                0UL,
                _bufferManager.GetBufferSize(
                    _buffers.SchedulerHistory(token.HistoryWriteIndex)),
                0u);
        }
        // Indirect dispatch deliberately skips inactive tiles. Clear every
        // transient image and the complete current history-write bank first,
        // so neither a zero-work frame nor a later tier promotion can expose
        // pixels left by an earlier use of this generation.
        ClearStorageImage(commandBuffer, _targets.NearFieldResidualRaw);
        ClearStorageImage(commandBuffer,
            HistoryRadiance(token.HistoryWriteIndex));
        ClearStorageImage(commandBuffer,
            HistoryMoments(token.HistoryWriteIndex));
        ClearStorageImage(commandBuffer,
            HistoryValidity(token.HistoryWriteIndex));
        ClearStorageImage(commandBuffer,
            HistoryNormals(token.HistoryWriteIndex));
        if (_targets.NearFieldResidualFilterScratch0 is { } scratch0)
            ClearStorageImage(commandBuffer, scratch0);
        if (_targets.NearFieldResidualFilterScratch1 is { } scratch1)
            ClearStorageImage(commandBuffer, scratch1);
        RecordTransferWriteBarrier(commandBuffer);
        uint tileCount = CalculateTileCount(extent);
        var push = new GPUSimpleDdgiNearFieldResidualResetPushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            MetadataCount = PixelCount(extent),
            TileWordCount = checked(
                SimpleDdgiNearFieldResidualGpuAbi.TelemetryHeaderWordCount +
                tileCount * SimpleDdgiNearFieldResidualGpuAbi.TileRecordWordCount),
            HistoryEpoch = token.HistoryEpoch,
            Flags = (uint)(
                SimpleDdgiNearFieldResidualGpuFlags.InvalidAndMissOutputsZeroed |
                SimpleDdgiNearFieldResidualGpuFlags.SourceAttachmentVerified),
            FrameSerialLow = unchecked((uint)completedFrameSerial),
            FrameSerialHigh = unchecked((uint)(completedFrameSerial >> 32)),
            TileCount = tileCount
        };
        BindAndPush(commandBuffer, _resetPipeline, _resetPipelineLayout,
            _resetSets[token.HistoryWriteIndex], &push,
            SimpleDdgiNearFieldResidualGpuAbi.ResetPushConstantByteCount);
        uint workItems = Math.Max(push.MetadataCount, push.TileWordCount);
        _context.Api.CmdDispatch(commandBuffer,
            DivideRoundUp(workItems, ResetLocalSize), 1u, 1u);
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordPrepare(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualExecutionExtent extent,
        float nearPlane,
        float farPlane,
        BufferHandle objectData,
        BufferHandle materialData,
        BufferHandle foliagePrototypes,
        BufferHandle foliagePatches,
        BufferHandle foliageClusters,
        uint foliageTokenBase,
        bool foliageMotionVectorsValid)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (!objectData.IsValid || !materialData.IsValid)
            throw new InvalidOperationException(
                "C5 prepare requires the exact scene object/material buffers.");
        WritePrepareSceneDescriptors(
            frameIndex,
            objectData,
            materialData,
            foliagePrototypes,
            foliagePatches,
            foliageClusters);
        uint tileCapacity = CalculateTileCount(extent);
        var push = new GPUSimpleDdgiNearFieldResidualPreparePushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            FullWidth = checked((uint)_layout.SourceWidth),
            FullHeight = checked((uint)_layout.SourceHeight),
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height),
            Flags = (SimpleDdgiNearFieldResidualGpuFlags)(
                (uint)BaseFlags |
                (_configuration.LocalAdaptiveSchedulingEnabled
                    ? (uint)SimpleDdgiNearFieldResidualGpuFlags
                        .LocalAdaptiveScheduling
                    : 0u) |
                (foliageMotionVectorsValid
                    ? (uint)SimpleDdgiNearFieldResidualGpuFlags
                        .FoliageMotionVectorsValid
                    : 0u) |
                (Math.Min(
                    foliageTokenBase,
                    SimpleDdgiNearFieldResidualGpuAbi
                        .MaximumSurfaceTableEntryCount) << 16)),
            TileCapacity = tileCapacity,
            // The high bit is an ABI-stamped prepare-only producer selector;
            // the low byte retains the exact 1..4 ray count.
            RaysPerPixel = checked((uint)_configuration.RaysPerPixel) |
                (_layout.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster
                    ? 0x8000_0000u
                    : 0u),
            NearPlane = MathF.Max(nearPlane, 0.001f),
            FarPlane = MathF.Max(farPlane, MathF.Max(nearPlane, 0.001f) + 0.01f),
            ActiveTileHeaderWords =
                SimpleDdgiNearFieldResidualGpuAbi.ActiveTileHeaderWordCount,
            IndirectStageCount = SimpleDdgiNearFieldResidualGpuAbi.IndirectStageCount
        };
        BindAndPush(commandBuffer, _preparePipeline, _preparePipelineLayout,
            _prepareSets[frameIndex], &push,
            SimpleDdgiNearFieldResidualGpuAbi.PreparePushConstantByteCount);
        // Dispatch the complete admitted extent. The shader writes exact-zero
        // prepared data outside the currently selected tier, so a later tier
        // promotion cannot expose stale receiver state.
        _context.Api.CmdDispatch(commandBuffer,
            DivideRoundUp(checked((uint)_layout.TraceWidth), ComputeLocalSize),
            DivideRoundUp(checked((uint)_layout.TraceHeight), ComputeLocalSize),
            1u);
        RecordComputeWriteBarrier(commandBuffer, includeIndirectRead: true);
    }

    internal void RecordClassify(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualExecutionExtent extent,
        in SimpleDdgiNearFieldResidualGpuHistoryRevision revision,
        bool historyInputValid,
        bool sourceLightingEpochChanged,
        ulong frameSerial)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        if (!_configuration.LocalAdaptiveSchedulingEnabled)
            throw new InvalidOperationException(
                "C5 classifier recorded while local adaptivity is disabled.");
        SimpleDdgiNearFieldResidualSchedulerThresholds thresholds =
            SimpleDdgiNearFieldResidualSchedulerThresholds.ForPreset(
                _configuration.Preset);
        var flags = BaseFlags |
            SimpleDdgiNearFieldResidualGpuFlags.LocalAdaptiveScheduling;
        if (historyInputValid)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.HistoryInputValid;
        if (revision.CameraCut)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.CameraCut;
        if (sourceLightingEpochChanged)
            flags |= SimpleDdgiNearFieldResidualGpuFlags
                .SourceLightingEpochChanged;
        uint schedulerEpoch = HashSchedulerEpoch(revision);
        var push = new GPUSimpleDdgiNearFieldResidualClassifyPushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height),
            TileCapacity = CalculateTileCount(extent),
            Flags = flags,
            HistoryEpoch = token.HistoryEpoch,
            FrameSerialLow = unchecked((uint)frameSerial),
            FrameSerialHigh = unchecked((uint)(frameSerial >> 32)),
            SchedulerEpoch = schedulerEpoch,
            MaximumRaysPerPixel = checked((uint)_configuration.RaysPerPixel),
            NormalRaysPerPixel = checked((uint)Math.Min(
                2, _configuration.RaysPerPixel)),
            MaximumHistoryOnlyAge = thresholds.MaximumHistoryOnlyAge,
            ForcedRefreshPeriod = thresholds.ForcedRefreshPeriod,
            HighMotion = thresholds.HighMotion,
            HighVariance = thresholds.HighVariance,
            ActiveEnergy = thresholds.ActiveEnergy,
            PerceptualEnergyFloor = thresholds.PerceptualEnergyFloor,
            LowConfidence = thresholds.LowConfidence,
            HistoryOnlyConfidenceDecay = 0.96f,
            InterleavedConfidenceDecay = 0.985f,
            ReceiverCacheMetadataAvailable = 0u,
            FullWidth = checked((uint)_layout.SourceWidth),
            FullHeight = checked((uint)_layout.SourceHeight)
        };
        BindAndPush(commandBuffer, _classifyPipeline, _classifyPipelineLayout,
            _classifySets[token.HistoryWriteIndex], &push,
            SimpleDdgiNearFieldResidualGpuAbi.ClassifyPushConstantByteCount);
        _context.Api.CmdDispatch(commandBuffer,
            DivideRoundUp(checked((uint)extent.Width), ComputeLocalSize),
            DivideRoundUp(checked((uint)extent.Height), ComputeLocalSize),
            1u);
        RecordComputeWriteBarrier(commandBuffer, includeIndirectRead: true);
    }

    internal void RecordTrace(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualExecutionExtent extent)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        var push = new GPUSimpleDdgiNearFieldResidualTracePushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            TraceSourceTerms = (uint)_configuration.TraceSourceContract.Terms,
            FullWidth = checked((uint)_layout.SourceWidth),
            FullHeight = checked((uint)_layout.SourceHeight),
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height),
            FrameIndex = checked((uint)frameIndex),
            HistoryEpoch = token.HistoryEpoch,
            MaximumTraceSteps = checked((uint)_configuration.MaximumTraceSteps),
            RaysPerPixel = checked((uint)_configuration.RaysPerPixel),
            BinaryRefinementSteps = checked((uint)_configuration.BinaryRefinementSteps),
            Flags = BaseFlags |
                (_configuration.LocalAdaptiveSchedulingEnabled
                    ? SimpleDdgiNearFieldResidualGpuFlags.LocalAdaptiveScheduling
                    : SimpleDdgiNearFieldResidualGpuFlags.None),
            Thickness = _configuration.Thickness,
            StartBias = _configuration.StartBias,
            DepthTolerance = _configuration.DepthTolerance,
            MinimumNormalDot = _configuration.MinimumNormalDot,
            MaximumTraceDistance = _configuration.MaximumTraceDistance,
            FullWeightTraceDistance = _configuration.FullWeightTraceDistance,
            MinimumB3FootprintRadius = checked((uint)_configuration.MinimumB3FootprintRadius),
            MaximumB3FootprintRadius = checked((uint)_configuration.MaximumB3FootprintRadius),
            TraceSourceRevision = _configuration.TraceSourceContract.SourceRevision
        };
        BindAndPush(commandBuffer, _tracePipeline, _tracePipelineLayout,
            _traceSets[frameIndex & 1], &push,
            SimpleDdgiNearFieldResidualGpuAbi.TracePushConstantByteCount);
        DispatchIndirect(commandBuffer,
            SimpleDdgiNearFieldResidualGpuAbi.TraceIndirectStage);
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordTemporal(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualGpuHistoryRevision revision,
        bool historyInputValid,
        bool sourceLightingEpochChanged,
        bool cameraOnlyReprojection,
        in SimpleDdgiNearFieldResidualExecutionExtent extent)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        var flags = BaseFlags;
        if (historyInputValid)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.HistoryInputValid;
        if (revision.CameraCut)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.CameraCut;
        if (sourceLightingEpochChanged)
        {
            flags |= SimpleDdgiNearFieldResidualGpuFlags
                .SourceLightingEpochChanged;
        }
        if (_configuration.LocalAdaptiveSchedulingEnabled)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.LocalAdaptiveScheduling;
        if (cameraOnlyReprojection)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.CameraOnlyReprojection;
        var push = new GPUSimpleDdgiNearFieldResidualTemporalPushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height),
            HistoryReadIndex = checked((uint)token.HistoryReadIndex),
            HistoryWriteIndex = checked((uint)token.HistoryWriteIndex),
            HistoryEpoch = token.HistoryEpoch,
            TraceSourceAbiRevision = revision.TraceSourceAbiRevision,
            ViewportRevision = revision.ViewportRevision,
            HiZRevision = revision.HiZRevision,
            EffectiveModeRevision = revision.EffectiveModeRevision,
            ExposureDomainRevision = revision.ExposureDomainRevision,
            StructuralProjectionRevision = revision.StructuralProjectionRevision,
            OriginRebaseRevision = revision.OriginRebaseRevision,
            SceneGeneration = revision.SceneGeneration,
            TraceSourceContentRevision = revision.TraceSourceContentRevision,
            NearFieldLayoutRevision = revision.NearFieldLayoutRevision,
            B3OwnershipRevision = revision.B3OwnershipRevision,
            TraceSourceLayoutRevision = revision.TraceSourceLayoutRevision,
            MaximumHistoryLength = checked((uint)_configuration.MaximumHistoryLength),
            Flags = flags,
            TemporalBlend = _configuration.TemporalBlend,
            DepthTolerance = _configuration.DepthTolerance,
            MinimumNormalDot = _configuration.MinimumNormalDot,
            HitUvTolerance = _configuration.HitUvTolerance
        };
        DescriptorSet set = _temporalSets[token.HistoryWriteIndex];
        BindAndPush(commandBuffer, _temporalPipeline, _temporalPipelineLayout,
            set, &push,
            SimpleDdgiNearFieldResidualGpuAbi.TemporalPushConstantByteCount);
        DispatchIndirect(commandBuffer,
            SimpleDdgiNearFieldResidualGpuAbi.TemporalIndirectStage);
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordFinalize(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiNearFieldResidualExecutionExtent extent)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        RenderingConstants.ValidateFrameIndex(frameIndex);
        var push = new GPUSimpleDdgiNearFieldResidualFinalizePushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            TileCount = CalculateTileCount(extent),
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height)
        };
        BindAndPush(commandBuffer, _finalizePipeline, _finalizePipelineLayout,
            _finalizeSets[frameIndex], &push,
            SimpleDdgiNearFieldResidualGpuAbi.FinalizePushConstantByteCount);
        _context.Api.CmdDispatch(commandBuffer, 1u, 1u, 1u);
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordFilter(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        int iteration,
        in SimpleDdgiNearFieldResidualExecutionExtent extent)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        if (iteration < 0 || iteration >= _configuration.FilterIterationCount)
            throw new ArgumentOutOfRangeException(nameof(iteration));
        var push = new GPUSimpleDdgiNearFieldResidualFilterPushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height),
            IterationIndex = checked((uint)iteration),
            IterationCount = checked((uint)_configuration.FilterIterationCount),
            FilterRadius = checked((uint)_configuration.FilterRadius),
            DepthTolerance = _configuration.DepthTolerance,
            NormalPower = 8.0f,
            MinimumNormalDot = _configuration.MinimumNormalDot,
            HistoryEpoch = token.HistoryEpoch,
            Flags = (uint)BaseFlags
        };
        DescriptorSet set = _filterSets[FilterSetIndex(token.HistoryWriteIndex, iteration)];
        BindAndPush(commandBuffer, _filterPipeline, _filterPipelineLayout,
            set, &push,
            SimpleDdgiNearFieldResidualGpuAbi.FilterPushConstantByteCount);
        DispatchIndirect(commandBuffer, checked(
            SimpleDdgiNearFieldResidualGpuAbi.FirstFilterIndirectStage +
            (uint)iteration));
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordFrequencySeparation(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualExecutionExtent extent,
        uint debugView)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        var push = new GPUSimpleDdgiNearFieldResidualFrequencyPushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height),
            HistoryEpoch = token.HistoryEpoch,
            ActiveTileHeaderWords =
                SimpleDdgiNearFieldResidualGpuAbi.ActiveTileHeaderWordCount,
            Flags = BaseFlags,
            DepthTolerance = _configuration.DepthTolerance,
            MinimumNormalDot = _configuration.MinimumNormalDot,
            MaximumOuterStride = SimpleDdgiNearFieldResidualGpuAbi
                .MaximumB3FootprintRadius,
            DebugView = debugView
        };
        if (debugView == (uint)GlobalIlluminationDebugView.C5RawCandidate)
        {
            // The trace candidate already lives in NearFieldResidualRaw. A
            // frequency dispatch would overwrite it with the filtered band.
            RecordComputeWriteBarrier(commandBuffer);
            return;
        }
        BindAndPush(commandBuffer, _frequencyPipeline, _frequencyPipelineLayout,
            _frequencySets[token.HistoryWriteIndex], &push,
            SimpleDdgiNearFieldResidualGpuAbi
                .FrequencySeparationPushConstantByteCount);
        DispatchIndirect(commandBuffer,
            SimpleDdgiNearFieldResidualGpuAbi.FrequencySeparationIndirectStage);
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordComposite(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualExecutionExtent extent,
        uint debugView)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        var push = new GPUSimpleDdgiNearFieldResidualCompositePushConstants
        {
            AbiVersion = SimpleDdgiNearFieldResidualGpuAbi.Version,
            FullWidth = checked((uint)_layout.SourceWidth),
            FullHeight = checked((uint)_layout.SourceHeight),
            TraceWidth = checked((uint)extent.Width),
            TraceHeight = checked((uint)extent.Height),
            HistoryEpoch = token.HistoryEpoch,
            Flags = BaseFlags |
                SimpleDdgiNearFieldResidualGpuFlags.CompositeUsesValidResidualOnly,
            ResidualIntensity = _configuration.ResidualIntensity,
            ConfidenceFloor = 1.0e-4f,
            DebugView = debugView
        };
        BindAndPush(commandBuffer, _compositePipeline, _compositePipelineLayout,
            _compositeSets[token.HistoryWriteIndex], &push,
            SimpleDdgiNearFieldResidualGpuAbi.CompositePushConstantByteCount);
        if (SimpleDdgiNearFieldResidualDebugViewContract.IsC5View(debugView))
        {
            // Debug views must clear unsupported and inactive pixels instead
            // of leaving canonical scene colour behind. This full-screen path
            // is diagnostic-only; production composition remains compacted.
            _context.Api.CmdDispatch(commandBuffer,
                DivideRoundUp(checked((uint)_layout.SourceWidth), 8u),
                DivideRoundUp(checked((uint)_layout.SourceHeight), 8u),
                1u);
        }
        else
        {
            DispatchIndirect(commandBuffer,
                SimpleDdgiNearFieldResidualGpuAbi.CompositeIndirectStage);
        }
        RecordComputeWriteBarrier(commandBuffer);
    }

    private SimpleDdgiNearFieldResidualGpuFlags BaseFlags =>
        SimpleDdgiNearFieldResidualGpuFlags.ReversedZ |
        SimpleDdgiNearFieldResidualGpuFlags.SourceAttachmentVerified |
        SimpleDdgiNearFieldResidualGpuFlags.InvalidAndMissOutputsZeroed;

    private static uint HashSchedulerEpoch(
        in SimpleDdgiNearFieldResidualGpuHistoryRevision revision)
    {
        uint hash = 2166136261u;
        hash = (hash ^ revision.ViewportRevision) * 16777619u;
        hash = (hash ^ revision.StructuralProjectionRevision) * 16777619u;
        hash = (hash ^ revision.SceneGeneration) * 16777619u;
        hash = (hash ^ revision.TraceSourceContentRevision) * 16777619u;
        hash = (hash ^ revision.NearFieldLayoutRevision) * 16777619u;
        return hash == 0u ? 1u : hash;
    }

    private static uint PixelCount(
        in SimpleDdgiNearFieldResidualExecutionExtent extent) => checked(
        (uint)extent.Width * (uint)extent.Height);

    private void DispatchIndirect(CommandBuffer commandBuffer, uint stage) =>
        _context.Api.CmdDispatchIndirect(
            commandBuffer,
            _bufferManager.GetBuffer(_buffers.ActiveTileAndIndirect),
            SimpleDdgiNearFieldResidualGpuAbi.IndirectArgumentOffset(stage));

    private static uint CalculateTileCount(
        in SimpleDdgiNearFieldResidualExecutionExtent extent) => checked(
        DivideRoundUp(checked((uint)extent.Width), ComputeLocalSize) *
        DivideRoundUp(checked((uint)extent.Height), ComputeLocalSize));

    private void ValidateExecutionExtent(
        in SimpleDdgiNearFieldResidualExecutionExtent extent)
    {
        if (!extent.IsValid || extent.Width > _layout.TraceWidth ||
            extent.Height > _layout.TraceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(extent),
                "C5 execution extent must fit the admitted allocation.");
        }
    }

    private void BindAndPush(
        CommandBuffer commandBuffer,
        VkPipeline pipeline,
        PipelineLayout pipelineLayout,
        DescriptorSet descriptorSet,
        void* pushConstants,
        uint pushConstantBytes)
    {
        _context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            pipelineLayout,
            0u,
            1u,
            &descriptorSet,
            0u,
            null);
        _context.Api.CmdPushConstants(
            commandBuffer,
            pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0u,
            pushConstantBytes,
            pushConstants);
    }

    private void RecordComputeWriteBarrier(
        CommandBuffer commandBuffer,
        bool includeIndirectRead = false)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                (includeIndirectRead
                    ? PipelineStageFlags2.DrawIndirectBit
                    : PipelineStageFlags2.None),
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                (includeIndirectRead
                    ? AccessFlags2.IndirectCommandReadBit
                    : AccessFlags2.None)
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void RecordTransferWriteBarrier(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.IndirectCommandReadBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void ClearStorageImage(
        CommandBuffer commandBuffer,
        RenderTarget target)
    {
        if ((target.Usage & ImageUsageFlags.TransferDstBit) == 0)
        {
            throw new InvalidOperationException(
                $"C5 reset target '{target.Name}' lacks transfer-destination usage.");
        }

        ClearColorValue zero = default;
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0u,
            LevelCount = 1u,
            BaseArrayLayer = 0u,
            LayerCount = 1u
        };
        _context.Api.CmdClearColorImage(
            commandBuffer,
            target.Image,
            ImageLayout.General,
            &zero,
            1u,
            &range);
    }

    private void CreateDescriptorSetLayouts()
    {
        _resetSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.StorageBuffer),
            new(1u, DescriptorType.StorageBuffer)
        ], "C5 Reset Descriptor Set Layout");
        _prepareSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.CombinedImageSampler),
            new(2u, DescriptorType.CombinedImageSampler),
            new(3u, DescriptorType.CombinedImageSampler),
            new(4u, DescriptorType.StorageImage),
            new(5u, DescriptorType.StorageImage),
            new(6u, DescriptorType.StorageImage),
            new(7u, DescriptorType.StorageImage),
            new(8u, DescriptorType.StorageBuffer)
            ,new(9u, DescriptorType.StorageBuffer)
            ,new(10u, DescriptorType.StorageBuffer)
            ,new(11u, DescriptorType.StorageBuffer)
            ,new(12u, DescriptorType.StorageBuffer)
            ,new(13u, DescriptorType.StorageBuffer)
            ,new(14u, DescriptorType.StorageBuffer)
            ,new(15u, DescriptorType.StorageBuffer)
        ], "C5 Prepare Descriptor Set Layout");
        _classifySetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.CombinedImageSampler),
            new(2u, DescriptorType.CombinedImageSampler),
            new(3u, DescriptorType.StorageBuffer),
            new(4u, DescriptorType.StorageBuffer),
            new(5u, DescriptorType.StorageBuffer),
            new(6u, DescriptorType.StorageBuffer)
        ], "C5 Classify Descriptor Set Layout");
        _traceSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.CombinedImageSampler),
            new(2u, DescriptorType.CombinedImageSampler),
            new(3u, DescriptorType.CombinedImageSampler),
            new(4u, DescriptorType.StorageImage),
            new(5u, DescriptorType.StorageBuffer),
            new(6u, DescriptorType.StorageBuffer),
            new(7u, DescriptorType.StorageBuffer),
            new(8u, DescriptorType.StorageBuffer),
            new(9u, DescriptorType.StorageBuffer),
            new(10u, DescriptorType.CombinedImageSampler),
            new(11u, DescriptorType.CombinedImageSampler),
            new(12u, DescriptorType.StorageBuffer)
        ], "C5 Trace Descriptor Set Layout");
        _temporalSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.StorageBuffer),
            new(2u, DescriptorType.CombinedImageSampler),
            new(3u, DescriptorType.CombinedImageSampler),
            new(4u, DescriptorType.CombinedImageSampler),
            new(5u, DescriptorType.StorageBuffer),
            new(6u, DescriptorType.StorageImage),
            new(7u, DescriptorType.StorageImage),
            new(8u, DescriptorType.StorageImage),
            new(9u, DescriptorType.StorageBuffer),
            new(10u, DescriptorType.CombinedImageSampler),
            new(11u, DescriptorType.CombinedImageSampler),
            new(12u, DescriptorType.CombinedImageSampler),
            new(13u, DescriptorType.StorageImage),
            new(14u, DescriptorType.StorageBuffer),
            new(15u, DescriptorType.StorageBuffer),
            new(16u, DescriptorType.CombinedImageSampler),
            new(17u, DescriptorType.CombinedImageSampler),
            new(18u, DescriptorType.CombinedImageSampler),
            new(19u, DescriptorType.StorageBuffer),
            new(20u, DescriptorType.StorageBuffer),
            new(21u, DescriptorType.CombinedImageSampler),
            new(22u, DescriptorType.StorageBuffer)
        ], "C5 Temporal Descriptor Set Layout");
        _finalizeSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.StorageBuffer)
        ], "C5 Finalize Descriptor Set Layout");
        _filterSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.StorageBuffer),
            new(2u, DescriptorType.StorageImage),
            new(3u, DescriptorType.CombinedImageSampler),
            new(4u, DescriptorType.StorageBuffer),
            new(5u, DescriptorType.CombinedImageSampler)
        ], "C5 Filter Descriptor Set Layout");
        _frequencySetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.CombinedImageSampler),
            new(2u, DescriptorType.CombinedImageSampler),
            new(3u, DescriptorType.StorageBuffer),
            new(4u, DescriptorType.StorageImage),
            new(5u, DescriptorType.StorageBuffer),
            new(6u, DescriptorType.StorageBuffer)
        ], "C5 Frequency Separation Descriptor Set Layout");
        _compositeSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.CombinedImageSampler),
            new(2u, DescriptorType.StorageBuffer),
            new(3u, DescriptorType.StorageImage),
            new(4u, DescriptorType.StorageBuffer),
            new(5u, DescriptorType.CombinedImageSampler),
            new(6u, DescriptorType.CombinedImageSampler),
            new(7u, DescriptorType.StorageBuffer),
            new(8u, DescriptorType.CombinedImageSampler),
            new(9u, DescriptorType.StorageBuffer),
            new(10u, DescriptorType.CombinedImageSampler),
            new(11u, DescriptorType.CombinedImageSampler)
        ], "C5 Composite Descriptor Set Layout");
    }

    private DescriptorSetLayout CreateDescriptorSetLayout(
        BindingSpec[] specs,
        string debugName)
    {
        var bindings = new DescriptorSetLayoutBinding[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = specs[i].Binding,
                DescriptorType = specs[i].Type,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }
        fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = checked((uint)bindings.Length),
                PBindings = bindingPointer
            };
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device, &info, null, out DescriptorSetLayout layout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create " + debugName + ".", result);
            _context.SetDebugName(layout.Handle, ObjectType.DescriptorSetLayout, debugName);
            return layout;
        }
    }

    private void CreateDescriptorPoolAndSets()
    {
        DescriptorPoolSize* poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 192u
        };
        poolSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = 96u
        };
        poolSizes[2] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = 96u
        };
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3u,
            PPoolSizes = poolSizes,
            MaxSets = MaximumDescriptorSets
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device, &info, null, out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C5 descriptor pool.", result);

        for (int i = 0; i < 2; i++)
        {
            _resetSets[i] = AllocateDescriptorSet(_resetSetLayout);
            _prepareSets[i] = AllocateDescriptorSet(_prepareSetLayout);
            _classifySets[i] = AllocateDescriptorSet(_classifySetLayout);
            _traceSets[i] = AllocateDescriptorSet(_traceSetLayout);
            _temporalSets[i] = AllocateDescriptorSet(_temporalSetLayout);
            _finalizeSets[i] = AllocateDescriptorSet(_finalizeSetLayout);
            _frequencySets[i] = AllocateDescriptorSet(_frequencySetLayout);
            _compositeSets[i] = AllocateDescriptorSet(_compositeSetLayout);
        }
        for (int i = 0; i < _filterSets.Length; i++)
            _filterSets[i] = AllocateDescriptorSet(_filterSetLayout);
    }

    private DescriptorSet AllocateDescriptorSet(DescriptorSetLayout layout)
    {
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1u,
            PSetLayouts = &layout
        };
        Result result = _context.Api.AllocateDescriptorSets(
            _context.Device, &info, out DescriptorSet set);
        if (result != Result.Success)
            throw new VulkanException("Failed to allocate C5 descriptor set.", result);
        return set;
    }

    private void CreatePipelineCache()
    {
        if (_pipelineCacheService != null)
        {
            _pipelineCache = _pipelineCacheService.Cache;
            return;
        }

        var info = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device, &info, null, out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C5 pipeline cache.", result);
    }

    private PipelineLayout CreatePipelineLayout(
        DescriptorSetLayout setLayout,
        uint pushConstantBytes,
        string debugName)
    {
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = pushConstantBytes
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1u,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &range
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device, &info, null, out PipelineLayout layout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create " + debugName + ".", result);
        _context.SetDebugName(layout.Handle, ObjectType.PipelineLayout, debugName);
        return layout;
    }

    private VkPipeline CreatePipeline(
        string shaderName,
        PipelineLayout layout,
        string debugName)
    {
        ShaderModule module = default;
        try
        {
            module = ShaderModuleLoader.Load(_context, shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)_entryPointName
            };
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = layout,
                BasePipelineIndex = -1
            };
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId(
                        $"SimpleDdgi.NearFieldResidual.{shaderName}"),
                    &info,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1u,
                    &info,
                    null,
                    out pipeline);
            if (result != Result.Success)
                throw new VulkanException("Failed to create " + debugName + ".", result);
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

    private void WriteResetDescriptorSet(int writeBank)
    {
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[3];
        buffers[0] = BufferInfo(_buffers.HistoryMetadata(writeBank));
        buffers[1] = BufferInfo(_buffers.TileRecords);
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        writes[0] = BufferWrite(_resetSets[writeBank], 0u, &buffers[0]);
        writes[1] = BufferWrite(_resetSets[writeBank], 1u, &buffers[1]);
        _context.Api.UpdateDescriptorSets(_context.Device, 2u, writes, 0u, null);
    }

    private void WritePrepareDescriptorSet(int frameSlot)
    {
        DescriptorSet set = _prepareSets[frameSlot];
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[8];
        images[0] = SampledDepth(_targets.SceneDepth, _bindlessHeap.ScreenSampler);
        images[1] = Sampled(_targets.NearFieldDirectSource!, _bindlessHeap.ScreenSampler);
        images[2] = Sampled(_targets.NearFieldReceiverPayload!, _bindlessHeap.HiZSampler);
        images[3] = Sampled(_targets.MotionVectors, _bindlessHeap.ScreenSampler);
        images[4] = Storage(_targets.NearFieldPreparedDepthFootprint!);
        images[5] = Storage(_targets.NearFieldPreparedReceiverPayload!);
        images[6] = Storage(_targets.NearFieldPreparedMotion!);
        images[7] = Storage(_targets.NearFieldSourceLuminance!);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[8];
        buffers[0] = BufferInfo(_buffers.ActiveTileAndIndirect);
        ulong surfaceBankBytes = _layout.SurfaceTableBytes /
            (ulong)RenderingConstants.FramesInFlight;
        buffers[1] = BufferInfo(_buffers.SurfaceTable,
            checked((ulong)frameSlot * surfaceBankBytes), surfaceBankBytes);
        // Valid placeholders are replaced for this frame slot after its fence
        // has completed and immediately before prepare records.
        buffers[2] = buffers[1];
        buffers[3] = buffers[1];
        buffers[4] = BufferInfo(_buffers.TileRecords);
        buffers[5] = buffers[1];
        buffers[6] = buffers[1];
        buffers[7] = buffers[1];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[16];
        for (uint binding = 0u; binding <= 3u; binding++)
            writes[binding] = ImageWrite(set, binding,
                DescriptorType.CombinedImageSampler, &images[binding]);
        for (uint binding = 4u; binding <= 7u; binding++)
            writes[binding] = ImageWrite(set, binding,
                DescriptorType.StorageImage, &images[binding]);
        writes[8] = BufferWrite(set, 8u, &buffers[0]);
        writes[9] = BufferWrite(set, 9u, &buffers[1]);
        writes[10] = BufferWrite(set, 10u, &buffers[2]);
        writes[11] = BufferWrite(set, 11u, &buffers[3]);
        writes[12] = BufferWrite(set, 12u, &buffers[4]);
        writes[13] = BufferWrite(set, 13u, &buffers[5]);
        writes[14] = BufferWrite(set, 14u, &buffers[6]);
        writes[15] = BufferWrite(set, 15u, &buffers[7]);
        _context.Api.UpdateDescriptorSets(_context.Device, 16u, writes, 0u, null);
    }

    private void WritePrepareSceneDescriptors(
        int frameSlot,
        BufferHandle objectData,
        BufferHandle materialData,
        BufferHandle foliagePrototypes,
        BufferHandle foliagePatches,
        BufferHandle foliageClusters)
    {
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[5];
        buffers[0] = BufferInfo(objectData);
        buffers[1] = BufferInfo(materialData);
        BufferHandle fallback = _buffers.SurfaceTable;
        buffers[2] = BufferInfo(foliagePrototypes.IsValid
            ? foliagePrototypes
            : fallback);
        buffers[3] = BufferInfo(foliagePatches.IsValid
            ? foliagePatches
            : fallback);
        buffers[4] = BufferInfo(foliageClusters.IsValid
            ? foliageClusters
            : fallback);
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[5];
        writes[0] = BufferWrite(_prepareSets[frameSlot], 10u, &buffers[0]);
        writes[1] = BufferWrite(_prepareSets[frameSlot], 11u, &buffers[1]);
        writes[2] = BufferWrite(_prepareSets[frameSlot], 13u, &buffers[2]);
        writes[3] = BufferWrite(_prepareSets[frameSlot], 14u, &buffers[3]);
        writes[4] = BufferWrite(_prepareSets[frameSlot], 15u, &buffers[4]);
        _context.Api.UpdateDescriptorSets(_context.Device, 5u, writes, 0u, null);
    }

    private void WriteClassifyDescriptorSet(int writeBank)
    {
        int readBank = 1 - writeBank;
        DescriptorSet set = _classifySets[writeBank];
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[3];
        images[0] = Sampled(_targets.NearFieldPreparedDepthFootprint!,
            _bindlessHeap.HiZSampler);
        images[1] = Sampled(_targets.NearFieldPreparedReceiverPayload!,
            _bindlessHeap.HiZSampler);
        images[2] = Sampled(_targets.NearFieldPreparedMotion!,
            _bindlessHeap.ScreenSampler);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[4];
        buffers[0] = BufferInfo(_buffers.SchedulerHistory(readBank));
        buffers[1] = BufferInfo(_buffers.SchedulerHistory(writeBank));
        buffers[2] = BufferInfo(_buffers.ActiveTileAndIndirect);
        buffers[3] = BufferInfo(_buffers.TileRecords);
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[7];
        for (uint binding = 0u; binding < 3u; binding++)
        {
            writes[binding] = ImageWrite(set, binding,
                DescriptorType.CombinedImageSampler, &images[binding]);
        }
        for (uint binding = 3u; binding < 7u; binding++)
            writes[binding] = BufferWrite(set, binding, &buffers[binding - 3u]);
        _context.Api.UpdateDescriptorSets(_context.Device, 7u, writes, 0u, null);
    }

    private void WriteTraceDescriptorSet(int frameSlot)
    {
        DescriptorSet set = _traceSets[frameSlot];
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[8];
        images[0] = Sampled(_targets.NearFieldDirectSource!, _bindlessHeap.ScreenSampler);
        images[1] = new DescriptorImageInfo
        {
            Sampler = _bindlessHeap.HiZSampler,
            ImageView = _hiZ.FullView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        images[2] = Sampled(_targets.NearFieldPreparedDepthFootprint!,
            _bindlessHeap.HiZSampler);
        images[3] = Sampled(_targets.NearFieldPreparedReceiverPayload!,
            _bindlessHeap.HiZSampler);
        images[4] = Storage(_targets.NearFieldResidualRaw!);
        images[5] = Sampled(_targets.NearFieldReceiverPayload!, _bindlessHeap.HiZSampler);
        images[6] = Sampled(_targets.NearFieldSourceLuminance!, _bindlessHeap.ScreenSampler);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[6];
        buffers[0] = BufferInfo(_buffers.HistoryMetadata(frameSlot));
        buffers[1] = BufferInfo(_buffers.TileRecords);
        buffers[2] = BufferInfo(_buffers.TraceFrameConstants(frameSlot));
        ulong surfaceBankBytes = _layout.SurfaceTableBytes /
            (ulong)RenderingConstants.FramesInFlight;
        buffers[3] = BufferInfo(_buffers.SurfaceTable,
            checked((ulong)frameSlot * surfaceBankBytes), surfaceBankBytes);
        buffers[4] = BufferInfo(_buffers.ActiveTileAndIndirect);
        buffers[5] = BufferInfo(_buffers.SchedulerHistory(frameSlot));
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[13];
        for (uint binding = 0u; binding <= 3u; binding++)
            writes[binding] = ImageWrite(set, binding,
                DescriptorType.CombinedImageSampler, &images[binding]);
        writes[4] = ImageWrite(set, 4u, DescriptorType.StorageImage, &images[4]);
        writes[5] = BufferWrite(set, 5u, &buffers[0]);
        writes[6] = BufferWrite(set, 6u, &buffers[1]);
        writes[7] = BufferWrite(set, 7u, &buffers[2]);
        writes[8] = BufferWrite(set, 8u, &buffers[3]);
        writes[9] = BufferWrite(set, 9u, &buffers[4]);
        writes[10] = ImageWrite(set, 10u,
            DescriptorType.CombinedImageSampler, &images[5]);
        writes[11] = ImageWrite(set, 11u,
            DescriptorType.CombinedImageSampler, &images[6]);
        writes[12] = BufferWrite(set, 12u, &buffers[5]);
        _context.Api.UpdateDescriptorSets(_context.Device, 13u, writes, 0u, null);
    }

    private void WriteTemporalDescriptorSet(int writeBank)
    {
        int readBank = 1 - writeBank;
        DescriptorSet set = _temporalSets[writeBank];
        RenderTarget historyRead = HistoryRadiance(readBank);
        RenderTarget momentsRead = HistoryMoments(readBank);
        RenderTarget validityRead = HistoryValidity(readBank);
        RenderTarget normalRead = HistoryNormals(readBank);
        RenderTarget historyWrite = HistoryRadiance(writeBank);
        RenderTarget momentsWrite = HistoryMoments(writeBank);
        RenderTarget validityWrite = HistoryValidity(writeBank);
        RenderTarget normalWrite = HistoryNormals(writeBank);

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[15];
        images[0] = Sampled(_targets.NearFieldResidualRaw!, _bindlessHeap.ScreenSampler);
        images[1] = Sampled(historyRead, _bindlessHeap.ScreenSampler);
        images[2] = Sampled(momentsRead, _bindlessHeap.ScreenSampler);
        // R16_UINT validity is fetched with texelFetch in the shader and is
        // not a linearly filterable format. Keep a nearest sampler in the
        // combined descriptor so validation and implementations that inspect
        // sampler state never see an illegal linear-filter pairing.
        images[3] = Sampled(validityRead, _bindlessHeap.HiZSampler);
        images[4] = Storage(historyWrite);
        images[5] = Storage(momentsWrite);
        images[6] = Storage(validityWrite);
        images[7] = Sampled(_targets.NearFieldPreparedMotion!, _bindlessHeap.ScreenSampler);
        images[8] = Sampled(_targets.NearFieldPreparedReceiverPayload!,
            _bindlessHeap.HiZSampler);
        // Packed history normals use R32_UINT and are read with texelFetch.
        // Integer formats are not linearly filterable, so pairing this view
        // with the screen sampler is invalid even though the shader never
        // requests interpolation.
        images[9] = Sampled(normalRead, _bindlessHeap.HiZSampler);
        images[10] = Storage(normalWrite);
        images[11] = Sampled(_targets.NearFieldDirectSource!, _bindlessHeap.ScreenSampler);
        images[12] = SampledDepth(_targets.SceneDepth, _bindlessHeap.ScreenSampler);
        images[13] = Sampled(_targets.NearFieldReceiverPayload!, _bindlessHeap.HiZSampler);
        images[14] = Sampled(_targets.NearFieldPreparedDepthFootprint!,
            _bindlessHeap.HiZSampler);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[8];
        buffers[0] = BufferInfo(_buffers.HistoryMetadata(writeBank));
        buffers[1] = BufferInfo(_buffers.HistoryMetadata(readBank));
        buffers[2] = BufferInfo(_buffers.HistoryMetadata(writeBank));
        buffers[3] = BufferInfo(_buffers.TileRecords);
        buffers[4] = BufferInfo(_buffers.ActiveTileAndIndirect);
        buffers[5] = BufferInfo(_buffers.TraceFrameConstants(writeBank));
        ulong surfaceBankBytes = _layout.SurfaceTableBytes /
            (ulong)RenderingConstants.FramesInFlight;
        buffers[6] = BufferInfo(_buffers.SurfaceTable,
            checked((ulong)writeBank * surfaceBankBytes), surfaceBankBytes);
        buffers[7] = BufferInfo(_buffers.SchedulerHistory(writeBank));

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[23];
        writes[0] = ImageWrite(set, 0u, DescriptorType.CombinedImageSampler, &images[0]);
        writes[1] = BufferWrite(set, 1u, &buffers[0]);
        writes[2] = ImageWrite(set, 2u, DescriptorType.CombinedImageSampler, &images[1]);
        writes[3] = ImageWrite(set, 3u, DescriptorType.CombinedImageSampler, &images[2]);
        writes[4] = ImageWrite(set, 4u, DescriptorType.CombinedImageSampler, &images[3]);
        writes[5] = BufferWrite(set, 5u, &buffers[1]);
        writes[6] = ImageWrite(set, 6u, DescriptorType.StorageImage, &images[4]);
        writes[7] = ImageWrite(set, 7u, DescriptorType.StorageImage, &images[5]);
        writes[8] = ImageWrite(set, 8u, DescriptorType.StorageImage, &images[6]);
        writes[9] = BufferWrite(set, 9u, &buffers[2]);
        writes[10] = ImageWrite(set, 10u, DescriptorType.CombinedImageSampler, &images[7]);
        writes[11] = ImageWrite(set, 11u, DescriptorType.CombinedImageSampler, &images[8]);
        writes[12] = ImageWrite(set, 12u, DescriptorType.CombinedImageSampler, &images[9]);
        writes[13] = ImageWrite(set, 13u, DescriptorType.StorageImage, &images[10]);
        writes[14] = BufferWrite(set, 14u, &buffers[3]);
        writes[15] = BufferWrite(set, 15u, &buffers[4]);
        writes[16] = ImageWrite(set, 16u,
            DescriptorType.CombinedImageSampler, &images[11]);
        writes[17] = ImageWrite(set, 17u,
            DescriptorType.CombinedImageSampler, &images[12]);
        writes[18] = ImageWrite(set, 18u,
            DescriptorType.CombinedImageSampler, &images[13]);
        writes[19] = BufferWrite(set, 19u, &buffers[5]);
        writes[20] = BufferWrite(set, 20u, &buffers[6]);
        writes[21] = ImageWrite(set, 21u,
            DescriptorType.CombinedImageSampler, &images[14]);
        writes[22] = BufferWrite(set, 22u, &buffers[7]);
        _context.Api.UpdateDescriptorSets(_context.Device, 23u, writes, 0u, null);
    }

    private void WriteFinalizeDescriptorSet(int frameSlot)
    {
        DescriptorBufferInfo buffer = BufferInfo(_buffers.TileRecords);
        WriteDescriptorSet write = BufferWrite(
            _finalizeSets[frameSlot],
            SimpleDdgiNearFieldResidualGpuBindings.FinalizeTileRecords,
            &buffer);
        _context.Api.UpdateDescriptorSets(
            _context.Device, 1u, &write, 0u, null);
    }

    private void WriteFilterDescriptorSet(int writeBank, int iteration)
    {
        DescriptorSet set = _filterSets[FilterSetIndex(writeBank, iteration)];
        RenderTarget input = iteration == 0
            ? HistoryRadiance(writeBank)
            : FilterTarget(iteration - 1);
        RenderTarget output = FilterTarget(iteration);
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[4];
        images[0] = Sampled(input, _bindlessHeap.ScreenSampler);
        images[1] = Storage(output);
        images[2] = Sampled(_targets.NearFieldPreparedReceiverPayload!,
            _bindlessHeap.HiZSampler);
        images[3] = Sampled(HistoryMoments(writeBank),
            _bindlessHeap.ScreenSampler);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[2];
        buffers[0] = BufferInfo(_buffers.HistoryMetadata(writeBank));
        buffers[1] = BufferInfo(_buffers.ActiveTileAndIndirect);
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[7];
        writes[0] = ImageWrite(set, 0u, DescriptorType.CombinedImageSampler, &images[0]);
        writes[1] = BufferWrite(set, 1u, &buffers[0]);
        writes[2] = ImageWrite(set, 2u, DescriptorType.StorageImage, &images[1]);
        writes[3] = ImageWrite(set, 3u, DescriptorType.CombinedImageSampler, &images[2]);
        writes[4] = BufferWrite(set, 4u, &buffers[1]);
        writes[5] = ImageWrite(set, 5u,
            DescriptorType.CombinedImageSampler, &images[3]);
        _context.Api.UpdateDescriptorSets(_context.Device, 6u, writes, 0u, null);
    }

    private void WriteFrequencyDescriptorSet(int writeBank)
    {
        DescriptorSet set = _frequencySets[writeBank];
        RenderTarget nearEstimate = _configuration.FilterIterationCount == 0
            ? HistoryRadiance(writeBank)
            : FilterTarget(_configuration.FilterIterationCount - 1);
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[4];
        images[0] = Sampled(nearEstimate, _bindlessHeap.ScreenSampler);
        images[1] = Sampled(_targets.NearFieldPreparedDepthFootprint!,
            _bindlessHeap.HiZSampler);
        images[2] = Sampled(_targets.NearFieldPreparedReceiverPayload!,
            _bindlessHeap.HiZSampler);
        images[3] = Storage(_targets.NearFieldResidualRaw!);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[3];
        buffers[0] = BufferInfo(_buffers.HistoryMetadata(writeBank));
        buffers[1] = BufferInfo(_buffers.ActiveTileAndIndirect);
        buffers[2] = BufferInfo(_buffers.TraceFrameConstants(writeBank));
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[7];
        writes[0] = ImageWrite(set, 0u, DescriptorType.CombinedImageSampler, &images[0]);
        writes[1] = ImageWrite(set, 1u, DescriptorType.CombinedImageSampler, &images[1]);
        writes[2] = ImageWrite(set, 2u, DescriptorType.CombinedImageSampler, &images[2]);
        writes[3] = BufferWrite(set, 3u, &buffers[0]);
        writes[4] = ImageWrite(set, 4u, DescriptorType.StorageImage, &images[3]);
        writes[5] = BufferWrite(set, 5u, &buffers[1]);
        writes[6] = BufferWrite(set, 6u, &buffers[2]);
        _context.Api.UpdateDescriptorSets(_context.Device, 7u, writes, 0u, null);
    }

    private void WriteCompositeDescriptorSet(int writeBank)
    {
        DescriptorSet set = _compositeSets[writeBank];
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[8];
        images[0] = new DescriptorImageInfo
        {
            Sampler = _bindlessHeap.ScreenSampler,
            ImageView = _targets.SceneColor.View,
            ImageLayout = ImageLayout.General
        };
        images[1] = Sampled(_targets.NearFieldResidualRaw!, _bindlessHeap.ScreenSampler);
        images[2] = Storage(_targets.SceneColor);
        images[3] = Sampled(_targets.NearFieldReceiverPayload!, _bindlessHeap.HiZSampler);
        images[4] = SampledDepth(_targets.SceneDepth, _bindlessHeap.ScreenSampler);
        images[5] = Sampled(_targets.NearFieldPreparedReceiverPayload!,
            _bindlessHeap.HiZSampler);
        images[6] = Sampled(_targets.NearFieldDirectSource!,
            _bindlessHeap.ScreenSampler);
        images[7] = Sampled(HistoryValidity(writeBank),
            _bindlessHeap.HiZSampler);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[4];
        buffers[0] = BufferInfo(_buffers.HistoryMetadata(writeBank));
        buffers[1] = BufferInfo(_buffers.ActiveTileAndIndirect);
        ulong surfaceBankBytes = _layout.SurfaceTableBytes /
            (ulong)RenderingConstants.FramesInFlight;
        buffers[2] = BufferInfo(_buffers.SurfaceTable,
            checked((ulong)writeBank * surfaceBankBytes), surfaceBankBytes);
        buffers[3] = BufferInfo(_buffers.TraceFrameConstants(writeBank));
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[12];
        writes[0] = ImageWrite(set, 0u, DescriptorType.CombinedImageSampler, &images[0]);
        writes[1] = ImageWrite(set, 1u, DescriptorType.CombinedImageSampler, &images[1]);
        writes[2] = BufferWrite(set, 2u, &buffers[0]);
        writes[3] = ImageWrite(set, 3u, DescriptorType.StorageImage, &images[2]);
        writes[4] = BufferWrite(set, 4u, &buffers[1]);
        writes[5] = ImageWrite(set, 5u,
            DescriptorType.CombinedImageSampler, &images[3]);
        writes[6] = ImageWrite(set, 6u,
            DescriptorType.CombinedImageSampler, &images[4]);
        writes[7] = BufferWrite(set, 7u, &buffers[2]);
        writes[8] = ImageWrite(set, 8u,
            DescriptorType.CombinedImageSampler, &images[5]);
        writes[9] = BufferWrite(set, 9u, &buffers[3]);
        writes[10] = ImageWrite(set, 10u,
            DescriptorType.CombinedImageSampler, &images[6]);
        writes[11] = ImageWrite(set, 11u,
            DescriptorType.CombinedImageSampler, &images[7]);
        _context.Api.UpdateDescriptorSets(_context.Device, 12u, writes, 0u, null);
    }

    private DescriptorBufferInfo BufferInfo(BufferHandle handle) => new()
    {
        Buffer = _bufferManager.GetBuffer(handle),
        Offset = 0UL,
        Range = _bufferManager.GetBufferSize(handle)
    };

    private DescriptorBufferInfo BufferInfo(
        BufferHandle handle,
        ulong offset,
        ulong range) => new()
    {
        Buffer = _bufferManager.GetBuffer(handle),
        Offset = offset,
        Range = range
    };

    private static DescriptorImageInfo Sampled(RenderTarget target, Sampler sampler) => new()
    {
        Sampler = sampler,
        ImageView = target.View,
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal
    };

    private static DescriptorImageInfo SampledDepth(RenderTarget target, Sampler sampler) => new()
    {
        Sampler = sampler,
        ImageView = target.View,
        ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
    };

    private static DescriptorImageInfo Storage(RenderTarget target) => new()
    {
        ImageView = target.View,
        ImageLayout = ImageLayout.General
    };

    private static WriteDescriptorSet BufferWrite(
        DescriptorSet set,
        uint binding,
        DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1u,
        DescriptorType = DescriptorType.StorageBuffer,
        PBufferInfo = info
    };

    private static WriteDescriptorSet ImageWrite(
        DescriptorSet set,
        uint binding,
        DescriptorType type,
        DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1u,
        DescriptorType = type,
        PImageInfo = info
    };

    private RenderTarget HistoryRadiance(int index) => index == 0
        ? _targets.NearFieldResidualHistory0!
        : _targets.NearFieldResidualHistory1!;
    private RenderTarget HistoryMoments(int index) => index == 0
        ? _targets.NearFieldResidualMoments0!
        : _targets.NearFieldResidualMoments1!;
    private RenderTarget HistoryValidity(int index) => index == 0
        ? _targets.NearFieldResidualValidity0!
        : _targets.NearFieldResidualValidity1!;
    private RenderTarget HistoryNormals(int index) => index == 0
        ? _targets.NearFieldResidualHistoryNormals0!
        : _targets.NearFieldResidualHistoryNormals1!;
    private RenderTarget FilterTarget(int iteration)
    {
        if ((uint)iteration >= (uint)_configuration.FilterIterationCount)
            throw new ArgumentOutOfRangeException(nameof(iteration));
        // For odd iteration counts start in the physical scratch image; for
        // even counts start in RawCandidate. This always leaves the last
        // filtered estimate in scratch, so frequency separation can safely
        // write its final band back to RawCandidate.
        bool rawTarget = ((iteration +
            (_configuration.FilterIterationCount & 1)) & 1) == 0;
        return rawTarget
            ? _targets.NearFieldResidualRaw!
            : _targets.NearFieldResidualFilterScratch1!;
    }

    private int FilterSetIndex(int writeBank, int iteration) => checked(
        writeBank * _configuration.FilterIterationCount + iteration);

    private void ValidatePushConstantLimit()
    {
        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
        uint required = Math.Max(
            SimpleDdgiNearFieldResidualGpuAbi.TemporalPushConstantByteCount,
            SimpleDdgiNearFieldResidualGpuAbi.TracePushConstantByteCount);
        if (required > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"C5 needs {required} push-constant bytes but the device exposes " +
                $"{properties.Limits.MaxPushConstantsSize}.");
        }
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DestroyPipeline(_resetPipeline);
        DestroyPipeline(_preparePipeline);
        DestroyPipeline(_classifyPipeline);
        DestroyPipeline(_tracePipeline);
        DestroyPipeline(_temporalPipeline);
        DestroyPipeline(_finalizePipeline);
        DestroyPipeline(_filterPipeline);
        DestroyPipeline(_frequencyPipeline);
        DestroyPipeline(_compositePipeline);
        DestroyPipelineLayout(_resetPipelineLayout);
        DestroyPipelineLayout(_preparePipelineLayout);
        DestroyPipelineLayout(_classifyPipelineLayout);
        DestroyPipelineLayout(_tracePipelineLayout);
        DestroyPipelineLayout(_temporalPipelineLayout);
        DestroyPipelineLayout(_finalizePipelineLayout);
        DestroyPipelineLayout(_filterPipelineLayout);
        DestroyPipelineLayout(_frequencyPipelineLayout);
        DestroyPipelineLayout(_compositePipelineLayout);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        DestroyDescriptorSetLayout(_resetSetLayout);
        DestroyDescriptorSetLayout(_prepareSetLayout);
        DestroyDescriptorSetLayout(_classifySetLayout);
        DestroyDescriptorSetLayout(_traceSetLayout);
        DestroyDescriptorSetLayout(_temporalSetLayout);
        DestroyDescriptorSetLayout(_finalizeSetLayout);
        DestroyDescriptorSetLayout(_filterSetLayout);
        DestroyDescriptorSetLayout(_frequencySetLayout);
        DestroyDescriptorSetLayout(_compositeSetLayout);
        if (_pipelineCacheService is null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);
    }

    private void DestroyPipeline(VkPipeline pipeline)
    {
        if (pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, pipeline, null);
    }

    private void DestroyPipelineLayout(PipelineLayout layout)
    {
        if (layout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, layout, null);
    }

    private void DestroyDescriptorSetLayout(DescriptorSetLayout layout)
    {
        if (layout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(_context.Device, layout, null);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiNearFieldResidualGpuCommandRecorder));
    }

    private readonly record struct BindingSpec(uint Binding, DescriptorType Type);
}

internal abstract class SimpleDdgiNearFieldResidualGraphPass : RenderPassBase
{
    private readonly Func<SimpleDdgiNearFieldResidualVulkanRuntime?>
        _runtimeProvider;

    protected SimpleDdgiNearFieldResidualVulkanRuntime Runtime =>
        _runtimeProvider() ?? throw new InvalidOperationException(
            "The active C5 resource generation is unavailable.");

    protected SimpleDdgiNearFieldResidualGraphPass(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : this(name, context, swapchain, bindlessHeap, () => runtime)
    {
    }

    protected SimpleDdgiNearFieldResidualGraphPass(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(name, context, swapchain, bindlessHeap) =>
        _runtimeProvider = runtimeProvider ??
            throw new ArgumentNullException(nameof(runtimeProvider));

    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        _runtimeProvider()?.CanExecute(sceneData) == true;
    public override void Initialize()
    {
    }
    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }
    public override void OnSwapchainRecreated() =>
        _runtimeProvider()?.OnRenderTargetsRecreated();
}

internal sealed class SimpleDdgiNearFieldResidualResetPass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualResetPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Reset, context,
            swapchain, bindlessHeap, runtime)
    {
    }

    public SimpleDdgiNearFieldResidualResetPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Reset, context,
            swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordReset(cmd, frameIndex, sceneData);
}

internal sealed class SimpleDdgiNearFieldResidualTracePass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualTracePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Trace, context,
            swapchain, bindlessHeap, runtime)
    {
    }

    public SimpleDdgiNearFieldResidualTracePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Trace, context,
            swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordTrace(cmd, frameIndex, sceneData);
}

internal sealed class SimpleDdgiNearFieldResidualPreparePass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualPreparePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Prepare, context,
            swapchain, bindlessHeap, runtime)
    {
    }

    public SimpleDdgiNearFieldResidualPreparePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Prepare, context,
            swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordPrepare(cmd, frameIndex, sceneData);
}

internal sealed class SimpleDdgiNearFieldResidualClassifyPass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualClassifyPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Classify, context,
            swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override bool ShouldExecute(int frameIndex,
        SceneRenderingData sceneData) =>
        base.ShouldExecute(frameIndex, sceneData) &&
        Runtime.LocalAdaptiveSchedulingEnabled;

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordClassify(cmd, frameIndex, sceneData);
}

internal sealed class SimpleDdgiNearFieldResidualTemporalPass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualTemporalPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Temporal, context,
            swapchain, bindlessHeap, runtime)
    {
    }

    public SimpleDdgiNearFieldResidualTemporalPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Temporal, context,
            swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordTemporal(cmd, frameIndex, sceneData);
}

internal sealed class SimpleDdgiNearFieldResidualFinalizePass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualFinalizePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Finalize, context,
            swapchain, bindlessHeap, runtime)
    {
    }

    public SimpleDdgiNearFieldResidualFinalizePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Finalize, context,
            swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordFinalize(cmd, frameIndex, sceneData);
}

internal sealed class SimpleDdgiNearFieldResidualFilterPass :
    SimpleDdgiNearFieldResidualGraphPass
{
    private readonly int _iteration;

    public SimpleDdgiNearFieldResidualFilterPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime, int iteration)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.FilterIteration(iteration),
            context, swapchain, bindlessHeap, runtime)
    {
        _iteration = iteration;
    }

    public SimpleDdgiNearFieldResidualFilterPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider,
        int iteration)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.FilterIteration(iteration),
            context, swapchain, bindlessHeap, runtimeProvider)
    {
        _iteration = iteration;
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordFilter(cmd, frameIndex, sceneData, _iteration);
}

internal sealed class SimpleDdgiNearFieldResidualCompositePass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualCompositePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Composite, context,
            swapchain, bindlessHeap, runtime)
    {
    }

    public SimpleDdgiNearFieldResidualCompositePass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.Composite, context,
            swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordComposite(cmd, frameIndex, sceneData);
}

internal sealed class SimpleDdgiNearFieldResidualFrequencySeparationPass :
    SimpleDdgiNearFieldResidualGraphPass
{
    public SimpleDdgiNearFieldResidualFrequencySeparationPass(VulkanContext context,
        SwapchainManager swapchain, BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.FrequencySeparation,
            context, swapchain, bindlessHeap, runtime)
    {
    }

    public SimpleDdgiNearFieldResidualFrequencySeparationPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        Func<SimpleDdgiNearFieldResidualVulkanRuntime?> runtimeProvider)
        : base(SimpleDdgiNearFieldResidualGpuPassNames.FrequencySeparation,
            context, swapchain, bindlessHeap, runtimeProvider)
    {
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordFrequencySeparation(cmd, frameIndex, sceneData);
}
