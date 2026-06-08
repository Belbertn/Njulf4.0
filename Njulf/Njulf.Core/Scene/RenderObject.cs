using System;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class RenderObject : IRenderable, IUpdateable, IDisposable
    {
        private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
        private object? _mesh;
        private object? _material;
        private bool _visible = true;
        private bool _enabled = true;
        private int _updateOrder;
        
        public Matrix4x4 WorldMatrix
        {
            get => _worldMatrix;
            set { _worldMatrix = value; _dirty = true; }
        }
        
        public object? Mesh
        {
            get => _mesh;
            set { _mesh = value; _dirty = true; }
        }
        
        public object? Material
        {
            get => _material;
            set { _material = value; _dirty = true; }
        }
        
        public bool Visible
        {
            get => _visible;
            set => _visible = value;
        }
        
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }
        
        public int UpdateOrder
        {
            get => _updateOrder;
            set => _updateOrder = value;
        }
        
        public string Name { get; set; } = "RenderObject";
        public Vector3 Position
        {
            get => new Vector3(_worldMatrix.M41, _worldMatrix.M42, _worldMatrix.M43);
            set
            {
                _worldMatrix.M41 = value.X;
                _worldMatrix.M42 = value.Y;
                _worldMatrix.M43 = value.Z;
                _dirty = true;
            }
        }
        
        public Vector3 Scale
        {
            get => new Vector3(
                (float)System.Math.Sqrt(_worldMatrix.M11 * _worldMatrix.M11 + _worldMatrix.M12 * _worldMatrix.M12 + _worldMatrix.M13 * _worldMatrix.M13),
                (float)System.Math.Sqrt(_worldMatrix.M21 * _worldMatrix.M21 + _worldMatrix.M22 * _worldMatrix.M22 + _worldMatrix.M23 * _worldMatrix.M23),
                (float)System.Math.Sqrt(_worldMatrix.M31 * _worldMatrix.M31 + _worldMatrix.M32 * _worldMatrix.M32 + _worldMatrix.M33 * _worldMatrix.M33));
            set
            {
                // Scale the rotation part of the matrix
                _worldMatrix.M11 = value.X; _worldMatrix.M12 = 0; _worldMatrix.M13 = 0;
                _worldMatrix.M21 = 0; _worldMatrix.M22 = value.Y; _worldMatrix.M23 = 0;
                _worldMatrix.M31 = 0; _worldMatrix.M32 = 0; _worldMatrix.M33 = value.Z;
                _dirty = true;
            }
        }
        
        private bool _dirty = true;
        private Matrix4x4 _cachedWorldMatrix;
        
        public RenderObject() { }
        
        public RenderObject(object mesh, object material)
        {
            _mesh = mesh;
            _material = material;
        }
        
        public void Draw()
        {
            if (!Visible) return;
            // Draw logic will be handled by the renderer
        }
        
        public void Update(float deltaTime)
        {
            if (!_enabled) return;
            // Custom update logic can be added by subclasses
        }
        
        public Matrix4x4 GetWorldMatrix()
        {
            if (_dirty)
            {
                _cachedWorldMatrix = _worldMatrix;
                _dirty = false;
            }
            return _cachedWorldMatrix;
        }
        
        public void Dispose()
        {
            // Cleanup resources if needed
            _mesh = null;
            _material = null;
        }
    }
}
