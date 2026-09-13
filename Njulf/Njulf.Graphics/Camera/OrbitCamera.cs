using Njulf.Core.Math;

namespace Njulf.Core.Camera
{
    public class OrbitCamera : CameraBase
    {
        private Vector3 _target;
        private Vector3 _upReference = Vector3.UnitY;
        private float _distance = 10f;
        private float _latitude = 0.5f; // 0 = south pole, 0.5 = equator, 1 = north pole
        private float _longitude = 0f; // 0 = front, 0.25 = right, 0.5 = back, 0.75 = left
        private float _minDistance = 1f;
        private float _maxDistance = 100f;

        public Vector3 Target
        {
            get => _target;
            set { _target = value; UpdatePosition(); }
        }

        public float Distance
        {
            get => _distance;
            set { _distance = System.Math.Clamp(value, _minDistance, _maxDistance); UpdatePosition(); }
        }

        public float Latitude
        {
            get => _latitude;
            set { _latitude = System.Math.Clamp(value, 0f, 1f); UpdatePosition(); }
        }

        public float Longitude
        {
            get => _longitude;
            set { _longitude = value % 1f; if (_longitude < 0) _longitude += 1f; UpdatePosition(); }
        }

        public float MinDistance
        {
            get => _minDistance;
            set { _minDistance = value; _distance = System.Math.Max(_distance, _minDistance); }
        }

        public float MaxDistance
        {
            get => _maxDistance;
            set { _maxDistance = value; _distance = System.Math.Min(_distance, _maxDistance); }
        }

        public override Vector3 Forward => (_target - Position).Normalized();
        public override Vector3 Right => LookAtBasis(Position, _target, _upReference).Right;
        public override Vector3 Up => LookAtBasis(Position, _target, _upReference).Up;

        public OrbitCamera() : base()
        {
            UpdatePosition();
        }

        public OrbitCamera(Vector3 target, float distance = 10f) : base()
        {
            _target = target;
            _distance = distance;
            UpdatePosition();
        }

        private void UpdatePosition()
        {
            float theta = _longitude * 2f * (float)System.Math.PI;
            float phi = (_latitude - 0.5f) * (float)System.Math.PI;

            float x = _distance * (float)System.Math.Cos(phi) * (float)System.Math.Cos(theta);
            float y = _distance * (float)System.Math.Sin(phi);
            float z = _distance * (float)System.Math.Cos(phi) * (float)System.Math.Sin(theta);

            Position = _target + new Vector3(x, y, z);
        }

        protected override Matrix4x4 CalculateViewMatrix()
        {
            return Matrix4x4.CreateLookAt(Position, _target, Up);
        }

        public override void LookAt(Vector3 target, Vector3 up)
        {
            _ = LookAtBasis(Position, target, up);
            Vector3 offset = Position - target;
            _target = target;
            _upReference = up.Normalized();
            _distance = offset.Length();
            _latitude = .5f + System.MathF.Asin(System.Math.Clamp(offset.Y / _distance, -1f, 1f)) / System.MathF.PI;
            _longitude = System.MathF.Atan2(offset.Z, offset.X) / (2 * System.MathF.PI);
            if (_longitude < 0) _longitude += 1;
            Update();
        }

        public void Rotate(float deltaLongitude, float deltaLatitude)
        {
            Longitude += deltaLongitude;
            Latitude += deltaLatitude;
        }

        public void Zoom(float delta)
        {
            Distance += delta;
        }
    }
}
