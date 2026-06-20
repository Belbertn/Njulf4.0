using System;
using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

internal enum SampleLightingMode
{
    DirectionalKey,
    ThreePointDemo,
    SpotShadowDemo,
    PointShadowDemo
}

internal static class SampleLighting
{
    public static void ConfigureRenderSettings(RenderSettings settings, SampleLightingMode mode)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        settings.Shadows.DirectionalShadowsEnabled = true;
        settings.Shadows.SpotShadowsEnabled = true;
        settings.Shadows.MaxShadowedSpotLights = Math.Max(settings.Shadows.MaxShadowedSpotLights, 2);
        settings.Shadows.PointShadowsEnabled = true;
        settings.Shadows.MaxShadowedPointLights = Math.Max(settings.Shadows.MaxShadowedPointLights, 1);
    }

    public static void Configure(LightManager lightManager, SampleLightingMode mode)
    {
        if (lightManager == null)
            throw new ArgumentNullException(nameof(lightManager));

        lightManager.ClearLights();

        switch (mode)
        {
            case SampleLightingMode.DirectionalKey:
                AddDirectionalKey(lightManager);
                break;
            case SampleLightingMode.ThreePointDemo:
                AddThreePointDemo(lightManager);
                break;
            case SampleLightingMode.SpotShadowDemo:
                AddSpotShadowDemo(lightManager);
                break;
            case SampleLightingMode.PointShadowDemo:
                AddPointShadowDemo(lightManager);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sample lighting mode.");
        }
    }

    private static void AddDirectionalKey(LightManager lightManager)
    {
        lightManager.AddLight(new Light
        {
            Type = LightType.Directional,
            Direction = new Vector3(0.0f, -0.5f, -1.0f),
            Color = new Vector3(0.7f, 0.7f, 0.7f),
            Intensity = 12f,
            Range = 10f,
            CastsShadows = true,
            ShadowStrength = 0.75f,
            ShadowPriority = 10
        });
    }

    private static void AddThreePointDemo(LightManager lightManager)
    {
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new Vector3(-2.5f, 2.6f, 3.0f),
            Color = new Vector3(1.0f, 0.82f, 0.58f),
            Intensity = 22f,
            Range = 8f
        });
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new Vector3(2.5f, 1.4f, 1.5f),
            Color = new Vector3(0.45f, 0.68f, 1.0f),
            Intensity = 12f,
            Range = 6f
        });
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new Vector3(0.0f, 3.0f, -2.75f),
            Color = new Vector3(0.7f, 1.0f, 0.72f),
            Intensity = 8f,
            Range = 7f
        });
    }

    private static void AddSpotShadowDemo(LightManager lightManager)
    {
        AddDirectionalKey(lightManager);
        lightManager.AddLight(new Light
        {
            Type = LightType.Spot,
            Position = new Vector3(-2.4f, 4.0f, 2.4f),
            Direction = Vector3.Normalize(new Vector3(0.70f, -1.0f, -0.45f)),
            Color = new Vector3(1.0f, 0.78f, 0.52f),
            Intensity = 45f,
            Range = 12f,
            SpotAngle = MathF.PI / 6f,
            CastsShadows = true,
            ShadowStrength = 0.9f,
            ShadowPriority = 10
        });
        lightManager.AddLight(new Light
        {
            Type = LightType.Spot,
            Position = new Vector3(2.5f, 3.2f, -1.5f),
            Direction = Vector3.Normalize(new Vector3(-0.3f, -0.4f, 0.35f)),
            Color = new Vector3(0.48f, 0.68f, 1.0f),
            Intensity = 24f,
            Range = 10f,
            SpotAngle = MathF.PI / 7f,
            CastsShadows = true,
            ShadowStrength = 0.75f,
            ShadowPriority = 4
        });
    }

    private static void AddPointShadowDemo(LightManager lightManager)
    {
        AddDirectionalKey(lightManager);
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new Vector3(0.0f, 2.6f, 0.2f),
            Color = new Vector3(1.0f, 0.72f, 0.45f),
            Intensity = 45f,
            Range = 9f,
            CastsShadows = true,
            ShadowStrength = 0.9f,
            ShadowPriority = 10
        });
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new Vector3(-3.0f, 1.5f, 3.0f),
            Color = new Vector3(0.42f, 0.62f, 1.0f),
            Intensity = 12f,
            Range = 6f,
            CastsShadows = false
        });
    }
}
