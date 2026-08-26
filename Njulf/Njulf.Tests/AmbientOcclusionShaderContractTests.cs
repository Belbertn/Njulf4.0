using System;
using System.IO;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmbientOcclusionShaderContractTests
{
    [Test]
    public void ForwardAo_ConsumesOneFullResolutionResolvedSample()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string renderingDirectory = FindRepoDirectory("Njulf.Rendering");
        string forward = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "forward.frag"));
        string targets = File.ReadAllText(Path.Combine(
            renderingDirectory,
            "Resources",
            "RenderTargetManager.cs"));
        string declaration = File.ReadAllText(Path.Combine(
            renderingDirectory,
            "Pipeline",
            "ProductionRenderPipelineDeclaration.cs"));
        string aoPass = File.ReadAllText(Path.Combine(
            renderingDirectory,
            "Pipeline",
            "AmbientOcclusionPass.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(forward, Does.Not.Contain(
                "SampleScreenSpaceAoDepthAware"));
            Assert.That(forward, Does.Contain(
                "return SampleScreenSpaceAoDirect();"));
            Assert.That(targets, Does.Contain(
                "ambientOcclusionEnabled ? extent : PlaceholderExtent"));
            Assert.That(targets, Does.Contain(
                "Extent2D resolvedExtent = enabled ? sceneExtent : PlaceholderExtent;"));
            Assert.That(declaration, Does.Match(
                @"OwnedImageResource\(\s*RenderGraphResourceId\.AmbientOcclusionBlurred,\s*""Ambient occlusion blurred"",\s*RenderTargetManager\.AmbientOcclusionFormat,\s*RenderGraphResourceSizePolicy\.SceneResolution\)"));
            Assert.That(aoPass, Does.Contain(
                "return AmbientOcclusionForwardSamplingMode.Direct;"));
            Assert.That(aoPass, Does.Contain(
                "sceneData.AmbientOcclusionForwardDepthAwareSamples = 0;"));
        });
    }

    [Test]
    public void ReconstructNormal_ReusesValidatedCenterDepthAndPosition()
    {
        string source = File.ReadAllText(Path.Combine(
                FindRepoDirectory("Njulf.Shaders"),
                "ambient_occlusion.comp"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int normalStart = source.IndexOf(
            "vec3 ReconstructNormal(",
            StringComparison.Ordinal);
        int normalEnd = source.IndexOf(
            "vec2 SampleJitter(",
            Math.Max(normalStart, 0),
            StringComparison.Ordinal);
        Assert.That(normalStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(normalEnd, Is.GreaterThan(normalStart));
        string normalBody = source[normalStart..normalEnd];

        Assert.Multiple(() =>
        {
            Assert.That(normalBody, Does.Contain("float centerDepth,"));
            Assert.That(normalBody, Does.Contain("vec3 center)"));
            Assert.That(normalBody, Does.Not.Contain("FetchDepth(uv)"),
                "The validated center texel must not be fetched a second time.");
            Assert.That(normalBody, Does.Not.Contain(
                    "ReconstructViewPosition(uv, centerDepth)"),
                "The validated center position must not be reconstructed a second time.");
            Assert.That(source, Does.Contain(
                "ReconstructNormal(\n        uv,\n        max(invSourceSize, invDestinationSize),\n        depth,\n        viewPosition);"));
            Assert.That(
                source.Split("float depth = FetchDepth(uv);", StringSplitOptions.None)
                    .Length - 1,
                Is.EqualTo(1));
            Assert.That(
                source.Split(
                    "vec3 viewPosition = ReconstructViewPosition(uv, depth);",
                    StringSplitOptions.None).Length - 1,
                Is.EqualTo(1));
        });
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