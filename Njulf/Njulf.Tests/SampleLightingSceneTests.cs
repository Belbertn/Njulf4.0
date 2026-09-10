using Njulf.Assets.Scenes;
using Njulf.Core.Scene;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed class SampleLightingSceneTests
{
    [Test]
    public void EnvironmentPresetsBelongToTheirScenesAndPersistWithoutDrawing()
    {
        using var main = new Scene();
        using var preview = new Scene();
        SampleEnvironment.Configure(main, SampleEnvironmentMode.ProceduralOutdoor);
        SceneEnvironment original = main.Environment!;
        SampleEnvironment.Configure(preview, SampleEnvironmentMode.StudioNeutral);
        Assert.That(preview.Environment!.SkyIntensity, Is.EqualTo(0.45f));
        SampleEnvironment.Configure(preview, SampleEnvironmentMode.HdrAsset);
        Assert.That(new SceneDocumentWriter().CreateDocument(preview).Environment!.SourcePath,
            Is.EqualTo("textures/kloppenheim_05_4k.hdr"));
        Assert.That(main.Environment, Is.SameAs(original));
        SampleEnvironment.Configure(preview, SampleEnvironmentMode.Disabled);
        Assert.That(preview.Environment!.Enabled, Is.False);
        Assert.That(main.Environment!.Enabled, Is.True);
    }

    [Test]
    public void ConfiguringAnotherSceneDoesNotReplaceTheActiveScenesLights()
    {
        using var main = new Scene();
        using var preview = new Scene();
        SampleLighting.Configure(main, SampleLightingMode.DirectionalKey);
        SceneLight sun = main.Lights.Single();
        ulong revision = main.LightRevision;
        SampleLighting.Configure(preview, SampleLightingMode.ThreePointDemo);
        Assert.That(preview.Lights.Count, Is.EqualTo(3));
        SampleLighting.Configure(preview, SampleLightingMode.PointShadowDemo);
        Assert.That(main.Lights.Single(), Is.SameAs(sun));
        Assert.That(main.LightRevision, Is.EqualTo(revision));
        Assert.That(preview.Lights.Any(light => light.Type == SceneLightType.Point && light.CastsShadows), Is.True);
    }

    [Test]
    public void PhotometricFixturePersistsItsSourceWithoutAGraphicsDevice()
    {
        using var scene = new Scene();
        SampleLighting.Configure(scene, SampleLightingMode.AnalyticalAreaLightShowcase);
        SceneLightDocument profileLight = new SceneDocumentWriter().CreateDocument(scene)
            .Lights.Single(light => light.IesProfile is not null);
        Assert.That(profileLight.Type, Is.EqualTo("Spot"));
        Assert.That(profileLight.IesProfile!.Path, Does.EndWith(SampleLighting.AnalyticalAreaLightIesFileName));
        Assert.That(profileLight.CastsShadows, Is.False);
    }
}