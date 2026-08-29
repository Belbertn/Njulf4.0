using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

internal sealed class ProductionRenderPipelineDeclaration
{
    public const string PipelineName = "Production";

    public static ProductionRenderPipelineDeclaration Instance { get; } = new();

    private readonly IReadOnlyList<string> _passOrder =
    [
        "SceneOpaqueCompactionPass",
        "DirectionalShadowPass",
        "SpotShadowPass",
        "PointShadowPass",
        "DepthPrePass",
        "MotionVectorPass",
        "HiZBuildPass",
        "DirectionalRayShadowPass",
        "AreaRayShadowPass",
        "DirectionalShadowTemporalPass",
        "DirectionalShadowSpatialPass",
        "ForwardVisibilityCompactionPass",
        "AmbientOcclusionPass",
        "AmbientOcclusionBlurPass",
        "GtaoPass",
        "GtaoTemporalPass",
        "GtaoSpatialPass",
        "TiledLightCullingPass",
        "SimpleDdgiLightTreePass",
        "EnvironmentPrefilterPass",
        "SimpleDdgiUrgentRelightPass",
        "VariableRateShadingPass",
        "ForwardPlusPass",
        "SimpleDdgiPageDemandPass",
        "SimpleDdgiPageResidencyPass",
        "FarFieldClipmapBakePass",
        "SimpleDdgiSchedulePass",
        "SimpleDdgiTracePass",
        "SimpleDdgiRelocateClassifyPass",
        "SimpleDdgiAcceleratedSolvePass",
        "SimpleDdgiTransportPass",
        "SimpleDdgiBlendPass",
        "SimpleDdgiDirectionalRadiancePass",
        "SimpleDdgiPublishPass",
        "SimpleDdgiTransportAuditPass",
        "SimpleDdgiSchedulerCommitPass",
        "SimpleDdgiPageFeedbackPass",
        "SkyboxPass",
        "HybridReflectionSsrPass",
        "HybridReflectionRayQueryPass",
        "HybridReflectionDdgiBasePass",
        "HybridReflectionResolvePass",
        "HybridReflectionTemporalPass",
        "HybridReflectionSpatialPass",
        "HybridReflectionCompositePass",
        "OpaqueSceneColorSnapshotPass",
        "TransparentForwardPass",
        "WeightedTransparentPass",
        "WeightedOitCompositePass",
        "GpuParticleResetPass",
        "GpuParticleSimulatePass",
        "GpuParticleSortPass",
        "ParticlePass",
        "SimpleDdgiProbeDebugPass",
        "DebugDrawPass",
        "DebugOverlayPass",
        "FogPass",
        "AutoExposurePass",
        "BloomPass",
        "ToneMapCompositePass",
        "AntiAliasingPass",
        "ImGuiRenderPass"
    ];

    private ProductionRenderPipelineDeclaration()
    {
    }

    public IReadOnlyList<string> PassOrder => _passOrder;

    /// <summary>
    /// Builds the concrete pass order for a transactionally admitted advanced
    /// GI mode set.  The default production declaration remains byte-for-byte
    /// free of experimental work; callers must pass effective, rather than
    /// requested, modes to opt into this variant.
    /// </summary>
    public IReadOnlyList<string> CreatePassOrder(in AdvancedGiRenderGraphModes modes)
    {
        if (!modes.HasGpuFeature)
            return PassOrder;

        var order = new List<string>(_passOrder);
        // C1 is recorded in the acceleration-structure prelude because Vulkan
        // micromap/BLAS commands cannot be emitted by a secondary graph pass.
        // Its logical position is exposed by the mode-aware externally
        // recorded declaration below, never by a no-op graph placeholder.
        // B1 is not a graph variant. Its candidate writes are part of the
        // concrete opaque/alpha/transparent/particle/fog/reflection receiver
        // passes and VulkanRenderer records the bounded sort/reduce transaction
        // only after every required producer has closed. Do not advertise
        // synthetic pass names that have no RenderPassBase implementation.
        if (modes.UsesDirectionalGuiding)
        {
            InsertAfter(order, "SimpleDdgiSchedulePass",
                SimpleDdgiGuidingGpuPassNames.Sample);
            InsertAfter(order, "SimpleDdgiTracePass",
                SimpleDdgiGuidingGpuPassNames.Train,
                SimpleDdgiGuidingGpuPassNames.Build,
                SimpleDdgiGuidingGpuPassNames.Validate);
        }

        var latePasses = new List<string>();
        if (modes.UsesCausticWorldCache)
        {
            latePasses.Add("GiCausticTaskPass");
            latePasses.Add("GiCausticTracePass");
            latePasses.Add("GiCausticCacheBuildPass");
            latePasses.Add("GiCausticResolvePass");
            latePasses.Add("GiCausticCompositePass");
        }

        if (modes.UsesNearFieldHiZResidual)
        {
            latePasses.Add("SimpleDdgiNearFieldResidualResetPass");
            latePasses.Add("SimpleDdgiNearFieldResidualPreparePass");
            latePasses.Add("SimpleDdgiNearFieldResidualClassifyPass");
            latePasses.Add("SimpleDdgiNearFieldResidualTracePass");
            latePasses.Add("SimpleDdgiNearFieldResidualTemporalPass");
            latePasses.Add("SimpleDdgiNearFieldResidualFinalizePass");
            for (int iteration = 0;
                 iteration < modes.NearFieldProfile.FilterIterationCount;
                 iteration++)
            {
                latePasses.Add(GetNearFieldFilterPassName(iteration));
            }

            latePasses.Add("SimpleDdgiNearFieldResidualFrequencySeparationPass");
            latePasses.Add("SimpleDdgiNearFieldResidualCompositePass");
        }

        if (latePasses.Count != 0)
            InsertAfter(order, "SimpleDdgiPageFeedbackPass", latePasses.ToArray());

        return order;
    }

    public string Name => PipelineName;

    public IReadOnlyList<RenderGraphPassResourceDeclaration> PassResourceDeclarations =>
        CreatePassResourceDeclarations();

    /// <summary>
    /// Prelude work recorded before graph execution. Keeping these contracts in
    /// the authoritative production declaration makes their compute-to-AS and
    /// AS-to-ray-query dependencies auditable even though Vulkan AS commands
    /// cannot be emitted from secondary graph command buffers.
    /// </summary>
    public IReadOnlyList<RenderGraphPassResourceDeclaration>
        ExternallyRecordedPassResourceDeclarations =>
        CreateExternallyRecordedPassResourceDeclarations();

    public IReadOnlyList<RenderGraphPassResourceDeclaration>
        CreateExternallyRecordedPassResourceDeclarations() =>
    [
        Pass("SkinningPass",
            ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
            ReadWriteComputeBuffer(RenderGraphResourceId.SkinningBuffers)),
        Pass("DdgiFoliageProxyGenerationPass",
            ReadComputeBuffer(RenderGraphResourceId.FoliageBuffers),
            ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
            ReadComputeBuffer(RenderGraphResourceId.DdgiFoliageProxyPatches),
            WriteComputeBuffer(RenderGraphResourceId.DdgiFoliageProxyGeometry)),
        Pass("AccelerationStructureBlasPass",
            ReadAccelerationStructureBuildInput(
                RenderGraphResourceId.SkinningBuffers),
            ReadAccelerationStructureBuildInput(
                RenderGraphResourceId.DdgiFoliageProxyGeometry),
            WriteAccelerationStructureBuild(
                RenderGraphResourceId.DynamicBlasStorage)),
        Pass("AccelerationStructureTlasPass",
            ReadAccelerationStructureBuildInput(
                RenderGraphResourceId.MeshGeometryBuffers),
            ReadAccelerationStructureBuildInput(
                RenderGraphResourceId.DynamicBlasStorage),
            WriteAccelerationStructureBuild(
                RenderGraphResourceId.TlasStorage),
            WriteComputeBuffer(
                RenderGraphResourceId.RayQueryInstanceMetadata))
    ];

    public IReadOnlyList<RenderGraphPassResourceDeclaration>
        CreateExternallyRecordedPassResourceDeclarations(
            in AdvancedGiRenderGraphModes modes)
    {
        var declarations = new List<RenderGraphPassResourceDeclaration>(
            CreateExternallyRecordedPassResourceDeclarations());
        if (!modes.UsesOpacityMicromaps)
            return declarations;

        int tlasIndex = declarations.FindIndex(static declaration =>
            declaration.PassName == "AccelerationStructureTlasPass");
        if (tlasIndex < 0)
        {
            throw new InvalidOperationException(
                "The TLAS prelude declaration is required for C1.");
        }

        declarations.Insert(
            tlasIndex,
            Pass("OpacityMicromapBuildPass",
                ReadAccelerationStructureBuildInput(
                    RenderGraphResourceId.MeshGeometryBuffers),
                ReadWriteMicromapAndAccelerationStructureBuild(
                    RenderGraphResourceId.OpacityMicromapResources),
                ReadWriteMicromapAndAccelerationStructureBuild(
                    RenderGraphResourceId.OpacityMicromapBuildScratch),
                ReadWriteMicromapAndAccelerationStructureBuild(
                    RenderGraphResourceId.OpacityMicromapCompactionHeadroom)));

        RenderGraphPassResourceDeclaration tlas = declarations[tlasIndex + 1];
        var tlasUsages = new List<RenderGraphResourceUsage>(tlas.Usages)
        {
            ReadAccelerationStructureBuildInput(
                RenderGraphResourceId.OpacityMicromapResources)
        };
        declarations[tlasIndex + 1] = tlas with
        {
            Usages = tlasUsages.ToArray()
        };
        return declarations;
    }

    public IReadOnlyList<RenderGraphPassResourceDeclaration> CreatePassResourceDeclarations()
    {
        var declarations = new List<RenderGraphPassResourceDeclaration>
        {
            Pass("SceneOpaqueCompactionPass",
                ReadComputeSampled(RenderGraphResourceId.HiZPyramid),
                WriteComputeBuffer(RenderGraphResourceId.SceneSubmissionBuffers)),
            Pass("DirectionalShadowPass",
                Read(RenderGraphResourceId.SceneSubmissionBuffers),
                Read(RenderGraphResourceId.FoliageBuffers),
                Write(RenderGraphResourceId.DirectionalShadowMap)),
            Pass("SpotShadowPass",
                Read(RenderGraphResourceId.SceneSubmissionBuffers),
                Read(RenderGraphResourceId.FoliageBuffers),
                Write(RenderGraphResourceId.SpotShadowAtlas)),
            Pass("PointShadowPass",
                Read(RenderGraphResourceId.SceneSubmissionBuffers),
                Read(RenderGraphResourceId.FoliageBuffers),
                Write(RenderGraphResourceId.PointShadowCubemapArray)),
            Pass("DepthPrePass",
                Read(RenderGraphResourceId.SceneSubmissionBuffers),
                Read(RenderGraphResourceId.FoliageBuffers),
                WriteDepthAttachment(RenderGraphResourceId.SceneDepth)),
            Pass("MotionVectorPass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                Read(RenderGraphResourceId.SceneSubmissionBuffers),
                WriteGraphicsStorage(
                    RenderGraphResourceId.DirectionalShadowScratch,
                    RenderGraphHistoryBindingSelection.Current),
                WriteColorAttachment(RenderGraphResourceId.MotionVectors)),
            Pass("HiZBuildPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                WriteComputeStorage(RenderGraphResourceId.HiZPyramid, ImageLayout.ShaderReadOnlyOptimal)),
            Pass("DirectionalRayShadowPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadComputeBuffer(RenderGraphResourceId.RayQueryInstanceMetadata),
                ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
                ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                ReadComputeSampled(RenderGraphResourceId.MaterialTextures),
                WriteTransferAndComputeBuffer(
                    RenderGraphResourceId.DirectionalRayShadowMask),
                WriteComputeBuffer(
                    RenderGraphResourceId.DirectionalShadowRaw,
                    RenderGraphHistoryBindingSelection.Current),
                ReadWriteComputeBuffer(RenderGraphResourceId.DirectionalShadowCounters)),
            Pass("AreaRayShadowPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadComputeBuffer(RenderGraphResourceId.RayQueryInstanceMetadata),
                ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
                ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                ReadComputeSampled(RenderGraphResourceId.MaterialTextures),
                ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
                WriteTransferAndComputeBuffer(RenderGraphResourceId.AreaRayShadowMask)),
            Pass("DirectionalShadowTemporalPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeSampled(RenderGraphResourceId.MotionVectors),
                ReadComputeBuffer(
                    RenderGraphResourceId.DirectionalShadowRaw,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeBuffer(
                    RenderGraphResourceId.DirectionalShadowScratch,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeBuffer(
                    RenderGraphResourceId.DirectionalShadowHistory,
                    RenderGraphHistoryBindingSelection.Previous),
                WriteComputeBuffer(
                    RenderGraphResourceId.DirectionalShadowHistory,
                    RenderGraphHistoryBindingSelection.Current),
                ReadWriteComputeBuffer(RenderGraphResourceId.DirectionalShadowDiagnostics),
                ReadWriteComputeBuffer(RenderGraphResourceId.DirectionalShadowCounters)),
            Pass("DirectionalShadowSpatialPass",
                ReadComputeBuffer(
                    RenderGraphResourceId.DirectionalShadowHistory,
                    RenderGraphHistoryBindingSelection.Current),
                ReadWriteComputeBuffer(RenderGraphResourceId.DirectionalShadowRaw),
                ReadWriteComputeBuffer(RenderGraphResourceId.DirectionalShadowScratch),
                WriteTransferAndComputeBuffer(RenderGraphResourceId.DirectionalRayShadowMask),
                ReadWriteComputeBuffer(RenderGraphResourceId.DirectionalShadowCounters)),
            Pass("ForwardVisibilityCompactionPass",
                ReadComputeSampled(RenderGraphResourceId.HiZPyramid),
                ReadComputeBuffer(RenderGraphResourceId.SceneSubmissionBuffers),
                WriteComputeBuffer(RenderGraphResourceId.ForwardVisibilityBuffers))
        };

        declarations.AddRange([
            Pass("AmbientOcclusionPass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                WriteComputeStorage(RenderGraphResourceId.AmbientOcclusionRaw, ImageLayout.ShaderReadOnlyOptimal)),
            Pass("AmbientOcclusionBlurPass",
                ReadComputeSampled(RenderGraphResourceId.AmbientOcclusionRaw),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadWriteComputeStorage(RenderGraphResourceId.AmbientOcclusionScratch,
                    ImageLayout.ShaderReadOnlyOptimal),
                WriteComputeStorage(RenderGraphResourceId.AmbientOcclusionBlurred, ImageLayout.ShaderReadOnlyOptimal)),
            Pass("GtaoPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeSampled(RenderGraphResourceId.HiZPyramid),
                WriteComputeStorage(RenderGraphResourceId.GtaoRaw,
                    ImageLayout.ShaderReadOnlyOptimal)),
            Pass("GtaoTemporalPass",
                ReadComputeSampled(RenderGraphResourceId.GtaoRaw),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeSampled(RenderGraphResourceId.MotionVectors),
                ReadComputeSampled(RenderGraphResourceId.GtaoHistory,
                    RenderGraphHistoryBindingSelection.Previous),
                ReadComputeSampled(RenderGraphResourceId.GtaoGeometryHistory,
                    RenderGraphHistoryBindingSelection.Previous),
                WriteComputeStorage(RenderGraphResourceId.GtaoHistory,
                    ImageLayout.ShaderReadOnlyOptimal,
                    RenderGraphHistoryBindingSelection.Current),
                WriteComputeStorage(RenderGraphResourceId.GtaoGeometryHistory,
                    ImageLayout.ShaderReadOnlyOptimal,
                    RenderGraphHistoryBindingSelection.Current)),
            Pass("GtaoSpatialPass",
                ReadComputeSampled(RenderGraphResourceId.GtaoHistory,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeSampled(RenderGraphResourceId.GtaoGeometryHistory,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeSampled(RenderGraphResourceId.GtaoRaw),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                WriteComputeStorage(RenderGraphResourceId.GtaoFiltered,
                    ImageLayout.ShaderReadOnlyOptimal),
                WriteComputeStorage(RenderGraphResourceId.GtaoSpatialScratch,
                    ImageLayout.ShaderReadOnlyOptimal),
                WriteComputeStorage(RenderGraphResourceId.AmbientOcclusionBlurred,
                    ImageLayout.ShaderReadOnlyOptimal)),
            Pass("TiledLightCullingPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                Write(RenderGraphResourceId.LightTiles)),
            // The pass performs exact per-mip image barriers internally because
            // the environment chain also contains immutable HDR/BRDF images.
            // It restores each written mip to shader-read layout before return.
            Pass("EnvironmentPrefilterPass",
                ReadComputeBuffer(RenderGraphResourceId.EnvironmentData))
        ]);

        declarations.Add(
            Pass("SimpleDdgiLightTreePass",
                ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiLightTree)));

        declarations.Add(
            Pass("SimpleDdgiUrgentRelightPass",
                ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
                ReadComputeBuffer(RenderGraphResourceId.EnvironmentData),
                ReadComputeSampled(RenderGraphResourceId.EnvironmentMaps),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRelocationData),
                WriteComputeBuffer(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadWriteComputeBuffer(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer)));

        declarations.Add(
            Pass("VariableRateShadingPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeSampled(RenderGraphResourceId.MotionVectors),
                WriteComputeStorage(
                    RenderGraphResourceId.VariableRateShading,
                    ImageLayout.FragmentShadingRateAttachmentOptimalKhr)));

        declarations.Add(
            Pass("ForwardPlusPass",
                ReadDepthAttachmentAndCompute(RenderGraphResourceId.SceneDepth),
                Read(RenderGraphResourceId.SceneSubmissionBuffers),
                Read(RenderGraphResourceId.ForwardVisibilityBuffers),
                Read(RenderGraphResourceId.FoliageBuffers),
                Read(RenderGraphResourceId.LightTiles),
                ReadFragmentSampled(RenderGraphResourceId.AmbientOcclusionBlurred),
                ReadFragmentSampled(RenderGraphResourceId.GtaoFiltered),
                Read(RenderGraphResourceId.DirectionalShadowMap),
                Read(RenderGraphResourceId.SpotShadowAtlas),
                Read(RenderGraphResourceId.PointShadowCubemapArray),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalRayShadowMask),
                ReadGraphicsStorage(RenderGraphResourceId.AreaRayShadowMask),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalShadowHistory),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalShadowDiagnostics),
                Read(RenderGraphResourceId.ReflectionProbeCubemaps),
                Read(RenderGraphResourceId.EnvironmentMaps),
                ReadGraphicsStorage(RenderGraphResourceId.MeshGeometryBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.MaterialBuffers),
                ReadFragmentSampled(RenderGraphResourceId.MaterialTextures),
                ReadGraphicsStorage(RenderGraphResourceId.LightBuffers),
                ReadFragmentShadingRate(
                    RenderGraphResourceId.VariableRateShading),
                ReadGraphicsStorage(RenderGraphResourceId.EnvironmentData),
                ReadGraphicsAndComputeStorage(RenderGraphResourceId.SimpleDdgiParameters),
                ReadGraphicsAndComputeStorage(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadGraphicsAndComputeStorage(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadGraphicsAndComputeStorage(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadGraphicsStorage(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                ReadWriteGraphicsAndComputeStorage(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteGraphicsAndComputeStorage(RenderGraphResourceId.SimpleDdgiScheduler),
#if DEBUG || NJULF_DETAILED_INVESTIGATION
                // Detailed receiver views inspect the update-side probe state.
                // Raw source-cache decoding is deliberately compute-only.
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiProbeState),
#endif
                ReadWriteGraphicsStorage(RenderGraphResourceId.RendererDiagnosticsBuffer),
                WriteColorAttachment(RenderGraphResourceId.SceneColor),
                WriteColorAttachment(
                    RenderGraphResourceId.HybridReflectionReceiverPayload)));

        // The bounded cache-only urgent lane above can publish radiometric edits
        // for this forward draw. The complete DDGI update remains after
        // ForwardPlusPass and publishes cache ownership for subsequent frames.
        // DDGI paths deliberately declare every concrete storage family they touch. A scheduler
        // rejects the path if even one binding is unavailable rather than treating BufferSet or
        // External as an opaque unit and risking an unpaired queue-family handoff.
        declarations.AddRange([
            Pass("SimpleDdgiPageDemandPass",
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency)),
            Pass("SimpleDdgiPageResidencyPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRelocationData),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler)),
            Pass("FarFieldClipmapBakePass",
                ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
                ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                ReadWriteComputeBuffer(RenderGraphResourceId.FarFieldParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.FarFieldVoxels),
                ReadComputeBuffer(RenderGraphResourceId.FarFieldInstances),
                ReadWriteComputeBuffer(RenderGraphResourceId.FarFieldJumpFlood),
                ReadWriteComputeBuffer(RenderGraphResourceId.FarFieldPageTable),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer)),
            Pass("SimpleDdgiSchedulePass",
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue)),
            Pass("SimpleDdgiTracePass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadComputeAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadComputeBuffer(RenderGraphResourceId.RayQueryInstanceMetadata),
                ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
                ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                ReadComputeSampled(RenderGraphResourceId.MaterialTextures),
                ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiLightTree),
                ReadComputeBuffer(RenderGraphResourceId.DdgiEmissiveSources),
                ReadComputeBuffer(RenderGraphResourceId.EnvironmentData),
                ReadComputeSampled(RenderGraphResourceId.EnvironmentMaps),
                ReadComputeBuffer(RenderGraphResourceId.FarFieldParameters),
                ReadComputeBuffer(RenderGraphResourceId.FarFieldVoxels),
                ReadComputeBuffer(RenderGraphResourceId.FarFieldInstances),
                ReadComputeBuffer(RenderGraphResourceId.FarFieldJumpFlood),
                ReadComputeBuffer(RenderGraphResourceId.FarFieldPageTable),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRelocationData),
                ReadComputeBuffer(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer)),
            Pass("SimpleDdgiRelocateClassifyPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRelocationData),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler)),
            Pass("SimpleDdgiAcceleratedSolvePass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRelocationData),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer)),
            Pass("SimpleDdgiTransportPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer)),
            Pass("SimpleDdgiBlendPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRelocationData),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer)),
            Pass("SimpleDdgiDirectionalRadiancePass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadComputeBuffer(
                    RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                ReadWriteComputeIndirectBuffer(
                    RenderGraphResourceId.SimpleDdgiScheduler)),
            Pass("SimpleDdgiPublishPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadComputeBuffer(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                WriteComputeBuffer(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler)),
            Pass("SimpleDdgiTransportAuditPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler)),
            Pass("SimpleDdgiSchedulerCommitPass",
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                ReadComputeBuffer(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                WriteComputeBuffer(RenderGraphResourceId.SimpleDdgiReceiverProbes)),
            Pass("SimpleDdgiPageFeedbackPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency)),
            Pass("SkyboxPass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                ReadFragmentSampled(RenderGraphResourceId.EnvironmentMaps),
                ReadGraphicsStorage(RenderGraphResourceId.EnvironmentData),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("HybridReflectionSsrPass",
                ReadComputeSampled(RenderGraphResourceId.SceneColor),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeSampled(RenderGraphResourceId.HiZPyramid),
                ReadComputeSampled(
                    RenderGraphResourceId.HybridReflectionReceiverPayload),
                ReadComputeSampled(RenderGraphResourceId.MotionVectors),
                ReadComputeBuffer(RenderGraphResourceId.SceneSubmissionBuffers),
                ReadComputeStorage(
                    RenderGraphResourceId.HybridReflectionHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Previous),
                WriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionRawRadiance),
                WriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionRawMetadata),
                ReadWriteComputeBuffer(
                    RenderGraphResourceId.HybridReflectionRayTasks),
                ReadWriteComputeBuffer(
                    RenderGraphResourceId.HybridReflectionCounters),
                ReadWriteComputeBuffer(
                    RenderGraphResourceId.HybridReflectionIndirectArguments)),
            Pass("HybridReflectionRayQueryPass",
                ReadComputeAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadComputeBuffer(RenderGraphResourceId.RayQueryInstanceMetadata),
                ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
                ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                ReadComputeSampled(RenderGraphResourceId.MaterialTextures),
                ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadComputeSampled(
                    RenderGraphResourceId.HybridReflectionReceiverPayload),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeBuffer(RenderGraphResourceId.HybridReflectionRayTasks),
                ReadComputeIndirectBuffer(
                    RenderGraphResourceId.HybridReflectionIndirectArguments),
                ReadWriteComputeBuffer(RenderGraphResourceId.HybridReflectionCounters),
                ReadWriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionRawRadiance),
                ReadWriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionRawMetadata)),
            Pass("HybridReflectionDdgiBasePass",
                ReadComputeSampled(
                    RenderGraphResourceId.HybridReflectionReceiverPayload),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadComputeBuffer(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                WriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionDdgiCohorts),
                WriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionFilterScratch)),
            Pass("HybridReflectionResolvePass",
                ReadComputeSampled(
                    RenderGraphResourceId.HybridReflectionReceiverPayload),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeBuffer(RenderGraphResourceId.EnvironmentData),
                ReadComputeSampled(RenderGraphResourceId.EnvironmentMaps),
                ReadComputeSampled(RenderGraphResourceId.ReflectionProbeCubemaps),
                ReadComputeStorage(
                    RenderGraphResourceId.HybridReflectionFilterScratch),
                ReadWriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionRawRadiance),
                ReadWriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionRawMetadata),
                ReadWriteComputeBuffer(RenderGraphResourceId.HybridReflectionCounters)),
            Pass("HybridReflectionTemporalPass",
                ReadComputeSampled(
                    RenderGraphResourceId.HybridReflectionReceiverPayload),
                ReadComputeSampled(RenderGraphResourceId.MotionVectors),
                ReadComputeBuffer(RenderGraphResourceId.SceneSubmissionBuffers),
                ReadComputeStorage(RenderGraphResourceId.HybridReflectionRawRadiance),
                ReadComputeStorage(RenderGraphResourceId.HybridReflectionRawMetadata),
                ReadComputeStorage(RenderGraphResourceId.HybridReflectionHistory,
                    RenderGraphHistoryBindingSelection.Previous),
                WriteComputeStorage(RenderGraphResourceId.HybridReflectionHistory,
                    historyBinding: RenderGraphHistoryBindingSelection.Current),
                ReadComputeStorage(RenderGraphResourceId.HybridReflectionMoments,
                    RenderGraphHistoryBindingSelection.Previous),
                WriteComputeStorage(RenderGraphResourceId.HybridReflectionMoments,
                    historyBinding: RenderGraphHistoryBindingSelection.Current),
                ReadComputeStorage(
                    RenderGraphResourceId.HybridReflectionHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Previous),
                WriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionHistoryMetadata,
                    historyBinding: RenderGraphHistoryBindingSelection.Current)),
            Pass("HybridReflectionSpatialPass",
                ReadComputeSampled(
                    RenderGraphResourceId.HybridReflectionReceiverPayload),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeStorage(RenderGraphResourceId.HybridReflectionHistory,
                    historyBinding: RenderGraphHistoryBindingSelection.Current),
                ReadComputeStorage(
                    RenderGraphResourceId.HybridReflectionHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Current),
                ReadWriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionRawRadiance),
                ReadWriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionFilterScratch)),
            Pass("HybridReflectionCompositePass",
                ReadComputeSampled(
                    RenderGraphResourceId.HybridReflectionReceiverPayload),
                ReadComputeStorage(RenderGraphResourceId.HybridReflectionHistory,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeStorage(
                    RenderGraphResourceId.HybridReflectionHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeStorage(
                    RenderGraphResourceId.HybridReflectionFilterScratch),
                ReadWriteComputeStorage(RenderGraphResourceId.SceneColor,
                    ImageLayout.ColorAttachmentOptimal)),
            Pass("OpaqueSceneColorSnapshotPass",
                ReadComputeStorage(RenderGraphResourceId.SceneColor),
                WriteComputeStorage(
                    RenderGraphResourceId.HybridReflectionFilterScratch)),
            Pass("TransparentForwardPass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                ReadFragmentSampled(
                    RenderGraphResourceId.HybridReflectionFilterScratch),
                ReadFragmentAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadGraphicsStorage(RenderGraphResourceId.RayQueryInstanceMetadata),
                Read(RenderGraphResourceId.DirectionalShadowMap),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalRayShadowMask),
                ReadGraphicsStorage(RenderGraphResourceId.AreaRayShadowMask),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalShadowHistory),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalShadowDiagnostics),
                ReadWriteGraphicsStorage(RenderGraphResourceId.DirectionalShadowCounters),
                Read(RenderGraphResourceId.SpotShadowAtlas),
                Read(RenderGraphResourceId.PointShadowCubemapArray),
                Read(RenderGraphResourceId.ReflectionProbeCubemaps),
                ReadFragmentSampled(RenderGraphResourceId.EnvironmentMaps),
                ReadGraphicsStorage(RenderGraphResourceId.MeshGeometryBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.MaterialBuffers),
                ReadFragmentSampled(RenderGraphResourceId.MaterialTextures),
                ReadGraphicsStorage(RenderGraphResourceId.LightBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.EnvironmentData),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadGraphicsStorage(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                ReadWriteGraphicsStorage(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteGraphicsStorage(RenderGraphResourceId.SimpleDdgiScheduler),
#if DEBUG || NJULF_DETAILED_INVESTIGATION
            ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiProbeState),
#endif
                ReadWriteGraphicsStorage(RenderGraphResourceId.RendererDiagnosticsBuffer),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("WeightedTransparentPass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                ReadFragmentSampled(
                    RenderGraphResourceId.HybridReflectionFilterScratch),
                ReadFragmentAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadGraphicsStorage(RenderGraphResourceId.RayQueryInstanceMetadata),
                Read(RenderGraphResourceId.DirectionalShadowMap),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalRayShadowMask),
                ReadGraphicsStorage(RenderGraphResourceId.AreaRayShadowMask),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalShadowHistory),
                ReadGraphicsStorage(RenderGraphResourceId.DirectionalShadowDiagnostics),
                ReadWriteGraphicsStorage(RenderGraphResourceId.DirectionalShadowCounters),
                Read(RenderGraphResourceId.SpotShadowAtlas),
                Read(RenderGraphResourceId.PointShadowCubemapArray),
                Read(RenderGraphResourceId.ReflectionProbeCubemaps),
                ReadFragmentSampled(RenderGraphResourceId.EnvironmentMaps),
                ReadGraphicsStorage(RenderGraphResourceId.MeshGeometryBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.MaterialBuffers),
                ReadFragmentSampled(RenderGraphResourceId.MaterialTextures),
                ReadGraphicsStorage(RenderGraphResourceId.LightBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.EnvironmentData),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadGraphicsStorage(
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                ReadWriteGraphicsStorage(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteGraphicsStorage(RenderGraphResourceId.SimpleDdgiScheduler),
#if DEBUG || NJULF_DETAILED_INVESTIGATION
            ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiProbeState),
#endif
                ReadWriteGraphicsStorage(RenderGraphResourceId.RendererDiagnosticsBuffer),
                WriteColorAttachment(RenderGraphResourceId.WeightedOitAccumulation),
                WriteColorAttachment(RenderGraphResourceId.WeightedOitRevealage)),
            Pass("WeightedOitCompositePass",
                ReadFragmentSampled(RenderGraphResourceId.WeightedOitAccumulation),
                ReadFragmentSampled(RenderGraphResourceId.WeightedOitRevealage),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("GpuParticleResetPass",
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleState),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleIndices),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleCounters),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleUnsortedOutput),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleRenderOutput),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleIndirectArguments),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleSortKeys)),
            Pass("GpuParticleSimulatePass",
                ReadComputeBuffer(RenderGraphResourceId.ParticleBuffers),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleState),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleIndices),
                ReadComputeBuffer(RenderGraphResourceId.GpuParticleEmitterData),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleCounters),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleUnsortedOutput),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleIndirectArguments),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleSortKeys)),
            Pass("GpuParticleSortPass",
                ReadComputeBuffer(RenderGraphResourceId.ParticleBuffers),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleCounters),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleUnsortedOutput),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleRenderOutput),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleIndirectArguments),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleSortKeys),
                ReadWriteComputeBuffer(RenderGraphResourceId.GpuParticleCounterReadback)),
            Pass("ParticlePass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                ReadGraphicsStorage(RenderGraphResourceId.ParticleBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.GpuParticleRenderOutput),
                ReadGraphicsIndirect(RenderGraphResourceId.GpuParticleIndirectArguments),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiParameters),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadWriteGraphicsStorage(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteGraphicsStorage(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteGraphicsStorage(RenderGraphResourceId.RendererDiagnosticsBuffer),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("SimpleDdgiProbeDebugPass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiParameters),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteGraphicsStorage(RenderGraphResourceId.RendererDiagnosticsBuffer),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("DebugDrawPass",
                ReadDepth(RenderGraphResourceId.SceneDepth),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("DebugOverlayPass",
                ReadGraphicsStorage(RenderGraphResourceId.LightTiles),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("FogPass",
                ReadComputeSampled(RenderGraphResourceId.SceneColor),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiReceiverProbes),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                ReadComputeAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
                ReadComputeBuffer(RenderGraphResourceId.DdgiEmissiveSources),
                ReadComputeSampled(RenderGraphResourceId.DirectionalShadowMap),
                ReadComputeSampled(RenderGraphResourceId.SpotShadowAtlas),
                ReadComputeSampled(RenderGraphResourceId.PointShadowCubemapArray),
                ReadComputeBuffer(RenderGraphResourceId.ParticleBuffers),
                ReadComputeBuffer(RenderGraphResourceId.GpuParticleRenderOutput),
                ReadComputeBuffer(RenderGraphResourceId.GpuParticleCounters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer),
                WriteComputeStorage(RenderGraphResourceId.FogOutput, ImageLayout.ShaderReadOnlyOptimal)),
            Pass("AutoExposurePass",
                ReadComputeSampled(RenderGraphResourceId.SceneColor),
                ReadComputeSampled(RenderGraphResourceId.FogOutput),
                Write(RenderGraphResourceId.TransientIntermediate)),
            Pass("BloomPass",
                ReadComputeSampled(RenderGraphResourceId.SceneColor),
                ReadComputeSampled(RenderGraphResourceId.FogOutput),
                ReadWriteComputeStorage(RenderGraphResourceId.BloomChain, ImageLayout.ShaderReadOnlyOptimal)),
            Pass("ToneMapCompositePass",
                ReadFragmentSampled(RenderGraphResourceId.SceneColor),
                ReadFragmentSampled(RenderGraphResourceId.FogOutput),
                ReadFragmentSampled(RenderGraphResourceId.BloomChain),
                WriteColorAttachment(RenderGraphResourceId.LdrSceneColor),
                WriteColorAttachment(RenderGraphResourceId.SwapchainColor)),
            Pass("AntiAliasingPass",
                ReadFragmentSampled(RenderGraphResourceId.LdrSceneColor),
                Read(RenderGraphResourceId.MotionVectors),
                WriteColorAttachment(RenderGraphResourceId.SmaaEdges),
                WriteColorAttachment(RenderGraphResourceId.SmaaBlendWeights),
                ReadWrite(RenderGraphResourceId.TaaHistory),
                WriteColorAttachment(RenderGraphResourceId.SwapchainColor)),
            Pass("ImGuiRenderPass", ReadWriteColorAttachment(RenderGraphResourceId.SwapchainColor))
        ]);

        return declarations;
    }

    /// <summary>
    /// Declares only the resources touched by the concrete advanced-GI graph
    /// variant.  This deliberately does not add optional usages to the base
    /// declaration: a disabled mode must not create hidden descriptor or
    /// synchronization pressure.
    /// </summary>
    public IReadOnlyList<RenderGraphPassResourceDeclaration>
        CreatePassResourceDeclarations(in AdvancedGiRenderGraphModes modes)
    {
        if (!modes.HasGpuFeature)
            return CreatePassResourceDeclarations();

        var declarations = new List<RenderGraphPassResourceDeclaration>(
            CreatePassResourceDeclarations());

        if (modes.UsesCausticWorldCache)
        {
            int forwardIndex = declarations.FindIndex(static declaration =>
                declaration.PassName == "ForwardPlusPass");
            if (forwardIndex < 0)
                throw new InvalidOperationException("ForwardPlusPass declaration is required for C4.");

            RenderGraphPassResourceDeclaration forward = declarations[forwardIndex];
            var usages = new List<RenderGraphResourceUsage>(forward.Usages)
            {
                WriteColorAttachment(RenderGraphResourceId.GiCausticReceiverPayload)
            };
            declarations[forwardIndex] = forward with { Usages = usages.ToArray() };
        }

        if (modes.UsesNearFieldHiZResidual)
        {
            int forwardIndex = declarations.FindIndex(static declaration =>
                declaration.PassName == "ForwardPlusPass");
            if (forwardIndex < 0)
                throw new InvalidOperationException("ForwardPlusPass declaration is required for C5.");

            RenderGraphPassResourceDeclaration forward = declarations[forwardIndex];
            var usages = new List<RenderGraphResourceUsage>(forward.Usages)
            {
                // These attachments are emitted only by the dedicated C5
                // forward variant. The normal shipping ForwardPlus pipeline
                // never declares or binds them.
                WriteColorAttachment(RenderGraphResourceId.NearFieldDirectSource),
                WriteColorAttachment(RenderGraphResourceId.NearFieldReceiverPayload)
            };
            if (modes.NearFieldProfile.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster)
            {
                usages.Add(WriteDepthAttachment(
                    RenderGraphResourceId.NearFieldTraceRasterDepth));
            }
            declarations[forwardIndex] = forward with { Usages = usages.ToArray() };
        }

        if (modes.UsesDirectionalGuiding)
        {
            declarations.AddRange([
                Pass(SimpleDdgiGuidingGpuPassNames.Sample,
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingDistributions),
                    ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                    WriteComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingDirectionPayloadSidecar)),
                Pass(SimpleDdgiGuidingGpuPassNames.Train,
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingDistributions),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingDirectionPayloadSidecar),
                    ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingScratch)),
                Pass(SimpleDdgiGuidingGpuPassNames.Build,
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiResidency),
                    ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingDistributions),
                    ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingScratch)),
                Pass(SimpleDdgiGuidingGpuPassNames.Validate,
                    ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingDistributions),
                    ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiGuidingScratch),
                    ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer))
            ]);
        }

        if (modes.UsesCausticWorldCache)
        {
            declarations.AddRange([
                Pass("GiCausticTaskPass",
                    ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
                    ReadComputeBuffer(RenderGraphResourceId.DdgiEmissiveSources),
                    ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                    ReadWriteComputeBuffer(RenderGraphResourceId.GiCausticTasks)),
                Pass("GiCausticTracePass",
                    ReadComputeBuffer(RenderGraphResourceId.GiCausticTasks),
                    ReadComputeAccelerationStructure(RenderGraphResourceId.TlasStorage),
                    ReadComputeBuffer(RenderGraphResourceId.RayQueryInstanceMetadata),
                    ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
                    ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                    ReadComputeSampled(RenderGraphResourceId.MaterialTextures),
                    ReadWriteComputeBuffer(RenderGraphResourceId.GiCausticPhotons)),
                Pass("GiCausticCacheBuildPass",
                    ReadComputeBuffer(RenderGraphResourceId.GiCausticPhotons),
                    ReadWriteComputeBuffer(RenderGraphResourceId.GiCausticCache),
                    ReadWriteComputeBuffer(RenderGraphResourceId.GiCausticScratch)),
                Pass("GiCausticResolvePass",
                    ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                    ReadComputeSampled(RenderGraphResourceId.GiCausticReceiverPayload),
                    ReadComputeBuffer(RenderGraphResourceId.GiCausticCache),
                    ReadComputeBuffer(RenderGraphResourceId.GiCausticPhotons),
                    ReadComputeBuffer(RenderGraphResourceId.GiCausticScreenFrameConstants),
                    ReadWriteComputeIndirectBuffer(RenderGraphResourceId.GiCausticScratch),
                    WriteComputeStorage(RenderGraphResourceId.GiCausticRadiance),
                    WriteComputeStorage(RenderGraphResourceId.GiCausticMoments)),
                Pass("GiCausticCompositePass",
                    ReadComputeStorage(RenderGraphResourceId.GiCausticRadiance),
                    ReadComputeStorage(RenderGraphResourceId.GiCausticMoments),
                    ReadComputeBuffer(RenderGraphResourceId.GiCausticScratch),
                    ReadWriteComputeStorage(RenderGraphResourceId.SceneColor,
                        ImageLayout.ShaderReadOnlyOptimal))
            ]);
        }

        if (modes.UsesNearFieldHiZResidual)
        {
            var nearFieldResetUsages = new List<RenderGraphResourceUsage>
            {
                WriteTransferAndComputeBuffer(
                    RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Current),
                WriteTransferAndComputeBuffer(
                    RenderGraphResourceId.NearFieldResidualTileBuffers),
                ReadWriteComputeIndirectBuffer(
                    RenderGraphResourceId
                        .NearFieldActiveTilesAndIndirectArguments),
                WriteTransferStorage(
                    RenderGraphResourceId.NearFieldResidualRaw),
                WriteTransferStorage(
                    RenderGraphResourceId.NearFieldResidualHistory,
                    RenderGraphHistoryBindingSelection.Current),
                WriteTransferStorage(
                    RenderGraphResourceId.NearFieldResidualMoments,
                    RenderGraphHistoryBindingSelection.Current),
                WriteTransferStorage(
                    RenderGraphResourceId.NearFieldResidualValidity,
                    RenderGraphHistoryBindingSelection.Current),
                WriteTransferStorage(
                    RenderGraphResourceId.NearFieldResidualHistoryNormals,
                    RenderGraphHistoryBindingSelection.Current)
            };
            if (modes.UsesNearFieldFiltering)
            {
                nearFieldResetUsages.Add(WriteTransferStorage(
                    RenderGraphResourceId.NearFieldResidualFilterScratch));
            }
            nearFieldResetUsages.Add(WriteTransferAndComputeBuffer(
                RenderGraphResourceId.NearFieldResidualSchedulerHistory,
                RenderGraphHistoryBindingSelection.Current));

            declarations.AddRange([
                Pass("SimpleDdgiNearFieldResidualResetPass",
                    nearFieldResetUsages.ToArray()),
                Pass("SimpleDdgiNearFieldResidualPreparePass",
                    ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldDirectSource),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldReceiverPayload),
                    ReadComputeSampled(RenderGraphResourceId.MotionVectors),
                    ReadComputeBuffer(RenderGraphResourceId.SceneSubmissionBuffers),
                    ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                    ReadComputeBuffer(RenderGraphResourceId.FoliageBuffers),
                    WriteComputeStorage(
                        RenderGraphResourceId.NearFieldPreparedDepthFootprint),
                    WriteComputeStorage(
                        RenderGraphResourceId.NearFieldPreparedReceiverPayload),
                    WriteComputeStorage(RenderGraphResourceId.NearFieldPreparedMotion),
                    WriteComputeStorage(RenderGraphResourceId.NearFieldSourceLuminance),
                    ReadWriteComputeIndirectBuffer(
                        RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments),
                    WriteComputeBuffer(RenderGraphResourceId.NearFieldSurfaceTable),
                    WriteComputeBuffer(RenderGraphResourceId.NearFieldResidualTileBuffers)),
                Pass("SimpleDdgiNearFieldResidualClassifyPass",
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldPreparedDepthFootprint),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldPreparedReceiverPayload),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldPreparedMotion),
                    ReadComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualSchedulerHistory,
                        RenderGraphHistoryBindingSelection.Previous),
                    WriteComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualSchedulerHistory,
                        RenderGraphHistoryBindingSelection.Current),
                    ReadWriteComputeIndirectBuffer(
                        RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments),
                    ReadWriteComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualTileBuffers)),
                Pass("SimpleDdgiNearFieldResidualTracePass",
                    ReadComputeSampled(RenderGraphResourceId.NearFieldDirectSource),
                    ReadComputeSampled(RenderGraphResourceId.HiZPyramid),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldPreparedDepthFootprint),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldPreparedReceiverPayload),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldReceiverPayload),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldSourceLuminance),
                    ReadComputeBuffer(RenderGraphResourceId.NearFieldSurfaceTable),
                    ReadComputeIndirectBuffer(
                        RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments),
                    ReadComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualTraceFrameConstants),
                    ReadComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualSchedulerHistory,
                        RenderGraphHistoryBindingSelection.Current),
                    WriteComputeStorage(RenderGraphResourceId.NearFieldResidualRaw),
                    WriteComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                        RenderGraphHistoryBindingSelection.Current),
                    WriteComputeBuffer(RenderGraphResourceId.NearFieldResidualTileBuffers)),
                Pass("SimpleDdgiNearFieldResidualTemporalPass",
                    ReadComputeSampled(RenderGraphResourceId.NearFieldResidualRaw),
                    ReadComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                        RenderGraphHistoryBindingSelection.Current),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldPreparedMotion),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldPreparedReceiverPayload),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldPreparedDepthFootprint),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldDirectSource),
                    ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                    ReadComputeSampled(RenderGraphResourceId.NearFieldReceiverPayload),
                    ReadComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualTraceFrameConstants),
                    ReadComputeBuffer(RenderGraphResourceId.NearFieldSurfaceTable),
                    ReadComputeIndirectBuffer(
                        RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldResidualHistory,
                        RenderGraphHistoryBindingSelection.Previous),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldResidualMoments,
                        RenderGraphHistoryBindingSelection.Previous),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldResidualValidity,
                        RenderGraphHistoryBindingSelection.Previous),
                    ReadComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                        RenderGraphHistoryBindingSelection.Previous),
                    ReadComputeSampled(
                        RenderGraphResourceId.NearFieldResidualHistoryNormals,
                        RenderGraphHistoryBindingSelection.Previous),
                    WriteComputeStorage(
                        RenderGraphResourceId.NearFieldResidualHistory,
                        historyBinding: RenderGraphHistoryBindingSelection.Current),
                    WriteComputeStorage(
                        RenderGraphResourceId.NearFieldResidualMoments,
                        historyBinding: RenderGraphHistoryBindingSelection.Current),
                    WriteComputeStorage(
                        RenderGraphResourceId.NearFieldResidualValidity,
                        historyBinding: RenderGraphHistoryBindingSelection.Current),
                    WriteComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                        RenderGraphHistoryBindingSelection.Current),
                    WriteComputeStorage(
                        RenderGraphResourceId.NearFieldResidualHistoryNormals,
                        historyBinding: RenderGraphHistoryBindingSelection.Current),
                    ReadWriteComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualSchedulerHistory,
                        RenderGraphHistoryBindingSelection.Current),
                    WriteComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualTileBuffers)),
                Pass("SimpleDdgiNearFieldResidualFinalizePass",
                    ReadWriteComputeBuffer(
                        RenderGraphResourceId.NearFieldResidualTileBuffers))
            ]);

            for (int iteration = 0;
                 iteration < modes.NearFieldProfile.FilterIterationCount;
                 iteration++)
            {
                var filterUsages = new List<RenderGraphResourceUsage>();
                if (iteration == 0)
                {
                    filterUsages.Add(ReadComputeSampled(
                        RenderGraphResourceId.NearFieldResidualHistory,
                        RenderGraphHistoryBindingSelection.Current));
                }
                else
                {
                    filterUsages.Add(ReadComputeSampled(
                        NearFieldFilterTargetResource(
                            modes.NearFieldProfile.FilterIterationCount,
                            iteration - 1)));
                }

                filterUsages.Add(ReadComputeBuffer(
                    RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Current));
                filterUsages.Add(ReadComputeSampled(
                    RenderGraphResourceId.NearFieldPreparedReceiverPayload));
                filterUsages.Add(ReadComputeSampled(
                    RenderGraphResourceId.NearFieldResidualMoments,
                    RenderGraphHistoryBindingSelection.Current));
                filterUsages.Add(ReadComputeIndirectBuffer(
                    RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments));
                filterUsages.Add(WriteComputeStorage(
                    NearFieldFilterTargetResource(
                        modes.NearFieldProfile.FilterIterationCount,
                        iteration)));
                declarations.Add(Pass(
                    GetNearFieldFilterPassName(iteration),
                    filterUsages.ToArray()));
            }

            RenderGraphResourceUsage frequencyInput = modes.UsesNearFieldFiltering
                ? ReadComputeSampled(
                    // Parity selection guarantees the final estimate resides
                    // in the separate scratch image, leaving Raw available as
                    // the frequency-separation output.
                    RenderGraphResourceId.NearFieldResidualFilterScratch)
                : ReadComputeSampled(
                    RenderGraphResourceId.NearFieldResidualHistory,
                    RenderGraphHistoryBindingSelection.Current);
            declarations.Add(Pass("SimpleDdgiNearFieldResidualFrequencySeparationPass",
                frequencyInput,
                ReadComputeSampled(
                    RenderGraphResourceId.NearFieldPreparedDepthFootprint),
                ReadComputeSampled(
                    RenderGraphResourceId.NearFieldPreparedReceiverPayload),
                ReadComputeBuffer(
                    RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeBuffer(
                    RenderGraphResourceId.NearFieldResidualTraceFrameConstants),
                ReadComputeIndirectBuffer(
                    RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments),
                WriteComputeStorage(RenderGraphResourceId.NearFieldResidualRaw)));
            declarations.Add(Pass("SimpleDdgiNearFieldResidualCompositePass",
                ReadComputeSampled(RenderGraphResourceId.NearFieldResidualRaw),
                ReadComputeBuffer(
                    RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                    RenderGraphHistoryBindingSelection.Current),
                ReadWriteComputeStorage(RenderGraphResourceId.SceneColor,
                    ImageLayout.ShaderReadOnlyOptimal),
                ReadComputeSampled(RenderGraphResourceId.NearFieldReceiverPayload),
                ReadComputeDepth(RenderGraphResourceId.SceneDepth),
                ReadComputeSampled(
                    RenderGraphResourceId.NearFieldPreparedReceiverPayload),
                ReadComputeSampled(RenderGraphResourceId.NearFieldDirectSource),
                ReadComputeSampled(
                    RenderGraphResourceId.NearFieldResidualValidity,
                    RenderGraphHistoryBindingSelection.Current),
                ReadComputeBuffer(RenderGraphResourceId.NearFieldSurfaceTable),
                ReadComputeBuffer(
                    RenderGraphResourceId.NearFieldResidualTraceFrameConstants),
                ReadComputeIndirectBuffer(
                    RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments)));
        }

        return declarations;
    }

    public IReadOnlyList<RenderGraphResourceDescriptor> CreateResourceDescriptors(
        Format depthFormat,
        Format swapchainColorFormat)
    {
        return
        [
            ImageResource(RenderGraphResourceId.SceneColor, "Scene color", RenderTargetManager.SceneColorFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageResource(RenderGraphResourceId.LdrSceneColor, "LDR scene color",
                RenderTargetManager.LdrSceneColorFormat, RenderGraphResourceSizePolicy.Swapchain),
            ImageResource(RenderGraphResourceId.SceneDepth, "Scene depth", depthFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageResource(RenderGraphResourceId.MotionVectors, "Motion vectors",
                RenderTargetManager.MotionVectorFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(
                RenderGraphResourceId.VariableRateShading,
                "Conservative fragment shading rate",
                RenderTargetManager.VariableRateShadingFormat,
                RenderGraphResourceSizePolicy.Dynamic),
            OwnedImageChainResource(RenderGraphResourceId.BloomChain, "Bloom chain",
                RenderTargetManager.SceneColorFormat, RenderGraphResourceSizePolicy.BloomMipChain),
            OwnedImageResource(RenderGraphResourceId.AmbientOcclusionRaw, "Ambient occlusion raw",
                RenderTargetManager.AmbientOcclusionFormat, RenderGraphResourceSizePolicy.HalfResolution),
            OwnedImageResource(RenderGraphResourceId.AmbientOcclusionBlurred, "Ambient occlusion blurred",
                RenderTargetManager.AmbientOcclusionFormat, RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageResource(RenderGraphResourceId.AmbientOcclusionScratch, "Ambient occlusion scratch",
                RenderTargetManager.AmbientOcclusionFormat, RenderGraphResourceSizePolicy.HalfResolution),
            OwnedImageResource(RenderGraphResourceId.GtaoRaw,
                "GTAO raw bent normal and visibility",
                RenderTargetManager.GtaoRadianceFormat,
                RenderGraphResourceSizePolicy.HalfResolution),
            OwnedImageResource(RenderGraphResourceId.GtaoSpatialScratch,
                "GTAO spatial debug scratch",
                RenderTargetManager.GtaoRadianceFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageChainResource(RenderGraphResourceId.GtaoHistory,
                "GTAO history",
                RenderTargetManager.GtaoRadianceFormat,
                RenderGraphResourceSizePolicy.HalfResolution),
            OwnedImageChainResource(RenderGraphResourceId.GtaoGeometryHistory,
                "GTAO geometry history",
                RenderTargetManager.GtaoGeometryHistoryFormat,
                RenderGraphResourceSizePolicy.HalfResolution),
            OwnedImageResource(RenderGraphResourceId.GtaoFiltered,
                "GTAO filtered bent normal and visibility",
                RenderTargetManager.GtaoRadianceFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            BufferSetResource(RenderGraphResourceId.DdgiProbeResources, "DDGI probe resources"),
            BufferSetResource(RenderGraphResourceId.TlasStorage, "TLAS storage"),
            BufferSetResource(RenderGraphResourceId.RayQueryInstanceMetadata, "Ray-query instance metadata"),
            BufferSetResource(RenderGraphResourceId.MeshGeometryBuffers, "Mesh geometry buffers"),
            BufferSetResource(RenderGraphResourceId.MaterialBuffers, "Material buffers"),
            ImageChainResource(RenderGraphResourceId.MaterialTextures, "Material textures", Format.R8G8B8A8Unorm,
                RenderGraphResourceSizePolicy.Dynamic),
            BufferSetResource(RenderGraphResourceId.LightBuffers, "Light buffers"),
            BufferSetResource(RenderGraphResourceId.EnvironmentData, "Environment data"),
            BufferSetResource(RenderGraphResourceId.RendererDiagnosticsBuffer, "Renderer diagnostics"),
            BufferSetResource(RenderGraphResourceId.DdgiEmissiveSources, "DDGI emissive sources"),
            BufferSetResource(RenderGraphResourceId.FarFieldParameters, "Far-field parameters"),
            BufferSetResource(RenderGraphResourceId.FarFieldVoxels, "Far-field voxel and distance buffers"),
            BufferSetResource(RenderGraphResourceId.FarFieldInstances, "Far-field instances"),
            BufferSetResource(RenderGraphResourceId.FarFieldJumpFlood, "Far-field jump-flood buffers"),
            BufferSetResource(RenderGraphResourceId.FarFieldPageTable, "Far-field page table"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiParameters, "Simple DDGI parameters"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiIrradianceAtlas, "Simple DDGI irradiance atlas"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiTransportAtlas,
                "Simple DDGI transport irradiance target"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiTransportSourceCache,
                "Simple DDGI transport source cache"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiVisibilityAtlas, "Simple DDGI visibility atlas"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiRayScratch, "Simple DDGI ray scratch"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiProbeState, "Simple DDGI probe state"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiUpdateQueue, "Simple DDGI update queue"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiRelocationData,
                "Simple DDGI relocation and classification"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiScheduler, "Simple DDGI GPU scheduler arena"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiReceiverProbes, "Simple DDGI compact receiver probes"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiResidency, "Simple DDGI probe residency arena"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiLightTree, "Simple DDGI local-light hierarchy"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiDirectionalRadiance,
                "Simple DDGI directional-radiance SH"),
            BufferSetResource(RenderGraphResourceId.DdgiFoliageProxyPatches, "DDGI foliage proxy generation patches"),
            BufferSetResource(RenderGraphResourceId.DdgiFoliageProxyGeometry, "DDGI foliage proxy AS geometry"),
            BufferSetResource(RenderGraphResourceId.DynamicBlasStorage, "Dynamic DDGI BLAS storage"),
            BufferSetResource(RenderGraphResourceId.DirectionalRayShadowMask, "Directional ray-shadow mask buffers"),
            BufferSetResource(RenderGraphResourceId.AreaRayShadowMask, "Area-light ray-shadow mask buffers"),
            BufferSetResource(RenderGraphResourceId.DirectionalShadowRaw, "Directional shadow raw visibility buffers"),
            BufferSetResource(RenderGraphResourceId.DirectionalShadowHistory,
                "Directional shadow temporal history buffers"),
            BufferSetResource(RenderGraphResourceId.DirectionalShadowScratch,
                "Directional shadow filter scratch buffers"),
            BufferSetResource(RenderGraphResourceId.DirectionalShadowDiagnostics,
                "Directional shadow per-pixel diagnostics"),
            BufferSetResource(RenderGraphResourceId.DirectionalShadowCounters, "Directional shadow aggregate counters"),
            OwnedImageResource(RenderGraphResourceId.FogOutput, "Fog output",
                RenderTargetManager.FoggedSceneColorFormat, RenderGraphResourceSizePolicy.Swapchain),
            ImageResource(RenderGraphResourceId.DirectionalShadowMap, "Directional shadow map", depthFormat,
                RenderGraphResourceSizePolicy.ShadowMap),
            ImageResource(RenderGraphResourceId.SpotShadowAtlas, "Spot shadow atlas", depthFormat,
                RenderGraphResourceSizePolicy.ShadowMap),
            ImageResource(RenderGraphResourceId.PointShadowCubemapArray, "Point shadow cubemap array", depthFormat,
                RenderGraphResourceSizePolicy.ShadowMap),
            ImageChainResource(RenderGraphResourceId.HiZPyramid, "Hi-Z pyramid", depthFormat,
                RenderGraphResourceSizePolicy.HalfResolution),
            BufferSetResource(RenderGraphResourceId.ParticleBuffers, "CPU particle buffers"),
            BufferSetResource(RenderGraphResourceId.GpuParticleBuffers, "GPU particle buffers"),
            BufferSetResource(RenderGraphResourceId.GpuParticleState, "GPU particle state buffers"),
            BufferSetResource(RenderGraphResourceId.GpuParticleIndices, "GPU particle index buffers"),
            BufferSetResource(RenderGraphResourceId.GpuParticleEmitterData, "GPU particle emitter and curve buffers"),
            BufferSetResource(RenderGraphResourceId.GpuParticleCounters, "GPU particle counters"),
            BufferSetResource(RenderGraphResourceId.GpuParticleUnsortedOutput, "GPU particle unsorted output"),
            BufferSetResource(RenderGraphResourceId.GpuParticleRenderOutput, "GPU particle render output"),
            BufferSetResource(RenderGraphResourceId.GpuParticleIndirectArguments, "GPU particle indirect arguments"),
            BufferSetResource(RenderGraphResourceId.GpuParticleSortKeys, "GPU particle sort keys"),
            BufferSetResource(RenderGraphResourceId.GpuParticleCounterReadback, "GPU particle counter readback"),
            BufferSetResource(RenderGraphResourceId.FoliageBuffers, "Foliage buffers"),
            BufferSetResource(RenderGraphResourceId.SceneSubmissionBuffers, "Scene submission buffers"),
            BufferSetResource(RenderGraphResourceId.ForwardVisibilityBuffers, "Forward visibility buffers"),
            BufferSetResource(RenderGraphResourceId.SkinningBuffers, "Skinning buffers"),
            BufferSetResource(RenderGraphResourceId.LightTiles, "Light tile buffers"),
            ImageResource(RenderGraphResourceId.SwapchainColor, "Swapchain color", swapchainColorFormat,
                RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.SmaaEdges, "SMAA edges", RenderTargetManager.SmaaEdgesFormat,
                RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.SmaaBlendWeights, "SMAA blend weights",
                RenderTargetManager.SmaaBlendWeightsFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageChainResource(RenderGraphResourceId.TaaHistory, "TAA history",
                RenderTargetManager.LdrSceneColorFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.WeightedOitAccumulation, "Weighted OIT accumulation",
                RenderTargetManager.WeightedOitAccumulationFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.WeightedOitRevealage, "Weighted OIT revealage",
                RenderTargetManager.WeightedOitRevealageFormat, RenderGraphResourceSizePolicy.Swapchain),
            TransientImageResource(
                RenderGraphResourceId.HybridReflectionReceiverPayload,
                "Hybrid reflection receiver payload",
                RenderTargetManager.HybridReflectionReceiverPayloadFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            TransientImageResource(
                RenderGraphResourceId.HybridReflectionRawRadiance,
                "Hybrid reflection raw radiance and confidence",
                RenderTargetManager.HybridReflectionRadianceFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            TransientImageResource(
                RenderGraphResourceId.HybridReflectionRawMetadata,
                "Hybrid reflection raw source metadata",
                RenderTargetManager.HybridReflectionRawMetadataFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageChainResource(
                RenderGraphResourceId.HybridReflectionHistory,
                "Hybrid reflection double-buffered radiance history",
                RenderTargetManager.HybridReflectionRadianceFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageChainResource(
                RenderGraphResourceId.HybridReflectionMoments,
                "Hybrid reflection double-buffered luminance moments",
                RenderTargetManager.HybridReflectionMomentsFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageChainResource(
                RenderGraphResourceId.HybridReflectionHistoryMetadata,
                "Hybrid reflection double-buffered history metadata",
                RenderTargetManager.HybridReflectionHistoryMetadataFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            TransientImageResource(
                RenderGraphResourceId.HybridReflectionFilterScratch,
                "Hybrid reflection spatial-filter scratch",
                RenderTargetManager.HybridReflectionRadianceFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            TransientImageResource(
                RenderGraphResourceId.HybridReflectionDdgiCohorts,
                "Hybrid reflection sparse DDGI cohort records",
                RenderTargetManager.HybridReflectionRadianceFormat,
                RenderGraphResourceSizePolicy.SceneResolution),
            BufferSetResource(RenderGraphResourceId.HybridReflectionRayTasks,
                "Hybrid reflection bounded ray-query tasks"),
            BufferSetResource(RenderGraphResourceId.HybridReflectionCounters,
                "Hybrid reflection source and budget counters"),
            BufferSetResource(
                RenderGraphResourceId.HybridReflectionIndirectArguments,
                "Hybrid reflection indirect dispatch arguments"),
            ImageChainResource(RenderGraphResourceId.ReflectionProbeCubemaps, "Reflection probe cubemaps",
                Format.R16G16B16A16Sfloat, RenderGraphResourceSizePolicy.Fixed),
            ImageChainResource(RenderGraphResourceId.EnvironmentMaps, "Environment maps", Format.R16G16B16A16Sfloat,
                RenderGraphResourceSizePolicy.Fixed),
            new RenderGraphResourceDescriptor(
                RenderGraphResourceId.TransientIntermediate,
                "Transient intermediates",
                RenderGraphResourceKind.External,
                null,
                RenderGraphResourceSizePolicy.Dynamic,
                RenderGraphResourceLifetime.Transient,
                Persistent: false)
        ];
    }

    /// <summary>
    /// Optional resource inventory for an already-admitted graph variant.  No
    /// descriptor is registered for a disabled mode, which makes disabled
    /// allocations observable rather than relying on lazy allocation.
    /// </summary>
    public IReadOnlyList<RenderGraphResourceDescriptor> CreateResourceDescriptors(
        Format depthFormat,
        Format swapchainColorFormat,
        in AdvancedGiRenderGraphModes modes)
    {
        if (!modes.HasGpuFeature)
            return CreateResourceDescriptors(depthFormat, swapchainColorFormat);

        var descriptors = new List<RenderGraphResourceDescriptor>(
            CreateResourceDescriptors(depthFormat, swapchainColorFormat));
        // B1 buffers are allocated and fence-retired by
        // SimpleDdgiReceiverFeedbackCoordinator. Registering parallel graph
        // descriptors here would suggest a second owner and violate the exact
        // central-memory accounting contract.
        if (modes.UsesOpacityMicromaps)
        {
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.OpacityMicromapResources,
                "EXT opacity-micromap resources"));
            descriptors.Add(TransientBufferSetResource(
                RenderGraphResourceId.OpacityMicromapBuildScratch,
                "EXT opacity-micromap build scratch"));
            descriptors.Add(TransientBufferSetResource(
                RenderGraphResourceId.OpacityMicromapCompactionHeadroom,
                "EXT opacity-micromap compaction headroom"));
        }

        if (modes.UsesDirectionalGuiding)
        {
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.SimpleDdgiGuidingDistributions,
                "Simple-DDGI directional guiding distribution banks"));
            descriptors.Add(TransientBufferSetResource(
                RenderGraphResourceId.SimpleDdgiGuidingScratch,
                "Simple-DDGI directional guiding training scratch"));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.SimpleDdgiGuidingDirectionPayloadSidecar,
                "Simple-DDGI source-cache direction/PDF sidecar"));
        }

        if (modes.UsesCausticWorldCache)
        {
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.GiCausticTasks,
                "Tagged caustic photon tasks"));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.GiCausticPhotons,
                "Tagged caustic photon append banks"));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.GiCausticCache,
                "Tagged caustic world-cache banks"));
            descriptors.Add(TransientBufferSetResource(
                RenderGraphResourceId.GiCausticScratch,
                "Tagged caustic sort/cache scratch"));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.GiCausticReceiverPayload,
                "C4 visible receiver material payload",
                GiCausticScreenGpuAbi.ReceiverPayloadFormat,
                RenderGraphResourceSizePolicy.SceneResolution));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.GiCausticRadiance,
                "C4 separately owned tagged radiance",
                GiCausticScreenGpuAbi.RadianceFormat,
                RenderGraphResourceSizePolicy.SceneResolution));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.GiCausticMoments,
                "C4 resolve confidence and luminance moments",
                GiCausticScreenGpuAbi.MomentsFormat,
                RenderGraphResourceSizePolicy.SceneResolution));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.GiCausticScreenFrameConstants,
                "C4 immutable screen reconstruction constants"));
        }

        if (modes.UsesNearFieldHiZResidual)
        {
            RenderGraphResourceSizePolicy traceSizePolicy =
                modes.NearFieldProfile.TraceSizePolicy;
            RenderGraphResourceSizePolicy sourceSizePolicy =
                modes.NearFieldProfile.SourceProducerMode ==
                    SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster
                    ? traceSizePolicy
                    : RenderGraphResourceSizePolicy.SceneResolution;
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.NearFieldDirectSource,
                "Near-field direct-diffuse plus emissive source",
                Format.R16G16B16A16Sfloat,
                sourceSizePolicy));
            if (modes.NearFieldProfile.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster)
            {
                descriptors.Add(TransientImageResource(
                    RenderGraphResourceId.NearFieldTraceRasterDepth,
                    "Near-field trace-resolution source depth",
                    depthFormat,
                    traceSizePolicy));
            }
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.NearFieldResidualRaw,
                "Near-field residual raw candidates",
                Format.R16G16B16A16Sfloat,
                traceSizePolicy));
            descriptors.Add(OwnedImageChainResource(
                RenderGraphResourceId.NearFieldResidualHistory,
                "Near-field residual double-buffered history",
                Format.R16G16B16A16Sfloat,
                traceSizePolicy));
            descriptors.Add(OwnedImageChainResource(
                RenderGraphResourceId.NearFieldResidualMoments,
                "Near-field residual double-buffered moments",
                Format.R16G16Sfloat,
                traceSizePolicy));
            descriptors.Add(OwnedImageChainResource(
                RenderGraphResourceId.NearFieldResidualValidity,
                "Near-field residual double-buffered validity",
                Format.R16Uint,
                traceSizePolicy));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                "Near-field residual double-buffered hit identity SSBO"));
            descriptors.Add(OwnedImageChainResource(
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                "Near-field residual packed receiver-normal history",
                Format.R32Uint,
                traceSizePolicy));
            if (modes.UsesNearFieldFiltering)
            {
                descriptors.Add(TransientImageResource(
                    RenderGraphResourceId.NearFieldResidualFilterScratch,
                    "Near-field residual filter scratch (Raw is peer target)",
                    Format.R16G16B16A16Sfloat,
                    traceSizePolicy));
            }

            descriptors.Add(TransientBufferSetResource(
                RenderGraphResourceId.NearFieldResidualTileBuffers,
                "Near-field residual compact tiles"));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.NearFieldSurfaceTable,
                "Near-field frame-buffered surface table"));
            descriptors.Add(TransientBufferSetResource(
                RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments,
                "Near-field active tiles and indirect dispatch arguments"));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.NearFieldResidualTraceFrameConstants,
                "Near-field residual per-frame reconstruction constants"));
            descriptors.Add(BufferSetResource(
                RenderGraphResourceId.NearFieldResidualSchedulerHistory,
                "Near-field residual double-buffered tile scheduler history"));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.NearFieldReceiverPayload,
                "Near-field C5 compact receiver payload",
                Format.R32G32B32A32Uint,
                sourceSizePolicy));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                "Near-field prepared linear depth and B3 footprint",
                Format.R32G32Sfloat,
                traceSizePolicy));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                "Near-field prepared receiver payload",
                Format.R32G32B32A32Uint,
                traceSizePolicy));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.NearFieldPreparedMotion,
                "Near-field prepared receiver motion",
                Format.R16G16Sfloat,
                traceSizePolicy));
            descriptors.Add(TransientImageResource(
                RenderGraphResourceId.NearFieldSourceLuminance,
                "Near-field source-guiding luminance base level",
                Format.R16Sfloat,
                traceSizePolicy));
        }

        return descriptors;
    }

    public void RegisterResources(RenderGraph graph, Format depthFormat, Format swapchainColorFormat)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));

        graph.RegisterResources(CreateResourceDescriptors(depthFormat, swapchainColorFormat));
    }

    public void RegisterResources(
        RenderGraph graph,
        Format depthFormat,
        Format swapchainColorFormat,
        in AdvancedGiRenderGraphModes modes)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));

        graph.RegisterResources(CreateResourceDescriptors(
            depthFormat,
            swapchainColorFormat,
            modes));
    }

    public void DeclarePassResources(RenderGraph graph)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));

        foreach (RenderGraphPassResourceDeclaration declaration in CreatePassResourceDeclarations())
            graph.DeclarePassResources(declaration.PassName, declaration.Usages);
    }

    public void DeclarePassResources(
        RenderGraph graph,
        in AdvancedGiRenderGraphModes modes)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));

        foreach (RenderGraphPassResourceDeclaration declaration in
                 CreatePassResourceDeclarations(modes))
        {
            graph.DeclarePassResources(declaration.PassName, declaration.Usages);
        }
    }

    public IReadOnlyList<string> GetActivePasses(RenderFeatureIsolationMode featureIsolation)
    {
        return GetActivePasses(featureIsolation, TransparencyMode.SortedAlphaBlend);
    }

    public IReadOnlyList<string> GetActivePasses(RenderFeatureIsolationMode featureIsolation,
        TransparencyMode transparencyMode)
    {
        var activePasses = new List<string>(PassOrder.Count);
        foreach (string passName in PassOrder)
        {
            if (passName == "TransparentForwardPass" && transparencyMode != TransparencyMode.SortedAlphaBlend)
                continue;
            if ((passName == "WeightedTransparentPass" || passName == "WeightedOitCompositePass") &&
                transparencyMode != TransparencyMode.WeightedBlendedOit)
                continue;
            if (RenderFeatureIsolationPolicy.ShouldExecutePass(featureIsolation, passName))
                activePasses.Add(passName);
        }

        return activePasses;
    }

    public void RegisterPasses(RenderGraph graph, IReadOnlyDictionary<string, RenderPassBase> passes)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        if (passes == null)
            throw new ArgumentNullException(nameof(passes));

        foreach (string passName in PassOrder)
        {
            if (!passes.TryGetValue(passName, out RenderPassBase? pass))
                throw new InvalidOperationException(
                    $"Production pipeline pass '{passName}' was not provided by the renderer.");

            graph.AddPass(pass);
        }
    }

    /// <summary>Registers the exact pass set selected during graph creation.</summary>
    public void RegisterPasses(
        RenderGraph graph,
        IReadOnlyDictionary<string, RenderPassBase> passes,
        in AdvancedGiRenderGraphModes modes)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        if (passes == null)
            throw new ArgumentNullException(nameof(passes));

        foreach (string passName in CreatePassOrder(modes))
        {
            if (!passes.TryGetValue(passName, out RenderPassBase? pass))
            {
                throw new InvalidOperationException(
                    $"Production pipeline pass '{passName}' was not provided by the renderer.");
            }

            graph.AddPass(pass);
        }
    }

    public void ValidatePassOrder(IReadOnlyList<string> actualPassOrder)
    {
        if (actualPassOrder == null)
            throw new ArgumentNullException(nameof(actualPassOrder));

        IReadOnlyList<string> expectedPassOrder = PassOrder;
        if (actualPassOrder.Count != expectedPassOrder.Count)
            throw new InvalidOperationException(
                $"Render graph pass count changed. Expected {string.Join(", ", expectedPassOrder)}; actual {string.Join(", ", actualPassOrder)}.");

        for (int i = 0; i < expectedPassOrder.Count; i++)
        {
            if (!string.Equals(actualPassOrder[i], expectedPassOrder[i], StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Render graph pass order changed. Expected {string.Join(", ", expectedPassOrder)}; actual {string.Join(", ", actualPassOrder)}.");
            }
        }
    }

    public void ValidatePassOrder(
        IReadOnlyList<string> actualPassOrder,
        in AdvancedGiRenderGraphModes modes)
    {
        if (actualPassOrder == null)
            throw new ArgumentNullException(nameof(actualPassOrder));

        IReadOnlyList<string> expectedPassOrder = CreatePassOrder(modes);
        if (actualPassOrder.Count != expectedPassOrder.Count)
        {
            throw new InvalidOperationException(
                $"Render graph pass count changed. Expected {string.Join(", ", expectedPassOrder)}; actual {string.Join(", ", actualPassOrder)}.");
        }

        for (int i = 0; i < expectedPassOrder.Count; i++)
        {
            if (!string.Equals(actualPassOrder[i], expectedPassOrder[i], StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Render graph pass order changed. Expected {string.Join(", ", expectedPassOrder)}; actual {string.Join(", ", actualPassOrder)}.");
            }
        }
    }

    private static RenderGraphPassResourceDeclaration Pass(string passName, params RenderGraphResourceUsage[] usages)
    {
        return new RenderGraphPassResourceDeclaration(passName, usages);
    }

    private static void InsertAfter(
        List<string> order,
        string anchor,
        params string[] inserted)
    {
        if (inserted.Length == 0)
            return;

        int index = order.IndexOf(anchor);
        if (index < 0)
            throw new InvalidOperationException(
                $"Cannot insert advanced-GI passes: anchor '{anchor}' is absent.");
        order.InsertRange(index + 1, inserted);
    }

    private static string GetNearFieldFilterPassName(int iteration)
    {
        if (iteration < 0)
            throw new ArgumentOutOfRangeException(nameof(iteration));

        return iteration == 0
            ? "SimpleDdgiNearFieldResidualFilterPass"
            : "SimpleDdgiNearFieldResidualFilterPass" + iteration;
    }

    private static RenderGraphResourceId NearFieldFilterTargetResource(
        int iterationCount,
        int iteration)
    {
        if (iterationCount <= 0 ||
            iteration < 0 || iteration >= iterationCount)
            throw new ArgumentOutOfRangeException(nameof(iteration));
        bool rawTarget = ((iteration + (iterationCount & 1)) & 1) == 0;
        return rawTarget
            ? RenderGraphResourceId.NearFieldResidualRaw
            : RenderGraphResourceId.NearFieldResidualFilterScratch;
    }

    private static RenderGraphResourceDescriptor ImageResource(
        RenderGraphResourceId id,
        string debugName,
        Format format,
        RenderGraphResourceSizePolicy sizePolicy)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.Image,
            format,
            sizePolicy,
            RenderGraphResourceLifetime.Imported,
            Persistent: true);
    }

    private static RenderGraphResourceDescriptor ImageChainResource(
        RenderGraphResourceId id,
        string debugName,
        Format format,
        RenderGraphResourceSizePolicy sizePolicy)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.ImageChain,
            format,
            sizePolicy,
            RenderGraphResourceLifetime.Imported,
            Persistent: true);
    }

    private static RenderGraphResourceDescriptor OwnedImageResource(
        RenderGraphResourceId id,
        string debugName,
        Format format,
        RenderGraphResourceSizePolicy sizePolicy)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.Image,
            format,
            sizePolicy,
            RenderGraphResourceLifetime.Persistent,
            Persistent: true);
    }

    private static RenderGraphResourceDescriptor TransientImageResource(
        RenderGraphResourceId id,
        string debugName,
        Format format,
        RenderGraphResourceSizePolicy sizePolicy)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.Image,
            format,
            sizePolicy,
            RenderGraphResourceLifetime.Transient,
            Persistent: false);
    }

    private static RenderGraphResourceDescriptor TransientImageChainResource(
        RenderGraphResourceId id,
        string debugName,
        Format format,
        RenderGraphResourceSizePolicy sizePolicy)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.ImageChain,
            format,
            sizePolicy,
            RenderGraphResourceLifetime.Transient,
            Persistent: false);
    }

    private static RenderGraphResourceDescriptor OwnedImageChainResource(
        RenderGraphResourceId id,
        string debugName,
        Format format,
        RenderGraphResourceSizePolicy sizePolicy)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.ImageChain,
            format,
            sizePolicy,
            RenderGraphResourceLifetime.Persistent,
            Persistent: true);
    }

    private static RenderGraphResourceDescriptor BufferSetResource(RenderGraphResourceId id, string debugName)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.BufferSet,
            null,
            RenderGraphResourceSizePolicy.Dynamic,
            RenderGraphResourceLifetime.Imported,
            Persistent: true);
    }

    private static RenderGraphResourceUsage Read(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(resource, RenderGraphResourceAccess.Read);
    }

    private static RenderGraphResourceUsage ReadFragmentSampled(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadFragmentShadingRate(
        RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.FragmentShadingRateAttachmentBitKhr,
            AccessFlags2.FragmentShadingRateAttachmentReadBitKhr,
            ImageLayout.FragmentShadingRateAttachmentOptimalKhr,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadComputeSampled(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal,
            RenderGraphQueueIntent.Compute,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceUsage ReadDepth(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit |
            PipelineStageFlags2.EarlyFragmentTestsBit,
            AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit,
            ImageLayout.DepthStencilReadOnlyOptimal,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadComputeDepth(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.DepthStencilReadOnlyOptimal,
            RenderGraphQueueIntent.Compute);
    }

    private static RenderGraphResourceUsage Write(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(resource, RenderGraphResourceAccess.Write);
    }

    private static RenderGraphResourceUsage ReadComputeBuffer(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceDescriptor TransientBufferSetResource(
        RenderGraphResourceId id,
        string debugName)
    {
        return new RenderGraphResourceDescriptor(
            id,
            debugName,
            RenderGraphResourceKind.BufferSet,
            null,
            RenderGraphResourceSizePolicy.Dynamic,
            RenderGraphResourceLifetime.Transient,
            Persistent: false);
    }

    // Compute indirect dispatch parameters are consumed at DRAW_INDIRECT even
    // though the dispatched pipeline is compute. Keep that stage/access in
    // the graph so an async queue handoff cannot expose stale dimensions.
    private static RenderGraphResourceUsage ReadWriteComputeIndirectBuffer(
        RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit |
            AccessFlags2.IndirectCommandReadBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute);
    }

    private static RenderGraphResourceUsage ReadComputeIndirectBuffer(
        RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit |
            PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.IndirectCommandReadBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute);
    }

    private static RenderGraphResourceUsage ReadComputeAccelerationStructure(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.AccelerationStructureReadBitKhr,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute);
    }

    private static RenderGraphResourceUsage ReadGraphicsStorage(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.TaskShaderBitExt |
            PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceUsage ReadFragmentAccelerationStructure(
        RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.AccelerationStructureReadBitKhr,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadWriteGraphicsStorage(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.TaskShaderBitExt |
            PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage WriteGraphicsStorage(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceUsage ReadAccelerationStructureBuildInput(
        RenderGraphResourceId resource) =>
        new(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            AccessFlags2.AccelerationStructureReadBitKhr,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);

    private static RenderGraphResourceUsage WriteAccelerationStructureBuild(
        RenderGraphResourceId resource) =>
        new(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            AccessFlags2.AccelerationStructureWriteBitKhr,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);

    private static RenderGraphResourceUsage
        ReadWriteMicromapAndAccelerationStructureBuild(
            RenderGraphResourceId resource) =>
        new(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.TransferBit |
            PipelineStageFlags2.MicromapBuildBitExt |
            PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            AccessFlags2.TransferWriteBit |
            AccessFlags2.MicromapReadBitExt |
            AccessFlags2.MicromapWriteBitExt |
            AccessFlags2.AccelerationStructureReadBitKhr |
            AccessFlags2.AccelerationStructureWriteBitKhr,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);

    private static RenderGraphResourceUsage ReadGraphicsAndComputeStorage(
        RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.TaskShaderBitExt |
            PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.FragmentShaderBit |
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadWriteGraphicsAndComputeStorage(
        RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.TaskShaderBitExt |
            PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.FragmentShaderBit |
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadGraphicsIndirect(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.IndirectCommandReadBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage WriteColorAttachment(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit,
            ImageLayout.ColorAttachmentOptimal,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage WriteDepthAttachment(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthStencilAttachmentOptimal,
            RenderGraphQueueIntent.Graphics);
    }

    // Forward+ is a strict consumer of the current-frame DepthPrePass output. A separate
    // depth-writing forward fallback would need its own pipeline and graph declaration.
    private static RenderGraphResourceUsage ReadDepthAttachment(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.EarlyFragmentTestsBit |
            PipelineStageFlags2.LateFragmentTestsBit |
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.DepthStencilAttachmentReadBit |
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.DepthStencilReadOnlyOptimal,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadDepthAttachmentAndCompute(
        RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.EarlyFragmentTestsBit |
            PipelineStageFlags2.LateFragmentTestsBit |
            PipelineStageFlags2.FragmentShaderBit |
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.DepthStencilAttachmentReadBit |
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.DepthStencilReadOnlyOptimal,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage WriteComputeStorage(
        RenderGraphResourceId resource,
        ImageLayout finalImageLayout = ImageLayout.Undefined,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.General,
            RenderGraphQueueIntent.Compute,
            finalImageLayout,
            historyBinding);
    }

    private static RenderGraphResourceUsage WriteTransferStorage(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            ImageLayout.General,
            RenderGraphQueueIntent.Compute,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceUsage WriteComputeBuffer(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceUsage WriteTransferAndComputeBuffer(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.TransferBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceUsage ReadWriteComputeBuffer(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit |
            AccessFlags2.TransferWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute,
            HistoryBinding: historyBinding);
    }

    private static RenderGraphResourceUsage ReadWrite(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(resource, RenderGraphResourceAccess.ReadWrite);
    }

    private static RenderGraphResourceUsage ReadWriteColorAttachment(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit,
            ImageLayout.ColorAttachmentOptimal,
            RenderGraphQueueIntent.Graphics);
    }

    private static RenderGraphResourceUsage ReadWriteComputeStorage(
        RenderGraphResourceId resource,
        ImageLayout finalImageLayout = ImageLayout.Undefined,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.General,
            RenderGraphQueueIntent.Compute,
            finalImageLayout,
            historyBinding);
    }

    private static RenderGraphResourceUsage ReadComputeStorage(
        RenderGraphResourceId resource,
        RenderGraphHistoryBindingSelection historyBinding =
            RenderGraphHistoryBindingSelection.All)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            ImageLayout.General,
            RenderGraphQueueIntent.Compute,
            HistoryBinding: historyBinding);
    }
}

internal sealed record RenderGraphPassResourceDeclaration(
    string PassName,
    RenderGraphResourceUsage[] Usages);
