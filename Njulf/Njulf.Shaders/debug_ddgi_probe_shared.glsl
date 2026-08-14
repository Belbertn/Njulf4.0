#ifndef NJULF_DEBUG_DDGI_PROBE_SHARED_GLSL
#define NJULF_DEBUG_DDGI_PROBE_SHARED_GLSL

#define SIMPLE_DDGI_DISABLE_SAMPLED_ATLAS 1
#define SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE 1
#include "common.glsl"
#include "ddgi_simple_shared.glsl"

const uint DEBUG_DDGI_MODE_PROBE_ACTIVITY = 12u;
const uint DEBUG_DDGI_MODE_UPDATED_PROBES = 13u;
const uint DEBUG_DDGI_MODE_PROBE_RELOCATION = 14u;
const uint DEBUG_DDGI_MODE_PROBE_AGE = 15u;
const uint DEBUG_DDGI_MODE_PHYSICAL_SLOTS = 16u;
const uint DEBUG_DDGI_MODE_NEWLY_EXPOSED = 18u;
const uint DEBUG_DDGI_MODE_SCHEDULER_PRIORITY = 19u;
const uint DEBUG_DDGI_MODE_UPDATE_REASONS = 22u;
const uint DEBUG_DDGI_MODE_PROBE_SPHERES = 23u;

const uint DEBUG_DDGI_SPHERE_SEGMENTS = 8u;
const uint DEBUG_DDGI_SPHERE_VERTICES =
    DEBUG_DDGI_SPHERE_SEGMENTS * 2u * 3u;
const uint DEBUG_DDGI_RELOCATION_VERTICES =
    DEBUG_DDGI_SPHERE_VERTICES * 2u + 2u;
const uint DEBUG_DDGI_INSTANCE_SCHEDULER_VISIBLE = 1u << 0u;

layout(push_constant) uniform DebugDdgiProbePushBlock
{
    mat4 ViewProjectionMatrix;
    uint Mode;
    uint SampledInstanceCount;
    uint UpdateRecordCapacity;
    uint SchedulerMode;
    uint SchedulerFrameOffsetWords;
    uint SchedulerProbeStateOffsetWords;
    uint SchedulerCountersOffsetWords;
    uint SchedulerUpdateRecordsOffsetWords;
    uint VolumeTableGeneration;
    uint SchedulerResourceGeneration;
    uint ResidencyResourceGeneration;
    // bit 0 = x-ray overlay layer, remaining bits = frame-in-flight index.
    uint XRayLayerAndFrameIndex;
    vec3 CameraPosition;
    float LifecycleLatencyTarget;
} debugPc;

struct DebugDdgiProbeResolution
{
    vec3 logicalPosition;
    vec3 resolvedPosition;
    vec3 relocation;
    float radius;
    uint volumeKind;
    uint sourceOrdinal;
    uint stateFlags;
    uint stateAge;
    uint stateClassification;
    uint stateGeneration;
    uint physicalProbeIndex;
    uint physicalPageIndex;
    uint pageMappingGeneration;
    bool tagsValid;
    bool resourceGenerationValid;
    bool mappingValid;
    bool stateValid;
    bool resident;
    bool published;
    bool schedulerProximity;
};

bool DebugDdgiFinite(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

uint DebugDdgiHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

vec4 DebugDdgiHashColor(uint value)
{
    uint hash = DebugDdgiHash(value);
    return vec4(
        0.20 + float(hash & 0xffu) / 255.0 * 0.75,
        0.20 + float((hash >> 8u) & 0xffu) / 255.0 * 0.75,
        0.20 + float((hash >> 16u) & 0xffu) / 255.0 * 0.75,
        0.95);
}

bool ResolveDebugDdgiProbe(
    uint volumeIndex,
    uvec3 logicalCoord,
    uint virtualProbeIndex,
    vec3 logicalPosition,
    float radius,
    uint expectedVolumeGeneration,
    uint expectedSchedulerGeneration,
    uint expectedResidencyGeneration,
    out DebugDdgiProbeResolution result)
{
    result.logicalPosition = logicalPosition;
    result.resolvedPosition = logicalPosition;
    result.relocation = vec3(0.0);
    result.radius = max(radius, 0.001);
    result.volumeKind = 0u;
    result.sourceOrdinal = 0u;
    result.stateFlags = 0u;
    result.stateAge = 0u;
    result.stateClassification = SIMPLE_DDGI_CLASSIFICATION_INACTIVE;
    result.stateGeneration = 0u;
    result.physicalProbeIndex = 0xffffffffu;
    result.physicalPageIndex = 0xffffffffu;
    result.pageMappingGeneration = 0u;
    result.tagsValid = false;
    result.resourceGenerationValid = false;
    result.mappingValid = false;
    result.stateValid = false;
    result.resident = false;
    result.published = false;
    result.schedulerProximity = false;

    SimpleDdgiParams params = ReadSimpleDdgiParams(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    if (volumeIndex >= params.volumeCount ||
        virtualProbeIndex >= params.probeCount ||
        expectedResidencyGeneration == 0u ||
        params.residencyResourceGeneration != expectedResidencyGeneration)
    {
        return false;
    }
    result.resourceGenerationValid = true;

    bool gpuResident = debugPc.SchedulerMode == 2u;
    if (gpuResident)
    {
        uint liveVolumeGeneration = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerFrameOffsetWords + 14u);
        uint liveSchedulerGeneration = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerFrameOffsetWords + 15u);
        if (liveVolumeGeneration != expectedVolumeGeneration ||
            liveSchedulerGeneration != expectedSchedulerGeneration)
        {
            result.resourceGenerationValid = false;
            return false;
        }
    }

    SimpleDdgiVolume volume = ReadSimpleDdgiVolume(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        volumeIndex);
    if (any(greaterThanEqual(logicalCoord, volume.gridCount)) ||
        SimpleDdgiProbeIndex(logicalCoord, volume) != virtualProbeIndex)
    {
        return false;
    }

    result.volumeKind = volume.kind;
    result.sourceOrdinal = volume.sourceOrdinal;
    float proximityRadius = volume.kind == SIMPLE_DDGI_VOLUME_KIND_RING
        ? volume.spacing * 4.0
        : max(volume.spacing * 3.0, volume.edgeFadeDistance);
    result.schedulerProximity = distance(
        debugPc.CameraPosition,
        logicalPosition) <= proximityRadius;
    result.tagsValid = true;

    SimpleDdgiVolumePaging paging = ReadSimpleDdgiVolumePaging(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        volumeIndex);
    SimpleDdgiProbeAddress address = ResolveSimpleDdgiProbeAddress(
        params,
        volume,
        paging,
        logicalCoord);
    result.resident = address.resident;
    result.physicalProbeIndex = address.physicalProbeIndex;
    result.physicalPageIndex = address.physicalPageIndex;
    result.pageMappingGeneration = address.pageMappingGeneration;
    result.mappingValid = address.resident && address.published &&
        address.pageMappingGeneration != 0u;

    SimpleDdgiProbeState state = ReadSimpleDdgiProbeState(
        uint(SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX),
        virtualProbeIndex);
    result.stateFlags = state.flags;
    result.stateAge = state.age;
    result.stateClassification = state.classification;
    result.stateGeneration = (state.flags &
        SIMPLE_DDGI_PROBE_FLAG_GENERATION_MASK) >>
        SIMPLE_DDGI_PROBE_FLAG_GENERATION_SHIFT;
    result.stateValid = result.stateGeneration != 0u &&
        DebugDdgiFinite(state.relocation) &&
        !isnan(state.activeWeight) && !isinf(state.activeWeight);
    result.relocation = result.stateValid ? state.relocation : vec3(0.0);

    SimpleDdgiReceiverProbe receiver = ReadSimpleDdgiReceiverProbe(
        uint(SIMPLE_DDGI_RECEIVER_PROBE_BUFFER_INDEX),
        virtualProbeIndex,
        volume.spacing);
    bool receiverCoherent =
        (receiver.flags & SIMPLE_DDGI_RECEIVER_FLAG_PUBLISHED_COHERENT) != 0u &&
        receiver.atlasProbeAddress == address.physicalProbeIndex &&
        receiver.slotGeneration == result.stateGeneration &&
        receiver.slotGeneration != 0u &&
        DebugDdgiFinite(receiver.relocation);
    result.published = result.resident && address.published &&
        result.stateValid && receiverCoherent;
    if (result.published)
    {
        result.relocation = receiver.relocation;
        result.resolvedPosition = logicalPosition + receiver.relocation;
    }
    return true;
}

vec4 DebugDdgiVolumeColor(DebugDdgiProbeResolution probe)
{
    if (probe.volumeKind == 1u)
        return vec4(0.95, 0.90, 0.25, 0.98);
    if (probe.volumeKind == 3u)
        return vec4(1.00, 0.48, 0.08, 0.98);
    uint ring = probe.sourceOrdinal >= 10000u
        ? min(probe.sourceOrdinal - 10000u, 2u)
        : 2u;
    return ring == 0u
        ? vec4(0.20, 0.75, 1.00, 0.95)
        : (ring == 1u
            ? vec4(0.30, 0.95, 0.55, 0.95)
            : vec4(0.95, 0.30, 0.85, 0.95));
}

vec4 DebugDdgiStateColor(DebugDdgiProbeResolution probe)
{
    if (!probe.tagsValid)
        return vec4(1.00, 0.05, 0.85, 0.98);
    if (!probe.resident)
        return vec4(0.42, 0.44, 0.48, 0.75);
    if (!probe.stateValid || !probe.published)
        return vec4(1.00, 0.05, 0.85, 0.98);
    if ((probe.stateFlags & SIMPLE_DDGI_PROBE_FLAG_INACTIVE) != 0u ||
        probe.stateClassification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE)
        return vec4(1.00, 0.12, 0.08, 0.98);
    if ((probe.stateFlags & SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING) != 0u)
        return vec4(1.00, 0.42, 0.04, 0.98);
    if ((probe.stateFlags & (SIMPLE_DDGI_PROBE_FLAG_FRESH |
            SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED)) != 0u)
        return vec4(1.00, 0.68, 0.08, 0.98);
    return vec4(0.14, 0.95, 0.30, 0.98);
}

vec4 DebugDdgiAgeColor(DebugDdgiProbeResolution probe)
{
    if (!probe.tagsValid || !probe.resident || !probe.stateValid)
        return vec4(0.42, 0.44, 0.48, 0.75);
    float target = max(debugPc.LifecycleLatencyTarget, 1.0);
    float ratio = float(probe.stateAge) / target;
    if (ratio <= 0.60)
        return vec4(0.12, 0.95, 0.28, 0.98);
    if (ratio <= 1.0)
        return vec4(1.00, 0.84, 0.08, 0.98);
    return vec4(1.00, 0.10, 0.04, 0.98);
}

vec4 DebugDdgiRelocationColor(DebugDdgiProbeResolution probe)
{
    if (probe.tagsValid && !probe.resident)
        return vec4(0.42, 0.44, 0.48, 0.75);
    if (!probe.tagsValid || !probe.stateValid ||
        (probe.stateFlags & SIMPLE_DDGI_PROBE_FLAG_INACTIVE) != 0u ||
        probe.stateClassification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE)
    {
        return vec4(1.00, 0.10, 0.06, 0.98);
    }
    if ((probe.stateFlags & SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING) != 0u)
        return vec4(1.00, 0.82, 0.08, 0.98);
    if (!probe.published)
        return vec4(1.00, 0.10, 0.06, 0.98);
    return vec4(0.14, 0.95, 0.30, 0.98);
}

vec4 DebugDdgiPhysicalColor(DebugDdgiProbeResolution probe)
{
    if (!probe.tagsValid || !probe.resourceGenerationValid ||
        (probe.resident && !probe.mappingValid))
        return vec4(1.00, 0.05, 0.85, 0.98);
    if (!probe.resident)
        return vec4(0.42, 0.44, 0.48, 0.75);
    uint identity = probe.physicalPageIndex != 0xffffffffu
        ? probe.physicalPageIndex * 8u + (probe.physicalProbeIndex & 7u)
        : probe.physicalProbeIndex;
    return DebugDdgiHashColor(identity);
}

vec4 DebugDdgiPriorityColor(
    DebugDdgiProbeResolution probe,
    uint virtualProbeIndex,
    uint instanceFlags)
{
    if (!probe.tagsValid || !probe.resident)
        return vec4(0.42, 0.44, 0.48, 0.75);
    bool visible = (instanceFlags &
        DEBUG_DDGI_INSTANCE_SCHEDULER_VISIBLE) != 0u;
    if (debugPc.SchedulerMode == 2u)
    {
        uint metadata = ReadStorageWordUniform(
            uint(SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX),
            debugPc.SchedulerProbeStateOffsetWords +
                virtualProbeIndex * 12u + 5u);
        visible = (metadata & (1u << 16u)) != 0u;
    }
    if (visible)
        return vec4(0.10, 0.90, 1.00, 0.98);
    return probe.schedulerProximity
        ? vec4(0.18, 0.95, 0.32, 0.96)
        : vec4(1.00, 0.42, 0.06, 0.90);
}

vec4 DebugDdgiProbeColor(
    DebugDdgiProbeResolution probe,
    uint virtualProbeIndex,
    uint instanceFlags)
{
    if (debugPc.Mode == DEBUG_DDGI_MODE_PROBE_SPHERES)
        return DebugDdgiVolumeColor(probe);
    if (debugPc.Mode == DEBUG_DDGI_MODE_PROBE_ACTIVITY ||
        debugPc.Mode == DEBUG_DDGI_MODE_NEWLY_EXPOSED)
        return DebugDdgiStateColor(probe);
    if (debugPc.Mode == DEBUG_DDGI_MODE_PROBE_RELOCATION)
        return DebugDdgiRelocationColor(probe);
    if (debugPc.Mode == DEBUG_DDGI_MODE_PROBE_AGE)
        return DebugDdgiAgeColor(probe);
    if (debugPc.Mode == DEBUG_DDGI_MODE_PHYSICAL_SLOTS)
        return DebugDdgiPhysicalColor(probe);
    if (debugPc.Mode == DEBUG_DDGI_MODE_SCHEDULER_PRIORITY)
        return DebugDdgiPriorityColor(
            probe,
            virtualProbeIndex,
            instanceFlags);
    return vec4(0.20, 0.75, 1.00, 0.95);
}

vec3 DebugDdgiSphereOffset(uint vertexIndex, float radius)
{
    uint endpoint = vertexIndex & 1u;
    uint segment = (vertexIndex >> 1u) % DEBUG_DDGI_SPHERE_SEGMENTS;
    uint ring = vertexIndex / (DEBUG_DDGI_SPHERE_SEGMENTS * 2u);
    float angle = 6.28318530718 *
        float(segment + endpoint) / float(DEBUG_DDGI_SPHERE_SEGMENTS);
    vec2 circle = vec2(cos(angle), sin(angle)) * radius;
    return ring == 0u
        ? vec3(circle, 0.0)
        : (ring == 1u
            ? vec3(circle.x, 0.0, circle.y)
            : vec3(0.0, circle));
}

void DebugDdgiEmit(
    vec3 worldPosition,
    vec4 color,
    out vec4 clipPosition,
    out vec4 vertexColor)
{
    if ((debugPc.XRayLayerAndFrameIndex & 1u) != 0u)
        color.a = min(color.a, 0.30);
    clipPosition = debugPc.ViewProjectionMatrix * vec4(worldPosition, 1.0);
    vertexColor = color;
}

void DebugDdgiEmitInvalid(out vec4 clipPosition, out vec4 vertexColor)
{
    clipPosition = vec4(2.0, 2.0, 2.0, 1.0);
    vertexColor = vec4(0.0);
}

bool DebugDdgiRecordsCounters()
{
    return gl_VertexIndex == 0 &&
        (debugPc.XRayLayerAndFrameIndex & 1u) == 0u;
}

uint DebugDdgiDiagnosticsBufferIndex()
{
    return uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) +
        (debugPc.XRayLayerAndFrameIndex >> 1u);
}

void DebugDdgiCounterAdd(uint counterIndex)
{
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(
            DebugDdgiDiagnosticsBufferIndex())].Words[counterIndex],
        1u);
}

void DebugDdgiWriteCounterHeader()
{
    if (!DebugDdgiRecordsCounters() || gl_InstanceIndex != 0)
        return;

    uint bufferIndex = DebugDdgiDiagnosticsBufferIndex();
    atomicExchange(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            DEBUG_DDGI_OVERLAY_MODE_COUNTER],
        debugPc.Mode + 1u);
    atomicExchange(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            DEBUG_DDGI_OVERLAY_VOLUME_GENERATION_COUNTER],
        debugPc.VolumeTableGeneration);
    atomicExchange(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            DEBUG_DDGI_OVERLAY_SCHEDULER_GENERATION_COUNTER],
        debugPc.SchedulerResourceGeneration);
    atomicExchange(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            DEBUG_DDGI_OVERLAY_RESIDENCY_GENERATION_COUNTER],
        debugPc.ResidencyResourceGeneration);
}

bool DebugDdgiProbeIsStale(
    DebugDdgiProbeResolution probe,
    bool resolved)
{
    return !resolved || !probe.resourceGenerationValid ||
        (probe.resident && !probe.mappingValid);
}

void DebugDdgiRecordResolvedMarker(
    DebugDdgiProbeResolution probe,
    bool resolved,
    bool filtered)
{
    if (!DebugDdgiRecordsCounters())
        return;

    DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_DRAWN_COUNTER);
    if (filtered)
        DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_FILTERED_COUNTER);
    if (!probe.resident && probe.resourceGenerationValid && probe.tagsValid)
        DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_NONRESIDENT_COUNTER);
    if (DebugDdgiProbeIsStale(probe, resolved))
        DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_STALE_MAPPING_COUNTER);
    if (!probe.tagsValid || !probe.stateValid)
        DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_STATE_UNAVAILABLE_COUNTER);
}

void DebugDdgiRecordFilteredTransaction()
{
    if (!DebugDdgiRecordsCounters())
        return;
    DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_FILTERED_COUNTER);
    DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_INVALID_TRANSACTION_COUNTER);
}

void DebugDdgiRecordInvalidTransaction()
{
    if (DebugDdgiRecordsCounters())
        DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_INVALID_TRANSACTION_COUNTER);
}

void DebugDdgiRecordUpdateReasons(uint reasons)
{
    if (!DebugDdgiRecordsCounters())
        return;
    for (uint reason = 0u; reason < 16u; ++reason)
    {
        if ((reasons & (1u << reason)) != 0u)
        {
            DebugDdgiCounterAdd(
                DEBUG_DDGI_OVERLAY_REASON_COUNTER_BASE + reason);
        }
    }
    if (bitCount(reasons) > 1)
        DebugDdgiCounterAdd(DEBUG_DDGI_OVERLAY_MULTI_REASON_COUNTER);
}

// Reconstruct the complete current reason family from the public update,
// canonical state, and the scheduler-private reason bits that deliberately do
// not escape into the public transport queue.
uint DebugDdgiUpdateReasonBits(
    uint updateFlags,
    uint stateFlags,
    uint stateClassification,
    uint privateFlags,
    bool sparseResidency)
{
    uint reasons = 0u;
    if ((stateFlags & SIMPLE_DDGI_PROBE_FLAG_FRESH) != 0u)
        reasons |= 1u << 0u;
    if ((stateFlags & SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED) != 0u)
        reasons |= 1u << 1u;
    if ((privateFlags & (1u << 26u)) != 0u)
        reasons |= 1u << 2u;
    if ((privateFlags & (1u << 27u)) != 0u)
        reasons |= 1u << 3u;
    if ((privateFlags & (1u << 25u)) != 0u)
        reasons |= 1u << 4u;
    if ((stateFlags & SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID) != 0u)
        reasons |= (1u << 5u) | (1u << 7u);
    if ((stateFlags & SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING) != 0u)
        reasons |= 1u << 6u;
    if ((updateFlags & (1u << 15u)) != 0u)
        reasons |= 1u << 8u;
    if ((updateFlags & SIMPLE_DDGI_UPDATE_MAINTENANCE) != 0u)
        reasons |= 1u << 9u;
    if ((stateFlags & SIMPLE_DDGI_PROBE_FLAG_INACTIVE) != 0u ||
        stateClassification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE)
        reasons |= 1u << 10u;
    if ((privateFlags & (1u << 29u)) != 0u ||
        (updateFlags & SIMPLE_DDGI_UPDATE_INVALIDATE) != 0u)
        reasons |= 1u << 11u;
    if (sparseResidency && (privateFlags & (1u << 25u)) != 0u)
        reasons |= 1u << 12u;
    uint sourceMode = (updateFlags >> 30u) & 3u;
    if ((updateFlags & SIMPLE_DDGI_UPDATE_SOURCE_REFRESH) != 0u &&
        (sourceMode == 1u || sourceMode == 2u))
        reasons |= 1u << 13u;
    if (sourceMode == 3u)
        reasons |= 1u << 14u;
    if ((updateFlags & SIMPLE_DDGI_UPDATE_MAINTENANCE) != 0u &&
        (updateFlags & (SIMPLE_DDGI_UPDATE_SOURCE_REFRESH | (1u << 15u))) == 0u)
        reasons |= 1u << 15u;
    if ((privateFlags & (1u << 28u)) != 0u)
        reasons |= 1u << 7u;
    return reasons;
}

vec4 DebugDdgiReasonColor(uint reasons)
{
    if ((reasons & (1u << 7u)) != 0u)  return vec4(0.86, 0.08, 1.00, 0.98);
    if ((reasons & (1u << 11u)) != 0u) return vec4(1.00, 0.08, 0.62, 0.98);
    if ((reasons & (1u << 6u)) != 0u)  return vec4(1.00, 0.38, 0.04, 0.98);
    if ((reasons & (1u << 10u)) != 0u) return vec4(1.00, 0.10, 0.08, 0.98);
    if ((reasons & (1u << 5u)) != 0u)  return vec4(1.00, 0.22, 0.06, 0.98);
    if ((reasons & (1u << 0u)) != 0u)  return vec4(1.00, 0.68, 0.08, 0.98);
    if ((reasons & (1u << 1u)) != 0u)  return vec4(0.08, 0.90, 1.00, 0.98);
    if ((reasons & (1u << 3u)) != 0u)  return vec4(1.00, 0.88, 0.08, 0.98);
    if ((reasons & (1u << 2u)) != 0u)  return vec4(0.52, 1.00, 0.08, 0.98);
    if ((reasons & (1u << 4u)) != 0u)  return vec4(0.18, 0.65, 1.00, 0.98);
    if ((reasons & (1u << 8u)) != 0u)  return vec4(0.38, 0.42, 1.00, 0.98);
    if ((reasons & (1u << 9u)) != 0u)  return vec4(0.16, 0.90, 0.48, 0.98);
    if ((reasons & (1u << 14u)) != 0u) return vec4(0.88, 0.26, 1.00, 0.98);
    if ((reasons & (1u << 13u)) != 0u) return vec4(0.62, 0.32, 1.00, 0.98);
    if ((reasons & (1u << 15u)) != 0u) return vec4(0.10, 0.82, 0.58, 0.98);
    if ((reasons & (1u << 12u)) != 0u) return vec4(0.10, 0.72, 0.92, 0.98);
    return vec4(0.55, 0.58, 0.64, 0.78);
}

#endif
