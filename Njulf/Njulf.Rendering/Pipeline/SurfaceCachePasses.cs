using System;
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

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SurfaceCachePass : RenderPassBase
    {
        private const string ShaderName = "surface_cache_update.comp.spv";
        private const string EntryPoint = "main";

        private readonly RenderSettings _settings;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly SurfaceCacheManager _surfaceCacheManager;
        private readonly nint _entryPointName;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public SurfaceCachePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            AccelerationStructureManager accelerationStructureManager,
            SurfaceCacheManager surfaceCacheManager)
            : base("SurfaceCachePass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
            _surfaceCacheManager = surfaceCacheManager ?? throw new ArgumentNullException(nameof(surfaceCacheManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "Surface cache atlas capture and lighting are compute-only and feed later DDGI ray hit shading.";

        public override void Initialize()
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            string reason = ResolveSkipReason(sceneData);
            if (reason.Length != 0)
            {
                MarkSkipped(sceneData, reason);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            SurfaceCacheFrameWork work = _surfaceCacheManager.PrepareFrame(
                gi.SurfaceCacheAtlasResolution,
                gi.SurfaceCacheTileUpdateBudget,
                gi.SurfaceCacheTexelLightBudget,
                sceneData.CurrentFrameIndex);

            sceneData.SurfaceCacheCardCount = work.CardCount;
            sceneData.SurfaceCacheAtlasResolution = work.AtlasResolution;
            sceneData.SurfaceCacheTileSize = work.TileSize;
            sceneData.SurfaceCacheTilesCaptured = work.TilesCaptured;
            sceneData.SurfaceCacheTexelsLit = work.TexelsLit;
            sceneData.SurfaceCacheOccupancyPermille = work.AtlasOccupancyPermille;
            sceneData.SurfaceCacheEvictionCount = work.EvictionCount;
            sceneData.SurfaceCacheAtlasBytes = work.AtlasBytes;

            if (work.CardCount == 0)
            {
                MarkSkipped(sceneData, "no-surface-cache-cards");
                return;
            }

            RenderTarget captureAtlas = _surfaceCacheManager.CaptureAtlas ?? throw new InvalidOperationException("Surface cache capture atlas is not initialized.");
            RenderTarget radianceAtlas = _surfaceCacheManager.RadianceAtlas ?? throw new InvalidOperationException("Surface cache radiance atlas is not initialized.");
            captureAtlas.TransitionToStorageReadWrite(cmd);
            radianceAtlas.TransitionToStorageReadWrite(cmd);

            MarkExecuted(sceneData);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            GPUSurfaceCacheConstants pushConstants = CreatePushConstants(work, sceneData.CurrentFrameIndex);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSurfaceCacheConstants>(),
                &pushConstants);

            int tileTexels = work.TileSize * work.TileSize;
            int captureGroups = work.TilesCaptured * ((tileTexels + 63) / 64);
            int lightGroups = (work.TexelsLit + 63) / 64;
            uint dispatchCount = checked((uint)Math.Max(captureGroups, lightGroups));
            if (dispatchCount > 0)
                _context.Api.CmdDispatch(cmd, dispatchCount, 1, 1);

            captureAtlas.TransitionToShaderRead(cmd);
            radianceAtlas.TransitionToShaderRead(cmd);
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

        private GPUSurfaceCacheConstants CreatePushConstants(SurfaceCacheFrameWork work, uint frameIndex)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return new GPUSurfaceCacheConstants
            {
                CardBufferIndex = checked((uint)work.CardBufferIndex),
                CardCount = checked((uint)work.CardCount),
                CaptureAtlasTextureIndex = checked((uint)work.CaptureAtlasTextureIndex),
                RadianceAtlasTextureIndex = checked((uint)work.RadianceAtlasTextureIndex),
                TileUpdateBudget = checked((uint)gi.SurfaceCacheTileUpdateBudget),
                TilesCaptured = checked((uint)work.TilesCaptured),
                TexelLightBudget = checked((uint)gi.SurfaceCacheTexelLightBudget),
                DebugFlags = gi.DebugSurfaceCacheAnalyticFallback ? 1u : 0u,
                AtlasResolution = checked((uint)work.AtlasResolution),
                TileSize = checked((uint)work.TileSize),
                FirstTileIndex = checked((uint)work.FirstTileIndex),
                FirstTexelIndex = checked((uint)work.FirstTexelIndex),
                TexelsLit = checked((uint)work.TexelsLit),
                FrameIndex = frameIndex,
                AtlasOccupancyPermille = checked((uint)work.AtlasOccupancyPermille),
                EvictionCount = checked((uint)work.EvictionCount)
            };
        }

        private void MarkExecuted(SceneRenderingData sceneData)
        {
            sceneData.SurfaceCacheExecuted = 1;
            sceneData.SurfaceCacheSkipReason = string.Empty;
        }

        private void MarkSkipped(SceneRenderingData sceneData, string reason)
        {
            sceneData.SurfaceCacheExecuted = 0;
            sceneData.SurfaceCacheSkipReason = reason;
        }

        private string ResolveSkipReason(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (_pipeline.Handle == 0)
                return "pipeline-unavailable";
            if (!gi.Enabled)
                return "global-illumination-disabled";
            if (!gi.EffectiveUseDdgi)
                return "ddgi-disabled";
            if (!_accelerationStructureManager.Active)
                return "acceleration-structure-inactive";
            if (sceneData.DdgiProbeVolumeCount <= 0)
                return "no-ddgi-volumes";
            return string.Empty;
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SurfaceCachePass pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "SurfaceCachePass Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout];
            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUSurfaceCacheConstants>()
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
                    throw new VulkanException("Failed to create SurfaceCachePass pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "SurfaceCachePass Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, ShaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, ShaderName);

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
                    throw new VulkanException("Failed to create SurfaceCachePass compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "SurfaceCachePass Compute Pipeline");
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
