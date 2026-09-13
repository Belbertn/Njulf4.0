using Njulf.Core.Math;

namespace Njulf.Assets;

/// <summary>Centered CPU primitives for ProcessedMeshAssetBuilder. Dimensions are world units, matching collider factories.</summary>
public static class PrimitiveMesh
{
    /// <summary>Box with full dimensions, separate face normals, and a [0,1] UV square per face.</summary>
    public static ModelMesh Box(Vector3 size, bool tangents = false)
    {
        Positive(size.X, nameof(size)); Positive(size.Y, nameof(size)); Positive(size.Z, nameof(size));
        var mesh = new Builder(tangents);
        Vector3[] normals = [Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Forward, Vector3.Backward];
        foreach (Vector3 normal in normals)
        {
            Vector3 u = Vector3.Cross(MathF.Abs(normal.Y) > .5f ? Vector3.UnitZ : Vector3.Up, normal);
            Vector3 v = Vector3.Cross(normal, u);
            uint start = (uint)mesh.Positions.Count;
            mesh.Vertex((normal - u - v) * size * .5f, normal, new(0, 0), u, v);
            mesh.Vertex((normal + u - v) * size * .5f, normal, new(1, 0), u, v);
            mesh.Vertex((normal + u + v) * size * .5f, normal, new(1, 1), u, v);
            mesh.Vertex((normal - u + v) * size * .5f, normal, new(0, 1), u, v);
            mesh.Triangle(start, start + 1, start + 2);
            mesh.Triangle(start, start + 2, start + 3);
        }
        return mesh.Build("Box", new(-size * .5f, size * .5f));
    }

    /// <summary>Single-sided XZ plane, +Y normal, full width/depth. For collision use its triangles as a static mesh.</summary>
    public static ModelMesh Plane(float width, float depth, bool tangents = false)
    {
        Positive(width, nameof(width)); Positive(depth, nameof(depth));
        var mesh = new Builder(tangents);
        Vector3 u = Vector3.Right, v = Vector3.Forward;
        mesh.Vertex(new(-width / 2, 0, depth / 2), Vector3.Up, new(0, 0), u, v);
        mesh.Vertex(new(width / 2, 0, depth / 2), Vector3.Up, new(1, 0), u, v);
        mesh.Vertex(new(width / 2, 0, -depth / 2), Vector3.Up, new(1, 1), u, v);
        mesh.Vertex(new(-width / 2, 0, -depth / 2), Vector3.Up, new(0, 1), u, v);
        mesh.Triangle(0, 1, 2); mesh.Triangle(0, 2, 3);
        return mesh.Build("Plane", new(new(-width / 2, 0, -depth / 2), new(width / 2, 0, depth / 2)));
    }

    /// <summary>UV sphere with a duplicated longitude seam. V runs from bottom to top.</summary>
    public static ModelMesh Sphere(float radius, int radialSegments = 32, int latitudeSegments = 16, bool tangents = false)
    {
        Positive(radius, nameof(radius)); Segments(radialSegments, latitudeSegments);
        var rings = new List<Ring>();
        for (int i = 0; i <= latitudeSegments; i++)
        {
            float fraction = (float)i / latitudeSegments;
            float angle = (fraction - .5f) * MathF.PI;
            float radial = i == 0 || i == latitudeSegments ? 0 : MathF.Cos(angle);
            float y = i == 0 ? -1 : i == latitudeSegments ? 1 : MathF.Sin(angle);
            rings.Add(new(radial * radius, y * radius, radial, y, fraction));
        }
        return Revolve("Sphere", rings, radialSegments, tangents, new(new(-radius), new(radius)));
    }

    /// <summary>Y-axis capsule. Length is the straight segment between hemisphere centers; total height is length + 2*radius.</summary>
    /// <remarks>Zero length is a sphere. Latitude subdivisions are split between the two hemispheres. V follows surface arc length.</remarks>
    public static ModelMesh Capsule(float radius, float length, int radialSegments = 32, int latitudeSegments = 16, bool tangents = false)
    {
        Positive(radius, nameof(radius)); Segments(radialSegments, latitudeSegments);
        if (!float.IsFinite(length) || length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return Sphere(radius, radialSegments, latitudeSegments, tangents);
        var rings = new List<Ring>();
        int bottom = latitudeSegments / 2, top = latitudeSegments - bottom;
        double surfaceLength = Math.PI * radius + length;
        for (int i = 0; i <= bottom; i++)
        {
            float angle = ((float)i / bottom - 1) * MathF.PI / 2;
            float radial = i == 0 ? 0 : i == bottom ? 1 : MathF.Cos(angle);
            float y = i == 0 ? -1 : i == bottom ? 0 : MathF.Sin(angle);
            rings.Add(new(radial * radius, -length / 2 + y * radius, radial, y,
                (float)((Math.PI / 2 * i / bottom * radius) / surfaceLength)));
        }
        for (int i = 0; i <= top; i++)
        {
            float angle = (float)i / top * MathF.PI / 2;
            float radial = i == top ? 0 : i == 0 ? 1 : MathF.Cos(angle);
            float y = i == top ? 1 : i == 0 ? 0 : MathF.Sin(angle);
            rings.Add(new(radial * radius, length / 2 + y * radius, radial, y,
                (float)((Math.PI / 2 * radius + length + Math.PI / 2 * i / top * radius) / surfaceLength)));
        }
        return Revolve("Capsule", rings, radialSegments, tangents,
            new(new(-radius, -length / 2 - radius, -radius), new(radius, length / 2 + radius, radius)));
    }

    private readonly record struct Ring(float Radius, float Y, float NormalRadius, float NormalY, float V);

    private static ModelMesh Revolve(string name, List<Ring> rings, int segments, bool tangents, BoundingBox bounds)
    {
        var mesh = new Builder(tangents);
        foreach (Ring ring in rings)
            for (int column = 0; column <= segments; column++)
            {
                float u = (float)column / segments;
                float angle = column == segments ? 0 : u * MathF.Tau;
                float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
                Vector3 normal = new(ring.NormalRadius * cos, ring.NormalY, ring.NormalRadius * sin);
                Vector3 tangent = new(-sin, 0, cos);
                mesh.Vertex(new(ring.Radius * cos, ring.Y, ring.Radius * sin), normal, new(u, ring.V),
                    tangent, Vector3.Cross(tangent, normal));
            }
        for (int row = 0; row < rings.Count - 1; row++)
            for (int column = 0; column < segments; column++)
            {
                uint a = (uint)(row * (segments + 1) + column), b = a + 1;
                uint c = a + (uint)segments + 1, d = c + 1;
                if (rings[row].Radius != 0) mesh.Triangle(a, c, b);
                if (rings[row + 1].Radius != 0) mesh.Triangle(b, c, d);
            }
        return mesh.Build(name, bounds);
    }

    private static void Positive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name, "Expected a finite positive dimension.");
    }

    private static void Segments(int radial, int latitude)
    {
        if (radial < 3) throw new ArgumentOutOfRangeException(nameof(radial), "At least three radial segments are required.");
        if (latitude < 2) throw new ArgumentOutOfRangeException(nameof(latitude), "At least two latitude segments are required.");
        _ = checked((radial + 1) * (latitude + 2));
    }

    private sealed class Builder(bool tangents)
    {
        internal readonly List<Vector3> Positions = [];
        private readonly List<Vector3> _normals = [], _tangents = [], _bitangents = [];
        private readonly List<Vector2> _uv = [];
        private readonly List<uint> _indices = [];
        internal void Vertex(Vector3 position, Vector3 normal, Vector2 uv, Vector3 tangent, Vector3 bitangent)
        {
            Positions.Add(position); _normals.Add(normal); _uv.Add(uv);
            if (tangents) { _tangents.Add(tangent); _bitangents.Add(bitangent); }
        }
        internal void Triangle(uint a, uint b, uint c) { _indices.Add(a); _indices.Add(b); _indices.Add(c); }
        internal ModelMesh Build(string name, BoundingBox bounds)
        {
            var mesh = new ModelMesh
            {
                Name = name, Vertices = Positions.ToArray(), Normals = _normals.ToArray(), TexCoords = _uv.ToArray(),
                Tangents = _tangents.ToArray(), Bitangents = _bitangents.ToArray(), Indices = _indices.ToArray(),
                BoundingBox = bounds, BoundingSphere = BoundingSphere.FromBox(bounds)
            };
            mesh.Materials.Add(ModelMaterial.Default);
            return mesh;
        }
    }
}
