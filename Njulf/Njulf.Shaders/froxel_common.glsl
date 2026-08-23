#ifndef NJULF_FROXEL_COMMON_GLSL
#define NJULF_FROXEL_COMMON_GLSL

#include "common.glsl"

const uint FROXEL_SOURCE_LOCAL_VOLUME = 0u;
const uint FROXEL_SOURCE_CPU_PARTICLE = 1u;
const uint FROXEL_SOURCE_GPU_PARTICLE = 2u;
const uint FROXEL_SOURCE_TYPE_SHIFT = 30u;
const uint FROXEL_SOURCE_INDEX_MASK = 0x3fffffffu;
const float FROXEL_PI = 3.14159265358979323846;

const uint FROXEL_FLAG_HISTORY_VALID = 1u << 0u;
const uint FROXEL_FLAG_CAMERA_CUT = 1u << 1u;
const uint FROXEL_FLAG_DDGI_L2 = 1u << 2u;
const uint FROXEL_FLAG_MULTIPLE_SCATTERING = 1u << 3u;

struct GPUVolumetricFogFrameData
{
    mat4 ViewProjectionMatrix;
    mat4 InverseViewProjectionMatrix;
    mat4 PreviousViewProjectionMatrix;
    vec4 CameraPositionAndTime;
    vec4 PreviousCameraPositionAndDeltaTime;
    vec4 ScreenDimensions;
    vec4 GridDimensions;
    vec4 SourceClusterDimensions;
    vec4 LightingGridDimensions;
    vec4 SourceClusterCellDimensions;
    vec4 LightingCellDimensions;
    vec4 DepthParameters;
    vec4 GlobalExtinction;
    vec4 GlobalScatteringAlbedoAndAnisotropy;
    vec4 WindAndNoiseScale;
    vec4 NoiseSelfShadowAndHistory;
    vec4 TemporalSampleAndReset;
    vec4 CountsAndDebug;
    vec4 MultipleScattering;
    vec4 LightCounts;
    vec4 FogColorAndOpacity;
    vec4 GridProjection;
    vec4 SunDirectionAndFlags;
};

struct GPUVolumetricDensityVolume
{
    vec4 PositionAndShape;
    vec4 Rotation;
    vec4 BoxExtentsAndRadius;
    vec4 ScatteringAlbedoAndDensity;
    vec4 ExtinctionEdgeAnisotropyPriority;
    vec4 NoiseParameters;
    vec4 FlowVelocityAndSeed;
    uint StableIdentityLow;
    uint StableIdentityHigh;
    uint Enabled;
    uint Padding0;
};

layout(set = 3, binding = 0, rgba16f) uniform image2D FroxelOutput;

layout(std430, set = 3, binding = 1) readonly buffer FroxelFrameBlock
{
    GPUVolumetricFogFrameData FroxelFrame;
};

layout(std430, set = 3, binding = 2) readonly buffer FroxelVolumeBlock
{
    GPUVolumetricDensityVolume FroxelVolumes[];
};

layout(std430, set = 3, binding = 3) buffer FroxelClusterCountBlock
{
    uint FroxelClusterCounts[];
};

layout(std430, set = 3, binding = 4) buffer FroxelClusterReferenceBlock
{
    uint FroxelClusterReferences[];
};

layout(set = 3, binding = 5, rgba16f) uniform image2DArray FroxelMedium;
layout(set = 3, binding = 6, rgba16f) uniform image2DArray FroxelMediumAuxiliary;
layout(set = 3, binding = 7, rgba16f) uniform image2DArray FroxelDirectRadiance;
layout(set = 3, binding = 8, rgba16f) uniform image2DArray FroxelIndirectRadiance;
layout(set = 3, binding = 9, rgba16f) uniform image2DArray FroxelHistoryRead;
layout(set = 3, binding = 10, rgba16f) uniform image2DArray FroxelHistoryWrite;
layout(set = 3, binding = 11, rgba16f) uniform image2DArray FroxelResolvedHalf;
layout(set = 3, binding = 12, rgba16f) uniform image2DArray FroxelLightingMedium;
layout(set = 3, binding = 13, rgba16f) uniform image2DArray FroxelCoarseTransmittance;
layout(set = 3, binding = 14, rgba8) uniform image3D FroxelNoiseStorage;
layout(set = 3, binding = 15) uniform sampler3D FroxelNoiseSampler;
layout(set = 3, binding = 16, rgba16f) uniform image2DArray FroxelMultipleScattering0;
layout(set = 3, binding = 17, rgba16f) uniform image2DArray FroxelMultipleScattering1;
layout(set = 3, binding = 18, r16f) uniform image2DArray FroxelHistoryConfidenceRead;
layout(set = 3, binding = 19, r16f) uniform image2DArray FroxelHistoryConfidenceWrite;

layout(std430, set = 3, binding = 20) buffer FroxelDiagnosticBlock
{
    uint SampleCount;
    uint MediumNonEmptyCount;
    uint MaximumExtinctionQ;
    uint ExtinctionSumQ;
    uint DirectNonZeroCount;
    uint MaximumDirectLuminanceQ;
    uint DirectLuminanceSumQ;
    uint IndirectNonZeroCount;
    uint MaximumIndirectLuminanceQ;
    uint IndirectLuminanceSumQ;
    uint DdgiSupportedCount;
    uint MaximumOpacityQ;
    uint TransmittanceSumQ;
    uint HistoryAcceptedCount;
    uint HistoryRejectedCount;
    uint HistoryRejectedInvalidCount;
    uint HistoryRejectedBoundsCount;
    uint HistoryRejectedExtinctionCount;
    uint HistoryRejectedRadianceCount;
    uint HistoryRejectedVelocityCount;
    uint NonFiniteCount;
    uint AdmittedSourceCount;
    uint ClusterOverflowCount;
    uint DiagnosticPadding0;
} FroxelDiagnostics;

layout(set = 3, binding = 21) uniform sampler2DArray FroxelDirectSampler;
layout(set = 3, binding = 22) uniform sampler2DArray FroxelIndirectSampler;
layout(set = 3, binding = 23) uniform sampler2DArray FroxelMultipleSampler0;
layout(set = 3, binding = 24) uniform sampler2DArray FroxelMultipleSampler1;
layout(set = 3, binding = 25) uniform sampler2DArray FroxelMediumSampler;
layout(set = 3, binding = 26) uniform sampler2DArray FroxelHistorySampler;
layout(set = 3, binding = 27) uniform sampler2DArray FroxelHistoryConfidenceSampler;

layout(push_constant) uniform FroxelPushConstantBlock
{
    uint FrameIndex;
    uint Stage;
    uint HistoryReadBank;
    uint HistoryWriteBank;
    uint MultipleScatteringIteration;
    uint Flags;
    uint Padding0;
    uint Padding1;
} FroxelPush;

uvec3 FroxelGridSize()
{
    return uvec3(max(round(FroxelFrame.GridDimensions.xyz), vec3(1.0)));
}

uvec3 FroxelClusterGridSize()
{
    return uvec3(max(round(
        FroxelFrame.SourceClusterDimensions.xyz), vec3(1.0)));
}

uvec3 FroxelLightingGridSize()
{
    return uvec3(max(round(
        FroxelFrame.LightingGridDimensions.xyz), vec3(1.0)));
}

uvec3 FroxelSourceClusterCellSize()
{
    return uvec3(max(round(
        FroxelFrame.SourceClusterCellDimensions.xyz), vec3(1.0)));
}

uvec3 FroxelLightingCellSize()
{
    return uvec3(max(round(
        FroxelFrame.LightingCellDimensions.xyz), vec3(1.0)));
}

uint FroxelClusterReferenceCapacity()
{
    // The host allocates and uploads the profile capacity together. Keeping
    // this limit runtime-valued avoids shader-driver unrolling of every source
    // consumer when high-density profiles raise the bounded list size.
    return uint(max(round(FroxelFrame.SourceClusterDimensions.w), 1.0));
}

uint FroxelFlattenCluster(uvec3 cluster)
{
    uvec3 dimensions = FroxelClusterGridSize();
    return cluster.x + cluster.y * dimensions.x +
        cluster.z * dimensions.x * dimensions.y;
}

uint FroxelClusterCount()
{
    uvec3 dimensions = FroxelClusterGridSize();
    return dimensions.x * dimensions.y * dimensions.z;
}

uint FroxelFlattenCoordinate(uvec3 coordinate)
{
    uvec3 dimensions = FroxelGridSize();
    return coordinate.x + coordinate.y * dimensions.x +
        coordinate.z * dimensions.x * dimensions.y;
}

bool FroxelIsDiagnosticSample(uvec3 coordinate)
{
    return (FroxelFlattenCoordinate(coordinate) & 63u) == 0u;
}

uint FroxelQuantizeDiagnostic(float value, float scale)
{
    return uint(round(clamp(value * scale, 0.0, 65535.0)));
}

uvec3 FroxelClusterForCoordinate(uvec3 coordinate)
{
    return min(
        coordinate / FroxelSourceClusterCellSize(),
        FroxelClusterGridSize() - uvec3(1u));
}

vec3 FroxelLightingCoordinate(vec3 coordinate)
{
    vec3 cell = vec3(FroxelLightingCellSize());
    return (coordinate + vec3(0.5)) / cell - vec3(0.5);
}

float FroxelSliceBoundary(float slice)
{
    float nearDistance = max(FroxelFrame.DepthParameters.x, 0.001);
    float farDistance = max(FroxelFrame.DepthParameters.y, nearDistance + 0.001);
    float depth = max(FroxelFrame.GridDimensions.z, 1.0);
    return nearDistance * pow(farDistance / nearDistance,
        clamp(slice / depth, 0.0, 1.0));
}

float FroxelSliceCenter(uint slice)
{
    return FroxelSliceBoundary(float(slice) + 0.5);
}

float FroxelContinuousSlice(float distanceFromCamera)
{
    float nearDistance = max(FroxelFrame.DepthParameters.x, 0.001);
    float farDistance = max(FroxelFrame.DepthParameters.y, nearDistance + 0.001);
    float normalized = log(clamp(distanceFromCamera, nearDistance, farDistance) /
        nearDistance) / log(farDistance / nearDistance);
    return normalized * FroxelFrame.GridDimensions.z;
}

vec2 FroxelCoordinateUv(vec2 coordinate)
{
    float pixelSize = max(FroxelFrame.GridDimensions.w, 1.0);
    float guard = max(FroxelFrame.DepthParameters.w, 0.0);
    vec2 pixel = (coordinate - vec2(guard) + vec2(0.5)) *
        pixelSize;
    return pixel * FroxelFrame.ScreenDimensions.zw;
}

uint FroxelSampleHash(uvec3 coordinate, uint channel)
{
    uint value = coordinate.x * 0x9e3779b9u ^
        coordinate.y * 0x85ebca6bu ^
        coordinate.z * 0xc2b2ae35u ^
        channel * 0x27d4eb2du;
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

float FroxelSampleUnit(uvec3 coordinate, uint channel)
{
    return float(FroxelSampleHash(coordinate, channel) & 0x00ffffffu) /
        16777216.0;
}

// The storage lattice is stable. Only the medium evaluation point moves, and
// every froxel receives a different low-discrepancy offset so the whole volume
// cannot wobble coherently from one temporal phase to the next.
vec3 FroxelTemporalSampleOffset(uvec3 coordinate)
{
    vec3 base = vec3(
        FroxelSampleUnit(coordinate, 0u),
        FroxelSampleUnit(coordinate, 1u),
        FroxelSampleUnit(coordinate, 2u));
    vec3 rotation = fract(FroxelFrame.TemporalSampleAndReset.xyz);
    vec3 offset = fract(base + rotation) - vec3(0.5);
    return offset * vec3(0.7, 0.7, 0.9);
}

vec3 FroxelWorldPositionAtCoordinate(vec3 coordinate)
{
    vec2 uv = FroxelCoordinateUv(coordinate.xy);
    vec2 ndc = uv * 2.0 - vec2(1.0);
    vec4 farWorld = MulRowMajor(
        vec4(ndc, 0.0, 1.0),
        FroxelFrame.InverseViewProjectionMatrix);
    farWorld.xyz /= max(abs(farWorld.w), 1.0e-6);
    vec3 camera = FroxelFrame.CameraPositionAndTime.xyz;
    vec3 ray = normalize(farWorld.xyz - camera);
    float distance = FroxelSliceBoundary(clamp(
        coordinate.z + 0.5,
        0.0,
        FroxelFrame.GridDimensions.z));
    return camera + ray * distance;
}

vec3 FroxelWorldPosition(uvec3 coordinate)
{
    return FroxelWorldPositionAtCoordinate(vec3(coordinate));
}

vec3 FroxelSampleWorldPosition(uvec3 coordinate)
{
    return FroxelWorldPositionAtCoordinate(
        vec3(coordinate) + FroxelTemporalSampleOffset(coordinate));
}

bool FroxelWorldToCoordinate(vec3 worldPosition, out vec3 coordinate)
{
    vec4 clip = MulRowMajor(
        vec4(worldPosition, 1.0),
        FroxelFrame.ViewProjectionMatrix);
    if (!(clip.w > 1.0e-6))
    {
        coordinate = vec3(0.0);
        return false;
    }
    vec2 uv = clip.xy / clip.w * 0.5 + vec2(0.5);
    float pixelSize = max(FroxelFrame.GridDimensions.w, 1.0);
    float guard = max(FroxelFrame.DepthParameters.w, 0.0);
    coordinate.xy = uv * FroxelFrame.ScreenDimensions.xy / pixelSize +
        vec2(guard) - vec2(0.5);
    coordinate.z = FroxelContinuousSlice(length(
        worldPosition - FroxelFrame.CameraPositionAndTime.xyz)) - 0.5;
    vec3 dimensions = FroxelFrame.GridDimensions.xyz;
    return all(greaterThanEqual(coordinate, vec3(-0.5))) &&
        all(lessThanEqual(coordinate, dimensions - vec3(0.5)));
}

float FroxelHenyeyGreenstein(float cosTheta, float anisotropy)
{
    float g = clamp(anisotropy, -0.9, 0.9);
    float denominator = max(1.0 + g * g - 2.0 * g *
        clamp(cosTheta, -1.0, 1.0), 1.0e-6);
    return (1.0 - g * g) /
        (4.0 * FROXEL_PI * denominator * sqrt(denominator));
}

vec3 FroxelQuaternionRotateInverse(vec4 quaternion, vec3 value)
{
    vec4 q = dot(quaternion, quaternion) > 1.0e-8
        ? quaternion * inversesqrt(dot(quaternion, quaternion))
        : vec4(0.0, 0.0, 0.0, 1.0);
    vec3 u = -q.xyz;
    return value + 2.0 * cross(u, cross(u, value) + q.w * value);
}

float FroxelBoxSignedDistance(vec3 point, vec3 extents)
{
    vec3 q = abs(point) - max(extents, vec3(0.001));
    return length(max(q, vec3(0.0))) +
        min(max(q.x, max(q.y, q.z)), 0.0);
}

float FroxelVolumeWeight(GPUVolumetricDensityVolume volume, vec3 worldPosition)
{
    vec3 local = FroxelQuaternionRotateInverse(
        volume.Rotation,
        worldPosition - volume.PositionAndShape.xyz);
    float signedDistance = volume.PositionAndShape.w > 0.5
        ? length(local) - max(volume.BoxExtentsAndRadius.w, 0.001)
        : FroxelBoxSignedDistance(local, volume.BoxExtentsAndRadius.xyz);
    float edgeFade = max(volume.ExtinctionEdgeAnisotropyPriority.y, 0.001);
    return signedDistance <= 0.0
        ? clamp(-signedDistance / edgeFade, 0.0, 1.0)
        : 0.0;
}

float FroxelDensityNoise(
    vec3 worldPosition,
    vec3 flow,
    float scale,
    float strength,
    float contrast,
    float seed)
{
    vec3 seedOffset = fract(vec3(
        seed * 0.1031,
        seed * 0.11369,
        seed * 0.13787));
    vec3 uvw = fract((worldPosition - flow *
        FroxelFrame.CameraPositionAndTime.w) * max(scale, 0.0001) +
        seedOffset);
    float noiseValue = texture(FroxelNoiseSampler, uvw).r;
    noiseValue = pow(clamp(noiseValue, 0.0, 1.0), max(contrast, 0.01));
    return mix(1.0, noiseValue * 2.0, clamp(strength, 0.0, 1.0));
}

GPUParticleInstance FroxelReadParticle(uint sourceType, uint sourceIndex)
{
    if (sourceType == FROXEL_SOURCE_CPU_PARTICLE)
    {
        return ReadParticleInstance(
            uint(PARTICLE_INSTANCE_BUFFER_BASE_INDEX),
            min(FroxelPush.FrameIndex, uint(FRAMES_IN_FLIGHT - 1)),
            sourceIndex);
    }
    return ReadParticleInstance(
        uint(GPU_PARTICLE_RENDER_INSTANCE_BUFFER_BASE_INDEX),
        min(FroxelPush.FrameIndex, uint(FRAMES_IN_FLIGHT - 1)),
        sourceIndex);
}

uint FroxelGpuParticleRenderedCount()
{
    uint bufferIndex = uint(GPU_PARTICLE_COUNTER_BUFFER_BASE_INDEX) +
        min(FroxelPush.FrameIndex, uint(FRAMES_IN_FLIGHT - 1));
    return ReadStorageWord(bufferIndex, 5u);
}

float FroxelMediumTransmittance(vec3 origin, vec3 direction, float distance)
{
    float limit = min(max(distance, 0.0),
        max(FroxelFrame.NoiseSelfShadowAndHistory.z, 0.0));
    if (!(limit > 0.001))
        return 1.0;
    const uint stepCount = 6u;
    float stepLength = limit / float(stepCount);
    float opticalDepth = 0.0;
    for (uint stepIndex = 0u; stepIndex < stepCount; stepIndex++)
    {
        vec3 samplePosition = origin + direction *
            ((float(stepIndex) + 0.5) * stepLength);
        vec3 coordinate;
        if (!FroxelWorldToCoordinate(samplePosition, coordinate))
            continue;
        ivec3 texel = ivec3(clamp(
            floor(coordinate + vec3(0.5)),
            vec3(0.0),
            FroxelFrame.GridDimensions.xyz - vec3(1.0)));
        opticalDepth += max(imageLoad(FroxelMedium, texel).a, 0.0) *
            stepLength;
    }
    return exp(-min(opticalDepth, 80.0));
}

void FroxelComputeBarrier()
{
    memoryBarrierImage();
    memoryBarrierBuffer();
    barrier();
}

#endif
