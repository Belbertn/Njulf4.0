using System;
using System.IO;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiCompactGatherTests
{
    [Test]
    public void ReceiverAbi_UsesOneAlignedFourWordLoadAndFlagsLastPublication()
    {
        string abi = ReadRepoText("Njulf.Shaders", "ddgi_simple_receiver_abi.glsl");
        string reader = Slice(
            abi,
            "SimpleDdgiReceiverProbe ReadSimpleDdgiReceiverProbe(",
            "void WriteInvalidSimpleDdgiReceiverProbe(");
        string packedPublisher = Slice(
            abi,
            "void PublishPackedSimpleDdgiReceiverProbe(",
            "bool PublishSimpleDdgiReceiverProbe(");

        Assert.Multiple(() =>
        {
            Assert.That(abi, Does.Contain("SIMPLE_DDGI_RECEIVER_PROBE_STRIDE_WORDS = 4u"));
            Assert.That(Count(reader, "ReadStorageAlignedUVec4Uniform("), Is.EqualTo(1));
            Assert.That(reader, Does.Not.Contain("ReadStorageWordUniform("));
            Assert.That(
                packedPublisher.IndexOf("baseWord + 2u, 0u", StringComparison.Ordinal),
                Is.LessThan(packedPublisher.IndexOf("baseWord + 0u, packed.x", StringComparison.Ordinal)));
            Assert.That(
                packedPublisher.IndexOf("baseWord + 3u, packed.w", StringComparison.Ordinal),
                Is.LessThan(packedPublisher.IndexOf("baseWord + 2u, packed.z", StringComparison.Ordinal)));
            Assert.That(Count(packedPublisher, "memoryBarrierBuffer();"), Is.EqualTo(2));
        });
    }

    [Test]
    public void ReceiverGather_RejectsCompactStateBeforeAnyAtlasAccess()
    {
        string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
        string gather = Slice(
            shared,
            "SimpleDdgiGatherResult SampleSimpleDdgiVolumeGather(",
            "// Volume identity and transition ownership");

        int compactRead = gather.IndexOf(
            "ReadSimpleDdgiReceiverProbe(",
            StringComparison.Ordinal);
        int stateReject = gather.IndexOf(
            "SimpleDdgiReceiverProbeSupportsGather(receiverProbe)",
            StringComparison.Ordinal);
        int earlyContinue = gather.IndexOf(
            "if (!stateSupported)",
            stateReject,
            StringComparison.Ordinal);
        int addressBuild = gather.IndexOf(
            "TryBuildSimpleDdgiAtlasAddress(",
            StringComparison.Ordinal);
        int irradianceRead = gather.IndexOf(
            "SampleSimpleDdgiIrradianceBilinearAtAddress(",
            StringComparison.Ordinal);
        int visibilityRead = gather.IndexOf(
            "SampleSimpleDdgiVisibilityBilinearAtAddress(",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(compactRead, Is.GreaterThanOrEqualTo(0));
            Assert.That(stateReject, Is.GreaterThan(compactRead));
            Assert.That(earlyContinue, Is.GreaterThan(stateReject));
            Assert.That(addressBuild, Is.GreaterThan(earlyContinue));
            Assert.That(irradianceRead, Is.GreaterThan(addressBuild));
            Assert.That(visibilityRead, Is.GreaterThan(irradianceRead));
            Assert.That(Count(gather, "ReadSimpleDdgiReceiverProbe("), Is.EqualTo(1));
            Assert.That(shared, Does.Contain("address.irradianceBaseWord"));
            Assert.That(shared, Does.Contain("address.visibilityBaseWord"));
        });
    }

    [Test]
    public void SolverKernelsKeepComputeStateWhileReceiverShadersUseCompactDefault()
    {
        string[] computeStateKernels =
        [
            "ddgi_simple_trace.comp",
            "ddgi_simple_transport.comp",
            "ddgi_simple_transport_audit.comp"
        ];
        foreach (string shader in computeStateKernels)
        {
            string source = ReadRepoText("Njulf.Shaders", shader);
            Assert.That(
                source,
                Does.Contain("#define SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE 1"),
                shader);
        }

        string[] receiverShaders =
        [
            "forward.frag",
            "foliage_grass.mesh",
            "foliage_mesh.mesh",
            "fog.comp",
            "particle.vert"
        ];
        foreach (string shader in receiverShaders)
        {
            string source = ReadRepoText("Njulf.Shaders", shader);
            Assert.That(
                source,
                Does.Not.Contain("SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE"),
                shader);
        }
    }

    [Test]
    public void BothPublicationModesCommitCompactReceiverRecords()
    {
        string publish = ReadRepoText("Njulf.Shaders", "ddgi_simple_publish.comp");
        string commit = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_commit_local.comp");

        Assert.Multiple(() =>
        {
            Assert.That(publish, Does.Contain("PublishSimpleDdgiReceiverProbe("));
            Assert.That(publish, Does.Contain("pc.ReceiverProbeBufferIndex"));
            Assert.That(commit, Does.Contain("TryPackSimpleDdgiReceiverProbe("));
            Assert.That(commit, Does.Contain("PublishPackedSimpleDdgiReceiverProbe("));
            Assert.That(commit, Does.Contain("SchedulerVolumeSpacing(volumeIndex)"));
        });
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Slice(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.That(startIndex, Is.GreaterThanOrEqualTo(0), start);
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.That(endIndex, Is.GreaterThan(startIndex), end);
        return source[startIndex..endIndex];
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
