using System;
using System.Linq;
using System.Text.Json;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBistroQualityCaptureHarnessTests
{
    [Test]
    public void MotionPath_IsDeterministicContinuousAndCrossesProbeCells()
    {
        var first = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.SteadyMotion);
        var second = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.SteadyMotion);
        SampleBistroQualityCameraBookmark[] cameras = Enumerable.Range(
                0,
                SampleBistroQualityCaptureContract.LoopFrameCount)
            .Select(first.ResolveCamera)
            .ToArray();

        float[] stepDistances = Enumerable.Range(0, cameras.Length)
            .Select(index => Distance(
                cameras[index].Position,
                first.ResolveFrame(index + 1).Camera.Position))
            .ToArray();
        float minimumX = cameras.Min(static camera => camera.Position.X);
        float maximumX = cameras.Max(static camera => camera.Position.X);
        float minimumZ = cameras.Min(static camera => camera.Position.Z);
        float maximumZ = cameras.Max(static camera => camera.Position.Z);
        float largestHorizontalExtent = MathF.Max(
            maximumX - minimumX,
            maximumZ - minimumZ);

        Assert.Multiple(() =>
        {
            Assert.That(first.Fingerprint, Is.EqualTo(second.Fingerprint));
            Assert.That(
                first.CameraPathFingerprint,
                Is.EqualTo(second.CameraPathFingerprint));
            Assert.That(stepDistances, Has.All.GreaterThan(0.0001f));
            Assert.That(stepDistances.Max(), Is.LessThan(0.25f));
            Assert.That(
                largestHorizontalExtent,
                Is.GreaterThanOrEqualTo(
                    SampleBistroQualityCaptureContract.MotionForwardRadius *
                    2.0f));
            Assert.That(
                Distance(
                    first.ResolveFrame(0).Camera.Position,
                    first.ResolveFrame(
                        SampleBistroQualityCaptureContract.LoopFrameCount)
                        .Camera.Position),
                Is.LessThan(1e-6f));
        });
    }

    [Test]
    public void LightingSteps_UseExactBoundedFramesWhileCameraKeepsMoving()
    {
        var scaleContract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.SunScaleStep);
        var directionContract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.SunDirectionStep);

        int measuredStart =
            SampleBistroQualityCaptureContract.FirstMeasuredFrame;
        SampleBistroQualityFrameState before = scaleContract.ResolveFrame(
            measuredStart + 59);
        SampleBistroQualityFrameState scaleStart = scaleContract.ResolveFrame(
            measuredStart + 60);
        SampleBistroQualityFrameState scaleLast = scaleContract.ResolveFrame(
            measuredStart + 179);
        SampleBistroQualityFrameState after = scaleContract.ResolveFrame(
            measuredStart + 180);
        SampleBistroQualityFrameState directionStart =
            directionContract.ResolveFrame(measuredStart + 60);

        Assert.Multiple(() =>
        {
            Assert.That(before.LightingEventActive, Is.False);
            Assert.That(
                scaleContract.ResolveFrame(60).LightingEventActive,
                Is.False,
                "warmup must hold one lighting state while the camera moves");
            Assert.That(before.DirectionalLightScale, Is.EqualTo(1.0f));
            Assert.That(scaleStart.LightingEventActive, Is.True);
            Assert.That(
                scaleStart.DirectionalLightScale,
                Is.EqualTo(
                    SampleBistroQualityCaptureContract
                        .SteppedDirectionalLightScale));
            Assert.That(scaleLast.LightingEventActive, Is.True);
            Assert.That(after.LightingEventActive, Is.False);
            Assert.That(after.DirectionalLightScale, Is.EqualTo(1.0f));
            Assert.That(
                directionStart.DirectionalLightYawOffsetRadians,
                Is.EqualTo(MathF.PI / 18.0f).Within(1e-6f));
            Assert.That(
                Distance(before.Camera.Position, scaleStart.Camera.Position),
                Is.GreaterThan(0.0001f));
            Assert.That(
                Distance(scaleLast.Camera.Position, after.Camera.Position),
                Is.GreaterThan(0.0001f));
        });
    }

    [Test]
    public void HybridRayQueryAb_EnablesRayQueriesOnlyInsideEventWindow()
    {
        var contract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.HybridRayQueryAb);
        int measuredStart =
            SampleBistroQualityCaptureContract.FirstMeasuredFrame;

        Assert.Multiple(() =>
        {
            Assert.That(
                contract.ResolveFrame(measuredStart + 59)
                    .HybridRayQueryEnabled,
                Is.False);
            Assert.That(
                contract.ResolveFrame(measuredStart + 60)
                    .HybridRayQueryEnabled,
                Is.True);
            Assert.That(
                contract.ResolveFrame(measuredStart + 179)
                    .HybridRayQueryEnabled,
                Is.True);
            Assert.That(
                contract.ResolveFrame(measuredStart + 180)
                    .HybridRayQueryEnabled,
                Is.False);
            Assert.That(
                contract.ResolveFrame(60).HybridRayQueryEnabled,
                Is.False);
        });
    }

    [TestCase(7u, 7u, 91u, 91u, 14_800, true)]
    [TestCase(0u, 0u, 91u, 91u, 14_800, false)]
    [TestCase(7u, 6u, 91u, 91u, 14_800, false)]
    [TestCase(7u, 7u, 0u, 0u, 14_800, false)]
    [TestCase(7u, 7u, 92u, 91u, 14_800, false)]
    [TestCase(7u, 7u, 91u, 91u, 0, false)]
    public void MovingWarmupBoundary_RequiresCurrentPublishedLiveField(
        uint sourceGeneration,
        uint liveSourceGeneration,
        uint transportGeneration,
        uint publishedTransportGeneration,
        int sourceReadyProbeCount,
        bool expected)
    {
        Assert.That(
            SampleBistroQualityCaptureRunner.HasCurrentLivePropagationPublication(
                sourceGeneration,
                liveSourceGeneration,
                transportGeneration,
                publishedTransportGeneration,
                sourceReadyProbeCount),
            Is.EqualTo(expected));
    }

    [Test]
    public void PresentationVariant_LocksCameraAndDoesNotRelight()
    {
        var contract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.Presentation);
        SampleBistroQualityFrameState start = contract.ResolveFrame(0);
        SampleBistroQualityFrameState eventFrame = contract.ResolveFrame(60);

        Assert.Multiple(() =>
        {
            Assert.That(contract.UsesContinuousCameraMotion, Is.False);
            Assert.That(
                Distance(start.Camera.Position, eventFrame.Camera.Position),
                Is.Zero.Within(1e-7f));
            Assert.That(eventFrame.DirectionalLightScale, Is.EqualTo(1.0f));
            Assert.That(
                eventFrame.DirectionalLightYawOffsetRadians,
                Is.Zero);
        });
    }

    [Test]
    public void SchemaV8_SerializesDdgiAndSeparateProbeLifecycleEvidence()
    {
        ReflectionProbeLifecycleFrameSnapshot current =
            CreateReflectionLifecycleFrame(
                frameSlot: 1,
                frameSerial: 52,
                captureFaceUnits: 3,
                prefilterMipUnits: 4,
                publishCopyUnits: 0);
        ReflectionProbeLifecycleFrameSnapshot completed =
            CreateReflectionLifecycleFrame(
                frameSlot: 1,
                frameSerial: 50,
                captureFaceUnits: 6,
                prefilterMipUnits: 7,
                publishCopyUnits: 1);
        ReflectionProbeGpuBudgetSnapshot budget = new(
            BudgetMicroseconds: 800,
            ReservedMicroseconds: 275,
            FaceEstimateMicroseconds: 110,
            PrefilterEstimateMicroseconds: 130,
            CopyEstimateMicroseconds: 35,
            HasTimingHistory: true,
            BudgetExhausted: false);
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            ReflectionProbeCurrentLifecycle = current,
            ReflectionProbeCurrentCaptureBudget = budget,
            ReflectionProbeCompletedLifecycle = completed,
            GpuReflectionProbePublishMicroseconds = 417
        };
        SampleBistroQualityFrameTelemetry emptyFrame =
            JsonSerializer.Deserialize<SampleBistroQualityFrameTelemetry>("{}")!;
        SampleBistroQualityFrameTelemetry frame = emptyFrame with
        {
            ReflectionProbeCurrentLifecycle =
                diagnostics.ReflectionProbeCurrentLifecycle,
            ReflectionProbeCurrentCaptureBudget =
                diagnostics.ReflectionProbeCurrentCaptureBudget,
            ReflectionProbeCompletedLifecycle =
                diagnostics.ReflectionProbeCompletedLifecycle,
            GpuReflectionProbePublishMicroseconds =
                diagnostics.GpuReflectionProbePublishMicroseconds,
            HybridReflectionCountersReadbackValid = 1,
            HybridReflectionDdgiFallbackCount = 2,
            HybridReflectionProbeFallbackCount = 0,
            HybridReflectionEnvironmentFallbackCount = 3,
            GpuHybridReflectionDdgiBaseMicroseconds = 211
        };
        var contract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.HybridRayQueryAb);
        var report = new SampleBistroQualityRunReport(
            "njulf-bistro-quality-capture",
            SampleBistroQualityCaptureContract.Schema,
            DateTimeOffset.UnixEpoch,
            "completed",
            contract.Variant,
            contract.Fingerprint,
            contract.CameraPathFingerprint,
            contract.LightingScriptFingerprint,
            SampleBistroQualityCaptureContract.Width,
            SampleBistroQualityCaptureContract.Height,
            SampleBistroQualityCaptureContract.FramesPerSecond,
            [frame],
            [],
            null,
            string.Empty);

        using JsonDocument document = JsonDocument.Parse(
            SampleBistroQualityCaptureRunner.SerializeReport(report));
        JsonElement jsonFrame = document.RootElement.GetProperty("Frames")[0];
        Assert.Multiple(() =>
        {
            Assert.That(SampleBistroQualityCaptureContract.Schema,
                Is.EqualTo("bistro-quality-run/v8"));
            Assert.That(document.RootElement.GetProperty("Schema").GetString(),
                Is.EqualTo("bistro-quality-run/v8"));
            Assert.That(frame.ReflectionProbeCurrentLifecycle, Is.EqualTo(current));
            Assert.That(frame.ReflectionProbeCompletedLifecycle, Is.EqualTo(completed));
            Assert.That(frame.ReflectionProbeCurrentCaptureBudget, Is.EqualTo(budget));
            Assert.That(frame.GpuReflectionProbePublishMicroseconds, Is.EqualTo(417));
            Assert.That(
                jsonFrame.GetProperty("ReflectionProbeCurrentLifecycle")
                    .GetProperty("FrameSerial").GetUInt64(),
                Is.EqualTo(52UL));
            Assert.That(
                jsonFrame.GetProperty("ReflectionProbeCompletedLifecycle")
                    .GetProperty("Lifecycle")
                    .GetProperty("PublishCopyUnitsThisFrame").GetInt32(),
                Is.EqualTo(1));
            Assert.That(
                jsonFrame.GetProperty("ReflectionProbeCurrentCaptureBudget")
                    .GetProperty("ReservedMicroseconds").GetInt32(),
                Is.EqualTo(275));
            Assert.That(
                jsonFrame.GetProperty("GpuReflectionProbePublishMicroseconds")
                    .GetInt64(),
                Is.EqualTo(417));
            Assert.That(
                jsonFrame.GetProperty("HybridReflectionDdgiFallbackCount")
                    .GetUInt32(),
                Is.EqualTo(2u));
            Assert.That(
                jsonFrame.GetProperty("HybridReflectionProbeFallbackCount")
                    .GetUInt32(),
                Is.Zero);
            Assert.That(
                jsonFrame.GetProperty(
                        "GpuHybridReflectionDdgiBaseMicroseconds")
                    .GetInt64(),
                Is.EqualTo(211));
        });
    }

    private static float Distance(Vector3 left, Vector3 right)
    {
        float x = left.X - right.X;
        float y = left.Y - right.Y;
        float z = left.Z - right.Z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    private static ReflectionProbeLifecycleFrameSnapshot CreateReflectionLifecycleFrame(
        int frameSlot,
        ulong frameSerial,
        int captureFaceUnits,
        int prefilterMipUnits,
        int publishCopyUnits) => new(
        Valid: true,
        FrameSlot: frameSlot,
        FrameSerial: frameSerial,
        GpuTimingRecorded: true,
        Lifecycle: new ReflectionProbeLifecycleSnapshot(
            QueuedCount: 1,
            ActiveCount: 1,
            State: ReflectionProbeCaptureState.CapturingFaces,
            AwaitingGpuCompletionCount: 0,
            PublishedCount: 0,
            CapturesStartedThisFrame: 1,
            CapturesCompletedThisFrame: 0,
            CaptureFaceUnitsThisFrame: captureFaceUnits,
            PrefilterMipUnitsThisFrame: prefilterMipUnits,
            PublishCopyUnitsThisFrame: publishCopyUnits,
            CapturesStartedTotal: frameSerial,
            CapturesCompletedTotal: frameSerial - 1,
            CapturesPublishedTotal: frameSerial - 1,
            CaptureFaceUnitsTotal: (ulong)captureFaceUnits,
            PrefilterMipUnitsTotal: (ulong)prefilterMipUnits,
            PublishCopyUnitsTotal: (ulong)publishCopyUnits));
}
