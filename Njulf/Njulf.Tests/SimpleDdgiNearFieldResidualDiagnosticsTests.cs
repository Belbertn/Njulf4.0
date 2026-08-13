using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualDiagnosticsTests
{
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
        uint[] covered = [64U, 8U, 8U, 1U];
        uint validSum = 0U;
        uint invalidSum = 0U;
        uint raySum = 0U;
        int headerWords = checked((int)
            SimpleDdgiNearFieldResidualGpuAbi.TelemetryHeaderWordCount);
        int tileWords = checked((int)
            SimpleDdgiNearFieldResidualGpuAbi.TileRecordWordCount);
        for (int tile = 0; tile < covered.Length; tile++)
        {
            int word = headerWords + tile * tileWords;
            uint valid = tile == 0 ? 48U : 0U;
            uint invalid = covered[tile] - valid;
            uint accepted = tile == 0 ? 32U : 0U;
            words[word] = (uint)tile;
            words[word + 1] = PackTileCounts(covered[tile], valid, invalid, valid);
            words[word + 2] = PackTileCounts(0U, invalid, 0U, 0U);
            words[word + 3] = 0U;
            words[word + 4] = PackTileCounts(0U, valid, accepted, 0U);
            words[word + 5] = 0U;
            words[word + 6] = 0U;
            words[word + 7] = tile == 0 ? 12U | (4U << 16) : 0U;
            words[word + 8] = tile == 0
                ? 2U | (12U << 16) | (4U << 25)
                : 0U;
            words[word + 9] = BitConverter.SingleToUInt32Bits(
                tile == 0 ? 4.0f : 0.0f);
            words[word + 10] = BitConverter.SingleToUInt32Bits(
                tile == 0 ? 0.25f : 0.0f);
            words[word + 11] = BitConverter.SingleToUInt32Bits(
                tile == 0 ? -1.0f : 0.0f);
            words[word + 12] = BitConverter.SingleToUInt32Bits(
                tile == 0 ? 3.0f : 0.0f);
            words[word + 13] = BitConverter.SingleToUInt32Bits(
                tile == 0 ? 5.0f : 0.0f);
            words[word + 14] = BitConverter.SingleToUInt32Bits(
                tile == 0 ? 0.75f : 0.0f);
            uint halfDistance = BitConverter.HalfToUInt16Bits((Half)7.5f);
            words[word + 15] = halfDistance |
                (SimpleDdgiNearFieldResidualGpuAbi.TelemetryRequiredCompletionMask << 16);
            validSum += valid;
            invalidSum += invalid;
            raySum += valid;
        }
        words[0] = SimpleDdgiNearFieldResidualGpuAbi.TelemetryMagic;
        words[1] = SimpleDdgiNearFieldResidualGpuAbi.Version;
        words[2] = 17U;
        words[3] = 0U;
        words[4] = (uint)layout.TraceWidth;
        words[5] = (uint)layout.TraceHeight;
        words[6] = (uint)covered.Length;
        words[7] =
            SimpleDdgiNearFieldResidualGpuAbi.TelemetryRequiredCompletionMask;
        words[8] = validSum + invalidSum;
        words[9] = raySum;
        words[10] = validSum;
        words[11] = raySum - validSum;
        words[12] = invalidSum;
        words[13] = 0U;
        words[14] = invalidSum;
        words[15] = 0U;

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
            Assert.That(witness.Trace.CandidateReceiverCount, Is.EqualTo(81UL));
            Assert.That(witness.Trace.RayHitCount, Is.EqualTo(48UL));
            Assert.That(witness.Trace.InvalidReceiverRejectedCount,
                Is.EqualTo(33UL));
            Assert.That(witness.History.AcceptedHistoryCount, Is.EqualTo(32UL));
            Assert.That(witness.Tiles.CandidateTileCount, Is.EqualTo(4U));
            Assert.That(witness.Tiles.CompactedTileCount, Is.EqualTo(1U));
            Assert.That(witness.MaximumTraceDistance, Is.EqualTo(7.5f));
            Assert.That(witness.ResidualEnergy.LowFrequencyLeakage,
                Is.EqualTo(1.0 / 48.0).Within(1.0e-6));
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
                    ReferenceCaptureId: "reference-corpus-22"));

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
