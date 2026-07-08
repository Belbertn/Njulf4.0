using System;
using System.Reflection;
using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiDiagnosticsFormattingTests
    {
        [Test]
        public void DdgiClassifier_ReportsActionableZeroContributionStates()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Classify(RendererDiagnostics.Empty), Is.EqualTo("Disabled"));
                Assert.That(Classify(RendererDiagnostics.Empty with
                {
                    GlobalIlluminationEnabled = 1,
                    GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                    GlobalIlluminationRayQueryActive = 0
                }), Is.EqualTo("RayQueryInactive"));
                Assert.That(Classify(ActiveDdgi() with
                {
                    DdgiProbeVolumeCount = 0,
                    DdgiActiveProbeCount = 0
                }), Is.EqualTo("NoVolumesOrProbes"));
                Assert.That(Classify(ActiveDdgi() with
                {
                    DdgiUpdateExecuted = 0,
                    DdgiProbesUpdated = 0
                }), Is.EqualTo("NoProbeUpdates"));
                Assert.That(Classify(ActiveDdgi() with
                {
                    DdgiAverageSpatialCoverageEstimate = 0.9f,
                    DdgiAverageSupportCoverageEstimate = 0.0f,
                    DdgiAverageEffectiveContributionEstimate = 0.0f
                }), Is.EqualTo("SpatialCoverageWithoutSupport"));
                Assert.That(Classify(ActiveDdgi() with
                {
                    DdgiAverageEffectiveContributionEstimate = 0.25f,
                    DdgiForwardEstimateFinalDiffuseLuminance = 0.1f
                }), Is.EqualTo("Contributing"));
                Assert.That(Classify(ActiveDdgi() with
                {
                    DdgiCacheGeneration = 12,
                    DdgiGatherSelectedClipmapTileFraction = 1.0f,
                    DdgiGatherFallbackTileFraction = 0.0f,
                    DdgiForwardEstimateSampleCount = 0,
                    DdgiFastGatherAttemptCount = 0,
                    DdgiBlendEnergySampleCount = 64
                }), Is.EqualTo("ForwardEstimateCountersPending"));
            });
        }

        [Test]
        public void DdgiTriageDescriptions_MapClassifierStatesToSeverityAndNextStep()
        {
            (string Severity, string Reason, string Next) noUpdates = Describe("NoProbeUpdates");
            (string Severity, string Reason, string Next) contributing = Describe("Contributing");
            (string Severity, string Reason, string Next) pending = Describe("ForwardEstimateCountersPending");

            Assert.Multiple(() =>
            {
                Assert.That(noUpdates.Severity, Is.EqualTo("Red"));
                Assert.That(noUpdates.Next, Does.Contain("scheduler"));
                Assert.That(contributing.Severity, Is.EqualTo("Green"));
                Assert.That(contributing.Reason, Does.Contain("measurable"));
                Assert.That(pending.Severity, Is.EqualTo("Amber"));
                Assert.That(pending.Reason, Does.Contain("forward estimate counters"));
            });
        }

        [Test]
        public void SdfClassifier_TreatsSmallTraceFailureTailAsSteady()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ClassifySdf(RendererDiagnostics.Empty with
                {
                    DdgiSdfTraceCount = 8576,
                    DdgiSdfStepExhaustedCount = 117,
                    DdgiSdfInsideStartCount = 0,
                    DdgiSurfaceCacheFallbackPercent = 0.0f
                }), Is.EqualTo("SteadyState"));
                Assert.That(ClassifySdf(RendererDiagnostics.Empty with
                {
                    DdgiSdfTraceCount = 8000,
                    DdgiSdfStepExhaustedCount = 112,
                    DdgiSdfInsideStartCount = 24,
                    DdgiSurfaceCacheFallbackPercent = 0.0f
                }), Is.EqualTo("SteadyState"));
            });
        }

        [Test]
        public void SdfClassifier_DegradesOnElevatedTraceFailures()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ClassifySdf(RendererDiagnostics.Empty with
                {
                    DdgiSdfTraceCount = 8000,
                    DdgiSdfStepExhaustedCount = 200
                }), Is.EqualTo("Degraded"));
                Assert.That(ClassifySdf(RendererDiagnostics.Empty with
                {
                    DdgiSdfTraceCount = 8000,
                    DdgiSdfInsideStartCount = 200
                }), Is.EqualTo("Degraded"));
                Assert.That(ClassifySdf(RendererDiagnostics.Empty with
                {
                    DdgiSdfTraceCount = 8000,
                    DdgiSurfaceCacheFallbackPercent = 10.1f
                }), Is.EqualTo("Degraded"));
            });
        }

        private static RendererDiagnostics ActiveDdgi()
        {
            return RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                GlobalIlluminationRayQueryActive = 1,
                DdgiProbeVolumeCount = 3,
                DdgiActiveProbeCount = 128,
                DdgiUpdateExecuted = 1,
                DdgiProbesUpdated = 16
            };
        }

        private static string Classify(RendererDiagnostics diagnostics)
        {
            return (string)InvokeReporterMethod("ClassifyDdgiState", diagnostics)!;
        }

        private static string ClassifySdf(RendererDiagnostics diagnostics)
        {
            return (string)InvokeReporterMethod("ClassifySdfState", diagnostics)!;
        }

        private static (string Severity, string Reason, string Next) Describe(string state)
        {
            object result = InvokeReporterMethod("DescribeDdgiTriageState", state)!;
            Type resultType = result.GetType();
            return (
                (string)resultType.GetField("Item1")!.GetValue(result)!,
                (string)resultType.GetField("Item2")!.GetValue(result)!,
                (string)resultType.GetField("Item3")!.GetValue(result)!);
        }

        private static object? InvokeReporterMethod(string name, params object[] args)
        {
            Type type = typeof(SampleBenchmarkOptions).Assembly.GetType(
                "NjulfHelloGame.SampleDiagnosticsReporter",
                throwOnError: true)!;
            MethodInfo method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(type.FullName, name);

            return method.Invoke(null, args);
        }
    }
}
