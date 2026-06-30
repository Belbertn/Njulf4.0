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
    public sealed unsafe class DdgiTracePass : DdgiComputePass
    {
        public DdgiTracePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("DdgiTracePass", "ddgi_trace.comp.spv", context, swapchain, bindlessHeap, settings, probeVolumeManager, accelerationStructureManager, requiresRayQuery: true)
        {
        }

        protected override AccessFlags2 BarrierDestinationAccess => AccessFlags2.ShaderStorageReadBit;
        protected override PipelineStageFlags2 BarrierDestinationStage => PipelineStageFlags2.ComputeShaderBit;
    }

    public sealed unsafe class DdgiBlendPass : DdgiComputePass
    {
        public DdgiBlendPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("DdgiBlendPass", "ddgi_blend.comp.spv", context, swapchain, bindlessHeap, settings, probeVolumeManager, accelerationStructureManager, requiresRayQuery: true)
        {
        }
    }

    public sealed unsafe class DdgiRelocateClassifyPass : DdgiComputePass
    {
        public DdgiRelocateClassifyPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("DdgiRelocateClassifyPass", "ddgi_relocate_classify.comp.spv", context, swapchain, bindlessHeap, settings, probeVolumeManager, accelerationStructureManager, requiresRayQuery: true)
        {
        }
    }

    public sealed unsafe class DdgiPublishPass : RenderPassBase
    {
        private readonly RenderSettings _settings;
        private readonly DdgiProbeVolumeManager _probeVolumeManager;
        private readonly AccelerationStructureManager _accelerationStructureManager;

        public DdgiPublishPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("DdgiPublishPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _probeVolumeManager = probeVolumeManager ?? throw new ArgumentNullException(nameof(probeVolumeManager));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "DDGI cache publication is compute-only synchronization for the next frame.";

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            string skipReason = DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(
                gi,
                _accelerationStructureManager.Active,
                sceneData);
            sceneData.DdgiPublishSkipReason = skipReason;
            return skipReason.Length == 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            sceneData.DdgiPublishExecuted = 1;
            sceneData.DdgiPublishSkipReason = string.Empty;
            InsertPublishBarrier(cmd);
            _probeVolumeManager.PublishCompletedUpdates(sceneData);
        }

        private void InsertPublishBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
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

    public abstract unsafe class DdgiComputePass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint EnabledFlag = 1u << 0;
        private const uint ProbeRelocationFlag = 1u << 1;
        private const uint ProbeClassificationFlag = 1u << 2;
        private const uint GpuSchedulerFlag = 1u << 3;
        private const uint RawAtlasRadianceConventionFlag = 1u << 4;

        private readonly string _shaderName;
        private readonly RenderSettings _settings;
        private readonly DdgiProbeVolumeManager _probeVolumeManager;
        private readonly AccelerationStructureManager? _accelerationStructureManager;
        private readonly bool _requiresRayQuery;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _accelerationStructureSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _accelerationStructureSet;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private AccelerationStructureKHR _boundTlas;

        protected DdgiComputePass(
            string passName,
            string shaderName,
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager? accelerationStructureManager,
            bool requiresRayQuery)
            : base(passName, context, swapchain, bindlessHeap)
        {
            _shaderName = shaderName ?? throw new ArgumentNullException(nameof(shaderName));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _probeVolumeManager = probeVolumeManager ?? throw new ArgumentNullException(nameof(probeVolumeManager));
            _accelerationStructureManager = accelerationStructureManager;
            _requiresRayQuery = requiresRayQuery;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "DDGI split update work is compute-only and writes probe buffers.";

        protected virtual PipelineStageFlags2 BarrierDestinationStage => PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit;
        protected virtual AccessFlags2 BarrierDestinationAccess => AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderSampledReadBit;

        public override void Initialize()
        {
            if (!_context.RayQuerySupported || _context.KhrAccelerationStructure == null)
                return;

            if (_requiresRayQuery)
            {
                CreateAccelerationStructureSetLayout();
                CreateDescriptorSet();
            }

            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            string skipReason = DdgiPassExecutionDiagnostics.ResolveUpdateSkipReason(
                _pipeline.Handle != 0,
                gi,
                _accelerationStructureManager?.Active ?? true,
                sceneData);
            sceneData.DdgiUpdateSkipReason = skipReason;
            return skipReason.Length == 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            sceneData.DdgiUpdateExecuted = 1;
            sceneData.DdgiUpdateSkipReason = string.Empty;

            if (_requiresRayQuery)
                UpdateAccelerationStructureDescriptor();

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            if (_requiresRayQuery)
            {
                var asSet = _accelerationStructureSet;
                _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 2, 1, &asSet, 0, null);
            }

            GPUDdgiUpdatePushConstants pushConstants = CreatePushConstants(sceneData);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUDdgiUpdatePushConstants>(),
                &pushConstants);

            if (CanUseGpuSchedulerIndirectDispatch(sceneData))
                _probeVolumeManager.RecordGpuSchedulerTraceIndirectDispatch(cmd);
            else
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

        private GPUDdgiUpdatePushConstants CreatePushConstants(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.SkyIntensity : 0.0f;
            int effectiveMaxShadedLights = sceneData.DdgiEffectiveMaxShadedLights > 0
                ? sceneData.DdgiEffectiveMaxShadedLights
                : gi.DdgiMaxShadedLights;

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
                RaysPerProbe = checked((uint)Math.Clamp(sceneData.DdgiRaysPerProbe, 1, GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe)),
                FrameIndex = sceneData.TemporalSampleIndex,
                IrradianceTexelsPerProbe = GlobalIlluminationProbeVolumeData.IrradianceTexelsPerProbe,
                VisibilityTexelsPerProbe = GlobalIlluminationProbeVolumeData.VisibilityTexelsPerProbe,
                ProbeStateBufferIndex = BindlessIndex.DdgiProbeStateBuffer,
                ProbeUpdateQueueBufferIndex = BindlessIndex.DdgiProbeUpdateQueueBuffer,
                RelocationClassificationBufferIndex = BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                IrradianceAtlasBufferIndex = BindlessIndex.DdgiIrradianceAtlasBuffer,
                VisibilityAtlasBufferIndex = BindlessIndex.DdgiVisibilityAtlasBuffer,
                RayResultScratchBufferIndex = BindlessIndex.DdgiRayResultScratchBuffer,
                RayCapacityPerProbe = checked((uint)Math.Clamp(sceneData.DdgiRaysPerProbe, 1, GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe)),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                Flags = BuildUpdateFlags(gi, sceneData),
                LightCount = checked((uint)Math.Max(0, sceneData.LightCount)),
                MaxShadedLights = checked((uint)Math.Clamp(effectiveMaxShadedLights, 0, 64)),
                DirectionalLightCount = checked((uint)Math.Max(0, sceneData.DirectionalLightCount)),
                LocalLightCount = checked((uint)Math.Max(0, sceneData.LocalLightCount)),
                LightSelectionMode = 1,
                PrimaryDirectionalLightIndex = EncodeLightIndex(sceneData.DdgiPrimaryDirectionalLightIndex),
                SelectedLocalLightIndex = EncodeLightIndex(sceneData.DdgiSelectedLocalLightIndex),
                SelectedLocalLightEnergyScale = Math.Clamp(sceneData.DdgiSelectedLocalLightEnergyScale, 0.0f, 64.0f),
                EmissiveSourceCount = checked((uint)Math.Max(0, sceneData.DdgiEmissiveSourceCount)),
                EmissiveSourceRevision = sceneData.DdgiEmissiveSourceRevision,
                MaterialTextureMaxCascade = EncodeMaterialTextureMaxCascade(gi.DdgiMaterialTextureMaxCascade),
                FrameSerial = sceneData.DdgiFrameSerialLow32,
                RelocationParams = new Vector4(
                    gi.DdgiRelocationTargetSurfaceDistanceFraction,
                    gi.DdgiRelocationMinSurfaceDistance,
                    gi.DdgiRelocationMaxDistanceFraction,
                    gi.DdgiRelocationBlendAlpha)
            };
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

        private static uint BuildUpdateFlags(GlobalIlluminationSettings settings, SceneRenderingData sceneData)
        {
            uint flags = EnabledFlag;
            if (settings.DdgiProbeRelocationEnabled)
                flags |= ProbeRelocationFlag;
            if (settings.DdgiProbeClassificationEnabled)
                flags |= ProbeClassificationFlag;
            if (settings.DdgiRawAtlasRadianceConventionEnabled)
                flags |= RawAtlasRadianceConventionFlag;
            if (IsGpuSchedulerRenderingActive(settings) &&
                sceneData.DdgiGpuSchedulerFallbackActive == 0 &&
                sceneData.DdgiGpuSchedulerConsideredProbeCount > 0)
            {
                flags |= GpuSchedulerFlag;
            }
            return flags;
        }

        private bool CanUseGpuSchedulerIndirectDispatch(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return IsGpuSchedulerRenderingActive(gi) &&
                   sceneData.DdgiGpuSchedulerFallbackActive == 0 &&
                   sceneData.DdgiGpuSchedulerConsideredProbeCount > 0 &&
                   _probeVolumeManager.HasGpuSchedulerTraceIndirectDispatchBuffer;
        }

        private static bool IsGpuSchedulerRenderingActive(GlobalIlluminationSettings settings)
        {
            return settings.DdgiSchedulerMode == DdgiSchedulerMode.Gpu ||
                   (settings.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare &&
                    settings.DdgiCompareModeUseGpuQueueForRendering);
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
                throw new VulkanException("Failed to create DDGI trace acceleration-structure descriptor set layout", result);
            _context.SetDebugName(_accelerationStructureSetLayout.Handle, ObjectType.DescriptorSetLayout, "DDGI Trace Acceleration Structure Set Layout");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {Name} pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, $"{Name} Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = _requiresRayQuery
                ? [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout, _accelerationStructureSetLayout]
                : [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout];

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
                    throw new VulkanException($"Failed to create {Name} pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, $"{Name} Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, _shaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, _shaderName);

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
                    throw new VulkanException($"Failed to create {Name} compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, $"{Name} Compute Pipeline");
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
                throw new VulkanException("Failed to create DDGI trace descriptor pool", result);

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
                throw new VulkanException("Failed to allocate DDGI trace acceleration-structure descriptor set", result);
        }

        private void UpdateAccelerationStructureDescriptor()
        {
            if (_accelerationStructureManager == null)
                throw new InvalidOperationException("DDGI trace requires an acceleration structure manager.");

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
                DstStageMask = BarrierDestinationStage,
                DstAccessMask = BarrierDestinationAccess
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

    internal static class DdgiPassExecutionDiagnostics
    {
        public static string ResolveUpdateSkipReason(
            bool pipelineAvailable,
            GlobalIlluminationSettings settings,
            bool accelerationStructureActive,
            SceneRenderingData sceneData)
        {
            if (!pipelineAvailable)
                return "pipeline-unavailable";
            return ResolveCommonSkipReason(settings, accelerationStructureActive, sceneData);
        }

        public static string ResolvePublishSkipReason(
            GlobalIlluminationSettings settings,
            bool accelerationStructureActive,
            SceneRenderingData sceneData)
        {
            return ResolveCommonSkipReason(settings, accelerationStructureActive, sceneData);
        }

        private static string ResolveCommonSkipReason(
            GlobalIlluminationSettings settings,
            bool accelerationStructureActive,
            SceneRenderingData sceneData)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));

            if (!settings.Enabled)
                return "global-illumination-disabled";
            if (!settings.EffectiveUseDdgi)
                return "ddgi-disabled";
            if (!settings.EffectiveUseRayQueryBackend)
                return "ray-query-backend-disabled";
            if (!accelerationStructureActive)
                return "acceleration-structure-inactive";
            if (sceneData.DdgiProbeVolumeCount <= 0)
                return "no-ddgi-volumes";
            if (sceneData.DdgiProbesUpdated <= 0)
                return "no-ddgi-updates";
            return string.Empty;
        }
    }
}
