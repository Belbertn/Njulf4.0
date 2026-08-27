using System.IO;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class NormalMapFilteringShaderContractTests
{
    [Test]
    public void ResolveNormal_UsesConservativeExplicitFootprint()
    {
        string forward = ReadRepoText("Njulf.Shaders", "forward.frag")
            .ReplaceLineEndings("\n");
        int resolveStart = forward.IndexOf(
            "vec3 ResolveNormal(",
            StringComparison.Ordinal);
        int resolveEnd = forward.IndexOf(
            "\n}\n",
            resolveStart,
            StringComparison.Ordinal);

        Assert.That(resolveStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(resolveEnd, Is.GreaterThan(resolveStart));

        string resolveNormal = forward[resolveStart..resolveEnd];
        Assert.Multiple(() =>
        {
            Assert.That(
                resolveNormal,
                Does.Contain("SampleMaterialTextureFootprint("));
            Assert.That(
                resolveNormal,
                Does.Contain("material.NormalTextureIndex,\n        uv,\n        4.0)"));
            Assert.That(
                resolveNormal,
                Does.Not.Contain(
                    "SampleMaterialTexture(material.NormalTextureIndex"));
            Assert.That(forward, Does.Contain("dFdx(uv) * scale"));
            Assert.That(forward, Does.Contain("dFdy(uv) * scale"));
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
