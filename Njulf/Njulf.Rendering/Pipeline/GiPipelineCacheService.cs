using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

public readonly record struct GiPipelineCacheTelemetry(
    bool CacheLoaded,
    bool RuntimeCacheLoaded,
    bool SeedCacheLoaded,
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
    public bool ShaderBundleChanged { get; init; }

    public bool BuildConfigurationChanged { get; init; }

    public bool LegacyEnvelopeLoaded { get; init; }

    public ulong ApplicationCacheHitCount { get; init; }

    public ulong PipelineCompileMissCount { get; init; }

    public ulong PipelineFeedbackUnavailableCount { get; init; }

    public int PeakConcurrentPipelineCreationCount { get; init; }

    public bool PipelineBinaryCacheEnabled { get; init; }

    public bool GraphicsPipelineLibraryEligible { get; init; }

    public ulong WritableBinaryHitCount { get; init; }

    public ulong SeedBinaryHitCount { get; init; }

    public ulong CapturedPipelineBinaryCount { get; init; }

    public string PipelineBinaryStorePath { get; init; } = string.Empty;

    public bool WarmEligible =>
        (RuntimeCacheLoaded ||
         PipelineCreationCount > 0 &&
         WritableBinaryHitCount == PipelineCreationCount) &&
        !ShaderBundleChanged &&
        !BuildConfigurationChanged &&
        !LegacyEnvelopeLoaded &&
        PipelineCompileMissCount == 0;

    public static GiPipelineCacheTelemetry Empty { get; } =
        new(false, false, false, false, false, 0, 0, 0, 0, 0,
            string.Empty, "Not initialized", string.Empty);
}

internal sealed record GiPipelineCacheIdentity(
    uint VendorId,
    uint DeviceId,
    uint DriverVersion,
    uint ApiVersion,
    byte[] PipelineCacheUuid,
    byte[] ShaderBundleHash,
    byte[] EngineAbiHash,
    byte[] BuildConfigurationHash);

/// <summary>
/// Fixed, checksummed envelope around the opaque Vulkan cache blob. Vulkan's
/// own cache header remains authoritative. The shader hash records provenance
/// so unchanged entries can survive shader-bundle revisions, while Njulf's GI
/// ABI remains a hard application-level compatibility boundary.
/// </summary>
internal static class GiPipelineCacheFileCodec
{
    private static ReadOnlySpan<byte> Magic => "NJGIPC01"u8;
    internal const uint LegacyFormatVersion = 1;
    internal const int LegacyHeaderSize = 152;
    internal const uint FormatVersion = 2;
    internal const int HeaderSize = 184;
    internal const int MinimumHeaderSize = LegacyHeaderSize;
    internal const int MaximumPayloadBytes = 512 * 1024 * 1024;

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
        identity.BuildConfigurationHash.CopyTo(header[112..144]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header[144..],
            checked((ulong)payload.Length));
        SHA256.HashData(payload, header[152..184]);
        payload.CopyTo(encoded.AsSpan(HeaderSize));
        return encoded;
    }

    /// <summary>
    /// Writes the checked envelope without allocating a second payload-sized
    /// byte array. The caller retains ownership of <paramref name="payload"/>.
    /// </summary>
    internal static void Write(
        Stream destination,
        GiPipelineCacheIdentity identity,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream is not writable.", nameof(destination));

        byte[] header = BuildHeader(identity, payload);
        destination.Write(header);
        destination.Write(payload);
    }

    /// <summary>
    /// Reads a checked envelope directly into its Vulkan payload buffer. This
    /// avoids retaining both the encoded file and a copied payload in memory.
    /// </summary>
    internal static bool TryRead(
        Stream source,
        GiPipelineCacheIdentity expected,
        out byte[] payload,
        out bool shaderBundleChanged,
        out bool buildConfigurationChanged,
        out bool legacyEnvelopeLoaded,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(source);
        payload = Array.Empty<byte>();
        shaderBundleChanged = false;
        buildConfigurationChanged = false;
        legacyEnvelopeLoaded = false;
        reason = string.Empty;

        try
        {
            ValidateIdentity(expected);
            if (!source.CanRead)
                return Reject("Cache stream is not readable.", out reason);
            if (source.CanSeek && source.Length < MinimumHeaderSize)
                return Reject("Cache file is truncated.", out reason);

            byte[] prefix = new byte[16];
            if (!TryReadExactly(source, prefix))
                return Reject("Cache file is truncated.", out reason);
            if (!prefix.AsSpan(0, 8).SequenceEqual(Magic))
                return Reject("Cache magic is not recognized.", out reason);

            uint formatVersion = BinaryPrimitives.ReadUInt32LittleEndian(
                prefix.AsSpan(8));
            int headerSize;
            int payloadLengthOffset;
            int payloadChecksumOffset;
            switch (formatVersion)
            {
                case LegacyFormatVersion:
                    headerSize = LegacyHeaderSize;
                    payloadLengthOffset = 112;
                    payloadChecksumOffset = 120;
                    legacyEnvelopeLoaded = true;
                    buildConfigurationChanged = true;
                    break;
                case FormatVersion:
                    headerSize = HeaderSize;
                    payloadLengthOffset = 144;
                    payloadChecksumOffset = 152;
                    break;
                default:
                    return Reject(
                        "Cache envelope version does not match.",
                        out reason);
            }

            byte[] header = new byte[headerSize];
            prefix.CopyTo(header, 0);
            if (!TryReadExactly(source, header.AsSpan(prefix.Length)))
                return Reject("Cache file is truncated.", out reason);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) !=
                headerSize)
            {
                return Reject("Cache header size does not match.", out reason);
            }
            if (!IdentityMatches(
                    header,
                    expected,
                    legacyEnvelopeLoaded,
                    out buildConfigurationChanged,
                    out reason))
            {
                return false;
            }

            ulong declaredLength = BinaryPrimitives.ReadUInt64LittleEndian(
                header.AsSpan(payloadLengthOffset));
            if (declaredLength > MaximumPayloadBytes ||
                declaredLength > int.MaxValue ||
                source.CanSeek &&
                declaredLength != checked((ulong)(source.Length - headerSize)))
            {
                return Reject("Cache payload length is invalid.", out reason);
            }

            payload = new byte[checked((int)declaredLength)];
            if (!TryReadExactly(source, payload))
            {
                payload = Array.Empty<byte>();
                return Reject("Cache file is truncated.", out reason);
            }
            if (!source.CanSeek && source.ReadByte() != -1)
            {
                payload = Array.Empty<byte>();
                return Reject("Cache payload length is invalid.", out reason);
            }

            Span<byte> actualHash = stackalloc byte[32];
            SHA256.HashData(payload, actualHash);
            if (!CryptographicOperations.FixedTimeEquals(
                    header.AsSpan(payloadChecksumOffset, 32),
                    actualHash))
            {
                payload = Array.Empty<byte>();
                return Reject("Cache payload checksum failed.", out reason);
            }

            shaderBundleChanged = !CryptographicOperations.FixedTimeEquals(
                header.AsSpan(48, 32),
                expected.ShaderBundleHash);
            reason = DescribeCompatibleCache(
                shaderBundleChanged,
                buildConfigurationChanged,
                legacyEnvelopeLoaded);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or ArgumentException or OverflowException or
                CryptographicException)
        {
            payload = Array.Empty<byte>();
            reason = $"Cache stream could not be decoded: {ex.Message}";
            return false;
        }
    }

    private static byte[] BuildHeader(
        GiPipelineCacheIdentity identity,
        ReadOnlySpan<byte> payload)
    {
        ValidateIdentity(identity);
        if (payload.Length > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));

        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), identity.VendorId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), identity.DeviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), identity.DriverVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), identity.ApiVersion);
        identity.PipelineCacheUuid.CopyTo(header, 32);
        identity.ShaderBundleHash.CopyTo(header, 48);
        identity.EngineAbiHash.CopyTo(header, 80);
        identity.BuildConfigurationHash.CopyTo(header, 112);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(144),
            checked((ulong)payload.Length));
        SHA256.HashData(payload, header.AsSpan(152, 32));
        return header;
    }

    private static bool IdentityMatches(
        ReadOnlySpan<byte> header,
        GiPipelineCacheIdentity expected,
        bool legacyEnvelopeLoaded,
        out bool buildConfigurationChanged,
        out string reason)
    {
        buildConfigurationChanged = legacyEnvelopeLoaded;
        reason = string.Empty;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != expected.VendorId ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != expected.DeviceId ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != expected.DriverVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[28..]) != expected.ApiVersion)
        {
            return Reject("Physical-device or driver identity changed.", out reason);
        }
        if (!CryptographicOperations.FixedTimeEquals(
                header[32..48], expected.PipelineCacheUuid))
            return Reject("Vulkan pipelineCacheUUID changed.", out reason);
        if (!CryptographicOperations.FixedTimeEquals(
                header[80..112], expected.EngineAbiHash))
            return Reject("GI engine ABI changed.", out reason);
        if (!legacyEnvelopeLoaded)
        {
            buildConfigurationChanged =
                !CryptographicOperations.FixedTimeEquals(
                    header[112..144],
                    expected.BuildConfigurationHash);
        }
        return true;
    }

    private static bool TryReadExactly(Stream source, Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = source.Read(destination[read..]);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> encoded,
        GiPipelineCacheIdentity expected,
        out byte[] payload,
        out bool shaderBundleChanged,
        out bool buildConfigurationChanged,
        out bool legacyEnvelopeLoaded,
        out string reason)
    {
        payload = Array.Empty<byte>();
        shaderBundleChanged = false;
        buildConfigurationChanged = false;
        legacyEnvelopeLoaded = false;
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

        if (encoded.Length < MinimumHeaderSize)
            return Reject("Cache file is truncated.", out reason);
        if (!encoded[..8].SequenceEqual(Magic))
            return Reject("Cache magic is not recognized.", out reason);

        uint formatVersion =
            BinaryPrimitives.ReadUInt32LittleEndian(encoded[8..]);
        int headerSize;
        int payloadLengthOffset;
        int payloadChecksumOffset;
        switch (formatVersion)
        {
            case LegacyFormatVersion:
                headerSize = LegacyHeaderSize;
                payloadLengthOffset = 112;
                payloadChecksumOffset = 120;
                legacyEnvelopeLoaded = true;
                buildConfigurationChanged = true;
                break;
            case FormatVersion:
                headerSize = HeaderSize;
                payloadLengthOffset = 144;
                payloadChecksumOffset = 152;
                break;
            default:
                return Reject(
                    "Cache envelope version does not match.",
                    out reason);
        }

        if (encoded.Length < headerSize)
            return Reject("Cache file is truncated.", out reason);
        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[12..]) !=
            headerSize)
        {
            return Reject("Cache header size does not match.", out reason);
        }
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
                encoded[80..112], expected.EngineAbiHash))
            return Reject("GI engine ABI changed.", out reason);
        if (!legacyEnvelopeLoaded)
        {
            buildConfigurationChanged =
                !CryptographicOperations.FixedTimeEquals(
                    encoded[112..144],
                    expected.BuildConfigurationHash);
        }

        ulong declaredLength =
            BinaryPrimitives.ReadUInt64LittleEndian(
                encoded[payloadLengthOffset..]);
        if (declaredLength > MaximumPayloadBytes ||
            declaredLength != checked((ulong)(encoded.Length - headerSize)))
        {
            return Reject("Cache payload length is invalid.", out reason);
        }

        ReadOnlySpan<byte> sourcePayload = encoded[headerSize..];
        Span<byte> actualHash = stackalloc byte[32];
        SHA256.HashData(sourcePayload, actualHash);
        if (!CryptographicOperations.FixedTimeEquals(
                encoded[payloadChecksumOffset..(payloadChecksumOffset + 32)],
                actualHash))
            return Reject("Cache payload checksum failed.", out reason);

        payload = sourcePayload.ToArray();
        shaderBundleChanged = !CryptographicOperations.FixedTimeEquals(
            encoded[48..80], expected.ShaderBundleHash);
        reason = DescribeCompatibleCache(
            shaderBundleChanged,
            buildConfigurationChanged,
            legacyEnvelopeLoaded);
        return true;
    }

    private static string DescribeCompatibleCache(
        bool shaderBundleChanged,
        bool buildConfigurationChanged,
        bool legacyEnvelopeLoaded)
    {
        if (legacyEnvelopeLoaded && shaderBundleChanged)
        {
            return "Compatible legacy cache loaded from a different shader " +
                   "bundle; build configuration provenance is unavailable.";
        }
        if (legacyEnvelopeLoaded)
        {
            return "Compatible legacy cache loaded; build configuration " +
                   "provenance is unavailable.";
        }
        if (shaderBundleChanged && buildConfigurationChanged)
        {
            return "Compatible cache loaded from a different shader bundle " +
                   "and build configuration.";
        }
        if (shaderBundleChanged)
            return "Compatible cache loaded from a different shader bundle.";
        if (buildConfigurationChanged)
        {
            return "Compatible cache loaded from a different build " +
                   "configuration.";
        }
        return "Compatible cache loaded.";
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
        if (identity.BuildConfigurationHash is not { Length: 32 })
        {
            throw new ArgumentException(
                "Build configuration hash must be 32 bytes.",
                nameof(identity));
        }
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
    private readonly ReaderWriterLockSlim _cacheAccess =
        new(LockRecursionPolicy.NoRecursion);
    private readonly PipelineCompilationScheduler _compilationScheduler = new();
    private readonly List<PipelineCreationObservation> _observations = new();
    private readonly List<Task> _binaryPersistTasks = new();
    private readonly VulkanContext _context;
    private readonly GiPipelineCacheIdentity _identity;
    private readonly string _cachePath;
    private readonly string _seedCachePath;
    private readonly PipelineBinaryStore? _pipelineBinaryStore;
    private PipelineCache _cache;
    private bool _renderCriticalFramesStarted;
    private bool _dirty;
    private bool _disposed;
    private bool _disposeStarted;
    private bool _cacheLoaded;
    private bool _runtimeCacheLoaded;
    private bool _seedCacheLoaded;
    private bool _cacheRejected;
    private bool _cacheSaved;
    private bool _loadedFromDifferentShaderBundle;
    private bool _loadedFromDifferentBuildConfiguration;
    private bool _legacyEnvelopeLoaded;
    private ulong _loadedPayloadBytes;
    private ulong _savedPayloadBytes;
    private ulong _pipelineCreationCount;
    private long _pipelineCreationMicroseconds;
    private ulong _renderCriticalPipelineCreationCount;
    private ulong _applicationCacheHitCount;
    private ulong _pipelineCompileMissCount;
    private ulong _pipelineFeedbackUnavailableCount;
    private ulong _writableBinaryHitCount;
    private ulong _seedBinaryHitCount;
    private ulong _capturedPipelineBinaryCount;
    private ulong _cacheMutationGeneration;
    private int _activePipelineCreationCount;
    private int _peakConcurrentPipelineCreationCount;
    private string _loadStatus = "Empty cache created.";
    private string _lastCreatedPipeline = string.Empty;
    private Task<bool>? _scheduledPersistTask;

    public GiPipelineCacheService(
        VulkanContext context,
        string shaderBundleHash,
        string? cacheDirectory = null)
        : this(
            context,
            shaderBundleHash,
            "unknown",
            cacheDirectory)
    {
    }

    public GiPipelineCacheService(
        VulkanContext context,
        string shaderBundleHash,
        string compileConfiguration,
        string? cacheDirectory)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _identity = CreateIdentity(
            context,
            shaderBundleHash,
            compileConfiguration);
        _pipelineBinaryStore = TryCreatePipelineBinaryStore();
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
        string? configuredSeedDirectory = Environment.GetEnvironmentVariable(
            "NJULF_VULKAN_PIPELINE_CACHE_SEED_DIRECTORY");
        string seedRoot = string.IsNullOrWhiteSpace(configuredSeedDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "PipelineCacheSeeds")
            : Path.GetFullPath(configuredSeedDirectory);
        _seedCachePath = Path.Combine(
            seedRoot,
            $"gi-{_identity.VendorId:x8}-{_identity.DeviceId:x8}.njvkcache");

        byte[] initialData = TryLoadCompatiblePayload();
        Result result = CreateCache(initialData, out _cache);
        if (result != Result.Success && initialData.Length > 0)
        {
            _cacheRejected = true;
            _cacheLoaded = false;
            _runtimeCacheLoaded = false;
            _seedCacheLoaded = false;
            _loadedFromDifferentShaderBundle = false;
            _loadedFromDifferentBuildConfiguration = false;
            _legacyEnvelopeLoaded = false;
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

    internal PipelineCompilationScheduler CompilationScheduler =>
        _compilationScheduler;

    internal IReadOnlyList<PipelineCreationObservation> CreationObservations
    {
        get
        {
            lock (_gate)
                return _observations.ToArray();
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
                    _runtimeCacheLoaded,
                    _seedCacheLoaded,
                    _cacheRejected,
                    _cacheSaved,
                    _loadedPayloadBytes,
                    _savedPayloadBytes,
                    _pipelineCreationCount,
                    _pipelineCreationMicroseconds,
                    _renderCriticalPipelineCreationCount,
                    _cachePath,
                    _loadStatus,
                    _lastCreatedPipeline)
                {
                    ShaderBundleChanged =
                        _loadedFromDifferentShaderBundle,
                    BuildConfigurationChanged =
                        _loadedFromDifferentBuildConfiguration,
                    LegacyEnvelopeLoaded = _legacyEnvelopeLoaded,
                    ApplicationCacheHitCount = _applicationCacheHitCount,
                    PipelineCompileMissCount = _pipelineCompileMissCount,
                    PipelineFeedbackUnavailableCount =
                        _pipelineFeedbackUnavailableCount,
                    PeakConcurrentPipelineCreationCount =
                        _peakConcurrentPipelineCreationCount,
                    PipelineBinaryCacheEnabled =
                        _pipelineBinaryStore != null,
                    GraphicsPipelineLibraryEligible =
                        _context.PipelineOptimizationSupport
                            .GraphicsPipelineLibraryFastLinking,
                    WritableBinaryHitCount = _writableBinaryHitCount,
                    SeedBinaryHitCount = _seedBinaryHitCount,
                    CapturedPipelineBinaryCount =
                        _capturedPipelineBinaryCount,
                    PipelineBinaryStorePath =
                        _pipelineBinaryStore?.WritableRoot ?? string.Empty
                };
            }
        }
    }

    public long BeginPipelineCreation()
    {
        _cacheAccess.EnterReadLock();
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _disposeStarted, this);
                _activePipelineCreationCount++;
                _peakConcurrentPipelineCreationCount = Math.Max(
                    _peakConcurrentPipelineCreationCount,
                    _activePipelineCreationCount);
            }
            return Stopwatch.GetTimestamp();
        }
        catch
        {
            _cacheAccess.ExitReadLock();
            throw;
        }
    }

    public void EndPipelineCreation(string pipelineName, long startedTimestamp)
    {
        EndPipelineCreation(
            new PipelineArtifactId(pipelineName),
            startedTimestamp,
            result: Result.Success,
            feedbackValid: false,
            applicationCacheHit: false,
            driverDurationNanoseconds: 0,
            stageCount: 0,
            source: PipelineArtifactSource.Unknown);
    }

    private void EndPipelineCreation(
        PipelineArtifactId artifactId,
        long startedTimestamp,
        Result result,
        bool feedbackValid,
        bool applicationCacheHit,
        ulong driverDurationNanoseconds,
        int stageCount,
        PipelineArtifactSource source,
        bool pipelineCacheParticipated = true)
    {
        try
        {
            long elapsedTicks = Math.Max(
                0L,
                Stopwatch.GetTimestamp() - startedTimestamp);
            long microseconds = checked((long)Math.Round(
                elapsedTicks * 1_000_000.0 / Stopwatch.Frequency));
            lock (_gate)
            {
                _activePipelineCreationCount = Math.Max(
                    0,
                    _activePipelineCreationCount - 1);
                if (_disposed)
                    return;
                _pipelineCreationCount = SaturatingIncrement(
                    _pipelineCreationCount);
                _pipelineCreationMicroseconds = SaturatingAdd(
                    _pipelineCreationMicroseconds,
                    microseconds);
                if (_renderCriticalFramesStarted)
                {
                    _renderCriticalPipelineCreationCount = SaturatingIncrement(
                        _renderCriticalPipelineCreationCount);
                }
                bool compileRequired =
                    result == Result.PipelineCompileRequired;
                if (feedbackValid)
                {
                    if (applicationCacheHit)
                    {
                        _applicationCacheHitCount = SaturatingIncrement(
                            _applicationCacheHitCount);
                    }
                    else if (result == Result.Success &&
                             source == PipelineArtifactSource.Compiled)
                    {
                        _pipelineCompileMissCount = SaturatingIncrement(
                            _pipelineCompileMissCount);
                    }
                }
                else if (source == PipelineArtifactSource.Unknown)
                {
                    _pipelineFeedbackUnavailableCount = SaturatingIncrement(
                        _pipelineFeedbackUnavailableCount);
                }
                if (compileRequired)
                {
                    _pipelineCompileMissCount = SaturatingIncrement(
                        _pipelineCompileMissCount);
                }

                _lastCreatedPipeline = artifactId.Value;
                if (pipelineCacheParticipated &&
                    result == Result.Success && source is not
                    (PipelineArtifactSource.WritableBinary or
                     PipelineArtifactSource.SeedBinary))
                {
                    _dirty = true;
                    _cacheMutationGeneration = SaturatingIncrement(
                        _cacheMutationGeneration);
                }
                _observations.Add(new PipelineCreationObservation(
                    artifactId,
                    source,
                    microseconds,
                    driverDurationNanoseconds,
                    feedbackValid,
                    applicationCacheHit,
                    compileRequired,
                    _renderCriticalFramesStarted,
                    _activePipelineCreationCount + 1,
                    stageCount));
            }
        }
        finally
        {
            _cacheAccess.ExitReadLock();
        }
    }

    internal Result CreateGraphicsPipeline(
        PipelineArtifactId artifactId,
        GraphicsPipelineCreateInfo* createInfo,
        out VkPipeline pipeline,
        PipelineCacheUsage cacheUsage = PipelineCacheUsage.Shared)
    {
        if (createInfo == null)
            throw new ArgumentNullException(nameof(createInfo));
        if (!Enum.IsDefined(cacheUsage))
            throw new ArgumentOutOfRangeException(nameof(cacheUsage));
        long started = BeginPipelineCreation();
        PipelineCreationFeedback feedback = default;
        int stageCount = checked((int)createInfo->StageCount);
        PipelineCreationFeedback* stageFeedback = stackalloc
            PipelineCreationFeedback[Math.Max(1, stageCount)];
        void* originalNext = createInfo->PNext;
        PipelineCreateFlags originalFlags = createInfo->Flags;
        bool feedbackAttached = false;
        bool cacheHit = false;
        PipelineArtifactSource source = PipelineArtifactSource.Unknown;
        byte[] pipelineKey = cacheUsage == PipelineCacheUsage.Shared
            ? TryGetPipelineKey(createInfo)
            : Array.Empty<byte>();
        bool capturePipelineData = ShouldCapturePipelineData(pipelineKey);
        var extendedFlagsInfo = new PipelineCreateFlags2CreateInfoKHR
        {
            SType = StructureType.PipelineCreateFlags2CreateInfoKhr,
            PNext = originalNext,
            Flags = BuildExtendedPipelineCreationFlags(
                originalFlags,
                capturePipelineData)
        };
        var feedbackInfo = new PipelineCreationFeedbackCreateInfo
        {
            SType = StructureType.PipelineCreationFeedbackCreateInfo,
            PNext = capturePipelineData
                ? &extendedFlagsInfo
                : originalNext,
            PPipelineCreationFeedback = &feedback,
            PipelineStageCreationFeedbackCount = checked((uint)stageCount),
            PPipelineStageCreationFeedbacks = stageCount == 0
                ? null
                : stageFeedback
        };
        Result result = default;
        pipeline = default;
        try
        {
            if (TryCreateGraphicsPipelineFromStoredBinary(
                    createInfo,
                    pipelineKey,
                    out pipeline,
                    out PipelineArtifactSource binarySource,
                    out Result binaryResult))
            {
                result = binaryResult;
                source = binarySource;
                RecordBinaryHit(binarySource);
                return result;
            }

            createInfo->PNext = originalNext;
            feedbackAttached = _context.PipelineCreationFeedbackEnabled;
            if (feedbackAttached)
                createInfo->PNext = &feedbackInfo;
            else if (capturePipelineData)
                createInfo->PNext = &extendedFlagsInfo;
            if (!capturePipelineData)
                ApplyVerificationFlag(ref createInfo->Flags);
            result = _context.Api.CreateGraphicsPipelines(
                _context.Device,
                capturePipelineData || cacheUsage == PipelineCacheUsage.Bypass
                    ? default
                    : _cache,
                1,
                createInfo,
                null,
                out pipeline);
            bool feedbackValidNow = feedbackAttached &&
                (feedback.Flags & PipelineCreationFeedbackFlags.ValidBit) != 0;
            cacheHit = feedbackValidNow &&
                (feedback.Flags &
                 PipelineCreationFeedbackFlags.ApplicationPipelineCacheHitBit) != 0;
            source = cacheHit
                ? PipelineArtifactSource.ApplicationCache
                : feedbackValidNow && result == Result.Success
                    ? PipelineArtifactSource.Compiled
                    : PipelineArtifactSource.Unknown;
            if (result == Result.Success && pipelineKey.Length != 0)
            {
                createInfo->PNext = originalNext;
                createInfo->Flags = originalFlags;
                CapturePipelineBinaries(
                    artifactId,
                    pipelineKey,
                    capturePipelineData ? pipeline : default,
                    capturePipelineData ? null : createInfo);
            }
            return result;
        }
        finally
        {
            createInfo->PNext = originalNext;
            createInfo->Flags = originalFlags;
            bool feedbackValid = feedbackAttached &&
                (feedback.Flags &
                 PipelineCreationFeedbackFlags.ValidBit) != 0;
            EndPipelineCreation(
                artifactId,
                started,
                result,
                feedbackValid,
                cacheHit,
                feedback.Duration,
                stageCount,
                source,
                pipelineCacheParticipated:
                    cacheUsage == PipelineCacheUsage.Shared &&
                    !capturePipelineData);
        }
    }

    internal Result CreateComputePipeline(
        PipelineArtifactId artifactId,
        ComputePipelineCreateInfo* createInfo,
        out VkPipeline pipeline,
        PipelineCacheUsage cacheUsage = PipelineCacheUsage.Shared)
    {
        if (createInfo == null)
            throw new ArgumentNullException(nameof(createInfo));
        if (!Enum.IsDefined(cacheUsage))
            throw new ArgumentOutOfRangeException(nameof(cacheUsage));
        long started = BeginPipelineCreation();
        PipelineCreationFeedback feedback = default;
        PipelineCreationFeedback stageFeedback = default;
        void* originalNext = createInfo->PNext;
        PipelineCreateFlags originalFlags = createInfo->Flags;
        bool feedbackAttached = false;
        bool cacheHit = false;
        PipelineArtifactSource source = PipelineArtifactSource.Unknown;
        byte[] pipelineKey = cacheUsage == PipelineCacheUsage.Shared
            ? TryGetPipelineKey(createInfo)
            : Array.Empty<byte>();
        bool capturePipelineData = ShouldCapturePipelineData(pipelineKey);
        var extendedFlagsInfo = new PipelineCreateFlags2CreateInfoKHR
        {
            SType = StructureType.PipelineCreateFlags2CreateInfoKhr,
            PNext = originalNext,
            Flags = BuildExtendedPipelineCreationFlags(
                originalFlags,
                capturePipelineData)
        };
        var feedbackInfo = new PipelineCreationFeedbackCreateInfo
        {
            SType = StructureType.PipelineCreationFeedbackCreateInfo,
            PNext = capturePipelineData
                ? &extendedFlagsInfo
                : originalNext,
            PPipelineCreationFeedback = &feedback,
            PipelineStageCreationFeedbackCount = 1,
            PPipelineStageCreationFeedbacks = &stageFeedback
        };
        Result result = default;
        pipeline = default;
        try
        {
            if (TryCreateComputePipelineFromStoredBinary(
                    createInfo,
                    pipelineKey,
                    out pipeline,
                    out PipelineArtifactSource binarySource,
                    out Result binaryResult))
            {
                result = binaryResult;
                source = binarySource;
                RecordBinaryHit(binarySource);
                return result;
            }

            createInfo->PNext = originalNext;
            feedbackAttached = _context.PipelineCreationFeedbackEnabled;
            if (feedbackAttached)
                createInfo->PNext = &feedbackInfo;
            else if (capturePipelineData)
                createInfo->PNext = &extendedFlagsInfo;
            if (!capturePipelineData)
                ApplyVerificationFlag(ref createInfo->Flags);
            result = _context.Api.CreateComputePipelines(
                _context.Device,
                capturePipelineData || cacheUsage == PipelineCacheUsage.Bypass
                    ? default
                    : _cache,
                1,
                createInfo,
                null,
                out pipeline);
            bool feedbackValidNow = feedbackAttached &&
                (feedback.Flags & PipelineCreationFeedbackFlags.ValidBit) != 0;
            cacheHit = feedbackValidNow &&
                (feedback.Flags &
                 PipelineCreationFeedbackFlags.ApplicationPipelineCacheHitBit) != 0;
            source = cacheHit
                ? PipelineArtifactSource.ApplicationCache
                : feedbackValidNow && result == Result.Success
                    ? PipelineArtifactSource.Compiled
                    : PipelineArtifactSource.Unknown;
            if (result == Result.Success && pipelineKey.Length != 0)
            {
                createInfo->PNext = originalNext;
                createInfo->Flags = originalFlags;
                CapturePipelineBinaries(
                    artifactId,
                    pipelineKey,
                    capturePipelineData ? pipeline : default,
                    capturePipelineData ? null : createInfo);
            }
            return result;
        }
        finally
        {
            createInfo->PNext = originalNext;
            createInfo->Flags = originalFlags;
            bool feedbackValid = feedbackAttached &&
                (feedback.Flags &
                 PipelineCreationFeedbackFlags.ValidBit) != 0;
            EndPipelineCreation(
                artifactId,
                started,
                result,
                feedbackValid,
                cacheHit,
                feedback.Duration,
                stageCount: 1,
                source,
                pipelineCacheParticipated:
                    cacheUsage == PipelineCacheUsage.Shared &&
                    !capturePipelineData);
        }
    }

    private bool ShouldCapturePipelineData(byte[] pipelineKey)
    {
        PipelineOptimizationDeviceSupport support =
            _context.PipelineOptimizationSupport;
        return pipelineKey.Length != 0 &&
               _pipelineBinaryStore != null &&
               _context.KhrPipelineBinary != null &&
               (!support.PipelineBinaryInternalCache ||
                !support.PipelineBinaryPrefersInternalCache);
    }

    private PipelineCreateFlags2 BuildExtendedPipelineCreationFlags(
        PipelineCreateFlags flags,
        bool capturePipelineData)
    {
        var extendedFlags = (PipelineCreateFlags2)(long)(uint)(int)flags;
        if (capturePipelineData)
        {
            extendedFlags |= PipelineCreateFlags2
                .Vk2CaptureDataBitKhr;
        }
        if (_context.PipelineCreationCacheControlEnabled &&
            RendererBuildConfiguration.VerifyPipelineCacheCompleteness)
        {
            extendedFlags |= PipelineCreateFlags2
                .Vk2FailOnPipelineCompileRequiredBit;
        }
        return extendedFlags;
    }

    private void ApplyVerificationFlag(ref PipelineCreateFlags flags)
    {
        if (_context.PipelineCreationCacheControlEnabled &&
            RendererBuildConfiguration.VerifyPipelineCacheCompleteness)
        {
            flags |= PipelineCreateFlags
                .CreateFailOnPipelineCompileRequiredBit;
        }
    }

    private byte[] TryGetPipelineKey(void* specificCreateInfo)
    {
        if (_pipelineBinaryStore == null ||
            _context.KhrPipelineBinary == null ||
            specificCreateInfo == null)
        {
            return Array.Empty<byte>();
        }

        var genericCreateInfo = new PipelineCreateInfoKHR
        {
            SType = StructureType.PipelineCreateInfoKhr,
            PNext = specificCreateInfo
        };
        var key = new PipelineBinaryKeyKHR
        {
            SType = StructureType.PipelineBinaryKeyKhr
        };
        Result result = _context.KhrPipelineBinary.GetPipelineKey(
            _context.Device,
            &genericCreateInfo,
            &key);
        return result == Result.Success
            ? CopyPipelineBinaryKey(key)
            : Array.Empty<byte>();
    }

    private bool TryCreateGraphicsPipelineFromStoredBinary(
        GraphicsPipelineCreateInfo* createInfo,
        byte[] pipelineKey,
        out VkPipeline pipeline,
        out PipelineArtifactSource source,
        out Result result)
    {
        pipeline = default;
        source = PipelineArtifactSource.Unknown;
        result = default;
        if (!TryCreateStoredBinaryHandles(
                pipelineKey,
                out PipelineBinaryLookup lookup,
                out PipelineBinaryKHR[] handles))
        {
            return false;
        }

        void* originalNext = createInfo->PNext;
        try
        {
            fixed (PipelineBinaryKHR* handlePointer = handles)
            {
                var binaryInfo = new PipelineBinaryInfoKHR
                {
                    SType = StructureType.PipelineBinaryInfoKhr,
                    PNext = originalNext,
                    BinaryCount = checked((uint)handles.Length),
                    PPipelineBinaries = handlePointer
                };
                createInfo->PNext = &binaryInfo;
                result = _context.Api.CreateGraphicsPipelines(
                    _context.Device,
                    default,
                    1,
                    createInfo,
                    null,
                    out pipeline);
            }
        }
        finally
        {
            createInfo->PNext = originalNext;
            DestroyPipelineBinaryHandles(handles);
        }

        if (result == Result.Success)
        {
            source = lookup.Source;
            return true;
        }

        if (lookup.Source == PipelineArtifactSource.WritableBinary)
            _pipelineBinaryStore?.InvalidateWritable(pipelineKey);
        pipeline = default;
        return false;
    }

    private bool TryCreateComputePipelineFromStoredBinary(
        ComputePipelineCreateInfo* createInfo,
        byte[] pipelineKey,
        out VkPipeline pipeline,
        out PipelineArtifactSource source,
        out Result result)
    {
        pipeline = default;
        source = PipelineArtifactSource.Unknown;
        result = default;
        if (!TryCreateStoredBinaryHandles(
                pipelineKey,
                out PipelineBinaryLookup lookup,
                out PipelineBinaryKHR[] handles))
        {
            return false;
        }

        void* originalNext = createInfo->PNext;
        try
        {
            fixed (PipelineBinaryKHR* handlePointer = handles)
            {
                var binaryInfo = new PipelineBinaryInfoKHR
                {
                    SType = StructureType.PipelineBinaryInfoKhr,
                    PNext = originalNext,
                    BinaryCount = checked((uint)handles.Length),
                    PPipelineBinaries = handlePointer
                };
                createInfo->PNext = &binaryInfo;
                result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    default,
                    1,
                    createInfo,
                    null,
                    out pipeline);
            }
        }
        finally
        {
            createInfo->PNext = originalNext;
            DestroyPipelineBinaryHandles(handles);
        }

        if (result == Result.Success)
        {
            source = lookup.Source;
            return true;
        }

        if (lookup.Source == PipelineArtifactSource.WritableBinary)
            _pipelineBinaryStore?.InvalidateWritable(pipelineKey);
        pipeline = default;
        return false;
    }

    private bool TryCreateStoredBinaryHandles(
        byte[] pipelineKey,
        out PipelineBinaryLookup lookup,
        out PipelineBinaryKHR[] handles)
    {
        lookup = default;
        handles = Array.Empty<PipelineBinaryKHR>();
        if (pipelineKey.Length == 0 ||
            _pipelineBinaryStore == null ||
            _context.KhrPipelineBinary == null ||
            !_pipelineBinaryStore.TryLoad(pipelineKey, out lookup))
        {
            return false;
        }

        int count = lookup.Binaries.Count;
        var keys = new PipelineBinaryKeyKHR[count];
        var data = new PipelineBinaryDataKHR[count];
        var pinnedData = new GCHandle[count];
        handles = new PipelineBinaryKHR[count];
        Result result = default;
        try
        {
            fixed (PipelineBinaryKeyKHR* keyPointer = keys)
            {
                for (int index = 0; index < count; index++)
                {
                    PipelineBinaryBlob binary = lookup.Binaries[index];
                    if (binary.Key.Length is 0 or > 32 ||
                        binary.Data.Length == 0)
                    {
                        return false;
                    }
                    keys[index].SType = StructureType.PipelineBinaryKeyKhr;
                    keys[index].KeySize = checked((uint)binary.Key.Length);
                    fixed (byte* source = binary.Key)
                    {
                        System.Buffer.MemoryCopy(
                            source,
                            keyPointer[index].Key,
                            32,
                            binary.Key.Length);
                    }
                    pinnedData[index] = GCHandle.Alloc(
                        binary.Data,
                        GCHandleType.Pinned);
                    data[index] = new PipelineBinaryDataKHR
                    {
                        DataSize = checked((nuint)binary.Data.Length),
                        PData = (void*)pinnedData[index]
                            .AddrOfPinnedObject()
                    };
                }

                fixed (PipelineBinaryDataKHR* dataPointer = data)
                fixed (PipelineBinaryKHR* handlePointer = handles)
                {
                    var keysAndData = new PipelineBinaryKeysAndDataKHR
                    {
                        BinaryCount = checked((uint)count),
                        PPipelineBinaryKeys = keyPointer,
                        PPipelineBinaryData = dataPointer
                    };
                    var createInfo = new PipelineBinaryCreateInfoKHR
                    {
                        SType = StructureType.PipelineBinaryCreateInfoKhr,
                        PKeysAndDataInfo = &keysAndData
                    };
                    var handlesInfo = new PipelineBinaryHandlesInfoKHR
                    {
                        SType = StructureType.PipelineBinaryHandlesInfoKhr,
                        PipelineBinaryCount = checked((uint)count),
                        PPipelineBinaries = handlePointer
                    };
                    result = _context.KhrPipelineBinary
                        .CreatePipelineBinaries(
                            _context.Device,
                            &createInfo,
                            null,
                            &handlesInfo);
                }
            }
        }
        finally
        {
            foreach (GCHandle pinned in pinnedData)
            {
                if (pinned.IsAllocated)
                    pinned.Free();
            }
        }

        if (result == Result.Success)
            return true;

        DestroyPipelineBinaryHandles(handles);
        handles = Array.Empty<PipelineBinaryKHR>();
        if (lookup.Source == PipelineArtifactSource.WritableBinary)
            _pipelineBinaryStore.InvalidateWritable(pipelineKey);
        return false;
    }

    private void CapturePipelineBinaries(
        PipelineArtifactId artifactId,
        byte[] pipelineKey,
        VkPipeline capturedPipeline,
        void* specificCreateInfo)
    {
        if (_pipelineBinaryStore == null ||
            _context.KhrPipelineBinary == null ||
            (capturedPipeline.Handle == 0 &&
             (!_context.PipelineOptimizationSupport
                  .PipelineBinaryInternalCache ||
              specificCreateInfo == null)))
        {
            return;
        }

        PipelineBinaryKHR[] handles = Array.Empty<PipelineBinaryKHR>();
        try
        {
            PipelineCreateInfoKHR genericCreateInfo = default;
            var createInfo = new PipelineBinaryCreateInfoKHR
            {
                SType = StructureType.PipelineBinaryCreateInfoKhr,
                Pipeline = capturedPipeline
            };
            if (capturedPipeline.Handle == 0)
            {
                genericCreateInfo = new PipelineCreateInfoKHR
                {
                    SType = StructureType.PipelineCreateInfoKhr,
                    PNext = specificCreateInfo
                };
                createInfo.PPipelineCreateInfo = &genericCreateInfo;
            }
            var handlesInfo = new PipelineBinaryHandlesInfoKHR
            {
                SType = StructureType.PipelineBinaryHandlesInfoKhr
            };
            Result result = _context.KhrPipelineBinary
                .CreatePipelineBinaries(
                    _context.Device,
                    &createInfo,
                    null,
                    &handlesInfo);
            if (result != Result.Success ||
                handlesInfo.PipelineBinaryCount == 0 ||
                handlesInfo.PipelineBinaryCount > 32)
            {
                return;
            }

            handles = new PipelineBinaryKHR[
                handlesInfo.PipelineBinaryCount];
            fixed (PipelineBinaryKHR* handlePointer = handles)
            {
                handlesInfo.PPipelineBinaries = handlePointer;
                result = _context.KhrPipelineBinary
                    .CreatePipelineBinaries(
                        _context.Device,
                        &createInfo,
                        null,
                        &handlesInfo);
            }
            if (result != Result.Success)
                return;

            var binaries = new List<PipelineBinaryBlob>(handles.Length);
            long capturedBytes = 0;
            foreach (PipelineBinaryKHR handle in handles)
            {
                var dataInfo = new PipelineBinaryDataInfoKHR
                {
                    SType = StructureType.PipelineBinaryDataInfoKhr,
                    PipelineBinary = handle
                };
                var binaryKey = new PipelineBinaryKeyKHR
                {
                    SType = StructureType.PipelineBinaryKeyKhr
                };
                nuint size = 0;
                result = _context.KhrPipelineBinary.GetPipelineBinaryData(
                    _context.Device,
                    &dataInfo,
                    &binaryKey,
                    &size,
                    null);
                if (result != Result.Success || size == 0 ||
                    size > PipelineBinaryStore.MaximumWritableBytes)
                {
                    return;
                }
                capturedBytes = checked(capturedBytes + (long)size);
                if (capturedBytes >
                    PipelineBinaryStore.MaximumWritableBytes)
                {
                    return;
                }

                byte[] payload = new byte[checked((int)size)];
                fixed (byte* payloadPointer = payload)
                {
                    result = _context.KhrPipelineBinary
                        .GetPipelineBinaryData(
                            _context.Device,
                            &dataInfo,
                            &binaryKey,
                            &size,
                            payloadPointer);
                }
                byte[] key = CopyPipelineBinaryKey(binaryKey);
                if (result != Result.Success || key.Length == 0)
                    return;
                if (size != checked((nuint)payload.Length))
                    Array.Resize(ref payload, checked((int)size));
                binaries.Add(new PipelineBinaryBlob(key, payload));
            }

            QueuePipelineBinarySave(
                artifactId,
                pipelineKey,
                binaries);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or OverflowException or
                CryptographicException)
        {
            lock (_gate)
            {
                _loadStatus =
                    $"Pipeline binary capture skipped: {exception.Message}";
            }
        }
        finally
        {
            DestroyPipelineBinaryHandles(handles);
            if (capturedPipeline.Handle != 0 &&
                _context.KhrPipelineBinary != null)
            {
                var releaseInfo = new ReleaseCapturedPipelineDataInfoKHR
                {
                    SType = StructureType
                        .ReleaseCapturedPipelineDataInfoKhr,
                    Pipeline = capturedPipeline
                };
                Result releaseResult = _context.KhrPipelineBinary
                    .ReleaseCapturedPipelineData(
                        _context.Device,
                        &releaseInfo,
                        null);
                if (releaseResult != Result.Success)
                {
                    lock (_gate)
                    {
                        _loadStatus =
                            "Pipeline binary captured-data release failed " +
                            $"({releaseResult}).";
                    }
                }
            }
        }
    }

    private void QueuePipelineBinarySave(
        PipelineArtifactId artifactId,
        byte[] pipelineKey,
        IReadOnlyList<PipelineBinaryBlob> binaries)
    {
        PipelineBinaryStore? store = _pipelineBinaryStore;
        if (store == null || binaries.Count == 0)
            return;

        Task task = Task.Run(() =>
        {
            try
            {
                store.Save(artifactId, pipelineKey, binaries);
                lock (_gate)
                {
                    _capturedPipelineBinaryCount = SaturatingAdd(
                        _capturedPipelineBinaryCount,
                        checked((ulong)binaries.Count));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    ArgumentException or OverflowException or
                    CryptographicException or
                    JsonException)
            {
                lock (_gate)
                {
                    _loadStatus =
                        $"Pipeline binary save skipped: {exception.Message}";
                }
            }
        });
        lock (_gate)
        {
            _binaryPersistTasks.RemoveAll(candidate => candidate.IsCompleted);
            _binaryPersistTasks.Add(task);
        }
    }

    private void RecordBinaryHit(PipelineArtifactSource source)
    {
        lock (_gate)
        {
            if (source == PipelineArtifactSource.WritableBinary)
            {
                _writableBinaryHitCount = SaturatingIncrement(
                    _writableBinaryHitCount);
            }
            else if (source == PipelineArtifactSource.SeedBinary)
            {
                _seedBinaryHitCount = SaturatingIncrement(
                    _seedBinaryHitCount);
            }
        }
    }

    private void DestroyPipelineBinaryHandles(
        IEnumerable<PipelineBinaryKHR> handles)
    {
        if (_context.KhrPipelineBinary == null)
            return;
        foreach (PipelineBinaryKHR handle in handles)
        {
            if (handle.Handle != 0)
            {
                _context.KhrPipelineBinary.DestroyPipelineBinary(
                    _context.Device,
                    handle,
                    null);
            }
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
            if (!_dirty)
                return true;
        }

        try
        {
            byte[] payload;
            ulong serializedGeneration;
            _cacheAccess.EnterWriteLock();
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

                payload = new byte[checked((int)size)];
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
                lock (_gate)
                    serializedGeneration = _cacheMutationGeneration;
            }
            finally
            {
                _cacheAccess.ExitWriteLock();
            }

            WriteAtomically(_cachePath, _identity, payload);
            lock (_gate)
            {
                _cacheSaved = true;
                _savedPayloadBytes = checked((ulong)payload.Length);
                if (_cacheMutationGeneration == serializedGeneration)
                    _dirty = false;
                _loadStatus = _cacheLoaded
                    ? HasLoadedProvenanceMismatch
                        ? "Compatible cache with stale or legacy provenance loaded and refreshed."
                        : "Compatible cache loaded and refreshed."
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

    /// <summary>
    /// Schedules cache serialization after a successful present so disk I/O
    /// and hashing are not part of time-to-first-frame. Repeated requests
    /// coalesce, while a mutation observed during a snapshot triggers another
    /// debounced save.
    /// </summary>
    public void SchedulePersist()
    {
        lock (_gate)
        {
            if (_disposed || _disposeStarted || !_dirty)
                return;
            if (_scheduledPersistTask is { IsCompleted: false })
                return;
            _scheduledPersistTask = Task.Run(() =>
            {
                bool saved = true;
                do
                {
                    Thread.Sleep(250);
                    saved &= Persist();
                    lock (_gate)
                    {
                        if (_disposeStarted || !_dirty || !saved)
                            return saved;
                    }
                } while (true);
            });
        }
    }

    private byte[] TryLoadCompatiblePayload()
    {
        bool runtimePresent = File.Exists(_cachePath);
        if (runtimePresent && TryLoadCompatiblePayloadFromPath(
                _cachePath,
                "Writable pipeline cache",
                runtimeCache: true,
                out byte[] runtimePayload))
        {
            return runtimePayload;
        }

        bool distinctSeed = !string.Equals(
            Path.GetFullPath(_cachePath),
            Path.GetFullPath(_seedCachePath),
            StringComparison.OrdinalIgnoreCase);
        bool seedPresent = distinctSeed && File.Exists(_seedCachePath);
        if (seedPresent && TryLoadCompatiblePayloadFromPath(
                _seedCachePath,
                "Read-only pipeline cache seed",
                runtimeCache: false,
                out byte[] seedPayload))
        {
            return seedPayload;
        }

        if (!runtimePresent && !seedPresent)
        {
            _loadStatus =
                "No writable pipeline cache or compatible read-only seed was present.";
        }
        return Array.Empty<byte>();
    }

    private bool TryLoadCompatiblePayloadFromPath(
        string path,
        string source,
        bool runtimeCache,
        out byte[] payload)
    {
        payload = Array.Empty<byte>();
        try
        {
            using FileStream? cacheLock = runtimeCache
                ? AcquireFileLock(path)
                : null;
            if (runtimeCache)
                CleanupOrphanedTemporaryFiles(path);

            var info = new FileInfo(path);
            if (info.Length < GiPipelineCacheFileCodec.MinimumHeaderSize ||
                info.Length > GiPipelineCacheFileCodec.HeaderSize +
                    (long)GiPipelineCacheFileCodec.MaximumPayloadBytes)
            {
                _cacheRejected = true;
                _loadStatus =
                    $"{source}: file length is outside the admitted range.";
                return false;
            }

            using var sourceStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            if (!GiPipelineCacheFileCodec.TryRead(
                    sourceStream,
                    _identity,
                    out payload,
                    out bool shaderBundleChanged,
                    out bool buildConfigurationChanged,
                    out bool legacyEnvelopeLoaded,
                    out string reason))
            {
                _cacheRejected = true;
                _loadStatus = $"{source}: {reason}";
                payload = Array.Empty<byte>();
                if (runtimeCache)
                    QuarantineRejectedWritableCache(path);
                return false;
            }

            _cacheLoaded = true;
            _runtimeCacheLoaded = runtimeCache;
            _seedCacheLoaded = !runtimeCache;
            _loadedFromDifferentShaderBundle = shaderBundleChanged;
            _loadedFromDifferentBuildConfiguration =
                buildConfigurationChanged;
            _legacyEnvelopeLoaded = legacyEnvelopeLoaded;
            _loadedPayloadBytes = checked((ulong)payload.Length);
            _loadStatus = $"{source}: {reason}";
            _dirty = HasLoadedProvenanceMismatch;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                ArgumentException or CryptographicException)
        {
            _cacheRejected = true;
            _loadStatus = $"{source}: load skipped: {ex.Message}";
            payload = Array.Empty<byte>();
            return false;
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
        string shaderBundleHash,
        string compileConfiguration)
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
            SHA256.HashData(Encoding.UTF8.GetBytes(EngineAbi)),
            SHA256.HashData(Encoding.UTF8.GetBytes(
                NormalizeCompileConfiguration(compileConfiguration))));
    }

    private PipelineBinaryStore? TryCreatePipelineBinaryStore()
    {
        RendererPipelineBinaryCacheMode mode =
            RendererBuildConfiguration.PipelineBinaryCacheMode;
        if (mode == RendererPipelineBinaryCacheMode.Off)
            return null;

        if (!_context.PipelineOptimizationSupport.PipelineBinary ||
            _context.KhrPipelineBinary == null)
        {
            if (mode == RendererPipelineBinaryCacheMode.Require)
            {
                throw new InvalidOperationException(
                    "Pipeline-binary verification was requested, but " +
                    "VK_KHR_pipeline_binary is unavailable.");
            }
            return null;
        }

        var globalKey = new PipelineBinaryKeyKHR
        {
            SType = StructureType.PipelineBinaryKeyKhr
        };
        Result result = _context.KhrPipelineBinary.GetPipelineKey(
            _context.Device,
            (PipelineCreateInfoKHR*)null,
            &globalKey);
        byte[] globalKeyBytes = CopyPipelineBinaryKey(globalKey);
        if (result != Result.Success || globalKeyBytes.Length == 0)
        {
            if (mode == RendererPipelineBinaryCacheMode.Require)
            {
                throw new VulkanException(
                    "Failed to query the required pipeline-binary global key",
                    result);
            }
            return null;
        }

        string? configuredRoot = Environment.GetEnvironmentVariable(
            "NJULF_PIPELINE_BINARY_CACHE_DIRECTORY");
        string? configuredSeedRoot = Environment.GetEnvironmentVariable(
            "NJULF_PIPELINE_BINARY_SEED_DIRECTORY");
        return new PipelineBinaryStore(
            new PipelineBinaryStoreIdentity(
                _identity.VendorId,
                _identity.DeviceId,
                _identity.DriverVersion,
                _identity.ApiVersion,
                Convert.ToHexString(globalKeyBytes),
                Convert.ToHexString(_identity.ShaderBundleHash),
                Convert.ToHexString(_identity.EngineAbiHash),
                Convert.ToHexString(_identity.BuildConfigurationHash)),
            configuredRoot,
            configuredSeedRoot);
    }

    private static byte[] CopyPipelineBinaryKey(
        PipelineBinaryKeyKHR key)
    {
        if (key.KeySize is 0 or > 32)
            return Array.Empty<byte>();
        byte[] result = new byte[key.KeySize];
        fixed (byte* destination = result)
        {
            System.Buffer.MemoryCopy(
                key.Key,
                destination,
                result.Length,
                result.Length);
        }
        return result;
    }

    private bool HasLoadedProvenanceMismatch =>
        _loadedFromDifferentShaderBundle ||
        _loadedFromDifferentBuildConfiguration ||
        _legacyEnvelopeLoaded;

    private static string NormalizeCompileConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static void WriteAtomically(
        string path,
        GiPipelineCacheIdentity identity,
        ReadOnlySpan<byte> payload)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Pipeline cache path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        using FileStream cacheLock = AcquireFileLock(fullPath);
        CleanupOrphanedTemporaryFiles(fullPath);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 128 * 1024,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                GiPipelineCacheFileCodec.Write(
                    destination,
                    identity,
                    payload);
                destination.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static FileStream AcquireFileLock(string cachePath)
    {
        string fullPath = Path.GetFullPath(cachePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Pipeline cache path has no directory.", nameof(cachePath));
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.lock");
        IOException? lastFailure = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                Thread.Sleep(25);
            }
        }

        throw new IOException(
            $"Timed out acquiring pipeline cache lock '{lockPath}'.",
            lastFailure);
    }

    private static void CleanupOrphanedTemporaryFiles(string cachePath)
    {
        string fullPath = Path.GetFullPath(cachePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        string pattern = $".{Path.GetFileName(fullPath)}.*.tmp";
        foreach (string candidate in Directory.EnumerateFiles(directory, pattern))
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // A live writer owns this candidate; its process lock protects
                // the file selected by the active save transaction.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort and must not make cache loading fatal.
            }
        }
    }

    private static void QuarantineRejectedWritableCache(string cachePath)
    {
        if (!File.Exists(cachePath))
            return;

        string quarantinePath =
            $"{cachePath}.rejected-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        try
        {
            File.Move(cachePath, quarantinePath);
        }
        catch (IOException)
        {
            // Rejection is already reported; quarantine is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Rejection is already reported; quarantine is best-effort.
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

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        left > ulong.MaxValue - right ? ulong.MaxValue : left + right;

    public void Dispose()
    {
        Task<bool>? scheduledPersistTask;
        lock (_gate)
        {
            if (_disposed || _disposeStarted)
                return;
            _disposeStarted = true;
            scheduledPersistTask = _scheduledPersistTask;
        }
        if (scheduledPersistTask != null)
        {
            try
            {
                scheduledPersistTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                lock (_gate)
                    _loadStatus =
                        $"Scheduled pipeline cache save failed: {exception.Message}";
            }
        }
        try
        {
            _compilationScheduler.Dispose();
        }
        catch (Exception exception)
        {
            lock (_gate)
                _loadStatus =
                    $"Pipeline compiler shutdown reported: {exception.Message}";
        }
        Task[] binaryPersistTasks;
        lock (_gate)
            binaryPersistTasks = _binaryPersistTasks.ToArray();
        try
        {
            Task.WhenAll(binaryPersistTasks).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            lock (_gate)
                _loadStatus =
                    $"Pipeline binary persistence reported: {exception.Message}";
        }
        Persist();
        _cacheAccess.EnterWriteLock();
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (_cache.Handle != 0)
                    _context.Api.DestroyPipelineCache(
                        _context.Device,
                        _cache,
                        null);
                _cache = default;
                _disposed = true;
            }
        }
        finally
        {
            _cacheAccess.ExitWriteLock();
            _cacheAccess.Dispose();
        }
    }
}
