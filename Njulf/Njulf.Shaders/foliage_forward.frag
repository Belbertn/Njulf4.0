#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"
#include "gi_material_transport.glsl"
#include "foliage_coverage.glsl"

#ifndef NJULF_SSGI_TRACE_OUTPUT
#define NJULF_SSGI_TRACE_OUTPUT 0
#endif

#ifndef NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#define NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT 0
#endif

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
#if NJULF_SSGI_TRACE_OUTPUT
layout(location = 1) out vec4 outSsgiTraceSource;
layout(location = 2) out vec4 outGiCompositionBaseline;
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
layout(location = 3) out float outMaterialTransportProvenance;
#endif
#else
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
layout(location = 1) out float outMaterialTransportProvenance;
#endif
#endif

layout(push_constant) uniform FoliageDrawPushConstantBlock
{
    GPUFoliageDrawPushConstants Push;
} pc;

void WriteFoliageForwardColor(vec4 color)
{
    outColor = color;
}

void WriteFoliageSsgiTraceSource(vec4 color)
{
#if NJULF_SSGI_TRACE_OUTPUT
    outSsgiTraceSource = color;
#endif
}

void WriteFoliageGiCompositionBaseline(vec3 diffuseIndirect, float ddgiOwnership)
{
#if NJULF_SSGI_TRACE_OUTPUT
    outGiCompositionBaseline = vec4(
        clamp(diffuseIndirect, vec3(0.0), vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE)),
        clamp(ddgiOwnership, 0.0, 1.0));
#endif
}

void WriteFoliageMaterialTransportProvenance(uint sourcePath)
{
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    const uint foliageMaterialTransportProvenanceFlag = 1u << 2u;
    if ((pc.Push.Flags & foliageMaterialTransportProvenanceFlag) != 0u)
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
    WriteFoliageSsgiTraceSource(vec4(0.0, 0.0, 0.0, 1.0));
    WriteFoliageGiCompositionBaseline(vec3(0.0), 0.0);
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
    vec3 ddgiIndirect = fragDdgiIrradianceCoverage.rgb * (baseColor / 3.14159265359) * fragDdgiIrradianceCoverage.a;
    vec3 foliageLighting = clamp(foliageDirectLighting + ddgiIndirect, vec3(0.0), vec3(64.0));
    WriteFoliageForwardColor(vec4(foliageLighting, 1.0));
    // Only direct diffuse is eligible as an SSGI bounce source. The existing
    // DDGI term is recorded separately so hybrid composition can replace, not
    // add to, the same diffuse path space.
    vec3 foliageEmission = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_EMITS_INTO_GI)
        ? max(material.Emissive.rgb, vec3(0.0))
        : vec3(0.0);
    WriteFoliageSsgiTraceSource(vec4(
        clamp(foliageDirectLighting + foliageEmission, vec3(0.0), vec3(64.0)),
        1.0));
    WriteFoliageGiCompositionBaseline(ddgiIndirect, fragDdgiIrradianceCoverage.a);
    // Foliage carries a precomputed DDGI estimate rather than enough per-probe
    // metadata to identify a compact/far contributor. Mark covered foliage as
    // the detailed mesh path and leave unsupported pixels explicitly unknown.
    WriteFoliageMaterialTransportProvenance(
        fragDdgiIrradianceCoverage.a > 0.0001 ? 1u : 255u);
}
