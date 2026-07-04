using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class VolumeTextureTests
{
    [Test]
    public void CalculateFullMipCount_UsesLargestVolumeAxis()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VolumeTexture.CalculateFullMipCount(new Extent3D { Width = 1, Height = 1, Depth = 1 }), Is.EqualTo(1));
            Assert.That(VolumeTexture.CalculateFullMipCount(new Extent3D { Width = 8, Height = 4, Depth = 2 }), Is.EqualTo(4));
            Assert.That(VolumeTexture.CalculateFullMipCount(new Extent3D { Width = 7, Height = 9, Depth = 3 }), Is.EqualTo(4));
        });
    }

    [Test]
    public void CalculateByteSize_IncludesDepthAndMipChain()
    {
        var extent = new Extent3D { Width = 4, Height = 4, Depth = 4 };

        Assert.Multiple(() =>
        {
            Assert.That(VolumeTexture.CalculateByteSize(extent, Format.R16Sfloat), Is.EqualTo(128));
            Assert.That(VolumeTexture.CalculateByteSize(extent, Format.R16Sfloat, 3), Is.EqualTo(146));
        });
    }

    [Test]
    public void Descriptor_RequiresAtLeastOneUsage()
    {
        Assert.Throws<ArgumentException>(() => _ = new VolumeTextureDescriptor(sampled: false));
        Assert.DoesNotThrow(() => _ = new VolumeTextureDescriptor(sampled: true, storage: true));
    }
}
