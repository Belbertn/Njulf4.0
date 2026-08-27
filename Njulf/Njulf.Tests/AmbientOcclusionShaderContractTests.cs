using System;
using System.IO;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmbientOcclusionShaderContractTests
{
    private const float GoldenAngle = 2.39996323f;
    private const float GoldenRotationX = -0.7373688783f;
    private const float GoldenRotationY = 0.6754902941f;

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
        string source = ReadShaderSource();
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

    [Test]
    public void HemisphereSpiral_UsesOneBasisRotationAndNoPerTapTrigonometry()
    {
        string source = ReadShaderSource();
        int sampleStart = source.IndexOf(
            "vec3 HemisphereSample(",
            StringComparison.Ordinal);
        int basisStart = source.IndexOf(
            "mat3 BuildBasis(",
            Math.Max(sampleStart, 0),
            StringComparison.Ordinal);
        Assert.That(sampleStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(basisStart, Is.GreaterThan(sampleStart));
        string sampleFunction = source[sampleStart..basisStart];

        int loopStart = source.IndexOf(
            "for (uint i = 0u; i < 32u; i++)",
            basisStart,
            StringComparison.Ordinal);
        int loopEnd = source.IndexOf(
            "occlusion /= float(sampleCount);",
            Math.Max(loopStart, 0),
            StringComparison.Ordinal);
        Assert.That(loopStart, Is.GreaterThan(basisStart));
        Assert.That(loopEnd, Is.GreaterThan(loopStart));
        string loop = source[loopStart..loopEnd];

        int inactiveBreak = loop.IndexOf(
            "if (i >= sampleCount)",
            StringComparison.Ordinal);
        int sampleCall = loop.IndexOf(
            "basis * HemisphereSample(",
            StringComparison.Ordinal);
        int recurrence = loop.IndexOf(
            "spiralDirection = vec2(",
            Math.Max(sampleCall, 0),
            StringComparison.Ordinal);
        int firstPotentialContinue = loop.IndexOf(
            "continue;",
            Math.Max(sampleCall, 0),
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "const float AO_GOLDEN_ANGLE = 2.39996323;"));
            Assert.That(source, Does.Contain(
                "const vec2 AO_GOLDEN_ROTATION = vec2(-0.7373688783, 0.6754902941);"));
            Assert.That(sampleFunction, Does.Contain("vec2 spiralDirection"));
            Assert.That(sampleFunction, Does.Contain(
                "float sampleIndex = float(index) + radialJitter;"));
            Assert.That(sampleFunction, Does.Contain(
                "return vec3(spiralDirection * r, z);"));
            Assert.That(sampleFunction, Does.Not.Contain("theta"));
            Assert.That(sampleFunction, Does.Not.Contain("sin("));
            Assert.That(sampleFunction, Does.Not.Contain("cos("));
            Assert.That(source, Does.Contain(
                "float radialJitter = jitter.y;\n    float randomAngle = jitter.x * 2.0 * PI +\n        radialJitter * AO_GOLDEN_ANGLE;"));
            Assert.That(
                CountOccurrences(source, "radialJitter * AO_GOLDEN_ANGLE"),
                Is.EqualTo(1));
            Assert.That(source, Does.Contain(
                "vec2 spiralDirection = vec2(1.0, 0.0);"));
            Assert.That(
                CountOccurrences(source, "BuildBasis(normal, randomAngle)"),
                Is.EqualTo(1));
            Assert.That(loop, Does.Contain(
                "if (i >= sampleCount)\n            break;"));
            Assert.That(loop, Does.Contain(
                "spiralDirection.x * AO_GOLDEN_ROTATION.x -\n                spiralDirection.y * AO_GOLDEN_ROTATION.y"));
            Assert.That(loop, Does.Contain(
                "spiralDirection.x * AO_GOLDEN_ROTATION.y +\n                spiralDirection.y * AO_GOLDEN_ROTATION.x"));
            Assert.That(loop, Does.Contain(
                "float sampleScale = clamp((float(i) + radialJitter + 0.5)"));
            Assert.That(loop, Does.Not.Contain("sin("));
            Assert.That(loop, Does.Not.Contain("cos("));
            Assert.That(loop, Does.Not.Contain("normalize(spiralDirection)"));
            Assert.That(source, Does.Not.Contain("pc.FrameIndex"));
            Assert.That(inactiveBreak, Is.GreaterThanOrEqualTo(0));
            Assert.That(sampleCall, Is.GreaterThan(inactiveBreak));
            Assert.That(recurrence, Is.GreaterThan(sampleCall));
            Assert.That(
                CountOccurrences(loop, "spiralDirection = vec2("),
                Is.EqualTo(1));
            Assert.That(firstPotentialContinue, Is.GreaterThan(recurrence),
                "Every active tap must advance before any rejection can continue.");
        });
    }

    [Test]
    public void HemisphereSpiral_GoldenRotationConstantsAreConsistent()
    {
        float lengthSquared =
            GoldenRotationX * GoldenRotationX +
            GoldenRotationY * GoldenRotationY;

        Assert.Multiple(() =>
        {
            Assert.That(
                MathF.Abs(GoldenRotationX - MathF.Cos(GoldenAngle)),
                Is.LessThanOrEqualTo(1.0e-7f));
            Assert.That(
                MathF.Abs(GoldenRotationY - MathF.Sin(GoldenAngle)),
                Is.LessThanOrEqualTo(1.0e-7f));
            Assert.That(
                MathF.Abs(lengthSquared - 1.0f),
                Is.LessThanOrEqualTo(1.0e-6f));
        });
    }

    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    public void HemisphereSpiral_FloatRecurrencePreservesSampling(int sampleCount)
    {
        float[] radialJitters =
            [0.0f, 0.0000001f, 0.25f, 0.5f, 0.999999f];
        foreach (float radialJitter in radialJitters)
        {
            float radialPhase = radialJitter * GoldenAngle;
            float phaseX = MathF.Cos(radialPhase);
            float phaseY = MathF.Sin(radialPhase);
            float directionX = 1.0f;
            float directionY = 0.0f;

            for (int index = 0; index < sampleCount; index++)
            {
                float sampleIndex = index + radialJitter;
                float referenceAngle = sampleIndex * GoldenAngle;
                float referenceX = MathF.Cos(referenceAngle);
                float referenceY = MathF.Sin(referenceAngle);
                float candidateX = phaseX * directionX - phaseY * directionY;
                float candidateY = phaseX * directionY + phaseY * directionX;
                float angularError = MathF.Abs(MathF.Atan2(
                    referenceX * candidateY - referenceY * candidateX,
                    referenceX * candidateX + referenceY * candidateY));

                float u = sampleIndex / MathF.Max(sampleCount, 1.0f);
                float radius = MathF.Sqrt(u);
                float referenceZ = MathF.Sqrt(MathF.Max(
                    1.0f - radius * radius,
                    0.0f));
                float candidateZ = MathF.Sqrt(MathF.Max(
                    1.0f - radius * radius,
                    0.0f));
                float referenceRadialLength = MathF.Sqrt(
                    referenceX * radius * referenceX * radius +
                    referenceY * radius * referenceY * radius);
                float candidateRadialLength = MathF.Sqrt(
                    candidateX * radius * candidateX * radius +
                    candidateY * radius * candidateY * radius);

                Assert.Multiple(() =>
                {
                    Assert.That(
                        angularError,
                        Is.LessThanOrEqualTo(1.0e-5f),
                        $"sample={index}, jitter={radialJitter:R}");
                    Assert.That(
                        MathF.Abs(candidateRadialLength - referenceRadialLength),
                        Is.LessThanOrEqualTo(1.0e-6f),
                        $"sample={index}, jitter={radialJitter:R}");
                    Assert.That(
                        MathF.Abs(candidateZ - referenceZ),
                        Is.LessThanOrEqualTo(1.0e-6f),
                        $"sample={index}, jitter={radialJitter:R}");
                });

                float nextX = directionX * GoldenRotationX -
                    directionY * GoldenRotationY;
                float nextY = directionX * GoldenRotationY +
                    directionY * GoldenRotationX;
                directionX = nextX;
                directionY = nextY;

                float normError = MathF.Abs(MathF.Sqrt(
                    directionX * directionX + directionY * directionY) - 1.0f);
                Assert.That(
                    normError,
                    Is.LessThanOrEqualTo(1.0e-6f),
                    $"rotation={index + 1}, jitter={radialJitter:R}");
            }
        }
    }

    private static string ReadShaderSource() =>
        File.ReadAllText(Path.Combine(
                FindRepoDirectory("Njulf.Shaders"),
                "ambient_occlusion.comp"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

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
