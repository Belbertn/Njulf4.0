using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class PerformanceSampleWindowTests
    {
        [Test]
        public void PerformanceSampleWindow_ComputesAverageMinMaxP95()
        {
            var window = new PerformanceSampleWindow(10);
            for (int i = 1; i <= 10; i++)
                window.Add(i);

            PerformanceSampleStats stats = window.GetStats();

            Assert.Multiple(() =>
            {
                Assert.That(stats.Count, Is.EqualTo(10));
                Assert.That(stats.Average, Is.EqualTo(5.5));
                Assert.That(stats.Min, Is.EqualTo(1));
                Assert.That(stats.Max, Is.EqualTo(10));
                Assert.That(stats.P95, Is.EqualTo(10));
            });
        }
    }
}
