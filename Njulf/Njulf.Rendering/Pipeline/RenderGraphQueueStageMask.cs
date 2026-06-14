using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

internal static class RenderGraphQueueStageMask
{
    public static PipelineStageFlags2 Sanitize(
        PipelineStageFlags2 stageMask,
        AccessFlags2 accessMask,
        RenderGraphQueueClass queue)
    {
        if (stageMask == PipelineStageFlags2.None || queue == RenderGraphQueueClass.Graphics)
            return stageMask;

        PipelineStageFlags2 allowed = queue == RenderGraphQueueClass.Compute
            ? PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit | PipelineStageFlags2.AllCommandsBit
            : PipelineStageFlags2.TransferBit | PipelineStageFlags2.AllCommandsBit;

        if ((stageMask & PipelineStageFlags2.AllCommandsBit) != 0)
            return PipelineStageFlags2.AllCommandsBit;

        PipelineStageFlags2 filtered = stageMask & allowed;
        if (filtered != PipelineStageFlags2.None)
            return filtered;

        return Fallback(accessMask, queue);
    }

    private static PipelineStageFlags2 Fallback(AccessFlags2 accessMask, RenderGraphQueueClass queue)
    {
        if ((accessMask & (AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit)) != 0)
            return PipelineStageFlags2.TransferBit;

        if (queue == RenderGraphQueueClass.Compute &&
            (accessMask & (
                AccessFlags2.ShaderSampledReadBit |
                AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.UniformReadBit)) != 0)
        {
            return PipelineStageFlags2.ComputeShaderBit;
        }

        return queue == RenderGraphQueueClass.Transfer
            ? PipelineStageFlags2.TransferBit
            : PipelineStageFlags2.AllCommandsBit;
    }
}
