using Njulf.Assets.Scenes;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>Connects the scene's imported-shadow switch to the renderer gates and bounded budgets.</summary>
public sealed class ImportedLightShadowPolicy
{
    private ShadowSettings? _settings;
    private ModelLightRuntimeController? _owner;
    private Snapshot _previous;

    /// <summary>
    /// An explicit editor request enables the required local shadow pass without changing its budgets.
    /// Retain that choice when a temporary imported-light override is later restored.
    /// </summary>
    public void ApplyLightEdit(ShadowSettings settings, in Light previous, in Light current)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!current.CastsShadows || (previous.CastsShadows && previous.Type == current.Type))
            return;

        bool restorePrevious = ReferenceEquals(_settings, settings);
        if (current.Type == LightType.Point)
        {
            settings.PointShadowsEnabled = true;
            if (restorePrevious) _previous = _previous with { Point = true };
        }
        else if (current.Type == LightType.Spot)
        {
            settings.SpotShadowsEnabled = true;
            if (restorePrevious) _previous = _previous with { Spot = true };
        }
    }

    public void Apply(ShadowSettings settings, ModelLightRuntimeController? imported)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool enabled = imported is { ImportedModelLightShadowsEnabled: true, ActiveLightCount: > 0 };
        if (_settings != null && (!enabled || !ReferenceEquals(_owner, imported) ||
                                 !ReferenceEquals(_settings, settings)))
        {
            _previous.Restore(_settings);
            _settings = null;
            _owner = null;
        }
        if (!enabled) return;
        if (_settings == null)
        {
            _settings = settings;
            _owner = imported;
            _previous = Snapshot.Capture(settings);
        }

        int points = imported!.CountActiveLights(ModelLightType.Point);
        int spots = imported.CountActiveLights(ModelLightType.Spot);
        int areas = imported.CountActiveLights(ModelLightType.Rectangle) +
                    imported.CountActiveLights(ModelLightType.Disk) +
                    imported.CountActiveLights(ModelLightType.Tube);
        if (points > 0)
        {
            settings.PointShadowsEnabled = true;
        }
        if (spots > 0)
        {
            settings.SpotShadowsEnabled = true;
        }
        if (areas > 0)
        {
            settings.AreaShadowsEnabled = true;
            settings.MaxShadowedAreaLights = Math.Max(settings.MaxShadowedAreaLights, areas);
        }
        if (imported.CountActiveLights(ModelLightType.Directional) > 0)
            settings.DirectionalShadowsEnabled = true;
    }

    private readonly record struct Snapshot(
        bool Directional, bool Point, bool Spot, bool Area,
        int AreaBudget)
    {
        public static Snapshot Capture(ShadowSettings settings) => new(
            settings.DirectionalShadowsEnabled, settings.PointShadowsEnabled,
            settings.SpotShadowsEnabled, settings.AreaShadowsEnabled,
            settings.MaxShadowedAreaLights);

        public void Restore(ShadowSettings settings)
        {
            settings.DirectionalShadowsEnabled = Directional;
            settings.PointShadowsEnabled = Point;
            settings.SpotShadowsEnabled = Spot;
            settings.AreaShadowsEnabled = Area;
            settings.MaxShadowedAreaLights = AreaBudget;
        }
    }
}
