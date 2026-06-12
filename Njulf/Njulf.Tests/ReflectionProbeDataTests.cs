using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class ReflectionProbeDataTests
    {
        [Test]
        public void CalculateMipCount_ReturnsFullMipChain()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ReflectionProbeData.CalculateMipCount(1), Is.EqualTo(1));
                Assert.That(ReflectionProbeData.CalculateMipCount(64), Is.EqualTo(7));
                Assert.That(ReflectionProbeData.CalculateMipCount(256), Is.EqualTo(9));
            });
        }

        [Test]
        public void EstimateCubemapArrayBytes_UsesRgba16FAllFacesAndMips()
        {
            ulong expectedOneProbe64 = 6UL * 8UL * (64UL * 64UL + 32UL * 32UL + 16UL * 16UL + 8UL * 8UL + 4UL * 4UL + 2UL * 2UL + 1UL);

            Assert.That(ReflectionProbeData.EstimateCubemapArrayBytes(1, 64, 7), Is.EqualTo(expectedOneProbe64));
        }

        [Test]
        public void BuildHeader_PacksSettingsAndClampDebugResources()
        {
            var settings = new ReflectionSettings
            {
                MaxProbesPerPixel = 4,
                DebugProbeIndex = 9,
                DebugCubemapFace = 4,
                DebugMipLevel = 9,
                DebugView = ReflectionDebugView.ProbeIndex
            };

            GPUReflectionProbeHeader header = ReflectionProbeData.BuildHeader(
                activeProbeCount: 2,
                settings,
                BindlessIndex.ReflectionProbeCubemapArrayTexture,
                BindlessIndex.ReflectionProbeDebugTexture,
                mipCount: 5);

            Assert.Multiple(() =>
            {
                Assert.That(header.ProbeCount, Is.EqualTo(2));
                Assert.That(header.MaxProbesPerPixel, Is.EqualTo(4));
                Assert.That(header.ProbeCubemapArrayTextureIndex, Is.EqualTo(BindlessIndex.ReflectionProbeCubemapArrayTexture));
                Assert.That(header.DebugTextureIndex, Is.EqualTo(BindlessIndex.ReflectionProbeDebugTexture));
                Assert.That(header.Flags & ReflectionProbeData.ReflectionEnabledFlag, Is.Not.EqualTo(0));
                Assert.That(header.Flags & ReflectionProbeData.ReflectionBoxProjectionEnabledFlag, Is.Not.EqualTo(0));
                Assert.That(header.Flags & ReflectionProbeData.ReflectionProbeBlendingEnabledFlag, Is.Not.EqualTo(0));
                Assert.That(header.DebugView, Is.EqualTo((uint)ReflectionDebugView.ProbeIndex));
                Assert.That(header.DebugProbeIndex, Is.EqualTo(1));
                Assert.That(header.DebugCubemapFace, Is.EqualTo(4));
                Assert.That(header.DebugMipLevel, Is.EqualTo(4));
            });
        }

        [Test]
        public void BuildProbes_SortsByPriorityThenNameDeterministically()
        {
            var settings = new ReflectionSettings { MaxProbes = 3 };
            var probes = new[]
            {
                new ReflectionProbe { Name = "B", Priority = 1, Position = new Vector3(2, 0, 0) },
                new ReflectionProbe { Name = "A", Priority = 1, Position = new Vector3(1, 0, 0) },
                new ReflectionProbe { Name = "C", Priority = 2, Position = new Vector3(3, 0, 0) }
            };
            var gpu = new GPUReflectionProbe[3];

            int count = ReflectionProbeData.BuildProbes(probes, settings, gpu);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(3));
                Assert.That(gpu[0].PositionAndRadius.X, Is.EqualTo(3));
                Assert.That(gpu[1].PositionAndRadius.X, Is.EqualTo(1));
                Assert.That(gpu[2].PositionAndRadius.X, Is.EqualTo(2));
            });
        }

        [Test]
        public void CalculateInfluenceWeight_SphereAndBoxFadeAtBlendRegion()
        {
            var sphere = new ReflectionProbe
            {
                Shape = ReflectionProbeShape.Sphere,
                Radius = 10.0f,
                BlendDistance = 2.0f
            };
            var box = new ReflectionProbe
            {
                Shape = ReflectionProbeShape.Box,
                BoxExtents = new Vector3(5.0f, 5.0f, 5.0f),
                BlendDistance = 1.0f
            };

            Assert.Multiple(() =>
            {
                Assert.That(ReflectionProbeData.CalculateInfluenceWeight(sphere, Vector3.Zero), Is.EqualTo(1.0f));
                Assert.That(ReflectionProbeData.CalculateInfluenceWeight(sphere, new Vector3(10.1f, 0, 0)), Is.EqualTo(0.0f));
                Assert.That(ReflectionProbeData.CalculateInfluenceWeight(sphere, new Vector3(9.0f, 0, 0)), Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(ReflectionProbeData.CalculateInfluenceWeight(box, Vector3.Zero), Is.EqualTo(1.0f));
                Assert.That(ReflectionProbeData.CalculateInfluenceWeight(box, new Vector3(6.0f, 0, 0)), Is.EqualTo(0.0f));
                Assert.That(ReflectionProbeData.CalculateInfluenceWeight(box, new Vector3(4.5f, 0, 0)), Is.EqualTo(0.5f).Within(0.0001f));
            });
        }

        [Test]
        public void BoxProjectDirection_IntersectsProbeBox()
        {
            var probe = new ReflectionProbe
            {
                Shape = ReflectionProbeShape.Box,
                BoxExtents = new Vector3(4.0f, 2.0f, 2.0f)
            };

            Vector3 direction = ReflectionProbeData.BoxProjectDirection(
                probe,
                new Vector3(2.0f, 0.0f, 0.0f),
                new Vector3(1.0f, 0.0f, 0.0f));

            Assert.That(direction.X, Is.EqualTo(1.0f).Within(0.0001f));
            Assert.That(direction.Y, Is.EqualTo(0.0f).Within(0.0001f));
            Assert.That(direction.Z, Is.EqualTo(0.0f).Within(0.0001f));
        }
    }
}
