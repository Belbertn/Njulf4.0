using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

public enum ReflectionProbeCaptureGraphResource : byte
{
    ScratchRadiance,
    CaptureDepth,
    CaptureSnapshotBuffer,
    PublishedCubemapArray
}

public readonly record struct ReflectionProbeCaptureGraphUsage(
    ReflectionProbeCaptureGraphResource Resource,
    RenderGraphResourceAccess Access,
    RenderGraphQueueIntent Queue,
    PipelineStageFlags2 StageMask,
    AccessFlags2 AccessMask,
    ImageLayout RequiredLayout,
    ImageLayout FinalLayout);

public readonly record struct ReflectionProbeCaptureGraphPass(
    string Name,
    RenderGraphQueueIntent Queue,
    bool SupportsAsyncCompute,
    IReadOnlyList<ReflectionProbeCaptureGraphUsage> Usages);

/// <summary>
/// Conditional graph contract for the reflection transaction. The main production graph retains
/// its compatibility pass order; these declarations are executed only when authored captures have
/// work and make the private scratch/depth/publish dependencies inspectable and testable.
/// </summary>
public static class ReflectionProbeCaptureGraphDeclaration
{
    private static readonly IReadOnlyList<ReflectionProbeCaptureGraphPass> Passes =
    [
        new ReflectionProbeCaptureGraphPass(
            "ReflectionProbeCapturePass",
            RenderGraphQueueIntent.Graphics,
            SupportsAsyncCompute: false,
            new[]
            {
                Usage(
                    ReflectionProbeCaptureGraphResource.ScratchRadiance,
                    RenderGraphResourceAccess.Write,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AccessFlags2.ColorAttachmentWriteBit,
                    ImageLayout.ColorAttachmentOptimal,
                    ImageLayout.ShaderReadOnlyOptimal),
                Usage(
                    ReflectionProbeCaptureGraphResource.CaptureDepth,
                    RenderGraphResourceAccess.Write,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                    AccessFlags2.DepthStencilAttachmentWriteBit,
                    ImageLayout.DepthStencilAttachmentOptimal,
                    ImageLayout.DepthStencilAttachmentOptimal),
                Usage(
                    ReflectionProbeCaptureGraphResource.CaptureSnapshotBuffer,
                    RenderGraphResourceAccess.Read,
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit,
                    ImageLayout.Undefined,
                    ImageLayout.Undefined)
            }),
        new ReflectionProbeCaptureGraphPass(
            "ReflectionProbePrefilterPass",
            RenderGraphQueueIntent.Graphics,
            SupportsAsyncCompute: false,
            new[]
            {
                Usage(
                    ReflectionProbeCaptureGraphResource.ScratchRadiance,
                    RenderGraphResourceAccess.ReadWrite,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    ImageLayout.ShaderReadOnlyOptimal)
            }),
        new ReflectionProbeCaptureGraphPass(
            "ReflectionProbePublishPass",
            RenderGraphQueueIntent.Graphics,
            SupportsAsyncCompute: false,
            new[]
            {
                Usage(
                    ReflectionProbeCaptureGraphResource.ScratchRadiance,
                    RenderGraphResourceAccess.Read,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    ImageLayout.TransferSrcOptimal,
                    ImageLayout.ShaderReadOnlyOptimal),
                Usage(
                    ReflectionProbeCaptureGraphResource.PublishedCubemapArray,
                    RenderGraphResourceAccess.Write,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    ImageLayout.TransferDstOptimal,
                    ImageLayout.ShaderReadOnlyOptimal)
            })
    ];

    public static IReadOnlyList<ReflectionProbeCaptureGraphPass> PassDeclarations => Passes;

    public static bool IsGraphicsOnly => true;

    public static bool Validate()
    {
        if (Passes.Count != 3)
            return false;
        for (int passIndex = 0; passIndex < Passes.Count; passIndex++)
        {
            ReflectionProbeCaptureGraphPass pass = Passes[passIndex];
            if (pass.Queue != RenderGraphQueueIntent.Graphics || pass.SupportsAsyncCompute ||
                string.IsNullOrWhiteSpace(pass.Name) || pass.Usages.Count == 0)
                return false;
            for (int usageIndex = 0; usageIndex < pass.Usages.Count; usageIndex++)
            {
                ReflectionProbeCaptureGraphUsage usage = pass.Usages[usageIndex];
                if (usage.StageMask == PipelineStageFlags2.None || usage.AccessMask == AccessFlags2.None ||
                    usage.Queue != RenderGraphQueueIntent.Graphics)
                    return false;
            }
        }
        return true;
    }

    private static ReflectionProbeCaptureGraphUsage Usage(
        ReflectionProbeCaptureGraphResource resource,
        RenderGraphResourceAccess access,
        PipelineStageFlags2 stageMask,
        AccessFlags2 accessMask,
        ImageLayout requiredLayout,
        ImageLayout finalLayout) =>
        new(resource, access, RenderGraphQueueIntent.Graphics, stageMask, accessMask, requiredLayout, finalLayout);
}
