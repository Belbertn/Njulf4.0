using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

public sealed unsafe partial class ForwardPlusPass
{
    private bool _recordingAutomaticPlanarDepthPrepass;

    private void DrawAutomaticPlanarOpaque(CommandBuffer cmd, SceneRenderingData sceneData)
    {
        bool simpleEligible = !_recordingAutomaticPlanarDepthPrepass &&
                              ResolveOpaqueVariantSelection(sceneData).UseSimpleGlobalIblPipeline;
        bool taskless = _meshPipeline.TasklessSubmissionEnabled;
        DrawForwardBucket(cmd, sceneData, AutomaticPlanarCapturePipelineBank.ResolveFamily(
                MaterialForwardClass.SimpleOpaque, simpleEligible, taskless),
            sceneData.AutomaticPlanarSimpleMeshletCount, BindlessIndex.MeshletDrawBufferBase);
        DrawForwardBucket(cmd, sceneData, AutomaticPlanarCapturePipelineBank.ResolveFamily(
                MaterialForwardClass.SimpleOpaqueNormal, simpleEligible, taskless),
            sceneData.AutomaticPlanarSimpleFullInputMeshletCount, BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase);
        DrawForwardBucket(cmd, sceneData, AutomaticPlanarCapturePipelineBank.ResolveFamily(
                MaterialForwardClass.FullOpaque, simpleEligible, taskless),
            sceneData.AutomaticPlanarFullMeshletCount, BindlessIndex.FullOpaqueMeshletDrawBufferBase);
    }

    private void RecordAutomaticPlanarDepthPrepass(
        CommandBuffer cmd, SceneRenderingData sceneData, ref RenderingInfo renderingInfo)
    {
        if (!_meshPipeline.AutomaticPlanarDepthPrepassEnabled)
            return;

        RenderingInfo depthInfo = renderingInfo;
        depthInfo.ColorAttachmentCount = 0;
        depthInfo.PColorAttachments = null;
        _context.KhrDynamicRendering.CmdBeginRendering(cmd, &depthInfo);
        _recordingAutomaticPlanarDepthPrepass = true;
        try
        {
            DrawAutomaticPlanarOpaque(cmd, sceneData);
        }
        finally
        {
            _recordingAutomaticPlanarDepthPrepass = false;
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit |
                            AccessFlags.DepthStencilAttachmentWriteBit
        };
        PipelineStageFlags depthStages = PipelineStageFlags.EarlyFragmentTestsBit |
                                         PipelineStageFlags.LateFragmentTestsBit;
        _context.Api.CmdPipelineBarrier(cmd, depthStages, depthStages, 0,
            1, &barrier, 0, null, 0, null);
        renderingInfo.PDepthAttachment->LoadOp = AttachmentLoadOp.Load;
        sceneData.DepthPrePassEnabled = true;
    }
}
