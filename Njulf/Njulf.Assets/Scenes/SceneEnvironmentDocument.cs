using Njulf.Core.Scene;
namespace Njulf.Assets.Scenes;

public sealed record SceneEnvironmentDocument
{
    public bool Enabled { get; init; } = true;
    public SceneEnvironmentSource SourceKind { get; init; } = SceneEnvironmentSource.ProceduralSky;
    public string? SourcePath { get; init; } = null;
    public SceneSunDriver SunDriver { get; init; } = SceneSunDriver.SceneDirectionalLight;
    public bool AnimateTimeOfDay { get; init; } = false;
    public float Turbidity { get; init; } = 3f;
    public SceneVector3 GroundAlbedo { get; init; } = new(0.2f, 0.2f, 0.2f);
    public float SunAngularDiameterDegrees { get; init; } = 0.53f;
    public float MoonAngularDiameterDegrees { get; init; } = 0.52f;
    public float TimeOfDayHours { get; init; } = 14f;
    public float LatitudeDegrees { get; init; } = 59.9139f;
    public int DayOfYear { get; init; } = 172;
    public float NorthOffsetDegrees { get; init; } = 0f;
    public float TimeScale { get; init; } = 60f;
    public SceneVector3 DirectSunDirection { get; init; } = new(-0.3f, 0.72f, 0.62f);
    public float AtmosphereIntensity { get; init; } = 1f;
    public float SolarIrradianceScale { get; init; } = 14f;
    public float MoonIrradianceScale { get; init; } = 0.12f;
    public float StarIntensity { get; init; } = 0.025f;
    public float AirglowIntensity { get; init; } = 0.025f;
    public float SkyIntensity { get; init; } = 1f;
    public float DiffuseIntensity { get; init; } = 1f;
    public float SpecularIntensity { get; init; } = 1f;
    public float RotationRadians { get; init; } = 0f;
    public SceneEnvironment ToEnvironment() => new()
    {
        Enabled = Enabled,
        SourceKind = SourceKind,
        SourcePath = SourcePath,
        SunDriver = SunDriver,
        AnimateTimeOfDay = AnimateTimeOfDay,
        Turbidity = Turbidity,
        GroundAlbedo = new(GroundAlbedo.X, GroundAlbedo.Y, GroundAlbedo.Z),
        SunAngularDiameterDegrees = SunAngularDiameterDegrees,
        MoonAngularDiameterDegrees = MoonAngularDiameterDegrees,
        TimeOfDayHours = TimeOfDayHours,
        LatitudeDegrees = LatitudeDegrees,
        DayOfYear = DayOfYear,
        NorthOffsetDegrees = NorthOffsetDegrees,
        TimeScale = TimeScale,
        DirectSunDirection = new(DirectSunDirection.X, DirectSunDirection.Y, DirectSunDirection.Z),
        AtmosphereIntensity = AtmosphereIntensity,
        SolarIrradianceScale = SolarIrradianceScale,
        MoonIrradianceScale = MoonIrradianceScale,
        StarIntensity = StarIntensity,
        AirglowIntensity = AirglowIntensity,
        SkyIntensity = SkyIntensity,
        DiffuseIntensity = DiffuseIntensity,
        SpecularIntensity = SpecularIntensity,
        RotationRadians = RotationRadians,
    };
    public static SceneEnvironmentDocument FromEnvironment(SceneEnvironment source) => new()
    {
        Enabled = source.Enabled,
        SourceKind = source.SourceKind,
        SourcePath = source.SourcePath,
        SunDriver = source.SunDriver,
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
}

