using System;
using Njulf.Rendering;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

internal enum SampleEnvironmentMode
{
    ProceduralOutdoor,
    StudioNeutral,
    HdrAsset,
    Disabled
}

internal static class SampleEnvironment
{
    public static void Configure(VulkanRenderer renderer, SampleEnvironmentMode mode)
    {
        if (renderer == null)
            throw new ArgumentNullException(nameof(renderer));

        EnvironmentSettings environment = renderer.Settings.Environment;
        environment.Enabled = true;
        environment.SourcePath = null;
        environment.DebugView = EnvironmentDebugView.None;
        environment.RotationRadians = 0.0f;

        switch (mode)
        {
            case SampleEnvironmentMode.ProceduralOutdoor:
                environment.SourceKind = EnvironmentSourceKind.ProceduralSky;
                environment.SkyIntensity = 1.0f;
                environment.DiffuseIntensity = 1.0f;
                environment.SpecularIntensity = 1.0f;
                break;
            case SampleEnvironmentMode.StudioNeutral:
                environment.SourceKind = EnvironmentSourceKind.ProceduralSky;
                environment.SkyIntensity = 0.45f;
                environment.DiffuseIntensity = 0.75f;
                environment.SpecularIntensity = 0.85f;
                break;
            case SampleEnvironmentMode.HdrAsset:
                environment.SourceKind = EnvironmentSourceKind.HdrEquirectangular;
                environment.SourcePath = "textures/kloppenheim_05_4k.hdr";
                environment.SkyIntensity = 1.0f;
                environment.DiffuseIntensity = 1.0f;
                environment.SpecularIntensity = 1.0f;
                break;
            case SampleEnvironmentMode.Disabled:
                environment.Enabled = false;
                environment.SourceKind = EnvironmentSourceKind.ProceduralSky;
                environment.SkyIntensity = 0.0f;
                environment.DiffuseIntensity = 0.0f;
                environment.SpecularIntensity = 0.0f;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sample environment mode.");
        }
    }
}
