using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Njulf.Core.Scene;

namespace NjulfHelloGame;

public sealed class SampleBenchmarkRunner
{
    // A production tail run may consume the complete source interval, one
    // solve epoch, every audit chunk, and an equal scheduling/readback margin
    // after scene/resource startup. Keep the harness fail-closed, but do not
    // terminate at the exact frame that source repair hands off to solving.
    private const int RequiredConsecutiveReadyFrameCount = 30;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly SampleBenchmarkOptions _options;
    private readonly SamplePerformanceScenario _scenario;
    private readonly Action _exit;
    private readonly Func<string> _getSettingsFingerprint;
    private readonly Func<string>?
        _getControlledIsolationSettingsFingerprint;
    private readonly Func<string, bool>? _requestLinearHdrCapture;
    private readonly Func<string, LinearHdrCaptureResult>? _getLinearHdrCaptureResult;
    private readonly SampleBenchmarkAnalyzer _analyzer;
    private readonly SampleBenchmarkActivationObserver _activationObserver;
    private readonly SampleBenchmarkSponzaSceneAnimationObserver?
        _sponzaSceneAnimationObserver;
    private readonly SampleTailDdgiRunObserver _tailDdgiObserver = new();
    private int _samplesCaptured;
    private int _firstMeasurementFrame = -1;
    private int _lastMeasurementFrame = -1;
    private int _hdrCaptureWaitFrameCount;
    private string _hdrCandidatePath = string.Empty;
    private bool _waitingForHdrCapture;
    private bool _completed;
    private int _additionalSettlingFrameCount;
    private int _productionPathWaitFrameCount;
    private bool _settlingWaitTimedOut;
    private RendererDiagnostics? _lastPreMeasurementDiagnostics;
    private int _consecutiveReadyFrameCount;
    private string? _measurementSettingsFingerprint;
    private string _measurementControlledIsolationSettingsFingerprint =
        "unavailable";
    private bool _movingTrajectoryMeasurementStarted;
    private bool _measurementActivationArmed;

    public SampleBenchmarkRunner(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        Action exit,
        Func<string> getSettingsFingerprint,
        Func<string, bool>? requestLinearHdrCapture = null,
        Func<string, LinearHdrCaptureResult>? getLinearHdrCaptureResult = null,
        Func<string>? getControlledIsolationSettingsFingerprint = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _analyzer = new SampleBenchmarkAnalyzer(
            _options.MeasureFrameCount,
            SampleBenchmarkCaptureVariant.IsTailVariant(
                _options.CaptureVariant));
        _activationObserver = new SampleBenchmarkActivationObserver(
            _options.Activation,
            scenario,
            _options.Trajectory,
            _options.CaptureVariant,
            _options.MeasureFrameCount);
        if (SampleBenchmarkTrajectory.RequiresSponza(_options.Trajectory) &&
            _options.SponzaFixtureMode ==
                SampleSponzaFixtureMode.AnimationDemo)
        {
            _sponzaSceneAnimationObserver =
                new SampleBenchmarkSponzaSceneAnimationObserver(
                    _options.MeasureFrameCount,
                    _options.Activation,
                    _options.Trajectory);
        }
        _scenario = scenario;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _getSettingsFingerprint = getSettingsFingerprint ??
            throw new ArgumentNullException(nameof(getSettingsFingerprint));
        _getControlledIsolationSettingsFingerprint =
            getControlledIsolationSettingsFingerprint;
        _requestLinearHdrCapture = requestLinearHdrCapture;
        _getLinearHdrCaptureResult = getLinearHdrCaptureResult;
    }

    public SampleBenchmarkReport? Report { get; private set; }
    public string? ReportPath { get; private set; }
    /// <summary>
    /// The timed interval has ended and the renderer must retain the last
    /// deterministic trajectory pose while the out-of-band HDR readback runs.
    /// </summary>
    public bool HoldTrajectoryForPostMeasurementEvidence =>
        _waitingForHdrCapture;

    public bool MovingTrajectoryMeasurementStarted =>
        _movingTrajectoryMeasurementStarted;

    public bool TryGetActivationFrameIndexForNextRender(
        out int measurementFrameIndex)
    {
        if (!SampleBenchmarkActivation.RequiresPreDrawMeasurementArm(
                _options.Activation))
        {
            measurementFrameIndex = -1;
            return false;
        }
        return TryGetMeasurementFrameIndexForNextRender(
            out measurementFrameIndex);
    }

    public bool TryGetMeasurementFrameIndexForNextRender(
        out int measurementFrameIndex)
    {
        measurementFrameIndex = -1;
        if (!_measurementActivationArmed || _completed ||
            _waitingForHdrCapture ||
            _samplesCaptured >= _options.MeasureFrameCount)
        {
            return false;
        }
        measurementFrameIndex = _samplesCaptured;
        return true;
    }

    public void RecordReflectionActivationRequest(
        int measurementFrameIndex,
        in ReflectionProbeRecaptureRequestSummary admission) =>
        _activationObserver.RecordReflectionRequest(
            measurementFrameIndex,
            admission);

    public void RecordPreDrawActivationFrame(
        int measurementFrameIndex,
        SampleBenchmarkActivationFrameState state) =>
        _activationObserver.RecordPreDrawFrame(
            measurementFrameIndex,
            state);

    public void RecordPreDrawActivationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _activationObserver.RecordFailure(
            "Benchmark activation pre-Draw control failed: " +
            $"{exception.GetType().Name}: {exception.Message}");
    }

    public void PrepareActivationAnimationFrame(
        Scene scene,
        int routeFrameIndex,
        int? measurementFrameIndex) =>
        _activationObserver.PrepareTimingAnimationFrame(
            scene,
            routeFrameIndex,
            measurementFrameIndex);

    public void PrepareSponzaSceneAnimationFrame(
        Scene scene,
        int authoredRouteFrameIndex,
        bool measurementFrame,
        bool hold)
    {
        if (_sponzaSceneAnimationObserver == null)
            return;
        try
        {
            _sponzaSceneAnimationObserver.PrepareTimingFrame(
                scene,
                authoredRouteFrameIndex,
                measurementFrame,
                hold);
        }
        catch (Exception exception)
        {
            _sponzaSceneAnimationObserver.RecordFailure(
                "Sponza animation pre-Draw attestation failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public int ResolveTrajectoryFrameIndexForNextRender(int absoluteFrameIndex)
    {
        if (!SampleBenchmarkTrajectory.IsMoving(_options.Trajectory))
            return 0;
        if (!_movingTrajectoryMeasurementStarted)
        {
            return SampleBenchmarkTrajectory.GetWarmupFrameIndex(
                _options.Trajectory,
                absoluteFrameIndex);
        }
        return Math.Min(
            _samplesCaptured,
            SampleBenchmarkTrajectory.GetFrameCount(_options.Trajectory) - 1);
    }

    public int ResolveBistroControllerFrameIndexForNextRender(
        int absoluteFrameIndex)
    {
        int routeFrame = ResolveTrajectoryFrameIndexForNextRender(
            absoluteFrameIndex);
        return _movingTrajectoryMeasurementStarted
            ? checked(
                SampleBistroQualityCaptureContract.FirstMeasuredFrame +
                routeFrame)
            : routeFrame;
    }

    public void OnFrameRendered(int frameIndex, RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        if (!_options.Enabled || _completed)
            return;
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (budget == null)
            throw new ArgumentNullException(nameof(budget));

        if (_waitingForHdrCapture)
        {
            PollHdrCapture();
            return;
        }

        // Progressive startup can present inexpensive bootstrap frames before
        // production resources exist. Those frames have no authenticated
        // build/shader identity and must not contaminate benchmark observers.
        if (_samplesCaptured == 0 &&
            !HasInitializedProductionIdentity(diagnostics))
        {
            _lastPreMeasurementDiagnostics = diagnostics;
            return;
        }

        // A requested production path may remain on its exact fallback while
        // a deferred pipeline bank is prepared. Keep that wait independent of
        // the convergence window so a successful late publication still owns
        // the full settling allowance, but fail closed after the same explicit
        // bound. Proceeding then emits a rejected report instead of leaving the
        // external campaign watchdog to terminate an otherwise healthy app.
        if (_samplesCaptured == 0 &&
            _options.RequireProductionTiming &&
            !HasEffectiveRequestedProductionPath(diagnostics))
        {
            if (_productionPathWaitFrameCount <
                _options.MaximumAdditionalSettlingFrameCount)
            {
                _productionPathWaitFrameCount++;
                _lastPreMeasurementDiagnostics = diagnostics;
                return;
            }

            _settlingWaitTimedOut = true;
        }

        _tailDdgiObserver.Observe(diagnostics);

        if (_samplesCaptured == 0)
        {
            _consecutiveReadyFrameCount = IsReadyForMeasurement(
                    diagnostics,
                    _options.Trajectory)
                ? Math.Min(
                    RequiredConsecutiveReadyFrameCount,
                    _consecutiveReadyFrameCount + 1)
                : 0;

            if (frameIndex < _options.WarmupFrameCount)
            {
                if (SampleBenchmarkTrajectory.IsMoving(_options.Trajectory) &&
                    frameIndex == _options.WarmupFrameCount - 1 &&
                    _consecutiveReadyFrameCount >=
                        RequiredConsecutiveReadyFrameCount &&
                    SampleBenchmarkTrajectory.CanStartMeasurementAfterFrame(
                        _options.Trajectory,
                        frameIndex))
                {
                    _movingTrajectoryMeasurementStarted = true;
                    _measurementActivationArmed = true;
                }
                else if (!SampleBenchmarkTrajectory.IsMoving(
                             _options.Trajectory) &&
                         frameIndex == _options.WarmupFrameCount - 1 &&
                         _consecutiveReadyFrameCount >=
                             RequiredConsecutiveReadyFrameCount &&
                         RequiresPreDrawMeasurementBoundary())
                {
                    _measurementActivationArmed = true;
                }
                _lastPreMeasurementDiagnostics = diagnostics;
                return;
            }

            if (_consecutiveReadyFrameCount < RequiredConsecutiveReadyFrameCount)
            {
                if (_additionalSettlingFrameCount <
                    _options.MaximumAdditionalSettlingFrameCount)
                {
                    _additionalSettlingFrameCount++;
                    _lastPreMeasurementDiagnostics = diagnostics;
                    return;
                }

                _settlingWaitTimedOut = true;
            }

            if (SampleBenchmarkTrajectory.IsMoving(_options.Trajectory) &&
                !_movingTrajectoryMeasurementStarted)
            {
                if (!SampleBenchmarkTrajectory.CanStartMeasurementAfterFrame(
                        _options.Trajectory,
                        frameIndex))
                {
                    if (_additionalSettlingFrameCount <
                        _options.MaximumAdditionalSettlingFrameCount)
                    {
                        _additionalSettlingFrameCount++;
                    }
                    else
                    {
                        _settlingWaitTimedOut = true;
                    }
                    _lastPreMeasurementDiagnostics = diagnostics;
                    return;
                }

                _movingTrajectoryMeasurementStarted = true;
                _measurementActivationArmed = true;
                _lastPreMeasurementDiagnostics = diagnostics;
                return;
            }

            if (RequiresPreDrawMeasurementBoundary() &&
                !_measurementActivationArmed)
            {
                _measurementActivationArmed = true;
                _lastPreMeasurementDiagnostics = diagnostics;
                return;
            }
        }

        if (_samplesCaptured == 0)
        {
            _firstMeasurementFrame = frameIndex;
            RendererDiagnostics baseline =
                _lastPreMeasurementDiagnostics ?? diagnostics;
            _analyzer.SetMeasurementBaseline(baseline);
            _activationObserver.BeginMeasurement(baseline);
        }
        _lastMeasurementFrame = frameIndex;
        if (_sponzaSceneAnimationObserver != null)
        {
            try
            {
                _sponzaSceneAnimationObserver.RecordTimingFrame(
                    _samplesCaptured,
                    _samplesCaptured);
            }
            catch (Exception exception)
            {
                _sponzaSceneAnimationObserver.RecordFailure(
                    "Sponza animation measured-frame attestation failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }
        _activationObserver.Observe(_samplesCaptured, diagnostics);
        _analyzer.AddSample(diagnostics, budget);
        _samplesCaptured++;

        if (_samplesCaptured < _options.MeasureFrameCount)
            return;

        // Freeze producer identity at the exact end of the measurement window.
        // Post-measurement HDR capture intentionally enables debug/screenshot
        // permissions and must not make an otherwise identical timing run look
        // like it used different render settings.
        _measurementSettingsFingerprint = _getSettingsFingerprint();
        if (SampleBenchmarkActivation.RequiresDeterministicAnimation(
                _options.Activation))
        {
            _measurementControlledIsolationSettingsFingerprint =
                _getControlledIsolationSettingsFingerprint?.Invoke() ??
                    "unavailable";
        }
        BeginPostMeasurementEvidence();
    }

    private bool RequiresPreDrawMeasurementBoundary() =>
        SampleBenchmarkActivation.RequiresPreDrawMeasurementArm(
            _options.Activation) ||
        SampleBenchmarkTrajectory.RequiresSponza(_options.Trajectory);

    private void BeginPostMeasurementEvidence()
    {
        if (string.IsNullOrWhiteSpace(_options.HdrReferencePath) &&
            string.IsNullOrWhiteSpace(_options.HdrCandidatePath))
        {
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                "No benchmark HDR reference path was supplied."));
            return;
        }

        if (_requestLinearHdrCapture == null || _getLinearHdrCaptureResult == null)
        {
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                "The benchmark host does not expose linear HDR capture callbacks."));
            return;
        }

        _hdrCandidatePath = ResolveHdrCandidatePath(_options);
        try
        {
            if (!_requestLinearHdrCapture(_hdrCandidatePath))
            {
                Complete(SampleBenchmarkHdrDifference.Unavailable(
                    "The renderer rejected the post-measurement linear HDR capture request."));
                return;
            }
        }
        catch (Exception exception)
        {
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                $"HDR capture request failed: {exception.GetType().Name}: {exception.Message}"));
            return;
        }

        _waitingForHdrCapture = true;
        _hdrCaptureWaitFrameCount = 0;
    }

    private void PollHdrCapture()
    {
        const int maximumWaitFrames = 120;
        _hdrCaptureWaitFrameCount++;
        LinearHdrCaptureResult result;
        try
        {
            result = _getLinearHdrCaptureResult!(_hdrCandidatePath);
        }
        catch (Exception exception)
        {
            _waitingForHdrCapture = false;
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                $"HDR capture status failed: {exception.GetType().Name}: {exception.Message}"));
            return;
        }

        if (result.State == LinearHdrCaptureState.Completed)
        {
            _waitingForHdrCapture = false;
            if (string.IsNullOrWhiteSpace(_options.HdrReferencePath))
            {
                Complete(SampleBenchmarkHdrDifference.Unavailable(
                    $"HDR candidate captured at '{_hdrCandidatePath}'; no reference was supplied."));
                return;
            }

            try
            {
                Complete(SampleBenchmarkHdrComparer.Compare(
                    _options.HdrReferencePath,
                    _hdrCandidatePath,
                    _options.HdrMaximumRelativeRmse,
                    _options.HdrMaximumFlipP95,
                    _options.HdrQualityContractPath));
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    IOException or
                    InvalidDataException or
                    InvalidOperationException or
                    JsonException or
                    NotSupportedException or
                    UnauthorizedAccessException)
            {
                Complete(SampleBenchmarkHdrDifference.Unavailable(
                    $"HDR comparison failed: {exception.GetType().Name}: {exception.Message}"));
            }
            return;
        }

        if (result.State == LinearHdrCaptureState.Failed)
        {
            _waitingForHdrCapture = false;
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                string.IsNullOrWhiteSpace(result.Error)
                    ? "The renderer failed the linear HDR capture."
                    : result.Error));
            return;
        }

        if (_hdrCaptureWaitFrameCount >= maximumWaitFrames)
        {
            _waitingForHdrCapture = false;
            Complete(SampleBenchmarkHdrDifference.Unavailable(
                $"Linear HDR capture did not complete within {maximumWaitFrames} frames."));
        }
    }

    private void Complete(SampleBenchmarkHdrDifference hdrDifference)
    {
        _completed = true;
        string reportTargetPath = ResolveReportPath(_options.ReportPath);
        SampleBenchmarkSponzaSceneAnimationBuild? sceneAnimationBuild = null;
        SampleBenchmarkSponzaSceneAnimationEvidence sceneAnimationEvidence;
        try
        {
            sceneAnimationBuild = _sponzaSceneAnimationObserver?.BuildTiming(
                reportTargetPath + ".sponza-animation.bin");
            sceneAnimationEvidence = sceneAnimationBuild?.Evidence ??
                SampleBenchmarkSponzaSceneAnimationEvidence.Unavailable;
        }
        catch (Exception exception)
        {
            sceneAnimationEvidence =
                SampleBenchmarkSponzaSceneAnimationEvidence.Failed(
                    SampleBenchmarkSponzaSceneAnimationContract.ResolveMode(
                        _options.Activation),
                    _samplesCaptured,
                    "Sponza animation evidence assembly failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
        }
        IReadOnlyList<SampleBenchmarkActivationFrameState>?
            activationAnimationFrames =
                SampleBenchmarkActivation.RequiresDeterministicAnimation(
                    _options.Activation)
                    ? sceneAnimationBuild?.Frames
                    : null;
        SampleBenchmarkActivationEvidence activationEvidence =
            _activationObserver.Build(activationAnimationFrames);
        Report = _analyzer.CreateReport(
            _options,
            _scenario,
            _options.WarmupFrameCount,
            _samplesCaptured,
            _firstMeasurementFrame,
            _lastMeasurementFrame,
            _tailDdgiObserver.Snapshot(),
            _measurementControlledIsolationSettingsFingerprint);
        Report = Report with
        {
            HdrDifference = hdrDifference,
            ActivationEvidence = activationEvidence,
            SponzaSceneAnimationEvidence = sceneAnimationEvidence,
            AdditionalSettlingFrameCount = _additionalSettlingFrameCount,
            SettlingWaitTimedOut = _settlingWaitTimedOut,
            ShaderProfile = SampleShaderProfileEvidenceLoader.Load(
                _options.ShaderProfileArtifactPath,
                Report.LastDiagnostics),
            ProducerIdentity =
                SampleMaterialGiProducerIdentityFactory.Create(
                    Report.LastDiagnostics,
                    _measurementSettingsFingerprint ??
                        throw new InvalidOperationException(
                            "Benchmark completion requires the measurement-window settings fingerprint."),
                    ResolveQualityTier(
                        Report.LastDiagnostics.ActiveBudgetProfile))
        };
        Report = Report with
        {
            CaptureContract = ApplyEvidenceContract(
                ApplySettlingWaitContract(
                    Report.CaptureContract,
                    _settlingWaitTimedOut,
                    _options.MaximumAdditionalSettlingFrameCount),
                _options,
                Report.HdrDifference,
                Report.ShaderProfile,
                Report.ActivationEvidence,
                Report.SponzaSceneAnimationEvidence,
                activationAnimationFrames)
        };
        Report = Report with
        {
            RealtimePerformanceTarget =
                SampleRealtimePerformanceTarget.Evaluate(Report)
        };
        if (SampleDdgiBenchmarkSuite.RequiredProductionGateScenes.Any(scene => scene.Scenario == _scenario))
        {
            SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(Report);
            Report = Report with { DdgiProductionGate = gate };
        }
        ReportPath = WriteReport(Report, reportTargetPath);
        Console.WriteLine(
            $"Benchmark report exported: {ReportPath} " +
            $"cpuP95={Report.CpuFrameMilliseconds.P95Milliseconds:F3}ms " +
            $"gpuP95={Report.GpuFrameMilliseconds.P95Milliseconds:F3}ms " +
            $"top='{Report.Findings.FirstOrDefault()?.Subject ?? "none"}'");
        if (Report.DdgiProductionGate != null)
        {
            Console.WriteLine(
                $"DDGI production gate: {(Report.DdgiProductionGate.Passed ? "passed" : "failed")} " +
                $"failures={Report.DdgiProductionGate.Failures.Count}");
        }
        _exit();
    }

    internal static bool IsReadyForMeasurement(RendererDiagnostics diagnostics) =>
        IsReadyForMeasurement(
            diagnostics,
            SampleBenchmarkTrajectoryKind.Stationary);

    internal static bool HasInitializedProductionIdentity(
        RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        string commit = diagnostics.CaptureRun.Commit;
        string shaderBundle = diagnostics.CaptureRun.ShaderBundleHash;
        return commit.Length == 40 &&
            commit.All(static character => char.IsAsciiHexDigit(character)) &&
            shaderBundle.Length == 71 &&
            shaderBundle.StartsWith("sha256:", StringComparison.Ordinal) &&
            shaderBundle.AsSpan("sha256:".Length).IndexOfAnyExcept(
                "0123456789abcdef") < 0;
    }

    internal static bool HasEffectiveRequestedProductionPath(
        RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        SimpleDdgiReceiverCacheDiagnostics cache =
            diagnostics.SimpleDdgiReceiverCache;
        if (!cache.RequestedMode.UsesCache())
            return true;

        return cache.EffectiveMode == cache.RequestedMode &&
            cache.FallbackReason == SimpleDdgiReceiverCacheFallbackReason.None &&
            diagnostics.ForwardGiReceiverCacheGenerated != 0 &&
            diagnostics.ForwardGiReceiverCacheConsumed != 0 &&
            diagnostics.ForwardGiExactGatherUsed == 0;
    }

    internal static bool IsReadyForMeasurement(
        RendererDiagnostics diagnostics,
        SampleBenchmarkTrajectoryKind trajectory)
    {
        bool movingTrajectory = SampleBenchmarkTrajectory.IsMoving(trajectory);
        bool acceptedTailCertificate =
            HasAcceptedCurrentSimpleDdgiTailCertificate(diagnostics);
        if (diagnostics.GpuTimingValid == 0 ||
            (!movingTrajectory &&
             diagnostics.CaptureFrame.WarmupState !=
                 DdgiRuntimeWarmupState.SteadyState) ||
            (!movingTrajectory &&
             diagnostics.CaptureFrame.TransportConvergencePending &&
                !acceptedTailCertificate))
        {
            return false;
        }

        if (diagnostics.SimpleDdgiActive == 0)
            return true;
        if (!diagnostics.SimpleDdgiUploadTiming.CapacityDetails.StableKeyHit)
            return false;

        // A closed moving route deliberately advances camera-relative DDGI
        // ownership. Its transport certificate cannot remain current for
        // thirty consecutive frames, but the allocation key must already be
        // stable. The complete route-state sequence is authenticated in the
        // capture contract and compared exactly for same-role runs.
        if (movingTrajectory)
            return true;

        if (diagnostics.SimpleDdgiTransportV2Active == 0)
            return true;

        return diagnostics.SimpleDdgiTransportTailCertificationEnabled
            ? acceptedTailCertificate
            : HasSourceReadySimpleDdgiTransportPopulation(diagnostics);
    }

    internal static bool HasAcceptedCurrentSimpleDdgiTailCertificate(
        RendererDiagnostics diagnostics)
    {
        if (diagnostics.SimpleDdgiActive == 0 ||
            diagnostics.SimpleDdgiTransportV2Active == 0 ||
            !diagnostics.SimpleDdgiTransportTailCertificationEnabled)
        {
            return false;
        }

        SimpleDdgiTransportConvergenceTelemetry tail =
            diagnostics.SimpleDdgiTransportConvergence;
        return tail.TailCertificateCurrent &&
            tail.TailAuditComplete &&
            tail.TailExpectedParticipantCount > 0u &&
            tail.TailAuditedParticipantCount ==
                tail.TailExpectedParticipantCount &&
            tail.TailExpectedTexelCount > 0u &&
            tail.TailAuditedTexelCount == tail.TailExpectedTexelCount &&
            tail.TailExcludedStaleSourceCount == 0u &&
            tail.TailExcludedInvalidCacheCount == 0u &&
            tail.TailCacheIdentityFailureCount == 0u &&
            tail.TailCacheCardinalityFailureCount == 0u &&
            tail.TailCacheSourceGenerationFailureCount == 0u &&
            tail.TailCacheSourceEpochFailureCount == 0u &&
            tail.TailCachePhysicalGenerationFailureCount == 0u &&
            tail.TailNonFiniteCount == 0u &&
            tail.TailCounterOverflowCount == 0u;
    }

    internal static bool HasSourceReadySimpleDdgiTransportPopulation(
        RendererDiagnostics diagnostics)
    {
        SimpleDdgiTransportConvergenceTelemetry convergence =
            diagnostics.SimpleDdgiTransportConvergence;
        int participants = Math.Max(0, convergence.ParticipatingProbeCount);
        int sourceRepair = Math.Clamp(
            convergence.SourceRepairProbeCount,
            0,
            participants);
        int routineSourceRepair = Math.Clamp(
            convergence.RoutineSourceRepairProbeCount,
            0,
            sourceRepair);
        int converged = Math.Clamp(
            convergence.ConvergedProbeCount,
            0,
            participants - sourceRepair);
        int routineMaintenance = Math.Clamp(
            convergence.RoutineMaintenancePendingProbeCount,
            0,
            Math.Max(0, participants - sourceRepair - converged));
        int qualified = Math.Min(
            participants,
            converged + routineSourceRepair + routineMaintenance);
        return convergence.ReadbackValid != 0 &&
            participants > 0 &&
            (long)qualified * 100L >=
                (long)participants * 95L;
    }

    private static SampleBenchmarkCaptureContract ApplySettlingWaitContract(
        SampleBenchmarkCaptureContract contract,
        bool timedOut,
        int maximumAdditionalSettlingFrameCount)
    {
        if (!timedOut)
            return contract;

        string[] mismatches = contract.Mismatches
            .Append(
                $"The benchmark did not settle within " +
                $"{maximumAdditionalSettlingFrameCount} additional frames.")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return contract with
        {
            Comparable = false,
            Mismatches = Array.AsReadOnly(mismatches)
        };
    }

    private static SampleBenchmarkCaptureContract ApplyEvidenceContract(
        SampleBenchmarkCaptureContract contract,
        SampleBenchmarkOptions options,
        SampleBenchmarkHdrDifference hdrDifference,
        SampleShaderProfileEvidence shaderProfile,
        SampleBenchmarkActivationEvidence activationEvidence,
        SampleBenchmarkSponzaSceneAnimationEvidence sceneAnimationEvidence,
        IReadOnlyList<SampleBenchmarkActivationFrameState>?
            activationAnimationFrames)
    {
        var mismatches = new List<string>(contract.Mismatches);
        bool hdrRequested = !string.IsNullOrWhiteSpace(options.HdrReferencePath);
        if ((options.RequireProductionTiming || hdrRequested) && !hdrDifference.Available)
        {
            mismatches.Add("HDR evidence is unavailable: " + hdrDifference.FailureReason);
        }
        else if (hdrRequested && !hdrDifference.Passed)
        {
            mismatches.Add("HDR image comparison failed: " + hdrDifference.FailureReason);
        }

        bool shaderProfileRequested =
            !string.IsNullOrWhiteSpace(options.ShaderProfileArtifactPath);
        if ((options.RequireShaderProfileEvidence || shaderProfileRequested) &&
            !shaderProfile.Available)
        {
            mismatches.Add(
                "Nsight shader-profile evidence is unavailable: " +
                shaderProfile.UnavailableReason);
        }

        string activation = SampleBenchmarkActivation.Normalize(
            options.Activation);
        string activationFingerprint =
            SampleBenchmarkActivation.CreateFingerprint(activation);
        if (!string.Equals(
                activationEvidence.Schema,
                SampleBenchmarkActivationEvidence.CurrentSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                activationEvidence.Activation,
                activation,
                StringComparison.Ordinal) ||
            !string.Equals(
                activationEvidence.Fingerprint,
                activationFingerprint,
                StringComparison.Ordinal) ||
            activationEvidence.MeasuredSampleCount !=
                options.MeasureFrameCount ||
            !activationEvidence.Passed ||
            activationEvidence.Failures.Count != 0)
        {
            mismatches.Add(
                "Authored benchmark activation evidence is missing, failed, " +
                "or does not match the capture contract.");
        }
        foreach (string failure in
                 SampleBenchmarkActivationEvidenceValidator.Validate(
                     activationEvidence,
                     activation,
                     options.CaptureVariant,
                     options.MeasureFrameCount,
                     qualitySequence: false,
                     trajectory: options.Trajectory,
                     authoredAnimationFrames: activationAnimationFrames))
        {
            mismatches.Add("Activation evidence: " + failure);
        }
        bool sponza = SampleBenchmarkTrajectory.RequiresSponza(
            options.Trajectory);
        bool sponzaAnimation = sponza &&
            options.SponzaFixtureMode == SampleSponzaFixtureMode.AnimationDemo;
        if (sponzaAnimation)
        {
            SampleBenchmarkSponzaSceneAnimationMode expectedMode =
                SampleBenchmarkSponzaSceneAnimationContract.ResolveMode(
                    options.Activation);
            if (sceneAnimationEvidence.Schema !=
                    SampleBenchmarkSponzaSceneAnimationEvidence.CurrentSchema ||
                sceneAnimationEvidence.Fingerprint !=
                    SampleBenchmarkSponzaSceneAnimationContract.Fingerprint ||
                sceneAnimationEvidence.Mode != expectedMode ||
                !sceneAnimationEvidence.Passed ||
                sceneAnimationEvidence.SampleCount !=
                    options.MeasureFrameCount ||
                sceneAnimationEvidence.Failures.Count != 0)
            {
                mismatches.Add(
                    "Sponza scene-animation evidence is missing, failed, or " +
                    "does not match the authored phase contract.");
            }
        }
        else if (!SampleBenchmarkSponzaSceneAnimationEvidence
                     .IsCanonicalUnavailable(sceneAnimationEvidence))
        {
            mismatches.Add(
                "A workload without the explicit Sponza animation fixture " +
                "does not contain the exact canonical " +
                "unavailable Sponza scene-animation evidence shape.");
        }

        string[] distinct = mismatches.Distinct(StringComparer.Ordinal).ToArray();
        return contract with
        {
            Comparable = contract.Comparable && distinct.Length == 0,
            Mismatches = Array.AsReadOnly(distinct),
            SponzaFixtureMode = options.SponzaFixtureMode,
            SponzaSceneAnimationFingerprint = sponzaAnimation
                ? sceneAnimationEvidence.Fingerprint
                : "unavailable",
            SponzaSceneAnimationMode = sponzaAnimation
                ? sceneAnimationEvidence.Mode
                : SampleBenchmarkSponzaSceneAnimationMode.Unavailable,
            SponzaSceneAnimationConfigurationFingerprint = sponzaAnimation
                ? sceneAnimationEvidence.ConfigurationFingerprint
                : "unavailable",
            SponzaSceneAnimationSequenceHash = sponzaAnimation
                ? sceneAnimationEvidence.SequenceHash
                : "unavailable",
            SponzaSceneAnimationSidecarSha256 = sponzaAnimation
                ? sceneAnimationEvidence.SidecarSha256
                : "unavailable"
        };
    }

    private static string ResolveHdrCandidatePath(SampleBenchmarkOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.HdrCandidatePath))
            return Path.GetFullPath(options.HdrCandidatePath);

        if (!string.IsNullOrWhiteSpace(options.ReportPath))
        {
            string reportPath = Path.GetFullPath(options.ReportPath);
            string directory = Path.GetDirectoryName(reportPath) ?? AppContext.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(reportPath);
            return Path.Combine(directory, name + ".hdr.pfm");
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "BenchmarkReports",
            $"benchmark-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.hdr.pfm");
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

    internal static string WriteReport(SampleBenchmarkReport report, string? path)
    {
        string targetPath = ResolveReportPath(path);
        byte[] payload =
            JsonSerializer.SerializeToUtf8Bytes(report, SerializerOptions);
        return SampleEvidenceFileIo.WriteAtomic(
            targetPath,
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Benchmark report").Path;
    }

    private static string ResolveReportPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                AppContext.BaseDirectory,
                "BenchmarkReports",
                $"benchmark-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json")
            : Path.GetFullPath(path);
}

public sealed class SampleBenchmarkAnalyzer
{
    private readonly bool _tailDdgiTimingProjection;
    private static readonly IReadOnlyList<TimingSelector> GpuTimings =
    [
        new("DepthPrePass", d => d.GpuDepthPrePassMicroseconds),
        new("MotionVectorPass", d => d.GpuMotionVectorMicroseconds),
        new("DirectionalShadowPass", d => d.GpuDirectionalShadowMicroseconds),
        new("DirectionalRayShadowPass", d => d.GpuDirectionalRayShadowMicroseconds),
        new("AreaRayShadowPass", d => d.GpuAreaRayShadowMicroseconds),
        new("DirectionalShadowTemporalPass", d => d.GpuDirectionalShadowTemporalMicroseconds),
        new("DirectionalShadowSpatialPass", d => d.GpuDirectionalShadowSpatialMicroseconds),
        new("SpotShadowPass", d => d.GpuSpotShadowMicroseconds),
        new("PointShadowPass", d => d.GpuPointShadowMicroseconds),
        new("HiZBuildPass", d => d.GpuHiZBuildMicroseconds),
        new("AmbientOcclusionPass", d => d.GpuAmbientOcclusionMicroseconds),
        new("AmbientOcclusionBlurPass", d => d.GpuAmbientOcclusionBlurMicroseconds),
        new("AccelerationStructureBlasPass", d => d.GpuAccelerationStructureBlasMicroseconds),
        new("AccelerationStructureTlasPass", d => d.GpuAccelerationStructureTlasMicroseconds),
        new("SimpleDdgiPageDemandPass", d => d.GpuSimpleDdgiPageDemandMicroseconds),
        new("SimpleDdgiPageResidencyPass", d => d.GpuSimpleDdgiPageResidencyMicroseconds),
        new("SimpleDdgiPageFeedbackPass", d => d.GpuSimpleDdgiPageFeedbackMicroseconds),
        new("SimpleDdgiSchedulePass", d => d.GpuSimpleDdgiScheduleMicroseconds),
        new("SimpleDdgiSchedule.Reset", d => d.GpuSimpleDdgiScheduleResetMicroseconds),
        new("SimpleDdgiSchedule.Classify", d => d.GpuSimpleDdgiScheduleClassifyMicroseconds),
        new("SimpleDdgiSchedule.Prefix", d => d.GpuSimpleDdgiSchedulePrefixMicroseconds),
        new("SimpleDdgiSchedule.LaneBase", d => d.GpuSimpleDdgiScheduleLaneBaseMicroseconds),
        new("SimpleDdgiSchedule.Compact", d => d.GpuSimpleDdgiScheduleCompactMicroseconds),
        new("SimpleDdgiSchedule.TailAdmit", d => d.GpuSimpleDdgiScheduleTailAdmitMicroseconds),
        new("SimpleDdgiSchedule.Admit", d => d.GpuSimpleDdgiScheduleAdmitMicroseconds),
        new("SimpleDdgiSchedule.Materialize", d => d.GpuSimpleDdgiScheduleMaterializeMicroseconds),
        new("SimpleDdgiSchedule.Emit", d => d.GpuSimpleDdgiScheduleEmitMicroseconds),
        new("SimpleDdgiTracePass", d => d.GpuSimpleDdgiTraceMicroseconds),
        new("SimpleDdgiAcceleratedSolvePass", d => d.GpuSimpleDdgiAcceleratedSolveMicroseconds),
        new("SimpleDdgiTransportPass", d => d.GpuSimpleDdgiTransportMicroseconds),
        new("SimpleDdgiDirectionalRadiancePass", d => d.GpuSimpleDdgiDirectionalRadianceMicroseconds),
        new("SimpleDdgiBlendPass", d => d.GpuSimpleDdgiBlendMicroseconds),
        new("SimpleDdgiRelocateClassifyPass", d => d.GpuSimpleDdgiRelocateClassifyMicroseconds),
        new("SimpleDdgiPublishPass", d => d.GpuSimpleDdgiPublishMicroseconds),
        new("SimpleDdgiTransportAuditPass", d => d.GpuSimpleDdgiTransportAuditMicroseconds),
        new("SimpleDdgiSchedulerCommitPass", d => d.GpuSimpleDdgiCommitMicroseconds),
        new("GlobalIlluminationCompositePass", d => d.GpuGiCompositeMicroseconds),
        new("TiledLightCullingPass", d => d.GpuLightCullMicroseconds),
        new("ForwardPlusPass", d => d.GpuForwardOpaqueMicroseconds),
        new("ForwardGiGatherPass", d => d.GpuForwardGiGatherMicroseconds),
        new("SimpleDdgiReceiverCachePass", d => d.GpuSimpleDdgiReceiverCacheMicroseconds),
        new("TransparentPasses", d => d.GpuTransparentMicroseconds),
        new("ParticlePasses", d => d.GpuParticleMicroseconds),
        new("TrailBeamPass", d => d.GpuTrailBeamMicroseconds),
        new("FogPass", d => d.GpuFogMicroseconds),
        new("AutoExposurePass", d => d.GpuAutoExposureMicroseconds),
        new("AntiAliasingPass", d => d.GpuAntiAliasingMicroseconds),
        new("BloomExtractPass", d => d.GpuBloomExtractMicroseconds),
        new("BloomDownsamplePass", d => d.GpuBloomDownsampleMicroseconds),
        new("BloomUpsamplePass", d => d.GpuBloomUpsampleMicroseconds),
        new("ToneMapCompositePass", d => d.GpuCompositeMicroseconds),
        new("SkinningPass", d => d.GpuSkinningMicroseconds),
        new("ReflectionProbeCapture", d => d.GpuReflectionProbeCaptureMicroseconds),
        new("ReflectionProbePrefilter", d => d.GpuReflectionProbePrefilterMicroseconds),
        new("ReflectionProbePublish", d => d.GpuReflectionProbePublishMicroseconds),
        new("AutomaticPlanarReflectionPass", d => d.GpuAutomaticPlanarCaptureMicroseconds),
        new("HybridReflectionSsrPass", d => d.GpuHybridReflectionSsrMicroseconds),
        new("HybridReflectionRayQueryPass", d => d.GpuHybridReflectionRayQueryMicroseconds),
        new("HybridReflectionDdgiBasePass", d => d.GpuHybridReflectionDdgiBaseMicroseconds),
        new("HybridReflectionResolvePass", d => d.GpuHybridReflectionResolveMicroseconds),
        new("HybridReflectionTemporalPass", d => d.GpuHybridReflectionTemporalMicroseconds),
        new("HybridReflectionSpatialPass", d => d.GpuHybridReflectionSpatialMicroseconds),
        new("HybridReflectionCompositePass", d => d.GpuHybridReflectionCompositeMicroseconds),
        new("FoliageCullPass", d => d.GpuFoliageCullMicroseconds),
        new("FoliageDepth", d => d.GpuFoliageDepthMicroseconds),
        new("FoliageForward", d => d.GpuFoliageForwardMicroseconds),
        new("FoliageShadow", d => d.GpuFoliageShadowMicroseconds),
        new("DebugDrawPass", d => d.GpuDebugDrawMicroseconds),
        new("DebugOverlay", d => d.GpuDebugOverlayMicroseconds)
    ];

    private static readonly IReadOnlyList<TimingSelector> GpuIndependentTimings =
        GpuTimings
            // Foliage shadow telemetry aliases the directional-shadow pass.
            // Scheduler stage timestamps are nested inside SchedulePass. They
            // remain first-class attribution rows, but summing both parent and
            // children makes a valid frame appear over-accounted by exactly the
            // scheduler duration and invalidates otherwise locked captures.
            .Where(static selector =>
                selector.Name != "FoliageShadow" &&
                selector.Name != "ForwardGiGatherPass" &&
                selector.Name != "SimpleDdgiReceiverCachePass" &&
                !selector.Name.StartsWith(
                    "SimpleDdgiSchedule.",
                    StringComparison.Ordinal))
            .ToArray();

    private static readonly IReadOnlyList<TimingSelector> CpuTimings =
    [
        new("DrawSceneTotal", d => d.CpuTotalDrawSceneMicroseconds),
        new("SceneBuild", d => d.CpuSceneBuildMicroseconds),
        new("ObjectCull", d => d.CpuObjectCullMicroseconds),
        new("MeshletCull", d => d.CpuMeshletCullMicroseconds),
        new("Upload", d => d.CpuUploadMicroseconds),
        new("MaterialUpload", d => d.CpuMaterialUploadMicroseconds),
        new("SimpleDdgiUpload", d => d.SimpleDdgiUploadTiming.TotalMicroseconds),
        new("SimpleDdgiUpload.Layout", d => d.SimpleDdgiUploadTiming.LayoutMicroseconds),
        new("SimpleDdgiUpload.Readback", d => d.SimpleDdgiUploadTiming.ReadbackMicroseconds),
        new("SimpleDdgiUpload.Capacity", d => d.SimpleDdgiUploadTiming.CapacityMicroseconds),
        new("SimpleDdgiUpload.Invalidation", d => d.SimpleDdgiUploadTiming.InvalidationMicroseconds),
        new("SimpleDdgiUpload.SchedulerRefresh", d => d.SimpleDdgiUploadTiming.SchedulerRefreshMicroseconds),
        new("SimpleDdgiUpload.Importance", d => d.SimpleDdgiUploadTiming.ImportanceMicroseconds),
        new("SimpleDdgiUpload.QueueBuild", d => d.SimpleDdgiUploadTiming.QueueBuildMicroseconds),
        new("SimpleDdgiUpload.LifecycleTelemetry", d => d.SimpleDdgiUploadTiming.LifecycleTelemetryMicroseconds),
        new("SimpleDdgiUpload.AtlasMaintenance", d => d.SimpleDdgiUploadTiming.AtlasMaintenanceMicroseconds),
        new("SimpleDdgiUpload.BufferUpload", d => d.SimpleDdgiUploadTiming.BufferUploadMicroseconds),
        new("SimpleDdgiUpload.Other", d => d.SimpleDdgiUploadTiming.OtherMicroseconds),
        new("SimpleDdgiCapacity.CpuProbeState", d => d.SimpleDdgiUploadTiming.CapacityDetails.CpuProbeStateMicroseconds),
        new("SimpleDdgiCapacity.PlanCreation", d => d.SimpleDdgiUploadTiming.CapacityDetails.PlanCreationMicroseconds),
        new("SimpleDdgiCapacity.Predicate", d => d.SimpleDdgiUploadTiming.CapacityDetails.PredicateMicroseconds),
        new("SimpleDdgiCapacity.BufferSizeLookup", d => d.SimpleDdgiUploadTiming.CapacityDetails.BufferSizeLookupMicroseconds),
        new("SimpleDdgiCapacity.DeviceIdleWait", d => d.SimpleDdgiUploadTiming.CapacityDetails.DeviceIdleWaitMicroseconds),
        new("SimpleDdgiCapacity.BufferTransition", d => d.SimpleDdgiUploadTiming.CapacityDetails.BufferTransitionMicroseconds),
        new("SimpleDdgiCapacity.ReadbackReconciliation", d => d.SimpleDdgiUploadTiming.CapacityDetails.ReadbackReconciliationMicroseconds),
        new("SimpleDdgiCapacity.SampledAtlasBudget", d => d.SimpleDdgiUploadTiming.CapacityDetails.SampledAtlasBudgetMicroseconds),
        new("SimpleDdgiCapacity.SampledAtlasEnsure", d => d.SimpleDdgiUploadTiming.CapacityDetails.SampledAtlasEnsureMicroseconds),
        new("SimpleDdgiCapacity.DescriptorRegistration", d => d.SimpleDdgiUploadTiming.CapacityDetails.DescriptorRegistrationMicroseconds),
        new("SimpleDdgiCapacity.RetiredResourceDestruction", d => d.SimpleDdgiUploadTiming.CapacityDetails.RetiredResourceDestructionMicroseconds),
        new("DepthPrePassRecord", d => d.CpuDepthPrePassRecordMicroseconds),
        new("HiZBuildRecord", d => d.CpuHiZBuildRecordMicroseconds),
        new("LightCullRecord", d => d.CpuLightCullRecordMicroseconds),
        new("ForwardOpaqueRecord", d => d.CpuForwardOpaqueRecordMicroseconds),
        new("TransparentRecord", d => d.CpuTransparentRecordMicroseconds),
        new("DirectionalShadowRecord", d => d.CpuDirectionalShadowRecordMicroseconds),
        new("SpotShadowRecord", d => d.CpuSpotShadowRecordMicroseconds),
        new("PointShadowRecord", d => d.CpuPointShadowRecordMicroseconds),
        new("AmbientOcclusionRecord", d => d.CpuAmbientOcclusionRecordMicroseconds),
        new("AmbientOcclusionBlurRecord", d => d.CpuAmbientOcclusionBlurRecordMicroseconds),
        new("AccelerationStructureBuild", d => d.CpuAccelerationStructureBuildMicroseconds),
        new("AccelerationStructureBlasBuild", d => d.CpuAccelerationStructureBlasBuildMicroseconds),
        new("AccelerationStructureBlasCompaction", d => d.CpuAccelerationStructureBlasCompactionMicroseconds),
        new("AccelerationStructureTlasBuild", d => d.CpuAccelerationStructureTlasBuildMicroseconds),
        new("AccelerationStructureInstanceUpload", d => d.CpuAccelerationStructureInstanceUploadMicroseconds),
        new("BloomExtractRecord", d => d.CpuBloomExtractRecordMicroseconds),
        new("BloomDownsampleRecord", d => d.CpuBloomDownsampleRecordMicroseconds),
        new("BloomUpsampleRecord", d => d.CpuBloomUpsampleRecordMicroseconds),
        new("FogRecord", d => d.CpuFogRecordMicroseconds),
        new("CompositeRecord", d => d.CpuCompositeRecordMicroseconds),
        new("AutoExposureRecord", d => d.CpuAutoExposureRecordMicroseconds),
        new("FxaaRecord", d => d.CpuFxaaRecordMicroseconds),
        new("SmaaEdgeRecord", d => d.CpuSmaaEdgeRecordMicroseconds),
        new("SmaaBlendRecord", d => d.CpuSmaaBlendRecordMicroseconds),
        new("SmaaNeighborhoodRecord", d => d.CpuSmaaNeighborhoodRecordMicroseconds),
        new("ReflectionProbeCaptureRecord", d => d.CpuReflectionProbeCaptureRecordMicroseconds),
        new("ReflectionProbePrefilterRecord", d => d.CpuReflectionProbePrefilterRecordMicroseconds),
        new("SkinningRecord", d => d.CpuSkinningRecordMicroseconds),
        new("ParticleRecord", d => d.CpuParticleRecordMicroseconds),
        new("ParticleSimulation", d => d.CpuParticleSimulationMicroseconds),
        new("ParticleBuild", d => d.CpuParticleBuildMicroseconds),
        new("FoliageBuild", d => d.CpuFoliageBuildMicroseconds),
        new("FoliageUpload", d => d.CpuFoliageUploadMicroseconds),
        new("PrimaryCommandRecord", d => d.CpuPrimaryCommandRecordMicroseconds),
        new("SecondaryCommandRecord", d => d.CpuSecondaryCommandRecordMicroseconds),
        new("AcquireImage", d => d.CpuAcquireImageMicroseconds),
        new("QueueSubmit", d => d.CpuQueueSubmitMicroseconds),
        new("Present", d => d.CpuPresentMicroseconds),
        new("WaitForFrameFence", d => d.CpuWaitForFrameFenceMicroseconds),
        new("SwapchainImageOwnerWait",
            d => d.CpuSwapchainImageOwnerWaitMicroseconds),
        new("FrameResourceRecycleWait",
            d => d.CpuFrameResourceRecycleWaitMicroseconds),
        new("RuntimeStall", d => d.RuntimeStallMicrosecondsThisFrame)
    ];

    private readonly List<RendererDiagnostics> _samples;
    private readonly Dictionary<string, BudgetMetric> _worstBudgetMetrics =
        new(StringComparer.Ordinal);
    private RendererDiagnostics? _measurementBaseline;

    public SampleBenchmarkAnalyzer(
        int expectedSampleCount = 0,
        bool tailDdgiTimingProjection = false)
    {
        if (expectedSampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSampleCount));
        _samples = new List<RendererDiagnostics>(expectedSampleCount);
        _tailDdgiTimingProjection = tailDdgiTimingProjection;
    }

    internal void SetMeasurementBaseline(RendererDiagnostics diagnostics)
    {
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (_samples.Count != 0)
            throw new InvalidOperationException(
                "The measurement baseline must be set before the first sample.");

        _measurementBaseline = diagnostics;
    }

    public void AddSample(RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (budget == null)
            throw new ArgumentNullException(nameof(budget));

        _samples.Add(diagnostics);
        RenderBudgetSnapshot measuredBudget = budget;
        if (_tailDdgiTimingProjection)
        {
            measuredBudget = SampleTailDdgiLongSoakProfile.ProjectBudget(
                budget,
                diagnostics,
                materialStressMetricsNotApplicable: false).Budget;
        }
        AccumulateWorstBudgetMetrics(measuredBudget.Metrics);
    }

    public SampleBenchmarkReport CreateReport(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        int warmupFrameCount,
        int measurementFrameCount,
        int firstMeasurementFrameIndex,
        int lastMeasurementFrameIndex,
        SampleTailDdgiRunObservation? tailObservation = null,
        string controlledIsolationSettingsFingerprint = "unavailable")
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        RendererDiagnostics last = _samples.Count == 0 ? RendererDiagnostics.Empty : _samples[^1];
        IReadOnlyList<SampleBenchmarkTimingStats> gpuPasses = BuildTimingStats(GpuTimings, requireGpuTiming: true);
        IReadOnlyList<SampleBenchmarkTimingStats> cpuStages = BuildTimingStats(CpuTimings, requireGpuTiming: false);
        SampleBenchmarkTimingStats cpuFrame = BuildStats("CPU frame", _samples.Select(d => MicrosecondsToMilliseconds(d.CpuTotalDrawSceneMicroseconds)));
        SampleBenchmarkTimingStats gpuFrame = BuildStats(
            "GPU frame",
            _samples.Where(d => d.GpuTimingValid != 0).Select(d => MicrosecondsToMilliseconds(d.GpuFrameMicroseconds)));
        int gpuValidSamples = _samples.Count(d => d.GpuTimingValid != 0);
        SampleBenchmarkTimingStats gpuPassSum = BuildStats(
            "GPU independent pass sum",
            _samples
                .Where(static d => d.GpuTimingValid != 0)
                .Select(d => MicrosecondsToMilliseconds(
                    GpuIndependentTimings.Sum(selector =>
                        Math.Max(0L, selector.GetMicroseconds(d))))));
        SampleBenchmarkTimingStats gpuUnexplained = BuildStats(
            "GPU unexplained",
            _samples
                .Where(static d => d.GpuTimingValid != 0)
                .Select(d => MicrosecondsToMilliseconds(
                    d.GpuFrameMicroseconds -
                    GpuIndependentTimings.Sum(selector =>
                        Math.Max(0L, selector.GetMicroseconds(d))))));
        SampleBenchmarkTimingStats simpleDdgiTransportBlend = BuildStats(
            "Simple DDGI transport + blend",
            _samples
                .Where(static d => d.GpuTimingValid != 0)
                .Select(d => MicrosecondsToMilliseconds(
                    Math.Max(0L, d.GpuSimpleDdgiTransportMicroseconds) +
                    Math.Max(0L, d.GpuSimpleDdgiBlendMicroseconds))));
        SampleDdgiSchedulerRefreshEvidence schedulerRefreshEvidence =
            BuildSimpleDdgiSchedulerRefreshEvidence();
        BudgetMetric[] budgetMetrics = _worstBudgetMetrics.Values
            .OrderBy(static metric => metric.Name, StringComparer.Ordinal)
            .ToArray();
        MaterialWindowTiming materialTiming =
            ApplyMeasurementWindowTimingMetrics(
                budgetMetrics,
                cpuFrame,
                gpuFrame);
        SampleBenchmarkReflectionProbeRawEvidence reflectionRawEvidence =
            SampleBenchmarkReflectionProbeCaptureEvaluator.CaptureRaw(
                _samples,
                options,
                scenario,
                measurementFrameCount);
        SampleReflectionProbeCaptureEvidence reflectionCaptureEvidence =
            SampleBenchmarkReflectionProbeCaptureEvaluator.Recompute(
                reflectionRawEvidence);
        SampleBenchmarkAutomaticPlanarEvidence automaticPlanarEvidence =
            BuildAutomaticPlanarEvidence();
        SampleBenchmarkDdgiTransientRawEvidence ddgiTransientRawEvidence =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.CaptureRaw(
                _samples,
                options,
                scenario,
                measurementFrameCount);
        SampleBenchmarkDdgiTransientEvidence ddgiTransientEvidence =
            SampleBenchmarkDdgiTransientEvidenceEvaluator.Recompute(
                ddgiTransientRawEvidence);
        SampleBenchmarkCaptureContract captureContract =
            ApplyDdgiTransientEvidenceContract(
                BuildCaptureContract(
                    options,
                    scenario,
                    controlledIsolationSettingsFingerprint),
                ddgiTransientEvidence);

        return new SampleBenchmarkReport(
            Kind: "njulf-renderer-benchmark",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Options: options,
            Scenario: scenario,
            WarmupFrameCount: warmupFrameCount,
            MeasurementFrameCount: measurementFrameCount,
            FirstMeasurementFrameIndex: firstMeasurementFrameIndex,
            LastMeasurementFrameIndex: lastMeasurementFrameIndex,
            CpuFrameMilliseconds: cpuFrame,
            GpuFrameMilliseconds: gpuFrame,
            GpuTimingSupported: last.GpuTimingSupported,
            GpuTimingValidSampleCount: gpuValidSamples,
            GpuTimingUnavailableReason: last.GpuTimingValid == 0 ? last.GpuTimingUnavailableReason : string.Empty,
            GpuPasses: gpuPasses,
            CpuStages: cpuStages,
            Findings: BuildFindings(cpuFrame, gpuFrame, gpuPasses, cpuStages, budgetMetrics),
            BudgetMetrics: budgetMetrics,
            LastDiagnostics: last)
        {
            AccuracyOracleResults = SampleGiAccuracyOracleEvaluator.Evaluate(scenario, _samples),
            CaptureContract = captureContract,
            GpuIndependentPassSumMilliseconds = gpuPassSum,
            GpuUnexplainedMilliseconds = gpuUnexplained,
            SimpleDdgiTransportBlendMilliseconds = simpleDdgiTransportBlend,
            SimpleDdgiSchedulerRefresh = schedulerRefreshEvidence,
            CpuSpikeEvidence = BuildCpuSpikeEvidence(),
            ReflectionProbeCaptureRawEvidence = reflectionRawEvidence,
            ReflectionProbeCaptureEvidence = reflectionCaptureEvidence,
            AutomaticPlanarEvidence = automaticPlanarEvidence,
            DdgiTransientRawEvidence = ddgiTransientRawEvidence,
            DdgiTransientEvidence = ddgiTransientEvidence,
            TailDdgiEvidence = SampleTailDdgiRuntimeEvidenceBuilder.Create(
                _samples,
                tailObservation ?? SampleTailDdgiRunObservation.Empty,
                options.CaptureVariant),
            MaterialTimingEvidence =
                new SampleBenchmarkMaterialTimingEvidence(
                    materialTiming.Compile,
                    materialTiming.Upload,
                    materialTiming.Pipeline,
                    materialTiming.CompileExact,
                    materialTiming.UploadExact)
        };
    }

    internal static SampleBenchmarkDdgiTransientEvidence
        RecomputeDdgiTransientEvidenceCore(
            SampleBenchmarkDdgiTransientRawEvidence raw)
    {
        var failures = new List<string>();
        int expectedFrameCount = SampleBistroQualityCaptureContract.LoopFrameCount;
        IReadOnlyList<SampleBenchmarkDdgiTransientRawFrame> samples = raw.Frames;
        if (raw.MeasurementFrameCount != expectedFrameCount ||
            samples.Count != expectedFrameCount)
        {
            failures.Add(
                $"DDGI transient evidence requires exactly {expectedFrameCount} " +
                $"measured Bistro route frames; report={raw.MeasurementFrameCount}, " +
                $"observed={samples.Count}.");
            return CreateUnavailableDdgiTransientEvidence(failures);
        }

        var originBySerial = new Dictionary<ulong, int>(expectedFrameCount);
        ulong firstRouteFrameSerial = samples[0].CaptureFrameSerial;
        for (int index = 0; index < samples.Count; index++)
        {
            ulong frameSerial = samples[index].CaptureFrameSerial;
            if (samples[index].Active == 0)
            {
                failures.Add(
                    $"DDGI transient route frame {index} is not Simple-DDGI active.");
            }
            if (frameSerial == ulong.MaxValue)
            {
                failures.Add(
                    $"DDGI transient route frame {index} has the invalid submitted frame serial sentinel.");
                continue;
            }
            if ((ulong)index > ulong.MaxValue - firstRouteFrameSerial)
            {
                failures.Add(
                    $"DDGI transient route frame {index} cannot be represented " +
                    $"as a contiguous serial after {firstRouteFrameSerial}.");
            }
            else
            {
                ulong expectedSerial = firstRouteFrameSerial + (ulong)index;
                if (expectedSerial == ulong.MaxValue || frameSerial != expectedSerial)
                {
                    failures.Add(
                        $"DDGI transient route frame {index} has submitted serial " +
                        $"{frameSerial}; expected contiguous serial {expectedSerial}.");
                }
            }
            if (!originBySerial.TryAdd(frameSerial, index))
            {
                failures.Add(
                    $"DDGI transient route frame serial {frameSerial} is duplicated.");
            }
        }

        var diagnosticGenerationEdges = new List<int>(2);
        uint previousDiagnosticGeneration = samples[0].SourceLightingGeneration;
        if (previousDiagnosticGeneration == 0u)
        {
            failures.Add(
                "DDGI transient route frame 0 has no source-lighting generation.");
        }
        for (int index = 1; index < samples.Count; index++)
        {
            uint generation = samples[index].SourceLightingGeneration;
            if (generation == 0u)
            {
                failures.Add(
                    $"DDGI transient route frame {index} has no source-lighting generation.");
            }
            if (generation != previousDiagnosticGeneration)
            {
                uint expectedGeneration = AdvanceNonZeroGeneration(
                    previousDiagnosticGeneration);
                if (generation != expectedGeneration)
                {
                    failures.Add(
                        $"DDGI diagnostic source-lighting generation changed from " +
                        $"{previousDiagnosticGeneration} to {generation} at route frame " +
                        $"{index}; expected wrap-safe +1 generation " +
                        $"{expectedGeneration}.");
                }
                diagnosticGenerationEdges.Add(index);
            }
            previousDiagnosticGeneration = generation;
        }

        int[] authoredEvents =
        [
            SampleBistroQualityCaptureContract.LightingEventStartFrame,
            SampleBistroQualityCaptureContract.LightingEventEndFrame
        ];
        var completedBySubmittedSerial =
            new Dictionary<ulong, (int CompletionIndex, SimpleDdgiCompletedFrameEvidence Evidence)>(
                expectedFrameCount);
        var allRowCompletionFailures = new List<string>();
        for (int completionIndex = 0; completionIndex < samples.Count; completionIndex++)
        {
            SimpleDdgiCompletedFrameEvidence completed =
                samples[completionIndex].CompletionObserved;
            if (!completed.Valid || !completed.Submitted.Valid)
                continue;

            ulong submittedSerial = completed.Submitted.FrameSerial;
            // The first in-flight completions belong to the warmup route and
            // deliberately fall outside this measured-window join.
            if (!originBySerial.TryGetValue(submittedSerial, out int originIndex))
            {
                ValidateDdgiTransientFrame(
                    allRowCompletionFailures,
                    windowIndex: -1,
                    routeFrameIndex: completionIndex,
                    completed.Submitted.SourceLightingGeneration,
                    submittedSerial,
                    feedbackSupersededBySourceChange:
                        samples[completionIndex].SourceLightingGeneration !=
                        completed.Submitted.SourceLightingGeneration,
                    completed);
                continue;
            }
            if (completionIndex <= originIndex)
            {
                failures.Add(
                    $"DDGI frame serial {submittedSerial} completed at measurement " +
                    $"sample {completionIndex} before/at its origin {originIndex}.");
            }
            int expectedCompletionIndex = checked(
                originIndex + RenderingConstants.FramesInFlight);
            if (completionIndex != expectedCompletionIndex)
            {
                failures.Add(
                    $"DDGI frame serial {submittedSerial} completed at measurement " +
                    $"sample {completionIndex}; expected exact " +
                    $"FramesInFlight delay at {expectedCompletionIndex}.");
            }
            int expectedFrameSlot = checked((int)(
                submittedSerial % (ulong)RenderingConstants.FramesInFlight));
            if (completed.Submitted.FrameSlot != expectedFrameSlot)
            {
                failures.Add(
                    $"DDGI frame serial {submittedSerial} retained slot " +
                    $"{completed.Submitted.FrameSlot}; expected renderer slot " +
                    $"{expectedFrameSlot}.");
            }
            if (!completedBySubmittedSerial.TryAdd(
                    submittedSerial,
                    (completionIndex, completed)))
            {
                failures.Add(
                    $"DDGI frame serial {submittedSerial} has more than one completed record.");
            }
        }

        ulong previousSchedulerSerial = 0UL;
        int previousSchedulerOrigin = -1;
        // The final FramesInFlight submitted origins cannot fence-complete
        // before this exact measured route ends. Every earlier active origin
        // must join to one completion at its exact slot delay; an observed
        // row without a retired submission is allowed only as the canonical
        // default completion payload validated by the raw evaluator.
        int completableOriginCount = Math.Max(
            0,
            expectedFrameCount - RenderingConstants.FramesInFlight);
        var observedSubmittedGenerationEdges = new List<int>(2);
        var submittedSourceGenerations = new uint[completableOriginCount];
        uint previousSubmittedGeneration = 0u;
        bool hasPreviousSubmittedGeneration = false;
        for (int originIndex = 0;
             originIndex < completableOriginCount;
             originIndex++)
        {
            if (samples[originIndex].Active == 0)
                continue;

            ulong routeSerial = samples[originIndex].CaptureFrameSerial;
            if (!completedBySubmittedSerial.TryGetValue(
                    routeSerial,
                    out (int CompletionIndex,
                        SimpleDdgiCompletedFrameEvidence Evidence) joined))
            {
                continue;
            }

            uint generation = joined.Evidence.Submitted.SourceLightingGeneration;
            submittedSourceGenerations[originIndex] = generation;
            if (generation == 0u)
            {
                failures.Add(
                    $"DDGI submitted route frame {originIndex} has no " +
                    "source-lighting generation.");
            }
            if (hasPreviousSubmittedGeneration &&
                generation != previousSubmittedGeneration)
            {
                uint expectedGeneration = AdvanceNonZeroGeneration(
                    previousSubmittedGeneration);
                if (generation != expectedGeneration)
                {
                    allRowCompletionFailures.Add(
                        $"DDGI submitted source-lighting generation changed " +
                        $"from {previousSubmittedGeneration} to {generation} " +
                        $"at route frame {originIndex}; expected wrap-safe +1 " +
                        $"generation {expectedGeneration}.");
                }
                observedSubmittedGenerationEdges.Add(originIndex);
            }
            previousSubmittedGeneration = generation;
            hasPreviousSubmittedGeneration = true;
        }

        if (diagnosticGenerationEdges.Count != authoredEvents.Length)
        {
            failures.Add(
                $"DDGI transient evidence expected exactly two diagnostic " +
                $"source-lighting generation edges, but observed " +
                $"{diagnosticGenerationEdges.Count}: " +
                $"{string.Join(",", diagnosticGenerationEdges)}.");
        }
        var generationEdges = new List<int>(authoredEvents.Length);
        if (diagnosticGenerationEdges.Count == authoredEvents.Length)
        {
            for (int windowIndex = 0;
                 windowIndex < authoredEvents.Length;
                 windowIndex++)
            {
                int diagnosticEdge = diagnosticGenerationEdges[windowIndex];
                uint diagnosticGeneration =
                    samples[diagnosticEdge].SourceLightingGeneration;
                int edge = diagnosticEdge;
                if (diagnosticEdge > 0 &&
                    submittedSourceGenerations[diagnosticEdge - 1] ==
                        diagnosticGeneration)
                {
                    edge = diagnosticEdge - 1;
                }
                if (edge > 0 &&
                    submittedSourceGenerations[edge - 1] ==
                        diagnosticGeneration)
                {
                    failures.Add(
                        $"DDGI submitted source-lighting edge {windowIndex} " +
                        $"leads diagnostic route frame {diagnosticEdge} by " +
                        "more than one frame.");
                }
                if (submittedSourceGenerations[diagnosticEdge] !=
                    diagnosticGeneration)
                {
                    failures.Add(
                        $"DDGI diagnostic source-lighting edge {windowIndex} " +
                        $"at route frame {diagnosticEdge} has no matching " +
                        "exact submitted generation.");
                }
                generationEdges.Add(edge);

                int authored = authoredEvents[windowIndex];
                if (edge < authored || edge > authored + 1)
                {
                    failures.Add(
                        $"DDGI submitted source-lighting edge {windowIndex} " +
                        $"occurred at route frame {edge}; expected " +
                        $"[{authored},{authored + 1}].");
                }
                if (diagnosticEdge < edge || diagnosticEdge > edge + 1)
                {
                    failures.Add(
                        $"DDGI diagnostic source-lighting edge " +
                        $"{windowIndex} occurred at route frame " +
                        $"{diagnosticEdge}; expected exact submitted " +
                        $"edge {edge} or its one-frame delayed feedback " +
                        "sample.");
                }
            }

            uint expectedSubmittedGeneration =
                samples[0].SourceLightingGeneration;
            for (int originIndex = 0;
                 originIndex < completableOriginCount;
                 originIndex++)
            {
                if (generationEdges.Contains(originIndex))
                {
                    expectedSubmittedGeneration = AdvanceNonZeroGeneration(
                        expectedSubmittedGeneration);
                }
                uint submittedGeneration =
                    submittedSourceGenerations[originIndex];
                if (submittedGeneration != 0u &&
                    submittedGeneration != expectedSubmittedGeneration)
                {
                    allRowCompletionFailures.Add(
                        $"DDGI submitted route frame {originIndex} retained " +
                        $"source generation {submittedGeneration}; " +
                        $"authenticated route generation is " +
                        $"{expectedSubmittedGeneration}.");
                }
                if (submittedGeneration == 0u ||
                    samples[originIndex].SourceLightingGeneration ==
                        submittedGeneration)
                {
                    continue;
                }

                int edgeIndex = generationEdges.IndexOf(originIndex);
                bool exactDelayedFeedbackBoundary =
                    edgeIndex >= 0 &&
                    originIndex > 0 &&
                    submittedSourceGenerations[originIndex - 1] != 0u &&
                    samples[originIndex].SourceLightingGeneration ==
                        submittedSourceGenerations[originIndex - 1] &&
                    originIndex + 1 < samples.Count &&
                    samples[originIndex + 1].SourceLightingGeneration ==
                        submittedGeneration;
                if (!exactDelayedFeedbackBoundary)
                {
                    allRowCompletionFailures.Add(
                        $"DDGI diagnostic route frame {originIndex} retained " +
                        $"source generation " +
                        $"{samples[originIndex].SourceLightingGeneration}; " +
                        $"exact submitted generation is " +
                        $"{submittedGeneration}.");
                }
            }

            if (!observedSubmittedGenerationEdges.SequenceEqual(
                    generationEdges))
            {
                allRowCompletionFailures.Add(
                    "DDGI submitted source-lighting transition sequence " +
                    $"{string.Join(",", observedSubmittedGenerationEdges)} " +
                    "does not match the two authenticated route edges " +
                    $"{string.Join(",", generationEdges)}.");
            }
        }

        for (int originIndex = 0;
             originIndex < completableOriginCount;
             originIndex++)
        {
            if (samples[originIndex].Active == 0)
            {
                continue;
            }

            ulong routeSerial = samples[originIndex].CaptureFrameSerial;
            if (!completedBySubmittedSerial.TryGetValue(
                    routeSerial,
                    out (int CompletionIndex,
                        SimpleDdgiCompletedFrameEvidence Evidence) joined))
            {
                failures.Add(
                    $"DDGI active route frame {originIndex}, serial {routeSerial}, " +
                    "has no exact completed scheduler identity.");
                previousSchedulerSerial = 0UL;
                previousSchedulerOrigin = -1;
                continue;
            }

            SimpleDdgiSubmittedFrameEvidence submitted = joined.Evidence.Submitted;
            int owningWindowIndex = generationEdges.Count == 2
                ? originIndex >= generationEdges[1]
                    ? 1
                    : originIndex >= generationEdges[0]
                        ? 0
                        : -1
                : -1;
            ValidateDdgiTransientFrame(
                allRowCompletionFailures,
                owningWindowIndex,
                originIndex,
                submitted.SourceLightingGeneration,
                routeSerial,
                feedbackSupersededBySourceChange:
                    samples[joined.CompletionIndex].SourceLightingGeneration !=
                    submitted.SourceLightingGeneration,
                joined.Evidence);
            ulong schedulerSerial = submitted.SchedulerFrameSerial;
            if (!submitted.FrameSerialsValid)
            {
                failures.Add(
                    $"DDGI active route frame {originIndex} retained invalid " +
                    $"renderer/scheduler serials {routeSerial}/{schedulerSerial}.");
            }
            if (previousSchedulerOrigin >= 0)
            {
                if (previousSchedulerSerial >= ulong.MaxValue - 1UL)
                {
                    failures.Add(
                        $"DDGI scheduler serial at active route frame " +
                        $"{previousSchedulerOrigin} cannot advance without " +
                        "entering a sentinel or wrapping.");
                }
                else
                {
                    ulong expectedSchedulerSerial = previousSchedulerSerial + 1UL;
                    if (schedulerSerial != expectedSchedulerSerial)
                    {
                        failures.Add(
                            $"DDGI active route frame {originIndex} retained " +
                            $"scheduler serial {schedulerSerial}; expected " +
                            $"contiguous serial {expectedSchedulerSerial} after " +
                            $"active route frame {previousSchedulerOrigin}.");
                    }
                }
            }

            previousSchedulerSerial = schedulerSerial;
            previousSchedulerOrigin = originIndex;
        }

        if (failures.Count != 0 || generationEdges.Count != authoredEvents.Length)
        {
            failures.AddRange(allRowCompletionFailures);
            return CreateUnavailableDdgiTransientEvidence(failures);
        }

        var windows = new List<SampleBenchmarkDdgiTransientWindow>(2);
        for (int windowIndex = 0; windowIndex < authoredEvents.Length; windowIndex++)
        {
            int edgeIndex = generationEdges[windowIndex];
            int endExclusive = windowIndex + 1 < generationEdges.Count
                ? generationEdges[windowIndex + 1]
                : expectedFrameCount;
            uint sourceGeneration =
                submittedSourceGenerations[edgeIndex];
            uint priorSourceGeneration =
                submittedSourceGenerations[edgeIndex - 1];
            var candidateFrames = new List<SampleBenchmarkDdgiTransientFrame>();
            int certificateIndex = -1;
            int firstLivePropagationIndex = -1;

            for (int originIndex = edgeIndex;
                 originIndex < Math.Min(endExclusive, completableOriginCount);
                 originIndex++)
            {
                SampleBenchmarkDdgiTransientRawFrame origin = samples[originIndex];
                ulong submittedSerial = origin.CaptureFrameSerial;
                if (!completedBySubmittedSerial.TryGetValue(
                        submittedSerial,
                        out (int CompletionIndex, SimpleDdgiCompletedFrameEvidence Evidence) joined))
                {
                    failures.Add(
                        $"DDGI transient window {windowIndex} is missing completed " +
                        $"evidence for route frame {originIndex}, serial {submittedSerial}.");
                    break;
                }

                SimpleDdgiCompletedFrameEvidence completed = joined.Evidence;
                candidateFrames.Add(new SampleBenchmarkDdgiTransientFrame(
                    originIndex,
                    originIndex,
                    joined.CompletionIndex,
                    joined.CompletionIndex,
                    completed));

                if (firstLivePropagationIndex < 0 &&
                    HasExactDynamicDdgiTransientClosure(
                        completed,
                        sourceGeneration))
                {
                    firstLivePropagationIndex = originIndex;
                }

                if (HasExactCertifiedDdgiTransientClosure(completed))
                {
                    certificateIndex = originIndex;
                    break;
                }
            }

            int closureIndex = certificateIndex >= 0
                ? certificateIndex
                : firstLivePropagationIndex;
            string closureKind = certificateIndex >= 0
                ? SampleBenchmarkDdgiTransientClosureKind.CertifiedTail
                : SampleBenchmarkDdgiTransientClosureKind.DynamicLivePropagation;
            if (closureIndex < 0)
            {
                failures.Add(
                    windowIndex + 1 < authoredEvents.Length
                        ? $"DDGI transient window {windowIndex} overlapped the next " +
                          $"source-lighting edge before the new generation became live."
                        : $"DDGI transient window {windowIndex} did not complete with " +
                          $"an authenticated live-propagation response inside the route.");
                continue;
            }

            int retainedFrameCount = closureIndex - edgeIndex + 1;
            SampleBenchmarkDdgiTransientFrame[] retainedFrames = candidateFrames
                .Take(retainedFrameCount)
                .ToArray();
            if (certificateIndex >= 0)
            {
                ValidateDdgiTransientAuditLifecycle(
                    failures,
                    windowIndex,
                    sourceGeneration,
                    retainedFrames);
            }
            else
            {
                for (int index = 0; index < retainedFrames.Length - 1; index++)
                {
                    if (retainedFrames[index].Completed.Submitted
                            .LivePropagationSourceGeneration == sourceGeneration)
                    {
                        failures.Add(
                            $"DDGI transient window {windowIndex} selected route " +
                            $"frame {closureIndex} after the new source generation " +
                            $"was already live at route frame " +
                            $"{retainedFrames[index].RouteFrameIndex}.");
                        break;
                    }
                }
            }

            windows.Add(new SampleBenchmarkDdgiTransientWindow(
                windowIndex,
                authoredEvents[windowIndex],
                edgeIndex,
                edgeIndex - authoredEvents[windowIndex],
                priorSourceGeneration,
                sourceGeneration,
                closureKind,
                closureIndex,
                closureIndex - edgeIndex,
                retainedFrames[0].Completed.Submitted.FrameSerial,
                retainedFrames[^1].Completed.Submitted.FrameSerial,
                retainedFrames[0].Completed.Submitted.SchedulerFrameSerial,
                retainedFrames[^1].Completed.Submitted.SchedulerFrameSerial,
                Array.AsReadOnly(retainedFrames)));
        }

        failures.AddRange(allRowCompletionFailures);
        if (failures.Count != 0 || windows.Count != authoredEvents.Length)
            return CreateUnavailableDdgiTransientEvidence(failures);

        return new SampleBenchmarkDdgiTransientEvidence(
            Applicable: true,
            Available: true,
            Array.Empty<string>(),
            Array.AsReadOnly(windows.ToArray()));
    }

    private static void ValidateDdgiTransientFrame(
        ICollection<string> failures,
        int windowIndex,
        int routeFrameIndex,
        uint sourceGeneration,
        ulong submittedSerial,
        bool feedbackSupersededBySourceChange,
        in SimpleDdgiCompletedFrameEvidence completed)
    {
        string prefix = windowIndex >= 0
            ? $"DDGI transient window {windowIndex} route frame {routeFrameIndex}"
            : $"DDGI transient route frame {routeFrameIndex}";
        if (completed.Submitted.FrameSerial != submittedSerial)
            failures.Add($"{prefix} joined the wrong submitted frame serial.");
        if (!completed.Submitted.FrameSerialsValid)
        {
            failures.Add(
                $"{prefix} retained invalid renderer/scheduler serials " +
                $"{completed.Submitted.FrameSerial}/" +
                $"{completed.Submitted.SchedulerFrameSerial}.");
        }
        if (completed.Submitted.SourceLightingGeneration != sourceGeneration)
        {
            failures.Add(
                $"{prefix} retained source generation " +
                $"{completed.Submitted.SourceLightingGeneration}; expected {sourceGeneration}.");
        }
        if (!completed.GpuTimingPassSetAligned ||
            completed.Submitted.AdmittedGpuTimingPasses !=
                completed.Submitted.IntendedGpuPasses ||
            completed.CompletedGpuTimingPasses !=
                completed.Submitted.IntendedGpuPasses)
        {
            failures.Add(
                $"{prefix} does not have exact intended/admitted/completed " +
                "DDGI GPU pass coverage.");
        }
        if (completed.Submitted.SchedulerMode !=
            SimpleDdgiSchedulerMode.GpuResident)
        {
            failures.Add(
                $"{prefix} is not a GPU-resident scheduler submission.");
        }
        if (completed.Submitted.QueueTransactionGeneration == 0u ||
            completed.Submitted.QueueTransactionGeneration !=
                completed.Submitted.SchedulerResourceGeneration)
        {
            failures.Add(
                $"{prefix} retained a queue epoch that is not the resident " +
                "scheduler-resource generation.");
        }
        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        bool activeAuditIdentity = tail.Phase is
            SimpleDdgiTransportPhase.AuditFrozen or
            SimpleDdgiTransportPhase.Certified;
        if (!tail.Generations.IsInitialized ||
            tail.SolveEpoch != tail.Generations.Solve ||
            (activeAuditIdentity &&
             tail.AuditEpoch != tail.Generations.Audit) ||
            !tail.HasDurableSummary ||
            tail.Summary.Generations != tail.Generations ||
            tail.Generations.VolumeTable !=
                completed.Submitted.TransportTopologyGeneration ||
            tail.Generations.PhysicalOwnership !=
                completed.Submitted.TransportTopologyGeneration ||
            tail.Generations.SourceLighting !=
                completed.Submitted.SourceLightingGeneration ||
            tail.Generations.Queue !=
                completed.Submitted.QueueTransactionGeneration ||
            tail.Generations.SchedulerResources !=
                completed.Submitted.SchedulerResourceGeneration)
        {
            failures.Add(
                $"{prefix} retained tail generations/digest that do not " +
                "exactly bind the submitted topology, source, queue, and " +
                "scheduler identity.");
        }
        SimpleDdgiGpuPassMask intended =
            completed.Submitted.IntendedGpuPasses;
        bool auditFrozen = tail.Phase == SimpleDdgiTransportPhase.AuditFrozen;
        bool auditIntended =
            (intended & SimpleDdgiGpuPassMask.TransportAudit) != 0;
        bool urgentRelightIntended =
            (intended & SimpleDdgiGpuPassMask.UrgentRelight) != 0;
        if (urgentRelightIntended !=
            completed.GpuUrgentRelightTimingAvailable)
        {
            failures.Add(
                $"{prefix} urgent-relight timing availability does not match " +
                "the exact completed pass mask.");
        }
        if (tail.Phase == SimpleDdgiTransportPhase.Certified &&
            !tail.IsAcceptedFor(completed.Submitted))
        {
            failures.Add(
                $"{prefix} retained a Certified tail row without an exact " +
                "current numerical/lifecycle certificate.");
        }

        if (auditFrozen)
        {
            ValidateAuditFrozenDdgiTransientFrame(failures, prefix, completed);
        }
        else
        {
            ValidateSchedulerDdgiTransientFrame(
                failures,
                prefix,
                feedbackSupersededBySourceChange,
                completed);
            if (auditIntended)
                failures.Add($"{prefix} mixed scheduler work with a frozen audit.");
        }
    }

    private static void ValidateAuditFrozenDdgiTransientFrame(
        ICollection<string> failures,
        string prefix,
        in SimpleDdgiCompletedFrameEvidence completed)
    {
        SimpleDdgiGpuPassMask forbidden =
            SimpleDdgiGpuPassMask.Schedule |
            SimpleDdgiGpuPassMask.Trace |
            SimpleDdgiGpuPassMask.DirectionalRadiance |
            SimpleDdgiGpuPassMask.AcceleratedSolve |
            SimpleDdgiGpuPassMask.Transport |
            SimpleDdgiGpuPassMask.Blend |
            SimpleDdgiGpuPassMask.RelocateClassify |
            SimpleDdgiGpuPassMask.Publish |
            SimpleDdgiGpuPassMask.UrgentRelight |
            SimpleDdgiGpuPassMask.SchedulerCommit |
            SimpleDdgiGpuPassMask.ScheduleTailAdmit |
            SimpleDdgiGpuPassMask.ScheduleEmit;
        if ((completed.Submitted.IntendedGpuPasses & forbidden) != 0 ||
            (completed.Submitted.AdmittedGpuTimingPasses & forbidden) != 0 ||
            (completed.CompletedGpuTimingPasses & forbidden) != 0)
        {
            failures.Add(
                $"{prefix} is AuditFrozen but retained ordinary " +
                "scheduler/solve/publication timing scopes.");
        }

        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        if (tail.Reason !=
                SimpleDdgiTransportCertificationReason.AuditInProgress ||
            tail.AuditComplete ||
            tail.CertificateCurrent ||
            tail.Summary.IsComplete ||
            !tail.HasCompleteIdentity ||
            !tail.HasDurableSummary ||
            !tail.HasCompleteAuditFeedbackLifecycle)
        {
            failures.Add(
                $"{prefix} has no complete frozen-audit identity/digest.");
        }
        if (tail.AuditPlannedChunkCount == 0u ||
            tail.AuditSubmittedChunkCount == 0u ||
            tail.AuditSubmittedChunkCount > tail.AuditPlannedChunkCount ||
            tail.AuditFirstSubmissionFrameSerial == 0UL ||
            tail.AuditFirstSubmissionFrameSerial == ulong.MaxValue ||
            tail.AuditFinalSubmissionFrameSerial == 0UL ||
            tail.AuditFinalSubmissionFrameSerial == ulong.MaxValue ||
            tail.AuditFirstSubmissionFrameSerial !=
                tail.Summary.FirstFrameSerial ||
            tail.AuditFinalSubmissionFrameSerial !=
                tail.Summary.FinalFrameSerial ||
            tail.AuditSubmittedChunkCount != tail.Summary.ChunkCount)
        {
            failures.Add(
                $"{prefix} has invalid frozen-audit cursor/lifecycle state.");
        }
        if (!SimpleDdgiAuditCardinalityContract.TryResolve(
                completed.Submitted.ActiveProbeCount,
                completed.Submitted.AuditPhysicalProbeCount,
                tail.ExpectedParticipantCount,
                out uint expectedChunkCount,
                out uint expectedTexelCount,
                out _) ||
            tail.AuditPlannedChunkCount != expectedChunkCount ||
            tail.ExpectedTexelCount != expectedTexelCount ||
            tail.Summary.ExpectedParticipantCount !=
                tail.ExpectedParticipantCount ||
            tail.Summary.ExpectedTexelCount != expectedTexelCount)
        {
            failures.Add(
                $"{prefix} frozen-audit cardinality/texel evidence does not " +
                "match the submitted active field.");
        }

        bool auditDispatch =
            (completed.Submitted.IntendedGpuPasses &
             SimpleDdgiGpuPassMask.TransportAudit) != 0;
        SimpleDdgiGpuPassMask expectedPasses = auditDispatch
            ? SimpleDdgiGpuPassMask.TransportAudit
            : SimpleDdgiGpuPassMask.None;
        if (completed.Submitted.IntendedGpuPasses != expectedPasses)
        {
            failures.Add(
                $"{prefix} does not match the phase-specific Bistro DDGI " +
                $"pass mask for AuditFrozen " +
                $"{(auditDispatch ? "dispatch" : "await")}.");
        }
        if (auditDispatch)
        {
            if (!completed.GpuTimingAvailable ||
                !completed.GpuDdgiTotalTimingAvailable ||
                !completed.GpuTransportAuditTimingAvailable)
            {
                failures.Add(
                    $"{prefix} audit dispatch has no exact transport-audit " +
                    "GPU timing/total.");
            }
            if (tail.AuditFinalSubmissionFrameSerial !=
                completed.Submitted.SchedulerFrameSerial)
            {
                failures.Add(
                    $"{prefix} audit dispatch does not own the frozen cursor's " +
                    "latest scheduler-frame serial.");
            }
        }
        else
        {
            SimpleDdgiGpuPassMask auditOrScheduler = forbidden |
                SimpleDdgiGpuPassMask.TransportAudit;
            if ((completed.Submitted.IntendedGpuPasses & auditOrScheduler) != 0 ||
                (completed.Submitted.AdmittedGpuTimingPasses & auditOrScheduler) != 0 ||
                (completed.CompletedGpuTimingPasses & auditOrScheduler) != 0 ||
                completed.GpuTimingAvailable ||
                completed.GpuDdgiTotalTimingAvailable ||
                completed.GpuTransportAuditTimingAvailable ||
                completed.GpuTransportAuditMicroseconds != 0)
            {
                failures.Add(
                    $"{prefix} frozen-audit await row retained GPU " +
                    "scheduler/audit work or a DDGI total.");
            }
            if (!tail.AuditDispatchComplete ||
                tail.AuditSubmittedChunkCount != tail.AuditPlannedChunkCount ||
                tail.AuditFinalSubmissionFrameSerial == ulong.MaxValue ||
                tail.AuditFinalSubmissionFrameSerial >=
                    completed.Submitted.SchedulerFrameSerial)
            {
                failures.Add(
                    $"{prefix} frozen-audit await row lacks a completed prior " +
                    "audit-dispatch cursor.");
            }
        }
        if (HasAnySchedulerFeedbackPayload(completed))
        {
            failures.Add(
                $"{prefix} is AuditFrozen but retained scheduler feedback.");
        }
    }

    private static void ValidateSchedulerDdgiTransientFrame(
        ICollection<string> failures,
        string prefix,
        bool feedbackSupersededBySourceChange,
        in SimpleDdgiCompletedFrameEvidence completed)
    {
        SimpleDdgiGpuPassMask intended =
            completed.Submitted.IntendedGpuPasses;
        if (!completed.GpuTimingAvailable ||
            !completed.GpuDdgiTotalTimingAvailable)
        {
            failures.Add(
                $"{prefix} has no same-slot completed total DDGI GPU timing.");
        }
        SimpleDdgiGpuPassMask required =
            SimpleDdgiGpuPassMask.Schedule |
            SimpleDdgiGpuPassMask.Trace |
            SimpleDdgiGpuPassMask.RelocateClassify |
            SimpleDdgiGpuPassMask.Publish |
            SimpleDdgiGpuPassMask.SchedulerCommit |
            SimpleDdgiGpuPassMask.ScheduleTailAdmit |
            SimpleDdgiGpuPassMask.ScheduleEmit;
        if ((intended & required) != required)
        {
            failures.Add(
                $"{prefix} is missing an ordinary scheduler/trace/relocate/" +
                "publish/commit timing scope.");
        }
        if ((intended & SimpleDdgiGpuPassMask.TransportAudit) != 0)
            failures.Add($"{prefix} mixed scheduler work with a frozen audit.");

        bool accelerated =
            (intended & SimpleDdgiGpuPassMask.AcceleratedSolve) != 0;
        SimpleDdgiGpuPassMask legacyMask =
            SimpleDdgiGpuPassMask.Transport | SimpleDdgiGpuPassMask.Blend;
        bool completeLegacy = (intended & legacyMask) == legacyMask;
        bool partialLegacy = (intended & legacyMask) != 0 && !completeLegacy;
        if (partialLegacy || accelerated == completeLegacy)
        {
            failures.Add(
                $"{prefix} does not identify exactly one complete accelerated " +
                "or legacy transport/blend path.");
        }
        SimpleDdgiTransportPhase phase =
            completed.Submitted.TailCertificate.Phase;
        bool expectedAccelerated =
            phase == SimpleDdgiTransportPhase.AcceleratedSolve;
        bool expectedLegacy = phase is
            SimpleDdgiTransportPhase.SourceRepair or
            SimpleDdgiTransportPhase.Certified;
        if ((!expectedAccelerated && !expectedLegacy) ||
            accelerated != expectedAccelerated ||
            completeLegacy != expectedLegacy)
        {
            failures.Add(
                $"{prefix} transport pass mask does not match tail phase " +
                $"{phase}.");
        }
        SimpleDdgiGpuPassMask expectedPhasePasses = required |
            (expectedAccelerated
                ? SimpleDdgiGpuPassMask.AcceleratedSolve
                : expectedLegacy
                    ? legacyMask
                    : SimpleDdgiGpuPassMask.None);
        SimpleDdgiGpuPassMask optionalPhasePasses =
            SimpleDdgiGpuPassMask.UrgentRelight |
            SimpleDdgiGpuPassMask.DirectionalRadiance;
        if ((intended & expectedPhasePasses) != expectedPhasePasses ||
            (intended & ~(expectedPhasePasses | optionalPhasePasses)) != 0)
        {
            failures.Add(
                $"{prefix} does not match the phase-specific Bistro DDGI " +
                $"pass mask for {phase}.");
        }
        if ((expectedAccelerated &&
             completed.Submitted.CachedSweepCount <= 0) ||
            (expectedLegacy && completed.Submitted.CachedSweepCount != 0))
        {
            failures.Add(
                $"{prefix} cached-sweep count does not match tail phase " +
                $"{phase}.");
        }
        if (!completed.GpuScheduleTimingAvailable ||
            !completed.GpuSchedulerCommitTimingAvailable ||
            !completed.GpuSchedulerTailAdmitTimingAvailable ||
            !completed.GpuSchedulerEmitTimingAvailable)
        {
            failures.Add(
                $"{prefix} lacks exact schedule/tail-admit/emit/commit GPU timing.");
        }
        if (accelerated && !completed.GpuAcceleratedSolveTimingAvailable)
        {
            failures.Add(
                $"{prefix} recorded cached sweeps without accelerated-solve GPU timing.");
        }
        if (accelerated && completed.Submitted.TailCertificate.Phase !=
                SimpleDdgiTransportPhase.AcceleratedSolve)
        {
            failures.Add(
                $"{prefix} recorded accelerated-solve work outside the " +
                "accelerated transport phase.");
        }
        if (!completed.SchedulerFeedbackAvailable)
        {
            if (!feedbackSupersededBySourceChange)
                failures.Add($"{prefix} has no same-slot scheduler feedback.");
            if (HasAnySchedulerFeedbackPayload(completed))
            {
                failures.Add(
                    $"{prefix} retained a partial scheduler-feedback payload " +
                    "after a source-generation change invalidated it.");
            }
            return;
        }
        if (!completed.SchedulerFeedbackFrameAligned)
            failures.Add($"{prefix} scheduler feedback frame serial is not aligned.");
        if (completed.SchedulerFeedbackFrameSerial !=
            completed.Submitted.SchedulerFrameSerial)
            failures.Add($"{prefix} scheduler feedback retained the wrong frame serial.");
        if (!completed.SchedulerFeedbackGenerationAligned)
            failures.Add($"{prefix} scheduler feedback generations are not aligned.");
        if (completed.SchedulerFeedbackVolumeResourceGeneration !=
                completed.Submitted.VolumeResourceGeneration ||
            completed.SchedulerFeedbackTransportTopologyGeneration !=
                completed.Submitted.TransportTopologyGeneration ||
            completed.SchedulerFeedbackSchedulerResourceGeneration !=
                completed.Submitted.SchedulerResourceGeneration ||
            completed.SchedulerFeedbackQueueTransactionGeneration !=
                completed.Submitted.QueueTransactionGeneration ||
            completed.SchedulerFeedbackSourceLightingGeneration !=
                completed.Submitted.SourceLightingGeneration)
        {
            failures.Add($"{prefix} scheduler feedback retained mismatched generation values.");
        }
        if (!IsSameOrNextNonZeroGeneration(
                completed.Submitted.TransportGeneration,
                completed.SchedulerFeedbackTransportGeneration))
        {
            failures.Add(
                $"{prefix} scheduler feedback transport generation " +
                $"{completed.SchedulerFeedbackTransportGeneration} is neither " +
                $"submitted current {completed.Submitted.TransportGeneration} " +
                "nor its exact wrap-safe successor.");
        }
        if (completed.SchedulerFeedbackStatusFlags != 0u)
        {
            failures.Add(
                $"{prefix} scheduler feedback status is " +
                $"0x{completed.SchedulerFeedbackStatusFlags:x8}.");
        }
        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        if (tail.Phase == SimpleDdgiTransportPhase.Certified &&
            !HasExactCertifiedDdgiTransientClosure(completed))
        {
            failures.Add(
                $"{prefix} Certified closure is not an exact quiescent, " +
                "generation-current participant witness; post-audit work " +
                "requires a fresh lifecycle.");
        }
    }

    private static bool HasExactCertifiedDdgiTransientClosure(
        in SimpleDdgiCompletedFrameEvidence completed)
    {
        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        return tail.IsAcceptedFor(completed.Submitted) &&
            completed.SchedulerFeedbackAvailable &&
            completed.SchedulerFeedbackFrameAligned &&
            completed.SchedulerFeedbackGenerationAligned &&
            completed.SchedulerFeedbackStatusFlags == 0u &&
            completed.SchedulerFeedbackTransportGeneration ==
                completed.Submitted.TransportGeneration &&
            completed.SchedulerFeedbackTransportGeneration ==
                tail.Generations.CanonicalField &&
            completed.SchedulerSolveEpoch == 0u &&
            completed.SchedulerSolveParticipantCount ==
                tail.ExpectedParticipantCount &&
            completed.SchedulerSolveVisitedCount == 0u &&
            completed.SchedulerActiveCanonicalMutationCount == 0u &&
            completed.SchedulerActiveSourceMutationCount == 0u &&
            completed.SchedulerBlockingTailSourceWorkCount == 0u;
    }

    internal static bool HasExactDynamicDdgiTransientClosure(
        in SimpleDdgiCompletedFrameEvidence completed,
        uint sourceGeneration)
    {
        SimpleDdgiSubmittedFrameEvidence submitted = completed.Submitted;
        SimpleDdgiTailCertificateFrameEvidence tail = submitted.TailCertificate;
        return sourceGeneration != 0u &&
            submitted.SourceLightingGeneration == sourceGeneration &&
            submitted.LivePropagationSourceGeneration == sourceGeneration &&
            IsExactOrdinaryFeedbackIdentity(completed, tail) &&
            IsSameOrNextNonZeroGeneration(
                submitted.TransportGeneration,
                completed.SchedulerFeedbackTransportGeneration) &&
            completed.SchedulerFeedbackStatusFlags == 0u &&
            completed.SchedulerSolveParticipantCount > 0u &&
            completed.SchedulerPublishedWorkCount > 0u;
    }

    private static void ValidateDdgiTransientAuditLifecycle(
        ICollection<string> failures,
        int windowIndex,
        uint sourceGeneration,
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames)
    {
        uint submittedChunkCount = 0u;
        uint plannedChunkCount = 0u;
        ulong firstSchedulerSerial = 0UL;
        ulong finalSchedulerSerial = 0UL;
        ulong solveFeedbackSerial = 0UL;
        ulong triggerFeedbackSerial = 0UL;
        SimpleDdgiTransportGenerations frozenGenerations = default;
        uint frozenSolveEpoch = 0u;
        uint frozenAuditEpoch = 0u;
        uint frozenExpectedParticipantCount = 0u;
        uint frozenExpectedTexelCount = 0u;
        int frozenPhysicalProbeCount = 0;
        uint frozenVolumeResourceGeneration = 0u;
        uint frozenPublishedPropagationGeneration = 0u;
        bool sawDispatch = false;
        bool sawAwait = false;
        SimpleDdgiTailCertificateFrameEvidence certificateTail = default;
        bool sawCertificate = false;

        foreach (SampleBenchmarkDdgiTransientFrame frame in frames)
        {
            SimpleDdgiCompletedFrameEvidence completed = frame.Completed;
            SimpleDdgiTailCertificateFrameEvidence tail =
                completed.Submitted.TailCertificate;
            bool auditFrozen =
                tail.Phase == SimpleDdgiTransportPhase.AuditFrozen;
            bool auditDispatch = auditFrozen &&
                (completed.Submitted.IntendedGpuPasses &
                 SimpleDdgiGpuPassMask.TransportAudit) != 0;
            if (auditDispatch)
            {
                if (sawAwait)
                {
                    failures.Add(
                        $"DDGI transient window {windowIndex} route frame " +
                        $"{frame.RouteFrameIndex} resumed audit dispatch after " +
                        "entering readback await.");
                }
                uint delta = tail.AuditSubmittedChunkCount >= submittedChunkCount
                    ? tail.AuditSubmittedChunkCount - submittedChunkCount
                    : uint.MaxValue;
                uint remaining = tail.AuditPlannedChunkCount >=
                    submittedChunkCount
                        ? tail.AuditPlannedChunkCount - submittedChunkCount
                        : 0u;
                uint expectedDelta = Math.Min(
                    SimpleDdgiAuditCardinalityContract
                        .MaximumChunksPerSubmittedFrame,
                    remaining);
                if (expectedDelta == 0u || delta != expectedDelta)
                {
                    failures.Add(
                        $"DDGI transient window {windowIndex} route frame " +
                        $"{frame.RouteFrameIndex} advanced the frozen audit by " +
                        $"{delta} chunks; greedy dispatch required " +
                        $"{expectedDelta} of {remaining} remaining chunks.");
                }

                if (!sawDispatch)
                {
                    firstSchedulerSerial =
                        completed.Submitted.SchedulerFrameSerial;
                    solveFeedbackSerial =
                        tail.AuditSolveFeedbackFrameSerial;
                    triggerFeedbackSerial =
                        tail.AuditTriggerFeedbackFrameSerial;
                    frozenGenerations = tail.Generations;
                    frozenSolveEpoch = tail.SolveEpoch;
                    frozenAuditEpoch = tail.AuditEpoch;
                    frozenExpectedParticipantCount =
                        tail.ExpectedParticipantCount;
                    frozenExpectedTexelCount = tail.ExpectedTexelCount;
                    frozenPhysicalProbeCount =
                        completed.Submitted.AuditPhysicalProbeCount;
                    frozenVolumeResourceGeneration =
                        completed.Submitted.VolumeResourceGeneration;
                    frozenPublishedPropagationGeneration = completed.Submitted
                        .PublishedPropagationGeneration;
                    plannedChunkCount = tail.AuditPlannedChunkCount;
                    if (tail.AuditFirstSubmissionFrameSerial !=
                        firstSchedulerSerial)
                    {
                        failures.Add(
                            $"DDGI transient window {windowIndex} first audit " +
                            "dispatch does not own the first scheduler-frame serial.");
                    }
                    if (!HasSameFrozenDdgiAuditIdentity(
                            completed,
                            frozenGenerations,
                            frozenSolveEpoch,
                            frozenAuditEpoch,
                            frozenExpectedParticipantCount,
                            frozenExpectedTexelCount,
                            frozenPhysicalProbeCount,
                            frozenVolumeResourceGeneration,
                            frozenPublishedPropagationGeneration,
                            certificate: false))
                    {
                        failures.Add(
                            $"DDGI transient window {windowIndex} first audit " +
                            "dispatch does not bind its submitted tuple to " +
                            "the frozen tail identity.");
                    }
                }
                else if (tail.AuditPlannedChunkCount != plannedChunkCount ||
                         tail.AuditFirstSubmissionFrameSerial !=
                            firstSchedulerSerial ||
                         tail.AuditSolveFeedbackFrameSerial !=
                            solveFeedbackSerial ||
                         tail.AuditTriggerFeedbackFrameSerial !=
                            triggerFeedbackSerial ||
                         !HasSameFrozenDdgiAuditIdentity(
                             completed,
                             frozenGenerations,
                             frozenSolveEpoch,
                             frozenAuditEpoch,
                             frozenExpectedParticipantCount,
                             frozenExpectedTexelCount,
                             frozenPhysicalProbeCount,
                             frozenVolumeResourceGeneration,
                             frozenPublishedPropagationGeneration,
                             certificate: false))
                {
                    failures.Add(
                        $"DDGI transient window {windowIndex} changed its " +
                        "frozen audit plan/first serial between dispatches.");
                }

                finalSchedulerSerial =
                    completed.Submitted.SchedulerFrameSerial;
                submittedChunkCount = tail.AuditSubmittedChunkCount;
                bool expectedComplete =
                    submittedChunkCount == tail.AuditPlannedChunkCount;
                if (tail.AuditDispatchComplete != expectedComplete)
                {
                    failures.Add(
                        $"DDGI transient window {windowIndex} route frame " +
                        $"{frame.RouteFrameIndex} retained a mismatched audit " +
                        "dispatch-complete state.");
                }
                sawDispatch = true;
                continue;
            }

            if (auditFrozen)
            {
                sawAwait = true;
                if (!sawDispatch ||
                    submittedChunkCount != plannedChunkCount ||
                    tail.AuditSubmittedChunkCount != submittedChunkCount ||
                    tail.AuditFirstSubmissionFrameSerial !=
                        firstSchedulerSerial ||
                    tail.AuditFinalSubmissionFrameSerial !=
                        finalSchedulerSerial ||
                    tail.AuditSolveFeedbackFrameSerial !=
                        solveFeedbackSerial ||
                    tail.AuditTriggerFeedbackFrameSerial !=
                        triggerFeedbackSerial ||
                    !HasSameFrozenDdgiAuditIdentity(
                        completed,
                        frozenGenerations,
                        frozenSolveEpoch,
                        frozenAuditEpoch,
                        frozenExpectedParticipantCount,
                        frozenExpectedTexelCount,
                        frozenPhysicalProbeCount,
                        frozenVolumeResourceGeneration,
                        frozenPublishedPropagationGeneration,
                        certificate: false))
                {
                    failures.Add(
                        $"DDGI transient window {windowIndex} route frame " +
                        $"{frame.RouteFrameIndex} has an await row that does " +
                        "not preserve the completed dispatch lifecycle.");
                }
                continue;
            }

            if (tail.Phase != SimpleDdgiTransportPhase.Certified)
            {
                if (sawDispatch)
                {
                    failures.Add(
                        $"DDGI transient window {windowIndex} route frame " +
                        $"{frame.RouteFrameIndex} resumed ordinary work after " +
                        "the frozen audit began; a fresh lifecycle is required.");
                }
                continue;
            }

            if (!sawDispatch || !sawAwait ||
                submittedChunkCount != plannedChunkCount ||
                tail.AuditPlannedChunkCount != plannedChunkCount ||
                tail.AuditSubmittedChunkCount != submittedChunkCount ||
                tail.AuditFirstSubmissionFrameSerial != firstSchedulerSerial ||
                tail.AuditFinalSubmissionFrameSerial != finalSchedulerSerial ||
                tail.AuditSolveFeedbackFrameSerial != solveFeedbackSerial ||
                tail.AuditTriggerFeedbackFrameSerial != triggerFeedbackSerial ||
                !HasSameFrozenDdgiAuditIdentity(
                    completed,
                    frozenGenerations,
                    frozenSolveEpoch,
                    frozenAuditEpoch,
                    frozenExpectedParticipantCount,
                    frozenExpectedTexelCount,
                    frozenPhysicalProbeCount,
                    frozenVolumeResourceGeneration,
                    frozenPublishedPropagationGeneration,
                    certificate: true))
            {
                failures.Add(
                    $"DDGI transient window {windowIndex} certificate does not " +
                    "close the exact observed dispatch/await lifecycle.");
            }
            certificateTail = tail;
            sawCertificate = true;
        }

        if (sawCertificate)
        {
            ValidateDdgiTransientAuditFeedbackProvenance(
                failures,
                windowIndex,
                sourceGeneration,
                frames,
                certificateTail);
        }
    }

    private static bool HasSameFrozenDdgiAuditIdentity(
        in SimpleDdgiCompletedFrameEvidence completed,
        in SimpleDdgiTransportGenerations generations,
        uint solveEpoch,
        uint auditEpoch,
        uint expectedParticipantCount,
        uint expectedTexelCount,
        int physicalProbeCount,
        uint volumeResourceGeneration,
        uint publishedPropagationGeneration,
        bool certificate)
    {
        SimpleDdgiTailCertificateFrameEvidence tail =
            completed.Submitted.TailCertificate;
        bool logicalVolumeMatches = certificate
            ? completed.Submitted.VolumeResourceGeneration != 0u &&
              (completed.Submitted.VolumeResourceGeneration ==
                   volumeResourceGeneration ||
               completed.Submitted.VolumeResourceGeneration ==
                   AdvanceNonZeroGeneration(volumeResourceGeneration))
            : volumeResourceGeneration != 0u &&
              completed.Submitted.VolumeResourceGeneration ==
                  volumeResourceGeneration;
        bool publicationMatches = certificate
            ? completed.Submitted.PublishedPropagationGeneration ==
                tail.Generations.CanonicalField
            : publishedPropagationGeneration != 0u &&
              completed.Submitted.PublishedPropagationGeneration ==
                publishedPropagationGeneration;
        return tail.Generations == generations &&
            tail.SolveEpoch == solveEpoch &&
            tail.AuditEpoch == auditEpoch &&
            tail.ExpectedParticipantCount == expectedParticipantCount &&
            tail.ExpectedTexelCount == expectedTexelCount &&
            completed.Submitted.AuditPhysicalProbeCount == physicalProbeCount &&
            logicalVolumeMatches &&
            completed.Submitted.TransportTopologyGeneration ==
                tail.Generations.VolumeTable &&
            completed.Submitted.TransportTopologyGeneration ==
                tail.Generations.PhysicalOwnership &&
            completed.Submitted.SourceLightingGeneration ==
                tail.Generations.SourceLighting &&
            completed.Submitted.TransportGeneration ==
                tail.Generations.CanonicalField &&
            publicationMatches &&
            completed.Submitted.QueueTransactionGeneration ==
                tail.Generations.Queue &&
            completed.Submitted.SchedulerResourceGeneration ==
                tail.Generations.SchedulerResources &&
            completed.Submitted.AdmittedSourceCohortGeneration ==
                tail.Generations.SourceLighting &&
            completed.Submitted.LivePropagationSourceGeneration ==
                tail.Generations.SourceLighting;
    }

    private static void ValidateDdgiTransientSourceOwnershipTransition(
        ICollection<string> failures,
        int windowIndex,
        uint sourceGeneration,
        uint expectedParticipantCount,
        int solveIndex,
        int triggerIndex,
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames)
    {
        string prefix = $"DDGI transient window {windowIndex}";
        bool sawSourceRepair = false;
        bool sawAcceleratedSolve = false;
        bool admittedCurrent = false;
        bool liveCurrent = false;
        int firstAcceleratedSolveIndex = -1;

        for (int index = 0; index <= triggerIndex; index++)
        {
            SimpleDdgiSubmittedFrameEvidence submitted =
                frames[index].Completed.Submitted;
            SimpleDdgiTransportPhase phase =
                submitted.TailCertificate.Phase;
            uint admitted = submitted.AdmittedSourceCohortGeneration;
            uint live = submitted.LivePropagationSourceGeneration;

            if (index == 0 &&
                (phase != SimpleDdgiTransportPhase.SourceRepair ||
                 admitted != 0u ||
                 live != 0u))
            {
                failures.Add(
                    $"{prefix} generation edge did not begin in SourceRepair " +
                    "with no admitted cohort or live propagation.");
            }

            if (phase == SimpleDdgiTransportPhase.SourceRepair)
            {
                SimpleDdgiCompletedFrameEvidence completed =
                    frames[index].Completed;
                if (sawAcceleratedSolve ||
                    admitted != 0u ||
                    live != 0u ||
                    completed.SchedulerSolveEpoch != 0u ||
                    completed.SchedulerSolveVisitedCount != 0u)
                {
                    failures.Add(
                        $"{prefix} route frame {frames[index].RouteFrameIndex} " +
                        "has an invalid delayed SourceRepair ownership/" +
                        "epoch state.");
                }
                sawSourceRepair = true;
                continue;
            }

            if (phase != SimpleDdgiTransportPhase.AcceleratedSolve)
            {
                failures.Add(
                    $"{prefix} route frame {frames[index].RouteFrameIndex} " +
                    $"has unexpected pre-audit phase {phase}.");
                continue;
            }

            if (!sawAcceleratedSolve && live != 0u)
            {
                failures.Add(
                    $"{prefix} first AcceleratedSolve submission claimed " +
                    "live propagation before delayed solve feedback.");
            }
            if (!sawAcceleratedSolve)
                firstAcceleratedSolveIndex = index;
            sawAcceleratedSolve = true;

            bool liveRequired = index >= solveIndex +
                RenderingConstants.FramesInFlight;
            if (admitted != sourceGeneration ||
                (liveRequired
                    ? live != sourceGeneration
                    : live != 0u && live != sourceGeneration))
            {
                failures.Add(
                    $"{prefix} route frame {frames[index].RouteFrameIndex} " +
                    "has an invalid admitted/live source generation during " +
                    "AcceleratedSolve.");
            }

            if (admittedCurrent && admitted != sourceGeneration)
            {
                failures.Add(
                    $"{prefix} admitted source cohort regressed after becoming current.");
            }
            admittedCurrent |= admitted == sourceGeneration;
            if (liveCurrent && live != sourceGeneration)
            {
                failures.Add(
                    $"{prefix} live propagation source regressed after becoming current.");
            }
            liveCurrent |= live == sourceGeneration;
        }

        if (!sawSourceRepair || !sawAcceleratedSolve ||
            frames[solveIndex].Completed.Submitted.TailCertificate.Phase !=
                SimpleDdgiTransportPhase.AcceleratedSolve)
        {
            failures.Add(
                $"{prefix} did not observe SourceRepair followed by an exact " +
                "AcceleratedSolve witness.");
        }

        int admissionFeedbackIndex = firstAcceleratedSolveIndex -
            RenderingConstants.FramesInFlight;
        if (admissionFeedbackIndex < 0 ||
            frames[admissionFeedbackIndex].Completed.Submitted.TailCertificate
                .Phase != SimpleDdgiTransportPhase.SourceRepair ||
            frames[admissionFeedbackIndex].Completed
                .SchedulerSolveParticipantCount != expectedParticipantCount ||
            frames[admissionFeedbackIndex].Completed
                .SchedulerBlockingTailSourceWorkCount != 0u)
        {
            failures.Add(
                $"{prefix} did not carry the exact delayed source-admission " +
                "feedback packet one FramesInFlight interval before " +
                "AcceleratedSolve.");
        }

        SimpleDdgiSubmittedFrameEvidence trigger =
            frames[triggerIndex].Completed.Submitted;
        if (trigger.AdmittedSourceCohortGeneration != sourceGeneration ||
            trigger.LivePropagationSourceGeneration != sourceGeneration)
        {
            failures.Add(
                $"{prefix} audit trigger did not retain the current admitted " +
                "cohort and live propagation generation.");
        }
    }

    private static void ValidateDdgiTransientAuditFeedbackProvenance(
        ICollection<string> failures,
        int windowIndex,
        uint sourceGeneration,
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames,
        in SimpleDdgiTailCertificateFrameEvidence tail)
    {
        int solveIndex = FindDdgiFeedbackFrame(
            frames,
            tail.AuditSolveFeedbackFrameSerial);
        int triggerIndex = FindDdgiFeedbackFrame(
            frames,
            tail.AuditTriggerFeedbackFrameSerial);
        int firstAuditIndex = -1;
        for (int index = 0; index < frames.Count; index++)
        {
            SimpleDdgiCompletedFrameEvidence completed = frames[index].Completed;
            if (completed.Submitted.TailCertificate.Phase ==
                    SimpleDdgiTransportPhase.AuditFrozen &&
                (completed.Submitted.IntendedGpuPasses &
                 SimpleDdgiGpuPassMask.TransportAudit) != 0)
            {
                firstAuditIndex = index;
                break;
            }
        }

        string prefix = $"DDGI transient window {windowIndex}";
        if (solveIndex < 0)
        {
            failures.Add(
                $"{prefix} is missing the exact earlier solve-feedback " +
                $"packet {tail.AuditSolveFeedbackFrameSerial}.");
        }
        if (triggerIndex < 0)
        {
            failures.Add(
                $"{prefix} is missing the exact earlier audit-trigger " +
                $"feedback packet {tail.AuditTriggerFeedbackFrameSerial}.");
        }
        if (solveIndex < 0 || triggerIndex < 0 || firstAuditIndex < 0)
            return;

        ValidateDdgiTransientSourceOwnershipTransition(
            failures,
            windowIndex,
            sourceGeneration,
            tail.ExpectedParticipantCount,
            solveIndex,
            triggerIndex,
            frames);

        if (solveIndex >= triggerIndex || triggerIndex >= firstAuditIndex)
        {
            failures.Add(
                $"{prefix} feedback lifecycle is not ordered solve, " +
                "epoch-zero trigger, then first audit submission.");
        }

        ulong solveSerial =
            frames[solveIndex].Completed.Submitted.SchedulerFrameSerial;
        ulong triggerSerial =
            frames[triggerIndex].Completed.Submitted.SchedulerFrameSerial;
        ulong firstAuditSerial =
            frames[firstAuditIndex].Completed.Submitted.SchedulerFrameSerial;
        ulong completionDelay = (ulong)RenderingConstants.FramesInFlight;
        if (triggerIndex - solveIndex < RenderingConstants.FramesInFlight ||
            solveSerial > ulong.MaxValue - completionDelay ||
            triggerSerial < solveSerial + completionDelay)
        {
            failures.Add(
                $"{prefix} audit trigger does not postdate solve feedback by " +
                "the required FramesInFlight completion delay.");
        }
        if (firstAuditIndex - triggerIndex !=
                RenderingConstants.FramesInFlight ||
            triggerSerial > ulong.MaxValue - completionDelay ||
            firstAuditSerial != triggerSerial + completionDelay)
        {
            failures.Add(
                $"{prefix} first audit submission is not exactly one " +
                "FramesInFlight delay after its trigger feedback packet.");
        }

        SimpleDdgiCompletedFrameEvidence solve = frames[solveIndex].Completed;
        SimpleDdgiCompletedFrameEvidence trigger =
            frames[triggerIndex].Completed;
        // Replay the manager's delayed-feedback admission rule. A packet can
        // name the current generation or its one-step predecessor; only work
        // published by the current generation advances the host generation.
        uint replayedTransportGeneration =
            frames[0].Completed.Submitted.TransportGeneration;
        if (replayedTransportGeneration == 0u)
        {
            failures.Add(
                $"{prefix} generation replay has an invalid zero origin.");
        }
        for (int index = 0; index <= triggerIndex; index++)
        {
            SimpleDdgiCompletedFrameEvidence replay = frames[index].Completed;
            if (!IsExactOrdinaryFeedbackIdentity(replay, tail))
            {
                failures.Add(
                    $"{prefix} route frame {frames[index].RouteFrameIndex} " +
                    "is not an exact ordinary feedback identity for replay.");
                continue;
            }

            uint feedbackGeneration =
                replay.SchedulerFeedbackTransportGeneration;
            if (feedbackGeneration == 0u || replayedTransportGeneration == 0u)
            {
                failures.Add(
                    $"{prefix} route frame {frames[index].RouteFrameIndex} " +
                    "retained a zero transport generation during replay.");
                continue;
            }

            bool currentGeneration =
                feedbackGeneration == replayedTransportGeneration;
            bool immediatePredecessor = AdvanceNonZeroGeneration(
                feedbackGeneration) == replayedTransportGeneration;
            if (!currentGeneration && !immediatePredecessor)
            {
                failures.Add(
                    $"{prefix} route frame {frames[index].RouteFrameIndex} " +
                    $"feedback generation {feedbackGeneration} is neither " +
                    $"current {replayedTransportGeneration} nor its exact " +
                    "wrap-safe predecessor.");
                continue;
            }

            if (currentGeneration &&
                replay.SchedulerPublishedWorkCount != 0u &&
                replay.SchedulerActiveCanonicalMutationCount != 0u)
            {
                replayedTransportGeneration = AdvanceNonZeroGeneration(
                    replayedTransportGeneration);
            }
        }

        // The packet after the trigger can already be in flight when BeginFrame
        // freezes the audit. Any work in that packet invalidates the frozen
        // pre-work certificate, even though its own completion is observed later.
        for (int index = triggerIndex + 1; index < firstAuditIndex; index++)
        {
            SimpleDdgiCompletedFrameEvidence postTrigger =
                frames[index].Completed;
            if (!IsExactOrdinaryFeedbackIdentity(postTrigger, tail) ||
                postTrigger.Submitted.TailCertificate.Phase !=
                    SimpleDdgiTransportPhase.AcceleratedSolve ||
                postTrigger.SchedulerFeedbackTransportGeneration !=
                    tail.Generations.CanonicalField ||
                postTrigger.SchedulerSolveEpoch != 0u ||
                postTrigger.SchedulerSolveParticipantCount !=
                    tail.ExpectedParticipantCount ||
                postTrigger.SchedulerSolveVisitedCount != 0u ||
                postTrigger.SchedulerActiveCanonicalMutationCount != 0u ||
                postTrigger.SchedulerActiveSourceMutationCount != 0u ||
                postTrigger.SchedulerBlockingTailSourceWorkCount != 0u)
            {
                failures.Add(
                    $"{prefix} route frame {frames[index].RouteFrameIndex} " +
                    "post-trigger in-flight feedback was not quiescent at " +
                    "the frozen canonical generation; a fresh audit " +
                    "lifecycle is required.");
            }
        }

        if (!IsExactOrdinaryFeedbackIdentity(solve, tail) ||
            solve.Submitted.TailCertificate.Phase !=
                SimpleDdgiTransportPhase.AcceleratedSolve ||
            solve.SchedulerSolveEpoch != tail.SolveEpoch ||
            solve.SchedulerSolveParticipantCount !=
                tail.ExpectedParticipantCount ||
            solve.SchedulerSolveVisitedCount !=
                tail.ExpectedParticipantCount ||
            solve.SchedulerPublishedWorkCount == 0u ||
            solve.SchedulerActiveCanonicalMutationCount == 0u ||
            solve.SchedulerActiveSourceMutationCount != 0u ||
            solve.SchedulerBlockingTailSourceWorkCount != 0u ||
            solve.SchedulerFeedbackTransportGeneration ==
                tail.Generations.CanonicalField)
        {
            failures.Add(
                $"{prefix} solve-feedback packet is not the exact complete " +
                "solve epoch/population that armed the drain.");
        }

        if (!IsExactOrdinaryFeedbackIdentity(trigger, tail) ||
            trigger.Submitted.TailCertificate.Phase !=
                SimpleDdgiTransportPhase.AcceleratedSolve ||
            trigger.SchedulerSolveEpoch != 0u ||
            trigger.SchedulerSolveParticipantCount !=
                tail.ExpectedParticipantCount ||
            trigger.SchedulerSolveVisitedCount != 0u ||
            trigger.SchedulerActiveCanonicalMutationCount != 0u ||
            trigger.SchedulerActiveSourceMutationCount != 0u ||
            trigger.SchedulerBlockingTailSourceWorkCount != 0u ||
            trigger.SchedulerFeedbackTransportGeneration !=
                replayedTransportGeneration ||
            replayedTransportGeneration != tail.Generations.CanonicalField)
        {
            failures.Add(
                $"{prefix} audit-trigger feedback packet is not the exact " +
                "epoch-zero/quiescent drain completion.");
        }
    }

    private static int FindDdgiFeedbackFrame(
        IReadOnlyList<SampleBenchmarkDdgiTransientFrame> frames,
        ulong schedulerFeedbackFrameSerial)
    {
        int result = -1;
        for (int index = 0; index < frames.Count; index++)
        {
            if (frames[index].Completed.SchedulerFeedbackFrameSerial !=
                schedulerFeedbackFrameSerial)
            {
                continue;
            }

            if (result >= 0)
                return -1;
            result = index;
        }
        return result;
    }

    private static bool IsExactOrdinaryFeedbackIdentity(
        in SimpleDdgiCompletedFrameEvidence completed,
        in SimpleDdgiTailCertificateFrameEvidence tail) =>
        (completed.Submitted.TailCertificate.Phase is
            SimpleDdgiTransportPhase.SourceRepair or
            SimpleDdgiTransportPhase.AcceleratedSolve) &&
        completed.SchedulerFeedbackAvailable &&
        completed.SchedulerFeedbackFrameAligned &&
        completed.SchedulerFeedbackGenerationAligned &&
        completed.SchedulerFeedbackStatusFlags == 0u &&
        completed.SchedulerFeedbackFrameSerial ==
            completed.Submitted.SchedulerFrameSerial &&
        completed.SchedulerFeedbackVolumeResourceGeneration ==
            completed.Submitted.VolumeResourceGeneration &&
        completed.SchedulerFeedbackTransportTopologyGeneration ==
            completed.Submitted.TransportTopologyGeneration &&
        completed.SchedulerFeedbackSourceLightingGeneration ==
            completed.Submitted.SourceLightingGeneration &&
        completed.SchedulerFeedbackQueueTransactionGeneration ==
            completed.Submitted.QueueTransactionGeneration &&
        completed.SchedulerFeedbackSchedulerResourceGeneration ==
            completed.Submitted.SchedulerResourceGeneration &&
        completed.SchedulerFeedbackTransportTopologyGeneration ==
            tail.Generations.VolumeTable &&
        completed.SchedulerFeedbackTransportTopologyGeneration ==
            tail.Generations.PhysicalOwnership &&
        completed.SchedulerFeedbackSourceLightingGeneration ==
            tail.Generations.SourceLighting &&
        completed.SchedulerFeedbackQueueTransactionGeneration ==
            tail.Generations.Queue &&
        completed.SchedulerFeedbackSchedulerResourceGeneration ==
            tail.Generations.SchedulerResources;

    private static bool IsSameOrNextNonZeroGeneration(
        uint current,
        uint candidate) =>
        current != 0u &&
        candidate != 0u &&
        (candidate == current ||
         candidate == AdvanceNonZeroGeneration(current));

    private static bool HasAnySchedulerFeedbackPayload(
        in SimpleDdgiCompletedFrameEvidence completed) =>
        completed.SchedulerFeedbackAvailable ||
        completed.SchedulerFeedbackFrameAligned ||
        completed.SchedulerFeedbackGenerationAligned ||
        completed.SchedulerFeedbackFrameSerial != 0UL ||
        completed.SchedulerFeedbackVolumeResourceGeneration != 0u ||
        completed.SchedulerFeedbackTransportTopologyGeneration != 0u ||
        completed.SchedulerFeedbackSchedulerResourceGeneration != 0u ||
        completed.SchedulerFeedbackQueueTransactionGeneration != 0u ||
        completed.SchedulerFeedbackSourceLightingGeneration != 0u ||
        completed.SchedulerFeedbackTransportGeneration != 0u ||
        completed.SchedulerFeedbackStatusFlags != 0u ||
        completed.SchedulerConsideredCandidateCount != 0u ||
        completed.SchedulerCompactedCandidateCount != 0u ||
        completed.SchedulerAcceptedWorkCount != 0u ||
        completed.SchedulerCommittedWorkCount != 0u ||
        completed.SchedulerPublishedWorkCount != 0u ||
        completed.SchedulerActiveWorkCount != 0u ||
        completed.SchedulerSourceParticipantCount != 0u ||
        completed.SchedulerHardSourceParticipantCount != 0u ||
        completed.SchedulerRoutineSourceParticipantCount != 0u ||
        completed.SchedulerCachedParticipantCount != 0u ||
        completed.SchedulerSolveParticipantCount != 0u ||
        completed.SchedulerSolveVisitedCount != 0u ||
        completed.SchedulerSolveEpoch != 0u ||
        completed.SchedulerActiveCanonicalMutationCount != 0u ||
        completed.SchedulerActiveSourceMutationCount != 0u ||
        completed.SchedulerBlockingTailSourceWorkCount != 0u ||
        completed.SchedulerPrimaryRayCount != 0u ||
        completed.SchedulerSourceRayCount != 0u ||
        completed.SchedulerTransportRayCount != 0u ||
        completed.SchedulerCachedRayCount != 0u;

    private static uint AdvanceNonZeroGeneration(uint generation)
    {
        uint next = unchecked(generation + 1u);
        return next == 0u ? 1u : next;
    }

    private static SampleBenchmarkDdgiTransientEvidence
        CreateUnavailableDdgiTransientEvidence(IReadOnlyList<string> failures)
    {
        string[] distinct = failures
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new SampleBenchmarkDdgiTransientEvidence(
            Applicable: true,
            Available: false,
            Array.AsReadOnly(distinct),
            Array.Empty<SampleBenchmarkDdgiTransientWindow>());
    }

    private static SampleBenchmarkCaptureContract
        ApplyDdgiTransientEvidenceContract(
            SampleBenchmarkCaptureContract contract,
            SampleBenchmarkDdgiTransientEvidence evidence)
    {
        if (!evidence.Applicable || evidence.Available)
            return contract;

        string[] mismatches = contract.Mismatches
            .Concat(evidence.Failures.Select(static failure =>
                "DDGI transient evidence unavailable: " + failure))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return contract with
        {
            Comparable = false,
            Mismatches = Array.AsReadOnly(mismatches)
        };
    }

    private IReadOnlyList<SampleBenchmarkTimingStats> BuildTimingStats(
        IReadOnlyList<TimingSelector> selectors,
        bool requireGpuTiming)
    {
        return selectors
            .Select(selector =>
            {
                bool simpleDdgiTiming = selector.Name.StartsWith(
                    "SimpleDdgi",
                    StringComparison.Ordinal);
                double[] samples = _samples
                    .Where(d => (!requireGpuTiming || d.GpuTimingValid != 0) &&
                        (!simpleDdgiTiming || d.SimpleDdgiActive != 0))
                    .Select(d => MicrosecondsToMilliseconds(
                        selector.GetMicroseconds(d)))
                    .ToArray();
                // A selected pass/stage owns the entire measurement window.
                // Preserve zero-duration timestamp quantization samples so its
                // percentile count remains exactly 120; omit only selectors that
                // were wholly inactive for the scenario.
                return simpleDdgiTiming || samples.Any(static value => value > 0.0)
                    ? BuildStats(selector.Name, samples)
                    : SampleBenchmarkTimingStats.Empty(selector.Name);
            })
            .Where(stats => stats.Count > 0)
            .OrderByDescending(stats => stats.P95Milliseconds)
            .ThenByDescending(stats => stats.AverageMilliseconds)
            .ToArray();
    }

    private SampleBenchmarkAutomaticPlanarEvidence
        BuildAutomaticPlanarEvidence()
    {
        SampleBenchmarkAutomaticPlanarFrame[] frames = _samples
            .Select((sample, index) => new
            {
                Sample = sample,
                Index = index,
                Lifecycle = sample.AutomaticPlanarCompletedLifecycle
            })
            .Where(static item =>
                item.Sample.GpuTimingValid != 0 &&
                item.Lifecycle.Valid &&
                item.Lifecycle.GpuTimingRecorded)
            .Select(static item =>
                new SampleBenchmarkAutomaticPlanarFrame(
                    item.Index,
                    item.Lifecycle,
                    Math.Max(
                        0L,
                        item.Sample
                            .GpuAutomaticPlanarCaptureMicroseconds)))
            .ToArray();
        if (frames.Length == 0)
            return SampleBenchmarkAutomaticPlanarEvidence.Unavailable;

        SampleBenchmarkAutomaticPlanarFrame[] captureFrames = frames
            .Where(static frame =>
                frame.CompletedLifecycle.CaptureCount > 0)
            .ToArray();
        SampleBenchmarkAutomaticPlanarFrame[] reprojectionFrames = frames
            .Where(static frame =>
                frame.CompletedLifecycle.CaptureCount == 0 &&
                frame.CompletedLifecycle.ReprojectionCount > 0)
            .ToArray();
        SampleBenchmarkAutomaticPlanarFrame[] noWorkFrames = frames
            .Where(static frame =>
                frame.CompletedLifecycle.CaptureCount == 0 &&
                frame.CompletedLifecycle.ReprojectionCount == 0)
            .ToArray();

        return new SampleBenchmarkAutomaticPlanarEvidence(
            Available: true,
            CompletedFrameCount: frames.Length,
            CaptureFrameCount: captureFrames.Length,
            ReprojectionFrameCount: reprojectionFrames.Length,
            NoWorkFrameCount: noWorkFrames.Length,
            CaptureFrameMilliseconds: BuildStats(
                "Automatic planar capture frames",
                captureFrames.Select(static frame =>
                    frame.GpuPassMicroseconds / 1000.0)),
            ReprojectionFrameMilliseconds: BuildStats(
                "Automatic planar reprojection frames",
                reprojectionFrames.Select(static frame =>
                    frame.GpuPassMicroseconds / 1000.0)),
            NoWorkFrameMilliseconds: BuildStats(
                "Automatic planar no-work frames",
                noWorkFrames.Select(static frame =>
                    frame.GpuPassMicroseconds / 1000.0)),
            Frames: Array.AsReadOnly(frames));
    }

    private static IReadOnlyList<SampleBenchmarkFinding> BuildFindings(
        SampleBenchmarkTimingStats cpuFrame,
        SampleBenchmarkTimingStats gpuFrame,
        IReadOnlyList<SampleBenchmarkTimingStats> gpuPasses,
        IReadOnlyList<SampleBenchmarkTimingStats> cpuStages,
        IReadOnlyList<BudgetMetric> budgetMetrics)
    {
        var findings = new List<SampleBenchmarkFinding>();
        SampleBenchmarkTimingStats? topGpu = gpuPasses.FirstOrDefault();
        SampleBenchmarkTimingStats? topCpu = cpuStages.FirstOrDefault(stage => stage.Name != "DrawSceneTotal");

        if (gpuFrame.Count > 0 && gpuFrame.P95Milliseconds >= cpuFrame.P95Milliseconds && topGpu != null)
        {
            findings.Add(new SampleBenchmarkFinding(
                "likely-bound",
                topGpu.Name,
                $"GPU dominated this sample set; pass p95={topGpu.P95Milliseconds:F3}ms avg={topGpu.AverageMilliseconds:F3}ms."));
        }
        else if (topCpu != null)
        {
            findings.Add(new SampleBenchmarkFinding(
                "likely-bound",
                topCpu.Name,
                $"CPU dominated this sample set; stage p95={topCpu.P95Milliseconds:F3}ms avg={topCpu.AverageMilliseconds:F3}ms."));
        }

        foreach (BudgetMetric metric in budgetMetrics.Where(
                     static metric => metric.Status is
                         RenderBudgetStatus.OverBudget or RenderBudgetStatus.Warning))
        {
            findings.Add(new SampleBenchmarkFinding(
                "budget",
                metric.Name,
                $"{metric.Status}: {metric.Value:F3} {metric.Unit}, budget={metric.FailureThreshold:F3} {metric.Unit}."));
        }

        if (gpuFrame.Count == 0)
        {
            findings.Add(new SampleBenchmarkFinding(
                "gpu-timing",
                "GPU frame",
                "No valid GPU timestamp samples were captured; CPU timings and counters are still reported."));
        }

        return findings;
    }

    private void AccumulateWorstBudgetMetrics(IReadOnlyList<BudgetMetric> metrics)
    {
        foreach (BudgetMetric metric in metrics)
        {
            string key = metric.Name + "\u001f" + metric.Unit;
            if (!_worstBudgetMetrics.TryGetValue(key, out BudgetMetric? current) ||
                IsWorse(metric, current))
            {
                _worstBudgetMetrics[key] = metric;
            }
        }
    }

    private static bool IsWorse(BudgetMetric candidate, BudgetMetric current)
    {
        int candidateRank = GetBudgetStatusRank(candidate.Status);
        int currentRank = GetBudgetStatusRank(current.Status);
        if (candidateRank != currentRank)
            return candidateRank > currentRank;

        return GetBudgetPressure(candidate) > GetBudgetPressure(current);
    }

    private static int GetBudgetStatusRank(RenderBudgetStatus status)
    {
        if (!Enum.IsDefined(status))
            return 7;

        // Availability is a coverage contract, not a benign low-pressure sample.
        // Retain it when any measurement frame loses a metric so the release gate
        // can fail closed for metrics required by the measured scenario.
        return status switch
        {
            RenderBudgetStatus.Unknown => 6,
            RenderBudgetStatus.Unavailable => 5,
            RenderBudgetStatus.OverBudget => 4,
            RenderBudgetStatus.Warning => 3,
            RenderBudgetStatus.WithinBudget => 2,
            _ => 0
        };
    }

    private static double GetBudgetPressure(BudgetMetric metric)
    {
        if (double.IsFinite(metric.FailureThreshold) && metric.FailureThreshold > 0.0)
            return metric.Value / metric.FailureThreshold;
        return metric.Value;
    }

    private MaterialWindowTiming ApplyMeasurementWindowTimingMetrics(
        BudgetMetric[] metrics,
        SampleBenchmarkTimingStats cpuFrame,
        SampleBenchmarkTimingStats gpuFrame)
    {
        ReplaceTimingMetric(metrics, "CPU renderer", cpuFrame);
        ReplaceTimingMetric(
            metrics,
            "GPU frame",
            gpuFrame,
            gpuFrame.Count == _samples.Count && _samples.Count > 0);

        RendererDiagnostics[] giSamples = _samples
            .Where(static sample => sample.GlobalIlluminationEnabled != 0)
            .ToArray();
        SampleBenchmarkTimingStats giCpu = BuildStats(
            "GI CPU scheduling and upload",
            giSamples.Select(static sample =>
                MicrosecondsToMilliseconds(
                    sample.CpuGlobalIlluminationRecordMicroseconds)));
        bool giCpuAvailable = giSamples.Length > 0 &&
            giSamples.All(static sample =>
                sample.GlobalIlluminationCpuTimingSampleCount > 0);
        ReplaceTimingMetric(
            metrics,
            "GI CPU scheduling and upload",
            giCpu,
            giCpuAvailable);

        SampleBenchmarkTimingStats giGpu = BuildStats(
            "GI GPU",
            giSamples.Select(sample =>
                _tailDdgiTimingProjection
                    ? ResolveTailDdgiGpuMilliseconds(sample)
                    : ResolveGlobalIlluminationGpuMilliseconds(sample)));
        bool giGpuAvailable = giSamples.Length > 0 &&
            giGpu.Count == giSamples.Length;
        ReplaceTimingMetric(metrics, "GI GPU", giGpu, giGpuAvailable);

        SampleBenchmarkTimingStats giForwardIncremental = BuildStats(
            "GI forward gather incremental",
            giSamples.Select(static sample =>
                HasForwardGiIncrementalTiming(sample)
                    ? MicrosecondsToMilliseconds(
                        sample.GpuForwardGiIncrementalMicroseconds)
                    : double.NaN));
        bool forwardRequired = giSamples.Any(static sample =>
            sample.GlobalIlluminationDdgiActive != 0 ||
            sample.SimpleDdgiActive != 0);
        bool giForwardIncrementalAvailable = forwardRequired &&
            giForwardIncremental.Count == giSamples.Length;
        ReplaceTimingMetric(
            metrics,
            "GI forward gather incremental",
            giForwardIncremental,
            giForwardIncrementalAvailable);

        MaterialWindowTiming materialTiming = BuildMaterialWindowTiming();
        ReplaceTimingMetric(
            metrics,
            RenderBudgetEvaluator.MaterialGiCompileP95MetricName,
            materialTiming.Compile,
            materialTiming.CompileExact && materialTiming.Compile.Count > 0);
        ReplaceTimingMetric(
            metrics,
            RenderBudgetEvaluator.MaterialGiUploadP95MetricName,
            materialTiming.Upload,
            materialTiming.UploadExact && materialTiming.Upload.Count > 0);
        ReplaceTimingMetric(
            metrics,
            RenderBudgetEvaluator.MaterialGiPipelineP95MetricName,
            materialTiming.Pipeline,
            materialTiming.CompileExact &&
                materialTiming.UploadExact &&
                materialTiming.Pipeline.Count > 0);
        return materialTiming;
    }

    private MaterialWindowTiming BuildMaterialWindowTiming()
    {
        var compile = new List<double>();
        var upload = new List<double>();
        var pipeline = new List<double>();
        RendererDiagnostics baseline = _measurementBaseline ??
            (_samples.Count > 0 ? _samples[0] : RendererDiagnostics.Empty);
        int previousCompileCount = baseline.MaterialCompileTimingSampleCount;
        int previousUploadCount = baseline.MaterialUploadTimingSampleCount;
        bool compileExact = true;
        bool uploadExact = true;

        foreach (RendererDiagnostics sample in _samples)
        {
            int compileDelta = sample.MaterialCompileTimingSampleCount -
                previousCompileCount;
            int uploadDelta = sample.MaterialUploadTimingSampleCount -
                previousUploadCount;
            if (compileDelta is < 0 or > 1)
                compileExact = false;
            if (uploadDelta is < 0 or > 1)
                uploadExact = false;

            double compileMilliseconds = 0.0;
            double uploadMilliseconds = 0.0;
            if (compileDelta == 1)
            {
                compileMilliseconds = MicrosecondsToMilliseconds(
                    sample.MaterialLastCompileMicroseconds);
                compile.Add(compileMilliseconds);
            }
            if (uploadDelta == 1)
            {
                uploadMilliseconds = MicrosecondsToMilliseconds(
                    sample.MaterialLastUploadMicroseconds);
                upload.Add(uploadMilliseconds);
            }
            if (compileDelta == 1 || uploadDelta == 1)
                pipeline.Add(compileMilliseconds + uploadMilliseconds);

            previousCompileCount = sample.MaterialCompileTimingSampleCount;
            previousUploadCount = sample.MaterialUploadTimingSampleCount;
        }

        return new MaterialWindowTiming(
            BuildStats("Material GI compile P95", compile),
            BuildStats("Material GI upload P95", upload),
            BuildStats("Material GI compile/upload P95", pipeline),
            compileExact,
            uploadExact);
    }

    private static double ResolveGlobalIlluminationGpuMilliseconds(
        RendererDiagnostics diagnostics)
    {
        bool forwardRequired = diagnostics.GlobalIlluminationDdgiActive != 0 ||
            diagnostics.SimpleDdgiActive != 0;
        bool hasForwardTiming = HasForwardGiIncrementalTiming(diagnostics);
        if (diagnostics.GpuTimingValid == 0 ||
            (forwardRequired && !hasForwardTiming))
        {
            return double.NaN;
        }

        long microseconds = diagnostics.GpuDdgiUpdateMicroseconds +
            diagnostics.GpuGiCompositeMicroseconds +
            diagnostics.GpuFarFieldUpdateMicroseconds +
            diagnostics.GpuAccelerationStructureBlasMicroseconds +
            diagnostics.GpuAccelerationStructureTlasMicroseconds +
            (hasForwardTiming
                ? diagnostics.GpuForwardGiIncrementalMicroseconds
                : 0);
        return MicrosecondsToMilliseconds(microseconds);
    }

    private static double ResolveTailDdgiGpuMilliseconds(
        RendererDiagnostics diagnostics) =>
        diagnostics.GpuTimingValid != 0 &&
        diagnostics.SimpleDdgiActive != 0 &&
        diagnostics.SimpleDdgiTransportV2Active != 0 &&
        diagnostics.SimpleDdgiTransportTailCertificationEnabled
            ? MicrosecondsToMilliseconds(
                diagnostics.GpuDdgiUpdateMicroseconds)
            : double.NaN;

    private static bool HasForwardGiIncrementalTiming(
        RendererDiagnostics diagnostics) =>
        diagnostics.GpuForwardGiIncrementalAttribution is
            GiTimingAttribution.Exclusive or GiTimingAttribution.PairedEstimate;

    private static void ReplaceTimingMetric(
        BudgetMetric[] metrics,
        string name,
        SampleBenchmarkTimingStats stats,
        bool available = true)
    {
        int index = Array.FindIndex(
            metrics,
            metric => string.Equals(metric.Name, name, StringComparison.Ordinal));
        if (index < 0)
            return;

        BudgetMetric template = metrics[index];
        // Coverage loss in any measured frame is itself evidence. Exact P95
        // replacement must not turn an Unknown/Unavailable timing stream into
        // an apparently valid one merely because other frames had timestamps.
        if (template.Status is RenderBudgetStatus.Unknown or
            RenderBudgetStatus.Unavailable)
        {
            return;
        }
        double value = available && stats.Count > 0
            ? stats.P95Milliseconds
            : 0.0;
        metrics[index] = template with
        {
            Value = value,
            Status = available && stats.Count > 0
                ? RenderBudgetEvaluator.Classify(value, template.FailureThreshold)
                : RenderBudgetStatus.Unavailable
        };
    }

    private sealed record MaterialWindowTiming(
        SampleBenchmarkTimingStats Compile,
        SampleBenchmarkTimingStats Upload,
        SampleBenchmarkTimingStats Pipeline,
        bool CompileExact,
        bool UploadExact);

    internal static SampleBenchmarkTimingStats BuildStats(string name, IEnumerable<double> values)
    {
        double[] samples = values.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        if (samples.Length == 0)
            return new SampleBenchmarkTimingStats(name, 0, 0, 0, 0, 0);

        Array.Sort(samples);
        double sum = samples.Sum();
        double average = Math.Clamp(sum / samples.Length, samples[0], samples[^1]);
        int p95Index = PercentileIndex(samples.Length, 0.95);
        int p99Index = PercentileIndex(samples.Length, 0.99);
        double median = samples.Length % 2 == 0
            ? (samples[samples.Length / 2 - 1] + samples[samples.Length / 2]) * 0.5
            : samples[samples.Length / 2];
        return new SampleBenchmarkTimingStats(
            name,
            samples.Length,
            average,
            samples[0],
            samples[^1],
            samples[p95Index])
        {
            MedianMilliseconds = median,
            P50Milliseconds = median,
            P99Milliseconds = samples[p99Index]
        };
    }

    private static int PercentileIndex(int sampleCount, double percentile) =>
        Math.Min(sampleCount - 1, (int)Math.Ceiling(sampleCount * percentile) - 1);

    private SampleDdgiSchedulerRefreshEvidence BuildSimpleDdgiSchedulerRefreshEvidence()
    {
        RendererDiagnostics[] samples = _samples
            .Where(static sample => sample.SimpleDdgiActive != 0)
            .ToArray();
        if (samples.Length == 0)
            return SampleDdgiSchedulerRefreshEvidence.Empty;

        SampleDdgiSchedulerSlowFrame[] slowest = samples
            .Select((sample, index) => new SampleDdgiSchedulerSlowFrame(
                index,
                sample.SimpleDdgiUploadTiming.SchedulerRefreshMicroseconds,
                sample.SimpleDdgiUploadTiming.SchedulerEntryRefreshCount,
                sample.SimpleDdgiUploadTiming.SchedulerWakeEntryRefreshCount,
                sample.SimpleDdgiUploadTiming.SchedulerWakeRefreshBudget,
                sample.SimpleDdgiUploadTiming.SchedulerWakeBudgetSaturated,
                sample.SimpleDdgiUploadTiming.SchedulerFullRebuildCount,
                sample.SimpleDdgiUploadTiming.VisibilityEntryRefreshCount,
                sample.SimpleDdgiUploadTiming.ReadbackProbeCount,
                sample.DdgiProbesUpdated,
                sample.SimpleDdgiTransportSourceReadyProbeCount,
                sample.SimpleDdgiTransportConvergedProbeCount,
                sample.SimpleDdgiTransportGlobalConvergencePending)
            {
                RoutineSourceRepairProbeCount =
                    sample.SimpleDdgiTransportConvergence.RoutineSourceRepairProbeCount,
                RoutineMaintenancePendingProbeCount =
                    sample.SimpleDdgiTransportConvergence.RoutineMaintenancePendingProbeCount
            })
            .OrderByDescending(static sample => sample.SchedulerRefreshMicroseconds)
            .ThenBy(static sample => sample.MeasurementSampleIndex)
            .Take(8)
            .ToArray();

        return new SampleDdgiSchedulerRefreshEvidence(
            BuildIntegerStats(
                "Scheduler entries refreshed",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.SchedulerEntryRefreshCount)),
            BuildIntegerStats(
                "Scheduler wake entries refreshed",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.SchedulerWakeEntryRefreshCount)),
            BuildIntegerStats(
                "Visibility entries refreshed",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.VisibilityEntryRefreshCount)),
            BuildIntegerStats(
                "Probe readback entries",
                samples.Select(static sample =>
                    sample.SimpleDdgiUploadTiming.ReadbackProbeCount)),
            samples.Count(static sample =>
                sample.SimpleDdgiUploadTiming.SchedulerWakeBudgetSaturated != 0),
            samples.Count(static sample =>
                sample.SimpleDdgiUploadTiming.SchedulerFullRebuildCount != 0),
            slowest);
    }

    private SampleBenchmarkCpuSpikeEvidence BuildCpuSpikeEvidence()
    {
        RendererDiagnostics[] rebuilt = _samples
            .Where(static sample => sample.ScenePayloadRebuilt != 0)
            .ToArray();
        RendererDiagnostics[] stable = _samples
            .Where(static sample => sample.ScenePayloadRebuilt == 0)
            .ToArray();
        SampleBenchmarkCpuSlowFrame[] slowest = _samples
            .Select(static (sample, index) => CreateCpuSlowFrame(index, sample))
            .OrderByDescending(static frame => frame.CpuTotalDrawSceneMicroseconds)
            .ThenBy(static frame => frame.MeasurementSampleIndex)
            .Take(SampleBenchmarkCpuSpikeEvidence.SlowFrameLimit)
            .ToArray();

        return new SampleBenchmarkCpuSpikeEvidence(
            BuildCpuCohort("Rebuilt", rebuilt),
            BuildCpuCohort("Stable", stable),
            slowest);
    }

    private static SampleBenchmarkCpuCohortEvidence BuildCpuCohort(
        string name,
        IReadOnlyList<RendererDiagnostics> samples)
    {
        string prefix = $"{name} CPU";
        return new SampleBenchmarkCpuCohortEvidence(
            name,
            samples.Count,
            samples.Count(static sample => sample.ScenePayloadRebuilt != 0),
            samples.Count(static sample =>
                sample.CameraDrivenCpuDrawListRebuilt != 0),
            BuildStats(
                $"{prefix} total draw scene",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuTotalDrawSceneMicroseconds))),
            BuildStats(
                $"{prefix} scene build",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuSceneBuildMicroseconds))),
            BuildStats(
                $"{prefix} payload signature",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuPayloadSignatureMicroseconds))),
            BuildStats(
                $"{prefix} object cull",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuObjectCullMicroseconds))),
            BuildStats(
                $"{prefix} meshlet cull",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuMeshletCullMicroseconds))),
            BuildStats(
                $"{prefix} static batch build",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuStaticBatchBuildMicroseconds))),
            BuildStats(
                $"{prefix} upload",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuUploadMicroseconds))),
            BuildStats(
                $"{prefix} material upload",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuMaterialUploadMicroseconds))),
            BuildStats(
                $"{prefix} acceleration structure build",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuAccelerationStructureBuildMicroseconds))),
            BuildStats(
                $"{prefix} primary command record",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuPrimaryCommandRecordMicroseconds))),
            BuildStats(
                $"{prefix} secondary command record",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuSecondaryCommandRecordMicroseconds))),
            BuildStats(
                $"{prefix} frame-fence wait",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuWaitForFrameFenceMicroseconds))),
            BuildStats(
                $"{prefix} swapchain-image owner wait",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuSwapchainImageOwnerWaitMicroseconds))),
            BuildStats(
                $"{prefix} frame-resource recycle wait",
                samples.Select(static sample => MicrosecondsToMilliseconds(
                    sample.CpuFrameResourceRecycleWaitMicroseconds))));
    }

    private static SampleBenchmarkCpuSlowFrame CreateCpuSlowFrame(
        int measurementSampleIndex,
        RendererDiagnostics sample) => new()
    {
        MeasurementSampleIndex = measurementSampleIndex,
        CpuTotalDrawSceneMicroseconds = sample.CpuTotalDrawSceneMicroseconds,
        CpuSceneBuildMicroseconds = sample.CpuSceneBuildMicroseconds,
        CpuPayloadSignatureMicroseconds = sample.CpuPayloadSignatureMicroseconds,
        CpuObjectCullMicroseconds = sample.CpuObjectCullMicroseconds,
        CpuMeshletCullMicroseconds = sample.CpuMeshletCullMicroseconds,
        CpuStaticBatchBuildMicroseconds = sample.CpuStaticBatchBuildMicroseconds,
        CpuUploadMicroseconds = sample.CpuUploadMicroseconds,
        CpuMaterialUploadMicroseconds = sample.CpuMaterialUploadMicroseconds,
        CpuAccelerationStructureBuildMicroseconds =
            sample.CpuAccelerationStructureBuildMicroseconds,
        CpuAccelerationStructureBlasBuildMicroseconds =
            sample.CpuAccelerationStructureBlasBuildMicroseconds,
        CpuAccelerationStructureBlasCompactionMicroseconds =
            sample.CpuAccelerationStructureBlasCompactionMicroseconds,
        CpuAccelerationStructureTlasBuildMicroseconds =
            sample.CpuAccelerationStructureTlasBuildMicroseconds,
        CpuAccelerationStructureInstanceUploadMicroseconds =
            sample.CpuAccelerationStructureInstanceUploadMicroseconds,
        CpuPrimaryCommandRecordMicroseconds =
            sample.CpuPrimaryCommandRecordMicroseconds,
        CpuSecondaryCommandRecordMicroseconds =
            sample.CpuSecondaryCommandRecordMicroseconds,
        CpuWaitForFrameFenceMicroseconds = sample.CpuWaitForFrameFenceMicroseconds,
        CpuSwapchainImageOwnerWaitMicroseconds =
            sample.CpuSwapchainImageOwnerWaitMicroseconds,
        CpuFrameResourceRecycleWaitMicroseconds =
            sample.CpuFrameResourceRecycleWaitMicroseconds,
        RuntimeStallMicrosecondsThisFrame = sample.RuntimeStallMicrosecondsThisFrame,
        CpuReflectionProbeCaptureRecordMicroseconds =
            sample.CpuReflectionProbeCaptureRecordMicroseconds,
        CpuReflectionProbePrefilterRecordMicroseconds =
            sample.CpuReflectionProbePrefilterRecordMicroseconds,
        ScenePayloadRebuilt = sample.ScenePayloadRebuilt,
        CameraDrivenCpuDrawListRebuilt = sample.CameraDrivenCpuDrawListRebuilt,
        HiZPolicyCameraCut = sample.HiZPolicyCameraCut,
        SceneUploadCount = sample.SceneUploadCount,
        SceneUploadSkipped = sample.SceneUploadSkipped,
        VisibleObjectCount = sample.VisibleObjectCount,
        VisibleMeshletCount = sample.VisibleMeshletCount,
        StaticInstanceBatchCount = sample.StaticInstanceBatchCount,
        StaticInstanceCount = sample.StaticInstanceCount,
        VisibleStaticInstanceCount = sample.VisibleStaticInstanceCount,
        CulledStaticInstanceCount = sample.CulledStaticInstanceCount,
        StaticBatchMeshletDrawCommandCount =
            sample.StaticBatchMeshletDrawCommandCount,
        MaterialCount = sample.MaterialCount,
        MaterialRevision = sample.MaterialRevision,
        TransparentSortCandidateCount = sample.TransparentSortCandidateCount,
        TransparentSortMicroseconds = sample.TransparentSortMicroseconds,
        ReflectionProbeCapturesQueued = sample.ReflectionProbeCapturesQueued,
        ReflectionProbeCapturesCompleted = sample.ReflectionProbeCapturesCompleted,
        ReflectionProbeCapturesCompletedTotal =
            sample.ReflectionProbeCapturesCompletedTotal,
        ObjectCandidatesCpu = sample.ObjectCandidatesCpu,
        ObjectFrustumCulledCpu = sample.ObjectFrustumCulledCpu,
        MeshletCandidatesCpu = sample.MeshletCandidatesCpu,
        MeshletFrustumCulledCpu = sample.MeshletFrustumCulledCpu,
        MeshletLodSkippedCpu = sample.MeshletLodSkippedCpu,
        MeshletLod0SubmittedCpu = sample.MeshletLod0SubmittedCpu,
        MeshletLod1SubmittedCpu = sample.MeshletLod1SubmittedCpu,
        MeshletLod2SubmittedCpu = sample.MeshletLod2SubmittedCpu,
        MeshletCountSubmittedCpu = sample.MeshletCountSubmittedCpu,
        SceneSubmissionActiveMode = sample.SceneSubmissionActiveMode,
        SceneSubmissionCpuCandidateCount = sample.SceneSubmissionCpuCandidateCount,
        SceneSubmissionGpuOpaqueCandidateCount =
            sample.SceneSubmissionGpuOpaqueCandidateCount,
        SceneSubmissionGpuOpaqueFrustumRejectedCount =
            sample.SceneSubmissionGpuOpaqueFrustumRejectedCount,
        SceneSubmissionGpuLod0EmittedCount =
            sample.SceneSubmissionGpuLod0EmittedCount,
        SceneSubmissionGpuLod1EmittedCount =
            sample.SceneSubmissionGpuLod1EmittedCount,
        SceneSubmissionGpuLod2EmittedCount =
            sample.SceneSubmissionGpuLod2EmittedCount,
        SceneSubmissionGpuMissingLodFallbackCount =
            sample.SceneSubmissionGpuMissingLodFallbackCount,
        SceneSubmissionGpuOpaqueLodDecimatedCount =
            sample.SceneSubmissionGpuOpaqueLodDecimatedCount,
        AccelerationStructureBlasBuildCount =
            sample.AccelerationStructureBlasBuildCount,
        AccelerationStructureBlasCompactionQueryCount =
            sample.AccelerationStructureBlasCompactionQueryCount,
        AccelerationStructureBlasCompactionCount =
            sample.AccelerationStructureBlasCompactionCount,
        AccelerationStructureBlasCompactionPendingCount =
            sample.AccelerationStructureBlasCompactionPendingCount,
        AccelerationStructureBlasCompactionQueryOverflowCount =
            sample.AccelerationStructureBlasCompactionQueryOverflowCount,
        AccelerationStructureBlasCompactionQueryReadbackFailureCount =
            sample.AccelerationStructureBlasCompactionQueryReadbackFailureCount,
        AccelerationStructureTlasBuildCount =
            sample.AccelerationStructureTlasBuildCount,
        AccelerationStructureTlasUpdateCount =
            sample.AccelerationStructureTlasUpdateCount,
        AccelerationStructureTlasSkipCount =
            sample.AccelerationStructureTlasSkipCount,
        UploadedBytes = sample.UploadedBytes,
        StableSceneInputUploadBytes = sample.StableSceneInputUploadBytes,
        CpuCandidateListUploadBytes = sample.CpuCandidateListUploadBytes,
        ObjectUploadBytes = sample.ObjectUploadBytes,
        InstanceUploadBytes = sample.InstanceUploadBytes,
        MeshletDrawUploadBytes = sample.MeshletDrawUploadBytes,
        TransparentMeshletDrawUploadBytes = sample.TransparentMeshletDrawUploadBytes,
        SolidDepthMeshletDrawUploadBytes = sample.SolidDepthMeshletDrawUploadBytes,
        MaskedDepthMeshletDrawUploadBytes = sample.MaskedDepthMeshletDrawUploadBytes,
        MaterialUploadBytes = sample.MaterialUploadBytes,
        MaterialExtensionUploadBytes = sample.MaterialExtensionUploadBytes,
        LightUploadBytes = sample.LightUploadBytes,
        AccelerationStructureInstanceUploadBytes =
            sample.AccelerationStructureInstanceUploadBytes,
        AccelerationStructureRayQueryMetadataUploadBytes =
            sample.AccelerationStructureRayQueryMetadataUploadBytes,
        CaptureSceneContentRevision = sample.CaptureSceneContentRevision,
        CaptureFrameSerial = sample.CaptureFrame.FrameSerial,
        CaptureFramesSinceSceneLoad = sample.CaptureFrame.FramesSinceSceneLoad,
        CaptureSceneAssetHash = sample.CaptureSceneAssetHash,
        CaptureSceneStateHash = sample.CaptureSceneStateHash
    };

    private static SampleBenchmarkIntegerStats BuildIntegerStats(
        string name,
        IEnumerable<int> values)
    {
        int[] samples = values.ToArray();
        if (samples.Length == 0)
            return SampleBenchmarkIntegerStats.Empty(name);

        Array.Sort(samples);
        long sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i];
        int p95Index = Math.Min(
            samples.Length - 1,
            (int)Math.Ceiling(samples.Length * 0.95) - 1);
        double median = samples.Length % 2 == 0
            ? (samples[samples.Length / 2 - 1] +
                (double)samples[samples.Length / 2]) * 0.5
            : samples[samples.Length / 2];
        return new SampleBenchmarkIntegerStats(
            name,
            samples.Length,
            sum / (double)samples.Length,
            samples[0],
            samples[^1],
            samples[p95Index],
            median);
    }

    private SampleBenchmarkCaptureContract BuildCaptureContract(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        string controlledIsolationSettingsFingerprint)
    {
        if (_samples.Count == 0)
            return SampleBenchmarkCaptureContract.Unavailable;

        RendererDiagnostics first = _samples[0];
        var mismatches = new List<string>();
        bool movingTrajectory =
            SampleBenchmarkTrajectory.IsMoving(options.Trajectory);
        bool namedTrajectory = options.Trajectory !=
            SampleBenchmarkTrajectoryKind.Stationary;
        int trajectoryFrameCount =
            SampleBenchmarkTrajectory.GetFrameCount(options.Trajectory);
        string expectedTrajectoryFingerprint =
            SampleBenchmarkTrajectory.CreateFingerprint(
                options.Trajectory,
                options.TrajectoryBistroVariant);
        if (string.IsNullOrWhiteSpace(options.TrajectoryFingerprint) ||
            !string.Equals(
                options.TrajectoryFingerprint,
                expectedTrajectoryFingerprint,
                StringComparison.Ordinal))
        {
            mismatches.Add(
                "Benchmark trajectory fingerprint is absent or does not match " +
                $"'{SampleBenchmarkTrajectory.GetName(options.Trajectory)}'.");
        }
        string expectedActivation =
            SampleBenchmarkActivation.Normalize(options.Activation);
        string expectedActivationFingerprint =
            SampleBenchmarkActivation.CreateFingerprint(expectedActivation);
        if (!string.Equals(
                options.Activation,
                expectedActivation,
                StringComparison.Ordinal) ||
            !string.Equals(
                options.ActivationFingerprint,
                expectedActivationFingerprint,
                StringComparison.Ordinal))
        {
            mismatches.Add(
                "Benchmark activation identity is absent, noncanonical, or " +
                "does not match its authored fingerprint.");
        }
        try
        {
            SampleBenchmarkActivation.Validate(
                expectedActivation,
                scenario,
                options.Trajectory,
                options.CaptureVariant,
                options.MeasureFrameCount);
        }
        catch (ArgumentException exception)
        {
            mismatches.Add(
                "Benchmark activation contract is invalid: " +
                exception.Message);
        }
        if (movingTrajectory && _samples.Count != trajectoryFrameCount)
        {
            mismatches.Add(
                $"Moving trajectory '{SampleBenchmarkTrajectory.GetName(options.Trajectory)}' " +
                $"requires exactly {trajectoryFrameCount} measured frames; captured {_samples.Count}.");
        }
        if (_samples.Count < 120)
            mismatches.Add($"Production timing requires at least 120 frames; captured {_samples.Count}.");
        if (first.GiMeasurement.Mode != GiMeasurementMode.Production)
            mismatches.Add($"Measurement mode is {first.GiMeasurement.Mode}, not Production.");
        if (first.ValidationMode != RendererValidationMode.Off)
            mismatches.Add($"Validation mode is {first.ValidationMode}, not Off.");
        if (first.DdgiDetailedCountersCompiled != 0 ||
            first.DdgiDetailedCountersEnabled != 0 ||
            first.DdgiDetailedCountersRequested != 0 ||
            first.GiMeasurement.DetailedCountersReadbackValid)
        {
            mismatches.Add("Detailed DDGI diagnostics are compiled or enabled in a production timing run.");
        }
        if (!IsProductionBuildConfiguration(first.CaptureRun.BuildConfiguration))
        {
            mismatches.Add(
                $"Build configuration '{first.CaptureRun.BuildConfiguration}' is not a production configuration.");
        }
        if (first.GiMeasurement.Mode == GiMeasurementMode.Production &&
            (first.CaptureRenderWidth != 1920 || first.CaptureRenderHeight != 1080))
        {
            mismatches.Add(
                $"Production timing requires an exact 1920x1080 framebuffer; " +
                $"captured {first.CaptureRenderWidth}x{first.CaptureRenderHeight}.");
        }
        RequireIdentity(mismatches, "GPU", first.CaptureGpuDeviceName);
        RequireIdentity(mismatches, "driver", first.CaptureGpuDriverVersion);
        RequireIdentity(mismatches, "scene asset hash", first.CaptureSceneAssetHash);
        RequireIdentity(mismatches, "scene state hash", first.CaptureSceneStateHash);
        RequireIdentity(mismatches, "camera view hash", first.CaptureCamera.ViewHash);
        RequireIdentity(mismatches, "camera projection hash", first.CaptureCamera.ProjectionHash);
        RequireIdentity(mismatches, "executable hash", first.CaptureRun.ExecutableHash);
        RequireIdentity(mismatches, "commit", first.CaptureRun.Commit);
        RequireIdentity(mismatches, "dirty-worktree state", first.CaptureRun.DirtyWorktreeState);
        RequireIdentity(mismatches, "shader bundle hash", first.CaptureRun.ShaderBundleHash);
        RequireIdentity(mismatches, "resolved GI settings hash", first.ResolvedGiSettings.StableHash);
        if (options.RequireProductionTiming && string.IsNullOrWhiteSpace(options.CapturePairId))
            mismatches.Add("Production timing requires a non-empty paired-capture identity.");

        long passTimestampToleranceMicroseconds = 0;
        int resolvedGiSettingsMismatchFrameCount = 0;
        int resolvedGiSettingsDetailBudget = 8;

        for (int index = 0; index < _samples.Count; index++)
        {
            RendererDiagnostics sample = _samples[index];
            CompareInvariant(mismatches, index, "GPU", first.CaptureGpuDeviceName, sample.CaptureGpuDeviceName);
            CompareInvariant(mismatches, index, "driver", first.CaptureGpuDriverVersion, sample.CaptureGpuDriverVersion);
            CompareInvariant(mismatches, index, "width", first.CaptureRenderWidth, sample.CaptureRenderWidth);
            CompareInvariant(mismatches, index, "height", first.CaptureRenderHeight, sample.CaptureRenderHeight);
            CompareInvariant(mismatches, index, "quality", first.ActiveQualityPreset, sample.ActiveQualityPreset);
            CompareInvariant(mismatches, index, "scene revision", first.CaptureSceneContentRevision, sample.CaptureSceneContentRevision);
            if (namedTrajectory)
            {
                IReadOnlyList<string> cameraMismatches =
                    SampleBenchmarkTrajectory.ValidateCamera(
                        options.Trajectory,
                        movingTrajectory ? index : 0,
                        options.TrajectoryBistroVariant,
                        sample.CaptureCamera);
                foreach (string mismatch in cameraMismatches)
                {
                    mismatches.Add(
                        $"Frame {index} trajectory camera {mismatch}.");
                }
            }
            if (movingTrajectory)
            {
                CompareInvariant(
                    mismatches,
                    index,
                    "camera cut serial",
                    first.CaptureCamera.CameraCutSerial,
                    sample.CaptureCamera.CameraCutSerial);
            }
            else
            {
                CompareInvariant(mismatches, index, "scene hash", first.CaptureSceneStateHash, sample.CaptureSceneStateHash);
                CompareInvariant(mismatches, index, "camera", first.CaptureCamera, sample.CaptureCamera);
            }
            CompareInvariant(mismatches, index, "executable", first.CaptureRun.ExecutableHash, sample.CaptureRun.ExecutableHash);
            CompareInvariant(mismatches, index, "commit", first.CaptureRun.Commit, sample.CaptureRun.Commit);
            CompareInvariant(mismatches, index, "dirty state", first.CaptureRun.DirtyWorktreeState, sample.CaptureRun.DirtyWorktreeState);
            CompareInvariant(mismatches, index, "shader bundle", first.CaptureRun.ShaderBundleHash, sample.CaptureRun.ShaderBundleHash);
            CompareInvariant(mismatches, index, "timestamp period", first.GpuTimestampPeriodNanoseconds, sample.GpuTimestampPeriodNanoseconds);
            if (!movingTrajectory && !string.Equals(
                    first.ResolvedGiSettings.StableHash,
                    sample.ResolvedGiSettings.StableHash,
                    StringComparison.Ordinal))
            {
                resolvedGiSettingsMismatchFrameCount++;
                if (resolvedGiSettingsDetailBudget > 0)
                {
                    IReadOnlyList<string> details = DescribeResolvedGiSettingsDifferences(
                        first.ResolvedGiSettings,
                        sample.ResolvedGiSettings,
                        resolvedGiSettingsDetailBudget);
                    if (details.Count == 0)
                    {
                        mismatches.Add(
                            $"Frame {index} changed the resolved GI settings hash " +
                            "without exposing a changed effective setting.");
                        resolvedGiSettingsDetailBudget--;
                    }
                    else
                    {
                        foreach (string detail in details)
                            mismatches.Add($"Frame {index} changed capture GI setting {detail}.");
                        resolvedGiSettingsDetailBudget -= details.Count;
                    }
                }
            }
            CompareInvariant(mismatches, index, "feature isolation", first.ActiveFeatureIsolation, sample.ActiveFeatureIsolation);
            CompareInvariant(mismatches, index, "debug view", first.GlobalIlluminationDebugView, sample.GlobalIlluminationDebugView);
            CompareInvariant(mismatches, index, "DDGI cache generation", first.CaptureFrame.DdgiCacheGeneration, sample.CaptureFrame.DdgiCacheGeneration);
            if (movingTrajectory)
            {
                RequireIdentity(
                    mismatches,
                    $"frame {index} resolved GI settings hash",
                    sample.ResolvedGiSettings.StableHash);
            }
            else if (sample.CaptureFrame.WarmupState !=
                     DdgiRuntimeWarmupState.SteadyState)
                mismatches.Add($"Frame {index} warmup state is {sample.CaptureFrame.WarmupState}.");
            bool acceptedTailCertificate =
                SampleBenchmarkRunner.HasAcceptedCurrentSimpleDdgiTailCertificate(
                    sample);
            if (!movingTrajectory &&
                sample.CaptureFrame.TransportConvergencePending &&
                !acceptedTailCertificate)
                mismatches.Add($"Frame {index} still has pending transport convergence.");
            if (!movingTrajectory &&
                sample.SimpleDdgiActive != 0 &&
                sample.SimpleDdgiTransportV2Active != 0 &&
                !(sample.SimpleDdgiTransportTailCertificationEnabled
                    ? acceptedTailCertificate
                    : SampleBenchmarkRunner.HasSourceReadySimpleDdgiTransportPopulation(
                        sample)))
            {
                SimpleDdgiTransportConvergenceTelemetry convergence =
                    sample.SimpleDdgiTransportConvergence;
                int qualified = Math.Min(
                    Math.Max(0, convergence.ParticipatingProbeCount),
                    Math.Max(0, convergence.ConvergedProbeCount) +
                        Math.Max(0, convergence.RoutineSourceRepairProbeCount) +
                        Math.Max(0, convergence.RoutineMaintenancePendingProbeCount));
                mismatches.Add(
                    $"Frame {index} has only " +
                    $"{qualified}/" +
                    $"{Math.Max(0, convergence.ParticipatingProbeCount)} converged or scheduled-refresh transport probes; " +
                    "at least 95% are required.");
            }
            if (sample.DebugOverlayEnabled != 0 || sample.GpuDebugOverlayMicroseconds > 0)
                mismatches.Add($"Frame {index} rendered a debug overlay.");
            if (sample.ScreenshotRequested != 0 || sample.ScreenshotPendingCount != 0)
                mismatches.Add($"Frame {index} contained screenshot capture work.");
            if (sample.RenderDocCaptureRequested != 0)
                mismatches.Add($"Frame {index} requested a RenderDoc capture.");
            if (sample.DdgiInvestigationCountersReadbackValid != 0)
                mismatches.Add($"Frame {index} contains detailed DDGI counter readback.");
            if (sample.SimpleDdgiActive != 0)
            {
                SimpleDdgiCapacityTiming capacity =
                    sample.SimpleDdgiUploadTiming.CapacityDetails;
                if (!capacity.StableKeyHit)
                    mismatches.Add($"Frame {index} missed the stable DDGI capacity key.");
                if (capacity.TransitionCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.TransitionCount} DDGI capacity transitions.");
                if (capacity.DeviceIdleWaitCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.DeviceIdleWaitCount} DDGI device-idle waits.");
                if (capacity.BufferSizeLookupCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.BufferSizeLookupCount} stable-path buffer-size lookups.");
                if (capacity.DescriptorRegistrationCount != 0)
                    mismatches.Add($"Frame {index} performed {capacity.DescriptorRegistrationCount} stable-path descriptor registrations.");
            }
            if (sample.GpuTimingValid != 0)
            {
                long tolerance = ResolvePassTimestampReconciliationToleranceMicroseconds(sample);
                passTimestampToleranceMicroseconds = Math.Max(
                    passTimestampToleranceMicroseconds,
                    tolerance);
                long passSum = GpuIndependentTimings.Sum(selector =>
                    Math.Max(0L, selector.GetMicroseconds(sample)));
                long unexplained = sample.GpuFrameMicroseconds - passSum;
                if (Math.Abs(unexplained) > tolerance)
                {
                    mismatches.Add(
                        $"Frame {index} GPU pass sum differs from the frame by " +
                        $"{unexplained} us; tolerance is {tolerance} us.");
                }
            }
        }

        if (resolvedGiSettingsMismatchFrameCount > 0)
        {
            mismatches.Add(
                $"Resolved GI settings changed in {resolvedGiSettingsMismatchFrameCount} " +
                $"of {_samples.Count} measured frames; at most eight field differences are shown.");
        }

        string identityHash = CreateCaptureIdentityHash(
            first,
            expectedActivationFingerprint,
            includeTargetState: false);
        string fullIdentityHash = CreateCaptureIdentityHash(
            first,
            expectedActivationFingerprint,
            includeTargetState: true);
        string controlledIsolationIdentityHash =
            SampleBenchmarkActivation.RequiresDeterministicAnimation(
                expectedActivation)
                ? CreateControlledIsolationIdentityHash(
                    first,
                    expectedActivation)
                : "unavailable";
        string trajectoryRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
            options.Trajectory,
            options.TrajectoryBistroVariant,
            first.CaptureCamera);
        string trajectorySequenceHash = CreateTrajectorySequenceHash(
            _samples,
            options);
        IReadOnlyList<SampleBenchmarkControlledIsolationFrameEvidence>
            controlledIsolationFrames =
                Array.Empty<SampleBenchmarkControlledIsolationFrameEvidence>();
        string controlledIsolationSequenceHash = "unavailable";
        if (SampleBenchmarkActivation.RequiresDeterministicAnimation(
                expectedActivation))
        {
            try
            {
                controlledIsolationFrames =
                    SampleBenchmarkControlledIsolationSequence.CreateFrames(
                        _samples,
                        controlledIsolationSettingsFingerprint);
                controlledIsolationSequenceHash =
                    SampleBenchmarkControlledIsolationSequence
                        .ValidateAndCreateHash(
                            controlledIsolationFrames,
                            options.MeasureFrameCount,
                            SampleBenchmarkTrajectory.GetName(
                                options.Trajectory),
                            expectedTrajectoryFingerprint,
                            trajectoryRouteHash,
                            expectedActivation,
                            controlledIsolationSettingsFingerprint);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or
                    OverflowException)
            {
                mismatches.Add(
                    "Directional controlled-isolation sequence evidence is " +
                    $"invalid: {exception.Message}");
            }
        }
        bool production = first.GiMeasurement.Mode == GiMeasurementMode.Production &&
            first.ValidationMode == RendererValidationMode.Off &&
            first.DdgiDetailedCountersCompiled == 0 &&
            first.DdgiDetailedCountersEnabled == 0 &&
            IsProductionBuildConfiguration(first.CaptureRun.BuildConfiguration);
        bool comparable = mismatches.Count == 0 &&
            (!options.RequireProductionTiming || production);
        return new SampleBenchmarkCaptureContract(
            comparable,
            production,
            options.CapturePairId?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(options.CaptureVariant)
                ? "baseline"
                : options.CaptureVariant.Trim(),
            identityHash,
            Array.AsReadOnly(mismatches.Distinct(StringComparer.Ordinal).ToArray()))
        {
            FullIdentityHash = fullIdentityHash,
            Trajectory = SampleBenchmarkTrajectory.GetName(options.Trajectory),
            TrajectoryFingerprint = expectedTrajectoryFingerprint,
            TrajectoryFrameCount = trajectoryFrameCount,
            TrajectoryRouteHash = trajectoryRouteHash,
            TrajectorySequenceHash = trajectorySequenceHash,
            SponzaFixtureMode = options.SponzaFixtureMode,
            Activation = expectedActivation,
            ActivationFingerprint = expectedActivationFingerprint,
            ControlledIsolationIdentityHash =
                controlledIsolationIdentityHash,
            ControlledIsolationSettingsFingerprint =
                SampleBenchmarkActivation.RequiresDeterministicAnimation(
                    expectedActivation)
                    ? controlledIsolationSettingsFingerprint
                    : "unavailable",
            ControlledIsolationSequenceHash =
                controlledIsolationSequenceHash,
            ControlledIsolationFrames = controlledIsolationFrames,
            PassTimestampReconciliationToleranceMicroseconds =
                passTimestampToleranceMicroseconds
        };
    }

    private static bool IsProductionBuildConfiguration(string? value)
    {
        string configuration = value?.Split(';', 2)[0].Trim() ?? string.Empty;
        return string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration, "ShippingPerformance", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration, "ProfileSymbols", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireIdentity(
        ICollection<string> mismatches,
        string role,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"Capture {role} is unavailable.");
        }
    }

    private static long ResolvePassTimestampReconciliationToleranceMicroseconds(
        RendererDiagnostics diagnostics)
    {
        int roundedIntervalCount = 1 + GpuIndependentTimings.Count(selector =>
            selector.GetMicroseconds(diagnostics) > 0);
        double timestampPeriodMicroseconds = Math.Max(
            diagnostics.GpuTimestampPeriodNanoseconds,
            0.0f) / 1000.0;
        // Each duration subtracts two raw timestamps, then is rounded to an
        // integer microsecond. Include both device timestamp quantization and
        // independent integer-rounding error for every interval.
        double tolerance = roundedIntervalCount *
            (1.0 + timestampPeriodMicroseconds * 2.0);
        return Math.Max(1L, checked((long)Math.Ceiling(tolerance)));
    }

    private static void CompareInvariant<T>(
        List<string> mismatches,
        int frameIndex,
        string name,
        T expected,
        T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            mismatches.Add($"Frame {frameIndex} changed capture {name}.");
    }

    internal static IReadOnlyList<string> DescribeResolvedGiSettingsDifferences(
        ResolvedGiSettingsMetadata expected,
        ResolvedGiSettingsMetadata actual,
        int maximumDifferenceCount)
    {
        if (maximumDifferenceCount <= 0)
            return Array.Empty<string>();

        IReadOnlyDictionary<string, string> expectedSettings =
            IndexResolvedGiSettings(expected.EffectiveSettings);
        IReadOnlyDictionary<string, string> actualSettings =
            IndexResolvedGiSettings(actual.EffectiveSettings);
        var keys = new SortedSet<string>(expectedSettings.Keys, StringComparer.Ordinal);
        keys.UnionWith(actualSettings.Keys);
        var differences = new List<string>(Math.Min(maximumDifferenceCount, keys.Count));
        foreach (string key in keys)
        {
            bool hasExpected = expectedSettings.TryGetValue(key, out string? expectedValue);
            bool hasActual = actualSettings.TryGetValue(key, out string? actualValue);
            if (hasExpected && hasActual &&
                string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                continue;
            }

            differences.Add(
                $"'{key}' from {FormatResolvedGiSettingValue(hasExpected, expectedValue)} " +
                $"to {FormatResolvedGiSettingValue(hasActual, actualValue)}");
            if (differences.Count >= maximumDifferenceCount)
                break;
        }
        return Array.AsReadOnly(differences.ToArray());
    }

    private static IReadOnlyDictionary<string, string> IndexResolvedGiSettings(
        IReadOnlyList<string> settings)
    {
        var indexed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string setting in settings)
        {
            int separator = setting.IndexOf('=');
            string key = separator < 0 ? setting : setting[..separator];
            string value = separator < 0 ? string.Empty : setting[(separator + 1)..];
            indexed[key] = value;
        }
        return indexed;
    }

    private static string FormatResolvedGiSettingValue(bool present, string? value)
    {
        if (!present)
            return "<missing>";
        const int maximumLength = 160;
        string bounded = value ?? string.Empty;
        if (bounded.Length > maximumLength)
            bounded = bounded[..maximumLength] + "...";
        return "'" + bounded + "'";
    }

    private static string CreateCaptureIdentityHash(
        RendererDiagnostics diagnostics,
        string activationFingerprint,
        bool includeTargetState)
    {
        var parts = new List<string>
        {
            diagnostics.CaptureGpuDeviceName,
            diagnostics.CaptureGpuDriverVersion,
            diagnostics.CaptureRenderWidth.ToString(CultureInfo.InvariantCulture),
            diagnostics.CaptureRenderHeight.ToString(CultureInfo.InvariantCulture),
            diagnostics.ActiveQualityPreset.ToString(),
            diagnostics.CaptureSceneAssetHash,
            diagnostics.CaptureSceneContentRevision.ToString(CultureInfo.InvariantCulture),
            diagnostics.CaptureCamera.ViewHash,
            diagnostics.CaptureCamera.ProjectionHash,
            diagnostics.CaptureRun.BuildConfiguration,
            diagnostics.CaptureRun.ExecutableHash,
            diagnostics.CaptureRun.Commit,
            diagnostics.CaptureRun.DirtyWorktreeState,
            diagnostics.CaptureRun.ShaderBundleHash,
            diagnostics.CaptureRun.SettingsSchemaVersion.ToString(
                CultureInfo.InvariantCulture),
            diagnostics.ResolvedGiSettings.StableHash,
            diagnostics.ActiveFeatureIsolation.ToString(),
            diagnostics.GlobalIlluminationDebugView.ToString(),
            diagnostics.CaptureFrame.DdgiCacheGeneration.ToString(
                CultureInfo.InvariantCulture),
            activationFingerprint
        };
        if (includeTargetState)
        {
            parts.Add(diagnostics.CaptureSceneStateHash);
        }
        string canonical = string.Join("|", parts);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string CreateControlledIsolationIdentityHash(
        RendererDiagnostics diagnostics,
        string activation) =>
        CreateCaptureIdentityHash(
            diagnostics,
            SampleBenchmarkActivation.CreateControlledIsolationFingerprint(
                activation),
            includeTargetState: true);

    private static string CreateTrajectorySequenceHash(
        IReadOnlyList<RendererDiagnostics> samples,
        SampleBenchmarkOptions options)
    {
        var canonical = new StringBuilder();
        canonical.Append("njulf-benchmark-trajectory-sequence/v1|")
            .Append(SampleBenchmarkTrajectory.GetName(options.Trajectory))
            .Append('|')
            .Append(options.TrajectoryFingerprint)
            .Append('|')
            .Append(options.ActivationFingerprint)
            .Append('\n');
        for (int index = 0; index < samples.Count; index++)
        {
            RendererDiagnostics sample = samples[index];
            PerformanceCaptureCameraMetadata camera = sample.CaptureCamera;
            canonical.Append(index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionX.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionY.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionZ.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.YawRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PitchRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FieldOfViewRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.NearPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FarPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.ViewHash).Append('|')
                .Append(camera.ProjectionHash).Append('|')
                .Append(camera.CameraCutSerial.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(sample.CaptureSceneStateHash).Append('|')
                .Append(sample.ResolvedGiSettings.StableHash).Append('|')
                .Append(sample.CaptureFrame.WarmupState).Append('|')
                .Append(sample.CaptureFrame.TransportConvergencePending ? '1' : '0')
                .Append('|')
                .Append(sample.ActiveFeatureIsolation).Append('|')
                .Append(sample.GlobalIlluminationDebugView)
                .Append('\n');
        }

        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static double MicrosecondsToMilliseconds(long microseconds)
    {
        return microseconds / 1000.0;
    }

    private sealed record TimingSelector(string Name, Func<RendererDiagnostics, long> GetMicroseconds);

}
