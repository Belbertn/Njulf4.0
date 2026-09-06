using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>Camera and content policy for a scene capture. It contains no main-view visibility.</summary>
internal readonly record struct SecondaryViewContext(
    Matrix4x4 View,
    Matrix4x4 Projection,
    Vector3 Position,
    uint Width,
    uint Height,
    int CaptureLayer,
    bool IncludesDdgi,
    bool IncludesTransparency,
    Vector4 ClipPlane,
    uint[] ExcludedObjects)
{
    public Matrix4x4 ViewProjection => View * Projection;
    public SecondaryViewRegion Region { get; init; }
    public float ClipTolerance { get; init; }
    public int MaximumTransparentMeshlets { get; init; } = int.MaxValue;
    public Matrix4x4 CullingViewProjection => Region.Crop(ViewProjection, Width, Height);
    public bool IsPlanar => (CaptureLayer & 0x1000) != 0;
}

internal readonly record struct SecondaryViewTransparentDraw(
    GPUMeshletDrawCommand Command, float DistanceSquared, int Layer);

internal sealed class SecondaryViewDrawLists
{
    internal readonly List<GPUMeshletDrawCommand>[] Opaque = [[], [], []];
    internal readonly List<SecondaryViewTransparentDraw> Transparent = [];
    internal readonly List<GPUMeshletDrawCommand> TransparentCommands = [];
    internal int CandidateMeshlets;
    internal int ExcludedObjects;
    internal int CulledObjects;
    internal int CulledMeshlets;

    internal void Clear()
    {
        foreach (var bucket in Opaque) bucket.Clear();
        Transparent.Clear();
        TransparentCommands.Clear();
        CandidateMeshlets = ExcludedObjects = CulledObjects = CulledMeshlets = 0;
    }

    internal void SortTransparency()
    {
        Transparent.Sort(static (a, b) =>
        {
            int order = b.DistanceSquared.CompareTo(a.DistanceSquared);
            if (order == 0) order = a.Layer.CompareTo(b.Layer);
            if (order == 0) order = a.Command.MaterialIndex.CompareTo(b.Command.MaterialIndex);
            if (order == 0) order = a.Command.InstanceId.CompareTo(b.Command.InstanceId);
            return order != 0 ? order : a.Command.MeshletIndex.CompareTo(b.Command.MeshletIndex);
        });
        foreach (var draw in Transparent) TransparentCommands.Add(draw.Command);
    }
}

internal static class SecondaryViewVisibility
{
    internal static BoundingBox TransformSphere(Vector3 center, float radius, Matrix4x4 world)
    {
        Vector3 transformed = center * world;
        Vector3 extent = new(
            radius * MathF.Sqrt(world.M11 * world.M11 + world.M21 * world.M21 + world.M31 * world.M31),
            radius * MathF.Sqrt(world.M12 * world.M12 + world.M22 * world.M22 + world.M32 * world.M32),
            radius * MathF.Sqrt(world.M13 * world.M13 + world.M23 * world.M23 + world.M33 * world.M33));
        return new BoundingBox(transformed - extent, transformed + extent);
    }

    // AABB support vertices handle non-uniform scale, shear, and mirrored transforms.
    internal static bool OutsidePlane(BoundingBox bounds, Vector4 plane, float tolerance = 0f)
    {
        Vector3 point = new(
            plane.X >= 0 ? bounds.Max.X : bounds.Min.X,
            plane.Y >= 0 ? bounds.Max.Y : bounds.Min.Y,
            plane.Z >= 0 ? bounds.Max.Z : bounds.Min.Z);
        return plane.X * point.X + plane.Y * point.Y + plane.Z * point.Z + plane.W < -tolerance;
    }

    internal static bool IsVisible(BoundingBox bounds, in Frustum frustum, Vector4 clipPlane, float tolerance = 0.001f)
    {
        return !OutsidePlane(bounds, frustum.Left) && !OutsidePlane(bounds, frustum.Right) &&
               !OutsidePlane(bounds, frustum.Bottom) && !OutsidePlane(bounds, frustum.Top) &&
               !OutsidePlane(bounds, frustum.Near) && !OutsidePlane(bounds, frustum.Far) &&
               !OutsidePlane(bounds, clipPlane, tolerance);
    }
}
