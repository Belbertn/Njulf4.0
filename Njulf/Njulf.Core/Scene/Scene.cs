using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class Scene : IDisposable
    {
        private readonly List<RenderObject> _renderObjects = new();
        private readonly List<IUpdateable> _updateables = new();
        private readonly List<ReflectionProbe> _reflectionProbes = new();
        private readonly List<ParticleEffectInstance> _particleEffects = new();
        private readonly List<StaticInstanceBatch> _staticInstanceBatches = new();
        private readonly ReadOnlyCollection<RenderObject> _readOnlyRenderObjects;
        private readonly ReadOnlyCollection<IUpdateable> _readOnlyUpdateables;
        private readonly ReadOnlyCollection<ReflectionProbe> _readOnlyReflectionProbes;
        private readonly ReadOnlyCollection<ParticleEffectInstance> _readOnlyParticleEffects;
        private readonly ReadOnlyCollection<StaticInstanceBatch> _readOnlyStaticInstanceBatches;
        private readonly Dictionary<IDisposable, int> _ownedDisposableReferences = new();
        
        public Scene()
        {
            _readOnlyRenderObjects = _renderObjects.AsReadOnly();
            _readOnlyUpdateables = _updateables.AsReadOnly();
            _readOnlyReflectionProbes = _reflectionProbes.AsReadOnly();
            _readOnlyParticleEffects = _particleEffects.AsReadOnly();
            _readOnlyStaticInstanceBatches = _staticInstanceBatches.AsReadOnly();
        }

        public string Name { get; set; } = "DefaultScene";
        public Color AmbientLight { get; set; } = new(0.2f, 0.2f, 0.2f, 1f);
        
        public IReadOnlyList<RenderObject> RenderObjects => _readOnlyRenderObjects;
        public IReadOnlyList<IUpdateable> Updateables => _readOnlyUpdateables;
        public IReadOnlyList<ReflectionProbe> ReflectionProbes => _readOnlyReflectionProbes;
        public IReadOnlyList<ParticleEffectInstance> ParticleEffects => _readOnlyParticleEffects;
        public IReadOnlyList<StaticInstanceBatch> StaticInstanceBatches => _readOnlyStaticInstanceBatches;

        public void Add(RenderObject renderObject)
        {
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

            _reflectionProbes.Add(reflectionProbe);
        }

        public void Add(ParticleEffectInstance particleEffect)
        {
            if (particleEffect == null)
                throw new ArgumentNullException(nameof(particleEffect));

            _particleEffects.Add(particleEffect);
        }

        public void Add(StaticInstanceBatch staticInstanceBatch)
        {
            if (staticInstanceBatch == null)
                throw new ArgumentNullException(nameof(staticInstanceBatch));

            _staticInstanceBatches.Add(staticInstanceBatch);
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

        public void Remove(ParticleEffectInstance particleEffect)
        {
            _particleEffects.Remove(particleEffect);
        }

        public void Remove(StaticInstanceBatch staticInstanceBatch)
        {
            _staticInstanceBatches.Remove(staticInstanceBatch);
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
            _particleEffects.Clear();
            _staticInstanceBatches.Clear();
            _ownedDisposableReferences.Clear();
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
    }
}
