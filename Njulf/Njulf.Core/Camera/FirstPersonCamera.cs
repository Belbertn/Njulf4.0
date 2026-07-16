using Njulf.Core.Math;

namespace Njulf.Core.Camera
{
    public class FirstPersonCamera : CameraBase
    {
        private Vector3 _forward = -Vector3.UnitZ;
        private Vector3 _right = Vector3.UnitX;
        private Vector3 _up = Vector3.UnitY;
        private float _yaw = 0f;
        private float _pitch = 0f;
        private float _roll = 0f;

        public float Yaw
        {
            get => _yaw;
            set
            {
                _yaw = value;
                UpdateOrientation();
            }
        }

        public float Pitch
        {
            get => _pitch;
            set
            {
                _pitch = System.Math.Clamp(value, -1.570796f, 1.570796f); // Clamp to ~90 degrees
                UpdateOrientation();
            }
        }

        public float Roll
        {
            get => _roll;
            set
            {
                _roll = value;
                UpdateOrientation();
            }
        }

        public override Vector3 Forward => _forward;
        public override Vector3 Right => _right;
        public override Vector3 Up => _up;

        public FirstPersonCamera() : base()
        {
            UpdateOrientation();
        }

        public FirstPersonCamera(Vector3 position) : base(position)
        {
            UpdateOrientation();
        }

        public FirstPersonCamera(Vector3 position, float yaw, float pitch, float roll = 0f) : base(position)
        {
            _yaw = yaw;
            _pitch = pitch;
            _roll = roll;
            UpdateOrientation();
        }

        private void UpdateOrientation()
        {
            Matrix4x4 rotation = Matrix4x4.CreateRotationZ(_roll) *
                               Matrix4x4.CreateRotationX(_pitch) *
                               Matrix4x4.CreateRotationY(_yaw);
            _forward = -Vector3.UnitZ * rotation;
            _right = Vector3.UnitX * rotation;
            _up = Vector3.UnitY * rotation;
            Update();
        }

        protected override Matrix4x4 CalculateViewMatrix()
        {
            return Matrix4x4.CreateLookAt(Position, Position + _forward, _up);
        }

        public void MoveForward(float amount)
        {
            Position += _forward * amount;
        }

        public void MoveBackward(float amount)
        {
            Position -= _forward * amount;
        }

        public void MoveRight(float amount)
        {
            Position += _right * amount;
        }

        public void MoveLeft(float amount)
        {
            Position -= _right * amount;
        }

        public void MoveUp(float amount)
        {
            Position += _up * amount;
        }

        public void MoveDown(float amount)
        {
            Position -= _up * amount;
        }

        public void RotateYawPitch(float yaw, float pitch)
        {
            Yaw += yaw;
            Pitch += pitch;
        }
    }
}
