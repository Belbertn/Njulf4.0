using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Graphics;

internal sealed partial class VulkanGraphicsDevice
{
    /// <summary>Creates a linear RGBA16F render target. Contents are initially undefined.</summary>
    public override VulkanRenderTarget2D CreateRenderTarget2D(int width, int height)
    {
        EnsureUsable();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!QueryRenderTargetSupport()) throw new NotSupportedException("RGBA16F sampled/storage/color-attachment render targets are unavailable on this device.");
        var handle = Textures.CreateTexture((uint)width, (uint)height, Format.R16G16B16A16Sfloat,
            additionalUsage: ImageUsageFlags.StorageBit | ImageUsageFlags.ColorAttachmentBit,
            debugName: "Graphics render target");
        try
        {
            Textures.GetGraphImage(handle, writable: true);
            return new(this, handle, width, height);
        }
        catch { ReleaseTexture(handle); throw; }
    }

    /// <summary>Creates an unmapped buffer for storage shaders and transfer commands.</summary>
    public override VulkanGraphicsBuffer CreateBuffer(ulong sizeInBytes)
    {
        EnsureUsable();
        if (sizeInBytes == 0) throw new ArgumentOutOfRangeException(nameof(sizeInBytes));
        var handle = Buffers.CreateBuffer(sizeInBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.AutoPreferDevice, debugName: "Graphics buffer");
        return new(this, handle, sizeInBytes);
    }

    internal TextureManager TextureResources => Textures;
    internal TextureHandle ValidateTexture(ITexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(texture.IsDisposed, texture);
        return texture switch
        {
            VulkanTexture t when ReferenceEquals(t.Owner, this) => t.Handle,
            VulkanRenderTarget2D t when ReferenceEquals(t.Owner, this) => t.Handle,
            _ => throw new ArgumentException("Texture belongs to another graphics device.", nameof(texture))
        };
    }
}
