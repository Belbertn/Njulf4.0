using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal sealed record SampleDdgiResidencyModeObservation(
    int FrameIndex,
    SimpleDdgiProbeResidencyMode Mode,
    uint ResourceGeneration,
    bool FeedbackValid,
    int ResidentPageCount,
    ulong PageArenaBytes);

/// <summary>
/// Exercises the complete Dense -> Shadow -> SparseNearRing residency mode
/// transaction in one renderer/device lifetime, then restores the exact
/// initial settings. This is intentionally independent of the quality-switch
/// budget gate because forward-GI incremental timing requires paired captures.
/// </summary>
internal sealed class SampleDdgiResidencySwitchSmokeRunner
{
    private const int MaximumTransitionWaitFrames = 120;

    private static readonly SimpleDdgiProbeResidencyMode[] RequiredModes =
    [
        SimpleDdgiProbeResidencyMode.Dense,
        SimpleDdgiProbeResidencyMode.Shadow,
        SimpleDdgiProbeResidencyMode.SparseNearRing
    ];

    private readonly Action<SimpleDdgiProbeResidencyMode> _applyMode;
    private readonly Action _restoreInitialSettings;
    private readonly Func<SimpleDdgiProbeResidencyMode> _getMode;
    private readonly Func<string> _getSettingsFingerprint;
    private readonly Func<string> _getDeviceIdentity;
    private readonly Action<SampleSmokeOperationResult> _record;
    private readonly Action _exit;
    private readonly SimpleDdgiProbeResidencyMode _initialMode;
    private readonly string _initialSettingsFingerprint;
    private readonly string _initialDeviceIdentity;
    private readonly List<SampleDdgiResidencyModeObservation> _observations =
        new();

    private int _nextModeIndex;
    private int _expectedModeAppliedAfterFrame = -1;
    private SimpleDdgiProbeResidencyMode? _expectedMode;
    private uint _lastDemandCollectingResourceGeneration;
    private bool _awaitingRollback;
    private bool _rollbackAttempted;
    private string? _rollbackFailure;
    private bool _completed;

    public SampleDdgiResidencySwitchSmokeRunner(
        Action<SimpleDdgiProbeResidencyMode> applyMode,
        Action restoreInitialSettings,
        Func<SimpleDdgiProbeResidencyMode> getMode,
        Func<string> getSettingsFingerprint,
        Func<string> getDeviceIdentity,
        Action<SampleSmokeOperationResult> record,
        Action exit)
    {
        _applyMode = applyMode ?? throw new ArgumentNullException(nameof(applyMode));
        _restoreInitialSettings = restoreInitialSettings ??
            throw new ArgumentNullException(nameof(restoreInitialSettings));
        _getMode = getMode ?? throw new ArgumentNullException(nameof(getMode));
        _getSettingsFingerprint = getSettingsFingerprint ??
            throw new ArgumentNullException(nameof(getSettingsFingerprint));
        _getDeviceIdentity = getDeviceIdentity ??
            throw new ArgumentNullException(nameof(getDeviceIdentity));
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _initialMode = _getMode().Sanitize();
        _initialSettingsFingerprint = RequireIdentity(
            _getSettingsFingerprint(),
            "render-settings fingerprint");
        _initialDeviceIdentity = RequireIdentity(
            _getDeviceIdentity(),
            "renderer device identity");
    }

    public IReadOnlyList<SampleDdgiResidencyModeObservation> Observations =>
        _observations;
    public bool Completed => _completed;
    public string? Failure { get; private set; }

    public void OnFrameRendered(
        int frameIndex,
        RendererDiagnostics diagnostics)
    {
        if (_completed)
            return;
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (_expectedMode.HasValue)
        {
            if (!TryObserveExpectedMode(
                    frameIndex,
                    _expectedMode.Value,
                    diagnostics))
            {
                return;
            }

            if (_awaitingRollback)
            {
                Complete(frameIndex);
                return;
            }
        }

        if (_nextModeIndex < RequiredModes.Length)
        {
            SimpleDdgiProbeResidencyMode next =
                RequiredModes[_nextModeIndex++];
            try
            {
                _applyMode(next);
            }
            catch (Exception ex)
            {
                Fail(
                    frameIndex,
                    $"Applying Simple-DDGI residency mode {next} failed with " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return;
            }
            _expectedMode = next;
            _expectedModeAppliedAfterFrame = frameIndex;
            return;
        }

        if (!TryRestoreInitialSettings(out string? rollbackFailure))
        {
            Fail(
                frameIndex,
                rollbackFailure ??
                "Restoring the initial render settings failed.");
            return;
        }
        _expectedMode = _initialMode;
        _expectedModeAppliedAfterFrame = frameIndex;
        _awaitingRollback = true;
    }

    private bool TryObserveExpectedMode(
        int frameIndex,
        SimpleDdgiProbeResidencyMode expected,
        RendererDiagnostics diagnostics)
    {
        int elapsedFrames = frameIndex - _expectedModeAppliedAfterFrame;
        SimpleDdgiProbeResidencyMode configured;
        try
        {
            configured = _getMode().Sanitize();
        }
        catch (Exception ex)
        {
            Fail(
                frameIndex,
                $"Reading the Simple-DDGI residency mode failed with " +
                $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        SimpleDdgiProbeResidencyTelemetry telemetry =
            diagnostics.SimpleDdgiProbeResidency;
        bool published = configured == expected &&
            telemetry.IsAvailable &&
            telemetry.Mode == expected;
        if (!published)
        {
            return WaitOrFail(
                frameIndex,
                elapsedFrames,
                expected,
                $"settings={configured}, telemetryAvailable={telemetry.IsAvailable}, " +
                $"telemetryMode={telemetry.Mode}, reason='{telemetry.FallbackReason}'");
        }

        if (expected == SimpleDdgiProbeResidencyMode.Dense)
        {
            if (telemetry.FeedbackValid || telemetry.SparseAuthoritative ||
                telemetry.PageArenaBytes != 0UL ||
                telemetry.FeedbackReadbackBytes != 0UL)
            {
                Fail(
                    frameIndex,
                    "Dense mode retained sparse residency authority or feedback: " +
                    $"feedback={telemetry.FeedbackValid}, " +
                    $"authoritative={telemetry.SparseAuthoritative}, " +
                    $"arena={telemetry.PageArenaBytes}, " +
                    $"readback={telemetry.FeedbackReadbackBytes}.");
                return false;
            }
        }
        else
        {
            bool ready = telemetry.FeedbackValid &&
                telemetry.ResidencyStateValid &&
                telemetry.CurrentResourceGeneration != 0u &&
                telemetry.FeedbackResourceGeneration ==
                    telemetry.CurrentResourceGeneration;
            if (!ready)
            {
                return WaitOrFail(
                    frameIndex,
                    elapsedFrames,
                    expected,
                    $"feedback={telemetry.FeedbackValid}, " +
                    $"stateValid={telemetry.ResidencyStateValid}, " +
                    $"resource={telemetry.CurrentResourceGeneration}, " +
                    $"feedbackResource={telemetry.FeedbackResourceGeneration}");
            }

            if (expected.UsesSparsePayloads() != telemetry.SparseAuthoritative)
            {
                Fail(
                    frameIndex,
                    $"Residency authority mismatch for {expected}: " +
                    $"sparseAuthoritative={telemetry.SparseAuthoritative}.");
                return false;
            }
            if (!_awaitingRollback)
            {
                if (_lastDemandCollectingResourceGeneration != 0u &&
                    telemetry.CurrentResourceGeneration ==
                        _lastDemandCollectingResourceGeneration)
                {
                    Fail(
                        frameIndex,
                        $"Residency mode {expected} reused resource generation " +
                        $"{telemetry.CurrentResourceGeneration} from the prior transaction.");
                    return false;
                }
                _lastDemandCollectingResourceGeneration =
                    telemetry.CurrentResourceGeneration;
            }
        }

        string? integrityFailure = FindIntegrityFailure(telemetry);
        if (integrityFailure != null)
        {
            Fail(frameIndex, $"Residency mode {expected} {integrityFailure}");
            return false;
        }

        string deviceIdentity;
        try
        {
            deviceIdentity = RequireIdentity(
                _getDeviceIdentity(),
                "renderer device identity");
        }
        catch (Exception ex)
        {
            Fail(
                frameIndex,
                $"Reading the renderer device identity failed with " +
                $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (!string.Equals(
                deviceIdentity,
                _initialDeviceIdentity,
                StringComparison.Ordinal))
        {
            Fail(
                frameIndex,
                "Renderer device changed during the in-process residency switch. " +
                $"before='{_initialDeviceIdentity}', after='{deviceIdentity}'.");
            return false;
        }

        if (_awaitingRollback)
        {
            string restoredFingerprint;
            try
            {
                restoredFingerprint = RequireIdentity(
                    _getSettingsFingerprint(),
                    "render-settings fingerprint");
            }
            catch (Exception ex)
            {
                Fail(
                    frameIndex,
                    $"Reading restored render settings failed with " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
            if (!string.Equals(
                    restoredFingerprint,
                    _initialSettingsFingerprint,
                    StringComparison.Ordinal))
            {
                Fail(
                    frameIndex,
                    "Residency-switch rollback did not restore the complete " +
                    $"render settings. before={_initialSettingsFingerprint}, " +
                    $"after={restoredFingerprint}.");
                return false;
            }
        }

        _observations.Add(new SampleDdgiResidencyModeObservation(
            frameIndex,
            expected,
            telemetry.CurrentResourceGeneration,
            telemetry.FeedbackValid,
            telemetry.ResidentPageCount,
            telemetry.PageArenaBytes));
        return true;
    }

    private bool WaitOrFail(
        int frameIndex,
        int elapsedFrames,
        SimpleDdgiProbeResidencyMode expected,
        string detail)
    {
        if (elapsedFrames <= MaximumTransitionWaitFrames)
            return false;
        Fail(
            frameIndex,
            $"Residency mode {expected} did not publish a complete transaction " +
            $"within {MaximumTransitionWaitFrames} frames: {detail}");
        return false;
    }

    private static string? FindIntegrityFailure(
        SimpleDdgiProbeResidencyTelemetry telemetry)
    {
        if (telemetry.PageTableReverseDisagreementCount != 0)
            return "reported page-table/reverse-map disagreement.";
        if (telemetry.DuplicateVirtualOwnerCount != 0 ||
            telemetry.DuplicatePhysicalOwnerCount != 0)
        {
            return "reported duplicate page ownership.";
        }
        if (telemetry.StaleVirtualRequestCount != 0 ||
            telemetry.StaleMappingRequestCount != 0 ||
            telemetry.StaleResourceRequestCount != 0)
        {
            return "reported a stale generation request.";
        }
        if (telemetry.OutOfRangeRequestCount != 0)
            return "reported an out-of-range request.";
        if (telemetry.ReceiverRequestOverflowCount != 0)
            return "reported receiver-demand overflow.";
        return null;
    }

    private void Complete(int frameIndex)
    {
        _completed = true;
        _record(new SampleSmokeOperationResult(
            "ddgi-residency-switch",
            "passed",
            frameIndex,
            $"modes={string.Join(",", RequiredModes)}, rollback={_initialMode}, " +
            $"settings={_initialSettingsFingerprint}, rendererRestarted=false, " +
            $"deviceIdentity='{_initialDeviceIdentity}'"));
        _exit();
    }

    private void Fail(int frameIndex, string failure)
    {
        if (_completed)
            return;

        bool rollbackSucceeded = TryRestoreInitialSettings(
            out string? rollbackFailure);
        failure += rollbackSucceeded
            ? " Initial render settings rollback completed."
            : $" Initial render settings rollback failed: {rollbackFailure}";
        Failure = failure;
        _completed = true;
        _record(new SampleSmokeOperationResult(
            "ddgi-residency-switch",
            "failed",
            frameIndex,
            failure));
        _exit();
    }

    private bool TryRestoreInitialSettings(out string? failure)
    {
        if (_rollbackAttempted)
        {
            failure = _rollbackFailure;
            return failure == null;
        }

        _rollbackAttempted = true;
        try
        {
            _restoreInitialSettings();
            string restoredFingerprint = RequireIdentity(
                _getSettingsFingerprint(),
                "render-settings fingerprint");
            if (!string.Equals(
                    restoredFingerprint,
                    _initialSettingsFingerprint,
                    StringComparison.Ordinal))
            {
                _rollbackFailure =
                    "Residency-switch rollback did not restore the complete " +
                    $"render settings. before={_initialSettingsFingerprint}, " +
                    $"after={restoredFingerprint}.";
            }
        }
        catch (Exception ex)
        {
            _rollbackFailure = $"{ex.GetType().Name}: {ex.Message}";
        }

        failure = _rollbackFailure;
        return failure == null;
    }

    private static string RequireIdentity(string identity, string role)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new InvalidOperationException($"The {role} was empty.");
        return identity;
    }
}
