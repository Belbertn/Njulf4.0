using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>Pure synchronization2 queue/stage/access validation.</summary>
public readonly record struct QueueStageCapabilities(QueueFlags QueueFlags)
{
    private const PipelineStageFlags2 UniversalStages =
        PipelineStageFlags2.TopOfPipeBit |
        PipelineStageFlags2.BottomOfPipeBit |
        PipelineStageFlags2.HostBit |
        PipelineStageFlags2.TransferBit |
        PipelineStageFlags2.AllCommandsBit;

    private const PipelineStageFlags2 GraphicsStages =
        PipelineStageFlags2.DrawIndirectBit |
        PipelineStageFlags2.IndexInputBit |
        PipelineStageFlags2.VertexAttributeInputBit |
        PipelineStageFlags2.VertexShaderBit |
        PipelineStageFlags2.TaskShaderBitExt |
        PipelineStageFlags2.MeshShaderBitExt |
        PipelineStageFlags2.FragmentShaderBit |
        PipelineStageFlags2.EarlyFragmentTestsBit |
        PipelineStageFlags2.LateFragmentTestsBit |
        PipelineStageFlags2.ColorAttachmentOutputBit;

    public PipelineStageFlags2 SupportedStages
    {
        get
        {
            PipelineStageFlags2 stages = UniversalStages;
            if ((QueueFlags & QueueFlags.ComputeBit) != 0)
                stages |= PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.AccelerationStructureBuildBitKhr;
            if ((QueueFlags & QueueFlags.GraphicsBit) != 0)
                stages |= GraphicsStages | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.AccelerationStructureBuildBitKhr;
            return stages;
        }
    }

    public bool SupportsStages(PipelineStageFlags2 stages) =>
        stages == PipelineStageFlags2.None || (stages & ~SupportedStages) == 0;

    public bool SupportsScope(PipelineStageFlags2 stages, AccessFlags2 access, bool ignoredScope = false)
    {
        if (ignoredScope)
            return stages == PipelineStageFlags2.None && access == AccessFlags2.None;
        if (stages == PipelineStageFlags2.None || access == AccessFlags2.None || !SupportsStages(stages))
            return false;
        if ((stages & PipelineStageFlags2.AllCommandsBit) != 0)
            return true;

        if ((access & (AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit)) != 0 &&
            (stages & PipelineStageFlags2.ColorAttachmentOutputBit) == 0)
            return false;
        if ((access & (AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit)) != 0 &&
            (stages & (PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit)) == 0)
            return false;
        if ((access & AccessFlags2.IndexReadBit) != 0 && (stages & PipelineStageFlags2.IndexInputBit) == 0)
            return false;
        if ((access & AccessFlags2.VertexAttributeReadBit) != 0 && (stages & PipelineStageFlags2.VertexAttributeInputBit) == 0)
            return false;
        if ((access & AccessFlags2.IndirectCommandReadBit) != 0 && (stages & PipelineStageFlags2.DrawIndirectBit) == 0)
            return false;
        if ((access & (AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit)) != 0 &&
            (stages & PipelineStageFlags2.TransferBit) == 0)
            return false;
        if ((access & (AccessFlags2.HostReadBit | AccessFlags2.HostWriteBit)) != 0 &&
            (stages & PipelineStageFlags2.HostBit) == 0)
            return false;

        AccessFlags2 shaderAccess = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit |
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit;
        PipelineStageFlags2 shaderStages = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt | PipelineStageFlags2.FragmentShaderBit;
        if ((access & shaderAccess) != 0 && (stages & shaderStages) == 0)
            return false;

        return true;
    }
}
