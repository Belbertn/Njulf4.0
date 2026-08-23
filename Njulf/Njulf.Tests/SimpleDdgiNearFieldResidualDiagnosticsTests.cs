using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualDiagnosticsTests
{
    [Test]
    public void StageTimingJoin_SumsResetAndAllFilterIterations()
    {
        var snapshot = new FrameTimingSnapshot(
        [
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Reset,
                0L, 11L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Prepare,
                0L, 13L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Trace,
                0L, 101L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Temporal,
                0L, 53L, true),
            new PassTiming(
                SimpleDdgiNearFieldResidualGpuPassNames.FilterIteration(0),
                0L, 29L, true),
            new PassTiming(
                SimpleDdgiNearFieldResidualGpuPassNames.FilterIteration(1),
                0L, 23L, true),
            new PassTiming(
                SimpleDdgiNearFieldResidualGpuPassNames.FrequencySeparation,
                0L, 19L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Composite,
                0L, 17L, true)
        ]);

        bool available = SimpleDdgiNearFieldResidualVulkanRuntime
            .TryResolveStageTimings(snapshot, 2, out var timings);

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True);
            Assert.That(timings.SourceMicroseconds, Is.Zero);
            Assert.That(timings.PrepareCompactionMicroseconds, Is.EqualTo(24UL));
            Assert.That(timings.RawTraceMicroseconds, Is.EqualTo(101UL));
            Assert.That(timings.TemporalMicroseconds, Is.EqualTo(53UL));
            Assert.That(timings.FilterMicroseconds, Is.EqualTo(52UL));
            Assert.That(timings.FrequencySeparationMicroseconds,
                Is.EqualTo(19UL));
            Assert.That(timings.CompositeMicroseconds, Is.EqualTo(17UL));
            Assert.That(timings.TotalMicroseconds, Is.EqualTo(266UL));
        });
    }

    [Test]
    public void StageTimingJoin_FailsClosedWhenOneIterationIsUnavailable()
    {
        var snapshot = new FrameTimingSnapshot(
        [
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Reset,
                0L, 1L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Prepare,
                0L, 1L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Trace,
                0L, 2L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Temporal,
                0L, 3L, true),
            new PassTiming(
                SimpleDdgiNearFieldResidualGpuPassNames.FilterIteration(0),
                0L, 4L, true),
            new PassTiming(
                SimpleDdgiNearFieldResidualGpuPassNames.FrequencySeparation,
                0L, 1L, true),
            new PassTiming(SimpleDdgiNearFieldResidualGpuPassNames.Composite,
                0L, 5L, true)
        ]);

        Assert.That(
            SimpleDdgiNearFieldResidualVulkanRuntime.TryResolveStageTimings(
                snapshot,
                2,
                out _),
            Is.False);
    }

    [Test]
    public void PendingRendererIntegration_ExposesPlanBytesButNoFabricatedGpuTelemetry()
    {
        SimpleDdgiNearFieldResidualMemoryTelemetry memory = new(
            RequestedBytes: 1_024UL,
            AdmittedBytes: 960UL,
            AllocatedBytes: 900UL,
            PeakAllocatedBytes: 900UL,
            RetiredBytes: 120UL);

        SimpleDdgiNearFieldResidualDiagnostics telemetry =
            SimpleDdgiNearFieldResidualDiagnostics.PendingRendererIntegration(memory);

        Assert.Multiple(() =>
        {
            Assert.That(telemetry.Readback.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualReadbackState.PendingRendererIntegration));
            Assert.That(telemetry.IsAuthoritativeReadback, Is.False);
            Assert.That(telemetry.Memory.RequestedBytes, Is.EqualTo(1_024UL));
            Assert.That(telemetry.Memory.AdmittedBytes, Is.EqualTo(960UL));
            Assert.That(telemetry.Memory.AllocatedBytes, Is.Zero);
            Assert.That(telemetry.Memory.PeakAllocatedBytes, Is.Zero);
            Assert.That(telemetry.Memory.RetiredBytes, Is.Zero);
            Assert.That(telemetry.Timings, Is.EqualTo(
                SimpleDdgiNearFieldResidualStageTimings.Empty));
            Assert.That(telemetry.Trace, Is.EqualTo(
                SimpleDdgiNearFieldResidualTraceTelemetry.Empty));
            Assert.That(telemetry.History, Is.EqualTo(
                SimpleDdgiNearFieldResidualHistoryTelemetry.Empty));
            Assert.That(telemetry.ResidualEnergy, Is.EqualTo(
                SimpleDdgiNearFieldResidualEnergyTelemetry.Empty));
            Assert.That(telemetry.Tiles, Is.EqualTo(
                SimpleDdgiNearFieldResidualTileTelemetry.Empty));
            Assert.That(telemetry.CaptureIdentifiers,
                Is.EqualTo(SimpleDdgiNearFieldResidualCaptureIdentifiers.None));
        });
    }

    [Test]
    public void PendingGpuReadback_PreservesMeasuredLiveAllocationWithoutClaimingCounters()
    {
        var memory = new SimpleDdgiNearFieldResidualMemoryTelemetry(
            1_024UL, 1_024UL, 992UL, 992UL, 0UL);

        SimpleDdgiNearFieldResidualDiagnostics telemetry =
            SimpleDdgiNearFieldResidualDiagnostics.PendingGpuReadback(memory)
                .NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(telemetry.Readback.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback));
            Assert.That(telemetry.IsAuthoritativeReadback, Is.False);
            Assert.That(telemetry.Memory.AllocatedBytes, Is.EqualTo(992UL));
            Assert.That(telemetry.Memory.PeakAllocatedBytes, Is.EqualTo(992UL));
            Assert.That(telemetry.Trace,
                Is.EqualTo(SimpleDdgiNearFieldResidualTraceTelemetry.Empty));
        });
    }

    [Test]
    public void NewSubmission_PreservesLastFenceValidCounterWitness()
    {
        var witness = new SimpleDdgiNearFieldResidualCompletionWitness(
            CompletedFrameSerial: 17UL,
            Trace: SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
            History: SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
            ResidualEnergy: SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
            Tiles: SimpleDdgiNearFieldResidualTileTelemetry.Empty,
            TotalTraceSteps: 0UL,
            TotalMipVisits: 0UL,
            TotalRefinementVisits: 0UL,
            MaximumTraceDistance: 0.0f,
            MaximumTraceStepCount: 0U,
            MaximumMipVisitCount: 0U);
        var memory = new SimpleDdgiNearFieldResidualMemoryTelemetry(
            1_024UL, 1_024UL, 992UL, 992UL, 0UL);

        SimpleDdgiNearFieldResidualDiagnostics telemetry =
            SimpleDdgiNearFieldResidualVulkanRuntime
                .CreatePendingReadbackDiagnostics(
                    witness,
                    memory,
                    pendingFrameSerial: 20UL);

        Assert.Multiple(() =>
        {
            Assert.That(telemetry.Readback.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback));
            Assert.That(telemetry.Readback.CounterReadbackValid, Is.True);
            Assert.That(telemetry.Readback.TimingReadbackValid, Is.False);
            Assert.That(telemetry.Readback.CompletedFrameSerial, Is.EqualTo(17UL));
            Assert.That(telemetry.Readback.AgeFrames, Is.EqualTo(3U));
            Assert.That(telemetry.Memory.AllocatedBytes, Is.EqualTo(992UL));
        });
    }

    [Test]
    public void FenceCompletionValidator_RequiresExactTileCoverageAndHeaderSums()
    {
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                18,
                18,
                SimpleDdgiNearFieldResidualProfile.HalfResolutionReference,
                16UL * 1024UL * 1024UL);
        Assert.That(layout.IsValid, Is.True);
        Assert.That((layout.TraceWidth, layout.TraceHeight), Is.EqualTo((9, 9)));
        var words = new uint[checked((int)(layout.TileBuffersBytes / 4UL))];
        const uint covered = 64U;
        uint validSum = 0U;
        uint invalidSum = 0U;
        uint raySum = 0U;
        uint hitSum = 0U;
        int headerWords = checked((int)
            SimpleDdgiNearFieldResidualGpuAbi.TelemetryHeaderWordCount);
        int tileWords = checked((int)
            SimpleDdgiNearFieldResidualGpuAbi.TileRecordWordCount);
        for (int tile = 0; tile < 1; tile++)
        {
            int word = headerWords + tile * tileWords;
            const uint valid = 56U;
            uint invalid = covered - valid;
            uint rays = valid;
            const uint hits = 48U;
            const uint accepted = 32U;
            words[word] = (uint)tile;
            words[word + 1] = PackTraceCounts(covered, valid, invalid, rays);
            words[word + 2] = PackTileCounts(0U, invalid, 0U, 0U);
            words[word + 3] = 0U;
            words[word + 4] = PackTileCounts(0U, valid, accepted, 0U);
            words[word + 5] = 0U;
            words[word + 6] = 0U;
            words[word + 7] = 12U | (4U << 16);
            words[word + 8] = 2U | (12U << 16) | (4U << 25);
            words[word + 9] = BitConverter.SingleToUInt32Bits(
                4.0f);
            words[word + 10] = BitConverter.SingleToUInt32Bits(
                0.25f);
            words[word + 11] = BitConverter.SingleToUInt32Bits(
                -1.0f);
            words[word + 12] = BitConverter.SingleToUInt32Bits(
                3.0f);
            words[word + 13] = BitConverter.SingleToUInt32Bits(
                5.0f);
            words[word + 14] = BitConverter.SingleToUInt32Bits(
                0.75f);
            uint halfDistance = BitConverter.HalfToUInt16Bits((Half)7.5f);
            words[word + 15] = halfDistance |
                (SimpleDdgiNearFieldResidualGpuAbi.TelemetryRequiredCompletionMask << 16);
            validSum += valid;
            invalidSum += invalid;
            raySum += rays;
            hitSum += hits;
        }
        words[0] = SimpleDdgiNearFieldResidualGpuAbi.TelemetryMagic;
        words[1] = SimpleDdgiNearFieldResidualGpuAbi.Version;
        words[2] = 17U;
        words[3] = 0U;
        words[4] = (uint)layout.TraceWidth;
        words[5] = (uint)layout.TraceHeight;
        const uint tileCount = 4U;
        words[6] = tileCount;
        words[7] =
            SimpleDdgiNearFieldResidualGpuAbi.TelemetryRequiredCompletionMask;
        words[8] = validSum + invalidSum;
        words[9] = raySum;
        words[10] = hitSum;
        words[11] = raySum - hitSum;
        words[12] = invalidSum;
        words[13] = 0U;
        words[14] = invalidSum;
        words[15] = 0U;
        words[16] = tileCount;
        words[17] = 1U;
        words[18] = 1U;
        words[19] = tileCount - 1U;
        words[20] = 0U;

        bool validReadback =
            SimpleDdgiNearFieldResidualCompletionValidator.TryValidate(
                words,
                layout,
                17UL,
                out SimpleDdgiNearFieldResidualCompletionWitness witness,
                out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(validReadback, Is.True, reason);
            Assert.That(witness.CompletedFrameSerial, Is.EqualTo(17UL));
            Assert.That(witness.Trace.CandidateReceiverCount, Is.EqualTo(64UL));
            Assert.That(witness.Trace.RayHitCount, Is.EqualTo(48UL));
            Assert.That(witness.Trace.RayMissCount, Is.EqualTo(8UL));
            Assert.That(witness.Trace.InvalidReceiverRejectedCount,
                Is.EqualTo(8UL));
            Assert.That(witness.History.AcceptedHistoryCount, Is.EqualTo(32UL));
            Assert.That(witness.Tiles.CandidateTileCount, Is.EqualTo(4U));
            Assert.That(witness.Tiles.CompactedTileCount, Is.EqualTo(1U));
            Assert.That(witness.Tiles.ActiveTileCount, Is.EqualTo(1U));
            Assert.That(witness.Tiles.EmptyTileCount, Is.EqualTo(3U));
            Assert.That(witness.MaximumTraceDistance, Is.EqualTo(7.5f));
            Assert.That(witness.ResidualEnergy.LowFrequencyLeakage,
                Is.EqualTo(1.0 / 56.0).Within(1.0e-6));
        });

        words[8]--;
        Assert.That(SimpleDdgiNearFieldResidualCompletionValidator.TryValidate(
            words, layout, 17UL, out _, out reason), Is.False);
        Assert.That(reason, Is.EqualTo(
            "near-field-completion-header-summary-mismatch"));
    }

    private static uint PackTileCounts(uint x, uint y, uint z, uint w)
    {
        Assert.That(x, Is.LessThanOrEqualTo(64U));
        Assert.That(y, Is.LessThanOrEqualTo(64U));
        Assert.That(z, Is.LessThanOrEqualTo(64U));
        Assert.That(w, Is.LessThanOrEqualTo(64U));
        return x | (y << 8) | (z << 16) | (w << 24);
    }

    private static uint PackTraceCounts(uint covered, uint valid, uint invalid,
        uint rays)
    {
        Assert.That(covered, Is.LessThanOrEqualTo(64U));
        Assert.That(valid, Is.LessThanOrEqualTo(64U));
        Assert.That(invalid, Is.LessThanOrEqualTo(64U));
        Assert.That(rays, Is.LessThanOrEqualTo(256U));
        return covered | (valid << 7) | (invalid << 14) | (rays << 21);
    }

    [Test]
    public void AuthoritativeReadback_PreservesCompleteC5ObservabilityPayload()
    {
        SimpleDdgiNearFieldResidualDiagnostics telemetry =
            SimpleDdgiNearFieldResidualDiagnostics.CreateAuthoritative(
                completedFrameSerial: 73UL,
                ageFrames: 2U,
                memory: new SimpleDdgiNearFieldResidualMemoryTelemetry(
                    RequestedBytes: 1_000UL,
                    AdmittedBytes: 960UL,
                    AllocatedBytes: 928UL,
                    PeakAllocatedBytes: 128UL,
                    RetiredBytes: 64UL),
                timings: new SimpleDdgiNearFieldResidualStageTimings(
                    SourceMicroseconds: 11UL,
                    RawTraceMicroseconds: 17UL,
                    TemporalMicroseconds: 19UL,
                    FilterMicroseconds: 23UL,
                    CompositeMicroseconds: 29UL),
                trace: new SimpleDdgiNearFieldResidualTraceTelemetry(
                    CandidateReceiverCount: 100UL,
                    RaysLaunched: 90UL,
                    RayHitCount: 60UL,
                    RayMissCount: 30UL,
                    EdgeRejectedCount: 7UL,
                    InvalidReceiverRejectedCount: 2UL,
                    InvalidRayRejectedCount: 3UL,
                    TraceStepBudgetRejectedCount: 5UL,
                    MipVisitBudgetRejectedCount: 11UL,
                    DepthRejectedCount: 13UL,
                    NormalRejectedCount: 17UL,
                    TraceSourceRejectedCount: 19UL,
                    NonFiniteRejectedCount: 23UL),
                history: new SimpleDdgiNearFieldResidualHistoryTelemetry(
                    CandidateHistoryCount: 80UL,
                    AcceptedHistoryCount: 50UL,
                    RejectedHistoryCount: 30UL,
                    InvalidCurrentRejectedCount: 2UL,
                    HistoryEpochRejectedCount: 3UL,
                    HitIdentityRejectedCount: 5UL,
                    ReprojectionRejectedCount: 7UL,
                    DepthRejectedCount: 11UL,
                    NormalRejectedCount: 13UL,
                    TraceSourceRevisionRejectedCount: 17UL,
                    VarianceClippedCount: 19UL,
                    MeanVariance: 0.25,
                    MaximumVariance: 1.5),
                residualEnergy: new SimpleDdgiNearFieldResidualEnergyTelemetry(
                    SampleCount: 64UL,
                    SignedResidualEnergy: -1.25,
                    AbsoluteResidualEnergy: 3.5,
                    SquaredResidualEnergy: 5.75,
                    MaximumAbsoluteResidualEnergy: 0.875,
                    LowFrequencyLeakage: 0.125,
                    NonFiniteRejectedCount: 1UL),
                tiles: new SimpleDdgiNearFieldResidualTileTelemetry(
                    TileCapacity: 64U,
                    CandidateTileCount: 48U,
                    CompactedTileCount: 31U,
                    EmptyTileCount: 17U,
                    OverflowTileCount: 2U,
                    TileRecordBytes: 496UL),
                captureIdentifiers: new SimpleDdgiNearFieldResidualCaptureIdentifiers(
                    DebugCaptureId: "c5-debug-73",
                    ReferenceCaptureId: "reference-corpus-22"))
            with
            {
                AdaptiveResolution =
                    new SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry(
                        SampledExtent:
                            new SimpleDdgiNearFieldResidualExecutionExtent(
                                480,
                                270,
                                SimpleDdgiNearFieldResidualExecutionScale.Quarter,
                                1U),
                        ActiveExtent:
                            new SimpleDdgiNearFieldResidualExecutionExtent(
                                240,
                                135,
                                SimpleDdgiNearFieldResidualExecutionScale.Eighth,
                                2U),
                        MaximumScale:
                            SimpleDdgiNearFieldResidualExecutionScale.Quarter,
                        LastP95Microseconds: 800UL,
                        AuthoritativeTimingSampleCount: 120UL,
                        WindowSampleCount: 0U,
                        PromotionWindowStreak: 0U,
                        PromotionCount: 0U,
                        DemotionCount: 1U,
                        ResolutionChangedAfterSample: true)
            };

        Assert.Multiple(() =>
        {
            Assert.That(telemetry.IsAuthoritativeReadback, Is.True);
            Assert.That(telemetry.Readback.CompletedFrameSerial, Is.EqualTo(73UL));
            Assert.That(telemetry.Readback.AgeFrames, Is.EqualTo(2U));
            Assert.That(telemetry.Memory.PeakAllocatedBytes, Is.EqualTo(928UL));
            Assert.That(telemetry.Timings.TotalMicroseconds, Is.EqualTo(99UL));
            Assert.That(telemetry.Trace.RayHitCount, Is.EqualTo(60UL));
            Assert.That(telemetry.Trace.RayMissCount, Is.EqualTo(30UL));
            Assert.That(telemetry.Trace.EdgeRejectedCount, Is.EqualTo(7UL));
            Assert.That(telemetry.History.AcceptedHistoryCount, Is.EqualTo(50UL));
            Assert.That(telemetry.History.RejectedHistoryCount, Is.EqualTo(30UL));
            Assert.That(telemetry.History.MaximumVariance, Is.EqualTo(1.5));
            Assert.That(telemetry.ResidualEnergy.SignedResidualEnergy, Is.EqualTo(-1.25));
            Assert.That(telemetry.ResidualEnergy.AbsoluteResidualEnergy, Is.EqualTo(3.5));
            Assert.That(telemetry.Tiles.CompactedTileCount, Is.EqualTo(31U));
            Assert.That(telemetry.ContractVersion,
                Is.EqualTo(SimpleDdgiNearFieldResidualDiagnostics
                    .CurrentContractVersion));
            Assert.That(telemetry.AdaptiveResolution.SampledExtent.Scale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Quarter));
            Assert.That(telemetry.AdaptiveResolution.ActiveExtent.Scale,
                Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Eighth));
            Assert.That(telemetry.AdaptiveResolution.LastP95Microseconds,
                Is.EqualTo(800UL));
            Assert.That(telemetry.AdaptiveResolution.DemotionCount, Is.EqualTo(1U));
            Assert.That(telemetry.AdaptiveResolution.ResolutionChangedAfterSample,
                Is.True);
            Assert.That(telemetry.CaptureIdentifiers.DebugCaptureId, Is.EqualTo("c5-debug-73"));
            Assert.That(telemetry.CaptureIdentifiers.ReferenceCaptureId,
                Is.EqualTo("reference-corpus-22"));
        });
    }

    [Test]
    public void InvalidStatistics_FailClosedWithoutPublishingCounters()
    {
        SimpleDdgiNearFieldResidualDiagnostics telemetry =
            SimpleDdgiNearFieldResidualDiagnostics.CreateAuthoritative(
                completedFrameSerial: 9UL,
                ageFrames: 0U,
                memory: new SimpleDdgiNearFieldResidualMemoryTelemetry(
                    128UL, 128UL, 96UL, 96UL, 12UL),
                timings: new SimpleDdgiNearFieldResidualStageTimings(1UL, 2UL, 3UL, 4UL, 5UL),
                trace: new SimpleDdgiNearFieldResidualTraceTelemetry(
                    1UL, 1UL, 1UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL),
                history: new SimpleDdgiNearFieldResidualHistoryTelemetry(
                    1UL, 1UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0.0, 0.0),
                residualEnergy: new SimpleDdgiNearFieldResidualEnergyTelemetry(
                    1UL, 0.0, 0.0, 0.0, 0.0, 0.0, 0UL),
                tiles: new SimpleDdgiNearFieldResidualTileTelemetry(1U, 1U, 1U, 0U, 0U, 16UL),
                captureIdentifiers: new SimpleDdgiNearFieldResidualCaptureIdentifiers(
                    "debug", "reference"));

        SimpleDdgiNearFieldResidualDiagnostics invalid = telemetry with
        {
            ResidualEnergy = telemetry.ResidualEnergy with
            {
                AbsoluteResidualEnergy = double.NaN
            }
        };
        SimpleDdgiNearFieldResidualDiagnostics normalized =
            invalid.NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Readback.State,
                Is.EqualTo(SimpleDdgiNearFieldResidualReadbackState.Faulted));
            Assert.That(normalized.IsAuthoritativeReadback, Is.False);
            Assert.That(normalized.Memory.RequestedBytes, Is.EqualTo(128UL));
            Assert.That(normalized.Memory.AdmittedBytes, Is.EqualTo(128UL));
            Assert.That(normalized.Memory.AllocatedBytes, Is.EqualTo(96UL));
            Assert.That(normalized.Timings,
                Is.EqualTo(SimpleDdgiNearFieldResidualStageTimings.Empty));
            Assert.That(normalized.Trace,
                Is.EqualTo(SimpleDdgiNearFieldResidualTraceTelemetry.Empty));
            Assert.That(normalized.CaptureIdentifiers,
                Is.EqualTo(SimpleDdgiNearFieldResidualCaptureIdentifiers.None));
        });
    }
}
