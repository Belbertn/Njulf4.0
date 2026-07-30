using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleMaterialGiRolloutBootstrapTests
{
    private static readonly DateOnly QualificationDate = new(2026, 7, 28);

    [Test]
    public void NoManifest_RemainsExplicitNonShippingConformance()
    {
        SampleMaterialGiRolloutBootstrap bootstrap =
            SampleMaterialGiRolloutBootstrap.Load(null, QualificationDate);
        var settings = new RenderSettings();

        MaterialGiRolloutEvaluation evaluation = bootstrap.Apply(settings);

        Assert.Multiple(() =>
        {
            Assert.That(bootstrap.IsQualifiedRelease, Is.False);
            Assert.That(evaluation.Mode, Is.EqualTo(MaterialGiRolloutMode.Conformance));
            Assert.That(evaluation.ReleaseQualificationRequired, Is.False);
            Assert.That(evaluation.ReleaseQualified, Is.False);
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.All));
        });
    }

    [Test]
    public void QualificationCandidate_IsExplicitRepeatableAndNonShipping()
    {
        SampleMaterialGiRolloutBootstrap bootstrap =
            SampleMaterialGiRolloutBootstrap.Load(
                null,
                QualificationDate,
                qualificationCandidate: true);
        var settings = new RenderSettings();
        using var announcements = new StringWriter();

        MaterialGiRolloutEvaluation first =
            bootstrap.Apply(settings, announcements);
        settings.GlobalIllumination.UseLegacyMaterialGiRollout();
        MaterialGiRolloutEvaluation second =
            bootstrap.Apply(settings, announcements);

        Assert.Multiple(() =>
        {
            Assert.That(bootstrap.IsQualifiedRelease, Is.False);
            Assert.That(bootstrap.IsQualificationCandidate, Is.True);
            Assert.That(
                first.Mode,
                Is.EqualTo(MaterialGiRolloutMode.QualificationCandidate));
            Assert.That(
                second.Mode,
                Is.EqualTo(MaterialGiRolloutMode.QualificationCandidate));
            Assert.That(second.ReleaseQualificationRequired, Is.True);
            Assert.That(second.ReleaseQualified, Is.False);
            Assert.That(second.QualificationFailureCount, Is.Zero);
            Assert.That(second.ApprovalId, Is.Empty);
            Assert.That(second.EvidenceSha256, Is.Empty);
            Assert.That(
                announcements.ToString().Split(
                    "Material-GI V2 non-shipping qualification candidate active;",
                    StringSplitOptions.None),
                Has.Length.EqualTo(2));
        });
    }

    [Test]
    public void QualificationCandidate_CannotConsumeApprovedManifest()
    {
        Assert.That(
            () => SampleMaterialGiRolloutBootstrap.Load(
                "release-qualification.json",
                QualificationDate,
                qualificationCandidate: true),
            Throws.ArgumentException.With.Message.Contains(
                "cannot consume an already approved"));
    }

    [Test]
    public void ValidManifest_IsPreflightedAndReappliedAfterSettingsMutation()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        SampleMaterialGiRolloutBootstrap bootstrap =
            SampleMaterialGiRolloutBootstrap.Load(
                artifacts.ManifestPath,
                QualificationDate);
        var settings = new RenderSettings();
        using var announcements = new StringWriter();

        MaterialGiRolloutEvaluation first =
            bootstrap.Apply(settings, announcements);
        settings.GlobalIllumination.UseLegacyMaterialGiRollout();
        MaterialGiRolloutEvaluation second =
            bootstrap.Apply(settings, announcements);

        Assert.Multiple(() =>
        {
            Assert.That(bootstrap.IsQualifiedRelease, Is.True);
            Assert.That(
                bootstrap.ManifestPath,
                Is.EqualTo(Path.GetFullPath(artifacts.ManifestPath)));
            Assert.That(first.ReleaseQualified, Is.True);
            Assert.That(second.ReleaseQualified, Is.True);
            Assert.That(second.QualificationFailureCount, Is.Zero);
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.All));
            Assert.That(
                announcements.ToString().Split(
                    "Material-GI V2 qualified release active:",
                    StringSplitOptions.None),
                Has.Length.EqualTo(2));
            Assert.That(announcements.ToString(), Does.Contain("devices=2"));
            Assert.That(
                announcements.ToString(),
                Does.Contain(
                    $"qualificationSchema={MaterialGiRolloutQualificationManifest.CurrentSchemaVersion}"));
            Assert.That(
                announcements.ToString(),
                Does.Contain(
                    $"evidenceBundleSchema={MaterialGiReleaseEvidenceContract.BundleSchemaVersion}"));
            Assert.That(
                announcements.ToString(),
                Does.Contain(
                    $"evidenceArtifactSchema={MaterialGiReleaseEvidenceContract.ArtifactSchemaVersion}"));
            Assert.That(
                announcements.ToString(),
                Does.Contain(
                    $"evidenceRoles={MaterialGiReleaseEvidenceContract.RequiredRoles.Count}"));
            Assert.That(announcements.ToString(), Does.Contain("tierDevices=2"));
            Assert.That(
                announcements.ToString(),
                Does.Contain("lowerMemoryRayQueryDevices=1"));
            Assert.That(
                announcements.ToString(),
                Does.Contain("recoveryCapabilities=supported=0,unsupported=2"));
            Assert.That(
                announcements.ToString(),
                Does.Contain(artifacts.Manifest.EvidenceSha256));
        });
    }

    [Test]
    public void InvalidManifest_FailsDuringBootstrapPreflight()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        artifacts.WriteManifest(artifacts.Manifest with
        {
            ApprovalId = string.Empty
        });
        Assert.That(
            () => SampleMaterialGiRolloutBootstrap.Load(
                artifacts.ManifestPath,
                QualificationDate),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("approval identifier"));
    }
}
