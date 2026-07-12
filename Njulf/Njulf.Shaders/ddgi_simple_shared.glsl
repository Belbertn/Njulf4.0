#ifndef NJULF_DDGI_SIMPLE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_SHARED_GLSL

const float SIMPLE_DDGI_PI = 3.14159265359;
const uint SIMPLE_DDGI_FLAG_ENABLED = 1u << 0;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_ENABLED = 1u << 1;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_FORCE_ALL = 1u << 2;
const uint SIMPLE_DDGI_IRRADIANCE_TEXELS = 8u;
const uint SIMPLE_DDGI_VISIBILITY_TEXELS = 16u;
const uint SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_HEADER_WORDS = 40u;
const uint SIMPLE_DDGI_VOLUME_STRIDE_WORDS = 24u;
const uint SIMPLE_DDGI_MAX_VOLUME_COUNT = 16u;
const uint SIMPLE_DDGI_VOLUME_KIND_LEGACY = 0u;
const uint SIMPLE_DDGI_VOLUME_KIND_AUTHORED = 1u;
const uint SIMPLE_DDGI_VOLUME_KIND_RING = 2u;
const uint SIMPLE_DDGI_PROBE_STATE_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS = 8u;
const uint SIMPLE_DDGI_RELOCATION_CLASSIFICATION_STRIDE_WORDS = 12u;
const uint SIMPLE_DDGI_PROBE_FLAG_FRESH = 1u << 0;
const uint SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED = 1u << 1;
const uint SIMPLE_DDGI_PROBE_FLAG_INACTIVE = 1u << 2;
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
    uint updateStartProbe;
    uint probesToUpdate;
    float selfShadowBiasScale;
    float indirectIntensity;
    uint debugView;
    uint farFieldMaxTraceSteps;
    vec4 rayRotation;
    float normalBias;
    float viewBias;
    uint volumeCount;
};

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
};

struct SimpleDdgiProbeUpdate
{
    uint probeIndex;
    uint volumeIndex;
    uint flags;
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
    return state;
}

void WriteSimpleDdgiProbeState(uint bufferIndex, uint probeIndex, SimpleDdgiProbeState state)
{
    uint baseWord = SimpleDdgiProbeStateBase(probeIndex);
    WriteStorageVec4(bufferIndex, baseWord, vec4(state.relocation, state.activeWeight));
    WriteStorageWord(bufferIndex, baseWord + 4u, state.flags);
    WriteStorageWord(bufferIndex, baseWord + 5u, state.age);
    WriteStorageWord(bufferIndex, baseWord + 6u, state.classification);
    WriteStorageWord(bufferIndex, baseWord + 7u, 0u);
}

SimpleDdgiProbeUpdate ReadSimpleDdgiProbeUpdate(uint bufferIndex, uint queueOffset)
{
    uint baseWord = queueOffset * SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS;
    SimpleDdgiProbeUpdate update;
    update.probeIndex = ReadStorageWord(bufferIndex, baseWord);
    update.volumeIndex = ReadStorageWord(bufferIndex, baseWord + 1u);
    update.flags = ReadStorageWord(bufferIndex, baseWord + 2u);
    return update;
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
    p.updateStartProbe = uint(max(updateRange.x, 0.0));
    p.probesToUpdate = uint(max(updateRange.y, 0.0));
    p.debugView = uint(max(debugAndBias.x, 0.0));
    p.selfShadowBiasScale = max(debugAndBias.y, 0.0);
    p.indirectIntensity = max(debugAndBias.z, 0.0);
    p.farFieldMaxTraceSteps = max(uint(debugAndBias.w), 1u);
    p.rayRotation = dot(rotation, rotation) > 0.000001 ? normalize(rotation) : vec4(0.0, 0.0, 0.0, 1.0);
    p.normalBias = max(bias.x, 0.0);
    p.viewBias = max(bias.y, 0.0);
    p.volumeCount = min(uint(max(max(reserved.x, updateRange.z), 0.0)), SIMPLE_DDGI_MAX_VOLUME_COUNT);
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
    return volume;
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
    return volume.firstProbeIndex + coord.x + coord.y * volume.gridCount.x + coord.z * volume.gridCount.x * volume.gridCount.y;
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
    return uvec3(x, y, z);
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

vec4 SampleSimpleDdgiAtlasBilinear(uint bufferIndex, uint probeIndex, vec3 direction, uint texelsPerProbe)
{
    vec2 texelUv = SimpleDdgiOctEncode(direction) * float(texelsPerProbe) - vec2(0.5);
    ivec2 base = ivec2(floor(texelUv));
    vec2 f = fract(texelUv);
    vec4 s00 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base, texelsPerProbe), texelsPerProbe);
    vec4 s10 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 0), texelsPerProbe), texelsPerProbe);
    vec4 s01 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(0, 1), texelsPerProbe), texelsPerProbe);
    vec4 s11 = ReadSimpleDdgiAtlasTexel(bufferIndex, probeIndex, SimpleDdgiMirrorOctTexelIndex(base + ivec2(1, 1), texelsPerProbe), texelsPerProbe);
    return mix(mix(s00, s10, f.x), mix(s01, s11, f.x), f.y);
}

float SimpleDdgiChebyshev(float mean, float mean2, float receiverDistance)
{
    if (receiverDistance <= mean)
        return 1.0;
    float variance = max(mean2 - mean * mean, 0.0025);
    float d = receiverDistance - mean;
    return clamp(variance / (variance + d * d), 0.0, 1.0);
}

vec3 SimpleDdgiBiasedSamplePosition(vec3 worldPos, vec3 normal, vec3 viewDir, SimpleDdgiParams p)
{
    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    vec3 safeView = length(viewDir) > 0.00001 ? normalize(viewDir) : safeNormal;
    return worldPos + safeNormal * p.normalBias + safeView * p.viewBias;
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

vec3 SampleSimpleDdgiVolumeIrradiance(SimpleDdgiParams p, SimpleDdgiVolume volume, vec3 biasedWorldPos, vec3 safeNormal)
{
    vec3 grid = (biasedWorldPos - volume.origin) / volume.spacing;
    vec3 baseF = floor(grid);
    vec3 fracV = clamp(grid - baseF, vec3(0.0), vec3(1.0));
    ivec3 base = ivec3(baseF);
    vec3 accumulated = vec3(0.0);
    float totalWeight = 0.0;

    for (uint z = 0u; z < 2u; z++)
    for (uint y = 0u; y < 2u; y++)
    for (uint x = 0u; x < 2u; x++)
    {
        ivec3 c = base + ivec3(int(x), int(y), int(z));
        if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(volume.gridCount))))
            continue;

        uint probeIndex = SimpleDdgiProbeIndex(uvec3(c), volume);
        SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX), probeIndex);
        if (state.classification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE || state.activeWeight <= 0.001)
            continue;

        vec3 probePos = volume.origin + vec3(c) * volume.spacing + state.relocation;
        vec3 toSurface = biasedWorldPos - probePos;
        float distanceToProbe = length(toSurface);
        vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : safeNormal;
        float halfLambert = clamp(dot(safeNormal, -probeToSurface) * 0.5 + 0.5, 0.0, 1.0);
        float backfaceWeight = halfLambert * halfLambert;
        vec4 irradiance = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX), probeIndex, safeNormal, p.irradianceTexels);
        vec4 moments = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), probeIndex, probeToSurface, p.visibilityTexels);
        float visibility = SimpleDdgiChebyshev(moments.x, moments.y, max(distanceToProbe - 0.03 * p.selfShadowBiasScale, 0.0));
        vec3 w3 = mix(1.0 - fracV, fracV, vec3(x, y, z));
        float trilinear = w3.x * w3.y * w3.z;
        float weight = max(trilinear * backfaceWeight * visibility, trilinear * 1.0e-5);
        accumulated += irradiance.rgb * weight;
        totalWeight += weight;
    }

    return totalWeight > 0.000001
        ? clamp(accumulated / totalWeight, vec3(0.0), vec3(64.0))
        : vec3(0.0);
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

SimpleDdgiDebugSample SampleSimpleDdgiDebug(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    vec3 biasedWorldPos = SimpleDdgiBiasedSamplePosition(worldPos, normal, viewDir, p);
    uint selectedVolumeIndex;
    SimpleDdgiVolume volume;
    float edgeWeight;
    SelectSimpleDdgiVolume(p, biasedWorldPos, selectedVolumeIndex, volume, edgeWeight);
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
    vec4 moments = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), probeIndex, probeToSurface, p.visibilityTexels);
    float mean = max(moments.x, 0.0);
    float variance = max(moments.y - mean * mean, 0.0);

    SimpleDdgiDebugSample result;
    result.probeIndex = probeIndex;
    result.volumeIndex = selectedVolumeIndex;
    result.logicalProbePosition = logicalProbePos;
    result.relocatedProbePosition = probePos;
    result.visibility = SimpleDdgiChebyshev(moments.x, moments.y, max(distanceToProbe - 0.03 * p.selfShadowBiasScale, 0.0));
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
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    if ((p.flags & SIMPLE_DDGI_FLAG_ENABLED) == 0u || p.probeCount == 0u || p.volumeCount == 0u)
        return vec3(0.0);

    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    vec3 biasedWorldPos = SimpleDdgiBiasedSamplePosition(worldPos, safeNormal, viewDir, p);
    uint selectedVolumeIndex;
    SimpleDdgiVolume selectedVolume;
    float edgeWeight;
    if (!SelectSimpleDdgiVolume(p, biasedWorldPos, selectedVolumeIndex, selectedVolume, edgeWeight))
        return SimpleDdgiEnvironmentIrradianceFallback(safeNormal, p) * p.indirectIntensity;

    vec3 selectedIrradiance = SampleSimpleDdgiVolumeIrradiance(p, selectedVolume, biasedWorldPos, safeNormal);
    if (edgeWeight >= 0.999)
        return selectedIrradiance * p.indirectIntensity;

    for (uint nextVolumeIndex = selectedVolumeIndex + 1u; nextVolumeIndex < p.volumeCount; nextVolumeIndex++)
    {
        SimpleDdgiVolume nextVolume = ReadSimpleDdgiVolume(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX), nextVolumeIndex);
        if (!SimpleDdgiContains(nextVolume, biasedWorldPos))
            continue;

        vec3 nextIrradiance = SampleSimpleDdgiVolumeIrradiance(p, nextVolume, biasedWorldPos, safeNormal);
        return mix(nextIrradiance, selectedIrradiance, edgeWeight) * p.indirectIntensity;
    }

    vec3 fallbackIrradiance = SimpleDdgiEnvironmentIrradianceFallback(safeNormal, p);
    return mix(fallbackIrradiance, selectedIrradiance, edgeWeight) * p.indirectIntensity;
}

#endif
