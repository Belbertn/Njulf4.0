#ifndef NJULF_DDGI_SIMPLE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_SHARED_GLSL

#include "farfield_clipmap.glsl"

const float SIMPLE_DDGI_PI = 3.14159265359;
const uint SIMPLE_DDGI_FLAG_ENABLED = 1u << 0;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_ENABLED = 1u << 1;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_FORCE_ALL = 1u << 2;
const uint SIMPLE_DDGI_FLAG_FOG_ENABLED = 1u << 3;
const uint SIMPLE_DDGI_FLAG_PARTICLE_ENABLED = 1u << 4;
const uint SIMPLE_DDGI_FLAG_ADAPTIVE_HYSTERESIS = 1u << 5;
const uint SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED = 1u << 6;
const uint SIMPLE_DDGI_FLAG_FAR_SUN_SHADOW_ENABLED = 1u << 7;
// Feature gate for the support-aware gather/composition path.  When disabled,
// callers receive explicit no-support and can take the environment-only safety
// fallback rather than sampling an obsolete atlas convention.
const uint SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED = 1u << 8;
// Detailed investigation counters are disabled in normal production rendering
// so per-ray/per-texel atomic traffic is never part of the steady-state cost.
const uint SIMPLE_DDGI_FLAG_DETAILED_DIAGNOSTICS_ENABLED = 1u << 9;
// The CPU knows when a real lighting edit is propagating.  Adaptive history must
// not mistake ordinary low-sample Monte-Carlo variation for that edit.
const uint SIMPLE_DDGI_FLAG_LIGHTING_CHANGE_ACTIVE = 1u << 10;
const uint SIMPLE_DDGI_IRRADIANCE_TEXELS = 8u;
const uint SIMPLE_DDGI_VISIBILITY_TEXELS = 16u;
const uint SIMPLE_DDGI_MAX_RAYS_PER_PROBE = 256u;
const uint SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_HEADER_WORDS = 40u;
const uint SIMPLE_DDGI_VOLUME_STRIDE_WORDS = 24u;
const uint SIMPLE_DDGI_MAX_VOLUME_COUNT = 16u;
const uint SIMPLE_DDGI_VOLUME_KIND_LEGACY = 0u;
const uint SIMPLE_DDGI_VOLUME_KIND_AUTHORED = 1u;
const uint SIMPLE_DDGI_VOLUME_KIND_RING = 2u;
const uint SIMPLE_DDGI_AUTHORED_VOLUME_CASCADE = 0xffffffffu;
const uint SIMPLE_DDGI_PROBE_STATE_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_RELOCATION_CLASSIFICATION_STRIDE_WORDS = 12u;
const uint SIMPLE_DDGI_PROBE_FLAG_FRESH = 1u << 0;
const uint SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED = 1u << 1;
const uint SIMPLE_DDGI_PROBE_FLAG_INACTIVE = 1u << 2;
const uint SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT = 3u;
const uint SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_MASK = 0x7u << SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT;
const uint SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT = 6u;
const uint SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_MASK = 0x3fu << SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT;
const uint SIMPLE_DDGI_UPDATE_MAINTENANCE = 1u << 12;
// The remaining state-flag bits carry a non-zero physical-slot generation.  An
// update recorded for an old toroidal mapping must never mutate the slot after it
// has been re-exposed for a new world cell.
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT = 8u;
const uint SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK = 0xffffff00u;
const uint SIMPLE_DDGI_PROBE_FLAG_RAY_COUNT_SHIFT = 16u;
const uint SIMPLE_DDGI_PROBE_FLAG_RAY_COUNT_MASK = 0xffff0000u;
const uint SIMPLE_DDGI_UPDATE_GENERATION_MASK = 0x00ffffffu;
const uint SIMPLE_DDGI_UPDATE_AGE_SHIFT = 24u;
const uint SIMPLE_DDGI_UPDATE_AGE_MASK = 0xff000000u;
const uint SIMPLE_DDGI_CLASSIFICATION_ACTIVE = 0u;
const uint SIMPLE_DDGI_CLASSIFICATION_INACTIVE = 1u;

struct SimpleDdgiParams
{
    vec3 origin;
    float spacing;
    uvec3 gridCount;
    uint probeCount;
    uint irradianceTexels;
    uint visibilityTexels;
    uint raysPerProbe;
    uint farFieldResolution;
    float hysteresis;
    uint frameIndex;
    uint flags;
    float farFieldStartDistance;
    vec3 environmentRadiance;
    float environmentIntensity;
    float environmentFallbackIntensity;
    uint updateStartProbe;
    uint probesToUpdate;
    float selfShadowBiasScale;
    float indirectIntensity;
    uint debugView;
    uint farFieldMaxTraceSteps;
    vec4 rayRotation;
    float normalBias;
    float viewBias;
    float hysteresisChangeThreshold;
    float hysteresisStepThreshold;
    uint volumeCount;
    // Optional sampled-image mirror metadata. The SSBO remains canonical and
    // handles octahedral seam filtering, while images accelerate interior taps.
    uint sampledAtlasLayersPerTexture;
    uint sampledAtlasTextureGroupCount;
    uint sampledAtlasEnabled;
};

bool SimpleDdgiDetailedDiagnosticsEnabled(SimpleDdgiParams params)
{
    return (params.flags & SIMPLE_DDGI_FLAG_DETAILED_DIAGNOSTICS_ENABLED) != 0u;
}

void AddSimpleDdgiDiagnostic(SimpleDdgiParams params, uint frameIndex, uint counterIndex, uint value)
{
    if (SimpleDdgiDetailedDiagnosticsEnabled(params))
        AddRendererDiagnostic(frameIndex, counterIndex, value);
}

struct SimpleDdgiVolume
{
    vec3 origin;
    float spacing;
    uvec3 gridCount;
    uint firstProbeIndex;
    vec3 worldMin;
    float edgeFadeDistance;
    vec3 worldMax;
    uint kind;
    uint updateStartProbe;
    uint probesToUpdate;
    uint sourceOrdinal;
    uvec3 physicalOffset;
};

struct SimpleDdgiDebugSample
{
    uint probeIndex;
    uint volumeIndex;
    vec3 logicalProbePosition;
    vec3 relocatedProbePosition;
    float visibility;
    float visibilityConfidence;
    float visibilityMomentMean;
    float visibilityMomentVariance;
    float visibilityProbeDistance;
    float visibilityMaxRayDistance;
};

struct SimpleDdgiProbeState
{
    vec3 relocation;
    float activeWeight;
    uint flags;
    uint age;
    uint classification;
    float luminanceChangeEma;
};

struct SimpleDdgiProbeUpdate
{
    uint probeIndex;
    uint volumeIndex;
    uint flags;
    uint expectedGeneration;
};

uint SimpleDdgiProbeStateBase(uint probeIndex)
{
    return probeIndex * SIMPLE_DDGI_PROBE_STATE_STRIDE_WORDS;
}

SimpleDdgiProbeState ReadSimpleDdgiProbeState(uint bufferIndex, uint probeIndex)
{
    uint baseWord = SimpleDdgiProbeStateBase(probeIndex);
    vec4 relocationAndActive = ReadStorageVec4(bufferIndex, baseWord);
    SimpleDdgiProbeState state;
    state.relocation = relocationAndActive.xyz;
    state.activeWeight = relocationAndActive.w;
    state.flags = ReadStorageWord(bufferIndex, baseWord + 4u);
    state.age = ReadStorageWord(bufferIndex, baseWord + 5u);
    state.classification = ReadStorageWord(bufferIndex, baseWord + 6u);
    state.luminanceChangeEma = uintBitsToFloat(ReadStorageWord(bufferIndex, baseWord + 7u));
    return state;
}

void WriteSimpleDdgiProbeState(uint bufferIndex, uint probeIndex, SimpleDdgiProbeState state)
{
    uint baseWord = SimpleDdgiProbeStateBase(probeIndex);
    WriteStorageVec4(bufferIndex, baseWord, vec4(state.relocation, state.activeWeight));
    WriteStorageWord(bufferIndex, baseWord + 4u, state.flags);
    WriteStorageWord(bufferIndex, baseWord + 5u, state.age);
    WriteStorageWord(bufferIndex, baseWord + 6u, state.classification);
    WriteStorageWord(bufferIndex, baseWord + 7u, floatBitsToUint(max(state.luminanceChangeEma, 0.0)));
}

SimpleDdgiProbeUpdate ReadSimpleDdgiProbeUpdate(uint bufferIndex, uint queueOffset)
{
    uint baseWord = queueOffset * SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS;
    SimpleDdgiProbeUpdate update;
    update.probeIndex = ReadStorageWord(bufferIndex, baseWord);
    update.volumeIndex = ReadStorageWord(bufferIndex, baseWord + 1u);
    update.flags = ReadStorageWord(bufferIndex, baseWord + 2u);
    update.expectedGeneration = ReadStorageWord(bufferIndex, baseWord + 3u);
    return update;
}

uint SimpleDdgiProbeGeneration(SimpleDdgiProbeState state)
{
    return (state.flags & SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK) >> SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT;
}

bool SimpleDdgiUpdateMatchesProbeGeneration(SimpleDdgiProbeUpdate update, SimpleDdgiProbeState state)
{
    uint expectedGeneration = update.expectedGeneration & SIMPLE_DDGI_UPDATE_GENERATION_MASK;
    return expectedGeneration != 0u && expectedGeneration == SimpleDdgiProbeGeneration(state);
}

uint SimpleDdgiUpdateAge(SimpleDdgiProbeUpdate update)
{
    return max((update.expectedGeneration & SIMPLE_DDGI_UPDATE_AGE_MASK) >> SIMPLE_DDGI_UPDATE_AGE_SHIFT, 1u);
}

bool SimpleDdgiUpdateIsMaintenance(SimpleDdgiProbeUpdate update)
{
    return (update.flags & SIMPLE_DDGI_UPDATE_MAINTENANCE) != 0u;
}

uint SimpleDdgiUpdateRayCount(SimpleDdgiProbeUpdate update, SimpleDdgiParams p)
{
    uint packed = (update.flags & SIMPLE_DDGI_PROBE_FLAG_RAY_COUNT_MASK) >> SIMPLE_DDGI_PROBE_FLAG_RAY_COUNT_SHIFT;
    return clamp(packed == 0u ? p.raysPerProbe : packed, 1u, min(p.raysPerProbe, SIMPLE_DDGI_MAX_RAYS_PER_PROBE));
}

int SimpleDdgiUpdateMaterialTextureMaxCascade(SimpleDdgiProbeUpdate update)
{
    uint packed = (update.flags & SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_MASK) >> SIMPLE_DDGI_UPDATE_MATERIAL_TEXTURE_CASCADE_SHIFT;
    return int(packed) - 1;
}

uint SimpleDdgiUpdateMaxShadedLights(SimpleDdgiProbeUpdate update, uint fallback)
{
    uint packed = (update.flags & SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_MASK) >> SIMPLE_DDGI_UPDATE_MAX_SHADED_LIGHTS_SHIFT;
    return min(packed, fallback);
}

SimpleDdgiParams ReadSimpleDdgiParams(uint bufferIndex)
{
    SimpleDdgiParams p;
    vec4 originAndSpacing = ReadStorageVec4(bufferIndex, 0u);
    vec4 grid = ReadStorageVec4(bufferIndex, 4u);
    vec4 atlas = ReadStorageVec4(bufferIndex, 8u);
    vec4 hysteresis = ReadStorageVec4(bufferIndex, 12u);
    vec4 environment = ReadStorageVec4(bufferIndex, 16u);
    vec4 updateRange = ReadStorageVec4(bufferIndex, 20u);
    vec4 debugAndBias = ReadStorageVec4(bufferIndex, 24u);
    vec4 rotation = ReadStorageVec4(bufferIndex, 28u);
    vec4 bias = ReadStorageVec4(bufferIndex, 32u);
    vec4 reserved = ReadStorageVec4(bufferIndex, 36u);
    p.origin = originAndSpacing.xyz;
    p.spacing = max(originAndSpacing.w, 0.001);
    p.gridCount = uvec3(max(grid.xyz, vec3(1.0)));
    p.probeCount = uint(max(grid.w, 0.0));
    p.irradianceTexels = max(uint(atlas.x), 1u);
    p.visibilityTexels = max(uint(atlas.y), 1u);
    p.raysPerProbe = max(uint(atlas.z), 1u);
    p.farFieldResolution = max(uint(atlas.w), 1u);
    p.hysteresis = clamp(hysteresis.x, 0.0, 0.995);
    p.frameIndex = uint(hysteresis.y);
    p.flags = uint(hysteresis.z);
    p.farFieldStartDistance = max(hysteresis.w, 0.0);
    p.environmentRadiance = max(environment.xyz, vec3(0.0));
    p.environmentIntensity = max(environment.w, 0.0);
    p.environmentFallbackIntensity = clamp(updateRange.w, 0.0, 4.0);
    p.updateStartProbe = uint(max(updateRange.x, 0.0));
    p.probesToUpdate = uint(max(updateRange.y, 0.0));
    p.debugView = uint(max(debugAndBias.x, 0.0));
    p.selfShadowBiasScale = max(debugAndBias.y, 0.0);
    p.indirectIntensity = max(debugAndBias.z, 0.0);
    p.farFieldMaxTraceSteps = max(uint(debugAndBias.w), 1u);
    p.rayRotation = dot(rotation, rotation) > 0.000001 ? normalize(rotation) : vec4(0.0, 0.0, 0.0, 1.0);
    p.normalBias = max(bias.x, 0.0);
    p.viewBias = max(bias.y, 0.0);
    p.hysteresisChangeThreshold = max(bias.z, 0.001);
    p.hysteresisStepThreshold = max(max(bias.w, 0.001), p.hysteresisChangeThreshold);
    p.volumeCount = min(uint(max(max(reserved.x, updateRange.z), 0.0)), SIMPLE_DDGI_MAX_VOLUME_COUNT);
    p.sampledAtlasLayersPerTexture = uint(max(reserved.y, 0.0));
    p.sampledAtlasTextureGroupCount = uint(max(reserved.z, 0.0));
    p.sampledAtlasEnabled = uint(max(reserved.w, 0.0));
    return p;
}

SimpleDdgiVolume ReadSimpleDdgiVolume(uint bufferIndex, uint volumeIndex)
{
    uint baseWord = SIMPLE_DDGI_HEADER_WORDS + volumeIndex * SIMPLE_DDGI_VOLUME_STRIDE_WORDS;
    vec4 originAndSpacing = ReadStorageVec4(bufferIndex, baseWord + 0u);
    vec4 gridAndFirst = ReadStorageVec4(bufferIndex, baseWord + 4u);
    vec4 worldMinAndEdge = ReadStorageVec4(bufferIndex, baseWord + 8u);
    vec4 worldMaxAndKind = ReadStorageVec4(bufferIndex, baseWord + 12u);
    vec4 updateRange = ReadStorageVec4(bufferIndex, baseWord + 16u);
    vec4 raysAndReserved = ReadStorageVec4(bufferIndex, baseWord + 20u);

    SimpleDdgiVolume volume;
    volume.origin = originAndSpacing.xyz;
    volume.spacing = max(originAndSpacing.w, 0.001);
    volume.gridCount = uvec3(max(gridAndFirst.xyz, vec3(1.0)));
    volume.firstProbeIndex = uint(max(gridAndFirst.w, 0.0));
    volume.worldMin = worldMinAndEdge.xyz;
    volume.edgeFadeDistance = max(worldMinAndEdge.w, volume.spacing);
    volume.worldMax = worldMaxAndKind.xyz;
    volume.kind = uint(max(worldMaxAndKind.w, 0.0));
    volume.updateStartProbe = uint(max(updateRange.x, 0.0));
    volume.probesToUpdate = uint(max(updateRange.y, 0.0));
    volume.sourceOrdinal = uint(max(raysAndReserved.x, 0.0));
    volume.physicalOffset = uvec3(max(raysAndReserved.yzw, vec3(0.0)));
    return volume;
}

uint SimpleDdgiVolumeQualityCascade(SimpleDdgiVolume volume)
{
    if (volume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED)
        return SIMPLE_DDGI_AUTHORED_VOLUME_CASCADE;
    if (volume.kind != 2u || volume.sourceOrdinal < 10000u)
        return 0u;
    return min(volume.sourceOrdinal - 10000u, 3u);
}

bool SimpleDdgiContains(SimpleDdgiVolume volume, vec3 worldPosition)
{
    return all(greaterThanEqual(worldPosition, volume.worldMin)) &&
        all(lessThanEqual(worldPosition, volume.worldMax));
}

float SimpleDdgiEdgeWeight(SimpleDdgiVolume volume, vec3 worldPosition)
{
    vec3 distanceToFace = min(worldPosition - volume.worldMin, volume.worldMax - worldPosition);
    float edgeDistance = min(min(distanceToFace.x, distanceToFace.y), distanceToFace.z);
    return smoothstep(0.0, max(volume.edgeFadeDistance, 0.001), edgeDistance);
}

uint SimpleDdgiVolumeProbeCount(SimpleDdgiVolume volume)
{
    return volume.gridCount.x * volume.gridCount.y * volume.gridCount.z;
}

uint SimpleDdgiProbeIndex(uvec3 coord, SimpleDdgiParams p)
{
    return coord.x + coord.y * p.gridCount.x + coord.z * p.gridCount.x * p.gridCount.y;
}

uint SimpleDdgiProbeIndex(uvec3 coord, SimpleDdgiVolume volume)
{
    // Ring coordinates are logical/world-relative.  Atlas/state storage is
    // toroidal, so preserved cells retain their physical slot without a copy.
    uvec3 physical = (coord + volume.physicalOffset) % max(volume.gridCount, uvec3(1u));
    return volume.firstProbeIndex + physical.x + physical.y * volume.gridCount.x + physical.z * volume.gridCount.x * volume.gridCount.y;
}

uvec3 SimpleDdgiProbeCoord(uint probeIndex, SimpleDdgiParams p)
{
    uint xy = max(p.gridCount.x * p.gridCount.y, 1u);
    uint z = probeIndex / xy;
    uint rem = probeIndex - z * xy;
    uint y = rem / max(p.gridCount.x, 1u);
    uint x = rem - y * max(p.gridCount.x, 1u);
    return uvec3(x, y, z);
}

vec3 SimpleDdgiProbeWorldPosition(uint probeIndex, SimpleDdgiParams p)
{
    return p.origin + vec3(SimpleDdgiProbeCoord(probeIndex, p)) * p.spacing;
}

bool ResolveSimpleDdgiProbeVolume(uint globalProbeIndex, SimpleDdgiParams p, out SimpleDdgiVolume volume, out uint localProbeIndex)
{
    for (uint volumeIndex = 0u; volumeIndex < p.volumeCount; volumeIndex++)
    {
        SimpleDdgiVolume candidate = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), volumeIndex);
        uint count = SimpleDdgiVolumeProbeCount(candidate);
        if (globalProbeIndex >= candidate.firstProbeIndex && globalProbeIndex < candidate.firstProbeIndex + count)
        {
            volume = candidate;
            localProbeIndex = globalProbeIndex - candidate.firstProbeIndex;
            return true;
        }
    }

    volume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), 0u);
    localProbeIndex = 0u;
    return false;
}

uvec3 SimpleDdgiProbeCoord(uint localProbeIndex, SimpleDdgiVolume volume)
{
    uint xy = max(volume.gridCount.x * volume.gridCount.y, 1u);
    uint z = localProbeIndex / xy;
    uint rem = localProbeIndex - z * xy;
    uint y = rem / max(volume.gridCount.x, 1u);
    uint x = rem - y * max(volume.gridCount.x, 1u);
    uvec3 physical = uvec3(x, y, z);
    return (physical + volume.gridCount - (volume.physicalOffset % volume.gridCount)) % volume.gridCount;
}

vec3 SimpleDdgiProbeWorldPosition(uint globalProbeIndex, SimpleDdgiParams p, out uint volumeIndexOut)
{
    for (uint volumeIndex = 0u; volumeIndex < p.volumeCount; volumeIndex++)
    {
        SimpleDdgiVolume volume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), volumeIndex);
        uint count = SimpleDdgiVolumeProbeCount(volume);
        if (globalProbeIndex >= volume.firstProbeIndex && globalProbeIndex < volume.firstProbeIndex + count)
        {
            volumeIndexOut = volumeIndex;
            return volume.origin + vec3(SimpleDdgiProbeCoord(globalProbeIndex - volume.firstProbeIndex, volume)) * volume.spacing;
        }
    }

    volumeIndexOut = 0u;
    return SimpleDdgiProbeWorldPosition(globalProbeIndex, p);
}

vec3 SimpleDdgiProbeLogicalPosition(SimpleDdgiVolume volume, uint localProbeIndex)
{
    return volume.origin + vec3(SimpleDdgiProbeCoord(localProbeIndex, volume)) * volume.spacing;
}

vec3 SimpleDdgiProbeRelocatedPosition(uint probeIndex, SimpleDdgiVolume volume, uint localProbeIndex)
{
    SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX), probeIndex);
    return SimpleDdgiProbeLogicalPosition(volume, localProbeIndex) + state.relocation;
}

vec2 SimpleDdgiOctEncode(vec3 n)
{
    n /= max(abs(n.x) + abs(n.y) + abs(n.z), 0.000001);
    vec2 encoded = n.xy;
    if (n.z < 0.0)
        encoded = (1.0 - abs(encoded.yx)) * sign(encoded.xy);
    return encoded * 0.5 + 0.5;
}

vec3 SimpleDdgiOctDecode(vec2 e)
{
    vec2 f = e * 2.0 - 1.0;
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = clamp(-n.z, 0.0, 1.0);
    n.xy += vec2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

vec3 SimpleDdgiRotateByQuaternion(vec3 v, vec4 q)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

vec4 SimpleDdgiMultiplyQuaternions(vec4 left, vec4 right)
{
    return vec4(
        left.w * right.xyz + right.w * left.xyz + cross(left.xyz, right.xyz),
        left.w * right.w - dot(left.xyz, right.xyz));
}

uint SimpleDdgiHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

float SimpleDdgiHashToUnitFloat(uint value, uint salt)
{
    return float(SimpleDdgiHash(value ^ salt) >> 8u) * (1.0 / 16777216.0);
}

// Compose a stable, uniform rotation for every probe with the frame rotation.
// This preserves per-probe temporal sampling while preventing all probes from
// moving through the same Monte-Carlo error pattern together.
vec4 SimpleDdgiPerProbeRayRotation(uint probeIndex, vec4 frameRotation)
{
    float u1 = SimpleDdgiHashToUnitFloat(probeIndex, 0x9e3779b9u);
    float u2 = SimpleDdgiHashToUnitFloat(probeIndex, 0x7f4a7c15u);
    float u3 = SimpleDdgiHashToUnitFloat(probeIndex, 0x94d049bbu);
    float r1 = sqrt(max(0.0, 1.0 - u1));
    float r2 = sqrt(max(0.0, u1));
    float theta1 = 2.0 * SIMPLE_DDGI_PI * u2;
    float theta2 = 2.0 * SIMPLE_DDGI_PI * u3;
    vec4 probeRotation = vec4(
        r1 * sin(theta1), r1 * cos(theta1),
        r2 * sin(theta2), r2 * cos(theta2));
    return normalize(SimpleDdgiMultiplyQuaternions(frameRotation, probeRotation));
}

vec3 SimpleDdgiFibonacciDirection(uint rayIndex, uint rayCount, vec4 rayRotation)
{
    float i = float(rayIndex);
    float n = max(float(rayCount), 1.0);
    float golden = 2.399963229728653;
    float z = 1.0 - 2.0 * (i + 0.5) / n;
    float radius = sqrt(max(0.0, 1.0 - z * z));
    float angle = golden * i;
    return normalize(SimpleDdgiRotateByQuaternion(vec3(cos(angle) * radius, sin(angle) * radius, z), rayRotation));
}

uint SimpleDdgiAtlasWord(uint probeIndex, uint texelIndex, uint texelsPerProbe)
{
    return (probeIndex * texelsPerProbe * texelsPerProbe + texelIndex) * 2u;
}

vec4 ReadSimpleDdgiAtlasTexel(uint bufferIndex, uint probeIndex, uint texelIndex, uint texelsPerProbe)
{
    uint word = SimpleDdgiAtlasWord(probeIndex, texelIndex, texelsPerProbe);
    vec2 xy = unpackHalf2x16(ReadStorageWord(bufferIndex, word));
    vec2 zw = unpackHalf2x16(ReadStorageWord(bufferIndex, word + 1u));
    return vec4(xy, zw);
}

void WriteSimpleDdgiAtlasTexel(uint bufferIndex, uint probeIndex, uint texelIndex, uint texelsPerProbe, vec4 value)
{
    value = clamp(value, vec4(0.0), vec4(65504.0));
    uint word = SimpleDdgiAtlasWord(probeIndex, texelIndex, texelsPerProbe);
    WriteStorageWord(bufferIndex, word, packHalf2x16(value.xy));
    WriteStorageWord(bufferIndex, word + 1u, packHalf2x16(value.zw));
}

uint SimpleDdgiDirectionTexel(vec3 direction, uint texelsPerProbe)
{
    vec2 uv = clamp(SimpleDdgiOctEncode(direction), vec2(0.0), vec2(0.999999));
    uvec2 xy = uvec2(floor(uv * float(texelsPerProbe)));
    xy = min(xy, uvec2(texelsPerProbe - 1u));
    return xy.x + xy.y * texelsPerProbe;
}

uint SimpleDdgiMirrorOctTexelIndex(ivec2 coord, uint texelsPerProbe)
{
    int n = int(texelsPerProbe);
    ivec2 c = coord;
    if (c.x < 0)
    {
        c.x = -c.x - 1;
        c.y = n - 1 - c.y;
    }
    else if (c.x >= n)
    {
        c.x = 2 * n - c.x - 1;
        c.y = n - 1 - c.y;
    }

    if (c.y < 0)
    {
        c.y = -c.y - 1;
        c.x = n - 1 - c.x;
    }
    else if (c.y >= n)
    {
        c.y = 2 * n - c.y - 1;
        c.x = n - 1 - c.x;
    }

    c = clamp(c, ivec2(0), ivec2(n - 1));
    return uint(c.x) + uint(c.y) * texelsPerProbe;
}

bool SimpleDdgiCanSampleAtlasImage(SimpleDdgiParams p, uint bufferIndex, uint probeIndex)
{
    if (p.sampledAtlasEnabled == 0u ||
        p.sampledAtlasLayersPerTexture == 0u ||
        p.sampledAtlasTextureGroupCount == 0u ||
        (bufferIndex != uint(SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX) &&
         bufferIndex != uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX)))
    {
        return false;
    }

    uint groupIndex = probeIndex / p.sampledAtlasLayersPerTexture;
    return groupIndex < p.sampledAtlasTextureGroupCount &&
        groupIndex < uint(SIMPLE_DDGI_SAMPLED_ATLAS_TEXTURE_GROUP_COUNT);
}

vec4 SampleSimpleDdgiAtlasImage(
    SimpleDdgiParams p,
    uint bufferIndex,
    uint probeIndex,
    vec2 encodedDirection)
{
    uint groupIndex = probeIndex / p.sampledAtlasLayersPerTexture;
    uint layerIndex = probeIndex - groupIndex * p.sampledAtlasLayersPerTexture;
    int textureIndex = bufferIndex == uint(SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX)
        ? SIMPLE_DDGI_SAMPLED_IRRADIANCE_TEXTURE_BASE_INDEX + int(groupIndex)
        : SIMPLE_DDGI_SAMPLED_VISIBILITY_TEXTURE_BASE_INDEX + int(groupIndex);
    return texture(
        BindlessArrayTextures[nonuniformEXT(textureIndex)],
        vec3(clamp(encodedDirection, vec2(0.0), vec2(1.0)), float(layerIndex)));
}

vec4 SampleSimpleDdgiAtlasBilinear(
    uint bufferIndex,
    uint probeIndex,
    vec3 direction,
    uint texelsPerProbe,
    SimpleDdgiParams p)
{
    vec2 encodedDirection = SimpleDdgiOctEncode(direction);
    vec2 texelUv = encodedDirection * float(texelsPerProbe) - vec2(0.5);
    ivec2 base = ivec2(floor(texelUv));
    vec2 f = fract(texelUv);
    // Hardware filtering is exactly equivalent for a strictly interior quad.
    // At octahedral seams retain the SSBO mirror lookup, which preserves the
    // established cross-edge convention instead of clamping the image border.
    if (all(greaterThanEqual(base, ivec2(0))) &&
        all(lessThan(base + ivec2(1), ivec2(int(texelsPerProbe)))) &&
        SimpleDdgiCanSampleAtlasImage(p, bufferIndex, probeIndex))
    {
        return SampleSimpleDdgiAtlasImage(p, bufferIndex, probeIndex, encodedDirection);
    }

    vec4 s00 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base, texelsPerProbe), texelsPerProbe);
    vec4 s10 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 0), texelsPerProbe), texelsPerProbe);
    vec4 s01 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(0, 1), texelsPerProbe), texelsPerProbe);
    vec4 s11 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 1), texelsPerProbe), texelsPerProbe);
    return mix(mix(s00, s10, f.x), mix(s01, s11, f.x), f.y);
}

float SimpleDdgiChebyshev(float mean, float mean2, float receiverDistance, float probeSpacing)
{
    if (receiverDistance <= mean)
        return 1.0;
    // Visibility moments represent progressively larger cells in outer rings.
    // A fixed world-space floor becomes nearly binary at 4-16 m spacing and lets
    // sparse ray noise turn otherwise valid coarse-ring samples completely black.
    float varianceFloor = max(0.0025, probeSpacing * probeSpacing * 0.0025);
    float variance = max(mean2 - mean * mean, varianceFloor);
    float d = receiverDistance - mean;
    return clamp(variance / (variance + d * d), 0.0, 1.0);
}

vec3 SimpleDdgiBiasedSamplePosition(vec3 worldPos, vec3 normal, vec3 viewDir, SimpleDdgiParams p, float volumeSpacing)
{
    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    vec3 safeView = length(viewDir) > 0.00001 ? normalize(viewDir) : safeNormal;
    // Settings are expressed as spacing-relative scales.  Clamp them to small,
    // safe world-space limits so a coarse far ring cannot push a lookup through
    // a wall and a fine authored volume still avoids self-intersection.
    float spacing = max(volumeSpacing, 0.001);
    float normalBias = clamp(p.normalBias * spacing, 0.002, max(0.01, spacing * 0.20));
    float viewBias = clamp(p.viewBias * spacing, 0.0, max(0.01, spacing * 0.35));
    return worldPos + safeNormal * normalBias + safeView * viewBias;
}

vec3 SimpleDdgiBiasedSamplePosition(vec3 worldPos, vec3 normal, vec3 viewDir, SimpleDdgiParams p)
{
    return SimpleDdgiBiasedSamplePosition(worldPos, normal, viewDir, p, p.spacing);
}

bool SelectSimpleDdgiVolume(SimpleDdgiParams p, vec3 worldPosition, out uint selectedVolumeIndex, out SimpleDdgiVolume selectedVolume, out float selectedEdgeWeight)
{
    for (uint volumeIndex = 0u; volumeIndex < p.volumeCount; volumeIndex++)
    {
        SimpleDdgiVolume volume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), volumeIndex);
        if (!SimpleDdgiContains(volume, worldPosition))
            continue;

        selectedVolumeIndex = volumeIndex;
        selectedVolume = volume;
        selectedEdgeWeight = SimpleDdgiEdgeWeight(volume, worldPosition);
        return true;
    }

    selectedVolumeIndex = 0u;
    selectedVolume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), 0u);
    selectedEdgeWeight = 0.0;
    return false;
}

struct SimpleDdgiGatherResult
{
    vec3 irradiance;
    float validSupport;
    float directionalSupport;
    float spatialCoverage;
    float transportVisibility;
    float ownership;
    uint selectedVolume;
    float selectedSpacing;
    uint validProbeCount;
};

SimpleDdgiGatherResult EmptySimpleDdgiGatherResult()
{
    SimpleDdgiGatherResult result;
    result.irradiance = vec3(0.0);
    result.validSupport = 0.0;
    result.directionalSupport = 0.0;
    result.spatialCoverage = 0.0;
    result.transportVisibility = 0.0;
    result.ownership = 0.0;
    result.selectedVolume = 0u;
    result.selectedSpacing = 0.0;
    result.validProbeCount = 0u;
    return result;
}

float SimpleDdgiRadiometricOwnership(SimpleDdgiGatherResult gather)
{
    // Probe validity mass is a confidence and interpolation signal, not an
    // energy term. Once a normalized gather has usable support, DDGI owns the
    // spatially covered share. Otherwise inactive probes next to geometry stamp
    // their trilinear support pattern back onto the final irradiance field.
    return gather.validSupport > 0.000001
        ? clamp(gather.spatialCoverage, 0.0, 1.0)
        : 0.0;
}

bool SimpleDdgiProbeSupportsGather(SimpleDdgiProbeState state, vec4 irradiance, vec4 visibility)
{
    uint invalidFlags = SIMPLE_DDGI_PROBE_FLAG_FRESH |
        SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED |
        SIMPLE_DDGI_PROBE_FLAG_INACTIVE;
    return (state.flags & invalidFlags) == 0u &&
        state.classification != SIMPLE_DDGI_CLASSIFICATION_INACTIVE &&
        state.activeWeight > 0.001 &&
        irradiance.w > 0.5 &&
        visibility.z > 0.5;
}

SimpleDdgiGatherResult SampleSimpleDdgiVolumeGather(
    SimpleDdgiParams p,
    SimpleDdgiVolume volume,
    uint volumeIndex,
    vec3 biasedWorldPos,
    vec3 safeNormal)
{
    SimpleDdgiGatherResult result = EmptySimpleDdgiGatherResult();
    result.selectedVolume = volumeIndex;
    result.selectedSpacing = volume.spacing;
    vec3 grid = (biasedWorldPos - volume.origin) / volume.spacing;
    vec3 baseF = floor(grid);
    vec3 fracV = clamp(grid - baseF, vec3(0.0), vec3(1.0));
    ivec3 base = ivec3(baseF);
    vec3 accumulated = vec3(0.0);
    float validMass = 0.0;
    float directionalMass = 0.0;
    float visibleMass = 0.0;

    for (uint z = 0u; z < 2u; z++)
    for (uint y = 0u; y < 2u; y++)
    for (uint x = 0u; x < 2u; x++)
    {
        ivec3 c = base + ivec3(int(x), int(y), int(z));
        if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(volume.gridCount))))
            continue;

        vec3 w3 = mix(1.0 - fracV, fracV, vec3(x, y, z));
        float trilinear = w3.x * w3.y * w3.z;
        result.spatialCoverage += trilinear;

        uint probeIndex = SimpleDdgiProbeIndex(uvec3(c), volume);
        SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX), probeIndex);
        vec3 probePos = volume.origin + vec3(c) * volume.spacing + state.relocation;
        vec3 toSurface = biasedWorldPos - probePos;
        float distanceToProbe = length(toSurface);
        vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : safeNormal;
        vec4 irradiance = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX), probeIndex, safeNormal, p.irradianceTexels, p);
        vec4 moments = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), probeIndex, probeToSurface, p.visibilityTexels, p);
        if (!SimpleDdgiProbeSupportsGather(state, irradiance, moments))
            continue;

        float halfLambert = clamp(dot(safeNormal, -probeToSurface) * 0.5 + 0.5, 0.0, 1.0);
        // Directional weighting chooses representative probes; it is not missing
        // data and must not remove energy from an otherwise valid interpolation
        // cell.  Keep a tiny directional floor so an entirely back-facing cell can
        // still produce a stable estimate while visibility remains authoritative.
        float directionalWeight = max(halfLambert * halfLambert, 1.0e-4);
        float dataWeight = trilinear * clamp(state.activeWeight, 0.0, 1.0);
        float directionalTransportWeight = dataWeight * directionalWeight;
        float visibilityBias = clamp(0.03 * p.selfShadowBiasScale * volume.spacing, 0.002, volume.spacing * 0.10);
        float transportVisibility = SimpleDdgiChebyshev(
            moments.x,
            moments.y,
            max(distanceToProbe - visibilityBias, 0.0),
            volume.spacing);
        // A probe behind occluding geometry has no usable transport for this
        // receiver. Excluding it from both numerator and denominator lets
        // actually visible neighbors own the cell instead of stamping a dark
        // probe-aligned blob into the interpolation result.
        if (transportVisibility < 0.05)
            continue;
        float transportWeight = directionalTransportWeight * transportVisibility;
        accumulated += max(irradiance.rgb, vec3(0.0)) * transportWeight;
        validMass += dataWeight;
        directionalMass += directionalTransportWeight;
        visibleMass += transportWeight;
        result.validProbeCount++;
    }

    float spatialCoverage = clamp(result.spatialCoverage, 0.0, 1.0);
    result.spatialCoverage = spatialCoverage;
    result.validSupport = spatialCoverage > 0.000001
        ? clamp(validMass / spatialCoverage, 0.0, 1.0)
        : 0.0;
    result.directionalSupport = validMass > 0.000001
        ? clamp(directionalMass / validMass, 0.0, 1.0)
        : 0.0;
    result.transportVisibility = directionalMass > 0.000001
        ? clamp(visibleMass / directionalMass, 0.0, 1.0)
        : 0.0;
    result.ownership = clamp(validMass, 0.0, 1.0);
    // Normalize probe selection while retaining physical visibility in the
    // numerator.  Normalizing by geometric coverage incorrectly premultiplies the
    // result by back-face support and produces a probe-aligned dark lattice.
    result.irradiance = directionalMass > 0.000001
        ? clamp(accumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    return result;
}

vec3 SimpleDdgiRotateEnvironmentDirection(vec3 direction, float radians)
{
    float s = sin(radians);
    float c = cos(radians);
    return normalize(vec3(
        direction.x * c - direction.z * s,
        direction.y,
        direction.x * s + direction.z * c));
}

vec3 SimpleDdgiEnvironmentIrradianceFallback(vec3 safeNormal, SimpleDdgiParams p)
{
    GPUEnvironmentData environment = ReadEnvironmentData();
    if (environment.Enabled != 0u && environment.IrradianceTextureIndex >= 0)
    {
        vec3 irradianceDirection = SimpleDdgiRotateEnvironmentDirection(safeNormal, environment.RotationRadians);
        vec3 irradiance = texture(BindlessCubeTextures[nonuniformEXT(environment.IrradianceTextureIndex)], irradianceDirection).rgb;
        return max(irradiance, vec3(0.0)) * environment.DiffuseIntensity;
    }

    float skyWeight = clamp(safeNormal.y * 0.5 + 0.5, 0.0, 1.0);
    return max(p.environmentRadiance, vec3(0.0)) * p.environmentIntensity * skyWeight;
}

float EstimateFarFieldSkyVisibility(vec3 worldPos)
{
    FarFieldClipmapParams farField = ReadFarFieldClipmapParams(uint(FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX));
    if (!farField.enabled)
        return 1.0;

    const vec3 coneDirections[3] = vec3[](
        vec3(0.0, 1.0, 0.0),
        normalize(vec3(0.70710678, 0.70710678, 0.0)),
        normalize(vec3(-0.5, 0.70710678, 0.5))
    );

    uint coneCount = 3u;
    float maxDistance = FarFieldTraceMaximumDistance(farField);
    float visibility = 0.0;
    for (uint i = 0u; i < coneCount; i++)
    {
        float hitT;
        vec3 hitNormal;
        vec3 hitAlbedo;
        bool stepExhausted;
        uint visitedSteps;
        bool blocked = TraceFarFieldClipmapDetailed(
            worldPos,
            coneDirections[i],
            farField.voxelSize * 0.5,
            maxDistance,
            hitT,
            hitNormal,
            hitAlbedo,
            stepExhausted,
            visitedSteps);
        visibility += blocked ? 0.0 : 1.0;
    }

    float normalizedVisibility = clamp(visibility / float(coneCount), 0.0, 1.0);
    SimpleDdgiParams simpleParams = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    uint diagnosticFrame = simpleParams.frameIndex % uint(FRAMES_IN_FLIGHT);
    AddSimpleDdgiDiagnostic(simpleParams, diagnosticFrame, DDGI_INVESTIGATION_SKY_VISIBILITY_SAMPLE_COUNTER, 1u);
    AddSimpleDdgiDiagnostic(
        simpleParams,
        diagnosticFrame,
        DDGI_INVESTIGATION_SKY_VISIBILITY_ACCUM_COUNTER,
        uint(clamp(normalizedVisibility, 0.0, 16.0) * 1024.0 + 0.5));
    return normalizedVisibility;
}

SimpleDdgiGatherResult BlendSimpleDdgiGatherResults(
    SimpleDdgiGatherResult outer,
    SimpleDdgiGatherResult inner,
    float innerWeight)
{
    float w = clamp(innerWeight, 0.0, 1.0);
    float innerValidMass = inner.ownership * w;
    // Inner data has priority, but a geometrically selected ring must not hide a
    // valid coarser ring when its own probes are fresh, inactive, or otherwise
    // unsupported. The outer ring fills only the ownership still unrepresented.
    float outerWeight = 1.0 - innerValidMass;
    float outerValidMass = outer.ownership * outerWeight;
    float validMass = outerValidMass + innerValidMass;
    float outerDirectionalMass = outerValidMass * outer.directionalSupport;
    float innerDirectionalMass = innerValidMass * inner.directionalSupport;
    float directionalMass = outerDirectionalMass + innerDirectionalMass;
    float visibleMass = outerDirectionalMass * outer.transportVisibility +
        innerDirectionalMass * inner.transportVisibility;
    vec3 accumulated = outer.irradiance * outerDirectionalMass +
        inner.irradiance * innerDirectionalMass;
    SimpleDdgiGatherResult result;
    float innerSpatialMass = inner.spatialCoverage * w;
    result.spatialCoverage = clamp(
        innerSpatialMass + outer.spatialCoverage * (1.0 - innerSpatialMass),
        0.0,
        1.0);
    result.validSupport = result.spatialCoverage > 0.000001
        ? clamp(validMass / result.spatialCoverage, 0.0, 1.0)
        : 0.0;
    result.directionalSupport = validMass > 0.000001
        ? clamp(directionalMass / validMass, 0.0, 1.0)
        : 0.0;
    result.transportVisibility = directionalMass > 0.000001
        ? clamp(visibleMass / directionalMass, 0.0, 1.0)
        : 0.0;
    result.ownership = clamp(validMass, 0.0, 1.0);
    result.irradiance = directionalMass > 0.000001
        ? clamp(accumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.selectedVolume = inner.selectedVolume;
    result.selectedSpacing = mix(outer.selectedSpacing, inner.selectedSpacing, w);
    result.validProbeCount = inner.validProbeCount + outer.validProbeCount;
    return result;
}

SimpleDdgiGatherResult SampleSimpleDdgiGather(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiGatherResult empty = EmptySimpleDdgiGatherResult();
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    if ((p.flags & (SIMPLE_DDGI_FLAG_ENABLED | SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) !=
            (SIMPLE_DDGI_FLAG_ENABLED | SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) ||
        p.probeCount == 0u || p.volumeCount == 0u)
        return empty;

    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    uint selectedVolumeIndex;
    SimpleDdgiVolume selectedVolume;
    float edgeWeight;
    if (!SelectSimpleDdgiVolume(p, worldPos, selectedVolumeIndex, selectedVolume, edgeWeight))
        return empty;
    vec3 biasedWorldPos = SimpleDdgiBiasedSamplePosition(
        worldPos,
        safeNormal,
        viewDir,
        p,
        selectedVolume.spacing);
    edgeWeight = SimpleDdgiEdgeWeight(selectedVolume, biasedWorldPos);

    SimpleDdgiGatherResult selected = SampleSimpleDdgiVolumeGather(
        p,
        selectedVolume,
        selectedVolumeIndex,
        biasedWorldPos,
        safeNormal);
    if (edgeWeight >= 0.999 && selected.ownership >= 0.999)
        return selected;

    bool foundOuterVolume = false;
    for (uint nextVolumeIndex = selectedVolumeIndex + 1u; nextVolumeIndex < p.volumeCount; nextVolumeIndex++)
    {
        SimpleDdgiVolume nextVolume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), nextVolumeIndex);
        if (!SimpleDdgiContains(nextVolume, biasedWorldPos))
            continue;

        SimpleDdgiGatherResult outer = SampleSimpleDdgiVolumeGather(
            p,
            nextVolume,
            nextVolumeIndex,
            biasedWorldPos,
            safeNormal);
        selected = BlendSimpleDdgiGatherResults(
            outer,
            selected,
            foundOuterVolume ? 1.0 : edgeWeight);
        foundOuterVolume = true;
        if (selected.ownership >= 0.999)
            break;
    }

    if (foundOuterVolume)
        return selected;

    // At the outer edge only ownership fades.  Irradiance stays normalized so the
    // caller can compose the represented DDGI share and the missing environment
    // share exactly once.
    selected.spatialCoverage *= edgeWeight;
    selected.ownership *= edgeWeight;
    return selected;
}

vec3 SampleSimpleDdgiUnifiedIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir, bool allowFallback)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    if ((p.flags & SIMPLE_DDGI_FLAG_ENABLED) == 0u)
        return vec3(0.0);

    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    SimpleDdgiGatherResult gather = SampleSimpleDdgiGather(worldPos, safeNormal, viewDir);
    float selectedSpacing = gather.selectedSpacing > 0.0 ? gather.selectedSpacing : p.spacing;
    vec3 biasedWorldPos = SimpleDdgiBiasedSamplePosition(worldPos, safeNormal, viewDir, p, selectedSpacing);
    float ownership = SimpleDdgiRadiometricOwnership(gather);
    vec3 irradiance = gather.irradiance * ownership;
    if (allowFallback)
    {
        vec3 fallback = SimpleDdgiEnvironmentIrradianceFallback(safeNormal, p);
        if ((p.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
            fallback *= EstimateFarFieldSkyVisibility(biasedWorldPos);
        irradiance += fallback * (1.0 - ownership) * p.environmentFallbackIntensity;
    }

    return clamp(irradiance * p.indirectIntensity, vec3(0.0), vec3(64.0));
}

SimpleDdgiDebugSample SampleSimpleDdgiDebug(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    uint selectedVolumeIndex;
    SimpleDdgiVolume volume;
    float edgeWeight;
    SelectSimpleDdgiVolume(p, worldPos, selectedVolumeIndex, volume, edgeWeight);
    vec3 biasedWorldPos = SimpleDdgiBiasedSamplePosition(worldPos, normal, viewDir, p, volume.spacing);
    vec3 grid = (biasedWorldPos - volume.origin) / volume.spacing;
    ivec3 nearest = ivec3(round(grid));
    nearest = clamp(nearest, ivec3(0), ivec3(volume.gridCount) - ivec3(1));
    uint probeIndex = SimpleDdgiProbeIndex(uvec3(nearest), volume);
    vec3 logicalProbePos = volume.origin + vec3(nearest) * volume.spacing;
    SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX), probeIndex);
    vec3 probePos = logicalProbePos + state.relocation;
    vec3 toSurface = biasedWorldPos - probePos;
    float distanceToProbe = length(toSurface);
    vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : normalize(normal);
    vec4 moments = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), probeIndex, probeToSurface, p.visibilityTexels, p);
    float mean = max(moments.x, 0.0);
    float variance = max(moments.y - mean * mean, 0.0);

    SimpleDdgiDebugSample result;
    result.probeIndex = probeIndex;
    result.volumeIndex = selectedVolumeIndex;
    result.logicalProbePosition = logicalProbePos;
    result.relocatedProbePosition = probePos;
    float visibilityBias = clamp(0.03 * p.selfShadowBiasScale * volume.spacing, 0.002, volume.spacing * 0.10);
    result.visibility = SimpleDdgiChebyshev(
        moments.x,
        moments.y,
        max(distanceToProbe - visibilityBias, 0.0),
        volume.spacing);
    result.visibilityMaxRayDistance = max(volume.spacing * float(max(max(volume.gridCount.x, volume.gridCount.y), volume.gridCount.z)), volume.spacing);
    result.visibilityConfidence = mean > 0.0001
        ? clamp(1.0 - sqrt(variance) / max(result.visibilityMaxRayDistance, 0.0001), 0.0, 1.0)
        : 0.0;
    result.visibilityMomentMean = mean;
    result.visibilityMomentVariance = variance;
    result.visibilityProbeDistance = distanceToProbe;
    return result;
}

vec3 SampleSimpleDdgiIrradiance(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    return SampleSimpleDdgiUnifiedIrradiance(worldPos, normal, viewDir, true);
}

#endif
