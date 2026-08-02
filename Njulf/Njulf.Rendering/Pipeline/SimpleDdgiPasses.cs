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
    public sealed unsafe class SimpleDdgiTracePass : SimpleDdgiComputePass
    {
        public SimpleDdgiTracePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("SimpleDdgiTracePass", "ddgi_simple_trace.comp.spv", context, swapchain, bindlessHeap, settings, volumeManager, farFieldClipmapManager, accelerationStructureManager, requiresRayQuery: true)
        {
        }

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            ulong rayCount = checked((ulong)Math.Max(0, VolumeManager.ProbesToUpdate) * (ulong)Math.Max(1, VolumeManager.RaysPerProbe));
            return checked((uint)Math.Max(1UL, (rayCount + 63UL) / 64UL));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            // The trace is the transaction producer.  If the ray-query resource is
            // unavailable, invalidate this frame's transaction before relocation or
            // blending have a chance to observe an older scratch allocation.
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanExecuteTraceTransaction)
            {
                VolumeManager.AbortUpdateTransaction();
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            base.Execute(cmd, frameIndex, sceneData);
            VolumeManager.MarkTraceExecuted();
        }
    }

    public sealed unsafe class SimpleDdgiBlendPass : SimpleDdgiComputePass
    {
        public SimpleDdgiBlendPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager)
            : base("SimpleDdgiBlendPass", "ddgi_simple_blend.comp.spv", context, swapchain, bindlessHeap, settings, volumeManager, farFieldClipmapManager, null, requiresRayQuery: false)
        {
        }

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            return checked((uint)Math.Max(1, VolumeManager.ProbesToUpdate));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            // A planner evaluates all three predicates before trace is recorded.
            // Use the schedule-time gate here, then require the strict producer
            // chain immediately before recording the actual consumer dispatch.
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanScheduleBlendTransaction)
            {
                VolumeManager.AbortUpdateTransaction();
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!VolumeManager.CanExecuteBlendTransaction)
            {
                VolumeManager.AbortUpdateTransaction();
                return;
            }

            base.Execute(cmd, frameIndex, sceneData);
            VolumeManager.MarkBlendExecuted();
        }
    }

    /// <summary>
    /// Resolves one explicit recursive DDGI transport iteration from cached
    /// source rays and the last published irradiance field.  It deliberately has
    /// no ray-query dependency: direct/sky/emissive source work remains in the
    /// trace producer and is reused until a source generation changes.
    /// </summary>
    public sealed unsafe class SimpleDdgiTransportPass : SimpleDdgiComputePass
    {
        public SimpleDdgiTransportPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager)
            : base("SimpleDdgiTransportPass", "ddgi_simple_transport.comp.spv", context, swapchain, bindlessHeap, settings, volumeManager, farFieldClipmapManager, null, requiresRayQuery: false)
        {
        }

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            ulong rayCount = checked((ulong)Math.Max(0, VolumeManager.ProbesToUpdate) * (ulong)Math.Max(1, VolumeManager.RaysPerProbe));
            return checked((uint)Math.Max(1UL, (rayCount + 63UL) / 64UL));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            // V1 has no standalone transport pass. Do not abort its valid
            // trace/relocate/blend transaction when the compatibility path is
            // intentionally selected.
            if (!VolumeManager.TransportV2Active)
                return false;
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanScheduleTransportTransaction)
            {
                VolumeManager.AbortUpdateTransaction();
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!VolumeManager.CanExecuteTransportTransaction)
            {
                VolumeManager.AbortUpdateTransaction();
                return;
            }

            base.Execute(cmd, frameIndex, sceneData);
            VolumeManager.MarkTransportExecuted();
        }
    }

    public sealed unsafe class SimpleDdgiRelocateClassifyPass : SimpleDdgiComputePass
    {
        public SimpleDdgiRelocateClassifyPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager)
            : base("SimpleDdgiRelocateClassifyPass", "ddgi_simple_relocate_classify.comp.spv", context, swapchain, bindlessHeap, settings, volumeManager, farFieldClipmapManager, null, requiresRayQuery: false)
        {
        }

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            return checked((uint)Math.Max(1UL, ((ulong)Math.Max(0, VolumeManager.ProbesToUpdate) + 63UL) / 64UL));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            // See SimpleDdgiBlendPass: this must stay schedulable beside trace for
            // async planning, but it may only record after this transaction's trace.
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanScheduleRelocateClassifyTransaction)
            {
                VolumeManager.AbortUpdateTransaction();
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!VolumeManager.CanExecuteRelocateClassifyTransaction)
            {
                VolumeManager.AbortUpdateTransaction();
                return;
            }

            base.Execute(cmd, frameIndex, sceneData);
            VolumeManager.MarkRelocateClassifyExecuted();
        }

    }

    /// <summary>
    /// Publishes the completed private Jacobi target directly from the GPU-visible
    /// update queue. The optional filtered image mirror is dual-written by a
    /// second compute dispatch; neither path needs CPU sorting or copy regions.
    /// </summary>
    public sealed unsafe class SimpleDdgiPublishPass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private readonly RenderSettings _settings;
        private readonly SimpleDdgiVolumeManager _volumeManager;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _sampledAtlasSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _sampledAtlasSet;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _canonicalPipeline;
        private VkPipeline _sampledPipeline;
        private ulong _boundSampledAtlasGeneration;
        private bool _sampledPublicationSupported;

        public SimpleDdgiPublishPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager)
            : base("SimpleDdgiPublishPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason =>
            "Simple DDGI publication consumes the GPU queue and writes canonical probe storage.";

        public override void Initialize()
        {
            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            _sampledPublicationSupported =
                _context.ShaderStorageImageArrayNonUniformIndexingSupported &&
                properties.Limits.MaxPerStageDescriptorStorageImages >=
                2u * SimpleDdgiSampledAtlas.MaxGpuPublishTextureGroups;
            CreateSampledAtlasSetLayout();
            CreateDescriptorSet();
            CreatePipelineCache();
            CreatePipelineLayout();
            _canonicalPipeline = CreatePipeline("ddgi_simple_publish.comp.spv");
            if (_sampledPublicationSupported)
                _sampledPipeline = CreatePipeline("ddgi_simple_publish_sampled.comp.spv");
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (_canonicalPipeline.Handle == 0 ||
                !gi.EffectiveUseSimpleDdgi ||
                !gi.SimpleDdgiStructuredGatherEnabled ||
                !gi.EffectiveUseRayQueryBackend ||
                _volumeManager.ProbeCount <= 0 ||
                _volumeManager.ProbesToUpdate <= 0 ||
                !_volumeManager.CanSchedulePublishTransaction)
            {
                _volumeManager.AbortUpdateTransaction();
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!_volumeManager.CanExecutePublishTransaction)
            {
                _volumeManager.AbortUpdateTransaction();
                return;
            }

            GPUSimpleDdgiPublishPushConstants pushConstants = CreatePushConstants();
            uint groupCount = checked((uint)Math.Max(1, _volumeManager.ProbesToUpdate));

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _canonicalPipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
            PushConstants(cmd, pushConstants);
            _context.Api.CmdDispatch(cmd, groupCount, 1, 1);
            InsertComputeStorageBarrier(cmd);

            if (_sampledPublicationSupported &&
                _sampledPipeline.Handle != 0 &&
                _volumeManager.SampledAtlasActive)
            {
                ulong allocationGeneration = _volumeManager.SampledAtlasAllocationGeneration;
                if (_boundSampledAtlasGeneration != allocationGeneration)
                {
                    _volumeManager.UpdateSampledAtlasGpuPublishDescriptors(_sampledAtlasSet);
                    _boundSampledAtlasGeneration = allocationGeneration;
                }

                _volumeManager.BeginSampledAtlasGpuPublication(cmd);
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _sampledPipeline);
                BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
                DescriptorSet sampledAtlasSet = _sampledAtlasSet;
                _context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    2,
                    1,
                    &sampledAtlasSet,
                    0,
                    null);
                PushConstants(cmd, pushConstants);
                _context.Api.CmdDispatch(cmd, groupCount, 1, 1);
                _volumeManager.EndSampledAtlasGpuPublication(cmd);
            }

            // Capture state only after canonical and optional image publication
            // have been recorded. The transaction is completed at this point.
            _volumeManager.RecordProbeStateReadback(cmd, frameIndex);
            _volumeManager.MarkPublishExecuted();
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void Cleanup()
        {
            if (_canonicalPipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _canonicalPipeline, null);
            if (_sampledPipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _sampledPipeline, null);
            if (_pipelineLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            if (_descriptorPool.Handle != 0)
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
            if (_sampledAtlasSetLayout.Handle != 0)
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _sampledAtlasSetLayout, null);
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);

            _canonicalPipeline = default;
            _sampledPipeline = default;
            _pipelineLayout = default;
            _pipelineCache = default;
            _descriptorPool = default;
            _sampledAtlasSet = default;
            _sampledAtlasSetLayout = default;
        }

        private GPUSimpleDdgiPublishPushConstants CreatePushConstants() => new()
        {
            ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
            IrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
            VisibilityAtlasBufferIndex = BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
            ProbeStateBufferIndex = BindlessIndex.SimpleDdgiProbeStateBuffer,
            ProbeUpdateQueueBufferIndex = BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
            TransportIrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer,
            SampledAtlasGroupCount = checked((uint)Math.Max(0, _volumeManager.SampledAtlasGroupCount)),
            SampledAtlasLayersPerTexture = checked((uint)Math.Max(0, _volumeManager.SampledAtlasLayersPerTexture))
        };

        private void PushConstants(CommandBuffer cmd, GPUSimpleDdgiPublishPushConstants pushConstants)
        {
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiPublishPushConstants>(),
                &pushConstants);
        }

        private void CreateSampledAtlasSetLayout()
        {
            uint descriptorCount = _sampledPublicationSupported
                ? SimpleDdgiSampledAtlas.MaxGpuPublishTextureGroups
                : 1u;
            DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = descriptorCount,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = descriptorCount,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = bindings
            };
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _sampledAtlasSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish image layout", result);
        }

        private void CreateDescriptorSet()
        {
            uint descriptorCount = _sampledPublicationSupported
                ? 2u * SimpleDdgiSampledAtlas.MaxGpuPublishTextureGroups
                : 2u;
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = descriptorCount
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 1
            };
            Result result = _context.Api.CreateDescriptorPool(
                _context.Device,
                &poolInfo,
                null,
                out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish descriptor pool", result);

            DescriptorSetLayout layout = _sampledAtlasSetLayout;
            var allocationInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocationInfo,
                out _sampledAtlasSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate Simple DDGI publish descriptor set", result);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish pipeline cache", result);
        }

        private void CreatePipelineLayout()
        {
            DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
            {
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout,
                _sampledAtlasSetLayout
            };
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Size = (uint)Marshal.SizeOf<GPUSimpleDdgiPublishPushConstants>()
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 3,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };
            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish pipeline layout", result);
        }

        private VkPipeline CreatePipeline(string shaderName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
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
                Result result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException($"Failed to create Simple DDGI publish pipeline '{shaderName}'", result);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void InsertComputeStorageBarrier(CommandBuffer cmd)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                                AccessFlags2.ShaderStorageWriteBit
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependency);
        }
    }

    public abstract unsafe class SimpleDdgiComputePass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint EnabledFlag = 1u << 0;
        private const uint FarFieldEnabledFlag = 1u << 1;
        private const uint FarFieldForceAllFlag = 1u << 2;
        private const uint SharedMemoryBlendEnabledFlag = 1u << 3;
        private const uint ClassificationSchedulingEnabledFlag = 1u << 4;
        private const uint ReducedBlendEnabledFlag = 1u << 5;
        private const uint CompleteRaySceneFlag = 1u << 6;
        private const uint AlphaMaskTransportEnabledFlag = 1u << 7;
        private const uint ThinSurfaceTransmissionEnabledFlag = 1u << 8;

        private readonly string _shaderName;
        private readonly RenderSettings _settings;
        private readonly FarFieldClipmapManager _farFieldClipmapManager;
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

        protected SimpleDdgiComputePass(
            string passName,
            string shaderName,
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager,
            AccelerationStructureManager? accelerationStructureManager,
            bool requiresRayQuery)
            : base(passName, context, swapchain, bindlessHeap)
        {
            _shaderName = shaderName ?? throw new ArgumentNullException(nameof(shaderName));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            VolumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
            _farFieldClipmapManager = farFieldClipmapManager ?? throw new ArgumentNullException(nameof(farFieldClipmapManager));
            _accelerationStructureManager = accelerationStructureManager;
            _requiresRayQuery = requiresRayQuery;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        protected SimpleDdgiVolumeManager VolumeManager { get; }
        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "Simple DDGI update work is compute-only and writes probe buffers.";

        public override void Initialize()
        {
            if (_requiresRayQuery && (!_context.RayQuerySupported || _context.KhrAccelerationStructure == null))
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
            if (_pipeline.Handle == 0)
                return false;
            if (!gi.EffectiveUseSimpleDdgi ||
                !gi.SimpleDdgiStructuredGatherEnabled ||
                !gi.EffectiveUseRayQueryBackend)
                return false;
            if (_requiresRayQuery && (_accelerationStructureManager?.Active != true))
                return false;
            return VolumeManager.ProbeCount > 0 && VolumeManager.ProbesToUpdate > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (_requiresRayQuery)
                UpdateAccelerationStructureDescriptor();

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            if (_requiresRayQuery)
            {
                var asSet = _accelerationStructureSet;
                _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 2, 1, &asSet, 0, null);
            }

            GPUSimpleDdgiPushConstants pushConstants = CreatePushConstants(sceneData);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>(),
                &pushConstants);

            _context.Api.CmdDispatch(cmd, CalculateGroupCount(sceneData), 1, 1);
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

        protected abstract uint CalculateGroupCount(SceneRenderingData sceneData);

        private GPUSimpleDdgiPushConstants CreatePushConstants(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            uint flags = EnabledFlag;
            if (_farFieldClipmapManager.CoverageReady)
                flags |= FarFieldEnabledFlag;
            if (_farFieldClipmapManager.CoverageReady && gi.FarFieldForceAll)
                flags |= FarFieldForceAllFlag;
            if (gi.SimpleDdgiSharedMemoryBlendEnabled)
                flags |= SharedMemoryBlendEnabledFlag;
            if (gi.SimpleDdgiClassificationSchedulingEnabled)
                flags |= ClassificationSchedulingEnabledFlag;
            if (gi.SimpleDdgiReducedBlendEnabled)
                flags |= ReducedBlendEnabledFlag;
            if (gi.DdgiQualityTier is DdgiQualityTier.DdgiHigh or DdgiQualityTier.DdgiUltra)
                flags |= CompleteRaySceneFlag;
            if (gi.DdgiAlphaMaskedTransportEnabled)
                flags |= AlphaMaskTransportEnabledFlag;
            if (gi.SimpleDdgiThinSurfaceTransmissionEnabled)
                flags |= ThinSurfaceTransmissionEnabledFlag;

            return new GPUSimpleDdgiPushConstants
            {
                ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
                IrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
                VisibilityAtlasBufferIndex = BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
                RayResultScratchBufferIndex = BindlessIndex.SimpleDdgiRayResultScratchBuffer,
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                LightCount = checked((uint)Math.Max(0, sceneData.LightCount)),
                DirectionalLightCount = checked((uint)Math.Max(0, sceneData.DirectionalLightCount)),
                LocalLightCount = checked((uint)Math.Max(0, sceneData.LocalLightCount)),
                MaxShadedLights = checked((uint)Math.Clamp(sceneData.DdgiEffectiveMaxShadedLights > 0 ? sceneData.DdgiEffectiveMaxShadedLights : gi.DdgiMaxShadedLights, 0, 64)),
                EmissiveSourceCount = checked((uint)Math.Max(0, sceneData.DdgiEmissiveSourceCount)),
                FarFieldParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                FarFieldVoxelBufferIndex = BindlessIndex.FarFieldClipmapVoxelBuffer,
                FarFieldInstanceBufferIndex = BindlessIndex.FarFieldClipmapInstanceBuffer,
                Flags = flags,
                MaterialTextureMaxCascade = gi.DdgiMaterialTextureMaxCascade < 0
                    ? GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount
                    : checked((uint)Math.Clamp(gi.DdgiMaterialTextureMaxCascade, 0, GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount - 1)),
                ProbeStateBufferIndex = BindlessIndex.SimpleDdgiProbeStateBuffer,
                ProbeUpdateQueueBufferIndex = BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
                RelocationClassificationBufferIndex = BindlessIndex.SimpleDdgiRelocationClassificationBuffer,
                TransportSourceCacheBufferIndex = BindlessIndex.SimpleDdgiTransportSourceCacheBuffer,
                TransportReadIrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
                TransportWriteIrradianceAtlasBufferIndex = gi.SimpleDdgiTransportV2Enabled
                    ? checked((uint)BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer)
                    : checked((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                TransportGeneration = VolumeManager.TransportGeneration,
                PrimaryDirectionalLightIndex = sceneData.DdgiPrimaryDirectionalLightIndex < 0
                    ? uint.MaxValue
                    : checked((uint)sceneData.DdgiPrimaryDirectionalLightIndex)
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
                throw new VulkanException("Failed to create simple DDGI acceleration-structure descriptor set layout", result);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {Name} pipeline cache", result);
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
                    Size = (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>()
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
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, _shaderName);
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
                throw new VulkanException("Failed to create simple DDGI descriptor pool", result);

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
                throw new VulkanException("Failed to allocate simple DDGI acceleration-structure descriptor set", result);
        }

        private void UpdateAccelerationStructureDescriptor()
        {
            if (_accelerationStructureManager == null)
                throw new InvalidOperationException("Simple DDGI trace requires an acceleration structure manager.");

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
            // This pass may execute on a compute-only queue. Publish only to
            // subsequent compute dispatches here; graphics consumers establish
            // their fragment visibility at the forward-pass boundary.
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                                AccessFlags2.ShaderStorageWriteBit
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
