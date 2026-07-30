using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// CPU mirror of the DDGI ray-hit texture-footprint policy. Keeping the
/// numerical policy executable on the CPU makes changes to probe spacing,
/// ray cones, UV transforms, or cascade behavior independently testable.
/// </summary>
public static class DdgiMaterialTextureLodPolicy
{
    public const uint AuthoredVolumeCascade = uint.MaxValue;
    public const float MinimumWorldEdgeLength = 0.0001f;
    public const float MinimumProbeSpacing = 0.001f;

    public static float Resolve(
        int textureWidth,
        int textureHeight,
        int texCoordSet,
        Vector2 bindingScale,
        in DdgiMaterialTriangleFootprint footprint,
        uint volumeCascadeIndex,
        float probeSpacing,
        float hitDistance,
        float rayAngularRadius,
        float authoredLodBias = 0f)
    {
        ValidateFinite(bindingScale, nameof(bindingScale));
        ValidateFinite(probeSpacing, nameof(probeSpacing));
        ValidateFinite(hitDistance, nameof(hitDistance));
        ValidateFinite(rayAngularRadius, nameof(rayAngularRadius));
        ValidateFinite(authoredLodBias, nameof(authoredLodBias));
        footprint.Validate();

        if (textureWidth <= 0 || textureHeight <= 0)
            return 0f;

        float uvPerWorldUnit = TriangleUvDensity(texCoordSet, bindingScale, footprint);
        float maximumDimension = Math.Max(Math.Max(textureWidth, textureHeight), 1);
        float latticeRadius = Math.Max(probeSpacing, MinimumProbeSpacing) *
            (volumeCascadeIndex == AuthoredVolumeCascade ? 0.125f : 0.25f);
        float coneRadius = Math.Max(hitDistance, 0f) * Math.Clamp(rayAngularRadius, 0f, 1f);
        float worldFootprint = Math.Max(latticeRadius, coneRadius);
        float texelFootprint = Math.Max(worldFootprint * uvPerWorldUnit * maximumDimension, 1f);
        float maximumLod = Math.Max(MathF.Floor(MathF.Log2(maximumDimension)), 0f);
        return Math.Clamp(
            Math.Max(authoredLodBias, 0f) + MathF.Log2(texelFootprint),
            0f,
            maximumLod);
    }

    public static float TriangleUvDensity(
        int texCoordSet,
        Vector2 bindingScale,
        in DdgiMaterialTriangleFootprint footprint)
    {
        ValidateFinite(bindingScale, nameof(bindingScale));
        footprint.Validate();

        bool useTexCoord1 = texCoordSet == 1;
        Vector2 uv0 = (useTexCoord1 ? footprint.TexCoord10 : footprint.TexCoord00) * bindingScale;
        Vector2 uv1 = (useTexCoord1 ? footprint.TexCoord11 : footprint.TexCoord01) * bindingScale;
        Vector2 uv2 = (useTexCoord1 ? footprint.TexCoord12 : footprint.TexCoord02) * bindingScale;

        float density01 = (uv1 - uv0).Length() /
            Math.Max((footprint.WorldPosition1 - footprint.WorldPosition0).Length(), MinimumWorldEdgeLength);
        float density12 = (uv2 - uv1).Length() /
            Math.Max((footprint.WorldPosition2 - footprint.WorldPosition1).Length(), MinimumWorldEdgeLength);
        float density20 = (uv0 - uv2).Length() /
            Math.Max((footprint.WorldPosition0 - footprint.WorldPosition2).Length(), MinimumWorldEdgeLength);
        return Math.Max(density01, Math.Max(density12, density20));
    }

    private static void ValidateFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "DDGI texture-footprint inputs must be finite.");
    }

    private static void ValidateFinite(Vector2 value, string parameterName)
    {
        ValidateFinite(value.X, parameterName);
        ValidateFinite(value.Y, parameterName);
    }
}

public readonly record struct DdgiMaterialTriangleFootprint(
    Vector3 WorldPosition0,
    Vector3 WorldPosition1,
    Vector3 WorldPosition2,
    Vector2 TexCoord00,
    Vector2 TexCoord01,
    Vector2 TexCoord02,
    Vector2 TexCoord10,
    Vector2 TexCoord11,
    Vector2 TexCoord12)
{
    internal void Validate()
    {
        ValidateFinite(WorldPosition0, nameof(WorldPosition0));
        ValidateFinite(WorldPosition1, nameof(WorldPosition1));
        ValidateFinite(WorldPosition2, nameof(WorldPosition2));
        ValidateFinite(TexCoord00, nameof(TexCoord00));
        ValidateFinite(TexCoord01, nameof(TexCoord01));
        ValidateFinite(TexCoord02, nameof(TexCoord02));
        ValidateFinite(TexCoord10, nameof(TexCoord10));
        ValidateFinite(TexCoord11, nameof(TexCoord11));
        ValidateFinite(TexCoord12, nameof(TexCoord12));
    }

    private static void ValidateFinite(Vector2 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(name, "DDGI texture-footprint inputs must be finite.");
    }

    private static void ValidateFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(name, "DDGI texture-footprint inputs must be finite.");
    }
}
