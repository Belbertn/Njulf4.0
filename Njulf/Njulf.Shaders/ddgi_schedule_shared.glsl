#ifndef NJULF_DDGI_SCHEDULE_SHARED_GLSL
#define NJULF_DDGI_SCHEDULE_SHARED_GLSL

const uint DDGI_SCHEDULE_WORKGROUP_SIZE = 64u;
const uint DDGI_SCHEDULER_FLAG_VIEW_PRIORITY = 1u << 0;
const uint DDGI_WARMUP_STATE_DISABLED = 0u;
const uint DDGI_WARMUP_STATE_COLD_START = 1u;
const uint DDGI_WARMUP_STATE_LOCAL_VOLUME = 2u;
const uint DDGI_WARMUP_STATE_NEAR_CASCADE = 3u;
const uint DDGI_WARMUP_STATE_STEADY = 4u;
const uint DDGI_WARMUP_STATE_RECOVERY = 5u;
const uint DDGI_SCHEDULE_INVALID_PROBE = 0xffffffffu;
const uint DDGI_SCHEDULE_REASON_NEW_CELL = 1u << 0;
const uint DDGI_SCHEDULE_REASON_DIRTY_BOUNDS = 1u << 1;
const uint DDGI_SCHEDULE_REASON_VISIBLE_FRUSTUM = 1u << 2;
const uint DDGI_SCHEDULE_REASON_AGE_REFRESH = 1u << 3;
const uint DDGI_SCHEDULE_REASON_LOW_CONFIDENCE = 1u << 4;
const uint DDGI_SCHEDULE_REASON_HIGH_VARIANCE = 1u << 5;
const uint DDGI_SCHEDULE_REASON_OUTSIDE_FRUSTUM_SAFETY = 1u << 6;
const uint DDGI_SCHEDULE_VOLUME_KIND_CAMERA_CLIPMAP = 1u;

struct DdgiScheduleConstants
{
    uint ActiveProbeCount;
    uint VolumeCount;
    uint RequestBudget;
    uint PrimaryRayBudget;
    uint DirtyRegionCount;
    uint PriorityBucketCount;
    uint FrameSerial;
    uint Flags;
    vec4 CameraPositionNearPlane;
    vec4 ForwardFarPlane;
    vec4 RightTanHalfFovX;
    vec4 UpTanHalfFovY;
    vec4 CameraVelocitySafetyRadius;
    float FrustumPriorityWeight;
    float NewProbeUpdateBoost;
    float OutOfFrustumMinimumUpdateFraction;
    uint MinimumProbeRefreshFrames;
    uint WarmupState;
    uint WarmupLocalBudget;
    uint WarmupCascade0Budget;
    uint WarmupNewCellBudget;
    uint WarmupSafetyBudget;
};

struct DdgiScheduleVolumeInfo
{
    uint VolumeIndex;
    uint FirstProbe;
    uint LocalProbeIndex;
    uint RaysPerProbe;
    uint Kind;
    uint CascadeIndex;
    uvec3 ProbeCounts;
    ivec3 LogicalCell;
    vec3 ProbePosition;
    bool Valid;
};

DdgiScheduleConstants ReadDdgiScheduleConstants()
{
    DdgiScheduleConstants constants;
    constants.ActiveProbeCount = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_ACTIVE_PROBE_COUNT) / 4u);
    constants.VolumeCount = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_VOLUME_COUNT) / 4u);
    constants.RequestBudget = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_REQUEST_BUDGET) / 4u);
    constants.PrimaryRayBudget = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_PRIMARY_RAY_BUDGET) / 4u);
    constants.DirtyRegionCount = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_DIRTY_REGION_COUNT) / 4u);
    constants.PriorityBucketCount = max(ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_PRIORITY_BUCKET_COUNT) / 4u), 1u);
    constants.FrameSerial = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_FRAME_SERIAL) / 4u);
    constants.Flags = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_FLAGS) / 4u);
    constants.CameraPositionNearPlane = ReadStorageVec4(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_CAMERA_POSITION_NEAR_PLANE) / 4u);
    constants.ForwardFarPlane = ReadStorageVec4(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_FORWARD_FAR_PLANE) / 4u);
    constants.RightTanHalfFovX = ReadStorageVec4(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_RIGHT_TAN_HALF_FOV_X) / 4u);
    constants.UpTanHalfFovY = ReadStorageVec4(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_UP_TAN_HALF_FOV_Y) / 4u);
    constants.CameraVelocitySafetyRadius = ReadStorageVec4(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_CAMERA_VELOCITY_SAFETY_RADIUS) / 4u);
    constants.FrustumPriorityWeight = ReadStorageFloat(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_FRUSTUM_PRIORITY_WEIGHT) / 4u);
    constants.NewProbeUpdateBoost = ReadStorageFloat(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_NEW_PROBE_UPDATE_BOOST) / 4u);
    constants.OutOfFrustumMinimumUpdateFraction = ReadStorageFloat(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_OUT_OF_FRUSTUM_MINIMUM_UPDATE_FRACTION) / 4u);
    constants.MinimumProbeRefreshFrames = max(ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_MINIMUM_PROBE_REFRESH_FRAMES) / 4u), 1u);
    constants.WarmupState = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_WARMUP_STATE) / 4u);
    constants.WarmupLocalBudget = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_WARMUP_LOCAL_BUDGET) / 4u);
    constants.WarmupCascade0Budget = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_WARMUP_CASCADE0_BUDGET) / 4u);
    constants.WarmupNewCellBudget = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_WARMUP_NEW_CELL_BUDGET) / 4u);
    constants.WarmupSafetyBudget = ReadStorageWord(uint(DDGI_SCHEDULER_CONSTANTS_BUFFER_INDEX), uint(OFFSET_GPU_DDGI_SCHEDULER_CONSTANTS_WARMUP_SAFETY_BUDGET) / 4u);
    return constants;
}

uint DdgiScheduleVolumeBaseWord(uint volumeIndex)
{
    return uint(SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER) / 4u +
        volumeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_VOLUME) / 4u);
}

bool TryResolveDdgiScheduleVolume(uint probeIndex, uint volumeCount, out DdgiScheduleVolumeInfo info)
{
    info.VolumeIndex = 0u;
    info.FirstProbe = 0u;
    info.LocalProbeIndex = 0u;
    info.RaysPerProbe = 1u;
    info.Kind = 0u;
    info.CascadeIndex = 0u;
    info.ProbeCounts = uvec3(1u);
    info.LogicalCell = ivec3(0);
    info.ProbePosition = vec3(0.0);
    info.Valid = false;

    for (uint volumeIndex = 0u; volumeIndex < volumeCount; volumeIndex++)
    {
        uint baseWord = DdgiScheduleVolumeBaseWord(volumeIndex);
        vec4 originAndFirst = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX) / 4u);
        vec4 sizeAndCountX = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X) / 4u);
        vec4 spacingAndCountY = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y) / 4u);
        vec4 biasAndCountZ = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z) / 4u);
        vec4 rayAndUpdateParams = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_RAY_AND_UPDATE_PARAMS) / 4u);
        vec4 gridMinAndKind = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND) / 4u);
        vec4 ringOffsetAndCascade = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE) / 4u);
        uint firstProbe = uint(max(round(originAndFirst.w), 0.0));
        uvec3 counts = max(uvec3(
            uint(max(round(sizeAndCountX.w), 1.0)),
            uint(max(round(spacingAndCountY.w), 1.0)),
            uint(max(round(biasAndCountZ.w), 1.0))), uvec3(1u));
        uint volumeProbeCount = counts.x * counts.y * counts.z;
        if (probeIndex < firstProbe || probeIndex >= firstProbe + volumeProbeCount)
            continue;

        uint localIndex = probeIndex - firstProbe;
        uint localX = localIndex % counts.x;
        uint localY = (localIndex / counts.x) % counts.y;
        uint localZ = localIndex / (counts.x * counts.y);
        uint kind = uint(max(round(gridMinAndKind.w), 0.0));
        ivec3 logicalCell = ivec3(int(localX), int(localY), int(localZ));
        vec3 spacing = max(spacingAndCountY.xyz, vec3(0.0001));
        vec3 probePosition = originAndFirst.xyz + spacing * vec3(logicalCell);
        if (kind == DDGI_SCHEDULE_VOLUME_KIND_CAMERA_CLIPMAP)
        {
            logicalCell = DdgiDecodeLogicalCellFromPhysicalProbeIndex(
                probeIndex,
                ivec3(round(gridMinAndKind.xyz)),
                ivec3(round(ringOffsetAndCascade.xyz)),
                counts,
                firstProbe);
            probePosition = vec3(logicalCell) * spacing;
        }

        info.VolumeIndex = volumeIndex;
        info.FirstProbe = firstProbe;
        info.LocalProbeIndex = localIndex;
        info.RaysPerProbe = max(uint(round(rayAndUpdateParams.x)), 1u);
        info.Kind = kind;
        info.CascadeIndex = uint(max(round(ringOffsetAndCascade.w), 0.0));
        info.ProbeCounts = counts;
        info.LogicalCell = logicalCell;
        info.ProbePosition = probePosition;
        info.Valid = true;
        return true;
    }

    return false;
}

bool DdgiScheduleProbeIntersectsDirtyRegion(vec3 probePosition, DdgiScheduleConstants constants)
{
    for (uint dirtyIndex = 0u; dirtyIndex < constants.DirtyRegionCount; dirtyIndex++)
    {
        uint baseWord = dirtyIndex * (uint(SIZEOF_GPU_DDGI_DIRTY_REGION) / 4u);
        vec4 minReason = ReadStorageVec4(uint(DDGI_DIRTY_REGION_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_DIRTY_REGION_MIN_REASON) / 4u);
        vec4 maxPadding = ReadStorageVec4(uint(DDGI_DIRTY_REGION_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_DIRTY_REGION_MAX_PADDING) / 4u);
        if (all(greaterThanEqual(probePosition, minReason.xyz)) && all(lessThanEqual(probePosition, maxPadding.xyz)))
            return true;
    }

    return false;
}

bool DdgiScheduleProbeInViewFrustum(vec3 probePosition, DdgiScheduleConstants constants)
{
    if ((constants.Flags & DDGI_SCHEDULER_FLAG_VIEW_PRIORITY) == 0u)
        return false;

    vec3 toProbe = probePosition - constants.CameraPositionNearPlane.xyz;
    float forwardDistance = dot(toProbe, normalize(constants.ForwardFarPlane.xyz));
    if (forwardDistance < constants.CameraPositionNearPlane.w || forwardDistance > constants.ForwardFarPlane.w)
        return false;

    float rightDistance = abs(dot(toProbe, normalize(constants.RightTanHalfFovX.xyz)));
    float upDistance = abs(dot(toProbe, normalize(constants.UpTanHalfFovY.xyz)));
    float maxRight = max(forwardDistance * constants.RightTanHalfFovX.w, 0.0001);
    float maxUp = max(forwardDistance * constants.UpTanHalfFovY.w, 0.0001);
    return rightDistance <= maxRight && upDistance <= maxUp;
}

bool DdgiScheduleProbeInSafetyShell(vec3 probePosition, DdgiScheduleConstants constants)
{
    if ((constants.Flags & DDGI_SCHEDULER_FLAG_VIEW_PRIORITY) == 0u)
        return false;

    float safetyRadius = max(constants.CameraVelocitySafetyRadius.w, 0.0);
    if (safetyRadius <= 0.0)
        return false;

    return distance(probePosition, constants.CameraPositionNearPlane.xyz) <= safetyRadius;
}

uint DdgiScheduleHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

bool DdgiScheduleLaneSelected(uint probeIndex, uint frameSerial, uint divisor)
{
    if (divisor <= 1u)
        return true;

    return DdgiScheduleHash(probeIndex ^ (frameSerial * 747796405u)) % divisor == 0u;
}

bool TryReserveDdgiScheduleCandidateSlot(
    DdgiScheduleConstants constants,
    uint groupIndex,
    uint priority,
    uint reasonFlags)
{
    uint requestBudget = max(min(constants.RequestBudget, constants.ActiveProbeCount), 1u);
    uint globalCap = max(requestBudget * 4u, 1u);
    uint groupCount = max((constants.ActiveProbeCount + DDGI_SCHEDULE_WORKGROUP_SIZE - 1u) / DDGI_SCHEDULE_WORKGROUP_SIZE, 1u);
    uint groupBucketCount = groupCount * constants.PriorityBucketCount;
    uint localTopKCap = min(max((requestBudget + groupCount - 1u) / groupCount, 1u), 16u);
    uint bucketCap = globalCap;
    if ((reasonFlags & (DDGI_SCHEDULE_REASON_NEW_CELL | DDGI_SCHEDULE_REASON_DIRTY_BOUNDS)) != 0u)
        bucketCap = min(globalCap, requestBudget * 2u);
    else if ((reasonFlags & DDGI_SCHEDULE_REASON_VISIBLE_FRUSTUM) != 0u)
        bucketCap = min(globalCap, requestBudget * 2u);
    else if ((reasonFlags & DDGI_SCHEDULE_REASON_OUTSIDE_FRUSTUM_SAFETY) != 0u)
        bucketCap = min(globalCap, requestBudget);
    else if ((reasonFlags & DDGI_SCHEDULE_REASON_AGE_REFRESH) != 0u)
        bucketCap = min(globalCap, max(requestBudget / 2u, 1u));

    uint bucketCounterIndex = min(priority, constants.PriorityBucketCount - 1u);
    uint bucketReservation = atomicAdd(BindlessStorageBuffers[uint(DDGI_SCHEDULER_PREFIX_BUFFER_INDEX)].Words[bucketCounterIndex], 1u);
    if (bucketReservation >= bucketCap)
        return false;

    uint groupBucketIndex = groupIndex * constants.PriorityBucketCount + bucketCounterIndex;
    uint localReservation = atomicAdd(BindlessStorageBuffers[uint(DDGI_SCHEDULER_PREFIX_BUFFER_INDEX)].Words[constants.PriorityBucketCount + groupBucketIndex], 1u);
    if (localReservation >= localTopKCap)
        return false;

    uint totalReservation = atomicAdd(BindlessStorageBuffers[uint(DDGI_SCHEDULER_PREFIX_BUFFER_INDEX)].Words[constants.PriorityBucketCount + groupBucketCount], 1u);
    return totalReservation < globalCap;
}

void WriteDdgiProbeCandidate(
    uint candidateIndex,
    uint probeIndex,
    uint volumeIndex,
    uint priority,
    uint reasonFlags,
    ivec3 logicalCell,
    uint primaryRayCost,
    uint scoreKey)
{
    uint baseWord = candidateIndex * (uint(SIZEOF_GPU_DDGI_PROBE_CANDIDATE) / 4u);
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_PROBE_INDEX) / 4u, probeIndex);
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_VOLUME_INDEX) / 4u, volumeIndex);
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_PRIORITY) / 4u, priority);
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_REASON_FLAGS) / 4u, reasonFlags);
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_LOGICAL_CELL_X) / 4u, uint(logicalCell.x));
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_LOGICAL_CELL_Y) / 4u, uint(logicalCell.y));
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_LOGICAL_CELL_Z) / 4u, uint(logicalCell.z));
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_PRIMARY_RAY_COST) / 4u, primaryRayCost);
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_SCORE_KEY) / 4u, scoreKey);
    WriteStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_CANDIDATE_RESERVED0) / 4u, 0u);
}

uint ReadDdgiProbeCandidateWord(uint candidateIndex, int byteOffset)
{
    uint baseWord = candidateIndex * (uint(SIZEOF_GPU_DDGI_PROBE_CANDIDATE) / 4u);
    return ReadStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), baseWord + uint(byteOffset) / 4u);
}

void CopyDdgiProbeCandidate(uint destinationCandidateIndex, uint sourceCandidateIndex)
{
    uint sourceBaseWord = sourceCandidateIndex * (uint(SIZEOF_GPU_DDGI_PROBE_CANDIDATE) / 4u);
    uint destinationBaseWord = destinationCandidateIndex * (uint(SIZEOF_GPU_DDGI_PROBE_CANDIDATE) / 4u);
    for (uint word = 0u; word < uint(SIZEOF_GPU_DDGI_PROBE_CANDIDATE) / 4u; word++)
        WriteStorageWord(
            uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX),
            destinationBaseWord + word,
            ReadStorageWord(uint(DDGI_PROBE_CANDIDATE_BUFFER_INDEX), sourceBaseWord + word));
}

void WriteDdgiProbeUpdateRequestFromCandidate(uint requestIndex, uint candidateIndex)
{
    uint requestBaseWord = requestIndex * (uint(SIZEOF_GPU_DDGI_PROBE_UPDATE_REQUEST) / 4u);
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PROBE_INDEX) / 4u, ReadDdgiProbeCandidateWord(candidateIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_PROBE_INDEX));
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_VOLUME_INDEX) / 4u, ReadDdgiProbeCandidateWord(candidateIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_VOLUME_INDEX));
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FLAGS) / 4u, ReadDdgiProbeCandidateWord(candidateIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_REASON_FLAGS));
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PRIORITY) / 4u, ReadDdgiProbeCandidateWord(candidateIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_PRIORITY));
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_X) / 4u, ReadDdgiProbeCandidateWord(candidateIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_LOGICAL_CELL_X));
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Y) / 4u, ReadDdgiProbeCandidateWord(candidateIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_LOGICAL_CELL_Y));
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Z) / 4u, ReadDdgiProbeCandidateWord(candidateIndex, OFFSET_GPU_DDGI_PROBE_CANDIDATE_LOGICAL_CELL_Z));
    WriteStorageWord(uint(DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX), requestBaseWord + 7u, 0u);
}

#endif
