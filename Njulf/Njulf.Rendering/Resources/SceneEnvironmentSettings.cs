using Njulf.Core.Scene;
using Njulf.Rendering.Data;
namespace Njulf.Rendering.Resources;

internal static class SceneEnvironmentSettings
{
    public static SceneEnvironment Capture(EnvironmentSettings source) => new()
    {
        Enabled = source.Enabled,
        SourceKind = (SceneEnvironmentSource)source.SourceKind,
        SourcePath = source.SourcePath,
        SunDriver = (SceneSunDriver)source.SunDriver,
        AnimateTimeOfDay = source.AnimateTimeOfDay,
        Turbidity = source.Turbidity,
        GroundAlbedo = new(source.GroundAlbedo.X, source.GroundAlbedo.Y, source.GroundAlbedo.Z),
        SunAngularDiameterDegrees = source.SunAngularDiameterDegrees,
        MoonAngularDiameterDegrees = source.MoonAngularDiameterDegrees,
        TimeOfDayHours = source.TimeOfDayHours,
        LatitudeDegrees = source.LatitudeDegrees,
        DayOfYear = source.DayOfYear,
        NorthOffsetDegrees = source.NorthOffsetDegrees,
        TimeScale = source.TimeScale,
        DirectSunDirection = new(source.DirectSunDirection.X, source.DirectSunDirection.Y, source.DirectSunDirection.Z),
        AtmosphereIntensity = source.AtmosphereIntensity,
        SolarIrradianceScale = source.SolarIrradianceScale,
        MoonIrradianceScale = source.MoonIrradianceScale,
        StarIntensity = source.StarIntensity,
        AirglowIntensity = source.AirglowIntensity,
        SkyIntensity = source.SkyIntensity,
        DiffuseIntensity = source.DiffuseIntensity,
        SpecularIntensity = source.SpecularIntensity,
        RotationRadians = source.RotationRadians,
    };
    public static void Apply(SceneEnvironment source, EnvironmentSettings destination)
    {
        destination.Enabled = source.Enabled;
        destination.SourceKind = (EnvironmentSourceKind)source.SourceKind;
        destination.SourcePath = source.SourcePath;
        destination.SunDriver = (ProceduralSkySunDriver)source.SunDriver;
        destination.AnimateTimeOfDay = source.AnimateTimeOfDay;
        destination.Turbidity = source.Turbidity;
        destination.GroundAlbedo = new(source.GroundAlbedo.X, source.GroundAlbedo.Y, source.GroundAlbedo.Z);
        destination.SunAngularDiameterDegrees = source.SunAngularDiameterDegrees;
        destination.MoonAngularDiameterDegrees = source.MoonAngularDiameterDegrees;
        destination.TimeOfDayHours = source.TimeOfDayHours;
        destination.LatitudeDegrees = source.LatitudeDegrees;
        destination.DayOfYear = source.DayOfYear;
        destination.NorthOffsetDegrees = source.NorthOffsetDegrees;
        destination.TimeScale = source.TimeScale;
        destination.DirectSunDirection = new(source.DirectSunDirection.X, source.DirectSunDirection.Y, source.DirectSunDirection.Z);
        destination.AtmosphereIntensity = source.AtmosphereIntensity;
        destination.SolarIrradianceScale = source.SolarIrradianceScale;
        destination.MoonIrradianceScale = source.MoonIrradianceScale;
        destination.StarIntensity = source.StarIntensity;
        destination.AirglowIntensity = source.AirglowIntensity;
        destination.SkyIntensity = source.SkyIntensity;
        destination.DiffuseIntensity = source.DiffuseIntensity;
        destination.SpecularIntensity = source.SpecularIntensity;
        destination.RotationRadians = source.RotationRadians;
    }
}

