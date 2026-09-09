using Njulf.Assets.Scenes;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>Mirrors only scene-owned lights; advanced renderer registrations remain independent.</summary>
internal sealed class SceneLightingCoordinator(LightManager manager)
{
    private readonly LightManagerSceneLightStore _adapter = new(manager);
    private readonly Dictionary<Guid, (LightHandle Handle, ulong Revision)> _mirrored = new();
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
        var currentIds = scene.Lights.Select(light => light.Id).ToHashSet();
        foreach (Guid id in _mirrored.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            manager.RemoveLight(_mirrored[id].Handle);
            _mirrored.Remove(id);
        }
        foreach (SceneLight source in scene.Lights)
        {
            if (_mirrored.TryGetValue(source.Id, out var existing) && existing.Revision == source.Revision &&
                manager.TryGetLightHandle(source.Id, out var currentHandle) && currentHandle == existing.Handle) continue;
            Light light = _adapter.Resolve(SceneLightStore.ToDocument(source));
            Light previous = default;
            if (_mirrored.TryGetValue(source.Id, out existing)) manager.TryGetLight(existing.Handle, out previous);
            if (_mirrored.TryGetValue(source.Id, out existing) && manager.UpdateLight(existing.Handle, light))
            {
                manager.SetLightName(existing.Handle, source.Name);
                _mirrored[source.Id] = (existing.Handle, source.Revision);
            }
            else
            {
                LightHandle handle = manager.AddLightHandle(light, source.Name, source.Id);
                _mirrored[source.Id] = (handle, source.Revision);
            }
            shadowEdit(previous, light);
        }
        _lightRevision = scene.LightRevision;
    }
}
