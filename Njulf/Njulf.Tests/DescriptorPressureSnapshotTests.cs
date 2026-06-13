using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DescriptorPressureSnapshotTests
{
    [Test]
    public void ComputesUsageRatiosAndExhaustionMessage()
    {
        var snapshot = new DescriptorPressureSnapshot(
            TextureCapacity: 100,
            TextureUsed: 75,
            TextureHighWater: 90,
            SamplerCapacity: 10,
            SamplerUsed: 10,
            SamplerHighWater: 10,
            DescriptorWrites: 42);

        Assert.That(snapshot.TextureUsageRatio, Is.EqualTo(0.75f));
        Assert.That(snapshot.IsSamplerExhausted, Is.True);
        Assert.That(snapshot.FormatExhaustionFailure("bindless", 1), Does.Contain("bindless descriptor capacity exhausted"));
    }
}
