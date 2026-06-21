using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderBudgetEvaluatorTests
    {
        [Test]
        public void RenderBudgetEvaluator_WithinWarningAndOverBudgetThresholds()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderBudgetEvaluator.Classify(84, 100), Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(RenderBudgetEvaluator.Classify(86, 100), Is.EqualTo(RenderBudgetStatus.Warning));
                Assert.That(RenderBudgetEvaluator.Classify(101, 100), Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_UsesActiveProfile()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.LowSpec1080p30;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                CpuTotalDrawSceneMicroseconds = 11_000,
                GpuFrameMicroseconds = 1,
                GpuTimingValid = 1
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(snapshot.OverallStatus, Is.EqualTo(RenderBudgetStatus.OverBudget));
            Assert.That(snapshot.Profile.Kind, Is.EqualTo(RenderBudgetProfileKind.LowSpec1080p30));
        }

        [Test]
        public void RenderBudgetEvaluator_UnavailableMetricsDoNotFailBudget()
        {
            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                RenderBudgetProfile.Development,
                RendererDiagnostics.Empty,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, RenderBudgetProfile.Development.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(snapshot.OverallStatus, Is.Not.EqualTo(RenderBudgetStatus.OverBudget));
        }

        [Test]
        public void RenderBudgetEvaluator_IncludesFoliageSpecificBudgets()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.LowSpec1080p30;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GpuFrameMicroseconds = 1,
                GpuTimingValid = 1,
                FoliageVisibleClusterCount = profile.FoliageClusterBudget + 1,
                FoliageVisibleMeshletDrawCount = profile.FoliageMeshletDrawBudget + 1,
                FoliageGrassBladeEstimate = profile.FoliageGrassBladeBudget + 1,
                FoliageInstanceBufferBytes = profile.FoliageMemoryBudgetBytes + 1
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "Foliage clusters").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Foliage meshlet draws").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Foliage grass blades").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Foliage memory").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_IncludesGlobalIlluminationBudgets()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.LowSpec1080p30;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Hybrid,
                GpuTimingValid = 1,
                GpuSsgiTraceMicroseconds = 200,
                GpuSsgiTemporalMicroseconds = 100,
                GlobalIlluminationRenderTargetBytes = 2,
                DdgiProbeCount = 2
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "GI GPU").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "GI memory").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "DDGI probes").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        private static BudgetMetric Metric(RenderBudgetSnapshot snapshot, string name)
        {
            return snapshot.Metrics.Single(metric => metric.Name == name);
        }
    }
}
