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
        private readonly object _resourceLock = new();
        private RenderResourceLifetime? _resourceLifetime;
        private bool _ownsMesh;
        private bool _ownsMaterial;
        private bool _materialTransferInProgress;
        private List<PendingResourceRelease>? _pendingReleases;
        private bool _visible = true;
        private bool _enabled = true;
        private int _updateOrder;
        private bool _disposed;
        private BoundingBox? _localMeshBounds;
        private bool _isStatic;
        private ulong _revision = 1;

        /// <summary>
        /// Raised only when transport-relevant object state actually changes.
        /// Scene-level systems subscribe once when the object is added instead
        /// of comparing every object signature every frame.
        /// </summary>
        public event Action<RenderObjectMutation>? Changed;

        public ulong Revision => _revision;

        public Matrix4x4 WorldMatrix
        {
            get => GetWorldMatrix();
            set => SetWorldMatrix(value);
        }

        public object? Mesh
        {
            get
            {
                lock (_resourceLock)
                    return _mesh;
            }
            set
            {
                object? previous;
                BoundingBox? bounds = GetWorldBounds();
                lock (_resourceLock)
                {
                    if (Equals(_mesh, value))
                        return;
                    previous = _mesh;
                    SetResource(
                        ref _mesh,
                        ref _ownsMesh,
                        value,
                        _resourceLifetime?.RetainMesh,
                        _resourceLifetime?.ReleaseMesh);
                    _dirty = true;
                }
                PublishChange(
                    SceneMutationKind.Geometry,
                    bounds,
                    bounds,
                    previous,
                    value);
            }
        }

        /// <summary>
        /// Gets or sets the axis-aligned bounds of <see cref="Mesh"/> in mesh-local space.
        /// Imported models populate this metadata so systems that do not own the mesh registry
        /// can still derive geometry-aware world bounds. Hand-authored objects may leave it unset.
        /// </summary>
        public BoundingBox? LocalMeshBounds
        {
            get => _localMeshBounds;
            set
            {
                if (_localMeshBounds.Equals(value))
                    return;

                BoundingBox? oldBounds = GetWorldBounds();
                _localMeshBounds = value;
                PublishChange(
                    SceneMutationKind.Geometry,
                    oldBounds,
                    GetWorldBounds());
            }
        }

        public object? Material
        {
            get
            {
                lock (_resourceLock)
                    return _material;
            }
            set
            {
                object? previous;
                BoundingBox? bounds = GetWorldBounds();
                lock (_resourceLock)
                {
                    if (_materialTransferInProgress)
                    {
                        throw new InvalidOperationException(
                            "The render object's material is already participating in an ownership transfer.");
                    }
                    if (Equals(_material, value))
                        return;
                    previous = _material;
                    SetResource(
                        ref _material,
                        ref _ownsMaterial,
                        value,
                        _resourceLifetime?.RetainMaterial,
                        _resourceLifetime?.ReleaseMaterial);
                    _dirty = true;
                }
                PublishChange(
                    SceneMutationKind.Material,
                    bounds,
                    bounds,
                    previous,
                    value);
            }
        }

        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value)
                    return;
                BoundingBox? bounds = GetWorldBounds();
                _visible = value;
                PublishChange(
                    SceneMutationKind.Visibility,
                    value ? null : bounds,
                    value ? bounds : null);
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;
                BoundingBox? bounds = GetWorldBounds();
                _enabled = value;
                PublishChange(
                    SceneMutationKind.Visibility,
                    value ? null : bounds,
                    value ? bounds : null);
            }
        }

        /// <summary>
        /// Marks geometry whose mesh and placement are normally stationary. Renderers may use
        /// this hint for bounded spatial residency; moving or skinned objects should leave it off.
        /// </summary>
        public bool IsStatic
        {
            get => _isStatic;
            set
            {
                if (_isStatic == value)
                    return;
                BoundingBox? bounds = GetWorldBounds();
                _isStatic = value;
                PublishChange(SceneMutationKind.Geometry, bounds, bounds);
            }
        }

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

        /// <summary>
        /// Controls whether this object is part of an authored scene document.
        /// Runtime-only diagnostics and fixtures can opt out when their mesh or
        /// material is generated in memory and therefore cannot be reloaded.
        /// </summary>
        public bool PersistInSceneDocument { get; set; } = true;

        /// <summary>True when the assigned world matrix contains shear or another non-TRS component.</summary>
        public bool HasNonTrsMatrix => _hasNonTrsMatrix;

        /// <summary>
        /// True when renderer retain/release callbacks are attached. Callers
        /// replacing a manager-transferred resource use this to distinguish an
        /// owned reference from a directly authored, manager-lifetime handle.
        /// </summary>
        public bool HasResourceLifetime
        {
            get
            {
                lock (_resourceLock)
                    return _resourceLifetime != null;
            }
        }

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

        /// <summary>
        /// Attaches renderer resource ownership to this object. Uploaders use
        /// <paramref name="retainCurrentResources"/> = <see langword="false"/>
        /// when transferring newly registered handles into a template; model
        /// instances use <see langword="true"/> to acquire independent shares.
        /// Release callbacks must throw only when no logical release was
        /// committed; physical cleanup failures should be retained and retried
        /// by the resource manager itself.
        /// </summary>
        public void AttachResourceLifetime(
            Action<object> retainMesh,
            Action<object> releaseMesh,
            Action<object> retainMaterial,
            Action<object> releaseMaterial,
            bool retainCurrentResources)
        {
            lock (_resourceLock)
            {
                AttachResourceLifetimeLocked(
                    retainMesh,
                    releaseMesh,
                    retainMaterial,
                    releaseMaterial,
                    retainCurrentResources);
            }
        }

        private void AttachResourceLifetimeLocked(
            Action<object> retainMesh,
            Action<object> releaseMesh,
            Action<object> retainMaterial,
            Action<object> releaseMaterial,
            bool retainCurrentResources)
        {
            ArgumentNullException.ThrowIfNull(retainMesh);
            ArgumentNullException.ThrowIfNull(releaseMesh);
            ArgumentNullException.ThrowIfNull(retainMaterial);
            ArgumentNullException.ThrowIfNull(releaseMaterial);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_materialTransferInProgress)
            {
                throw new InvalidOperationException(
                    "Resource lifetime cannot be attached during a material ownership transfer.");
            }
            if (_resourceLifetime != null)
            {
                throw new InvalidOperationException(
                    "Render-object resource lifetime is already attached.");
            }

            var lifetime = new RenderResourceLifetime(
                retainMesh,
                releaseMesh,
                retainMaterial,
                releaseMaterial);
            if (!retainCurrentResources)
            {
                _resourceLifetime = lifetime;
                _ownsMesh = _mesh != null;
                _ownsMaterial = _material != null;
                return;
            }

            bool meshRetained = false;
            try
            {
                if (_mesh != null)
                {
                    retainMesh(_mesh);
                    meshRetained = true;
                }
                if (_material != null)
                    retainMaterial(_material);
            }
            catch (Exception retainFailure)
            {
                if (!meshRetained)
                    throw;

                try
                {
                    releaseMesh(_mesh!);
                }
                catch (Exception rollbackFailure)
                {
                    // Preserve the successfully acquired mesh reference in a
                    // retryable, dispose-only object. Model.CreateInstance
                    // installs clones before attachment so its rollback sees
                    // this state.
                    _resourceLifetime = lifetime;
                    _ownsMesh = true;
                    _ownsMaterial = false;
                    _disposed = true;
                    throw new AggregateException(
                        "Render-object resource acquisition and rollback both failed.",
                        retainFailure,
                        rollbackFailure);
                }

                throw;
            }

            _resourceLifetime = lifetime;
            _ownsMesh = _mesh != null;
            _ownsMaterial = _material != null;
        }

        /// <summary>
        /// Replaces a material after its manager has atomically transferred the
        /// object's existing logical reference to the supplied handle.
        /// </summary>
        public void AdoptTransferredMaterial(object material)
        {
            object? previous;
            lock (_resourceLock)
            {
                ArgumentNullException.ThrowIfNull(material);
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_materialTransferInProgress)
                {
                    throw new InvalidOperationException(
                        "The render object's material is already participating in an ownership transfer.");
                }
                if (_resourceLifetime == null ||
                    !_ownsMaterial ||
                    _material == null)
                {
                    throw new InvalidOperationException(
                        "A transferred material requires an attached, currently owned material reference.");
                }

                previous = _material;
                _material = material;
                _dirty = true;
            }
            PublishChange(
                SceneMutationKind.Material,
                GetWorldBounds(),
                GetWorldBounds(),
                previous,
                material);
        }

        /// <summary>
        /// Keeps the current material stable while its manager prepares and
        /// commits a logical ownership transfer. The factory must publish all
        /// manager-side state before returning; a factory failure leaves this
        /// object unchanged, and a successful return is installed without
        /// invoking retain or release callbacks.
        /// </summary>
        internal object TransferMaterialOwnership(
            object expectedMaterial,
            Func<object> replacementFactory)
        {
            ArgumentNullException.ThrowIfNull(expectedMaterial);
            ArgumentNullException.ThrowIfNull(replacementFactory);

            object? previous = null;
            object replacement;
            lock (_resourceLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_materialTransferInProgress)
                {
                    throw new InvalidOperationException(
                        "The render object's material is already participating in an ownership transfer.");
                }
                if (!Equals(_material, expectedMaterial))
                {
                    throw new InvalidOperationException(
                        "The render object's material changed while an authored edit was being prepared.");
                }
                if (_resourceLifetime != null &&
                    (!_ownsMaterial || _material == null))
                {
                    throw new InvalidOperationException(
                        "A transferred material requires a currently owned material reference.");
                }

                _materialTransferInProgress = true;
                try
                {
                    replacement = replacementFactory() ??
                        throw new InvalidOperationException(
                            "A material ownership transfer cannot publish a null replacement.");
                    if (!Equals(_material, replacement))
                    {
                        previous = _material;
                        _material = replacement;
                        _dirty = true;
                    }
                }
                finally
                {
                    _materialTransferInProgress = false;
                }
            }

            if (previous != null)
            {
                PublishChange(
                    SceneMutationKind.Material,
                    GetWorldBounds(),
                    GetWorldBounds(),
                    previous,
                    replacement);
            }
            return replacement;
        }

        internal void CopyResourceLifetimeTo(RenderObject target)
        {
            ArgumentNullException.ThrowIfNull(target);
            RenderResourceLifetime? lifetime;
            lock (_resourceLock)
                lifetime = _resourceLifetime;
            if (lifetime == null)
                return;

            target.AttachResourceLifetime(
                lifetime.RetainMesh,
                lifetime.ReleaseMesh,
                lifetime.RetainMaterial,
                lifetime.ReleaseMaterial,
                retainCurrentResources: true);
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
            lock (_resourceLock)
                DisposeLocked();
        }

        private void DisposeLocked()
        {
            if (_materialTransferInProgress)
            {
                throw new InvalidOperationException(
                    "The render object cannot be disposed during a material ownership transfer.");
            }
            if (_disposed &&
                _resourceLifetime == null &&
                (_pendingReleases == null ||
                 _pendingReleases.Count == 0))
            {
                return;
            }
            _disposed = true;

            RenderResourceLifetime? lifetime = _resourceLifetime;
            if (lifetime == null)
            {
                _mesh = null;
                _material = null;
                return;
            }

            List<Exception>? failures = null;
            if (_ownsMesh && _mesh != null)
            {
                try
                {
                    lifetime.ReleaseMesh(_mesh);
                    _ownsMesh = false;
                    _mesh = null;
                }
                catch (Exception releaseFailure)
                {
                    (failures ??= new List<Exception>())
                        .Add(releaseFailure);
                }
            }
            else
            {
                _ownsMesh = false;
                _mesh = null;
            }

            if (_ownsMaterial && _material != null)
            {
                try
                {
                    lifetime.ReleaseMaterial(_material);
                    _ownsMaterial = false;
                    _material = null;
                }
                catch (Exception releaseFailure)
                {
                    (failures ??= new List<Exception>())
                        .Add(releaseFailure);
                }
            }
            else
            {
                _ownsMaterial = false;
                _material = null;
            }

            if (_pendingReleases != null)
            {
                for (int index = _pendingReleases.Count - 1;
                     index >= 0;
                     index--)
                {
                    PendingResourceRelease pending =
                        _pendingReleases[index];
                    try
                    {
                        pending.Release(pending.Resource);
                        _pendingReleases.RemoveAt(index);
                    }
                    catch (Exception releaseFailure)
                    {
                        (failures ??= new List<Exception>())
                            .Add(releaseFailure);
                    }
                }
            }

            if (!_ownsMesh &&
                !_ownsMaterial &&
                (_pendingReleases == null ||
                 _pendingReleases.Count == 0))
            {
                _resourceLifetime = null;
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more render-object resources could not be released.",
                    failures);
            }
        }

        private void SetResource(
            ref object? storage,
            ref bool ownsStorage,
            object? value,
            Action<object>? retain,
            Action<object>? release)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Equals(storage, value))
                return;

            object? previous = storage;
            bool ownedPrevious = ownsStorage;
            bool retained = false;
            if (value != null && retain != null)
            {
                PreparePendingRelease();
                retain(value);
                retained = true;
            }

            try
            {
                if (ownedPrevious &&
                    previous != null &&
                    release != null)
                {
                    release(previous);
                }
            }
            catch (Exception releaseFailure)
            {
                if (!retained || release == null)
                    throw;

                try
                {
                    release(value!);
                }
                catch (Exception rollbackFailure)
                {
                    _pendingReleases!.Add(
                        new PendingResourceRelease(
                            value!,
                            release));
                    throw new AggregateException(
                        "Render-object resource replacement and rollback both failed.",
                        releaseFailure,
                        rollbackFailure);
                }

                throw;
            }

            storage = value;
            ownsStorage = retained;
        }

        private void PreparePendingRelease()
        {
            _pendingReleases ??=
                new List<PendingResourceRelease>();
            _pendingReleases.EnsureCapacity(
                checked(_pendingReleases.Count + 1));
        }

        private sealed record RenderResourceLifetime(
            Action<object> RetainMesh,
            Action<object> ReleaseMesh,
            Action<object> RetainMaterial,
            Action<object> ReleaseMaterial);

        private readonly record struct PendingResourceRelease(
            object Resource,
            Action<object> Release);

        private void SetTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (!_hasNonTrsMatrix &&
                _position == position &&
                _rotation.Equals(rotation) &&
                _scale == scale)
            {
                return;
            }

            BoundingBox? oldBounds = GetWorldBounds();
            _position = position;
            _rotation = rotation;
            _scale = scale;
            _hasNonTrsMatrix = false;
            _dirty = true;
            PublishChange(
                SceneMutationKind.Transform,
                oldBounds,
                GetWorldBounds());
        }

        private void SetWorldMatrix(Matrix4x4 matrix)
        {
            Matrix4x4 current = GetWorldMatrix();
            if (current.Equals(matrix))
                return;
            BoundingBox? oldBounds = GetWorldBounds(current);

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
            PublishChange(
                SceneMutationKind.Transform,
                oldBounds,
                GetWorldBounds());
        }

        private BoundingBox? GetWorldBounds() =>
            GetWorldBounds(GetWorldMatrix());

        private BoundingBox? GetWorldBounds(Matrix4x4 matrix) =>
            _localMeshBounds is { } local
                ? BoundingBox.Transform(local, matrix)
                : null;

        /// <summary>
        /// Lets derived render-object state participate in the scene's render
        /// payload revision without exposing the mutation event for arbitrary
        /// external publication.
        /// </summary>
        protected void PublishDerivedChange(SceneMutationKind kind)
        {
            BoundingBox? bounds = GetWorldBounds();
            PublishChange(kind, bounds, bounds);
        }

        private void PublishChange(
            SceneMutationKind kind,
            BoundingBox? oldBounds,
            BoundingBox? newBounds,
            object? oldResource = null,
            object? newResource = null)
        {
            _revision = _revision == ulong.MaxValue ? 1UL : _revision + 1UL;
            Changed?.Invoke(new RenderObjectMutation(
                this,
                kind,
                oldBounds,
                newBounds,
                oldResource,
                newResource,
                _revision));
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
