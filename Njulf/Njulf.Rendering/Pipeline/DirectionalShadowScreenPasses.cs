using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline;

public sealed unsafe class DirectionalShadowTemporalPass : RenderPassBase
{
    private const uint WorkgroupSize = 8u;
    private readonly RenderTargetManager _renderTargets;
    private readonly DirectionalShadowHistoryResources _resources;
    private readonly ShadowSettings _settings;
    private readonly BufferManager _bufferManager;
    private readonly GiPipelineCacheService? _cacheService;
    private DirectionalShadowComputePipeline? _csmResolve;
    private DirectionalShadowComputePipeline? _temporal;

    public DirectionalShadowTemporalPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        DirectionalShadowHistoryResources resources,
        ShadowSettings settings,
        BufferManager bufferManager,
        GiPipelineCacheService? cacheService = null)
        : base("DirectionalShadowTemporalPass", context, swapchain, bindlessHeap)
    {
        _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _cacheService = cacheService;
    }

    public override bool SupportsSecondaryCommandBuffer => true;

    public override void Initialize()
    {
        _csmResolve = new DirectionalShadowComputePipeline(
            _context,
            _bindlessHeap,
            "directional_csm_resolve.comp.spv",
            (uint)Marshal.SizeOf<GPUDirectionalRayShadowPushConstants>(),
            _cacheService);
        _temporal = new DirectionalShadowComputePipeline(
            _context,
            _bindlessHeap,
            "directional_shadow_temporal.comp.spv",
            (uint)Marshal.SizeOf<GPUDirectionalShadowTemporalPushConstants>(),
            _cacheService);
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        bool execute = sceneData.DirectionalShadowFramePlan.UsesScreenHistory &&
            _resources.IsAllocated;
        sceneData.DirectionalShadowTemporalPassEnabled = execute;
        return execute;
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!ShouldExecute(frameIndex, sceneData))
            return;
        if (_temporal == null || _csmResolve == null)
            throw new InvalidOperationException("Directional shadow temporal pipelines are unavailable.");

        _renderTargets.SceneDepth.TransitionToDepthReadOnly(commandBuffer);
        if (sceneData.DirectionalShadowFramePlan.UsesCsmTemporal)
        {
            if (sceneData.DirectionalShadowRayCountersEnabled)
                ResetCounters(commandBuffer, frameIndex);
            ResolveCsm(commandBuffer, frameIndex, sceneData);
        }

        DirectionalShadowHistoryRevision revision =
            DirectionalShadowHistoryRevision.Capture(
                sceneData,
                _resources,
                _settings.MaxShadowDistance);
        DirectionalShadowHistoryResetReason resetReasons =
            _resources.ResolveHistoryResetReasons(
                revision,
                sceneData.MotionVectorsEnabled != 0);
        sceneData.DirectionalShadowFramePlan =
            sceneData.DirectionalShadowFramePlan with
            {
                HistoryResetReason = resetReasons
            };

        int previousFrame = (frameIndex + RenderingConstants.FramesInFlight - 1) %
            RenderingConstants.FramesInFlight;
        uint currentHistory = checked((uint)
            (BindlessIndex.DirectionalShadowHistoryBufferBase + frameIndex));
        uint previousHistory = checked((uint)
            (BindlessIndex.DirectionalShadowHistoryBufferBase + previousFrame));
        var push = new GPUDirectionalShadowTemporalPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndMaximumDistance = new Vector4(
                sceneData.CameraPosition,
                _settings.MaxShadowDistance),
            ScreenWidth = _resources.Width,
            ScreenHeight = _resources.Height,
            RawInputBufferIndex = checked((uint)
                (BindlessIndex.DirectionalShadowRawBufferBase + frameIndex)),
            PreviousHistoryBufferIndex = previousHistory,
            CurrentHistoryBufferIndex = currentHistory,
            OutputBufferIndex = _resources.DetailedDiagnosticsAllocated
                ? checked((uint)(BindlessIndex.DirectionalShadowDiagnosticBufferBase + frameIndex))
                : uint.MaxValue,
            MaximumHistoryAge = sceneData.DirectionalShadowFramePlan.UsesCsmTemporal
                ? 3u
                : checked((uint)_settings.DirectionalSoftHistoryLength),
            ResetReasons = (uint)resetReasons,
            MaximumHistoryWeight = sceneData.DirectionalShadowFramePlan.UsesCsmTemporal
                ? 0.35f
                : 0.90f,
            RelativeDepthThreshold = 0.02f,
            NormalThreshold = 0.85f,
            TemporalKind = sceneData.DirectionalShadowFramePlan.UsesCsmTemporal
                ? sceneData.DirectionalShadowRayCountersEnabled ? 3f : 1f
                : sceneData.DirectionalShadowRayCountersEnabled ? 2f : 0f
        };
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _temporal.Pipeline);
        BindBindlessStorageAndTextures(
            commandBuffer,
            _temporal.Layout,
            PipelineBindPoint.Compute);
        _context.Api.CmdPushConstants(
            commandBuffer,
            _temporal.Layout,
            ShaderStageFlags.ComputeBit,
            0u,
            (uint)Marshal.SizeOf<GPUDirectionalShadowTemporalPushConstants>(),
            &push);
        _context.Api.CmdDispatch(
            commandBuffer,
            (_resources.Width + WorkgroupSize - 1u) / WorkgroupSize,
            (_resources.Height + WorkgroupSize - 1u) / WorkgroupSize,
            1u);

        VkBuffer history = Buffer(currentHistory);
        Barrier(
            commandBuffer,
            history,
            _resources.HistoryBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        PublishHistoryState(sceneData, revision);
        if (sceneData.DirectionalShadowRayCountersEnabled)
            _resources.MarkCountersSubmitted(frameIndex);
    }

    private void ResetCounters(CommandBuffer commandBuffer, int frameIndex)
    {
        BufferHandle handle = _resources.GetCounters(frameIndex);
        if (!handle.IsValid)
            throw new InvalidOperationException(
                "Directional shadow counters are unavailable.");
        VkBuffer buffer = _bufferManager.GetBuffer(handle);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            DirectionalShadowHistoryResources.CounterBytes);
        ExecuteBarriers(commandBuffer, barriers);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            buffer,
            0UL,
            DirectionalShadowHistoryResources.CounterBytes,
            0u);
        barriers[0] = BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            0UL,
            DirectionalShadowHistoryResources.CounterBytes);
        ExecuteBarriers(commandBuffer, barriers);
    }

    private void ExecuteBarriers(
        CommandBuffer commandBuffer,
        ReadOnlySpan<BufferMemoryBarrier2> barriers)
    {
        fixed (BufferMemoryBarrier2* pBarriers = barriers)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)barriers.Length,
                PBufferMemoryBarriers = pBarriers
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }
    }

    private void ResolveCsm(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        Vector3 cameraForward = new(
            -sceneData.InverseViewMatrix.M31,
            -sceneData.InverseViewMatrix.M32,
            -sceneData.InverseViewMatrix.M33);
        cameraForward = cameraForward.LengthSquared() > 1.0e-8f
            ? cameraForward.Normalized()
            : Vector3.Forward;
        var push = new GPUDirectionalRayShadowPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndReceiverDistance = new Vector4(
                sceneData.CameraPosition,
                _settings.MaxShadowDistance),
            RayDirectionAndMaximumDistance = new Vector4(
                cameraForward,
                _settings.MaxShadowDistance),
            ScreenWidth = _resources.Width,
            ScreenHeight = _resources.Height,
            OutputBufferIndex = checked((uint)
                (BindlessIndex.DirectionalShadowRawBufferBase + frameIndex)),
            TemporalSampleIndex = sceneData.TemporalSampleIndex
        };
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _csmResolve!.Pipeline);
        BindBindlessStorageAndTextures(
            commandBuffer,
            _csmResolve.Layout,
            PipelineBindPoint.Compute);
        _context.Api.CmdPushConstants(
            commandBuffer,
            _csmResolve.Layout,
            ShaderStageFlags.ComputeBit,
            0u,
            (uint)Marshal.SizeOf<GPUDirectionalRayShadowPushConstants>(),
            &push);
        _context.Api.CmdDispatch(
            commandBuffer,
            (_resources.Width + WorkgroupSize - 1u) / WorkgroupSize,
            (_resources.Height + WorkgroupSize - 1u) / WorkgroupSize,
            1u);
        Barrier(
            commandBuffer,
            Buffer(push.OutputBufferIndex),
            _resources.RawBufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
    }

    private void PublishHistoryState(
        SceneRenderingData sceneData,
        in DirectionalShadowHistoryRevision revision)
    {
        DirectionalShadowFramePlan plan = sceneData.DirectionalShadowFramePlan;
        _resources.CommitHistoryRevision(revision);
        sceneData.DirectionalShadowHistoryValid = 1;
        sceneData.DirectionalShadowHistoryResetReason = plan.HistoryResetReason;
    }

    private VkBuffer Buffer(uint bindlessIndex)
    {
        int frame = checked((int)(bindlessIndex -
            (bindlessIndex >= BindlessIndex.DirectionalShadowHistoryBufferBase &&
             bindlessIndex <= BindlessIndex.DirectionalShadowHistoryBufferFrame1
                ? BindlessIndex.DirectionalShadowHistoryBufferBase
                : BindlessIndex.DirectionalShadowRawBufferBase)));
        BufferHandle handle = bindlessIndex >= BindlessIndex.DirectionalShadowHistoryBufferBase
            ? _resources.GetHistory(frame)
            : _resources.GetRaw(frame);
        return _bufferManager.GetBuffer(handle);
    }

    private void Barrier(
        CommandBuffer commandBuffer,
        VkBuffer buffer,
        ulong bytes,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
    {
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            buffer,
            sourceStage,
            sourceAccess,
            destinationStage,
            destinationAccess,
            0UL,
            bytes);
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void OnSwapchainRecreated() =>
        _resources.InvalidateHistoryState(
            DirectionalShadowHistoryResetReason.ResourceRecreated);

    public override void Cleanup()
    {
        _csmResolve?.Dispose();
        _temporal?.Dispose();
        _csmResolve = null;
        _temporal = null;
        _resources.InvalidateHistoryState();
    }
}

public sealed unsafe class DirectionalShadowSpatialPass : RenderPassBase
{
    private const uint WorkgroupSize = 8u;
    private readonly DirectionalShadowHistoryResources _resources;
    private readonly DirectionalRayShadowPass _rayPass;
    private readonly ShadowSettings _settings;
    private readonly BufferManager _bufferManager;
    private readonly GiPipelineCacheService? _cacheService;
    private DirectionalShadowComputePipeline? _pipeline;

    public DirectionalShadowSpatialPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        DirectionalShadowHistoryResources resources,
        DirectionalRayShadowPass rayPass,
        ShadowSettings settings,
        BufferManager bufferManager,
        GiPipelineCacheService? cacheService = null)
        : base("DirectionalShadowSpatialPass", context, swapchain, bindlessHeap)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _rayPass = rayPass ?? throw new ArgumentNullException(nameof(rayPass));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _cacheService = cacheService;
    }

    public override bool SupportsSecondaryCommandBuffer => true;

    public override void Initialize()
    {
        _pipeline = new DirectionalShadowComputePipeline(
            _context,
            _bindlessHeap,
            "directional_shadow_spatial.comp.spv",
            (uint)Marshal.SizeOf<GPUDirectionalShadowSpatialPushConstants>(),
            _cacheService);
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        bool execute = sceneData.DirectionalShadowFramePlan.UsesSoftHistory &&
            _resources.IsAllocated &&
            _rayPass.GetMaskBuffer(frameIndex).IsValid;
        sceneData.DirectionalShadowSpatialPassEnabled = execute;
        return execute;
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!ShouldExecute(frameIndex, sceneData))
            return;
        if (_pipeline == null)
            throw new InvalidOperationException("Directional shadow spatial pipeline is unavailable.");

        ClearPackedOutput(commandBuffer, frameIndex);
        int configuredPasses = sceneData.DirectionalShadowFramePlan.UsesCsmTemporal
            ? 0
            : _settings.DirectionalSoftSpatialPassCount;
        int dispatchCount = Math.Max(1, configuredPasses);
        uint historyIndex = checked((uint)
            (BindlessIndex.DirectionalShadowHistoryBufferBase + frameIndex));
        uint rawIndex = checked((uint)
            (BindlessIndex.DirectionalShadowRawBufferBase + frameIndex));
        uint scratchIndex = checked((uint)
            (BindlessIndex.DirectionalShadowScratchBufferBase + frameIndex));
        uint maskIndex = checked((uint)
            (BindlessIndex.DirectionalRayShadowMaskBufferBase + frameIndex));

        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipeline.Pipeline);
        BindBindlessStorageAndTextures(
            commandBuffer,
            _pipeline.Layout,
            PipelineBindPoint.Compute);
        for (int pass = 0; pass < dispatchCount; pass++)
        {
            bool final = pass + 1 == dispatchCount;
            uint input = pass switch
            {
                0 => historyIndex,
                1 => rawIndex,
                _ => scratchIndex
            };
            uint output = final
                ? maskIndex
                : pass == 0 ? rawIndex : scratchIndex;
            var push = new GPUDirectionalShadowSpatialPushConstants
            {
                InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
                CameraPositionAndMaximumDistance = new Vector4(
                    sceneData.CameraPosition,
                    _settings.MaxShadowDistance),
                ScreenWidth = _resources.Width,
                ScreenHeight = _resources.Height,
                InputBufferIndex = input,
                OutputBufferIndex = output,
                HistoryBufferIndex = historyIndex,
                StepWidth = configuredPasses == 0 ? 0u : 1u << pass,
                WritePackedVisibility = final ? 1u : 0u,
                CounterEnabled = sceneData.DirectionalShadowRayCountersEnabled
                    ? 1u
                    : 0u,
                EdgeThresholds = new Vector4(0.02f, 0.85f, 0.15f, 8f)
            };
            _context.Api.CmdPushConstants(
                commandBuffer,
                _pipeline.Layout,
                ShaderStageFlags.ComputeBit,
                0u,
                (uint)Marshal.SizeOf<GPUDirectionalShadowSpatialPushConstants>(),
                &push);
            _context.Api.CmdDispatch(
                commandBuffer,
                (_resources.Width + WorkgroupSize - 1u) / WorkgroupSize,
                (_resources.Height + WorkgroupSize - 1u) / WorkgroupSize,
                1u);
            if (!final)
            {
                BufferHandle outputHandle = pass == 0
                    ? _resources.GetRaw(frameIndex)
                    : _resources.GetScratch(frameIndex);
                Barrier(
                    commandBuffer,
                    _bufferManager.GetBuffer(outputHandle),
                    pass == 0 ? _resources.RawBufferBytes : _resources.ScratchBufferBytes,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit);
            }
        }

        Barrier(
            commandBuffer,
            _bufferManager.GetBuffer(_rayPass.GetMaskBuffer(frameIndex)),
            _rayPass.BufferBytes,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit);
    }

    private void ClearPackedOutput(CommandBuffer commandBuffer, int frameIndex)
    {
        VkBuffer mask = _bufferManager.GetBuffer(_rayPass.GetMaskBuffer(frameIndex));
        Barrier(
            commandBuffer,
            mask,
            _rayPass.BufferBytes,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            mask,
            0UL,
            _rayPass.BufferBytes,
            0u);
        Barrier(
            commandBuffer,
            mask,
            _rayPass.BufferBytes,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
    }

    private void Barrier(
        CommandBuffer commandBuffer,
        VkBuffer buffer,
        ulong bytes,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
    {
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            buffer,
            sourceStage,
            sourceAccess,
            destinationStage,
            destinationAccess,
            0UL,
            bytes);
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        _pipeline?.Dispose();
        _pipeline = null;
    }
}
