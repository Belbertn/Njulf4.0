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
}
