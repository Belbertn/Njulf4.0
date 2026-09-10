using Njulf.Core.Math;
using Njulf.Rendering.Resources;

namespace Njulf.Graphics;

/// <summary>An owned geometry reference. Scene objects retain independent references.</summary>
internal sealed class VulkanMesh : Mesh, IMesh, IDisposable, IResourceReference
{
    private readonly Action<MeshHandle> _retain, _release;
    private readonly Action _validate;
    private bool _ownsReference = true;
    internal VulkanMesh AsBorrowedView() { _ownsReference = false; return this; }
    internal object OwnerIdentity { get; }
    internal MeshHandle Handle { get; }
    private readonly BoundingBox _bounds;
    private Func<BoundingBox>? _boundsResolver;
    private MeshChangeSource? _changes;
    public override BoundingBox Bounds => _boundsResolver?.Invoke() ?? _bounds;
    public override MeshUsage Usage => _changes == null ? MeshUsage.Static : MeshUsage.Dynamic;
    internal override event Action? ContentChanged { add { if (_changes != null) _changes.Changed += value; } remove { if (_changes != null) _changes.Changed -= value; } }
    public override bool IsDisposed { get; protected set; }
    internal VulkanMesh(VulkanGraphicsDevice owner, MeshHandle handle)
        : this(owner.Context, handle, owner.GetMeshBounds(handle), owner.RetainMesh, owner.ReleaseMesh,
            () => { owner.EnsureUsable(); owner.GetMeshBounds(handle); })
    { _changes = owner.Meshes.GetChangeSource(handle); if (_changes != null) _boundsResolver = () => owner.GetMeshBounds(handle); }
    internal VulkanMesh(object owner, MeshHandle handle, BoundingBox bounds,
        Action<MeshHandle> retain, Action<MeshHandle> release, Action validate)
    { OwnerIdentity = owner; Handle = handle; _bounds = bounds; _retain = retain; _release = release; _validate = validate; }
    object IResourceReference.OwnerIdentity => OwnerIdentity;
    void IResourceReference.Validate() { ObjectDisposedException.ThrowIf(IsDisposed, this); _validate(); }
    IResourceReference IResourceReference.Retain()
    {
        ((IResourceReference)this).Validate();
        var copy = new VulkanMesh(OwnerIdentity, Handle, Bounds, _retain, _release, _validate) { _changes = _changes, _boundsResolver = _boundsResolver };
        _retain(Handle);
        return copy;
    }
    bool IResourceReference.HasSameIdentity(IGraphicsResource other) =>
        other is VulkanMesh mesh && ReferenceEquals(OwnerIdentity, mesh.OwnerIdentity) && Handle == mesh.Handle;
    public override bool Equals(object? obj) => obj is VulkanMesh mesh &&
        ReferenceEquals(OwnerIdentity, mesh.OwnerIdentity) && Handle == mesh.Handle;
    public override int GetHashCode() => HashCode.Combine(OwnerIdentity, Handle);
    void IResourceReference.Release() => Dispose();
    void IResourceReference.AbandonTransferredReference() => IsDisposed = true;
    /// <summary>Releases this reference. GPU use completes before manager reclamation.</summary>
    public override void Dispose() { if (IsDisposed) return; if (_ownsReference) _release(Handle); IsDisposed = true; }
}
