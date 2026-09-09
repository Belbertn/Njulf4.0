using Njulf.Core.Math;
namespace Njulf.Core.Scene;

public enum SceneEnvironmentSource { ProceduralSky, HdrEquirectangular, Cubemap }
public enum SceneSunDriver { SceneDirectionalLight, AstronomicalTime, DirectSunDirection }

/// <summary>Immutable authored environment. Resolution and update scheduling remain renderer settings.</summary>
public sealed record SceneEnvironment
{
    public bool Enabled { get; init; } = true;
    public SceneEnvironmentSource SourceKind { get; init; } = SceneEnvironmentSource.ProceduralSky;
    public string? SourcePath { get; init; } = null;
    public SceneSunDriver SunDriver { get; init; } = SceneSunDriver.SceneDirectionalLight;
    public bool AnimateTimeOfDay { get; init; } = false;
    public float Turbidity { get; init; } = 3f;
    public Vector3 GroundAlbedo { get; init; } = new(0.2f, 0.2f, 0.2f);
    public float SunAngularDiameterDegrees { get; init; } = 0.53f;
    public float MoonAngularDiameterDegrees { get; init; } = 0.52f;
    public float TimeOfDayHours { get; init; } = 14f;
    public float LatitudeDegrees { get; init; } = 59.9139f;
    public int DayOfYear { get; init; } = 172;
    public float NorthOffsetDegrees { get; init; } = 0f;
    public float TimeScale { get; init; } = 60f;
    public Vector3 DirectSunDirection { get; init; } = new(-0.3f, 0.72f, 0.62f);
    public float AtmosphereIntensity { get; init; } = 1f;
    public float SolarIrradianceScale { get; init; } = 14f;
    public float MoonIrradianceScale { get; init; } = 0.12f;
    public float StarIntensity { get; init; } = 0.025f;
    public float AirglowIntensity { get; init; } = 0.025f;
    public float SkyIntensity { get; init; } = 1f;
    public float DiffuseIntensity { get; init; } = 1f;
    public float SpecularIntensity { get; init; } = 1f;
    public float RotationRadians { get; init; } = 0f;
}

