using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiSchedulerTransportCategory : byte
{
    HardSourceRepair = 0,
    RoutineSourceValidation = 1,
    CachedSolverPropagation = 2,
    OrdinaryMaintenance = 3,
    Count = 4
}

public enum SimpleDdgiSchedulerRayTier : byte
{
    Full = 0,
    Maintenance = 1,
    Count = 2
}

[Flags]
public enum SimpleDdgiSchedulerCandidateReason : ushort
{
    None = 0,
    Fresh = 1 << 0,
    ScrollExposed = 1 << 1,
    RegionalDirty = 1 << 2,
    GlobalDirty = 1 << 3,
    Visible = 1 << 4,
    Retry = 1 << 5,
    RelocationRetry = 1 << 6,
    SourceCacheInvalid = 1 << 7,
    RoutineDue = 1 << 8,
    ConvergencePending = 1 << 9,
    InactiveRetry = 1 << 10
}

/// <summary>
/// Shared integer packing helpers for the scheduler ABI.  Every packed field
/// has a bounded range and is validated before it can reach an indirect command
/// or a queue record.
/// </summary>
public static class SimpleDdgiSchedulerAbi
{
    public const uint InvalidCandidateProbeIndex = uint.MaxValue;
    public const int WorkClassBits = 3;
    public const int TransportCategoryBits = 2;
    public const int MaxLaneCount = GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
        (int)SimpleDdgiSchedulerWorkClass.Count *
        (int)SimpleDdgiSchedulerTransportCategory.Count *
        (int)SimpleDdgiSchedulerRayTier.Count;
    public const int MaxRayBucketCount = 6;
    public const uint PhysicalGenerationMask = 0x00ff_ffffu;
    public const uint UpdateRayCountShift = 16u;
    public const uint UpdateRayCountMask = 0xffff_0000u;
    public const uint UpdateMaintenanceFlag = 1u << 12;
    public const uint UpdateSourceRefreshFlag = 1u << 13;
    public const uint UpdateInvalidateFlag = 1u << 14;
    public const uint UpdateRoutineSourceRefreshFlag = 1u << 15;
    public const uint UpdateFreshFlag = 1u << 0;
    public const uint UpdateScrollExposedFlag = 1u << 1;
    public const uint UpdateMaterialCascadeMask = 0x7u << 3;
    public const uint UpdateMaxLightsMask = 0x3fu << 6;
    public const uint SchedulerFeatureGpuResident = 1u << 0;
    public const uint SchedulerFeatureGpuMirror = 1u << 1;
    public const uint SchedulerFeatureTransportV2 = 1u << 2;
    public const uint SchedulerFeatureToroidalScrolling = 1u << 3;
    public const uint SchedulerFeatureAtlasFresh = 1u << 4;
    public const uint SchedulerFeatureGlobalConvergence = 1u << 5;
    public const uint SchedulerFeatureDirtyOverflow = 1u << 6;
    public const uint SchedulerFeatureClassification = 1u << 7;
    public const uint SchedulerFeatureSampledPublication = 1u << 8;
    // Tail certification makes cached-solver admission deliberately ignore
    // the retired local residual/stable-generation heuristic. The GPU still
    // tracks those values for diagnostics, but fairness and the frozen audit
    // own V2 retirement.
    public const uint SchedulerFeatureTransportTailCertification = 1u << 9;
    public const uint ReasonFresh = (uint)SimpleDdgiSchedulerCandidateReason.Fresh;
    public const uint ReasonScrollExposed = (uint)SimpleDdgiSchedulerCandidateReason.ScrollExposed;
    public const uint ReasonRegionalDirty = (uint)SimpleDdgiSchedulerCandidateReason.RegionalDirty;
    public const uint ProbeMetadataVisible = 1u << 16;
    public const uint ProbeMetadataPublished = 1u << 17;
    // A rejected resident transaction leaves this private repair marker set so
    // the next classifier pass re-admits a source refresh without mutating the
    // public probe record.
    public const uint ProbeMetadataRepair = 1u << 30;

    // GPUSimpleDdgiSchedulerProbeState.PackedTransportAndLifecycle:
    // source rays [0,8], transport generation [9,16], stable count [17,24],
    // routine state [25,27], fail-closed transaction state [28,31].
    public const int SourceRayCountShift = 0;
    public const uint SourceRayCountMask = 0x1ffu;
    public const int TransportGenerationShift = 9;
    public const uint TransportGenerationMask = 0xffu << TransportGenerationShift;
    public const int StableUpdateCountShift = 17;
    public const uint StableUpdateCountMask = 0xffu << StableUpdateCountShift;
    public const int RoutineMaintenanceShift = 25;
    public const uint RoutineMaintenanceMask = 0x7u << RoutineMaintenanceShift;
    public const int TransactionStatusShift = 28;
    public const uint TransactionStatusMask = 0xfu << TransactionStatusShift;

    public static int GetLaneIndex(
        int volumeIndex,
        SimpleDdgiSchedulerWorkClass workClass,
        SimpleDdgiSchedulerTransportCategory transport,
        SimpleDdgiSchedulerRayTier rayTier)
    {
        if ((uint)volumeIndex >= GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            throw new ArgumentOutOfRangeException(nameof(volumeIndex));
        if ((uint)workClass >= (uint)SimpleDdgiSchedulerWorkClass.Count)
            throw new ArgumentOutOfRangeException(nameof(workClass));
        if ((uint)transport >= (uint)SimpleDdgiSchedulerTransportCategory.Count)
            throw new ArgumentOutOfRangeException(nameof(transport));
        if ((uint)rayTier >= (uint)SimpleDdgiSchedulerRayTier.Count)
            throw new ArgumentOutOfRangeException(nameof(rayTier));

        return ((((volumeIndex * (int)SimpleDdgiSchedulerWorkClass.Count) +
                    (int)workClass) * (int)SimpleDdgiSchedulerTransportCategory.Count +
                (int)transport) * (int)SimpleDdgiSchedulerRayTier.Count) +
            (int)rayTier;
    }

    public static void DecodeLaneIndex(
        int laneIndex,
        out int volumeIndex,
        out SimpleDdgiSchedulerWorkClass workClass,
        out SimpleDdgiSchedulerTransportCategory transport,
        out SimpleDdgiSchedulerRayTier rayTier)
    {
        if ((uint)laneIndex >= MaxLaneCount)
            throw new ArgumentOutOfRangeException(nameof(laneIndex));

        rayTier = (SimpleDdgiSchedulerRayTier)(laneIndex %
            (int)SimpleDdgiSchedulerRayTier.Count);
        int remainder = laneIndex / (int)SimpleDdgiSchedulerRayTier.Count;
        transport = (SimpleDdgiSchedulerTransportCategory)(remainder %
            (int)SimpleDdgiSchedulerTransportCategory.Count);
        remainder /= (int)SimpleDdgiSchedulerTransportCategory.Count;
        workClass = (SimpleDdgiSchedulerWorkClass)(remainder %
            (int)SimpleDdgiSchedulerWorkClass.Count);
        volumeIndex = remainder / (int)SimpleDdgiSchedulerWorkClass.Count;
    }

    public static uint PackCandidateWorkClassAndTransport(
        SimpleDdgiSchedulerWorkClass workClass,
        SimpleDdgiSchedulerTransportCategory transport)
    {
        if ((uint)workClass >= (uint)SimpleDdgiSchedulerWorkClass.Count)
            throw new ArgumentOutOfRangeException(nameof(workClass));
        if ((uint)transport >= (uint)SimpleDdgiSchedulerTransportCategory.Count)
            throw new ArgumentOutOfRangeException(nameof(transport));
        return (uint)workClass | ((uint)transport << WorkClassBits);
    }

    public static void UnpackCandidateWorkClassAndTransport(
        uint packed,
        out SimpleDdgiSchedulerWorkClass workClass,
        out SimpleDdgiSchedulerTransportCategory transport)
    {
        workClass = (SimpleDdgiSchedulerWorkClass)(packed & ((1u << WorkClassBits) - 1u));
        transport = (SimpleDdgiSchedulerTransportCategory)((packed >> WorkClassBits) &
            ((1u << TransportCategoryBits) - 1u));
    }

    public static uint PackCandidateRayTierAndReasons(
        SimpleDdgiSchedulerRayTier rayTier,
        SimpleDdgiSchedulerCandidateReason reasons)
    {
        if ((uint)rayTier >= (uint)SimpleDdgiSchedulerRayTier.Count)
            throw new ArgumentOutOfRangeException(nameof(rayTier));
        return (uint)rayTier | ((uint)reasons << 8);
    }

    public static void UnpackCandidateRayTierAndReasons(
        uint packed,
        out SimpleDdgiSchedulerRayTier rayTier,
        out SimpleDdgiSchedulerCandidateReason reasons)
    {
        rayTier = (SimpleDdgiSchedulerRayTier)(packed & 0xffu);
        reasons = (SimpleDdgiSchedulerCandidateReason)((packed >> 8) & ushort.MaxValue);
    }

    public static uint PackProbeUpdateMetadata(uint physicalGeneration, uint age)
    {
        uint generation = physicalGeneration & PhysicalGenerationMask;
        if (generation == 0)
            throw new ArgumentOutOfRangeException(nameof(physicalGeneration),
                "A queued Simple-DDGI update must carry a non-zero physical generation.");
        return generation | (Math.Min(age, 0xffu) << 24);
    }

    public static uint PackSchedulerProbeLifecycle(
        uint sourceRayCount,
        uint completedTransportGeneration,
        uint stableUpdateCount,
        uint routineMaintenanceState,
        uint transactionStatus)
    {
        if (sourceRayCount > 0x1ffu)
            throw new ArgumentOutOfRangeException(nameof(sourceRayCount));
        if (completedTransportGeneration > 0xffu)
            throw new ArgumentOutOfRangeException(nameof(completedTransportGeneration));
        if (stableUpdateCount > 0xffu)
            throw new ArgumentOutOfRangeException(nameof(stableUpdateCount));
        if (routineMaintenanceState > 0x7u)
            throw new ArgumentOutOfRangeException(nameof(routineMaintenanceState));
        if (transactionStatus > 0xfu)
            throw new ArgumentOutOfRangeException(nameof(transactionStatus));

        return (sourceRayCount << SourceRayCountShift) |
            (completedTransportGeneration << TransportGenerationShift) |
            (stableUpdateCount << StableUpdateCountShift) |
            (routineMaintenanceState << RoutineMaintenanceShift) |
            (transactionStatus << TransactionStatusShift);
    }

    public static void UnpackSchedulerProbeLifecycle(
        uint packed,
        out uint sourceRayCount,
        out uint completedTransportGeneration,
        out uint stableUpdateCount,
        out uint routineMaintenanceState,
        out uint transactionStatus)
    {
        sourceRayCount = packed & SourceRayCountMask;
        completedTransportGeneration = (packed & TransportGenerationMask) >> TransportGenerationShift;
        stableUpdateCount = (packed & StableUpdateCountMask) >> StableUpdateCountShift;
        routineMaintenanceState = (packed & RoutineMaintenanceMask) >> RoutineMaintenanceShift;
        transactionStatus = (packed & TransactionStatusMask) >> TransactionStatusShift;
    }

    /// <summary>
    /// CPU-side mirror of CommitLocal's fail-closed outcome predicate. It is
    /// intentionally pure so fault-injection and delayed-generation tests can
    /// exercise the same acceptance contract without a Vulkan device.
    /// </summary>
    public static bool OutcomeCanCommit(
        in GPUSimpleDdgiUpdateOutcome outcome,
        uint queueTransactionGeneration,
        uint schedulerResourceGeneration,
        uint volumeTableGeneration,
        uint sourceLightingGeneration,
        uint transportGeneration,
        uint currentPhysicalGeneration)
    {
        return outcome.QueueTransactionGeneration == queueTransactionGeneration &&
            outcome.SchedulerResourceGeneration == schedulerResourceGeneration &&
            outcome.VolumeTableGeneration == volumeTableGeneration &&
            outcome.SourceLightingGeneration == sourceLightingGeneration &&
            outcome.TransportGeneration == transportGeneration &&
            outcome.ExpectedPhysicalGeneration != 0u &&
            outcome.ExpectedPhysicalGeneration == currentPhysicalGeneration &&
            outcome.FailureReason == 0u &&
            (outcome.CompletionMask & outcome.RequiredCompletionMask) ==
                outcome.RequiredCompletionMask;
    }
}

public readonly record struct SimpleDdgiCpuVolumePolicy(
    int ProbeCapacity,
    int MinimumQuota,
    int PreferredMaximumQuota,
    int SchedulingWeight,
    bool Active)
{
    public SimpleDdgiCpuVolumePolicy Normalize()
    {
        int capacity = Math.Max(0, ProbeCapacity);
        int minimum = Math.Clamp(MinimumQuota, 0, capacity);
        int maximum = Math.Clamp(PreferredMaximumQuota, minimum, capacity);
        return this with
        {
            ProbeCapacity = capacity,
            MinimumQuota = minimum,
            PreferredMaximumQuota = maximum,
            SchedulingWeight = Math.Max(0, SchedulingWeight)
        };
    }
}

public readonly record struct SimpleDdgiCpuSchedulePolicy(
    int RequestBudget,
    ulong PrimaryRayBudget,
    ulong SourceCohortRayBudget,
    uint SourceLightingGeneration,
    int ActiveVolumeCount,
    bool DeterministicFixedBudget);

public readonly record struct SimpleDdgiCpuScheduleResult(
    int ConsideredCandidateCount,
    int EligibleCandidateCount,
    int AcceptedRequestCount,
    ulong AcceptedPrimaryRayCount,
    ulong AcceptedSourceRayCount,
    int RequestBudgetRejectedCount,
    int PrimaryRayBudgetRejectedCount,
    int SourceCohortRejectedCount,
    int InvalidCandidateCount,
    bool Overflowed,
    bool InvalidSchedule)
{
    public bool IsValid => !InvalidSchedule && !Overflowed;
}

public readonly record struct SimpleDdgiRayBucket(
    uint RaysPerProbe,
    uint QueueOffset,
    uint ProbeCount)
{
    public uint TraceGroupCount => SimpleDdgiIndirectDispatchMath.RayGroupCount(ProbeCount, RaysPerProbe);
}

public static class SimpleDdgiIndirectDispatchMath
{
    public const uint TraceLocalSize = 64;
    public const uint RelocateLocalSize = 64;

    public static uint RayGroupCount(uint probeCount, uint raysPerProbe)
    {
        if (probeCount == 0 || raysPerProbe == 0)
            return 0;
        ulong rays = checked((ulong)probeCount * raysPerProbe);
        return checked((uint)((rays + TraceLocalSize - 1u) / TraceLocalSize));
    }

    public static uint RequestThreadGroupCount(uint requestCount) =>
        requestCount == 0 ? 0u : (requestCount + RelocateLocalSize - 1u) / RelocateLocalSize;

    public static uint ProbeWorkgroupCount(uint requestCount) => requestCount;

    public static GPUSimpleDdgiDispatchIndirectCommand BuildRayBucketCommand(
        uint probeCount,
        uint raysPerProbe) => new()
    {
        GroupCountX = RayGroupCount(probeCount, raysPerProbe),
        GroupCountY = 1u,
        GroupCountZ = 1u,
        Reserved = 0u
    };

    public static GPUSimpleDdgiDispatchIndirectCommand BuildRequestCommand(
        uint requestCount) => new()
    {
        GroupCountX = RequestThreadGroupCount(requestCount),
        GroupCountY = 1u,
        GroupCountZ = 1u,
        Reserved = 0u
    };

    public static GPUSimpleDdgiDispatchIndirectCommand BuildProbeCommand(
        uint requestCount) => new()
    {
        GroupCountX = ProbeWorkgroupCount(requestCount),
        GroupCountY = 1u,
        GroupCountZ = 1u,
        Reserved = 0u
    };

    public static GPUSimpleDdgiDispatchIndirectCommand BuildFeedbackCommand() => new()
    {
        GroupCountX = 1u,
        GroupCountY = 1u,
        GroupCountZ = 1u,
        Reserved = 0u
    };

    public static int DeduplicateRayBuckets(
        ReadOnlySpan<uint> rayCounts,
        Span<uint> uniqueRayCounts)
    {
        if (uniqueRayCounts.Length < SimpleDdgiSchedulerAbi.MaxRayBucketCount)
            throw new ArgumentException("The destination must hold all six Simple-DDGI ray buckets.", nameof(uniqueRayCounts));

        int count = 0;
        for (int i = 0; i < rayCounts.Length; i++)
        {
            uint value = rayCounts[i];
            if (value == 0)
                continue;
            bool duplicate = false;
            for (int existing = 0; existing < count; existing++)
            {
                if (uniqueRayCounts[existing] == value)
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
                continue;
            if (count == SimpleDdgiSchedulerAbi.MaxRayBucketCount)
                throw new InvalidOperationException("Simple-DDGI resolved more than six ray buckets.");
            uniqueRayCounts[count++] = value;
        }

        uniqueRayCounts[count..SimpleDdgiSchedulerAbi.MaxRayBucketCount].Clear();
        return count;
    }
}

/// <summary>
/// Allocation-free CPU oracle for the GPU lane/admission policy.  It is used in
/// CpuReference validation and GpuMirror; GpuResident never calls it on the
/// render thread.
/// </summary>
public static class SimpleDdgiCpuScheduleModel
{
    public static SimpleDdgiCpuScheduleResult Schedule(
        ReadOnlySpan<GPUSimpleDdgiSchedulerCandidate> candidates,
        ReadOnlySpan<SimpleDdgiCpuVolumePolicy> volumePolicies,
        SimpleDdgiCpuSchedulePolicy policy,
        Span<GPUSimpleDdgiProbeUpdate> outputQueue,
        Span<int> laneCandidateCounts,
        Span<int> laneAcceptedCounts,
        Span<uint> laneCursors)
    {
        if (laneCandidateCounts.Length < SimpleDdgiSchedulerAbi.MaxLaneCount ||
            laneAcceptedCounts.Length < SimpleDdgiSchedulerAbi.MaxLaneCount ||
            laneCursors.Length < SimpleDdgiSchedulerAbi.MaxLaneCount)
        {
            return new SimpleDdgiCpuScheduleResult(
                candidates.Length, 0, 0, 0, 0, 0, 0, 0, 0, false, true);
        }

        outputQueue.Clear();
        laneCandidateCounts[..SimpleDdgiSchedulerAbi.MaxLaneCount].Clear();
        laneAcceptedCounts[..SimpleDdgiSchedulerAbi.MaxLaneCount].Clear();

        // Resolve every candidate once.  The old reference implementation
        // intentionally scanned the candidate stream once per lane, which is
        // useful as a very simple oracle but becomes needlessly expensive when
        // a capture contains many probes.  Keeping the resolved lane in a
        // bounded stack buffer preserves the exact ordering while making the
        // model O(candidateCount + laneCount * candidateCount) only for the
        // small admission passes.
        Span<int> candidateLanes = stackalloc int[candidates.Length];
        candidateLanes.Fill(-1);
        Span<byte> admittedCandidates = stackalloc byte[candidates.Length];

        int considered = 0;
        int eligible = 0;
        int invalid = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            GPUSimpleDdgiSchedulerCandidate candidate = candidates[i];
            if (!candidate.IsValid)
                continue;
            considered++;
            if (!TryResolveLane(candidate, volumePolicies, out int laneIndex, out _, out _, out _, out _))
            {
                invalid++;
                continue;
            }

            candidateLanes[i] = laneIndex;
            laneCandidateCounts[laneIndex]++;
            eligible++;
        }

        int requestBudget = Math.Clamp(policy.RequestBudget, 0, Math.Min(outputQueue.Length, candidates.Length));
        if (requestBudget == 0 || eligible == 0)
        {
            return new SimpleDdgiCpuScheduleResult(
                considered, eligible, 0, 0, 0, 0, 0, 0, invalid, false, false);
        }

        Span<int> quotas = stackalloc int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        Span<int> volumeUsage = stackalloc int[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        Span<int> pendingByVolumeClass = stackalloc int[
            GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
            (int)SimpleDdgiSchedulerWorkClass.Count];
        Span<int> classReservations = stackalloc int[pendingByVolumeClass.Length];
        Span<int> classUsage = stackalloc int[pendingByVolumeClass.Length];
        BuildVolumeQuotas(volumePolicies, requestBudget, quotas);
        for (int lane = 0; lane < SimpleDdgiSchedulerAbi.MaxLaneCount; lane++)
        {
            SimpleDdgiSchedulerAbi.DecodeLaneIndex(
                lane,
                out int volumeIndex,
                out SimpleDdgiSchedulerWorkClass workClass,
                out _,
                out _);
            int pendingIndex = volumeIndex * (int)SimpleDdgiSchedulerWorkClass.Count +
                (int)workClass;
            pendingByVolumeClass[pendingIndex] += laneCandidateCounts[lane];
        }
        int activeVolumes = Math.Clamp(
            policy.ActiveVolumeCount,
            0,
            Math.Min(volumePolicies.Length, GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount));
        for (int volumeIndex = 0; volumeIndex < activeVolumes; volumeIndex++)
        {
            int pendingBase = volumeIndex * (int)SimpleDdgiSchedulerWorkClass.Count;
            SimpleDdgiVolumeManager.AllocateSchedulerClassQuotas(
                volumeIndex < quotas.Length ? quotas[volumeIndex] : 0,
                pendingByVolumeClass.Slice(
                    pendingBase,
                    (int)SimpleDdgiSchedulerWorkClass.Count),
                classReservations.Slice(
                    pendingBase,
                    (int)SimpleDdgiSchedulerWorkClass.Count));
        }

        int accepted = 0;
        ulong primaryRays = 0;
        ulong sourceRays = 0;
        int requestRejected = 0;
        int primaryRejected = 0;
        int sourceRejected = 0;

        // Keep the same eleven phases as the production CPU queue builder:
        // per-volume class reservations, visible urgent classes, source cohort,
        // routine source validation, cached solver, ring maintenance, then a
        // deterministic return of unused reservations.
        for (int workClass = 0; workClass <= (int)SimpleDdgiSchedulerWorkClass.VisibleRetry; workClass++)
        {
            AdmitCandidates(
                candidates,
                candidateLanes,
                volumePolicies,
                policy,
                outputQueue,
                laneCandidateCounts,
                laneAcceptedCounts,
                laneCursors,
                admittedCandidates,
                quotas,
                volumeUsage,
                classReservations,
                classUsage,
                workClassFilter: (SimpleDdgiSchedulerWorkClass)workClass,
                categoryFilter: SimpleDdgiSchedulerTransportCategory.HardSourceRepair,
                reservedPass: true,
                requestBudget: requestBudget,
                ref accepted,
                ref primaryRays,
                ref sourceRays,
                ref requestRejected,
                ref primaryRejected,
                ref sourceRejected);
        }

        AdmitCandidates(
            candidates, candidateLanes, volumePolicies, policy, outputQueue,
            laneCandidateCounts, laneAcceptedCounts, laneCursors, admittedCandidates,
            quotas, volumeUsage, classReservations, classUsage,
            workClassFilter: null,
            categoryFilter: SimpleDdgiSchedulerTransportCategory.HardSourceRepair,
            reservedPass: true,
            requestBudget: requestBudget,
            ref accepted, ref primaryRays, ref sourceRays,
            ref requestRejected, ref primaryRejected, ref sourceRejected);

        AdmitCandidates(
            candidates, candidateLanes, volumePolicies, policy, outputQueue,
            laneCandidateCounts, laneAcceptedCounts, laneCursors, admittedCandidates,
            quotas, volumeUsage, classReservations, classUsage,
            workClassFilter: null,
            categoryFilter: SimpleDdgiSchedulerTransportCategory.RoutineSourceValidation,
            reservedPass: true,
            requestBudget: requestBudget,
            ref accepted, ref primaryRays, ref sourceRays,
            ref requestRejected, ref primaryRejected, ref sourceRejected);

        AdmitCandidates(
            candidates, candidateLanes, volumePolicies, policy, outputQueue,
            laneCandidateCounts, laneAcceptedCounts, laneCursors, admittedCandidates,
            quotas, volumeUsage, classReservations, classUsage,
            workClassFilter: null,
            categoryFilter: SimpleDdgiSchedulerTransportCategory.CachedSolverPropagation,
            reservedPass: true,
            requestBudget: requestBudget,
            ref accepted, ref primaryRays, ref sourceRays,
            ref requestRejected, ref primaryRejected, ref sourceRejected);

        for (int workClass = (int)SimpleDdgiSchedulerWorkClass.NearMaintenance;
             workClass <= (int)SimpleDdgiSchedulerWorkClass.FarMaintenance;
             workClass++)
        {
            AdmitCandidates(
                candidates,
                candidateLanes,
                volumePolicies,
                policy,
                outputQueue,
                laneCandidateCounts,
                laneAcceptedCounts,
                laneCursors,
                admittedCandidates,
                quotas,
                volumeUsage,
                classReservations,
                classUsage,
                workClassFilter: (SimpleDdgiSchedulerWorkClass)workClass,
                categoryFilter: null,
                reservedPass: true,
                requestBudget: requestBudget,
                ref accepted,
                ref primaryRays,
                ref sourceRays,
                ref requestRejected,
                ref primaryRejected,
                ref sourceRejected);
        }

        // Return unused reservations in the same deterministic policy order.
        for (int workClass = 0; workClass < (int)SimpleDdgiSchedulerWorkClass.Count; workClass++)
        {
            AdmitCandidates(
                candidates,
                candidateLanes,
                volumePolicies,
                policy,
                outputQueue,
                laneCandidateCounts,
                laneAcceptedCounts,
                laneCursors,
                admittedCandidates,
                quotas,
                volumeUsage,
                classReservations,
                classUsage,
                workClassFilter: (SimpleDdgiSchedulerWorkClass)workClass,
                categoryFilter: null,
                reservedPass: false,
                requestBudget: requestBudget,
                ref accepted,
                ref primaryRays,
                ref sourceRays,
                ref requestRejected,
                ref primaryRejected,
                ref sourceRejected);
        }

        return new SimpleDdgiCpuScheduleResult(
            considered,
            eligible,
            accepted,
            primaryRays,
            sourceRays,
            requestRejected,
            primaryRejected,
            sourceRejected,
            invalid,
            false,
            false);
    }

    private static void AdmitCandidates(
        ReadOnlySpan<GPUSimpleDdgiSchedulerCandidate> candidates,
        ReadOnlySpan<int> candidateLanes,
        ReadOnlySpan<SimpleDdgiCpuVolumePolicy> volumePolicies,
        SimpleDdgiCpuSchedulePolicy policy,
        Span<GPUSimpleDdgiProbeUpdate> outputQueue,
        Span<int> laneCandidateCounts,
        Span<int> laneAcceptedCounts,
        Span<uint> laneCursors,
        Span<byte> admittedCandidates,
        Span<int> quotas,
        Span<int> volumeUsage,
        ReadOnlySpan<int> classReservations,
        Span<int> classUsage,
        SimpleDdgiSchedulerWorkClass? workClassFilter,
        SimpleDdgiSchedulerTransportCategory? categoryFilter,
        bool reservedPass,
        int requestBudget,
        ref int acceptedCount,
        ref ulong acceptedPrimaryRays,
        ref ulong acceptedSourceRays,
        ref int requestBudgetRejected,
        ref int primaryBudgetRejected,
        ref int sourceBudgetRejected)
    {
        if (acceptedCount >= requestBudget)
            return;

        for (int workClass = 0; workClass < (int)SimpleDdgiSchedulerWorkClass.Count; workClass++)
        {
            if (workClassFilter.HasValue &&
                (int)workClassFilter.Value != workClass)
            {
                continue;
            }

            for (int category = 0; category < (int)SimpleDdgiSchedulerTransportCategory.Count; category++)
            {
                if (categoryFilter.HasValue &&
                    (int)categoryFilter.Value != category)
                {
                    continue;
                }

                for (int rayTier = 0; rayTier < (int)SimpleDdgiSchedulerRayTier.Count; rayTier++)
                {
                    for (int volumeIndex = 0; volumeIndex < volumePolicies.Length; volumeIndex++)
                    {
                        if (volumeIndex >= GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
                            break;

                        int laneIndex = SimpleDdgiSchedulerAbi.GetLaneIndex(
                            volumeIndex,
                            (SimpleDdgiSchedulerWorkClass)workClass,
                            (SimpleDdgiSchedulerTransportCategory)category,
                            (SimpleDdgiSchedulerRayTier)rayTier);
                        int laneCount = laneCandidateCounts[laneIndex];
                        if (laneCount == 0)
                            continue;

                        int start = (int)(laneCursors[laneIndex] % (uint)laneCount);
                        int acceptedFromLane = 0;
                        bool stop = false;
                        for (int pass = 0; pass < 2 && !stop; pass++)
                        {
                            int rankStart = pass == 0 ? start : 0;
                            int rankEnd = pass == 0 ? laneCount : start;
                            if (rankStart >= rankEnd)
                                continue;

                            int rank = 0;
                            for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
                            {
                                if (candidateLanes[candidateIndex] != laneIndex)
                                    continue;

                                if (rank < rankStart || rank >= rankEnd)
                                {
                                    rank++;
                                    continue;
                                }
                                rank++;

                                if (admittedCandidates[candidateIndex] != 0)
                                    continue;
                                if (acceptedCount >= requestBudget || outputQueue.Length <= acceptedCount)
                                {
                                    requestBudgetRejected++;
                                    stop = true;
                                    break;
                                }

                                GPUSimpleDdgiSchedulerCandidate candidate = candidates[candidateIndex];
                                int candidateVolume = (int)candidate.VolumeIndex;
                                if ((uint)candidateVolume >= (uint)quotas.Length ||
                                    volumeUsage[candidateVolume] >= quotas[candidateVolume])
                                {
                                    continue;
                                }

                                SimpleDdgiSchedulerAbi.UnpackCandidateWorkClassAndTransport(
                                    candidate.WorkClassAndTransport,
                                    out SimpleDdgiSchedulerWorkClass candidateClass,
                                    out SimpleDdgiSchedulerTransportCategory candidateCategory);
                                SimpleDdgiSchedulerAbi.UnpackCandidateRayTierAndReasons(
                                    candidate.RayTierAndReasonFlags,
                                    out SimpleDdgiSchedulerRayTier candidateRayTier,
                                    out SimpleDdgiSchedulerCandidateReason candidateReasons);
                                if (candidateClass != (SimpleDdgiSchedulerWorkClass)workClass ||
                                    (int)candidateCategory != category ||
                                    (int)candidateRayTier != rayTier)
                                {
                                    continue;
                                }

                                int classBase = candidateVolume * (int)SimpleDdgiSchedulerWorkClass.Count;
                                int classLimit = reservedPass
                                    ? classReservations[classBase + workClass]
                                    : quotas[candidateVolume];
                                if (classUsage[classBase + workClass] >= classLimit)
                                    continue;

                                uint activeRays = Math.Clamp(
                                    candidate.ActiveRayCount,
                                    1u,
                                    (uint)GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
                                bool sourceWork = candidateCategory is
                                    SimpleDdgiSchedulerTransportCategory.HardSourceRepair or
                                    SimpleDdgiSchedulerTransportCategory.RoutineSourceValidation;
                                // The resident candidate storage derives the
                                // source cohort from the volume policy. The
                                // CPU mirror receives that same cardinality in
                                // the full candidate ABI; use it when present
                                // and retain the active-ray fallback for small
                                // hand-authored fixtures.
                                uint sourceRays = sourceWork
                                    ? Math.Clamp(
                                        candidate.SourceRayCount == 0
                                            ? activeRays
                                            : candidate.SourceRayCount,
                                        1u,
                                        (uint)GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe)
                                    : 0u;
                                ulong primaryCost = sourceWork ? activeRays : 0UL;
                                ulong sourceCost = sourceWork ? sourceRays : 0UL;
                                if (primaryCost > policy.PrimaryRayBudget ||
                                    acceptedPrimaryRays > policy.PrimaryRayBudget - primaryCost)
                                {
                                    primaryBudgetRejected++;
                                    // A candidate rejected by a hard ray
                                    // budget is consumed for this frame. The
                                    // GPU admission pass invalidates the same
                                    // compact candidate, preventing a second
                                    // rejection during the return phase.
                                    admittedCandidates[candidateIndex] = 1;
                                    continue;
                                }

                                ulong sourceBudget = policy.SourceCohortRayBudget == 0
                                    ? ulong.MaxValue
                                    : policy.SourceCohortRayBudget;
                                if (sourceCost > sourceBudget ||
                                    acceptedSourceRays > sourceBudget - sourceCost)
                                {
                                    sourceBudgetRejected++;
                                    admittedCandidates[candidateIndex] = 1;
                                    continue;
                                }

                                uint flags = 0;
                                if ((candidateReasons & SimpleDdgiSchedulerCandidateReason.Fresh) != 0)
                                    flags |= SimpleDdgiSchedulerAbi.UpdateFreshFlag;
                                if ((candidateReasons & SimpleDdgiSchedulerCandidateReason.ScrollExposed) != 0)
                                    flags |= SimpleDdgiSchedulerAbi.UpdateScrollExposedFlag;
                                if (candidateRayTier == SimpleDdgiSchedulerRayTier.Maintenance)
                                    flags |= SimpleDdgiSchedulerAbi.UpdateMaintenanceFlag;
                                if ((candidateReasons & (
                                        SimpleDdgiSchedulerCandidateReason.Fresh |
                                        SimpleDdgiSchedulerCandidateReason.ScrollExposed |
                                        SimpleDdgiSchedulerCandidateReason.RegionalDirty |
                                        SimpleDdgiSchedulerCandidateReason.GlobalDirty |
                                        SimpleDdgiSchedulerCandidateReason.SourceCacheInvalid |
                                        SimpleDdgiSchedulerCandidateReason.RelocationRetry)) != 0)
                                {
                                    flags |= SimpleDdgiSchedulerAbi.UpdateInvalidateFlag;
                                }
                                if (sourceWork)
                                {
                                    flags |= SimpleDdgiSchedulerAbi.UpdateSourceRefreshFlag;
                                    if (candidateCategory == SimpleDdgiSchedulerTransportCategory.RoutineSourceValidation)
                                        flags |= SimpleDdgiSchedulerAbi.UpdateRoutineSourceRefreshFlag;
                                }
                                flags |= activeRays << (int)SimpleDdgiSchedulerAbi.UpdateRayCountShift;
                                outputQueue[acceptedCount] = new GPUSimpleDdgiProbeUpdate
                                {
                                    ProbeIndex = candidate.ProbeIndex,
                                    VolumeIndex = candidate.VolumeIndex,
                                    Flags = flags,
                                    Reserved0 = SimpleDdgiSchedulerAbi.PackProbeUpdateMetadata(
                                        candidate.ExpectedPhysicalGeneration,
                                        candidate.SequenceOrdinal),
                                    SourceRayCount = sourceRays,
                                    SourceLightingGeneration = sourceWork
                                        ? policy.SourceLightingGeneration
                                        : 0u,
                                    OutcomeIndex = (uint)acceptedCount
                                };
                                admittedCandidates[candidateIndex] = 1;
                                acceptedCount++;
                                volumeUsage[candidateVolume]++;
                                classUsage[classBase + workClass]++;
                                acceptedPrimaryRays = checked(acceptedPrimaryRays + primaryCost);
                                acceptedSourceRays = checked(acceptedSourceRays + sourceCost);
                                acceptedFromLane++;

                                if (acceptedCount >= requestBudget)
                                {
                                    stop = true;
                                    break;
                                }
                            }
                        }

                        if (acceptedFromLane > 0)
                        {
                            // A cursor is advanced only by successful
                            // admissions. Empty or rejected lanes retain the
                            // previous persistent position.
                            laneCursors[laneIndex] = (uint)((start + acceptedFromLane) % laneCount);
                            laneAcceptedCounts[laneIndex] += acceptedFromLane;
                        }
                        if (stop)
                            return;
                    }
                }
            }
        }
    }

    private static bool TryResolveLane(
        GPUSimpleDdgiSchedulerCandidate candidate,
        ReadOnlySpan<SimpleDdgiCpuVolumePolicy> volumes,
        out int laneIndex,
        out int volumeIndex,
        out SimpleDdgiSchedulerWorkClass workClass,
        out SimpleDdgiSchedulerTransportCategory category,
        out SimpleDdgiSchedulerRayTier rayTier)
    {
        volumeIndex = (int)candidate.VolumeIndex;
        SimpleDdgiSchedulerAbi.UnpackCandidateWorkClassAndTransport(
            candidate.WorkClassAndTransport, out workClass, out category);
        SimpleDdgiSchedulerAbi.UnpackCandidateRayTierAndReasons(
            candidate.RayTierAndReasonFlags, out rayTier, out _);
        laneIndex = -1;
        if (!candidate.IsValid || (uint)volumeIndex >= (uint)volumes.Length ||
            !volumes[volumeIndex].Normalize().Active ||
            (uint)workClass >= (uint)SimpleDdgiSchedulerWorkClass.Count ||
            (uint)category >= (uint)SimpleDdgiSchedulerTransportCategory.Count ||
            (uint)rayTier >= (uint)SimpleDdgiSchedulerRayTier.Count ||
            (candidate.WorkClassAndTransport & 0xFFFFFFC0u) != 0u ||
            (candidate.RayTierAndReasonFlags & 0xFF000000u) != 0u ||
            candidate.ExpectedPhysicalGeneration == 0 ||
            (candidate.ExpectedPhysicalGeneration & ~SimpleDdgiSchedulerAbi.PhysicalGenerationMask) != 0 ||
            candidate.ActiveRayCount == 0 ||
            candidate.ActiveRayCount > GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe ||
            candidate.SourceRayCount > GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe)
        {
            return false;
        }

        laneIndex = SimpleDdgiSchedulerAbi.GetLaneIndex(volumeIndex, workClass, category, rayTier);
        return true;
    }

    private static void BuildVolumeQuotas(
        ReadOnlySpan<SimpleDdgiCpuVolumePolicy> sourceVolumes,
        int requestBudget,
        Span<int> quotas)
    {
        quotas.Clear();
        Span<SimpleDdgiCpuVolumePolicy> volumes = stackalloc SimpleDdgiCpuVolumePolicy[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        int count = Math.Min(sourceVolumes.Length, volumes.Length);
        int minimumTotal = 0;
        for (int i = 0; i < count; i++)
        {
            volumes[i] = sourceVolumes[i].Normalize();
            if (!volumes[i].Active)
                continue;
            quotas[i] = volumes[i].MinimumQuota;
            minimumTotal = checked(minimumTotal + quotas[i]);
        }

        if (minimumTotal > requestBudget)
        {
            // A tiny budget cannot satisfy every authored minimum. Reduce from
            // the lowest-priority/last volume first, preserving deterministic
            // ring ordering and never overflowing the budget.
            int overflow = minimumTotal - requestBudget;
            for (int i = count - 1; i >= 0 && overflow > 0; i--)
            {
                int reduction = Math.Min(quotas[i], overflow);
                quotas[i] -= reduction;
                overflow -= reduction;
            }
            return;
        }

        int remaining = requestBudget - minimumTotal;
        for (int quotaPass = 0; quotaPass < 2 && remaining > 0; quotaPass++)
        {
            while (remaining > 0)
            {
                int weightTotal = 0;
                for (int i = 0; i < count; i++)
                {
                    int limit = quotaPass == 0
                        ? volumes[i].PreferredMaximumQuota
                        : volumes[i].ProbeCapacity;
                    if (volumes[i].Active && quotas[i] < limit)
                        weightTotal = checked(weightTotal + Math.Max(1, volumes[i].SchedulingWeight));
                }
                if (weightTotal == 0)
                    break;

                int distributed = 0;
                int bestIndex = -1;
                long bestRemainder = long.MinValue;
                for (int i = 0; i < count; i++)
                {
                    int limit = quotaPass == 0
                        ? volumes[i].PreferredMaximumQuota
                        : volumes[i].ProbeCapacity;
                    if (!volumes[i].Active || quotas[i] >= limit)
                        continue;
                    int available = limit - quotas[i];
                    int weight = Math.Max(1, volumes[i].SchedulingWeight);
                    int share = (int)Math.Min(available, ((long)remaining * weight) / weightTotal);
                    if (share > 0)
                    {
                        quotas[i] += share;
                        distributed += share;
                    }
                    long remainder = ((long)remaining * weight) % weightTotal;
                    if (remainder > bestRemainder)
                    {
                        bestRemainder = remainder;
                        bestIndex = i;
                    }
                }
                remaining -= distributed;
                if (remaining == 0)
                    break;
                if (bestIndex < 0)
                {
                    break;
                }
                quotas[bestIndex]++;
                remaining--;
            }
        }
    }
}
