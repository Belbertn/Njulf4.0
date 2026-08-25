using System;
using System.Numerics;

namespace Njulf.Rendering.Resources;

public readonly record struct AreaLightSurfaceSample(
    Vector3 Position,
    Vector3 Normal,
    float AreaPdf);

/// <summary>
/// Canonical CPU geometry for analytical emitters. The GPU helpers in
/// area_lighting.glsl intentionally mirror these definitions.
/// </summary>
public static class AnalyticalLightGeometry
{
    private const float DirectionEpsilonSquared = 1e-12f;
    private const float DimensionEpsilon = 1e-5f;

    public static bool IsArea(LightType type) =>
        type is LightType.Rectangle or LightType.Disk or LightType.Tube;

    public static bool IsLocal(LightType type) => type != LightType.Directional;

    public static bool IsPunctual(LightType type) =>
        type is LightType.Point or LightType.Spot;

    public static bool HasValidDimensions(in Light light)
    {
        if (!IsArea(light.Type))
            return true;
        if (!float.IsFinite(light.Size.X) || !float.IsFinite(light.Size.Y) ||
            light.Size.X <= DimensionEpsilon || light.Size.Y <= DimensionEpsilon)
        {
            return false;
        }

        if (light.Type != LightType.Disk)
            return true;

        float scale = MathF.Max(MathF.Max(light.Size.X, light.Size.Y), 1f);
        return MathF.Abs(light.Size.X - light.Size.Y) <= scale * 1e-4f;
    }

    public static bool TryGetFrame(
        in Light light,
        out Vector3 axis,
        out Vector3 up,
        out Vector3 right)
    {
        axis = light.Direction;
        if (!IsFinite(axis) || axis.LengthSquared() <= DirectionEpsilonSquared)
        {
            up = default;
            right = default;
            return false;
        }

        axis = Vector3.Normalize(axis);
        Vector3 candidateUp = light.Up;
        if (!IsFinite(candidateUp) || candidateUp.LengthSquared() <= DirectionEpsilonSquared)
            candidateUp = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        candidateUp -= axis * Vector3.Dot(candidateUp, axis);
        if (candidateUp.LengthSquared() <= DirectionEpsilonSquared)
            candidateUp = MathF.Abs(axis.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX;
        candidateUp -= axis * Vector3.Dot(candidateUp, axis);
        if (candidateUp.LengthSquared() <= DirectionEpsilonSquared)
        {
            up = default;
            right = default;
            return false;
        }

        up = Vector3.Normalize(candidateUp);
        right = Vector3.Normalize(Vector3.Cross(up, axis));
        up = Vector3.Normalize(Vector3.Cross(axis, right));
        return IsFinite(up) && IsFinite(right);
    }

    public static bool TryGetSurfaceArea(in Light light, out float area)
    {
        area = 0f;
        if (!HasValidDimensions(light) || !IsArea(light.Type))
            return false;

        area = light.Type switch
        {
            LightType.Rectangle => light.Size.X * light.Size.Y *
                (light.TwoSided ? 2f : 1f),
            LightType.Disk => MathF.PI * Square(light.Size.X * 0.5f) *
                (light.TwoSided ? 2f : 1f),
            LightType.Tube =>
                2f * MathF.PI * (light.Size.Y * 0.5f) * light.Size.X +
                2f * MathF.PI * Square(light.Size.Y * 0.5f),
            _ => 0f
        };
        return float.IsFinite(area) && area > 0f;
    }

    public static float ComputeSurfaceArea(in Light light) =>
        TryGetSurfaceArea(light, out float area) ? area : 0f;

    public static float ComputePowerWeight(in Light light)
    {
        float luminance =
            MathF.Max(light.Color.X, 0f) * 0.2126f +
            MathF.Max(light.Color.Y, 0f) * 0.7152f +
            MathF.Max(light.Color.Z, 0f) * 0.0722f;
        float radiance = luminance * MathF.Max(light.Intensity, 0f);
        if (!float.IsFinite(radiance))
            return 0f;
        if (!IsArea(light.Type))
            return radiance;
        return TryGetSurfaceArea(light, out float area)
            ? radiance * MathF.PI * area
            : 0f;
    }

    public static bool TryGetInfluenceBounds(
        in Light light,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        minimum = default;
        maximum = default;
        if (!IsFinite(light.Position) || !float.IsFinite(light.Range) ||
            light.Range <= 0f)
        {
            return false;
        }

        Vector3 shapeExtent = Vector3.Zero;
        if (IsArea(light.Type))
        {
            if (!HasValidDimensions(light) ||
                !TryGetFrame(light, out Vector3 axis, out Vector3 up, out Vector3 right))
            {
                return false;
            }

            shapeExtent = light.Type switch
            {
                LightType.Rectangle =>
                    Abs(right) * (light.Size.X * 0.5f) +
                    Abs(up) * (light.Size.Y * 0.5f),
                LightType.Disk => DiskExtent(axis, light.Size.X * 0.5f),
                LightType.Tube => TubeExtent(
                    axis,
                    light.Size.X * 0.5f,
                    light.Size.Y * 0.5f),
                _ => Vector3.Zero
            };
        }

        Vector3 extent = shapeExtent + new Vector3(light.Range);
        minimum = light.Position - extent;
        maximum = light.Position + extent;
        return IsFinite(minimum) && IsFinite(maximum);
    }

    public static float GetShapeBoundingRadius(in Light light)
    {
        if (IsArea(light.Type) && !HasValidDimensions(light))
            return 0f;

        float shapeRadius = light.Type switch
        {
            LightType.Rectangle => 0.5f * MathF.Sqrt(
                Square(light.Size.X) + Square(light.Size.Y)),
            LightType.Disk => light.Size.X * 0.5f,
            LightType.Tube => 0.5f * MathF.Sqrt(
                Square(light.Size.X) + Square(light.Size.Y)),
            _ => 0f
        };
        return float.IsFinite(shapeRadius) ? MathF.Max(shapeRadius, 0f) : 0f;
    }

    /// <summary>
    /// Radius of the emitter's finite-range influence volume. This is the
    /// emitter bounding radius expanded by its authored range.
    /// </summary>
    public static float GetBoundingRadius(in Light light)
    {
        float range = float.IsFinite(light.Range) ? MathF.Max(light.Range, 0f) : 0f;
        return range + GetShapeBoundingRadius(light);
    }

    /// <summary>
    /// Conservative longest receiver-to-sample segment for a receiver inside
    /// the emitter's influence volume. Ray-scene residency uses this rather
    /// than the influence radius because a sample may lie on the far side of
    /// the emitter.
    /// </summary>
    public static float GetMaximumSurfaceSampleDistanceWithinRange(in Light light)
    {
        float range = float.IsFinite(light.Range) ? MathF.Max(light.Range, 0f) : 0f;
        return range + 2f * GetShapeBoundingRadius(light);
    }

    public static bool TrySampleSurface(
        in Light light,
        Vector3 random,
        out AreaLightSurfaceSample sample)
    {
        sample = default;
        if (!TryGetSurfaceArea(light, out float totalArea) ||
            !TryGetFrame(light, out Vector3 axis, out Vector3 up, out Vector3 right))
        {
            return false;
        }

        random = new Vector3(
            ClampUnit(random.X),
            ClampUnit(random.Y),
            ClampUnit(random.Z));
        Vector3 position;
        Vector3 normal;
        switch (light.Type)
        {
            case LightType.Rectangle:
                position = light.Position +
                    right * ((random.X - 0.5f) * light.Size.X) +
                    up * ((random.Y - 0.5f) * light.Size.Y);
                normal = light.TwoSided && random.Z >= 0.5f ? -axis : axis;
                break;
            case LightType.Disk:
            {
                float radius = light.Size.X * 0.5f;
                float radial = radius * MathF.Sqrt(random.X);
                float angle = 2f * MathF.PI * random.Y;
                position = light.Position +
                    right * (radial * MathF.Cos(angle)) +
                    up * (radial * MathF.Sin(angle));
                normal = light.TwoSided && random.Z >= 0.5f ? -axis : axis;
                break;
            }
            case LightType.Tube:
            {
                float radius = light.Size.Y * 0.5f;
                float sideArea = 2f * MathF.PI * radius * light.Size.X;
                float capArea = MathF.PI * radius * radius;
                float selector = random.Z * totalArea;
                if (selector < sideArea)
                {
                    float angle = 2f * MathF.PI * random.X;
                    normal = right * MathF.Cos(angle) + up * MathF.Sin(angle);
                    position = light.Position +
                        axis * ((random.Y - 0.5f) * light.Size.X) +
                        normal * radius;
                }
                else
                {
                    bool positiveCap = selector >= sideArea + capArea;
                    float radial = radius * MathF.Sqrt(random.X);
                    float angle = 2f * MathF.PI * random.Y;
                    normal = positiveCap ? axis : -axis;
                    position = light.Position +
                        axis * (positiveCap ? light.Size.X * 0.5f : -light.Size.X * 0.5f) +
                        right * (radial * MathF.Cos(angle)) +
                        up * (radial * MathF.Sin(angle));
                }
                break;
            }
            default:
                return false;
        }

        sample = new AreaLightSurfaceSample(position, normal, 1f / totalArea);
        return IsFinite(position) && IsFinite(normal) &&
            float.IsFinite(sample.AreaPdf) && sample.AreaPdf > 0f;
    }

    private static Vector3 DiskExtent(Vector3 normal, float radius) => new(
        radius * MathF.Sqrt(MathF.Max(0f, 1f - normal.X * normal.X)),
        radius * MathF.Sqrt(MathF.Max(0f, 1f - normal.Y * normal.Y)),
        radius * MathF.Sqrt(MathF.Max(0f, 1f - normal.Z * normal.Z)));

    private static Vector3 TubeExtent(Vector3 axis, float halfLength, float radius) => new(
        MathF.Abs(axis.X) * halfLength + radius * MathF.Sqrt(MathF.Max(0f, 1f - axis.X * axis.X)),
        MathF.Abs(axis.Y) * halfLength + radius * MathF.Sqrt(MathF.Max(0f, 1f - axis.Y * axis.Y)),
        MathF.Abs(axis.Z) * halfLength + radius * MathF.Sqrt(MathF.Max(0f, 1f - axis.Z * axis.Z)));

    private static Vector3 Abs(Vector3 value) => new(
        MathF.Abs(value.X),
        MathF.Abs(value.Y),
        MathF.Abs(value.Z));

    private static float Square(float value) => value * value;
    private static float ClampUnit(float value) => Math.Clamp(value, 0f, MathF.BitDecrement(1f));
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
