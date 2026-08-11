using System.Buffers.Binary;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Shaders;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiTraceVariantTests
{
    [Test]
    public void Selector_UsesOpaqueSingleSunOnlyForExactImmutableFacts()
    {
        var baseline = new SimpleDdgiTraceContentFacts(
            SimpleDdgiStoragePackingMode.Packed,
            DetailedDiagnosticsCompiled: false,
            HasAlphaCandidateGeometry: false,
            HasThinTransmissionGeometry: false,
            DirectionalLightCount: 1,
            LocalLightCount: 0,
            EmissiveSourceCount: 0,
            CompleteRayScene: true,
            FarFieldCoverageReady: true);

        SimpleDdgiTraceVariantSelection selection =
            SimpleDdgiTraceVariantSelector.Select(baseline);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Specialized, Is.True);
            Assert.That(
                selection.ContentProfile,
                Is.EqualTo(SimpleDdgiTraceContentProfile.OpaqueSingleSun));
            Assert.That(
                selection.DistanceProfile,
                Is.EqualTo(SimpleDdgiTraceDistanceProfile.CompleteRayScene));
            Assert.That(
                SimpleDdgiTraceVariantSelector.ResolveShaderStem(selection),
                Is.EqualTo("packed_opaque_sun_complete"));
        });
    }

    [Test]
    public void Selector_PreservesCandidateAndFarFieldSemantics()
    {
        var candidateFacts = new SimpleDdgiTraceContentFacts(
            SimpleDdgiStoragePackingMode.Packed,
            DetailedDiagnosticsCompiled: false,
            HasAlphaCandidateGeometry: true,
            HasThinTransmissionGeometry: false,
            DirectionalLightCount: 1,
            LocalLightCount: 0,
            EmissiveSourceCount: 0,
            CompleteRayScene: false,
            FarFieldCoverageReady: true);
        SimpleDdgiTraceVariantSelection candidate =
            SimpleDdgiTraceVariantSelector.Select(candidateFacts);
        SimpleDdgiTraceVariantSelection opaque =
            SimpleDdgiTraceVariantSelector.Select(candidateFacts with
            {
                HasAlphaCandidateGeometry = false,
                LocalLightCount = 3,
                EmissiveSourceCount = 4
            });

        Assert.Multiple(() =>
        {
            Assert.That(
                candidate.ContentProfile,
                Is.EqualTo(SimpleDdgiTraceContentProfile.General));
            Assert.That(
                candidate.DistanceProfile,
                Is.EqualTo(SimpleDdgiTraceDistanceProfile.SplitFarField));
            Assert.That(
                SimpleDdgiTraceVariantSelector.ResolveShaderStem(candidate),
                Is.EqualTo("packed_general_split"));
            Assert.That(
                opaque.ContentProfile,
                Is.EqualTo(SimpleDdgiTraceContentProfile.Opaque));
            Assert.That(
                SimpleDdgiTraceVariantSelector.ResolveShaderStem(opaque),
                Is.EqualTo("packed_opaque_split"));
        });
    }

    [Test]
    public void Selector_FailsClosedForValidationDiagnosticsAndUnmeasuredWorkgroups()
    {
        var facts = new SimpleDdgiTraceContentFacts(
            SimpleDdgiStoragePackingMode.Validate,
            DetailedDiagnosticsCompiled: false,
            HasAlphaCandidateGeometry: false,
            HasThinTransmissionGeometry: false,
            DirectionalLightCount: 1,
            LocalLightCount: 0,
            EmissiveSourceCount: 0,
            CompleteRayScene: true,
            FarFieldCoverageReady: false);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiTraceVariantSelector.Select(facts).Specialized,
                Is.False);
            Assert.That(
                SimpleDdgiTraceVariantSelector.Select(facts with
                {
                    StoragePackingMode = SimpleDdgiStoragePackingMode.Packed,
                    DetailedDiagnosticsCompiled = true
                }).Specialized,
                Is.False);
            Assert.That(
                SimpleDdgiTraceVariantSelector.Select(facts with
                {
                    StoragePackingMode = SimpleDdgiStoragePackingMode.Packed
                }, measuredWorkgroupSize: 32).Specialized,
                Is.False);
        });
    }

    [Test]
    public void ShaderFastPath_TerminatesOnlyTheOpaqueBinaryVisibilityVariant()
    {
        string hitShading = ReadRepoText("Njulf.Shaders", "ddgi_hit_shading.glsl");
        string trace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
        string project = ReadRepoText("Njulf.Shaders", "Njulf.Shaders.csproj");

        Assert.Multiple(() =>
        {
            Assert.That(hitShading, Does.Contain("gl_RayFlagsTerminateOnFirstHitEXT"));
            Assert.That(hitShading, Does.Contain("DDGI_HIT_BINARY_OPAQUE_SHADOW_FAST_PATH"));
            Assert.That(hitShading, Does.Contain("ResolveDdgiThinCandidateTransmittance"));
            Assert.That(trace, Does.Contain("SIMPLE_DDGI_TRACE_OPAQUE_ONLY"));
            Assert.That(trace, Does.Contain("SIMPLE_DDGI_TRACE_FAR_FIELD_MODE"));
            Assert.That(project, Does.Contain("ddgi_simple_trace_packed_general_split_source.comp"));
            Assert.That(project, Does.Contain("ddgi_simple_trace_packed_opaque_sun_complete_final.comp"));
        });
    }

    [Test]
    public void DirectionalGuiding_ResolvesOnlyDedicatedShaderVariants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiTracePass.ResolveDirectionalGuidingShaderName(
                    SimpleDdgiStoragePackingMode.Legacy,
                    dispatchIndex: 0),
                Is.EqualTo("ddgi_simple_trace_legacy_guided_reuse.comp.spv"));
            Assert.That(
                SimpleDdgiTracePass.ResolveDirectionalGuidingShaderName(
                    SimpleDdgiStoragePackingMode.Validate,
                    dispatchIndex: 1),
                Is.EqualTo("ddgi_simple_trace_validate_guided_source.comp.spv"));
            Assert.That(
                SimpleDdgiTracePass.ResolveDirectionalGuidingShaderName(
                    SimpleDdgiStoragePackingMode.Packed,
                    dispatchIndex: 2),
                Is.EqualTo("ddgi_simple_trace_packed_guided_final.comp.spv"));

            Assert.That(
                SimpleDdgiTransportPass.ResolveTransportShaderName(
                    SimpleDdgiStoragePackingMode.Legacy,
                    directionalGuidingTransport: false),
                Is.EqualTo("ddgi_simple_transport_legacy.comp.spv"));
            Assert.That(
                SimpleDdgiTransportPass.ResolveTransportShaderName(
                    SimpleDdgiStoragePackingMode.Validate,
                    directionalGuidingTransport: true),
                Is.EqualTo("ddgi_simple_transport_guided_validate.comp.spv"));
            Assert.That(
                SimpleDdgiTransportPass.ResolveTransportShaderName(
                    SimpleDdgiStoragePackingMode.Packed,
                    directionalGuidingTransport: true),
                Is.EqualTo("ddgi_simple_transport_guided_packed.comp.spv"));

            Assert.That(
                SimpleDdgiAcceleratedSolvePass.ResolveTransportShaderName(
                    SimpleDdgiStoragePackingMode.Legacy,
                    directionalGuidingTransport: false),
                Is.EqualTo("ddgi_simple_transport_solve_legacy.comp.spv"));
            Assert.That(
                SimpleDdgiAcceleratedSolvePass.ResolveTransportShaderName(
                    SimpleDdgiStoragePackingMode.Validate,
                    directionalGuidingTransport: true),
                Is.EqualTo(
                    "ddgi_simple_transport_solve_guided_validate.comp.spv"));
            Assert.That(
                SimpleDdgiAcceleratedSolvePass.ResolveTransportShaderName(
                    SimpleDdgiStoragePackingMode.Packed,
                    directionalGuidingTransport: true),
                Is.EqualTo(
                    "ddgi_simple_transport_solve_guided_packed.comp.spv"));

            Assert.That(
                SimpleDdgiBlendPass.ResolveDirectionalGuidingShaderName(false),
                Is.EqualTo("ddgi_simple_blend.comp.spv"));
            Assert.That(
                SimpleDdgiBlendPass.ResolveDirectionalGuidingShaderName(true),
                Is.EqualTo("ddgi_simple_blend_guided.comp.spv"));
            Assert.That(
                SimpleDdgiRelocateClassifyPass
                    .ResolveDirectionalGuidingShaderName(false),
                Is.EqualTo("ddgi_simple_relocate_classify.comp.spv"));
            Assert.That(
                SimpleDdgiRelocateClassifyPass
                    .ResolveDirectionalGuidingShaderName(true),
                Is.EqualTo(
                    "ddgi_simple_relocate_classify_guided.comp.spv"));
            Assert.That(
                SimpleDdgiDirectionalRadiancePass.ResolveProjectShaderName(false),
                Is.EqualTo("ddgi_simple_directional_project.comp.spv"));
            Assert.That(
                SimpleDdgiDirectionalRadiancePass.ResolveProjectShaderName(true),
                Is.EqualTo(
                    "ddgi_simple_directional_project_guided.comp.spv"));
        });
    }

    [Test]
    public void DirectionalGuiding_BaselineShaderSourcesExcludeSidecarAbi()
    {
        string shared = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_shared.glsl");
        string project = ReadRepoText(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");
        string[] consumers =
        [
            "ddgi_simple_trace.comp",
            "ddgi_simple_transport.comp",
            "ddgi_simple_blend.comp",
            "ddgi_simple_relocate_classify.comp",
            "ddgi_simple_directional_project.comp"
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                shared,
                Does.Contain(
                    "#define SIMPLE_DDGI_DIRECTIONAL_GUIDING_TRANSPORT 0"));
            Assert.That(
                shared,
                Does.Contain("#include \"ddgi_guiding_transport.glsl\""));
            foreach (string consumer in consumers)
            {
                string source = ReadRepoText("Njulf.Shaders", consumer);
                Assert.That(
                    source,
                    Does.Not.Contain(
                        "#define SIMPLE_DDGI_DIRECTIONAL_GUIDING_TRANSPORT 1"),
                    consumer);
                Assert.That(
                    source,
                    Does.Contain(
                        "#if SIMPLE_DDGI_DIRECTIONAL_GUIDING_TRANSPORT"),
                    consumer);
            }

            foreach (string artifact in new[]
                     {
                         "ddgi_simple_blend_guided.comp",
                         "ddgi_simple_relocate_classify_guided.comp",
                         "ddgi_simple_directional_project_guided.comp",
                         "ddgi_simple_transport_guided_legacy.comp",
                         "ddgi_simple_transport_guided_validate.comp",
                         "ddgi_simple_transport_guided_packed.comp",
                         "ddgi_simple_transport_solve_guided_legacy.comp",
                         "ddgi_simple_transport_solve_guided_validate.comp",
                         "ddgi_simple_transport_solve_guided_packed.comp"
                     })
            {
                Assert.That(project, Does.Contain(artifact), artifact);
            }
        });
    }

    [Test]
    public void DirectionalGuiding_EmbeddedBaselineSpirvHasNoSidecarLengthReads()
    {
        const ushort opArrayLength = 68;
        (string Baseline, string Guided)[] shaderPairs =
        [
            ("ddgi_simple_blend.comp", "ddgi_simple_blend_guided.comp"),
            (
                "ddgi_simple_relocate_classify.comp",
                "ddgi_simple_relocate_classify_guided.comp"),
            (
                "ddgi_simple_directional_project.comp",
                "ddgi_simple_directional_project_guided.comp"),
            (
                "ddgi_simple_transport_packed.comp",
                "ddgi_simple_transport_guided_packed.comp"),
            (
                "ddgi_simple_trace_packed_reuse.comp",
                "ddgi_simple_trace_packed_guided_reuse.comp")
        ];

        Assert.Multiple(() =>
        {
            foreach ((string baseline, string guided) in shaderPairs)
            {
                Assert.That(
                    CountSpirvOpcode(baseline, opArrayLength),
                    Is.Zero,
                    $"Baseline shader '{baseline}' retained a runtime-array " +
                    "length read from the C3 sidecar ABI.");
                Assert.That(
                    CountSpirvOpcode(guided, opArrayLength),
                    Is.GreaterThan(0),
                    $"Guided shader '{guided}' no longer validates its " +
                    "source-cache sidecar bounds.");
            }
        });
    }

    private static int CountSpirvOpcode(string shaderName, ushort opcode)
    {
        const uint spirvMagic = 0x0723_0203u;
        string resourceName = $"Njulf.Shaders.{shaderName}";
        using Stream stream = typeof(ShaderLibrary).Assembly
            .GetManifestResourceStream(resourceName) ??
            throw new AssertionException(
                $"Missing shader resource '{resourceName}'.");
        using var memory = new MemoryStream(
            checked((int)stream.Length));
        stream.CopyTo(memory);
        ReadOnlySpan<byte> bytes = memory.GetBuffer().AsSpan(
            0,
            checked((int)memory.Length));
        if (bytes.Length < 5 * sizeof(uint) ||
            (bytes.Length & (sizeof(uint) - 1)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != spirvMagic)
        {
            throw new AssertionException(
                $"Shader resource '{resourceName}' is not valid SPIR-V word data.");
        }

        int count = 0;
        int wordOffset = 5;
        int wordLength = bytes.Length / sizeof(uint);
        while (wordOffset < wordLength)
        {
            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(wordOffset * sizeof(uint), sizeof(uint)));
            int instructionWords = checked((int)(instruction >> 16));
            if (instructionWords <= 0 ||
                instructionWords > wordLength - wordOffset)
            {
                throw new AssertionException(
                    $"Shader resource '{resourceName}' contains a malformed " +
                    $"SPIR-V instruction at word {wordOffset}.");
            }
            if ((ushort)instruction == opcode)
                count++;
            wordOffset += instructionWords;
        }
        return count;
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(pathParts));
    }
}
