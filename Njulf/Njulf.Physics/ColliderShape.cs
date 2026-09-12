using Jitter2.Collision.Shapes;
using Jitter2.Collision;
using Jitter2.LinearMath;
using Njulf.Core.Math;

namespace Njulf.Physics;

/// <summary>Immutable reusable geometry recipe. LocalTransform maps mesh/shape coordinates to the node pivot.</summary>
public sealed class ColliderShape
{
    private readonly Func<RigidBodyShape[]> _create;
    internal bool IsMesh { get; }
    public Matrix4x4 LocalTransform { get; }
    private ColliderShape(Func<RigidBodyShape[]> create, bool mesh, Matrix4x4 transform)
    {
        PhysicsMath.Decompose(transform);
        _create = create; IsMesh = mesh; LocalTransform = transform;
    }
    public ColliderShape WithLocalTransform(Matrix4x4 transform) => new(_create, IsMesh, transform);
    public static ColliderShape Box(Vector3 size)
    {
        PhysicsMath.Positive(size.X); PhysicsMath.Positive(size.Y); PhysicsMath.Positive(size.Z);
        return new(() => [new BoxShape(PhysicsMath.ToJ(size))], false, Matrix4x4.Identity);
    }
    public static ColliderShape Sphere(float radius)
    {
        PhysicsMath.Positive(radius);
        return new(() => [new SphereShape(radius)], false, Matrix4x4.Identity);
    }
    /// <summary>Y-axis capsule. Length is the straight segment between hemisphere centers.</summary>
    public static ColliderShape Capsule(float radius, float length)
    {
        PhysicsMath.Positive(radius);
        if (!float.IsFinite(length) || length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        return new(() => [new CapsuleShape(radius, length)], false, Matrix4x4.Identity);
    }
    public static ColliderShape ConvexHull(ReadOnlySpan<Vector3> vertices)
    {
        JVector[] copy = CopyVertices(vertices);
        // Validate the volume once; clones share immutable support data.
        var prototype = new PointCloudShape(copy.AsSpan());
        return new(() => [prototype.Clone()], false, Matrix4x4.Identity);
    }
    /// <summary>Static, two-sided triangles. Copies CPU data; rejects invalid indices and degenerate triangles.</summary>
    public static ColliderShape TriangleMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
    {
        JVector[] copy = CopyVertices(vertices);
        if (indices.Length == 0 || indices.Length % 3 != 0) throw new ArgumentException("Indices must contain complete triangles.");
        foreach (uint index in indices)
            if (index >= copy.Length) throw new ArgumentOutOfRangeException(nameof(indices));
        var mesh = new TriangleMesh(copy.AsSpan(), indices);
        return new(() => Enumerable.Range(0, mesh.Indices.Length).Select(i => (RigidBodyShape)new TwoSidedTriangle(mesh, i)).ToArray(), true, Matrix4x4.Identity);
    }
    private static JVector[] CopyVertices(ReadOnlySpan<Vector3> vertices)
    {
        if (vertices.IsEmpty) throw new ArgumentException("Geometry is empty.");
        var copy = new JVector[vertices.Length];
        for (int i = 0; i < copy.Length; i++) { PhysicsMath.Finite(vertices[i]); copy[i] = PhysicsMath.ToJ(vertices[i]); }
        return copy;
    }
    internal RigidBodyShape[] Create(float scale)
    {
        var m = LocalTransform * Matrix4x4.CreateScale(new Vector3(scale));
        // Transpose the row-vector linear transform for Jitter.
        var linear = new JMatrix(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, m.M13, m.M23, m.M33);
        var shapes = _create();
        if (m.Equals(Matrix4x4.Identity)) return shapes;
        for (int i = 0; i < shapes.Length; i++) shapes[i] = new TransformedShape(shapes[i], PhysicsMath.ToJ(m.Translation), linear);
        return shapes;
    }

    private sealed class TwoSidedTriangle(TriangleMesh mesh, int index) : TriangleShape(mesh, index)
    {
        public override bool LocalRayCast(in JVector origin, in JVector direction, out JVector normal, out float lambda)
        {
            // The generic shape routine includes edges, rejects hits behind the ray, and is two-sided.
            // Jitter's specialized triangle routine in 2.8.11 excludes edges and culls backfaces.
            return NarrowPhase.RayCast(this, origin, direction, out lambda, out normal);
        }
    }
}
