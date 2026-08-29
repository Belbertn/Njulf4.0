namespace Njulf.Rendering.Resources;

internal readonly record struct MeshletFrameRangeResolution(
    uint RequestedRangeIndex,
    uint ResolvedRangeIndex,
    uint FirstMeshletAddress,
    uint MeshletCount,
    int EffectiveLod,
    bool UsesFallback)
{
    internal bool IsComplete => MeshletCount != 0;

    internal static MeshletFrameRangeResolution Unavailable(
        uint requestedRangeIndex,
        int effectiveLod) =>
        new(
            requestedRangeIndex,
            uint.MaxValue,
            0,
            0,
            effectiveLod,
            false);
}

/// <summary>
/// CPU counterpart of the shader whole-range resolver. It reads only the
/// range-state snapshot recorded into the current frame command buffer.
/// </summary>
internal sealed class MeshletFrameResidencyResolver
{
    private readonly MeshletStreamingResidencyCoordinator _coordinator;
    private readonly MeshletPhysicalPageCacheUploader _uploader;

    internal MeshletFrameResidencyResolver(
        MeshletStreamingResidencyCoordinator coordinator,
        MeshletPhysicalPageCacheUploader uploader)
    {
        _coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));
        _uploader = uploader ??
            throw new ArgumentNullException(nameof(uploader));
    }

    internal ulong GetRecordedRangeStateRevision(int frameSlot) =>
        _uploader.GetRecordedRangeStateRevision(frameSlot);

    internal MeshletFrameRangeResolution Resolve(
        in MeshInfo meshInfo,
        int requestedLod,
        int frameSlot)
    {
        if (!meshInfo.UsesManagedPhysicalResidency)
        {
            throw new ArgumentException(
                "Frame residency resolution requires a managed mesh.",
                nameof(meshInfo));
        }

        int selectedLod = SelectAuthoredLod(meshInfo, requestedLod);
        uint requestedRangeIndex = checked(
            meshInfo.StreamingRangeIndex + (uint)selectedLod);
        if (!_coordinator.TryGetGlobalRangeContract(
                requestedRangeIndex,
                out GPUMeshletStreamingRange requestedRange) ||
            !IsUsable(requestedRange))
        {
            return MeshletFrameRangeResolution.Unavailable(
                requestedRangeIndex,
                selectedLod);
        }

        if (_uploader.IsRecordedRangeReady(
                requestedRangeIndex,
                frameSlot) &&
            MatchesMeshInfo(meshInfo, selectedLod, requestedRange))
        {
            return Create(
                requestedRangeIndex,
                requestedRangeIndex,
                requestedRange,
                selectedLod,
                usesFallback: false);
        }

        uint fallbackRangeIndex = requestedRange.FallbackRangeIndex;
        if (fallbackRangeIndex == uint.MaxValue ||
            !_uploader.IsRecordedRangeReady(
                fallbackRangeIndex,
                frameSlot) ||
            !_coordinator.TryGetGlobalRangeContract(
                fallbackRangeIndex,
                out GPUMeshletStreamingRange fallbackRange) ||
            !IsUsable(fallbackRange))
        {
            return MeshletFrameRangeResolution.Unavailable(
                requestedRangeIndex,
                selectedLod);
        }

        int fallbackLod = fallbackRangeIndex >=
                          meshInfo.StreamingRangeIndex &&
                          fallbackRangeIndex <=
                          meshInfo.StreamingRangeIndex + 2u
            ? checked((int)(fallbackRangeIndex -
                meshInfo.StreamingRangeIndex))
            : selectedLod;
        if (!MatchesMeshInfo(meshInfo, fallbackLod, fallbackRange))
        {
            return MeshletFrameRangeResolution.Unavailable(
                requestedRangeIndex,
                selectedLod);
        }
        return Create(
            requestedRangeIndex,
            fallbackRangeIndex,
            fallbackRange,
            fallbackLod,
            usesFallback: true);
    }

    internal int RequestRanges(ReadOnlySpan<uint> rangeIndices) =>
        _coordinator.RequestCpuRanges(
            rangeIndices,
            MeshletStreamingResidencyCoordinator.VisiblePriority);

    private static MeshletFrameRangeResolution Create(
        uint requestedRangeIndex,
        uint resolvedRangeIndex,
        in GPUMeshletStreamingRange range,
        int effectiveLod,
        bool usesFallback) =>
        new(
            requestedRangeIndex,
            resolvedRangeIndex,
            MeshletVirtualAddress.Encode(range.FirstVirtualMeshlet),
            range.MeshletCount,
            effectiveLod,
            usesFallback);

    private static bool IsUsable(
        in GPUMeshletStreamingRange range) =>
        range.PageCount != 0 && range.MeshletCount != 0;

    private static bool MatchesMeshInfo(
        in MeshInfo meshInfo,
        int lod,
        in GPUMeshletStreamingRange range)
    {
        uint expectedOffset;
        uint expectedCount;
        switch (lod)
        {
            case 2:
                expectedOffset = meshInfo.MeshletLod2Offset;
                expectedCount = meshInfo.MeshletLod2Count;
                break;
            case 1:
                expectedOffset = meshInfo.MeshletLod1Offset;
                expectedCount = meshInfo.MeshletLod1Count;
                break;
            default:
                expectedOffset = meshInfo.MeshletOffset;
                expectedCount = meshInfo.MeshletCount;
                break;
        }
        return expectedCount == range.MeshletCount &&
            MeshletVirtualAddress.IsVirtual(expectedOffset) &&
            MeshletVirtualAddress.Decode(expectedOffset) ==
                range.FirstVirtualMeshlet;
    }

    private static int SelectAuthoredLod(
        in MeshInfo meshInfo,
        int requestedLod)
    {
        int selected = Math.Clamp(requestedLod, 0, 2);
        uint count = selected switch
        {
            2 => meshInfo.MeshletLod2Count,
            1 => meshInfo.MeshletLod1Count,
            _ => meshInfo.MeshletCount
        };
        if (count != 0)
            return selected;
        if (meshInfo.MeshletCount != 0)
            return 0;
        return meshInfo.MeshletLod1Count != 0 ? 1 : 2;
    }
}
