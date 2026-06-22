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
    public sealed unsafe class SsgiTemporalPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _outputSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _writeHistoryASet;
        private DescriptorSet _writeHistoryBSet;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _historyValid;
        private bool _writeHistoryA = true;
        private Extent2D _lastExtent;
        private GlobalIlluminationMode _lastMode = GlobalIlluminationMode.Disabled;
        private float _lastResolutionScale = -1.0f;

        public SsgiTemporalPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("SsgiTemporalPass", context, swapchain, bindlessHeap)
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
            RecreateDescriptorSets();
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (!gi.EffectiveUseSsgi ||
                !sceneData.DepthPrePassEnabled ||
                sceneData.AnimationDebugView != AnimationDebugView.None)
            {
                InvalidateHistory(sceneData);
                RegisterResolvedSsgiTexture(_renderTargets.SsgiRaw.View, _renderTargets.SsgiRaw.View);
                return false;
            }

            if (!gi.TemporalEnabled)
            {
                InvalidateHistory(sceneData);
                RegisterResolvedSsgiTexture(_renderTargets.SsgiRaw.View, _renderTargets.SsgiRaw.View);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            ResetHistoryIfInputsChanged();

            RenderTarget historyRead = _writeHistoryA ? _renderTargets.SsgiHistoryB : _renderTargets.SsgiHistoryA;
            RenderTarget historyWrite = _writeHistoryA ? _renderTargets.SsgiHistoryA : _renderTargets.SsgiHistoryB;
            RenderTarget depthHistoryRead = _writeHistoryA ? _renderTargets.SsgiDepthHistoryB : _renderTargets.SsgiDepthHistoryA;
            RenderTarget depthHistoryWrite = _writeHistoryA ? _renderTargets.SsgiDepthHistoryA : _renderTargets.SsgiDepthHistoryB;
            RenderTarget normalHistoryRead = _writeHistoryA ? _renderTargets.SsgiNormalHistoryB : _renderTargets.SsgiNormalHistoryA;
            RenderTarget normalHistoryWrite = _writeHistoryA ? _renderTargets.SsgiNormalHistoryA : _renderTargets.SsgiNormalHistoryB;
            DescriptorSet outputSet = _writeHistoryA ? _writeHistoryASet : _writeHistoryBSet;

            _renderTargets.SsgiRaw.TransitionToShaderRead(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.SceneNormal.TransitionToShaderRead(cmd);
            if (sceneData.MotionVectorsEnabled != 0)
                _renderTargets.MotionVectors.TransitionToShaderRead(cmd);
            historyRead.TransitionToShaderRead(cmd);
            depthHistoryRead.TransitionToShaderRead(cmd);
            normalHistoryRead.TransitionToShaderRead(cmd);
            _renderTargets.SsgiFiltered.TransitionToStorageWrite(cmd);
            historyWrite.TransitionToStorageWrite(cmd);
            depthHistoryWrite.TransitionToStorageWrite(cmd);
            normalHistoryWrite.TransitionToStorageWrite(cmd);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryTexture,
                historyRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousDepthTexture,
                depthHistoryRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousNormalTexture,
                normalHistoryRead.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 1, 1, &textureSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 2, 1, &outputSet, 0, null);

            uint historyWasValid = _historyValid ? 1u : 0u;
            GPUSsgiTemporalPushConstants pushConstants = CreatePushConstants(sceneData, historyWasValid);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSsgiTemporalPushConstants>(),
                &pushConstants);

            Extent2D extent = _renderTargets.SsgiFiltered.Extent;
            _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u, (extent.Height + 7u) / 8u, 1);

            _renderTargets.SsgiFiltered.TransitionToShaderRead(cmd);
            historyWrite.TransitionToShaderRead(cmd);
            depthHistoryWrite.TransitionToShaderRead(cmd);
            normalHistoryWrite.TransitionToShaderRead(cmd);
            RegisterResolvedSsgiTexture(_renderTargets.SsgiFiltered.View, historyWrite.View);
            RegisterPreviousSurfaceHistoryTextures(depthHistoryWrite.View, normalHistoryWrite.View);

            sceneData.SsgiHistoryValid = (int)historyWasValid;
            sceneData.SsgiRejectedHistoryPixelCount = historyWasValid == 0u
                ? SaturatingPixelCount(extent)
                : 0;

            _historyValid = true;
            _writeHistoryA = !_writeHistoryA;
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
            _historyValid = false;
            _writeHistoryA = true;
            _lastExtent = default;
            RecreateDescriptorSets();
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

        private void InvalidateHistory(SceneRenderingData sceneData)
        {
            _historyValid = false;
            _writeHistoryA = true;
            sceneData.SsgiHistoryValid = 0;
            sceneData.SsgiRejectedHistoryPixelCount = 0;
        }

        private void ResetHistoryIfInputsChanged()
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            Extent2D extent = _renderTargets.SsgiRaw.Extent;
            bool changed = _lastExtent.Width != extent.Width ||
                _lastExtent.Height != extent.Height ||
                _lastMode != gi.Mode ||
                MathF.Abs(_lastResolutionScale - gi.ResolutionScale) > 0.0001f;

            if (changed)
            {
                _historyValid = false;
                _writeHistoryA = true;
                _lastExtent = extent;
                _lastMode = gi.Mode;
                _lastResolutionScale = gi.ResolutionScale;
            }
        }

        private GPUSsgiTemporalPushConstants CreatePushConstants(SceneRenderingData sceneData, uint historyValid)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            Extent2D extent = _renderTargets.SsgiFiltered.Extent;
            return new GPUSsgiTemporalPushConstants
            {
                SourceDimensions = new Vector4(
                    extent.Width,
                    extent.Height,
                    1.0f / Math.Max(1u, extent.Width),
                    1.0f / Math.Max(1u, extent.Height)),
                ReprojectionParams = new Vector4(
                    gi.HistoryResponsiveness,
                    gi.NormalRejectionThreshold,
                    gi.DepthRejectionThreshold,
                    gi.LeakClampStrength),
                HistoryValid = historyValid,
                MotionVectorsEnabled = sceneData.MotionVectorsEnabled != 0 ? 1u : 0u,
                FrameIndex = sceneData.TemporalSampleIndex,
                DebugView = (uint)gi.DebugView
            };
        }

        private void RegisterResolvedSsgiTexture(ImageView filteredView, ImageView historyView)
        {
            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiFilteredTexture,
                filteredView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryTexture,
                historyView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RegisterPreviousSurfaceHistoryTextures(ImageView previousDepthView, ImageView previousNormalView)
        {
            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousDepthTexture,
                previousDepthView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousNormalTexture,
                previousNormalView,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        private static int SaturatingPixelCount(Extent2D extent)
        {
            ulong pixels = (ulong)extent.Width * extent.Height;
            return pixels > int.MaxValue ? int.MaxValue : (int)pixels;
        }

        private void CreateOutputSetLayout()
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[3];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageImage,
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
            bindings[2] = new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 3,
                PBindings = bindings
            };

            Result result = _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _outputSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SSGI temporal output descriptor set layout", result);
            _context.SetDebugName(_outputSetLayout.Handle, ObjectType.DescriptorSetLayout, "SSGI Temporal Output Descriptor Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SSGI temporal pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "SSGI Temporal Pipeline Cache");
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
                    Size = (uint)Marshal.SizeOf<GPUSsgiTemporalPushConstants>()
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
                    throw new VulkanException("Failed to create SSGI temporal pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "SSGI Temporal Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "ssgi_temporal.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "ssgi_temporal.comp.spv");

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
                    throw new VulkanException("Failed to create SSGI temporal compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "SSGI Temporal Compute Pipeline");
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

            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = 8
            };

            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 2
            };

            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create SSGI temporal descriptor pool", result);

            var sets = stackalloc DescriptorSet[2];
            var layouts = stackalloc DescriptorSetLayout[2];
            layouts[0] = _outputSetLayout;
            layouts[1] = _outputSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 2,
                PSetLayouts = layouts
            };

            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocInfo, sets);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate SSGI temporal descriptor sets", result);

            _writeHistoryASet = sets[0];
            _writeHistoryBSet = sets[1];
            WriteOutputSet(
                _writeHistoryASet,
                _renderTargets.SsgiHistoryA.View,
                _renderTargets.SsgiDepthHistoryA.View,
                _renderTargets.SsgiNormalHistoryA.View);
            WriteOutputSet(
                _writeHistoryBSet,
                _renderTargets.SsgiHistoryB.View,
                _renderTargets.SsgiDepthHistoryB.View,
                _renderTargets.SsgiNormalHistoryB.View);
        }

        private void WriteOutputSet(
            DescriptorSet set,
            ImageView historyWriteView,
            ImageView depthHistoryWriteView,
            ImageView normalHistoryWriteView)
        {
            var outputInfos = stackalloc DescriptorImageInfo[2];
            outputInfos[0] = new DescriptorImageInfo
            {
                ImageView = _renderTargets.SsgiFiltered.View,
                ImageLayout = ImageLayout.General
            };
            outputInfos[1] = new DescriptorImageInfo
            {
                ImageView = historyWriteView,
                ImageLayout = ImageLayout.General
            };

            var depthHistoryInfo = new DescriptorImageInfo
            {
                ImageView = depthHistoryWriteView,
                ImageLayout = ImageLayout.General
            };
            var normalHistoryInfo = new DescriptorImageInfo
            {
                ImageView = normalHistoryWriteView,
                ImageLayout = ImageLayout.General
            };

            var writes = stackalloc WriteDescriptorSet[3];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 2,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = outputInfos
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &depthHistoryInfo
            };
            writes[2] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &normalHistoryInfo
            };

            _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
        }

        private void DestroyDescriptorPool()
        {
            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
                _writeHistoryASet = default;
                _writeHistoryBSet = default;
            }
        }
    }
}
