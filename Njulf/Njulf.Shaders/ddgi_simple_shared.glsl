#ifndef NJULF_DDGI_SIMPLE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_SHARED_GLSL

const float SIMPLE_DDGI_PI = 3.14159265359;
const uint SIMPLE_DDGI_FLAG_ENABLED = 1u << 0;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_ENABLED = 1u << 1;
const uint SIMPLE_DDGI_FLAG_FAR_FIELD_FORCE_ALL = 1u << 2;
const uint SIMPLE_DDGI_IRRADIANCE_TEXELS = 8u;
const uint SIMPLE_DDGI_VISIBILITY_TEXELS = 16u;
const uint SIMPLE_DDGI_RAY_RESULT_STRIDE_WORDS = 8u;

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
};

struct SimpleDdgiDebugSample
{
    uint probeIndex;
    vec3 logicalProbePosition;
    vec3 relocatedProbePosition;
    float visibility;
    float visibilityConfidence;
    float visibilityMomentMean;
    float visibilityMomentVariance;
    float visibilityProbeDistance;
    float visibilityMaxRayDistance;
};

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
    return p;
}

uint SimpleDdgiProbeIndex(uvec3 coord, SimpleDdgiParams p)
{
    return coord.x + coord.y * p.gridCount.x + coord.z * p.gridCount.x * p.gridCount.y;
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

SimpleDdgiDebugSample SampleSimpleDdgiDebug(vec3 worldPos, vec3 normal, vec3 viewDir)
{
    SimpleDdgiParams p = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    vec3 biasedWorldPos = SimpleDdgiBiasedSamplePosition(worldPos, normal, viewDir, p);
    vec3 grid = (biasedWorldPos - p.origin) / p.spacing;
    ivec3 nearest = ivec3(round(grid));
    nearest = clamp(nearest, ivec3(0), ivec3(p.gridCount) - ivec3(1));
    uint probeIndex = SimpleDdgiProbeIndex(uvec3(nearest), p);
    vec3 probePos = p.origin + vec3(nearest) * p.spacing;
    vec3 toSurface = biasedWorldPos - probePos;
    float distanceToProbe = length(toSurface);
    vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : normalize(normal);
    vec4 moments = SampleSimpleDdgiAtlasBilinear(uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), probeIndex, probeToSurface, p.visibilityTexels);
    float mean = max(moments.x, 0.0);
    float variance = max(moments.y - mean * mean, 0.0);

    SimpleDdgiDebugSample result;
    result.probeIndex = probeIndex;
    result.logicalProbePosition = probePos;
    result.relocatedProbePosition = probePos;
    result.visibility = SimpleDdgiChebyshev(moments.x, moments.y, max(distanceToProbe - 0.03 * p.selfShadowBiasScale, 0.0));
    result.visibilityMaxRayDistance = max(p.spacing * float(max(max(p.gridCount.x, p.gridCount.y), p.gridCount.z)), p.spacing);
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
    if ((p.flags & SIMPLE_DDGI_FLAG_ENABLED) == 0u || p.probeCount == 0u)
        return vec3(0.0);

    vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
    vec3 biasedWorldPos = SimpleDdgiBiasedSamplePosition(worldPos, safeNormal, viewDir, p);
    vec3 grid = (biasedWorldPos - p.origin) / p.spacing;
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
        if (any(lessThan(c, ivec3(0))) || any(greaterThanEqual(c, ivec3(p.gridCount))))
            continue;

        vec3 probePos = p.origin + vec3(c) * p.spacing;
        vec3 toSurface = biasedWorldPos - probePos;
        float distanceToProbe = length(toSurface);
        vec3 probeToSurface = distanceToProbe > 0.00001 ? toSurface / distanceToProbe : safeNormal;
        float halfLambert = clamp(dot(safeNormal, -probeToSurface) * 0.5 + 0.5, 0.0, 1.0);
        float backfaceWeight = halfLambert * halfLambert;
        uint probeIndex = SimpleDdgiProbeIndex(uvec3(c), p);
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
        ? clamp(accumulated / totalWeight, vec3(0.0), vec3(64.0)) * p.indirectIntensity
        : vec3(0.0);
}

#endif
