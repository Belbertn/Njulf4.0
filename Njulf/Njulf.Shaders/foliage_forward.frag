#version 460
#extension GL_GOOGLE_include_directive : require

#ifndef NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#define NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION 0
#endif

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#extension GL_KHR_shader_subgroup_basic : require
#extension GL_KHR_shader_subgroup_arithmetic : require
#extension GL_KHR_shader_subgroup_ballot : require
#endif

#include "common.glsl"
#include "foliage_coverage.glsl"

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#define SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT 0u
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE false
#define SIMPLE_DDGI_RECEIVER_CONTRIBUTION_SAMPLE false
#define SIMPLE_DDGI_RECEIVER_COVERAGE_HASH 0u
#define SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS SIMPLE_DDGI_RECEIVER_CONSUMER_OPAQUE
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 0
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 0u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 0
#include "ddgi_simple_shared.glsl"
#undef SIMPLE_DDGI_OPAQUE_GATHER_ORACLE
#undef SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET
#undef SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT
#undef SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS
#undef SIMPLE_DDGI_RECEIVER_COVERAGE_HASH
#undef SIMPLE_DDGI_RECEIVER_CONTRIBUTION_SAMPLE
#undef SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE
#undef SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT
#include "ddgi_receiver_feedback_source_abi.glsl"
#include "ddgi_receiver_feedback_producer.glsl"
#include "ddgi_receiver_feedback_surface_producer.glsl"
#endif

#ifndef NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#define NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT 0
#endif

const uint FOLIAGE_MATERIAL_TRANSPORT_PROVENANCE_FLAG = 1u << 2u;
const uint FOLIAGE_REFLECTION_FEEDBACK_FLAG = 1u << 3u;
const uint FOLIAGE_REFLECTION_CAPTURE_LAYER_SHIFT = 8u;
const uint FOLIAGE_REFLECTION_CAPTURE_LAYER_MASK = 0x1fffu;

layout(location = 0) in vec2 fragTexCoord;
layout(location = 1) flat in uint fragMaterialIndex;
layout(location = 2) in vec3 fragWorldPosition;
layout(location = 3) in vec3 fragNormal;
layout(location = 4) flat in uint fragClusterIndex;
layout(location = 5) flat in uint fragLodBand;
layout(location = 6) flat in uint fragGeometryMode;
layout(location = 7) flat in uint fragDebugMeshletIndex;
layout(location = 8) flat in vec4 fragColorVariation;
layout(location = 9) flat in vec4 fragDdgiIrradianceCoverage;

layout(location = 0) out vec4 outColor;
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
layout(location = 1) out float outMaterialTransportProvenance;
#endif

layout(push_constant) uniform FoliageDrawPushConstantBlock
{
    GPUFoliageDrawPushConstants Push;
} pc;

void WriteFoliageForwardColor(vec4 color)
{
    outColor = color;
}

void WriteFoliageMaterialTransportProvenance(uint sourcePath)
{
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    if ((pc.Push.Flags & FOLIAGE_MATERIAL_TRANSPORT_PROVENANCE_FLAG) != 0u)
        outMaterialTransportProvenance = float(min(sourcePath, 255u)) / 255.0;
#endif
}

vec3 DebugColor(uint value)
{
    uint hash = value * 747796405u + 2891336453u;
    hash = (hash >> ((hash >> 28u) + 4u)) ^ hash;
    hash *= 277803737u;
    hash = (hash >> 22u) ^ hash;
    return vec3(
        float(hash & 255u),
        float((hash >> 8u) & 255u),
        float((hash >> 16u) & 255u)) / 255.0;
}

vec3 SafeNormalize(vec3 value, vec3 fallback)
{
    float lengthSquared = dot(value, value);
    if (lengthSquared <= 0.000001)
        return fallback;
    return value * inversesqrt(lengthSquared);
}

vec3 ComputeBentNormal(vec3 rawNormal, vec3 viewDirection, GPUFoliageCluster cluster, GPUFoliagePrototype prototype)
{
    vec3 normal = SafeNormalize(rawNormal, vec3(0.0, 1.0, 0.0));
    float normalBend = clamp(prototype.LightingParams.z, 0.0, 1.0);
    vec3 clusterVector = fragWorldPosition - cluster.WorldCenterRadius.xyz;
    vec3 clumpNormal = SafeNormalize(
        vec3(clusterVector.x, max(cluster.WorldCenterRadius.w * 0.35, 0.1), clusterVector.z),
        vec3(0.0, 1.0, 0.0));

    float bendStrength = fragGeometryMode == 0u ? normalBend : normalBend * 0.55;
    normal = SafeNormalize(mix(normal, clumpNormal, bendStrength), vec3(0.0, 1.0, 0.0));
    if (dot(normal, viewDirection) < 0.0)
        normal = -normal;
    return normal;
}

vec3 ApplyFoliageLighting(vec3 baseColor, vec3 normal, vec3 viewDirection, GPUFoliagePrototype prototype)
{
    vec3 lightDirection = normalize(vec3(-0.35, 0.85, 0.25));
    float wrap = mix(0.08, 0.72, clamp(prototype.LightingParams.x, 0.0, 1.0));
    float backlightStrength = clamp(prototype.LightingParams.y, 0.0, 1.0);
    float frontDiffuse = clamp((dot(normal, lightDirection) + wrap) / (1.0 + wrap), 0.0, 1.0);
    float backDiffuse = clamp((dot(-normal, lightDirection) + wrap) / (1.0 + wrap), 0.0, 1.0);
    float viewBacklight = pow(clamp(dot(viewDirection, -lightDirection) * 0.5 + 0.5, 0.0, 1.0), 2.0);
    float diffuse = frontDiffuse + backDiffuse * backlightStrength * viewBacklight * 0.65;
    float heightShade = mix(0.74, 1.08, clamp(fragTexCoord.y, 0.0, 1.0));
    return baseColor * (0.18 + diffuse * 0.92) * heightShade;
}

void main()
{
    WriteFoliageMaterialTransportProvenance(255u);
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    vec4 sampledAlbedo;
    if (!FoliageCoverageSurvives(
            material,
            fragTexCoord,
            fragGeometryMode,
            fragClusterIndex,
            fragLodBand,
            gl_FragCoord.xy,
            sampledAlbedo))
        discard;

    if (pc.Push.DebugView == 1u)
    {
        uint debugId = fragGeometryMode == 1u ? fragDebugMeshletIndex : fragClusterIndex;
        WriteFoliageForwardColor(vec4(DebugColor(debugId), 1.0));
        return;
    }

    if (pc.Push.DebugView == 2u)
    {
        vec3 lodColor = fragLodBand == 0u
            ? vec3(0.2, 0.95, 0.25)
            : (fragLodBand == 1u ? vec3(0.95, 0.85, 0.2) : vec3(0.95, 0.28, 0.18));
        WriteFoliageForwardColor(vec4(lodColor, 1.0));
        return;
    }

    GPUFoliageCluster cluster = ReadFoliageCluster(fragClusterIndex);
    GPUFoliagePatch foliagePatch = ReadFoliagePatch(cluster.PatchIndex);
    GPUFoliagePrototype prototype = ReadFoliagePrototype(foliagePatch.PrototypeIndex);
    vec3 baseColor = material.Albedo.rgb * sampledAlbedo.rgb;
    baseColor *= mix(vec3(1.0), max(fragColorVariation.rgb, vec3(0.0)), clamp(fragColorVariation.a, 0.0, 1.0));
    if (length(baseColor) <= 0.001)
        baseColor = vec3(0.18, 0.48, 0.12);

    vec3 viewDirection = SafeNormalize(pc.Push.CameraPositionTime.xyz - fragWorldPosition, vec3(0.0, 0.0, 1.0));
    vec3 normal = ComputeBentNormal(fragNormal, viewDirection, cluster, prototype);
    vec3 foliageDirectLighting = ApplyFoliageLighting(baseColor, normal, viewDirection, prototype);
    vec4 ddgiIrradianceCoverage = fragDdgiIrradianceCoverage;
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    SimpleDdgiParams simpleDdgiParams = ReadSimpleDdgiParams(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    bool simpleDdgiConfigured =
        (simpleDdgiParams.flags &
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) ==
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) &&
        simpleDdgiParams.probeCount > 0u;
    SimpleDdgiGatherResult exactGather = EmptySimpleDdgiGatherResult();
    float radiometricOwnership = 0.0;
    float leakAttenuation = 0.0;
    if (simpleDdgiConfigured)
    {
        exactGather = SampleSimpleDdgiGather(
            simpleDdgiParams,
            fragWorldPosition,
            normal,
            viewDirection);
        radiometricOwnership = SimpleDdgiRadiometricOwnership(exactGather);
        leakAttenuation = SimpleDdgiLeakAttenuation(
            exactGather,
            simpleDdgiParams);
        ddgiIrradianceCoverage = vec4(
            clamp(
                exactGather.irradiance *
                    max(simpleDdgiParams.indirectIntensity, 0.0),
                vec3(0.0),
                vec3(64.0)),
            clamp(
                radiometricOwnership * leakAttenuation,
                0.0,
                1.0));
    }
#endif
    vec3 ddgiIndirect = ddgiIrradianceCoverage.rgb *
        (baseColor / 3.14159265359) * ddgiIrradianceCoverage.a;
    vec3 foliageLighting = clamp(foliageDirectLighting + ddgiIndirect, vec3(0.0), vec3(64.0));
    WriteFoliageForwardColor(vec4(foliageLighting, 1.0));
    // Foliage carries a precomputed DDGI estimate rather than enough per-probe
    // metadata to identify a compact/far contributor. Mark covered foliage as
    // the detailed mesh path and leave unsupported pixels explicitly unknown.
    WriteFoliageMaterialTransportProvenance(
        ddgiIrradianceCoverage.a > 0.0001 ? 1u : 255u);
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    float survivingCoverage = FoliageLodCoverage(fragLodBand) *
        (fragGeometryMode == 0u
            ? 1.0
            : clamp(material.Albedo.a * sampledAlbedo.a, 0.0, 1.0));
    bool reflectionFeedback =
        (pc.Push.Flags & FOLIAGE_REFLECTION_FEEDBACK_FLAG) != 0u;
    uint reflectionCaptureLayer =
        (pc.Push.Flags >> FOLIAGE_REFLECTION_CAPTURE_LAYER_SHIFT) &
        FOLIAGE_REFLECTION_CAPTURE_LAYER_MASK;
    uint tileNamespaceBase = 0u;
    bool tileNamespaceValid = !reflectionFeedback ||
        SimpleDdgiTryComputeCubemapTileNamespace(
            reflectionCaptureLayer,
            pc.Push.ScreenDimensions.xy,
            tileNamespaceBase);
    float physicalSurfaceWeight = reflectionFeedback
        ? SimpleDdgiCubemapTexelSolidAngle(
              gl_FragCoord.xy,
              pc.Push.ScreenDimensions.xy) *
          SimpleDdgiRoughSpecularWeight(
              simpleDdgiParams.residencyFlags,
              clamp(material.MetallicRoughnessAO.y, 0.04, 1.0))
        : survivingCoverage;
    EmitSimpleDdgiSurfaceReceiverFeedbackCore(
        exactGather,
        simpleDdgiConfigured,
        radiometricOwnership,
        leakAttenuation,
        physicalSurfaceWeight,
        true,
        reflectionFeedback ? 5u : 1u,
        pc.Push.CurrentFrameIndex,
        pc.Push.ScreenDimensions.xy,
        tileNamespaceValid,
        tileNamespaceBase,
        uvec3(
            fragClusterIndex,
            fragMaterialIndex,
            fragGeometryMode == 1u
                ? fragDebugMeshletIndex
                : fragLodBand));
#endif
}
