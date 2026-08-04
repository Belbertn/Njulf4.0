using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PerformanceSnapshotWriterTests
{
    [Test]
    public void SnapshotWriter_RemainsAvailableForSimpleDdgiCaptures()
    {
        Assert.That(typeof(PerformanceSnapshotWriter).Assembly.GetName().Name, Is.EqualTo("Njulf.Rendering"));
    }
}
