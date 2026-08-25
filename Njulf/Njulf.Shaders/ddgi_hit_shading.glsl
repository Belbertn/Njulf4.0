#ifndef NJULF_DDGI_HIT_SHADING_GLSL
#define NJULF_DDGI_HIT_SHADING_GLSL

#include "gi_material_transport.glsl"
#include "ddgi_alpha_coverage.glsl"
#include "ddgi_content_stochastic.glsl"
#include "area_lighting.glsl"

#ifndef DDGI_HIT_WORLD_PROBE_STABLE_KEY
#define DDGI_HIT_WORLD_PROBE_STABLE_KEY uvec2(0u)
#endif
#ifndef DDGI_HIT_DIRECTION_RAY_ORDINAL
#define DDGI_HIT_DIRECTION_RAY_ORDINAL 0u
#endif
#ifndef DDGI_HIT_SOURCE_LIGHTING_EPOCH
#define DDGI_HIT_SOURCE_LIGHTING_EPOCH 1u
#endif
#ifndef DDGI_HIT_SAMPLING_SEQUENCE_EPOCH
#define DDGI_HIT_SAMPLING_SEQUENCE_EPOCH 1u
#endif
#ifndef DDGI_HIT_CURRENT_FRAME_INDEX
#define DDGI_HIT_CURRENT_FRAME_INDEX pc.CurrentFrameIndex
#endif

vec3 TraceLightVisibility(
    vec3 worldPosition,
    vec3 normal,
    vec3 lightDirection,
    float maxDistance,
    float receiverProbeSpacing,
    bool recordAnalyticDirectDiagnostics);

#ifndef DDGI_HIT_USE_SELECTED_LIGHTS
#define DDGI_HIT_USE_SELECTED_LIGHTS 1
#endif

#ifndef DDGI_HIT_ENABLE_ENVIRONMENT_WRAPPER
#define DDGI_HIT_ENABLE_ENVIRONMENT_WRAPPER 1
#endif

#ifndef DDGI_HIT_DIRECT_LIGHT_CAP
#define DDGI_HIT_DIRECT_LIGHT_CAP pc.MaxShadedLights
#endif

// The Simple DDGI update queue carries a quality profile per probe update.  The
// hit shader is also shared by other GI paths, so leave the push-constant
// defaults intact unless the caller explicitly supplies per-update values.
#ifndef DDGI_HIT_MAX_SHADED_LIGHTS
#define DDGI_HIT_MAX_SHADED_LIGHTS pc.MaxShadedLights
#endif

#ifndef DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE
#define DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE pc.MaterialTextureMaxCascade
#endif

#ifndef DDGI_HIT_ALPHA_MASK_TRANSPORT_ENABLED
#define DDGI_HIT_ALPHA_MASK_TRANSPORT_ENABLED true
#endif

#ifndef DDGI_HIT_CANDIDATE_MATERIAL_TEXTURES_ALLOWED
#define DDGI_HIT_CANDIDATE_MATERIAL_TEXTURES_ALLOWED true
#endif

#ifndef DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED
#define DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED true
#endif

struct DdgiAreaLightSurfaceSample
{
    bool valid;
    vec3 position;
    vec3 normal;
    float areaPdf;
};

float DdgiAreaLightRandom(inout uint state)
{
    state = DdgiStochasticMix(state, 0x9E3779B9u);
    return (float(state >> 8u) + 0.5) * (1.0 / 16777216.0);
}

DdgiAreaLightSurfaceSample DdgiSampleAreaLightSurface(
    GPULight light,
    uint sampleOrdinal)
{
    DdgiAreaLightSurfaceSample emitterSample;
    emitterSample.valid = false;
    emitterSample.position = light.Position;
    emitterSample.normal = vec3(0.0, 1.0, 0.0);
    emitterSample.areaPdf = 0.0;
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        DDGI_AREA_LIGHT_SAMPLE_ATTEMPT_COUNTER,
        1u);
    vec3 axis;
    vec3 up;
    vec3 right;
    float totalArea = NjulfAreaSurfaceArea(light);
    if (!NjulfBuildLightFrame(light, axis, up, right) ||
        !(totalArea > 0.0) || isnan(totalArea) || isinf(totalArea))
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_AREA_LIGHT_INVALID_PDF_COUNTER,
            1u);
        return emitterSample;
    }
    uint state = DdgiStableDecisionHash(
        DDGI_HIT_WORLD_PROBE_STABLE_KEY,
        DDGI_HIT_DIRECTION_RAY_ORDINAL,
        DDGI_HIT_SOURCE_LIGHTING_EPOCH,
        DDGI_HIT_SAMPLING_SEQUENCE_EPOCH,
        DDGI_STOCHASTIC_DOMAIN_AREA_LIGHT_SURFACE,
        light.StableIdentity,
        sampleOrdinal);
    vec3 random = vec3(
        DdgiAreaLightRandom(state),
        DdgiAreaLightRandom(state),
        DdgiAreaLightRandom(state));
    if (light.Type == GPU_LIGHT_TYPE_RECTANGLE)
    {
        emitterSample.position = light.Position +
            right * ((random.x - 0.5) * light.SizeX) +
            up * ((random.y - 0.5) * light.SizeY);
        emitterSample.normal = NjulfAreaLightIsTwoSided(light) && random.z >= 0.5
            ? -axis
            : axis;
    }
    else if (light.Type == GPU_LIGHT_TYPE_DISK)
    {
        float radius = light.SizeX * 0.5 * sqrt(random.x);
        float angle = 2.0 * NJULF_LTC_PI * random.y;
        emitterSample.position = light.Position +
            right * (radius * cos(angle)) +
            up * (radius * sin(angle));
        emitterSample.normal = NjulfAreaLightIsTwoSided(light) && random.z >= 0.5
            ? -axis
            : axis;
    }
    else if (light.Type == GPU_LIGHT_TYPE_TUBE)
    {
        float radius = light.SizeY * 0.5;
        float sideArea = 2.0 * NJULF_LTC_PI * radius * light.SizeX;
        float capArea = NJULF_LTC_PI * radius * radius;
        float selector = random.z * totalArea;
        if (selector < sideArea)
        {
            float angle = 2.0 * NJULF_LTC_PI * random.x;
            emitterSample.normal = right * cos(angle) + up * sin(angle);
            emitterSample.position = light.Position +
                axis * ((random.y - 0.5) * light.SizeX) +
                emitterSample.normal * radius;
        }
        else
        {
            bool positiveCap = selector >= sideArea + capArea;
            float radial = radius * sqrt(random.x);
            float angle = 2.0 * NJULF_LTC_PI * random.y;
            emitterSample.normal = positiveCap ? axis : -axis;
            emitterSample.position = light.Position +
                axis * (positiveCap ? light.SizeX * 0.5 : -light.SizeX * 0.5) +
                right * (radial * cos(angle)) +
                up * (radial * sin(angle));
        }
    }
    else
    {
        return emitterSample;
    }
    emitterSample.areaPdf = 1.0 / totalArea;
    emitterSample.valid = NjulfAreaFinite(emitterSample.position) &&
        NjulfAreaFinite(emitterSample.normal) && emitterSample.areaPdf > 0.0 &&
        !isnan(emitterSample.areaPdf) && !isinf(emitterSample.areaPdf);
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        emitterSample.valid
            ? DDGI_AREA_LIGHT_SAMPLE_ACCEPT_COUNTER
            : DDGI_AREA_LIGHT_INVALID_PDF_COUNTER,
        1u);
    return emitterSample;
}

vec3 EvaluateDdgiAreaLightDiffuseRadianceAtHit(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    GPULight light,
    uint sampleOrdinal,
    float energyScale,
    float receiverProbeSpacing,
    out vec3 noShadowDiffuse)
{
    noShadowDiffuse = vec3(0.0);
    DdgiAreaLightSurfaceSample emitter = DdgiSampleAreaLightSurface(
        light,
        sampleOrdinal);
    if (!emitter.valid)
        return vec3(0.0);
    vec3 toEmitter = emitter.position - worldPosition;
    float distanceSquared = dot(toEmitter, toEmitter);
    if (!(distanceSquared > 4e-6) || isnan(distanceSquared) ||
        isinf(distanceSquared))
    {
        return vec3(0.0);
    }
    float distanceToEmitter = sqrt(distanceSquared);
    vec3 lightDirection = toEmitter / distanceToEmitter;
    bool transmitted = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED &&
        GiMaterialHasFlag(surface.Flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION) &&
        dot(surface.GeometricNormal, lightDirection) < 0.0;
    float receiverCosine = transmitted
        ? max(dot(-surface.ShadingNormal, lightDirection), 0.0)
        : max(dot(surface.ShadingNormal, lightDirection), 0.0);
    float emitterCosine = max(dot(emitter.normal, -lightDirection), 0.0);
    if (receiverCosine <= 0.0 || emitterCosine <= 0.0)
        return vec3(0.0);
    vec3 axis;
    vec3 up;
    vec3 right;
    if (!NjulfBuildLightFrame(light, axis, up, right))
        return vec3(0.0);
    float closestDistance = NjulfAreaClosestDistance(
        light, worldPosition, axis, up, right);
    float rangeWindow = EvaluateNjulfFiniteRangeWindow(
        closestDistance, light.Range);
    if (rangeWindow <= 0.0)
        return vec3(0.0);
    float nDotV = max(dot(surface.ShadingNormal, viewDirection), 0.0);
    vec3 radiance = max(light.Color, vec3(0.0)) *
        max(light.Intensity, 0.0) * max(energyScale, 0.0) * rangeWindow;
    vec3 incidentIrradiance = radiance *
        (receiverCosine * emitterCosine /
         max(distanceSquared * emitter.areaPdf, 1e-10));
    noShadowDiffuse = transmitted
        ? incidentIrradiance *
            (surface.TransmittedDiffuseReflectance / GI_MATERIAL_PI)
        : incidentIrradiance * EvaluateGiDiffuseBrdf(
            surface.DirectionalDiffuseBase,
            surface.DielectricF0,
            receiverCosine,
            nDotV);
    if ((uint(light.ShadowFlags) & GPU_LIGHT_SHADOW_FLAG_CASTS_SHADOWS) == 0u)
        return noShadowDiffuse;
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        DDGI_AREA_LIGHT_VISIBILITY_RAY_COUNTER,
        1u);
    vec3 visibility = TraceLightVisibility(
        worldPosition,
        surface.GeometricNormal,
        lightDirection,
        distanceToEmitter,
        receiverProbeSpacing,
        false);
    return noShadowDiffuse * visibility;
}

#ifndef DDGI_HIT_BINARY_OPAQUE_SHADOW_FAST_PATH
#define DDGI_HIT_BINARY_OPAQUE_SHADOW_FAST_PATH 0
#endif

#ifndef DDGI_HIT_LOCAL_LIGHTS_ENABLED
#define DDGI_HIT_LOCAL_LIGHTS_ENABLED 1
#endif

#ifndef DDGI_HIT_EMISSIVE_SOURCES_ENABLED
#define DDGI_HIT_EMISSIVE_SOURCES_ENABLED 1
#endif

#ifndef DDGI_HIT_ONE_DIRECTIONAL_LIGHT_ONLY
#define DDGI_HIT_ONE_DIRECTIONAL_LIGHT_ONLY 0
#endif

#ifndef DDGI_HIT_CONTENT_DEPENDENT_LOCAL_LIGHTS
#define DDGI_HIT_CONTENT_DEPENDENT_LOCAL_LIGHTS 0
#endif

#ifndef DDGI_HIT_LOCAL_SAMPLING_ENABLED
#define DDGI_HIT_LOCAL_SAMPLING_ENABLED false
#endif

#ifndef DDGI_HIT_LOCAL_SAMPLING_MODE
#define DDGI_HIT_LOCAL_SAMPLING_MODE 3u
#endif

#ifndef DDGI_HIT_EXACT_LOCAL_LIGHT_THRESHOLD
#define DDGI_HIT_EXACT_LOCAL_LIGHT_THRESHOLD 0u
#endif

#ifndef DDGI_HIT_UNIFORM_LIGHT_MIXTURE
#define DDGI_HIT_UNIFORM_LIGHT_MIXTURE 0.02
#endif

#ifndef DDGI_HIT_WORLD_PROBE_STABLE_KEY
#define DDGI_HIT_WORLD_PROBE_STABLE_KEY uvec2(0u)
#endif

#ifndef DDGI_HIT_DIRECTION_RAY_ORDINAL
#define DDGI_HIT_DIRECTION_RAY_ORDINAL 0u
#endif

#ifndef DDGI_HIT_SOURCE_LIGHTING_EPOCH
#define DDGI_HIT_SOURCE_LIGHTING_EPOCH 1u
#endif

#ifndef DDGI_HIT_SAMPLING_SEQUENCE_EPOCH
#define DDGI_HIT_SAMPLING_SEQUENCE_EPOCH 1u
#endif

#ifndef DDGI_HIT_CURRENT_FRAME_INDEX
#define DDGI_HIT_CURRENT_FRAME_INDEX pc.CurrentFrameIndex
#endif

#ifndef DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED
#define DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED false
#endif

#ifndef DDGI_HIT_VOLUME_DIAGNOSTICS_ENABLED
#define DDGI_HIT_VOLUME_DIAGNOSTICS_ENABLED 0
#endif

#ifndef DDGI_HIT_VOLUME_ENERGY_COUNTER_BASE
#define DDGI_HIT_VOLUME_ENERGY_COUNTER_BASE 0u
#endif

#ifndef DDGI_HIT_VOLUME_ENERGY_COUNTER_STRIDE
#define DDGI_HIT_VOLUME_ENERGY_COUNTER_STRIDE 0u
#endif

#ifndef DDGI_HIT_VOLUME_INDEX
#define DDGI_HIT_VOLUME_INDEX 0xffffffffu
#endif

#ifndef DDGI_HIT_RECEIVER_INSTANCE_INDEX
#define DDGI_HIT_RECEIVER_INSTANCE_INDEX 0xffffffffu
#endif

#ifndef DDGI_HIT_RECEIVER_PRIMITIVE_INDEX
#define DDGI_HIT_RECEIVER_PRIMITIVE_INDEX 0xffffffffu
#endif

const uint DDGI_HIT_TOP_LIGHT_LIMIT = 8u;
const uint DDGI_HIT_LIGHT_CANDIDATE_LIMIT = 64u;
// Pathological stacks of cutout geometry cannot create unbounded any-hit
// texture work. Overflow resolves conservatively as opaque and is diagnosed.
#ifndef DDGI_HIT_ALPHA_CANDIDATE_LIMIT
#define DDGI_HIT_ALPHA_CANDIDATE_LIMIT 64u
#endif
#ifndef DDGI_HIT_TRANSPARENCY_LAYER_LIMIT
#define DDGI_HIT_TRANSPARENCY_LAYER_LIMIT 8u
#endif
const uint DDGI_MATERIAL_ALPHA_DIAGNOSTIC_SAMPLE_WEIGHT = 64u;
const uint DDGI_MATERIAL_PROVENANCE_DIAGNOSTIC_SAMPLE_WEIGHT = 64u;

uint DdgiMaterialDiagnosticHash(uint instanceIndex, uint primitiveIndex, vec2 barycentrics)
{
    uint hash =
        instanceIndex * 0x9e3779b9u ^
        primitiveIndex * 0x85ebca6bu ^
        floatBitsToUint(barycentrics.x) * 0xc2b2ae35u ^
        floatBitsToUint(barycentrics.y) * 0x27d4eb2du;
    hash ^= hash >> 16u;
    hash *= 0x7feb352du;
    return hash ^ (hash >> 15u);
}

void RecordDdgiMaterialTransportProvenance(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    uint counterIndex)
{
    // Exact classification on a deterministic sparse sample keeps atomic
    // traffic independent of the ray budget. The weighted counters are
    // mutually exclusive and therefore form an unbiased source distribution.
    uint hash = DdgiMaterialDiagnosticHash(instanceIndex, primitiveIndex, barycentrics);
    if ((hash & (DDGI_MATERIAL_PROVENANCE_DIAGNOSTIC_SAMPLE_WEIGHT - 1u)) != 0u)
        return;
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        counterIndex,
        DDGI_MATERIAL_PROVENANCE_DIAGNOSTIC_SAMPLE_WEIGHT);
}

void RecordDdgiEmissiveSamplingInvocation(vec3 worldPosition)
{
    uint hash =
        floatBitsToUint(worldPosition.x) * 0x9e3779b9u ^
        floatBitsToUint(worldPosition.y) * 0x85ebca6bu ^
        floatBitsToUint(worldPosition.z) * 0xc2b2ae35u ^
        DDGI_HIT_CURRENT_FRAME_INDEX * 0x27d4eb2du;
    hash ^= hash >> 16u;
    hash *= 0x7feb352du;
    hash ^= hash >> 15u;
    if ((hash & (DDGI_MATERIAL_PROVENANCE_DIAGNOSTIC_SAMPLE_WEIGHT - 1u)) != 0u)
        return;
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        MATERIAL_GI_EMISSIVE_SAMPLING_INVOCATION_COUNTER,
        DDGI_MATERIAL_PROVENANCE_DIAGNOSTIC_SAMPLE_WEIGHT);
}

void RecordDdgiAlphaCandidateLimitReached()
{
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        MATERIAL_GI_ALPHA_CANDIDATE_LIMIT_COUNTER,
        1u);
}

void RecordDdgiAlphaCandidateDiagnostics(uint instanceIndex, uint primitiveIndex, bool rejected)
{
    // One deterministic bucket in 64 keeps normal telemetry bounded while
    // retaining a stable rejection-rate estimate across identical frames.
    uint hash = instanceIndex * 0x9e3779b9u ^ primitiveIndex * 0x85ebca6bu;
    if ((hash & (DDGI_MATERIAL_ALPHA_DIAGNOSTIC_SAMPLE_WEIGHT - 1u)) != 0u)
        return;

    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        MATERIAL_GI_ALPHA_CANDIDATE_TEST_COUNTER,
        DDGI_MATERIAL_ALPHA_DIAGNOSTIC_SAMPLE_WEIGHT);
    if (rejected)
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            MATERIAL_GI_ALPHA_CANDIDATE_REJECT_COUNTER,
            DDGI_MATERIAL_ALPHA_DIAGNOSTIC_SAMPLE_WEIGHT);
    }
}

void RecordAndSanitizeDdgiMaterialSurface(
    GPUMaterialData material,
    inout GiSurfaceSample surface)
{
    bool nonFinite =
        any(isnan(material.Albedo)) || any(isinf(material.Albedo)) ||
        any(isnan(material.Emissive)) || any(isinf(material.Emissive)) ||
        any(isnan(material.MetallicRoughnessAO)) || any(isinf(material.MetallicRoughnessAO)) ||
        any(isnan(surface.GeometricNormal)) || any(isinf(surface.GeometricNormal)) ||
        any(isnan(surface.CanonicalGeometricNormal)) || any(isinf(surface.CanonicalGeometricNormal)) ||
        any(isnan(surface.ShadingNormal)) || any(isinf(surface.ShadingNormal)) ||
        any(isnan(surface.DirectionalDiffuseBase)) || any(isinf(surface.DirectionalDiffuseBase)) ||
        any(isnan(surface.DielectricF0)) || any(isinf(surface.DielectricF0)) ||
        any(isnan(surface.DiffuseReflectance)) || any(isinf(surface.DiffuseReflectance)) ||
        any(isnan(surface.TransmittedDiffuseReflectance)) || any(isinf(surface.TransmittedDiffuseReflectance)) ||
        any(isnan(surface.EmissiveRadiance)) || any(isinf(surface.EmissiveRadiance)) ||
        isnan(surface.MaterialOcclusion) || isinf(surface.MaterialOcclusion) ||
        isnan(surface.Opacity) || isinf(surface.Opacity) ||
        isnan(surface.Metallic) || isinf(surface.Metallic) ||
        isnan(surface.Roughness) || isinf(surface.Roughness);
    bool clamped =
        any(lessThan(material.Albedo, vec4(0.0))) ||
        any(greaterThan(material.Albedo, vec4(1.0))) ||
        material.MetallicRoughnessAO.x < 0.0 || material.MetallicRoughnessAO.x > 1.0 ||
        material.MetallicRoughnessAO.y < 0.0 || material.MetallicRoughnessAO.y > 1.0 ||
        material.MetallicRoughnessAO.z < 0.0 || material.MetallicRoughnessAO.z > 1.0 ||
        any(lessThan(surface.DirectionalDiffuseBase, vec3(0.0))) ||
        any(greaterThan(surface.DirectionalDiffuseBase, vec3(1.0))) ||
        any(lessThan(surface.DielectricF0, vec3(0.0))) ||
        any(greaterThan(surface.DielectricF0, vec3(1.0))) ||
        any(lessThan(surface.DiffuseReflectance, vec3(0.0))) ||
        any(greaterThan(surface.DiffuseReflectance, vec3(1.0))) ||
        any(lessThan(surface.TransmittedDiffuseReflectance, vec3(0.0))) ||
        any(greaterThan(surface.TransmittedDiffuseReflectance, vec3(1.0))) ||
        any(greaterThan(
            surface.DiffuseReflectance + surface.TransmittedDiffuseReflectance,
            vec3(1.0001))) ||
        any(lessThan(surface.EmissiveRadiance, vec3(0.0))) ||
        any(greaterThan(surface.EmissiveRadiance, vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE))) ||
        surface.MaterialOcclusion < 0.0 || surface.MaterialOcclusion > 1.0 ||
        surface.Opacity < 0.0 || surface.Opacity > 1.0 ||
        surface.Metallic < 0.0 || surface.Metallic > 1.0 ||
        surface.Roughness < GI_MATERIAL_MINIMUM_ROUGHNESS || surface.Roughness > 1.0;
    bool invalidTransmission =
        any(isnan(surface.TransmittedDiffuseReflectance)) ||
        any(isinf(surface.TransmittedDiffuseReflectance));
    bool transmissionEnergyClamped =
        any(lessThan(surface.TransmittedDiffuseReflectance, vec3(0.0))) ||
        any(greaterThan(surface.TransmittedDiffuseReflectance, vec3(1.0))) ||
        any(greaterThan(
            surface.DiffuseReflectance + surface.TransmittedDiffuseReflectance,
            vec3(1.0001)));

    if (nonFinite)
    {
        AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, MATERIAL_GI_NONFINITE_VALUE_COUNTER, 1u);
        if (invalidTransmission)
            AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_INVALID_TRANSMISSION_COUNTER, 1u);
        surface = EmptyGiSurfaceSample(vec3(0.0, 1.0, 0.0), vec3(0.0, 1.0, 0.0), material.TransportFlags);
    }
    else
    {
        if (clamped)
            AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, MATERIAL_GI_CLAMPED_VALUE_COUNTER, 1u);
        if (transmissionEnergyClamped)
            AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_ENERGY_CLAMP_COUNTER, 1u);

        // The hit evaluator deliberately preserves raw scene-linear emission
        // through the checks above. Clamp only now, at the FP16 transport
        // storage boundary, so finite overflow can never saturate silently.
        surface.EmissiveRadiance = clamp(
            surface.EmissiveRadiance,
            vec3(0.0),
            vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE));
    }
}

vec4 SampleDdgiMaterialTexture(int textureIndex, vec2 uv, float lod, vec4 fallback)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    if (!valid)
        return fallback;

    return textureLod(BindlessTextures[nonuniformEXT(textureIndex)], uv, lod);
}

bool ShouldSampleDdgiMaterialTextures(uint volumeCascadeIndex)
{
    if (volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE)
        return true;

    return DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE < DDGI_MATERIAL_TEXTURE_DISABLED_CASCADE &&
        volumeCascadeIndex <= DDGI_HIT_MATERIAL_TEXTURE_MAX_CASCADE;
}

bool ShouldUseCompactDdgiMaterial(uint volumeCascadeIndex)
{
    return !ShouldSampleDdgiMaterialTextures(volumeCascadeIndex);
}

bool IsCompactDdgiMaterialProfileValid(GPUMaterialData material)
{
    uint flags = material.TransportFlags;
    bool diffuseValid =
        !GiMaterialHasFlag(flags, GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE) ||
        GiMaterialHasFlag(flags, GI_MATERIAL_DIFFUSE_PROFILE_VALID);
    bool emissionValid =
        !GiMaterialHasFlag(flags, GI_MATERIAL_EMITS_INTO_GI) ||
        GiMaterialHasFlag(flags, GI_MATERIAL_EMISSION_PROFILE_VALID);
    bool masked =
        DecodeMaterialAlphaMode(material.NormalScaleBias.y) == MATERIAL_ALPHA_MODE_MASK;
    bool alphaValid =
        !masked ||
        GiMaterialHasFlag(flags, GI_MATERIAL_ALPHA_PROFILE_VALID);
    bool occlusionValid =
        !GiMaterialHasFlag(flags, GI_MATERIAL_HAS_OCCLUSION_TEXTURE) ||
        GiMaterialHasFlag(flags, GI_MATERIAL_BASE_STATISTICS_VALID);
    return diffuseValid && emissionValid && alphaValid && occlusionValid;
}

struct DdgiMaterialTriangleFootprint
{
    vec3 WorldPosition0;
    vec3 WorldPosition1;
    vec3 WorldPosition2;
    vec2 TexCoord00;
    vec2 TexCoord01;
    vec2 TexCoord02;
    vec2 TexCoord10;
    vec2 TexCoord11;
    vec2 TexCoord12;
};

float DdgiTriangleUvDensity(
    vec2 uv0,
    vec2 uv1,
    vec2 uv2,
    vec4 offsetScale,
    DdgiMaterialTriangleFootprint footprint)
{
    // Rotation and translation do not change differential length. Applying
    // the potentially non-uniform authored scale before measuring each edge
    // gives a conservative texel-per-world-unit density at UV seams too.
    vec2 scaledUv0 = uv0 * offsetScale.zw;
    vec2 scaledUv1 = uv1 * offsetScale.zw;
    vec2 scaledUv2 = uv2 * offsetScale.zw;
    float density01 = length(scaledUv1 - scaledUv0) /
        max(length(footprint.WorldPosition1 - footprint.WorldPosition0), 0.0001);
    float density12 = length(scaledUv2 - scaledUv1) /
        max(length(footprint.WorldPosition2 - footprint.WorldPosition1), 0.0001);
    float density20 = length(scaledUv0 - scaledUv2) /
        max(length(footprint.WorldPosition0 - footprint.WorldPosition2), 0.0001);
    return max(density01, max(density12, density20));
}

float ResolveDdgiMaterialTextureLod(
    int textureIndex,
    float texCoordSet,
    vec4 offsetScale,
    DdgiMaterialTriangleFootprint footprint,
    uint volumeCascadeIndex,
    float probeSpacing,
    float hitDistance,
    float rayAngularRadius,
    float authoredLodBias)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX &&
        textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    if (!valid)
        return 0.0;

    bool useTexCoord1 = int(round(texCoordSet)) == 1;
    vec2 uv0 = useTexCoord1 ? footprint.TexCoord10 : footprint.TexCoord00;
    vec2 uv1 = useTexCoord1 ? footprint.TexCoord11 : footprint.TexCoord01;
    vec2 uv2 = useTexCoord1 ? footprint.TexCoord12 : footprint.TexCoord02;
    float uvPerWorldUnit = DdgiTriangleUvDensity(uv0, uv1, uv2, offsetScale, footprint);

    ivec2 dimensions = textureSize(
        BindlessTextures[nonuniformEXT(textureIndex)],
        0);
    float maximumDimension = max(float(max(dimensions.x, dimensions.y)), 1.0);

    // A probe ray represents both a spatial lattice footprint and a bounded
    // solid-angle cone. Authored local volumes retain more detail; clipmap
    // cascades use the quarter-cell footprint required for stable mean energy.
    float latticeRadius = max(probeSpacing, 0.001) *
        (volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE ? 0.125 : 0.25);
    float coneRadius = max(hitDistance, 0.0) * clamp(rayAngularRadius, 0.0, 1.0);
    float worldFootprint = max(latticeRadius, coneRadius);
    float texelFootprint = max(worldFootprint * uvPerWorldUnit * maximumDimension, 1.0);
    float maximumLod = max(floor(log2(maximumDimension)), 0.0);
    return clamp(
        max(authoredLodBias, 0.0) + log2(texelFootprint),
        0.0,
        maximumLod);
}

vec2 ApplyDdgiTextureTransform(vec2 uv, vec4 offsetScale, float rotationRadians)
{
    vec2 scaled = uv * offsetScale.zw;
    float s = sin(rotationRadians);
    float c = cos(rotationRadians);
    return offsetScale.xy + vec2(
        scaled.x * c - scaled.y * s,
        scaled.x * s + scaled.y * c);
}

bool IsIdentityDdgiTextureTransform(vec4 offsetScale, float rotationRadians)
{
    return abs(offsetScale.x) <= 0.0001 &&
           abs(offsetScale.y) <= 0.0001 &&
           abs(offsetScale.z - 1.0) <= 0.0001 &&
           abs(offsetScale.w - 1.0) <= 0.0001 &&
           abs(rotationRadians) <= 0.0001;
}

vec2 SelectDdgiHitUv(vec2 uv0, vec2 uv1, float texCoordSet)
{
    return int(round(texCoordSet)) == 1 ? uv1 : uv0;
}

vec2 MaterialDdgiHitUv(vec2 uv0, vec2 uv1, float texCoordSet, vec4 offsetScale, float rotationRadians)
{
    vec2 uv = SelectDdgiHitUv(uv0, uv1, texCoordSet);
    return IsIdentityDdgiTextureTransform(offsetScale, rotationRadians)
        ? uv
        : ApplyDdgiTextureTransform(uv, offsetScale, rotationRadians);
}

GPUDdgiRayQueryInstance ReadDdgiRayQueryInstance(uint instanceIndex)
{
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_DDGI_RAY_QUERY_INSTANCE / 4);
    uint bufferIndex = uint(SIMPLE_DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX);
    uvec4 header0 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 0u);
    uvec4 header1 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 4u);
    uvec4 header2 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 8u);
    uvec4 header3 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 12u);
    uvec4 header4 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 16u);
    uvec4 header5 = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 20u);
    GPUDdgiRayQueryInstance instance;
    instance.AbiVersion = header0.x;
    instance.GeometryClass = header0.y;
    instance.GeometryFlags = header0.z;
    instance.StableInstanceIdentity = header0.w;
    instance.VertexBufferIndex = header1.x;
    instance.VertexOffset = header1.y;
    instance.VertexStride = header1.z;
    instance.VertexFormat = header1.w;
    instance.PositionOffset = header2.x;
    instance.NormalOffset = header2.y;
    instance.TangentOffset = header2.z;
    instance.TexCoord0Offset = header2.w;
    instance.TexCoord1Offset = header3.x;
    instance.ColorOffset = header3.y;
    instance.IndexBufferIndex = header3.z;
    instance.IndexOffset = header3.w;
    instance.IndexType = header4.x;
    instance.MaterialIndex = header4.y;
    instance.MaterialRevision = header4.z;
    instance.PackedAlpha = header4.w;
    instance.PackedDecalLayerAndOrder = header5.x;
    instance.DecalDepthTolerance = uintBitsToFloat(header5.y);
    instance.DecalDepthBias = uintBitsToFloat(header5.z);
    instance.RepresentationGeneration = header5.w;
    instance.WorldMatrixInverseTranspose = ReadStorageAlignedMat4Uniform(
        bufferIndex,
        baseWord + 24u);
    return instance;
}

bool DdgiRayQueryInstanceIsValid(GPUDdgiRayQueryInstance instance)
{
    return instance.AbiVersion == DDGI_RAY_QUERY_INSTANCE_ABI_V2 &&
        instance.GeometryClass != DDGI_RAY_GEOMETRY_INVALID &&
        instance.RepresentationGeneration != 0u &&
        instance.IndexType == 0u &&
        (instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_SPLIT_STATIC ||
         instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_GPU_VERTEX ||
         instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_FOLIAGE_PROXY);
}

bool DdgiRayGeometryHasFlag(
    GPUDdgiRayQueryInstance instance,
    uint flag)
{
    return (instance.GeometryFlags & flag) == flag;
}

bool DdgiRayGeometryIsDecal(GPUDdgiRayQueryInstance instance)
{
    return instance.GeometryClass == DDGI_RAY_GEOMETRY_DECAL_OVERLAY ||
        DdgiRayGeometryHasFlag(instance, DDGI_RAY_GEOMETRY_FLAG_DECAL_OVERLAY);
}

uint ReadDdgiRayIndex(GPUDdgiRayQueryInstance instance, uint indexOffset)
{
    // V2 currently admits only uint32 index streams. Keep the type check in
    // the ABI validator so an unsupported future stream fails closed.
    return ReadStorageWord(
        instance.IndexBufferIndex,
        instance.IndexOffset + indexOffset);
}

GPUVertex ReadDdgiRayVertex(
    GPUDdgiRayQueryInstance instance,
    uint localVertexIndex)
{
    uint vertexIndex = instance.VertexOffset + localVertexIndex;
    if (instance.VertexFormat == DDGI_RAY_VERTEX_FORMAT_SPLIT_STATIC)
        return ReadSplitVertex(vertexIndex);

    // Interleaved GPUVertex is the current-pose skinning ABI. Foliage proxy
    // records deliberately use the same attribute offsets and stride in their
    // first production version so hit shading stays typed and bounded.
    return ReadVertexFromBuffer(instance.VertexBufferIndex, vertexIndex);
}

void RecordDdgiFoliageProxyHit(GPUDdgiRayQueryInstance instance)
{
    if (instance.GeometryClass == DDGI_RAY_GEOMETRY_AUTHORED_FOLIAGE ||
        instance.GeometryClass == DDGI_RAY_GEOMETRY_PROCEDURAL_FOLIAGE ||
        DdgiRayGeometryHasFlag(instance, DDGI_RAY_GEOMETRY_FLAG_FOLIAGE))
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_FOLIAGE_PROXY_HIT_COUNTER,
            1u);
    }
}

bool ResolveCommittedHitSurface(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    vec3 rayDirection,
    bool frontFacing,
    uint volumeCascadeIndex,
    bool sampleMaterialTextures,
    float hitDistance,
    float probeSpacing,
    float rayAngularRadius,
    out GiSurfaceSample surface)
{
    GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
    if (!DdgiRayQueryInstanceIsValid(instance))
        return false;
    uint triangleIndexBase = primitiveIndex * 3u;
    uint i0 = ReadDdgiRayIndex(instance, triangleIndexBase + 0u);
    uint i1 = ReadDdgiRayIndex(instance, triangleIndexBase + 1u);
    uint i2 = ReadDdgiRayIndex(instance, triangleIndexBase + 2u);
    GPUVertex vertex0 = ReadDdgiRayVertex(instance, i0);
    GPUVertex vertex1 = ReadDdgiRayVertex(instance, i1);
    GPUVertex vertex2 = ReadDdgiRayVertex(instance, i2);

    vec3 bary = vec3(
        1.0 - barycentrics.x - barycentrics.y,
        barycentrics.x,
        barycentrics.y);

    vec3 p0 = vertex0.Position;
    vec3 p1 = vertex1.Position;
    vec3 p2 = vertex2.Position;
    vec3 localGeometricNormal = cross(p1 - p0, p2 - p0);
    localGeometricNormal = dot(localGeometricNormal, localGeometricNormal) > 0.000001
        ? normalize(localGeometricNormal)
        : vec3(0.0, 1.0, 0.0);
    vec3 localShadingNormal =
        vertex0.Normal * bary.x +
        vertex1.Normal * bary.y +
        vertex2.Normal * bary.z;
    if (dot(localShadingNormal, localShadingNormal) <= 0.000001)
        localShadingNormal = localGeometricNormal;

    vec2 texCoord00 = vertex0.TexCoord;
    vec2 texCoord01 = vertex1.TexCoord;
    vec2 texCoord02 = vertex2.TexCoord;
    vec2 texCoord10 = vertex0.TexCoord2;
    vec2 texCoord11 = vertex1.TexCoord2;
    vec2 texCoord12 = vertex2.TexCoord2;
    vec2 uv0 = texCoord00 * bary.x + texCoord01 * bary.y + texCoord02 * bary.z;
    vec2 uv1 = texCoord10 * bary.x + texCoord11 * bary.y + texCoord12 * bary.z;
    vec4 vertexColor =
        vertex0.Color * bary.x +
        vertex1.Color * bary.y +
        vertex2.Color * bary.z;

    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    bool doubleSided = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_DOUBLE_SIDED);
    if (!EvaluateGiSidedness(doubleSided, frontFacing))
        return false;

    mat4 worldMatrix = transpose(inverse(instance.WorldMatrixInverseTranspose));
    DdgiMaterialTriangleFootprint footprint;
    footprint.WorldPosition0 = MulRowMajor(vec4(p0, 1.0), worldMatrix).xyz;
    footprint.WorldPosition1 = MulRowMajor(vec4(p1, 1.0), worldMatrix).xyz;
    footprint.WorldPosition2 = MulRowMajor(vec4(p2, 1.0), worldMatrix).xyz;
    footprint.TexCoord00 = texCoord00;
    footprint.TexCoord01 = texCoord01;
    footprint.TexCoord02 = texCoord02;
    footprint.TexCoord10 = texCoord10;
    footprint.TexCoord11 = texCoord11;
    footprint.TexCoord12 = texCoord12;
    float determinantSign = determinant(mat3(worldMatrix)) < 0.0 ? -1.0 : 1.0;
    vec3 geometricNormal = GiSafeNormal(
        MulRowMajor(
            vec4(localGeometricNormal * determinantSign, 0.0),
            instance.WorldMatrixInverseTranspose).xyz,
        normalize(-rayDirection));
    vec3 shadingNormal = GiSafeNormal(
        MulRowMajor(
            vec4(normalize(localShadingNormal), 0.0),
            instance.WorldMatrixInverseTranspose).xyz,
        geometricNormal);
    vec3 canonicalGeometricNormal = geometricNormal;
    float faceSign = frontFacing ? 1.0 : -1.0;
    geometricNormal *= faceSign;
    shadingNormal *= faceSign;

    bool compactRequested = ShouldUseCompactDdgiMaterial(volumeCascadeIndex);
    bool compactTextureFallback =
        compactRequested &&
        !IsCompactDdgiMaterialProfileValid(material) &&
        GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_COMPACT_TEXTURE_FALLBACK);
    if (compactRequested && !compactTextureFallback)
    {
        surface = EvaluateGiCompactSurface(material, geometricNormal, shadingNormal);
        surface.CanonicalGeometricNormal = canonicalGeometricNormal;
        RecordAndSanitizeDdgiMaterialSurface(material, surface);
        if (DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED &&
            DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED &&
            GiMaterialHasFlag(surface.Flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION))
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_THIN_COMPACT_HIT_COUNTER,
                1u);
        }
        RecordDdgiMaterialTransportProvenance(
            instanceIndex,
            primitiveIndex,
            barycentrics,
            MATERIAL_GI_COMPACT_TRANSPORT_HIT_COUNTER);
        RecordDdgiFoliageProxyHit(instance);
        return true;
    }

    // A missing statistic is never relabelled as valid. The explicit
    // correctness fallback pays the detailed sampling cost for this hit,
    // even when the normal cascade policy would otherwise select compact
    // transport.
    bool sampleDetailedMaterialTextures = sampleMaterialTextures || compactTextureFallback;

    vec4 albedoSample = vec4(1.0);
    if (sampleDetailedMaterialTextures && material.AlbedoTextureIndex != DEFAULT_WHITE_TEXTURE)
    {
        vec2 albedoUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.x,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        float albedoLod = ResolveDdgiMaterialTextureLod(
            material.AlbedoTextureIndex,
            material.TextureTexCoordSets.x,
            material.BaseColorOffsetScale,
            footprint,
            volumeCascadeIndex,
            probeSpacing,
            hitDistance,
            rayAngularRadius,
            material.DdgiMaterialPolicy.y);
        albedoSample = SampleDdgiMaterialTexture(
            material.AlbedoTextureIndex,
            albedoUv,
            albedoLod,
            vec4(1.0));
    }
    vec4 metallicRoughnessSample = vec4(1.0);
    if (sampleDetailedMaterialTextures &&
        material.MetallicRoughnessTextureIndex != DEFAULT_BLACK_TEXTURE)
    {
        vec2 metallicRoughnessUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.z,
            material.MetallicRoughnessOffsetScale,
            material.TextureRotations.z);
        float metallicRoughnessLod = ResolveDdgiMaterialTextureLod(
            material.MetallicRoughnessTextureIndex,
            material.TextureTexCoordSets.z,
            material.MetallicRoughnessOffsetScale,
            footprint,
            volumeCascadeIndex,
            probeSpacing,
            hitDistance,
            rayAngularRadius,
            material.DdgiMaterialPolicy.y);
        metallicRoughnessSample = SampleDdgiMaterialTexture(
            material.MetallicRoughnessTextureIndex,
            metallicRoughnessUv,
            metallicRoughnessLod,
            vec4(1.0));
    }

    float occlusionSample = 1.0;
    if (sampleDetailedMaterialTextures &&
        GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_HAS_OCCLUSION_TEXTURE))
    {
        vec2 occlusionUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.OcclusionBinding.y,
            material.OcclusionOffsetScale,
            material.OcclusionBinding.x);
        float occlusionLod = ResolveDdgiMaterialTextureLod(
            material.OcclusionTextureIndex,
            material.OcclusionBinding.y,
            material.OcclusionOffsetScale,
            footprint,
            volumeCascadeIndex,
            probeSpacing,
            hitDistance,
            rayAngularRadius,
            material.DdgiMaterialPolicy.y);
        occlusionSample = SampleDdgiMaterialTexture(
            material.OcclusionTextureIndex,
            occlusionUv,
            occlusionLod,
            vec4(1.0)).r;
    }

    vec3 emissiveSample = vec3(1.0);
    if (sampleDetailedMaterialTextures &&
        GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_HAS_EMISSIVE_TEXTURE))
    {
        vec2 emissiveUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.w,
            material.EmissiveOffsetScale,
            material.TextureRotations.w);
        float emissiveLod = ResolveDdgiMaterialTextureLod(
            material.EmissiveTextureIndex,
            material.TextureTexCoordSets.w,
            material.EmissiveOffsetScale,
            footprint,
            volumeCascadeIndex,
            probeSpacing,
            hitDistance,
            rayAngularRadius,
            material.DdgiMaterialPolicy.y);
        emissiveSample = SampleDdgiMaterialTexture(
            material.EmissiveTextureIndex,
            emissiveUv,
            emissiveLod,
            vec4(1.0)).rgb;
    }

    if (sampleDetailedMaterialTextures &&
        material.NormalTextureIndex != DEFAULT_NORMAL_TEXTURE &&
        material.NormalScaleBias.x > 0.001)
    {
        vec4 localTangent =
            vertex0.Tangent * bary.x +
            vertex1.Tangent * bary.y +
            vertex2.Tangent * bary.z;
        vec3 worldTangent = GiSafeNormal(
            MulRowMajor(vec4(localTangent.xyz, 0.0), worldMatrix).xyz,
            vec3(1.0, 0.0, 0.0));
        worldTangent = GiSafeNormal(
            worldTangent - shadingNormal * dot(shadingNormal, worldTangent),
            vec3(1.0, 0.0, 0.0));
        float tangentHandedness =
            (localTangent.w < 0.0 ? -1.0 : 1.0) * determinantSign * faceSign;
        vec3 worldBitangent = GiSafeNormal(
            cross(shadingNormal, worldTangent) * tangentHandedness,
            vec3(0.0, 0.0, 1.0));
        vec2 normalUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.y,
            material.NormalOffsetScale,
            material.TextureRotations.y);
        float normalLod = ResolveDdgiMaterialTextureLod(
            material.NormalTextureIndex,
            material.TextureTexCoordSets.y,
            material.NormalOffsetScale,
            footprint,
            volumeCascadeIndex,
            probeSpacing,
            hitDistance,
            rayAngularRadius,
            material.DdgiMaterialPolicy.y);
        vec3 tangentNormal = SampleDdgiMaterialTexture(
            material.NormalTextureIndex,
            normalUv,
            normalLod,
            vec4(0.5, 0.5, 1.0, 1.0)).xyz * 2.0 - 1.0;
        if ((material.FeatureFlags & MATERIAL_FEATURE_NORMAL_GREEN_INVERTED) != 0u)
            tangentNormal.y = -tangentNormal.y;
        // BC5 stores only tangent-space X/Y. Match the visible forward path by
        // rebuilding the positive hemisphere instead of interpreting the
        // texture view's absent blue channel as a backwards-facing normal.
        if ((material.FeatureFlags & MATERIAL_FEATURE_COMPRESSED_NORMAL_BC5) != 0u)
            tangentNormal.z = sqrt(max(0.0, 1.0 - dot(tangentNormal.xy, tangentNormal.xy)));
        tangentNormal.xy *= material.NormalScaleBias.x;
        tangentNormal = GiSafeNormal(tangentNormal, vec3(0.0, 0.0, 1.0));
        shadingNormal = GiSafeNormal(
            mat3(worldTangent, worldBitangent, shadingNormal) * tangentNormal,
            shadingNormal);
    }

    bool hasExtensionData = material.FeatureFlags != 0u && material.ExtensionDataIndex >= 0;
    GPUMaterialExtensionData extensionData;
    if (hasExtensionData)
    {
        extensionData = ReadMaterialExtension(uint(material.ExtensionDataIndex));
        // Evaluate base-layer energy using the same independent extension
        // bindings, UV sets, transforms, and channel conventions as forward.
        if (sampleDetailedMaterialTextures &&
            (material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT_TEXTURE) != 0u)
        {
            vec2 extensionUv = MaterialDdgiHitUv(
                uv0, uv1,
                extensionData.ExtensionTextureTexCoordSets0.x,
                extensionData.ClearcoatOffsetScale,
                extensionData.ExtensionTextureRotations0.x);
            float extensionLod = ResolveDdgiMaterialTextureLod(
                extensionData.ClearcoatTextureIndex,
                extensionData.ExtensionTextureTexCoordSets0.x,
                extensionData.ClearcoatOffsetScale,
                footprint,
                volumeCascadeIndex,
                probeSpacing,
                hitDistance,
                rayAngularRadius,
                material.DdgiMaterialPolicy.y);
            extensionData.Clearcoat.x *= SampleDdgiMaterialTexture(
                extensionData.ClearcoatTextureIndex, extensionUv, extensionLod, vec4(1.0)).r;
        }
        if (sampleDetailedMaterialTextures &&
            (material.FeatureFlags & MATERIAL_FEATURE_SHEEN_COLOR_TEXTURE) != 0u)
        {
            vec2 extensionUv = MaterialDdgiHitUv(
                uv0, uv1,
                extensionData.ExtensionTextureTexCoordSets0.w,
                extensionData.SheenColorOffsetScale,
                extensionData.ExtensionTextureRotations0.w);
            float extensionLod = ResolveDdgiMaterialTextureLod(
                extensionData.SheenColorTextureIndex,
                extensionData.ExtensionTextureTexCoordSets0.w,
                extensionData.SheenColorOffsetScale,
                footprint,
                volumeCascadeIndex,
                probeSpacing,
                hitDistance,
                rayAngularRadius,
                material.DdgiMaterialPolicy.y);
            extensionData.SheenColor.rgb *= SampleDdgiMaterialTexture(
                extensionData.SheenColorTextureIndex, extensionUv, extensionLod, vec4(1.0)).rgb;
        }
        if (sampleDetailedMaterialTextures &&
            (material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION_TEXTURE) != 0u)
        {
            vec2 extensionUv = MaterialDdgiHitUv(
                uv0, uv1,
                extensionData.ExtensionTextureTexCoordSets1.z,
                extensionData.TransmissionOffsetScale,
                extensionData.ExtensionTextureRotations1.z);
            float extensionLod = ResolveDdgiMaterialTextureLod(
                extensionData.TransmissionTextureIndex,
                extensionData.ExtensionTextureTexCoordSets1.z,
                extensionData.TransmissionOffsetScale,
                footprint,
                volumeCascadeIndex,
                probeSpacing,
                hitDistance,
                rayAngularRadius,
                material.DdgiMaterialPolicy.y);
            extensionData.Transmission.x *= SampleDdgiMaterialTexture(
                extensionData.TransmissionTextureIndex, extensionUv, extensionLod, vec4(1.0)).r;
        }
        if (sampleDetailedMaterialTextures &&
            (material.FeatureFlags & MATERIAL_FEATURE_SPECULAR_TEXTURE) != 0u)
        {
            vec2 extensionUv = MaterialDdgiHitUv(
                uv0, uv1,
                extensionData.ExtensionTextureTexCoordSets2.x,
                extensionData.SpecularOffsetScale,
                extensionData.ExtensionTextureRotations2.x);
            float extensionLod = ResolveDdgiMaterialTextureLod(
                extensionData.SpecularTextureIndex,
                extensionData.ExtensionTextureTexCoordSets2.x,
                extensionData.SpecularOffsetScale,
                footprint,
                volumeCascadeIndex,
                probeSpacing,
                hitDistance,
                rayAngularRadius,
                material.DdgiMaterialPolicy.y);
            extensionData.SpecularColor.a *= SampleDdgiMaterialTexture(
                extensionData.SpecularTextureIndex, extensionUv, extensionLod, vec4(1.0)).a;
        }
        if (sampleDetailedMaterialTextures &&
            (material.FeatureFlags & MATERIAL_FEATURE_SPECULAR_COLOR_TEXTURE) != 0u)
        {
            vec2 extensionUv = MaterialDdgiHitUv(
                uv0, uv1,
                extensionData.ExtensionTextureTexCoordSets2.y,
                extensionData.SpecularColorOffsetScale,
                extensionData.ExtensionTextureRotations2.y);
            float extensionLod = ResolveDdgiMaterialTextureLod(
                extensionData.SpecularColorTextureIndex,
                extensionData.ExtensionTextureTexCoordSets2.y,
                extensionData.SpecularColorOffsetScale,
                footprint,
                volumeCascadeIndex,
                probeSpacing,
                hitDistance,
                rayAngularRadius,
                material.DdgiMaterialPolicy.y);
            extensionData.SpecularColor.rgb *= SampleDdgiMaterialTexture(
                extensionData.SpecularColorTextureIndex, extensionUv, extensionLod, vec4(1.0)).rgb;
        }
    }
    surface = EvaluateGiTexturedSurface(
        material,
        extensionData,
        hasExtensionData,
        albedoSample,
        metallicRoughnessSample,
        occlusionSample,
        emissiveSample,
        vertexColor,
        geometricNormal,
        shadingNormal,
        normalize(-rayDirection),
        true);
    surface.CanonicalGeometricNormal = canonicalGeometricNormal;
    RecordAndSanitizeDdgiMaterialSurface(material, surface);
    if (DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED &&
        DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED &&
        GiMaterialHasFlag(surface.Flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION))
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_THIN_DETAILED_HIT_COUNTER,
            1u);
    }
    RecordDdgiMaterialTransportProvenance(
        instanceIndex,
        primitiveIndex,
        barycentrics,
        compactTextureFallback
            ? MATERIAL_GI_CORRECTNESS_FALLBACK_HIT_COUNTER
            : MATERIAL_GI_DETAILED_TRANSPORT_HIT_COUNTER);
    RecordDdgiFoliageProxyHit(instance);
    return true;
}

// Resolve coverage once for all ray classes. Primary transport uses the result
// for either a deterministic mask or stable stochastic coverage; visibility
// uses the exact same authored alpha analytically and never turns a blended
// layer into a stochastic blocker.
float ResolveDdgiCandidateCoverageAlpha(
    GPUDdgiRayQueryInstance instance,
    uint primitiveIndex,
    vec2 barycentrics,
    GPUMaterialData material)
{
    uint triangleIndexBase = primitiveIndex * 3u;
    uint i0 = ReadDdgiRayIndex(instance, triangleIndexBase + 0u);
    uint i1 = ReadDdgiRayIndex(instance, triangleIndexBase + 1u);
    uint i2 = ReadDdgiRayIndex(instance, triangleIndexBase + 2u);
    GPUVertex vertex0 = ReadDdgiRayVertex(instance, i0);
    GPUVertex vertex1 = ReadDdgiRayVertex(instance, i1);
    GPUVertex vertex2 = ReadDdgiRayVertex(instance, i2);
    vec3 bary = vec3(
        1.0 - barycentrics.x - barycentrics.y,
        barycentrics.x,
        barycentrics.y);
    vec2 uv0 = vertex0.TexCoord * bary.x +
        vertex1.TexCoord * bary.y +
        vertex2.TexCoord * bary.z;
    vec2 uv1 = vertex0.TexCoord2 * bary.x +
        vertex1.TexCoord2 * bary.y +
        vertex2.TexCoord2 * bary.z;
    float vertexAlpha = clamp(
        vertex0.Color.a * bary.x +
        vertex1.Color.a * bary.y +
        vertex2.Color.a * bary.z,
        0.0,
        1.0);
    float sampledTextureAlpha = 1.0;
    // Ray queries have no derivatives. The authored deterministic DDGI LOD is
    // shared by primary, visibility, and shadow queries so classification
    // cannot disagree merely because the consumer changed.
    if (GiMaterialHasFlag(
            material.TransportFlags,
            GI_MATERIAL_HAS_BASE_COLOR_TEXTURE))
    {
        vec2 uv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.x,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        sampledTextureAlpha = SampleDdgiMaterialTexture(
            material.AlbedoTextureIndex,
            uv,
            max(material.DdgiMaterialPolicy.y, 0.0),
            vec4(1.0)).a;
    }

    return ComposeDdgiCandidateAlpha(
        material.Albedo.a,
        vertexAlpha,
        sampledTextureAlpha);
}

// The TLAS marks alpha-mask, alpha-blend, thin, foliage, and decal instances
// non-opaque. Ordinary opaque geometry remains on Vulkan's fast path. Decals
// are retained separately by primary transport and are never confirmed here.
bool DdgiCandidatePassesOpacityPolicy(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    bool frontFacing,
    bool enforceMaterialSidedness)
{
    GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
    // Invalid V2 metadata is conservative: candidate geometry blocks rather
    // than being interpreted with legacy offsets. Decals are the inverse—an
    // overlay is never confirmed as an occluder by any ray class.
    if (!DdgiRayQueryInstanceIsValid(instance))
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_RAY_METADATA_INVALID_COUNTER,
            1u);
        return true;
    }
    if (DdgiRayGeometryIsDecal(instance))
        return false;

    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    bool opticalBoundary =
        DdgiRayGeometryHasFlag(
            instance, DDGI_RAY_GEOMETRY_FLAG_VOLUME_TRANSMISSION) ||
        DdgiRayGeometryHasFlag(
            instance, DDGI_RAY_GEOMETRY_FLAG_WATER_SURFACE);
    bool doubleSided = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_DOUBLE_SIDED);
    if (enforceMaterialSidedness && !opticalBoundary &&
        !EvaluateGiSidedness(doubleSided, frontFacing))
        return false;
    if (opticalBoundary)
        return true;

    bool alphaMask = DdgiRayGeometryHasFlag(
        instance,
        DDGI_RAY_GEOMETRY_FLAG_ALPHA_MASK);
    bool alphaBlend = DdgiRayGeometryHasFlag(
        instance,
        DDGI_RAY_GEOMETRY_FLAG_ALPHA_BLEND);
    if (!alphaMask && !alphaBlend)
        return true;
    if (alphaMask && !DDGI_HIT_ALPHA_MASK_TRANSPORT_ENABLED)
        return true;

    float effectiveAlpha = ResolveDdgiCandidateCoverageAlpha(
        instance,
        primitiveIndex,
        barycentrics,
        material);
    bool thin = DdgiRayGeometryHasFlag(
        instance,
        DDGI_RAY_GEOMETRY_FLAG_THIN_TRANSMISSION);
    bool covered;
    if (alphaBlend && !thin)
    {
        // Barycentrics are quantized into the primitive identity so two
        // geometrically distinct candidates on a large triangle do not share
        // a coverage decision. Frame number is deliberately absent.
        uvec2 quantizedBary = uvec2(round(clamp(barycentrics, vec2(0.0), vec2(1.0)) * 65535.0));
        uint candidateIdentity = primitiveIndex ^
            (quantizedBary.x * 0x9e3779b9u) ^
            (quantizedBary.y * 0x85ebca6bu);
        float stableRandom = DdgiStableDecisionUnitFloat(
            DDGI_HIT_WORLD_PROBE_STABLE_KEY,
            DDGI_HIT_DIRECTION_RAY_ORDINAL,
            DDGI_HIT_SOURCE_LIGHTING_EPOCH,
            DDGI_HIT_SAMPLING_SEQUENCE_EPOCH,
            DDGI_STOCHASTIC_DOMAIN_ALPHA_COVERAGE,
            instance.StableInstanceIdentity,
            candidateIdentity);
        covered = stableRandom < effectiveAlpha;
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            covered
                ? DDGI_STOCHASTIC_ALPHA_ACCEPT_COUNTER
                : DDGI_STOCHASTIC_ALPHA_REJECT_COUNTER,
            1u);
    }
    else if (alphaBlend)
    {
        // A physical thin-surface contract owns this interaction. It must not
        // also enter the stochastic ordinary-blend mixture. Its alpha is folded
        // into deterministic layer transmittance by visibility queries.
        covered = effectiveAlpha > 0.0;
    }
    else
    {
        covered = MaterialAlphaOccupiesOpaqueTransport(
            effectiveAlpha,
            material.NormalScaleBias.y,
            material.NormalScaleBias.z);
    }
    RecordDdgiAlphaCandidateDiagnostics(instanceIndex, primitiveIndex, !covered);
    return covered;
}

bool DdgiCandidatePassesOpacity(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    bool frontFacing)
{
    return DdgiCandidatePassesOpacityPolicy(
        instanceIndex,
        primitiveIndex,
        barycentrics,
        frontFacing,
        true);
}

bool DdgiCandidatePassesTwoSidedOpacity(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    bool frontFacing)
{
    // Receiver visibility treats a covered architectural shell as an occluder
    // from either side, while retaining the authored alpha-mask coverage.
    return DdgiCandidatePassesOpacityPolicy(
        instanceIndex,
        primitiveIndex,
        barycentrics,
        frontFacing,
        false);
}

vec3 ResolveDdgiThinCandidateTransmittance(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    GPUMaterialData material,
    GPUMaterialExtensionData extensionData)
{
    float transmission = clamp(extensionData.Transmission.x, 0.0, 1.0);
    if (DDGI_HIT_CANDIDATE_MATERIAL_TEXTURES_ALLOWED &&
        GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_HAS_TRANSMISSION_TEXTURE))
    {
        GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
        uint triangleIndexBase = primitiveIndex * 3u;
        uint i0 = ReadDdgiRayIndex(instance, triangleIndexBase + 0u);
        uint i1 = ReadDdgiRayIndex(instance, triangleIndexBase + 1u);
        uint i2 = ReadDdgiRayIndex(instance, triangleIndexBase + 2u);
        GPUVertex vertex0 = ReadDdgiRayVertex(instance, i0);
        GPUVertex vertex1 = ReadDdgiRayVertex(instance, i1);
        GPUVertex vertex2 = ReadDdgiRayVertex(instance, i2);
        vec3 bary = vec3(
            1.0 - barycentrics.x - barycentrics.y,
            barycentrics.x,
            barycentrics.y);
        vec2 uv0 = vertex0.TexCoord * bary.x +
            vertex1.TexCoord * bary.y +
            vertex2.TexCoord * bary.z;
        vec2 uv1 = vertex0.TexCoord2 * bary.x +
            vertex1.TexCoord2 * bary.y +
            vertex2.TexCoord2 * bary.z;
        vec2 transmissionUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            extensionData.ExtensionTextureTexCoordSets1.z,
            extensionData.TransmissionOffsetScale,
            extensionData.ExtensionTextureRotations1.z);
        transmission *= SampleDdgiMaterialTexture(
            extensionData.TransmissionTextureIndex,
            transmissionUv,
            max(material.DdgiMaterialPolicy.y, 0.0),
            vec4(1.0)).r;
    }

    return clamp(extensionData.Dispersion.yzw, vec3(0.0), vec3(1.0)) *
        clamp(transmission, 0.0, 1.0);
}

vec3 TraceLightVisibility(
    vec3 worldPosition,
    vec3 normal,
    vec3 lightDirection,
    float maxDistance,
    float receiverProbeSpacing,
    bool recordAnalyticDirectDiagnostics)
{
    float normalOffset = DDGI_PROBE_TRACE_EPSILON * 4.0;
    float rayTMin = DDGI_PROBE_TRACE_EPSILON * 2.0;
    float rayDistance = max(maxDistance - normalOffset, rayTMin);
    vec3 safeNormal = length(normal) > 0.00001
        ? normalize(normal)
        : vec3(0.0, 1.0, 0.0);
    // Analytic direct injection receives the geometric normal already resolved
    // from the committed probe-ray facing. Preserve that side: choosing it from
    // the light direction can move the origin back through the hit surface and
    // manufacture a same-instance self-shadow. Other visibility callers retain
    // their historical light-facing policy.
    vec3 offsetNormal = recordAnalyticDirectDiagnostics
        ? safeNormal
        : (dot(safeNormal, lightDirection) >= 0.0 ? safeNormal : -safeNormal);
    vec3 origin = worldPosition + offsetNormal * normalOffset;
    vec3 visibilityRgb = vec3(1.0);
    uint thinLayerCount = 0u;
    uint blendedLayerCount = 0u;

    bool recordVisibilityDiagnostics =
        DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED &&
        recordAnalyticDirectDiagnostics;
    if (recordVisibilityDiagnostics)
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_SHADOW_VISIBILITY_RAY_COUNTER,
            1u);
        if (DDGI_HIT_VOLUME_DIAGNOSTICS_ENABLED != 0 &&
            DDGI_HIT_VOLUME_INDEX != 0xffffffffu)
        {
            uint volumeBank = DDGI_HIT_VOLUME_ENERGY_COUNTER_BASE +
                DDGI_HIT_VOLUME_INDEX * DDGI_HIT_VOLUME_ENERGY_COUNTER_STRIDE;
            AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, volumeBank + 11u, 1u);
        }
    }

    rayQueryEXT shadowQuery;
#if DDGI_HIT_BINARY_OPAQUE_SHADOW_FAST_PATH
    // The selected TLAS content class contains neither alpha candidates nor
    // layered transmission. For analytic direct rays retain candidate access
    // solely to reject the originating primitive; other NEE rays can let the
    // implementation accept the first opaque blocker directly. In both cases
    // TerminateOnFirstHit is exact because visibility is binary.
    uint shadowRayFlags = gl_RayFlagsTerminateOnFirstHitEXT |
        (recordAnalyticDirectDiagnostics
            ? gl_RayFlagsNoOpaqueEXT
            : gl_RayFlagsOpaqueEXT);
#else
    const uint shadowRayFlags = gl_RayFlagsNoneEXT;
#endif
    rayQueryInitializeEXT(
        shadowQuery,
        SceneTlas,
        // Sidedness belongs to DdgiCandidatePassesOpacity below. Hardware
        // backface culling would discard the reverse side of authored
        // double-sided/thin cloth before its transmission can be evaluated.
        shadowRayFlags,
        0xff,
        origin,
        rayTMin,
        lightDirection,
        rayDistance);

    uint alphaCandidateCount = 0u;
    while (rayQueryProceedEXT(shadowQuery))
    {
        if (rayQueryGetIntersectionTypeEXT(shadowQuery, false) == gl_RayQueryCandidateIntersectionTriangleEXT)
        {
#if DDGI_HIT_BINARY_OPAQUE_SHADOW_FAST_PATH
            if (recordAnalyticDirectDiagnostics)
            {
                uint instanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(
                    shadowQuery,
                    false);
                uint primitiveIndex = rayQueryGetIntersectionPrimitiveIndexEXT(
                    shadowQuery,
                    false);
                if (instanceIndex == DDGI_HIT_RECEIVER_INSTANCE_INDEX &&
                    primitiveIndex == DDGI_HIT_RECEIVER_PRIMITIVE_INDEX)
                {
                    continue;
                }
            }
            rayQueryConfirmIntersectionEXT(shadowQuery);
#else
            uint instanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(shadowQuery, false);
            uint primitiveIndex = rayQueryGetIntersectionPrimitiveIndexEXT(shadowQuery, false);
            vec2 barycentrics = rayQueryGetIntersectionBarycentricsEXT(shadowQuery, false);
            bool frontFacing = rayQueryGetIntersectionFrontFaceEXT(shadowQuery, false);
            // The receiver's BSDF/BTDF already accounts for this exact surface
            // interaction. In particular, a transmitted-light query begins on
            // the probe-facing side of thin cloth and otherwise crosses the
            // originating triangle again, squaring its colored transmittance.
            // Keep other triangles from the same curtain instance eligible so
            // folds and genuinely stacked cloth layers still attenuate light.
            if (recordAnalyticDirectDiagnostics &&
                instanceIndex == DDGI_HIT_RECEIVER_INSTANCE_INDEX &&
                primitiveIndex == DDGI_HIT_RECEIVER_PRIMITIVE_INDEX)
            {
                continue;
            }

            GPUDdgiRayQueryInstance instance =
                ReadDdgiRayQueryInstance(instanceIndex);
            // Decal geometry is an overlay source only. It cannot consume the
            // visibility candidate/layer budget and never blocks a light ray.
            if (DdgiRayQueryInstanceIsValid(instance) &&
                DdgiRayGeometryIsDecal(instance))
            {
                continue;
            }

            alphaCandidateCount++;
            if (alphaCandidateCount > DDGI_HIT_ALPHA_CANDIDATE_LIMIT)
            {
                RecordDdgiAlphaCandidateLimitReached();
                rayQueryConfirmIntersectionEXT(shadowQuery);
                rayQueryTerminateEXT(shadowQuery);
                break;
            }

            // Invalid metadata is fail-closed. Do not dereference material or
            // vertex payload under an ABI that did not validate.
            if (!DdgiRayQueryInstanceIsValid(instance))
            {
                rayQueryConfirmIntersectionEXT(shadowQuery);
                rayQueryTerminateEXT(shadowQuery);
                break;
            }

            GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
            bool doubleSided = GiMaterialHasFlag(
                material.TransportFlags,
                GI_MATERIAL_DOUBLE_SIDED);
            if (!EvaluateGiSidedness(doubleSided, frontFacing))
                continue;

            bool thin = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED &&
                DdgiRayGeometryHasFlag(
                    instance,
                    DDGI_RAY_GEOMETRY_FLAG_THIN_TRANSMISSION) &&
                GiMaterialHasFlag(
                    material.TransportFlags,
                    GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
            bool ordinaryBlend = !thin && DdgiRayGeometryHasFlag(
                instance,
                DDGI_RAY_GEOMETRY_FLAG_ALPHA_BLEND);
            if (ordinaryBlend)
            {
                // Visibility integrates expected front-to-back coverage. Stable
                // stochastic acceptance is reserved for primary transport; a
                // cached light path therefore never flickers between blockers.
                float coverage = ResolveDdgiCandidateCoverageAlpha(
                    instance,
                    primitiveIndex,
                    barycentrics,
                    material);
                visibilityRgb *= vec3(1.0 - coverage);
                blendedLayerCount++;
                AddRendererDiagnostic(
                    DDGI_HIT_CURRENT_FRAME_INDEX,
                    DDGI_TRANSPARENT_VISIBILITY_LAYER_COUNTER,
                    1u);
                RecordDdgiAlphaCandidateDiagnostics(
                    instanceIndex,
                    primitiveIndex,
                    coverage <= 0.0);
                if (blendedLayerCount >=
                    DDGI_HIT_TRANSPARENCY_LAYER_LIMIT)
                {
                    AddRendererDiagnostic(
                        DDGI_HIT_CURRENT_FRAME_INDEX,
                        DDGI_TRANSPARENT_VISIBILITY_LIMIT_COUNTER,
                        1u);
                    visibilityRgb = vec3(0.0);
                    break;
                }
                if (max(visibilityRgb.r, max(visibilityRgb.g, visibilityRgb.b)) < 0.01)
                {
                    break;
                }
                continue;
            }

            if (!DdgiCandidatePassesOpacity(
                    instanceIndex,
                    primitiveIndex,
                    barycentrics,
                    frontFacing))
            {
                continue;
            }

            if (thin && material.ExtensionDataIndex >= 0)
            {
                GPUMaterialExtensionData extensionData =
                    ReadMaterialExtension(uint(material.ExtensionDataIndex));
                vec3 layerTransmission = ResolveDdgiThinCandidateTransmittance(
                    instanceIndex,
                    primitiveIndex,
                    barycentrics,
                    material,
                    extensionData);
                if (DdgiRayGeometryHasFlag(
                        instance,
                        DDGI_RAY_GEOMETRY_FLAG_ALPHA_BLEND))
                {
                    float coverage = ResolveDdgiCandidateCoverageAlpha(
                        instance,
                        primitiveIndex,
                        barycentrics,
                        material);
                    layerTransmission = mix(
                        vec3(1.0),
                        layerTransmission,
                        coverage);
                }
                visibilityRgb *= layerTransmission;
                thinLayerCount++;
                if (thinLayerCount >= DDGI_HIT_TRANSPARENCY_LAYER_LIMIT)
                {
                    if (DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED)
                        AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_SHADOW_LAYER_LIMIT_COUNTER, 1u);
                    visibilityRgb = vec3(0.0);
                    break;
                }
                if (max(visibilityRgb.r, max(visibilityRgb.g, visibilityRgb.b)) < 0.01)
                {
                    if (DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED)
                        AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_SHADOW_LOW_TRANSMITTANCE_COUNTER, 1u);
                    break;
                }
                continue;
            }

            rayQueryConfirmIntersectionEXT(shadowQuery);
            rayQueryTerminateEXT(shadowQuery);
            break;
#endif
        }
    }

    uint hitType = rayQueryGetIntersectionTypeEXT(shadowQuery, true);
    bool occluded = hitType != gl_RayQueryCommittedIntersectionNoneEXT;
    if (thinLayerCount > 0u && DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED)
    {
        AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_SHADOW_TRANSMISSION_RAY_COUNTER, 1u);
        AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_SHADOW_TOTAL_LAYER_COUNTER, thinLayerCount);
        MaxRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_SHADOW_MAX_LAYER_COUNTER, thinLayerCount);
    }
    if (occluded && recordVisibilityDiagnostics)
    {
        float committedHitDistance = max(
            rayQueryGetIntersectionTEXT(shadowQuery, true),
            0.0);
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_SHADOW_VISIBILITY_OCCLUDED_COUNTER,
            1u);
        if (committedHitDistance < max(receiverProbeSpacing, 0.001))
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_SHADOW_VISIBILITY_NEAR_HIT_COUNTER,
                1u);
        }
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_SHADOW_VISIBILITY_HIT_DISTANCE_COUNTER,
            uint(round(clamp(committedHitDistance, 0.0, 256.0) *
                DDGI_SHADOW_VISIBILITY_HIT_DISTANCE_SCALE)));
        if (DDGI_HIT_VOLUME_DIAGNOSTICS_ENABLED != 0 &&
            DDGI_HIT_VOLUME_INDEX != 0xffffffffu)
        {
            uint volumeBank = DDGI_HIT_VOLUME_ENERGY_COUNTER_BASE +
                DDGI_HIT_VOLUME_INDEX * DDGI_HIT_VOLUME_ENERGY_COUNTER_STRIDE;
            AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, volumeBank + 12u, 1u);
            uint distanceBucket = committedHitDistance < rayTMin
                ? 13u
                : (committedHitDistance < 2.0 * normalOffset
                    ? 14u
                    : (committedHitDistance < max(receiverProbeSpacing, 0.001)
                        ? 15u
                        : 16u));
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                volumeBank + distanceBucket,
                1u);
            uint committedInstanceIndex =
                rayQueryGetIntersectionInstanceCustomIndexEXT(shadowQuery, true);
            if (DDGI_HIT_RECEIVER_INSTANCE_INDEX != 0xffffffffu &&
                committedInstanceIndex == DDGI_HIT_RECEIVER_INSTANCE_INDEX)
            {
                AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, volumeBank + 17u, 1u);
            }
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                volumeBank + 18u,
                uint(round(clamp(committedHitDistance, 0.0, 256.0) *
                    DDGI_SHADOW_VISIBILITY_HIT_DISTANCE_SCALE)));
        }
    }
    return occluded ? vec3(0.0) : visibilityRgb;
}

vec3 SampleDdgiEnvironmentMissRadianceWithFallback(
    vec3 direction,
    vec3 fallbackRadianceBase,
    float fallbackIntensity)
{
    float skyWeight = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 fallbackRadiance = fallbackRadianceBase *
        max(fallbackIntensity, 0.0) * skyWeight;
    GPUEnvironmentData environment = ReadGiEnvironmentData();
    if (environment.Enabled == 0u)
        return fallbackRadiance;
    // A probe miss is physical transport from the visible sky, not the
    // screen-space diffuse IBL complement.  Keep it radiometrically consistent
    // with the skybox even when DiffuseIntensity is reduced to avoid double
    // counting ambient light at receivers already owned by DDGI.
    vec3 environmentRadiance = EvaluateEnvironmentRadiance(
        environment,
        direction,
        true,
        false,
        false);
    return max(environmentRadiance, vec3(0.0));
}

void ApplyDdgiSteppedAtmosphereDirectionalLight(
    uint lightIndex,
    inout GPULight light)
{
    if (light.Type != 1)
        return;

    GPUEnvironmentData currentEnvironment = ReadEnvironmentData();
    GPUEnvironmentData giEnvironment = ReadGiEnvironmentData();
    if (currentEnvironment.Enabled == 0u ||
        giEnvironment.Enabled == 0u ||
        !EnvironmentUsesAnalyticSky(currentEnvironment) ||
        !EnvironmentUsesAnalyticSky(giEnvironment))
    {
        return;
    }

    if (lightIndex == pc.PrimaryDirectionalLightIndex)
    {
        light.Direction = -normalize(giEnvironment.SunDirectionAndAngularRadius.xyz);
        light.Color = max(giEnvironment.SunRadianceAndElevation.xyz, vec3(0.0));
        light.Intensity = 1.0;
        return;
    }

    // The atmosphere-owned moon has no dedicated public light slot in the
    // scene ABI. Match both its continuous direction and radiance before
    // substituting the stepped snapshot, so an authored fill light that merely
    // points in a similar direction is never captured by this policy.
    vec3 currentMoonDirection = -normalize(
        currentEnvironment.MoonDirectionAndAngularRadius.xyz);
    vec3 currentMoonRadiance = max(
        currentEnvironment.MoonRadianceAndNightBlend.xyz,
        vec3(0.0));
    vec3 lightRadiance = max(light.Color, vec3(0.0)) *
        max(light.Intensity, 0.0);
    float radianceTolerance = max(0.001, length(currentMoonRadiance) * 0.02);
    bool atmosphereMoon = dot(normalize(light.Direction), currentMoonDirection) >
            cos(radians(0.05)) &&
        length(lightRadiance - currentMoonRadiance) <= radianceTolerance;
    if (!atmosphereMoon)
        return;

    light.Direction = -normalize(giEnvironment.MoonDirectionAndAngularRadius.xyz);
    light.Color = max(giEnvironment.MoonRadianceAndNightBlend.xyz, vec3(0.0));
    light.Intensity = 1.0;
}

#if DDGI_HIT_ENABLE_ENVIRONMENT_WRAPPER
vec3 SampleDdgiEnvironmentMissRadiance(vec3 direction)
{
    return SampleDdgiEnvironmentMissRadianceWithFallback(
        direction,
        pc.EnvironmentRadianceAndIntensity.rgb,
        pc.EnvironmentRadianceAndIntensity.w);
}
#endif

#if DDGI_HIT_USE_SELECTED_LIGHTS
bool TryReadSelectedDdgiDirectionalLight(out GPULight selectedLight)
{
    if (pc.DirectionalLightCount == 0u ||
        pc.PrimaryDirectionalLightIndex == DDGI_INVALID_LIGHT_INDEX ||
        pc.PrimaryDirectionalLightIndex >= pc.LightCount)
        return false;

    selectedLight = ReadLight(pc.PrimaryDirectionalLightIndex);
    ApplyDdgiSteppedAtmosphereDirectionalLight(
        pc.PrimaryDirectionalLightIndex,
        selectedLight);
    return selectedLight.Type == 1;
}

bool TryBuildSelectedDdgiLocalLightContribution(
    vec3 worldPosition,
    vec3 normal,
    bool twoSidedDiffuse,
    out GPULight light,
    out vec3 lightDirection,
    out float distanceToLight,
    out float attenuation)
{
    if (pc.LocalLightCount == 0u ||
        pc.SelectedLocalLightIndex == DDGI_INVALID_LIGHT_INDEX ||
        pc.SelectedLocalLightIndex >= pc.LightCount)
        return false;

    light = ReadLight(pc.SelectedLocalLightIndex);
    if (light.Type == GPU_LIGHT_TYPE_DIRECTIONAL || NjulfIsAreaLight(light))
        return false;

    vec3 toLight = light.Position - worldPosition;
    distanceToLight = length(toLight);
    if (distanceToLight >= light.Range || light.Range <= 0.0)
        return false;

    lightDirection = toLight / max(distanceToLight, 0.0001);
    float nDotL = twoSidedDiffuse
        ? abs(dot(normal, lightDirection))
        : max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return false;

    attenuation = EvaluateNjulfLightDistanceAttenuation(
        light,
        distanceToLight);
    attenuation *= EvaluateNjulfIesProfile(light, -lightDirection);
    if (light.Type == GPU_LIGHT_TYPE_SPOT)
        attenuation *= EvaluateNjulfSpotAttenuation(light, lightDirection);

    attenuation *= max(pc.SelectedLocalLightEnergyScale, 0.0);
    return attenuation > 0.0;
}
#endif

float DdgiHitLuminance(vec3 value)
{
    return dot(max(value, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
}

vec3 EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    GPULight light,
    vec3 lightDirection,
    float visibilityDistance,
    float attenuation,
    float receiverProbeSpacing,
    out vec3 noShadowDiffuse)
{
    noShadowDiffuse = vec3(0.0);
    bool transmitted = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED &&
        GiMaterialHasFlag(surface.Flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION) &&
        dot(surface.GeometricNormal, lightDirection) < 0.0;
    float nDotL = transmitted
        ? max(dot(-surface.ShadingNormal, lightDirection), 0.0)
        : max(dot(surface.ShadingNormal, lightDirection), 0.0);
    float nDotV = max(dot(surface.ShadingNormal, viewDirection), 0.0);
    if (nDotL <= 0.0)
        return vec3(0.0);

    vec3 incomingRadiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    noShadowDiffuse = transmitted
        ? incomingRadiance * nDotL *
            (surface.TransmittedDiffuseReflectance / GI_MATERIAL_PI)
        : incomingRadiance * nDotL *
            EvaluateGiDiffuseBrdf(
                surface.DirectionalDiffuseBase,
                surface.DielectricF0,
                nDotL,
                nDotV);
    if (DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED &&
        GiMaterialHasFlag(surface.Flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION))
    {
        uint luminance = uint(round(clamp(
            DdgiHitLuminance(noShadowDiffuse) * DDGI_THIN_LUMINANCE_SCALE,
            0.0,
            4294967040.0)));
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            transmitted
                ? DDGI_THIN_TRANSMITTED_DIRECT_LUMINANCE_COUNTER
                : DDGI_THIN_REFLECTED_DIRECT_LUMINANCE_COUNTER,
            luminance);
    }
    if (DdgiHitLuminance(noShadowDiffuse) <= 0.0001)
        return vec3(0.0);

    if ((uint(light.ShadowFlags) & GPU_LIGHT_SHADOW_FLAG_CASTS_SHADOWS) == 0u)
        return noShadowDiffuse;

    vec3 tracedVisibility = TraceLightVisibility(
        worldPosition,
        // GeometricNormal is resolved from the probe ray's committed facing.
        // CanonicalGeometricNormal is authored orientation and flipping it
        // toward the light can offset the shadow origin back through the exact
        // surface the probe ray just hit.
        surface.GeometricNormal,
        lightDirection,
        visibilityDistance,
        receiverProbeSpacing,
        true);
    // DDGI is the transport reference: shadow strength is an artistic raster
    // control, not a source of unoccluded direct energy behind geometry.
    return noShadowDiffuse * tracedVisibility;
}

#if DDGI_HIT_CONTENT_DEPENDENT_LOCAL_LIGHTS
const uint DDGI_LOCAL_LIGHT_MODE_AUTO = 0u;
const uint DDGI_LOCAL_LIGHT_MODE_EXACT = 1u;
const uint DDGI_LOCAL_LIGHT_MODE_TREE = 2u;
const uint DDGI_LOCAL_LIGHT_MODE_LEGACY_TOP_K_REFERENCE = 3u;

bool DdgiFiniteLocalLight(GPULight light)
{
    return all(not(isnan(light.Position))) && all(not(isinf(light.Position))) &&
        all(not(isnan(light.Color))) && all(not(isinf(light.Color))) &&
        !isnan(light.Intensity) && !isinf(light.Intensity);
}

bool DdgiTryBuildLocalLightContribution(
    vec3 worldPosition,
    GiSurfaceSample surface,
    GPULight light,
    out vec3 lightDirection,
    out float distanceToLight,
    out float attenuation)
{
    lightDirection = vec3(0.0, 1.0, 0.0);
    distanceToLight = 0.0;
    attenuation = 0.0;
    if (light.Type == GPU_LIGHT_TYPE_DIRECTIONAL || NjulfIsAreaLight(light) ||
        !NjulfIsPunctualLight(light) || !DdgiFiniteLocalLight(light) ||
        isnan(light.Range) || light.Range <= 0.0 || light.Intensity <= 0.0 ||
        DdgiHitLuminance(light.Color) <= 0.0)
    {
        return false;
    }

    vec3 toLight = light.Position - worldPosition;
    float distanceSquared = dot(toLight, toLight);
    if (isnan(distanceSquared) || isinf(distanceSquared) ||
        distanceSquared >= light.Range * light.Range)
        return false;
    distanceToLight = sqrt(max(distanceSquared, 0.0));
    lightDirection = distanceToLight > 1e-5
        ? toLight / distanceToLight
        : surface.ShadingNormal;
    bool thinSurface = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED &&
        GiMaterialHasFlag(surface.Flags, GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    float nDotL = thinSurface
        ? abs(dot(surface.ShadingNormal, lightDirection))
        : max(dot(surface.ShadingNormal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return false;

    attenuation = EvaluateNjulfLightDistanceAttenuation(
        light,
        distanceToLight);
    attenuation *= EvaluateNjulfIesProfile(light, -lightDirection);
    if (light.Type == GPU_LIGHT_TYPE_SPOT)
    {
        attenuation *= EvaluateNjulfSpotAttenuation(
            light,
            lightDirection);
    }
    return attenuation > 0.0 && !isnan(attenuation) && !isinf(attenuation);
}

void DdgiEvaluateDirectionalLightsExact(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    float receiverProbeSpacing,
    inout vec3 radiance,
    inout vec3 noShadowRadiance)
{
    for (uint lightIndex = 0u; lightIndex < pc.LightCount; lightIndex++)
    {
        GPULight light = ReadLight(lightIndex);
        if (light.Type != 1 ||
            any(isnan(light.Color)) || any(isinf(light.Color)) ||
            isnan(light.Intensity) || isinf(light.Intensity) ||
            all(lessThanEqual(max(light.Color, vec3(0.0)), vec3(0.0))) ||
            !(light.Intensity > 0.0) ||
            any(isnan(light.Direction)) || any(isinf(light.Direction)) ||
            dot(light.Direction, light.Direction) <= 1e-10)
        {
            continue;
        }
        ApplyDdgiSteppedAtmosphereDirectionalLight(lightIndex, light);
        vec3 lightNoShadow;
        radiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            surface,
            viewDirection,
            light,
            normalize(-light.Direction),
            DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE,
            1.0,
            receiverProbeSpacing,
            lightNoShadow);
        noShadowRadiance += lightNoShadow;
    }
}

void DdgiEvaluateLocalLightsExact(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    float receiverProbeSpacing,
    inout vec3 radiance,
    inout vec3 noShadowRadiance)
{
    for (uint lightIndex = 0u; lightIndex < pc.LightCount; lightIndex++)
    {
        GPULight light = ReadLight(lightIndex);
        if (NjulfIsAreaLight(light))
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_MANY_LIGHT_EXACT_LIGHT_EVALUATION_COUNTER,
                1u);
            vec3 lightNoShadow;
            radiance += EvaluateDdgiAreaLightDiffuseRadianceAtHit(
                worldPosition,
                surface,
                viewDirection,
                light,
                lightIndex,
                1.0,
                receiverProbeSpacing,
                lightNoShadow);
            noShadowRadiance += lightNoShadow;
            continue;
        }
        vec3 lightDirection;
        float distanceToLight;
        float attenuation;
        if (!DdgiTryBuildLocalLightContribution(
            worldPosition,
            surface,
            light,
            lightDirection,
            distanceToLight,
            attenuation))
        {
            continue;
        }
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_EXACT_LIGHT_EVALUATION_COUNTER,
            1u);
        vec3 lightNoShadow;
        radiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            surface,
            viewDirection,
            light,
            lightDirection,
            distanceToLight,
            attenuation,
            receiverProbeSpacing,
            lightNoShadow);
        noShadowRadiance += lightNoShadow;
    }
}

bool DdgiEvaluateLocalLightTreeSamples(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    float receiverProbeSpacing,
    uint sampleCount,
    out vec3 radiance,
    out vec3 noShadowRadiance)
{
    radiance = vec3(0.0);
    noShadowRadiance = vec3(0.0);
    DdgiLightTreeState state = DdgiReadLightTreeState();
    if ((state.flags & DDGI_LIGHT_TREE_STATE_VALID_BIT) == 0u ||
        state.leafCount == 0u || state.leafCount > DDGI_LIGHT_TREE_MAX_LEAVES ||
        state.paddedLeafCount == 0u || state.nodeCount == 0u ||
        !DdgiLightTreeStateMatchesCurrentLightBuffer(
            state,
            pc.LightCount,
            pc.LocalLightCount))
    {
        return false;
    }

#if NJULF_DDGI_DETAILED_COUNTERS
    uint sampledLeafOrdinals[64];
#endif
    for (uint sampleOrdinal = 0u; sampleOrdinal < sampleCount; sampleOrdinal++)
    {
        DdgiLightTreeSample lightSample = DdgiSampleLocalLightTree(
            worldPosition,
            DDGI_HIT_WORLD_PROBE_STABLE_KEY,
            DDGI_HIT_DIRECTION_RAY_ORDINAL,
            DDGI_HIT_SOURCE_LIGHTING_EPOCH,
            DDGI_HIT_SAMPLING_SEQUENCE_EPOCH,
            sampleOrdinal,
            DDGI_HIT_UNIFORM_LIGHT_MIXTURE);
        if (!lightSample.valid ||
            lightSample.packedLightIndex >= pc.LightCount ||
            !(lightSample.pdf > 0.0) ||
            isnan(lightSample.pdf) || isinf(lightSample.pdf))
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_MANY_LIGHT_INVALID_SAMPLE_PDF_COUNTER,
                1u);
            return false;
        }

        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_SAMPLED_LIGHT_COUNTER,
            1u);
        if (lightSample.repairedInvalidBound)
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_MANY_LIGHT_UNIFORM_REPAIR_COUNTER,
                1u);
        }
#if NJULF_DDGI_DETAILED_COUNTERS
        bool duplicate = false;
        for (uint previousOrdinal = 0u;
             previousOrdinal < sampleOrdinal;
             previousOrdinal++)
        {
            duplicate = duplicate ||
                sampledLeafOrdinals[previousOrdinal] == lightSample.leafOrdinal;
        }
        sampledLeafOrdinals[sampleOrdinal] = lightSample.leafOrdinal;
        if (duplicate)
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_MANY_LIGHT_DUPLICATE_DRAW_COUNTER,
                1u);
        }
        float negativeLog2Pdf = clamp(-log2(lightSample.pdf), 0.0, 32.0);
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_PDF_SUM_COUNTER,
            uint(round(clamp(
                lightSample.pdf * DDGI_MANY_LIGHT_PDF_SCALE,
                0.0,
                4294967040.0))));
        uint quantizedNegativeLog2Pdf = uint(round(
            negativeLog2Pdf * DDGI_MANY_LIGHT_LOG_PDF_SCALE));
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_NEGATIVE_LOG2_PDF_SUM_COUNTER,
            quantizedNegativeLog2Pdf);
        MaxRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_MAX_NEGATIVE_LOG2_PDF_COUNTER,
            quantizedNegativeLog2Pdf);
#endif

        GPULight light = ReadLight(lightSample.packedLightIndex);
        // Packed indices are allowed to change after topology edits. The stable
        // identity turns a stale publication into an exact fallback instead of
        // silently sampling the wrong light.
        if (light.Type == GPU_LIGHT_TYPE_DIRECTIONAL ||
            light.StableIdentity != lightSample.stableLightIdentity)
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_MANY_LIGHT_INVALID_SAMPLE_PDF_COUNTER,
                1u);
            return false;
        }

        vec3 lightNoShadow;
        vec3 contribution;
        if (NjulfIsAreaLight(light))
        {
            contribution = EvaluateDdgiAreaLightDiffuseRadianceAtHit(
                worldPosition,
                surface,
                viewDirection,
                light,
                sampleOrdinal,
                1.0,
                receiverProbeSpacing,
                lightNoShadow);
        }
        else
        {
            vec3 lightDirection;
            float distanceToLight;
            float attenuation;
            if (!DdgiTryBuildLocalLightContribution(
                worldPosition,
                surface,
                light,
                lightDirection,
                distanceToLight,
                attenuation))
            {
                AddRendererDiagnostic(
                    DDGI_HIT_CURRENT_FRAME_INDEX,
                    DDGI_MANY_LIGHT_REJECTED_ZERO_TERM_COUNTER,
                    1u);
                continue;
            }
            contribution = EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
                worldPosition,
                surface,
                viewDirection,
                light,
                lightDirection,
                distanceToLight,
                attenuation,
                receiverProbeSpacing,
                lightNoShadow);
        }
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_VISIBILITY_EVALUATION_COUNTER,
            1u);
        float inverseEstimatorPdf = 1.0 /
            (float(sampleCount) * lightSample.pdf);
        if (isnan(inverseEstimatorPdf) || isinf(inverseEstimatorPdf) ||
            !(inverseEstimatorPdf > 0.0))
        {
            AddRendererDiagnostic(
                DDGI_HIT_CURRENT_FRAME_INDEX,
                DDGI_MANY_LIGHT_INVALID_SAMPLE_PDF_COUNTER,
                1u);
            return false;
        }
#if NJULF_DDGI_DETAILED_COUNTERS
        MaxRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_MAX_ESTIMATOR_WEIGHT_COUNTER,
            uint(round(clamp(
                inverseEstimatorPdf *
                    DDGI_MANY_LIGHT_ESTIMATOR_WEIGHT_SCALE,
                0.0,
                4294967040.0))));
#endif
        // Samples are drawn with replacement. Duplicate leaves remain
        // independent contributions and are deliberately not collapsed.
        radiance += contribution * inverseEstimatorPdf;
        noShadowRadiance += lightNoShadow * inverseEstimatorPdf;
    }
    return true;
}

vec3 DdgiEvaluateContentDependentDirectDiffuseRadianceAtHit(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    float receiverProbeSpacing,
    out vec3 directNoShadowDiffuse)
{
    vec3 radiance = vec3(0.0);
    directNoShadowDiffuse = vec3(0.0);
    DdgiEvaluateDirectionalLightsExact(
        worldPosition,
        surface,
        viewDirection,
        receiverProbeSpacing,
        radiance,
        directNoShadowDiffuse);

#if DDGI_HIT_LOCAL_LIGHTS_ENABLED
    if (pc.LocalLightCount == 0u)
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_BYPASS_HIT_COUNTER,
            1u);
        return radiance;
    }

    uint mode = DDGI_HIT_LOCAL_SAMPLING_MODE;
    bool exact = mode == DDGI_LOCAL_LIGHT_MODE_EXACT ||
        pc.LocalLightCount <= DDGI_HIT_EXACT_LOCAL_LIGHT_THRESHOLD;
    if (exact)
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_EXACT_HIT_COUNTER,
            1u);
        DdgiEvaluateLocalLightsExact(
            worldPosition,
            surface,
            viewDirection,
            receiverProbeSpacing,
            radiance,
            directNoShadowDiffuse);
        return radiance;
    }

    // Keep malformed/manual shader variants inside the bounded duplicate-tracking
    // storage even though the settings contract already clamps this value.
    uint sampleCount = min(DDGI_HIT_MAX_SHADED_LIGHTS, 64u);
    if (sampleCount == 0u)
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_TREE_FALLBACK_HIT_COUNTER,
            1u);
        DdgiEvaluateLocalLightsExact(
            worldPosition,
            surface,
            viewDirection,
            receiverProbeSpacing,
            radiance,
            directNoShadowDiffuse);
        return radiance;
    }
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        DDGI_MANY_LIGHT_TREE_ATTEMPT_HIT_COUNTER,
        1u);
    vec3 sampledRadiance;
    vec3 sampledNoShadow;
    if (DdgiEvaluateLocalLightTreeSamples(
        worldPosition,
        surface,
        viewDirection,
        receiverProbeSpacing,
        sampleCount,
        sampledRadiance,
        sampledNoShadow))
    {
        AddRendererDiagnostic(
            DDGI_HIT_CURRENT_FRAME_INDEX,
            DDGI_MANY_LIGHT_TREE_SUCCESS_HIT_COUNTER,
            1u);
        radiance += sampledRadiance;
        directNoShadowDiffuse += sampledNoShadow;
        return radiance;
    }

    // Invalid/stale/unpublished hierarchy: preserve correctness with the
    // all-lights oracle. This can cost more, but never drops supported energy.
    AddRendererDiagnostic(
        DDGI_HIT_CURRENT_FRAME_INDEX,
        DDGI_MANY_LIGHT_TREE_FALLBACK_HIT_COUNTER,
        1u);
    DdgiEvaluateLocalLightsExact(
        worldPosition,
        surface,
        viewDirection,
        receiverProbeSpacing,
        radiance,
        directNoShadowDiffuse);
#endif
    return radiance;
}
#endif

float DdgiEmissiveRandom(inout uint state)
{
    state = state * 1664525u + 1013904223u;
    return float(state >> 8u) * (1.0 / 16777216.0);
}

uint DdgiEmissiveHierarchyLeafCapacity(uint sourceCount)
{
    uint capacity = 1u;
    while (capacity < sourceCount)
        capacity <<= 1u;
    return capacity;
}

GPUDdgiEmissiveSource ReadDdgiEmissiveHierarchyNode(
    uint sourceCount,
    uint nodeIndex)
{
    // Hierarchy nodes immediately follow the live source prefix in the same
    // 64-byte-record storage buffer.
    return ReadDdgiEmissiveSource(sourceCount + nodeIndex);
}

float DdgiEmissiveMaximumCosineWithinCone(
    vec3 referenceAxis,
    vec3 coneAxis,
    float coneHalfAngle)
{
    float cosine = clamp(dot(
        GiSafeNormal(referenceAxis, vec3(0.0, 1.0, 0.0)),
        GiSafeNormal(coneAxis, vec3(0.0, 1.0, 0.0))), -1.0, 1.0);
    float angle = acos(cosine);
    if (angle <= coneHalfAngle)
        return 1.0;
    return max(cos(min(angle - coneHalfAngle, 3.14159265359)), 0.0);
}

float EvaluateDdgiEmissiveHierarchyNodeImportance(
    GPUDdgiEmissiveSource node,
    vec3 receiverPosition,
    vec3 receiverNormal)
{
    const uint NodeValid = 1u << 0u;
    const uint NodeContainsDoubleSided = 1u << 1u;
    const uint NodeConeUnbounded = 1u << 2u;
    const float ImportanceFloor = 1.0 / 1024.0;

    uint nodeFlags = floatBitsToUint(node.Edge2AliasFlags.w);
    float power = node.Vertex0Area.w;
    if ((nodeFlags & NodeValid) == 0u ||
        !(power > 0.0) || isnan(power) || isinf(power))
    {
        return 0.0;
    }

    vec3 minimum = node.Vertex0Area.xyz;
    vec3 maximum = node.Edge1AliasProbability.xyz;
    vec3 center = (minimum + maximum) * 0.5;
    float radius = max(length(maximum - minimum) * 0.5, 1e-4);
    vec3 toCenter = center - receiverPosition;
    float centerDistance = length(toCenter);
    float directionConeAngle;
    vec3 centerDirection;
    if (!(centerDistance > radius) || isnan(centerDistance) || isinf(centerDistance))
    {
        directionConeAngle = 3.14159265359;
        centerDirection = receiverNormal;
    }
    else
    {
        directionConeAngle = asin(clamp(radius / centerDistance, 0.0, 1.0));
        centerDirection = toCenter / centerDistance;
    }

    float receiverBound = DdgiEmissiveMaximumCosineWithinCone(
        receiverNormal,
        centerDirection,
        directionConeAngle);
    float sourceBound = 1.0;
    if ((nodeFlags & (NodeContainsDoubleSided | NodeConeUnbounded)) == 0u)
    {
        vec3 sourceAxis = GiSafeNormal(
            node.Edge2AliasFlags.xyz,
            vec3(0.0, 1.0, 0.0));
        float sourceConeAngle = acos(clamp(
            node.Edge1AliasProbability.w,
            -1.0,
            1.0));
        sourceBound = DdgiEmissiveMaximumCosineWithinCone(
            sourceAxis,
            -centerDirection,
            min(sourceConeAngle + directionConeAngle, 3.14159265359));
    }

    vec3 delta = max(max(minimum - receiverPosition, vec3(0.0)),
        receiverPosition - maximum);
    float distanceSquared = dot(delta, delta);
    float scale = max(radius * radius * 1e-4, 1e-6);
    distanceSquared = max(distanceSquared, scale);
    float angularBound = max(receiverBound * sourceBound, ImportanceFloor);
    float importance = power * angularBound / distanceSquared;
    return importance > 0.0 && !isnan(importance) && !isinf(importance)
        ? importance
        : 0.0;
}

bool SampleDdgiEmissiveHierarchy(
    vec3 receiverPosition,
    vec3 receiverNormal,
    uint sourceCount,
    inout uint seed,
    out uint selectedIndex,
    out float selectionProbability)
{
    selectedIndex = 0u;
    selectionProbability = 0.0;
    if (sourceCount == 0u)
        return false;

    uint leafCapacity = DdgiEmissiveHierarchyLeafCapacity(sourceCount);
    uint leafBase = leafCapacity - 1u;
    uint nodeIndex = 0u;
    float probability = 1.0;
    while (nodeIndex < leafBase)
    {
        uint leftIndex = nodeIndex * 2u + 1u;
        uint rightIndex = leftIndex + 1u;
        float leftWeight = EvaluateDdgiEmissiveHierarchyNodeImportance(
            ReadDdgiEmissiveHierarchyNode(sourceCount, leftIndex),
            receiverPosition,
            receiverNormal);
        float rightWeight = EvaluateDdgiEmissiveHierarchyNodeImportance(
            ReadDdgiEmissiveHierarchyNode(sourceCount, rightIndex),
            receiverPosition,
            receiverNormal);
        float totalWeight = leftWeight + rightWeight;
        if (!(totalWeight > 0.0) || isnan(totalWeight) || isinf(totalWeight))
            return false;

        float leftProbability = clamp(leftWeight / totalWeight, 0.0, 1.0);
        bool chooseLeft = DdgiEmissiveRandom(seed) < leftProbability;
        probability *= chooseLeft ? leftProbability : 1.0 - leftProbability;
        nodeIndex = chooseLeft ? leftIndex : rightIndex;
    }

    selectedIndex = nodeIndex - leafBase;
    if (selectedIndex >= sourceCount ||
        !(probability > 0.0) || isnan(probability) || isinf(probability))
    {
        selectedIndex = 0u;
        selectionProbability = 0.0;
        return false;
    }

    selectionProbability = probability;
    return true;
}

float EvaluateDdgiEmissiveHierarchyProbability(
    vec3 receiverPosition,
    vec3 receiverNormal,
    uint sourceCount,
    uint sourceIndex)
{
    if (sourceCount == 0u || sourceIndex >= sourceCount)
        return 0.0;

    uint leafCapacity = DdgiEmissiveHierarchyLeafCapacity(sourceCount);
    uint nodeIndex = 0u;
    uint rangeStart = 0u;
    uint rangeSize = leafCapacity;
    float probability = 1.0;
    while (rangeSize > 1u)
    {
        uint leftIndex = nodeIndex * 2u + 1u;
        uint rightIndex = leftIndex + 1u;
        float leftWeight = EvaluateDdgiEmissiveHierarchyNodeImportance(
            ReadDdgiEmissiveHierarchyNode(sourceCount, leftIndex),
            receiverPosition,
            receiverNormal);
        float rightWeight = EvaluateDdgiEmissiveHierarchyNodeImportance(
            ReadDdgiEmissiveHierarchyNode(sourceCount, rightIndex),
            receiverPosition,
            receiverNormal);
        float totalWeight = leftWeight + rightWeight;
        if (!(totalWeight > 0.0) || isnan(totalWeight) || isinf(totalWeight))
            return 0.0;

        uint halfRange = rangeSize >> 1u;
        bool chooseLeft = sourceIndex < rangeStart + halfRange;
        float leftProbability = clamp(leftWeight / totalWeight, 0.0, 1.0);
        probability *= chooseLeft ? leftProbability : 1.0 - leftProbability;
        if (chooseLeft)
        {
            nodeIndex = leftIndex;
        }
        else
        {
            nodeIndex = rightIndex;
            rangeStart += halfRange;
        }
        rangeSize = halfRange;
    }

    return probability > 0.0 && !isnan(probability) && !isinf(probability)
        ? probability
        : 0.0;
}

uint SampleDdgiEmissiveGlobalAlias(uint sourceCount, inout uint seed)
{
    const uint EmissiveSourceAliasIndexMask = 0x0000ffffu;
    float columnSample = DdgiEmissiveRandom(seed);
    float aliasSample = DdgiEmissiveRandom(seed);
    uint column = min(uint(columnSample * float(sourceCount)), sourceCount - 1u);
    GPUDdgiEmissiveSource columnSource = ReadDdgiEmissiveSource(column);
    uint packedAliasFlags = floatBitsToUint(columnSource.Edge2AliasFlags.w);
    uint aliasIndex = min(
        packedAliasFlags & EmissiveSourceAliasIndexMask,
        sourceCount - 1u);
    return aliasSample < clamp(columnSource.Edge1AliasProbability.w, 0.0, 1.0)
        ? column
        : aliasIndex;
}

void DdgiMacroBasis(vec3 axis, out vec3 tangent, out vec3 bitangent)
{
    axis = GiSafeNormal(axis, vec3(0.0, 1.0, 0.0));
    vec3 helper = abs(axis.y) < 0.9
        ? vec3(0.0, 1.0, 0.0)
        : vec3(1.0, 0.0, 0.0);
    tangent = GiSafeNormal(cross(helper, axis), vec3(1.0, 0.0, 0.0));
    bitangent = GiSafeNormal(cross(axis, tangent), vec3(0.0, 0.0, 1.0));
}

vec3 SampleDdgiMacroEmitterPosition(
    GPUDdgiEmissiveSource source,
    uint sourceFlags,
    inout uint seed)
{
    const uint MacroShapeMask = 0x0f00u;
    vec3 center = source.Vertex0Area.xyz;
    float radius = max(source.Vertex0Area.w, 1e-4);
    vec3 axis = GiSafeNormal(source.Edge1AliasProbability.xyz, vec3(0.0, 1.0, 0.0));
    float axialExtent = max(source.Edge2AliasFlags.x, 0.0);
    float secondaryExtent = max(source.Edge2AliasFlags.y, 0.0);
    uint shape = (sourceFlags & MacroShapeMask) >> 8u;
    vec3 tangent;
    vec3 bitangent;
    DdgiMacroBasis(axis, tangent, bitangent);

    float u0 = DdgiEmissiveRandom(seed);
    float u1 = DdgiEmissiveRandom(seed);
    float u2 = DdgiEmissiveRandom(seed);
    float angle = 6.28318530718 * u0;
    vec2 diskDirection = vec2(cos(angle), sin(angle));

    if (shape == 4u) // line / beam
        return center + axis * ((u1 * 2.0 - 1.0) * axialExtent);

    if (shape == 2u) // capsule/cylindrical volume
    {
        vec2 disk = diskDirection * (radius * sqrt(u1));
        return center + axis * ((u2 * 2.0 - 1.0) * axialExtent) +
            tangent * disk.x + bitangent * disk.y;
    }

    if (shape == 3u) // cone volume
    {
        float heightT = pow(max(u2, 0.0), 1.0 / 3.0);
        float localRadius = radius * heightT * sqrt(u1);
        float axial = (heightT * 2.0 - 1.0) * axialExtent;
        return center + axis * axial +
            tangent * (diskDirection.x * localRadius) +
            bitangent * (diskDirection.y * localRadius);
    }

    if (shape == 5u) // disk
    {
        float diskRadius = radius * sqrt(u1);
        return center + tangent * (diskDirection.x * diskRadius) +
            bitangent * (diskDirection.y * diskRadius) +
            axis * ((u2 * 2.0 - 1.0) * secondaryExtent);
    }

    if (shape == 6u) // bounded volume
    {
        vec3 extents = vec3(radius, max(axialExtent, 1e-4), max(secondaryExtent, 1e-4));
        return center + (vec3(u0, u1, u2) * 2.0 - vec3(1.0)) * extents;
    }

    // Sphere volume is the robust default for point and unknown authored
    // shapes. Cuberoot radial sampling preserves uniform power density.
    float z = 1.0 - 2.0 * u1;
    float radial = sqrt(max(1.0 - z * z, 0.0));
    vec3 direction = vec3(radial * diskDirection.x, z, radial * diskDirection.y);
    return center + direction * (radius * pow(max(u2, 0.0), 1.0 / 3.0));
}

vec3 EvaluateDdgiMacroEmitterDiffuseRadiance(
    GPUDdgiEmissiveSource source,
    uint sourceFlags,
    float sourceSelectionProbability,
    vec3 worldPosition,
    GiSurfaceSample surface,
    float nDotV,
    float receiverProbeSpacing,
    inout uint seed)
{
    vec3 samplePosition = SampleDdgiMacroEmitterPosition(source, sourceFlags, seed);
    vec3 toSource = samplePosition - worldPosition;
    float distanceSquared = dot(toSource, toSource);
    if (!(distanceSquared > 0.000004) || isnan(distanceSquared) || isinf(distanceSquared))
        return vec3(0.0);

    float distanceToSource = sqrt(distanceSquared);
    vec3 lightDirection = toSource / distanceToSource;
    float receiverCosine = max(dot(surface.ShadingNormal, lightDirection), 0.0);
    if (receiverCosine <= 0.0)
        return vec3(0.0);

    // IntegratedPower is distributed according to the exact analytic sample
    // above. Its conditional spatial density cancels the matching normalized
    // emission density; only source-selection probability remains in the
    // one-source estimator.
    vec3 integratedPower = max(source.RadianceSelectionProbability.rgb, vec3(0.0));
    vec3 incidentIrradiance = integratedPower *
        (receiverCosine /
         max(12.5663706144 * distanceSquared * sourceSelectionProbability, 1e-10));
    vec3 contribution = incidentIrradiance * EvaluateGiDiffuseBrdf(
        surface.DirectionalDiffuseBase,
        surface.DielectricF0,
        receiverCosine,
        nDotV);
    vec3 visibility = TraceLightVisibility(
        worldPosition,
        surface.GeometricNormal,
        lightDirection,
        distanceToSource,
        receiverProbeSpacing,
        false);
    return contribution * visibility;
}

vec3 EvaluateDdgiDynamicEmissiveSurfaceRadiance(
    uint sourceIndex,
    vec3 barycentrics)
{
    GPUDdgiEmissiveSurface sourceSurface =
        ReadDdgiEmissiveSurface(sourceIndex);
    uint materialIndex = floatBitsToUint(
        sourceSurface.MaterialAndVertexAlpha.x);
    GPUMaterialData material = ReadMaterial(materialIndex);

    vec2 uv00 = sourceSurface.Uv0Vertex01.xy;
    vec2 uv01 = sourceSurface.Uv0Vertex01.zw;
    vec2 uv02 = sourceSurface.Uv0Vertex2Uv1Vertex0.xy;
    vec2 uv10 = sourceSurface.Uv0Vertex2Uv1Vertex0.zw;
    vec2 uv11 = sourceSurface.Uv1Vertex12.xy;
    vec2 uv12 = sourceSurface.Uv1Vertex12.zw;
    vec2 uv0 = uv00 * barycentrics.x +
        uv01 * barycentrics.y +
        uv02 * barycentrics.z;
    vec2 uv1 = uv10 * barycentrics.x +
        uv11 * barycentrics.y +
        uv12 * barycentrics.z;

    float vertexAlpha = clamp(dot(
        sourceSurface.MaterialAndVertexAlpha.yzw,
        barycentrics), 0.0, 1.0);
    float policyLod = max(material.DdgiMaterialPolicy.y, 0.0);
    if (DecodeMaterialAlphaMode(material.NormalScaleBias.y) ==
        MATERIAL_ALPHA_MODE_MASK)
    {
        float sampledTextureAlpha = 1.0;
        if (GiMaterialHasFlag(
                material.TransportFlags,
                GI_MATERIAL_HAS_BASE_COLOR_TEXTURE))
        {
            vec2 alphaUv = MaterialDdgiHitUv(
                uv0,
                uv1,
                material.TextureTexCoordSets.x,
                material.BaseColorOffsetScale,
                material.TextureRotations.x);
            sampledTextureAlpha = SampleDdgiMaterialTexture(
                material.AlbedoTextureIndex,
                alphaUv,
                policyLod,
                vec4(1.0)).a;
        }
        if (!DdgiAlphaCandidateOccupiesOpaqueTransport(
                material.Albedo.a,
                vertexAlpha,
                sampledTextureAlpha,
                material.NormalScaleBias.y,
                material.NormalScaleBias.z))
        {
            return vec3(0.0);
        }
    }

    vec3 emissiveTexture = vec3(1.0);
    if (GiMaterialHasFlag(
            material.TransportFlags,
            GI_MATERIAL_HAS_EMISSIVE_TEXTURE))
    {
        vec2 emissiveUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.w,
            material.EmissiveOffsetScale,
            material.TextureRotations.w);
        emissiveTexture = SampleDdgiMaterialTexture(
            material.EmissiveTextureIndex,
            emissiveUv,
            policyLod,
            vec4(1.0)).rgb;
    }

    vec3 radiance = max(material.Emissive.rgb, vec3(0.0)) *
        max(emissiveTexture, vec3(0.0));
    return any(isnan(radiance)) || any(isinf(radiance))
        ? vec3(0.0)
        : radiance;
}

vec3 EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    float receiverProbeSpacing)
{
#if !DDGI_HIT_EMISSIVE_SOURCES_ENABLED
    return vec3(0.0);
#else
    // Estimator ownership: this function is next-event estimation at the
    // receiver. It never replaces direct surface-hit emission and never owns
    // cached recursive irradiance. CPU policy uploads exactly one NEE mode:
    // triangle alias sampling or the proxy rollback estimator.
    if (pc.EmissiveSourceCount == 0u)
        return vec3(0.0);
    float nDotV = max(dot(
        surface.ShadingNormal,
        GiSafeNormal(viewDirection, surface.ShadingNormal)), 0.0);
    if (nDotV <= 0.0)
        return vec3(0.0);
    RecordDdgiEmissiveSamplingInvocation(worldPosition);

    const uint EmissiveSourceAliasIndexMask = 0x0000ffffu;
    const uint EmissiveSourceFlagsShift = 16u;
    const uint EmissiveSourceTriangleFlag = 1u << 0u;
    const uint EmissiveSourceDoubleSidedFlag = 1u << 1u;
    const uint EmissiveSourceProxyRollbackFlag = 1u << 4u;
    const uint EmissiveSourceSpatialHierarchyFlag = 1u << 5u;
    const uint EmissiveSourceMacroEmitterFlag = 1u << 6u;
    const uint EmissiveSourceDynamicTextureFlag = 1u << 7u;

    GPUDdgiEmissiveSource firstSource = ReadDdgiEmissiveSource(0u);
    uint firstPackedAliasFlags = floatBitsToUint(firstSource.Edge2AliasFlags.w);
    uint firstFlags = firstPackedAliasFlags >> EmissiveSourceFlagsShift;
    if ((firstFlags & EmissiveSourceProxyRollbackFlag) != 0u)
    {
        // Rollback estimator retained for operational safety. The CPU never
        // uploads proxy and triangle records together.
        const uint MaxEvaluatedProxySources = 64u;
        uint sourceCount = min(pc.EmissiveSourceCount, MaxEvaluatedProxySources);
        vec3 diffuseRadiance = vec3(0.0);
        vec3 dominantContribution = vec3(0.0);
        vec3 dominantLightDirection = vec3(0.0, 1.0, 0.0);
        float dominantDistance = 0.0;
        float dominantLuminance = 0.0;
        for (uint sourceIndex = 0u; sourceIndex < sourceCount; sourceIndex++)
        {
            GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(sourceIndex);
            vec3 toSource = source.Vertex0Area.xyz - worldPosition;
            float distanceToSource = length(toSource);
            float radius = max(source.Vertex0Area.w, 0.001);
            if (distanceToSource >= radius)
                continue;

            vec3 lightDirection = toSource / max(distanceToSource, 0.0001);
            float nDotL = max(dot(surface.ShadingNormal, lightDirection), 0.0);
            if (nDotL <= 0.0)
                continue;

            float radiusAttenuation = 1.0 - distanceToSource / radius;
            radiusAttenuation *= radiusAttenuation;
            vec3 contribution = max(source.Edge1AliasProbability.rgb, vec3(0.0)) *
                nDotL *
                radiusAttenuation *
                EvaluateGiDiffuseBrdf(
                    surface.DirectionalDiffuseBase,
                    surface.DielectricF0,
                    nDotL,
                    nDotV);
            diffuseRadiance += contribution;
            float contributionLuminance = DdgiHitLuminance(contribution);
            if (contributionLuminance > dominantLuminance)
            {
                dominantContribution = contribution;
                dominantLightDirection = lightDirection;
                dominantDistance = distanceToSource;
                dominantLuminance = contributionLuminance;
            }
        }

        if (dominantLuminance <= 0.0001)
            return diffuseRadiance;
        vec3 dominantVisibility = TraceLightVisibility(
            worldPosition,
            surface.GeometricNormal,
            dominantLightDirection,
            dominantDistance,
            receiverProbeSpacing,
            false);
        return diffuseRadiance + dominantContribution * (dominantVisibility - vec3(1.0));
    }

    if ((firstFlags & (EmissiveSourceTriangleFlag | EmissiveSourceMacroEmitterFlag)) == 0u)
        return vec3(0.0);

    // A deterministic per-hit seed drives either the point-dependent spatial
    // hierarchy or the global Vose alias support floor, followed by one
    // uniform-area triangle sample. Both technique probabilities are evaluated
    // for the selected leaf, yielding the exact mixture PDF.
    uint seed =
        floatBitsToUint(worldPosition.x) * 0x9e3779b9u ^
        floatBitsToUint(worldPosition.y) * 0x85ebca6bu ^
        floatBitsToUint(worldPosition.z) * 0xc2b2ae35u ^
        pc.CurrentFrameIndex * 0x27d4eb2du;
    seed ^= seed >> 16u;
    seed *= 0x7feb352du;
    seed ^= seed >> 15u;

    uint sourceCount = pc.EmissiveSourceCount;
    uint selectedIndex = 0u;
    float hierarchySelectionProbability = 0.0;
    bool hierarchyAvailable =
        (firstFlags & EmissiveSourceSpatialHierarchyFlag) != 0u;
    const float HierarchyTechniqueProbability = 0.875;

    // Local helpers are defined below as macros cannot carry inout RNG state
    // safely across all GLSL compilers used by the build matrix.
    float techniqueSample = DdgiEmissiveRandom(seed);
    if (hierarchyAvailable && techniqueSample < HierarchyTechniqueProbability)
    {
        hierarchyAvailable = SampleDdgiEmissiveHierarchy(
            worldPosition,
            surface.ShadingNormal,
            sourceCount,
            seed,
            selectedIndex,
            hierarchySelectionProbability);
    }

    if (!hierarchyAvailable || techniqueSample >= HierarchyTechniqueProbability)
    {
        selectedIndex = SampleDdgiEmissiveGlobalAlias(sourceCount, seed);
        if (hierarchyAvailable)
        {
            hierarchySelectionProbability = EvaluateDdgiEmissiveHierarchyProbability(
                worldPosition,
                surface.ShadingNormal,
                sourceCount,
                selectedIndex);
        }
    }

    GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(selectedIndex);
    uint sourceFlags = floatBitsToUint(source.Edge2AliasFlags.w) >> EmissiveSourceFlagsShift;

    float globalSelectionProbability = max(
        source.RadianceSelectionProbability.w,
        1e-10);
    float selectionProbability = hierarchyAvailable &&
            hierarchySelectionProbability > 0.0
        ? (1.0 - HierarchyTechniqueProbability) * globalSelectionProbability +
            HierarchyTechniqueProbability * hierarchySelectionProbability
        : globalSelectionProbability;

    if ((sourceFlags & EmissiveSourceMacroEmitterFlag) != 0u)
    {
        return EvaluateDdgiMacroEmitterDiffuseRadiance(
            source,
            sourceFlags,
            max(selectionProbability, 1e-10),
            worldPosition,
            surface,
            nDotV,
            receiverProbeSpacing,
            seed);
    }

    float u2 = DdgiEmissiveRandom(seed);
    float u3 = DdgiEmissiveRandom(seed);
    float sqrtU2 = sqrt(clamp(u2, 0.0, 1.0));
    float bary1 = sqrtU2 * (1.0 - u3);
    float bary2 = sqrtU2 * u3;
    vec3 sourceBarycentrics = vec3(1.0 - bary1 - bary2, bary1, bary2);
    vec3 samplePosition =
        source.Vertex0Area.xyz +
        source.Edge1AliasProbability.xyz * bary1 +
        source.Edge2AliasFlags.xyz * bary2;
    vec3 toSource = samplePosition - worldPosition;
    float distanceSquared = dot(toSource, toSource);
    if (distanceSquared <= 0.000004)
        return vec3(0.0);

    float distanceToSource = sqrt(distanceSquared);
    vec3 lightDirection = toSource / distanceToSource;
    float receiverCosine = max(dot(surface.ShadingNormal, lightDirection), 0.0);
    if (receiverCosine <= 0.0)
        return vec3(0.0);

    vec3 sourceNormal = GiSafeNormal(
        cross(source.Edge1AliasProbability.xyz, source.Edge2AliasFlags.xyz),
        vec3(0.0, 1.0, 0.0));
    float sourceCosine = dot(sourceNormal, -lightDirection);
    if ((sourceFlags & EmissiveSourceDoubleSidedFlag) != 0u)
        sourceCosine = abs(sourceCosine);
    else
        sourceCosine = max(sourceCosine, 0.0);
    if (sourceCosine <= 0.0)
        return vec3(0.0);

    float area = max(source.Vertex0Area.w, 1e-10);
    selectionProbability = max(selectionProbability, 1e-10);
    vec3 sourceRadiance = (sourceFlags & EmissiveSourceDynamicTextureFlag) != 0u
        ? EvaluateDdgiDynamicEmissiveSurfaceRadiance(selectedIndex, sourceBarycentrics)
        : max(source.RadianceSelectionProbability.rgb, vec3(0.0));
    vec3 incidentIrradiance = sourceRadiance *
        (receiverCosine * sourceCosine * area /
         max(distanceSquared * selectionProbability, 1e-10));
    // This is direct illumination from an area emitter. Material AO is a
    // receiver-side indirect term and must not attenuate direct light or the
    // emitter radiance itself.
    vec3 contribution = incidentIrradiance * EvaluateGiDiffuseBrdf(
        surface.DirectionalDiffuseBase,
        surface.DielectricF0,
        receiverCosine,
        nDotV);
    vec3 visibility = TraceLightVisibility(
        worldPosition,
        surface.GeometricNormal,
        lightDirection,
        distanceToSource,
        receiverProbeSpacing,
        false);
    return contribution * visibility;
#endif
}

vec3 EvaluateDirectDiffuseRadianceAtHit(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    float receiverProbeSpacing,
    out vec3 directNoShadowDiffuse)
{
    vec3 directDiffuseRadiance = vec3(0.0);
    directNoShadowDiffuse = vec3(0.0);

#if DDGI_HIT_ONE_DIRECTIONAL_LIGHT_ONLY
    if (pc.PrimaryDirectionalLightIndex == 0xffffffffu ||
        pc.PrimaryDirectionalLightIndex >= pc.LightCount)
    {
        return directDiffuseRadiance;
    }
    GPULight directionalLight = ReadLight(pc.PrimaryDirectionalLightIndex);
    ApplyDdgiSteppedAtmosphereDirectionalLight(
        pc.PrimaryDirectionalLightIndex,
        directionalLight);
    if (directionalLight.Type != 1)
        return directDiffuseRadiance;
    vec3 lightNoShadowDiffuse;
    directDiffuseRadiance = EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
        worldPosition,
        surface,
        viewDirection,
        directionalLight,
        normalize(-directionalLight.Direction),
        DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE,
        1.0,
        receiverProbeSpacing,
        lightNoShadowDiffuse);
    directNoShadowDiffuse = lightNoShadowDiffuse;
#elif DDGI_HIT_USE_SELECTED_LIGHTS
    uint selectedLightCapacity = min(DDGI_HIT_MAX_SHADED_LIGHTS, DDGI_MAX_SELECTED_HIT_LIGHTS);
    uint selectedLightCount = 0u;
    if (selectedLightCapacity == 0u || pc.LightCount == 0u)
        return directDiffuseRadiance;

    GPULight directionalLight;
    if (TryReadSelectedDdgiDirectionalLight(directionalLight))
    {
        vec3 lightDirection = normalize(-directionalLight.Direction);
        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            surface,
            viewDirection,
            directionalLight,
            lightDirection,
            DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE,
            1.0,
            receiverProbeSpacing,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
        selectedLightCount++;
    }

    if (selectedLightCount >= selectedLightCapacity)
        return directDiffuseRadiance;

#if DDGI_HIT_LOCAL_LIGHTS_ENABLED
    GPULight localLight = pc.SelectedLocalLightIndex < pc.LightCount
        ? ReadLight(pc.SelectedLocalLightIndex)
        : ReadLight(0u);
    vec3 localLightDirection;
    float localLightDistance;
    float localLightAttenuation;
    bool thinSurface = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED && GiMaterialHasFlag(
        surface.Flags,
        GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    if (pc.SelectedLocalLightIndex < pc.LightCount &&
        NjulfIsAreaLight(localLight))
    {
        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateDdgiAreaLightDiffuseRadianceAtHit(
            worldPosition,
            surface,
            viewDirection,
            localLight,
            pc.SelectedLocalLightIndex,
            max(pc.SelectedLocalLightEnergyScale, 0.0),
            receiverProbeSpacing,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
    }
    else if (TryBuildSelectedDdgiLocalLightContribution(
        worldPosition,
        surface.ShadingNormal,
        thinSurface,
        localLight,
        localLightDirection,
        localLightDistance,
        localLightAttenuation))
    {
        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            surface,
            viewDirection,
            localLight,
            localLightDirection,
            localLightDistance,
            localLightAttenuation,
            receiverProbeSpacing,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
    }
#endif
#else
#if DDGI_HIT_CONTENT_DEPENDENT_LOCAL_LIGHTS
    if (DDGI_HIT_LOCAL_SAMPLING_ENABLED &&
        DDGI_HIT_LOCAL_SAMPLING_MODE !=
            DDGI_LOCAL_LIGHT_MODE_LEGACY_TOP_K_REFERENCE)
    {
        return DdgiEvaluateContentDependentDirectDiffuseRadianceAtHit(
            worldPosition,
            surface,
            viewDirection,
            receiverProbeSpacing,
            directNoShadowDiffuse);
    }
#endif
    // Ray-query DDGI is memory-bound, so evaluate a bounded ALU-only estimate
    // for the local-light set before spending shadow rays. Directional lights
    // remain first-class transport contributors and bypass the ranking.
    uint selectedLightCapacity = min(DDGI_HIT_DIRECT_LIGHT_CAP, DDGI_HIT_TOP_LIGHT_LIMIT);
    if (selectedLightCapacity == 0u || pc.LightCount == 0u)
        return directDiffuseRadiance;

    uint shadedLightCount = 0u;
    uint candidateCount = min(pc.LightCount, DDGI_HIT_LIGHT_CANDIDATE_LIMIT);
    for (uint i = 0u; i < candidateCount && shadedLightCount < selectedLightCapacity; i++)
    {
        GPULight light = ReadLight(i);
        if (light.Type != 1)
            continue;
        ApplyDdgiSteppedAtmosphereDirectionalLight(i, light);

        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            surface,
            viewDirection,
            light,
            normalize(-light.Direction),
            DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE,
            1.0,
            receiverProbeSpacing,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
        shadedLightCount++;
    }

#if DDGI_HIT_LOCAL_LIGHTS_ENABLED
    uint localCapacity = selectedLightCapacity - shadedLightCount;
    if (localCapacity == 0u)
        return directDiffuseRadiance;

    GPULight selectedLocalLights[DDGI_HIT_TOP_LIGHT_LIMIT];
    vec3 selectedDirections[DDGI_HIT_TOP_LIGHT_LIMIT];
    float selectedDistances[DDGI_HIT_TOP_LIGHT_LIMIT];
    float selectedAttenuations[DDGI_HIT_TOP_LIGHT_LIMIT];
    float selectedImportance[DDGI_HIT_TOP_LIGHT_LIMIT];
    uint selectedLocalCount = 0u;
    for (uint i = 0u; i < candidateCount; i++)
    {
        GPULight light = ReadLight(i);
        if (light.Type == 1)
            continue;

        vec3 toLight = light.Position - worldPosition;
        float distanceToLight = length(toLight);
        if (NjulfIsAreaLight(light))
        {
            vec3 axis;
            vec3 up;
            vec3 right;
            if (!NjulfBuildLightFrame(light, axis, up, right))
                continue;
            float closestDistance = NjulfAreaClosestDistance(
                light, worldPosition, axis, up, right);
            if (closestDistance >= light.Range || light.Range <= 0.0)
                continue;
        }
        else if (distanceToLight >= light.Range || light.Range <= 0.0)
        {
            continue;
        }
        vec3 lightDirection = toLight / max(distanceToLight, 0.0001);
        float nDotL = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED && GiMaterialHasFlag(
                surface.Flags,
                GI_MATERIAL_THIN_SURFACE_TRANSMISSION)
            ? abs(dot(surface.ShadingNormal, lightDirection))
            : max(dot(surface.ShadingNormal, lightDirection), 0.0);
        if (nDotL <= 0.0)
            continue;

        float attenuation = NjulfIsAreaLight(light)
            ? EvaluateNjulfFiniteRangeWindow(
                max(distanceToLight - NjulfAreaBoundingRadius(light), 0.0),
                light.Range)
            : EvaluateNjulfLightDistanceAttenuation(light, distanceToLight) *
                EvaluateNjulfIesProfile(light, -lightDirection);
        if (light.Type == GPU_LIGHT_TYPE_SPOT)
            attenuation *= EvaluateNjulfSpotAttenuation(light, lightDirection);
        float sourceWeight = NjulfIsAreaLight(light)
            ? NJULF_LTC_PI * NjulfAreaSurfaceArea(light)
            : 1.0;
        float importance = DdgiHitLuminance(max(light.Color, vec3(0.0)) *
            max(light.Intensity, 0.0)) * sourceWeight * attenuation * nDotL;
        if (importance <= 0.000001)
            continue;

        uint insertIndex = selectedLocalCount;
        if (insertIndex < localCapacity)
            selectedLocalCount++;
        else if (importance <= selectedImportance[localCapacity - 1u])
            continue;
        else
            insertIndex = localCapacity - 1u;

        while (insertIndex > 0u && importance > selectedImportance[insertIndex - 1u])
        {
            selectedLocalLights[insertIndex] = selectedLocalLights[insertIndex - 1u];
            selectedDirections[insertIndex] = selectedDirections[insertIndex - 1u];
            selectedDistances[insertIndex] = selectedDistances[insertIndex - 1u];
            selectedAttenuations[insertIndex] = selectedAttenuations[insertIndex - 1u];
            selectedImportance[insertIndex] = selectedImportance[insertIndex - 1u];
            insertIndex--;
        }
        selectedLocalLights[insertIndex] = light;
        selectedDirections[insertIndex] = lightDirection;
        selectedDistances[insertIndex] = distanceToLight;
        selectedAttenuations[insertIndex] = attenuation;
        selectedImportance[insertIndex] = importance;
    }

    for (uint i = 0u; i < selectedLocalCount; i++)
    {
        vec3 lightNoShadowDiffuse;
        if (NjulfIsAreaLight(selectedLocalLights[i]))
        {
            directDiffuseRadiance += EvaluateDdgiAreaLightDiffuseRadianceAtHit(
                worldPosition,
                surface,
                viewDirection,
                selectedLocalLights[i],
                i,
                1.0,
                receiverProbeSpacing,
                lightNoShadowDiffuse);
        }
        else
        {
            directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
                worldPosition,
                surface,
                viewDirection,
                selectedLocalLights[i],
                selectedDirections[i],
                selectedDistances[i],
                selectedAttenuations[i],
                receiverProbeSpacing,
                lightNoShadowDiffuse);
        }
        directNoShadowDiffuse += lightNoShadowDiffuse;
    }
#endif
#endif

    return directDiffuseRadiance;
}

#endif
