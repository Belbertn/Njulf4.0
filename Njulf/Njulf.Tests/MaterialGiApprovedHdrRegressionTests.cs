using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialGiApprovedHdrRegressionTests
{
    [Test]
    public void HdrImageMetric_UsesDeterministicNvidiaHdrFlipAndEnforcesRelativeRmse()
    {
        LinearFloatImage reference = ConstantImage(16, 8, 1.0f);
        LinearFloatImage acceptedImage = ConstantImage(16, 8, 1.05f);
        LinearFloatImage rejectedImage = ConstantImage(16, 8, 1.5f);
        SampleMaterialGiArtifact referenceArtifact =
            Artifact(SampleMaterialGiCaptureSignal.FinalComposedIndirect, "reference", 'a');
        SampleMaterialGiArtifact acceptedArtifact =
            Artifact(SampleMaterialGiCaptureSignal.FinalComposedIndirect, "accepted", 'b');
        SampleMaterialGiArtifact rejectedArtifact =
            Artifact(SampleMaterialGiCaptureSignal.FinalComposedIndirect, "rejected", 'c');

        SampleMaterialGiApprovedHdrImageResult accepted =
            SampleMaterialGiApprovedHdrComparer.CompareImages(
                SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                referenceArtifact,
                acceptedArtifact,
                reference,
                acceptedImage);
        SampleMaterialGiApprovedHdrImageResult acceptedAgain =
            SampleMaterialGiApprovedHdrComparer.CompareImages(
                SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                referenceArtifact,
                acceptedArtifact,
                reference,
                acceptedImage);
        SampleMaterialGiApprovedHdrImageResult rejected =
            SampleMaterialGiApprovedHdrComparer.CompareImages(
                SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                referenceArtifact,
                rejectedArtifact,
                reference,
                rejectedImage);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Passed, Is.True);
            Assert.That(accepted.RelativeRmse, Is.EqualTo(0.05).Within(1e-6));
            Assert.That(accepted.FlipP95, Is.EqualTo(acceptedAgain.FlipP95));
            Assert.That(accepted.FlipP95, Is.LessThanOrEqualTo(
                SampleMaterialGiApprovedHdrComparer.MaximumFlipP95));
            Assert.That(rejected.Passed, Is.False);
            Assert.That(rejected.RelativeRmse, Is.EqualTo(0.5).Within(1e-6));
            Assert.That(rejected.FlipP95, Is.GreaterThan(accepted.FlipP95));
            Assert.That(
                SampleMaterialGiApprovedHdrComparer.RelativeRmseDefinition,
                Does.Contain("1e-6"));
            Assert.That(
                SampleMaterialGiHdrFlipMetric.Definition,
                Does.Contain("NVIDIA HDR-FLIP v1.7"));
        });
    }

    [Test]
    public void HdrFlipMetric_MatchesPinnedNvidiaV17SyntheticVector()
    {
        const int width = 16;
        const int height = 16;
        LinearFloatImage reference = CreateRgbImage(
            width,
            height,
            (x, y) =>
            {
                float value = 0.25f + 0.03f * x + 0.02f * y;
                return (value, value * 0.8f, value * 0.6f);
            });
        LinearFloatImage candidate = CreateRgbImage(
            width,
            height,
            (x, y) =>
            {
                float value = 0.25f + 0.03f * x + 0.02f * y;
                return x is >= 6 and < 10 && y is >= 6 and < 10
                    ? (value * 1.2f, value * 0.8f * 0.9f, value * 0.6f * 1.1f)
                    : (value, value * 0.8f, value * 0.6f);
            });

        double identicalP95 =
            SampleMaterialGiHdrFlipMetric.ComputeP95(reference, reference);
        double perturbedP95 =
            SampleMaterialGiHdrFlipMetric.ComputeP95(reference, candidate);
        double perturbedAgainP95 =
            SampleMaterialGiHdrFlipMetric.ComputeP95(reference, candidate);

        Assert.Multiple(() =>
        {
            Assert.That(identicalP95, Is.Zero);
            // NVIDIA's own cross-backend conformance tests use 1e-5 for
            // native floating-point variation.
            Assert.That(perturbedP95, Is.EqualTo(0.1125755).Within(1e-5));
            Assert.That(perturbedAgainP95, Is.EqualTo(perturbedP95));
            Assert.That(
                SampleMaterialGiHdrFlipMetric.FixedConfiguration,
                Is.EqualTo(new SampleMaterialGiHdrFlipConfiguration(
                    "1.7",
                    "b475eb4",
                    "FlipBinding.CSharp",
                    "1.0.3",
                    67.0206451,
                    "aces",
                    "reference-auto",
                    "reference-auto",
                    "reference-auto")));
        });
    }

    [Test]
    public void HdrImageMetric_RejectsNonfiniteNegativeAndDimensionMismatch()
    {
        LinearFloatImage valid = ConstantImage(2, 2, 1f);
        var nonfinite = new LinearFloatImage(
            2,
            2,
            [float.NaN, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f]);
        var negative = new LinearFloatImage(
            2,
            2,
            [-0.01f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f]);
        LinearFloatImage wrongSize = ConstantImage(1, 2, 1f);
        SampleMaterialGiArtifact artifact =
            Artifact(SampleMaterialGiCaptureSignal.FinalComposedIndirect, "image", 'a');

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.CompareImages(
                    SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                    artifact,
                    artifact,
                    valid,
                    nonfinite),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("non-finite"));
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.CompareImages(
                    SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                    artifact,
                    artifact,
                    valid,
                    negative),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("negative radiance"));
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.CompareImages(
                    SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                    artifact,
                    artifact,
                    valid,
                    wrongSize),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("dimensions"));
        });
    }

    [Test]
    public void RoiEvaluator_PassesAllFiveApprovedSyntheticGates()
    {
        const int width = 4;
        const int height = 2;
        SampleMaterialGiApprovedHdrReferenceManifest manifest =
            CreateManifest(width, height, includeTemporal: true);
        LinearFloatImage referenceDdgi = CreateImage(
            width,
            height,
            (x, _) => x < 2 ? 1.0f : 1.02f);
        LinearFloatImage candidateDdgi = CreateImage(
            width,
            height,
            (x, _) => x < 2 ? 1.0f : 1.04f);
        LinearFloatImage raster = ConstantImage(width, height, 1.0f);
        LinearFloatImage referenceComposed = referenceDdgi;
        LinearFloatImage candidateComposed = Scale(candidateDdgi, 1.01f);
        var reference = new Dictionary<SampleMaterialGiCaptureSignal, LinearFloatImage>
        {
            [SampleMaterialGiCaptureSignal.DirectDiffuse] = raster,
            [SampleMaterialGiCaptureSignal.FinalDdgiDiffuse] = referenceDdgi,
            [SampleMaterialGiCaptureSignal.FinalComposedIndirect] = referenceComposed
        };
        var candidate = new Dictionary<SampleMaterialGiCaptureSignal, LinearFloatImage>
        {
            [SampleMaterialGiCaptureSignal.DirectDiffuse] = raster,
            [SampleMaterialGiCaptureSignal.FinalDdgiDiffuse] = candidateDdgi,
            [SampleMaterialGiCaptureSignal.FinalComposedIndirect] = candidateComposed
        };
        var temporal = new Dictionary<string, LinearFloatImage>(StringComparer.Ordinal)
        {
            ["temporal/frame-000.pfm"] = ConstantImage(width, height, 0.99f),
            ["temporal/frame-001.pfm"] = ConstantImage(width, height, 1.00f),
            ["temporal/frame-002.pfm"] = ConstantImage(width, height, 1.01f)
        };

        IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> results =
            SampleMaterialGiApprovedHdrComparer.EvaluateRoiGates(
                manifest,
                reference,
                candidate,
                path => (temporal[path], new string('d', 64)));

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(4));
            Assert.That(results.All(static result => result.Passed), Is.True);
            Assert.That(
                results.Single(result =>
                    result.Kind == SampleMaterialGiVisualRoiGateKind.UniformLuminance)
                    .MeasuredRelativeDifference,
                Is.LessThanOrEqualTo(0.05));
            Assert.That(
                results.Single(result =>
                    result.Kind == SampleMaterialGiVisualRoiGateKind.TransitionStep)
                    .MeasuredRelativeDifference,
                Is.LessThanOrEqualTo(0.10));
            Assert.That(
                results.Single(result =>
                    result.Kind == SampleMaterialGiVisualRoiGateKind.LowFrequencyMean)
                    .MeasuredRelativeDifference,
                Is.EqualTo(0.01).Within(1e-5));
            Assert.That(
                results.Single(result =>
                    result.Kind == SampleMaterialGiVisualRoiGateKind.TemporalStability)
                    .MeasuredRelativeDifference,
                Is.LessThan(0.03));
        });
    }

    [Test]
    public void RoiEvaluator_FailsEachSyntheticGateOutsideItsLimit()
    {
        const int width = 4;
        const int height = 2;
        SampleMaterialGiApprovedHdrReferenceManifest manifest =
            CreateManifest(width, height, includeTemporal: true);
        LinearFloatImage referenceDdgi = ConstantImage(width, height, 1.0f);
        LinearFloatImage candidateDdgi = CreateImage(
            width,
            height,
            (x, _) => x == 0 ? 0.0f : x < 2 ? 0.5f : 1.5f);
        LinearFloatImage referenceRaster = ConstantImage(width, height, 1.0f);
        LinearFloatImage candidateRaster = ConstantImage(width, height, 1.0f);
        LinearFloatImage referenceComposed = referenceDdgi;
        LinearFloatImage candidateComposed = Scale(candidateDdgi, 1.10f);
        var reference = new Dictionary<SampleMaterialGiCaptureSignal, LinearFloatImage>
        {
            [SampleMaterialGiCaptureSignal.DirectDiffuse] = referenceRaster,
            [SampleMaterialGiCaptureSignal.FinalDdgiDiffuse] = referenceDdgi,
            [SampleMaterialGiCaptureSignal.FinalComposedIndirect] = referenceComposed
        };
        var candidate = new Dictionary<SampleMaterialGiCaptureSignal, LinearFloatImage>
        {
            [SampleMaterialGiCaptureSignal.DirectDiffuse] = candidateRaster,
            [SampleMaterialGiCaptureSignal.FinalDdgiDiffuse] = candidateDdgi,
            [SampleMaterialGiCaptureSignal.FinalComposedIndirect] = candidateComposed
        };
        var temporal = new Dictionary<string, LinearFloatImage>(StringComparer.Ordinal)
        {
            ["temporal/frame-000.pfm"] = ConstantImage(width, height, 0.80f),
            ["temporal/frame-001.pfm"] = ConstantImage(width, height, 1.00f),
            ["temporal/frame-002.pfm"] = ConstantImage(width, height, 1.20f)
        };

        IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> results =
            SampleMaterialGiApprovedHdrComparer.EvaluateRoiGates(
                manifest,
                reference,
                candidate,
                path => (temporal[path], new string('e', 64)));

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(4));
            Assert.That(
                results.Select(static result => result.Kind),
                Is.EquivalentTo(Enum.GetValues<SampleMaterialGiVisualRoiGateKind>()
                    .Where(static kind =>
                        kind != SampleMaterialGiVisualRoiGateKind.Unknown &&
                        kind != SampleMaterialGiVisualRoiGateKind.LegacyRadianceThresholdAlphaProxy)));
            Assert.That(results.All(static result => !result.Passed), Is.True);
            Assert.That(
                results.Single(result =>
                    result.Kind == SampleMaterialGiVisualRoiGateKind.TemporalStability)
                    .MeasuredRelativeDifference,
                Is.GreaterThanOrEqualTo(0.03));
        });
    }

    [Test]
    public void RoiEvaluator_TemporalGateRejectsSpatiallyCancellingCheckerboardFlicker()
    {
        const int width = 4;
        const int height = 4;
        SampleMaterialGiApprovedHdrReferenceManifest manifest =
            CreateManifest(width, height, includeTemporal: true);
        LinearFloatImage stable = ConstantImage(width, height, 1.0f);
        var images = new Dictionary<SampleMaterialGiCaptureSignal, LinearFloatImage>
        {
            [SampleMaterialGiCaptureSignal.DirectDiffuse] = stable,
            [SampleMaterialGiCaptureSignal.FinalDdgiDiffuse] = stable,
            [SampleMaterialGiCaptureSignal.FinalComposedIndirect] = stable
        };
        LinearFloatImage checkerboardA = CreateImage(
            width,
            height,
            (x, y) => ((x + y) & 1) == 0 ? 0.8f : 1.2f);
        LinearFloatImage checkerboardB = CreateImage(
            width,
            height,
            (x, y) => ((x + y) & 1) == 0 ? 1.2f : 0.8f);
        var temporal = new Dictionary<string, LinearFloatImage>(StringComparer.Ordinal)
        {
            ["temporal/frame-000.pfm"] = checkerboardA,
            ["temporal/frame-001.pfm"] = checkerboardB,
            ["temporal/frame-002.pfm"] = checkerboardA
        };

        double[] frameMeans = temporal.Values
            .Select(static image => image.Pixels.Average(static value => (double)value))
            .ToArray();
        IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> results =
            SampleMaterialGiApprovedHdrComparer.EvaluateRoiGates(
                manifest,
                images,
                images,
                path => (temporal[path], new string('f', 64)));
        SampleMaterialGiApprovedRoiGateResult temporalResult =
            results.Single(result =>
                result.Kind == SampleMaterialGiVisualRoiGateKind.TemporalStability);

        Assert.Multiple(() =>
        {
            // The former frame-mean metric measured zero variation for this
            // sequence because bright and dark pixels cancel spatially.
            Assert.That(frameMeans.Max() - frameMeans.Min(), Is.LessThan(1.0e-6));
            Assert.That(temporalResult.Passed, Is.False);
            Assert.That(
                temporalResult.MeasuredRelativeDifference,
                Is.GreaterThan(SampleMaterialGiApprovedHdrComparer.MaximumTemporalP95));
            Assert.That(
                temporalResult.SampleCount,
                Is.EqualTo((long)width * height * temporal.Count));
            Assert.That(temporalResult.ReferenceValue, Is.Null);
            Assert.That(
                temporalResult.ComparisonDefinition,
                Is.EqualTo(SampleMaterialGiApprovedHdrComparer.TemporalMetricDefinition));
        });
    }

    [Test]
    public void ApprovedManifest_RejectsTemporalEvidenceBeyondBoundedSampleBuffer()
    {
        int width = SampleMaterialGiConformanceCatalog.LockedWidth;
        int height = SampleMaterialGiConformanceCatalog.LockedHeight;
        SampleMaterialGiApprovedHdrReferenceManifest valid =
            CreateManifest(width, height, includeTemporal: true);
        long pixelCount = (long)width * height;
        int overBudgetFrameCount = checked(
            (int)(SampleMaterialGiApprovedHdrComparer.MaximumTemporalSampleCount /
                  pixelCount) + 1);
        Assert.That(
            overBudgetFrameCount,
            Is.LessThanOrEqualTo(
                SampleMaterialGiApprovedHdrComparer.MaximumTemporalFrameCount));

        SampleMaterialGiApprovedRoi roi = valid.Rois.Single();
        SampleMaterialGiApprovedRoiGate oversizedTemporal =
            roi.Gates.Single(gate =>
                gate.Kind == SampleMaterialGiVisualRoiGateKind.TemporalStability) with
            {
                TemporalFrameRelativePaths = Enumerable.Range(0, overBudgetFrameCount)
                    .Select(index => $"temporal/frame-{index:D3}.pfm")
                    .ToArray()
            };
        SampleMaterialGiApprovedHdrReferenceManifest oversized =
            valid with
            {
                Rois =
                [
                    roi with
                    {
                        Gates = roi.Gates
                            .Select(gate =>
                                gate.Kind == SampleMaterialGiVisualRoiGateKind.TemporalStability
                                    ? oversizedTemporal
                                    : gate)
                            .ToArray()
                    }
                ]
            };

        Assert.That(
            () => SampleMaterialGiApprovedHdrComparer.ValidateApprovedManifest(oversized),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("bounded temporal sample-buffer budget"));
    }

    [Test]
    public void ApprovedManifest_RequiresFixedThresholdsNamedRoisAndTemporalDisposition()
    {
        SampleMaterialGiApprovedHdrReferenceManifest valid =
            CreateManifest(
                SampleMaterialGiConformanceCatalog.LockedWidth,
                SampleMaterialGiConformanceCatalog.LockedHeight,
                includeTemporal: false);
        SampleMaterialGiApprovedHdrReferenceManifest noRois =
            valid with { Rois = Array.Empty<SampleMaterialGiApprovedRoi>() };
        SampleMaterialGiApprovedRoi firstRoi = valid.Rois[0];
        SampleMaterialGiApprovedRoiGate relaxedUniform =
            firstRoi.Gates[0] with { MaximumRelativeDifference = 0.051 };
        SampleMaterialGiApprovedHdrReferenceManifest relaxed =
            valid with
            {
                Rois =
                [
                    firstRoi with
                    {
                        Gates = [relaxedUniform, .. firstRoi.Gates.Skip(1)]
                    }
                ]
            };
        SampleMaterialGiApprovedHdrReferenceManifest noTemporalDisposition =
            valid with { TemporalPolicy = null! };
        SampleMaterialGiApprovedHdrReferenceManifest incompatibleFlip =
            valid with
            {
                FlipConfiguration = valid.FlipConfiguration with
                {
                    PixelsPerDegree = 60.0
                }
            };
        var retiredAlphaProxy = new SampleMaterialGiApprovedRoiGate(
            SampleMaterialGiVisualRoiGateKind.LegacyRadianceThresholdAlphaProxy,
            0.02,
            SampleMaterialGiCaptureSignal.FinalDdgiDiffuse,
            ComparisonSignal: SampleMaterialGiCaptureSignal.DirectDiffuse,
            CoverageThreshold: 0.5);
        SampleMaterialGiApprovedHdrReferenceManifest incompatibleAlpha =
            valid with
            {
                Rois =
                [
                    firstRoi with
                    {
                        Gates = [.. firstRoi.Gates, retiredAlphaProxy]
                    }
                ]
            };

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateApprovedManifest(valid),
                Throws.Nothing);
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateApprovedManifest(noRois),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("named ROI"));
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateApprovedManifest(relaxed),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("exactly"));
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateApprovedManifest(noTemporalDisposition),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("temporal applicability"));
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateApprovedManifest(incompatibleFlip),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("HDR-FLIP parameters"));
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateApprovedManifest(incompatibleAlpha),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("retired radiance-threshold"));
        });
    }

    [Test]
    public void CaptureCompatibility_RejectsSceneAndReleaseMetadataDrift()
    {
        SampleMaterialGiApprovedHdrReferenceManifest approved =
            CreateManifest(
                SampleMaterialGiConformanceCatalog.LockedWidth,
                SampleMaterialGiConformanceCatalog.LockedHeight,
                includeTemporal: false);
        SampleMaterialGiRunManifest reference = CreateRunManifest(17, "Release");
        SampleMaterialGiRunManifest candidateSceneDrift = CreateRunManifest(18, "Release");
        SampleMaterialGiRunManifest candidateDebug = CreateRunManifest(17, "Debug");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateCaptureCompatibility(
                    approved,
                    reference,
                    CreateRunManifest(17, "Release")),
                Throws.Nothing);
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateCaptureCompatibility(
                    approved,
                    reference,
                    candidateSceneDrift),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("scene content revision"));
            Assert.That(
                () => SampleMaterialGiApprovedHdrComparer.ValidateCaptureCompatibility(
                    approved,
                    reference,
                    candidateDebug),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("Release"));
        });
    }

    [Test]
    public void ApprovedHdrCli_WritesAtomicMachineReadableFailureBeforeVulkan()
    {
        string root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-approved-hdr-cli",
            Guid.NewGuid().ToString("N"));
        string candidate = Path.Combine(root, "candidate");
        string missingApproved = Path.Combine(root, "missing-approved.json");
        string reportPath = Path.Combine(root, "report.json");
        Directory.CreateDirectory(candidate);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            bool handled = SampleMaterialGiApprovedHdrCli.TryRun(
                [
                    SampleMaterialGiApprovedHdrCli.CompareOption,
                    missingApproved,
                    candidate,
                    SampleMaterialGiApprovedHdrCli.ReportOption,
                    reportPath
                ],
                output,
                error,
                out int exitCode);
            using JsonDocument report = JsonDocument.Parse(File.ReadAllBytes(reportPath));

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(exitCode, Is.EqualTo(2));
                Assert.That(error.ToString(), Does.Contain("approved HDR regression failed"));
                Assert.That(
                    report.RootElement.GetProperty("schemaVersion").GetString(),
                    Is.EqualTo(SampleMaterialGiApprovedHdrComparer.ReportSchemaVersion));
                Assert.That(report.RootElement.GetProperty("status").GetString(), Is.EqualTo("failed"));
                Assert.That(
                    report.RootElement.GetProperty("failureReason").GetString(),
                    Does.Contain("reference manifest is missing"));
                Assert.That(
                    report.RootElement.GetProperty("flipConfiguration")
                        .GetProperty("nvidiaFlipVersion").GetString(),
                    Is.EqualTo("1.7"));
                Assert.That(
                    Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories),
                    Is.Empty);
            });

            // Exercise atomic replacement, not only first publication.
            bool handledAgain = SampleMaterialGiApprovedHdrCli.TryRun(
                [
                    SampleMaterialGiApprovedHdrCli.CompareOption,
                    missingApproved,
                    candidate,
                    $"{SampleMaterialGiApprovedHdrCli.ReportOption}={reportPath}"
                ],
                TextWriter.Null,
                TextWriter.Null,
                out int secondExitCode);
            Assert.Multiple(() =>
            {
                Assert.That(handledAgain, Is.True);
                Assert.That(secondExitCode, Is.EqualTo(2));
                Assert.That(File.Exists(reportPath), Is.True);
                Assert.That(
                    Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories),
                    Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ApprovedHdrComparison_FailsClosedOnUnknownReferenceMetadata()
    {
        string root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-approved-hdr-malformed",
            Guid.NewGuid().ToString("N"));
        string candidate = Path.Combine(root, "candidate");
        string approvedPath = Path.Combine(root, "approved.json");
        Directory.CreateDirectory(candidate);
        try
        {
            File.WriteAllText(
                approvedPath,
                $$"""
                {
                  "schemaVersion": "{{SampleMaterialGiApprovedHdrComparer.ManifestSchemaVersion}}",
                  "status": "approved",
                  "unexpectedApprovalBypass": true
                }
                """);

            SampleMaterialGiApprovedHdrRegressionReport report =
                SampleMaterialGiApprovedHdrComparer.Compare(approvedPath, candidate);

            Assert.Multiple(() =>
            {
                Assert.That(report.Passed, Is.False);
                Assert.That(report.Images, Is.Empty);
                Assert.That(report.RoiGates, Is.Empty);
                Assert.That(report.FailureReason, Does.Contain("unknown metadata"));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static SampleMaterialGiApprovedHdrReferenceManifest CreateManifest(
        int width,
        int height,
        bool includeTemporal)
    {
        var bounds = new SampleMaterialGiPixelRegion(0, 0, width, height);
        int leftWidth = Math.Max(1, width / 2);
        int rightWidth = width - leftWidth;
        if (rightWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var gates = new List<SampleMaterialGiApprovedRoiGate>
        {
            new(
                SampleMaterialGiVisualRoiGateKind.UniformLuminance,
                SampleMaterialGiApprovedHdrComparer.MaximumUniformLuminanceDifference,
                SampleMaterialGiCaptureSignal.FinalDdgiDiffuse),
            new(
                SampleMaterialGiVisualRoiGateKind.TransitionStep,
                SampleMaterialGiApprovedHdrComparer.MaximumTransitionStep,
                SampleMaterialGiCaptureSignal.FinalDdgiDiffuse,
                TransitionSamples:
                [
                    new SampleMaterialGiPixelRegion(0, 0, leftWidth, height),
                    new SampleMaterialGiPixelRegion(leftWidth, 0, rightWidth, height)
                ]),
            new(
                SampleMaterialGiVisualRoiGateKind.LowFrequencyMean,
                SampleMaterialGiApprovedHdrComparer.MaximumLowFrequencyMeanDifference,
                SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                ComparisonSignal: SampleMaterialGiCaptureSignal.FinalDdgiDiffuse)
        };
        if (includeTemporal)
        {
            gates.Add(
                new SampleMaterialGiApprovedRoiGate(
                    SampleMaterialGiVisualRoiGateKind.TemporalStability,
                    SampleMaterialGiApprovedHdrComparer.MaximumTemporalP95,
                    SampleMaterialGiCaptureSignal.FinalComposedIndirect,
                    TemporalFrameRelativePaths:
                    [
                        "temporal/frame-000.pfm",
                        "temporal/frame-001.pfm",
                        "temporal/frame-002.pfm"
                    ],
                    TemporalWarmupFrameCount: 0));
        }

        return new SampleMaterialGiApprovedHdrReferenceManifest(
            SampleMaterialGiApprovedHdrComparer.ManifestSchemaVersion,
            "approved",
            SampleMaterialGiConformanceCatalog.Fingerprint,
            SampleMaterialGiHdrFlipMetric.MetricVersion,
            SampleMaterialGiHdrFlipMetric.FixedConfiguration,
            new SampleMaterialGiVisualApproval(
                "VISUAL-2026-0001",
                "Rendering Review",
                new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero),
                "Approved locked Material/GI linear-HDR reference."),
            Path.Combine("reference", SampleMaterialGiArtifactPublisher.ManifestFileName),
            new string('a', 64),
            width,
            height,
            SampleMaterialGiApprovedHdrComparer.MaximumRelativeRmse,
            SampleMaterialGiApprovedHdrComparer.MaximumFlipP95,
            [SampleMaterialGiCaptureSignal.FinalComposedIndirect],
            [new SampleMaterialGiApprovedRoi("uniform-material-and-transition", bounds, gates)],
            includeTemporal
                ? new SampleMaterialGiTemporalPolicy(
                    SampleMaterialGiTemporalApplicability.Required,
                    "Static post-warmup sequence is part of this capture.")
                : new SampleMaterialGiTemporalPolicy(
                    SampleMaterialGiTemporalApplicability.NotApplicable,
                    "This approved still-image reference has no temporal sequence."));
    }

    private static SampleMaterialGiRunManifest CreateRunManifest(
        ulong sceneRevision,
        string buildConfiguration)
    {
        var camera = new PerformanceCaptureCameraMetadata(
            0,
            1,
            2,
            0,
            0,
            1,
            0.05f,
            100f,
            "view",
            "projection",
            0);
        var renderer = new SampleMaterialGiRendererProvenance(
            "GPU",
            "Driver",
            buildConfiguration,
            "1.0",
            "0123456789abcdef0123456789abcdef01234567",
            "sha256:" + new string('b', 64),
            1,
            SampleMaterialGiConformanceCatalog.LockedWidth,
            SampleMaterialGiConformanceCatalog.LockedHeight,
            sceneRevision,
            RenderQualityPreset.DdgiHigh,
            MaterialGiV2Feature.All,
            GlobalIlluminationMode.Ddgi,
            AsyncComputeMode.Disabled,
            AsyncComputeMode.Disabled,
            0,
            camera)
        {
            SettingsFingerprint =
                "sha256:" + new string('c', 64)
        };
        DateTimeOffset started = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        return new SampleMaterialGiRunManifest(
            SampleMaterialGiArtifactPublisher.ManifestSchemaVersion,
            "passed",
            string.Empty,
            SampleMaterialGiConformanceCatalog.Fingerprint,
            started,
            started.AddMinutes(1),
            SampleMaterialGiConformanceCatalog.RequiredOutputs.Count,
            SampleMaterialGiArtifactPublisher.FloatFormat,
            renderer,
            Array.Empty<SampleMaterialGiArtifact>());
    }

    private static SampleMaterialGiArtifact Artifact(
        SampleMaterialGiCaptureSignal signal,
        string path,
        char hashCharacter) =>
        new(
            signal,
            path,
            path + ".pfm",
            new string(hashCharacter, 64),
            1,
            1,
            1,
            0f,
            1f);

    private static LinearFloatImage ConstantImage(
        int width,
        int height,
        float value) =>
        CreateImage(width, height, (_, _) => value);

    private static LinearFloatImage Scale(
        LinearFloatImage source,
        float scale)
    {
        var pixels = new float[source.Pixels.Length];
        for (int index = 0; index < pixels.Length; index++)
            pixels[index] = source.Pixels[index] * scale;
        return new LinearFloatImage(source.Width, source.Height, pixels);
    }

    private static LinearFloatImage CreateImage(
        int width,
        int height,
        Func<int, int, float> value)
    {
        var pixels = new float[checked(width * height * 3)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float component = value(x, y);
                int index = (y * width + x) * 3;
                pixels[index] = component;
                pixels[index + 1] = component;
                pixels[index + 2] = component;
            }
        }
        return new LinearFloatImage(width, height, pixels);
    }

    private static LinearFloatImage CreateRgbImage(
        int width,
        int height,
        Func<int, int, (float Red, float Green, float Blue)> value)
    {
        var pixels = new float[checked(width * height * 3)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (float red, float green, float blue) = value(x, y);
                int index = (y * width + x) * 3;
                pixels[index] = red;
                pixels[index + 1] = green;
                pixels[index + 2] = blue;
            }
        }
        return new LinearFloatImage(width, height, pixels);
    }
}
