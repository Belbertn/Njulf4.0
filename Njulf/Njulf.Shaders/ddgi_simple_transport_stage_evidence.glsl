#ifndef NJULF_DDGI_SIMPLE_TRANSPORT_STAGE_EVIDENCE_GLSL
#define NJULF_DDGI_SIMPLE_TRANSPORT_STAGE_EVIDENCE_GLSL

// Optional one-witness trace written into the reserved tail of the scheduler
// audit summary. The host arms it only after a failed exact audit has selected
// a concrete probe/texel. VERSION is always the final publication store, so a
// delayed readback cannot accept a partially written multiword record.
const uint SIMPLE_DDGI_TRANSPORT_STAGE_MAGIC = 0x44444749u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_VERSION = 3u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_BLEND = 1u << 0u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_PRIVATE_READBACK = 1u << 1u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_INTERMEDIATE_PUBLISH = 1u << 2u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_CANONICAL_READBACK = 1u << 3u;

const uint SIMPLE_DDGI_TRANSPORT_STAGE_VERSION_WORD = 0u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVE_EPOCH_WORD = 1u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_VIRTUAL_PROBE_WORD = 2u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_PHYSICAL_PROBE_WORD = 3u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TEXEL_WORD = 4u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SWEEP_WORD = 5u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_COLOR_WORD = 6u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_MASK_WORD = 7u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_PREVIOUS_R_WORD = 8u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_CANDIDATE_R_WORD = 11u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_BLENDED_R_WORD = 14u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_PRIVATE_R_WORD = 17u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_PUBLISH_INPUT_R_WORD = 20u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_CANONICAL_R_WORD = 23u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_WEIGHT_WORD = 26u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_ACCUMULATED_R_WORD = 27u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_RAY_COUNT_WORD = 30u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_GUIDING_BACKED_WORD = 31u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_GUIDING_VALID_WORD = 32u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_GUIDING_MAINTENANCE_WORD = 33u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_GUIDING_MIXTURE_WORD = 34u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_FIRST_RAY_R_WORD = 35u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_FIRST_DIRECTION_X_WORD = 38u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_SOLVER_FIRST_PDF_WORD = 41u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TRANSPORT_SOLVE_EPOCH_WORD = 60u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TRANSPORT_SOURCE_R_WORD = 61u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TRANSPORT_BOUNCE_R_WORD = 64u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TRANSPORT_TOTAL_R_WORD = 67u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TRANSPORT_GATHER_COUNT_WORD = 70u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TRANSPORT_OWNERSHIP_WORD = 71u;
const uint SIMPLE_DDGI_TRANSPORT_STAGE_TRANSPORT_FALLBACK_WORD = 72u;

bool SimpleDdgiTransportStageArmed()
{
    return pc.LightCount == SIMPLE_DDGI_TRANSPORT_STAGE_MAGIC &&
        pc.DirectionalLightCount == SIMPLE_DDGI_TRANSPORT_STAGE_VERSION &&
        pc.SchedulerArenaBufferIndex != 0u &&
        pc.SchedulerArenaBufferIndex != 0xffffffffu &&
        pc.FarFieldParamsBufferIndex != 0u &&
        pc.MaxShadedLights != 0xffffffffu &&
        pc.EmissiveSourceCount < 64u;
}

bool SimpleDdgiTransportStageMatchesWitness(
    uint virtualProbeIndex,
    uint texelIndex)
{
    return SimpleDdgiTransportStageArmed() &&
        virtualProbeIndex == pc.MaxShadedLights &&
        texelIndex == pc.EmissiveSourceCount;
}

uint SimpleDdgiTransportStageWord(uint relativeWord)
{
    return pc.LocalLightCount + relativeWord;
}

void SimpleDdgiTransportStageWrite(uint relativeWord, uint value)
{
    WriteStorageWordUniform(
        pc.SchedulerArenaBufferIndex,
        SimpleDdgiTransportStageWord(relativeWord),
        value);
}

uint SimpleDdgiTransportStageRead(uint relativeWord)
{
    return ReadStorageWordUniform(
        pc.SchedulerArenaBufferIndex,
        SimpleDdgiTransportStageWord(relativeWord));
}

void SimpleDdgiTransportStageWriteRgb(uint firstWord, vec3 value)
{
    SimpleDdgiTransportStageWrite(firstWord + 0u, floatBitsToUint(value.r));
    SimpleDdgiTransportStageWrite(firstWord + 1u, floatBitsToUint(value.g));
    SimpleDdgiTransportStageWrite(firstWord + 2u, floatBitsToUint(value.b));
}

void SimpleDdgiTransportStageBegin(
    uint virtualProbeIndex,
    uint physicalProbeIndex,
    uint texelIndex)
{
    // Invalidate a prior epoch before replacing any dependent word.
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_VERSION_WORD,
        0u);
    memoryBarrierBuffer();
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_SOLVE_EPOCH_WORD,
        pc.FarFieldParamsBufferIndex);
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_VIRTUAL_PROBE_WORD,
        virtualProbeIndex);
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_PHYSICAL_PROBE_WORD,
        physicalProbeIndex);
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_TEXEL_WORD,
        texelIndex);
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_SWEEP_WORD,
        pc.Flags >> SIMPLE_DDGI_SOLVE_SWEEP_INDEX_SHIFT);
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_COLOR_WORD,
        SimpleDdgiSolveTargetColor(pc.Flags));
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_MASK_WORD,
        0u);
}

bool SimpleDdgiTransportStageContextMatches(
    uint virtualProbeIndex,
    uint physicalProbeIndex,
    uint texelIndex)
{
    return SimpleDdgiTransportStageRead(
            SIMPLE_DDGI_TRANSPORT_STAGE_SOLVE_EPOCH_WORD) ==
                pc.FarFieldParamsBufferIndex &&
        SimpleDdgiTransportStageRead(
            SIMPLE_DDGI_TRANSPORT_STAGE_VIRTUAL_PROBE_WORD) ==
                virtualProbeIndex &&
        SimpleDdgiTransportStageRead(
            SIMPLE_DDGI_TRANSPORT_STAGE_PHYSICAL_PROBE_WORD) ==
                physicalProbeIndex &&
        SimpleDdgiTransportStageRead(
            SIMPLE_DDGI_TRANSPORT_STAGE_TEXEL_WORD) == texelIndex;
}

void SimpleDdgiTransportStagePublish(uint stageMask)
{
    memoryBarrierBuffer();
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_MASK_WORD,
        stageMask);
    memoryBarrierBuffer();
    SimpleDdgiTransportStageWrite(
        SIMPLE_DDGI_TRANSPORT_STAGE_VERSION_WORD,
        SIMPLE_DDGI_TRANSPORT_STAGE_VERSION);
}

#endif
