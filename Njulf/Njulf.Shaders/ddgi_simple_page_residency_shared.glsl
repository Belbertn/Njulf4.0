#ifndef NJULF_DDGI_SIMPLE_PAGE_RESIDENCY_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_PAGE_RESIDENCY_SHARED_GLSL

#include "common.glsl"
#include "ddgi_simple_scheduler_metadata_abi.glsl"
#define SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS 1
#define SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE 1
#include "ddgi_simple_shared.glsl"

layout(push_constant) uniform SimpleDdgiPageResidencyPushBlock
{
    uint ParamsBufferIndex;
    uint ProbeStateBufferIndex;
    uint RelocationClassificationBufferIndex;
    uint ReceiverProbeBufferIndex;
    uint TransportSourceCacheBufferIndex;
    uint SchedulerArenaBufferIndex;
    uint SchedulerProbeStateOffsetWords;
    uint SchedulerActiveProbeCount;
    uint CurrentFrame;
    uint DemandEpoch;
    uint ResourceGeneration;
    uint GeometryGeneration;
    uint SourceGeneration;
    uint CohortGeneration;
    uint CameraCut;
    uint Stage;
    uint Reserved0;
    uint Reserved1;
} pc;

uint ResidencyRead(SimpleDdgiParams params, uint word)
{
    return ReadStorageWordUniform(params.residencyArenaBufferIndex, word);
}

void ResidencyWrite(SimpleDdgiParams params, uint word, uint value)
{
    WriteStorageWordUniform(
        params.residencyArenaBufferIndex,
        word,
        value);
}

uint ResidencyAtomicAdd(
    SimpleDdgiParams params,
    uint word,
    uint value)
{
    return atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(
            params.residencyArenaBufferIndex)].Words[word],
        value);
}

uint ResidencyCounterWord(SimpleDdgiParams params, uint counter)
{
    return SimpleDdgiDemandCountersOffsetWords(params) + counter;
}

uint ResidencyTableBase(uint virtualPageIndex)
{
    return SIMPLE_DDGI_RESIDENCY_PAGE_TABLE_OFFSET_WORDS +
        virtualPageIndex * SIMPLE_DDGI_PAGE_TABLE_ENTRY_WORDS;
}

uint ResidencyHistoryBase(
    SimpleDdgiParams params,
    uint virtualPageIndex)
{
    return SimpleDdgiPageHistoryOffsetWords(params) +
        virtualPageIndex * SIMPLE_DDGI_PAGE_HISTORY_WORDS;
}

uint ResidencyPhysicalBase(
    SimpleDdgiParams params,
    uint physicalPageIndex)
{
    return SimpleDdgiPhysicalMetadataOffsetWords(params) +
        physicalPageIndex * SIMPLE_DDGI_PHYSICAL_PAGE_METADATA_WORDS;
}

uint ResidencyClassBase(
    SimpleDdgiParams params,
    uint virtualPageIndex)
{
    return SimpleDdgiClassificationScratchOffsetWords(params) +
        virtualPageIndex * 4u;
}

uint ResidencyAdvanceMappingGeneration(SimpleDdgiParams params)
{
    uint current = ResidencyRead(params, 3u);
    uint next = current + 1u;
    if (next == 0u || next >= 0xffff0000u)
        return 0u;
    ResidencyWrite(params, 3u, next);
    return next;
}

uint ResidencyAdvanceVirtualGeneration(uint flags)
{
    uint generation = (flags & SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK) >>
        SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT;
    generation = NextSimpleDdgiPhysicalGeneration(generation);
    return (flags & ~SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK) |
        (generation << SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT);
}

void ResidencyInvalidateVirtualProbe(
    SimpleDdgiParams params,
    uint virtualProbeIndex,
    bool remainNonResident)
{
    if (virtualProbeIndex >= params.probeCount)
        return;
    uint stateBase = virtualProbeIndex * SIMPLE_DDGI_PROBE_STATE_STRIDE_WORDS;
    uint flags = ReadStorageWordUniform(
        pc.ProbeStateBufferIndex,
        stateBase + 4u);
    flags = ResidencyAdvanceVirtualGeneration(flags);
    flags &= ~(SIMPLE_DDGI_PROBE_FLAG_INACTIVE |
        SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING |
        SIMPLE_DDGI_PROBE_FLAG_VISIBILITY_VALID);
    flags |= SIMPLE_DDGI_PROBE_FLAG_FRESH |
        SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID;
    if (remainNonResident)
        flags |= SIMPLE_DDGI_PROBE_FLAG_NON_RESIDENT;
    else
        flags &= ~SIMPLE_DDGI_PROBE_FLAG_NON_RESIDENT;
    WriteStorageVec4Uniform(
        pc.ProbeStateBufferIndex,
        stateBase,
        vec4(0.0));
    WriteStorageWordUniform(
        pc.ProbeStateBufferIndex,
        stateBase + 4u,
        flags);
    WriteStorageWordUniform(pc.ProbeStateBufferIndex, stateBase + 5u, 0u);
    WriteStorageWordUniform(
        pc.ProbeStateBufferIndex,
        stateBase + 6u,
        SIMPLE_DDGI_CLASSIFICATION_ACTIVE);
    WriteStorageWordUniform(
        pc.ProbeStateBufferIndex,
        stateBase + 7u,
        floatBitsToUint(1.0));
    if (pc.ReceiverProbeBufferIndex != 0u)
        WriteInvalidSimpleDdgiReceiverProbe(
            pc.ReceiverProbeBufferIndex,
            virtualProbeIndex);

    if (pc.RelocationClassificationBufferIndex != 0u)
    {
        uint relocationBase = virtualProbeIndex * 12u;
        for (uint word = 0u; word < 12u; word++)
            WriteStorageWordUniform(
                pc.RelocationClassificationBufferIndex,
                relocationBase + word,
                0u);
    }

    if (pc.SchedulerArenaBufferIndex != 0u &&
        virtualProbeIndex < pc.SchedulerActiveProbeCount)
    {
        uint schedulerBase = pc.SchedulerProbeStateOffsetWords +
            virtualProbeIndex * 10u;
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 0u,
            0u);
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 1u,
            0u);
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 2u,
            0u);
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 3u,
            0u);
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 5u,
            SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR);
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 7u,
            0u);
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 8u,
            0u);
        WriteStorageWordUniform(
            pc.SchedulerArenaBufferIndex,
            schedulerBase + 9u,
            0u);
    }
}

bool ResidencyPageIdentityValid(
    SimpleDdgiParams params,
    uint virtualPageIndex,
    uvec4 table,
    out uint physicalPageIndex)
{
    physicalPageIndex = 0xffffffffu;
    if (table.x == 0u || table.y == 0u ||
        (table.z & SIMPLE_DDGI_PAGE_TABLE_VALID) == 0u)
    {
        return false;
    }
    physicalPageIndex = table.x - 1u;
    if (physicalPageIndex >= params.sparsePhysicalPageCapacity)
        return false;
    uint reverseBase = ResidencyPhysicalBase(params, physicalPageIndex);
    return ResidencyRead(params, reverseBase + 0u) ==
            virtualPageIndex + 1u &&
        ResidencyRead(params, reverseBase + 1u) == table.y &&
        ResidencyRead(params, reverseBase + 2u) ==
            params.residencyResourceGeneration;
}

#endif
