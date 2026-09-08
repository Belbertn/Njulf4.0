using System.Numerics;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Box = Njulf.Core.Math.BoundingBox;
using Vec = Njulf.Core.Math.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class LocalShadowCapacityTests
{
    private static Light Light(LightType type, uint size = 512, int priority = 0) => new()
    {
        Type = type, Position = Vector3.Zero, Direction = -Vector3.UnitY,
        Range = 5, ShadowFarPlane = 5, ShadowNearPlane = .1f,
        Intensity = 10, Color = Vector3.One, CastsShadows = true,
        SpotAngle = 1, ShadowMapSizeOverride = size, ShadowPriority = priority
    };
    private static LocalShadowSelection Selection(params Light[] lights) => new()
    {
        PointLights = lights.Select((x, i) => new SelectedLocalShadow(i, x, 1)).Where(x => x.Light.Type == LightType.Point).ToArray(),
        SpotLights = lights.Select((x, i) => new SelectedLocalShadow(i, x, 1)).Where(x => x.Light.Type == LightType.Spot).ToArray(),
        PointCandidateCount = lights.Count(x => x.Type == LightType.Point),
        SpotCandidateCount = lights.Count(x => x.Type == LightType.Spot)
    };

    [TestCase(7, LightType.Point)] [TestCase(40, LightType.Point)] [TestCase(128, LightType.Point)]
    [TestCase(40, LightType.Spot)] [TestCase(128, LightType.Spot)]
    public void ConfiguredCapacityIsNotSceneSpecific(int count, LightType type)
    {
        var settings = new ShadowSettings { PointShadowsEnabled = true, SpotShadowsEnabled = true, MaxShadowedPointLights = count, MaxShadowedSpotLights = count };
        var camera = new Njulf.Core.Camera.FirstPersonCamera(new Vec(0, 0, 8), 0, 0);
        camera.Update();
        var lights = Enumerable.Range(0, count + 2).Select(_ => Light(type, 128)).ToArray();
        var selected = new LocalShadowSelector().Select(lights, camera, settings);
        Assert.That(selected.PointLights.Length + selected.SpotLights.Length, Is.EqualTo(count));
        var layout = new LocalShadowLayout();
        layout.Plan(selected, default, settings);
        Assert.That(layout.Points.Length + layout.Spots.Length, Is.EqualTo(count));
        Assert.That(layout.ImageBytes, Is.LessThan((ulong)settings.LocalShadowMemoryBudgetMiB * 1024 * 1024));
    }

    [TestCase(0)]
    [TestCase(37)]
    public void LightingModeChangesPreserveConfiguredLocalLimits(int limit)
    {
        var settings = new RenderSettings();
        settings.Shadows.MaxShadowedPointLights = limit;
        settings.Shadows.MaxShadowedSpotLights = limit;
        foreach (var mode in Enum.GetValues<NjulfHelloGame.SampleLightingMode>())
        {
            NjulfHelloGame.SampleLighting.ConfigureRenderSettings(settings, mode);
            Assert.That(settings.Shadows.MaxShadowedPointLights, Is.EqualTo(limit), mode.ToString());
            Assert.That(settings.Shadows.MaxShadowedSpotLights, Is.EqualTo(limit), mode.ToString());
        }
    }

    [Test]
    public void ShadowStatusDistinguishesCapacityFromInvalidConeAndMemory()
    {
        var settings = new ShadowSettings { PointShadowsEnabled = true, SpotShadowsEnabled = true, MaxShadowedPointLights = 0 };
        var point = Light(LightType.Point);
        Assert.That(LocalShadowLightDiagnostics.DescribeStatus(point, settings, false, false, false),
            Is.EqualTo("Point shadow count limit is 0"));
        settings.MaxShadowedPointLights = 13;
        Assert.That(LocalShadowLightDiagnostics.DescribeStatus(point, settings, false, false, false),
            Is.EqualTo("Point shadow count limit reached (13)"));
        var spot = Light(LightType.Spot); spot.SpotAngle = 0;
        Assert.That(LocalShadowLightDiagnostics.DescribeStatus(spot, settings, false, false, false),
            Is.EqualTo("Invalid spot cone angle"));
        Assert.That(LocalShadowLightDiagnostics.DescribeStatus(point, settings, true, false, false),
            Does.Contain("memory budget"));
        Assert.That(LocalShadowLightDiagnostics.DescribeStatus(point, settings, true, false, true),
            Is.EqualTo("GPU allocation failed"));
    }

    [Test]
    public void MemoryPressureReducesLowerPriorityResolutionBeforeDroppingLights()
    {
        var layout = new LocalShadowLayout();
        var high = Light(LightType.Point, 512, 10);
        var low = Light(LightType.Point, 512);
        layout.Plan(Selection(high, low), default, new ShadowSettings { LocalShadowMemoryBudgetMiB = 16 });
        Assert.Multiple(() => {
            Assert.That(layout.Points, Has.Length.EqualTo(2));
            Assert.That(layout.Points[0].Resolution, Is.EqualTo(512));
            Assert.That(layout.Points[1].Resolution, Is.LessThan(512));
            Assert.That(layout.Points[1].RequestedResolution, Is.EqualTo(512));
            Assert.That(low.ShadowMapSizeOverride, Is.EqualTo(512));
            Assert.That(layout.ImageBytes, Is.LessThan(16UL * 1024 * 1024));
        });
        layout.Plan(Selection(high, low), default, new ShadowSettings { LocalShadowMemoryBudgetMiB = 0 });
        Assert.That(layout.Points, Is.Empty);
    }

    [Test]
    public void VariableSpotRegionsDoNotOverlapAndSurvivePackedReordering()
    {
        var layout = new LocalShadowLayout();
        var lights = new[] { Light(LightType.Spot, 1024), Light(LightType.Spot, 512), Light(LightType.Spot, 128) };
        layout.Plan(Selection(lights), new uint[] { 10, 20, 30 }, new ShadowSettings());
        var before = layout.Spots.ToDictionary(x => x.Identity, x => x.Region);
        foreach (var a in layout.Spots)
        foreach (var b in layout.Spots.Where(x => x.Identity != a.Identity))
            Assert.That(a.Region.X + a.Region.Width <= b.Region.X || b.Region.X + b.Region.Width <= a.Region.X ||
                        a.Region.Y + a.Region.Height <= b.Region.Y || b.Region.Y + b.Region.Height <= a.Region.Y, Is.True);
        layout.Plan(Selection(lights.Reverse().ToArray()), new uint[] { 30, 20, 10 }, new ShadowSettings());
        foreach (var a in layout.Spots) Assert.That(a.Region, Is.EqualTo(before[a.Identity]));
    }

    [Test]
    public void CacheIgnoresShadingChangesButInvalidatesMovedLightAndForcedRefresh()
    {
        using var cache = new LocalShadowCache();
        var settings = new ShadowSettings();
        var layout = new LocalShadowLayout();
        var light = Light(LightType.Point);
        layout.Plan(Selection(light), default, settings);
        cache.Prepare(layout.Points, settings)[0].Commit(false);
        light.Color = Vector3.UnitX; light.Intensity = 20; light.ShadowStrength = .5f;
        settings.PointPcfRadius = 3; settings.PointNormalBias = .05f;
        layout.Plan(Selection(light), default, settings);
        Assert.That(cache.Prepare(layout.Points, settings)[0].StaticDirty, Is.False);
        light.Position = Vector3.UnitX;
        layout.Plan(Selection(light), default, settings);
        var entry = cache.Prepare(layout.Points, settings)[0];
        Assert.That(entry.StaticDirty, Is.True);
        entry.Commit(false); settings.LocalShadowCacheEnabled = false;
        Assert.That(cache.Prepare(layout.Points, settings)[0].StaticDirty, Is.True);
    }

    [Test]
    public void SkinnedAnimationPreservesStaticCacheAndDynamicRemovalIsRemembered()
    {
        using var scene = new Scene(); using var cache = new LocalShadowCache();
        var caster = new SkinnedRenderObject { SkinningEnabled = true };
        scene.Add(caster); cache.Attach(scene);
        var layout = new LocalShadowLayout(); var settings = new ShadowSettings();
        layout.Plan(Selection(Light(LightType.Point)), default, settings);
        var entry = cache.Prepare(layout.Points, settings)[0]; entry.Commit(true);
        caster.Position = new Vec(1, 0, 0);
        caster.SkinnedVertexOffset = 128;
        Assert.That(entry.StaticDirty, Is.False);
        Assert.That(cache.DynamicCasters(layout.Points)[0], Is.True);
        caster.Visible = false;
        Assert.That(cache.DynamicCasters(layout.Points)[0], Is.False);
        Assert.That(entry.HadDynamic, Is.True, "The pass must copy static depth once to remove the old dynamic shadow.");
        entry.Commit(false);
        Assert.That(entry.HadDynamic, Is.False);
    }

    [Test]
    public void SettingsSnapshotPreservesLocalShadowConfiguration()
    {
        var original = new RenderSettings();
        original.Shadows.MaxShadowedPointLights = 73;
        original.Shadows.MaxShadowedSpotLights = 61;
        original.Shadows.LocalShadowMemoryBudgetMiB = 149;
        original.Shadows.LocalShadowCacheEnabled = false;
        original.Shadows.PointShadowMapSize = 1024;
        original.Shadows.SpotShadowTileSize = 256;
        var snapshot = original.CreateSnapshot().Shadows;
        Assert.Multiple(() => {
            Assert.That(snapshot.MaxShadowedPointLights, Is.EqualTo(73));
            Assert.That(snapshot.MaxShadowedSpotLights, Is.EqualTo(61));
            Assert.That(snapshot.LocalShadowMemoryBudgetMiB, Is.EqualTo(149));
            Assert.That(snapshot.LocalShadowCacheEnabled, Is.False);
            Assert.That(snapshot.PointShadowMapSize, Is.EqualTo(1024));
            Assert.That(snapshot.SpotShadowTileSize, Is.EqualTo(256));
        });
    }

    [Test]
    public void MovingAndRemovingCasterInvalidateBothOldAndNewInfluence()
    {
        using var scene = new Scene(); using var cache = new LocalShadowCache();
        var caster = new RenderObject { LocalMeshBounds = new Box(new Vec(-1, -1, -1), new Vec(1, 1, 1)) };
        scene.Add(caster); cache.Attach(scene);
        var near = Light(LightType.Point); var far = near; far.Position = new Vector3(100, 0, 0);
        var layout = new LocalShadowLayout(); var settings = new ShadowSettings();
        layout.Plan(Selection(near, far), default, settings);
        var entries = cache.Prepare(layout.Points, settings);
        foreach (var entry in entries) entry.Commit(false);
        caster.Position = new Vec(100, 0, 0);
        Assert.That(entries.All(x => x.StaticDirty), Is.True);
        foreach (var entry in entries) entry.Commit(false);
        scene.Remove(caster);
        Assert.That(entries[0].StaticDirty, Is.False);
        Assert.That(entries[1].StaticDirty, Is.True);
    }
}
