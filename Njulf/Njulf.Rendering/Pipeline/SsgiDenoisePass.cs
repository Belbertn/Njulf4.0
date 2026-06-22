using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SsgiDenoisePass : RenderPassBase
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

        public SsgiDenoisePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("SsgiDenoisePass", context, swapchain, bindlessHeap)
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

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            bool execute = gi.EffectiveUseSsgi &&
                sceneData.DepthPrePassEnabled &&
                sceneData.AnimationDebugView == AnimationDebugView.None;

            if (!execute)
            {
                _bindlessHeap.RegisterTexture(
                    BindlessIndex.GiFinalDiffuseTexture,
                    _renderTargets.GiFinalDiffuse.View,
                    _bindlessHeap.ScreenSampler,
                    ImageLayout.ShaderReadOnlyOptimal);
            }

            return execute;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            _renderTargets.SsgiRaw.TransitionToShaderRead(cmd);
            _renderTargets.SsgiFiltered.TransitionToShaderRead(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.SceneNormal.TransitionToShaderRead(cmd);
            _renderTargets.GiFinalDiffuse.TransitionToStorageWrite(cmd);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            var outputSet = _outputSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 1, 1, &textureSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 2, 1, &outputSet, 0, null);

            GPUSsgiDenoisePushConstants pushConstants = CreatePushConstants(sceneData);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSsgiDenoisePushConstants>(),
                &pushConstants);

            Extent2D extent = _renderTargets.GiFinalDiffuse.Extent;
            _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u, (extent.Height + 7u) / 8u, 1);

            _renderTargets.GiFinalDiffuse.TransitionToShaderRead(cmd);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.GiFinalDiffuseTexture,
                _renderTargets.GiFinalDiffuse.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);
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

        private GPUSsgiDenoisePushConstants CreatePushConstants(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            Extent2D source = _renderTargets.SsgiFiltered.Extent;
            Extent2D destination = _renderTargets.GiFinalDiffuse.Extent;
            return new GPUSsgiDenoisePushConstants
            {
                SourceDimensions = new Vector4(
                    source.Width,
                    source.Height,
                    1.0f / Math.Max(1u, source.Width),
                    1.0f / Math.Max(1u, source.Height)),
                DestinationDimensions = new Vector4(
                    destination.Width,
                    destination.Height,
                    1.0f / Math.Max(1u, destination.Width),
                    1.0f / Math.Max(1u, destination.Height)),
                FilterParams = new Vector4(
                    Math.Max(gi.DepthRejectionThreshold * 0.65f, 0.0005f),
                    ResolveNormalPower(gi.NormalRejectionThreshold),
                    0.75f,
                    gi.LeakClampStrength),
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                Radius = ResolveRadius(_settings.QualityPreset),
                DenoiserEnabled = gi.DenoiserEnabled ? 1u : 0u,
                DebugView = (uint)gi.DebugView
            };
        }

        private static float ResolveNormalPower(float normalThreshold)
        {
            return Math.Clamp(normalThreshold, 0.0f, 1.0f) * 24.0f + 4.0f;
        }

        private static uint ResolveRadius(RenderQualityPreset qualityPreset)
        {
            return qualityPreset switch
            {
                RenderQualityPreset.Low => 1u,
                RenderQualityPreset.Medium => 1u,
                RenderQualityPreset.Ultra => 3u,
                _ => 2u
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
                throw new VulkanException("Failed to create SSGI denoise output descriptor set layout", result);
            _context.SetDebugName(_outputSetLayout.Handle, ObjectType.DescriptorSetLayout, "SSGI Denoise Output Descriptor Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SSGI denoise pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "SSGI Denoise Pipeline Cache");
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
                    Size = (uint)Marshal.SizeOf<GPUSsgiDenoisePushConstants>()
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
                    throw new VulkanException("Failed to create SSGI denoise pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "SSGI Denoise Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "ssgi_denoise.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "ssgi_denoise.comp.spv");

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

                Result result = _context.Api.CreateComputePipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create SSGI denoise compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "SSGI Denoise Compute Pipeline");
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
                throw new VulkanException("Failed to create SSGI denoise descriptor pool", result);

            var layout = _outputSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };

            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, out _outputSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate SSGI denoise descriptor set", result);

            var outputInfo = new DescriptorImageInfo
            {
                ImageView = _renderTargets.GiFinalDiffuse.View,
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
    }
}
