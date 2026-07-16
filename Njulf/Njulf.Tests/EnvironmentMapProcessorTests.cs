using System;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    public sealed class EnvironmentMapProcessorTests
    {
        [Test]
        public void LoadRadianceHdr_DecodesFlatRgbE()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "flat-rgbe-test.hdr");
            byte[] header = "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 2\n"u8.ToArray();
            byte[] pixels =
            [
                128, 0, 0, 129,
                0, 64, 0, 130
            ];
            File.WriteAllBytes(path, Combine(header, pixels));

            HdrEquirectangularImage image = EnvironmentMapProcessor.LoadRadianceHdr(path);

            Assert.Multiple(() =>
            {
                Assert.That(image.Width, Is.EqualTo(2));
                Assert.That(image.Height, Is.EqualTo(1));
                Assert.That(image.RgbPixels[0], Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(image.RgbPixels[1], Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(image.RgbPixels[2], Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(image.RgbPixels[3], Is.EqualTo(0.0f).Within(0.0001f));
                Assert.That(image.RgbPixels[4], Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(image.RgbPixels[5], Is.EqualTo(0.0f).Within(0.0001f));
            });
        }

        [Test]
        public void ConvertEquirectangularToCubemap_PreservesConstantRadiance()
        {
            var image = CreateConstantImage(4, 2, 2.0f, 4.0f, 8.0f);

            float[] pixels = BytesToFloats(EnvironmentMapProcessor.ConvertEquirectangularToCubemap(image, 2));

            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                Assert.That(pixels[offset + 0], Is.EqualTo(2.0f).Within(0.0001f));
                Assert.That(pixels[offset + 1], Is.EqualTo(4.0f).Within(0.0001f));
                Assert.That(pixels[offset + 2], Is.EqualTo(8.0f).Within(0.0001f));
                Assert.That(pixels[offset + 3], Is.EqualTo(1.0f).Within(0.0001f));
            }
        }

        [Test]
        public void GenerateIrradianceCubemap_IntegratesConstantRadianceOverHemisphere()
        {
            var image = CreateConstantImage(4, 2, 0.5f, 1.0f, 2.0f);

            float[] pixels = BytesToFloats(EnvironmentMapProcessor.GenerateIrradianceCubemap(image, 1, sampleCount: 64));

            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                Assert.That(pixels[offset + 0], Is.EqualTo(MathF.PI * 0.5f).Within(0.0001f));
                Assert.That(pixels[offset + 1], Is.EqualTo(MathF.PI).Within(0.0001f));
                Assert.That(pixels[offset + 2], Is.EqualTo(MathF.PI * 2.0f).Within(0.0001f));
                Assert.That(pixels[offset + 3], Is.EqualTo(1.0f).Within(0.0001f));
            }
        }

        [Test]
        public void GeneratePrefilteredEnvironmentCubemap_PreservesConstantRadianceAcrossMips()
        {
            var image = CreateConstantImage(4, 2, 3.0f, 2.0f, 1.0f);

            float[] pixels = BytesToFloats(EnvironmentMapProcessor.GeneratePrefilteredEnvironmentCubemap(image, 2, 2, sampleCount: 32));

            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                Assert.That(pixels[offset + 0], Is.EqualTo(3.0f).Within(0.0001f));
                Assert.That(pixels[offset + 1], Is.EqualTo(2.0f).Within(0.0001f));
                Assert.That(pixels[offset + 2], Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That(pixels[offset + 3], Is.EqualTo(1.0f).Within(0.0001f));
            }
        }

        [Test]
        public void ConvertRgbaFloat32Payload_ConvertsToHalfPayload()
        {
            float[] source =
            [
                1.0f, 0.5f, 0.25f, 1.0f,
                8.0f, 4.0f, 2.0f, 1.0f
            ];
            byte[] bytes = MemoryMarshal.AsBytes(source.AsSpan()).ToArray();

            byte[] halfBytes = EnvironmentManager.ConvertRgbaFloat32Payload(bytes, Format.R16G16B16A16Sfloat);
            Half[] halves = MemoryMarshal.Cast<byte, Half>(halfBytes).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(halfBytes, Has.Length.EqualTo(bytes.Length / 2));
                Assert.That((float)halves[0], Is.EqualTo(1.0f).Within(0.0001f));
                Assert.That((float)halves[1], Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That((float)halves[4], Is.EqualTo(8.0f).Within(0.0001f));
            });
        }

        private static HdrEquirectangularImage CreateConstantImage(uint width, uint height, float r, float g, float b)
        {
            float[] pixels = new float[checked((int)(width * height * 3u))];
            for (int offset = 0; offset < pixels.Length; offset += 3)
            {
                pixels[offset + 0] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
            }

            return new HdrEquirectangularImage(width, height, pixels);
        }

        private static float[] BytesToFloats(byte[] bytes)
        {
            float[] values = new float[bytes.Length / sizeof(float)];
            MemoryMarshal.Cast<byte, float>(bytes).CopyTo(values);
            return values;
        }

        private static byte[] Combine(byte[] first, byte[] second)
        {
            byte[] combined = new byte[first.Length + second.Length];
            System.Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            System.Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
            return combined;
        }
    }
}
