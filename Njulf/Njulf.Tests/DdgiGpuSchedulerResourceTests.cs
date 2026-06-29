using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiGpuSchedulerResourceTests
    {
        [Test]
        public void CalculateGpuSchedulerResourceLayout_UsesMinimumDescriptorSafeCapacities()
        {
            DdgiGpuSchedulerResourceLayout layout = DdgiProbeVolumeManager.CalculateGpuSchedulerResourceLayout(
                activeProbeCount: 0,
                maxDirtyRegions: 0,
                priorityBucketCount: 0);

            Assert.Multiple(() =>
            {
                Assert.That(layout.DirtyRegionCapacity, Is.EqualTo(1));
                Assert.That(layout.CandidateCapacity, Is.EqualTo(1));
                Assert.That(layout.WorkgroupCount, Is.EqualTo(1));
                Assert.That(layout.PriorityBucketCount, Is.EqualTo(1));
                Assert.That(layout.GroupCountCapacity, Is.EqualTo(1));
                Assert.That(layout.PrefixCapacity, Is.EqualTo(2));
                Assert.That(layout.DirtyRegionBufferSize, Is.EqualTo((ulong)Marshal.SizeOf<GPUDdgiDirtyRegion>()));
                Assert.That(layout.CandidateBufferSize, Is.EqualTo((ulong)Marshal.SizeOf<GPUDdgiProbeCandidate>()));
                Assert.That(layout.GroupCountBufferSize, Is.EqualTo(sizeof(uint)));
                Assert.That(layout.PrefixBufferSize, Is.EqualTo(2UL * sizeof(uint)));
            });
        }

        [Test]
        public void CalculateGpuSchedulerResourceLayout_ScalesWorkgroupsAndBuckets()
        {
            DdgiGpuSchedulerResourceLayout layout = DdgiProbeVolumeManager.CalculateGpuSchedulerResourceLayout(
                activeProbeCount: 129,
                maxDirtyRegions: 32,
                priorityBucketCount: 16);

            Assert.Multiple(() =>
            {
                Assert.That(layout.DirtyRegionCapacity, Is.EqualTo(32));
                Assert.That(layout.CandidateCapacity, Is.EqualTo(129));
                Assert.That(layout.WorkgroupCount, Is.EqualTo(3));
                Assert.That(layout.PriorityBucketCount, Is.EqualTo(16));
                Assert.That(layout.GroupCountCapacity, Is.EqualTo(48));
                Assert.That(layout.PrefixCapacity, Is.EqualTo(64));
                Assert.That(layout.DirtyRegionBufferSize, Is.EqualTo(32UL * (ulong)Marshal.SizeOf<GPUDdgiDirtyRegion>()));
                Assert.That(layout.CandidateBufferSize, Is.EqualTo(129UL * (ulong)Marshal.SizeOf<GPUDdgiProbeCandidate>()));
                Assert.That(layout.GroupCountBufferSize, Is.EqualTo(48UL * sizeof(uint)));
                Assert.That(layout.PrefixBufferSize, Is.EqualTo(64UL * sizeof(uint)));
            });
        }

        [Test]
        public void CalculateGpuSchedulerResourceLayout_ClampsToSupportedMaximums()
        {
            DdgiGpuSchedulerResourceLayout layout = DdgiProbeVolumeManager.CalculateGpuSchedulerResourceLayout(
                activeProbeCount: DdgiProbeVolumeManager.AbsoluteMaxProbeCount + 1024,
                maxDirtyRegions: GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget + 1,
                priorityBucketCount: 1024);

            Assert.Multiple(() =>
            {
                Assert.That(layout.DirtyRegionCapacity, Is.EqualTo(GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget));
                Assert.That(layout.CandidateCapacity, Is.EqualTo(DdgiProbeVolumeManager.AbsoluteMaxProbeCount));
                Assert.That(layout.WorkgroupCount, Is.EqualTo(1024));
                Assert.That(layout.PriorityBucketCount, Is.EqualTo(256));
                Assert.That(layout.GroupCountCapacity, Is.EqualTo(1024 * 256));
                Assert.That(layout.PrefixCapacity, Is.EqualTo(1024 * 256 + 256));
            });
        }

        [TestCase(0, 1024, 1024, 0, 0)]
        [TestCase(12, 1024, 1024, 12, 0)]
        [TestCase(12, 4, 1024, 4, 8)]
        [TestCase(12, 1024, 4, 4, 8)]
        [TestCase(12, 0, 1024, 0, 12)]
        [TestCase(-12, 1024, 1024, 0, 0)]
        public void CalculateGpuSchedulerDirtyRegionUpload_ClampsAndReportsRecoverableOverflow(
            int sourceCount,
            int configuredLimit,
            int allocatedCapacity,
            int expectedUploadCount,
            int expectedOverflowCount)
        {
            DdgiGpuSchedulerDirtyRegionUploadPlan plan = DdgiProbeVolumeManager.CalculateGpuSchedulerDirtyRegionUpload(
                sourceCount,
                configuredLimit,
                allocatedCapacity);

            Assert.Multiple(() =>
            {
                Assert.That(plan.UploadCount, Is.EqualTo(expectedUploadCount));
                Assert.That(plan.OverflowCount, Is.EqualTo(expectedOverflowCount));
            });
        }
    }
}
