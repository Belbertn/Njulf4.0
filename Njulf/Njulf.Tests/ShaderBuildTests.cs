using System.Buffers.Binary;
using System.Text;
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
            Assert.That(shader, Does.Contain("float coveredWeight = 0.0;"));
            Assert.That(shader, Does.Contain("float supportedWeight = 0.0;"));
            Assert.That(shader, Does.Contain("float supportWeight = expectedContributionWeight * probeActive * irradianceConfidence * qualityConfidence;"));
            Assert.That(shader, Does.Contain("coveredWeight += expectedContributionWeight;"));
            Assert.That(shader, Does.Contain("if (probeActive <= 0.001)"));
            Assert.That(shader, Does.Contain("supportedWeight += supportWeight;"));
            Assert.That(shader, Does.Contain("float visibilityTransport = EvaluateDdgiVisibility("));
            Assert.That(shader, Does.Contain("float probeVisibilityConfidence = DdgiVisibilityConfidence(visibilityTransport);"));
            Assert.That(shader, Does.Contain("float visibilityWeightedContribution = supportWeight * visibilityTransport;"));
            Assert.That(shader, Does.Contain("accumulated += clamp(probeIrradiance, vec3(0.0), vec3(64.0)) * supportWeight;"));
            Assert.That(shader, Does.Contain("totalWeight += supportWeight;"));
            Assert.That(shader, Does.Contain("float minVariance = max(0.005, minProbeSpacing * minProbeSpacing * 0.0025);"));
            Assert.That(shader, Does.Contain("variance = max(mean2 - mean * mean, minVariance);"));
            Assert.That(shader, Does.Contain("float grazingRejection = smoothstep(-0.15, 0.25, alignment);"));
            Assert.That(shader, Does.Contain("float normalWeight = normalHemisphereWeight * normalHemisphereWeight * grazingRejection;"));
            Assert.That(shader, Does.Contain("float supportCoverage = clamp(coveredWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * clamp(volumeEdgeFade, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("result.weight = clamp(totalWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * clamp(volumeEdgeFade, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("uint DdgiCacheGeneration()"));
            Assert.That(shader, Does.Contain("uint DdgiCacheLastUpdatedFrameSerial()"));
            Assert.That(shader, Does.Contain("uint DdgiCacheWarmupState()"));
            Assert.That(shader, Does.Contain("return cacheGeneration == 0u || cacheWarmupState == DDGI_WARMUP_STATE_COLD_START;"));
            Assert.That(shader, Does.Contain("if (DdgiCacheCold())"));
            Assert.That(shader, Does.Contain("dataConfidence = 0.0;"));
            Assert.That(shader, Does.Contain("float blendedVisibleSupport = 0.0;"));
            Assert.That(shader, Does.Contain("blendedVisibleSupport += candidate.weight * blendWeight;"));
            Assert.That(shader, Does.Contain("result.weight = clamp(blendedVisibleSupport * invCoverage, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("result.coverage = supportCoverage;"));
            Assert.That(shader, Does.Not.Contain("result.coverage = clamp(totalWeight / max(expectedWeight, 0.000001), 0.0, 1.0) * clamp(volumeEdgeFade, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("HybridDiffuseGiResult ComposeHybridDiffuseGi("));
            Assert.That(shader, Does.Contain("float coverage = clamp(ddgi.coverage, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float dataConfidence = clamp(ddgi.weight, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityConfidence = clamp(ddgi.visibility, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityTransport = clamp(ddgi.leakClamp, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float ddgiLowFrequencyCoverage = clamp(ddgi.coverage * ddgi.activeProbe, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float thinWallLeakClampStrength = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 14u), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float thinWallProxyThickness = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 15u), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float leakAttenuation = clamp(mix(1.0, visibilityTransport, leakStrength), 0.05, 1.0);"));
            Assert.That(shader, Does.Contain("float effectiveDdgiWeight = clamp(coverage * smoothstep(0.02, 0.25, dataConfidence), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float ddgiTrust = clamp(effectiveDdgiWeight * leakAttenuation, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 debugSuppression = vec3("));
            Assert.That(shader, Does.Contain("visibilityConfidence,"));
            Assert.That(shader, Does.Contain("leakAttenuation,"));
            Assert.That(shader, Does.Contain("dataConfidence);"));
            Assert.That(shader, Does.Contain("float environmentFallbackWeight = clamp(environmentTrust * environmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(shader, Does.Not.Contain("float environmentFallbackWeight = clamp((1.0 - ddgiLowFrequencyCoverage) * indirectAo * environmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(shader, Does.Not.Contain("float environmentFallbackWeight = clamp((1.0 - effectiveDdgiWeight) * indirectAo * environmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(shader, Does.Not.Contain("float effectiveDdgiWeight = clamp(ddgiLowFrequencyCoverage * ddgiVisibleSupport * (1.0 - ddgiContactSuppression), 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float ddgiUsableCoverage = clamp(ddgiLowFrequencyCoverage * (1.0 - ddgiContactSuppression), 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("float ddgiFallbackCoverage = clamp(ddgiUsableCoverage * ddgiVisibleSupport, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 ddgiLowFrequencyField = ddgiDiffuse * ddgiTrust;"));
            Assert.That(shader, Does.Contain("vec3 environmentFallbackField = diffuseIbl * environmentFallbackWeight;"));
            Assert.That(shader, Does.Contain("if (dataConfidence <= 0.000001)"));
            Assert.That(shader, Does.Contain("result.diffuse = clamp(environmentFallbackField * indirectAoWeight, vec3(0.0), vec3(64.0));"));
            Assert.That(shader, Does.Contain("result.diffuse = clamp((environmentFallbackField + ddgiLowFrequencyField + nearField) * indirectAoWeight, vec3(0.0), vec3(64.0));"));
            Assert.That(shader, Does.Contain("float ddgiEnvironmentFallbackIntensity = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 13u), 0.0, 4.0);"));
            Assert.That(shader, Does.Contain("ComposeHybridDiffuseGi(diffuseIbl, ddgiDiffuse, ddgiSample, indirectAo, ddgiEnvironmentFallbackIntensity, debugViewMode)"));
            Assert.That(shader, Does.Contain("bool DdgiDebugBypassFinalSuppression(uint debugViewMode)"));
            Assert.That(shader, Does.Contain("bool DdgiDebugBypassFinalSuppression()"));
            Assert.That(shader, Does.Contain("return debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE;"));
            Assert.That(shader, Does.Contain("result.diffuse = clamp(ddgiDiffuse, vec3(0.0), vec3(64.0));"));
            Assert.That(shader, Does.Contain("vec3 nearField = vec3(0.0);"));
            Assert.That(shader, Does.Not.Contain("ssgiConfidence * 0.25"));
            Assert.That(shader, Does.Not.Contain("ssgiConfidence * 0.75"));
            Assert.That(shader, Does.Not.Contain("vec3 worldField = ddgiDiffuse * (1.0 - nearContactSuppression);"));
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
            Assert.That(shader, Does.Contain("float finalIntensity = DdgiRawAtlasRadianceConventionEnabled()"));
            Assert.That(shader, Does.Contain("? globalIntensity * info.volumeIntensity"));
            Assert.That(shader, Does.Contain("bool ReadDdgiVolumeSampleInfo("));
            Assert.That(shader, Does.Contain("DdgiSampleResult SampleDdgiVolumeIrradiance("));
            Assert.That(shader, Does.Contain("DdgiSampleResult SampleDdgiGatherCandidates("));
            Assert.That(shader, Does.Contain("float primaryClipmapEdgeFade = -1.0;"));
            Assert.That(shader, Does.Contain("bool nearClipmapTransition = primaryClipmapEdgeFade >= 0.0 && primaryClipmapEdgeFade < 0.985;"));
            Assert.That(shader, Does.Contain("tile.blendWeights.z > 0.0001"));
            Assert.That(shader, Does.Contain("if (ReadDdgiGatherTile(tile) &&"));
            Assert.That(shader, Does.Contain("(tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)"));
            Assert.That(shader, Does.Contain("return SampleDdgiGatherCandidates(tile, volumeCount, worldPosition, normal, indirectAo, globalIntensity);"));
            Assert.That(shader, Does.Contain("if (DdgiExhaustiveGatherFallbackEnabled())"));
            Assert.That(shader, Does.Contain("return SampleDdgiIrradianceExhaustive(min(volumeCount, 16u), worldPosition, normal, indirectAo, globalIntensity);"));
            Assert.That(shader, Does.Not.Contain("if (tiledResult.coverage > 0.000001 || tiledResult.weight > 0.000001)"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT"));
            Assert.That(shader, Does.Contain("GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK"));
            Assert.That(shader, Does.Contain("uint DdgiProbeIndex(DdgiVolumeSampleInfo info, ivec3 probeCoord)"));
            Assert.That(shader, Does.Contain("return DdgiCalculatePhysicalProbeIndex("));
            Assert.That(shader, Does.Contain("probePosition = DdgiProbeWorldPosition(info, corner) + relocationAndClassification.xyz;"));
            Assert.That(shader, Does.Contain("if (DdgiDebugForceProbeActive())"));
            Assert.That(shader, Does.Contain("probeActive = 1.0;"));
            Assert.That(shader, Does.Contain("float blendWeight = clamp(candidateCoverage * remainingCoverage"));
            Assert.That(shader, Does.Contain("remainingCoverage = clamp(remainingCoverage - blendWeight, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("bool sampleAuthored = pass == 0u;"));
            Assert.That(shader, Does.Contain("bool isAuthored = info.kind == DDGI_VOLUME_KIND_AUTHORED;"));
            Assert.That(shader, Does.Contain("if (isAuthored != sampleAuthored)"));
            Assert.That(shader, Does.Contain("result.irradiance = clamp(blendedIrradiance * invCoverage, vec3(0.0), vec3(64.0));"));
            Assert.That(common, Does.Contain("DDGI_GATHER_TILE_BUFFER_INDEX"));
            Assert.That(shader, Does.Not.Contain("ReadDdgiContainingVolume("));
            Assert.That(shader, Does.Not.Contain("return firstProbe + probeCoord.x + probeCoord.y * probeCounts.x + probeCoord.z * probeCounts.x * probeCounts.y;"));
        });
    }

    [Test]
    public void DdgiUpdateShader_UsesStableVisibilityTexelJitterAndSolidAngleWeightsIrradiance()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("vec2 Hash22(uvec3 value)"));
            Assert.That(shader, Does.Contain("vec3 JitteredAtlasTexelDirection("));
            Assert.That(shader, Does.Contain("vec2 jitter = Hash22(uvec3(probeIndex, texel, safeTexels)) - vec2(0.5);"));
            Assert.That(shader, Does.Not.Contain("Hash22(uvec3(probeIndex, pc.FrameIndex, texel))"));
            Assert.That(shader, Does.Contain("vec2 uv = (vec2(texelCoord) + vec2(0.5) + jitter * 0.85) / float(safeTexels);"));
            Assert.That(shader, Does.Contain("solidAngle = OctahedralTexelSolidAngle(uv, safeTexels);"));
            Assert.That(shader, Does.Contain("SharedRayDirection[rayIndex] = vec4(result.direction, result.solidAngle);"));
            Assert.That(shader, Does.Contain("WriteVisibilityAtlasSample("));
            Assert.That(shader, Does.Contain("directionalTexel,"));
            Assert.That(shader, Does.Contain("float raySolidAngle = max(SharedRayDirection[rayIndex].w, 0.0);"));
            Assert.That(shader, Does.Contain("float weight = max(dot(rayDirection, texelDirection), 0.0) * raySolidAngle * rayIrradiance.w;"));
            Assert.That(shader, Does.Contain("float expectedWeight = PI;"));
            Assert.That(shader, Does.Contain("float sampleCoverageScale = float(directionalTexelCount) / max(float(sampleCount), 1.0);"));
            Assert.That(shader, Does.Contain("weightedRadiance *= sampleCoverageScale;"));
            Assert.That(shader, Does.Contain("weightSum *= sampleCoverageScale;"));
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
            Assert.That(shader, Does.Contain("float previousLuminance = dot(previousState.rgb, vec3(0.2126, 0.7152, 0.0722));"));
            Assert.That(shader, Does.Contain("float luminanceChange = abs(currentLuminance - previousLuminance) / max(max(previousLuminance, currentLuminance), 0.05);"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(visibility, clamp(luminanceChange, 0.0, 1.0), 1.0));"));
            Assert.That(shader, Does.Contain("float luminanceConfidence = 1.0 - luminanceChange * 0.45;"));
            Assert.That(shader, Does.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * (1.0 - missRatio * 0.5) * luminanceConfidence, 0.0, 1.0);"));
            Assert.That(shader, Does.Not.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(visibility, float(pc.FrameIndex), 1.0));"));
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
            Assert.That(shader, Does.Contain("localProbeIndex = DdgiCalculateLocalPhysicalProbeIndex("));
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
            Assert.That(shader, Does.Not.Contain("uint probeIndex = (pc.StartProbeIndex + updateIndex)"));
            Assert.That(shader, Does.Not.Contain("WriteStorageWord(pc.ProbeUpdateQueueBufferIndex, requestBase + 0u, probeIndex);"));
        });
    }

    [Test]
    public void DdgiUpdateShader_UsesVolumeRayCountsAndBoundedHitLightCap()
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
            Assert.That(shader, Does.Contain("const uint DDGI_LIGHT_SELECTION_MODE_BOUNDED_DIRECTIONAL_LOCAL = 1u;"));
            Assert.That(shader, Does.Contain("const uint DDGI_INVALID_LIGHT_INDEX = 0xffffffffu;"));
            Assert.That(shader, Does.Contain("bool TryReadSelectedDdgiDirectionalLight(out GPULight selectedLight)"));
            Assert.That(shader, Does.Contain("bool TryBuildSelectedDdgiLocalLightContribution("));
            Assert.That(shader, Does.Contain("vec3 EvaluateSelectedDdgiLight("));
            Assert.That(shader, Does.Contain("uint selectedLightCapacity = min(pc.MaxShadedLights, DDGI_MAX_SELECTED_HIT_LIGHTS);"));
            Assert.That(shader, Does.Contain("attenuation *= max(pc.SelectedLocalLightEnergyScale, 0.0);"));
            Assert.That(shader, Does.Contain("bool ShouldUseCompactDdgiMaterial(uint volumeCascadeIndex)"));
            Assert.That(shader, Does.Contain("vec3 ResolveCompactDdgiAlbedo(GPUMaterialData material)"));
            Assert.That(shader, Does.Contain("vec3 ResolveCompactDdgiEmissive(GPUMaterialData material)"));
            Assert.That(shader, Does.Contain("float ResolveDdgiMaterialTextureLod(GPUMaterialData material, uint volumeCascadeIndex)"));
            Assert.That(shader, Does.Contain("vec3 EvaluateSelectedDdgiEmissiveSourceAtHit("));
            Assert.That(shader, Does.Contain("GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(0u);"));
            Assert.That(shader, Does.Contain("uint raysPerProbe = clamp(uint(round(updateParams.x)), 1u, DDGI_MAX_RAYS_PER_PROBE);"));
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
            Assert.That(shader, Does.Contain("float hardInvalid = smoothstep(0.75, 0.95, invalidProbeScore);"));
            Assert.That(shader, Does.Contain("float softInvalid = smoothstep(0.35, 0.75, invalidProbeScore);"));
            Assert.That(shader, Does.Contain("float targetActiveProbe = classificationEnabled ? (1.0 - hardInvalid) : 1.0;"));
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
            Assert.That(shader, Does.Contain("? mix(previousRelocationAndClassification.xyz, relocation, relocationBlendAlpha)"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 8u, vec4(nearestHitDistance, missRatio, relocationEvidence, hitRatio));"));
            Assert.That(shader, Does.Not.Contain("float maxRelocationDistance = 0.4 * minProbeSpacing;"));
            Assert.That(shader, Does.Contain("float rayHitConfidence = clamp(hitRatio * (1.0 - backfaceRatio) * confidencePenalty, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float luminanceConfidence = 1.0 - luminanceChange * 0.45;"));
            Assert.That(shader, Does.Contain("float irradianceConfidence = clamp(activeProbe * confidencePenalty * (1.0 - missRatio * 0.5) * luminanceConfidence, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityConfidence = clamp((hitRatio + missRatio * 0.35) * (1.0 - closeRatio * 0.5) * confidencePenalty, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("vec3 qualityConfidence = vec3(rayHitConfidence, irradianceConfidence, visibilityConfidence);"));
            Assert.That(shader, Does.Not.Contain("classifiedActiveProbe"));
            Assert.That(shader, Does.Not.Contain("activeProbe * (1.0 - invalidProbeScore)"));
            Assert.That(shader, Does.Contain("vec3 blendedQualityConfidence = historyValid > 0.5"));
            Assert.That(shader, Does.Contain("float lastUpdateReason = float(ResolvePrimaryProbeUpdateReason(request.Flags));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(0.0));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 16u, vec4(0.0));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 8u, vec4(0.0));"));
            Assert.That(shader, Does.Contain("WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(blendedQualityConfidence, lastUpdateReason));"));
            Assert.That(shader, Does.Contain("WriteStorageWord(pc.ProbeStateBufferIndex, stateBase + 16u, pc.FrameSerial);"));
            Assert.That(forwardShader, Does.Contain("coveredWeight += expectedContributionWeight;"));
            Assert.That(forwardShader, Does.Contain("if (probeActive <= 0.001)"));
            Assert.That(forwardShader, Does.Contain("result.diffuse = clamp(environmentFallbackField * indirectAoWeight, vec3(0.0), vec3(64.0));"));
        });
    }

    [Test]
    public void DdgiUpdateShader_DirectBounceIsIndependentFromEnvironmentFallback()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ddgi_update_shared.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("float intensity = max(updateParams.z, 0.0);"));
            Assert.That(shader, Does.Contain("bool DdgiRawAtlasRadianceConventionEnabled()"));
            Assert.That(shader, Does.Contain("bool DdgiDebugForceProbeActive()"));
            Assert.That(shader, Does.Contain("DDGI_DEBUG_FORCE_PROBE_ACTIVE_FLAG"));
            Assert.That(shader, Does.Contain("if (DdgiDebugForceProbeActive())"));
            Assert.That(shader, Does.Contain("vec3 sampleIrradiance = DdgiRawAtlasRadianceConventionEnabled()"));
            Assert.That(shader, Does.Contain("? radiance"));
            Assert.That(shader, Does.Contain(": radiance * intensity;"));
            Assert.That(shader, Does.Contain("if (DdgiRawAtlasRadianceConventionEnabled())"));
            Assert.That(shader, Does.Contain("return clamp(rawIrradiance, vec3(0.0), vec3(64.0));"));
            Assert.That(shader, Does.Not.Contain("float intensity = max(updateParams.z, 0.0) * max(pc.EnvironmentRadianceAndIntensity.w, 0.0);"));
            Assert.That(shader, Does.Contain("radiance = pc.EnvironmentRadianceAndIntensity.rgb * max(pc.EnvironmentRadianceAndIntensity.w, 0.0) * skyWeight;"));
            Assert.That(shader, Does.Contain("float variance = max(mean2 - mean * mean, 0.005);"));
            Assert.That(shader, Does.Contain("float grazingRejection = smoothstep(-0.15, 0.25, alignment);"));
            Assert.That(shader, Does.Contain("float normalWeight = normalHemisphereWeight * normalHemisphereWeight * grazingRejection;"));
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
        string pipelineDeclaration = ReadRepoText("Njulf.Rendering", "Pipeline", "ProductionRenderPipelineDeclaration.cs");

        Assert.Multiple(() =>
        {
            Assert.That(pipelineDeclaration, Does.Contain("// DDGI update runs after ForwardPlusPass and publishes cache data for subsequent frames."));
            Assert.That(scheduleReset, Does.Contain("DDGI_SCHEDULER_COUNTER_BUFFER_INDEX"));
            Assert.That(scheduleReset, Does.Contain("SIZEOF_GPU_DDGI_SCHEDULER_COUNTERS"));
            Assert.That(scheduleReset, Does.Contain("DDGI_TRACE_INDIRECT_DISPATCH_BUFFER_INDEX"));
            Assert.That(scheduleReset, Does.Contain("SIZEOF_GPU_DDGI_TRACE_INDIRECT_DISPATCH"));
            Assert.That(scheduleScore, Does.Contain("TryResolveDdgiScheduleVolume"));
            Assert.That(scheduleShared, Does.Contain("DDGI_PROBE_CANDIDATE_BUFFER_INDEX"));
            Assert.That(scheduleShared, Does.Contain("MinimumProbeRefreshFrames"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_PROBE_STATE_UPDATE_METADATA"));
            Assert.That(scheduleShared, Does.Contain("uint FrameSerial;"));
            Assert.That(scheduleShared, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_FRAME_SERIAL"));
            Assert.That(scheduleShared, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_WARMUP_STATE"));
            Assert.That(scheduleShared, Does.Contain("uint WarmupCascade0Budget;"));
            Assert.That(scheduleScore, Does.Contain("bool ageDue = !newProbe && constants.FrameSerial - lastUpdateFrame >= constants.MinimumProbeRefreshFrames;"));
            Assert.That(scheduleScore, Does.Contain("DDGI_WARMUP_STATE_LOCAL_VOLUME"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_WARMUP_WARMED_CASCADE0_PROBE_COUNT"));
            Assert.That(scheduleShared, Does.Contain("TryReserveDdgiScheduleCandidateSlot"));
            Assert.That(scheduleShared, Does.Contain("uint globalCap = max(requestBudget * 4u, 1u);"));
            Assert.That(scheduleScore, Does.Contain("TryReserveDdgiScheduleCandidateSlot(constants, groupIndex, priority, reasonFlags)"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_OVERFLOW_COUNT"));
            Assert.That(scheduleShared, Does.Contain("uint localTopKCap = min(max((requestBudget + groupCount - 1u) / groupCount, 1u), 16u);"));
            Assert.That(scheduleReset, Does.Contain("uint prefixCount = groupBucketCount + constants.PriorityBucketCount + 1u;"));
            Assert.That(scheduleFinalize, Does.Contain("bucketQuota = min(constants.WarmupLocalBudget, requestBudget);"));
            Assert.That(scheduleFinalize, Does.Contain("bucketQuota = min((requestBudget * 40u + 99u) / 100u, requestBudget);"));
            Assert.That(scheduleFinalize, Does.Contain("uint unusedQuotaCarry = 0u;"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_STABLE_SKIPPED_COUNT"));
            Assert.That(scheduleScore, Does.Not.Contain("uint reasonFlags = DDGI_SCHEDULE_REASON_AGE_REFRESH;"));
            Assert.That(scheduleFinalize, Does.Contain("WriteDdgiProbeUpdateRequestFromCandidate"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_REQUEST_COUNT"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_PRIORITY0_REQUEST_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_VISIBLE_FRUSTUM_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_SAFETY_SHELL_COUNT"));
            Assert.That(scheduleFinalize, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_CANDIDATE_COUNT"));
            Assert.That(scheduleScore, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_AGE_REFRESH_COUNT"));
            Assert.That(schedulePrefix, Does.Contain("OFFSET_GPU_DDGI_SCHEDULER_COUNTER_CANDIDATE_COUNT"));
            Assert.That(scheduleCompact, Does.Contain("CopyDdgiProbeCandidate(constants.ActiveProbeCount + compactedOffset, candidateIndex);"));
            Assert.That(scheduleFinalize, Does.Contain("constants.ActiveProbeCount + bucketStart"));
            Assert.That(scheduleFinalize, Does.Not.Contain("offset < constants.ActiveProbeCount"));
            Assert.That(trace, Does.Contain("#define DDGI_TRACE_PASS 1"));
            Assert.That(blend, Does.Contain("#define DDGI_BLEND_PASS 1"));
            Assert.That(relocateClassify, Does.Contain("#define DDGI_RELOCATE_CLASSIFY_PASS 1"));
            Assert.That(shader, Does.Contain("uint RayResultScratchBufferIndex;"));
            Assert.That(shader, Does.Contain("void WriteDdgiRayResult(uint updateIndex, uint rayIndex, DdgiRayResult result)"));
            Assert.That(shader, Does.Contain("DdgiRayResult ReadDdgiRayResult(uint updateIndex, uint rayIndex)"));
            Assert.That(shader, Does.Contain("vec3 stableDiffuse = EvaluateStableDiffuseAtHit(hitPosition, surfaceNormal, surfaceAlbedo);"));
            Assert.That(shader, Does.Contain("vec3 emissiveProxyDiffuse = EvaluateSelectedDdgiEmissiveSourceAtHit(hitPosition, surfaceNormal, surfaceAlbedo);"));
            Assert.That(shader, Does.Contain("radiance = surfaceEmissive + emissiveProxyDiffuse + directDiffuse + stableDiffuse;"));
            Assert.That(shader, Does.Contain("ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);"));
            Assert.That(shader, Does.Contain("ReadPackedHalf4(pc.IrradianceAtlasBufferIndex"));
            Assert.That(shader, Does.Contain("ReadPackedHalf2(pc.VisibilityAtlasBufferIndex"));
            Assert.That(shader, Does.Contain("vec3 rawIrradiance = blendedIrradiance / blendedCoverage;"));
            Assert.That(shader, Does.Contain("if (DdgiRawAtlasRadianceConventionEnabled())"));
            Assert.That(shader, Does.Contain("return globalIntensity > 0.0001"));
            Assert.That(shader, Does.Contain("? clamp(rawIrradiance / globalIntensity, vec3(0.0), vec3(64.0))"));
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
            Assert.That(shader, Does.Contain("float rayHitConfidence = clamp(qualityAndReason.x, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float stateIrradianceConfidence = clamp(qualityAndReason.y, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float visibilityConfidence = clamp(qualityAndReason.z, 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("float qualityConfidence = clamp(max(rayHitConfidence, 0.25) * max(stateIrradianceConfidence, irradianceConfidence) * max(visibilityConfidence, 0.25), 0.0, 1.0);"));
            Assert.That(shader, Does.Contain("if (DdgiDebugBypassFinalSuppression())"));
            Assert.That(shader, Does.Contain("qualityConfidence = max(qualityConfidence, 0.25);"));
            Assert.That(shader, Does.Contain("float supportWeight = expectedContributionWeight * probeActive * irradianceConfidence * qualityConfidence;"));
            Assert.That(shader, Does.Contain("totalActive += probeActive * irradianceConfidence * qualityConfidence * cellWeight;"));
            Assert.That(shader, Does.Not.Contain("float supportWeight = expectedContributionWeight * probeActive * irradianceConfidence;"));
            Assert.That(shader, Does.Not.Contain("totalActive += probeActive * irradianceConfidence * cellWeight;"));
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
            Assert.That(shader, Does.Contain("float cascadeIndex;"));
            Assert.That(shader, Does.Contain("float cascadeBlendWeight;"));
            Assert.That(shader, Does.Contain("float updateReason;"));
            Assert.That(shader, Does.Contain("float rayBudget;"));
            Assert.That(shader, Does.Contain("float minProbeSpacing;"));
            Assert.That(shader, Does.Contain("float classificationInvalidScore;"));
            Assert.That(shader, Does.Contain("float visibilityMomentMean;"));
            Assert.That(shader, Does.Contain("float visibilityMomentVariance;"));
            Assert.That(shader, Does.Contain("float visibilityProbeDistance;"));
            Assert.That(shader, Does.Contain("float visibilityMaxRayDistance;"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(vec3(clamp(ddgiSample.coverage, 0.0, 1.0)), 1.0));"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(vec3(clamp(hybridDiffuse.effectiveDdgiWeight, 0.0, 1.0)), 1.0));"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(vec3(clamp(hybridDiffuse.environmentFallbackWeight / 4.0, 0.0, 1.0)), 1.0));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS)"));
            Assert.That(shader, Does.Contain("clamp(ddgiSample.visibilityMomentMean / visibilityMaxDistance, 0.0, 1.0)"));
            Assert.That(shader, Does.Contain("clamp(sqrt(max(ddgiSample.visibilityMomentVariance, 0.0)) / visibilityMaxDistance, 0.0, 1.0)"));
            Assert.That(shader, Does.Contain("clamp(ddgiSample.visibilityProbeDistance / visibilityMaxDistance, 0.0, 1.0)"));
            Assert.That(shader, Does.Contain("float relocationAmount = length(ddgiSample.relocation) / max(ddgiSample.minProbeSpacing * 0.4, 0.001);"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(vec3(clamp(ddgiSample.classificationInvalidScore, 0.0, 1.0)), 1.0));"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(MeshletDebugColor(uint(max(ddgiSample.cascadeIndex, 0.0)) + 1u), 1.0));"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(vec3(clamp(ddgiSample.cascadeBlendWeight, 0.0, 1.0)), 1.0));"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(MeshletDebugColor(uint(clamp(ddgiSample.updateReason * 255.0, 0.0, 255.0))), 1.0));"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(ddgiSample.rayBudget, ddgiSample.coverage, ddgiSample.activeProbe, 1.0));"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE)"));
            Assert.That(shader, Does.Contain("if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK)"));
            Assert.That(shader, Does.Contain("WriteForwardColor(vec4(clamp(hybridDiffuse.suppressionMask, vec3(0.0), vec3(1.0)), 1.0));"));
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
            Assert.That(forwardShader, Does.Contain("float environmentFallbackWeight = clamp(environmentTrust * environmentFallbackIntensity, 0.0, 4.0);"));
            Assert.That(forwardShader, Does.Contain("result.diffuse = clamp((environmentFallbackField + ddgiLowFrequencyField + nearField) * indirectAoWeight, vec3(0.0), vec3(64.0));"));
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
