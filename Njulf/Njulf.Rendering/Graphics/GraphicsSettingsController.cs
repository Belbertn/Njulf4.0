using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;

namespace Njulf.Graphics;

/// <summary>
/// Device-thread settings controller. One request may be pending. Cancellation is honored
/// until frame-boundary processing starts; completion follows resource preparation.
/// Legacy RenderSettings writes remain supported but do not produce receipts.
/// </summary>
internal sealed class VulkanGraphicsSettingsController : GraphicsSettingsController
{
    private readonly RenderSettings _settings;
    private readonly Action _ensureUsable;
    private Pending? _pending;
    private GraphicsSettingsResult? _processing;
    private bool _stopped;
    internal Func<RenderQualityPreset, string?>? ValidatePreset { get; init; }
    private sealed class Pending
    {
        internal readonly GraphicsSettingsChange Change;
        internal readonly TaskCompletionSource<GraphicsSettingsResult> Completion;
        private readonly CancellationToken _cancellation;
        private readonly CancellationTokenRegistration _registration;
        private int _state; // waiting, processing, settled
        internal Pending(GraphicsSettingsChange change, CancellationToken cancellation,
            TaskCompletionSource<GraphicsSettingsResult> completion)
        {
            Change = change; Completion = completion; _cancellation = cancellation;
            _registration = cancellation.Register(static state => ((Pending)state!).Cancel(), this);
        }
        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                Completion.TrySetCanceled(_cancellation);
        }
        internal bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        internal void Settle()
        { Interlocked.Exchange(ref _state, 2); _registration.Dispose(); }
    }
    internal VulkanGraphicsSettingsController(RenderSettings settings, Action ensureUsable)
    { _settings = settings; _ensureUsable = ensureUsable; }
    public override GraphicsSettingsSnapshot Current => Snapshot(_settings);
    public override GraphicsSettingsSnapshot Requested => _processing?.Requested ?? (_pending is { } p && !p.Completion.Task.IsCompleted ? Preview(p.Change).Requested : Current);
    public override bool IsPending => _pending != null && !_pending.Completion.Task.IsCompleted;

    public override GraphicsSettingsResult Preview(GraphicsSettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var candidate = _settings.CreateSnapshot();
        candidate.Debug.AllowGpuTiming = _settings.Debug.AllowGpuTiming;
        candidate.Debug.CpuSnapshotsEnabled = _settings.Debug.CpuSnapshotsEnabled;
        candidate.Debug.Mode = _settings.Debug.Mode;
        var fields = new List<GraphicsSettingsFieldResult>();
        try { ApplyValues(candidate, change); }
        catch (ArgumentException e) { return new(GraphicsSettingsOutcome.Rejected, Current,
            Array.AsReadOnly(new[] { new GraphicsSettingsFieldResult("Request", GraphicsSettingsImpact.Rejected, e.Message) }), e.Message); }
        if (change.QualityPreset.HasValue && !Equals(_settings.QualityPreset, candidate.QualityPreset))
            fields.Add(new(nameof(change.QualityPreset), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.ResolutionScale.HasValue && !Equals(_settings.ResolutionScale, candidate.ResolutionScale))
            fields.Add(new(nameof(change.ResolutionScale), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.Exposure.HasValue && !Equals(_settings.Exposure, candidate.Exposure))
            fields.Add(new(nameof(change.Exposure), GraphicsSettingsImpact.Runtime, "Applies before the next production frame."));
        if (change.ToneMapper.HasValue && !Equals(_settings.ToneMapper, candidate.ToneMapper))
            fields.Add(new(nameof(change.ToneMapper), GraphicsSettingsImpact.Runtime, "Applies before the next production frame."));
        if (change.AutoExposureEnabled.HasValue && !Equals(_settings.AutoExposure.Enabled, candidate.AutoExposure.Enabled))
            fields.Add(new(nameof(change.AutoExposureEnabled), GraphicsSettingsImpact.Runtime, "Applies before the next production frame."));
        if (change.AntiAliasingMode.HasValue && !Equals(_settings.AntiAliasing.Mode, candidate.AntiAliasing.Mode))
            fields.Add(new(nameof(change.AntiAliasingMode), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.AmbientOcclusionMode.HasValue && !Equals(_settings.AmbientOcclusion.Mode, candidate.AmbientOcclusion.Mode))
            fields.Add(new(nameof(change.AmbientOcclusionMode), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.ShadowsEnabled.HasValue &&
            (_settings.Shadows.DirectionalShadowsEnabled != candidate.Shadows.DirectionalShadowsEnabled ||
             _settings.Shadows.SpotShadowsEnabled != candidate.Shadows.SpotShadowsEnabled ||
             _settings.Shadows.PointShadowsEnabled != candidate.Shadows.PointShadowsEnabled ||
             _settings.Shadows.AreaShadowsEnabled != candidate.Shadows.AreaShadowsEnabled))
            fields.Add(new(nameof(change.ShadowsEnabled), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.DirectionalShadowMapSize.HasValue && !Equals(_settings.Shadows.DirectionalShadowMapSize, candidate.Shadows.DirectionalShadowMapSize))
            fields.Add(new(nameof(change.DirectionalShadowMapSize), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.ReflectionMode.HasValue && !Equals(_settings.Reflections.Mode, candidate.Reflections.Mode))
            fields.Add(new(nameof(change.ReflectionMode), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.GlobalIlluminationMode.HasValue && !Equals(_settings.GlobalIllumination.Mode, candidate.GlobalIllumination.Mode))
            fields.Add(new(nameof(change.GlobalIlluminationMode), GraphicsSettingsImpact.ResourceRebuild, "Uses existing renderer resource preparation."));
        if (change.AsyncComputeMode.HasValue && !Equals(_settings.AsyncCompute.Mode, candidate.AsyncCompute.Mode))
            fields.Add(new(nameof(change.AsyncComputeMode), GraphicsSettingsImpact.Runtime, "Applies before the next production frame."));
        if (change.GpuTimingEnabled.HasValue && !Equals(_settings.Debug.AllowGpuTiming, candidate.Debug.AllowGpuTiming))
            fields.Add(new(nameof(change.GpuTimingEnabled), GraphicsSettingsImpact.Runtime, "Applies before the next production frame."));
        if (change.CpuDiagnosticSnapshotsEnabled.HasValue && !Equals(_settings.Debug.CpuSnapshotsEnabled, candidate.Debug.CpuSnapshotsEnabled))
            fields.Add(new(nameof(change.CpuDiagnosticSnapshotsEnabled), GraphicsSettingsImpact.Runtime, "Applies before the next production frame."));
        if (change.DebugOverlayMode.HasValue && !Equals(_settings.Debug.Mode, candidate.Debug.Mode))
            fields.Add(new(nameof(change.DebugOverlayMode), GraphicsSettingsImpact.Runtime, "Applies before the next production frame."));
        // Presets can change immutable, startup-admitted experimental graph branches.
        var before = _settings.GlobalIllumination;
        var after = candidate.GlobalIllumination;
        if (before.DdgiOpacityMicromapMode != after.DdgiOpacityMicromapMode ||
            before.SimpleDdgiDirectionalGuidingMode != after.SimpleDdgiDirectionalGuidingMode ||
            before.GiCausticMode != after.GiCausticMode ||
            before.SimpleDdgiNearFieldResidualMode != after.SimpleDdgiNearFieldResidualMode ||
            before.SimpleDdgiNearFieldResidualQualityPreset != after.SimpleDdgiNearFieldResidualQualityPreset)
            fields.Add(new(nameof(change.QualityPreset), GraphicsSettingsImpact.RestartRequired,
                "The preset changes startup-admitted graph branches or their resource profile."));
        bool persistenceChanged = _settings.ComputePersistenceSha256() != candidate.ComputePersistenceSha256();
        if (persistenceChanged && fields.Count == 0)
            fields.Add(new("Preset overrides", GraphicsSettingsImpact.ResourceRebuild, "The preset resets advanced quality values."));
        if (change.QualityPreset is { } requestedPreset && ValidatePreset?.Invoke(requestedPreset) is { } rejection)
            fields.Add(new(nameof(change.QualityPreset), GraphicsSettingsImpact.Rejected, rejection));
        var outcome = fields.Any(f => f.Impact == GraphicsSettingsImpact.Rejected) ? GraphicsSettingsOutcome.Rejected :
            fields.Any(f => f.Impact == GraphicsSettingsImpact.RestartRequired)
            ? GraphicsSettingsOutcome.RestartRequired : fields.Count == 0
            ? GraphicsSettingsOutcome.NoChange : fields.Any(f => f.Impact == GraphicsSettingsImpact.ResourceRebuild)
            ? GraphicsSettingsOutcome.Rebuilt : GraphicsSettingsOutcome.Applied;
        return new(outcome, Snapshot(candidate), fields.AsReadOnly());
    }

    public override Task<GraphicsSettingsResult> ApplyAsync(GraphicsSettingsChange change, CancellationToken cancellationToken = default)
    {
        _ensureUsable();
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<GraphicsSettingsResult>(cancellationToken);
        if (_pending?.Completion.Task.IsCanceled == true) { _pending.Settle(); _pending = null; }
        var preview = Preview(change);
        if (_pending != null) return Task.FromResult(preview with { Outcome = GraphicsSettingsOutcome.Rejected, Reason = "A settings request is already pending." });
        if (preview.Outcome is GraphicsSettingsOutcome.Rejected or GraphicsSettingsOutcome.RestartRequired or GraphicsSettingsOutcome.NoChange)
            return Task.FromResult(preview);
        var completion = new TaskCompletionSource<GraphicsSettingsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = new(change, cancellationToken, completion);
        return completion.Task;
    }

    internal void BeginFrame()
    {
        if (_processing != null) return;
        if (_pending is not { } pending) return;
        if (!pending.TryStart()) { _pending = null; pending.Settle(); return; }
        _processing = Preview(pending.Change); // Re-evaluate legacy writes made since admission.
        if (_processing.Outcome is GraphicsSettingsOutcome.Rejected or GraphicsSettingsOutcome.RestartRequired or GraphicsSettingsOutcome.NoChange)
        { CompleteFrame(); return; }
        try { ApplyValues(_settings, pending.Change); }
        catch (Exception e) { Fail(e); throw; }
    }
    internal void CompleteFrame()
    {
        if (_processing is not { } result || _pending is not { } pending) return;
        _pending = null; _processing = null;
        pending.Settle();
        pending.Completion.TrySetResult(result);
    }
    internal void Fail(Exception failure)
    {
        if (_pending is not { } pending) return;
        _pending = null;
        var result = _processing ?? new(GraphicsSettingsOutcome.Failed, Current, Array.Empty<GraphicsSettingsFieldResult>());
        _processing = null;
        pending.Settle();
        pending.Completion.TrySetResult(result with { Outcome = GraphicsSettingsOutcome.Failed, Reason = failure.Message });
    }
    internal void Shutdown()
    {
        _stopped = true;
        Fail(new ObjectDisposedException(nameof(VulkanGraphicsSettingsController)));
    }
    internal static GraphicsSettingsSnapshot Snapshot(RenderSettings s) => new(
        s.QualityPreset,
        s.ResolutionScale,
        s.Exposure,
        s.ToneMapper,
        s.AutoExposure.Enabled,
        s.AntiAliasing.Mode,
        s.AmbientOcclusion.Mode,
        ShadowsEnabled(s),
        s.Shadows.DirectionalShadowMapSize,
        s.Reflections.Mode,
        s.GlobalIllumination.Mode,
        s.AsyncCompute.Mode,
        s.Debug.AllowGpuTiming,
        s.Debug.CpuSnapshotsEnabled,
        s.Debug.Mode);
    private static bool ShadowsEnabled(RenderSettings s) => s.Shadows.DirectionalShadowsEnabled && s.Shadows.SpotShadowsEnabled && s.Shadows.PointShadowsEnabled && s.Shadows.AreaShadowsEnabled;
    private static void ApplyValues(RenderSettings s, GraphicsSettingsChange c)
    {
        if (c.QualityPreset is { } enumQualityPreset && !Enum.IsDefined(enumQualityPreset)) throw new ArgumentOutOfRangeException(nameof(c.QualityPreset));
        if (c.ToneMapper is { } enumToneMapper && !Enum.IsDefined(enumToneMapper)) throw new ArgumentOutOfRangeException(nameof(c.ToneMapper));
        if (c.AntiAliasingMode is { } enumAntiAliasingMode && !Enum.IsDefined(enumAntiAliasingMode)) throw new ArgumentOutOfRangeException(nameof(c.AntiAliasingMode));
        if (c.AmbientOcclusionMode is { } enumAmbientOcclusionMode && !Enum.IsDefined(enumAmbientOcclusionMode)) throw new ArgumentOutOfRangeException(nameof(c.AmbientOcclusionMode));
        if (c.ReflectionMode is { } enumReflectionMode && !Enum.IsDefined(enumReflectionMode)) throw new ArgumentOutOfRangeException(nameof(c.ReflectionMode));
        if (c.GlobalIlluminationMode is { } enumGlobalIlluminationMode && !Enum.IsDefined(enumGlobalIlluminationMode)) throw new ArgumentOutOfRangeException(nameof(c.GlobalIlluminationMode));
        if (c.AsyncComputeMode is { } enumAsyncComputeMode && !Enum.IsDefined(enumAsyncComputeMode)) throw new ArgumentOutOfRangeException(nameof(c.AsyncComputeMode));
        if (c.DebugOverlayMode is { } enumDebugOverlayMode && !Enum.IsDefined(enumDebugOverlayMode)) throw new ArgumentOutOfRangeException(nameof(c.DebugOverlayMode));
        if (c.Exposure is { } exposure && !float.IsFinite(exposure)) throw new ArgumentOutOfRangeException(nameof(c.Exposure));
        if (c.ResolutionScale is { } scale && !float.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(c.ResolutionScale));
        if (c.QualityPreset is { } preset) s.ApplyQualityPreset(preset);
        if (c.ResolutionScale is { } valueResolutionScale) s.ResolutionScale = valueResolutionScale;
        if (c.Exposure is { } valueExposure) s.Exposure = valueExposure;
        if (c.ToneMapper is { } valueToneMapper) s.ToneMapper = valueToneMapper;
        if (c.AutoExposureEnabled is { } valueAutoExposureEnabled) s.AutoExposure.Enabled = valueAutoExposureEnabled;
        if (c.AntiAliasingMode is { } valueAntiAliasingMode) s.AntiAliasing.Mode = valueAntiAliasingMode;
        if (c.AmbientOcclusionMode is { } valueAmbientOcclusionMode) s.AmbientOcclusion.Mode = valueAmbientOcclusionMode;
        if (c.ShadowsEnabled is { } valueShadowsEnabled) s.Shadows.DirectionalShadowsEnabled = s.Shadows.SpotShadowsEnabled = s.Shadows.PointShadowsEnabled = s.Shadows.AreaShadowsEnabled = valueShadowsEnabled;
        if (c.DirectionalShadowMapSize is { } valueDirectionalShadowMapSize) s.Shadows.DirectionalShadowMapSize = valueDirectionalShadowMapSize;
        if (c.ReflectionMode is { } valueReflectionMode) s.Reflections.Mode = valueReflectionMode;
        if (c.GlobalIlluminationMode is { } valueGlobalIlluminationMode) s.GlobalIllumination.Mode = valueGlobalIlluminationMode;
        if (c.AsyncComputeMode is { } valueAsyncComputeMode) s.AsyncCompute.Mode = valueAsyncComputeMode;
        if (c.GpuTimingEnabled is { } valueGpuTimingEnabled) s.Debug.AllowGpuTiming = valueGpuTimingEnabled;
        if (c.CpuDiagnosticSnapshotsEnabled is { } valueCpuDiagnosticSnapshotsEnabled) s.Debug.CpuSnapshotsEnabled = valueCpuDiagnosticSnapshotsEnabled;
        if (c.DebugOverlayMode is { } valueDebugOverlayMode) s.Debug.Mode = valueDebugOverlayMode;
        if (c.DebugOverlayMode.HasValue) s.Debug.Enabled = s.Debug.Mode != DebugOverlayMode.None;
    }
}
