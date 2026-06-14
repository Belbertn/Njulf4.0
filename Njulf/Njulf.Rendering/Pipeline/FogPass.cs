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
    public sealed unsafe class FogPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _outputSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _outputSet;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public FogPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("FogPass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            CreateOutputSetLayout();
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
            RecreateDescriptorSet();
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            FogSettings fog = _settings.Fog;
            bool enabled = fog.Enabled && fog.Mode != FogMode.Disabled;
            sceneData.ActiveSceneColorTextureIndex = BindlessIndex.HdrSceneColorTexture;
            sceneData.FogEnabled = enabled;
            sceneData.FogMode = enabled ? fog.Mode : FogMode.Disabled;
            sceneData.FogColorMode = fog.ColorMode;
            sceneData.FogDebugView = fog.DebugView;
            sceneData.FogDensity = fog.Density;
            sceneData.FogStartDistance = fog.StartDistance;
            sceneData.FogEndDistance = fog.EndDistance;
            sceneData.FogHeight = fog.Height;
            sceneData.FogHeightFalloff = fog.HeightFalloff;
            sceneData.FogHeightDensity = fog.HeightDensity;
            sceneData.FogMaxOpacity = fog.MaxOpacity;
            sceneData.FogDirectionalInscatteringEnabled = fog.DirectionalInscatteringEnabled ? 1 : 0;
            sceneData.FogWidth = enabled ? _renderTargets.FoggedSceneColor.Extent.Width : 0u;
            sceneData.FogHeightPixels = enabled ? _renderTargets.FoggedSceneColor.Extent.Height : 0u;
            sceneData.FogFormat = enabled ? _renderTargets.FoggedSceneColor.Format.ToString() : string.Empty;

            if (!enabled)
                return;

            _renderTargets.SceneColor.TransitionToShaderRead(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.FoggedSceneColor.TransitionToStorageWrite(cmd);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            var outputSet = _outputSet;
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                1,
                &storageSet,
                0,
                null);
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                1,
                1,
                &textureSet,
                0,
                null);
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                2,
                1,
                &outputSet,
                0,
                null);

            GPUFogPushConstants pushConstants = CreatePushConstants(sceneData);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUFogPushConstants>(),
                &pushConstants);

            Extent2D extent = _renderTargets.FoggedSceneColor.Extent;
            _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u, (extent.Height + 7u) / 8u, 1);
            _renderTargets.FoggedSceneColor.TransitionToShaderRead(cmd);
            sceneData.ActiveSceneColorTextureIndex = BindlessIndex.FoggedSceneColorTexture;
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

            DestroyDescriptorPool();

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_outputSetLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _outputSetLayout, null);
                _outputSetLayout = default;
            }

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private GPUFogPushConstants CreatePushConstants(SceneRenderingData sceneData)
        {
            FogSettings fog = _settings.Fog;
            Vector3 clearColor = new(sceneData.ClearColor.X, sceneData.ClearColor.Y, sceneData.ClearColor.Z);
            Vector3 sunDirection = sceneData.FogDirectionalInscatteringDirection.LengthSquared() > 0.000001f
                ? sceneData.FogDirectionalInscatteringDirection.Normalized()
                : new Vector3(-0.35f, -0.75f, -0.55f).Normalized();

            return new GPUFogPushConstants
            {
                InverseViewProjectionMatrix = sceneData.ViewProjectionMatrix.Invert(),
                CameraPositionAndTime = new Vector4(sceneData.CameraPosition, sceneData.Time),
                ScreenDimensions = new Vector4(
                    _renderTargets.FoggedSceneColor.Extent.Width,
                    _renderTargets.FoggedSceneColor.Extent.Height,
                    1.0f / _renderTargets.FoggedSceneColor.Extent.Width,
                    1.0f / _renderTargets.FoggedSceneColor.Extent.Height),
                FogColorAndDensity = new Vector4(fog.Color, fog.Density),
                FogHeightParams = new Vector4(fog.Height, fog.HeightFalloff, fog.HeightDensity, fog.MaxOpacity),
                FogDistanceParams = new Vector4(fog.StartDistance, fog.EndDistance, 0.0f, 0.0f),
                DirectionalInscatteringColorAndIntensity = new Vector4(
                    fog.DirectionalInscatteringColor,
                    fog.DirectionalInscatteringIntensity),
                DirectionalInscatteringDirectionAndExponent = new Vector4(
                    sunDirection,
                    fog.DirectionalInscatteringExponent),
                SkyColorAndBlend = new Vector4(clearColor, fog.ColorBlend),
                SceneColorTextureIndex = BindlessIndex.HdrSceneColorTexture,
                DepthTextureIndex = BindlessIndex.DepthTexture,
                EnvironmentTextureIndex = BindlessIndex.EnvironmentCubemapTexture,
                Mode = (uint)fog.Mode,
                ColorMode = (uint)fog.ColorMode,
                DebugView = (uint)fog.DebugView,
                DirectionalInscatteringEnabled = fog.DirectionalInscatteringEnabled ? 1u : 0u
            };
        }

        private void CreateOutputSetLayout()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding
            };

            Result result = _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _outputSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create fog output descriptor set layout", result);
            _context.SetDebugName(_outputSetLayout.Handle, ObjectType.DescriptorSetLayout, "Fog Pass Output Descriptor Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create fog pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Fog Pass Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts =
            [
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout,
                _outputSetLayout
            ];

            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUFogPushConstants>()
                };

                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)_setLayouts.Length,
                    PSetLayouts = setLayouts,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create fog pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Fog Pass Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "fog.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "fog.comp.spv");

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
                    out VkPipeline pipeline);

                if (result != Result.Success)
                    throw new VulkanException("Failed to create fog pass pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "Fog Pass Pipeline");
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void RecreateDescriptorSet()
        {
            DestroyDescriptorPool();

            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = 1
            };

            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 1
            };

            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create fog descriptor pool", result);

            var layout = _outputSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };

            DescriptorSet outputSet = default;
            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, &outputSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate fog descriptor set", result);
            _outputSet = outputSet;

            var outputInfo = new DescriptorImageInfo
            {
                ImageView = _renderTargets.FoggedSceneColor.View,
                ImageLayout = ImageLayout.General
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _outputSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &outputInfo
            };

            _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        }

        private void DestroyDescriptorPool()
        {
            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
                _outputSet = default;
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
