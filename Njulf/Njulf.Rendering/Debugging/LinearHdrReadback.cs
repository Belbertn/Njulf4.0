using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.Debug;

/// <summary>
/// Lifecycle of one renderer-owned linear HDR capture request.
/// </summary>
public enum LinearHdrCaptureState : byte
{
    Unknown = 0,
    Queued = 1,
    Submitted = 2,
    Completed = 3,
    Failed = 4
}

/// <summary>
/// Immutable request status. A completed result refers to an atomically
/// replaced, validated PFM file; a failed result is terminal and contains a
/// diagnostic reason.
/// </summary>
public sealed record LinearHdrCaptureResult(
    string OutputPath,
    LinearHdrCaptureState State,
    string Error)
{
    public bool IsTerminal => State is LinearHdrCaptureState.Completed or LinearHdrCaptureState.Failed;
}

internal sealed record LinearHdrCaptureRequest(string OutputPath);

/// <summary>
/// Thread-safe request/status registry used by the public renderer API and the
/// frame-slot readback manager.
/// </summary>
internal sealed class LinearHdrCaptureService
{
    private readonly Queue<LinearHdrCaptureRequest> _requests = new();
    private readonly Dictionary<string, LinearHdrCaptureResult> _results =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _requests.Count;
        }
    }

    public void Request(string outputPath)
    {
        string fullPath = NormalizeOutputPath(outputPath);
        lock (_gate)
        {
            if (_results.TryGetValue(fullPath, out LinearHdrCaptureResult? existing) &&
                !existing.IsTerminal)
            {
                throw new InvalidOperationException(
                    $"Linear HDR capture '{fullPath}' is already queued or submitted.");
            }

            _requests.Enqueue(new LinearHdrCaptureRequest(fullPath));
            _results[fullPath] = new LinearHdrCaptureResult(
                fullPath,
                LinearHdrCaptureState.Queued,
                string.Empty);
        }
    }

    public LinearHdrCaptureResult GetResult(string outputPath)
    {
        string fullPath = NormalizeOutputPath(outputPath);
        lock (_gate)
        {
            return _results.TryGetValue(fullPath, out LinearHdrCaptureResult? result)
                ? result
                : new LinearHdrCaptureResult(fullPath, LinearHdrCaptureState.Unknown, string.Empty);
        }
    }

    public bool TryDequeue(out LinearHdrCaptureRequest request)
    {
        lock (_gate)
        {
            if (_requests.Count == 0)
            {
                request = new LinearHdrCaptureRequest(string.Empty);
                return false;
            }

            request = _requests.Dequeue();
            return true;
        }
    }

    public void MarkSubmitted(string outputPath)
    {
        Update(outputPath, LinearHdrCaptureState.Submitted, string.Empty);
    }

    public void MarkCompleted(string outputPath)
    {
        Update(outputPath, LinearHdrCaptureState.Completed, string.Empty);
    }

    public void MarkFailed(string outputPath, string error)
    {
        Update(
            outputPath,
            LinearHdrCaptureState.Failed,
            string.IsNullOrWhiteSpace(error)
                ? "The linear HDR capture failed without a diagnostic reason."
                : error.Trim());
    }

    public void FailPendingRequests(string error)
    {
        lock (_gate)
        {
            while (_requests.Count > 0)
            {
                LinearHdrCaptureRequest request = _requests.Dequeue();
                _results[request.OutputPath] = new LinearHdrCaptureResult(
                    request.OutputPath,
                    LinearHdrCaptureState.Failed,
                    error);
            }
        }
    }

    private void Update(string outputPath, LinearHdrCaptureState state, string error)
    {
        string fullPath = NormalizeOutputPath(outputPath);
        lock (_gate)
            _results[fullPath] = new LinearHdrCaptureResult(fullPath, state, error);
    }

    private static string NormalizeOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("A linear HDR capture output path is required.", nameof(outputPath));

        string fullPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".pfm", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Linear HDR captures use the lossless .pfm container and require a .pfm output path.",
                nameof(outputPath));
        }

        return fullPath;
    }
}

/// <summary>
/// Narrow format/usage decision for lossless SceneColor readback. The capture
/// path intentionally accepts only the renderer's native RGBA16F target.
/// </summary>
public readonly record struct LinearHdrReadbackFormatSupport(bool Supported, string Reason)
{
    public const Format RequiredFormat = Format.R16G16B16A16Sfloat;
    public const ulong BytesPerPixel = 8;

    public static LinearHdrReadbackFormatSupport Evaluate(
        Format sourceFormat,
        ImageUsageFlags sourceUsage)
    {
        if (sourceFormat != RequiredFormat)
        {
            return new LinearHdrReadbackFormatSupport(
                false,
                $"Linear HDR readback requires {RequiredFormat}, but the source is {sourceFormat}.");
        }

        if ((sourceUsage & ImageUsageFlags.TransferSrcBit) == 0)
        {
            return new LinearHdrReadbackFormatSupport(
                false,
                "The linear HDR source image was not created with TransferSrc usage.");
        }

        return new LinearHdrReadbackFormatSupport(
            true,
            $"Source format {sourceFormat} is captured as little-endian RGBA16F and stored as RGB32F PFM.");
    }
}

/// <summary>
/// Decoded logical top-left-origin RGB image.
/// </summary>
public sealed record LinearFloatImage(int Width, int Height, float[] Pixels);

/// <summary>
/// Deterministic Portable Float Map codec for linear-scRGB evidence. Files use
/// PFM's standard RGB32F payload, little-endian scale marker, and bottom-up
/// serialized rows. A versioned comment fixes the Njulf color/origin contract.
/// RGBA16F source alpha is deliberately omitted because SceneColor alpha is not
/// a material/GI evidence signal.
/// </summary>
public static class PfmLinearImageCodec
{
    public const int CurrentVersion = 1;
    public const string MediaType = "image/x-portable-floatmap";
    public const string ColorSpace = "linear-scRGB";
    public const int MaximumEncodedBytes = 128 * 1024 * 1024;
    public const int MaximumPixelCount =
        (MaximumEncodedBytes - 256) / (3 * sizeof(float));
    private const string Magic = "PF";
    private const string ContractComment =
        "# NJULF_LINEAR_FLOAT_IMAGE_VERSION=1 COLOR_SPACE=linear-scRGB LOGICAL_ORIGIN=top-left";

    public static byte[] Encode(ReadOnlySpan<float> topDownRgb, int width, int height)
    {
        ValidatePixels(topDownRgb, width, height);
        using var output = new MemoryStream(
            GetEncodedByteLength(width, height));
        Write(output, topDownRgb, width, height);
        byte[] encoded = output.GetBuffer();
        if (output.Length != encoded.Length)
        {
            throw new IOException(
                "PFM encoding did not fill its exact admitted output buffer.");
        }
        return encoded;
    }

    public static LinearFloatImage Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty || encoded.Length > MaximumEncodedBytes)
        {
            throw new InvalidDataException(
                $"PFM input must contain between 1 and {MaximumEncodedBytes} bytes.");
        }

        int offset = 0;
        string magic = ReadAsciiLine(encoded, ref offset);
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            throw new InvalidDataException("Linear image does not contain the PFM RGB magic.");

        string contract = ReadAsciiLine(encoded, ref offset);
        if (!string.Equals(contract, ContractComment, StringComparison.Ordinal))
            throw new InvalidDataException("Linear image does not contain the supported Njulf PFM v1 contract header.");

        string dimensions = ReadAsciiLine(encoded, ref offset);
        string[] dimensionParts = dimensions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (dimensionParts.Length != 2 ||
            !int.TryParse(dimensionParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(dimensionParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) ||
            width <= 0 ||
            height <= 0)
        {
            throw new InvalidDataException($"PFM dimensions '{dimensions}' are invalid.");
        }

        string scale = ReadAsciiLine(encoded, ref offset);
        if (!string.Equals(scale, "-1.0", StringComparison.Ordinal))
            throw new InvalidDataException("Njulf PFM v1 requires the little-endian -1.0 scale marker.");

        int floatCount;
        int payloadBytes;
        try
        {
            int pixelCount = GetBoundedPixelCount(width, height);
            floatCount = checked(pixelCount * 3);
            payloadBytes = checked(floatCount * sizeof(float));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("PFM dimensions overflow the supported payload size.", exception);
        }

        if (encoded.Length - offset != payloadBytes)
        {
            throw new InvalidDataException(
                $"PFM payload contains {encoded.Length - offset} bytes; {payloadBytes} are required.");
        }

        var pixels = new float[floatCount];
        int rowFloatCount = checked(width * 3);
        for (int serializedRow = 0; serializedRow < height; serializedRow++)
        {
            int logicalRow = height - 1 - serializedRow;
            int destinationBase = checked(logicalRow * rowFloatCount);
            for (int component = 0; component < rowFloatCount; component++)
            {
                int bits = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(offset, sizeof(float)));
                float value = BitConverter.Int32BitsToSingle(bits);
                if (!float.IsFinite(value))
                    throw new InvalidDataException("PFM payload contains a non-finite component.");
                pixels[destinationBase + component] = value;
                offset += sizeof(float);
            }
        }

        return new LinearFloatImage(width, height, pixels);
    }

    public static void WriteAtomic(
        string outputPath,
        ReadOnlySpan<float> topDownRgb,
        int width,
        int height)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("A PFM output path is required.", nameof(outputPath));

        ValidatePixels(topDownRgb, width, height);
        int expectedByteLength = GetEncodedByteLength(width, height);
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException($"Could not resolve a directory for PFM output '{fullPath}'.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.WriteThrough))
            {
                Write(output, topDownRgb, width, height);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, fullPath);

            VerifyPublished(
                fullPath,
                topDownRgb,
                width,
                height,
                expectedByteLength);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static string ComputeSha256(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        using var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        long admittedLength = input.Length;
        if (admittedLength <= 0 || admittedLength > MaximumEncodedBytes)
        {
            throw new InvalidDataException(
                $"PFM '{fullPath}' has an invalid bounded length.");
        }
        byte[] hash = SHA256.HashData(input);
        if (input.ReadByte() != -1 || input.Length != admittedLength)
        {
            throw new IOException(
                $"PFM '{fullPath}' changed length while it was being hashed.");
        }
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Write(
        Stream output,
        ReadOnlySpan<float> topDownRgb,
        int width,
        int height)
    {
        ValidatePixels(topDownRgb, width, height);
        string header = CreateHeader(width, height);
        int encodedByteLength = checked(
            Encoding.ASCII.GetByteCount(header) +
            GetBoundedPixelCount(width, height) * 3 * sizeof(float));
        if (encodedByteLength > MaximumEncodedBytes)
        {
            throw new InvalidDataException(
                $"PFM output exceeds the {MaximumEncodedBytes}-byte publication bound.");
        }
        output.Write(Encoding.ASCII.GetBytes(header));

        int rowFloatCount = checked(width * 3);
        const int serializationBufferBytes = 64 * 1024;
        byte[] serializationBuffer =
            ArrayPool<byte>.Shared.Rent(serializationBufferBytes);
        try
        {
            int componentCapacity =
                serializationBufferBytes / sizeof(float);
            for (int logicalRow = height - 1; logicalRow >= 0; logicalRow--)
            {
                ReadOnlySpan<float> row = topDownRgb.Slice(
                    logicalRow * rowFloatCount,
                    rowFloatCount);
                for (int componentBase = 0;
                     componentBase < row.Length;
                     componentBase += componentCapacity)
                {
                    int componentCount = Math.Min(
                        componentCapacity,
                        row.Length - componentBase);
                    Span<byte> encoded = serializationBuffer.AsSpan(
                        0,
                        componentCount * sizeof(float));
                    for (int component = 0;
                         component < componentCount;
                         component++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(
                            encoded.Slice(
                                component * sizeof(float),
                                sizeof(float)),
                            BitConverter.SingleToInt32Bits(
                                row[componentBase + component]));
                    }
                    output.Write(encoded);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(serializationBuffer);
        }
    }

    private static void VerifyPublished(
        string path,
        ReadOnlySpan<float> topDownRgb,
        int width,
        int height,
        int expectedByteLength)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        long admittedLength = input.Length;
        if (admittedLength != expectedByteLength ||
            admittedLength <= 0 ||
            admittedLength > MaximumEncodedBytes)
        {
            throw new IOException(
                $"Published PFM '{path}' has an unexpected bounded length.");
        }

        byte[] expectedHeader = Encoding.ASCII.GetBytes(
            CreateHeader(width, height));
        Span<byte> actualHeader = stackalloc byte[expectedHeader.Length];
        input.ReadExactly(actualHeader);
        if (!actualHeader.SequenceEqual(expectedHeader))
        {
            throw new IOException(
                $"Published PFM '{path}' failed its header verification.");
        }

        int rowFloatCount = checked(width * 3);
        const int verificationBufferBytes = 64 * 1024;
        byte[] verificationBuffer =
            ArrayPool<byte>.Shared.Rent(verificationBufferBytes);
        try
        {
            int componentCapacity =
                verificationBufferBytes / sizeof(float);
            for (int logicalRow = height - 1; logicalRow >= 0; logicalRow--)
            {
                ReadOnlySpan<float> row = topDownRgb.Slice(
                    logicalRow * rowFloatCount,
                    rowFloatCount);
                for (int componentBase = 0;
                     componentBase < row.Length;
                     componentBase += componentCapacity)
                {
                    int componentCount = Math.Min(
                        componentCapacity,
                        row.Length - componentBase);
                    Span<byte> encoded = verificationBuffer.AsSpan(
                        0,
                        componentCount * sizeof(float));
                    input.ReadExactly(encoded);
                    for (int component = 0;
                         component < componentCount;
                         component++)
                    {
                        int actualBits =
                            BinaryPrimitives.ReadInt32LittleEndian(
                                encoded.Slice(
                                    component * sizeof(float),
                                    sizeof(float)));
                        if (actualBits !=
                            BitConverter.SingleToInt32Bits(
                                row[componentBase + component]))
                        {
                            int linearComponent = checked(
                                logicalRow * rowFloatCount +
                                componentBase +
                                component);
                            throw new IOException(
                                $"Published PFM '{path}' failed lossless " +
                                $"verification at RGB component " +
                                $"{linearComponent}.");
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(verificationBuffer);
        }

        if (input.ReadByte() != -1 || input.Length != admittedLength)
        {
            throw new IOException(
                $"Published PFM '{path}' changed length during verification.");
        }
    }

    private static void ValidatePixels(ReadOnlySpan<float> pixels, int width, int height)
    {
        int required = checked(GetBoundedPixelCount(width, height) * 3);

        if (pixels.Length != required)
        {
            throw new ArgumentException(
                $"PFM RGB payload contains {pixels.Length} components; {required} are required.",
                nameof(pixels));
        }

        foreach (float value in pixels)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException(
                    "Linear HDR evidence contains a non-finite component and cannot be published.",
                    nameof(pixels));
            }
        }
    }

    internal static bool TryGetEncodedByteLength(
        int width,
        int height,
        out int encodedByteLength,
        out string failure)
    {
        try
        {
            encodedByteLength = GetEncodedByteLength(width, height);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or
                InvalidDataException or
                InvalidOperationException or
                OverflowException)
        {
            encodedByteLength = 0;
            failure = exception.Message;
            return false;
        }
    }

    private static int GetEncodedByteLength(int width, int height)
    {
        int pixelCount = GetBoundedPixelCount(width, height);
        int encodedByteLength;
        try
        {
            encodedByteLength = checked(
                Encoding.ASCII.GetByteCount(CreateHeader(width, height)) +
                pixelCount * 3 * sizeof(float));
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "PFM dimensions exceed the supported encoded size.",
                exception);
        }
        if (encodedByteLength > MaximumEncodedBytes)
        {
            throw new InvalidDataException(
                $"PFM output exceeds the {MaximumEncodedBytes}-byte publication bound.");
        }
        return encodedByteLength;
    }

    private static int GetBoundedPixelCount(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "PFM dimensions must both be positive.");
        }

        int pixelCount;
        try
        {
            pixelCount = checked(width * height);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "PFM dimensions exceed the supported payload size.",
                exception);
        }
        if (pixelCount > MaximumPixelCount)
        {
            throw new InvalidDataException(
                $"PFM dimensions contain {pixelCount} pixels, exceeding the " +
                $"{MaximumPixelCount}-pixel publication bound.");
        }
        return pixelCount;
    }

    private static string CreateHeader(int width, int height) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Magic}\n{ContractComment}\n{width} {height}\n-1.0\n");

    private static string ReadAsciiLine(ReadOnlySpan<byte> encoded, ref int offset)
    {
        if ((uint)offset >= (uint)encoded.Length)
            throw new InvalidDataException("PFM header ended unexpectedly.");

        int newline = encoded[offset..].IndexOf((byte)'\n');
        if (newline < 0)
            throw new InvalidDataException("PFM header line is not newline terminated.");

        ReadOnlySpan<byte> line = encoded.Slice(offset, newline);
        if (!line.IsEmpty && line[^1] == (byte)'\r')
            line = line[..^1];
        offset = checked(offset + newline + 1);
        return Encoding.ASCII.GetString(line);
    }
}

internal readonly record struct LinearHdrReadbackCapturePlan(
    int FrameIndex,
    BufferHandle Buffer,
    int Width,
    int Height,
    ulong ByteCount);

/// <summary>
/// Owns one mapped RGBA16F destination per in-flight frame. CPU access occurs
/// only after the exact frame-slot fence has completed.
/// </summary>
internal sealed unsafe class LinearHdrReadbackManager : IDisposable
{
    private sealed class PendingCapture
    {
        public required LinearHdrCaptureRequest Request { get; init; }
        public required BufferHandle Buffer { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required ulong ByteCount { get; init; }
        public bool Submitted { get; set; }
    }

    private readonly BufferManager _bufferManager;
    private readonly LinearHdrCaptureService _captureService;
    private readonly BufferHandle[] _buffers = new BufferHandle[FramesInFlight];
    private readonly ulong[] _bufferCapacities = new ulong[FramesInFlight];
    private readonly PendingCapture?[] _pending = new PendingCapture?[FramesInFlight];
    private bool _disposed;

    public LinearHdrReadbackManager(
        BufferManager bufferManager,
        LinearHdrCaptureService captureService)
    {
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
    }

    public bool TryPrepareCapture(
        int frameIndex,
        RenderTarget source,
        out LinearHdrReadbackCapturePlan plan)
    {
        ThrowIfDisposed();
        ValidateFrameIndex(frameIndex);
        ArgumentNullException.ThrowIfNull(source);
        plan = default;

        if (!_captureService.TryDequeue(out LinearHdrCaptureRequest request))
            return false;

        if (_pending[frameIndex] != null)
        {
            _captureService.MarkFailed(
                request.OutputPath,
                $"Linear HDR frame slot {frameIndex} was reused before its prior readback completed.");
            return false;
        }

        LinearHdrReadbackFormatSupport support =
            LinearHdrReadbackFormatSupport.Evaluate(source.Format, source.Usage);
        if (!support.Supported)
        {
            _captureService.MarkFailed(request.OutputPath, support.Reason);
            return false;
        }

        if (!TryGetByteCount(source.Extent, out int width, out int height, out ulong byteCount, out string failure))
        {
            _captureService.MarkFailed(request.OutputPath, failure);
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
                ByteCount = byteCount
            };
            plan = new LinearHdrReadbackCapturePlan(frameIndex, buffer, width, height, byteCount);
            return true;
        }
        catch (Exception exception)
        {
            _captureService.MarkFailed(
                request.OutputPath,
                $"Could not allocate linear HDR readback storage: {DescribeException(exception)}");
            return false;
        }
    }

    public void RecordCopy(
        CommandBuffer commandBuffer,
        Image sourceImage,
        LinearHdrReadbackCapturePlan plan,
        Vk vk)
    {
        ThrowIfDisposed();
        ValidateFrameIndex(plan.FrameIndex);
        PendingCapture? pending = _pending[plan.FrameIndex];
        if (pending == null || pending.Buffer != plan.Buffer || pending.Submitted)
            throw new InvalidOperationException("Linear HDR copy was recorded without an active frame-slot capture.");

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = 1
            },
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
        if (_pending[frameIndex] is not { } pending)
            return;

        pending.Submitted = true;
        _captureService.MarkSubmitted(pending.Request.OutputPath);
    }

    public void CompleteFrameAfterFence(int frameIndex)
    {
        ValidateFrameIndex(frameIndex);
        PendingCapture? pending = _pending[frameIndex];
        if (pending == null)
            return;

        if (!pending.Submitted)
        {
            FailFrame(frameIndex, "Linear HDR copy did not reach a successful terminal queue submission.");
            return;
        }

        try
        {
            _bufferManager.InvalidateBuffer(pending.Buffer, 0, pending.ByteCount);
            void* mapped = _bufferManager.GetMappedPointer(pending.Buffer);
            if (mapped == null)
                throw new InvalidOperationException("Linear HDR readback allocation is not host mapped.");

            ReadOnlySpan<byte> rgba16 = new(mapped, checked((int)pending.ByteCount));
            float[] rgb32 = DecodeRgba16Float(rgba16, pending.Width, pending.Height);
            PfmLinearImageCodec.WriteAtomic(
                pending.Request.OutputPath,
                rgb32,
                pending.Width,
                pending.Height);
            _captureService.MarkCompleted(pending.Request.OutputPath);
        }
        catch (Exception exception)
        {
            _captureService.MarkFailed(
                pending.Request.OutputPath,
                $"GPU readback completed, but linear HDR publication failed: {DescribeException(exception)}");
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

        _captureService.MarkFailed(pending.Request.OutputPath, reason);
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

        FailAll(
            "Linear HDR capture was cancelled while readback resources were being disposed.",
            includeQueuedRequests: true);
        for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
        {
            if (_buffers[frameIndex].IsValid)
                _bufferManager.DestroyBuffer(_buffers[frameIndex]);
            _buffers[frameIndex] = BufferHandle.Invalid;
            _bufferCapacities[frameIndex] = 0;
        }
    }

    internal static float[] DecodeRgba16Float(
        ReadOnlySpan<byte> rgba16,
        int width,
        int height)
    {
        int pixelCount = checked(width * height);
        int expectedBytes = checked(pixelCount * 8);
        if (rgba16.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"RGBA16F payload contains {rgba16.Length} bytes; {expectedBytes} are required.",
                nameof(rgba16));
        }

        var rgb = new float[checked(pixelCount * 3)];
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int source = pixel * 8;
            int destination = pixel * 3;
            for (int component = 0; component < 3; component++)
            {
                ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(
                    rgba16.Slice(source + component * sizeof(ushort), sizeof(ushort)));
                float value = (float)BitConverter.UInt16BitsToHalf(bits);
                if (!float.IsFinite(value))
                {
                    throw new InvalidDataException(
                        $"Linear HDR source contains a non-finite component at pixel {pixel}, channel {component}.");
                }

                rgb[destination + component] = value;
            }
        }

        return rgb;
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
            $"Linear HDR Readback Frame {frameIndex}",
            MemoryBudgetCategory.DiagnosticsAndDebug);
        _bufferCapacities[frameIndex] = byteCount;
    }

    private static bool TryGetByteCount(
        Extent2D extent,
        out int width,
        out int height,
        out ulong byteCount,
        out string failure)
    {
        width = 0;
        height = 0;
        byteCount = 0;
        failure = string.Empty;
        if (extent.Width == 0 || extent.Height == 0 ||
            extent.Width > int.MaxValue || extent.Height > int.MaxValue)
        {
            failure = $"Linear HDR source extent {extent.Width}x{extent.Height} is unsupported.";
            return false;
        }

        try
        {
            width = checked((int)extent.Width);
            height = checked((int)extent.Height);
            if (!PfmLinearImageCodec.TryGetEncodedByteLength(
                    width,
                    height,
                    out _,
                    out string publicationFailure))
            {
                failure =
                    $"Linear HDR source extent {extent.Width}x{extent.Height} " +
                    $"cannot be published: {publicationFailure}";
                return false;
            }
            byteCount = checked((ulong)width * (ulong)height * LinearHdrReadbackFormatSupport.BytesPerPixel);
            if (byteCount > int.MaxValue)
            {
                failure = $"Linear HDR source requires {byteCount} mapped bytes, exceeding the supported payload size.";
                return false;
            }

            return true;
        }
        catch (OverflowException)
        {
            failure = $"Linear HDR source extent {extent.Width}x{extent.Height} overflows readback storage.";
            return false;
        }
    }

    private static void ValidateFrameIndex(int frameIndex)
    {
        if ((uint)frameIndex >= FramesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
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
            throw new ObjectDisposedException(nameof(LinearHdrReadbackManager));
    }
}
