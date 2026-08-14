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
    private readonly Dictionary<Guid, ActivePlacement> _activePlacements = [];
    private readonly HashSet<Guid> _activeLightIds = [];
    private bool _disposed;
    private bool _reconciling;

    private ModelLightRuntimeController(
        Scene scene,
        IContentManager content,
        IMutableSceneLightStore store)
    {
        _scene = scene;
        _content = content;
        _store = store;
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

    /// <summary>Gets whether all imported model lights are active.</summary>
    public bool ImportedModelLightsEnabled { get; private set; }

    /// <summary>Number of distinct ordinary model placements in the scene.</summary>
    public int ModelPlacementCount { get; private set; }

    /// <summary>Number of placements whose model contains imported lights.</summary>
    public int ModelPlacementsWithLightsCount { get; private set; }

    /// <summary>Total imported light definitions across all placements.</summary>
    public int ImportedLightDefinitionCount { get; private set; }

    /// <summary>Number of imported lights currently present in the live store.</summary>
    public int ActiveLightCount => _activeLightIds.Count;

    /// <summary>The last automatic reconciliation failure, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Attaches one scene-owned controller, or returns the controller already
    /// attached to the scene.
    /// </summary>
    public static ModelLightRuntimeController Attach(
        Scene scene,
        IContentManager content,
        IMutableSceneLightStore store)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(store);

        if (scene.GetComponent<ModelLightRuntimeController>() is { } existing)
            return existing;

        var controller = new ModelLightRuntimeController(scene, content, store);
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
    public bool IsImportedLight(Guid id) => _activeLightIds.Contains(id);

    /// <summary>Immediately enables or disables every imported model light.</summary>
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
        ImportedModelLightsEnabled = false;
    }

    bool IUpdateable.Enabled { get; set; }
    int IUpdateable.UpdateOrder { get; set; }
    void IUpdateable.Update(float deltaTime) { }

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
        foreach (PlacementGroup group in groups.Values)
        {
            if (!models.TryGetValue(group.AssetKey, out Model? model))
            {
                model = _content.Load<Model>(group.AssetPath) ??
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
        }

        ModelPlacementCount = groups.Count;
        ModelPlacementsWithLightsCount = placementsWithLights;
        ImportedLightDefinitionCount = definitionCount;
        return desired;
    }

    private void ReconcileEnabledPlacements(
        IReadOnlyDictionary<Guid, DesiredPlacement> desired)
    {
        var created = new List<ActivePlacement>();
        var updated = new List<(ActivePlacement Placement, Matrix4x4 Previous)>();
        try
        {
            foreach (DesiredPlacement placement in desired.Values)
            {
                if (placement.Model.Lights.Count == 0)
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
                    ModelLightInstantiator.Instantiate(
                        placement.Model,
                        _store,
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
                placement.Model.Lights.Count == 0)
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
