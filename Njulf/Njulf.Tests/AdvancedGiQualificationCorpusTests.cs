using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiQualificationCorpusTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-advanced-gi-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void PinAndVerify_ProducesStablePortableCorpusIdentity()
    {
        string requestPath = WriteRequestAndArtifacts(_directory);
        string outputPath = Path.Combine(_directory, "corpus.json");

        AdvancedGiVerifiedQualificationCorpus pinned =
            AdvancedGiQualificationCorpusCodec.Pin(
                _directory, requestPath, outputPath);
        bool accepted =
            AdvancedGiQualificationCorpusCodec.TryLoadAndVerify(
                outputPath,
                out AdvancedGiVerifiedQualificationCorpus? verified,
                out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True, detail);
            Assert.That(verified, Is.Not.Null);
            Assert.That(verified!.CorpusSha256,
                Is.EqualTo(pinned.CorpusSha256));
            Assert.That(verified.CorpusSha256,
                Does.StartWith("sha256:").And.Length.EqualTo(71));
            Assert.That(verified.CaseCount, Is.EqualTo(1));
            Assert.That(verified.ArtifactCount, Is.EqualTo(4));
            Assert.That(verified.CoveredFeatures,
                Is.EquivalentTo(Enum.GetValues<
                    AdvancedGiPrerequisiteFeature>()));
        });
    }

    [Test]
    public void ArtifactMutation_InvalidatesPinnedCorpus()
    {
        string requestPath = WriteRequestAndArtifacts(_directory);
        string outputPath = Path.Combine(_directory, "corpus.json");
        _ = AdvancedGiQualificationCorpusCodec.Pin(
            _directory, requestPath, outputPath);
        File.AppendAllText(Path.Combine(_directory, "case", "scene.json"),
            " ");

        bool accepted =
            AdvancedGiQualificationCorpusCodec.TryLoadAndVerify(
                outputPath, out _, out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(detail,
                Is.EqualTo("advanced-gi-corpus-artifact-length-mismatch"));
        });
    }

    [Test]
    public void TraversalPath_IsRejectedBeforePinning()
    {
        AdvancedGiQualificationCorpusDocument request = CreateRequest() with
        {
            Cases =
            [
                CreateRequest().Cases.Single() with
                {
                    Artifacts =
                    [
                        new AdvancedGiQualificationCorpusArtifact
                        {
                            Role = "scene",
                            RelativePath = "../escape.json"
                        },
                        Artifact("camera-script", "case/camera.json"),
                        Artifact("settings", "case/settings.json"),
                        Artifact("reference", "case/reference.exr")
                    ]
                }
            ]
        };
        string requestPath = Path.Combine(_directory, "request.json");
        File.WriteAllText(requestPath,
            AdvancedGiQualificationCorpusCodec.SerializeDocument(request));

        Assert.That(
            () => AdvancedGiQualificationCorpusCodec.Pin(
                _directory,
                requestPath,
                Path.Combine(_directory, "corpus.json")),
            Throws.TypeOf<InvalidDataException>().With.Message.EqualTo(
                 "advanced-gi-corpus-artifact-path-invalid-or-duplicate"));
    }

    [Test]
    public void OutputCollision_IsRejectedBeforeArtifactCanBeOverwritten()
    {
        string requestPath = WriteRequestAndArtifacts(_directory);
        string artifactPath = Path.Combine(
            _directory, "case", "scene.json");
        string original = File.ReadAllText(artifactPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => AdvancedGiQualificationCorpusCodec.Pin(
                    _directory,
                    requestPath,
                    artifactPath),
                Throws.TypeOf<InvalidDataException>().With.Message.EqualTo(
                    "advanced-gi-corpus-output-collides-with-artifact"));
            Assert.That(File.ReadAllText(artifactPath), Is.EqualTo(original));
        });
    }

    private static string WriteRequestAndArtifacts(string root)
    {
        string caseDirectory = Path.Combine(root, "case");
        Directory.CreateDirectory(caseDirectory);
        File.WriteAllText(Path.Combine(caseDirectory, "scene.json"),
            "{\"scene\":1}");
        File.WriteAllText(Path.Combine(caseDirectory, "camera.json"),
            "{\"camera\":1}");
        File.WriteAllText(Path.Combine(caseDirectory, "settings.json"),
            "{\"settings\":1}");
        File.WriteAllBytes(Path.Combine(caseDirectory, "reference.exr"),
            [1, 2, 3, 4]);
        string requestPath = Path.Combine(root, "request.json");
        File.WriteAllText(requestPath,
            AdvancedGiQualificationCorpusCodec.SerializeDocument(
                CreateRequest()));
        return requestPath;
    }

    private static AdvancedGiQualificationCorpusDocument CreateRequest() =>
        new()
        {
            CorpusId = "advanced-gi-test-corpus",
            Cases =
            [
                new AdvancedGiQualificationCorpusCase
                {
                    Id = "all-feature-case",
                    Scenario = "deterministic-test",
                    Description =
                        "Synthetic fixture covering all Advanced GI identities.",
                    Features = Enum.GetValues<
                        AdvancedGiPrerequisiteFeature>(),
                    Artifacts =
                    [
                        Artifact("scene", "case/scene.json"),
                        Artifact("camera-script", "case/camera.json"),
                        Artifact("settings", "case/settings.json"),
                        Artifact("reference", "case/reference.exr")
                    ]
                }
            ]
        };

    private static AdvancedGiQualificationCorpusArtifact Artifact(
        string role,
        string path) => new()
    {
        Role = role,
        RelativePath = path
    };
}
