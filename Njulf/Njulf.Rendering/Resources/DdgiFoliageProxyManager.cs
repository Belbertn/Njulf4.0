using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

public enum DdgiFoliageProxyLodTier : uint
{
    Near = 0,
    Mid = 1,
    Far = 2,
    Excluded = 3
}

/// <summary>
/// Builds a camera-independent DDGI foliage representation. Authored foliage
/// retains its qualified source mesh; procedural grass and billboard patches
/// become deterministic clustered crossed cards in frame-slot AS input buffers.
/// The renderer calls this only after the slot fence has completed, allowing a
/// slot buffer to grow without a device-wide idle.
/// </summary>
public sealed class DdgiFoliageProxyManager : IDisposable
{
    public const uint ProbeInfluenceLodPolicyVersion = 1;
    public const int BladesRepresentedPerGrassCard = 64;
    public const int BillboardsRepresentedPerCard = 16;
    public const int TrianglesPerCrossedCard = 4;
    public const int VerticesPerCrossedCard = 8;
    public const int IndicesPerCrossedCard = 12;

    private const uint InitialVertexCapacity = 256;
    private const uint InitialIndexCapacity = 384;
    private const uint InitialPatchCapacity = 16;
    private static readonly ulong VertexStride =
        (ulong)Marshal.SizeOf<GPUVertex>();
    private const ulong IndexStride = sizeof(uint);
    private static readonly ulong PatchStride =
        (ulong)Marshal.SizeOf<GPUDdgiFoliageProxyPatch>();
    private const BufferUsageFlags ProxyBufferUsage =
        BufferUsageFlags.StorageBufferBit |
        BufferUsageFlags.TransferDstBit |
        BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr |
        BufferUsageFlags.ShaderDeviceAddressBit;
    private static readonly UploadBarrierDescription ProxyUploadBarrier = new(
        PipelineStageFlags2.AccelerationStructureBuildBitKhr |
            PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.AccelerationStructureReadBitKhr |
            AccessFlags2.ShaderStorageReadBit);

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly StagingRing _stagingRing;
    private readonly ProxyBuffer[] _vertexBuffers =
        new ProxyBuffer[FramesInFlight];
    private readonly ProxyBuffer[] _indexBuffers =
        new ProxyBuffer[FramesInFlight];
    private readonly ProxyBuffer[] _patchBuffers =
        new ProxyBuffer[FramesInFlight];
    private readonly ulong[] _slotSignatures = new ulong[FramesInFlight];
    private readonly bool[] _hasSlotSignatures = new bool[FramesInFlight];
    private readonly DdgiFoliageProxyFrame[] _frames =
        new DdgiFoliageProxyFrame[FramesInFlight];
    private DdgiFoliageProxyGenerationPlan _cachedPlan =
        DdgiFoliageProxyGenerationPlan.Empty;
    private ulong _cachedPlanSignature;
    private bool _hasCachedPlan;
    private BindlessHeap? _bindlessHeap;
    private bool _disposed;

    public DdgiFoliageProxyManager(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ??
            throw new ArgumentNullException(nameof(stagingRing));

        for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
        {
            _vertexBuffers[frameIndex] = CreateBuffer(
                InitialVertexCapacity,
                VertexStride,
                $"DDGI Foliage Proxy Vertices Frame{frameIndex}");
            _indexBuffers[frameIndex] = CreateBuffer(
                InitialIndexCapacity,
                IndexStride,
                $"DDGI Foliage Proxy Indices Frame{frameIndex}");
            _patchBuffers[frameIndex] = CreateBuffer(
                InitialPatchCapacity,
                PatchStride,
                $"DDGI Foliage Proxy Patches Frame{frameIndex}",
                BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false);
            _frames[frameIndex] = DdgiFoliageProxyFrame.Empty(frameIndex);
        }
    }

    public void Register(BindlessHeap bindlessHeap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bindlessHeap = bindlessHeap ??
            throw new ArgumentNullException(nameof(bindlessHeap));
        RegisterCurrentBuffers();
    }

    public DdgiFoliageProxyFrame PrepareFrame(
        Scene scene,
        DdgiFoliageGeometryMode mode,
        int triangleBudget,
        int updateCadenceFrames,
        ulong frameSerial,
        float windTimeSeconds,
        float densityScale,
        bool proceduralGenerationAvailable,
        string? proceduralGenerationFailureReason,
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        ValidateFrameIndex(frameIndex);
        if (commandBuffer.Handle == 0)
            throw new ArgumentException(
                "A valid command buffer is required for foliage proxy uploads.",
                nameof(commandBuffer));
        if (!float.IsFinite(windTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(windTimeSeconds));
        if (!float.IsFinite(densityScale) || densityScale < 0f)
            throw new ArgumentOutOfRangeException(nameof(densityScale));

        int cadence = Math.Max(1, updateCadenceFrames);
        ulong cadenceGeneration = frameSerial / unchecked((ulong)cadence);
        int boundedBudget = Math.Clamp(triangleBudget, 0, 4_000_000);
        ulong signature = CreateFrameSignature(
            scene.FoliagePatches,
            scene.GlobalIlluminationProbeVolumes,
            mode,
            boundedBudget,
            cadenceGeneration,
            densityScale,
            proceduralGenerationAvailable);
        if (_hasSlotSignatures[frameIndex] &&
            _slotSignatures[frameIndex] == signature)
        {
            DdgiFoliageProxyFrame unchanged = _frames[frameIndex] with
            {
                UpdatedThisFrame = false,
                UploadedBytes = 0,
                CpuBuildMicroseconds = 0,
                CpuUploadMicroseconds = 0
            };
            _frames[frameIndex] = unchanged;
            return unchanged;
        }

        long buildStart = Stopwatch.GetTimestamp();
        DdgiFoliageProxyGenerationPlan plan;
        if (_hasCachedPlan && _cachedPlanSignature == signature)
        {
            plan = _cachedPlan;
        }
        else
        {
            // The CPU admits stable patches and uploads only compact generation
            // records. Card vertices and indices are expanded by compute after
            // this slot's fence and before its dynamic BLAS build.
            plan = BuildGenerationPlan(
                scene.FoliagePatches,
                mode,
                boundedBudget,
                cadenceGeneration,
                densityScale,
                proceduralGenerationAvailable,
                proceduralGenerationFailureReason,
                scene.GlobalIlluminationProbeVolumes);
            _cachedPlan = plan;
            _cachedPlanSignature = signature;
            _hasCachedPlan = true;
        }
        long cpuBuildMicroseconds = ElapsedMicroseconds(buildStart);

        if (!TryEnsureFrameCapacity(
                frameIndex,
                checked((uint)(plan.CardCount * VerticesPerCrossedCard)),
                checked((uint)(plan.CardCount * IndicesPerCrossedCard)),
                checked((uint)plan.Patches.Length),
                out string capacityFailure))
        {
            DdgiFoliageProxyFrame fallback = _frames[frameIndex] with
            {
                UpdatedThisFrame = false,
                UploadedBytes = 0,
                CpuBuildMicroseconds = cpuBuildMicroseconds,
                CpuUploadMicroseconds = 0,
                FallbackReason = capacityFailure
            };
            _frames[frameIndex] = fallback;
            return fallback;
        }
        RegisterCurrentBuffers();

        long uploadStart = Stopwatch.GetTimestamp();
        ulong uploadedBytes = 0;
        if (plan.Patches.Length > 0)
        {
            uploadedBytes += GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _patchBuffers[frameIndex].Handle,
                plan.Patches.AsSpan(),
                barrierDescription: ProxyUploadBarrier).ByteCount;
        }
        long cpuUploadMicroseconds = ElapsedMicroseconds(uploadStart);

        uint vertexBufferIndex = checked((uint)(frameIndex == 0
            ? BindlessIndex.DdgiFoliageProxyVertexBuffer
            : BindlessIndex.DdgiFoliageProxyVertexBufferFrame1));
        uint indexBufferIndex = checked((uint)(frameIndex == 0
            ? BindlessIndex.DdgiFoliageProxyIndexBuffer
            : BindlessIndex.DdgiFoliageProxyIndexBufferFrame1));
        uint patchBufferIndex = checked((uint)(frameIndex == 0
            ? BindlessIndex.DdgiFoliageProxyPatchBuffer
            : BindlessIndex.DdgiFoliageProxyPatchBufferFrame1));
        var instances = new DdgiFoliageProxyInstance[plan.Instances.Length];
        for (int index = 0; index < instances.Length; index++)
        {
            DdgiFoliageProxyInstance instance = plan.Instances[index];
            instances[index] = instance.Generated
                ? instance with
                {
                    VertexBuffer = _vertexBuffers[frameIndex].Handle,
                    IndexBuffer = _indexBuffers[frameIndex].Handle,
                    VertexBufferIndex = vertexBufferIndex,
                    IndexBufferIndex = indexBufferIndex,
                    FrameSlot = frameIndex
                }
                : instance with { FrameSlot = frameIndex };
        }

        var frame = new DdgiFoliageProxyFrame(
            frameIndex,
            cadenceGeneration,
            instances,
            checked(plan.CardCount * VerticesPerCrossedCard),
            checked(plan.CardCount * TrianglesPerCrossedCard),
            plan.AuthoredInstanceCount,
            plan.GeneratedInstanceCount,
            plan.DroppedTriangleCount,
            plan.EstimatedRepresentedBladeCount,
            uploadedBytes,
            _vertexBuffers[frameIndex].ByteSize,
            _indexBuffers[frameIndex].ByteSize,
            signature,
            ComputeInfluenceBounds(instances),
            UpdatedThisFrame: true,
            cpuBuildMicroseconds,
            cpuUploadMicroseconds,
            plan.FallbackReason)
        {
            PatchBuffer = _patchBuffers[frameIndex].Handle,
            PatchBufferIndex = patchBufferIndex,
            PatchCount = plan.Patches.Length,
            PatchBufferBytes = _patchBuffers[frameIndex].ByteSize,
            VertexBuffer = _vertexBuffers[frameIndex].Handle,
            IndexBuffer = _indexBuffers[frameIndex].Handle,
            VertexBufferIndex = vertexBufferIndex,
            IndexBufferIndex = indexBufferIndex,
            WindTimeSeconds = windTimeSeconds,
            RequestedRepresentedInstanceCount =
                plan.RequestedRepresentedInstanceCount,
            DensityError = plan.DensityError,
            NearCardCount = plan.NearCardCount,
            MidCardCount = plan.MidCardCount,
            FarCardCount = plan.FarCardCount,
            ExcludedPatchCount = plan.ExcludedPatchCount,
            LodPolicyVersion = ProbeInfluenceLodPolicyVersion
        };
        _frames[frameIndex] = frame;
        _slotSignatures[frameIndex] = signature;
        _hasSlotSignatures[frameIndex] = true;
        return frame;
    }

    internal static DdgiFoliageProxyGenerationPlan BuildGenerationPlan(
        IReadOnlyList<FoliagePatch> patches,
        DdgiFoliageGeometryMode mode,
        int triangleBudget,
        ulong cadenceGeneration,
        float densityScale,
        bool proceduralGenerationAvailable,
        string? proceduralGenerationFailureReason = null,
        IReadOnlyList<GlobalIlluminationProbeVolume>? probeVolumes = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (triangleBudget < 0 || triangleBudget > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(triangleBudget));
        if (!float.IsFinite(densityScale) || densityScale < 0f)
            throw new ArgumentOutOfRangeException(nameof(densityScale));
        if (mode == DdgiFoliageGeometryMode.Excluded)
            return DdgiFoliageProxyGenerationPlan.Empty;

        List<FoliagePatch> ordered = CollectOrderedPatches(
            patches,
            probeVolumes);
        var gpuPatches = new List<GPUDdgiFoliageProxyPatch>();
        var instances = new List<DdgiFoliageProxyInstance>();
        int remainingTriangles = triangleBudget;
        int droppedTriangles = 0;
        int cardCount = 0;
        int authoredCount = 0;
        int generatedCount = 0;
        int representedInstanceCount = 0;
        int requestedRepresentedInstanceCount = 0;
        int unavailableTriangleCount = 0;
        int nearCardCount = 0;
        int midCardCount = 0;
        int farCardCount = 0;
        int excludedPatchCount = 0;

        for (int patchIndex = 0; patchIndex < ordered.Count; patchIndex++)
        {
            FoliagePatch patch = ordered[patchIndex];
            FoliagePrototype prototype = patch.Prototype;
            DdgiFoliageProxyLodTier lodTier = ClassifyLodTier(
                patch.Bounds,
                probeVolumes);
            if (lodTier == DdgiFoliageProxyLodTier.Excluded)
            {
                excludedPatchCount++;
                if (prototype.GeometryMode != FoliageGeometryMode.AuthoredMeshlets)
                {
                    int representedPerExcludedCard = prototype.GeometryMode ==
                        FoliageGeometryMode.BillboardCards
                            ? BillboardsRepresentedPerCard
                            : BladesRepresentedPerGrassCard;
                    int excludedCards = EstimateProxyCardCount(
                        patch,
                        representedPerExcludedCard,
                        densityScale);
                    requestedRepresentedInstanceCount = SaturatingAdd(
                        requestedRepresentedInstanceCount,
                        SaturatingMultiply(
                            excludedCards,
                            representedPerExcludedCard));
                    droppedTriangles = SaturatingAdd(
                        droppedTriangles,
                        SaturatingMultiply(
                            excludedCards,
                            TrianglesPerCrossedCard));
                }
                continue;
            }
            if (prototype.GeometryMode == FoliageGeometryMode.AuthoredMeshlets &&
                prototype.Mesh is MeshHandle authoredMesh &&
                authoredMesh.IsValid)
            {
                instances.Add(DdgiFoliageProxyInstance.Authored(
                    patch,
                    authoredMesh,
                    CreateAuthoredTransform(patch),
                    cadenceGeneration,
                    lodTier));
                authoredCount++;
                continue;
            }

            if (mode != DdgiFoliageGeometryMode.AuthoredAndProceduralProxy)
                continue;

            int representedPerCard = prototype.GeometryMode ==
                FoliageGeometryMode.BillboardCards
                    ? BillboardsRepresentedPerCard
                    : BladesRepresentedPerGrassCard;
            int fullDensityCards = EstimateProxyCardCount(
                patch,
                representedPerCard,
                densityScale);
            requestedRepresentedInstanceCount = SaturatingAdd(
                requestedRepresentedInstanceCount,
                SaturatingMultiply(fullDensityCards, representedPerCard));
            float lodDensityScale = LodDensityScale(lodTier);
            int requestedCards = ScaleCardCount(
                fullDensityCards,
                lodDensityScale);
            int representedPerAdmittedCard = Math.Max(
                representedPerCard,
                checked((int)Math.Ceiling(
                    representedPerCard / (double)lodDensityScale)));
            int requestedTriangles = requestedCards >
                int.MaxValue / TrianglesPerCrossedCard
                    ? int.MaxValue
                    : requestedCards * TrianglesPerCrossedCard;
            if (!proceduralGenerationAvailable)
            {
                droppedTriangles = SaturatingAdd(
                    droppedTriangles,
                    requestedTriangles);
                unavailableTriangleCount = SaturatingAdd(
                    unavailableTriangleCount,
                    requestedTriangles);
                continue;
            }

            int admittedCards = Math.Min(
                requestedCards,
                remainingTriangles / TrianglesPerCrossedCard);
            int admittedTriangles = checked(
                admittedCards * TrianglesPerCrossedCard);
            remainingTriangles -= admittedTriangles;
            droppedTriangles = SaturatingAdd(
                droppedTriangles,
                requestedTriangles - admittedTriangles);
            if (admittedCards == 0)
                continue;

            int patchCardOffset = cardCount;
            cardCount = checked(cardCount + admittedCards);
            gpuPatches.Add(CreateGenerationPatch(
                patch,
                admittedCards,
                representedPerAdmittedCard,
                checked((uint)patchCardOffset),
                lodTier));
            instances.Add(DdgiFoliageProxyInstance.Procedural(
                patch,
                checked((uint)(patchCardOffset * VerticesPerCrossedCard)),
                checked((uint)(admittedCards * VerticesPerCrossedCard)),
                checked((uint)(patchCardOffset * IndicesPerCrossedCard)),
                checked((uint)(admittedCards * IndicesPerCrossedCard)),
                cadenceGeneration,
                lodTier));
            generatedCount++;
            representedInstanceCount = SaturatingAdd(
                representedInstanceCount,
                Math.Min(
                    SaturatingMultiply(
                        admittedCards,
                        representedPerAdmittedCard),
                    SaturatingMultiply(
                        fullDensityCards,
                        representedPerCard)));
            switch (lodTier)
            {
                case DdgiFoliageProxyLodTier.Near:
                    nearCardCount = SaturatingAdd(nearCardCount, admittedCards);
                    break;
                case DdgiFoliageProxyLodTier.Mid:
                    midCardCount = SaturatingAdd(midCardCount, admittedCards);
                    break;
                case DdgiFoliageProxyLodTier.Far:
                    farCardCount = SaturatingAdd(farCardCount, admittedCards);
                    break;
            }
        }

        string fallbackReason = string.Empty;
        if (unavailableTriangleCount > 0)
        {
            string detail = string.IsNullOrWhiteSpace(
                proceduralGenerationFailureReason)
                    ? "compute generation is unavailable"
                    : proceduralGenerationFailureReason.Trim();
            fallbackReason =
                $"DDGI foliage procedural proxies excluded because {detail}; " +
                $"{unavailableTriangleCount} requested triangles were omitted.";
        }
        int budgetDroppedTriangles = Math.Max(
            0,
            droppedTriangles - unavailableTriangleCount);
        if (budgetDroppedTriangles > 0)
        {
            fallbackReason = AppendFallbackReason(
                fallbackReason,
                $"DDGI foliage proxy triangle budget exhausted; " +
                $"{budgetDroppedTriangles} lowest-priority stable proxy triangles were omitted.");
        }
        if (excludedPatchCount > 0)
        {
            fallbackReason = AppendFallbackReason(
                fallbackReason,
                $"{excludedPatchCount} stable patch(es) outside authored " +
                "probe influence were excluded by the world-space LOD policy.");
        }
        float densityError = requestedRepresentedInstanceCount <= 0
            ? 0f
            : Math.Clamp(
                MathF.Abs(
                    representedInstanceCount /
                        (float)requestedRepresentedInstanceCount - 1f),
                0f,
                1f);

        return new DdgiFoliageProxyGenerationPlan(
            gpuPatches.ToArray(),
            instances.ToArray(),
            cardCount,
            authoredCount,
            generatedCount,
            droppedTriangles,
            representedInstanceCount,
            fallbackReason)
        {
            RequestedRepresentedInstanceCount =
                requestedRepresentedInstanceCount,
            DensityError = densityError,
            NearCardCount = nearCardCount,
            MidCardCount = midCardCount,
            FarCardCount = farCardCount,
            ExcludedPatchCount = excludedPatchCount
        };
    }

    /// <summary>
    /// Deterministic camera-free CPU oracle used by unit and statistical
    /// qualification. Production uploads compact patch records and expands the
    /// same construction in <c>ddgi_foliage_proxy_generate.comp</c>.
    /// </summary>
    public static DdgiFoliageProxyBuild BuildReference(
        IReadOnlyList<FoliagePatch> patches,
        DdgiFoliageGeometryMode mode,
        int triangleBudget,
        ulong cadenceGeneration,
        float windTimeSeconds,
        float densityScale = 1f,
        IReadOnlyList<GlobalIlluminationProbeVolume>? probeVolumes = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (triangleBudget < 0 || triangleBudget > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(triangleBudget));
        if (!float.IsFinite(windTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(windTimeSeconds));
        if (!float.IsFinite(densityScale) || densityScale < 0f)
            throw new ArgumentOutOfRangeException(nameof(densityScale));

        if (mode == DdgiFoliageGeometryMode.Excluded)
            return DdgiFoliageProxyBuild.Empty;

        List<FoliagePatch> ordered = CollectOrderedPatches(
            patches,
            probeVolumes);

        var vertices = new List<GPUVertex>();
        var indices = new List<uint>();
        var instances = new List<DdgiFoliageProxyInstance>();
        int remainingTriangles = triangleBudget;
        int droppedTriangles = 0;
        int authoredCount = 0;
        int generatedCount = 0;
        int representedBladeCount = 0;
        int requestedRepresentedInstanceCount = 0;
        int nearCardCount = 0;
        int midCardCount = 0;
        int farCardCount = 0;
        int excludedPatchCount = 0;

        for (int patchIndex = 0; patchIndex < ordered.Count; patchIndex++)
        {
            FoliagePatch patch = ordered[patchIndex];
            FoliagePrototype prototype = patch.Prototype;
            DdgiFoliageProxyLodTier lodTier = ClassifyLodTier(
                patch.Bounds,
                probeVolumes);
            if (lodTier == DdgiFoliageProxyLodTier.Excluded)
            {
                excludedPatchCount++;
                if (prototype.GeometryMode != FoliageGeometryMode.AuthoredMeshlets)
                {
                    int representedPerExcludedCard = prototype.GeometryMode ==
                        FoliageGeometryMode.BillboardCards
                            ? BillboardsRepresentedPerCard
                            : BladesRepresentedPerGrassCard;
                    int excludedCards = EstimateProxyCardCount(
                        patch,
                        representedPerExcludedCard,
                        densityScale);
                    requestedRepresentedInstanceCount = SaturatingAdd(
                        requestedRepresentedInstanceCount,
                        SaturatingMultiply(
                            excludedCards,
                            representedPerExcludedCard));
                    droppedTriangles = SaturatingAdd(
                        droppedTriangles,
                        SaturatingMultiply(
                            excludedCards,
                            TrianglesPerCrossedCard));
                }
                continue;
            }
            if (prototype.GeometryMode == FoliageGeometryMode.AuthoredMeshlets &&
                prototype.Mesh is MeshHandle authoredMesh &&
                authoredMesh.IsValid)
            {
                instances.Add(DdgiFoliageProxyInstance.Authored(
                    patch,
                    authoredMesh,
                    CreateAuthoredTransform(patch),
                    cadenceGeneration,
                    lodTier));
                authoredCount++;
                continue;
            }

            if (mode != DdgiFoliageGeometryMode.AuthoredAndProceduralProxy)
                continue;

            int representedPerCard = prototype.GeometryMode ==
                FoliageGeometryMode.BillboardCards
                    ? BillboardsRepresentedPerCard
                    : BladesRepresentedPerGrassCard;
            int fullDensityCards = EstimateProxyCardCount(
                patch,
                representedPerCard,
                densityScale);
            requestedRepresentedInstanceCount = SaturatingAdd(
                requestedRepresentedInstanceCount,
                SaturatingMultiply(fullDensityCards, representedPerCard));
            float lodDensityScale = LodDensityScale(lodTier);
            int requestedCards = ScaleCardCount(
                fullDensityCards,
                lodDensityScale);
            int representedPerAdmittedCard = Math.Max(
                representedPerCard,
                checked((int)Math.Ceiling(
                    representedPerCard / (double)lodDensityScale)));
            int requestedTriangles = requestedCards >
                int.MaxValue / TrianglesPerCrossedCard
                    ? int.MaxValue
                    : requestedCards * TrianglesPerCrossedCard;
            int admittedCards = Math.Min(
                requestedCards,
                remainingTriangles / TrianglesPerCrossedCard);
            int admittedTriangles = checked(
                admittedCards * TrianglesPerCrossedCard);
            remainingTriangles -= admittedTriangles;
            droppedTriangles = SaturatingAdd(
                droppedTriangles,
                requestedTriangles - admittedTriangles);
            if (admittedCards == 0)
                continue;

            int vertexOffset = vertices.Count;
            int indexOffset = indices.Count;
            GeneratePatchCards(
                patch,
                admittedCards,
                representedPerAdmittedCard,
                cadenceGeneration,
                windTimeSeconds,
                vertices,
                indices);
            int vertexCount = vertices.Count - vertexOffset;
            int indexCount = indices.Count - indexOffset;
            instances.Add(DdgiFoliageProxyInstance.Procedural(
                patch,
                checked((uint)vertexOffset),
                checked((uint)vertexCount),
                checked((uint)indexOffset),
                checked((uint)indexCount),
                cadenceGeneration,
                lodTier));
            generatedCount++;
            representedBladeCount = SaturatingAdd(
                representedBladeCount,
                Math.Min(
                    SaturatingMultiply(
                        admittedCards,
                        representedPerAdmittedCard),
                    SaturatingMultiply(
                        fullDensityCards,
                        representedPerCard)));
            switch (lodTier)
            {
                case DdgiFoliageProxyLodTier.Near:
                    nearCardCount = SaturatingAdd(nearCardCount, admittedCards);
                    break;
                case DdgiFoliageProxyLodTier.Mid:
                    midCardCount = SaturatingAdd(midCardCount, admittedCards);
                    break;
                case DdgiFoliageProxyLodTier.Far:
                    farCardCount = SaturatingAdd(farCardCount, admittedCards);
                    break;
            }
        }

        return new DdgiFoliageProxyBuild(
            vertices.ToArray(),
            indices.ToArray(),
            instances.ToArray(),
            authoredCount,
            generatedCount,
            droppedTriangles,
            representedBladeCount)
        {
            RequestedRepresentedInstanceCount =
                requestedRepresentedInstanceCount,
            DensityError = requestedRepresentedInstanceCount <= 0
                ? 0f
                : Math.Clamp(
                    MathF.Abs(
                        representedBladeCount /
                            (float)requestedRepresentedInstanceCount - 1f),
                    0f,
                    1f),
            NearCardCount = nearCardCount,
            MidCardCount = midCardCount,
            FarCardCount = farCardCount,
            ExcludedPatchCount = excludedPatchCount
        };
    }

    internal static int EstimateProxyCardCount(
        FoliagePatch patch,
        int representedInstancesPerCard) =>
        EstimateProxyCardCount(
            patch,
            representedInstancesPerCard,
            densityScale: 1f);

    internal static int EstimateProxyCardCount(
        FoliagePatch patch,
        int representedInstancesPerCard,
        float densityScale)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (representedInstancesPerCard <= 0)
            throw new ArgumentOutOfRangeException(nameof(representedInstancesPerCard));
        if (!float.IsFinite(densityScale) || densityScale < 0f)
            throw new ArgumentOutOfRangeException(nameof(densityScale));
        Vector3 size = patch.Bounds.Size;
        double area = Math.Max(0f, size.X) * Math.Max(0f, size.Z);
        double instances = Math.Ceiling(area * patch.Density * densityScale);
        if (instances <= 0d)
            return 0;
        double cards = Math.Ceiling(instances / representedInstancesPerCard);
        return cards >= int.MaxValue ? int.MaxValue : checked((int)cards);
    }

    private static List<FoliagePatch> CollectOrderedPatches(
        IReadOnlyList<FoliagePatch> patches,
        IReadOnlyList<GlobalIlluminationProbeVolume>? probeVolumes = null)
    {
        var ordered = new List<FoliagePatch>(patches.Count);
        for (int index = 0; index < patches.Count; index++)
        {
            FoliagePatch patch = patches[index] ??
                throw new ArgumentException(
                    "Foliage patch collections cannot contain null entries.",
                    nameof(patches));
            if (patch.Visible && patch.Density > 0f)
                ordered.Add(patch);
        }
        ordered.Sort((left, right) =>
        {
            int tier = ClassifyLodTier(left.Bounds, probeVolumes).CompareTo(
                ClassifyLodTier(right.Bounds, probeVolumes));
            return tier != 0 ? tier : left.Id.CompareTo(right.Id);
        });
        return ordered;
    }

    internal static DdgiFoliageProxyLodTier ClassifyLodTier(
        BoundingBox patchBounds,
        IReadOnlyList<GlobalIlluminationProbeVolume>? probeVolumes)
    {
        if (probeVolumes == null || probeVolumes.Count == 0)
            return DdgiFoliageProxyLodTier.Near;

        bool hasEnabledVolume = false;
        DdgiFoliageProxyLodTier best = DdgiFoliageProxyLodTier.Excluded;
        for (int index = 0; index < probeVolumes.Count; index++)
        {
            GlobalIlluminationProbeVolume volume = probeVolumes[index] ??
                throw new ArgumentException(
                    "Probe-volume collections cannot contain null entries.",
                    nameof(probeVolumes));
            if (!volume.Enabled)
                continue;
            hasEnabledVolume = true;

            Vector3 spacing = volume.ProbeSpacing;
            float maximumSpacing = MathF.Max(
                spacing.X,
                MathF.Max(spacing.Y, spacing.Z));
            float nearDistance = MathF.Max(
                volume.BlendDistance,
                maximumSpacing * 2f);
            float midDistance = MathF.Max(
                nearDistance,
                volume.MaxRayDistance);
            float farDistance = MathF.Max(
                midDistance + maximumSpacing,
                volume.MaxRayDistance * 2f);
            float distance = DistanceBetweenBounds(
                patchBounds,
                volume.Bounds);
            DdgiFoliageProxyLodTier candidate = distance <= nearDistance
                ? DdgiFoliageProxyLodTier.Near
                : distance <= midDistance
                    ? DdgiFoliageProxyLodTier.Mid
                    : distance <= farDistance
                        ? DdgiFoliageProxyLodTier.Far
                        : DdgiFoliageProxyLodTier.Excluded;
            if (candidate < best)
                best = candidate;
        }

        // Runtime camera rings are not authored scene data. When no authored
        // volume is enabled, retain full density rather than coupling GI
        // geometry to the camera.
        return hasEnabledVolume ? best : DdgiFoliageProxyLodTier.Near;
    }

    private static float DistanceBetweenBounds(
        BoundingBox left,
        BoundingBox right)
    {
        float dx = AxisSeparation(
            left.Min.X,
            left.Max.X,
            right.Min.X,
            right.Max.X);
        float dy = AxisSeparation(
            left.Min.Y,
            left.Max.Y,
            right.Min.Y,
            right.Max.Y);
        float dz = AxisSeparation(
            left.Min.Z,
            left.Max.Z,
            right.Min.Z,
            right.Max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float AxisSeparation(
        float leftMinimum,
        float leftMaximum,
        float rightMinimum,
        float rightMaximum)
    {
        if (leftMaximum < rightMinimum)
            return rightMinimum - leftMaximum;
        return rightMaximum < leftMinimum
            ? leftMinimum - rightMaximum
            : 0f;
    }

    private static float LodDensityScale(DdgiFoliageProxyLodTier tier) =>
        tier switch
        {
            DdgiFoliageProxyLodTier.Near => 1f,
            DdgiFoliageProxyLodTier.Mid => 0.5f,
            DdgiFoliageProxyLodTier.Far => 0.25f,
            _ => 0f
        };

    private static int ScaleCardCount(int fullDensityCards, float scale)
    {
        if (fullDensityCards <= 0 || scale <= 0f)
            return 0;
        double scaled = Math.Ceiling(fullDensityCards * (double)scale);
        return scaled >= int.MaxValue
            ? int.MaxValue
            : checked((int)scaled);
    }

    private static GPUDdgiFoliageProxyPatch CreateGenerationPatch(
        FoliagePatch patch,
        int cardCount,
        int representedPerCard,
        uint cardOffset,
        DdgiFoliageProxyLodTier lodTier)
    {
        Vector3 size = patch.Bounds.Size;
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cardCount)));
        int rows = Math.Max(1, (cardCount + columns - 1) / columns);
        float cellX = size.X / columns;
        float cellZ = size.Z / rows;
        float baseWidth = Math.Max(0.005f, patch.Prototype.CardWidth) *
            Math.Max(0.0001f, patch.InstanceScale);
        float height = Math.Max(0.01f, patch.Prototype.CardHeight) *
            Math.Max(0.0001f, patch.InstanceScale);
        float clusterWidth = Math.Min(
            Math.Max(
                baseWidth,
                MathF.Sqrt(Math.Max(1, representedPerCard)) * baseWidth),
            Math.Max(
                baseWidth,
                Math.Min(Math.Abs(cellX), Math.Abs(cellZ)) * 0.8f));
        ulong prototypeKey = StableGuidKey(patch.Prototype.Id);
        ulong patchKey = Hash(StableGuidKey(patch.Id), patch.Seed);
        patchKey = Hash(patchKey, unchecked((uint)prototypeKey));
        patchKey = Hash(patchKey, unchecked((uint)(prototypeKey >> 32)));
        float representedFraction = Math.Clamp(
            representedPerCard / (float)BladesRepresentedPerGrassCard,
            0.05f,
            1f);

        return new GPUDdgiFoliageProxyPatch
        {
            BoundsMinimumAndClusterWidth = new System.Numerics.Vector4(
                patch.Bounds.Min.X,
                patch.Bounds.Min.Y,
                patch.Bounds.Min.Z,
                clusterWidth),
            BoundsMaximumAndCardHeight = new System.Numerics.Vector4(
                patch.Bounds.Max.X,
                patch.Bounds.Max.Y,
                patch.Bounds.Max.Z,
                height),
            WindAndCoverage = new System.Numerics.Vector4(
                patch.Prototype.Wind.Strength,
                patch.Prototype.Wind.Frequency,
                patch.Prototype.Wind.Flutter,
                representedFraction),
            StablePatchKeyLow = unchecked((uint)patchKey),
            StablePatchKeyHigh = unchecked((uint)(patchKey >> 32)),
            CardOffset = cardOffset,
            CardCount = checked((uint)cardCount),
            GridColumns = checked((uint)columns),
            GridRows = checked((uint)rows),
            RepresentedInstancesPerCard = checked((uint)representedPerCard),
            Flags = (patch.Prototype.GeometryMode ==
                FoliageGeometryMode.BillboardCards
                    ? 1u
                    : 0u) | ((uint)lodTier << 8)
        };
    }

    private static string AppendFallbackReason(
        string existing,
        string addition) =>
        string.IsNullOrWhiteSpace(existing)
            ? addition
            : $"{existing} {addition}";

    private static void GeneratePatchCards(
        FoliagePatch patch,
        int cardCount,
        int representedPerCard,
        ulong cadenceGeneration,
        float windTimeSeconds,
        List<GPUVertex> vertices,
        List<uint> indices)
    {
        Vector3 size = patch.Bounds.Size;
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cardCount)));
        int rows = Math.Max(1, (cardCount + columns - 1) / columns);
        float cellX = size.X / columns;
        float cellZ = size.Z / rows;
        float baseWidth = Math.Max(0.005f, patch.Prototype.CardWidth) *
            Math.Max(0.0001f, patch.InstanceScale);
        float height = Math.Max(0.01f, patch.Prototype.CardHeight) *
            Math.Max(0.0001f, patch.InstanceScale);
        float clusterWidth = Math.Min(
            Math.Max(baseWidth, MathF.Sqrt(Math.Max(1, representedPerCard)) * baseWidth),
            Math.Max(baseWidth, Math.Min(Math.Abs(cellX), Math.Abs(cellZ)) * 0.8f));
        ulong prototypeKey = StableGuidKey(patch.Prototype.Id);
        ulong patchKey = Hash(StableGuidKey(patch.Id), patch.Seed);
        patchKey = Hash(patchKey, unchecked((uint)prototypeKey));
        patchKey = Hash(patchKey, unchecked((uint)(prototypeKey >> 32)));

        for (int cardIndex = 0; cardIndex < cardCount; cardIndex++)
        {
            int x = cardIndex % columns;
            int z = cardIndex / columns;
            float randomX = StableUnit(patchKey, cardIndex, 0x11u);
            float randomZ = StableUnit(patchKey, cardIndex, 0x23u);
            float randomYaw = StableUnit(patchKey, cardIndex, 0x37u);
            float randomScale = 0.85f +
                StableUnit(patchKey, cardIndex, 0x41u) * 0.3f;
            float centerX = patch.Bounds.Min.X +
                (x + 0.15f + randomX * 0.7f) * cellX;
            float centerZ = patch.Bounds.Min.Z +
                (z + 0.15f + randomZ * 0.7f) * cellZ;
            float centerY = patch.Bounds.Min.Y;
            float yaw = randomYaw * MathF.Tau;
            float cardHeight = height * randomScale;
            float halfWidth = clusterWidth * randomScale * 0.5f;
            float windPhase =
                (centerX * 0.113f + centerZ * 0.173f) +
                windTimeSeconds * patch.Prototype.Wind.Frequency * MathF.Tau;
            float bendMagnitude = patch.Prototype.Wind.Strength *
                cardHeight * 0.25f;
            float flutter = patch.Prototype.Wind.Flutter *
                cardHeight * 0.05f *
                MathF.Sin(windPhase * 2.37f + randomYaw * MathF.Tau);
            Vector3 bend = new(
                MathF.Sin(windPhase) * bendMagnitude + flutter,
                0f,
                MathF.Cos(windPhase * 0.83f) * bendMagnitude - flutter);
            float representedFraction = Math.Clamp(
                representedPerCard /
                    (float)BladesRepresentedPerGrassCard,
                0.05f,
                1f);
            Vector4 color = new(1f, 1f, 1f, representedFraction);
            uint cardLocalVertexBase = checked(
                (uint)cardIndex * (uint)VerticesPerCrossedCard);

            AddCrossedQuad(
                new Vector3(centerX, centerY, centerZ),
                yaw,
                halfWidth,
                cardHeight,
                bend,
                color,
                cardLocalVertexBase,
                vertices,
                indices);
            AddCrossedQuad(
                new Vector3(centerX, centerY, centerZ),
                yaw + MathF.PI * 0.5f,
                halfWidth,
                cardHeight,
                bend,
                color,
                cardLocalVertexBase + 4u,
                vertices,
                indices);
        }
    }

    private static void AddCrossedQuad(
        Vector3 bottomCenter,
        float yaw,
        float halfWidth,
        float height,
        Vector3 topBend,
        Vector4 color,
        uint patchLocalVertexBase,
        List<GPUVertex> vertices,
        List<uint> indices)
    {
        Vector3 right = new(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 normal = new(-right.Z, 0f, right.X);
        Vector3 topCenter = bottomCenter + new Vector3(0f, height, 0f) + topBend;
        vertices.Add(CreateVertex(bottomCenter - right * halfWidth, normal, right, 0f, 0f, color));
        vertices.Add(CreateVertex(bottomCenter + right * halfWidth, normal, right, 1f, 0f, color));
        vertices.Add(CreateVertex(topCenter + right * halfWidth, normal, right, 1f, 1f, color));
        vertices.Add(CreateVertex(topCenter - right * halfWidth, normal, right, 0f, 1f, color));
        indices.Add(patchLocalVertexBase + 0u);
        indices.Add(patchLocalVertexBase + 1u);
        indices.Add(patchLocalVertexBase + 2u);
        indices.Add(patchLocalVertexBase + 0u);
        indices.Add(patchLocalVertexBase + 2u);
        indices.Add(patchLocalVertexBase + 3u);
    }

    private static GPUVertex CreateVertex(
        Vector3 position,
        Vector3 normal,
        Vector3 tangent,
        float u,
        float v,
        Vector4 color) =>
        new()
        {
            Position = position,
            Normal = normal,
            TexCoord = new Vector2(u, v),
            TexCoord2 = new Vector2(u, v),
            Tangent = new Vector4(tangent.X, tangent.Y, tangent.Z, 1f),
            Color = color
        };

    private static Matrix4x4 CreateAuthoredTransform(FoliagePatch patch) =>
        Matrix4x4.CreateScale(new Vector3(patch.InstanceScale)) *
        Matrix4x4.CreateTranslation(patch.InstancePosition);

    private static ulong CreateFrameSignature(
        IReadOnlyList<FoliagePatch> patches,
        IReadOnlyList<GlobalIlluminationProbeVolume>? probeVolumes,
        DdgiFoliageGeometryMode mode,
        int triangleBudget,
        ulong cadenceGeneration,
        float densityScale,
        bool proceduralGenerationAvailable)
    {
        ulong hash = 14695981039346656037UL;
        hash = Hash(hash, ProbeInfluenceLodPolicyVersion);
        hash = Hash(hash, (uint)mode);
        hash = Hash(hash, unchecked((uint)triangleBudget));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(densityScale));
        hash = Hash(hash, proceduralGenerationAvailable ? 1u : 0u);
        var ordered = new List<FoliagePatch>(patches.Count);
        for (int index = 0; index < patches.Count; index++)
        {
            ordered.Add(patches[index] ??
                throw new ArgumentException(
                    "Foliage patch collections cannot contain null entries.",
                    nameof(patches)));
        }
        ordered.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        for (int index = 0; index < ordered.Count; index++)
        {
            FoliagePatch patch = ordered[index];
            ulong key = StableGuidKey(patch.Id);
            hash = Hash(hash, (uint)key);
            hash = Hash(hash, (uint)(key >> 32));
            hash = Hash(hash, patch.ContentRevision);
            hash = Hash(hash, patch.Visible ? 1u : 0u);
            hash = Hash(hash, patch.Seed);
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(patch.Density));
            hash = HashVector(hash, patch.Bounds.Min);
            hash = HashVector(hash, patch.Bounds.Max);
            hash = HashVector(hash, patch.InstancePosition);
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(patch.InstanceScale));
            ulong prototypeKey = StableGuidKey(patch.Prototype.Id);
            hash = Hash(hash, unchecked((uint)prototypeKey));
            hash = Hash(hash, unchecked((uint)(prototypeKey >> 32)));
            hash = Hash(hash, (uint)patch.Prototype.GeometryMode);
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(
                patch.Prototype.CardHeight));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(
                patch.Prototype.CardWidth));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(
                patch.Prototype.Wind.Strength));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(
                patch.Prototype.Wind.Frequency));
            hash = Hash(hash, BitConverter.SingleToUInt32Bits(
                patch.Prototype.Wind.Flutter));
            hash = Hash(
                hash,
                (uint)ClassifyLodTier(patch.Bounds, probeVolumes));
            if (RequiresAnimatedProxyUpdate(patch, mode))
            {
                hash = Hash(hash, unchecked((uint)cadenceGeneration));
                hash = Hash(hash, unchecked((uint)(cadenceGeneration >> 32)));
            }
        }
        if (probeVolumes != null)
        {
            var orderedVolumes = new List<GlobalIlluminationProbeVolume>(
                probeVolumes.Count);
            for (int index = 0; index < probeVolumes.Count; index++)
            {
                orderedVolumes.Add(probeVolumes[index] ??
                    throw new ArgumentException(
                        "Probe-volume collections cannot contain null entries.",
                        nameof(probeVolumes)));
            }
            orderedVolumes.Sort(static (left, right) =>
                left.Id.CompareTo(right.Id));
            for (int index = 0; index < orderedVolumes.Count; index++)
            {
                GlobalIlluminationProbeVolume volume = orderedVolumes[index];
                ulong key = StableGuidKey(volume.Id);
                hash = Hash(hash, (uint)key);
                hash = Hash(hash, (uint)(key >> 32));
                hash = Hash(hash, volume.Enabled ? 1u : 0u);
                hash = HashVector(hash, volume.Origin);
                hash = HashVector(hash, volume.Size);
                hash = Hash(hash, unchecked((uint)volume.ProbeCountX));
                hash = Hash(hash, unchecked((uint)volume.ProbeCountY));
                hash = Hash(hash, unchecked((uint)volume.ProbeCountZ));
                hash = Hash(hash, BitConverter.SingleToUInt32Bits(
                    volume.BlendDistance));
                hash = Hash(hash, BitConverter.SingleToUInt32Bits(
                    volume.MaxRayDistance));
            }
        }
        return hash;
    }

    private static bool RequiresAnimatedProxyUpdate(
        FoliagePatch patch,
        DdgiFoliageGeometryMode mode) =>
        mode == DdgiFoliageGeometryMode.AuthoredAndProceduralProxy &&
        patch.Visible &&
        patch.Density > 0f &&
        patch.Prototype.GeometryMode != FoliageGeometryMode.AuthoredMeshlets &&
        patch.Prototype.Wind.Frequency > 0f &&
        (patch.Prototype.Wind.Strength > 0f ||
            patch.Prototype.Wind.Flutter > 0f);

    private static ulong HashVector(ulong hash, Vector3 value)
    {
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(value.X));
        hash = Hash(hash, BitConverter.SingleToUInt32Bits(value.Y));
        return Hash(hash, BitConverter.SingleToUInt32Bits(value.Z));
    }

    private static float StableUnit(
        ulong patchKey,
        int cardIndex,
        uint salt)
    {
        var identity = new DdgiStochasticIdentity(
            patchKey,
            unchecked((uint)cardIndex),
            1u,
            DdgiStochasticIdentity.DefaultSamplingSequenceEpoch,
            DdgiStochasticDecisionDomain.FoliageProxyGeneration,
            unchecked((uint)patchKey),
            unchecked((uint)cardIndex) ^ salt);
        return identity.UnitFloat();
    }

    private static ulong StableGuidKey(Guid identity)
    {
        Span<byte> bytes = stackalloc byte[16];
        identity.TryWriteBytes(bytes);
        ulong hash = 14695981039346656037UL;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash ^= bytes[index];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong Hash(ulong hash, uint value)
    {
        hash ^= value;
        return unchecked(hash * 1099511628211UL);
    }

    private ProxyBuffer CreateBuffer(
        uint capacity,
        ulong stride,
        string debugName,
        BufferUsageFlags usage = ProxyBufferUsage,
        bool requireDeviceAddress = true)
    {
        capacity = Math.Max(1u, capacity);
        ulong byteSize = checked((ulong)capacity * stride);
        BufferHandle handle = _bufferManager.CreateDeviceBuffer(
            byteSize,
            usage,
            requireDeviceAddress,
            MemoryBudgetCategory.GlobalIllumination,
            debugName);
        try
        {
            _context.SetDebugName(
                _bufferManager.GetBuffer(handle).Handle,
                ObjectType.Buffer,
                debugName);
            return new ProxyBuffer(handle, capacity, byteSize);
        }
        catch
        {
            _bufferManager.DestroyBuffer(handle);
            throw;
        }
    }

    private bool TryEnsureFrameCapacity(
        int frameIndex,
        uint requiredVertexCount,
        uint requiredIndexCount,
        uint requiredPatchCount,
        out string failureReason)
    {
        ProxyBuffer previousVertices = _vertexBuffers[frameIndex];
        ProxyBuffer previousIndices = _indexBuffers[frameIndex];
        ProxyBuffer previousPatches = _patchBuffers[frameIndex];
        ProxyBuffer replacementVertices = previousVertices;
        ProxyBuffer replacementIndices = previousIndices;
        ProxyBuffer replacementPatches = previousPatches;
        bool replaceVertices = false;
        bool replaceIndices = false;
        bool replacePatches = false;
        try
        {
            requiredVertexCount = Math.Max(1u, requiredVertexCount);
            requiredIndexCount = Math.Max(1u, requiredIndexCount);
            requiredPatchCount = Math.Max(1u, requiredPatchCount);
            if (requiredVertexCount > previousVertices.Capacity)
            {
                replacementVertices = CreateBuffer(
                    GrowCapacity(previousVertices.Capacity, requiredVertexCount),
                    VertexStride,
                    $"DDGI Foliage Proxy Vertices Frame{frameIndex}");
                replaceVertices = true;
            }
            if (requiredIndexCount > previousIndices.Capacity)
            {
                replacementIndices = CreateBuffer(
                    GrowCapacity(previousIndices.Capacity, requiredIndexCount),
                    IndexStride,
                    $"DDGI Foliage Proxy Indices Frame{frameIndex}");
                replaceIndices = true;
            }
            if (requiredPatchCount > previousPatches.Capacity)
            {
                replacementPatches = CreateBuffer(
                    GrowCapacity(previousPatches.Capacity, requiredPatchCount),
                    PatchStride,
                    $"DDGI Foliage Proxy Patches Frame{frameIndex}",
                    BufferUsageFlags.StorageBufferBit |
                        BufferUsageFlags.TransferDstBit,
                    requireDeviceAddress: false);
                replacePatches = true;
            }
        }
        catch (Exception exception) when (exception is VulkanException or
                                           InvalidOperationException or
                                           OutOfMemoryException or
                                           OverflowException)
        {
            if (replaceVertices && replacementVertices.Handle.IsValid)
                _bufferManager.DestroyBuffer(replacementVertices.Handle);
            if (replaceIndices && replacementIndices.Handle.IsValid)
                _bufferManager.DestroyBuffer(replacementIndices.Handle);
            if (replacePatches && replacementPatches.Handle.IsValid)
                _bufferManager.DestroyBuffer(replacementPatches.Handle);
            failureReason =
                $"DDGI foliage proxy capacity growth failed: {exception.Message}";
            return false;
        }

        // PrepareFrame runs only after this exact slot's fence. Allocate every
        // replacement first, then swap them as one transaction so a failure
        // cannot destroy the last complete representation.
        _vertexBuffers[frameIndex] = replacementVertices;
        _indexBuffers[frameIndex] = replacementIndices;
        _patchBuffers[frameIndex] = replacementPatches;
        if (replaceVertices)
            _bufferManager.DestroyBuffer(previousVertices.Handle);
        if (replaceIndices)
            _bufferManager.DestroyBuffer(previousIndices.Handle);
        if (replacePatches)
            _bufferManager.DestroyBuffer(previousPatches.Handle);
        failureReason = string.Empty;
        return true;
    }

    private static uint GrowCapacity(uint current, uint required)
    {
        uint capacity = Math.Max(1u, current);
        while (capacity < required)
        {
            if (capacity > uint.MaxValue / 2u)
                return required;
            capacity *= 2u;
        }
        return capacity;
    }

    private static BoundingBox? ComputeInfluenceBounds(
        IReadOnlyList<DdgiFoliageProxyInstance> instances)
    {
        BoundingBox? bounds = null;
        for (int index = 0; index < instances.Count; index++)
        {
            BoundingBox current = instances[index].WorldBounds;
            bounds = bounds.HasValue
                ? new BoundingBox(
                    Vector3.Min(bounds.Value.Min, current.Min),
                    Vector3.Max(bounds.Value.Max, current.Max))
                : current;
        }
        return bounds;
    }

    private static int SaturatingAdd(int left, int right)
    {
        long result = (long)left + right;
        return result >= int.MaxValue ? int.MaxValue : checked((int)result);
    }

    private static int SaturatingMultiply(int left, int right)
    {
        long result = (long)left * right;
        if (result <= 0)
            return 0;
        return result >= int.MaxValue ? int.MaxValue : checked((int)result);
    }

    private static long ElapsedMicroseconds(long startTimestamp) =>
        (long)((Stopwatch.GetTimestamp() - startTimestamp) *
            1_000_000.0 / Stopwatch.Frequency);

    private void RegisterCurrentBuffers()
    {
        if (_bindlessHeap == null)
            return;
        RegisterBuffer(
            BindlessIndex.DdgiFoliageProxyVertexBuffer,
            _vertexBuffers[0]);
        RegisterBuffer(
            BindlessIndex.DdgiFoliageProxyIndexBuffer,
            _indexBuffers[0]);
        RegisterBuffer(
            BindlessIndex.DdgiFoliageProxyVertexBufferFrame1,
            _vertexBuffers[1]);
        RegisterBuffer(
            BindlessIndex.DdgiFoliageProxyIndexBufferFrame1,
            _indexBuffers[1]);
        RegisterBuffer(
            BindlessIndex.DdgiFoliageProxyPatchBuffer,
            _patchBuffers[0]);
        RegisterBuffer(
            BindlessIndex.DdgiFoliageProxyPatchBufferFrame1,
            _patchBuffers[1]);
    }

    private void RegisterBuffer(int index, ProxyBuffer buffer)
    {
        VkBuffer vkBuffer = _bufferManager.GetBuffer(buffer.Handle);
        _bindlessHeap!.RegisterStorageBuffer(
            index,
            vkBuffer,
            0,
            buffer.ByteSize);
    }

    private static void ValidateFrameIndex(int frameIndex)
    {
        if ((uint)frameIndex >= FramesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
        {
            if (_vertexBuffers[frameIndex].Handle.IsValid)
                _bufferManager.DestroyBuffer(_vertexBuffers[frameIndex].Handle);
            if (_indexBuffers[frameIndex].Handle.IsValid)
                _bufferManager.DestroyBuffer(_indexBuffers[frameIndex].Handle);
            if (_patchBuffers[frameIndex].Handle.IsValid)
                _bufferManager.DestroyBuffer(_patchBuffers[frameIndex].Handle);
        }
    }

    private readonly record struct ProxyBuffer(
        BufferHandle Handle,
        uint Capacity,
        ulong ByteSize);
}

public sealed record DdgiFoliageProxyFrame(
    int FrameSlot,
    ulong CadenceGeneration,
    IReadOnlyList<DdgiFoliageProxyInstance> Instances,
    int VertexCount,
    int TriangleCount,
    int AuthoredInstanceCount,
    int GeneratedInstanceCount,
    int DroppedTriangleCount,
    int EstimatedRepresentedBladeCount,
    ulong UploadedBytes,
    ulong VertexBufferBytes,
    ulong IndexBufferBytes,
    ulong ContentSignature,
    BoundingBox? InfluenceBounds,
    bool UpdatedThisFrame,
    long CpuBuildMicroseconds,
    long CpuUploadMicroseconds,
    string FallbackReason)
{
    public BufferHandle PatchBuffer { get; init; }
    public uint PatchBufferIndex { get; init; }
    public int PatchCount { get; init; }
    public ulong PatchBufferBytes { get; init; }
    public BufferHandle VertexBuffer { get; init; }
    public BufferHandle IndexBuffer { get; init; }
    public uint VertexBufferIndex { get; init; }
    public uint IndexBufferIndex { get; init; }
    public float WindTimeSeconds { get; init; }
    public int RequestedRepresentedInstanceCount { get; init; }
    public float DensityError { get; init; }
    public int NearCardCount { get; init; }
    public int MidCardCount { get; init; }
    public int FarCardCount { get; init; }
    public int ExcludedPatchCount { get; init; }
    public uint LodPolicyVersion { get; init; }
    public int CardCount => VertexCount /
        DdgiFoliageProxyManager.VerticesPerCrossedCard;
    public bool RequiresGpuGeneration =>
        UpdatedThisFrame && CardCount > 0 && PatchCount > 0;

    public static DdgiFoliageProxyFrame Empty(int frameSlot) => new(
        frameSlot,
        0,
        Array.Empty<DdgiFoliageProxyInstance>(),
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        false,
        0,
        0,
        string.Empty);
}

internal readonly record struct DdgiFoliageProxyGenerationPlan(
    GPUDdgiFoliageProxyPatch[] Patches,
    DdgiFoliageProxyInstance[] Instances,
    int CardCount,
    int AuthoredInstanceCount,
    int GeneratedInstanceCount,
    int DroppedTriangleCount,
    int EstimatedRepresentedBladeCount,
    string FallbackReason)
{
    public int RequestedRepresentedInstanceCount { get; init; }
    public float DensityError { get; init; }
    public int NearCardCount { get; init; }
    public int MidCardCount { get; init; }
    public int FarCardCount { get; init; }
    public int ExcludedPatchCount { get; init; }

    public static DdgiFoliageProxyGenerationPlan Empty { get; } = new(
        Array.Empty<GPUDdgiFoliageProxyPatch>(),
        Array.Empty<DdgiFoliageProxyInstance>(),
        0,
        0,
        0,
        0,
        0,
        string.Empty);
}

public readonly record struct DdgiFoliageProxyBuild(
    GPUVertex[] Vertices,
    uint[] Indices,
    DdgiFoliageProxyInstance[] Instances,
    int AuthoredInstanceCount,
    int GeneratedInstanceCount,
    int DroppedTriangleCount,
    int EstimatedRepresentedBladeCount)
{
    public int RequestedRepresentedInstanceCount { get; init; }
    public float DensityError { get; init; }
    public int NearCardCount { get; init; }
    public int MidCardCount { get; init; }
    public int FarCardCount { get; init; }
    public int ExcludedPatchCount { get; init; }

    public static DdgiFoliageProxyBuild Empty { get; } = new(
        Array.Empty<GPUVertex>(),
        Array.Empty<uint>(),
        Array.Empty<DdgiFoliageProxyInstance>(),
        0,
        0,
        0,
        0);
}

public readonly record struct DdgiFoliageProxyInstance
{
    public Guid PatchIdentity { get; init; }
    public object? Material { get; init; }
    public MeshHandle SourceMesh { get; init; }
    public Matrix4x4 WorldMatrix { get; init; }
    public BoundingBox WorldBounds { get; init; }
    public bool Generated { get; init; }
    public uint VertexOffset { get; init; }
    public uint VertexCount { get; init; }
    public uint IndexOffset { get; init; }
    public uint IndexCount { get; init; }
    public BufferHandle VertexBuffer { get; init; }
    public BufferHandle IndexBuffer { get; init; }
    public uint VertexBufferIndex { get; init; }
    public uint IndexBufferIndex { get; init; }
    public uint RepresentationGeneration { get; init; }
    public int FrameSlot { get; init; }
    public DdgiFoliageProxyLodTier LodTier { get; init; }

    public static DdgiFoliageProxyInstance Authored(
        FoliagePatch patch,
        MeshHandle mesh,
        Matrix4x4 worldMatrix,
        ulong generation,
        DdgiFoliageProxyLodTier lodTier = DdgiFoliageProxyLodTier.Near) =>
        new()
        {
            PatchIdentity = patch.Id,
            Material = patch.Prototype.Material,
            SourceMesh = mesh,
            WorldMatrix = worldMatrix,
            WorldBounds = patch.Bounds,
            Generated = false,
            LodTier = lodTier,
            RepresentationGeneration = FoldGeneration(
                generation,
                patch.ContentRevision)
        };

    public static DdgiFoliageProxyInstance Procedural(
        FoliagePatch patch,
        uint vertexOffset,
        uint vertexCount,
        uint indexOffset,
        uint indexCount,
        ulong generation,
        DdgiFoliageProxyLodTier lodTier = DdgiFoliageProxyLodTier.Near) =>
        new()
        {
            PatchIdentity = patch.Id,
            Material = patch.Prototype.Material,
            SourceMesh = default,
            WorldMatrix = Matrix4x4.Identity,
            WorldBounds = CreateProceduralWorldBounds(patch),
            Generated = true,
            LodTier = lodTier,
            VertexOffset = vertexOffset,
            VertexCount = vertexCount,
            IndexOffset = indexOffset,
            IndexCount = indexCount,
            RepresentationGeneration = FoldGeneration(
                generation,
                patch.ContentRevision)
        };

    private static BoundingBox CreateProceduralWorldBounds(FoliagePatch patch)
    {
        float height = Math.Max(0.01f, patch.Prototype.CardHeight) *
            Math.Max(0.0001f, patch.InstanceScale) * 1.15f;
        float horizontalWindExtent =
            patch.Prototype.Wind.Strength * height * 0.25f +
            patch.Prototype.Wind.Flutter * height * 0.05f;
        Vector3 minimum = patch.Bounds.Min -
            new Vector3(horizontalWindExtent, 0f, horizontalWindExtent);
        Vector3 maximum = patch.Bounds.Max +
            new Vector3(horizontalWindExtent, 0f, horizontalWindExtent);
        maximum.Y = Math.Max(maximum.Y, patch.Bounds.Min.Y + height);
        return new BoundingBox(minimum, maximum);
    }

    private static uint FoldGeneration(ulong generation, uint revision)
    {
        uint folded = unchecked((uint)generation) ^
            unchecked((uint)(generation >> 32)) ^ revision;
        return folded == 0u ? 1u : folded;
    }
}
