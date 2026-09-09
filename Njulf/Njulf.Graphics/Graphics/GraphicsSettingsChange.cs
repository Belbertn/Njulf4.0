using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


/// <summary>A common-settings request. Null fields retain current values; presets precede explicit overrides.</summary>
public sealed record GraphicsSettingsChange
{
    /// <summary>Quality preset applied before individual fields. Null leaves this setting unchanged.</summary>
    public RenderQualityPreset? QualityPreset { get; init; }
    /// <summary>Linear resolution multiplier relative to the destination dimensions. Null leaves this setting unchanged.</summary>
    public float? ResolutionScale { get; init; }
    /// <summary>Manual linear exposure multiplier, used when automatic exposure is disabled. Null leaves this setting unchanged.</summary>
    public float? Exposure { get; init; }
    /// <summary>Display tone mapping operator. Null leaves this setting unchanged.</summary>
    public ToneMapper? ToneMapper { get; init; }
    /// <summary>Whether exposure adapts automatically to scene luminance. Null leaves this setting unchanged.</summary>
    public bool? AutoExposureEnabled { get; init; }
    /// <summary>Anti-aliasing technique; changes may rebuild temporal resources. Null leaves this setting unchanged.</summary>
    public AntiAliasingMode? AntiAliasingMode { get; init; }
    /// <summary>Screen-space ambient occlusion technique. Null leaves this setting unchanged.</summary>
    public AmbientOcclusionMode? AmbientOcclusionMode { get; init; }
    /// <summary>Whether scene shadow rendering is requested. Null leaves this setting unchanged.</summary>
    public bool? ShadowsEnabled { get; init; }
    /// <summary>Directional shadow map edge length in texels; changes require resource rebuilding. Null leaves this setting unchanged.</summary>
    public uint? DirectionalShadowMapSize { get; init; }
    /// <summary>Reflection technique requested for the scene. Null leaves this setting unchanged.</summary>
    public ReflectionMode? ReflectionMode { get; init; }
    /// <summary>Global illumination technique requested for the scene. Null leaves this setting unchanged.</summary>
    public GlobalIlluminationMode? GlobalIlluminationMode { get; init; }
    /// <summary>Async scheduling policy; capability support does not guarantee actual execution. Null leaves this setting unchanged.</summary>
    public AsyncComputeMode? AsyncComputeMode { get; init; }
    /// <summary>Whether GPU timing queries are requested. Null leaves this setting unchanged.</summary>
    public bool? GpuTimingEnabled { get; init; }
    /// <summary>Whether CPU diagnostic snapshots are requested. Null leaves this setting unchanged.</summary>
    public bool? CpuDiagnosticSnapshotsEnabled { get; init; }
    /// <summary>Diagnostic overlay to display. Null leaves this setting unchanged.</summary>
    public DebugOverlayMode? DebugOverlayMode { get; init; }
}
