using System.Globalization;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

/// <summary>
/// Runs one complete named camera route outside all benchmark timing samples,
/// queues checkpoint readbacks before their owning Draw, and lets the route
/// continue while frame-slot fences drain. Only the final authored route pose
/// may be held after the route is complete.
/// </summary>
public sealed class SampleBenchmarkQualitySequenceRunner
{
    private const int RequiredConsecutiveReadyFrameCount = 30;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private sealed class PendingCheckpoint
    {
        public required int Ordinal { get; init; }
        public required int RouteFrameIndex { get; init; }
        public int AbsoluteFrameIndex { get; set; } = -1;
        public required string Path { get; init; }
        public required string Token { get; init; }
        public bool Requested { get; set; }
        public RendererDiagnostics? Diagnostics { get; set; }
        public string SettingsFingerprint { get; set; } = string.Empty;
        public MaterialGiProducerIdentity? ProducerIdentity { get; set; }
        public bool Completed { get; set; }
        public bool Terminal { get; set; }
        public SampleEvidenceFileContent? CapturedPfmEvidence { get; set; }
    }

    private readonly SampleBenchmarkQualitySequenceOptions _options;
    private readonly SamplePerformanceScenario _scenario;
    private readonly Action _exit;
    private readonly Func<string> _getSettingsFingerprint;
    private readonly Func<string, string, bool> _requestLinearHdrCapture;
    private readonly Func<string, LinearHdrCaptureResult> _getLinearHdrCaptureResult;
    private readonly IReadOnlyList<int> _checkpointIndices;
    private readonly PendingCheckpoint[] _checkpoints;
    private readonly Dictionary<int, PendingCheckpoint> _checkpointsByFrame;
    private readonly SampleBenchmarkQualitySequenceLoadedReference? _reference;
    private readonly List<SampleBenchmarkQualityRouteObservation> _routeObservations = new();
    private int _routeFramesRendered;
    private int _firstRouteAbsoluteFrameIndex = -1;
    private int _preparedRouteFrameIndex = -1;
    private SampleBenchmarkCameraPose? _preparedCamera;
    private SampleBistroQualityFrameState? _preparedBistroState;
    private string? _preparedSettingsFingerprint;
    private PerformanceCaptureCameraMetadata? _frozenStationaryCamera;
    private int _additionalSettlingFrameCount;
    private int _consecutiveReadyFrameCount;
    private int _readbackDrainFrameCount;
    private bool _routeStarted;
    private bool _settlingWaitTimedOut;
    private bool _completed;
    private bool _failureDrainActive;
    private readonly List<string> _failures = new();

    public SampleBenchmarkQualitySequenceRunner(
        SampleBenchmarkQualitySequenceOptions options,
        SamplePerformanceScenario scenario,
        Action exit,
        Func<string> getSettingsFingerprint,
        Func<string, string, bool> requestLinearHdrCapture,
        Func<string, LinearHdrCaptureResult> getLinearHdrCaptureResult)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scenario = scenario;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _getSettingsFingerprint = getSettingsFingerprint ??
            throw new ArgumentNullException(nameof(getSettingsFingerprint));
        _requestLinearHdrCapture = requestLinearHdrCapture ??
            throw new ArgumentNullException(nameof(requestLinearHdrCapture));
        _getLinearHdrCaptureResult = getLinearHdrCaptureResult ??
            throw new ArgumentNullException(nameof(getLinearHdrCaptureResult));
        ValidateOptions(options);
        if (scenario != options.Scenario)
        {
            throw new ArgumentException(
                "Quality-sequence scenario differs between options and runner.",
                nameof(scenario));
        }

        _checkpointIndices =
            SampleBenchmarkQualityCheckpointCatalog.GetCheckpointIndices(
                options.Trajectory);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        _checkpoints = _checkpointIndices
            .Select((routeFrame, ordinal) => new PendingCheckpoint
            {
                Ordinal = ordinal,
                RouteFrameIndex = routeFrame,
                Path = Path.Combine(
                    outputDirectory,
                    $"checkpoint-{routeFrame:D4}.pfm"),
                Token = CreateCaptureToken(options, ordinal, routeFrame)
            })
            .ToArray();
        foreach (PendingCheckpoint checkpoint in _checkpoints)
        {
            if (File.Exists(checkpoint.Path))
            {
                throw new IOException(
                    $"Quality-sequence checkpoint already exists: {checkpoint.Path}");
            }
        }
        string reportPath = Path.GetFullPath(options.ReportPath);
        if (_checkpoints.Any(checkpoint => string.Equals(
                Path.GetFullPath(checkpoint.Path),
                reportPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Quality-sequence report path cannot alias a checkpoint PFM path.",
                nameof(options));
        }
        if (File.Exists(reportPath))
        {
            throw new IOException(
                $"Quality-sequence report already exists: {options.ReportPath}");
        }
        _checkpointsByFrame = _checkpoints.ToDictionary(
            static checkpoint => checkpoint.RouteFrameIndex);
        _reference = options.Role == SampleBenchmarkQualitySequenceRole.Canonical
            ? null
            : SampleBenchmarkQualitySequenceReferenceLoader.Load(options);
    }

    public SampleBenchmarkQualitySequenceReport? Report { get; private set; }
    public string? ReportPath { get; private set; }
    public bool RouteStarted => _routeStarted;
    public bool HoldTrajectoryForReadbackDrain =>
        _routeStarted &&
        _routeFramesRendered >=
            SampleBenchmarkTrajectory.GetFrameCount(_options.Trajectory);

    public int ResolveTrajectoryFrameIndexForNextRender(int absoluteFrameIndex)
    {
        if (!_routeStarted)
        {
            return SampleBenchmarkTrajectory.GetWarmupFrameIndex(
                _options.Trajectory,
                absoluteFrameIndex);
        }
        return Math.Min(
            _routeFramesRendered,
            SampleBenchmarkTrajectory.GetFrameCount(_options.Trajectory) - 1);
    }

    public int ResolveBistroControllerFrameIndexForNextRender(
        int absoluteFrameIndex)
    {
        int routeFrame = ResolveTrajectoryFrameIndexForNextRender(
            absoluteFrameIndex);
        return _routeStarted
            ? checked(
                SampleBistroQualityCaptureContract.FirstMeasuredFrame +
                routeFrame)
            : routeFrame;
    }

    /// <summary>
    /// Called from Update after camera/scene controls and before DrawScene.
    /// The request queued here is consumed by the same rendered route frame.
    /// </summary>
    public void PrepareFrame(
        int absoluteFrameIndex,
        SampleBenchmarkCameraPose preDrawCamera,
        SampleBistroQualityFrameState? bistroFrameState)
    {
        if (_completed || !_routeStarted)
            return;
        ArgumentNullException.ThrowIfNull(preDrawCamera);

        int routeFrame = ResolveTrajectoryFrameIndexForNextRender(
            absoluteFrameIndex);
        _preparedRouteFrameIndex = routeFrame;
        _preparedCamera = preDrawCamera;
        _preparedBistroState = bistroFrameState;
        _preparedSettingsFingerprint = null;
        try
        {
            ValidatePreDrawFrame(
                routeFrame,
                preDrawCamera,
                bistroFrameState);
            string settingsFingerprint = _getSettingsFingerprint();
            SampleBenchmarkQualitySequenceReferenceLoader.RequireSha256Identity(
                settingsFingerprint,
                "quality route pre-Draw settings fingerprint");
            _preparedSettingsFingerprint = settingsFingerprint;
        }
        catch (Exception exception)
        {
            RecordFailure(
                $"Quality route frame {routeFrame} pre-Draw attestation failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        if (_failureDrainActive)
            return;
        if (_routeFramesRendered >=
            SampleBenchmarkTrajectory.GetFrameCount(_options.Trajectory))
        {
            return;
        }
        if (!_checkpointsByFrame.TryGetValue(
                routeFrame,
                out PendingCheckpoint? checkpoint))
        {
            return;
        }
        if (checkpoint.Requested)
        {
            RecordFailure(
                $"Checkpoint route frame {routeFrame} was requested more than once.");
            return;
        }

        try
        {
            if (!_requestLinearHdrCapture(checkpoint.Path, checkpoint.Token))
            {
                RecordFailure(
                    $"Renderer rejected quality checkpoint route frame {routeFrame}.");
                return;
            }
            checkpoint.Requested = true;
        }
        catch (Exception exception)
        {
            RecordFailure(
                $"Quality checkpoint {routeFrame} request failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public void OnFrameRendered(
        int absoluteFrameIndex,
        RendererDiagnostics diagnostics)
    {
        if (_completed)
            return;
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!_routeStarted)
        {
            ObserveWarmupAndTryArmRoute(absoluteFrameIndex, diagnostics);
            return;
        }

        int routeFrameCount =
            SampleBenchmarkTrajectory.GetFrameCount(_options.Trajectory);
        if (_routeFramesRendered < routeFrameCount)
        {
            int expectedRouteFrame = _routeFramesRendered;
            if (expectedRouteFrame == 0)
                _firstRouteAbsoluteFrameIndex = absoluteFrameIndex;
            if (_preparedRouteFrameIndex != expectedRouteFrame)
            {
                RecordFailure(
                    $"Quality route frame {expectedRouteFrame} was rendered without " +
                    "a matching pre-Draw preparation.");
            }
            _checkpointsByFrame.TryGetValue(
                expectedRouteFrame,
                out PendingCheckpoint? owningCheckpoint);
            if (owningCheckpoint?.Requested == true)
            {
                // Preserve the renderer submission-frame identity even if a
                // separate camera/settings invariant fails and triggers a
                // bounded failure drain.
                owningCheckpoint.Diagnostics = diagnostics;
                owningCheckpoint.AbsoluteFrameIndex = absoluteFrameIndex;
            }
            try
            {
                if (absoluteFrameIndex != checked(
                        _firstRouteAbsoluteFrameIndex + expectedRouteFrame))
                {
                    throw new InvalidDataException(
                        "Quality route absolute frames are not consecutive from route zero.");
                }
                ValidateRenderedRouteFrame(expectedRouteFrame, diagnostics);
                string settingsFingerprint = _preparedSettingsFingerprint ??
                    throw new InvalidDataException(
                        "Pre-Draw settings fingerprint evidence is absent.");
                MaterialGiProducerIdentity producer =
                    SampleMaterialGiProducerIdentityFactory.Create(
                        diagnostics,
                        settingsFingerprint,
                        ResolveQualityTier(diagnostics.ActiveBudgetProfile));
                if (owningCheckpoint?.Requested == true)
                {
                    owningCheckpoint.SettingsFingerprint = settingsFingerprint;
                    owningCheckpoint.ProducerIdentity = producer;
                }
                string postDrawSettingsFingerprint = _getSettingsFingerprint();
                RequireExact(
                    postDrawSettingsFingerprint,
                    settingsFingerprint,
                    "settings fingerprint changed between pre-Draw and post-Draw");
                var observation = new SampleBenchmarkQualityRouteObservation(
                    expectedRouteFrame,
                    _preparedCamera ?? throw new InvalidDataException(
                        "Pre-Draw camera evidence is absent."),
                    _preparedBistroState,
                    diagnostics,
                    settingsFingerprint,
                    producer);
                ValidateRouteObservation(observation);
                _routeObservations.Add(observation);
            }
            catch (Exception exception)
            {
                RecordFailure(
                    $"Quality route frame {expectedRouteFrame} evidence failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
            if (owningCheckpoint is { } requiredCheckpoint &&
                !requiredCheckpoint.Requested &&
                !_failureDrainActive)
            {
                RecordFailure(
                    $"Quality checkpoint {expectedRouteFrame} was not queued before Draw.");
            }
            _routeFramesRendered++;
            _preparedRouteFrameIndex = -1;
            _preparedCamera = null;
            _preparedBistroState = null;
            _preparedSettingsFingerprint = null;
        }
        else
        {
            ValidateHeldFrame(diagnostics, routeFrameCount - 1);
            _readbackDrainFrameCount++;
        }

        PollCheckpointCaptures();
        if (_completed)
            return;
        bool allRequestedTerminal = _checkpoints
            .Where(static checkpoint => checkpoint.Requested)
            .All(static checkpoint => checkpoint.Terminal);
        bool allCheckpointsCompleted = _checkpoints
            .All(static checkpoint => checkpoint.Completed);
        if (_routeFramesRendered == routeFrameCount &&
            ((_failureDrainActive && allRequestedTerminal) ||
             (!_failureDrainActive && allCheckpointsCompleted)))
        {
            Finish();
            return;
        }
        if (_routeFramesRendered == routeFrameCount &&
            _readbackDrainFrameCount >=
                _options.MaximumReadbackDrainFrameCount)
        {
            RecordFailure(
                "Quality checkpoint readbacks did not complete within " +
                $"{_options.MaximumReadbackDrainFrameCount} drain frames.");
            Finish();
        }
    }

    private void ObserveWarmupAndTryArmRoute(
        int absoluteFrameIndex,
        RendererDiagnostics diagnostics)
    {
        _consecutiveReadyFrameCount =
            SampleBenchmarkRunner.IsReadyForMeasurement(diagnostics)
                ? Math.Min(
                    RequiredConsecutiveReadyFrameCount,
                    _consecutiveReadyFrameCount + 1)
                : 0;
        if (absoluteFrameIndex < _options.WarmupFrameCount)
        {
            if (absoluteFrameIndex == _options.WarmupFrameCount - 1 &&
                _consecutiveReadyFrameCount >= RequiredConsecutiveReadyFrameCount &&
                SampleBenchmarkTrajectory.CanStartMeasurementAfterFrame(
                    _options.Trajectory,
                    absoluteFrameIndex))
            {
                ArmRoute(diagnostics);
            }
            return;
        }

        if (_consecutiveReadyFrameCount < RequiredConsecutiveReadyFrameCount)
        {
            if (_additionalSettlingFrameCount <
                _options.MaximumAdditionalSettlingFrameCount)
            {
                _additionalSettlingFrameCount++;
                return;
            }
            _settlingWaitTimedOut = true;
            RecordFailure(
                "Quality sequence did not reach thirty consecutive ready frames " +
                $"within {_options.MaximumAdditionalSettlingFrameCount} additional frames.");
            Finish();
            return;
        }

        if (!SampleBenchmarkTrajectory.CanStartMeasurementAfterFrame(
                _options.Trajectory,
                absoluteFrameIndex))
        {
            if (_additionalSettlingFrameCount <
                _options.MaximumAdditionalSettlingFrameCount)
            {
                _additionalSettlingFrameCount++;
                return;
            }
            _settlingWaitTimedOut = true;
            RecordFailure(
                "Quality sequence exhausted settling frames before the closed " +
                "route reached its authored frame-zero boundary.");
            Finish();
            return;
        }

        // Arm route frame zero for the next Update. No readback is requested
        // from this warmup/alignment frame.
        ArmRoute(diagnostics);
    }

    private void ArmRoute(RendererDiagnostics diagnostics)
    {
        if (_options.Trajectory == SampleBenchmarkTrajectoryKind.Stationary)
        {
            SampleBenchmarkQualitySequenceReferenceLoader.ValidateCamera(
                diagnostics.CaptureCamera,
                "frozen stationary camera");
            _frozenStationaryCamera = diagnostics.CaptureCamera;
        }
        _routeStarted = true;
    }

    private void ValidatePreDrawFrame(
        int routeFrame,
        SampleBenchmarkCameraPose actualCamera,
        SampleBistroQualityFrameState? actualBistroState)
    {
        SampleBenchmarkCameraPose? authoredCamera =
            SampleBenchmarkTrajectory.ResolveCamera(
                _options.Trajectory,
                routeFrame,
                _options.TrajectoryBistroVariant);
        if (authoredCamera != null)
            RequirePoseEqual(actualCamera, authoredCamera, "authored pre-Draw camera");
        else
        {
            PerformanceCaptureCameraMetadata frozen = _frozenStationaryCamera ??
                throw new InvalidDataException(
                    "Generic stationary route did not freeze its arming-frame camera.");
            RequirePoseEqual(actualCamera, frozen, "frozen stationary pre-Draw camera");
        }

        if (SampleBenchmarkTrajectory.RequiresBistro(_options.Trajectory))
        {
            int controllerFrame = checked(
                SampleBistroQualityCaptureContract.FirstMeasuredFrame + routeFrame);
            SampleBistroQualityFrameState expected =
                new SampleBistroQualityCaptureContract(
                    _options.TrajectoryBistroVariant)
                    .ResolveFrame(controllerFrame);
            if (actualBistroState == null || actualBistroState != expected)
            {
                throw new InvalidDataException(
                    $"Bistro LastAppliedState does not match authored frame " +
                    $"FirstMeasuredFrame+{routeFrame} ({controllerFrame}).");
            }
        }
        else if (actualBistroState != null)
        {
            throw new InvalidDataException(
                "A non-Bistro quality route supplied Bistro script state.");
        }
    }

    private void ValidateRenderedRouteFrame(
        int routeFrame,
        RendererDiagnostics diagnostics)
    {
        if (_options.Trajectory == SampleBenchmarkTrajectoryKind.Stationary)
        {
            RequireCameraEqual(
                diagnostics.CaptureCamera,
                _frozenStationaryCamera ?? throw new InvalidDataException(
                    "Generic stationary route did not retain a frozen camera."));
        }
        else
        {
            IReadOnlyList<string> cameraMismatches =
                SampleBenchmarkTrajectory.ValidateCamera(
                    _options.Trajectory,
                    routeFrame,
                    _options.TrajectoryBistroVariant,
                    diagnostics.CaptureCamera);
            if (cameraMismatches.Count != 0)
            {
                throw new InvalidDataException(
                    string.Join("; ", cameraMismatches));
            }
        }
        if (_preparedCamera == null)
            throw new InvalidDataException("Pre-Draw camera evidence is absent.");
        RequirePoseEqual(
            _preparedCamera,
            diagnostics.CaptureCamera,
            "pre-Draw and submitted camera");
    }

    private void ValidateHeldFrame(
        RendererDiagnostics diagnostics,
        int heldRouteFrame)
    {
        try
        {
            if (_preparedRouteFrameIndex != heldRouteFrame ||
                _preparedCamera == null)
            {
                throw new InvalidDataException(
                    $"Readback drain did not prepare held route frame {heldRouteFrame}.");
            }
            ValidatePreDrawFrame(
                heldRouteFrame,
                _preparedCamera,
                _preparedBistroState);
            ValidateRenderedRouteFrame(heldRouteFrame, diagnostics);
            string preparedSettingsFingerprint =
                _preparedSettingsFingerprint ?? throw new InvalidDataException(
                    "Readback drain has no pre-Draw settings fingerprint.");
            RequireExact(
                _getSettingsFingerprint(),
                preparedSettingsFingerprint,
                "readback-drain settings fingerprint changed between pre-Draw and post-Draw");
        }
        catch (Exception exception)
        {
            RecordFailure(
                $"Quality readback hold frame {heldRouteFrame} attestation failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ValidateRouteObservation(
        SampleBenchmarkQualityRouteObservation observation)
    {
        RendererDiagnostics diagnostics = observation.Diagnostics;
        SampleBenchmarkQualitySequenceReferenceLoader.ValidateCamera(
            diagnostics.CaptureCamera,
            "quality route camera");
        SampleBenchmarkQualitySequenceReferenceLoader.RequireSha256Identity(
            diagnostics.CaptureSceneAssetHash,
            "quality route scene asset hash");
        SampleBenchmarkQualitySequenceReferenceLoader.RequireSha256Identity(
            diagnostics.CaptureSceneStateHash,
            "quality route scene state hash");
        SampleBenchmarkQualitySequenceReferenceLoader.RequireSha256Identity(
            observation.SettingsFingerprint,
            "quality route settings fingerprint");
        SampleBenchmarkQualitySequenceReferenceLoader.ValidateProducer(
            observation.ProducerIdentity,
            "quality route producer");
        SampleBenchmarkQualitySequenceReferenceLoader.ValidateCaptureRun(
            diagnostics.CaptureRun,
            "quality route capture run");
        if (diagnostics.CaptureRenderWidth !=
                SampleBenchmarkQualityCheckpointCatalog.RequiredWidth ||
            diagnostics.CaptureRenderHeight !=
                SampleBenchmarkQualityCheckpointCatalog.RequiredHeight)
        {
            throw new InvalidDataException(
                $"Quality route render extent {diagnostics.CaptureRenderWidth}x" +
                $"{diagnostics.CaptureRenderHeight} is not " +
                $"{SampleBenchmarkQualityCheckpointCatalog.RequiredWidth}x" +
                $"{SampleBenchmarkQualityCheckpointCatalog.RequiredHeight}.");
        }
        if (diagnostics.CaptureFrame.FrameSerial == ulong.MaxValue)
        {
            throw new InvalidDataException(
                "Quality route DDGI frame serial is absent or invalid.");
        }
        RequireExact(
            observation.SettingsFingerprint[7..],
            observation.ProducerIdentity.SettingsFingerprint,
            "settings and producer fingerprint");
        RequireExact(
            diagnostics.CaptureRun.Commit.ToLowerInvariant(),
            observation.ProducerIdentity.BuildCommit,
            "capture-run and producer commit");
        RequireExact(
            diagnostics.CaptureRun.ShaderBundleHash[7..],
            observation.ProducerIdentity.ShaderFingerprint,
            "capture-run and producer shader");

        if (_routeObservations.Count == 0)
        {
            RequireExact(
                diagnostics.CaptureRun.SceneKind,
                SampleBenchmarkQualityWorkloadIdentity.GetCaptureSceneKind(
                    _options.SceneKind),
                "quality workload scene");
            RequireExact(
                diagnostics.CaptureRun.Scenario,
                _options.Scenario.ToString(),
                "quality workload scenario");
            if (_options.BudgetProfileOverride.HasValue &&
                diagnostics.ActiveBudgetProfile !=
                    _options.BudgetProfileOverride.Value)
            {
                throw new InvalidDataException(
                    "Quality workload active budget profile differs from its requested profile.");
            }
            if (_reference != null)
            {
                SampleBenchmarkQualitySequenceReferenceContract contract =
                    _reference.Contract;
                RequireExact(
                    diagnostics.CaptureRun.BuildConfiguration,
                    contract.BuildConfiguration,
                    "reference build configuration");
                RequireExact(
                    observation.SettingsFingerprint,
                    contract.Checkpoints[0].SettingsFingerprint,
                    "reference settings fingerprint");
                RequireExact(
                    observation.ProducerIdentity.GpuName,
                    contract.ProducerIdentity.GpuName,
                    "reference producer GPU");
                RequireExact(
                    observation.ProducerIdentity.DriverVersion,
                    contract.ProducerIdentity.DriverVersion,
                    "reference producer driver");
                RequireExact(
                    observation.ProducerIdentity.QualityTier,
                    contract.ProducerIdentity.QualityTier,
                    "reference producer quality tier");
                if (diagnostics.CaptureRun.SettingsSchemaVersion !=
                    contract.CaptureRun.SettingsSchemaVersion)
                {
                    throw new InvalidDataException(
                        "Reference and candidate capture settings schema differ.");
                }
                if (_options.Role == SampleBenchmarkQualitySequenceRole.Repeat)
                {
                    SampleBenchmarkQualitySequenceReferenceLoader.RequireCaptureRunEqual(
                        diagnostics.CaptureRun,
                        contract.CaptureRun,
                        "baseline repeat top-level CaptureRun");
                    SampleBenchmarkQualitySequenceReferenceLoader.RequireProducerEqual(
                        observation.ProducerIdentity,
                        contract.ProducerIdentity,
                        "baseline repeat top-level producer");
                }
            }
            return;
        }
        SampleBenchmarkQualityRouteObservation first = _routeObservations[0];
        SampleBenchmarkQualityRouteObservation previous = _routeObservations[^1];
        if (diagnostics.CaptureFrame.FrameSerial != checked(
                previous.Diagnostics.CaptureFrame.FrameSerial + 1))
        {
            throw new InvalidDataException(
                "Quality route DDGI frame serials are not strictly contiguous.");
        }
        RequireExact(
            diagnostics.CaptureSceneAssetHash,
            first.Diagnostics.CaptureSceneAssetHash,
            "within-sequence scene asset hash");
        if (diagnostics.ActiveBudgetProfile !=
            first.Diagnostics.ActiveBudgetProfile)
        {
            throw new InvalidDataException(
                "Within-sequence active budget profile changed.");
        }
        RequireExact(
            observation.SettingsFingerprint,
            first.SettingsFingerprint,
            "within-sequence settings fingerprint");
        RequireProducerEqual(
            observation.ProducerIdentity,
            first.ProducerIdentity,
            "within-sequence producer");
        if (diagnostics.CaptureRun != first.Diagnostics.CaptureRun)
        {
            throw new InvalidDataException(
                "Within-sequence CaptureRun identity changed.");
        }
    }

    private void PollCheckpointCaptures()
    {
        foreach (PendingCheckpoint checkpoint in _checkpoints)
        {
            if (!checkpoint.Requested || checkpoint.Terminal)
                continue;
            LinearHdrCaptureResult result;
            try
            {
                result = _getLinearHdrCaptureResult(checkpoint.Path);
            }
            catch (Exception exception)
            {
                RecordFailure(
                    $"Quality checkpoint {checkpoint.RouteFrameIndex} status failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                continue;
            }

            if (checkpoint.Diagnostics == null)
            {
                RecordFailure(
                    $"Quality checkpoint {checkpoint.RouteFrameIndex} has no " +
                    "submission-frame diagnostics.");
                continue;
            }
            string expectedPath = Path.GetFullPath(checkpoint.Path);
            string observedPath;
            try
            {
                observedPath = Path.GetFullPath(result.OutputPath);
            }
            catch (Exception exception)
            {
                observedPath = string.Empty;
                RecordFailure(
                    $"Quality checkpoint {checkpoint.RouteFrameIndex} result path is invalid: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
            if (!string.Equals(
                    observedPath,
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                RecordFailure(
                    $"Quality checkpoint {checkpoint.RouteFrameIndex} result path differs " +
                    "from its canonical requested output path.");
            }
            if (!string.Equals(
                    result.CaptureToken,
                    checkpoint.Token,
                    StringComparison.Ordinal))
            {
                RecordFailure(
                    $"Quality checkpoint {checkpoint.RouteFrameIndex} capture token changed.");
            }
            switch (result.State)
            {
                case LinearHdrCaptureState.Unknown:
                    RecordFailure(
                        $"Quality checkpoint {checkpoint.RouteFrameIndex} status became unknown.");
                    break;
                case LinearHdrCaptureState.Queued:
                    // The host observes this immediately after DrawScene but before
                    // VulkanRenderer.EndFrame publishes Queued -> Submitted. The
                    // frame serial is therefore validated on the next observation.
                    break;
                case LinearHdrCaptureState.Submitted:
                    ValidateCaptureFrameSerial(checkpoint, result);
                    break;
                case LinearHdrCaptureState.Completed:
                    ValidateCaptureFrameSerial(checkpoint, result);
                    checkpoint.Completed = true;
                    checkpoint.Terminal = true;
                    break;
                case LinearHdrCaptureState.Failed:
                    ValidateCaptureFrameSerial(checkpoint, result);
                    checkpoint.Terminal = true;
                    RecordFailure(
                        $"Quality checkpoint {checkpoint.RouteFrameIndex} failed: " +
                        (string.IsNullOrWhiteSpace(result.Error)
                            ? "renderer supplied no reason"
                            : result.Error));
                    break;
                default:
                    RecordFailure(
                        $"Quality checkpoint {checkpoint.RouteFrameIndex} has an invalid state.");
                    break;
            }
        }
    }

    private void ValidateCaptureFrameSerial(
        PendingCheckpoint checkpoint,
        LinearHdrCaptureResult result)
    {
        if (result.FrameSerial != checkpoint.Diagnostics!.CaptureFrame.FrameSerial)
        {
            RecordFailure(
                $"Quality checkpoint {checkpoint.RouteFrameIndex} readback frame " +
                $"serial {result.FrameSerial} differs from diagnostics frame " +
                $"{checkpoint.Diagnostics.CaptureFrame.FrameSerial}.");
        }
    }

    private void RecordFailure(string reason)
    {
        if (!_failures.Contains(reason, StringComparer.Ordinal))
            _failures.Add(reason);
        _failureDrainActive = true;
    }

    private void Finish()
    {
        if (_completed)
            return;
        _completed = true;

        var evidence = new List<SampleBenchmarkQualityCheckpointEvidence>();
        if (_checkpoints.Any(static checkpoint => !checkpoint.Completed))
        {
            _failures.Add(
                "Quality sequence did not publish every authored checkpoint in exact order.");
        }
        foreach (PendingCheckpoint checkpoint in _checkpoints)
        {
            if (!checkpoint.Completed ||
                checkpoint.Diagnostics == null ||
                checkpoint.ProducerIdentity == null)
            {
                continue;
            }
            try
            {
                evidence.Add(BuildCheckpointEvidence(checkpoint));
            }
            catch (Exception exception)
            {
                _failures.Add(
                    $"Quality checkpoint {checkpoint.RouteFrameIndex} evidence failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        string routeHash = "unavailable";
        string sequenceHash = "unavailable";
        try
        {
            routeHash = ResolveRouteHash(evidence);
            sequenceHash = SampleBenchmarkQualityRouteSequenceHasher.Create(
                _options,
                _routeObservations);
        }
        catch (Exception exception)
        {
            _failures.Add(
                "Quality full-route identity failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        if (_reference != null &&
            !string.Equals(
                routeHash,
                _reference.Contract.TrajectoryRouteHash,
                StringComparison.Ordinal))
        {
            _failures.Add("Quality route hash differs from the canonical reference.");
        }
        if (_reference != null &&
            !string.Equals(
                sequenceHash,
                _reference.Contract.TrajectorySequenceHash,
                StringComparison.Ordinal))
        {
            _failures.Add(
                "Quality full-route observed sequence differs from the canonical reference.");
        }
        List<SampleBenchmarkQualityTemporalResult> temporal;
        try
        {
            temporal = BuildTemporalEvidence(evidence);
        }
        catch (Exception exception)
        {
            temporal = new List<SampleBenchmarkQualityTemporalResult>();
            _failures.Add(
                "Quality temporal evidence failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        try
        {
            AssertAdmittedEvidenceUnchanged();
        }
        catch (Exception exception)
        {
            _failures.Add(
                "Quality evidence changed during comparison: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        if (evidence.Count != _checkpointIndices.Count)
        {
            _failures.Add(
                $"Quality sequence emitted {evidence.Count} of " +
                $"{_checkpointIndices.Count} required checkpoint records.");
        }
        ulong previousCheckpointSerial = 0;
        ulong firstCheckpointSerial = 0;
        for (int index = 0; index < evidence.Count; index++)
        {
            if (index == 0)
                firstCheckpointSerial = evidence[index].DdgiFrameSerial;
            if (evidence[index].Ordinal != index ||
                evidence[index].RouteFrameIndex != _checkpointIndices[index] ||
                evidence[index].AbsoluteFrameIndex != checked(
                    _firstRouteAbsoluteFrameIndex +
                    evidence[index].RouteFrameIndex) ||
                evidence[index].DdgiFrameSerial == ulong.MaxValue ||
                (index > 0 &&
                 evidence[index].DdgiFrameSerial <= previousCheckpointSerial) ||
                evidence[index].DdgiFrameSerial != checked(
                    firstCheckpointSerial +
                    (ulong)evidence[index].RouteFrameIndex))
            {
                _failures.Add(
                    "Quality checkpoint records are missing, duplicated, or reordered.");
                break;
            }
            previousCheckpointSerial = evidence[index].DdgiFrameSerial;
        }

        string[] failures = _failures
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Report = new SampleBenchmarkQualitySequenceReport(
            SampleBenchmarkQualitySequenceReport.CurrentKind,
            SampleBenchmarkQualitySequenceReport.CurrentSchema,
            DateTimeOffset.UtcNow,
            _options.Role,
            _options.SequenceId,
            SampleBenchmarkQualityWorkloadIdentity.GetCaptureSceneKind(
                _options.SceneKind),
            _scenario.ToString(),
            SampleBenchmarkCaptureVariant.Normalize(_options.CaptureVariant),
            SampleBenchmarkTrajectory.GetName(_options.Trajectory),
            _options.TrajectoryFingerprint,
            routeHash,
            sequenceHash,
            SampleBenchmarkTrajectory.GetFrameCount(_options.Trajectory),
            _firstRouteAbsoluteFrameIndex,
            SampleBenchmarkQualityCheckpointCatalog.CreateFingerprint(
                _options.Trajectory),
            Array.AsReadOnly(_checkpointIndices.ToArray()),
            Array.AsReadOnly(evidence.ToArray()),
            Array.AsReadOnly(temporal.ToArray()),
            failures.Length == 0,
            Array.AsReadOnly(failures))
        {
            TimingEligible = false,
            ProductionTiming = false,
            WarmupFrameCount = _options.WarmupFrameCount,
            MaximumAdditionalSettlingFrameCount =
                _options.MaximumAdditionalSettlingFrameCount,
            MaximumReadbackDrainFrameCount =
                _options.MaximumReadbackDrainFrameCount,
            AdditionalSettlingFrameCount = _additionalSettlingFrameCount,
            SettlingWaitTimedOut = _settlingWaitTimedOut,
            ReferenceContractPath = _reference?.Path ?? string.Empty,
            ReferenceContractSha256 = _reference?.Sha256 ?? string.Empty,
            BuildConfiguration = _routeObservations.FirstOrDefault()
                ?.Diagnostics.CaptureRun.BuildConfiguration ?? string.Empty,
            CaptureRun = _routeObservations.FirstOrDefault()?.Diagnostics.CaptureRun,
            ProducerIdentity = _routeObservations.FirstOrDefault()?.ProducerIdentity
        };
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                Report,
                SerializerOptions);
            ReportPath = SampleEvidenceFileIo.WriteAtomic(
                Path.GetFullPath(_options.ReportPath),
                payload,
                SampleEvidenceFileIo.MaximumJsonBytes,
                "Benchmark quality-sequence report").Path;
            Console.WriteLine(
                $"Quality sequence report exported: {ReportPath} " +
                $"checkpoints={Report.Checkpoints.Count}/{_checkpointIndices.Count} " +
                $"passed={Report.Passed} timingEligible={Report.TimingEligible}.");
        }
        finally
        {
            _exit();
        }
    }

    private SampleBenchmarkQualityCheckpointEvidence BuildCheckpointEvidence(
        PendingCheckpoint checkpoint)
    {
        RendererDiagnostics diagnostics = checkpoint.Diagnostics!;
        SampleEvidenceFileContent candidate = SampleEvidenceFileIo.Read(
            checkpoint.Path,
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            $"Quality checkpoint {checkpoint.RouteFrameIndex}");
        LinearFloatImage image = PfmLinearImageCodec.Decode(candidate.Bytes);
        checkpoint.CapturedPfmEvidence = candidate;
        for (int component = 0; component < image.Pixels.Length; component++)
        {
            if (!float.IsFinite(image.Pixels[component]))
            {
                throw new InvalidDataException(
                    $"Quality checkpoint contains a non-finite component at " +
                    $"scalar index {component}.");
            }
        }
        if (image.Width != SampleBenchmarkQualityCheckpointCatalog.RequiredWidth ||
            image.Height != SampleBenchmarkQualityCheckpointCatalog.RequiredHeight)
        {
            throw new InvalidDataException(
                $"Quality checkpoint extent {image.Width}x{image.Height} is not " +
                $"{SampleBenchmarkQualityCheckpointCatalog.RequiredWidth}x" +
                $"{SampleBenchmarkQualityCheckpointCatalog.RequiredHeight}.");
        }
        SampleBenchmarkHdrDifference difference =
            SampleBenchmarkHdrDifference.Unavailable(
                "Canonical quality checkpoint; no reference comparison requested.");
        if (_reference != null)
        {
            SampleBenchmarkQualitySequenceReferenceCheckpoint expected =
                _reference.Contract.Checkpoints[checkpoint.Ordinal];
            ValidateCheckpointIdentity(checkpoint, image, expected);
            difference = SampleBenchmarkHdrComparer.Compare(
                _reference.CheckpointPfmEvidence[checkpoint.Ordinal],
                candidate,
                _options.HdrMaximumRelativeRmse,
                _options.HdrMaximumFlipP95,
                _reference.QualityContractEvidence);
            if (!difference.Available || !difference.Passed)
            {
                _failures.Add(
                    $"Quality checkpoint {checkpoint.RouteFrameIndex} image gate failed: " +
                    difference.FailureReason);
            }
        }
        return new SampleBenchmarkQualityCheckpointEvidence(
            checkpoint.Ordinal,
            checkpoint.RouteFrameIndex,
            checkpoint.AbsoluteFrameIndex,
            candidate.Path,
            candidate.Sha256,
            image.Width,
            image.Height,
            checkpoint.Token,
            diagnostics.CaptureFrame.FrameSerial,
            diagnostics.CaptureCamera,
            diagnostics.CaptureSceneAssetHash,
            diagnostics.CaptureSceneStateHash,
            diagnostics.CaptureSceneContentRevision,
            checkpoint.SettingsFingerprint,
            diagnostics.CaptureRun,
            checkpoint.ProducerIdentity!,
            difference);
    }

    private void AssertAdmittedEvidenceUnchanged()
    {
        foreach (PendingCheckpoint checkpoint in _checkpoints)
        {
            if (!checkpoint.CapturedPfmEvidence.HasValue)
                continue;
            SampleEvidenceFileContent admitted = checkpoint.CapturedPfmEvidence.Value;
            SampleEvidenceFileContent current = SampleEvidenceFileIo.Read(
                admitted.Path,
                SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
                $"Final quality checkpoint {checkpoint.RouteFrameIndex}");
            RequireExact(current.Sha256, admitted.Sha256, "candidate PFM hash");
        }
        if (_reference == null)
            return;
        SampleEvidenceFileContent referenceContract = SampleEvidenceFileIo.Read(
            _reference.Path,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Final quality reference contract");
        RequireExact(
            referenceContract.Sha256,
            _reference.Sha256,
            "reference contract hash");
        for (int index = 0; index < _reference.CheckpointPfmEvidence.Count; index++)
        {
            SampleEvidenceFileContent admitted =
                _reference.CheckpointPfmEvidence[index];
            SampleEvidenceFileContent current = SampleEvidenceFileIo.Read(
                admitted.Path,
                SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
                $"Final quality reference checkpoint {index}");
            RequireExact(current.Sha256, admitted.Sha256, "reference PFM hash");
        }
        SampleEvidenceFileContent qualityContract = SampleEvidenceFileIo.Read(
            _reference.QualityContractEvidence.Path,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Final quality ROI contract");
        RequireExact(
            qualityContract.Sha256,
            _reference.QualityContractEvidence.Sha256,
            "quality contract hash");
    }

    private void ValidateCheckpointIdentity(
        PendingCheckpoint actual,
        LinearFloatImage actualImage,
        SampleBenchmarkQualitySequenceReferenceCheckpoint expected)
    {
        RendererDiagnostics diagnostics = actual.Diagnostics!;
        MaterialGiProducerIdentity producer = actual.ProducerIdentity!;
        if (actual.Ordinal != expected.Ordinal ||
            actual.RouteFrameIndex != expected.RouteFrameIndex ||
            actual.AbsoluteFrameIndex != checked(
                _firstRouteAbsoluteFrameIndex + actual.RouteFrameIndex))
        {
            throw new InvalidDataException(
                "Checkpoint ordinal or route-frame identity differs from reference.");
        }
        if (actualImage.Width != expected.Width ||
            actualImage.Height != expected.Height)
        {
            throw new InvalidDataException(
                "Checkpoint PFM extent differs from reference.");
        }
        RequireCameraEqual(diagnostics.CaptureCamera, expected.Camera);
        RequireExact(
            diagnostics.CaptureSceneAssetHash,
            expected.SceneAssetHash,
            "scene asset hash");
        RequireExact(
            diagnostics.CaptureSceneStateHash,
            expected.SceneStateHash,
            "scene state hash");
        if (diagnostics.CaptureSceneContentRevision !=
            expected.SceneContentRevision)
        {
            throw new InvalidDataException(
                "Checkpoint scene content revision differs from reference.");
        }
        RequireExact(
            actual.SettingsFingerprint,
            expected.SettingsFingerprint,
            "settings fingerprint");
        RequireExact(
            diagnostics.CaptureRun.SceneKind,
            expected.CaptureRun.SceneKind,
            "capture scene");
        RequireExact(
            diagnostics.CaptureRun.Scenario,
            expected.CaptureRun.Scenario,
            "capture scenario");
        RequireExact(
            diagnostics.CaptureRun.BuildConfiguration,
            expected.CaptureRun.BuildConfiguration,
            "build configuration");
        if (diagnostics.CaptureRun.SettingsSchemaVersion !=
            expected.CaptureRun.SettingsSchemaVersion)
        {
            throw new InvalidDataException(
                "Capture settings schema differs from reference.");
        }
        RequireExact(producer.Schema, expected.ProducerIdentity.Schema, "producer schema");
        RequireExact(
            producer.SettingsFingerprint,
            expected.ProducerIdentity.SettingsFingerprint,
            "producer settings");
        RequireExact(producer.GpuName, expected.ProducerIdentity.GpuName, "producer GPU");
        RequireExact(
            producer.DriverVersion,
            expected.ProducerIdentity.DriverVersion,
            "producer driver");
        RequireExact(
            producer.QualityTier,
            expected.ProducerIdentity.QualityTier,
            "producer quality tier");
        if (_options.Role == SampleBenchmarkQualitySequenceRole.Repeat)
        {
            SampleBenchmarkQualitySequenceReferenceLoader.RequireCaptureRunEqual(
                diagnostics.CaptureRun,
                expected.CaptureRun,
                "baseline repeat CaptureRun");
            SampleBenchmarkQualitySequenceReferenceLoader.RequireProducerEqual(
                producer,
                expected.ProducerIdentity,
                "baseline repeat producer");
        }
    }

    private List<SampleBenchmarkQualityTemporalResult> BuildTemporalEvidence(
        IReadOnlyList<SampleBenchmarkQualityCheckpointEvidence> evidence)
    {
        var results = new List<SampleBenchmarkQualityTemporalResult>();
        if (_reference == null || evidence.Count != _checkpointIndices.Count)
            return results;
        Dictionary<int, int> referenceOrdinalByFrame = _reference.Contract.Checkpoints
            .ToDictionary(
                static checkpoint => checkpoint.RouteFrameIndex,
                static checkpoint => checkpoint.Ordinal);
        IReadOnlyList<SampleBenchmarkQualityTemporalPair> pairs =
            SampleBenchmarkQualityCheckpointCatalog.GetTemporalPairs(
                _options.Trajectory);
        for (int index = 0; index < pairs.Count; index++)
        {
            SampleBenchmarkQualityTemporalPair pair = pairs[index];
            double residual = SampleBenchmarkQualityTemporalComparer.Compare(
                _reference.CheckpointPfmEvidence[
                    referenceOrdinalByFrame[pair.FromRouteFrameIndex]],
                _reference.CheckpointPfmEvidence[
                    referenceOrdinalByFrame[pair.ToRouteFrameIndex]],
                _checkpointsByFrame[pair.FromRouteFrameIndex]
                    .CapturedPfmEvidence ?? throw new InvalidDataException(
                        "Temporal source checkpoint bytes were not admitted."),
                _checkpointsByFrame[pair.ToRouteFrameIndex]
                    .CapturedPfmEvidence ?? throw new InvalidDataException(
                        "Temporal destination checkpoint bytes were not admitted."));
            double? maximum = _options.Role ==
                SampleBenchmarkQualitySequenceRole.Candidate
                    ? _reference.Contract.TemporalGates[index]
                        .MaximumRelativeResidual
                    : null;
            bool passed = !maximum.HasValue || residual <= maximum.Value;
            if (!passed)
            {
                _failures.Add(
                    $"Temporal residual {pair.FromRouteFrameIndex}->" +
                    $"{pair.ToRouteFrameIndex} {residual:R} exceeds " +
                    $"{maximum!.Value:R}.");
            }
            results.Add(new SampleBenchmarkQualityTemporalResult(
                pair.FromRouteFrameIndex,
                pair.ToRouteFrameIndex,
                residual,
                maximum,
                passed));
        }
        return results;
    }

    private string ResolveRouteHash(
        IReadOnlyList<SampleBenchmarkQualityCheckpointEvidence> evidence)
    {
        if (_options.Trajectory != SampleBenchmarkTrajectoryKind.Stationary)
        {
            return SampleBenchmarkTrajectory.CreateRouteHash(
                _options.Trajectory,
                _options.TrajectoryBistroVariant);
        }
        if (evidence.Count == 0)
            return "unavailable";
        return SampleBenchmarkTrajectory.CreateRouteHash(
            _options.Trajectory,
            _options.TrajectoryBistroVariant,
            evidence[0].Camera);
    }

    private static void RequireCameraEqual(
        PerformanceCaptureCameraMetadata actual,
        PerformanceCaptureCameraMetadata expected)
    {
        if (actual.PositionX != expected.PositionX ||
            actual.PositionY != expected.PositionY ||
            actual.PositionZ != expected.PositionZ ||
            actual.YawRadians != expected.YawRadians ||
            actual.PitchRadians != expected.PitchRadians ||
            actual.FieldOfViewRadians != expected.FieldOfViewRadians ||
            actual.NearPlane != expected.NearPlane ||
            actual.FarPlane != expected.FarPlane ||
            !string.Equals(actual.ViewHash, expected.ViewHash, StringComparison.Ordinal) ||
            !string.Equals(actual.ProjectionHash, expected.ProjectionHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Checkpoint camera identity differs from reference.");
        }
    }

    private static void RequirePoseEqual(
        SampleBenchmarkCameraPose actual,
        SampleBenchmarkCameraPose expected,
        string role)
    {
        if (!Near(actual.Position.X, expected.Position.X) ||
            !Near(actual.Position.Y, expected.Position.Y) ||
            !Near(actual.Position.Z, expected.Position.Z) ||
            !Near(actual.Yaw, expected.Yaw) ||
            !Near(actual.Pitch, expected.Pitch) ||
            !Near(actual.FieldOfView, expected.FieldOfView) ||
            !Near(actual.NearPlane, expected.NearPlane) ||
            !Near(actual.FarPlane, expected.FarPlane))
        {
            throw new InvalidDataException($"{role} differs from the expected pose.");
        }
    }

    private static void RequirePoseEqual(
        SampleBenchmarkCameraPose actual,
        PerformanceCaptureCameraMetadata expected,
        string role)
    {
        if (!Near(actual.Position.X, expected.PositionX) ||
            !Near(actual.Position.Y, expected.PositionY) ||
            !Near(actual.Position.Z, expected.PositionZ) ||
            !Near(actual.Yaw, expected.YawRadians) ||
            !Near(actual.Pitch, expected.PitchRadians) ||
            !Near(actual.FieldOfView, expected.FieldOfViewRadians) ||
            !Near(actual.NearPlane, expected.NearPlane) ||
            !Near(actual.FarPlane, expected.FarPlane))
        {
            throw new InvalidDataException($"{role} differs from the expected pose.");
        }
    }

    private static void RequireProducerEqual(
        MaterialGiProducerIdentity actual,
        MaterialGiProducerIdentity expected,
        string role)
    {
        if (!string.Equals(actual.Schema, expected.Schema, StringComparison.Ordinal) ||
            !string.Equals(actual.BuildCommit, expected.BuildCommit, StringComparison.Ordinal) ||
            !string.Equals(actual.ShaderFingerprint, expected.ShaderFingerprint, StringComparison.Ordinal) ||
            !string.Equals(actual.SettingsFingerprint, expected.SettingsFingerprint, StringComparison.Ordinal) ||
            !string.Equals(actual.GpuName, expected.GpuName, StringComparison.Ordinal) ||
            !string.Equals(actual.DriverVersion, expected.DriverVersion, StringComparison.Ordinal) ||
            !string.Equals(actual.QualityTier, expected.QualityTier, StringComparison.Ordinal) ||
            !actual.SourceSettingsFingerprints.SequenceEqual(
                expected.SourceSettingsFingerprints,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{role} changed.");
        }
    }

    private static bool Near(float actual, float expected) =>
        float.IsFinite(actual) &&
        float.IsFinite(expected) &&
        MathF.Abs(actual - expected) <= 1.0e-4f;

    private static void RequireExact(string actual, string expected, string role)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Checkpoint {role} differs from reference.");
    }

    private static string CreateCaptureToken(
        SampleBenchmarkQualitySequenceOptions options,
        int ordinal,
        int routeFrame)
    {
        string token = string.Create(
            CultureInfo.InvariantCulture,
            $"{options.SequenceId}:{SampleBenchmarkTrajectory.GetName(options.Trajectory)}:" +
            $"{ordinal:D2}:{routeFrame:D4}");
        SampleBenchmarkQualitySequenceReferenceLoader.RequireCanonicalToken(
            token,
            "quality checkpoint capture token");
        return token;
    }

    private static void ValidateOptions(
        SampleBenchmarkQualitySequenceOptions options)
    {
        if (!options.Enabled)
            throw new ArgumentException("Quality-sequence runner requires enabled options.");
        _ = SampleBenchmarkCaptureVariant.Normalize(options.CaptureVariant);
        SampleBenchmarkQualitySequenceReferenceLoader.RequireCanonicalToken(
            options.SequenceId,
            "quality sequence id");
        if (string.IsNullOrWhiteSpace(options.ReportPath) ||
            string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new ArgumentException(
                "Quality sequence requires explicit report and output paths.");
        }
        if (options.WarmupFrameCount < 0 ||
            options.MaximumAdditionalSettlingFrameCount <
                SampleBenchmarkOptions.ProductionMinimumAdditionalSettlingFrameCount ||
            options.MaximumReadbackDrainFrameCount < 3)
        {
            throw new ArgumentException(
                "Quality sequence warmup, settling, or readback-drain bounds are invalid.");
        }
        string expectedFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
            options.Trajectory,
            options.TrajectoryBistroVariant);
        if (!string.Equals(
                options.TrajectoryFingerprint,
                expectedFingerprint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Quality-sequence trajectory fingerprint is not authored.");
        }
        if (options.Role == SampleBenchmarkQualitySequenceRole.Canonical)
        {
            if (!string.IsNullOrWhiteSpace(options.ReferenceContractPath))
            {
                throw new ArgumentException(
                    "Canonical quality sequence cannot consume a reference contract.");
            }
        }
        else if (string.IsNullOrWhiteSpace(options.ReferenceContractPath) ||
                 string.IsNullOrWhiteSpace(options.HdrQualityContractPath))
        {
            throw new ArgumentException(
                "Repeat and candidate quality sequences require reference and ROI contracts.");
        }
        if (!double.IsFinite(options.HdrMaximumRelativeRmse) ||
            options.HdrMaximumRelativeRmse < 0.0 ||
            !double.IsFinite(options.HdrMaximumFlipP95) ||
            options.HdrMaximumFlipP95 < 0.0)
        {
            throw new ArgumentException("Quality-sequence image thresholds are invalid.");
        }
        if (SampleBenchmarkTrajectory.RequiresBistro(options.Trajectory) &&
            options.TrajectoryBistroVariant ==
                SampleBistroQualityCaptureVariant.ReflectionSourceAb)
        {
            throw new ArgumentException(
                "Quality sequence does not admit the ReflectionSourceAb Bistro " +
                "variant because it intentionally changes settings inside the route.");
        }
    }

    private static string ResolveQualityTier(
        RenderBudgetProfileKind profile) => profile switch
        {
            RenderBudgetProfileKind.LowSpec1080p30 => "Low",
            RenderBudgetProfileKind.MidSpec1080p60 => "Medium",
            RenderBudgetProfileKind.HighSpec1440p60 => "High",
            RenderBudgetProfileKind.Ultra4k60 => "Ultra",
            _ => profile.ToString()
        };
}
