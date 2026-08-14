#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "debug_ddgi_probe_shared.glsl"

layout(location = 0) out vec4 outColor;

void main()
{
    DebugDdgiWriteCounterHeader();
    SimpleDdgiParams params = ReadSimpleDdgiParams(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    uint liveCount = min(params.probesToUpdate, debugPc.UpdateRecordCapacity);
    if (debugPc.SchedulerMode == 2u)
    {
        uint liveVolumeGeneration = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerFrameOffsetWords + 14u);
        uint liveSchedulerGeneration = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerFrameOffsetWords + 15u);
        if (liveVolumeGeneration != debugPc.VolumeTableGeneration ||
            liveSchedulerGeneration != debugPc.SchedulerResourceGeneration)
        {
            DebugDdgiRecordFilteredTransaction();
            DebugDdgiEmitInvalid(gl_Position, outColor);
            return;
        }
        liveCount = min(
            ReadStorageWordUniform(
                uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
                debugPc.SchedulerCountersOffsetWords + 2u),
            debugPc.UpdateRecordCapacity);
    }

    uint queueIndex = uint(gl_InstanceIndex);
    if (queueIndex >= liveCount)
    {
        DebugDdgiEmitInvalid(gl_Position, outColor);
        return;
    }

    SimpleDdgiProbeUpdate update = ReadSimpleDdgiProbeUpdate(
        uint(SIMPLE_DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX),
        queueIndex);
    if (update.volumeIndex >= params.volumeCount ||
        update.probeIndex >= params.probeCount)
    {
        DebugDdgiRecordFilteredTransaction();
        DebugDdgiEmitInvalid(gl_Position, outColor);
        return;
    }

    SimpleDdgiVolume volume = ReadSimpleDdgiVolume(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        update.volumeIndex);
    uint volumeProbeCount = SimpleDdgiVolumeProbeCount(volume);
    if (update.probeIndex < volume.firstProbeIndex ||
        update.probeIndex - volume.firstProbeIndex >= volumeProbeCount)
    {
        DebugDdgiRecordFilteredTransaction();
        DebugDdgiEmitInvalid(gl_Position, outColor);
        return;
    }

    uvec3 logicalCoord = SimpleDdgiProbeCoord(
        update.probeIndex - volume.firstProbeIndex,
        volume);
    vec3 logicalPosition = volume.origin + vec3(logicalCoord) * volume.spacing;
    DebugDdgiProbeResolution probe;
    bool resolved = ResolveDebugDdgiProbe(
        update.volumeIndex,
        logicalCoord,
        update.probeIndex,
        logicalPosition,
        clamp(volume.spacing * 0.08, 0.04, 0.20),
        debugPc.VolumeTableGeneration,
        debugPc.SchedulerResourceGeneration,
        debugPc.ResidencyResourceGeneration,
        probe);

    bool denseResidency = params.residencyMode ==
        SIMPLE_DDGI_RESIDENCY_MODE_DENSE;
    uint expectedGeneration = update.expectedGeneration & 0x00ffffffu;
    bool transactionValid = probe.tagsValid && probe.resident &&
        probe.stateGeneration == expectedGeneration &&
        update.physicalProbeIndex == probe.physicalProbeIndex &&
        update.pageMappingGeneration == probe.pageMappingGeneration &&
        ((denseResidency && update.residencyResourceGeneration == 0u) ||
         (!denseResidency && update.residencyResourceGeneration ==
            params.residencyResourceGeneration));
    DebugDdgiRecordResolvedMarker(probe, resolved, false);
    if (!transactionValid)
    {
        DebugDdgiRecordInvalidTransaction();
        if (!DebugDdgiProbeIsStale(probe, resolved))
            DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_STALE_MAPPING_COUNTER);
    }

    uint privateFlags = 0u;
    bool committed = probe.stateAge == 0u;
    if (debugPc.SchedulerMode == 2u &&
        update.outcomeIndex < debugPc.UpdateRecordCapacity)
    {
        privateFlags = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerUpdateRecordsOffsetWords +
                update.outcomeIndex * 10u + 2u);
        uint lastCommittedFrame = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerProbeStateOffsetWords +
                update.probeIndex * 12u);
        uint schedulerFrame = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerFrameOffsetWords + 10u);
        committed = lastCommittedFrame == schedulerFrame;
    }

    uint reasons = 0u;
    vec4 color;
    if (!transactionValid)
    {
        color = vec4(1.00, 0.08, 0.06, 0.98);
    }
    else if (debugPc.Mode == DEBUG_DDGI_MODE_UPDATE_REASONS)
    {
        reasons = DebugDdgiUpdateReasonBits(
            update.flags,
            probe.stateFlags,
            probe.stateClassification,
            privateFlags,
            !denseResidency);
        color = DebugDdgiReasonColor(reasons);
    }
    else if ((update.flags & SIMPLE_DDGI_UPDATE_SOURCE_REFRESH) != 0u)
    {
        color = vec4(0.72, 0.24, 1.00, committed ? 1.0 : 0.76);
    }
    else if ((update.flags & SIMPLE_DDGI_UPDATE_MAINTENANCE) != 0u)
    {
        color = vec4(0.08, 0.90, 1.00, committed ? 1.0 : 0.76);
    }
    else
    {
        color = vec4(0.12, 0.45, 1.00, committed ? 1.0 : 0.76);
    }
    if (debugPc.Mode == DEBUG_DDGI_MODE_UPDATE_REASONS)
        DebugDdgiRecordUpdateReasons(reasons);

    uint vertexIndex = uint(gl_VertexIndex);
    DebugDdgiEmit(
        probe.resolvedPosition + DebugDdgiSphereOffset(
            vertexIndex,
            probe.radius),
        color,
        gl_Position,
        outColor);
}
