using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSampledAtlasTests
{
    [Test]
    public void ProbeLayerMapping_IsStableAtTextureGroupBoundaries()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiSampledAtlas.TryResolveProbeLayer(0, 2048, 3, out int group0, out int layer0),
                Is.True);
            Assert.That(group0, Is.EqualTo(0));
            Assert.That(layer0, Is.EqualTo(0));

            Assert.That(
                SimpleDdgiSampledAtlas.TryResolveProbeLayer(2047, 2048, 3, out int groupLast, out int layerLast),
                Is.True);
            Assert.That(groupLast, Is.EqualTo(0));
            Assert.That(layerLast, Is.EqualTo(2047));

            Assert.That(
                SimpleDdgiSampledAtlas.TryResolveProbeLayer(2048, 2048, 3, out int nextGroup, out int nextLayer),
                Is.True);
            Assert.That(nextGroup, Is.EqualTo(1));
            Assert.That(nextLayer, Is.EqualTo(0));

            Assert.That(
                SimpleDdgiSampledAtlas.TryResolveProbeLayer(6144, 2048, 3, out _, out _),
                Is.False);
        });
    }

    [Test]
    public void ProbeCapacityProvisioning_BoundsReallocationChurnAndDescriptorRange()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(1, 2048), Is.EqualTo(256));
            Assert.That(SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(256, 2048), Is.EqualTo(256));
            Assert.That(SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(257, 2048), Is.EqualTo(512));
            Assert.That(
                SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(
                    32_768,
                    2_048),
                Is.EqualTo(32_768));
            Assert.That(BindlessIndex.SimpleDdgiSampledVisibilityTextureBase +
                BindlessIndex.MaxSimpleDdgiSampledAtlasTextureGroups,
                Is.EqualTo(BindlessIndex.FirstDynamicTextureIndex));
        });
    }

    [Test]
    public void ProvisionedCapacity_AndImageBytesUseTheSameAdmissionBoundary()
    {
        const ulong bytesPerProbe = (8UL * 8UL + 16UL * 16UL) * 8UL;
        int capacity = SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(257, 2_048);

        Assert.Multiple(() =>
        {
            Assert.That(capacity, Is.EqualTo(512));
            Assert.That(
                SimpleDdgiSampledAtlas.CalculateEstimatedImageBytesForProbeCapacity(capacity),
                Is.EqualTo(512UL * bytesPerProbe));
        });
    }

    [Test]
    public void StableCapacityReconciliation_ShrinksAfterQualityRollback()
    {
        int ultraCapacity =
            SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(23_636, 2_048);
        int highCapacity =
            SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(17_960, 2_048);

        Assert.Multiple(() =>
        {
            Assert.That(ultraCapacity, Is.EqualTo(23_808));
            Assert.That(highCapacity, Is.EqualTo(18_176));
            Assert.That(
                SimpleDdgiSampledAtlas.RequiresStableCapacityReallocation(
                    ultraCapacity,
                    highCapacity,
                    2_048,
                    2_048),
                Is.True);
            Assert.That(
                SimpleDdgiSampledAtlas.RequiresStableCapacityReallocation(
                    highCapacity,
                    highCapacity,
                    2_048,
                    2_048),
                Is.False);
        });
    }

    [TestCase(1)]
    [TestCase(64)]
    [TestCase(128)]
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(2_048)]
    public void LayoutAdmissionRounding_MatchesOrConservativelyBoundsDeviceProvisioning(
        int layersPerTexture)
    {
        int deviceCapacity = checked(
            layersPerTexture *
            BindlessIndex.MaxSimpleDdgiSampledAtlasTextureGroups);
        int requestedProbes = Math.Min(257, deviceCapacity);
        int runtimeCapacity =
            SimpleDdgiSampledAtlas.CalculateProvisionedProbeCapacity(
                requestedProbes,
                layersPerTexture);
        int admittedCapacity =
            SimpleDdgiMemoryPlan.ResolveSampledAtlasProbeCapacity(
                requestedProbes);

        Assert.That(admittedCapacity, Is.GreaterThanOrEqualTo(runtimeCapacity));
        if (layersPerTexture >= 256)
            Assert.That(admittedCapacity, Is.EqualTo(runtimeCapacity));
    }

    [Test]
    public void UpdatedProbeCopies_CoalesceRunsAndBoundPathologicalRegionLists()
    {
        int[] contiguousAndDuplicate = [3, 4, 4, 5, 12, 13, 90];
        int[] fragmented = Enumerable.Range(0, 65).Select(index => index * 2).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiSampledAtlas.CountContiguousProbeRuns(contiguousAndDuplicate),
                Is.EqualTo(3));
            Assert.That(SimpleDdgiSampledAtlas.ShouldCopyWholeGroup(64), Is.False);
            Assert.That(
                SimpleDdgiSampledAtlas.ShouldCopyWholeGroup(
                    SimpleDdgiSampledAtlas.CountContiguousProbeRuns(fragmented)),
                Is.True);
        });
    }
}
