using System.Buffers.Binary;
using System.Security.Cryptography;
using Njulf.Assets.Cooked;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Produces an immutable submesh-local EXT OMM payload from the model-wide
/// cooked payload.  Static BLASes are submesh-local in the renderer, so using
/// the model-wide index stream directly would associate the wrong primitive
/// with the wrong micromap descriptor.
/// </summary>
internal static class OpacityMicromapRuntimePayloadPartitioner
{
    private const uint PartitionAbiRevision = 1U;
    private const int NativeTriangleDescriptorBytes = 8;
    private const int NativeTriangleDataAlignment = 8;

    public static bool TryCreateSubmeshPayload(
        OpacityMicromapCookedPayload? aggregate,
        uint firstPrimitive,
        uint primitiveCount,
        uint materialSlot,
        out OpacityMicromapCookedPayload? partition,
        out string detail)
    {
        partition = null;
        if (aggregate is null || primitiveCount == 0U)
        {
            detail = "omm-runtime-partition-input-invalid";
            return false;
        }
        if (!OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
                aggregate,
                out string layoutDetail))
        {
            detail = "omm-runtime-partition-parent-invalid-" + layoutDetail;
            return false;
        }

        ulong endPrimitive = (ulong)firstPrimitive + primitiveCount;
        if (endPrimitive > aggregate.PrimitiveCount)
        {
            detail = "omm-runtime-partition-primitive-range-out-of-bounds";
            return false;
        }

        OpacityMicromapMaterialContract localContract = default;
        int exactContractCount = 0;
        foreach (OpacityMicromapMaterialContract contract in
                 aggregate.MaterialContracts)
        {
            ulong contractEnd =
                (ulong)contract.FirstPrimitive + contract.PrimitiveCount;
            bool overlaps = contract.FirstPrimitive < endPrimitive &&
                firstPrimitive < contractEnd;
            if (!overlaps)
                continue;
            if (contract.FirstPrimitive != firstPrimitive ||
                contract.PrimitiveCount != primitiveCount ||
                contract.MaterialSlot != materialSlot)
            {
                detail =
                    "omm-runtime-partition-material-range-is-not-submesh-exact";
                return false;
            }

            localContract = contract with { FirstPrimitive = 0U };
            exactContractCount++;
        }
        if (exactContractCount != 1)
        {
            detail = exactContractCount == 0
                ? "omm-runtime-partition-submesh-is-not-omm-eligible"
                : "omm-runtime-partition-material-range-is-ambiguous";
            return false;
        }

        int localPrimitiveCount;
        int firstIndexOffset;
        try
        {
            localPrimitiveCount = checked((int)primitiveCount);
            firstIndexOffset = checked((int)firstPrimitive * sizeof(uint));
        }
        catch (OverflowException)
        {
            detail = "omm-runtime-partition-host-range-overflow";
            return false;
        }

        ReadOnlySpan<byte> aggregateIndices = aggregate.IndexData.Span;
        var referencedDescriptors = new HashSet<uint>();
        for (int primitive = 0; primitive < localPrimitiveCount; primitive++)
        {
            int offset = checked(firstIndexOffset + primitive * sizeof(uint));
            uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(
                aggregateIndices.Slice(offset, sizeof(uint)));
            if (descriptor < aggregate.DescriptorCount)
                referencedDescriptors.Add(descriptor);
        }
        if (referencedDescriptors.Count == 0)
        {
            // A range containing only Vulkan special indices does not require
            // an OMM object.  Keep the ordinary candidate BLAS rather than
            // manufacturing an empty EXT build.
            detail =
                "omm-runtime-partition-submesh-has-only-special-indices";
            return false;
        }

        uint[] sourceDescriptorIndices = referencedDescriptors.ToArray();
        Array.Sort(sourceDescriptorIndices);
        ReadOnlySpan<byte> aggregateDescriptors = aggregate.DescriptorData.Span;
        ReadOnlySpan<byte> aggregateData = aggregate.OmmData.Span;
        var descriptorDataSizes = new int[sourceDescriptorIndices.Length];
        int localDataBytes = 0;
        for (int localIndex = 0;
             localIndex < sourceDescriptorIndices.Length;
             localIndex++)
        {
            uint sourceDescriptor = sourceDescriptorIndices[localIndex];
            int descriptorOffset = checked(
                (int)sourceDescriptor * NativeTriangleDescriptorBytes);
            ushort subdivision = BinaryPrimitives.ReadUInt16LittleEndian(
                aggregateDescriptors.Slice(
                    descriptorOffset + sizeof(uint),
                    sizeof(ushort)));
            if (!TryGetFourStateDataBytes(subdivision, out int dataBytes))
            {
                detail =
                    "omm-runtime-partition-descriptor-size-overflow";
                return false;
            }

            try
            {
                localDataBytes = AlignUp(
                    localDataBytes,
                    NativeTriangleDataAlignment);
                descriptorDataSizes[localIndex] = dataBytes;
                localDataBytes = checked(localDataBytes + dataBytes);
            }
            catch (OverflowException)
            {
                detail = "omm-runtime-partition-data-size-overflow";
                return false;
            }
        }

        byte[] localData = new byte[localDataBytes];
        byte[] localDescriptors = new byte[checked(
            sourceDescriptorIndices.Length * NativeTriangleDescriptorBytes)];
        var descriptorRemap = new Dictionary<uint, uint>(
            sourceDescriptorIndices.Length);
        var usageBySubdivision = new SortedDictionary<uint, ulong>();
        uint maximumSubdivision = 0U;
        int writeDataOffset = 0;
        for (int localIndex = 0;
             localIndex < sourceDescriptorIndices.Length;
             localIndex++)
        {
            uint sourceDescriptor = sourceDescriptorIndices[localIndex];
            int sourceDescriptorOffset = checked(
                (int)sourceDescriptor * NativeTriangleDescriptorBytes);
            uint sourceDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                aggregateDescriptors.Slice(
                    sourceDescriptorOffset,
                    sizeof(uint)));
            ushort subdivision = BinaryPrimitives.ReadUInt16LittleEndian(
                aggregateDescriptors.Slice(
                    sourceDescriptorOffset + sizeof(uint),
                    sizeof(ushort)));
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(
                aggregateDescriptors.Slice(
                    sourceDescriptorOffset + sizeof(uint) + sizeof(ushort),
                    sizeof(ushort)));
            int dataBytes = descriptorDataSizes[localIndex];
            writeDataOffset = AlignUp(
                writeDataOffset,
                NativeTriangleDataAlignment);
            aggregateData.Slice(checked((int)sourceDataOffset), dataBytes)
                .CopyTo(localData.AsSpan(writeDataOffset, dataBytes));

            int localDescriptorOffset = checked(
                localIndex * NativeTriangleDescriptorBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(
                localDescriptors.AsSpan(
                    localDescriptorOffset,
                    sizeof(uint)),
                checked((uint)writeDataOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(
                localDescriptors.AsSpan(
                    localDescriptorOffset + sizeof(uint),
                    sizeof(ushort)),
                subdivision);
            BinaryPrimitives.WriteUInt16LittleEndian(
                localDescriptors.AsSpan(
                    localDescriptorOffset + sizeof(uint) + sizeof(ushort),
                    sizeof(ushort)),
                format);

            descriptorRemap.Add(
                sourceDescriptor,
                checked((uint)localIndex));
            usageBySubdivision.TryGetValue(subdivision, out ulong usageCount);
            usageBySubdivision[subdivision] = checked(usageCount + 1UL);
            maximumSubdivision = Math.Max(maximumSubdivision, subdivision);
            writeDataOffset = checked(writeDataOffset + dataBytes);
        }

        byte[] localIndices = new byte[checked(
            localPrimitiveCount * sizeof(uint))];
        for (int primitive = 0; primitive < localPrimitiveCount; primitive++)
        {
            int sourceOffset = checked(
                firstIndexOffset + primitive * sizeof(uint));
            uint sourceIndex = BinaryPrimitives.ReadUInt32LittleEndian(
                aggregateIndices.Slice(sourceOffset, sizeof(uint)));
            uint localIndex = sourceIndex < aggregate.DescriptorCount
                ? descriptorRemap[sourceIndex]
                : sourceIndex;
            BinaryPrimitives.WriteUInt32LittleEndian(
                localIndices.AsSpan(
                    primitive * sizeof(uint),
                    sizeof(uint)),
                localIndex);
        }

        OpacityMicromapUsage[] localUsage = usageBySubdivision
            .Select(static pair => new OpacityMicromapUsage(
                OpacityMicromapFormat.FourState,
                pair.Key,
                pair.Value))
            .ToArray();
        OpacityMicromapContentKey partitionKey = CreatePartitionKey(
            aggregate.SourceContentHash,
            firstPrimitive,
            primitiveCount,
            materialSlot);
        try
        {
            partition = OpacityMicromapCookedPayload.Create(
                aggregate.CookAbi,
                partitionKey,
                aggregate.SdkProvenanceHash,
                maximumSubdivision,
                primitiveCount,
                checked((uint)sourceDescriptorIndices.Length),
                [localContract],
                localUsage,
                localData,
                localIndices,
                localDescriptors);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           OverflowException)
        {
            partition = null;
            detail = "omm-runtime-partition-payload-rejected-" +
                exception.GetType().Name;
            return false;
        }

        if (!OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
                partition,
                out layoutDetail))
        {
            partition = null;
            detail = "omm-runtime-partition-output-invalid-" + layoutDetail;
            return false;
        }

        detail = "omm-runtime-partition-created";
        return true;
    }

    private static OpacityMicromapContentKey CreatePartitionKey(
        OpacityMicromapContentKey aggregateKey,
        uint firstPrimitive,
        uint primitiveCount,
        uint materialSlot)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        hash.AppendData("njulf.opacity-micromap.runtime-partition"u8);
        Span<byte> bytes = stackalloc byte[
            OpacityMicromapContentKey.ByteLength + 4 * sizeof(uint)];
        aggregateKey.CopyTo(bytes);
        int offset = OpacityMicromapContentKey.ByteLength;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[offset..],
            PartitionAbiRevision);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[offset..],
            firstPrimitive);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[offset..],
            primitiveCount);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes[offset..],
            materialSlot);
        hash.AppendData(bytes);
        return OpacityMicromapContentKey.FromSha256(hash.GetHashAndReset());
    }

    private static int AlignUp(int value, int alignment) => checked(
        (value + alignment - 1) & -alignment);

    private static bool TryGetFourStateDataBytes(
        ushort subdivisionLevel,
        out int dataBytes)
    {
        uint bitShift = checked((uint)subdivisionLevel * 2U + 1U);
        if (bitShift >= sizeof(ulong) * 8U)
        {
            dataBytes = 0;
            return false;
        }

        ulong bitCount = 1UL << checked((int)bitShift);
        ulong byteCount = checked((bitCount + 7UL) / 8UL);
        if (byteCount > int.MaxValue)
        {
            dataBytes = 0;
            return false;
        }

        dataBytes = checked((int)byteCount);
        return true;
    }
}
