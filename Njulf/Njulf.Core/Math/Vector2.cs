using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    /// <summary>Single-precision framework math value; positions and distances use scene units unless an API specifies pixels.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Vector2 : IEquatable<Vector2>
    {
        public float X;
        public float Y;

        /// <summary>The vector with every component zero.</summary>
        public static readonly Vector2 Zero = new(0f, 0f);
        /// <summary>The vector with every component one; useful for uniform scale.</summary>
        public static readonly Vector2 One = new(1f, 1f);
        /// <summary>Unit vector along positive X.</summary>
        public static readonly Vector2 UnitX = new(1f, 0f);
        /// <summary>Unit vector along positive Y.</summary>
        public static readonly Vector2 UnitY = new(0f, 1f);

        /// <summary>Creates a vector from its individual components.</summary>
        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Creates a vector with every component set to the supplied value.</summary>
        public Vector2(float value)
        {
            X = value;
            Y = value;
        }

        /// <summary>Returns the Euclidean length in the vector's units.</summary>
        public float Length() => (float)System.Math.Sqrt(X * X + Y * Y);
        /// <summary>Returns squared length, avoiding a square root for comparisons.</summary>
        public float LengthSquared() => X * X + Y * Y;

        /// <summary>Returns a unit-length copy, or Zero when the length is zero; does not modify this vector.</summary>
        public Vector2 Normalized()
        {
            float len = Length();
            return len > 0 ? this / len : Zero;
        }

        /// <summary>Adds corresponding components.</summary>
        public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
        /// <summary>Subtracts corresponding components.</summary>
        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
        /// <summary>Multiplies corresponding components; this is not a dot product.</summary>
        public static Vector2 operator *(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);
        /// <summary>Scales every component by b.</summary>
        public static Vector2 operator *(Vector2 a, float b) => new(a.X * b, a.Y * b);
        /// <summary>Scales every component by a.</summary>
        public static Vector2 operator *(float a, Vector2 b) => new(a * b.X, a * b.Y);
        /// <summary>Divides corresponding components using floating-point division, including IEEE zero-division results.</summary>
        public static Vector2 operator /(Vector2 a, Vector2 b) => new(a.X / b.X, a.Y / b.Y);
        /// <summary>Divides every component by b using floating-point division.</summary>
        public static Vector2 operator /(Vector2 a, float b) => new(a.X / b, a.Y / b);
        /// <summary>Negates every component.</summary>
        public static Vector2 operator -(Vector2 a) => new(-a.X, -a.Y);

        /// <summary>Returns the component dot product; for unit vectors this is the cosine of the angle.</summary>
        public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;
        /// <summary>Returns Euclidean distance between the two positions.</summary>
        public static float Distance(Vector2 a, Vector2 b) => (a - b).Length();
        /// <summary>Returns squared distance between positions, suitable for radius comparisons.</summary>
        public static float DistanceSquared(Vector2 a, Vector2 b) => (a - b).LengthSquared();

        /// <summary>Interpolates componentwise from a to b. The factor t is not clamped; 0 returns a and 1 returns b.</summary>
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t);

        /// <summary>Exact component equality, matching Equals; NaN is unequal to itself.</summary>
        public static bool operator ==(Vector2 a, Vector2 b) => a.Equals(b);
        public static bool operator !=(Vector2 a, Vector2 b) => !a.Equals(b);
        public bool Equals(Vector2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Vector2 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X}, {Y})";
    }
}
