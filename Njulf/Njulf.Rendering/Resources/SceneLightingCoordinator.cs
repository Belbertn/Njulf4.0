using Njulf.Assets.Scenes;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>Mirrors only scene-owned lights; advanced renderer registrations remain independent.</summary>
internal sealed class SceneLightingCoordinator(LightManager manager)
{
    private readonly record struct Mirror(
        LightHandle Handle,
        ulong Revision,
        SceneAssetReference? ProfileSource,
        SceneAssetReferenceDocument? ProfileDocument);

    private readonly Dictionary<Guid, Mirror> _mirrored = new();
    private readonly HashSet<Guid> _currentIds = new();
    private readonly List<Guid> _removedIds = new();
    private Scene? _scene;
    private SceneEnvironment? _appliedEnvironment;
    private SceneEnvironment? _rendererEnvironment;
    private ulong _lightRevision;

    public void Synchronize(Scene scene, EnvironmentSettings environment, Action<Light, Light> shadowEdit)
    {
        bool sceneChanged = !ReferenceEquals(scene, _scene);
        if (sceneChanged)
        {
            foreach (var entry in _mirrored.Values) manager.RemoveLight(entry.Handle);
            _mirrored.Clear();
            _scene = scene;
        }

        if (sceneChanged || !Equals(scene.Environment, _appliedEnvironment))
        {
            if (_appliedEnvironment == null && scene.Environment != null)
                _rendererEnvironment = SceneEnvironmentSettings.Capture(environment);
            if (scene.Environment != null)
                SceneEnvironmentSettings.Apply(scene.Environment, environment);
            else if (_rendererEnvironment != null)
                SceneEnvironmentSettings.Apply(_rendererEnvironment, environment);
            _appliedEnvironment = scene.Environment;
        }

        if (!sceneChanged && _lightRevision == scene.LightRevision) return;
        IReadOnlyList<SceneLight> lights = scene.Lights;
        _currentIds.Clear();
        for (int i = 0; i < lights.Count; i++) _currentIds.Add(lights[i].Id);
        _removedIds.Clear();
        foreach (Guid id in _mirrored.Keys)
            if (!_currentIds.Contains(id))
                _removedIds.Add(id);
        foreach (Guid id in _removedIds)
        {
            manager.RemoveLight(_mirrored[id].Handle);
            _mirrored.Remove(id);
        }

        for (int i = 0; i < lights.Count; i++)
        {
            SceneLight source = lights[i];
            if (_mirrored.TryGetValue(source.Id, out var existing) && existing.Revision == source.Revision &&
                manager.TryGetLightHandle(source.Id, out var currentHandle) &&
                currentHandle == existing.Handle) continue;
            SceneAssetReferenceDocument? profile = Equals(source.IesProfile, existing.ProfileSource)
                ? existing.ProfileDocument
                : source.IesProfile is { } reference
                    ? new SceneAssetReferenceDocument(reference.Path, reference.SubObject, reference.ContentHash)
                    : null;
            PhotometricProfileHandle profileHandle = default;
            if (profile is not null && manager.PhotometricProfiles is { } profiles)
                profiles.TryResolve(profile, out profileHandle);
            Light light = ResolveLight(source, profileHandle);
            Light previous = default;
            if (_mirrored.TryGetValue(source.Id, out existing)) manager.TryGetLight(existing.Handle, out previous);
            if (_mirrored.TryGetValue(source.Id, out existing) && manager.UpdateLight(existing.Handle, light))
            {
                manager.SetLightName(existing.Handle, source.Name);
                _mirrored[source.Id] = new(existing.Handle, source.Revision, source.IesProfile, profile);
            }
            else
            {
                LightHandle handle = manager.AddLightHandle(light, source.Name, source.Id);
                _mirrored[source.Id] = new(handle, source.Revision, source.IesProfile, profile);
            }

            shadowEdit(previous, light);
        }

        _lightRevision = scene.LightRevision;
    }

    private static Light ResolveLight(SceneLight source, PhotometricProfileHandle profile) => new()
    {
        Type = source.Type switch
        {
            SceneLightType.Point => LightType.Point,
            SceneLightType.Directional => LightType.Directional,
            SceneLightType.Spot => LightType.Spot,
            SceneLightType.Rectangle => LightType.Rectangle,
            SceneLightType.Disk => LightType.Disk,
            SceneLightType.Tube => LightType.Tube,
            _ => throw new ArgumentOutOfRangeException(nameof(source.Type))
        },
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
        AttenuationMode = source.AttenuationMode switch
        {
            SceneLightAttenuationMode.LegacyWindowed => LightAttenuationMode.LegacyWindowed,
            SceneLightAttenuationMode.InverseSquare => LightAttenuationMode.InverseSquare,
            SceneLightAttenuationMode.Polynomial => LightAttenuationMode.Polynomial,
            _ => throw new ArgumentOutOfRangeException(nameof(source.AttenuationMode))
        },
        AttenuationConstant = source.AttenuationConstant,
        AttenuationLinear = source.AttenuationLinear,
        AttenuationQuadratic = source.AttenuationQuadratic,
        CastsShadows = source.CastsShadows,
        ShadowStrength = source.ShadowStrength,
        ShadowMapSizeOverride = source.ShadowMapSizeOverride,
        ShadowNearPlane = source.ShadowNearPlane,
        ShadowFarPlane = source.ShadowFarPlane,
        ShadowPriority = source.ShadowPriority,
        PhotometricProfile = profile,
        IesRotationRadians = source.IesRotationRadians
    };
}