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
            Assert.That(shader, Does.Contain("float supportedWeight = 0.0;"));
            Assert.That(shader, Does.Contain("float supportWeight = expectedContributionWeight * probeActive * irradianceConfidence;"));
            Assert.That(shader, Does.Contain("supportedWeight += supportWeight;"));
            Assert.That(shader, Does.Contain("float weight = supportWeight * visibility;"));
            Assert.That(shader, Does.Contain("float supportCoverage = clamp(supportedWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * clamp(volumeEdgeFade, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("result.coverage = supportCoverage;"));
            Assert.That(shader, Does.Not.Contain("result.coverage = clamp(totalWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * clamp(volumeEdgeFade, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float ddgiFieldCoverage = clamp(ddgiCoverage * (1.0 - nearContactSuppression), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float nearContactSuppression = clamp(contactOcclusion * 0.65, 0.0, 0.95);"));
            Assert.That(shader, Does.Contain("float fallbackWeight = clamp(1.0 - ddgiFieldCoverage, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 worldField = ddgiDiffuse * ddgiFieldCoverage;"));
            Assert.That(shader, Does.Not.Contain("ssgiConfidence * 0.25"));
            Assert.That(shader, Does.Not.Contain("ssgiConfidence * 0.75"));
            Assert.That(shader, Does.Not.Contain("vec3 worldField = ddgiDiffuse * (1.0 - nearContactSuppression);"));
        });
    }

    [Test]
    public void DdgiUpdateShader_JittersVisibilityTexelsAndSolidAngleWeightsIrradiance()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update.comp");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("vec2 Hash22(uvec3 value)"));
            Assert.That(shader, Does.Contain("vec3 JitteredAtlasTexelDirection("));
            Assert.That(shader, Does.Contain("vec2 jitter = Hash22(uvec3(probeIndex, pc.FrameIndex, texel)) - vec2(0.5);"));
            Assert.That(shader, Does.Contain("vec2 uv = (vec2(texelCoord) + vec2(0.5) + jitter * 0.85) / float(safeTexels);"));
            Assert.That(shader, Does.Contain("solidAngle = OctahedralTexelSolidAngle(uv, safeTexels);"));
            Assert.That(shader, Does.Contain("SharedRayDirection[rayIndex] = vec4(direction, raySolidAngle);"));
            Assert.That(shader, Does.Contain("WriteVisibilityAtlasSample("));
            Assert.That(shader, Does.Contain("directionalTexel,"));
            Assert.That(shader, Does.Contain("float raySolidAngle = max(SharedRayDirection[rayIndex].w, 0.0);"));
            Assert.That(shader, Does.Contain("float weight = max(dot(rayDirection, texelDirection), 0.0) * raySolidAngle * rayIrradiance.w;"));
            Assert.That(shader, Does.Contain("float expectedWeight = PI;"));
            Assert.That(shader, Does.Contain("? weightedRadiance"));
            Assert.That(shader, Does.Not.Contain("weightedRadiance * (4.0 * PI / float(sampleCount))"));
        });
    }

    [Test]
    public void ForwardShader_ReflectsDdgiOctahedralSeamTexelsOnSameEdge()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string normalizedShader = shader.Replace("\r\n", "\n", StringComparison.Ordinal);

        var rightEdge = RemapDdgiOctahedralTexelCoord(8, 3, 8);
        var leftEdge = RemapDdgiOctahedralTexelCoord(-1, 3, 8);
        var topEdge = RemapDdgiOctahedralTexelCoord(3, 8, 8);
        var bottomEdge = RemapDdgiOctahedralTexelCoord(3, -1, 8);
        var positiveX = DdgiBilinearOctahedralTexels(1.0f, 0.5f, 8);
        var positiveY = DdgiBilinearOctahedralTexels(0.5f, 1.0f, 8);

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("uvec2 RemapDdgiOctahedralTexelCoord"));
            Assert.That(normalizedShader, Does.Contain("if (remapped.x < 0)\n    {\n        remapped.x = 0;\n        remapped.y = maxCoord - remapped.y;\n    }\n    else if (remapped.x > maxCoord)\n    {\n        remapped.x = maxCoord;"));
            Assert.That(normalizedShader, Does.Contain("if (remapped.y < 0)\n    {\n        remapped.y = 0;\n        remapped.x = maxCoord - remapped.x;\n    }\n    else if (remapped.y > maxCoord)\n    {\n        remapped.y = maxCoord;"));
            Assert.That(normalizedShader, Does.Not.Contain("if (remapped.x < 0)\n    {\n        remapped.x = maxCoord;"));
            Assert.That(normalizedShader, Does.Not.Contain("else if (remapped.x > maxCoord)\n    {\n        remapped.x = 0;"));
            Assert.That(normalizedShader, Does.Not.Contain("if (remapped.y < 0)\n    {\n        remapped.y = maxCoord;"));
            Assert.That(normalizedShader, Does.Not.Contain("else if (remapped.y > maxCoord)\n    {\n        remapped.y = 0;"));
            Assert.That(rightEdge, Is.EqualTo((7, 4)));
            Assert.That(leftEdge, Is.EqualTo((0, 4)));
            Assert.That(topEdge, Is.EqualTo((4, 7)));
            Assert.That(bottomEdge, Is.EqualTo((4, 0)));
            Assert.That(positiveX.C10.X, Is.EqualTo(7));
            Assert.That(positiveX.C11.X, Is.EqualTo(7));
            Assert.That(positiveY.C01.Y, Is.EqualTo(7));
            Assert.That(positiveY.C11.Y, Is.EqualTo(7));
        });
    }

    [Test]
    public void SsgiComposite_AppliesCurrentFramePremultipliedEnergy()
    {
        string traceShader = ReadRepoText("Njulf.Shaders", "ssgi_trace.comp");
        string forwardShader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string compositeShader = ReadRepoText("Njulf.Shaders", "ssgi_composite.frag");
        string compositePass = ReadRepoText("Njulf.Rendering", "Pipeline", "SsgiCompositePass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(traceShader, Does.Contain("radiance = FetchSceneColor(uv);"));
            Assert.That(traceShader, Does.Contain("accumulatedRadiance += radiance * confidence;"));
            Assert.That(traceShader, Does.Contain("vec3 energy = accumulatedRadiance * invRayCount * intensity;"));
            Assert.That(traceShader, Does.Not.Contain("accumulatedRadiance / accumulatedConfidence"));
            Assert.That(forwardShader, Does.Contain("vec3 nearField = vec3(0.0);"));
            Assert.That(forwardShader, Does.Contain("float fallbackWeight = clamp(1.0 - ddgiFieldCoverage, 0.0, 1.0);"));
            Assert.That(compositeShader, Does.Contain("vec3 receiverAlbedo = clamp(material.rgb"));
            Assert.That(compositeShader, Does.Contain("float diffuseWeight = 1.0 - clamp(material.a, 0.0, 1.0);"));
            Assert.That(compositeShader, Does.Contain("vec3 indirect = clamp(gi.rgb"));
            Assert.That(compositePass, Does.Contain("_renderTargets.GiFinalDiffuse.TransitionToShaderRead(cmd);"));
            Assert.That(compositePass, Does.Contain("_renderTargets.SceneColor.TransitionToColorAttachment(cmd);"));
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
            Assert.That(shader, Does.Contain("float FetchPreviousDepthPixel(ivec2 pixel)"));
            Assert.That(shader, Does.Contain("vec4 FetchPreviousNormalPixel(ivec2 pixel)"));
            Assert.That(shader, Does.Contain("bool FindBestPreviousSurface("));
            Assert.That(shader, Does.Contain("bool previousSurfaceValid = FindBestPreviousSurface("));
            Assert.That(shader, Does.Contain("!previousSurfaceValid"));
            Assert.That(shader, Does.Contain("imageStore(SsgiDepthHistoryOutput, pixel, vec4(currentDepth, 0.0, 0.0, 0.0));"));
            Assert.That(shader, Does.Contain("imageStore(SsgiNormalHistoryOutput, pixel, currentNormalSample);"));
            Assert.That(shader, Does.Not.Contain("float historyDepth = FetchCurrentDepth(historyUv);"));
            Assert.That(shader, Does.Not.Contain("vec4 historyNormalSample = FetchCurrentNormal(historyUv);"));
            Assert.That(shader, Does.Not.Contain("float historyDepth = FetchPreviousDepth(historyUv);"));
            Assert.That(shader, Does.Not.Contain("vec4 historyNormalSample = FetchPreviousNormal(historyUv);"));
        });
    }

    [Test]
    public void SsgiTemporalShader_ConfidenceWeightsNeighborhoodAndCountsRejectedHistory()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ssgi_temporal.comp");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("float sampleConfidence = clamp(sampleValue.a, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("if (sampleConfidence <= 0.0001)"));
            Assert.That(shader, Does.Contain("sumColor += sampleColor * sampleConfidence;"));
            Assert.That(shader, Does.Contain("float response = mix(0.02, baseResponse, confidence);"));
            Assert.That(shader, Does.Contain("float motionResponse = max(response, baseResponse);"));
            Assert.That(shader, Does.Contain("response = mix(response, motionResponse, motionBlend);"));
            Assert.That(shader, Does.Not.Contain("localContrast"));
            Assert.That(shader, Does.Not.Contain("0.55"));
            Assert.That(shader, Does.Not.Contain("mix(response, 0.75"));
            Assert.That(shader, Does.Contain("shared uint SharedRejectedHistoryCount;"));
            Assert.That(shader, Does.Contain("atomicAdd(SharedRejectedHistoryCount, 1u);"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.FrameIndex, DIAGNOSTIC_SSGI_HISTORY_REJECTED, SharedRejectedHistoryCount);"));
            Assert.That(common, Does.Contain("const uint DIAGNOSTIC_SSGI_HISTORY_REJECTED = 8u;"));
        });
    }

    [Test]
    public void SsgiDenoiseShader_PointFetchesJointBilateralUpsampleInputs()
    {
        string denoiseShader = ReadRepoText("Njulf.Shaders", "ssgi_denoise.comp");
        string temporalShader = ReadRepoText("Njulf.Shaders", "ssgi_temporal.comp");
        string denoisePass = ReadRepoText("Njulf.Rendering", "Pipeline", "SsgiDenoisePass.cs");
        string temporalPass = ReadRepoText("Njulf.Rendering", "Pipeline", "SsgiTemporalPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(denoiseShader, Does.Contain("mat4 InverseProjectionMatrix;"));
            Assert.That(denoiseShader, Does.Contain("FetchSsgiPixel(sourcePixel);"));
            Assert.That(denoiseShader, Does.Contain("texelFetch(BindlessTextures[nonuniformEXT(SSGI_FILTERED_TEXTURE_INDEX)]"));
            Assert.That(denoiseShader, Does.Contain("texelFetch(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)]"));
            Assert.That(denoiseShader, Does.Contain("texelFetch(BindlessTextures[nonuniformEXT(SCENE_NORMAL_TEXTURE_INDEX)]"));
            Assert.That(denoiseShader, Does.Contain("float sampleViewDepth = ReconstructViewDepth(sampleUv, sampleDepth);"));
            Assert.That(denoiseShader, Does.Contain("float depthDifference = abs(sampleViewDepth - centerViewDepth);"));
            Assert.That(denoiseShader, Does.Contain("uint radius = pc.DenoiserEnabled == 0u ? 0u : clamp(pc.Radius, 1u, 4u);"));
            Assert.That(denoiseShader, Does.Contain("for (int y = -4; y <= 4; y++)"));
            Assert.That(denoiseShader, Does.Contain("float bilateralWeight = spatialWeight * depthWeight * normalWeight * lumaWeight;"));
            Assert.That(denoiseShader, Does.Contain("float supportWeight = mix(0.25, 1.0, sampleSupport);"));
            Assert.That(denoiseShader, Does.Contain("accumulated += max(ssgi.rgb, vec3(0.0)) * bilateralWeight * supportWeight;"));
            Assert.That(denoiseShader, Does.Contain("vec3 result = energyWeightSum > 0.00001"));
            Assert.That(denoiseShader, Does.Contain("? accumulated / energyWeightSum"));
            Assert.That(denoiseShader, Does.Contain("float confidence = supportWeightSum > 0.00001"));
            Assert.That(denoiseShader, Does.Contain("? supportSum / supportWeightSum"));
            Assert.That(denoiseShader, Does.Not.Contain("centerBlend"));
            Assert.That(denoiseShader, Does.Not.Contain("accumulatedConfidence += ssgi.a * weight;"));
            Assert.That(denoiseShader, Does.Not.Contain("texture(BindlessTextures[nonuniformEXT(SSGI_FILTERED_TEXTURE_INDEX)]"));
            Assert.That(denoiseShader, Does.Not.Contain("texture(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)]"));
            Assert.That(denoiseShader, Does.Not.Contain("texture(BindlessTextures[nonuniformEXT(SCENE_NORMAL_TEXTURE_INDEX)]"));
            Assert.That(temporalShader, Does.Contain("float currentViewDepth = ReconstructViewDepth(uv, currentDepth);"));
            Assert.That(temporalShader, Does.Contain("float candidateViewDepth = ReconstructViewDepth(sampleUv, candidateDepth);"));
            Assert.That(temporalShader, Does.Contain("float candidateDepthDelta = abs(currentViewDepth - candidateViewDepth);"));
            Assert.That(denoisePass, Does.Contain("InverseProjectionMatrix = sceneData.InverseProjectionMatrix"));
            Assert.That(temporalPass, Does.Contain("InverseProjectionMatrix = sceneData.InverseProjectionMatrix"));
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

    private static (
        (int X, int Y) C00,
        (int X, int Y) C10,
        (int X, int Y) C01,
        (int X, int Y) C11) DdgiBilinearOctahedralTexels(float u, float v, int texelsPerProbe)
    {
        int baseX = (int)MathF.Floor(u * texelsPerProbe - 0.5f);
        int baseY = (int)MathF.Floor(v * texelsPerProbe - 0.5f);
        return (
            RemapDdgiOctahedralTexelCoord(baseX, baseY, texelsPerProbe),
            RemapDdgiOctahedralTexelCoord(baseX + 1, baseY, texelsPerProbe),
            RemapDdgiOctahedralTexelCoord(baseX, baseY + 1, texelsPerProbe),
            RemapDdgiOctahedralTexelCoord(baseX + 1, baseY + 1, texelsPerProbe));
    }

    private static (int X, int Y) RemapDdgiOctahedralTexelCoord(int x, int y, int texelsPerProbe)
    {
        int maxCoord = Math.Max(texelsPerProbe, 1) - 1;
        int remappedX = x;
        int remappedY = y;

        if (remappedX < 0)
        {
            remappedX = 0;
            remappedY = maxCoord - remappedY;
        }
        else if (remappedX > maxCoord)
        {
            remappedX = maxCoord;
            remappedY = maxCoord - remappedY;
        }

        if (remappedY < 0)
        {
            remappedY = 0;
            remappedX = maxCoord - remappedX;
        }
        else if (remappedY > maxCoord)
        {
            remappedY = maxCoord;
            remappedX = maxCoord - remappedX;
        }

        return (Math.Clamp(remappedX, 0, maxCoord), Math.Clamp(remappedY, 0, maxCoord));
    }
}
