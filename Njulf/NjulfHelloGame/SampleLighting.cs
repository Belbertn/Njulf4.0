using System;
using System.Numerics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

internal enum SampleLightingMode
{
    DirectionalKey,
    ThreePointDemo
}

internal static class SampleLighting
{
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
            Range = 10f
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
}
