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
    public const string Trace = "SimpleDdgiNearFieldResidualTracePass";
    public const string Temporal = "SimpleDdgiNearFieldResidualTemporalPass";
    public const string Filter = "SimpleDdgiNearFieldResidualFilterPass";
    public const string Composite = "SimpleDdgiNearFieldResidualCompositePass";

    public const string ResetShader = "ddgi_near_field_residual_reset.comp.spv";
    public const string TraceShader = "ddgi_near_field_residual_trace.comp.spv";
    public const string TemporalShader = "ddgi_near_field_residual_temporal.comp.spv";
    public const string FilterShader = "ddgi_near_field_residual_filter.comp.spv";
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
    Trace,
    Temporal,
    Filter,
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
    private const int MaximumDescriptorSets = 24;

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly RenderTargetManager _targets;
    private readonly HiZDepthPyramid _hiZ;
    private readonly SimpleDdgiNearFieldResidualLayout _layout;
    private readonly SimpleDdgiNearFieldResidualGpuConfiguration _configuration;
    private readonly SimpleDdgiNearFieldResidualVulkanBuffers _buffers;
    private readonly nint _entryPointName;

    private DescriptorSetLayout _resetSetLayout;
    private DescriptorSetLayout _traceSetLayout;
    private DescriptorSetLayout _temporalSetLayout;
    private DescriptorSetLayout _filterSetLayout;
    private DescriptorSetLayout _compositeSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _resetSet;
    private readonly DescriptorSet[] _traceSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _temporalSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _filterSets;
    private readonly DescriptorSet[] _compositeSets = new DescriptorSet[2];

    private PipelineLayout _resetPipelineLayout;
    private PipelineLayout _tracePipelineLayout;
    private PipelineLayout _temporalPipelineLayout;
    private PipelineLayout _filterPipelineLayout;
    private PipelineLayout _compositePipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _resetPipeline;
    private VkPipeline _tracePipeline;
    private VkPipeline _temporalPipeline;
    private VkPipeline _filterPipeline;
    private VkPipeline _compositePipeline;
    private bool _disposed;

    public SimpleDdgiNearFieldResidualGpuCommandRecorder(
        VulkanContext context,
        BufferManager bufferManager,
        BindlessHeap bindlessHeap,
        RenderTargetManager targets,
        HiZDepthPyramid hiZ,
        in SimpleDdgiNearFieldResidualLayout layout,
        in SimpleDdgiNearFieldResidualGpuConfiguration configuration,
        in SimpleDdgiNearFieldResidualVulkanBuffers buffers)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _hiZ = hiZ ?? throw new ArgumentNullException(nameof(hiZ));
        _layout = layout;
        _configuration = configuration;
        _buffers = buffers;
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
            _tracePipelineLayout = CreatePipelineLayout(
                _traceSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.TracePushConstantByteCount,
                "C5 Trace Pipeline Layout");
            _temporalPipelineLayout = CreatePipelineLayout(
                _temporalSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.TemporalPushConstantByteCount,
                "C5 Temporal Pipeline Layout");
            _filterPipelineLayout = CreatePipelineLayout(
                _filterSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.FilterPushConstantByteCount,
                "C5 Filter Pipeline Layout");
            _compositePipelineLayout = CreatePipelineLayout(
                _compositeSetLayout,
                SimpleDdgiNearFieldResidualGpuAbi.CompositePushConstantByteCount,
                "C5 Composite Pipeline Layout");
            _resetPipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.ResetShader,
                _resetPipelineLayout, "C5 Reset Pipeline");
            _tracePipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.TraceShader,
                _tracePipelineLayout, "C5 Trace Pipeline");
            _temporalPipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.TemporalShader,
                _temporalPipelineLayout, "C5 Temporal Pipeline");
            _filterPipeline = CreatePipeline(SimpleDdgiNearFieldResidualGpuPassNames.FilterShader,
                _filterPipelineLayout, "C5 Filter Pipeline");
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

    internal void RewriteDescriptors()
    {
        ThrowIfDisposed();
        WriteResetDescriptorSet();
        for (int frameSlot = 0; frameSlot < 2; frameSlot++)
            WriteTraceDescriptorSet(frameSlot);
        for (int writeBank = 0; writeBank < 2; writeBank++)
        {
            WriteTemporalDescriptorSet(writeBank);
            for (int iteration = 0;
                 iteration < _configuration.FilterIterationCount;
                 iteration++)
            {
                WriteFilterDescriptorSet(writeBank, iteration);
            }
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
            _resetSet, &push,
            SimpleDdgiNearFieldResidualGpuAbi.ResetPushConstantByteCount);
        uint workItems = Math.Max(push.MetadataCount, push.TileWordCount);
        _context.Api.CmdDispatch(commandBuffer,
            DivideRoundUp(workItems, ResetLocalSize), 1u, 1u);
        RecordComputeWriteBarrier(commandBuffer);
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
            MaximumMipVisits = checked((uint)_configuration.MaximumMipVisits),
            BinaryRefinementSteps = checked((uint)_configuration.BinaryRefinementSteps),
            Flags = BaseFlags,
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
        DispatchTraceExtent(commandBuffer, extent);
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordTemporal(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualGpuHistoryRevision revision,
        bool historyInputValid,
        in SimpleDdgiNearFieldResidualExecutionExtent extent)
    {
        ThrowIfDisposed();
        ValidateExecutionExtent(extent);
        var flags = BaseFlags;
        if (historyInputValid)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.HistoryInputValid;
        if (revision.CameraCut)
            flags |= SimpleDdgiNearFieldResidualGpuFlags.CameraCut;
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
            ProjectionJitterRevision = revision.ProjectionJitterRevision,
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
        DispatchTraceExtent(commandBuffer, extent);
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
        DispatchTraceExtent(commandBuffer, extent);
        RecordComputeWriteBarrier(commandBuffer);
    }

    internal void RecordComposite(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualGpuFrameToken token,
        in SimpleDdgiNearFieldResidualExecutionExtent extent)
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
            ResidualIntensity = 1.0f,
            ConfidenceFloor = 1.0e-4f
        };
        BindAndPush(commandBuffer, _compositePipeline, _compositePipelineLayout,
            _compositeSets[token.HistoryWriteIndex], &push,
            SimpleDdgiNearFieldResidualGpuAbi.CompositePushConstantByteCount);
        _context.Api.CmdDispatch(commandBuffer,
            DivideRoundUp(push.FullWidth, ComputeLocalSize),
            DivideRoundUp(push.FullHeight, ComputeLocalSize),
            1u);
        RecordComputeWriteBarrier(commandBuffer);
    }

    private SimpleDdgiNearFieldResidualGpuFlags BaseFlags =>
        SimpleDdgiNearFieldResidualGpuFlags.ReversedZ |
        SimpleDdgiNearFieldResidualGpuFlags.SourceAttachmentVerified |
        SimpleDdgiNearFieldResidualGpuFlags.InvalidAndMissOutputsZeroed;

    private static uint PixelCount(
        in SimpleDdgiNearFieldResidualExecutionExtent extent) => checked(
        (uint)extent.Width * (uint)extent.Height);

    private void DispatchTraceExtent(
        CommandBuffer commandBuffer,
        in SimpleDdgiNearFieldResidualExecutionExtent extent) =>
        _context.Api.CmdDispatch(commandBuffer,
            DivideRoundUp(checked((uint)extent.Width), ComputeLocalSize),
            DivideRoundUp(checked((uint)extent.Height), ComputeLocalSize),
            1u);

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

    private void RecordComputeWriteBarrier(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void CreateDescriptorSetLayouts()
    {
        _resetSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.StorageBuffer),
            new(1u, DescriptorType.StorageBuffer)
        ], "C5 Reset Descriptor Set Layout");
        _traceSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.CombinedImageSampler),
            new(2u, DescriptorType.CombinedImageSampler),
            new(3u, DescriptorType.CombinedImageSampler),
            new(4u, DescriptorType.StorageImage),
            new(5u, DescriptorType.StorageBuffer),
            new(6u, DescriptorType.StorageBuffer),
            new(7u, DescriptorType.StorageBuffer)
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
            new(14u, DescriptorType.StorageBuffer)
        ], "C5 Temporal Descriptor Set Layout");
        _filterSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.StorageBuffer),
            new(2u, DescriptorType.StorageImage),
            new(3u, DescriptorType.CombinedImageSampler)
        ], "C5 Filter Descriptor Set Layout");
        _compositeSetLayout = CreateDescriptorSetLayout(
        [
            new(0u, DescriptorType.CombinedImageSampler),
            new(1u, DescriptorType.CombinedImageSampler),
            new(2u, DescriptorType.StorageBuffer),
            new(3u, DescriptorType.StorageImage)
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

        _resetSet = AllocateDescriptorSet(_resetSetLayout);
        for (int i = 0; i < 2; i++)
        {
            _traceSets[i] = AllocateDescriptorSet(_traceSetLayout);
            _temporalSets[i] = AllocateDescriptorSet(_temporalSetLayout);
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
            Result result = _context.Api.CreateComputePipelines(
                _context.Device,
                _pipelineCache,
                1u,
                &info,
                null,
                out VkPipeline pipeline);
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

    private void WriteResetDescriptorSet()
    {
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[2];
        buffers[0] = BufferInfo(_buffers.HitMetadata);
        buffers[1] = BufferInfo(_buffers.TileRecords);
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        writes[0] = BufferWrite(_resetSet, 0u, &buffers[0]);
        writes[1] = BufferWrite(_resetSet, 1u, &buffers[1]);
        _context.Api.UpdateDescriptorSets(_context.Device, 2u, writes, 0u, null);
    }

    private void WriteTraceDescriptorSet(int frameSlot)
    {
        DescriptorSet set = _traceSets[frameSlot];
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[5];
        images[0] = Sampled(_targets.NearFieldDirectSource!, _bindlessHeap.ScreenSampler);
        images[1] = new DescriptorImageInfo
        {
            Sampler = _bindlessHeap.HiZSampler,
            ImageView = _hiZ.FullView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        images[2] = SampledDepth(_targets.SceneDepth, _bindlessHeap.ScreenSampler);
        images[3] = Sampled(_targets.NearFieldReceiverPayload!, _bindlessHeap.HiZSampler);
        images[4] = Storage(_targets.NearFieldResidualRaw!);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[4];
        buffers[0] = BufferInfo(_buffers.HitMetadata);
        buffers[1] = BufferInfo(_buffers.TileRecords);
        buffers[2] = BufferInfo(_buffers.TraceFrameConstants(frameSlot));
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[8];
        for (uint binding = 0u; binding <= 3u; binding++)
            writes[binding] = ImageWrite(set, binding,
                DescriptorType.CombinedImageSampler, &images[binding]);
        writes[4] = ImageWrite(set, 4u, DescriptorType.StorageImage, &images[4]);
        writes[5] = BufferWrite(set, 5u, &buffers[0]);
        writes[6] = BufferWrite(set, 6u, &buffers[1]);
        writes[7] = BufferWrite(set, 7u, &buffers[2]);
        _context.Api.UpdateDescriptorSets(_context.Device, 8u, writes, 0u, null);
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

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[11];
        images[0] = Sampled(_targets.NearFieldResidualRaw!, _bindlessHeap.ScreenSampler);
        images[1] = Sampled(historyRead, _bindlessHeap.ScreenSampler);
        images[2] = Sampled(momentsRead, _bindlessHeap.ScreenSampler);
        // R32_UINT validity is fetched with texelFetch in the shader and is
        // not a linearly filterable format. Keep a nearest sampler in the
        // combined descriptor so validation and implementations that inspect
        // sampler state never see an illegal linear-filter pairing.
        images[3] = Sampled(validityRead, _bindlessHeap.HiZSampler);
        images[4] = Storage(historyWrite);
        images[5] = Storage(momentsWrite);
        images[6] = Storage(validityWrite);
        images[7] = Sampled(_targets.MotionVectors, _bindlessHeap.ScreenSampler);
        images[8] = Sampled(_targets.NearFieldReceiverPayload!, _bindlessHeap.HiZSampler);
        images[9] = Sampled(normalRead, _bindlessHeap.ScreenSampler);
        images[10] = Storage(normalWrite);
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[4];
        buffers[0] = BufferInfo(_buffers.HitMetadata);
        buffers[1] = BufferInfo(_buffers.HistoryMetadata(readBank));
        buffers[2] = BufferInfo(_buffers.HistoryMetadata(writeBank));
        buffers[3] = BufferInfo(_buffers.TileRecords);

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[15];
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
        _context.Api.UpdateDescriptorSets(_context.Device, 15u, writes, 0u, null);
    }

    private void WriteFilterDescriptorSet(int writeBank, int iteration)
    {
        DescriptorSet set = _filterSets[FilterSetIndex(writeBank, iteration)];
        RenderTarget input = iteration == 0
            ? HistoryRadiance(writeBank)
            : FilterScratch((iteration - 1) & 1);
        RenderTarget output = FilterScratch(iteration & 1);
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[3];
        images[0] = Sampled(input, _bindlessHeap.ScreenSampler);
        images[1] = Storage(output);
        images[2] = Sampled(_targets.NearFieldReceiverPayload!, _bindlessHeap.HiZSampler);
        DescriptorBufferInfo metadata = BufferInfo(_buffers.HistoryMetadata(writeBank));
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];
        writes[0] = ImageWrite(set, 0u, DescriptorType.CombinedImageSampler, &images[0]);
        writes[1] = BufferWrite(set, 1u, &metadata);
        writes[2] = ImageWrite(set, 2u, DescriptorType.StorageImage, &images[1]);
        writes[3] = ImageWrite(set, 3u, DescriptorType.CombinedImageSampler, &images[2]);
        _context.Api.UpdateDescriptorSets(_context.Device, 4u, writes, 0u, null);
    }

    private void WriteCompositeDescriptorSet(int writeBank)
    {
        DescriptorSet set = _compositeSets[writeBank];
        RenderTarget residual = _configuration.FilterIterationCount == 0
            ? HistoryRadiance(writeBank)
            : FilterScratch((_configuration.FilterIterationCount - 1) & 1);
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[3];
        images[0] = new DescriptorImageInfo
        {
            Sampler = _bindlessHeap.ScreenSampler,
            ImageView = _targets.SceneColor.View,
            ImageLayout = ImageLayout.General
        };
        images[1] = Sampled(residual, _bindlessHeap.ScreenSampler);
        images[2] = Storage(_targets.SceneColor);
        DescriptorBufferInfo metadata = BufferInfo(_buffers.HistoryMetadata(writeBank));
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];
        writes[0] = ImageWrite(set, 0u, DescriptorType.CombinedImageSampler, &images[0]);
        writes[1] = ImageWrite(set, 1u, DescriptorType.CombinedImageSampler, &images[1]);
        writes[2] = BufferWrite(set, 2u, &metadata);
        writes[3] = ImageWrite(set, 3u, DescriptorType.StorageImage, &images[2]);
        _context.Api.UpdateDescriptorSets(_context.Device, 4u, writes, 0u, null);
    }

    private DescriptorBufferInfo BufferInfo(BufferHandle handle) => new()
    {
        Buffer = _bufferManager.GetBuffer(handle),
        Offset = 0UL,
        Range = _bufferManager.GetBufferSize(handle)
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
    private RenderTarget FilterScratch(int index) => index == 0
        ? _targets.NearFieldResidualFilterScratch0!
        : _targets.NearFieldResidualFilterScratch1!;

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
        DestroyPipeline(_tracePipeline);
        DestroyPipeline(_temporalPipeline);
        DestroyPipeline(_filterPipeline);
        DestroyPipeline(_compositePipeline);
        DestroyPipelineLayout(_resetPipelineLayout);
        DestroyPipelineLayout(_tracePipelineLayout);
        DestroyPipelineLayout(_temporalPipelineLayout);
        DestroyPipelineLayout(_filterPipelineLayout);
        DestroyPipelineLayout(_compositePipelineLayout);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        DestroyDescriptorSetLayout(_resetSetLayout);
        DestroyDescriptorSetLayout(_traceSetLayout);
        DestroyDescriptorSetLayout(_temporalSetLayout);
        DestroyDescriptorSetLayout(_filterSetLayout);
        DestroyDescriptorSetLayout(_compositeSetLayout);
        if (_pipelineCache.Handle != 0)
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
    protected readonly SimpleDdgiNearFieldResidualVulkanRuntime Runtime;

    protected SimpleDdgiNearFieldResidualGraphPass(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiNearFieldResidualVulkanRuntime runtime)
        : base(name, context, swapchain, bindlessHeap) =>
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Runtime.CanExecute(sceneData);
    public override void Initialize()
    {
    }
    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }
    public override void OnSwapchainRecreated() => Runtime.OnRenderTargetsRecreated();
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

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordTrace(cmd, frameIndex, sceneData);
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

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordTemporal(cmd, frameIndex, sceneData);
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

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData) => Runtime.RecordComposite(cmd, frameIndex, sceneData);
}
