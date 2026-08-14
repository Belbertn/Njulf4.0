using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ReflectionProbeGpuBudgetPlannerTests
{
    [Test]
    public void ColdFrameAdmitsOneUnitAndWaitsForTimingHistory()
    {
        ReflectionProbeGpuBudgetPlanner planner = new();
        planner.BeginFrame(400);

        Assert.Multiple(() =>
        {
            Assert.That(planner.TryReserve(ReflectionProbeWorkKind.CaptureFace), Is.True);
            Assert.That(planner.CanReserve(ReflectionProbeWorkKind.CaptureFace), Is.False);
            Assert.That(planner.GetSnapshot().HasTimingHistory, Is.False);
        });
    }

    [Test]
    public void TimingHistoryUsesPerUnitEwmaForAdmission()
    {
        ReflectionProbeGpuBudgetPlanner planner = new();
        planner.BeginFrame(500);
        planner.RecordTiming(ReflectionProbeWorkKind.CaptureFace, unitCount: 2, measuredMicroseconds: 400);

        planner.BeginFrame(400);

        Assert.Multiple(() =>
        {
            Assert.That(planner.TryReserve(ReflectionProbeWorkKind.CaptureFace), Is.True);
            Assert.That(planner.TryReserve(ReflectionProbeWorkKind.CaptureFace), Is.True);
            Assert.That(planner.TryReserve(ReflectionProbeWorkKind.CaptureFace), Is.True);
            Assert.That(planner.CanReserve(ReflectionProbeWorkKind.CaptureFace), Is.False);
            Assert.That(planner.GetSnapshot().FaceEstimateMicroseconds, Is.EqualTo(125));
        });
    }

    [Test]
    public void FailedUnitReleasesItsReservation()
    {
        ReflectionProbeGpuBudgetPlanner planner = new();
        planner.BeginFrame(200);
        planner.RecordTiming(ReflectionProbeWorkKind.PrefilterMip, unitCount: 1, measuredMicroseconds: 100);

        Assert.That(planner.TryReserve(ReflectionProbeWorkKind.PrefilterMip), Is.True);
        planner.Release(ReflectionProbeWorkKind.PrefilterMip);

        ReflectionProbeGpuBudgetSnapshot snapshot = planner.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.ReservedMicroseconds, Is.Zero);
            Assert.That(planner.CanReserve(ReflectionProbeWorkKind.PrefilterMip), Is.True);
        });
    }

    [Test]
    public void LearnedUnitAboveBudgetStillAdmitsOneUnitPerFrame()
    {
        ReflectionProbeGpuBudgetPlanner planner = new();
        planner.RecordTiming(
            ReflectionProbeWorkKind.CaptureFace,
            unitCount: 1,
            measuredMicroseconds: 4_000);

        planner.BeginFrame(100);

        Assert.Multiple(() =>
        {
            Assert.That(planner.TryReserve(ReflectionProbeWorkKind.CaptureFace), Is.True);
            Assert.That(planner.CanReserve(ReflectionProbeWorkKind.CaptureFace), Is.False);
            Assert.That(planner.GetSnapshot().ReservedMicroseconds,
                Is.GreaterThan(planner.GetSnapshot().BudgetMicroseconds));
        });

        planner.BeginFrame(100);
        Assert.That(planner.TryReserve(ReflectionProbeWorkKind.CaptureFace), Is.True);
    }
}
