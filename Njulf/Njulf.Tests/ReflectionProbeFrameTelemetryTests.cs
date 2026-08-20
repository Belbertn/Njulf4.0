using System;
using Njulf.Rendering;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ReflectionProbeFrameTelemetryTests
{
    [Test]
    public void CompletionPulseSurvivesPollAndUploadUntilNextBeginCaptureFrame()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1);
        ReflectionProbeCaptureFrameCounters counters = default;
        counters.BeginCaptureFrame();

        // PollCaptureCompletions records the pulse. Upload has no reset authority,
        // so observing the same manager-owned state afterward must preserve it.
        counters.RecordCompletedCapture();
        ReflectionProbeLifecycleSnapshot afterPoll =
            ReflectionProbeLifecycleSnapshotFactory.Create(
                scheduler,
                publishedCount: 1,
                capturesCompletedTotal: 1UL,
                counters);
        ReflectionProbeLifecycleSnapshot afterUpload =
            ReflectionProbeLifecycleSnapshotFactory.Create(
                scheduler,
                publishedCount: 1,
                capturesCompletedTotal: 1UL,
                counters);

        counters.BeginCaptureFrame();
        ReflectionProbeLifecycleSnapshot nextFrame =
            ReflectionProbeLifecycleSnapshotFactory.Create(
                scheduler,
                publishedCount: 1,
                capturesCompletedTotal: 1UL,
                counters);

        Assert.Multiple(() =>
        {
            Assert.That(afterPoll.CapturesCompletedThisFrame, Is.EqualTo(1));
            Assert.That(afterUpload.CapturesCompletedThisFrame, Is.EqualTo(1));
            Assert.That(afterUpload.CapturesCompletedTotal, Is.EqualTo(1UL));
            Assert.That(nextFrame.CapturesCompletedThisFrame, Is.Zero);
            Assert.That(nextFrame.CapturesCompletedTotal, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void CaptureStartCountsOnlyFaceZeroWhileRetainingGranularUnitCounts()
    {
        ReflectionProbeCaptureFrameCounters counters = default;
        counters.BeginCaptureFrame();

        for (int face = 0; face < 6; face++)
        {
            var work = new ReflectionProbeWork(
                ReflectionProbeWorkKind.CaptureFace,
                default,
                Face: face,
                Mip: 0);
            bool startsCapture = ReflectionProbeManager.CountsAsCaptureStart(work);
            Assert.That(startsCapture, Is.EqualTo(face == 0));
            counters.RecordStartedUnit(work.Kind, startsCapture);
        }
        for (int mip = 1; mip <= 3; mip++)
        {
            var work = new ReflectionProbeWork(
                ReflectionProbeWorkKind.PrefilterMip,
                default,
                Face: -1,
                Mip: mip);
            Assert.That(ReflectionProbeManager.CountsAsCaptureStart(work), Is.False);
            counters.RecordStartedUnit(work.Kind, startsCapture: false);
        }
        var publish = new ReflectionProbeWork(
            ReflectionProbeWorkKind.PublishCopy,
            default,
            Face: -1,
            Mip: -1);
        Assert.That(ReflectionProbeManager.CountsAsCaptureStart(publish), Is.False);
        counters.RecordStartedUnit(publish.Kind, startsCapture: false);

        Assert.Multiple(() =>
        {
            Assert.That(counters.CapturesStartedThisFrame, Is.EqualTo(1));
            Assert.That(counters.CaptureFaceUnitsThisFrame, Is.EqualTo(6));
            Assert.That(counters.PrefilterMipUnitsThisFrame, Is.EqualTo(3));
            Assert.That(counters.PublishCopyUnitsThisFrame, Is.EqualTo(1));
            Assert.That(counters.CaptureFaceUnitsTotal, Is.EqualTo(6UL));
            Assert.That(counters.PrefilterMipUnitsTotal, Is.EqualTo(3UL));
            Assert.That(counters.PublishCopyUnitsTotal, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void LifecycleSnapshotAndSubmittedRingAreAllocationFreeAfterConstruction()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1);
        ReflectionProbeCaptureFrameCounters counters = default;
        counters.BeginCaptureFrame();
        counters.RecordStartedUnit(
            ReflectionProbeWorkKind.CaptureFace,
            startsCapture: true);
        var ring = new ReflectionProbeSubmittedFrameRing();

        Assert.That(
            ring.FrameSlotCount,
            Is.EqualTo(RenderingConstants.FramesInFlight));

        ReflectionProbeLifecycleSnapshot warmup =
            ReflectionProbeLifecycleSnapshotFactory.Create(
                scheduler,
                publishedCount: 0,
                capturesCompletedTotal: 0UL,
                counters);
        var warmupFrame = new ReflectionProbeSubmittedFrameTelemetry(
            1UL,
            1,
            0,
            0,
            true,
            warmup);
        ring.MarkSubmitted(0, warmupFrame);
        Assert.That(ring.TryConsume(0, out _), Is.True);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong checksum = 0UL;
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            ReflectionProbeLifecycleSnapshot lifecycle =
                ReflectionProbeLifecycleSnapshotFactory.Create(
                    scheduler,
                    publishedCount: 0,
                    capturesCompletedTotal: 0UL,
                    counters);
            int slot = iteration & 1;
            var submitted = new ReflectionProbeSubmittedFrameTelemetry(
                (ulong)iteration,
                1,
                0,
                0,
                true,
                lifecycle);
            ring.MarkSubmitted(slot, submitted);
            if (!ring.TryConsume(
                    slot,
                    out ReflectionProbeSubmittedFrameTelemetry completed))
            {
                Assert.Fail("A just-submitted frame slot must be consumable.");
            }
            checksum += completed.FrameSerial;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);

        Assert.That(allocated, Is.Zero);
    }

    [Test]
    public void SubmittedRingRejectsReusingPendingFrameSlot()
    {
        var ring = new ReflectionProbeSubmittedFrameRing();
        ReflectionProbeSubmittedFrameTelemetry frame = default;
        ring.MarkSubmitted(0, frame);

        Assert.That(
            () => ring.MarkSubmitted(0, frame),
            Throws.TypeOf<InvalidOperationException>());
    }
}
