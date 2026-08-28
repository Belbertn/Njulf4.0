using System.Collections.Generic;
using Njulf.Rendering.Data;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Narrow ownership boundary used by materials. A material registration owns
/// one reference per bound texture occurrence and per logical material
/// reference, even when several bindings resolve to the same texture handle.
/// </summary>
internal interface ITextureReferenceManager
{
    void RetainTexture(TextureHandle handle);

    void ReleaseTexture(TextureHandle handle, Fence retireFence = default);
}

/// <summary>
/// Optional fast path for managers that can validate and retain a complete
/// texture set atomically under one ownership lock.
/// </summary>
internal interface IBulkTextureReferenceManager : ITextureReferenceManager
{
    void RetainTextures(IReadOnlyList<TextureHandle> handles);
}
