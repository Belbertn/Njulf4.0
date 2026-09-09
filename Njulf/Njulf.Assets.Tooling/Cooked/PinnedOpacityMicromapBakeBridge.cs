using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Njulf.Assets.Cooked;

public sealed record PinnedOpacityMicromapBakeBridgeOptions
{
    public const uint CurrentBridgeAbi = 1U;

    public required string LibraryPath { get; init; }
    public required OpacityMicromapSdkProvenance Provenance { get; init; }
    public required string ExpectedSdkVersion { get; init; }
    public int MaximumInputBytes { get; init; } = 512 * 1024 * 1024;
    public int MaximumOutputBytes { get; init; } = 512 * 1024 * 1024;
    public int MaximumPrimitiveCount { get; init; } = 16_777_216;

    public bool TryValidate(out string detail)
    {
        if (string.IsNullOrWhiteSpace(LibraryPath) || !Path.IsPathFullyQualified(LibraryPath) ||
            !File.Exists(LibraryPath))
        {
            detail = "omm-pinned-bridge-library-path-invalid";
            return false;
        }
        if (!Provenance.TryValidate(out detail))
            return false;
        if (string.IsNullOrWhiteSpace(ExpectedSdkVersion) ||
            ExpectedSdkVersion.Length > 64 ||
            !ExpectedSdkVersion.All(static value =>
                char.IsAsciiDigit(value) || value == '.'))
        {
            detail = "omm-pinned-bridge-sdk-version-invalid";
            return false;
        }
        if (MaximumInputBytes <= 0 || MaximumOutputBytes <= 0 ||
            MaximumPrimitiveCount <= 0)
        {
            detail = "omm-pinned-bridge-bounds-invalid";
            return false;
        }

        detail = "omm-pinned-bridge-options-valid";
        return true;
    }
}

/// <summary>Frozen native C ABI sizes for the supported 64-bit AssetTool.</summary>
public static class PinnedOpacityMicromapBridgeAbi
{
    public const uint Version = 1U;
    public const int BridgeInfoBytes64 = 32;
    public const int BakeRequestBytes64 = 120;
    public const int UsageBytes = 8;
    public const int ResultViewBytes64 = 312;
}

/// <summary>
/// Strict manifest consumed by the AssetTool before any native library is
/// loaded. The binary digest is hexadecimal because the content-key value type
/// intentionally does not expose serializer-visible mutable bytes.
/// </summary>
public sealed record OpacityMicromapBridgeProvenanceManifest
{
    public const uint CurrentSchemaVersion = 1U;
    public const int MaximumManifestBytes = 64 * 1024;

    public uint SchemaVersion { get; init; } = CurrentSchemaVersion;
    public uint BridgeAbi { get; init; } =
        PinnedOpacityMicromapBakeBridgeOptions.CurrentBridgeAbi;
    public string SourceUri { get; init; } = string.Empty;
    public string CommitOrRelease { get; init; } = string.Empty;
    public string LicenseIdentifier { get; init; } = string.Empty;
    public string BuildFlags { get; init; } = string.Empty;
    public string CompilerIdentity { get; init; } = string.Empty;
    public string BinarySha256 { get; init; } = string.Empty;
    public string SdkVersion { get; init; } = string.Empty;
    public int MaximumInputBytes { get; init; } = 512 * 1024 * 1024;
    public int MaximumOutputBytes { get; init; } = 512 * 1024 * 1024;
    public int MaximumPrimitiveCount { get; init; } = 16_777_216;

    public static PinnedOpacityMicromapBakeBridgeOptions LoadOptions(
        string manifestPath,
        string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        string absoluteManifest = Path.GetFullPath(manifestPath);
        string absoluteLibrary = Path.GetFullPath(libraryPath);
        var info = new FileInfo(absoluteManifest);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "Opacity-micromap provenance manifest is missing, empty, or oversized.");
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 16
        };
        OpacityMicromapBridgeProvenanceManifest manifest;
        using (FileStream stream = new(
                   absoluteManifest,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 16 * 1024,
                   FileOptions.SequentialScan))
        {
            manifest = JsonSerializer.Deserialize<OpacityMicromapBridgeProvenanceManifest>(
                stream,
                jsonOptions) ?? throw new InvalidDataException(
                "Opacity-micromap provenance manifest deserialized to null.");
        }

        if (manifest.SchemaVersion != CurrentSchemaVersion ||
            manifest.BridgeAbi != PinnedOpacityMicromapBakeBridgeOptions.CurrentBridgeAbi)
        {
            throw new InvalidDataException(
                "Opacity-micromap provenance manifest schema or bridge ABI is unsupported.");
        }
        byte[] digest;
        try
        {
            digest = Convert.FromHexString(manifest.BinarySha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Opacity-micromap provenance binary SHA-256 is not hexadecimal.",
                exception);
        }
        if (digest.Length != OpacityMicromapContentKey.ByteLength)
        {
            throw new InvalidDataException(
                "Opacity-micromap provenance binary SHA-256 must contain 32 bytes.");
        }
        var provenance = new OpacityMicromapSdkProvenance(
            manifest.SourceUri,
            manifest.CommitOrRelease,
            manifest.LicenseIdentifier,
            manifest.BuildFlags,
            manifest.CompilerIdentity,
            OpacityMicromapContentKey.FromSha256(digest));
        var options = new PinnedOpacityMicromapBakeBridgeOptions
        {
            LibraryPath = absoluteLibrary,
            Provenance = provenance,
            ExpectedSdkVersion = manifest.SdkVersion,
            MaximumInputBytes = manifest.MaximumInputBytes,
            MaximumOutputBytes = manifest.MaximumOutputBytes,
            MaximumPrimitiveCount = manifest.MaximumPrimitiveCount
        };
        if (!options.TryValidate(out string detail))
            throw new InvalidDataException(detail);
        return options;
    }
}

/// <summary>
/// SHA-authenticated loader for the versioned native CPU-baker bridge. Native
/// output remains borrowed until copied and validated into an immutable cooked
/// payload; every handle is released in a finally block.
/// </summary>
public sealed unsafe class PinnedOpacityMicromapBakeBridge :
    IOpacityMicromapBakeBridge,
    IDisposable
{
    private readonly object _gate = new();
    private readonly nint _library;
    private readonly GetBridgeInfoDelegate _getBridgeInfo;
    private readonly BakeDelegate _bake;
    private readonly GetResultViewDelegate _getResultView;
    private readonly DestroyResultDelegate _destroyResult;
    private bool _disposed;

    public PinnedOpacityMicromapBakeBridge(
        PinnedOpacityMicromapBakeBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TryValidate(out string optionsDetail))
            throw new ArgumentException(optionsDetail, nameof(options));
        VerifyManagedAbi();

        OpacityMicromapContentKey actualHash;
        using (FileStream stream = new(
                   options.LibraryPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 128 * 1024,
                   FileOptions.SequentialScan))
        {
            actualHash = OpacityMicromapContentKey.FromSha256(
                SHA256.HashData(stream));
        }
        if (actualHash != options.Provenance.BinarySha256)
        {
            throw new InvalidDataException(
                "Pinned opacity-micromap bridge binary SHA-256 does not match its provenance manifest.");
        }

        nint library = NativeLibrary.Load(options.LibraryPath);
        try
        {
            _getBridgeInfo = Load<GetBridgeInfoDelegate>(library, "njulf_omm_get_bridge_info");
            _bake = Load<BakeDelegate>(library, "njulf_omm_bake");
            _getResultView = Load<GetResultViewDelegate>(library, "njulf_omm_get_result_view");
            _destroyResult = Load<DestroyResultDelegate>(library, "njulf_omm_destroy_result");
            var info = new NativeBridgeInfo { StructSize = (uint)sizeof(NativeBridgeInfo) };
            NativeStatus status = _getBridgeInfo(&info);
            if (status != NativeStatus.Success ||
                info.BridgeAbi != PinnedOpacityMicromapBakeBridgeOptions.CurrentBridgeAbi)
            {
                throw new InvalidDataException(
                    "Pinned opacity-micromap bridge reported an unsupported ABI.");
            }
            SdkVersion = $"{info.SdkVersionMajor}.{info.SdkVersionMinor}.{info.SdkVersionBuild}";
            if (!string.Equals(SdkVersion, options.ExpectedSdkVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Pinned opacity-micromap SDK version is {SdkVersion}, expected {options.ExpectedSdkVersion}.");
            }

            _library = library;
            Contract = new OpacityMicromapBakeBridgeContract(
                info.BridgeAbi,
                options.Provenance,
                options.MaximumInputBytes,
                options.MaximumOutputBytes,
                options.MaximumPrimitiveCount);
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    public OpacityMicromapBakeBridgeContract Contract { get; }
    public string SdkVersion { get; }

    public ValueTask<OpacityMicromapBakeResult> BakeAsync(
        OpacityMicromapBakeRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.TryValidate(Contract, out OpacityMicromapBakeFailure failure,
                out string detail))
        {
            return ValueTask.FromResult(
                OpacityMicromapBakeResult.Rejected(failure, detail));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(OpacityMicromapBakeResult.Rejected(
                OpacityMicromapBakeFailure.Cancelled,
                "omm-pinned-bridge-cancelled-before-invocation"));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ValueTask.FromResult(BakeCore(request, cancellationToken));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            NativeLibrary.Free(_library);
        }
        GC.SuppressFinalize(this);
    }

    private OpacityMicromapBakeResult BakeCore(
        in OpacityMicromapBakeRequest request,
        CancellationToken cancellationToken)
    {
        nint cancellationMemory = Marshal.AllocHGlobal(sizeof(uint));
        Marshal.WriteInt32(cancellationMemory, 0);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => Marshal.WriteInt32((nint)state!, 1),
            cancellationMemory);
        nint resultHandle = 0;
        try
        {
            fixed (byte* indexBytes = request.IndexBytes.Span)
            fixed (byte* uvBytes = request.UvBytes.Span)
            fixed (byte* alphaBytes = request.AlphaTextureBytes.Span)
            {
                var nativeRequest = new NativeBakeRequest
                {
                    StructSize = (uint)sizeof(NativeBakeRequest),
                    BridgeAbi = Contract.BridgeAbi,
                    AlphaFp32 = (float*)alphaBytes,
                    AlphaValueCount = checked((ulong)request.AlphaTextureBytes.Length / sizeof(float)),
                    TextureWidth = request.TextureWidth,
                    TextureHeight = request.TextureHeight,
                    Uv32 = (float*)uvBytes,
                    UvFloatCount = checked((ulong)request.UvBytes.Length / sizeof(float)),
                    VertexCount = request.VertexCount,
                    Indices = (uint*)indexBytes,
                    IndexCount = checked((ulong)request.IndexBytes.Length / sizeof(uint)),
                    PrimitiveCount = request.PrimitiveCount,
                    SubdivisionLevel = request.RequestedSubdivisionLevel,
                    AddressMode = request.MaterialContract.Sampler.AddressModeU,
                    Filter = request.MaterialContract.Sampler.MagFilter,
                    AlphaCutoffInclusive = BitConverter.Int32BitsToSingle(
                        unchecked((int)request.MaterialContract.AlphaCutoffBits)),
                    MaximumArrayDataBytes = request.MaximumArrayDataBytes,
                    MaximumTotalOutputBytes = checked((ulong)Contract.MaximumOutputBytes),
                    MaximumWorkloadSize = request.MaximumWorkloadSize,
                    CancellationFlag = (uint*)cancellationMemory
                };
                NativeStatus status = _bake(&nativeRequest, &resultHandle);
                if (status != NativeStatus.Success || resultHandle == 0)
                    return Rejected(status, cancellationToken.IsCancellationRequested);
            }

            var view = new NativeResultView
            {
                StructSize = (uint)sizeof(NativeResultView)
            };
            NativeStatus viewStatus = _getResultView(resultHandle, &view);
            string detail = string.Empty;
            if (viewStatus != NativeStatus.Success ||
                !TryValidateView(request, view, out detail))
            {
                return OpacityMicromapBakeResult.Rejected(
                    OpacityMicromapBakeFailure.OutputRejected,
                    viewStatus == NativeStatus.Success
                        ? detail
                        : "omm-pinned-bridge-result-view-failed");
            }

            byte[] ommData = Copy(view.ArrayData, view.ArrayDataBytes);
            byte[] descriptorData = Copy(view.DescriptorData, view.DescriptorDataBytes);
            byte[] indexData = Copy(view.IndexData, view.IndexDataBytes);
            var usage = new OpacityMicromapUsage[view.DescriptorUsageCount];
            for (int i = 0; i < usage.Length; i++)
            {
                NativeUsage native = view.DescriptorUsage[i];
                if (native.Count == 0U || native.Format != 2U)
                {
                    return OpacityMicromapBakeResult.Rejected(
                        OpacityMicromapBakeFailure.OutputRejected,
                        "omm-pinned-bridge-usage-entry-invalid");
                }
                usage[i] = new OpacityMicromapUsage(
                    OpacityMicromapFormat.FourState,
                    native.SubdivisionLevel,
                    native.Count);
            }

            OpacityMicromapCookedPayload payload;
            try
            {
                payload = OpacityMicromapCookedPayload.Create(
                    NvidiaOpacityMicromapCookPolicy.CurrentCookAbi,
                    request.ContentKey,
                    Contract.Provenance.ComputeFingerprint(),
                    request.RequestedSubdivisionLevel,
                    request.PrimitiveCount,
                    view.DescriptorCount,
                    [request.MaterialContract],
                    usage,
                    ommData,
                    indexData,
                    descriptorData,
                    new OpacityMicromapClassificationStatistics(
                        view.OpaqueCount,
                        view.TransparentCount,
                        view.UnknownOpaqueCount,
                        view.UnknownTransparentCount));
            }
            catch (Exception exception) when (exception is ArgumentException or
                                               InvalidOperationException or
                                               OverflowException)
            {
                return OpacityMicromapBakeResult.Rejected(
                    OpacityMicromapBakeFailure.OutputRejected,
                    "omm-pinned-bridge-immutable-payload-validation-failed");
            }

            return new OpacityMicromapBakeResult(
                true,
                payload,
                OpacityMicromapBakeFailure.None,
                ReadDetail(view.Detail));
        }
        finally
        {
            if (resultHandle != 0)
                _destroyResult(resultHandle);
            Marshal.FreeHGlobal(cancellationMemory);
        }
    }

    private bool TryValidateView(
        in OpacityMicromapBakeRequest request,
        in NativeResultView view,
        out string detail)
    {
        ulong totalBytes;
        try
        {
            totalBytes = checked(view.ArrayDataBytes + view.DescriptorDataBytes +
                view.IndexDataBytes +
                checked((ulong)view.DescriptorUsageCount * (ulong)sizeof(NativeUsage)));
        }
        catch (OverflowException)
        {
            detail = "omm-pinned-bridge-output-size-overflow";
            return false;
        }
        if (view.BridgeAbi != Contract.BridgeAbi ||
            view.ArrayData == null || view.DescriptorData == null || view.IndexData == null ||
            view.DescriptorUsage == null || view.ArrayDataBytes == 0UL ||
            view.DescriptorCount == 0U || view.DescriptorUsageCount == 0U ||
            view.DescriptorDataBytes != checked((ulong)view.DescriptorCount * 8UL) ||
            view.IndexCount != request.PrimitiveCount ||
            view.IndexDataBytes != checked((ulong)view.IndexCount * sizeof(uint)) ||
            totalBytes > checked((ulong)Contract.MaximumOutputBytes) ||
            view.ArrayDataBytes > request.MaximumArrayDataBytes ||
            view.ArrayDataBytes > int.MaxValue || view.DescriptorDataBytes > int.MaxValue ||
            view.IndexDataBytes > int.MaxValue || view.DescriptorUsageCount > 4096U)
        {
            detail = "omm-pinned-bridge-output-bounds-invalid";
            return false;
        }

        detail = "omm-pinned-bridge-output-valid";
        return true;
    }

    private static byte[] Copy(byte* source, ulong bytes)
    {
        byte[] destination = new byte[checked((int)bytes)];
        new ReadOnlySpan<byte>(source, destination.Length).CopyTo(destination);
        return destination;
    }

    private static OpacityMicromapBakeResult Rejected(
        NativeStatus status,
        bool cancellationRequested)
    {
        if (status == NativeStatus.Cancelled || cancellationRequested)
        {
            return OpacityMicromapBakeResult.Rejected(
                OpacityMicromapBakeFailure.Cancelled,
                "omm-pinned-bridge-cancelled");
        }
        return OpacityMicromapBakeResult.Rejected(
            status == NativeStatus.InvalidArgument
                ? OpacityMicromapBakeFailure.RequestInvalid
                : status == NativeStatus.OutputInvalid
                    ? OpacityMicromapBakeFailure.OutputRejected
                    : OpacityMicromapBakeFailure.NativeFailure,
            "omm-pinned-bridge-native-status-" + (uint)status);
    }

    private static string ReadDetail(byte* detail)
    {
        int length = 0;
        while (length < 192 && detail[length] != 0)
            length++;
        return length == 0
            ? "omm-pinned-bridge-bake-complete"
            : System.Text.Encoding.UTF8.GetString(detail, length);
    }

    private static T Load<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private enum NativeStatus : uint
    {
        Success = 0,
        InvalidArgument = 1,
        Cancelled = 2,
        WorkloadTooLarge = 3,
        SdkFailure = 4,
        OutputInvalid = 5,
        OutOfMemory = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBridgeInfo
    {
        public uint StructSize;
        public uint BridgeAbi;
        public uint SdkVersionMajor;
        public uint SdkVersionMinor;
        public uint SdkVersionBuild;
        public fixed uint Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBakeRequest
    {
        public uint StructSize;
        public uint BridgeAbi;
        public float* AlphaFp32;
        public ulong AlphaValueCount;
        public uint TextureWidth;
        public uint TextureHeight;
        public float* Uv32;
        public ulong UvFloatCount;
        public uint VertexCount;
        public uint* Indices;
        public ulong IndexCount;
        public uint PrimitiveCount;
        public uint SubdivisionLevel;
        public uint AddressMode;
        public uint Filter;
        public float AlphaCutoffInclusive;
        public uint MaximumArrayDataBytes;
        public ulong MaximumTotalOutputBytes;
        public ulong MaximumWorkloadSize;
        public uint* CancellationFlag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUsage
    {
        public uint Count;
        public ushort SubdivisionLevel;
        public ushort Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResultView
    {
        public uint StructSize;
        public uint BridgeAbi;
        public byte* ArrayData;
        public ulong ArrayDataBytes;
        public byte* DescriptorData;
        public ulong DescriptorDataBytes;
        public uint DescriptorCount;
        public byte* IndexData;
        public ulong IndexDataBytes;
        public uint IndexCount;
        public NativeUsage* DescriptorUsage;
        public uint DescriptorUsageCount;
        public ulong OpaqueCount;
        public ulong TransparentCount;
        public ulong UnknownOpaqueCount;
        public ulong UnknownTransparentCount;
        public fixed byte Detail[192];
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeStatus GetBridgeInfoDelegate(NativeBridgeInfo* info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeStatus BakeDelegate(
        NativeBakeRequest* request,
        nint* result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeStatus GetResultViewDelegate(
        nint result,
        NativeResultView* view);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyResultDelegate(nint result);

    private static void VerifyManagedAbi()
    {
        if (IntPtr.Size != sizeof(ulong))
        {
            throw new PlatformNotSupportedException(
                "The pinned opacity-micromap AssetTool bridge supports only 64-bit processes.");
        }
        if (sizeof(NativeBridgeInfo) != PinnedOpacityMicromapBridgeAbi.BridgeInfoBytes64 ||
            sizeof(NativeBakeRequest) != PinnedOpacityMicromapBridgeAbi.BakeRequestBytes64 ||
            sizeof(NativeUsage) != PinnedOpacityMicromapBridgeAbi.UsageBytes ||
            sizeof(NativeResultView) != PinnedOpacityMicromapBridgeAbi.ResultViewBytes64)
        {
            throw new TypeLoadException(
                "Managed opacity-micromap bridge structures do not match the frozen C ABI.");
        }
    }
}
