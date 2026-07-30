using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialGiTestMatrixBuilderTests
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "njulf-material-gi-trx",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [Test]
    public void ReadTrxResult_RequiresConcreteCompletedPassingResults()
    {
        string path = WriteTrx(
            "passed.trx",
            summaryOutcome: "Completed",
            declaredTotal: 2,
            declaredPassed: 2,
            "Passed",
            "Passed");

        MaterialGiTestMatrixProducerResult result =
            MaterialGiTestMatrixBuilder.ReadTrxResult(
                "GpuOracle",
                path);

        Assert.Multiple(() =>
        {
            Assert.That(
                result.Status,
                Is.EqualTo(
                    MaterialGiReleaseEvidenceContract.PassedStatus));
            Assert.That(result.PassedCount, Is.EqualTo(2));
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(result.SkippedCount, Is.Zero);
        });
    }

    [Test]
    public void ReadTrxResult_SkippedOrIncompleteRunFailsClosed()
    {
        string path = WriteTrx(
            "skipped.trx",
            summaryOutcome: "Aborted",
            declaredTotal: 2,
            declaredPassed: 1,
            "Passed",
            "NotExecuted");

        MaterialGiTestMatrixProducerResult result =
            MaterialGiTestMatrixBuilder.ReadTrxResult(
                "ReleaseTests",
                path);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("Failed"));
            Assert.That(result.PassedCount, Is.EqualTo(1));
            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ReadTrxResult_RejectsSummaryCountersThatDoNotMatchPayload()
    {
        string path = WriteTrx(
            "mismatched.trx",
            summaryOutcome: "Completed",
            declaredTotal: 3,
            declaredPassed: 2,
            "Passed",
            "Passed");

        Assert.That(
            () => MaterialGiTestMatrixBuilder.ReadTrxResult(
                "CpuOracle",
                path),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains(
                    "counters do not match"));
    }

    [Test]
    public void CreateReport_RequiresExactReleaseMatrixAndCanonicalIdentity()
    {
        MaterialGiTestMatrixProducerResult[] results =
        [
            MaterialGiTestMatrixBuilder.CreateAttestedBuildResult(
                "ReleaseBuild"),
            Passed("ReleaseTests"),
            Passed("CpuOracle"),
            Passed("GpuOracle")
        ];

        MaterialGiTestMatrixProducerReport report =
            MaterialGiTestMatrixBuilder.CreateReport(
                new string('A', 40),
                "sha256:" + new string('B', 64),
                new string('C', 64),
                new MaterialGiEvidenceDeviceIdentity
                {
                    DeviceId = "reference-device",
                    GpuName = "Reference GPU",
                    DriverVersion = "1.2.3"
                },
                results);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.Status,
                Is.EqualTo(
                    MaterialGiReleaseEvidenceContract.PassedStatus));
            Assert.That(report.BuildConfiguration, Is.EqualTo("Release"));
            Assert.That(report.BuildCommit, Is.EqualTo(new string('a', 40)));
            Assert.That(
                report.ShaderFingerprint,
                Is.EqualTo(new string('b', 64)));
            Assert.That(
                report.Results.Select(static result => result.Name),
                Is.EqualTo(
                    new[]
                    {
                        "CpuOracle",
                        "GpuOracle",
                        "ReleaseBuild",
                        "ReleaseTests"
                    }));
        });
    }

    [Test]
    public void CreateAttestedBuildResult_OnlyAllowsReleaseBuild()
    {
        Assert.That(
            () => MaterialGiTestMatrixBuilder
                .CreateAttestedBuildResult("GpuOracle"),
            Throws.ArgumentException.With.Message.Contains(
                "Only ReleaseBuild"));
    }

    private static MaterialGiTestMatrixProducerResult Passed(string name) =>
        new()
        {
            Name = name,
            Status = MaterialGiReleaseEvidenceContract.PassedStatus,
            PassedCount = 1
        };

    private string WriteTrx(
        string fileName,
        string summaryOutcome,
        int declaredTotal,
        int declaredPassed,
        params string[] outcomes)
    {
        string results = string.Join(
            Environment.NewLine,
            outcomes.Select(
                (outcome, index) =>
                    $"      <UnitTestResult executionId=\"{index}\" " +
                    $"testId=\"{index}\" testName=\"test-{index}\" " +
                    $"outcome=\"{outcome}\" />"));
        string xml =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
               <Results>
             {results}
               </Results>
               <ResultSummary outcome="{summaryOutcome}">
                 <Counters total="{declaredTotal}" executed="{outcomes.Length}" passed="{declaredPassed}" failed="0" />
               </ResultSummary>
             </TestRun>
             """;
        string path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllText(path, xml);
        return path;
    }
}
