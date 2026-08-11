#ifndef NJULF_DDGI_SIMPLE_DIRECTIONAL_SCRATCH_GLSL
#define NJULF_DDGI_SIMPLE_DIRECTIONAL_SCRATCH_GLSL

// The directional projection runs after every ray-scratch consumer. Reuse the
// first 128 bytes of each queue-local probe allocation as a transient FP32
// record rather than adding a persistent full-precision sidecar. V2 prepares
// this record before projecting from its independent source cache. V1 first
// consumes every in-place ray and only then overwrites the prefix. Production
// settings reserve at least 16 compact five-word ray records (320 bytes) per
// probe, so this layout also fits the smallest qualified allocation.
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_WORDS = 32u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_COEFFICIENT_WORDS = 27u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_VALID_COUNT_WORD = 27u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_RAY_COUNT_WORD = 28u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_GENERATION_WORD = 29u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_FINGERPRINT_WORD = 30u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_STATUS_WORD = 31u;

// Zero is deliberately the no-work state written before projection. A prepare
// dispatch publishes the prefix, projection adds the complete coefficient
// mask, and the consumer accepts only that finished mode-specific value.
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_STATUS_EMPTY = 0u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_STATUS_PROJECTING = 0x44534800u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_COEFFICIENT_MASK = 0x1ffu;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_FAILURE_BIT = 1u << 9u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_DYNAMIC_MASK = 0x3ffu;
// Low coefficient-mask bits are unused on a failed projection and carry a
// bounded diagnostic reason to the publication/commit feedback path.
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_FAILURE_HEADER = 1u;
const uint SIMPLE_DDGI_DIRECTIONAL_SCRATCH_FAILURE_SOURCE = 2u;
// Update word 11 reserves bit 31 for the ordered trace fallback handshake.
// The persistent cache address is the remaining one-based 31-bit value.
const uint SIMPLE_DDGI_DIRECTIONAL_CACHE_BASE_MASK = 0x7fffffffu;

uint SimpleDdgiDirectionalPublicationGeneration(
    SimpleDdgiProbeUpdate update,
    SimpleDdgiProbeState state)
{
    uint generation = SimpleDdgiProbeGeneration(state);
    return (update.flags & SIMPLE_DDGI_UPDATE_INVALIDATE) != 0u
        ? NextSimpleDdgiPhysicalGeneration(generation)
        : generation;
}

uint SimpleDdgiDirectionalSourceCacheGeneration(
    SimpleDdgiProbeUpdate update,
    SimpleDdgiProbeState state)
{
    uint generation = SimpleDdgiProbeGeneration(state);
    return SimpleDdgiUpdateRequiresSourceRefresh(update) &&
            (update.flags & SIMPLE_DDGI_UPDATE_INVALIDATE) != 0u
        ? NextSimpleDdgiPhysicalGeneration(generation)
        : generation;
}

uint SimpleDdgiDirectionalScratchExpectedCoefficientMask(uint mode)
{
    uint coefficientCount = SimpleDdgiRadianceShCoefficientCount(mode);
    return coefficientCount == 0u
        ? 0u
        : (1u << coefficientCount) - 1u;
}

bool SimpleDdgiDirectionalScratchStatusIsOwned(uint status)
{
    return (status & ~SIMPLE_DDGI_DIRECTIONAL_SCRATCH_DYNAMIC_MASK) ==
        SIMPLE_DDGI_DIRECTIONAL_SCRATCH_STATUS_PROJECTING;
}

uint SimpleDdgiDirectionalScratchCapacityWords(
    SimpleDdgiParams params,
    SimpleDdgiVolume volume)
{
    return params.raysPerProbe * SimpleDdgiRayResultStrideWords(volume);
}

uint SimpleDdgiDirectionalScratchBaseWord(
    SimpleDdgiParams params,
    SimpleDdgiVolume volume,
    uint localProbeOffset)
{
    return localProbeOffset *
        SimpleDdgiDirectionalScratchCapacityWords(params, volume);
}

uint SimpleDdgiDirectionalScratchFingerprint(
    SimpleDdgiProbeUpdate update,
    uint slotGeneration,
    uint rayCount,
    uint mode)
{
    uint hash = 2166136261u;
    hash = SimpleDdgiRadianceShHashAdd(hash, update.probeIndex);
    hash = SimpleDdgiRadianceShHashAdd(hash, update.physicalProbeIndex);
    hash = SimpleDdgiRadianceShHashAdd(hash, update.expectedGeneration);
    hash = SimpleDdgiRadianceShHashAdd(hash, update.sourceEpoch);
    hash = SimpleDdgiRadianceShHashAdd(
        hash,
        update.sourceLightingGeneration);
    hash = SimpleDdgiRadianceShHashAdd(hash, update.flags);
    hash = SimpleDdgiRadianceShHashAdd(hash, slotGeneration);
    hash = SimpleDdgiRadianceShHashAdd(hash, rayCount);
    hash = SimpleDdgiRadianceShHashAdd(hash, mode);
    return SimpleDdgiRadianceShFinishHash(hash);
}

bool SimpleDdgiDirectionalParityRequired(SimpleDdgiParams params)
{
    uint glossyMode = SimpleDdgiGlossyTransportMode(params.residencyFlags);
    return glossyMode == SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_ONE_BOUNCE ||
        glossyMode ==
            SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_RECURSIVE_EXPERIMENTAL;
}

// A fast publication-header check is sufficient for deciding whether work is
// needed. The publish phase still performs the complete checksum validation;
// a bad existing payload therefore fails closed and is retried as fresh.
bool SimpleDdgiDirectionalRecordHeaderIsPublished(
    uint bufferIndex,
    uint physicalProbeIndex,
    uint mode,
    uint expectedSlotGeneration)
{
    uint recordWords = SimpleDdgiRadianceShRecordWords(mode);
    if (recordWords == 0u || expectedSlotGeneration == 0u)
        return false;

    uint baseWord = physicalProbeIndex * recordWords;
    uint generationWord = recordWords - 2u;
    uint metadataWord = recordWords - 1u;
    uint metadata = ReadStorageWordUniform(
        bufferIndex,
        baseWord + metadataWord);
    if ((metadata & SIMPLE_DDGI_RADIANCE_SH_VALID_BIT) == 0u ||
        ((metadata & SIMPLE_DDGI_RADIANCE_SH_VERSION_MASK) >>
            SIMPLE_DDGI_RADIANCE_SH_VERSION_SHIFT) !=
                SimpleDdgiRadianceShRepresentationVersion(mode))
    {
        return false;
    }

    uint slotGeneration = ReadStorageWordUniform(
        bufferIndex,
        baseWord + generationWord);
    uint metadataAfter = ReadStorageWordUniform(
        bufferIndex,
        baseWord + metadataWord);
    return metadataAfter == metadata &&
        slotGeneration == expectedSlotGeneration;
}

#endif
