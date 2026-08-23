using System;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Scopes presentation settings that belong to the enclosed VFX showcase so
/// they cannot leak into another sample scene. Volumetric qualification is
/// owned by the renderer quality profile and is never changed here.
/// </summary>
internal sealed class SampleVfxVolumetricDemoOverride
{
    private bool _active;
    private bool _autoExposureEnabled;
    private float _exposure;
    private FogDebugView _fogDebugView;
    private FogDebugProjection _fogDebugProjection;
    private int _fogDebugSlice;

    public bool Active => _active;

    public void Enter(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!_active)
        {
            _autoExposureEnabled = settings.AutoExposure.Enabled;
            _exposure = settings.Exposure;
            _fogDebugView = settings.Fog.DebugView;
            _fogDebugProjection =
                settings.Fog.Volumetric.DebugProjection;
            _fogDebugSlice = settings.Fog.Volumetric.DebugSlice;
            _active = true;

            // Debug views are global renderer state. Starting the showcase in
            // one inherited from another scene can replace the beauty output
            // with a display-referred diagnostic (density in particular can
            // appear almost solid white). Enter the demo in its authored
            // presentation state; Apply intentionally leaves later, explicit
            // user/CLI debug choices alone while the scene is active.
            settings.Fog.DebugView = FogDebugView.None;
            settings.Fog.Volumetric.DebugProjection =
                FogDebugProjection.MaxAlongRay;
            settings.Fog.Volumetric.DebugSlice = -1;
        }

        SampleVfxShowcaseScene.ApplyPostQualityPreset(settings);
    }

    public void Apply(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!_active)
            throw new InvalidOperationException(
                "The volumetric showcase override must be entered before it is applied.");

        SampleVfxShowcaseScene.ApplyPostQualityPreset(settings);
    }

    public void Exit(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!_active)
            return;

        settings.AutoExposure.Enabled = _autoExposureEnabled;
        settings.Exposure = _exposure;
        settings.Fog.DebugView = _fogDebugView;
        settings.Fog.Volumetric.DebugProjection = _fogDebugProjection;
        settings.Fog.Volumetric.DebugSlice = _fogDebugSlice;
        _active = false;
    }
}
