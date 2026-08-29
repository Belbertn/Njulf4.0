using Njulf.Core.Geometry;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Debug;

internal interface IDebugOverlayResourceLookup
{
    bool TryGetMaterialMetadata(
        MaterialHandle handle,
        out MaterialRenderMetadata metadata);

    bool TryGetMeshInfo(MeshHandle handle, out MeshInfo meshInfo);

    bool TryGetMeshlet(
        MeshHandle mesh,
        uint index,
        out Meshlet meshlet);
}

internal sealed class RendererDebugOverlayResourceLookup :
    IDebugOverlayResourceLookup
{
    private readonly MeshManager _meshManager;
    private readonly MaterialManager _materialManager;

    internal RendererDebugOverlayResourceLookup(
        MeshManager meshManager,
        MaterialManager materialManager)
    {
        _meshManager = meshManager ??
            throw new ArgumentNullException(nameof(meshManager));
        _materialManager = materialManager ??
            throw new ArgumentNullException(nameof(materialManager));
    }

    public bool TryGetMaterialMetadata(
        MaterialHandle handle,
        out MaterialRenderMetadata metadata)
    {
        try
        {
            metadata = _materialManager.GetMaterialMetadata(handle);
            return true;
        }
        catch (ArgumentException)
        {
            metadata = null!;
            return false;
        }
        catch (InvalidOperationException)
        {
            metadata = null!;
            return false;
        }
    }

    public bool TryGetMeshInfo(MeshHandle handle, out MeshInfo meshInfo)
    {
        try
        {
            meshInfo = _meshManager.GetMeshInfo(handle);
            return true;
        }
        catch (ArgumentException)
        {
            meshInfo = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            meshInfo = default;
            return false;
        }
    }

    public bool TryGetMeshlet(
        MeshHandle mesh,
        uint index,
        out Meshlet meshlet)
    {
        try
        {
            meshlet = _meshManager.GetMeshlet(mesh, index);
            return true;
        }
        catch (ArgumentException)
        {
            meshlet = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            meshlet = default;
            return false;
        }
    }
}
