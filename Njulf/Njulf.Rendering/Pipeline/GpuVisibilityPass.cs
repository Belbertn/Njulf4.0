using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

public sealed unsafe class GpuVisibilityPass : RenderPassBase
{
    private const string EntryPoint = "main";
    private const uint WorkgroupSize = 64;

    private readonly BufferManager _bufferManager;
    private readonly GpuVisibilityBufferSet _visibilityBuffers;
    private readonly bool _useCurrentFrameHiZ;
    private readonly nint _entryPointName;
    private PipelineLayout _layout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;
    private bool _disposed;

    public GpuVisibilityPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        BufferManager bufferManager,
        GpuVisibilityBufferSet visibilityBuffers,
        string name = "GpuVisibilityPass",
        bool useCurrentFrameHiZ = false)
        : base(name, context, swapchain, bindlessHeap)
    {
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _visibilityBuffers = visibilityBuffers ?? throw new ArgumentNullException(nameof(visibilityBuffers));
        _useCurrentFrameHiZ = useCurrentFrameHiZ;
        _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
    }

    public override void Initialize()
    {
        ValidatePushConstantRange((uint)Marshal.SizeOf<GPUVisibilityPushConstants>());
        CreatePipelineCache();
        CreatePipelineLayout();
        CreatePipeline();
    }

    public override void DeclareResources(RenderGraphResourceRegistry resources)
    {
        if (resources == null)
            throw new ArgumentNullException(nameof(resources));

        RenderGraphResourceHandle opaqueDraws = ProductionRenderGraphResources.OpaqueMeshletDrawBuffer(resources);
        RenderGraphResourceHandle solidDepthDraws = ProductionRenderGraphResources.SolidDepthMeshletDrawBuffer(resources);
        RenderGraphResourceHandle maskedDepthDraws = ProductionRenderGraphResources.MaskedDepthMeshletDrawBuffer(resources);
        RenderGraphResourceHandle transparentDraws = ProductionRenderGraphResources.TransparentMeshletDrawBuffer(resources);
        RenderGraphResourceHandle directionalShadowDraws = ProductionRenderGraphResources.DirectionalShadowMeshletDrawBuffer(resources);
        RenderGraphResourceHandle localShadowDraws = ProductionRenderGraphResources.LocalShadowMeshletDrawBuffer(resources);
        RenderGraphResourceHandle gpuSceneObjects = ProductionRenderGraphResources.GpuSceneObjectBuffer(resources);
        RenderGraphResourceHandle gpuSceneInstances = ProductionRenderGraphResources.GpuSceneInstanceBuffer(resources);
        RenderGraphResourceHandle gpuSceneTransforms = ProductionRenderGraphResources.GpuSceneTransformBuffer(resources);
        RenderGraphResourceHandle gpuSceneBounds = ProductionRenderGraphResources.GpuSceneBoundsBuffer(resources);
        RenderGraphResourceHandle gpuSceneVisibility = ProductionRenderGraphResources.GpuSceneVisibilityBuffer(resources);
        RenderGraphResourceHandle hizDepth = _useCurrentFrameHiZ
            ? ProductionRenderGraphResources.HiZDepthPyramid(resources)
            : RenderGraphResourceHandle.InvalidImage;

        RenderGraphPassDesc pass = new RenderGraphPassDesc(Name, RenderGraphQueueClass.Compute)
        {
            AsyncEligible = true,
            PreferredQueue = RenderGraphQueueClass.Compute,
            ExpectedWorkloadScore = _useCurrentFrameHiZ ? 140 : 180,
            DependencyUrgency = RenderGraphDependencyUrgency.ImmediateGraphicsConsumer,
            TimingLabel = Name,
            HasExternalSideEffect = true,
            NeverCull = true
        }.SupportsQueue(RenderGraphQueueClass.Graphics);

        if (_useCurrentFrameHiZ)
        {
            if (!string.Equals(Name, "GpuVisibilityPass", StringComparison.Ordinal))
                pass.After("GpuVisibilityPass");
            pass.After("HiZBuildPass");
        }
        else
        {
            pass.After("SkinningPass");
        }

        pass
            .Read(gpuSceneObjects, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit)
            .Read(gpuSceneInstances, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit)
            .Read(gpuSceneTransforms, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit)
            .Read(gpuSceneBounds, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit)
            .Read(gpuSceneVisibility, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit)
            .Write(opaqueDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .Write(solidDepthDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .Write(maskedDepthDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .Write(transparentDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .Write(directionalShadowDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
            .Write(localShadowDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit);

        if (_useCurrentFrameHiZ)
        {
            pass.Read(hizDepth, RenderGraphResourceAccess.SampledRead, PipelineStageFlags2.ComputeShaderBit);
        }

        resources.AddPass(pass);
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        return !_useCurrentFrameHiZ || FramePassRuntimePolicy.ShouldExecute(Name, sceneData);
    }

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        uint frame = (uint)frameIndex;
        ClearOutputBuffers(cmd, frameIndex);
        BarrierFromTransferToCompute(cmd, frameIndex);

        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        BindBindlessStorageAndTextures(cmd, _layout, PipelineBindPoint.Compute);

        var pushConstants = new GPUVisibilityPushConstants
        {
            ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
            CameraPositionAndFrameIndex = new Vector4(
                sceneData.CameraPosition.X,
                sceneData.CameraPosition.Y,
                sceneData.CameraPosition.Z,
                frame),
            ObjectCount = (uint)sceneData.ObjectDataCount,
            InstanceCount = (uint)sceneData.ObjectDataCount,
            FeatureMask = sceneData.OcclusionCullingEnabled && sceneData.HiZMipCount > 0 && (_useCurrentFrameHiZ || sceneData.FrameIndex > 0) ? 1u : 0u,
            OutputCapacity = (uint)_visibilityBuffers.DrawCapacity,
            SolidDepthCapacity = (uint)_visibilityBuffers.SolidDepthCapacity,
            MaskedDepthCapacity = (uint)_visibilityBuffers.MaskedDepthCapacity,
            OpaqueCapacity = (uint)_visibilityBuffers.OpaqueCapacity,
            TransparentCapacity = (uint)_visibilityBuffers.TransparentCapacity,
            DirectionalShadowListCapacity = (uint)_visibilityBuffers.DirectionalShadowListCapacity,
            LocalShadowListCapacity = (uint)_visibilityBuffers.LocalShadowListCapacity,
            DirectionalShadowCascadeCount = sceneData.DirectionalShadowPassEnabled ? (uint)Math.Max(0, sceneData.DirectionalShadowCascadeCount) : 0u,
            SpotShadowCount = sceneData.SpotShadowsEnabled ? (uint)Math.Max(0, sceneData.SpotShadowSelectedCount) : 0u,
            PointShadowCount = sceneData.PointShadowsEnabled ? (uint)Math.Max(0, sceneData.PointShadowSelectedCount) : 0u,
            HiZTextureIndex = BindlessIndex.HiZDepthTexture,
            HiZMipCount = sceneData.HiZMipCount,
            ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
            OcclusionBias = sceneData.OcclusionBias,
            TransparencyMode = (uint)sceneData.TransparencyMode
        };

        PushConstants(cmd, pushConstants);

        uint groups = Math.Max(1u, (pushConstants.ObjectCount + WorkgroupSize - 1u) / WorkgroupSize);
        _context.Api.CmdDispatch(cmd, groups, 1, 1);
        BarrierFromComputeToCompute(cmd, frameIndex);

        if (sceneData.TransparentPassEnabled && sceneData.TransparencyMode == TransparencyMode.SortedAlphaBlend)
        {
            RunTransparentSort(cmd, frameIndex, pushConstants);
            BarrierFromComputeToCompute(cmd, frameIndex);
        }

        pushConstants.FeatureMask |= 1u << 16;
        PushConstants(cmd, pushConstants);
        _context.Api.CmdDispatch(cmd, 1, 1, 1);
        BarrierFromComputeToConsumers(cmd, frameIndex);
        CopyCountersToReadback(cmd, frameIndex);
        sceneData.CpuGpuVisibilityRecordMicroseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        DisposePipeline();
    }

    private void ClearOutputBuffers(CommandBuffer cmd, int frameIndex)
    {
        Fill(cmd, _visibilityBuffers.GetOpaqueDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.OpaqueDrawBufferBytes);
        Fill(cmd, _visibilityBuffers.GetSolidDepthDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.SolidDepthDrawBufferBytes);
        Fill(cmd, _visibilityBuffers.GetMaskedDepthDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.MaskedDepthDrawBufferBytes);
        Fill(cmd, _visibilityBuffers.GetTransparentDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.TransparentDrawBufferBytes);
        Fill(cmd, _visibilityBuffers.GetDirectionalShadowDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.DirectionalShadowDrawBufferBytes);
        Fill(cmd, _visibilityBuffers.GetLocalShadowDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.LocalShadowDrawBufferBytes);
        Fill(cmd, _visibilityBuffers.GetCounterBuffer(frameIndex), 0u, _visibilityBuffers.CounterBufferBytes);
    }

    private void PushConstants(CommandBuffer cmd, GPUVisibilityPushConstants pushConstants)
    {
        _context.Api.CmdPushConstants(
            cmd,
            _layout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUVisibilityPushConstants>(),
            &pushConstants);
    }

    private void RunTransparentSort(CommandBuffer cmd, int frameIndex, GPUVisibilityPushConstants basePushConstants)
    {
        uint count = (uint)_visibilityBuffers.TransparentCapacity;
        if (count <= 1u)
            return;

        uint groups = Math.Max(1u, (count + WorkgroupSize - 1u) / WorkgroupSize);
        for (uint k = 2u; k <= count; k <<= 1)
        {
            for (uint j = k >> 1; j > 0u; j >>= 1)
            {
                GPUVisibilityPushConstants sortPushConstants = basePushConstants;
                sortPushConstants.FeatureMask = 2u << 16;
                sortPushConstants.ObjectCount = j;
                sortPushConstants.InstanceCount = k;
                sortPushConstants.OutputCapacity = count;
                PushConstants(cmd, sortPushConstants);
                _context.Api.CmdDispatch(cmd, groups, 1, 1);
                BarrierFromComputeToCompute(cmd, frameIndex);
            }
        }
    }

    private void Fill(CommandBuffer cmd, BufferHandle handle, uint value, ulong size)
    {
        if (!handle.IsValid)
            return;

        _context.Api.CmdFillBuffer(cmd, _bufferManager.GetBuffer(handle), 0, size, value);
    }

    private void BarrierFromTransferToCompute(CommandBuffer cmd, int frameIndex)
    {
        ExecuteBufferBarriers(
            cmd,
            frameIndex,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
    }

    private void BarrierFromComputeToConsumers(CommandBuffer cmd, int frameIndex)
    {
        ExecuteBufferBarriers(
            cmd,
            frameIndex,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit);
    }

    private void BarrierFromComputeToCompute(CommandBuffer cmd, int frameIndex)
    {
        ExecuteBufferBarriers(
            cmd,
            frameIndex,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
    }

    private void CopyCountersToReadback(CommandBuffer cmd, int frameIndex)
    {
        BufferHandle source = _visibilityBuffers.GetCounterBuffer(frameIndex);
        BufferHandle destination = _visibilityBuffers.GetCounterReadbackBuffer(frameIndex);
        if (!source.IsValid || !destination.IsValid)
            return;

        var region = new BufferCopy
        {
            SrcOffset = 0,
            DstOffset = 0,
            Size = _visibilityBuffers.CounterBufferBytes
        };
        _context.Api.CmdCopyBuffer(cmd, _bufferManager.GetBuffer(source), _bufferManager.GetBuffer(destination), 1, &region);
    }

    private void ExecuteBufferBarriers(
        CommandBuffer cmd,
        int frameIndex,
        PipelineStageFlags2 srcStage,
        AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess)
    {
        BufferMemoryBarrier2[] barriers =
        [
            Barrier(_visibilityBuffers.GetOpaqueDrawBuffer(frameIndex), srcStage, srcAccess, dstStage, dstAccess),
            Barrier(_visibilityBuffers.GetSolidDepthDrawBuffer(frameIndex), srcStage, srcAccess, dstStage, dstAccess),
            Barrier(_visibilityBuffers.GetMaskedDepthDrawBuffer(frameIndex), srcStage, srcAccess, dstStage, dstAccess),
            Barrier(_visibilityBuffers.GetTransparentDrawBuffer(frameIndex), srcStage, srcAccess, dstStage, dstAccess),
            Barrier(_visibilityBuffers.GetDirectionalShadowDrawBuffer(frameIndex), srcStage, srcAccess, dstStage, dstAccess),
            Barrier(_visibilityBuffers.GetLocalShadowDrawBuffer(frameIndex), srcStage, srcAccess, dstStage, dstAccess),
            Barrier(_visibilityBuffers.GetCounterBuffer(frameIndex), srcStage, srcAccess, dstStage, dstAccess)
        ];

        BarrierBuilder.ExecuteBarrier(cmd, bufferBarriers: barriers);
    }

    private BufferMemoryBarrier2 Barrier(
        BufferHandle handle,
        PipelineStageFlags2 srcStage,
        AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess)
    {
        return BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(handle),
            srcStage,
            srcAccess,
            dstStage,
            dstAccess);
    }

    private void ValidatePushConstantRange(uint requiredSize)
    {
        var properties = new PhysicalDeviceProperties();
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
        if (requiredSize > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, but GPU visibility requires {requiredSize} bytes.");
        }
    }

    private void CreatePipelineCache()
    {
        var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
        Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException("Failed to create GPU visibility pipeline cache", result);
        _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "GPU Visibility Pipeline Cache");
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[2];
        setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
        setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<GPUVisibilityPushConstants>()
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };

        Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _layout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create GPU visibility pipeline layout", result);
        _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "GPU Visibility Pipeline Layout");
    }

    private void CreatePipeline()
    {
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = ShaderModuleLoader.Load(_context, "gpu_visibility.comp.spv");
            _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "gpu_visibility.comp.spv");

            var shaderStageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName = (byte*)_entryPointName
            };

            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = shaderStageInfo,
                Layout = _layout,
                BasePipelineHandle = default,
                BasePipelineIndex = -1
            };

            Result result = _context.Api.CreateComputePipelines(
                _context.Device,
                _pipelineCache,
                1,
                &pipelineInfo,
                null,
                out _pipeline);

            if (result != Result.Success)
                throw new VulkanException("Failed to create GPU visibility compute pipeline", result);
            _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "GpuVisibilityPass");
        }
        finally
        {
            if (shaderModule.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposePipeline();
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);
        base.Dispose(disposing);
    }

    private void DisposePipeline()
    {
        if (_pipeline.Handle != 0)
        {
            _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
            _pipeline = default;
        }

        if (_layout.Handle != 0)
        {
            _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
            _layout = default;
        }

        if (_pipelineCache.Handle != 0)
        {
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            _pipelineCache = default;
        }
    }
}
