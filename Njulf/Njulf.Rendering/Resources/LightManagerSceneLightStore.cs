using System;
using System.Collections.Generic;
using Njulf.Assets.Scenes;

namespace Njulf.Rendering.Resources;

/// <summary>Connects source-scene light records to the renderer's packed, handle-based light store.</summary>
public sealed class LightManagerSceneLightStore : IMutableSceneLightStore
{
    private readonly LightManager _lights;
    private readonly IPhotometricProfileResolver? _photometricProfiles;
    private readonly Dictionary<Guid, SceneAssetReferenceDocument>
        _photometricSources = new();

    public LightManagerSceneLightStore(
        LightManager lights,
        IPhotometricProfileResolver? photometricProfiles = null)
    {
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        _photometricProfiles = photometricProfiles;
    }

    public void Clear()
    {
        _lights.ClearLights();
        _photometricSources.Clear();
    }

    public void Add(Guid id, SceneLightDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _lights.AddLightHandle(ToLight(source), source.Name, id);
        SetPhotometricSource(id, source.IesProfile);
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

        SetPhotometricSource(id, source.IesProfile);
        return _lights.SetLightName(handle, source.Name);
    }

    public bool TryRemove(Guid id)
    {
        bool removed = _lights.TryGetLightHandle(id, out LightHandle handle) &&
            _lights.RemoveLight(handle);
        if (removed)
            _photometricSources.Remove(id);
        return removed;
    }

    private Light ToLight(SceneLightDocument source) => new()
    {
        Type = ParseType(source.Type),
        Position = new System.Numerics.Vector3(source.Position.X, source.Position.Y, source.Position.Z),
        Direction = new System.Numerics.Vector3(source.Direction.X, source.Direction.Y, source.Direction.Z),
        Up = new System.Numerics.Vector3(source.Up.X, source.Up.Y, source.Up.Z),
        Size = new System.Numerics.Vector2(source.Size.X, source.Size.Y),
        TwoSided = source.TwoSided,
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
        ShadowPriority = source.ShadowPriority,
        PhotometricProfile = ResolvePhotometricProfile(source.IesProfile),
        IesRotationRadians = source.IesRotationRadians
    };

    private SceneLightDocument ToDocument(Guid id, string? name, Light source) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(name) ? "Light" : name,
        Type = source.Type.ToString(),
        Position = new SceneVector3(source.Position.X, source.Position.Y, source.Position.Z),
        Direction = new SceneVector3(source.Direction.X, source.Direction.Y, source.Direction.Z),
        Up = new SceneVector3(source.Up.X, source.Up.Y, source.Up.Z),
        Size = new SceneVector2(source.Size.X, source.Size.Y),
        TwoSided = source.TwoSided,
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
        ShadowPriority = source.ShadowPriority,
        IesProfile = AnalyticalLightGeometry.IsPunctual(source.Type)
            ? _photometricSources.TryGetValue(id, out var profileSource)
                ? profileSource
                : ResolvePhotometricProfileReference(source.PhotometricProfile)
            : null,
        IesRotationRadians = source.IesRotationRadians
    };

    private void SetPhotometricSource(
        Guid id,
        SceneAssetReferenceDocument? source)
    {
        if (source == null)
            _photometricSources.Remove(id);
        else
            _photometricSources[id] = source;
    }

    private PhotometricProfileHandle ResolvePhotometricProfile(
        SceneAssetReferenceDocument? source) =>
        source != null && ResolveProfileService() is { } profiles &&
        profiles.TryResolve(source, out PhotometricProfileHandle handle)
            ? handle
            : default;

    private SceneAssetReferenceDocument? ResolvePhotometricProfileReference(
        PhotometricProfileHandle handle) =>
        handle.IsValid && ResolveProfileService() is { } profiles &&
        profiles.TryGetReference(handle, out SceneAssetReferenceDocument? source)
            ? source
            : null;

    private IPhotometricProfileResolver? ResolveProfileService() =>
        _photometricProfiles ?? _lights.PhotometricProfiles;

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

public interface IPhotometricProfileResolver
{
    bool TryResolve(
        SceneAssetReferenceDocument source,
        out PhotometricProfileHandle handle);

    bool TryGetReference(
        PhotometricProfileHandle handle,
        out SceneAssetReferenceDocument? source);
}
