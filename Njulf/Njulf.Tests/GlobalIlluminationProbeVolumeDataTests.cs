using System;
using System.IO;
using System.Linq;
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
        public void BuildVolumes_PacksRuntimeClipmapMetadata()
        {
            var settings = new GlobalIlluminationSettings { Enabled = true, Mode = GlobalIlluminationMode.Ddgi };
            var volumes = new[]
            {
                new GlobalIlluminationProbeVolume
                {
                    Origin = new Vector3(4.0f, 2.0f, -8.0f),
                    Size = new Vector3(6.0f, 2.0f, 6.0f),
                    ProbeCountX = 4,
                    ProbeCountY = 2,
                    ProbeCountZ = 4,
                    MaxRayDistance = 8.0f
                }
            };
            var metadata = new[]
            {
                new DdgiProbeVolumeRuntimeMetadata(
                    DdgiProbeVolumeKind.CameraClipmap,
                    2,
                    4,
                    -1,
                    -8,
                    3,
                    0,
                    1,
                    0.25f,
                    GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag)
            };
            var gpu = new GPUDdgiProbeVolume[1];

            int count = GlobalIlluminationProbeVolumeData.BuildVolumes(
                volumes,
                settings,
                gpu,
                out int totalProbeCount,
                out _,
                out _,
                out _,
                metadata);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(1));
                Assert.That(totalProbeCount, Is.EqualTo(32));
                Assert.That(gpu[0].ClipmapGridMinAndKind.X, Is.EqualTo(4.0f));
                Assert.That(gpu[0].ClipmapGridMinAndKind.Y, Is.EqualTo(-1.0f));
                Assert.That(gpu[0].ClipmapGridMinAndKind.Z, Is.EqualTo(-8.0f));
                Assert.That(gpu[0].ClipmapGridMinAndKind.W, Is.EqualTo((float)(uint)DdgiProbeVolumeKind.CameraClipmap));
                Assert.That(gpu[0].ClipmapRingOffsetAndCascade.X, Is.EqualTo(3.0f));
                Assert.That(gpu[0].ClipmapRingOffsetAndCascade.Z, Is.EqualTo(1.0f));
                Assert.That(gpu[0].ClipmapRingOffsetAndCascade.W, Is.EqualTo(2.0f));
                Assert.That(gpu[0].ClipmapBlendAndFlags.X, Is.EqualTo(0.25f));
                Assert.That(gpu[0].ClipmapBlendAndFlags.Y, Is.EqualTo(0.5f));
                Assert.That(gpu[0].ClipmapBlendAndFlags.Z, Is.EqualTo(0.0f));
                Assert.That((uint)gpu[0].ClipmapBlendAndFlags.W, Is.EqualTo(GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag));
            });
        }

        [Test]
        public void BuildVolumes_UsesShaderRayBudgetAndExplicitMaxRayDistance()
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
                Assert.That(gpu[0].BiasAndProbeCountZ.Z, Is.EqualTo(5.6f).Within(0.0001f));
            });
        }

        [Test]
        public void BuildVolumes_EnforcesActiveProbeUpdateRayAndAtlasBudgets()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                DdgiMaxActiveProbes = 48,
                DdgiMaxProbeUpdatesPerFrame = 5,
                DdgiMaxRaysPerProbe = 80,
                DdgiCascade0RaysPerProbe = 96,
                DdgiCascade1RaysPerProbe = 64,
                DdgiAtlasMemoryBudgetBytes = GlobalIlluminationProbeVolumeData.AtlasBytesPerProbe * 48UL
            };
            var volumes = new[]
            {
                new GlobalIlluminationProbeVolume
                {
                    ProbeCountX = 4,
                    ProbeCountY = 2,
                    ProbeCountZ = 4,
                    RaysPerProbe = 160,
                    MaxProbeUpdatesPerFrame = 128
                },
                new GlobalIlluminationProbeVolume
                {
                    ProbeCountX = 4,
                    ProbeCountY = 2,
                    ProbeCountZ = 4,
                    RaysPerProbe = 160,
                    MaxProbeUpdatesPerFrame = 128
                }
            };
            var metadata = new[]
            {
                new DdgiProbeVolumeRuntimeMetadata(
                    DdgiProbeVolumeKind.CameraClipmap,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0.15f,
                    GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag),
                new DdgiProbeVolumeRuntimeMetadata(
                    DdgiProbeVolumeKind.CameraClipmap,
                    1,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0.15f,
                    GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag)
            };
            var gpu = new GPUDdgiProbeVolume[2];

            int count = GlobalIlluminationProbeVolumeData.BuildVolumes(
                volumes,
                settings,
                gpu,
                out int totalProbeCount,
                out int activeProbeCount,
                out int raysPerProbe,
                out int maxUpdates,
                metadata);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(1));
                Assert.That(totalProbeCount, Is.EqualTo(32));
                Assert.That(activeProbeCount, Is.EqualTo(32));
                Assert.That(raysPerProbe, Is.EqualTo(80));
                Assert.That(maxUpdates, Is.EqualTo(5));
                Assert.That(gpu[0].RayAndUpdateParams.X, Is.EqualTo(80.0f));
                Assert.That(gpu[0].RayAndUpdateParams.Y, Is.EqualTo(5.0f));
                Assert.That(GlobalIlluminationProbeVolumeData.CalculateActiveProbeBudget(settings), Is.EqualTo(48));
            });
        }

        [Test]
        public void GlobalIlluminationSettings_ClampsDdgiBudgetControlsAndReservesAsyncHeadroom()
        {
            var settings = new GlobalIlluminationSettings
            {
                DdgiMaxActiveProbes = int.MaxValue,
                DdgiMaxProbeUpdatesPerFrame = -1,
                DdgiMaxRaysPerProbe = 1024,
                DdgiCascade0RaysPerProbe = 8,
                DdgiCascade0MaxRayDistance = -1.0f,
                DdgiMaxShadedLights = int.MaxValue,
                DdgiMaterialTextureMaxCascade = int.MaxValue,
                DdgiAtlasMemoryBudgetBytes = 0,
                DdgiProbeUpdateTimeBudgetMilliseconds = 4.0f,
                DdgiAsyncComputeReservedBudgetFraction = 0.5f
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.DdgiMaxActiveProbes, Is.EqualTo(GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget));
                Assert.That(settings.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(0));
                Assert.That(settings.DdgiMaxRaysPerProbe, Is.EqualTo(GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe));
                Assert.That(settings.DdgiCascade0RaysPerProbe, Is.EqualTo(GlobalIlluminationProbeVolume.MinRaysPerProbe));
                Assert.That(settings.DdgiCascade0MaxRayDistance, Is.EqualTo(0.1f));
                Assert.That(settings.DdgiMaxShadedLights, Is.EqualTo(64));
                Assert.That(settings.DdgiMaterialTextureMaxCascade, Is.EqualTo(GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount - 1));
                Assert.That(settings.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(1UL * 1024UL * 1024UL));
                Assert.That(settings.EffectiveDdgiProbeUpdateTimeBudgetMilliseconds, Is.EqualTo(2.0f));
            });
        }

        [Test]
        public void EstimateTextureBytes_UsesSeparateIrradianceAndVisibilityAtlases()
        {
            ulong expected = 10UL * 8UL * 8UL * 8UL + 10UL * 16UL * 16UL * 4UL;

            Assert.That(GlobalIlluminationProbeVolumeData.EstimateTextureBytes(10), Is.EqualTo(expected));
        }

        [Test]
        public void CalculateRecursiveCommitBytes_ScalesWithUpdatedProbeCount()
        {
            ulong perProbeBytes = GlobalIlluminationProbeVolumeData.ProbeStateStride +
                GlobalIlluminationProbeVolumeData.IrradianceBytesPerProbe +
                GlobalIlluminationProbeVolumeData.VisibilityBytesPerProbe;

            Assert.Multiple(() =>
            {
                Assert.That(DdgiProbeVolumeManager.CalculateRecursiveCommitBytes(0), Is.EqualTo(0UL));
                Assert.That(DdgiProbeVolumeManager.CalculateRecursiveCommitBytes(3), Is.EqualTo(perProbeBytes * 3UL));
                Assert.That(perProbeBytes, Is.LessThan(GlobalIlluminationProbeVolumeData.EstimateTextureBytes(64)));
            });
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

        [Test]
        public void ResourceSignature_StaysStableWhenCameraRelativeVolumeOriginMoves()
        {
            var volumes = new[]
            {
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(-12.0f, -2.0f, -12.0f, 0.0f),
                    SizeAndProbeCountX = new Vector4(24.0f, 10.0f, 24.0f, 24.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 10.0f),
                    BiasAndProbeCountZ = new Vector4(0.2f, 0.5f, 36.0f, 24.0f),
                    RayAndUpdateParams = new Vector4(96.0f, 256.0f, 1.0f, 0.97f),
                    DebugColorAndFlags = new Vector4(0.15f, 0.9f, 0.65f, GlobalIlluminationProbeVolumeData.EnabledFlag)
                }
            };
            ulong original = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 5760, 5760, 96, 256);

            volumes[0].OriginAndFirstProbeIndex = new Vector4(125.0f, -2.0f, -48.0f, 0.0f);
            volumes[0].SizeAndProbeCountX = new Vector4(24.0f, 10.0f, 24.0f, 24.0f);
            ulong moved = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 5760, 5760, 96, 256);

            Assert.That(moved, Is.EqualTo(original));
        }

        [Test]
        public void ResourceSignature_StaysStableWhenOnlyUpdateBudgetChanges()
        {
            var volumes = new[]
            {
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                    SizeAndProbeCountX = new Vector4(8.0f, 4.0f, 8.0f, 8.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 4.0f),
                    BiasAndProbeCountZ = new Vector4(0.2f, 0.5f, 12.0f, 8.0f),
                    RayAndUpdateParams = new Vector4(96.0f, 64.0f, 1.0f, 0.97f),
                    DebugColorAndFlags = new Vector4(0.15f, 0.9f, 0.65f, GlobalIlluminationProbeVolumeData.EnabledFlag)
                }
            };
            ulong original = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 96, 64);

            volumes[0].RayAndUpdateParams = new Vector4(96.0f, 512.0f, 1.0f, 0.97f);
            ulong changedBudget = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 96, 512);

            Assert.That(changedBudget, Is.EqualTo(original));
        }

        [Test]
        public void ResourceSignature_ChangesForPersistentResourcePolicyInputs()
        {
            var volumes = new[]
            {
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                    SizeAndProbeCountX = new Vector4(8.0f, 4.0f, 8.0f, 8.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 4.0f),
                    BiasAndProbeCountZ = new Vector4(0.2f, 0.5f, 12.0f, 8.0f),
                    RayAndUpdateParams = new Vector4(96.0f, 64.0f, 1.0f, 0.97f),
                    DebugColorAndFlags = new Vector4(0.15f, 0.9f, 0.65f, GlobalIlluminationProbeVolumeData.EnabledFlag)
                }
            };
            ulong original = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 96, 64);

            volumes[0].ProbeSpacingAndProbeCountY = new Vector4(1.25f, 1.25f, 1.25f, 4.0f);
            ulong spacingChanged = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 96, 64);
            volumes[0].ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 4.0f);

            volumes[0].RayAndUpdateParams = new Vector4(128.0f, 64.0f, 1.0f, 0.97f);
            ulong raysChanged = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 128, 64);
            volumes[0].RayAndUpdateParams = new Vector4(96.0f, 64.0f, 1.0f, 0.97f);

            ulong relocationChanged = DdgiProbeVolumeManager.CreateResourceSignature(
                volumes,
                256,
                256,
                96,
                64,
                GlobalIlluminationProbeVolumeData.ProbeRelocationEnabledFlag);

            Assert.Multiple(() =>
            {
                Assert.That(spacingChanged, Is.Not.EqualTo(original));
                Assert.That(raysChanged, Is.Not.EqualTo(original));
                Assert.That(relocationChanged, Is.Not.EqualTo(original));
            });
        }

        [Test]
        public void ResourceSignature_IgnoresDynamicClipmapGridAndRingMetadata()
        {
            var volumes = new[]
            {
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                    SizeAndProbeCountX = new Vector4(8.0f, 4.0f, 8.0f, 8.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 4.0f),
                    BiasAndProbeCountZ = new Vector4(0.2f, 0.5f, 12.0f, 8.0f),
                    RayAndUpdateParams = new Vector4(96.0f, 64.0f, 1.0f, 0.97f),
                    DebugColorAndFlags = new Vector4(0.15f, 0.9f, 0.65f, GlobalIlluminationProbeVolumeData.EnabledFlag),
                    ClipmapGridMinAndKind = new Vector4(-4.0f, -2.0f, -4.0f, (float)(uint)DdgiProbeVolumeKind.CameraClipmap),
                    ClipmapRingOffsetAndCascade = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
                    ClipmapBlendAndFlags = new Vector4(0.15f, 1.2f, 0.0f, GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag)
                }
            };
            ulong original = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 96, 64);

            volumes[0].ClipmapGridMinAndKind = new Vector4(256.0f, -2.0f, 128.0f, (float)(uint)DdgiProbeVolumeKind.CameraClipmap);
            volumes[0].ClipmapRingOffsetAndCascade = new Vector4(3.0f, 1.0f, 2.0f, 0.0f);
            ulong scrolled = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 96, 64);

            volumes[0].ClipmapGridMinAndKind = new Vector4(256.0f, -2.0f, 128.0f, (float)(uint)DdgiProbeVolumeKind.Authored);
            ulong kindChanged = DdgiProbeVolumeManager.CreateResourceSignature(volumes, 256, 256, 96, 64);

            Assert.Multiple(() =>
            {
                Assert.That(scrolled, Is.EqualTo(original));
                Assert.That(kindChanged, Is.Not.EqualTo(original));
            });
        }

        [Test]
        public void CacheCompatibilitySignature_IgnoresAuthoredStreamingButChangesClipmapLayout()
        {
            var volumes = new[]
            {
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(-4.0f, -1.0f, -4.0f, 0.0f),
                    SizeAndProbeCountX = new Vector4(8.0f, 2.0f, 8.0f, 4.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(2.0f, 2.0f, 2.0f, 2.0f),
                    BiasAndProbeCountZ = new Vector4(0.2f, 0.5f, 8.0f, 4.0f),
                    RayAndUpdateParams = new Vector4(64.0f, 16.0f, 1.0f, 0.985f),
                    ClipmapGridMinAndKind = new Vector4(-2.0f, 0.0f, -2.0f, (float)(uint)DdgiProbeVolumeKind.CameraClipmap),
                    ClipmapRingOffsetAndCascade = new Vector4(0.0f, 0.0f, 0.0f, 0.0f)
                },
                new GPUDdgiProbeVolume
                {
                    OriginAndFirstProbeIndex = new Vector4(20.0f, 0.0f, 20.0f, 32.0f),
                    SizeAndProbeCountX = new Vector4(4.0f, 3.0f, 4.0f, 2.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(4.0f, 3.0f, 4.0f, 2.0f),
                    BiasAndProbeCountZ = new Vector4(0.2f, 0.5f, 6.0f, 2.0f),
                    RayAndUpdateParams = new Vector4(96.0f, 8.0f, 1.0f, 0.97f),
                    ClipmapGridMinAndKind = new Vector4(0.0f, 0.0f, 0.0f, (float)(uint)DdgiProbeVolumeKind.Authored)
                }
            };

            ulong original = DdgiProbeVolumeManager.CreateCacheCompatibilitySignature(volumes);
            volumes[1].OriginAndFirstProbeIndex = new Vector4(-50.0f, 0.0f, -50.0f, 32.0f);
            volumes[1].SizeAndProbeCountX = new Vector4(10.0f, 4.0f, 10.0f, 3.0f);
            ulong authoredChanged = DdgiProbeVolumeManager.CreateCacheCompatibilitySignature(volumes);
            volumes[0].ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 2.0f);
            ulong clipmapChanged = DdgiProbeVolumeManager.CreateCacheCompatibilitySignature(volumes);

            Assert.Multiple(() =>
            {
                Assert.That(authoredChanged, Is.EqualTo(original));
                Assert.That(clipmapChanged, Is.Not.EqualTo(original));
            });
        }

        [Test]
        public void CreateVisibilityAtlasRangeInitializationPayload_InitializesOnlyRequestedRange()
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
                    SizeAndProbeCountX = new Vector4(1.0f, 1.0f, 1.0f, 2.0f),
                    ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                    BiasAndProbeCountZ = new Vector4(0.0f, 0.0f, 7.0f, 1.0f)
                }
            };
            byte[] bytes = new byte[2 * 4 * sizeof(uint)];

            DdgiProbeVolumeManager.CreateVisibilityAtlasRangeInitializationPayload(
                volumes,
                activeProbeCount: 4,
                startProbeIndex: 1,
                probeCount: 2,
                visibilityTexelsPerProbe: 2,
                bytes);

            uint firstVolumeMoments = PackHalf2(3.0f, 9.0f);
            uint secondVolumeMoments = PackHalf2(7.0f, 49.0f);
            uint[] words = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(words[0], Is.EqualTo(firstVolumeMoments));
                Assert.That(words[3], Is.EqualTo(firstVolumeMoments));
                Assert.That(words[4], Is.EqualTo(secondVolumeMoments));
                Assert.That(words[7], Is.EqualTo(secondVolumeMoments));
            });
        }

        [Test]
        public void DdgiProbeVolumeManager_ReallocatedBuffersAreRetiredAfterFramesInFlight()
        {
            string source = ReadRepoText("Njulf.Rendering", "Resources", "DdgiProbeVolumeManager.cs");

            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("BeginFrameResourceRetirement();"));
                Assert.That(source, Does.Contain("RetireBufferResource(handle);"));
                Assert.That(source, Does.Contain("_frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL"));
                Assert.That(source, Does.Contain("DrainRetiredResources(force: false);"));
            });
        }

        private static uint PackHalf2(float x, float y)
        {
            uint hx = BitConverter.HalfToUInt16Bits((Half)x);
            uint hy = BitConverter.HalfToUInt16Bits((Half)y);
            return hx | (hy << 16);
        }

        private static string ReadRepoText(params string[] pathParts)
        {
            string? directory = TestContext.CurrentContext.TestDirectory;
            while (directory != null)
            {
                string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = Directory.GetParent(directory)?.FullName;
            }

            Assert.Fail($"Could not find repo file '{Path.Combine(pathParts)}'.");
            return string.Empty;
        }
    }
}
