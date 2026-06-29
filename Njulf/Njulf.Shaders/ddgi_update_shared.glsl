#ifndef NJULF_DDGI_UPDATE_SHARED_GLSL
#define NJULF_DDGI_UPDATE_SHARED_GLSL

layout(set = 2, binding = 0) uniform accelerationStructureEXT SceneTlas;

layout(push_constant) uniform DdgiUpdatePushBlock
{
    vec4 EnvironmentRadianceAndIntensity;
    uint ProbeCount;
    uint VolumeCount;
    uint StartProbeIndex;
    uint ProbesToUpdate;
    uint RaysPerProbe;
    uint FrameIndex;
    uint IrradianceTexelsPerProbe;
    uint VisibilityTexelsPerProbe;
    uint ProbeStateBufferIndex;
    uint ProbeUpdateQueueBufferIndex;
    uint RelocationClassificationBufferIndex;
    uint IrradianceAtlasBufferIndex;
    uint VisibilityAtlasBufferIndex;
    uint RayResultScratchBufferIndex;
    uint RayCapacityPerProbe;
    uint CurrentFrameIndex;
    uint Flags;
    uint LightCount;
    uint MaxShadedLights;
    uint DirectionalLightCount;
    uint LocalLightCount;
    uint LightSelectionMode;
    uint PrimaryDirectionalLightIndex;
    uint SelectedLocalLightIndex;
    float SelectedLocalLightEnergyScale;
    uint EmissiveSourceCount;
    uint EmissiveSourceRevision;
    uint MaterialTextureMaxCascade;
    uint Padding1;
} pc;

const float PI = 3.14159265359;
const uint DDGI_UPDATE_FLAG_ENABLED = 1u << 0;
const uint DDGI_UPDATE_FLAG_RELOCATION = 1u << 1;
const uint DDGI_UPDATE_FLAG_CLASSIFICATION = 1u << 2;
const uint DDGI_UPDATE_FLAG_GPU_SCHEDULER = 1u << 3;
const uint DDGI_PROBE_UPDATE_REASON_NEW_CELL = 1u << 0;
const uint DDGI_PROBE_UPDATE_REASON_DIRTY_BOUNDS = 1u << 1;
const uint DDGI_PROBE_UPDATE_REASON_VISIBLE_FRUSTUM = 1u << 2;
const uint DDGI_PROBE_UPDATE_REASON_AGE_REFRESH = 1u << 3;
const uint DDGI_PROBE_UPDATE_REASON_TELEPORT_WARMUP = 1u << 4;
const uint DDGI_PROBE_UPDATE_REASON_OUTSIDE_FRUSTUM_SAFETY = 1u << 6;
const uint DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED = 1u << 8;
const uint DDGI_PROBE_UPDATE_REASON_GEOMETRY_REMOVED = 1u << 9;
const uint DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED = 1u << 10;
const uint DDGI_PROBE_UPDATE_REASON_MATERIAL_CHANGED = 1u << 11;
const uint DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED = 1u << 12;
const uint DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED = 1u << 13;
const uint DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED = 1u << 14;
const uint DDGI_PROBE_UPDATE_REASON_STREAM_IN = 1u << 15;
const uint DDGI_PROBE_UPDATE_REASON_STREAM_OUT = 1u << 16;
const uint DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP = 1u;
const uint DDGI_LOCAL_SIZE = 64u;
const uint DDGI_MAX_RAYS_PER_PROBE = 256u;
const uint DDGI_MAX_SELECTED_HIT_LIGHTS = 2u;
const uint DDGI_LIGHT_SELECTION_MODE_BOUNDED_DIRECTIONAL_LOCAL = 1u;
const uint DDGI_INVALID_LIGHT_INDEX = 0xffffffffu;
const uint DDGI_MATERIAL_TEXTURE_DISABLED_CASCADE = 4u;
const uint DDGI_AUTHORED_VOLUME_CASCADE = 0xffffffffu;
const uint DDGI_RAY_RESULT_STRIDE_WORDS = 20u;
const float DDGI_PROBE_TRACE_EPSILON = 0.02;
const float DDGI_DIFFUSE_ALBEDO = 0.78;
const float DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE = 256.0;

shared vec4 SharedRadianceAndRayCount[64];
shared vec4 SharedVisibilityAndHitCount[64];
shared vec4 SharedRelocationAndCloseCount[64];
shared vec4 SharedBackfaceAndMissCount[64];
shared vec4 SharedRayIrradiance[256];
shared vec4 SharedRayDirection[256];
shared vec4 SharedProbeAtlasControl;

struct DdgiProbeUpdateRequest
{
    uint ProbeIndex;
    uint VolumeIndex;
    uint Flags;
    uint Priority;
    ivec3 LogicalCell;
};

struct StableDdgiVolumeSampleInfo
{
    uint firstProbe;
    uint kind;
    uvec3 probeCounts;
    ivec3 gridMinCell;
    ivec3 ringOffset;
    vec3 origin;
    vec3 spacing;
    ivec3 cellBase;
    vec3 cellFraction;
    float edgeFade;
    float normalBias;
    float viewBias;
};

void WritePackedHalf4(uint bufferIndex, uint wordOffset, vec4 value)
{
    WriteStorageWord(bufferIndex, wordOffset + 0u, packHalf2x16(value.xy));
    WriteStorageWord(bufferIndex, wordOffset + 1u, packHalf2x16(value.zw));
}

vec4 ReadPackedHalf4(uint bufferIndex, uint wordOffset)
{
    vec2 xy = unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset + 0u));
    vec2 zw = unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset + 1u));
    return vec4(xy, zw);
}

void WritePackedHalf2(uint bufferIndex, uint wordOffset, vec2 value)
{
    WriteStorageWord(bufferIndex, wordOffset, packHalf2x16(value));
}

vec2 ReadPackedHalf2(uint bufferIndex, uint wordOffset)
{
    return unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset));
}

float Hash11(float p)
{
    p = fract(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return fract(p);
}

uint HashUint(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

vec2 Hash22(uvec3 value)
{
    uint seed = value.x * 1664525u + value.y * 1013904223u + value.z * 747796405u;
    return vec2(
        float(HashUint(seed)) * (1.0 / 4294967296.0),
        float(HashUint(seed ^ 0x9e3779b9u)) * (1.0 / 4294967296.0));
}

uint ResolvePrimaryProbeUpdateReason(uint flags)
{
    if ((flags & DDGI_PROBE_UPDATE_REASON_TELEPORT_WARMUP) != 0u)
        return 4u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_GEOMETRY_REMOVED) != 0u)
        return 8u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED) != 0u)
        return 7u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED) != 0u)
        return 9u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED) != 0u)
        return 11u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED) != 0u)
        return 12u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED) != 0u)
        return 13u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_MATERIAL_CHANGED) != 0u)
        return 10u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_STREAM_OUT) != 0u)
        return 15u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_STREAM_IN) != 0u)
        return 14u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_NEW_CELL) != 0u)
        return 1u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_DIRTY_BOUNDS) != 0u)
        return 2u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_VISIBLE_FRUSTUM) != 0u)
        return 3u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_OUTSIDE_FRUSTUM_SAFETY) != 0u)
        return 6u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_AGE_REFRESH) != 0u)
        return 5u;
    return 0u;
}

bool ShouldResetDdgiProbeHistory(uint flags)
{
    return (flags & (
        DDGI_PROBE_UPDATE_REASON_NEW_CELL |
        DDGI_PROBE_UPDATE_REASON_TELEPORT_WARMUP |
        DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED |
        DDGI_PROBE_UPDATE_REASON_GEOMETRY_REMOVED |
        DDGI_PROBE_UPDATE_REASON_STREAM_IN |
        DDGI_PROBE_UPDATE_REASON_STREAM_OUT)) != 0u;
}

float ResolveDdgiDirtyReasonHysteresis(float baseHysteresis, uint flags)
{
    if (ShouldResetDdgiProbeHistory(flags))
        return 0.0;
    if ((flags & DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED) != 0u)
        return min(baseHysteresis, 0.25);
    if ((flags & DDGI_PROBE_UPDATE_REASON_MATERIAL_CHANGED) != 0u)
        return min(baseHysteresis, 0.35);
    if ((flags & (DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED | DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED)) != 0u)
        return min(baseHysteresis, 0.65);
    if ((flags & DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED) != 0u)
        return min(baseHysteresis, 0.85);
    return baseHysteresis;
}

vec2 SignNotZero(vec2 value)
{
    return vec2(
        value.x >= 0.0 ? 1.0 : -1.0,
        value.y >= 0.0 ? 1.0 : -1.0);
}

vec3 OctahedralDecode(vec2 encoded)
{
    vec2 f = encoded * 2.0 - 1.0;
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0.0)
    {
        vec2 folded = (1.0 - abs(n.yx)) * SignNotZero(n.xy);
        n.xy = folded;
    }

    return normalize(n);
}

vec2 OctahedralEncode(vec3 direction)
{
    vec3 n = direction / max(abs(direction.x) + abs(direction.y) + abs(direction.z), 0.0001);
    vec2 encoded = n.xy;
    if (n.z < 0.0)
        encoded = (1.0 - abs(encoded.yx)) * SignNotZero(encoded);
    return encoded * 0.5 + 0.5;
}

vec3 AtlasTexelDirection(uint texel, uint texelsPerProbe, uint frameOffset)
{
    uint texelCount = max(texelsPerProbe * texelsPerProbe, 1u);
    uint rotatedTexel = (texel + frameOffset) % texelCount;
    uint x = rotatedTexel % texelsPerProbe;
    uint y = rotatedTexel / texelsPerProbe;
    vec2 uv = (vec2(float(x), float(y)) + vec2(0.5)) / vec2(float(texelsPerProbe));
    return OctahedralDecode(uv);
}

float OctahedralTexelSolidAngle(vec2 uv, uint texelsPerProbe)
{
    vec2 f = uv * 2.0 - vec2(1.0);
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0.0)
        n.xy = (1.0 - abs(n.yx)) * SignNotZero(n.xy);

    float texelArea = 4.0 / max(float(texelsPerProbe * texelsPerProbe), 1.0);
    return texelArea / max(pow(dot(n, n), 1.5), 0.000001);
}

vec3 JitteredAtlasTexelDirection(
    uint texel,
    uint texelsPerProbe,
    uint probeIndex,
    out float solidAngle)
{
    uint safeTexels = max(texelsPerProbe, 1u);
    uvec2 texelCoord = uvec2(texel % safeTexels, texel / safeTexels);
    vec2 jitter = Hash22(uvec3(probeIndex, texel, safeTexels)) - vec2(0.5);
    vec2 uv = (vec2(texelCoord) + vec2(0.5) + jitter * 0.85) / float(safeTexels);
    uv = clamp(uv, vec2(0.000001), vec2(0.999999));
    solidAngle = OctahedralTexelSolidAngle(uv, safeTexels);
    return OctahedralDecode(uv);
}

uint DirectionToAtlasTexel(vec3 direction, uint texelsPerProbe)
{
    vec2 uv = clamp(OctahedralEncode(direction), vec2(0.0), vec2(0.999999));
    uvec2 coord = uvec2(uv * float(texelsPerProbe));
    return coord.y * texelsPerProbe + coord.x;
}

uvec2 RemapStableDdgiOctahedralTexelCoord(ivec2 coord, uint texelsPerProbe)
{
    int maxCoord = int(max(texelsPerProbe, 1u)) - 1;
    ivec2 remapped = coord;

    if (remapped.x < 0)
    {
        remapped.x = 0;
        remapped.y = maxCoord - remapped.y;
    }
    else if (remapped.x > maxCoord)
    {
        remapped.x = maxCoord;
        remapped.y = maxCoord - remapped.y;
    }

    if (remapped.y < 0)
    {
        remapped.y = 0;
        remapped.x = maxCoord - remapped.x;
    }
    else if (remapped.y > maxCoord)
    {
        remapped.y = maxCoord;
        remapped.x = maxCoord - remapped.x;
    }

    return uvec2(clamp(remapped, ivec2(0), ivec2(maxCoord)));
}

void StableDdgiBilinearOctahedralTexels(
    vec3 direction,
    uint texelsPerProbe,
    out uvec2 c00,
    out uvec2 c10,
    out uvec2 c01,
    out uvec2 c11,
    out vec2 fraction)
{
    vec2 uv = clamp(OctahedralEncode(direction), vec2(0.0), vec2(1.0));
    vec2 sampleCoord = uv * float(texelsPerProbe) - vec2(0.5);
    ivec2 baseCoord = ivec2(floor(sampleCoord));
    fraction = fract(sampleCoord);

    c00 = RemapStableDdgiOctahedralTexelCoord(baseCoord, texelsPerProbe);
    c10 = RemapStableDdgiOctahedralTexelCoord(baseCoord + ivec2(1, 0), texelsPerProbe);
    c01 = RemapStableDdgiOctahedralTexelCoord(baseCoord + ivec2(0, 1), texelsPerProbe);
    c11 = RemapStableDdgiOctahedralTexelCoord(baseCoord + ivec2(1, 1), texelsPerProbe);
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

    return pc.MaterialTextureMaxCascade < DDGI_MATERIAL_TEXTURE_DISABLED_CASCADE &&
        volumeCascadeIndex <= pc.MaterialTextureMaxCascade;
}

bool ShouldUseCompactDdgiMaterial(uint volumeCascadeIndex)
{
    return !ShouldSampleDdgiMaterialTextures(volumeCascadeIndex);
}

float DdgiMaterialTextureLod(uint volumeCascadeIndex)
{
    if (volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE)
        return 0.0;

    return min(float(volumeCascadeIndex) * 1.5, 4.0);
}

float ResolveDdgiMaterialTextureLod(GPUMaterialData material, uint volumeCascadeIndex)
{
    return max(DdgiMaterialTextureLod(volumeCascadeIndex), max(material.DdgiMaterialPolicy.y, 0.0));
}

vec3 ResolveCompactDdgiAlbedo(GPUMaterialData material)
{
    return dot(material.DdgiAverageAlbedo.rgb, vec3(1.0)) > 0.000001
        ? material.DdgiAverageAlbedo.rgb
        : material.Albedo.rgb;
}

vec3 ResolveCompactDdgiEmissive(GPUMaterialData material)
{
    return dot(material.DdgiAverageEmissive.rgb, vec3(1.0)) > 0.000001
        ? material.DdgiAverageEmissive.rgb
        : material.Emissive.rgb;
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
    GPUDdgiRayQueryInstance instance;
    instance.VertexOffset = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 0u);
    instance.IndexOffset = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 1u);
    instance.MaterialIndex = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 2u);
    instance.Padding0 = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 3u);
    instance.WorldMatrixInverseTranspose = ReadStorageMat4(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 4u);
    return instance;
}

bool ResolveCommittedHitSurface(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    vec3 rayDirection,
    uint volumeCascadeIndex,
    bool sampleMaterialTextures,
    float materialTextureLod,
    out vec3 normal,
    out vec3 albedo,
    out vec3 emissive)
{
    GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
    uint triangleIndexBase = instance.IndexOffset + primitiveIndex * 3u;
    uint i0 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 0u);
    uint i1 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 1u);
    uint i2 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 2u);
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
    vec3 fallbackLocalNormal = cross(p1 - p0, p2 - p0);
    fallbackLocalNormal = dot(fallbackLocalNormal, fallbackLocalNormal) > 0.000001
        ? normalize(fallbackLocalNormal)
        : vec3(0.0, 1.0, 0.0);
    vec3 localNormal =
        ReadSplitVertexNormal(v0) * bary.x +
        ReadSplitVertexNormal(v1) * bary.y +
        ReadSplitVertexNormal(v2) * bary.z;
    if (dot(localNormal, localNormal) <= 0.000001)
        localNormal = fallbackLocalNormal;

    normal = normalize(MulRowMajor(vec4(normalize(localNormal), 0.0), instance.WorldMatrixInverseTranspose).xyz);
    if (dot(normal, normal) <= 0.000001)
        normal = normalize(-rayDirection);
    if (dot(normal, rayDirection) > 0.0)
        normal = -normal;

    vec2 uv0 =
        ReadSplitVertexTexCoord(v0) * bary.x +
        ReadSplitVertexTexCoord(v1) * bary.y +
        ReadSplitVertexTexCoord(v2) * bary.z;
    vec2 uv1 =
        ReadSplitVertexTexCoord2(v0) * bary.x +
        ReadSplitVertexTexCoord2(v1) * bary.y +
        ReadSplitVertexTexCoord2(v2) * bary.z;
    vec3 vertexColor =
        ReadSplitVertexColor(v0).rgb * bary.x +
        ReadSplitVertexColor(v1).rgb * bary.y +
        ReadSplitVertexColor(v2).rgb * bary.z;

    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    if (ShouldUseCompactDdgiMaterial(volumeCascadeIndex))
    {
        albedo = max(ResolveCompactDdgiAlbedo(material) * vertexColor, vec3(0.0));
        emissive = max(ResolveCompactDdgiEmissive(material), vec3(0.0));
        return true;
    }

    materialTextureLod = ResolveDdgiMaterialTextureLod(material, volumeCascadeIndex);
    vec4 albedoSample = vec4(1.0);
    if (sampleMaterialTextures && material.AlbedoTextureIndex != DEFAULT_WHITE_TEXTURE)
    {
        vec2 albedoUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.x,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        albedoSample = SampleDdgiMaterialTexture(material.AlbedoTextureIndex, albedoUv, materialTextureLod, vec4(1.0));
    }
    albedo = max(ResolveCompactDdgiAlbedo(material) * albedoSample.rgb * vertexColor, vec3(0.0));

    vec4 emissiveSample = vec4(1.0);
    if (sampleMaterialTextures && material.EmissiveTextureIndex != DEFAULT_BLACK_TEXTURE)
    {
        vec2 emissiveUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.w,
            material.EmissiveOffsetScale,
            material.TextureRotations.w);
        emissiveSample = SampleDdgiMaterialTexture(material.EmissiveTextureIndex, emissiveUv, materialTextureLod, vec4(0.0));
    }
    emissive = max(ResolveCompactDdgiEmissive(material) * emissiveSample.rgb, vec3(0.0));
    return true;
}

float TraceLightVisibility(vec3 worldPosition, vec3 normal, vec3 lightDirection, float maxDistance)
{
    float rayDistance = max(maxDistance - DDGI_PROBE_TRACE_EPSILON * 2.0, DDGI_PROBE_TRACE_EPSILON);
    vec3 origin = worldPosition + normal * DDGI_PROBE_TRACE_EPSILON + lightDirection * DDGI_PROBE_TRACE_EPSILON;

    rayQueryEXT shadowQuery;
    rayQueryInitializeEXT(
        shadowQuery,
        SceneTlas,
        gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT,
        0xff,
        origin,
        DDGI_PROBE_TRACE_EPSILON,
        lightDirection,
        rayDistance);

    while (rayQueryProceedEXT(shadowQuery))
    {
    }

    uint hitType = rayQueryGetIntersectionTypeEXT(shadowQuery, true);
    return hitType == gl_RayQueryCommittedIntersectionNoneEXT ? 1.0 : 0.0;
}

bool TryReadSelectedDdgiDirectionalLight(out GPULight selectedLight)
{
    if (pc.DirectionalLightCount == 0u ||
        pc.PrimaryDirectionalLightIndex == DDGI_INVALID_LIGHT_INDEX ||
        pc.PrimaryDirectionalLightIndex >= pc.LightCount)
        return false;

    selectedLight = ReadLight(pc.PrimaryDirectionalLightIndex);
    return selectedLight.Type == 1;
}

bool TryBuildSelectedDdgiLocalLightContribution(
    vec3 worldPosition,
    vec3 normal,
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
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return false;

    float rangeFactor = clamp(1.0 - distanceToLight / light.Range, 0.0, 1.0);
    attenuation = rangeFactor * rangeFactor;
    if (light.Type == 2)
    {
        float coneCos = cos(light.SpotAngle);
        float spotCos = dot(normalize(light.Direction), -lightDirection);
        float spotFactor = smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
        attenuation *= spotFactor;
    }

    attenuation *= max(pc.SelectedLocalLightEnergyScale, 0.0);
    return attenuation > 0.0;
}

vec3 EvaluateSelectedDdgiLight(
    vec3 worldPosition,
    vec3 normal,
    vec3 albedo,
    GPULight light,
    vec3 lightDirection,
    float visibilityDistance,
    float attenuation)
{
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return vec3(0.0);

    float visibility = TraceLightVisibility(worldPosition, normal, lightDirection, visibilityDistance);
    vec3 incoming = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    return incoming * nDotL * visibility * (albedo / PI);
}

vec3 EvaluateSelectedDdgiEmissiveSourceAtHit(vec3 worldPosition, vec3 normal, vec3 albedo)
{
    if (pc.EmissiveSourceCount == 0u)
        return vec3(0.0);

    GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(0u);
    vec3 toSource = source.CenterRadius.xyz - worldPosition;
    float distanceToSource = length(toSource);
    float radius = max(source.CenterRadius.w, 0.001);
    if (distanceToSource >= radius)
        return vec3(0.0);

    vec3 lightDirection = toSource / max(distanceToSource, 0.0001);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return vec3(0.0);

    float radiusAttenuation = 1.0 - distanceToSource / radius;
    radiusAttenuation *= radiusAttenuation;
    vec3 radiance = max(source.RadianceImportance.rgb, vec3(0.0));
    return radiance * nDotL * radiusAttenuation * (albedo / PI);
}

vec3 EvaluateDirectDiffuseAtHit(vec3 worldPosition, vec3 normal, vec3 albedo)
{
    vec3 radiance = vec3(0.0);
    uint selectedLightCapacity = min(pc.MaxShadedLights, DDGI_MAX_SELECTED_HIT_LIGHTS);
    uint selectedLightCount = 0u;
    if (selectedLightCapacity == 0u || pc.LightCount == 0u)
        return radiance;

    GPULight directionalLight;
    if (TryReadSelectedDdgiDirectionalLight(directionalLight))
    {
        vec3 lightDirection = normalize(-directionalLight.Direction);
        radiance += EvaluateSelectedDdgiLight(
            worldPosition,
            normal,
            albedo,
            directionalLight,
            lightDirection,
            DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE,
            1.0);
        selectedLightCount++;
    }

    if (selectedLightCount >= selectedLightCapacity)
        return radiance;

    GPULight localLight;
    vec3 localLightDirection;
    float localLightDistance;
    float localLightAttenuation;
    if (TryBuildSelectedDdgiLocalLightContribution(
        worldPosition,
        normal,
        localLight,
        localLightDirection,
        localLightDistance,
        localLightAttenuation))
    {
        radiance += EvaluateSelectedDdgiLight(
            worldPosition,
            normal,
            albedo,
            localLight,
            localLightDirection,
            localLightDistance,
            localLightAttenuation);
    }

    return radiance;
}

bool ReadStableDdgiVolumeSampleInfo(
    uint volumeIndex,
    vec3 worldPosition,
    out StableDdgiVolumeSampleInfo info)
{
    uint volumeBaseWord = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER) / 4u;
    uint volumeStrideWords = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME) / 4u;
    uint baseWord = volumeBaseWord + volumeIndex * volumeStrideWords;
    vec4 originAndFirst = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX) / 4u);
    vec4 sizeAndCountX = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X) / 4u);
    vec4 spacingAndCountY = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y) / 4u);
    vec4 biasAndCountZ = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z) / 4u);
    vec4 gridMinAndKind = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND) / 4u);
    vec4 ringOffsetAndCascade = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE) / 4u);
    vec4 blendAndFlags = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_BLEND_AND_FLAGS) / 4u);

    info.firstProbe = uint(originAndFirst.w);
    info.kind = uint(round(gridMinAndKind.w));
    info.probeCounts = uvec3(
        max(uint(sizeAndCountX.w), 2u),
        max(uint(spacingAndCountY.w), 2u),
        max(uint(biasAndCountZ.w), 2u));
    info.gridMinCell = ivec3(round(gridMinAndKind.xyz));
    info.ringOffset = ivec3(round(ringOffsetAndCascade.xyz));
    info.origin = originAndFirst.xyz;
    info.spacing = max(spacingAndCountY.xyz, vec3(0.0001));
    info.normalBias = max(biasAndCountZ.x, 0.0);
    info.viewBias = max(biasAndCountZ.y, 0.0);

    vec3 latticeMax = info.origin + info.spacing * vec3(info.probeCounts - uvec3(1u));
    vec3 influenceMin = info.origin - info.spacing * 0.5;
    vec3 influenceMax = latticeMax + info.spacing * 0.5;
    if (any(lessThan(worldPosition, influenceMin)) || any(greaterThan(worldPosition, influenceMax)))
        return false;

    float volumeEdgeFade;
    if (info.kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
    {
        vec3 logicalPosition = worldPosition / info.spacing;
        vec3 minLogical = vec3(info.gridMinCell);
        vec3 maxLogical = minLogical + vec3(info.probeCounts - uvec3(1u));
        vec3 logicalGridPosition = clamp(logicalPosition, minLogical, maxLogical);
        vec3 logicalBase = floor(clamp(logicalGridPosition, minLogical, maxLogical - vec3(1.0)));
        info.cellBase = ivec3(logicalBase);
        info.cellFraction = clamp(logicalGridPosition - logicalBase, vec3(0.0), vec3(1.0));

        vec3 logicalEdgeDistance = min(logicalGridPosition - minLogical, maxLogical - logicalGridPosition);
        float edgeBlendCells = max(blendAndFlags.x * min(min(float(info.probeCounts.x), float(info.probeCounts.y)), float(info.probeCounts.z)), 1.0);
        float edgeBlendDistance = max(blendAndFlags.y / max(min(min(info.spacing.x, info.spacing.y), info.spacing.z), 0.0001), edgeBlendCells);
        vec3 edgeFade3 = smoothstep(vec3(0.0), vec3(edgeBlendDistance), logicalEdgeDistance);
        volumeEdgeFade = min(edgeFade3.x, min(edgeFade3.y, edgeFade3.z));
    }
    else
    {
        vec3 influenceEdgeDistance = min(worldPosition - influenceMin, influenceMax - worldPosition);
        vec3 edgeFade3 = smoothstep(vec3(0.0), info.spacing * 0.25, influenceEdgeDistance);
        volumeEdgeFade = min(edgeFade3.x, min(edgeFade3.y, edgeFade3.z));
        vec3 gridPosition = clamp((worldPosition - info.origin) / info.spacing, vec3(0.0), vec3(info.probeCounts - uvec3(1u)));
        vec3 localBase = floor(clamp(gridPosition, vec3(0.0), vec3(info.probeCounts - uvec3(2u))));
        info.cellBase = ivec3(localBase);
        info.cellFraction = clamp(gridPosition - localBase, vec3(0.0), vec3(1.0));
    }

    info.edgeFade = clamp(volumeEdgeFade, 0.0, 1.0);
    return true;
}

uint StableDdgiProbeIndex(StableDdgiVolumeSampleInfo info, ivec3 probeCoord)
{
    if (info.kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
    {
        return DdgiCalculatePhysicalProbeIndex(
            probeCoord,
            info.gridMinCell,
            info.ringOffset,
            info.probeCounts,
            info.firstProbe);
    }

    uvec3 localCoord = uvec3(max(probeCoord, ivec3(0)));
    localCoord = min(localCoord, info.probeCounts - uvec3(1u));
    return info.firstProbe + localCoord.x + localCoord.y * info.probeCounts.x + localCoord.z * info.probeCounts.x * info.probeCounts.y;
}

vec3 StableDdgiProbeWorldPosition(StableDdgiVolumeSampleInfo info, ivec3 probeCoord)
{
    if (info.kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
        return vec3(probeCoord) * info.spacing;

    return info.origin + info.spacing * vec3(probeCoord);
}

vec4 ReadStableDdgiProbeIrradiance(uint probeIndex, vec3 normal)
{
    uint texelsPerProbe = max(pc.IrradianceTexelsPerProbe, 1u);
    uint texelCount = texelsPerProbe * texelsPerProbe;
    uint wordsPerProbe = texelCount * 2u;
    uvec2 c00;
    uvec2 c10;
    uvec2 c01;
    uvec2 c11;
    vec2 fraction;
    StableDdgiBilinearOctahedralTexels(normal, texelsPerProbe, c00, c10, c01, c11, fraction);
    uint baseWord = probeIndex * wordsPerProbe;
    vec4 s00 = ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c00.y * texelsPerProbe + c00.x) * 2u);
    vec4 s10 = ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c10.y * texelsPerProbe + c10.x) * 2u);
    vec4 s01 = ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c01.y * texelsPerProbe + c01.x) * 2u);
    vec4 s11 = ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c11.y * texelsPerProbe + c11.x) * 2u);
    return mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y);
}

vec2 ReadStableDdgiProbeVisibility(uint probeIndex, vec3 probeToPoint)
{
    uint texelsPerProbe = max(pc.VisibilityTexelsPerProbe, 1u);
    uint texelCount = texelsPerProbe * texelsPerProbe;
    uvec2 c00;
    uvec2 c10;
    uvec2 c01;
    uvec2 c11;
    vec2 fraction;
    StableDdgiBilinearOctahedralTexels(probeToPoint, texelsPerProbe, c00, c10, c01, c11, fraction);
    uint baseWord = probeIndex * texelCount;
    vec2 s00 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c00.y * texelsPerProbe + c00.x);
    vec2 s10 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c10.y * texelsPerProbe + c10.x);
    vec2 s01 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c01.y * texelsPerProbe + c01.x);
    vec2 s11 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c11.y * texelsPerProbe + c11.x);
    return mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y);
}

float EvaluateStableDdgiVisibility(vec2 moments, float probeDistance, float viewBias)
{
    float mean = max(moments.x, 0.0001);
    float mean2 = max(moments.y, mean * mean);
    if (probeDistance <= mean + max(viewBias, 0.02))
        return 1.0;

    float variance = max(mean2 - mean * mean, 0.005);
    float delta = probeDistance - mean;
    return clamp(variance / (variance + delta * delta), 0.0, 1.0);
}

vec3 SampleStableDdgiVolumeIrradiance(StableDdgiVolumeSampleInfo info, vec3 worldPosition, vec3 normal)
{
    vec3 biasedPosition = worldPosition + normal * info.normalBias;
    vec3 accumulated = vec3(0.0);
    float totalWeight = 0.0;

    for (uint z = 0u; z <= 1u; z++)
    {
        for (uint y = 0u; y <= 1u; y++)
        {
            for (uint x = 0u; x <= 1u; x++)
            {
                ivec3 corner = info.cellBase + ivec3(x, y, z);
                vec3 trilinear = mix(vec3(1.0) - info.cellFraction, info.cellFraction, vec3(x, y, z));
                float cellWeight = trilinear.x * trilinear.y * trilinear.z;
                if (cellWeight <= 0.000001)
                    continue;

                uint probeIndex = StableDdgiProbeIndex(info, corner);
                uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
                vec4 stateIrradiance = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
                vec4 relocationAndClassification = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
                vec4 qualityAndReason = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u);
                float probeActive = clamp(min(stateIrradiance.w, relocationAndClassification.w), 0.0, 1.0);
                if (probeActive <= 0.001)
                    continue;

                vec3 probePosition = StableDdgiProbeWorldPosition(info, corner) + relocationAndClassification.xyz;
                vec3 toProbe = probePosition - worldPosition;
                float distanceToProbe = max(length(toProbe), 0.0001);
                vec3 pointToProbeDirection = toProbe / distanceToProbe;
                float alignment = dot(normal, pointToProbeDirection);
                float normalHemisphereWeight = clamp(alignment * 0.5 + 0.5, 0.0, 1.0);
                float grazingRejection = smoothstep(-0.15, 0.25, alignment);
                float normalWeight = normalHemisphereWeight * normalHemisphereWeight * grazingRejection;
                float distanceWeight = 1.0 / (1.0 + distanceToProbe * 0.025);

                vec4 irradianceSample = ReadStableDdgiProbeIrradiance(probeIndex, normal);
                float irradianceConfidence = clamp(irradianceSample.w, 0.0, 1.0);
                float rayHitConfidence = clamp(qualityAndReason.x, 0.0, 1.0);
                float stateIrradianceConfidence = clamp(qualityAndReason.y, 0.0, 1.0);
                float visibilityConfidence = clamp(qualityAndReason.z, 0.0, 1.0);
                float qualityConfidence = clamp(max(rayHitConfidence, 0.25) * max(stateIrradianceConfidence, irradianceConfidence) * max(visibilityConfidence, 0.25), 0.0, 1.0);
                if (irradianceConfidence <= 0.000001 || qualityConfidence <= 0.000001)
                    continue;

                vec3 probeToBiasedPoint = biasedPosition - probePosition;
                float biasedDistanceToProbe = max(length(probeToBiasedPoint), 0.0001);
                vec3 probeToPointDirection = probeToBiasedPoint / biasedDistanceToProbe;
                float visibility = EvaluateStableDdgiVisibility(
                    ReadStableDdgiProbeVisibility(probeIndex, probeToPointDirection),
                    biasedDistanceToProbe,
                    info.viewBias);
                float weight = cellWeight * normalWeight * distanceWeight * probeActive * irradianceConfidence * qualityConfidence * visibility;
                accumulated += clamp(irradianceSample.rgb, vec3(0.0), vec3(64.0)) * weight;
                totalWeight += weight;
            }
        }
    }

    return totalWeight > 0.000001
        ? clamp((accumulated / totalWeight) * info.edgeFade, vec3(0.0), vec3(64.0))
        : vec3(0.0);
}

vec3 SampleStableDdgiIrradiance(vec3 worldPosition, vec3 normal)
{
    uint flags = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 8u);
    uint volumeCount = min(ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 0u), pc.VolumeCount);
    if ((flags & DDGI_UPDATE_FLAG_ENABLED) == 0u || volumeCount == 0u)
        return vec3(0.0);

    float globalIntensity = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 12u), 0.0, 8.0);
    vec3 blendedIrradiance = vec3(0.0);
    float blendedCoverage = 0.0;
    float remainingCoverage = 1.0;

    for (uint volumeIndex = 0u; volumeIndex < volumeCount && remainingCoverage > 0.0001; volumeIndex++)
    {
        StableDdgiVolumeSampleInfo info;
        if (!ReadStableDdgiVolumeSampleInfo(volumeIndex, worldPosition, info))
            continue;

        vec3 irradiance = SampleStableDdgiVolumeIrradiance(info, worldPosition, normal);
        float coverage = clamp(info.edgeFade, 0.0, 1.0);
        if (coverage <= 0.000001)
            continue;

        float contribution = coverage * remainingCoverage;
        blendedIrradiance += irradiance * contribution;
        blendedCoverage += contribution;
        remainingCoverage *= 1.0 - coverage;
    }

    return blendedCoverage > 0.000001 && globalIntensity > 0.0001
        ? clamp((blendedIrradiance / blendedCoverage) / globalIntensity, vec3(0.0), vec3(64.0))
        : vec3(0.0);
}

vec3 EvaluateStableDiffuseAtHit(vec3 worldPosition, vec3 normal, vec3 albedo)
{
    vec3 stableIrradiance = SampleStableDdgiIrradiance(worldPosition + normal * DDGI_PROBE_TRACE_EPSILON, normal);
    return stableIrradiance * (albedo / PI);
}

DdgiProbeUpdateRequest ReadProbeUpdateRequest(uint updateIndex)
{
    uint baseWord = updateIndex * (uint(SIZEOF_GPU_DDGI_PROBE_UPDATE_REQUEST) / 4u);
    DdgiProbeUpdateRequest request;
    request.ProbeIndex = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PROBE_INDEX) / 4u);
    request.VolumeIndex = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_VOLUME_INDEX) / 4u);
    request.Flags = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FLAGS) / 4u);
    request.Priority = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PRIORITY) / 4u);
    request.LogicalCell = ivec3(
        int(ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_X) / 4u)),
        int(ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Y) / 4u)),
        int(ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Z) / 4u)));
    return request;
}

uint ResolveDdgiUpdateRequestCount()
{
    uint requestedCount = pc.ProbesToUpdate;
    if ((pc.Flags & DDGI_UPDATE_FLAG_GPU_SCHEDULER) == 0u)
        return requestedCount;

    uint gpuRequestCount = ReadStorageWord(
        uint(DDGI_SCHEDULER_COUNTER_BUFFER_INDEX),
        uint(OFFSET_GPU_DDGI_SCHEDULER_COUNTER_REQUEST_COUNT) / 4u);
    return min(gpuRequestCount, requestedCount);
}

bool ResolveProbeUpdateRequest(
    DdgiProbeUpdateRequest request,
    out uint localProbeIndex,
    out vec3 probePosition,
    out vec3 probeSpacing,
    out vec4 biasAndDistance,
    out vec4 updateParams,
    out uint volumeIndex,
    out uint volumeCascadeIndex)
{
    if (request.VolumeIndex >= pc.VolumeCount || request.ProbeIndex >= pc.ProbeCount)
    {
        localProbeIndex = 0u;
        probePosition = vec3(0.0);
        probeSpacing = vec3(1.0);
        biasAndDistance = vec4(0.0);
        updateParams = vec4(0.0);
        volumeIndex = 0u;
        volumeCascadeIndex = DDGI_AUTHORED_VOLUME_CASCADE;
        return false;
    }

    uint volumeBaseWord = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER) / 4u;
    uint volumeStrideWords = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME) / 4u;
    volumeIndex = request.VolumeIndex;
    volumeCascadeIndex = DDGI_AUTHORED_VOLUME_CASCADE;
    uint baseWord = volumeBaseWord + volumeIndex * volumeStrideWords;
    vec4 originAndFirst = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX) / 4u);
    vec4 sizeAndCountX = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X) / 4u);
    vec4 spacingAndCountY = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y) / 4u);
    vec4 volumeBiasAndCountZ = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z) / 4u);
    vec4 volumeUpdateParams = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_RAY_AND_UPDATE_PARAMS) / 4u);
    vec4 gridMinAndKind = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND) / 4u);
    vec4 ringOffsetAndCascade = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE) / 4u);

    uint firstProbe = uint(originAndFirst.w);
    uvec3 probeCounts = uvec3(
        max(uint(sizeAndCountX.w), 1u),
        max(uint(spacingAndCountY.w), 1u),
        max(uint(volumeBiasAndCountZ.w), 1u));
    uint countX = probeCounts.x;
    uint countY = probeCounts.y;
    uint countZ = probeCounts.z;
    uint volumeProbeCount = probeCounts.x * probeCounts.y * probeCounts.z;
    if (request.ProbeIndex < firstProbe || request.ProbeIndex >= firstProbe + volumeProbeCount)
    {
        localProbeIndex = 0u;
        probePosition = vec3(0.0);
        probeSpacing = vec3(1.0);
        biasAndDistance = vec4(0.0);
        updateParams = vec4(0.0);
        return false;
    }

    probeSpacing = max(spacingAndCountY.xyz, vec3(0.0001));
    uint kind = uint(round(gridMinAndKind.w));
    if (kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
    {
        volumeCascadeIndex = uint(max(round(ringOffsetAndCascade.w), 0.0));
        ivec3 gridMin = ivec3(round(gridMinAndKind.xyz));
        ivec3 ringOffset = ivec3(round(ringOffsetAndCascade.xyz));
        ivec3 relative = request.LogicalCell - gridMin;
        bool inGrid =
            relative.x >= 0 && relative.x < int(countX) &&
            relative.y >= 0 && relative.y < int(countY) &&
            relative.z >= 0 && relative.z < int(countZ);
        if (!inGrid)
        {
            localProbeIndex = 0u;
            probePosition = vec3(0.0);
            biasAndDistance = vec4(0.0);
            updateParams = vec4(0.0);
            return false;
        }

        localProbeIndex = DdgiCalculateLocalPhysicalProbeIndex(
            request.LogicalCell,
            gridMin,
            ringOffset,
            probeCounts);
        if (firstProbe + localProbeIndex != request.ProbeIndex)
        {
            probePosition = vec3(0.0);
            biasAndDistance = vec4(0.0);
            updateParams = vec4(0.0);
            return false;
        }

        probePosition = vec3(request.LogicalCell) * probeSpacing;
    }
    else
    {
        bool inVolume =
            request.LogicalCell.x >= 0 && request.LogicalCell.x < int(countX) &&
            request.LogicalCell.y >= 0 && request.LogicalCell.y < int(countY) &&
            request.LogicalCell.z >= 0 && request.LogicalCell.z < int(countZ);
        if (!inVolume)
        {
            localProbeIndex = 0u;
            probePosition = vec3(0.0);
            biasAndDistance = vec4(0.0);
            updateParams = vec4(0.0);
            return false;
        }

        localProbeIndex = uint(request.LogicalCell.x) +
            uint(request.LogicalCell.y) * countX +
            uint(request.LogicalCell.z) * countX * countY;
        if (firstProbe + localProbeIndex != request.ProbeIndex)
        {
            probePosition = vec3(0.0);
            biasAndDistance = vec4(0.0);
            updateParams = vec4(0.0);
            return false;
        }

        probePosition = originAndFirst.xyz + probeSpacing * vec3(request.LogicalCell);
    }

    biasAndDistance = vec4(volumeBiasAndCountZ.xyz, 0.0);
    updateParams = volumeUpdateParams;
    return true;
}

void TraceProbeRay(
    vec3 probePosition,
    vec3 direction,
    float normalBias,
    float viewBias,
    float maxDistance,
    uint volumeCascadeIndex,
    out vec3 radiance,
    out vec2 visibilityMoment,
    out float hit,
    out float miss,
    out float closeHit,
    out float backface,
    out vec3 relocation)
{
    float tMin = min(DDGI_PROBE_TRACE_EPSILON, max(maxDistance * 0.01, 0.001));
    vec3 origin = probePosition;

    rayQueryEXT query;
    rayQueryInitializeEXT(
        query,
        SceneTlas,
        gl_RayFlagsOpaqueEXT,
        0xff,
        origin,
        tMin,
        direction,
        maxDistance);

    while (rayQueryProceedEXT(query))
    {
    }

    uint hitType = rayQueryGetIntersectionTypeEXT(query, true);
    if (hitType != gl_RayQueryCommittedIntersectionNoneEXT)
    {
        float hitT = rayQueryGetIntersectionTEXT(query, true);
        bool frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);
        float closeThreshold = max(normalBias + viewBias * 2.0, 0.05);
        float closeWeight = 1.0 - smoothstep(closeThreshold, closeThreshold * 4.0, hitT);

        hit = 1.0;
        miss = 0.0;
        closeHit = closeWeight;
        backface = frontFace ? 0.0 : 1.0;
        relocation = -direction * closeWeight;
        visibilityMoment = vec2(hitT, hitT * hitT);

        vec3 hitPosition = origin + direction * hitT;
        vec3 surfaceNormal = normalize(-direction);
        vec3 surfaceAlbedo = vec3(DDGI_DIFFUSE_ALBEDO);
        vec3 surfaceEmissive = vec3(0.0);
        bool sampleMaterialTextures = ShouldSampleDdgiMaterialTextures(volumeCascadeIndex);
        float materialTextureLod = DdgiMaterialTextureLod(volumeCascadeIndex);
        uint instanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(query, true);
        uint primitiveIndex = rayQueryGetIntersectionPrimitiveIndexEXT(query, true);
        vec2 barycentrics = rayQueryGetIntersectionBarycentricsEXT(query, true);
        ResolveCommittedHitSurface(
            instanceIndex,
            primitiveIndex,
            barycentrics,
            direction,
            volumeCascadeIndex,
            sampleMaterialTextures,
            materialTextureLod,
            surfaceNormal,
            surfaceAlbedo,
            surfaceEmissive);
        vec3 directDiffuse = EvaluateDirectDiffuseAtHit(hitPosition, surfaceNormal, surfaceAlbedo);
        vec3 emissiveProxyDiffuse = EvaluateSelectedDdgiEmissiveSourceAtHit(hitPosition, surfaceNormal, surfaceAlbedo);
        vec3 stableDiffuse = EvaluateStableDiffuseAtHit(hitPosition, surfaceNormal, surfaceAlbedo);
        radiance = surfaceEmissive + emissiveProxyDiffuse + directDiffuse + stableDiffuse;
        return;
    }

    float skyWeight = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
    radiance = pc.EnvironmentRadianceAndIntensity.rgb * max(pc.EnvironmentRadianceAndIntensity.w, 0.0) * skyWeight;
    visibilityMoment = vec2(maxDistance, maxDistance * maxDistance);
    hit = 0.0;
    miss = 1.0;
    closeHit = 0.0;
    backface = 0.0;
    relocation = vec3(0.0);
}

void WriteVisibilityAtlasSample(
    uint visibilityTexel,
    vec2 visibilitySample,
    float blendAlpha,
    uint probeIndex)
{
    uint visibilityTexels = max(pc.VisibilityTexelsPerProbe, 1u);
    uint visibilityTexelCount = visibilityTexels * visibilityTexels;

    if (visibilityTexel < visibilityTexelCount)
    {
        uint visibilityBase = probeIndex * visibilityTexelCount;
        uint visibilityWord = visibilityBase + visibilityTexel;
        vec2 previous = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, visibilityWord);
        WritePackedHalf2(pc.VisibilityAtlasBufferIndex, visibilityWord, mix(previous, visibilitySample, blendAlpha));
    }
}

vec4 AccumulateProbeIrradianceTexel(uint texel, uint texelsPerProbe, uint rayCount, float activeProbe)
{
    vec3 texelDirection = AtlasTexelDirection(texel, texelsPerProbe, 0u);
    vec3 weightedRadiance = vec3(0.0);
    float weightSum = 0.0;
    uint sampleCount = min(rayCount, DDGI_MAX_RAYS_PER_PROBE);

    for (uint rayIndex = 0u; rayIndex < sampleCount; rayIndex++)
    {
        vec4 rayIrradiance = SharedRayIrradiance[rayIndex];
        vec3 rayDirection = SharedRayDirection[rayIndex].xyz;
        float raySolidAngle = max(SharedRayDirection[rayIndex].w, 0.0);
        float weight = max(dot(rayDirection, texelDirection), 0.0) * raySolidAngle * rayIrradiance.w;
        weightedRadiance += rayIrradiance.rgb * weight;
        weightSum += weight;
    }

    float expectedWeight = PI;
    uint directionalTexelCount = max(pc.VisibilityTexelsPerProbe * pc.VisibilityTexelsPerProbe, 1u);
    float sampleCoverageScale = float(directionalTexelCount) / max(float(sampleCount), 1.0);
    weightedRadiance *= sampleCoverageScale;
    weightSum *= sampleCoverageScale;
    float confidence = clamp(weightSum / expectedWeight, 0.0, 1.0) * activeProbe;
    vec3 irradiance = sampleCount > 0u
        ? weightedRadiance
        : vec3(0.0);

    return vec4(irradiance, confidence);
}

void WriteProbeIrradianceAtlasTexel(uint probeIndex, uint texel, vec4 irradianceSample, float blendAlpha)
{
    uint irradianceTexels = max(pc.IrradianceTexelsPerProbe, 1u);
    uint irradianceTexelCount = irradianceTexels * irradianceTexels;
    uint irradianceWordsPerProbe = irradianceTexelCount * 2u;
    uint irradianceBase = probeIndex * irradianceWordsPerProbe;
    vec4 previous = ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, irradianceBase + texel * 2u);
    WritePackedHalf4(pc.IrradianceAtlasBufferIndex, irradianceBase + texel * 2u, mix(previous, irradianceSample, blendAlpha));
}

struct DdgiRayResult
{
    vec3 radiance;
    float confidence;
    vec3 direction;
    float solidAngle;
    float hitDistance;
    float hitDistanceSquared;
    float hit;
    float miss;
    vec3 relocation;
    float closeHit;
    float frontface;
    float backface;
    float flags;
};

uint RayResultBaseWord(uint updateIndex, uint rayIndex)
{
    return (updateIndex * max(pc.RayCapacityPerProbe, 1u) + rayIndex) * DDGI_RAY_RESULT_STRIDE_WORDS;
}

void WriteDdgiRayResult(uint updateIndex, uint rayIndex, DdgiRayResult result)
{
    uint baseWord = RayResultBaseWord(updateIndex, rayIndex);
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 0u, vec4(result.radiance, result.confidence));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 4u, vec4(result.direction, result.solidAngle));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 8u, vec4(result.hitDistance, result.hitDistanceSquared, result.hit, result.miss));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 12u, vec4(result.relocation, result.closeHit));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 16u, vec4(result.frontface, result.backface, result.flags, 0.0));
}

DdgiRayResult ReadDdgiRayResult(uint updateIndex, uint rayIndex)
{
    uint baseWord = RayResultBaseWord(updateIndex, rayIndex);
    vec4 radiance = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 0u);
    vec4 direction = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 4u);
    vec4 visibility = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 8u);
    vec4 relocation = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 12u);
    vec4 evidence = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 16u);

    DdgiRayResult result;
    result.radiance = radiance.rgb;
    result.confidence = radiance.w;
    result.direction = direction.xyz;
    result.solidAngle = direction.w;
    result.hitDistance = visibility.x;
    result.hitDistanceSquared = visibility.y;
    result.hit = visibility.z;
    result.miss = visibility.w;
    result.relocation = relocation.xyz;
    result.closeHit = relocation.w;
    result.frontface = evidence.x;
    result.backface = evidence.y;
    result.flags = evidence.z;
    return result;
}

#if defined(DDGI_TRACE_PASS)
void main()
{
    uint localIndex = gl_LocalInvocationID.x;
    uint updateIndex = gl_WorkGroupID.x;
    bool enabled = (pc.Flags & DDGI_UPDATE_FLAG_ENABLED) != 0u &&
        updateIndex < ResolveDdgiUpdateRequestCount() &&
        pc.ProbeCount > 0u;

    DdgiProbeUpdateRequest request;
    request.ProbeIndex = 0u;
    request.VolumeIndex = 0u;
    request.Flags = 0u;
    request.Priority = 0u;
    request.LogicalCell = ivec3(0);
    if (enabled)
        request = ReadProbeUpdateRequest(updateIndex);

    uint probeIndex = request.ProbeIndex;
    uint volumeIndex;
    uint volumeCascadeIndex;
    uint localProbeIndex;
    vec3 probePosition;
    vec3 probeSpacing;
    vec4 biasAndDistance;
    vec4 updateParams;
    bool resolved = enabled && ResolveProbeUpdateRequest(
        request,
        localProbeIndex,
        probePosition,
        probeSpacing,
        biasAndDistance,
        updateParams,
        volumeIndex,
        volumeCascadeIndex);

    uint raysPerProbe = clamp(uint(round(updateParams.x)), 1u, DDGI_MAX_RAYS_PER_PROBE);
    float normalBias = max(biasAndDistance.x, 0.0);
    float viewBias = max(biasAndDistance.y, 0.0);
    float maxDistance = max(biasAndDistance.z > 0.0 ? biasAndDistance.z : 16.0, 0.1);
    float intensity = max(updateParams.z, 0.0);
    float hysteresis = ResolveDdgiDirtyReasonHysteresis(clamp(updateParams.w, 0.0, 0.999), request.Flags);
    uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
    bool relocationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_RELOCATION) != 0u;
    bool classificationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_CLASSIFICATION) != 0u;
    bool resetHistory = ShouldResetDdgiProbeHistory(request.Flags);
    vec4 previousState = vec4(0.0);
    vec4 previousStateHistory = vec4(0.0);
    vec4 previousRelocationAndClassification = vec4(0.0);
    vec4 previousQualityAndReason = vec4(0.0);
    if (resolved)
    {
        if (!resetHistory)
        {
            previousState = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
            previousStateHistory = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u);
            previousRelocationAndClassification = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
            previousQualityAndReason = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u);
        }
        else if (localIndex == 0u)
        {
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 16u, vec4(0.0));
            if (relocationEnabled || classificationEnabled)
            {
                uint relocationBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION) / 4u);
                WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase, vec4(0.0));
                WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 4u, vec4(0.0));
            }
        }
    }

    float historyValid = clamp(previousStateHistory.w, 0.0, 1.0);
    float blendAlpha = historyValid > 0.5 ? 1.0 - hysteresis : 1.0;

    vec3 localRadiance = vec3(0.0);
    vec2 localVisibility = vec2(0.0);
    vec3 localRelocation = vec3(0.0);
    float localRayCount = 0.0;
    float localHitCount = 0.0;
    float localCloseCount = 0.0;
    float localBackfaceCount = 0.0;
    float localMissCount = 0.0;

    if (resolved)
    {
        uint visibilityTexels = max(pc.VisibilityTexelsPerProbe, 1u);
        uint visibilityTexelCount = visibilityTexels * visibilityTexels;
        uint frameOffset = pc.FrameIndex * max(raysPerProbe, 1u);

        for (uint rayIndex = localIndex; rayIndex < raysPerProbe; rayIndex += DDGI_LOCAL_SIZE)
        {
            uint directionalTexel = (rayIndex + frameOffset) % visibilityTexelCount;
            float raySolidAngle;
            vec3 direction = JitteredAtlasTexelDirection(
                directionalTexel,
                visibilityTexels,
                probeIndex,
                raySolidAngle);
            vec3 radiance;
            vec2 visibilityMoment;
            float hit;
            float miss;
            float closeHit;
            float backface;
            vec3 relocation;
            TraceProbeRay(
                probePosition,
                direction,
                normalBias,
                viewBias,
                maxDistance,
                volumeCascadeIndex,
                radiance,
                visibilityMoment,
                hit,
                miss,
                closeHit,
                backface,
                relocation);

            vec3 sampleIrradiance = radiance * intensity;
            DdgiRayResult rayResult;
            rayResult.radiance = sampleIrradiance;
            rayResult.confidence = 1.0;
            rayResult.direction = direction;
            rayResult.solidAngle = raySolidAngle;
            rayResult.hitDistance = visibilityMoment.x;
            rayResult.hitDistanceSquared = visibilityMoment.y;
            rayResult.hit = hit;
            rayResult.miss = miss;
            rayResult.relocation = relocation;
            rayResult.closeHit = closeHit;
            rayResult.frontface = 1.0 - backface;
            rayResult.backface = backface;
            rayResult.flags = resolved ? 1.0 : 0.0;
            WriteDdgiRayResult(updateIndex, rayIndex, rayResult);
        }
    }
}
#elif defined(DDGI_BLEND_PASS)
void main()
{
    uint localIndex = gl_LocalInvocationID.x;
    uint updateIndex = gl_WorkGroupID.x;
    bool enabled = (pc.Flags & DDGI_UPDATE_FLAG_ENABLED) != 0u &&
        updateIndex < ResolveDdgiUpdateRequestCount() &&
        pc.ProbeCount > 0u;

    DdgiProbeUpdateRequest request;
    request.ProbeIndex = 0u;
    request.VolumeIndex = 0u;
    request.Flags = 0u;
    request.Priority = 0u;
    request.LogicalCell = ivec3(0);
    if (enabled)
        request = ReadProbeUpdateRequest(updateIndex);

    uint probeIndex = request.ProbeIndex;
    uint volumeIndex;
    uint volumeCascadeIndex;
    uint localProbeIndex;
    vec3 probePosition;
    vec3 probeSpacing;
    vec4 biasAndDistance;
    vec4 updateParams;
    bool resolved = enabled && ResolveProbeUpdateRequest(
        request,
        localProbeIndex,
        probePosition,
        probeSpacing,
        biasAndDistance,
        updateParams,
        volumeIndex,
        volumeCascadeIndex);

    if (!resolved)
        return;

    uint raysPerProbe = clamp(uint(round(updateParams.x)), 1u, min(DDGI_MAX_RAYS_PER_PROBE, max(pc.RayCapacityPerProbe, 1u)));
    float hysteresis = ResolveDdgiDirtyReasonHysteresis(clamp(updateParams.w, 0.0, 0.999), request.Flags);
    uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
    bool resetHistory = ShouldResetDdgiProbeHistory(request.Flags);
    vec4 previousState = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
    vec4 previousStateHistory = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u);
    vec4 previousRelocationAndClassification = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
    float historyValid = clamp(previousStateHistory.w, 0.0, 1.0);
    float blendAlpha = historyValid > 0.5 ? 1.0 - hysteresis : 1.0;

    vec3 localRadiance = vec3(0.0);
    vec2 localVisibility = vec2(0.0);
    float localRayCount = 0.0;

    uint visibilityTexels = max(pc.VisibilityTexelsPerProbe, 1u);
    uint visibilityTexelCount = visibilityTexels * visibilityTexels;
    uint frameOffset = pc.FrameIndex * max(raysPerProbe, 1u);
    for (uint rayIndex = localIndex; rayIndex < raysPerProbe; rayIndex += DDGI_LOCAL_SIZE)
    {
        DdgiRayResult result = ReadDdgiRayResult(updateIndex, rayIndex);
        if (result.flags <= 0.0)
            continue;

        uint directionalTexel = (rayIndex + frameOffset) % visibilityTexelCount;
        vec2 visibilityMoment = vec2(result.hitDistance, result.hitDistanceSquared);
        WriteVisibilityAtlasSample(directionalTexel, visibilityMoment, blendAlpha, probeIndex);
        SharedRayIrradiance[rayIndex] = vec4(result.radiance, result.confidence);
        SharedRayDirection[rayIndex] = vec4(result.direction, result.solidAngle);
        localRadiance += result.radiance;
        localVisibility += visibilityMoment;
        localRayCount += result.confidence;
    }

    SharedRadianceAndRayCount[localIndex] = vec4(localRadiance, localRayCount);
    SharedVisibilityAndHitCount[localIndex] = vec4(localVisibility, 0.0, 0.0);
    barrier();

    if (localIndex == 0u)
    {
        vec3 totalRadiance = vec3(0.0);
        vec2 totalVisibility = vec2(0.0);
        float totalRayCount = 0.0;
        for (uint i = 0u; i < DDGI_LOCAL_SIZE; i++)
        {
            totalRadiance += SharedRadianceAndRayCount[i].xyz;
            totalRayCount += SharedRadianceAndRayCount[i].w;
            totalVisibility += SharedVisibilityAndHitCount[i].xy;
        }

        float invRayCount = 1.0 / max(totalRayCount, 1.0);
        vec3 irradiance = totalRadiance * invRayCount;
        vec2 visibility = totalVisibility * invRayCount;
        float previousLuminance = dot(previousState.rgb, vec3(0.2126, 0.7152, 0.0722));
        float currentLuminance = dot(irradiance, vec3(0.2126, 0.7152, 0.0722));
        float luminanceChange = abs(currentLuminance - previousLuminance) / max(max(previousLuminance, currentLuminance), 0.05);
        float previousActiveProbe = historyValid > 0.5
            ? clamp(min(previousState.w, previousRelocationAndClassification.w), 0.0, 1.0)
            : 1.0;
        vec4 blendedIrradiance = vec4(mix(previousState.rgb, irradiance, blendAlpha), previousActiveProbe);
        WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase, blendedIrradiance);
        WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(visibility, clamp(luminanceChange, 0.0, 1.0), 1.0));
        SharedProbeAtlasControl = vec4(previousActiveProbe, blendAlpha, 0.0, 0.0);
    }

    barrier();

    uint irradianceTexels = max(pc.IrradianceTexelsPerProbe, 1u);
    uint irradianceTexelCount = irradianceTexels * irradianceTexels;
    if (localIndex < irradianceTexelCount)
    {
        vec4 directionalIrradiance = AccumulateProbeIrradianceTexel(
            localIndex,
            irradianceTexels,
            raysPerProbe,
            SharedProbeAtlasControl.x);
        WriteProbeIrradianceAtlasTexel(
            probeIndex,
            localIndex,
            directionalIrradiance,
            SharedProbeAtlasControl.y);
    }
}
#elif defined(DDGI_RELOCATE_CLASSIFY_PASS)
void main()
{
    uint localIndex = gl_LocalInvocationID.x;
    uint updateIndex = gl_WorkGroupID.x;
    bool enabled = (pc.Flags & DDGI_UPDATE_FLAG_ENABLED) != 0u &&
        updateIndex < ResolveDdgiUpdateRequestCount() &&
        pc.ProbeCount > 0u;

    DdgiProbeUpdateRequest request;
    request.ProbeIndex = 0u;
    request.VolumeIndex = 0u;
    request.Flags = 0u;
    request.Priority = 0u;
    request.LogicalCell = ivec3(0);
    if (enabled)
        request = ReadProbeUpdateRequest(updateIndex);

    uint probeIndex = request.ProbeIndex;
    uint volumeIndex;
    uint volumeCascadeIndex;
    uint localProbeIndex;
    vec3 probePosition;
    vec3 probeSpacing;
    vec4 biasAndDistance;
    vec4 updateParams;
    bool resolved = enabled && ResolveProbeUpdateRequest(
        request,
        localProbeIndex,
        probePosition,
        probeSpacing,
        biasAndDistance,
        updateParams,
        volumeIndex,
        volumeCascadeIndex);

    if (!resolved)
        return;

    uint raysPerProbe = clamp(uint(round(updateParams.x)), 1u, min(DDGI_MAX_RAYS_PER_PROBE, max(pc.RayCapacityPerProbe, 1u)));
    float normalBias = max(biasAndDistance.x, 0.0);
    float viewBias = max(biasAndDistance.y, 0.0);
    float hysteresis = ResolveDdgiDirtyReasonHysteresis(clamp(updateParams.w, 0.0, 0.999), request.Flags);
    uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
    bool relocationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_RELOCATION) != 0u;
    bool classificationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_CLASSIFICATION) != 0u;
    bool resetHistory = ShouldResetDdgiProbeHistory(request.Flags);

    vec4 previousState = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
    vec4 previousStateHistory = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u);
    vec4 previousRelocationAndClassification = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
    vec4 previousQualityAndReason = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u);

    vec3 localRelocation = vec3(0.0);
    float localRayCount = 0.0;
    float localHitCount = 0.0;
    float localCloseCount = 0.0;
    float localBackfaceCount = 0.0;
    float localMissCount = 0.0;

    for (uint rayIndex = localIndex; rayIndex < raysPerProbe; rayIndex += DDGI_LOCAL_SIZE)
    {
        DdgiRayResult result = ReadDdgiRayResult(updateIndex, rayIndex);
        if (result.flags <= 0.0)
            continue;

        localRelocation += result.relocation;
        localRayCount += result.confidence;
        localHitCount += result.hit;
        localCloseCount += result.closeHit;
        localBackfaceCount += result.backface;
        localMissCount += result.miss;
    }

    SharedRadianceAndRayCount[localIndex] = vec4(0.0, 0.0, 0.0, localRayCount);
    SharedVisibilityAndHitCount[localIndex] = vec4(0.0, 0.0, localHitCount, 0.0);
    SharedRelocationAndCloseCount[localIndex] = vec4(localRelocation, localCloseCount);
    SharedBackfaceAndMissCount[localIndex] = vec4(localBackfaceCount, localMissCount, 0.0, 0.0);
    barrier();

    if (localIndex != 0u)
        return;

    vec3 totalRelocation = vec3(0.0);
    float totalRayCount = 0.0;
    float totalHitCount = 0.0;
    float totalCloseCount = 0.0;
    float totalBackfaceCount = 0.0;
    float totalMissCount = 0.0;
    for (uint i = 0u; i < DDGI_LOCAL_SIZE; i++)
    {
        totalRayCount += SharedRadianceAndRayCount[i].w;
        totalHitCount += SharedVisibilityAndHitCount[i].z;
        totalRelocation += SharedRelocationAndCloseCount[i].xyz;
        totalCloseCount += SharedRelocationAndCloseCount[i].w;
        totalBackfaceCount += SharedBackfaceAndMissCount[i].x;
        totalMissCount += SharedBackfaceAndMissCount[i].y;
    }

    float invRayCount = 1.0 / max(totalRayCount, 1.0);
    float closeRatio = clamp(totalCloseCount * invRayCount, 0.0, 1.0);
    float backfaceRatio = clamp(totalBackfaceCount * invRayCount, 0.0, 1.0);
    float missRatio = clamp(totalMissCount * invRayCount, 0.0, 1.0);
    float hitRatio = clamp(totalHitCount * invRayCount, 0.0, 1.0);
    float invalidProbeScore = max(
        smoothstep(0.25, 0.45, closeRatio),
        smoothstep(0.40, 0.60, backfaceRatio));
    float historyValid = clamp(previousStateHistory.w, 0.0, 1.0);
    float blendAlpha = historyValid > 0.5 ? 1.0 - hysteresis : 1.0;
    float stateBlendAlpha = historyValid > 0.5 ? clamp(max(blendAlpha, 0.08), 0.0, 1.0) : 1.0;
    float previousActiveProbe = historyValid > 0.5
        ? clamp(min(previousState.w, previousRelocationAndClassification.w), 0.0, 1.0)
        : 1.0;
    float classifiedActiveProbe = historyValid > 0.5
        ? (previousActiveProbe > 0.5
            ? (invalidProbeScore > 0.65 ? 0.0 : 1.0)
            : (invalidProbeScore < 0.35 ? 1.0 : 0.0))
        : (invalidProbeScore > 0.5 ? 0.0 : 1.0);
    float targetActiveProbe = classificationEnabled ? classifiedActiveProbe : 1.0;
    float activeProbe = mix(previousActiveProbe, targetActiveProbe, stateBlendAlpha);
    vec3 relocationDirection = length(totalRelocation) > 0.0001 ? normalize(totalRelocation) : vec3(0.0);
    float unclampedRelocationDistance = closeRatio * max(normalBias + viewBias, 0.01) * 4.0;
    float minProbeSpacing = max(min(min(probeSpacing.x, probeSpacing.y), probeSpacing.z), 0.001);
    float maxRelocationDistance = 0.4 * minProbeSpacing;
    float relocationDistance = relocationEnabled ? clamp(unclampedRelocationDistance, 0.0, maxRelocationDistance) : 0.0;
    vec3 relocation = relocationEnabled ? relocationDirection * relocationDistance : vec3(0.0);
    vec3 blendedRelocation = historyValid > 0.5
        ? mix(previousRelocationAndClassification.xyz, relocation, stateBlendAlpha)
        : relocation;

    float rayHitConfidence = clamp(hitRatio * (1.0 - backfaceRatio), 0.0, 1.0);
    float luminanceChange = clamp(previousStateHistory.z, 0.0, 1.0);
    float luminanceConfidence = 1.0 - luminanceChange * 0.45;
    float irradianceConfidence = clamp(activeProbe * (1.0 - invalidProbeScore) * (1.0 - missRatio * 0.5) * luminanceConfidence, 0.0, 1.0);
    float visibilityConfidence = clamp((hitRatio + missRatio * 0.35) * (1.0 - closeRatio * 0.5), 0.0, 1.0);
    vec3 qualityConfidence = vec3(rayHitConfidence, irradianceConfidence, visibilityConfidence);
    vec3 blendedQualityConfidence = historyValid > 0.5
        ? mix(previousQualityAndReason.xyz, qualityConfidence, stateBlendAlpha)
        : qualityConfidence;
    float lastUpdateReason = float(ResolvePrimaryProbeUpdateReason(request.Flags));

    vec4 currentIrradiance = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
    WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase, vec4(currentIrradiance.rgb, activeProbe));
    WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u, vec4(blendedRelocation, activeProbe));
    WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(blendedQualityConfidence, lastUpdateReason));
    WriteStorageWord(pc.ProbeStateBufferIndex, stateBase + 16u, pc.CurrentFrameIndex);

    if (relocationEnabled || classificationEnabled)
    {
        uint relocationBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION) / 4u);
        WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase, vec4(blendedRelocation, relocationDistance));
        WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 4u, vec4(activeProbe, classificationEnabled ? invalidProbeScore : 0.0, closeRatio, backfaceRatio));
    }
}
#endif

#endif
