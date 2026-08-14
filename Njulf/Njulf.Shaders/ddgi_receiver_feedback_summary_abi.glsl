#ifndef NJULF_DDGI_RECEIVER_FEEDBACK_SUMMARY_ABI_GLSL
#define NJULF_DDGI_RECEIVER_FEEDBACK_SUMMARY_ABI_GLSL

// Read-only B1 summary ABI for the resident DDGI scheduler. Keep this small:
// the producer ABI owns a different push-constant block and therefore cannot
// be included by scheduler shaders. Constants mirror
// SimpleDdgiReceiverFeedbackGpuSortContracts.cs and
// SimpleDdgiReceiverFeedbackV2.cs.
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_LAYOUT_REVISION = 0xb1010002u;
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_ENDIAN_SENTINEL = 0x01020304u;
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_HEADER_WORDS = 16u;
const uint SIMPLE_DDGI_FEEDBACK_REFINEMENT_WITNESS_WORDS = 4u;
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_PREFIX_WORDS =
    SIMPLE_DDGI_FEEDBACK_SUMMARY_HEADER_WORDS +
    SIMPLE_DDGI_FEEDBACK_REFINEMENT_WITNESS_WORDS;
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_LOCATOR_WORDS = 2u;
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_RECORD_WORDS = 8u;
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_FALLBACK_WORDS = 4u;

const uint SIMPLE_DDGI_FEEDBACK_HEADER_LAYOUT = 0u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_ENDIAN = 1u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_GENERATION = 2u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_VIEWPORT_GENERATION = 3u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_FRAME_LOW = 4u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_FRAME_HIGH = 5u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_APPEND_COUNT = 6u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_DROPPED_COUNT = 7u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_PRODUCER_OVERFLOW = 8u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_RECORD_CAPACITY = 9u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_PROBE_PARTIAL_COUNT = 10u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_FALLBACK_PARTIAL_COUNT = 11u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_SUMMARY_COUNT = 12u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_FALLBACK_SUMMARY_COUNT = 13u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_INVALID_RECORD_COUNT = 14u;
const uint SIMPLE_DDGI_FEEDBACK_HEADER_FLAGS = 15u;

const uint SIMPLE_DDGI_FEEDBACK_BANK_VALIDATED = 1u;
const uint SIMPLE_DDGI_FEEDBACK_SUMMARY_VALIDATED = 1u;
const uint SIMPLE_DDGI_FEEDBACK_PRODUCER_MASK = 0x7fu;
const uint SIMPLE_DDGI_FEEDBACK_PRODUCER_REFLECTION_CAPTURE = 1u << 5u;

uint SimpleDdgiFeedbackSummaryRead(
    uint bufferIndex,
    uint bankBaseWord,
    uint relativeWord)
{
    return ReadStorageWordUniform(
        bufferIndex,
        bankBaseWord + relativeWord);
}

bool SimpleDdgiFeedbackSummaryTryValidateHeader(
    uint bufferIndex,
    uint bankBaseWord,
    uint bankStrideWords,
    uint recordCapacity,
    uint summaryCapacity,
    uint fallbackCapacity,
    uint expectedGeneration,
    uint expectedViewportGeneration,
    uint expectedFrameLow,
    uint expectedFrameHigh,
    out uint summaryCount)
{
    summaryCount = 0u;
    if (recordCapacity == 0u || summaryCapacity == 0u ||
        fallbackCapacity < recordCapacity || bankStrideWords == 0u ||
        expectedGeneration == 0u || expectedViewportGeneration == 0u)
    {
        return false;
    }

    // Prove every partition calculation before indexing the runtime array.
    const uint summaryWordsPerRecord =
        SIMPLE_DDGI_FEEDBACK_SUMMARY_LOCATOR_WORDS +
        SIMPLE_DDGI_FEEDBACK_SUMMARY_RECORD_WORDS;
    if (summaryCapacity >
            (0xffffffffu - SIMPLE_DDGI_FEEDBACK_SUMMARY_PREFIX_WORDS) /
                summaryWordsPerRecord)
    {
        return false;
    }
    uint requiredWords = SIMPLE_DDGI_FEEDBACK_SUMMARY_PREFIX_WORDS +
        summaryCapacity * summaryWordsPerRecord;
    if (fallbackCapacity >
            (0xffffffffu - requiredWords) /
                SIMPLE_DDGI_FEEDBACK_SUMMARY_FALLBACK_WORDS)
    {
        return false;
    }
    requiredWords += fallbackCapacity *
        SIMPLE_DDGI_FEEDBACK_SUMMARY_FALLBACK_WORDS;
    uint bufferWords = uint(BindlessStorageBuffers[
        nonuniformEXT(bufferIndex)].Words.length());
    if (requiredWords > bankStrideWords || bankBaseWord > bufferWords ||
        requiredWords > bufferWords - bankBaseWord)
    {
        return false;
    }

    uint appendCount = SimpleDdgiFeedbackSummaryRead(
        bufferIndex, bankBaseWord,
        SIMPLE_DDGI_FEEDBACK_HEADER_APPEND_COUNT);
    uint probePartialCount = SimpleDdgiFeedbackSummaryRead(
        bufferIndex, bankBaseWord,
        SIMPLE_DDGI_FEEDBACK_HEADER_PROBE_PARTIAL_COUNT);
    uint fallbackPartialCount = SimpleDdgiFeedbackSummaryRead(
        bufferIndex, bankBaseWord,
        SIMPLE_DDGI_FEEDBACK_HEADER_FALLBACK_PARTIAL_COUNT);
    uint candidateSummaryCount = SimpleDdgiFeedbackSummaryRead(
        bufferIndex, bankBaseWord,
        SIMPLE_DDGI_FEEDBACK_HEADER_SUMMARY_COUNT);
    uint fallbackSummaryCount = SimpleDdgiFeedbackSummaryRead(
        bufferIndex, bankBaseWord,
        SIMPLE_DDGI_FEEDBACK_HEADER_FALLBACK_SUMMARY_COUNT);

    if (SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_LAYOUT) !=
                SIMPLE_DDGI_FEEDBACK_SUMMARY_LAYOUT_REVISION ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_ENDIAN) !=
                SIMPLE_DDGI_FEEDBACK_SUMMARY_ENDIAN_SENTINEL ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_GENERATION) != expectedGeneration ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_VIEWPORT_GENERATION) !=
                expectedViewportGeneration ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_FRAME_LOW) != expectedFrameLow ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_FRAME_HIGH) != expectedFrameHigh ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_RECORD_CAPACITY) != recordCapacity ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_DROPPED_COUNT) != 0u ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_PRODUCER_OVERFLOW) != 0u ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_INVALID_RECORD_COUNT) != 0u ||
        SimpleDdgiFeedbackSummaryRead(bufferIndex, bankBaseWord,
            SIMPLE_DDGI_FEEDBACK_HEADER_FLAGS) !=
                SIMPLE_DDGI_FEEDBACK_BANK_VALIDATED ||
        appendCount > recordCapacity ||
        probePartialCount > appendCount ||
        fallbackPartialCount > appendCount ||
        candidateSummaryCount > probePartialCount ||
        candidateSummaryCount > summaryCapacity ||
        fallbackSummaryCount > fallbackPartialCount ||
        fallbackSummaryCount > fallbackCapacity)
    {
        return false;
    }

    summaryCount = candidateSummaryCount;
    return true;
}

bool SimpleDdgiFeedbackSummaryTryFindProbe(
    uint bufferIndex,
    uint bankBaseWord,
    uint summaryCapacity,
    uint summaryCount,
    uint expectedGeneration,
    uint probeIndex,
    out float contribution,
    out uint coverageAndFlags)
{
    contribution = 0.0;
    coverageAndFlags = 0u;
    uint low = 0u;
    uint high = summaryCount;
    // Summary locators are emitted in resolved-probe radix order. A fixed
    // 32-step lower-bound search covers every addressable u32 count without a
    // data-dependent unbounded loop.
    for (uint step = 0u; step < 32u && low < high; ++step)
    {
        uint middle = low + ((high - low) >> 1u);
        uint locatorWord = bankBaseWord +
            SIMPLE_DDGI_FEEDBACK_SUMMARY_PREFIX_WORDS +
            middle * SIMPLE_DDGI_FEEDBACK_SUMMARY_LOCATOR_WORDS;
        uint resolvedProbe = ReadStorageWordUniform(
            bufferIndex, locatorWord);
        if (resolvedProbe < probeIndex)
            low = middle + 1u;
        else
            high = middle;
    }
    if (low >= summaryCount)
        return false;

    uint locatorWord = bankBaseWord +
        SIMPLE_DDGI_FEEDBACK_SUMMARY_PREFIX_WORDS +
        low * SIMPLE_DDGI_FEEDBACK_SUMMARY_LOCATOR_WORDS;
    uint resolvedProbe = ReadStorageWordUniform(bufferIndex, locatorWord + 0u);
    uint locatorGeneration = ReadStorageWordUniform(
        bufferIndex, locatorWord + 1u);
    if (resolvedProbe != probeIndex ||
        locatorGeneration != expectedGeneration)
    {
        return false;
    }

    uint summaryWord = bankBaseWord +
        SIMPLE_DDGI_FEEDBACK_SUMMARY_PREFIX_WORDS +
        summaryCapacity * SIMPLE_DDGI_FEEDBACK_SUMMARY_LOCATOR_WORDS +
        low * SIMPLE_DDGI_FEEDBACK_SUMMARY_RECORD_WORDS;
    float mass = uintBitsToFloat(ReadStorageWordUniform(
        bufferIndex, summaryWord + 0u));
    float maximumWeight = uintBitsToFloat(ReadStorageWordUniform(
        bufferIndex, summaryWord + 1u));
    uint uniqueTileCount = ReadStorageWordUniform(
        bufferIndex, summaryWord + 2u);
    uint sampledReceiverCount = ReadStorageWordUniform(
        bufferIndex, summaryWord + 3u);
    uint consumerMask = ReadStorageWordUniform(
        bufferIndex, summaryWord + 4u);
    uint packedFallbackCounts = ReadStorageWordUniform(
        bufferIndex, summaryWord + 5u);
    uint summaryGeneration = ReadStorageWordUniform(
        bufferIndex, summaryWord + 6u);
    uint status = ReadStorageWordUniform(bufferIndex, summaryWord + 7u);
    if (isnan(mass) || isinf(mass) || mass < 0.0 ||
        isnan(maximumWeight) || isinf(maximumWeight) ||
        maximumWeight < 0.0 || maximumWeight > 1.0 ||
        uniqueTileCount > sampledReceiverCount ||
        (consumerMask & ~SIMPLE_DDGI_FEEDBACK_PRODUCER_MASK) != 0u ||
        summaryGeneration != expectedGeneration ||
        status != SIMPLE_DDGI_FEEDBACK_SUMMARY_VALIDATED)
    {
        return false;
    }

    float massScore = log2(1.0 + mass);
    float coverageScore = log2(1.0 + float(uniqueTileCount));
    float roleBias = packedFallbackCounts != 0u ? 0.25 : 0.0;
    if ((consumerMask &
            SIMPLE_DDGI_FEEDBACK_PRODUCER_REFLECTION_CAPTURE) != 0u)
    {
        roleBias += 0.5;
    }
    contribution = clamp(
        0.75 * massScore + 0.5 * coverageScore + roleBias,
        0.0,
        12.0);

    // Preserve the compact legacy classifier's role bits while the public
    // scheduler candidate ABI remains unchanged. These bits are derived from
    // exact V2 data; they are never used as ownership or eligibility gates.
    if (consumerMask != 0u)
        coverageAndFlags |= 1u << 24u;
    if (packedFallbackCounts != 0u)
        coverageAndFlags |= 1u << 25u;
    if ((consumerMask &
            SIMPLE_DDGI_FEEDBACK_PRODUCER_REFLECTION_CAPTURE) != 0u)
    {
        coverageAndFlags |= 1u << 31u;
    }
    return true;
}

#endif
