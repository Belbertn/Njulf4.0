using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class TransparencyAndDecalSettingsTests
{
    [Test]
    public void TransparencyDefaults_PreserveSortedAlphaBlendPath()
    {
        var settings = new RenderSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.Transparency.Enabled, Is.True);
            Assert.That(settings.Transparency.Mode, Is.EqualTo(TransparencyMode.SortedAlphaBlend));
            Assert.That(settings.Transparency.DebugView, Is.EqualTo(TransparencyDebugView.None));
            Assert.That(settings.Transparency.ReceiveShadows, Is.True);
            Assert.That(settings.Transparency.SampleReflections, Is.True);
            Assert.That(settings.Transparency.SortPerMeshlet, Is.True);
            Assert.That(settings.Transparency.MaxTransparentMeshlets, Is.EqualTo(262144));
            Assert.That(settings.Transparency.AlphaDiscardThreshold, Is.EqualTo(0.001f));
        });
    }

    [Test]
    public void DecalDefaults_AreConservative()
    {
        var settings = new RenderSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.Decals.GeometryDecalsEnabled, Is.True);
            Assert.That(settings.Decals.ProjectedDecalsEnabled, Is.False);
            Assert.That(settings.Decals.DebugView, Is.EqualTo(DecalDebugView.None));
            Assert.That(settings.Decals.GeometryDepthBias, Is.EqualTo(0.0005f));
            Assert.That(settings.Decals.GeometrySlopeScaledDepthBias, Is.EqualTo(0f));
            Assert.That(settings.Decals.MaxProjectedDecals, Is.EqualTo(256));
            Assert.That(settings.Decals.MaxProjectedDecalsPerTile, Is.EqualTo(64));
            Assert.That(settings.Decals.MaxProjectedDecalsPerPixel, Is.EqualTo(8));
        });
    }

    [Test]
    public void TransparencyAndDecalSettings_ClampInvalidValues()
    {
        var settings = new RenderSettings();

        settings.Transparency.MaxTransparentMeshlets = -1;
        settings.Transparency.AlphaDiscardThreshold = 1f;
        settings.Decals.GeometryDepthBias = 1f;
        settings.Decals.GeometrySlopeScaledDepthBias = 10f;
        settings.Decals.MaxProjectedDecals = 9999;
        settings.Decals.MaxProjectedDecalsPerTile = 999;
        settings.Decals.MaxProjectedDecalsPerPixel = 999;

        Assert.Multiple(() =>
        {
            Assert.That(settings.Transparency.MaxTransparentMeshlets, Is.EqualTo(0));
            Assert.That(settings.Transparency.AlphaDiscardThreshold, Is.EqualTo(0.05f));
            Assert.That(settings.Decals.GeometryDepthBias, Is.EqualTo(0.01f));
            Assert.That(settings.Decals.GeometrySlopeScaledDepthBias, Is.EqualTo(4f));
            Assert.That(settings.Decals.MaxProjectedDecals, Is.EqualTo(4096));
            Assert.That(settings.Decals.MaxProjectedDecalsPerTile, Is.EqualTo(256));
            Assert.That(settings.Decals.MaxProjectedDecalsPerPixel, Is.EqualTo(32));
        });
    }
}
