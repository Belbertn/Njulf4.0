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
        }

        public void Add(FoliagePrototype foliagePrototype)
        {
            if (foliagePrototype == null)
                throw new ArgumentNullException(nameof(foliagePrototype));

            if (!_foliagePrototypes.Contains(foliagePrototype))
            {
                EnsureCanAdd(foliagePrototype);
                _foliagePrototypes.Add(foliagePrototype);
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
            _renderObjects.Remove(renderObject);
            if (renderObject is IDisposable disposable)
                RemoveDisposableReference(disposable);
        }

        public void Remove(IUpdateable updateable)
        {
            _updateables.Remove(updateable);
            if (updateable is IDisposable disposable)
                RemoveDisposableReference(disposable);
        }

        public void Remove(ReflectionProbe reflectionProbe)
        {
            _reflectionProbes.Remove(reflectionProbe);
        }

        public void Remove(GlobalIlluminationProbeVolume probeVolume)
        {
            _globalIlluminationProbeVolumes.Remove(probeVolume);
        }

        public void Remove(ParticleEffectInstance particleEffect)
        {
            _particleEffects.Remove(particleEffect);
        }

        public void Remove(StaticInstanceBatch staticInstanceBatch)
        {
            _staticInstanceBatches.Remove(staticInstanceBatch);
        }

        public void Remove(FoliagePrototype foliagePrototype)
        {
            _foliagePrototypes.Remove(foliagePrototype);
            _foliagePatches.RemoveAll(patch => ReferenceEquals(patch.Prototype, foliagePrototype));
        }

        public void Remove(FoliagePatch foliagePatch)
        {
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

        public void Clear()
        {
            _renderObjects.Clear();
            _updateables.Clear();
            _reflectionProbes.Clear();
            _globalIlluminationProbeVolumes.Clear();
            _particleEffects.Clear();
            _staticInstanceBatches.Clear();
            _foliagePrototypes.Clear();
            _foliagePatches.Clear();
            _ownedDisposableReferences.Clear();
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

        public void ClearAndDispose()
        {
            foreach (var disposable in _ownedDisposableReferences.Keys)
                disposable.Dispose();

            Clear();
        }

        public void Update(float deltaTime)
        {
            _updateables.Sort((a, b) => a.UpdateOrder.CompareTo(b.UpdateOrder));
            foreach (var updateable in _updateables)
            {
                if (updateable.Enabled)
                    updateable.Update(deltaTime);
            }
        }

        public void Dispose()
        {
            ClearAndDispose();
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
                _ownedDisposableReferences.Remove(disposable);
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
            ArgumentNullException.ThrowIfNull(entity);
            if (entity.Id == Guid.Empty)
                throw new ArgumentException("Scene entity IDs must not be empty.", nameof(entity));
            if (FindById(entity.Id) != null)
                throw new InvalidOperationException($"The scene already contains an entity with ID '{entity.Id}'.");
        }
    }
}
