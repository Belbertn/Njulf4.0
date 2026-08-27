using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBistroReflectionQualificationTests
{
    private static readonly string[] ArtifactNames =
    [
        "000-beauty",
        "059-beauty",
        "060-beauty",
        "061-beauty",
        "068-beauty",
        "076-beauty",
        "179-beauty",
        "180-beauty",
        "181-beauty",
        "239-beauty"
    ];

    [Test]
    public void Evaluate_QualifiesReflectionEvidenceIndependentlyOfDdgiGate()
    {
        SampleBistroQualityRunReport report = CreateReport();

        SampleBistroReflectionQualificationResult result =
            SampleBistroReflectionQualification.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True,
                string.Join(Environment.NewLine, result.Failures));
            Assert.That(result.BistroRunStatus, Is.EqualTo("failed"));
            Assert.That(result.ValidTelemetryFrameCount,
                Is.EqualTo(SampleBistroQualityCaptureContract.LoopFrameCount));
            Assert.That(result.SsrHitCount, Is.GreaterThan(0));
            Assert.That(result.RayQueryRequestCount, Is.GreaterThan(0));
            Assert.That(result.RayQueryHitCount, Is.GreaterThan(0));
            Assert.That(result.RayQueryOverflowCount, Is.Zero);
            Assert.That(result.DdgiFallbackCount, Is.GreaterThan(0));
            Assert.That(result.ProbeFallbackCount, Is.Zero);
            Assert.That(result.SortedAlphaTelemetryFrameCount,
                Is.GreaterThan(0));
            Assert.That(result.WeightedOitTelemetryFrameCount,
                Is.GreaterThan(0));
            Assert.That(result.TransparentSsrHitCount, Is.GreaterThan(0));
            Assert.That(result.TransparentRayAdmittedCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Evaluate_RejectsProbeFallbackAndOffWindowRayWork()
    {
        SampleBistroQualityRunReport report = CreateReport();
        SampleBistroQualityFrameTelemetry[] frames = report.Frames.ToArray();
        frames[20] = frames[20] with
        {
            HybridReflectionRayQueryRequestCount = 1,
            HybridReflectionRayQueryCount = 1,
            HybridReflectionRayQueryHitCount = 1,
            GpuHybridReflectionRayQueryMicroseconds = 1
        };
        frames[100] = frames[100] with
        {
            HybridReflectionProbeFallbackCount = 1
        };

        SampleBistroReflectionQualificationResult result =
            SampleBistroReflectionQualification.Evaluate(
                report with { Frames = frames });

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(result.Failures, Has.Some.Contains(
                "off windows still recorded ray-query work"));
            Assert.That(result.Failures, Has.Some.Contains(
                "manual reflection probe path"));
        });
    }

    [Test]
    public void Cli_AuthenticatesEveryCapturedArtifact()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"njulf-reflection-qualification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "frames"));
        try
        {
            SampleBistroQualityArtifact[] artifacts = ArtifactNames
                .Select(name => CreateArtifact(directory, name))
                .ToArray();
            SampleBistroQualityRunReport report = CreateReport() with
            {
                Artifacts = artifacts
            };
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            File.WriteAllBytes(
                Path.Combine(directory, "bistro-quality-run.json"),
                JsonSerializer.SerializeToUtf8Bytes(report, jsonOptions));
            var output = new StringWriter();
            var error = new StringWriter();

            bool handled = SampleBistroReflectionQualificationCli.TryRun(
                ["--analyze-bistro-reflection-run", directory],
                output,
                error,
                out int exitCode);

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(exitCode, Is.Zero, error.ToString());
                Assert.That(output.ToString(), Does.Contain(
                    "Bistro reflection qualification passed"));
                Assert.That(File.Exists(Path.Combine(
                    directory,
                    "reflection-qualification.json")), Is.True);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SampleBistroQualityRunReport CreateReport()
    {
        var contract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.HybridRayQueryAb);
        SampleBistroQualityFrameTelemetry empty =
            JsonSerializer.Deserialize<SampleBistroQualityFrameTelemetry>(
                "{}")!;
        SampleBistroQualityFrameTelemetry[] frames = Enumerable.Range(
                0,
                SampleBistroQualityCaptureContract.LoopFrameCount)
            .Select(index =>
            {
                SampleBistroQualityFrameState state = contract.ResolveFrame(
                    SampleBistroQualityCaptureContract.FirstMeasuredFrame +
                    index);
                bool rayEvidence = index is >= 68 and <= 175;
                return empty with
                {
                    AbsoluteFrameIndex = state.AbsoluteFrameIndex,
                    LoopFrameIndex = state.LoopFrameIndex,
                    HybridRayQueryEnabled = state.HybridRayQueryEnabled,
                    TransparencyMode = state.TransparencyMode,
                    HybridReflectionCountersReadbackValid = 1,
                    HybridReflectionSsrHitCount = 1,
                    HybridReflectionRayQueryRequestCount =
                        rayEvidence ? 3u : 0u,
                    HybridReflectionRayQueryCount = rayEvidence ? 3u : 0u,
                    HybridReflectionRayQueryHitCount = rayEvidence ? 2u : 0u,
                    HybridReflectionRayQueryMissCount = rayEvidence ? 1u : 0u,
                    HybridReflectionDdgiFallbackCount = 2,
                    HybridReflectionEnvironmentFallbackCount = 1,
                    GpuHybridReflectionSsrMicroseconds = 1,
                    GpuHybridReflectionRayQueryMicroseconds =
                        rayEvidence ? 1 : 0,
                    GpuHybridReflectionDdgiBaseMicroseconds = 1,
                    GpuHybridReflectionResolveMicroseconds = 1,
                    GpuHybridReflectionTemporalMicroseconds = 1,
                    GpuHybridReflectionSpatialMicroseconds = 1,
                    GpuHybridReflectionCompositeMicroseconds = 1,
                    TransparentSceneReflectionSsrSampleBudget = 4_194_304,
                    TransparentReflectionExactSsrEligibleCount = 3,
                    TransparentReflectionExactSsrAdmittedCount = 2,
                    TransparentReflectionExactSsrReservedSampleCount = 130,
                    TransparentReflectionExactSsrActualSampleCount = 96,
                    TransparentReflectionExactSsrHitCount = 1,
                    TransparentReflectionExactSsrBudgetRejectedCount = 1,
                    TransparentReflectionRayRequestCount =
                        rayEvidence ? 3u : 0u,
                    TransparentReflectionExactRayAdmittedCount =
                        rayEvidence ? 3u : 0u
                };
            })
            .ToArray();
        SampleBistroQualityArtifact[] artifacts = ArtifactNames
            .Select(name => new SampleBistroQualityArtifact(
                name,
                Path.Combine("frames", $"{name}.renderer.png"),
                1,
                new string('0', 64)))
            .ToArray();
        return new SampleBistroQualityRunReport(
            "njulf-bistro-quality-capture",
            SampleBistroQualityCaptureContract.Schema,
            DateTimeOffset.UnixEpoch,
            "failed",
            contract.Variant,
            contract.Fingerprint,
            contract.CameraPathFingerprint,
            contract.LightingScriptFingerprint,
            SampleBistroQualityCaptureContract.Width,
            SampleBistroQualityCaptureContract.Height,
            SampleBistroQualityCaptureContract.FramesPerSecond,
            frames,
            artifacts,
            null,
            "An unrelated DDGI scrolling gate failed.");
    }

    private static SampleBistroQualityArtifact CreateArtifact(
        string directory,
        string name)
    {
        string relativePath = Path.Combine(
            "frames",
            $"{name}.renderer.png");
        string path = Path.Combine(directory, relativePath);
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        File.WriteAllBytes(path, bytes);
        return new SampleBistroQualityArtifact(
            name,
            relativePath,
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
