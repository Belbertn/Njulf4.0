using System;
using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class HybridReflectionSparseLobePayloadTests
{
    [Test]
    public void Abi_UsesExactScreenLinearTwoWordRecords()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HybridReflectionSparseLobePayloadAbi.Version,
                Is.EqualTo(1u));
            Assert.That(HybridReflectionSparseLobePayloadAbi.WordsPerPixel,
                Is.EqualTo(2u));
            Assert.That(HybridReflectionSparseLobePayloadAbi.BytesPerPixel,
                Is.EqualTo(8u));
            Assert.That(HybridReflectionSparseLobePayloadAbi
                    .ResolveBufferBytes(16u, 9u),
                Is.EqualTo(1_152UL));
            Assert.That(HybridReflectionSparseLobePayloadAbi
                    .ResolveBufferBytes(0u, 9u),
                Is.Zero);
            Assert.That(HybridReflectionSparseLobePayloadAbi
                    .ResolvePixelWordOffset(15u, 8u, 16u, 9u),
                Is.EqualTo(286u));
            Assert.That(
                () => HybridReflectionSparseLobePayloadAbi
                    .ResolvePixelWordOffset(16u, 8u, 16u, 9u),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => HybridReflectionSparseLobePayloadAbi
                    .ResolvePixelWordOffset(
                        uint.MaxValue - 1u,
                        1u,
                        uint.MaxValue,
                        2u),
                Throws.TypeOf<OverflowException>());
        });
    }

    [Test]
    public void DynamicRenderingContract_RemovesOnlyTheLobeAttachment()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    hybridReflectionReceiverEnabled: true),
                Is.EqualTo(3u));
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    hybridReflectionReceiverEnabled: true,
                    sparseHybridLobePayloadEnabled: true),
                Is.EqualTo(2u));
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    nearFieldDirectSourceEnabled: true,
                    giCausticReceiverEnabled: true,
                    hybridReflectionReceiverEnabled: true),
                Is.EqualTo(6u));
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    nearFieldDirectSourceEnabled: true,
                    giCausticReceiverEnabled: true,
                    hybridReflectionReceiverEnabled: true,
                    sparseHybridLobePayloadEnabled: true),
                Is.EqualTo(5u));
            Assert.That(
                () => ForwardDynamicRenderingContract
                    .ResolveColorAttachmentCount(
                        hasColorAttachment: true,
                        sparseHybridLobePayloadEnabled: true),
                Throws.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void ShaderContract_PreservesBaselineAndSparseArtifacts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ForwardHybridReflectionReceiverContract
                    .ResolveFragmentShader(
                        simple: false,
                        simpleFullInput: false,
                        giCaustic: false,
                        nearField: false),
                Is.EqualTo(
                    "forward_opaque_ddgi_hybrid_reflection.frag.spv"));
            Assert.That(
                ForwardHybridReflectionReceiverContract
                    .ResolveFragmentShader(
                        simple: true,
                        simpleFullInput: true,
                        giCaustic: true,
                        nearField: true,
                        receiverCacheRequired: true,
                        sparseLobePayload: true),
                Is.EqualTo(
                    "forward_opaque_simple_full_input_ddgi_c4_c5_" +
                    "cache_required_hybrid_reflection_sparse_lobe.frag.spv"));
            Assert.That(
                ForwardHybridReflectionReceiverContract
                    .ResolveFragmentShader(
                        simple: true,
                        simpleFullInput: false,
                        giCaustic: false,
                        nearField: true,
                        receiverCacheRequired: true,
                        receiverCacheExactFallbackOnly: true),
                Is.EqualTo(
                    "forward_opaque_simple_ddgi_c5_" +
                    "cache_exact_fallback_hybrid_reflection.frag.spv"));
            Assert.That(
                ForwardHybridReflectionReceiverContract
                    .ResolveFragmentShader(
                        simple: false,
                        simpleFullInput: false,
                        giCaustic: true,
                        nearField: false,
                        receiverCacheRequired: true,
                        receiverCacheCombined: true,
                        sparseLobePayload: true),
                Is.EqualTo(
                    "forward_opaque_ddgi_c4_" +
                    "cache_combined_hybrid_reflection_sparse_lobe.frag.spv"));
            Assert.That(
                () => ForwardHybridReflectionReceiverContract
                    .ResolveFragmentShader(
                        simple: false,
                        simpleFullInput: false,
                        giCaustic: false,
                        nearField: false,
                        receiverCacheExactFallbackOnly: true),
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                () => ForwardHybridReflectionReceiverContract
                    .ResolveFragmentShader(
                        simple: false,
                        simpleFullInput: false,
                        giCaustic: false,
                        nearField: false,
                        receiverCacheRequired: true,
                        receiverCacheExactFallbackOnly: true,
                        receiverCacheCombined: true),
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                BindlessIndex.HybridReflectionSparseLobeBufferFrame1,
                Is.EqualTo(
                    BindlessIndex.HybridReflectionSparseLobeBufferBase + 1));
            Assert.That(BindlessIndex.StaticBufferCount,
                Is.EqualTo(
                    BindlessIndex.HybridReflectionSparseLobeBufferFrame1 + 1));
        });
    }

    [Test]
    public void RuntimeContract_ClearsPublishesAndReadsTheExactSidecar()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag")
            .ReplaceLineEndings("\n");
        string ssr = ReadRepoText(
                "Njulf.Shaders", "hybrid_reflection_ssr.comp")
            .ReplaceLineEndings("\n");
        string pass = ReadRepoText(
                "Njulf.Rendering", "Pipeline",
                "ForwardPlusPass.SparseHybridLobePayload.cs")
            .ReplaceLineEndings("\n");

        int sparseLoad = ssr.IndexOf(
            "NjulfHybridSparseLobeLoad(", StringComparison.Ordinal);
        int validityGuard = ssr.IndexOf(
            "if (!HybridReflectionPayloadValid(payload))",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(forward, Does.Contain(
                "NJULF_HYBRID_REFLECTION_SPARSE_LOBE_OUTPUT"));
            Assert.That(forward, Does.Contain(
                "NjulfHybridSparseLobeStore("));
            Assert.That(forward, Does.Contain(
                "NjulfHybridReflectionPackUnorm8(anisotropyStrength) != 0u"),
                "Sub-threshold anisotropy must remain bit-exact.");
            Assert.That(ssr, Does.Contain(
                "NJULF_HYBRID_REFLECTION_SPARSE_LOBE_INPUT"));
            Assert.That(sparseLoad, Is.GreaterThan(validityGuard),
                "Rejected/background receivers must not read sparse storage.");
            Assert.That(pass, Does.Contain("CmdFillBuffer("));
            Assert.That(pass, Does.Contain(
                "SrcStageMask = PipelineStageFlags2.TransferBit"));
            Assert.That(pass, Does.Contain(
                "DstStageMask = PipelineStageFlags2.FragmentShaderBit"));
            Assert.That(pass, Does.Contain(
                "SrcStageMask = PipelineStageFlags2.FragmentShaderBit"));
            Assert.That(pass, Does.Contain(
                "DstStageMask = PipelineStageFlags2.ComputeShaderBit"));
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

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, segments));
    }
}
