using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Njulf.Core.Foliage;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class Scene : IDisposable, IIdentifiedSceneEntity
    {
        private readonly List<RenderObject> _renderObjects = new();
        private readonly List<IUpdateable> _updateables = new();
        private readonly List<ReflectionProbe> _reflectionProbes = new();
        private readonly List<GlobalIlluminationProbeVolume> _globalIlluminationProbeVolumes = new();
        private readonly List<ParticleEffectInstance> _particleEffects = new();
        private readonly List<StaticInstanceBatch> _staticInstanceBatches = new();
        private readonly List<FoliagePrototype> _foliagePrototypes = new();
        private readonly List<FoliagePatch> _foliagePatches = new();
        private readonly ReadOnlyCollection<RenderObject> _readOnlyRenderObjects;
        private readonly ReadOnlyCollection<IUpdateable> _readOnlyUpdateables;
        private readonly ReadOnlyCollection<ReflectionProbe> _readOnlyReflectionProbes;
        private readonly ReadOnlyCollection<GlobalIlluminationProbeVolume> _readOnlyGlobalIlluminationProbeVolumes;
        private readonly ReadOnlyCollection<ParticleEffectInstance> _readOnlyParticleEffects;
        private readonly ReadOnlyCollection<StaticInstanceBatch> _readOnlyStaticInstanceBatches;
        private readonly ReadOnlyCollection<FoliagePrototype> _readOnlyFoliagePrototypes;
        private readonly ReadOnlyCollection<FoliagePatch> _readOnlyFoliagePatches;
        private readonly Dictionary<IDisposable, int> _ownedDisposableReferences = new();
        private bool _disposeRequested;
        private bool _clearDisposalPending;
        private bool _disposeInProgress;
        private bool _disposed;
        private uint _reflectionProbeRevision;
        private ulong _mutationSerial;
        private Color _ambientLight = new(0.2f, 0.2f, 0.2f, 1f);

        public event Action<SceneMutation>? Mutated;
        public event Action<RenderObjectMutation>? RenderObjectMutated;
        public ulong MutationSerial => _mutationSerial;

        public Scene()
        {
            _readOnlyRenderObjects = _renderObjects.AsReadOnly();
            _readOnlyUpdateables = _updateables.AsReadOnly();
            _readOnlyReflectionProbes = _reflectionProbes.AsReadOnly();
            _readOnlyGlobalIlluminationProbeVolumes = _globalIlluminationProbeVolumes.AsReadOnly();
            _readOnlyParticleEffects = _particleEffects.AsReadOnly();
            _readOnlyStaticInstanceBatches = _staticInstanceBatches.AsReadOnly();
            _readOnlyFoliagePrototypes = _foliagePrototypes.AsReadOnly();
            _readOnlyFoliagePatches = _foliagePatches.AsReadOnly();
        }

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "DefaultScene";
        public Color AmbientLight
        {
            get => _ambientLight;
            set
            {
                if (_ambientLight.Equals(value))
                    return;
                _ambientLight = value;
                PublishMutation(
                    this,
                    SceneMutationKind.Content | SceneMutationKind.Global,
                    null,
                    null,
                    _mutationSerial);
            }
        }

        public IReadOnlyList<RenderObject> RenderObjects => _readOnlyRenderObjects;
        public IReadOnlyList<IUpdateable> Updateables => _readOnlyUpdateables;
        public IReadOnlyList<ReflectionProbe> ReflectionProbes => _readOnlyReflectionProbes;
        public uint ReflectionProbeRevision => _reflectionProbeRevision;
        public IReadOnlyList<GlobalIlluminationProbeVolume> GlobalIlluminationProbeVolumes => _readOnlyGlobalIlluminationProbeVolumes;
        public IReadOnlyList<ParticleEffectInstance> ParticleEffects => _readOnlyParticleEffects;
        public IReadOnlyList<StaticInstanceBatch> StaticInstanceBatches => _readOnlyStaticInstanceBatches;
        public IReadOnlyList<FoliagePrototype> FoliagePrototypes => _readOnlyFoliagePrototypes;
        public IReadOnlyList<FoliagePatch> FoliagePatches => _readOnlyFoliagePatches;

        public void Add(RenderObject renderObject)
        {
            EnsureCanAdd(renderObject);
            _renderObjects.Add(renderObject);
            renderObject.Changed += OnRenderObjectChanged;
            if (renderObject is IDisposable disposable)
                AddDisposableReference(disposable);
            PublishMutation(
                renderObject,
                SceneMutationKind.Added | SceneMutationKind.Geometry,
                null,
                TryGetRenderObjectBounds(renderObject),
                renderObject.Revision);
        }

        public void Add(IUpdateable updateable)
        {
            EnsureMutable();
            ArgumentNullException.ThrowIfNull(updateable);
            _updateables.Add(updateable);
            if (updateable is IDisposable disposable)
                AddDisposableReference(disposable);
        }

        public void Add(ReflectionProbe reflectionProbe)
        {
            if (reflectionProbe == null)
                throw new ArgumentNullException(nameof(reflectionProbe));

            EnsureCanAdd(reflectionProbe);
            _reflectionProbes.Add(reflectionProbe);
            reflectionProbe.Changed += OnReflectionProbeChanged;
            AdvanceReflectionProbeRevision();
        }

        public void Add(GlobalIlluminationProbeVolume probeVolume)
        {
            if (probeVolume == null)
                throw new ArgumentNullException(nameof(probeVolume));

            EnsureCanAdd(probeVolume);
            _globalIlluminationProbeVolumes.Add(probeVolume);
        }

        public void Add(ParticleEffectInstance particleEffect)
        {
            if (particleEffect == null)
                throw new ArgumentNullException(nameof(particleEffect));

            EnsureCanAdd(particleEffect);
            _particleEffects.Add(particleEffect);
            particleEffect.Changed += OnParticleEffectChanged;
            PublishMutation(
                particleEffect,
                SceneMutationKind.Added | SceneMutationKind.ParticleState,
                null,
                null,
                particleEffect.Version);
        }

        public void Add(StaticInstanceBatch staticInstanceBatch)
        {
            if (staticInstanceBatch == null)
                throw new ArgumentNullException(nameof(staticInstanceBatch));

            EnsureCanAdd(staticInstanceBatch);
            _staticInstanceBatches.Add(staticInstanceBatch);
            staticInstanceBatch.Changed += OnStaticInstanceBatchChanged;
            AddDisposableReference(staticInstanceBatch);
            PublishMutation(
                staticInstanceBatch,
                SceneMutationKind.Added | SceneMutationKind.StaticInstances,
                null,
                null,
                staticInstanceBatch.Revision);
        }

        public void Add(FoliagePrototype foliagePrototype)
        {
            if (foliagePrototype == null)
                throw new ArgumentNullException(nameof(foliagePrototype));

            if (!_foliagePrototypes.Contains(foliagePrototype))
            {
                EnsureCanAdd(foliagePrototype);
                _foliagePrototypes.Add(foliagePrototype);
                foliagePrototype.Changed += OnFoliagePrototypeChanged;
                AddDisposableReference(foliagePrototype);
            }
        }

        public void Add(FoliagePatch foliagePatch)
        {
            if (foliagePatch == null)
                throw new ArgumentNullException(nameof(foliagePatch));

            Add(foliagePatch.Prototype);
            EnsureCanAdd(foliagePatch);
            _foliagePatches.Add(foliagePatch);
            foliagePatch.Changed += OnFoliagePatchChanged;
            PublishMutation(
                foliagePatch,
                SceneMutationKind.Added | SceneMutationKind.Foliage,
                null,
                foliagePatch.Bounds,
                foliagePatch.ContentRevision);
        }

        public void Remove(RenderObject renderObject)
        {
            EnsureMutable();
            if (!_renderObjects.Contains(renderObject))
                return;
            BoundingBox? oldBounds = TryGetRenderObjectBounds(renderObject);
            renderObject.Changed -= OnRenderObjectChanged;
            RemoveDisposableReference(renderObject);
            _renderObjects.Remove(renderObject);
            PublishMutation(
                renderObject,
                SceneMutationKind.Removed | SceneMutationKind.Geometry,
                oldBounds,
                null,
                renderObject.Revision);
        }

        public void Remove(IUpdateable updateable)
        {
            EnsureMutable();
            if (!_updateables.Contains(updateable))
                return;
            if (updateable is IDisposable disposable)
                RemoveDisposableReference(disposable);
            _updateables.Remove(updateable);
        }

        public void Remove(ReflectionProbe reflectionProbe)
        {
            EnsureMutable();
            if (_reflectionProbes.Remove(reflectionProbe))
            {
                reflectionProbe.Changed -= OnReflectionProbeChanged;
                AdvanceReflectionProbeRevision();
            }
        }

        public void Remove(GlobalIlluminationProbeVolume probeVolume)
        {
            EnsureMutable();
            _globalIlluminationProbeVolumes.Remove(probeVolume);
        }

        public void Remove(ParticleEffectInstance particleEffect)
        {
            EnsureMutable();
            if (_particleEffects.Remove(particleEffect))
            {
                particleEffect.Changed -= OnParticleEffectChanged;
                PublishMutation(
                    particleEffect,
                    SceneMutationKind.Removed | SceneMutationKind.ParticleState,
                    null,
                    null,
                    particleEffect.Version);
            }
        }

        public void Remove(StaticInstanceBatch staticInstanceBatch)
        {
            EnsureMutable();
            if (!_staticInstanceBatches.Contains(staticInstanceBatch))
                return;
            staticInstanceBatch.Changed -= OnStaticInstanceBatchChanged;
            RemoveDisposableReference(staticInstanceBatch);
            _staticInstanceBatches.Remove(staticInstanceBatch);
            PublishMutation(
                staticInstanceBatch,
                SceneMutationKind.Removed | SceneMutationKind.StaticInstances,
                null,
                null,
                staticInstanceBatch.Revision);
        }

        public void Remove(FoliagePrototype foliagePrototype)
        {
            EnsureMutable();
            if (!_foliagePrototypes.Contains(foliagePrototype))
                return;

            for (int index = _foliagePatches.Count - 1; index >= 0; index--)
            {
                FoliagePatch patch = _foliagePatches[index];
                if (ReferenceEquals(patch.Prototype, foliagePrototype))
                    Remove(patch);
            }

            foliagePrototype.Changed -= OnFoliagePrototypeChanged;
            RemoveDisposableReference(foliagePrototype);
            _foliagePrototypes.Remove(foliagePrototype);
        }

        public void Remove(FoliagePatch foliagePatch)
        {
            EnsureMutable();
            if (_foliagePatches.Remove(foliagePatch))
            {
                foliagePatch.Changed -= OnFoliagePatchChanged;
                PublishMutation(
                    foliagePatch,
                    SceneMutationKind.Removed | SceneMutationKind.Foliage,
                    foliagePatch.Bounds,
                    null,
                    foliagePatch.ContentRevision);
            }
        }

        public T? GetComponent<T>() where T : class
        {
            foreach (var obj in _renderObjects)
            {
                if (obj is T component)
                    return component;
            }
            foreach (var obj in _updateables)
            {
                if (obj is T component)
                    return component;
            }
            return default;
        }

        public IEnumerable<T> GetComponents<T>() where T : class
        {
            foreach (var obj in _renderObjects)
            {
                if (obj is T component)
                    yield return component;
            }
            foreach (var obj in _updateables)
            {
                if (obj is T component)
                    yield return component;
            }
        }

        /// <summary>
        /// Removes all entities and relinquishes their ownership without disposing them.
        /// </summary>
        public void Clear()
        {
            EnsureMutable();
            PublishGlobalClearMutation();
            ClearCollections();
        }

        private void ClearCollections()
        {
            ClearEntityCollections();
            _ownedDisposableReferences.Clear();
        }

        private void ClearEntityCollections()
        {
            foreach (RenderObject renderObject in _renderObjects)
                renderObject.Changed -= OnRenderObjectChanged;
            foreach (ParticleEffectInstance particleEffect in _particleEffects)
                particleEffect.Changed -= OnParticleEffectChanged;
            foreach (StaticInstanceBatch batch in _staticInstanceBatches)
                batch.Changed -= OnStaticInstanceBatchChanged;
            foreach (FoliagePatch patch in _foliagePatches)
                patch.Changed -= OnFoliagePatchChanged;
            foreach (FoliagePrototype prototype in _foliagePrototypes)
                prototype.Changed -= OnFoliagePrototypeChanged;
            if (_reflectionProbes.Count > 0)
            {
                foreach (ReflectionProbe probe in _reflectionProbes)
                    probe.Changed -= OnReflectionProbeChanged;
                AdvanceReflectionProbeRevision();
            }
            _renderObjects.Clear();
            _updateables.Clear();
            _reflectionProbes.Clear();
            _globalIlluminationProbeVolumes.Clear();
            _particleEffects.Clear();
            _staticInstanceBatches.Clear();
            _foliagePrototypes.Clear();
            _foliagePatches.Clear();
        }

        private void OnRenderObjectChanged(RenderObjectMutation mutation)
        {
            RenderObjectMutated?.Invoke(mutation);
            PublishMutation(
                mutation.Source,
                mutation.Kind,
                mutation.OldWorldBounds,
                mutation.NewWorldBounds,
                mutation.Revision);
        }

        private void OnParticleEffectChanged(
            ParticleEffectInstance particleEffect,
            SceneMutationKind kind) =>
            PublishMutation(
                particleEffect,
                kind,
                null,
                null,
                particleEffect.Version);

        private void OnStaticInstanceBatchChanged(StaticInstanceBatch batch) =>
            PublishMutation(
                batch,
                SceneMutationKind.StaticInstances,
                null,
                null,
                batch.Revision);

        private void OnFoliagePatchChanged(
            FoliagePatch patch,
            BoundingBox previousBounds)
        {
            if (!_foliagePrototypes.Contains(patch.Prototype))
                Add(patch.Prototype);
            PublishMutation(
                patch,
                SceneMutationKind.Foliage,
                previousBounds,
                patch.Bounds,
                patch.ContentRevision);
        }

        private void OnFoliagePrototypeChanged(FoliagePrototype prototype)
        {
            foreach (FoliagePatch patch in _foliagePatches)
            {
                if (!ReferenceEquals(patch.Prototype, prototype))
                    continue;
                PublishMutation(
                    patch,
                    SceneMutationKind.Foliage | SceneMutationKind.Content,
                    patch.Bounds,
                    patch.Bounds,
                    patch.ContentRevision);
            }
        }

        private void PublishMutation(
            IIdentifiedSceneEntity producer,
            SceneMutationKind kind,
            BoundingBox? oldWorldBounds,
            BoundingBox? newWorldBounds,
            ulong contentRevision)
        {
            _mutationSerial = _mutationSerial == ulong.MaxValue
                ? 1UL
                : _mutationSerial + 1UL;
            Mutated?.Invoke(new SceneMutation(
                _mutationSerial,
                producer.Id,
                producer,
                kind,
                oldWorldBounds,
                newWorldBounds,
                contentRevision));
        }

        private static BoundingBox? TryGetRenderObjectBounds(
            RenderObject renderObject) =>
            renderObject.LocalMeshBounds is { } local
                ? BoundingBox.Transform(local, renderObject.WorldMatrix)
                : null;

        private void OnReflectionProbeChanged(ReflectionProbe probe) => AdvanceReflectionProbeRevision();

        private void AdvanceReflectionProbeRevision() =>
            _reflectionProbeRevision = _reflectionProbeRevision == uint.MaxValue ? 1u : _reflectionProbeRevision + 1u;

        /// <summary>Finds a scene-owned entity by its stable identifier.</summary>
        public IIdentifiedSceneEntity? FindById(Guid id)
        {
            if (id == Guid.Empty)
                return null;

            return Find(_renderObjects, id)
                ?? Find(_reflectionProbes, id)
                ?? Find(_globalIlluminationProbeVolumes, id)
                ?? Find(_particleEffects, id)
                ?? Find(_staticInstanceBatches, id)
                ?? Find(_foliagePrototypes, id)
                ?? Find(_foliagePatches, id);
        }

        /// <summary>
        /// Disposes all scene-owned entities and clears the scene for reuse.
        /// </summary>
        public void ClearAndDispose()
        {
            if (_disposed || _disposeRequested || _disposeInProgress)
                throw new ObjectDisposedException(nameof(Scene));

            PublishGlobalClearMutation();
            _clearDisposalPending = true;
            _disposeInProgress = true;
            List<Exception>? failures = null;
            try
            {
                failures = DisposeOwnedEntities();
                ClearEntityCollections();
                if (failures is null)
                {
                    _ownedDisposableReferences.Clear();
                    _clearDisposalPending = false;
                }
            }
            finally
            {
                _disposeInProgress = false;
            }

            ThrowIfDisposalFailed(failures);
        }

        public void Update(float deltaTime)
        {
            EnsureMutable();
            _updateables.Sort((a, b) => a.UpdateOrder.CompareTo(b.UpdateOrder));
            foreach (var updateable in _updateables)
            {
                if (updateable.Enabled)
                    updateable.Update(deltaTime);
            }
        }

        public void Dispose()
        {
            if (_disposed || _disposeInProgress)
                return;

            PublishGlobalClearMutation();
            _disposeRequested = true;
            _disposeInProgress = true;
            List<Exception>? failures = null;
            try
            {
                failures = DisposeOwnedEntities();
                ClearEntityCollections();
                if (failures is null)
                {
                    _ownedDisposableReferences.Clear();
                    _clearDisposalPending = false;
                    _disposed = true;
                }
            }
            finally
            {
                _disposeInProgress = false;
            }

            ThrowIfDisposalFailed(failures);
        }

        private List<Exception>? DisposeOwnedEntities()
        {
            List<Exception>? failures = null;
            foreach (IDisposable disposable in
                     _ownedDisposableReferences.Keys.ToArray())
            {
                try
                {
                    disposable.Dispose();
                    _ownedDisposableReferences.Remove(disposable);
                }
                catch (Exception disposeFailure)
                {
                    (failures ??= new List<Exception>())
                        .Add(disposeFailure);
                }
            }

            return failures;
        }

        private static void ThrowIfDisposalFailed(List<Exception>? failures)
        {
            if (failures != null)
            {
                throw new AggregateException(
                    "One or more scene-owned resources could not be disposed.",
                    failures);
            }
        }

        private void AddDisposableReference(IDisposable disposable)
        {
            _ownedDisposableReferences.TryGetValue(disposable, out int references);
            _ownedDisposableReferences[disposable] = references + 1;
        }

        private void RemoveDisposableReference(IDisposable disposable)
        {
            if (!_ownedDisposableReferences.TryGetValue(disposable, out int references))
                return;

            if (references <= 1)
            {
                disposable.Dispose();
                _ownedDisposableReferences.Remove(disposable);
            }
            else
                _ownedDisposableReferences[disposable] = references - 1;
        }

        private void PublishGlobalClearMutation()
        {
            if (_renderObjects.Count == 0 &&
                _particleEffects.Count == 0 &&
                _staticInstanceBatches.Count == 0 &&
                _foliagePatches.Count == 0)
            {
                return;
            }

            PublishMutation(
                this,
                SceneMutationKind.Removed | SceneMutationKind.Global,
                null,
                null,
                _mutationSerial);
        }

        private static IIdentifiedSceneEntity? Find<T>(IEnumerable<T> entities, Guid id)
            where T : IIdentifiedSceneEntity
        {
            foreach (T entity in entities)
                if (entity.Id == id)
                    return entity;

            return null;
        }

        private void EnsureCanAdd(IIdentifiedSceneEntity entity)
        {
            EnsureMutable();
            ArgumentNullException.ThrowIfNull(entity);
            if (entity.Id == Guid.Empty)
                throw new ArgumentException("Scene entity IDs must not be empty.", nameof(entity));
            if (FindById(entity.Id) != null)
                throw new InvalidOperationException($"The scene already contains an entity with ID '{entity.Id}'.");
        }

        private void EnsureMutable()
        {
            if (_disposeRequested ||
                _clearDisposalPending ||
                _disposeInProgress ||
                _disposed)
            {
                throw new ObjectDisposedException(
                    nameof(Scene));
            }
        }
    }
}
