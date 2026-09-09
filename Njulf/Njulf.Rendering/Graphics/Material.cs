using Njulf.Rendering.Resources;

namespace Njulf.Graphics;

/// <summary>An owned material reference. Scene properties expose a borrowed IMaterial view.</summary>
internal sealed class VulkanMaterial : Material, IMaterial, IDisposable, IResourceReference
{
    private readonly Action<MaterialHandle> _retain, _release;
    private readonly Action _validate;
    private bool _ownsReference = true;
    internal VulkanMaterial AsBorrowedView() { _ownsReference = false; return this; }
    internal object OwnerIdentity { get; }
    internal MaterialHandle Handle { get; }
    private readonly string _name;
    internal Func<MaterialHandle, string>? NameResolver { get; init; }
    public override string Name => NameResolver?.Invoke(Handle) ?? _name;
    public override bool IsDisposed { get; protected set; }
    internal VulkanMaterial(VulkanGraphicsDevice owner, MaterialHandle handle)
        : this(owner.Context, handle, owner.GetMaterialName(handle), owner.RetainMaterial, owner.ReleaseMaterial,
            () => { owner.EnsureUsable(); owner.GetMaterialName(handle); })
    { NameResolver = owner.GetMaterialName; }
    internal VulkanMaterial(object owner, MaterialHandle handle, string name,
        Action<MaterialHandle> retain, Action<MaterialHandle> release, Action validate)
    { OwnerIdentity = owner; Handle = handle; _name = name; _retain = retain; _release = release; _validate = validate; }
    object IResourceReference.OwnerIdentity => OwnerIdentity;
    void IResourceReference.Validate() { ObjectDisposedException.ThrowIf(IsDisposed, this); _validate(); }
    IResourceReference IResourceReference.Retain()
    {
        ((IResourceReference)this).Validate();
        var copy = new VulkanMaterial(OwnerIdentity, Handle, _name, _retain, _release, _validate) { NameResolver = NameResolver };
        _retain(Handle);
        return copy;
    }
    bool IResourceReference.HasSameIdentity(IGraphicsResource other) =>
        other is VulkanMaterial material && ReferenceEquals(OwnerIdentity, material.OwnerIdentity) && Handle == material.Handle;
    void IResourceReference.Release() => Dispose();
    void IResourceReference.AbandonTransferredReference() => IsDisposed = true;
    /// <summary>Releases this reference; other owners and pending GPU work remain protected.</summary>
    public override void Dispose() { if (IsDisposed) return; if (_ownsReference) _release(Handle); IsDisposed = true; }
}
