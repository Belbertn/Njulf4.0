using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class AmbientOcclusionBlurPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly GiPipelineCacheService? _pipelineCacheService;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _descriptorSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _horizontalSet;
        private DescriptorSet _verticalSet;
        private DescriptorSet _resolveRawSet;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _pipelinePrepared;

        public AmbientOcclusionBlurPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : this(
                context,
                swapchain,
                bindlessHeap,
                renderTargets,
                settings,
                pipelineCacheService: null)
        {
        }

        internal AmbientOcclusionBlurPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            GiPipelineCacheService? pipelineCacheService)
            : base("AmbientOcclusionBlurPass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pipelineCacheService = pipelineCacheService;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            CreateDescriptorSetLayout();
            CreatePipelineLayout();
            RecreateDescriptorSets();
            if (RendererBuildConfiguration.PipelineStartupMode ==
                    RendererPipelineStartupMode.Exhaustive ||
                _settings.AmbientOcclusion.Enabled &&
                _settings.AmbientOcclusion.Mode == AmbientOcclusionMode.Ssao)
            {
                PreparePipeline();
            }
        }

        // The pass contains an intra-pass compute dependency followed by a
        // fragment-consumer publication barrier. Recording it on the primary
        // command buffer keeps those stage scopes contiguous on drivers that
        // otherwise expose stale columns when the publication follows an
        // executed secondary command buffer.
        public override bool SupportsSecondaryCommandBuffer => false;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "AO blur is compute-only and works on AO intermediate targets.";

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            AmbientOcclusionSettings ao = _settings.AmbientOcclusion;
            if (!sceneData.AmbientOcclusionEnabled ||
                sceneData.AmbientOcclusionMode != AmbientOcclusionMode.Ssao)
                return false;

            bool requiresFullResolutionResolve =
                _renderTargets.AmbientOcclusionRaw.Extent.Width !=
                    _renderTargets.AmbientOcclusionBlurred.Extent.Width ||
                _renderTargets.AmbientOcclusionRaw.Extent.Height !=
                    _renderTargets.AmbientOcclusionBlurred.Extent.Height;
            if (ao.BlurRadius == 0 && !requiresFullResolutionResolve)
            {
                RegisterBlurredAoTexture(_renderTargets.AmbientOcclusionRaw.View);
                return false;
            }

            RegisterBlurredAoTexture(_renderTargets.AmbientOcclusionBlurred.View);
            PreparePipeline();
            return true;
        }

        internal bool IsPrepared => _pipelinePrepared;

        internal void PreparePipeline()
        {
            if (_pipelinePrepared)
                return;
            CreatePipelineCache();
            CreatePipeline();
            _pipelinePrepared = true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            bool blurEnabled = _settings.AmbientOcclusion.BlurRadius > 0;
            if (blurEnabled)
            {
                Dispatch(cmd, _horizontalSet, _renderTargets.AmbientOcclusionRaw.Extent, new Vector2(1.0f, 0.0f), sceneData, "AmbientOcclusionBlurPass Horizontal");
                _renderTargets.AmbientOcclusionScratch.TransitionToComputeShaderRead(cmd);
            }

            Dispatch(
                cmd,
                blurEnabled ? _verticalSet : _resolveRawSet,
                _renderTargets.AmbientOcclusionBlurred.Extent,
                new Vector2(0.0f, 1.0f),
                sceneData,
                blurEnabled
                    ? "AmbientOcclusionBlurPass Vertical Resolve"
                    : "AmbientOcclusionBlurPass Resolve");
            if (!IsRecordingOnComputeQueue)
            {
                // Publish in the producer command buffer on the ordinary
                // graphics-queue path. The raw AO pass uses the same scope and
                // is stable on the immediate Forward+ consumer; postponing this
                // transition until the primary command buffer produced stale
                // column reads on affected drivers. A true async-compute run
                // deliberately leaves General here so its compiled
                // release/acquire pair owns both layout and queue visibility.
                _renderTargets.AmbientOcclusionBlurred
                    .TransitionToShaderRead(cmd);
            }
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            RecreateDescriptorSets();
        }

        public override void Cleanup()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }

            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_descriptorSetLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _descriptorSetLayout, null);
                _descriptorSetLayout = default;
            }

            if (_pipelineCacheService is null && _pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            _pipelinePrepared = false;

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private void Dispatch(CommandBuffer cmd, DescriptorSet set, Extent2D extent, Vector2 direction, SceneRenderingData sceneData, string label)
        {
            _context.BeginDebugLabel(cmd, label);
            try
            {
                _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
                var pushConstants = new GPUAmbientOcclusionBlurPushConstants
                {
                    InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                    Dimensions = new Vector2(extent.Width, extent.Height),
                    Direction = direction,
                    Radius = (uint)_settings.AmbientOcclusion.BlurRadius,
                    DepthSigma = _settings.AmbientOcclusion.DepthSigma,
                    NormalSigma = _settings.AmbientOcclusion.NormalSigma,
                    UseSceneNormals = 0
                };
                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<GPUAmbientOcclusionBlurPushConstants>(),
                    &pushConstants);
                _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u, (extent.Height + 7u) / 8u, 1);
            }
            finally
            {
                _context.EndDebugLabel(cmd);
            }
        }

        private void RegisterBlurredAoTexture(ImageView view)
        {
            _bindlessHeap.RegisterTexture(
                BindlessIndex.AmbientOcclusionBlurredTexture,
                view,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        private void CreateDescriptorSetLayout()
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 2,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = bindings
            };
            Result result = _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _descriptorSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion blur descriptor set layout", result);
            _context.SetDebugName(_descriptorSetLayout.Handle, ObjectType.DescriptorSetLayout, "Ambient Occlusion Blur Descriptor Set Layout");
        }

        private void CreatePipelineCache()
        {
            if (_pipelineCacheService != null)
            {
                _pipelineCache = _pipelineCacheService.Cache;
                return;
            }

            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion blur pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Ambient Occlusion Blur Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUAmbientOcclusionBlurPushConstants>()
            };
            var layout = _descriptorSetLayout;
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &layout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };
            Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion blur pipeline layout", result);
            _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Ambient Occlusion Blur Pipeline Layout");
        }

        private void CreatePipeline()
        {
            long pipelineStart =
                _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = PipelineObjects.ShaderModuleLoader.Load(_context, "ambient_occlusion_blur.comp.spv");
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
                Result result = _context.Api.CreateComputePipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out _pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create ambient occlusion blur compute pipeline", result);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "Ambient Occlusion Blur Compute Pipeline");
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
                _pipelineCacheService?.EndPipelineCreation(
                    "AmbientOcclusion.Blur",
                    pipelineStart);
            }
        }

        private void RecreateDescriptorSets()
        {
            if (_descriptorPool.Handle != 0)
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);

            var poolSizes = stackalloc DescriptorPoolSize[2];
            poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 6 };
            poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 3 };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
                MaxSets = 3
            };
            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion blur descriptor pool", result);

            var sets = stackalloc DescriptorSet[3];
            var layouts = stackalloc DescriptorSetLayout[3];
            layouts[0] = _descriptorSetLayout;
            layouts[1] = _descriptorSetLayout;
            layouts[2] = _descriptorSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 3,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, sets);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate ambient occlusion blur descriptor sets", result);
            _horizontalSet = sets[0];
            _verticalSet = sets[1];
            _resolveRawSet = sets[2];

            WriteSet(_horizontalSet, _renderTargets.AmbientOcclusionRaw.View, _renderTargets.AmbientOcclusionScratch.View);
            WriteSet(_verticalSet, _renderTargets.AmbientOcclusionScratch.View, _renderTargets.AmbientOcclusionBlurred.View);
            WriteSet(_resolveRawSet, _renderTargets.AmbientOcclusionRaw.View, _renderTargets.AmbientOcclusionBlurred.View);
        }

        private void WriteSet(DescriptorSet set, ImageView sourceAo, ImageView destination)
        {
            var sources = stackalloc DescriptorImageInfo[2];
            sources[0] = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.ScreenSampler,
                ImageView = sourceAo,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            sources[1] = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.ScreenSampler,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
            };
            var destinationInfo = new DescriptorImageInfo
            {
                ImageView = destination,
                ImageLayout = ImageLayout.General
            };
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 2,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = sources
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &destinationInfo
            };
            _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        }
    }
}
