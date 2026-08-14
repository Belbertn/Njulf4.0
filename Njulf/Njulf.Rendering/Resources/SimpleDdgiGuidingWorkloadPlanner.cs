using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Header-validated identity of the guide currently readable for one physical
/// probe.  It never exposes a candidate write bank.
/// </summary>
public readonly record struct SimpleDdgiGuidingReadableProbeIdentity(
    uint PhysicalProbeIndex,
    uint VirtualProbeId,
    uint PageGeneration,
    uint DistributionGeneration,
    uint ProposalEpoch,
    int ReadBankIndex)
{
    public bool IsReadable => DistributionGeneration != 0u &&
        ProposalEpoch != 0u && ReadBankIndex is 0 or 1;
}

/// <summary>
/// Immutable CPU-scheduler projection of one current DDGI update.  Only full
/// source traces generate new training evidence; cached relights and ordinary
/// solver work are intentionally excluded by <see cref="IsFullSourceTrace"/>.
/// </summary>
public readonly record struct SimpleDdgiGuidingFrameProbe(
    uint QueueOffset,
    uint VirtualProbeId,
    uint PhysicalProbeIndex,
    uint PageGeneration,
    ulong StableProbeId,
    uint SourceEpoch,
    uint SourceLightingGeneration,
    uint ContentRevision,
    uint ActiveRayCount,
    bool IsFullSourceTrace,
    SimpleDdgiGuidingReadableProbeIdentity ReadableGuide);

/// <summary>Bounded rotation and estimator policy for the initial C3 profile.</summary>
public readonly record struct SimpleDdgiGuidingProposalPolicy(
    uint MinimumProposalEpochFrames,
    float MaterialTotalVariationThreshold,
    float MaximumGuidedRotationFraction,
    float UniformMixtureFraction)
{
    public static SimpleDdgiGuidingProposalPolicy ProductionBaseline { get; } =
        new(
            MinimumProposalEpochFrames: 24u,
            MaterialTotalVariationThreshold: 0.05f,
            MaximumGuidedRotationFraction: 0.25f,
            UniformMixtureFraction: 0.25f);

    public void Validate()
    {
        if (MinimumProposalEpochFrames is < 16u or > 4_096u)
            throw new ArgumentOutOfRangeException(nameof(MinimumProposalEpochFrames));
        if (!float.IsFinite(MaterialTotalVariationThreshold) ||
            MaterialTotalVariationThreshold is < 0.0f or > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaterialTotalVariationThreshold));
        }
        if (!float.IsFinite(MaximumGuidedRotationFraction) ||
            MaximumGuidedRotationFraction <= 0.0f ||
            MaximumGuidedRotationFraction > 0.25f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumGuidedRotationFraction),
                "The initial C3 profile may rotate at most 25% of guided slots per epoch.");
        }
        if (!float.IsFinite(UniformMixtureFraction) ||
            UniformMixtureFraction <
                SimpleDdgiDirectionalGuidingExperiment.MinimumUniformFraction ||
            UniformMixtureFraction > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(UniformMixtureFraction));
        }
    }
}

public readonly record struct SimpleDdgiGuidingProposalEpochPlan(
    ulong Serial,
    uint CurrentEpoch,
    uint TargetEpoch,
    ulong FrameSerial,
    bool AdvancesEpoch)
{
    public bool IsValid => Serial != 0UL && TargetEpoch != 0u;
}

/// <summary>
/// Transactional global proposal-epoch controller.  A materially changed guide
/// can request an advance only after the minimum stability interval.  Failed
/// builds abort the plan; wrap is rejected so an old source-cache identity can
/// never become current again through ABA.
/// </summary>
public sealed class SimpleDdgiGuidingProposalEpochController
{
    private readonly SimpleDdgiGuidingProposalPolicy _policy;
    private uint _publishedEpoch;
    private ulong _publishedFrameSerial;
    private ulong _nextSerial;
    private SimpleDdgiGuidingProposalEpochPlan? _pending;

    public SimpleDdgiGuidingProposalEpochController(
        in SimpleDdgiGuidingProposalPolicy policy)
    {
        policy.Validate();
        _policy = policy;
    }

    public uint PublishedEpoch => _publishedEpoch;

    public bool TryPlan(
        ulong frameSerial,
        float maximumTotalVariation,
        out SimpleDdgiGuidingProposalEpochPlan plan,
        out string reason)
    {
        if (!float.IsFinite(maximumTotalVariation) ||
            maximumTotalVariation < 0.0f)
        {
            plan = default;
            reason = "guiding-proposal-total-variation-invalid";
            return false;
        }
        if (_pending.HasValue)
        {
            plan = default;
            reason = "guiding-proposal-epoch-transaction-already-pending";
            return false;
        }
        if (_publishedEpoch != 0u && frameSerial < _publishedFrameSerial)
        {
            plan = default;
            reason = "guiding-proposal-frame-serial-regressed";
            return false;
        }

        uint target = _publishedEpoch == 0u ? 1u : _publishedEpoch;
        bool elapsed = _publishedEpoch != 0u &&
            frameSerial - _publishedFrameSerial >=
                _policy.MinimumProposalEpochFrames;
        bool advance = elapsed && maximumTotalVariation >=
            _policy.MaterialTotalVariationThreshold;
        if (advance)
        {
            if (_publishedEpoch == uint.MaxValue)
            {
                plan = default;
                reason = "guiding-proposal-epoch-exhausted-recreate-required";
                return false;
            }
            target = _publishedEpoch + 1u;
        }

        _nextSerial = _nextSerial == ulong.MaxValue ? 1UL : _nextSerial + 1UL;
        plan = new(
            _nextSerial,
            _publishedEpoch,
            target,
            frameSerial,
            advance);
        _pending = plan;
        reason = advance
            ? "guiding-proposal-epoch-advance-planned"
            : _publishedEpoch == 0u
                ? "guiding-proposal-bootstrap-planned"
                : "guiding-proposal-epoch-retained";
        return true;
    }

    public bool Commit(
        in SimpleDdgiGuidingProposalEpochPlan plan,
        out string reason)
    {
        if (!_pending.HasValue || !_pending.Value.Equals(plan) || !plan.IsValid)
        {
            reason = "guiding-proposal-epoch-plan-mismatch";
            return false;
        }
        bool bootstrap = _publishedEpoch == 0u;
        _publishedEpoch = plan.TargetEpoch;
        if (plan.AdvancesEpoch || bootstrap)
            _publishedFrameSerial = plan.FrameSerial;
        _pending = null;
        reason = "guiding-proposal-epoch-committed";
        return true;
    }

    public bool Abort(in SimpleDdgiGuidingProposalEpochPlan plan)
    {
        if (!_pending.HasValue || !_pending.Value.Equals(plan))
            return false;
        _pending = null;
        return true;
    }

    public void Reset()
    {
        _publishedEpoch = 0u;
        _publishedFrameSerial = 0UL;
        _pending = null;
    }
}

/// <summary>
/// One CPU-side commit witness for a completed sample dispatch.  The planner
/// advances its per-slot ownership state only after the caller has observed a
/// fence-complete zero-error validation counter set.
/// </summary>
public readonly record struct SimpleDdgiGuidingSampleCommit(
    uint PhysicalProbeIndex,
    uint VirtualProbeId,
    uint PageGeneration,
    ulong StableProbeId,
    uint ProposalEpoch,
    bool CompletePayloadSet);

public readonly record struct SimpleDdgiGuidingWorkloadCounts(
    int GuidedProbeCount,
    uint TrainingRecordCount,
    int TrainingWorkItemCount,
    int BuildWorkItemCount,
    int ExpectedHeaderCount,
    int SampleRequestCount,
    int SampleCommitCount,
    int BootstrapProbeCount,
    int RotatedGuidedSlotCount);

public readonly record struct SimpleDdgiGuidingWorkloadCompileResult(
    bool Compiled,
    SimpleDdgiGuidingWorkloadCounts Counts,
    string Reason)
{
    public static SimpleDdgiGuidingWorkloadCompileResult Rejected(
        string reason) => new(false, default, reason);
}

/// <summary>
/// Allocation-free C3 frame compiler.  It consumes immutable scheduler facts,
/// emits strictly ordered work, preserves maintenance rays, and limits proposal
/// rotation without ever making a partial output look valid.
/// </summary>
public sealed class SimpleDdgiGuidingWorkloadPlanner
{
    private readonly SimpleDdgiGuidingLayout _layout;
    private readonly SimpleDdgiGuidingProposalPolicy _policy;
    private readonly PayloadOwnerState[] _payloadOwners;

    public SimpleDdgiGuidingWorkloadPlanner(
        in SimpleDdgiGuidingLayout layout,
        in SimpleDdgiGuidingProposalPolicy policy)
    {
        policy.Validate();
        if (layout.AbiVersion != SimpleDdgiGuidingGpuAbi.Version ||
            !layout.HasTransportSidecar || layout.PhysicalProbeCapacity <= 0 ||
            layout.ScheduledGuidedProbeCapacity <= 0 ||
            layout.DirectionSlotsPerProbe <= 0)
        {
            throw new ArgumentException(
                "A complete production C3 transport layout is required.",
                nameof(layout));
        }
        _layout = layout;
        _policy = policy;
        _payloadOwners = new PayloadOwnerState[layout.PhysicalProbeCapacity];
    }

    public SimpleDdgiGuidingWorkloadCompileResult TryCompile(
        in SimpleDdgiGuidingBuildToken buildToken,
        ReadOnlySpan<SimpleDdgiGuidingFrameProbe> frameProbes,
        Span<SimpleDdgiGuidingFrameProbe> selectedProbeScratch,
        Span<GPUSimpleDdgiGuidingTrainingWorkItem> trainingWorkItems,
        Span<GPUSimpleDdgiGuidingBuildWorkItem> buildWorkItems,
        Span<SimpleDdgiGuidingExpectedProbeHeader> expectedHeaders,
        Span<GPUSimpleDdgiGuidingSampleRequest> sampleRequests,
        Span<SimpleDdgiGuidingSampleCommit> sampleCommits)
    {
        if (buildToken.IsDefault ||
            buildToken.GuidingAbiVersion != SimpleDdgiGuidingGpuAbi.Version ||
            buildToken.LeafResolution != _layout.LeafResolution ||
            buildToken.CandidateBankGeneration == 0u ||
            buildToken.TargetProposalEpoch == 0u)
        {
            return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                "guiding-workload-build-token-invalid");
        }

        int selectedCount = 0;
        foreach (ref readonly SimpleDdgiGuidingFrameProbe probe in frameProbes)
        {
            if (!probe.IsFullSourceTrace ||
                probe.PhysicalProbeIndex >=
                    (uint)_layout.PhysicalProbeCapacity)
            {
                continue;
            }
            int selectionCapacity = Math.Min(
                _layout.ScheduledGuidedProbeCapacity,
                selectedProbeScratch.Length);
            if (selectionCapacity == 0)
            {
                return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                    "guiding-workload-output-probe-capacity-insufficient");
            }
            if (!ValidateProbe(probe, out string probeReason))
                return SimpleDdgiGuidingWorkloadCompileResult.Rejected(probeReason);
            if (selectedCount < selectionCapacity)
            {
                selectedProbeScratch[selectedCount++] = probe;
                continue;
            }

            // Memory pressure admits a deterministic physical-probe prefix.
            // Keep the lowest physical identities instead of rejecting the
            // whole frame or inheriting scheduler queue order as sampling bias.
            int largestIndex = 0;
            for (int index = 1; index < selectedCount; index++)
            {
                if (selectedProbeScratch[index].PhysicalProbeIndex >
                    selectedProbeScratch[largestIndex].PhysicalProbeIndex)
                {
                    largestIndex = index;
                }
            }
            if (probe.PhysicalProbeIndex <
                selectedProbeScratch[largestIndex].PhysicalProbeIndex)
            {
                selectedProbeScratch[largestIndex] = probe;
            }
        }

        if (selectedCount == 0)
        {
            return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                "guiding-workload-has-no-full-source-trace");
        }

        Span<SimpleDdgiGuidingFrameProbe> selected =
            selectedProbeScratch[..selectedCount];
        selected.Sort(static (left, right) =>
            left.PhysicalProbeIndex.CompareTo(right.PhysicalProbeIndex));
        for (int index = 1; index < selected.Length; index++)
        {
            if (selected[index - 1].PhysicalProbeIndex ==
                selected[index].PhysicalProbeIndex)
            {
                return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                    "guiding-workload-physical-probe-duplicated");
            }
        }

        if (trainingWorkItems.Length < selectedCount ||
            buildWorkItems.Length < selectedCount ||
            expectedHeaders.Length < selectedCount ||
            sampleCommits.Length < selectedCount)
        {
            return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                "guiding-workload-output-probe-capacity-insufficient");
        }

        int requiredSampleRequests = 0;
        int requiredSampleCommits = 0;
        int bootstrapProbeCount = 0;
        int rotatedSlotCount = 0;
        foreach (ref readonly SimpleDdgiGuidingFrameProbe probe in selected)
        {
            if (!TryResolveSampleSelection(
                    probe,
                    out SampleSelection selection,
                    out string selectionReason))
            {
                return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                    selectionReason);
            }
            requiredSampleRequests = checked(
                requiredSampleRequests + selection.RequestCount);
            if (selection.RequestCount > 0)
            {
                requiredSampleCommits++;
                if (selection.CompletePayloadSet)
                    bootstrapProbeCount++;
                else
                    rotatedSlotCount = checked(
                        rotatedSlotCount + selection.RequestCount);
            }
        }
        if (sampleRequests.Length < requiredSampleRequests ||
            sampleCommits.Length < requiredSampleCommits)
        {
            return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                "guiding-workload-output-sample-capacity-insufficient");
        }

        uint recordOffset = 0u;
        int sampleRequestOffset = 0;
        int sampleCommitOffset = 0;
        int leafCount = _layout.LeafCount;
        for (int probeIndex = 0; probeIndex < selected.Length; probeIndex++)
        {
            ref readonly SimpleDdgiGuidingFrameProbe probe =
                ref selected[probeIndex];
            uint recordCount = probe.ActiveRayCount;
            uint partialOffset = checked((uint)(probeIndex * leafCount));
            uint sourceDistributionGeneration = probe.ReadableGuide.IsReadable
                ? probe.ReadableGuide.DistributionGeneration
                : 1u;
            uint sourceProposalEpoch = probe.ReadableGuide.IsReadable
                ? probe.ReadableGuide.ProposalEpoch
                : buildToken.TargetProposalEpoch;
            uint rayResultBaseIndex = checked(
                probe.QueueOffset * (uint)_layout.DirectionSlotsPerProbe);

            trainingWorkItems[probeIndex] = new()
            {
                PhysicalProbeIndex = probe.PhysicalProbeIndex,
                VirtualProbeId = probe.VirtualProbeId,
                PageGeneration = probe.PageGeneration,
                SourceDistributionGeneration = sourceDistributionGeneration,
                DirectionProposalEpoch = sourceProposalEpoch,
                RecordOffset = recordOffset,
                RecordCount = recordCount,
                PartialOffset = partialOffset,
                ExpectedContentRevision = probe.ContentRevision,
                QueueOffset = probe.QueueOffset,
                RayResultBaseIndex = rayResultBaseIndex,
                DirectionSlotsPerProbe =
                    checked((uint)_layout.DirectionSlotsPerProbe),
                SourceEpoch = probe.SourceEpoch,
                SourceLightingGeneration = probe.SourceLightingGeneration
            };
            buildWorkItems[probeIndex] = new()
            {
                PhysicalProbeIndex = probe.PhysicalProbeIndex,
                VirtualProbeId = probe.VirtualProbeId,
                PageGeneration = probe.PageGeneration,
                PreviousDistributionGeneration = probe.ReadableGuide.IsReadable
                    ? probe.ReadableGuide.DistributionGeneration
                    : 0u,
                TargetDistributionGeneration =
                    buildToken.CandidateBankGeneration,
                TargetProposalEpoch = buildToken.TargetProposalEpoch,
                PartialOffset = partialOffset,
                PartialCount = 1u,
                SampleCountAndAge = PackSampleCountAndAge(recordCount, 0u),
                ExpectedContentRevision = probe.ContentRevision,
                Flags = 0u,
                Reserved = 0u
            };
            expectedHeaders[probeIndex] = new(
                probe.PhysicalProbeIndex,
                probe.VirtualProbeId,
                probe.PageGeneration);
            recordOffset = checked(recordOffset + recordCount);

            if (!TryResolveSampleSelection(
                    probe,
                    out SampleSelection selection,
                    out string selectionReason))
            {
                return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                    selectionReason);
            }
            if (selection.RequestCount == 0)
                continue;
            int firstRequest = sampleRequestOffset;
            EmitSampleRequests(
                probe,
                selection,
                sampleRequests,
                ref sampleRequestOffset);
            if (sampleRequestOffset - firstRequest != selection.RequestCount)
            {
                return SimpleDdgiGuidingWorkloadCompileResult.Rejected(
                    "guiding-workload-sample-selection-count-mismatch");
            }
            sampleCommits[sampleCommitOffset++] = new(
                probe.PhysicalProbeIndex,
                probe.VirtualProbeId,
                probe.PageGeneration,
                probe.StableProbeId,
                probe.ReadableGuide.ProposalEpoch,
                selection.CompletePayloadSet);
        }

        var counts = new SimpleDdgiGuidingWorkloadCounts(
            selectedCount,
            recordOffset,
            selectedCount,
            selectedCount,
            selectedCount,
            sampleRequestOffset,
            sampleCommitOffset,
            bootstrapProbeCount,
            rotatedSlotCount);
        return new(true, counts, "guiding-workload-compiled");
    }

    /// <summary>
    /// Commits only a fence-complete sample transaction whose GPU validation
    /// counters were all zero.  A failed dispatch leaves ownership unchanged,
    /// causing the exact same deterministic requests to be emitted again.
    /// </summary>
    public bool TryCommitSamples(
        ReadOnlySpan<SimpleDdgiGuidingSampleCommit> commits,
        bool gpuWorkCompleted,
        bool validationCountersZero,
        out string reason)
    {
        if (!gpuWorkCompleted || !validationCountersZero)
        {
            reason = !gpuWorkCompleted
                ? "guiding-sample-gpu-work-incomplete"
                : "guiding-sample-validation-counters-nonzero";
            return false;
        }

        uint previousPhysical = 0u;
        bool hasPrevious = false;
        foreach (ref readonly SimpleDdgiGuidingSampleCommit commit in commits)
        {
            if (commit.PhysicalProbeIndex >= (uint)_payloadOwners.Length ||
                commit.StableProbeId == 0UL || commit.ProposalEpoch == 0u ||
                (hasPrevious && commit.PhysicalProbeIndex <= previousPhysical))
            {
                reason = "guiding-sample-commit-invalid-or-unsorted";
                return false;
            }
            previousPhysical = commit.PhysicalProbeIndex;
            hasPrevious = true;
        }

        foreach (ref readonly SimpleDdgiGuidingSampleCommit commit in commits)
        {
            ref PayloadOwnerState owner =
                ref _payloadOwners[commit.PhysicalProbeIndex];
            bool sameOwner = owner.IsComplete &&
                owner.VirtualProbeId == commit.VirtualProbeId &&
                owner.PageGeneration == commit.PageGeneration &&
                owner.StableProbeId == commit.StableProbeId;
            if (!commit.CompletePayloadSet && !sameOwner)
            {
                reason = "guiding-partial-sample-commit-has-no-complete-owner";
                return false;
            }
        }

        foreach (ref readonly SimpleDdgiGuidingSampleCommit commit in commits)
        {
            _payloadOwners[commit.PhysicalProbeIndex] = new(
                commit.VirtualProbeId,
                commit.PageGeneration,
                commit.StableProbeId,
                commit.ProposalEpoch,
                true);
        }
        reason = "guiding-sample-transaction-committed";
        return true;
    }

    public void InvalidateAllPayloadOwnership() =>
        Array.Clear(_payloadOwners);

    private bool ValidateProbe(
        in SimpleDdgiGuidingFrameProbe probe,
        out string reason)
    {
        if (probe.StableProbeId == 0UL || probe.SourceEpoch == 0u ||
            probe.SourceLightingGeneration == 0u || probe.ContentRevision == 0u ||
            probe.ActiveRayCount == 0u ||
            probe.ActiveRayCount > (uint)_layout.DirectionSlotsPerProbe)
        {
            reason = "guiding-frame-probe-source-identity-or-ray-count-invalid";
            return false;
        }
        if (probe.ReadableGuide.IsReadable &&
            (probe.ReadableGuide.PhysicalProbeIndex != probe.PhysicalProbeIndex ||
             probe.ReadableGuide.VirtualProbeId != probe.VirtualProbeId ||
             probe.ReadableGuide.PageGeneration != probe.PageGeneration))
        {
            reason = "guiding-frame-probe-readable-guide-owner-mismatch";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private bool TryResolveSampleSelection(
        in SimpleDdgiGuidingFrameProbe probe,
        out SampleSelection selection,
        out string reason)
    {
        if (!probe.ReadableGuide.IsReadable)
        {
            selection = default;
            reason = string.Empty;
            return true;
        }

        ref readonly PayloadOwnerState owner =
            ref _payloadOwners[probe.PhysicalProbeIndex];
        bool sameOwner = owner.IsComplete &&
            owner.VirtualProbeId == probe.VirtualProbeId &&
            owner.PageGeneration == probe.PageGeneration &&
            owner.StableProbeId == probe.StableProbeId;
        if (!sameOwner)
        {
            selection = new(
                RequestCount: _layout.DirectionSlotsPerProbe,
                CompletePayloadSet: true,
                RotationStartRank: 0,
                RotationCount: 0);
            reason = string.Empty;
            return true;
        }
        if (owner.CommittedProposalEpoch == probe.ReadableGuide.ProposalEpoch)
        {
            selection = default;
            reason = string.Empty;
            return true;
        }
        if (!IsStrictlyNewer(
                probe.ReadableGuide.ProposalEpoch,
                owner.CommittedProposalEpoch))
        {
            selection = default;
            reason = "guiding-readable-proposal-epoch-regressed-or-ambiguous";
            return false;
        }

        int maintenanceCount = SimpleDdgiGuidingTransportEstimator
            .ResolveMaintenanceRayCount(_layout.DirectionSlotsPerProbe);
        int guidedCount = _layout.DirectionSlotsPerProbe - maintenanceCount;
        if (guidedCount <= 0)
        {
            selection = default;
            reason = string.Empty;
            return true;
        }
        int rotationCount = Math.Clamp(
            checked((int)Math.Ceiling(
                guidedCount * (double)_policy.MaximumGuidedRotationFraction)),
            1,
            guidedCount);
        uint seed = StableHash32(
            (uint)probe.StableProbeId,
            (uint)(probe.StableProbeId >> 32),
            probe.ReadableGuide.ProposalEpoch,
            0x72f8_a451u);
        int startRank = checked((int)(seed % (uint)guidedCount));
        selection = new(rotationCount, false, startRank, rotationCount);
        reason = string.Empty;
        return true;
    }

    private void EmitSampleRequests(
        in SimpleDdgiGuidingFrameProbe probe,
        in SampleSelection selection,
        Span<GPUSimpleDdgiGuidingSampleRequest> destination,
        ref int offset)
    {
        int guidedRank = 0;
        int maintenanceCount = SimpleDdgiGuidingTransportEstimator
            .ResolveMaintenanceRayCount(_layout.DirectionSlotsPerProbe);
        int guidedCount = _layout.DirectionSlotsPerProbe - maintenanceCount;
        for (int slot = 0; slot < _layout.DirectionSlotsPerProbe; slot++)
        {
            bool maintenance = SimpleDdgiGuidingTransportEstimator
                .IsMaintenanceSlot(slot, _layout.DirectionSlotsPerProbe);
            bool selected = selection.CompletePayloadSet;
            if (!selection.CompletePayloadSet && !maintenance)
            {
                int circularDistance = guidedRank - selection.RotationStartRank;
                if (circularDistance < 0)
                    circularDistance += guidedCount;
                selected = circularDistance < selection.RotationCount;
            }
            if (!maintenance)
                guidedRank++;
            if (!selected)
                continue;

            uint slotIndex = checked((uint)slot);
            uint proposalEpoch = probe.ReadableGuide.ProposalEpoch;
            uint stableLow = (uint)probe.StableProbeId;
            uint stableHigh = (uint)(probe.StableProbeId >> 32);
            destination[offset++] = new()
            {
                PhysicalProbeIndex = probe.PhysicalProbeIndex,
                VirtualProbeId = probe.VirtualProbeId,
                PageGeneration = probe.PageGeneration,
                ExpectedDistributionGeneration =
                    probe.ReadableGuide.DistributionGeneration,
                ExpectedProposalEpoch = proposalEpoch,
                StableProbeIdLow = stableLow,
                StableProbeIdHigh = stableHigh,
                SlotIndex = slotIndex,
                Technique = maintenance
                    ? (uint)SimpleDdgiDirectionSamplingTechnique
                        .UniformMaintenance
                    : (uint)SimpleDdgiDirectionSamplingTechnique.Mixture,
                RandomBranchBits = StableHash32(
                    stableLow, stableHigh, proposalEpoch, slotIndex ^ 0x19b4_7a31u),
                RequestedUniformFraction = _policy.UniformMixtureFraction,
                SourceEpoch = probe.SourceEpoch,
                SourceLightingGeneration = probe.SourceLightingGeneration,
                TraceRayIndex = checked(
                    probe.PhysicalProbeIndex *
                        checked((uint)_layout.DirectionSlotsPerProbe) +
                    slotIndex)
            };
        }
    }

    private static uint PackSampleCountAndAge(uint sampleCount, uint age)
    {
        const uint SampleMask = 0x00ff_ffffu;
        return Math.Min(sampleCount, SampleMask) |
            (Math.Min(age, byte.MaxValue) << 24);
    }

    private static bool IsStrictlyNewer(uint candidate, uint current)
    {
        if (candidate == 0u || current == 0u || candidate == current)
            return false;
        uint delta = unchecked(candidate - current);
        return delta < 0x8000_0000u;
    }

    private static uint StableHash32(uint a, uint b, uint c, uint d)
    {
        uint value = unchecked(a ^ RotateLeft(b, 7) ^ RotateLeft(c, 17) ^ d);
        value ^= value >> 16;
        value = unchecked(value * 0x7feb_352du);
        value ^= value >> 15;
        value = unchecked(value * 0x846c_a68bu);
        return value ^ (value >> 16);
    }

    private static uint RotateLeft(uint value, int amount) =>
        (value << amount) | (value >> (32 - amount));

    private readonly record struct SampleSelection(
        int RequestCount,
        bool CompletePayloadSet,
        int RotationStartRank,
        int RotationCount);

    private readonly record struct PayloadOwnerState(
        uint VirtualProbeId,
        uint PageGeneration,
        ulong StableProbeId,
        uint CommittedProposalEpoch,
        bool IsComplete);
}
