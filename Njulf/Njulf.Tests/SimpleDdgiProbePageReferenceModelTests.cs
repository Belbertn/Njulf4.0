using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiProbePageReferenceModelTests
{
    private static readonly SimpleDdgiPageReferenceSettings Settings = new(
        RetentionFrames: 3,
        MaximumAdmissionsPerFrame: 2,
        MaximumEvictionsPerFrame: 2,
        EmptySuppressionConfirmationCount: 2,
        SuppressedRetryFrames: 4);

    [Test]
    public void AdmissionUsesDemandDistanceAndVirtualIndexTotalOrder()
    {
        var model = new SimpleDdgiProbePageReferenceModel(8, 2);
        var transitions = new SimpleDdgiPageTransition[4];
        SimpleDdgiPageDemand[] demand =
        {
            new(4, SimpleDdgiPageDemandClass.ReceiverMiss, 1),
            new(3, SimpleDdgiPageDemandClass.VisibleSurface, 8),
            new(2, SimpleDdgiPageDemandClass.VisibleSurface, 3),
            new(1, SimpleDdgiPageDemandClass.VisibleSurface, 3)
        };

        SimpleDdgiPageReconcileSummary result = model.Reconcile(
            1,
            demand,
            Settings,
            transitions);

        Assert.Multiple(() =>
        {
            Assert.That(result.AdmissionCount, Is.EqualTo(2));
            Assert.That(result.FailedAdmissionCount, Is.EqualTo(2));
            Assert.That(model.GetPhysicalOwner(0), Is.EqualTo(1));
            Assert.That(model.GetPhysicalOwner(1), Is.EqualTo(2));
            Assert.That(model.ValidateBijection(out string reason), Is.True, reason);
        });
    }

    [Test]
    public void FullDemandedPoolHasNoVictimAndFailsClosed()
    {
        var model = new SimpleDdgiProbePageReferenceModel(3, 2);
        var transitions = new SimpleDdgiPageTransition[4];
        model.Reconcile(
            1,
            new[]
            {
                new SimpleDdgiPageDemand(0, SimpleDdgiPageDemandClass.VisibleSurface, 0),
                new SimpleDdgiPageDemand(1, SimpleDdgiPageDemandClass.VisibleSurface, 1)
            },
            Settings,
            transitions);
        model.MarkPublished(0, 1);
        model.MarkPublished(1, 1);

        SimpleDdgiPageReconcileSummary result = model.Reconcile(
            2,
            new[]
            {
                new SimpleDdgiPageDemand(0, SimpleDdgiPageDemandClass.VisibleSurface, 0),
                new SimpleDdgiPageDemand(1, SimpleDdgiPageDemandClass.VisibleSurface, 1),
                new SimpleDdgiPageDemand(2, SimpleDdgiPageDemandClass.VisibleSurface, 2)
            },
            Settings,
            transitions);

        Assert.Multiple(() =>
        {
            Assert.That(result.AdmissionCount, Is.Zero);
            Assert.That(result.EvictionCount, Is.Zero);
            Assert.That(result.FailedAdmissionCount, Is.EqualTo(1));
            Assert.That(result.PoolPressure, Is.True);
            Assert.That(model.GetPageState(2).PhysicalPageIndex, Is.EqualTo(-1));
        });
    }

    [Test]
    public void DevelopmentPinDemandsMissingPageAndPreventsEvictionUntilReleased()
    {
        var model = new SimpleDdgiProbePageReferenceModel(2, 1);
        var transitions = new SimpleDdgiPageTransition[2];
        SimpleDdgiPageReferenceSettings settings = Settings with
        {
            MaximumAdmissionsPerFrame = 1,
            MaximumEvictionsPerFrame = 1
        };

        model.SetPinned(0, true);
        SimpleDdgiPageReconcileSummary admitted = model.Reconcile(
            1,
            Array.Empty<SimpleDdgiPageDemand>(),
            settings,
            transitions);
        SimpleDdgiPageReconcileSummary protectedResult = model.Reconcile(
            8,
            new[]
            {
                new SimpleDdgiPageDemand(
                    1,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    0)
            },
            settings,
            transitions);

        model.SetPinned(0, false);
        SimpleDdgiPageReconcileSummary releasedResult = model.Reconcile(
            12,
            new[]
            {
                new SimpleDdgiPageDemand(
                    1,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    0)
            },
            settings,
            transitions);

        Assert.Multiple(() =>
        {
            Assert.That(admitted.AdmissionCount, Is.EqualTo(1));
            Assert.That(protectedResult.FailedAdmissionCount, Is.EqualTo(1));
            Assert.That(protectedResult.EvictionCount, Is.Zero);
            Assert.That(releasedResult.AdmissionCount, Is.EqualTo(1));
            Assert.That(releasedResult.EvictionCount, Is.EqualTo(1));
            Assert.That(model.GetPhysicalOwner(0), Is.EqualTo(1));
        });
    }

    [Test]
    public void CameraCutEvictsOldUndemandedPageDeterministically()
    {
        var model = new SimpleDdgiProbePageReferenceModel(4, 2);
        var transitions = new SimpleDdgiPageTransition[4];
        model.Reconcile(
            1,
            new[]
            {
                new SimpleDdgiPageDemand(0, SimpleDdgiPageDemandClass.VisibleSurface, 0),
                new SimpleDdgiPageDemand(1, SimpleDdgiPageDemandClass.VisibleSurface, 1)
            },
            Settings,
            transitions);
        model.MarkPublished(0, 1);
        model.MarkPublished(1, 1);

        SimpleDdgiPageReconcileSummary result = model.Reconcile(
            2,
            new[]
            {
                new SimpleDdgiPageDemand(2, SimpleDdgiPageDemandClass.VisibleSurface, 0)
            },
            Settings,
            transitions,
            cameraCut: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.AdmissionCount, Is.EqualTo(1));
            Assert.That(result.EvictionCount, Is.EqualTo(1));
            Assert.That(model.GetPageState(2).PhysicalPageIndex, Is.EqualTo(0));
            Assert.That(model.GetPageState(0).PhysicalPageIndex, Is.EqualTo(-1));
            Assert.That(model.GetPageState(1).PhysicalPageIndex, Is.EqualTo(1),
                "A camera cut waives retention under admission pressure; it must not flush unrelated pages eagerly.");
        });
    }

    [Test]
    public void DemandGapResetsFirstRequestAgeBeforeLaterAdmission()
    {
        var model = new SimpleDdgiProbePageReferenceModel(3, 1);
        var transitions = new SimpleDdgiPageTransition[2];
        SimpleDdgiPageReferenceSettings settings = Settings with
        {
            MaximumAdmissionsPerFrame = 1,
            MaximumEvictionsPerFrame = 1
        };

        // Page 2 first requests while page 0 wins the only slot.
        model.Reconcile(
            1,
            new[]
            {
                new SimpleDdgiPageDemand(
                    0,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    0),
                new SimpleDdgiPageDemand(
                    2,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    1)
            },
            settings,
            transitions);
        // This gap ends page 2's outstanding request.
        model.Reconcile(
            2,
            new[]
            {
                new SimpleDdgiPageDemand(
                    0,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    0)
            },
            settings,
            transitions);
        model.Reconcile(5, Array.Empty<SimpleDdgiPageDemand>(), settings, transitions);

        model.Reconcile(
            6,
            new[]
            {
                new SimpleDdgiPageDemand(
                    2,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    0),
                new SimpleDdgiPageDemand(
                    1,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    0)
            },
            settings,
            transitions);

        Assert.That(model.GetPageState(1).PhysicalPageIndex, Is.EqualTo(0),
            "After a demand gap both requests start this frame, so virtual index is the deterministic final tie breaker.");
    }

    [Test]
    public void AllInactivePageSuppressesAndGeometryRevisionReactivates()
    {
        var model = new SimpleDdgiProbePageReferenceModel(2, 1);
        var transitions = new SimpleDdgiPageTransition[2];
        model.Reconcile(
            1,
            new[] { new SimpleDdgiPageDemand(0, SimpleDdgiPageDemandClass.VisibleSurface, 0) },
            Settings with { MaximumAdmissionsPerFrame = 1, MaximumEvictionsPerFrame = 1 },
            transitions);
        model.MarkPublished(0, 1);
        model.Reconcile(2, System.Array.Empty<SimpleDdgiPageDemand>(),
            Settings with { MaximumAdmissionsPerFrame = 1, MaximumEvictionsPerFrame = 1 },
            transitions);
        model.ReportClassification(0, 8, 8, 1, 2);
        model.ReportClassification(0, 8, 8, 1, 3);

        Assert.That(model.GetPageState(0).Lifecycle,
            Is.EqualTo(SimpleDdgiPageLifecycle.SuppressedEmpty));
        model.InvalidateGeometry(2);
        Assert.That(model.GetPageState(0).Lifecycle,
            Is.EqualTo(SimpleDdgiPageLifecycle.ResidentFresh));
        Assert.That(model.GetPageState(0).EmptyConfirmationCount, Is.Zero);
    }

    [Test]
    public void MixedActiveInactivePage_NeverBecomesSuppressible()
    {
        var model = new SimpleDdgiProbePageReferenceModel(1, 1);
        var transitions = new SimpleDdgiPageTransition[2];
        SimpleDdgiPageReferenceSettings settings = Settings with
        {
            MaximumAdmissionsPerFrame = 1,
            MaximumEvictionsPerFrame = 1
        };
        model.Reconcile(
            1,
            new[]
            {
                new SimpleDdgiPageDemand(
                    0,
                    SimpleDdgiPageDemandClass.VisibleSurface,
                    0)
            },
            settings,
            transitions);
        model.MarkPublished(0, 1);

        model.ReportClassification(0, 8, 7, 1, 2);
        model.ReportClassification(0, 8, 7, 1, 3);
        SimpleDdgiPageReferenceState state = model.GetPageState(0);

        Assert.Multiple(() =>
        {
            Assert.That(state.Lifecycle,
                Is.EqualTo(SimpleDdgiPageLifecycle.ResidentPublished));
            Assert.That(state.EmptyConfirmationCount, Is.Zero);
            Assert.That(state.PhysicalPageIndex, Is.EqualTo(0));
        });
    }

    [Test]
    public void SuppressedPage_RetriesOnlyAtTheDeclaredBoundedInterval()
    {
        var model = new SimpleDdgiProbePageReferenceModel(1, 1);
        var transitions = new SimpleDdgiPageTransition[2];
        SimpleDdgiPageReferenceSettings settings = Settings with
        {
            MaximumAdmissionsPerFrame = 1,
            MaximumEvictionsPerFrame = 1
        };
        SimpleDdgiPageDemand[] demand =
        {
            new(0, SimpleDdgiPageDemandClass.VisibleSurface, 0)
        };
        model.Reconcile(1, demand, settings, transitions);
        model.MarkPublished(0, 1);
        model.ReportClassification(0, 8, 8, 1, 2);
        model.ReportClassification(0, 8, 8, 1, 3);
        model.Reconcile(3, System.Array.Empty<SimpleDdgiPageDemand>(),
            settings, transitions);

        SimpleDdgiPageReconcileSummary early = model.Reconcile(
            4,
            demand,
            settings,
            transitions);
        SimpleDdgiPageReconcileSummary due = model.Reconcile(
            7,
            demand,
            settings,
            transitions);

        Assert.Multiple(() =>
        {
            Assert.That(early.AdmissionCount, Is.Zero);
            Assert.That(early.SuppressedPageCount, Is.EqualTo(1));
            Assert.That(due.AdmissionCount, Is.EqualTo(1));
            Assert.That(due.SuppressedPageCount, Is.Zero);
            Assert.That(model.GetPageState(0).DemandClass,
                Is.EqualTo(SimpleDdgiPageDemandClass.SuppressedRetry));
        });
    }

    [Test]
    public void ResourceTransactionClearsOwnersAndAdvancesNonZeroGeneration()
    {
        var model = new SimpleDdgiProbePageReferenceModel(2, 1);
        var transitions = new SimpleDdgiPageTransition[2];
        model.Reconcile(
            1,
            new[] { new SimpleDdgiPageDemand(0, SimpleDdgiPageDemandClass.VisibleSurface, 0) },
            Settings with { MaximumAdmissionsPerFrame = 1, MaximumEvictionsPerFrame = 1 },
            transitions);
        uint previous = model.ResidencyResourceGeneration;

        model.BeginNewResourceTransaction();

        Assert.Multiple(() =>
        {
            Assert.That(model.ResidencyResourceGeneration, Is.Not.EqualTo(0u));
            Assert.That(model.ResidencyResourceGeneration, Is.Not.EqualTo(previous));
            Assert.That(model.GetPhysicalOwner(0), Is.EqualTo(-1));
            Assert.That(model.GetPageState(0).PhysicalPageIndex, Is.EqualTo(-1));
            Assert.That(model.GetPageState(0).DemandClass,
                Is.EqualTo(SimpleDdgiPageDemandClass.None));
            Assert.That(model.GetPageState(0).LastRelevantFrame, Is.Zero);
            Assert.That(model.ValidateBijection(out string reason), Is.True, reason);
        });
    }
}
