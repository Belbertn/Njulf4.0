using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PerformanceOptimizationSettingsTests
{
    [Test]
    public void DefaultsEnableEveryCampaignFeature()
    {
        var settings = new RenderSettings();

        Assert.Multiple(() =>
        {
            Assert.That(RenderSettings.SerializationVersion, Is.EqualTo(27));
            Assert.That(settings.PerformanceOptimizations.Enabled, Is.True);
            Assert.That(
                settings.PerformanceOptimizations.EnabledFeatures,
                Is.EqualTo(PerformanceOptimizationFeature.All));
            Assert.That(
                settings.EffectivePerformanceOptimizationFeatures,
                Is.EqualTo(PerformanceOptimizationFeature.All));
        });
    }

    [Test]
    public void MaskExpressionsSupportAllNoneAndSubtraction()
    {
        PerformanceOptimizationFeature parsed =
            PerformanceOptimizationFeatureMask.Parse(
                "all,-async-gi,-generation-reuse");
        PerformanceOptimizationFeature additive =
            PerformanceOptimizationFeatureMask.Parse(
                "none,meshlet-working-set,split-hybrid-forward");

        Assert.Multiple(() =>
        {
            Assert.That(
                parsed,
                Is.EqualTo(
                    PerformanceOptimizationFeature.All &
                    ~PerformanceOptimizationFeature.AsyncGiFarFieldExecution &
                    ~PerformanceOptimizationFeature
                        .DdgiPublicationGenerationReuse));
            Assert.That(
                additive,
                Is.EqualTo(
                    PerformanceOptimizationFeature.MeshletWorkingSetAdmission |
                    PerformanceOptimizationFeature.SplitHybridForwardPrograms));
            Assert.That(
                PerformanceOptimizationFeatureMask.Format(parsed),
                Does.Not.Contain("async-gi"));
            Assert.Throws<ArgumentException>(() =>
                PerformanceOptimizationFeatureMask.Parse("all,unknown"));
        });
    }

    [Test]
    public void MasterAndAsyncDisableOnlyTheirEffectivePaths()
    {
        var settings = new RenderSettings();
        settings.AsyncCompute.Mode = AsyncComputeMode.Disabled;

        Assert.That(
            settings.IsPerformanceOptimizationEnabled(
                PerformanceOptimizationFeature.AsyncGiFarFieldExecution),
            Is.False);
        Assert.That(
            settings.IsPerformanceOptimizationEnabled(
                PerformanceOptimizationFeature.SplitHybridForwardPrograms),
            Is.True);

        settings.PerformanceOptimizations.Enabled = false;
        Assert.That(
            settings.EffectivePerformanceOptimizationFeatures,
            Is.EqualTo(PerformanceOptimizationFeature.None));
    }

    [Test]
    public void Version27RoundTripsAndOlderFilesDefaultOn()
    {
        string legacyPath = Path.GetTempFileName();
        string currentPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(legacyPath, """
            {
              "Version": 26,
              "QualityPreset": "DdgiHigh"
            }
            """);
            RenderSettings legacy = RenderSettings.Load(legacyPath);

            var current = new RenderSettings();
            current.PerformanceOptimizations.Enabled = false;
            current.PerformanceOptimizations.EnabledFeatures =
                PerformanceOptimizationFeature.MeshletWorkingSetAdmission |
                PerformanceOptimizationFeature.RowMajorSpatialDdgiGather;
            current.Save(currentPath);
            RenderSettings roundTrip = RenderSettings.Load(currentPath);

            Assert.Multiple(() =>
            {
                Assert.That(legacy.PerformanceOptimizations.Enabled, Is.True);
                Assert.That(
                    legacy.PerformanceOptimizations.EnabledFeatures,
                    Is.EqualTo(PerformanceOptimizationFeature.All));
                Assert.That(roundTrip.PerformanceOptimizations.Enabled, Is.False);
                Assert.That(
                    roundTrip.PerformanceOptimizations.EnabledFeatures,
                    Is.EqualTo(current.PerformanceOptimizations.EnabledFeatures));
            });
        }
        finally
        {
            File.Delete(legacyPath);
            File.Delete(currentPath);
        }
    }
}
