using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Njulf.Core.Camera;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using StbImageSharp;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

public static class SampleVolumetricTemporalCaptureContract
{
    public const string SchemaVersion = "volumetric-temporal-capture-contract/v1";
    public const string RunSchemaVersion = "volumetric-temporal-capture-run/v1";
    public const string ReportSchemaVersion = "volumetric-temporal-quality-report/v1";
    public const int FramesPerSecond = 60;
    public const int WarmupFrameCount = 128;
    public const int CaptureFrameCount = 32;
    public const int MaximumDrainFrameCount = 120;
    public const string ContractFileName = "volumetric-temporal-contract.json";
    public const string RunFileName = "volumetric-temporal-run.json";
    public const string ReportFileName = "volumetric-temporal-report.json";
    public const string ChangesFileName = "volumetric-temporal-changes.csv";

    public const double MaximumMeanAbsoluteRgbDelta = 0.002;
    public const double MaximumP95AbsoluteChannelDelta = 0.015;
    public const double MaximumChangedPixelFraction = 0.02;
    public const double MaximumHistoryRejectionRatio = 0.05;

    public static (int Width, int Height) GetDimensions(
        RenderQualityPreset preset) => ValidatePreset(preset) switch
    {
        RenderQualityPreset.Ultra => (3840, 2160),
        _ => (2560, 1440)
    };

    public static (uint Width, uint Height, uint Depth) GetExpectedGrid(
        RenderQualityPreset preset) => ValidatePreset(preset) switch
    {
        RenderQualityPreset.Ultra => (304u, 175u, 104u),
        _ => (222u, 128u, 80u)
    };

    public static long GetGpuBudgetMicroseconds(RenderQualityPreset preset) =>
        ValidatePreset(preset) == RenderQualityPreset.Ultra ? 8_000L : 2_000L;

    public static ulong GetMemoryBudgetBytes(RenderQualityPreset preset) =>
        ValidatePreset(preset) == RenderQualityPreset.Ultra
            ? 320UL * 1024UL * 1024UL
            : 128UL * 1024UL * 1024UL;

    public static string GetFrameRelativePath(int captureIndex)
    {
        if (captureIndex is < 0 or >= CaptureFrameCount)
            throw new ArgumentOutOfRangeException(nameof(captureIndex));
        return $"frames/frame-{captureIndex:D3}.png";
    }

    public static string CreateFingerprint(RenderQualityPreset preset)
    {
        preset = ValidatePreset(preset);
        (int width, int height) = GetDimensions(preset);
        string canonical = string.Join('|',
            SchemaVersion,
            preset.ToString(),
            width.ToString(CultureInfo.InvariantCulture),
            height.ToString(CultureInfo.InvariantCulture),
            FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            WarmupFrameCount.ToString(CultureInfo.InvariantCulture),
            CaptureFrameCount.ToString(CultureInfo.InvariantCulture),
            "stationary-camera",
            "frozen-volume-flow",
            "production-taa-jitter",
            "final-ldr-beauty");
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static void Write(
        string outputDirectory,
        RenderQualityPreset preset,
        string settingsFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFingerprint);
        preset = ValidatePreset(preset);
        (int width, int height) = GetDimensions(preset);
        (uint gridWidth, uint gridHeight, uint gridDepth) = GetExpectedGrid(preset);
        SampleSponzaTemporalCaptureContract.WriteJsonAtomic(
            Path.Combine(outputDirectory, ContractFileName),
            new
            {
                schemaVersion = SchemaVersion,
                fingerprint = CreateFingerprint(preset),
                qualityPreset = preset,
                width,
                height,
                framesPerSecond = FramesPerSecond,
                simulationDeltaSeconds = 1.0 / FramesPerSecond,
                warmupFrameCount = WarmupFrameCount,
                captureFrameCount = CaptureFrameCount,
                screenshotDrainFrameLimit = MaximumDrainFrameCount,
                expectedGrid = new
                {
                    width = gridWidth,
                    height = gridHeight,
                    depth = gridDepth
                },
                gpuBudgetMicroseconds = GetGpuBudgetMicroseconds(preset),
                memoryBudgetBytes = GetMemoryBudgetBytes(preset),
                stabilityGates = new
                {
                    maximumMeanAbsoluteRgbDelta = MaximumMeanAbsoluteRgbDelta,
                    maximumP95AbsoluteChannelDelta =
                        MaximumP95AbsoluteChannelDelta,
                    maximumChangedPixelFraction =
                        MaximumChangedPixelFraction,
                    maximumHistoryRejectionRatio =
                        MaximumHistoryRejectionRatio
                },
                settingsFingerprint
            },
            "Volumetric temporal capture contract");
    }

    public static RenderQualityPreset ValidatePreset(RenderQualityPreset preset)
    {
        if (preset is RenderQualityPreset.High or
            RenderQualityPreset.DdgiHigh or RenderQualityPreset.Ultra)
        {
            return preset;
        }

        throw new ArgumentOutOfRangeException(
            nameof(preset), preset,
            "Volumetric temporal capture requires High, DdgiHigh, or Ultra.");
    }
}

public sealed record SampleVolumetricTemporalFrameArtifact
{
    public int CaptureIndex { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public ulong RendererFrameSerial { get; init; }
    public uint TemporalSampleIndex { get; init; }
    public uint GridWidth { get; init; }
    public uint GridHeight { get; init; }
    public uint GridDepth { get; init; }
    public ulong AllocatedBytes { get; init; }
    public long GpuFogMicroseconds { get; init; }
    public int HistoryValid { get; init; }
    public int OutputReadbackValid { get; init; }
    public int OutputProduced { get; init; }
    public int HistoryAccepted { get; init; }
    public int HistoryRejected { get; init; }
    public int HistoryRejectedInvalid { get; init; }
    public int HistoryRejectedBounds { get; init; }
    public int HistoryRejectedExtinction { get; init; }
    public int HistoryRejectedRadiance { get; init; }
    public int HistoryRejectedVelocity { get; init; }
    public int ClusterOverflowCount { get; init; }
    public int NonFiniteCount { get; init; }
    public int ParticleCandidateCount { get; init; }
    public int ParticleAdmittedCount { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed record SampleVolumetricTemporalRunManifest
{
    public string SchemaVersion { get; init; } =
        SampleVolumetricTemporalCaptureContract.RunSchemaVersion;
    public string Status { get; init; } = "running";
    public string Failure { get; init; } = string.Empty;
    public RenderQualityPreset QualityPreset { get; init; } =
        RenderQualityPreset.High;
    public string ContractFingerprint { get; init; } = string.Empty;
    public string SettingsFingerprint { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int FramesPerSecond { get; init; } =
        SampleVolumetricTemporalCaptureContract.FramesPerSecond;
    public int WarmupFrameCount { get; init; } =
        SampleVolumetricTemporalCaptureContract.WarmupFrameCount;
    public int ExpectedFrameCount { get; init; } =
        SampleVolumetricTemporalCaptureContract.CaptureFrameCount;
    public int ScreenshotCompletedCountAtStart { get; init; }
    public int ScreenshotCompletedCountAtEnd { get; init; }
    public string GpuDevice { get; init; } = "unknown-device";
    public string GpuDriver { get; init; } = "unknown-driver";
    public PerformanceCaptureRunMetadata CaptureRun { get; init; } =
        PerformanceCaptureRunMetadata.Unknown;
    public IReadOnlyList<SampleVolumetricTemporalFrameArtifact> Frames
        { get; init; } = Array.Empty<SampleVolumetricTemporalFrameArtifact>();
}

public sealed record SampleVolumetricTemporalFrameChange(
    int PreviousFrameIndex,
    int FrameIndex,
    double MeanAbsoluteRgbDelta,
    double RootMeanSquareRgbDelta,
    double P95AbsoluteChannelDelta,
    double MaximumAbsoluteChannelDelta,
    double ChangedPixelFraction);

public sealed record SampleVolumetricTemporalQualityGate(
    string Name,
    bool Passed,
    double Observed,
    double Limit,
    string Unit);

public sealed record SampleVolumetricTemporalQualityReport
{
    public string SchemaVersion { get; init; } =
        SampleVolumetricTemporalCaptureContract.ReportSchemaVersion;
    public bool Passed { get; init; }
    public RenderQualityPreset QualityPreset { get; init; }
    public int FrameCount { get; init; }
    public int PairCount { get; init; }
    public double MeanAbsoluteRgbDelta { get; init; }
    public double MaximumMeanAbsoluteRgbDelta { get; init; }
    public double MaximumP95AbsoluteChannelDelta { get; init; }
    public double MaximumChangedPixelFraction { get; init; }
    public double MaximumHistoryRejectionRatio { get; init; }
    public long GpuFogP95Microseconds { get; init; }
    public ulong MaximumAllocatedBytes { get; init; }
    public IReadOnlyList<SampleVolumetricTemporalQualityGate> Gates
        { get; init; } = Array.Empty<SampleVolumetricTemporalQualityGate>();
    public IReadOnlyList<SampleVolumetricTemporalFrameChange> Changes
        { get; init; } = Array.Empty<SampleVolumetricTemporalFrameChange>();
}

public static class SampleVolumetricTemporalCaptureAnalyzer
{
    public static int RunOffline(
        string outputDirectory,
        TextWriter output,
        TextWriter error)
    {
        try
        {
            string root = Path.GetFullPath(outputDirectory);
            string manifestPath = Path.Combine(
                root, SampleVolumetricTemporalCaptureContract.RunFileName);
            SampleVolumetricTemporalRunManifest? manifest =
                JsonSerializer.Deserialize<SampleVolumetricTemporalRunManifest>(
                    File.ReadAllText(manifestPath),
                    SampleSponzaTemporalCaptureContract.CreateJsonOptions());
            if (manifest == null)
                throw new InvalidDataException("Volumetric capture manifest is empty.");
            SampleVolumetricTemporalQualityReport report =
                Analyze(root, manifest, output);
            return report.Passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            error.WriteLine(
                $"Volumetric temporal analysis failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    public static SampleVolumetricTemporalQualityReport Analyze(
        string outputDirectory,
        SampleVolumetricTemporalRunManifest manifest,
        TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(output);
        string root = Path.GetFullPath(outputDirectory);
        ValidateManifest(manifest);

        var changes = new List<SampleVolumetricTemporalFrameChange>(
            manifest.Frames.Count - 1);
        DecodedImage? previous = null;
        for (int index = 0; index < manifest.Frames.Count; index++)
        {
            SampleVolumetricTemporalFrameArtifact frame = manifest.Frames[index];
            DecodedImage current = LoadFrame(root, manifest, frame);
            if (previous is { } prior)
            {
                SampleSponzaTemporalPixelChangeMetrics metrics =
                    SampleSponzaTemporalCaptureAnalyzer.CalculatePixelChange(
                        prior.Pixels,
                        current.Pixels,
                        current.Width,
                        current.Height);
                changes.Add(new SampleVolumetricTemporalFrameChange(
                    index - 1,
                    index,
                    metrics.MeanAbsoluteRgbDelta,
                    metrics.RootMeanSquareRgbDelta,
                    metrics.P95AbsoluteChannelDelta,
                    metrics.MaximumAbsoluteChannelDelta,
                    metrics.ChangedPixelFraction));
            }
            previous = current;
        }

        double meanDelta = changes.Count == 0
            ? 0.0
            : changes.Average(change => change.MeanAbsoluteRgbDelta);
        double maximumMeanDelta = changes.Max(
            change => change.MeanAbsoluteRgbDelta);
        double maximumP95Delta = changes.Max(
            change => change.P95AbsoluteChannelDelta);
        double maximumChangedFraction = changes.Max(
            change => change.ChangedPixelFraction);
        double maximumRejectionRatio = manifest.Frames.Max(frame =>
        {
            long total = (long)frame.HistoryAccepted + frame.HistoryRejected;
            return total <= 0 ? 1.0 : frame.HistoryRejected / (double)total;
        });
        long gpuP95 = Percentile95(manifest.Frames
            .Select(frame => frame.GpuFogMicroseconds)
            .Where(value => value > 0)
            .ToArray());
        ulong maximumAllocatedBytes = manifest.Frames.Max(
            frame => frame.AllocatedBytes);
        (uint expectedWidth, uint expectedHeight, uint expectedDepth) =
            SampleVolumetricTemporalCaptureContract.GetExpectedGrid(
                manifest.QualityPreset);
        long gpuBudget =
            SampleVolumetricTemporalCaptureContract.GetGpuBudgetMicroseconds(
                manifest.QualityPreset);
        ulong memoryBudget =
            SampleVolumetricTemporalCaptureContract.GetMemoryBudgetBytes(
                manifest.QualityPreset);

        var gates = new List<SampleVolumetricTemporalQualityGate>
        {
            Gate("maximum mean RGB delta", maximumMeanDelta,
                SampleVolumetricTemporalCaptureContract
                    .MaximumMeanAbsoluteRgbDelta, "normalized"),
            Gate("maximum p95 channel delta", maximumP95Delta,
                SampleVolumetricTemporalCaptureContract
                    .MaximumP95AbsoluteChannelDelta, "normalized"),
            Gate("maximum changed-pixel fraction", maximumChangedFraction,
                SampleVolumetricTemporalCaptureContract
                    .MaximumChangedPixelFraction, "fraction"),
            Gate("maximum history rejection ratio", maximumRejectionRatio,
                SampleVolumetricTemporalCaptureContract
                    .MaximumHistoryRejectionRatio, "fraction"),
            Gate("fog GPU p95", gpuP95, gpuBudget, "microseconds"),
            Gate("volumetric allocation", maximumAllocatedBytes,
                memoryBudget, "bytes"),
            BooleanGate("expected froxel grid", manifest.Frames.All(frame =>
                frame.GridWidth == expectedWidth &&
                frame.GridHeight == expectedHeight &&
                frame.GridDepth == expectedDepth)),
            BooleanGate("valid history and output", manifest.Frames.All(frame =>
                frame.HistoryValid != 0 &&
                frame.OutputReadbackValid != 0 &&
                frame.OutputProduced != 0)),
            BooleanGate("finite overflow-free output", manifest.Frames.All(frame =>
                frame.ClusterOverflowCount == 0 && frame.NonFiniteCount == 0)),
            BooleanGate("rejection accounting", manifest.Frames.All(frame =>
                frame.HistoryRejected ==
                frame.HistoryRejectedInvalid +
                frame.HistoryRejectedBounds +
                frame.HistoryRejectedExtinction +
                frame.HistoryRejectedRadiance +
                frame.HistoryRejectedVelocity)),
            BooleanGate("static particle population", manifest.Frames.All(frame =>
                frame.ParticleCandidateCount == 0 &&
                frame.ParticleAdmittedCount == 0)),
            BooleanGate("production froxel path", manifest.Frames.All(frame =>
                string.Equals(
                    frame.Status,
                    "active-output-verified",
                    StringComparison.Ordinal)))
        };

        var report = new SampleVolumetricTemporalQualityReport
        {
            Passed = gates.All(gate => gate.Passed),
            QualityPreset = manifest.QualityPreset,
            FrameCount = manifest.Frames.Count,
            PairCount = changes.Count,
            MeanAbsoluteRgbDelta = meanDelta,
            MaximumMeanAbsoluteRgbDelta = maximumMeanDelta,
            MaximumP95AbsoluteChannelDelta = maximumP95Delta,
            MaximumChangedPixelFraction = maximumChangedFraction,
            MaximumHistoryRejectionRatio = maximumRejectionRatio,
            GpuFogP95Microseconds = gpuP95,
            MaximumAllocatedBytes = maximumAllocatedBytes,
            Gates = gates,
            Changes = changes
        };
        WriteChangesCsv(root, changes);
        SampleSponzaTemporalCaptureContract.WriteJsonAtomic(
            Path.Combine(root,
                SampleVolumetricTemporalCaptureContract.ReportFileName),
            report,
            "Volumetric temporal quality report");
        output.WriteLine(
            $"Volumetric temporal quality: " +
            $"{(report.Passed ? "PASS" : "FAIL")}, " +
            $"preset={report.QualityPreset}, pairs={report.PairCount}, " +
            $"mean/maxMean={report.MeanAbsoluteRgbDelta:F6}/" +
            $"{report.MaximumMeanAbsoluteRgbDelta:F6}, " +
            $"maxP95={report.MaximumP95AbsoluteChannelDelta:F6}, " +
            $"changed={report.MaximumChangedPixelFraction:P2}, " +
            $"historyRejected={report.MaximumHistoryRejectionRatio:P2}, " +
            $"fogP95={report.GpuFogP95Microseconds}us.");
        return report;
    }

    private static void ValidateManifest(
        SampleVolumetricTemporalRunManifest manifest)
    {
        RenderQualityPreset preset =
            SampleVolumetricTemporalCaptureContract.ValidatePreset(
                manifest.QualityPreset);
        (int width, int height) =
            SampleVolumetricTemporalCaptureContract.GetDimensions(preset);
        if (manifest.SchemaVersion !=
                SampleVolumetricTemporalCaptureContract.RunSchemaVersion ||
            manifest.ContractFingerprint !=
                SampleVolumetricTemporalCaptureContract.CreateFingerprint(preset) ||
            manifest.Width != width || manifest.Height != height ||
            manifest.FramesPerSecond !=
                SampleVolumetricTemporalCaptureContract.FramesPerSecond ||
            manifest.WarmupFrameCount !=
                SampleVolumetricTemporalCaptureContract.WarmupFrameCount ||
            manifest.ExpectedFrameCount !=
                SampleVolumetricTemporalCaptureContract.CaptureFrameCount ||
            manifest.Frames.Count !=
                SampleVolumetricTemporalCaptureContract.CaptureFrameCount)
        {
            throw new InvalidDataException(
                "Volumetric temporal manifest does not match the capture contract.");
        }
        for (int index = 0; index < manifest.Frames.Count; index++)
        {
            SampleVolumetricTemporalFrameArtifact frame = manifest.Frames[index];
            if (frame.CaptureIndex != index ||
                frame.RelativePath !=
                SampleVolumetricTemporalCaptureContract.GetFrameRelativePath(index))
            {
                throw new InvalidDataException(
                    $"Volumetric temporal frame {index} is out of contract.");
            }
        }
    }

    private static DecodedImage LoadFrame(
        string root,
        SampleVolumetricTemporalRunManifest manifest,
        SampleVolumetricTemporalFrameArtifact frame)
    {
        string path = ResolveBundlePath(root, frame.RelativePath);
        byte[] encoded = File.ReadAllBytes(path);
        string hash = "sha256:" + Convert.ToHexString(
            SHA256.HashData(encoded)).ToLowerInvariant();
        if (!string.Equals(hash, frame.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Volumetric frame hash changed: {frame.RelativePath}.");
        }
        ImageResult image = ImageResult.FromMemory(
            encoded, ColorComponents.RedGreenBlueAlpha);
        if (image.Width != manifest.Width || image.Height != manifest.Height ||
            image.Data.Length != checked(image.Width * image.Height * 4))
        {
            throw new InvalidDataException(
                $"Volumetric frame has unexpected dimensions: " +
                $"{frame.RelativePath} ({image.Width}x{image.Height}).");
        }
        return new DecodedImage(image.Width, image.Height, image.Data);
    }

    private static SampleVolumetricTemporalQualityGate Gate(
        string name, double observed, double limit, string unit) =>
        new(name, double.IsFinite(observed) && observed <= limit,
            observed, limit, unit);

    private static SampleVolumetricTemporalQualityGate BooleanGate(
        string name, bool passed) =>
        new(name, passed, passed ? 1.0 : 0.0, 1.0, "boolean");

    private static long Percentile95(long[] values)
    {
        if (values.Length == 0)
            return long.MaxValue;
        Array.Sort(values);
        int index = Math.Clamp(
            (int)Math.Ceiling(values.Length * 0.95) - 1,
            0,
            values.Length - 1);
        return values[index];
    }

    private static void WriteChangesCsv(
        string root,
        IReadOnlyList<SampleVolumetricTemporalFrameChange> changes)
    {
        string path = Path.Combine(
            root, SampleVolumetricTemporalCaptureContract.ChangesFileName);
        using var writer = new StreamWriter(path, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(
            "previousFrame,frame,meanAbsoluteRgbDelta,rootMeanSquareRgbDelta," +
            "p95AbsoluteChannelDelta,maximumAbsoluteChannelDelta,changedPixelFraction");
        foreach (SampleVolumetricTemporalFrameChange change in changes)
        {
            writer.WriteLine(string.Join(',',
                change.PreviousFrameIndex.ToString(CultureInfo.InvariantCulture),
                change.FrameIndex.ToString(CultureInfo.InvariantCulture),
                change.MeanAbsoluteRgbDelta.ToString("R", CultureInfo.InvariantCulture),
                change.RootMeanSquareRgbDelta.ToString("R", CultureInfo.InvariantCulture),
                change.P95AbsoluteChannelDelta.ToString("R", CultureInfo.InvariantCulture),
                change.MaximumAbsoluteChannelDelta.ToString("R", CultureInfo.InvariantCulture),
                change.ChangedPixelFraction.ToString("R", CultureInfo.InvariantCulture)));
        }
    }

    private static string ResolveBundlePath(string root, string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Capture artifact escapes the bundle: {relativePath}");
        return path;
    }

    private sealed record DecodedImage(int Width, int Height, byte[] Pixels);
}

public sealed class SampleVolumetricTemporalCaptureRunner
{
    private readonly VulkanRenderer _renderer;
    private readonly FirstPersonCamera _camera;
    private readonly string _outputDirectory;
    private readonly Func<(int Width, int Height)> _viewportSize;
    private readonly Action _exit;
    private readonly RenderQualityPreset _qualityPreset;
    private readonly string _settingsFingerprint;
    private readonly int _completedAtStart;
    private readonly CoreVector3 _cameraPosition;
    private readonly float _cameraYaw;
    private readonly float _cameraPitch;
    private readonly float _cameraFieldOfView;
    private readonly float _cameraNearPlane;
    private readonly float _cameraFarPlane;
    private readonly List<SampleVolumetricTemporalFrameArtifact> _frames = [];
    private int _warmupFrames;
    private int _drainFrames;
    private bool _prepared;
    private bool _stopped;
    private RendererDiagnostics _lastDiagnostics = RendererDiagnostics.Empty;

    public SampleVolumetricTemporalQualityReport? Report { get; private set; }

    public SampleVolumetricTemporalCaptureRunner(
        VulkanRenderer renderer,
        FirstPersonCamera camera,
        Scene scene,
        string outputDirectory,
        Func<(int Width, int Height)> viewportSize,
        Action exit)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _viewportSize = viewportSize ??
            throw new ArgumentNullException(nameof(viewportSize));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _outputDirectory = Path.GetFullPath(outputDirectory);
        EnsureEmptyOutputDirectory(_outputDirectory);
        Directory.CreateDirectory(_outputDirectory);

        _qualityPreset =
            SampleVolumetricTemporalCaptureContract.ValidatePreset(
                _renderer.Settings.QualityPreset);
        ConfigureRenderer(scene);
        _settingsFingerprint =
            SampleRenderSettingsFingerprint.Capture(_renderer.Settings);
        _completedAtStart =
            _renderer.LastDiagnostics.ScreenshotCompletedCount;
        _cameraPosition = _camera.Position;
        _cameraYaw = _camera.Yaw;
        _cameraPitch = _camera.Pitch;
        _cameraFieldOfView = _camera.FieldOfView;
        _cameraNearPlane = _camera.NearPlane;
        _cameraFarPlane = _camera.FarPlane;

        SampleVolumetricTemporalCaptureContract.Write(
            _outputDirectory, _qualityPreset, _settingsFingerprint);
        WriteManifest("running", string.Empty, _renderer.LastDiagnostics);
        (int width, int height) =
            SampleVolumetricTemporalCaptureContract.GetDimensions(_qualityPreset);
        Console.WriteLine(
            $"Volumetric temporal capture armed: preset={_qualityPreset}, " +
            $"warmup={SampleVolumetricTemporalCaptureContract.WarmupFrameCount}, " +
            $"frames={SampleVolumetricTemporalCaptureContract.CaptureFrameCount}, " +
            $"resolution={width}x{height}, directory={_outputDirectory}.");
    }

    public void PrepareFrame()
    {
        if (_stopped)
            return;
        try
        {
            if (_prepared)
                throw new InvalidOperationException(
                    "A volumetric temporal frame was prepared twice.");
            (int expectedWidth, int expectedHeight) =
                SampleVolumetricTemporalCaptureContract.GetDimensions(
                    _qualityPreset);
            (int width, int height) = _viewportSize();
            if (width != expectedWidth || height != expectedHeight)
            {
                throw new InvalidOperationException(
                    $"Locked volumetric capture resolution is " +
                    $"{expectedWidth}x{expectedHeight}, current viewport is " +
                    $"{width}x{height}.");
            }
            ApplyFixedCamera(width, height);
            if (_warmupFrames >=
                    SampleVolumetricTemporalCaptureContract.WarmupFrameCount &&
                _frames.Count <
                    SampleVolumetricTemporalCaptureContract.CaptureFrameCount)
            {
                string relativePath =
                    SampleVolumetricTemporalCaptureContract
                        .GetFrameRelativePath(_frames.Count);
                string path = ResolveBundlePath(relativePath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path) ?? _outputDirectory);
                _renderer.RequestScreenshot(path);
            }
            _prepared = true;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    public void OnFrameRendered(RendererDiagnostics diagnostics)
    {
        if (_stopped)
            return;
        try
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            if (!_prepared)
                throw new InvalidOperationException(
                    "A volumetric temporal frame rendered without preparation.");
            _prepared = false;
            _lastDiagnostics = diagnostics;
            if (diagnostics.VolumetricFogStatus.StartsWith(
                    "froxel-resource-admission-failed",
                    StringComparison.Ordinal) ||
                diagnostics.VolumetricFogStatus.StartsWith(
                    "froxel-pipeline-initialization-failed",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The production froxel path cannot start: " +
                    diagnostics.VolumetricFogStatus);
            }
            if (_warmupFrames <
                SampleVolumetricTemporalCaptureContract.WarmupFrameCount)
            {
                _warmupFrames++;
                return;
            }
            if (_frames.Count <
                SampleVolumetricTemporalCaptureContract.CaptureFrameCount)
            {
                RecordFrame(diagnostics);
                return;
            }

            int completedDelta = Math.Max(
                0, diagnostics.ScreenshotCompletedCount - _completedAtStart);
            if (completedDelta >=
                SampleVolumetricTemporalCaptureContract.CaptureFrameCount)
            {
                Complete(diagnostics);
                return;
            }
            _drainFrames++;
            if (_drainFrames >=
                SampleVolumetricTemporalCaptureContract.MaximumDrainFrameCount)
            {
                throw new TimeoutException(
                    "Volumetric screenshots did not settle within the drain limit.");
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void ConfigureRenderer(Scene scene)
    {
        RenderSettings settings = _renderer.Settings;
        settings.ResolutionScale = 1.0f;
        settings.DynamicResolution.Enabled = false;
        settings.DynamicResolution.MinimumScale = 1.0f;
        settings.DynamicResolution.MaximumScale = 1.0f;
        settings.Particles.Enabled = false;
        settings.Animation.Enabled = false;
        settings.Environment.AnimateTimeOfDay = false;
        settings.AutoExposure.Enabled = false;
        settings.Fog.Technique = FogTechnique.Froxel;
        settings.Fog.DebugView = FogDebugView.None;
        settings.Fog.Volumetric.DebugProjection =
            FogDebugProjection.MaxAlongRay;
        settings.Fog.Volumetric.DebugSlice = -1;
        settings.Fog.Volumetric.GlobalWind = new CoreVector3(0f);
        settings.FeatureIsolation = RenderFeatureIsolationMode.FullFrame;
        settings.AntiAliasing.DebugView = AntiAliasingDebugView.None;
        settings.ResetRenderViewOverrides();
        settings.Debug.Enabled = true;
        settings.Debug.AllowScreenshots = true;
        settings.Debug.CpuSnapshotsEnabled = false;
        settings.Debug.AllowGpuTiming = true;
        foreach (VolumetricDensityVolume volume in
                 scene.VolumetricDensityVolumes)
        {
            volume.FlowVelocity = new CoreVector3(0f);
        }
        _renderer.CaptureScenario = "VolumetricTemporalStability";
    }

    private void ApplyFixedCamera(int width, int height)
    {
        _camera.Position = _cameraPosition;
        _camera.Yaw = _cameraYaw;
        _camera.Pitch = _cameraPitch;
        _camera.FieldOfView = _cameraFieldOfView;
        _camera.NearPlane = _cameraNearPlane;
        _camera.FarPlane = _cameraFarPlane;
        _camera.AspectRatio = width / (float)height;
        _camera.Update();
    }

    private void RecordFrame(RendererDiagnostics diagnostics)
    {
        int index = _frames.Count;
        _frames.Add(new SampleVolumetricTemporalFrameArtifact
        {
            CaptureIndex = index,
            RelativePath = SampleVolumetricTemporalCaptureContract
                .GetFrameRelativePath(index),
            RendererFrameSerial = diagnostics.CaptureFrame.FrameSerial,
            TemporalSampleIndex = diagnostics.TemporalSampleIndex,
            GridWidth = diagnostics.VolumetricFogGridWidth,
            GridHeight = diagnostics.VolumetricFogGridHeight,
            GridDepth = diagnostics.VolumetricFogGridDepth,
            AllocatedBytes = diagnostics.VolumetricFogAllocatedBytes,
            GpuFogMicroseconds = diagnostics.GpuFogMicroseconds,
            HistoryValid = diagnostics.VolumetricFogHistoryValid,
            OutputReadbackValid = diagnostics.VolumetricFogOutputReadbackValid,
            OutputProduced = diagnostics.VolumetricFogOutputProduced,
            HistoryAccepted =
                diagnostics.VolumetricFogHistoryAcceptedFroxelCount,
            HistoryRejected =
                diagnostics.VolumetricFogHistoryRejectedFroxelCount,
            HistoryRejectedInvalid =
                diagnostics.VolumetricFogHistoryRejectedInvalidFroxelCount,
            HistoryRejectedBounds =
                diagnostics.VolumetricFogHistoryRejectedBoundsFroxelCount,
            HistoryRejectedExtinction =
                diagnostics.VolumetricFogHistoryRejectedExtinctionFroxelCount,
            HistoryRejectedRadiance =
                diagnostics.VolumetricFogHistoryRejectedRadianceFroxelCount,
            HistoryRejectedVelocity =
                diagnostics.VolumetricFogHistoryRejectedVelocityFroxelCount,
            ClusterOverflowCount = diagnostics.VolumetricFogClusterOverflowCount,
            NonFiniteCount = diagnostics.VolumetricFogNonFiniteCount,
            ParticleCandidateCount =
                diagnostics.VolumetricFogParticleCandidateCount,
            ParticleAdmittedCount =
                diagnostics.VolumetricFogParticleAdmittedCount,
            Status = diagnostics.VolumetricFogStatus
        });
    }

    private void Complete(RendererDiagnostics diagnostics)
    {
        for (int index = 0; index < _frames.Count; index++)
        {
            SampleVolumetricTemporalFrameArtifact frame = _frames[index];
            string path = ResolveBundlePath(frame.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0)
                throw new FileNotFoundException(
                    $"Captured frame is missing or empty: {frame.RelativePath}",
                    path);
            using FileStream stream = File.OpenRead(path);
            string sha256 = "sha256:" + Convert.ToHexString(
                SHA256.HashData(stream)).ToLowerInvariant();
            _frames[index] = frame with
            {
                ByteLength = info.Length,
                Sha256 = sha256
            };
        }

        SampleVolumetricTemporalRunManifest manifest =
            CreateManifest("completed", string.Empty, diagnostics);
        WriteManifest(manifest);
        SampleVolumetricTemporalQualityReport report =
            SampleVolumetricTemporalCaptureAnalyzer.Analyze(
                _outputDirectory, manifest, Console.Out);
        Report = report;
        _stopped = true;
        Environment.ExitCode = report.Passed ? 0 : 1;
        Console.WriteLine(
            $"Volumetric temporal capture completed: {_outputDirectory}");
        _exit();
    }

    private void Fail(Exception exception)
    {
        if (_stopped)
            return;
        _stopped = true;
        string failure =
            $"{exception.GetType().Name}: {exception.Message}";
        try
        {
            WriteManifest("failed", failure, _lastDiagnostics);
        }
        catch (Exception manifestException)
        {
            Console.Error.WriteLine(
                $"Could not write failed volumetric manifest: " +
                manifestException.Message);
        }
        Environment.ExitCode = 1;
        Console.Error.WriteLine(
            $"Volumetric temporal capture failed: {failure}");
        _exit();
    }

    private void WriteManifest(
        string status,
        string failure,
        RendererDiagnostics diagnostics) =>
        WriteManifest(CreateManifest(status, failure, diagnostics));

    private SampleVolumetricTemporalRunManifest CreateManifest(
        string status,
        string failure,
        RendererDiagnostics diagnostics)
    {
        (int width, int height) =
            SampleVolumetricTemporalCaptureContract.GetDimensions(_qualityPreset);
        return new SampleVolumetricTemporalRunManifest
        {
            Status = status,
            Failure = failure,
            QualityPreset = _qualityPreset,
            ContractFingerprint =
                SampleVolumetricTemporalCaptureContract.CreateFingerprint(
                    _qualityPreset),
            SettingsFingerprint = _settingsFingerprint,
            Width = width,
            Height = height,
            ScreenshotCompletedCountAtStart = _completedAtStart,
            ScreenshotCompletedCountAtEnd =
                diagnostics.ScreenshotCompletedCount,
            GpuDevice = diagnostics.CaptureGpuDeviceName,
            GpuDriver = diagnostics.CaptureGpuDriverVersion,
            CaptureRun = diagnostics.CaptureRun,
            Frames = _frames.ToArray()
        };
    }

    private void WriteManifest(
        SampleVolumetricTemporalRunManifest manifest) =>
        SampleSponzaTemporalCaptureContract.WriteJsonAtomic(
            Path.Combine(_outputDirectory,
                SampleVolumetricTemporalCaptureContract.RunFileName),
            manifest,
            "Volumetric temporal capture run manifest");

    private string ResolveBundlePath(string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(
            Path.Combine(_outputDirectory, normalized));
        string prefix = _outputDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _outputDirectory
            : _outputDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Capture artifact escapes the bundle: {relativePath}");
        return path;
    }

    private static void EnsureEmptyOutputDirectory(string path)
    {
        if (Directory.Exists(path) &&
            Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException(
                "Volumetric temporal capture directory must be absent or " +
                $"empty: {path}");
        }
    }
}
