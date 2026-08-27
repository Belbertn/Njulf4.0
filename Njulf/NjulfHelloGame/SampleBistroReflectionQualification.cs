using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NjulfHelloGame;

public sealed record SampleBistroReflectionQualificationResult
{
    public const string CurrentSchema = "bistro-reflection-qualification/v1";

    public string Schema { get; init; } = CurrentSchema;
    public bool Passed { get; init; }
    public string SourceReportPath { get; init; } = string.Empty;
    public string SourceReportSha256 { get; init; } = string.Empty;
    public string BistroRunStatus { get; init; } = string.Empty;
    public IReadOnlyList<string> BistroRunFailures { get; init; } = [];
    public int FrameCount { get; init; }
    public int ValidTelemetryFrameCount { get; init; }
    public int RayQueryOffFrameCount { get; init; }
    public int RayQueryOnFrameCount { get; init; }
    public ulong SsrHitCount { get; init; }
    public ulong RayQueryRequestCount { get; init; }
    public ulong RayQueryCount { get; init; }
    public ulong RayQueryHitCount { get; init; }
    public ulong RayQueryMissCount { get; init; }
    public ulong RayQueryOverflowCount { get; init; }
    public ulong DdgiFallbackCount { get; init; }
    public ulong ProbeFallbackCount { get; init; }
    public ulong EnvironmentFallbackCount { get; init; }
    public long SsrGpuMicroseconds { get; init; }
    public long RayQueryGpuMicroseconds { get; init; }
    public long DdgiBaseGpuMicroseconds { get; init; }
    public long ResolveGpuMicroseconds { get; init; }
    public long TemporalGpuMicroseconds { get; init; }
    public long SpatialGpuMicroseconds { get; init; }
    public long CompositeGpuMicroseconds { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = [];
}

/// <summary>
/// Reflection-only qualification for the deterministic Bistro HybridRayQuery
/// A/B run. The broader Bistro harness also owns DDGI scrolling/tail gates;
/// those remain visible in the result but cannot mask reflection evidence.
/// </summary>
public static class SampleBistroReflectionQualification
{
    private const int MinimumValidTelemetryFrames =
        SampleBistroQualityCaptureContract.LoopFrameCount - 4;
    private const int EarlyOffLastFrame = 55;
    private const int OnFirstFrame = 68;
    private const int OnLastFrame = 175;
    private const int LateOffFirstFrame = 185;
    private static readonly string[] ExpectedArtifacts =
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

    public static SampleBistroReflectionQualificationResult Evaluate(
        SampleBistroQualityRunReport report,
        string? runDirectory = null,
        string sourceReportPath = "",
        string sourceReportSha256 = "")
    {
        ArgumentNullException.ThrowIfNull(report);
        var failures = new List<string>();
        IReadOnlyList<SampleBistroQualityFrameTelemetry> frames =
            report.Frames ?? [];

        if (!string.Equals(
                report.Kind,
                "njulf-bistro-quality-capture",
                StringComparison.Ordinal))
        {
            failures.Add("The input is not a Bistro quality capture report.");
        }
        if (!string.Equals(
                report.Schema,
                SampleBistroQualityCaptureContract.Schema,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"Bistro report schema '{report.Schema}' is not " +
                $"'{SampleBistroQualityCaptureContract.Schema}'.");
        }
        if (report.Variant !=
            SampleBistroQualityCaptureVariant.HybridRayQueryAb)
        {
            failures.Add(
                "Reflection qualification requires the HybridRayQueryAb variant.");
        }
        if (report.Width != SampleBistroQualityCaptureContract.Width ||
            report.Height != SampleBistroQualityCaptureContract.Height ||
            report.FramesPerSecond !=
                SampleBistroQualityCaptureContract.FramesPerSecond)
        {
            failures.Add("The Bistro capture dimensions or frame rate changed.");
        }
        if (frames.Count != SampleBistroQualityCaptureContract.LoopFrameCount)
        {
            failures.Add(
                $"Expected {SampleBistroQualityCaptureContract.LoopFrameCount} " +
                $"measured frames, found {frames.Count}.");
        }

        int deterministicFrameCount = Math.Min(
            frames.Count,
            SampleBistroQualityCaptureContract.LoopFrameCount);
        var contract = new SampleBistroQualityCaptureContract(
            SampleBistroQualityCaptureVariant.HybridRayQueryAb);
        for (int index = 0; index < deterministicFrameCount; index++)
        {
            SampleBistroQualityFrameTelemetry frame = frames[index];
            SampleBistroQualityFrameState expected = contract.ResolveFrame(
                SampleBistroQualityCaptureContract.FirstMeasuredFrame + index);
            if (frame.AbsoluteFrameIndex != expected.AbsoluteFrameIndex ||
                frame.LoopFrameIndex != expected.LoopFrameIndex ||
                frame.HybridRayQueryEnabled != expected.HybridRayQueryEnabled)
            {
                failures.Add(
                    $"Frame {index} does not match the deterministic " +
                    "HybridRayQuery A/B schedule.");
                break;
            }
        }

        SampleBistroQualityFrameTelemetry[] valid = frames
            .Where(static frame =>
                frame.HybridReflectionCountersReadbackValid != 0)
            .ToArray();
        if (valid.Length < MinimumValidTelemetryFrames)
        {
            failures.Add(
                $"Only {valid.Length} reflection telemetry frames were valid; " +
                $"at least {MinimumValidTelemetryFrames} are required.");
        }

        SampleBistroQualityFrameTelemetry[] off = valid
            .Where(static frame =>
                frame.LoopFrameIndex <= EarlyOffLastFrame ||
                frame.LoopFrameIndex >= LateOffFirstFrame)
            .ToArray();
        SampleBistroQualityFrameTelemetry[] on = valid
            .Where(static frame =>
                frame.LoopFrameIndex >= OnFirstFrame &&
                frame.LoopFrameIndex <= OnLastFrame)
            .ToArray();
        if (off.Length == 0 || on.Length == 0)
            failures.Add("The stable ray-query off/on windows are incomplete.");
        if (off.Any(static frame => frame.HybridRayQueryEnabled))
            failures.Add("A stable off-window frame enabled hybrid ray queries.");
        if (on.Any(static frame => !frame.HybridRayQueryEnabled))
            failures.Add("A stable on-window frame disabled hybrid ray queries.");

        ulong offRequests = Sum(
            off,
            static frame => frame.HybridReflectionRayQueryRequestCount);
        ulong offQueries = Sum(
            off,
            static frame => frame.HybridReflectionRayQueryCount);
        ulong offHits = Sum(
            off,
            static frame => frame.HybridReflectionRayQueryHitCount);
        long offRayGpu = Sum(
            off,
            static frame => frame.GpuHybridReflectionRayQueryMicroseconds);
        if (offRequests != 0 || offQueries != 0 || offHits != 0 ||
            offRayGpu != 0)
        {
            failures.Add(
                "The stable ray-query off windows still recorded ray-query work.");
        }

        ulong ssrHits = Sum(
            valid,
            static frame => frame.HybridReflectionSsrHitCount);
        ulong rayRequests = Sum(
            on,
            static frame => frame.HybridReflectionRayQueryRequestCount);
        ulong rayQueries = Sum(
            on,
            static frame => frame.HybridReflectionRayQueryCount);
        ulong rayHits = Sum(
            on,
            static frame => frame.HybridReflectionRayQueryHitCount);
        ulong rayMisses = Sum(
            on,
            static frame => frame.HybridReflectionRayQueryMissCount);
        ulong rayOverflows = Sum(
            valid,
            static frame => frame.HybridReflectionRayQueryOverflowCount);
        ulong ddgiFallbacks = Sum(
            valid,
            static frame => frame.HybridReflectionDdgiFallbackCount);
        ulong probeFallbacks = Sum(
            valid,
            static frame => frame.HybridReflectionProbeFallbackCount);
        ulong environmentFallbacks = Sum(
            valid,
            static frame => frame.HybridReflectionEnvironmentFallbackCount);

        long ssrGpu = Sum(
            valid,
            static frame => frame.GpuHybridReflectionSsrMicroseconds);
        long rayGpu = Sum(
            on,
            static frame => frame.GpuHybridReflectionRayQueryMicroseconds);
        long ddgiGpu = Sum(
            valid,
            static frame => frame.GpuHybridReflectionDdgiBaseMicroseconds);
        long resolveGpu = Sum(
            valid,
            static frame => frame.GpuHybridReflectionResolveMicroseconds);
        long temporalGpu = Sum(
            valid,
            static frame => frame.GpuHybridReflectionTemporalMicroseconds);
        long spatialGpu = Sum(
            valid,
            static frame => frame.GpuHybridReflectionSpatialMicroseconds);
        long compositeGpu = Sum(
            valid,
            static frame => frame.GpuHybridReflectionCompositeMicroseconds);

        if (ssrHits == 0 || ssrGpu <= 0)
            failures.Add("SSR produced no hits or GPU work.");
        if (rayRequests == 0 || rayQueries == 0 || rayHits == 0 ||
            rayGpu <= 0)
        {
            failures.Add("The ray-query on window produced no useful ray work.");
        }
        if (rayQueries != rayHits + rayMisses)
            failures.Add("Ray-query hit/miss accounting is inconsistent.");
        if (rayOverflows != 0)
            failures.Add($"Hybrid ray queries overflowed {rayOverflows} times.");
        if (ddgiFallbacks == 0 || ddgiGpu <= 0)
            failures.Add("DDGI supplied no reflection base radiance or GPU work.");
        if (probeFallbacks != 0 ||
            valid.Any(static frame => frame.ReflectionProbeCount != 0))
        {
            failures.Add(
                "The probe-free Bistro run used a manual reflection probe path.");
        }
        if (resolveGpu <= 0 || temporalGpu <= 0 || spatialGpu <= 0 ||
            compositeGpu <= 0)
        {
            failures.Add(
                "One or more hybrid reflection resolve/filter/composite stages " +
                "recorded no GPU work.");
        }

        ValidateArtifacts(report.Artifacts ?? [], runDirectory, failures);

        return new SampleBistroReflectionQualificationResult
        {
            Passed = failures.Count == 0,
            SourceReportPath = sourceReportPath,
            SourceReportSha256 = sourceReportSha256,
            BistroRunStatus = report.Status,
            BistroRunFailures = report.Gate?.Failures?.ToArray() ??
                (string.IsNullOrWhiteSpace(report.Failure)
                    ? []
                    : [report.Failure]),
            FrameCount = frames.Count,
            ValidTelemetryFrameCount = valid.Length,
            RayQueryOffFrameCount = off.Length,
            RayQueryOnFrameCount = on.Length,
            SsrHitCount = ssrHits,
            RayQueryRequestCount = rayRequests,
            RayQueryCount = rayQueries,
            RayQueryHitCount = rayHits,
            RayQueryMissCount = rayMisses,
            RayQueryOverflowCount = rayOverflows,
            DdgiFallbackCount = ddgiFallbacks,
            ProbeFallbackCount = probeFallbacks,
            EnvironmentFallbackCount = environmentFallbacks,
            SsrGpuMicroseconds = ssrGpu,
            RayQueryGpuMicroseconds = rayGpu,
            DdgiBaseGpuMicroseconds = ddgiGpu,
            ResolveGpuMicroseconds = resolveGpu,
            TemporalGpuMicroseconds = temporalGpu,
            SpatialGpuMicroseconds = spatialGpu,
            CompositeGpuMicroseconds = compositeGpu,
            Failures = failures.ToArray()
        };
    }

    private static void ValidateArtifacts(
        IReadOnlyList<SampleBistroQualityArtifact> artifacts,
        string? runDirectory,
        ICollection<string> failures)
    {
        string[] actualNames = artifacts.Select(static artifact => artifact.Name)
            .ToArray();
        if (!actualNames.SequenceEqual(ExpectedArtifacts, StringComparer.Ordinal))
        {
            failures.Add("The deterministic Bistro beauty artifact set changed.");
            return;
        }

        string? root = string.IsNullOrWhiteSpace(runDirectory)
            ? null
            : Path.GetFullPath(runDirectory);
        foreach (SampleBistroQualityArtifact artifact in artifacts)
        {
            if (artifact.ByteLength <= 0 || !IsSha256(artifact.Sha256))
            {
                failures.Add(
                    $"Artifact '{artifact.Name}' has invalid size or identity.");
                continue;
            }
            if (root is null)
                continue;
            if (Path.IsPathRooted(artifact.RelativePath))
            {
                failures.Add($"Artifact '{artifact.Name}' uses an absolute path.");
                continue;
            }

            string path = Path.GetFullPath(Path.Combine(root, artifact.RelativePath));
            string relative = Path.GetRelativePath(root, path);
            if (relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                failures.Add($"Artifact '{artifact.Name}' escapes the run root.");
                continue;
            }
            if (!File.Exists(path))
            {
                failures.Add($"Artifact '{artifact.Name}' is missing.");
                continue;
            }
            var info = new FileInfo(path);
            using FileStream stream = File.OpenRead(path);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (info.Length != artifact.ByteLength ||
                !hash.Equals(artifact.Sha256, StringComparison.Ordinal))
            {
                failures.Add(
                    $"Artifact '{artifact.Name}' bytes do not match the report.");
            }
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ulong Sum(
        IEnumerable<SampleBistroQualityFrameTelemetry> frames,
        Func<SampleBistroQualityFrameTelemetry, uint> selector)
    {
        ulong total = 0;
        foreach (SampleBistroQualityFrameTelemetry frame in frames)
            total = checked(total + selector(frame));
        return total;
    }

    private static long Sum(
        IEnumerable<SampleBistroQualityFrameTelemetry> frames,
        Func<SampleBistroQualityFrameTelemetry, long> selector)
    {
        long total = 0;
        foreach (SampleBistroQualityFrameTelemetry frame in frames)
            total = checked(total + selector(frame));
        return total;
    }
}

public static class SampleBistroReflectionQualificationCli
{
    private const string AnalyzeOption = "--analyze-bistro-reflection-run";
    private const string OutputOption = "--bistro-reflection-report";
    private const int MaximumReportBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool TryRun(
        string[] args,
        TextWriter output,
        TextWriter error,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        exitCode = 0;
        if (args.Length == 0 ||
            !string.Equals(args[0], AnalyzeOption, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (args.Length is not (2 or 4) ||
                (args.Length == 4 && !string.Equals(
                    args[2], OutputOption, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Usage: {AnalyzeOption} <run-directory> " +
                    $"[{OutputOption} <output-json>]");
            }

            string runDirectory = Path.GetFullPath(args[1]);
            string sourceReportPath = Path.Combine(
                runDirectory,
                "bistro-quality-run.json");
            var info = new FileInfo(sourceReportPath);
            if (!info.Exists || info.Length <= 0 ||
                info.Length > MaximumReportBytes)
            {
                throw new InvalidDataException(
                    "The Bistro quality report is missing or outside the " +
                    "admitted size range.");
            }

            byte[] reportBytes = File.ReadAllBytes(sourceReportPath);
            string reportSha256 = Convert.ToHexStringLower(
                SHA256.HashData(reportBytes));
            SampleBistroQualityRunReport report =
                JsonSerializer.Deserialize<SampleBistroQualityRunReport>(
                    reportBytes,
                    JsonOptions) ??
                throw new InvalidDataException(
                    "The Bistro quality report deserialized to null.");
            SampleBistroReflectionQualificationResult result =
                SampleBistroReflectionQualification.Evaluate(
                    report,
                    runDirectory,
                    sourceReportPath,
                    reportSha256);
            string outputPath = args.Length == 4
                ? Path.GetFullPath(args[3])
                : Path.Combine(runDirectory, "reflection-qualification.json");
            byte[] resultBytes = JsonSerializer.SerializeToUtf8Bytes(
                result,
                JsonOptions);
            SampleEvidenceFileIo.WriteAtomic(
                outputPath,
                resultBytes,
                4 * 1024 * 1024,
                "Bistro reflection qualification report");
            output.WriteLine(
                $"Bistro reflection qualification " +
                $"{(result.Passed ? "passed" : "failed")}: " +
                $"ssrHits={result.SsrHitCount}, " +
                $"rayHits={result.RayQueryHitCount}, " +
                $"ddgiFallbacks={result.DdgiFallbackCount}, " +
                $"probeFallbacks={result.ProbeFallbackCount}, " +
                $"report='{outputPath}'.");
            if (!result.Passed)
            {
                foreach (string failure in result.Failures)
                    error.WriteLine(failure);
            }
            exitCode = result.Passed ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                InvalidDataException or JsonException or
                UnauthorizedAccessException)
        {
            error.WriteLine(
                $"Bistro reflection qualification failed: " +
                $"{exception.Message}");
            exitCode = 2;
        }

        return true;
    }
}
