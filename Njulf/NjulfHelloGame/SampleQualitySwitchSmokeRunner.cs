using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal sealed record SampleQualityTierObservation(
    int FrameIndex,
    RenderQualityPreset Preset,
    RenderBudgetStatus BudgetStatus,
    ulong TrackedGpuMemoryBytes,
    ulong ConfiguredGpuMemoryBudgetBytes,
    ulong ActualGpuMemoryUsageBytes,
    ulong ActualGpuMemoryBudgetBytes,
    IReadOnlyList<string> OverBudgetMetrics);

/// <summary>
/// Applies every shipping quality tier in one renderer process, observes one
/// completed frame from each tier, and restores the original settings without
/// recreating the application or Vulkan device.
/// </summary>
internal sealed class SampleQualitySwitchSmokeRunner
{
    private const int MaximumGpuTimingWaitFrames = 120;

    private static readonly RenderQualityPreset[] RequiredPresets =
    [
        RenderQualityPreset.Low,
        RenderQualityPreset.Medium,
        RenderQualityPreset.High,
        RenderQualityPreset.Ultra
    ];

    private readonly Action<RenderQualityPreset> _applyPreset;
    private readonly Action _restoreInitialSettings;
    private readonly Func<RenderQualityPreset> _getPreset;
    private readonly Func<string> _getSettingsFingerprint;
    private readonly Func<string> _getDeviceIdentity;
    private readonly Action<SampleSmokeOperationResult> _record;
    private readonly Action _exit;
    private readonly RenderQualityPreset _initialPreset;
    private readonly string _initialSettingsFingerprint;
    private readonly string _initialDeviceIdentity;
    private readonly List<SampleQualityTierObservation> _observations = new();

    private int _nextPresetIndex;
    private int _expectedPresetAppliedAfterFrame = -1;
    private RenderQualityPreset? _expectedPreset;
    private bool _awaitingRollback;
    private bool _rollbackAttempted;
    private string? _rollbackFailure;
    private bool _completed;

    public SampleQualitySwitchSmokeRunner(
        Action<RenderQualityPreset> applyPreset,
        Action restoreInitialSettings,
        Func<RenderQualityPreset> getPreset,
        Func<string> getSettingsFingerprint,
        Func<string> getDeviceIdentity,
        Action<SampleSmokeOperationResult> record,
        Action exit)
    {
        _applyPreset = applyPreset ?? throw new ArgumentNullException(nameof(applyPreset));
        _restoreInitialSettings =
            restoreInitialSettings ?? throw new ArgumentNullException(nameof(restoreInitialSettings));
        _getPreset = getPreset ?? throw new ArgumentNullException(nameof(getPreset));
        _getSettingsFingerprint =
            getSettingsFingerprint ?? throw new ArgumentNullException(nameof(getSettingsFingerprint));
        _getDeviceIdentity = getDeviceIdentity ?? throw new ArgumentNullException(nameof(getDeviceIdentity));
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _initialPreset = _getPreset();
        _initialSettingsFingerprint = RequireIdentity(
            _getSettingsFingerprint(),
            "render-settings fingerprint");
        _initialDeviceIdentity = RequireIdentity(
            _getDeviceIdentity(),
            "renderer device identity");
    }

    public IReadOnlyList<SampleQualityTierObservation> Observations => _observations;
    public bool Completed => _completed;
    public string? Failure { get; private set; }

    public void OnFrameRendered(
        int frameIndex,
        RendererDiagnostics diagnostics,
        RenderBudgetSnapshot budget)
    {
        if (_completed)
            return;
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(budget);

        if (_expectedPreset.HasValue)
        {
            if (!IsFreshGpuTimingReady(
                    frameIndex,
                    _expectedPreset.Value,
                    diagnostics))
            {
                return;
            }

            if (!ValidateCompletedTier(frameIndex, _expectedPreset.Value, diagnostics, budget))
                return;

            if (_awaitingRollback)
            {
                Complete(frameIndex);
                return;
            }
        }

        if (_nextPresetIndex < RequiredPresets.Length)
        {
            RenderQualityPreset next = RequiredPresets[_nextPresetIndex++];
            try
            {
                _applyPreset(next);
            }
            catch (Exception ex)
            {
                Fail(
                    frameIndex,
                    $"Applying quality tier {next} failed with " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return;
            }
            _expectedPreset = next;
            _expectedPresetAppliedAfterFrame = frameIndex;
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
        _expectedPreset = _initialPreset;
        _expectedPresetAppliedAfterFrame = frameIndex;
        _awaitingRollback = true;
    }

    private bool IsFreshGpuTimingReady(
        int frameIndex,
        RenderQualityPreset expected,
        RendererDiagnostics diagnostics)
    {
        int elapsedFrames = frameIndex - _expectedPresetAppliedAfterFrame;
        int pipelineLatency = Math.Max(0, diagnostics.GpuTimingFrameLatency);
        if (elapsedFrames <= pipelineLatency)
            return false;

        if (diagnostics.GpuTimingSupported == 0)
        {
            Fail(
                frameIndex,
                $"Quality tier {expected} cannot be qualified because GPU timestamps are unsupported: " +
                $"{diagnostics.GpuTimingUnavailableReason}");
            return false;
        }

        if (diagnostics.GpuTimingEnabled == 0)
        {
            Fail(
                frameIndex,
                $"Quality tier {expected} cannot be qualified because GPU timing is disabled.");
            return false;
        }

        if (diagnostics.GpuTimingValid != 0)
            return true;

        if (elapsedFrames <= pipelineLatency + MaximumGpuTimingWaitFrames)
            return false;

        Fail(
            frameIndex,
            $"Quality tier {expected} did not publish a fresh GPU timing sample within " +
            $"{MaximumGpuTimingWaitFrames} post-latency frames: " +
            $"{diagnostics.GpuTimingUnavailableReason}");
        return false;
    }

    private bool ValidateCompletedTier(
        int frameIndex,
        RenderQualityPreset expected,
        RendererDiagnostics diagnostics,
        RenderBudgetSnapshot budget)
    {
        RenderQualityPreset currentPreset;
        try
        {
            currentPreset = _getPreset();
        }
        catch (Exception ex)
        {
            Fail(
                frameIndex,
                $"Reading the active quality tier failed with " +
                $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (currentPreset != expected || diagnostics.ActiveQualityPreset != expected)
        {
            Fail(
                frameIndex,
                $"Quality tier publication mismatch. expected={expected}, " +
                $"settings={currentPreset}, diagnostics={diagnostics.ActiveQualityPreset}.");
            return false;
        }

        ulong expectedPrimitiveProfileBudget =
            RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(expected);
        if (diagnostics.MaterialPrimitiveProfileAbsoluteBudgetBytes !=
            expectedPrimitiveProfileBudget)
        {
            Fail(
                frameIndex,
                $"Primitive transport profile admission budget did not follow the quality tier. " +
                $"preset={expected}, expected={expectedPrimitiveProfileBudget}, " +
                $"actual={diagnostics.MaterialPrimitiveProfileAbsoluteBudgetBytes} bytes.");
            return false;
        }

        string currentDeviceIdentity;
        try
        {
            currentDeviceIdentity = RequireIdentity(
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

        if (!string.Equals(currentDeviceIdentity, _initialDeviceIdentity, StringComparison.Ordinal))
        {
            Fail(
                frameIndex,
                $"Renderer device changed during an in-process quality switch. " +
                $"before='{_initialDeviceIdentity}', after='{currentDeviceIdentity}'.");
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
                    $"Quality-switch rollback did not restore the complete render settings. " +
                    $"before={_initialSettingsFingerprint}, after={restoredFingerprint}.");
                return false;
            }
        }

        SampleBudgetMetricCoverage metricCoverage =
            SampleBudgetMetricCoverage.Evaluate(
                budget.Metrics,
                diagnostics,
                $"Quality tier {expected}",
                budget.OverallStatus);
        if (!metricCoverage.Passed)
        {
            Fail(
                frameIndex,
                metricCoverage.Failure ??
                $"Quality tier {expected} budget telemetry coverage failed.");
            return false;
        }

        string[] overBudget = budget.Metrics
            .Where(metric => metric.Status == RenderBudgetStatus.OverBudget)
            .Select(metric =>
                $"{metric.Name}={metric.Value:R}{metric.Unit}>{metric.FailureThreshold:R}{metric.Unit}")
            .ToArray();
        if (diagnostics.GpuMemoryBudgetQueryAvailable != 0 &&
            diagnostics.ActualGpuMemoryBudgetBytes > 0 &&
            diagnostics.ActualGpuMemoryUsageBytes > diagnostics.ActualGpuMemoryBudgetBytes)
        {
            overBudget =
            [
                .. overBudget,
                $"actual-gpu-memory={diagnostics.ActualGpuMemoryUsageBytes}bytes>" +
                $"{diagnostics.ActualGpuMemoryBudgetBytes}bytes"
            ];
        }

        _observations.Add(new SampleQualityTierObservation(
            frameIndex,
            expected,
            budget.OverallStatus,
            diagnostics.TrackedGpuMemoryBytes,
            diagnostics.GpuMemoryBudgetBytes,
            diagnostics.ActualGpuMemoryUsageBytes,
            diagnostics.ActualGpuMemoryBudgetBytes,
            overBudget));

        if (overBudget.Length > 0)
        {
            Fail(
                frameIndex,
                $"Quality tier {expected} exceeded a release budget: {string.Join("; ", overBudget)}");
            return false;
        }

        return true;
    }

    private void Complete(int frameIndex)
    {
        _completed = true;
        _record(new SampleSmokeOperationResult(
            "quality-switch",
            "passed",
            frameIndex,
            $"tiers={string.Join(",", RequiredPresets)}, rollback={_initialPreset}, " +
            $"settings={_initialSettingsFingerprint}, rendererRestarted=false, " +
            $"deviceIdentity='{_initialDeviceIdentity}'"));
        _record(new SampleSmokeOperationResult(
            "device-loss-recovery",
            "rejected-unsupported",
            frameIndex,
            "No safe deterministic device-loss injection is exposed; unsafe driver/device fault injection was not attempted."));
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
            "quality-switch",
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
                    "Quality-switch rollback did not restore the complete render settings. " +
                    $"before={_initialSettingsFingerprint}, after={restoredFingerprint}.";
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
