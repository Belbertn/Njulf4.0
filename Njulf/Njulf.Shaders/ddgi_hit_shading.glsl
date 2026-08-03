#ifndef NJULF_DDGI_HIT_SHADING_GLSL
#define NJULF_DDGI_HIT_SHADING_GLSL

#include "gi_material_transport.glsl"
#include "ddgi_alpha_coverage.glsl"

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
const uint DDGI_HIT_ALPHA_CANDIDATE_LIMIT = 64u;
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
    uint bufferIndex = uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX);
    uvec4 header = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord);
    GPUDdgiRayQueryInstance instance;
    instance.VertexOffset = header.x;
    instance.IndexOffset = header.y;
    instance.MaterialIndex = header.z;
    instance.Padding0 = header.w;
    instance.WorldMatrixInverseTranspose = ReadStorageAlignedMat4Uniform(
        bufferIndex,
        baseWord + 4u);
    return instance;
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
    uint triangleIndexBase = instance.IndexOffset + primitiveIndex * 3u;
    uint i0 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 0u);
    uint i1 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 1u);
    uint i2 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 2u);
    uint v0 = instance.VertexOffset + i0;
    uint v1 = instance.VertexOffset + i1;
    uint v2 = instance.VertexOffset + i2;

    vec3 bary = vec3(
        1.0 - barycentrics.x - barycentrics.y,
        barycentrics.x,
        barycentrics.y);

    vec3 p0 = ReadSplitVertexPosition(v0);
    vec3 p1 = ReadSplitVertexPosition(v1);
    vec3 p2 = ReadSplitVertexPosition(v2);
    vec3 localGeometricNormal = cross(p1 - p0, p2 - p0);
    localGeometricNormal = dot(localGeometricNormal, localGeometricNormal) > 0.000001
        ? normalize(localGeometricNormal)
        : vec3(0.0, 1.0, 0.0);
    vec3 localShadingNormal =
        ReadSplitVertexNormal(v0) * bary.x +
        ReadSplitVertexNormal(v1) * bary.y +
        ReadSplitVertexNormal(v2) * bary.z;
    if (dot(localShadingNormal, localShadingNormal) <= 0.000001)
        localShadingNormal = localGeometricNormal;

    vec2 texCoord00 = ReadSplitVertexTexCoord(v0);
    vec2 texCoord01 = ReadSplitVertexTexCoord(v1);
    vec2 texCoord02 = ReadSplitVertexTexCoord(v2);
    vec2 texCoord10 = ReadSplitVertexTexCoord2(v0);
    vec2 texCoord11 = ReadSplitVertexTexCoord2(v1);
    vec2 texCoord12 = ReadSplitVertexTexCoord2(v2);
    vec2 uv0 = texCoord00 * bary.x + texCoord01 * bary.y + texCoord02 * bary.z;
    vec2 uv1 = texCoord10 * bary.x + texCoord11 * bary.y + texCoord12 * bary.z;
    vec4 vertexColor =
        ReadSplitVertexColor(v0) * bary.x +
        ReadSplitVertexColor(v1) * bary.y +
        ReadSplitVertexColor(v2) * bary.z;

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
            ReadSplitVertexTangent(v0) * bary.x +
            ReadSplitVertexTangent(v1) * bary.y +
            ReadSplitVertexTangent(v2) * bary.z;
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
    return true;
}

// The TLAS marks alpha-mask instances non-opaque. Only those intersections take
// this path; ordinary opaque geometry remains on Vulkan's fast opaque traversal.
// Use a fixed LOD so a cutout has deterministic transport visibility independent
// of probe direction or ray differentials, neither of which ray queries expose.
bool DdgiCandidatePassesOpacityPolicy(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    bool frontFacing,
    bool enforceMaterialSidedness)
{
    GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    bool doubleSided = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_DOUBLE_SIDED);
    if (enforceMaterialSidedness && !EvaluateGiSidedness(doubleSided, frontFacing))
        return false;
    if (!DDGI_HIT_ALPHA_MASK_TRANSPORT_ENABLED)
        return true;
    if (DecodeMaterialAlphaMode(material.NormalScaleBias.y) != MATERIAL_ALPHA_MODE_MASK)
        return true;

    uint triangleIndexBase = instance.IndexOffset + primitiveIndex * 3u;
    uint i0 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 0u);
    uint i1 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 1u);
    uint i2 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 2u);
    uint v0 = instance.VertexOffset + i0;
    uint v1 = instance.VertexOffset + i1;
    uint v2 = instance.VertexOffset + i2;
    vec3 bary = vec3(1.0 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
    vec2 texCoord00 = ReadSplitVertexTexCoord(v0);
    vec2 texCoord01 = ReadSplitVertexTexCoord(v1);
    vec2 texCoord02 = ReadSplitVertexTexCoord(v2);
    vec2 texCoord10 = ReadSplitVertexTexCoord2(v0);
    vec2 texCoord11 = ReadSplitVertexTexCoord2(v1);
    vec2 texCoord12 = ReadSplitVertexTexCoord2(v2);
    vec2 uv0 = texCoord00 * bary.x + texCoord01 * bary.y + texCoord02 * bary.z;
    vec2 uv1 = texCoord10 * bary.x + texCoord11 * bary.y + texCoord12 * bary.z;
    float vertexAlpha = clamp(
        ReadSplitVertexColor(v0).a * bary.x +
        ReadSplitVertexColor(v1).a * bary.y +
        ReadSplitVertexColor(v2).a * bary.z,
        0.0,
        1.0);
    float sampledTextureAlpha = 1.0;
    // Visibility is independent from color-transport quality. Masked geometry
    // always evaluates its coverage texture at a deterministic policy LOD.
    if (GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_HAS_BASE_COLOR_TEXTURE))
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

    bool covered = DdgiAlphaCandidateOccupiesOpaqueTransport(
        material.Albedo.a,
        vertexAlpha,
        sampledTextureAlpha,
        material.NormalScaleBias.y,
        material.NormalScaleBias.z);
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
        uint triangleIndexBase = instance.IndexOffset + primitiveIndex * 3u;
        uint i0 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 0u);
        uint i1 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 1u);
        uint i2 = ReadStorageWordUniform(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 2u);
        uint v0 = instance.VertexOffset + i0;
        uint v1 = instance.VertexOffset + i1;
        uint v2 = instance.VertexOffset + i2;
        vec3 bary = vec3(
            1.0 - barycentrics.x - barycentrics.y,
            barycentrics.x,
            barycentrics.y);
        vec2 uv0 = ReadSplitVertexTexCoord(v0) * bary.x +
            ReadSplitVertexTexCoord(v1) * bary.y +
            ReadSplitVertexTexCoord(v2) * bary.z;
        vec2 uv1 = ReadSplitVertexTexCoord2(v0) * bary.x +
            ReadSplitVertexTexCoord2(v1) * bary.y +
            ReadSplitVertexTexCoord2(v2) * bary.z;
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
    rayQueryInitializeEXT(
        shadowQuery,
        SceneTlas,
        // Sidedness belongs to DdgiCandidatePassesOpacity below. Hardware
        // backface culling would discard the reverse side of authored
        // double-sided/thin cloth before its transmission can be evaluated.
        gl_RayFlagsNoneEXT,
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
            alphaCandidateCount++;
            if (alphaCandidateCount > DDGI_HIT_ALPHA_CANDIDATE_LIMIT)
            {
                RecordDdgiAlphaCandidateLimitReached();
                rayQueryConfirmIntersectionEXT(shadowQuery);
                rayQueryTerminateEXT(shadowQuery);
                break;
            }
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
            if (!DdgiCandidatePassesOpacity(instanceIndex, primitiveIndex, barycentrics, frontFacing))
                continue;

            GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
            GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
            bool thin = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED && GiMaterialHasFlag(
                material.TransportFlags,
                GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
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
                visibilityRgb *= layerTransmission;
                thinLayerCount++;
                if (thinLayerCount >= 8u)
                {
                    if (DDGI_HIT_SHADOW_DIAGNOSTICS_ENABLED)
                        AddRendererDiagnostic(DDGI_HIT_CURRENT_FRAME_INDEX, DDGI_THIN_SHADOW_LAYER_LIMIT_COUNTER, 1u);
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
    if (light.Type == 1)
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

    attenuation = EvaluateNjulfPunctualRangeAttenuation(distanceToLight, light.Range);
    if (light.Type == 2)
        attenuation *= EvaluateNjulfSpotAttenuation(light.Direction, lightDirection, light.SpotAngle);

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
            4294967295.0)));
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

vec3 EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit(
    vec3 worldPosition,
    GiSurfaceSample surface,
    vec3 viewDirection,
    float receiverProbeSpacing)
{
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

    if ((firstFlags & EmissiveSourceTriangleFlag) == 0u)
        return vec3(0.0);

    // A deterministic per-hit seed drives one Vose alias sample and one
    // uniform-area triangle sample. Selection PDF and area are stored explicitly
    // so the estimator remains invariant under triangle area and bounds padding.
    uint seed =
        floatBitsToUint(worldPosition.x) * 0x9e3779b9u ^
        floatBitsToUint(worldPosition.y) * 0x85ebca6bu ^
        floatBitsToUint(worldPosition.z) * 0xc2b2ae35u ^
        pc.CurrentFrameIndex * 0x27d4eb2du;
    seed ^= seed >> 16u;
    seed *= 0x7feb352du;
    seed ^= seed >> 15u;
    float u0 = float(seed) * 2.3283064365386963e-10;
    seed = seed * 1664525u + 1013904223u;
    float u1 = float(seed) * 2.3283064365386963e-10;
    seed = seed * 1664525u + 1013904223u;
    float u2 = float(seed) * 2.3283064365386963e-10;
    seed = seed * 1664525u + 1013904223u;
    float u3 = float(seed) * 2.3283064365386963e-10;

    uint sourceCount = pc.EmissiveSourceCount;
    uint column = min(uint(u0 * float(sourceCount)), sourceCount - 1u);
    GPUDdgiEmissiveSource columnSource = ReadDdgiEmissiveSource(column);
    uint packedAliasFlags = floatBitsToUint(columnSource.Edge2AliasFlags.w);
    uint aliasIndex = min(packedAliasFlags & EmissiveSourceAliasIndexMask, sourceCount - 1u);
    uint selectedIndex = u1 < clamp(columnSource.Edge1AliasProbability.w, 0.0, 1.0)
        ? column
        : aliasIndex;
    GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(selectedIndex);
    uint sourceFlags = floatBitsToUint(source.Edge2AliasFlags.w) >> EmissiveSourceFlagsShift;

    float sqrtU2 = sqrt(clamp(u2, 0.0, 1.0));
    float bary1 = sqrtU2 * (1.0 - u3);
    float bary2 = sqrtU2 * u3;
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
    float selectionProbability = max(source.RadianceSelectionProbability.w, 1e-10);
    vec3 incidentIrradiance = max(source.RadianceSelectionProbability.rgb, vec3(0.0)) *
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

#if DDGI_HIT_USE_SELECTED_LIGHTS
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

    GPULight localLight;
    vec3 localLightDirection;
    float localLightDistance;
    float localLightAttenuation;
    bool thinSurface = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED && GiMaterialHasFlag(
        surface.Flags,
        GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    if (TryBuildSelectedDdgiLocalLightContribution(
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
#else
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
        if (distanceToLight >= light.Range || light.Range <= 0.0)
            continue;
        vec3 lightDirection = toLight / max(distanceToLight, 0.0001);
        float nDotL = DDGI_HIT_THIN_SURFACE_TRANSMISSION_ENABLED && GiMaterialHasFlag(
                surface.Flags,
                GI_MATERIAL_THIN_SURFACE_TRANSMISSION)
            ? abs(dot(surface.ShadingNormal, lightDirection))
            : max(dot(surface.ShadingNormal, lightDirection), 0.0);
        if (nDotL <= 0.0)
            continue;

        float attenuation = EvaluateNjulfPunctualRangeAttenuation(distanceToLight, light.Range);
        if (light.Type == 2)
            attenuation *= EvaluateNjulfSpotAttenuation(light.Direction, lightDirection, light.SpotAngle);
        float importance = DdgiHitLuminance(max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0)) * attenuation * nDotL;
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
        directNoShadowDiffuse += lightNoShadowDiffuse;
    }
#endif

    return directDiffuseRadiance;
}

#endif
