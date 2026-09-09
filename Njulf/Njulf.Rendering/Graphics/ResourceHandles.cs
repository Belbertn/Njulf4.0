using System.Runtime.CompilerServices;
using Njulf.Graphics;

namespace Njulf.Rendering.Resources;

/// <summary>Advanced renderer-handle inspection. Handles are borrowed, device-local identities.</summary>
public static class ResourceHandles
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetMeshHandle(this object? resource, out MeshHandle handle)
    {
        handle = resource switch { VulkanMesh mesh => mesh.Handle, MeshHandle raw => raw, _ => MeshHandle.Invalid };
        return handle.IsValid;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetMaterialHandle(this object? resource, out MaterialHandle handle)
    {
        handle = resource switch { VulkanMaterial material => material.Handle, MaterialHandle raw => raw, _ => MaterialHandle.Invalid };
        return handle.IsValid;
    }
    public static MeshHandle GetMeshHandle(this IMesh resource) => resource.TryGetMeshHandle(out var handle)
        ? handle : throw new ArgumentException("Not a renderer mesh.", nameof(resource));
    public static MaterialHandle GetMaterialHandle(this IMaterial resource) => resource.TryGetMaterialHandle(out var handle)
        ? handle : throw new ArgumentException("Not a renderer material.", nameof(resource));
}
