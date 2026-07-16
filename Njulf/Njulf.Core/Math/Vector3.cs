using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Vector3 : IEquatable<Vector3>
    {
        public float X;
        public float Y;
        public float Z;

        public static readonly Vector3 Zero = new(0f, 0f, 0f);
        public static readonly Vector3 One = new(1f, 1f, 1f);
        public static readonly Vector3 UnitX = new(1f, 0f, 0f);
        public static readonly Vector3 UnitY = new(0f, 1f, 0f);
        public static readonly Vector3 UnitZ = new(0f, 0f, 1f);
        public static readonly Vector3 Forward = new(0f, 0f, -1f);
        public static readonly Vector3 Backward = new(0f, 0f, 1f);
        public static readonly Vector3 Up = new(0f, 1f, 0f);
        public static readonly Vector3 Down = new(0f, -1f, 0f);
        public static readonly Vector3 Right = new(1f, 0f, 0f);
        public static readonly Vector3 Left = new(-1f, 0f, 0f);

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Vector3(float value)
        {
            X = value;
            Y = value;
            Z = value;
        }

        public Vector3(Vector2 v, float z)
        {
            X = v.X;
            Y = v.Y;
            Z = z;
        }

        public float Length() => (float)System.Math.Sqrt(X * X + Y * Y + Z * Z);
        public float LengthSquared() => X * X + Y * Y + Z * Z;

        public Vector3 Normalized()
        {
            float len = Length();
            return len > 0 ? this / len : Zero;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator *(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.X * b, a.Y * b, a.Z * b);
        public static Vector3 operator *(float a, Vector3 b) => new(a * b.X, a * b.Y, a * b.Z);
        public static Vector3 operator /(Vector3 a, Vector3 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
        public static Vector3 operator /(Vector3 a, float b) => new(a.X / b, a.Y / b, a.Z / b);
        public static Vector3 operator -(Vector3 a) => new(-a.X, -a.Y, -a.Z);
        public static Vector3 operator *(Vector3 v, Matrix4x4 m) => new(
            v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41,
            v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42,
            v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43);

        public static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static float Distance(Vector3 a, Vector3 b) => (a - b).Length();
        public static float DistanceSquared(Vector3 a, Vector3 b) => (a - b).LengthSquared();

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t);

        public static Vector3 Min(Vector3 a, Vector3 b) => new(
            System.Math.Min(a.X, b.X),
            System.Math.Min(a.Y, b.Y),
            System.Math.Min(a.Z, b.Z));
        public static Vector3 Max(Vector3 a, Vector3 b) => new(
            System.Math.Max(a.X, b.X),
            System.Math.Max(a.Y, b.Y),
            System.Math.Max(a.Z, b.Z));
        public static Vector3 Clamp(Vector3 value, Vector3 min, Vector3 max) => new(
            System.Math.Clamp(value.X, min.X, max.X),
            System.Math.Clamp(value.Y, min.Y, max.Y),
            System.Math.Clamp(value.Z, min.Z, max.Z));

        public static bool operator ==(Vector3 a, Vector3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public bool Equals(Vector3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is Vector3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}
