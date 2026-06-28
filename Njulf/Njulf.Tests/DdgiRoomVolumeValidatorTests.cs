using System.Linq;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiRoomVolumeValidatorTests
    {
        [Test]
        public void SmallRoomPreset_UsesProductionInteriorRanges()
        {
            var volume = GlobalIlluminationProbeVolume.CreateSmallRoomPreset(
                new BoundingBox(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(6.0f, 3.0f, 6.0f)),
                targetSpacing: 0.6f);
            var settings = CreateSettings();

            DdgiRoomVolumeValidationReport report = DdgiRoomVolumeValidator.Validate(
                new[] { volume },
                settings);

            Assert.Multiple(() =>
            {
                Assert.That(report.IsProductionReady, Is.True);
                Assert.That(volume.Interior, Is.True);
                Assert.That(volume.QualityClass, Is.EqualTo(GlobalIlluminationProbeVolumeQualityClass.High));
                Assert.That(volume.ProbeSpacing.X, Is.InRange(0.4f, 0.75f));
                Assert.That(volume.RaysPerProbe, Is.EqualTo(32));
                Assert.That(volume.DirtyRaysPerProbe, Is.EqualTo(48));
                Assert.That(volume.MaxProbeUpdatesPerFrame, Is.InRange(48, 64));
                Assert.That(volume.MaxRayDistance, Is.InRange(8.0f, 15.0f));
                Assert.That(volume.Hysteresis, Is.LessThan(0.9f));
                Assert.That(volume.NormalBias, Is.GreaterThanOrEqualTo(volume.ProbeSpacing.X * 0.08f).Within(0.0001f));
                Assert.That(volume.ViewBias, Is.GreaterThanOrEqualTo(volume.ProbeSpacing.X * 0.2f).Within(0.0001f));
            });
        }

        [Test]
        public void ThinWallRoomPreset_UsesTighterBiasAndDirtyRayBudget()
        {
            var volume = GlobalIlluminationProbeVolume.CreateThinWallRoomPreset(
                new BoundingBox(new Vector3(-2.0f, 0.0f, -2.0f), new Vector3(2.0f, 3.0f, 2.0f)),
                targetSpacing: 0.45f);
            float minSpacing = MathF.Min(volume.ProbeSpacing.X, MathF.Min(volume.ProbeSpacing.Y, volume.ProbeSpacing.Z));

            Assert.Multiple(() =>
            {
                Assert.That(volume.QualityClass, Is.EqualTo(GlobalIlluminationProbeVolumeQualityClass.Ultra));
                Assert.That(volume.ProbeSpacing.X, Is.InRange(0.4f, 0.6f));
                Assert.That(volume.RaysPerProbe, Is.EqualTo(32));
                Assert.That(volume.DirtyRaysPerProbe, Is.EqualTo(64));
                Assert.That(volume.MaxProbeUpdatesPerFrame, Is.EqualTo(64));
                Assert.That(volume.MaxRayDistance, Is.InRange(8.0f, 12.0f));
                Assert.That(volume.NormalBias, Is.GreaterThanOrEqualTo(minSpacing * 0.1f).Within(0.0001f));
                Assert.That(volume.ViewBias, Is.GreaterThanOrEqualTo(minSpacing * 0.25f).Within(0.0001f));
                Assert.That(volume.DirtyHysteresis, Is.LessThan(volume.SteadyHysteresis));
            });
        }

        [Test]
        public void Validate_ReportsGeometryBlendOverlapThinWallAndBudgetFailures()
        {
            GlobalIlluminationProbeVolume a = GlobalIlluminationProbeVolume.CreateSmallRoomPreset(
                new BoundingBox(Vector3.Zero, new Vector3(4.0f, 3.0f, 4.0f)),
                targetSpacing: 0.5f);
            a.BlendDistance = 0.0f;
            GlobalIlluminationProbeVolume b = GlobalIlluminationProbeVolume.CreateSmallRoomPreset(
                new BoundingBox(new Vector3(0.1f, 0.0f, 0.1f), new Vector3(4.1f, 3.0f, 4.1f)),
                targetSpacing: 0.5f);
            var settings = CreateSettings();
            settings.DdgiMaxActiveProbes = 64;
            settings.DdgiAtlasMemoryBudgetBytes = GlobalIlluminationProbeVolumeData.AtlasBytesPerProbe * 64UL;
            settings.DdgiThinWallPolicyEnabled = false;
            var solid = new[]
            {
                new BoundingBox(new Vector3(-0.1f), new Vector3(4.2f, 3.1f, 4.2f))
            };
            var thinWalls = new[]
            {
                new BoundingBox(new Vector3(2.0f, 0.0f, -0.2f), new Vector3(2.08f, 3.0f, 4.2f))
            };

            DdgiRoomVolumeValidationReport report = DdgiRoomVolumeValidator.Validate(
                new[] { a, b },
                settings,
                solid,
                thinWalls);
            DdgiRoomVolumeValidationCode[] codes = report.Issues.Select(issue => issue.Code).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(report.IsProductionReady, Is.False);
                Assert.That(codes, Does.Contain(DdgiRoomVolumeValidationCode.InvalidBlendMargin));
                Assert.That(codes, Does.Contain(DdgiRoomVolumeValidationCode.ProbeInsideGeometry));
                Assert.That(codes, Does.Contain(DdgiRoomVolumeValidationCode.InsufficientFreeSpaceCoverage));
                Assert.That(codes, Does.Contain(DdgiRoomVolumeValidationCode.ProbeTooCloseToThinWall));
                Assert.That(codes, Does.Contain(DdgiRoomVolumeValidationCode.ExcessiveOverlap));
                Assert.That(codes, Does.Contain(DdgiRoomVolumeValidationCode.ProbeQuotaOverflow));
                Assert.That(codes, Does.Contain(DdgiRoomVolumeValidationCode.ThinWallPolicyDisabled));
                Assert.That(report.BlockedProbeCount, Is.GreaterThan(0));
                Assert.That(report.ThinWallNearProbeCount, Is.GreaterThan(0));
            });
        }

        [Test]
        public void Validate_RequiresConservativeThinWallProxyThickness()
        {
            var volume = GlobalIlluminationProbeVolume.CreateThinWallRoomPreset(
                new BoundingBox(Vector3.Zero, new Vector3(4.0f, 3.0f, 4.0f)));
            var settings = CreateSettings();
            settings.DdgiThinWallProxyThickness = 0.01f;
            var thinWalls = new[]
            {
                new BoundingBox(new Vector3(2.0f, 0.0f, 0.0f), new Vector3(2.12f, 3.0f, 4.0f))
            };

            DdgiRoomVolumeValidationReport report = DdgiRoomVolumeValidator.Validate(
                new[] { volume },
                settings,
                thinWallBounds: thinWalls);

            Assert.That(report.HasIssue(DdgiRoomVolumeValidationCode.ThinWallProxyTooThin), Is.True);
        }

        private static GlobalIlluminationSettings CreateSettings()
        {
            return new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                DdgiMaxActiveProbes = 4096,
                DdgiAtlasMemoryBudgetBytes = GlobalIlluminationProbeVolumeData.AtlasBytesPerProbe * 4096UL,
                DdgiThinWallPolicyEnabled = true,
                DdgiThinWallProxyThickness = 0.12f,
                DdgiThinWallLeakClampStrength = 0.9f
            };
        }
    }
}
