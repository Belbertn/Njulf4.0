using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmdReflectionAllocationTests
{
    [TestCase(1u, 1u)]
    [TestCase(1919u, 1079u)]
    [TestCase(1920u, 1080u)]
    public void ReducedLayoutKeepsNativeHitWritesAndTileListInsideEachBank(uint width,uint height)
    {
        foreach(int bank in new[]{0,1})
        {
            var allocation=AmdReflectionGpuContract.Layout(width,height,true,bank);
            Assert.That(allocation.Width,Is.EqualTo((width+1)/2));
            Assert.That(allocation.Height,Is.EqualTo((height+1)/2));
            ulong nativeEnd=(ulong)allocation.HitOffset+(ulong)width*height;
            Assert.That(nativeEnd,Is.EqualTo(allocation.TileOffset));
            Assert.That(allocation.HitOffset,Is.GreaterThanOrEqualTo(128UL+(ulong)allocation.Width*allocation.Height*allocation.PlaneCount));
            ulong tileCount=(ulong)((allocation.Width+7)/8)*((allocation.Height+7)/8);
            Assert.That(allocation.Bytes,Is.EqualTo((nativeEnd+tileCount)*4));
        }
    }

    [Test]
    public void FullResolutionLayoutRetainsExistingProducerAddressAndReducedLayoutSavesMemory()
    {
        ulong full=0,reduced=0;
        foreach(int bank in new[]{0,1})
        {
            var native=AmdReflectionGpuContract.Layout(1920,1080,false,bank);
            Assert.That(native.HitOffset,Is.EqualTo(128));
            full+=native.Bytes;
            reduced+=AmdReflectionGpuContract.Layout(1920,1080,true,bank).Bytes;
        }
        Assert.That(full,Is.EqualTo(191031424UL));
        Assert.That(reduced,Is.LessThan(full/2));
    }
}
