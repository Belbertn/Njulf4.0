using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleHealthReportEvaluationTests
{
    [Test]
    public void Evaluate_SeparatesWarningsFromErrorsAndPreservesFirstError()
    {
        GiDiagnosticWarning warning = CreateDiagnostic(
            GiDiagnosticWarningCode.PagedFarFieldInactive,
            GiDiagnosticSeverity.Warning,
            "paged-far-field");
        GiDiagnosticWarning firstError = CreateDiagnostic(
            GiDiagnosticWarningCode.GiBudgetOverrun,
            GiDiagnosticSeverity.Error,
            "ddgi-storage");
        GiDiagnosticWarning secondError = CreateDiagnostic(
            GiDiagnosticWarningCode.GiBudgetOverrun,
            GiDiagnosticSeverity.Error,
            "simple-ddgi-scheduler-requests");
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiWarnings = [warning, firstError, secondError]
        };

        SampleHealthReportEvaluation evaluation =
            SampleHealthReportEvaluation.Evaluate(diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.GiDiagnosticWarningCount, Is.EqualTo(1));
            Assert.That(evaluation.GiDiagnosticErrorCount, Is.EqualTo(2));
            Assert.That(evaluation.FirstGiDiagnosticError, Is.SameAs(firstError));
        });
    }

    [Test]
    public void Evaluate_IgnoresInformationalDiagnosticsForGateCounts()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiWarnings =
            [
                CreateDiagnostic(
                    GiDiagnosticWarningCode.InvestigationCountersUnavailable,
                    GiDiagnosticSeverity.Info,
                    "detailed-gi-counters")
            ]
        };

        SampleHealthReportEvaluation evaluation =
            SampleHealthReportEvaluation.Evaluate(diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.GiDiagnosticWarningCount, Is.Zero);
            Assert.That(evaluation.GiDiagnosticErrorCount, Is.Zero);
            Assert.That(evaluation.FirstGiDiagnosticError, Is.Null);
        });
    }

    [Test]
    public void FindFirstFailedOperation_IgnoresPassedAndUnsupportedEvidence()
    {
        var failure = new SampleSmokeOperationResult(
            "missing-assets",
            "failed",
            2,
            "unexpected exception");

        SampleSmokeOperationResult? actual =
            SampleHealthReportEvaluation.FindFirstFailedOperation(
            [
                new SampleSmokeOperationResult("resize", "passed", 1, null),
                new SampleSmokeOperationResult(
                    "device-loss-recovery",
                    "rejected-unsupported",
                    1,
                    "unsupported"),
                failure
            ]);

        Assert.That(actual, Is.SameAs(failure));
    }

    [TestCase("resize", "pending")]
    [TestCase("resize", "skipped")]
    [TestCase("quality-switch", "rejected-unsupported")]
    [TestCase("device-loss-recovery", "unknown")]
    public void FindFirstFailedOperation_RejectsUnexpectedTerminalStatus(
        string name,
        string status)
    {
        SampleSmokeOperationResult? actual =
            SampleHealthReportEvaluation.FindFirstFailedOperation(
            [
                new SampleSmokeOperationResult(name, status, 4, null)
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual!.Status, Is.EqualTo("failed"));
            Assert.That(actual.Detail, Does.Contain("unexpected non-terminal status"));
        });
    }

    [Test]
    public void FindIncompleteSmokeOperation_RejectsEarlyStartupShutdown()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--smoke-mode", "startup",
            "--smoke-frames", "3"
        ]);

        SampleSmokeOperationResult? actual =
            SampleHealthReportEvaluation.FindIncompleteSmokeOperation(
                options,
                Array.Empty<SampleSmokeOperationResult>(),
                renderedFrameCount: 2);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual!.Status, Is.EqualTo("failed"));
            Assert.That(actual.Detail, Does.Contain("2/3 required rendered frames"));
        });
    }

    [Test]
    public void FindIncompleteSmokeOperation_RejectsMissingLifecycleEvidence()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--smoke-mode", "all",
            "--scene-reloads", "2"
        ]);
        SampleSmokeOperationResult[] operations =
        [
            Passed("fullscreen"),
            Passed("resize"),
            Passed("resize"),
            Passed("resize"),
            Passed("minimize-zero-framebuffer"),
            Passed("restore-framebuffer"),
            Passed("scene-reload")
        ];

        SampleSmokeOperationResult? actual =
            SampleHealthReportEvaluation.FindIncompleteSmokeOperation(
                options,
                operations,
                options.FrameCount);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual!.Status, Is.EqualTo("failed"));
            Assert.That(actual.Detail, Does.Contain("'scene-reload'"));
            Assert.That(actual.Detail, Does.Contain("1/2"));
        });
    }

    [TestCase("quality-switch", "quality-switch")]
    [TestCase("ddgi-residency-switch", "ddgi-residency-switch")]
    [TestCase("texture-hot-reload", "texture-hot-reload")]
    public void FindIncompleteSmokeOperation_RejectsMissingRollbackRunnerResult(
        string mode,
        string expectedOperation)
    {
        SampleSmokeOptions options =
            SampleSmokeOptionsParser.Parse(["--smoke-mode", mode]);

        SampleSmokeOperationResult? actual =
            SampleHealthReportEvaluation.FindIncompleteSmokeOperation(
                options,
                Array.Empty<SampleSmokeOperationResult>(),
                options.FrameCount);

        Assert.That(actual!.Detail, Does.Contain(expectedOperation));
    }

    [TestCase("quality-switch", "quality-switch")]
    [TestCase("ddgi-residency-switch", "ddgi-residency-switch")]
    [TestCase("texture-hot-reload", "texture-hot-reload")]
    public void FindIncompleteSmokeOperation_AcceptsSpecializedTerminalEvidence(
        string mode,
        string operation)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--smoke-mode", mode,
            "--smoke-frames", "120"
        ]);

        SampleSmokeOperationResult? actual =
            SampleHealthReportEvaluation.FindIncompleteSmokeOperation(
                options,
                [Passed(operation)],
                renderedFrameCount: 1);

        Assert.That(actual, Is.Null);
    }

    [Test]
    public void FindIncompleteSmokeOperation_AcceptsCompleteResizeEvidence()
    {
        SampleSmokeOptions options =
            SampleSmokeOptionsParser.Parse(["--smoke-mode", "resize"]);

        SampleSmokeOperationResult? actual =
            SampleHealthReportEvaluation.FindIncompleteSmokeOperation(
                options,
                [Passed("resize"), Passed("resize"), Passed("resize")],
                options.FrameCount);

        Assert.That(actual, Is.Null);
    }

    [Test]
    public void RequiredHealthReport_WriteFailureIsObservable()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"health-report-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
            [
                "--health-report",
                directory
            ]);
            var writer = new SampleHealthReportWriter();

            Assert.That(
                () => writer.Write(
                    options,
                    startupLogPath: null,
                    Array.Empty<SampleSmokeOperationResult>(),
                    RendererDiagnostics.Empty,
                    "passed",
                    failure: null),
                Throws.Exception);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Test]
    public void RendererHealthReportWriter_AtomicallyReplacesAndLeavesNoTemporaryFile()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"health-report-atomic-{Guid.NewGuid():N}");
        string reportPath = Path.Combine(directory, "health.json");
        Directory.CreateDirectory(directory);
        try
        {
            var writer = new RendererHealthReportWriter();
            writer.Write(reportPath, new { status = "first" });
            writer.Write(reportPath, new { status = "passed", revision = 2 });

            using JsonDocument document =
                JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Multiple(() =>
            {
                Assert.That(
                    document.RootElement.GetProperty("status").GetString(),
                    Is.EqualTo("passed"));
                Assert.That(
                    document.RootElement.GetProperty("revision").GetInt32(),
                    Is.EqualTo(2));
                Assert.That(
                    Directory.EnumerateFiles(directory, "*.tmp").ToArray(),
                    Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GiDiagnosticWarning CreateDiagnostic(
        GiDiagnosticWarningCode code,
        GiDiagnosticSeverity severity,
        string feature) =>
        new(
            code,
            severity,
            "diagnostic",
            feature,
            1,
            0,
            "count",
            GiMetricFreshness.CurrentFrame,
            "test",
            "test",
            "test",
            1,
            1,
            "inspect");

    private static SampleSmokeOperationResult Passed(string name) =>
        new(name, "passed", 1, null);
}
