using Njulf.Rendering.Memory;

namespace Njulf.Graphics;

/// <summary>An owned storage/transfer buffer. Registered passes retain independent references.</summary>
internal sealed class VulkanGraphicsBuffer : GraphicsBuffer, IDisposable
{
    internal VulkanGraphicsDevice Owner { get; }
    internal BufferHandle Handle { get; }
    internal int References = 1;
    public override ulong SizeInBytes { get; }
    public override bool IsDisposed { get; protected set; }
    internal VulkanGraphicsBuffer(VulkanGraphicsDevice owner, BufferHandle handle, ulong size)
    { Owner = owner; Handle = handle; SizeInBytes = size; }
    internal void Retain() { Owner.EnsureUsable(); checked { References++; } }
    internal void Release() => Owner.DeferRelease(() =>
    {
        if (References == 1) Owner.Buffers.DestroyBuffer(Handle);
        References--;
    });
    public override void Dispose() { if (IsDisposed) return; Release(); IsDisposed = true; }
}
