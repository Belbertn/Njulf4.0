using System;
using System.Linq;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DdgiProbeUpdateSchedulerTests
    {
        [Test]
        public void BuildRequests_PrioritizesDirtyClipmapCellsWithLogicalCoordinates()
        {
            GPUDdgiProbeVolume volume = CreateCameraClipmapVolume(
                firstProbeIndex: 10,
                gridMin: new DdgiClipmapCell(8, 0, -8),
                ringOffset: new DdgiClipmapCell(1, 0, 2),
                countX: 4,
                countY: 2,
                countZ: 4,
                spacing: 1.0f);
            DdgiClipmapCell dirtyCell = new(9, 0, -7);
            var layout = CreateLayout(new[]
            {
                new DdgiFrameLayoutDirtyProbeRequest(
                    0,
                    0,
                    dirtyCell,
                    dirtyCell,
                    10,
                    DdgiClipmapDirtyReason.Scroll)
            });
            var requests = new GPUDdgiProbeUpdateRequest[4];
            var marks = new byte[42];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout,
                dirtyBounds: null,
                activeProbeCount: 42,
                hardMaxRequestCount: 4,
                updateCursor: 0,
                CreateSettings(outOfFrustumFraction: 0.25f),
                requests,
                marks);

            int expectedPhysicalProbe = CameraRelativeDdgiClipmapController.CalculatePhysicalProbeIndex(
                dirtyCell,
                new DdgiClipmapCell(8, 0, -8),
                new DdgiClipmapCell(1, 0, 2),
                4,
                2,
                4,
                10);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(4));
                Assert.That(requests[0].ProbeIndex, Is.EqualTo((uint)expectedPhysicalProbe));
                Assert.That(requests[0].VolumeIndex, Is.EqualTo(0u));
                Assert.That(requests[0].Priority, Is.EqualTo(DdgiProbeUpdateScheduler.PriorityNewCell));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].LogicalCellX, Is.EqualTo(dirtyCell.X));
                Assert.That(requests[0].LogicalCellY, Is.EqualTo(dirtyCell.Y));
                Assert.That(requests[0].LogicalCellZ, Is.EqualTo(dirtyCell.Z));
                Assert.That(result.CompatibilityStartProbeIndex, Is.EqualTo(expectedPhysicalProbe));
            });
        }

        [Test]
        public void BuildRequests_UsesDirtyBoundsPriorityForClipmapDirtyBoundsRequests()
        {
            GPUDdgiProbeVolume volume = CreateCameraClipmapVolume(
                firstProbeIndex: 10,
                gridMin: new DdgiClipmapCell(8, 0, -8),
                ringOffset: new DdgiClipmapCell(1, 0, 2),
                countX: 4,
                countY: 2,
                countZ: 4,
                spacing: 1.0f);
            DdgiClipmapCell dirtyCell = new(9, 0, -7);
            var layout = CreateLayout(new[]
            {
                new DdgiFrameLayoutDirtyProbeRequest(
                    0,
                    0,
                    dirtyCell,
                    dirtyCell,
                    10,
                    DdgiClipmapDirtyReason.DirtyBounds)
            });
            var requests = new GPUDdgiProbeUpdateRequest[2];
            var marks = new byte[42];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout,
                dirtyBounds: null,
                activeProbeCount: 42,
                hardMaxRequestCount: 2,
                updateCursor: 0,
                CreateSettings(outOfFrustumFraction: 0.0f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(2));
                Assert.That(requests[0].Priority, Is.EqualTo(DdgiProbeUpdateScheduler.PriorityDirtyBounds));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag, Is.EqualTo(0));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].LogicalCellX, Is.EqualTo(dirtyCell.X));
                Assert.That(requests[0].LogicalCellZ, Is.EqualTo(dirtyCell.Z));
            });
        }

        [Test]
        public void BuildRequests_DistributesLargeClipmapResetAcrossVolume()
        {
            GPUDdgiProbeVolume volume = CreateCameraClipmapVolume(
                firstProbeIndex: 10,
                gridMin: new DdgiClipmapCell(-2, 0, -4),
                ringOffset: DdgiClipmapCell.Zero,
                countX: 4,
                countY: 2,
                countZ: 4,
                spacing: 1.0f);
            var layout = CreateLayout(new[]
            {
                new DdgiFrameLayoutDirtyProbeRequest(
                    0,
                    0,
                    new DdgiClipmapCell(-2, 0, -4),
                    new DdgiClipmapCell(1, 1, -1),
                    10,
                    DdgiClipmapDirtyReason.InitialActivation)
            });
            var requests = new GPUDdgiProbeUpdateRequest[4];
            var marks = new byte[42];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout,
                dirtyBounds: null,
                activeProbeCount: 42,
                hardMaxRequestCount: 4,
                updateCursor: 0,
                CreateSettings(outOfFrustumFraction: 0.0f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(4));
                Assert.That(requests[..result.RequestCount].Select(request => request.Priority), Has.All.EqualTo(DdgiProbeUpdateScheduler.PriorityNewCell));
                Assert.That(requests[..result.RequestCount].Select(request => request.LogicalCellZ).Distinct().Count(), Is.GreaterThan(1));
                Assert.That(requests[..result.RequestCount].Select(request => request.ProbeIndex).Distinct().Count(), Is.EqualTo(4));
            });
        }

        [Test]
        public void BuildRequests_PrioritizesUninitializedClipmapCellsBeforeViewFocusedRefinement()
        {
            GPUDdgiProbeVolume volume = CreateCameraClipmapVolume(
                firstProbeIndex: 0,
                gridMin: new DdgiClipmapCell(-2, 0, -4),
                ringOffset: DdgiClipmapCell.Zero,
                countX: 5,
                countY: 1,
                countZ: 5,
                spacing: 1.0f);
            var cascade = new DdgiClipmapCascadeState(
                cascadeIndex: 0,
                probeCountX: 5,
                probeCountY: 1,
                probeCountZ: 5,
                probeSpacing: 1.0f,
                physicalFirstProbeIndex: 0);
            cascade.ResetTo(new DdgiClipmapCell(-2, 0, -4), frameIndex: 1, DdgiClipmapDirtyReason.InitialActivation);
            var layout = CreateLayout(
                Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
                CreateViewPriorityContext(),
                cameraRelativeCascades: new[] { cascade });
            var requests = new GPUDdgiProbeUpdateRequest[4];
            var marks = new byte[25];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout,
                dirtyBounds: null,
                activeProbeCount: 25,
                hardMaxRequestCount: 4,
                updateCursor: 20,
                CreateSettings(outOfFrustumFraction: 0.0f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(4));
                Assert.That(requests[0].Priority, Is.EqualTo(DdgiProbeUpdateScheduler.PriorityNewCell));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].LogicalCellZ, Is.EqualTo(0));
                Assert.That(requests[0].LogicalCellZ, Is.Not.LessThan(0));
            });
        }

        [Test]
        public void BuildRequests_WhenNoDirtyWork_UsesRoundRobinSafetyRefreshWithoutStarvation()
        {
            GPUDdgiProbeVolume volume = CreateAuthoredVolume(firstProbeIndex: 0, countX: 4, countY: 1, countZ: 2);
            var requests = new GPUDdgiProbeUpdateRequest[4];
            var marks = new byte[8];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout: null,
                dirtyBounds: null,
                activeProbeCount: 8,
                hardMaxRequestCount: 4,
                updateCursor: 6,
                CreateSettings(outOfFrustumFraction: 0.25f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(4));
                Assert.That(result.NextUpdateCursor, Is.EqualTo(2));
                Assert.That(result.SafetyRequestCount, Is.EqualTo(1));
                Assert.That(requests[0].ProbeIndex, Is.EqualTo(6u));
                Assert.That(requests[1].ProbeIndex, Is.EqualTo(7u));
                Assert.That(requests[2].ProbeIndex, Is.EqualTo(0u));
                Assert.That(requests[3].ProbeIndex, Is.EqualTo(1u));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAuthoredVolumeFlag, Is.Not.EqualTo(0));
                Assert.That(requests[1].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag, Is.Not.EqualTo(0));
            });
        }

        [Test]
        public void BuildRequests_WhenViewPriorityAvailable_SchedulesCurrentFrustumProbesFirst()
        {
            GPUDdgiProbeVolume volume = CreateCameraClipmapVolume(
                firstProbeIndex: 0,
                gridMin: new DdgiClipmapCell(-2, 0, -4),
                ringOffset: DdgiClipmapCell.Zero,
                countX: 5,
                countY: 1,
                countZ: 5,
                spacing: 1.0f);
            var layout = CreateLayout(
                Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
                CreateViewPriorityContext());
            var requests = new GPUDdgiProbeUpdateRequest[4];
            var marks = new byte[25];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout,
                dirtyBounds: null,
                activeProbeCount: 25,
                hardMaxRequestCount: 4,
                updateCursor: 20,
                CreateSettings(outOfFrustumFraction: 0.0f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(4));
                Assert.That(requests[0].Priority, Is.EqualTo(DdgiProbeUpdateScheduler.PriorityVisibleFrustum));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].LogicalCellZ, Is.LessThan(0));
                Assert.That(requests[0].LogicalCellX, Is.InRange(-1, 1));
            });
        }

        [Test]
        public void BuildRequests_ReservesSafetyQuotaForSideAndRearShell()
        {
            GPUDdgiProbeVolume volume = CreateCameraClipmapVolume(
                firstProbeIndex: 0,
                gridMin: new DdgiClipmapCell(-2, 0, 1),
                ringOffset: DdgiClipmapCell.Zero,
                countX: 5,
                countY: 1,
                countZ: 2,
                spacing: 1.0f);
            var layout = CreateLayout(
                Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
                CreateViewPriorityContext());
            var requests = new GPUDdgiProbeUpdateRequest[4];
            var marks = new byte[10];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout,
                dirtyBounds: null,
                activeProbeCount: 10,
                hardMaxRequestCount: 4,
                updateCursor: 0,
                CreateSettings(outOfFrustumFraction: 0.5f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(4));
                Assert.That(result.SafetyRequestCount, Is.EqualTo(2));
                Assert.That(requests[0].Priority, Is.EqualTo(DdgiProbeUpdateScheduler.PriorityOutsideFrustumSafety));
                Assert.That(requests[1].Priority, Is.EqualTo(DdgiProbeUpdateScheduler.PriorityOutsideFrustumSafety));
                Assert.That(requests[0].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag, Is.Not.EqualTo(0));
                Assert.That(requests[1].Flags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag, Is.Not.EqualTo(0));
                Assert.That(requests[0].LogicalCellZ, Is.GreaterThan(0));
                Assert.That(requests[1].LogicalCellZ, Is.GreaterThan(0));
            });
        }

        [Test]
        public void BuildRequests_DuringFastCameraMovement_ReducesFarCascadeFocusedWork()
        {
            GPUDdgiProbeVolume near = CreateCameraClipmapVolume(
                firstProbeIndex: 0,
                gridMin: new DdgiClipmapCell(-2, 0, -4),
                ringOffset: DdgiClipmapCell.Zero,
                countX: 5,
                countY: 1,
                countZ: 5,
                spacing: 1.0f,
                cascadeIndex: 0);
            GPUDdgiProbeVolume far = CreateCameraClipmapVolume(
                firstProbeIndex: 25,
                gridMin: new DdgiClipmapCell(-2, 0, 1),
                ringOffset: DdgiClipmapCell.Zero,
                countX: 5,
                countY: 1,
                countZ: 2,
                spacing: 2.0f,
                cascadeIndex: 1);
            var layout = CreateLayout(
                Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
                CreateViewPriorityContext(),
                fastCameraMovement: true,
                volumeCount: 2);
            var requests = new GPUDdgiProbeUpdateRequest[4];
            var marks = new byte[35];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { near, far },
                layout,
                dirtyBounds: null,
                activeProbeCount: 35,
                hardMaxRequestCount: 4,
                updateCursor: 25,
                CreateSettings(outOfFrustumFraction: 0.0f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(4));
                Assert.That(requests[0].VolumeIndex, Is.EqualTo(0u));
                Assert.That(requests[1].VolumeIndex, Is.EqualTo(0u));
                Assert.That(requests[2].VolumeIndex, Is.EqualTo(0u));
                Assert.That(requests[3].VolumeIndex, Is.EqualTo(0u));
                Assert.That(requests[0].Priority, Is.EqualTo(DdgiProbeUpdateScheduler.PriorityVisibleFrustum));
            });
        }

        [Test]
        public void CalculateTimeBudgetedRequestCount_ReducesBudgetWhenPreviousGpuTimeExceeded()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    DdgiProbeUpdateScheduler.CalculateTimeBudgetedRequestCount(100, 1000, 100, 1.0f, 0),
                    Is.EqualTo(100));
                Assert.That(
                    DdgiProbeUpdateScheduler.CalculateTimeBudgetedRequestCount(100, 1000, 25, 1.0f, 0),
                    Is.EqualTo(25));
                Assert.That(
                    DdgiProbeUpdateScheduler.CalculateTimeBudgetedRequestCount(100, 1000, 100, 1.0f, 2_000),
                    Is.EqualTo(50));
                Assert.That(
                    DdgiProbeUpdateScheduler.CalculateTimeBudgetedRequestCount(100, 1000, 100, 0.0f, 0),
                    Is.EqualTo(0));
            });
        }

        [Test]
        public void CalculateTimeBudgetedPrimaryRayCount_UsesColdStartBudgetUntilGpuTimingIsKnown()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    DdgiProbeUpdateScheduler.CalculateTimeBudgetedPrimaryRayCount(32_768, 65_536, 1.0f, 0),
                    Is.EqualTo(65_536));
                Assert.That(
                    DdgiProbeUpdateScheduler.CalculateTimeBudgetedPrimaryRayCount(32_768, 65_536, 1.0f, 500),
                    Is.EqualTo(32_768));
                Assert.That(
                    DdgiProbeUpdateScheduler.CalculateTimeBudgetedPrimaryRayCount(32_768, 65_536, 1.0f, 2_000),
                    Is.EqualTo(16_384));
            });
        }

        [Test]
        public void CalculateAdaptiveBudgets_ReducesWorkAndLightsDuringEmergencyDegrade()
        {
            DdgiAdaptiveBudgetSelection budget = DdgiProbeUpdateScheduler.CalculateAdaptiveBudgets(
                hardMaxRequestCount: 100,
                activeProbeCount: 1000,
                coldStartMaxRequestCount: 100,
                steadyPrimaryRayBudget: 32_000,
                coldStartPrimaryRayBudget: 64_000,
                minimumProbeRefreshFrames: 240,
                maxShadedLights: 8,
                adaptiveEnabled: true,
                budgetMilliseconds: 1.0f,
                hysteresisFraction: 0.15f,
                emergencyDegradeMultiplier: 2.0f,
                previousGpuUpdateMicroseconds: 2_500,
                previousBudgetScale: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(budget.RequestBudget, Is.EqualTo(40));
                Assert.That(budget.PrimaryRayBudget, Is.EqualTo(12_800));
                Assert.That(budget.BudgetScale, Is.EqualTo(0.4f).Within(0.0001f));
                Assert.That(budget.BudgetReduced, Is.True);
                Assert.That(budget.EmergencyDegradeActive, Is.True);
                Assert.That(budget.EffectiveMaxShadedLights, Is.EqualTo(4));
                Assert.That(budget.Reason, Is.EqualTo("emergency-degrade"));
            });
        }

        [Test]
        public void CalculateAdaptiveBudgets_UsesHysteresisBeforeReducing()
        {
            DdgiAdaptiveBudgetSelection budget = DdgiProbeUpdateScheduler.CalculateAdaptiveBudgets(
                hardMaxRequestCount: 100,
                activeProbeCount: 1000,
                coldStartMaxRequestCount: 100,
                steadyPrimaryRayBudget: 32_000,
                coldStartPrimaryRayBudget: 64_000,
                minimumProbeRefreshFrames: 240,
                maxShadedLights: 8,
                adaptiveEnabled: true,
                budgetMilliseconds: 1.0f,
                hysteresisFraction: 0.15f,
                emergencyDegradeMultiplier: 2.0f,
                previousGpuUpdateMicroseconds: 1_100,
                previousBudgetScale: 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(budget.RequestBudget, Is.EqualTo(100));
                Assert.That(budget.PrimaryRayBudget, Is.EqualTo(32_000));
                Assert.That(budget.BudgetScale, Is.EqualTo(1.0f));
                Assert.That(budget.BudgetReduced, Is.False);
                Assert.That(budget.EmergencyDegradeActive, Is.False);
                Assert.That(budget.EffectiveMaxShadedLights, Is.EqualTo(8));
                Assert.That(budget.Reason, Is.EqualTo("within-budget"));
            });
        }

        [Test]
        public void CalculateAdaptiveBudgets_PreservesMinimumRefreshWhenNotEmergency()
        {
            DdgiAdaptiveBudgetSelection budget = DdgiProbeUpdateScheduler.CalculateAdaptiveBudgets(
                hardMaxRequestCount: 100,
                activeProbeCount: 1000,
                coldStartMaxRequestCount: 100,
                steadyPrimaryRayBudget: 32_000,
                coldStartPrimaryRayBudget: 64_000,
                minimumProbeRefreshFrames: 10,
                maxShadedLights: 8,
                adaptiveEnabled: true,
                budgetMilliseconds: 1.0f,
                hysteresisFraction: 0.15f,
                emergencyDegradeMultiplier: 8.0f,
                previousGpuUpdateMicroseconds: 2_000,
                previousBudgetScale: 0.1f);

            Assert.Multiple(() =>
            {
                Assert.That(budget.RequestBudget, Is.EqualTo(100));
                Assert.That(budget.PrimaryRayBudget, Is.EqualTo(3_200));
                Assert.That(budget.BudgetScale, Is.EqualTo(0.1f).Within(0.0001f));
                Assert.That(budget.BudgetReduced, Is.True);
                Assert.That(budget.EmergencyDegradeActive, Is.False);
                Assert.That(budget.EffectiveMaxShadedLights, Is.EqualTo(8));
                Assert.That(budget.Reason, Is.EqualTo("gpu-time"));
            });
        }

        [Test]
        public void BuildRequests_StopsAtPrimaryRayBudget()
        {
            GPUDdgiProbeVolume volume = CreateAuthoredVolume(firstProbeIndex: 0, countX: 8, countY: 1, countZ: 1, raysPerProbe: 64);
            var requests = new GPUDdgiProbeUpdateRequest[8];
            var marks = new byte[8];

            DdgiProbeUpdateSchedulerResult result = DdgiProbeUpdateScheduler.BuildRequests(
                new[] { volume },
                layout: null,
                dirtyBounds: null,
                activeProbeCount: 8,
                hardMaxRequestCount: 8,
                hardMaxPrimaryRayCount: 128,
                updateCursor: 0,
                CreateSettings(outOfFrustumFraction: 0.0f),
                requests,
                marks);

            Assert.Multiple(() =>
            {
                Assert.That(result.RequestCount, Is.EqualTo(2));
                Assert.That(requests[0].ProbeIndex, Is.EqualTo(0u));
                Assert.That(requests[1].ProbeIndex, Is.EqualTo(1u));
                Assert.That(marks[0], Is.EqualTo(1));
                Assert.That(marks[1], Is.EqualTo(1));
                Assert.That(marks[2], Is.EqualTo(0));
            });
        }

        private static DdgiFrameLayout CreateLayout(
            DdgiFrameLayoutDirtyProbeRequest[] dirtyRequests,
            DdgiViewPriorityContext viewPriority = default,
            bool fastCameraMovement = false,
            int volumeCount = 1,
            IReadOnlyList<DdgiClipmapCascadeState>? cameraRelativeCascades = null)
        {
            var volumes = new GlobalIlluminationProbeVolume[volumeCount];
            var metadata = new DdgiProbeVolumeRuntimeMetadata[volumeCount];
            for (int i = 0; i < volumeCount; i++)
            {
                volumes[i] = new GlobalIlluminationProbeVolume();
                metadata[i] = DdgiProbeVolumeRuntimeMetadata.Authored;
            }

            return new DdgiFrameLayout(
                volumes,
                metadata,
                Array.Empty<BoundingBox>(),
                dirtyRequests,
                isDdgiActive: true,
                cameraRelativeEnabled: true,
                defaultVolumeIncluded: false,
                authoredVolumeCount: 0,
                cameraRelativeCascadeCount: 1,
                authoredProbeCount: 0,
                cameraRelativeProbeCount: 32,
                totalPhysicalProbeCount: 32,
                viewPriority,
                cameraRelativeCascades,
                movementClass: fastCameraMovement ? DdgiCameraMovementClass.Fast : DdgiCameraMovementClass.Normal,
                fastCameraMovement: fastCameraMovement);
        }

        private static GlobalIlluminationSettings CreateSettings(float outOfFrustumFraction)
        {
            return new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiCameraRelativeEnabled = true,
                DdgiOutOfFrustumMinimumUpdateFraction = outOfFrustumFraction,
                DdgiFrustumPriorityWeight = 2.0f,
                DdgiProbeUpdateTimeBudgetMilliseconds = 1.0f
            };
        }

        private static DdgiViewPriorityContext CreateViewPriorityContext()
        {
            return new DdgiViewPriorityContext(
                true,
                Vector3.Zero,
                Vector3.Forward,
                Vector3.Right,
                Vector3.Up,
                Vector3.Zero,
                0.1f,
                32.0f,
                1.0f,
                1.0f,
                0.5f,
                8.0f);
        }

        private static GPUDdgiProbeVolume CreateCameraClipmapVolume(
            int firstProbeIndex,
            DdgiClipmapCell gridMin,
            DdgiClipmapCell ringOffset,
            int countX,
            int countY,
            int countZ,
            float spacing,
            int cascadeIndex = 0)
        {
            return new GPUDdgiProbeVolume
            {
                OriginAndFirstProbeIndex = new Vector4(gridMin.X * spacing, gridMin.Y * spacing, gridMin.Z * spacing, firstProbeIndex),
                SizeAndProbeCountX = new Vector4(spacing * (countX - 1), spacing * (countY - 1), spacing * (countZ - 1), countX),
                ProbeSpacingAndProbeCountY = new Vector4(spacing, spacing, spacing, countY),
                BiasAndProbeCountZ = new Vector4(0.05f, 0.2f, 16.0f, countZ),
                ClipmapGridMinAndKind = new Vector4(gridMin.X, gridMin.Y, gridMin.Z, (uint)DdgiProbeVolumeKind.CameraClipmap),
                ClipmapRingOffsetAndCascade = new Vector4(ringOffset.X, ringOffset.Y, ringOffset.Z, cascadeIndex)
            };
        }

        private static GPUDdgiProbeVolume CreateAuthoredVolume(int firstProbeIndex, int countX, int countY, int countZ, int raysPerProbe = 16)
        {
            return new GPUDdgiProbeVolume
            {
                OriginAndFirstProbeIndex = new Vector4(0.0f, 0.0f, 0.0f, firstProbeIndex),
                SizeAndProbeCountX = new Vector4(countX - 1, countY - 1, countZ - 1, countX),
                ProbeSpacingAndProbeCountY = new Vector4(1.0f, 1.0f, 1.0f, countY),
                BiasAndProbeCountZ = new Vector4(0.05f, 0.2f, 16.0f, countZ),
                RayAndUpdateParams = new Vector4(raysPerProbe, 0.0f, 0.0f, 0.0f),
                ClipmapGridMinAndKind = new Vector4(0.0f, 0.0f, 0.0f, (uint)DdgiProbeVolumeKind.Authored)
            };
        }
    }
}
