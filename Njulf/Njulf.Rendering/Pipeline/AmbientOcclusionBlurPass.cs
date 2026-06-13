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
        private readonly nint _entryPointName;
        private DescriptorSetLayout _descriptorSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _horizontalSet;
        private DescriptorSet _verticalSet;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public AmbientOcclusionBlurPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("AmbientOcclusionBlurPass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            CreateDescriptorSetLayout();
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
            RecreateDescriptorSets();
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            AmbientOcclusionSettings ao = _settings.AmbientOcclusion;
            if (ao.BlurRadius == 0)
            {
                CopyRawToBlurred(cmd);
                return;
            }

            _renderTargets.AmbientOcclusionScratch.TransitionToStorageWrite(cmd);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            Dispatch(cmd, _horizontalSet, _renderTargets.AmbientOcclusionRaw.Extent, new Vector2(1.0f, 0.0f), sceneData, "AmbientOcclusionBlurPass Horizontal");
            _renderTargets.AmbientOcclusionScratch.TransitionToShaderRead(cmd);

            _renderTargets.AmbientOcclusionBlurred.TransitionToStorageWrite(cmd);
            Dispatch(cmd, _verticalSet, _renderTargets.AmbientOcclusionBlurred.Extent, new Vector2(0.0f, 1.0f), sceneData, "AmbientOcclusionBlurPass Vertical");
            _renderTargets.AmbientOcclusionBlurred.TransitionToShaderRead(cmd);
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

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

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
                    InverseProjectionMatrix = sceneData.ProjectionMatrix.Invert(),
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

        private void CopyRawToBlurred(CommandBuffer cmd)
        {
            _renderTargets.AmbientOcclusionRaw.TransitionToTransferSource(cmd);
            _renderTargets.AmbientOcclusionBlurred.TransitionToTransferDestination(cmd);

            var region = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                Extent = new Extent3D
                {
                    Width = _renderTargets.AmbientOcclusionBlurred.Extent.Width,
                    Height = _renderTargets.AmbientOcclusionBlurred.Extent.Height,
                    Depth = 1
                }
            };

            _context.Api.CmdCopyImage(
                cmd,
                _renderTargets.AmbientOcclusionRaw.Image,
                ImageLayout.TransferSrcOptimal,
                _renderTargets.AmbientOcclusionBlurred.Image,
                ImageLayout.TransferDstOptimal,
                1,
                &region);
            _renderTargets.AmbientOcclusionBlurred.TransitionToShaderRead(cmd);
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
            }
        }

        private void RecreateDescriptorSets()
        {
            if (_descriptorPool.Handle != 0)
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);

            var poolSizes = stackalloc DescriptorPoolSize[2];
            poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 4 };
            poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 2 };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
                MaxSets = 2
            };
            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion blur descriptor pool", result);

            var sets = stackalloc DescriptorSet[2];
            var layouts = stackalloc DescriptorSetLayout[2];
            layouts[0] = _descriptorSetLayout;
            layouts[1] = _descriptorSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 2,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, sets);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate ambient occlusion blur descriptor sets", result);
            _horizontalSet = sets[0];
            _verticalSet = sets[1];

            WriteSet(_horizontalSet, _renderTargets.AmbientOcclusionRaw.View, _renderTargets.AmbientOcclusionScratch.View);
            WriteSet(_verticalSet, _renderTargets.AmbientOcclusionScratch.View, _renderTargets.AmbientOcclusionBlurred.View);
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
                ImageView = _swapchain.DepthImageView,
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
