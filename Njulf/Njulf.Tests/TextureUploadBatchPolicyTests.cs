using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class TextureUploadBatchPolicyTests
{
    [TestCase(0UL, 32UL, 64UL, false)]
    [TestCase(32UL, 32UL, 64UL, false)]
    [TestCase(33UL, 32UL, 64UL, true)]
    [TestCase(64UL, 1UL, 64UL, true)]
    public void FlushPolicy_BoundsRetainedStaging(
        ulong staged,
        ulong next,
        ulong maximum,
        bool expected)
    {
        Assert.That(
            TextureManager.ShouldFlushUploadBatch(
                staged,
                next,
                maximum),
            Is.EqualTo(expected));
    }
}
