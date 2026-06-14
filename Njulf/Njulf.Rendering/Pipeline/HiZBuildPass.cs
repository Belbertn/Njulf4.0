using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class HiZBuildPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly HiZDepthPyramid _pyramid;
        private readonly RenderTargetManager _renderTargets;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _descriptorSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet[] _descriptorSets = Array.Empty<DescriptorSet>();
        private PipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private PipelineCache _pipelineCache;

        public HiZBuildPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            HiZDepthPyramid pyramid,
            RenderTargetManager renderTargets)
            : base("HiZBuildPass", context, swapchain, bindlessHeap)
        {
            _pyramid = pyramid ?? throw new ArgumentNullException(nameof(pyramid));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
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
            if (!sceneData.HiZBuildEnabled)
                return;

            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            TransitionPyramidToGeneral(cmd);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

            for (uint mip = 0; mip < _pyramid.MipLevels; mip++)
            {
                DescriptorSet set = _descriptorSets[mip];
                _context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    0,
                    1,
                    &set,
                    0,
                    null);

                Extent2D sourceExtent = mip == 0
                    ? new Extent2D { Width = sceneData.ScreenWidth, Height = sceneData.ScreenHeight }
                    : _pyramid.GetMipExtent(mip - 1);
                Extent2D destinationExtent = _pyramid.GetMipExtent(mip);

                var pushConstants = new GPUHiZBuildPushConstants
                {
                    SourceDimensions = new Vector2(sourceExtent.Width, sourceExtent.Height),
                    DestinationDimensions = new Vector2(destinationExtent.Width, destinationExtent.Height)
                };

                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<GPUHiZBuildPushConstants>(),
                    &pushConstants);

                _context.Api.CmdDispatch(
                    cmd,
                    (destinationExtent.Width + 7u) / 8u,
                    (destinationExtent.Height + 7u) / 8u,
                    1);

                TransitionMipToShaderRead(cmd, mip);
            }

            _pyramid.Layout = ImageLayout.ShaderReadOnlyOptimal;
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
            DestroyPipeline();
            DestroyDescriptorPool();

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

        private void CreateDescriptorSetLayout()
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
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

            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _descriptorSetLayout);

            if (result != Result.Success)
                throw new VulkanException("Failed to create Hi-Z descriptor set layout", result);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _pipelineCache);

            if (result != Result.Success)
                throw new VulkanException("Failed to create Hi-Z pipeline cache", result);
        }

        private void CreatePipelineLayout()
        {
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUHiZBuildPushConstants>()
            };

            var setLayout = _descriptorSetLayout;
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _pipelineLayout);

            if (result != Result.Success)
                throw new VulkanException("Failed to create Hi-Z pipeline layout", result);
        }

        private void CreatePipeline()
        {
            ShaderModule shaderModule = default;

            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "hiz_downsample.comp.spv");
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
                    Layout = _pipelineLayout,
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
                    throw new VulkanException("Failed to create Hi-Z compute pipeline", result);
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void RecreateDescriptorSets()
        {
            DestroyDescriptorPool();

            var poolSizes = stackalloc DescriptorPoolSize[2];
            poolSizes[0] = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = _pyramid.MipLevels
            };
            poolSizes[1] = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = _pyramid.MipLevels
            };

            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
                MaxSets = _pyramid.MipLevels
            };

            Result result = _context.Api.CreateDescriptorPool(
                _context.Device,
                &poolInfo,
                null,
                out _descriptorPool);

            if (result != Result.Success)
                throw new VulkanException("Failed to create Hi-Z descriptor pool", result);

            _descriptorSets = new DescriptorSet[_pyramid.MipLevels];
            var layouts = new DescriptorSetLayout[_pyramid.MipLevels];
            Array.Fill(layouts, _descriptorSetLayout);

            fixed (DescriptorSetLayout* layoutsPtr = layouts)
            fixed (DescriptorSet* descriptorSetsPtr = _descriptorSets)
            {
                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = _pyramid.MipLevels,
                    PSetLayouts = layoutsPtr
                };

                result = _context.Api.AllocateDescriptorSets(
                    _context.Device,
                    &allocInfo,
                    descriptorSetsPtr);

                if (result != Result.Success)
                    throw new VulkanException("Failed to allocate Hi-Z descriptor sets", result);
            }

            UpdateDescriptorSets();
        }

        private void UpdateDescriptorSets()
        {
            for (uint mip = 0; mip < _pyramid.MipLevels; mip++)
            {
                ImageView sourceView = mip == 0
                    ? _renderTargets.SceneDepth.View
                    : _pyramid.MipViews[(int)mip - 1];

                ImageLayout sourceLayout = mip == 0
                    ? ImageLayout.DepthStencilReadOnlyOptimal
                    : ImageLayout.ShaderReadOnlyOptimal;

                var sourceInfo = new DescriptorImageInfo
                {
                    Sampler = _bindlessHeap.DefaultSampler,
                    ImageView = sourceView,
                    ImageLayout = sourceLayout
                };

                var destinationInfo = new DescriptorImageInfo
                {
                    ImageView = _pyramid.MipViews[(int)mip],
                    ImageLayout = ImageLayout.General
                };

                var sourceWrite = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _descriptorSets[mip],
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &sourceInfo
                };
                var destinationWrite = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _descriptorSets[mip],
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageImage,
                    PImageInfo = &destinationInfo
                };

                _context.Api.UpdateDescriptorSets(_context.Device, 1, &sourceWrite, 0, null);
                _context.Api.UpdateDescriptorSets(_context.Device, 1, &destinationWrite, 0, null);
            }
        }

        private void TransitionDepthForRead(CommandBuffer cmd)
        {
            if (_swapchain.DepthImageLayout == ImageLayout.DepthStencilReadOnlyOptimal)
                return;

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageLayout oldLayout = _swapchain.DepthImageLayout;
            _swapchain.SetDepthImageLayout(ImageLayout.DepthStencilReadOnlyOptimal);

            var barrier = BarrierBuilder.CreateImageBarrier(
                _swapchain.DepthImage,
                PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                oldLayout,
                ImageLayout.DepthStencilReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private void TransitionPyramidToGeneral(CommandBuffer cmd)
        {
            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = _pyramid.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            var barrier = BarrierBuilder.CreateImageBarrier(
                _pyramid.Image,
                _pyramid.Layout == ImageLayout.Undefined
                    ? PipelineStageFlags2.None
                    : PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.ComputeShaderBit,
                _pyramid.Layout == ImageLayout.Undefined
                    ? AccessFlags2.None
                    : AccessFlags2.ShaderSampledReadBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                _pyramid.Layout,
                ImageLayout.General,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
            _pyramid.Layout = ImageLayout.General;
        }

        private void TransitionMipToShaderRead(CommandBuffer cmd, uint mip)
        {
            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = mip,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            var barrier = BarrierBuilder.CreateImageBarrier(
                _pyramid.Image,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TaskShaderBitExt,
                AccessFlags2.ShaderSampledReadBit,
                ImageLayout.General,
                ImageLayout.ShaderReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private void DestroyPipeline()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }
        }

        private void DestroyDescriptorPool()
        {
            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
            }

            _descriptorSets = Array.Empty<DescriptorSet>();
        }
    }
}
