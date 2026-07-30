#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"
#include "gi_material_transport.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform SsgiCompositePushBlock
{
    uint GiFinalDiffuseTextureIndex;
    uint SceneMaterialTextureIndex;
    uint MaterialTransportProvenanceTextureIndex;
    uint DebugView;
    uint CompositionFlags;
    uint Padding0;
    uint Padding1;
    uint Padding2;
} pc;

const uint GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT = 1u;
const uint GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED = 3u;
const uint GLOBAL_ILLUMINATION_DEBUG_MATERIAL_TRANSPORT_SOURCE_OWNERSHIP = 46u;
const uint GLOBAL_ILLUMINATION_DEBUG_HYBRID_ESTIMATOR_OWNERSHIP = 47u;
const uint GLOBAL_ILLUMINATION_DEBUG_HYBRID_FINAL_COMPOSITION = 48u;
const uint GLOBAL_ILLUMINATION_DEBUG_MATERIAL_TRANSPORT_HIT_PROVENANCE = 49u;
const uint SSGI_COMPOSITION_FLAG_HYBRID_V2 = 1u;
const uint SSGI_COMPOSITION_FLAG_ENVIRONMENT_FALLBACK = 1u << 1u;
const uint SSGI_COMPOSITION_FLAG_MATERIAL_TRANSPORT_V2 = 1u << 2u;
const uint SSGI_COMPOSITION_FLAG_FAR_FIELD_TRANSPORT = 1u << 3u;

vec3 ComposeScreenSpaceContactGi(vec4 gi, vec4 material)
{
    vec3 receiverDiffuseReflectance = clamp(material.rgb, vec3(0.0), vec3(1.0));
    float materialOcclusion = clamp(material.a, 0.0, 1.0);
    vec3 incidentIrradiance = clamp(
        gi.rgb,
        vec3(0.0),
        vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE));
    return ApplyGiMaterialOcclusion(
        EvaluateGiDiffuseFromIrradiance(incidentIrradiance, receiverDiffuseReflectance),
        materialOcclusion);
}

bool IsStandaloneHybridDiagnosticView(uint view)
{
    return view == GLOBAL_ILLUMINATION_DEBUG_MATERIAL_TRANSPORT_SOURCE_OWNERSHIP ||
        view == GLOBAL_ILLUMINATION_DEBUG_HYBRID_ESTIMATOR_OWNERSHIP ||
        view == GLOBAL_ILLUMINATION_DEBUG_HYBRID_FINAL_COMPOSITION ||
        view == GLOBAL_ILLUMINATION_DEBUG_MATERIAL_TRANSPORT_HIT_PROVENANCE;
}

vec3 MaterialTransportSourceOwnershipColor(
    float ssgiSupport,
    float ddgiOwnership,
    bool fallbackCapable,
    bool materialTransportV2)
{
    // The current composition target does not retain exact ray-hit provenance
    // or selected DDGI cascade. This deliberately visualizes available source
    // ownership instead: red=textured/SSGI-supported, green=compact/probe-owned,
    // blue=far-field or environment-fallback-capable remainder. Overlap means
    // two estimators are available, not that both contribute at full weight.
    if (!materialTransportV2)
        return vec3(1.0, 0.0, 1.0); // Explicit legacy/unclassified state.

    float fallbackCapability = fallbackCapable
        ? 1.0 - ddgiOwnership
        : 0.0;
    return clamp(
        vec3(ssgiSupport, ddgiOwnership, fallbackCapability),
        vec3(0.0),
        vec3(1.0));
}

vec3 HybridEstimatorOwnershipColor(
    float replacementWeight,
    float ddgiOwnership,
    float fallbackOwnership)
{
    // These channels are non-overlapping coefficients after SSGI replacement:
    // red=retained DDGI, green=SSGI, blue=retained environment fallback.
    float retainedBaseline = 1.0 - replacementWeight;
    return clamp(
        vec3(
            ddgiOwnership * retainedBaseline,
            replacementWeight,
            fallbackOwnership * retainedBaseline),
        vec3(0.0),
        vec3(1.0));
}

vec3 MaterialTransportHitProvenanceColor(uint sourcePath)
{
    // Stable categorical palette: black=background, red=detailed mesh,
    // green=compact primitive, blue=far field, magenta=unknown.
    if (sourcePath == 0u)
        return vec3(0.0);
    if (sourcePath == 1u)
        return vec3(1.0, 0.12, 0.05);
    if (sourcePath == 2u)
        return vec3(0.10, 1.0, 0.25);
    if (sourcePath == 3u)
        return vec3(0.05, 0.35, 1.0);
    return vec3(1.0, 0.0, 1.0);
}

void main()
{
    if (pc.DebugView == GLOBAL_ILLUMINATION_DEBUG_MATERIAL_TRANSPORT_HIT_PROVENANCE)
    {
        ivec2 provenanceExtent = textureSize(
            BindlessTextures[
                nonuniformEXT(int(pc.MaterialTransportProvenanceTextureIndex))],
            0);
        ivec2 provenancePixel = clamp(
            ivec2(inUv * vec2(provenanceExtent)),
            ivec2(0),
            provenanceExtent - ivec2(1));
        // Integer-addressed fetch prevents interpolation from manufacturing
        // undefined categorical codes along geometry/path boundaries.
        float encodedSourcePath = texelFetch(
            BindlessTextures[
                nonuniformEXT(int(pc.MaterialTransportProvenanceTextureIndex))],
            provenancePixel,
            0).r;
        uint sourcePath = uint(round(clamp(encodedSourcePath, 0.0, 1.0) * 255.0));
        outColor = vec4(MaterialTransportHitProvenanceColor(sourcePath), 1.0);
        return;
    }

    // Forward records the diffuse-indirect term already present in SceneColor
    // into GiFinalDiffuse. Once tracing has consumed SsgiTraceSource, denoising
    // phase-reuses that image for the SSGI estimate.
    vec4 baseline = texture(BindlessTextures[nonuniformEXT(int(pc.GiFinalDiffuseTextureIndex))], inUv);
    vec4 gi = texture(BindlessTextures[nonuniformEXT(SSGI_TRACE_SOURCE_TEXTURE_INDEX)], inUv);
    vec4 material = texture(BindlessTextures[nonuniformEXT(int(pc.SceneMaterialTextureIndex))], inUv);
    float support = clamp(gi.a, 0.0, 1.0);
    vec3 ssgiIndirect = ComposeScreenSpaceContactGi(gi, material);
    vec3 baselineIndirect = clamp(
        baseline.rgb,
        vec3(0.0),
        vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE));
    float ddgiOwnership = clamp(baseline.a, 0.0, 1.0);
    float fallbackOwnership =
        (pc.CompositionFlags & SSGI_COMPOSITION_FLAG_ENVIRONMENT_FALLBACK) != 0u
            ? 1.0 - ddgiOwnership
            : 0.0;
    float baselineOwnership = clamp(ddgiOwnership + fallbackOwnership, 0.0, 1.0);
    // The denoised support channel already carries trace range/edge support,
    // depth-normal bilateral agreement, and temporal confidence. DDGI plus
    // the explicitly enabled environment fallback bound the path-space share
    // that SSGI is allowed to replace.
    float replacementWeight = support * baselineOwnership;
    bool standaloneDiagnostic = IsStandaloneHybridDiagnosticView(pc.DebugView);

    if (support <= 0.0001 &&
        pc.DebugView != GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED &&
        !standaloneDiagnostic)
        discard;

    if (pc.DebugView == GLOBAL_ILLUMINATION_DEBUG_MATERIAL_TRANSPORT_SOURCE_OWNERSHIP)
    {
        bool fallbackCapable =
            (pc.CompositionFlags & (
                SSGI_COMPOSITION_FLAG_ENVIRONMENT_FALLBACK |
                SSGI_COMPOSITION_FLAG_FAR_FIELD_TRANSPORT)) != 0u;
        bool materialTransportV2 =
            (pc.CompositionFlags & SSGI_COMPOSITION_FLAG_MATERIAL_TRANSPORT_V2) != 0u;
        outColor = vec4(
            MaterialTransportSourceOwnershipColor(
                support,
                ddgiOwnership,
                fallbackCapable,
                materialTransportV2),
            1.0);
    }
    else if (pc.DebugView == GLOBAL_ILLUMINATION_DEBUG_HYBRID_ESTIMATOR_OWNERSHIP)
    {
        bool hybridV2 =
            (pc.CompositionFlags & SSGI_COMPOSITION_FLAG_HYBRID_V2) != 0u;
        outColor = hybridV2
            ? vec4(
                HybridEstimatorOwnershipColor(
                    replacementWeight,
                    ddgiOwnership,
                    fallbackOwnership),
                1.0)
            : vec4(1.0, 0.0, 1.0, 1.0); // Legacy additive overlap warning.
    }
    else if (pc.DebugView == GLOBAL_ILLUMINATION_DEBUG_HYBRID_FINAL_COMPOSITION)
    {
        bool hybridV2 =
            (pc.CompositionFlags & SSGI_COMPOSITION_FLAG_HYBRID_V2) != 0u;
        vec3 finalComposition = hybridV2
            ? mix(baselineIndirect, ssgiIndirect, replacementWeight)
            // Match the legacy production branch exactly: unsupported pixels
            // are discarded there, leaving the forward baseline untouched.
            : support > 0.0001
                ? baselineIndirect + ssgiIndirect
                : baselineIndirect;
        outColor = vec4(finalComposition, 1.0);
    }
    else if (pc.DebugView == GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED)
        outColor = vec4(vec3(support), 1.0);
    else if ((pc.CompositionFlags & SSGI_COMPOSITION_FLAG_HYBRID_V2) != 0u)
    {
        // SceneColor already contains baselineIndirect. Signed additive
        // blending applies this delta, yielding exactly:
        //   (1-w) * Lbaseline + w * Lssgi
        // This is a bounded convex estimator for every w in [0,1], and the
        // identical-estimator case is an exact zero delta before target
        // quantization.
        vec3 replacementDelta = replacementWeight * (ssgiIndirect - baselineIndirect);
        outColor = vec4(replacementDelta, 0.0);
    }
    else
    {
        // One-release rollback for A/B diagnostics. Only this legacy path adds
        // the full SSGI estimator; the V2 path above is the sole contributor
        // whenever its independent feature flag is enabled.
        outColor = vec4(ssgiIndirect, 0.0);
    }
}
