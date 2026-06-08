using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core.Camera
{
    public abstract class CameraBase : ICamera
    {
        private Vector3 _position;
        private float _nearPlane = 0.1f;
        private float _farPlane = 1000f;
        private float _fieldOfView = (float)System.Math.PI / 3f; // 60 degrees
        private float _aspectRatio = 16f / 9f;
        
        private Matrix4x4 _viewMatrix;
        private Matrix4x4 _projectionMatrix;
        private Matrix4x4 _viewProjectionMatrix;
        private bool _dirty = true;

        public Vector3 Position
        {
            get => _position;
            set { _position = value; _dirty = true; }
        }

        public Matrix4x4 ViewMatrix
        {
            get
            {
                if (_dirty) UpdateMatrices();
                return _viewMatrix;
            }
        }

        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                if (_dirty) UpdateMatrices();
                return _projectionMatrix;
            }
        }

        public Matrix4x4 ViewProjectionMatrix
        {
            get
            {
                if (_dirty) UpdateMatrices();
                return _viewProjectionMatrix;
            }
        }

        public float NearPlane
        {
            get => _nearPlane;
            set { _nearPlane = value; _dirty = true; }
        }

        public float FarPlane
        {
            get => _farPlane;
            set { _farPlane = value; _dirty = true; }
        }

        public float FieldOfView
        {
            get => _fieldOfView;
            set { _fieldOfView = value; _dirty = true; }
        }

        public float AspectRatio
        {
            get => _aspectRatio;
            set { _aspectRatio = value; _dirty = true; }
        }

        public abstract Vector3 Forward { get; }
        public abstract Vector3 Right { get; }
        public abstract Vector3 Up { get; }

        protected CameraBase()
        {
            _position = Vector3.Zero;
        }

        protected CameraBase(Vector3 position)
        {
            _position = position;
        }

        protected abstract Matrix4x4 CalculateViewMatrix();

        protected virtual Matrix4x4 CalculateProjectionMatrix() =>
            Matrix4x4.CreatePerspectiveFieldOfView(
                _fieldOfView, _aspectRatio, _nearPlane, _farPlane);

        private void UpdateMatrices()
        {
            _viewMatrix = CalculateViewMatrix();
            _projectionMatrix = CalculateProjectionMatrix();
            _viewProjectionMatrix = _viewMatrix * _projectionMatrix;
            _dirty = false;
        }

        public virtual void Update()
        {
            _dirty = true;
        }

        public void LookAt(Vector3 target, Vector3 up)
        {
            _viewMatrix = Matrix4x4.CreateLookAt(_position, target, up);
            _viewProjectionMatrix = _viewMatrix * _projectionMatrix;
            _dirty = false;
        }
    }
}
