#ifndef NJULF_DDGI_NEAR_FIELD_RESIDUAL_GLSL
#define NJULF_DDGI_NEAR_FIELD_RESIDUAL_GLSL

#include "c5_receiver_payload.glsl"

// C5 is intentionally a separate, opt-in ABI.  These stages are not part of
// the global bindless contract until the renderer has explicitly created the
// source attachment, history identity resources, barriers, and dispatch path.
// V13 makes complete per-tile records the sole source for header summaries.
// Keep this in lockstep with SimpleDdgiNearFieldResidualGpuAbi.
const uint SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_ABI_VERSION = 0x4335000du;
const uint SIMPLE_DDGI_NEAR_FIELD_TELEMETRY_MAGIC = 0x4335544du;
const uint SIMPLE_DDGI_NEAR_FIELD_TELEMETRY_HEADER_WORDS = 32u;
const uint SIMPLE_DDGI_NEAR_FIELD_ACTIVE_TILE_HEADER_WORDS = 64u;
const uint SIMPLE_DDGI_NEAR_FIELD_TELEMETRY_TILE_WORDS = 24u;
const uint SIMPLE_DDGI_NEAR_FIELD_TELEMETRY_TRACE_COMPLETE = 1u << 0u;
const uint SIMPLE_DDGI_NEAR_FIELD_TELEMETRY_TEMPORAL_COMPLETE = 1u << 1u;
const uint SIMPLE_DDGI_NEAR_FIELD_DIRECT_DIFFUSE_SOURCE = 1u << 0u;
const uint SIMPLE_DDGI_NEAR_FIELD_EMISSIVE_SOURCE = 1u << 1u;
const uint SIMPLE_DDGI_NEAR_FIELD_ALLOWED_SOURCE_TERMS =
    SIMPLE_DDGI_NEAR_FIELD_DIRECT_DIFFUSE_SOURCE |
    SIMPLE_DDGI_NEAR_FIELD_EMISSIVE_SOURCE;

const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_VALID_CANDIDATE = 1u << 0u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_SCREEN_SPACE_HIT = 1u << 1u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_HISTORY_INPUT_VALID = 1u << 2u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_HISTORY_ACCEPTED = 1u << 3u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_CAMERA_CUT = 1u << 4u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_REVERSED_Z = 1u << 5u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_SOURCE_ATTACHMENT_VERIFIED = 1u << 6u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_INVALID_AND_MISS_ZEROED = 1u << 7u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_COMPOSITE_VALID_ONLY = 1u << 8u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_FOLIAGE_MOTION_VALID = 1u << 9u;
const uint SIMPLE_DDGI_NEAR_FIELD_FLAG_SOURCE_LIGHTING_EPOCH_CHANGED = 1u << 10u;
const uint SIMPLE_DDGI_NEAR_FIELD_REJECTION_REASON_SHIFT = 12u;
const uint SIMPLE_DDGI_NEAR_FIELD_REJECTION_REASON_MASK = 0xfu <<
    SIMPLE_DDGI_NEAR_FIELD_REJECTION_REASON_SHIFT;

// Raw values of GlobalIlluminationDebugView. They travel through a dedicated
// C5 frame channel and never enter the legacy forward debug-number namespace.
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_NONE = 0u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_SOURCE_RADIANCE = 56u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_RAW_CANDIDATE = 57u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_NEAR_ESTIMATE = 58u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_LOW_ESTIMATE = 59u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_SIGNED_RESIDUAL = 60u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_FINAL_CONTRIBUTION = 61u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_CONFIDENCE = 62u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_HISTORY_LENGTH = 63u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_HISTORY_REJECTION = 64u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_TRACE_DISTANCE_VALIDITY = 65u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_TILE_ACTIVITY = 66u;
const uint SIMPLE_DDGI_NEAR_FIELD_DEBUG_B3_FOOTPRINT = 67u;

const uint SIMPLE_DDGI_NEAR_FIELD_MAX_TRACE_STEPS = 256u;
const uint SIMPLE_DDGI_NEAR_FIELD_MAX_MIP_VISITS = 32u;
const uint SIMPLE_DDGI_NEAR_FIELD_MAX_BINARY_REFINEMENTS = 16u;
const uint SIMPLE_DDGI_NEAR_FIELD_MAX_FILTER_ITERATIONS = 8u;
const uint SIMPLE_DDGI_NEAR_FIELD_MAX_FILTER_RADIUS = 8u;
const uint SIMPLE_DDGI_NEAR_FIELD_MAX_HISTORY_LENGTH = 64u;
const uint SIMPLE_DDGI_NEAR_FIELD_MAX_SURFACE_TABLE_ENTRIES = 65534u;
const uint SIMPLE_DDGI_NEAR_FIELD_INVALID_SURFACE_TOKEN = 0xffffu;
const float SIMPLE_DDGI_NEAR_FIELD_MAX_ENCODED_TRACE_DISTANCE = 65504.0;
const float SIMPLE_DDGI_NEAR_FIELD_MINIMUM_SIGNAL = 1.0e-6;
const float SIMPLE_DDGI_NEAR_FIELD_MINIMUM_VARIANCE = 1.0e-12;
const float SIMPLE_DDGI_NEAR_FIELD_TEMPORAL_SNR_LOW = 1.5;
const float SIMPLE_DDGI_NEAR_FIELD_TEMPORAL_SNR_HIGH = 3.0;
const float SIMPLE_DDGI_NEAR_FIELD_SPATIAL_SNR_LOW = 1.0;
const float SIMPLE_DDGI_NEAR_FIELD_SPATIAL_SNR_HIGH = 2.5;
const float SIMPLE_DDGI_NEAR_FIELD_MAX_RELATIVE_CORRECTION = 0.20;
const float SIMPLE_DDGI_NEAR_FIELD_MIN_ABSOLUTE_CORRECTION = 1.0e-4;
const uint SIMPLE_DDGI_NEAR_FIELD_HISTORY_VALID_BIT = 1u;
const uint SIMPLE_DDGI_NEAR_FIELD_HISTORY_LENGTH_SHIFT = 1u;
const uint SIMPLE_DDGI_NEAR_FIELD_HISTORY_LENGTH_MASK = 0x7fu <<
    SIMPLE_DDGI_NEAR_FIELD_HISTORY_LENGTH_SHIFT;
const uint SIMPLE_DDGI_NEAR_FIELD_HISTORY_EPOCH_SHIFT = 8u;
const uint SIMPLE_DDGI_NEAR_FIELD_HISTORY_EPOCH_MASK = 0x00ffffffu <<
    SIMPLE_DDGI_NEAR_FIELD_HISTORY_EPOCH_SHIFT;

// Exactly twelve 32-bit words (48 bytes). Flags occupy the lower 16 bits of
// packedFlagsAndReceiverFootprint and the receiver's FP16 B3 footprint the
// upper 16. Trace writes the current history bank directly; no third metadata
// allocation exists.
struct SimpleDdgiNearFieldResidualHitMetadata
{
    float receiverLinearDepth;
    float hitLinearDepth;
    uint packedFlagsAndReceiverFootprint;
    uint packedHitNormal;
    uvec2 receiverIdentity;
    uvec2 hitIdentity;
    uint packedReceiverRevisions;
    uint packedHitRevisions;
    uint packedHitUv;
    uint packedHitSourceRadiance;
};

struct SimpleDdgiNearFieldSurfaceEntry
{
    uint stableObjectId;
    uint stableMaterialId;
    uint packedObjectMaterialRevisions;
    uint coverageMotionFlags;
};

struct SimpleDdgiNearFieldResidualTraceFrameConstants
{
    mat4 viewProjection;
    mat4 inverseViewProjection;
    mat4 previousViewProjection;
    mat4 previousInverseViewProjection;
    vec4 fullExtentAndInverse;
    vec4 clipAndSequence;
};

struct SimpleDdgiNearFieldResidualTileRecord
{
    uint tileIndex;
    uint traceCounts0;
    uint traceCounts1;
    uint traceCounts2;
    uint historyCounts0;
    uint historyCounts1;
    uint historyCounts2;
    uint traceVisitTotals;
    uint tracePeakAndRefinement;
    uint varianceSumBits;
    uint maximumVarianceBits;
    uint signedResidualEnergyBits;
    uint absoluteResidualEnergyBits;
    uint squaredResidualEnergyBits;
    uint maximumAbsoluteResidualEnergyBits;
    uint flagsAndMaximumDistance;
    uint detailedHistoryCounts0;
    uint detailedHistoryCounts1;
    uint detailedHistoryCounts2;
    uint proposalCounts;
    uint guidedAndTraversalCounts;
    uint hitAndValidSampleCounts;
    uint reserved22;
    uint reserved23;
};

uint SimpleDdgiNearFieldPackTraceCounts(
    uint covered, uint valid, uint invalid, uint raysLaunched)
{
    // 8x8 populations need seven bits each; four rays per receiver need nine.
    return min(covered, 64u) |
        (min(valid, 64u) << 7u) |
        (min(invalid, 64u) << 14u) |
        (min(raysLaunched, 256u) << 21u);
}

uint SimpleDdgiNearFieldPackFourTileCounts(uvec4 counts)
{
    // Every count describes a subset of one 8x8 workgroup and is therefore
    // losslessly representable in an unsigned byte.
    uvec4 bounded = min(counts, uvec4(64u));
    return bounded.x | (bounded.y << 8u) |
        (bounded.z << 16u) | (bounded.w << 24u);
}

struct SimpleDdgiNearFieldResidualTracePushConstants
{
    uint abiVersion;
    uint traceSourceTerms;
    uint fullWidth;
    uint fullHeight;
    uint traceWidth;
    uint traceHeight;
    uint frameIndex;
    uint historyEpoch;
    uint maximumTraceSteps;
    uint raysPerPixel;
    uint binaryRefinementSteps;
    uint flags;
    float thickness;
    float startBias;
    float depthTolerance;
    float minimumNormalDot;
    float maximumTraceDistance;
    float fullWeightTraceDistance;
    uint minimumB3FootprintRadius;
    uint maximumB3FootprintRadius;
    uint traceSourceRevision;
};

struct SimpleDdgiNearFieldResidualResetPushConstants
{
    uint abiVersion;
    uint metadataCount;
    uint tileWordCount;
    uint historyEpoch;
    uint flags;
    uint frameSerialLow;
    uint frameSerialHigh;
    uint tileCount;
};

struct SimpleDdgiNearFieldResidualFinalizePushConstants
{
    uint abiVersion;
    uint tileCount;
    uint traceWidth;
    uint traceHeight;
};

struct SimpleDdgiNearFieldResidualPreparePushConstants
{
    uint abiVersion;
    uint fullWidth;
    uint fullHeight;
    uint traceWidth;
    uint traceHeight;
    uint flags;
    uint tileCapacity;
    uint raysPerPixel;
    float nearPlane;
    float farPlane;
    uint activeTileHeaderWords;
    uint indirectStageCount;
};

struct SimpleDdgiNearFieldResidualTemporalPushConstants
{
    uint abiVersion;
    uint traceWidth;
    uint traceHeight;
    uint historyReadIndex;
    uint historyWriteIndex;
    uint historyEpoch;
    uint traceSourceAbiRevision;
    uint viewportRevision;
    uint hizRevision;
    uint effectiveModeRevision;
    uint exposureDomainRevision;
    uint structuralProjectionRevision;
    uint originRebaseRevision;
    uint sceneGeneration;
    uint traceSourceContentRevision;
    uint nearFieldLayoutRevision;
    uint b3OwnershipRevision;
    uint traceSourceLayoutRevision;
    uint maximumHistoryLength;
    uint flags;
    float temporalBlend;
    float depthTolerance;
    float minimumNormalDot;
    float hitUvTolerance;
};

struct SimpleDdgiNearFieldResidualFilterPushConstants
{
    uint abiVersion;
    uint traceWidth;
    uint traceHeight;
    uint iterationIndex;
    uint iterationCount;
    uint filterRadius;
    float depthTolerance;
    float normalPower;
    float minimumNormalDot;
    float reserved0;
    uint historyEpoch;
    uint flags;
};

struct SimpleDdgiNearFieldResidualFrequencyPushConstants
{
    uint abiVersion;
    uint traceWidth;
    uint traceHeight;
    uint historyEpoch;
    uint activeTileHeaderWords;
    uint flags;
    float depthTolerance;
    float minimumNormalDot;
    uint maximumOuterStride;
    uint debugView;
    uint reserved1;
    uint reserved2;
};

struct SimpleDdgiNearFieldResidualCompositePushConstants
{
    uint abiVersion;
    uint fullWidth;
    uint fullHeight;
    uint traceWidth;
    uint traceHeight;
    uint historyEpoch;
    uint flags;
    float residualIntensity;
    float confidenceFloor;
    float reserved0;
    float reserved1;
    uint debugView;
};

bool SimpleDdgiNearFieldIsDebugView(uint view)
{
    return view >= SIMPLE_DDGI_NEAR_FIELD_DEBUG_SOURCE_RADIANCE &&
        view <= SIMPLE_DDGI_NEAR_FIELD_DEBUG_B3_FOOTPRINT;
}

uint SimpleDdgiNearFieldRejectionReason(
    SimpleDdgiNearFieldResidualHitMetadata metadata)
{
    return ((metadata.packedFlagsAndReceiverFootprint & 0xffffu) &
        SIMPLE_DDGI_NEAR_FIELD_REJECTION_REASON_MASK) >>
        SIMPLE_DDGI_NEAR_FIELD_REJECTION_REASON_SHIFT;
}

bool SimpleDdgiNearFieldFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool SimpleDdgiNearFieldFinite(vec3 value)
{
    return SimpleDdgiNearFieldFinite(value.x) &&
        SimpleDdgiNearFieldFinite(value.y) &&
        SimpleDdgiNearFieldFinite(value.z);
}

bool SimpleDdgiNearFieldTraceSourcesAllowed(uint terms)
{
    return (terms & ~SIMPLE_DDGI_NEAR_FIELD_ALLOWED_SOURCE_TERMS) == 0u &&
        (terms & SIMPLE_DDGI_NEAR_FIELD_ALLOWED_SOURCE_TERMS) != 0u;
}

bool SimpleDdgiNearFieldHasFlag(uint flags, uint flag)
{
    return (flags & flag) != 0u;
}

uint SimpleDdgiNearFieldMetadataFlags(
    SimpleDdgiNearFieldResidualHitMetadata metadata)
{
    return metadata.packedFlagsAndReceiverFootprint & 0xffffu;
}

void SimpleDdgiNearFieldSetMetadataFlags(
    inout SimpleDdgiNearFieldResidualHitMetadata metadata,
    uint flags)
{
    metadata.packedFlagsAndReceiverFootprint =
        (metadata.packedFlagsAndReceiverFootprint & 0xffff0000u) |
        (flags & 0xffffu);
}

float SimpleDdgiNearFieldReceiverFootprint(
    SimpleDdgiNearFieldResidualHitMetadata metadata)
{
    return unpackHalf2x16(
        metadata.packedFlagsAndReceiverFootprint & 0xffff0000u).y;
}

void SimpleDdgiNearFieldSetReceiverFootprint(
    inout SimpleDdgiNearFieldResidualHitMetadata metadata,
    float footprint)
{
    uint packed = packHalf2x16(vec2(0.0, max(footprint, 0.0)));
    metadata.packedFlagsAndReceiverFootprint =
        (metadata.packedFlagsAndReceiverFootprint & 0xffffu) |
        (packed & 0xffff0000u);
}

vec2 SimpleDdgiNearFieldHitUv(
    SimpleDdgiNearFieldResidualHitMetadata metadata)
{
    return unpackHalf2x16(metadata.packedHitUv);
}

vec3 SimpleDdgiNearFieldDecodeOctNormal(vec2 encoded)
{
    vec2 f = clamp(encoded, vec2(-1.0), vec2(1.0));
    vec3 normal = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    if (normal.z < 0.0)
    {
        vec2 folded = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
        normal.xy = folded;
    }
    float lengthSquared = dot(normal, normal);
    return lengthSquared > 1.0e-12 && SimpleDdgiNearFieldFinite(normal)
        ? normal * inversesqrt(lengthSquared)
        : vec3(0.0);
}

vec2 SimpleDdgiNearFieldEncodeOctNormal(vec3 value)
{
    float lengthSquared = dot(value, value);
    if (lengthSquared <= 1.0e-12 || !SimpleDdgiNearFieldFinite(value))
        return vec2(0.0);
    vec3 normal = value * inversesqrt(lengthSquared);
    normal /= abs(normal.x) + abs(normal.y) + abs(normal.z);
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    return clamp(normal.xy, vec2(-1.0), vec2(1.0));
}

uint SimpleDdgiNearFieldPackHistoryValidity(uint historyLength, uint historyEpoch)
{
    uint length = clamp(historyLength, 1u,
        SIMPLE_DDGI_NEAR_FIELD_MAX_HISTORY_LENGTH);
    return SIMPLE_DDGI_NEAR_FIELD_HISTORY_VALID_BIT |
        (length << SIMPLE_DDGI_NEAR_FIELD_HISTORY_LENGTH_SHIFT) |
        ((historyEpoch & 0x00ffffffu) <<
            SIMPLE_DDGI_NEAR_FIELD_HISTORY_EPOCH_SHIFT);
}

bool SimpleDdgiNearFieldUnpackHistoryValidity(uint packed,
    uint expectedHistoryEpoch, out uint historyLength)
{
    historyLength = (packed & SIMPLE_DDGI_NEAR_FIELD_HISTORY_LENGTH_MASK) >>
        SIMPLE_DDGI_NEAR_FIELD_HISTORY_LENGTH_SHIFT;
    uint epoch = (packed & SIMPLE_DDGI_NEAR_FIELD_HISTORY_EPOCH_MASK) >>
        SIMPLE_DDGI_NEAR_FIELD_HISTORY_EPOCH_SHIFT;
    return (packed & SIMPLE_DDGI_NEAR_FIELD_HISTORY_VALID_BIT) != 0u &&
        historyLength >= 1u &&
        historyLength <= SIMPLE_DDGI_NEAR_FIELD_MAX_HISTORY_LENGTH &&
        epoch == (expectedHistoryEpoch & 0x00ffffffu);
}

bool SimpleDdgiNearFieldIsValidCandidate(vec4 residual)
{
    return SimpleDdgiNearFieldFinite(residual.rgb) &&
        SimpleDdgiNearFieldFinite(residual.a) && residual.a > 0.0;
}

float SimpleDdgiNearFieldSmoothEvidence(float low, float high, float value)
{
    if (!SimpleDdgiNearFieldFinite(low) ||
        !SimpleDdgiNearFieldFinite(high) ||
        !SimpleDdgiNearFieldFinite(value) || high <= low)
    {
        return 0.0;
    }
    float t = clamp((value - low) / (high - low), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

float SimpleDdgiNearFieldSignalConfidence(
    float mean,
    float variance,
    float sampleCount,
    float snrLow,
    float snrHigh)
{
    if (!SimpleDdgiNearFieldFinite(mean) ||
        !SimpleDdgiNearFieldFinite(variance) ||
        !SimpleDdgiNearFieldFinite(sampleCount) ||
        variance < 0.0 || sampleCount <= 0.0 ||
        abs(mean) <= SIMPLE_DDGI_NEAR_FIELD_MINIMUM_SIGNAL)
    {
        return 0.0;
    }
    float standardError = sqrt(max(
        variance / sampleCount,
        SIMPLE_DDGI_NEAR_FIELD_MINIMUM_VARIANCE));
    float snr = abs(mean) / standardError;
    return SimpleDdgiNearFieldSmoothEvidence(snrLow, snrHigh, snr);
}

float SimpleDdgiNearFieldTemporalEvidenceConfidence(
    vec2 moments,
    uint historyLength)
{
    float historyEvidence = SimpleDdgiNearFieldSmoothEvidence(
        8.0, 32.0, float(historyLength));
    float variance = max(moments.y - moments.x * moments.x, 0.0);
    return historyEvidence * SimpleDdgiNearFieldSignalConfidence(
        moments.x,
        variance,
        float(max(historyLength, 1u)),
        SIMPLE_DDGI_NEAR_FIELD_TEMPORAL_SNR_LOW,
        SIMPLE_DDGI_NEAR_FIELD_TEMPORAL_SNR_HIGH);
}

float SimpleDdgiNearFieldDepthWeight(float currentDepth, float neighbourDepth,
    float tolerance)
{
    if (!SimpleDdgiNearFieldFinite(currentDepth) ||
        !SimpleDdgiNearFieldFinite(neighbourDepth) ||
        !SimpleDdgiNearFieldFinite(tolerance) || tolerance < 0.0)
    {
        return 0.0;
    }
    if (tolerance == 0.0)
        return currentDepth == neighbourDepth ? 1.0 : 0.0;
    return clamp(1.0 - abs(currentDepth - neighbourDepth) / tolerance, 0.0, 1.0);
}

SimpleDdgiNearFieldResidualHitMetadata
SimpleDdgiNearFieldZeroMetadata()
{
    SimpleDdgiNearFieldResidualHitMetadata metadata;
    metadata.receiverLinearDepth = 0.0;
    metadata.hitLinearDepth = 0.0;
    metadata.packedFlagsAndReceiverFootprint = 0u;
    metadata.packedHitNormal = 0u;
    metadata.receiverIdentity = uvec2(0u);
    metadata.hitIdentity = uvec2(0u);
    metadata.packedReceiverRevisions = 0u;
    metadata.packedHitRevisions = 0u;
    metadata.packedHitUv = 0u;
    metadata.packedHitSourceRadiance = 0u;
    return metadata;
}

void SimpleDdgiNearFieldDecodeReceiverPayload(
    uvec4 payload,
    out vec4 packedNormals,
    out uint surfaceToken,
    out vec3 diffuseBase,
    out vec3 dielectricF0)
{
    packedNormals = vec4(
        unpackSnorm2x16(payload.x),
        unpackSnorm2x16(payload.y));
    surfaceToken = payload.z & 0xffffu;
    dielectricF0 = NjulfC5UnpackRgb565(payload.z >> 16u);
    diffuseBase = NjulfC5UnpackRgb9E5(payload.w);
}

#endif
