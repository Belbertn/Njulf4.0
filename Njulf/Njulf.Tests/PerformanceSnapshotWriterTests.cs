using Njulf.Rendering.Data;
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

    [Test]
    public void GlobalIlluminationSnapshot_PreservesCompactReceiverPublicationEvidence()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiReceiverProbeBytes = 16UL * 123UL,
            SimpleDdgiReceiverProbeCapacity = 123,
            SimpleDdgiReceiverInvalidationBytes = 48UL,
            SimpleDdgiReceiverInvalidationRangeCount = 2,
            SimpleDdgiReceiverFullClear = 1,
            SimpleDdgiReceiverResourceGeneration = 17u,
            SimpleDdgiReceiverRecordsPublished = 29
        };

        PerformanceGlobalIlluminationSnapshot snapshot =
            PerformanceSnapshotWriter.CreateGlobalIlluminationSnapshot(diagnostics);

        Assert.That(snapshot.SimpleDdgiReceiverProbeBytes, Is.EqualTo(16UL * 123UL));
        Assert.That(snapshot.SimpleDdgiReceiverProbeCapacity, Is.EqualTo(123));
        Assert.That(snapshot.SimpleDdgiReceiverInvalidationBytes, Is.EqualTo(48UL));
        Assert.That(snapshot.SimpleDdgiReceiverInvalidationRangeCount, Is.EqualTo(2));
        Assert.That(snapshot.SimpleDdgiReceiverFullClear, Is.True);
        Assert.That(snapshot.SimpleDdgiReceiverResourceGeneration, Is.EqualTo(17u));
        Assert.That(snapshot.SimpleDdgiReceiverRecordsPublished, Is.EqualTo(29));
    }
}
