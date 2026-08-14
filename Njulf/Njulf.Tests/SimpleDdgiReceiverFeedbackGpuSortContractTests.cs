using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverFeedbackGpuSortContractTests
{
    [Test]
    public void FrozenGpuAbi_HasExactStructSizesAndFixedBindlessSlots()
    {
        Assert.DoesNotThrow(SimpleDdgiReceiverFeedbackGpuSortAbi.VerifyManagedLayout);

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2>(),
                Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackBankHeaderV2>(),
                Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackRefinementWitnessV1>(),
                Is.EqualTo(16));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackSummaryLocatorV2>(),
                Is.EqualTo(8));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackFallbackPressureV2>(),
                Is.EqualTo(16));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants>(),
                Is.EqualTo(96));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.Version,
                Is.EqualTo(0xB101_1005u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.BankPrefixWordCount,
                Is.EqualTo(20u));
            Assert.That(
                SimpleDdgiReceiverFeedbackGpuSortAbi.HeaderAndRefinementWitnessByteCount,
                Is.EqualTo(80u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.ProducerOverflowKnownMask,
                Is.EqualTo(0x0000_007fu));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.ProducerOverflowUnattributedMask,
                Is.EqualTo(0x8000_0000u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot,
                Is.EqualTo(194u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.SortScratchBindlessSlot,
                Is.EqualTo(195u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot,
                Is.EqualTo(196u));
            Assert.That(BindlessIndex.SimpleDdgiReceiverFeedbackRecordsBuffer,
                Is.EqualTo((int)SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot));
            Assert.That(BindlessIndex.SimpleDdgiReceiverFeedbackSortScratchBuffer,
                Is.EqualTo((int)SimpleDdgiReceiverFeedbackGpuSortAbi.SortScratchBindlessSlot));
            Assert.That(BindlessIndex.SimpleDdgiReceiverFeedbackSummaryBuffer,
                Is.EqualTo((int)SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.RawRecordRadixPassCount,
                Is.EqualTo(28u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.ProbePartialRadixPassCount,
                Is.EqualTo(12u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.FallbackPartialRadixPassCount,
                Is.EqualTo(12u));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity,
                Is.EqualTo(252_645_000u));
        });
    }

    [Test]
    public void Layout_ReservesEveryTransientSidecarAndPersistentSummaryIdentity()
    {
        Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
            recordCapacity: 512u,
            summaryCapacity: 64u,
            fallbackCapacity: 512u,
            out SimpleDdgiReceiverFeedbackGpuSortLayout layout,
            out string reason), Is.True, reason);

        Assert.Multiple(() =>
        {
            Assert.That(layout.RecordBankStrideWords, Is.EqualTo(4096u));
            Assert.That(layout.RecordBanksWords, Is.EqualTo(8192u));
            Assert.That(layout.ScratchTemporaryRecordOffsetWords, Is.Zero);
            Assert.That(layout.ScratchRawMassAOffsetWords, Is.EqualTo(4096u));
            Assert.That(layout.ScratchRawMassBOffsetWords, Is.EqualTo(4608u));
            Assert.That(layout.ScratchRequestedPageAOffsetWords, Is.EqualTo(5120u));
            Assert.That(layout.ScratchRequestedPageBOffsetWords, Is.EqualTo(5632u));
            Assert.That(layout.ScratchFallbackPartialOffsetWords, Is.EqualTo(6144u));
            Assert.That(layout.ScratchRadixPrefixOffsetWords, Is.EqualTo(8192u));
            Assert.That(layout.ScratchRadixBaseOffsetWords, Is.EqualTo(8704u));
            Assert.That(layout.ScratchRequiredWords, Is.EqualTo(8960u));
            Assert.That(layout.RadixWorkgroupCount, Is.EqualTo(2u));
            Assert.That(layout.SummaryLocatorOffsetWords, Is.EqualTo(20u));
            Assert.That(layout.SummaryRecordOffsetWords, Is.EqualTo(148u));
            Assert.That(layout.FallbackPressureOffsetWords, Is.EqualTo(660u));
            Assert.That(layout.SummaryBankStrideWords, Is.EqualTo(2708u));
            Assert.That(layout.SummaryBanksWords, Is.EqualTo(5416u));
            Assert.That(layout.RequiredRecordBanksBytes, Is.EqualTo(32768UL));
            Assert.That(layout.RequiredSortScratchBytes, Is.EqualTo(35840UL));
            Assert.That(layout.RequiredSummaryBanksBytes, Is.EqualTo(21664UL));
            Assert.That(layout.RequiredTotalBytes, Is.EqualTo(90272UL));
        });
    }

    [Test]
    public void Layout_RejectsLossyFallbackCapacityRatherThanAcceptingPartialPressure()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
                64u, 1u, 63u, out _, out string tooSmallReason), Is.False);
            Assert.That(tooSmallReason,
                Does.Contain("full-fallback-capacity"));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
                0u, 1u, 1u, out _, out string zeroReason), Is.False);
            Assert.That(zeroReason, Does.Contain("nonzero"));
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
                SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity + 1u,
                1u,
                SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity + 1u,
                out _, out string addressReason), Is.False);
            Assert.That(addressReason, Does.Contain("addressable"));
        });
    }

    [Test]
    public void CaptureNormalization_PreservesExactIdsAndRejectsBadMassOrGeneration()
    {
        Assert.That(SimpleDdgiReceiverFeedbackV2Abi
            .TryPackConsumerFallbackAndPageGeneration(
                SimpleDdgiReceiverFeedbackProducer.Fog,
                SimpleDdgiReceiverFeedbackFallbackRole.RefinementToBaseFallback,
                pageGeneration: 77u,
                out uint packed), Is.True);
        var candidate = new GPUSimpleDdgiReceiverFeedbackCaptureCandidateV2
        {
            RequestedVirtualProbeId = 17u,
            ResolvedVirtualProbeId = 4u,
            ResolvedVirtualPageId = 91u,
            RequestedVirtualPageId = 92u,
            ExactTileId = 0xf00d00cau,
            InterpolationWeight = 0.25f,
            InverseInclusionProbability = 8.0f,
            PhysicalReceiverContribution = 3.0f,
            PackedConsumerFallbackAndPageGeneration = packed,
            FeedbackGeneration = 5u,
            StableReceiverIdentityLow = 0x12345678u,
            StableReceiverIdentityHigh = 0x9abcdef0u
        };

        bool valid = SimpleDdgiReceiverFeedbackGpuSortAbi.TryNormalizeCaptureCandidate(
            candidate, 5u, out GPUSimpleDdgiReceiverContributionRecordV2 record,
            out float correctedMass, out uint requestedPage);
        candidate.PhysicalReceiverContribution = float.PositiveInfinity;
        bool nonFinite = SimpleDdgiReceiverFeedbackGpuSortAbi.TryNormalizeCaptureCandidate(
            candidate, 5u, out _, out _, out _);
        candidate.PhysicalReceiverContribution = 3.0f;
        candidate.FeedbackGeneration = 6u;
        bool wrongGeneration = SimpleDdgiReceiverFeedbackGpuSortAbi.TryNormalizeCaptureCandidate(
            candidate, 5u, out _, out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(valid, Is.True);
            Assert.That(record.RequestedVirtualProbeId, Is.EqualTo(17u));
            Assert.That(record.ResolvedVirtualProbeId, Is.EqualTo(4u));
            Assert.That(record.ResolvedVirtualPageId, Is.EqualTo(91u));
            Assert.That(record.ExactTileId, Is.EqualTo(0xf00d00cau));
            Assert.That(record.PackedConsumerFallbackAndPageGeneration, Is.EqualTo(packed));
            Assert.That(requestedPage, Is.EqualTo(92u));
            Assert.That(correctedMass, Is.EqualTo(6.0f));
            Assert.That(nonFinite, Is.False);
            Assert.That(wrongGeneration, Is.False);
        });
    }

    [Test]
    public void Header_OnlyConvertsToASchedulablePreviousFrameBankAfterValidatedPublication()
    {
        var header = new GPUSimpleDdgiReceiverFeedbackBankHeaderV2
        {
            LayoutRevision = SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
            EndianSentinel = SimpleDdgiReceiverFeedbackV2Abi.EndianSentinel,
            FeedbackGeneration = 9u,
            ViewportGeneration = 3u,
            FrameSerialLow = 41u,
            FrameSerialHigh = 0u,
            AppendCount = 7u,
            RecordCapacity = 16u,
            ProbePartialCount = 4u,
            FallbackPartialCount = 2u,
            SummaryCount = 2u,
            FallbackSummaryCount = 1u,
            Flags = SimpleDdgiReceiverFeedbackGpuBankFlags.Validated
        };

        Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.IsCompleteAndReadable(header),
            Is.True);
        SimpleDdgiReceiverFeedbackBankValidation validation =
            SimpleDdgiReceiverFeedbackBankValidator.ValidateForScheduling(
                SimpleDdgiReceiverFeedbackGpuSortAbi.ToManagedBankHeader(header),
                SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
                expectedFeedbackGeneration: 9u,
                expectedViewportGeneration: 3u,
                expectedFrameSerial: 42UL);
        header.Flags |= SimpleDdgiReceiverFeedbackGpuBankFlags.AppendOverflow;

        Assert.Multiple(() =>
        {
            Assert.That(validation.UseFeedback, Is.True, validation.Detail);
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.IsCompleteAndReadable(header),
                Is.False);
        });
    }

    [Test]
    public void Header_StrictReaderGateRequiresTheAdmittedSummaryPartition()
    {
        Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
            16u, 1u, 16u, out SimpleDdgiReceiverFeedbackGpuSortLayout layout,
            out string layoutReason), Is.True, layoutReason);
        var header = new GPUSimpleDdgiReceiverFeedbackBankHeaderV2
        {
            LayoutRevision = SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
            EndianSentinel = SimpleDdgiReceiverFeedbackV2Abi.EndianSentinel,
            FeedbackGeneration = 3u,
            ViewportGeneration = 4u,
            AppendCount = 4u,
            RecordCapacity = 16u,
            ProbePartialCount = 2u,
            FallbackPartialCount = 1u,
            SummaryCount = 1u,
            FallbackSummaryCount = 1u,
            Flags = SimpleDdgiReceiverFeedbackGpuBankFlags.Validated
        };
        GPUSimpleDdgiReceiverFeedbackBankHeaderV2 summaryOverCapacity = header;
        summaryOverCapacity.SummaryCount = 2u;
        GPUSimpleDdgiReceiverFeedbackBankHeaderV2 invalidRecord = header;
        invalidRecord.InvalidRecordCount = 1u;

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.IsCompleteAndReadable(
                header, layout), Is.True);
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.IsCompleteAndReadable(
                summaryOverCapacity, layout), Is.False);
            Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.IsCompleteAndReadable(
                invalidRecord, layout), Is.False);
        });
    }

    [Test]
    public void RefinementWitness_SelectsMeasuredMaximumWithStableProbeTieBreak()
    {
        const uint generation = 17u;
        GPUSimpleDdgiReceiverFeedbackSummaryLocatorV2[] locators =
        [
            new() { ResolvedVirtualProbeId = 91u, SummaryGeneration = generation },
            new() { ResolvedVirtualProbeId = 7u, SummaryGeneration = generation },
            new() { ResolvedVirtualProbeId = 33u, SummaryGeneration = generation }
        ];
        GPUSimpleDdgiReceiverContributionSummaryV2 ValidSummary(float mass) => new()
        {
            EstimatedContributionMass = mass,
            MaximumSingleReceiverWeight = 0.75f,
            ExactUniqueTileCount = 3u,
            SampledReceiverCount = 4u,
            ConsumerMask = 1u,
            FeedbackGeneration = generation,
            StatusFlags = (uint)SimpleDdgiReceiverFeedbackSummaryStatus.Validated
        };
        GPUSimpleDdgiReceiverContributionSummaryV2[] summaries =
        [
            ValidSummary(4.0f),
            ValidSummary(4.0f),
            ValidSummary(2.0f)
        ];

        bool selected = SimpleDdgiReceiverFeedbackGpuSortAbi
            .TrySelectRefinementWitness(
                locators,
                summaries,
                generation,
                out GPUSimpleDdgiReceiverFeedbackRefinementWitnessV1 witness);
        var header = new GPUSimpleDdgiReceiverFeedbackBankHeaderV2
        {
            LayoutRevision = SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
            EndianSentinel = SimpleDdgiReceiverFeedbackV2Abi.EndianSentinel,
            FeedbackGeneration = generation,
            ViewportGeneration = 2u,
            FrameSerialLow = 9u,
            RecordCapacity = 8u,
            AppendCount = 3u,
            ProbePartialCount = 3u,
            SummaryCount = 3u,
            Flags = SimpleDdgiReceiverFeedbackGpuBankFlags.Validated
        };
        bool decoded = SimpleDdgiReceiverFeedbackGpuSortAbi
            .TryDecodeRefinementWitness(
                header,
                witness,
                volumeTableGeneration: 23u,
                out SimpleDdgiReceiverFeedbackRefinementWitness decodedWitness);
        bool unstampedDomain = SimpleDdgiReceiverFeedbackGpuSortAbi
            .TryDecodeRefinementWitness(header, witness, 0u, out _);
        summaries[1].StatusFlags = 0u;
        bool malformed = SimpleDdgiReceiverFeedbackGpuSortAbi
            .TrySelectRefinementWitness(locators, summaries, generation, out _);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.True);
            Assert.That(witness.Version,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuSortAbi.RefinementWitnessVersion));
            Assert.That(witness.ResolvedVirtualProbeId, Is.EqualTo(7u));
            Assert.That(witness.EstimatedContributionMass, Is.EqualTo(4.0f));
            Assert.That(witness.FeedbackGeneration, Is.EqualTo(generation));
            Assert.That(decoded, Is.True);
            Assert.That(decodedWitness.ResolvedVirtualProbeId, Is.EqualTo(7u));
            Assert.That(decodedWitness.VolumeTableGeneration, Is.EqualTo(23u));
            Assert.That(unstampedDomain, Is.False);
            Assert.That(malformed, Is.False);
        });
    }

    [Test]
    public void PushConstants_RejectInPlaceRadixAndAllowsDerivedDynamicInputCount()
    {
        Assert.That(SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreateLayout(
            256u, 16u, 256u, out SimpleDdgiReceiverFeedbackGpuSortLayout layout,
            out string layoutReason), Is.True, layoutReason);
        bool dynamicCount = SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreatePushConstants(
            layout,
            SimpleDdgiReceiverFeedbackGpuOperation.RadixHistogram,
            feedbackGeneration: 1u,
            viewportGeneration: 2u,
            frameSerial: 3UL,
            recordBankIndex: 0u,
            summaryBankIndex: 1u,
            inputKind: SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords,
            inputLocation: SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary,
            outputLocation: SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
            radixPassIndex: 27u,
            inputCount: 0u,
            captureSourceBufferIndex: 174u,
            captureSourceRecordOffsetWords: 0u,
            captureSourceRecordCount: 0u,
            captureSourceControlOffsetWords: 0u,
            flags: SimpleDdgiReceiverFeedbackGpuSortFlags.InputRawAuxiliaryBankB,
            constants: out GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants constants,
            reason: out string dynamicReason);
        bool inPlace = SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreatePushConstants(
            layout,
            SimpleDdgiReceiverFeedbackGpuOperation.RadixScatter,
            1u, 2u, 3UL, 0u, 1u,
            SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords,
            SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
            SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
            0u, 0u, 174u, 0u, 0u, 0u,
            SimpleDdgiReceiverFeedbackGpuSortFlags.None,
            out _, out _);
        bool aliasedCapture = SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreatePushConstants(
            layout,
            SimpleDdgiReceiverFeedbackGpuOperation.Capture,
            1u, 2u, 3UL, 0u, 1u,
            SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords,
            SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
            SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
            0u, 0u,
            SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot,
            0u, 0u, 0u,
            SimpleDdgiReceiverFeedbackGpuSortFlags.None,
            out _, out _);

        Assert.Multiple(() =>
        {
            Assert.That(dynamicCount, Is.True, dynamicReason);
            Assert.That(constants.InputCount, Is.Zero);
            Assert.That(constants.RadixByteShift, Is.EqualTo(24u));
            Assert.That(inPlace, Is.False);
            Assert.That(aliasedCapture, Is.False);
        });
    }

    [Test]
    public void RadixDispatchSequence_PreservesRawSidecarsAndLeavesReducedStreamsInExpectedScratch()
    {
        SimpleDdgiReceiverFeedbackGpuRadixDispatch[] raw =
            SimpleDdgiReceiverFeedbackGpuSortAbi.CreateRadixDispatchSequence(
                SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords).ToArray();
        SimpleDdgiReceiverFeedbackGpuRadixDispatch[] probe =
            SimpleDdgiReceiverFeedbackGpuSortAbi.CreateRadixDispatchSequence(
                SimpleDdgiReceiverFeedbackGpuInputKind.ProbePartials).ToArray();
        SimpleDdgiReceiverFeedbackGpuRadixDispatch[] fallback =
            SimpleDdgiReceiverFeedbackGpuSortAbi.CreateRadixDispatchSequence(
                SimpleDdgiReceiverFeedbackGpuInputKind.FallbackPartials).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(raw, Has.Length.EqualTo(28));
            Assert.That(raw[0].InputLocation,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank));
            Assert.That(raw[0].OutputLocation,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary));
            Assert.That(raw[0].Flags,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuSortFlags.OutputRawAuxiliaryBankB));
            Assert.That(raw[1].Flags,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuSortFlags.InputRawAuxiliaryBankB));
            Assert.That(raw[^1].OutputLocation,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank));
            Assert.That(raw[^1].Flags,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuSortFlags.InputRawAuxiliaryBankB));
            Assert.That(raw.Any(pass => pass.InputLocation == pass.OutputLocation), Is.False);
            Assert.That(probe, Has.Length.EqualTo(12));
            Assert.That(probe[^1].OutputLocation,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary));
            Assert.That(fallback, Has.Length.EqualTo(12));
            Assert.That(fallback[^1].OutputLocation,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchFallback));
        });
    }

    [Test]
    public void ShaderSources_UseExactKeysAndValidatedLastPublication()
    {
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string abi = ReadRepoText("Njulf.Shaders", "ddgi_receiver_feedback_abi.glsl");
        string capture = ReadRepoText("Njulf.Shaders", "ddgi_receiver_feedback_capture.comp");
        string histogram = ReadRepoText("Njulf.Shaders", "ddgi_receiver_feedback_radix_histogram.comp");
        string prefix = ReadRepoText("Njulf.Shaders", "ddgi_receiver_feedback_radix_prefix.comp");
        string scatter = ReadRepoText("Njulf.Shaders", "ddgi_receiver_feedback_radix_scatter.comp");
        string reduce = ReadRepoText("Njulf.Shaders", "ddgi_receiver_feedback_reduce.comp");
        string reset = ReadRepoText("Njulf.Shaders", "ddgi_receiver_feedback_reset.comp");
        string summaryReader = ReadRepoText(
            "Njulf.Shaders", "ddgi_receiver_feedback_summary_abi.glsl");
        string scheduler = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_shared.glsl");
        string classify = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_classify.comp");

        Assert.Multiple(() =>
        {
            Assert.That(common, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORDS_BUFFER_INDEX = 194"));
            Assert.That(common, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_SORT_SCRATCH_BUFFER_INDEX = 195"));
            Assert.That(common, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_BUFFER_INDEX = 196"));
            Assert.That(common, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX = 209"));
            Assert.That(abi, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_RAW_RADIX_PASS_COUNT = 28u"));
            Assert.That(abi, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_GPU_SORT_ABI_VERSION = 0xb1011005u"));
            Assert.That(abi, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_PREFIX_WORDS"));
            Assert.That(abi, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_REQUESTED_PROBE"));
            Assert.That(abi, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORD_RESOLVED_PROBE"));
            Assert.That(abi, Does.Contain(
                "SimpleDdgiReceiverFeedbackReadInputRequestedPage(itemIndex)"));
            Assert.That(abi, Does.Not.Contain("ReceiverFeedbackHash("));
            Assert.That(capture, Does.Contain("pageGeneration != 0u"));
            Assert.That(capture, Does.Contain("SimpleDdgiReceiverFeedbackTryCorrectedMass"));
            Assert.That(capture, Does.Contain("captureSourceBufferIndex =="));
            Assert.That(histogram, Does.Contain("atomicAdd(radixHistogram[digit]"));
            Assert.That(prefix, Does.Contain("globalBase != inputCount"));
            Assert.That(scatter, Does.Contain("inputLocation == receiverFeedbackPc.outputLocation"));
            Assert.That(scatter, Does.Contain("radixDestination[localIndex]"));
            Assert.That(scatter, Does.Not.Contain("atomicAdd(radixCursor"));
            Assert.That(reduce, Does.Contain("SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FALLBACK_SUMMARY_COUNT"));
            Assert.That(reduce, Does.Contain(
                "SimpleDdgiReceiverFeedbackFinalizeCandidates"));
            Assert.That(reduce, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_REFINEMENT_WITNESS_VERSION"));
            Assert.That(reduce.IndexOf("memoryBarrierBuffer();", StringComparison.Ordinal),
                Is.LessThan(reduce.LastIndexOf(
                    "SIMPLE_DDGI_RECEIVER_FEEDBACK_BANK_VALIDATED",
                    StringComparison.Ordinal)));
            Assert.That(reset, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_FEEDBACK_HEADER_FLAGS, 0u"));
            Assert.That(summaryReader, Does.Contain(
                "SimpleDdgiFeedbackSummaryTryValidateHeader"));
            Assert.That(summaryReader, Does.Contain(
                "SIMPLE_DDGI_FEEDBACK_SUMMARY_PREFIX_WORDS"));
            Assert.That(summaryReader, Does.Contain(
                "SIMPLE_DDGI_FEEDBACK_HEADER_PRODUCER_OVERFLOW) != 0u"));
            Assert.That(summaryReader, Does.Contain(
                "status != SIMPLE_DDGI_FEEDBACK_SUMMARY_VALIDATED"));
            Assert.That(summaryReader, Does.Contain(
                "for (uint step = 0u; step < 32u && low < high; ++step)"));
            Assert.That(scheduler, Does.Contain(
                "SchedulerPrepareReceiverFeedbackWorkgroup"));
            Assert.That(scheduler, Does.Contain(
                "if (SchedulerExactReceiverFeedback())"));
            Assert.That(classify, Does.Contain(
                "float ordinaryReceiverPrior = visible"));
            Assert.That(classify, Does.Contain(
                "ordinaryReceiverPrior +"));
        });
    }

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
