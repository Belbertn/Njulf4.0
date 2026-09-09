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
}
/// <summary>A typed view of a sampled 2D texture.</summary>
public interface ITexture : IGraphicsResource
{
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
