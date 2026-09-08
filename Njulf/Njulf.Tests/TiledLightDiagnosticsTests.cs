using System.Numerics;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class TiledLightDiagnosticsTests
{
    [TestCase(false, false, DebugOverlayMode.None, false)]
    [TestCase(false, false, DebugOverlayMode.LightTiles, false)]
    [TestCase(false, true, DebugOverlayMode.None, false)]
    [TestCase(false, true, DebugOverlayMode.LightTiles, true)]
    [TestCase(true, false, DebugOverlayMode.None, true)]
    public void CollectsOnlyWhenExplicitlyRequestedOrLightTileOverlayIsActive(
        bool requested, bool debugEnabled, DebugOverlayMode overlay, bool expectedValid)
    {
        using var sceneData = CreateSceneData();
        sceneData.DebugToolingEnabled = debugEnabled;
        sceneData.DebugOverlayMode = overlay;

        VulkanRenderer.UpdateTiledLightDiagnostics(sceneData, CreateLights(), requested);

        AssertStatistics(sceneData, expectedValid);
        Assert.That(sceneData.LocalLightCount, Is.EqualTo(131));
        Assert.That(sceneData.MaxLightsPerTile, Is.EqualTo(64));
    }

    [Test]
    public void DisablingCollectionAndClearingSceneDiscardPreviousStatistics()
    {
        using var sceneData = CreateSceneData();
        LightFrameSnapshot lights = CreateLights();
        VulkanRenderer.UpdateTiledLightDiagnostics(sceneData, lights, true);
        AssertStatistics(sceneData, true);

        VulkanRenderer.UpdateTiledLightDiagnostics(sceneData, lights, false);
        AssertStatistics(sceneData, false);

        VulkanRenderer.UpdateTiledLightDiagnostics(sceneData, lights, true);
        sceneData.Clear();
        AssertStatistics(sceneData, false);
    }

    [Test]
    public void RequestedEmptySceneReportsValidZeroStatistics()
    {
        using var sceneData = CreateSceneData();
        VulkanRenderer.UpdateTiledLightDiagnostics(sceneData, CreateLights(), true);
        sceneData.LocalLightCount = 0;

        VulkanRenderer.UpdateTiledLightDiagnostics(sceneData, default, true);

        AssertStatistics(sceneData, true, hasLights: false);
    }

    [TestCase(0u, 68u, 64)]
    [TestCase(120u, 0u, 64)]
    [TestCase(120u, 68u, 0)]
    public void RequestedInvalidTileGridReportsUnavailable(uint width, uint height, int capacity)
    {
        using var sceneData = CreateSceneData();
        sceneData.TileCountX = width;
        sceneData.TileCountY = height;
        sceneData.MaxLightsPerTile = capacity;

        VulkanRenderer.UpdateTiledLightDiagnostics(sceneData, CreateLights(), true);

        AssertStatistics(sceneData, false);
    }

    private static SceneRenderingData CreateSceneData() => new()
    {
        ScreenWidth = 1920,
        ScreenHeight = 1080,
        TileCountX = 120,
        TileCountY = 68,
        MaxLightsPerTile = 64,
        LocalLightCount = 131
    };

    private static LightFrameSnapshot CreateLights()
    {
        var lights = new Light[132];
        for (int i = 0; i < 128; i++)
        {
            lights[i] = new Light
            {
                Type = LightType.Point,
                Position = Vector3.Zero,
                Range = 100f,
                Intensity = 1f
            };
        }

        // Invalid ranges exercise all three rejection counters. A directional
        // light must not contribute to the local-light histogram.
        lights[128] = new Light { Type = LightType.Point, Intensity = 1f };
        lights[129] = new Light { Type = LightType.Spot, Intensity = 1f };
        lights[130] = new Light { Type = LightType.Rectangle, Intensity = 1f };
        lights[131] = new Light { Type = LightType.Directional, Intensity = 1f };
        return new LightFrameSnapshot(lights, lights.Length, 1, 131, -1, default, 1);
    }

    private static void AssertStatistics(SceneRenderingData sceneData, bool valid, bool hasLights = true)
    {
        bool populated = valid && hasLights;
        Assert.Multiple(() =>
        {
            Assert.That(sceneData.TiledLightDiagnosticsValid, Is.EqualTo(valid));
            Assert.That(sceneData.MaxLightsInAnyTile, Is.EqualTo(populated ? 128 : 0));
            Assert.That(sceneData.AverageLightsPerNonEmptyTile, Is.EqualTo(populated ? 128f : 0f));
            Assert.That(sceneData.LightTileSaturationCount, Is.EqualTo(populated ? 8160 : 0));
            Assert.That(sceneData.LightCullRejectedPointCount, Is.EqualTo(populated ? 1 : 0));
            Assert.That(sceneData.LightCullRejectedSpotCount, Is.EqualTo(populated ? 1 : 0));
            Assert.That(sceneData.LightCullRejectedAreaCount, Is.EqualTo(populated ? 1 : 0));
        });
    }
}