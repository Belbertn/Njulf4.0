using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiScrollPlannerTests
{
    [Test]
    public void CameraCutTransition_DetectsFirstCutAfterZeroBaseline()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.IsCameraCutTransition(
                    ulong.MaxValue,
                    0UL),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.IsCameraCutTransition(0UL, 1UL),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.IsCameraCutTransition(1UL, 1UL),
                Is.False);
        });
    }

    [Test]
    public void CameraCutSignals_DoNotForceOverlappingDdgiRebase()
    {
        Assert.Multiple(() =>
        {
            // A 6 m move can trip temporal/Hi-Z cut policy while retaining
            // most of a 28-cell ring.
            Assert.That(
                SimpleDdgiVolumeManager.IsCameraCutTransition(4UL, 5UL),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.RequiresRingRebase(
                    28, 14, 28, 6, 0, 0),
                Is.False);
            // Rotation changes no world-space ring origin at all.
            Assert.That(
                SimpleDdgiVolumeManager.RequiresRingRebase(
                    28, 14, 28, 0, 0, 0),
                Is.False);
        });
    }

    [Test]
    public void NoOverlapTeleport_RequiresTrueRebase()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.RequiresRingRebase(
                    28, 14, 28, 28, 0, 0),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.RequiresRingRebase(
                    28, 14, 28, -28, 0, 0),
                Is.True);
        });
    }

    [TestCase(28, 14, 28, 1, 0, 0, 392)]
    [TestCase(28, 14, 28, -1, 0, 0, 392)]
    [TestCase(28, 14, 28, 1, 0, 1, 770)]
    [TestCase(28, 14, 28, 1, 1, 1, 1499)]
    [TestCase(28, 14, 28, 28, 0, 0, 10976)]
    public void ExposedCount_IsExactUnionWithoutDuplicateEdges(
        int countX,
        int countY,
        int countZ,
        int deltaX,
        int deltaY,
        int deltaZ,
        int expected)
    {
        Assert.That(
            SimpleDdgiScrollPlanner.CountExposedProbes(
                countX,
                countY,
                countZ,
                deltaX,
                deltaY,
                deltaZ),
            Is.EqualTo(expected));
    }

    [Test]
    public void ExposedEnumerator_VisitsEachPhysicalReplacementOnce()
    {
        var cells = new HashSet<(int X, int Y, int Z)>();
        foreach (SimpleDdgiExposedCell cell in new SimpleDdgiExposedCells(
                     6,
                     4,
                     5,
                     1,
                     -1,
                     1))
        {
            Assert.That(cells.Add((cell.X, cell.Y, cell.Z)), Is.True);
        }

        Assert.That(
            cells.Count,
            Is.EqualTo(SimpleDdgiScrollPlanner.CountExposedProbes(
                6,
                4,
                5,
                1,
                -1,
                1)));
    }

    [Test]
    public void ExposedEnumerator_UsesOldOriginMinusNewOriginDirection()
    {
        var positiveWorldMotion = new List<SimpleDdgiExposedCell>();
        foreach (SimpleDdgiExposedCell cell in new SimpleDdgiExposedCells(
                     4,
                     2,
                     3,
                     deltaX: -1,
                     deltaY: 0,
                     deltaZ: 0))
        {
            positiveWorldMotion.Add(cell);
        }

        var negativeWorldMotion = new List<SimpleDdgiExposedCell>();
        foreach (SimpleDdgiExposedCell cell in new SimpleDdgiExposedCells(
                     4,
                     2,
                     3,
                     deltaX: 1,
                     deltaY: 0,
                     deltaZ: 0))
        {
            negativeWorldMotion.Add(cell);
        }

        Assert.Multiple(() =>
        {
            Assert.That(positiveWorldMotion, Has.All.Property("X").EqualTo(3));
            Assert.That(negativeWorldMotion, Has.All.Property("X").EqualTo(0));
        });
    }

    [Test]
    public void IncrementalPlanner_UsesAllAxesWhenMinimumCohortFits()
    {
        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                4,
                -2,
                3,
                22,
                11,
                22,
                maintenanceRaysPerProbe: 16,
                fullRaysPerProbe: 64,
                frameRayBuckets: new uint[] { 64, 16, 32, 0, 0, 0 },
                availableProbeRequests: 1_000,
                availablePrimaryRays: 16_384,
                out SimpleDdgiScrollStep step),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(step.DeltaX, Is.EqualTo(1));
            Assert.That(step.DeltaY, Is.EqualTo(-1));
            Assert.That(step.DeltaZ, Is.EqualTo(1));
            Assert.That(step.ExposedProbeCount, Is.EqualTo(914));
            Assert.That(step.BootstrapRaysPerProbe, Is.EqualTo(16));
            Assert.That(
                SimpleDdgiRayBucketPolicy.Contains(
                    new uint[] { 64, 16, 32, 0, 0, 0 },
                    step.BootstrapRaysPerProbe),
                Is.True);
            Assert.That(step.ReservedPrimaryRays, Is.LessThanOrEqualTo(16_384UL));
        });
    }

    [Test]
    public void IncrementalPlanner_SelectsBudgetedAxisSubsetInsteadOfOverspending()
    {
        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                1,
                1,
                1,
                16,
                8,
                16,
                maintenanceRaysPerProbe: 16,
                fullRaysPerProbe: 32,
                frameRayBuckets: new uint[] { 32, 16, 8, 0, 0, 0 },
                availableProbeRequests: 256,
                availablePrimaryRays: 4_096,
                out SimpleDdgiScrollStep step),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(step.ExposedProbeCount, Is.LessThanOrEqualTo(256));
            Assert.That(step.ReservedPrimaryRays, Is.LessThanOrEqualTo(4_096UL));
            Assert.That(step.DeltaX != 0 || step.DeltaY != 0 || step.DeltaZ != 0,
                Is.True);
        });
    }

    [Test]
    public void IncrementalPlanner_DefersWhenEvenOnePlaneCannotFit()
    {
        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                1,
                0,
                0,
                32,
                16,
                32,
                maintenanceRaysPerProbe: 48,
                fullRaysPerProbe: 192,
                frameRayBuckets: new uint[] { 192, 48, 96, 0, 0, 0 },
                availableProbeRequests: 100,
                availablePrimaryRays: 1_000,
                out _),
            Is.False);
    }

    [Test]
    public void HighNearRingLateralScroll_SelectsSupported64RayBucket()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        uint[] buckets = new uint[SimpleDdgiSchedulerAbi.MaxRayBucketCount];
        SimpleDdgiRayBucketPolicy.Build(settings.GlobalIllumination, buckets);

        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                1,
                0,
                0,
                28,
                14,
                28,
                settings.GlobalIllumination.SimpleDdgiNearMaintenanceRaysPerProbe,
                settings.GlobalIllumination.SimpleDdgiNearFullRaysPerProbe,
                buckets,
                availableProbeRequests: 1_000,
                availablePrimaryRays:
                    SimpleDdgiScrollPlanner.MaximumSpatialRecoveryPrimaryRays,
                out SimpleDdgiScrollStep step),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(step.ExposedProbeCount, Is.EqualTo(392));
            Assert.That(step.BootstrapRaysPerProbe, Is.EqualTo(64));
            Assert.That(
                SimpleDdgiRayBucketPolicy.Contains(
                    buckets,
                    step.BootstrapRaysPerProbe),
                Is.True);
        });
    }

    [TestCase(RenderQualityPreset.Low)]
    [TestCase(RenderQualityPreset.Medium)]
    [TestCase(RenderQualityPreset.High)]
    [TestCase(RenderQualityPreset.Ultra)]
    [TestCase(RenderQualityPreset.DdgiHigh)]
    public void QualityPreset_PlannedCardinalitiesAlwaysOccurInUploadedBuckets(
        RenderQualityPreset preset)
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(preset);
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        Span<uint> buckets = stackalloc uint[SimpleDdgiSchedulerAbi.MaxRayBucketCount];
        SimpleDdgiRayBucketPolicy.Build(gi, buckets);

        (int X, int Y, int Z)[] deltas =
        [
            (1, 0, 0), (-1, 0, 0),
            (0, 1, 0), (0, -1, 0),
            (0, 0, 1), (0, 0, -1),
            (1, -1, 1), (-1, 1, -1)
        ];
        for (int ring = 0; ring < Math.Clamp(gi.SimpleDdgiRingCount, 1, 3); ring++)
        {
            int maintenance = ring switch
            {
                1 => gi.SimpleDdgiMidMaintenanceRaysPerProbe,
                2 => gi.SimpleDdgiFarMaintenanceRaysPerProbe,
                _ => gi.SimpleDdgiNearMaintenanceRaysPerProbe
            };
            int full = ring switch
            {
                1 => gi.SimpleDdgiMidFullRaysPerProbe,
                2 => gi.SimpleDdgiFarFullRaysPerProbe,
                _ => gi.SimpleDdgiNearFullRaysPerProbe
            };
            foreach ((int deltaX, int deltaY, int deltaZ) in deltas)
            {
                Assert.That(
                    SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                        deltaX,
                        deltaY,
                        deltaZ,
                        4,
                        3,
                        4,
                        maintenance,
                        full,
                        buckets,
                        availableProbeRequests: 48,
                        availablePrimaryRays: 48UL * (ulong)full,
                        out SimpleDdgiScrollStep step),
                    Is.True,
                    $"preset={preset}, ring={ring}, delta={deltaX},{deltaY},{deltaZ}");
                Assert.That(
                    SimpleDdgiRayBucketPolicy.Contains(
                        buckets,
                        step.BootstrapRaysPerProbe),
                    Is.True,
                    $"preset={preset}, ring={ring}, rays={step.BootstrapRaysPerProbe}");
            }
        }
    }

    [Test]
    public void CustomizedPolicy_UsesTheSameTableForPlanningAndUpload()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.SimpleDdgiRingCount = 3;
        gi.SimpleDdgiNearFullRaysPerProbe = 111;
        gi.SimpleDdgiNearMaintenanceRaysPerProbe = 13;
        gi.SimpleDdgiMidFullRaysPerProbe = 73;
        gi.SimpleDdgiMidMaintenanceRaysPerProbe = 11;
        gi.SimpleDdgiFarFullRaysPerProbe = 37;
        gi.SimpleDdgiFarMaintenanceRaysPerProbe = 7;
        Span<uint> buckets = stackalloc uint[SimpleDdgiSchedulerAbi.MaxRayBucketCount];
        SimpleDdgiRayBucketPolicy.Build(gi, buckets);

        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                1,
                0,
                -1,
                5,
                4,
                5,
                gi.SimpleDdgiNearMaintenanceRaysPerProbe,
                gi.SimpleDdgiNearFullRaysPerProbe,
                buckets,
                availableProbeRequests: 100,
                availablePrimaryRays: 5_000,
                out SimpleDdgiScrollStep step),
            Is.True);
        Assert.That(
            SimpleDdgiRayBucketPolicy.Contains(
                buckets,
                step.BootstrapRaysPerProbe),
            Is.True);
    }

    [Test]
    public void IncrementalPlanner_DefersWhenNoFrameBucketFitsRingBounds()
    {
        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                1,
                0,
                0,
                8,
                4,
                8,
                maintenanceRaysPerProbe: 16,
                fullRaysPerProbe: 32,
                frameRayBuckets: new uint[] { 128, 64, 8, 0, 0, 0 },
                availableProbeRequests: 256,
                availablePrimaryRays: 32_768,
                out _),
            Is.False);
    }

    [Test]
    public void TwoCascades_ReserveCompleteSupportedCohortsWithinOneFrame()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        uint[] buckets = new uint[SimpleDdgiSchedulerAbi.MaxRayBucketCount];
        SimpleDdgiRayBucketPolicy.Build(gi, buckets);
        int requests = 2_048;
        ulong rays = SimpleDdgiScrollPlanner.MaximumSpatialRecoveryPrimaryRays;

        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                1, 0, 0, 28, 14, 28,
                gi.SimpleDdgiNearMaintenanceRaysPerProbe,
                gi.SimpleDdgiNearFullRaysPerProbe,
                buckets,
                requests,
                rays,
                out SimpleDdgiScrollStep near),
            Is.True);
        requests -= near.ExposedProbeCount;
        rays -= near.ReservedPrimaryRays;

        Assert.That(
            SimpleDdgiScrollPlanner.TryPlanIncrementalStep(
                0, 0, -1, 16, 8, 16,
                gi.SimpleDdgiMidMaintenanceRaysPerProbe,
                gi.SimpleDdgiMidFullRaysPerProbe,
                buckets,
                requests,
                rays,
                out SimpleDdgiScrollStep mid),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiRayBucketPolicy.Contains(
                    buckets,
                    near.BootstrapRaysPerProbe),
                Is.True);
            Assert.That(
                SimpleDdgiRayBucketPolicy.Contains(
                    buckets,
                    mid.BootstrapRaysPerProbe),
                Is.True);
            Assert.That(
                near.ReservedPrimaryRays + mid.ReservedPrimaryRays,
                Is.LessThanOrEqualTo(
                    SimpleDdgiScrollPlanner.MaximumSpatialRecoveryPrimaryRays));
        });
    }
}
