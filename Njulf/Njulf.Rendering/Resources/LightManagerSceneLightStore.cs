using System;
using System.Collections.Generic;
using Njulf.Assets.Scenes;

namespace Njulf.Rendering.Resources;

/// <summary>Connects source-scene light records to the renderer's packed, handle-based light store.</summary>
public sealed class LightManagerSceneLightStore : ISceneLightStore
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

    private static Light ToLight(SceneLightDocument source) => new()
    {
        Type = ParseType(source.Type),
        Position = new System.Numerics.Vector3(source.Position.X, source.Position.Y, source.Position.Z),
        Direction = new System.Numerics.Vector3(source.Direction.X, source.Direction.Y, source.Direction.Z),
        Color = new System.Numerics.Vector3(source.Color.X, source.Color.Y, source.Color.Z),
        Intensity = source.Intensity,
        Range = source.Range,
        SpotAngle = source.SpotAngle,
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
        CastsShadows = source.CastsShadows,
        ShadowStrength = source.ShadowStrength,
        ShadowMapSizeOverride = source.ShadowMapSizeOverride,
        ShadowNearPlane = source.ShadowNearPlane,
        ShadowFarPlane = source.ShadowFarPlane,
        ShadowPriority = source.ShadowPriority
    };

    private static LightType ParseType(string source) => Enum.TryParse(source, ignoreCase: true, out LightType value)
        ? value
        : throw new InvalidDataException($"Unsupported light type '{source}'.");
}
