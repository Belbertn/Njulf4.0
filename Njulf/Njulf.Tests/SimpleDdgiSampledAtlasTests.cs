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
                Is.EqualTo(BindlessIndex.OpaqueSceneColorSnapshotTexture));
            Assert.That(BindlessIndex.OpaqueSceneColorSnapshotTexture + 1,
                Is.EqualTo(BindlessIndex.FirstDynamicTextureIndex));
        });
    }

    [Test]
    public void ProvisionedCapacity_AndImageBytesUseTheSameAdmissionBoundary()
    {
        const ulong bytesPerProbe = 8UL * 8UL * 8UL + 16UL * 16UL * 4UL;
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
    public void FullSyncCopy_ExcludesRoundedImagePaddingBeyondCanonicalPayloadBuffers()
    {
        const int virtualProbeCount = 15_368;
        const int physicalProbeCapacity = 12_072;
        const int sampledProbeCapacity = 12_288;
        const ulong irradianceBytesPerProbe = 8UL * 8UL * 8UL;
        const ulong visibilityBytesPerProbe = 16UL * 16UL * 4UL;

        int copiedProbeCount = SimpleDdgiSampledAtlas.CalculateSafeCopyProbeCount(
            virtualProbeCount,
            sampledProbeCapacity,
            physicalProbeCapacity * irradianceBytesPerProbe,
            physicalProbeCapacity * visibilityBytesPerProbe);

        Assert.That(copiedProbeCount, Is.EqualTo(physicalProbeCapacity));
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

    [Test]
    public void StableCapacityCheck_UsesCachedLayerLimitWithoutQueryingVulkan()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiSampledAtlas.cs"));
        int constructorStart = source.IndexOf(
            "public SimpleDdgiSampledAtlas(",
            StringComparison.Ordinal);
        int constructorEnd = source.IndexOf(
            "public bool IsReady",
            constructorStart,
            StringComparison.Ordinal);
        int ensureCapacityStart = source.IndexOf(
            "public bool EnsureCapacity(int requiredProbeCount)",
            StringComparison.Ordinal);
        int ensureCapacityEnd = source.IndexOf(
            "internal static bool RequiresStableCapacityReallocation(",
            ensureCapacityStart,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(constructorStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(constructorEnd, Is.GreaterThan(constructorStart));
            Assert.That(ensureCapacityStart, Is.GreaterThan(constructorEnd));
            Assert.That(ensureCapacityEnd, Is.GreaterThan(ensureCapacityStart));
        });

        string constructor = source[constructorStart..constructorEnd];
        string ensureCapacity = source[ensureCapacityStart..ensureCapacityEnd];
        Assert.Multiple(() =>
        {
            Assert.That(
                constructor,
                Does.Contain("_resolvedLayersPerTexture = ResolveLayersPerTexture();"));
            Assert.That(
                constructor,
                Does.Contain("_ = recordRuntimeStall ??"));
            Assert.That(
                ensureCapacity,
                Does.Contain("int layersPerTexture = _resolvedLayersPerTexture;"));
            Assert.That(ensureCapacity, Does.Not.Contain("ResolveLayersPerTexture("));
            Assert.That(ensureCapacity, Does.Not.Contain("GetPhysicalDeviceProperties("));
            Assert.That(source, Does.Contain("GpuCompletionToken.ForFrameFence("));
            Assert.That(source, Does.Not.Contain("WaitForDeviceIdle("));
            Assert.That(source, Does.Not.Contain("_context.WaitIdle"));
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
    public void GpuPublication_UsesBoundedStorageImageTable()
    {
        Assert.That(SimpleDdgiSampledAtlas.MaxGpuPublishTextureGroups, Is.EqualTo(16));
    }

    [Test]
    public void GpuPublication_EnablesItsOptionalDescriptorIndexingFeatureAndUsesQueuePortableBarriers()
    {
        string context = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Core",
            "VulkanContext.cs"));
        string pass = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPasses.cs"));
        string atlas = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiSampledAtlas.cs"));
        string manager = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(
                context.Split("ShaderStorageImageArrayNonUniformIndexing = true").Length - 1,
                Is.EqualTo(1),
                "The optional storage-image indexing feature must be queried.");
            Assert.That(
                context,
                Does.Contain("_shaderStorageImageArrayNonUniformIndexingSupported"));
            Assert.That(
                context,
                Does.Not.Contain("missingFeatures.Add(\"shaderStorageImageArrayNonUniformIndexing\")"));
            Assert.That(
                pass,
                Does.Contain("_context.ShaderStorageImageArrayNonUniformIndexingSupported &&"));
            Assert.That(
                manager,
                Does.Contain("sampled-atlas-storage-image-non-uniform-indexing-unavailable"));
            Assert.That(
                atlas,
                Does.Not.Contain("PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit"));
        });
    }

    private static string FindSourceFile(params string[] relativeParts)
    {
        string directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(directory);
            directory = parent?.FullName ?? string.Empty;
        }

        throw new FileNotFoundException("Could not locate repository source file.", Path.Combine(relativeParts));
    }
}
