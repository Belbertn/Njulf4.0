using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Rendering.Resources;

/// <summary>
/// CPU oracle for the GPU cached-ray segment selector. The production shader
/// uses the same slab test and deliberately treats uncertain/non-finite input
/// as touched. False positives are legal; false negatives are not.
/// </summary>
public static class SimpleDdgiSegmentSelector
{
    public static bool IntersectsSegment(
        Vector3 origin,
        Vector3 direction,
        float segmentLength,
        BoundingBox bounds,
        float padding = 0.0f)
    {
        if (!IsFinite(origin) || !IsFinite(direction) ||
            !float.IsFinite(segmentLength) || segmentLength < 0.0f ||
            !IsFinite(bounds.Min) || !IsFinite(bounds.Max) ||
            !float.IsFinite(padding))
        {
            return true;
        }

        float safePadding = Math.Max(padding, 0.0f);
        Vector3 minimum = new(
            MathF.Min(bounds.Min.X, bounds.Max.X) - safePadding,
            MathF.Min(bounds.Min.Y, bounds.Max.Y) - safePadding,
            MathF.Min(bounds.Min.Z, bounds.Max.Z) - safePadding);
        Vector3 maximum = new(
            MathF.Max(bounds.Min.X, bounds.Max.X) + safePadding,
            MathF.Max(bounds.Min.Y, bounds.Max.Y) + safePadding,
            MathF.Max(bounds.Min.Z, bounds.Max.Z) + safePadding);
        float intervalMinimum = 0.0f;
        float intervalMaximum = segmentLength;
        for (int axis = 0; axis < 3; axis++)
        {
            float axisOrigin = GetAxis(origin, axis);
            float axisDirection = GetAxis(direction, axis);
            float axisMinimum = GetAxis(minimum, axis);
            float axisMaximum = GetAxis(maximum, axis);
            if (MathF.Abs(axisDirection) <= 1.0e-8f)
            {
                if (axisOrigin < axisMinimum || axisOrigin > axisMaximum)
                    return false;
                continue;
            }

            float inverseDirection = 1.0f / axisDirection;
            float first = (axisMinimum - axisOrigin) * inverseDirection;
            float second = (axisMaximum - axisOrigin) * inverseDirection;
            intervalMinimum = MathF.Max(
                intervalMinimum,
                MathF.Min(first, second));
            intervalMaximum = MathF.Min(
                intervalMaximum,
                MathF.Max(first, second));
            if (intervalMaximum < intervalMinimum)
                return false;
        }

        return intervalMaximum >= intervalMinimum;
    }

    public static bool RequiresRetrace(
        Vector3 probePosition,
        Vector3 direction,
        float cachedDistance,
        bool cachedSurfaceHit,
        float probeSpacing,
        IReadOnlyList<DdgiDirtyRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
            return true;

        bool sawInfluencingRegion = false;
        float spacing = Math.Max(probeSpacing, 0.001f);
        float padding = Math.Max(0.02f, spacing * 0.005f);
        for (int i = 0; i < regions.Count; i++)
        {
            DdgiDirtyRegion region = regions[i];
            uint reasonFlags = region.ReasonFlags == 0u
                ? 1u << (int)region.Reason
                : region.ReasonFlags;
            if (!SimpleDdgiVolumeManager.IsSegmentSelectiveReasonFlags(
                    reasonFlags))
            {
                return true;
            }

            if (!ContainsExpanded(
                    region.InfluenceBounds,
                    probePosition,
                    spacing))
            {
                continue;
            }
            sawInfluencingRegion = true;
            bool structural = (reasonFlags & StructuralReasonMask) != 0u;
            if (cachedSurfaceHit && structural)
                return true;

            BoundingBox swept = Union(
                region.OldWorldBounds,
                region.NewWorldBounds,
                region.Bounds);
            if (IntersectsSegment(
                    probePosition,
                    direction,
                    cachedDistance,
                    swept,
                    padding))
            {
                return true;
            }
        }

        return !sawInfluencingRegion;
    }

    private const uint StructuralReasonMask =
        (1u << (int)DdgiDirtyReason.GeometryAdded) |
        (1u << (int)DdgiDirtyReason.GeometryRemoved) |
        (1u << (int)DdgiDirtyReason.TransformChanged);

    private static bool ContainsExpanded(
        BoundingBox bounds,
        Vector3 point,
        float expansion)
    {
        if (!IsFinite(bounds.Min) || !IsFinite(bounds.Max) || !IsFinite(point))
            return true;
        return point.X >= MathF.Min(bounds.Min.X, bounds.Max.X) - expansion &&
            point.X <= MathF.Max(bounds.Min.X, bounds.Max.X) + expansion &&
            point.Y >= MathF.Min(bounds.Min.Y, bounds.Max.Y) - expansion &&
            point.Y <= MathF.Max(bounds.Min.Y, bounds.Max.Y) + expansion &&
            point.Z >= MathF.Min(bounds.Min.Z, bounds.Max.Z) - expansion &&
            point.Z <= MathF.Max(bounds.Min.Z, bounds.Max.Z) + expansion;
    }

    private static BoundingBox Union(
        BoundingBox oldBounds,
        BoundingBox newBounds,
        BoundingBox fallback)
    {
        if (!IsFinite(oldBounds.Min) || !IsFinite(oldBounds.Max) ||
            !IsFinite(newBounds.Min) || !IsFinite(newBounds.Max))
        {
            return fallback;
        }

        return new BoundingBox(
            new Vector3(
                MathF.Min(MathF.Min(oldBounds.Min.X, oldBounds.Max.X),
                    MathF.Min(newBounds.Min.X, newBounds.Max.X)),
                MathF.Min(MathF.Min(oldBounds.Min.Y, oldBounds.Max.Y),
                    MathF.Min(newBounds.Min.Y, newBounds.Max.Y)),
                MathF.Min(MathF.Min(oldBounds.Min.Z, oldBounds.Max.Z),
                    MathF.Min(newBounds.Min.Z, newBounds.Max.Z))),
            new Vector3(
                MathF.Max(MathF.Max(oldBounds.Min.X, oldBounds.Max.X),
                    MathF.Max(newBounds.Min.X, newBounds.Max.X)),
                MathF.Max(MathF.Max(oldBounds.Min.Y, oldBounds.Max.Y),
                    MathF.Max(newBounds.Min.Y, newBounds.Max.Y)),
                MathF.Max(MathF.Max(oldBounds.Min.Z, oldBounds.Max.Z),
                    MathF.Max(newBounds.Min.Z, newBounds.Max.Z))));
    }

    private static float GetAxis(Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z
    };

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
