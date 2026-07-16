using System;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public class RenderObject : IRenderable, IUpdateable, IDisposable, IIdentifiedSceneEntity
    {
        private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
        private Vector3 _position;
        private Quaternion _rotation = Quaternion.Identity;
        private Vector3 _scale = Vector3.One;
        private bool _hasNonTrsMatrix;
        private object? _mesh;
        private object? _material;
        private bool _visible = true;
        private bool _enabled = true;
        private int _updateOrder;
        
        public Matrix4x4 WorldMatrix
        {
            get => GetWorldMatrix();
            set => SetWorldMatrix(value);
        }
        
        public object? Mesh
        {
            get => _mesh;
            set { _mesh = value; _dirty = true; }
        }

        /// <summary>
        /// Gets or sets the axis-aligned bounds of <see cref="Mesh"/> in mesh-local space.
        /// Imported models populate this metadata so systems that do not own the mesh registry
        /// can still derive geometry-aware world bounds. Hand-authored objects may leave it unset.
        /// </summary>
        public BoundingBox? LocalMeshBounds { get; set; }
        
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

        /// <summary>
        /// Marks geometry whose mesh and placement are normally stationary. Renderers may use
        /// this hint for bounded spatial residency; moving or skinned objects should leave it off.
        /// </summary>
        public bool IsStatic { get; set; }
        
        public int UpdateOrder
        {
            get => _updateOrder;
            set => _updateOrder = value;
        }
        
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "RenderObject";

        /// <summary>
        /// Source asset information retained on live instances so scenes can be saved without
        /// attempting to reverse-map renderer resource handles.
        /// </summary>
        public SceneAssetReference? AssetReference { get; set; }

        /// <summary>True when the assigned world matrix contains shear or another non-TRS component.</summary>
        public bool HasNonTrsMatrix => _hasNonTrsMatrix;

        public Vector3 Position
        {
            get => _position;
            set => SetTransform(value, _rotation, _scale);
        }

        public Quaternion Rotation
        {
            get => _rotation;
            set => SetTransform(_position, NormalizeRotation(value), _scale);
        }
        
        public Vector3 Scale
        {
            get => _scale;
            set => SetTransform(_position, _rotation, value);
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
        
        public virtual void Update(float deltaTime)
        {
            if (!_enabled) return;
            // Custom update logic can be added by subclasses
        }
        
        public Matrix4x4 GetWorldMatrix()
        {
            if (_dirty)
            {
                _cachedWorldMatrix = _hasNonTrsMatrix
                    ? _worldMatrix
                    : Matrix4x4.CreateScale(_scale) * _rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(_position);
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

        private void SetTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            _position = position;
            _rotation = rotation;
            _scale = scale;
            _hasNonTrsMatrix = false;
            _dirty = true;
        }

        private void SetWorldMatrix(Matrix4x4 matrix)
        {
            if (TryDecompose(matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale))
            {
                _position = position;
                _rotation = rotation;
                _scale = scale;
                _hasNonTrsMatrix = false;
            }
            else
            {
                _worldMatrix = matrix;
                _position = matrix.Translation;
                _rotation = Quaternion.Identity;
                _scale = matrix.Scale;
                _hasNonTrsMatrix = true;
            }

            _dirty = true;
        }

        private static Quaternion NormalizeRotation(Quaternion rotation)
        {
            float lengthSquared = rotation.LengthSquared();
            return float.IsFinite(lengthSquared) && lengthSquared > 1e-12f
                ? rotation.Normalized()
                : Quaternion.Identity;
        }

        private static bool TryDecompose(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            const float epsilon = 1e-6f;
            if (!float.IsFinite(matrix.M11) || !float.IsFinite(matrix.M12) || !float.IsFinite(matrix.M13) ||
                !float.IsFinite(matrix.M21) || !float.IsFinite(matrix.M22) || !float.IsFinite(matrix.M23) ||
                !float.IsFinite(matrix.M31) || !float.IsFinite(matrix.M32) || !float.IsFinite(matrix.M33) ||
                System.MathF.Abs(matrix.M14) > epsilon || System.MathF.Abs(matrix.M24) > epsilon ||
                System.MathF.Abs(matrix.M34) > epsilon || System.MathF.Abs(matrix.M44 - 1f) > epsilon)
            {
                position = default;
                rotation = default;
                scale = default;
                return false;
            }

            Vector3 row0 = new(matrix.M11, matrix.M12, matrix.M13);
            Vector3 row1 = new(matrix.M21, matrix.M22, matrix.M23);
            Vector3 row2 = new(matrix.M31, matrix.M32, matrix.M33);
            scale = new Vector3(row0.Length(), row1.Length(), row2.Length());
            if (scale.X <= epsilon || scale.Y <= epsilon || scale.Z <= epsilon)
            {
                position = default;
                rotation = default;
                return false;
            }

            row0 /= scale.X;
            row1 /= scale.Y;
            row2 /= scale.Z;
            if (System.MathF.Abs(Vector3.Dot(row0, row1)) > epsilon ||
                System.MathF.Abs(Vector3.Dot(row0, row2)) > epsilon ||
                System.MathF.Abs(Vector3.Dot(row1, row2)) > epsilon)
            {
                position = default;
                rotation = default;
                scale = default;
                return false;
            }

            if (Vector3.Dot(Vector3.Cross(row0, row1), row2) < 0f)
            {
                scale.X = -scale.X;
                row0 = -row0;
            }

            position = matrix.Translation;
            rotation = NormalizeRotation(Quaternion.FromMatrix4x4(new Matrix4x4(
                row0.X, row0.Y, row0.Z, 0f,
                row1.X, row1.Y, row1.Z, 0f,
                row2.X, row2.Y, row2.Z, 0f,
                0f, 0f, 0f, 1f)));
            Matrix4x4 recomposed = Matrix4x4.CreateScale(scale) * rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(position);
            return ApproximatelyEqual(matrix, recomposed, 0.0001f);
        }

        private static bool ApproximatelyEqual(Matrix4x4 a, Matrix4x4 b, float epsilon)
        {
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (System.MathF.Abs(a[row, column] - b[row, column]) > epsilon)
                    return false;

            return true;
        }
    }
}
