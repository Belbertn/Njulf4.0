using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed class SkinningGraphPass : RenderPassBase
    {
        private readonly SkinningPass _implementation;

        public SkinningGraphPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            SkinningPass implementation)
            : base("SkinningPass", context, swapchain, bindlessHeap)
        {
            _implementation = implementation ?? throw new ArgumentNullException(nameof(implementation));
        }

        public override void Initialize()
        {
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle matrices = ProductionRenderGraphResources.SkinMatrixBuffer(resources);
            RenderGraphResourceHandle dispatches = ProductionRenderGraphResources.SkinningDispatchBuffer(resources);
            RenderGraphResourceHandle skinnedVertices = ProductionRenderGraphResources.SkinnedVertexBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Compute)
            {
                AsyncEligible = true,
                PreferredQueue = RenderGraphQueueClass.Compute,
                ExpectedWorkloadScore = 180,
                DependencyUrgency = RenderGraphDependencyUrgency.ImmediateGraphicsConsumer,
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true
            }.SupportsQueue(RenderGraphQueueClass.Graphics)
                .Read(matrices, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit)
                .Read(dispatches, RenderGraphResourceAccess.StorageRead, PipelineStageFlags2.ComputeShaderBit)
                .Write(skinnedVertices, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.AnimationEnabled && sceneData.SkinningDispatchCount > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            _implementation.Execute(cmd, frameIndex, sceneData);
        }
    }
}
