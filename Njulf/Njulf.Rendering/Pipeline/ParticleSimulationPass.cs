using System;
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
    public sealed unsafe class ParticleSimulationPass : RenderPassBase
    {
        private const string EntryPoint = "main";

        private readonly ParticleSettings _settings;
        private readonly ParticleSystemManager _particleSystemManager;
        private readonly nint _entryPointName;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public ParticleSimulationPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            ParticleSettings settings,
            ParticleSystemManager particleSystemManager)
            : base("ParticleSimulationPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _particleSystemManager = particleSystemManager ?? throw new ArgumentNullException(nameof(particleSystemManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override void Initialize()
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle particleInstances = ProductionRenderGraphResources.ParticleInstanceBuffer(resources);
            RenderGraphResourceHandle particleBatches = ProductionRenderGraphResources.ParticleBatchBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Compute)
            {
                AsyncEligible = true,
                PreferredQueue = RenderGraphQueueClass.Compute,
                ExpectedWorkloadScore = 220,
                DependencyUrgency = RenderGraphDependencyUrgency.LongIndependentWork,
                TimingLabel = Name,
                IsEnabled = _settings.Enabled,
                HasExternalSideEffect = true,
                NeverCull = true
            }.SupportsQueue(RenderGraphQueueClass.Graphics)
                .Write(particleInstances, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
                .Read(particleBatches, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.ParticlesEnabled &&
                   sceneData.ParticleSimulationMode == ParticleSimulationMode.Gpu &&
                   _particleSystemManager.GpuEmitterCount > 0 &&
                   (sceneData.RenderedParticleCount > 0 || sceneData.ParticleBatchCount > 0);
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            sceneData.ParticleSimulationMode = ParticleSimulationMode.Gpu;
            _particleSystemManager.DispatchGpuSimulation(
                cmd,
                _bindlessHeap,
                _pipelineLayout,
                _pipeline,
                frameIndex,
                sceneData.Time,
                0.0f);
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

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create particle simulation pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Particle Simulation Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUParticleSimulationPushConstants>()
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
                throw new VulkanException("Failed to create particle simulation pipeline layout", result);
            _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "Particle Simulation Pipeline Layout");
        }

        private void CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "particle_simulation.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "particle_simulation.comp.spv");

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
                    out _pipeline);

                if (result != Result.Success)
                    throw new VulkanException("Failed to create particle simulation compute pipeline", result);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "ParticleSimulationPass");
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }
    }
}
