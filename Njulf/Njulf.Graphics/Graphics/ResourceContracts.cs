using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>A borrowed resource view. Obtaining a view does not acquire ownership.</summary>
public interface IGraphicsResource
{
    /// <summary>Whether this view's reference has been released; other owners may still retain the storage.</summary>
    bool IsDisposed { get; }
}
/// <summary>A typed view of indexed geometry.</summary>
public interface IMesh : IGraphicsResource
{
    /// <summary>Local-space bounds in scene units.</summary>
    BoundingBox Bounds { get; }
}
/// <summary>A typed view of a compiled material.</summary>
public interface IMaterial : IGraphicsResource
{
    /// <summary>Authored name of the compiled material.</summary>
    string Name { get; }
    /// <summary>Immutable snapshot of the current authored values and binding settings.</summary>
    MaterialDefinition Definition { get; }
    /// <summary>Retains an independently owned texture snapshot, or null for an unbound slot. Dispose the result.</summary>
    Texture? RetainTexture(MaterialTextureSlot slot);
    /// <summary>Atomically updates every user of this shared material, including deduplicated aliases.</summary>
    /// <remarks>Use RenderObject.UpdateMaterial for an isolated edit. Typed assignments replace or clear
    /// textures; omitted slots retain their texture. Raw handles must remain unchanged in omitted slots.
    /// Requires the device thread. The permanent default cannot be edited in place.</remarks>
    void UpdateShared(MaterialDefinition definition, ReadOnlySpan<MaterialTextureAssignment> textures = default);
}
/// <summary>A typed view of a sampled 2D texture.</summary>
public interface ITexture : IGraphicsResource
{
    /// <summary>Storage format, or Unknown for a content format outside the portable subset.</summary>
    TextureFormat Format => TextureFormat.Unknown;
    /// <summary>Number of allocated mip levels.</summary>
    int MipLevels => 1;
    /// <summary>Width in pixels.</summary>
    int Width { get; }
    /// <summary>Height in pixels.</summary>
    int Height { get; }
    /// <summary>Interpretation of stored color channels.</summary>
    TextureColorSpace ColorSpace { get; }
}


// Implemented only by framework-owned wrappers (and intentional test doubles).
// A retained reference is independent of the input wrapper's disposal state.
internal interface IResourceReference : IGraphicsResource
{
    object OwnerIdentity { get; }
    void Validate();
    IResourceReference Retain();
    void Release();
    void AbandonTransferredReference();
    bool HasSameIdentity(IGraphicsResource other);
}
