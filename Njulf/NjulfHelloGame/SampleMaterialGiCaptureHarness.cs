using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Camera;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

public enum SampleMaterialGiCaptureStage : byte
{
    Warmup = 0,
    PresentOutput = 1,
    CaptureOutput = 2,
    AwaitReadback = 3,
    Complete = 4,
    Failed = 5
}

public sealed record SampleMaterialGiCaptureInstruction(
    SampleMaterialGiCaptureStage Stage,
    int WarmupFrameIndex,
    int OutputIndex,
    SampleMaterialGiCaptureOutput? Output,
    bool QueueCapture);

/// <summary>
/// Pure deterministic frame state machine. Each signal receives one
/// presentation frame before its capture frame; readback wait frames do not
/// advance the output index.
/// </summary>
public sealed class SampleMaterialGiCaptureSequence
{
    public const int MaximumReadbackWaitFrames = 16;

    private SampleMaterialGiCaptureStage _stage = SampleMaterialGiCaptureStage.Warmup;
    private int _warmupFramesRendered;
    private int _outputIndex;
    private int _readbackWaitFrames;
    private string _failureReason = string.Empty;

    public SampleMaterialGiCaptureStage Stage => _stage;
    public bool IsComplete => _stage == SampleMaterialGiCaptureStage.Complete;
    public bool IsFailed => _stage == SampleMaterialGiCaptureStage.Failed;
    public string FailureReason => _failureReason;

    public SampleMaterialGiCaptureInstruction CurrentInstruction
    {
        get
        {
            SampleMaterialGiCaptureOutput? output =
                _outputIndex < SampleMaterialGiConformanceCatalog.RequiredOutputs.Count
                    ? SampleMaterialGiConformanceCatalog.RequiredOutputs[_outputIndex]
                    : null;
            return new SampleMaterialGiCaptureInstruction(
                _stage,
                _warmupFramesRendered,
                _outputIndex,
                output,
                _stage == SampleMaterialGiCaptureStage.CaptureOutput);
        }
    }

    public void AdvanceAfterRenderedFrame(LinearHdrCaptureState readbackState)
    {
        switch (_stage)
        {
            case SampleMaterialGiCaptureStage.Warmup:
                _warmupFramesRendered++;
                if (_warmupFramesRendered >= SampleMaterialGiConformanceCatalog.WarmupFrameCount)
                    _stage = SampleMaterialGiCaptureStage.PresentOutput;
                break;
            case SampleMaterialGiCaptureStage.PresentOutput:
                _stage = SampleMaterialGiCaptureStage.CaptureOutput;
                break;
            case SampleMaterialGiCaptureStage.CaptureOutput:
                _readbackWaitFrames = 0;
                _stage = SampleMaterialGiCaptureStage.AwaitReadback;
                break;
            case SampleMaterialGiCaptureStage.AwaitReadback:
                AdvanceReadback(readbackState);
                break;
            case SampleMaterialGiCaptureStage.Complete:
            case SampleMaterialGiCaptureStage.Failed:
                break;
            default:
                Fail($"Unsupported material/GI capture stage '{_stage}'.");
                break;
        }
    }

    public void Fail(string reason)
    {
        _failureReason = string.IsNullOrWhiteSpace(reason)
            ? "Material/GI capture failed without a reason."
            : reason.Trim();
        _stage = SampleMaterialGiCaptureStage.Failed;
    }

    private void AdvanceReadback(LinearHdrCaptureState readbackState)
    {
        if (readbackState == LinearHdrCaptureState.Completed)
        {
            _outputIndex++;
            _readbackWaitFrames = 0;
            _stage = _outputIndex >= SampleMaterialGiConformanceCatalog.RequiredOutputs.Count
                ? SampleMaterialGiCaptureStage.Complete
                : SampleMaterialGiCaptureStage.PresentOutput;
            return;
        }

        if (readbackState == LinearHdrCaptureState.Failed)
        {
            Fail($"GPU readback failed for output index {_outputIndex}.");
            return;
        }

        if (readbackState == LinearHdrCaptureState.Unknown)
        {
            Fail($"GPU readback request disappeared for output index {_outputIndex}.");
            return;
        }

        _readbackWaitFrames++;
        if (_readbackWaitFrames > MaximumReadbackWaitFrames)
        {
            Fail(
                $"GPU readback for output index {_outputIndex} did not complete within " +
                $"{MaximumReadbackWaitFrames} rendered wait frames.");
        }
    }
}

public sealed record SampleMaterialGiArtifact(
    SampleMaterialGiCaptureSignal Signal,
    string FileStem,
    string RelativePath,
    string Sha256,
    long ByteLength,
    int Width,
    int Height,
    float MinimumComponent,
    float MaximumComponent);

public sealed record SampleMaterialGiRendererProvenance(
    string GpuDevice,
    string GpuDriver,
    string BuildConfiguration,
    string ApplicationVersion,
    string Commit,
    string ShaderBundleHash,
    int SettingsSchemaVersion,
    uint RenderWidth,
    uint RenderHeight,
    ulong SceneContentRevision,
    RenderQualityPreset QualityPreset,
    MaterialGiV2Feature ActiveMaterialGiV2Features,
    GlobalIlluminationMode GlobalIlluminationMode,
    AsyncComputeMode AsyncComputeRequestedMode,
    AsyncComputeMode AsyncComputeEffectiveMode,
    int AsyncComputeSubmittedComputeSegmentCount,
    PerformanceCaptureCameraMetadata Camera)
{
    public string SettingsFingerprint { get; init; } = string.Empty;
}

public sealed record SampleMaterialGiFloatFormatDescriptor(
    string SchemaVersion,
    string FileExtension,
    string MediaType,
    string ColorSpace,
    string ComponentType,
    int ComponentCount,
    string SerializedRowOrder,
    string LogicalOrigin,
    string NonfinitePolicy);

public sealed record SampleMaterialGiRunManifest(
    string SchemaVersion,
    string Status,
    string FailureReason,
    string ContractFingerprint,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int RequiredOutputCount,
    SampleMaterialGiFloatFormatDescriptor FloatFormat,
    SampleMaterialGiRendererProvenance? Renderer,
    IReadOnlyList<SampleMaterialGiArtifact> Artifacts)
{
    public SampleMaterialGiSemanticEvidenceReport? SemanticEvidence { get; init; }
}

/// <summary>
/// Atomic, fail-closed material/GI evidence publication.
/// </summary>
public static class SampleMaterialGiArtifactPublisher
{
    public const string ManifestFileName = "material-gi-conformance-manifest.json";
    public const string FloatFormatFileName = "material-gi-linear-float-format.json";
    public const string ManifestSchemaVersion = "material-gi-capture-run/v3";
    public const string FloatFormatSchemaVersion = "njulf-linear-float-pfm/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SampleMaterialGiFloatFormatDescriptor FloatFormat { get; } = new(
        FloatFormatSchemaVersion,
        ".pfm",
        PfmLinearImageCodec.MediaType,
        PfmLinearImageCodec.ColorSpace,
        "IEEE-754 float32 little-endian",
        3,
        "PFM bottom-to-top",
        "top-left",
        "reject capture if any RGB component is NaN or infinity");

    public static void PrepareOutputDirectory(string outputDirectory)
    {
        string directory = NormalizeDirectory(outputDirectory);
        Directory.CreateDirectory(directory);

        var protectedNames = new HashSet<string>(
            new[]
            {
                ManifestFileName,
                FloatFormatFileName,
                "material-gi-conformance-contract.json",
                "material-gi-conformance-capture.json"
            },
            StringComparer.OrdinalIgnoreCase);
        foreach (SampleMaterialGiCaptureOutput output in SampleMaterialGiConformanceCatalog.RequiredOutputs)
            protectedNames.Add(GetRelativeArtifactPath(output));

        string[] conflicts = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => protectedNames.Contains(Path.GetFileName(path)))
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
        if (conflicts.Length > 0)
        {
            throw new IOException(
                $"Material/GI capture directory '{directory}' contains prior evidence files: " +
                $"{string.Join(", ", conflicts)}. Use an empty directory to prevent stale-artifact publication.");
        }

        WriteJsonAtomic(Path.Combine(directory, FloatFormatFileName), FloatFormat);
    }

    public static string GetRelativeArtifactPath(SampleMaterialGiCaptureOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        int index = -1;
        for (int candidate = 0;
             candidate < SampleMaterialGiConformanceCatalog.RequiredOutputs.Count;
             candidate++)
        {
            if (SampleMaterialGiConformanceCatalog.RequiredOutputs[candidate] == output)
            {
                index = candidate;
                break;
            }
        }
        if (index < 0)
            throw new ArgumentException("Output is not part of the material/GI conformance contract.", nameof(output));
        return $"{index:00}-{output.FileStem}.pfm";
    }

    public static SampleMaterialGiArtifact VerifyArtifact(
        string outputDirectory,
        SampleMaterialGiCaptureOutput output)
    {
        string directory = NormalizeDirectory(outputDirectory);
        string relativePath = GetRelativeArtifactPath(output);
        string path = ResolveContainedPath(directory, relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required material/GI artifact '{relativePath}' is missing.", path);

        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            $"Material/GI artifact '{relativePath}'");
        byte[] encoded = evidence.Bytes;
        LinearFloatImage image = PfmLinearImageCodec.Decode(encoded);
        if (image.Width != SampleMaterialGiConformanceCatalog.LockedWidth ||
            image.Height != SampleMaterialGiConformanceCatalog.LockedHeight)
        {
            throw new InvalidDataException(
                $"Artifact '{relativePath}' is {image.Width}x{image.Height}; " +
                $"{SampleMaterialGiConformanceCatalog.LockedWidth}x{SampleMaterialGiConformanceCatalog.LockedHeight} is required.");
        }

        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        foreach (float component in image.Pixels)
        {
            if (!float.IsFinite(component))
                throw new InvalidDataException($"Artifact '{relativePath}' contains a non-finite component.");
            minimum = Math.Min(minimum, component);
            maximum = Math.Max(maximum, component);
        }

        return new SampleMaterialGiArtifact(
            output.Signal,
            output.FileStem,
            relativePath,
            evidence.Sha256,
            encoded.LongLength,
            image.Width,
            image.Height,
            minimum,
            maximum);
    }

    public static void WriteInProgressManifest(
        string outputDirectory,
        DateTimeOffset startedAtUtc)
    {
        WriteManifest(
            outputDirectory,
            new SampleMaterialGiRunManifest(
                ManifestSchemaVersion,
                "in-progress",
                string.Empty,
                SampleMaterialGiConformanceCatalog.Fingerprint,
                startedAtUtc.ToUniversalTime(),
                null,
                SampleMaterialGiConformanceCatalog.RequiredOutputs.Count,
                FloatFormat,
                null,
                Array.Empty<SampleMaterialGiArtifact>()));
    }

    public static void WriteFailedManifest(
        string outputDirectory,
        DateTimeOffset startedAtUtc,
        string reason,
        IReadOnlyList<SampleMaterialGiArtifact> completedArtifacts)
    {
        WriteManifest(
            outputDirectory,
            new SampleMaterialGiRunManifest(
                ManifestSchemaVersion,
                "failed",
                string.IsNullOrWhiteSpace(reason) ? "Capture failed without a reason." : reason.Trim(),
                SampleMaterialGiConformanceCatalog.Fingerprint,
                startedAtUtc.ToUniversalTime(),
                DateTimeOffset.UtcNow,
                SampleMaterialGiConformanceCatalog.RequiredOutputs.Count,
                FloatFormat,
                null,
                completedArtifacts.ToArray()));
    }

    public static void WritePassedManifest(
        string outputDirectory,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        SampleMaterialGiRendererProvenance renderer,
        IReadOnlyList<SampleMaterialGiArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ValidateCompleteArtifactSet(outputDirectory, artifacts);
        SampleMaterialGiSemanticEvidenceReport semanticEvidence =
            SampleMaterialGiSemanticEvidenceGate.EvaluateCapture(outputDirectory, artifacts);
        if (!semanticEvidence.Passed)
            throw new InvalidDataException(semanticEvidence.FailureReason);
        WriteManifest(
            outputDirectory,
            new SampleMaterialGiRunManifest(
                ManifestSchemaVersion,
                "passed",
                string.Empty,
                SampleMaterialGiConformanceCatalog.Fingerprint,
                startedAtUtc.ToUniversalTime(),
                completedAtUtc.ToUniversalTime(),
                SampleMaterialGiConformanceCatalog.RequiredOutputs.Count,
                FloatFormat,
                renderer,
                artifacts.ToArray())
            {
                SemanticEvidence = semanticEvidence
            });
    }

    public static void ValidateCompleteArtifactSet(
        string outputDirectory,
        IReadOnlyList<SampleMaterialGiArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count != SampleMaterialGiConformanceCatalog.RequiredOutputs.Count)
        {
            throw new InvalidDataException(
                $"A passed capture requires exactly {SampleMaterialGiConformanceCatalog.RequiredOutputs.Count} artifacts; " +
                $"{artifacts.Count} were supplied.");
        }

        string directory = NormalizeDirectory(outputDirectory);
        foreach (SampleMaterialGiCaptureOutput output in SampleMaterialGiConformanceCatalog.RequiredOutputs)
        {
            string relativePath = GetRelativeArtifactPath(output);
            SampleMaterialGiArtifact[] matches = artifacts
                .Where(artifact =>
                    artifact.Signal == output.Signal &&
                    string.Equals(artifact.RelativePath, relativePath, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"Required output '{output.FileStem}' has {matches.Length} artifact records.");

            SampleMaterialGiArtifact artifact = matches[0];
            string path = ResolveContainedPath(directory, artifact.RelativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required artifact '{artifact.RelativePath}' is missing.", path);
            string actualHash = PfmLinearImageCodec.ComputeSha256(path);
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Artifact '{artifact.RelativePath}' changed after verification.");
            if (artifact.Width != SampleMaterialGiConformanceCatalog.LockedWidth ||
                artifact.Height != SampleMaterialGiConformanceCatalog.LockedHeight)
            {
                throw new InvalidDataException($"Artifact '{artifact.RelativePath}' has invalid locked dimensions.");
            }
        }
    }

    private static void WriteManifest(string outputDirectory, SampleMaterialGiRunManifest manifest)
    {
        string directory = NormalizeDirectory(outputDirectory);
        Directory.CreateDirectory(directory);
        WriteJsonAtomic(Path.Combine(directory, ManifestFileName), manifest);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions);
        SampleEvidenceFileIo.WriteAtomic(
            path,
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Material/GI capture manifest");
    }

    private static string NormalizeDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("A material/GI capture directory is required.", nameof(outputDirectory));
        return Path.GetFullPath(outputDirectory);
    }

    private static string ResolveContainedPath(string directory, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
        string root = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact path '{relativePath}' escapes capture directory '{directory}'.");
        return fullPath;
    }
}

/// <summary>
/// Applies the immutable capture profile and bridges the pure frame sequence to
/// the renderer's fence-safe SceneColor capture API.
/// </summary>
public sealed class SampleMaterialGiCaptureRunner
{
    private readonly VulkanRenderer _renderer;
    private readonly FirstPersonCamera _camera;
    private readonly LightManager _lightManager;
    private readonly string _outputDirectory;
    private readonly Action _exit;
    private readonly Func<(int Width, int Height)> _getWindowSize;
    private readonly AsyncComputeMode _asyncComputeMode;
    private readonly SampleMaterialGiCaptureSequence _sequence = new();
    private readonly List<SampleMaterialGiArtifact> _artifacts = [];
    private readonly DateTimeOffset _startedAtUtc;
    private string? _activeCapturePath;
    private RendererDiagnostics? _lastSubmittedFrameDiagnostics;
    private bool _captureQueuedForInstruction;
    private bool _terminalPublicationWritten;

    public SampleMaterialGiCaptureRunner(
        VulkanRenderer renderer,
        FirstPersonCamera camera,
        LightManager lightManager,
        string outputDirectory,
        Func<(int Width, int Height)> getWindowSize,
        Action exit,
        AsyncComputeMode asyncComputeMode = AsyncComputeMode.Disabled)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
        _outputDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(outputDirectory)
                ? throw new ArgumentException("A material/GI capture directory is required.", nameof(outputDirectory))
                : outputDirectory);
        _getWindowSize = getWindowSize ?? throw new ArgumentNullException(nameof(getWindowSize));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        if (asyncComputeMode is not (
                AsyncComputeMode.Disabled or
                AsyncComputeMode.ForceEnabledForValidation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(asyncComputeMode),
                "Material/GI evidence requires an explicit graphics-only or forced-async mode.");
        }
        _asyncComputeMode = asyncComputeMode;
        _startedAtUtc = DateTimeOffset.UtcNow;

        SampleMaterialGiArtifactPublisher.PrepareOutputDirectory(_outputDirectory);
        SampleMaterialGiConformanceCatalog.WriteContract(_outputDirectory);
        SampleMaterialGiArtifactPublisher.WriteInProgressManifest(_outputDirectory, _startedAtUtc);
        ConfigureLockedLighting();
        ApplyLockedSettings(_renderer.Settings, _asyncComputeMode);
        ApplyLockedCamera(_camera);
        _renderer.CaptureScenario = SampleMaterialGiConformanceCatalog.Scenario.ToString();
    }

    public SampleMaterialGiCaptureSequence Sequence => _sequence;

    public void CancelIfIncomplete(string reason)
    {
        if (_sequence.IsComplete || _terminalPublicationWritten)
            return;
        Abort(reason);
    }

    public void PrepareFrame()
    {
        if (_sequence.IsComplete || _sequence.IsFailed)
            return;

        try
        {
            // Update runs between render callbacks. LastDiagnostics therefore
            // includes EndFrame's submitted-segment counters for the preceding
            // fully submitted frame, unlike diagnostics sampled from Draw.
            _lastSubmittedFrameDiagnostics = _renderer.LastDiagnostics;
            ValidateWindowSize();
            ApplyLockedCamera(_camera);
            ApplyLockedSettings(_renderer.Settings, _asyncComputeMode);
            SampleMaterialGiCaptureInstruction instruction = _sequence.CurrentInstruction;
            ApplySignal(instruction.Output);
            if (instruction.QueueCapture && !_captureQueuedForInstruction)
            {
                SampleMaterialGiCaptureOutput output = instruction.Output
                    ?? throw new InvalidOperationException("Capture instruction has no required output.");
                string relativePath = SampleMaterialGiArtifactPublisher.GetRelativeArtifactPath(output);
                _activeCapturePath = Path.Combine(_outputDirectory, relativePath);
                if (!_renderer.RequestLinearHdrCapture(_activeCapturePath))
                {
                    throw new InvalidOperationException(
                        "Renderer rejected linear HDR capture because debug screenshot permission is disabled.");
                }

                _captureQueuedForInstruction = true;
            }
        }
        catch (Exception exception)
        {
            Abort($"Material/GI frame preparation failed: {DescribeException(exception)}");
            throw;
        }
    }

    public void OnFrameRendered()
    {
        if (_sequence.IsComplete || _sequence.IsFailed)
            return;

        try
        {
            SampleMaterialGiCaptureInstruction instruction = _sequence.CurrentInstruction;
            LinearHdrCaptureState readbackState = LinearHdrCaptureState.Unknown;
            string readbackError = string.Empty;
            if (instruction.Stage == SampleMaterialGiCaptureStage.AwaitReadback)
            {
                if (string.IsNullOrWhiteSpace(_activeCapturePath))
                    throw new InvalidOperationException("Readback stage has no active capture path.");
                LinearHdrCaptureResult result = _renderer.GetLinearHdrCaptureResult(_activeCapturePath);
                readbackState = result.State;
                readbackError = result.Error;
            }

            SampleMaterialGiCaptureStage priorStage = instruction.Stage;
            SampleMaterialGiCaptureOutput? priorOutput = instruction.Output;
            _sequence.AdvanceAfterRenderedFrame(readbackState);

            if (priorStage == SampleMaterialGiCaptureStage.AwaitReadback &&
                readbackState == LinearHdrCaptureState.Completed)
            {
                SampleMaterialGiCaptureOutput output = priorOutput
                    ?? throw new InvalidOperationException("Completed readback has no capture output.");
                _artifacts.Add(SampleMaterialGiArtifactPublisher.VerifyArtifact(_outputDirectory, output));
                _activeCapturePath = null;
                _captureQueuedForInstruction = false;
            }

            if (_sequence.IsFailed)
            {
                string reason = string.IsNullOrWhiteSpace(readbackError)
                    ? _sequence.FailureReason
                    : $"{_sequence.FailureReason} Renderer: {readbackError}";
                Abort(reason);
                throw new InvalidOperationException(reason);
            }

            if (_sequence.IsComplete)
            {
                PublishSuccess();
                _exit();
            }
        }
        catch (Exception exception) when (!_terminalPublicationWritten)
        {
            Abort($"Material/GI capture failed: {DescribeException(exception)}");
            throw;
        }
    }

    public static void ApplyLockedSettings(
        RenderSettings settings,
        AsyncComputeMode asyncComputeMode = AsyncComputeMode.Disabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (asyncComputeMode is not (
                AsyncComputeMode.Disabled or
                AsyncComputeMode.ForceEnabledForValidation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(asyncComputeMode),
                "Material/GI evidence requires an explicit graphics-only or forced-async mode.");
        }
        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        settings.ResolutionScale = 1f;
        settings.DynamicResolution.Enabled = false;
        settings.Exposure = SampleMaterialGiConformanceCatalog.LockedExposure;
        settings.AutoExposure.Enabled = false;
        settings.AntiAliasing.Mode = AntiAliasingMode.None;
        settings.AntiAliasing.JitterEnabled = false;
        settings.AsyncCompute.Mode = asyncComputeMode;
        settings.Transparency.Mode = TransparencyMode.SortedAlphaBlend;
        settings.Fog.Enabled = false;
        settings.Bloom.Enabled = false;
        settings.Particles.Enabled = false;
        settings.Animation.Enabled = true;
        settings.Animation.SkinningMode = AnimationSkinningMode.GpuCompute;
        settings.Animation.DebugView = AnimationDebugView.None;
        settings.Shadows.DirectionalShadowsEnabled = true;
        settings.Shadows.SpotShadowsEnabled = false;
        settings.Shadows.PointShadowsEnabled = false;
        settings.SceneSubmission.ValidationCompareCpuGpuLists = false;
        settings.Debug.Enabled = true;
        settings.Debug.AllowScreenshots = true;

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        // The capture executable is a conformance surface, so it must select
        // the complete V2 contract itself instead of depending on whichever
        // scene setup happened to run before the harness was constructed.
        gi.EnableMaterialGiV2ForConformance();
        gi.Enabled = true;
        gi.EmergencyGiFallbackEnabled = false;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.UseDdgi = true;
        gi.DdgiAdaptiveBudgetingEnabled = false;
        gi.FarFieldClipmapEnabled = true;
        gi.FarFieldPagedEnabled = true;
        gi.FarFieldForceAll = false;
        gi.FarFieldSkyVisibilityEnabled = true;
        gi.FarFieldSunShadowEnabled = true;
        gi.TemporalEnabled = true;
        gi.DenoiserEnabled = true;
        gi.DebugView = GlobalIlluminationDebugView.None;
        settings.Materials.DebugView = MaterialDebugView.None;
    }

    public static void ApplyLockedCamera(FirstPersonCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        SampleSponzaGiCameraBookmark bookmark = SampleMaterialGiConformanceCatalog.Camera;
        camera.Position = bookmark.Position;
        camera.Yaw = bookmark.Yaw;
        camera.Pitch = bookmark.Pitch;
        camera.FieldOfView = bookmark.FieldOfView;
        camera.NearPlane = bookmark.NearPlane;
        camera.FarPlane = bookmark.FarPlane;
        camera.AspectRatio =
            (float)SampleMaterialGiConformanceCatalog.LockedWidth /
            SampleMaterialGiConformanceCatalog.LockedHeight;
        camera.Update();
    }

    private void ApplySignal(SampleMaterialGiCaptureOutput? output)
    {
        _renderer.Settings.Materials.DebugView = MaterialDebugView.None;
        _renderer.Settings.GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
        if (output == null)
            return;

        _renderer.Settings.Materials.DebugView = output.Signal switch
        {
            SampleMaterialGiCaptureSignal.DirectDiffuse =>
                MaterialDebugView.CaptureLinearDirectDiffuse,
            SampleMaterialGiCaptureSignal.DirectSpecular =>
                MaterialDebugView.CaptureLinearDirectSpecular,
            _ => output.MaterialDebugView
        };
        _renderer.Settings.GlobalIllumination.DebugView = output.GlobalIlluminationDebugView;
    }

    private void ConfigureLockedLighting()
    {
        CoreVector3 direction = SampleMaterialGiConformanceCatalog.DirectionalLightDirection;
        CoreVector3 color = SampleMaterialGiConformanceCatalog.DirectionalLightColor;
        _lightManager.ClearLights();
        _lightManager.AddLight(new Light
        {
            Type = LightType.Directional,
            Direction = Vector3.Normalize(new Vector3(direction.X, direction.Y, direction.Z)),
            Color = new Vector3(color.X, color.Y, color.Z),
            Intensity = SampleMaterialGiConformanceCatalog.DirectionalLightIntensity,
            CastsShadows = true,
            ShadowStrength = 1f,
            ShadowPriority = 100
        });
    }

    private void ValidateWindowSize()
    {
        (int width, int height) = _getWindowSize();
        if (width != SampleMaterialGiConformanceCatalog.LockedWidth ||
            height != SampleMaterialGiConformanceCatalog.LockedHeight)
        {
            throw new InvalidOperationException(
                $"Material/GI capture requires a locked " +
                $"{SampleMaterialGiConformanceCatalog.LockedWidth}x{SampleMaterialGiConformanceCatalog.LockedHeight} window; " +
                $"the current size is {width}x{height}.");
        }
    }

    private void PublishSuccess()
    {
        RendererDiagnostics diagnostics = _lastSubmittedFrameDiagnostics
            ?? throw new InvalidOperationException(
                "No fully submitted renderer diagnostics were observed before publication.");
        if (diagnostics.CaptureRenderWidth != SampleMaterialGiConformanceCatalog.LockedWidth ||
            diagnostics.CaptureRenderHeight != SampleMaterialGiConformanceCatalog.LockedHeight)
        {
            throw new InvalidDataException(
                $"Renderer provenance reports {diagnostics.CaptureRenderWidth}x{diagnostics.CaptureRenderHeight}, " +
                "which does not match the locked capture extent.");
        }

        string shaderSha256 = NormalizeShaderSha256(diagnostics.CaptureRun.ShaderBundleHash);
        var provenance = new SampleMaterialGiRendererProvenance(
            diagnostics.CaptureGpuDeviceName,
            diagnostics.CaptureGpuDriverVersion,
            diagnostics.CaptureRun.BuildConfiguration,
            diagnostics.CaptureRun.ApplicationVersion,
            diagnostics.CaptureRun.Commit,
            diagnostics.CaptureRun.ShaderBundleHash,
            diagnostics.CaptureRun.SettingsSchemaVersion,
            diagnostics.CaptureRenderWidth,
            diagnostics.CaptureRenderHeight,
            diagnostics.CaptureSceneContentRevision,
            diagnostics.ActiveQualityPreset,
            _renderer.Settings.GlobalIllumination.ActiveMaterialGiV2Features,
            _renderer.Settings.GlobalIllumination.Mode,
            diagnostics.AsyncComputeRequestedMode,
            diagnostics.AsyncComputeEffectiveMode,
            diagnostics.AsyncComputeSubmittedComputeSegmentCount,
            diagnostics.CaptureCamera)
        {
            SettingsFingerprint =
                SampleRenderSettingsFingerprint.Capture(_renderer.Settings)
        };
        DateTimeOffset completedAtUtc = DateTimeOffset.UtcNow;
        SampleMaterialGiCaptureMetadata metadata =
            SampleMaterialGiConformanceCatalog.CreateCaptureMetadata(
                completedAtUtc,
                diagnostics.CaptureRun.Commit,
                shaderSha256,
                diagnostics.CaptureGpuDeviceName,
                diagnostics.CaptureGpuDriverVersion);
        SampleMaterialGiConformanceCatalog.WriteCaptureMetadata(_outputDirectory, metadata);
        SampleMaterialGiArtifactPublisher.WritePassedManifest(
            _outputDirectory,
            _startedAtUtc,
            completedAtUtc,
            provenance,
            _artifacts);
        _terminalPublicationWritten = true;
        Console.WriteLine(
            $"Material/GI linear HDR conformance capture passed: {_outputDirectory} " +
            $"artifacts={_artifacts.Count} async={diagnostics.AsyncComputeEffectiveMode} " +
            $"device={diagnostics.CaptureGpuDeviceName}");
    }

    private void Abort(string reason)
    {
        if (_terminalPublicationWritten)
            return;
        _sequence.Fail(reason);
        SampleMaterialGiArtifactPublisher.WriteFailedManifest(
            _outputDirectory,
            _startedAtUtc,
            reason,
            _artifacts);
        _terminalPublicationWritten = true;
        Console.Error.WriteLine($"Material/GI linear HDR conformance capture failed: {reason}");
    }

    private static string NormalizeShaderSha256(string value)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Renderer shader provenance '{value}' is not a SHA-256 bundle identity.");
        }

        string hash = value[prefix.Length..].Trim();
        if (hash.Length != 64 || hash.Any(static character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Renderer shader provenance '{value}' is malformed.");
        return hash.ToLowerInvariant();
    }

    private static string DescribeException(Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return $"{exception.GetType().Name}: {message}";
    }
}
