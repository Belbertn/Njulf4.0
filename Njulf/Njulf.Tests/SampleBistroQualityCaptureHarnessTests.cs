using System;
using System.Linq;
using Njulf.Core.Math;
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
    public void ReflectionSourceAb_DisablesDdgiOnlyInsideEventWindow()
    {
        var contract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.ReflectionSourceAb);
        int measuredStart =
            SampleBistroQualityCaptureContract.FirstMeasuredFrame;

        Assert.Multiple(() =>
        {
            Assert.That(
                contract.ResolveFrame(measuredStart + 59)
                    .ReflectionCaptureIncludesDdgi,
                Is.False);
            Assert.That(
                contract.ResolveFrame(measuredStart + 60)
                    .ReflectionCaptureIncludesDdgi,
                Is.True);
            Assert.That(
                contract.ResolveFrame(measuredStart + 179)
                    .ReflectionCaptureIncludesDdgi,
                Is.True);
            Assert.That(
                contract.ResolveFrame(measuredStart + 180)
                    .ReflectionCaptureIncludesDdgi,
                Is.False);
            Assert.That(
                contract.ResolveFrame(60).ReflectionCaptureIncludesDdgi,
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
    public void ReflectionPromotion_UsesLivePublicationWithoutRequiringTailAudit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                HelloGame.IsBistroDdgiReflectionPromotionReady(
                    sourceGeneration: 7,
                    livePropagationSourceGeneration: 7,
                    propagationGeneration: 91,
                    publishedPropagationGeneration: 91,
                    staleSourceProbeCount: 0,
                    pendingSolverProbeCount: 0),
                Is.True,
                "an exact audit may remain pending while moving live GI is coherent");
            Assert.That(
                HelloGame.IsBistroDdgiReflectionPromotionReady(
                    sourceGeneration: 7,
                    livePropagationSourceGeneration: 7,
                    propagationGeneration: 92,
                    publishedPropagationGeneration: 91,
                    staleSourceProbeCount: 0,
                    pendingSolverProbeCount: 0),
                Is.False,
                "a stale publication must not feed a new reflection capture");
            Assert.That(
                HelloGame.IsBistroDdgiReflectionPromotionReady(
                    sourceGeneration: 7,
                    livePropagationSourceGeneration: 7,
                    propagationGeneration: 91,
                    publishedPropagationGeneration: 91,
                    staleSourceProbeCount: 0,
                    pendingSolverProbeCount: 1),
                Is.False);
        });
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

    private static float Distance(Vector3 left, Vector3 right)
    {
        float x = left.X - right.X;
        float y = left.Y - right.Y;
        float z = left.Z - right.Z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }
}
