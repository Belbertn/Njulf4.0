using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSampledAtlasBorderTests
{
    [TestCase(8, 8)]
    [TestCase(16, 4)]
    public void FullSyncCopy_PreservesWrappedFilteringAndCanonicalLayerStride(int n, int texelBytes)
    {
        const int firstProbe = 2;
        const int firstLayer = 7;
        const int layerCount = 2;
        int extent = n + 2;
        var source = new float[(firstProbe + layerCount) * n * n];
        for (int probe = 0; probe < firstProbe + layerCount; probe++)
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
            source[probe * n * n + y * n + x] =
                (float)(Half)((probe * 317 + x * 11 + y * 7 + x * y * 3) / 2048f);

        var regions = new BufferImageCopy[1 + 4 * n + 4];
        int count = SimpleDdgiSampledAtlas.BuildCopyRegions(
            regions, n, (ulong)texelBytes, firstProbe, firstLayer, layerCount);
        Assert.That(count, Is.EqualTo(regions.Length));
        var images = new float[layerCount * extent * extent];
        var writes = new int[images.Length];
        foreach (BufferImageCopy region in regions)
        {
            Assert.That(region.ImageSubresource.BaseArrayLayer, Is.EqualTo(firstLayer));
            Assert.That(region.ImageSubresource.LayerCount, Is.EqualTo(layerCount));
            int rowStride = (int)Math.Max(region.BufferRowLength, region.ImageExtent.Width);
            int layerStride = rowStride * (int)Math.Max(region.BufferImageHeight, region.ImageExtent.Height);
            for (int layer = 0; layer < layerCount; layer++)
            for (int y = 0; y < region.ImageExtent.Height; y++)
            for (int x = 0; x < region.ImageExtent.Width; x++)
            {
                int sourceIndex = checked((int)(region.BufferOffset / (ulong)texelBytes)) +
                    layer * layerStride + y * rowStride + x;
                Assert.That(sourceIndex, Is.InRange(firstProbe * n * n, source.Length - 1));
                int destinationIndex = layer * extent * extent +
                    (region.ImageOffset.Y + y) * extent + region.ImageOffset.X + x;
                images[destinationIndex] = source[sourceIndex];
                writes[destinationIndex]++;
            }
        }
        Assert.That(writes, Is.All.EqualTo(1), "Every interior and border texel must be written exactly once.");

        for (int layer = 0; layer < layerCount; layer++)
        {
            int sourceBase = (firstProbe + layer) * n * n;
            int imageBase = layer * extent * extent;
            for (int y = 0; y < extent; y++)
            for (int x = 0; x < extent; x++)
                Assert.That(images[imageBase + y * extent + x],
                    Is.EqualTo(source[sourceBase + ExpectedSource(x, y, n)]),
                    $"layer {layer}, padded texel ({x}, {y})");

            // Include exact octahedral boundaries, both sides of the boundary
            // footprint, texel centers, and asymmetric interior coordinates.
            double[] coordinates = [0, 0.000001, 0.5 / n - 0.000001,
                0.5 / n, 0.5 / n + 0.000001, 0.34, 0.5, 0.73,
                1 - 0.5 / n, 0.999999, 1];
            foreach (double u in coordinates)
            foreach (double v in coordinates)
            {
                double reference = Filter(u * n - 0.5, v * n - 0.5,
                    (x, y) => source[sourceBase + ExpectedSource(x + 1, y + 1, n)]);
                double imageU = (u * n + 1) / extent;
                double imageV = (v * n + 1) / extent;
                double actual = Filter(imageU * extent - 0.5, imageV * extent - 0.5,
                    (x, y) => images[imageBase + y * extent + x]);
                Assert.That(actual, Is.EqualTo(reference).Within(1e-6), $"UV ({u}, {v})");
            }
        }
    }

    // Explicit edge reversals and opposite corners are independent of the
    // production mirror helper's sequential X/Y folding algorithm.
    private static int ExpectedSource(int x, int y, int n)
    {
        if (x == 0 && y == 0) return n * n - 1;
        if (x == n + 1 && y == 0) return (n - 1) * n;
        if (x == 0 && y == n + 1) return n - 1;
        if (x == n + 1 && y == n + 1) return 0;
        if (x == 0) return (n - y) * n;
        if (x == n + 1) return (n - y) * n + n - 1;
        if (y == 0) return n - x;
        if (y == n + 1) return (n - 1) * n + n - x;
        return (y - 1) * n + x - 1;
    }

    private static double Filter(double x, double y, Func<int, int, float> read)
    {
        int bx = (int)Math.Floor(x), by = (int)Math.Floor(y);
        double fx = x - bx, fy = y - by;
        return (1 - fy) * ((1 - fx) * read(bx, by) + fx * read(bx + 1, by)) +
            fy * ((1 - fx) * read(bx, by + 1) + fx * read(bx + 1, by + 1));
    }
}
