using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiCandidateProfileTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-advanced-gi-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Load_CompilesBoundedCandidateAndNormalizesCorpusHashForm()
    {
        AdvancedGiCandidateProfileDocument document = CreateDocument(
            admissionCorpus: "sha256:" + new string('d', 64));
        string path = Write(document);

        bool accepted = AdvancedGiCandidateProfileCodec.TryLoad(
            path,
            out AdvancedGiCandidateProfileDocument? loaded,
            out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True, detail);
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Caustics, Is.Not.Null);
        });
    }

    [Test]
    public void Load_RejectsCandidateWhosePlanEscapesAuthorizedCorpus()
    {
        AdvancedGiCandidateProfileDocument document = CreateDocument(
            admissionCorpus: new string('f', 64));
        string path = Write(document);

        bool accepted = AdvancedGiCandidateProfileCodec.TryLoad(
            path, out _, out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(detail, Does.StartWith(
                "advanced-gi-C4-candidate-plan-invalid:"));
            Assert.That(detail, Does.Contain(
                "caustic-candidate-corpus-binding-mismatch"));
        });
    }

    [Test]
    public void RuntimeContentPolicy_RequiresExactMatchForCandidatesAndAuto()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                GiCausticMode.WorldCacheExperiment,
                usesCandidateAuthorization: false), Is.False);
            Assert.That(AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                GiCausticMode.WorldCacheExperiment,
                usesCandidateAuthorization: true), Is.True);
            Assert.That(AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                GiCausticMode.AutoQualified,
                usesCandidateAuthorization: false), Is.True);
            Assert.That(AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment,
                usesCandidateAuthorization: false), Is.False);
            Assert.That(AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment,
                usesCandidateAuthorization: true), Is.True);
            Assert.That(AdvancedGiRuntimeContentPolicy.RequiresExactMatch(
                SimpleDdgiNearFieldResidualMode.AutoQualified,
                usesCandidateAuthorization: false), Is.True);
        });
    }

    private string Write(AdvancedGiCandidateProfileDocument document)
    {
        string path = Path.Combine(_directory, "candidate.json");
        File.WriteAllText(path,
            AdvancedGiCandidateProfileCodec.SerializeDocument(document));
        return path;
    }

    private static AdvancedGiCandidateProfileDocument CreateDocument(
        string admissionCorpus)
    {
        var authorization = new AdvancedGiCandidateAuthorization(
            AuthorizationId: "candidate-test",
            BuildCommit: new string('a', 40),
            ShaderBundleSha256: new string('b', 64),
            SettingsFingerprintSha256: new string('c', 64),
            ContentBinding: new(
                new string('d', 64),
                "candidate-profile",
                new string('e', 64)));
        var configuration = new GiTaggedCausticCacheConfiguration(
            Enabled: true,
            HeroMaterialCount: 2,
            PhotonTaskCapacity: 1_024,
            MaximumWorldCells: 1_024,
            MaximumPhotonsPerCell: 8,
            MemoryBudgetBytes: 1UL * 1024UL * 1024UL,
            ScreenResolveProfile: new(64, 64));
        var context = new GiCausticAdmissionContext(
            DeviceQualificationKey: "candidate-device",
            CorpusId: admissionCorpus,
            ContentRevision: 1UL,
            LightDistributionRevision: 2UL,
            EmissiveDistributionRevision: 3UL,
            HeroSourceRevision: 4UL,
            CurrentPoseTlasSignature: 5UL,
            ShaderBundleHash: "candidate-shaders");
        return new AdvancedGiCandidateProfileDocument
        {
            Authorization = authorization,
            Caustics = new AdvancedGiCausticCandidateDocument
            {
                AdmissionContext = context,
                Configuration = configuration
            }
        };
    }
}
