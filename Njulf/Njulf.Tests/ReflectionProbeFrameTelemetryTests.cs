using System;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ReflectionProbeFrameTelemetryTests
{
    [Test]
    public void CompatibilityBudgetMappingUsesReservedMicrosecondsAndPreservesExceededState()
    {
        var planner = new ReflectionProbeGpuBudgetPlanner();
        planner.RecordTiming(
            ReflectionProbeWorkKind.CaptureFace,
            unitCount: 1,
            measuredMicroseconds: 800);
        planner.BeginFrame(budgetMicroseconds: 200);
        Assert.That(
            planner.TryReserve(ReflectionProbeWorkKind.CaptureFace),
            Is.True);

        ReflectionProbeGpuBudgetSnapshot budget = planner.GetSnapshot();
        planner.BeginFrame(budgetMicroseconds: 400);
        Assert.That(
            planner.TryReserve(ReflectionProbeWorkKind.CaptureFace),
            Is.True);
        ReflectionProbeGpuBudgetSnapshot withinBudget = planner.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(budget.ReservedMicroseconds, Is.EqualTo(275));
            Assert.That(
                ReflectionProbeTelemetryValueMapper
                    .CaptureBudgetUsedMicroseconds(budget),
                Is.EqualTo(275));
            Assert.That(
                ReflectionProbeTelemetryValueMapper
                    .CaptureBudgetExceeded(budget),
                Is.EqualTo(1));
            Assert.That(
                ReflectionProbeTelemetryValueMapper
                    .CaptureBudgetUsedMicroseconds(withinBudget),
                Is.EqualTo(275));
            Assert.That(
                ReflectionProbeTelemetryValueMapper
                    .CaptureBudgetExceeded(withinBudget),
                Is.Zero);
        });
    }

    [Test]
    public void RendererMappingKeepsCurrentAndCompletedLifecycleFrameIdentityExplicit()
    {
        var ring = new ReflectionProbeSubmittedFrameRing();
        ReflectionProbeLifecycleSnapshot completedLifecycle = CreateLifecycle(
            queued: 0,
            active: 0,
            awaiting: 1,
            published: 2,
            completedThisFrame: 0,
            completedTotal: 2UL);
        var submitted = new ReflectionProbeSubmittedFrameTelemetry(
            FrameSlot: 1,
            FrameSerial: 40UL,
            CaptureFaceUnitCount: 0,
            PrefilterMipUnitCount: 0,
            PublishCopyUnitCount: 1,
            GpuTimingRecorded: true,
            Lifecycle: completedLifecycle);
        ring.MarkSubmitted(1, submitted);
        Assert.That(
            ring.TryConsume(
                1,
                out ReflectionProbeSubmittedFrameTelemetry completedFrame),
            Is.True);

        ReflectionProbeLifecycleSnapshot currentLifecycle = CreateLifecycle(
            queued: 2,
            active: 1,
            awaiting: 0,
            published: 3,
            completedThisFrame: 1,
            completedTotal: 3UL);
        var current = new ReflectionProbeLifecycleFrameSnapshot(
            Valid: true,
            FrameSlot: 1,
            FrameSerial: 42UL,
            GpuTimingRecorded: true,
            Lifecycle: currentLifecycle);
        ReflectionProbeLifecycleFrameSnapshot completed =
            completedFrame.ToLifecycleFrameSnapshot();
        ReflectionProbeGpuBudgetSnapshot budget = new(
            BudgetMicroseconds: 400,
            ReservedMicroseconds: 125,
            FaceEstimateMicroseconds: 100,
            PrefilterEstimateMicroseconds: 125,
            CopyEstimateMicroseconds: 25,
            HasTimingHistory: true,
            BudgetExhausted: false);
        using var sceneData = new SceneRenderingData();

        VulkanRenderer.ApplyReflectionProbeTelemetry(
            sceneData,
            current,
            completed,
            budget);

        Assert.Multiple(() =>
        {
            Assert.That(sceneData.ReflectionProbeCurrentLifecycle.Valid, Is.True);
            Assert.That(sceneData.ReflectionProbeCurrentLifecycle.FrameSlot, Is.EqualTo(1));
            Assert.That(sceneData.ReflectionProbeCurrentLifecycle.FrameSerial, Is.EqualTo(42UL));
            Assert.That(sceneData.ReflectionProbeCurrentLifecycle.GpuTimingRecorded, Is.True);
            Assert.That(sceneData.ReflectionProbeCurrentLifecycle.Lifecycle, Is.EqualTo(currentLifecycle));
            Assert.That(sceneData.ReflectionProbeCompletedLifecycle.Valid, Is.True);
            Assert.That(sceneData.ReflectionProbeCompletedLifecycle.FrameSlot, Is.EqualTo(1));
            Assert.That(sceneData.ReflectionProbeCompletedLifecycle.FrameSerial, Is.EqualTo(40UL));
            Assert.That(sceneData.ReflectionProbeCompletedLifecycle.GpuTimingRecorded, Is.True);
            Assert.That(sceneData.ReflectionProbeCompletedLifecycle.Lifecycle, Is.EqualTo(completedLifecycle));
            Assert.That(sceneData.ReflectionProbeCapturesQueued, Is.EqualTo(3));
            Assert.That(sceneData.ReflectionProbeCapturesCompleted, Is.EqualTo(1));
            Assert.That(sceneData.ReflectionProbeCapturesCompletedTotal, Is.EqualTo(3UL));
            Assert.That(sceneData.ReflectionProbePublishedCount, Is.EqualTo(3));
            Assert.That(sceneData.ReflectionProbeCaptureBudget, Is.EqualTo(budget));
        });

        VulkanRenderer.ApplyReflectionProbeTelemetry(
            sceneData,
            current: default,
            completed: default,
            budget: default);
        Assert.Multiple(() =>
        {
            Assert.That(sceneData.ReflectionProbeCurrentLifecycle.Valid, Is.False);
            Assert.That(sceneData.ReflectionProbeCompletedLifecycle.Valid, Is.False);
            Assert.That(sceneData.ReflectionProbeCapturesQueued, Is.Zero);
            Assert.That(sceneData.ReflectionProbeCapturesCompleted, Is.Zero);
            Assert.That(sceneData.ReflectionProbeCapturesCompletedTotal, Is.Zero);
            Assert.That(sceneData.ReflectionProbePublishedCount, Is.Zero);
            Assert.That(sceneData.ReflectionProbeCaptureBudget, Is.EqualTo(default(ReflectionProbeGpuBudgetSnapshot)));
        });
    }

    [Test]
    public void ReflectionPublishTimingContributesMicrosecondsToGpuFrameTotal()
    {
        using var sceneData = new SceneRenderingData
        {
            ReflectionProbeCompletedLifecycle =
                new ReflectionProbeLifecycleFrameSnapshot(
                    Valid: true,
                    FrameSlot: 0,
                    FrameSerial: 8UL,
                    GpuTimingRecorded: true,
                    Lifecycle: default)
        };
        var timings = new FrameTimingSnapshot(
        [
            new PassTiming("ReflectionProbeCapturePass", 0, 11, true),
            new PassTiming("ReflectionProbePrefilterPass", 0, 13, true),
            new PassTiming("ReflectionProbePublishPass", 0, 17, true)
        ]);

        VulkanRenderer.ApplyCompletedGpuTimings(sceneData, timings);

        Assert.Multiple(() =>
        {
            Assert.That(sceneData.GpuReflectionProbePublishMicroseconds, Is.EqualTo(17));
            Assert.That(
                VulkanRenderer.CalculateGpuFrameMicroseconds(sceneData),
                Is.EqualTo(41));
        });

        sceneData.ReflectionProbeCompletedLifecycle =
            sceneData.ReflectionProbeCompletedLifecycle with
            {
                GpuTimingRecorded = false
            };
        VulkanRenderer.ApplyCompletedGpuTimings(sceneData, timings);
        Assert.That(sceneData.GpuReflectionProbePublishMicroseconds, Is.Zero);
    }

    [Test]
    public void AreaRayShadowTimingContributesMicrosecondsToGpuFrameTotal()
    {
        using var sceneData = new SceneRenderingData();
        var timings = new FrameTimingSnapshot(
        [
            new PassTiming("AreaRayShadowPass", 0, 19, true)
        ]);

        VulkanRenderer.ApplyCompletedGpuTimings(sceneData, timings);

        Assert.Multiple(() =>
        {
            Assert.That(sceneData.GpuAreaRayShadowMicroseconds, Is.EqualTo(19));
            Assert.That(
                VulkanRenderer.CalculateGpuFrameMicroseconds(sceneData),
                Is.EqualTo(19));
        });
    }

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
            0,
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
                slot,
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

    [Test]
    public void SubmittedRingRejectsMismatchedFrameSlotIdentity()
    {
        var ring = new ReflectionProbeSubmittedFrameRing();
        var frame = new ReflectionProbeSubmittedFrameTelemetry(
            FrameSlot: 1,
            FrameSerial: 10UL,
            CaptureFaceUnitCount: 0,
            PrefilterMipUnitCount: 0,
            PublishCopyUnitCount: 0,
            GpuTimingRecorded: true,
            Lifecycle: default);

        Assert.That(
            () => ring.MarkSubmitted(0, frame),
            Throws.TypeOf<ArgumentException>());
    }

    private static ReflectionProbeLifecycleSnapshot CreateLifecycle(
        int queued,
        int active,
        int awaiting,
        int published,
        int completedThisFrame,
        ulong completedTotal) =>
        new(
            QueuedCount: queued,
            ActiveCount: active,
            State: awaiting > 0
                ? ReflectionProbeCaptureState.AwaitingGpuCompletion
                : active > 0
                    ? ReflectionProbeCaptureState.CapturingFaces
                    : queued > 0
                        ? ReflectionProbeCaptureState.Queued
                        : published > 0
                            ? ReflectionProbeCaptureState.Published
                            : ReflectionProbeCaptureState.Unregistered,
            AwaitingGpuCompletionCount: awaiting,
            PublishedCount: published,
            CapturesStartedThisFrame: active > 0 ? 1 : 0,
            CapturesCompletedThisFrame: completedThisFrame,
            CaptureFaceUnitsThisFrame: active > 0 ? 1 : 0,
            PrefilterMipUnitsThisFrame: 0,
            PublishCopyUnitsThisFrame: awaiting > 0 ? 1 : 0,
            CapturesStartedTotal: (ulong)published + (active > 0 ? 1UL : 0UL),
            CapturesCompletedTotal: completedTotal,
            CapturesPublishedTotal: (ulong)published,
            CaptureFaceUnitsTotal: (ulong)published * 6UL + (active > 0 ? 1UL : 0UL),
            PrefilterMipUnitsTotal: (ulong)published,
            PublishCopyUnitsTotal: (ulong)published + (awaiting > 0 ? 1UL : 0UL));
}
