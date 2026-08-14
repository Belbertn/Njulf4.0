#ifndef NJULF_DDGI_DIRECTIONAL_GUIDING_GLSL
#define NJULF_DDGI_DIRECTIONAL_GUIDING_GLSL

#include "ddgi_guiding_arithmetic.glsl"

// C3 persistent/dispatched payload ABI.  Keep this in lock-step with
// SimpleDdgiGuidingGpuContracts.cs.  A header/payload revision mismatch is a
// hard fallback, never an opportunity to reinterpret old learned data.
const uint SIMPLE_DDGI_GUIDING_ABI_VERSION = 0x43330009u;
const uint SIMPLE_DDGI_GUIDING_HEADER_WORDS = 8u;
const uint SIMPLE_DDGI_GUIDING_TRAINING_WORK_ITEM_WORDS = 14u;
const uint SIMPLE_DDGI_GUIDING_BUILD_WORK_ITEM_WORDS = 12u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_REQUEST_WORDS = 14u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_PAYLOAD_WORDS = 16u;
const uint SIMPLE_DDGI_GUIDING_PUBLICATION_RECORD_WORDS = 12u;
const uint SIMPLE_DDGI_GUIDING_MAX_LEAF_RESOLUTION = 16u;
const uint SIMPLE_DDGI_GUIDING_MAX_LEAF_COUNT = 256u;
const uint SIMPLE_DDGI_GUIDING_MAX_HIERARCHY_WEIGHT_COUNT = 341u;
const float SIMPLE_DDGI_GUIDING_PI = 3.14159265358979323846;
const float SIMPLE_DDGI_GUIDING_UNIFORM_SPHERE_PDF =
    1.0 / (4.0 * SIMPLE_DDGI_GUIDING_PI);
const float SIMPLE_DDGI_GUIDING_MINIMUM_UNIFORM_FRACTION = 0.10;

const uint SIMPLE_DDGI_GUIDING_DISTRIBUTION_UNIFORM_FALLBACK = 1u << 8u;
const uint SIMPLE_DDGI_GUIDING_DISTRIBUTION_BUILD_COMPLETE = 1u << 9u;
const uint SIMPLE_DDGI_GUIDING_DISTRIBUTION_VALIDATION_REFERENCE = 1u << 10u;
const uint SIMPLE_DDGI_GUIDING_DISTRIBUTION_INVALID = 1u << 11u;
const uint SIMPLE_DDGI_GUIDING_DISTRIBUTION_FLAGS_MASK = ~0xffu;

const uint SIMPLE_DDGI_GUIDING_TRAINING_FINITE_INCIDENT_RADIANCE = 1u << 0u;
const uint SIMPLE_DDGI_GUIDING_TRAINING_CONTENT_REVISION_MATCHED = 1u << 1u;
const uint SIMPLE_DDGI_GUIDING_TRAINING_RADIOMETRIC_TRANSPORT = 1u << 2u;
const uint SIMPLE_DDGI_GUIDING_TRAINING_REQUIRED_FLAGS =
    SIMPLE_DDGI_GUIDING_TRAINING_FINITE_INCIDENT_RADIANCE |
    SIMPLE_DDGI_GUIDING_TRAINING_CONTENT_REVISION_MATCHED |
    SIMPLE_DDGI_GUIDING_TRAINING_RADIOMETRIC_TRANSPORT;

const uint SIMPLE_DDGI_GUIDING_TECHNIQUE_UNIFORM_MAINTENANCE = 0u;
const uint SIMPLE_DDGI_GUIDING_TECHNIQUE_MIXTURE = 1u;
const uint SIMPLE_DDGI_GUIDING_MINIMUM_MAINTENANCE_RAYS = 8u;
const uint SIMPLE_DDGI_GUIDING_BRANCH_UNIFORM = 0u;
const uint SIMPLE_DDGI_GUIDING_BRANCH_GUIDED = 1u;

const uint SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_MAINTENANCE = 1u << 0u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_UNIFORM_BRANCH = 1u << 1u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_MIXTURE_GUIDED_BRANCH = 1u << 2u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_UNIFORM_FALLBACK = 1u << 3u;
const uint SIMPLE_DDGI_GUIDING_SAMPLE_INVALID_DISTRIBUTION = 1u << 4u;

#include "ddgi_guiding_payload_identity.glsl"

uint SimpleDdgiGuidingMaintenanceRayCount(uint totalRayCount)
{
    if (totalRayCount == 0u)
        return 0u;
    uint fractional = (totalRayCount + 3u) / 4u;
    return min(totalRayCount, max(
        SIMPLE_DDGI_GUIDING_MINIMUM_MAINTENANCE_RAYS,
        fractional));
}

bool SimpleDdgiGuidingIsMaintenanceSlot(
    uint slotIndex,
    uint totalRayCount)
{
    if (totalRayCount == 0u || slotIndex >= totalRayCount)
        return false;
    uint maintenanceCount = SimpleDdgiGuidingMaintenanceRayCount(
        totalRayCount);
    uint rank = (slotIndex * maintenanceCount + totalRayCount - 1u) /
        totalRayCount;
    return slotIndex == rank * totalRayCount / maintenanceCount;
}

const uint SIMPLE_DDGI_GUIDING_COUNTER_INVALID_RECORDS = 0u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_INVALID_HEADERS = 1u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_INVALID_PDFS = 2u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_PUBLICATION_REJECTIONS = 3u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_VALID_SAMPLES = 4u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_MAINTENANCE_SAMPLES = 5u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_MIXTURE_UNIFORM_SAMPLES = 6u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_MIXTURE_GUIDED_SAMPLES = 7u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_UNIFORM_FALLBACK_SAMPLES = 8u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_MAXIMUM_INVERSE_PDF_BITS = 9u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_MAXIMUM_PDF_BITS = 10u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_INVERSE_PDF_HISTOGRAM_BASE = 12u;
const uint SIMPLE_DDGI_GUIDING_INVERSE_PDF_HISTOGRAM_BIN_COUNT = 16u;
const uint SIMPLE_DDGI_GUIDING_VALIDATION_COUNTER_WORD_COUNT = 32u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_GPU_WORK_ITEM_COUNT = 28u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_GPU_TRAINING_RECORD_COUNT = 29u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_GPU_SAMPLE_REQUEST_COUNT = 30u;
const uint SIMPLE_DDGI_GUIDING_COUNTER_GPU_PREPARATION_STATUS = 31u;
const uint SIMPLE_DDGI_GUIDING_PUSH_GPU_GENERATED_WORK = 1u << 0u;

struct SimpleDdgiGuidingDistributionHeader
{
    uint abiVersion;
    uint virtualProbeId;
    uint pageGeneration;
    uint distributionGeneration;
    uint directionProposalEpoch;
    uint sampleCountAndAge;
    float totalIncidentEnergy;
    uint packedLeafResolutionAndFlags;
};

struct SimpleDdgiGuidingTrainingRecord
{
    uint physicalProbeIndex;
    uint virtualProbeId;
    uint pageGeneration;
    uint sourceDistributionGeneration;
    uint directionProposalEpoch;
    uint leafIndex;
    float samplePdf;
    float incidentLuminance;
    uint contentRevision;
    uint flags;
};

struct SimpleDdgiGuidingTrainingWorkItem
{
    uint physicalProbeIndex;
    uint virtualProbeId;
    uint pageGeneration;
    uint sourceDistributionGeneration;
    uint directionProposalEpoch;
    uint recordOffset;
    uint recordCount;
    uint partialOffset;
    uint expectedContentRevision;
    uint queueOffset;
    uint rayResultBaseIndex;
    uint directionSlotsPerProbe;
    uint sourceEpoch;
    uint sourceLightingGeneration;
};

struct SimpleDdgiGuidingBuildWorkItem
{
    uint physicalProbeIndex;
    uint virtualProbeId;
    uint pageGeneration;
    uint previousDistributionGeneration;
    uint targetDistributionGeneration;
    uint targetProposalEpoch;
    uint partialOffset;
    uint partialCount;
    uint sampleCountAndAge;
    uint expectedContentRevision;
    uint flags;
    uint reserved;
};

struct SimpleDdgiGuidingSampleRequest
{
    uint physicalProbeIndex;
    uint virtualProbeId;
    uint pageGeneration;
    uint expectedDistributionGeneration;
    uint expectedProposalEpoch;
    uint stableProbeIdLow;
    uint stableProbeIdHigh;
    uint slotIndex;
    uint technique;
    uint randomBranchBits;
    float requestedUniformFraction;
    uint sourceEpoch;
    uint sourceLightingGeneration;
    uint traceRayIndex;
};

struct SimpleDdgiGuidingSamplePayload
{
    uint abiVersion;
    uint stableProbeIdLow;
    uint stableProbeIdHigh;
    uint physicalProbeIndex;
    uint virtualProbeId;
    uint pageGeneration;
    uint distributionGeneration;
    uint directionProposalEpoch;
    uint slotIndex;
    uint techniqueAndBranch;
    uint sourceEpoch;
    uint sourceLightingGeneration;
    uint packedDirectionOct32;
    uint generationTimePdfBits;
    uint flags;
    uint traceOwnershipTag;
};

layout(push_constant) uniform SimpleDdgiGuidingPushConstants
{
    uint abiVersion;
    uint physicalProbeCapacity;
    uint leafResolution;
    uint hierarchyWeightCount;
    uint bankStrideWords;
    uint dispatchCount;
    uint readBankIndex;
    uint writeBankIndex;
    uint targetDistributionGeneration;
    uint targetProposalEpoch;
    uint flags;
    uint reserved;
} guidingPc;

bool SimpleDdgiGuidingFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool SimpleDdgiGuidingSupportedLeafResolution(uint leafResolution)
{
    return leafResolution == 4u || leafResolution == 8u || leafResolution == 16u;
}

uint SimpleDdgiGuidingLeafCount(uint leafResolution)
{
    return leafResolution * leafResolution;
}

uint SimpleDdgiGuidingHierarchyWeightCount(uint leafResolution)
{
    if (leafResolution == 0u)
        return 0u;
    uint count = 0u;
    for (uint side = leafResolution;; side >>= 1u)
    {
        count += side * side;
        if (side == 1u)
            break;
    }
    return count;
}

uint SimpleDdgiGuidingLevelOffset(uint leafResolution, uint sideLength)
{
    uint offset = 0u;
    for (uint side = leafResolution; side > sideLength; side >>= 1u)
        offset += side * side;
    return offset;
}

uint SimpleDdgiGuidingNodeIndex(
    uint leafResolution,
    uint sideLength,
    uint x,
    uint y)
{
    return SimpleDdgiGuidingLevelOffset(leafResolution, sideLength) +
        y * sideLength + x;
}

uint SimpleDdgiGuidingBankBaseWord(uint physicalProbeIndex)
{
    return physicalProbeIndex * guidingPc.bankStrideWords;
}

uint SimpleDdgiGuidingWeightWordOffset(uint weightIndex)
{
    return SIMPLE_DDGI_GUIDING_HEADER_WORDS + (weightIndex >> 1u);
}

float SimpleDdgiGuidingUnpackWeight(uint packedWord, uint weightIndex)
{
    vec2 pair = unpackHalf2x16(packedWord);
    return (weightIndex & 1u) == 0u ? pair.x : pair.y;
}

uint SimpleDdgiGuidingPackLeafResolutionAndFlags(uint leafResolution, uint flags)
{
    return leafResolution | (flags & SIMPLE_DDGI_GUIDING_DISTRIBUTION_FLAGS_MASK);
}

uint SimpleDdgiGuidingLeafResolution(SimpleDdgiGuidingDistributionHeader header)
{
    return header.packedLeafResolutionAndFlags & 0xffu;
}

uint SimpleDdgiGuidingHeaderFlags(SimpleDdgiGuidingDistributionHeader header)
{
    return header.packedLeafResolutionAndFlags &
        SIMPLE_DDGI_GUIDING_DISTRIBUTION_FLAGS_MASK;
}

bool SimpleDdgiGuidingHeaderIsCompatible(
    SimpleDdgiGuidingDistributionHeader header,
    SimpleDdgiGuidingSampleRequest request)
{
    uint flags = SimpleDdgiGuidingHeaderFlags(header);
    return guidingPc.abiVersion == SIMPLE_DDGI_GUIDING_ABI_VERSION &&
        header.abiVersion == SIMPLE_DDGI_GUIDING_ABI_VERSION &&
        header.virtualProbeId == request.virtualProbeId &&
        header.pageGeneration == request.pageGeneration &&
        header.distributionGeneration == request.expectedDistributionGeneration &&
        header.directionProposalEpoch == request.expectedProposalEpoch &&
        header.distributionGeneration != 0u &&
        header.directionProposalEpoch != 0u &&
        SimpleDdgiGuidingSupportedLeafResolution(
            SimpleDdgiGuidingLeafResolution(header)) &&
        SimpleDdgiGuidingLeafResolution(header) == guidingPc.leafResolution &&
        SimpleDdgiGuidingFinite(header.totalIncidentEnergy) &&
        header.totalIncidentEnergy >= 0.0 &&
        (flags & SIMPLE_DDGI_GUIDING_DISTRIBUTION_BUILD_COMPLETE) != 0u &&
        (flags & SIMPLE_DDGI_GUIDING_DISTRIBUTION_INVALID) == 0u;
}

float SimpleDdgiGuidingUnitOpen(uint bits)
{
    // Use 24 canonical mantissa bits and a cell centre: zero and one are both
    // impossible, so directions cannot drift over the azimuth seam by a
    // platform-dependent endpoint conversion.
    return (float(bits >> 8u) + 0.5) * (1.0 / 16777216.0);
}

uint SimpleDdgiGuidingHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

uint SimpleDdgiGuidingHash3(uint a, uint b, uint c)
{
    return SimpleDdgiGuidingHash(a ^
        SimpleDdgiGuidingHash(b + 0x9e3779b9u) ^
        SimpleDdgiGuidingHash(c + 0x85ebca6bu));
}

uint SimpleDdgiGuidingPackIntraLeaf(float u, float v)
{
    uint packedU = min(65535u, uint(clamp(u, 0.0, 0.99999994) * 65536.0));
    uint packedV = min(65535u, uint(clamp(v, 0.0, 0.99999994) * 65536.0));
    return packedU | (packedV << 16u);
}

vec2 SimpleDdgiGuidingUnpackIntraLeaf(uint packed)
{
    return vec2(
        (float(packed & 0xffffu) + 0.5) / 65536.0,
        (float(packed >> 16u) + 0.5) / 65536.0);
}

vec3 SimpleDdgiGuidingDirectionFromSquare(float u, float v)
{
    float phi = 2.0 * SIMPLE_DDGI_GUIDING_PI * u;
    float z = 2.0 * v - 1.0;
    float radius = sqrt(max(0.0, 1.0 - z * z));
    return vec3(cos(phi) * radius, sin(phi) * radius, z);
}

uint SimpleDdgiGuidingPackOctahedralSnorm16(vec3 direction)
{
    vec3 normal = normalize(direction);
    float denominator = max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-20);
    vec2 octahedral = normal.xy / denominator;
    if (normal.z < 0.0)
    {
        vec2 folded = (1.0 - abs(octahedral.yx)) *
            vec2(octahedral.x >= 0.0 ? 1.0 : -1.0,
                 octahedral.y >= 0.0 ? 1.0 : -1.0);
        octahedral = folded;
    }
    ivec2 signedPacked = ivec2(round(clamp(octahedral, -1.0, 1.0) * 32767.0));
    return (uint(signedPacked.x) & 0xffffu) |
        ((uint(signedPacked.y) & 0xffffu) << 16u);
}

uint SimpleDdgiGuidingPackTechniqueAndBranch(uint technique, uint branch)
{
    return (technique & 0xffu) | ((branch & 0xffu) << 8u);
}

#endif
