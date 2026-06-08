using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BoundingSphere : IEquatable<BoundingSphere>
    {
        public Vector3 Center;
        public float Radius;

        public BoundingSphere(Vector3 center, float radius)
        {
            Center = center;
            Radius = radius;
        }

        public bool Contains(Vector3 point) =>
            Vector3.DistanceSquared(point, Center) <= Radius * Radius;

        public bool Contains(BoundingSphere other)
        {
            float distance = Vector3.Distance(Center, other.Center);
            return distance <= Radius - other.Radius;
        }

        public bool Intersects(BoundingSphere other)
        {
            float distance = Vector3.Distance(Center, other.Center);
            return distance <= Radius + other.Radius;
        }

        public bool Intersects(BoundingBox box) =>
            box.DistanceSquared(Center) <= Radius * Radius;

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
        public override bool Equals(object obj) => obj is BoundingSphere other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Center, Radius);
        public override string ToString() => $"Center:{Center} Radius:{Radius}";
    }
}
