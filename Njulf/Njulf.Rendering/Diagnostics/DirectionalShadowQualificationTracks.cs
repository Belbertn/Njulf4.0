using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics;

public enum DirectionalShadowTrackKind : byte
{
    UpVectorThresholdSweep = 0,
    SunAzimuthSweep = 1,
    MicroTranslation = 2,
    CameraRotation = 3,
    CascadeTransition = 4,
    OffCameraCaster = 5,
    GeometryMaterialMatrix = 6,
    LargeCoordinatesAndTeleport = 7,
    RayConsumerMatrix = 8
}

public readonly record struct DirectionalShadowTrackSample(
    uint Frame,
    Vector3 CameraPosition,
    Vector3 CameraForward,
    Vector3 LightDirection,
    float CascadeTexelOffsetX,
    float CascadeTexelOffsetY,
    int CameraCut);

public sealed record DirectionalShadowQualificationTrack(
    string TrackId,
    DirectionalShadowTrackKind Kind,
    string RequiredSceneContract,
    IReadOnlyList<DirectionalShadowTrackSample> Samples);

public readonly record struct DirectionalShadowCaptureVariant(
    uint Width,
    uint Height,
    AntiAliasingMode AntiAliasingMode,
    DirectionalShadowMode ShadowMode,
    bool DdgiEnabled,
    bool DdgiRayQueryBackend);

/// <summary>
/// Pure deterministic inputs for the visual/performance harness. The catalog
/// contains no wall-clock or random state, so CSM, hybrid, hard, and soft A/B
/// captures consume byte-identical camera/light tracks.
/// </summary>
public static class DirectionalShadowQualificationTracks
{
    private static readonly IReadOnlyList<DirectionalShadowQualificationTrack>
        Catalog = BuildCatalog();

    public static IReadOnlyList<DirectionalShadowQualificationTrack> All =>
        Catalog;

    public static IReadOnlyList<DirectionalShadowCaptureVariant>
        CreateReferenceVariants()
    {
        (uint Width, uint Height)[] resolutions =
        [
            (1920u, 1080u),
            (2560u, 1440u),
            (3840u, 2160u)
        ];
        AntiAliasingMode[] aaModes =
        [
            AntiAliasingMode.SmaaHigh,
            AntiAliasingMode.Taa
        ];
        DirectionalShadowMode[] shadowModes =
        [
            DirectionalShadowMode.Cascaded,
            DirectionalShadowMode.HybridContact,
            DirectionalShadowMode.RayQueryHard,
            DirectionalShadowMode.RayQuerySoft
        ];
        var result = new List<DirectionalShadowCaptureVariant>();
        foreach ((uint width, uint height) in resolutions)
        foreach (AntiAliasingMode aa in aaModes)
        foreach (DirectionalShadowMode shadow in shadowModes)
        foreach (bool ddgi in new[] { false, true })
        foreach (bool ddgiRayQuery in ddgi
                     ? new[] { false, true }
                     : new[] { false })
        {
            result.Add(new(
                width,
                height,
                aa,
                shadow,
                ddgi,
                ddgiRayQuery));
        }
        return result.AsReadOnly();
    }

    public static string SerializeCatalog()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return JsonSerializer.Serialize(new
        {
            schemaRevision = 1,
            numericCulture = CultureInfo.InvariantCulture.Name,
            tracks = Catalog,
            variants = CreateReferenceVariants()
        }, options);
    }

    private static IReadOnlyList<DirectionalShadowQualificationTrack>
        BuildCatalog()
    {
        var tracks = new List<DirectionalShadowQualificationTrack>
        {
            new(
                "up-vector-threshold-bidirectional",
                DirectionalShadowTrackKind.UpVectorThresholdSweep,
                "stable opaque plane and pole; no animation",
                CreateThresholdSweep()),
            new(
                "sun-azimuth-elevation-grid",
                DirectionalShadowTrackKind.SunAzimuthSweep,
                "wide receiver and vertical occluders",
                CreateSunSweep()),
            new(
                "cascade-texel-micro-translation",
                DirectionalShadowTrackKind.MicroTranslation,
                "origin-centered opaque reference grid",
                CreateMicroTranslation()),
            new(
                "camera-yaw-pitch-roll",
                DirectionalShadowTrackKind.CameraRotation,
                "fixed camera position and static light",
                CreateRotationSweep()),
            new(
                "cascade-overlap-and-distance",
                DirectionalShadowTrackKind.CascadeTransition,
                "receiver ramp spanning every split and maximum distance",
                CreateLinearTrack(256u, 0.5f, cameraCutAtEnd: false)),
            new(
                "off-camera-tall-caster",
                DirectionalShadowTrackKind.OffCameraCaster,
                "tall caster outside view whose projected shadow enters view",
                CreateLinearTrack(180u, 0.1f, cameraCutAtEnd: false)),
            new(
                "geometry-material-qualification-matrix",
                DirectionalShadowTrackKind.GeometryMaterialMatrix,
                "static, rigid, skinned, alpha-mask, authored/procedural foliage, double-sided, transparent",
                CreateLinearTrack(240u, 0.03f, cameraCutAtEnd: false)),
            new(
                "large-coordinate-camera-cut",
                DirectionalShadowTrackKind.LargeCoordinatesAndTeleport,
                "duplicate reference scene at +1,000,000 world units",
                CreateLargeCoordinateTrack()),
            new(
                "ddgi-and-ray-consumer-matrix",
                DirectionalShadowTrackKind.RayConsumerMatrix,
                "same qualification scene; variants control DDGI/ray consumers",
                CreateLinearTrack(180u, 0.04f, cameraCutAtEnd: false))
        };
        return tracks.AsReadOnly();
    }

    private static IReadOnlyList<DirectionalShadowTrackSample>
        CreateThresholdSweep()
    {
        var samples = new List<DirectionalShadowTrackSample>(242);
        for (int direction = 0; direction < 2; direction++)
        for (uint frame = 0; frame <= 120u; frame++)
        {
            float t = frame / 120f;
            if (direction != 0)
                t = 1f - t;
            float y = 0.93f + t * 0.04f;
            float x = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            samples.Add(Sample(
                checked((uint)samples.Count),
                Vector3.Zero,
                new Vector3(0f, 0f, -1f),
                new Vector3(x, -y, 0f).Normalized()));
        }
        return samples.AsReadOnly();
    }

    private static IReadOnlyList<DirectionalShadowTrackSample> CreateSunSweep()
    {
        float[] elevations = [2f, 25f, 70f, 89.5f, -89.5f];
        var samples = new List<DirectionalShadowTrackSample>(
            elevations.Length * 181);
        foreach (float elevationDegrees in elevations)
        {
            float elevation = elevationDegrees * MathF.PI / 180f;
            for (int azimuthDegrees = 0; azimuthDegrees <= 360;
                 azimuthDegrees += 2)
            {
                float azimuth = azimuthDegrees * MathF.PI / 180f;
                var direction = new Vector3(
                    MathF.Cos(elevation) * MathF.Cos(azimuth),
                    -MathF.Sin(elevation),
                    MathF.Cos(elevation) * MathF.Sin(azimuth));
                samples.Add(Sample(
                    checked((uint)samples.Count),
                    Vector3.Zero,
                    new Vector3(0f, 0f, -1f),
                    direction.Normalized()));
            }
        }
        return samples.AsReadOnly();
    }

    private static IReadOnlyList<DirectionalShadowTrackSample>
        CreateMicroTranslation()
    {
        float[] offsets =
            [-1.5f, -1f, -0.51f, -0.49f, -0.1f, 0f,
                0.1f, 0.49f, 0.51f, 1f, 1.5f];
        var samples = new List<DirectionalShadowTrackSample>(
            offsets.Length * offsets.Length);
        foreach (float y in offsets)
        foreach (float x in offsets)
        {
            samples.Add(Sample(
                checked((uint)samples.Count),
                Vector3.Zero,
                new Vector3(0f, 0f, -1f),
                new Vector3(-0.35f, -0.75f, -0.55f).Normalized(),
                x,
                y));
        }
        return samples.AsReadOnly();
    }

    private static IReadOnlyList<DirectionalShadowTrackSample>
        CreateRotationSweep()
    {
        var samples = new List<DirectionalShadowTrackSample>(363);
        for (int axis = 0; axis < 3; axis++)
        for (int degrees = -180; degrees <= 180; degrees += 3)
        {
            float angle = degrees * MathF.PI / 180f;
            Vector3 forward = axis switch
            {
                0 => new Vector3(MathF.Sin(angle), 0f, -MathF.Cos(angle)),
                1 => new Vector3(0f, MathF.Sin(angle), -MathF.Cos(angle)),
                _ => new Vector3(
                    MathF.Sin(angle) * 0.001f,
                    0f,
                    -MathF.Cos(angle)).Normalized()
            };
            samples.Add(Sample(
                checked((uint)samples.Count),
                Vector3.Zero,
                forward,
                new Vector3(-0.35f, -0.75f, -0.55f).Normalized()));
        }
        return samples.AsReadOnly();
    }

    private static IReadOnlyList<DirectionalShadowTrackSample>
        CreateLinearTrack(uint count, float step, bool cameraCutAtEnd)
    {
        var samples = new List<DirectionalShadowTrackSample>((int)count);
        for (uint frame = 0; frame < count; frame++)
        {
            samples.Add(Sample(
                frame,
                new Vector3(0f, 1.7f, -frame * step),
                new Vector3(0f, 0f, -1f),
                new Vector3(-0.35f, -0.75f, -0.55f).Normalized(),
                cameraCut: cameraCutAtEnd && frame + 1u == count ? 1 : 0));
        }
        return samples.AsReadOnly();
    }

    private static IReadOnlyList<DirectionalShadowTrackSample>
        CreateLargeCoordinateTrack()
    {
        var samples = new List<DirectionalShadowTrackSample>(121);
        for (uint frame = 0; frame <= 120u; frame++)
        {
            bool cut = frame == 60u;
            float baseCoordinate = cut || frame > 60u ? 1_000_000f : 0f;
            samples.Add(Sample(
                frame,
                new Vector3(baseCoordinate, 1.7f, baseCoordinate - frame * 0.1f),
                new Vector3(0f, 0f, -1f),
                new Vector3(-0.35f, -0.75f, -0.55f).Normalized(),
                cameraCut: cut ? 1 : 0));
        }
        return samples.AsReadOnly();
    }

    private static DirectionalShadowTrackSample Sample(
        uint frame,
        Vector3 position,
        Vector3 forward,
        Vector3 light,
        float texelX = 0f,
        float texelY = 0f,
        int cameraCut = 0) => new(
            frame,
            position,
            forward,
            light,
            texelX,
            texelY,
            cameraCut);
}
