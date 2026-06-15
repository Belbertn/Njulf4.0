using Njulf.Rendering.Data;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public class GpuDrivenVisibilityTests
{
    [Test]
    public void CapacityPlanner_GrowsOnlyOverflowedLists()
    {
        var planner = new GpuVisibilityCapacityPlanner(new GpuVisibilityCapacity(4, 4, 4, 4));
        var counters = new GPUVisibilityCounters
        {
            OverflowFlags = (uint)(GpuVisibilityOverflowFlags.Opaque | GpuVisibilityOverflowFlags.Transparent),
            RequiredOpaqueCapacity = 9,
            RequiredMaskedCapacity = 3,
            RequiredTransparentCapacity = 5,
            RequiredShadowCapacity = 4
        };

        bool resized = planner.ApplyCounters(counters);

        Assert.That(resized, Is.True);
        Assert.That(planner.ResizeCount, Is.EqualTo(1));
        Assert.That(planner.Capacity.OpaqueMeshlets, Is.EqualTo(16));
        Assert.That(planner.Capacity.MaskedMeshlets, Is.EqualTo(4));
        Assert.That(planner.Capacity.TransparentMeshlets, Is.EqualTo(8));
        Assert.That(planner.Capacity.ShadowMeshlets, Is.EqualTo(4));
    }

    [Test]
    public void ListSignature_MatchesIdenticalDrawListsAndDetectsMismatch()
    {
        var cpu = new[]
        {
            new GPUMeshletDrawCommand { MeshletIndex = 1, InstanceId = 2, MaterialIndex = 3 },
            new GPUMeshletDrawCommand { MeshletIndex = 4, InstanceId = 5, MaterialIndex = 6 }
        };
        var gpu = new[]
        {
            new GPUMeshletDrawCommand { MeshletIndex = 1, InstanceId = 2, MaterialIndex = 3 },
            new GPUMeshletDrawCommand { MeshletIndex = 4, InstanceId = 5, MaterialIndex = 6 }
        };
        var changed = new[]
        {
            new GPUMeshletDrawCommand { MeshletIndex = 1, InstanceId = 2, MaterialIndex = 3 },
            new GPUMeshletDrawCommand { MeshletIndex = 99, InstanceId = 5, MaterialIndex = 6 }
        };

        GpuVisibilityListSignature cpuSignature = GpuVisibilityListSignature.FromDrawCommands(cpu);
        GpuVisibilityListSignature gpuSignature = GpuVisibilityListSignature.FromDrawCommands(gpu);
        GpuVisibilityListSignature changedSignature = GpuVisibilityListSignature.FromDrawCommands(changed);

        Assert.That(new GpuVisibilityComparisonResult("opaque", cpuSignature, gpuSignature).Matches, Is.True);
        Assert.That(new GpuVisibilityComparisonResult("opaque", cpuSignature, changedSignature).Matches, Is.False);
    }

    [Test]
    public void TransparentSortKey_OrdersFarToNearThenMaterialLayer()
    {
        GPUVisibilitySortKey far = GpuVisibilitySortKeyPacker.PackTransparentKey(0.9f, 0, 4, 1, 1, 0);
        GPUVisibilitySortKey near = GpuVisibilitySortKeyPacker.PackTransparentKey(0.1f, 0, 4, 1, 1, 1);
        GPUVisibilitySortKey sameDepthHigherLayer = GpuVisibilitySortKeyPacker.PackTransparentKey(0.9f, 2, 4, 1, 1, 2);

        Assert.That(far.Key, Is.LessThan(near.Key));
        Assert.That(sameDepthHigherLayer.Key, Is.GreaterThan(far.Key));
    }

    [Test]
    public void VisibilityConsumerBarrier_CoversIndirectAndMeshShaderReads()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                (GpuVisibilityPass.ConsumerStageMask & PipelineStageFlags2.DrawIndirectBit) != 0,
                Is.True);
            Assert.That(
                (GpuVisibilityPass.ConsumerStageMask & PipelineStageFlags2.TaskShaderBitExt) != 0,
                Is.True);
            Assert.That(
                (GpuVisibilityPass.ConsumerStageMask & PipelineStageFlags2.MeshShaderBitExt) != 0,
                Is.True);
            Assert.That(
                (GpuVisibilityPass.ConsumerStageMask & PipelineStageFlags2.TransferBit) != 0,
                Is.True);
            Assert.That(
                (GpuVisibilityPass.ConsumerAccessMask & AccessFlags2.IndirectCommandReadBit) != 0,
                Is.True);
            Assert.That(
                (GpuVisibilityPass.ConsumerAccessMask & AccessFlags2.ShaderStorageReadBit) != 0,
                Is.True);
            Assert.That(
                (GpuVisibilityPass.ConsumerAccessMask & AccessFlags2.TransferReadBit) != 0,
                Is.True);
        });
    }
}
