using System.Security.Cryptography;
using System.Text.Json;
using Njulf.Assets.Validation;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class KhronosMaterialGiRenderedGateTests
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "NjulfKhronosRenderedGateTests",
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
    public void Parser_RequiresCompleteStandaloneOptionSetAndForcesValidation()
    {
        SampleKhronosMaterialGiRenderedGateOptions expected = CreateOptions();
        SampleSmokeOptions parsed = SampleSmokeOptionsParser.Parse(CreateArguments(expected));

        Assert.Multiple(() =>
        {
            Assert.That(parsed.KhronosMaterialGiRenderedGate, Is.EqualTo(expected));
            Assert.That(parsed.SceneKind, Is.EqualTo(SampleSceneKind.MaterialShowcase));
            Assert.That(parsed.Mode, Is.EqualTo(SampleSmokeMode.None));
            Assert.That(parsed.FrameCount, Is.Zero);
            Assert.That(parsed.ValidationMode, Is.EqualTo(RendererValidationMode.Standard));
            Assert.That(parsed.FailOnValidationMessage, Is.False);
            Assert.That(parsed.EnableGpuTiming, Is.True);
            Assert.That(parsed.AsyncComputeModeOverride, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(parsed.Enabled, Is.True);
        });

        string[] incomplete = CreateArguments(expected)[..^2];
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(incomplete),
            Throws.ArgumentException.With.Message.Contains("requires all five options"));
    }

    [Test]
    public void Parser_RejectsCompetingModesAndDisabledValidation()
    {
        SampleKhronosMaterialGiRenderedGateOptions options = CreateOptions();
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                [.. CreateArguments(options), "--smoke-frames", "2"]),
            Throws.ArgumentException.With.Message.Contains("owns its scene"));
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                [.. CreateArguments(options), "--material-gi-capture-dir", _temporaryDirectory]),
            Throws.ArgumentException.With.Message.Contains("cannot be combined"));
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
                [.. CreateArguments(options), "--validation", "off"]),
            Throws.ArgumentException.With.Message.Contains("validation cannot be off"));
    }

    [Test]
    public void Options_RejectOutputsInsideCookedInputRoot()
    {
        string cookedRoot = Path.Combine(_temporaryDirectory, "cooked", "win-x64");
        Assert.That(
            () => SampleKhronosMaterialGiRenderedGateOptions.Create(
                Path.Combine(_temporaryDirectory, "manifest.json"),
                Path.Combine(_temporaryDirectory, "semantic.json"),
                cookedRoot,
                Path.Combine(cookedRoot, "capture.pfm"),
                Path.Combine(_temporaryDirectory, "render.json")),
            Throws.ArgumentException.With.Message.Contains("inside the authenticated cooked root"));
    }

    [Test]
    public void Authentication_AcceptsExactPassedEvidence()
    {
        (string manifestPath, string reportPath, KhronosMaterialGiManifest manifest, _) =
            WriteAuthenticatedInputs();

        KhronosMaterialGiAuthenticatedGate authenticated =
            KhronosMaterialGiConformance.AuthenticatePassedGate(
                manifestPath,
                reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(authenticated.Manifest.Repository, Is.EqualTo(manifest.Repository));
            Assert.That(authenticated.Manifest.Commit, Is.EqualTo(manifest.Commit));
            Assert.That(
                authenticated.Manifest.Assets.Select(static asset => asset.Name),
                Is.EqualTo(manifest.Assets.Select(static asset => asset.Name)));
            Assert.That(authenticated.GateReport.Status, Is.EqualTo("Passed"));
            Assert.That(authenticated.ManifestSha256, Has.Length.EqualTo(64));
            Assert.That(authenticated.GateReportSha256, Has.Length.EqualTo(64));
            Assert.That(authenticated.GateReport.Entries, Has.Count.EqualTo(1));
        });
    }

    [TestCase("manifest-hash")]
    [TestCase("commit")]
    [TestCase("failed-entry")]
    [TestCase("missing-entry")]
    [TestCase("extra-entry")]
    public void Authentication_RejectsTamperMissingExtraAndFailure(string mutation)
    {
        (string manifestPath, string reportPath, _, KhronosMaterialGiGateReport report) =
            WriteAuthenticatedInputs();
        KhronosMaterialGiGateReport mutated = mutation switch
        {
            "manifest-hash" => report with { ManifestSha256 = new string('0', 64) },
            "commit" => report with { Commit = new string('c', 40) },
            "failed-entry" => report with
            {
                Entries =
                [
                    report.Entries[0] with
                    {
                        Status = "Failed",
                        Failure = "semantic failure"
                    }
                ]
            },
            "missing-entry" => report with
            {
                Entries = Array.Empty<KhronosMaterialGiGateEntry>()
            },
            "extra-entry" => report with
            {
                Entries =
                [
                    report.Entries[0],
                    report.Entries[0] with { Name = "UnexpectedAsset" }
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        WriteJson(reportPath, mutated);

        Assert.That(
            () => KhronosMaterialGiConformance.AuthenticatePassedGate(
                manifestPath,
                reportPath),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("not authenticated"));
    }

    [Test]
    public void Authentication_RejectsUnsafeManifestAssetName()
    {
        (string manifestPath, string reportPath, KhronosMaterialGiManifest manifest, _) =
            WriteAuthenticatedInputs();
        WriteJson(
            manifestPath,
            manifest with
            {
                Assets =
                [
                    manifest.Assets[0] with { Name = $"..{Path.DirectorySeparatorChar}UnlitTest" }
                ]
            });

        Assert.That(
            () => KhronosMaterialGiConformance.AuthenticatePassedGate(
                manifestPath,
                reportPath),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("manifest"));
    }

    [Test]
    public void LayoutAndPackageHash_AreDeterministicAndOrderLocked()
    {
        SampleKhronosMaterialGiLayoutItem[] items =
        [
            new(
                "UnlitTest",
                new BoundingBox(new Vector3(-1f, -2f, -0.5f), new Vector3(1f, 2f, 0.5f)),
                2),
            new(
                "EmissiveStrengthTest",
                new BoundingBox(new Vector3(-2f, 0f, -1f), new Vector3(2f, 2f, 1f)),
                6),
            new(
                "AlphaBlendModeTest",
                new BoundingBox(new Vector3(0f), new Vector3(1f, 1f, 1f)),
                6)
        ];
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        IReadOnlyList<SampleKhronosMaterialGiLayoutPlacement> first =
            SampleKhronosMaterialGiLayout.Create(items, commit);
        IReadOnlyList<SampleKhronosMaterialGiLayoutPlacement> second =
            SampleKhronosMaterialGiLayout.Create(items, commit);

        (string Name, string Sha256, long Bytes)[] packages =
        [
            ("UnlitTest", new string('1', 64), 100),
            ("EmissiveStrengthTest", new string('2', 64), 200),
            ("AlphaBlendModeTest", new string('3', 64), 300)
        ];
        string packageHash =
            SampleKhronosMaterialGiRenderedSceneBuilder.ComputePackageSetSha256(packages);
        string repeatedHash =
            SampleKhronosMaterialGiRenderedSceneBuilder.ComputePackageSetSha256(packages);
        string reorderedHash =
            SampleKhronosMaterialGiRenderedSceneBuilder.ComputePackageSetSha256(
                packages.Reverse());

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            for (int index = 0; index < first.Count; index++)
            {
                float transformedCenterX =
                    items[index].Bounds.Center.X * first[index].UniformScale +
                    first[index].Translation.X;
                float transformedMinimumY =
                    items[index].Bounds.Min.Y * first[index].UniformScale +
                    first[index].Translation.Y;
                Assert.That(
                    transformedCenterX,
                    Is.EqualTo((index - 1) * 3.2f).Within(1e-6f));
                Assert.That(
                    transformedMinimumY,
                    Is.EqualTo(SampleKhronosMaterialGiLayout.GroundHeight).Within(1e-6f));
                Assert.That(first[index].StableBaseId, Is.Not.EqualTo(Guid.Empty));
            }
            Assert.That(packageHash, Has.Length.EqualTo(64));
            Assert.That(repeatedHash, Is.EqualTo(packageHash));
            Assert.That(reorderedHash, Is.Not.EqualTo(packageHash));
        });
    }

    [Test]
    public void Sequence_HasExactWarmupAndFailsClosedOnReadbackTimeout()
    {
        var sequence = new SampleKhronosMaterialGiRenderedGateSequence();
        for (int frame = 0;
             frame < SampleKhronosMaterialGiRenderedGateSequence.WarmupFrameCount;
             frame++)
        {
            sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Unknown);
        }

        Assert.That(sequence.ShouldQueueCapture, Is.True);
        sequence.MarkCaptureQueued();
        for (int frame = 0;
             frame <= SampleKhronosMaterialGiRenderedGateSequence.ReadbackTimeoutFrameCount;
             frame++)
        {
            sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Submitted);
        }

        Assert.Multiple(() =>
        {
            Assert.That(sequence.IsFailed, Is.True);
            Assert.That(sequence.FailureReason, Does.Contain("timeout"));
            Assert.That(
                sequence.RenderedFrameCount,
                Is.EqualTo(
                    SampleKhronosMaterialGiRenderedGateSequence.WarmupFrameCount +
                    SampleKhronosMaterialGiRenderedGateSequence.ReadbackTimeoutFrameCount + 1));
        });
    }

    [Test]
    public void Sequence_CompletesOnlyAfterTerminalCapture()
    {
        var sequence = new SampleKhronosMaterialGiRenderedGateSequence();
        for (int frame = 0;
             frame < SampleKhronosMaterialGiRenderedGateSequence.WarmupFrameCount;
             frame++)
        {
            sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Unknown);
        }
        sequence.MarkCaptureQueued();
        sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Queued);
        sequence.AdvanceAfterRenderedFrame(LinearHdrCaptureState.Completed);

        Assert.Multiple(() =>
        {
            Assert.That(sequence.IsComplete, Is.True);
            Assert.That(sequence.IsFailed, Is.False);
            Assert.That(sequence.ReadbackFrameCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void CompletionGate_FailsZeroDrawValidationV2AndUnlitRegressions()
    {
        var invalid = new SampleKhronosMaterialGiCompletionSnapshot(
            RendererValidationMode.Off,
            ValidationWarningCount: 1,
            ValidationErrorCount: 1,
            SawDrawSubmission: false,
            RenderedFrameCount: 1,
            DiagnosticsMaterialGiV2Features: MaterialGiV2Feature.None,
            SettingsMaterialGiV2Features: MaterialGiV2Feature.MaterialTransport,
            AssetCount: 0,
            RenderObjectCount: 0,
            ExpectedUnlitRenderObjectCount: 2,
            RuntimeUnlitRenderObjectCount: 0,
            GpuDevice: "unknown-device",
            GpuDriver: "unknown-driver",
            CaptureWidth: 1,
            CaptureHeight: 1,
            CaptureMaximumComponent: 0f);

        IReadOnlyList<string> failures =
            SampleKhronosMaterialGiCompletionGate.Evaluate(invalid);

        Assert.Multiple(() =>
        {
            Assert.That(failures, Has.Some.Contains("Vulkan validation was not active"));
            Assert.That(failures, Has.Some.Contains("warning(s)"));
            Assert.That(failures, Has.Some.Contains("No non-empty draw submission"));
            Assert.That(failures, Has.Some.Contains("Material/GI V2"));
            Assert.That(failures, Has.Some.Contains("Unlit evidence"));
            Assert.That(failures, Has.Some.Contains("positive rendered signal"));
        });
    }

    [Test]
    public void CompletionGate_AcceptsCompleteEvidence()
    {
        var valid = new SampleKhronosMaterialGiCompletionSnapshot(
            RendererValidationMode.Standard,
            ValidationWarningCount: 0,
            ValidationErrorCount: 0,
            SawDrawSubmission: true,
            RenderedFrameCount:
                SampleKhronosMaterialGiRenderedGateSequence.WarmupFrameCount + 2,
            DiagnosticsMaterialGiV2Features: MaterialGiV2Feature.All,
            SettingsMaterialGiV2Features: MaterialGiV2Feature.All,
            AssetCount: 3,
            RenderObjectCount: 14,
            ExpectedUnlitRenderObjectCount: 2,
            RuntimeUnlitRenderObjectCount: 2,
            GpuDevice: "Production GPU",
            GpuDriver: "1.2.3",
            CaptureWidth: SampleKhronosMaterialGiRenderedGateRunner.LockedWidth,
            CaptureHeight: SampleKhronosMaterialGiRenderedGateRunner.LockedHeight,
            CaptureMaximumComponent: 2f);

        Assert.That(
            SampleKhronosMaterialGiCompletionGate.Evaluate(valid),
            Is.Empty);
    }

    [Test]
    public void ForwardDynamicRenderingContract_DeclaresOnlySceneAndOptionalProvenanceAttachments()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: false,
                    materialTransportProvenanceEnabled: true),
                Is.Zero);
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    materialTransportProvenanceEnabled: false),
                Is.EqualTo(1));
            Assert.That(
                ForwardDynamicRenderingContract.ResolveColorAttachmentCount(
                    hasColorAttachment: true,
                    materialTransportProvenanceEnabled: true),
                Is.EqualTo(2));
        });
    }

    [Test]
    public void AtomicFailureReport_ReplacesStaleFileAndLeavesNoTemporaryArtifacts()
    {
        SampleKhronosMaterialGiRenderedGateOptions options = CreateOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);
        File.WriteAllText(options.ReportPath, "stale");
        var report = new SampleKhronosMaterialGiRenderedGateReport
        {
            Status = "Failed",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Failure = "expected failure",
            Failures = ["expected failure"]
        };

        SampleKhronosMaterialGiRenderedGateReportPublisher.WriteAtomic(
            options.ReportPath,
            report);

        using JsonDocument published =
            JsonDocument.Parse(File.ReadAllBytes(options.ReportPath));
        string[] temporaryFiles = Directory.GetFiles(
            Path.GetDirectoryName(options.ReportPath)!,
            $".{Path.GetFileName(options.ReportPath)}.*.tmp");
        Assert.Multiple(() =>
        {
            Assert.That(
                published.RootElement.GetProperty("status").GetString(),
                Is.EqualTo("Failed"));
            Assert.That(
                published.RootElement.GetProperty("failure").GetString(),
                Is.EqualTo("expected failure"));
            Assert.That(temporaryFiles, Is.Empty);
        });
    }

    [Test]
    public void FailedPreflightReport_IsPublishedForMissingInputs()
    {
        SampleKhronosMaterialGiRenderedGateOptions options = CreateOptions();
        bool written =
            SampleKhronosMaterialGiRenderedGateReportPublisher.TryWriteFailed(
                options,
                "missing authenticated input");

        Assert.That(written, Is.True);
        using JsonDocument published =
            JsonDocument.Parse(File.ReadAllBytes(options.ReportPath));
        Assert.Multiple(() =>
        {
            Assert.That(
                published.RootElement.GetProperty("status").GetString(),
                Is.EqualTo("Failed"));
            Assert.That(
                published.RootElement.GetProperty("manifestSha256").GetString(),
                Is.Empty);
            Assert.That(
                published.RootElement.GetProperty("failure").GetString(),
                Does.Contain("missing authenticated input"));
        });
    }

    [Test]
    public void HostFailureFinalization_AtomicallyReplacesInProgressAndRetainsEvidence()
    {
        SampleKhronosMaterialGiRenderedGateOptions options = CreateOptions();
        var inProgress = new SampleKhronosMaterialGiRenderedGateReport
        {
            Status = "InProgress",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Repository = KhronosMaterialGiConformance.OfficialRepository,
            Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ManifestSha256 = new string('1', 64),
            PackageSha256 = new string('2', 64),
            AssetCount = 3,
            RuntimeSubMeshCount = 17
        };
        SampleKhronosMaterialGiRenderedGateReportPublisher.WriteAtomic(
            options.ReportPath,
            inProgress);

        bool finalized =
            SampleKhronosMaterialGiRenderedGateReportPublisher.TryFinalizeInProgress(
                options,
                "unhandled validation shutdown");

        using JsonDocument published =
            JsonDocument.Parse(File.ReadAllBytes(options.ReportPath));
        Assert.Multiple(() =>
        {
            Assert.That(finalized, Is.True);
            Assert.That(
                published.RootElement.GetProperty("status").GetString(),
                Is.EqualTo("Failed"));
            Assert.That(
                published.RootElement.GetProperty("failure").GetString(),
                Does.Contain("validation shutdown"));
            Assert.That(
                published.RootElement.GetProperty("packageSha256").GetString(),
                Is.EqualTo(new string('2', 64)));
            Assert.That(
                published.RootElement.GetProperty("assetCount").GetInt32(),
                Is.EqualTo(3));
            Assert.That(
                published.RootElement.GetProperty("runtimeSubMeshCount").GetInt32(),
                Is.EqualTo(17));
            Assert.That(
                published.RootElement.GetProperty("completedAtUtc").ValueKind,
                Is.EqualTo(JsonValueKind.String));
        });
    }

    [Test]
    public void NonzeroHostFailure_CannotLeaveStalePassedReport()
    {
        SampleKhronosMaterialGiRenderedGateOptions options = CreateOptions();
        SampleKhronosMaterialGiRenderedGateReportPublisher.WriteAtomic(
            options.ReportPath,
            new SampleKhronosMaterialGiRenderedGateReport
            {
                Status = "Passed",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            });

        using (var guard =
               new SampleKhronosMaterialGiRenderedGateHostFailureGuard(options))
        {
            // Simulate a report published after the guard's HostStarting
            // marker, followed by a nonzero host result.
            SampleKhronosMaterialGiRenderedGateReportPublisher.WriteAtomic(
                options.ReportPath,
                new SampleKhronosMaterialGiRenderedGateReport
                {
                    Status = "Passed",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            Assert.That(guard.CompleteHostRun(exitCode: 1), Is.False);
        }

        Assert.That(
            SampleKhronosMaterialGiRenderedGateReportPublisher.TryReadStatus(options),
            Is.EqualTo("Failed"));
    }

    [Test]
    public void HostStatusReader_RejectsStructurallyIncompletePassedReport()
    {
        SampleKhronosMaterialGiRenderedGateOptions options = CreateOptions();
        SampleKhronosMaterialGiRenderedGateReportPublisher.WriteAtomic(
            options.ReportPath,
            new SampleKhronosMaterialGiRenderedGateReport
            {
                Status = "Passed",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            });

        Assert.That(
            SampleKhronosMaterialGiRenderedGateReportPublisher.TryReadStatus(
                options),
            Is.Null);
    }

    [Test]
    public void SemanticRenderGate_ProvesUnlitInvarianceAndOfficialEmissiveSeries()
    {
        (LinearFloatImage lit,
         LinearFloatImage lightingOff,
         LinearFloatImage shadingModel,
         LinearFloatImage compiledEmission) = CreateSemanticFixture();

        SampleKhronosMaterialGiSemanticMetrics metrics =
            SampleKhronosMaterialGiSemanticRenderGate.Evaluate(
                lit,
                lightingOff,
                shadingModel,
                compiledEmission);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.UnlitPixelCount, Is.EqualTo(256));
            Assert.That(metrics.UnlitLightingRelativeRmse, Is.Zero);
            Assert.That(metrics.LightingResponsivePbrPixelCount, Is.EqualTo(256));
            Assert.That(metrics.MeanPbrLightingResponse, Is.GreaterThan(0.5));
            Assert.That(
                metrics.EmissiveStrengths.Select(static value => value.Strength),
                Is.EqualTo(new[] { 1f, 2f, 4f, 8f, 16f }));
            Assert.That(
                metrics.EmissiveStrengths,
                Has.All.Matches<SampleKhronosMaterialGiEmissionStrengthEvidence>(
                    value =>
                        value.PixelCount == 64 &&
                        value.MaximumRelativeRadianceError <= 1e-6 &&
                        value.BeautyEmissionCoverageRatio == 1.0));
        });
    }

    [Test]
    public void SemanticRenderGate_FailsClosedForLightingOrEmissionRegression()
    {
        (LinearFloatImage lit,
         LinearFloatImage lightingOff,
         LinearFloatImage shadingModel,
         LinearFloatImage compiledEmission) = CreateSemanticFixture();
        Array.Clear(lightingOff.Pixels, 0, 256 * 3);

        Assert.That(
            () => SampleKhronosMaterialGiSemanticRenderGate.Evaluate(
                lit,
                lightingOff,
                shadingModel,
                compiledEmission),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("Unlit lighting-on/off"));

        (lit, lightingOff, shadingModel, compiledEmission) = CreateSemanticFixture();
        Array.Clear(compiledEmission.Pixels, (512 + 4 * 64) * 3, 64 * 3);
        Assert.That(
            () => SampleKhronosMaterialGiSemanticRenderGate.Evaluate(
                lit,
                lightingOff,
                shadingModel,
                compiledEmission),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("emissive-strength 16"));
    }

    [Test]
    public void SemanticCompanionPaths_AreDeterministicAndSchemaIsVersioned()
    {
        string primary = Path.Combine(_temporaryDirectory, "official.pfm");

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleKhronosMaterialGiRenderedGateRunner.CreateCompanionCapturePath(
                    primary,
                    "lighting-off"),
                Is.EqualTo(Path.Combine(_temporaryDirectory, "official.lighting-off.pfm")));
            Assert.That(
                SampleKhronosMaterialGiRenderedGateReport.CurrentSchemaVersion,
                Is.EqualTo(3));
            Assert.That(
                SampleKhronosMaterialGiRenderedGateReport.CurrentSchema,
                Is.EqualTo("khronos-material-gi-rendered/v3"));
        });
    }

    private static (
        LinearFloatImage Lit,
        LinearFloatImage LightingOff,
        LinearFloatImage ShadingModel,
        LinearFloatImage CompiledEmission) CreateSemanticFixture()
    {
        const int width = 104;
        const int height = 8;
        const int pixelCount = width * height;
        var lit = new float[pixelCount * 3];
        var lightingOff = new float[pixelCount * 3];
        var shadingModel = new float[pixelCount * 3];
        var compiledEmission = new float[pixelCount * 3];

        for (int pixel = 0; pixel < 256; pixel++)
        {
            Set(shadingModel, pixel, 1f, 0.65f, 0.1f);
            Set(lit, pixel, 0.8f, 0.2f, 0.1f);
            Set(lightingOff, pixel, 0.8f, 0.2f, 0.1f);
        }
        for (int pixel = 256; pixel < 512; pixel++)
        {
            Set(shadingModel, pixel, 0.2f, 0.55f, 1f);
            Set(lit, pixel, 1f, 1f, 1f);
            Set(lightingOff, pixel, 0.05f, 0.05f, 0.05f);
        }

        float[] strengths = [1f, 2f, 4f, 8f, 16f];
        for (int strengthIndex = 0; strengthIndex < strengths.Length; strengthIndex++)
        {
            float strength = strengths[strengthIndex];
            float red = 0.1f * strength;
            float green = 0.5f * strength;
            float blue = 0.9f * strength;
            for (int offset = 0; offset < 64; offset++)
            {
                int pixel = 512 + strengthIndex * 64 + offset;
                Set(shadingModel, pixel, 0.2f, 0.55f, 1f);
                Set(
                    compiledEmission,
                    pixel,
                    red / (1f + red),
                    green / (1f + green),
                    blue / (1f + blue));
                Set(lit, pixel, red, green, blue);
                Set(lightingOff, pixel, red, green, blue);
            }
        }

        return (
            new LinearFloatImage(width, height, lit),
            new LinearFloatImage(width, height, lightingOff),
            new LinearFloatImage(width, height, shadingModel),
            new LinearFloatImage(width, height, compiledEmission));
    }

    private static void Set(
        float[] pixels,
        int pixel,
        float red,
        float green,
        float blue)
    {
        int component = pixel * 3;
        pixels[component] = red;
        pixels[component + 1] = green;
        pixels[component + 2] = blue;
    }

    private SampleKhronosMaterialGiRenderedGateOptions CreateOptions()
    {
        string cookedRoot = Path.Combine(_temporaryDirectory, "cooked", "win-x64");
        return SampleKhronosMaterialGiRenderedGateOptions.Create(
            Path.Combine(_temporaryDirectory, "manifest.json"),
            Path.Combine(_temporaryDirectory, "semantic-gate.json"),
            cookedRoot,
            Path.Combine(_temporaryDirectory, "evidence", "capture.pfm"),
            Path.Combine(_temporaryDirectory, "evidence", "render-report.json"));
    }

    private static string[] CreateArguments(
        SampleKhronosMaterialGiRenderedGateOptions options) =>
    [
        "--khronos-material-gi-render-manifest", options.ManifestPath,
        "--khronos-material-gi-gate-report", options.GateReportPath,
        "--khronos-material-gi-cooked-root", options.CookedRoot,
        "--khronos-material-gi-render-capture", options.CapturePath,
        "--khronos-material-gi-render-report", options.ReportPath
    ];

    private (
        string ManifestPath,
        string ReportPath,
        KhronosMaterialGiManifest Manifest,
        KhronosMaterialGiGateReport Report) WriteAuthenticatedInputs()
    {
        const string sourceSha =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var manifest = new KhronosMaterialGiManifest
        {
            SchemaVersion = KhronosMaterialGiConformance.CurrentSchemaVersion,
            Repository = KhronosMaterialGiConformance.OfficialRepository,
            Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Assets =
            [
                new KhronosMaterialGiAsset
                {
                    Name = "UnlitTest",
                    RelativePath = "Models/UnlitTest/glTF-Binary/UnlitTest.glb",
                    Sha256 = sourceSha,
                    Bytes = 123,
                    License = "CC0",
                    Expectations = new KhronosMaterialGiExpectations
                    {
                        MinimumMaterialCount = 2,
                        MinimumUnlitCount = 1,
                        MinimumOpaqueCount = 1
                    }
                }
            ]
        };
        string manifestPath = Path.Combine(_temporaryDirectory, "manifest.json");
        WriteJson(manifestPath, manifest);
        string manifestSha = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
        var report = new KhronosMaterialGiGateReport
        {
            SchemaVersion = KhronosMaterialGiConformance.GateReportSchemaVersion,
            Status = "Passed",
            Repository = KhronosMaterialGiConformance.OfficialRepository,
            Commit = manifest.Commit,
            ManifestSha256 = manifestSha,
            StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Entries =
            [
                new KhronosMaterialGiGateEntry
                {
                    Name = "UnlitTest",
                    Status = "Passed",
                    Sha256 = sourceSha,
                    Bytes = 123,
                    ImportBackend = "SharpGLTF",
                    ImportBackendVersion = "1.0",
                    MaterialCount = 2,
                    SubMeshCount = 2,
                    PrimitiveProfileCount = 2,
                    Warnings = Array.Empty<string>(),
                    ElapsedMilliseconds = 1
                }
            ]
        };
        string reportPath = Path.Combine(_temporaryDirectory, "semantic-gate.json");
        WriteJson(reportPath, report);
        return (manifestPath, reportPath, manifest, report);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                }));
    }
}
