using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BoundingBox : IEquatable<BoundingBox>
    {
        public Vector3 Min;
        public Vector3 Max;

        public BoundingBox(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;
        public Vector3 Extents => Size * 0.5f;

        public bool Contains(Vector3 point) =>
            point.X >= Min.X && point.X <= Max.X &&
            point.Y >= Min.Y && point.Y <= Max.Y &&
            point.Z >= Min.Z && point.Z <= Max.Z;

        public bool Intersects(BoundingBox other) =>
            Min.X <= other.Max.X && Max.X >= other.Min.X &&
            Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
            Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

        public bool Intersects(BoundingSphere sphere) =>
            DistanceSquared(Center, sphere.Center) <= sphere.Radius * sphere.Radius;

        public static float DistanceSquared(Vector3 a, Vector3 b) =>
            (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

        public static BoundingBox FromPoints(Vector3[] points)
        {
            if (points == null || points.Length == 0)
                throw new ArgumentException("Points array cannot be null or empty.");

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            for (int i = 0; i < points.Length; i++)
            {
                min.X = System.Math.Min(min.X, points[i].X);
                min.Y = System.Math.Min(min.Y, points[i].Y);
                min.Z = System.Math.Min(min.Z, points[i].Z);
                max.X = System.Math.Max(max.X, points[i].X);
                max.Y = System.Math.Max(max.Y, points[i].Y);
                max.Z = System.Math.Max(max.Z, points[i].Z);
            }

            return new BoundingBox(min, max);
        }

        public static BoundingBox Transform(BoundingBox box, Matrix4x4 matrix)
        {
            Vector3[] corners = new Vector3[8];
            corners[0] = box.Min;
            corners[1] = new Vector3(box.Max.X, box.Min.Y, box.Min.Z);
            corners[2] = new Vector3(box.Min.X, box.Max.Y, box.Min.Z);
            corners[3] = new Vector3(box.Max.X, box.Max.Y, box.Min.Z);
            corners[4] = new Vector3(box.Min.X, box.Min.Y, box.Max.Z);
            corners[5] = new Vector3(box.Max.X, box.Min.Y, box.Max.Z);
            corners[6] = new Vector3(box.Min.X, box.Max.Y, box.Max.Z);
            corners[7] = box.Max;

            for (int i = 0; i < 8; i++)
                corners[i] = matrix * corners[i];

            return FromPoints(corners);
        }

        public bool Equals(BoundingBox other) => Min == other.Min && Max == other.Max;
        public override bool Equals(object? obj) => obj is BoundingBox other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Min, Max);
        public override string ToString() => $"Min:{Min} Max:{Max}";
    }
}
