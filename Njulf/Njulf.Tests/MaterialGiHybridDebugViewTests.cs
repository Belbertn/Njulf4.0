using System.Runtime.InteropServices;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialGiHybridDebugViewTests
{
    [Test]
    public void DebugViews_AreStableContiguousValuesAfterExistingViews()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                (uint)GlobalIlluminationDebugView.MaterialTransportSourceOwnership,
                Is.EqualTo(46u));
            Assert.That(
                (uint)GlobalIlluminationDebugView.HybridEstimatorOwnership,
                Is.EqualTo(47u));
            Assert.That(
                (uint)GlobalIlluminationDebugView.HybridFinalComposition,
                Is.EqualTo(48u));
            Assert.That(
                (uint)GlobalIlluminationDebugView.MaterialTransportHitProvenance,
                Is.EqualTo(49u));
            Assert.That(
                Enum.GetValues<GlobalIlluminationDebugView>().Distinct().Count(),
                Is.EqualTo(Enum.GetValues<GlobalIlluminationDebugView>().Length));
        });
    }

    [Test]
    public void DebugViewSetter_RejectsUndefinedPersistedValues()
    {
        var settings = new RenderSettings();

        settings.GlobalIllumination.DebugView =
            GlobalIlluminationDebugView.HybridEstimatorOwnership;
        Assert.That(
            settings.GlobalIllumination.DebugView,
            Is.EqualTo(GlobalIlluminationDebugView.HybridEstimatorOwnership));

        settings.GlobalIllumination.DebugView =
            (GlobalIlluminationDebugView)uint.MaxValue;
        Assert.That(
            settings.GlobalIllumination.DebugView,
            Is.EqualTo(GlobalIlluminationDebugView.None));
    }

    [Test]
    public void CompositePushConstants_HaveStableThirtyTwoByteProvenanceAbi()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSsgiCompositePushConstants>(), Is.EqualTo(32));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.GiFinalDiffuseTextureIndex)).ToInt32(),
                Is.EqualTo(0));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.SceneMaterialTextureIndex)).ToInt32(),
                Is.EqualTo(4));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.MaterialTransportProvenanceTextureIndex)).ToInt32(),
                Is.EqualTo(8));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.DebugView)).ToInt32(),
                Is.EqualTo(12));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.CompositionFlags)).ToInt32(),
                Is.EqualTo(16));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.Padding0)).ToInt32(),
                Is.EqualTo(20));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.Padding1)).ToInt32(),
                Is.EqualTo(24));
            Assert.That(
                Marshal.OffsetOf<GPUSsgiCompositePushConstants>(
                    nameof(GPUSsgiCompositePushConstants.Padding2)).ToInt32(),
                Is.EqualTo(28));
        });
    }

    [Test]
    public void SpatialProvenanceEncoding_HasStableR8UnormCodes()
    {
        MaterialTransportProvenanceCode[] codes =
        [
            MaterialTransportProvenanceCode.Background,
            MaterialTransportProvenanceCode.DetailedMesh,
            MaterialTransportProvenanceCode.CompactPrimitive,
            MaterialTransportProvenanceCode.FarField,
            MaterialTransportProvenanceCode.Unknown
        ];

        Assert.Multiple(() =>
        {
            Assert.That((byte)codes[0], Is.EqualTo(0));
            Assert.That((byte)codes[1], Is.EqualTo(1));
            Assert.That((byte)codes[2], Is.EqualTo(2));
            Assert.That((byte)codes[3], Is.EqualTo(3));
            Assert.That((byte)codes[4], Is.EqualTo(255));
            foreach (MaterialTransportProvenanceCode code in codes)
            {
                Assert.That(
                    MaterialTransportProvenanceEncoding.DecodeUnorm(
                        MaterialTransportProvenanceEncoding.EncodeUnorm(code)),
                    Is.EqualTo(code));
            }
            Assert.That(
                MaterialTransportProvenanceEncoding.DecodeUnorm(17.0f / 255.0f),
                Is.EqualTo(MaterialTransportProvenanceCode.Unknown));
            Assert.That(
                MaterialTransportProvenanceEncoding.DecodeUnorm(float.NaN),
                Is.EqualTo(MaterialTransportProvenanceCode.Unknown));
        });
    }

    [Test]
    public void SpatialProvenanceTarget_IsAllocatedOnlyWhileDiagnosticIsRequested()
    {
        var settings = new RenderSettings();

        Assert.That(
            VulkanRenderer.IsMaterialTransportProvenanceTargetEnabled(settings),
            Is.False);

        settings.GlobalIllumination.DebugView =
            GlobalIlluminationDebugView.MaterialTransportHitProvenance;
        Assert.That(
            VulkanRenderer.IsMaterialTransportProvenanceTargetEnabled(settings),
            Is.True);

        settings.GlobalIllumination.DebugView =
            GlobalIlluminationDebugView.MaterialTransportSourceOwnership;
        Assert.That(
            VulkanRenderer.IsMaterialTransportProvenanceTargetEnabled(settings),
            Is.False);
    }

    [Test]
    public void CompositePass_ClassifiesStandaloneViewsAndTransportCapabilities()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.EnableMaterialGiV2ForConformance(
            MaterialGiV2Feature.MaterialTransport |
            MaterialGiV2Feature.FarFieldMaterial |
            MaterialGiV2Feature.HybridComposition);
        settings.GlobalIllumination.EnvironmentFallbackIntensity = 1.0f;
        settings.GlobalIllumination.FarFieldClipmapEnabled = true;

        SsgiCompositionFlags flags =
            SsgiCompositePass.ResolveCompositionFlags(settings.GlobalIllumination);

        Assert.Multiple(() =>
        {
            Assert.That(
                SsgiCompositePass.IsStandaloneHybridDiagnosticView(
                    GlobalIlluminationDebugView.MaterialTransportSourceOwnership),
                Is.True);
            Assert.That(
                SsgiCompositePass.IsStandaloneHybridDiagnosticView(
                    GlobalIlluminationDebugView.HybridEstimatorOwnership),
                Is.True);
            Assert.That(
                SsgiCompositePass.IsStandaloneHybridDiagnosticView(
                    GlobalIlluminationDebugView.HybridFinalComposition),
                Is.True);
            Assert.That(
                SsgiCompositePass.IsStandaloneHybridDiagnosticView(
                    GlobalIlluminationDebugView.MaterialTransportHitProvenance),
                Is.True);
            Assert.That(
                SsgiCompositePass.IsStandaloneHybridDiagnosticView(
                    GlobalIlluminationDebugView.FinalIndirect),
                Is.False);
            Assert.That(flags.HasFlag(SsgiCompositionFlags.HybridV2), Is.True);
            Assert.That(flags.HasFlag(SsgiCompositionFlags.EnvironmentFallback), Is.True);
            Assert.That(flags.HasFlag(SsgiCompositionFlags.MaterialTransportV2), Is.True);
            Assert.That(flags.HasFlag(SsgiCompositionFlags.FarFieldTransport), Is.True);
        });
    }

    [Test]
    public void EstimatorOwnershipChannels_FormExpectedNonOverlappingPartition()
    {
        const float ddgiOwnership = 0.6f;
        const float fallbackOwnership = 0.4f;
        const float ssgiSupport = 0.25f;
        float baselineOwnership = ddgiOwnership + fallbackOwnership;
        float replacementWeight = ssgiSupport * baselineOwnership;
        float retainedBaseline = 1.0f - replacementWeight;
        float retainedDdgi = ddgiOwnership * retainedBaseline;
        float retainedFallback = fallbackOwnership * retainedBaseline;

        Assert.Multiple(() =>
        {
            Assert.That(retainedDdgi, Is.EqualTo(0.45f).Within(1e-6f));
            Assert.That(replacementWeight, Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(retainedFallback, Is.EqualTo(0.30f).Within(1e-6f));
            Assert.That(
                retainedDdgi + replacementWeight + retainedFallback,
                Is.EqualTo(1.0f).Within(1e-6f));
        });
    }

    [Test]
    public void ShaderAndPass_ExposeDocumentedStandaloneDiagnosticContracts()
    {
        string shader = ReadRepoText("Njulf.Shaders", "ssgi_composite.frag");
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string foliage = ReadRepoText("Njulf.Shaders", "foliage_forward.frag");
        string hitShading = ReadRepoText("Njulf.Shaders", "ddgi_hit_shading.glsl");
        string simpleTrace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SsgiCompositePass.cs");
        string forwardPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs");
        string targets = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "RenderTargetManager.cs");
        string graph = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ProductionRenderPipelineDeclaration.cs");

        Assert.Multiple(() =>
        {
            Assert.That(
                shader,
                Does.Contain("red=textured/SSGI-supported, green=compact/probe-owned,"));
            Assert.That(
                shader,
                Does.Contain("blue=far-field or environment-fallback-capable remainder."));
            Assert.That(
                shader,
                Does.Contain("red=retained DDGI, green=SSGI, blue=retained environment fallback."));
            Assert.That(
                shader,
                Does.Contain("mix(baselineIndirect, ssgiIndirect, replacementWeight)"));
            Assert.That(
                shader,
                Does.Contain("baselineIndirect + ssgiIndirect"));
            Assert.That(
                shader,
                Does.Contain("support > 0.0001"));
            Assert.That(
                shader,
                Does.Contain("Legacy additive overlap warning."));
            Assert.That(
                shader,
                Does.Contain("black=background, red=detailed mesh,"));
            Assert.That(
                shader,
                Does.Contain("green=compact primitive, blue=far field, magenta=unknown."));
            Assert.That(
                shader,
                Does.Contain("MaterialTransportHitProvenanceColor(uint sourcePath)"));
            Assert.That(
                shader,
                Does.Contain("pc.MaterialTransportProvenanceTextureIndex"));
            Assert.That(
                shader,
                Does.Contain("float encodedSourcePath = texelFetch("));
            Assert.That(
                shader,
                Does.Not.Contain("DetailedTransportHitCount"));
            Assert.That(
                shader,
                Does.Contain("!standaloneDiagnostic"));
            Assert.That(
                pass,
                Does.Contain("LoadOp = standaloneDiagnostic"));
            Assert.That(
                pass,
                Does.Contain("? AttachmentLoadOp.Clear"));
            Assert.That(
                pass,
                Does.Contain("GlobalIlluminationPassExecutionPolicy.ShouldCompositeSsgi("));
            Assert.That(
                pass,
                Does.Contain("if (transportProvenanceDiagnostic)"));
            Assert.That(
                pass,
                Does.Contain("_renderTargets.MaterialTransportProvenance.TransitionToShaderRead(cmd)"));
            Assert.That(
                pass,
                Does.Contain("BindlessIndex.MaterialTransportProvenanceTexture"));
            Assert.That(
                forward,
                Does.Contain("MATERIAL_TRANSPORT_PROVENANCE_DETAILED_MESH = 1u"));
            Assert.That(
                forward,
                Does.Contain("MATERIAL_TRANSPORT_PROVENANCE_COMPACT_PRIMITIVE = 2u"));
            Assert.That(
                forward,
                Does.Contain("MATERIAL_TRANSPORT_PROVENANCE_FAR_FIELD = 3u"));
            Assert.That(
                forward,
                Does.Contain("ResolveSimpleDdgiMaterialTransportProvenance("));
            Assert.That(
                forward,
                Does.Contain("WriteMaterialTransportProvenance(materialTransportProvenance)"));
            Assert.That(
                foliage,
                Does.Contain("WriteFoliageMaterialTransportProvenance("));
            Assert.That(
                forwardPass,
                Does.Contain("_renderTargets.MaterialTransportProvenance.TransitionToColorAttachment(cmd)"));
            Assert.That(
                forwardPass,
                Does.Contain("materialTransportProvenanceEnabled"));
            Assert.That(
                targets,
                Does.Contain("public const Format MaterialTransportProvenanceFormat = Format.R8Unorm"));
            Assert.That(
                targets,
                Does.Contain("materialTransportProvenanceEnabled ? extent : PlaceholderExtent"));
            Assert.That(
                graph,
                Does.Contain("WriteColorAttachment(RenderGraphResourceId.MaterialTransportProvenance)"));
            Assert.That(
                graph,
                Does.Contain("ReadFragmentSampled(RenderGraphResourceId.MaterialTransportProvenance)"));
            Assert.That(
                BindlessIndex.MaterialTransportProvenanceTexture,
                Is.EqualTo(BindlessIndex.WeightedOitRevealageTexture + 1));
            Assert.That(
                RenderTargetManager.MaterialTransportProvenanceFormat,
                Is.EqualTo(Silk.NET.Vulkan.Format.R8Unorm));
            Assert.That(
                hitShading,
                Does.Contain("RecordDdgiMaterialTransportProvenance("));
            Assert.That(
                hitShading,
                Does.Contain("MATERIAL_GI_DETAILED_TRANSPORT_HIT_COUNTER"));
            Assert.That(
                hitShading,
                Does.Contain("MATERIAL_GI_COMPACT_TRANSPORT_HIT_COUNTER"));
            Assert.That(
                hitShading,
                Does.Contain("MATERIAL_GI_CORRECTNESS_FALLBACK_HIT_COUNTER"));
            Assert.That(
                hitShading,
                Does.Contain("RecordDdgiEmissiveSamplingInvocation(worldPosition);"));
            Assert.That(
                simpleTrace,
                Does.Contain("MATERIAL_GI_FAR_FIELD_TRANSPORT_HIT_COUNTER"));
            Assert.That(
                common,
                Does.Contain("MATERIAL_GI_EMISSIVE_SAMPLING_INVOCATION_COUNTER = MATERIAL_GI_COUNTER_BASE + 9u"));
        });
    }

    [Test]
    public void MaterialTransportProvenance_IsDeclaredOnlyByFramebufferCompatibleShaderVariants()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string foliage = ReadRepoText("Njulf.Shaders", "foliage_forward.frag");
        string shaderProject = ReadRepoText("Njulf.Shaders", "Njulf.Shaders.csproj");
        string meshPipeline = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "MeshPipeline.cs");
        string foliagePipeline = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "FoliagePipeline.cs");

        Assert.Multiple(() =>
        {
            Assert.That(
                forward,
                Does.Contain("#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT"));
            Assert.That(
                forward,
                Does.Contain("#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT"));
            Assert.That(
                foliage,
                Does.Contain("#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT"));
            Assert.That(
                shaderProject,
                Does.Contain("-DNJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT=1"));
            Assert.That(
                shaderProject,
                Does.Contain("forward_opaque_simple_full_input_ddgi_provenance.frag"));
            Assert.That(
                shaderProject,
                Does.Contain("foliage_forward_ddgi_provenance.frag"));
            Assert.That(
                meshPipeline,
                Does.Contain("materialTransportProvenanceEnabled ? \"_provenance\" : string.Empty"));
            Assert.That(
                foliagePipeline,
                Does.Contain("materialTransportProvenanceEnabled ? \"_provenance\" : string.Empty"));
        });
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate =
                Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate repository file.",
            Path.Combine(pathParts));
    }
}
