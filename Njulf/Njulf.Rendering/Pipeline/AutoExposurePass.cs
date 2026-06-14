using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public sealed unsafe class AutoExposurePass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint ModeBuildHistogram = 0u;
        private const uint ModeReduceHistogram = 1u;

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly AutoExposureManager _autoExposure;
        private readonly nint _entryPointName;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private long _lastTimestamp;

        public AutoExposurePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings,
            AutoExposureManager autoExposure)
            : base("AutoExposurePass", context, swapchain, bindlessHeap)
        {
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _autoExposure = autoExposure ?? throw new ArgumentNullException(nameof(autoExposure));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override void Initialize()
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle sceneColor = ProductionRenderGraphResources.HdrSceneColor(resources);
            RenderGraphResourceHandle foggedSceneColor = ProductionRenderGraphResources.FoggedSceneColor(resources);
            RenderGraphResourceHandle histogram = ProductionRenderGraphResources.AutoExposureHistogramBuffer(resources);
            RenderGraphResourceHandle state = ProductionRenderGraphResources.AutoExposureStateBuffer(resources);

            RenderGraphPassDesc pass = new RenderGraphPassDesc(Name, RenderGraphQueueClass.Compute)
            {
                AsyncEligible = true,
                PreferredQueue = RenderGraphQueueClass.Compute,
                ExpectedWorkloadScore = 40,
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true,
                SupportsSecondaryCommandBuffer = SupportsSecondaryCommandBuffer
            }.SupportsQueue(RenderGraphQueueClass.Graphics)
                .After("FogPass")
                .Read(
                    sceneColor,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.ComputeShaderBit);

            if (_settings.Fog.Enabled && _settings.Fog.Mode != FogMode.Disabled)
            {
                pass.Read(
                    foggedSceneColor,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.ComputeShaderBit);
            }

            pass.ReadWrite(
                    histogram,
                    RenderGraphResourceAccess.StorageWrite,
                    PipelineStageFlags2.ComputeShaderBit)
                .Write(
                    state,
                    RenderGraphResourceAccess.StorageWrite,
                    PipelineStageFlags2.ComputeShaderBit,
                    usesAcrossFrames: true);

            resources.AddPass(pass);
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            AutoExposureSettings settings = _settings.AutoExposure;
            AutoExposureSnapshot completed = _autoExposure.LastCompleted;
            sceneData.AutoExposureEnabled = settings.Enabled;
            sceneData.AutoExposureAverageLuminance = completed.AverageLuminance;
            sceneData.AutoExposureTargetExposure = completed.TargetExposure;
            sceneData.AutoExposureSampleCount = checked((int)Math.Min(completed.SampleCount, (uint)int.MaxValue));
            sceneData.AutoExposureStateBufferIndex = _autoExposure.GetStateBufferIndex(frameIndex);
            sceneData.EffectiveExposure = settings.Enabled ? completed.Exposure : _settings.Exposure;

            if (!settings.Enabled)
                return;

            int activeSceneColorTextureIndex = sceneData.ActiveSceneColorTextureIndex == BindlessIndex.FoggedSceneColorTexture
                ? BindlessIndex.FoggedSceneColorTexture
                : BindlessIndex.HdrSceneColorTexture;
            RenderTarget activeSceneColor = activeSceneColorTextureIndex == BindlessIndex.FoggedSceneColorTexture
                ? _renderTargets.FoggedSceneColor
                : _renderTargets.SceneColor;

            _autoExposure.ResetHistogram(cmd, frameIndex);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
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

            GPUAutoExposurePushConstants pushConstants = CreatePushConstants(
                activeSceneColor.Extent,
                activeSceneColorTextureIndex,
                frameIndex,
                ModeBuildHistogram);
            PushConstants(cmd, pushConstants);

            uint stride = Math.Max(1u, (uint)settings.SamplingStride);
            uint sampleWidth = (activeSceneColor.Extent.Width + stride - 1u) / stride;
            uint sampleHeight = (activeSceneColor.Extent.Height + stride - 1u) / stride;
            _context.Api.CmdDispatch(cmd, (sampleWidth + 15u) / 16u, (sampleHeight + 15u) / 16u, 1);
            _autoExposure.BarrierAfterHistogram(cmd, frameIndex);

            pushConstants.Mode = ModeReduceHistogram;
            PushConstants(cmd, pushConstants);
            _context.Api.CmdDispatch(cmd, 1, 1, 1);
            _autoExposure.BarrierAfterExposureWrite(cmd, frameIndex);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void Cleanup()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private GPUAutoExposurePushConstants CreatePushConstants(
            Extent2D sourceExtent,
            int sceneColorTextureIndex,
            int frameIndex,
            uint mode)
        {
            AutoExposureSettings settings = _settings.AutoExposure;
            return new GPUAutoExposurePushConstants
            {
                SourceDimensions = new Vector2(sourceExtent.Width, sourceExtent.Height),
                SceneColorTextureIndex = (uint)sceneColorTextureIndex,
                HistogramBufferIndex = (uint)_autoExposure.GetHistogramBufferIndex(frameIndex),
                ExposureStateBufferIndex = (uint)_autoExposure.GetStateBufferIndex(frameIndex),
                MinLogLuminance = settings.MinLogLuminance,
                LogLuminanceRange = Math.Max(0.01f, settings.LogLuminanceRange),
                TargetLuminance = settings.TargetLuminance,
                PreviousExposure = _autoExposure.PreviousExposure,
                DeltaTime = GetDeltaSeconds(),
                AdaptationSpeed = settings.AdaptationSpeed,
                MinExposure = settings.MinExposure,
                MaxExposure = settings.MaxExposure,
                Mode = mode,
                SamplingStride = (uint)settings.SamplingStride,
                HistogramBinCount = AutoExposureManager.HistogramBinCount
            };
        }

        private void PushConstants(CommandBuffer cmd, GPUAutoExposurePushConstants pushConstants)
        {
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUAutoExposurePushConstants>(),
                &pushConstants);
        }

        private float GetDeltaSeconds()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastTimestamp == 0)
            {
                _lastTimestamp = now;
                return 1.0f / 60.0f;
            }

            float delta = (float)Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            _lastTimestamp = now;
            return Math.Clamp(delta, 0.0f, 0.25f);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create auto exposure pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Auto Exposure Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUAutoExposurePushConstants>()
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create auto exposure pipeline layout", result);
            _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Auto Exposure Pipeline Layout");
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "auto_exposure.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "auto_exposure.comp.spv");

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
                    throw new VulkanException("Failed to create auto exposure pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "Auto Exposure Pipeline");
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }
    }
}
