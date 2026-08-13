using System;
using System.IO;
using System.Text.RegularExpressions;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ForwardNearFieldDirectSourceContractTests
{
    [Test]
    public void PipelineConfiguration_IsDefaultOffAndRequiresTheExactReferenceSource()
    {
        ForwardNearFieldDirectSourcePipelineConfiguration disabled = default;
        ForwardNearFieldDirectSourcePipelineConfiguration valid =
            CreateValidConfiguration();
        ForwardNearFieldDirectSourcePipelineConfiguration wrongShaderVersion =
            valid with { ShaderSemanticVersion = 0u };
        ForwardNearFieldDirectSourcePipelineConfiguration packedSource =
            valid with
            {
                TraceSourceContract = valid.TraceSourceContract with
                {
                    Format = SimpleDdgiNearFieldResidualFormat.B10G11R11UfloatPack32
                }
            };

        Assert.Multiple(() =>
        {
            Assert.That(
                ForwardNearFieldDirectSourceContract.TryValidatePipelineConfiguration(
                    disabled,
                    out string disabledFailure),
                Is.False);
            Assert.That(disabledFailure,
                Is.EqualTo("near-field-direct-source-disabled"));
            Assert.That(
                ForwardNearFieldDirectSourcePipelineConfiguration.Disabled,
                Is.EqualTo(disabled));
            Assert.That(
                ForwardNearFieldDirectSourceContract.TryValidatePipelineConfiguration(
                    valid,
                    out string validFailure),
                Is.True);
            Assert.That(validFailure, Is.EqualTo("valid"));
            Assert.That(
                ForwardNearFieldDirectSourceContract.TryValidatePipelineConfiguration(
                    wrongShaderVersion,
                    out string versionFailure),
                Is.False);
            Assert.That(versionFailure,
                Is.EqualTo(
                    "near-field-direct-source-shader-semantics-version-mismatch"));
            Assert.That(
                ForwardNearFieldDirectSourceContract.TryValidatePipelineConfiguration(
                    packedSource,
                    out string formatFailure),
                Is.False);
            Assert.That(formatFailure,
                Is.EqualTo("near-field-direct-source-r16g16b16a16-sfloat-required"));
        });
    }

    [Test]
    public void DedicatedOpaqueVariantsPreserveSourceOwnershipAndFailClosedBoundaries()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string shaderProject = ReadRepoText("Njulf.Shaders", "Njulf.Shaders.csproj");
        string meshPipeline = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "PipelineObjects", "MeshPipeline.cs");
        string forwardPass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");
        string normalizedForward = Regex.Replace(forward, @"\s+", " ");
        const string sourceWrite =
            "outDirectDiffuseAndEmissive = vec4( clamp(directDiffuseSource + emissive, vec3(0.0), vec3(C5_MAXIMUM_FINITE_FP16)), c5ReceiverPayloadValid ? 1.0 : 0.0);";
        int sourceWriteIndex = normalizedForward.IndexOf(
            sourceWrite,
            StringComparison.Ordinal);
        int finalColorIndex = normalizedForward.IndexOf(
            "vec3 color = finalDiffuseIndirect + specularIbl + directLighting + emissive;",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(forward,
                Does.Contain("layout(location = 1) out vec4 outDirectDiffuseAndEmissive;"));
            Assert.That(forward,
                Does.Contain("layout(location = 2) out uvec4 outNearFieldReceiverPayload;"));
            Assert.That(forward,
                Does.Not.Contain("outNearFieldReceiverProjectedRay"));
            Assert.That(forward,
                Does.Contain("outDirectDiffuseAndEmissive = vec4(0.0);"));
            Assert.That(sourceWriteIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(finalColorIndex, Is.GreaterThan(sourceWriteIndex));
            Assert.That(forward,
                Does.Contain("C5 direct source is valid only for opaque or alpha-mask forward variants."));
            Assert.That(forward,
                Does.Contain("C5 direct source cannot share the forward MRT variant with material provenance."));
            Assert.That(forward,
                Does.Not.Contain("outDirectDiffuseAndEmissive = vec4(color"));

            foreach (string artifact in new[]
                     {
                         ForwardNearFieldDirectSourceContract.OpaqueFragmentShader,
                         ForwardNearFieldDirectSourceContract.SimpleOpaqueFragmentShader,
                         ForwardNearFieldDirectSourceContract
                             .SimpleFullInputOpaqueFragmentShader
                     })
            {
                string itemName = artifact[..^".spv".Length];
                Assert.That(shaderProject, Does.Contain(itemName));
            }

            Assert.That(shaderProject,
                Does.Contain("-DNJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT=1"));
            Assert.That(shaderProject,
                Does.Contain("-DNJULF_C5_DIRECT_SOURCE_SEMANTICS_VERSION=3"));
            Assert.That(shaderProject,
                Does.Not.Contain("forward_weighted_oit_ddgi_near_field_direct_source"));
            Assert.That(meshPipeline,
                Does.Contain("TryResolveNearFieldDirectSourcePipeline"));
            Assert.That(meshPipeline,
                Does.Contain("CreateNearFieldDirectSourcePipelines"));
            Assert.That(meshPipeline,
                Does.Contain("DestroyNearFieldDirectSourcePipelines"));
            Assert.That(meshPipeline,
                Does.Contain("ForwardNearFieldDirectSourceContract.RequiredAttachmentFormat"));
            Assert.That(meshPipeline,
                Does.Contain("ForwardNearFieldDirectSourceContract.ReceiverPayloadFormat"));
            Assert.That(forwardPass,
                Does.Contain("ReceiverPayload.View"));
            foreach (string pipelineVariant in new[]
                     {
                         "_forwardNearFieldDirectSourcePipeline",
                         "_forwardCompactedNearFieldDirectSourcePipeline",
                         "_forwardSimpleNearFieldDirectSourcePipeline",
                         "_forwardSimpleFullInputNearFieldDirectSourcePipeline",
                         "_forwardCompactedSimpleNearFieldDirectSourcePipeline",
                         "_forwardCompactedSimpleFullInputNearFieldDirectSourcePipeline"
                     })
            {
                Assert.That(
                    Regex.Matches(meshPipeline, Regex.Escape(pipelineVariant)).Count,
                    Is.GreaterThanOrEqualTo(4),
                    $"C5 pipeline variant '{pipelineVariant}' must be created, selected, and retired.");
            }
            Assert.That(forwardPass,
                Does.Contain("TryValidateAttachmentBinding"));
            Assert.That(forwardPass,
                Does.Contain("DrawFoliageWithoutNearFieldDirectSource"));
            Assert.That(forwardPass,
                Does.Contain("near-field-direct-source-debug-view-active"));
        });
    }

    [Test]
    public void CombinedC4C5Variant_UsesFrozenFourAttachmentAbiAndEveryOpaqueFamily()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag");
        string shaderProject = ReadRepoText(
            "Njulf.Shaders", "Njulf.Shaders.csproj");
        string meshPipeline = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "PipelineObjects", "MeshPipeline.cs");
        string forwardPass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    nearFieldDirectSourceEnabled: true,
                    giCausticReceiverEnabled: true),
                Is.EqualTo(ForwardAdvancedGiCombinedContract.ColorAttachmentCount));
            Assert.That(
                () => ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    materialTransportProvenanceEnabled: true,
                    nearFieldDirectSourceEnabled: true,
                    giCausticReceiverEnabled: true),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(forward, Does.Contain(
                "#if NJULF_C4_RECEIVER_OUTPUT && NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT"));
            Assert.That(forward, Does.Contain(
                "layout(location = 1) out uvec4 outGiCausticReceiverPayload;"));
            Assert.That(forward, Does.Contain(
                "layout(location = 2) out vec4 outDirectDiffuseAndEmissive;"));
            Assert.That(forward, Does.Contain(
                "layout(location = 3) out uvec4 outNearFieldReceiverPayload;"));
            Assert.That(meshPipeline,
                Does.Contain("TryResolveCombinedAdvancedGiPipeline"));
            Assert.That(meshPipeline,
                Does.Contain("quaternaryColorFormat"));
            Assert.That(forwardPass,
                Does.Contain("TryResolveCombinedAdvancedGiPipeline"));
            Assert.That(forwardPass,
                Does.Contain("CombinedAdvancedGiAttachmentEnabled"));
            Assert.That(forwardPass,
                Does.Contain("CombinedAdvancedGiFailureReason"));
            Assert.That(forwardPass,
                Does.Contain("colorAttachments[3]"));

            foreach (string artifact in new[]
                     {
                         ForwardAdvancedGiCombinedContract.OpaqueFragmentShader,
                         ForwardAdvancedGiCombinedContract.SimpleOpaqueFragmentShader,
                         ForwardAdvancedGiCombinedContract
                             .SimpleFullInputOpaqueFragmentShader
                     })
            {
                Assert.That(shaderProject,
                    Does.Contain(artifact[..^".spv".Length]));
            }
        });
    }

    private static ForwardNearFieldDirectSourcePipelineConfiguration
        CreateValidConfiguration()
    {
        SimpleDdgiNearFieldResidualProfile profile =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference;
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                sourceWidth: 640,
                sourceHeight: 360,
                profile: profile,
                budgetBytes: 256UL * 1024UL * 1024UL);
        Assert.That(layout.IsValid, Is.True);
        SimpleDdgiNearFieldTraceSourceContract source =
            SimpleDdgiNearFieldTraceSourceContract
                .CreatePreDdgiDirectDiffuseAndEmissive(layout, profile);
        return new ForwardNearFieldDirectSourcePipelineConfiguration(
            IsC5EffectivelyEnabled: true,
            TraceSourceContract: source,
            ShaderSemanticVersion:
                ForwardNearFieldDirectSourceContract.ShaderSemanticVersion);
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
