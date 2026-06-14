using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed class ParticleSimulationPass : RenderPassBase
    {
        private readonly ParticleSettings _settings;

        public ParticleSimulationPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            ParticleSettings settings)
            : base("ParticleSimulationPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
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
                .Write(particleBatches, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.ParticlesEnabled &&
                   (sceneData.RenderedParticleCount > 0 || sceneData.ParticleBatchCount > 0);
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            sceneData.ParticleSimulationMode = ParticleSimulationMode.Gpu;
        }
    }
}
