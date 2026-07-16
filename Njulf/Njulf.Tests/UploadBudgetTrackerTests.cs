using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class UploadBudgetTrackerTests
    {
        [Test]
        public void UploadBudgetTracker_AggregatesByCategory()
        {
            var tracker = new UploadBudgetTracker();
            tracker.BeginFrame();
            tracker.AddBytes(UploadBudgetCategory.Scene, 10);
            tracker.AddBytes(UploadBudgetCategory.Scene, 5);
            tracker.AddBytes(UploadBudgetCategory.Materials, 3);

            UploadBudgetSnapshot snapshot = tracker.EndFrame(RenderBudgetProfile.Development);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.TotalBytes, Is.EqualTo(18));
                Assert.That(snapshot.Entries, Has.One.Matches<UploadBudgetEntry>(entry => entry.Category == UploadBudgetCategory.Scene && entry.Bytes == 15));
            });
        }

        [Test]
        public void UploadBudgetTracker_OverBudgetSetsStatus()
        {
            var tracker = new UploadBudgetTracker();
            tracker.BeginFrame();
            tracker.AddBytes(UploadBudgetCategory.Textures, RenderBudgetProfile.LowSpec1080p30.UploadBudgetBytesPerFrame + 1);

            UploadBudgetSnapshot snapshot = tracker.EndFrame(RenderBudgetProfile.LowSpec1080p30);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(snapshot.BudgetExceededFrameCount, Is.EqualTo(1));
            });
        }
    }
}
