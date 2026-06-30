using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class CameraRelativeDdgiClipmapControllerTests
    {
        [Test]
        public void Update_InitializesCenteredCascadesFromSettings()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateLowSettings();

            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(Vector3.Zero, 1, settings);

            Assert.Multiple(() =>
            {
                Assert.That(result.LayoutChanged, Is.True);
                Assert.That(result.FirstActivation, Is.True);
                Assert.That(result.MovementClass, Is.EqualTo(DdgiCameraMovementClass.LayoutChanged));
                Assert.That(result.DirtyProbeCount, Is.EqualTo(16 * 8 * 16 * 3));
                Assert.That(controller.WarmUpActive, Is.True);
                Assert.That(controller.TotalProbeCount, Is.EqualTo(16 * 8 * 16 * 3));
                Assert.That(controller.Cascades, Has.Count.EqualTo(3));
            });

            DdgiClipmapCascadeState near = controller.Cascades[0];
            DdgiClipmapCascadeState mid = controller.Cascades[1];

            Assert.Multiple(() =>
            {
                Assert.That(near.CascadeIndex, Is.EqualTo(0));
                Assert.That(near.ProbeSpacing, Is.EqualTo(1.5f));
                Assert.That(near.PhysicalFirstProbeIndex, Is.EqualTo(0));
                Assert.That(near.LogicalGridMinCell, Is.EqualTo(new DdgiClipmapCell(-8, -4, -8)));
                Assert.That(near.SnappedOrigin, Is.EqualTo(new Vector3(-12.0f, -6.0f, -12.0f)));
                Assert.That(near.DirtyRegions, Has.Count.EqualTo(1));
                Assert.That(near.DirtyRegions[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.LayoutChange));
                Assert.That(mid.ProbeSpacing, Is.EqualTo(3.75f));
                Assert.That(mid.PhysicalFirstProbeIndex, Is.EqualTo(16 * 8 * 16));
            });
        }

        [Test]
        public void Update_ZeroVerticalCenterOffsetCentersYLatticeAroundCamera()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();
            settings.DdgiClipmapProbeCountY = 4;
            settings.DdgiClipmapVerticalCenterOffset = 0.0f;

            controller.Update(new Vector3(0.0f, 5.0f, 0.0f), 1, settings);

            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            Assert.Multiple(() =>
            {
                Assert.That(cascade.LogicalGridMinCell, Is.EqualTo(new DdgiClipmapCell(-2, 3, -2)));
                Assert.That(cascade.SnappedOrigin, Is.EqualTo(new Vector3(-2.0f, 3.0f, -2.0f)));
                Assert.That(cascade.SnappedOrigin.Y + settings.DdgiClipmapProbeCountY * cascade.ProbeSpacing * 0.5f, Is.EqualTo(5.0f));
            });
        }

        [Test]
        public void Update_AppliesVerticalCenterOffsetToCascadeGrid()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();
            settings.DdgiClipmapVerticalCenterOffset = 3.0f;

            controller.Update(Vector3.Zero, 1, settings);

            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            Assert.Multiple(() =>
            {
                Assert.That(cascade.LogicalGridMinCell, Is.EqualTo(new DdgiClipmapCell(-2, 2, -2)));
                Assert.That(cascade.SnappedOrigin, Is.EqualTo(new Vector3(-2.0f, 2.0f, -2.0f)));
            });
        }

        [Test]
        public void Update_DdgiHighPresetReachesUpperSponzaGeometry()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            var settings = new RenderSettings();

            controller.Update(Vector3.Zero, 1, settings.GlobalIllumination);

            DdgiClipmapCascadeState near = controller.Cascades[0];
            DdgiClipmapCascadeState middle = controller.Cascades[1];
            float nearTop = near.SnappedOrigin.Y + near.ProbeSpacing * (near.ProbeCountY - 1);
            float middleTop = middle.SnappedOrigin.Y + middle.ProbeSpacing * (middle.ProbeCountY - 1);

            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.DdgiClipmapProbeCountY, Is.EqualTo(14));
                Assert.That(settings.GlobalIllumination.DdgiClipmapVerticalCenterOffset, Is.EqualTo(6.25f));
                Assert.That(controller.Cascades, Has.Count.EqualTo(3));
                Assert.That(controller.TotalProbeCount, Is.EqualTo(24 * 14 * 24 * 3));
                Assert.That(controller.TotalProbeCount, Is.LessThanOrEqualTo(settings.GlobalIllumination.DdgiMaxActiveProbes));
                Assert.That(near.SnappedOrigin.Y, Is.LessThanOrEqualTo(-2.0f));
                Assert.That(nearTop, Is.GreaterThanOrEqualTo(13.5f));
                Assert.That(middleTop, Is.GreaterThanOrEqualTo(20.0f));
            });
        }

        [Test]
        public void Update_SubCellMovementKeepsCurrentSlotsClean()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();

            controller.Update(Vector3.Zero, 1, settings);
            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(new Vector3(0.49f, 0.0f, 0.0f), 2, settings);

            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            Assert.Multiple(() =>
            {
                Assert.That(result.DirtyProbeCount, Is.EqualTo(0));
                Assert.That(result.TeleportReset, Is.False);
                Assert.That(result.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Normal));
                Assert.That(controller.WarmUpActive, Is.False);
                Assert.That(cascade.ScrollDelta, Is.EqualTo(DdgiClipmapCell.Zero));
                Assert.That(cascade.RingOffset, Is.EqualTo(DdgiClipmapCell.Zero));
                Assert.That(cascade.DirtyRegions, Is.Empty);
            });
        }

        [Test]
        public void Update_ScrollsRingOffsetAndInvalidatesOnlyNewSlabs()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();

            controller.Update(Vector3.Zero, 1, settings);
            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            var stableCell = new DdgiClipmapCell(0, -1, -2);
            int physicalBeforeScroll = cascade.GetPhysicalProbeIndex(stableCell);
            cascade.MarkLogicalCellUpdated(stableCell, 1);

            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(new Vector3(1.1f, 0.0f, 0.0f), 2, settings);

            Assert.Multiple(() =>
            {
                Assert.That(result.DirtyProbeCount, Is.EqualTo(8));
                Assert.That(cascade.ScrollDelta, Is.EqualTo(new DdgiClipmapCell(1, 0, 0)));
                Assert.That(cascade.RingOffset, Is.EqualTo(new DdgiClipmapCell(1, 0, 0)));
                Assert.That(cascade.LogicalGridMinCell, Is.EqualTo(new DdgiClipmapCell(-1, -1, -2)));
                Assert.That(cascade.DirtyRegions, Has.Count.EqualTo(1));
                Assert.That(cascade.DirtyRegions[0], Is.EqualTo(new DdgiClipmapDirtyRegion(
                    new DdgiClipmapCell(2, -1, -2),
                    new DdgiClipmapCell(2, 0, 1),
                    DdgiClipmapDirtyReason.Scroll)));
                Assert.That(cascade.GetPhysicalProbeIndex(stableCell), Is.EqualTo(physicalBeforeScroll));
                Assert.That(cascade.GetCellState(stableCell).Initialized, Is.True);
                Assert.That(cascade.GetCellState(new DdgiClipmapCell(2, -1, -2)).Initialized, Is.False);
            });
        }

        [Test]
        public void Update_NegativeScrollUsesPositiveModuloRingAddressing()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();

            controller.Update(Vector3.Zero, 1, settings);
            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(new Vector3(-1.1f, 0.0f, -1.1f), 2, settings);

            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            Assert.Multiple(() =>
            {
                Assert.That(result.DirtyProbeCount, Is.EqualTo(24));
                Assert.That(cascade.ScrollDelta, Is.EqualTo(new DdgiClipmapCell(-2, 0, -2)));
                Assert.That(cascade.RingOffset, Is.EqualTo(new DdgiClipmapCell(2, 0, 2)));
                Assert.That(cascade.LogicalGridMinCell, Is.EqualTo(new DdgiClipmapCell(-4, -1, -4)));
                Assert.That(cascade.DirtyRegions, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void Update_TeleportInvalidatesAllCascadesAndResetsRingOffset()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();
            settings.DdgiTeleportResetDistance = 10.0f;

            controller.Update(Vector3.Zero, 1, settings);
            controller.Update(new Vector3(1.1f, 0.0f, 0.0f), 2, settings);
            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(new Vector3(100.0f, 0.0f, 0.0f), 3, settings);

            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            Assert.Multiple(() =>
            {
                Assert.That(result.TeleportReset, Is.True);
                Assert.That(result.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Teleport));
                Assert.That(result.DirtyProbeCount, Is.EqualTo(32));
                Assert.That(controller.WarmUpActive, Is.True);
                Assert.That(cascade.RingOffset, Is.EqualTo(DdgiClipmapCell.Zero));
                Assert.That(cascade.DirtyRegions, Has.Count.EqualTo(1));
                Assert.That(cascade.DirtyRegions[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.Teleport));
            });
        }

        [Test]
        public void Update_FastMovementScrollsWhenOverlapRemainsTrustworthy()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();
            settings.DdgiClipmapSafetyMarginCells = 1;
            settings.DdgiTeleportResetDistance = 1000.0f;

            controller.Update(Vector3.Zero, 1, settings);
            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            var stableCell = new DdgiClipmapCell(0, -1, -1);
            int physicalBeforeScroll = cascade.GetPhysicalProbeIndex(stableCell);
            cascade.MarkLogicalCellUpdated(stableCell, 1);

            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(new Vector3(2.1f, 0.0f, 0.0f), 2, settings);

            Assert.Multiple(() =>
            {
                Assert.That(result.FastMovement, Is.True);
                Assert.That(result.MovementClass, Is.EqualTo(DdgiCameraMovementClass.Fast));
                Assert.That(result.TeleportReset, Is.False);
                Assert.That(result.DirtyProbeCount, Is.EqualTo(16));
                Assert.That(cascade.ScrollDelta, Is.EqualTo(new DdgiClipmapCell(2, 0, 0)));
                Assert.That(cascade.RingOffset, Is.EqualTo(new DdgiClipmapCell(2, 0, 0)));
                Assert.That(cascade.DirtyRegions[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.Scroll));
                Assert.That(cascade.GetPhysicalProbeIndex(stableCell), Is.EqualTo(physicalBeforeScroll));
                Assert.That(cascade.GetCellState(stableCell).Initialized, Is.True);
            });
        }

        [Test]
        public void Update_FastMovementResetsOnlyCascadesWithUntrustworthyOverlap()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateLowSettings();
            settings.DdgiClipmapSafetyMarginCells = 1;
            settings.DdgiTeleportResetDistance = 1000.0f;

            controller.Update(Vector3.Zero, 1, settings);
            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(new Vector3(24.0f, 0.0f, 0.0f), 2, settings);

            DdgiClipmapCascadeState near = controller.Cascades[0];
            DdgiClipmapCascadeState mid = controller.Cascades[1];
            Assert.Multiple(() =>
            {
                Assert.That(result.FastMovement, Is.True);
                Assert.That(result.TeleportReset, Is.False);
                Assert.That(near.RingOffset, Is.EqualTo(DdgiClipmapCell.Zero));
                Assert.That(near.DirtyRegions[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.Teleport));
                Assert.That(mid.ScrollDelta.X, Is.EqualTo(6));
                Assert.That(mid.RingOffset.X, Is.EqualTo(6));
                Assert.That(mid.DirtyRegions[0].Reason, Is.EqualTo(DdgiClipmapDirtyReason.Scroll));
            });
        }

        [Test]
        public void Update_CameraCutResetsViewPriorityHistoryWithoutInvalidatingProbeData()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();

            controller.Update(Vector3.Zero, 1, settings);
            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            var cell = new DdgiClipmapCell(-2, -1, -2);
            cascade.MarkLogicalCellUpdated(cell, 1);

            CameraRelativeDdgiClipmapUpdateResult result = controller.Update(Vector3.Zero, 2, settings, cameraCut: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.CameraCutReset, Is.True);
                Assert.That(result.ViewPriorityHistoryReset, Is.True);
                Assert.That(result.MovementClass, Is.EqualTo(DdgiCameraMovementClass.ViewResetOnly));
                Assert.That(result.DirtyProbeCount, Is.EqualTo(0));
                Assert.That(cascade.DirtyRegions, Is.Empty);
                Assert.That(cascade.GetCellState(cell).Initialized, Is.True);
            });
        }

        [Test]
        public void MarkLogicalCellUpdated_TracksLastUpdateFrameAndAge()
        {
            var controller = new CameraRelativeDdgiClipmapController();
            GlobalIlluminationSettings settings = CreateSingleCascadeSettings();

            controller.Update(Vector3.Zero, 10, settings);
            DdgiClipmapCascadeState cascade = controller.Cascades[0];
            var cell = new DdgiClipmapCell(-2, -1, -2);
            cascade.MarkLogicalCellUpdated(cell, 10);

            controller.Update(Vector3.Zero, 14, settings);
            DdgiClipmapCellState state = cascade.GetCellState(cell);

            Assert.Multiple(() =>
            {
                Assert.That(state.Initialized, Is.True);
                Assert.That(state.LastUpdateFrame, Is.EqualTo(10UL));
                Assert.That(state.AgeFrames, Is.EqualTo(4UL));
            });
        }

        [Test]
        public void CalculatePhysicalProbeIndex_IsStableForOverlappingScrolledCells()
        {
            var logicalCell = new DdgiClipmapCell(-7, -3, 5);
            int before = CameraRelativeDdgiClipmapController.CalculatePhysicalProbeIndex(
                logicalCell,
                new DdgiClipmapCell(-8, -4, 4),
                new DdgiClipmapCell(0, 0, 0),
                16,
                8,
                16,
                2048);
            int after = CameraRelativeDdgiClipmapController.CalculatePhysicalProbeIndex(
                logicalCell,
                new DdgiClipmapCell(-10, -4, 3),
                new DdgiClipmapCell(14, 0, 15),
                16,
                8,
                16,
                2048);

            Assert.That(after, Is.EqualTo(before));
        }

        [Test]
        public void CalculateCenteredGridMinimum_ClampsExtremeWorldCoordinates()
        {
            DdgiClipmapCell positive = CameraRelativeDdgiClipmapController.CalculateCenteredGridMinimum(
                new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                0.25f,
                32,
                12,
                32);
            DdgiClipmapCell negative = CameraRelativeDdgiClipmapController.CalculateCenteredGridMinimum(
                new Vector3(float.MinValue, float.MinValue, float.MinValue),
                0.25f,
                32,
                12,
                32);

            Assert.Multiple(() =>
            {
                Assert.That(positive.X, Is.EqualTo(int.MaxValue - 31));
                Assert.That(positive.Y, Is.EqualTo(int.MaxValue - 11));
                Assert.That(positive.Z, Is.EqualTo(int.MaxValue - 31));
                Assert.That(negative.X, Is.EqualTo(int.MinValue));
                Assert.That(negative.Y, Is.EqualTo(int.MinValue));
                Assert.That(negative.Z, Is.EqualTo(int.MinValue));
            });
        }

        private static GlobalIlluminationSettings CreateLowSettings()
        {
            return new GlobalIlluminationSettings
            {
                DdgiClipmapCascadeCount = 3,
                DdgiClipmapProbeCountX = 16,
                DdgiClipmapProbeCountY = 8,
                DdgiClipmapProbeCountZ = 16,
                DdgiClipmapBaseSpacing = 1.5f,
                DdgiClipmapSpacingScale = 2.5f
            };
        }

        private static GlobalIlluminationSettings CreateSingleCascadeSettings()
        {
            return new GlobalIlluminationSettings
            {
                DdgiClipmapCascadeCount = 1,
                DdgiClipmapProbeCountX = 4,
                DdgiClipmapProbeCountY = 2,
                DdgiClipmapProbeCountZ = 4,
                DdgiClipmapBaseSpacing = 1.0f,
                DdgiClipmapSpacingScale = 2.0f,
                DdgiTeleportResetDistance = 1000.0f
            };
        }

    }
}
