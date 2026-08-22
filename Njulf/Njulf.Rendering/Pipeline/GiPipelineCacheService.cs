using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

public readonly record struct GiPipelineCacheTelemetry(
    bool CacheLoaded,
    bool CacheRejected,
    bool CacheSaved,
    ulong LoadedPayloadBytes,
    ulong SavedPayloadBytes,
    ulong PipelineCreationCount,
    long PipelineCreationMicroseconds,
    ulong RenderCriticalPipelineCreationCount,
    string CachePath,
    string LoadStatus,
    string LastCreatedPipeline)
{
    public static GiPipelineCacheTelemetry Empty { get; } =
        new(false, false, false, 0, 0, 0, 0, 0,
            string.Empty, "Not initialized", string.Empty);
}

internal sealed record GiPipelineCacheIdentity(
    uint VendorId,
    uint DeviceId,
    uint DriverVersion,
    uint ApiVersion,
    byte[] PipelineCacheUuid,
    byte[] ShaderBundleHash,
    byte[] EngineAbiHash);

/// <summary>
/// Fixed, checksummed envelope around the opaque Vulkan cache blob. Vulkan's
/// own cache header remains authoritative, while this header rejects changes
/// in shaders and Njulf's GI ABI before data reaches the driver.
/// </summary>
internal static class GiPipelineCacheFileCodec
{
    private static ReadOnlySpan<byte> Magic => "NJGIPC01"u8;
    internal const uint FormatVersion = 1;
    internal const int HeaderSize = 152;
    internal const int MaximumPayloadBytes = 256 * 1024 * 1024;

    internal static byte[] Encode(
        GiPipelineCacheIdentity identity,
        ReadOnlySpan<byte> payload)
    {
        ValidateIdentity(identity);
        if (payload.Length > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));

        byte[] encoded = new byte[checked(HeaderSize + payload.Length)];
        Span<byte> header = encoded.AsSpan(0, HeaderSize);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], identity.VendorId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], identity.DeviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], identity.DriverVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], identity.ApiVersion);
        identity.PipelineCacheUuid.CopyTo(header[32..48]);
        identity.ShaderBundleHash.CopyTo(header[48..80]);
        identity.EngineAbiHash.CopyTo(header[80..112]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header[112..],
            checked((ulong)payload.Length));
        SHA256.HashData(payload, header[120..152]);
        payload.CopyTo(encoded.AsSpan(HeaderSize));
        return encoded;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> encoded,
        GiPipelineCacheIdentity expected,
        out byte[] payload,
        out string reason)
    {
        payload = Array.Empty<byte>();
        reason = string.Empty;
        try
        {
            ValidateIdentity(expected);
        }
        catch (ArgumentException ex)
        {
            reason = ex.Message;
            return false;
        }

        if (encoded.Length < HeaderSize)
            return Reject("Cache file is truncated.", out reason);
        if (!encoded[..8].SequenceEqual(Magic))
            return Reject("Cache magic is not recognized.", out reason);
        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[8..]) != FormatVersion)
            return Reject("Cache envelope version does not match.", out reason);
        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[12..]) != HeaderSize)
            return Reject("Cache header size does not match.", out reason);
        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[16..]) != expected.VendorId ||
            BinaryPrimitives.ReadUInt32LittleEndian(encoded[20..]) != expected.DeviceId ||
            BinaryPrimitives.ReadUInt32LittleEndian(encoded[24..]) != expected.DriverVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(encoded[28..]) != expected.ApiVersion)
        {
            return Reject("Physical-device or driver identity changed.", out reason);
        }
        if (!CryptographicOperations.FixedTimeEquals(
                encoded[32..48], expected.PipelineCacheUuid))
            return Reject("Vulkan pipelineCacheUUID changed.", out reason);
        if (!CryptographicOperations.FixedTimeEquals(
                encoded[48..80], expected.ShaderBundleHash))
            return Reject("GI shader bundle changed.", out reason);
        if (!CryptographicOperations.FixedTimeEquals(
                encoded[80..112], expected.EngineAbiHash))
            return Reject("GI engine ABI changed.", out reason);

        ulong declaredLength =
            BinaryPrimitives.ReadUInt64LittleEndian(encoded[112..]);
        if (declaredLength > MaximumPayloadBytes ||
            declaredLength != checked((ulong)(encoded.Length - HeaderSize)))
        {
            return Reject("Cache payload length is invalid.", out reason);
        }

        ReadOnlySpan<byte> sourcePayload = encoded[HeaderSize..];
        Span<byte> actualHash = stackalloc byte[32];
        SHA256.HashData(sourcePayload, actualHash);
        if (!CryptographicOperations.FixedTimeEquals(
                encoded[120..152], actualHash))
            return Reject("Cache payload checksum failed.", out reason);

        payload = sourcePayload.ToArray();
        reason = "Compatible cache loaded.";
        return true;
    }

    private static bool Reject(string message, out string reason)
    {
        reason = message;
        return false;
    }

    private static void ValidateIdentity(GiPipelineCacheIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.PipelineCacheUuid is not { Length: 16 })
            throw new ArgumentException("pipelineCacheUUID must be 16 bytes.", nameof(identity));
        if (identity.ShaderBundleHash is not { Length: 32 })
            throw new ArgumentException("Shader hash must be 32 bytes.", nameof(identity));
        if (identity.EngineAbiHash is not { Length: 32 })
            throw new ArgumentException("Engine ABI hash must be 32 bytes.", nameof(identity));
    }
}

/// <summary>
/// Renderer-owned Vulkan pipeline cache shared by mesh and admitted GI
/// pipelines. It owns the cache object; pipeline owners only borrow
/// <see cref="Cache"/>. The historical type name is retained for diagnostics
/// and capture-schema compatibility.
/// </summary>
public sealed unsafe class GiPipelineCacheService : IDisposable
{
    private const string EngineAbi =
        "Njulf.GI.PipelineCache/1;SimpleDdgiPush=136;BindlessABI=20260809";
    private readonly object _gate = new();
    private readonly VulkanContext _context;
    private readonly GiPipelineCacheIdentity _identity;
    private readonly string _cachePath;
    private PipelineCache _cache;
    private bool _renderCriticalFramesStarted;
    private bool _dirty;
    private bool _disposed;
    private bool _cacheLoaded;
    private bool _cacheRejected;
    private bool _cacheSaved;
    private ulong _loadedPayloadBytes;
    private ulong _savedPayloadBytes;
    private ulong _pipelineCreationCount;
    private long _pipelineCreationMicroseconds;
    private ulong _renderCriticalPipelineCreationCount;
    private string _loadStatus = "Empty cache created.";
    private string _lastCreatedPipeline = string.Empty;

    public GiPipelineCacheService(
        VulkanContext context,
        string shaderBundleHash,
        string? cacheDirectory = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _identity = CreateIdentity(context, shaderBundleHash);
        string? configuredCacheDirectory =
            string.IsNullOrWhiteSpace(cacheDirectory)
                ? Environment.GetEnvironmentVariable(
                    "NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY")
                : cacheDirectory;
        string root = string.IsNullOrWhiteSpace(configuredCacheDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Njulf",
                "PipelineCaches")
            : Path.GetFullPath(configuredCacheDirectory);
        _cachePath = Path.Combine(
            root,
            $"gi-{_identity.VendorId:x8}-{_identity.DeviceId:x8}.njvkcache");

        byte[] initialData = TryLoadCompatiblePayload();
        Result result = CreateCache(initialData, out _cache);
        if (result != Result.Success && initialData.Length > 0)
        {
            _cacheRejected = true;
            _cacheLoaded = false;
            _loadedPayloadBytes = 0;
            _loadStatus = $"Driver rejected cached data ({result}); using an empty cache.";
            result = CreateCache(Array.Empty<byte>(), out _cache);
        }
        if (result != Result.Success)
            throw new VulkanException("Failed to create shared GI pipeline cache", result);

        _context.SetDebugName(
            _cache.Handle,
            ObjectType.PipelineCache,
            "Persistent Shared Renderer Pipeline Cache");
    }

    public PipelineCache Cache
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _cache;
            }
        }
    }

    public GiPipelineCacheTelemetry Telemetry
    {
        get
        {
            lock (_gate)
            {
                return new GiPipelineCacheTelemetry(
                    _cacheLoaded,
                    _cacheRejected,
                    _cacheSaved,
                    _loadedPayloadBytes,
                    _savedPayloadBytes,
                    _pipelineCreationCount,
                    _pipelineCreationMicroseconds,
                    _renderCriticalPipelineCreationCount,
                    _cachePath,
                    _loadStatus,
                    _lastCreatedPipeline);
            }
        }
    }

    public long BeginPipelineCreation() => Stopwatch.GetTimestamp();

    public void EndPipelineCreation(string pipelineName, long startedTimestamp)
    {
        long elapsedTicks = Math.Max(0L, Stopwatch.GetTimestamp() - startedTimestamp);
        long microseconds = checked((long)Math.Round(
            elapsedTicks * 1_000_000.0 / Stopwatch.Frequency));
        lock (_gate)
        {
            if (_disposed)
                return;
            _pipelineCreationCount = SaturatingIncrement(_pipelineCreationCount);
            _pipelineCreationMicroseconds = SaturatingAdd(
                _pipelineCreationMicroseconds,
                microseconds);
            if (_renderCriticalFramesStarted)
            {
                _renderCriticalPipelineCreationCount = SaturatingIncrement(
                    _renderCriticalPipelineCreationCount);
            }
            _lastCreatedPipeline = pipelineName ?? string.Empty;
            _dirty = true;
        }
    }

    public void MarkRenderCriticalFramesStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _renderCriticalFramesStarted = true;
        }
    }

    public bool Persist()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_dirty && _cacheSaved)
                return true;
        }

        try
        {
            nuint size = 0;
            Result result = _context.Api.GetPipelineCacheData(
                _context.Device,
                _cache,
                &size,
                null);
            if (result != Result.Success || size == 0 ||
                size > GiPipelineCacheFileCodec.MaximumPayloadBytes)
            {
                lock (_gate)
                    _loadStatus = $"Pipeline cache serialization unavailable ({result}, {size} bytes).";
                return false;
            }

            byte[] payload = new byte[checked((int)size)];
            fixed (byte* data = payload)
            {
                result = _context.Api.GetPipelineCacheData(
                    _context.Device,
                    _cache,
                    &size,
                    data);
            }
            if (result != Result.Success)
            {
                lock (_gate)
                    _loadStatus = $"vkGetPipelineCacheData failed ({result}).";
                return false;
            }
            if (size != checked((nuint)payload.Length))
                Array.Resize(ref payload, checked((int)size));

            byte[] encoded = GiPipelineCacheFileCodec.Encode(_identity, payload);
            WriteAtomically(_cachePath, encoded);
            lock (_gate)
            {
                _cacheSaved = true;
                _savedPayloadBytes = checked((ulong)payload.Length);
                _dirty = false;
                _loadStatus = _cacheLoaded
                    ? "Compatible cache loaded and refreshed."
                    : "Pipeline cache saved.";
            }
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                ArgumentException or CryptographicException or OverflowException)
        {
            lock (_gate)
                _loadStatus = $"Pipeline cache save skipped: {ex.Message}";
            return false;
        }
    }

    private byte[] TryLoadCompatiblePayload()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                _loadStatus = "No compatible cache file was present.";
                return Array.Empty<byte>();
            }

            var info = new FileInfo(_cachePath);
            if (info.Length < GiPipelineCacheFileCodec.HeaderSize ||
                info.Length > GiPipelineCacheFileCodec.HeaderSize +
                    (long)GiPipelineCacheFileCodec.MaximumPayloadBytes)
            {
                _cacheRejected = true;
                _loadStatus = "Cache file length is outside the admitted range.";
                return Array.Empty<byte>();
            }

            byte[] encoded = File.ReadAllBytes(_cachePath);
            if (!GiPipelineCacheFileCodec.TryDecode(
                    encoded,
                    _identity,
                    out byte[] payload,
                    out string reason))
            {
                _cacheRejected = true;
                _loadStatus = reason;
                return Array.Empty<byte>();
            }

            _cacheLoaded = true;
            _loadedPayloadBytes = checked((ulong)payload.Length);
            _loadStatus = reason;
            return payload;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                ArgumentException or CryptographicException)
        {
            _cacheRejected = true;
            _loadStatus = $"Cache load skipped: {ex.Message}";
            return Array.Empty<byte>();
        }
    }

    private Result CreateCache(byte[] initialData, out PipelineCache cache)
    {
        fixed (byte* data = initialData)
        {
            var createInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = checked((nuint)initialData.Length),
                PInitialData = initialData.Length == 0 ? null : data
            };
            return _context.Api.CreatePipelineCache(
                _context.Device,
                &createInfo,
                null,
                out cache);
        }
    }

    private static GiPipelineCacheIdentity CreateIdentity(
        VulkanContext context,
        string shaderBundleHash)
    {
        PhysicalDeviceProperties properties = default;
        context.Api.GetPhysicalDeviceProperties(context.PhysicalDevice, &properties);
        byte[] uuid = new byte[16];
        fixed (byte* destination = uuid)
        {
            System.Buffer.MemoryCopy(
                properties.PipelineCacheUuid,
                destination,
                uuid.Length,
                uuid.Length);
        }
        return new GiPipelineCacheIdentity(
            properties.VendorID,
            properties.DeviceID,
            properties.DriverVersion,
            properties.ApiVersion,
            uuid,
            SHA256.HashData(Encoding.UTF8.GetBytes(
                shaderBundleHash ?? string.Empty)),
            SHA256.HashData(Encoding.UTF8.GetBytes(EngineAbi)));
    }

    private static void WriteAtomically(string path, ReadOnlySpan<byte> data)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Pipeline cache path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static ulong SaturatingIncrement(ulong value) =>
        value == ulong.MaxValue ? value : value + 1UL;

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return left;
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }
        Persist();
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_cache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _cache, null);
            _cache = default;
            _disposed = true;
        }
    }
}
