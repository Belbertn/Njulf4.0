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
        private const string DilateShaderName = "surface_cache_dilate.comp.spv";
        private const string EntryPoint = "main";
        private const uint WorkModeCapture = 0u;
        private const uint WorkModeLight = 1u;
        private const uint WorkModeDilateCapture = 2u;
        private const uint WorkModeDilateRadiance = 3u;

        private readonly RenderSettings _settings;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly SurfaceCacheManager _surfaceCacheManager;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _accelerationStructureSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _accelerationStructureSet;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private VkPipeline _dilatePipeline;
        private AccelerationStructureKHR _boundTlas;

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
            if (!_context.RayQuerySupported || _context.KhrAccelerationStructure == null)
                return;

            CreateAccelerationStructureSetLayout();
            CreateDescriptorSet();
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline(ShaderName, "SurfaceCachePass Compute Pipeline");
            _dilatePipeline = CreatePipeline(DilateShaderName, "SurfaceCachePass Dilation Compute Pipeline");
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
                _accelerationStructureManager.LastStaticOpaqueInstances,
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
            if (work.AtlasesRequireClear)
            {
                ClearSurfaceCacheAtlas(cmd, captureAtlas);
                ClearSurfaceCacheAtlas(cmd, radianceAtlas);
                _surfaceCacheManager.MarkAtlasesCleared();
            }

            captureAtlas.TransitionToStorageReadWrite(cmd);
            radianceAtlas.TransitionToStorageReadWrite(cmd);

            MarkExecuted(sceneData);
            UpdateAccelerationStructureDescriptor();
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
            var asSet = _accelerationStructureSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 2, 1, &asSet, 0, null);

            GPUSurfaceCacheConstants pushConstants = CreatePushConstants(work, sceneData, sceneData.CurrentFrameIndex);
            int tileTexels = work.TileSize * work.TileSize;
            int captureGroups = work.TilesCaptured * ((tileTexels + 63) / 64);
            int lightGroups = (work.TexelsLit + 63) / 64;
            DispatchSurfaceCacheWork(cmd, pushConstants, WorkModeCapture, captureGroups);
            if (captureGroups > 0)
            {
                InsertSurfaceCacheWorkBarrier(cmd);
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _dilatePipeline);
                DispatchSurfaceCacheWork(cmd, pushConstants, WorkModeDilateCapture, captureGroups);
                InsertSurfaceCacheWorkBarrier(cmd);
                DispatchSurfaceCacheWork(cmd, pushConstants, WorkModeDilateRadiance, captureGroups);
                InsertSurfaceCacheWorkBarrier(cmd);
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            }
            DispatchSurfaceCacheWork(cmd, pushConstants, WorkModeLight, lightGroups);

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

            if (_dilatePipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _dilatePipeline, null);
                _dilatePipeline = default;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
                _accelerationStructureSet = default;
            }

            if (_accelerationStructureSetLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _accelerationStructureSetLayout, null);
                _accelerationStructureSetLayout = default;
            }

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private GPUSurfaceCacheConstants CreatePushConstants(SurfaceCacheFrameWork work, SceneRenderingData sceneData, uint frameIndex)
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
                EvictionCount = checked((uint)work.EvictionCount),
                EnvironmentRadianceAndIntensity = new Njulf.Core.Math.Vector4(
                    Math.Max(sceneData.ClearColor.X, 0.0f) * (_settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f),
                    Math.Max(sceneData.ClearColor.Y, 0.0f) * (_settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f),
                    Math.Max(sceneData.ClearColor.Z, 0.0f) * (_settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f),
                    _settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f),
                LightCount = checked((uint)Math.Max(0, sceneData.LightCount)),
                MaxShadedLights = checked((uint)Math.Clamp(sceneData.DdgiEffectiveMaxShadedLights > 0 ? sceneData.DdgiEffectiveMaxShadedLights : gi.DdgiMaxShadedLights, 0, 64)),
                DirectionalLightCount = checked((uint)Math.Max(0, sceneData.DirectionalLightCount)),
                LocalLightCount = checked((uint)Math.Max(0, sceneData.LocalLightCount)),
                PrimaryDirectionalLightIndex = EncodeLightIndex(sceneData.DdgiPrimaryDirectionalLightIndex),
                SelectedLocalLightIndex = EncodeLightIndex(sceneData.DdgiSelectedLocalLightIndex),
                SelectedLocalLightEnergyScale = Math.Clamp(sceneData.DdgiSelectedLocalLightEnergyScale, 0.0f, 64.0f),
                EmissiveSourceCount = checked((uint)Math.Max(0, sceneData.DdgiEmissiveSourceCount)),
                MaterialTextureMaxCascade = EncodeMaterialTextureMaxCascade(gi.DdgiMaterialTextureMaxCascade),
                WorkMode = WorkModeCapture,
                WorkBufferIndex = checked((uint)work.WorkBufferIndex),
                Padding2 = 0
            };
        }

        private void DispatchSurfaceCacheWork(CommandBuffer cmd, GPUSurfaceCacheConstants pushConstants, uint workMode, int groupCount)
        {
            if (groupCount <= 0)
                return;

            pushConstants.WorkMode = workMode;
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSurfaceCacheConstants>(),
                &pushConstants);
            _context.Api.CmdDispatch(cmd, checked((uint)groupCount), 1, 1);
        }

        private void InsertSurfaceCacheWorkBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private void ClearSurfaceCacheAtlas(CommandBuffer cmd, RenderTarget atlas)
        {
            atlas.TransitionToTransferDestination(cmd);
            var clearColor = new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f);
            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };
            _context.Api.CmdClearColorImage(cmd, atlas.Image, ImageLayout.TransferDstOptimal, &clearColor, 1, &range);
        }

        private static uint EncodeLightIndex(int lightIndex)
        {
            return lightIndex < 0 ? uint.MaxValue : checked((uint)lightIndex);
        }

        private static uint EncodeMaterialTextureMaxCascade(int maxCascade)
        {
            return maxCascade < 0
                ? GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount
                : checked((uint)Math.Clamp(maxCascade, 0, GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount - 1));
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

        private void CreateAccelerationStructureSetLayout()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding
            };

            Result result = _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _accelerationStructureSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SurfaceCachePass acceleration-structure descriptor set layout", result);
            _context.SetDebugName(_accelerationStructureSetLayout.Handle, ObjectType.DescriptorSetLayout, "SurfaceCachePass Acceleration Structure Set Layout");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout, _accelerationStructureSetLayout];
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

        private void CreateDescriptorSet()
        {
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.AccelerationStructureKhr,
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
                throw new VulkanException("Failed to create SurfaceCachePass descriptor pool", result);

            var layout = _accelerationStructureSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };

            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, out _accelerationStructureSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate SurfaceCachePass acceleration-structure descriptor set", result);
        }

        private void UpdateAccelerationStructureDescriptor()
        {
            AccelerationStructureKHR tlas = _accelerationStructureManager.TopLevelAccelerationStructureHandle;
            if (_boundTlas.Handle == tlas.Handle)
                return;

            var accelerationStructureInfo = new WriteDescriptorSetAccelerationStructureKHR
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = &tlas
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                PNext = &accelerationStructureInfo,
                DstSet = _accelerationStructureSet,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr
            };

            _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
            _boundTlas = tlas;
        }

        private VkPipeline CreatePipeline(string shaderName, string debugName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, shaderName);

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
                    throw new VulkanException($"Failed to create {debugName}", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
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
