using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class GlobalIlluminationProbeVolumeDataTests
    {
        [Test]
        public void ProbeVolume_ClampsAuthoringValuesAndCalculatesSpacing()
        {
            var volume = new GlobalIlluminationProbeVolume
            {
                Size = new Vector3(-1.0f, float.PositiveInfinity, 9.0f),
                ProbeCountX = 1,
                ProbeCountY = 999,
                ProbeCountZ = 4,
                RaysPerProbe = 1,
                MaxProbeUpdatesPerFrame = -10,
                NormalBias = -1.0f,
                ViewBias = 99.0f,
                MaxRayDistance = -5.0f,
                Intensity = 99.0f,
                Hysteresis = 1.0f
            };

            Assert.Multiple(() =>
            {
                Assert.That(volume.Size.X, Is.EqualTo(0.1f));
                Assert.That(volume.Size.Y, Is.EqualTo(0.1f));
                Assert.That(volume.Size.Z, Is.EqualTo(9.0f));
                Assert.That(volume.ProbeCountX, Is.EqualTo(GlobalIlluminationProbeVolume.MinProbeCountPerAxis));
                Assert.That(volume.ProbeCountY, Is.EqualTo(GlobalIlluminationProbeVolume.MaxProbeCountPerAxis));
                Assert.That(volume.ProbeCountZ, Is.EqualTo(4));
                Assert.That(volume.RaysPerProbe, Is.EqualTo(GlobalIlluminationProbeVolume.MinRaysPerProbe));
                Assert.That(volume.MaxProbeUpdatesPerFrame, Is.EqualTo(0));
                Assert.That(volume.NormalBias, Is.EqualTo(0.0f));
                Assert.That(volume.ViewBias, Is.EqualTo(10.0f));
                Assert.That(volume.MaxRayDistance, Is.EqualTo(0.1f));
                Assert.That(volume.Intensity, Is.EqualTo(16.0f));
                Assert.That(volume.Hysteresis, Is.EqualTo(0.999f));
                Assert.That(volume.ProbeSpacing.Z, Is.EqualTo(3.0f));
            });
        }

        [Test]
        public void BuildVolumes_PacksEnabledVolumesAndHeader()
        {
            var settings = new GlobalIlluminationSettings { Enabled = true, Mode = GlobalIlluminationMode.Ddgi };
            var volumes = new[]
            {
                new GlobalIlluminationProbeVolume
                {
                    Origin = new Vector3(1.0f, 2.0f, 3.0f),
                    Size = new Vector3(6.0f, 4.0f, 2.0f),
                    ProbeCountX = 4,
                    ProbeCountY = 3,
                    ProbeCountZ = 2,
                    RaysPerProbe = 128,
                    MaxProbeUpdatesPerFrame = 8
                },
                new GlobalIlluminationProbeVolume { Enabled = false }
            };
            var gpu = new GPUDdgiProbeVolume[4];

            int count = GlobalIlluminationProbeVolumeData.BuildVolumes(
                volumes,
                settings,
                gpu,
                out int totalProbeCount,
                out int activeProbeCount,
                out int raysPerProbe,
                out int maxUpdates);
            GPUDdgiProbeVolumeHeader header = GlobalIlluminationProbeVolumeData.BuildHeader(
                count,
                totalProbeCount,
                activeProbeCount,
                raysPerProbe,
                maxUpdates,
                settings,
                BindlessIndex.DdgiProbeStateBuffer);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(1));
                Assert.That(totalProbeCount, Is.EqualTo(24));
                Assert.That(activeProbeCount, Is.EqualTo(24));
                Assert.That(raysPerProbe, Is.EqualTo(128));
                Assert.That(maxUpdates, Is.EqualTo(8));
                Assert.That(gpu[0].OriginAndFirstProbeIndex.X, Is.EqualTo(1.0f));
                Assert.That(gpu[0].OriginAndFirstProbeIndex.W, Is.EqualTo(0.0f));
                Assert.That(gpu[0].SizeAndProbeCountX.W, Is.EqualTo(4.0f));
                Assert.That(gpu[0].ProbeSpacingAndProbeCountY.X, Is.EqualTo(2.0f));
                Assert.That(gpu[0].BiasAndProbeCountZ.Z, Is.EqualTo(16.0f));
                Assert.That(header.Flags & GlobalIlluminationProbeVolumeData.EnabledFlag, Is.Not.EqualTo(0));
                Assert.That(header.Flags & GlobalIlluminationProbeVolumeData.ProbeRelocationEnabledFlag, Is.EqualTo(0));
                Assert.That(header.Flags & GlobalIlluminationProbeVolumeData.ProbeClassificationEnabledFlag, Is.Not.EqualTo(0));
                Assert.That(header.ProbeStateBufferIndex, Is.EqualTo(BindlessIndex.DdgiProbeStateBuffer));
            });
        }

        [Test]
        public void BuildVolumes_UsesShaderRayBudgetAndAtLeastVolumeDiagonal()
        {
            var settings = new GlobalIlluminationSettings { Enabled = true, Mode = GlobalIlluminationMode.Ddgi };
            var volumes = new[]
            {
                new GlobalIlluminationProbeVolume
                {
                    Size = new Vector3(5.6f, 3.6f, 5.6f),
                    ProbeCountX = 8,
                    ProbeCountY = 5,
                    ProbeCountZ = 8,
                    RaysPerProbe = 1024,
                    MaxRayDistance = 5.6f,
                    MaxProbeUpdatesPerFrame = 320
                }
            };
            var gpu = new GPUDdgiProbeVolume[1];

            int count = GlobalIlluminationProbeVolumeData.BuildVolumes(
                volumes,
                settings,
                gpu,
                out _,
                out _,
                out int raysPerProbe,
                out _);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(1));
                Assert.That(raysPerProbe, Is.EqualTo(GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe));
                Assert.That(gpu[0].RayAndUpdateParams.X, Is.EqualTo(GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe));
                Assert.That(gpu[0].BiasAndProbeCountZ.Z, Is.EqualTo(volumes[0].Size.Length()).Within(0.0001f));
            });
        }

        [Test]
        public void EstimateTextureBytes_UsesSeparateIrradianceAndVisibilityAtlases()
        {
            ulong expected = 10UL * 8UL * 8UL * 8UL + 10UL * 16UL * 16UL * 4UL;

            Assert.That(GlobalIlluminationProbeVolumeData.EstimateTextureBytes(10), Is.EqualTo(expected));
        }

        [Test]
        public void ProbeUpdateScheduling_CentersDirtyProbeWithinUpdateWindow()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DdgiProbeVolumeManager.CalculateProbeUpdateStartForDirtyProbe(100, 10, 50), Is.EqualTo(45));
                Assert.That(DdgiProbeVolumeManager.CalculateProbeUpdateStartForDirtyProbe(100, 10, 2), Is.EqualTo(0));
                Assert.That(DdgiProbeVolumeManager.CalculateProbeUpdateStartForDirtyProbe(100, 10, 98), Is.EqualTo(90));
                Assert.That(DdgiProbeVolumeManager.CalculateProbeUpdateStartForDirtyProbe(12, 32, 7), Is.EqualTo(0));
                Assert.That(DdgiProbeVolumeManager.CalculateProbeUpdateStartForDirtyProbe(0, 8, 3), Is.EqualTo(0));
                Assert.That(DdgiProbeVolumeManager.CalculateProbeUpdateStartForDirtyProbe(8, 0, 3), Is.EqualTo(0));
            });
        }

        [Test]
        public void VisibilityAtlasInitialization_UsesPerProbeMaxDistanceMoments()
        {
            var volumes = new[]
            {
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                    SizeAndProbeCountX = new Vector4(1.0f, 1.0f, 1.0f, 2.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                    BiasAndProbeCountZ = new Vector4(0.0f, 0.0f, 3.0f, 1.0f)
                },
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, 2.0f),
                    SizeAndProbeCountX = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                    BiasAndProbeCountZ = new Vector4(0.0f, 0.0f, 5.0f, 1.0f)
                }
            };
            const int activeProbeCount = 3;
            const uint texelsPerProbe = 2;
            byte[] payload = new byte[activeProbeCount * (int)(texelsPerProbe * texelsPerProbe) * sizeof(uint)];

            DdgiProbeVolumeManager.CreateVisibilityAtlasInitializationPayload(
                volumes,
                activeProbeCount,
                texelsPerProbe,
                payload);

            uint[] words = MemoryMarshal.Cast<byte, uint>(payload).ToArray();
            uint firstVolumeMoments = PackHalf2(3.0f, 9.0f);
            uint secondVolumeMoments = PackHalf2(5.0f, 25.0f);

            Assert.Multiple(() =>
            {
                Assert.That(words[0], Is.EqualTo(firstVolumeMoments));
                Assert.That(words[3], Is.EqualTo(firstVolumeMoments));
                Assert.That(words[4], Is.EqualTo(firstVolumeMoments));
                Assert.That(words[7], Is.EqualTo(firstVolumeMoments));
                Assert.That(words[8], Is.EqualTo(secondVolumeMoments));
                Assert.That(words[11], Is.EqualTo(secondVolumeMoments));
            });
        }

        [Test]
        public void ResourceSignature_ChangesWhenProbeVolumeLayoutChanges()
        {
            var volumes = new[]
            {
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                    SizeAndProbeCountX = new Vector4(1.0f, 1.0f, 1.0f, 2.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                    BiasAndProbeCountZ = new Vector4(0.0f, 0.0f, 3.0f, 1.0f)
                }
            };
            ulong original = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 2, 2, 96, 2);

            volumes[0].SizeAndProbeCountX = new Vector4(1.0f, 1.0f, 1.0f, 3.0f);
            ulong changed = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 3, 3, 96, 3);

            Assert.That(changed, Is.Not.EqualTo(original));
        }

        private static uint PackHalf2(float x, float y)
        {
            uint hx = BitConverter.HalfToUInt16Bits((Half)x);
            uint hy = BitConverter.HalfToUInt16Bits((Half)y);
            return hx | (hy << 16);
        }
    }
}
