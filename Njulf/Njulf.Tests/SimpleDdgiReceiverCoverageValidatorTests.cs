using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverCoverageValidatorTests
{
    [Test]
    public void CanonicalSponzaCameraRings_CoverFacadeAndGalleriesAcrossLowHighPath()
    {
        var settings = new RenderSettings();
        SampleSponzaGlobalIlluminationProfile.Configure(settings);

        SimpleDdgiReceiverCoverageReport report = SimpleDdgiReceiverCoverageValidator.Validate(
            settings.GlobalIllumination,
            new BoundingBox(new Vector3(-17.0f, -2.0f, -10.0f), new Vector3(21.0f, 20.0f, 15.0f)),
            [
                new SimpleDdgiReceiverCoverageRegion(
                    "upper-central-facade",
                    new BoundingBox(new Vector3(-4.0f, 10.0f, -2.0f), new Vector3(6.0f, 18.0f, 6.0f)),
                    MaximumPrimarySpacing: 3.75f,
                    RequireCoarserFallback: false),
                new SimpleDdgiReceiverCoverageRegion(
                    "left-gallery-interior",
                    new BoundingBox(new Vector3(-14.5f, 5.0f, -6.5f), new Vector3(-8.5f, 8.5f, 11.5f)),
                    MaximumPrimarySpacing: 3.75f,
                    RequireCoarserFallback: false),
                new SimpleDdgiReceiverCoverageRegion(
                    "right-gallery-interior",
                    new BoundingBox(new Vector3(11.5f, 5.0f, -6.5f), new Vector3(18.5f, 8.5f, 11.5f)),
                    MaximumPrimarySpacing: 3.75f,
                    RequireCoarserFallback: false)
            ],
            [
                new SimpleDdgiCoverageCameraSample("SponzaPlazaUpperFacadeLow", new Vector3(0.0f, 1.35f, 0.0f)),
                new SimpleDdgiCoverageCameraSample("SponzaPlazaUpperFacadeHigh", new Vector3(0.0f, 10.35f, 0.0f))
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(report.Layout.WasDegraded, Is.False, report.Layout.Summary);
            Assert.That(report.Issues, Is.Empty);
            Assert.That(report.Samples, Has.All.Matches<SimpleDdgiReceiverCoverageSample>(sample =>
                sample.IsCovered && sample.IsWithinResolutionTarget && sample.PrimarySpacing <= 3.75f));
            Assert.That(report.Samples, Has.All.Matches<SimpleDdgiReceiverCoverageSample>(sample =>
                sample.PrimaryVolume != null && sample.PrimaryVolume.StartsWith("ring-", StringComparison.Ordinal)));
            Assert.That(
                report.Layout.Volumes.Single(static volume => volume.Request.Id == "ring-2").Request.Spacing,
                Is.EqualTo(11.25f).Within(0.0001f));
            Assert.That(report.ExpectedRingRecenterEvents, Is.GreaterThan(0));
        });
    }

    [Test]
    public void CoarseOnlyCoverage_IsReportedAsUnderResolvedRatherThanAcceptedByCameraPosition()
    {
        var settings = new GlobalIlluminationSettings
        {
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            SimpleDdgiRingCount = 1,
            SimpleDdgiRingBaseSpacing = 3.0f,
            SimpleDdgiNearRingGridSizeX = 6,
            SimpleDdgiNearRingGridSizeY = 6,
            SimpleDdgiNearRingGridSizeZ = 6,
            SimpleDdgiSampledAtlasEnabled = false
        };

        SimpleDdgiReceiverCoverageReport report = SimpleDdgiReceiverCoverageValidator.Validate(
            settings,
            new BoundingBox(new Vector3(-10.0f, -10.0f, -10.0f), new Vector3(10.0f, 10.0f, 10.0f)),
            [new SimpleDdgiReceiverCoverageRegion(
                "fine-receiver",
                new BoundingBox(new Vector3(-1.0f, -1.0f, -1.0f), new Vector3(1.0f, 1.0f, 1.0f)),
                MaximumPrimarySpacing: 1.0f,
                RequireCoarserFallback: false)],
            [new SimpleDdgiCoverageCameraSample("camera", Vector3.Zero)]);

        Assert.Multiple(() =>
        {
            Assert.That(report.Layout.WasDegraded, Is.False, report.Layout.Summary);
            Assert.That(report.Issues, Has.Some.Matches<SimpleDdgiReceiverCoverageIssue>(issue =>
                issue.Kind == SimpleDdgiCoverageIssueKind.UnderResolved));
            Assert.That(report.Samples, Has.All.Matches<SimpleDdgiReceiverCoverageSample>(sample =>
                sample.IsCovered && !sample.IsWithinResolutionTarget));
        });
    }

    [Test]
    public void AuthoredTransitionWithoutAcceptedFallback_IsAnActionableCoverageFailure()
    {
        var settings = new GlobalIlluminationSettings
        {
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            SimpleDdgiRingCount = 0,
            SimpleDdgiSampledAtlasEnabled = false
        };
        settings.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            new Vector3(-2.0f, -2.0f, -2.0f),
            new Vector3(2.0f, 2.0f, 2.0f),
            1.0f,
            purpose: SimpleDdgiVolumePurpose.ReceiverHero,
            priority: 10));

        SimpleDdgiReceiverCoverageReport report = SimpleDdgiReceiverCoverageValidator.Validate(
            settings,
            new BoundingBox(new Vector3(-4.0f, -4.0f, -4.0f), new Vector3(4.0f, 4.0f, 4.0f)),
            [new SimpleDdgiReceiverCoverageRegion(
                "authored-edge",
                new BoundingBox(new Vector3(2.0f, -0.1f, -0.1f), new Vector3(2.0f, 0.1f, 0.1f)),
                MaximumPrimarySpacing: 1.0f)],
            [new SimpleDdgiCoverageCameraSample("camera", Vector3.Zero)]);

        Assert.That(report.Issues, Has.Some.Matches<SimpleDdgiReceiverCoverageIssue>(issue =>
            issue.Kind == SimpleDdgiCoverageIssueKind.MissingTransitionFallback));
    }
}
