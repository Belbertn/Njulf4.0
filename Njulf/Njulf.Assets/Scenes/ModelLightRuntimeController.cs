using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Assets.Scenes;

/// <summary>
/// Owns the aggregate set of lights imported from ordinary model placements in
/// a scene. Model sub-objects that share an asset and world transform are
/// treated as one placement, so flattened models do not duplicate their lights.
/// </summary>
public sealed class ModelLightRuntimeController : IUpdateable, IDisposable
{
    private readonly Scene _scene;
    private readonly IContentManager _content;
    private readonly IMutableSceneLightStore _store;
    private readonly IMutableSceneLightStore _instanceStore;
    private readonly Dictionary<Guid, SceneLightDocument> _importedSourceLights = [];
    private readonly Dictionary<Guid, SceneImportedLightOverrideDocument> _lightOverrides = [];
    private readonly Func<string, Model> _loadModel;
    private readonly Dictionary<Guid, ActivePlacement> _activePlacements = [];
    private readonly HashSet<Guid> _activeLightIds = [];
    private readonly List<SceneLightDocument> _suspendedDirectionalLights = [];
    private ActivePlacement? _directionalPlacement;
    private ModelLightDefinition? _directionalDefinition;
    private bool _disposed;
    private bool _reconciling;
    private bool _refreshPending;

    private ModelLightRuntimeController(
        Scene scene,
        IContentManager content,
        IMutableSceneLightStore store,
        Func<string, Model>? loadModel)
    {
        _scene = scene;
        _content = content;
        _store = store;
        _instanceStore = new ImportedOverrideLightStore(this);
        _loadModel = loadModel ?? LoadModelFromContent;
        _scene.Mutated += OnSceneMutated;
        try
        {
            Refresh();
        }
        catch
        {
            _scene.Mutated -= OnSceneMutated;
            throw;
        }
    }

    /// <summary>Gets whether imported non-directional model lights are active.</summary>
    public bool ImportedModelLightsEnabled { get; private set; }

    /// <summary>Uses one imported directional light in place of authored directional lights.</summary>
    public bool ImportedDirectionalLightEnabled { get; private set; }

    /// <summary>Temporarily forces shadows on for all imported lights, preserving individual settings.</summary>
    public bool ImportedModelLightShadowsEnabled { get; private set; }

    public void SetImportedModelLightShadowsEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled == ImportedModelLightShadowsEnabled) return;
        SceneLightDocument[] previous = _store.Enumerate()
            .Where(light => IsImportedLight(light.Id)).ToArray();
        int updated = 0;
        try
        {
            for (; updated < previous.Length; updated++)
            {
                Guid id = previous[updated].Id;
                if (!_store.TryUpdate(id, ApplyLightSettings(_importedSourceLights[id], enabled)))
                    throw new InvalidOperationException($"Could not change imported light shadows for '{id}'.");
            }
            ImportedModelLightShadowsEnabled = enabled;
            LastError = null;
        }
        catch (Exception failure)
        {
            LastError = failure.Message;
            List<Exception> failures = [failure];
            for (int index = updated - 1; index >= 0; index--)
            {
                try
                {
                    if (!_store.TryUpdate(previous[index].Id, previous[index]))
                        throw new InvalidOperationException($"Could not restore imported light '{previous[index].Id}'.");
                }
                catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
            }
            if (failures.Count > 1)
                throw new AggregateException("Imported shadow settings and rollback both failed.", failures);
            throw;
        }
    }

    public int ImportedDirectionalLightDefinitionCount { get; private set; }

    /// <summary>Imported definitions that need a default intensity when explicitly enabled.</summary>
    public int ImportedZeroIntensityLightDefinitionCount { get; private set; }

    internal IReadOnlyList<SceneLightDocument> SuspendedDirectionalLights =>
        _suspendedDirectionalLights;

    public IReadOnlyList<SceneLightDocument> GetSuspendedDirectionalLights() =>
        _suspendedDirectionalLights.ToArray();

    public bool TryUpdateSuspendedDirectionalLight(SceneLightDocument light)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int index = _suspendedDirectionalLights.FindIndex(item => item.Id == light.Id);
        if (index < 0) return false;
        if (!string.Equals(light.Type, "Directional", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore the scene light before changing its type.");
        _suspendedDirectionalLights[index] = light;
        return true;
    }

    public IReadOnlyCollection<SceneImportedLightOverrideDocument> LightOverrides => _lightOverrides.Values;

    public bool HasLightOverride(Guid id) => _lightOverrides.ContainsKey(id);

    public bool TryUpdateImportedLight(SceneLightDocument light)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsImportedLight(light.Id) || !_importedSourceLights.TryGetValue(light.Id, out var source))
            return false;
        // Directional ownership is controlled by the separate sun switch.
        if (string.Equals(source.Type, "Directional", StringComparison.OrdinalIgnoreCase) !=
            string.Equals(light.Type, "Directional", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use the imported directional light toggle to switch the scene sun.");
        if (ImportedModelLightShadowsEnabled)
        {
            // The inspector sees the forced flag. Editing another setting must
            // not turn that temporary flag into a persistent per-light override.
            bool individualShadows = _lightOverrides.TryGetValue(light.Id, out var previous)
                ? previous.Apply(source).CastsShadows : source.CastsShadows;
            light = WithShadowFlag(light, individualShadows);
        }
        if (!_store.TryUpdate(light.Id,
                ImportedModelLightShadowsEnabled ? WithShadowFlag(light, true) : light))
            return false;
        _lightOverrides[light.Id] = new SceneImportedLightOverrideDocument(light.Id, source, light);
        return true;
    }

    public bool ResetLightOverride(Guid id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_lightOverrides.ContainsKey(id))
            return false;
        if (_importedSourceLights.TryGetValue(id, out var source) && !_store.TryUpdate(id,
                ImportedModelLightShadowsEnabled ? WithShadowFlag(source, true) : source))
            return false;
        return _lightOverrides.Remove(id);
    }

    internal void LoadLightOverrides(IEnumerable<SceneImportedLightOverrideDocument> overrides)
    {
        foreach (var item in overrides)
            _lightOverrides.Add(item.Id, item);
    }

    /// <summary>Number of distinct ordinary model placements in the scene.</summary>
    public int ModelPlacementCount { get; private set; }

    /// <summary>Number of placements whose model contains imported lights.</summary>
    public int ModelPlacementsWithLightsCount { get; private set; }

    /// <summary>Total imported light definitions across all placements.</summary>
    public int ImportedLightDefinitionCount { get; private set; }

    /// <summary>Number of imported lights currently present in the live store.</summary>
    public int ActiveLightCount => _activeLightIds.Count +
        (_directionalPlacement?.Instance.LightIds.Count ?? 0);

    public int CountActiveLights(ModelLightType type)
    {
        string typeName = type.ToString();
        int count = 0;
        foreach (SceneLightDocument source in _importedSourceLights.Values)
        {
            string effectiveType = _lightOverrides.TryGetValue(source.Id, out var edits) &&
                                   edits.Values.Type != edits.Source.Type
                ? edits.Values.Type : source.Type;
            if (string.Equals(effectiveType, typeName, StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    /// <summary>The last automatic reconciliation failure, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Attaches one scene-owned controller, or returns the controller already
    /// attached to the scene.
    /// </summary>
    public static ModelLightRuntimeController Attach(
        Scene scene,
        IContentManager content,
        IMutableSceneLightStore store,
        Func<string, Model>? loadModel = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(store);

        if (scene.GetComponent<ModelLightRuntimeController>() is { } existing)
            return existing;

        var controller = new ModelLightRuntimeController(
            scene,
            content,
            store,
            loadModel);
        try
        {
            scene.Add((IUpdateable)controller);
            return controller;
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    /// <summary>Returns true when a live light is owned by this controller.</summary>
    public bool IsImportedLight(Guid id) => _activeLightIds.Contains(id) ||
        _directionalPlacement?.Instance.LightIds.Contains(id) == true;

    /// <summary>Immediately enables or disables imported non-directional model lights.</summary>
    public void SetImportedModelLightsEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled == ImportedModelLightsEnabled)
            return;

        if (enabled)
        {
            IReadOnlyDictionary<Guid, DesiredPlacement> desired = DiscoverPlacements();
            try
            {
                ReconcileEnabledPlacements(desired);
                ImportedModelLightsEnabled = true;
                LastError = null;
            }
            catch (Exception failure)
            {
                LastError = failure.Message;
                throw;
            }
        }
        else
        {
            try
            {
                RemoveAllActivePlacements();
                ImportedModelLightsEnabled = false;
                LastError = null;
            }
            catch (Exception failure)
            {
                LastError = failure.Message;
                throw;
            }
        }
    }

    public void SetImportedDirectionalLightEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled == ImportedDirectionalLightEnabled)
            return;

        try
        {
            if (enabled)
                ReconcileDirectionalLight(DiscoverPlacements());
            else
                RestoreDirectionalLights();
            ImportedDirectionalLightEnabled = enabled && _directionalPlacement != null;
            LastError = null;
        }
        catch (Exception failure)
        {
            LastError = failure.Message;
            throw;
        }
    }

    /// <summary>Rediscovers placements and reconciles live imported lights.</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reconciling)
            return;

        _reconciling = true;
        try
        {
            IReadOnlyDictionary<Guid, DesiredPlacement> desired = DiscoverPlacements();
            if (ImportedModelLightsEnabled)
                ReconcileEnabledPlacements(desired);
            if (ImportedDirectionalLightEnabled)
                ReconcileDirectionalLight(desired);
            _refreshPending = false;
            LastError = null;
        }
        catch (Exception failure)
        {
            LastError = failure.Message;
            throw;
        }
        finally
        {
            _reconciling = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _scene.Mutated -= OnSceneMutated;
        _disposed = true;
        RemoveAllActivePlacements();
        RestoreDirectionalLights();
        ImportedModelLightsEnabled = false;
        ImportedDirectionalLightEnabled = false;
    }

    bool IUpdateable.Enabled { get; set; }
    int IUpdateable.UpdateOrder { get; set; }
    void IUpdateable.Update(float deltaTime)
    {
        if (_disposed || _reconciling || !_refreshPending)
            return;

        _refreshPending = false;
        try
        {
            Refresh();
        }
        catch
        {
            // The scene mutation has already committed. Refresh preserves the
            // prior active-light set transactionally and records LastError.
            // A later mutation will schedule another reconciliation attempt.
        }
    }

    private void OnSceneMutated(SceneMutation mutation)
    {
        const SceneMutationKind relevant =
            SceneMutationKind.Added |
            SceneMutationKind.Removed |
            SceneMutationKind.Transform |
            SceneMutationKind.Geometry;
        if (_disposed || _reconciling || mutation.Producer is not RenderObject ||
            (mutation.Kind & relevant) == 0)
        {
            return;
        }

        if (mutation.Kind.HasFlag(SceneMutationKind.Added) ||
            mutation.Kind.HasFlag(SceneMutationKind.Removed))
        {
            // Flattened imported models can publish thousands of sub-objects
            // in one host update. Coalesce those structural changes so light
            // discovery scans the scene once on the next update instead of
            // once per object (which otherwise becomes O(n^2)).
            _refreshPending = true;
            return;
        }

        try
        {
            Refresh();
        }
        catch
        {
            // Scene mutation has already committed. Keep the previous imported
            // light set intact and surface the failure through LastError.
        }
    }

    private IReadOnlyDictionary<Guid, DesiredPlacement> DiscoverPlacements()
    {
        var groups = new Dictionary<PlacementGroupKey, PlacementGroup>();
        foreach (RenderObject renderObject in _scene.RenderObjects)
        {
            SceneAssetReference? asset = renderObject.AssetReference;
            if (asset == null || string.IsNullOrWhiteSpace(asset.Path))
                continue;

            string assetKey = NormalizeAssetKey(asset.Path);
            var key = new PlacementGroupKey(assetKey, renderObject.WorldMatrix);
            if (!groups.TryGetValue(key, out PlacementGroup? group))
            {
                group = new PlacementGroup(asset.Path, assetKey, renderObject.WorldMatrix);
                groups.Add(key, group);
            }
            group.Include(renderObject.Id);
        }

        var models = new Dictionary<string, Model>(StringComparer.Ordinal);
        var desired = new Dictionary<Guid, DesiredPlacement>();
        int placementsWithLights = 0;
        int definitionCount = 0;
        int directionalCount = 0;
        int zeroIntensityCount = 0;
        foreach (PlacementGroup group in groups.Values)
        {
            if (!models.TryGetValue(group.AssetKey, out Model? model))
            {
                model = _loadModel(group.AssetPath) ??
                    throw new InvalidOperationException(
                        $"Could not load model '{group.AssetPath}' while discovering imported lights.");
                models.Add(group.AssetKey, model);
            }

            Guid placementId = CreatePlacementId(group.AnchorId, group.AssetKey);
            if (!desired.TryAdd(
                    placementId,
                    new DesiredPlacement(
                        placementId,
                        group.WorldTransform,
                        model)))
            {
                throw new InvalidOperationException(
                    $"Model placement '{placementId}' is not unique.");
            }

            if (model.Lights.Count > 0)
                placementsWithLights++;
            definitionCount += model.Lights.Count;
            directionalCount += model.Lights.Count(light =>
                light.Type == ModelLightType.Directional);
            zeroIntensityCount += model.Lights.Count(light => light.Intensity == 0f);
        }

        ModelPlacementCount = groups.Count;
        ModelPlacementsWithLightsCount = placementsWithLights;
        ImportedLightDefinitionCount = definitionCount;
        ImportedDirectionalLightDefinitionCount = directionalCount;
        ImportedZeroIntensityLightDefinitionCount = zeroIntensityCount;
        return desired;
    }

    private Model LoadModelFromContent(string assetPath) =>
        _content.Load<Model>(assetPath) ??
        throw new InvalidOperationException(
            $"Could not load model '{assetPath}' while discovering imported lights.");

    private void ReconcileEnabledPlacements(
        IReadOnlyDictionary<Guid, DesiredPlacement> desired)
    {
        var created = new List<ActivePlacement>();
        var updated = new List<(ActivePlacement Placement, Matrix4x4 Previous)>();
        try
        {
            foreach (DesiredPlacement placement in desired.Values)
            {
                ModelLightDefinition[] definitions = placement.Model.Lights
                    .Where(light => light.Type != ModelLightType.Directional)
                    .Select(ActivateImportedLight).ToArray();
                if (definitions.Length == 0)
                    continue;

                if (_activePlacements.TryGetValue(
                        placement.PlacementId,
                        out ActivePlacement? active))
                {
                    if (!active.Instance.WorldTransform.Equals(
                            placement.WorldTransform))
                    {
                        Matrix4x4 previous = active.Instance.WorldTransform;
                        active.Instance.UpdateTransform(placement.WorldTransform);
                        updated.Add((active, previous));
                    }
                    continue;
                }

                ModelLightInstanceSet instance =
                    ModelLightInstanceSet.Create(
                        definitions,
                        _instanceStore,
                        placement.WorldTransform,
                        placement.PlacementId);
                created.Add(new ActivePlacement(
                    placement.PlacementId,
                    instance));
            }
        }
        catch (Exception activationFailure)
        {
            ThrowActivationFailureWithRollback(
                activationFailure,
                created,
                updated);
        }

        foreach (ActivePlacement placement in created)
            _activePlacements.Add(placement.PlacementId, placement);

        List<Exception>? removalFailures = null;
        Guid[] obsolete = _activePlacements.Keys
            .Where(id => !desired.TryGetValue(id, out DesiredPlacement? placement) ||
                !placement.Model.Lights.Any(light => light.Type != ModelLightType.Directional))
            .ToArray();
        foreach (Guid id in obsolete)
        {
            ActivePlacement placement = _activePlacements[id];
            try
            {
                placement.Instance.Dispose();
            }
            catch (Exception failure)
            {
                (removalFailures ??= []).Add(failure);
            }
            finally
            {
                _activePlacements.Remove(id);
            }
        }

        RebuildActiveLightIds();
        if (removalFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more obsolete imported model-light placements could not be removed.",
                removalFailures);
        }
    }

    private void ReconcileDirectionalLight(
        IReadOnlyDictionary<Guid, DesiredPlacement> desired)
    {
        // Keep the current choice while its placement exists. Otherwise select
        // deterministically, even when multiple models contain a sun.
        DesiredPlacement? selected = desired.Values
            .Where(placement => placement.Model.Lights.Any(light =>
                light.Type == ModelLightType.Directional))
            .OrderBy(placement => placement.PlacementId == _directionalPlacement?.PlacementId ? 0 : 1)
            .ThenBy(placement => placement.PlacementId)
            .FirstOrDefault();
        if (selected == null)
        {
            RestoreDirectionalLights();
            ImportedDirectionalLightEnabled = false;
            return;
        }

        ModelLightDefinition definition = ActivateImportedLight(
            selected.Model.Lights.First(light => light.Type == ModelLightType.Directional));
        if (_directionalPlacement?.PlacementId == selected.PlacementId &&
            _directionalDefinition == definition)
        {
            if (!_directionalPlacement.Instance.WorldTransform.Equals(selected.WorldTransform))
                _directionalPlacement.Instance.UpdateTransform(selected.WorldTransform);
            return;
        }

        ActivePlacement? previous = _directionalPlacement;
        ModelLightDefinition? previousDefinition = _directionalDefinition;
        SceneLightDocument[] authored = previous == null
            ? _store.Enumerate().Where(light => string.Equals(
                light.Type, "Directional", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        var removed = new List<SceneLightDocument>();
        try
        {
            foreach (SceneLightDocument light in authored)
            {
                if (!_store.TryRemove(light.Id))
                    throw new InvalidOperationException($"Could not suspend directional light '{light.Id}'.");
                removed.Add(light);
            }
            if (previous != null)
            {
                previous.Instance.Dispose();
                _directionalPlacement = null;
            }
            var instance = ModelLightInstanceSet.Create(
                [definition], _instanceStore, selected.WorldTransform,
                CreatePlacementId(selected.PlacementId, "directional"));
            _directionalPlacement = new ActivePlacement(selected.PlacementId, instance);
            _directionalDefinition = definition;
            _suspendedDirectionalLights.AddRange(removed);
        }
        catch (Exception failure)
        {
            try
            {
                if (previous != null && _directionalPlacement == null)
                    _directionalPlacement = new ActivePlacement(previous.PlacementId,
                        ModelLightInstanceSet.Create([previousDefinition!], _instanceStore,
                            previous.Instance.WorldTransform, previous.Instance.InstanceId));
                foreach (SceneLightDocument light in removed)
                    _store.Add(light.Id, light);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Directional light switching and rollback both failed.",
                    failure, rollbackFailure);
            }
            throw;
        }
    }

    private void RestoreDirectionalLights()
    {
        ActivePlacement? previous = _directionalPlacement;
        previous?.Instance.Dispose();
        _directionalPlacement = null;
        var restored = new List<SceneLightDocument>();
        try
        {
            foreach (SceneLightDocument light in _suspendedDirectionalLights)
            {
                _store.Add(light.Id, light);
                restored.Add(light);
            }
            _suspendedDirectionalLights.Clear();
            _directionalDefinition = null;
        }
        catch (Exception failure)
        {
            try
            {
                foreach (SceneLightDocument light in restored)
                    if (!_store.TryRemove(light.Id))
                        throw new InvalidOperationException($"Could not roll back restored light '{light.Id}'.");
                if (previous != null)
                    _directionalPlacement = new ActivePlacement(previous.PlacementId,
                        ModelLightInstanceSet.Create([_directionalDefinition!], _instanceStore,
                            previous.Instance.WorldTransform, previous.Instance.InstanceId));
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Directional light restoration and rollback both failed.",
                    failure, rollbackFailure);
            }
            throw;
        }
    }

    private static ModelLightDefinition ActivateImportedLight(ModelLightDefinition source)
    {
        // Some assets (including Sponza) export disabled lights as intensity
        // zero. Explicit activation gives those lights usable output without
        // rewriting imported metadata or changing positive authored values.
        return source.Intensity == 0f
            ? source with { Intensity = source.Type == ModelLightType.Directional ? 1f : 100f }
            : source;
    }

    private static void ThrowActivationFailureWithRollback(
        Exception activationFailure,
        IReadOnlyList<ActivePlacement> created,
        IReadOnlyList<(ActivePlacement Placement, Matrix4x4 Previous)> updated)
    {
        List<Exception>? rollbackFailures = null;
        for (int index = updated.Count - 1; index >= 0; index--)
        {
            try
            {
                updated[index].Placement.Instance.UpdateTransform(
                    updated[index].Previous);
            }
            catch (Exception failure)
            {
                (rollbackFailures ??= []).Add(failure);
            }
        }
        for (int index = created.Count - 1; index >= 0; index--)
        {
            try
            {
                created[index].Instance.Dispose();
            }
            catch (Exception failure)
            {
                (rollbackFailures ??= []).Add(failure);
            }
        }

        if (rollbackFailures is { Count: > 0 })
        {
            rollbackFailures.Insert(0, activationFailure);
            throw new AggregateException(
                "Imported model-light activation and rollback both failed.",
                rollbackFailures);
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo
            .Capture(activationFailure)
            .Throw();
    }

    private void RemoveAllActivePlacements()
    {
        List<Exception>? failures = null;
        foreach (ActivePlacement placement in
                 _activePlacements.Values.Reverse().ToArray())
        {
            try
            {
                placement.Instance.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        _activePlacements.Clear();
        _activeLightIds.Clear();
        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more imported model-light placements could not be removed.",
                failures);
        }
    }

    private void RebuildActiveLightIds()
    {
        _activeLightIds.Clear();
        foreach (ActivePlacement placement in _activePlacements.Values)
            foreach (Guid id in placement.Instance.LightIds)
                _activeLightIds.Add(id);
    }

    private static string NormalizeAssetKey(string path) =>
        path.Replace('\\', '/');

    private static Guid CreatePlacementId(Guid anchorId, string assetKey)
    {
        byte[] assetBytes = Encoding.UTF8.GetBytes(assetKey);
        byte[] input = new byte[16 + assetBytes.Length];
        anchorId.TryWriteBytes(input);
        assetBytes.CopyTo(input.AsSpan(16));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[7] = (byte)((hash[7] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[..16]);
    }

    private readonly record struct PlacementGroupKey(
        string AssetKey,
        Matrix4x4 WorldTransform);

    private SceneLightDocument ApplyLightSettings(SceneLightDocument source, bool forceShadows)
    {
        SceneLightDocument light = _lightOverrides.TryGetValue(source.Id, out var edits)
            ? edits.Apply(source) : source;
        return forceShadows ? WithShadowFlag(light, true) : light;
    }

    private static SceneLightDocument WithShadowFlag(SceneLightDocument source, bool castsShadows) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Type = source.Type,
        Position = source.Position,
        Direction = source.Direction,
        Up = source.Up,
        Size = source.Size,
        TwoSided = source.TwoSided,
        Color = source.Color,
        Intensity = source.Intensity,
        Range = source.Range,
        SpotAngle = source.SpotAngle,
        InnerSpotAngle = source.InnerSpotAngle,
        AttenuationMode = source.AttenuationMode,
        AttenuationConstant = source.AttenuationConstant,
        AttenuationLinear = source.AttenuationLinear,
        AttenuationQuadratic = source.AttenuationQuadratic,
        CastsShadows = castsShadows,
        ShadowStrength = source.ShadowStrength,
        ShadowMapSizeOverride = source.ShadowMapSizeOverride,
        ShadowNearPlane = source.ShadowNearPlane,
        ShadowFarPlane = source.ShadowFarPlane,
        ShadowPriority = source.ShadowPriority,
        IesProfile = source.IesProfile,
        IesRotationRadians = source.IesRotationRadians,
    };

    private sealed class ImportedOverrideLightStore(ModelLightRuntimeController owner) : IMutableSceneLightStore
    {
        public void Clear() => throw new NotSupportedException();
        public IEnumerable<SceneLightDocument> Enumerate() => owner._store.Enumerate();
        public void Add(Guid id, SceneLightDocument light)
        {
            owner._store.Add(id, Apply(light));
            owner._importedSourceLights[id] = light;
        }
        public bool TryUpdate(Guid id, SceneLightDocument light)
        {
            if (!owner._store.TryUpdate(id, Apply(light)))
                return false;
            owner._importedSourceLights[id] = light;
            return true;
        }
        public bool TryRemove(Guid id)
        {
            if (!owner._store.TryRemove(id))
                return false;
            owner._importedSourceLights.Remove(id);
            return true;
        }
        private SceneLightDocument Apply(SceneLightDocument light) =>
            owner.ApplyLightSettings(light, owner.ImportedModelLightShadowsEnabled);
    }

    private sealed class PlacementGroup(
        string assetPath,
        string assetKey,
        Matrix4x4 worldTransform)
    {
        public string AssetPath { get; } = assetPath;
        public string AssetKey { get; } = assetKey;
        public Matrix4x4 WorldTransform { get; } = worldTransform;
        public Guid AnchorId { get; private set; }

        public void Include(Guid objectId)
        {
            if (AnchorId == Guid.Empty || objectId.CompareTo(AnchorId) < 0)
                AnchorId = objectId;
        }
    }

    private sealed record DesiredPlacement(
        Guid PlacementId,
        Matrix4x4 WorldTransform,
        Model Model);

    private sealed record ActivePlacement(
        Guid PlacementId,
        ModelLightInstanceSet Instance);
}
