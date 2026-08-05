using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class RuntimeStallTrackerTests
    {
        [Test]
        public void RuntimeStallTracker_RecordsWorstStall()
        {
            var tracker = new RuntimeStallTracker();
            tracker.BeginFrame();
            tracker.Record(RuntimeStallReason.SwapchainAcquire, 10);
            tracker.Record(RuntimeStallReason.Present, 50);

            RuntimeStallSnapshot snapshot = tracker.CreateSnapshot();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.WorstMicrosecondsThisFrame, Is.EqualTo(50));
                Assert.That(snapshot.WorstReasonThisFrame, Is.EqualTo(RuntimeStallReason.Present));
                Assert.That(snapshot.TotalMicrosecondsThisFrame, Is.EqualTo(60));
            });
        }

        [Test]
        public void RuntimeStallTracker_RingBufferKeepsRecentEvents()
        {
            var tracker = new RuntimeStallTracker(capacity: 2);
            tracker.Record(RuntimeStallReason.FrameFenceWait, 1, "old");
            tracker.Record(RuntimeStallReason.QueueSubmit, 2, "middle");
            tracker.Record(RuntimeStallReason.Present, 3, "new");

            RuntimeStallSnapshot snapshot = tracker.CreateSnapshot();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.RecentEvents, Has.Count.EqualTo(2));
                Assert.That(snapshot.RecentEvents[0].Description, Is.EqualTo("middle"));
                Assert.That(snapshot.RecentEvents[1].Description, Is.EqualTo("new"));
            });
        }

        [Test]
        public void RuntimeStallTracker_CountsMeasuredDeviceIdleReasons()
        {
            var tracker = new RuntimeStallTracker();
            tracker.BeginFrame();

            tracker.Record(RuntimeStallReason.DeviceWaitIdle, 0, "Environment resource recreate");
            tracker.Record(RuntimeStallReason.ResourceResize, 25, "Render target profile rebuild");

            RuntimeStallSnapshot snapshot = tracker.CreateSnapshot();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.DeviceWaitIdleCount, Is.EqualTo(2));
                Assert.That(snapshot.WorstMicrosecondsThisFrame, Is.EqualTo(25));
                Assert.That(snapshot.WorstReasonThisFrame, Is.EqualTo(RuntimeStallReason.ResourceResize));
                Assert.That(snapshot.RecentEvents, Has.Count.EqualTo(2));
                Assert.That(snapshot.RecentEvents[0].Description, Is.EqualTo("Environment resource recreate"));
                Assert.That(snapshot.RecentEvents[1].Description, Is.EqualTo("Render target profile rebuild"));
            });
        }

        [Test]
        public void ResourceGenerationFenceWait_IsAppendOnlyAndNotDeviceIdle()
        {
            var tracker = new RuntimeStallTracker();
            tracker.BeginFrame();
            tracker.Record(
                RuntimeStallReason.ResourceGenerationFenceWait,
                25,
                "bindless generation transition");

            RuntimeStallSnapshot snapshot = tracker.CreateSnapshot();

            Assert.Multiple(() =>
            {
                Assert.That((int)RuntimeStallReason.DeviceWaitIdle, Is.EqualTo(5));
                Assert.That((int)RuntimeStallReason.ResourceResize, Is.EqualTo(6));
                Assert.That((int)RuntimeStallReason.SynchronousUpload, Is.EqualTo(7));
                Assert.That(
                    (int)RuntimeStallReason.ResourceGenerationFenceWait,
                    Is.EqualTo(8));
                Assert.That(snapshot.DeviceWaitIdleCount, Is.Zero);
                Assert.That(
                    snapshot.WorstReasonThisFrame,
                    Is.EqualTo(
                        RuntimeStallReason.ResourceGenerationFenceWait));
            });
        }
    }
}
