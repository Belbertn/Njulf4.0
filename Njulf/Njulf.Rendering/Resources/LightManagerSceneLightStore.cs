using System;
using System.Collections.Generic;
using Njulf.Assets.Scenes;

namespace Njulf.Rendering.Resources;

/// <summary>Connects source-scene light records to the renderer's packed, handle-based light store.</summary>
public sealed class LightManagerSceneLightStore : IMutableSceneLightStore
{
    private readonly LightManager _lights;

    public LightManagerSceneLightStore(LightManager lights)
    {
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
    }

    public void Clear() => _lights.ClearLights();

    public void Add(Guid id, SceneLightDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _lights.AddLightHandle(ToLight(source), source.Name, id);
    }

    public IEnumerable<SceneLightDocument> Enumerate()
    {
        foreach (LightRecord record in _lights.GetLightRecords())
            yield return ToDocument(record.Id, record.Name, record.Light);
    }

    public bool TryUpdate(Guid id, SceneLightDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_lights.TryGetLightHandle(id, out LightHandle handle) ||
            !_lights.UpdateLight(handle, ToLight(source)))
        {
            return false;
        }

        return _lights.SetLightName(handle, source.Name);
    }

    public bool TryRemove(Guid id) =>
        _lights.TryGetLightHandle(id, out LightHandle handle) &&
        _lights.RemoveLight(handle);

    private static Light ToLight(SceneLightDocument source) => new()
    {
        Type = ParseType(source.Type),
        Position = new System.Numerics.Vector3(source.Position.X, source.Position.Y, source.Position.Z),
        Direction = new System.Numerics.Vector3(source.Direction.X, source.Direction.Y, source.Direction.Z),
        Color = new System.Numerics.Vector3(source.Color.X, source.Color.Y, source.Color.Z),
        Intensity = source.Intensity,
        Range = source.Range,
        SpotAngle = source.SpotAngle,
        InnerSpotAngle = source.InnerSpotAngle,
        AttenuationMode = ParseAttenuationMode(source.AttenuationMode),
        AttenuationConstant = source.AttenuationConstant,
        AttenuationLinear = source.AttenuationLinear,
        AttenuationQuadratic = source.AttenuationQuadratic,
        CastsShadows = source.CastsShadows,
        ShadowStrength = source.ShadowStrength,
        ShadowMapSizeOverride = source.ShadowMapSizeOverride,
        ShadowNearPlane = source.ShadowNearPlane,
        ShadowFarPlane = source.ShadowFarPlane,
        ShadowPriority = source.ShadowPriority
    };

    private static SceneLightDocument ToDocument(Guid id, string? name, Light source) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(name) ? "Light" : name,
        Type = source.Type.ToString(),
        Position = new SceneVector3(source.Position.X, source.Position.Y, source.Position.Z),
        Direction = new SceneVector3(source.Direction.X, source.Direction.Y, source.Direction.Z),
        Color = new SceneVector3(source.Color.X, source.Color.Y, source.Color.Z),
        Intensity = source.Intensity,
        Range = source.Range,
        SpotAngle = source.SpotAngle,
        InnerSpotAngle = source.InnerSpotAngle,
        AttenuationMode = source.AttenuationMode.ToString(),
        AttenuationConstant = source.AttenuationConstant,
        AttenuationLinear = source.AttenuationLinear,
        AttenuationQuadratic = source.AttenuationQuadratic,
        CastsShadows = source.CastsShadows,
        ShadowStrength = source.ShadowStrength,
        ShadowMapSizeOverride = source.ShadowMapSizeOverride,
        ShadowNearPlane = source.ShadowNearPlane,
        ShadowFarPlane = source.ShadowFarPlane,
        ShadowPriority = source.ShadowPriority
    };

    private static LightType ParseType(string source) =>
        Enum.TryParse(source, ignoreCase: true, out LightType value) &&
        Enum.IsDefined(value)
        ? value
        : throw new InvalidDataException($"Unsupported light type '{source}'.");

    private static LightAttenuationMode ParseAttenuationMode(string source) =>
        Enum.TryParse(
            source,
            ignoreCase: true,
            out LightAttenuationMode value) &&
        Enum.IsDefined(value)
            ? value
            : throw new InvalidDataException(
                $"Unsupported light attenuation mode '{source}'.");
}
