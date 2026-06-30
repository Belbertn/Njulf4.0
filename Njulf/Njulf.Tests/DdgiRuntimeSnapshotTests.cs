using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiRuntimeSnapshotTests
    {
        [Test]
        public void DdgiRuntimeSnapshot_EmptyMatchesPhaseOneContractDefaults()
        {
            DdgiRuntimeSnapshot snapshot = DdgiRuntimeSnapshot.Empty;

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.VolumeCount, Is.EqualTo(0));
                Assert.That(snapshot.ActiveProbeCount, Is.EqualTo(0));
                Assert.That(snapshot.ScheduledProbeUpdates, Is.EqualTo(0));
                Assert.That(snapshot.WarmupState, Is.EqualTo(DdgiRuntimeWarmupState.Disabled));
                Assert.That(snapshot.WarmedVisibleProbeFraction, Is.EqualTo(0.0f));
                Assert.That(snapshot.WarmedLocalProbeFraction, Is.EqualTo(0.0f));
                Assert.That(snapshot.WarmedCascade0ProbeFraction, Is.EqualTo(0.0f));
                Assert.That(snapshot.SchedulerCandidateCount, Is.EqualTo(0));
                Assert.That(snapshot.SchedulerRequestCount, Is.EqualTo(0));
                Assert.That(snapshot.SchedulerBudgetRejectedCount, Is.EqualTo(0));
                Assert.That(snapshot.SchedulerGpuMicroseconds, Is.EqualTo(0));
                Assert.That(snapshot.SchedulerGpuP95Microseconds, Is.EqualTo(0));
                Assert.That(snapshot.EstimateSpatialCoverage, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateSupportCoverage, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateDataConfidence, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateVisibilityConfidence, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateLeakAttenuation, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateEffectiveWeight, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateOwnershipConsumed, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateRelocationMagnitude, Is.EqualTo(0.0f));
                Assert.That(snapshot.EstimateInactiveProbeCount, Is.EqualTo(0));
                Assert.That(snapshot.GatherFallbackTileCount, Is.EqualTo(0));
                Assert.That(snapshot.EmptyGatherTileCount, Is.EqualTo(0));
                Assert.That(snapshot.SelectedLocalTileCount, Is.EqualTo(0));
                Assert.That(snapshot.SelectedClipmapTileCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void DdgiDiagnosticWarningTracker_EmitsPersistentWarmupWarnings()
        {
            var tracker = new DdgiDiagnosticWarningTracker();
            var localWarmup = DdgiRuntimeSnapshot.Empty with
            {
                WarmupState = DdgiRuntimeWarmupState.LocalVolumeWarmup,
                WarmedLocalProbeFraction = 0.5f
            };

            for (int i = 0; i < 30; i++)
                Assert.That(tracker.Update(localWarmup, schedulerOverBudget: false), Is.Empty);

            IReadOnlyList<string> warnings = tracker.Update(localWarmup, schedulerOverBudget: false);

            Assert.That(warnings, Has.Some.Contains("local visible probe warmup"));
        }

        [Test]
        public void DdgiDiagnosticWarningTracker_EmitsPersistentVisibleWarmupWarnings()
        {
            var tracker = new DdgiDiagnosticWarningTracker();
            var visibleWarmup = DdgiRuntimeSnapshot.Empty with
            {
                WarmupState = DdgiRuntimeWarmupState.NearCascadeWarmup,
                WarmedVisibleProbeFraction = 0.35f,
                WarmedLocalProbeFraction = 0.85f,
                WarmedCascade0ProbeFraction = 0.45f
            };

            for (int i = 0; i < 30; i++)
                Assert.That(tracker.Update(visibleWarmup, schedulerOverBudget: false), Is.Empty);

            IReadOnlyList<string> warnings = tracker.Update(visibleWarmup, schedulerOverBudget: false);

            Assert.That(warnings, Has.Some.Contains("visible probe warmup"));
        }

        [Test]
        public void DdgiDiagnosticWarningTracker_EmitsPersistentCollapseWarningsAfterThreshold()
        {
            var tracker = new DdgiDiagnosticWarningTracker();
            var snapshot = DdgiRuntimeSnapshot.Empty with
            {
                VolumeCount = 5,
                ActiveProbeCount = 32648,
                ScheduledProbeUpdates = 144,
                SchedulerCandidateCount = 11502,
                SchedulerRequestCount = 66,
                SchedulerBudgetRejectedCount = 11436,
                EstimateSpatialCoverage = 1.0f,
                EstimateSupportCoverage = 0.0f,
                EstimateEffectiveWeight = 0.0f
            };

            for (int i = 0; i < DdgiDiagnosticWarningTracker.DefaultPersistenceFrames; i++)
                Assert.That(tracker.Update(snapshot, schedulerOverBudget: true), Is.Empty);

            var warnings = tracker.Update(snapshot, schedulerOverBudget: true);

            Assert.Multiple(() =>
            {
                Assert.That(warnings, Has.Some.Contains("support coverage"));
                Assert.That(warnings, Has.Some.Contains("effective contribution"));
                Assert.That(warnings, Has.Some.Contains("scheduler has remained over budget"));
                Assert.That(warnings, Has.Some.Contains("budget rejections"));
                Assert.That(warnings, Has.Some.Contains("scheduled update rate"));
            });
        }

        [Test]
        public void DdgiDiagnosticWarningTracker_ResetsWhenConditionClears()
        {
            var tracker = new DdgiDiagnosticWarningTracker();
            var failing = DdgiRuntimeSnapshot.Empty with
            {
                EstimateSpatialCoverage = 1.0f,
                EstimateSupportCoverage = 0.0f
            };
            var passing = failing with
            {
                EstimateSupportCoverage = 0.25f,
                EstimateEffectiveWeight = 0.25f
            };

            for (int i = 0; i < DdgiDiagnosticWarningTracker.DefaultPersistenceFrames; i++)
                _ = tracker.Update(failing, schedulerOverBudget: false);

            Assert.That(tracker.Update(passing, schedulerOverBudget: false), Is.Empty);
        }
    }
}
