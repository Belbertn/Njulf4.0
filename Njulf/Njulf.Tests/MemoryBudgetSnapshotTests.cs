using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class MemoryBudgetSnapshotTests
    {
        [Test]
        public void MemoryBudgetSnapshot_TotalEqualsEntrySum()
        {
            var snapshot = new MemoryBudgetSnapshot(
                12,
                100,
                [
                    new MemoryBudgetEntry(MemoryBudgetCategory.MeshBuffers, 5, 1, "mesh"),
                    new MemoryBudgetEntry(MemoryBudgetCategory.RenderTargets, 7, 1, "rt")
                ],
                MemoryHeapBudgetSnapshot.Unavailable);

            ulong sum = 0;
            foreach (MemoryBudgetEntry entry in snapshot.Entries)
                sum += entry.Bytes;

            Assert.That(snapshot.TotalTrackedBytes, Is.EqualTo(sum));
        }

        [Test]
        public void MemoryBudgetSnapshot_EffectiveMemoryUsesHeapBudgetWhenAvailable()
        {
            var snapshot = new MemoryBudgetSnapshot(
                12,
                100,
                Array.Empty<MemoryBudgetEntry>(),
                new MemoryHeapBudgetSnapshot(
                    true,
                    [
                        new MemoryHeapBudgetEntry(0, true, 70, 90, 60, 72, 3, 1)
                    ]));

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.EffectiveMemoryBytes, Is.EqualTo(70));
                Assert.That(snapshot.EffectiveBudgetBytes, Is.EqualTo(90));
            });
        }

        [Test]
        public void MemoryBudgetSnapshot_EffectiveMemoryPrefersDeviceLocalHeapBudget()
        {
            var snapshot = new MemoryBudgetSnapshot(
                12,
                100,
                Array.Empty<MemoryBudgetEntry>(),
                new MemoryHeapBudgetSnapshot(
                    true,
                    [
                        new MemoryHeapBudgetEntry(0, false, 200, 300, 180, 220, 4, 2),
                        new MemoryHeapBudgetEntry(1, true, 70, 90, 60, 72, 3, 1)
                    ]));

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.EffectiveMemoryBytes, Is.EqualTo(70));
                Assert.That(snapshot.EffectiveBudgetBytes, Is.EqualTo(90));
            });
        }

        [Test]
        public void MemoryBudgetSnapshot_UnknownCategoryIsReported()
        {
            var tracker = new GpuAllocationTracker();
            tracker.RegisterBuffer(new BufferHandle(1, 1), 64, BufferUsageFlags.StorageBufferBit, MemoryBudgetCategory.Unknown, "unknown");

            MemoryBudgetSnapshot snapshot = tracker.CreateSnapshot(RenderBudgetProfile.Development);

            Assert.That(snapshot.Entries, Has.One.Matches<MemoryBudgetEntry>(entry => entry.Category == MemoryBudgetCategory.Unknown && entry.Bytes == 64));
        }

        [Test]
        public void GpuAllocationTracker_RegisterAndRetireBufferUpdatesTotals()
        {
            var tracker = new GpuAllocationTracker();
            var handle = new BufferHandle(2, 1);
            tracker.RegisterBuffer(handle, 128, BufferUsageFlags.VertexBufferBit, MemoryBudgetCategory.MeshBuffers, "mesh");

            Assert.That(tracker.CreateSnapshot(RenderBudgetProfile.Development).TotalTrackedBytes, Is.EqualTo(128));

            tracker.RetireBuffer(handle);

            Assert.That(tracker.CreateSnapshot(RenderBudgetProfile.Development).TotalTrackedBytes, Is.EqualTo(0));
        }

        [Test]
        public void GpuAllocationTracker_RegisterAndRetireImageUpdatesTotals()
        {
            var tracker = new GpuAllocationTracker();
            tracker.RegisterImage(
                99,
                256,
                Format.R8G8B8A8Unorm,
                new Extent3D { Width = 8, Height = 8, Depth = 1 },
                1,
                1,
                MemoryBudgetCategory.TextureAssets,
                "texture");

            Assert.That(tracker.CreateSnapshot(RenderBudgetProfile.Development).TotalTrackedBytes, Is.EqualTo(256));

            tracker.RetireImage(99);

            Assert.That(tracker.CreateSnapshot(RenderBudgetProfile.Development).TotalTrackedBytes, Is.EqualTo(0));
        }

        [Test]
        public void GpuAllocationTracker_DoubleRetireIsIgnored()
        {
            var tracker = new GpuAllocationTracker();
            var handle = new BufferHandle(3, 1);
            tracker.RegisterBuffer(handle, 32, BufferUsageFlags.StorageBufferBit, MemoryBudgetCategory.LightBuffers, "lights");
            tracker.RetireBuffer(handle);
            tracker.RetireBuffer(handle);

            Assert.That(tracker.CreateSnapshot(RenderBudgetProfile.Development).TotalTrackedBytes, Is.EqualTo(0));
        }

        [Test]
        public void ImageByteEstimator_HandlesRgba8Rgba16DepthAndMips()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    ImageByteEstimator.EstimateBytes(Format.R8G8B8A8Unorm, new Extent3D { Width = 4, Height = 4, Depth = 1 }),
                    Is.EqualTo(64));
                Assert.That(
                    ImageByteEstimator.EstimateBytes(Format.R16G16B16A16Sfloat, new Extent3D { Width = 4, Height = 4, Depth = 1 }),
                    Is.EqualTo(128));
                Assert.That(
                    ImageByteEstimator.EstimateBytes(Format.D32Sfloat, new Extent3D { Width = 4, Height = 4, Depth = 1 }, mipLevels: 2),
                    Is.EqualTo((16 + 4) * 4));
            });
        }

        [Test]
        public void ImageByteEstimator_HandlesBlockCompressedFormatsAndMips()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    ImageByteEstimator.EstimateBytes(Format.BC7UnormBlock, new Extent3D { Width = 4, Height = 4, Depth = 1 }),
                    Is.EqualTo(16));
                Assert.That(
                    ImageByteEstimator.EstimateBytes(Format.BC7UnormBlock, new Extent3D { Width = 5, Height = 5, Depth = 1 }),
                    Is.EqualTo(64));
                Assert.That(
                    ImageByteEstimator.EstimateBytes(Format.BC1RgbaUnormBlock, new Extent3D { Width = 4, Height = 4, Depth = 1 }, mipLevels: 2),
                    Is.EqualTo(16));
            });
        }
    }
}
