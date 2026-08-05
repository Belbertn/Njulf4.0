using System;
using System.IO;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiResidencyCoverageValidatorTests
{
    [Test]
    public void DistinguishesFalseNegativesFromDemandInflation()
    {
        var validator = new SimpleDdgiResidencyCoverageValidator(16, 4);
        SimpleDdgiResidencyCoverageFrame frame = validator.ObserveFrame(
            predictedPages: new[] { 1, 2, 3 },
            actualPages: new[] { 2, 3, 4, 5 });

        Assert.Multiple(() =>
        {
            Assert.That(frame.PredictedPageCount, Is.EqualTo(3));
            Assert.That(frame.ActualPageCount, Is.EqualTo(4));
            Assert.That(frame.FalseNegativePageCount, Is.EqualTo(2));
            Assert.That(frame.FalsePositivePageCount, Is.EqualTo(1));
            Assert.That(frame.FalseNegativeRate, Is.EqualTo(0.5));
            Assert.That(frame.InflationRatio, Is.EqualTo(0.75));
        });
    }

    [Test]
    public void QualificationSummaryUsesPredeclaredP95Gates()
    {
        var validator = new SimpleDdgiResidencyCoverageValidator(8, 4);
        validator.ObserveFrame(new[] { 0, 1 }, new[] { 0, 1 });
        validator.ObserveFrame(new[] { 2, 3 }, new[] { 2, 3 });
        validator.ObserveFrame(new[] { 4 }, new[] { 4 });

        SimpleDdgiResidencyCoverageSummary summary = validator.GetSummary();

        Assert.Multiple(() =>
        {
            Assert.That(summary.FrameCount, Is.EqualTo(3));
            Assert.That(summary.FalseNegativeRateP95, Is.Zero);
            Assert.That(summary.InflationRatioP95, Is.EqualTo(1.0));
            Assert.That(summary.MeetsInitialQualificationGate, Is.True);
        });
    }

    [Test]
    public void WorkingSetReportExportsRetentionCapacityGeometryCoverageAndMemory()
    {
        var analyzer = new SimpleDdgiResidencyWorkingSetAnalyzer(
            virtualPageCount: 8,
            alternativeVirtualPageCount: 4,
            retentionIntervals: new[] { 1, 2 },
            physicalPageCapacities: new[] { 2, 4 },
            maximumRecordedFrames: 4);
        analyzer.ObserveFrame(
            predictedPages: new[] { 0, 1 },
            actualPages: new[] { 0, 1 },
            alternativeDemandPages: new[] { 0 });
        analyzer.ObserveFrame(
            predictedPages: new[] { 2 },
            actualPages: new[] { 2, 3 },
            alternativeDemandPages: new[] { 1 });
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probeCount: 64,
            updateRequestCapacity: 8,
            rayCapacity: 8,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            residencyMode: Njulf.Rendering.Data.SimpleDdgiProbeResidencyMode.SparseNearRing,
            densePayloadProbeCount: 32,
            sparseVirtualProbeCount: 32,
            sparseVirtualPageCount: 4,
            sparsePhysicalPageCapacity: 2,
            maximumPageAdmissionsPerFrame: 2);

        SimpleDdgiResidencyWorkingSetReport report = analyzer.CreateReport(
        [
            new SimpleDdgiResidencyMemoryCandidate(
                "candidate-2",
                RetentionFrames: 2,
                PhysicalPageCapacity: 2,
                SparsePlan: plan,
                DenseEquivalentLiveBytes: checked(plan.LiveBytes + 1_024UL))
        ]);

        SimpleDdgiResidencyRetentionSummary retention2 =
            report.RetentionSummaries[1];
        SimpleDdgiResidencyCapacitySimulation constrained =
            report.CapacitySimulations[2];
        Assert.Multiple(() =>
        {
            Assert.That(report.FrameCount, Is.EqualTo(2));
            Assert.That(report.Frames[1].CurrentDemandPageCount, Is.EqualTo(2));
            Assert.That(report.Frames[1].FalseNegativePageCount, Is.EqualTo(1));
            Assert.That(retention2.RetentionFrames, Is.EqualTo(2));
            Assert.That(retention2.RequiredPoolMaximum, Is.EqualTo(4));
            Assert.That(report.PageGeometries[0].Name, Is.EqualTo("2x2x2"));
            Assert.That(report.PageGeometries[1].Name, Is.EqualTo("4x2x4"));
            Assert.That(constrained.RetentionFrames, Is.EqualTo(2));
            Assert.That(constrained.PhysicalPageCapacity, Is.EqualTo(2));
            Assert.That(constrained.PressureFrameCount, Is.EqualTo(1));
            Assert.That(constrained.FailedAdmissionCount, Is.EqualTo(2));
            Assert.That(report.MemoryProjections[0].AvoidedBytes, Is.EqualTo(1_024UL));
            Assert.That(report.Coverage.MeetsInitialQualificationGate, Is.False);
        });
    }

    [Test]
    public void WorkingSetReportWritesAtomicallyAsJson()
    {
        var analyzer = new SimpleDdgiResidencyWorkingSetAnalyzer(
            4,
            0,
            new[] { 1 },
            new[] { 4 },
            maximumRecordedFrames: 1);
        analyzer.ObserveFrame(new[] { 0 }, new[] { 0 });
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"simple-ddgi-residency-working-set-{Guid.NewGuid():N}.json");
        try
        {
            SimpleDdgiResidencyWorkingSetAnalyzer.WriteJson(
                path,
                analyzer.CreateReport());

            string json = File.ReadAllText(path);
            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("\"FrameCount\": 1"));
                Assert.That(json, Does.Contain("\"2x2x2\""));
                Assert.That(Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileName(path) + ".*.tmp"), Is.Empty);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
