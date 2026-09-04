// Each fused commit workgroup owns one volume. It independently validates the
// bounded accepted set before any lane may mutate that volume, preserving the
// mandatory-scroll all-or-defer gate without cross-workgroup synchronization.

uint ScrollExposedProbeCount(uint volumeIndex)
{
    uint countX = max(SchedulerVolumeWord(volumeIndex, 16u), 1u);
    uint countY = max(SchedulerVolumeWord(volumeIndex, 17u), 1u);
    uint countZ = max(SchedulerVolumeWord(volumeIndex, 18u), 1u);
    int deltaX = int(SchedulerVolumeWord(volumeIndex, 36u));
    int deltaY = int(SchedulerVolumeWord(volumeIndex, 37u));
    int deltaZ = int(SchedulerVolumeWord(volumeIndex, 38u));
    uint overlapX = uint(max(int(countX) - abs(deltaX), 0));
    uint overlapY = uint(max(int(countY) - abs(deltaY), 0));
    uint overlapZ = uint(max(int(countZ) - abs(deltaZ), 0));
    return countX * countY * countZ - overlapX * overlapY * overlapZ;
}

void ValidateScrollCohort(uint volumeIndex)
{
    uint activeVolumeCount = min(
        SchedulerActiveVolumeCount(),
        SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES);
    if (volumeIndex >= activeVolumeCount)
        return;

    bool mandatoryScroll =
        SchedulerVolumeHasMandatoryScrollRepair(volumeIndex);
    uint volumeAccepted = 0u;
    uint volumeFailure = 0u;
    uint scrollExpected = 0u;
    uint scrollAccepted = 0u;
    uint scrollTraced = 0u;
    uint globalFatalFailure = 0u;

    if (SchedulerArenaRead(
            pc.CountersOffsetWords +
                SIMPLE_DDGI_SCHEDULER_COUNTER_EMISSION_FAILURE) != 0u)
    {
        globalFatalFailure |=
            SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_EMISSION_FAILURE;
    }

    SchedulerArenaWrite(
        pc.CountersOffsetWords +
            SIMPLE_DDGI_SCHEDULER_COUNTER_SCROLL_COHORT_STATE_BASE +
            volumeIndex,
        0u);
    if (mandatoryScroll)
    {
        scrollExpected =
            SchedulerVolumeScrollExpectedRepairCount(volumeIndex);
        if (scrollExpected != ScrollExposedProbeCount(volumeIndex))
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_COUNT_MISMATCH;
        }
    }

    uint accepted = min(
        SchedulerArenaRead(
            pc.CountersOffsetWords +
                SIMPLE_DDGI_SCHEDULER_COUNTER_ACCEPTED),
        SchedulerRequestCapacity());
    for (uint outcomeIndex = 0u;
         outcomeIndex < accepted;
         outcomeIndex++)
    {
        uint outcomeBase = pc.OutcomesOffsetWords +
            outcomeIndex * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS;
        uint updateFlags = SchedulerArenaRead(outcomeBase + 10u);
        if ((updateFlags & SIMPLE_DDGI_SCHEDULER_PROBE_SCROLL) == 0u)
            continue;

        uint probeIndex = SchedulerArenaRead(outcomeBase + 5u);
        uint outcomeVolumeIndex;
        if (!SchedulerFindVolume(probeIndex, outcomeVolumeIndex) ||
            outcomeVolumeIndex >= activeVolumeCount ||
            !SchedulerVolumeHasMandatoryScrollRepair(outcomeVolumeIndex))
        {
            globalFatalFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_UNEXPECTED_PROBE;
            continue;
        }
        if (outcomeVolumeIndex != volumeIndex)
            continue;
        if (!SchedulerProbeIsCurrentScrollExposed(probeIndex, volumeIndex))
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_UNEXPECTED_PROBE;
            continue;
        }

        volumeAccepted++;
        scrollAccepted++;

        uint packedCountAndScrollSerial = SchedulerArenaRead(outcomeBase + 11u);
        if ((packedCountAndScrollSerial >> 16u) !=
            (SchedulerVolumeScrollTransactionSerial(volumeIndex) & 0xffffu))
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_SERIAL_MISMATCH;
        }

        uint expectedRays = packedCountAndScrollSerial & 0xffffu;
        if (expectedRays == 0u || expectedRays !=
            SchedulerVolumeScrollBootstrapRays(volumeIndex))
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_CARDINALITY_MISMATCH;
        }

        uint requiredMask = SchedulerArenaRead(outcomeBase + 7u);
        uint completionMask = SchedulerArenaRead(outcomeBase + 8u);
        uint traceInvocationCount = SchedulerArenaRead(outcomeBase + 12u);
        bool traceComplete =
            (completionMask & SIMPLE_DDGI_SCHEDULER_COMPLETE_TRACE) != 0u &&
            traceInvocationCount == expectedRays;
        if (traceComplete)
            scrollTraced++;
        else
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_TRACE_INCOMPLETE;

        bool transportRequired =
            (requiredMask & SIMPLE_DDGI_SCHEDULER_COMPLETE_TRANSPORT) != 0u;
        uint transportInvocationCount = SchedulerArenaRead(outcomeBase + 13u);
        bool transportComplete = transportRequired
            ? (completionMask & SIMPLE_DDGI_SCHEDULER_COMPLETE_TRANSPORT) != 0u &&
                transportInvocationCount == expectedRays
            : (completionMask &
                SIMPLE_DDGI_SCHEDULER_TRANSPORT_NOT_REQUIRED) != 0u;
        if (!transportComplete)
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_TRANSPORT_INCOMPLETE;
        }

        uint publicationMask = requiredMask & ~(
            SIMPLE_DDGI_SCHEDULER_COMPLETE_TRACE |
            SIMPLE_DDGI_SCHEDULER_COMPLETE_TRANSPORT |
            SIMPLE_DDGI_SCHEDULER_TRANSPORT_NOT_REQUIRED);
        if ((completionMask & publicationMask) != publicationMask)
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_PUBLICATION_INCOMPLETE;
        }
        if (SchedulerArenaRead(outcomeBase + 9u) != 0u)
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_PRODUCER_FAILURE;
        }
        if (SchedulerArenaRead(outcomeBase + 0u) !=
                SchedulerQueueGeneration() ||
            SchedulerArenaRead(outcomeBase + 1u) !=
                SchedulerResourceGeneration() ||
            SchedulerArenaRead(outcomeBase + 2u) !=
                SchedulerVolumeGeneration() ||
            SchedulerArenaRead(outcomeBase + 3u) !=
                SchedulerSourceGeneration() ||
            SchedulerArenaRead(outcomeBase + 4u) !=
                SchedulerTransportGeneration() ||
            SchedulerArenaRead(outcomeBase + 6u) == 0u)
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_TRANSACTION_MISMATCH;
        }
    }

    if (mandatoryScroll)
    {
        if (volumeAccepted != scrollExpected)
        {
            volumeFailure |=
                SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_COUNT_MISMATCH;
        }
        volumeFailure |= globalFatalFailure;
        SchedulerArenaWrite(
            pc.CountersOffsetWords +
                SIMPLE_DDGI_SCHEDULER_COUNTER_SCROLL_COHORT_STATE_BASE +
                volumeIndex,
            volumeFailure == 0u
                ? SIMPLE_DDGI_SCHEDULER_SCROLL_COHORT_VALID
                : 0u);
    }

    SchedulerArenaAtomicAdd(
        pc.CountersOffsetWords +
            SIMPLE_DDGI_SCHEDULER_COUNTER_SCROLL_EXPECTED,
        scrollExpected);
    SchedulerArenaAtomicAdd(
        pc.CountersOffsetWords +
            SIMPLE_DDGI_SCHEDULER_COUNTER_SCROLL_ACCEPTED,
        scrollAccepted);
    SchedulerArenaAtomicAdd(
        pc.CountersOffsetWords +
            SIMPLE_DDGI_SCHEDULER_COUNTER_SCROLL_TRACED,
        scrollTraced);
    SchedulerArenaAtomicOr(
        pc.CountersOffsetWords +
            SIMPLE_DDGI_SCHEDULER_COUNTER_SCROLL_COHORT_FAILURE,
        volumeFailure | globalFatalFailure);
}
