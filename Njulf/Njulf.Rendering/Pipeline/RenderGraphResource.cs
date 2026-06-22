using System;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public enum RenderGraphResourceId
    {
        SceneColor,
        LdrSceneColor,
        SceneDepth,
        MotionVectors,
        BloomChain,
        AmbientOcclusionRaw,
        AmbientOcclusionBlurred,
        AmbientOcclusionScratch,
        SceneNormal,
        SceneMaterial,
        SsgiTraceSource,
        SsgiRaw,
        SsgiFiltered,
        SsgiHistory,
        SsgiDepthHistory,
        SsgiNormalHistory,
        GiFinalDiffuse,
        DdgiProbeResources,
        FogOutput,
        DirectionalShadowMap,
        SpotShadowAtlas,
        PointShadowCubemapArray,
        HiZPyramid,
        ParticleBuffers,
        GpuParticleBuffers,
        FoliageBuffers,
        SceneSubmissionBuffers,
        SkinningBuffers,
        LightTiles,
        SwapchainColor,
        SmaaEdges,
        SmaaBlendWeights,
        TaaHistory,
        WeightedOitAccumulation,
        WeightedOitRevealage,
        ReflectionProbeCubemaps,
        EnvironmentMaps,
        TransientIntermediate
    }

    public enum RenderGraphResourceKind
    {
        Image,
        ImageChain,
        Buffer,
        BufferSet,
        External
    }

    public enum RenderGraphResourceSizePolicy
    {
        Swapchain,
        SceneResolution,
        HalfResolution,
        BloomMipChain,
        ShadowMap,
        Fixed,
        Dynamic,
        External
    }

    public enum RenderGraphResourceLifetime
    {
        Imported,
        Persistent,
        Transient
    }

    public enum RenderGraphResourceAccess
    {
        Read,
        Write,
        ReadWrite
    }

    public enum RenderGraphQueueIntent
    {
        Graphics,
        Compute,
        Transfer,
        External
    }

    public sealed record RenderGraphResourceDescriptor(
        RenderGraphResourceId Id,
        string DebugName,
        RenderGraphResourceKind Kind,
        Format? Format,
        RenderGraphResourceSizePolicy SizePolicy,
        RenderGraphResourceLifetime Lifetime,
        bool Persistent)
    {
        public RenderGraphResourceDescriptor Validate()
        {
            if (string.IsNullOrWhiteSpace(DebugName))
                throw new ArgumentException("Resource debug name is required.", nameof(DebugName));
            if ((Kind == RenderGraphResourceKind.Image || Kind == RenderGraphResourceKind.ImageChain) && !Format.HasValue)
                throw new ArgumentException("Image graph resources require a format.", nameof(Format));
            if ((Kind == RenderGraphResourceKind.Buffer || Kind == RenderGraphResourceKind.BufferSet || Kind == RenderGraphResourceKind.External) && Format.HasValue)
                throw new ArgumentException("Non-image graph resources cannot declare an image format.", nameof(Format));
            if (Lifetime == RenderGraphResourceLifetime.Imported && !Persistent)
                throw new ArgumentException("Imported graph resources must be persistent.", nameof(Persistent));
            if (Lifetime == RenderGraphResourceLifetime.Transient && Persistent)
                throw new ArgumentException("Transient graph resources cannot be persistent.", nameof(Persistent));

            return this;
        }
    }

    public readonly record struct RenderGraphResourceUsage(
        RenderGraphResourceId Resource,
        RenderGraphResourceAccess Access,
        PipelineStageFlags2 StageMask = PipelineStageFlags2.None,
        AccessFlags2 AccessMask = AccessFlags2.None,
        ImageLayout ImageLayout = ImageLayout.Undefined,
        RenderGraphQueueIntent QueueIntent = RenderGraphQueueIntent.Graphics);

    public readonly record struct RenderGraphPlannedBarrier(
        string PassName,
        RenderGraphResourceId Resource,
        RenderGraphResourceAccess PreviousAccess,
        RenderGraphResourceAccess NextAccess,
        ImageLayout OldLayout,
        ImageLayout NewLayout,
        PipelineStageFlags2 SourceStage,
        AccessFlags2 SourceAccess,
        PipelineStageFlags2 DestinationStage,
        AccessFlags2 DestinationAccess,
        RenderGraphQueueIntent PreviousQueueIntent,
        RenderGraphQueueIntent QueueIntent,
        bool QueueOwnershipTransition,
        bool Executed);
}
