using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Matrix4x4 : IEquatable<Matrix4x4>
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public static readonly Matrix4x4 Identity = new(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f);

        public static readonly Matrix4x4 Zero = new(
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f);

        public Matrix4x4(
            float m11, float m12, float m13, float m14,
            float m21, float m22, float m23, float m24,
            float m31, float m32, float m33, float m34,
            float m41, float m42, float m43, float m44)
        {
            M11 = m11; M12 = m12; M13 = m13; M14 = m14;
            M21 = m21; M22 = m22; M23 = m23; M24 = m24;
            M31 = m31; M32 = m32; M33 = m33; M34 = m34;
            M41 = m41; M42 = m42; M43 = m43; M44 = m44;
        }

        public float this[int row, int col]
        {
            get
            {
                if (row < 0 || row > 3 || col < 0 || col > 3)
                    throw new IndexOutOfRangeException();
                return row switch
                {
                    0 => col switch { 0 => M11, 1 => M12, 2 => M13, _ => M14 },
                    1 => col switch { 0 => M21, 1 => M22, 2 => M23, _ => M24 },
                    2 => col switch { 0 => M31, 1 => M32, 2 => M33, _ => M34 },
                    _ => col switch { 0 => M41, 1 => M42, 2 => M43, _ => M44 }
                };
            }
            set
            {
                if (row < 0 || row > 3 || col < 0 || col > 3)
                    throw new IndexOutOfRangeException();
                switch (row)
                {
                    case 0:
                        switch (col)
                        { case 0: M11 = value; break; case 1: M12 = value; break; case 2: M13 = value; break; default: M14 = value; break; }
                        break;
                    case 1:
                        switch (col)
                        { case 0: M21 = value; break; case 1: M22 = value; break; case 2: M23 = value; break; default: M24 = value; break; }
                        break;
                    case 2:
                        switch (col)
                        { case 0: M31 = value; break; case 1: M32 = value; break; case 2: M33 = value; break; default: M34 = value; break; }
                        break;
                    default:
                        switch (col)
                        { case 0: M41 = value; break; case 1: M42 = value; break; case 2: M43 = value; break; default: M44 = value; break; }
                        break;
                }
            }
        }

        public Vector3 Translation => new(M41, M42, M43);
        public Vector3 Scale => new(
            (float)System.Math.Sqrt(M11 * M11 + M12 * M12 + M13 * M13),
            (float)System.Math.Sqrt(M21 * M21 + M22 * M22 + M23 * M23),
            (float)System.Math.Sqrt(M31 * M31 + M32 * M32 + M33 * M33));

        public static Matrix4x4 operator +(Matrix4x4 a, Matrix4x4 b) => new(
            a.M11 + b.M11, a.M12 + b.M12, a.M13 + b.M13, a.M14 + b.M14,
            a.M21 + b.M21, a.M22 + b.M22, a.M23 + b.M23, a.M24 + b.M24,
            a.M31 + b.M31, a.M32 + b.M32, a.M33 + b.M33, a.M34 + b.M34,
            a.M41 + b.M41, a.M42 + b.M42, a.M43 + b.M43, a.M44 + b.M44);

        public static Matrix4x4 operator -(Matrix4x4 a, Matrix4x4 b) => new(
            a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13, a.M14 - b.M14,
            a.M21 - b.M21, a.M22 - b.M22, a.M23 - b.M23, a.M24 - b.M24,
            a.M31 - b.M31, a.M32 - b.M32, a.M33 - b.M33, a.M34 - b.M34,
            a.M41 - b.M41, a.M42 - b.M42, a.M43 - b.M43, a.M44 - b.M44);

        public static Matrix4x4 operator *(Matrix4x4 a, Matrix4x4 b) => new(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
            a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
            a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
            a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
            a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
            a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
            a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
            a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
            a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
            a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
            a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
            a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
            a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
            a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);

        public static Vector4 operator *(Matrix4x4 m, Vector4 v) => new(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
            m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);

        public static Vector3 operator *(Matrix4x4 m, Vector3 v) => new(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34);

        public static Matrix4x4 operator *(Matrix4x4 m, float s) => new(
            m.M11 * s, m.M12 * s, m.M13 * s, m.M14 * s,
            m.M21 * s, m.M22 * s, m.M23 * s, m.M24 * s,
            m.M31 * s, m.M32 * s, m.M33 * s, m.M34 * s,
            m.M41 * s, m.M42 * s, m.M43 * s, m.M44 * s);

        public static Matrix4x4 CreateTranslation(Vector3 position) => new(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            position.X, position.Y, position.Z, 1f);

        public static Matrix4x4 CreateScale(Vector3 scale) => new(
            scale.X, 0f, 0f, 0f,
            0f, scale.Y, 0f, 0f,
            0f, 0f, scale.Z, 0f,
            0f, 0f, 0f, 1f);

        public static Matrix4x4 CreateRotationX(float radians)
        {
            float c = (float)System.Math.Cos(radians);
            float s = (float)System.Math.Sin(radians);
            return new(
                1f, 0f, 0f, 0f,
                0f, c, -s, 0f,
                0f, s, c, 0f,
                0f, 0f, 0f, 1f);
        }

        public static Matrix4x4 CreateRotationY(float radians)
        {
            float c = (float)System.Math.Cos(radians);
            float s = (float)System.Math.Sin(radians);
            return new(
                c, 0f, s, 0f,
                0f, 1f, 0f, 0f,
                -s, 0f, c, 0f,
                0f, 0f, 0f, 1f);
        }

        public static Matrix4x4 CreateRotationZ(float radians)
        {
            float c = (float)System.Math.Cos(radians);
            float s = (float)System.Math.Sin(radians);
            return new(
                c, -s, 0f, 0f,
                s, c, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f);
        }

        public static Matrix4x4 CreateFromAxisAngle(Vector3 axis, float angle)
        {
            float c = (float)System.Math.Cos(angle);
            float s = (float)System.Math.Sin(angle);
            float t = 1f - c;
            axis = axis.Normalized();
            return new(
                t * axis.X * axis.X + c, t * axis.X * axis.Y - s * axis.Z, t * axis.X * axis.Z + s * axis.Y, 0f,
                t * axis.X * axis.Y + s * axis.Z, t * axis.Y * axis.Y + c, t * axis.Y * axis.Z - s * axis.X, 0f,
                t * axis.X * axis.Z - s * axis.Y, t * axis.Y * axis.Z + s * axis.X, t * axis.Z * axis.Z + c, 0f,
                0f, 0f, 0f, 1f);
        }

        public static Matrix4x4 CreateLookAt(Vector3 cameraPosition, Vector3 cameraTarget, Vector3 cameraUp)
        {
            Vector3 z = (cameraPosition - cameraTarget).Normalized();
            Vector3 x = Vector3.Cross(cameraUp, z).Normalized();
            Vector3 y = Vector3.Cross(z, x);
            return new(
                x.X, x.Y, x.Z, 0f,
                y.X, y.Y, y.Z, 0f,
                z.X, z.Y, z.Z, 0f,
                -Vector3.Dot(x, cameraPosition), -Vector3.Dot(y, cameraPosition), -Vector3.Dot(z, cameraPosition), 1f);
        }

        public static Matrix4x4 CreatePerspectiveFieldOfView(float fieldOfView, float aspectRatio, float nearPlaneDistance, float farPlaneDistance)
        {
            float f = 1f / (float)System.Math.Tan(fieldOfView * 0.5f);
            return new(
                f / aspectRatio, 0f, 0f, 0f,
                0f, f, 0f, 0f,
                0f, 0f, farPlaneDistance / (nearPlaneDistance - farPlaneDistance), -1f,
                0f, 0f, nearPlaneDistance * farPlaneDistance / (nearPlaneDistance - farPlaneDistance), 0f);
        }

        public static Matrix4x4 CreateOrthographic(float width, float height, float zNearPlane, float zFarPlane)
        {
            return new(
                2f / width, 0f, 0f, 0f,
                0f, 2f / height, 0f, 0f,
                0f, 0f, 1f / (zNearPlane - zFarPlane), 0f,
                0f, 0f, zNearPlane / (zNearPlane - zFarPlane), 1f);
        }

        public Matrix4x4 Transpose() => new(
            M11, M21, M31, M41,
            M12, M22, M32, M42,
            M13, M23, M33, M43,
            M14, M24, M34, M44);

        public Matrix4x4 Invert()
        {
            float det = Determinant();
            if (System.Math.Abs(det) < float.Epsilon)
                throw new InvalidOperationException("Matrix is singular and cannot be inverted.");
            float invDet = 1f / det;
            return new(
                (M22 * M33 * M44 + M23 * M34 * M42 + M24 * M32 * M43 - M22 * M34 * M43 - M23 * M32 * M44 - M24 * M33 * M42) * invDet,
                (M12 * M34 * M43 + M13 * M32 * M44 + M14 * M33 * M42 - M12 * M33 * M44 - M13 * M34 * M42 - M14 * M32 * M43) * invDet,
                (M12 * M24 * M43 + M13 * M22 * M44 + M14 * M23 * M42 - M12 * M23 * M44 - M13 * M24 * M42 - M14 * M22 * M43) * invDet,
                (M12 * M23 * M34 + M13 * M24 * M32 + M14 * M22 * M33 - M12 * M22 * M34 - M13 * M23 * M32 - M14 * M24 * M33) * invDet,
                (M21 * M34 * M43 + M23 * M32 * M41 + M24 * M33 * M41 - M21 * M33 * M44 - M23 * M34 * M41 - M24 * M31 * M43) * invDet,
                (M11 * M33 * M44 + M13 * M34 * M41 + M14 * M31 * M43 - M11 * M34 * M43 - M13 * M31 * M44 - M14 * M33 * M41) * invDet,
                (M11 * M24 * M43 + M13 * M22 * M41 + M14 * M23 * M41 - M11 * M23 * M44 - M13 * M24 * M41 - M14 * M21 * M43) * invDet,
                (M11 * M23 * M34 + M13 * M24 * M31 + M14 * M21 * M33 - M11 * M22 * M34 - M13 * M21 * M33 - M14 * M23 * M31) * invDet,
                (M21 * M32 * M44 + M22 * M34 * M41 + M24 * M31 * M42 - M21 * M34 * M42 - M22 * M31 * M44 - M24 * M32 * M41) * invDet,
                (M11 * M34 * M42 + M12 * M31 * M44 + M14 * M32 * M41 - M11 * M32 * M44 - M12 * M34 * M41 - M14 * M31 * M42) * invDet,
                (M11 * M22 * M44 + M12 * M24 * M41 + M14 * M21 * M42 - M11 * M24 * M42 - M12 * M21 * M44 - M14 * M22 * M41) * invDet,
                (M11 * M22 * M34 + M12 * M24 * M31 + M14 * M21 * M32 - M11 * M22 * M34 - M12 * M21 * M33 - M14 * M24 * M31) * invDet,
                (M21 * M32 * M43 + M22 * M33 * M41 + M23 * M31 * M42 - M21 * M33 * M42 - M22 * M31 * M43 - M23 * M32 * M41) * invDet,
                (M11 * M33 * M42 + M12 * M31 * M43 + M13 * M32 * M41 - M11 * M32 * M43 - M12 * M33 * M41 - M13 * M31 * M42) * invDet,
                (M11 * M23 * M42 + M12 * M21 * M43 + M13 * M22 * M41 - M11 * M22 * M43 - M12 * M23 * M41 - M13 * M21 * M42) * invDet,
                (M11 * M23 * M32 + M12 * M21 * M33 + M13 * M22 * M31 - M11 * M22 * M33 - M12 * M23 * M31 - M13 * M21 * M32) * invDet);
        }

        public float Determinant() =>
            M11 * M22 * M33 * M44 + M11 * M23 * M34 * M42 + M11 * M24 * M32 * M43
            - M11 * M22 * M34 * M43 - M11 * M23 * M32 * M44 - M11 * M24 * M33 * M42
            - M12 * M21 * M33 * M44 - M12 * M23 * M34 * M41 - M12 * M24 * M31 * M43
            + M12 * M21 * M34 * M43 + M12 * M23 * M31 * M44 + M12 * M24 * M33 * M41
            + M13 * M21 * M32 * M44 + M13 * M22 * M34 * M41 + M13 * M24 * M31 * M42
            - M13 * M21 * M34 * M42 - M13 * M22 * M31 * M44 - M13 * M24 * M32 * M41
            - M14 * M21 * M32 * M43 - M14 * M22 * M33 * M41 - M14 * M23 * M31 * M42
            + M14 * M21 * M33 * M42 + M14 * M22 * M31 * M43 + M14 * M23 * M32 * M41;

        public bool Equals(Matrix4x4 other) =>
            M11 == other.M11 && M12 == other.M12 && M13 == other.M13 && M14 == other.M14 &&
            M21 == other.M21 && M22 == other.M22 && M23 == other.M23 && M24 == other.M24 &&
            M31 == other.M31 && M32 == other.M32 && M33 == other.M33 && M34 == other.M34 &&
            M41 == other.M41 && M42 == other.M42 && M43 == other.M43 && M44 == other.M44;

        public override bool Equals(object obj) => obj is Matrix4x4 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44);
        public override string ToString() => $"[{M11}, {M12}, {M13}, {M14}|{M21}, {M22}, {M23}, {M24}|{M31}, {M32}, {M33}, {M34}|{M41}, {M42}, {M43}, {M44}]";
    }
}
