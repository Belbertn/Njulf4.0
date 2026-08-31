using System;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiMaskedFeedbackCompactionTests
{
    [Test]
    public void Abi_UsesFrozenHeaderAndCandidateSizes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiMaskedFeedbackCompactionAbi.Version,
                Is.EqualTo(1u));
            Assert.That(SimpleDdgiMaskedFeedbackCompactionAbi.HeaderBytes,
                Is.EqualTo(16u));
            Assert.That(SimpleDdgiMaskedFeedbackCompactionAbi.RecordBytes,
                Is.EqualTo(48u));
            Assert.That(Marshal.SizeOf<
                GPUSimpleDdgiMaskedFeedbackCompactPushConstants>(),
                Is.EqualTo(36));
        });
    }

    [Test]
    public void CapacityPolicy_UsesMeasuredHighWaterAndFiftyPercentMargin()
    {
        uint physical =
            SimpleDdgiMaskedFeedbackCompactionAbi.ResolvePhysicalCapacity(
                14_400u);
        uint bootstrap =
            SimpleDdgiMaskedFeedbackCompactionAbi.ResolveLogicalCapacity(
                0u,
                0u,
                physical);
        uint measured =
            SimpleDdgiMaskedFeedbackCompactionAbi.ResolveLogicalCapacity(
                2_000u,
                bootstrap,
                physical);

        Assert.Multiple(() =>
        {
            Assert.That(physical, Is.EqualTo(28_800u));
            Assert.That(bootstrap, Is.EqualTo(1_024u));
            Assert.That(measured, Is.EqualTo(3_256u));
            Assert.That(
                SimpleDdgiMaskedFeedbackCompactionAbi.ResolveBufferBytes(
                    physical),
                Is.EqualTo(1_382_416UL));
        });
    }

    [Test]
    public void CapacityPolicy_NeverShrinksAndClampsToPhysicalBacking()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiMaskedFeedbackCompactionAbi
                    .ResolveLogicalCapacity(8u, 900u, 2_000u),
                Is.EqualTo(900u));
            Assert.That(
                SimpleDdgiMaskedFeedbackCompactionAbi
                    .ResolveLogicalCapacity(10_000u, 900u, 2_000u),
                Is.EqualTo(2_000u));
            Assert.That(
                SimpleDdgiMaskedFeedbackCompactionAbi
                    .ResolveLogicalCapacity(0u, 0u, 0u),
                Is.Zero);
        });
    }

    [Test]
    public void PackedState_DistinguishesInactiveFromUninitialized()
    {
        uint inactive =
            SimpleDdgiMaskedFeedbackCompactionAbi.PackState(1_024u, false);
        uint active =
            SimpleDdgiMaskedFeedbackCompactionAbi.PackState(1_024u, true);

        Assert.Multiple(() =>
        {
            Assert.That(inactive &
                SimpleDdgiMaskedFeedbackCompactionAbi.InitializedBit,
                Is.Not.Zero);
            Assert.That(inactive &
                SimpleDdgiMaskedFeedbackCompactionAbi.ActiveBit,
                Is.Zero);
            Assert.That(active &
                SimpleDdgiMaskedFeedbackCompactionAbi.ActiveBit,
                Is.Not.Zero);
            Assert.That(active &
                SimpleDdgiMaskedFeedbackCompactionAbi.CapacityMask,
                Is.EqualTo(1_024u));
        });
    }

    [Test]
    public void ShaderContract_OverflowExecutesInlineExactFallback()
    {
        string forward = ReadShader("forward.frag");
        string abi = ReadShader("ddgi_masked_feedback_compaction_abi.glsl");
        string compact = ReadShader("ddgi_masked_feedback_compact.comp");

        int append = forward.IndexOf(
            "TryHandleSimpleDdgiMaskedFeedbackWithoutInlineGather",
            StringComparison.Ordinal);
        int exactFallback = forward.IndexOf(
            "!exactFeedbackMaskedHandledByCompaction",
            append,
            StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(append, Is.GreaterThanOrEqualTo(0));
            Assert.That(exactFallback, Is.GreaterThan(append));
            Assert.That(abi, Does.Contain("ordinal >= logicalCapacity"));
            Assert.That(abi, Does.Contain(
                "SIMPLE_DDGI_MASKED_FEEDBACK_OVERFLOW_FALLBACK_WORD"));
            Assert.That(compact, Does.Contain("SampleSimpleDdgiGather("));
            Assert.That(compact, Does.Contain(
                "EmitSimpleDdgiSurfaceReceiverFeedbackCore("));
        });
    }

    [Test]
    public void ShaderContract_ReusesDenseSelectionAndStableIdentity()
    {
        string surface = ReadShader(
            "ddgi_receiver_feedback_surface_producer.glsl");
        string forward = ReadShader("forward.frag");
        string compact = ReadShader("ddgi_masked_feedback_compact.comp");

        Assert.Multiple(() =>
        {
            Assert.That(surface, Does.Contain(
                "SimpleDdgiSurfaceFeedbackCouldSelectProducer"));
            Assert.That(surface, Does.Contain(
                "SimpleDdgiSurfaceFeedbackRepresentativePixel"));
            Assert.That(forward, Does.Contain(
                "uvec3(fragObjectIndex, fragMaterialIndex, fragMeshletIndex)"));
            Assert.That(compact, Does.Contain("stableGeometryIdentity"));
            Assert.That(compact, Does.Contain("survivingCoverage"));
        });
    }

    [Test]
    public void HostContract_ResolvesBeforeProducerCompletion()
    {
        string host = ReadRepoFile(
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs");
        int compact = host.IndexOf(
            "RecordSimpleDdgiMaskedFeedbackCompaction(",
            StringComparison.Ordinal);
        int completion = host.IndexOf(
            ".AlphaMaskOrFoliage",
            compact,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(compact, Is.GreaterThanOrEqualTo(0));
            Assert.That(completion, Is.GreaterThan(compact));
        });
    }

    private static string ReadShader(string file) =>
        ReadRepoFile("Njulf.Shaders", file);

    private static string ReadRepoFile(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string path = directory.FullName;
            foreach (string segment in segments)
                path = Path.Combine(path, segment);
            if (File.Exists(path))
            {
                return File.ReadAllText(path)
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', segments));
    }
}
