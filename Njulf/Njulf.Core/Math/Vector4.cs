using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    /// <summary>Single-precision framework math value; positions and distances use scene units unless an API specifies pixels.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Vector4 : IEquatable<Vector4>
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        /// <summary>The vector with every component zero.</summary>
        public static readonly Vector4 Zero = new(0f, 0f, 0f, 0f);
        /// <summary>The vector with every component one; useful for uniform scale.</summary>
        public static readonly Vector4 One = new(1f, 1f, 1f, 1f);
        /// <summary>Unit vector along positive X.</summary>
        public static readonly Vector4 UnitX = new(1f, 0f, 0f, 0f);
        /// <summary>Unit vector along positive Y.</summary>
        public static readonly Vector4 UnitY = new(0f, 1f, 0f, 0f);
        /// <summary>Unit vector along positive Z.</summary>
        public static readonly Vector4 UnitZ = new(0f, 0f, 1f, 0f);
        /// <summary>Unit vector along positive W.</summary>
        public static readonly Vector4 UnitW = new(0f, 0f, 0f, 1f);

        /// <summary>Creates a vector from its individual components.</summary>
        public Vector4(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>Creates a vector with every component set to the supplied value.</summary>
        public Vector4(float value)
        {
            X = value;
            Y = value;
            Z = value;
            W = value;
        }

        /// <summary>Creates a vector by copying the supplied lower-dimensional vector and appending the remaining components.</summary>
        public Vector4(Vector3 v, float w)
        {
            X = v.X;
            Y = v.Y;
            Z = v.Z;
            W = w;
        }

        /// <summary>Creates a vector by copying the supplied lower-dimensional vector and appending the remaining components.</summary>
        public Vector4(Vector2 v, float z, float w)
        {
            X = v.X;
            Y = v.Y;
            Z = z;
            W = w;
        }

        /// <summary>Returns the Euclidean length in the vector's units.</summary>
        public float Length() => (float)System.Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
        /// <summary>Returns squared length, avoiding a square root for comparisons.</summary>
        public float LengthSquared() => X * X + Y * Y + Z * Z + W * W;

        /// <summary>Returns a unit-length copy, or Zero when the length is zero; does not modify this vector.</summary>
        public Vector4 Normalized()
        {
            float len = Length();
            return len > 0 ? this / len : Zero;
        }

        /// <summary>Adds corresponding components.</summary>
        public static Vector4 operator +(Vector4 a, Vector4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        /// <summary>Subtracts corresponding components.</summary>
        public static Vector4 operator -(Vector4 a, Vector4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
        /// <summary>Multiplies corresponding components; this is not a dot product.</summary>
        public static Vector4 operator *(Vector4 a, Vector4 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W);
        /// <summary>Scales every component by b.</summary>
        public static Vector4 operator *(Vector4 a, float b) => new(a.X * b, a.Y * b, a.Z * b, a.W * b);
        /// <summary>Scales every component by a.</summary>
        public static Vector4 operator *(float a, Vector4 b) => new(a * b.X, a * b.Y, a * b.Z, a * b.W);
        /// <summary>Divides corresponding components using floating-point division, including IEEE zero-division results.</summary>
        public static Vector4 operator /(Vector4 a, Vector4 b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z, a.W / b.W);
        /// <summary>Divides every component by b using floating-point division.</summary>
        public static Vector4 operator /(Vector4 a, float b) => new(a.X / b, a.Y / b, a.Z / b, a.W / b);
        /// <summary>Negates every component.</summary>
        public static Vector4 operator -(Vector4 a) => new(-a.X, -a.Y, -a.Z, -a.W);

        /// <summary>Returns the component dot product; for unit vectors this is the cosine of the angle.</summary>
        public static float Dot(Vector4 a, Vector4 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        /// <summary>Returns Euclidean distance between the two positions.</summary>
        public static float Distance(Vector4 a, Vector4 b) => (a - b).Length();
        /// <summary>Returns squared distance between positions, suitable for radius comparisons.</summary>
        public static float DistanceSquared(Vector4 a, Vector4 b) => (a - b).LengthSquared();

        /// <summary>Interpolates componentwise from a to b. The factor t is not clamped; 0 returns a and 1 returns b.</summary>
        public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t,
            a.W + (b.W - a.W) * t);

        /// <summary>Exact component equality, matching Equals; NaN is unequal to itself.</summary>
        public static bool operator ==(Vector4 a, Vector4 b) => a.Equals(b);
        public static bool operator !=(Vector4 a, Vector4 b) => !a.Equals(b);
        public bool Equals(Vector4 other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
        public override bool Equals(object? obj) => obj is Vector4 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
        public override string ToString() => $"({X}, {Y}, {Z}, {W})";
    }
}
