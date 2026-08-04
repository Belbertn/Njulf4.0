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
        "ForwardVisibilityCompactionPass",
        "AmbientOcclusionPass",
        "AmbientOcclusionBlurPass",
        "TiledLightCullingPass",
        "EnvironmentPrefilterPass",
        "ForwardPlusPass",
        "FarFieldClipmapBakePass",
        "SimpleDdgiSchedulePass",
        "SimpleDdgiTracePass",
        "SimpleDdgiRelocateClassifyPass",
        "SimpleDdgiTransportPass",
        "SimpleDdgiBlendPass",
        "SimpleDdgiPublishPass",
        "SimpleDdgiSchedulerCommitPass",
        "SkyboxPass",
        "TransparentForwardPass",
        "WeightedTransparentPass",
        "WeightedOitCompositePass",
        "GpuParticleResetPass",
        "GpuParticleSimulatePass",
        "GpuParticleSortPass",
        "ParticlePass",
        "DebugDrawPass",
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

    public string Name => PipelineName;

    public IReadOnlyList<RenderGraphPassResourceDeclaration> PassResourceDeclarations =>
        CreatePassResourceDeclarations();

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
            WriteColorAttachment(RenderGraphResourceId.MotionVectors)),
            Pass("HiZBuildPass",
            ReadComputeDepth(RenderGraphResourceId.SceneDepth),
            WriteComputeStorage(RenderGraphResourceId.HiZPyramid, ImageLayout.ShaderReadOnlyOptimal)),
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
            ReadWriteComputeStorage(RenderGraphResourceId.AmbientOcclusionScratch, ImageLayout.ShaderReadOnlyOptimal),
            WriteComputeStorage(RenderGraphResourceId.AmbientOcclusionBlurred, ImageLayout.ShaderReadOnlyOptimal)),
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
            Pass("ForwardPlusPass",
                ReadDepthAttachment(RenderGraphResourceId.SceneDepth),
                Read(RenderGraphResourceId.SceneSubmissionBuffers),
                Read(RenderGraphResourceId.ForwardVisibilityBuffers),
                Read(RenderGraphResourceId.FoliageBuffers),
                Read(RenderGraphResourceId.LightTiles),
                ReadFragmentSampled(RenderGraphResourceId.AmbientOcclusionBlurred),
                Read(RenderGraphResourceId.DirectionalShadowMap),
                Read(RenderGraphResourceId.SpotShadowAtlas),
                Read(RenderGraphResourceId.PointShadowCubemapArray),
                Read(RenderGraphResourceId.ReflectionProbeCubemaps),
                Read(RenderGraphResourceId.EnvironmentMaps),
                ReadGraphicsStorage(RenderGraphResourceId.MeshGeometryBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.MaterialBuffers),
                ReadFragmentSampled(RenderGraphResourceId.MaterialTextures),
                ReadGraphicsStorage(RenderGraphResourceId.LightBuffers),
                ReadGraphicsStorage(RenderGraphResourceId.EnvironmentData),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteGraphicsStorage(RenderGraphResourceId.RendererDiagnosticsBuffer),
                WriteColorAttachment(RenderGraphResourceId.SceneColor)));

        // DDGI update runs after ForwardPlusPass and publishes cache data for subsequent frames.
        // DDGI paths deliberately declare every concrete storage family they touch. A scheduler
        // rejects the path if even one binding is unavailable rather than treating BufferSet or
        // External as an opaque unit and risking an unpaired queue-family handoff.
        declarations.AddRange([
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
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue)),
            Pass("SimpleDdgiTracePass",
                ReadComputeAccelerationStructure(RenderGraphResourceId.TlasStorage),
                ReadComputeBuffer(RenderGraphResourceId.RayQueryInstanceMetadata),
                ReadComputeBuffer(RenderGraphResourceId.MeshGeometryBuffers),
                ReadComputeBuffer(RenderGraphResourceId.MaterialBuffers),
                ReadComputeSampled(RenderGraphResourceId.MaterialTextures),
                ReadComputeBuffer(RenderGraphResourceId.LightBuffers),
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
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.RendererDiagnosticsBuffer)),
            Pass("SimpleDdgiRelocateClassifyPass",
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiRayScratch),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiRelocationData),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler)),
            Pass("SimpleDdgiTransportPass",
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
            Pass("SimpleDdgiPublishPass",
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiParameters),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler)),
            Pass("SimpleDdgiSchedulerCommitPass",
                ReadWriteComputeIndirectBuffer(RenderGraphResourceId.SimpleDdgiScheduler),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiProbeState),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiUpdateQueue),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiIrradianceAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiVisibilityAtlas),
                ReadWriteComputeBuffer(RenderGraphResourceId.SimpleDdgiTransportAtlas)),
            Pass("SkyboxPass",
            ReadDepth(RenderGraphResourceId.SceneDepth),
                ReadFragmentSampled(RenderGraphResourceId.EnvironmentMaps),
                ReadGraphicsStorage(RenderGraphResourceId.EnvironmentData),
                ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("TransparentForwardPass",
            ReadDepth(RenderGraphResourceId.SceneDepth),
            Read(RenderGraphResourceId.DirectionalShadowMap),
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
            ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiProbeState),
            ReadWriteGraphicsStorage(RenderGraphResourceId.RendererDiagnosticsBuffer),
            ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("WeightedTransparentPass",
            ReadDepth(RenderGraphResourceId.SceneDepth),
            Read(RenderGraphResourceId.DirectionalShadowMap),
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
            ReadGraphicsStorage(RenderGraphResourceId.SimpleDdgiProbeState),
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
            ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("DebugDrawPass",
            ReadDepth(RenderGraphResourceId.SceneDepth),
            ReadWriteColorAttachment(RenderGraphResourceId.SceneColor)),
            Pass("FogPass",
            ReadComputeSampled(RenderGraphResourceId.SceneColor),
            ReadComputeDepth(RenderGraphResourceId.SceneDepth),
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

    public IReadOnlyList<RenderGraphResourceDescriptor> CreateResourceDescriptors(
        Format depthFormat,
        Format swapchainColorFormat)
    {
        return
        [
            ImageResource(RenderGraphResourceId.SceneColor, "Scene color", RenderTargetManager.SceneColorFormat, RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageResource(RenderGraphResourceId.LdrSceneColor, "LDR scene color", RenderTargetManager.LdrSceneColorFormat, RenderGraphResourceSizePolicy.Swapchain),
            ImageResource(RenderGraphResourceId.SceneDepth, "Scene depth", depthFormat, RenderGraphResourceSizePolicy.SceneResolution),
            OwnedImageResource(RenderGraphResourceId.MotionVectors, "Motion vectors", RenderTargetManager.MotionVectorFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageChainResource(RenderGraphResourceId.BloomChain, "Bloom chain", RenderTargetManager.SceneColorFormat, RenderGraphResourceSizePolicy.BloomMipChain),
            OwnedImageResource(RenderGraphResourceId.AmbientOcclusionRaw, "Ambient occlusion raw", RenderTargetManager.AmbientOcclusionFormat, RenderGraphResourceSizePolicy.HalfResolution),
            OwnedImageResource(RenderGraphResourceId.AmbientOcclusionBlurred, "Ambient occlusion blurred", RenderTargetManager.AmbientOcclusionFormat, RenderGraphResourceSizePolicy.HalfResolution),
            OwnedImageResource(RenderGraphResourceId.AmbientOcclusionScratch, "Ambient occlusion scratch", RenderTargetManager.AmbientOcclusionFormat, RenderGraphResourceSizePolicy.HalfResolution),
            BufferSetResource(RenderGraphResourceId.DdgiProbeResources, "DDGI probe resources"),
            BufferSetResource(RenderGraphResourceId.TlasStorage, "TLAS storage"),
            BufferSetResource(RenderGraphResourceId.RayQueryInstanceMetadata, "Ray-query instance metadata"),
            BufferSetResource(RenderGraphResourceId.MeshGeometryBuffers, "Mesh geometry buffers"),
            BufferSetResource(RenderGraphResourceId.MaterialBuffers, "Material buffers"),
            ImageChainResource(RenderGraphResourceId.MaterialTextures, "Material textures", Format.R8G8B8A8Unorm, RenderGraphResourceSizePolicy.Dynamic),
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
            BufferSetResource(RenderGraphResourceId.SimpleDdgiTransportAtlas, "Simple DDGI transport irradiance target"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiTransportSourceCache, "Simple DDGI transport source cache"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiVisibilityAtlas, "Simple DDGI visibility atlas"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiRayScratch, "Simple DDGI ray scratch"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiProbeState, "Simple DDGI probe state"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiUpdateQueue, "Simple DDGI update queue"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiRelocationData, "Simple DDGI relocation and classification"),
            BufferSetResource(RenderGraphResourceId.SimpleDdgiScheduler, "Simple DDGI GPU scheduler arena"),
            OwnedImageResource(RenderGraphResourceId.FogOutput, "Fog output", RenderTargetManager.FoggedSceneColorFormat, RenderGraphResourceSizePolicy.Swapchain),
            ImageResource(RenderGraphResourceId.DirectionalShadowMap, "Directional shadow map", depthFormat, RenderGraphResourceSizePolicy.ShadowMap),
            ImageResource(RenderGraphResourceId.SpotShadowAtlas, "Spot shadow atlas", depthFormat, RenderGraphResourceSizePolicy.ShadowMap),
            ImageResource(RenderGraphResourceId.PointShadowCubemapArray, "Point shadow cubemap array", depthFormat, RenderGraphResourceSizePolicy.ShadowMap),
            ImageChainResource(RenderGraphResourceId.HiZPyramid, "Hi-Z pyramid", depthFormat, RenderGraphResourceSizePolicy.HalfResolution),
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
            ImageResource(RenderGraphResourceId.SwapchainColor, "Swapchain color", swapchainColorFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.SmaaEdges, "SMAA edges", RenderTargetManager.SmaaEdgesFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.SmaaBlendWeights, "SMAA blend weights", RenderTargetManager.SmaaBlendWeightsFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageChainResource(RenderGraphResourceId.TaaHistory, "TAA history", RenderTargetManager.LdrSceneColorFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.WeightedOitAccumulation, "Weighted OIT accumulation", RenderTargetManager.WeightedOitAccumulationFormat, RenderGraphResourceSizePolicy.Swapchain),
            OwnedImageResource(RenderGraphResourceId.WeightedOitRevealage, "Weighted OIT revealage", RenderTargetManager.WeightedOitRevealageFormat, RenderGraphResourceSizePolicy.Swapchain),
            ImageChainResource(RenderGraphResourceId.ReflectionProbeCubemaps, "Reflection probe cubemaps", Format.R16G16B16A16Sfloat, RenderGraphResourceSizePolicy.Fixed),
            ImageChainResource(RenderGraphResourceId.EnvironmentMaps, "Environment maps", Format.R16G16B16A16Sfloat, RenderGraphResourceSizePolicy.Fixed),
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

    public void RegisterResources(RenderGraph graph, Format depthFormat, Format swapchainColorFormat)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));

        graph.RegisterResources(CreateResourceDescriptors(depthFormat, swapchainColorFormat));
    }

    public void DeclarePassResources(RenderGraph graph)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));

        foreach (RenderGraphPassResourceDeclaration declaration in CreatePassResourceDeclarations())
            graph.DeclarePassResources(declaration.PassName, declaration.Usages);
    }

    public IReadOnlyList<string> GetActivePasses(RenderFeatureIsolationMode featureIsolation)
    {
        return GetActivePasses(featureIsolation, TransparencyMode.SortedAlphaBlend);
    }

    public IReadOnlyList<string> GetActivePasses(RenderFeatureIsolationMode featureIsolation, TransparencyMode transparencyMode)
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
                throw new InvalidOperationException($"Production pipeline pass '{passName}' was not provided by the renderer.");

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

    private static RenderGraphPassResourceDeclaration Pass(string passName, params RenderGraphResourceUsage[] usages)
    {
        return new RenderGraphPassResourceDeclaration(passName, usages);
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

    private static RenderGraphResourceUsage ReadComputeSampled(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal,
            RenderGraphQueueIntent.Compute);
    }

    private static RenderGraphResourceUsage ReadDepth(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.EarlyFragmentTestsBit,
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

    private static RenderGraphResourceUsage ReadComputeBuffer(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Read,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute);
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

    private static RenderGraphResourceUsage ReadGraphicsStorage(RenderGraphResourceId resource)
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

    private static RenderGraphResourceUsage WriteComputeStorage(
        RenderGraphResourceId resource,
        ImageLayout finalImageLayout = ImageLayout.Undefined)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.General,
            RenderGraphQueueIntent.Compute,
            finalImageLayout);
    }

    private static RenderGraphResourceUsage WriteComputeBuffer(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute);
    }

    private static RenderGraphResourceUsage ReadWriteComputeBuffer(RenderGraphResourceId resource)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
            ImageLayout.Undefined,
            RenderGraphQueueIntent.Compute);
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
        ImageLayout finalImageLayout = ImageLayout.Undefined)
    {
        return new RenderGraphResourceUsage(
            resource,
            RenderGraphResourceAccess.ReadWrite,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.General,
            RenderGraphQueueIntent.Compute,
            finalImageLayout);
    }
}

internal sealed record RenderGraphPassResourceDeclaration(
    string PassName,
    RenderGraphResourceUsage[] Usages);
