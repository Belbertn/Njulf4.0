using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class Model : IDisposable
    {
        private readonly List<RenderObject> _renderObjects = new();
        private readonly List<Action> _disposeActions = new();
        private readonly ReadOnlyCollection<RenderObject> _readOnlyRenderObjects;

        public Model()
        {
            _readOnlyRenderObjects = _renderObjects.AsReadOnly();
        }
        
        public string Name { get; set; } = "Model";
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
        
        public IReadOnlyList<RenderObject> RenderObjects => _readOnlyRenderObjects;

        public Model CreateInstance()
        {
            var instance = new Model
            {
                Name = Name,
                BoundingBox = BoundingBox,
                BoundingSphere = BoundingSphere
            };

            foreach (RenderObject renderObject in _renderObjects)
            {
                instance.Add(new RenderObject
                {
                    Mesh = renderObject.Mesh,
                    Material = renderObject.Material,
                    Name = renderObject.Name,
                    WorldMatrix = renderObject.WorldMatrix,
                    Visible = renderObject.Visible,
                    Enabled = renderObject.Enabled,
                    UpdateOrder = renderObject.UpdateOrder
                });
            }

            return instance;
        }
        
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
