using System;
using System.IO;
using System.Numerics;
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
        public void LoadRadianceHdr_RejectsOversizedEncodedInputBeforeDecode()
        {
            string path = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"oversized-rgbe-{Guid.NewGuid():N}.hdr");
            try
            {
                using (var output = new FileStream(
                           path,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    output.SetLength(EnvironmentMapProcessor.MaximumEncodedHdrBytes + 1);
                }

                InvalidDataException exception = Assert.Throws<InvalidDataException>(
                    () => EnvironmentMapProcessor.LoadRadianceHdr(path))!;

                Assert.That(exception.Message, Does.Contain("input limit"));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void LoadRadianceHdr_RejectsDecodedPixelBudgetAndUnsupportedOrientation()
        {
            string oversizedDimensions = WriteHdrFixture(
                $"oversized-dimensions-{Guid.NewGuid():N}.hdr",
                "-Y 4097 +X 8192");
            string unsupportedOrientation = WriteHdrFixture(
                $"unsupported-orientation-{Guid.NewGuid():N}.hdr",
                "+Y 1 +X 1");
            try
            {
                InvalidDataException dimensionsException =
                    Assert.Throws<InvalidDataException>(
                        () => EnvironmentMapProcessor.LoadRadianceHdr(
                            oversizedDimensions))!;
                InvalidDataException orientationException =
                    Assert.Throws<InvalidDataException>(
                        () => EnvironmentMapProcessor.LoadRadianceHdr(
                            unsupportedOrientation))!;

                Assert.Multiple(() =>
                {
                    Assert.That(dimensionsException.Message, Does.Contain("pixel decode limit"));
                    Assert.That(orientationException.Message, Does.Contain("Unsupported HDR resolution"));
                });
            }
            finally
            {
                File.Delete(oversizedDimensions);
                File.Delete(unsupportedOrientation);
            }
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
        public void GenerateProceduralSkyIrradianceCubemap_StoresIntegratedIrradianceRatherThanBlurredRadiance()
        {
            float[] irradiance = BytesToFloats(
                EnvironmentMapProcessor.GenerateProceduralSkyIrradianceCubemap(2, sampleCount: 128));
            float[] oldBlurredRadianceApproximation = BytesToFloats(
                EnvironmentMapProcessor.GenerateProceduralSkyCubemap(2, 1, blur: 0.85f));

            float irradianceLuminance = AverageLuminance(irradiance);
            float blurredRadianceLuminance = AverageLuminance(oldBlurredRadianceApproximation);

            Assert.Multiple(() =>
            {
                Assert.That(irradianceLuminance, Is.GreaterThan(0.0f));
                Assert.That(irradianceLuminance, Is.GreaterThan(blurredRadianceLuminance * 2.0f));
                Assert.That(Array.Exists(irradiance, float.IsNaN), Is.False);
            });
        }

        [Test]
        public void ProceduralSky_TracksPrimarySunDiffuseFractionWithoutDoubleCountingDisc()
        {
            Vector3 firstSunDirection = Vector3.Normalize(new Vector3(0.2f, 0.75f, -0.63f));
            Vector3 secondSunDirection = Vector3.Normalize(new Vector3(-0.2f, 0.75f, 0.63f));
            var parameters = new EnvironmentMapProcessor.ProceduralSkyParameters(
                firstSunDirection,
                new Vector3(14.0f, 12.88f, 11.48f),
                DiffuseFraction: 0.15f,
                GroundAlbedo: 0.20f);
            var rotatedSunParameters = parameters with { ToSunDirection = secondSunDirection };

            float expectedSkyIrradiance = 0.15f / 0.85f *
                (14.0f * 0.2126f + 12.88f * 0.7152f + 11.48f * 0.0722f) * 0.75f;
            float measuredSkyIrradiance = EnvironmentMapProcessor.EstimateProceduralSkyHorizontalIrradianceLuminance(
                parameters,
                sampleCount: 512);
            Vector3 withDisc = EnvironmentMapProcessor.SampleProceduralSkyRadiance(
                parameters.ToSunDirection,
                parameters,
                includeSunDisc: true);
            Vector3 withoutDisc = EnvironmentMapProcessor.SampleProceduralSkyRadiance(
                parameters.ToSunDirection,
                parameters,
                includeSunDisc: false);
            Vector3 discAtOtherDirection = EnvironmentMapProcessor.SampleProceduralSkyRadiance(
                secondSunDirection,
                parameters,
                includeSunDisc: true);
            Vector3 noDiscAtOtherDirection = EnvironmentMapProcessor.SampleProceduralSkyRadiance(
                secondSunDirection,
                parameters,
                includeSunDisc: false);
            byte[] firstIrradiance = EnvironmentMapProcessor.GenerateProceduralSkyIrradianceCubemap(
                2,
                sampleCount: 128,
                parameters: parameters);
            byte[] rotatedIrradiance = EnvironmentMapProcessor.GenerateProceduralSkyIrradianceCubemap(
                2,
                sampleCount: 128,
                parameters: rotatedSunParameters);

            Assert.Multiple(() =>
            {
                Assert.That(measuredSkyIrradiance, Is.EqualTo(expectedSkyIrradiance).Within(expectedSkyIrradiance * 0.03f));
                Assert.That(Luminance(withDisc), Is.GreaterThan(Luminance(withoutDisc)));
                Assert.That(
                    Luminance(withDisc) - Luminance(withoutDisc),
                    Is.GreaterThan(Luminance(discAtOtherDirection) - Luminance(noDiscAtOtherDirection)));
                Assert.That(firstIrradiance, Is.EqualTo(rotatedIrradiance),
                    "Diffuse irradiance must exclude the visible directional sun disc.");
            });
        }

        [Test]
        public void ProceduralSky_GroundHemisphereReceivesGlobalIllumination()
        {
            var parameters = new EnvironmentMapProcessor.ProceduralSkyParameters(
                Vector3.Normalize(new Vector3(0.0f, 0.8f, 0.6f)),
                new Vector3(12.0f),
                DiffuseFraction: 0.15f,
                GroundAlbedo: 0.20f);

            Vector3 ground = EnvironmentMapProcessor.SampleProceduralSkyRadiance(
                new Vector3(0.0f, -1.0f, 0.0f),
                parameters,
                includeSunDisc: false);

            Assert.That(Luminance(ground), Is.GreaterThan(0.1f));
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

        private static string WriteHdrFixture(string name, string resolution)
        {
            string path = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                name);
            File.WriteAllText(
                path,
                $"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n{resolution}\n");
            return path;
        }

        private static float[] BytesToFloats(byte[] bytes)
        {
            float[] values = new float[bytes.Length / sizeof(float)];
            MemoryMarshal.Cast<byte, float>(bytes).CopyTo(values);
            return values;
        }

        private static float AverageLuminance(float[] rgba)
        {
            float sum = 0.0f;
            int count = rgba.Length / 4;
            for (int offset = 0; offset < rgba.Length; offset += 4)
                sum += rgba[offset] * 0.2126f + rgba[offset + 1] * 0.7152f + rgba[offset + 2] * 0.0722f;
            return sum / Math.Max(count, 1);
        }

        private static float Luminance(Vector3 value) =>
            value.X * 0.2126f + value.Y * 0.7152f + value.Z * 0.0722f;

        private static byte[] Combine(byte[] first, byte[] second)
        {
            byte[] combined = new byte[first.Length + second.Length];
            System.Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            System.Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
            return combined;
        }
    }
}
