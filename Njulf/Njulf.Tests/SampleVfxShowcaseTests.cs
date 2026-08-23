using System.IO;
using System.Linq;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleVfxShowcaseTests
{
    [Test]
    public void DefaultStartup_PreservesSelectedDdgiHighFroxelProfile()
    {
        var settings = new RenderSettings();

        SampleVfxShowcaseScene.ConfigurePreInitializationSettings(
            settings,
            explicitQualityPreset: null);

        SimpleDdgiAuthoredVolume authored = settings.GlobalIllumination
            .SimpleDdgiAuthoredVolumes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(settings.Fog.Enabled, Is.True);
            Assert.That(settings.Fog.Technique, Is.EqualTo(FogTechnique.Froxel));
            Assert.That(settings.Fog.Mode, Is.EqualTo(FogMode.Height));
            Assert.That(settings.Reflections.Enabled, Is.False);
            Assert.That(settings.Fog.Volumetric.SingleScatteringQualified, Is.True);
            Assert.That(settings.Fog.Volumetric.MultipleScatteringQualified, Is.False);
            Assert.That(settings.Fog.Volumetric.MultipleScatteringIterations, Is.Zero);
            Assert.That(settings.Fog.Volumetric.MaxDistance, Is.EqualTo(36f));
            Assert.That(settings.AutoExposure.Enabled, Is.False);
            Assert.That(settings.Exposure, Is.EqualTo(0.05f));
            Assert.That(settings.GlobalIllumination.Enabled, Is.True);
            Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
            Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiDirectionalRadianceMode,
                Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
            Assert.That(authored.Spacing, Is.EqualTo(1f));
            Assert.That(authored.Priority, Is.EqualTo(10));
            Assert.That(authored.Purpose, Is.EqualTo(SimpleDdgiVolumePurpose.ReceiverHero));
        });
    }

    [TestCase(RenderQualityPreset.High, true, false, 0)]
    [TestCase(RenderQualityPreset.DdgiHigh, true, false, 0)]
    [TestCase(RenderQualityPreset.Ultra, true, true, 2)]
    [TestCase(RenderQualityPreset.Medium, false, false, 0)]
    public void ExplicitQuality_RemainsAuthoritativeWhileFogIntentIsPreserved(
        RenderQualityPreset preset,
        bool expectedSingleScatteringQualification,
        bool expectedMultipleScatteringQualification,
        int expectedMultipleScatteringIterations)
    {
        var settings = new RenderSettings();

        SampleVfxShowcaseScene.ConfigurePreInitializationSettings(
            settings,
            preset);

        Assert.Multiple(() =>
        {
            Assert.That(settings.QualityPreset, Is.EqualTo(preset));
            Assert.That(settings.Fog.Enabled, Is.True);
            Assert.That(settings.Fog.Technique, Is.EqualTo(FogTechnique.Froxel));
            Assert.That(
                settings.Fog.Volumetric.SingleScatteringQualified,
                Is.EqualTo(expectedSingleScatteringQualification));
            Assert.That(
                settings.Fog.Volumetric.MultipleScatteringQualified,
                Is.EqualTo(expectedMultipleScatteringQualification));
            Assert.That(
                settings.Fog.Volumetric.MultipleScatteringIterations,
                Is.EqualTo(expectedMultipleScatteringIterations));
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiAuthoredVolumes,
                Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void AdvancedGiOverride_RemainsAuthoritativeAfterFogPostConfiguration()
    {
        var settings = new RenderSettings();
        SampleVfxShowcaseScene.ConfigurePreInitializationSettings(
            settings,
            RenderQualityPreset.Ultra);

        settings.GlobalIllumination.SimpleDdgiReceiverFeedbackMode =
            SimpleDdgiReceiverFeedbackMode.ExactCompacted;
        SampleVfxShowcaseScene.ApplyPostQualityPreset(settings);

        Assert.Multiple(() =>
        {
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(settings.Fog.Enabled, Is.True);
            Assert.That(settings.Fog.Technique, Is.EqualTo(FogTechnique.Froxel));
        });
    }

    [Test]
    public void DemoOverride_RestoresExposureWithoutOwningQualification()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.Ultra);
        settings.AutoExposure.Enabled = true;
        settings.Exposure = 0.72f;
        settings.Fog.DebugView = FogDebugView.Density;
        settings.Fog.Volumetric.DebugProjection = FogDebugProjection.Slice;
        settings.Fog.Volumetric.DebugSlice = 17;
        var demoOverride = new SampleVfxVolumetricDemoOverride();

        demoOverride.Enter(settings);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Fog.DebugView, Is.EqualTo(FogDebugView.None));
            Assert.That(settings.Fog.Volumetric.DebugProjection,
                Is.EqualTo(FogDebugProjection.MaxAlongRay));
            Assert.That(settings.Fog.Volumetric.DebugSlice, Is.EqualTo(-1));
        });

        // An explicit choice made while the scene is active must survive the
        // post-quality reapplication path.
        settings.Fog.DebugView = FogDebugView.DirectRadiance;
        settings.Fog.Volumetric.DebugProjection = FogDebugProjection.Surface;
        settings.ApplyQualityPreset(RenderQualityPreset.High);
        settings.Fog.DebugView = FogDebugView.DirectRadiance;
        settings.Fog.Volumetric.DebugProjection = FogDebugProjection.Surface;
        demoOverride.Apply(settings);

        Assert.Multiple(() =>
        {
            Assert.That(demoOverride.Active, Is.True);
            Assert.That(settings.Fog.Volumetric.SingleScatteringQualified, Is.True);
            Assert.That(settings.Fog.Volumetric.MultipleScatteringQualified, Is.False);
            Assert.That(settings.Fog.Volumetric.MultipleScatteringIterations, Is.Zero);
            Assert.That(settings.AutoExposure.Enabled, Is.False);
            Assert.That(settings.Exposure, Is.EqualTo(0.05f));
            Assert.That(settings.Fog.DebugView,
                Is.EqualTo(FogDebugView.DirectRadiance));
            Assert.That(settings.Fog.Volumetric.DebugProjection,
                Is.EqualTo(FogDebugProjection.Surface));
        });

        demoOverride.Exit(settings);

        Assert.Multiple(() =>
        {
            Assert.That(demoOverride.Active, Is.False);
            Assert.That(settings.Fog.Volumetric.SingleScatteringQualified, Is.True);
            Assert.That(settings.AutoExposure.Enabled, Is.True);
            Assert.That(settings.Exposure, Is.EqualTo(0.72f));
            Assert.That(settings.Fog.DebugView, Is.EqualTo(FogDebugView.Density));
            Assert.That(settings.Fog.Volumetric.DebugProjection,
                Is.EqualTo(FogDebugProjection.Slice));
            Assert.That(settings.Fog.Volumetric.DebugSlice, Is.EqualTo(17));
        });
    }

    [Test]
    public void DensityVolumes_AreDeterministicAndExerciseBoxSphereNoiseAndFlow()
    {
        using var scene = new Scene();

        SampleVfxShowcaseScene.ConfigureDensityVolumes(scene);

        VolumetricDensityVolume ground = scene.VolumetricDensityVolumes.Single(
            volume => volume.Name.EndsWith("GroundMist"));
        VolumetricDensityVolume haze = scene.VolumetricDensityVolumes.Single(
            volume => volume.Name.EndsWith("SpotlightHaze"));
        VolumetricDensityVolume smoke = scene.VolumetricDensityVolumes.Single(
            volume => volume.Name.EndsWith("DenseSmokeBank"));
        Assert.Multiple(() =>
        {
            Assert.That(scene.VolumetricDensityVolumes, Has.Count.EqualTo(3));
            Assert.That(
                scene.VolumetricDensityVolumes
                    .Select(volume => volume.Id)
                    .Distinct()
                    .Count(),
                Is.EqualTo(3));
            Assert.That(ground.Shape, Is.EqualTo(VolumetricDensityVolumeShape.Box));
            Assert.That(haze.Shape, Is.EqualTo(VolumetricDensityVolumeShape.Box));
            Assert.That(smoke.Shape, Is.EqualTo(VolumetricDensityVolumeShape.Sphere));
            Assert.That(smoke.ExtinctionPerMeter, Is.EqualTo(0.32f));
            Assert.That(
                scene.VolumetricDensityVolumes.All(volume =>
                    volume.NoiseStrength > 0f &&
                    volume.FlowVelocity.LengthSquared() > 0f),
                Is.True);
        });
    }

    [Test]
    public void ParticleEffects_InjectSmokeAndDustAndPublishStableEmissiveSources()
    {
        using var scene = new Scene();

        SampleVfxEffects.Configure(scene);

        ParticleEffect fire = scene.ParticleEffects.Single(
            instance => instance.Effect.Name == "Vfx.FirePit").Effect;
        ParticleEffect impact = scene.ParticleEffects.Single(
            instance => instance.Effect.Name == "Vfx.ImpactBurst").Effect;
        ParticleEffect smokeBank = scene.ParticleEffects.Single(
            instance => instance.Effect.Name == "Vfx.SmokeBank").Effect;
        ParticleEffect orb = scene.ParticleEffects.Single(
            instance => instance.Effect.Name == "Vfx.MagicOrb").Effect;
        ParticleEmitterDefinition flame = fire.Emitters.Single(
            emitter => emitter.Name == "Flame");
        ParticleEmitterDefinition smoke = fire.Emitters.Single(
            emitter => emitter.Name == "Smoke");
        ParticleEmitterDefinition sparks = fire.Emitters.Single(
            emitter => emitter.Name == "Sparks");
        ParticleEmitterDefinition dust = impact.Emitters.Single(
            emitter => emitter.Name == "Dust");
        ParticleEmitterDefinition glow = orb.Emitters.Single();
        ParticleEmitterDefinition denseSmoke = smokeBank.Emitters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(smoke.VolumetricInjectionEnabled, Is.True);
            Assert.That(smoke.VolumetricDensity, Is.EqualTo(0.14f));
            Assert.That(dust.VolumetricInjectionEnabled, Is.True);
            Assert.That(dust.VolumetricDensity, Is.EqualTo(0.11f));
            Assert.That(sparks.VolumetricInjectionEnabled, Is.False);
            Assert.That(denseSmoke.VolumetricInjectionEnabled, Is.True);
            Assert.That(denseSmoke.VolumetricDensity, Is.EqualTo(0.22f));
            Assert.That(dust.Looping, Is.True);
            Assert.That(flame.GlobalIlluminationEmission, Is.EqualTo(ParticleGiEmissionMode.Force));
            Assert.That(flame.GlobalIlluminationSourceShape, Is.EqualTo(ParticleGiSourceShape.Cone));
            Assert.That(flame.GlobalIlluminationPower.LengthSquared(), Is.GreaterThan(0f));
            Assert.That(glow.GlobalIlluminationEmission, Is.EqualTo(ParticleGiEmissionMode.Force));
            Assert.That(glow.GlobalIlluminationSourceShape, Is.EqualTo(ParticleGiSourceShape.Disk));
            Assert.That(glow.GlobalIlluminationPower.LengthSquared(), Is.GreaterThan(0f));
        });
    }

    [Test]
    public void UntexturedShowcaseParticles_UseSoftCoverageAndCorrectBlendOwnership()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string vertex = File.ReadAllText(Path.Combine(
            shaderDirectory, "particle.vert"));
        string fragment = File.ReadAllText(Path.Combine(
            shaderDirectory, "particle.frag"));

        Assert.Multiple(() =>
        {
            Assert.That(vertex, Does.Contain("outLocalUv"));
            Assert.That(vertex,
                Does.Contain("particle.TextureIndex == uint(DEFAULT_WHITE_TEXTURE)"));
            Assert.That(fragment,
                Does.Contain("inTextureIndex == uint(DEFAULT_WHITE_TEXTURE)"));
            Assert.That(fragment,
                Does.Contain("hdr *= color.a"));
        });
    }

    [Test]
    public void VolumetricLightingMode_CoversEveryDirectLightClassAndLocalShadowFamily()
    {
        var settings = new RenderSettings();

        SampleLighting.ConfigureRenderSettings(
            settings,
            SampleLightingMode.VolumetricShowcase);
        var lights = SampleLighting.CreateVolumetricShowcaseLights();

        Assert.Multiple(() =>
        {
            Assert.That(settings.Shadows.DirectionalShadowsEnabled, Is.True);
            Assert.That(settings.Shadows.SpotShadowsEnabled, Is.True);
            Assert.That(settings.Shadows.PointShadowsEnabled, Is.True);
            Assert.That(settings.Shadows.MaxShadowedSpotLights, Is.GreaterThanOrEqualTo(1));
            Assert.That(settings.Shadows.MaxShadowedPointLights, Is.GreaterThanOrEqualTo(1));
            Assert.That(lights.Any(light => light.Type == LightType.Directional), Is.True);
            Assert.That(lights.Any(light => light.Type == LightType.Spot), Is.True);
            Assert.That(lights.Any(light => light.Type == LightType.Point), Is.True);
            Assert.That(
                lights.Any(light => light.Type == LightType.Spot && light.CastsShadows),
                Is.True);
            Assert.That(
                lights.Any(light => light.Type == LightType.Point && light.CastsShadows),
                Is.True);
        });
    }

    private static string FindRepoDirectory(string name)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new AssertionException($"Could not find repo directory '{name}'.");
    }
}
