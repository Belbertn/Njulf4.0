using Njulf.Rendering.Resources;

namespace Njulf.Graphics;

/// <summary>
/// A one-mip, one-sample linear RGBA16F image supporting sampling, storage,
/// color attachment and transfers. Replace this object to resize it.
/// Materials and registered passes retain independent references.
/// </summary>
internal sealed class VulkanRenderTarget2D : RenderTarget2D, ITexture, IDisposable
{
    internal VulkanGraphicsDevice Owner { get; }
    internal TextureHandle Handle { get; }
    public override int Width { get; }
    public override int Height { get; }
    public override TextureColorSpace ColorSpace => TextureColorSpace.Linear;
    public override bool IsDisposed { get; protected set; }
    internal VulkanRenderTarget2D(VulkanGraphicsDevice owner, TextureHandle handle, int width, int height)
    { Owner = owner; Handle = handle; Width = width; Height = height; }
    public override void Dispose() { if (IsDisposed) return; Owner.ReleaseTexture(Handle); IsDisposed = true; }
}
