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
    public sealed unsafe class BloomPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _descriptorSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet[] _extractSets = Array.Empty<DescriptorSet>();
        private DescriptorSet[] _downsampleSets = Array.Empty<DescriptorSet>();
        private DescriptorSet[] _upsampleSets = Array.Empty<DescriptorSet>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _extractPipeline;
        private VkPipeline _downsamplePipeline;
        private VkPipeline _upsamplePipeline;

        public BloomPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("BloomPass", context, swapchain, bindlessHeap)
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
            _extractPipeline = CreatePipeline("bloom_extract.comp.spv", "Bloom Extract Compute Pipeline");
            _downsamplePipeline = CreatePipeline("bloom_downsample.comp.spv", "Bloom Downsample Compute Pipeline");
            _upsamplePipeline = CreatePipeline("bloom_upsample.comp.spv", "Bloom Upsample Compute Pipeline");
            RecreateDescriptorSets();
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            int mipCount = EffectiveMipCount;
            sceneData.BloomEnabled = _settings.Bloom.Enabled;
            sceneData.BloomMipCount = (uint)mipCount;
            sceneData.BloomBaseWidth = mipCount == 0 ? 0u : _renderTargets.BloomMipChain[0].Extent.Width;
            sceneData.BloomBaseHeight = mipCount == 0 ? 0u : _renderTargets.BloomMipChain[0].Extent.Height;

            if (!_settings.Bloom.Enabled || mipCount == 0 || (sceneData.FogEnabled && _settings.Fog.DebugView != FogDebugView.None))
            {
                sceneData.BloomEnabled = false;
                return;
            }

            RenderTarget activeSceneColor = sceneData.ActiveSceneColorTextureIndex == BindlessIndex.FoggedSceneColorTexture
                ? _renderTargets.FoggedSceneColor
                : _renderTargets.SceneColor;
            DescriptorSet extractSet = sceneData.ActiveSceneColorTextureIndex == BindlessIndex.FoggedSceneColorTexture && _extractSets.Length > 1
                ? _extractSets[1]
                : _extractSets[0];

            activeSceneColor.TransitionToShaderRead(cmd);
            _renderTargets.BloomMipChain[0].TransitionToStorageWrite(cmd);

            long stageStart = Stopwatch.GetTimestamp();
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _extractPipeline);
            Dispatch(cmd, extractSet, activeSceneColor.Extent, _renderTargets.BloomMipChain[0].Extent, "BloomExtractPass", mode: 0);
            _renderTargets.BloomMipChain[0].TransitionToShaderRead(cmd);
            sceneData.CpuBloomExtractRecordMicroseconds = ElapsedMicroseconds(stageStart);

            stageStart = Stopwatch.GetTimestamp();
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _downsamplePipeline);
            for (int mip = 1; mip < mipCount; mip++)
            {
                RenderTarget destination = _renderTargets.BloomMipChain[mip];
                destination.TransitionToStorageWrite(cmd);
                Dispatch(
                    cmd,
                    _downsampleSets[mip - 1],
                    _renderTargets.BloomMipChain[mip - 1].Extent,
                    destination.Extent,
                    $"BloomDownsamplePass Mip {mip}",
                    mode: 0);
                destination.TransitionToShaderRead(cmd);
            }
            sceneData.CpuBloomDownsampleRecordMicroseconds = ElapsedMicroseconds(stageStart);

            stageStart = Stopwatch.GetTimestamp();
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _upsamplePipeline);
            for (int mip = mipCount - 2; mip >= 0; mip--)
            {
                RenderTarget destination = _renderTargets.BloomMipChain[mip];
                destination.TransitionToStorageReadWrite(cmd);
                Dispatch(
                    cmd,
                    _upsampleSets[mip],
                    _renderTargets.BloomMipChain[mip + 1].Extent,
                    destination.Extent,
                    $"BloomUpsamplePass Mip {mip}",
                    mode: 0);
                destination.TransitionToShaderRead(cmd);
            }
            sceneData.CpuBloomUpsampleRecordMicroseconds = ElapsedMicroseconds(stageStart);
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
            DestroyPipeline(_extractPipeline);
            _extractPipeline = default;
            DestroyPipeline(_downsamplePipeline);
            _downsamplePipeline = default;
            DestroyPipeline(_upsamplePipeline);
            _upsamplePipeline = default;
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

        private int EffectiveMipCount => Math.Min(_settings.Bloom.MipCount, _renderTargets.BloomMipCount);

        private void Dispatch(
            CommandBuffer cmd,
            DescriptorSet descriptorSet,
            Extent2D sourceExtent,
            Extent2D destinationExtent,
            string label,
            uint mode)
        {
            _context.BeginDebugLabel(cmd, label);
            try
            {
                _context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);

                var pushConstants = new GPUBloomPushConstants
                {
                    SourceDimensions = new Vector2(sourceExtent.Width, sourceExtent.Height),
                    DestinationDimensions = new Vector2(destinationExtent.Width, destinationExtent.Height),
                    Threshold = _settings.Bloom.Threshold,
                    Knee = _settings.Bloom.Knee,
                    Radius = _settings.Bloom.Radius,
                    Mode = mode
                };

                _context.Api.CmdPushConstants(
                    cmd,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<GPUBloomPushConstants>(),
                    &pushConstants);

                _context.Api.CmdDispatch(
                    cmd,
                    (destinationExtent.Width + 7u) / 8u,
                    (destinationExtent.Height + 7u) / 8u,
                    1);
            }
            finally
            {
                _context.EndDebugLabel(cmd);
            }
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
                throw new VulkanException("Failed to create bloom descriptor set layout", result);
            _context.SetDebugName(_descriptorSetLayout.Handle, ObjectType.DescriptorSetLayout, "Bloom Descriptor Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create bloom pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Bloom Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUBloomPushConstants>()
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

            Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create bloom pipeline layout", result);
            _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Bloom Compute Pipeline Layout");
        }

        private VkPipeline CreatePipeline(string shaderName, string debugName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, shaderName);

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
                    throw new VulkanException($"Failed to create bloom pipeline '{debugName}'", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
                return pipeline;
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

            int mipCount = _renderTargets.BloomMipCount;
            int extractCount = mipCount > 0 ? 2 : 0;
            int downsampleCount = Math.Max(0, mipCount - 1);
            int upsampleCount = Math.Max(0, mipCount - 1);
            int setCount = extractCount + downsampleCount + upsampleCount;
            if (setCount == 0)
                return;

            var poolSizes = stackalloc DescriptorPoolSize[2];
            poolSizes[0] = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = (uint)(setCount * 2)
            };
            poolSizes[1] = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = (uint)setCount
            };

            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
                MaxSets = (uint)setCount
            };

            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create bloom descriptor pool", result);

            var allSets = new DescriptorSet[setCount];
            var layouts = new DescriptorSetLayout[setCount];
            Array.Fill(layouts, _descriptorSetLayout);

            fixed (DescriptorSetLayout* layoutsPtr = layouts)
            fixed (DescriptorSet* setsPtr = allSets)
            {
                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = (uint)setCount,
                    PSetLayouts = layoutsPtr
                };

                result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, setsPtr);
                if (result != Result.Success)
                    throw new VulkanException("Failed to allocate bloom descriptor sets", result);
            }

            int offset = 0;
            _extractSets = allSets[offset..(offset + extractCount)];
            offset += extractCount;
            _downsampleSets = allSets[offset..(offset + downsampleCount)];
            offset += downsampleCount;
            _upsampleSets = allSets[offset..(offset + upsampleCount)];
            UpdateDescriptorSets();
        }

        private void UpdateDescriptorSets()
        {
            if (_renderTargets.BloomMipCount == 0)
                return;

            WriteDescriptorSet(_extractSets[0], _renderTargets.SceneColor.View, _renderTargets.SceneColor.View, _renderTargets.BloomMipChain[0].View);
            if (_extractSets.Length > 1)
                WriteDescriptorSet(_extractSets[1], _renderTargets.FoggedSceneColor.View, _renderTargets.FoggedSceneColor.View, _renderTargets.BloomMipChain[0].View);

            for (int mip = 1; mip < _renderTargets.BloomMipCount; mip++)
            {
                WriteDescriptorSet(
                    _downsampleSets[mip - 1],
                    _renderTargets.BloomMipChain[mip - 1].View,
                    _renderTargets.BloomMipChain[mip - 1].View,
                    _renderTargets.BloomMipChain[mip].View);
            }

            for (int mip = 0; mip < _renderTargets.BloomMipCount - 1; mip++)
            {
                RenderTarget lowerMip = _renderTargets.BloomMipChain[mip + 1];
                WriteDescriptorSet(
                    _upsampleSets[mip],
                    lowerMip.View,
                    lowerMip.View,
                    _renderTargets.BloomMipChain[mip].View);
            }
        }

        private void WriteDescriptorSet(DescriptorSet set, ImageView source0, ImageView source1, ImageView destination)
        {
            var sourceInfos = stackalloc DescriptorImageInfo[2];
            sourceInfos[0] = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.ScreenSampler,
                ImageView = source0,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            sourceInfos[1] = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.ScreenSampler,
                ImageView = source1,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
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
                PImageInfo = sourceInfos
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

        private void DestroyDescriptorPool()
        {
            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
            }

            _extractSets = Array.Empty<DescriptorSet>();
            _downsampleSets = Array.Empty<DescriptorSet>();
            _upsampleSets = Array.Empty<DescriptorSet>();
        }

        private void DestroyPipeline(VkPipeline pipeline)
        {
            if (pipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, pipeline, null);
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }
    }
}
