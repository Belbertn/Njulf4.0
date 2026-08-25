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
        MaterialTransportProvenance,
        DdgiProbeResources,
        TlasStorage,
        RayQueryInstanceMetadata,
        MeshGeometryBuffers,
        MaterialBuffers,
        MaterialTextures,
        LightBuffers,
        EnvironmentData,
        RendererDiagnosticsBuffer,
        DdgiEmissiveSources,
        FarFieldParameters,
        FarFieldVoxels,
        FarFieldInstances,
        FarFieldJumpFlood,
        FarFieldPageTable,
        SimpleDdgiParameters,
        SimpleDdgiIrradianceAtlas,
        SimpleDdgiTransportAtlas,
        SimpleDdgiTransportSourceCache,
        SimpleDdgiVisibilityAtlas,
        SimpleDdgiRayScratch,
        SimpleDdgiProbeState,
        SimpleDdgiUpdateQueue,
        SimpleDdgiRelocationData,
        FogOutput,
        DirectionalShadowMap,
        SpotShadowAtlas,
        PointShadowCubemapArray,
        HiZPyramid,
        ParticleBuffers,
        GpuParticleBuffers,
        GpuParticleState,
        GpuParticleIndices,
        GpuParticleEmitterData,
        GpuParticleCounters,
        GpuParticleUnsortedOutput,
        GpuParticleRenderOutput,
        GpuParticleIndirectArguments,
        GpuParticleSortKeys,
        GpuParticleCounterReadback,
        FoliageBuffers,
        SceneSubmissionBuffers,
        ForwardVisibilityBuffers,
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
        TransientIntermediate,
        // Persistent GPU-resident Simple-DDGI scheduler arena. Appended to keep
        // existing graph resource identities stable for capture compatibility.
        SimpleDdgiScheduler,
        // Receiver-only compact probe publication. Keep it distinct from the
        // compute state so async ownership and visibility barriers match the
        // buffer actually consumed by forward/fog/particle shaders.
        SimpleDdgiReceiverProbes,
        // Virtual-page residency arena. Appended to preserve graph-resource
        // identities used by captures and serialized diagnostics.
        SimpleDdgiResidency,
        // Double-buffered local-light hierarchy, publication state, and build
        // scratch. Appended to preserve all prior capture identities.
        SimpleDdgiLightTree,
        // Canonical directional incident-radiance SH and optional Jacobi
        // parity. Appended to retain all existing capture identities.
        SimpleDdgiDirectionalRadiance,
        // Compact stable foliage work records written by upload and consumed
        // by the externally recorded compute-generation prelude.
        DdgiFoliageProxyPatches,
        // Frame-slot compute output consumed by procedural foliage BLAS builds.
        DdgiFoliageProxyGeometry,
        // Frame-slot current-pose/proxy BLAS storage published into the TLAS.
        DynamicBlasStorage,
        // Optional advanced-GI resources are appended and registered only by
        // an effective experimental graph variant.  This preserves the
        // shipping graph/capture identities and keeps disabled modes at zero
        // resource, descriptor, and pass cost.
        SimpleDdgiReceiverFeedbackRecords,
        SimpleDdgiReceiverFeedbackSortScratch,
        SimpleDdgiReceiverFeedbackSummaries,
        OpacityMicromapResources,
        SimpleDdgiGuidingDistributions,
        SimpleDdgiGuidingScratch,
        GiCausticTasks,
        GiCausticPhotons,
        GiCausticCache,
        GiCausticScratch,
        NearFieldDirectSource,
        NearFieldResidualRaw,
        NearFieldResidualHitMetadata,
        NearFieldResidualHistory,
        NearFieldResidualMoments,
        NearFieldResidualValidity,
        NearFieldResidualFilterScratch,
        NearFieldResidualTileBuffers,
        // C3's generation-time direction/PDF payload is owned by the
        // source-cache ABI, not by the guiding distribution allocator.  It is
        // appended here so the graph can prove the sample-to-trace dependency
        // without making it part of C3's persistent memory category.
        SimpleDdgiGuidingDirectionPayloadSidecar,
        // C5 retains a separate, double-buffered hit/receiver identity image
        // alongside radiance, moments, and validity.  It is appended to retain
        // all established render-graph resource IDs.
        NearFieldResidualHistoryMetadata,
        // C5 input/output resources are appended to preserve pre-existing
        // capture identities. They are registered only for an effective C5
        // graph and each has one unambiguous shader ABI role.
        NearFieldReceiverPayload,
        // Reserved legacy C5 prototype IDs. They are never registered by the
        // V5 compact-payload graph, but retaining their numeric positions keeps
        // older diagnostic captures readable.
        NearFieldReceiverProjectedRays,
        NearFieldReceiverIdentity,
        NearFieldReceiverDiffuseBounceThroughput,
        NearFieldResidualHistoryNormals,
        // C1 resident objects and its two transient build ranges have
        // different ownership/lifetimes.  Keep the original resource ID as
        // the resident publication and append the scratch identities so
        // capture IDs remain stable.
        OpacityMicromapBuildScratch,
        OpacityMicromapCompactionHeadroom,
        // Per-frame immutable reconstruction matrices. Appended to preserve
        // all capture/resource IDs established by prior schemas.
        NearFieldResidualTraceFrameConstants,
        // C4 screen integration is append-only for capture compatibility.
        // The forward payload contains current receiver material/normal data;
        // the radiance target retains separately-owned C4 energy until its
        // explicit composite pass. Frame matrices are immutable per dispatch.
        GiCausticReceiverPayload,
        GiCausticRadiance,
        GiCausticMoments,
        GiCausticScreenFrameConstants,
        // Double-buffered full-resolution packed R8-unorm visibility output
        // for deterministic directional ray shadows.
        DirectionalRayShadowMask,
        DirectionalShadowRaw,
        DirectionalShadowHistory,
        DirectionalShadowScratch,
        DirectionalShadowDiagnostics,
        DirectionalShadowCounters,
        // C5 V12 append-only resources.
        NearFieldPreparedDepthFootprint,
        NearFieldPreparedReceiverPayload,
        NearFieldPreparedMotion,
        NearFieldSourceLuminance,
        NearFieldSurfaceTable,
        NearFieldActiveTilesAndIndirectArguments,
        // P0 hybrid-reflection resources are append-only for capture compatibility.
        HybridReflectionReceiverPayload,
        HybridReflectionRawRadiance,
        HybridReflectionHistory,
        HybridReflectionMoments,
        HybridReflectionHistoryMetadata,
        HybridReflectionFilterScratch,
        HybridReflectionRayTasks,
        HybridReflectionCounters,
        HybridReflectionIndirectArguments,
        // Appended after the original P0 range to preserve capture IDs.
        HybridReflectionRawMetadata,
        // Full-resolution packed visibility for up to four scheduled area lights.
        AreaRayShadowMask,
        // Sparse per-tile DDGI cohort records. Appended to preserve capture IDs.
        HybridReflectionDdgiCohorts
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
        QuarterResolution,
        EighthResolution,
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

    /// <summary>
    /// Selects one physical member of an <see cref="RenderGraphResourceKind.ImageChain"/>
    /// or buffer/image set.  A logical history resource is not safe to declare as
    /// read/write when a temporal pass needs distinct previous and current banks:
    /// callers must make the two accesses explicit with <see cref="Previous"/>
    /// and <see cref="Current"/>.  The current/previous mapping is deterministic
    /// frame-index parity (current = frameIndex mod 2).
    /// </summary>
    public enum RenderGraphHistoryBindingSelection : byte
    {
        /// <summary>Use every concrete binding, preserving legacy set semantics.</summary>
        All = 0,
        /// <summary>Use the history bank written by the current frame.</summary>
        Current = 1,
        /// <summary>Use the other history bank, written by the previous frame.</summary>
        Previous = 2,
        /// <summary>Use physical history bank zero.</summary>
        Bank0 = 3,
        /// <summary>Use physical history bank one.</summary>
        Bank1 = 4
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
        RenderGraphQueueIntent QueueIntent = RenderGraphQueueIntent.Graphics,
        /// <summary>
        /// The layout a pass leaves behind after its internal transitions. This is distinct from
        /// <see cref="ImageLayout"/>, which describes the layout required when graph execution
        /// enters the pass. Queue handoffs use this value for their release barrier.
        /// </summary>
        ImageLayout FinalImageLayout = ImageLayout.Undefined,
        /// <summary>
        /// Physical history-bank selection for this usage.  The default preserves
        /// existing resources that intentionally expose every binding.
        /// </summary>
        RenderGraphHistoryBindingSelection HistoryBinding =
            RenderGraphHistoryBindingSelection.All);

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
        bool Executed,
        int HistoryIndex = -1);
}
