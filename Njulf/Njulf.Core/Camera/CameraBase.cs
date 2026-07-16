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

        /// <summary>
        /// Converts a top-left-origin framebuffer coordinate into a world-space ray.
        /// The projection used by Njulf is reverse-Z, so the near and far clip depths are 1 and 0.
        /// </summary>
        public Ray ScreenPointToRay(Vector2 screenPosition, Vector2 viewportSize)
        {
            if (!float.IsFinite(viewportSize.X) || !float.IsFinite(viewportSize.Y) ||
                viewportSize.X <= 0f || viewportSize.Y <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(viewportSize), "Viewport dimensions must be finite and positive.");
            }

            float x = screenPosition.X / viewportSize.X * 2f - 1f;
            // The engine's Vulkan projection flips Y, and framebuffer coordinates are top-left origin.
            float y = screenPosition.Y / viewportSize.Y * 2f - 1f;
            Matrix4x4 inverseViewProjection = ViewProjectionMatrix.Invert();
            Vector3 nearPoint = Unproject(x, y, 1f, inverseViewProjection);
            Vector3 farPoint = Unproject(x, y, 0f, inverseViewProjection);
            Vector3 direction = (farPoint - nearPoint).Normalized();
            if (direction.LengthSquared() <= 1e-12f)
                throw new InvalidOperationException("Could not produce a ray from the supplied screen coordinate.");

            return new Ray(nearPoint, direction);
        }

        public void LookAt(Vector3 target, Vector3 up)
        {
            _viewMatrix = Matrix4x4.CreateLookAt(_position, target, up);
            _viewProjectionMatrix = _viewMatrix * _projectionMatrix;
            _dirty = false;
        }

        private static Vector3 Unproject(float x, float y, float z, Matrix4x4 inverseViewProjection)
        {
            Vector4 result = new(
                x * inverseViewProjection.M11 + y * inverseViewProjection.M21 + z * inverseViewProjection.M31 + inverseViewProjection.M41,
                x * inverseViewProjection.M12 + y * inverseViewProjection.M22 + z * inverseViewProjection.M32 + inverseViewProjection.M42,
                x * inverseViewProjection.M13 + y * inverseViewProjection.M23 + z * inverseViewProjection.M33 + inverseViewProjection.M43,
                x * inverseViewProjection.M14 + y * inverseViewProjection.M24 + z * inverseViewProjection.M34 + inverseViewProjection.M44);

            if (!float.IsFinite(result.W) || System.MathF.Abs(result.W) <= 1e-12f)
                throw new InvalidOperationException("Could not unproject a point at infinity.");

            return new Vector3(result.X / result.W, result.Y / result.W, result.Z / result.W);
        }
    }
}
