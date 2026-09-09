using Njulf.Core.Scene;

namespace Njulf.Assets.Scenes;

/// <summary>Stores authored lights in the scene without requiring a renderer.</summary>
public sealed class SceneLightStore(Scene scene) : IMutableSceneLightStore
{
    public void Clear()
    {
        foreach (SceneLight light in scene.Lights.ToArray()) scene.Remove(light);
    }
    public void Add(Guid id, SceneLightDocument source) => scene.Add(FromDocument(id, source));
    public IEnumerable<SceneLightDocument> Enumerate() => scene.Lights.Select(ToDocument);
    public bool TryRemove(Guid id)
    {
        SceneLight? light = scene.Lights.FirstOrDefault(item => item.Id == id);
        if (light == null) return false;
        scene.Remove(light);
        return true;
    }
    public bool TryUpdate(Guid id, SceneLightDocument source)
    {
        SceneLight? light = scene.Lights.FirstOrDefault(item => item.Id == id);
        if (light == null) return false;
        SceneLight replacement = FromDocument(id, source);
        light.Name = replacement.Name;
        light.Type = replacement.Type;
        light.Position = replacement.Position;
        light.Direction = replacement.Direction;
        light.Up = replacement.Up;
        light.Size = replacement.Size;
        light.TwoSided = replacement.TwoSided;
        light.Color = replacement.Color;
        light.Intensity = replacement.Intensity;
        light.Range = replacement.Range;
        light.SpotAngle = replacement.SpotAngle;
        light.InnerSpotAngle = replacement.InnerSpotAngle;
        light.AttenuationMode = replacement.AttenuationMode;
        light.AttenuationConstant = replacement.AttenuationConstant;
        light.AttenuationLinear = replacement.AttenuationLinear;
        light.AttenuationQuadratic = replacement.AttenuationQuadratic;
        light.CastsShadows = replacement.CastsShadows;
        light.ShadowStrength = replacement.ShadowStrength;
        light.ShadowMapSizeOverride = replacement.ShadowMapSizeOverride;
        light.ShadowNearPlane = replacement.ShadowNearPlane;
        light.ShadowFarPlane = replacement.ShadowFarPlane;
        light.ShadowPriority = replacement.ShadowPriority;
        light.IesProfile = replacement.IesProfile;
        light.IesRotationRadians = replacement.IesRotationRadians;
        return true;
    }
    public static SceneLight FromDocument(Guid id, SceneLightDocument source) => new()
    {
        Id = id,
        Name = source.Name,
        Type = Enum.Parse<SceneLightType>(source.Type, ignoreCase: true),
        Position = new(source.Position.X, source.Position.Y, source.Position.Z),
        Direction = new(source.Direction.X, source.Direction.Y, source.Direction.Z),
        Up = new(source.Up.X, source.Up.Y, source.Up.Z),
        Size = new(source.Size.X, source.Size.Y),
        TwoSided = source.TwoSided,
        Color = new(source.Color.X, source.Color.Y, source.Color.Z),
        Intensity = source.Intensity,
        Range = source.Range,
        SpotAngle = source.SpotAngle,
        InnerSpotAngle = source.InnerSpotAngle,
        AttenuationMode = Enum.Parse<SceneLightAttenuationMode>(source.AttenuationMode, ignoreCase: true),
        AttenuationConstant = source.AttenuationConstant,
        AttenuationLinear = source.AttenuationLinear,
        AttenuationQuadratic = source.AttenuationQuadratic,
        CastsShadows = source.CastsShadows,
        ShadowStrength = source.ShadowStrength,
        ShadowMapSizeOverride = source.ShadowMapSizeOverride,
        ShadowNearPlane = source.ShadowNearPlane,
        ShadowFarPlane = source.ShadowFarPlane,
        ShadowPriority = source.ShadowPriority,
        IesProfile = source.IesProfile is { } profile ? new SceneAssetReference { Path = profile.Path, SubObject = profile.SubObject, ContentHash = profile.ContentHash } : null,
        IesRotationRadians = source.IesRotationRadians,
    };
    public static SceneLightDocument ToDocument(SceneLight source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Type = source.Type.ToString(),
        Position = new(source.Position.X, source.Position.Y, source.Position.Z),
        Direction = new(source.Direction.X, source.Direction.Y, source.Direction.Z),
        Up = new(source.Up.X, source.Up.Y, source.Up.Z),
        Size = new(source.Size.X, source.Size.Y),
        TwoSided = source.TwoSided,
        Color = new(source.Color.X, source.Color.Y, source.Color.Z),
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
        IesProfile = source.IesProfile is { } profile ? new SceneAssetReferenceDocument(profile.Path, profile.SubObject, profile.ContentHash) : null,
        IesRotationRadians = source.IesRotationRadians,
    };
}
