using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class AmbientOcclusionPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _descriptorSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _descriptorSet;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public AmbientOcclusionPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("AmbientOcclusionPass", context, swapchain, bindlessHeap)
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
            RecreateDescriptorSet();
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            AmbientOcclusionSettings ao = _settings.AmbientOcclusion;
            bool enabled = ao.Enabled && sceneData.DepthPrePassEnabled;
            sceneData.AmbientOcclusionEnabled = enabled;
            sceneData.AmbientOcclusionMode = enabled ? ao.Mode : AmbientOcclusionMode.Disabled;
            sceneData.AmbientOcclusionDebugView = ao.DebugView;
            sceneData.AmbientOcclusionWidth = enabled ? _renderTargets.AmbientOcclusionRaw.Extent.Width : 1u;
            sceneData.AmbientOcclusionHeight = enabled ? _renderTargets.AmbientOcclusionRaw.Extent.Height : 1u;
            sceneData.AmbientOcclusionFormat = RenderTargetManager.AmbientOcclusionFormat.ToString();
            sceneData.AmbientOcclusionResolutionScale = enabled ? ao.ResolutionScale : 0.0f;
            sceneData.AmbientOcclusionRadius = enabled ? ao.Radius : 0.0f;
            sceneData.AmbientOcclusionIntensity = enabled ? ao.Intensity : 0.0f;
            sceneData.AmbientOcclusionBias = enabled ? ao.Bias : 0.0f;
            sceneData.AmbientOcclusionSampleCount = enabled ? ao.SampleCount : 0;
            sceneData.AmbientOcclusionBlurRadius = enabled ? ao.BlurRadius : 0;
            return enabled;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            AmbientOcclusionSettings ao = _settings.AmbientOcclusion;

            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.AmbientOcclusionRaw.TransitionToStorageWrite(cmd);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            DescriptorSet descriptorSet = _descriptorSet;
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                1,
                &descriptorSet,
                0,
                null);

            var pushConstants = new GPUAmbientOcclusionPushConstants
            {
                InverseProjectionMatrix = sceneData.ProjectionMatrix.Invert(),
                ProjectionMatrix = sceneData.ProjectionMatrix,
                SourceDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                DestinationDimensions = new Vector2(_renderTargets.AmbientOcclusionRaw.Extent.Width, _renderTargets.AmbientOcclusionRaw.Extent.Height),
                Radius = ao.Radius,
                Intensity = ao.Intensity,
                Bias = ao.Bias,
                Power = ao.Power,
                SampleCount = (uint)ao.SampleCount,
                FrameIndex = (uint)frameIndex,
                UseSceneNormals = 0,
                Mode = (uint)ao.Mode
            };

            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUAmbientOcclusionPushConstants>(),
                &pushConstants);

            Extent2D extent = _renderTargets.AmbientOcclusionRaw.Extent;
            _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u, (extent.Height + 7u) / 8u, 1);
            _renderTargets.AmbientOcclusionRaw.TransitionToShaderRead(cmd);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            RecreateDescriptorSet();
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

            Result result = _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _descriptorSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion descriptor set layout", result);
            _context.SetDebugName(_descriptorSetLayout.Handle, ObjectType.DescriptorSetLayout, "Ambient Occlusion Descriptor Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Ambient Occlusion Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUAmbientOcclusionPushConstants>()
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
                throw new VulkanException("Failed to create ambient occlusion pipeline layout", result);
            _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Ambient Occlusion Pipeline Layout");
        }

        private void CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = PipelineObjects.ShaderModuleLoader.Load(_context, "ambient_occlusion.comp.spv");
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
                    throw new VulkanException("Failed to create ambient occlusion compute pipeline", result);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "Ambient Occlusion Compute Pipeline");
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void RecreateDescriptorSet()
        {
            if (_descriptorPool.Handle != 0)
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);

            var poolSizes = stackalloc DescriptorPoolSize[2];
            poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
            poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 1 };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
                MaxSets = 1
            };
            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create ambient occlusion descriptor pool", result);

            var layout = _descriptorSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };
            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, out _descriptorSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate ambient occlusion descriptor set", result);

            var depthInfo = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.ScreenSampler,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
            };
            var outputInfo = new DescriptorImageInfo
            {
                ImageView = _renderTargets.AmbientOcclusionRaw.View,
                ImageLayout = ImageLayout.General
            };
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &depthInfo
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &outputInfo
            };
            _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
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
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                oldLayout,
                ImageLayout.DepthStencilReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);
            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }
    }
}
