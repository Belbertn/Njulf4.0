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
        "forward.frag",
        "ddgi_simple_trace.comp",
        "ddgi_simple_transport.comp",
        "ddgi_simple_blend.comp",
        "ddgi_simple_publish.comp",
        "ddgi_simple_publish_sampled.comp",
        "ddgi_simple_relocate_classify.comp",
        "farfield_voxelize.comp",
        "farfield_jumpflood.comp"
    ];

    [Test]
    public void SimpleDdgiShadersAreEmbeddedAsSpirv()
    {
        var assembly = typeof(ShaderLibrary).Assembly;
        byte[] magicBytes = new byte[4];

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
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_simple_blend.comp")), Is.True);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_trace.comp")), Is.False);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ddgi_schedule_score.comp")), Is.False);
            Assert.That(File.Exists(Path.Combine(shaderDirectory, "ssgi_trace.comp")), Is.False);
            Assert.That(common, Does.Not.Contain("DDGI_GATHER_TILE_BUFFER_INDEX"));
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
}
