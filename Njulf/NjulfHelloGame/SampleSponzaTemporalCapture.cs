using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Camera;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

public enum SampleSponzaTemporalCaptureStage : byte
{
    Warmup = 0,
    Horizontal = 1,
    Vertical = 2,
    Drain = 3,
    Complete = 4
}

public sealed record SampleSponzaTemporalCaptureInstruction(
    SampleSponzaTemporalCaptureStage Stage,
    int StageFrameIndex,
    int StageFrameCount,
    SampleSponzaGiCameraBookmark Camera,
    string Route,
    string Phase,
    bool CaptureFrame);

/// <summary>
/// Fixed-frame temporal sequence. It deliberately contains no renderer,
/// filesystem, or wall-clock behavior so path boundaries remain unit-testable.
/// </summary>
public sealed class SampleSponzaTemporalCaptureSequence
{
    public const int MaximumDrainFrameCount = 120;

    private readonly SampleSponzaGiCaptureContract _contract;
    private SampleSponzaTemporalCaptureStage _stage =
        SampleSponzaTemporalCaptureStage.Warmup;
    private int _stageFrameIndex;

    public SampleSponzaTemporalCaptureSequence(
        SampleSponzaGiCaptureContract? contract = null)
    {
        _contract = contract ?? SampleSponzaGiCaptureContract.Default;
    }

    public SampleSponzaTemporalCaptureStage Stage => _stage;
    public bool IsComplete => _stage == SampleSponzaTemporalCaptureStage.Complete;

    public SampleSponzaTemporalCaptureInstruction CurrentInstruction =>
        _stage switch
        {
            SampleSponzaTemporalCaptureStage.Warmup => new(
                _stage,
                _stageFrameIndex,
                SampleSponzaTemporalCaptureContract.WarmupFrameCount,
                _contract.LowBookmark,
                string.Empty,
                "warmup",
                false),
            SampleSponzaTemporalCaptureStage.Horizontal => new(
                _stage,
                _stageFrameIndex,
                _contract.MotionTraversalFrameCount,
                _contract.SampleWorldXMotionTraversalFrame(_stageFrameIndex),
                SampleSponzaTemporalCaptureContract.HorizontalRoute,
                ResolveHorizontalPhase(_stageFrameIndex),
                true),
            SampleSponzaTemporalCaptureStage.Vertical => new(
                _stage,
                _stageFrameIndex,
                _contract.VerticalTraversalFrameCount,
                _contract.SampleVerticalTraversalFrame(_stageFrameIndex),
                SampleSponzaTemporalCaptureContract.VerticalRoute,
                "vertical",
                true),
            SampleSponzaTemporalCaptureStage.Drain => new(
                _stage,
                _stageFrameIndex,
                MaximumDrainFrameCount,
                _contract.HighBookmark,
                string.Empty,
                "drain",
                false),
            _ => throw new InvalidOperationException(
                "The Sponza temporal capture sequence is complete.")
        };

    public void AdvanceAfterRenderedFrame(bool screenshotsComplete)
    {
        if (IsComplete)
            return;

        if (_stage == SampleSponzaTemporalCaptureStage.Drain &&
            screenshotsComplete)
        {
            _stage = SampleSponzaTemporalCaptureStage.Complete;
            _stageFrameIndex = 0;
            return;
        }

        _stageFrameIndex++;
        int frameCount = CurrentStageFrameCount();
        if (_stageFrameIndex < frameCount)
            return;

        _stage = _stage switch
        {
            SampleSponzaTemporalCaptureStage.Warmup =>
                SampleSponzaTemporalCaptureStage.Horizontal,
            SampleSponzaTemporalCaptureStage.Horizontal =>
                SampleSponzaTemporalCaptureStage.Vertical,
            SampleSponzaTemporalCaptureStage.Vertical =>
                SampleSponzaTemporalCaptureStage.Drain,
            SampleSponzaTemporalCaptureStage.Drain =>
                throw new TimeoutException(
                    $"Renderer screenshots did not settle within " +
                    $"{MaximumDrainFrameCount} drain frames."),
            _ => SampleSponzaTemporalCaptureStage.Complete
        };
        _stageFrameIndex = 0;
    }

    public static string ResolveHorizontalPhase(int frameIndex)
    {
        if (frameIndex < 0 ||
            frameIndex >= SampleSponzaGiCaptureContract
                .MotionOutboundFrameCount +
            SampleSponzaGiCaptureContract.MotionPauseFrameCount +
            SampleSponzaGiCaptureContract.MotionReturnFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        if (frameIndex < SampleSponzaGiCaptureContract.MotionOutboundFrameCount)
            return "outbound";
        if (frameIndex <
            SampleSponzaGiCaptureContract.MotionOutboundFrameCount +
            SampleSponzaGiCaptureContract.MotionPauseFrameCount)
        {
            return "hold";
        }

        return "return";
    }

    private int CurrentStageFrameCount() => _stage switch
    {
        SampleSponzaTemporalCaptureStage.Warmup =>
            SampleSponzaTemporalCaptureContract.WarmupFrameCount,
        SampleSponzaTemporalCaptureStage.Horizontal =>
            _contract.MotionTraversalFrameCount,
        SampleSponzaTemporalCaptureStage.Vertical =>
            _contract.VerticalTraversalFrameCount,
        SampleSponzaTemporalCaptureStage.Drain => MaximumDrainFrameCount,
        _ => 0
    };
}

public static class SampleSponzaTemporalCaptureContract
{
    public const string SchemaVersion = "sponza-temporal-capture-contract/v2";
    public const string RunSchemaVersion = "sponza-temporal-capture-run/v2";
    public const string HorizontalRoute = "world-x";
    public const string VerticalRoute = "vertical";
    public const int Width = 1600;
    public const int Height = 900;
    public const int FramesPerSecond = 60;
    public const int WarmupFrameCount = 2048;
    public const int ExpectedFrameCount = 1260;
    public const string ContractFileName = "sponza-temporal-contract.json";
    public const string RunFileName = "sponza-temporal-run.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string Fingerprint { get; } = CreateFingerprint();

    public static string GetFrameRelativePath(string route, int frameIndex)
    {
        ValidateRoute(route);
        if (frameIndex < 0 || frameIndex >= GetRouteFrameCount(route))
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return $"{route}/frames/frame-{frameIndex:D4}.png";
    }

    public static string GetTraceRelativePath(string route)
    {
        ValidateRoute(route);
        return $"{route}/trace.json";
    }

    public static int GetRouteFrameCount(string route) => route switch
    {
        HorizontalRoute =>
            SampleSponzaGiCaptureContract.Default.MotionTraversalFrameCount,
        VerticalRoute =>
            SampleSponzaGiCaptureContract.Default.VerticalTraversalFrameCount,
        _ => throw new ArgumentException(
            $"Unknown Sponza temporal route '{route}'.", nameof(route))
    };

    public static void Write(string outputDirectory, string settingsFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFingerprint);
        SampleSponzaGiCaptureContract sponza =
            SampleSponzaGiCaptureContract.Default;
        var payload = new
        {
            schemaVersion = SchemaVersion,
            fingerprint = Fingerprint,
            baseSponzaContractFingerprint = sponza.Fingerprint,
            width = Width,
            height = Height,
            framesPerSecond = FramesPerSecond,
            simulationDeltaSeconds = 1.0 / FramesPerSecond,
            warmupFrameCount = WarmupFrameCount,
            expectedFrameCount = ExpectedFrameCount,
            screenshotDrainFrameLimit =
                SampleSponzaTemporalCaptureSequence.MaximumDrainFrameCount,
            finalOutput = "renderer final LDR beauty",
            settingsFingerprint,
            routes = new object[]
            {
                new
                {
                    name = HorizontalRoute,
                    frameCount = sponza.MotionTraversalFrameCount,
                    phases = new[] { "outbound", "hold", "return" }
                },
                new
                {
                    name = VerticalRoute,
                    frameCount = sponza.VerticalTraversalFrameCount,
                    phases = new[] { "vertical" }
                }
            }
        };
        WriteJsonAtomic(
            Path.Combine(outputDirectory, ContractFileName),
            payload,
            "Sponza temporal capture contract");
    }

    internal static JsonSerializerOptions CreateJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void WriteJsonAtomic<T>(
        string path,
        T value,
        string description)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions);
        SampleEvidenceFileIo.WriteAtomic(
            path,
            bytes,
            SampleEvidenceFileIo.MaximumJsonBytes,
            description);
    }

    private static void ValidateRoute(string route)
    {
        if (route is not (HorizontalRoute or VerticalRoute))
        {
            throw new ArgumentException(
                $"Unknown Sponza temporal route '{route}'.", nameof(route));
        }
    }

    private static string CreateFingerprint()
    {
        string canonical = string.Join(
            "|",
            SchemaVersion,
            SampleSponzaGiCaptureContract.Default.Fingerprint,
            Width.ToString(CultureInfo.InvariantCulture),
            Height.ToString(CultureInfo.InvariantCulture),
            FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            WarmupFrameCount.ToString(CultureInfo.InvariantCulture),
            ExpectedFrameCount.ToString(CultureInfo.InvariantCulture),
            "final-ldr-beauty",
            "presentation-overlay");
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

public sealed record SampleSponzaTemporalFrameArtifact
{
    public int CaptureOrdinal { get; init; }
    public string Route { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public int RouteFrameIndex { get; init; }
    public float SimulationTimeSeconds { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public ulong RendererFrameSerial { get; init; }
    public uint TemporalSampleIndex { get; init; }
    public float CameraPositionX { get; init; }
    public float CameraPositionY { get; init; }
    public float CameraPositionZ { get; init; }
    public float CameraYaw { get; init; }
    public float CameraPitch { get; init; }
    public float CameraFieldOfView { get; init; }
    public float CameraNearPlane { get; init; }
    public float CameraFarPlane { get; init; }
    public string ViewHash { get; init; } = string.Empty;
    public string ProjectionHash { get; init; } = string.Empty;
    public ulong CameraCutSerial { get; init; }
    public AntiAliasingMode AntiAliasingMode { get; init; }
    public bool MotionVectorsEnabled { get; init; }
    public bool JitterEnabled { get; init; }
    public float JitterX { get; init; }
    public float JitterY { get; init; }
    public uint DdgiFrameRayBucket0 { get; init; }
    public uint DdgiFrameRayBucket1 { get; init; }
    public uint DdgiFrameRayBucket2 { get; init; }
    public uint DdgiFrameRayBucket3 { get; init; }
    public uint DdgiFrameRayBucket4 { get; init; }
    public uint DdgiFrameRayBucket5 { get; init; }
    public int DdgiNearScrollCardinality { get; init; }
    public int DdgiMidScrollCardinality { get; init; }
    public int DdgiFarScrollCardinality { get; init; }
    public int DdgiScrollPlannedExpectedCount { get; init; }
    public uint DdgiScrollExpectedCount { get; init; }
    public uint DdgiScrollAcceptedCount { get; init; }
    public uint DdgiScrollTracedCount { get; init; }
    public uint DdgiScrollCommittedCount { get; init; }
    public uint DdgiScrollUnbucketedCount { get; init; }
    public SimpleDdgiScrollCohortFailureReason DdgiScrollCohortFailure
        { get; init; }
    public uint DdgiRebuildingRingMask { get; init; }
    public SimpleDdgiRebaseState DdgiNearRebaseState { get; init; }
    public SimpleDdgiRebaseState DdgiMidRebaseState { get; init; }
    public SimpleDdgiRebaseState DdgiFarRebaseState { get; init; }
}

public sealed record SampleSponzaTemporalRunManifest
{
    public string SchemaVersion { get; init; } =
        SampleSponzaTemporalCaptureContract.RunSchemaVersion;
    public string Status { get; init; } = "running";
    public string ContractFingerprint { get; init; } =
        SampleSponzaTemporalCaptureContract.Fingerprint;
    public string SettingsFingerprint { get; init; } = string.Empty;
    public int Width { get; init; } = SampleSponzaTemporalCaptureContract.Width;
    public int Height { get; init; } = SampleSponzaTemporalCaptureContract.Height;
    public int FramesPerSecond { get; init; } =
        SampleSponzaTemporalCaptureContract.FramesPerSecond;
    public int WarmupFrameCount { get; init; } =
        SampleSponzaTemporalCaptureContract.WarmupFrameCount;
    public int ExpectedFrameCount { get; init; } =
        SampleSponzaTemporalCaptureContract.ExpectedFrameCount;
    public int ScreenshotCompletedCountAtStart { get; init; }
    public int ScreenshotCompletedCountAtEnd { get; init; }
    public string GpuDevice { get; init; } = "unknown-device";
    public string GpuDriver { get; init; } = "unknown-driver";
    public PerformanceCaptureRunMetadata CaptureRun { get; init; } =
        PerformanceCaptureRunMetadata.Unknown;
    public string Failure { get; init; } = string.Empty;
    public IReadOnlyList<SampleSponzaTemporalFrameArtifact> Frames { get; init; } =
        Array.Empty<SampleSponzaTemporalFrameArtifact>();
}

/// <summary>
/// Renderer-backed driver for the standalone temporal evidence run.
/// Screenshot requests are issued before Draw and diagnostics are sampled
/// immediately after that same Draw, preserving a one-to-one route mapping.
/// </summary>
public sealed class SampleSponzaTemporalCaptureRunner
{
    private readonly VulkanRenderer _renderer;
    private readonly FirstPersonCamera _camera;
    private readonly string _outputDirectory;
    private readonly Func<(int Width, int Height)> _viewportSize;
    private readonly Action _exit;
    private readonly SampleSponzaTemporalCaptureSequence _sequence = new();
    private readonly List<SampleSponzaTemporalFrameArtifact> _frames = [];
    private readonly string _settingsFingerprint;
    private readonly int _completedAtStart;
    private SampleSponzaGiTemporalTrace _routeTrace = new();
    private SampleSponzaTemporalCaptureInstruction? _preparedInstruction;
    private RendererDiagnostics _lastDiagnostics = RendererDiagnostics.Empty;
    private bool _renderDocRecenterCaptureAttempted;
    private bool _stopped;

    public SampleSponzaTemporalCaptureRunner(
        VulkanRenderer renderer,
        FirstPersonCamera camera,
        LightManager lightManager,
        string outputDirectory,
        Func<(int Width, int Height)> viewportSize,
        Action exit)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        ArgumentNullException.ThrowIfNull(lightManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _viewportSize = viewportSize ??
            throw new ArgumentNullException(nameof(viewportSize));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _outputDirectory = Path.GetFullPath(outputDirectory);

        EnsureEmptyOutputDirectory(_outputDirectory);
        Directory.CreateDirectory(_outputDirectory);
        ConfigureRenderer(lightManager);
        _settingsFingerprint =
            SampleRenderSettingsFingerprint.Capture(_renderer.Settings);
        _completedAtStart =
            _renderer.LastDiagnostics.ScreenshotCompletedCount;

        SampleSponzaTemporalCaptureContract.Write(
            _outputDirectory,
            _settingsFingerprint);
        WriteManifest("running", string.Empty, _renderer.LastDiagnostics);
        Console.WriteLine(
            $"Sponza temporal capture armed: " +
            $"warmup={SampleSponzaTemporalCaptureContract.WarmupFrameCount}, " +
            $"frames={SampleSponzaTemporalCaptureContract.ExpectedFrameCount}, " +
            $"resolution={SampleSponzaTemporalCaptureContract.Width}x" +
            $"{SampleSponzaTemporalCaptureContract.Height}, " +
            $"directory={_outputDirectory}.");
    }

    public void PrepareFrame(int viewportWidth, int viewportHeight)
    {
        if (_stopped)
            return;

        try
        {
            if (_preparedInstruction != null)
            {
                throw new InvalidOperationException(
                    "A Sponza temporal frame was prepared twice without being rendered.");
            }
            if (viewportWidth != SampleSponzaTemporalCaptureContract.Width ||
                viewportHeight != SampleSponzaTemporalCaptureContract.Height)
            {
                throw new InvalidOperationException(
                    $"Locked resolution is " +
                    $"{SampleSponzaTemporalCaptureContract.Width}x" +
                    $"{SampleSponzaTemporalCaptureContract.Height}, but the " +
                    $"current viewport is {viewportWidth}x{viewportHeight}.");
            }

            SampleSponzaTemporalCaptureInstruction instruction =
                _sequence.CurrentInstruction;
            ApplyCamera(instruction.Camera, viewportWidth, viewportHeight);
            QueueFirstLateralRecenterRenderDocCapture(instruction);
            if (instruction.CaptureFrame)
            {
                string relativePath =
                    SampleSponzaTemporalCaptureContract.GetFrameRelativePath(
                        instruction.Route,
                        instruction.StageFrameIndex);
                string path = ResolveBundlePath(relativePath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path) ?? _outputDirectory);
                _renderer.RequestScreenshot(path);
            }

            _preparedInstruction = instruction;
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
            SampleSponzaTemporalCaptureInstruction instruction =
                _preparedInstruction ?? throw new InvalidOperationException(
                    "A Sponza temporal frame rendered without a prepared instruction.");
            _preparedInstruction = null;
            _lastDiagnostics = diagnostics;

            if (instruction.CaptureFrame)
            {
                RecordCapturedFrame(instruction, diagnostics);
                if (instruction.StageFrameIndex ==
                    instruction.StageFrameCount - 1)
                {
                    WriteCurrentRouteTrace(instruction.Route);
                    _routeTrace = new SampleSponzaGiTemporalTrace();
                }
            }

            int completedDelta = Math.Max(
                0,
                diagnostics.ScreenshotCompletedCount - _completedAtStart);
            bool screenshotsComplete =
                completedDelta >=
                SampleSponzaTemporalCaptureContract.ExpectedFrameCount;
            _sequence.AdvanceAfterRenderedFrame(screenshotsComplete);
            if (_sequence.IsComplete)
                Complete(diagnostics);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void ConfigureRenderer(LightManager lightManager)
    {
        SampleLighting.Configure(lightManager, SampleLightingMode.DirectionalKey);
        SampleLighting.ConfigureRenderSettings(
            _renderer.Settings,
            SampleLightingMode.DirectionalKey);
        SampleEnvironment.Configure(
            _renderer,
            SampleEnvironmentMode.ProceduralOutdoor);
        SampleGlobalIlluminationValidation.ConfigureSponzaCaptureSettings(
            _renderer.Settings);
        SampleSponzaGlobalIlluminationProfile.ApplyPresentationOverlay(
            _renderer.Settings);

        RenderSettings settings = _renderer.Settings;
        settings.ResolutionScale = 1.0f;
        settings.DynamicResolution.Enabled = false;
        settings.DynamicResolution.MinimumScale = 1.0f;
        settings.DynamicResolution.MaximumScale = 1.0f;
        settings.Particles.Enabled = false;
        settings.Animation.Enabled = false;
        settings.Environment.AnimateTimeOfDay = false;
        settings.FeatureIsolation = RenderFeatureIsolationMode.FullFrame;
        settings.GlobalIllumination.DebugView =
            GlobalIlluminationDebugView.None;
        settings.AntiAliasing.DebugView = AntiAliasingDebugView.None;
        settings.ResetRenderViewOverrides();
        settings.Debug.Enabled = true;
        settings.Debug.AllowScreenshots = true;
        settings.Debug.AllowRenderDocCapture = true;
        settings.Debug.CpuSnapshotsEnabled = false;
        settings.Debug.AllowGpuTiming = true;
        _renderer.CaptureScenario = "SponzaTemporalStability";
    }

    private void QueueFirstLateralRecenterRenderDocCapture(
        SampleSponzaTemporalCaptureInstruction instruction)
    {
        if (_renderDocRecenterCaptureAttempted ||
            instruction.Stage != SampleSponzaTemporalCaptureStage.Horizontal ||
            !string.Equals(
                instruction.Phase,
                "outbound",
                StringComparison.Ordinal) ||
            !_renderer.WouldSimpleDdgiNearRingRecenter(
                _camera.Position,
                _camera.Forward))
        {
            return;
        }

        _renderDocRecenterCaptureAttempted = true;
        _renderer.RequestRenderDocCapture();
        Console.WriteLine(
            $"RenderDoc queued for first world-X DDGI recenter: " +
            $"routeFrame={instruction.StageFrameIndex}, " +
            $"camera=({_camera.Position.X:R},{_camera.Position.Y:R}," +
            $"{_camera.Position.Z:R}).");
    }

    private void RecordCapturedFrame(
        SampleSponzaTemporalCaptureInstruction instruction,
        RendererDiagnostics diagnostics)
    {
        int captureOrdinal = _frames.Count;
        string relativePath =
            SampleSponzaTemporalCaptureContract.GetFrameRelativePath(
                instruction.Route,
                instruction.StageFrameIndex);
        PerformanceCaptureCameraMetadata actualCamera =
            diagnostics.CaptureCamera;
        _frames.Add(new SampleSponzaTemporalFrameArtifact
        {
            CaptureOrdinal = captureOrdinal,
            Route = instruction.Route,
            Phase = instruction.Phase,
            RouteFrameIndex = instruction.StageFrameIndex,
            SimulationTimeSeconds = instruction.StageFrameIndex /
                (float)SampleSponzaTemporalCaptureContract.FramesPerSecond,
            RelativePath = relativePath,
            RendererFrameSerial = diagnostics.CaptureFrame.FrameSerial,
            TemporalSampleIndex = diagnostics.TemporalSampleIndex,
            CameraPositionX = actualCamera.PositionX,
            CameraPositionY = actualCamera.PositionY,
            CameraPositionZ = actualCamera.PositionZ,
            CameraYaw = actualCamera.YawRadians,
            CameraPitch = actualCamera.PitchRadians,
            CameraFieldOfView = actualCamera.FieldOfViewRadians,
            CameraNearPlane = actualCamera.NearPlane,
            CameraFarPlane = actualCamera.FarPlane,
            ViewHash = actualCamera.ViewHash,
            ProjectionHash = actualCamera.ProjectionHash,
            CameraCutSerial = actualCamera.CameraCutSerial,
            AntiAliasingMode = diagnostics.AntiAliasingMode,
            MotionVectorsEnabled = diagnostics.MotionVectorsEnabled != 0,
            JitterEnabled = diagnostics.JitterEnabled != 0,
            JitterX = diagnostics.JitterX,
            JitterY = diagnostics.JitterY,
            DdgiFrameRayBucket0 = diagnostics.SimpleDdgiFrameRayBucket0,
            DdgiFrameRayBucket1 = diagnostics.SimpleDdgiFrameRayBucket1,
            DdgiFrameRayBucket2 = diagnostics.SimpleDdgiFrameRayBucket2,
            DdgiFrameRayBucket3 = diagnostics.SimpleDdgiFrameRayBucket3,
            DdgiFrameRayBucket4 = diagnostics.SimpleDdgiFrameRayBucket4,
            DdgiFrameRayBucket5 = diagnostics.SimpleDdgiFrameRayBucket5,
            DdgiNearScrollCardinality = diagnostics.SimpleDdgiNearScrollCardinality,
            DdgiMidScrollCardinality = diagnostics.SimpleDdgiMidScrollCardinality,
            DdgiFarScrollCardinality = diagnostics.SimpleDdgiFarScrollCardinality,
            DdgiScrollPlannedExpectedCount =
                diagnostics.SimpleDdgiScrollRepairExpectedProbeCount,
            DdgiScrollExpectedCount = diagnostics.SimpleDdgiScrollGpuExpectedCount,
            DdgiScrollAcceptedCount = diagnostics.SimpleDdgiScrollGpuAcceptedCount,
            DdgiScrollTracedCount = diagnostics.SimpleDdgiScrollGpuTracedCount,
            DdgiScrollCommittedCount = diagnostics.SimpleDdgiScrollGpuCommittedCount,
            DdgiScrollUnbucketedCount = diagnostics.SimpleDdgiScrollUnbucketedCount,
            DdgiScrollCohortFailure = diagnostics.SimpleDdgiScrollCohortFailure,
            DdgiRebuildingRingMask = diagnostics.SimpleDdgiRebuildingRingMask,
            DdgiNearRebaseState = diagnostics.SimpleDdgiNearRebaseState,
            DdgiMidRebaseState = diagnostics.SimpleDdgiMidRebaseState,
            DdgiFarRebaseState = diagnostics.SimpleDdgiFarRebaseState
        });

        SampleSponzaGiCaptureStage traceStage =
            instruction.Stage == SampleSponzaTemporalCaptureStage.Horizontal
                ? SampleSponzaGiCaptureStage.MotionTraversal
                : SampleSponzaGiCaptureStage.VerticalTraversal;
        _routeTrace.Record(
            new SampleSponzaGiCaptureInstruction(
                traceStage,
                instruction.StageFrameIndex,
                instruction.StageFrameCount,
                instruction.Camera,
                null,
                instruction.Route,
                false),
            diagnostics);
    }

    private void WriteCurrentRouteTrace(string route)
    {
        _routeTrace.Write(
            ResolveBundlePath(
                SampleSponzaTemporalCaptureContract.GetTraceRelativePath(route)),
            SampleSponzaTemporalCaptureContract.Fingerprint,
            route);
    }

    private void Complete(RendererDiagnostics diagnostics)
    {
        if (_frames.Count !=
            SampleSponzaTemporalCaptureContract.ExpectedFrameCount)
        {
            throw new InvalidDataException(
                $"Expected {SampleSponzaTemporalCaptureContract.ExpectedFrameCount} " +
                $"frame records, found {_frames.Count}.");
        }

        for (int i = 0; i < _frames.Count; i++)
        {
            SampleSponzaTemporalFrameArtifact frame = _frames[i];
            string path = ResolveBundlePath(frame.RelativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Captured frame {frame.RelativePath} is missing.", path);
            var info = new FileInfo(path);
            if (info.Length <= 0)
            {
                throw new InvalidDataException(
                    $"Captured frame {frame.RelativePath} is empty.");
            }

            using FileStream stream = File.OpenRead(path);
            string sha256 = "sha256:" + Convert.ToHexString(
                SHA256.HashData(stream)).ToLowerInvariant();
            _frames[i] = frame with
            {
                ByteLength = info.Length,
                Sha256 = sha256
            };
        }

        SampleSponzaTemporalRunManifest capturedManifest =
            CreateManifest("running", string.Empty, diagnostics);
        WriteManifest(capturedManifest);
        SampleSponzaTemporalCaptureAnalyzer.Analyze(
            _outputDirectory,
            capturedManifest,
            Console.Out);
        WriteManifest("completed", string.Empty, diagnostics);
        _stopped = true;
        Environment.ExitCode = 0;
        Console.WriteLine(
            $"Sponza temporal capture completed: {_outputDirectory}");
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
                $"Could not write failed temporal manifest: " +
                manifestException.Message);
        }

        Environment.ExitCode = 1;
        Console.Error.WriteLine(
            $"Sponza temporal capture failed: {failure}");
        _exit();
    }

    private void WriteManifest(
        string status,
        string failure,
        RendererDiagnostics diagnostics) =>
        WriteManifest(CreateManifest(status, failure, diagnostics));

    private SampleSponzaTemporalRunManifest CreateManifest(
        string status,
        string failure,
        RendererDiagnostics diagnostics) => new()
    {
        Status = status,
        SettingsFingerprint = _settingsFingerprint,
        ScreenshotCompletedCountAtStart = _completedAtStart,
        ScreenshotCompletedCountAtEnd =
            diagnostics.ScreenshotCompletedCount,
        GpuDevice = diagnostics.CaptureGpuDeviceName,
        GpuDriver = diagnostics.CaptureGpuDriverVersion,
        CaptureRun = diagnostics.CaptureRun,
        Failure = failure,
        Frames = _frames.ToArray()
    };

    private void WriteManifest(SampleSponzaTemporalRunManifest manifest) =>
        SampleSponzaTemporalCaptureContract.WriteJsonAtomic(
            Path.Combine(
                _outputDirectory,
                SampleSponzaTemporalCaptureContract.RunFileName),
            manifest,
            "Sponza temporal capture run manifest");

    private string ResolveBundlePath(string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(_outputDirectory, normalized));
        string prefix = _outputDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _outputDirectory
            : _outputDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Capture artifact escapes the bundle: {relativePath}");
        }

        return path;
    }

    private void ApplyCamera(
        SampleSponzaGiCameraBookmark bookmark,
        int viewportWidth,
        int viewportHeight)
    {
        _camera.Position = bookmark.Position;
        _camera.Yaw = bookmark.Yaw;
        _camera.Pitch = bookmark.Pitch;
        _camera.FieldOfView = bookmark.FieldOfView;
        _camera.NearPlane = bookmark.NearPlane;
        _camera.FarPlane = bookmark.FarPlane;
        _camera.AspectRatio = (float)viewportWidth / viewportHeight;
        _camera.Update();
    }

    private static void EnsureEmptyOutputDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException(
                $"Sponza temporal capture directory must be absent or empty: {path}");
        }
    }
}
