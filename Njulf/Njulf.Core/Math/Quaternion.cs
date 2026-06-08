using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public static readonly Quaternion Identity = new(0f, 0f, 0f, 1f);

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public Quaternion(Vector3 axis, float angle)
        {
            axis = axis.Normalized();
            float halfAngle = angle * 0.5f;
            float s = (float)System.Math.Sin(halfAngle);
            float c = (float)System.Math.Cos(halfAngle);
            X = axis.X * s;
            Y = axis.Y * s;
            Z = axis.Z * s;
            W = c;
        }

        public Quaternion(Vector3 eulerAngles)
        {
            float roll = eulerAngles.X * 0.5f;
            float pitch = eulerAngles.Y * 0.5f;
            float yaw = eulerAngles.Z * 0.5f;

            float sr = (float)System.Math.Sin(roll);
            float cr = (float)System.Math.Cos(roll);
            float sp = (float)System.Math.Sin(pitch);
            float cp = (float)System.Math.Cos(pitch);
            float sy = (float)System.Math.Sin(yaw);
            float cy = (float)System.Math.Cos(yaw);

            X = cy * sp * cr + sy * cp * sr;
            Y = sy * cp * cr - cy * sp * sr;
            Z = cy * cp * sr - sy * sp * cr;
            W = cy * cp * cr + sy * sp * sr;
        }

        public float Length() => (float)System.Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
        public float LengthSquared() => X * X + Y * Y + Z * Z + W * W;

        public Quaternion Normalized()
        {
            float len = Length();
            return len > 0 ? this / len : Identity;
        }

        public Quaternion Conjugate() => new(-X, -Y, -Z, W);

        public Quaternion Inverse()
        {
            float lenSq = LengthSquared();
            return lenSq > 0 ? Conjugate() / lenSq : Identity;
        }

        public static Quaternion operator +(Quaternion a, Quaternion b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        public static Quaternion operator -(Quaternion a, Quaternion b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
        public static Quaternion operator *(Quaternion a, Quaternion b) => new(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        public static Quaternion operator *(Quaternion q, float s) => new(q.X * s, q.Y * s, q.Z * s, q.W * s);
        public static Quaternion operator *(float s, Quaternion q) => new(q.X * s, q.Y * s, q.Z * s, q.W * s);
        public static Quaternion operator /(Quaternion q, float s) => new(q.X / s, q.Y / s, q.Z / s, q.W / s);
        public static Quaternion operator -(Quaternion q) => new(-q.X, -q.Y, -q.Z, -q.W);

        public static float Dot(Quaternion a, Quaternion b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            float dot = Dot(a, b);
            if (dot < 0)
            {
                b = -b;
                dot = -dot;
            }
            if (dot > 0.9995f)
            {
                return (a + (b - a) * t).Normalized();
            }
            float theta0 = (float)System.Math.Acos(dot);
            float theta = theta0 * t;
            float sinTheta = (float)System.Math.Sin(theta);
            float sinTheta0 = (float)System.Math.Sin(theta0);
            float s0 = System.Math.Cos(theta) - dot * sinTheta / sinTheta0;
            float s1 = sinTheta / sinTheta0;
            return a * s0 + b * s1;
        }

        public static Quaternion Lerp(Quaternion a, Quaternion b, float t) => (a + (b - a) * t).Normalized();

        public Vector3 ToEulerAngles()
        {
            float sinrCosp = 2f * (W * X + Y * Z);
            float cosrCosp = 1f - 2f * (X * X + Y * Y);
            float roll = (float)System.Math.Atan2(sinrCosp, cosrCosp);

            float sinp = 2f * (W * Y - Z * X);
            float pitch;
            if (System.Math.Abs(sinp) >= 1f)
                pitch = System.Math.CopySign((float)System.Math.PI / 2, sinp);
            else
                pitch = (float)System.Math.Asin(sinp);

            float sinyCosp = 2f * (W * Z + X * Y);
            float cosyCosp = 1f - 2f * (Y * Y + Z * Z);
            float yaw = (float)System.Math.Atan2(sinyCosp, cosyCosp);

            return new Vector3(roll, pitch, yaw);
        }

        public Matrix4x4 ToMatrix4x4()
        {
            float xx = X * X, yy = Y * Y, zz = Z * Z;
            float xy = X * Y, xz = X * Z, yz = Y * Z;
            float wx = W * X, wy = W * Y, wz = W * Z;

            return new Matrix4x4(
                1f - 2f * (yy + zz), 2f * (xy - wz), 2f * (xz + wy), 0f,
                2f * (xy + wz), 1f - 2f * (xx + zz), 2f * (yz - wx), 0f,
                2f * (xz - wy), 2f * (yz + wx), 1f - 2f * (xx + yy), 0f,
                0f, 0f, 0f, 1f);
        }

        public static Quaternion FromMatrix4x4(Matrix4x4 m)
        {
            float trace = m.M11 + m.M22 + m.M33;
            if (trace > 0)
            {
                float s = 0.5f / (float)System.Math.Sqrt(trace + 1);
                return new Quaternion(
                    (m.M32 - m.M23) * s,
                    (m.M13 - m.M31) * s,
                    (m.M21 - m.M12) * s,
                    0.25f / s);
            }
            else if (m.M11 > m.M22 && m.M11 > m.M33)
            {
                float s = 2f * (float)System.Math.Sqrt(1 + m.M11 - m.M22 - m.M33);
                return new Quaternion(
                    0.25f * s,
                    (m.M12 + m.M21) / s,
                    (m.M13 + m.M31) / s,
                    (m.M23 - m.M32) / s);
            }
            else if (m.M22 > m.M33)
            {
                float s = 2f * (float)System.Math.Sqrt(1 + m.M22 - m.M11 - m.M33);
                return new Quaternion(
                    (m.M12 + m.M21) / s,
                    0.25f * s,
                    (m.M23 + m.M32) / s,
                    (m.M31 - m.M13) / s);
            }
            else
            {
                float s = 2f * (float)System.Math.Sqrt(1 + m.M33 - m.M11 - m.M22);
                return new Quaternion(
                    (m.M13 + m.M31) / s,
                    (m.M23 + m.M32) / s,
                    0.25f * s,
                    (m.M12 - m.M21) / s);
            }
        }

        public bool Equals(Quaternion other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
        public override bool Equals(object obj) => obj is Quaternion other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
        public override string ToString() => $"({X}, {Y}, {Z}, {W})";
    }
}
