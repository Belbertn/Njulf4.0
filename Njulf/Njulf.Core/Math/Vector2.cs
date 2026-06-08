using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Vector2 : IEquatable<Vector2>
    {
        public float X;
        public float Y;

        public static readonly Vector2 Zero = new(0f, 0f);
        public static readonly Vector2 One = new(1f, 1f);
        public static readonly Vector2 UnitX = new(1f, 0f);
        public static readonly Vector2 UnitY = new(0f, 1f);

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public Vector2(float value)
        {
            X = value;
            Y = value;
        }

        public float Length() => (float)System.Math.Sqrt(X * X + Y * Y);
        public float LengthSquared() => X * X + Y * Y;

        public Vector2 Normalized()
        {
            float len = Length();
            return len > 0 ? this / len : Zero;
        }

        public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
        public static Vector2 operator *(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);
        public static Vector2 operator *(Vector2 a, float b) => new(a.X * b, a.Y * b);
        public static Vector2 operator *(float a, Vector2 b) => new(a * b.X, a * b.Y);
        public static Vector2 operator /(Vector2 a, Vector2 b) => new(a.X / b.X, a.Y / b.Y);
        public static Vector2 operator /(Vector2 a, float b) => new(a.X / b, a.Y / b);
        public static Vector2 operator -(Vector2 a) => new(-a.X, -a.Y);

        public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;
        public static float Distance(Vector2 a, Vector2 b) => (a - b).Length();
        public static float DistanceSquared(Vector2 a, Vector2 b) => (a - b).LengthSquared();

        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t);

        public bool Equals(Vector2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Vector2 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X}, {Y})";
    }
}
