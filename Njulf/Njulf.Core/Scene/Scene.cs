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
        public Color AmbientLight { get; set; } = new(0.2f, 0.2f, 0.2f, 1f);

        public IReadOnlyList<RenderObject> RenderObjects => _readOnlyRenderObjects;
        public IReadOnlyList<IUpdateable> Updateables => _readOnlyUpdateables;
        public IReadOnlyList<ReflectionProbe> ReflectionProbes => _readOnlyReflectionProbes;
        public IReadOnlyList<GlobalIlluminationProbeVolume> GlobalIlluminationProbeVolumes => _readOnlyGlobalIlluminationProbeVolumes;
        public IReadOnlyList<ParticleEffectInstance> ParticleEffects => _readOnlyParticleEffects;
        public IReadOnlyList<StaticInstanceBatch> StaticInstanceBatches => _readOnlyStaticInstanceBatches;
        public IReadOnlyList<FoliagePrototype> FoliagePrototypes => _readOnlyFoliagePrototypes;
        public IReadOnlyList<FoliagePatch> FoliagePatches => _readOnlyFoliagePatches;

        public void Add(RenderObject renderObject)
        {
            EnsureCanAdd(renderObject);
            _renderObjects.Add(renderObject);
            if (renderObject is IDisposable disposable)
                AddDisposableReference(disposable);
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
        }

        public void Add(StaticInstanceBatch staticInstanceBatch)
        {
            if (staticInstanceBatch == null)
                throw new ArgumentNullException(nameof(staticInstanceBatch));

            EnsureCanAdd(staticInstanceBatch);
            _staticInstanceBatches.Add(staticInstanceBatch);
            AddDisposableReference(staticInstanceBatch);
        }

        public void Add(FoliagePrototype foliagePrototype)
        {
            if (foliagePrototype == null)
                throw new ArgumentNullException(nameof(foliagePrototype));

            if (!_foliagePrototypes.Contains(foliagePrototype))
            {
                EnsureCanAdd(foliagePrototype);
                _foliagePrototypes.Add(foliagePrototype);
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
        }

        public void Remove(RenderObject renderObject)
        {
            EnsureMutable();
            if (!_renderObjects.Contains(renderObject))
                return;
            RemoveDisposableReference(renderObject);
            _renderObjects.Remove(renderObject);
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
            _reflectionProbes.Remove(reflectionProbe);
        }

        public void Remove(GlobalIlluminationProbeVolume probeVolume)
        {
            EnsureMutable();
            _globalIlluminationProbeVolumes.Remove(probeVolume);
        }

        public void Remove(ParticleEffectInstance particleEffect)
        {
            EnsureMutable();
            _particleEffects.Remove(particleEffect);
        }

        public void Remove(StaticInstanceBatch staticInstanceBatch)
        {
            EnsureMutable();
            if (!_staticInstanceBatches.Contains(staticInstanceBatch))
                return;
            RemoveDisposableReference(staticInstanceBatch);
            _staticInstanceBatches.Remove(staticInstanceBatch);
        }

        public void Remove(FoliagePrototype foliagePrototype)
        {
            EnsureMutable();
            if (!_foliagePrototypes.Contains(foliagePrototype))
                return;
            RemoveDisposableReference(foliagePrototype);
            _foliagePrototypes.Remove(foliagePrototype);
            _foliagePatches.RemoveAll(patch => ReferenceEquals(patch.Prototype, foliagePrototype));
        }

        public void Remove(FoliagePatch foliagePatch)
        {
            EnsureMutable();
            _foliagePatches.Remove(foliagePatch);
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
            ClearCollections();
        }

        private void ClearCollections()
        {
            ClearEntityCollections();
            _ownedDisposableReferences.Clear();
        }

        private void ClearEntityCollections()
        {
            _renderObjects.Clear();
            _updateables.Clear();
            _reflectionProbes.Clear();
            _globalIlluminationProbeVolumes.Clear();
            _particleEffects.Clear();
            _staticInstanceBatches.Clear();
            _foliagePrototypes.Clear();
            _foliagePatches.Clear();
        }

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
