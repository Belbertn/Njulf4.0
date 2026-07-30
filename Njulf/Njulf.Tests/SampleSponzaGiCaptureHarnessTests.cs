using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSponzaGiCaptureHarnessTests
{
    [Test]
    public void DefaultContract_DefinesLockedNamedEndpointsRoisAndOutputs()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;

        contract.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(contract.SceneKind, Is.EqualTo(SampleSceneKind.SponzaPlaza));
            Assert.That(contract.Scenario, Is.EqualTo(SamplePerformanceScenario.GiSponzaRightWallStationary));
            Assert.That(contract.Width, Is.EqualTo(1600));
            Assert.That(contract.Height, Is.EqualTo(900));
            Assert.That(contract.WarmupFrames, Is.EqualTo(SampleSponzaGiCaptureContract.FullSourceRefreshSweepFrameCount));
            Assert.That(SampleSponzaGiCaptureContract.HighBookmarkStationarySettleFrameCount, Is.EqualTo(2048));
            Assert.That(contract.VerticalPathDurationSeconds, Is.InRange(10, 20));
            Assert.That(contract.VerticalTraversalFrameCount, Is.EqualTo(960));
            Assert.That(contract.SchemaVersion, Is.EqualTo("realtime-gi-closure-sponza-capture/v7"));
            Assert.That(contract.TotalCaptureFrameCount, Is.EqualTo(5_182));
            Assert.That(contract.LowBookmark.Name, Is.EqualTo("SponzaPlazaUpperFacadeLow"));
            Assert.That(contract.LowBookmark.Position.Y, Is.EqualTo(1.35f));
            Assert.That(contract.HighBookmark.Name, Is.EqualTo("SponzaPlazaUpperFacadeHigh"));
            Assert.That(contract.HighBookmark.Position.Y, Is.EqualTo(10.35f));
            Assert.That(contract.ReceiverRois.Select(static roi => roi.Name), Is.EquivalentTo(new[]
            {
                "central-upper-facade",
                "right-upper-wall",
                "left-gallery-interior",
                "right-gallery-interior",
                "arcade-interior",
                "outdoor-reference-patch"
            }));
            Assert.That(contract.Outputs.Select(static output => output.Name), Is.EquivalentTo(new[]
            {
                "beauty",
                "direct-only",
                "final-indirect",
                "irradiance-log",
                "sampled-irradiance",
                "final-diffuse",
                "volume-contributor",
                "gather-clipmap",
                "gather-blend-weight",
                "gather-fallback",
                "spatial-coverage",
                "support",
                "data-confidence",
                "directional-support",
                "confidence-chain",
                "visibility",
                "ownership",
                "fallback",
                "probe-state",
                "classification-invalid-score",
                "update-reasons"
            }));
            SampleSponzaGiCaptureOutput directOnly = contract.Outputs.Single(static output => output.Name == "direct-only");
            Assert.That(directOnly.DisableGlobalIllumination, Is.True);
            Assert.That(directOnly.DisableEnvironmentLighting, Is.True);
            Assert.That(
                contract.Outputs.Where(static output => output.Name != "direct-only"),
                Has.None.Matches<SampleSponzaGiCaptureOutput>(static output => output.DisableEnvironmentLighting));
            Assert.That(contract.Outputs.Single(static output => output.Name == "ownership").DebugView,
                Is.EqualTo(GlobalIlluminationDebugView.DdgiEffectiveWeight));
            Assert.That(contract.ReceiverRois, Has.All.Matches<SampleSponzaGiReceiverRoi>(roi => roi.RequireCoarserFallback));
            Assert.That(contract.Fingerprint, Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void VerticalTraversal_IsFixedAndReachesBothBookmarksExactly()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;

        SampleSponzaGiCameraBookmark first = contract.SampleVerticalTraversalFrame(0);
        SampleSponzaGiCameraBookmark middle = contract.SampleVerticalTraversalFrame(contract.VerticalTraversalFrameCount / 2);
        SampleSponzaGiCameraBookmark last = contract.SampleVerticalTraversalFrame(contract.VerticalTraversalFrameCount - 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.Position, Is.EqualTo(contract.LowBookmark.Position));
            Assert.That(last.Position, Is.EqualTo(contract.HighBookmark.Position));
            Assert.That(first.Yaw, Is.EqualTo(contract.LowBookmark.Yaw));
            Assert.That(last.Pitch, Is.EqualTo(contract.HighBookmark.Pitch));
            Assert.That(middle.Position.X, Is.EqualTo(contract.LowBookmark.Position.X));
            Assert.That(middle.Position.Z, Is.EqualTo(contract.LowBookmark.Position.Z));
            Assert.That(middle.Position.Y, Is.GreaterThan(contract.LowBookmark.Position.Y));
            Assert.That(middle.Position.Y, Is.LessThan(contract.HighBookmark.Position.Y));
            Assert.That(
                () => contract.SampleVerticalTraversalFrame(contract.VerticalTraversalFrameCount),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void CoveragePath_ContainsEveryFixedTimestepOfTheLockedVerticalTraversal()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        IReadOnlyList<SimpleDdgiCoverageCameraSample> path = contract.CreateCoverageCameraPath();

        Assert.Multiple(() =>
        {
            Assert.That(path, Has.Count.EqualTo(960));
            Assert.That(path[0].Name, Is.EqualTo(contract.LowBookmark.Name));
            Assert.That(path[^1].Name, Is.EqualTo(contract.HighBookmark.Name));
            Assert.That(path[0].Position, Is.EqualTo(contract.LowBookmark.Position));
            Assert.That(path[^1].Position, Is.EqualTo(contract.HighBookmark.Position));
            Assert.That(path.Select(static sample => sample.Name).Distinct().Count(), Is.EqualTo(path.Count));
            Assert.That(path[480].Position, Is.EqualTo(contract.SampleVerticalTraversalFrame(480).Position));
        });
    }

    [Test]
    public void Sequence_ProducesEndpointOutputSetsInAStableFrameOrder()
    {
        var sequence = new SampleSponzaGiCaptureSequence();
        var captured = new List<(string Bookmark, string Output)>();
        int frames = 0;

        while (!sequence.IsComplete)
        {
            SampleSponzaGiCaptureInstruction instruction = sequence.CurrentInstruction;
            if (instruction.Output != null && instruction.CaptureWindowAfterRenderedFrame)
                captured.Add((instruction.BookmarkName, instruction.Output.Name));
            sequence.AdvanceAfterRenderedFrame();
            frames++;
        }

        SampleSponzaGiCaptureContract contract = sequence.Contract;
        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.EqualTo(contract.TotalCaptureFrameCount));
            Assert.That(captured, Has.Count.EqualTo(contract.Outputs.Count * 2));
            Assert.That(captured.Take(contract.Outputs.Count).Select(static capture => capture.Bookmark),
                Is.All.EqualTo(contract.LowBookmark.Name));
            Assert.That(captured.Skip(contract.Outputs.Count).Select(static capture => capture.Bookmark),
                Is.All.EqualTo(contract.HighBookmark.Name));
            Assert.That(captured.Take(contract.Outputs.Count).Select(static capture => capture.Output),
                Is.EqualTo(contract.Outputs.Select(static output => output.Name)));
            Assert.That(captured.Skip(contract.Outputs.Count).Select(static capture => capture.Output),
                Is.EqualTo(contract.Outputs.Select(static output => output.Name)));
            Assert.That(sequence.Stage, Is.EqualTo(SampleSponzaGiCaptureStage.Complete));
        });
    }

    [Test]
    public void Sequence_PresentsEveryEndpointOutputBeforeCapturingTheHeldState()
    {
        var sequence = new SampleSponzaGiCaptureSequence();
        var endpointFrames = new List<SampleSponzaGiCaptureInstruction>();

        while (!sequence.IsComplete)
        {
            SampleSponzaGiCaptureInstruction instruction = sequence.CurrentInstruction;
            if (instruction.Output != null)
                endpointFrames.Add(instruction);
            sequence.AdvanceAfterRenderedFrame();
        }

        SampleSponzaGiCaptureContract contract = sequence.Contract;
        Assert.That(endpointFrames, Has.Count.EqualTo(
            contract.Outputs.Count * 2 * SampleSponzaGiCaptureContract.FramesPerEndpointOutput));
        for (int i = 0; i < endpointFrames.Count; i += SampleSponzaGiCaptureContract.FramesPerEndpointOutput)
        {
            SampleSponzaGiCaptureInstruction presentation = endpointFrames[i];
            SampleSponzaGiCaptureInstruction capture = endpointFrames[
                i + SampleSponzaGiCaptureContract.FramesPerEndpointOutput - 1];
            Assert.Multiple(() =>
            {
                Assert.That(presentation.Output, Is.EqualTo(capture.Output));
                Assert.That(presentation.Camera, Is.EqualTo(capture.Camera));
                Assert.That(presentation.BookmarkName, Is.EqualTo(capture.BookmarkName));
                Assert.That(presentation.CaptureWindowAfterRenderedFrame, Is.False);
                Assert.That(capture.CaptureWindowAfterRenderedFrame, Is.True);
                Assert.That(capture.StageFrameIndex, Is.EqualTo(
                    presentation.StageFrameIndex + SampleSponzaGiCaptureContract.FramesPerEndpointOutput - 1));
                Assert.That(
                    endpointFrames
                        .Skip(i)
                        .Take(SampleSponzaGiCaptureContract.FramesPerEndpointOutput - 1)
                        .Select(static frame => frame.CaptureWindowAfterRenderedFrame),
                    Is.All.False);
            });
        }
    }

    [Test]
    public void CaptureMode_SeparatesProductionTimingFromDetailedInvestigationCounters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SampleSponzaGiCaptureContract.UsesDetailedInvestigationCounters(
                    SampleSponzaGiCaptureMode.ProductionTiming),
                Is.False);
            Assert.That(
                SampleSponzaGiCaptureContract.UsesDetailedInvestigationCounters(
                    SampleSponzaGiCaptureMode.DetailedDiagnostics),
                Is.True);
        });
    }

    [Test]
    public void CanonicalProfile_SatisfiesCaptureLockAndReceiverCoverageOracle()
    {
        var settings = new RenderSettings();
        SampleSponzaGlobalIlluminationProfile.Configure(settings);
        SampleSponzaGlobalIlluminationProfile.ApplyValidationOverlay(settings);
        settings.Particles.Enabled = false;
        settings.Animation.Enabled = false;
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;

        IReadOnlyList<string> violations = contract.ValidateLockedSettings(settings);
        SimpleDdgiReceiverCoverageReport report = SimpleDdgiReceiverCoverageValidator.Validate(
            settings.GlobalIllumination,
            contract.SceneBounds,
            contract.CreateCoverageRegions(),
            contract.CreateCoverageCameraPath());

        Assert.Multiple(() =>
        {
            Assert.That(violations, Is.Empty);
            Assert.That(report.Layout.WasDegraded, Is.False, report.Layout.Summary);
            Assert.That(report.IsCovered, Is.True,
                string.Join(Environment.NewLine, report.Issues.Select(static issue => issue.Message)));
            Assert.That(report.Samples, Has.Count.EqualTo(
                contract.ReceiverRois.Count * contract.VerticalTraversalFrameCount * 15));
            Assert.That(report.Samples.Where(static sample => sample.IsInTransitionBand), Is.Not.Empty);
            Assert.That(report.Samples.Where(static sample => sample.IsInTransitionBand),
                Has.All.Matches<SimpleDdgiReceiverCoverageSample>(sample => sample.HasCoarserFallback));
        });
    }

    [Test]
    public void CanonicalLighting_SatisfiesCaptureLockAndRejectsOccludedSunProfilesAndShadowLeak()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        Light canonical = SampleSponzaLightingProfile.CreateDirectionalKey();
        Light disabledSourceSun = canonical;
        disabledSourceSun.Direction = SampleSponzaLightingProfile.SourceSunDirection;
        Light formerSyntheticSun = canonical;
        formerSyntheticSun.Direction = System.Numerics.Vector3.Normalize(
            new System.Numerics.Vector3(0.18f, -0.82f, 0.54f));
        Light partialStrength = canonical;
        partialStrength.ShadowStrength = 0.85f;
        Light localLight = new() { Type = LightType.Point };

        Assert.Multiple(() =>
        {
            Assert.That(contract.ValidateLockedLighting(new[] { canonical }), Is.Empty);
            Assert.That(
                contract.ValidateLockedLighting(new[] { disabledSourceSun }),
                Has.Some.Contains("locked directional key"));
            Assert.That(
                contract.ValidateLockedLighting(new[] { formerSyntheticSun }),
                Has.Some.Contains("locked directional key"));
            Assert.That(
                contract.ValidateLockedLighting(new[] { partialStrength }),
                Has.Some.Contains("fully occluding"));
            Assert.That(
                contract.ValidateLockedLighting(new[] { canonical, localLight }),
                Has.Some.Contains("exactly one light"));
        });
    }

    [Test]
    public void CompletedManifest_RejectsUnverifiedRendererRequests()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sponza-gi-capture-{Guid.NewGuid():N}");
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        try
        {
            SampleSponzaGiCaptureOutput beauty = contract.Outputs.Single(static output => output.Name == "beauty");
            var artifacts = new[]
            {
                new SampleSponzaGiCapturedArtifact(
                    contract.LowBookmark.Name,
                    beauty.Name,
                    "renderer-screenshot-request",
                    Path.ChangeExtension(contract.GetRelativeImagePath(contract.LowBookmark.Name, beauty), ".renderer.png"),
                    VerificationStatus: "requested")
            };

            Assert.That(
                () => contract.WriteRunManifest(directory, artifacts, "completed"),
                Throws.InvalidOperationException.With.Message.Contains("cannot be completed"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ArtifactVerification_RejectsOversizedPngBeforeReadingPayload()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sponza-gi-artifact-bound-{Guid.NewGuid():N}");
        string relativePath = "captures/oversized.png";
        string fullPath = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            using (var output = new FileStream(
                       fullPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                output.SetLength(
                    SampleEvidenceFileIo.MaximumLinearFloatImageBytes + 1);
            }
            var artifact = new SampleSponzaGiCapturedArtifact(
                string.Empty,
                string.Empty,
                "renderer-screenshot-request",
                relativePath);

            bool verified =
                SampleSponzaGiCaptureContract.Default.TryVerifyArtifact(
                    directory,
                    artifact,
                    out _,
                    out string failureReason);

            Assert.Multiple(() =>
            {
                Assert.That(verified, Is.False);
                Assert.That(
                    failureReason,
                    Does.Contain("bounded limit"));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void CompletedManifest_RequiresAndRecordsHashVerifiedRendererArtifacts()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sponza-gi-capture-{Guid.NewGuid():N}");
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        try
        {
            var artifacts = new List<SampleSponzaGiCapturedArtifact>();
            artifacts.Add(CreateVerifiedTextArtifact(
                contract,
                directory,
                string.Empty,
                string.Empty,
                "capture-contract",
                "sponza-gi-capture-contract.json",
                "{\"schemaVersion\":\"test\"}"));
            artifacts.Add(CreateVerifiedTextArtifact(
                contract,
                directory,
                string.Empty,
                string.Empty,
                "visual-metric-gate",
                "sponza-gi-visual-metric-gate.json",
                "{\"schemaVersion\":\"test\"}"));
            artifacts.Add(CreateVerifiedTextArtifact(
                contract,
                directory,
                string.Empty,
                string.Empty,
                "coverage-oracle",
                "sponza-gi-coverage-oracle.json",
                "{\"schemaVersion\":\"test\"}"));
            foreach (SampleSponzaGiCameraBookmark bookmark in new[] { contract.LowBookmark, contract.HighBookmark })
            {
                foreach (SampleSponzaGiCaptureOutput output in contract.Outputs)
                {
                    string imagePath = contract.GetRelativeImagePath(bookmark.Name, output);
                    artifacts.Add(CreateVerifiedPngArtifact(
                        contract, directory, bookmark.Name, output.Name, "window-screenshot", imagePath));
                    artifacts.Add(CreateVerifiedPngArtifact(
                        contract,
                        directory,
                        bookmark.Name,
                        output.Name,
                        "renderer-screenshot",
                        Path.ChangeExtension(imagePath, ".renderer.png")));
                }

                string snapshotPath = Path.Combine(
                    Path.GetDirectoryName(contract.GetRelativeImagePath(bookmark.Name, contract.Outputs[0]))!,
                    "performance-snapshot.json");
                artifacts.Add(CreateVerifiedTextArtifact(
                    contract,
                    directory,
                    bookmark.Name,
                    "beauty",
                    "performance-snapshot",
                    snapshotPath,
                    "{\"schemaVersion\":\"test\"}"));
            }

            contract.WriteRunManifest(
                directory,
                artifacts,
                "completed",
                captureMode: SampleSponzaGiCaptureMode.ProductionTiming);

            string runJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-capture-run.json"));
            Assert.Multiple(() =>
            {
                Assert.That(contract.GetCompletionBlockers(directory, artifacts), Is.Empty);
                Assert.That(runJson, Does.Contain("\"status\": \"completed\""));
                Assert.That(runJson, Does.Contain("\"captureMode\": \"ProductionTiming\""));
                Assert.That(runJson, Does.Contain("\"sha256\":"));
                Assert.That(runJson, Does.Not.Contain("renderer-screenshot-request"));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void VisualMetricGate_IsDeterministicAndExplicitlyRequiresAnApprovedBaseline()
    {
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        SampleSponzaGiVisualMetricGate gate = contract.CreateVisualMetricGate(
            SampleSponzaGiCaptureMode.DetailedDiagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(gate.ContractFingerprint, Is.EqualTo(contract.Fingerprint));
            Assert.That(gate.EvaluationStatus, Is.EqualTo("not-evaluated-no-approved-baseline"));
            Assert.That(gate.TimingClassification, Does.Contain("timing-ineligible"));
            Assert.That(gate.ReceiverRois.Select(static roi => roi.Name),
                Is.EquivalentTo(contract.ReceiverRois.Select(static roi => roi.Name)));
            Assert.That(gate.ReceiverRois, Has.All.Matches<SampleSponzaGiVisualMetricRoi>(roi =>
                roi.RequiredMetrics.Any(static metric => metric.RequiresApprovedBaseline) &&
                roi.RequiredOutputs.Contains("direct-only") &&
                roi.RequiredOutputs.Contains("volume-contributor") &&
                roi.RequiredOutputs.Contains("fallback")));
        });
    }

    [Test]
    public void ContractAndRunManifest_KeepTheFingerprintAndRelativeArtifactsTogether()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sponza-gi-capture-{Guid.NewGuid():N}");
        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        try
        {
            contract.WriteContract(directory);
            contract.WriteVisualMetricGate(directory, SampleSponzaGiCaptureMode.DetailedDiagnostics);
            contract.WriteRunManifest(
                directory,
                [new SampleSponzaGiCapturedArtifact(
                    contract.LowBookmark.Name,
                    "beauty",
                    "renderer-screenshot-request",
                    contract.GetRelativeImagePath(contract.LowBookmark.Name, contract.Outputs[0]))],
                "running");

            string contractJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-capture-contract.json"));
            string visualMetricJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-visual-metric-gate.json"));
            string runJson = File.ReadAllText(Path.Combine(directory, "sponza-gi-capture-run.json"));
            Assert.Multiple(() =>
            {
                Assert.That(contractJson, Does.Contain(contract.Fingerprint));
                Assert.That(visualMetricJson, Does.Contain(contract.Fingerprint));
                Assert.That(visualMetricJson, Does.Contain("not-evaluated-no-approved-baseline"));
                Assert.That(runJson, Does.Contain(contract.Fingerprint));
                Assert.That(runJson, Does.Contain("renderer-screenshot-request"));
                Assert.That(runJson, Does.Not.Contain(Path.GetFullPath(directory)));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static SampleSponzaGiCapturedArtifact CreateVerifiedPngArtifact(
        SampleSponzaGiCaptureContract contract,
        string directory,
        string bookmark,
        string output,
        string kind,
        string relativePath)
    {
        string fullPath = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, ValidPng);
        return Verify(contract, directory, new SampleSponzaGiCapturedArtifact(bookmark, output, kind, relativePath));
    }

    private static SampleSponzaGiCapturedArtifact CreateVerifiedTextArtifact(
        SampleSponzaGiCaptureContract contract,
        string directory,
        string bookmark,
        string output,
        string kind,
        string relativePath,
        string content)
    {
        string fullPath = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return Verify(contract, directory, new SampleSponzaGiCapturedArtifact(bookmark, output, kind, relativePath));
    }

    private static SampleSponzaGiCapturedArtifact Verify(
        SampleSponzaGiCaptureContract contract,
        string directory,
        SampleSponzaGiCapturedArtifact artifact)
    {
        bool verified = contract.TryVerifyArtifact(directory, artifact, out SampleSponzaGiCapturedArtifact result, out string reason);
        Assert.That(verified, Is.True, reason);
        return result;
    }

    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL0NwAAAABJRU5ErkJggg==");
}
