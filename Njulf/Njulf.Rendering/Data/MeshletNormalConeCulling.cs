using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Managed mirror of the conservative meshlet normal-cone decision used by
/// camera depth and forward task production.
/// </summary>
internal static class MeshletNormalConeCulling
{
    public const float RejectionSafetyEpsilon = 1e-6f;

    public static bool IsPerspectiveCulled(
        Vector3 worldAxis,
        float cutoff,
        Vector3 worldCenter,
        float worldRadius,
        Vector3 cameraPosition)
    {
        if (!TryNormalizeValidConeAxis(worldAxis, cutoff, out Vector3 axis) ||
            !IsFinite(worldCenter) ||
            !IsFinite(cameraPosition) ||
            !float.IsFinite(worldRadius))
        {
            return false;
        }

        Vector3 centerToCamera = cameraPosition - worldCenter;
        float distanceSquared = centerToCamera.LengthSquared();
        float radius = MathF.Max(worldRadius, 0.0f);
        if (!float.IsFinite(distanceSquared) ||
            distanceSquared <= radius * radius + 1e-8f)
        {
            return false;
        }

        float distance = MathF.Sqrt(distanceSquared);
        float sinA = MathF.Sqrt(MathF.Max(1.0f - cutoff * cutoff, 0.0f));
        float sinB = Math.Clamp(radius / distance, 0.0f, 1.0f);
        float cosB = MathF.Sqrt(MathF.Max(1.0f - sinB * sinB, 0.0f));
        float cosAB = cutoff * cosB - sinA * sinB;
        if (!(cosAB > 0.0f))
            return false;

        float sinAB = MathF.Min(
            sinA * cosB + cutoff * sinB,
            1.0f);
        Vector3 surfaceToCamera = centerToCamera / distance;
        return Vector3.Dot(axis, surfaceToCamera) <
               -sinAB - RejectionSafetyEpsilon;
    }

    public static bool IsOrthographicCulled(
        Vector3 worldAxis,
        float cutoff,
        Vector3 surfaceToCameraDirection)
    {
        if (!TryNormalizeValidConeAxis(worldAxis, cutoff, out Vector3 axis) ||
            !TryNormalize(surfaceToCameraDirection, out Vector3 direction))
        {
            return false;
        }

        float coneSine = MathF.Sqrt(
            MathF.Max(1.0f - cutoff * cutoff, 0.0f));
        return Vector3.Dot(axis, direction) <
               -coneSine - RejectionSafetyEpsilon;
    }

    private static bool TryNormalizeValidConeAxis(
        Vector3 axis,
        float cutoff,
        out Vector3 normalized)
    {
        if (!(cutoff > 0.0f && cutoff <= 1.0f) ||
            !float.IsFinite(cutoff))
        {
            normalized = Vector3.Zero;
            return false;
        }

        return TryNormalize(axis, out normalized);
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        float lengthSquared = value.LengthSquared();
        if (!IsFinite(value) ||
            !float.IsFinite(lengthSquared) ||
            lengthSquared <= 1e-12f)
        {
            normalized = Vector3.Zero;
            return false;
        }

        normalized = value / MathF.Sqrt(lengthSquared);
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
