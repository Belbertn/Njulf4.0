using Njulf.Rendering.Debug;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleMaterialGiSemanticEvidenceTests
{
    [Test]
    public void SemanticGate_AcceptsEveryNamedExpectedSurfaceAndReportsMetrics()
    {
        SampleMaterialGiSemanticEvidenceContract contract =
            SampleMaterialGiConformanceCatalog.SemanticEvidence;
        LinearFloatImage image = CreateExpectedImage(contract);
        string relativePath = SemanticArtifactRelativePath(contract);

        SampleMaterialGiSemanticEvidenceReport report =
            SampleMaterialGiSemanticEvidenceGate.Evaluate(
                contract,
                image,
                relativePath,
                new string('a', 64));

        Assert.Multiple(() =>
        {
            Assert.That(report.Passed, Is.True);
            Assert.That(report.FailureReason, Is.Empty);
            Assert.That(report.ContractFingerprint, Is.EqualTo(contract.Fingerprint));
            Assert.That(report.Signal, Is.EqualTo(SampleMaterialGiCaptureSignal.MaterialSidedness));
            Assert.That(report.Regions, Has.Count.EqualTo(contract.Regions.Count));
            Assert.That(report.Regions.Select(static value => value.Name), Is.Unique);
            Assert.That(report.Regions.All(static value => value.Passed), Is.True);
            Assert.That(
                report.Regions.All(static value => value.MatchingPixelFraction == 1.0),
                Is.True);
        });
    }

    [Test]
    public void SemanticGate_FailsClosedAndNamesAnIncorrectMirroredRegion()
    {
        SampleMaterialGiSemanticEvidenceContract contract =
            SampleMaterialGiConformanceCatalog.SemanticEvidence;
        LinearFloatImage image = CreateExpectedImage(contract);
        string relativePath = SemanticArtifactRelativePath(contract);
        SampleMaterialGiSemanticRoi target = contract.Regions.Single(
            static value =>
                value.Name == "winding.mirrored-double-back.visible-backface");
        FillRegion(
            image,
            target.Region,
            new SampleMaterialGiSemanticRgb(0.1f, 0.8f, 1f));

        SampleMaterialGiSemanticEvidenceReport report =
            SampleMaterialGiSemanticEvidenceGate.Evaluate(
                contract,
                image,
                relativePath,
                new string('b', 64));

        Assert.Multiple(() =>
        {
            Assert.That(report.Passed, Is.False);
            Assert.That(report.FailureReason, Does.Contain(target.Name));
            Assert.That(
                report.Regions.Single(value => value.Name == target.Name).Passed,
                Is.False);
            Assert.That(
                report.Regions.Where(value => value.Name != target.Name)
                    .All(static value => value.Passed),
                Is.True);
        });
    }

    [Test]
    public void MeshShaders_RestoreAuthoredWindingForEveryObjectRasterPath()
    {
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string[] objectMeshShaders =
        [
            "depth.mesh",
            "depth_alpha.mesh",
            "forward.mesh",
            "forward_simple.mesh",
            "motion_vector.mesh",
            "shadow_depth.mesh",
            "shadow_depth_alpha.mesh"
        ];

        Assert.That(
            common,
            Does.Contain("uvec3 ResolveMirroredInstanceTriangle(")
                .And.Contain("negativeDeterminant ? uvec3(i0, i2, i1)"));
        foreach (string shaderName in objectMeshShaders)
        {
            string shader = ReadRepoText("Njulf.Shaders", shaderName);
            Assert.Multiple(() =>
            {
                Assert.That(
                    shader,
                    Does.Contain("ReadRowMajorLinearDeterminant(instanceBufferIndex, objectWordOffset) < 0.0"),
                    shaderName);
                Assert.That(
                    shader,
                    Does.Contain("ResolveMirroredInstanceTriangle("),
                    shaderName);
                Assert.That(
                    shader,
                    Does.Contain("shared uint meshNegativeDeterminant;"),
                    shaderName);
            });
        }
    }

    private static LinearFloatImage CreateExpectedImage(
        SampleMaterialGiSemanticEvidenceContract contract)
    {
        var image = new LinearFloatImage(
            contract.Width,
            contract.Height,
            new float[checked(contract.Width * contract.Height * 3)]);
        foreach (SampleMaterialGiSemanticRoi region in contract.Regions)
            FillRegion(image, region.Region, region.ExpectedRgb);
        return image;
    }

    private static string SemanticArtifactRelativePath(
        SampleMaterialGiSemanticEvidenceContract contract)
    {
        SampleMaterialGiCaptureOutput output =
            SampleMaterialGiConformanceCatalog.RequiredOutputs.Single(
                value => value.Signal == contract.Signal);
        return SampleMaterialGiArtifactPublisher.GetRelativeArtifactPath(output);
    }

    private static void FillRegion(
        LinearFloatImage image,
        SampleMaterialGiPixelRegion region,
        SampleMaterialGiSemanticRgb rgb)
    {
        for (int y = region.Y; y < region.Y + region.Height; y++)
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                int component = checked((y * image.Width + x) * 3);
                image.Pixels[component] = rgb.R;
                image.Pixels[component + 1] = rgb.G;
                image.Pixels[component + 2] = rgb.B;
            }
    }

    private static string ReadRepoText(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeSegments)}'.");
    }
}
