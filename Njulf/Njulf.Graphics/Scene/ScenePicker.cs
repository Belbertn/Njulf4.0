using System;
using Njulf.Core.Math;

namespace Njulf.Core.Scene;

/// <summary>
/// Allocation-free broad-phase selection for editor and tooling clients.
/// Mesh bounds are transformed to world AABBs, which is intentionally conservative.
/// </summary>
public static class ScenePicker
{
    public static bool TryPickRenderObject(Scene scene, in Ray ray, out RenderObject? picked, out float distance)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        picked = null;
        distance = float.PositiveInfinity;
        foreach (RenderObject renderObject in scene.RenderObjects)
        {
            if (!renderObject.Visible || !renderObject.LocalMeshBounds.HasValue)
                continue;

            BoundingBox bounds = TransformBoundingBox(renderObject.LocalMeshBounds.Value, renderObject.WorldMatrix);
            if (!ray.Intersects(bounds, out float hitDistance) || hitDistance >= distance)
                continue;

            picked = renderObject;
            distance = hitDistance;
        }

        return picked != null;
    }

    private static BoundingBox TransformBoundingBox(BoundingBox bounds, Matrix4x4 matrix)
    {
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        AddCorner(bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
        AddCorner(bounds.Max.X, bounds.Min.Y, bounds.Min.Z);
        AddCorner(bounds.Min.X, bounds.Max.Y, bounds.Min.Z);
        AddCorner(bounds.Max.X, bounds.Max.Y, bounds.Min.Z);
        AddCorner(bounds.Min.X, bounds.Min.Y, bounds.Max.Z);
        AddCorner(bounds.Max.X, bounds.Min.Y, bounds.Max.Z);
        AddCorner(bounds.Min.X, bounds.Max.Y, bounds.Max.Z);
        AddCorner(bounds.Max.X, bounds.Max.Y, bounds.Max.Z);
        return new BoundingBox(min, max);

        void AddCorner(float x, float y, float z)
        {
            Vector3 point = new Vector3(x, y, z) * matrix;
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }
}
