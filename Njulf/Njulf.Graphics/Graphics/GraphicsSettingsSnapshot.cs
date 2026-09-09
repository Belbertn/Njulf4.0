using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Immutable requested values; effective feature status is available through capabilities.</summary>
public sealed record GraphicsSettingsSnapshot(
    RenderQualityPreset QualityPreset,
    float ResolutionScale,
    float Exposure,
    ToneMapper ToneMapper,
    bool AutoExposureEnabled,
    AntiAliasingMode AntiAliasingMode,
    AmbientOcclusionMode AmbientOcclusionMode,
    bool ShadowsEnabled,
    uint DirectionalShadowMapSize,
    ReflectionMode ReflectionMode,
    GlobalIlluminationMode GlobalIlluminationMode,
    AsyncComputeMode AsyncComputeMode,
    bool GpuTimingEnabled,
    bool CpuDiagnosticSnapshotsEnabled,
    DebugOverlayMode DebugOverlayMode);
