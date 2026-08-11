#ifndef NJULF_GI_CAUSTIC_SCREEN_GLSL
#define NJULF_GI_CAUSTIC_SCREEN_GLSL

#include "gi_caustic_shared.glsl"
#include "c4_receiver_payload.glsl"
#include "gi_material_transport.glsl"

const uint GI_CAUSTIC_SCREEN_ABI_VERSION = 0xC4020001u;
const uint GI_CAUSTIC_SCREEN_TILE_SIZE = 8u;
const uint GI_CAUSTIC_SCREEN_ACTIVE_TILE_COUNT_WORD = 0u;
const uint GI_CAUSTIC_SCREEN_REJECTED_TILE_COUNT_WORD = 1u;
const uint GI_CAUSTIC_SCREEN_INDIRECT_WORD_OFFSET = 4u;
const uint GI_CAUSTIC_SCREEN_TILE_LIST_WORD_OFFSET = 16u;
const uint GI_CAUSTIC_SCREEN_REVERSED_Z = 1u << 0u;
const uint GI_CAUSTIC_SCREEN_PAYLOAD_VALIDATED = 1u << 1u;
const uint GI_CAUSTIC_SCREEN_COMPOSITE_ENABLED = 1u << 2u;

// Sets 0 and 1 are the immutable bindless storage/texture heaps declared by
// common.glsl. Screen resources live in a private set so their ABI cannot
// alias or reinterpret either persistent heap.
layout(set = 2, binding = 0) uniform sampler2D giCausticSceneDepth;
layout(set = 2, binding = 1) uniform usampler2D giCausticReceiverPayload;
layout(rgba16f, set = 2, binding = 2) uniform image2D giCausticRadiance;
layout(rgba16f, set = 2, binding = 3) uniform image2D giCausticMoments;
layout(rgba16f, set = 2, binding = 4) uniform image2D giCausticSceneColor;

struct GiCausticScreenFrameConstants
{
    mat4 viewProjection;
    mat4 inverseViewProjection;
    vec4 fullExtentAndInverse;
    vec4 cameraPositionAndFlags;
    uvec4 screenParameters;
    vec4 resolveParameters;
};

layout(std430, set = 2, binding = 5) readonly buffer
    GiCausticScreenFrameConstantsBuffer
{
    GiCausticScreenFrameConstants frame;
} giCausticScreenFrame;

uvec2 GiCausticScreenExtent()
{
    return uvec2(giCausticScreenFrame.frame.fullExtentAndInverse.xy +
        vec2(0.5));
}

uvec2 GiCausticScreenTileCount()
{
    return giCausticScreenFrame.frame.screenParameters.xy;
}

uint GiCausticScreenTileCapacity()
{
    return giCausticScreenFrame.frame.screenParameters.z;
}

uint GiCausticScreenFlags()
{
    return giCausticScreenFrame.frame.screenParameters.w;
}

float GiCausticMinimumReceiverNormalCosine()
{
    return giCausticScreenFrame.frame.resolveParameters.x;
}

float GiCausticMaximumSearchDistance()
{
    return giCausticScreenFrame.frame.resolveParameters.y;
}

bool GiCausticScreenFinite(vec4 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

bool GiCausticScreenContractValid()
{
    uvec2 extent = GiCausticScreenExtent();
    uvec2 tileCount = GiCausticScreenTileCount();
    uint tileCapacity = GiCausticScreenTileCapacity();
    uint requiredTileX = (extent.x + GI_CAUSTIC_SCREEN_TILE_SIZE - 1u) /
        GI_CAUSTIC_SCREEN_TILE_SIZE;
    uint requiredTileY = (extent.y + GI_CAUSTIC_SCREEN_TILE_SIZE - 1u) /
        GI_CAUSTIC_SCREEN_TILE_SIZE;
    uint requiredScratchWords = GI_CAUSTIC_SCREEN_TILE_LIST_WORD_OFFSET +
        tileCapacity;
    uint requiredFlags = GI_CAUSTIC_SCREEN_REVERSED_Z |
        GI_CAUSTIC_SCREEN_PAYLOAD_VALIDATED;

    return giCausticPc.AbiVersion == GI_CAUSTIC_SCREEN_ABI_VERSION &&
        giCausticPc.TaskBufferIndex == 204u &&
        giCausticPc.PhotonBufferIndex == 205u &&
        giCausticPc.CacheBufferIndex == 206u &&
        giCausticPc.ScratchBufferIndex == 207u &&
        giCausticPc.PhotonCapacity > 0u &&
        giCausticPc.PhotonRecordStrideWords == GI_CAUSTIC_PHOTON_WORDS &&
        giCausticPc.CellTableCapacity > 0u &&
        (giCausticPc.CellTableCapacity &
            (giCausticPc.CellTableCapacity - 1u)) == 0u &&
        giCausticPc.MaximumPhotonsPerCell > 0u &&
        giCausticPc.CacheGeneration != 0u &&
        giCausticPc.PhotonReadBankIndex < GI_CAUSTIC_REQUIRED_BANK_COUNT &&
        giCausticPc.CacheReadBankIndex < GI_CAUSTIC_REQUIRED_BANK_COUNT &&
        GiCausticFinite(giCausticPc.CellOriginAndSize) &&
        giCausticPc.CellOriginAndSize.w > 0.0 &&
        extent.x > 0u && extent.y > 0u &&
        all(equal(tileCount, uvec2(requiredTileX, requiredTileY))) &&
        tileCapacity == tileCount.x * tileCount.y &&
        requiredScratchWords >= GI_CAUSTIC_SCREEN_TILE_LIST_WORD_OFFSET &&
        requiredScratchWords <= giCausticPc.ScratchWordCapacity &&
        (GiCausticScreenFlags() & requiredFlags) == requiredFlags &&
        GiCausticScreenFinite(
            giCausticScreenFrame.frame.fullExtentAndInverse) &&
        GiCausticScreenFinite(
            giCausticScreenFrame.frame.cameraPositionAndFlags) &&
        giCausticScreenFrame.frame.fullExtentAndInverse.z > 0.0 &&
        giCausticScreenFrame.frame.fullExtentAndInverse.w > 0.0 &&
        GiCausticFinite(GiCausticMinimumReceiverNormalCosine()) &&
        GiCausticMinimumReceiverNormalCosine() >= 0.0 &&
        GiCausticMinimumReceiverNormalCosine() <= 1.0 &&
        GiCausticFinite(GiCausticMaximumSearchDistance()) &&
        GiCausticMaximumSearchDistance() > 0.0;
}

vec4 GiCausticMulRowMajor(vec4 value, mat4 matrix)
{
    return vec4(
        dot(value, vec4(matrix[0][0], matrix[1][0], matrix[2][0], matrix[3][0])),
        dot(value, vec4(matrix[0][1], matrix[1][1], matrix[2][1], matrix[3][1])),
        dot(value, vec4(matrix[0][2], matrix[1][2], matrix[2][2], matrix[3][2])),
        dot(value, vec4(matrix[0][3], matrix[1][3], matrix[2][3], matrix[3][3])));
}

bool GiCausticReconstructWorldPosition(
    ivec2 pixel,
    float depth,
    out vec3 worldPosition)
{
    worldPosition = vec3(0.0);
    uvec2 extent = GiCausticScreenExtent();
    vec2 uv = (vec2(pixel) + vec2(0.5)) /
        max(vec2(extent), vec2(1.0));
    vec4 clip = vec4(uv * 2.0 - vec2(1.0), depth, 1.0);
    vec4 world = GiCausticMulRowMajor(
        clip,
        giCausticScreenFrame.frame.inverseViewProjection);
    if (!GiCausticScreenFinite(world) || abs(world.w) <= 1.0e-7)
        return false;
    worldPosition = world.xyz / world.w;
    return GiCausticFinite(worldPosition);
}

bool GiCausticLoadReceiver(
    ivec2 pixel,
    out vec3 worldPosition,
    out vec3 geometricNormal,
    out vec3 shadingNormal,
    out vec3 directionalDiffuseBase,
    out vec3 dielectricF0)
{
    worldPosition = vec3(0.0);
    geometricNormal = vec3(0.0);
    shadingNormal = vec3(0.0);
    directionalDiffuseBase = vec3(0.0);
    dielectricF0 = vec3(0.0);
    uvec2 extent = GiCausticScreenExtent();
    if (any(lessThan(pixel, ivec2(0))) ||
        any(greaterThanEqual(pixel, ivec2(extent))))
    {
        return false;
    }

    uvec4 payload = texelFetch(giCausticReceiverPayload, pixel, 0);
    float depth = texelFetch(giCausticSceneDepth, pixel, 0).r;
    if (!NjulfC4ReceiverPayloadValid(payload) ||
        !GiCausticFinite(depth) || depth <= 0.0 || depth > 1.0 ||
        !GiCausticReconstructWorldPosition(pixel, depth, worldPosition))
    {
        return false;
    }

    geometricNormal = NjulfC4OctDecodeNormal(payload.x);
    shadingNormal = NjulfC4OctDecodeNormal(payload.y);
    directionalDiffuseBase = NjulfC4UnpackRgb9E5(payload.z);
    dielectricF0 = NjulfC4UnpackDielectricF0(payload.w);
    return GiCausticFinite(geometricNormal) &&
        GiCausticFinite(shadingNormal) &&
        GiCausticFinite(directionalDiffuseBase) &&
        GiCausticFinite(dielectricF0) &&
        all(greaterThanEqual(directionalDiffuseBase, vec3(0.0))) &&
        all(greaterThanEqual(dielectricF0, vec3(0.0)));
}

bool GiCausticCellNeighborhoodOccupied(vec3 worldPosition)
{
    ivec4 center;
    if (!GiCausticCellFromPosition(worldPosition, center))
        return false;
    for (int z = -1; z <= 1; ++z)
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        uint photonOffset;
        uint photonCount;
        if (GiCausticFindCell(
                center + ivec4(x, y, z, 0),
                giCausticPc.CacheBankTableWordOffset,
                photonOffset,
                photonCount) && photonCount > 0u)
        {
            return true;
        }
    }
    return false;
}

float GiCausticLuminance(vec3 value)
{
    return dot(value, vec3(0.2126, 0.7152, 0.0722));
}

#endif
