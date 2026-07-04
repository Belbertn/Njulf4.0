using System.Buffers.Binary;
using System.Text;
using Njulf.Rendering.Resources;
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
        "forward_visibility_compact.comp",
        "forward.mesh",
        "forward_simple.mesh",
        "forward.frag",
        "forward_opaque.frag",
        "forward_opaque_ddgi.frag",
        "forward_opaque_simple.frag",
        "forward_opaque_simple_ddgi.frag",
        "forward_opaque_simple_full_input.frag",
        "forward_opaque_simple_full_input_ddgi.frag",
        "particle.vert",
        "particle.frag",
        "skinning.comp",
        "lightcull.comp",
        "hiz_downsample.comp",
        "ambient_occlusion.comp",
        "ambient_occlusion_blur.comp",
        "ddgi_schedule_reset.comp",
        "ddgi_schedule_score.comp",
        "ddgi_schedule_prefix.comp",
        "ddgi_schedule_compact.comp",
        "ddgi_schedule_finalize.comp",
        "ddgi_trace.comp",
        "ddgi_blend.comp",
        "ddgi_relocate_classify.comp",
        "mesh_sdf_bake.comp",
        "global_sdf_update.comp",
        "surface_cache_update.comp",
        "bindless_3d_texture_smoke.comp",
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
        "foliage_forward_ssgi.frag",
        "foliage_forward_ddgi.frag",
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
    public void Bindless3DTextureSmokeShader_UsesSampledAndStorageVolumeBindings()
    {
        string shader = ReadRepoText("Njulf.Shaders", "bindless_3d_texture_smoke.comp");
        string runtime = ReadRepoText("Njulf.Rendering", "Diagnostics", "Bindless3DTextureRoundTripSmoke.cs");
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
        string sample = ReadRepoText("NjulfHelloGame", "SampleLifecycleSmokeRunner.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("BindlessVolumeTextures"));
            Assert.That(shader, Does.Contain("BindlessStorageImages"));
            Assert.That(shader, Does.Contain("textureLod(BindlessVolumeTextures"));
            Assert.That(shader, Does.Contain("imageStore(BindlessStorageImages"));
            Assert.That(runtime, Does.Contain("bindless_3d_texture_smoke.comp.spv"));
            Assert.That(runtime, Does.Contain("BeginSingleTimeCommands()"));
            Assert.That(runtime, Does.Contain("CmdCopyImageToBuffer"));
            Assert.That(runtime, Does.Contain("AllocateStorageImageIndex"));
            Assert.That(runtime, Does.Contain("new VolumeTextureDescriptor(sampled: true, transferDestination: true)"));
            Assert.That(renderer, Does.Contain("RunBindless3DTextureRoundTripSmoke"));
            Assert.That(sample, Does.Contain("bindless-3d-texture-roundtrip"));
        });
    }

    [Test]
    public void MeshSdfBakeAndGlobalSdfSampling_UseTexelCenterAddressing()
    {
        string bake = ReadRepoText("Njulf.Shaders", "mesh_sdf_bake.comp");
        string sample = ReadRepoText("Njulf.Shaders", "global_sdf_update.comp");

        Assert.Multiple(() =>
        {
            Assert.That(bake, Does.Contain("vec3 uv = (vec3(voxel) + vec3(0.5)) / max(vec3(imageSize), vec3(1.0));"));
            Assert.That(bake, Does.Not.Contain("imageSize - ivec3(1)"));
            Assert.That(bake, Does.Not.Contain("vec3 uv = vec3(voxel) / denom;"));
            Assert.That(sample, Does.Contain("vec3 uvw = (localPosition - localMin) / localExtent;"));
            Assert.That(sample, Does.Not.Contain("ResolutionX - 1u"));
            Assert.That(sample, Does.Contain("textureLod(BindlessVolumeTextures[nonuniformEXT(meshSdf.TextureIndex)], uvw, 0.0).r"));
        });
    }

    [Test]
    public void MeshSdfBake_UsesAngleWeightedPseudonormalAndUnsignedFallback()
    {
        string bake = ReadRepoText("Njulf.Shaders", "mesh_sdf_bake.comp");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string manager = ReadRepoText("Njulf.Rendering", "Resources", "MeshSdfManager.cs");

        Assert.Multiple(() =>
        {
            Assert.That(common, Does.Contain("MESH_SDF_FLAG_UNSIGNED_FALLBACK"));
            Assert.That(bake, Does.Contain("CornerAngle"));
            Assert.That(bake, Does.Contain("AccumulateVertexPseudonormal"));
            Assert.That(bake, Does.Contain("AccumulateEdgePseudonormal"));
            Assert.That(bake, Does.Contain("ResolveClosestFeaturePseudonormal"));
            Assert.That(bake, Does.Contain("bool unsignedFallback = (pc.Push.Flags & MESH_SDF_FLAG_UNSIGNED_FALLBACK) != 0u || !hasClosestTriangle;"));
            Assert.That(bake, Does.Not.Contain("signValue = dot(delta, normal) < 0.0 ? -1.0 : 1.0;"));
            Assert.That(manager, Does.Contain("LastFrameUnsignedFallbackMeshCount"));
            Assert.That(manager, Does.Contain("TotalUnsignedFallbackMeshCount"));
        });
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
    public void ForwardPass_SplitsDdgiAndSsgiGlobalIlluminationGate()
    {
        string source = ReadRepoText("Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("internal static bool ShouldApplyGlobalIllumination("));
            Assert.That(source, Does.Contain("return ShouldApplyDdgi(sceneData, gi) || ShouldApplySsgi(sceneData, gi);"));
            Assert.That(source, Does.Contain("return gi.EffectiveUseSsgi && sceneData.DepthPrePassEnabled;"));
            Assert.That(source, Does.Contain("(sceneData.DepthPrePassEnabled || gi.DdgiAllowForwardWithoutDepthPrePass)"));
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
            Assert.That(shader, Does.Contain("float ddgiIndirectAo = ambientOcclusion;"));
        });
    }

    [Test]
    public void ForwardShader_ScalesDdgiByCoverageAndComplementsFallback()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("float expectedWeight = 0.0;"));
            Assert.That(shader, Does.Contain("float spatialCoveredWeight = 0.0;"));
            Assert.That(shader, Does.Contain("float supportWeightSum = 0.0;"));
            Assert.That(shader, Does.Contain("float dataWeightSum = 0.0;"));
            Assert.That(shader, Does.Contain("bool confidenceBypass = DdgiDebugBypassConfidenceSuppression();"));
            Assert.That(shader, Does.Contain("float atlasDataTrust = confidenceBypass ? 1.0 : DdgiSparseDataTrust(irradianceConfidence);"));
            Assert.That(shader, Does.Contain("float radianceTransportTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(rayHitConfidence, 0.35);"));
            Assert.That(shader, Does.Contain("float stateIrradianceTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(max(stateIrradianceConfidence, irradianceConfidence), 0.45);"));
            Assert.That(shader, Does.Contain("float supportWeight = expectedContributionWeight * probeActive * atlasDataTrust;"));
            Assert.That(shader, Does.Contain("float radianceWeight = supportWeight * qualityConfidence;"));
            Assert.That(shader, Does.Contain("spatialCoveredWeight += expectedContributionWeight;"));
            Assert.That(shader, Does.Contain("if (probeActive <= 0.001)"));
            Assert.That(shader, Does.Not.Contain("supportWeightSum += supportWeight;"));
            Assert.That(shader, Does.Contain("visibilityTransport = EvaluateDdgiVisibility("));
            Assert.That(shader, Does.Contain("float visibilityTrust = DdgiVisibilityMomentTrust(visibilityConfidence);"));
            Assert.That(shader, Does.Contain("if (visibilityTrust > 0.000001 && useProbeVisibility)"));
            Assert.That(shader, Does.Contain("float visibilityAttenuation = mix("));
            Assert.That(shader, Does.Contain("float probeVisibilityConfidence = DdgiVisibilityConfidence(visibilityAttenuation);"));
            Assert.That(shader, Does.Contain("float cellWeight = clamp(trilinear.x * trilinear.y * trilinear.z * 2.0, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float normalWeight = max(DdgiSquare(clamp(alignment * 0.5 + 0.5, 0.0, 1.0)), 0.1);"));
            Assert.That(shader, Does.Contain("float visibilityLeakFloor = mix(0.005, 0.05, probeVisibilityConfidence);"));
            Assert.That(shader, Does.Contain("float visibilityWeight = max(visibilityAttenuation * visibilityAttenuation * visibilityAttenuation, visibilityLeakFloor);"));
            Assert.That(shader, Does.Contain("float visibleRadianceWeight = ShapeDdgiGatherWeight(radianceWeight * visibilityWeight);"));
            Assert.That(shader, Does.Contain("float visibleSupportWeight = supportWeight * mix(0.05, 1.0, probeVisibilityConfidence);"));
            Assert.That(shader, Does.Contain("supportWeightSum += visibleSupportWeight;"));
            Assert.That(shader, Does.Contain("accumulated += clamp(probeIrradiance, vec3(0.0), vec3(64.0)) * visibleRadianceWeight;"));
            Assert.That(shader, Does.Contain("totalWeight += visibleRadianceWeight;"));
            Assert.That(shader, Does.Contain("dataWeightSum += visibleSupportWeight * qualityConfidence;"));
            Assert.That(shader, Does.Contain("visibilityWeightedSupport += visibleSupportWeight * visibilityAttenuation;"));
            Assert.That(shader, Does.Not.Contain("float visibleRadianceWeight = radianceWeight * visibilityAttenuation;"));
            Assert.That(shader, Does.Not.Contain("float visibilityWeightedContribution = supportWeight * visibilityTransport * visibilityTrust;"));
            Assert.That(shader, Does.Contain("float minVariance = max(0.005, minProbeSpacing * minProbeSpacing * 0.0025);"));
            Assert.That(shader, Does.Contain("variance = max(mean2 - mean * mean, minVariance);"));
            Assert.That(shader, Does.Not.Contain("float grazingRejection = smoothstep(-0.15, 0.25, alignment);"));
            Assert.That(shader, Does.Not.Contain("float normalWeight = normalHemisphereWeight * normalHemisphereWeight * grazingRejection;"));
            Assert.That(shader, Does.Contain("float ResolveDdgiRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)"));
            Assert.That(shader, Does.Contain("vec3 axisFade = clamp(edgeDistance / safeBlendDistance, vec3(0.0), vec3(1.0));"));
            Assert.That(shader, Does.Contain("float perAxisFade = min(axisFade.x, min(axisFade.y, axisFade.z));"));
            Assert.That(shader, Does.Contain("float cornerPressure = clamp(length(vec3(1.0) - axisFade) * 0.70710678, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("volumeEdgeFade = ResolveDdgiRoundedBoxEdgeFade(logicalEdgeDistance, vec3(edgeBlendDistance));"));
            Assert.That(shader, Does.Contain("volumeEdgeFade = ResolveDdgiRoundedBoxEdgeFade(influenceEdgeDistance, info.spacing * 0.5);"));
            Assert.That(shader, Does.Not.Contain("vec3 edgeFade3 = smoothstep(vec3(0.0), vec3(edgeBlendDistance), logicalEdgeDistance);"));
            Assert.That(shader, Does.Not.Contain("volumeEdgeFade = min(edgeFade3.x, min(edgeFade3.y, edgeFade3.z));"));
            Assert.That(shader, Does.Contain("float normalizationWeight = mix(1.0, totalWeight, clamp(totalWeight * totalWeight + 0.9, 0.0, 1.0));"));
            Assert.That(shader, Does.Contain("result.irradiance = clamp((accumulated / max(normalizationWeight, 0.000001)) * finalIntensity, vec3(0.0), vec3(64.0));"));
            Assert.That(shader, Does.Contain("float spatialCoverage = clamp(spatialCoveredWeight / safeExpectedWeight, 0.0, 1.0) * edgeFade;"));
            Assert.That(shader, Does.Contain("float supportCoverage = clamp(supportWeightSum / safeExpectedWeight, 0.0, 1.0) * edgeFade;"));
            Assert.That(shader, Does.Contain("float dataConfidence = supportWeightSum > 0.000001"));
            Assert.That(shader, Does.Contain("? clamp(dataWeightSum / supportWeightSum, 0.0, 1.0) * edgeFade"));
            Assert.That(shader, Does.Contain("uint DdgiCacheGeneration()"));
            Assert.That(shader, Does.Contain("uint DdgiCacheLastUpdatedFrameSerial()"));
            Assert.That(shader, Does.Contain("uint DdgiCacheWarmupState()"));
            Assert.That(shader, Does.Contain("bool DdgiCacheValid()"));
            Assert.That(shader, Does.Contain("return cacheGeneration > 0u;"));
            Assert.That(shader, Does.Contain("float DdgiCacheReadiness()"));
            Assert.That(shader, Does.Contain("if (cacheWarmupState == DDGI_WARMUP_STATE_COLD_START)"));
            Assert.That(shader, Does.Contain("return 0.35;"));
            Assert.That(shader, Does.Contain("if (DdgiCacheGeneration() == 0u)"));
            Assert.That(shader, Does.Contain("supportCoverage = 0.0;"));
            Assert.That(shader, Does.Not.Contain("dataConfidence = clamp(dataConfidence * cacheReadiness, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("return cacheGeneration == 0u || cacheWarmupState == DDGI_WARMUP_STATE_COLD_START;"));
            Assert.That(shader, Does.Not.Contain("if (DdgiCacheCold())"));
            Assert.That(shader, Does.Contain("float blendedSupportCoverage = 0.0;"));
            Assert.That(shader, Does.Contain("float totalOwnership = 0.0;"));
            Assert.That(shader, Does.Contain("float DdgiSoftConfidenceTrust(float confidence, float trustedFloor)"));
            Assert.That(shader, Does.Contain("float DdgiSparseDataTrust(float dataConfidence)"));
            Assert.That(shader, Does.Contain("return DdgiSoftConfidenceTrust(confidence, 0.35);"));
            Assert.That(shader, Does.Not.Contain("return smoothstep(0.08, 0.55, confidence);"));
            Assert.That(shader, Does.Contain("float candidateBlendWeight,"));
            Assert.That(shader, Does.Contain("candidateBlendWeight = clamp(candidateBlendWeight, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float candidateVisibility = clamp(candidate.leakClamp, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float candidateOwnership = candidateSupport * DdgiSparseDataTrust(candidateData) * mix(0.10, 1.0, candidateVisibility) * candidateBlendWeight;"));
            Assert.That(shader, Does.Not.Contain("float candidateOwnership = candidateSupport * DdgiSparseDataTrust(candidateData) * candidateBlendWeight;"));
            Assert.That(shader, Does.Not.Contain("float candidateOwnership = candidateSupport * smoothstep(0.02, 0.25, candidateData);"));
            Assert.That(shader, Does.Contain("blendedDataConfidence += candidateData * blendWeight;"));
            Assert.That(shader, Does.Contain("result.supportCoverage = clamp(blendedSupportCoverage * invOwnership, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("result.ownershipConsumed = clamp(totalOwnership, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("result.coverage = spatialCoverage;"));
            Assert.That(shader, Does.Not.Contain("result.coverage = clamp(totalWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * clamp(volumeEdgeFade, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("HybridDiffuseGiResult ComposeHybridDiffuseGi("));
            Assert.That(shader, Does.Contain("float spatialCoverage = clamp(ddgi.coverage, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float supportCoverage = clamp(ddgi.supportCoverage, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float dataConfidence = clamp(ddgi.weight, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityConfidence = clamp(ddgi.visibility, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityTransport = clamp(ddgi.leakClamp, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float ddgiLowFrequencyCoverage = clamp(ddgi.coverage * ddgi.activeProbe, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float thinWallLeakClampStrength = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 14u), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float thinWallProxyThickness = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 15u), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float leakAttenuation = clamp(mix(1.0, visibilityTransport, leakStrength), 0.05, 1.0);"));
            Assert.That(shader, Does.Contain("float dataTrust = confidenceBypass && dataConfidence > 0.000001"));
            Assert.That(shader, Does.Contain("? 1.0"));
            Assert.That(shader, Does.Contain(": DdgiSparseDataTrust(dataConfidence);"));
            Assert.That(shader, Does.Contain("float supportTrust = supportCoverage * dataTrust;"));
            Assert.That(shader, Does.Not.Contain("float supportTrust = supportCoverage * smoothstep(0.02, 0.25, dataConfidence);"));
            Assert.That(shader, Does.Contain("float ddgiTrust = clamp(supportTrust * leakAttenuation, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float environmentTrust = clamp(1.0 - supportTrust, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float environmentTrust = clamp(1.0 - ddgiTrust, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 debugSuppression = vec3("));
            Assert.That(shader, Does.Contain("supportCoverage,"));
            Assert.That(shader, Does.Contain("leakAttenuation,"));
            Assert.That(shader, Does.Contain("dataConfidence);"));
            Assert.That(shader, Does.Contain("float cacheReadiness = DdgiCacheReadiness();"));
            Assert.That(shader, Does.Contain("float warmupFallbackFloor = DdgiCacheValid()"));
            Assert.That(shader, Does.Contain("? (1.0 - cacheReadiness) * (1.0 - supportTrust)"));
            Assert.That(shader, Does.Not.Contain("? (1.0 - cacheReadiness) * (1.0 - dataTrust)"));
            Assert.That(shader, Does.Contain("float effectiveEnvironmentFallbackIntensity = max(environmentFallbackIntensity, warmupFallbackFloor);"));
            Assert.That(shader, Does.Contain("float environmentFallbackWeight = clamp(environmentTrust * effectiveEnvironmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(shader, Does.Not.Contain("float environmentFallbackWeight = clamp(environmentTrust * environmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(shader, Does.Not.Contain("float environmentFallbackWeight = clamp((1.0 - ddgiLowFrequencyCoverage) * indirectAo * environmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(shader, Does.Not.Contain("float environmentFallbackWeight = clamp((1.0 - effectiveDdgiWeight) * indirectAo * environmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(shader, Does.Not.Contain("float effectiveDdgiWeight = clamp(ddgiLowFrequencyCoverage * ddgiVisibleSupport * (1.0 - ddgiContactSuppression), 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float ddgiUsableCoverage = clamp(ddgiLowFrequencyCoverage * (1.0 - ddgiContactSuppression), 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float ddgiFallbackCoverage = clamp(ddgiUsableCoverage * ddgiVisibleSupport, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 SafeRadiance(vec3 value)"));
            Assert.That(shader, Does.Contain("if (any(isnan(value)) || any(isinf(value)))"));
            Assert.That(shader, Does.Contain("vec3 ddgiLowFrequencyField = SafeRadiance(ddgiDiffuse * ddgiTrust);"));
            Assert.That(shader, Does.Contain("vec3 environmentFallbackField = SafeRadiance(diffuseIbl * environmentFallbackWeight);"));
            Assert.That(shader, Does.Contain("if (dataTrust <= 0.000001)"));
            Assert.That(shader, Does.Contain("result.diffuse = SafeRadiance(environmentFallbackField * indirectAoWeight);"));
            Assert.That(shader, Does.Contain("result.diffuse = SafeRadiance(ddgiLowFrequencyField + (environmentFallbackField + nearField) * indirectAoWeight);"));
            Assert.That(shader, Does.Not.Contain("result.diffuse = clamp((environmentFallbackField + ddgiLowFrequencyField + nearField) * indirectAoWeight, vec3(0.0), vec3(64.0));"));
            Assert.That(shader, Does.Contain("float ddgiEnvironmentFallbackIntensity = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 13u), 0.0, 4.0);"));
            Assert.That(shader, Does.Contain("ComposeHybridDiffuseGi(diffuseIbl, ddgiDiffuse, ddgiSample, indirectAo, ddgiEnvironmentFallbackIntensity, debugViewMode)"));
            Assert.That(shader, Does.Contain("bool DdgiDebugBypassFinalSuppression(uint debugViewMode)"));
            Assert.That(shader, Does.Contain("bool DdgiDebugBypassFinalSuppression()"));
            Assert.That(shader, Does.Contain("return debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE;"));
            Assert.That(shader, Does.Contain("bool DdgiDebugBypassConfidenceSuppression(uint debugViewMode)"));
            Assert.That(shader, Does.Contain("return debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS;"));
            Assert.That(shader, Does.Contain("result.diffuse = SafeRadiance(ddgiDiffuse);"));
            Assert.That(shader, Does.Contain("vec3 nearField = vec3(0.0);"));
            Assert.That(shader, Does.Not.Contain("ssgiConfidence * 0.25"));
            Assert.That(shader, Does.Not.Contain("ssgiConfidence * 0.75"));
            Assert.That(shader, Does.Not.Contain("vec3 worldField = ddgiDiffuse * (1.0 - nearContactSuppression);"));
        });
    }

    [Test]
    public void CornellValidationScene_UsesCameraRelativeClipmapAndSolidRoomGeometry()
    {
        string validation = ReadRepoText("NjulfHelloGame", "SampleGlobalIlluminationValidation.cs");
        string builder = ReadRepoText("NjulfHelloGame", "SampleStressSceneBuilder.cs");

        Assert.Multiple(() =>
        {
            Assert.That(validation, Does.Contain("gi.IndirectIntensity = 1.5f;"));
            Assert.That(validation, Does.Contain("gi.EnvironmentFallbackIntensity = 0.2f;"));
            Assert.That(validation, Does.Contain("if (scenario == SamplePerformanceScenario.GiCornellRoom)"));
            Assert.That(validation, Does.Contain("gi.EnvironmentFallbackIntensity = 0.0f;"));
            Assert.That(validation, Does.Contain("settings.Environment.Enabled = false;"));
            Assert.That(validation, Does.Contain("settings.Environment.SkyIntensity = 0.0f;"));
            Assert.That(validation, Does.Contain("settings.Environment.DiffuseIntensity = 0.0f;"));
            Assert.That(validation, Does.Contain("settings.Environment.SpecularIntensity = 0.0f;"));
            Assert.That(validation, Does.Contain("gi.DdgiClipmapBaseSpacing = 0.75f;"));
            Assert.That(builder, Does.Contain("private const float ValidationRoomWallThickness = 0.22f;"));
            Assert.That(builder, Does.Contain("AddValidationSolidBox("));
            Assert.That(builder, Does.Not.Contain("GI.Cornell.DDGI"));
            Assert.That(builder, Does.Not.Contain("AddValidationRoomProbeVolume("));
            Assert.That(builder, Does.Not.Contain("GlobalIlluminationProbeVolume.CreateThinWallRoomPreset("));
        });
    }

    [Test]
    public void SdfCascadeValidationScene_SpansMultipleSdfCascadesAndSurfaceCachePath()
    {
        string validation = ReadRepoText("NjulfHelloGame", "SampleGlobalIlluminationValidation.cs");
        string builder = ReadRepoText("NjulfHelloGame", "SampleStressSceneBuilder.cs");
        string program = ReadRepoText("NjulfHelloGame", "Program.cs");

        Assert.Multiple(() =>
        {
            Assert.That(builder, Does.Contain("BuildGiSdfCascadeField()"));
            Assert.That(builder, Does.Contain("GI.SdfCascadeField.Foundation"));
            Assert.That(builder, Does.Contain("new CoreVector3(34.0f, 0.16f, 92.0f)"));
            Assert.That(builder, Does.Contain("GI.SdfCascadeField.FarRoom"));
            Assert.That(builder, Does.Contain("centerZ: -72.0f"));
            Assert.That(builder, Does.Contain("includeFloor: false"));
            Assert.That(builder, Does.Contain("MaterialHandle redWall"));
            Assert.That(builder, Does.Contain("MaterialHandle greenWall"));
            Assert.That(builder, Does.Contain("MaterialHandle blueWall"));
            Assert.That(builder, Does.Contain("MaterialHandle[] occluderMaterials"));
            Assert.That(builder, Does.Contain("GI.SdfCascadeField.FarAmberPanel"));
            Assert.That(validation, Does.Contain("SamplePerformanceScenario.GiSdfCascadeField"));
            Assert.That(validation, Does.Contain("gi.SdfBackendFirstCascade = 1;"));
            Assert.That(validation, Does.Contain("gi.MeshSdfBakeBudget = 8;"));
            Assert.That(validation, Does.Contain("gi.SdfBrickUpdateBudget = 512;"));
            Assert.That(validation, Does.Contain("gi.SurfaceCacheTileUpdateBudget = 128;"));
            Assert.That(program, Does.Contain("SampleSceneKind.DdgiSdfCacheTest"));
            Assert.That(program, Does.Contain("builder.Apply(SamplePerformanceScenario.GiSdfCascadeField);"));
        });
    }

    [Test]
    public void ForwardShader_UsesGeometricNormalForDdgiQueryAndSurfaceBias()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("vec3 geometricNormal = normalize(fragNormal) * (gl_FrontFacing ? 1.0 : -1.0);"));
            Assert.That(shader, Does.Contain("vec3 ddgiNormal = geometricNormal;"));
            Assert.That(shader, Does.Contain("DdgiSampleResult ddgiSample = SampleDdgiIrradiance(fragWorldPosition, ddgiNormal, ddgiIndirectAo);"));
            Assert.That(shader, Does.Contain("vec3 ddgiDiffuse = SampleDdgiDiffuse(ddgiSample, albedo, metallic);"));
            Assert.That(shader, Does.Contain("ComposeHybridDiffuseGi(diffuseIbl, ddgiDiffuse, ddgiSample, indirectAo, ddgiEnvironmentFallbackIntensity, debugViewMode)"));
            Assert.That(shader, Does.Not.Contain("DdgiSampleResult ddgiSample = SampleDdgiIrradiance(fragWorldPosition, normal, indirectAo);"));
            Assert.That(shader, Does.Not.Contain("DdgiSampleResult ddgiSample = SampleDdgiIrradiance(fragWorldPosition, ddgiNormal, indirectAo);"));
        });
    }

    [Test]
    public void ForwardShader_SamplesDdgiCascadesWithToroidalCoverageBlending()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string ddgiUpdateShared = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string normalizedShader = shader.Replace("\r\n", "\n");
        int readGatherTileIndex = normalizedShader.IndexOf("if (ReadDdgiGatherTile(tile))", StringComparison.Ordinal);
        int clipmapCoverageDiagnosticIndex = normalizedShader.IndexOf("AddDdgiClipmapCoverageDiagnostics(tile, volumeCount, worldPosition);", StringComparison.Ordinal);
        int fallbackFlagIndex = normalizedShader.IndexOf("if ((tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)", StringComparison.Ordinal);
        int gatherCandidateIndex = normalizedShader.IndexOf("DdgiSampleResult gatherResult = SampleDdgiGatherCandidates(tile, volumeCount, worldPosition, normal, indirectAo, globalIntensity);", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("struct DdgiVolumeSampleInfo"));
            Assert.That(shader, Does.Contain("struct DdgiGatherTileInfo"));
            Assert.That(shader, Does.Contain("bool ReadDdgiGatherTile(out DdgiGatherTileInfo tile)"));
            Assert.That(shader, Does.Contain("bool DdgiExhaustiveGatherFallbackEnabled()"));
            Assert.That(shader, Does.Contain("DDGI_EXHAUSTIVE_GATHER_FALLBACK_ENABLED_FLAG"));
            Assert.That(shader, Does.Contain("bool DdgiRawAtlasRadianceConventionEnabled()"));
            Assert.That(shader, Does.Contain("bool DdgiDebugForceProbeActive()"));
            Assert.That(shader, Does.Contain("DDGI_DEBUG_FORCE_PROBE_ACTIVE_FLAG"));
            Assert.That(shader, Does.Contain("info.volumeIntensity = max(rayAndUpdateParams.z, 0.0);"));
            Assert.That(shader, Does.Contain("float finalIntensity = globalIntensity * info.volumeIntensity;"));
            Assert.That(shader, Does.Contain("bool ReadDdgiVolumeSampleInfo("));
            Assert.That(shader, Does.Contain("vec3 DdgiSurfaceProbeSamplePosition(DdgiVolumeSampleInfo info, vec3 worldPosition, vec3 normal)"));
            Assert.That(shader, Does.Contain("float surfaceBias = clamp(max(info.normalBias, minProbeSpacing * 0.16), 0.0, minProbeSpacing * 0.45);"));
            Assert.That(shader, Does.Contain("vec3 probeSamplePosition = DdgiSurfaceProbeSamplePosition(info, worldPosition, normal);"));
            Assert.That(shader, Does.Contain("DdgiVolumeSampleInfo biasedInfo;"));
            Assert.That(shader, Does.Contain("if (ReadDdgiVolumeSampleInfo(volumeIndex, probeSamplePosition, biasedInfo))"));
            Assert.That(shader, Does.Contain("info = biasedInfo;"));
            Assert.That(shader, Does.Contain("vec3 logicalPosition = worldPosition / info.spacing;"));
            Assert.That(shader, Does.Contain("vec3 minLogical = vec3(info.gridMinCell);"));
            Assert.That(shader, Does.Contain("vec3 maxLogical = minLogical + vec3(info.probeCounts - uvec3(1u));"));
            Assert.That(shader, Does.Contain("any(lessThan(logicalPosition, minLogical - vec3(0.5)))"));
            Assert.That(shader, Does.Contain("any(greaterThan(logicalPosition, maxLogical + vec3(0.5)))"));
            Assert.That(ddgiUpdateShared, Does.Contain("bool ReadStableDdgiVolumeSampleInfo("));
            Assert.That(ddgiUpdateShared, Does.Contain("vec3 StableDdgiSurfaceProbeSamplePosition(StableDdgiVolumeSampleInfo info, vec3 worldPosition, vec3 normal)"));
            Assert.That(ddgiUpdateShared, Does.Contain("vec3 probeSamplePosition = StableDdgiSurfaceProbeSamplePosition(info, worldPosition, normal);"));
            Assert.That(ddgiUpdateShared, Does.Contain("vec3 logicalPosition = worldPosition / info.spacing;"));
            Assert.That(ddgiUpdateShared, Does.Contain("any(lessThan(logicalPosition, minLogical - vec3(0.5)))"));
            Assert.That(ddgiUpdateShared, Does.Contain("any(greaterThan(logicalPosition, maxLogical + vec3(0.5)))"));
            Assert.That(shader, Does.Contain("float ResolveDdgiRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)"));
            Assert.That(shader, Does.Contain("float minEdgeBlendCells = min(2.0, max(shortestAxisCells * 0.125, 1.0));"));
            Assert.That(shader, Does.Contain("float edgeBlendCells = max(blendAndFlags.x * shortestAxisCells, minEdgeBlendCells);"));
            Assert.That(ddgiUpdateShared, Does.Contain("float ResolveStableDdgiRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)"));
            Assert.That(ddgiUpdateShared, Does.Contain("volumeEdgeFade = ResolveStableDdgiRoundedBoxEdgeFade(logicalEdgeDistance, vec3(edgeBlendDistance));"));
            Assert.That(common, Does.Contain("float ResolveDdgiAmbientRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)"));
            Assert.That(common, Does.Contain("info.edgeFade = ResolveDdgiAmbientRoundedBoxEdgeFade(logicalEdgeDistance, vec3(edgeBlendDistance));"));
            Assert.That(common, Does.Contain("const float DDGI_IRRADIANCE_ATLAS_MAX = 64.0;"));
            Assert.That(common, Does.Contain("const float DDGI_IRRADIANCE_ATLAS_GAMMA = 5.0;"));
            Assert.That(common, Does.Contain("vec4 DecodeDdgiIrradianceAtlasSqrtSample(vec4 encodedSample)"));
            Assert.That(common, Does.Contain("vec4 ResolveDdgiIrradianceAtlasSqrtBlend(vec4 sqrtSample)"));
            Assert.That(common, Does.Contain("irradiance = ResolveDdgiIrradianceAtlasSqrtBlend(DecodeDdgiIrradianceAtlasSqrtSample(irradiance));"));
            Assert.That(shader, Does.Contain("DecodeDdgiIrradianceAtlasSqrtSample(ReadPackedDdgiHalf4(uint(DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX)"));
            Assert.That(shader, Does.Contain("return ResolveDdgiIrradianceAtlasSqrtBlend(mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y));"));
            Assert.That(shader, Does.Contain("DdgiSampleResult SampleDdgiVolumeIrradiance("));
            Assert.That(shader, Does.Contain("DdgiSampleResult SampleDdgiGatherCandidates("));
            Assert.That(shader, Does.Contain("float primaryClipmapEdgeFade = -1.0;"));
            Assert.That(shader, Does.Contain("bool nearClipmapTransition = primaryClipmapEdgeFade < 0.0 || primaryClipmapEdgeFade < 0.985;"));
            Assert.That(shader, Does.Contain("tile.blendWeights.z > 0.0001"));
            Assert.That(shader, Does.Contain("if (ReadDdgiGatherTile(tile))"));
            Assert.That(shader, Does.Contain("(tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)"));
            Assert.That(shader, Does.Contain("bool DdgiSampleHasUsableGatherData(DdgiSampleResult ddgiSample)"));
            Assert.That(shader, Does.Contain("!any(isnan(ddgiSample.irradiance))"));
            Assert.That(shader, Does.Contain("!any(isinf(ddgiSample.irradiance))"));
            Assert.That(shader, Does.Contain("ddgiSample.spatialCoverage > 0.000001"));
            Assert.That(shader, Does.Contain("ddgiSample.supportCoverage > 0.000001"));
            Assert.That(shader, Does.Contain("ddgiSample.weight > 0.000001"));
            Assert.That(shader, Does.Contain("ddgiSample.ownershipConsumed > 0.000001"));
            Assert.That(shader, Does.Contain("DdgiSampleResult gatherResult = SampleDdgiGatherCandidates(tile, volumeCount, worldPosition, normal, indirectAo, globalIntensity);"));
            Assert.That(shader, Does.Contain("if (DdgiSampleHasUsableGatherData(gatherResult))"));
            Assert.That(shader, Does.Contain("return gatherResult;"));
            Assert.That(shader, Does.Contain("uint exhaustiveFallbackVolumeCount = min(volumeCount, 4u);"));
            Assert.That(shader, Does.Contain("bool DdgiShouldTryExhaustiveGatherFallback(DdgiSampleResult gatherResult)"));
            Assert.That(shader, Does.Contain("return gatherResult.spatialCoverage <= 0.000001 ||"));
            Assert.That(shader, Does.Contain("gatherResult.supportCoverage <= 0.000001 ||"));
            Assert.That(shader, Does.Contain("gatherResult.weight <= 0.000001 ||"));
            Assert.That(shader, Does.Contain("gatherResult.ownershipConsumed <= 0.000001;"));
            Assert.That(shader, Does.Contain("if (DdgiExhaustiveGatherFallbackEnabled() && DdgiShouldTryExhaustiveGatherFallback(gatherResult))"));
            Assert.That(shader, Does.Not.Contain("bool spatialNoSupport = gatherResult.spatialCoverage > 0.000001 && gatherResult.supportCoverage <= 0.000001;"));
            Assert.That(shader, Does.Not.Contain("if (DdgiExhaustiveGatherFallbackEnabled() && spatialNoSupport)"));
            Assert.That(shader, Does.Contain("AddDdgiShaderGatherFallbackAttemptDiagnostic();"));
            Assert.That(shader, Does.Contain("DdgiSampleResult fallbackResult = SampleDdgiIrradianceExhaustive(exhaustiveFallbackVolumeCount, worldPosition, normal, indirectAo, globalIntensity);"));
            Assert.That(shader, Does.Contain("AddDdgiShaderGatherFallbackResultDiagnostic(fallbackResult);"));
            Assert.That(shader, Does.Contain("return fallbackResult;"));
            Assert.That(shader, Does.Contain("DDGI_FAST_GATHER_ATTEMPT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 32u"));
            Assert.That(shader, Does.Contain("DDGI_SHADER_GATHER_FALLBACK_EMPTY_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 40u"));
            Assert.That(shader, Does.Contain("DDGI_SAMPLED_PROBE_CURRENT_FRUSTUM_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 43u"));
            Assert.That(shader, Does.Contain("DDGI_SAMPLED_PROBE_SIDE_REAR_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 44u"));
            Assert.That(shader, Does.Contain("DDGI_SAMPLED_PROBE_STALE_AGE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 45u"));
            Assert.That(shader, Does.Contain("AccumulateDdgiSampledProbeUseDiagnostics(sampleQualityAndReason, sampleProbePosition);"));
            Assert.That(shader, Does.Not.Contain("return SampleDdgiGatherCandidates(tile, volumeCount, worldPosition, normal, indirectAo, globalIntensity);"));
            Assert.That(shader, Does.Not.Contain("if (tiledResult.coverage > 0.000001 || tiledResult.weight > 0.000001)"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK"));
            Assert.That(shader, Does.Contain("bool DdgiClipmapCoverageCountersEnabled()"));
            Assert.That(shader, Does.Contain("return (pc.Push.DiagnosticFlags & 2u) != 0u;"));
            Assert.That(shader, Does.Contain("bool DdgiSparseDiagnosticPixel()"));
            Assert.That(shader, Does.Contain("bool DdgiForwardEstimateDiagnosticPixel()"));
            Assert.That(shader, Does.Contain("return DdgiForwardEstimateCountersEnabled() && DdgiSparseDiagnosticPixel();"));
            Assert.That(shader, Does.Contain("bool DdgiFastGatherDiagnosticPixel()"));
            Assert.That(shader, Does.Contain("return DdgiFastGatherCountersEnabled() && DdgiSparseDiagnosticPixel();"));
            Assert.That(shader, Does.Contain("bool DdgiClipmapCoverageDiagnosticPixel()"));
            Assert.That(shader, Does.Contain("return DdgiClipmapCoverageCountersEnabled() && DdgiSparseDiagnosticPixel();"));
            Assert.That(shader, Does.Contain("return (pixel.x & 15u) == 0u && (pixel.y & 15u) == 0u;"));
            Assert.That(shader, Does.Contain("const float DDGI_FORWARD_ESTIMATE_LUMINANCE_SCALE = 4096.0;"));
            Assert.That(shader, Does.Contain("return uint(round(clamp(value, 0.0, 16.0) * DDGI_FORWARD_ESTIMATE_LUMINANCE_SCALE));"));
            Assert.That(shader, Does.Contain("DDGI_CLIPMAP_INFO_PRIMARY_ATTEMPT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 27u"));
            Assert.That(shader, Does.Contain("DDGI_CLIPMAP_INFO_PRIMARY_OK_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 28u"));
            Assert.That(shader, Does.Contain("DDGI_CLIPMAP_INFO_PRIMARY_FAILED_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 29u"));
            Assert.That(shader, Does.Contain("DDGI_CLIPMAP_INFO_PRIMARY_EDGE_FADE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 30u"));
            Assert.That(shader, Does.Contain("DDGI_CLIPMAP_INFO_PRIMARY_BLEND_WEIGHT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 31u"));
            Assert.That(shader, Does.Contain("void AddDdgiClipmapCoverageAttempt(float primaryBlendWeight)"));
            Assert.That(shader, Does.Contain("void AddDdgiClipmapCoverageOk(float primaryEdgeFade, float primaryBlendWeight)"));
            Assert.That(shader, Does.Contain("void AddDdgiClipmapCoverageFail(float primaryBlendWeight)"));
            Assert.That(shader, Does.Contain("void AddDdgiClipmapCoverageDiagnostics(DdgiGatherTileInfo tile, uint volumeCount, vec3 worldPosition)"));
            Assert.That(shader, Does.Contain("if (!DdgiClipmapCoverageCountersEnabled())"));
            Assert.That(shader, Does.Contain("if (!DdgiClipmapCoverageDiagnosticPixel())"));
            Assert.That(shader, Does.Contain("if (!DdgiForwardEstimateDiagnosticPixel())"));
            Assert.That(shader, Does.Contain("if (!DdgiFastGatherDiagnosticPixel())"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_CLIPMAP_INFO_PRIMARY_ATTEMPT_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("bool primaryInfoOk ="));
            Assert.That(shader, Does.Contain("ReadDdgiVolumeSampleInfo(tile.primaryClipmapVolumeIndex, worldPosition, info);"));
            Assert.That(shader, Does.Contain("if (primaryInfoOk)"));
            Assert.That(shader, Does.Contain("AddDdgiClipmapCoverageOk(info.edgeFade, tile.blendWeights.y);"));
            Assert.That(readGatherTileIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(clipmapCoverageDiagnosticIndex, Is.GreaterThan(readGatherTileIndex));
            Assert.That(fallbackFlagIndex, Is.GreaterThan(clipmapCoverageDiagnosticIndex));
            Assert.That(gatherCandidateIndex, Is.GreaterThan(fallbackFlagIndex));
            Assert.That(shader, Does.Contain("uint DdgiProbeIndex(DdgiVolumeSampleInfo info, ivec3 probeCoord)"));
            Assert.That(shader, Does.Contain("return DdgiCalculatePhysicalProbeIndex("));
            Assert.That(shader, Does.Contain("vec3 logicalProbePosition = DdgiProbeWorldPosition(info, corner);"));
            Assert.That(shader, Does.Contain("vec3 probePosition = logicalProbePosition + relocationAndClassification.xyz;"));
            Assert.That(shader, Does.Contain("result.logicalProbePosition = candidate.logicalProbePosition;"));
            Assert.That(shader, Does.Contain("result.relocatedProbePosition = candidate.relocatedProbePosition;"));
            Assert.That(shader, Does.Contain("if (DdgiDebugForceProbeActive())"));
            Assert.That(shader, Does.Contain("probeActive = 1.0;"));
            Assert.That(shader, Does.Contain("tile.blendWeights.x,"));
            Assert.That(shader, Does.Contain("tile.blendWeights.y,"));
            Assert.That(shader, Does.Contain("tile.blendWeights.z,"));
            Assert.That(shader, Does.Contain("float blendWeight = clamp(candidateOwnership * remainingOwnership"));
            Assert.That(shader, Does.Contain("remainingOwnership = clamp(remainingOwnership - blendWeight, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("bool sampleAuthored = pass == 0u;"));
            Assert.That(shader, Does.Contain("bool isAuthored = info.kind == DDGI_VOLUME_KIND_AUTHORED;"));
            Assert.That(shader, Does.Contain("if (isAuthored != sampleAuthored)"));
            Assert.That(shader, Does.Contain("result.irradiance = clamp(blendedIrradiance * invOwnership, vec3(0.0), vec3(64.0));"));
            Assert.That(common, Does.Contain("DDGI_GATHER_TILE_BUFFER_INDEX"));
            Assert.That(shader, Does.Not.Contain("ReadDdgiContainingVolume("));
            Assert.That(shader, Does.Not.Contain("return firstProbe + probeCoord.x + probeCoord.y * probeCounts.x + probeCoord.z * probeCounts.x * probeCounts.y;"));
        });
    }

    [Test]
    public void VulkanRenderer_GatesClipmapGatherTileReadinessDuringWarmup()
    {
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
        int start = renderer.IndexOf("private DdgiGatherTileManager.DdgiGatherSupportReadiness ResolveDdgiGatherSupportReadiness()", StringComparison.Ordinal);
        int end = renderer.IndexOf("private static float ResolveDdgiGatherReadinessHint", StringComparison.Ordinal);

        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));

        string method = renderer.Substring(start, end - start).Replace("\r\n", "\n");

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("const float readyFraction = 0.80f;"));
            Assert.That(method, Does.Contain("float visibleReadiness = ResolveDdgiGatherReadinessHint(_ddgiProbeVolumeManager.LastWarmedVisibleProbeFraction);"));
            Assert.That(method, Does.Contain("localReadiness >= readyFraction"));
            Assert.That(method, Does.Contain("cascade0Readiness >= readyFraction"));
            Assert.That(method, Does.Contain("visibleReadiness >= readyFraction"));
            Assert.That(method, Does.Contain("float localReadiness = ResolveDdgiGatherReadinessHint(_ddgiProbeVolumeManager.LastWarmedLocalProbeFraction);"));
            Assert.That(method, Does.Contain("float publishedCacheReadiness = _ddgiProbeVolumeManager.PublishedCacheGeneration > 0u ? 0.05f : 0.0f;"));
            Assert.That(method, Does.Contain("float publishedProbeConfidence = _ddgiProbeVolumeManager.PublishedCacheGeneration > 0u"));
            Assert.That(method, Does.Contain("? ResolveDdgiGatherReadinessHint(_ddgiProbeVolumeManager.LastAverageProbeConfidence)"));
            Assert.That(method, Does.Contain("Math.Min(cascade0Readiness, visibleReadiness),"));
            Assert.That(method, Does.Contain("Math.Max(publishedProbeConfidence, publishedCacheReadiness));"));
            Assert.That(method, Does.Contain("localReadiness,"));
            Assert.That(method, Does.Contain("clipmapReadiness,\n                clipmapReadiness);"));
            Assert.That(method, Does.Not.Contain("1.0f,\n                1.0f);"));
        });
    }

    [Test]
    public void DdgiUpdateShader_UsesFullSphereFibonacciSamplingAndGatheredVisibility()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("vec2 Hash22(uvec3 value)"));
            Assert.That(shader, Does.Contain("vec3 DdgiSphericalFibonacci(uint index, uint count)"));
            Assert.That(shader, Does.Contain("mat3 DdgiProbeRayRotation(uint probeIndex, uint frameSerial)"));
            Assert.That(shader, Does.Contain("mat3 rayRotation = DdgiProbeRayRotation(probeIndex, pc.FrameSerial);"));
            Assert.That(shader, Does.Contain("float raySolidAngle = (4.0 * PI) / max(float(raysPerProbe), 1.0);"));
            Assert.That(shader, Does.Contain("vec3 direction = rayRotation * DdgiSphericalFibonacci(rayIndex, raysPerProbe);"));
            Assert.That(shader, Does.Not.Contain("Hash22(uvec3(probeIndex, pc.FrameIndex, texel))"));
            Assert.That(shader, Does.Not.Contain("uint frameOffset = pc.FrameIndex * max(raysPerProbe, 1u);"));
            Assert.That(shader, Does.Contain("SharedRayDirection[rayIndex] = vec4(result.direction, result.solidAngle);"));
            Assert.That(shader, Does.Contain("shared vec2 SharedRayVisibility[256];"));
            Assert.That(shader, Does.Contain("SharedRayVisibility[rayIndex] = visibilityMoment;"));
            Assert.That(shader, Does.Contain("float DdgiVisibilityGatherWeight(float cosTheta)"));
            Assert.That(shader, Does.Contain("return x32 * x16 * x2;"));
            Assert.That(shader, Does.Contain("float weight = DdgiVisibilityGatherWeight(dot(rayDirectionAndSolidAngle.xyz, texelDirection)) * rayValid;"));
            Assert.That(shader, Does.Not.Contain("pow(max(dot(rayDirectionAndSolidAngle.xyz, texelDirection), 0.0), 50.0)"));
            Assert.That(shader, Does.Contain("WriteVisibilityAtlasSample(visibilityTexel, weightedVisibility / weightSum, visibilityBlendAlpha, probeIndex);"));
            Assert.That(shader, Does.Contain("WriteVisibilityAtlasSample("));
            Assert.That(shader, Does.Not.Contain("directionalTexel,"));
            Assert.That(shader, Does.Contain("float raySolidAngle = max(SharedRayDirection[rayIndex].w, 0.0);"));
            Assert.That(shader, Does.Contain("float weight = max(dot(rayDirection, texelDirection), 0.0) * raySolidAngle * rayIrradiance.w;"));
            Assert.That(shader, Does.Contain("float expectedWeight = PI;"));
            Assert.That(shader, Does.Not.Contain("sampleCoverageScale"));
            Assert.That(shader, Does.Not.Contain("directionalTexelCount"));
            Assert.That(shader, Does.Contain("float confidence = clamp(weightSum / expectedWeight, 0.0, 1.0) * activeProbe;"));
            Assert.That(shader, Does.Contain("return vec4(irradiance, confidence);"));
            Assert.That(shader, Does.Contain("DDGI_UPDATE_FLAG_TRACE_ENERGY_DIAGNOSTICS"));
            Assert.That(shader, Does.Contain($"DDGI_TRACE_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiTraceEnergyCounterBase}u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_ENERGY_DIRECT_NO_SHADOW_LUMINANCE_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 10u"));
            Assert.That(shader, Does.Contain($"DDGI_TRACE_EARLY_OUT_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiTraceEarlyOutCounterBase}u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_EARLY_OUT_DISABLED_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 0u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_EARLY_OUT_BEYOND_REQUEST_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 1u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_EARLY_OUT_RESOLVE_BOUNDS_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 2u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_EARLY_OUT_RESOLVE_PROBE_RANGE_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 3u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_EARLY_OUT_RESOLVE_CLIPMAP_CELL_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 4u"));
            Assert.That(shader, Does.Contain($"DDGI_BLEND_ENERGY_COUNTER_BASE = {RendererDiagnosticsBuffer.DdgiBlendEnergyCounterBase}u"));
            Assert.That(shader, Does.Contain("DDGI_BLEND_ENERGY_NONFINITE_IRRADIANCE_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 5u"));
            Assert.That(shader, Does.Contain("DDGI_BLEND_ENERGY_FIREFLY_SUPPRESSED_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 6u"));
            Assert.That(shader, Does.Contain($"DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE = {RendererDiagnosticsBuffer.DdgiTraceRingMismatchSampleBase}u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_RING_MISMATCH_SAMPLE_VALID_COUNTER = DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 0u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_RING_MISMATCH_SAMPLE_REQUEST_AGE_COUNTER = DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 18u"));
            Assert.That(shader, Does.Contain("DDGI_TRACE_RING_MISMATCH_CORRECTED_COUNTER = DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 19u"));
            Assert.That(shader, Does.Contain("bool DdgiTraceEnergyDiagnosticRay(uint probeIndex, uint rayIndex)"));
            Assert.That(shader, Does.Contain("return DdgiTraceEnergyDiagnosticsEnabled() && ((probeIndex + rayIndex + pc.FrameIndex) & 3u) == 0u;"));
            Assert.That(shader, Does.Contain("RecordDdgiTraceEnergyDiagnostics("));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_DIRECT_LUMINANCE_COUNTER"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_DIRECT_NO_SHADOW_LUMINANCE_COUNTER"));
            Assert.That(shader, Does.Contain("directNoShadowDiffuse"));
            Assert.That(shader, Does.Contain("vec3 EvaluateDdgiRayQuerySurfaceRadianceAtHit("));
            Assert.That(shader, Does.Contain("const uint DDGI_SURFACE_CACHE_ANALYTIC_FALLBACK_FLAG = 1u << 0;"));
            Assert.That(shader, Does.Contain("bool DdgiSurfaceCacheAnalyticFallbackForced()"));
            Assert.That(shader, Does.Contain("return DDGI_SURFACE_CACHE_FALLBACK != 0 || (pc.SurfaceCacheFlags & DDGI_SURFACE_CACHE_ANALYTIC_FALLBACK_FLAG) != 0u;"));
            Assert.That(shader, Does.Contain("bool forceAnalyticFallback = DdgiSurfaceCacheAnalyticFallbackForced();"));
            Assert.That(shader, Does.Contain("if (!forceAnalyticFallback && TrySampleDdgiSurfaceCacheRadiance(hitPosition, surfaceNormal, surfaceAlbedo, cacheRadiance))"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_SURFACE_CACHE_FALLBACK_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("radiance = EvaluateDdgiRayQuerySurfaceRadianceAtHit("));
            Assert.That(shader, Does.Contain("gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT"));
            Assert.That(shader, Does.Not.Contain("gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT"));
            Assert.That(shader, Does.Contain("float normalOffset = DDGI_PROBE_TRACE_EPSILON * 4.0;"));
            Assert.That(shader, Does.Contain("float rayDistance = max(maxDistance - normalOffset, rayTMin);"));
            Assert.That(shader, Does.Contain("vec3 origin = worldPosition + normal * normalOffset;"));
            Assert.That(shader, Does.Not.Contain("directionOffset"));
            Assert.That(shader, Does.Not.Contain("worldPosition + normal * normalOffset + lightDirection"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_DISABLED_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_BEYOND_REQUEST_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_RESOLVE_BOUNDS_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_RESOLVE_PROBE_RANGE_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_RESOLVE_CLIPMAP_CELL_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("RecordDdgiTraceRingMismatchSample("));
            Assert.That(shader, Does.Contain("WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 8u, computedProbeIndex);"));
            Assert.That(shader, Does.Contain("request.LogicalCell = DdgiDecodeLogicalCellFromPhysicalProbeIndex("));
            Assert.That(shader, Does.Contain("uint requestAge = pc.FrameSerial - request.RequestFrameSerial;"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_RING_MISMATCH_CORRECTED_COUNTER, 1u);"));
            Assert.That(shader, Does.Not.Contain("request.ProbeIndex = computedProbeIndex;"));
            Assert.That(shader, Does.Contain("RecordDdgiBlendEnergyDiagnostics(probeIndex, localIndex, directionalIrradiance);"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_IRRADIANCE_LUMINANCE_COUNTER"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_NONFINITE_IRRADIANCE_COUNTER, 1u);"));
            Assert.That(shader, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_FIREFLY_SUPPRESSED_COUNTER, 1u);"));
            Assert.That(common, Does.Contain("const float DDGI_IRRADIANCE_ATLAS_MAX = 64.0;"));
            Assert.That(common, Does.Contain("const float DDGI_IRRADIANCE_ATLAS_GAMMA = 5.0;"));
            Assert.That(shader, Does.Contain("const float DDGI_HALF_FLOAT_MAX = 65504.0;"));
            Assert.That(common, Does.Contain("vec3 EncodeDdgiIrradianceAtlasRgb(vec3 irradiance)"));
            Assert.That(common, Does.Contain("vec4 DecodeDdgiIrradianceAtlasSqrtSample(vec4 encodedSample)"));
            Assert.That(common, Does.Contain("vec4 ResolveDdgiIrradianceAtlasSqrtBlend(vec4 sqrtSample)"));
            Assert.That(shader, Does.Contain("float ResolveDdgiIrradianceReasonBlendFloor(uint flags)"));
            Assert.That(shader, Does.Contain("response = max(response, ResolveDdgiIrradianceReasonBlendFloor(flags));"));
            Assert.That(shader, Does.Contain("float ResolveDdgiAsymmetricIrradianceBlendAlpha("));
            Assert.That(shader, Does.Contain("float changeAttention = smoothstep(0.02, 0.35, relativeDelta);"));
            Assert.That(shader, Does.Contain("float brighteningDamping = mix(1.0, 0.5, changeAttention);"));
            Assert.That(shader, Does.Contain("SanitizeDdgiIrradianceAtlasSample("));
            Assert.That(shader, Does.Contain("SanitizeDdgiEncodedIrradianceAtlasSample("));
            Assert.That(shader, Does.Contain("ApplyDdgiIrradianceFireflySuppression("));
            Assert.That(shader, Does.Contain("float visibilityTrust = smoothstep(0.05, 0.20, visibilityConfidence);"));
            Assert.That(shader, Does.Contain("float visibility = 1.0;"));
            Assert.That(shader, Does.Contain("if (visibilityTrust > 0.000001)"));
            Assert.That(shader, Does.Contain("visibility = EvaluateStableDdgiVisibility("));
            Assert.That(shader, Does.Contain("float visibilityAttenuation = mix("));
            Assert.That(shader, Does.Contain("float radianceWeight = cellWeight * normalWeight * distanceWeight * probeActive * irradianceConfidence * qualityConfidence;"));
            Assert.That(shader, Does.Contain("accumulated += clamp(irradianceSample.rgb, vec3(0.0), vec3(64.0)) * radianceWeight * visibilityAttenuation;"));
            Assert.That(shader, Does.Contain("totalWeight += radianceWeight;"));
            Assert.That(shader, Does.Not.Contain("float confidence = clamp(dot(irradiance"));
            Assert.That(shader, Does.Not.Contain("float confidence = clamp(currentLuminance"));
            Assert.That(shader, Does.Not.Contain("float weight = cellWeight * normalWeight * distanceWeight * probeActive * irradianceConfidence * qualityConfidence * visibility;"));
            Assert.That(shader, Does.Contain("? weightedRadiance"));
            Assert.That(shader, Does.Not.Contain("weightedRadiance * (4.0 * PI / float(sampleCount))"));
        });
    }

    [Test]
    public void DdgiUpdateShader_TracksLuminanceChangeForProbeConfidence()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("vec3 previousIrradianceHistory = ReadDdgiIrradianceHistoryMetrics(stateBase, resetHistory);"));
            Assert.That(shader, Does.Contain("vec4 irradianceHistory = ResolveDdgiIrradianceHistory("));
            Assert.That(shader, Does.Contain("float luminanceInconsistency = irradianceHistory.z;"));
            Assert.That(shader, Does.Contain("float irradianceBlendAlpha = ResolveDdgiIrradianceBlendAlpha(baseBlendAlpha, request.Flags, luminanceInconsistency);"));
            Assert.That(shader, Does.Contain("float visibilityBlendAlpha = ResolveDdgiVisibilityBlendAlpha(baseBlendAlpha, request.Flags);"));
            Assert.That(shader, Does.Contain("WriteProbeIrradianceAtlasTexel("));
            Assert.That(shader, Does.Contain("request.Flags,"));
            Assert.That(shader, Does.Contain("bool DdgiProbeL1MetadataEnabled()"));
            Assert.That(shader, Does.Contain("vec4 ResolveDdgiProbeL1Metadata(uint rayCount, float historyValid, float blendAlpha, vec4 previousMetadata)"));
            Assert.That(shader, Does.Contain("vec4 previousRepresentationMetadata = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 20u);"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(visibility, clamp(luminanceInconsistency, 0.0, 1.0), 1.0));"));
            Assert.That(shader, Does.Contain("WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 17u, irradianceHistory.x);"));
            Assert.That(shader, Does.Contain("WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 18u, irradianceHistory.y);"));
            Assert.That(shader, Does.Contain("WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 19u, luminanceInconsistency);"));
            Assert.That(shader, Does.Contain("ResolveDdgiProbeL1Metadata(raysPerProbe, historyValid, irradianceBlendAlpha, previousRepresentationMetadata)"));
            Assert.That(shader, Does.Contain("float luminanceConfidence = 1.0 - luminanceChange * 0.45;"));
            Assert.That(shader, Does.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * luminanceConfidence, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * (1.0 - missRatio * 0.5) * luminanceConfidence, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(visibility, float(pc.FrameIndex), 1.0));"));
        });
    }

    [Test]
    public void SurfaceCacheUpdateShader_UsesRayQueryMaterialLightingPath()
    {
        string shader = ReadRepoText("Njulf.Shaders", "surface_cache_update.comp");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string pass = ReadRepoText("Njulf.Rendering", "Pipeline", "SurfaceCachePasses.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("#extension GL_EXT_ray_query : require"));
            Assert.That(shader, Does.Contain("layout(set = 2, binding = 0) uniform accelerationStructureEXT SceneTlas;"));
            Assert.That(shader, Does.Contain("rayQueryInitializeEXT("));
            Assert.That(shader, Does.Contain("ReadSurfaceCacheRayQueryInstance("));
            Assert.That(shader, Does.Contain("ResolveSurfaceCacheHit("));
            Assert.That(shader, Does.Contain("ReadMaterial(instance.MaterialIndex)"));
            Assert.That(shader, Does.Contain("EvaluateSurfaceCacheDirectRadiance("));
            Assert.That(shader, Does.Contain("SurfaceCacheTraceVisibility("));
            Assert.That(shader, Does.Contain("uint SelectStochasticSurfaceCacheLocalLightOrdinal(uint cardIndex, uint tileTexel)"));
            Assert.That(shader, Does.Contain("bool TryBuildStochasticSurfaceCacheLocalLightContribution("));
            Assert.That(shader, Does.Contain("energyScale = float(pc.Push.LocalLightCount);"));
            Assert.That(shader, Does.Contain("EvaluateSurfaceCacheDirectLight("));
            Assert.That(shader, Does.Contain("localLightAttenuation) * localLightEnergyScale;"));
            Assert.That(shader, Does.Contain("for (uint lightIndex = 0u; lightIndex < pc.Push.LightCount; lightIndex++)"));
            Assert.That(shader, Does.Contain("vec3 SampleStableDdgiIrradiance(vec3 worldPosition, vec3 normal)"));
            Assert.That(shader, Does.Contain("vec3 stableIrradiance = SampleStableDdgiIrradiance(worldPosition + normal * SURFACE_CACHE_DDGI_PROBE_TRACE_EPSILON, normal);"));
            Assert.That(shader, Does.Contain("vec3 stableDiffuse = stableIrradiance * (albedo / SURFACE_CACHE_PI);"));
            Assert.That(shader, Does.Contain("return max(direct + stableDiffuse + emissive + emissiveProxy, vec3(0.0));"));
            Assert.That(shader, Does.Not.Contain("vec3 environmentDiffuse ="));
            Assert.That(shader, Does.Contain("vec3 captureDirection = -cardNormal;"));
            Assert.That(shader, Does.Contain("CardTexelRayOrigin(card, tileTexel) + cardNormal * (depthRange + SURFACE_CACHE_RAY_EPSILON)"));
            Assert.That(shader, Does.Contain("ResolveSurfaceCacheHit(instanceIndex, primitiveIndex, barycentrics, captureDirection"));
            Assert.That(common, Does.Contain("uint WorkMode;"));
            Assert.That(shader, Does.Contain("const uint SURFACE_CACHE_WORK_MODE_CAPTURE = 0u;"));
            Assert.That(shader, Does.Contain("const uint SURFACE_CACHE_WORK_MODE_LIGHT = 1u;"));
            Assert.That(shader, Does.Contain("if (pc.Push.WorkMode == SURFACE_CACHE_WORK_MODE_CAPTURE)"));
            Assert.That(shader, Does.Contain("if (pc.Push.WorkMode != SURFACE_CACHE_WORK_MODE_LIGHT || lightTexelOffset >= pc.Push.TexelsLit)"));
            Assert.That(shader, Does.Not.Contain("pc.Push.TilesCaptured != 0u"));
            Assert.That(pass, Does.Contain("DispatchSurfaceCacheWork(cmd, pushConstants, WorkModeCapture, captureGroups);"));
            Assert.That(pass, Does.Contain("InsertSurfaceCacheWorkBarrier(cmd);"));
            Assert.That(pass, Does.Contain("DispatchSurfaceCacheWork(cmd, pushConstants, WorkModeLight, lightGroups);"));
            Assert.That(pass, Does.Not.Contain("Math.Max(captureGroups, lightGroups)"));
            Assert.That(pass, Does.Contain("if (work.AtlasesRequireClear)"));
            Assert.That(pass, Does.Contain("ClearSurfaceCacheAtlas(cmd, captureAtlas);"));
            Assert.That(pass, Does.Contain("ClearSurfaceCacheAtlas(cmd, radianceAtlas);"));
            Assert.That(pass, Does.Contain("_surfaceCacheManager.MarkAtlasesCleared();"));
            Assert.That(pass, Does.Contain("CmdClearColorImage(cmd, atlas.Image, ImageLayout.TransferDstOptimal"));
            Assert.That(shader, Does.Contain("imageStore(BindlessStorageImages2D[pc.Push.RadianceAtlasTextureIndex]"));
            Assert.That(shader, Does.Not.Contain("vec3 CardAlbedo("));
            Assert.That(shader, Does.Not.Contain("card.ObjectIndex * 1664525u"));
            Assert.That(shader, Does.Not.Contain("vec3 sunDir = normalize(vec3(0.45, 0.75, 0.35));"));
            Assert.That(shader, Does.Not.Contain("float sky = 0.22 + 0.28"));
        });
    }

    [Test]
    public void DdgiUpdateShader_ConsumesCpuProbeUpdateRequests()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(common, Does.Contain("uint DdgiCalculateLocalPhysicalProbeIndex("));
            Assert.That(common, Does.Contain("uint DdgiCalculatePhysicalProbeIndex("));
            Assert.That(common, Does.Contain("ivec3 DdgiDecodeLogicalCellFromPhysicalProbeIndex("));
            Assert.That(common, Does.Contain("ivec3 relative = logicalCell - gridMinCell;"));
            Assert.That(shader, Does.Contain("DdgiProbeUpdateRequest ReadProbeUpdateRequest(uint updateIndex)"));
            Assert.That(shader, Does.Contain("request = ReadProbeUpdateRequest(updateIndex);"));
            Assert.That(shader, Does.Contain("bool resolved = enabled && ResolveProbeUpdateRequest("));
            Assert.That(shader, Does.Contain("request.LogicalCell - gridMin"));
            Assert.That(shader, Does.Contain("localProbeIndex = request.ProbeIndex - firstProbe;"));
            Assert.That(shader, Does.Contain("request.LogicalCell = DdgiDecodeLogicalCellFromPhysicalProbeIndex("));
            Assert.That(shader, Does.Contain("firstProbe + localProbeIndex != request.ProbeIndex"));
            Assert.That(shader, Does.Contain("probePosition = vec3(request.LogicalCell) * probeSpacing;"));
            Assert.That(shader, Does.Contain("bool ShouldResetDdgiProbeHistory(uint flags)"));
            Assert.That(shader, Does.Contain("float ResolveDdgiDirtyReasonHysteresis(float baseHysteresis, uint flags)"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED"));
            Assert.That(shader, Does.Contain("bool resetHistory = ShouldResetDdgiProbeHistory(request.Flags);"));
            Assert.That(shader, Does.Contain("float hysteresis = ResolveDdgiDirtyReasonHysteresis(clamp(updateParams.w, 0.0, 0.999), request.Flags);"));
            Assert.That(shader, Does.Contain("vec3 traceProbePosition = probePosition + (resetHistory ? vec3(0.0) : previousRelocationAndClassification.xyz);"));
            Assert.That(shader, Does.Contain("TraceProbeRay("));
            Assert.That(shader, Does.Contain("                traceProbePosition,"));
            Assert.That(shader, Does.Not.Contain("uint probeIndex = (pc.StartProbeIndex + updateIndex)"));
            Assert.That(shader, Does.Not.Contain("WriteStorageWord(pc.ProbeUpdateQueueBufferIndex, requestBase + 0u, probeIndex);"));
        });
    }

    [Test]
    public void DdgiUpdateShader_UsesVolumeRayCountsAndStochasticHitLightCap()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string pass = ReadRepoText("Njulf.Rendering", "Pipeline", "DdgiPipelinePasses.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("uint MaxShadedLights;"));
            Assert.That(shader, Does.Contain("uint DirectionalLightCount;"));
            Assert.That(shader, Does.Contain("uint LocalLightCount;"));
            Assert.That(shader, Does.Contain("uint LightSelectionMode;"));
            Assert.That(shader, Does.Contain("uint PrimaryDirectionalLightIndex;"));
            Assert.That(shader, Does.Contain("uint SelectedLocalLightIndex;"));
            Assert.That(shader, Does.Contain("float SelectedLocalLightEnergyScale;"));
            Assert.That(shader, Does.Contain("uint EmissiveSourceCount;"));
            Assert.That(shader, Does.Contain("uint EmissiveSourceRevision;"));
            Assert.That(shader, Does.Contain("uint MaterialTextureMaxCascade;"));
            Assert.That(shader, Does.Contain("uint CurrentFrameIndex;"));
            Assert.That(shader, Does.Contain("const uint DDGI_MAX_SELECTED_HIT_LIGHTS = 2u;"));
            Assert.That(shader, Does.Contain("const uint DDGI_LIGHT_SELECTION_MODE_STOCHASTIC_DIRECTIONAL_LOCAL = 1u;"));
            Assert.That(shader, Does.Contain("const uint DDGI_INVALID_LIGHT_INDEX = 0xffffffffu;"));
            Assert.That(shader, Does.Contain("bool TryReadSelectedDdgiDirectionalLight(out GPULight selectedLight)"));
            Assert.That(shader, Does.Contain("uint SelectStochasticDdgiLocalLightOrdinal(uint probeIndex, uint rayIndex)"));
            Assert.That(shader, Does.Contain("bool TryBuildStochasticDdgiLocalLightContribution("));
            Assert.That(shader, Does.Contain("for (uint lightIndex = 0u; lightIndex < pc.LightCount; lightIndex++)"));
            Assert.That(shader, Does.Contain("vec3 EvaluateSelectedDdgiDirectDiffuseRadianceAtHit("));
            Assert.That(shader, Does.Contain("uint selectedLightCapacity = min(pc.MaxShadedLights, DDGI_MAX_SELECTED_HIT_LIGHTS);"));
            Assert.That(shader, Does.Contain("energyScale = float(pc.LocalLightCount);"));
            Assert.That(shader, Does.Contain("EvaluateSelectedDdgiDirectDiffuseRadianceAtHit("));
            Assert.That(shader, Does.Contain("lightNoShadowDiffuse) * localLightEnergyScale;"));
            Assert.That(shader, Does.Contain("contributionScore = DdgiTraceEnergyLuminance(incomingRadiance) * attenuation * nDotL;"));
            Assert.That(shader, Does.Contain("bool ShouldUseCompactDdgiMaterial(uint volumeCascadeIndex)"));
            Assert.That(shader, Does.Contain("vec3 ResolveCompactDdgiAlbedo(GPUMaterialData material)"));
            Assert.That(shader, Does.Contain("vec3 ResolveCompactDdgiEmissive(GPUMaterialData material)"));
            Assert.That(shader, Does.Contain("float ResolveDdgiMaterialTextureLod(GPUMaterialData material, uint volumeCascadeIndex)"));
            Assert.That(shader, Does.Contain("vec3 EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit("));
            Assert.That(shader, Does.Contain("GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(0u);"));
            Assert.That(shader, Does.Contain("uint ResolveDdgiRequestRayCount(DdgiProbeUpdateRequest request, vec4 updateParams)"));
            Assert.That(shader, Does.Contain("uint requestRaysPerProbe = request.RayCount > 0u"));
            Assert.That(shader, Does.Contain("uint raysPerProbe = ResolveDdgiRequestRayCount(request, updateParams);"));
            Assert.That(shader, Does.Contain("bool ShouldSampleDdgiMaterialTextures(uint volumeCascadeIndex)"));
            Assert.That(shader, Does.Contain("volumeCascadeIndex <= pc.MaterialTextureMaxCascade"));
            Assert.That(shader, Does.Contain("float materialTextureLod = DdgiMaterialTextureLod(volumeCascadeIndex);"));
            Assert.That(shader, Does.Not.Contain("DDGI_HARD_MAX_SHADED_LIGHTS"));
            Assert.That(shader, Does.Not.Contain("uint lightCount = min(pc.LightCount, min(pc.MaxShadedLights"));
            Assert.That(shader, Does.Not.Contain("uint raysPerProbe = clamp(pc.RaysPerProbe"));
            Assert.That(pass, Does.Contain("MaxShadedLights = checked((uint)Math.Clamp(effectiveMaxShadedLights, 0, 64))"));
            Assert.That(pass, Does.Contain("DirectionalLightCount = checked((uint)Math.Max(0, sceneData.DirectionalLightCount))"));
            Assert.That(pass, Does.Contain("LocalLightCount = checked((uint)Math.Max(0, sceneData.LocalLightCount))"));
            Assert.That(pass, Does.Contain("LightSelectionMode = 1"));
            Assert.That(pass, Does.Contain("PrimaryDirectionalLightIndex = EncodeLightIndex(sceneData.DdgiPrimaryDirectionalLightIndex)"));
            Assert.That(pass, Does.Contain("SelectedLocalLightIndex = EncodeLightIndex(sceneData.DdgiSelectedLocalLightIndex)"));
            Assert.That(pass, Does.Contain("SelectedLocalLightEnergyScale = Math.Clamp(sceneData.DdgiSelectedLocalLightEnergyScale, 0.0f, 64.0f)"));
            Assert.That(pass, Does.Contain("EmissiveSourceCount = checked((uint)Math.Max(0, sceneData.DdgiEmissiveSourceCount))"));
            Assert.That(pass, Does.Contain("EmissiveSourceRevision = sceneData.DdgiEmissiveSourceRevision"));
            Assert.That(pass, Does.Contain("MaterialTextureMaxCascade = EncodeMaterialTextureMaxCascade(gi.DdgiMaterialTextureMaxCascade)"));
            Assert.That(pass, Does.Contain("RelocationParams = new Vector4("));
            Assert.That(pass, Does.Contain("gi.DdgiRelocationTargetSurfaceDistanceFraction"));
            Assert.That(pass, Does.Contain("gi.DdgiRelocationMinSurfaceDistance"));
            Assert.That(pass, Does.Contain("gi.DdgiRelocationMaxDistanceFraction"));
            Assert.That(pass, Does.Contain("gi.DdgiRelocationBlendAlpha"));
            Assert.That(pass, Does.Contain("CurrentFrameIndex = sceneData.CurrentFrameIndex"));
            Assert.That(pass, Does.Contain("FrameSerial = sceneData.DdgiFrameSerialLow32"));
        });
    }

    [Test]
    public void DdgiUpdateShader_WritesProbeQualityDiagnostics()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string forwardShader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(common, Does.Contain("const int SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION = 48;"));
            Assert.That(shader, Does.Contain("uint ResolvePrimaryProbeUpdateReason(uint flags)"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_DIRTY_BOUNDS"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_VISIBLE_FRUSTUM"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_AGE_REFRESH"));
            Assert.That(shader, Does.Contain("DDGI_PROBE_UPDATE_REASON_OUTSIDE_FRUSTUM_SAFETY"));
            Assert.That(shader, Does.Contain("Triangle winding is not reliable probe-validity evidence for production scenes"));
            Assert.That(shader, Does.Contain("float softInvalidProbeScore = smoothstep(0.25, 0.45, closeRatio);"));
            Assert.That(shader, Does.Contain("smoothstep(0.70, 0.90, closeRatio)"));
            Assert.That(shader, Does.Not.Contain("smoothstep(0.55, 0.75, backfaceRatio)"));
            Assert.That(shader, Does.Contain("float invalidProbeScore = softInvalidProbeScore;"));
            Assert.That(shader, Does.Contain("float hardInvalid = smoothstep(0.75, 0.95, hardInvalidProbeScore);"));
            Assert.That(shader, Does.Contain("float softInvalid = smoothstep(0.35, 0.75, softInvalidProbeScore);"));
            Assert.That(shader, Does.Contain("float clipmapActiveFloor = volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE ? 0.0 : 0.35;"));
            Assert.That(shader, Does.Contain("float targetActiveProbe = classificationEnabled ? max(1.0 - hardInvalid, clipmapActiveFloor) : 1.0;"));
            Assert.That(shader, Does.Contain("float activeBlendAlpha = targetActiveProbe > previousActiveProbe"));
            Assert.That(shader, Does.Contain("? max(stateBlendAlpha, 0.35)"));
            Assert.That(shader, Does.Contain("float activeProbe = mix(previousActiveProbe, targetActiveProbe, activeBlendAlpha);"));
            Assert.That(shader, Does.Contain("float confidencePenalty = classificationEnabled ? 1.0 - softInvalid * 0.75 : 1.0;"));
            Assert.That(shader, Does.Contain("localNearestHitDistance = min(localNearestHitDistance, max(result.hitDistance, 0.0));"));
            Assert.That(shader, Does.Contain("SharedBackfaceAndMissCount[localIndex] = vec4(localBackfaceCount, localMissCount, localNearestHitDistance, 0.0);"));
            Assert.That(shader, Does.Contain("nearestHitDistance = min(nearestHitDistance, SharedBackfaceAndMissCount[i].z);"));
            Assert.That(shader, Does.Contain("float targetSurfaceDistance = max(minProbeSpacing * pc.RelocationParams.x, pc.RelocationParams.y);"));
            Assert.That(shader, Does.Contain("float maxRelocationDistance = pc.RelocationParams.z * minProbeSpacing;"));
            Assert.That(shader, Does.Contain("float relocationBlendAlpha = pc.RelocationParams.w;"));
            Assert.That(shader, Does.Contain("float relocationEvidence = smoothstep(0.10, 0.35, closeRatio) * (1.0 - missRatio);"));
            Assert.That(shader, Does.Contain("float neededPush = max(targetSurfaceDistance - nearestHitDistance, 0.0);"));
            Assert.That(shader, Does.Contain("float closePush = closeRatio * max(normalBias + viewBias, 0.01) * 4.0;"));
            Assert.That(shader, Does.Contain("float unclampedRelocationDistance = max(neededPush, closePush) * relocationEvidence;"));
            Assert.That(shader, Does.Contain("vec3 ClampDdgiRelocationVector(vec3 relocation, float maxRelocationDistance)"));
            Assert.That(shader, Does.Contain("vec3 blendedRelocationUnclamped = historyValid > 0.5"));
            Assert.That(shader, Does.Contain("? mix(previousRelocationAndClassification.xyz, relocation, relocationBlendAlpha)"));
            Assert.That(shader, Does.Contain("vec3 blendedRelocation = ClampDdgiRelocationVector(blendedRelocationUnclamped, maxRelocationDistance);"));
            Assert.That(shader, Does.Contain("float blendedRelocationDistance = length(blendedRelocation);"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase, vec4(blendedRelocation, blendedRelocationDistance));"));
            Assert.That(shader, Does.Contain("uint fallbackProbeIndex = ResolveDdgiInactiveProbeFallback(volumeIndex, request.LogicalCell, probeIndex, activeProbe);"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 8u, vec4(nearestHitDistance, missRatio, PackDdgiFallbackProbeIndex(fallbackProbeIndex), hitRatio));"));
            Assert.That(shader, Does.Not.Contain("float maxRelocationDistance = 0.4 * minProbeSpacing;"));
            Assert.That(shader, Does.Contain("float traceSampleConfidence = clamp(hitRatio + missRatio * 0.35, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float rayHitConfidence = clamp(mix(0.35, 1.0, traceSampleConfidence) * confidencePenalty, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float rayHitConfidence = clamp(mix(0.35, 1.0, traceSampleConfidence) * (1.0 - backfaceRatio) * confidencePenalty, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float rayHitConfidence = clamp(hitRatio * (1.0 - backfaceRatio) * confidencePenalty, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float luminanceConfidence = 1.0 - luminanceChange * 0.45;"));
            Assert.That(shader, Does.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * luminanceConfidence, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * (1.0 - missRatio * 0.5) * luminanceConfidence, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityConfidence = clamp((hitRatio + missRatio * 0.35) * (1.0 - closeRatio * 0.5) * confidencePenalty, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 qualityConfidence = vec3(rayHitConfidence, irradianceConfidence, visibilityConfidence);"));
            Assert.That(shader, Does.Not.Contain("classifiedActiveProbe"));
            Assert.That(shader, Does.Not.Contain("activeProbe * (1.0 - invalidProbeScore)"));
            Assert.That(shader, Does.Contain("vec3 blendedQualityConfidence = historyValid > 0.5"));
            Assert.That(shader, Does.Contain("float lastUpdateReason = float(ResolvePrimaryProbeUpdateReason(request.Flags));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(0.0));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 16u, vec4(0.0));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 20u, vec4(0.0));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 8u, vec4(0.0));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(blendedQualityConfidence, lastUpdateReason));"));
            Assert.That(shader, Does.Contain("WriteStorageWord(pc.ProbeStateBufferIndex, stateBase + 16u, pc.FrameSerial);"));
            Assert.That(forwardShader, Does.Contain("spatialCoveredWeight += expectedContributionWeight;"));
            Assert.That(forwardShader, Does.Contain("bool TryResolveDdgiInactiveProbeFallback("));
            Assert.That(forwardShader, Does.Contain("if (!DdgiDebugForceProbeActive() && sourceProbeActive <= 0.36)"));
            Assert.That(forwardShader, Does.Not.Contain("sourceProbeActive <= 0.36 && sourceClassification.y > 0.50"));
            Assert.That(forwardShader, Does.Contain("vec4 probeStatistics = ReadStorageVec4(uint(DDGI_PROBE_RELOCATION_CLASSIFICATION_BUFFER_INDEX), relocationBase + 8u);"));
            Assert.That(forwardShader, Does.Contain("useFallbackVisibility = dot(vec3(cellDelta), vec3(cellDelta)) <= 1.0;"));
            Assert.That(forwardShader, Does.Contain("vec4 probeIrradianceSample = ReadDdgiProbeIrradiance(sampleProbeIndex, normal);"));
            Assert.That(forwardShader, Does.Contain("if (visibilityTrust > 0.000001 && useProbeVisibility)"));
            Assert.That(forwardShader, Does.Contain("vec2 visibilityMoments = ReadDdgiProbeVisibility(sampleProbeIndex, probeToPointDirection);"));
            Assert.That(forwardShader, Does.Contain("if (probeActive <= 0.001)"));
            Assert.That(forwardShader, Does.Contain("result.diffuse = SafeRadiance(environmentFallbackField * indirectAoWeight);"));
        });
    }

    [Test]
    public void DdgiUpdateShader_DirectBounceIsIndependentFromEnvironmentFallback()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string pass = ReadRepoText("Njulf.Rendering", "Pipeline", "DdgiPipelinePasses.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("float intensity = max(updateParams.z, 0.0);"));
            Assert.That(shader, Does.Contain("bool DdgiRawAtlasRadianceConventionEnabled()"));
            Assert.That(shader, Does.Contain("return true;"));
            Assert.That(shader, Does.Contain("bool DdgiDebugForceProbeActive()"));
            Assert.That(shader, Does.Contain("DDGI_DEBUG_FORCE_PROBE_ACTIVE_FLAG"));
            Assert.That(shader, Does.Contain("DDGI_UPDATE_FLAG_PROBE_L1_METADATA"));
            Assert.That(pass, Does.Contain("ProbeL1MetadataFlag = 1u << 7"));
            Assert.That(pass, Does.Contain("settings.DdgiProbeL1MetadataEnabled"));
            Assert.That(shader, Does.Contain("if (DdgiDebugForceProbeActive())"));
            Assert.That(shader, Does.Contain("vec3 probeRayRadiance = radiance;"));
            Assert.That(shader, Does.Not.Contain("vec3 sampleIrradiance = DdgiRawAtlasRadianceConventionEnabled()"));
            Assert.That(shader, Does.Not.Contain(": radiance * intensity;"));
            Assert.That(shader, Does.Not.Contain("float intensity = max(updateParams.z, 0.0) * max(pc.EnvironmentRadianceAndIntensity.w, 0.0);"));
            Assert.That(shader, Does.Not.Contain("radiance = pc.EnvironmentRadianceAndIntensity.rgb * max(pc.EnvironmentRadianceAndIntensity.w, 0.0) * skyWeight;"));
            Assert.That(shader, Does.Contain("radiance = SampleDdgiEnvironmentMissRadiance(direction);"));
            Assert.That(pass, Does.Contain("flags |= RawAtlasRadianceConventionFlag;"));
            Assert.That(pass, Does.Not.Contain("if (settings.DdgiRawAtlasRadianceConventionEnabled)\r\n                flags |= RawAtlasRadianceConventionFlag;"));
            Assert.That(pass, Does.Not.Contain("if (settings.DdgiRawAtlasRadianceConventionEnabled)\n                flags |= RawAtlasRadianceConventionFlag;"));
            Assert.That(shader, Does.Contain("textureLod("));
            Assert.That(shader, Does.Contain("BindlessCubeTextures[nonuniformEXT(environment.EnvironmentTextureIndex)]"));
            Assert.That(shader, Does.Contain("* max(environment.DiffuseIntensity, 0.0);"));
            Assert.That(shader, Does.Not.Contain("* max(environment.SkyIntensity, 0.0);"));
            Assert.That(pass, Does.Contain("float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f;"));
            Assert.That(pass, Does.Not.Contain("float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.SkyIntensity : 0.0f;"));
            Assert.That(shader, Does.Contain("float variance = max(mean2 - mean * mean, 0.005);"));
        });
    }

    [Test]
    public void DdgiUpdateShader_UsesSplitScratchAndStablePublishedCache()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string trace = ReadRepoText("Njulf.Shaders", "ddgi_trace.comp");
        string blend = ReadRepoText("Njulf.Shaders", "ddgi_blend.comp");
        string relocateClassify = ReadRepoText("Njulf.Shaders", "ddgi_relocate_classify.comp");
        string scheduleShared = ReadRepoText("Njulf.Shaders", "ddgi_schedule_shared.glsl");
        string scheduleReset = ReadRepoText("Njulf.Shaders", "ddgi_schedule_reset.comp");
        string scheduleScore = ReadRepoText("Njulf.Shaders", "ddgi_schedule_score.comp");
        string schedulePrefix = ReadRepoText("Njulf.Shaders", "ddgi_schedule_prefix.comp");
        string scheduleCompact = ReadRepoText("Njulf.Shaders", "ddgi_schedule_compact.comp");
        string scheduleFinalize = ReadRepoText("Njulf.Shaders", "ddgi_schedule_finalize.comp");
        string schedulePass = ReadRepoText("Njulf.Rendering", "Pipeline", "DdgiSchedulePass.cs");
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
        string pass = ReadRepoText("Njulf.Rendering", "Pipeline", "DdgiPipelinePasses.cs");
        string manager = ReadRepoText("Njulf.Rendering", "Resources", "DdgiProbeVolumeManager.cs");
        string scheduler = ReadRepoText("Njulf.Rendering", "Resources", "DdgiProbeUpdateScheduler.cs");
        string pipelineDeclaration = ReadRepoText("Njulf.Rendering", "Pipeline", "ProductionRenderPipelineDeclaration.cs");

        Assert.Multiple(() =>
        {
            Assert.That(pipelineDeclaration, Does.Contain("// DDGI update runs after ForwardPlusPass and publishes cache data for subsequent frames."));
            Assert.That(scheduleReset, Does.Contain("if (gl_GlobalInvocationID.x != 0u)"));
            Assert.That(scheduleReset, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_SCAN_PROBE_COUNT"));
            Assert.That(scheduleReset, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_CANDIDATE_OUTPUT_CAPACITY"));
            Assert.That(scheduleScore, Does.Contain("TryResolveDdgiScheduleVolume"));
            Assert.That(scheduleShared, Does.Contain("DDGI_PROBE_CANDIDATE_BUFFER_INDEX"));
            Assert.That(scheduleShared, Does.Contain("MinimumProbeRefreshFrames"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_PROBE_STATE_UPDATE_METADATA"));
            Assert.That(scheduleShared, Does.Contain("uint FrameSerial;"));
            Assert.That(scheduleShared, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_FRAME_SERIAL"));
            Assert.That(scheduleShared, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_WARMUP_STATE"));
            Assert.That(scheduleShared, Does.Contain("uint WarmupCascade0Budget;"));
            Assert.That(scheduleScore, Does.Contain("bool ageDue = !newProbe && constants.FrameSerial - lastUpdateFrame >= constants.MinimumProbeRefreshFrames;"));
            Assert.That(scheduleScore, Does.Contain("uint inputReasonFlags = ReadDdgiProbeCandidateWord(scanIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_REASON_FLAGS);"));
            Assert.That(scheduleScore, Does.Contain("bool hintedDirtyProbe = (inputReasonFlags & DDGI_SCHEDULE_REASON_DIRTY_BOUNDS) != 0u;"));
            Assert.That(scheduleScore, Does.Contain("bool hintedVisibleProbe = (inputReasonFlags & DDGI_SCHEDULE_REASON_VISIBLE_FRUSTUM) != 0u;"));
            Assert.That(scheduleScore, Does.Contain("bool hintedSafetyProbe = (inputReasonFlags & DDGI_SCHEDULE_REASON_OUTSIDE_FRUSTUM_SAFETY) != 0u;"));
            Assert.That(scheduleScore, Does.Contain("bool dirtyProbe = hintedDirtyProbe || DdgiScheduleProbeIntersectsDirtyRegion(volume.ProbePosition, constants);"));
            Assert.That(scheduleScore, Does.Contain("bool visibleProbe = hintedVisibleProbe || DdgiScheduleProbeInViewFrustum(volume.ProbePosition, constants);"));
            Assert.That(scheduleScore, Does.Contain("bool safetyProbe = !visibleProbe && (hintedSafetyProbe || DdgiScheduleProbeInSafetyShell(volume.ProbePosition, constants));"));
            Assert.That(scheduleScore, Does.Contain("DDGI_WARMUP_STATE_LOCAL_VOLUME"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_WARMUP_WARMED_CASCADE0_PROBE_COUNT"));
            Assert.That(scheduleScore, Does.Contain("uint visibleReserveBudget = max((constants.RequestBudget * 30u + 99u) / 100u, 1u);"));
            Assert.That(scheduleScore, Does.Contain("uint visibleObservedRefreshBudget = max((constants.RequestBudget * 15u + 99u) / 100u, 1u);"));
            Assert.That(scheduleScore, Does.Contain("uint localRefreshBudget = warmupActive ? max(constants.WarmupLocalBudget, 1u) : visibleObservedRefreshBudget;"));
            Assert.That(scheduleScore, Does.Contain("uint cascade0RefreshBudget = warmupActive ? min(max(constants.WarmupCascade0Budget, 1u), visibleObservedRefreshBudget) : visibleObservedRefreshBudget;"));
            Assert.That(scheduleScore, Does.Contain("uint warmupVisibleMaxProbeAge = max(60u, (constants.ActiveProbeCount + visibleObservedRefreshBudget - 1u) / visibleObservedRefreshBudget);"));
            Assert.That(scheduleScore, Does.Contain("uint warmupLocalMaxProbeAge = max(60u, (constants.ActiveProbeCount + localRefreshBudget - 1u) / localRefreshBudget);"));
            Assert.That(scheduleScore, Does.Contain("uint warmupCascade0MaxProbeAge = max(60u, (constants.ActiveProbeCount + cascade0RefreshBudget - 1u) / cascade0RefreshBudget);"));
            Assert.That(scheduleScore, Does.Contain("bool warmedQuality = qualityAndReason.y > 0.25 && qualityAndReason.z > 0.10;"));
            Assert.That(scheduleScore, Does.Contain("probeAge <= warmupVisibleMaxProbeAge;"));
            Assert.That(scheduleScore, Does.Contain("probeAge <= warmupLocalMaxProbeAge;"));
            Assert.That(scheduleScore, Does.Contain("probeAge <= warmupCascade0MaxProbeAge;"));
            Assert.That(scheduleScore, Does.Not.Contain("bool warmedProbe = stateIrradiance.w > 0.5"));
            Assert.That(scheduleShared, Does.Contain("DDGI_SCHEDULE_REASON_LOW_CONFIDENCE"));
            Assert.That(scheduleShared, Does.Contain("bool DdgiScheduleLaneSelected(uint probeIndex, uint frameSerial, uint divisor)"));
            Assert.That(scheduleScore, Does.Contain("bool lowConfidenceProbe = !newProbe && visibleProbe && combinedConfidence < 0.55;"));
            Assert.That(scheduleScore, Does.Contain("float luminanceChange = clamp(stateHistory.z, 0.0, 1.0);"));
            Assert.That(scheduleScore, Does.Contain("float storedInconsistency = ReadStorageFloat(uint(DDGI_PROBE_STATE_BUFFER_INDEX), stateBase + uint(OFFSET_GPU_DDGI_PROBE_STATE_UPDATE_METADATA) / 4u + 3u);"));
            Assert.That(scheduleScore, Does.Contain("storedInconsistency = (isnan(storedInconsistency) || isinf(storedInconsistency)) ? 0.0 : clamp(storedInconsistency, 0.0, 1.0);"));
            Assert.That(scheduleScore, Does.Contain("float luminanceInconsistency = max(luminanceChange, storedInconsistency);"));
            Assert.That(scheduleScore, Does.Contain("float ReadDdgiScheduleHistoricalHitRatio(uint probeIndex)"));
            Assert.That(scheduleScore, Does.Contain("vec4 probeStatistics = ReadStorageVec4(uint(DDGI_PROBE_RELOCATION_CLASSIFICATION_BUFFER_INDEX), relocationBase + 8u);"));
            Assert.That(scheduleScore, Does.Contain("return clamp(probeStatistics.w, 0.0, 1.0);"));
            Assert.That(scheduleScore, Does.Contain("uint ResolveDdgiGeometryProximateLaneDivisor(uint baseDivisor, float historicalHitRatio)"));
            Assert.That(scheduleScore, Does.Contain("float geometryProximity = smoothstep(0.02, 0.20, historicalHitRatio);"));
            Assert.That(scheduleScore, Does.Contain("float historicalHitRatio = ReadDdgiScheduleHistoricalHitRatio(probeIndex);"));
            Assert.That(scheduleScore, Does.Contain("bool geometryProximateProbe = historicalHitRatio > 0.02;"));
            Assert.That(scheduleScore, Does.Contain("bool highVarianceProbe = !newProbe && visibleProbe && probeAge >= 2u && luminanceInconsistency > 0.35;"));
            Assert.That(scheduleScore, Does.Contain("uint AlignDdgiScheduleRayBucket(uint rayCount, uint maxRayCount)"));
            Assert.That(scheduleScore, Does.Contain("if (rays <= 32u)"));
            Assert.That(scheduleScore, Does.Contain("return min(32u, safeMax);"));
            Assert.That(scheduleScore, Does.Not.Contain("if (rays <= 8u)"));
            Assert.That(scheduleScore, Does.Not.Contain("return min(8u, safeMax);"));
            Assert.That(scheduleScore, Does.Not.Contain("if (rays <= 16u)"));
            Assert.That(scheduleScore, Does.Not.Contain("return min(16u, safeMax);"));
            Assert.That(scheduleScore, Does.Not.Contain("if (rays <= 24u)"));
            Assert.That(scheduleScore, Does.Not.Contain("return min(24u, safeMax);"));
            Assert.That(scheduleScore, Does.Contain("uint ResolveDdgiScheduleAdaptiveRayCost("));
            Assert.That(scheduleScore, Does.Contain("float varianceBoost = mix(1.25, 1.5, clamp((luminanceInconsistency - 0.35) / 0.65, 0.0, 1.0));"));
            Assert.That(scheduleScore, Does.Not.Contain("float varianceBoost = mix(1.25, 1.75, clamp((luminanceInconsistency - 0.25) / 0.75, 0.0, 1.0));"));
            Assert.That(scheduleScore, Does.Contain("uint primaryRayCost = ResolveDdgiScheduleAdaptiveRayCost("));
            Assert.That(scheduleScore, Does.Contain("primaryRayCost,"));
            Assert.That(scheduleShared, Does.Contain("PackDdgiScheduleRequestPriorityAndRays(priority, rayCount)"));
            Assert.That(scheduleShared, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_RAY_CAPACITY_PER_PROBE"));
            Assert.That(shader, Does.Contain("request.RayCount = packedPriority >> DDGI_UPDATE_REQUEST_RAY_COUNT_SHIFT;"));
            Assert.That(manager, Does.Contain("ResolveAverageRaysPerProbe()"));
            Assert.That(manager, Does.Contain("_lastGpuUpdateEstimated,"));
            Assert.That(manager, Does.Contain("int averageRaysPerProbe = ResolveAverageRaysPerProbe();"));
            Assert.That(manager, Does.Contain("averageRaysPerProbe);"));
            Assert.That(manager, Does.Contain("private int ResolveAverageRaysPerProbe()"));
            Assert.That(manager, Does.Contain("weightedRays += (long)raysPerProbe * probeCount;"));
            Assert.That(manager, Does.Contain("long minimumRays = (long)requestBudget * Math.Max(1, averageRaysPerProbe);"));
            Assert.That(scheduler, Does.Contain("ResolveRaySampleRequestBudget(primaryRayBudget, averageRays, hardMax)"));
            Assert.That(scheduler, Does.Contain("int raySampleBudget = Math.Max(1, primaryRayBudget / safeAverageRays);"));
            Assert.That(scheduleScore, Does.Contain("uint boundedLaneDivisor = max(DdgiScheduleScanProbeCount(constants) / max(constants.RequestBudget * 4u, 1u), 1u);"));
            Assert.That(scheduleScore, Does.Contain("uint visibleReserveDivisor = max(DdgiScheduleScanProbeCount(constants) / visibleReserveBudget, 1u);"));
            Assert.That(scheduleScore, Does.Contain("uint geometryVisibleReserveDivisor = ResolveDdgiGeometryProximateLaneDivisor(visibleReserveDivisor, historicalHitRatio);"));
            Assert.That(scheduleScore, Does.Contain("uint geometryBoundedLaneDivisor = ResolveDdgiGeometryProximateLaneDivisor(boundedLaneDivisor, historicalHitRatio);"));
            Assert.That(scheduleScore, Does.Contain("bool steadyVisibleProbeSelected = !warmupActive"));
            Assert.That(scheduleScore, Does.Contain("bool visibleHotProbe = visibleProbe && (localAuthoredProbe || lowConfidenceProbe || highVarianceProbe || (cascade0Probe && geometryProximateProbe));"));
            Assert.That(scheduleScore, Does.Contain("DdgiScheduleLaneSelected(probeIndex, constants.FrameSerial + 101u, geometryVisibleReserveDivisor);"));
            Assert.That(scheduleScore, Does.Contain("steadyVisibleProbeSelected ||"));
            Assert.That(scheduleScore, Does.Contain("(visibleProbe && DdgiScheduleLaneSelected(probeIndex, constants.FrameSerial, geometryBoundedLaneDivisor));"));
            Assert.That(scheduleScore, Does.Contain("bool safetyProbeSelected = safetyProbe && DdgiScheduleLaneSelected"));
            Assert.That(scheduleScore, Does.Contain("bool ageProbeSelected = hintedAgeProbe ||"));
            Assert.That(scheduleScore, Does.Contain("(ageDue && DdgiScheduleLaneSelected"));
            Assert.That(scheduleShared, Does.Contain("TryReserveDdgiScheduleCandidateSlot"));
            Assert.That(scheduleShared, Does.Contain("uint globalCap = min(max(requestBudget * 4u, 1u), outputCapacity);"));
            Assert.That(scheduleScore, Does.Contain("uint reserveResult = TryReserveDdgiScheduleCandidateSlot(constants, groupIndex, priority, reasonFlags);"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_CANDIDATE_BUFFER_OVERFLOW_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_PER_BUCKET_OVERFLOW_COUNT"));
            Assert.That(scheduleShared, Does.Contain("uint localTopKCap = min(max((requestBudget + groupCount - 1u) / groupCount, 1u), 16u);"));
            Assert.That(scheduleReset, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_SCAN_PROBE_COUNT"));
            Assert.That(scheduleReset, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_CANDIDATE_OUTPUT_CAPACITY"));
            Assert.That(schedulePass, Does.Contain("RecordGpuSchedulerResetClears(cmd);"));
            Assert.That(schedulePass, Does.Contain("DispatchPipeline(cmd, _pipelines[0], 1);"));
            Assert.That(scheduleFinalize, Does.Contain("bucketQuota = min(constants.WarmupLocalBudget, requestBudget);"));
            Assert.That(scheduleFinalize, Does.Contain("bucketQuota = min((requestBudget * 40u + 99u) / 100u, requestBudget);"));
            Assert.That(scheduleFinalize, Does.Contain("bucketQuota = min((requestBudget * 30u + 99u) / 100u, requestBudget);"));
            Assert.That(scheduleFinalize, Does.Contain("bucketQuota = min((requestBudget * 20u + 99u) / 100u, requestBudget);"));
            Assert.That(scheduleFinalize, Does.Contain("uint unusedQuotaCarry = 0u;"));
            Assert.That(scheduleFinalize, Does.Contain("if (requestCount >= requestBudget || bucketRequestCount >= bucketRequestBudget)"));
            Assert.That(scheduleFinalize, Does.Not.Contain("if (bucketRequestCount >= bucketRequestBudget)\n                break;"));
            Assert.That(scheduleFinalize, Does.Not.Contain("if (requestCount >= requestBudget || primaryRayCount >= primaryRayBudget)\n                break;"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_STABLE_SKIPPED_COUNT"));
            Assert.That(scheduleScore, Does.Not.Contain("uint reasonFlags = DDGI_SCHEDULE_REASON_AGE_REFRESH;"));
            Assert.That(scheduleFinalize, Does.Contain("WriteDdgiProbeUpdateRequestFromCandidate"));
            Assert.That(scheduleFinalize, Does.Contain("priorityBucketMismatchSkipCount++;"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_PRIORITY_BUCKET_MISMATCH_SKIP_COUNT"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_REQUEST_COUNT"));
            Assert.That(scheduleFinalize, Does.Contain("WriteStorageWord(uint(DDGI_SCHEDULER_COUNTER_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_COUNTER_OVERFLOW_COUNT) / 4u, candidateBufferOverflowCount);"));
            Assert.That(scheduleFinalize, Does.Not.Contain("candidateBufferOverflowCount + perBucketOverflowCount"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_REQUEST_BUDGET_REJECTED_COUNT"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_PRIMARY_RAY_BUDGET_REJECTED_COUNT"));
            Assert.That(manager, Does.Contain("ResolveWarmupMaxAgeFrames(_activeProbeCount, requestBudget)"));
            Assert.That(manager, Does.Contain("ResolveWarmupMaxAgeFrames(_activeProbeCount, _lastProbeUpdateRequestBudget)"));
            Assert.That(manager, Does.Contain("cascade0Fraction = 0.65f;"));
            Assert.That(manager, Does.Contain("safetyFraction = 0.05f;"));
            Assert.That(manager, Does.Contain("cascade0Fraction = 0.70f;"));
            Assert.That(manager, Does.Contain("GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag"));
            Assert.That(manager, Does.Contain("GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag"));
            Assert.That(manager, Does.Contain("GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag"));
            Assert.That(manager, Does.Contain("ReasonFlags = reasonFlags"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_PRIORITY0_REQUEST_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_VISIBLE_FRUSTUM_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_SAFETY_SHELL_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_LOW_CONFIDENCE_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_HIGH_VARIANCE_COUNT"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_CANDIDATE_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_AGE_REFRESH_COUNT"));
            Assert.That(schedulePrefix, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_CANDIDATE_COUNT"));
            Assert.That(scheduleCompact, Does.Contain("CopyDdgiProbeCandidate(constants.CandidateOutputOffset + compactedOffset, candidateIndex);"));
            Assert.That(scheduleFinalize, Does.Contain("constants.CandidateOutputOffset + bucketStart"));
            Assert.That(scheduleFinalize, Does.Not.Contain("offset < constants.ActiveProbeCount"));
            Assert.That(trace, Does.Contain("#define DDGI_TRACE_PASS 1"));
            Assert.That(blend, Does.Contain("#define DDGI_BLEND_PASS 1"));
            Assert.That(relocateClassify, Does.Contain("#define DDGI_RELOCATE_CLASSIFY_PASS 1"));
            Assert.That(shader, Does.Contain("uint RayResultScratchBufferIndex;"));
            Assert.That(shader, Does.Contain("void WriteDdgiRayResult(uint updateIndex, uint rayIndex, DdgiRayResult result)"));
            Assert.That(shader, Does.Contain("DdgiRayResult ReadDdgiRayResult(uint updateIndex, uint rayIndex)"));
            Assert.That(shader, Does.Contain("stableDiffuse = EvaluateStableDdgiDiffuseRadianceAtHit(worldPosition, normal, albedo);"));
            Assert.That(shader, Does.Contain("vec3 emissiveProxyDiffuse = EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit(worldPosition, normal, albedo);"));
            Assert.That(shader, Does.Contain("return directDiffuse + emissiveDiffuse + stableDiffuse;"));
            Assert.That(shader, Does.Contain("radiance = EvaluateDdgiRayQuerySurfaceRadianceAtHit("));
            Assert.That(shader, Does.Contain("ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);"));
            Assert.That(shader, Does.Contain("ReadPackedHalf4(pc.IrradianceAtlasBufferIndex"));
            Assert.That(shader, Does.Contain("DecodeDdgiIrradianceAtlasSqrtSample(ReadPackedHalf4(pc.IrradianceAtlasBufferIndex"));
            Assert.That(shader, Does.Contain("return ResolveDdgiIrradianceAtlasSqrtBlend(mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y));"));
            Assert.That(shader, Does.Contain("ReadPackedHalf2(pc.VisibilityAtlasBufferIndex"));
            Assert.That(shader, Does.Contain("vec3 sampledIrradiance = blendedIrradiance / blendedCoverage;"));
            Assert.That(shader, Does.Contain("return clamp(sampledIrradiance, vec3(0.0), vec3(64.0));"));
            Assert.That(shader, Does.Not.Contain("rawIrradiance / globalIntensity"));
            Assert.That(schedulePass, Does.Contain("public sealed unsafe class DdgiSchedulePass"));
            Assert.That(schedulePass, Does.Contain("ddgi_schedule_reset.comp.spv"));
            Assert.That(schedulePass, Does.Contain("ddgi_schedule_score.comp.spv"));
            Assert.That(schedulePass, Does.Contain("ddgi_schedule_prefix.comp.spv"));
            Assert.That(schedulePass, Does.Contain("ddgi_schedule_compact.comp.spv"));
            Assert.That(schedulePass, Does.Contain("ddgi_schedule_finalize.comp.spv"));
            Assert.That(schedulePass, Does.Contain("PipelineStageFlags2.DrawIndirectBit"));
            Assert.That(schedulePass, Does.Contain("AccessFlags2.IndirectCommandReadBit"));
            Assert.That(schedulePass, Does.Contain("InsertScheduleStageBarrier"));
            Assert.That(schedulePass, Does.Contain("InsertScheduleToTraceBarrier"));
            Assert.That(schedulePass, Does.Contain("RecordGpuSchedulerCounterReadback"));
            Assert.That(schedulePass, Does.Contain("InitializationFailureReason"));
            Assert.That(schedulePass, Does.Contain("DdgiGpuSchedulerFallbackActive == 0"));
            Assert.That(pass, Does.Contain("public sealed unsafe class DdgiTracePass"));
            Assert.That(pass, Does.Contain("GpuSchedulerFlag"));
            Assert.That(pass, Does.Contain("CanUseGpuSchedulerIndirectDispatch"));
            Assert.That(pass, Does.Contain("RecordGpuSchedulerTraceIndirectDispatch"));
            Assert.That(pass, Does.Contain("IsGpuSchedulerRenderingActive"));
            Assert.That(pass, Does.Contain("DdgiCompareModeUseGpuQueueForRendering"));
            Assert.That(pass, Does.Contain("sceneData.DdgiGpuSchedulerFallbackActive == 0"));
            Assert.That(pass, Does.Contain("CmdDispatch(cmd, (uint)sceneData.DdgiProbesUpdated, 1, 1)"));
            Assert.That(shader, Does.Contain("ResolveDdgiUpdateRequestCount()"));
            Assert.That(renderer, Does.Contain("gpuSchedulerActive"));
            Assert.That(renderer, Does.Contain("ResolveDdgiGpuSchedulerCounterFailureReason"));
            Assert.That(renderer, Does.Contain("DdgiGpuSchedulerForceCpuFallback"));
            Assert.That(renderer, Does.Contain("gpu-scheduler-input-prep-failed"));
            Assert.That(renderer, Does.Contain("CaptureGpuSchedulerValidationExpectedFrame"));
            Assert.That(renderer, Does.Contain("DdgiCompareModeUseGpuQueueForRendering"));
            Assert.That(renderer, Does.Contain("ReadCompletedGpuSchedulerCounters(_currentFrame)"));
            Assert.That(renderer, Does.Contain("UploadScheduledProbeUpdateQueue(_stagingRing, _currentCommandBuffer);"));
            Assert.That(pass, Does.Contain("public sealed unsafe class DdgiBlendPass"));
            Assert.That(pass, Does.Contain("public sealed unsafe class DdgiRelocateClassifyPass"));
            Assert.That(pass, Does.Contain("public sealed unsafe class DdgiPublishPass"));
            Assert.That(pass, Does.Contain("DstStageMask = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit"));
            Assert.That(pass, Does.Contain("DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderSampledReadBit"));
            Assert.That(manager, Does.Contain("BindlessIndex.DdgiRayResultScratchBuffer"));
            Assert.That(manager, Does.Contain("HasGpuSchedulerTraceIndirectDispatchBuffer"));
            Assert.That(manager, Does.Contain("CmdDispatchIndirect(commandBuffer, indirectBuffer, 0)"));
            Assert.That(manager, Does.Contain("CalculateRayScratchBytes("));
            Assert.That(manager, Does.Contain("ReadCompletedGpuSchedulerCounters"));
            Assert.That(manager, Does.Contain("ValidateCompletedGpuSchedulerFrame"));
            Assert.That(manager, Does.Contain("gpu-schedule-over-budget"));
            Assert.That(manager, Does.Contain("DdgiGpuSchedulerLocalScanFraction"));
            Assert.That(manager, Does.Contain("DdgiGpuSchedulerCascade0ScanFraction"));
            Assert.That(manager, Does.Contain("DdgiGpuSchedulerSafetyScanFraction"));
            Assert.That(manager, Does.Contain("DdgiGpuSchedulerDirtyScanFraction"));
            Assert.That(manager, Does.Contain("CmdCopyBuffer(commandBuffer, source, destination"));
            Assert.That(renderer, Does.Contain("requested but inactive: renderer does not yet create a dedicated async compute queue; graph queue ownership transitions are diagnostic-only."));
            Assert.That(renderer, Does.Contain("DdgiAsyncComputeEnabled = ddgiAsyncComputeActuallyEnabled ? 1 : 0"));
            Assert.That(renderer, Does.Contain("IsDdgiAsyncComputeActuallyEnabled"));
            Assert.That(shader, Does.Not.Contain("RecursiveProbeStateBufferIndex"));
            Assert.That(shader, Does.Not.Contain("RecursiveIrradianceAtlasBufferIndex"));
            Assert.That(shader, Does.Not.Contain("RecursiveVisibilityAtlasBufferIndex"));
            Assert.That(manager, Does.Not.Contain("CopyRecursiveCacheRange("));
            Assert.That(manager, Does.Not.Contain("BindlessIndex.DdgiRecursiveIrradianceAtlasBuffer"));
        });
    }

    [Test]
    public void ForwardShader_WeightsDdgiSamplingByProbeQualityDiagnostics()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("vec4 qualityAndReason = ReadStorageVec4(uint(DDGI_PROBE_STATE_BUFFER_INDEX), stateBase + 12u);"));
            Assert.That(shader, Does.Contain("float rayHitConfidence = clamp(sampleQualityAndReason.x, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float stateIrradianceConfidence = clamp(sampleQualityAndReason.y, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityConfidence = clamp(sampleQualityAndReason.z, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float irradianceConfidence = clamp(probeIrradianceSample.w, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float sourceProbeActive = clamp(min(stateIrradiance.w, relocationAndClassification.w), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float probeActive = clamp(min(sampleStateIrradiance.w, sampleRelocationAndClassification.w), 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("probeActive = max(probeActive, irradianceConfidence);"));
            Assert.That(shader, Does.Contain("bool confidenceBypass = DdgiDebugBypassConfidenceSuppression();"));
            Assert.That(shader, Does.Contain("float atlasDataTrust = confidenceBypass ? 1.0 : DdgiSparseDataTrust(irradianceConfidence);"));
            Assert.That(shader, Does.Contain("float radianceTransportTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(rayHitConfidence, 0.35);"));
            Assert.That(shader, Does.Contain("float stateIrradianceTrust = confidenceBypass ? 1.0 : DdgiSoftConfidenceTrust(max(stateIrradianceConfidence, irradianceConfidence), 0.45);"));
            Assert.That(shader, Does.Contain("float qualityConfidence = clamp(radianceTransportTrust * stateIrradianceTrust, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float transportConfidence = clamp(rayHitConfidence + visibilityConfidence, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float qualityConfidence = clamp(radianceTransportConfidence * max(stateIrradianceConfidence, irradianceConfidence), 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("qualityConfidence = max(qualityConfidence, 0.25);"));
            Assert.That(shader, Does.Contain("float supportWeight = expectedContributionWeight * probeActive * atlasDataTrust;"));
            Assert.That(shader, Does.Contain("float radianceWeight = supportWeight * qualityConfidence;"));
            Assert.That(shader, Does.Contain("totalActive += probeActive * atlasDataTrust * cellWeight;"));
            Assert.That(shader, Does.Not.Contain("float supportWeight = expectedContributionWeight * probeActive * irradianceConfidence * qualityConfidence;"));
            Assert.That(shader, Does.Not.Contain("totalActive += probeActive * irradianceConfidence * qualityConfidence * cellWeight;"));
        });
    }

    [Test]
    public void ForwardShader_ExposesDdgiCoverageAndCascadeDebugViews()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE = 92u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION = 93u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT = 94u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS = 95u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET = 96u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE = 101u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK = 102u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT = 103u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT = 104u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED = 105u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE = 106u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS = 107u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE = 108u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE = 109u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE = 110u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE = 111u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN = 112u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION = 113u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION = 114u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION = 115u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT = 116u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE = 117u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE = 118u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS = 119u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_GLOBAL_SDF_SLICE = 120u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_SURFACE_CACHE_CARD_PROJECTION = 121u"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BACKEND_HEATMAP = 122u"));
            Assert.That(shader, Does.Contain("float cascadeIndex;"));
            Assert.That(shader, Does.Contain("float cascadeBlendWeight;"));
            Assert.That(shader, Does.Contain("float updateReason;"));
            Assert.That(shader, Does.Contain("float rayBudget;"));
            Assert.That(shader, Does.Contain("float irradianceAtlasConfidence;"));
            Assert.That(shader, Does.Contain("float rayHitConfidence;"));
            Assert.That(shader, Does.Contain("float stateIrradianceConfidence;"));
            Assert.That(shader, Does.Contain("float visibilityConfidence;"));
            Assert.That(shader, Does.Contain("float qualityConfidence;"));
            Assert.That(shader, Does.Contain("float minProbeSpacing;"));
            Assert.That(shader, Does.Contain("vec3 logicalProbePosition;"));
            Assert.That(shader, Does.Contain("vec3 relocatedProbePosition;"));
            Assert.That(shader, Does.Contain("float classificationInvalidScore;"));
            Assert.That(shader, Does.Contain("float visibilityMomentMean;"));
            Assert.That(shader, Does.Contain("float visibilityMomentVariance;"));
            Assert.That(shader, Does.Contain("float visibilityProbeDistance;"));
            Assert.That(shader, Does.Contain("float visibilityMaxRayDistance;"));
            Assert.That(shader, Does.Contain("bool IsDdgiDebugView(uint view)"));
            Assert.That(shader, Does.Contain("vec3 ApplyDdgiDebugIdentity(vec3 color, uint view)"));
            Assert.That(shader, Does.Contain("void WriteDdgiDebugColor(uint view, vec3 color)"));
            Assert.That(shader, Does.Contain("view >= GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE"));
            Assert.That(shader, Does.Contain("view <= GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BACKEND_HEATMAP"));
            Assert.That(shader, Does.Contain("vec3 ForwardWorldRayDirection()"));
            Assert.That(shader, Does.Contain("vec3 GlobalSdfRaymarchDebugColor(vec3 worldPosition)"));
            Assert.That(shader, Does.Contain("TraceGlobalSdfCascadeSegment("));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_GLOBAL_SDF_SLICE, GlobalSdfRaymarchDebugColor(fragWorldPosition));"));
            Assert.That(shader, Does.Not.Contain("vec3 GlobalSdfSliceDebugColor(vec3 worldPosition)"));
            Assert.That(shader, Does.Contain("vec3 SurfaceCacheCardProjectionDebugColor(vec3 worldPosition, vec3 normal)"));
            Assert.That(shader, Does.Contain("vec3 DdgiRayBackendHeatmapDebugColor(DdgiSampleResult ddgiSample)"));
            Assert.That(shader, Does.Contain("p.x < 4.0 || p.y < 4.0"));
            Assert.That(shader, Does.Contain("bool badge = p.x < 96.0 && p.y < 32.0;"));
            Assert.That(shader, Does.Contain("for (uint bit = 0u; bit < 6u; bit++)"));
            Assert.That(shader, Does.Contain("bool legend = p.x < 96.0 && p.y >= screen.y - 12.0;"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE, vec3(clamp(ddgiSample.spatialCoverage, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE, vec3(clamp(ddgiSample.supportCoverage, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE, vec3(clamp(ddgiSample.weight, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE, vec3(clamp(ddgiSample.visibilityConfidence, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("clamp(ddgiSample.irradianceAtlasConfidence, 0.0, 1.0),"));
            Assert.That(shader, Does.Contain("clamp(ddgiSample.qualityConfidence, 0.0, 1.0),"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT, vec3(clamp(hybridDiffuse.effectiveDdgiWeight, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT, vec3(clamp(hybridDiffuse.environmentFallbackWeight / 4.0, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS)"));
            Assert.That(shader, Does.Contain("clamp(ddgiSample.visibilityMomentMean / visibilityMaxDistance, 0.0, 1.0)"));
            Assert.That(shader, Does.Contain("clamp(sqrt(max(ddgiSample.visibilityMomentVariance, 0.0)) / visibilityMaxDistance, 0.0, 1.0)"));
            Assert.That(shader, Does.Contain("clamp(ddgiSample.visibilityProbeDistance / visibilityMaxDistance, 0.0, 1.0)"));
            Assert.That(shader, Does.Contain("float relocationAmount = length(ddgiSample.relocation) / max(ddgiSample.minProbeSpacing * 0.4, 0.001);"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION, abs(ddgiSample.relocation));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED, vec3(clamp(relocationAmount, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION)"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION, fract(abs(ddgiSample.logicalProbePosition) * 0.05));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION)"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION, fract(abs(ddgiSample.relocatedProbePosition) * 0.05));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION)"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE, vec3(clamp(ddgiSample.classificationInvalidScore, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION, MeshletDebugColor(uint(max(ddgiSample.cascadeIndex, 0.0)) + 1u));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT, vec3(clamp(ddgiSample.cascadeBlendWeight, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS, MeshletDebugColor(uint(clamp(ddgiSample.updateReason * 255.0, 0.0, 255.0))));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET, vec3(ddgiSample.rayBudget, ddgiSample.supportCoverage, ddgiSample.weight));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT)"));
            Assert.That(shader, Does.Contain("if (!ReadDdgiGatherTile(tile))"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT, vec3(1.0, 0.0, 1.0));"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT, vec3(clamp(tile.blendWeights.y, 0.0, 1.0)));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE)"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE)"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE, clamp(ddgiSample.irradiance, vec3(0.0), vec3(64.0)));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE)"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE, clamp(ddgiDiffuse, vec3(0.0), vec3(64.0)));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS)"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS, clamp(hybridDiffuse.diffuse, vec3(0.0), vec3(64.0)));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK)"));
            Assert.That(shader, Does.Contain("WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK, clamp(hybridDiffuse.suppressionMask, vec3(0.0), vec3(1.0)));"));
            Assert.That(shader, Does.Contain("DDGI_FORWARD_ESTIMATE_SAMPLED_IRRADIANCE_LUMINANCE_COUNTER"));
            Assert.That(shader, Does.Contain("DDGI_FORWARD_ESTIMATE_ENVIRONMENT_FALLBACK_WEIGHT_COUNTER"));
            Assert.That(shader, Does.Contain("PackDdgiForwardEstimateLuminance(DdgiDiagnosticLuminance(ddgi.irradiance))"));
            Assert.That(shader, Does.Contain("PackDdgiForwardEstimateWeight(hybridDiffuse.environmentFallbackWeight / 4.0)"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiCoverage => 92u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiCascadeSelection => 93u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiCascadeBlendWeight => 94u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiUpdateReasons => 95u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiRayBudget => 96u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiRawDiffuse => 101u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiSuppressionMask => 102u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiEffectiveWeight => 103u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight => 104u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiRelocationNormalized => 105u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiClassificationInvalidScore => 106u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiVisibilityMoments => 107u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiSpatialCoverage => 108u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiSupportCoverage => 109u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiDataConfidence => 110u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiVisibilityConfidence => 111u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiConfidenceChain => 112u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiProbeLogicalPosition => 113u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiProbeRelocatedPosition => 114u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiProbeRelocationDirection => 115u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiGatherBlendWeight => 116u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiSampledIrradiance => 117u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiFinalDiffuse => 118u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiConfidenceBypass => 119u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.GlobalSdfSlice => 120u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.SurfaceCacheCardProjection => 121u"));
            Assert.That(renderer, Does.Contain("GlobalIlluminationDebugView.DdgiRayBackendHeatmap => 122u"));
        });
    }

    [Test]
    public void GlobalSdfShaders_UseToroidalClipmapBrickAddressing()
    {
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string update = ReadRepoText("Njulf.Shaders", "global_sdf_update.comp");
        string sampling = ReadRepoText("Njulf.Shaders", "global_sdf.glsl");
        string manager = ReadRepoText("Njulf.Rendering", "Resources", "GlobalSdfManager.cs");
        string pass = ReadRepoText("Njulf.Rendering", "Pipeline", "GlobalSdfPasses.cs");

        Assert.Multiple(() =>
        {
            Assert.That(common, Does.Contain("int LogicalGridMinX;"));
            Assert.That(common, Does.Contain("int RingOffsetX;"));
            Assert.That(common, Does.Contain("uint BricksPerAxis;"));
            Assert.That(update, Does.Contain("PositiveModulo(physicalBrick.x - ringOffset.x"));
            Assert.That(update, Does.Contain("vec3 worldPosition = pc.Push.WorldMinAndVoxelSize.xyz + (vec3(logicalVoxel) + vec3(0.5))"));
            Assert.That(update, Does.Contain("SharedMeshSdfBoundsCenterRadius"));
            Assert.That(update, Does.Contain("DistanceToBoundingSphere"));
            Assert.That(update, Does.Contain("nearestCandidateIndex"));
            Assert.That(update, Does.Contain("if (boundsDistance >= distanceMeters)"));
            Assert.That(sampling, Does.Contain("GlobalSdfLogicalVoxelToPhysicalTexel"));
            Assert.That(sampling, Does.Contain("float FetchGlobalSdfCascadeEncodedDistance(ivec3 logicalVoxel, GPUGlobalSdfCascade cascade)"));
            Assert.That(sampling, Does.Contain("SampleGlobalSdfCascadeLod"));
            Assert.That(sampling, Does.Contain("texelFetch(BindlessVolumeTextures[nonuniformEXT(cascade.TextureIndex)], physicalTexel, 0).r;"));
            Assert.That(sampling, Does.Contain("vec3 centeredLogicalVoxel = logicalVoxelFloat - vec3(0.5);"));
            Assert.That(sampling, Does.Contain("ivec3 logicalVoxel = ivec3(floor(centeredLogicalVoxel));"));
            Assert.That(sampling, Does.Contain("vec3 voxelFraction = fract(centeredLogicalVoxel);"));
            Assert.That(sampling, Does.Contain("float c000 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(0, 0, 0), cascade);"));
            Assert.That(sampling, Does.Contain("float c111 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(1, 1, 1), cascade);"));
            Assert.That(sampling, Does.Contain("float encodedDistance = mix("));
            Assert.That(sampling, Does.Not.Contain("float encodedDistance = textureLod("));
            Assert.That(sampling, Does.Contain("SelectGlobalSdfTraceLod"));
            Assert.That(sampling, Does.Contain("TraceGlobalSdfCascadeSegment"));
            Assert.That(sampling, Does.Contain("float initialSurfaceBandEnd = initialT + voxelSize;"));
            Assert.That(sampling, Does.Contain("hitTestArmed = sdfSample.DistanceMeters > hitEpsilon || t > initialSurfaceBandEnd;"));
            Assert.That(pass, Does.Contain("\"GlobalSdfUpload\""));
            Assert.That(pass, Does.Contain("\"GlobalSdfBricks\""));
            Assert.That(pass, Does.Contain("\"GlobalSdfMips\""));
            Assert.That(manager, Does.Contain("DdgiClipmapAddressing.CalculateLocalPhysicalProbeIndex"));
            Assert.That(manager, Does.Contain("ApplyDdgiEvents"));
            Assert.That(manager, Does.Contain("MarkDirtyProbeRequest"));
            Assert.That(pass, Does.Contain("_ddgiFrameLayoutProvider()"));
        });
    }

    [Test]
    public void SurfaceCacheShaders_UseWorkBufferGridAndCacheFirstHitShading()
    {
        string surfaceCache = ReadRepoText("Njulf.Shaders", "surface_cache_update.comp");
        string ddgi = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");
        string manager = ReadRepoText("Njulf.Rendering", "Resources", "SurfaceCacheManager.cs");
        string surfacePass = ReadRepoText("Njulf.Rendering", "Pipeline", "SurfaceCachePasses.cs");
        string ddgiPass = ReadRepoText("Njulf.Rendering", "Pipeline", "DdgiPipelinePasses.cs");

        Assert.Multiple(() =>
        {
            Assert.That(surfaceCache, Does.Contain("ReadSurfaceCacheWorkWord"));
            Assert.That(surfaceCache, Does.Contain("SurfaceCacheWorkCaptureListOffset"));
            Assert.That(surfaceCache, Does.Contain("uint cardIndex = ReadSurfaceCacheWorkWord(SurfaceCacheWorkCaptureListOffset() + captureTileOffset);"));
            Assert.That(surfaceCache, Does.Contain("uint tileSize = CardTileSize(card);"));
            Assert.That(surfaceCache, Does.Contain("if (tileTexel >= CardTileTexelCount(card))"));

            Assert.That(ddgi, Does.Contain("SurfaceCacheWorkBufferIndex"));
            Assert.That(ddgi, Does.Contain("TryResolveDdgiSurfaceCacheGridCell"));
            Assert.That(ddgi, Does.Contain("ConsiderDdgiSurfaceCacheCard"));
            Assert.That(ddgi, Does.Contain("uint gridCellsOffset = ReadDdgiSurfaceCacheWorkWord(9u);"));
            Assert.That(ddgi, Does.Not.Contain("4096u"));
            Assert.That(ddgi, Does.Contain("if (!forceAnalyticFallback && TrySampleDdgiSurfaceCacheRadiance(hitPosition, surfaceNormal, surfaceAlbedo, cacheRadiance))"));

            Assert.That(manager, Does.Contain("SurfaceCacheAtlasShelfAllocator"));
            Assert.That(manager, Does.Contain("BuildCaptureList"));
            Assert.That(manager, Does.Contain("InsertCardIntoGrid"));
            Assert.That(manager, Does.Contain("SurfaceCacheCardFlagNew"));
            Assert.That(surfacePass, Does.Contain("WorkBufferIndex = checked((uint)work.WorkBufferIndex)"));
            Assert.That(ddgiPass, Does.Contain("SurfaceCacheWorkBufferIndex = BindlessIndex.SurfaceCacheWorkBuffer"));
        });
    }

    [Test]
    public void ForwardShader_DoesNotEvaluateUntrustedDdgiVisibilityMomentsAsOcclusion()
    {
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("float DdgiVisibilityMomentTrust(float visibilityConfidence)"));
            Assert.That(shader, Does.Contain("float visibilityMean = info.maxRayDistance;"));
            Assert.That(shader, Does.Contain("float visibilityTransport = 1.0;"));
            Assert.That(shader, Does.Contain("float visibilityTrust = DdgiVisibilityMomentTrust(visibilityConfidence);"));
            Assert.That(shader, Does.Contain("if (visibilityTrust > 0.000001 && useProbeVisibility)"));
            Assert.That(shader, Does.Contain("vec2 visibilityMoments = ReadDdgiProbeVisibility(sampleProbeIndex, probeToPointDirection);"));
            Assert.That(shader, Does.Contain("float visibilityAttenuation = mix("));
            Assert.That(shader, Does.Contain("float probeVisibilityConfidence = DdgiVisibilityConfidence(visibilityAttenuation);"));
            Assert.That(shader, Does.Contain("float radianceWeight = supportWeight * qualityConfidence;"));
            Assert.That(shader, Does.Contain("float visibilityLeakFloor = mix(0.005, 0.05, probeVisibilityConfidence);"));
            Assert.That(shader, Does.Contain("float visibilityWeight = max(visibilityAttenuation * visibilityAttenuation * visibilityAttenuation, visibilityLeakFloor);"));
            Assert.That(shader, Does.Contain("float visibleRadianceWeight = ShapeDdgiGatherWeight(radianceWeight * visibilityWeight);"));
            Assert.That(shader, Does.Contain("float visibleSupportWeight = supportWeight * mix(0.05, 1.0, probeVisibilityConfidence);"));
            Assert.That(shader, Does.Contain("totalWeight += visibleRadianceWeight;"));
            Assert.That(shader, Does.Contain("dataWeightSum += visibleSupportWeight * qualityConfidence;"));
            Assert.That(shader, Does.Contain("visibilityWeightedSupport += visibleSupportWeight * visibilityAttenuation;"));
            Assert.That(shader, Does.Not.Contain("float visibleRadianceWeight = radianceWeight * visibilityAttenuation;"));
            Assert.That(shader, Does.Not.Contain("float visibilityWeightedContribution = supportWeight * visibilityTransport * visibilityTrust;"));
            Assert.That(shader, Does.Not.Contain("totalWeight += visibilityWeightedContribution;"));
            Assert.That(shader, Does.Not.Contain("dataWeightSum += visibilityWeightedContribution;"));
            Assert.That(shader, Does.Not.Contain("float probeVisibilityConfidence = DdgiVisibilityConfidence(visibilityTransport) * visibilityTrust;"));
        });
    }

    [Test]
    public void DdgiResourceInitialization_ClearsIrradianceAndInitializesVisibilityNonOccluding()
    {
        string manager = ReadRepoText("Njulf.Rendering", "Resources", "DdgiProbeVolumeManager.cs");

        Assert.Multiple(() =>
        {
            Assert.That(manager, Does.Contain("ClearStorageBuffer(commandBuffer, _probeStateBuffer, _probeStateBufferSize);"));
            Assert.That(manager, Does.Contain("ClearStorageBuffer(commandBuffer, _probeRelocationClassificationBuffer, _probeRelocationClassificationBufferSize);"));
            Assert.That(manager, Does.Contain("ClearStorageBuffer(commandBuffer, _irradianceAtlasBuffer, _irradianceAtlasBufferSize);"));
            Assert.That(manager, Does.Contain("float maxDistance = MathF.Max(volume.BiasAndProbeCountZ.Z > 0.0f ? volume.BiasAndProbeCountZ.Z : 16.0f, 0.1f);"));
            Assert.That(manager, Does.Contain("uint packedMoments = PackHalf2(maxDistance, maxDistance * maxDistance);"));
            Assert.That(manager, Does.Contain("CreateVisibilityAtlasRangeInitializationPayload("));
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
            Assert.That(forwardShader, Does.Not.Contain("SampleSsgiDiffuse"));
            Assert.That(forwardShader, Does.Not.Contain("GI_FINAL_DIFFUSE_TEXTURE_INDEX"));
            Assert.That(forwardShader, Does.Contain("float environmentFallbackWeight = clamp(environmentTrust * effectiveEnvironmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(forwardShader, Does.Contain("result.diffuse = SafeRadiance(ddgiLowFrequencyField + (environmentFallbackField + nearField) * indirectAoWeight);"));
            Assert.That(forwardShader, Does.Contain("float fallbackWeight = hybridDiffuse.environmentFallbackWeight;"));
            Assert.That(compositeShader, Does.Contain("vec3 receiverAlbedo = clamp(material.rgb"));
            Assert.That(compositeShader, Does.Contain("float diffuseWeight = 1.0 - clamp(material.a, 0.0, 1.0);"));
            Assert.That(compositeShader, Does.Contain("vec3 ComposeScreenSpaceContactGi(vec4 gi, vec4 material)"));
            Assert.That(compositeShader, Does.Not.Contain("float screenSpaceDetailWeight = smoothstep(0.08, 0.75, support);"));
            Assert.That(compositeShader, Does.Contain("return ssgiDiffuse * receiverAlbedo * diffuseWeight;"));
            Assert.That(compositeShader, Does.Not.Contain("return ssgiDiffuse * receiverAlbedo * diffuseWeight * screenSpaceDetailWeight;"));
            Assert.That(compositeShader, Does.Contain("vec3 indirect = ComposeScreenSpaceContactGi(gi, material);"));
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
            Assert.That(traceShader, Does.Contain("layout(set = 2, binding = 1, r16f) uniform writeonly image2D SsgiHitDistanceOutput;"));
            Assert.That(traceShader, Does.Contain("LowDiscrepancyBlueNoise(pixel, pc.FrameIndex);"));
            Assert.That(traceShader, Does.Contain("BLUE_NOISE_8X8"));
            Assert.That(traceShader, Does.Contain("RadicalInverseVdC"));
            Assert.That(traceShader, Does.Contain("FetchHiZDepth"));
            Assert.That(traceShader, Does.Contain("HIZ_DEPTH_TEXTURE_INDEX"));
            Assert.That(traceShader, Does.Contain("for (uint refineIndex = 0u; refineIndex < 5u; refineIndex++)"));
            Assert.That(traceShader, Does.Contain("imageStore(SsgiHitDistanceOutput, pixel, vec4(meanHitDistance, 0.0, 0.0, 0.0));"));
            Assert.That(traceShader, Does.Not.Contain("HDR_SCENE_COLOR_TEXTURE_INDEX"));
            Assert.That(forwardShader, Does.Contain("NJULF_SSGI_TRACE_OUTPUT"));
            Assert.That(forwardShader, Does.Contain("FORWARD_SSGI_TRACE_SOURCE_OUTPUT"));
            Assert.That(forwardShader, Does.Contain("layout(location = 1) out vec4 outSsgiTraceSource;"));
            Assert.That(forwardShader, Does.Contain("WriteSsgiTraceSource(vec4(clamp(directLighting + emissive, vec3(0.0), vec3(64.0)), 1.0));"));
            Assert.That(forwardPass, Does.Contain("_renderTargets.SsgiTraceSource.TransitionToColorAttachment(cmd);"));
            Assert.That(forwardPass, Does.Contain("ColorAttachmentCount = ssgiEnabled ? 2u : 1u"));
            Assert.That(tracePass, Does.Contain("_renderTargets.SsgiTraceSource.TransitionToShaderRead(cmd);"));
            Assert.That(meshPipeline, Does.Contain("\"forward_opaque.frag.spv\""));
            Assert.That(meshPipeline, Does.Contain("secondaryColorFormat: forwardSecondaryColorFormat"));
        });
    }

    [Test]
    public void DdgiOnlyForwardVariants_CompileWithoutSsgiTraceOutput()
    {
        string shaderProject = ReadRepoText("Njulf.Shaders", "Njulf.Shaders.csproj");
        string forwardShader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string foliageShader = ReadRepoText("Njulf.Shaders", "foliage_forward.frag");
        string meshPipeline = ReadRepoText("Njulf.Rendering", "Pipeline", "PipelineObjects", "MeshPipeline.cs");
        string foliagePipeline = ReadRepoText("Njulf.Rendering", "Pipeline", "PipelineObjects", "FoliagePipeline.cs");
        string forwardPass = ReadRepoText("Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shaderProject, Does.Contain("-DNJULF_SSGI_TRACE_OUTPUT=1 -I&quot;$(MSBuildProjectDirectory)&quot; -o &quot;$(IntermediateOutputPath)Shaders\\forward_opaque.frag.spv"));
            Assert.That(shaderProject, Does.Contain("forward_opaque_ddgi.frag.spv"));
            Assert.That(shaderProject, Does.Contain("forward_opaque_simple_ddgi.frag.spv"));
            Assert.That(shaderProject, Does.Contain("forward_opaque_simple_full_input_ddgi.frag.spv"));
            Assert.That(shaderProject, Does.Contain("-DNJULF_SSGI_TRACE_OUTPUT=1 -I&quot;$(MSBuildProjectDirectory)&quot; -o &quot;$(IntermediateOutputPath)Shaders\\foliage_forward_ssgi.frag.spv"));
            Assert.That(shaderProject, Does.Contain("foliage_forward_ddgi.frag.spv"));
            Assert.That(shaderProject, Does.Not.Contain("-DNJULF_SSGI_TRACE_OUTPUT=1 -I&quot;$(MSBuildProjectDirectory)&quot; -o &quot;$(IntermediateOutputPath)Shaders\\forward_opaque_ddgi.frag.spv"));
            Assert.That(shaderProject, Does.Not.Contain("-DNJULF_SSGI_TRACE_OUTPUT=1 -I&quot;$(MSBuildProjectDirectory)&quot; -o &quot;$(IntermediateOutputPath)Shaders\\foliage_forward_ddgi.frag.spv"));
            Assert.That(shaderProject, Does.Not.Contain("-DFORWARD_SSGI_TRACE_SOURCE_OUTPUT=1"));
            Assert.That(forwardShader, Does.Contain("#define FORWARD_SSGI_TRACE_SOURCE_OUTPUT NJULF_SSGI_TRACE_OUTPUT"));
            Assert.That(forwardShader, Does.Contain("#if FORWARD_SSGI_TRACE_SOURCE_OUTPUT"));
            Assert.That(foliageShader, Does.Contain("#if NJULF_SSGI_TRACE_OUTPUT"));
            Assert.That(meshPipeline, Does.Contain("Settings.GlobalIllumination.EffectiveUseSsgi"));
            Assert.That(meshPipeline, Does.Contain("\"forward_opaque_ddgi.frag.spv\""));
            Assert.That(meshPipeline, Does.Contain("\"forward_opaque_simple_ddgi.frag.spv\""));
            Assert.That(meshPipeline, Does.Contain("\"forward_opaque_simple_full_input_ddgi.frag.spv\""));
            Assert.That(meshPipeline, Does.Contain("Format? forwardSecondaryColorFormat = ssgiEnabled ? colorFormat : null;"));
            Assert.That(foliagePipeline, Does.Contain("Settings.GlobalIllumination.EffectiveUseSsgi"));
            Assert.That(foliagePipeline, Does.Contain("\"foliage_forward_ssgi.frag.spv\""));
            Assert.That(foliagePipeline, Does.Contain("\"foliage_forward_ddgi.frag.spv\""));
            Assert.That(foliagePipeline, Does.Contain("Format? foliageSecondaryColorFormat = ssgiEnabled ? colorFormat : null;"));
            Assert.That(foliagePipeline, Does.Contain("secondaryColorFormat: foliageSecondaryColorFormat"));
            Assert.That(forwardPass, Does.Contain("if (ssgiEnabled)"));
            Assert.That(forwardPass, Does.Contain("ColorAttachmentCount = ssgiEnabled ? 2u : 1u"));
        });
    }

    [Test]
    public void DdgiOnlyForwardSpirv_DoesNotContainSsgiTraceOutput()
    {
        string[] ddgiOnlyShaders =
        [
            "forward_opaque_ddgi.frag",
            "forward_opaque_simple_ddgi.frag",
            "forward_opaque_simple_full_input_ddgi.frag",
            "foliage_forward_ddgi.frag"
        ];

        foreach (string shaderName in ddgiOnlyShaders)
        {
            string spirvText = Encoding.ASCII.GetString(ReadEmbeddedShaderBytes(shaderName));
            Assert.That(spirvText, Does.Not.Contain("outSsgiTraceSource"), shaderName);
        }

        string ssgiFoliageSpirvText = Encoding.ASCII.GetString(ReadEmbeddedShaderBytes("foliage_forward_ssgi.frag"));
        Assert.That(ssgiFoliageSpirvText, Does.Contain("outSsgiTraceSource"));
    }

    [Test]
    public void ForwardShader_SeparatesVisibleColorFromSsgiTraceSource()
    {
        string forwardShader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string writeForwardColor = ExtractFunction(forwardShader, "void WriteForwardColor");
        string writeSsgiTraceSource = ExtractFunction(forwardShader, "void WriteSsgiTraceSource");
        int defaultTraceSource = forwardShader.IndexOf(
            "WriteSsgiTraceSource(vec4(0.0, 0.0, 0.0, 1.0));",
            StringComparison.Ordinal);
        int materialDebugBranch = forwardShader.IndexOf("if (IsMaterialDebugView(debugViewMode))", StringComparison.Ordinal);
        int canonicalTraceSource = forwardShader.IndexOf(
            "WriteSsgiTraceSource(vec4(clamp(directLighting + emissive, vec3(0.0), vec3(64.0)), 1.0));",
            StringComparison.Ordinal);
        int firstGiDebugReturn = forwardShader.IndexOf(
            "if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT)",
            StringComparison.Ordinal);
        int finalForwardColor = forwardShader.LastIndexOf(
            "WriteForwardColor(vec4(color, alphaMode > 0.5 && alphaMode < 1.5 ? 1.0 : outputAlpha));",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(writeForwardColor, Does.Not.Contain("outSsgiTraceSource"));
            Assert.That(writeSsgiTraceSource, Does.Contain("outSsgiTraceSource = color;"));
            Assert.That(defaultTraceSource, Is.GreaterThanOrEqualTo(0));
            Assert.That(materialDebugBranch, Is.GreaterThan(defaultTraceSource));
            Assert.That(canonicalTraceSource, Is.GreaterThanOrEqualTo(0));
            Assert.That(firstGiDebugReturn, Is.GreaterThan(canonicalTraceSource));
            Assert.That(finalForwardColor, Is.GreaterThan(canonicalTraceSource));
        });
    }

    [Test]
    public void FoliageForwardShader_SeparatesVisibleColorFromSsgiTraceSource()
    {
        string foliageShader = ReadRepoText("Njulf.Shaders", "foliage_forward.frag");
        string writeForwardColor = ExtractFunction(foliageShader, "void WriteFoliageForwardColor");
        string writeSsgiTraceSource = ExtractFunction(foliageShader, "void WriteFoliageSsgiTraceSource");

        Assert.Multiple(() =>
        {
            Assert.That(writeForwardColor, Does.Not.Contain("outSsgiTraceSource"));
            Assert.That(writeSsgiTraceSource, Does.Contain("outSsgiTraceSource = color;"));
            Assert.That(writeSsgiTraceSource, Does.Contain("#if NJULF_SSGI_TRACE_OUTPUT"));
            Assert.That(foliageShader, Does.Contain("WriteFoliageSsgiTraceSource(vec4(0.0, 0.0, 0.0, 1.0));"));
            Assert.That(foliageShader, Does.Contain("WriteFoliageSsgiTraceSource(vec4(clamp(foliageLighting, vec3(0.0), vec3(64.0)), 1.0));"));
        });
    }

    [Test]
    public void CommonShader_ProvidesDdgiAmbientSamplerForAlphaDomains()
    {
        string commonShader = ReadRepoText("Njulf.Shaders", "common.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(commonShader, Does.Contain("vec3 SampleDdgiAmbientDiffuse"));
            Assert.That(commonShader, Does.Contain("DDGI_AMBIENT_VOLUME_KIND_AUTHORED"));
            Assert.That(commonShader, Does.Contain("float normalBias;"));
            Assert.That(commonShader, Does.Contain("info.normalBias = max(biasAndCountZ.x, 0.0);"));
            Assert.That(commonShader, Does.Contain("vec3 DdgiAmbientSurfaceProbeSamplePosition(DdgiAmbientVolumeInfo info, vec3 worldPosition, vec3 normal)"));
            Assert.That(commonShader, Does.Contain("vec3 probeSamplePosition = DdgiAmbientSurfaceProbeSamplePosition(info, worldPosition, normal);"));
            Assert.That(commonShader, Does.Contain("vec3 selectedProbeSamplePosition = worldPosition;"));
            Assert.That(commonShader, Does.Contain("uint probeIndex = DdgiAmbientNearestProbeIndex(info, selectedProbeSamplePosition);"));
            Assert.That(commonShader, Does.Contain("for (uint pass = 0u; pass < 2u && remainingCoverage > 0.001; pass++)"));
            Assert.That(commonShader, Does.Contain("uint volumeLimit = min(min(volumeCount, maxVolumeSamples), 16u);"));
        });
    }

    [Test]
    public void ForwardShader_SamplesAuthoredDdgiVolumesBeforeClipmaps()
    {
        string forwardShader = ReadRepoText("Njulf.Shaders", "forward.frag");

        Assert.Multiple(() =>
        {
            Assert.That(forwardShader, Does.Contain("bool sampleAuthored = pass == 0u;"));
            Assert.That(forwardShader, Does.Contain("bool isAuthored = info.kind == DDGI_VOLUME_KIND_AUTHORED;"));
            Assert.That(forwardShader, Does.Contain("if (isAuthored != sampleAuthored)"));
            Assert.That(forwardShader, Does.Not.Contain("mix(1.0, 1.25, authoredPriority)"));
        });
    }

    [Test]
    public void FoliageForwardShader_ReceivesDdgiAmbient()
    {
        string foliageShader = ReadRepoText("Njulf.Shaders", "foliage_forward.frag");
        string grassMeshShader = ReadRepoText("Njulf.Shaders", "foliage_grass.mesh");
        string authoredMeshShader = ReadRepoText("Njulf.Shaders", "foliage_mesh.mesh");

        Assert.Multiple(() =>
        {
            Assert.That(grassMeshShader, Does.Contain("vec4 clusterDdgiIrradianceCoverage = SampleDdgiAmbientIrradiance("));
            Assert.That(authoredMeshShader, Does.Contain("vec4 meshletDdgiIrradianceCoverage = SampleDdgiAmbientIrradiance("));
            Assert.That(foliageShader, Does.Contain("layout(location = 9) flat in vec4 fragDdgiIrradianceCoverage;"));
            Assert.That(foliageShader, Does.Contain("vec3 ddgiIndirect = fragDdgiIrradianceCoverage.rgb * (baseColor / 3.14159265359) * fragDdgiIrradianceCoverage.a;"));
            Assert.That(foliageShader, Does.Contain("vec3 foliageLighting = clamp(foliageDirectLighting + ddgiIndirect, vec3(0.0), vec3(64.0));"));
            Assert.That(foliageShader, Does.Not.Contain("SampleDdgiAmbientDiffuse(fragWorldPosition"));
        });
    }

    [Test]
    public void ParticleShader_ReceivesDdgiAmbientForNonEmissiveParticles()
    {
        string particleVertex = ReadRepoText("Njulf.Shaders", "particle.vert");
        string particleFragment = ReadRepoText("Njulf.Shaders", "particle.frag");

        Assert.Multiple(() =>
        {
            Assert.That(particleVertex, Does.Contain("layout(location = 8) out vec3 outWorldPosition;"));
            Assert.That(particleVertex, Does.Contain("layout(location = 9) flat out vec3 outDdgiAmbient;"));
            Assert.That(particleVertex, Does.Contain("outDdgiAmbient = SampleDdgiAmbientDiffuse(center, particleDdgiNormal, particleAlbedo, 0.75, 4u);"));
            Assert.That(particleFragment, Does.Contain("layout(location = 8) in vec3 inWorldPosition;"));
            Assert.That(particleFragment, Does.Contain("layout(location = 9) flat in vec3 inDdgiAmbient;"));
            Assert.That(particleFragment, Does.Contain("float nonEmissiveWeight = clamp(1.0 - max(emissiveStrength - 1.0, 0.0), 0.0, 1.0);"));
            Assert.That(particleFragment, Does.Contain("hdr += inDdgiAmbient * nonEmissiveWeight;"));
            Assert.That(particleFragment, Does.Not.Contain("SampleDdgiAmbientDiffuse(inWorldPosition"));
        });
    }

    [Test]
    public void FogShader_UsesCoarseDdgiAmbient()
    {
        string fogShader = ReadRepoText("Njulf.Shaders", "fog.comp");

        Assert.Multiple(() =>
        {
            Assert.That(fogShader, Does.Contain("vec3 ResolveDdgiFogAmbient(vec3 cameraPosition, vec3 worldPosition, vec3 viewDirection, float fogFactor)"));
            Assert.That(fogShader, Does.Contain("vec4 irradiance = SampleDdgiAmbientIrradiance(samplePosition, ambientNormal, 6u);"));
            Assert.That(fogShader, Does.Contain("vec3 ddgiFogAmbient = ResolveDdgiFogAmbient(cameraPosition, worldPosition, viewDirection, fogFactor);"));
            Assert.That(fogShader, Does.Contain("vec3 fogRadiance = fogColor + inscattering + ddgiFogAmbient;"));
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
            Assert.That(shader, Does.Contain("layout(set = 2, binding = 3, rg16f) uniform writeonly image2D SsgiMomentsOutput;"));
            Assert.That(shader, Does.Contain("layout(set = 2, binding = 4, r16f) uniform writeonly image2D SsgiHistoryLengthOutput;"));
            Assert.That(shader, Does.Contain("SSGI_PREVIOUS_DEPTH_TEXTURE_INDEX"));
            Assert.That(shader, Does.Contain("SSGI_PREVIOUS_NORMAL_TEXTURE_INDEX"));
            Assert.That(shader, Does.Contain("SSGI_MOMENTS_TEXTURE_INDEX"));
            Assert.That(shader, Does.Contain("SSGI_HISTORY_LENGTH_TEXTURE_INDEX"));
            Assert.That(shader, Does.Contain("vec4 FetchHistoryPixel(ivec2 pixel)"));
            Assert.That(shader, Does.Contain("vec2 FetchMomentsPixel(ivec2 pixel)"));
            Assert.That(shader, Does.Contain("float FetchHistoryLengthPixel(ivec2 pixel)"));
            Assert.That(shader, Does.Contain("float FetchPreviousDepthPixel(ivec2 pixel)"));
            Assert.That(shader, Does.Contain("vec4 FetchPreviousNormalPixel(ivec2 pixel)"));
            Assert.That(shader, Does.Contain("float PackMaterialSignature(float metallic)"));
            Assert.That(shader, Does.Contain("bool FindBestPreviousSurface("));
            Assert.That(shader, Does.Contain("bool previousSurfaceValid = FindBestPreviousSurface("));
            Assert.That(shader, Does.Contain("!previousSurfaceValid"));
            Assert.That(shader, Does.Contain("vec4 history = previousSurfaceValid ? FetchHistoryPixel(historyPixel) : vec4(0.0);"));
            Assert.That(shader, Does.Contain("vec2 historyMoments = previousSurfaceValid ? FetchMomentsPixel(historyPixel) : vec2(0.0);"));
            Assert.That(shader, Does.Contain("float previousHistoryLength = previousSurfaceValid ? FetchHistoryLengthPixel(historyPixel) : 0.0;"));
            Assert.That(shader, Does.Contain("materialDelta > materialThreshold"));
            Assert.That(shader, Does.Contain("imageStore(SsgiDepthHistoryOutput, pixel, vec4(surfaceValid ? currentViewDepth : 0.0"));
            Assert.That(shader, Does.Contain("imageStore(SsgiNormalHistoryOutput, pixel, vec4(currentNormalSample.xyz"));
            Assert.That(shader, Does.Contain("imageStore(SsgiMomentsOutput, pixel, vec4(resolvedMoments, 0.0, 0.0));"));
            Assert.That(shader, Does.Contain("imageStore(SsgiHistoryLengthOutput, pixel, vec4(historyLength, 0.0, 0.0, 0.0));"));
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
            Assert.That(denoiseShader, Does.Contain("vec2 FetchMomentsPixel(ivec2 pixel)"));
            Assert.That(denoiseShader, Does.Contain("float FetchHistoryLengthPixel(ivec2 pixel)"));
            Assert.That(denoiseShader, Does.Contain("SSGI_MOMENTS_TEXTURE_INDEX"));
            Assert.That(denoiseShader, Does.Contain("SSGI_HISTORY_LENGTH_TEXTURE_INDEX"));
            Assert.That(denoiseShader, Does.Contain("SSGI_HIT_DISTANCE_TEXTURE_INDEX"));
            Assert.That(denoiseShader, Does.Contain("float FetchHitDistancePixel(ivec2 pixel)"));
            Assert.That(denoiseShader, Does.Contain("float hitDistanceWeight"));
            Assert.That(denoiseShader, Does.Contain("uint iterations = pc.DenoiserEnabled == 0u"));
            Assert.That(denoiseShader, Does.Contain("const float atrousWeights[5]"));
            Assert.That(denoiseShader, Does.Contain("for (uint iteration = 0u; iteration < 4u; iteration++)"));
            Assert.That(denoiseShader, Does.Contain("float bilateralWeight = waveletWeight * depthWeight * normalWeight * hitDistanceWeight * lumaWeight * historyWeight;"));
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
            Assert.That(temporalShader, Does.Contain("float candidateViewDepth = candidateDepth;"));
            Assert.That(temporalShader, Does.Contain("float candidateDepthDelta = abs(currentViewDepth - candidateViewDepth);"));
            Assert.That(denoisePass, Does.Contain("InverseProjectionMatrix = sceneData.InverseProjectionMatrix"));
            Assert.That(denoisePass, Does.Contain("TemporalEnabled = gi.TemporalEnabled ? 1u : 0u"));
            Assert.That(temporalPass, Does.Contain("InverseProjectionMatrix = sceneData.InverseProjectionMatrix"));
            Assert.That(temporalPass, Does.Contain("sceneData.HiZPolicyCameraCut != 0"));
            Assert.That(temporalPass, Does.Contain("HasProjectionChanged(sceneData.ProjectionMatrix)"));
            Assert.That(temporalPass, Does.Contain("HasCameraTeleported(sceneData.CameraPosition, gi.SsgiMaxDistance)"));
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
        string compactionShader = ReadRepoText("Njulf.Shaders", "scene_opaque_compact.comp");
        string forwardVisibilityShader = ReadRepoText("Njulf.Shaders", "forward_visibility_compact.comp");
        string compactionPass = ReadRepoText("Njulf.Rendering", "Pipeline", "SceneOpaqueCompactionPass.cs");
        string forwardVisibilityPass = ReadRepoText("Njulf.Rendering", "Pipeline", "ForwardVisibilityCompactionPass.cs");
        string pipeline = ReadRepoText("Njulf.Rendering", "Pipeline", "PipelineObjects", "MeshPipeline.cs");
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
        string productionDeclaration = ReadRepoText("Njulf.Rendering", "Pipeline", "ProductionRenderPipelineDeclaration.cs");

        Assert.Multiple(() =>
        {
            Assert.That(taskShader, Does.Contain("vec2 uvPadding = 4.0 / max(pc.Push.ScreenDimensions, vec2(1.0));"));
            Assert.That(taskShader, Does.Contain("float mipFloat = ceil(log2(max(extentPixels.x, extentPixels.y)));"));
            Assert.That(taskShader, Does.Not.Contain("vec2 uvPadding = 2.0 / max(pc.Push.ScreenDimensions, vec2(1.0));"));
            Assert.That(taskShader, Does.Not.Contain("float mipFloat = floor(log2(max(extentPixels.x, extentPixels.y)));"));
            Assert.That(bindlessHeap, Does.Contain("private void CreateHiZSampler()"));
            Assert.That(bindlessHeap, Does.Contain("MagFilter = Filter.Nearest"));
            Assert.That(bindlessHeap, Does.Contain("MinFilter = Filter.Nearest"));
            Assert.That(compactionShader, Does.Contain("SCENE_SUBMISSION_COUNTER_OPAQUE_HIZ_TESTED"));
            Assert.That(compactionShader, Does.Contain("SCENE_SUBMISSION_COUNTER_OPAQUE_HIZ_REJECTED"));
            Assert.That(compactionShader, Does.Contain("MeshletOccludedByHiZ"));
            Assert.That(compactionShader, Does.Contain("ReadMeshletTaskPreviousHiZViewProjectionMatrix"));
            Assert.That(compactionShader, Does.Contain("ReadMeshletTaskPreviousHiZFrameValid"));
            Assert.That(compactionShader, Does.Contain("pc.Push.PreviousHiZFrameValid"));
            Assert.That(compactionShader, Does.Contain("CanHiZTestMeshletDrawCommand"));
            Assert.That(compactionShader, Does.Contain("material.NormalScaleBias.y < 1.5"));
            Assert.That(compactionShader, Does.Contain("float(pc.Push.PreviousFrameUvPaddingPixels) / screenDimensions"));
            Assert.That(compactionShader, Does.Not.Contain("vec2 uvPadding = 8.0 / screenDimensions;"));
            Assert.That(compactionShader, Does.Contain("textureLod(BindlessTextures"));
            Assert.That(compactionShader, Does.Not.Contain("mat4 viewProjection = ReadMeshletTaskViewProjectionMatrix(pc.Push.CurrentFrameIndex);"));
            Assert.That(forwardVisibilityShader, Does.Contain("GPUForwardVisibilityCompactionPushConstants"));
            Assert.That(forwardVisibilityShader, Does.Contain("ReadMeshletTaskViewProjectionMatrix(pc.Push.CurrentFrameIndex)"));
            Assert.That(forwardVisibilityShader, Does.Contain("ReadMeshletTaskInverseViewMatrix(pc.Push.CurrentFrameIndex)"));
            Assert.That(forwardVisibilityShader, Does.Contain("FORWARD_VISIBILITY_COUNTER_HIZ_TESTED"));
            Assert.That(forwardVisibilityShader, Does.Contain("FORWARD_VISIBILITY_COUNTER_HIZ_REJECTED"));
            Assert.That(forwardVisibilityShader, Does.Contain("vec2 uvPadding = 4.0 / screenDimensions;"));
            Assert.That(forwardVisibilityShader, Does.Contain("vec2 uvCenter = (minUv + maxUv) * 0.5;"));
            Assert.That(forwardVisibilityShader, Does.Contain("CURRENT_FRAME_HIZ_MIN_SELF_OCCLUSION_BIAS"));
            Assert.That(forwardVisibilityShader, Does.Contain("float occlusionBias = max(pc.Push.OcclusionBias, CURRENT_FRAME_HIZ_MIN_SELF_OCCLUSION_BIAS);"));
            Assert.That(forwardVisibilityPass, Does.Contain("ForwardVisibleSimpleOpaqueMeshletDrawBufferBase"));
            Assert.That(compactionPass, Does.Contain("_bindlessHeap.TextureSamplerSet"));
            Assert.That(compactionPass, Does.Contain("PreviousFrameUvPaddingPixels"));
            Assert.That(compactionPass, Does.Contain("PreviousHiZFrameValid = sceneData.PreviousHiZFrameValid ? 1u : 0u"));
            Assert.That(forwardVisibilityPass, Does.Contain("ForwardVisibilityCompactionPass"));
            Assert.That(forwardVisibilityPass, Does.Contain("ForwardVisibilityCounterBufferBase"));
            Assert.That(forwardVisibilityPass, Does.Contain("ForwardVisibleFullOpaqueMeshletDrawBufferBase"));
            Assert.That(pipeline, Does.Contain("_bindlessHeap.TextureSamplerSetLayout"));
            Assert.That(pipeline, Does.Contain("forward_visibility_compact.comp.spv"));
            Assert.That(renderer, Does.Contain("ResolveHiZConsumers"));
            Assert.That(renderer, Does.Contain("ForwardVisibilityCurrentHiZ"));
            Assert.That(renderer, Does.Contain("SceneSubmissionPreviousHiZ"));
            Assert.That(renderer, Does.Contain("LegacyForwardTask"));
            Assert.That(renderer, Does.Contain("Foliage"));
            Assert.That(renderer, Does.Contain("Ssgi"));
            Assert.That(renderer, Does.Contain("ResolveCompletedHiZCounters"));
            Assert.That(renderer, Does.Contain("HiZCounterSource.ForwardVisibilityCompaction"));
            Assert.That(renderer, Does.Contain("HiZCounterSource.SceneSubmissionCompaction"));
            Assert.That(renderer, Does.Contain("UpdateHiZFallbackDiagnostics"));
            Assert.That(renderer, Does.Contain("HiZFallbackPaths.CurrentFrameForwardVisibility"));
            Assert.That(renderer, Does.Contain("HiZFallbackPaths.PreviousFrameSceneSubmission"));
            Assert.That(renderer, Does.Contain("HiZFallbackPaths.CompactedNoHiZ"));
            Assert.That(renderer, Does.Contain("HiZFallbackPaths.LegacyForward"));
            Assert.That(renderer, Does.Contain("EnableHiZOcclusion && Settings.HiZOcclusion.Enabled"));
            Assert.That(renderer, Does.Contain("EnableAdaptiveHiZOcclusion && Settings.HiZOcclusion.AdaptiveEnabled"));
            Assert.That(renderer, Does.Contain("Settings.HiZOcclusion.ForceOn"));
            Assert.That(renderer, Does.Contain("Settings.HiZOcclusion.ForceProbe"));
            Assert.That(renderer, Does.Contain("Settings.HiZOcclusion.ValidateAgainstLegacyPath"));
            Assert.That(renderer, Does.Contain("sceneData.PreviousHiZFrameValid = previousHiZHistoryValid && !_previousHiZCameraMotionSuppressedThisFrame"));
            Assert.That(renderer, Does.Contain("Settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled"));
            Assert.That(renderer, Does.Contain("_completedSceneSubmissionCounters.HiZTestedCount"));
            Assert.That(productionDeclaration, Does.Contain("ReadComputeSampled(RenderGraphResourceId.HiZPyramid)"));
            Assert.That(productionDeclaration, Does.Contain("ForwardVisibilityCompactionPass"));
            Assert.That(productionDeclaration, Does.Contain("WriteComputeBuffer(RenderGraphResourceId.ForwardVisibilityBuffers)"));
        });
    }

    [Test]
    public void HiZBuildPass_CachesMipMetadataAndBatchesFinalLayoutTransition()
    {
        string source = ReadRepoText("Njulf.Rendering", "Pipeline", "HiZBuildPass.cs");
        string sceneData = ReadRepoText("Njulf.Rendering", "Data", "SceneRenderingData.cs");
        string diagnostics = ReadRepoText("Njulf.Rendering", "Data", "RendererDiagnostics.cs");
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("private MipRecordMetadata[] _mipMetadata"));
            Assert.That(source, Does.Contain("private void RebuildMipMetadata()"));
            Assert.That(source, Does.Contain("? _renderTargets.SceneDepth.Extent"));
            Assert.That(source, Does.Contain("DescriptorSet DescriptorSet"));
            Assert.That(source, Does.Contain("GPUHiZBuildPushConstants PushConstants"));
            Assert.That(source, Does.Contain("uint DispatchGroupCountX"));
            Assert.That(source, Does.Contain("uint DispatchGroupCountY"));
            Assert.That(source, Does.Contain("ImageLayout sourceLayout = mip == 0"));
            Assert.That(source, Does.Contain(": ImageLayout.General;"));
            Assert.That(source, Does.Contain("AddMipWriteToNextReadDependency"));
            Assert.That(source, Does.Contain("ImageLayout.General,"));
            Assert.That(source, Does.Contain("TransitionPyramidToShaderRead(cmd);"));
            Assert.That(source, Does.Contain("LevelCount = _pyramid.MipLevels"));
            Assert.That(source, Does.Not.Contain("TransitionMipToShaderRead"));
            Assert.That(source, Does.Contain("CpuHiZDepthTransitionMicroseconds"));
            Assert.That(source, Does.Contain("CpuHiZPyramidTransitionMicroseconds"));
            Assert.That(source, Does.Contain("CpuHiZDescriptorBindMicroseconds"));
            Assert.That(source, Does.Contain("CpuHiZPushDispatchMicroseconds"));
            Assert.That(source, Does.Contain("CpuHiZFinalBarrierMicroseconds"));
            Assert.That(sceneData, Does.Contain("CpuHiZDepthTransitionMicroseconds"));
            Assert.That(diagnostics, Does.Contain("CpuHiZDescriptorBindMicroseconds"));
            Assert.That(renderer, Does.Contain("CpuHiZFinalBarrierMicroseconds = sceneData.CpuHiZFinalBarrierMicroseconds"));
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
            Assert.That(source, Does.Contain("DrawCompactedForwardBucketsDirect"));
            Assert.That(source, Does.Contain("DrawCompactedForwardBucketsIndirect"));
            Assert.That(source, Does.Contain("SceneSimpleOpaqueCompactedMeshletDrawBufferBase"));
            Assert.That(source, Does.Contain("SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase"));
            Assert.That(source, Does.Contain("SceneFullOpaqueCompactedMeshletDrawBufferBase"));
            Assert.That(source, Does.Contain("ResolveOpaqueVariantSelection"));
            Assert.That(pipeline, Does.Contain("ForwardFullMaterialPipeline"));
            Assert.That(pipeline, Does.Contain("ForwardSimpleGlobalIblPipeline"));
            Assert.That(pipeline, Does.Contain("ForwardCompactedSimpleGlobalIblPipeline"));
            Assert.That(pipeline, Does.Contain("ForwardCompactedSimpleFullInputGlobalIblPipeline"));
            Assert.That(pipeline, Does.Contain("forward_opaque.frag.spv"));
            Assert.That(pipeline, Does.Contain("forward_opaque_simple_full_input.frag.spv"));
            Assert.That(taskShader, Does.Contain("SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"));
            Assert.That(taskShader, Does.Contain("PACKED_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"));
            Assert.That(taskShader, Does.Contain("SCENE_SIMPLE_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX"));
            Assert.That(taskShader, Does.Contain("SCENE_SIMPLE_NORMAL_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX"));
            Assert.That(taskShader, Does.Contain("SCENE_FULL_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX"));
            Assert.That(taskShader, Does.Contain("SceneCompactedEmittedCounterWord"));
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

    private static byte[] ReadEmbeddedShaderBytes(string shaderName)
    {
        string resourceName = $"Njulf.Shaders.{shaderName}";
        using Stream? stream = typeof(ShaderLibrary).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new AssertionException($"Missing shader resource '{resourceName}'.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string ExtractFunction(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
            throw new AssertionException($"Could not find function signature '{signature}'.");

        int openBrace = source.IndexOf('{', signatureIndex);
        if (openBrace < 0)
            throw new AssertionException($"Could not find function body for '{signature}'.");

        int depth = 0;
        for (int index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
                depth--;

            if (depth == 0)
                return source[signatureIndex..(index + 1)];
        }

        throw new AssertionException($"Function '{signature}' has an unterminated body.");
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
