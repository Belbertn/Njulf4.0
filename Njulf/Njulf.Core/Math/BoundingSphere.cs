using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    /// <summary>A center and nonnegative radius in a common coordinate space. Boundary contact counts as intersection.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BoundingSphere : IEquatable<BoundingSphere>
    {
        public Vector3 Center;
        public float Radius;

        /// <summary>Stores a center and radius in the same coordinate space; supply a nonnegative radius.</summary>
        public BoundingSphere(Vector3 center, float radius)
        {
            Center = center;
            Radius = radius;
        }

        /// <summary>Tests whether the point lies inside or on the boundary.</summary>
        public bool Contains(Vector3 point) =>
            Vector3.DistanceSquared(point, Center) <= Radius * Radius;

        public bool Contains(BoundingSphere other)
        {
            float distance = Vector3.Distance(Center, other.Center);
            return distance <= Radius - other.Radius;
        }

        /// <summary>Tests intersection including touching boundaries, using a common coordinate space.</summary>
        public bool Intersects(BoundingSphere other)
        {
            float distance = Vector3.Distance(Center, other.Center);
            return distance <= Radius + other.Radius;
        }

        /// <summary>Tests intersection including touching boundaries, using a common coordinate space.</summary>
        public bool Intersects(BoundingBox box) => box.Intersects(this);

        /// <summary>Builds enclosing bounds from a nonempty point array; null and empty arrays are rejected.</summary>
        public static BoundingSphere FromPoints(Vector3[] points)
        {
            if (points == null || points.Length == 0)
                throw new ArgumentException("Points array cannot be null or empty.");

            BoundingBox box = BoundingBox.FromPoints(points);
            return FromBox(box);
        }

        public static BoundingSphere FromBox(BoundingBox box)
        {
            Vector3 center = box.Center;
            float radius = box.Extents.Length();
            return new BoundingSphere(center, radius);
        }

        public static BoundingSphere Transform(BoundingSphere sphere, Matrix4x4 matrix)
        {
            Vector3 scale = matrix.Scale;
            float maxScale = System.Math.Max(System.Math.Max(scale.X, scale.Y), scale.Z);
            return new BoundingSphere(
                matrix * sphere.Center,
                sphere.Radius * maxScale);
        }

        public bool Equals(BoundingSphere other) => Center == other.Center && Radius == other.Radius;
        public override bool Equals(object? obj) => obj is BoundingSphere other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Center, Radius);
        public override string ToString() => $"Center:{Center} Radius:{Radius}";
    }
}
