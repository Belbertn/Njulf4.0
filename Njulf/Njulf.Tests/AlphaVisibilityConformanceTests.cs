using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AlphaVisibilityConformanceTests
{
    [Test]
    public void Contract_IsLockedDeterministicAndUsesCoveragePreservingMips()
    {
        AlphaVisibilityTextureData first =
            AlphaVisibilityConformanceContract.CreateTextureData();
        AlphaVisibilityTextureData second =
            AlphaVisibilityConformanceContract.CreateTextureData();
        IReadOnlyList<AlphaVisibilityShaderEvidence> shaders =
            AlphaVisibilityConformanceContract.LoadShaderEvidence();

        Assert.Multiple(() =>
        {
            Assert.That(
                AlphaVisibilityConformanceContract.GateId,
                Is.EqualTo("material-gi-alpha-visibility/v1"));
            Assert.That(
                AlphaVisibilityConformanceContract.Distances,
                Is.EqualTo(new[] { 2.0f, 4.0f, 8.0f }));
            Assert.That(
                AlphaVisibilityConformanceContract.MaximumCoverageDifference,
                Is.EqualTo(0.02));
            Assert.That(
                AlphaVisibilityConformanceContract.ContractFingerprint,
                Has.Length.EqualTo(64));
            Assert.That(first.Pixels, Is.EqualTo(second.Pixels));
            Assert.That(first.Sha256, Is.EqualTo(second.Sha256));
            Assert.That(
                first.Sha256,
                Is.EqualTo(
                    Convert.ToHexString(SHA256.HashData(first.Pixels))
                        .ToLowerInvariant()));
            Assert.That(
                first.MipLevels,
                Has.Count.EqualTo(
                    AlphaVisibilityConformanceContract.TextureMipLevelCount));
            Assert.That(
                first.MipLevels.All(
                    mip => Math.Abs(mip.Coverage - first.BaseCoverage) <= 0.02),
                Is.True,
                "Every tested mip must preserve binary alpha coverage within the release threshold.");
            Assert.That(
                shaders.Select(static shader => shader.ResourceName),
                Is.EqualTo(new[]
                {
                    AlphaVisibilityConformanceContract.VertexShaderResourceName,
                    AlphaVisibilityConformanceContract.FragmentShaderResourceName,
                    AlphaVisibilityConformanceContract.RayQueryShaderResourceName
                }));
            Assert.That(shaders, Has.All.Matches<AlphaVisibilityShaderEvidence>(
                shader => shader.ByteLength > 0 && shader.Sha256.Length == 64));
        });
    }

    [Test]
    public void PushConstantAbi_MatchesAllThreeExecutableShaderStages()
    {
        Assert.That(
            Marshal.SizeOf<AlphaVisibilityVulkanHarness.AlphaVisibilityPushConstants>(),
            Is.EqualTo(32));
    }

    [Test]
    [NonParallelizable]
    public void LoaderIsolation_DisablesOnlyImplicitLayersAndRestoresProcessValues()
    {
        const string existingDisableValue = "*capture*";
        const string existingPathValue = "C:\\existing-implicit-layers";
        string disableVariable =
            AlphaVisibilityVulkanHarness.LoaderLayersDisableEnvironmentVariable;
        string pathVariable =
            AlphaVisibilityVulkanHarness
                .LoaderImplicitLayerPathEnvironmentVariable;
        string? originalDisableValue = Environment.GetEnvironmentVariable(
            disableVariable,
            EnvironmentVariableTarget.Process);
        string? originalPathValue = Environment.GetEnvironmentVariable(
            pathVariable,
            EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(
                disableVariable,
                existingDisableValue,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                pathVariable,
                existingPathValue,
                EnvironmentVariableTarget.Process);
            string isolatedPath;
            using (AlphaVisibilityVulkanHarness.BeginLoaderLayerIsolation())
            {
                isolatedPath = Environment.GetEnvironmentVariable(
                    pathVariable,
                    EnvironmentVariableTarget.Process)!;
                Assert.That(
                    Environment.GetEnvironmentVariable(
                        disableVariable,
                        EnvironmentVariableTarget.Process),
                    Is.EqualTo(
                        AlphaVisibilityVulkanHarness
                            .DisableImplicitLoaderLayersFilter));
                Assert.That(isolatedPath, Is.Not.EqualTo(existingPathValue));
                Assert.That(Directory.Exists(isolatedPath), Is.True);
                Assert.That(
                    Directory.EnumerateFileSystemEntries(isolatedPath),
                    Is.Empty);
            }
            Assert.Multiple(() =>
            {
                Assert.That(
                    Environment.GetEnvironmentVariable(
                        disableVariable,
                        EnvironmentVariableTarget.Process),
                    Is.EqualTo(existingDisableValue));
                Assert.That(
                    Environment.GetEnvironmentVariable(
                        pathVariable,
                        EnvironmentVariableTarget.Process),
                    Is.EqualTo(existingPathValue));
                Assert.That(Directory.Exists(isolatedPath), Is.False);
            });

            Environment.SetEnvironmentVariable(
                disableVariable,
                null,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                pathVariable,
                null,
                EnvironmentVariableTarget.Process);
            using (AlphaVisibilityVulkanHarness.BeginLoaderLayerIsolation())
            {
                Assert.That(
                    Environment.GetEnvironmentVariable(
                        disableVariable,
                        EnvironmentVariableTarget.Process),
                    Is.EqualTo(
                        AlphaVisibilityVulkanHarness
                            .DisableImplicitLoaderLayersFilter));
                Assert.That(
                    Environment.GetEnvironmentVariable(
                        pathVariable,
                        EnvironmentVariableTarget.Process),
                    Is.Not.Null.And.Not.Empty);
            }
            Assert.Multiple(() =>
            {
                Assert.That(
                    Environment.GetEnvironmentVariable(
                        disableVariable,
                        EnvironmentVariableTarget.Process),
                    Is.Null);
                Assert.That(
                    Environment.GetEnvironmentVariable(
                        pathVariable,
                        EnvironmentVariableTarget.Process),
                    Is.Null);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                disableVariable,
                originalDisableValue,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                pathVariable,
                originalPathValue,
                EnvironmentVariableTarget.Process);
        }
    }

    [Test]
    public void Evaluator_AcceptsAtTwoPercentAndRejectsAnyLargerDifference()
    {
        AlphaVisibilityRawEvidence atLimit = CreateEvidence(
            rasterCandidates: 10_000,
            rasterCovered: 5_000,
            rayCandidates: 10_000,
            rayCovered: 5_200);
        AlphaVisibilityRawEvidence overLimit = CreateEvidence(
            rasterCandidates: 10_000,
            rasterCovered: 5_000,
            rayCandidates: 10_000,
            rayCovered: 5_201);

        IReadOnlyList<AlphaVisibilityDistanceResult> accepted =
            AlphaVisibilityConformanceEvaluator.Evaluate(atLimit);
        IReadOnlyList<AlphaVisibilityDistanceResult> rejected =
            AlphaVisibilityConformanceEvaluator.Evaluate(overLimit);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Has.All.Property(nameof(AlphaVisibilityDistanceResult.Passed)).True);
            Assert.That(
                accepted,
                Has.All.Property(nameof(AlphaVisibilityDistanceResult.AbsoluteCoverageDifference))
                    .EqualTo(0.02).Within(1e-12));
            Assert.That(rejected, Has.All.Property(nameof(AlphaVisibilityDistanceResult.Passed)).False);
            Assert.That(
                rejected,
                Has.All.Property(nameof(AlphaVisibilityDistanceResult.AbsoluteCoverageDifference))
                    .GreaterThan(0.02));
        });
    }

    [Test]
    public void Evaluator_FailsClosedForInsufficientCandidateGeometry()
    {
        AlphaVisibilityRawEvidence evidence = CreateEvidence(
            rasterCandidates:
                AlphaVisibilityConformanceContract.MinimumCandidateSamples - 1,
            rasterCovered: 500,
            rayCandidates:
                AlphaVisibilityConformanceContract.MinimumCandidateSamples - 1,
            rayCovered: 500);

        Assert.That(
            AlphaVisibilityConformanceEvaluator.Evaluate(evidence),
            Has.All.Property(nameof(AlphaVisibilityDistanceResult.Passed)).False);
    }

    [Test]
    public void EvidenceCodec_RoundTripsExactBinaryMasksAndRejectsTamper()
    {
        AlphaVisibilityRawEvidence evidence = CreateEvidence(
            rasterCandidates: 12_345,
            rasterCovered: 6_321,
            rayCandidates: 12_300,
            rayCovered: 6_290);
        byte[] encoded = AlphaVisibilityEvidenceCodec.Encode(evidence);
        AlphaVisibilityRawEvidence decoded =
            AlphaVisibilityEvidenceCodec.Decode(encoded);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.RasterCandidates, Is.EqualTo(evidence.RasterCandidates));
            Assert.That(decoded.RasterCovered, Is.EqualTo(evidence.RasterCovered));
            Assert.That(decoded.RayCandidates, Is.EqualTo(evidence.RayCandidates));
            Assert.That(decoded.RayCovered, Is.EqualTo(evidence.RayCovered));
            Assert.That(
                () => AlphaVisibilityEvidenceCodec.Decode(encoded[..^1]),
                Throws.TypeOf<InvalidDataException>());
        });

        byte[] badMagic = (byte[])encoded.Clone();
        badMagic[0] ^= 0xff;
        Assert.That(
            () => AlphaVisibilityEvidenceCodec.Decode(badMagic),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void RawEvidence_RejectsNonBinaryAndCoveredOutsideCandidate()
    {
        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        var candidates = new byte[samples];
        var covered = new byte[samples];
        candidates[0] = 1;
        covered[1] = 1;

        Assert.That(
            () => AlphaVisibilityRawEvidence.CreateValidated(
                candidates,
                covered,
                candidates,
                candidates),
            Throws.TypeOf<InvalidDataException>());

        candidates[2] = 2;
        Assert.That(
            () => AlphaVisibilityRawEvidence.CreateValidated(
                candidates,
                new byte[samples],
                new byte[samples],
                new byte[samples]),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void PassedReport_AuthenticatesExactShadersTextureAndRawEvidence()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string reportPath = Path.Combine(temporary.Path, "alpha-report.json");
        string evidencePath = Path.Combine(temporary.Path, "alpha-evidence.bin");
        AlphaVisibilityRawEvidence raw = CreateEvidence(
            rasterCandidates: 10_000,
            rasterCovered: 5_000,
            rayCandidates: 10_000,
            rayCovered: 5_100);
        byte[] evidence = AlphaVisibilityEvidenceCodec.Encode(raw);
        AlphaVisibilityHardwareOutput hardware = CreateHardware(raw);
        AlphaVisibilityConformanceReport report =
            AlphaVisibilityConformanceReports.Create(
                new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 29, 8, 0, 1, TimeSpan.Zero),
                hardware,
                Path.GetFileName(evidencePath),
                evidence);

        AlphaVisibilityConformanceReports.WriteEvidenceAtomically(
            evidencePath,
            evidence);
        AlphaVisibilityConformanceReports.WriteAtomically(reportPath, report);
        AlphaVisibilityConformanceReport authenticated =
            AlphaVisibilityConformanceReports.AuthenticatePassed(
                reportPath,
                evidencePath);

        Assert.Multiple(() =>
        {
            Assert.That(authenticated.Status, Is.EqualTo("Passed"));
            Assert.That(authenticated.Distances, Has.Count.EqualTo(3));
            Assert.That(authenticated.Distances, Has.All.Property(nameof(AlphaVisibilityDistanceResult.Passed)).True);
            Assert.That(authenticated.EvidenceAuthenticationSha256, Has.Length.EqualTo(64));
            Assert.That(
                Directory.EnumerateFiles(temporary.Path, "*.tmp"),
                Is.Empty);
        });
    }

    [Test]
    public void Report_FailsClosedAndRetainsStructuredValidationCallbacks()
    {
        AlphaVisibilityRawEvidence raw = CreateEvidence(
            rasterCandidates: 10_000,
            rasterCovered: 5_000,
            rayCandidates: 10_000,
            rayCovered: 5_100);
        byte[] evidence = AlphaVisibilityEvidenceCodec.Encode(raw);
        const string exactMessage =
            "loader_get_json: Failed to open JSON file D:\\missing-layer.json";
        var callback = new AlphaVisibilityValidationMessage(
            "Error",
            MessageTypes: 3,
            MessageIdNumber: -1,
            MessageIdName: "Vulkan Loader Message",
            Message: exactMessage,
            TextTruncated: false);
        AlphaVisibilityHardwareOutput hardware = CreateHardware(raw) with
        {
            ValidationErrorCount = 1,
            FirstValidationError = exactMessage,
            ValidationMessages = [callback]
        };

        AlphaVisibilityConformanceReport report =
            AlphaVisibilityConformanceReports.Create(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                hardware,
                "alpha-evidence.bin",
                evidence);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.SchemaVersion,
                Is.EqualTo(
                    AlphaVisibilityConformanceContract.ReportSchemaVersion));
            Assert.That(report.Status, Is.EqualTo("Failed"));
            Assert.That(report.ValidationErrorCount, Is.EqualTo(1));
            Assert.That(report.FirstValidationError, Is.EqualTo(exactMessage));
            Assert.That(report.ValidationMessages, Is.EqualTo(new[] { callback }));
            Assert.That(report.ValidationMessagesTruncated, Is.False);
            Assert.That(
                report.Failures,
                Has.Some.Contains("1 error"));
        });
    }

    [Test]
    public void Authentication_RejectsArtifactAndReportTamper()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string reportPath = Path.Combine(temporary.Path, "alpha-report.json");
        string evidencePath = Path.Combine(temporary.Path, "alpha-evidence.bin");
        AlphaVisibilityRawEvidence raw = CreateEvidence(
            rasterCandidates: 10_000,
            rasterCovered: 5_000,
            rayCandidates: 10_000,
            rayCovered: 5_100);
        byte[] evidence = AlphaVisibilityEvidenceCodec.Encode(raw);
        AlphaVisibilityConformanceReport report =
            AlphaVisibilityConformanceReports.Create(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                CreateHardware(raw),
                Path.GetFileName(evidencePath),
                evidence);
        AlphaVisibilityConformanceReports.WriteEvidenceAtomically(
            evidencePath,
            evidence);
        AlphaVisibilityConformanceReports.WriteAtomically(reportPath, report);

        byte[] tamperedEvidence = (byte[])evidence.Clone();
        tamperedEvidence[^1] ^= 1;
        File.WriteAllBytes(evidencePath, tamperedEvidence);
        Assert.That(
            () => AlphaVisibilityConformanceReports.AuthenticatePassed(
                reportPath,
                evidencePath),
            Throws.TypeOf<InvalidDataException>());

        AlphaVisibilityConformanceReports.WriteEvidenceAtomically(
            evidencePath,
            evidence);
        var tamperedReport = report with
        {
            MaximumCoverageDifference = 0.021
        };
        AlphaVisibilityConformanceReports.WriteAtomically(
            reportPath,
            tamperedReport);
        Assert.That(
            () => AlphaVisibilityConformanceReports.AuthenticatePassed(
                reportPath,
                evidencePath),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void FailedReport_CannotAuthenticateAsReleaseEvidence()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string reportPath = Path.Combine(temporary.Path, "failed.json");
        string evidencePath = Path.Combine(temporary.Path, "unused.bin");
        AlphaVisibilityConformanceReport failed =
            AlphaVisibilityConformanceReports.CreateFailed(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "No ray-query device.");
        AlphaVisibilityConformanceReports.WriteAtomically(reportPath, failed);
        File.WriteAllBytes(evidencePath, [1]);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Status, Is.EqualTo("Failed"));
            Assert.That(failed.Evidence, Is.Null);
            Assert.That(failed.Failures, Has.Count.EqualTo(1));
            Assert.That(
                () => AlphaVisibilityConformanceReports.AuthenticatePassed(
                    reportPath,
                    evidencePath),
                Throws.TypeOf<InvalidDataException>());
        });
    }

    [Test]
    public void StrictJson_RejectsUnknownEvidenceClaims()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string reportPath = Path.Combine(temporary.Path, "unknown.json");
        File.WriteAllText(
            reportPath,
            """
            {
              "schemaVersion": 1,
              "gateId": "material-gi-alpha-visibility/v1",
              "status": "Passed",
              "unknownAcceptanceClaim": true
            }
            """);

        Assert.That(
            () => AlphaVisibilityConformanceReports.ReadReport(reportPath),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void StrictJson_RejectsDuplicateClaimsAndOversizedReports()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string reportPath = Path.Combine(temporary.Path, "ambiguous.json");
        File.WriteAllText(
            reportPath,
            """
            {
              "schemaVersion": 2,
              "schemaVersion": 2,
              "gateId": "material-gi-alpha-visibility/v1",
              "status": "Passed"
            }
            """);
        Assert.That(
            () => AlphaVisibilityConformanceReports.ReadReport(reportPath),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("duplicate JSON property"));

        File.WriteAllBytes(
            reportPath,
            new byte[
                AlphaVisibilityConformanceContract.MaximumReportBytes + 1]);
        Assert.That(
            () => AlphaVisibilityConformanceReports.ReadReport(reportPath),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("invalid bounded length"));
    }

    [Test]
    [Explicit("Runs the standalone Vulkan raster/ray-query acceptance workload.")]
    public void HardwareGate_ProducesAuthenticCoverageEvidence()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        AlphaVisibilityHardwareOutput hardware =
            AlphaVisibilityVulkanHarness.Run();
        AlphaVisibilityRawEvidence raw =
            AlphaVisibilityRawEvidence.FromGpuWords(hardware.ResultWords);
        byte[] evidence = AlphaVisibilityEvidenceCodec.Encode(raw);
        string evidencePath = Path.Combine(temporary.Path, "hardware.bin");
        string reportPath = Path.Combine(temporary.Path, "hardware.json");
        AlphaVisibilityConformanceReport report =
            AlphaVisibilityConformanceReports.Create(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                hardware,
                Path.GetFileName(evidencePath),
                evidence);
        AlphaVisibilityConformanceReports.WriteEvidenceAtomically(evidencePath, evidence);
        AlphaVisibilityConformanceReports.WriteAtomically(reportPath, report);

        Assert.That(report.Status, Is.EqualTo("Passed"), string.Join(Environment.NewLine, report.Failures));
        Assert.That(
            () => AlphaVisibilityConformanceReports.AuthenticatePassed(
                reportPath,
                evidencePath),
            Throws.Nothing);
    }

    private static AlphaVisibilityHardwareOutput CreateHardware(
        AlphaVisibilityRawEvidence raw)
    {
        uint[] words = ToWords(raw);
        return new AlphaVisibilityHardwareOutput(
            "Deterministic Vulkan Qualification Device",
            0x0040_3000,
            1,
            ValidationEnabled: true,
            ValidationWarningCount: 0,
            ValidationErrorCount: 0,
            FirstValidationError: string.Empty,
            ValidationMessages: Array.Empty<AlphaVisibilityValidationMessage>(),
            ValidationMessagesTruncated: false,
            words);
    }

    private static uint[] ToWords(AlphaVisibilityRawEvidence raw)
    {
        var words =
            new uint[AlphaVisibilityConformanceContract.ResultWordCount];
        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        Copy(raw.RasterCandidates, words, 0);
        Copy(raw.RasterCovered, words, samples);
        Copy(raw.RayCandidates, words, samples * 2);
        Copy(raw.RayCovered, words, samples * 3);
        return words;
    }

    private static void Copy(
        ReadOnlySpan<byte> source,
        Span<uint> destination,
        int destinationOffset)
    {
        for (int index = 0; index < source.Length; index++)
            destination[destinationOffset + index] = source[index];
    }

    private static AlphaVisibilityRawEvidence CreateEvidence(
        int rasterCandidates,
        int rasterCovered,
        int rayCandidates,
        int rayCovered)
    {
        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        int samplesPerDistance =
            AlphaVisibilityConformanceContract.SamplesPerDistance;
        var rasterCandidatePlane = new byte[samples];
        var rasterCoveredPlane = new byte[samples];
        var rayCandidatePlane = new byte[samples];
        var rayCoveredPlane = new byte[samples];
        for (int distance = 0;
             distance < AlphaVisibilityConformanceContract.Distances.Count;
             distance++)
        {
            int offset = distance * samplesPerDistance;
            rasterCandidatePlane.AsSpan(offset, rasterCandidates).Fill(1);
            rasterCoveredPlane.AsSpan(offset, rasterCovered).Fill(1);
            rayCandidatePlane.AsSpan(offset, rayCandidates).Fill(1);
            rayCoveredPlane.AsSpan(offset, rayCovered).Fill(1);
        }
        return AlphaVisibilityRawEvidence.CreateValidated(
            rasterCandidatePlane,
            rasterCoveredPlane,
            rayCandidatePlane,
            rayCoveredPlane);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "njulf-alpha-visibility-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
