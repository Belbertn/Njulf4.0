using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GpuParticleDispatchContractTests
{
    [Test]
    public void IndirectLayout_PreservesDrawPrefixAndRoundsLiveWork()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                GpuParticleDispatchLayout.ActiveListSelectorWord,
                Is.EqualTo(20u));
            Assert.That(
                GpuParticleDispatchLayout.ByteOffset(
                    GpuParticleDispatchLayout.UpdateDispatchWord),
                Is.EqualTo(96UL));
            Assert.That(
                GpuParticleDispatchLayout.ByteOffset(
                    GpuParticleDispatchLayout.EmitDispatchWord),
                Is.EqualTo(112UL));
            Assert.That(GpuParticleDispatchLayout.TotalWordCount,
                Is.EqualTo(56u));
            Assert.That(
                GpuParticleDispatchLayout.SortWorkDispatchWord(2u),
                Is.EqualTo(40u));
            Assert.That(
                GpuParticleDispatchLayout.SortPrefixDispatchWord(2u),
                Is.EqualTo(52u));
            Assert.That(
                GpuParticleDispatchLayout.AliveIndexElementCount(65_536u),
                Is.EqualTo(131_072u));
            Assert.That(
                GpuParticleDispatchLayout.SortScratchRequiredWordCount(
                    65_536u),
                Is.LessThanOrEqualTo(
                    GpuParticleDispatchLayout.SortKeyElementCount(65_536u) *
                    2u));
            Assert.That(GpuParticleDispatchLayout.GroupCount(0u),
                Is.EqualTo(0u));
            Assert.That(GpuParticleDispatchLayout.GroupCount(1u),
                Is.EqualTo(1u));
            Assert.That(GpuParticleDispatchLayout.GroupCount(256u),
                Is.EqualTo(1u));
            Assert.That(GpuParticleDispatchLayout.GroupCount(257u),
                Is.EqualTo(2u));
            Assert.That(
                FroxelSourceDispatchLayout.CommandOffsetBytes(100u),
                Is.EqualTo(404UL));
            Assert.That(
                FroxelSourceDispatchLayout.BufferByteSize(100u),
                Is.EqualTo(416UL));
        });
    }
}
