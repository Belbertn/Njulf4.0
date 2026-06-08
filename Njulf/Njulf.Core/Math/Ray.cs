using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Ray : IEquatable<Ray>
    {
        public Vector3 Position;
        public Vector3 Direction;

        public Ray(Vector3 position, Vector3 direction)
        {
            Position = position;
            Direction = direction;
        }

        public bool Intersects(BoundingBox box, out float distance)
        {
            distance = 0f;
            float tmin = 0f;
            float tmax = float.MaxValue;

            Vector3 invDir = new Vector3(
                1f / Direction.X,
                1f / Direction.Y,
                1f / Direction.Z);

            Vector3 t1 = (box.Min - Position) * invDir;
            Vector3 t2 = (box.Max - Position) * invDir;

            Vector3 tminVec = Vector3.Min(t1, t2);
            Vector3 tmaxVec = Vector3.Max(t1, t2);

            tmin = System.Math.Max(System.Math.Max(tminVec.X, tminVec.Y), tminVec.Z);
            tmax = System.Math.Min(System.Math.Min(tmaxVec.X, tmaxVec.Y), tmaxVec.Z);

            if (tmax < 0 || tmin > tmax)
                return false;

            distance = tmin < 0 ? tmax : tmin;
            return true;
        }

        public bool Intersects(BoundingSphere sphere, out float distance)
        {
            Vector3 oc = Position - sphere.Center;
            float a = Vector3.Dot(Direction, Direction);
            float b = 2f * Vector3.Dot(oc, Direction);
            float c = Vector3.Dot(oc, oc) - sphere.Radius * sphere.Radius;
            float discriminant = b * b - 4f * a * c;

            if (discriminant < 0)
            {
                distance = 0f;
                return false;
            }

            distance = (-b - (float)System.Math.Sqrt(discriminant)) / (2f * a);
            return distance >= 0;
        }

        public Vector3 GetPointAt(float distance) => Position + Direction * distance;

        public bool Equals(Ray other) => Position == other.Position && Direction == other.Direction;
        public override bool Equals(object? obj) => obj is Ray other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Position, Direction);
        public override string ToString() => $"Position:{Position} Direction:{Direction}";
    }
}
