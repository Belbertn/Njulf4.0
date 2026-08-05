#ifndef NJULF_DDGI_SIMPLE_RECEIVER_ABI_GLSL
#define NJULF_DDGI_SIMPLE_RECEIVER_ABI_GLSL

// Compact receiver projection. Keep synchronized with
// GPUSimpleDdgiReceiverProbe and SimpleDdgiReceiverProbeEncoding.
const uint SIMPLE_DDGI_RECEIVER_PROBE_STRIDE_WORDS = 4u;
const uint SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS = 0xffffffffu;
const float SIMPLE_DDGI_RECEIVER_RELOCATION_RANGE_SPACINGS = 0.5;
const float SIMPLE_DDGI_RELOCATION_UPDATE_MAX_SPACINGS = 0.45;
const float SIMPLE_DDGI_RECEIVER_ACTIVE_WEIGHT_THRESHOLD = 0.001;

const uint SIMPLE_DDGI_RECEIVER_FLAG_PUBLISHED_COHERENT = 1u << 0u;
const uint SIMPLE_DDGI_RECEIVER_FLAG_FRESH = 1u << 1u;
const uint SIMPLE_DDGI_RECEIVER_FLAG_SCROLL_EXPOSED = 1u << 2u;
const uint SIMPLE_DDGI_RECEIVER_FLAG_RELOCATION_PENDING = 1u << 3u;
const uint SIMPLE_DDGI_RECEIVER_FLAG_INACTIVE = 1u << 4u;
const uint SIMPLE_DDGI_RECEIVER_FLAG_INACTIVE_CLASSIFICATION = 1u << 5u;
const uint SIMPLE_DDGI_RECEIVER_STATE_REJECTION_FLAGS =
    SIMPLE_DDGI_RECEIVER_FLAG_FRESH |
    SIMPLE_DDGI_RECEIVER_FLAG_SCROLL_EXPOSED |
    SIMPLE_DDGI_RECEIVER_FLAG_RELOCATION_PENDING |
    SIMPLE_DDGI_RECEIVER_FLAG_INACTIVE |
    SIMPLE_DDGI_RECEIVER_FLAG_INACTIVE_CLASSIFICATION;
const uint SIMPLE_DDGI_RECEIVER_PUBLISHER_INPUT_FLAGS =
    SIMPLE_DDGI_RECEIVER_STATE_REJECTION_FLAGS;

struct SimpleDdgiReceiverProbe
{
    vec3 relocation;
    float activeWeight;
    uint flags;
    uint atlasProbeAddress;
};

uint SimpleDdgiPackReceiverSnorm16(float value)
{
    int quantized = int(roundEven(clamp(value, -1.0, 1.0) * 32767.0));
    return uint(quantized) & 0xffffu;
}

float SimpleDdgiUnpackReceiverSnorm16(uint value)
{
    int signedValue = int(value & 0xffffu);
    if (signedValue >= 32768)
        signedValue -= 65536;
    return max(float(signedValue) / 32767.0, -1.0);
}

uint SimpleDdgiPackReceiverActiveWeight(float activeWeight)
{
    if (activeWeight <= SIMPLE_DDGI_RECEIVER_ACTIVE_WEIGHT_THRESHOLD)
        return 0u;
    uint quantized = uint(roundEven(clamp(activeWeight, 0.0, 1.0) * 65535.0));
    return clamp(quantized, 66u, 65535u);
}

bool TryPackSimpleDdgiReceiverProbe(
    vec3 relocation,
    float probeSpacing,
    float activeWeight,
    uint normalizedReceiverFlags,
    uint atlasProbeAddress,
    out uvec4 packed)
{
    packed = uvec4(0xffffffffu);
    if (isnan(probeSpacing) || isinf(probeSpacing) || probeSpacing <= 0.0 ||
        any(isnan(relocation)) || any(isinf(relocation)) ||
        isnan(activeWeight) || isinf(activeWeight) ||
        activeWeight < 0.0 || activeWeight > 1.0 ||
        (normalizedReceiverFlags &
            ~SIMPLE_DDGI_RECEIVER_PUBLISHER_INPUT_FLAGS) != 0u ||
        atlasProbeAddress == SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS)
    {
        return false;
    }

    float relocationRange =
        probeSpacing * SIMPLE_DDGI_RECEIVER_RELOCATION_RANGE_SPACINGS;
    if (isnan(relocationRange) || isinf(relocationRange) ||
        relocationRange <= 0.0 ||
        any(greaterThan(abs(relocation), vec3(relocationRange))))
    {
        return false;
    }

    vec3 normalizedRelocation = relocation / relocationRange;
    uint x = SimpleDdgiPackReceiverSnorm16(normalizedRelocation.x);
    uint y = SimpleDdgiPackReceiverSnorm16(normalizedRelocation.y);
    uint z = SimpleDdgiPackReceiverSnorm16(normalizedRelocation.z);
    uint weight = SimpleDdgiPackReceiverActiveWeight(activeWeight);
    packed = uvec4(
        x | (y << 16u),
        z | (weight << 16u),
        normalizedReceiverFlags |
            SIMPLE_DDGI_RECEIVER_FLAG_PUBLISHED_COHERENT,
        atlasProbeAddress);
    return true;
}

SimpleDdgiReceiverProbe ReadSimpleDdgiReceiverProbe(
    uint bufferIndex,
    uint probeIndex,
    float probeSpacing)
{
    uint baseWord = probeIndex * SIMPLE_DDGI_RECEIVER_PROBE_STRIDE_WORDS;
    // The compact stride guarantees four-word alignment, preserving one 128-bit
    // receiver-state load per in-domain corner.
    uvec4 packed = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord);
    float relocationRange = max(probeSpacing, 0.0) *
        SIMPLE_DDGI_RECEIVER_RELOCATION_RANGE_SPACINGS;
    SimpleDdgiReceiverProbe result;
    result.relocation = vec3(
        SimpleDdgiUnpackReceiverSnorm16(packed.x),
        SimpleDdgiUnpackReceiverSnorm16(packed.x >> 16u),
        SimpleDdgiUnpackReceiverSnorm16(packed.y)) * relocationRange;
    result.activeWeight = float(packed.y >> 16u) / 65535.0;
    result.flags = packed.z;
    result.atlasProbeAddress = packed.w;
    return result;
}

void WriteInvalidSimpleDdgiReceiverProbe(
    uint bufferIndex,
    uint probeIndex)
{
    uint baseWord = probeIndex * SIMPLE_DDGI_RECEIVER_PROBE_STRIDE_WORDS;
    // Invalidate first. If this record is ever observed across an ownership
    // transition, no payload can be accepted until the final flag commit.
    WriteStorageWordUniform(bufferIndex, baseWord + 2u, 0u);
    memoryBarrierBuffer();
    WriteStorageWordUniform(bufferIndex, baseWord + 0u, 0xffffffffu);
    WriteStorageWordUniform(bufferIndex, baseWord + 1u, 0xffffffffu);
    WriteStorageWordUniform(
        bufferIndex,
        baseWord + 3u,
        SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS);
}

void PublishPackedSimpleDdgiReceiverProbe(
    uint bufferIndex,
    uint probeIndex,
    uvec4 packed)
{
    uint baseWord = probeIndex * SIMPLE_DDGI_RECEIVER_PROBE_STRIDE_WORDS;
    WriteStorageWordUniform(bufferIndex, baseWord + 2u, 0u);
    memoryBarrierBuffer();
    WriteStorageWordUniform(bufferIndex, baseWord + 0u, packed.x);
    WriteStorageWordUniform(bufferIndex, baseWord + 1u, packed.y);
    WriteStorageWordUniform(bufferIndex, baseWord + 3u, packed.w);
    memoryBarrierBuffer();
    // Publication is the final scalar store. Render-graph/queue barriers make
    // the complete record visible as one transaction to later receiver stages.
    WriteStorageWordUniform(bufferIndex, baseWord + 2u, packed.z);
}

bool PublishSimpleDdgiReceiverProbe(
    uint bufferIndex,
    uint probeIndex,
    vec3 relocation,
    float probeSpacing,
    float activeWeight,
    uint normalizedReceiverFlags,
    uint atlasProbeAddress)
{
    uvec4 packed;
    if (!TryPackSimpleDdgiReceiverProbe(
            relocation,
            probeSpacing,
            activeWeight,
            normalizedReceiverFlags,
            atlasProbeAddress,
            packed))
    {
        WriteInvalidSimpleDdgiReceiverProbe(bufferIndex, probeIndex);
        return false;
    }

    PublishPackedSimpleDdgiReceiverProbe(bufferIndex, probeIndex, packed);
    return true;
}

#endif
