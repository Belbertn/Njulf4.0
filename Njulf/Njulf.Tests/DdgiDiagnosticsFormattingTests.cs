using System.IO;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiDiagnosticsFormattingTests
    {
        [Test]
        public void SampleDiagnosticsReporter_UsesExplicitDdgiEstimateNames()
        {
            string reporter = File.ReadAllText(Path.Combine("..", "..", "..", "..", "NjulfHelloGame", "SampleDiagnosticsReporter.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(reporter, Does.Contain("ddgiEstimate spatial/support/data/visibility/leak/effective/rawLum/finalLum/ownership/reloc/inactive"));
                Assert.That(reporter, Does.Contain("DdgiForwardEstimateRawDiffuseLuminance:F5"));
                Assert.That(reporter, Does.Contain("DdgiForwardEstimateFinalDiffuseLuminance:F5"));
                Assert.That(reporter, Does.Contain("ddgiTrace samples/hit/miss/ray/direct/emissive/stable/sky/zeroDirect/directHit"));
                Assert.That(reporter, Does.Contain("DdgiTraceEnergyRayLuminanceAverage:F5"));
                Assert.That(reporter, Does.Contain("DdgiTraceEnergyDirectLuminanceAverage:F5"));
                Assert.That(reporter, Does.Contain("ddgiBlend samples/irrLum/conf/lowConf/nonzero"));
                Assert.That(reporter, Does.Contain("DdgiBlendEnergyIrradianceLuminanceAverage:F5"));
                Assert.That(reporter, Does.Contain("DdgiBlendEnergyConfidenceAverage:F3"));
                Assert.That(reporter, Does.Contain("ddgiClipmapCoverage attempts/ok/fail/avgEdgeFade/avgBlend"));
                Assert.That(reporter, Does.Contain("ddgiDispatchCapacity"));
                Assert.That(reporter, Does.Contain("ddgiActualRequests"));
                Assert.That(reporter, Does.Contain("readback={FormatReadbackStatus(diagnostics)}"));
                Assert.That(reporter, Does.Not.Contain("ddgiEstimate coverage/visible/effective"));
            });
        }

        [Test]
        public void SampleDiagnosticsReporter_DefinesDdgiOnlyRuntimeFilter()
        {
            string reporter = File.ReadAllText(Path.Combine("..", "..", "..", "..", "NjulfHelloGame", "SampleDiagnosticsReporter.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(reporter, Does.Contain("internal enum SampleDiagnosticsFilter"));
                Assert.That(reporter, Does.Contain("FullFrame"));
                Assert.That(reporter, Does.Contain("DdgiOnly"));
                Assert.That(reporter, Does.Contain("private SampleDiagnosticsFilter _filter = SampleDiagnosticsFilter.FullFrame;"));
                Assert.That(reporter, Does.Contain("public SampleDiagnosticsFilter Filter => _filter;"));
                Assert.That(reporter, Does.Contain("public SampleDiagnosticsFilter ToggleDdgiFilter()"));
                Assert.That(reporter, Does.Contain("public void SetFilter(SampleDiagnosticsFilter filter)"));
                Assert.That(reporter, Does.Contain("Console.WriteLine($\"Diagnostics filter: {_filter}\")"));
            });
        }

        [Test]
        public void SampleDiagnosticsReporter_DdgiOnlyFilterPrintsCompactTriageAndDdgiLines()
        {
            string reporter = File.ReadAllText(Path.Combine("..", "..", "..", "..", "NjulfHelloGame", "SampleDiagnosticsReporter.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(reporter, Does.Contain("if (_filter == SampleDiagnosticsFilter.DdgiOnly)"));
                Assert.That(reporter, Does.Contain("if (_diagnosticFrameCounter % 30 != 0)"));
                Assert.That(reporter, Does.Contain("PrintDdgiTriageDiagnostics(diagnostics);"));
                Assert.That(reporter, Does.Contain("PrintGiDiagnostics(diagnostics);"));
                Assert.That(reporter, Does.Contain("PrintDdgiSchedulerDiagnostics(diagnostics);"));
                Assert.That(reporter, Does.Contain("PrintDdgiUpdateDiagnostics(diagnostics);"));
                Assert.That(reporter, Does.Contain("DDGI TRIAGE: state={state} severity={severity}"));
                Assert.That(reporter, Does.Contain("DDGI TRIAGE VALUES: volumes={diagnostics.DdgiProbeVolumeCount}"));
                Assert.That(reporter, Does.Contain("trace={diagnostics.DdgiTraceEnergySampleCount}/{diagnostics.DdgiTraceEnergyHitCount}/{diagnostics.DdgiTraceEnergyMissCount}/{diagnostics.DdgiTraceEnergyRayLuminanceAverage:F5}/{diagnostics.DdgiTraceEnergyDirectLuminanceAverage:F5}"));
                Assert.That(reporter, Does.Contain("blend={diagnostics.DdgiBlendEnergySampleCount}/{diagnostics.DdgiBlendEnergyIrradianceLuminanceAverage:F5}/{diagnostics.DdgiBlendEnergyConfidenceAverage:F3}"));
                Assert.That(reporter, Does.Contain("support/data/effective={diagnostics.DdgiAverageSupportCoverageEstimate:F3}/{diagnostics.DdgiAverageDataConfidenceEstimate:F3}/{diagnostics.DdgiAverageEffectiveContributionEstimate:F3}"));
            });
        }

        [Test]
        public void SampleDiagnosticsReporter_DdgiClassifierNamesKnownFailureStates()
        {
            string reporter = File.ReadAllText(Path.Combine("..", "..", "..", "..", "NjulfHelloGame", "SampleDiagnosticsReporter.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(reporter, Does.Contain("private static string ClassifyDdgiState(RendererDiagnostics d)"));
                Assert.That(reporter, Does.Contain("Disabled"));
                Assert.That(reporter, Does.Contain("RayQueryInactive"));
                Assert.That(reporter, Does.Contain("NoVolumesOrProbes"));
                Assert.That(reporter, Does.Contain("NoProbeUpdates"));
                Assert.That(reporter, Does.Contain("FastGatherBlackHole"));
                Assert.That(reporter, Does.Contain("ProbeQualityZero"));
                Assert.That(reporter, Does.Contain("ClassificationOrActiveStateSuppressed"));
                Assert.That(reporter, Does.Contain("SpatialCoverageWithoutSupport"));
                Assert.That(reporter, Does.Contain("Contributing"));
                Assert.That(reporter, Does.Contain("UnknownZeroContribution"));
            });
        }

        [Test]
        public void DdgiDiagnosticsDocumentation_IncludesTroubleshootingMatrix()
        {
            string docs = File.ReadAllText(Path.Combine("..", "..", "..", "..", "docs", "rendering", "ddgi-diagnostics.md"));

            Assert.Multiple(() =>
            {
                Assert.That(docs, Does.Contain("spatial` high, `support` low"));
                Assert.That(docs, Does.Contain("rawLum` high, `finalLum` low"));
                Assert.That(docs, Does.Contain("ownership` must also be `0.000`"));
            });
        }
    }
}
