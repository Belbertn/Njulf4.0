#ifndef NJULF_DDGI_RECEIVER_FEEDBACK_ABI_GLSL
#define NJULF_DDGI_RECEIVER_FEEDBACK_ABI_GLSL

#include "ddgi_receiver_feedback_source_abi.glsl"

// B1 exact receiver-feedback capture/sort/reduce ABI. Keep synchronized with
// SimpleDdgiReceiverFeedbackGpuSortContracts.cs. The append record remains the
// frozen 32-byte V2 record; corrected mass and requested page are transient
// sidecars because widening/reinterpreting the record would invalidate every
// recorded layout revision.

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_GPU_SORT_ABI_VERSION = 0xb1011004u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_LAYOUT_REVISION = 0xb1010002u;

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS = 8u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_WORDS = 16u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_LOCATOR_WORDS = 2u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_WORDS = 8u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS = 4u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_WORKGROUP_SIZE = 256u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RADIX_BIN_COUNT = 256u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_MAX_RECORD_CAPACITY = 252645000u;


const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_LAYOUT_REVISION = 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_ENDIAN_SENTINEL = 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FEEDBACK_GENERATION = 2u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_VIEWPORT_GENERATION = 3u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FRAME_SERIAL_LOW = 4u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FRAME_SERIAL_HIGH = 5u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_APPEND_COUNT = 6u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_DROPPED_COUNT = 7u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_PRODUCER_OVERFLOW_MASK = 8u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_RECORD_CAPACITY = 9u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_PROBE_PARTIAL_COUNT = 10u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FALLBACK_PARTIAL_COUNT = 11u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_SUMMARY_COUNT = 12u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FALLBACK_SUMMARY_COUNT = 13u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_INVALID_RECORD_COUNT = 14u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FLAGS = 15u;

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_VALIDATED = 1u << 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_APPEND_OVERFLOW = 1u << 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_PRODUCER_RANGE_OVERFLOW = 1u << 2u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_NONFINITE_INPUT = 1u << 3u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_SORT_OR_REDUCE_FAILURE = 1u << 4u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_FAILURE_MASK =
    SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_APPEND_OVERFLOW |
    SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_PRODUCER_RANGE_OVERFLOW |
    SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_NONFINITE_INPUT |
    SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_SORT_OR_REDUCE_FAILURE;

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_VALIDATED = 1u << 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_FALLBACK_COUNT_OVERFLOW = 1u << 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_NONFINITE_INPUT_REJECTED = 1u << 2u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_GENERATION_MISMATCH = 1u << 3u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_PRODUCER_RANGE_OVERFLOW = 1u << 4u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_APPEND_OVERFLOW = 1u << 5u;

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_RESET = 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_CAPTURE = 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_RADIX_HISTOGRAM = 2u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_RADIX_PREFIX = 3u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_RADIX_SCATTER = 4u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_BUILD_PARTIALS = 5u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_REDUCE_PROBE_SUMMARIES = 6u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_REDUCE_FALLBACK_PRESSURE = 7u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_FINALIZE = 8u;

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS = 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_PROBE_PARTIALS = 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_FALLBACK_PARTIALS = 2u;

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK = 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_SCRATCH_TEMPORARY = 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_SCRATCH_FALLBACK = 2u;

const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_INPUT_RAW_AUXILIARY_BANK_B = 1u << 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_OUTPUT_RAW_AUXILIARY_BANK_B = 1u << 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_MASK =
    SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_INPUT_RAW_AUXILIARY_BANK_B |
    SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_OUTPUT_RAW_AUXILIARY_BANK_B;

// Raw sorting retains every exact identity that is needed by either the
// resolved-owner or requested-owner reduction. Requested page is in a
// transient sidecar, but is still a real secondary key rather than a sketch.
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RAW_KEY_WORD_COUNT = 7u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RAW_RADIX_PASS_COUNT = 28u;
// Parallel block compaction does not promise reservation order. The probe
// partial stream therefore sorts the exact [resolved probe, first tile, last
// tile] span before merging tile boundaries.
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PROBE_PARTIAL_RADIX_PASS_COUNT = 12u;
// Mass is an exact finite FP32 bit-pattern tie breaker after requested owner
// and page. This makes fallback accumulation independent of atomic append
// order for unequal contributions.
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_RADIX_PASS_COUNT = 12u;

// Raw record words match GPUSimpleDdgiReceiverContributionRecordV2 exactly.
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_REQUESTED_PROBE = 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_RESOLVED_PROBE = 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_RESOLVED_PAGE = 2u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_EXACT_TILE = 3u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_INTERPOLATION_WEIGHT = 4u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_INVERSE_INCLUSION = 5u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_PACKED_ROLE_PAGE_GENERATION = 6u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_FEEDBACK_GENERATION = 7u;

// Probe partial words stored in the temporary record region after raw radix
// completes: resolved ID, first/last exact tile, mass, max weight, tile count,
// sample count, and packed consumer/fallback count.
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_RESOLVED_PROBE = 0u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_FIRST_TILE = 1u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_LAST_TILE = 2u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_MASS = 3u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_MAX_WEIGHT = 4u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_UNIQUE_TILES = 5u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_SAMPLE_COUNT = 6u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_CONSUMER_AND_FALLBACK = 7u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_CONSUMER_MASK = 0x000000ffu;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_FALLBACK_SHIFT = 8u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_FALLBACK_MASK = 0x7fffff00u;
const uint SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_FALLBACK_OVERFLOW = 1u << 31u;

layout(push_constant) uniform SimpleDdgiReceiverFeedbackGpuSortPushBlock
{
    uint abiVersion;
    uint operation;
    uint recordCapacity;
    uint summaryCapacity;
    uint fallbackCapacity;
    uint feedbackGeneration;
    uint viewportGeneration;
    uint frameSerialLow;
    uint frameSerialHigh;
    uint recordBankIndex;
    uint summaryBankIndex;
    uint summaryBankStrideWords;
    uint inputCount;
    uint inputKind;
    uint inputLocation;
    uint outputLocation;
    uint radixByteShift;
    uint radixPassIndex;
    uint captureSourceBufferIndex;
    uint captureSourceRecordOffsetWords;
    uint captureSourceRecordCount;
    uint captureSourceControlOffsetWords;
    uint flags;
    uint reserved0;
} receiverFeedbackPc;

uint SimpleDdgiReceiverFeedbackRecordsBuffer()
{
    return uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORDS_BUFFER_INDEX);
}

uint SimpleDdgiReceiverFeedbackScratchBuffer()
{
    return uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_SORT_SCRATCH_BUFFER_INDEX);
}

uint SimpleDdgiReceiverFeedbackSummaryBuffer()
{
    return uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_BUFFER_INDEX);
}

bool SimpleDdgiReceiverFeedbackFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

uint SimpleDdgiReceiverFeedbackDivideRoundUp(uint value, uint divisor)
{
    return value == 0u ? 0u : 1u + (value - 1u) / divisor;
}

uint SimpleDdgiReceiverFeedbackRecordBankBaseWord()
{
    return receiverFeedbackPc.recordBankIndex *
        receiverFeedbackPc.recordCapacity *
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS;
}

uint SimpleDdgiReceiverFeedbackSummaryBankBaseWord()
{
    return receiverFeedbackPc.summaryBankIndex *
        receiverFeedbackPc.summaryBankStrideWords;
}

uint SimpleDdgiReceiverFeedbackHeaderWord(uint word)
{
    return SimpleDdgiReceiverFeedbackSummaryBankBaseWord() + word;
}

uint SimpleDdgiReceiverFeedbackTemporaryRecordOffsetWords()
{
    return 0u;
}

uint SimpleDdgiReceiverFeedbackRawMassAOffsetWords()
{
    return receiverFeedbackPc.recordCapacity *
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS;
}

uint SimpleDdgiReceiverFeedbackRawMassBOffsetWords()
{
    return SimpleDdgiReceiverFeedbackRawMassAOffsetWords() +
        receiverFeedbackPc.recordCapacity;
}

uint SimpleDdgiReceiverFeedbackRequestedPageAOffsetWords()
{
    return SimpleDdgiReceiverFeedbackRawMassBOffsetWords() +
        receiverFeedbackPc.recordCapacity;
}

uint SimpleDdgiReceiverFeedbackRequestedPageBOffsetWords()
{
    return SimpleDdgiReceiverFeedbackRequestedPageAOffsetWords() +
        receiverFeedbackPc.recordCapacity;
}

uint SimpleDdgiReceiverFeedbackFallbackPartialOffsetWords()
{
    return SimpleDdgiReceiverFeedbackRequestedPageBOffsetWords() +
        receiverFeedbackPc.recordCapacity;
}

uint SimpleDdgiReceiverFeedbackRadixPrefixOffsetWords()
{
    return SimpleDdgiReceiverFeedbackFallbackPartialOffsetWords() +
        receiverFeedbackPc.recordCapacity *
            SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS;
}

uint SimpleDdgiReceiverFeedbackRadixBaseOffsetWords()
{
    return SimpleDdgiReceiverFeedbackRadixPrefixOffsetWords() +
        SimpleDdgiReceiverFeedbackDivideRoundUp(
            receiverFeedbackPc.recordCapacity,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_WORKGROUP_SIZE) *
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RADIX_BIN_COUNT;
}

uint SimpleDdgiReceiverFeedbackScratchRequiredWords()
{
    return SimpleDdgiReceiverFeedbackRadixBaseOffsetWords() +
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RADIX_BIN_COUNT;
}

uint SimpleDdgiReceiverFeedbackSummaryLocatorOffsetWords()
{
    return SimpleDdgiReceiverFeedbackSummaryBankBaseWord() +
        SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_WORDS;
}

uint SimpleDdgiReceiverFeedbackSummaryRecordOffsetWords()
{
    return SimpleDdgiReceiverFeedbackSummaryLocatorOffsetWords() +
        receiverFeedbackPc.summaryCapacity *
            SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_LOCATOR_WORDS;
}

uint SimpleDdgiReceiverFeedbackFallbackPressureOffsetWords()
{
    return SimpleDdgiReceiverFeedbackSummaryRecordOffsetWords() +
        receiverFeedbackPc.summaryCapacity *
            SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_WORDS;
}

uint SimpleDdgiReceiverFeedbackRequiredSummaryBankWords()
{
    return SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_WORDS +
        receiverFeedbackPc.summaryCapacity *
            (SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_LOCATOR_WORDS +
                SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_WORDS) +
        receiverFeedbackPc.fallbackCapacity *
            SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS;
}

bool SimpleDdgiReceiverFeedbackTryRequiredSummaryBankWords(out uint requiredWords)
{
    requiredWords = 0u;
    // Do not permit a malformed push block to wrap its computed partition
    // before the descriptor-length checks below.
    if (receiverFeedbackPc.summaryCapacity >
        (0xffffffffu - SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_WORDS) /
            (SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_LOCATOR_WORDS +
                SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_WORDS))
    {
        return false;
    }
    uint summaryWords = SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_WORDS +
        receiverFeedbackPc.summaryCapacity *
            (SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_LOCATOR_WORDS +
                SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_WORDS);
    if (receiverFeedbackPc.fallbackCapacity >
        (0xffffffffu - summaryWords) /
            SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS)
    {
        return false;
    }
    requiredWords = summaryWords + receiverFeedbackPc.fallbackCapacity *
        SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS;
    return true;
}

bool SimpleDdgiReceiverFeedbackLayoutIsSane()
{
    if (receiverFeedbackPc.abiVersion !=
            SIMPLE_DDGI_RECEIVER_FEEDBACK_GPU_SORT_ABI_VERSION ||
        receiverFeedbackPc.recordCapacity == 0u ||
        receiverFeedbackPc.summaryCapacity == 0u ||
        receiverFeedbackPc.fallbackCapacity < receiverFeedbackPc.recordCapacity ||
        receiverFeedbackPc.recordBankIndex > 1u ||
        receiverFeedbackPc.summaryBankIndex > 1u ||
        receiverFeedbackPc.operation >
            SIMPLE_DDGI_RECEIVER_FEEDBACK_OPERATION_FINALIZE ||
        receiverFeedbackPc.inputKind >
            SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_FALLBACK_PARTIALS ||
        receiverFeedbackPc.inputLocation >
            SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_SCRATCH_FALLBACK ||
        receiverFeedbackPc.outputLocation >
            SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_SCRATCH_FALLBACK ||
        (receiverFeedbackPc.flags &
            ~SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_MASK) != 0u ||
        // Keep all derived u32 word arithmetic below its overflow range.
        receiverFeedbackPc.recordCapacity >
            SIMPLE_DDGI_RECEIVER_FEEDBACK_MAX_RECORD_CAPACITY)
    {
        return false;
    }

    uint recordWords = receiverFeedbackPc.recordCapacity *
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS;
    uint recordBase = SimpleDdgiReceiverFeedbackRecordBankBaseWord();
    uint summaryBase = SimpleDdgiReceiverFeedbackSummaryBankBaseWord();
    uint requiredSummaryWords;
    if (!SimpleDdgiReceiverFeedbackTryRequiredSummaryBankWords(requiredSummaryWords) ||
        receiverFeedbackPc.summaryBankStrideWords < requiredSummaryWords ||
        recordBase > uint(BindlessStorageBuffers[
            SimpleDdgiReceiverFeedbackRecordsBuffer()].Words.length()) ||
        recordWords > uint(BindlessStorageBuffers[
            SimpleDdgiReceiverFeedbackRecordsBuffer()].Words.length()) - recordBase ||
        summaryBase > uint(BindlessStorageBuffers[
            SimpleDdgiReceiverFeedbackSummaryBuffer()].Words.length()) ||
        requiredSummaryWords > uint(BindlessStorageBuffers[
            SimpleDdgiReceiverFeedbackSummaryBuffer()].Words.length()) - summaryBase ||
        SimpleDdgiReceiverFeedbackScratchRequiredWords() >
            uint(BindlessStorageBuffers[
                SimpleDdgiReceiverFeedbackScratchBuffer()].Words.length()))
    {
        return false;
    }
    return true;
}

uint SimpleDdgiReceiverFeedbackReadHeader(uint word)
{
    return ReadStorageWordUniform(
        SimpleDdgiReceiverFeedbackSummaryBuffer(),
        SimpleDdgiReceiverFeedbackHeaderWord(word));
}

void SimpleDdgiReceiverFeedbackWriteHeader(uint word, uint value)
{
    WriteStorageWordUniform(
        SimpleDdgiReceiverFeedbackSummaryBuffer(),
        SimpleDdgiReceiverFeedbackHeaderWord(word), value);
}

void SimpleDdgiReceiverFeedbackMarkBankFailure(uint failureFlag)
{
    atomicOr(BindlessStorageBuffers[
        SimpleDdgiReceiverFeedbackSummaryBuffer()].Words[
            SimpleDdgiReceiverFeedbackHeaderWord(
                SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FLAGS)], failureFlag);
}

void SimpleDdgiReceiverFeedbackMarkProducerOverflow(uint producer)
{
    // Bits 0..6 name the frozen producer enum. Bit 31 is a deliberately
    // non-producer sentinel for a malformed source/range that cannot be
    // attributed safely. Any nonzero mask invalidates the complete bank.
    uint producerBit = producer <= 6u ? (1u << producer) : 0x80000000u;
    atomicOr(BindlessStorageBuffers[
        SimpleDdgiReceiverFeedbackSummaryBuffer()].Words[
            SimpleDdgiReceiverFeedbackHeaderWord(
                SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_PRODUCER_OVERFLOW_MASK)],
        producerBit);
    SimpleDdgiReceiverFeedbackMarkBankFailure(
        SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_PRODUCER_RANGE_OVERFLOW);
}

void SimpleDdgiReceiverFeedbackMarkInvalidInput(bool producerRangeFailure)
{
    atomicAdd(BindlessStorageBuffers[
        SimpleDdgiReceiverFeedbackSummaryBuffer()].Words[
            SimpleDdgiReceiverFeedbackHeaderWord(
                SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_DROPPED_COUNT)], 1u);
    atomicAdd(BindlessStorageBuffers[
        SimpleDdgiReceiverFeedbackSummaryBuffer()].Words[
            SimpleDdgiReceiverFeedbackHeaderWord(
                SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_INVALID_RECORD_COUNT)], 1u);
    SimpleDdgiReceiverFeedbackMarkBankFailure(
        producerRangeFailure
            ? SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_PRODUCER_RANGE_OVERFLOW
            : SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_NONFINITE_INPUT);
}

bool SimpleDdgiReceiverFeedbackHeaderMatchesWriteTransaction()
{
    return SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_LAYOUT_REVISION) ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_LAYOUT_REVISION &&
        SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_ENDIAN_SENTINEL) ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_ENDIAN_SENTINEL &&
        SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FEEDBACK_GENERATION) ==
                receiverFeedbackPc.feedbackGeneration &&
        receiverFeedbackPc.feedbackGeneration != 0u &&
        SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_VIEWPORT_GENERATION) ==
                receiverFeedbackPc.viewportGeneration &&
        receiverFeedbackPc.viewportGeneration != 0u &&
        SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FRAME_SERIAL_LOW) ==
                receiverFeedbackPc.frameSerialLow &&
        SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FRAME_SERIAL_HIGH) ==
                receiverFeedbackPc.frameSerialHigh &&
        SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_RECORD_CAPACITY) ==
                receiverFeedbackPc.recordCapacity;
}

bool SimpleDdgiReceiverFeedbackBankCanProcess()
{
    return SimpleDdgiReceiverFeedbackLayoutIsSane() &&
        SimpleDdgiReceiverFeedbackHeaderMatchesWriteTransaction() &&
        (SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FLAGS) &
            SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_FAILURE_MASK) == 0u;
}

uint SimpleDdgiReceiverFeedbackInputCapacity()
{
    // One partial of either kind can be emitted per admitted raw record. The
    // persistent fallback output may be larger, but the sort input cannot.
    return receiverFeedbackPc.recordCapacity;
}

uint SimpleDdgiReceiverFeedbackInputCount()
{
    if (receiverFeedbackPc.inputCount != 0u)
        return receiverFeedbackPc.inputCount;
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS)
    {
        return SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_APPEND_COUNT);
    }
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_PROBE_PARTIALS)
    {
        return SimpleDdgiReceiverFeedbackReadHeader(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_PROBE_PARTIAL_COUNT);
    }
    return SimpleDdgiReceiverFeedbackReadHeader(
        SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FALLBACK_PARTIAL_COUNT);
}

uint SimpleDdgiReceiverFeedbackRadixPassCount()
{
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS)
    {
        return SIMPLE_DDGI_RECEIVER_FEEDBACK_RAW_RADIX_PASS_COUNT;
    }
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_PROBE_PARTIALS)
    {
        return SIMPLE_DDGI_RECEIVER_FEEDBACK_PROBE_PARTIAL_RADIX_PASS_COUNT;
    }
    return SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_RADIX_PASS_COUNT;
}

bool SimpleDdgiReceiverFeedbackRadixConfigurationIsValid()
{
    bool evenPass = (receiverFeedbackPc.radixPassIndex & 1u) == 0u;
    uint initialLocation = receiverFeedbackPc.inputKind ==
            SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS
        ? SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK
        : receiverFeedbackPc.inputKind ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_PROBE_PARTIALS
            ? SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_SCRATCH_TEMPORARY
            : SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_SCRATCH_FALLBACK;
    uint alternateLocation = receiverFeedbackPc.inputKind ==
            SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS
        ? SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_SCRATCH_TEMPORARY
        : SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK;
    uint expectedInputLocation = evenPass
        ? initialLocation
        : alternateLocation;
    uint expectedOutputLocation = evenPass
        ? alternateLocation
        : initialLocation;
    uint expectedFlags = receiverFeedbackPc.inputKind ==
            SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS
        ? evenPass
            ? SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_OUTPUT_RAW_AUXILIARY_BANK_B
            : SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_INPUT_RAW_AUXILIARY_BANK_B
        : 0u;
    return SimpleDdgiReceiverFeedbackBankCanProcess() &&
        receiverFeedbackPc.radixPassIndex <
            SimpleDdgiReceiverFeedbackRadixPassCount() &&
        receiverFeedbackPc.inputLocation == expectedInputLocation &&
        receiverFeedbackPc.outputLocation == expectedOutputLocation &&
        receiverFeedbackPc.flags == expectedFlags &&
        receiverFeedbackPc.radixByteShift ==
            (receiverFeedbackPc.radixPassIndex & 3u) * 8u &&
        SimpleDdgiReceiverFeedbackInputCount() <=
            SimpleDdgiReceiverFeedbackInputCapacity();
}

uint SimpleDdgiReceiverFeedbackRawRecordWord(
    uint location,
    uint recordIndex,
    uint word)
{
    if (location == SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK)
    {
        return ReadStorageWordUniform(
            SimpleDdgiReceiverFeedbackRecordsBuffer(),
            SimpleDdgiReceiverFeedbackRecordBankBaseWord() +
                recordIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word);
    }
    return ReadStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackTemporaryRecordOffsetWords() +
            recordIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word);
}

void SimpleDdgiReceiverFeedbackWriteRawRecordWord(
    uint location,
    uint recordIndex,
    uint word,
    uint value)
{
    if (location == SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK)
    {
        WriteStorageWordUniform(
            SimpleDdgiReceiverFeedbackRecordsBuffer(),
            SimpleDdgiReceiverFeedbackRecordBankBaseWord() +
                recordIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word,
            value);
        return;
    }
    WriteStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackTemporaryRecordOffsetWords() +
            recordIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word,
        value);
}

uint SimpleDdgiReceiverFeedbackProbePartialWord(
    uint location,
    uint partialIndex,
    uint word)
{
    if (location == SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK)
    {
        return ReadStorageWordUniform(
            SimpleDdgiReceiverFeedbackRecordsBuffer(),
            SimpleDdgiReceiverFeedbackRecordBankBaseWord() +
                partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word);
    }
    return ReadStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackTemporaryRecordOffsetWords() +
            partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word);
}

void SimpleDdgiReceiverFeedbackWriteProbePartialWord(
    uint location,
    uint partialIndex,
    uint word,
    uint value)
{
    if (location == SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK)
    {
        WriteStorageWordUniform(
            SimpleDdgiReceiverFeedbackRecordsBuffer(),
            SimpleDdgiReceiverFeedbackRecordBankBaseWord() +
                partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word,
            value);
        return;
    }
    WriteStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackTemporaryRecordOffsetWords() +
            partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS + word,
        value);
}

uint SimpleDdgiReceiverFeedbackFallbackPartialWord(
    uint location,
    uint partialIndex,
    uint word)
{
    if (location == SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK)
    {
        return ReadStorageWordUniform(
            SimpleDdgiReceiverFeedbackRecordsBuffer(),
            SimpleDdgiReceiverFeedbackRecordBankBaseWord() +
                partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS + word);
    }
    return ReadStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackFallbackPartialOffsetWords() +
            partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS + word);
}

void SimpleDdgiReceiverFeedbackWriteFallbackPartialWord(
    uint location,
    uint partialIndex,
    uint word,
    uint value)
{
    if (location == SIMPLE_DDGI_RECEIVER_FEEDBACK_LOCATION_RECORD_BANK)
    {
        WriteStorageWordUniform(
            SimpleDdgiReceiverFeedbackRecordsBuffer(),
            SimpleDdgiReceiverFeedbackRecordBankBaseWord() +
                partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS + word,
            value);
        return;
    }
    WriteStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackFallbackPartialOffsetWords() +
            partialIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS + word,
        value);
}

uint SimpleDdgiReceiverFeedbackReadInputWord(uint itemIndex, uint word)
{
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS)
    {
        return SimpleDdgiReceiverFeedbackRawRecordWord(
            receiverFeedbackPc.inputLocation, itemIndex, word);
    }
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_PROBE_PARTIALS)
    {
        return SimpleDdgiReceiverFeedbackProbePartialWord(
            receiverFeedbackPc.inputLocation, itemIndex, word);
    }
    return SimpleDdgiReceiverFeedbackFallbackPartialWord(
        receiverFeedbackPc.inputLocation, itemIndex, word);
}

void SimpleDdgiReceiverFeedbackWriteOutputWord(
    uint itemIndex,
    uint word,
    uint value)
{
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS)
    {
        SimpleDdgiReceiverFeedbackWriteRawRecordWord(
            receiverFeedbackPc.outputLocation, itemIndex, word, value);
        return;
    }
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_PROBE_PARTIALS)
    {
        SimpleDdgiReceiverFeedbackWriteProbePartialWord(
            receiverFeedbackPc.outputLocation, itemIndex, word, value);
        return;
    }
    SimpleDdgiReceiverFeedbackWriteFallbackPartialWord(
        receiverFeedbackPc.outputLocation, itemIndex, word, value);
}

uint SimpleDdgiReceiverFeedbackInputWordCount()
{
    return receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_FALLBACK_PARTIALS
        ? SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS
        : SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_WORDS;
}

uint SimpleDdgiReceiverFeedbackRawMassOffset(bool bankB)
{
    return bankB
        ? SimpleDdgiReceiverFeedbackRawMassBOffsetWords()
        : SimpleDdgiReceiverFeedbackRawMassAOffsetWords();
}

uint SimpleDdgiReceiverFeedbackRequestedPageOffset(bool bankB)
{
    return bankB
        ? SimpleDdgiReceiverFeedbackRequestedPageBOffsetWords()
        : SimpleDdgiReceiverFeedbackRequestedPageAOffsetWords();
}

float SimpleDdgiReceiverFeedbackReadInputRawMass(uint recordIndex)
{
    bool bankB = (receiverFeedbackPc.flags &
        SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_INPUT_RAW_AUXILIARY_BANK_B) != 0u;
    return uintBitsToFloat(ReadStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackRawMassOffset(bankB) + recordIndex));
}

uint SimpleDdgiReceiverFeedbackReadInputRequestedPage(uint recordIndex)
{
    bool bankB = (receiverFeedbackPc.flags &
        SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_INPUT_RAW_AUXILIARY_BANK_B) != 0u;
    return ReadStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackRequestedPageOffset(bankB) + recordIndex);
}

void SimpleDdgiReceiverFeedbackWriteOutputRawAuxiliary(
    uint recordIndex,
    float correctedMass,
    uint requestedPage)
{
    bool bankB = (receiverFeedbackPc.flags &
        SIMPLE_DDGI_RECEIVER_FEEDBACK_FLAG_OUTPUT_RAW_AUXILIARY_BANK_B) != 0u;
    WriteStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackRawMassOffset(bankB) + recordIndex,
        floatBitsToUint(correctedMass));
    WriteStorageWordUniform(
        SimpleDdgiReceiverFeedbackScratchBuffer(),
        SimpleDdgiReceiverFeedbackRequestedPageOffset(bankB) + recordIndex,
        requestedPage);
}

uint SimpleDdgiReceiverFeedbackRawSortKeyWord(uint itemIndex, uint keyWord)
{
    if (keyWord == 0u)
    {
        return SimpleDdgiReceiverFeedbackRawRecordWord(
            receiverFeedbackPc.inputLocation, itemIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_FEEDBACK_GENERATION);
    }
    if (keyWord == 1u)
    {
        return SimpleDdgiReceiverFeedbackRawRecordWord(
            receiverFeedbackPc.inputLocation, itemIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_PACKED_ROLE_PAGE_GENERATION);
    }
    if (keyWord == 2u)
    {
        return SimpleDdgiReceiverFeedbackReadInputRequestedPage(itemIndex);
    }
    if (keyWord == 3u)
    {
        return SimpleDdgiReceiverFeedbackRawRecordWord(
            receiverFeedbackPc.inputLocation, itemIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_RESOLVED_PAGE);
    }
    if (keyWord == 4u)
    {
        return SimpleDdgiReceiverFeedbackRawRecordWord(
            receiverFeedbackPc.inputLocation, itemIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_REQUESTED_PROBE);
    }
    if (keyWord == 5u)
    {
        return SimpleDdgiReceiverFeedbackRawRecordWord(
            receiverFeedbackPc.inputLocation, itemIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_EXACT_TILE);
    }
    return SimpleDdgiReceiverFeedbackRawRecordWord(
        receiverFeedbackPc.inputLocation, itemIndex,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_RESOLVED_PROBE);
}

uint SimpleDdgiReceiverFeedbackRadixKey(uint itemIndex)
{
    uint keyWord = receiverFeedbackPc.radixPassIndex >> 2u;
    uint key = 0u;
    if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_RAW_RECORDS)
    {
        key = SimpleDdgiReceiverFeedbackRawSortKeyWord(itemIndex, keyWord);
    }
    else if (receiverFeedbackPc.inputKind ==
        SIMPLE_DDGI_RECEIVER_FEEDBACK_INPUT_PROBE_PARTIALS)
    {
        // LSD order: last tile, first tile, then resolved probe. This repairs
        // the nondeterministic order in which independent raw blocks reserve
        // their partial slots and makes the boundary de-duplication below
        // exact without relying on append timing.
        key = SimpleDdgiReceiverFeedbackProbePartialWord(
            receiverFeedbackPc.inputLocation, itemIndex,
            keyWord == 0u
                ? SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_LAST_TILE
                : keyWord == 1u
                    ? SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_FIRST_TILE
                    : SIMPLE_DDGI_RECEIVER_FEEDBACK_PARTIAL_RESOLVED_PROBE);
    }
    else
    {
        // Corrected mass is the low tie breaker, requested page the middle
        // key, and requested probe the high key. The complete requested
        // owner/page identity remains exact rather than a hash/sketch.
        key = SimpleDdgiReceiverFeedbackFallbackPartialWord(
            receiverFeedbackPc.inputLocation, itemIndex,
            keyWord == 0u ? 2u : keyWord == 1u ? 1u : 0u);
    }
    return (key >> receiverFeedbackPc.radixByteShift) & 0xffu;
}

bool SimpleDdgiReceiverFeedbackTryCorrectedMass(
    float physicalReceiverContribution,
    float interpolationWeight,
    float inverseInclusionProbability,
    out float correctedMass)
{
    correctedMass = 0.0;
    if (!SimpleDdgiReceiverFeedbackFinite(physicalReceiverContribution) ||
        physicalReceiverContribution < 0.0 ||
        !SimpleDdgiReceiverFeedbackFinite(interpolationWeight) ||
        interpolationWeight < 0.0 || interpolationWeight > 1.0 ||
        !SimpleDdgiReceiverFeedbackFinite(inverseInclusionProbability) ||
        inverseInclusionProbability < 1.0)
    {
        return false;
    }
    correctedMass = physicalReceiverContribution * interpolationWeight *
        inverseInclusionProbability;
    return SimpleDdgiReceiverFeedbackFinite(correctedMass) && correctedMass >= 0.0;
}

bool SimpleDdgiReceiverFeedbackRawRecordIsValid(uint location, uint recordIndex)
{
    uint packed = SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_PACKED_ROLE_PAGE_GENERATION);
    uint producer = packed & 0xfu;
    uint fallbackRole = (packed >> 4u) & 0xfu;
    uint pageGeneration = packed >> 8u;
    float interpolationWeight = uintBitsToFloat(
        SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_INTERPOLATION_WEIGHT));
    float inverseInclusionProbability = uintBitsToFloat(
        SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_INVERSE_INCLUSION));
    float correctedMass = SimpleDdgiReceiverFeedbackReadInputRawMass(recordIndex);
    return SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_FEEDBACK_GENERATION) ==
                receiverFeedbackPc.feedbackGeneration &&
        producer <= 6u && fallbackRole <= 3u && pageGeneration != 0u &&
        SimpleDdgiReceiverFeedbackFinite(interpolationWeight) &&
        interpolationWeight >= 0.0 && interpolationWeight <= 1.0 &&
        SimpleDdgiReceiverFeedbackFinite(inverseInclusionProbability) &&
        inverseInclusionProbability >= 1.0 &&
        SimpleDdgiReceiverFeedbackFinite(correctedMass) && correctedMass >= 0.0;
}

bool SimpleDdgiReceiverFeedbackIsFallback(uint location, uint recordIndex)
{
    uint requested = SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_REQUESTED_PROBE);
    uint resolved = SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_RESOLVED_PROBE);
    uint packed = SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_PACKED_ROLE_PAGE_GENERATION);
    return requested != resolved || ((packed >> 4u) & 0xfu) != 0u;
}

uint SimpleDdgiReceiverFeedbackProducerMask(uint location, uint recordIndex)
{
    uint packed = SimpleDdgiReceiverFeedbackRawRecordWord(location, recordIndex,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_PACKED_ROLE_PAGE_GENERATION);
    return 1u << (packed & 0xfu);
}

uint SimpleDdgiReceiverFeedbackSaturatingAdd(uint left, uint right, out bool overflow)
{
    uint result = left + right;
    overflow = result < left;
    return overflow ? 0xffffffffu : result;
}

void SimpleDdgiReceiverFeedbackWriteSummaryLocator(
    uint summaryIndex,
    uint resolvedVirtualProbeId)
{
    uint base = SimpleDdgiReceiverFeedbackSummaryLocatorOffsetWords() +
        summaryIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_LOCATOR_WORDS;
    WriteStorageWordUniform(SimpleDdgiReceiverFeedbackSummaryBuffer(), base + 0u,
        resolvedVirtualProbeId);
    WriteStorageWordUniform(SimpleDdgiReceiverFeedbackSummaryBuffer(), base + 1u,
        receiverFeedbackPc.feedbackGeneration);
}

void SimpleDdgiReceiverFeedbackWriteSummaryWord(
    uint summaryIndex,
    uint word,
    uint value)
{
    WriteStorageWordUniform(SimpleDdgiReceiverFeedbackSummaryBuffer(),
        SimpleDdgiReceiverFeedbackSummaryRecordOffsetWords() +
            summaryIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_WORDS + word,
        value);
}

void SimpleDdgiReceiverFeedbackWriteFallbackPressureWord(
    uint fallbackIndex,
    uint word,
    uint value)
{
    WriteStorageWordUniform(SimpleDdgiReceiverFeedbackSummaryBuffer(),
        SimpleDdgiReceiverFeedbackFallbackPressureOffsetWords() +
            fallbackIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_FALLBACK_WORDS + word,
        value);
}

#endif
