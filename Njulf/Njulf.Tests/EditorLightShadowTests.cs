using System.Numerics;
using Njulf.Core.Camera;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;
using Vec = Njulf.Core.Math.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class EditorLightShadowTests
{
    [TestCase("High", LightType.Point)]
    [TestCase("Medium", LightType.Point)]
    [TestCase("Low", LightType.Point)]
    [TestCase("High", LightType.Spot)]
    [TestCase("Medium", LightType.Spot)]
    [TestCase("Low", LightType.Spot)]
    public void SponzaLightShadowEdit_AdmitsShadowMap(string profile, LightType type)
    {
        var settings = new RenderSettings();
        SampleLighting.ConfigureRenderSettings(settings, SampleLightingMode.DirectionalKey);
        SamplePlazaGlobalIllumination.ConfigureRenderSettingsForMemoryProfile(settings,
            Enum.Parse<SamplePlazaGpuMemoryProfile>(profile));
        int pointLimit = settings.Shadows.MaxShadowedPointLights;
        int spotLimit = settings.Shadows.MaxShadowedSpotLights;
        int memoryBudget = settings.Shadows.LocalShadowMemoryBudgetMiB;
        Light light = CreateLight(type);
        Light previous = light;
        previous.CastsShadows = false;

        // The reported failure: the checkbox flag alone leaves the pass disabled.
        var selector = new LocalShadowSelector();
        var camera = CreateCamera();
        LocalShadowSelection before = selector.Select([light], camera, settings.Shadows);
        Assert.That(before.PointCandidateCount + before.SpotCandidateCount, Is.EqualTo(1));
        Assert.That(before.PointLights.Length + before.SpotLights.Length, Is.Zero);

        var policy = new ImportedLightShadowPolicy();
        policy.ApplyLightEdit(settings.Shadows, previous, light);
        policy.Apply(settings.Shadows, null);
        LocalShadowSelection selected = selector.Select([light], camera, settings.Shadows);
        var layout = new LocalShadowLayout();
        LocalShadowSelection admitted = layout.Plan(selected, default, settings.Shadows);

        Assert.Multiple(() =>
        {
            Assert.That(admitted.PointLights.Length + admitted.SpotLights.Length, Is.EqualTo(1));
            Assert.That(layout.ImageBytes, Is.GreaterThan(0));
            Assert.That(settings.Shadows.PointShadowsEnabled, Is.EqualTo(type == LightType.Point));
            Assert.That(settings.Shadows.SpotShadowsEnabled, Is.EqualTo(type == LightType.Spot));
            Assert.That(settings.Shadows.MaxShadowedPointLights, Is.EqualTo(pointLimit));
            Assert.That(settings.Shadows.MaxShadowedSpotLights, Is.EqualTo(spotLimit));
            Assert.That(settings.Shadows.LocalShadowMemoryBudgetMiB, Is.EqualTo(memoryBudget));
        });
    }

    [TestCase(LightType.Point)]
    [TestCase(LightType.Spot)]
    public void LightEdits_RespectGlobalDisableUntilShadowsAreExplicitlyRequested(LightType type)
    {
        var settings = new ShadowSettings { PointShadowsEnabled = false, SpotShadowsEnabled = false };
        var policy = new ImportedLightShadowPolicy();
        Light light = CreateLight(type);
        light.CastsShadows = false;
        policy.ApplyLightEdit(settings, default, light);
        Assert.That(settings.PointShadowsEnabled || settings.SpotShadowsEnabled, Is.False);

        light.CastsShadows = true;
        policy.ApplyLightEdit(settings, default, light);
        Assert.That(settings.PointShadowsEnabled || settings.SpotShadowsEnabled, Is.True,
            "Adding a light with shadows already requested must activate its pass.");

        settings.PointShadowsEnabled = settings.SpotShadowsEnabled = false;
        Light previous = light;
        light.Intensity += 1f;
        policy.ApplyLightEdit(settings, previous, light);
        Assert.That(settings.PointShadowsEnabled || settings.SpotShadowsEnabled, Is.False,
            "An unrelated edit must preserve an explicit renderer disable.");

        previous = light;
        light.Type = type == LightType.Point ? LightType.Spot : LightType.Point;
        policy.ApplyLightEdit(settings, previous, light);
        Assert.That(settings.PointShadowsEnabled, Is.EqualTo(light.Type == LightType.Point));
        Assert.That(settings.SpotShadowsEnabled, Is.EqualTo(light.Type == LightType.Spot));

        previous = light;
        light.CastsShadows = false;
        policy.ApplyLightEdit(settings, previous, light);
        Assert.That(settings.PointShadowsEnabled || settings.SpotShadowsEnabled, Is.True,
            "Turning off one light's shadows must leave the pass available to other lights.");
    }

    [TestCase(LightType.Point, 0, 16)]
    [TestCase(LightType.Point, 2, 0)]
    [TestCase(LightType.Spot, 0, 16)]
    [TestCase(LightType.Spot, 2, 0)]
    public void LightShadowEdit_PreservesExplicitZeroCapacityOrMemory(LightType type, int limit, int memoryBudget)
    {
        var settings = new ShadowSettings
        {
            PointShadowsEnabled = false, SpotShadowsEnabled = false,
            MaxShadowedPointLights = limit, MaxShadowedSpotLights = limit,
            LocalShadowMemoryBudgetMiB = memoryBudget
        };
        Light light = CreateLight(type);
        new ImportedLightShadowPolicy().ApplyLightEdit(settings, default, light);
        LocalShadowSelection selected = new LocalShadowSelector().Select([light], CreateCamera(), settings);
        LocalShadowSelection admitted = new LocalShadowLayout().Plan(selected, default, settings);

        Assert.Multiple(() =>
        {
            Assert.That(admitted.PointLights.Length + admitted.SpotLights.Length, Is.Zero);
            Assert.That(settings.MaxShadowedPointLights, Is.EqualTo(limit));
            Assert.That(settings.MaxShadowedSpotLights, Is.EqualTo(limit));
            Assert.That(settings.LocalShadowMemoryBudgetMiB, Is.EqualTo(memoryBudget));
        });
    }

    private static Light CreateLight(LightType type) => new()
    {
        Type = type, Color = Vector3.One, Direction = -Vector3.UnitY,
        Intensity = 10f, Range = 12f, SpotAngle = MathF.PI / 4f,
        ShadowStrength = 1f, ShadowNearPlane = 0.1f, ShadowFarPlane = 100f,
        CastsShadows = true
    };

    private static FirstPersonCamera CreateCamera()
    {
        var camera = new FirstPersonCamera(new Vec(0f, 0f, 8f), 0f, 0f);
        camera.Update();
        return camera;
    }
}
