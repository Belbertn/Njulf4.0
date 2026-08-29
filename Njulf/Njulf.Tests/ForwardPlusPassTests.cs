using System.IO;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ForwardPlusPassTests
{
    [Test]
    public void ForwardPlus_UsesSimpleDdgi()
    {
        string source = ReadRepoText("Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ShouldApplyDdgi"));
        });
    }

    [Test]
    public void ForwardOpaquePipelineKey_HasCollisionFreeFixedCacheIndex()
    {
        var indices = new HashSet<int>();
        foreach (ForwardOpaquePipelineFamily family in
                 Enum.GetValues<ForwardOpaquePipelineFamily>())
        {
            for (int features = 0;
                 features <
                 ForwardOpaquePipelineKey.FeatureCombinationCount;
                 features++)
            {
                var key = new ForwardOpaquePipelineKey(
                    family,
                    (ForwardOpaquePipelineFeatures)features);
                Assert.That(indices.Add(key.CacheIndex), Is.True, key.ToString());
            }
        }

        Assert.That(
            indices.Count,
            Is.EqualTo(ForwardOpaquePipelineKey.CacheEntryCount));
    }

    [Test]
    public void ForwardPlus_UsesProductionGiDisabledPipelineSelection()
    {
        string source = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(
                source,
                Does.Contain("ShouldUseProductionForwardGiDisabledPipeline"));
            Assert.That(
                source,
                Does.Contain("TryResolveForwardOpaquePipeline"));
        });
    }

    [Test]
    public void ReceiverGatherSurfaceDescriptor_CoversTheWholeAllocation()
    {
        string source = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs").ReplaceLineEndings("\n");
        const string registrationStart =
            "_bindlessHeap.RegisterStorageBuffer(\n" +
            "                    BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase + i,";
        int start = source.IndexOf(registrationStart, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        int end = source.IndexOf(");", start, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThan(start));
        string registration = source[start..(end + 2)];

        Assert.Multiple(() =>
        {
            Assert.That(registration, Does.Contain(
                "gatherSurfaceNativeBuffers[i],\n                    0,\n                    gatherSurfaceByteSize"));
            Assert.That(registration, Does.Not.Contain(
                nameof(FoliageManager.AuthoredIndirectDispatchOffset)));
        });
    }

    [Test]
    public void ForwardMaterial_ColdDiagnosticMetadataIsDemandLoaded()
    {
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        int readStart = common.IndexOf(
            "GPUMaterialData ReadForwardMaterial(",
            StringComparison.Ordinal);
        int diagnosticStart = common.IndexOf(
            "void LoadForwardMaterialDiagnosticMetadata(",
            StringComparison.Ordinal);
        Assert.That(readStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(diagnosticStart, Is.GreaterThan(readStart));
        string ordinaryRead = common[readStart..diagnosticStart];

        Assert.Multiple(() =>
        {
            Assert.That(ordinaryRead, Does.Contain(
                "material.TransportProfileRevision = 0u;"));
            Assert.That(ordinaryRead, Does.Contain(
                "material.TransportProfileQuality = 0u;"));
            Assert.That(ordinaryRead, Does.Not.Contain(
                "coldBaseWord + 56u"));
            Assert.That(ordinaryRead, Does.Not.Contain(
                "coldBaseWord + 58u"));
            Assert.That(forward, Does.Contain(
                "LoadForwardMaterialDiagnosticMetadata("));
            Assert.That(forward, Does.Contain(
                "debugViewMode == MATERIAL_DEBUG_TRANSPORT_PROFILE"));
        });
    }

    [Test]
    public void ForwardMaterialUpload_PublishesToFragmentAndComputeConsumers()
    {
        string source = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "MaterialManager.cs");
        int barrierStart = source.IndexOf(
            "private void RecordForwardMaterialReadBarrier(",
            StringComparison.Ordinal);
        int barrierEnd = source.IndexOf(
            "private void UpdateRegisteredBindlessBuffer(",
            barrierStart,
            StringComparison.Ordinal);
        Assert.That(barrierStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(barrierEnd, Is.GreaterThan(barrierStart));
        string barrier = source[barrierStart..barrierEnd];

        Assert.Multiple(() =>
        {
            Assert.That(
                barrier,
                Does.Contain("PipelineStageFlags2.FragmentShaderBit"));
            Assert.That(
                barrier,
                Does.Contain("PipelineStageFlags2.ComputeShaderBit"));
            Assert.That(
                barrier,
                Does.Contain("AccessFlags2.ShaderStorageReadBit"));
        });
    }

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
