using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DescriptorPublicationSlotTests
{
    [Test]
    public void StableIdentityProducesNoWritesAfterWarmup()
    {
        DescriptorPublicationSlot<ulong> slot = default;
        const ulong identity = 0x1234UL;

        Assert.That(slot.RequiresPublication(identity), Is.True);
        slot.Commit(identity);

        for (int frame = 0; frame < 10_000; frame++)
            Assert.That(slot.RequiresPublication(identity), Is.False, $"frame {frame}");
    }

    [Test]
    public void ReplacementAndRetirementRequireFreshPublication()
    {
        DescriptorPublicationSlot<ulong> slot = default;
        slot.Commit(11UL);

        Assert.That(slot.RequiresPublication(12UL), Is.True);
        slot.Commit(12UL);
        Assert.That(slot.RequiresPublication(12UL), Is.False);

        slot.Invalidate();
        Assert.That(slot.RequiresPublication(12UL), Is.True);
    }

    [Test]
    public void FailedWriteCanLeavePriorPublishedIdentityUntouched()
    {
        DescriptorPublicationSlot<ulong> slot = default;
        slot.Commit(21UL);

        // A caller deliberately does not Commit when the native publication fails.
        Assert.That(slot.RequiresPublication(22UL), Is.True);
        Assert.That(slot.RequiresPublication(21UL), Is.False);
    }
}
