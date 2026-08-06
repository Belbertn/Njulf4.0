using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Records a bounded cached-source transport/blend/publication loop. The
/// canonical SSBO is updated between sweeps, while the sampled-image mirror and
/// probe lifecycle are left to the final SimpleDdgiPublishPass. No loop emits
/// a ray query: every inner sweep reuses the source cache produced earlier in
/// the transaction.
/// </summary>
public sealed unsafe class SimpleDdgiAcceleratedSolvePass : RenderPassBase
{
    private const uint EnabledFlag = 1u << 0;
    private const uint FarFieldEnabledFlag = 1u << 1;
    private const uint FarFieldForceAllFlag = 1u << 2;
    private const uint SharedMemoryBlendEnabledFlag = 1u << 3;
    private const uint ClassificationSchedulingEnabledFlag = 1u << 4;
    private const uint ReducedBlendEnabledFlag = 1u << 5;
    private const uint CompleteRaySceneFlag = 1u << 6;
    private const uint AlphaMaskTransportEnabledFlag = 1u << 7;
    private const uint ThinSurfaceTransmissionEnabledFlag = 1u << 8;
    private const uint SolveVolumeFilterFlag = 1u << 23;
    private const uint SolveColorFilterFlag = 1u << 24;
    private const uint SolveFirstColorFlag = 1u << 25;
    private const uint SolveFinalColorFlag = 1u << 26;
    private const uint SolveColorShift = 27u;
    private const uint SweepIndexShift = 28u;

    private const string LegacyTransportShader =
        "ddgi_simple_transport_solve_legacy.comp.spv";
    private const string ValidateTransportShader =
        "ddgi_simple_transport_solve_validate.comp.spv";
    private const string PackedTransportShader =
        "ddgi_simple_transport_solve_packed.comp.spv";

    private static readonly string[] SharedShaderNames =
    [
        "ddgi_simple_blend.comp.spv",
        "ddgi_simple_transport_intermediate_publish.comp.spv"
    ];

    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private nint _entryPointName;
    private readonly VkPipeline[] _pipelines = new VkPipeline[3];
    private SimpleDdgiStoragePackingMode? _transportPipelineAttemptedMode;
    private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;

    public SimpleDdgiAcceleratedSolvePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager)
        : base("SimpleDdgiAcceleratedSolvePass", context, swapchain, bindlessHeap)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override bool SupportsSecondaryCommandBuffer => true;
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute =>
        AsyncComputePassCatalog.IsCorrectnessCertified(AsyncComputePath.SimpleDdgiUpdate);
    public override string AsyncComputeReason =>
        "Cached-source V2 sweeps serialize transport, blend, and intermediate canonical publication.";

    public override void Initialize()
    {
        try
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            for (int i = 0; i < SharedShaderNames.Length; i++)
                _pipelines[i + 1] = CreatePipeline(SharedShaderNames[i]);
            // Runtime/CLI settings are resolved after pass initialization.
            // Defer only the storage-specific transport module until the first
            // predicate evaluation; the shared blend/publication pipelines stay
            // eagerly initialized and immutable.
            _volumeManager.SetTransportAccelerationRuntimeAvailable(false);
        }
        catch (Exception)
        {
            // TailJacobi remains a valid V2 authority when the optional
            // acceleration graph cannot be created. Keep the renderer alive
            // and make the legacy transport/blend predicates eligible.
            _volumeManager.SetTransportAccelerationRuntimeAvailable(false);
            Cleanup();
        }
    }

    private static string ResolveTransportShaderName(
        SimpleDdgiStoragePackingMode mode) =>
        mode switch
        {
            SimpleDdgiStoragePackingMode.Legacy => LegacyTransportShader,
            SimpleDdgiStoragePackingMode.Validate => ValidateTransportShader,
            SimpleDdgiStoragePackingMode.Packed => PackedTransportShader,
            _ => throw new InvalidOperationException(
                "Unsupported Simple-DDGI storage packing mode.")
        };

    private bool EnsureTransportPipeline()
    {
        SimpleDdgiStoragePackingMode mode =
            _settings.GlobalIllumination.SimpleDdgiStoragePackingMode;
        if (_pipelines[0].Handle != 0 && _transportPipelineAttemptedMode == mode)
            return true;
        if (_transportPipelineAttemptedMode == mode)
            return false;

        if (_pipelines[0].Handle != 0)
        {
            _context.Api.DestroyPipeline(_context.Device, _pipelines[0], null);
            _pipelines[0] = default;
        }

        _transportPipelineAttemptedMode = mode;
        try
        {
            // Inner red/black sweeps must observe the canonical SSBO publication
            // from the preceding color. The receiver image mirror
            // is intentionally republished only after the complete transaction,
            // so these storage-specialized modules compile that path out.
            _pipelines[0] = CreatePipeline(ResolveTransportShaderName(mode));
            _volumeManager.SetTransportAccelerationRuntimeAvailable(true);
            return true;
        }
        catch (Exception)
        {
            _volumeManager.SetTransportAccelerationRuntimeAvailable(false);
            return false;
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        if (!_volumeManager.TransportV2Active ||
            !_volumeManager.TailCertificationEnabled ||
            !_volumeManager.TransportAccelerationEnabled ||
            _volumeManager.TransportTailAuditPending ||
            !gi.EffectiveUseDdgi ||
            !gi.SimpleDdgiStructuredGatherEnabled ||
            !gi.EffectiveUseRayQueryBackend ||
            !_volumeManager.GpuSchedulerFrameExecutionAvailable ||
            _volumeManager.ProbeCount <= 0 ||
            _pipelines[1].Handle == 0 ||
            _pipelines[2].Handle == 0)
        {
            return false;
        }

        if (!EnsureTransportPipeline() ||
            !_volumeManager.TransportAccelerationSolveActive)
        {
            return false;
        }

        if (_volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident)
        {
            return _volumeManager.GpuScheduler.IsReady;
        }

        return _volumeManager.ProbesToUpdate > 0 &&
            _volumeManager.CanScheduleTransportTransaction;
    }

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        if (_volumeManager.SchedulerMode != SimpleDdgiSchedulerMode.GpuResident &&
            !_volumeManager.CanExecuteTransportTransaction)
        {
            _volumeManager.AbortUpdateTransaction();
            return;
        }

        SimpleDdgiGpuSchedulerLayout? layout = _volumeManager.GpuScheduler.Layout;
        int sweepCount = Math.Clamp(_volumeManager.TransportAcceleratedSweepCount, 1, 4);
        GPUSimpleDdgiPushConstants pushConstants = CreatePushConstants(sceneData);
        uint baseFlags = pushConstants.Flags & 0x00ffffffu;
        uint solveEpoch = _volumeManager.TransportTailSolveEpoch;
        int startingColor = solveEpoch != 0u
            ? (int)(solveEpoch & 1u)
            : (int)(_volumeManager.FrameSerial & 1u);
        ReadOnlySpan<int> volumeOrder = _volumeManager.TransportSolveVolumeOrder;
        bool recordedWork = false;
        for (int volumeOrderIndex = 0;
             volumeOrderIndex < volumeOrder.Length;
             volumeOrderIndex++)
        {
            int volumeIndex = volumeOrder[volumeOrderIndex];
            if (!_volumeManager.HasTransportWorkForVolume(volumeIndex))
                continue;

            recordedWork = true;

            for (int sweep = 0; sweep < sweepCount; sweep++)
            {
                for (int colorOrdinal = 0; colorOrdinal < 2; colorOrdinal++)
                {
                    int targetColor = startingColor ^ colorOrdinal;
                    uint phaseFlags = baseFlags |
                        SolveVolumeFilterFlag |
                        SolveColorFilterFlag |
                        (colorOrdinal == 0 ? SolveFirstColorFlag : 0u) |
                        (colorOrdinal == 1 ? SolveFinalColorFlag : 0u) |
                        (checked((uint)targetColor) << (int)SolveColorShift) |
                        (checked((uint)sweep) << (int)SweepIndexShift);
                    pushConstants.Flags = phaseFlags;
                    pushConstants.PrimaryDirectionalLightIndex = checked((uint)volumeIndex);
                    DispatchTransport(cmd, pushConstants);
                    InsertStorageBarrier(cmd);
                    DispatchBlend(cmd, pushConstants);
                    InsertStorageBarrier(cmd);

                    // Keep the canonical SSBO current after each color. The
                    // final publish pass still owns sampled-image publication
                    // and probe lifecycle completion; this copy only feeds the
                    // opposite color and the next cached inner sweep.
                    DispatchIntermediatePublication(cmd, pushConstants, layout);
                    InsertStorageBarrier(cmd);
                }
            }
        }

        if (recordedWork)
        {
            sceneData.SimpleDdgiTransportCachedSweepCount = checked(
                sceneData.SimpleDdgiTransportCachedSweepCount + sweepCount);
        }

        if (_volumeManager.SchedulerMode != SimpleDdgiSchedulerMode.GpuResident)
        {
            _volumeManager.MarkTransportExecuted();
            _volumeManager.MarkBlendExecuted();
        }
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        _volumeManager.SetTransportAccelerationRuntimeAvailable(false);
        for (int i = 0; i < _pipelines.Length; i++)
        {
            if (_pipelines[i].Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _pipelines[i], null);
            _pipelines[i] = default;
        }
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipelineLayout = default;
        _pipelineCache = default;
        _transportPipelineAttemptedMode = null;
    }

    private void DispatchTransport(CommandBuffer cmd, GPUSimpleDdgiPushConstants pushConstants)
    {
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[0]);
        BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        if (_volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident)
        {
            for (int bucket = 0; bucket < SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount; bucket++)
            {
                pushConstants.SchedulerRayBucketIndex = checked((uint)bucket);
                pushConstants.DispatchQueueOffset = 0u;
                pushConstants.DispatchProbeCount = 0u;
                pushConstants.DispatchRaysPerProbe = 0u;
                PushConstants(cmd, pushConstants);
                InsertIndirectCommandReadBarrier(cmd);
                _context.Api.CmdDispatchIndirect(
                    cmd,
                    _volumeManager.GpuScheduler.GetArenaVkBuffer(),
                    _volumeManager.GpuScheduler.GetRayBucketCommandOffset(bucket));
            }
            return;
        }

        ReadOnlySpan<SimpleDdgiRayDispatchBatch> batches = _volumeManager.RayDispatchBatches;
        if (batches.IsEmpty)
        {
            pushConstants.DispatchQueueOffset = 0u;
            pushConstants.DispatchProbeCount = checked((uint)_volumeManager.ProbesToUpdate);
            pushConstants.DispatchRaysPerProbe = checked((uint)Math.Max(1, _volumeManager.RaysPerProbe));
            PushConstants(cmd, pushConstants);
            _context.Api.CmdDispatch(
                cmd,
                checked((uint)Math.Max(
                    1,
                    ((ulong)Math.Max(0, _volumeManager.ProbesToUpdate) *
                     (ulong)Math.Max(1, _volumeManager.RaysPerProbe) + 63UL) / 64UL)),
                1,
                1);
            return;
        }

        foreach (ref readonly SimpleDdgiRayDispatchBatch batch in batches)
        {
            pushConstants.DispatchQueueOffset = checked((uint)batch.QueueOffset);
            pushConstants.DispatchProbeCount = checked((uint)batch.ProbeCount);
            pushConstants.DispatchRaysPerProbe = checked((uint)batch.RaysPerProbe);
            PushConstants(cmd, pushConstants);
            ulong rayCount = checked((ulong)batch.ProbeCount * (ulong)batch.RaysPerProbe);
            _context.Api.CmdDispatch(
                cmd,
                checked((uint)Math.Max(1UL, (rayCount + 63UL) / 64UL)),
                1,
                1);
        }
    }

    private void DispatchBlend(CommandBuffer cmd, GPUSimpleDdgiPushConstants pushConstants)
    {
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[1]);
        BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        if (_volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident)
        {
            InsertIndirectCommandReadBarrier(cmd);
            _context.Api.CmdDispatchIndirect(
                cmd,
                _volumeManager.GpuScheduler.GetArenaVkBuffer(),
                _volumeManager.GpuScheduler.GetIndirectCommandOffset(
                    SimpleDdgiSchedulerDispatchSlot.Blend));
        }
        else
        {
            _context.Api.CmdDispatch(
                cmd,
                checked((uint)Math.Max(1, _volumeManager.ProbesToUpdate)),
                1,
                1);
        }
    }

    private void DispatchIntermediatePublication(
        CommandBuffer cmd,
        GPUSimpleDdgiPushConstants pushConstants,
        SimpleDdgiGpuSchedulerLayout? layout)
    {
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[2]);
        BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        uint groupCount = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
            ? checked((uint)Math.Max(1, layout?.RequestCapacity ?? 1))
            : checked((uint)Math.Max(1, _volumeManager.ProbesToUpdate));
        _context.Api.CmdDispatch(cmd, groupCount, 1, 1);
    }

    private GPUSimpleDdgiPushConstants CreatePushConstants(SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        uint flags = EnabledFlag;
        if (_volumeManager.GpuScheduler.IsReady)
            flags |= 0u;
        if (gi.FarFieldForceAll)
            flags |= FarFieldForceAllFlag;
        if (gi.SimpleDdgiSharedMemoryBlendEnabled)
            flags |= SharedMemoryBlendEnabledFlag;
        if (gi.SimpleDdgiClassificationSchedulingEnabled)
            flags |= ClassificationSchedulingEnabledFlag;
        if (gi.SimpleDdgiReducedBlendEnabled)
            flags |= ReducedBlendEnabledFlag;
        if (gi.DdgiQualityTier is DdgiQualityTier.DdgiHigh or DdgiQualityTier.DdgiUltra)
            flags |= CompleteRaySceneFlag;
        if (gi.DdgiAlphaMaskedTransportEnabled)
            flags |= AlphaMaskTransportEnabledFlag;
        if (gi.SimpleDdgiThinSurfaceTransmissionEnabled)
            flags |= ThinSurfaceTransmissionEnabledFlag;

        return new GPUSimpleDdgiPushConstants
        {
            ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
            IrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
            VisibilityAtlasBufferIndex = BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
            RayResultScratchBufferIndex = BindlessIndex.SimpleDdgiRayResultScratchBuffer,
            CurrentFrameIndex = sceneData.CurrentFrameIndex,
            LightCount = checked((uint)Math.Max(0, sceneData.LightCount)),
            DirectionalLightCount = checked((uint)Math.Max(0, sceneData.DirectionalLightCount)),
            LocalLightCount = checked((uint)Math.Max(0, sceneData.LocalLightCount)),
            MaxShadedLights = checked((uint)Math.Clamp(
                sceneData.DdgiEffectiveMaxShadedLights > 0
                    ? sceneData.DdgiEffectiveMaxShadedLights
                    : gi.DdgiMaxShadedLights,
                0,
                64)),
            EmissiveSourceCount = checked((uint)Math.Max(0, sceneData.DdgiEmissiveSourceCount)),
            FarFieldParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
            FarFieldVoxelBufferIndex = BindlessIndex.FarFieldClipmapVoxelBuffer,
            FarFieldInstanceBufferIndex = BindlessIndex.FarFieldClipmapInstanceBuffer,
            Flags = flags,
            MaterialTextureMaxCascade = gi.DdgiMaterialTextureMaxCascade < 0
                ? GlobalIlluminationSettings.MaxSimpleDdgiMaterialTextureCascade
                : checked((uint)Math.Clamp(
                    gi.DdgiMaterialTextureMaxCascade,
                    0,
                    GlobalIlluminationSettings.MaxSimpleDdgiMaterialTextureCascade - 1)),
            ProbeStateBufferIndex = BindlessIndex.SimpleDdgiProbeStateBuffer,
            ProbeUpdateQueueBufferIndex = BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
            RelocationClassificationBufferIndex = BindlessIndex.SimpleDdgiRelocationClassificationBuffer,
            TransportSourceCacheBufferIndex = BindlessIndex.SimpleDdgiTransportSourceCacheBuffer,
            TransportReadIrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
            TransportWriteIrradianceAtlasBufferIndex = checked((uint)BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer),
            PrivateVisibilityAtlasOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuSchedulerPrivateVisibilityOffsetWords
                : 0u,
            TransportGeneration = _volumeManager.TransportGeneration,
            PrimaryDirectionalLightIndex = sceneData.DdgiPrimaryDirectionalLightIndex < 0
                ? uint.MaxValue
                : checked((uint)sceneData.DdgiPrimaryDirectionalLightIndex),
            SchedulerArenaBufferIndex = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? checked((uint)BindlessIndex.SimpleDdgiSchedulerArenaBuffer)
                : uint.MaxValue,
            SchedulerRayBucketCommandsOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.RayBucketCommands.OffsetWords
                : 0u,
            SchedulerRayBucketMetadataOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.RayBucketMetadata.OffsetWords
                : 0u,
            SchedulerOutcomesOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.Outcomes.OffsetWords
                : 0u,
            SchedulerCountersOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.Counters.OffsetWords
                : 0u,
            SchedulerUpdateRecordsOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.UpdateRecords.OffsetWords
                : 0u
        };
    }

    private void PushConstants(CommandBuffer cmd, GPUSimpleDdgiPushConstants pushConstants)
    {
        _context.Api.CmdPushConstants(
            cmd,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>(),
            &pushConstants);
    }

    private void InsertStorageBarrier(CommandBuffer cmd)
    {
        var memoryBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                            AccessFlags2.ShaderStorageWriteBit
        };
        var dependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &memoryBarrier
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
    }

    private void InsertIndirectCommandReadBarrier(CommandBuffer cmd)
    {
        var memoryBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.IndirectCommandReadBit
        };
        var dependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &memoryBarrier
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
    }

    private void CreatePipelineCache()
    {
        var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device,
            &cacheInfo,
            null,
            out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException("Failed to create Simple DDGI accelerated solve pipeline cache", result);
    }

    private void CreatePipelineLayout()
    {
        _setLayouts =
        [
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout
        ];
        fixed (DescriptorSetLayout* layouts = _setLayouts)
        {
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>()
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)_setLayouts.Length,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange
            };
            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI accelerated solve pipeline layout", result);
        }
    }

    private VkPipeline CreatePipeline(string shaderName)
    {
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = ShaderModuleLoader.Load(_context, shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName = (byte*)_entryPointName
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _pipelineLayout,
                BasePipelineIndex = -1
            };
            Result result = _context.Api.CreateComputePipelines(
                _context.Device,
                _pipelineCache,
                1,
                &pipelineInfo,
                null,
                out VkPipeline pipeline);
            if (result != Result.Success)
                throw new VulkanException(
                    $"Failed to create {Name} compute pipeline '{shaderName}'",
                    result);
            return pipeline;
        }
        finally
        {
            if (shaderModule.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
        }
    }
}
