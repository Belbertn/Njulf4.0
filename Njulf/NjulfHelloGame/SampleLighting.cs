using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Njulf.Assets.Scenes;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

internal enum SampleLightingMode
{
    DirectionalKey,
    ThreePointDemo,
    SpotShadowDemo,
    PointShadowDemo,
    AnalyticalAreaLightShowcase,
    VolumetricShowcase
}

internal static class SampleLighting
{
    internal const string AnalyticalAreaLightIesFileName =
        "area-light-room-cross.ies";

    public static void ConfigureRenderSettings(RenderSettings settings, SampleLightingMode mode)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        bool areaLightShowcase = mode ==
            SampleLightingMode.AnalyticalAreaLightShowcase;
        settings.Shadows.DirectionalShadowsEnabled = !areaLightShowcase;
        bool spotShadows = mode is SampleLightingMode.SpotShadowDemo or
            SampleLightingMode.VolumetricShowcase;
        bool pointShadows = mode is SampleLightingMode.PointShadowDemo or
            SampleLightingMode.VolumetricShowcase;
        settings.Shadows.SpotShadowsEnabled = spotShadows;
        settings.Shadows.PointShadowsEnabled = pointShadows;
        settings.Shadows.MaxShadowedSpotLights = spotShadows
            ? Math.Max(settings.Shadows.MaxShadowedSpotLights, 2)
            : 0;
        settings.Shadows.MaxShadowedPointLights = pointShadows
            ? Math.Max(settings.Shadows.MaxShadowedPointLights, 1)
            : 0;
        settings.Shadows.AreaShadowsEnabled = areaLightShowcase;
        settings.Shadows.MaxShadowedAreaLights = areaLightShowcase ? 3 : 0;
        settings.Shadows.AreaShadowSampleCount = areaLightShowcase ? 2 : 1;
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
            case SampleLightingMode.AnalyticalAreaLightShowcase:
                foreach (Light light in CreateAnalyticalAreaLightShowcaseLights())
                {
                    lightManager.AddLightHandle(
                        light,
                        $"AreaLightRoom.{light.Type}");
                }
                AddAnalyticalAreaLightIesShowcase(lightManager);
                break;
            case SampleLightingMode.VolumetricShowcase:
                foreach (Light light in CreateVolumetricShowcaseLights())
                    lightManager.AddLight(light);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sample lighting mode.");
        }
    }

    private static void AddDirectionalKey(LightManager lightManager)
    {
        lightManager.AddLight(SampleSponzaLightingProfile.CreateDirectionalKey());
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

    internal static IReadOnlyList<Light> CreateVolumetricShowcaseLights()
    {
        return
        [
            SampleSponzaLightingProfile.CreateDirectionalKey(),
            new Light
            {
                Type = LightType.Spot,
                // Place the key behind the pillar and aim it toward the open
                // camera side. The pillar therefore cuts a readable shadow
                // through the authored haze volume.
                Position = new Vector3(-0.75f, 3.85f, -2.45f),
                Direction = Vector3.Normalize(new Vector3(0.16f, -0.28f, 0.95f)),
                Color = new Vector3(1.0f, 0.78f, 0.52f),
                Intensity = 72f,
                Range = 10f,
                SpotAngle = MathF.PI / 6f,
                CastsShadows = true,
                ShadowStrength = 0.92f,
                ShadowPriority = 12
            },
            new Light
            {
                Type = LightType.Point,
                Position = new Vector3(-2.2f, 1.1f, 0.6f),
                Color = new Vector3(1.0f, 0.28f, 0.05f),
                Intensity = 34f,
                Range = 4.5f,
                CastsShadows = true,
                ShadowStrength = 0.85f,
                ShadowPriority = 10
            },
            new Light
            {
                Type = LightType.Point,
                Position = new Vector3(2f, 1.25f, 0.2f),
                Color = new Vector3(0.18f, 0.65f, 1.0f),
                Intensity = 24f,
                Range = 4f,
                CastsShadows = false
            }
        ];
    }

    internal static IReadOnlyList<Light> CreateAnalyticalAreaLightShowcaseLights()
    {
        return
        [
            new Light
            {
                Type = LightType.Rectangle,
                Position = new Vector3(-2.75f, 4.82f, -0.55f),
                Direction = -Vector3.UnitY,
                Up = -Vector3.UnitZ,
                Size = new Vector2(2.15f, 1.25f),
                Color = new Vector3(1.0f, 0.67f, 0.34f),
                Intensity = 4.2f,
                Range = 8.5f,
                CastsShadows = true,
                ShadowStrength = 0.94f,
                ShadowPriority = 30
            },
            new Light
            {
                Type = LightType.Disk,
                Position = new Vector3(2.75f, 3.35f, -3.86f),
                Direction = Vector3.Normalize(new Vector3(0f, -0.20f, 1f)),
                Up = Vector3.UnitY,
                Size = new Vector2(1.25f, 1.25f),
                Color = new Vector3(0.25f, 0.52f, 1.0f),
                Intensity = 5.8f,
                Range = 9.0f,
                CastsShadows = true,
                ShadowStrength = 0.92f,
                ShadowPriority = 20
            },
            new Light
            {
                Type = LightType.Tube,
                Position = new Vector3(0f, 4.02f, 0.65f),
                Direction = Vector3.UnitX,
                Up = Vector3.UnitY,
                Size = new Vector2(2.5f, 0.16f),
                Color = new Vector3(0.18f, 1.0f, 0.66f),
                Intensity = 3.8f,
                Range = 7.5f,
                CastsShadows = true,
                ShadowStrength = 0.90f,
                ShadowPriority = 10
            }
        ];
    }

    internal static Light CreateAnalyticalAreaLightIesShowcaseLight(
        PhotometricProfileHandle profile) => new()
    {
        Type = LightType.Spot,
        Position = new Vector3(0f, 4.55f, 2.65f),
        Direction = Vector3.Normalize(new Vector3(0f, -1.70f, -6.53f)),
        Up = Vector3.UnitY,
        Color = new Vector3(1.0f, 0.90f, 0.72f),
        Intensity = 12f,
        Range = 11.5f,
        SpotAngle = 0.78f,
        CastsShadows = false,
        ShadowStrength = 0f,
        ShadowPriority = 0,
        PhotometricProfile = profile
    };

    private static void AddAnalyticalAreaLightIesShowcase(
        LightManager lightManager)
    {
        string profilePath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Photometry",
            AnalyticalAreaLightIesFileName);
        var source = new SceneAssetReferenceDocument(profilePath);
        PhotometricProfileHandle profile = default;
        bool loaded = lightManager.PhotometricProfiles?.TryResolve(
            source,
            out profile) == true;

        lightManager.AddLightHandle(
            CreateAnalyticalAreaLightIesShowcaseLight(profile),
            "AreaLightRoom.IES.CrossProfileSpot");

        if (loaded)
        {
            Console.WriteLine(
                $"Analytical area-light room IES profile: {profilePath}");
        }
        else
        {
            Console.Error.WriteLine(
                $"Analytical area-light room IES profile was unavailable: " +
                $"{profilePath}. The demonstration spot uses its unit profile.");
        }
    }
}
