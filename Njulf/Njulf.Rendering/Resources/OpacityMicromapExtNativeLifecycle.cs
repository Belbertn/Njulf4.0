using System.Buffers.Binary;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Core;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

/// <summary>
/// The explicit binary-to-EXT binding for one four-state payload.  The cooked
/// payload intentionally remains backend-neutral enough to be validated and
/// inspected without loading Vulkan.  Before its bytes are used as a Vulkan
/// <see cref="MicromapTriangleEXT"/> array or an
/// <see cref="AccelerationStructureTrianglesOpacityMicromapEXT"/> index
/// stream, this binding pins the otherwise implicit ABI.
/// </summary>
/// <remarks>
/// This is deliberately a strict first profile.  Padded triangle records,
/// non-zero base-triangle indirection, and custom index strides require a
/// revisioned cooked schema; accepting them here would silently reinterpret
/// opaque bytes from older payloads.
/// </remarks>
public readonly record struct OpacityMicromapExtNativeInputLayout(
    uint LayoutRevision,
    IndexType PerPrimitiveIndexType,
    ulong PerPrimitiveIndexStride,
    uint BaseTriangle,
    ulong TriangleArrayStride)
{
    public const uint CurrentLayoutRevision = 1U;
    public const ulong VulkanMicromapTriangleBytes = 8UL;

    public static OpacityMicromapExtNativeInputLayout PackedUint32 { get; } = new(
        LayoutRevision: CurrentLayoutRevision,
        PerPrimitiveIndexType: IndexType.Uint32,
        PerPrimitiveIndexStride: sizeof(uint),
        BaseTriangle: 0U,
        TriangleArrayStride: VulkanMicromapTriangleBytes);

    /// <summary>
    /// Validates every byte interpretation used by the native build path.  In
    /// particular, the sum of usage counts describes the descriptor array,
    /// while the per-primitive index stream maps geometry triangles to that
    /// array (or to the Vulkan special-index sentinels).
    /// </summary>
    public bool TryValidate(
        OpacityMicromapCookedPayload? payload,
        out string detail)
    {
        if (payload is null ||
            payload.PayloadKind != OpacityMicromapPayloadKind.VulkanExtFourState ||
            payload.Format != OpacityMicromapFormat.FourState)
        {
            detail = "native-omm-layout-payload-not-vulkan-ext-four-state";
            return false;
        }
        if (LayoutRevision != CurrentLayoutRevision)
        {
            detail = "native-omm-layout-revision-unsupported";
            return false;
        }
        if (BaseTriangle != 0U)
        {
            detail = "native-omm-layout-nonzero-base-triangle-not-supported";
            return false;
        }
        if (TriangleArrayStride != VulkanMicromapTriangleBytes)
        {
            detail = "native-omm-layout-triangle-array-stride-not-vulkan-micromap-triangle";
            return false;
        }

        int indexBytes;
        switch (PerPrimitiveIndexType)
        {
            case IndexType.Uint16:
                indexBytes = sizeof(ushort);
                break;
            case IndexType.Uint32:
                indexBytes = sizeof(uint);
                break;
            default:
                detail = "native-omm-layout-index-type-not-uint16-or-uint32";
                return false;
        }
        if (PerPrimitiveIndexStride != (ulong)indexBytes)
        {
            detail = "native-omm-layout-index-stride-not-tightly-packed";
            return false;
        }

        ulong expectedDescriptorBytes;
        ulong expectedIndexBytes;
        try
        {
            expectedDescriptorBytes = checked(
                (ulong)payload.DescriptorCount * TriangleArrayStride);
            expectedIndexBytes = checked(
                (ulong)payload.PrimitiveCount * PerPrimitiveIndexStride);
        }
        catch (OverflowException)
        {
            detail = "native-omm-layout-byte-count-overflow";
            return false;
        }

        if ((ulong)payload.DescriptorData.Length != expectedDescriptorBytes)
        {
            detail = "native-omm-layout-descriptor-data-length-mismatch";
            return false;
        }
        if ((ulong)payload.IndexData.Length != expectedIndexBytes)
        {
            detail = "native-omm-layout-per-primitive-index-length-mismatch";
            return false;
        }
        if (!TryCreateNativeUsageCounts(payload, out MicromapUsageEXT[] usageCounts, out detail))
            return false;

        ulong usageTotal = 0UL;
        var expectedDescriptorUsageCounts =
            new Dictionary<uint, uint>(usageCounts.Length);
        foreach (MicromapUsageEXT usage in usageCounts)
        {
            try
            {
                usageTotal = checked(usageTotal + usage.Count);
            }
            catch (OverflowException)
            {
                detail = "native-omm-layout-usage-count-overflow";
                return false;
            }
            if (!expectedDescriptorUsageCounts.TryAdd(
                    usage.SubdivisionLevel,
                    usage.Count))
            {
                detail = "native-omm-layout-usage-histogram-contains-duplicate-subdivision";
                return false;
            }
        }
        if (usageTotal != payload.DescriptorCount)
        {
            detail = "native-omm-layout-usage-count-does-not-cover-descriptor-array";
            return false;
        }

        ReadOnlySpan<byte> descriptors = payload.DescriptorData.Span;
        var actualDescriptorUsageCounts =
            new Dictionary<uint, uint>(expectedDescriptorUsageCounts.Count);
        for (uint descriptorIndex = 0; descriptorIndex < payload.DescriptorCount; descriptorIndex++)
        {
            int offset = checked((int)((ulong)descriptorIndex * TriangleArrayStride));
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                descriptors.Slice(offset, sizeof(uint)));
            ushort subdivision = BinaryPrimitives.ReadUInt16LittleEndian(
                descriptors.Slice(offset + sizeof(uint), sizeof(ushort)));
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(
                descriptors.Slice(offset + sizeof(uint) + sizeof(ushort), sizeof(ushort)));
            if (!TryGetFourStateDataBytes(subdivision, out ulong descriptorDataBytes))
            {
                detail = "native-omm-layout-triangle-descriptor-subdivision-overflow";
                return false;
            }
            if (format != (ushort)OpacityMicromapFormatEXT.Format4StateExt ||
                subdivision > payload.MaximumSubdivisionLevel ||
                (ulong)dataOffset > (ulong)payload.OmmData.Length ||
                descriptorDataBytes >
                    (ulong)payload.OmmData.Length - (ulong)dataOffset)
            {
                detail = "native-omm-layout-triangle-descriptor-invalid";
                return false;
            }

            uint subdivisionKey = subdivision;
            actualDescriptorUsageCounts.TryGetValue(
                subdivisionKey,
                out uint existingCount);
            try
            {
                actualDescriptorUsageCounts[subdivisionKey] = checked(existingCount + 1U);
            }
            catch (OverflowException)
            {
                detail = "native-omm-layout-triangle-descriptor-count-overflow";
                return false;
            }
        }

        if (!UsageCountsMatch(
                expectedDescriptorUsageCounts,
                actualDescriptorUsageCounts))
        {
            detail = "native-omm-layout-usage-histogram-does-not-match-triangle-descriptors";
            return false;
        }

        ReadOnlySpan<byte> indices = payload.IndexData.Span;
        for (uint primitiveIndex = 0; primitiveIndex < payload.PrimitiveCount; primitiveIndex++)
        {
            int offset = checked((int)((ulong)primitiveIndex * PerPrimitiveIndexStride));
            uint descriptorIndex = PerPrimitiveIndexType == IndexType.Uint16
                ? BinaryPrimitives.ReadUInt16LittleEndian(indices.Slice(offset, sizeof(ushort)))
                : BinaryPrimitives.ReadUInt32LittleEndian(indices.Slice(offset, sizeof(uint)));
            if (descriptorIndex >= payload.DescriptorCount &&
                !IsSupportedSpecialIndex(descriptorIndex, PerPrimitiveIndexType))
            {
                detail = "native-omm-layout-per-primitive-index-out-of-range";
                return false;
            }
        }

        detail = "native-omm-layout-valid";
        return true;
    }

    /// <summary>
    /// Derives the usage-count table required by
    /// <see cref="AccelerationStructureTrianglesOpacityMicromapEXT"/> for the
    /// matching geometry.  This is intentionally distinct from the micromap
    /// build histogram: the geometry may reuse a descriptor, omit one, or use
    /// a Vulkan special index for a triangle.
    /// </summary>
    internal static bool TryCreateStaticBlasUsageCounts(
        OpacityMicromapCookedPayload? payload,
        in OpacityMicromapExtNativeInputLayout layout,
        out MicromapUsageEXT[] usageCounts,
        out string detail)
    {
        usageCounts = Array.Empty<MicromapUsageEXT>();
        if (!layout.TryValidate(payload, out detail))
        {
            detail = "native-omm-static-blas-attachment-layout-invalid-" + detail;
            return false;
        }

        OpacityMicromapCookedPayload validatedPayload = payload!;
        var geometryUsageCounts = new Dictionary<uint, uint>(
            validatedPayload.UsageHistogram.Count);
        ReadOnlySpan<byte> descriptors = validatedPayload.DescriptorData.Span;
        ReadOnlySpan<byte> indices = validatedPayload.IndexData.Span;
        for (uint primitiveIndex = 0;
             primitiveIndex < validatedPayload.PrimitiveCount;
             primitiveIndex++)
        {
            int indexOffset = checked((int)(
                (ulong)primitiveIndex * layout.PerPrimitiveIndexStride));
            uint descriptorIndex = layout.PerPrimitiveIndexType == IndexType.Uint16
                ? BinaryPrimitives.ReadUInt16LittleEndian(
                    indices.Slice(indexOffset, sizeof(ushort)))
                : BinaryPrimitives.ReadUInt32LittleEndian(
                    indices.Slice(indexOffset, sizeof(uint)));
            if (descriptorIndex >= validatedPayload.DescriptorCount)
            {
                // Validation above already proves that this is one of Vulkan's
                // special indices.  Special triangles have no micromap usage
                // entry in the AS attachment table.
                continue;
            }

            int descriptorOffset = checked((int)(
                (ulong)descriptorIndex * layout.TriangleArrayStride));
            uint subdivision = BinaryPrimitives.ReadUInt16LittleEndian(
                descriptors.Slice(
                    descriptorOffset + sizeof(uint),
                    sizeof(ushort)));
            geometryUsageCounts.TryGetValue(subdivision, out uint existingCount);
            try
            {
                geometryUsageCounts[subdivision] = checked(existingCount + 1U);
            }
            catch (OverflowException)
            {
                detail = "native-omm-static-blas-attachment-usage-count-overflow";
                return false;
            }
        }

        var result = new MicromapUsageEXT[validatedPayload.UsageHistogram.Count];
        int resultCount = 0;
        foreach (OpacityMicromapUsage histogramEntry in
                 validatedPayload.UsageHistogram)
        {
            if (!geometryUsageCounts.TryGetValue(
                    histogramEntry.SubdivisionLevel,
                    out uint count) ||
                count == 0U)
            {
                continue;
            }

            result[resultCount++] = new MicromapUsageEXT
            {
                Count = count,
                SubdivisionLevel = histogramEntry.SubdivisionLevel,
                Format = (uint)OpacityMicromapFormatEXT.Format4StateExt
            };
        }

        if (resultCount == 0)
        {
            detail = "native-omm-static-blas-attachment-has-no-micromap-referenced-triangles";
            return false;
        }

        if (resultCount != result.Length)
            Array.Resize(ref result, resultCount);
        usageCounts = result;
        detail = "native-omm-static-blas-attachment-usage-counts-derived";
        return true;
    }

    internal static bool TryCreateNativeUsageCounts(
        OpacityMicromapCookedPayload payload,
        out MicromapUsageEXT[] usageCounts,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.UsageHistogram.Count == 0)
        {
            usageCounts = Array.Empty<MicromapUsageEXT>();
            detail = "native-omm-usage-histogram-count-invalid";
            return false;
        }

        usageCounts = new MicromapUsageEXT[payload.UsageHistogram.Count];
        for (int index = 0; index < usageCounts.Length; index++)
        {
            OpacityMicromapUsage source = payload.UsageHistogram[index];
            if (source.Format != OpacityMicromapFormat.FourState ||
                source.Count == 0UL || source.Count > uint.MaxValue ||
                source.SubdivisionLevel > payload.MaximumSubdivisionLevel)
            {
                usageCounts = Array.Empty<MicromapUsageEXT>();
                detail = "native-omm-usage-histogram-entry-invalid";
                return false;
            }

            usageCounts[index] = new MicromapUsageEXT
            {
                Count = checked((uint)source.Count),
                SubdivisionLevel = source.SubdivisionLevel,
                Format = (uint)OpacityMicromapFormatEXT.Format4StateExt
            };
        }

        detail = "native-omm-usage-histogram-valid";
        return true;
    }

    private static bool IsSupportedSpecialIndex(
        uint index,
        IndexType indexType)
    {
        uint mask = indexType == IndexType.Uint16 ? ushort.MaxValue : uint.MaxValue;
        return index == (unchecked((uint)OpacityMicromapSpecialIndexEXT.FullyUnknownOpaqueExt) & mask) ||
            index == (unchecked((uint)OpacityMicromapSpecialIndexEXT.FullyUnknownTransparentExt) & mask) ||
            index == (unchecked((uint)OpacityMicromapSpecialIndexEXT.FullyOpaqueExt) & mask) ||
            index == (unchecked((uint)OpacityMicromapSpecialIndexEXT.FullyTransparentExt) & mask);
    }

    private static bool TryGetFourStateDataBytes(
        ushort subdivisionLevel,
        out ulong dataBytes)
    {
        // A four-state OMM has two bits per microtriangle.  A subdivided
        // triangle contains 4^level microtriangles, and byte packing rounds
        // the level-zero two-bit payload up to one byte.
        uint bitShift = checked((uint)subdivisionLevel * 2U + 1U);
        if (bitShift >= sizeof(ulong) * 8U)
        {
            dataBytes = 0UL;
            return false;
        }

        ulong bitCount = 1UL << checked((int)bitShift);
        dataBytes = checked((bitCount + 7UL) / 8UL);
        return true;
    }

    private static bool UsageCountsMatch(
        IReadOnlyDictionary<uint, uint> expected,
        IReadOnlyDictionary<uint, uint> actual)
    {
        if (expected.Count != actual.Count)
            return false;

        foreach ((uint subdivision, uint count) in expected)
        {
            if (!actual.TryGetValue(subdivision, out uint actualCount) ||
                actualCount != count)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Fixed static-mesh registration required to resolve a backend-neutral cooked
/// content key into a real ordinary BLAS fallback.  The renderer must publish
/// this only for static triangle geometry whose per-primitive order exactly
/// matches the cooked OMM index stream.
/// </summary>
public readonly record struct OpacityMicromapExtStaticBlasCandidate(
    OpacityMicromapContentKey ContentKey,
    MeshHandle Mesh,
    OpacityMicromapContentKey MeshGeometryKey,
    StaticBlasRayGeometryPolicy RayGeometryPolicy,
    uint AccelerationStructureBuildAbi,
    OpacityMicromapExtNativeInputLayout NativeInputLayout)
{
    public StaticBlasVariantKey CreateVariantKey() => new(
        MeshGeometryKey,
        RayGeometryPolicy,
        ContentKey,
        AccelerationStructureBuildAbi);

    public bool HasSameImmutableVariant(
        in OpacityMicromapExtStaticBlasCandidate other) =>
        CreateVariantKey() == other.CreateVariantKey() &&
        NativeInputLayout == other.NativeInputLayout;

    public bool TryValidateFor(
        in OpacityMicromapBackendBuildRequest request,
        out string detail)
    {
        if (ContentKey.IsZero || ContentKey != request.ContentKey ||
            !Mesh.IsValid || MeshGeometryKey.IsZero ||
            AccelerationStructureBuildAbi == 0U ||
            AccelerationStructureBuildAbi != request.AccelerationStructureBuildAbi)
        {
            detail = "omm-static-blas-candidate-key-or-mesh-invalid";
            return false;
        }
        if (RayGeometryPolicy is not
            (StaticBlasRayGeometryPolicy.CandidateConfirmationRequired or
             StaticBlasRayGeometryPolicy.TwoSidedCandidateConfirmationRequired))
        {
            detail = "omm-static-blas-candidate-does-not-retain-candidate-confirmation";
            return false;
        }
        if (!NativeInputLayout.TryValidate(request.Payload, out detail))
        {
            detail = "omm-static-blas-candidate-native-input-layout-invalid-" + detail;
            return false;
        }

        detail = "omm-static-blas-candidate-valid";
        return true;
    }
}

/// <summary>
/// Observed ordinary path retained by the acceleration-structure owner.  A
/// native OMM build is never started until this exact candidate-tested BLAS is
/// resident, so every failure preserves traversal correctness.
/// </summary>
public readonly record struct OpacityMicromapExtOrdinaryFallback(
    MeshHandle Mesh,
    uint PrimitiveCount,
    ulong BlasHandle,
    ulong ResidentBytes,
    bool IsStaticTriangleGeometry,
    bool CandidateConfirmationAvailable)
{
    public bool IsUsableFor(in OpacityMicromapExtStaticBlasCandidate candidate,
        in OpacityMicromapBackendBuildRequest request) =>
        Mesh == candidate.Mesh &&
        PrimitiveCount == request.Payload.PrimitiveCount &&
        BlasHandle != 0UL &&
        ResidentBytes != 0UL &&
        IsStaticTriangleGeometry &&
        CandidateConfirmationAvailable;
}

/// <summary>
/// A native buffer reference supplied to the EXT recorder.  Build inputs and
/// scratch require a device address; micromap storage does not unless a future
/// capture/replay profile explicitly opts into one.  The owner must keep the
/// backing allocation alive until the GPU completion token associated with the
/// build has been observed.
/// </summary>
public readonly record struct OpacityMicromapExtDeviceBufferBinding(
    BufferHandle Buffer,
    ulong DeviceAddress,
    ulong ByteLength)
{
    public bool IsValid =>
        Buffer.IsValid && DeviceAddress != 0UL && ByteLength != 0UL;

    public bool HasRequiredBufferRange(ulong bytes) =>
        Buffer.IsValid && ByteLength != 0UL && bytes <= ByteLength;

    public bool HasRequiredBytes(ulong bytes) =>
        IsValid && bytes <= ByteLength;

    /// <summary>
    /// Revalidates the handle, size, usage mask, and device address against
    /// the live allocator immediately before the binding is submitted to
    /// Vulkan.  A stale or substituted handle is never treated as an
    /// equivalent device address.
    /// </summary>
    public bool TryValidateLive(
        BufferManager? bufferManager,
        ulong requiredBytes,
        BufferUsageFlags requiredUsage,
        bool requireDeviceAddress,
        out string detail)
    {
        if (!HasRequiredBufferRange(requiredBytes) ||
            (requireDeviceAddress && DeviceAddress == 0UL))
        {
            detail = "native-omm-device-buffer-binding-range-invalid";
            return false;
        }
        if (bufferManager is null)
        {
            detail = "native-omm-device-buffer-binding-manager-unavailable";
            return false;
        }

        try
        {
            if (bufferManager.GetBufferSize(Buffer) < requiredBytes)
            {
                detail = "native-omm-device-buffer-binding-live-size-too-small";
                return false;
            }

            BufferUsageFlags actualUsage = bufferManager.GetBufferUsage(Buffer);
            if ((actualUsage & requiredUsage) != requiredUsage)
            {
                detail = "native-omm-device-buffer-binding-required-usage-missing";
                return false;
            }

            if (requireDeviceAddress)
            {
                ulong actualAddress = bufferManager.GetBufferDeviceAddress(Buffer);
                if (actualAddress == 0UL || actualAddress != DeviceAddress)
                {
                    detail = "native-omm-device-buffer-binding-device-address-stale";
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          ObjectDisposedException or
                                          OverflowException)
        {
            detail = "native-omm-device-buffer-binding-live-validation-rejected-" +
                exception.GetType().Name;
            return false;
        }

        detail = "native-omm-device-buffer-binding-live-valid";
        return true;
    }
}

/// <summary>
/// Actual sizes returned by <c>vkGetMicromapBuildSizesEXT</c>.  These values,
/// not cooked byte estimates, are the only valid allocation sizes for the
/// micromap storage and scratch resources.
/// </summary>
public readonly record struct OpacityMicromapExtNativeBuildSizes(
    ulong MicromapStorageBytes,
    ulong BuildScratchBytes,
    bool CompactionAllowed,
    bool Discardable)
{
    public bool IsValid => MicromapStorageBytes != 0UL;
}

/// <summary>
/// Holds the native shape needed for one command-buffer build after the device
/// query and upload allocations have completed.  It intentionally has no host
/// build path: the target RTX 3060 class requires GPU command recording.
/// </summary>
public readonly record struct OpacityMicromapExtNativeBuildInputs(
    OpacityMicromapExtDeviceBufferBinding OmmData,
    OpacityMicromapExtDeviceBufferBinding TriangleArray,
    OpacityMicromapExtDeviceBufferBinding PerPrimitiveIndex,
    OpacityMicromapExtDeviceBufferBinding Scratch,
    MicromapEXT DestinationMicromap)
{
    public bool TryValidateForBuild(
        OpacityMicromapCookedPayload payload,
        in OpacityMicromapExtNativeBuildSizes sizes,
        out string detail)
    {
        if (DestinationMicromap.Handle == 0UL || !sizes.IsValid)
        {
            detail = "native-omm-build-destination-or-device-query-invalid";
            return false;
        }
        if (!OmmData.HasRequiredBytes((ulong)payload.OmmData.Length) ||
            !TriangleArray.HasRequiredBytes((ulong)payload.DescriptorData.Length) ||
            !PerPrimitiveIndex.HasRequiredBytes((ulong)payload.IndexData.Length))
        {
            detail = "native-omm-build-input-buffer-range-invalid";
            return false;
        }
        if (OmmData.DeviceAddress % 256UL != 0UL ||
            TriangleArray.DeviceAddress % 256UL != 0UL)
        {
            detail = "native-omm-build-data-or-triangle-address-not-256-byte-aligned";
            return false;
        }
        if (sizes.BuildScratchBytes != 0UL &&
            !Scratch.HasRequiredBytes(sizes.BuildScratchBytes))
        {
            detail = "native-omm-build-scratch-buffer-range-invalid";
            return false;
        }

        detail = "native-omm-build-inputs-valid";
        return true;
    }
}

/// <summary>
/// A stack-scoped callback used to attach a fully built EXT micromap to a
/// static triangle geometry.  The callback must record all KHR BLAS size/build
/// commands while the supplied pNext structure and usage-count array remain
/// alive; retaining the pointer is invalid.
/// </summary>
public unsafe delegate void OpacityMicromapExtBlasAttachmentRecorder(
    AccelerationStructureTrianglesOpacityMicromapEXT* attachment);

/// <summary>
/// Immutable final micromap-to-BLAS attachment.  Its managed data is copied on
/// construction; <see cref="RecordWithNativeAttachment"/> pins only for the
/// duration of the caller's command-recording callback.
/// </summary>
public sealed class OpacityMicromapExtStaticBlasAttachment
{
    private readonly MicromapUsageEXT[] _usageCounts;

    private OpacityMicromapExtStaticBlasAttachment(
        StaticBlasVariantKey variantKey,
        MicromapEXT micromap,
        ulong perPrimitiveIndexAddress,
        IndexType perPrimitiveIndexType,
        ulong perPrimitiveIndexStride,
        uint baseTriangle,
        MicromapUsageEXT[] usageCounts)
    {
        VariantKey = variantKey;
        Micromap = micromap;
        PerPrimitiveIndexAddress = perPrimitiveIndexAddress;
        PerPrimitiveIndexType = perPrimitiveIndexType;
        PerPrimitiveIndexStride = perPrimitiveIndexStride;
        BaseTriangle = baseTriangle;
        _usageCounts = usageCounts.ToArray();
    }

    /// <summary>
    /// Creates the only supported attachment shape from the exact cooked
    /// descriptor and per-primitive streams.  This prevents an accidental use
    /// of the micromap-build histogram for the BLAS attachment, which is
    /// invalid whenever geometry indices reuse, skip, or special-case a
    /// descriptor.
    /// </summary>
    public static bool TryCreate(
        in StaticBlasVariantKey variantKey,
        MicromapEXT micromap,
        OpacityMicromapCookedPayload? payload,
        in OpacityMicromapExtNativeInputLayout layout,
        in OpacityMicromapExtDeviceBufferBinding perPrimitiveIndex,
        out OpacityMicromapExtStaticBlasAttachment? attachment,
        out string detail)
    {
        attachment = null;
        if (payload is null || !variantKey.IsValid ||
            !variantKey.HasOpacityMicromap ||
            variantKey.OpacityMicromapContentKeyOrNull !=
                payload.SourceContentHash ||
            micromap.Handle == 0UL)
        {
            detail = "native-omm-static-blas-attachment-key-or-micromap-invalid";
            return false;
        }
        if (variantKey.RayGeometryPolicy is not
            (StaticBlasRayGeometryPolicy.CandidateConfirmationRequired or
             StaticBlasRayGeometryPolicy.TwoSidedCandidateConfirmationRequired))
        {
            detail = "native-omm-static-blas-attachment-does-not-retain-candidate-confirmation";
            return false;
        }
        if (!perPrimitiveIndex.HasRequiredBytes((ulong)payload.IndexData.Length))
        {
            detail = "native-omm-static-blas-attachment-index-buffer-range-invalid";
            return false;
        }
        if (!OpacityMicromapExtNativeInputLayout.TryCreateStaticBlasUsageCounts(
                payload,
                layout,
                out MicromapUsageEXT[] usageCounts,
                out detail))
        {
            detail = "native-omm-static-blas-attachment-usage-derivation-invalid-" +
                detail;
            return false;
        }

        var created = new OpacityMicromapExtStaticBlasAttachment(
            variantKey,
            micromap,
            perPrimitiveIndex.DeviceAddress,
            layout.PerPrimitiveIndexType,
            layout.PerPrimitiveIndexStride,
            layout.BaseTriangle,
            usageCounts);
        if (!created.TryValidate(out detail))
        {
            detail = "native-omm-static-blas-attachment-created-invalid-" + detail;
            return false;
        }

        attachment = created;
        detail = "native-omm-static-blas-attachment-created";
        return true;
    }

    public StaticBlasVariantKey VariantKey { get; }
    public MicromapEXT Micromap { get; }
    public ulong PerPrimitiveIndexAddress { get; }
    public IndexType PerPrimitiveIndexType { get; }
    public ulong PerPrimitiveIndexStride { get; }
    public uint BaseTriangle { get; }
    public IReadOnlyList<MicromapUsageEXT> UsageCounts => _usageCounts;

    public bool TryValidate(out string detail)
    {
        if (!VariantKey.IsValid || !VariantKey.HasOpacityMicromap ||
            Micromap.Handle == 0UL || PerPrimitiveIndexAddress == 0UL ||
            BaseTriangle != 0U || _usageCounts.Length == 0)
        {
            detail = "native-omm-static-blas-attachment-fields-invalid";
            return false;
        }
        ulong requiredStride;
        switch (PerPrimitiveIndexType)
        {
            case IndexType.Uint16:
                requiredStride = sizeof(ushort);
                break;
            case IndexType.Uint32:
                requiredStride = sizeof(uint);
                break;
            default:
                detail = "native-omm-static-blas-attachment-index-type-invalid";
                return false;
        }
        if (PerPrimitiveIndexStride != requiredStride)
        {
            detail = "native-omm-static-blas-attachment-index-stride-invalid";
            return false;
        }
        foreach (MicromapUsageEXT usage in _usageCounts)
        {
            if (usage.Count == 0U ||
                usage.Format != (uint)OpacityMicromapFormatEXT.Format4StateExt)
            {
                detail = "native-omm-static-blas-attachment-usage-invalid";
                return false;
            }
        }

        detail = "native-omm-static-blas-attachment-valid";
        return true;
    }

    public unsafe void RecordWithNativeAttachment(
        OpacityMicromapExtBlasAttachmentRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (!TryValidate(out string detail))
            throw new InvalidOperationException(detail);

        fixed (MicromapUsageEXT* usageCounts = _usageCounts)
        {
            var attachment = new AccelerationStructureTrianglesOpacityMicromapEXT
            {
                SType = StructureType.AccelerationStructureTrianglesOpacityMicromapExt,
                IndexType = PerPrimitiveIndexType,
                IndexBuffer = new DeviceOrHostAddressConstKHR
                {
                    DeviceAddress = PerPrimitiveIndexAddress
                },
                IndexStride = PerPrimitiveIndexStride,
                BaseTriangle = BaseTriangle,
                UsageCountsCount = checked((uint)_usageCounts.Length),
                PUsageCounts = usageCounts,
                PpUsageCounts = null,
                Micromap = Micromap
            };
            recorder(&attachment);
        }
    }
}

/// <summary>
/// Low-level GPU-only EXT command recorder.  It has no cache or publication
/// policy: callers must supply allocations, command-buffer ownership, and a
/// completion primitive.  Keeping that boundary explicit prevents the current
/// frame-oriented AS manager from claiming a safe asynchronous lifecycle it
/// does not yet own.
/// </summary>
public static unsafe class VulkanExtOpacityMicromapNativeCommandRecorder
{
    private const BufferUsageFlags MicromapBuildInputUsage =
        BufferUsageFlags.MicromapBuildInputReadOnlyBitExt |
        BufferUsageFlags.ShaderDeviceAddressBit;
    private const BufferUsageFlags AccelerationStructureBuildInputUsage =
        BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr |
        BufferUsageFlags.ShaderDeviceAddressBit;
    private const BufferUsageFlags MicromapStorageUsage =
        BufferUsageFlags.MicromapStorageBitExt;
    private const BufferUsageFlags MicromapScratchUsage =
        BufferUsageFlags.StorageBufferBit |
        BufferUsageFlags.ShaderDeviceAddressBit;

    public static bool TryQueryBuildSizes(
        SilkNetExtOpacityMicromapCommandApi? api,
        Device device,
        OpacityMicromapCookedPayload? payload,
        in OpacityMicromapExtNativeInputLayout layout,
        in OpacityMicromapExtBuildPolicy policy,
        out OpacityMicromapExtNativeBuildSizes sizes,
        out string detail)
    {
        sizes = default;
        if (api is null || device.Handle == 0)
        {
            detail = "native-omm-build-size-dispatch-or-device-unavailable";
            return false;
        }
        if (!policy.IsValid)
        {
            detail = "native-omm-build-size-policy-invalid";
            return false;
        }
        if (!layout.TryValidate(payload, out detail))
        {
            detail = "native-omm-build-size-layout-invalid-" + detail;
            return false;
        }
        if (!OpacityMicromapExtNativeInputLayout.TryCreateNativeUsageCounts(
                payload!,
                out MicromapUsageEXT[] usageCounts,
                out detail))
        {
            detail = "native-omm-build-size-usage-invalid-" + detail;
            return false;
        }

        fixed (MicromapUsageEXT* usageCountPointer = usageCounts)
        {
            BuildMicromapFlagsEXT flags =
                BuildMicromapFlagsEXT.PreferFastTraceBitExt;
            if (policy.EnableCompaction)
                flags |= BuildMicromapFlagsEXT.AllowCompactionBitExt;
            var buildInfo = new MicromapBuildInfoEXT
            {
                SType = StructureType.MicromapBuildInfoExt,
                Type = MicromapTypeEXT.OpacityMicromapExt,
                Flags = flags,
                Mode = BuildMicromapModeEXT.BuildExt,
                UsageCountsCount = checked((uint)usageCounts.Length),
                PUsageCounts = usageCountPointer,
                PpUsageCounts = null
            };
            var nativeSizes = new MicromapBuildSizesInfoEXT
            {
                SType = StructureType.MicromapBuildSizesInfoExt
            };
            api.GetMicromapBuildSizes(
                device,
                AccelerationStructureBuildTypeKHR.DeviceKhr,
                ref buildInfo,
                ref nativeSizes);
            if (nativeSizes.MicromapSize == 0UL)
            {
                detail = "native-omm-build-size-query-returned-zero-micromap-size";
                return false;
            }

            sizes = new OpacityMicromapExtNativeBuildSizes(
                MicromapStorageBytes: nativeSizes.MicromapSize,
                BuildScratchBytes: nativeSizes.BuildScratchSize,
                CompactionAllowed: policy.EnableCompaction,
                Discardable: nativeSizes.Discardable);
        }

        detail = "native-omm-build-sizes-queried";
        return true;
    }

    /// <summary>
    /// Validates the live storage allocation before creating the native
    /// micromap object.  The caller receives no usable object on a validation
    /// or Vulkan failure and must retain the ordinary BLAS path.
    /// </summary>
    public static bool TryCreateMicromap(
        SilkNetExtOpacityMicromapCommandApi? api,
        Device device,
        BufferManager? bufferManager,
        in OpacityMicromapExtDeviceBufferBinding storage,
        in OpacityMicromapExtNativeBuildSizes sizes,
        out MicromapEXT micromap,
        out string detail)
    {
        micromap = default;
        if (api is null || device.Handle == 0 || !sizes.IsValid)
        {
            detail = "native-omm-create-dispatch-device-or-build-size-invalid";
            return false;
        }
        if (!storage.TryValidateLive(
                bufferManager,
                sizes.MicromapStorageBytes,
                MicromapStorageUsage,
                requireDeviceAddress: false,
                out detail))
        {
            detail = "native-omm-create-storage-invalid-" + detail;
            return false;
        }

        VkBuffer storageBuffer;
        try
        {
            storageBuffer = bufferManager!.GetBuffer(storage.Buffer);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          ObjectDisposedException or
                                          OverflowException)
        {
            detail = "native-omm-create-storage-handle-rejected-" +
                exception.GetType().Name;
            return false;
        }

        var createInfo = new MicromapCreateInfoEXT
        {
            SType = StructureType.MicromapCreateInfoExt,
            Buffer = storageBuffer,
            Offset = 0UL,
            Size = sizes.MicromapStorageBytes,
            Type = MicromapTypeEXT.OpacityMicromapExt,
            // A non-zero deviceAddress requests capture/replay placement, not
            // the normal VkBuffer device address.  C1 has no capture/replay
            // contract, so normal allocations must leave it zero.
            DeviceAddress = 0UL
        };
        Result result = api.CreateMicromap(device, createInfo, out micromap);
        if (result != Result.Success || micromap.Handle == 0UL)
        {
            micromap = default;
            detail = result == Result.Success
                ? "native-omm-create-returned-null-micromap"
                : "native-omm-create-vulkan-failed-" + result;
            return false;
        }

        detail = "native-omm-created";
        return true;
    }

    /// <summary>
    /// Uploads the three actual device-addressed inputs.  The barriers match
    /// Vulkan's EXT requirements: OMM data and triangle records are shader
    /// reads at the micromap-build stage; the per-primitive indirection stream
    /// is consumed by the following AS build.
    /// </summary>
    public static bool TryUploadInputs(
        VulkanContext? context,
        BufferManager? bufferManager,
        StagingRing? stagingRing,
        CommandBuffer commandBuffer,
        OpacityMicromapCookedPayload? payload,
        in OpacityMicromapExtNativeInputLayout layout,
        in OpacityMicromapExtNativeBuildInputs inputs,
        out string detail)
    {
        if (context is null || bufferManager is null || stagingRing is null ||
            commandBuffer.Handle == 0 || payload is null)
        {
            detail = "native-omm-upload-context-or-input-missing";
            return false;
        }
        if (!layout.TryValidate(payload, out detail))
        {
            detail = "native-omm-upload-layout-invalid-" + detail;
            return false;
        }
        if (!TryValidateLiveInputBuffers(
                bufferManager,
                payload,
                inputs,
                out detail))
        {
            detail = "native-omm-upload-buffer-invalid-" + detail;
            return false;
        }

        try
        {
            GpuBufferUploader.UploadSpanToBuffer(
                context,
                bufferManager,
                stagingRing,
                commandBuffer,
                inputs.OmmData.Buffer,
                payload.OmmData.Span,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.MicromapBuildBitExt,
                    AccessFlags2.ShaderReadBit));
            GpuBufferUploader.UploadSpanToBuffer(
                context,
                bufferManager,
                stagingRing,
                commandBuffer,
                inputs.TriangleArray.Buffer,
                payload.DescriptorData.Span,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.MicromapBuildBitExt,
                    AccessFlags2.ShaderReadBit));
            GpuBufferUploader.UploadSpanToBuffer(
                context,
                bufferManager,
                stagingRing,
                commandBuffer,
                inputs.PerPrimitiveIndex.Buffer,
                payload.IndexData.Span,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                    AccessFlags2.AccelerationStructureReadBitKhr));
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           OverflowException)
        {
            detail = "native-omm-upload-rejected-" + exception.GetType().Name;
            return false;
        }

        detail = "native-omm-device-addressable-inputs-uploaded";
        return true;
    }

    public static bool TryRecordBuild(
        SilkNetExtOpacityMicromapCommandApi? api,
        BufferManager? bufferManager,
        CommandBuffer commandBuffer,
        OpacityMicromapCookedPayload? payload,
        in OpacityMicromapExtNativeInputLayout layout,
        in OpacityMicromapExtNativeBuildSizes sizes,
        in OpacityMicromapExtNativeBuildInputs inputs,
        ulong scratchAddressAlignment,
        out string detail)
    {
        if (api is null || commandBuffer.Handle == 0 ||
            scratchAddressAlignment == 0UL)
        {
            detail = "native-omm-record-build-api-command-buffer-or-alignment-invalid";
            return false;
        }
        if (!layout.TryValidate(payload, out detail))
        {
            detail = "native-omm-record-build-layout-invalid-" + detail;
            return false;
        }
        if (!inputs.TryValidateForBuild(payload!, sizes, out detail))
        {
            detail = "native-omm-record-build-resources-invalid-" + detail;
            return false;
        }
        if (!TryValidateLiveInputBuffers(
                bufferManager,
                payload!,
                inputs,
                out detail) ||
            (sizes.BuildScratchBytes != 0UL &&
             !inputs.Scratch.TryValidateLive(
                 bufferManager,
                 sizes.BuildScratchBytes,
                 MicromapScratchUsage,
                 requireDeviceAddress: true,
                 out detail)))
        {
            detail = "native-omm-record-build-live-resources-invalid-" + detail;
            return false;
        }
        if (sizes.BuildScratchBytes != 0UL &&
            inputs.Scratch.DeviceAddress % scratchAddressAlignment != 0UL)
        {
            detail = "native-omm-record-build-scratch-address-alignment-invalid";
            return false;
        }
        if (!OpacityMicromapExtNativeInputLayout.TryCreateNativeUsageCounts(
                payload!,
                out MicromapUsageEXT[] usageCounts,
                out detail))
        {
            detail = "native-omm-record-build-usage-invalid-" + detail;
            return false;
        }

        fixed (MicromapUsageEXT* usageCountPointer = usageCounts)
        {
            BuildMicromapFlagsEXT flags =
                BuildMicromapFlagsEXT.PreferFastTraceBitExt;
            if (sizes.CompactionAllowed)
                flags |= BuildMicromapFlagsEXT.AllowCompactionBitExt;
            var buildInfo = new MicromapBuildInfoEXT
            {
                SType = StructureType.MicromapBuildInfoExt,
                Type = MicromapTypeEXT.OpacityMicromapExt,
                Flags = flags,
                Mode = BuildMicromapModeEXT.BuildExt,
                DstMicromap = inputs.DestinationMicromap,
                UsageCountsCount = checked((uint)usageCounts.Length),
                PUsageCounts = usageCountPointer,
                PpUsageCounts = null,
                Data = new DeviceOrHostAddressConstKHR
                {
                    DeviceAddress = inputs.OmmData.DeviceAddress
                },
                ScratchData = new DeviceOrHostAddressKHR
                {
                    DeviceAddress = inputs.Scratch.DeviceAddress
                },
                TriangleArray = new DeviceOrHostAddressConstKHR
                {
                    DeviceAddress = inputs.TriangleArray.DeviceAddress
                },
                TriangleArrayStride = layout.TriangleArrayStride
            };
            api.CmdBuildMicromaps(commandBuffer, new ReadOnlySpan<MicromapBuildInfoEXT>(
                &buildInfo,
                1));
        }

        detail = "native-omm-build-recorded";
        return true;
    }

    private static bool TryValidateLiveInputBuffers(
        BufferManager? bufferManager,
        OpacityMicromapCookedPayload payload,
        in OpacityMicromapExtNativeBuildInputs inputs,
        out string detail)
    {
        if (!inputs.OmmData.TryValidateLive(
                bufferManager,
                (ulong)payload.OmmData.Length,
                MicromapBuildInputUsage,
                requireDeviceAddress: true,
                out detail))
        {
            detail = "omm-data-" + detail;
            return false;
        }
        if (!inputs.TriangleArray.TryValidateLive(
                bufferManager,
                (ulong)payload.DescriptorData.Length,
                MicromapBuildInputUsage,
                requireDeviceAddress: true,
                out detail))
        {
            detail = "triangle-array-" + detail;
            return false;
        }
        if (!inputs.PerPrimitiveIndex.TryValidateLive(
                bufferManager,
                (ulong)payload.IndexData.Length,
                AccelerationStructureBuildInputUsage,
                requireDeviceAddress: true,
                out detail))
        {
            detail = "per-primitive-index-" + detail;
            return false;
        }

        detail = "native-omm-live-input-buffers-valid";
        return true;
    }

    /// <summary>Records the mandatory OMM-write to BLAS-read dependency.</summary>
    public static void RecordMicromapBuildToBlasBarrier(
        VulkanContext context,
        CommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (commandBuffer.Handle == 0)
            throw new ArgumentOutOfRangeException(nameof(commandBuffer));
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.MicromapBuildBitExt,
            SrcAccessMask = AccessFlags2.MicromapWriteBitExt,
            DstStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            DstAccessMask = AccessFlags2.MicromapReadBitExt
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier
        };
        context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    /// <summary>
    /// Makes micromap construction visible to a compaction-size query or copy
    /// recorded on the same command stream.
    /// </summary>
    public static void RecordMicromapBuildToMicromapReadBarrier(
        VulkanContext context,
        CommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (commandBuffer.Handle == 0)
            throw new ArgumentOutOfRangeException(nameof(commandBuffer));
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.MicromapBuildBitExt,
            SrcAccessMask = AccessFlags2.MicromapWriteBitExt,
            DstStageMask = PipelineStageFlags2.MicromapBuildBitExt,
            DstAccessMask = AccessFlags2.MicromapReadBitExt
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier
        };
        context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    public static void RecordCompactedSizeQuery(
        VulkanContext context,
        SilkNetExtOpacityMicromapCommandApi api,
        CommandBuffer commandBuffer,
        MicromapEXT micromap,
        QueryPool queryPool,
        in OpacityMicromapExtNativeBuildSizes sourceBuildSizes,
        uint queryIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(api);
        if (commandBuffer.Handle == 0 || micromap.Handle == 0UL ||
            queryPool.Handle == 0UL || !sourceBuildSizes.IsValid ||
            !sourceBuildSizes.CompactionAllowed)
            throw new ArgumentOutOfRangeException(nameof(micromap));

        RecordMicromapBuildToMicromapReadBarrier(context, commandBuffer);
        api.CmdWriteCompactedSize(
            commandBuffer,
            new ReadOnlySpan<MicromapEXT>(&micromap, 1),
            queryPool,
            queryIndex);
    }

    public static bool ShouldCompact(
        in OpacityMicromapExtBuildPolicy policy,
        in OpacityMicromapExtNativeBuildSizes sizes,
        ulong queriedCompactedBytes,
        out string detail)
    {
        if (!policy.IsValid)
        {
            detail = "native-omm-compaction-policy-invalid";
            return false;
        }
        if (!sizes.IsValid)
        {
            detail = "native-omm-compaction-build-size-invalid";
            return false;
        }
        if (!sizes.CompactionAllowed)
        {
            detail = "native-omm-compaction-not-enabled-for-original-build";
            return false;
        }
        if (queriedCompactedBytes == 0UL)
        {
            detail = "native-omm-compaction-query-returned-zero";
            return false;
        }
        if (!policy.ShouldCompact(sizes.MicromapStorageBytes, queriedCompactedBytes))
        {
            detail = "native-omm-compaction-saving-below-policy";
            return false;
        }

        detail = "native-omm-compaction-admitted";
        return true;
    }

    public static void RecordCompactionCopy(
        VulkanContext context,
        SilkNetExtOpacityMicromapCommandApi api,
        CommandBuffer commandBuffer,
        MicromapEXT source,
        MicromapEXT destination,
        in OpacityMicromapExtNativeBuildSizes sourceBuildSizes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(api);
        if (commandBuffer.Handle == 0 || source.Handle == 0UL ||
            destination.Handle == 0UL || !sourceBuildSizes.IsValid ||
            !sourceBuildSizes.CompactionAllowed)
            throw new ArgumentOutOfRangeException(nameof(source));

        RecordMicromapBuildToMicromapReadBarrier(context, commandBuffer);
        var copy = new CopyMicromapInfoEXT
        {
            SType = StructureType.CopyMicromapInfoExt,
            Src = source,
            Dst = destination,
            Mode = CopyMicromapModeEXT.CompactExt
        };
        api.CmdCopyMicromap(commandBuffer, copy);
    }
}

/// <summary>
/// Concrete AS-owned lifecycle host exposed before renderer submission wiring
/// exists.  It owns content-key registration and ordinary-fallback proof, but
/// intentionally refuses to issue a native build until the renderer provides a
/// static-BLAS variant cache, command submission domain, completion token, and
/// descriptor/TLAS publication hook as one transaction.
/// </summary>
public sealed class AccelerationStructureOpacityMicromapNativeLifecycleHost :
    IOpacityMicromapExtNativeLifecycleHost,
    IDisposable
{
    private readonly Func<OpacityMicromapExtCapabilityReport> _capabilityProvider;
    private readonly Func<MeshHandle, OpacityMicromapExtOrdinaryFallback>
        _ordinaryFallbackProvider;
    private readonly object _sync = new();
    private readonly Dictionary<OpacityMicromapContentKey,
        OpacityMicromapExtStaticBlasCandidate> _registrations = new();
    private bool _disposed;

    public AccelerationStructureOpacityMicromapNativeLifecycleHost(
        Func<OpacityMicromapExtCapabilityReport> capabilityProvider,
        Func<MeshHandle, OpacityMicromapExtOrdinaryFallback> ordinaryFallbackProvider)
    {
        _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(
            nameof(capabilityProvider));
        _ordinaryFallbackProvider = ordinaryFallbackProvider ?? throw new ArgumentNullException(
            nameof(ordinaryFallbackProvider));
    }

    public OpacityMicromapExtCapabilityReport CapabilityReport
    {
        get
        {
            OpacityMicromapExtCapabilityReport reported = _capabilityProvider();
            if (!reported.SupportsPublication)
                return reported;

            // A context capability only proves device enablement.  Do not let
            // future context changes accidentally advertise C1 hardware output
            // before this host has an atomic AS variant submission/retirement
            // integration.
            return reported with
            {
                NativeBuildPathAvailable = false,
                Failure = OpacityMicromapExtCapabilityFailure.BlasAttachmentNotIntegrated,
                Detail = "matching-static-BLAS-EXT-attachment-submission-and-retirement-not-integrated"
            };
        }
    }

    public bool TryRegister(
        in OpacityMicromapExtStaticBlasCandidate candidate,
        out string detail)
    {
        if (candidate.ContentKey.IsZero || !candidate.Mesh.IsValid ||
            candidate.MeshGeometryKey.IsZero ||
            candidate.AccelerationStructureBuildAbi == 0U)
        {
            detail = "omm-static-blas-registration-fields-invalid";
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                detail = "omm-static-blas-registration-host-disposed";
                return false;
            }
            if (_registrations.TryGetValue(candidate.ContentKey, out var existing))
            {
                if (existing == candidate)
                {
                    detail = "omm-static-blas-registration-already-present";
                    return true;
                }

                if (existing.HasSameImmutableVariant(candidate))
                {
                    // A rigid instance with an identical immutable variant may
                    // become the representative ordinary-BLAS owner after the
                    // previous mesh handle is released. Replace only that
                    // handle atomically; content/layout conflicts still fail
                    // closed below.
                    _registrations[candidate.ContentKey] = candidate;
                    detail =
                        "omm-static-blas-registration-shared-owner-updated";
                    return true;
                }

                detail = "omm-static-blas-registration-content-key-conflict";
                return false;
            }

            _registrations.Add(candidate.ContentKey, candidate);
        }

        detail = "omm-static-blas-registration-added";
        return true;
    }

    public bool RemoveRegistration(
        OpacityMicromapContentKey contentKey,
        out string detail)
    {
        if (contentKey.IsZero)
        {
            detail = "omm-static-blas-registration-content-key-zero";
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                detail = "omm-static-blas-registration-host-disposed";
                return false;
            }
            if (!_registrations.Remove(contentKey))
            {
                detail = "omm-static-blas-registration-not-found";
                return false;
            }
        }

        detail = "omm-static-blas-registration-removed";
        return true;
    }

    public bool TryCreateBuildPlan(
        OpacityMicromapBackendBuildRequest request,
        OpacityMicromapExtBuildPolicy policy,
        out OpacityMicromapExtBuildPlan plan,
        out string detail)
    {
        plan = default;
        if (!request.IsWellFormed || request.PublicationGeneration == 0UL ||
            !policy.IsValid)
        {
            detail = "omm-static-blas-build-plan-request-or-policy-invalid";
            return false;
        }

        OpacityMicromapExtCapabilityReport capability = CapabilityReport;
        if (!capability.SupportsPublication)
        {
            detail = string.IsNullOrWhiteSpace(capability.Detail)
                ? "matching-static-BLAS-opacity-micromap-attachment-not-integrated"
                : capability.Detail;
            return false;
        }

        OpacityMicromapExtStaticBlasCandidate candidate;
        lock (_sync)
        {
            if (_disposed)
            {
                detail = "omm-static-blas-build-plan-host-disposed";
                return false;
            }
            if (!_registrations.TryGetValue(request.ContentKey, out candidate))
            {
                detail = "omm-static-blas-build-plan-content-key-not-registered";
                return false;
            }
        }

        if (!candidate.TryValidateFor(request, out detail))
        {
            detail = "omm-static-blas-build-plan-candidate-invalid-" + detail;
            return false;
        }

        OpacityMicromapExtOrdinaryFallback fallback =
            _ordinaryFallbackProvider(candidate.Mesh);
        if (!fallback.IsUsableFor(candidate, request))
        {
            detail = "omm-static-blas-build-plan-ordinary-candidate-fallback-not-resident";
            return false;
        }

        StaticBlasVariantKey plain = StaticBlasVariantKey.Plain(
            candidate.MeshGeometryKey,
            candidate.RayGeometryPolicy,
            candidate.AccelerationStructureBuildAbi);
        StaticBlasVariantKey opacity = plain with
        {
            OpacityMicromapContentKeyOrNull = request.ContentKey
        };
        plan = new OpacityMicromapExtBuildPlan(
            opacity,
            plain,
            fallback.BlasHandle,
            fallback.ResidentBytes);
        if (!plan.IsWellFormedFor(request))
        {
            plan = default;
            detail = "omm-static-blas-build-plan-internal-variant-invariant-failed";
            return false;
        }

        detail = "omm-static-blas-build-plan-ready-with-ordinary-candidate-fallback";
        return true;
    }

    public ValueTask<OpacityMicromapExtBuildReceipt> BuildAndWaitForPublicationAsync(
        OpacityMicromapBackendBuildRequest request,
        OpacityMicromapExtBuildPlan plan,
        OpacityMicromapExtBuildPolicy policy,
        CancellationToken cancellationToken)
    {
        OpacityMicromapExtBuildReceipt receipt = cancellationToken.IsCancellationRequested
            ? OpacityMicromapExtBuildReceipt.Failed(
                "omm-static-blas-native-build-cancelled-before-submission")
            : OpacityMicromapExtBuildReceipt.Failed(
                "matching-static-BLAS-EXT-attachment-submission-completion-and-retirement-contract-not-integrated");
        return ValueTask.FromResult(receipt);
    }

    public void DisposeUnpublished(OpacityMicromapExtPublishedArtifacts artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        throw new InvalidOperationException(
            "This fail-closed AS host cannot accept unpublished native OMM artifacts because it never publishes a hardware variant.");
    }

    public void RetirePublished(
        OpacityMicromapExtPublishedArtifacts artifacts,
        GpuCompletionToken completion)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (!completion.IsValid)
            throw new ArgumentOutOfRangeException(nameof(completion));
        throw new InvalidOperationException(
            "This fail-closed AS host cannot retire a native OMM publication because it never publishes a hardware variant.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _registrations.Clear();
        }
    }
}
