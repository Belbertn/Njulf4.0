using System.Collections.Concurrent;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>View-independent geometry, refreshed only when a submission actually captures a view.</summary>
internal sealed class SecondarySceneSnapshot : IDisposable
{
    internal readonly record struct MaterialData(int Index, MaterialRenderMetadata Metadata,
        MaterialForwardClass Family);

    internal sealed class MaterialRecord(MaterialHandle handle, MaterialData data, uint revision)
    {
        internal readonly MaterialHandle Handle = handle;
        internal MaterialData Data = data;
        internal uint Revision = revision;
        internal int References;
    }

    internal sealed class Instance
    {
        internal uint InstanceId;
        internal MeshHandle Mesh;
        internal MeshInfo Info;
        internal Matrix4x4 World;
        internal BoundingBox Bounds;
        internal bool Deforming;
        internal SecondaryLodInstanceKey LodKey;
        internal MaterialRecord Material = null!;
    }

    private sealed class ProducerRecord
    {
        internal readonly List<Instance> Instances = [];
        internal ulong Revision;
        internal bool Initialized;
    }

    private readonly Func<MeshHandle, MeshInfo> _readMesh;
    private readonly Func<MaterialHandle, MaterialData> _readMaterial;
    private readonly Func<MaterialHandle, uint> _readMaterialRevision;
    private readonly MaterialHandle _defaultMaterial;
    private readonly Dictionary<IIdentifiedSceneEntity, ProducerRecord> _producers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MaterialHandle, MaterialRecord> _materials = [];
    private readonly HashSet<IIdentifiedSceneEntity> _dirtyProducers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MaterialHandle> _dirtyMaterials = [];
    private readonly ConcurrentQueue<SceneMutation> _sceneChanges = new();
    private readonly ConcurrentQueue<MaterialHandle> _materialChanges = new();
    private readonly HashSet<IIdentifiedSceneEntity> _membership = new(ReferenceEqualityComparer.Instance);
    private readonly List<IIdentifiedSceneEntity> _removedProducers = [];
    private readonly List<MaterialHandle> _unusedMaterials = [];
    private readonly List<Instance> _instances = [];
    private Scene? _scene;
    private ulong _sceneRevision;
    private uint _materialRevision;
    private uint _submissionMaterialRevision;
    private bool _topologyDirty;
    private bool _prepared;

    internal SecondarySceneSnapshot(MaterialHandle defaultMaterial, Func<MeshHandle, MeshInfo> readMesh,
        Func<MaterialHandle, MaterialData> readMaterial, Func<MaterialHandle, uint> readMaterialRevision)
    {
        _defaultMaterial = defaultMaterial;
        _readMesh = readMesh;
        _readMaterial = readMaterial;
        _readMaterialRevision = readMaterialRevision;
    }

    internal void BeginSubmission(Scene scene, uint materialRevision)
    {
        if (!ReferenceEquals(_scene, scene))
        {
            Dispose();
            _scene = scene;
            scene.Mutated += OnSceneMutated;
            _topologyDirty = true;
        }
        _submissionMaterialRevision = materialRevision;
        _prepared = false;
        DrainChanges();
    }

    // Asset/material producers may publish from worker threads. Apply their changes only
    // at the next preparation boundary, never while another view consumes the snapshot.
    internal void OnMaterialChanged(MaterialChangedEvent change) => _materialChanges.Enqueue(change.Handle);

    private void OnSceneMutated(SceneMutation change) => _sceneChanges.Enqueue(change);

    private void DrainChanges()
    {
        // Coalesce even submissions without captures so queued animation/asset changes
        // cannot grow indefinitely while secondary rendering is inactive.
        while (_sceneChanges.TryDequeue(out SceneMutation change)) InvalidateScene(change);
        while (_materialChanges.TryDequeue(out MaterialHandle handle))
            if (_materials.ContainsKey(handle)) _dirtyMaterials.Add(handle);
        if (_topologyDirty) _dirtyProducers.Clear();
    }

    private void InvalidateScene(SceneMutation change)
    {
        if ((change.Kind & SceneMutationKind.Global) != 0)
            _topologyDirty = true;
        if (change.Producer is not (RenderObject or StaticInstanceBatch)) return;
        _dirtyProducers.Add(change.Producer);
        if ((change.Kind & (SceneMutationKind.Added | SceneMutationKind.Removed)) != 0)
            _topologyDirty = true;
    }

    internal IReadOnlyList<Instance> Prepare()
    {
        Scene scene = _scene ?? throw new InvalidOperationException("Scene submission must precede snapshot preparation.");
        if (_prepared) return _instances;

        DrainChanges();

        // Revisions also cover invalidation not accompanied by a relevant producer event.
        if (scene.RenderPayloadRevision != _sceneRevision && _dirtyProducers.Count == 0)
            _topologyDirty = true;
        if (!_topologyDirty && _dirtyProducers.Count == 0 && _dirtyMaterials.Count == 0 &&
            _materialRevision == _submissionMaterialRevision)
        {
            _prepared = true;
            return _instances;
        }
        bool rebuildOrder = _topologyDirty;
        if (_topologyDirty)
        {
            _membership.Clear();
            foreach (RenderObject obj in scene.RenderObjects) RefreshMember(obj);
            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches) RefreshMember(batch);
            _removedProducers.Clear();
            foreach (var pair in _producers)
                if (!_membership.Contains(pair.Key)) _removedProducers.Add(pair.Key);
            foreach (var producer in _removedProducers)
            {
                ReleaseMaterials(_producers[producer]);
                _producers.Remove(producer);
            }
            _removedProducers.Clear();
            _membership.Clear();
        }
        else
        {
            foreach (var producer in _dirtyProducers)
            {
                if (!_producers.TryGetValue(producer, out ProducerRecord? record)) continue;
                int count = record.Instances.Count;
                RefreshProducer(producer, record);
                rebuildOrder |= count != record.Instances.Count;
            }
        }

        if (rebuildOrder)
        {
            _instances.Clear();
            foreach (RenderObject obj in scene.RenderObjects) Append(_producers[obj]);
            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches) Append(_producers[batch]);
        }

        _unusedMaterials.Clear();
        foreach (var pair in _materials)
        {
            MaterialRecord material = pair.Value;
            if (material.References == 0) { _unusedMaterials.Add(pair.Key); continue; }
            if (_materialRevision == _submissionMaterialRevision && !_dirtyMaterials.Contains(pair.Key)) continue;
            uint revision = _readMaterialRevision(pair.Key);
            if (revision == material.Revision && !_dirtyMaterials.Contains(pair.Key)) continue;
            material.Data = _readMaterial(pair.Key);
            material.Revision = revision;
        }
        foreach (MaterialHandle handle in _unusedMaterials) _materials.Remove(handle);
        _unusedMaterials.Clear();
        _dirtyProducers.Clear();
        _dirtyMaterials.Clear();
        _topologyDirty = false;
        _sceneRevision = scene.RenderPayloadRevision;
        _materialRevision = _submissionMaterialRevision;
        _prepared = true;
        return _instances;
    }

    private void RefreshMember(IIdentifiedSceneEntity producer)
    {
        _membership.Add(producer);
        if (!_producers.TryGetValue(producer, out ProducerRecord? record))
            _producers.Add(producer, record = new());
        RefreshProducer(producer, record);
    }

    private void RefreshProducer(IIdentifiedSceneEntity producer, ProducerRecord record)
    {
        RenderObject? obj = producer as RenderObject;
        StaticInstanceBatch? batch = producer as StaticInstanceBatch;
        ulong revision = obj?.Revision ?? batch!.Revision;
        if (record.Initialized && record.Revision == revision) return;
        bool visible = obj?.Visible ?? batch!.Visible;
        object? meshValue = obj is not null ? obj.Mesh : batch!.Mesh;
        int count = visible && meshValue.TryGetMeshHandle(out _)
            ? (obj is not null ? 1 : batch!.WorldMatrices.Count) : 0;
        if (count == 0)
        {
            ReleaseMaterials(record);
            record.Instances.Clear();
        }
        else
        {
            meshValue.TryGetMeshHandle(out MeshHandle mesh);
            MeshInfo info = _readMesh(mesh);
            MaterialHandle handle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                obj is not null ? obj.Material : batch!.Material, _defaultMaterial,
                obj is not null ? obj.Name ?? string.Empty : batch!.Name);
            if (!_materials.TryGetValue(handle, out MaterialRecord? material))
            {
                material = new(handle, _readMaterial(handle), _readMaterialRevision(handle));
                _materials.Add(handle, material);
            }
            ReleaseMaterials(record);
            while (record.Instances.Count > count) record.Instances.RemoveAt(record.Instances.Count - 1);
            BoundingBox localBounds = new(new(info.BoundingBoxMin.X, info.BoundingBoxMin.Y, info.BoundingBoxMin.Z),
                new(info.BoundingBoxMax.X, info.BoundingBoxMax.Y, info.BoundingBoxMax.Z));
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                if (ordinal == record.Instances.Count) record.Instances.Add(new());
                Instance entry = record.Instances[ordinal];
                Matrix4x4 world = obj is not null ? obj.WorldMatrix : batch!.WorldMatrices[ordinal];
                if (obj is SkinnedRenderObject skinned) world = skinned.SkinningBindTransform * world;
                entry.Mesh = mesh;
                entry.Info = info;
                entry.World = world;
                entry.Bounds = SceneDataBuilder.TransformBoundingBox(localBounds, world);
                entry.Deforming = obj is SkinnedRenderObject { SkinningEnabled: true };
                entry.LodKey = new(producer.Id, ordinal, mesh);
                entry.Material = material;
                material.References++;
            }
        }
        record.Revision = revision;
        record.Initialized = true;
    }

    private static void ReleaseMaterials(ProducerRecord record)
    {
        foreach (Instance instance in record.Instances) instance.Material.References--;
    }

    private void Append(ProducerRecord record)
    {
        foreach (Instance instance in record.Instances)
        {
            instance.InstanceId = checked((uint)_instances.Count);
            _instances.Add(instance);
        }
    }

    public void Dispose()
    {
        if (_scene is not null) _scene.Mutated -= OnSceneMutated;
        _scene = null;
        _producers.Clear();
        _materials.Clear();
        _instances.Clear();
        _dirtyProducers.Clear();
        _dirtyMaterials.Clear();
        _membership.Clear();
        _sceneChanges.Clear();
        _materialChanges.Clear();
        _removedProducers.Clear();
        _unusedMaterials.Clear();
        _prepared = false;
    }
}
