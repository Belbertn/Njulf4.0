using System.Runtime.InteropServices;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Graphics;

internal sealed partial class VulkanGraphicsDevice
{
    private readonly Queue<(Action<CommandBuffer> Record, Action Release)> _uploads = new();
    private readonly List<PendingReadback> _readbacks = new();

    private sealed class PendingReadback
    {
        internal required Action<CommandBuffer, BufferHandle> Record;
        internal required Action Release;
        internal required int Size;

        internal readonly TaskCompletionSource<byte[]> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenRegistration Cancellation;
        internal BufferHandle Storage;
        internal ulong Serial = ulong.MaxValue;
        internal bool Recorded;
    }

    private void EnsureTransferAllowed()
    {
        EnsureUsable();
        if (_lifetime.FrameInProgress)
            throw new InvalidOperationException("Queue resource transfers before BeginFrame, on the device thread.");
    }

    private VulkanGraphicsBuffer RequireBuffer(GraphicsBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(buffer.IsDisposed, buffer);
        if (buffer is not VulkanGraphicsBuffer native || !ReferenceEquals(native.Owner, this))
            throw new ArgumentException("Buffer belongs to another device.", nameof(buffer));
        return native;
    }

    private static void ValidateRange(ulong capacity, ulong offset, int count)
    {
        if (count <= 0 || offset > capacity || (ulong)count > capacity - offset)
            throw new ArgumentOutOfRangeException(nameof(count), "The nonempty range must fit the resource.");
    }

    internal unsafe BufferHandle StageBytes(ReadOnlySpan<byte> data)
    {
        var staging = Buffers.CreateStagingBuffer((ulong)data.Length, "Graphics upload");
        try
        {
            data.CopyTo(new Span<byte>(Buffers.GetMappedPointer(staging), data.Length));
            Buffers.FlushBuffer(staging, 0, (ulong)data.Length);
            return staging;
        }
        catch
        {
            Buffers.DestroyBuffer(staging);
            throw;
        }
    }

    internal unsafe void RecordBufferCopy(CommandBuffer cmd, BufferHandle source, BufferHandle destination,
        ulong offset, ulong count)
    {
        var before = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2, SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.TransferBit,
            DstAccessMask = AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit
        };
        var dependency = new DependencyInfo
            { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &before };
        Context.Api.CmdPipelineBarrier2(cmd, &dependency);
        var copy = new BufferCopy(0, offset, count);
        Context.Api.CmdCopyBuffer(cmd, Buffers.GetBuffer(source), Buffers.GetBuffer(destination), 1, &copy);
        before.SrcStageMask = PipelineStageFlags2.TransferBit;
        before.SrcAccessMask = AccessFlags2.TransferWriteBit;
        before.DstStageMask = PipelineStageFlags2.AllCommandsBit;
        before.DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
        Context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    public override GraphicsBuffer CreateBuffer(ulong sizeInBytes, ReadOnlySpan<byte> initialData)
    {
        EnsureTransferAllowed();
        ValidateRange(sizeInBytes, 0, initialData.Length);
        var buffer = CreateBuffer(sizeInBytes);
        try
        {
            UpdateBuffer(buffer, 0, initialData);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public override void UpdateBuffer(GraphicsBuffer buffer, ulong destinationOffsetBytes, ReadOnlySpan<byte> data)
    {
        EnsureTransferAllowed();
        var native = RequireBuffer(buffer);
        ValidateRange(native.SizeInBytes, destinationOffsetBytes, data.Length);
        var staging = StageBytes(data);
        int count = data.Length;
        native.Retain();
        _uploads.Enqueue((cmd => RecordBufferCopy(cmd, staging, native.Handle, destinationOffsetBytes, (ulong)count),
            () =>
            {
                Buffers.DestroyBuffer(staging);
                native.ReleaseCompleted();
            }));
    }

    private static void ValidateMip(Texture2DDescription d, int mip)
    {
        if ((uint)mip >= (uint)d.MipLevels) throw new ArgumentOutOfRangeException(nameof(mip));
    }

    private static void ValidateRectangle(Texture2DDescription d, int mip, TextureRectangle r, int bytes)
    {
        ValidateMip(d, mip);
        int width = Math.Max(1, d.Width >> mip), height = Math.Max(1, d.Height >> mip);
        if (r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || r.X > width - r.Width || r.Y > height - r.Height)
            throw new ArgumentOutOfRangeException(nameof(r));
        if (bytes != checked(r.Width * r.Height * TextureManager.PixelSize(d.Format)))
            throw new ArgumentException("Texture data must exactly fill the tightly packed rectangle.");
    }

    public override Texture CreateTexture2D(Texture2DDescription description,
        ReadOnlySpan<ReadOnlyMemory<byte>> mipData, bool generateMipmaps = false)
    {
        EnsureUsable();
        var d = description;
        if (d.Width <= 0 || d.Height <= 0 || d.MipLevels <= 0 ||
            d.MipLevels > 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(d.Width, d.Height)))
            throw new ArgumentOutOfRangeException(nameof(description));
        Context.Api.GetPhysicalDeviceProperties(Context.PhysicalDevice, out var properties);
        if ((uint)d.Width > properties.Limits.MaxImageDimension2D ||
            (uint)d.Height > properties.Limits.MaxImageDimension2D)
            throw new ArgumentOutOfRangeException(nameof(description), "Texture dimensions exceed the device limit.");
        if (mipData.Length != (generateMipmaps ? 1 : d.MipLevels))
            throw new ArgumentException("Supply every mip, or only mip zero for generation.", nameof(mipData));
        Textures.ValidateGraphicsFormat(d.Format, generateMipmaps);
        for (int mip = 0; mip < mipData.Length; mip++)
            ValidateRectangle(d, mip, new(0, 0, Math.Max(1, d.Width >> mip), Math.Max(1, d.Height >> mip)),
                mipData[mip].Length);
        var handle = Textures.CreateTexture((uint)d.Width, (uint)d.Height, TextureManager.NativeFormat(d.Format),
            (uint)d.MipLevels);
        try
        {
            // Initial creation retains the established synchronous upload contract.
            if (generateMipmaps)
                Textures.UploadTextureData(handle, mipData[0].Span, (uint)d.Width, (uint)d.Height,
                    TextureManager.NativeFormat(d.Format), true);
            else
            {
                using var stream = new MemoryStream();
                foreach (var level in mipData) stream.Write(level.Span);
                Textures.UploadTextureDataAllMipsAndLayers(handle, stream.ToArray(), (uint)d.Width, (uint)d.Height,
                    TextureManager.NativeFormat(d.Format));
            }

            Textures.PublishGraphicsPixels(handle, d, new(0, 0, d.Width, d.Height), mipData[0].Span);
            return new VulkanTexture(this, handle, d.Width, d.Height,
                d.Format is TextureFormat.Rgba8Srgb or TextureFormat.Bgra8Srgb
                    ? TextureColorSpace.Srgb
                    : TextureColorSpace.Linear);
        }
        catch
        {
            ReleaseTexture(handle);
            throw;
        }
    }

    public override void UpdateTexture2D(Texture texture, int mipLevel, TextureRectangle rectangle,
        ReadOnlySpan<byte> data)
    {
        EnsureTransferAllowed();
        var handle = ValidateTexture(texture);
        var d = Textures.GetGraphicsDescription(handle);
        ValidateRectangle(d, mipLevel, rectangle, data.Length);
        byte[] pixels = data.ToArray();
        var staging = StageBytes(pixels);
        Textures.RetainTexture(handle);
        _uploads.Enqueue((cmd =>
            {
                if (Textures.GetGraphicsDescription(handle) != d)
                    throw new InvalidOperationException("Texture storage changed before update recording.");
                Textures.RecordGraphicsUpdate(cmd, handle, mipLevel, rectangle, staging);
                if (mipLevel == 0) Textures.PublishGraphicsPixels(handle, d, rectangle, pixels);
                else Textures.PublishGraphicsMipChange(handle);
            },
            () =>
            {
                Buffers.DestroyBuffer(staging);
                Textures.ReleaseTexture(handle);
            }));
    }

    public override void GenerateMipmaps(Texture texture)
    {
        EnsureTransferAllowed();
        var handle = ValidateTexture(texture);
        var d = Textures.GetGraphicsDescription(handle);
        Textures.ValidateGraphicsFormat(d.Format, true);
        if (d.MipLevels == 1) return;
        Textures.RetainTexture(handle);
        _uploads.Enqueue((cmd =>
        {
            Textures.RecordGraphicsMipmaps(cmd, handle);
            Textures.PublishGraphicsMipChange(handle);
        }, () => Textures.ReleaseTexture(handle)));
    }

    public override unsafe Task<byte[]> ReadBufferAsync(GraphicsBuffer buffer, ulong offsetBytes, int byteCount,
        CancellationToken cancellationToken = default)
    {
        EnsureTransferAllowed();
        var native = RequireBuffer(buffer);
        ValidateRange(native.SizeInBytes, offsetBytes, byteCount);
        CheckReadbackCapacity();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<byte[]>(cancellationToken);
        native.Retain();
        return QueueReadback(byteCount, (cmd, target) =>
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2, SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.MemoryWriteBit, DstStageMask = PipelineStageFlags2.TransferBit,
                DstAccessMask = AccessFlags2.TransferReadBit
            };
            var dependency = new DependencyInfo
                { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
            Context.Api.CmdPipelineBarrier2(cmd, &dependency);
            var copy = new BufferCopy(offsetBytes, 0, (ulong)byteCount);
            Context.Api.CmdCopyBuffer(cmd, Buffers.GetBuffer(native.Handle), Buffers.GetBuffer(target), 1, &copy);
        }, native.ReleaseCompleted, cancellationToken);
    }

    public override Task<TextureReadback> ReadTexture2DAsync(ITexture texture, int mipLevel = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureTransferAllowed();
        var handle = ValidateTexture(texture);
        var d = Textures.GetGraphicsDescription(handle);
        ValidateMip(d, mipLevel);
        int width = Math.Max(1, d.Width >> mipLevel),
            height = Math.Max(1, d.Height >> mipLevel),
            pitch = checked(width * TextureManager.PixelSize(d.Format));
        int size = checked(pitch * height);
        CheckReadbackCapacity();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<TextureReadback>(cancellationToken);
        Textures.RetainTexture(handle);
        var task = QueueReadback(size, (cmd, target) =>
        {
            if (Textures.GetGraphicsDescription(handle) != d)
                throw new InvalidOperationException("Texture storage changed before readback recording.");
            Textures.RecordGraphicsReadback(cmd, handle, mipLevel, target);
        }, () => Textures.ReleaseTexture(handle), cancellationToken);
        return ConvertReadback(task, width, height, d.Format, pitch);
    }

    private static async Task<TextureReadback> ConvertReadback(Task<byte[]> task, int width, int height,
        TextureFormat format, int pitch) => new(width, height, format, pitch, await task.ConfigureAwait(false));

    private void CheckReadbackCapacity()
    {
        if (_readbacks.Count >= 8)
            throw new InvalidOperationException("At most eight graphics readbacks may be outstanding.");
    }

    private Task<byte[]> QueueReadback(int size, Action<CommandBuffer, BufferHandle> record, Action release,
        CancellationToken token)
    {
        var pending = new PendingReadback { Size = size, Record = record, Release = release };
        pending.Cancellation = token.Register(() => pending.Completion.TrySetCanceled(token));
        _readbacks.Add(pending);
        return pending.Completion.Task;
    }

    internal void RecordResourceUploads(CommandBuffer cmd)
    {
        while (_uploads.TryDequeue(out var upload))
        {
            // Even a recording failure may leave commands referring to storage.
            Releases.EnqueueRetirement(upload.Release, true);
            try
            {
                upload.Record(cmd);
            }
            catch (Exception error)
            {
                _lifetime.LatchSubmissionFault($"Resource upload recording failed: {error.Message}", false);
                FailResourceTransfers(error);
                throw;
            }
        }
    }

    internal unsafe void RecordResourceReadbacks(CommandBuffer cmd)
    {
        foreach (var pending in _readbacks)
        {
            if (pending.Recorded) continue;
            if (pending.Completion.Task.IsCanceled)
            {
                pending.Recorded = true;
                pending.Serial = 0;
                continue;
            }

            try
            {
                pending.Storage = Buffers.CreateBuffer((ulong)pending.Size, BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost,
                    AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit, "Graphics readback");
                pending.Record(cmd, pending.Storage);
                var barrier = new BufferMemoryBarrier2
                {
                    SType = StructureType.BufferMemoryBarrier2,
                    SrcStageMask = PipelineStageFlags2.TransferBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstStageMask = PipelineStageFlags2.HostBit, DstAccessMask = AccessFlags2.HostReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = Buffers.GetBuffer(pending.Storage), Size = (ulong)pending.Size
                };
                var dependency = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier
                };
                Context.Api.CmdPipelineBarrier2(cmd, &dependency);
                pending.Recorded = true;
            }
            catch (Exception error)
            {
                pending.Completion.TrySetException(error);
                pending.Recorded = true;
            }
        }
    }

    internal void MarkResourceTransfersSubmitted(ulong serial)
    {
        foreach (var pending in _readbacks)
            if (pending.Recorded && pending.Serial == ulong.MaxValue)
                pending.Serial = serial;
    }

    internal unsafe void CompleteResourceTransfers(ulong serial)
    {
        for (int i = _readbacks.Count - 1; i >= 0; i--)
        {
            var pending = _readbacks[i];
            if (!pending.Recorded || pending.Serial > serial) continue;
            try
            {
                if (!pending.Completion.Task.IsCompleted)
                {
                    Buffers.InvalidateBuffer(pending.Storage, 0, (ulong)pending.Size);
                    pending.Completion.TrySetResult(
                        new ReadOnlySpan<byte>(Buffers.GetMappedPointer(pending.Storage), pending.Size).ToArray());
                }
            }
            catch (Exception error)
            {
                pending.Completion.TrySetException(error);
            }
            finally
            {
                if (pending.Storage.IsValid)
                {
                    Buffers.DestroyBuffer(pending.Storage);
                    pending.Storage = BufferHandle.Invalid;
                }

                pending.Release();
                pending.Cancellation.Dispose();
                _readbacks.RemoveAt(i);
            }
        }
    }

    internal void FailResourceTransfers(Exception error)
    {
        foreach (var pending in _readbacks) pending.Completion.TrySetException(error);
    }

    internal void ShutdownResourceTransfers()
    {
        FailResourceTransfers(new ObjectDisposedException(nameof(GraphicsDevice)));
        while (_uploads.TryPeek(out var upload))
        {
            upload.Release();
            _uploads.Dequeue();
        }

        foreach (var pending in _readbacks)
        {
            pending.Recorded = true;
            pending.Serial = 0;
        }

        CompleteResourceTransfers(ulong.MaxValue);
    }
}