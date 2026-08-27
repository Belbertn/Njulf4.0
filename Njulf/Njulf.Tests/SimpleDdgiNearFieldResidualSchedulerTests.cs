using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualSchedulerTests
{
    [Test]
    public void V15ArenaOwnsIndependentFullCapacityTraceAndResolveLists()
    {
        const uint capacity = 713u;
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiNearFieldResidualGpuAbi.Version,
                Is.EqualTo(0x4335_000Fu));
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi.TraceListFirstWord,
                Is.EqualTo(64u));
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi
                .ResolveListFirstWord(capacity), Is.EqualTo(64u + capacity));
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi
                .ArenaByteCount(capacity),
                Is.EqualTo((ulong)(64u + capacity * 2u) * sizeof(uint)));
            Assert.That(Marshal.SizeOf<
                GPUSimpleDdgiNearFieldResidualSchedulerRecord>(), Is.EqualTo(16));
        });
    }

    [Test]
    public void NewChangedOrDisoccludedReceiverCanNeverUseHistoryOnly()
    {
        SimpleDdgiNearFieldResidualSchedulerThresholds thresholds =
            SimpleDdgiNearFieldResidualSchedulerThresholds.ForPreset(
                SimpleDdgiNearFieldResidualQualityPreset.Balanced);
        SimpleDdgiNearFieldResidualSchedulerInput stable = StableInput();

        foreach (SimpleDdgiNearFieldResidualSchedulerInput changed in new[]
        {
            stable with { HistoryValid = false },
            stable with { ReprojectionValid = false },
            stable with { ReceiverIdentityMatches = false },
            stable with { StructuralEpochMatches = false },
            stable with { LightingEpochMatches = false },
            stable with { MaximumMotion = thresholds.HighMotion + 0.001f }
        })
        {
            SimpleDdgiNearFieldResidualSchedulerDecision decision =
                SimpleDdgiNearFieldResidualScheduler.Select(changed, thresholds);
            Assert.That(decision.TileClass,
                Is.EqualTo(SimpleDdgiNearFieldResidualTileClass.TraceHigh));
            Assert.That(decision.AppendTrace, Is.True);
            Assert.That(decision.AppendResolve, Is.True);
        }
    }

    [Test]
    public void StableEnergyUsesNormalInterleavedAndHistoryOnlyBands()
    {
        SimpleDdgiNearFieldResidualSchedulerThresholds thresholds =
            SimpleDdgiNearFieldResidualSchedulerThresholds.ForPreset(
                SimpleDdgiNearFieldResidualQualityPreset.Balanced);
        SimpleDdgiNearFieldResidualSchedulerInput input = StableInput() with
        {
            FrameSerial = 1UL,
            TileX = 11u,
            TileY = 7u
        };

        SimpleDdgiNearFieldResidualSchedulerDecision normal =
            SimpleDdgiNearFieldResidualScheduler.Select(input with
            {
                SignedResidualEnergy = thresholds.ActiveEnergy * 1.1f
            }, thresholds);
        SimpleDdgiNearFieldResidualSchedulerDecision interleaved =
            SimpleDdgiNearFieldResidualScheduler.Select(input with
            {
                SignedResidualEnergy = thresholds.PerceptualEnergyFloor * 1.1f
            }, thresholds);
        SimpleDdgiNearFieldResidualSchedulerDecision history =
            SimpleDdgiNearFieldResidualScheduler.Select(input with
            {
                SignedResidualEnergy = thresholds.PerceptualEnergyFloor * 0.25f,
                Variance = 0.0f
            }, thresholds);

        Assert.Multiple(() =>
        {
            Assert.That(normal.TileClass,
                Is.EqualTo(SimpleDdgiNearFieldResidualTileClass.TraceNormal));
            Assert.That(normal.RaysPerSelectedPixel, Is.EqualTo(2u));
            Assert.That(interleaved.TileClass,
                Is.EqualTo(SimpleDdgiNearFieldResidualTileClass.TraceInterleaved));
            Assert.That(interleaved.RaysPerSelectedPixel, Is.EqualTo(1u));
            Assert.That(history.TileClass,
                Is.EqualTo(SimpleDdgiNearFieldResidualTileClass.HistoryOnly));
            Assert.That(history.AppendResolve, Is.True);
            Assert.That(history.AppendTrace, Is.False);
        });
    }

    [Test]
    public void AgeForcesRefreshAndRayTierNeverExceedsGlobalMaximum()
    {
        SimpleDdgiNearFieldResidualSchedulerThresholds thresholds =
            SimpleDdgiNearFieldResidualSchedulerThresholds.ForPreset(
                SimpleDdgiNearFieldResidualQualityPreset.Quality);
        SimpleDdgiNearFieldResidualSchedulerDecision decision =
            SimpleDdgiNearFieldResidualScheduler.Select(StableInput() with
            {
                Age = thresholds.MaximumHistoryOnlyAge,
                MaximumRaysPerPixel = 1u
            }, thresholds);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ForcedRefresh, Is.True);
            Assert.That(decision.TileClass,
                Is.EqualTo(SimpleDdgiNearFieldResidualTileClass.TraceNormal));
            Assert.That(decision.RaysPerSelectedPixel, Is.EqualTo(1u));
        });
    }

    [Test]
    public void PackedSchedulerStateRoundTripsEveryControlLane()
    {
        uint packed = SimpleDdgiNearFieldResidualAdaptiveAbi.PackState(
            SimpleDdgiNearFieldResidualTileClass.TraceInterleaved,
            checkerboardPhase: 1u,
            rayCount: 2u,
            valid: true,
            age: 37u,
            confidence: 0.75f);

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi.UnpackClass(packed),
                Is.EqualTo(SimpleDdgiNearFieldResidualTileClass.TraceInterleaved));
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi.UnpackPhase(packed),
                Is.EqualTo(1u));
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi.UnpackRayCount(packed),
                Is.EqualTo(2u));
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi.UnpackAge(packed),
                Is.EqualTo(37u));
            Assert.That(SimpleDdgiNearFieldResidualAdaptiveAbi
                .UnpackConfidence(packed), Is.EqualTo(0.75f).Within(1.0f / 255.0f));
        });
    }

    [Test]
    public void LocalAdaptiveCandidateDefaultsOff()
    {
        var settings = new GlobalIlluminationSettings();
        Assert.That(
            settings.SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled,
            Is.False);
    }

    private static SimpleDdgiNearFieldResidualSchedulerInput StableInput() => new(
        ReceiverOccupied: true,
        HistoryValid: true,
        ReprojectionValid: true,
        ReceiverIdentityMatches: true,
        StructuralEpochMatches: true,
        LightingEpochMatches: true,
        MaximumMotion: 0.0f,
        SignedResidualEnergy: 0.0f,
        Variance: 0.0f,
        Confidence: 1.0f,
        Age: 1u,
        TileX: 3u,
        TileY: 5u,
        FrameSerial: 101UL,
        MaximumRaysPerPixel: 4u);
}
