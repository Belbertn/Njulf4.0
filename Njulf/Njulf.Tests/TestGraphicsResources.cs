using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Rendering.Resources;

namespace Njulf.Tests;

// CPU scene fixtures: handles identify geometry/material data supplied by the fixture.
// Lifetime tests supply their own retain/release callbacks instead of these no-ops.
internal static class TestGraphicsResources
{
    internal static Njulf.Core.Scene.RenderObject AdoptMaterial(MaterialManager manager, MaterialHandle handle)
    {
        var result = new Njulf.Core.Scene.RenderObject();
        result.AdoptResources(null, manager.AdoptResource(handle));
        return result;
    }
    internal static readonly object Owner = new();
    internal static IMesh Mesh(MeshHandle handle) => new VulkanMesh(Owner, handle,
        new BoundingBox(new Vector3(-1), new Vector3(1)), static _ => { }, static _ => { }, static () => { });
    internal static IMaterial Material(MaterialHandle handle) => new VulkanMaterial(Owner, handle,
        "Fixture material", static _ => { }, static _ => { }, static () => { });
    internal static Reference Mesh(string name) => new(name, Owner);
    internal static Reference Material(string name) => new(name, Owner);

    internal sealed class Reference(string name, object owner,
        Action<object>? retain = null, Action<object>? release = null) : IMesh, IMaterial, IResourceReference
    {
        public string Name => name;
        public BoundingBox Bounds => new(new Vector3(-1), new Vector3(1));
        public bool IsDisposed { get; private set; }
        public object OwnerIdentity => owner;
        public void Validate() => ObjectDisposedException.ThrowIf(IsDisposed, this);
        public IResourceReference Retain()
        {
            Validate();
            var copy = new Reference(name, owner, retain, release);
            retain?.Invoke(name);
            return copy;
        }
        public void Release() { if (IsDisposed) return; release?.Invoke(name); IsDisposed = true; }
        public void AbandonTransferredReference() => IsDisposed = true;
        public bool HasSameIdentity(IGraphicsResource other) => other is Reference r &&
            ReferenceEquals(owner, r.OwnerIdentity) && name == r.Name;
    }
}
