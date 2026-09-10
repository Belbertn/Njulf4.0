using Njulf.Rendering.Resources;

namespace Njulf.Graphics;

/// <summary>An owned sampled texture reference. Materials retain their own dependencies.</summary>
internal sealed class VulkanTexture : Texture, ITexture, IDisposable
{
    internal VulkanGraphicsDevice Owner { get; }
    internal TextureHandle Handle { get; }
    public override TextureFormat Format => Owner.TextureResources.GetGraphicsDescription(Handle).Format;
    public override int MipLevels => Owner.TextureResources.GetGraphicsDescription(Handle).MipLevels;
    public override int Width { get; }
    public override int Height { get; }
    public override TextureColorSpace ColorSpace { get; }
    public override bool IsDisposed { get; protected set; }
    internal VulkanTexture(VulkanGraphicsDevice owner, TextureHandle handle, int width, int height, TextureColorSpace colorSpace)
    { Owner = owner; Handle = handle; Width = width; Height = height; ColorSpace = colorSpace; }
    /// <summary>Releases this reference after outstanding GPU use; materials remain usable.</summary>
    public override void Dispose() { if (IsDisposed) return; Owner.ReleaseTexture(Handle); IsDisposed = true; }
}
