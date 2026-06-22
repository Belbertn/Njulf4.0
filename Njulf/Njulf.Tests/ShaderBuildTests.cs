using System.Buffers.Binary;
using Njulf.Shaders;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ShaderBuildTests
{
    private static readonly string[] RequiredShaders =
    [
        "depth.task",
        "depth_diagnostics.task",
        "depth.mesh",
        "depth_sided.frag",
        "depth_alpha.mesh",
        "depth_alpha.frag",
        "shadow_depth.task",
        "shadow_depth.mesh",
        "shadow_depth_alpha.mesh",
        "forward.task",
        "forward_diagnostics.task",
        "forward_compacted.task",
        "forward_compacted_diagnostics.task",
        "forward.mesh",
        "forward_simple.mesh",
        "forward.frag",
        "forward_opaque.frag",
        "forward_opaque_simple.frag",
        "forward_opaque_simple_full_input.frag",
        "particle.vert",
        "particle.frag",
        "skinning.comp",
        "lightcull.comp",
        "hiz_downsample.comp",
        "ambient_occlusion.comp",
        "ambient_occlusion_blur.comp",
        "auto_exposure.comp",
        "bloom_extract.comp",
        "bloom_downsample.comp",
        "bloom_upsample.comp",
        "skybox.frag",
        "tonemap_composite.frag",
        "fxaa.frag",
        "smaa_edge.frag",
        "smaa_blend_weight.frag",
        "smaa_neighborhood.frag",
        "motion_vector.task",
        "motion_vector.mesh",
        "motion_vector.frag",
        "foliage_cull.comp",
        "foliage_grass.task",
        "foliage_grass.mesh",
        "foliage_mesh.task",
        "foliage_mesh.mesh",
        "foliage_depth.frag",
        "foliage_forward.frag",
        "foliage_motion.task",
        "foliage_motion.mesh",
        "foliage_motion.frag",
        "taa_resolve.frag"
    ];

    [Test]
    public void RequiredShadersAreEmbeddedAsSpirv()
    {
        var assembly = typeof(ShaderLibrary).Assembly;
        byte[] magicBytes = new byte[4];

        foreach (string shaderName in RequiredShaders)
        {
            string resourceName = $"Njulf.Shaders.{shaderName}";
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);

            Assert.That(stream, Is.Not.Null, $"Missing shader resource '{resourceName}'.");
            Assert.That(stream!.Length, Is.GreaterThanOrEqualTo(4), $"Shader resource '{resourceName}' is empty.");

            Assert.That(stream.Read(magicBytes), Is.EqualTo(4), $"Could not read SPIR-V magic from '{resourceName}'.");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
            Assert.That(magic, Is.EqualTo(0x07230203), $"Shader resource '{resourceName}' is not SPIR-V bytecode.");
        }
    }

    [Test]
    public void AnimationDebugShader_IsolatesSkinnedObjects()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("ANIMATION_DEBUG_SKINNED_OBJECTS"));
            Assert.That(shader, Does.Contain("objectData.SkinningEnabled != 0"));
            Assert.That(shader, Does.Contain("vec3(1.0, 0.0, 0.85)"));
            Assert.That(shader, Does.Contain("discard;"));
        });
    }

    [Test]
    public void ForwardPass_ClearsDepthWhenDepthPrepassIsDisabled()
    {
        string source = ReadRepoText("Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("sceneData.DepthPrePassEnabled ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.DepthStencilAttachmentOptimal"));
            Assert.That(source, Does.Contain("sceneData.DepthPrePassEnabled ? AttachmentLoadOp.Load : AttachmentLoadOp.Clear"));
        });
    }

    [Test]
    public void ForwardShader_HasDirectAndDepthAwareAmbientOcclusionSamplingModes()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("SampleScreenSpaceAoDirect"));
            Assert.That(shader, Does.Contain("SampleScreenSpaceAoDepthAware"));
            Assert.That(shader, Does.Contain("AO_FORWARD_SAMPLING_DIRECT"));
            Assert.That(shader, Does.Contain("AO_FORWARD_SAMPLING_DEPTH_AWARE_UPSAMPLE"));
        });
    }

    [Test]
    public void ForwardShader_ScalesDdgiByCoverageAndComplementsFallback()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("float expectedWeight = 0.0;"));
            Assert.That(shader, Does.Contain("result.coverage = clamp(totalWeight / max(expectedWeight, 0.000001), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float ddgiFieldCoverage = clamp(ddgiCoverage * (1.0 - nearContactSuppression), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float fallbackWeight = clamp(1.0 - ddgiFieldCoverage - ssgiConfidence * 0.25, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 worldField = ddgiDiffuse * ddgiFieldCoverage;"));
            Assert.That(shader, Does.Not.Contain("vec3 worldField = ddgiDiffuse * (1.0 - nearContactSuppression);"));
        });
    }

    [Test]
    public void ForwardShader_DoesNotApplySsgiConfidenceTwice()
    {
        string traceShader = ReadRepoText("Njulf.Shaders", "ssgi_trace.comp");
        string forwardShader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(traceShader, Does.Contain("radiance = FetchSceneColor(uv) * confidence;"));
            Assert.That(forwardShader, Does.Contain("vec3 nearField = ssgiSample.diffuse;"));
            Assert.That(forwardShader, Does.Contain("float fallbackWeight = clamp(1.0 - ddgiFieldCoverage - ssgiConfidence * 0.25, 0.0, 1.0);"));
            Assert.That(forwardShader, Does.Not.Contain("vec3 nearField = ssgiSample.diffuse * ssgiConfidence;"));
        });
    }

    [Test]
    public void SsgiTrace_UsesNonRecursiveForwardTraceSource()
    {
        string traceShader = ReadRepoText("Njulf.Shaders", "ssgi_trace.comp");
        string forwardShader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string forwardPass = ReadRepoText("Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");
        string tracePass = ReadRepoText("Njulf.Rendering", "Pipeline", "SsgiTracePass.cs");
        string meshPipeline = ReadRepoText("Njulf.Rendering", "Pipeline", "PipelineObjects", "MeshPipeline.cs");

        Assert.Multiple(() =>
        {
            Assert.That(traceShader, Does.Contain("SSGI_TRACE_SOURCE_TEXTURE_INDEX"));
            Assert.That(traceShader, Does.Not.Contain("HDR_SCENE_COLOR_TEXTURE_INDEX"));
            Assert.That(forwardShader, Does.Contain("FORWARD_SSGI_TRACE_SOURCE_OUTPUT"));
            Assert.That(forwardShader, Does.Contain("layout(location = 1) out vec4 outSsgiTraceSource;"));
            Assert.That(forwardShader, Does.Contain("WriteSsgiTraceSource(vec4(clamp(directLighting + emissive, vec3(0.0), vec3(64.0)), 1.0));"));
            Assert.That(forwardPass, Does.Contain("_renderTargets.SsgiTraceSource.TransitionToColorAttachment(cmd);"));
            Assert.That(forwardPass, Does.Contain("ColorAttachmentCount = 2"));
            Assert.That(tracePass, Does.Contain("_renderTargets.SsgiTraceSource.TransitionToShaderRead(cmd);"));
            Assert.That(meshPipeline, Does.Contain("\"forward_opaque.frag.spv\""));
            Assert.That(meshPipeline, Does.Contain("secondaryColorFormat: colorFormat"));
        });
    }

    [Test]
    public void SsgiTemporalShader_PreservesHistoryOnStochasticMiss()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ssgi_temporal.comp");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("bool surfaceValid = currentDepth > 0.000001"));
            Assert.That(shader, Does.Contain("bool currentSampleValid = current.a > 0.0001;"));
            Assert.That(shader, Does.Contain("else if (!currentSampleValid)"));
            Assert.That(shader, Does.Contain("resolved = history.rgb * SSGI_HISTORY_MISS_DECAY;"));
            Assert.That(shader, Does.Contain("resolvedConfidence = history.a * SSGI_HISTORY_MISS_DECAY;"));
            Assert.That(shader, Does.Not.Contain("bool currentValid = current.a > 0.0001"));
            Assert.That(shader, Does.Not.Contain("!currentValid"));
        });
    }

    [Test]
    public void SsgiTemporalShader_UsesPreviousSurfaceHistoryForDisocclusion()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ssgi_temporal.comp");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("layout(set = 2, binding = 1, r32f) uniform writeonly image2D SsgiDepthHistoryOutput;"));
            Assert.That(shader, Does.Contain("layout(set = 2, binding = 2, rgba16f) uniform writeonly image2D SsgiNormalHistoryOutput;"));
            Assert.That(shader, Does.Contain("SSGI_PREVIOUS_DEPTH_TEXTURE_INDEX"));
            Assert.That(shader, Does.Contain("SSGI_PREVIOUS_NORMAL_TEXTURE_INDEX"));
            Assert.That(shader, Does.Contain("float historyDepth = FetchPreviousDepth(historyUv);"));
            Assert.That(shader, Does.Contain("vec4 historyNormalSample = FetchPreviousNormal(historyUv);"));
            Assert.That(shader, Does.Contain("imageStore(SsgiDepthHistoryOutput, pixel, vec4(currentDepth, 0.0, 0.0, 0.0));"));
            Assert.That(shader, Does.Contain("imageStore(SsgiNormalHistoryOutput, pixel, currentNormalSample);"));
            Assert.That(shader, Does.Not.Contain("float historyDepth = FetchCurrentDepth(historyUv);"));
            Assert.That(shader, Does.Not.Contain("vec4 historyNormalSample = FetchCurrentNormal(historyUv);"));
        });
    }

    [Test]
    public void ForwardShader_SkipsExpensiveShadowPathsWhenRadiusIsZero()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("return SampleShadowCascade(textureIndex, uv, receiverDepth, 0.0005);"));
            Assert.That(shader, Does.Contain("float sampledDepth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], atlasUv).r;"));
            Assert.That(shader, Does.Contain("radius > 0 && PointShadowFaceEdgeDistance(faceUv) <= seamWidth"));
            Assert.That(shader, Does.Contain("shadow.BiasStrengthTexelSize.z <= 0.0"));
        });
    }

    [Test]
    public void LightCullShader_CullsLocalLightsPerTileAndSkipsDirectionals()
    {
        string lightCull = ReadRepoText("Njulf.Shaders", "lightcull.comp");
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(lightCull, Does.Contain("TryProjectLightScreenBounds"));
            Assert.That(lightCull, Does.Contain("return true;"));
            Assert.That(lightCull, Does.Not.Contain("SphereOverlapsTileDepthRange"));
            Assert.That(lightCull, Does.Contain("if (light.Type == 1)"));
            Assert.That(lightCull, Does.Contain("return false;"));
            Assert.That(forward, Does.Contain("if (light.Type != 1)"));
            Assert.That(forward, Does.Contain("Directional lights were handled above"));
        });
    }

    [Test]
    public void TiledLightIndexBuffer_IsNotClearedAndReadsAreBoundedByHeaderCount()
    {
        string sceneDataBuilder = ReadRepoText("Njulf.Rendering", "Data", "SceneDataBuilder.cs");
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(sceneDataBuilder, Does.Contain("_lastTiledLightHeaderBufferClearBytes = headerBytes;"));
            Assert.That(sceneDataBuilder, Does.Contain("_lastTiledLightIndexBufferClearBytes = 0;"));
            Assert.That(sceneDataBuilder, Does.Not.Contain("_bufferManager.GetBuffer(_tiledLightIndexBuffer.Handle), 0, indexBytes"));
            Assert.That(forward, Does.Contain("i < tileHeader.LightCount"));
            Assert.That(forward, Does.Contain("ReadTiledLightIndex(tileHeader.LightOffset + i)"));
        });
    }

    [Test]
    public void SceneDataBuilder_InvalidatesPerFrameUploadsWhenPayloadRebuilds()
    {
        string sceneDataBuilder = ReadRepoText("Njulf.Rendering", "Data", "SceneDataBuilder.cs");

        Assert.Multiple(() =>
        {
            Assert.That(sceneDataBuilder, Does.Contain("if (payloadRebuilt)"));
            Assert.That(sceneDataBuilder, Does.Contain("InvalidateDrawStreamUploadStates();"));
            Assert.That(sceneDataBuilder, Does.Contain("if (staticPayloadChanged)"));
            Assert.That(sceneDataBuilder, Does.Contain("MarkInstanceUploadFramesDirty();"));
            Assert.That(sceneDataBuilder, Does.Contain("public void InvalidateAllUploadStates()"));
            Assert.That(sceneDataBuilder, Does.Contain("Array.Clear(_uploadStates, 0, _uploadStates.Length);"));
        });
    }

    [Test]
    public void ForwardHiZOcclusion_UsesConservativeEdgeSampling()
    {
        string taskShader = ReadRepoText("Njulf.Shaders", "forward.task");
        string bindlessHeap = ReadRepoText("Njulf.Rendering", "Descriptors", "BindlessHeap.cs");

        Assert.Multiple(() =>
        {
            Assert.That(taskShader, Does.Contain("vec2 uvPadding = 4.0 / max(pc.Push.ScreenDimensions, vec2(1.0));"));
            Assert.That(taskShader, Does.Contain("float mipFloat = ceil(log2(max(extentPixels.x, extentPixels.y)));"));
            Assert.That(taskShader, Does.Not.Contain("vec2 uvPadding = 2.0 / max(pc.Push.ScreenDimensions, vec2(1.0));"));
            Assert.That(taskShader, Does.Not.Contain("float mipFloat = floor(log2(max(extentPixels.x, extentPixels.y)));"));
            Assert.That(bindlessHeap, Does.Contain("private void CreateHiZSampler()"));
            Assert.That(bindlessHeap, Does.Contain("MagFilter = Filter.Nearest"));
            Assert.That(bindlessHeap, Does.Contain("MinFilter = Filter.Nearest"));
        });
    }

    [Test]
    public void ForwardPass_SelectsNamedSimpleGlobalIblVariant()
    {
        string source = ReadRepoText("Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");
        string pipeline = ReadRepoText("Njulf.Rendering", "Pipeline", "PipelineObjects", "MeshPipeline.cs");
        string taskShader = ReadRepoText("Njulf.Shaders", "forward.task");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ForwardSimpleGlobalIblPipeline"));
            Assert.That(source, Does.Contain("ForwardSimpleFullInputGlobalIblPipeline"));
            Assert.That(source, Does.Contain("ResolveOpaqueVariantSelection"));
            Assert.That(pipeline, Does.Contain("ForwardFullMaterialPipeline"));
            Assert.That(pipeline, Does.Contain("ForwardSimpleGlobalIblPipeline"));
            Assert.That(pipeline, Does.Contain("forward_opaque.frag.spv"));
            Assert.That(pipeline, Does.Contain("forward_opaque_simple_full_input.frag.spv"));
            Assert.That(taskShader, Does.Contain("SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"));
            Assert.That(taskShader, Does.Contain("PACKED_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"));
        });
    }

    [Test]
    public void AnimationDebugView_SkipsBackgroundAndFogPasses()
    {
        string skybox = ReadRepoText("Njulf.Rendering", "Pipeline", "SkyboxPass.cs");
        string fog = ReadRepoText("Njulf.Rendering", "Pipeline", "FogPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(skybox, Does.Contain("sceneData.AnimationDebugView == AnimationDebugView.None"));
            Assert.That(fog, Does.Contain("sceneData.AnimationDebugView == AnimationDebugView.None"));
        });
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.Fail($"Could not find repo file '{Path.Combine(pathParts)}'.");
        return string.Empty;
    }
}
