using System.Buffers.Binary;
using System.IO.Compression;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.Debug
{
    /// <summary>
    /// Byte ordering of the only swapchain formats accepted by the renderer PNG
    /// capture path. Both UNORM and SRGB variants retain the same bytes; PNG
    /// writing never applies a second gamma transform.
    /// </summary>
    public enum ScreenshotPixelFormat
    {
        Bgra8,
        Rgba8
    }

    /// <summary>
    /// Separates presentation-surface transfer support from the deliberately
    /// narrow, deterministic PNG source format contract.
    /// </summary>
    public readonly record struct ScreenshotReadbackFormatSupport(
        bool Supported,
        ScreenshotPixelFormat PixelFormat,
        string Reason)
    {
        public static ScreenshotReadbackFormatSupport Evaluate(
            ScreenshotColorSpace requestedColorSpace,
            Format swapchainFormat,
            bool transferSourceSupported,
            string? transferSourceReason)
        {
            if (requestedColorSpace != ScreenshotColorSpace.FinalLdrSrgb)
            {
                return new ScreenshotReadbackFormatSupport(
                    false,
                    default,
                    $"Screenshot color space '{requestedColorSpace}' is not available from the final LDR swapchain image. Request '{ScreenshotColorSpace.FinalLdrSrgb}' instead.");
            }

            if (!transferSourceSupported)
            {
                string reason = string.IsNullOrWhiteSpace(transferSourceReason)
                    ? "The presentation surface does not support TransferSrc usage for swapchain images."
                    : transferSourceReason;
                return new ScreenshotReadbackFormatSupport(false, default, reason);
            }

            return swapchainFormat switch
            {
                Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => new ScreenshotReadbackFormatSupport(
                    true,
                    ScreenshotPixelFormat.Bgra8,
                    $"Swapchain format '{swapchainFormat}' is supported as BGRA8."),
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => new ScreenshotReadbackFormatSupport(
                    true,
                    ScreenshotPixelFormat.Rgba8,
                    $"Swapchain format '{swapchainFormat}' is supported as RGBA8."),
                _ => new ScreenshotReadbackFormatSupport(
                    false,
                    default,
                    $"Swapchain format '{swapchainFormat}' cannot be captured losslessly by the renderer PNG path. Supported formats are B8G8R8A8_UNORM/SRGB and R8G8B8A8_UNORM/SRGB.")
            };
        }
    }

    /// <summary>
    /// Minimal allocation-conscious PNG writer for final LDR renderer captures.
    /// It writes scanline-filter type 0 RGBA data and includes an sRGB chunk so
    /// the captured final display image has an explicit color interpretation.
    /// </summary>
    public static class PngScreenshotEncoder
    {
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
        private static readonly uint[] CrcTable = CreateCrcTable();

        /// <summary>
        /// Encodes tightly packed four-byte pixels into a complete PNG. This is
        /// public so deterministic capture tests can validate the exact bytes
        /// without needing a Vulkan device.
        /// </summary>
        public static byte[] Encode(
            ReadOnlySpan<byte> sourcePixels,
            int width,
            int height,
            ScreenshotPixelFormat sourceFormat)
        {
            using var output = new MemoryStream();
            WritePng(output, sourcePixels, width, height, sourceFormat);
            return output.ToArray();
        }

        /// <summary>
        /// Writes a fully encoded, flushed temporary PNG and atomically replaces
        /// the target only after the byte stream is durable. Existing capture
        /// artifacts are therefore never observed as partially written files.
        /// </summary>
        public static void WriteAtomic(
            string outputPath,
            ReadOnlySpan<byte> sourcePixels,
            int width,
            int height,
            ScreenshotPixelFormat sourceFormat)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("A screenshot output path is required.", nameof(outputPath));

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(outputPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new IOException($"Screenshot output path '{outputPath}' is invalid.", exception);
            }

            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new IOException($"Could not resolve a directory for screenshot output '{fullPath}'.");

            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.WriteThrough))
                {
                    WritePng(output, sourcePixels, width, height, sourceFormat);
                    output.Flush(flushToDisk: true);
                }

                ReplaceAtomically(tempPath, fullPath);
                VerifyCompletedPng(fullPath);
            }
            finally
            {
                // A successful replace moves the temporary path; a failed
                // encode/replacement must not leave a plausible artifact behind.
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static void WritePng(
            Stream output,
            ReadOnlySpan<byte> sourcePixels,
            int width,
            int height,
            ScreenshotPixelFormat sourceFormat)
        {
            ValidatePixelPayload(sourcePixels, width, height);

            output.Write(Signature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 6; // RGBA
            ihdr[10] = 0; // compression method
            ihdr[11] = 0; // filter method
            ihdr[12] = 0; // no interlace
            WriteChunk(output, "IHDR", ihdr);

            // Rendering intent: perceptual. The source is the final LDR output
            // and is already encoded for the display swapchain.
            Span<byte> renderingIntent = stackalloc byte[1];
            renderingIntent[0] = 0;
            WriteChunk(output, "sRGB", renderingIntent);

            int sourceRowBytes = checked(width * 4);
            byte[] row = new byte[checked(sourceRowBytes + 1)];
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    row[0] = 0; // PNG filter type: None.
                    ReadOnlySpan<byte> sourceRow = sourcePixels.Slice(y * sourceRowBytes, sourceRowBytes);
                    Span<byte> destinationRow = row.AsSpan(1);
                    if (sourceFormat == ScreenshotPixelFormat.Rgba8)
                    {
                        sourceRow.CopyTo(destinationRow);
                    }
                    else
                    {
                        for (int x = 0; x < sourceRowBytes; x += 4)
                        {
                            destinationRow[x] = sourceRow[x + 2];
                            destinationRow[x + 1] = sourceRow[x + 1];
                            destinationRow[x + 2] = sourceRow[x];
                            destinationRow[x + 3] = sourceRow[x + 3];
                        }
                    }

                    zlib.Write(row);
                }
            }

            if (!compressed.TryGetBuffer(out ArraySegment<byte> compressedBytes))
                throw new IOException("Could not access the renderer screenshot PNG compression buffer.");
            WriteChunk(output, "IDAT", compressedBytes.AsSpan(0, checked((int)compressed.Length)));
            WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        }

        private static void ValidatePixelPayload(ReadOnlySpan<byte> sourcePixels, int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Screenshot dimensions must both be positive.");

            int requiredBytes;
            try
            {
                requiredBytes = checked(width * height * 4);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("Screenshot dimensions exceed the supported PNG payload size.", exception);
            }

            if (sourcePixels.Length != requiredBytes)
            {
                throw new ArgumentException(
                    $"Screenshot pixel payload is {sourcePixels.Length} bytes, but {requiredBytes} bytes are required for {width}x{height} RGBA8 data.",
                    nameof(sourcePixels));
            }
        }

        private static void ReplaceAtomically(string temporaryPath, string destinationPath)
        {
            // File.Replace uses an atomic same-volume replacement when an old
            // artifact exists. File.Move covers the first capture and remains
            // same-directory/same-volume by construction.
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }

        private static void VerifyCompletedPng(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < Signature.Length)
                throw new IOException($"Renderer screenshot '{path}' was not durably replaced with a complete PNG.");

            Span<byte> signature = stackalloc byte[8];
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read = input.Read(signature);
            if (read != Signature.Length || !signature.SequenceEqual(Signature))
                throw new IOException($"Renderer screenshot '{path}' was replaced, but does not have a valid PNG signature.");
        }

        private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
        {
            if (type == null || type.Length != 4)
                throw new ArgumentException("PNG chunk types must contain exactly four ASCII characters.", nameof(type));

            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);

            Span<byte> typeBytes = stackalloc byte[4];
            typeBytes[0] = checked((byte)type[0]);
            typeBytes[1] = checked((byte)type[1]);
            typeBytes[2] = checked((byte)type[2]);
            typeBytes[3] = checked((byte)type[3]);
            stream.Write(typeBytes);
            stream.Write(data);

            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ComputeCrc(typeBytes, data));
            stream.Write(crcBytes);
        }

        private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint crc = 0xffffffffu;
            foreach (byte value in type)
                crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            foreach (byte value in data)
                crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            return crc ^ 0xffffffffu;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint value = 0; value < table.Length; value++)
            {
                uint crc = value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
                table[value] = crc;
            }

            return table;
        }
    }

    internal readonly record struct ScreenshotReadbackCapturePlan(
        int FrameIndex,
        BufferHandle Buffer,
        int Width,
        int Height,
        ulong ByteCount,
        ScreenshotPixelFormat PixelFormat);

    /// <summary>
    /// Owns one host-visible image-to-buffer destination per frame slot. A
    /// capture only completes when that exact slot's fence has completed; no
    /// polling or reuse of a newer frame's fence is permitted.
    /// </summary>
    internal sealed unsafe class ScreenshotReadbackManager : IDisposable
    {
        private sealed class PendingCapture
        {
            public required ScreenshotRequest Request { get; init; }
            public required BufferHandle Buffer { get; init; }
            public required int Width { get; init; }
            public required int Height { get; init; }
            public required ulong ByteCount { get; init; }
            public required ScreenshotPixelFormat PixelFormat { get; init; }
            public bool Submitted { get; set; }
        }

        private readonly BufferManager _bufferManager;
        private readonly ScreenshotCaptureService _captureService;
        private readonly BufferHandle[] _buffers = new BufferHandle[FramesInFlight];
        private readonly ulong[] _bufferCapacities = new ulong[FramesInFlight];
        private readonly PendingCapture?[] _pending = new PendingCapture?[FramesInFlight];
        private bool _disposed;

        public ScreenshotReadbackManager(BufferManager bufferManager, ScreenshotCaptureService captureService)
        {
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        }

        public ulong AllocatedBytes => _bufferCapacities.Aggregate(0UL, static (sum, size) => checked(sum + size));

        /// <summary>
        /// Dequeues at most one request for a terminal rendered frame. A request
        /// that cannot be captured is consumed and marked failed immediately so
        /// callers never wait indefinitely for an unsupported swapchain.
        /// </summary>
        public bool TryPrepareCapture(
            int frameIndex,
            Extent2D extent,
            Format swapchainFormat,
            bool transferSourceSupported,
            string? transferSourceReason,
            out ScreenshotReadbackCapturePlan plan)
        {
            ThrowIfDisposed();
            ValidateFrameIndex(frameIndex);
            plan = default;

            if (!_captureService.TryDequeue(out ScreenshotRequest request))
                return false;

            if (_pending[frameIndex] != null)
            {
                _captureService.MarkFailed(
                    request.OutputPath,
                    $"Renderer screenshot frame slot {frameIndex} was reused before its earlier readback completed.");
                return false;
            }

            ScreenshotReadbackFormatSupport support = ScreenshotReadbackFormatSupport.Evaluate(
                request.ColorSpace,
                swapchainFormat,
                transferSourceSupported,
                transferSourceReason);
            if (!support.Supported)
            {
                _captureService.MarkFailed(request.OutputPath, support.Reason);
                return false;
            }

            if (!TryGetByteCount(extent, out int width, out int height, out ulong byteCount, out string sizeFailure))
            {
                _captureService.MarkFailed(request.OutputPath, sizeFailure);
                return false;
            }

            try
            {
                EnsureReadbackBuffer(frameIndex, byteCount);
                BufferHandle buffer = _buffers[frameIndex];
                _pending[frameIndex] = new PendingCapture
                {
                    Request = request,
                    Buffer = buffer,
                    Width = width,
                    Height = height,
                    ByteCount = byteCount,
                    PixelFormat = support.PixelFormat,
                    Submitted = false
                };
                plan = new ScreenshotReadbackCapturePlan(
                    frameIndex,
                    buffer,
                    width,
                    height,
                    byteCount,
                    support.PixelFormat);
                return true;
            }
            catch (Exception exception)
            {
                _captureService.MarkFailed(
                    request.OutputPath,
                    $"Could not allocate host-visible renderer screenshot readback storage: {DescribeException(exception)}");
                return false;
            }
        }

        public void RecordCopy(CommandBuffer commandBuffer, Image sourceImage, ScreenshotReadbackCapturePlan plan, Vk vk)
        {
            ThrowIfDisposed();
            ValidateFrameIndex(plan.FrameIndex);
            PendingCapture? pending = _pending[plan.FrameIndex];
            if (pending == null || pending.Buffer != plan.Buffer || pending.Submitted)
                throw new InvalidOperationException("Renderer screenshot readback copy was recorded without an active frame-slot capture.");

            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                ImageExtent = new Extent3D
                {
                    Width = checked((uint)plan.Width),
                    Height = checked((uint)plan.Height),
                    Depth = 1
                }
            };

            VkBuffer destination = _bufferManager.GetBuffer(plan.Buffer);
            vk.CmdCopyImageToBuffer(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destination,
                1,
                &region);

            // A fence orders completion, while this barrier makes the transfer
            // writes available to the mapped host allocation before the exact
            // frame fence is observed by the CPU.
            var bufferBarrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.HostBit,
                DstAccessMask = AccessFlags2.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = destination,
                Offset = 0,
                Size = plan.ByteCount
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &bufferBarrier
            };
            vk.CmdPipelineBarrier2(commandBuffer, &dependency);
        }

        public void MarkFrameSubmitted(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            if (_pending[frameIndex] is { } pending)
                pending.Submitted = true;
        }

        /// <summary>
        /// Called only after the matching frame slot fence has completed. The
        /// buffer remains mapped for its lifetime, is invalidated here, and is
        /// copied into a durable PNG before the request is marked completed.
        /// </summary>
        public void CompleteFrameAfterFence(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            PendingCapture? pending = _pending[frameIndex];
            if (pending == null)
                return;

            if (!pending.Submitted)
            {
                FailFrame(frameIndex, "Renderer screenshot command recording did not reach a successful terminal queue submission.");
                return;
            }

            try
            {
                if (!pending.Buffer.IsValid || pending.ByteCount > int.MaxValue)
                    throw new InvalidOperationException("Renderer screenshot readback storage is invalid or exceeds the supported mapped payload size.");

                _bufferManager.InvalidateBuffer(pending.Buffer, 0, pending.ByteCount);
                void* mappedPixels = _bufferManager.GetMappedPointer(pending.Buffer);
                if (mappedPixels == null)
                    throw new InvalidOperationException("Renderer screenshot readback allocation is not host mapped.");

                ReadOnlySpan<byte> pixels = new(mappedPixels, checked((int)pending.ByteCount));
                PngScreenshotEncoder.WriteAtomic(
                    pending.Request.OutputPath,
                    pixels,
                    pending.Width,
                    pending.Height,
                    pending.PixelFormat);
                _captureService.MarkCompleted(pending.Request.OutputPath);
            }
            catch (Exception exception)
            {
                _captureService.MarkFailed(
                    pending.Request.OutputPath,
                    $"Renderer screenshot readback completed on the GPU, but PNG encoding failed: {DescribeException(exception)}");
            }
            finally
            {
                _pending[frameIndex] = null;
            }
        }

        public void CompleteAllAfterDeviceIdle()
        {
            for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
                CompleteFrameAfterFence(frameIndex);
        }

        public void FailFrame(int frameIndex, string reason)
        {
            ValidateFrameIndex(frameIndex);
            PendingCapture? pending = _pending[frameIndex];
            if (pending == null)
                return;

            _captureService.MarkFailed(
                pending.Request.OutputPath,
                string.IsNullOrWhiteSpace(reason)
                    ? "Renderer screenshot capture failed before the frame fence completed."
                    : reason);
            _pending[frameIndex] = null;
        }

        public void FailAll(string reason, bool includeQueuedRequests)
        {
            for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
                FailFrame(frameIndex, reason);

            if (includeQueuedRequests)
                _captureService.FailPendingRequests(reason);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            FailAll("Renderer screenshot capture was cancelled while readback resources were being disposed.", includeQueuedRequests: true);
            for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
            {
                if (_buffers[frameIndex].IsValid)
                    _bufferManager.DestroyBuffer(_buffers[frameIndex]);
                _buffers[frameIndex] = BufferHandle.Invalid;
                _bufferCapacities[frameIndex] = 0;
            }
        }

        private void EnsureReadbackBuffer(int frameIndex, ulong byteCount)
        {
            if (_buffers[frameIndex].IsValid && _bufferCapacities[frameIndex] >= byteCount)
                return;

            if (_buffers[frameIndex].IsValid)
            {
                _bufferManager.DestroyBuffer(_buffers[frameIndex]);
                _buffers[frameIndex] = BufferHandle.Invalid;
                _bufferCapacities[frameIndex] = 0;
            }

            _buffers[frameIndex] = _bufferManager.CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Renderer Screenshot Readback Frame {frameIndex}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
            _bufferCapacities[frameIndex] = byteCount;
        }

        private static bool TryGetByteCount(
            Extent2D extent,
            out int width,
            out int height,
            out ulong byteCount,
            out string failureReason)
        {
            width = 0;
            height = 0;
            byteCount = 0;
            failureReason = string.Empty;
            if (extent.Width == 0 || extent.Height == 0)
            {
                failureReason = "The swapchain extent is empty, so no final LDR screenshot can be captured.";
                return false;
            }

            if (extent.Width > int.MaxValue || extent.Height > int.MaxValue)
            {
                failureReason = $"Swapchain extent {extent.Width}x{extent.Height} exceeds the PNG encoder's supported dimensions.";
                return false;
            }

            try
            {
                width = checked((int)extent.Width);
                height = checked((int)extent.Height);
                byteCount = checked((ulong)width * (ulong)height * 4UL);
                if (byteCount > int.MaxValue)
                {
                    failureReason = $"Swapchain extent {width}x{height} requires {byteCount} bytes, exceeding the renderer PNG encoder's maximum mapped payload.";
                    return false;
                }

                return true;
            }
            catch (OverflowException)
            {
                failureReason = $"Swapchain extent {extent.Width}x{extent.Height} overflows renderer screenshot readback storage.";
                return false;
            }
        }

        private static string DescribeException(Exception exception)
        {
            string message = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            return $"{exception.GetType().Name}: {message}";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ScreenshotReadbackManager));
        }
    }
}
