using System.Buffers.Binary;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Shaders;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ShaderBuildTests
{
    private static readonly string[] RequiredShaders =
    [
        "ambient_occlusion.comp",
        "variable_rate_shading.comp",
        "gtao.comp",
        "gtao_temporal.comp",
        "gtao_spatial.comp",
        "hybrid_reflection_ssr.comp",
        "hybrid_reflection_ray_query.comp",
        "hybrid_reflection_ddgi_base.comp",
        "hybrid_reflection_resolve.comp",
        "hybrid_reflection_temporal.comp",
        "hybrid_reflection_spatial.comp",
        "hybrid_reflection_composite.comp",
        "automatic_planar_reproject.comp",
        "automatic_planar_prefilter.comp",
        "foliage_authored_expand.comp",
        "opaque_scene_color_snapshot.comp",
        "area_ray_shadow.comp",
        "forward.frag",
        "forward_opaque_ddgi_c4_receiver_cache_required.frag",
        "forward_opaque_simple_ddgi_c4_receiver_cache_required.frag",
        "forward_opaque_simple_full_input_ddgi_c4_receiver_cache_required.frag",
        "forward_opaque_ddgi_c4_c5_cache_required.frag",
        "forward_opaque_simple_ddgi_c4_c5_cache_required.frag",
        "forward_opaque_simple_full_input_ddgi_c4_c5_cache_required.frag",
        "geometry_decal.frag",
        "forward_transparent_thin_glass.frag",
        "forward_transparent_thin_glass_ddgi_b1.frag",
        "ddgi_near_field_residual_finalize.comp",
        "ddgi_simple_trace.comp",
        "ddgi_simple_transport.comp",
        "ddgi_simple_directional_prepare.comp",
        "ddgi_simple_directional_project.comp",
        "ddgi_simple_directional_publish.comp",
        "ddgi_simple_blend.comp",
        "ddgi_simple_publish.comp",
        "ddgi_simple_publish_sampled.comp",
        "ddgi_simple_relocate_classify.comp",
        "ddgi_simple_receiver_cache.comp",
        "ddgi_simple_receiver_cache_b1.comp",
        "ddgi_simple_receiver_cache_classify.comp",
        "ddgi_simple_receiver_cache_adaptive.comp",
        "ddgi_simple_receiver_cache_resolve_adaptive.comp",
        "ddgi_simple_schedule_admit_tail.comp",
        "ddgi_simple_schedule_feedback_partial.comp",
        "ddgi_simple_schedule_materialize.comp",
        "farfield_voxelize.comp",
        "farfield_jumpflood.comp",
        "froxel_noise.comp",
        "froxel_source_cull.comp",
        "froxel_medium.comp",
        "froxel_transmittance.comp",
        "froxel_ddgi_bounce_l2.comp",
        "froxel_lighting.comp",
        "froxel_indirect.comp",
        "froxel_multiple_scatter.comp",
        "froxel_temporal.comp",
        "froxel_integrate.comp",
        "froxel_resolve.comp",
        "froxel_composite.comp",
        "motion_vector_alpha.mesh",
        "motion_vector_alpha.frag",
        "motion_vector_compacted.mesh",
        "motion_vector_compacted_48v64p_128t.mesh",
        "motion_vector_compacted_64v126p_64t.mesh",
        "motion_vector_compacted_64v126p_128t.mesh",
        "motion_vector_alpha_compacted.mesh",
        "foliage_motion_compacted.mesh",
        "depth_compacted.mesh",
        "depth_alpha_compacted.mesh",
        "shadow_depth_alpha_compacted.mesh",
        "forward_compacted.mesh",
        "forward_compacted_48v64p_128t.mesh",
        "forward_compacted_64v126p_64t.mesh",
        "forward_compacted_64v126p_128t.mesh"
    ];

    [Test]
    public void SimpleDdgiShadersAreEmbeddedAsSpirv()
    {
        var assembly = typeof(ShaderLibrary).Assembly;
        string[] shaderResourceNames = assembly.GetManifestResourceNames()
            .Where(name =>
                name.StartsWith("Njulf.Shaders.", StringComparison.Ordinal) &&
                (name.EndsWith(".comp", StringComparison.Ordinal) ||
                 name.EndsWith(".frag", StringComparison.Ordinal) ||
                 name.EndsWith(".vert", StringComparison.Ordinal) ||
                 name.EndsWith(".mesh", StringComparison.Ordinal) ||
                 name.EndsWith(".task", StringComparison.Ordinal)))
            .ToArray();
        byte[] magicBytes = new byte[4];

        Assert.That(shaderResourceNames, Has.Length.EqualTo(399));

        foreach (string shaderName in RequiredShaders)
        {
            string resourceName = $"Njulf.Shaders.{shaderName}";
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);

            Assert.That(stream, Is.Not.Null, $"Missing shader resource '{resourceName}'.");
            Assert.That(stream!.Length, Is.GreaterThanOrEqualTo(4), $"Shader resource '{resourceName}' is empty.");
            Assert.That(stream.Read(magicBytes), Is.EqualTo(4), $"Could not read SPIR-V magic from '{resourceName}'.");
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(magicBytes), Is.EqualTo(0x07230203));
        }
    }

    [Test]
    public void ReceiverCacheVariantsEmbedWorkgroupStorageAndControlBarrier()
    {
        string[] receiverCacheVariants =
        [
            "ddgi_simple_receiver_cache.comp",
            "ddgi_simple_receiver_cache_b1.comp",
            "ddgi_simple_receiver_cache_adaptive.comp"
        ];

        foreach (string shaderName in receiverCacheVariants)
        {
            string resourceName = $"Njulf.Shaders.{shaderName}";
            using Stream stream = typeof(ShaderLibrary).Assembly
                .GetManifestResourceStream(resourceName)
                ?? throw new AssertionException(
                    $"Missing shader resource '{resourceName}'.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] spirv = memory.ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(
                    ContainsSpirvOpcode(spirv, 224),
                    Is.True,
                    $"'{resourceName}' does not contain OpControlBarrier.");
                Assert.That(
                    ContainsSpirvWorkgroupVariable(spirv),
                    Is.True,
                    $"'{resourceName}' does not contain a Workgroup OpVariable.");
            });
        }
    }

    [Test]
    public void ShaderSourcesContainOnlyTheSimpleDdgiPipeline()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string project = File.ReadAllText(Path.Combine(shaderDirectory, "Njulf.Shaders.csproj"));
        string common = File.ReadAllText(Path.Combine(shaderDirectory, "common.glsl"));

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Contain("*.comp"));
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_simple_trace.comp")), Is.True);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_simple_transport.comp")), Is.True);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_simple_directional_prepare.comp")), Is.True);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_simple_directional_project.comp")), Is.True);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_simple_directional_publish.comp")), Is.True);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_simple_blend.comp")), Is.True);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_trace.comp")), Is.False);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_schedule_score.comp")), Is.False);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ssgi_trace.comp")), Is.False);
            Assert.That(common, Does.Not.Contain("DDGI_GATHER_TILE_BUFFER_INDEX"));
        });
    }

    [Test]
    public void ShaderProject_InvalidatesIdeBuildWhenShaderInputsChange()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string project = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "Njulf.Shaders.csproj"));

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Contain("<UpToDateCheckInput Include="));
            Assert.That(project,
                Does.Contain("$(MSBuildProjectDirectory)\\*.comp"));
            Assert.That(project,
                Does.Contain("$(MSBuildProjectDirectory)\\*.glsl"));
            Assert.That(project,
                Does.Contain("$(NjulfShaderBuildTaskSource)"));
        });
    }

    [Test]
    public void ProductionVerificationUsesBoundedNativeScansAndFileBackedDisassembly()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string atomicVerification = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "VerifyProductionDiagnosticAtomics.ps1"));
        string receiverVerification = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "VerifySimpleDdgiReceiverContract.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(atomicVerification,
                Does.Contain("SpirvInstructionInspector]::CountOpcode"));
            Assert.That(atomicVerification,
                Does.Not.Contain("for ($byteOffset = 20;"),
                "Production builds must not interpret every SPIR-V instruction in PowerShell.");
            Assert.That(receiverVerification,
                Does.Contain("& $spirvDis --no-color -o $temporaryPath $ModulePath"));
            Assert.That(receiverVerification,
                Does.Contain("[IO.File]::ReadAllText($temporaryPath)"));
            Assert.That(receiverVerification,
                Does.Not.Contain("(& $spirvDis $modulePath 2>&1) -join"),
                "Multi-megabyte disassemblies must not be line-materialized through the PowerShell pipeline.");
        });
    }

    [Test]
    public void ShaderModuleLoaderUsesTheBuildPinnedResource()
    {
        const string shaderFileName = "ddgi_simple_trace.comp.spv";
        const string resourceName = "Njulf.Shaders.ddgi_simple_trace.comp";
        byte[] actual = ShaderModuleLoader.LoadBytes(shaderFileName);
        using Stream stream = typeof(ShaderLibrary).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new AssertionException($"Missing shader resource '{resourceName}'.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        Assert.That(actual, Is.EqualTo(memory.ToArray()));
    }

    [Test]
    public void ShaderModuleLoaderRejectsEmptyAndOversizedInput()
    {
        using var empty = new MemoryStream();
        Assert.That(
            () => ShaderModuleLoader.ReadBoundedSnapshot(empty, "empty shader"),
            Throws.TypeOf<InvalidDataException>());

        using var oversized = new MemoryStream(
            new byte[ShaderModuleLoader.MaximumShaderModuleBytes + 1],
            writable: false);
        Assert.That(
            () => ShaderModuleLoader.ReadBoundedSnapshot(oversized, "oversized shader"),
            Throws.TypeOf<InvalidDataException>());
    }

    private static string FindRepoDirectory(string name)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new AssertionException($"Could not find repo directory '{name}'.");
    }

    private static bool ContainsSpirvOpcode(byte[] spirv, ushort expectedOpcode)
    {
        foreach ((ushort Opcode, int WordOffset, int WordCount) instruction in
                 EnumerateSpirvInstructions(spirv))
        {
            if (instruction.Opcode == expectedOpcode)
                return true;
        }

        return false;
    }

    private static bool ContainsSpirvWorkgroupVariable(byte[] spirv)
    {
        const ushort opVariable = 59;
        const uint storageClassWorkgroup = 4;
        foreach ((ushort opcode, int wordOffset, int wordCount) in
                 EnumerateSpirvInstructions(spirv))
        {
            if (opcode == opVariable &&
                wordCount >= 4 &&
                ReadSpirvWord(spirv, wordOffset + 3) == storageClassWorkgroup)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Opcode, int WordOffset, int WordCount)>
        EnumerateSpirvInstructions(byte[] spirv)
    {
        if (spirv.Length < 20 || spirv.Length % sizeof(uint) != 0 ||
            ReadSpirvWord(spirv, 0) != 0x07230203u)
        {
            throw new AssertionException("The embedded resource is not valid SPIR-V word data.");
        }

        int totalWords = spirv.Length / sizeof(uint);
        for (int wordOffset = 5; wordOffset < totalWords;)
        {
            uint instruction = ReadSpirvWord(spirv, wordOffset);
            int wordCount = checked((int)(instruction >> 16));
            ushort opcode = checked((ushort)(instruction & 0xffffu));
            if (wordCount <= 0 || wordOffset + wordCount > totalWords)
            {
                throw new AssertionException(
                    $"Invalid SPIR-V instruction at word {wordOffset}.");
            }

            yield return (opcode, wordOffset, wordCount);
            wordOffset += wordCount;
        }
    }

    private static uint ReadSpirvWord(byte[] spirv, int wordOffset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            spirv.AsSpan(checked(wordOffset * sizeof(uint)), sizeof(uint)));
}
