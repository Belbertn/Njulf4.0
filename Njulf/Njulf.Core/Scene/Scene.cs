using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class Scene : IDisposable
    {
        private readonly List<RenderObject> _renderObjects = new();
        private readonly List<IUpdateable> _updateables = new();
        private readonly List<IDisposable> _disposables = new();
        
        public string Name { get; set; } = "DefaultScene";
        public Color AmbientLight { get; set; } = new(0.2f, 0.2f, 0.2f, 1f);
        
        public IReadOnlyList<RenderObject> RenderObjects => _renderObjects.AsReadOnly();
        public IReadOnlyList<IUpdateable> Updateables => _updateables.AsReadOnly();

        public void Add(RenderObject renderObject)
        {
            _renderObjects.Add(renderObject);
            if (renderObject is IDisposable disposable)
                _disposables.Add(disposable);
        }

        public void Add(IUpdateable updateable)
        {
            _updateables.Add(updateable);
            if (updateable is IDisposable disposable)
                _disposables.Add(disposable);
        }

        public void Remove(RenderObject renderObject)
        {
            _renderObjects.Remove(renderObject);
            if (renderObject is IDisposable disposable)
                _disposables.Remove(disposable);
        }

        public void Remove(IUpdateable updateable)
        {
            _updateables.Remove(updateable);
            if (updateable is IDisposable disposable)
                _disposables.Remove(disposable);
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
        }

        public void Update(float deltaTime)
        {
            foreach (var updateable in _updateables)
            {
                if (updateable.Enabled)
                    updateable.Update(deltaTime);
            }
            _updateables.Sort((a, b) => a.UpdateOrder.CompareTo(b.UpdateOrder));
        }

        public void Dispose()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
            _disposables.Clear();
        }
    }
}
