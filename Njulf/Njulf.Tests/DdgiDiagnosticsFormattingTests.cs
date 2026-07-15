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
            });
        }

        [Test]
        public void DdgiTriageDescriptions_MapClassifierStatesToSeverityAndNextStep()
        {
            (string Severity, string Reason, string Next) noUpdates = Describe("NoProbeUpdates");
            (string Severity, string Reason, string Next) contributing = Describe("Contributing");

            Assert.Multiple(() =>
            {
                Assert.That(noUpdates.Severity, Is.EqualTo("Red"));
                Assert.That(noUpdates.Next, Does.Contain("scheduler"));
                Assert.That(contributing.Severity, Is.EqualTo("Green"));
                Assert.That(contributing.Reason, Does.Contain("measurable"));
            });
        }

        [Test]
        public void DdgiRingDiagnostics_FormatsGridReachAndAgeP95PerCameraRelativeRing()
        {
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                DdgiVolumes =
                [
                    CreateRing(0, 28, 14, 28, 1.25f, 11.0f),
                    CreateRing(1, 18, 10, 18, 3.75f, 17.0f),
                    CreateRing(2, 12, 8, 12, 11.25f, 29.0f)
                ]
            };

            string formatted = (string)InvokeReporterMethod("FormatDdgiRingDiagnostics", diagnostics)!;

            Assert.That(
                formatted,
                Is.EqualTo(
                    "ring0 grid=28x14x28 spacing=1.25 reach=±16.9/±8.1m ageP95=11; " +
                    "ring1 grid=18x10x18 spacing=3.75 reach=±31.9/±16.9m ageP95=17; " +
                    "ring2 grid=12x8x12 spacing=11.25 reach=±61.9/±39.4m ageP95=29"));
        }

        [Test]
        public void DdgiVolumeActivityDiagnostics_FormatsProbeStateAndGpuGatherCountsPerVolume()
        {
            DdgiVolumeDiagnosticsEntry measuredRing = CreateRing(0, 8, 4, 8, 1.0f, 3.0f) with
            {
                VolumeIndex = 1,
                ProbeStateCountsValid = 1,
                ActiveProbeCount = 220,
                InactiveProbeCount = 36,
                GatherCountersReadbackValid = 1,
                PrimaryGatherCount = 1200,
                SampledGatherCount = 1800
            };
            DdgiVolumeDiagnosticsEntry pendingAuthored = measuredRing with
            {
                VolumeIndex = 0,
                Kind = DdgiProbeVolumeKind.Authored,
                DesignPreset = "simple-authored",
                ProbeStateCountsValid = 0,
                ActiveProbeCount = 512,
                InactiveProbeCount = 0,
                GatherCountersReadbackValid = 0,
                PrimaryGatherCount = 0,
                SampledGatherCount = 0
            };
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                DdgiVolumes = [pendingAuthored, measuredRing]
            };

            string formatted = (string)InvokeReporterMethod(
                "FormatDdgiVolumeActivityDiagnostics",
                diagnostics)!;

            Assert.That(
                formatted,
                Is.EqualTo(
                    "v0 authored active/inactive=512/0 state=pending gather primary/sampled=pending; " +
                    "v1 ring0 active/inactive=220/36 state=measured gather primary/sampled=1200/1800"));
        }

        private static DdgiVolumeDiagnosticsEntry CreateRing(
            int ringIndex,
            int countX,
            int countY,
            int countZ,
            float spacing,
            float ageP95)
        {
            return new DdgiVolumeDiagnosticsEntry(
                VolumeIndex: ringIndex,
                Kind: DdgiProbeVolumeKind.CameraClipmap,
                CascadeIndex: ringIndex,
                FirstProbeIndex: 0,
                ProbeCount: countX * countY * countZ,
                RaysPerProbe: 64,
                MaxProbeUpdatesPerFrame: 512,
                ScheduledProbeUpdates: 0,
                ScheduledPrimaryRayCount: 0,
                MaxRayDistance: 64.0f)
            {
                SizeX = (countX - 1) * spacing,
                SizeY = (countY - 1) * spacing,
                SizeZ = (countZ - 1) * spacing,
                ProbeSpacingX = spacing,
                ProbeSpacingY = spacing,
                ProbeSpacingZ = spacing,
                EstimatedAgeP95Frames = ageP95,
                DesignPreset = "simple-ring"
            };
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
