#ifndef NJULF_DDGI_SIMPLE_SCHEDULE_SPATIAL_GLSL
#define NJULF_DDGI_SIMPLE_SCHEDULE_SPATIAL_GLSL

// Scheduler policy accessors and arena reads are supplied by the caller.
// Scheduling order is a permutation of the world lattice; physical wrapping
// only changes the storage address. Spatial tests must use the same logical
// coordinates as the probe selected by that permutation.

uvec3 SchedulerSequenceLogicalCoordinates(uint volumeIndex, uint localOrdinal)
{
    uint count = max(SchedulerVolumeProbeCount(volumeIndex), 1u);
    uint stride = max(SchedulerVolumeSequenceStride(volumeIndex), 1u);
    uint logical = (localOrdinal * stride) % count;
    uint countX = max(SchedulerVolumeCurrentCountX(volumeIndex), 1u);
    uint countY = max(SchedulerVolumeCurrentCountY(volumeIndex), 1u);
    uint xy = countX * countY;
    uint z = logical / xy;
    uint rem = logical - z * xy;
    uint y = rem / countX;
    uint x = rem - y * countX;
    return uvec3(x, y, z);
}

uint SchedulerSequenceProbeIndex(uint volumeIndex, uint localOrdinal)
{
    uvec3 logical = SchedulerSequenceLogicalCoordinates(volumeIndex, localOrdinal);
    uint countX = max(SchedulerVolumeCurrentCountX(volumeIndex), 1u);
    uint countY = max(SchedulerVolumeCurrentCountY(volumeIndex), 1u);
    uint countZ = max(SchedulerVolumeCurrentCountZ(volumeIndex), 1u);
    uint physicalX = (logical.x + SchedulerVolumePhysicalOffsetX(volumeIndex)) % countX;
    uint physicalY = (logical.y + SchedulerVolumePhysicalOffsetY(volumeIndex)) % countY;
    uint physicalZ = (logical.z + SchedulerVolumePhysicalOffsetZ(volumeIndex)) % countZ;
    return SchedulerVolumeFirstProbe(volumeIndex) +
        physicalX + physicalY * countX + physicalZ * countX * countY;
}

bool SchedulerDirtyIntersects(uint volumeIndex, uint localOrdinal, out uint reasons)
{
    reasons = 0u;
    uvec3 logical = SchedulerSequenceLogicalCoordinates(volumeIndex, localOrdinal);
    vec3 position = SchedulerVolumeOrigin(volumeIndex) +
        vec3(logical) * SchedulerVolumeSpacing(volumeIndex);
    uint dirtyCount = min(SchedulerDirtyRegionCount(), SchedulerFrame(25u));
    float expand = SchedulerVolumeSpacing(volumeIndex);
    for (uint i = 0u; i < SIMPLE_DDGI_SCHEDULER_MAX_VOLUMES * 64u; i++)
    {
        if (i >= dirtyCount)
            continue;
        uint base = pc.DirtyRegionOffsetWords + i * 20u;
        vec3 minimum = vec3(
            uintBitsToFloat(SchedulerArenaRead(base + 0u)),
            uintBitsToFloat(SchedulerArenaRead(base + 1u)),
            uintBitsToFloat(SchedulerArenaRead(base + 2u))) - vec3(expand);
        vec3 maximum = vec3(
            uintBitsToFloat(SchedulerArenaRead(base + 4u)),
            uintBitsToFloat(SchedulerArenaRead(base + 5u)),
            uintBitsToFloat(SchedulerArenaRead(base + 6u))) + vec3(expand);
        if (all(greaterThanEqual(position, minimum)) && all(lessThanEqual(position, maximum)))
            reasons |= SchedulerArenaRead(base + 16u);
    }
    return reasons != 0u;
}

#endif
