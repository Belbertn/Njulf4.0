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
    public sealed unsafe class DdgiUpdatePass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint EnabledFlag = 1u << 0;

        private readonly RenderSettings _settings;
        private readonly DdgiProbeVolumeManager _probeVolumeManager;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _accelerationStructureSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _accelerationStructureSet;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private AccelerationStructureKHR _boundTlas;

        public DdgiUpdatePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("DdgiUpdatePass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _probeVolumeManager = probeVolumeManager ?? throw new ArgumentNullException(nameof(probeVolumeManager));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "DDGI ray updates are compute-only and write probe buffers.";

        public override void Initialize()
        {
            if (!_context.RayQuerySupported || _context.KhrAccelerationStructure == null)
                return;

            CreateAccelerationStructureSetLayout();
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
            CreateDescriptorSet();
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return _pipeline.Handle != 0 &&
                   gi.Enabled &&
                   gi.EffectiveUseDdgi &&
                   gi.EffectiveUseRayQueryBackend &&
                   _accelerationStructureManager.Active &&
                   sceneData.DdgiProbeVolumeCount > 0 &&
                   sceneData.DdgiProbesUpdated > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            UpdateAccelerationStructureDescriptor();

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
            var asSet = _accelerationStructureSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 2, 1, &asSet, 0, null);

            GPUDdgiUpdatePushConstants pushConstants = CreatePushConstants(sceneData, frameIndex);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUDdgiUpdatePushConstants>(),
                &pushConstants);

            _context.Api.CmdDispatch(cmd, (uint)sceneData.DdgiProbesUpdated, 1, 1);
            InsertWriteBarrier(cmd);
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

            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
                _accelerationStructureSet = default;
                _boundTlas = default;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
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

        private GPUDdgiUpdatePushConstants CreatePushConstants(SceneRenderingData sceneData, int frameIndex)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.SkyIntensity : 0.0f;
            return new GPUDdgiUpdatePushConstants
            {
                EnvironmentRadianceAndIntensity = new Vector4(
                    0.45f * environmentIntensity,
                    0.55f * environmentIntensity,
                    0.7f * environmentIntensity,
                    gi.EnvironmentFallbackIntensity),
                ProbeCount = checked((uint)Math.Max(0, sceneData.DdgiProbeCount)),
                VolumeCount = checked((uint)Math.Max(0, sceneData.DdgiProbeVolumeCount)),
                StartProbeIndex = checked((uint)Math.Max(0, _probeVolumeManager.ScheduledUpdateStartProbeIndex)),
                ProbesToUpdate = checked((uint)Math.Max(0, sceneData.DdgiProbesUpdated)),
                RaysPerProbe = checked((uint)Math.Clamp(sceneData.DdgiRaysPerProbe, 1, 256)),
                FrameIndex = checked((uint)frameIndex),
                IrradianceTexelsPerProbe = GlobalIlluminationProbeVolumeData.IrradianceTexelsPerProbe,
                VisibilityTexelsPerProbe = GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe,
                ProbeStateBufferIndex = BindlessIndex.DdgiProbeStateBuffer,
                ProbeUpdateQueueBufferIndex = BindlessIndex.DdgiProbeUpdateQueueBuffer,
                RelocationClassificationBufferIndex = BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                IrradianceAtlasBufferIndex = BindlessIndex.DdgiIrradianceAtlasBuffer,
                VisibilityAtlasBufferIndex = BindlessIndex.DdgiVisibilityAtlasBuffer,
                Flags = EnabledFlag,
                LightCount = checked((uint)Math.Max(0, sceneData.LightCount))
            };
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
                throw new VulkanException("Failed to create DDGI update acceleration-structure descriptor set layout", result);
            _context.SetDebugName(_accelerationStructureSetLayout.Handle, ObjectType.DescriptorSetLayout, "DDGI Update Acceleration Structure Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create DDGI update pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "DDGI Update Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts =
            [
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout,
                _accelerationStructureSetLayout
            ];

            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUDdgiUpdatePushConstants>()
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
                    throw new VulkanException("Failed to create DDGI update pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "DDGI Update Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "ddgi_update.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "ddgi_update.comp.spv");

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
                    throw new VulkanException("Failed to create DDGI update compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, "DDGI Update Compute Pipeline");
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
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
                throw new VulkanException("Failed to create DDGI update descriptor pool", result);

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
                throw new VulkanException("Failed to allocate DDGI update acceleration-structure descriptor set", result);
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

        private void InsertWriteBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderSampledReadBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }
    }
}
