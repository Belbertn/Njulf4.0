using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects;

public sealed unsafe partial class FoliagePipeline
{
    private readonly VkPipeline[,] _automaticPlanarCapturePipelines = new VkPipeline[2, 2];

    internal void PrepareAutomaticPlanarCapturePipelines()
    {
        for (int authored = 0; authored < 2; authored++)
        for (int feedback = 0; feedback < (_receiverFeedbackPipelinesEnabled ? 2 : 1); feedback++)
        {
            if (_automaticPlanarCapturePipelines[authored, feedback].Handle != 0)
                continue;
            VkPipeline pipeline = CreateGraphicsPipeline(null,
                authored == 0
                    ? (feedback == 0 ? "foliage_grass_compacted.mesh.spv" : "foliage_grass_b1_compacted.mesh.spv")
                    : (feedback == 0 ? "foliage_mesh_compacted.mesh.spv" : "foliage_mesh_b1_compacted.mesh.spv"),
                feedback == 0 ? "foliage_forward_ddgi.frag.spv" : "foliage_forward_ddgi_b1.frag.spv",
                _colorFormat, _depthFormat, hasColorAttachment: true, depthWriteEnable: true);
            _automaticPlanarCapturePipelines[authored, feedback] = pipeline;
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline,
                $"Secondary Capture Foliage Depth Write A{authored} F{feedback}");
        }
    }

    internal bool TryResolveAutomaticPlanarCapturePipeline(
        bool authored, bool receiverFeedback, out VkPipeline pipeline)
    {
        pipeline = _automaticPlanarCapturePipelines[authored ? 1 : 0, receiverFeedback ? 1 : 0];
        return pipeline.Handle != 0;
    }

    private void DestroyAutomaticPlanarCapturePipelines()
    {
        for (int authored = 0; authored < 2; authored++)
        for (int feedback = 0; feedback < 2; feedback++)
            DestroyPipeline(ref _automaticPlanarCapturePipelines[authored, feedback]);
    }
}
