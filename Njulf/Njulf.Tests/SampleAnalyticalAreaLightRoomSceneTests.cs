using System.IO;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleAnalyticalAreaLightRoomSceneTests
{
    [Test]
    public void LightRig_ContainsValidShadowCastingRectangleDiskAndTube()
    {
        Light[] lights = SampleLighting
            .CreateAnalyticalAreaLightShowcaseLights()
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(lights, Has.Length.EqualTo(3));
            Assert.That(lights.Select(light => light.Type), Is.EquivalentTo(new[]
            {
                LightType.Rectangle,
                LightType.Disk,
                LightType.Tube
            }));
            Assert.That(lights, Has.All.Matches<Light>(light =>
                light.CastsShadows &&
                AnalyticalLightGeometry.HasValidDimensions(light) &&
                AnalyticalLightGeometry.TryGetFrame(
                    light,
                    out _,
                    out _,
                    out _) &&
                AnalyticalLightGeometry.ComputePowerWeight(light) > 0f));
        });
    }

    [Test]
    public void RenderProfile_EnablesAreaShadowsAndDdgiHitLighting()
    {
        var settings = new RenderSettings();

        SampleAnalyticalAreaLightRoomScene.ConfigureRenderSettings(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Shadows.AreaShadowsEnabled, Is.True);
            Assert.That(settings.Shadows.MaxShadowedAreaLights, Is.EqualTo(3));
            Assert.That(settings.Shadows.AreaShadowSampleCount, Is.EqualTo(2));
            Assert.That(settings.Shadows.DirectionalShadowsEnabled, Is.False);
            Assert.That(settings.Shadows.SpotShadowsEnabled, Is.False);
            Assert.That(settings.Shadows.PointShadowsEnabled, Is.False);
            Assert.That(settings.GlobalIllumination.Enabled, Is.True);
            Assert.That(settings.GlobalIllumination.Mode,
                Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
            Assert.That(settings.GlobalIllumination.SimpleDdgiAuthoredVolumes,
                Has.Count.EqualTo(1));
            Assert.That(settings.Environment.Enabled, Is.True);
            Assert.That(settings.Environment.DiffuseIntensity,
                Is.GreaterThan(0f));
            Assert.That(settings.Environment.SpecularIntensity,
                Is.GreaterThan(0f));
            Assert.That(settings.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
        });
    }

    [Test]
    public void IesShowcase_UsesAValidBundledTypeCProfile()
    {
        string profilePath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Photometry",
            SampleLighting.AnalyticalAreaLightIesFileName);
        IesPhotometricProfile profile = IesPhotometricProfileParser.Parse(
            File.ReadAllText(profilePath));
        var handle = new PhotometricProfileHandle(
            1,
            BindlessIndex.FirstDynamicTextureIndex,
            1);
        Light light = SampleLighting
            .CreateAnalyticalAreaLightIesShowcaseLight(handle);

        Assert.Multiple(() =>
        {
            Assert.That(profile.PeakCandela, Is.EqualTo(1000f));
            Assert.That(profile.Evaluate(0f, 12f),
                Is.GreaterThan(profile.Evaluate(45f, 12f)));
            Assert.That(light.Type, Is.EqualTo(LightType.Spot));
            Assert.That(light.PhotometricProfile, Is.EqualTo(handle));
            Assert.That(light.PhotometricProfile.IsValid, Is.True);
            Assert.That(light.CastsShadows, Is.False);
        });
    }

    [Test]
    public void BoxMesh_TriangleWindingMatchesAuthoredNormals()
    {
        GPUVertex[] vertices = SampleAnalyticalAreaLightRoomScene
            .CreateBoxVertices();
        uint[] indices = SampleAnalyticalAreaLightRoomScene
            .CreateBoxIndices();

        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            GPUVertex vertex0 = vertices[indices[triangle]];
            GPUVertex vertex1 = vertices[indices[triangle + 1]];
            GPUVertex vertex2 = vertices[indices[triangle + 2]];
            Njulf.Core.Math.Vector3 geometricNormal =
                Njulf.Core.Math.Vector3.Cross(
                    vertex1.Position - vertex0.Position,
                    vertex2.Position - vertex0.Position);

            Assert.That(
                Njulf.Core.Math.Vector3.Dot(
                    geometricNormal,
                    vertex0.Normal),
                Is.GreaterThan(0f),
                $"Triangle {triangle / 3} is wound opposite its authored normal.");
        }
    }

    [Test]
    public void CameraPreset_FramesTheOpenFrontOfTheRoom()
    {
        var preset = HelloGame.GetCameraPreset(
            SampleSceneKind.AnalyticalAreaLights);

        Assert.Multiple(() =>
        {
            Assert.That(preset.Position,
                Is.EqualTo(new Njulf.Core.Math.Vector3(0f, 2.15f, 8.25f)));
            Assert.That(preset.Yaw, Is.Zero);
            Assert.That(preset.Pitch, Is.EqualTo(-0.08f));
            Assert.That(preset.FarPlane, Is.EqualTo(60f));
        });
    }

    [Test]
    public void Key3SceneCycle_VisitsTheAreaLightRoom()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                HelloGame.GetNextKey3Scene(SampleSceneKind.MaterialShowcase),
                Is.EqualTo(SampleSceneKind.AnalyticalAreaLights));
            Assert.That(
                HelloGame.GetNextKey3Scene(SampleSceneKind.AnalyticalAreaLights),
                Is.EqualTo(SampleSceneKind.FoliageShowcase));
        });
    }
}
