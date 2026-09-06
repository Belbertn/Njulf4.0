using System;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class FrameSubmissionOwnershipTrackerTests
{
    [Test]
    public void AcquireFirstScheduling_HasOneSemaphoreBeyondInFlightContexts()
    {
        var tracker = new FrameSubmissionOwnershipTracker(
            frameContextCount: 2,
            acquireSemaphoreCount: 3,
            swapchainImageCount: 3);

        int acquire0 = tracker.SelectAcquireSemaphore();
        FrameResourceContextSelection frame0 =
            tracker.SelectFrameResourceContext(_ => false);
        tracker.MarkSubmitted(
            frame0.FrameContext,
            swapchainImageIndex: 0,
            acquire0,
            submissionSerial: 1);

        int acquire1 = tracker.SelectAcquireSemaphore();
        FrameResourceContextSelection frame1 =
            tracker.SelectFrameResourceContext(_ => false);
        tracker.MarkSubmitted(
            frame1.FrameContext,
            swapchainImageIndex: 1,
            acquire1,
            submissionSerial: 2);

        int acquire2 = tracker.SelectAcquireSemaphore();
        SwapchainImageSubmissionOwner image0 =
            tracker.GetSwapchainImageOwner(0);
        FrameResourceContextSelection recycle =
            tracker.SelectFrameResourceContext(_ => false);

        Assert.Multiple(() =>
        {
            Assert.That((acquire0, acquire1, acquire2),
                Is.EqualTo((0, 1, 2)));
            Assert.That((frame0.FrameContext, frame1.FrameContext),
                Is.EqualTo((0, 1)));
            Assert.That(image0.Completed, Is.False);
            Assert.That(image0.FrameContext, Is.EqualTo(0));
            Assert.That(recycle.FrameContext, Is.EqualTo(0));
            Assert.That(recycle.PreviousSubmissionSerial, Is.EqualTo(1UL));
            Assert.That(recycle.RequiresWait, Is.True);
        });

        tracker.ObserveContextCompleted(recycle.FrameContext);
        Assert.That(tracker.SelectAcquireSemaphore(), Is.EqualTo(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CyclicContextReuse_SelectsImmediatelyPrecedingHistory(
        bool preferredFenceSignaled)
    {
        var tracker = CreateTwoFrameTracker();
        var historySubmissions = new ulong[RenderingConstants.FramesInFlight];
        for (ulong serial = 1; serial <= 2; serial++)
        {
            int acquire = tracker.SelectAcquireSemaphore();
            FrameResourceContextSelection frame =
                tracker.SelectFrameResourceContext(_ => false);
            tracker.MarkSubmitted(frame.FrameContext, (uint)(serial - 1), acquire, serial);
            historySubmissions[frame.FrameContext] = serial;
        }

        int acquire2 = tracker.SelectAcquireSemaphore();
        FrameResourceContextSelection selected =
            tracker.SelectFrameResourceContext(
                context => context == 1 || preferredFenceSignaled);
        int previousHistoryFrame =
            DirectionalShadowTemporalPass.GetPreviousHistoryFrameIndex(
                selected.FrameContext);

        Assert.Multiple(() =>
        {
            Assert.That(selected.FrameContext, Is.EqualTo(0));
            Assert.That(selected.PreviousSubmissionSerial, Is.EqualTo(1UL));
            Assert.That(selected.RequiresWait, Is.EqualTo(!preferredFenceSignaled));
            Assert.That(previousHistoryFrame, Is.Not.EqualTo(selected.FrameContext));
            Assert.That(historySubmissions[previousHistoryFrame], Is.EqualTo(2UL),
                "Motion vectors for submission 3 refer to submission 2's history.");
        });

        if (selected.RequiresWait)
            tracker.ObserveContextCompleted(selected.FrameContext);
        tracker.MarkSubmitted(selected.FrameContext, 2, acquire2, 3);
        historySubmissions[selected.FrameContext] = 3;

        Assert.Multiple(() =>
        {
            Assert.That(tracker.GetSwapchainImageOwner(2).SubmissionSerial, Is.EqualTo(3UL));
            Assert.That(tracker.PreferredFrameContext, Is.EqualTo(1));
            Assert.That(historySubmissions[previousHistoryFrame], Is.EqualTo(2UL),
                "Writing the current history must preserve the preceding submission's bank.");
        });
    }

    [Test]
    public void IncompleteFrameOrAcquireOwner_CannotBeReassigned()
    {
        var tracker = CreateTwoFrameTracker();
        tracker.MarkSubmitted(0, 0, 0, 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => tracker.MarkSubmitted(0, 1, 1, 2),
                Throws.InvalidOperationException);
            Assert.That(
                () => tracker.MarkSubmitted(1, 1, 0, 2),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void DeviceIdleReset_ReleasesAcquireOwnersAndRebuildsImageLedger()
    {
        var tracker = CreateTwoFrameTracker();
        tracker.MarkSubmitted(0, 0, 0, 1);
        tracker.MarkSubmitted(1, 1, 1, 2);

        tracker.ResetAfterDeviceIdle(swapchainImageCount: 4);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.CompletedSubmissionSerial, Is.EqualTo(2UL));
            Assert.That(tracker.SelectAcquireSemaphore(), Is.EqualTo(0));
            Assert.That(tracker.GetSwapchainImageOwner(3),
                Is.EqualTo(new SwapchainImageSubmissionOwner(0, -1, true)));
        });
    }

    private static FrameSubmissionOwnershipTracker CreateTwoFrameTracker() =>
        new(
            frameContextCount: 2,
            acquireSemaphoreCount: 3,
            swapchainImageCount: 3);
}
