using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiBlendSweepWork(
    bool WritesIrradiance,
    bool WritesVisibility,
    bool AdvancesOneUpdateLifecycle);

public enum SimpleDdgiTailCertificationFallbackReason : byte
{
    None = 0,
    DisabledByConfiguration = 1,
    RequiresGpuResidentScheduler = 2,
    GpuSchedulerNotReady = 3,
    GpuSchedulerFrameExecutionUnavailable = 4
}

public readonly record struct SimpleDdgiTailCertificationAvailability(
    bool Enabled,
    SimpleDdgiTailCertificationFallbackReason Reason)
{
    public string Message => Reason switch
    {
        SimpleDdgiTailCertificationFallbackReason.None => string.Empty,
        SimpleDdgiTailCertificationFallbackReason.DisabledByConfiguration =>
            "Tail certification is disabled by configuration.",
        SimpleDdgiTailCertificationFallbackReason.RequiresGpuResidentScheduler =>
            "Tail certification requires the GpuResident Simple-DDGI scheduler; CPU reference and GPU mirror modes use uncertified fallback convergence.",
        SimpleDdgiTailCertificationFallbackReason.GpuSchedulerNotReady =>
            "Tail certification is pending because the GpuResident scheduler resources are not ready.",
        SimpleDdgiTailCertificationFallbackReason.GpuSchedulerFrameExecutionUnavailable =>
            "Tail certification is disabled because GpuResident scheduler frame execution is unavailable.",
        _ => "Tail certification is unavailable for an unknown reason."
    };
}

/// <summary>
/// The scheduler-facing state machine for error-bounded V2 transport.  It is
/// intentionally independent of Vulkan objects so generation invalidation and
/// complete-epoch rules can be exercised without a device.
/// </summary>
public sealed class SimpleDdgiTransportSolveController
{
    private uint[] _participantVisitEpoch;
    private int _expectedParticipantCount;
    private int _visitedParticipantCount;
    private uint _solveEpoch;
    private uint _auditEpoch;
    private bool _auditCancelled;

    public SimpleDdgiTransportSolveController(int participantCapacity = 0)
    {
        _participantVisitEpoch = participantCapacity > 0
            ? new uint[participantCapacity]
            : Array.Empty<uint>();
        Phase = SimpleDdgiTransportPhase.SourceRepair;
        LastReason = SimpleDdgiTransportCertificationReason.SourceRepairRequired;
        LastSummary = SimpleDdgiTransportTailSummary.Empty;
    }

    public SimpleDdgiTransportPhase Phase { get; private set; }
    public SimpleDdgiTransportCertificationReason LastReason { get; private set; }
    public SimpleDdgiTransportTailSummary LastSummary { get; private set; }
    public SimpleDdgiTransportGenerations FrozenGenerations { get; private set; }
    public uint SolveEpoch => _solveEpoch;
    public uint AuditEpoch => _auditEpoch;
    public int ExpectedParticipantCount => _expectedParticipantCount;
    public int VisitedParticipantCount => _visitedParticipantCount;
    public int ParticipantVisitCapacity => _participantVisitEpoch.Length;
    public bool IsSolveEpochComplete =>
        Phase == SimpleDdgiTransportPhase.AcceleratedSolve &&
        _visitedParticipantCount == _expectedParticipantCount;
    public bool IsCertified => Phase == SimpleDdgiTransportPhase.Certified && LastSummary.IsCertified;

    /// <summary>
    /// CPU mirror of the blend shader's per-sweep side-effect policy. Cached
    /// sweeps always advance irradiance, while visibility and transaction
    /// lifecycle work are restricted to sweep zero's first color.
    /// </summary>
    public static SimpleDdgiBlendSweepWork ResolveBlendSweepWork(
        int sweepIndex,
        bool isFirstColor,
        bool transportV2Active,
        bool requiresSourceRefresh,
        bool freshUpdate)
    {
        if (sweepIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sweepIndex));

        bool firstSweepFirstColor = sweepIndex == 0 && isFirstColor;
        bool visibility = !transportV2Active ||
            (firstSweepFirstColor && (requiresSourceRefresh || freshUpdate));
        return new SimpleDdgiBlendSweepWork(
            WritesIrradiance: true,
            WritesVisibility: visibility,
            AdvancesOneUpdateLifecycle: !transportV2Active || firstSweepFirstColor);
    }

    public static SimpleDdgiTailCertificationAvailability ResolveTailCertificationAvailability(
        bool requested,
        SimpleDdgiSchedulerMode schedulerMode,
        bool gpuSchedulerReady,
        bool gpuSchedulerFrameExecutionAvailable)
    {
        if (!requested)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.DisabledByConfiguration);
        }

        if (schedulerMode != SimpleDdgiSchedulerMode.GpuResident)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.RequiresGpuResidentScheduler);
        }

        if (!gpuSchedulerReady)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.GpuSchedulerNotReady);
        }

        if (!gpuSchedulerFrameExecutionAvailable)
        {
            return new SimpleDdgiTailCertificationAvailability(
                false,
                SimpleDdgiTailCertificationFallbackReason.GpuSchedulerFrameExecutionUnavailable);
        }

        return new SimpleDdgiTailCertificationAvailability(
            true,
            SimpleDdgiTailCertificationFallbackReason.None);
    }

    /// <summary>
    /// Starts a source-repair transaction and invalidates any certificate that
    /// belongs to the old source or ownership generations.
    /// </summary>
    public void BeginSourceRepair(SimpleDdgiTransportGenerations generations)
    {
        FrozenGenerations = generations;
        _expectedParticipantCount = 0;
        _visitedParticipantCount = 0;
        _auditCancelled = false;
        Phase = SimpleDdgiTransportPhase.SourceRepair;
        LastReason = SimpleDdgiTransportCertificationReason.SourceRepairRequired;
        LastSummary = SimpleDdgiTransportTailSummary.Empty with
        {
            Generations = FrozenGenerations
        };
    }

    /// <summary>
    /// Starts a complete solve epoch.  Every participant must be visited once
    /// before <see cref="TryBeginAudit"/> can succeed; zero participants is a
    /// valid empty field only when the caller explicitly supplies zero.
    /// </summary>
    public bool BeginSolveEpoch(SimpleDdgiTransportGenerations generations, int expectedParticipantCount)
    {
        if (expectedParticipantCount < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedParticipantCount));

        if (Phase == SimpleDdgiTransportPhase.AuditFrozen)
        {
            LastReason = SimpleDdgiTransportCertificationReason.AuditInProgress;
            return false;
        }

        EnsureParticipantCapacity(expectedParticipantCount);
        _expectedParticipantCount = expectedParticipantCount;
        AdvanceSolveEpoch(generations);
        LastReason = expectedParticipantCount == 0
            ? SimpleDdgiTransportCertificationReason.SolveEpochIncomplete
            : SimpleDdgiTransportCertificationReason.None;
        LastSummary = SimpleDdgiTransportTailSummary.Empty with
        {
            Generations = FrozenGenerations
        };
        return true;
    }

    /// <summary>
    /// Marks one active participant as visited by the current accelerated
    /// epoch.  Duplicate visits are rejected so a malformed queue cannot make
    /// an incomplete field appear complete.
    /// </summary>
    public bool MarkParticipantVisited(int participantIndex, SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            !TryRefreshSolveGenerations(generations) ||
            (uint)participantIndex >= (uint)_participantVisitEpoch.Length)
        {
            LastReason = !IsSolveGenerationCompatible(generations, FrozenGenerations)
                ? SimpleDdgiTransportCertificationReason.GenerationsChanged
                : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        uint stamp = _solveEpoch;
        if (_participantVisitEpoch[participantIndex] == stamp)
        {
            LastReason = SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        _participantVisitEpoch[participantIndex] = stamp;
        _visitedParticipantCount++;
        if (_visitedParticipantCount != _expectedParticipantCount)
            LastReason = SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
        return true;
    }

    /// <summary>
    /// Advances the mutable canonical/queue observations while an epoch is
    /// solving. Those generations naturally change after each publication and
    /// queue transaction; source/operator/ownership changes do not belong to
    /// this method and invalidate the epoch instead.
    /// </summary>
    public bool TryRefreshSolveGenerations(SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            !IsSolveGenerationCompatible(generations, FrozenGenerations))
        {
            return false;
        }

        FrozenGenerations = generations;
        LastSummary = LastSummary with { Generations = generations };
        return true;
    }

    /// <summary>
    /// Completes the visit witness produced by the GPU-resident scheduler.
    /// The scheduler has already reduced the per-probe stamps, so the host
    /// must not synthesize individual visits from a delayed queue summary.
    /// Requiring the exact epoch, generation, and participant count prevents
    /// a stale feedback packet from authorizing an audit for a different field.
    /// </summary>
    public bool MarkGpuEpochComplete(
        uint solveEpoch,
        int participantCount,
        SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            solveEpoch == 0u ||
            solveEpoch != _solveEpoch ||
            generations != FrozenGenerations ||
            participantCount < 0 ||
            participantCount != _expectedParticipantCount)
        {
            LastReason = generations != FrozenGenerations
                ? SimpleDdgiTransportCertificationReason.GenerationsChanged
                : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        _visitedParticipantCount = participantCount;
        LastReason = participantCount > 0
            ? SimpleDdgiTransportCertificationReason.None
            : SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
        return IsSolveEpochComplete;
    }

    /// <summary>
    /// Freezes the resource generations for the audit.  No queue publication or
    /// source refresh is allowed to mutate this snapshot while the audit is in
    /// flight.
    /// </summary>
    public bool TryBeginAudit(SimpleDdgiTransportGenerations generations)
    {
        if (Phase != SimpleDdgiTransportPhase.AcceleratedSolve ||
            !IsSolveEpochComplete)
        {
            LastReason = SimpleDdgiTransportCertificationReason.SolveEpochIncomplete;
            return false;
        }

        if (!TryRefreshSolveGenerations(generations))
        {
            LastReason = SimpleDdgiTransportCertificationReason.GenerationsChanged;
            return false;
        }

        _auditEpoch = NextNonZero(_auditEpoch);
        _auditCancelled = false;
        FrozenGenerations = FrozenGenerations with { Audit = _auditEpoch };
        LastSummary = LastSummary with { Generations = FrozenGenerations };
        Phase = SimpleDdgiTransportPhase.AuditFrozen;
        LastReason = SimpleDdgiTransportCertificationReason.AuditInProgress;
        return true;
    }

    /// <summary>
    /// Accepts a summary only when it describes the exact frozen epoch and has
    /// complete participant/texel coverage.  A failed audit returns to solving
    /// and never leaves a stale certificate active.
    /// </summary>
    public bool TryAcceptAudit(
        SimpleDdgiTransportTailSummary summary,
        SimpleDdgiTransportGenerations currentGenerations)
    {
        if (Phase != SimpleDdgiTransportPhase.AuditFrozen || _auditCancelled)
        {
            LastReason = SimpleDdgiTransportCertificationReason.AuditInProgress;
            return false;
        }

        if (summary.AuditEpoch != _auditEpoch ||
            summary.Generations != FrozenGenerations ||
            currentGenerations != FrozenGenerations)
        {
            CancelAudit(SimpleDdgiTransportCertificationReason.GenerationsChanged);
            return false;
        }

        if (!summary.HasExactParticipantCoverage || !summary.HasExactTexelCoverage)
        {
            LastSummary = summary with { Reason = SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete };
            LastReason = SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete;
            Phase = SimpleDdgiTransportPhase.AcceleratedSolve;
            return false;
        }

        if (!summary.HasFiniteEvidence)
        {
            LastSummary = summary with { Reason = SimpleDdgiTransportCertificationReason.NonFiniteEvidence };
            LastReason = SimpleDdgiTransportCertificationReason.NonFiniteEvidence;
            Phase = SimpleDdgiTransportPhase.AcceleratedSolve;
            return false;
        }

        if (summary.Reason != SimpleDdgiTransportCertificationReason.Certified ||
            !summary.IsCertified)
        {
            LastSummary = summary;
            LastReason = summary.CanonicalQuantizationFloor > summary.Tolerance
                ? SimpleDdgiTransportCertificationReason.QuantizationLimited
                : SimpleDdgiTransportCertificationReason.TailAboveTolerance;
            // A finite, complete audit above tolerance is useful evidence, but
            // it does not authorize another audit of the byte-identical field.
            // Start a distinct epoch and clear its visit witness so every probe
            // receives another cached solve before certification is attempted.
            if (LastReason == SimpleDdgiTransportCertificationReason.TailAboveTolerance)
                AdvanceSolveEpoch(currentGenerations);
            else
                Phase = SimpleDdgiTransportPhase.AcceleratedSolve;
            return false;
        }

        LastSummary = summary;
        LastReason = SimpleDdgiTransportCertificationReason.Certified;
        Phase = SimpleDdgiTransportPhase.Certified;
        return true;
    }

    /// <summary>
    /// Cancels a frozen audit.  Publication, generation changes, source-cache
    /// invalidation, and non-finite evidence all use this path.
    /// </summary>
    public void CancelAudit(SimpleDdgiTransportCertificationReason reason)
    {
        if (Phase == SimpleDdgiTransportPhase.AuditFrozen)
        {
            _auditCancelled = true;
            Phase = SimpleDdgiTransportPhase.AcceleratedSolve;
        }

        LastReason = reason;
        LastSummary = LastSummary with { Reason = reason };
    }

    public void EnterTracking()
    {
        Phase = SimpleDdgiTransportPhase.Tracking;
        LastReason = SimpleDdgiTransportCertificationReason.Tracking;
        LastSummary = LastSummary with { Reason = LastReason };
    }

    /// <summary>
    /// Invalidates the field from any phase.  The caller supplies the new
    /// generation snapshot so the next source repair cannot accidentally reuse
    /// a certificate from an older physical ownership mapping.
    /// </summary>
    public void Invalidate(
        SimpleDdgiTransportGenerations generations,
        SimpleDdgiTransportCertificationReason reason,
        bool requireSourceRepair)
    {
        FrozenGenerations = generations;
        _expectedParticipantCount = 0;
        _visitedParticipantCount = 0;
        _auditCancelled = Phase == SimpleDdgiTransportPhase.AuditFrozen;
        Phase = requireSourceRepair
            ? SimpleDdgiTransportPhase.SourceRepair
            : SimpleDdgiTransportPhase.AcceleratedSolve;
        LastReason = reason;
        LastSummary = LastSummary with
        {
            Generations = generations,
            Reason = reason,
            IsComplete = false
        };
    }

    public static int ResolveLogicalParity(
        int localProbeIndex,
        int gridCountX,
        int gridCountY,
        int gridCountZ,
        int physicalOffsetX,
        int physicalOffsetY,
        int physicalOffsetZ)
    {
        ResolveLogicalCoordinate(
            localProbeIndex,
            gridCountX,
            gridCountY,
            gridCountZ,
            physicalOffsetX,
            physicalOffsetY,
            physicalOffsetZ,
            out int logicalX,
            out int logicalY,
            out int logicalZ);
        return (logicalX + logicalY + logicalZ) & 1;
    }

    /// <summary>
    /// Mirrors the shader's toroidal ownership mapping.  Parity must be based
    /// on this logical coordinate, never on a physical array index.
    /// </summary>
    public static void ResolveLogicalCoordinate(
        int localProbeIndex,
        int gridCountX,
        int gridCountY,
        int gridCountZ,
        int physicalOffsetX,
        int physicalOffsetY,
        int physicalOffsetZ,
        out int logicalX,
        out int logicalY,
        out int logicalZ)
    {
        if (gridCountX <= 0 || gridCountY <= 0 || gridCountZ <= 0)
            throw new ArgumentOutOfRangeException("Grid counts must be positive.");
        int layerSize = checked(gridCountX * gridCountY);
        int physicalZ = localProbeIndex / layerSize;
        int remainder = localProbeIndex - physicalZ * layerSize;
        int physicalY = remainder / gridCountX;
        int physicalX = remainder - physicalY * gridCountX;
        if ((uint)physicalZ >= (uint)gridCountZ)
            throw new ArgumentOutOfRangeException(nameof(localProbeIndex));

        logicalX = PositiveModulo(physicalX - physicalOffsetX, gridCountX);
        logicalY = PositiveModulo(physicalY - physicalOffsetY, gridCountY);
        logicalZ = PositiveModulo(physicalZ - physicalOffsetZ, gridCountZ);
    }

    public static int ResolveColor(int localProbeIndex, int gridCountX, int gridCountY, int gridCountZ,
        int physicalOffsetX, int physicalOffsetY, int physicalOffsetZ, int startingColor)
    {
        int parity = ResolveLogicalParity(
            localProbeIndex, gridCountX, gridCountY, gridCountZ,
            physicalOffsetX, physicalOffsetY, physicalOffsetZ);
        return (parity ^ (startingColor & 1)) & 1;
    }

    /// <summary>
    /// Orders volumes from the coarsest/farthest operator to the finest/near
    /// operator.  Lower fallback priority wins before the stable volume index
    /// tie-breaker, making dispatch order reproducible across frames.
    /// </summary>
    public static void OrderVolumes(
        ReadOnlySpan<SimpleDdgiTransportVolumeOrderKey> keys,
        Span<int> orderedVolumeIndices)
    {
        if (orderedVolumeIndices.Length < keys.Length)
            throw new ArgumentException("The destination span is too small.", nameof(orderedVolumeIndices));

        for (int i = 0; i < keys.Length; i++)
        {
            int candidate = keys[i].VolumeIndex;
            int insertAt = i;
            while (insertAt > 0 && Compare(keys[FindKeyIndex(keys, orderedVolumeIndices[insertAt - 1])], keys[i]) > 0)
            {
                orderedVolumeIndices[insertAt] = orderedVolumeIndices[insertAt - 1];
                insertAt--;
            }
            orderedVolumeIndices[insertAt] = candidate;
        }
    }

    public static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    public void EnsureParticipantCapacity(int required)
    {
        if (required < 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (required <= _participantVisitEpoch.Length)
            return;
        Array.Resize(ref _participantVisitEpoch, required);
    }

    private void AdvanceSolveEpoch(SimpleDdgiTransportGenerations generations)
    {
        uint previousSolveEpoch = _solveEpoch;
        _solveEpoch = NextNonZero(_solveEpoch);
        if (_solveEpoch <= previousSolveEpoch)
        {
            // Visit stamps are meaningful only within the current 32-bit
            // epoch namespace. Clear the bounded table on wrap so a visit
            // from an old epoch 1 cannot authorize the new epoch 1.
            Array.Clear(_participantVisitEpoch);
        }

        _visitedParticipantCount = 0;
        _auditCancelled = false;
        FrozenGenerations = generations with
        {
            Solve = _solveEpoch,
            Audit = NonZeroGeneration(_auditEpoch)
        };
        Phase = SimpleDdgiTransportPhase.AcceleratedSolve;
    }

    private static uint NextNonZero(uint value)
    {
        value++;
        return value == 0u ? 1u : value;
    }

    private static int Compare(SimpleDdgiTransportVolumeOrderKey left, SimpleDdgiTransportVolumeOrderKey right)
    {
        int comparison = right.Spacing.CompareTo(left.Spacing);
        if (comparison != 0)
            return comparison;
        comparison = left.FallbackPriority.CompareTo(right.FallbackPriority);
        if (comparison != 0)
            return comparison;
        comparison = left.OuterPriority.CompareTo(right.OuterPriority);
        if (comparison != 0)
            return comparison;
        return left.VolumeIndex.CompareTo(right.VolumeIndex);
    }

    private static int FindKeyIndex(ReadOnlySpan<SimpleDdgiTransportVolumeOrderKey> keys, int volumeIndex)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].VolumeIndex == volumeIndex)
                return i;
        }
        throw new InvalidOperationException("Volume order contains an unknown volume index.");
    }

    private static bool IsSolveGenerationCompatible(
        SimpleDdgiTransportGenerations current,
        SimpleDdgiTransportGenerations frozen) =>
        current.VolumeTable == frozen.VolumeTable &&
        current.PhysicalOwnership == frozen.PhysicalOwnership &&
        current.SourceLighting == frozen.SourceLighting &&
        current.SourceEpoch == frozen.SourceEpoch &&
        current.TransportOperator == frozen.TransportOperator &&
        current.Solve == frozen.Solve &&
        current.SchedulerResources == frozen.SchedulerResources;

    private static uint NonZeroGeneration(uint value) => value == 0u ? 1u : value;
}

public readonly record struct SimpleDdgiTransportVolumeOrderKey(
    int VolumeIndex,
    float Spacing,
    int FallbackPriority = 0,
    int OuterPriority = 0);

public readonly record struct SimpleDdgiTransportSolveParticipant(
    int ParticipantIndex,
    int VolumeIndex,
    int LocalProbeIndex,
    int GridCountX,
    int GridCountY,
    int GridCountZ,
    int PhysicalOffsetX,
    int PhysicalOffsetY,
    int PhysicalOffsetZ)
{
    public int LogicalParity => SimpleDdgiTransportSolveController.ResolveLogicalParity(
        LocalProbeIndex,
        GridCountX,
        GridCountY,
        GridCountZ,
        PhysicalOffsetX,
        PhysicalOffsetY,
        PhysicalOffsetZ);
}
