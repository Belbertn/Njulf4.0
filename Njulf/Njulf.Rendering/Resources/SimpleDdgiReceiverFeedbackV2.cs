using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>Guaranteed append reservation for one feedback producer.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackProducerQuota(
    SimpleDdgiReceiverFeedbackProducer Producer,
    uint ReservedRecordCount);

/// <summary>
/// Capacity inputs for the exact B1 receiver-feedback path.  Values are
/// collected at a resource transition, never derived on the hot path.
/// <para>
/// <see cref="SortScratchBytesPerRecord"/> is retained only for source
/// compatibility with the pre-B1 planner request.  It must be zero for the
/// versioned GPU-sort ABI: scratch is derived exclusively by
/// <see cref="SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout"/>.  In
/// particular, the historical 16-byte-per-record placeholder is rejected
/// rather than silently under-allocating the radix partitions.
/// </para>
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackLayoutRequest(
    int ActivePhysicalProbeCapacity,
    ulong ScreenTileCount,
    double ScreenSamplingProbability,
    uint MaximumUniqueGatherOwnersPerTile,
    uint SafetyMarginRecords,
    uint WorkgroupSize,
    ulong SortScratchBytesPerRecord,
    ulong IndependentMemoryBudgetBytes,
    ulong RendererMemoryHeadroomBytes,
    ulong MaximumStorageBufferRange,
    uint MaximumPagePublicationGeneration);

/// <summary>Read-only admission inputs that are not capacity arithmetic.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackPrerequisites(
    bool ExactBackendSupported,
    bool PrerequisitesSatisfied,
    bool ExactQualificationPassed,
    string? QualificationId,
    bool ResourcesComplete);

/// <summary>Calculated, immutable V2 layout assumptions and byte totals.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackLayout(
    ulong SampledScreenTileCount,
    ulong ScreenRecordCount,
    ulong OtherProducerRecordCount,
    ulong SafetyMarginRecordCount,
    ulong RecordCapacity,
    ulong RecordBankBytes,
    ulong RecordBanksBytes,
    ulong SortScratchBytes,
    ulong SummaryBytes,
    ulong TotalBytes,
    uint GpuSortAbiVersion = 0u,
    uint GpuSortSummaryCapacity = 0u,
    uint GpuSortFallbackCapacity = 0u,
    SimpleDdgiReceiverFeedbackCaptureSourceLayout CaptureSource = default,
    ulong SourceScreenTileCount = 0UL,
    uint MaximumUniqueGatherOwnersPerTile = 0u)
{
    public static SimpleDdgiReceiverFeedbackLayout Empty { get; } = new();

    public uint ScreenSamplingPeriod =>
        SampledScreenTileCount == 0UL || SourceScreenTileCount == 0UL
            ? 0u
            : checked((uint)Math.Min(
                uint.MaxValue,
                (SourceScreenTileCount + SampledScreenTileCount - 1UL) /
                    SampledScreenTileCount));

    /// <summary>
    /// Reconstructs and verifies the one legal B1 GPU partition.  Allocation
    /// owners use this as a fail-closed gate, so an old hand-authored layout
    /// cannot activate newer shaders simply because its aggregate byte count
    /// happens to be nonzero.
    /// </summary>
    public bool TryGetGpuSortLayout(
        out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
        out string reason)
    {
        gpuLayout = default;
        reason = string.Empty;
        if (GpuSortAbiVersion != SimpleDdgiReceiverFeedbackGpuSortAbi.Version)
        {
            reason = "receiver-feedback-gpu-sort-abi-version-mismatch";
            return false;
        }
        if (RecordCapacity == 0UL ||
            RecordCapacity > SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity ||
            GpuSortSummaryCapacity == 0u ||
            GpuSortFallbackCapacity < RecordCapacity)
        {
            reason = "receiver-feedback-gpu-sort-capacity-metadata-invalid";
            return false;
        }

        if (!SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
                checked((uint)RecordCapacity),
                GpuSortSummaryCapacity,
                GpuSortFallbackCapacity,
                out gpuLayout,
                out string abiReason))
        {
            reason = "receiver-feedback-gpu-sort-layout-rejected:" + abiReason;
            return false;
        }

        ulong expectedRecordBankBytes = gpuLayout.RequiredRecordBanksBytes / 2UL;
        if (RecordBankBytes != expectedRecordBankBytes ||
            RecordBanksBytes != gpuLayout.RequiredRecordBanksBytes ||
            SortScratchBytes != gpuLayout.RequiredSortScratchBytes ||
            SummaryBytes != gpuLayout.RequiredSummaryBanksBytes ||
            !CaptureSource.IsValid ||
            CaptureSource.RecordCapacity != RecordCapacity ||
            SourceScreenTileCount < SampledScreenTileCount ||
            MaximumUniqueGatherOwnersPerTile == 0u ||
            ScreenRecordCount != checked(
                SampledScreenTileCount * MaximumUniqueGatherOwnersPerTile) ||
            TotalBytes != checked(
                gpuLayout.RequiredTotalBytes + CaptureSource.RequiredBytes))
        {
            reason = "receiver-feedback-gpu-sort-layout-bytes-do-not-match-abi";
            gpuLayout = default;
            return false;
        }

        return true;
    }
}

/// <summary>
/// Full B1 admission result.  Legacy mode intentionally reports no V2 memory;
/// it remains an A/B reference rather than an alternate interpretation of the
/// exact ABI.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackPlan(
    GiExperimentModeState<SimpleDdgiReceiverFeedbackMode> Mode,
    SimpleDdgiReceiverFeedbackLayout Layout,
    SimpleDdgiAdvancedExperimentMemoryPlan Memory)
{
    public bool UsesExactCompacted => Mode.EffectiveMode ==
        SimpleDdgiReceiverFeedbackMode.ExactCompacted;

    public static SimpleDdgiReceiverFeedbackPlan Disabled(
        GiExperimentFallbackReason reason = GiExperimentFallbackReason.None) => new(
            GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>.Disabled(
                SimpleDdgiReceiverFeedbackMode.Off),
            SimpleDdgiReceiverFeedbackLayout.Empty,
            SimpleDdgiAdvancedExperimentMemoryPlan
                .CreateReceiverFeedbackRejected(reason));
}

/// <summary>
/// Compiler for B1's exact record/sort/summary allocations.  Any overflow or
/// capacity failure rejects the entire V2 feature before allocation rather than
/// reducing producer quotas or truncating a generation.
/// </summary>
public static class SimpleDdgiReceiverFeedbackPlanner
{
    public static SimpleDdgiReceiverFeedbackPlan Compile(
        SimpleDdgiReceiverFeedbackMode requestedMode,
        in SimpleDdgiReceiverFeedbackLayoutRequest request,
        ReadOnlySpan<SimpleDdgiReceiverFeedbackProducerQuota> producerQuotas,
        in SimpleDdgiReceiverFeedbackPrerequisites prerequisites)
    {
        if (requestedMode == SimpleDdgiReceiverFeedbackMode.Off)
            return SimpleDdgiReceiverFeedbackPlan.Disabled();

        if (!Enum.IsDefined(requestedMode))
        {
            return new SimpleDdgiReceiverFeedbackPlan(
                new GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>(
                    requestedMode,
                    SimpleDdgiReceiverFeedbackMode.Off,
                    SimpleDdgiReceiverFeedbackMode.Off,
                    SimpleDdgiReceiverFeedbackMode.Off,
                    GiExperimentFallbackReason.InvalidRequestedMode,
                    "requested-mode-is-not-defined-by-the-current-abi",
                    string.Empty),
                SimpleDdgiReceiverFeedbackLayout.Empty,
                SimpleDdgiAdvancedExperimentMemoryPlan.CreateReceiverFeedbackRejected(
                    GiExperimentFallbackReason.InvalidRequestedMode));
        }

        // The existing packed path owns its legacy scheduler arena.  V2 must
        // neither add hidden allocations nor claim that legacy records are
        // exact compacted records.
        if (requestedMode ==
            SimpleDdgiReceiverFeedbackMode.LegacyPackedReference)
        {
            return new SimpleDdgiReceiverFeedbackPlan(
                new GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>(
                    requestedMode,
                    requestedMode,
                    requestedMode,
                    requestedMode,
                    GiExperimentFallbackReason.None,
                    "legacy-packed-reference-active",
                    string.Empty),
                SimpleDdgiReceiverFeedbackLayout.Empty,
                SimpleDdgiAdvancedExperimentMemoryPlan.Empty);
        }

        var evaluation = new GiExperimentModeEvaluation(
            Supported: prerequisites.ExactBackendSupported,
            PrerequisitesSatisfied: prerequisites.PrerequisitesSatisfied,
            MemoryAdmitted: true,
            ResourcesComplete: prerequisites.ResourcesComplete,
            // ExactCompacted remains the explicit developer/reference
            // selection. The shared resolver recognizes AutoQualified by its
            // stable enum name and requires the B1 qualification result.
            RequiresQualification: false,
            QualificationPassed: prerequisites.ExactQualificationPassed,
            QualificationId: prerequisites.QualificationId);
        GiExperimentModeState<SimpleDdgiReceiverFeedbackMode> mode =
            GiExperimentModeResolver.Resolve(
                requestedMode,
                SimpleDdgiReceiverFeedbackMode.Off,
                evaluation);
        if (!mode.IsAdmitted)
        {
            return new SimpleDdgiReceiverFeedbackPlan(
                mode,
                SimpleDdgiReceiverFeedbackLayout.Empty,
                SimpleDdgiAdvancedExperimentMemoryPlan.CreateReceiverFeedbackRejected(
                    mode.FallbackReason));
        }

        if (!TryCompileLayout(
                request,
                producerQuotas,
                out SimpleDdgiReceiverFeedbackLayout layout,
                out GiExperimentFallbackReason layoutFailure,
                out string layoutFailureDetail))
        {
            return new SimpleDdgiReceiverFeedbackPlan(
                CreateExactFallback(
                    requestedMode,
                    prerequisites,
                    layoutFailure,
                    layoutFailureDetail),
                SimpleDdgiReceiverFeedbackLayout.Empty,
                SimpleDdgiAdvancedExperimentMemoryPlan
                    .CreateReceiverFeedbackRejected(layoutFailure));
        }

        bool allocated = mode.IsEffective;
        return new SimpleDdgiReceiverFeedbackPlan(
            mode,
            layout,
            CreateMemoryPlan(layout, allocated));
    }

    private static bool TryCompileLayout(
        in SimpleDdgiReceiverFeedbackLayoutRequest request,
        ReadOnlySpan<SimpleDdgiReceiverFeedbackProducerQuota> producerQuotas,
        out SimpleDdgiReceiverFeedbackLayout layout,
        out GiExperimentFallbackReason failure,
        out string failureDetail)
    {
        layout = SimpleDdgiReceiverFeedbackLayout.Empty;
        failure = GiExperimentFallbackReason.None;
        failureDetail = string.Empty;

        if (request.ActivePhysicalProbeCapacity <= 0 ||
            request.ScreenTileCount == 0UL ||
            !double.IsFinite(request.ScreenSamplingProbability) ||
            request.ScreenSamplingProbability <= 0.0 ||
            request.ScreenSamplingProbability > 1.0 ||
            request.MaximumUniqueGatherOwnersPerTile == 0u ||
            request.MaximumUniqueGatherOwnersPerTile >
                SimpleDdgiReceiverFeedbackCaptureSourceAbi
                    .MaximumUniqueGatherOwnersPerTile)
        {
            failure = GiExperimentFallbackReason.InvalidConfiguration;
            failureDetail = "nonzero-probe-tile-sampling-and-owner-inputs-required";
            return false;
        }

        if (request.WorkgroupSize != SimpleDdgiReceiverFeedbackGpuSortAbi.WorkgroupSize)
        {
            failure = GiExperimentFallbackReason.InvalidConfiguration;
            failureDetail = "receiver-feedback-gpu-sort-workgroup-size-must-match-abi";
            return false;
        }

        if (request.SortScratchBytesPerRecord != 0UL)
        {
            failure = GiExperimentFallbackReason.InvalidConfiguration;
            failureDetail = "receiver-feedback-gpu-sort-abi-derives-scratch-legacy-sort-scratch-bytes-per-record-must-be-zero";
            return false;
        }

        if (!SimpleDdgiReceiverFeedbackV2Abi.CanRepresentPageGeneration(
                request.MaximumPagePublicationGeneration))
        {
            failure = GiExperimentFallbackReason.FeedbackLayoutNotRepresentable;
            failureDetail = "page-publication-generation-exceeds-v2-packed-field";
            return false;
        }

        if (request.IndependentMemoryBudgetBytes == 0UL ||
            request.RendererMemoryHeadroomBytes == 0UL ||
            request.MaximumStorageBufferRange == 0UL)
        {
            failure = GiExperimentFallbackReason.InvalidConfiguration;
            failureDetail = "explicit-memory-budgets-and-storage-buffer-limit-required";
            return false;
        }

        try
        {
            ulong sampledTiles = checked((ulong)Math.Ceiling(
                request.ScreenTileCount * request.ScreenSamplingProbability));
            sampledTiles = Math.Min(sampledTiles, request.ScreenTileCount);
            ulong screenRecords = checked(sampledTiles *
                request.MaximumUniqueGatherOwnersPerTile);
            ulong otherRecords = SumReservedProducerRecords(producerQuotas);
            ulong unalignedRecordCapacity = checked(screenRecords + otherRecords +
                request.SafetyMarginRecords);
            ulong recordCapacity = AlignUp(
                unalignedRecordCapacity,
                request.WorkgroupSize);
            if (recordCapacity == 0UL ||
                recordCapacity > SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity)
            {
                failure = GiExperimentFallbackReason.VulkanLimitExceeded;
                failureDetail = "record-capacity-exceeds-b1-gpu-sort-addressable-capacity";
                return false;
            }

            if (!SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
                    checked((uint)recordCapacity),
                    checked((uint)request.ActivePhysicalProbeCapacity),
                    checked((uint)recordCapacity),
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuSortLayout,
                    out string gpuSortFailure))
            {
                failure = GiExperimentFallbackReason.InvalidConfiguration;
                failureDetail = "receiver-feedback-gpu-sort-layout-rejected:" + gpuSortFailure;
                return false;
            }

            ulong recordBankBytes = gpuSortLayout.RequiredRecordBanksBytes / 2UL;
            ulong recordBanksBytes = gpuSortLayout.RequiredRecordBanksBytes;
            ulong sortScratchBytes = gpuSortLayout.RequiredSortScratchBytes;
            ulong summaryBytes = gpuSortLayout.RequiredSummaryBanksBytes;
            var producerCapacities = new SimpleDdgiReceiverFeedbackProducerCapacities(
                OpaqueForward: checked((uint)screenRecords),
                AlphaMaskOrFoliage: 0u,
                TransparentWeightedOit: 0u,
                Particles: 0u,
                Fog: 0u,
                ReflectionCapture: 0u,
                RefinementOrBaseFallback: 0u);
            for (int quotaIndex = 0; quotaIndex < producerQuotas.Length; quotaIndex++)
            {
                SimpleDdgiReceiverFeedbackProducerQuota quota =
                    producerQuotas[quotaIndex];
                producerCapacities = producerCapacities.Add(
                    quota.Producer,
                    quota.ReservedRecordCount);
            }
            if (!SimpleDdgiReceiverFeedbackCaptureSourceAbi.TryCreateLayout(
                    checked((uint)recordCapacity),
                    producerCapacities,
                    out SimpleDdgiReceiverFeedbackCaptureSourceLayout captureSource,
                    out string captureLayoutFailure))
            {
                failure = GiExperimentFallbackReason.InvalidConfiguration;
                failureDetail = captureLayoutFailure;
                return false;
            }
            ulong totalBytes = checked(
                gpuSortLayout.RequiredTotalBytes + captureSource.RequiredBytes);

            if (recordBanksBytes > request.MaximumStorageBufferRange ||
                sortScratchBytes > request.MaximumStorageBufferRange ||
                summaryBytes > request.MaximumStorageBufferRange ||
                captureSource.RequiredBytes > request.MaximumStorageBufferRange)
            {
                failure = GiExperimentFallbackReason.VulkanLimitExceeded;
                failureDetail = "one-or-more-v2-storage-buffers-exceed-the-device-limit";
                return false;
            }
            if (totalBytes > request.IndependentMemoryBudgetBytes)
            {
                failure = GiExperimentFallbackReason.IndependentMemoryBudgetExceeded;
                failureDetail = "receiver-feedback-independent-memory-budget-exceeded";
                return false;
            }
            if (totalBytes > request.RendererMemoryHeadroomBytes)
            {
                failure = GiExperimentFallbackReason.RendererMemoryHeadroomExceeded;
                failureDetail = "receiver-feedback-would-exceed-renderer-memory-headroom";
                return false;
            }

            layout = new SimpleDdgiReceiverFeedbackLayout(
                sampledTiles,
                screenRecords,
                otherRecords,
                request.SafetyMarginRecords,
                recordCapacity,
                recordBankBytes,
                recordBanksBytes,
                sortScratchBytes,
                summaryBytes,
                totalBytes,
                SimpleDdgiReceiverFeedbackGpuSortAbi.Version,
                gpuSortLayout.SummaryCapacity,
                gpuSortLayout.FallbackCapacity,
                captureSource,
                request.ScreenTileCount,
                request.MaximumUniqueGatherOwnersPerTile);
            return true;
        }
        catch (OverflowException)
        {
            failure = GiExperimentFallbackReason.ArithmeticOverflow;
            failureDetail = "receiver-feedback-layout-byte-calculation-overflowed";
            return false;
        }
        catch (ArgumentException)
        {
            failure = GiExperimentFallbackReason.InvalidConfiguration;
            failureDetail = "receiver-feedback-producer-reservations-are-invalid";
            return false;
        }
    }

    private static ulong SumReservedProducerRecords(
        ReadOnlySpan<SimpleDdgiReceiverFeedbackProducerQuota> quotas)
    {
        ulong total = 0UL;
        uint seen = 0u;
        for (int index = 0; index < quotas.Length; index++)
        {
            SimpleDdgiReceiverFeedbackProducerQuota quota = quotas[index];
            if (!Enum.IsDefined(quota.Producer))
                throw new ArgumentOutOfRangeException(nameof(quotas));
            uint producerBit = 1u << (int)quota.Producer;
            if ((seen & producerBit) != 0u)
                throw new ArgumentException(
                    "Every exact receiver-feedback producer needs one reservation.",
                    nameof(quotas));
            seen |= producerBit;
            total = checked(total + quota.ReservedRecordCount);
        }
        return total;
    }

    private static ulong AlignUp(ulong value, uint alignment)
    {
        ulong align = alignment;
        return checked(((value + align - 1UL) / align) * align);
    }

    private static GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>
        CreateExactFallback(
            SimpleDdgiReceiverFeedbackMode requestedMode,
            in SimpleDdgiReceiverFeedbackPrerequisites prerequisites,
            GiExperimentFallbackReason reason,
            string detail)
    {
        string qualificationId = string.IsNullOrWhiteSpace(prerequisites.QualificationId)
            ? string.Empty
            : prerequisites.QualificationId.Trim();
        return new GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>(
            requestedMode,
            prerequisites.ExactBackendSupported
                ? requestedMode
                : SimpleDdgiReceiverFeedbackMode.Off,
            SimpleDdgiReceiverFeedbackMode.Off,
            SimpleDdgiReceiverFeedbackMode.Off,
            reason,
            detail,
            qualificationId);
    }

    private static SimpleDdgiAdvancedExperimentMemoryPlan CreateMemoryPlan(
        in SimpleDdgiReceiverFeedbackLayout layout,
        bool allocated)
    {
        ulong recordAndCaptureBytes = checked(
            layout.RecordBanksBytes + layout.CaptureSource.RequiredBytes);
        ulong recordAllocated = allocated ? recordAndCaptureBytes : 0UL;
        ulong scratchAllocated = allocated ? layout.SortScratchBytes : 0UL;
        ulong summaryAllocated = allocated ? layout.SummaryBytes : 0UL;
        return new SimpleDdgiAdvancedExperimentMemoryPlan(
            SimpleDdgiAdvancedMemoryUsage.Admitted(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks,
                recordAndCaptureBytes,
                recordAllocated,
                recordAllocated),
            SimpleDdgiAdvancedMemoryUsage.Admitted(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                layout.SortScratchBytes,
                scratchAllocated,
                scratchAllocated),
            SimpleDdgiAdvancedMemoryUsage.Admitted(
                SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackProbeSummaries,
                layout.SummaryBytes,
                summaryAllocated,
                summaryAllocated),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.OpacityMicromapResidentData),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.OpacityMicromapBuildScratch),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.OpacityMicromapCompactionHeadroom),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingHistoryBanks),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.CausticHistory),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.NearFieldTraceTargets),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.NearFieldHistoryAndMoments),
            SimpleDdgiAdvancedMemoryUsage.Zero(
                SimpleDdgiAdvancedMemoryCategory.NearFieldFilterScratch));
    }
}

[Flags]
public enum SimpleDdgiReceiverFeedbackBankFlags : uint
{
    None = 0,
    Validated = 1u << 0,
    AppendOverflow = 1u << 1,
    ProducerRangeOverflow = 1u << 2,
    NonFiniteInput = 1u << 3,
    SortOrReduceFailure = 1u << 4
}

/// <summary>CPU mirror of the published record/summary bank header state.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackBankHeader(
    uint LayoutRevision,
    uint FeedbackGeneration,
    uint ViewportGeneration,
    ulong FrameSerial,
    uint AppendCount,
    uint DroppedCount,
    uint ProducerOverflowMask,
    uint RecordCapacity,
    SimpleDdgiReceiverFeedbackBankFlags Flags);

public readonly record struct SimpleDdgiReceiverFeedbackBankValidation(
    bool UseFeedback,
    GiExperimentFallbackReason Reason,
    string Detail)
{
    public static SimpleDdgiReceiverFeedbackBankValidation Valid { get; } = new(
        true,
        GiExperimentFallbackReason.None,
        "validated-previous-frame-bank");
}

public static class SimpleDdgiReceiverFeedbackBankValidator
{
    public static SimpleDdgiReceiverFeedbackBankValidation ValidateForScheduling(
        in SimpleDdgiReceiverFeedbackBankHeader header,
        uint expectedLayoutRevision,
        uint expectedFeedbackGeneration,
        uint expectedViewportGeneration,
        ulong expectedFrameSerial)
    {
        if (header.LayoutRevision != expectedLayoutRevision)
        {
            return Invalid(
                GiExperimentFallbackReason.LayoutRevisionMismatch,
                "receiver-feedback-layout-revision-mismatch");
        }
        if (header.FeedbackGeneration == 0u ||
            header.FeedbackGeneration != expectedFeedbackGeneration ||
            header.ViewportGeneration != expectedViewportGeneration ||
            header.FrameSerial == ulong.MaxValue ||
            header.FrameSerial + 1UL != expectedFrameSerial)
        {
            return Invalid(
                GiExperimentFallbackReason.GenerationMismatch,
                "receiver-feedback-bank-is-stale-or-generation-mismatched");
        }
        if (header.AppendCount > header.RecordCapacity ||
            header.DroppedCount != 0u || header.ProducerOverflowMask != 0u ||
            (header.Flags & (SimpleDdgiReceiverFeedbackBankFlags.AppendOverflow |
                SimpleDdgiReceiverFeedbackBankFlags.ProducerRangeOverflow)) != 0)
        {
            return Invalid(
                GiExperimentFallbackReason.FeedbackBankOverflowed,
                "receiver-feedback-write-bank-overflowed");
        }
        if ((header.Flags & SimpleDdgiReceiverFeedbackBankFlags.Validated) == 0 ||
            (header.Flags & (SimpleDdgiReceiverFeedbackBankFlags.NonFiniteInput |
                SimpleDdgiReceiverFeedbackBankFlags.SortOrReduceFailure)) != 0)
        {
            return Invalid(
                GiExperimentFallbackReason.FeedbackBankInvalid,
                "receiver-feedback-write-bank-failed-validation");
        }
        return SimpleDdgiReceiverFeedbackBankValidation.Valid;
    }

    /// <summary>
    /// Advances a nonzero bank generation without creating an ABA ambiguity.
    /// Callers must recreate the double-bank resource transaction when this
    /// returns false rather than wrapping an old generation back to one.
    /// </summary>
    public static bool TryGetNextGeneration(uint current, out uint next)
    {
        if (current == uint.MaxValue)
        {
            next = 0u;
            return false;
        }

        next = current == 0u ? 1u : current + 1u;
        return true;
    }

    /// <summary>
    /// Convenience guard for callers that cannot perform a resource rebuild
    /// inline.  Production transition code should use
    /// <see cref="TryGetNextGeneration"/> and recreate the banks on false.
    /// </summary>
    public static uint NextGeneration(uint current)
    {
        if (!TryGetNextGeneration(current, out uint next))
        {
            throw new OverflowException(
                "Receiver-feedback generation reached its wrap threshold; recreate the bank transaction.");
        }
        return next;
    }

    private static SimpleDdgiReceiverFeedbackBankValidation Invalid(
        GiExperimentFallbackReason reason,
        string detail) => new(false, reason, detail);
}

/// <summary>Physical receiver contribution paired with its exact gather owner.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackSample(
    SimpleDdgiReceiverContribution Contribution,
    float PhysicalReceiverContribution);

public readonly record struct SimpleDdgiReceiverFeedbackProbeSummary(
    uint ResolvedVirtualProbeId,
    GPUSimpleDdgiReceiverContributionSummaryV2 Summary);

public readonly record struct SimpleDdgiReceiverFeedbackFallbackPressure(
    uint RequestedVirtualProbeId,
    uint RequestedVirtualPageId,
    float EstimatedContributionMass,
    uint SampledReceiverCount);

/// <summary>
/// CPU oracle result for the sort/unique/reduce sequence.  An invalid input
/// deliberately returns no partial summaries because scheduling must consume
/// ordinary priors rather than a preferential partial sample.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackReductionResult(
    bool Valid,
    GiExperimentFallbackReason FailureReason,
    IReadOnlyList<SimpleDdgiReceiverFeedbackProbeSummary> ProbeSummaries,
    IReadOnlyList<SimpleDdgiReceiverFeedbackFallbackPressure> FallbackPressure)
{
    public static SimpleDdgiReceiverFeedbackReductionResult Invalid(
        GiExperimentFallbackReason reason) => new(
            false,
            reason,
            Array.Empty<SimpleDdgiReceiverFeedbackProbeSummary>(),
            Array.Empty<SimpleDdgiReceiverFeedbackFallbackPressure>());
}

/// <summary>
/// Deterministic CPU reference for B1's GPU sort/unique/reduce path.  It is
/// intentionally not a hot path; it defines the exact semantics that GPU
/// implementations must match across arbitrary compaction order.
/// </summary>
public static class SimpleDdgiReceiverFeedbackReducer
{
    public static SimpleDdgiReceiverFeedbackReductionResult Reduce(
        ReadOnlySpan<SimpleDdgiReceiverFeedbackSample> samples,
        uint expectedFeedbackGeneration)
    {
        // Probe and page IDs are often identical in a dense test layout.  Real
        // sparse callers must use the overload with the authoritative mapping.
        return Reduce(samples, expectedFeedbackGeneration, static probeId => probeId);
    }

    public static SimpleDdgiReceiverFeedbackReductionResult Reduce(
        ReadOnlySpan<SimpleDdgiReceiverFeedbackSample> samples,
        uint expectedFeedbackGeneration,
        Func<uint, uint> requestedProbeToPage)
    {
        if (expectedFeedbackGeneration == 0u)
            throw new ArgumentOutOfRangeException(nameof(expectedFeedbackGeneration));
        ArgumentNullException.ThrowIfNull(requestedProbeToPage);

        var normalized = new List<NormalizedSample>(samples.Length);
        for (int index = 0; index < samples.Length; index++)
        {
            SimpleDdgiReceiverFeedbackSample sample = samples[index];
            SimpleDdgiReceiverContribution contribution = sample.Contribution;
            if (contribution.FeedbackGeneration != expectedFeedbackGeneration)
            {
                return SimpleDdgiReceiverFeedbackReductionResult.Invalid(
                    GiExperimentFallbackReason.GenerationMismatch);
            }
            if (!contribution.TryCreateGpuRecord(out _))
            {
                return SimpleDdgiReceiverFeedbackReductionResult.Invalid(
                    GiExperimentFallbackReason.FeedbackLayoutNotRepresentable);
            }

            try
            {
                normalized.Add(new NormalizedSample(
                    contribution,
                    contribution.EstimateContributionMass(
                        sample.PhysicalReceiverContribution)));
            }
            catch (ArgumentOutOfRangeException)
            {
                return SimpleDdgiReceiverFeedbackReductionResult.Invalid(
                    GiExperimentFallbackReason.FeedbackBankInvalid);
            }
            catch (OverflowException)
            {
                return SimpleDdgiReceiverFeedbackReductionResult.Invalid(
                    GiExperimentFallbackReason.ArithmeticOverflow);
            }
        }

        normalized.Sort(NormalizedSampleComparer.Instance);
        return ReduceSorted(
            normalized,
            expectedFeedbackGeneration,
            requestedProbeToPage);
    }

    private static SimpleDdgiReceiverFeedbackReductionResult ReduceSorted(
        List<NormalizedSample> samples,
        uint feedbackGeneration,
        Func<uint, uint> requestedProbeToPage)
    {
        var summaries = new List<SimpleDdgiReceiverFeedbackProbeSummary>();
        var pressure = new List<SimpleDdgiReceiverFeedbackFallbackPressure>();

        int index = 0;
        while (index < samples.Count)
        {
            uint resolvedProbe = samples[index].Contribution.ResolvedVirtualProbeId;
            double mass = 0.0;
            float maximumWeight = 0.0f;
            uint uniqueTiles = 0u;
            uint sampledReceivers = 0u;
            uint consumerMask = 0u;
            uint requestedFallbackCount = 0u;
            uint resolvedFallbackCount = 0u;
            uint lastTile = 0u;
            bool hasTile = false;

            while (index < samples.Count &&
                   samples[index].Contribution.ResolvedVirtualProbeId == resolvedProbe)
            {
                NormalizedSample sample = samples[index];
                SimpleDdgiReceiverContribution contribution = sample.Contribution;
                if (!hasTile || contribution.ExactTileId != lastTile)
                {
                    uniqueTiles = SaturatingIncrement(uniqueTiles);
                    lastTile = contribution.ExactTileId;
                    hasTile = true;
                }

                mass += sample.CorrectedMass;
                if (!double.IsFinite(mass) || mass > float.MaxValue)
                {
                    return SimpleDdgiReceiverFeedbackReductionResult.Invalid(
                        GiExperimentFallbackReason.ArithmeticOverflow);
                }
                maximumWeight = Math.Max(maximumWeight, contribution.InterpolationWeight);
                sampledReceivers = SaturatingIncrement(sampledReceivers);
                consumerMask |= 1u << (int)contribution.Producer;
                if (contribution.IsFallback)
                {
                    requestedFallbackCount = SaturatingIncrement(
                        requestedFallbackCount);
                    resolvedFallbackCount = SaturatingIncrement(resolvedFallbackCount);
                }
                index++;
            }

            SimpleDdgiReceiverFeedbackSummaryStatus status =
                SimpleDdgiReceiverFeedbackSummaryStatus.Validated;
            uint packedFallbackCounts;
            if (!SimpleDdgiReceiverFeedbackV2Abi.TryPackFallbackCounts(
                    requestedFallbackCount,
                    resolvedFallbackCount,
                    out packedFallbackCounts))
            {
                status |= SimpleDdgiReceiverFeedbackSummaryStatus.FallbackCountOverflow;
                packedFallbackCounts = 0u;
            }

            summaries.Add(new SimpleDdgiReceiverFeedbackProbeSummary(
                resolvedProbe,
                new GPUSimpleDdgiReceiverContributionSummaryV2
                {
                    EstimatedContributionMass = (float)mass,
                    MaximumSingleReceiverWeight = maximumWeight,
                    ExactUniqueTileCount = uniqueTiles,
                    SampledReceiverCount = sampledReceivers,
                    ConsumerMask = consumerMask,
                    PackedFallbackCounts = packedFallbackCounts,
                    FeedbackGeneration = feedbackGeneration,
                    StatusFlags = (uint)status
                }));
        }

        if (!BuildFallbackPressure(samples, pressure, requestedProbeToPage))
        {
            return SimpleDdgiReceiverFeedbackReductionResult.Invalid(
                GiExperimentFallbackReason.ArithmeticOverflow);
        }
        return new SimpleDdgiReceiverFeedbackReductionResult(
            true,
            GiExperimentFallbackReason.None,
            summaries,
            pressure);
    }

    private static bool BuildFallbackPressure(
        List<NormalizedSample> samples,
        List<SimpleDdgiReceiverFeedbackFallbackPressure> destination,
        Func<uint, uint> requestedProbeToPage)
    {
        var accumulators = new Dictionary<FallbackPressureKey, FallbackAccumulator>();
        for (int index = 0; index < samples.Count; index++)
        {
            NormalizedSample current = samples[index];
            if (!current.Contribution.IsFallback)
                continue;

            var key = new FallbackPressureKey(
                current.Contribution.RequestedVirtualProbeId,
                requestedProbeToPage(current.Contribution.RequestedVirtualProbeId));
            accumulators.TryGetValue(key, out FallbackAccumulator accumulated);
            double mass = accumulated.Mass + current.CorrectedMass;
            if (!double.IsFinite(mass) || mass > float.MaxValue)
                return false;
            accumulators[key] = new FallbackAccumulator(
                mass,
                SaturatingIncrement(accumulated.Count));
        }

        var ordered = new List<KeyValuePair<FallbackPressureKey, FallbackAccumulator>>(
            accumulators);
        ordered.Sort(static (left, right) =>
        {
            int result = left.Key.RequestedVirtualProbeId.CompareTo(
                right.Key.RequestedVirtualProbeId);
            return result != 0
                ? result
                : left.Key.RequestedVirtualPageId.CompareTo(
                    right.Key.RequestedVirtualPageId);
        });
        foreach (KeyValuePair<FallbackPressureKey, FallbackAccumulator> entry in ordered)
        {
            destination.Add(new SimpleDdgiReceiverFeedbackFallbackPressure(
                entry.Key.RequestedVirtualProbeId,
                entry.Key.RequestedVirtualPageId,
                (float)entry.Value.Mass,
                entry.Value.Count));
        }
        return true;
    }

    private readonly record struct FallbackPressureKey(
        uint RequestedVirtualProbeId,
        uint RequestedVirtualPageId);

    private readonly record struct FallbackAccumulator(double Mass, uint Count);

    private static uint SaturatingIncrement(uint value) =>
        value == uint.MaxValue ? uint.MaxValue : value + 1u;

    private readonly record struct NormalizedSample(
        SimpleDdgiReceiverContribution Contribution,
        float CorrectedMass);

    private sealed class NormalizedSampleComparer : IComparer<NormalizedSample>
    {
        public static NormalizedSampleComparer Instance { get; } = new();

        public int Compare(NormalizedSample left, NormalizedSample right)
        {
            SimpleDdgiReceiverContribution a = left.Contribution;
            SimpleDdgiReceiverContribution b = right.Contribution;
            int result = a.ResolvedVirtualProbeId.CompareTo(b.ResolvedVirtualProbeId);
            if (result != 0) return result;
            result = a.ExactTileId.CompareTo(b.ExactTileId);
            if (result != 0) return result;
            result = ((uint)a.Producer).CompareTo((uint)b.Producer);
            if (result != 0) return result;
            result = ((uint)a.FallbackRole).CompareTo((uint)b.FallbackRole);
            if (result != 0) return result;
            result = a.RequestedVirtualProbeId.CompareTo(b.RequestedVirtualProbeId);
            if (result != 0) return result;
            result = a.ResolvedVirtualPageId.CompareTo(b.ResolvedVirtualPageId);
            if (result != 0) return result;
            result = a.PagePublicationGeneration.CompareTo(b.PagePublicationGeneration);
            if (result != 0) return result;
            return BitConverter.SingleToUInt32Bits(left.CorrectedMass).CompareTo(
                BitConverter.SingleToUInt32Bits(right.CorrectedMass));
        }
    }
}

/// <summary>Bounded score transform applied after exact mass accumulation.</summary>
public static class SimpleDdgiReceiverFeedbackPriority
{
    public static float Transform(
        float estimatedContributionMass,
        float medianContributionMass,
        uint exactUniqueTileCount,
        float roleBias,
        float massWeight,
        float coverageWeight,
        float cap,
        float epsilon = 1.0e-6f)
    {
        if (!float.IsFinite(estimatedContributionMass) ||
            estimatedContributionMass < 0.0f ||
            !float.IsFinite(medianContributionMass) ||
            medianContributionMass < 0.0f ||
            !float.IsFinite(roleBias) || !float.IsFinite(massWeight) ||
            massWeight < 0.0f || !float.IsFinite(coverageWeight) ||
            coverageWeight < 0.0f || !float.IsFinite(cap) || cap < 0.0f ||
            !float.IsFinite(epsilon) || epsilon <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedContributionMass));
        }

        double massScore = Math.Log2(1.0 + estimatedContributionMass /
            Math.Max(medianContributionMass, epsilon));
        double coverageScore = Math.Log2(1.0 + exactUniqueTileCount);
        double score = massWeight * massScore + coverageWeight * coverageScore +
            roleBias;
        if (!double.IsFinite(score))
            return 0.0f;
        return (float)Math.Clamp(score, 0.0, cap);
    }
}
