using Njulf.Core.Math;
using Njulf.Graphics;

namespace Njulf.Core.Scene;

public partial class RenderObject
{
    private IMesh? _mesh;
    private IMaterial? _material;
    private object? _resourceOwner;
    private List<IResourceReference>? _pendingReleases;

    public RenderObject() { }
    public RenderObject(IMesh? mesh, IMaterial? material)
    {
        try { Mesh = mesh; Material = material; }
        catch (Exception acquisitionFailure)
        {
            try { Dispose(); }
            catch (Exception rollbackFailure) { throw new AggregateException(acquisitionFailure, rollbackFailure); }
            throw;
        }
    }

    /// <summary>Borrowed geometry view. Assignment acquires an independent reference and updates local bounds.</summary>
    public IMesh? Mesh
    {
        get { lock (_resourceLock) return _mesh; }
        set
        {
            IMesh? old;
            BoundingBox? oldBounds, newBounds;
            lock (_resourceLock)
            {
                old = _mesh;
                oldBounds = GetWorldBounds();
                BoundingBox? candidateBounds = value?.Bounds;
                IMesh? next = ReplaceResource(old, value);
                if (ReferenceEquals(next, old)) return;
                _mesh = next;
                _localMeshBounds = candidateBounds;
                _dirty = true;
                newBounds = GetWorldBounds();
            }
            PublishChange(SceneMutationKind.Geometry, oldBounds, newBounds, old, _mesh);
        }
    }

    /// <summary>Borrowed material view. Assignment acquires an independent reference.</summary>
    public IMaterial? Material
    {
        get { lock (_resourceLock) return _material; }
        set
        {
            IMaterial? old;
            BoundingBox? bounds;
            lock (_resourceLock)
            {
                old = _material;
                bounds = GetWorldBounds();
                IMaterial? next = ReplaceResource(old, value);
                if (ReferenceEquals(next, old)) return;
                _material = next;
                _dirty = true;
            }
            PublishChange(SceneMutationKind.Material, bounds, bounds, old, _material);
        }
    }

    private IResourceReference RequireReference(IGraphicsResource resource)
    {
        ObjectDisposedException.ThrowIf(resource.IsDisposed, resource);
        if (resource is not IResourceReference reference)
            throw new ArgumentException("Resource was not created by the framework.", nameof(resource));
        reference.Validate();
        if (_resourceOwner != null && !ReferenceEquals(_resourceOwner, reference.OwnerIdentity))
            throw new ArgumentException("Resource belongs to another graphics device.", nameof(resource));
        return reference;
    }

    private T? ReplaceResource<T>(T? old, T? candidate) where T : class, IGraphicsResource
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_materialTransferInProgress) throw new InvalidOperationException("A material transfer is in progress.");
        IResourceReference? next = candidate == null ? null : RequireReference(candidate);
        if (next != null && old != null && next.HasSameIdentity(old)) return old;
        if (next == null && old == null) return null;
        _pendingReleases ??= new();
        _pendingReleases.EnsureCapacity(_pendingReleases.Count + 1);
        IResourceReference? acquired = next?.Retain();
        try { (old as IResourceReference)?.Release(); }
        catch (Exception releaseFailure)
        {
            if (acquired != null)
            {
                try { acquired.Release(); }
                catch (Exception rollbackFailure)
                {
                    _pendingReleases.Add(acquired);
                    throw new AggregateException(releaseFailure, rollbackFailure);
                }
            }
            throw;
        }
        if (next != null) _resourceOwner ??= next.OwnerIdentity;
        return (T?)acquired;
    }

    // Uploaders transfer newly registered references; no second retain is performed.
    internal void AdoptResources(IMesh? mesh, IMaterial? material)
    {
        lock (_resourceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mesh != null || _material != null) throw new InvalidOperationException("Object already owns resources.");
            IResourceReference? meshReference = mesh == null ? null : RequireReference(mesh);
            IResourceReference? materialReference = material == null ? null : RequireReference(material);
            if (meshReference != null && materialReference != null && !ReferenceEquals(meshReference.OwnerIdentity, materialReference.OwnerIdentity))
                throw new ArgumentException("Resources belong to different devices.");
            _resourceOwner = meshReference?.OwnerIdentity ?? materialReference?.OwnerIdentity;
            _mesh = mesh;
            _material = material;
            _localMeshBounds = mesh?.Bounds;
        }
    }

    internal IMaterial TransferMaterialOwnership(IMaterial expectedMaterial, Func<IMaterial> replacementFactory)
    {
        IMaterial old, replacement;
        lock (_resourceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_materialTransferInProgress) throw new InvalidOperationException("Material transfer is already in progress.");
            if (_material == null || expectedMaterial is not IResourceReference expected || !expected.HasSameIdentity(_material))
                throw new InvalidOperationException("The object's material changed before the transfer.");
            RequireReference(expectedMaterial);
            old = _material;
            _materialTransferInProgress = true;
            try
            {
                replacement = replacementFactory();
                // Factory is an internal transaction: it validates before manager commit.
                _material = replacement;
                if (!ReferenceEquals(old, replacement)) ((IResourceReference)old).AbandonTransferredReference();
                _dirty = true;
            }
            finally { _materialTransferInProgress = false; }
        }
        if (!ReferenceEquals(old, replacement))
            PublishChange(SceneMutationKind.Material, GetWorldBounds(), GetWorldBounds(), old, replacement);
        return replacement;
    }

    public void Dispose()
    {
        lock (_resourceLock)
        {
            if (_materialTransferInProgress) throw new InvalidOperationException("Material transfer is in progress.");
            _disposed = true;
            List<Exception>? failures = null;
            try { (_mesh as IResourceReference)?.Release(); _mesh = null; }
            catch (Exception e) { (failures ??= new()).Add(e); }
            try { (_material as IResourceReference)?.Release(); _material = null; }
            catch (Exception e) { (failures ??= new()).Add(e); }
            if (_pendingReleases != null)
                for (int i = _pendingReleases.Count - 1; i >= 0; i--)
                    try { _pendingReleases[i].Release(); _pendingReleases.RemoveAt(i); }
                    catch (Exception e) { (failures ??= new()).Add(e); }
            if (failures != null) throw new AggregateException("Resource release failed; disposal can be retried.", failures);
        }
    }
}
