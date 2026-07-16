using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Njulf.Rendering.Resources
{
    internal sealed class HdrEquirectangularImage
    {
        public HdrEquirectangularImage(uint width, uint height, float[] rgbPixels)
        {
            if (width == 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height == 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (rgbPixels == null)
                throw new ArgumentNullException(nameof(rgbPixels));
            if (rgbPixels.Length != checked((int)(width * height * 3u)))
                throw new ArgumentException("HDR image data must contain tightly packed RGB float pixels.", nameof(rgbPixels));

            Width = width;
            Height = height;
            RgbPixels = rgbPixels;
        }

        public uint Width { get; }
        public uint Height { get; }
        public float[] RgbPixels { get; }
    }

    internal static class EnvironmentMapProcessor
    {
        private const int DiffuseIrradianceSampleCount = 128;
        private const int SpecularPrefilterSampleCount = 128;
        private const float Pi = MathF.PI;
        private const float TwoPi = MathF.PI * 2.0f;

        public static HdrEquirectangularImage LoadRadianceHdr(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("HDR path cannot be null or empty.", nameof(path));

            using var stream = File.OpenRead(path);
            string signature = ReadAsciiLine(stream);
            if (!signature.StartsWith("#?", StringComparison.Ordinal))
                throw new InvalidDataException("Radiance HDR file is missing the signature line.");

            bool hasRgbEFormat = false;
            while (true)
            {
                string line = ReadAsciiLine(stream);
                if (line.Length == 0)
                    break;

                if (line.Equals("FORMAT=32-bit_rle_rgbe", StringComparison.Ordinal))
                    hasRgbEFormat = true;
            }

            if (!hasRgbEFormat)
                throw new InvalidDataException("Only Radiance FORMAT=32-bit_rle_rgbe HDR files are supported.");

            string resolution = ReadAsciiLine(stream);
            (uint width, uint height) = ParseResolution(resolution);
            float[] pixels = new float[checked((int)(width * height * 3u))];
            DecodeRgbE(stream, width, height, pixels);
            return new HdrEquirectangularImage(width, height, pixels);
        }

        public static byte[] ConvertEquirectangularToCubemap(HdrEquirectangularImage source, uint cubeSize)
        {
            return GenerateCubemap(source, cubeSize, mipLevels: 1, (image, direction, _, _) => SampleEquirectangular(image!, direction));
        }

        public static byte[] GenerateIrradianceCubemap(
            HdrEquirectangularImage source,
            uint cubeSize,
            int sampleCount = DiffuseIrradianceSampleCount)
        {
            if (sampleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));

            return GenerateCubemap(source, cubeSize, mipLevels: 1, (image, normal, _, _) =>
            {
                BuildTangentBasis(normal, out Vec3 tangent, out Vec3 bitangent);
                Vec3 sum = default;
                for (int i = 0; i < sampleCount; i++)
                {
                    (float u, float v) = Hammersley(i, sampleCount);
                    Vec3 local = CosineSampleHemisphere(u, v);
                    Vec3 direction = (tangent * local.X + bitangent * local.Y + normal * local.Z).Normalized();
                    sum += SampleEquirectangular(image!, direction);
                }

                return sum * (Pi / sampleCount);
            });
        }

        public static byte[] GeneratePrefilteredEnvironmentCubemap(
            HdrEquirectangularImage source,
            uint baseSize,
            uint mipLevels,
            int sampleCount = SpecularPrefilterSampleCount)
        {
            if (sampleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));

            return GenerateCubemap(source, baseSize, mipLevels, (image, reflection, mip, totalMips) =>
            {
                float roughness = totalMips <= 1 ? 0.0f : mip / (float)(totalMips - 1u);
                if (roughness <= 0.0001f)
                    return SampleEquirectangular(image!, reflection);

                Vec3 normal = reflection;
                Vec3 view = reflection;
                BuildTangentBasis(normal, out Vec3 tangent, out Vec3 bitangent);

                Vec3 weightedColor = default;
                float totalWeight = 0.0f;
                for (int i = 0; i < sampleCount; i++)
                {
                    (float u, float v) = Hammersley(i, sampleCount);
                    Vec3 localHalf = ImportanceSampleGgx(u, v, roughness);
                    Vec3 halfVector = (tangent * localHalf.X + bitangent * localHalf.Y + normal * localHalf.Z).Normalized();
                    Vec3 light = (halfVector * (2.0f * Vec3.Dot(view, halfVector)) - view).Normalized();
                    float nDotL = MathF.Max(Vec3.Dot(normal, light), 0.0f);
                    if (nDotL <= 0.0f)
                        continue;

                    weightedColor += SampleEquirectangular(image!, light) * nDotL;
                    totalWeight += nDotL;
                }

                return totalWeight > 0.0f ? weightedColor * (1.0f / totalWeight) : SampleEquirectangular(image!, reflection);
            });
        }

        internal static Vec3 SampleEquirectangular(HdrEquirectangularImage source, Vec3 direction)
        {
            direction = direction.Normalized();
            float u = 0.5f + MathF.Atan2(direction.Z, direction.X) / TwoPi;
            float v = MathF.Acos(Math.Clamp(direction.Y, -1.0f, 1.0f)) / Pi;

            u -= MathF.Floor(u);
            v = Math.Clamp(v, 0.0f, 1.0f);

            float x = u * source.Width - 0.5f;
            float y = v * source.Height - 0.5f;
            int x0 = FloorToInt(x);
            int y0 = FloorToInt(y);
            float tx = x - x0;
            float ty = y - y0;

            Vec3 c00 = GetPixelWrapped(source, x0, y0);
            Vec3 c10 = GetPixelWrapped(source, x0 + 1, y0);
            Vec3 c01 = GetPixelWrapped(source, x0, y0 + 1);
            Vec3 c11 = GetPixelWrapped(source, x0 + 1, y0 + 1);
            return Vec3.Lerp(Vec3.Lerp(c00, c10, tx), Vec3.Lerp(c01, c11, tx), ty);
        }

        internal static byte[] GenerateProceduralSkyCubemap(uint baseSize, uint mipLevels, float blur)
        {
            return GenerateCubemap(null, baseSize, mipLevels, (_, direction, mip, totalMips) =>
            {
                float mipBlur = Math.Clamp(blur + (totalMips <= 1 ? 0f : mip / (float)(totalMips - 1)), 0f, 1f);
                return ProceduralSky(direction, mipBlur);
            });
        }

        private static byte[] GenerateCubemap(
            HdrEquirectangularImage? source,
            uint baseSize,
            uint mipLevels,
            Func<HdrEquirectangularImage?, Vec3, uint, uint, Vec3> sample)
        {
            if (baseSize == 0)
                throw new ArgumentOutOfRangeException(nameof(baseSize));
            if (mipLevels == 0)
                throw new ArgumentOutOfRangeException(nameof(mipLevels));

            int[] mipOffsets = CalculateMipOffsets(baseSize, mipLevels, out int totalFloats);
            float[] values = new float[totalFloats];

            Parallel.For(0, checked((int)(mipLevels * 6u)), job =>
            {
                uint mip = (uint)(job / 6);
                uint face = (uint)(job % 6);
                uint size = Math.Max(1u, baseSize >> (int)mip);
                int faceOffset = mipOffsets[mip] + checked((int)(face * size * size * 4u));

                for (uint y = 0; y < size; y++)
                {
                    for (uint x = 0; x < size; x++)
                    {
                        Vec3 direction = CubeDirection(face, (x + 0.5f) / size, (y + 0.5f) / size);
                        Vec3 color = sample(source, direction, mip, mipLevels);
                        int offset = faceOffset + checked((int)((y * size + x) * 4u));
                        values[offset + 0] = MathF.Max(0.0f, color.X);
                        values[offset + 1] = MathF.Max(0.0f, color.Y);
                        values[offset + 2] = MathF.Max(0.0f, color.Z);
                        values[offset + 3] = 1.0f;
                    }
                }
            });

            return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        }

        private static int[] CalculateMipOffsets(uint baseSize, uint mipLevels, out int totalFloats)
        {
            int[] offsets = new int[mipLevels];
            ulong total = 0;
            uint size = baseSize;
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                offsets[mip] = checked((int)total);
                total = checked(total + (ulong)size * size * 6UL * 4UL);
                size = Math.Max(1u, size / 2u);
            }

            if (total > int.MaxValue)
                throw new InvalidOperationException("Generated environment texture is too large for a single upload.");

            totalFloats = (int)total;
            return offsets;
        }

        private static Vec3 CubeDirection(uint face, float u, float v)
        {
            float a = 2.0f * u - 1.0f;
            float b = 1.0f - 2.0f * v;
            Vec3 direction = face switch
            {
                0 => new Vec3(1.0f, b, -a),
                1 => new Vec3(-1.0f, b, a),
                2 => new Vec3(a, 1.0f, -b),
                3 => new Vec3(a, -1.0f, b),
                4 => new Vec3(a, b, 1.0f),
                _ => new Vec3(-a, b, -1.0f)
            };

            return direction.Normalized();
        }

        private static Vec3 ProceduralSky(Vec3 direction, float blur)
        {
            float t = Math.Clamp(direction.Y * 0.5f + 0.5f, 0.0f, 1.0f);
            var ground = new Vec3(0.03f, 0.035f, 0.04f);
            var horizon = new Vec3(0.85f, 0.92f, 1.0f);
            var zenith = new Vec3(0.12f, 0.35f, 0.85f);
            Vec3 sky = Vec3.Lerp(horizon, zenith, t * t);
            Vec3 color = direction.Y < 0.0f
                ? Vec3.Lerp(ground, horizon, Math.Clamp((direction.Y + 1.0f) * 0.35f, 0.0f, 1.0f))
                : sky;

            float sunDot = Math.Clamp(direction.X * -0.3f + direction.Y * 0.72f + direction.Z * 0.62f, 0.0f, 1.0f);
            float sun = MathF.Pow(sunDot, 512.0f) * (1.0f - blur) * 8.0f;
            color += new Vec3(sun, sun * 0.86f, sun * 0.62f);

            var average = new Vec3(0.34f, 0.45f, 0.58f);
            return Vec3.Lerp(color, average, blur * 0.8f);
        }

        private static Vec3 GetPixelWrapped(HdrEquirectangularImage source, int x, int y)
        {
            int width = checked((int)source.Width);
            int height = checked((int)source.Height);
            x %= width;
            if (x < 0)
                x += width;
            y = Math.Clamp(y, 0, height - 1);

            int offset = checked((y * width + x) * 3);
            return new Vec3(source.RgbPixels[offset], source.RgbPixels[offset + 1], source.RgbPixels[offset + 2]);
        }

        private static void BuildTangentBasis(Vec3 normal, out Vec3 tangent, out Vec3 bitangent)
        {
            Vec3 up = MathF.Abs(normal.Y) < 0.999f ? new Vec3(0.0f, 1.0f, 0.0f) : new Vec3(1.0f, 0.0f, 0.0f);
            tangent = Vec3.Cross(up, normal).Normalized();
            bitangent = Vec3.Cross(normal, tangent);
        }

        private static Vec3 CosineSampleHemisphere(float u, float v)
        {
            float radius = MathF.Sqrt(u);
            float phi = TwoPi * v;
            float x = radius * MathF.Cos(phi);
            float y = radius * MathF.Sin(phi);
            float z = MathF.Sqrt(MathF.Max(0.0f, 1.0f - u));
            return new Vec3(x, y, z);
        }

        private static Vec3 ImportanceSampleGgx(float u, float v, float roughness)
        {
            float a = roughness * roughness;
            float phi = TwoPi * u;
            float cosTheta = MathF.Sqrt((1.0f - v) / (1.0f + (a * a - 1.0f) * v));
            float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
            return new Vec3(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);
        }

        private static (float U, float V) Hammersley(int index, int count)
        {
            return ((index + 0.5f) / count, RadicalInverseVdc((uint)index));
        }

        private static float RadicalInverseVdc(uint bits)
        {
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            return bits * 2.3283064365386963e-10f;
        }

        private static void DecodeRgbE(Stream stream, uint width, uint height, float[] pixels)
        {
            byte[] scanline = new byte[checked((int)(width * 4u))];
            for (uint y = 0; y < height; y++)
            {
                int r0 = stream.ReadByte();
                int g0 = stream.ReadByte();
                int b0 = stream.ReadByte();
                int e0 = stream.ReadByte();
                if (e0 < 0)
                    throw new EndOfStreamException("Unexpected end of HDR pixel data.");

                bool useRle = width >= 8 &&
                              width <= 0x7FFF &&
                              r0 == 2 &&
                              g0 == 2 &&
                              ((b0 << 8) | e0) == width;

                if (useRle)
                {
                    DecodeRleScanline(stream, width, scanline);
                }
                else
                {
                    scanline[0] = (byte)r0;
                    scanline[1] = (byte)g0;
                    scanline[2] = (byte)b0;
                    scanline[3] = (byte)e0;
                    ReadExactly(stream, scanline.AsSpan(4));
                }

                for (uint x = 0; x < width; x++)
                {
                    int sourceOffset = checked((int)(x * 4u));
                    int destinationOffset = checked((int)((y * width + x) * 3u));
                    RgbEToFloat(
                        scanline[sourceOffset],
                        scanline[sourceOffset + 1],
                        scanline[sourceOffset + 2],
                        scanline[sourceOffset + 3],
                        out pixels[destinationOffset],
                        out pixels[destinationOffset + 1],
                        out pixels[destinationOffset + 2]);
                }
            }
        }

        private static void DecodeRleScanline(Stream stream, uint width, byte[] scanline)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                uint x = 0;
                while (x < width)
                {
                    int code = stream.ReadByte();
                    if (code < 0)
                        throw new EndOfStreamException("Unexpected end of HDR RLE data.");

                    if (code > 128)
                    {
                        int count = code - 128;
                        int value = stream.ReadByte();
                        if (value < 0)
                            throw new EndOfStreamException("Unexpected end of HDR RLE data.");
                        if (x + count > width)
                            throw new InvalidDataException("HDR RLE run exceeds scanline width.");

                        for (int i = 0; i < count; i++, x++)
                            scanline[checked((int)(x * 4u + (uint)channel))] = (byte)value;
                    }
                    else if (code > 0)
                    {
                        int count = code;
                        if (x + count > width)
                            throw new InvalidDataException("HDR RLE literal exceeds scanline width.");

                        for (int i = 0; i < count; i++, x++)
                        {
                            int value = stream.ReadByte();
                            if (value < 0)
                                throw new EndOfStreamException("Unexpected end of HDR RLE data.");
                            scanline[checked((int)(x * 4u + (uint)channel))] = (byte)value;
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("HDR RLE packet length cannot be zero.");
                    }
                }
            }
        }

        private static void RgbEToFloat(byte r, byte g, byte b, byte e, out float red, out float green, out float blue)
        {
            if (e == 0)
            {
                red = 0.0f;
                green = 0.0f;
                blue = 0.0f;
                return;
            }

            float scale = MathF.Pow(2.0f, e - 136);
            red = r * scale;
            green = g * scale;
            blue = b * scale;
        }

        private static (uint Width, uint Height) ParseResolution(string resolution)
        {
            string[] parts = resolution.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
                throw new InvalidDataException($"Unsupported HDR resolution line '{resolution}'.");

            uint? width = null;
            uint? height = null;
            for (int i = 0; i < parts.Length - 1; i += 2)
            {
                if (!uint.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value) || value == 0)
                    throw new InvalidDataException($"Invalid HDR resolution value in '{resolution}'.");

                if (parts[i].EndsWith("X", StringComparison.Ordinal))
                    width = value;
                else if (parts[i].EndsWith("Y", StringComparison.Ordinal))
                    height = value;
            }

            if (width == null || height == null)
                throw new InvalidDataException($"Unsupported HDR resolution line '{resolution}'.");

            return (width.Value, height.Value);
        }

        private static string ReadAsciiLine(Stream stream)
        {
            var builder = new StringBuilder();
            while (true)
            {
                int value = stream.ReadByte();
                if (value < 0)
                {
                    if (builder.Length == 0)
                        throw new EndOfStreamException("Unexpected end of HDR header.");
                    break;
                }

                if (value == '\n')
                    break;
                if (value != '\r')
                    builder.Append((char)value);
            }

            return builder.ToString();
        }

        private static void ReadExactly(Stream stream, Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                int read = stream.Read(destination);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of HDR data.");
                destination = destination[read..];
            }
        }

        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }

        internal readonly struct Vec3
        {
            public Vec3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public float X { get; }
            public float Y { get; }
            public float Z { get; }

            public Vec3 Normalized()
            {
                float lengthSquared = X * X + Y * Y + Z * Z;
                if (lengthSquared <= 0.0f)
                    return new Vec3(0.0f, 1.0f, 0.0f);

                float invLength = 1.0f / MathF.Sqrt(lengthSquared);
                return new Vec3(X * invLength, Y * invLength, Z * invLength);
            }

            public static Vec3 Lerp(Vec3 a, Vec3 b, float t)
            {
                return a + (b - a) * t;
            }

            public static float Dot(Vec3 a, Vec3 b)
            {
                return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            }

            public static Vec3 Cross(Vec3 a, Vec3 b)
            {
                return new Vec3(
                    a.Y * b.Z - a.Z * b.Y,
                    a.Z * b.X - a.X * b.Z,
                    a.X * b.Y - a.Y * b.X);
            }

            public static Vec3 operator +(Vec3 left, Vec3 right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
            public static Vec3 operator -(Vec3 left, Vec3 right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
            public static Vec3 operator *(Vec3 value, float scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
        }
    }
}
