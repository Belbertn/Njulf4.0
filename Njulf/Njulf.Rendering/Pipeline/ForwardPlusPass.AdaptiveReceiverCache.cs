using System;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

public sealed unsafe partial class ForwardPlusPass
{
    private const int AdaptiveReceiverDescriptorBindingCount = 12;

    private readonly BufferHandle[] _adaptiveReceiverMetadataBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _adaptiveReceiverTileScheduleBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _adaptiveReceiverGatherWorkBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _adaptiveReceiverResolveTileBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _adaptiveReceiverControlBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _adaptiveReceiverGatherStampBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _adaptiveReceiverMissingPrefixBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _adaptiveReceiverReadbackBuffers =
        new BufferHandle[FramesInFlight];
    private readonly DescriptorSet[] _adaptiveReceiverDescriptorSets =
        new DescriptorSet[FramesInFlight];
    private readonly ulong[] _adaptiveReceiverHistorySerials =
        new ulong[FramesInFlight];
    private readonly SimpleDdgiReceiverCacheHistoryIdentity[]
        _adaptiveReceiverHistoryIdentities =
            new SimpleDdgiReceiverCacheHistoryIdentity[FramesInFlight];
    private readonly bool[] _adaptiveReceiverReadbackRecorded =
        new bool[FramesInFlight];
    private readonly bool[] _receiverPublicationDependentClearPending =
        new bool[FramesInFlight];
    private readonly SimpleDdgiReceiverPublicationTracker
        _receiverPublicationTracker = new();

    private DescriptorSetLayout _adaptiveReceiverDescriptorSetLayout;
    private DescriptorPool _adaptiveReceiverDescriptorPool;
    private PipelineLayout _adaptiveReceiverPipelineLayout;
    private VkPipeline _adaptiveReceiverClassifyPipeline;
    private VkPipeline _adaptiveReceiverGatherPipeline;
    private VkPipeline _adaptiveReceiverFeedbackGatherPipeline;
    private VkPipeline _adaptiveReceiverMissingFeedbackGatherPipeline;
    private VkPipeline _adaptiveReceiverResolvePipeline;
    private bool _adaptiveReceiverInitializationAttempted;
    private bool _adaptiveReceiverInitializationFailed;
    private bool _adaptiveReceiverExecutedForCurrentView;
    private uint _adaptiveReceiverResourceGeneration;
    private ulong _adaptiveReceiverMetadataBytes;
    private ulong _adaptiveReceiverTileScheduleBytes;
    private ulong _adaptiveReceiverGatherWorkBytes;
    private ulong _adaptiveReceiverResolveTileBytes;
    private ulong _adaptiveReceiverGatherStampBytes;
    private ulong _adaptiveReceiverMissingPrefixBytes;
    private SimpleDdgiReceiverCacheFrameToken _adaptiveReceiverFrameToken;
    private SimpleDdgiReceiverCacheAdaptiveCounters _adaptiveReceiverCounters;
    private uint _receiverPublicationStamp = 1u;
    private uint _receiverPublicationChangedRegionMask;
    private bool _receiverPublicationGenerationEnabled;

    internal SimpleDdgiReceiverCacheFrameToken
        SimpleDdgiReceiverCacheFrameToken => _adaptiveReceiverFrameToken;

    internal SimpleDdgiReceiverCacheAdaptiveCounters
        SimpleDdgiReceiverCacheAdaptiveCounters => _adaptiveReceiverCounters;

    internal ulong SimpleDdgiReceiverCacheAdaptiveBytes => checked(
        (_adaptiveReceiverMetadataBytes +
         _adaptiveReceiverTileScheduleBytes +
         _adaptiveReceiverGatherWorkBytes +
         _adaptiveReceiverResolveTileBytes +
         SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes +
         _adaptiveReceiverGatherStampBytes +
         _adaptiveReceiverMissingPrefixBytes) * FramesInFlight);

    private void InitializeSimpleDdgiReceiverCacheAdaptiveInfrastructure()
    {
        if (_bufferManager is null ||
            _simpleDdgiReceiverCacheEntryPointName == 0 ||
            _simpleDdgiReceiverCachePipelineCache.Handle == 0 ||
            _simpleDdgiReceiverCacheWidth == 0u ||
            _simpleDdgiReceiverCacheHeight == 0u ||
            _simpleDdgiReceiverGatherWidth == 0u ||
            _simpleDdgiReceiverGatherHeight == 0u)
        {
            return;
        }

        if (_adaptiveReceiverInitializationAttempted ||
            _adaptiveReceiverInitializationFailed)
        {
            return;
        }

        try
        {
            _adaptiveReceiverInitializationAttempted = true;
            CreateSimpleDdgiReceiverCacheAdaptiveDescriptors();
            CreateSimpleDdgiReceiverCacheAdaptivePipelineLayout();
            RecreateSimpleDdgiReceiverCacheAdaptiveResources();
        }
        catch (Exception exception) when (
            exception is VulkanException or InvalidOperationException or
            ArgumentException or OverflowException or IOException)
        {
            System.Diagnostics.Debug.WriteLine(
                "Simple-DDGI adaptive receiver cache unavailable; canonical " +
                $"execution retained: {exception.GetType().Name}: " +
                exception.Message);
            CleanupSimpleDdgiReceiverCacheAdaptive(destroyInfrastructure: true);
            _adaptiveReceiverInitializationAttempted = true;
            _adaptiveReceiverInitializationFailed = true;
        }
    }

    private bool IsSimpleDdgiReceiverCacheAdaptiveReady(
        SimpleDdgiReceiverPipelineBank? pipelineBank) =>
        pipelineBank is not null &&
        AdaptiveReceiverInfrastructureValid();

    private bool AdaptiveReceiverInfrastructureValid()
    {
        if (_adaptiveReceiverClassifyPipeline.Handle == 0 ||
            _adaptiveReceiverGatherPipeline.Handle == 0 ||
            _adaptiveReceiverFeedbackGatherPipeline.Handle == 0 ||
            _adaptiveReceiverMissingFeedbackGatherPipeline.Handle == 0 ||
            _adaptiveReceiverResolvePipeline.Handle == 0 ||
            _adaptiveReceiverPipelineLayout.Handle == 0 ||
            _adaptiveReceiverResourceGeneration == 0u)
        {
            return false;
        }

        return AdaptiveReceiverResourcesValid();
    }

    private bool AdaptiveReceiverResourcesValid()
    {
        if (_adaptiveReceiverPipelineLayout.Handle == 0 ||
            _adaptiveReceiverResourceGeneration == 0u)
        {
            return false;
        }

        for (int i = 0; i < FramesInFlight; ++i)
        {
            if (!_adaptiveReceiverMetadataBuffers[i].IsValid ||
                !_adaptiveReceiverTileScheduleBuffers[i].IsValid ||
                !_adaptiveReceiverGatherWorkBuffers[i].IsValid ||
                !_adaptiveReceiverResolveTileBuffers[i].IsValid ||
                !_adaptiveReceiverControlBuffers[i].IsValid ||
                !_adaptiveReceiverGatherStampBuffers[i].IsValid ||
                !_adaptiveReceiverMissingPrefixBuffers[i].IsValid ||
                !_adaptiveReceiverReadbackBuffers[i].IsValid ||
                _adaptiveReceiverDescriptorSets[i].Handle == 0)
            {
                return false;
            }
        }

        return true;
    }

    private void CreateSimpleDdgiReceiverCacheAdaptiveDescriptors()
    {
        DescriptorSetLayoutBinding* bindings =
            stackalloc DescriptorSetLayoutBinding[
                AdaptiveReceiverDescriptorBindingCount];
        for (uint binding = 0u;
             binding < AdaptiveReceiverDescriptorBindingCount;
             ++binding)
        {
            bindings[binding] = new DescriptorSetLayoutBinding
            {
                Binding = binding,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = AdaptiveReceiverDescriptorBindingCount,
            PBindings = bindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &layoutInfo,
            null,
            out _adaptiveReceiverDescriptorSetLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create adaptive receiver-cache descriptor layout",
                result);
        }
        _context.SetDebugName(
            _adaptiveReceiverDescriptorSetLayout.Handle,
            ObjectType.DescriptorSetLayout,
            "Simple DDGI Adaptive Receiver Cache Descriptor Layout");

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = FramesInFlight *
                AdaptiveReceiverDescriptorBindingCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = FramesInFlight
        };
        result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _adaptiveReceiverDescriptorPool);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create adaptive receiver-cache descriptor pool",
                result);
        }

        DescriptorSetLayout* layouts =
            stackalloc DescriptorSetLayout[FramesInFlight];
        DescriptorSet* sets = stackalloc DescriptorSet[FramesInFlight];
        for (int i = 0; i < FramesInFlight; ++i)
            layouts[i] = _adaptiveReceiverDescriptorSetLayout;
        var allocationInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _adaptiveReceiverDescriptorPool,
            DescriptorSetCount = FramesInFlight,
            PSetLayouts = layouts
        };
        result = _context.Api.AllocateDescriptorSets(
            _context.Device,
            &allocationInfo,
            sets);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to allocate adaptive receiver-cache descriptor sets",
                result);
        }
        for (int i = 0; i < FramesInFlight; ++i)
        {
            _adaptiveReceiverDescriptorSets[i] = sets[i];
            _context.SetDebugName(
                sets[i].Handle,
                ObjectType.DescriptorSet,
                $"Simple DDGI Adaptive Receiver Cache Descriptor Set {i}");
        }
    }

    private void CreateSimpleDdgiReceiverCacheAdaptivePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout,
            _adaptiveReceiverDescriptorSetLayout
        };
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = (uint)Marshal.SizeOf<
                GPUSimpleDdgiReceiverCachePushConstants>()
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &info,
            null,
            out _adaptiveReceiverPipelineLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create adaptive receiver-cache pipeline layout",
                result);
        }
        _context.SetDebugName(
            _adaptiveReceiverPipelineLayout.Handle,
            ObjectType.PipelineLayout,
            "Simple DDGI Adaptive Receiver Cache Pipeline Layout");
    }

    private VkPipeline CreateSimpleDdgiReceiverCacheAdaptivePipeline(
        string artifact,
        string debugName)
    {
        bool usesPerformanceSpecialization =
            UsesAdaptiveReceiverPerformanceSpecialization(artifact);
        uint performanceSpecializationMask =
            ResolveAdaptiveReceiverPerformanceSpecializationMask(_settings);
        uint specializationData = performanceSpecializationMask;
        var specializationEntry = new SpecializationMapEntry
        {
            ConstantID = MeshPipeline
                .ForwardPerformanceSpecializationConstantId,
            Offset = 0u,
            Size = (nuint)sizeof(uint)
        };
        var specializationInfo = new SpecializationInfo
        {
            MapEntryCount = 1u,
            PMapEntries = &specializationEntry,
            DataSize = (nuint)sizeof(uint),
            PData = &specializationData
        };
        ShaderModule module = default;
        try
        {
            module = ShaderModuleLoader.Load(_context, artifact);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)_simpleDdgiReceiverCacheEntryPointName
            };
            if (usesPerformanceSpecialization)
                stage.PSpecializationInfo = &specializationInfo;
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _adaptiveReceiverPipelineLayout,
                BasePipelineIndex = -1
            };
            Result result = _giPipelineCacheService != null
                ? _giPipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId(
                        usesPerformanceSpecialization
                            ? $"{Name}:{artifact}:performance-{performanceSpecializationMask:x8}"
                            : $"{Name}:{artifact}"),
                    &info,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _simpleDdgiReceiverCachePipelineCache,
                    1,
                    &info,
                    null,
                    out pipeline);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {debugName}", result);
            _context.SetDebugName(
                pipeline.Handle,
                ObjectType.Pipeline,
                debugName);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

    internal static uint
        ResolveAdaptiveReceiverPerformanceSpecializationMask(
            RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (uint)(settings.EffectivePerformanceOptimizationFeatures &
            (PerformanceOptimizationFeature.RowMajorSpatialDdgiGather |
             PerformanceOptimizationFeature.SharedDdgiResolveStaging |
             PerformanceOptimizationFeature.DdgiPublicationGenerationReuse));
    }

    internal static bool UsesAdaptiveReceiverPerformanceSpecialization(
        string? artifact) =>
        artifact?.Equals(
            "ddgi_simple_receiver_cache_classify.comp.spv",
            StringComparison.Ordinal) == true ||
        artifact?.StartsWith(
            "ddgi_simple_receiver_cache_adaptive",
            StringComparison.Ordinal) == true ||
        artifact?.Equals(
            "ddgi_simple_receiver_cache_resolve_adaptive.comp.spv",
            StringComparison.Ordinal) == true;

    private void RecreateSimpleDdgiReceiverCacheAdaptiveResources()
    {
        if (_bufferManager is null ||
            _adaptiveReceiverDescriptorPool.Handle == 0)
        {
            return;
        }

        ulong metadataBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredMetadataBytes(
                _simpleDdgiReceiverCacheWidth,
                _simpleDdgiReceiverCacheHeight);
        ulong tileScheduleBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredTileScheduleBytes(
                _simpleDdgiReceiverCacheWidth,
                _simpleDdgiReceiverCacheHeight);
        ulong gatherWorkBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredGatherWorkBytes(
                _simpleDdgiReceiverGatherWidth,
                _simpleDdgiReceiverGatherHeight);
        ulong resolveTileBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredResolveTileBytes(
                _simpleDdgiReceiverCacheWidth,
                _simpleDdgiReceiverCacheHeight);
        ulong gatherStampBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredGatherStampBytes(
                _simpleDdgiReceiverGatherWidth,
                _simpleDdgiReceiverGatherHeight);
        ulong missingPrefixBytes =
            SimpleDdgiReceiverCacheAdaptiveAbi.RequiredMissingPrefixBytes(
                _simpleDdgiReceiverGatherWidth,
                _simpleDdgiReceiverGatherHeight);
        bool matches = _adaptiveReceiverMetadataBytes == metadataBytes &&
            _adaptiveReceiverTileScheduleBytes == tileScheduleBytes &&
            _adaptiveReceiverGatherWorkBytes == gatherWorkBytes &&
            _adaptiveReceiverResolveTileBytes == resolveTileBytes &&
            _adaptiveReceiverGatherStampBytes == gatherStampBytes &&
            _adaptiveReceiverMissingPrefixBytes == missingPrefixBytes;
        for (int i = 0; i < FramesInFlight; ++i)
        {
            matches &= _adaptiveReceiverMetadataBuffers[i].IsValid &&
                _adaptiveReceiverTileScheduleBuffers[i].IsValid &&
                _adaptiveReceiverGatherWorkBuffers[i].IsValid &&
                _adaptiveReceiverResolveTileBuffers[i].IsValid &&
                _adaptiveReceiverControlBuffers[i].IsValid &&
                _adaptiveReceiverGatherStampBuffers[i].IsValid &&
                _adaptiveReceiverMissingPrefixBuffers[i].IsValid &&
                _adaptiveReceiverReadbackBuffers[i].IsValid;
        }
        if (matches)
            return;

        var metadata = NewInvalidHandleArray();
        var tileSchedule = NewInvalidHandleArray();
        var gatherWork = NewInvalidHandleArray();
        var resolveTiles = NewInvalidHandleArray();
        var controls = NewInvalidHandleArray();
        var gatherStamps = NewInvalidHandleArray();
        var missingPrefixes = NewInvalidHandleArray();
        var readbacks = NewInvalidHandleArray();
        try
        {
            for (int i = 0; i < FramesInFlight; ++i)
            {
                metadata[i] = CreateAdaptiveDeviceBuffer(
                    metadataBytes,
                    BufferUsageFlags.StorageBufferBit,
                    $"Simple DDGI Adaptive Receiver Metadata Frame {i}");
                tileSchedule[i] = CreateAdaptiveDeviceBuffer(
                    tileScheduleBytes,
                    BufferUsageFlags.StorageBufferBit,
                    $"Simple DDGI Adaptive Receiver Tile Schedule Frame {i}");
                gatherWork[i] = CreateAdaptiveDeviceBuffer(
                    gatherWorkBytes,
                    BufferUsageFlags.StorageBufferBit,
                    $"Simple DDGI Adaptive Receiver Gather Work Frame {i}");
                resolveTiles[i] = CreateAdaptiveDeviceBuffer(
                    resolveTileBytes,
                    BufferUsageFlags.StorageBufferBit,
                    $"Simple DDGI Adaptive Receiver Resolve Tiles Frame {i}");
                controls[i] = CreateAdaptiveDeviceBuffer(
                    SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
                    BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferDstBit |
                    BufferUsageFlags.TransferSrcBit |
                    BufferUsageFlags.IndirectBufferBit,
                    $"Simple DDGI Adaptive Receiver Control Frame {i}");
                gatherStamps[i] = CreateAdaptiveDeviceBuffer(
                    gatherStampBytes,
                    BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferDstBit,
                    $"Simple DDGI Adaptive Receiver Gather Stamps Frame {i}");
                missingPrefixes[i] = CreateAdaptiveDeviceBuffer(
                    missingPrefixBytes,
                    BufferUsageFlags.StorageBufferBit,
                    $"Simple DDGI Adaptive Receiver Missing Prefix Frame {i}");
                readbacks[i] = _bufferManager.CreateBuffer(
                    SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
                    BufferUsageFlags.TransferDstBit,
                    Vma.MemoryUsage.AutoPreferHost,
                    Vma.AllocationCreateFlags.MappedBit |
                    Vma.AllocationCreateFlags.HostAccessRandomBit,
                    $"Simple DDGI Adaptive Receiver Readback Frame {i}",
                    MemoryBudgetCategory.DiagnosticsAndDebug);
            }

            if (!SimpleDdgiReceiverCacheAdaptiveAbi.CapacitiesCoverCanonicalWork(
                    _simpleDdgiReceiverCacheWidth,
                    _simpleDdgiReceiverCacheHeight,
                    _simpleDdgiReceiverGatherWidth,
                    _simpleDdgiReceiverGatherHeight,
                    gatherWorkBytes,
                    resolveTileBytes))
            {
                throw new InvalidOperationException(
                    "Adaptive receiver-cache compact-list capacity is not canonical-safe.");
            }

            PublishAdaptiveReceiverDescriptors(
                metadata,
                tileSchedule,
                gatherWork,
                resolveTiles,
                controls,
                gatherStamps,
                missingPrefixes,
                metadataBytes,
                tileScheduleBytes,
                gatherWorkBytes,
                resolveTileBytes,
                gatherStampBytes,
                missingPrefixBytes);
        }
        catch
        {
            DestroyHandleArray(metadata);
            DestroyHandleArray(tileSchedule);
            DestroyHandleArray(gatherWork);
            DestroyHandleArray(resolveTiles);
            DestroyHandleArray(controls);
            DestroyHandleArray(gatherStamps);
            DestroyHandleArray(missingPrefixes);
            DestroyHandleArray(readbacks);
            throw;
        }

        SwapAdaptiveHandleArray(_adaptiveReceiverMetadataBuffers, metadata);
        SwapAdaptiveHandleArray(
            _adaptiveReceiverTileScheduleBuffers,
            tileSchedule);
        SwapAdaptiveHandleArray(_adaptiveReceiverGatherWorkBuffers, gatherWork);
        SwapAdaptiveHandleArray(
            _adaptiveReceiverResolveTileBuffers,
            resolveTiles);
        SwapAdaptiveHandleArray(_adaptiveReceiverControlBuffers, controls);
        SwapAdaptiveHandleArray(
            _adaptiveReceiverGatherStampBuffers,
            gatherStamps);
        SwapAdaptiveHandleArray(
            _adaptiveReceiverMissingPrefixBuffers,
            missingPrefixes);
        SwapAdaptiveHandleArray(_adaptiveReceiverReadbackBuffers, readbacks);
        _adaptiveReceiverMetadataBytes = metadataBytes;
        _adaptiveReceiverTileScheduleBytes = tileScheduleBytes;
        _adaptiveReceiverGatherWorkBytes = gatherWorkBytes;
        _adaptiveReceiverResolveTileBytes = resolveTileBytes;
        _adaptiveReceiverGatherStampBytes = gatherStampBytes;
        _adaptiveReceiverMissingPrefixBytes = missingPrefixBytes;
        _adaptiveReceiverResourceGeneration =
            _adaptiveReceiverResourceGeneration == uint.MaxValue
                ? 1u
                : _adaptiveReceiverResourceGeneration + 1u;
        if (_adaptiveReceiverResourceGeneration == 0u)
            _adaptiveReceiverResourceGeneration = 1u;
        _receiverPublicationTracker.Reset();
        Array.Fill(_receiverPublicationDependentClearPending, true);
        InvalidateSimpleDdgiReceiverCacheAdaptiveHistory();
    }

    private BufferHandle CreateAdaptiveDeviceBuffer(
        ulong bytes,
        BufferUsageFlags usage,
        string debugName)
    {
        if (_bufferManager is null)
            return BufferHandle.Invalid;
        return _bufferManager.CreateDeviceBuffer(
            bytes,
            usage,
            requireDeviceAddress: false,
            MemoryBudgetCategory.GlobalIllumination,
            debugName);
    }

    private static BufferHandle[] NewInvalidHandleArray()
    {
        var handles = new BufferHandle[FramesInFlight];
        for (int i = 0; i < handles.Length; ++i)
            handles[i] = BufferHandle.Invalid;
        return handles;
    }

    private void DestroyHandleArray(BufferHandle[] handles)
    {
        if (_bufferManager is null)
            return;
        for (int i = 0; i < handles.Length; ++i)
        {
            if (handles[i].IsValid)
                _bufferManager.DestroyBuffer(handles[i]);
            handles[i] = BufferHandle.Invalid;
        }
    }

    private void SwapAdaptiveHandleArray(
        BufferHandle[] destination,
        BufferHandle[] replacement)
    {
        if (_bufferManager is null)
            return;
        for (int i = 0; i < FramesInFlight; ++i)
        {
            BufferHandle old = destination[i];
            destination[i] = replacement[i];
            replacement[i] = BufferHandle.Invalid;
            if (old.IsValid)
                _bufferManager.DestroyBuffer(old);
        }
    }

    private void PublishAdaptiveReceiverDescriptors(
        BufferHandle[] metadata,
        BufferHandle[] tileSchedule,
        BufferHandle[] gatherWork,
        BufferHandle[] resolveTiles,
        BufferHandle[] controls,
        BufferHandle[] gatherStamps,
        BufferHandle[] missingPrefixes,
        ulong metadataBytes,
        ulong tileScheduleBytes,
        ulong gatherWorkBytes,
        ulong resolveTileBytes,
        ulong gatherStampBytes,
        ulong missingPrefixBytes)
    {
        if (_bufferManager is null)
            throw new InvalidOperationException("Buffer manager is unavailable.");

        DescriptorBufferInfo* infos =
            stackalloc DescriptorBufferInfo[
                AdaptiveReceiverDescriptorBindingCount];
        WriteDescriptorSet* writes =
            stackalloc WriteDescriptorSet[
                AdaptiveReceiverDescriptorBindingCount];
        for (int frame = 0; frame < FramesInFlight; ++frame)
        {
            int previous = 1 - frame;
            if (_adaptiveReceiverDescriptorSets[frame].Handle == 0 ||
                !_simpleDdgiReceiverCacheBuffers[frame].IsValid ||
                !_simpleDdgiReceiverCacheSurfaceBuffers[frame].IsValid ||
                !_simpleDdgiReceiverCacheBuffers[previous].IsValid ||
                !_simpleDdgiReceiverCacheSurfaceBuffers[previous].IsValid)
            {
                throw new InvalidOperationException(
                    "Adaptive receiver descriptor publication prerequisites are invalid.");
            }

            infos[0] = DescriptorInfo(
                _simpleDdgiReceiverCacheBuffers[frame],
                _simpleDdgiReceiverCacheBufferBytes);
            infos[1] = DescriptorInfo(
                _simpleDdgiReceiverCacheSurfaceBuffers[frame],
                _simpleDdgiReceiverCacheSurfaceBufferBytes);
            infos[2] = DescriptorInfo(
                _simpleDdgiReceiverCacheBuffers[previous],
                _simpleDdgiReceiverCacheBufferBytes);
            infos[3] = DescriptorInfo(
                _simpleDdgiReceiverCacheSurfaceBuffers[previous],
                _simpleDdgiReceiverCacheSurfaceBufferBytes);
            infos[4] = DescriptorInfo(metadata[frame], metadataBytes);
            infos[5] = DescriptorInfo(metadata[previous], metadataBytes);
            infos[6] = DescriptorInfo(tileSchedule[frame], tileScheduleBytes);
            infos[7] = DescriptorInfo(gatherWork[frame], gatherWorkBytes);
            infos[8] = DescriptorInfo(resolveTiles[frame], resolveTileBytes);
            infos[9] = DescriptorInfo(
                controls[frame],
                SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes);
            infos[10] = DescriptorInfo(gatherStamps[frame], gatherStampBytes);
            infos[11] = DescriptorInfo(
                missingPrefixes[frame],
                missingPrefixBytes);

            for (uint binding = 0u;
                 binding < AdaptiveReceiverDescriptorBindingCount;
                 ++binding)
            {
                writes[binding] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _adaptiveReceiverDescriptorSets[frame],
                    DstBinding = binding,
                    DescriptorCount = 1u,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &infos[binding]
                };
            }
            _context.Api.UpdateDescriptorSets(
                _context.Device,
                AdaptiveReceiverDescriptorBindingCount,
                writes,
                0,
                null);
        }
    }

    private DescriptorBufferInfo DescriptorInfo(
        BufferHandle handle,
        ulong range)
    {
        if (_bufferManager is null || !handle.IsValid || range == 0UL)
            throw new InvalidOperationException("Invalid adaptive buffer descriptor.");
        return new DescriptorBufferInfo
        {
            Buffer = _bufferManager.GetBuffer(handle),
            Offset = 0UL,
            Range = range
        };
    }

    private void InvalidateSimpleDdgiReceiverCacheAdaptiveHistory()
    {
        Array.Clear(_adaptiveReceiverHistorySerials);
        Array.Clear(_adaptiveReceiverHistoryIdentities);
        Array.Clear(_adaptiveReceiverReadbackRecorded);
        _adaptiveReceiverFrameToken =
            SimpleDdgiReceiverCacheFrameToken.Unavailable;
        _adaptiveReceiverCounters =
            SimpleDdgiReceiverCacheAdaptiveCounters.Unavailable;
    }

    private SimpleDdgiReceiverCacheHistoryIdentity
        CaptureSimpleDdgiReceiverCacheHistoryIdentity(
            SceneRenderingData sceneData)
    {
        return new SimpleDdgiReceiverCacheHistoryIdentity(
            _simpleDdgiReceiverCacheWidth,
            _simpleDdgiReceiverCacheHeight,
            _simpleDdgiReceiverGatherWidth,
            _simpleDdgiReceiverGatherHeight,
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M11),
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M22),
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M33),
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M43),
            sceneData.CaptureCameraCutSerial,
            sceneData.SceneContentRevision,
            sceneData.GiTransportMaterialRevision,
            sceneData.SimpleDdgiVolumeResourceGeneration,
            sceneData.SimpleDdgiTransportTopologyGeneration,
            sceneData.SimpleDdgiSourceLightingGeneration,
            sceneData.SimpleDdgiAdmittedSourceCohortGeneration,
            sceneData.SimpleDdgiPublishedRadiometricGeneration,
            sceneData.SimpleDdgiReceiverPublicationGeneration,
            sceneData.SimpleDdgiTransportGeneration,
            sceneData.SimpleDdgiPublishedPropagationGeneration,
            sceneData.SimpleDdgiLivePropagationSourceGeneration,
            sceneData.DdgiEmissiveSourceRevision,
            sceneData.DdgiVfxMacroRevision,
            SimpleDdgiReceiverCacheMode.TemporalAdaptive,
            _adaptiveReceiverResourceGeneration);
    }

    private SimpleDdgiReceiverPublicationIdentity
        CaptureSimpleDdgiReceiverPublicationIdentity(
            SceneRenderingData sceneData)
    {
        return new SimpleDdgiReceiverPublicationIdentity(
            _simpleDdgiReceiverCacheWidth,
            _simpleDdgiReceiverCacheHeight,
            _simpleDdgiReceiverGatherWidth,
            _simpleDdgiReceiverGatherHeight,
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M11),
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M22),
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M33),
            BitConverter.SingleToInt32Bits(sceneData.ProjectionMatrix.M43),
            sceneData.CaptureCameraCutSerial,
            sceneData.SceneContentRevision,
            sceneData.GiTransportMaterialRevision,
            sceneData.SimpleDdgiVolumeResourceGeneration,
            sceneData.SimpleDdgiTransportTopologyGeneration,
            sceneData.SimpleDdgiSourceLightingGeneration,
            sceneData.SimpleDdgiAdmittedSourceCohortGeneration,
            sceneData.SimpleDdgiPublishedRadiometricGeneration,
            sceneData.SimpleDdgiReceiverPublicationGeneration,
            sceneData.SimpleDdgiTransportGeneration,
            sceneData.SimpleDdgiPublishedPropagationGeneration,
            sceneData.SimpleDdgiLivePropagationSourceGeneration,
            sceneData.DdgiEmissiveSourceRevision,
            sceneData.DdgiVfxMacroRevision,
            _simpleDdgiReceiverCacheRequestedMode,
            _adaptiveReceiverResourceGeneration);
    }

    private void UpdateSimpleDdgiReceiverPublication(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        bool enabled = _settings.IsPerformanceOptimizationEnabled(
            PerformanceOptimizationFeature.DdgiPublicationGenerationReuse);
        SimpleDdgiReceiverPublicationIdentity identity =
            CaptureSimpleDdgiReceiverPublicationIdentity(sceneData);
        SimpleDdgiReceiverPublicationUpdate update =
            _receiverPublicationTracker.Update(
                identity,
                enabled,
                BuildAdaptiveReceiverFrameStamp(sceneData.DdgiFrameSerial),
                forceDirty:
                    sceneData.DdgiProbesUpdated > 0 ||
                    sceneData.SimpleDdgiAtlasCleared != 0 ||
                    (sceneData.SimpleDdgiSchedulerMode ==
                        SimpleDdgiSchedulerMode.GpuResident &&
                     sceneData.SimpleDdgiQuietPeriodComplete == 0));
        _receiverPublicationStamp = update.Stamp;
        _receiverPublicationChangedRegionMask = update.ChangedRegionMask;
        _receiverPublicationGenerationEnabled = update.Enabled;
        if (update.IdentityChanged)
            InvalidateSimpleDdgiReceiverCacheAdaptiveHistory();
        if (update.ResetDependentCache)
        {
            Array.Fill(_receiverPublicationDependentClearPending, true);
        }

        ClearSimpleDdgiReceiverPublicationDependentBank(
            commandBuffer,
            frameIndex);
    }

    private void ClearSimpleDdgiReceiverPublicationDependentBank(
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        if (_bufferManager is null || frameIndex < 0 ||
            frameIndex >= FramesInFlight ||
            !_receiverPublicationDependentClearPending[frameIndex])
        {
            return;
        }

        BufferHandle gather = _simpleDdgiReceiverGatherBuffers[frameIndex];
        if (!gather.IsValid)
            return;
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(gather),
            0UL,
            _simpleDdgiReceiverGatherBufferBytes,
            0u);

        bool hasStamps =
            _adaptiveReceiverGatherStampBuffers[frameIndex].IsValid &&
            _adaptiveReceiverGatherStampBytes != 0UL;
        if (hasStamps)
        {
            _context.Api.CmdFillBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(
                    _adaptiveReceiverGatherStampBuffers[frameIndex]),
                0UL,
                _adaptiveReceiverGatherStampBytes,
                0u);
        }

        Span<BufferMemoryBarrier2> barriers =
            stackalloc BufferMemoryBarrier2[hasStamps ? 2 : 1];
        barriers[0] = CreateAdaptiveReceiverBufferBarrier(
            gather,
            _simpleDdgiReceiverGatherBufferBytes,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit);
        if (hasStamps)
        {
            barriers[1] = CreateAdaptiveReceiverBufferBarrier(
                _adaptiveReceiverGatherStampBuffers[frameIndex],
                _adaptiveReceiverGatherStampBytes,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit);
        }
        ExecuteAdaptiveReceiverBarriers(commandBuffer, barriers);
        _receiverPublicationDependentClearPending[frameIndex] = false;
    }

    private bool HasValidSimpleDdgiReceiverCacheAdaptiveHistory(
        int frameIndex,
        SceneRenderingData sceneData,
        in SimpleDdgiReceiverCacheHistoryIdentity identity)
    {
        int previous = 1 - frameIndex;
        return sceneData.MotionVectorsEnabled != 0 &&
            sceneData.SurfaceHistoryConsumers.HasFlag(
                SurfaceHistoryConsumer.SimpleDdgiReceiverCache) &&
            SimpleDdgiReceiverCacheHistoryIdentity.IsImmediatelyPrevious(
                sceneData.DdgiFrameSerial,
                _adaptiveReceiverHistorySerials[previous]) &&
            _adaptiveReceiverHistoryIdentities[previous]
                .IsHistoryCompatibleWith(identity);
    }

    private uint BuildAdaptiveReceiverFrameStamp(ulong frameSerial)
    {
        uint stamp = unchecked((uint)frameSerial);
        return stamp == 0u ? 1u : stamp;
    }

    private GPUSimpleDdgiReceiverCacheAdaptivePushConstants
        BuildAdaptiveReceiverPushConstants(
            SceneRenderingData sceneData,
            Extent2D renderExtent,
            bool historyValid,
            uint phase)
    {
        SimpleDdgiReceiverCacheRateThresholds thresholds =
            SimpleDdgiReceiverCacheRateThresholds.ForPreset(
                _settings.QualityPreset);
        return new GPUSimpleDdgiReceiverCacheAdaptivePushConstants
        {
            InverseViewProjectionMatrix =
                sceneData.InverseViewProjectionMatrix,
            CameraPositionAndPadding =
                new Vector4(
                    sceneData.CameraPosition,
                    BitConverter.UInt32BitsToSingle(
                        _receiverPublicationStamp)),
            ScreenWidth = renderExtent.Width,
            ScreenHeight = renderExtent.Height,
            CacheWidth = _simpleDdgiReceiverCacheWidth,
            CacheHeight = _simpleDdgiReceiverCacheHeight,
            GatherWidth = _simpleDdgiReceiverGatherWidth,
            GatherHeight = _simpleDdgiReceiverGatherHeight,
            MotionTextureIndex = BindlessIndex.MotionVectorTexture,
            HistoryAndPresetFlags = (historyValid ? 1u : 0u) |
                ((uint)_settings.QualityPreset << 8),
            FrameStamp = _receiverPublicationGenerationEnabled
                ? _receiverPublicationStamp
                : BuildAdaptiveReceiverFrameStamp(
                    sceneData.DdgiFrameSerial),
            SamplingPhase = unchecked((uint)sceneData.DdgiFrameSerial) & 3u,
            MaximumHistoryAge = thresholds.MaximumHistoryAge,
            ClassifyPhase = phase
        };
    }

    private void BindAdaptiveReceiverDescriptors(
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        BindBindlessStorageAndTextures(
            commandBuffer,
            _adaptiveReceiverPipelineLayout,
            PipelineBindPoint.Compute);
        DescriptorSet set = _adaptiveReceiverDescriptorSets[frameIndex];
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _adaptiveReceiverPipelineLayout,
            2,
            1,
            &set,
            0,
            null);
    }

    private void PushAdaptiveReceiverConstants<T>(
        CommandBuffer commandBuffer,
        in T constants)
        where T : unmanaged
    {
        T local = constants;
        _context.Api.CmdPushConstants(
            commandBuffer,
            _adaptiveReceiverPipelineLayout,
            ShaderStageFlags.ComputeBit,
            0u,
            (uint)Marshal.SizeOf<T>(),
            &local);
    }

    private BufferMemoryBarrier2 CreateAdaptiveReceiverBufferBarrier(
        BufferHandle handle,
        ulong size,
        PipelineStageFlags2 sourceStages,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStages,
        AccessFlags2 destinationAccess,
        ulong offset = 0UL)
    {
        if (_bufferManager is null || !handle.IsValid || size == 0UL)
            throw new InvalidOperationException(
                "Adaptive receiver barrier resource is unavailable.");
        return BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(handle),
            sourceStages,
            sourceAccess,
            destinationStages,
            destinationAccess,
            offset,
            size);
    }

    private void ExecuteAdaptiveReceiverBarriers(
        CommandBuffer commandBuffer,
        ReadOnlySpan<BufferMemoryBarrier2> barriers)
    {
        fixed (BufferMemoryBarrier2* barrierPointer = barriers)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = checked((uint)barriers.Length),
                PBufferMemoryBarriers = barrierPointer
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }
    }

    private void ExecuteAdaptiveReceiverBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteAdaptiveReceiverBarriers(commandBuffer, barriers);
    }

    private bool DispatchSimpleDdgiReceiverCacheAdaptive(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        Extent2D renderExtent,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract
            feedbackProducer,
        SimpleDdgiReceiverPipelineBank? pipelineBank)
    {
        _adaptiveReceiverExecutedForCurrentView = false;
        ObserveCompletedAdaptiveReceiverReadback(frameIndex);
        if (!IsSimpleDdgiReceiverCacheAdaptiveReady(pipelineBank) ||
            frameIndex < 0 || frameIndex >= FramesInFlight)
        {
            return false;
        }

        SimpleDdgiReceiverCacheHistoryIdentity identity =
            CaptureSimpleDdgiReceiverCacheHistoryIdentity(sceneData);
        if (!HasValidSimpleDdgiReceiverCacheAdaptiveHistory(
                frameIndex,
                sceneData,
                identity))
        {
            return false;
        }

        BufferHandle controlHandle =
            _adaptiveReceiverControlBuffers[frameIndex];
        VkBuffer control = _bufferManager!.GetBuffer(controlHandle);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            control,
            0UL,
            SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
            0u);
        ExecuteAdaptiveReceiverBarrier(
            commandBuffer,
            CreateAdaptiveReceiverBufferBarrier(
                controlHandle,
                SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit));

        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _adaptiveReceiverClassifyPipeline);
        BindAdaptiveReceiverDescriptors(commandBuffer, frameIndex);
        GPUSimpleDdgiReceiverCacheAdaptivePushConstants adaptivePush =
            BuildAdaptiveReceiverPushConstants(
                sceneData,
                renderExtent,
                historyValid: true,
                phase: 0u);
        PushAdaptiveReceiverConstants(commandBuffer, adaptivePush);
        _context.Api.CmdDispatch(
            commandBuffer,
            SimpleDdgiReceiverCacheAdaptiveAbi.TileWidth(
                _simpleDdgiReceiverCacheWidth),
            SimpleDdgiReceiverCacheAdaptiveAbi.TileHeight(
                _simpleDdgiReceiverCacheHeight),
            1u);

        Span<BufferMemoryBarrier2> classifyToCompact =
            stackalloc BufferMemoryBarrier2[2];
        classifyToCompact[0] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverTileScheduleBuffers[frameIndex],
            _adaptiveReceiverTileScheduleBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        classifyToCompact[1] = CreateAdaptiveReceiverBufferBarrier(
            controlHandle,
            SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit);
        ExecuteAdaptiveReceiverBarriers(
            commandBuffer,
            classifyToCompact);
        adaptivePush.ClassifyPhase = 1u;
        PushAdaptiveReceiverConstants(commandBuffer, adaptivePush);
        _context.Api.CmdDispatch(
            commandBuffer,
            DivideRoundUp(
                _simpleDdgiReceiverGatherWidth,
                SimpleDdgiReceiverCacheWorkgroupSize),
            DivideRoundUp(
                _simpleDdgiReceiverGatherHeight,
                SimpleDdgiReceiverCacheWorkgroupSize),
            1u);

        ExecuteAdaptiveReceiverBarrier(
            commandBuffer,
            CreateAdaptiveReceiverBufferBarrier(
                controlHandle,
                SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit));
        adaptivePush.ClassifyPhase = 2u;
        PushAdaptiveReceiverConstants(commandBuffer, adaptivePush);
        _context.Api.CmdDispatch(commandBuffer, 1u, 1u, 1u);

        Span<BufferMemoryBarrier2> finalizeToGather =
            stackalloc BufferMemoryBarrier2[2];
        finalizeToGather[0] = CreateAdaptiveReceiverBufferBarrier(
            controlHandle,
            SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.IndirectCommandReadBit);
        finalizeToGather[1] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverGatherWorkBuffers[frameIndex],
            _adaptiveReceiverGatherWorkBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        ExecuteAdaptiveReceiverBarriers(commandBuffer, finalizeToGather);

        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            feedbackProducer.IsAvailable
                ? _adaptiveReceiverFeedbackGatherPipeline
                : _adaptiveReceiverGatherPipeline);
        BindAdaptiveReceiverDescriptors(commandBuffer, frameIndex);
        var gatherPush = new GPUSimpleDdgiReceiverCachePushConstants
        {
            InverseViewProjectionMatrix =
                sceneData.InverseViewProjectionMatrix,
            CameraPositionAndPadding =
                new Vector4(
                    sceneData.CameraPosition,
                    BitConverter.UInt32BitsToSingle(
                        _receiverPublicationStamp)),
            ScreenWidth = renderExtent.Width,
            ScreenHeight = renderExtent.Height,
            CacheWidth = _simpleDdgiReceiverGatherWidth,
            CacheHeight = _simpleDdgiReceiverGatherHeight,
            ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
            DepthTextureIndex = BindlessIndex.DepthTexture,
            CacheBufferIndex = checked((uint)
                (BindlessIndex.SimpleDdgiReceiverGatherBufferBase +
                 frameIndex)),
            ReceiverScale = SimpleDdgiReceiverGatherScale,
            FeedbackControlOffsetWords = feedbackProducer.IsAvailable
                ? feedbackProducer.CandidateControlOffsetWords
                : 0u,
            FeedbackSamplePeriod = feedbackProducer.IsAvailable
                ? feedbackProducer.ScreenSamplingPeriod
                : 0u,
            FeedbackSamplePhase = feedbackProducer.IsAvailable
                ? feedbackProducer.ScreenSamplingPhase
                : 0u,
            FeedbackMaximumOwnersPerTile = feedbackProducer.IsAvailable
                ? feedbackProducer.MaximumUniqueGatherOwnersPerTile
                : 0u,
            SurfaceBufferIndex = checked((uint)
                (BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase +
                 frameIndex))
        };
        PushAdaptiveReceiverConstants(commandBuffer, gatherPush);
        _context.Api.CmdDispatchIndirect(
            commandBuffer,
            control,
            SimpleDdgiReceiverCacheAdaptiveAbi.GatherIndirectByteOffset);

        Span<BufferMemoryBarrier2> gatherToResolve =
            stackalloc BufferMemoryBarrier2[8];
        gatherToResolve[0] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheBuffers[frameIndex],
            _simpleDdgiReceiverCacheBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit);
        gatherToResolve[1] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheSurfaceBuffers[frameIndex],
            _simpleDdgiReceiverCacheSurfaceBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit);
        gatherToResolve[2] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverMetadataBuffers[frameIndex],
            _adaptiveReceiverMetadataBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit);
        gatherToResolve[3] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverTileScheduleBuffers[frameIndex],
            _adaptiveReceiverTileScheduleBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        gatherToResolve[4] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverResolveTileBuffers[frameIndex],
            _adaptiveReceiverResolveTileBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        gatherToResolve[5] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverGatherStampBuffers[frameIndex],
            _adaptiveReceiverGatherStampBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        gatherToResolve[6] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverGatherBuffers[frameIndex],
            _simpleDdgiReceiverGatherBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        gatherToResolve[7] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverGatherSurfaceBuffers[frameIndex],
            _simpleDdgiReceiverGatherSurfaceBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        ExecuteAdaptiveReceiverBarriers(commandBuffer, gatherToResolve);

        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _adaptiveReceiverResolvePipeline);
        BindAdaptiveReceiverDescriptors(commandBuffer, frameIndex);
        var resolvePush = new GPUSimpleDdgiReceiverCacheResolvePushConstants
        {
            InverseViewProjectionMatrix =
                sceneData.InverseViewProjectionMatrix,
            CameraPositionAndPadding =
                new Vector4(sceneData.CameraPosition, 0.0f),
            ScreenWidth = renderExtent.Width,
            ScreenHeight = renderExtent.Height,
            GatherWidth = _simpleDdgiReceiverGatherWidth,
            GatherHeight = _simpleDdgiReceiverGatherHeight,
            CacheWidth = _simpleDdgiReceiverCacheWidth,
            CacheHeight = _simpleDdgiReceiverCacheHeight,
            GatherBufferIndex = checked((uint)
                (BindlessIndex.SimpleDdgiReceiverGatherBufferBase +
                 frameIndex)),
            GatherSurfaceBufferIndex = checked((uint)
                (BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase +
                 frameIndex)),
            PackedScaleAndEdgeExtents =
                PackSimpleDdgiReceiverCacheResolveDimensions(renderExtent),
            DepthTextureIndex = BindlessIndex.DepthTexture,
            CurrentFrameIndex = adaptivePush.FrameStamp
        };
        PushAdaptiveReceiverConstants(commandBuffer, resolvePush);
        _context.Api.CmdDispatchIndirect(
            commandBuffer,
            control,
            SimpleDdgiReceiverCacheAdaptiveAbi.ResolveIndirectByteOffset);

        Span<BufferMemoryBarrier2> resolveToConsumers =
            stackalloc BufferMemoryBarrier2[3];
        resolveToConsumers[0] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheBuffers[frameIndex],
            _simpleDdgiReceiverCacheBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        resolveToConsumers[1] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheSurfaceBuffers[frameIndex],
            _simpleDdgiReceiverCacheSurfaceBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        resolveToConsumers[2] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverMetadataBuffers[frameIndex],
            _adaptiveReceiverMetadataBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        ExecuteAdaptiveReceiverBarriers(
            commandBuffer,
            resolveToConsumers);
        if (feedbackProducer.IsAvailable)
        {
            DispatchAdaptiveReceiverMissingFeedback(
                commandBuffer,
                frameIndex,
                feedbackProducer,
                adaptivePush,
                gatherPush);
        }
        RecordAdaptiveReceiverReadback(commandBuffer, frameIndex);

        _adaptiveReceiverHistorySerials[frameIndex] =
            sceneData.DdgiFrameSerial;
        _adaptiveReceiverHistoryIdentities[frameIndex] = identity;
        _adaptiveReceiverFrameToken = new SimpleDdgiReceiverCacheFrameToken(
            sceneData.DdgiFrameSerial,
            _adaptiveReceiverResourceGeneration,
            frameIndex,
            _simpleDdgiReceiverCacheWidth,
            _simpleDdgiReceiverCacheHeight,
            SimpleDdgiReceiverCacheAdaptiveAbi.TileWidth(
                _simpleDdgiReceiverCacheWidth),
            SimpleDdgiReceiverCacheAdaptiveAbi.TileHeight(
                _simpleDdgiReceiverCacheHeight),
            _adaptiveReceiverMetadataBuffers[frameIndex],
            _adaptiveReceiverTileScheduleBuffers[frameIndex],
            _adaptiveReceiverResolveTileBuffers[frameIndex],
            _adaptiveReceiverControlBuffers[frameIndex]);
        _adaptiveReceiverExecutedForCurrentView = true;
        return true;
    }

    private void DispatchAdaptiveReceiverMissingFeedback(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        GPUSimpleDdgiReceiverCacheAdaptivePushConstants adaptivePush,
        GPUSimpleDdgiReceiverCachePushConstants gatherPush)
    {
        uint workgroupCount =
            SimpleDdgiReceiverCacheAdaptiveAbi.DivideRoundUp(
                checked(_simpleDdgiReceiverGatherWidth *
                    _simpleDdgiReceiverGatherHeight),
                SimpleDdgiReceiverCacheAdaptiveAbi
                    .WorkgroupInvocationCount);
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _adaptiveReceiverClassifyPipeline);
        BindAdaptiveReceiverDescriptors(commandBuffer, frameIndex);

        adaptivePush.ClassifyPhase = 4u;
        PushAdaptiveReceiverConstants(commandBuffer, adaptivePush);
        _context.Api.CmdDispatch(commandBuffer, workgroupCount, 1u, 1u);
        ExecuteAdaptiveReceiverBarrier(
            commandBuffer,
            CreateAdaptiveReceiverBufferBarrier(
                _adaptiveReceiverMissingPrefixBuffers[frameIndex],
                _adaptiveReceiverMissingPrefixBytes,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit));

        adaptivePush.ClassifyPhase = 5u;
        PushAdaptiveReceiverConstants(commandBuffer, adaptivePush);
        _context.Api.CmdDispatch(commandBuffer, 1u, 1u, 1u);
        Span<BufferMemoryBarrier2> prefixToScatter =
            stackalloc BufferMemoryBarrier2[2];
        prefixToScatter[0] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverMissingPrefixBuffers[frameIndex],
            _adaptiveReceiverMissingPrefixBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        prefixToScatter[1] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverControlBuffers[frameIndex],
            SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.IndirectCommandReadBit);
        ExecuteAdaptiveReceiverBarriers(commandBuffer, prefixToScatter);

        adaptivePush.ClassifyPhase = 6u;
        PushAdaptiveReceiverConstants(commandBuffer, adaptivePush);
        _context.Api.CmdDispatch(commandBuffer, workgroupCount, 1u, 1u);

        ulong candidateOffsetBytes = checked(
            (ulong)producer.CandidateControlOffsetWords * sizeof(uint));
        ulong candidateEndBytes = checked(
            (ulong)producer.CandidateRecordOffsetWords * sizeof(uint) +
            (ulong)producer.CandidateRecordCount *
                producer.CandidateRecordStrideBytes);
        Span<BufferMemoryBarrier2> scatterToFeedback =
            stackalloc BufferMemoryBarrier2[2];
        scatterToFeedback[0] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverGatherWorkBuffers[frameIndex],
            _adaptiveReceiverGatherWorkBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        scatterToFeedback[1] = CreateAdaptiveReceiverBufferBarrier(
            producer.CandidateBuffer,
            candidateEndBytes - candidateOffsetBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            candidateOffsetBytes);
        ExecuteAdaptiveReceiverBarriers(commandBuffer, scatterToFeedback);

        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _adaptiveReceiverMissingFeedbackGatherPipeline);
        BindAdaptiveReceiverDescriptors(commandBuffer, frameIndex);
        PushAdaptiveReceiverConstants(commandBuffer, gatherPush);
        _context.Api.CmdDispatchIndirect(
            commandBuffer,
            _bufferManager!.GetBuffer(
                _adaptiveReceiverControlBuffers[frameIndex]),
            SimpleDdgiReceiverCacheAdaptiveAbi
                .MissingFeedbackIndirectByteOffset);

        Span<BufferMemoryBarrier2> feedbackToConsumers =
            stackalloc BufferMemoryBarrier2[3];
        feedbackToConsumers[0] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverGatherBuffers[frameIndex],
            _simpleDdgiReceiverGatherBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        feedbackToConsumers[1] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverGatherSurfaceBuffers[frameIndex],
            _simpleDdgiReceiverGatherSurfaceBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        feedbackToConsumers[2] = CreateAdaptiveReceiverBufferBarrier(
            producer.CandidateBuffer,
            candidateEndBytes - candidateOffsetBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            candidateOffsetBytes);
        ExecuteAdaptiveReceiverBarriers(
            commandBuffer,
            feedbackToConsumers);
    }

    private bool SeedSimpleDdgiReceiverCacheAdaptiveHistory(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        Extent2D renderExtent,
        SimpleDdgiReceiverPipelineBank? pipelineBank)
    {
        if (!IsSimpleDdgiReceiverCacheAdaptiveReady(pipelineBank) ||
            frameIndex < 0 || frameIndex >= FramesInFlight)
        {
            return false;
        }

        Span<BufferMemoryBarrier2> canonicalToSeed =
            stackalloc BufferMemoryBarrier2[2];
        canonicalToSeed[0] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheBuffers[frameIndex],
            _simpleDdgiReceiverCacheBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        canonicalToSeed[1] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheSurfaceBuffers[frameIndex],
            _simpleDdgiReceiverCacheSurfaceBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        ExecuteAdaptiveReceiverBarriers(commandBuffer, canonicalToSeed);
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _adaptiveReceiverClassifyPipeline);
        BindAdaptiveReceiverDescriptors(commandBuffer, frameIndex);
        GPUSimpleDdgiReceiverCacheAdaptivePushConstants push =
            BuildAdaptiveReceiverPushConstants(
                sceneData,
                renderExtent,
                historyValid: false,
                phase: 3u);
        PushAdaptiveReceiverConstants(commandBuffer, push);
        _context.Api.CmdDispatch(
            commandBuffer,
            SimpleDdgiReceiverCacheAdaptiveAbi.TileWidth(
                _simpleDdgiReceiverCacheWidth),
            SimpleDdgiReceiverCacheAdaptiveAbi.TileHeight(
                _simpleDdgiReceiverCacheHeight),
            1u);
        // Canonical gather populated the whole coarse lattice. Seed the
        // parallel stamp bank with the same publication transaction so the
        // next use of this frame bank can retain unchanged entries.
        push.ClassifyPhase = 7u;
        PushAdaptiveReceiverConstants(commandBuffer, push);
        _context.Api.CmdDispatch(
            commandBuffer,
            DivideRoundUp(
                _simpleDdgiReceiverGatherWidth,
                SimpleDdgiReceiverCacheWorkgroupSize),
            DivideRoundUp(
                _simpleDdgiReceiverGatherHeight,
                SimpleDdgiReceiverCacheWorkgroupSize),
            1u);
        Span<BufferMemoryBarrier2> seedToConsumers =
            stackalloc BufferMemoryBarrier2[5];
        seedToConsumers[0] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheBuffers[frameIndex],
            _simpleDdgiReceiverCacheBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        seedToConsumers[1] = CreateAdaptiveReceiverBufferBarrier(
            _simpleDdgiReceiverCacheSurfaceBuffers[frameIndex],
            _simpleDdgiReceiverCacheSurfaceBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        seedToConsumers[2] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverMetadataBuffers[frameIndex],
            _adaptiveReceiverMetadataBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        seedToConsumers[3] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverTileScheduleBuffers[frameIndex],
            _adaptiveReceiverTileScheduleBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        seedToConsumers[4] = CreateAdaptiveReceiverBufferBarrier(
            _adaptiveReceiverGatherStampBuffers[frameIndex],
            _adaptiveReceiverGatherStampBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        ExecuteAdaptiveReceiverBarriers(commandBuffer, seedToConsumers);

        _adaptiveReceiverHistorySerials[frameIndex] =
            sceneData.DdgiFrameSerial;
        _adaptiveReceiverHistoryIdentities[frameIndex] =
            CaptureSimpleDdgiReceiverCacheHistoryIdentity(sceneData);
        _adaptiveReceiverFrameToken =
            SimpleDdgiReceiverCacheFrameToken.Unavailable;
        return true;
    }

    private void RecordAdaptiveReceiverReadback(
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        if (_bufferManager is null ||
            !_adaptiveReceiverControlBuffers[frameIndex].IsValid ||
            !_adaptiveReceiverReadbackBuffers[frameIndex].IsValid)
        {
            return;
        }
        ExecuteAdaptiveReceiverBarrier(
            commandBuffer,
            CreateAdaptiveReceiverBufferBarrier(
                _adaptiveReceiverControlBuffers[frameIndex],
                SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit));
        VkBuffer source = _bufferManager.GetBuffer(
            _adaptiveReceiverControlBuffers[frameIndex]);
        VkBuffer destination = _bufferManager.GetBuffer(
            _adaptiveReceiverReadbackBuffers[frameIndex]);
        var copy = new BufferCopy
        {
            SrcOffset = 0UL,
            DstOffset = 0UL,
            Size = SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            source,
            destination,
            1,
            &copy);
        ExecuteAdaptiveReceiverBarrier(
            commandBuffer,
            CreateAdaptiveReceiverBufferBarrier(
                _adaptiveReceiverReadbackBuffers[frameIndex],
                SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit));
        _adaptiveReceiverReadbackRecorded[frameIndex] = true;
    }

    private void ObserveCompletedAdaptiveReceiverReadback(int frameIndex)
    {
        if (_bufferManager is null || frameIndex < 0 ||
            frameIndex >= FramesInFlight ||
            !_adaptiveReceiverReadbackRecorded[frameIndex] ||
            !_adaptiveReceiverReadbackBuffers[frameIndex].IsValid)
        {
            return;
        }

        _bufferManager.InvalidateBuffer(
            _adaptiveReceiverReadbackBuffers[frameIndex],
            0UL,
            SimpleDdgiReceiverCacheAdaptiveAbi.ControlBytes);
        uint* words = (uint*)_bufferManager.GetMappedPointer(
            _adaptiveReceiverReadbackBuffers[frameIndex]);
        if (words == null)
        {
            _adaptiveReceiverCounters =
                SimpleDdgiReceiverCacheAdaptiveCounters.Unavailable;
        }
        else
        {
            _adaptiveReceiverCounters =
                new SimpleDdgiReceiverCacheAdaptiveCounters(
                    1,
                    words[SimpleDdgiReceiverCacheAdaptiveAbi.GatherCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi
                        .MissingFeedbackCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi.ResolveCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi.OverflowFlagsWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi
                        .AcceptedEntryCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi
                        .RejectedEntryCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi.FullTileCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi.HalfTileCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi
                        .QuarterTileCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi.ReuseTileCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi
                        .PublicationGenerationHitCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi
                        .PublicationDirtyInvalidationCountWord],
                    words[SimpleDdgiReceiverCacheAdaptiveAbi
                        .PublicationSkippedTileCountWord]);
        }
        _adaptiveReceiverReadbackRecorded[frameIndex] = false;
    }

    private void CleanupSimpleDdgiReceiverCacheAdaptive(
        bool destroyInfrastructure)
    {
        DestroyHandleArray(_adaptiveReceiverMetadataBuffers);
        DestroyHandleArray(_adaptiveReceiverTileScheduleBuffers);
        DestroyHandleArray(_adaptiveReceiverGatherWorkBuffers);
        DestroyHandleArray(_adaptiveReceiverResolveTileBuffers);
        DestroyHandleArray(_adaptiveReceiverControlBuffers);
        DestroyHandleArray(_adaptiveReceiverGatherStampBuffers);
        DestroyHandleArray(_adaptiveReceiverMissingPrefixBuffers);
        DestroyHandleArray(_adaptiveReceiverReadbackBuffers);
        _adaptiveReceiverMetadataBytes = 0UL;
        _adaptiveReceiverTileScheduleBytes = 0UL;
        _adaptiveReceiverGatherWorkBytes = 0UL;
        _adaptiveReceiverResolveTileBytes = 0UL;
        _adaptiveReceiverGatherStampBytes = 0UL;
        _adaptiveReceiverMissingPrefixBytes = 0UL;
        _adaptiveReceiverResourceGeneration = 0u;
        _adaptiveReceiverExecutedForCurrentView = false;
        InvalidateSimpleDdgiReceiverCacheAdaptiveHistory();

        if (!destroyInfrastructure)
            return;

        DestroyAdaptiveReceiverPipeline(ref _adaptiveReceiverResolvePipeline);
        DestroyAdaptiveReceiverPipeline(
            ref _adaptiveReceiverMissingFeedbackGatherPipeline);
        DestroyAdaptiveReceiverPipeline(
            ref _adaptiveReceiverFeedbackGatherPipeline);
        DestroyAdaptiveReceiverPipeline(ref _adaptiveReceiverGatherPipeline);
        DestroyAdaptiveReceiverPipeline(ref _adaptiveReceiverClassifyPipeline);
        if (_adaptiveReceiverPipelineLayout.Handle != 0)
        {
            _context.Api.DestroyPipelineLayout(
                _context.Device,
                _adaptiveReceiverPipelineLayout,
                null);
            _adaptiveReceiverPipelineLayout = default;
        }
        if (_adaptiveReceiverDescriptorPool.Handle != 0)
        {
            _context.Api.DestroyDescriptorPool(
                _context.Device,
                _adaptiveReceiverDescriptorPool,
                null);
            _adaptiveReceiverDescriptorPool = default;
        }
        if (_adaptiveReceiverDescriptorSetLayout.Handle != 0)
        {
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device,
                _adaptiveReceiverDescriptorSetLayout,
                null);
            _adaptiveReceiverDescriptorSetLayout = default;
        }
        Array.Clear(_adaptiveReceiverDescriptorSets);
        _adaptiveReceiverInitializationAttempted = false;
        _adaptiveReceiverInitializationFailed = false;
    }

    private void DestroyAdaptiveReceiverPipeline(ref VkPipeline pipeline)
    {
        if (pipeline.Handle == 0)
            return;
        _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        pipeline = default;
    }
}
