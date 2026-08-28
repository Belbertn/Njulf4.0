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
            Assert.That(verified.CaseCount,
                Is.EqualTo(AdvancedGiQualificationCorpusCodec
                    .RequiredProductionScenarios.Count));
            Assert.That(verified.ArtifactCount,
                Is.EqualTo(
                    AdvancedGiQualificationCorpusCodec
                        .RequiredProductionScenarios.Count *
                    AdvancedGiQualificationCorpusCodec
                        .RequiredArtifactRoles.Count));
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
        string firstScene = CreateRequest().Cases[0].Artifacts
            .Single(static artifact => artifact.Role == "scene")
            .RelativePath.Replace('/', Path.DirectorySeparatorChar);
        File.AppendAllText(Path.Combine(_directory, firstScene), " ");

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
        AdvancedGiQualificationCorpusDocument source = CreateRequest();
        AdvancedGiQualificationCorpusDocument request = source with
        {
            Cases = source.Cases.Select((item, index) => index == 0
                ? item with
                {
                    Artifacts = item.Artifacts.Select(artifact =>
                            artifact.Role == "scene"
                                ? artifact with
                                {
                                    RelativePath = "../escape.json"
                                }
                                : artifact)
                        .ToArray()
                }
                : item).ToArray()
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
            _directory,
            CreateRequest().Cases[0].Artifacts[0].RelativePath.Replace(
                '/', Path.DirectorySeparatorChar));
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

    [Test]
    public void MissingProductionScenario_IsRejectedBeforeArtifactIo()
    {
        AdvancedGiQualificationCorpusDocument source = CreateRequest();
        AdvancedGiQualificationCorpusDocument incomplete = source with
        {
            Cases = source.Cases
                .Where(static item => item.Scenario != "water")
                .ToArray()
        };
        string requestPath = Path.Combine(_directory, "request.json");
        File.WriteAllText(
            requestPath,
            AdvancedGiQualificationCorpusCodec.SerializeDocument(incomplete));

        Assert.That(
            () => AdvancedGiQualificationCorpusCodec.Pin(
                _directory,
                requestPath,
                Path.Combine(_directory, "corpus.json")),
            Throws.TypeOf<InvalidDataException>().With.Message.EqualTo(
                "advanced-gi-corpus-production-scenario-coverage-incomplete"));
    }

    [Test]
    public void IsolationWithoutPairwiseCombinationCoverage_IsRejected()
    {
        AdvancedGiQualificationCorpusDocument source = CreateRequest();
        AdvancedGiPrerequisiteFeature[] features =
            Enum.GetValues<AdvancedGiPrerequisiteFeature>();
        AdvancedGiQualificationCorpusDocument isolatedOnly = source with
        {
            Cases = source.Cases.Select((item, index) => item with
            {
                Features = [features[index % features.Length]]
            }).ToArray()
        };
        string requestPath = Path.Combine(_directory, "request.json");
        File.WriteAllText(
            requestPath,
            AdvancedGiQualificationCorpusCodec.SerializeDocument(isolatedOnly));

        Assert.That(
            () => AdvancedGiQualificationCorpusCodec.Pin(
                _directory,
                requestPath,
                Path.Combine(_directory, "corpus.json")),
            Throws.TypeOf<InvalidDataException>().With.Message.EqualTo(
                "advanced-gi-corpus-pairwise-feature-coverage-incomplete"));
    }

    private static string WriteRequestAndArtifacts(string root)
    {
        AdvancedGiQualificationCorpusDocument request = CreateRequest();
        foreach (AdvancedGiQualificationCorpusArtifact artifact in
                 request.Cases.SelectMany(static item => item.Artifacts))
        {
            string path = Path.Combine(
                root,
                artifact.RelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"{{\"role\":\"{artifact.Role}\",\"path\":\"{artifact.RelativePath}\"}}");
        }
        string requestPath = Path.Combine(root, "request.json");
        File.WriteAllText(requestPath,
            AdvancedGiQualificationCorpusCodec.SerializeDocument(
                request));
        return requestPath;
    }

    private static AdvancedGiQualificationCorpusDocument CreateRequest() =>
        new()
        {
            CorpusId = "advanced-gi-test-corpus",
            Cases = AdvancedGiQualificationCorpusCodec
                .RequiredProductionScenarios
                .Select((scenario, index) =>
                    CreateCase(scenario, index))
                .ToArray()
        };

    private static AdvancedGiQualificationCorpusCase CreateCase(
        string scenario,
        int index)
    {
        AdvancedGiPrerequisiteFeature[] all =
            Enum.GetValues<AdvancedGiPrerequisiteFeature>();
        AdvancedGiPrerequisiteFeature[] features = index < all.Length
            ? [all[index]]
            : all;
        string directory = $"case-{index:D2}-{scenario}";
        return new AdvancedGiQualificationCorpusCase
        {
            Id = directory,
            Scenario = scenario,
            Description =
                $"Deterministic {scenario} golden/equal-work fixture.",
            Features = features,
            Artifacts = AdvancedGiQualificationCorpusCodec
                .RequiredArtifactRoles
                .Select(role => Artifact(
                    role,
                    $"{directory}/{role}.bin"))
                .ToArray()
        };
    }

    private static AdvancedGiQualificationCorpusArtifact Artifact(
        string role,
        string path) => new()
    {
        Role = role,
        RelativePath = path
    };
}
