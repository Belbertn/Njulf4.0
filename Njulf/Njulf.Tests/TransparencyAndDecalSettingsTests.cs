using Njulf.Rendering.Data;
using Njulf.Rendering;
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
            Assert.That(settings.Transparency.SceneReflectionRayTaskBudget,
                Is.EqualTo(65_536));
            Assert.That(settings.Transparency.SceneReflectionSsrSampleBudget,
                Is.EqualTo(4_194_304));
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
            Assert.That(settings.Decals.ReceiveShadows, Is.True);
            Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.True);
            Assert.That(settings.Decals.IsolatedMaterialIndex, Is.EqualTo(-1));
            Assert.That(settings.Decals.DebugView, Is.EqualTo(DecalDebugView.None));
            Assert.That(settings.Decals.GeometryDepthBias, Is.EqualTo(0.0005f));
            Assert.That(settings.Decals.GeometrySlopeScaledDepthBias, Is.EqualTo(0f));
            Assert.That(settings.Decals.MaxProjectedDecals, Is.EqualTo(256));
            Assert.That(settings.Decals.MaxProjectedDecalsPerTile, Is.EqualTo(64));
            Assert.That(settings.Decals.MaxProjectedDecalsPerPixel, Is.EqualTo(8));
        });
    }

    [Test]
    public void GeometryDecals_RemainVisibleForGiDiagnosticsOnly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VulkanRenderer.ShouldRenderGeometryDecals(0u), Is.True);
            Assert.That(VulkanRenderer.ShouldRenderGeometryDecals(1u), Is.False);
            Assert.That(VulkanRenderer.ShouldRenderGeometryDecals(79u), Is.False);
            Assert.That(VulkanRenderer.ShouldRenderGeometryDecals(80u), Is.True);
            Assert.That(VulkanRenderer.ShouldRenderGeometryDecals(97u), Is.True);
            Assert.That(VulkanRenderer.ShouldRenderGeometryDecals(129u), Is.True);
            Assert.That(VulkanRenderer.ShouldRenderGeometryDecals(130u), Is.False);
        });
    }

    [Test]
    public void TransparencyAndDecalSettings_ClampInvalidValues()
    {
        var settings = new RenderSettings();

        settings.Transparency.MaxTransparentMeshlets = -1;
        settings.Transparency.AlphaDiscardThreshold = 1f;
        settings.Transparency.SceneReflectionRayTaskBudget = -1;
        settings.Transparency.SceneReflectionSsrSampleBudget = -1;
        settings.Decals.GeometryDepthBias = 1f;
        settings.Decals.GeometrySlopeScaledDepthBias = 10f;
        settings.Decals.MaxProjectedDecals = 9999;
        settings.Decals.MaxProjectedDecalsPerTile = 999;
        settings.Decals.MaxProjectedDecalsPerPixel = 999;

        Assert.Multiple(() =>
        {
            Assert.That(settings.Transparency.MaxTransparentMeshlets, Is.EqualTo(0));
            Assert.That(settings.Transparency.AlphaDiscardThreshold, Is.EqualTo(0.05f));
            Assert.That(settings.Transparency.SceneReflectionRayTaskBudget,
                Is.Zero);
            Assert.That(settings.Transparency.SceneReflectionSsrSampleBudget,
                Is.Zero);
            Assert.That(settings.Decals.GeometryDepthBias, Is.EqualTo(0.01f));
            Assert.That(settings.Decals.GeometrySlopeScaledDepthBias, Is.EqualTo(4f));
            Assert.That(settings.Decals.MaxProjectedDecals, Is.EqualTo(4096));
            Assert.That(settings.Decals.MaxProjectedDecalsPerTile, Is.EqualTo(256));
            Assert.That(settings.Decals.MaxProjectedDecalsPerPixel, Is.EqualTo(32));
        });

        settings.Transparency.SceneReflectionRayTaskBudget = int.MaxValue;
        settings.Transparency.SceneReflectionSsrSampleBudget = int.MaxValue;
        Assert.That(settings.Transparency.SceneReflectionRayTaskBudget,
            Is.EqualTo(TransparencySettings.MaximumSceneReflectionRayTaskBudget));
        Assert.That(settings.Transparency.SceneReflectionSsrSampleBudget,
            Is.EqualTo(TransparencySettings.MaximumSceneReflectionSsrSampleBudget));
    }

    [Test]
    public void QualityPresets_EnableSceneReflectionsOnHighAndAbove()
    {
        var settings = new RenderSettings();

        settings.ApplyQualityPreset(RenderQualityPreset.Low);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Transparency.SampleReflections, Is.False);
            Assert.That(settings.Transparency.SceneReflectionRayTaskBudget,
                Is.Zero);
            Assert.That(settings.Transparency.SceneReflectionSsrSampleBudget,
                Is.Zero);
        });

        settings.ApplyQualityPreset(RenderQualityPreset.Medium);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Transparency.SampleReflections, Is.False);
            Assert.That(settings.Transparency.SceneReflectionRayTaskBudget,
                Is.Zero);
            Assert.That(settings.Transparency.SceneReflectionSsrSampleBudget,
                Is.Zero);
        });

        foreach (RenderQualityPreset preset in new[]
                 {
                     RenderQualityPreset.High,
                     RenderQualityPreset.DdgiHigh
                 })
        {
            settings.ApplyQualityPreset(preset);
            Assert.Multiple(() =>
            {
                Assert.That(settings.Transparency.SampleReflections, Is.True,
                    preset.ToString());
                Assert.That(settings.Transparency.SceneReflectionRayTaskBudget,
                    Is.EqualTo(65_536), preset.ToString());
                Assert.That(settings.Transparency.SceneReflectionSsrSampleBudget,
                    Is.EqualTo(4_194_304), preset.ToString());
            });
        }

        settings.ApplyQualityPreset(RenderQualityPreset.Ultra);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Transparency.SampleReflections, Is.True);
            Assert.That(settings.Transparency.SceneReflectionRayTaskBudget,
                Is.EqualTo(131_072));
            Assert.That(settings.Transparency.SceneReflectionSsrSampleBudget,
                Is.EqualTo(8_388_608));
        });
    }
}
