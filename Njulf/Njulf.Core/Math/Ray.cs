using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    /// <summary>A ray with an arbitrary nonzero finite direction. Queries and GetPointAt use world-unit distances.</summary>
    /// <remarks>Inside/on-surface hits have distance zero. Invalid origins/directions throw; misses return distance zero.</remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Ray : IEquatable<Ray>
    {
        public Vector3 Position;
        public Vector3 Direction;

        /// <summary>Stores origin and direction without normalization. Queries validate and normalize the direction.</summary>
        public Ray(Vector3 position, Vector3 direction)
        {
            Position = position;
            Direction = direction;
        }

        /// <summary>Tests the forward ray against a box, returning world-unit entry distance; inside hits and misses use zero.</summary>
        public bool Intersects(BoundingBox box, out float distance)
        {
            Vector3 direction = UnitDirection();
            distance = 0f;
            double near = 0, far = double.PositiveInfinity;
            if (!Slab(Position.X, direction.X, box.Min.X, box.Max.X, ref near, ref far) ||
                !Slab(Position.Y, direction.Y, box.Min.Y, box.Max.Y, ref near, ref far) ||
                !Slab(Position.Z, direction.Z, box.Min.Z, box.Max.Z, ref near, ref far))
                return false;
            distance = (float)near;
            return true;
        }

        public bool Intersects(BoundingSphere sphere, out float distance)
        {
            Vector3 direction = UnitDirection();
            distance = 0;
            double x = (double)Position.X - sphere.Center.X;
            double y = (double)Position.Y - sphere.Center.Y;
            double z = (double)Position.Z - sphere.Center.Z;
            double c = x * x + y * y + z * z - (double)sphere.Radius * sphere.Radius;
            if (c <= 0) return true;
            double a = (double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z;
            double b = x * direction.X + y * direction.Y + z * direction.Z;
            double discriminant = b * b - a * c;
            if (b > 0 || discriminant < 0) return false;
            distance = (float)(c / (-b + System.Math.Sqrt(discriminant)));
            return true;
        }

        /// <summary>Returns a point a finite nonnegative distance along the normalized direction. Zero or nonfinite directions are rejected.</summary>
        public Vector3 GetPointAt(float distance)
        {
            if (!float.IsFinite(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
            return Position + UnitDirection() * distance;
        }

        private Vector3 UnitDirection()
        {
            if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) || !float.IsFinite(Position.Z))
                throw new ArgumentException("Ray position must be finite.", nameof(Position));
            double length = System.Math.Sqrt((double)Direction.X * Direction.X + (double)Direction.Y * Direction.Y + (double)Direction.Z * Direction.Z);
            if (!double.IsFinite(length) || length == 0)
                throw new ArgumentException("Ray direction must be finite and nonzero.", nameof(Direction));
            return new Vector3((float)(Direction.X / length), (float)(Direction.Y / length), (float)(Direction.Z / length));
        }

        private static bool Slab(float origin, float direction, float min, float max, ref double near, ref double far)
        {
            if (direction == 0) return origin >= min && origin <= max;
            double a = ((double)min - origin) / direction, b = ((double)max - origin) / direction;
            near = System.Math.Max(near, System.Math.Min(a, b));
            far = System.Math.Min(far, System.Math.Max(a, b));
            return near <= far;
        }

        public bool Equals(Ray other) => Position == other.Position && Direction == other.Direction;
        public override bool Equals(object? obj) => obj is Ray other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Position, Direction);
        public override string ToString() => $"Position:{Position} Direction:{Direction}";
    }
}
