using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class Model : IDisposable
    {
        private readonly List<RenderObject> _renderObjects = new();
        private readonly List<Action> _disposeActions = new();
        
        public string Name { get; set; } = "Model";
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
        
        public IReadOnlyList<RenderObject> RenderObjects => _renderObjects.AsReadOnly();
        
        public void Add(RenderObject renderObject)
        {
            _renderObjects.Add(renderObject);
        }
        
        public void Remove(RenderObject renderObject)
        {
            _renderObjects.Remove(renderObject);
        }
        
        public void Clear()
        {
            _renderObjects.Clear();
        }

        public void AddDisposeAction(Action disposeAction)
        {
            if (disposeAction == null)
                throw new ArgumentNullException(nameof(disposeAction));

            _disposeActions.Add(disposeAction);
        }
        
        public void Update(float deltaTime)
        {
            foreach (var renderObject in _renderObjects)
            {
                if (renderObject.Enabled)
                    renderObject.Update(deltaTime);
            }
        }
        
        public void Dispose()
        {
            foreach (var renderObject in _renderObjects)
            {
                renderObject.Dispose();
            }

            foreach (Action disposeAction in _disposeActions)
                disposeAction();

            _renderObjects.Clear();
            _disposeActions.Clear();
        }
    }
}
