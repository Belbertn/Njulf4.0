using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>
/// Byte-level contract for the B1 exact receiver-feedback capture/sort/reduce
/// compute programs.  This contract intentionally sits beside, rather than
/// replacing, the 32-byte V2 record ABI: the extra capture mass and requested
/// page are transient sidecars and are never smuggled into persistent record
/// fields.
/// </summary>
public static class SimpleDdgiReceiverFeedbackGpuSortAbi
{
    /// <summary>
    /// Increment when a shader-visible field, scratch partition, or reduction
    /// meaning changes.  The record layout revision remains
    /// <see cref="SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision"/>.
    /// </summary>
    public const uint Version = 0xB101_1004u;

    public const uint RecordBindlessSlot = 194u;
    public const uint SortScratchBindlessSlot = 195u;
    public const uint SummaryBindlessSlot = 196u;

    public const uint WorkgroupSize = 256u;
    public const uint RadixBinCount = 256u;
    public const uint RadixBitsPerPass = 8u;
    /// <summary>
    /// Conservative u32 word-address ceiling shared with GLSL. It leaves room
    /// for the aligned radix-prefix matrix and base bins after all 16C
    /// sidecar/partial words; values above it must fail admission rather than
    /// relying on wrapped shader offsets.
    /// </summary>
    public const uint MaximumRecordCapacity = 252_645_000u;
    public const uint RecordWordCount = 8u;
    public const uint RecordByteCount = RecordWordCount * sizeof(uint);
    public const uint CaptureCandidateWordCount = 12u;
    public const uint CaptureCandidateByteCount = CaptureCandidateWordCount * sizeof(uint);
    public const uint BankHeaderWordCount = 16u;
    public const uint BankHeaderByteCount = BankHeaderWordCount * sizeof(uint);
    public const uint SummaryLocatorWordCount = 2u;
    public const uint SummaryLocatorByteCount = SummaryLocatorWordCount * sizeof(uint);
    public const uint ProbePartialWordCount = 8u;
    public const uint FallbackPartialWordCount = 4u;
    public const uint FallbackPressureWordCount = 4u;
    public const uint FallbackPressureByteCount = FallbackPressureWordCount * sizeof(uint);
    public const uint PushConstantByteCount = 96u;
    public const uint ProducerOverflowKnownMask = 0x0000_007fu;
    public const uint ProducerOverflowUnattributedMask = 0x8000_0000u;

    /// <summary>
    /// The full, non-hashed sort identity consists of generation, producer /
    /// fallback / page-generation, requested page, resolved page, requested
    /// probe, exact tile, and resolved probe.  The requested page is a
    /// transient sidecar rather than a field in the frozen record ABI, but it
    /// remains an exact secondary key so sparse fallback ownership is never
    /// collapsed before its separate reduction. LSD passes process the listed
    /// words from low to high significance.
    /// </summary>
    public const uint RawRecordRadixKeyWordCount = 7u;
    public const uint RawRecordRadixPassCount = RawRecordRadixKeyWordCount * 4u;
    // Build-partials reserves in parallel. Sort the exact partial span
    // (resolved probe, first tile, last tile), rather than relying on atomic
    // reservation order, before joining workgroup boundaries.
    public const uint ProbePartialRadixPassCount = 12u;
    // Corrected mass is a deterministic tie-breaker after the exact requested
    // owner/page key. This prevents a nondeterministic atomic append order
    // from changing FP32 accumulation order for otherwise equal pressure keys.
    public const uint FallbackPartialRadixPassCount = 12u;

    public const uint HeaderLayoutRevisionWord = 0u;
    public const uint HeaderEndianSentinelWord = 1u;
    public const uint HeaderFeedbackGenerationWord = 2u;
    public const uint HeaderViewportGenerationWord = 3u;
    public const uint HeaderFrameSerialLowWord = 4u;
    public const uint HeaderFrameSerialHighWord = 5u;
    public const uint HeaderAppendCountWord = 6u;
    public const uint HeaderDroppedCountWord = 7u;
    public const uint HeaderProducerOverflowMaskWord = 8u;
    public const uint HeaderRecordCapacityWord = 9u;
    public const uint HeaderProbePartialCountWord = 10u;
    public const uint HeaderFallbackPartialCountWord = 11u;
    public const uint HeaderSummaryCountWord = 12u;
    public const uint HeaderFallbackSummaryCountWord = 13u;
    public const uint HeaderInvalidRecordCountWord = 14u;
    public const uint HeaderFlagsWord = 15u;

    public static void VerifyManagedLayout()
    {
        Verify<GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2>(
            CaptureCandidateByteCount,
            (nameof(GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2.RequestedVirtualProbeId), 0),
            (nameof(GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2.ResolvedVirtualPageId), 8),
            (nameof(GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2.ExactTileId), 16),
            (nameof(GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2.InterpolationWeight), 20),
            (nameof(GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2.PhysicalReceiverContribution), 28),
            (nameof(GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2.PackedConsumerFallbackAndPageGeneration), 32),
            (nameof(GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2.StableReceiverIdentityHigh), 44));
        Verify<GPUSimpleDdgiReceiverFeedbackBankHeaderV2>(
            BankHeaderByteCount,
            (nameof(GPUSimpleDdgiReceiverFeedbackBankHeaderV2.LayoutRevision), 0),
            (nameof(GPUSimpleDdgiReceiverFeedbackBankHeaderV2.FrameSerialLow), 16),
            (nameof(GPUSimpleDdgiReceiverFeedbackBankHeaderV2.AppendCount), 24),
            (nameof(GPUSimpleDdgiReceiverFeedbackBankHeaderV2.RecordCapacity), 36),
            (nameof(GPUSimpleDdgiReceiverFeedbackBankHeaderV2.SummaryCount), 48),
            (nameof(GPUSimpleDdgiReceiverFeedbackBankHeaderV2.Flags), 60));
        Verify<GPUSimpleDdgiReceiverFeedbackSummaryLocatorV2>(
            SummaryLocatorByteCount,
            (nameof(GPUSimpleDdgiReceiverFeedbackSummaryLocatorV2.ResolvedVirtualProbeId), 0),
            (nameof(GPUSimpleDdgiReceiverFeedbackSummaryLocatorV2.SummaryGeneration), 4));
        Verify<GPUSimpleDdgiReceiverFeedbackFallbackPressureV2>(
            FallbackPressureByteCount,
            (nameof(GPUSimpleDdgiReceiverFeedbackFallbackPressureV2.RequestedVirtualProbeId), 0),
            (nameof(GPUSimpleDdgiReceiverFeedbackFallbackPressureV2.EstimatedContributionMass), 8));
        Verify<GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants>(
            PushConstantByteCount,
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.RecordCapacity), 8),
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.FrameSerialLow), 28),
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.SummaryBankStrideWords), 44),
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.RadixByteShift), 64),
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.CaptureSourceRecordCount), 80),
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.CaptureSourceControlOffsetWords), 84),
            (nameof(GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants.Flags), 88));
    }

    public static bool TryCreateLayout(
        uint recordCapacity,
        uint summaryCapacity,
        uint fallbackCapacity,
        out SimpleDdgiReceiverFeedbackGpuSortLayout layout,
        out string reason)
    {
        layout = default;
        reason = string.Empty;
        if (recordCapacity == 0u || recordCapacity > MaximumRecordCapacity ||
            summaryCapacity == 0u ||
            fallbackCapacity < recordCapacity)
        {
            reason = "b1-gpu-sort-requires-addressable-nonzero-record-and-summary-capacities-and-full-fallback-capacity";
            return false;
        }

        try
        {
            ulong recordBankWords = checked((ulong)recordCapacity * RecordWordCount);
            ulong recordBanksWords = checked(recordBankWords * 2UL);
            ulong workgroupCount = DivideRoundUp(recordCapacity, WorkgroupSize);

            // A raw record starts in the record bank.  Its exact corrected mass
            // and requested page move in dedicated A/B sidecars as radix passes
            // move the record itself.  Temporary records become probe partials
            // after the raw sort completes, so their lifetime legitimately
            // aliases rather than relying on an undocumented overlap.
            ulong temporaryRecords = 0UL;
            ulong rawMassA = checked(temporaryRecords + recordBankWords);
            ulong rawMassB = checked(rawMassA + recordCapacity);
            ulong requestedPageA = checked(rawMassB + recordCapacity);
            ulong requestedPageB = checked(requestedPageA + recordCapacity);
            ulong fallbackPartials = checked(requestedPageB + recordCapacity);
            ulong radixPrefix = checked(fallbackPartials +
                (ulong)recordCapacity * FallbackPartialWordCount);
            ulong radixBases = checked(radixPrefix + workgroupCount * RadixBinCount);
            ulong scratchWords = checked(radixBases + RadixBinCount);

            // Header + compact virtual-probe locator + fixed 32-byte summary
            // records + bounded requested-page fallback pressure.  The locator
            // keeps the frozen 32-byte summary ABI intact without pretending a
            // sparse virtual ID is a physical-probe array index.
            ulong summaryLocator = BankHeaderWordCount;
            ulong summaryRecords = checked(summaryLocator +
                (ulong)summaryCapacity * SummaryLocatorWordCount);
            ulong fallbackRecords = checked(summaryRecords +
                (ulong)summaryCapacity * RecordWordCount);
            ulong summaryBankWords = checked(fallbackRecords +
                (ulong)fallbackCapacity * FallbackPressureWordCount);
            ulong summaryBanksWords = checked(summaryBankWords * 2UL);

            if (recordBankWords > uint.MaxValue || recordBanksWords > uint.MaxValue ||
                scratchWords > uint.MaxValue || summaryBankWords > uint.MaxValue ||
                summaryBanksWords > uint.MaxValue)
            {
                reason = "b1-gpu-sort-word-offset-exceeds-u32-shader-address-contract";
                return false;
            }

            layout = new SimpleDdgiReceiverFeedbackGpuSortLayout(
                recordCapacity,
                summaryCapacity,
                fallbackCapacity,
                checked((uint)recordBankWords),
                checked((uint)recordBanksWords),
                checked((uint)temporaryRecords),
                checked((uint)rawMassA),
                checked((uint)rawMassB),
                checked((uint)requestedPageA),
                checked((uint)requestedPageB),
                checked((uint)fallbackPartials),
                checked((uint)radixPrefix),
                checked((uint)radixBases),
                checked((uint)scratchWords),
                checked((uint)workgroupCount),
                checked((uint)summaryLocator),
                checked((uint)summaryRecords),
                checked((uint)fallbackRecords),
                checked((uint)summaryBankWords),
                checked((uint)summaryBanksWords));
            return true;
        }
        catch (OverflowException)
        {
            reason = "b1-gpu-sort-byte-layout-overflow";
            return false;
        }
    }

    /// <summary>
    /// Builds a validated push block.  A zero <paramref name="inputCount"/>
    /// asks the shader to consume the current count from the write-bank
    /// header; integration may use that form with GPU indirect dispatch.
    /// </summary>
    public static bool TryCreatePushConstants(
        in SimpleDdgiReceiverFeedbackGpuSortLayout layout,
        SimpleDdgiReceiverFeedbackGpuOperation operation,
        uint feedbackGeneration,
        uint viewportGeneration,
        ulong frameSerial,
        uint recordBankIndex,
        uint summaryBankIndex,
        SimpleDdgiReceiverFeedbackGpuInputKind inputKind,
        SimpleDdgiReceiverFeedbackGpuItemLocation inputLocation,
        SimpleDdgiReceiverFeedbackGpuItemLocation outputLocation,
        uint radixPassIndex,
        uint inputCount,
        uint captureSourceBufferIndex,
        uint captureSourceRecordOffsetWords,
        uint captureSourceRecordCount,
        uint captureSourceControlOffsetWords,
        SimpleDdgiReceiverFeedbackGpuSortFlags flags,
        out GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants constants,
        out string reason)
    {
        constants = default;
        reason = string.Empty;
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(inputKind) ||
            !Enum.IsDefined(inputLocation) || !Enum.IsDefined(outputLocation) ||
            recordBankIndex > 1u || summaryBankIndex > 1u ||
            feedbackGeneration == 0u || viewportGeneration == 0u ||
            frameSerial == ulong.MaxValue ||
            !AreLocationsCompatible(inputKind, inputLocation, outputLocation) ||
            (operation == SimpleDdgiReceiverFeedbackGpuOperation.RadixScatter &&
             inputLocation == outputLocation) ||
            ((uint)flags & ~(uint)SimpleDdgiReceiverFeedbackGpuSortFlags.KnownMask) != 0u)
        {
            reason = "b1-gpu-sort-push-constants-invalid";
            return false;
        }

        if (operation == SimpleDdgiReceiverFeedbackGpuOperation.Capture &&
            (captureSourceBufferIndex == RecordBindlessSlot ||
             captureSourceBufferIndex == SortScratchBindlessSlot ||
             captureSourceBufferIndex == SummaryBindlessSlot))
        {
            // The candidate staging buffer must never alias a B1 output. A
            // self-read/write capture dispatch would make an append prefix
            // data-dependent and cannot be recovered by later validation.
            reason = "b1-gpu-sort-capture-source-aliases-fixed-b1-output-slot";
            return false;
        }

        uint passCount = GetRadixPassCount(inputKind);
        if (operation is SimpleDdgiReceiverFeedbackGpuOperation.RadixHistogram or
            SimpleDdgiReceiverFeedbackGpuOperation.RadixPrefix or
            SimpleDdgiReceiverFeedbackGpuOperation.RadixScatter)
        {
            if (radixPassIndex >= passCount ||
                !TryGetRadixDispatch(inputKind, radixPassIndex,
                    out SimpleDdgiReceiverFeedbackGpuRadixDispatch expectedDispatch) ||
                inputLocation != expectedDispatch.InputLocation ||
                outputLocation != expectedDispatch.OutputLocation ||
                flags != expectedDispatch.Flags)
            {
                reason = "b1-gpu-sort-radix-dispatch-does-not-match-strict-ping-pong-abi";
                return false;
            }
        }
        else if (radixPassIndex != 0u || flags != SimpleDdgiReceiverFeedbackGpuSortFlags.None)
        {
            reason = "b1-gpu-sort-non-radix-operation-has-radix-state";
            return false;
        }

        uint maximumInput = inputKind ==
            SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords
            ? layout.RecordCapacity
            : inputKind == SimpleDdgiReceiverFeedbackGpuInputKind.ProbePartials
                ? layout.RecordCapacity
                // Fallback partials are one-for-one with captured records;
                // their persistent output can be larger, but scratch cannot
                // accept more than the admitted append capacity.
                : layout.RecordCapacity;
        if (inputCount > maximumInput ||
            captureSourceRecordCount > layout.RecordCapacity)
        {
            reason = "b1-gpu-sort-input-exceeds-admitted-bounded-capacity";
            return false;
        }

        constants = new GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants
        {
            AbiVersion = Version,
            Operation = operation,
            RecordCapacity = layout.RecordCapacity,
            SummaryCapacity = layout.SummaryCapacity,
            FallbackCapacity = layout.FallbackCapacity,
            FeedbackGeneration = feedbackGeneration,
            ViewportGeneration = viewportGeneration,
            FrameSerialLow = unchecked((uint)frameSerial),
            FrameSerialHigh = checked((uint)(frameSerial >> 32)),
            RecordBankIndex = recordBankIndex,
            SummaryBankIndex = summaryBankIndex,
            SummaryBankStrideWords = layout.SummaryBankStrideWords,
            InputCount = inputCount,
            InputKind = inputKind,
            InputLocation = inputLocation,
            OutputLocation = outputLocation,
            RadixByteShift = (radixPassIndex & 3u) * RadixBitsPerPass,
            RadixPassIndex = radixPassIndex,
            CaptureSourceBufferIndex = captureSourceBufferIndex,
            CaptureSourceRecordOffsetWords = captureSourceRecordOffsetWords,
            CaptureSourceRecordCount = captureSourceRecordCount,
            CaptureSourceControlOffsetWords = captureSourceControlOffsetWords,
            Flags = flags
        };
        return true;
    }

    public static uint GetRadixPassCount(
        SimpleDdgiReceiverFeedbackGpuInputKind inputKind) => inputKind switch
    {
        SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords =>
            RawRecordRadixPassCount,
        SimpleDdgiReceiverFeedbackGpuInputKind.ProbePartials =>
            ProbePartialRadixPassCount,
        SimpleDdgiReceiverFeedbackGpuInputKind.FallbackPartials =>
            FallbackPartialRadixPassCount,
        _ => throw new ArgumentOutOfRangeException(nameof(inputKind))
    };

    /// <summary>
    /// Produces the only legal ping-pong sequence for each sort stream.  Raw
    /// records additionally toggle their exact corrected-mass and requested-
    /// page sidecars; callers should consume this sequence rather than hand-
    /// assembling 24 pass constants.
    /// </summary>
    public static IReadOnlyList<SimpleDdgiReceiverFeedbackGpuRadixDispatch>
        CreateRadixDispatchSequence(
            SimpleDdgiReceiverFeedbackGpuInputKind inputKind)
    {
        uint passCount = GetRadixPassCount(inputKind);
        var result = new SimpleDdgiReceiverFeedbackGpuRadixDispatch[
            checked((int)passCount)];
        for (uint pass = 0u; pass < passCount; ++pass)
        {
            _ = TryGetRadixDispatch(inputKind, pass, out result[pass]);
        }
        return result;
    }

    /// <summary>
    /// Returns the sole legal direction and raw-sidecar state for one radix
    /// pass. Hosts must use this instead of reconstructing ping-pong parity;
    /// <see cref="TryCreatePushConstants"/> rejects any other direction.
    /// </summary>
    public static bool TryGetRadixDispatch(
        SimpleDdgiReceiverFeedbackGpuInputKind inputKind,
        uint passIndex,
        out SimpleDdgiReceiverFeedbackGpuRadixDispatch dispatch)
    {
        dispatch = default;
        if (!Enum.IsDefined(inputKind) || passIndex >= GetRadixPassCount(inputKind))
            return false;

        bool usesInitialLocation = (passIndex & 1u) == 0u;
        SimpleDdgiReceiverFeedbackGpuItemLocation initialLocation = inputKind switch
        {
            SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords =>
                SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
            SimpleDdgiReceiverFeedbackGpuInputKind.ProbePartials =>
                SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary,
            SimpleDdgiReceiverFeedbackGpuInputKind.FallbackPartials =>
                SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchFallback,
            _ => throw new ArgumentOutOfRangeException(nameof(inputKind))
        };
        SimpleDdgiReceiverFeedbackGpuItemLocation alternateLocation = inputKind ==
            SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords
            ? SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary
            : SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank;
        SimpleDdgiReceiverFeedbackGpuItemLocation input = usesInitialLocation
            ? initialLocation
            : alternateLocation;
        SimpleDdgiReceiverFeedbackGpuItemLocation output = usesInitialLocation
            ? alternateLocation
            : initialLocation;
        SimpleDdgiReceiverFeedbackGpuSortFlags flags =
            inputKind == SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords
                ? (usesInitialLocation
                    ? SimpleDdgiReceiverFeedbackGpuSortFlags.OutputRawAuxiliaryBankB
                    : SimpleDdgiReceiverFeedbackGpuSortFlags.InputRawAuxiliaryBankB)
                : SimpleDdgiReceiverFeedbackGpuSortFlags.None;
        dispatch = new SimpleDdgiReceiverFeedbackGpuRadixDispatch(
            inputKind,
            passIndex,
            input,
            output,
            flags);
        return true;
    }

    public static bool TryNormalizeCaptureCandidate(
        in GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2 candidate,
        uint expectedFeedbackGeneration,
        out GPUSimpleDdgiReceiverContributionRecordV2 record,
        out float correctedContributionMass,
        out uint requestedVirtualPageId)
    {
        record = default;
        correctedContributionMass = 0.0f;
        requestedVirtualPageId = 0u;
        if (expectedFeedbackGeneration == 0u ||
            candidate.FeedbackGeneration != expectedFeedbackGeneration ||
            !float.IsFinite(candidate.InterpolationWeight) ||
            candidate.InterpolationWeight < 0.0f ||
            candidate.InterpolationWeight > 1.0f ||
            !float.IsFinite(candidate.InverseInclusionProbability) ||
            candidate.InverseInclusionProbability < 1.0f ||
            !float.IsFinite(candidate.PhysicalReceiverContribution) ||
            candidate.PhysicalReceiverContribution < 0.0f ||
            !SimpleDdgiReceiverFeedbackV2Abi.CanRepresentPageGeneration(
                SimpleDdgiReceiverFeedbackV2Abi.UnpackPageGeneration(
                    candidate.PackedConsumerFallbackAndPageGeneration)) ||
            !Enum.IsDefined(SimpleDdgiReceiverFeedbackV2Abi.UnpackProducer(
                candidate.PackedConsumerFallbackAndPageGeneration)) ||
            !Enum.IsDefined(SimpleDdgiReceiverFeedbackV2Abi.UnpackFallbackRole(
                candidate.PackedConsumerFallbackAndPageGeneration)) ||
            !TryComputeCorrectedContributionMass(
                candidate.PhysicalReceiverContribution,
                candidate.InterpolationWeight,
                candidate.InverseInclusionProbability,
                out correctedContributionMass))
        {
            return false;
        }

        record = new GPUSimpleDdgiReceiverContributionRecordV2
        {
            RequestedVirtualProbeId = candidate.RequestedVirtualProbeId,
            ResolvedVirtualProbeId = candidate.ResolvedVirtualProbeId,
            ResolvedVirtualPageId = candidate.ResolvedVirtualPageId,
            ExactTileId = candidate.ExactTileId,
            InterpolationWeight = candidate.InterpolationWeight,
            InverseInclusionProbability = candidate.InverseInclusionProbability,
            PackedConsumerFallbackAndPageGeneration =
                candidate.PackedConsumerFallbackAndPageGeneration,
            FeedbackGeneration = candidate.FeedbackGeneration
        };
        requestedVirtualPageId = candidate.RequestedVirtualPageId;
        return true;
    }

    public static bool TryComputeCorrectedContributionMass(
        float physicalReceiverContribution,
        float interpolationWeight,
        float inverseInclusionProbability,
        out float correctedContributionMass)
    {
        correctedContributionMass = 0.0f;
        if (!float.IsFinite(physicalReceiverContribution) ||
            physicalReceiverContribution < 0.0f ||
            !float.IsFinite(interpolationWeight) ||
            interpolationWeight < 0.0f || interpolationWeight > 1.0f ||
            !float.IsFinite(inverseInclusionProbability) ||
            inverseInclusionProbability < 1.0f)
        {
            return false;
        }

        double corrected = (double)physicalReceiverContribution *
            interpolationWeight * inverseInclusionProbability;
        if (!double.IsFinite(corrected) || corrected < 0.0 || corrected > float.MaxValue)
            return false;
        correctedContributionMass = (float)corrected;
        return float.IsFinite(correctedContributionMass);
    }

    private static bool AreLocationsCompatible(
        SimpleDdgiReceiverFeedbackGpuInputKind inputKind,
        SimpleDdgiReceiverFeedbackGpuItemLocation inputLocation,
        SimpleDdgiReceiverFeedbackGpuItemLocation outputLocation) => inputKind switch
    {
        SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords or
        SimpleDdgiReceiverFeedbackGpuInputKind.ProbePartials =>
            (inputLocation is SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank or
                SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary) &&
            (outputLocation is SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank or
                SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary),
        SimpleDdgiReceiverFeedbackGpuInputKind.FallbackPartials =>
            (inputLocation is SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank or
                SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchFallback) &&
            (outputLocation is SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank or
                SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchFallback),
        _ => false
    };

    public static bool IsCompleteAndReadable(
        in GPUSimpleDdgiReceiverFeedbackBankHeaderV2 header)
    {
        SimpleDdgiReceiverFeedbackGpuBankFlags flags = header.Flags;
        return header.LayoutRevision == SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision &&
            header.EndianSentinel == SimpleDdgiReceiverFeedbackV2Abi.EndianSentinel &&
            header.FeedbackGeneration != 0u && header.ViewportGeneration != 0u &&
            (header.FrameSerialLow != uint.MaxValue ||
                header.FrameSerialHigh != uint.MaxValue) &&
            header.RecordCapacity != 0u && header.AppendCount <= header.RecordCapacity &&
            header.DroppedCount == 0u && header.ProducerOverflowMask == 0u &&
            header.InvalidRecordCount == 0u &&
            header.ProbePartialCount <= header.AppendCount &&
            header.FallbackPartialCount <= header.AppendCount &&
            header.SummaryCount <= header.ProbePartialCount &&
            header.FallbackSummaryCount <= header.FallbackPartialCount &&
            (flags & SimpleDdgiReceiverFeedbackGpuBankFlags.Validated) != 0 &&
            (flags & SimpleDdgiReceiverFeedbackGpuBankFlags.FailureMask) == 0 &&
            (flags & ~(SimpleDdgiReceiverFeedbackGpuBankFlags.Validated |
                SimpleDdgiReceiverFeedbackGpuBankFlags.FailureMask)) == 0;
    }

    /// <summary>
    /// Strict reader gate used by the scheduler/runtime.  The header alone
    /// cannot prove that its compact counts fit the currently bound summary
    /// partition, so consumers must validate it against the admitted B1 GPU
    /// layout before indexing locators, summaries, or fallback pressure.
    /// </summary>
    public static bool IsCompleteAndReadable(
        in GPUSimpleDdgiReceiverFeedbackBankHeaderV2 header,
        in SimpleDdgiReceiverFeedbackGpuSortLayout layout) =>
        IsCompleteAndReadable(header) &&
        header.RecordCapacity == layout.RecordCapacity &&
        header.SummaryCount <= layout.SummaryCapacity &&
        header.FallbackSummaryCount <= layout.FallbackCapacity;

    public static SimpleDdgiReceiverFeedbackBankHeader ToManagedBankHeader(
        in GPUSimpleDdgiReceiverFeedbackBankHeaderV2 header) => new(
            header.LayoutRevision,
            header.FeedbackGeneration,
            header.ViewportGeneration,
            ((ulong)header.FrameSerialHigh << 32) | header.FrameSerialLow,
            header.AppendCount,
            header.DroppedCount,
            header.ProducerOverflowMask,
            header.RecordCapacity,
            (SimpleDdgiReceiverFeedbackBankFlags)(uint)header.Flags);

    private static ulong DivideRoundUp(uint value, uint divisor) =>
        ((ulong)value + divisor - 1UL) / divisor;

    private static void Verify<T>(
        uint expectedSize,
        params (string Field, int Offset)[] expectedOffsets)
        where T : struct
    {
        int actualSize = Marshal.SizeOf<T>();
        if (actualSize != expectedSize)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} is {actualSize} bytes; expected {expectedSize}.");
        }

        foreach ((string field, int expectedOffset) in expectedOffsets)
        {
            int actualOffset = checked((int)Marshal.OffsetOf<T>(field));
            if (actualOffset != expectedOffset)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{field} is at byte {actualOffset}; " +
                    $"expected {expectedOffset}.");
            }
        }
    }
}

/// <summary>GPU-visible bank-state bits.  The numerical values deliberately
/// mirror <see cref="SimpleDdgiReceiverFeedbackBankFlags"/>.</summary>
[Flags]
public enum SimpleDdgiReceiverFeedbackGpuBankFlags : uint
{
    None = 0u,
    Validated = 1u << 0,
    AppendOverflow = 1u << 1,
    ProducerRangeOverflow = 1u << 2,
    NonFiniteInput = 1u << 3,
    SortOrReduceFailure = 1u << 4,
    FailureMask = AppendOverflow | ProducerRangeOverflow | NonFiniteInput |
        SortOrReduceFailure
}

public enum SimpleDdgiReceiverFeedbackGpuOperation : uint
{
    Reset = 0u,
    Capture = 1u,
    RadixHistogram = 2u,
    RadixPrefix = 3u,
    RadixScatter = 4u,
    BuildPartials = 5u,
    ReduceProbeSummaries = 6u,
    ReduceFallbackPressure = 7u,
    Finalize = 8u
}

public enum SimpleDdgiReceiverFeedbackGpuInputKind : uint
{
    RawRecords = 0u,
    ProbePartials = 1u,
    FallbackPartials = 2u
}

public enum SimpleDdgiReceiverFeedbackGpuItemLocation : uint
{
    RecordBank = 0u,
    ScratchTemporary = 1u,
    ScratchFallback = 2u
}

/// <summary>One bounded histogram/prefix/scatter iteration.  The renderer
/// dispatches all three shaders with this exact descriptor/sidecar direction.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackGpuRadixDispatch(
    SimpleDdgiReceiverFeedbackGpuInputKind InputKind,
    uint PassIndex,
    SimpleDdgiReceiverFeedbackGpuItemLocation InputLocation,
    SimpleDdgiReceiverFeedbackGpuItemLocation OutputLocation,
    SimpleDdgiReceiverFeedbackGpuSortFlags Flags);

[Flags]
public enum SimpleDdgiReceiverFeedbackGpuSortFlags : uint
{
    None = 0u,
    InputRawAuxiliaryBankB = 1u << 0,
    OutputRawAuxiliaryBankB = 1u << 1,
    KnownMask = InputRawAuxiliaryBankB | OutputRawAuxiliaryBankB
}

/// <summary>
/// Full memory partition that must be admitted before a B1 exact GPU pass is
/// allowed to bind its three fixed descriptors.  The layout is deliberately
/// larger than the historical 16-byte-per-record placeholder scratch value;
/// activating this ABI with the old allocation would be memory corruption.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackGpuSortLayout(
    uint RecordCapacity,
    uint SummaryCapacity,
    uint FallbackCapacity,
    uint RecordBankStrideWords,
    uint RecordBanksWords,
    uint ScratchTemporaryRecordOffsetWords,
    uint ScratchRawMassAOffsetWords,
    uint ScratchRawMassBOffsetWords,
    uint ScratchRequestedPageAOffsetWords,
    uint ScratchRequestedPageBOffsetWords,
    uint ScratchFallbackPartialOffsetWords,
    uint ScratchRadixPrefixOffsetWords,
    uint ScratchRadixBaseOffsetWords,
    uint ScratchRequiredWords,
    uint RadixWorkgroupCount,
    uint SummaryLocatorOffsetWords,
    uint SummaryRecordOffsetWords,
    uint FallbackPressureOffsetWords,
    uint SummaryBankStrideWords,
    uint SummaryBanksWords)
{
    public ulong RequiredRecordBanksBytes => (ulong)RecordBanksWords * sizeof(uint);
    public ulong RequiredSortScratchBytes => (ulong)ScratchRequiredWords * sizeof(uint);
    public ulong RequiredSummaryBanksBytes => (ulong)SummaryBanksWords * sizeof(uint);
    public ulong RequiredTotalBytes => checked(RequiredRecordBanksBytes +
        RequiredSortScratchBytes + RequiredSummaryBanksBytes);
}

/// <summary>48-byte transient source record produced by a sparse receiver
/// reconstruction or a real gather producer.  Its extra requested page and
/// physical mass are moved through scratch sidecars, preserving the frozen
/// 32-byte append record exactly.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2
{
    public uint RequestedVirtualProbeId;
    public uint ResolvedVirtualProbeId;
    public uint ResolvedVirtualPageId;
    public uint RequestedVirtualPageId;
    public uint ExactTileId;
    public float InterpolationWeight;
    public float InverseInclusionProbability;
    public float PhysicalReceiverContribution;
    public uint PackedConsumerFallbackAndPageGeneration;
    public uint FeedbackGeneration;
    public uint StableReceiverIdentityLow;
    public uint StableReceiverIdentityHigh;
}

/// <summary>64-byte bank header stored at the start of each summary bank.
/// <see cref="Flags"/> is clear during writes and receives Validated only as
/// the final store of the finalize dispatch.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct GPUSimpleDdgiReceiverFeedbackBankHeaderV2
{
    public uint LayoutRevision;
    public uint EndianSentinel;
    public uint FeedbackGeneration;
    public uint ViewportGeneration;
    public uint FrameSerialLow;
    public uint FrameSerialHigh;
    public uint AppendCount;
    public uint DroppedCount;
    public uint ProducerOverflowMask;
    public uint RecordCapacity;
    public uint ProbePartialCount;
    public uint FallbackPartialCount;
    public uint SummaryCount;
    public uint FallbackSummaryCount;
    public uint InvalidRecordCount;
    public SimpleDdgiReceiverFeedbackGpuBankFlags Flags;
}

/// <summary>Associates a compact summary record with its exact sparse virtual
/// resolved-probe identity without enlarging the frozen 32-byte summary.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public struct GPUSimpleDdgiReceiverFeedbackSummaryLocatorV2
{
    public uint ResolvedVirtualProbeId;
    public uint SummaryGeneration;
}

/// <summary>Persistent requested-owner pressure emitted separately from the
/// resolved-owner summary stream.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct GPUSimpleDdgiReceiverFeedbackFallbackPressureV2
{
    public uint RequestedVirtualProbeId;
    public uint RequestedVirtualPageId;
    public float EstimatedContributionMass;
    public uint SampledReceiverCount;
}

/// <summary>96-byte push block shared by reset, capture, radix, and reduction
/// programs.  All bank/sidecar addresses are derived from the admitted layout,
/// avoiding host-controlled arbitrary offsets.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public struct GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants
{
    public uint AbiVersion;
    public SimpleDdgiReceiverFeedbackGpuOperation Operation;
    public uint RecordCapacity;
    public uint SummaryCapacity;
    public uint FallbackCapacity;
    public uint FeedbackGeneration;
    public uint ViewportGeneration;
    public uint FrameSerialLow;
    public uint FrameSerialHigh;
    public uint RecordBankIndex;
    public uint SummaryBankIndex;
    public uint SummaryBankStrideWords;
    public uint InputCount;
    public SimpleDdgiReceiverFeedbackGpuInputKind InputKind;
    public SimpleDdgiReceiverFeedbackGpuItemLocation InputLocation;
    public SimpleDdgiReceiverFeedbackGpuItemLocation OutputLocation;
    public uint RadixByteShift;
    public uint RadixPassIndex;
    public uint CaptureSourceBufferIndex;
    public uint CaptureSourceRecordOffsetWords;
    public uint CaptureSourceRecordCount;
    public uint CaptureSourceControlOffsetWords;
    public SimpleDdgiReceiverFeedbackGpuSortFlags Flags;
    public uint Reserved0;
}
