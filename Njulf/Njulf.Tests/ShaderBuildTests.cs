using System.Buffers.Binary;
using Njulf.Shaders;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ShaderBuildTests
{
    private static readonly string[] RequiredShaders =
    [
        "depth.task",
        "depth.mesh",
        "forward.task",
        "forward.mesh",
        "forward.frag",
        "lightcull.comp",
        "hiz_downsample.comp",
        "bloom_extract.comp",
        "bloom_downsample.comp",
        "bloom_upsample.comp",
        "tonemap_composite.frag"
    ];

    [Test]
    public void RequiredShadersAreEmbeddedAsSpirv()
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

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
            Assert.That(magic, Is.EqualTo(0x07230203), $"Shader resource '{resourceName}' is not SPIR-V bytecode.");
        }
    }
}
