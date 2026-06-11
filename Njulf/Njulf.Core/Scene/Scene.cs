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
        private readonly ReadOnlyCollection<RenderObject> _readOnlyRenderObjects;
        private readonly ReadOnlyCollection<IUpdateable> _readOnlyUpdateables;
        private readonly Dictionary<IDisposable, int> _ownedDisposableReferences = new();
        
        public Scene()
        {
            _readOnlyRenderObjects = _renderObjects.AsReadOnly();
            _readOnlyUpdateables = _updateables.AsReadOnly();
        }

        public string Name { get; set; } = "DefaultScene";
        public Color AmbientLight { get; set; } = new(0.2f, 0.2f, 0.2f, 1f);
        
        public IReadOnlyList<RenderObject> RenderObjects => _readOnlyRenderObjects;
        public IReadOnlyList<IUpdateable> Updateables => _readOnlyUpdateables;

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
