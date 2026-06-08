using System;
using System.Runtime.InteropServices;

namespace Njulf.Core.Math
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Color : IEquatable<Color>
    {
        public float R;
        public float G;
        public float B;
        public float A;

        public static readonly Color White = new(1f, 1f, 1f, 1f);
        public static readonly Color Black = new(0f, 0f, 0f, 1f);
        public static readonly Color Red = new(1f, 0f, 0f, 1f);
        public static readonly Color Green = new(0f, 1f, 0f, 1f);
        public static readonly Color Blue = new(0f, 0f, 1f, 1f);
        public static readonly Color Yellow = new(1f, 1f, 0f, 1f);
        public static readonly Color Cyan = new(0f, 1f, 1f, 1f);
        public static readonly Color Magenta = new(1f, 0f, 1f, 1f);
        public static readonly Color Gray = new(0.5f, 0.5f, 0.5f, 1f);
        public static readonly Color Transparent = new(0f, 0f, 0f, 0f);
        public static readonly Color CornflowerBlue = new(0.392f, 0.584f, 0.929f, 1f);

        public Color(float r, float g, float b, float a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public Color(float r, float g, float b) : this(r, g, b, 1f) { }

        public Color(Vector3 rgb, float a = 1f) : this(rgb.X, rgb.Y, rgb.Z, a) { }

        public Color(Vector4 rgba) : this(rgba.X, rgba.Y, rgba.Z, rgba.W) { }

        public static Color FromRgba(byte r, byte g, byte b, byte a) => new(
            r / 255f, g / 255f, b / 255f, a / 255f);

        public Vector3 ToVector3() => new(R, G, B);
        public Vector4 ToVector4() => new(R, G, B, A);

        public static Color operator +(Color a, Color b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
        public static Color operator -(Color a, Color b) => new(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);
        public static Color operator *(Color a, Color b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);
        public static Color operator *(Color a, float b) => new(a.R * b, a.G * b, a.B * b, a.A * b);
        public static Color operator *(float a, Color b) => new(a * b.R, a * b.G, a * b.B, a * b.A);

        public static Color Lerp(Color a, Color b, float t) => new(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);

        public bool Equals(Color other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object obj) => obj is Color other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public override string ToString() => $"R:{R} G:{G} B:{B} A:{A}";
    }
}
