using System;
using System.Collections.Generic;
using System.Numerics;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Immutable qualification for the two implementation-dependent C4 kernels.
/// The checked-in deterministic cache builder is production-capable.  The
/// aggregate remains fail-closed until the tagged ray-query transport kernel
/// is also integrated and qualified.
/// </summary>
public readonly record struct GiCausticGpuPipelineQualification(
    bool TaggedFirstDiffuseTraceQualified,
    bool DeterministicParallelCacheBuildQualified)
{
    /// <summary>
    /// The checked-in shader pair used by an explicit user selection. This is
    /// an implementation-availability assertion, not promotion evidence.
    /// </summary>
    public static GiCausticGpuPipelineQualification IntegratedExplicit { get; } = new(
        TaggedFirstDiffuseTraceQualified: true,
        DeterministicParallelCacheBuildQualified: true);

    public static GiCausticGpuPipelineQualification CheckedInShadersFailClosed { get; } = new(
        TaggedFirstDiffuseTraceQualified: false,
        DeterministicParallelCacheBuildQualified: true);

    public bool TryValidateBuild(out string reason)
    {
        if (!TaggedFirstDiffuseTraceQualified)
        {
            reason = "caustic-tagged-first-diffuse-trace-shader-unqualified";
            return false;
        }
        if (!DeterministicParallelCacheBuildQualified)
        {
            reason = "caustic-deterministic-parallel-cache-build-shader-unqualified";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// Fixed C4 recording facts that are intentionally independent of the render
/// graph. They require every renderer integration to prove the same two-bank,
/// pass-order, and fence-publication boundary before it can enable C4.
/// </summary>
public static class GiCausticGpuVulkanRuntimeContract
{
    public const int RequiredPhotonBankCount = 2;
    public const int RequiredCacheBankCount = 2;
    public const int TaskBufferBindlessSlot = 204;
    public const int PhotonBufferBindlessSlot = 205;
    public const int CacheBufferBindlessSlot = 206;
    public const int ScratchBufferBindlessSlot = 207;

    public static bool TryValidateRecordingLayout(
        in GiCausticGpuResourceLayout layout,
        out string reason)
    {
        if (!layout.IsValid)
        {
            reason = "caustic-vulkan-recording-layout-invalid:" + layout.FailureReason;
            return false;
        }
        if (layout.PhotonBankCount != RequiredPhotonBankCount ||
            layout.CacheBankCount != RequiredCacheBankCount)
        {
            reason = "caustic-vulkan-recording-requires-exactly-two-photon-and-cache-banks";
            return false;
        }
        if (layout.TaskQueueBytes < GiCausticGpuAbi.TaskDispatchHeaderBytes ||
            layout.TaskCapacity <= 0 || layout.PhotonCapacity <= 0 ||
            layout.CellTableCapacity <= 0 || layout.PhotonRecordStride !=
            GiCausticGpuAbi.PhotonRecordBytes)
        {
            reason = "caustic-vulkan-recording-layout-capacity-or-abi-invalid";
            return false;
        }
        try
        {
            ulong expectedEmitterOffset = checked(
                (ulong)GiCausticGpuAbi.TaskDispatchHeaderBytes +
                (ulong)layout.TaskCapacity * GiCausticGpuAbi.TaskRecordBytes);
            ulong expectedHeroOffset = checked(
                expectedEmitterOffset +
                (ulong)layout.EmitterCapacity * GiCausticGpuAbi.EmitterRecordBytes);
            ulong expectedPairOffset = checked(
                expectedHeroOffset +
                (ulong)layout.HeroCapacity * GiCausticGpuAbi.HeroRecordBytes);
            ulong expectedQueueBytes = checked(
                expectedPairOffset +
                (ulong)layout.ProposalPairCapacity *
                    GiCausticGpuAbi.ProposalPairRecordBytes);
            if (layout.TaskRecordOffsetBytes != GiCausticGpuAbi.TaskDispatchHeaderBytes ||
                layout.EmitterRecordOffsetBytes != expectedEmitterOffset ||
                layout.HeroRecordOffsetBytes != expectedHeroOffset ||
                layout.ProposalPairRecordOffsetBytes != expectedPairOffset ||
                layout.TaskQueueBytes != expectedQueueBytes ||
                layout.EmitterCapacity is <= 0 or >
                    GiCausticGpuTaskGenerationFlags.MaximumEmitterCount ||
                layout.HeroCapacity is <= 0 or >
                    GiCausticGpuTaskGenerationFlags.MaximumHeroCount ||
                layout.ProposalPairCapacity is <= 0 or >
                    GiCausticGpuTaskGenerationFlags.MaximumProposalPairCount)
            {
                reason = "caustic-vulkan-task-generation-metadata-layout-invalid";
                return false;
            }
        }
        catch (OverflowException)
        {
            reason = "caustic-vulkan-task-generation-metadata-layout-overflow";
            return false;
        }
        if ((layout.TaskQueueBytes & 3UL) != 0UL ||
            (layout.CandidateStagingBytes & 3UL) != 0UL ||
            (layout.PublishedPhotonBytes & 3UL) != 0UL ||
            (layout.CacheBytes & 3UL) != 0UL ||
            (layout.ScratchBytes & 3UL) != 0UL)
        {
            reason = "caustic-vulkan-recording-storage-layout-is-not-word-addressable";
            return false;
        }
        if (!GiCausticDeterministicBuildScratchLayout.TryCreate(
                layout.PhotonCapacity,
                out GiCausticDeterministicBuildScratchLayout scratchLayout) ||
            layout.ScratchBytes < scratchLayout.RequiredBytes)
        {
            reason = "caustic-vulkan-deterministic-build-scratch-layout-invalid";
            return false;
        }
        if (!layout.ScreenResolve.IsValid ||
            layout.ScreenResolve.TileScratchBytes > layout.ScratchBytes ||
            layout.ScreenResolve.TileSize != GiCausticScreenGpuAbi.TileSize ||
            layout.ScreenResolve.Width <= 0 ||
            layout.ScreenResolve.Height <= 0)
        {
            reason = "caustic-vulkan-screen-resolve-layout-invalid";
            return false;
        }
        try
        {
            if (!FitsGpuWordAddress(layout.TaskQueueBytes) ||
                !FitsGpuWordAddress(checked(layout.CandidateStagingBytes +
                    layout.PublishedPhotonBytes)) ||
                !FitsGpuWordAddress(layout.CacheBytes) ||
                !FitsGpuWordAddress(layout.ScratchBytes))
            {
                reason = "caustic-vulkan-recording-storage-layout-exceeds-32-bit-word-addressing";
                return false;
            }
            if (layout.CacheHeaderBytesPerBank != GiCausticGpuAbi.CacheHeaderBytes ||
                layout.CacheTableBytesPerBank == 0UL ||
                checked(layout.CacheTableBytes + layout.CacheHistoryBytes +
                    layout.PublicationHeaderBytes) != layout.CacheBytes)
            {
                reason = "caustic-vulkan-recording-cache-header-or-table-layout-invalid";
                return false;
            }
        }
        catch (OverflowException)
        {
            reason = "caustic-vulkan-recording-layout-overflow";
            return false;
        }

        GiCausticGpuBindlessSlots slots = GiCausticGpuAbi.BindlessSlots;
        try
        {
            slots.Validate();
        }
        catch (InvalidOperationException)
        {
            reason = "caustic-vulkan-recording-fixed-bindless-slot-contract-invalid";
            return false;
        }
        if (slots.TaskBufferIndex != TaskBufferBindlessSlot ||
            slots.PhotonBufferIndex != PhotonBufferBindlessSlot ||
            slots.CacheBufferIndex != CacheBufferBindlessSlot ||
            slots.ScratchBufferIndex != ScratchBufferBindlessSlot)
        {
            reason = "caustic-vulkan-recording-bindless-slots-are-not-204-through-207";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static uint GetScratchWordCapacity(in GiCausticGpuResourceLayout layout)
    {
        if (!TryValidateRecordingLayout(layout, out string reason))
            throw new ArgumentException("C4 recording layout is invalid: " + reason, nameof(layout));
        return checked((uint)(layout.ScratchBytes / sizeof(uint)));
    }

    public static ulong GetCacheHeaderOffsetBytes(
        in GiCausticGpuResourceLayout layout,
        int cacheBankIndex)
    {
        if (!TryValidateRecordingLayout(layout, out string reason))
            throw new ArgumentException("C4 recording layout is invalid: " + reason, nameof(layout));
        if (cacheBankIndex is < 0 or >= RequiredCacheBankCount)
            throw new ArgumentOutOfRangeException(nameof(cacheBankIndex));

        ulong offset = checked(
            layout.CacheTableBytes + layout.CacheHistoryBytes +
            (ulong)cacheBankIndex * GiCausticGpuAbi.CacheHeaderBytes);
        if (offset > layout.CacheBytes ||
            GiCausticGpuAbi.CacheHeaderBytes > layout.CacheBytes - offset)
        {
            throw new InvalidOperationException(
                "C4 cache-header range is outside the exact cache allocation.");
        }
        return offset;
    }

    private static bool FitsGpuWordAddress(ulong bytes) =>
        bytes / sizeof(uint) <= uint.MaxValue;
}

/// <summary>Observable order for the C4 submission protocol.</summary>
public enum GiCausticGpuRecordingStage : byte
{
    TaggedTaskUpload = 0,
    TaggedTaskUploadToTaskBarrier = 1,
    TaskReset = 2,
    TaskResetToMetadataValidationBarrier = 3,
    TaskMetadataValidation = 4,
    TaskMetadataToGenerationBarrier = 5,
    TaskGeneration = 6,
    TaskGenerationToValidationBarrier = 7,
    TaskValidation = 8,
    TaskToTraceBarrier = 9,
    Trace = 10,
    TraceToCacheBuildBarrier = 11,
    CacheBuildClear = 12,
    CacheBuildClearToRadixBarrier = 13,
    CacheBuildStableRadix = 14,
    CacheBuildRadixToCompactBarrier = 15,
    CacheBuildDeterministicBottomK = 16,
    CacheBuildCompactToHashBarrier = 17,
    CacheBuildDeterministicCellHash = 18,
    CacheBuildToHeaderReadbackBarrier = 19,
    HeaderReadbackCopy = 20,
    FenceValidatedPublication = 21,
    ResolveRequestUpload = 22,
    ResolveRequestToResolveBarrier = 23,
    Resolve = 24,
    ResolveToForwardCompositeBarrier = 25,
    ForwardCompositeHandoff = 26
}

/// <summary>Immutable CPU-readable schedule used by focused parity tests.</summary>
public static class GiCausticGpuRecordingContract
{
    private static readonly GiCausticGpuRecordingStage[] s_buildStages =
    [
        GiCausticGpuRecordingStage.TaggedTaskUpload,
        GiCausticGpuRecordingStage.TaggedTaskUploadToTaskBarrier,
        GiCausticGpuRecordingStage.TaskReset,
        GiCausticGpuRecordingStage.TaskResetToMetadataValidationBarrier,
        GiCausticGpuRecordingStage.TaskMetadataValidation,
        GiCausticGpuRecordingStage.TaskMetadataToGenerationBarrier,
        GiCausticGpuRecordingStage.TaskGeneration,
        GiCausticGpuRecordingStage.TaskGenerationToValidationBarrier,
        GiCausticGpuRecordingStage.TaskValidation,
        GiCausticGpuRecordingStage.TaskToTraceBarrier,
        GiCausticGpuRecordingStage.Trace,
        GiCausticGpuRecordingStage.TraceToCacheBuildBarrier,
        GiCausticGpuRecordingStage.CacheBuildClear,
        GiCausticGpuRecordingStage.CacheBuildClearToRadixBarrier,
        GiCausticGpuRecordingStage.CacheBuildStableRadix,
        GiCausticGpuRecordingStage.CacheBuildRadixToCompactBarrier,
        GiCausticGpuRecordingStage.CacheBuildDeterministicBottomK,
        GiCausticGpuRecordingStage.CacheBuildCompactToHashBarrier,
        GiCausticGpuRecordingStage.CacheBuildDeterministicCellHash,
        GiCausticGpuRecordingStage.CacheBuildToHeaderReadbackBarrier,
        GiCausticGpuRecordingStage.HeaderReadbackCopy,
        GiCausticGpuRecordingStage.FenceValidatedPublication
    ];

    private static readonly GiCausticGpuRecordingStage[] s_resolveStages =
    [
        GiCausticGpuRecordingStage.ResolveRequestUpload,
        GiCausticGpuRecordingStage.ResolveRequestToResolveBarrier,
        GiCausticGpuRecordingStage.Resolve,
        GiCausticGpuRecordingStage.ResolveToForwardCompositeBarrier,
        GiCausticGpuRecordingStage.ForwardCompositeHandoff
    ];

    public static ReadOnlySpan<GiCausticGpuRecordingStage> BuildStages => s_buildStages;

    public static ReadOnlySpan<GiCausticGpuRecordingStage> ResolveStages => s_resolveStages;

    public const bool RequiresFenceValidatedHeaderPublication = true;
}

/// <summary>
/// A concrete, producer-owned C4 task source.  It represents only authored
/// tagged-light samples and validated hero-caster metadata, never an inferred
/// DDGI or generic light-buffer reinterpretation.
/// </summary>
public readonly record struct GiCausticTaggedTransportProducerContract(
    bool IsAvailable,
    uint C4GpuAbiVersion,
    uint TransportAbiVersion,
    int TaskCount,
    uint TaskRecordStrideBytes,
    ulong TaskPayloadBytes,
    int EmitterCount,
    int HeroCount,
    int ProposalPairCount,
    ulong MetadataPayloadBytes,
    ulong RevisionFingerprint,
    bool TaggedLightDistributionAvailable,
    bool HeroCasterMetadataAvailable,
    bool CurrentPoseAccelerationStructureAvailable,
    bool FirstDiffuseEndpointsOnly,
    bool SupportsTransactionStamping,
    bool GpuTaskGeneration,
    bool ExactTwoLevelProposal,
    bool CanonicalEmissionSupport,
    PipelineStageFlags2 ProducerWriteStageMask,
    AccessFlags2 ProducerWriteAccessMask)
{
    public static GiCausticTaggedTransportProducerContract Unavailable { get; } = new(
        IsAvailable: false,
        C4GpuAbiVersion: 0u,
        TransportAbiVersion: 0u,
        TaskCount: 0,
        TaskRecordStrideBytes: 0u,
        TaskPayloadBytes: 0UL,
        EmitterCount: 0,
        HeroCount: 0,
        ProposalPairCount: 0,
        MetadataPayloadBytes: 0UL,
        RevisionFingerprint: 0UL,
        TaggedLightDistributionAvailable: false,
        HeroCasterMetadataAvailable: false,
        CurrentPoseAccelerationStructureAvailable: false,
        FirstDiffuseEndpointsOnly: false,
        SupportsTransactionStamping: false,
        GpuTaskGeneration: false,
        ExactTwoLevelProposal: false,
        CanonicalEmissionSupport: false,
        ProducerWriteStageMask: 0,
        ProducerWriteAccessMask: 0);

    public bool TryValidateForLayout(
        in GiCausticGpuResourceLayout layout,
        out string reason)
    {
        if (!layout.IsValid || layout.TaskQueueBytes <
            GiCausticGpuAbi.TaskDispatchHeaderBytes)
        {
            reason = "caustic-tagged-hero-transport-producer-layout-invalid";
            return false;
        }
        if (!IsAvailable)
        {
            reason = "caustic-tagged-hero-transport-producer-unavailable";
            return false;
        }
        if (C4GpuAbiVersion != GiCausticGpuAbi.Version)
        {
            reason = "caustic-tagged-hero-transport-producer-c4-abi-version-mismatch";
            return false;
        }
        if (TransportAbiVersion == 0u || TaskRecordStrideBytes !=
            GiCausticGpuAbi.TaskRecordBytes)
        {
            reason = "caustic-tagged-hero-transport-producer-task-abi-invalid";
            return false;
        }
        if (TaskCount <= 0 || TaskCount > layout.TaskCapacity)
        {
            reason = "caustic-tagged-hero-transport-producer-task-count-out-of-bounds";
            return false;
        }
        if (EmitterCount <= 0 || EmitterCount > layout.EmitterCapacity ||
            HeroCount <= 0 || HeroCount > layout.HeroCapacity ||
            ProposalPairCount <= 0 || ProposalPairCount >
                layout.ProposalPairCapacity ||
            ProposalPairCount > EmitterCount * HeroCount)
        {
            reason = "caustic-tagged-hero-transport-producer-metadata-count-out-of-bounds";
            return false;
        }
        if (!TaggedLightDistributionAvailable || !HeroCasterMetadataAvailable ||
            !CurrentPoseAccelerationStructureAvailable || !FirstDiffuseEndpointsOnly ||
            !SupportsTransactionStamping || !GpuTaskGeneration ||
            !ExactTwoLevelProposal || !CanonicalEmissionSupport)
        {
            reason = "caustic-tagged-hero-transport-producer-semantics-unqualified";
            return false;
        }
        if (ProducerWriteStageMask == 0 || ProducerWriteAccessMask == 0 ||
            !NamesWriteAccess(ProducerWriteAccessMask))
        {
            reason = "caustic-tagged-hero-transport-producer-write-visibility-invalid";
            return false;
        }

        try
        {
            ulong expectedBytes = checked(
                (ulong)TaskCount * GiCausticGpuAbi.TaskRecordBytes);
            if (TaskPayloadBytes != expectedBytes)
            {
                reason = "caustic-tagged-hero-transport-producer-task-payload-size-mismatch";
                return false;
            }
            if (TaskPayloadBytes > layout.TaskQueueBytes -
                GiCausticGpuAbi.TaskDispatchHeaderBytes)
            {
                reason = "caustic-tagged-hero-transport-producer-task-payload-exceeds-queue";
                return false;
            }
            ulong expectedMetadataBytes = checked(
                (ulong)EmitterCount * GiCausticGpuAbi.EmitterRecordBytes +
                (ulong)HeroCount * GiCausticGpuAbi.HeroRecordBytes +
                (ulong)ProposalPairCount * GiCausticGpuAbi.ProposalPairRecordBytes);
            if (MetadataPayloadBytes != expectedMetadataBytes)
            {
                reason = "caustic-tagged-hero-transport-producer-metadata-payload-size-mismatch";
                return false;
            }
        }
        catch (OverflowException)
        {
            reason = "caustic-tagged-hero-transport-producer-task-payload-overflow";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryValidateForBuild(
        in GiCausticGpuResourceLayout layout,
        in GiCausticCacheRevision revision,
        out string reason)
    {
        if (!TryValidateForLayout(layout, out reason))
            return false;
        if (!revision.IsValid || TransportAbiVersion != revision.TransportAbi)
        {
            reason = "caustic-tagged-hero-transport-producer-transport-revision-mismatch";
            return false;
        }
        if (RevisionFingerprint == 0UL || RevisionFingerprint !=
            GiCausticGpuAbi.ComputeRevisionFingerprint(revision))
        {
            reason = "caustic-tagged-hero-transport-producer-content-revision-mismatch";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool NamesWriteAccess(AccessFlags2 access) =>
        (access & (AccessFlags2.ShaderStorageWriteBit |
                   AccessFlags2.TransferWriteBit |
                   AccessFlags2.HostWriteBit |
                   AccessFlags2.MemoryWriteBit)) != 0;
}

/// <summary>Destination owned by C4 while a tagged transport producer uploads tasks.</summary>
public readonly record struct GiCausticTaggedTransportTaskUploadTarget(
    BufferHandle TaskBuffer,
    ulong TaskRecordOffsetBytes,
    ulong TaskPayloadBytes,
    ulong EmitterRecordOffsetBytes,
    ulong HeroRecordOffsetBytes,
    ulong ProposalPairRecordOffsetBytes,
    int EmitterCount,
    int HeroCount,
    int ProposalPairCount,
    ulong MetadataPayloadBytes,
    GiCausticGpuBuildToken Token)
{
    public bool IsValid => TaskBuffer.IsValid &&
        TaskRecordOffsetBytes == GiCausticGpuAbi.TaskDispatchHeaderBytes &&
        TaskPayloadBytes != 0UL && EmitterRecordOffsetBytes > TaskRecordOffsetBytes &&
        HeroRecordOffsetBytes > EmitterRecordOffsetBytes &&
        ProposalPairRecordOffsetBytes > HeroRecordOffsetBytes &&
        EmitterCount > 0 && HeroCount > 0 && ProposalPairCount > 0 &&
        MetadataPayloadBytes != 0UL && !Token.IsDefault;
}

/// <summary>
/// A producer must record the real tagged-light/hero-caster task bytes into
/// the supplied C4 task queue.  The runtime owns the queue header, generation,
/// banks, and all later storage; implementations must not bind a generic DDGI
/// source as a substitute.
/// </summary>
public interface IGiCausticTaggedTransportProducer
{
    GiCausticTaggedTransportProducerContract Contract { get; }

    bool TryRecordTaskUpload(
        CommandBuffer commandBuffer,
        in GiCausticTaggedTransportTaskUploadTarget target,
        out string reason);
}

/// <summary>
/// Typed downstream writer/reader contract for C4's isolated resolve scratch.
/// It does not make a scene-color write: a future forward pass receives an
/// explicit storage handoff after it has provided bounded receiver requests.
/// </summary>
public readonly record struct GiCausticForwardCompositeConsumerContract(
    bool IsAvailable,
    uint C4GpuAbiVersion,
    uint ScratchBufferBindlessIndex,
    uint ResolveRequestWordOffset,
    uint ResolveRequestCount,
    PipelineStageFlags2 RequestWriteStageMask,
    AccessFlags2 RequestWriteAccessMask,
    PipelineStageFlags2 CompositeReadStageMask,
    AccessFlags2 CompositeReadAccessMask,
    bool UsesOnlyValidatedC4ResolveResults,
    bool KeepsC4ScratchSeparateFromDdgi)
{
    public static GiCausticForwardCompositeConsumerContract Unavailable { get; } = new(
        IsAvailable: false,
        C4GpuAbiVersion: 0u,
        ScratchBufferBindlessIndex: 0u,
        ResolveRequestWordOffset: 0u,
        ResolveRequestCount: 0u,
        RequestWriteStageMask: 0,
        RequestWriteAccessMask: 0,
        CompositeReadStageMask: 0,
        CompositeReadAccessMask: 0,
        UsesOnlyValidatedC4ResolveResults: false,
        KeepsC4ScratchSeparateFromDdgi: false);

    public bool TryValidate(uint scratchWordCapacity, out string reason)
    {
        if (!IsAvailable)
        {
            reason = "caustic-forward-composite-consumer-unavailable";
            return false;
        }
        if (C4GpuAbiVersion != GiCausticGpuAbi.Version ||
            ScratchBufferBindlessIndex !=
                (uint)GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot)
        {
            reason = "caustic-forward-composite-consumer-c4-abi-or-scratch-slot-invalid";
            return false;
        }
        if (ResolveRequestCount == 0u ||
            RequestWriteStageMask == 0 || RequestWriteAccessMask == 0 ||
            CompositeReadStageMask == 0 || CompositeReadAccessMask == 0 ||
            !NamesWriteAccess(RequestWriteAccessMask) ||
            (CompositeReadAccessMask & AccessFlags2.ShaderStorageReadBit) == 0 ||
            !UsesOnlyValidatedC4ResolveResults || !KeepsC4ScratchSeparateFromDdgi)
        {
            reason = "caustic-forward-composite-consumer-semantics-or-visibility-invalid";
            return false;
        }

        try
        {
            ulong requestWords = GiCausticGpuAbi.ResolveRequestBytes / sizeof(uint);
            ulong resultWords = GiCausticGpuAbi.ResolveResultBytes / sizeof(uint);
            ulong requiredWords = checked(
                (ulong)ResolveRequestWordOffset +
                (ulong)ResolveRequestCount * (requestWords + resultWords));
            if (requiredWords > scratchWordCapacity)
            {
                reason = "caustic-forward-composite-consumer-request-range-exceeds-scratch";
                return false;
            }
        }
        catch (OverflowException)
        {
            reason = "caustic-forward-composite-consumer-request-range-overflow";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool NamesWriteAccess(AccessFlags2 access) =>
        (access & (AccessFlags2.ShaderStorageWriteBit |
                   AccessFlags2.TransferWriteBit |
                   AccessFlags2.HostWriteBit |
                   AccessFlags2.MemoryWriteBit)) != 0;
}

/// <summary>Bounded scratch range that a future forward composite fills with receiver requests.</summary>
public readonly record struct GiCausticForwardCompositeRequestTarget(
    BufferHandle ScratchBuffer,
    uint ScratchBufferBindlessIndex,
    uint ResolveRequestWordOffset,
    uint ResolveRequestCount,
    uint CacheGeneration,
    GiCausticCacheRevision Revision)
{
    public bool IsValid => ScratchBuffer.IsValid &&
        ScratchBufferBindlessIndex ==
            (uint)GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot &&
        ResolveRequestCount > 0u && CacheGeneration != 0u && Revision.IsValid;
}

/// <summary>Future forward code owns concrete receiver-request generation.</summary>
public interface IGiCausticForwardCompositeConsumer
{
    GiCausticForwardCompositeConsumerContract Contract { get; }

    bool TryRecordResolveRequests(
        CommandBuffer commandBuffer,
        in GiCausticForwardCompositeRequestTarget target,
        out string reason);
}

/// <summary>
/// The only C4 value a future forward composite may consume.  It is returned
/// after the resolve recorder has emitted its compute-to-consumer barrier; it
/// does not imply that scene color has been altered.
/// </summary>
public readonly record struct GiCausticForwardCompositeHandoff(
    bool IsAvailable,
    uint C4GpuAbiVersion,
    uint ScratchBufferBindlessIndex,
    uint ResolveResultWordOffset,
    uint ResolveResultCount,
    uint CacheGeneration,
    PipelineStageFlags2 ConsumerReadStageMask,
    AccessFlags2 ConsumerReadAccessMask,
    string Reason)
{
    public static GiCausticForwardCompositeHandoff Disabled(string reason) => new(
        IsAvailable: false,
        C4GpuAbiVersion: GiCausticGpuAbi.Version,
        ScratchBufferBindlessIndex:
            (uint)GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot,
        ResolveResultWordOffset: 0u,
        ResolveResultCount: 0u,
        CacheGeneration: 0u,
        ConsumerReadStageMask: 0,
        ConsumerReadAccessMask: 0,
        Reason: string.IsNullOrWhiteSpace(reason)
            ? "caustic-forward-composite-handoff-unavailable"
            : reason.Trim());
}

/// <summary>Inspectable failure state for the isolated C4 Vulkan boundary.</summary>
public enum GiCausticVulkanRuntimeCapabilityReason : byte
{
    None = 0,
    EffectiveModeDisabled = 1,
    FeatureSupportUnavailable = 2,
    RecordingLayoutInvalid = 3,
    PipelineQualificationUnavailable = 4,
    TaggedTransportProducerUnavailable = 5,
    TaggedTransportProducerContractInvalid = 6,
    BindlessDescriptorContextUnavailable = 7,
    PipelineUnavailable = 8,
    ResourceAllocationFailed = 9,
    BuildSubmissionRejected = 10,
    HeaderReadbackRejected = 11,
    ForwardCompositeConsumerUnavailable = 12,
    ForwardCompositeHandoffRejected = 13,
    ScreenResolveUnavailable = 14,
    ScreenResolveRejected = 15,
    ScreenCompositeRejected = 16,
    Disposed = 17
}

/// <summary>Inspectable C4 runtime state; unavailable means zero C4-owned work is recorded.</summary>
public readonly record struct GiCausticVulkanRuntimeDiagnostics(
    GiCausticVulkanRuntimeCapabilityReason CapabilityReason,
    bool TaggedTransportProducerAvailable,
    bool DeterministicCacheBuildQualified,
    bool DescriptorContextRegistered,
    bool HeaderReadbackPending,
    GiCausticGpuRuntimeSnapshot Resource,
    string Detail)
{
    public GiCausticPublicationTelemetry Publication { get; init; } =
        GiCausticPublicationTelemetry.Empty;

    public static GiCausticVulkanRuntimeDiagnostics Disabled { get; } = new(
        GiCausticVulkanRuntimeCapabilityReason.PipelineQualificationUnavailable,
        TaggedTransportProducerAvailable: false,
        DeterministicCacheBuildQualified: false,
        DescriptorContextRegistered: false,
        HeaderReadbackPending: false,
        Resource: new GiCausticGpuRuntimeSnapshot(
            GiCausticGpuResourceState.Disabled,
            false,
            0UL,
            0UL,
            0u,
            -1,
            0,
            -1,
            0,
            0u,
            0u,
            0UL,
            0UL,
            0UL,
            GiCausticGpuMemoryRequirements.Empty,
            "caustic-tagged-first-diffuse-trace-shader-unqualified"),
        Detail: "caustic-tagged-first-diffuse-trace-shader-unqualified");
}

/// <summary>
/// Vulkan allocation, descriptor, recording, readback, and handoff boundary
/// for the C4 caustic cache.  The renderer instantiates this boundary but it
/// remains allocation-free until an evidence-bound tagged producer and the
/// complete shader qualification contract are admitted.
/// </summary>
public sealed unsafe class GiCausticVulkanRuntime : IDisposable
{
    private const ulong HeaderReadbackBytes = GiCausticGpuAbi.CacheHeaderBytes;

    private readonly object _sync = new();
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly AccelerationStructureManager _accelerationStructureManager;
    private readonly RenderTargetManager? _renderTargets;
    private readonly Action? _waitForDescriptorReaders;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly GiCausticGpuResourceManager _resourceManager = new();
    private readonly VulkanAllocator _allocator;
    private readonly PendingReadback?[] _pendingReadbacks =
        new PendingReadback?[RenderingConstants.FramesInFlight];
    private readonly PreparedGraphFrame?[] _preparedGraphFrames =
        new PreparedGraphFrame?[RenderingConstants.FramesInFlight];
    private readonly PendingGraphBuild?[] _pendingGraphBuilds =
        new PendingGraphBuild?[RenderingConstants.FramesInFlight];
    private readonly PendingScreenFrame?[] _pendingScreenFrames =
        new PendingScreenFrame?[RenderingConstants.FramesInFlight];
    private readonly GiCausticCacheRevision?[] _screenFrameRevisions =
        new GiCausticCacheRevision?[RenderingConstants.FramesInFlight];

    private GiCausticGpuPass? _pass;
    private GiCausticScreenGpuPass? _screenPass;
    private GiCausticGpuPipelineQualification _qualification;
    private GiCausticPublicationTelemetry _lastPublication;
    private bool _disposed;

    public GiCausticVulkanRuntime(
        VulkanContext context,
        BufferManager bufferManager,
        AccelerationStructureManager accelerationStructureManager,
        Action? waitForDescriptorReaders = null,
        RenderTargetManager? renderTargets = null,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _accelerationStructureManager = accelerationStructureManager ??
            throw new ArgumentNullException(nameof(accelerationStructureManager));
        _renderTargets = renderTargets;
        _waitForDescriptorReaders = waitForDescriptorReaders;
        _pipelineCacheService = pipelineCacheService;
        _allocator = new VulkanAllocator(bufferManager);
        Diagnostics = GiCausticVulkanRuntimeDiagnostics.Disabled;
    }

    public GiCausticVulkanRuntimeDiagnostics Diagnostics { get; private set; }

    internal GiCausticVulkanBuffers Buffers
    {
        get
        {
            lock (_sync)
            {
                if (_disposed ||
                    !_resourceManager.TryGetActiveAllocation(
                        out GiCausticGpuAllocation allocation,
                        out _) ||
                    !_allocator.TryGetNativeAllocation(
                        allocation.AllocationId,
                        out GiCausticNativeAllocation nativeAllocation))
                {
                    return default;
                }
                return nativeAllocation.Buffers;
            }
        }
    }

    internal BufferHandle GetFrameConstantBuffer(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            if (_disposed ||
                !_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out _) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out GiCausticNativeAllocation nativeAllocation))
            {
                return BufferHandle.Invalid;
            }
            return nativeAllocation.FrameConstantBuffers[frameIndex];
        }
    }

    /// <summary>
    /// Registers all four C4 slots to a safe non-C4 fallback while inactive.
    /// The fallback is never interpreted as C4 data: dispatch is prohibited
    /// until the full producer/qualification/allocation transaction succeeds.
    /// </summary>
    public bool TryRegisterDescriptors(
        BindlessHeap bindlessHeap,
        BufferHandle safeFallbackBuffer,
        ulong safeFallbackBufferBytes,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(bindlessHeap);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_allocator.TrySetDescriptorContext(
                    bindlessHeap,
                    safeFallbackBuffer,
                    safeFallbackBufferBytes,
                    out reason))
            {
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    producerAvailable: false);
                return false;
            }

            if (_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out _) &&
                _allocator.TryGetNativeAllocation(allocation.AllocationId, out _))
            {
                SynchronizeDescriptorReadersNoLock();
                if (!_allocator.TryBindAllocation(allocation.AllocationId, out reason))
                {
                    DisableAtSafeTransitionNoLock(
                        GiCausticVulkanRuntimeCapabilityReason.BindlessDescriptorContextUnavailable,
                        reason,
                        producerAvailable: false);
                    return false;
                }

                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.None,
                    "caustic-registered-active-descriptors",
                    producerAvailable: true);
                reason = "caustic-registered-active-descriptors";
                return true;
            }

            if (!_allocator.TryBindFallback(out reason))
            {
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    producerAvailable: false);
                return false;
            }
            UpdateDiagnosticsNoLock(
                GiCausticVulkanRuntimeCapabilityReason.PipelineQualificationUnavailable,
                "caustic-registered-safe-descriptor-fallbacks",
                producerAvailable: false);
            reason = "caustic-registered-safe-descriptor-fallbacks";
            return true;
        }
    }

    /// <summary>
    /// Configures C4 only when all effective-mode, feature, shader, typed
    /// producer, descriptor, and exact-allocation gates succeed.  A failure
    /// first restores fallback descriptors and releases C4 resources.
    /// </summary>
    public bool TryConfigure(
        in GiCausticGpuRuntimeRequest request,
        in GiCausticGpuPipelineQualification qualification,
        IGiCausticTaggedTransportProducer? taggedTransportProducer,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            SynchronizeDescriptorReadersNoLock();
            AbortPendingReadbacksNoLock("caustic-runtime-reconfigured-before-header-readback");
            AbortPendingGraphBuildsNoLock(
                "caustic-runtime-reconfigured-before-graph-build-completion");
            AbortPendingScreenFramesNoLock(
                "caustic-runtime-reconfigured-before-screen-composite");

            if (!request.IsEffectivelyEnabled)
            {
                reason = "caustic-effective-mode-disabled";
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.EffectiveModeDisabled,
                    reason,
                    producerAvailable: taggedTransportProducer?.Contract.IsAvailable ?? false);
                return false;
            }
            if (!request.FeatureSupport.IsSupported)
            {
                reason = request.FeatureSupport.FailureReason;
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.FeatureSupportUnavailable,
                    reason,
                    producerAvailable: taggedTransportProducer?.Contract.IsAvailable ?? false);
                return false;
            }
            if (!GiCausticGpuVulkanRuntimeContract.TryValidateRecordingLayout(
                    request.Layout,
                    out reason))
            {
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.RecordingLayoutInvalid,
                    reason,
                    producerAvailable: taggedTransportProducer?.Contract.IsAvailable ?? false);
                return false;
            }
            if (!qualification.TryValidateBuild(out reason))
            {
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.PipelineQualificationUnavailable,
                    reason,
                    producerAvailable: taggedTransportProducer?.Contract.IsAvailable ?? false);
                return false;
            }
            if (taggedTransportProducer is null)
            {
                reason = "caustic-tagged-hero-transport-producer-unavailable";
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.TaggedTransportProducerUnavailable,
                    reason,
                    producerAvailable: false);
                return false;
            }
            if (!taggedTransportProducer.Contract.TryValidateForLayout(
                    request.Layout,
                    out reason))
            {
                DisableAtSafeTransitionNoLock(
                    taggedTransportProducer.Contract.IsAvailable
                        ? GiCausticVulkanRuntimeCapabilityReason.TaggedTransportProducerContractInvalid
                        : GiCausticVulkanRuntimeCapabilityReason.TaggedTransportProducerUnavailable,
                    reason,
                    producerAvailable: taggedTransportProducer.Contract.IsAvailable);
                return false;
            }
            if (!_allocator.HasDescriptorContext)
            {
                reason = "caustic-bindless-descriptor-context-unavailable";
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }
            if (_renderTargets is null)
            {
                reason = "caustic-screen-render-target-manager-unavailable";
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenResolveUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }

            try
            {
                _pass ??= new GiCausticGpuPass(
                    _context,
                    _allocator.BindlessHeap!,
                    _bufferManager,
                    _accelerationStructureManager,
                    _pipelineCacheService);
            }
            catch (Exception exception)
            {
                reason = "caustic-compute-pipeline-unavailable:" + exception.GetType().Name;
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.PipelineUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }

            // Never replace an allocation while its descriptors still name it.
            _screenPass?.Dispose();
            _screenPass = null;
            if (!_allocator.TryBindFallback(out string fallbackReason))
            {
                reason = fallbackReason;
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }

            GiCausticGpuRuntimeSnapshot snapshot;
            try
            {
                snapshot = _resourceManager.Reconcile(request, _allocator);
            }
            catch (Exception exception)
            {
                reason = "caustic-resource-configuration-failed:" + exception.GetType().Name;
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ResourceAllocationFailed,
                    reason,
                    producerAvailable: true);
                return false;
            }
            if (!snapshot.IsEffectivelyEnabled ||
                !_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out _))
            {
                reason = snapshot.Reason;
                _resourceManager.Disable("caustic-runtime-allocation-not-active");
                _allocator.TryBindFallback(out _);
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ResourceAllocationFailed,
                    reason,
                    producerAvailable: true);
                return false;
            }
            if (!_allocator.TryBindAllocation(allocation.AllocationId, out reason))
            {
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }
            if (!_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out GiCausticNativeAllocation nativeAllocation))
            {
                reason = "caustic-screen-native-allocation-unavailable";
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenResolveUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }
            try
            {
                _screenPass = new GiCausticScreenGpuPass(
                    _context,
                    _allocator.BindlessHeap!,
                    _bufferManager,
                    _renderTargets,
                    request.Layout.ScreenResolve,
                    nativeAllocation.FrameConstantBuffers,
                    _pipelineCacheService);
            }
            catch (Exception exception)
            {
                reason = "caustic-screen-pipeline-unavailable:" +
                    exception.GetType().Name;
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenResolveUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }

            _qualification = qualification;
            UpdateDiagnosticsNoLock(
                GiCausticVulkanRuntimeCapabilityReason.None,
                "caustic-allocated-awaiting-tagged-transport-build",
                producerAvailable: true);
            reason = "caustic-allocated-awaiting-tagged-transport-build";
            return true;
        }
    }

    /// <summary>
    /// Freezes one frame's exact producer/revision tuple before graph
    /// execution. Preparation performs no upload, allocation, pipeline work,
    /// or barrier; consequently a rejected/no-hero frame remains genuinely
    /// zero-work.
    /// </summary>
    public bool TryPrepareGraphFrame(
        int frameIndex,
        in GiCausticCacheRevision revision,
        Vector4 cellOriginAndSize,
        IGiCausticTaggedTransportProducer? taggedTransportProducer,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            _preparedGraphFrames[frameIndex] = null;
            if (_pendingReadbacks[frameIndex].HasValue ||
                _pendingGraphBuilds[frameIndex].HasValue)
            {
                reason = "caustic-frame-slot-still-owned-by-prior-build";
                return false;
            }
            if (!_qualification.TryValidateBuild(out reason))
                return false;
            if (taggedTransportProducer is null)
            {
                reason = "caustic-tagged-hero-transport-producer-unavailable";
                return false;
            }
            if (!revision.IsValid || !Finite(cellOriginAndSize) ||
                cellOriginAndSize.W <= 0.0f)
            {
                reason = "caustic-frame-revision-or-cell-layout-invalid";
                return false;
            }
            if (!_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out GiCausticGpuResourceLayout layout) ||
                !_allocator.TryGetNativeAllocation(allocation.AllocationId, out _))
            {
                reason = "caustic-runtime-native-allocation-unavailable";
                return false;
            }
            if (!taggedTransportProducer.Contract.TryValidateForBuild(
                    layout, revision, out reason))
            {
                return false;
            }

            _preparedGraphFrames[frameIndex] = new PreparedGraphFrame(
                allocation.AllocationId,
                revision,
                cellOriginAndSize,
                taggedTransportProducer);
            reason = "caustic-graph-frame-prepared";
            return true;
        }
    }

    public bool CanRecordTaskStage(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
            return !_disposed && _preparedGraphFrames[frameIndex].HasValue;
    }

    /// <summary>
    /// Graph planning evaluates all candidate passes before the first C4
    /// dispatch. Keep the complete build chain selected from its prepared
    /// transaction; each recorder still validates the exact predecessor
    /// stage immediately before emitting commands.
    /// </summary>
    public bool CanExecuteBuildFrame(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            return !_disposed &&
                (_preparedGraphFrames[frameIndex].HasValue ||
                 _pendingGraphBuilds[frameIndex].HasValue);
        }
    }

    /// <summary>
    /// Freezes the current semantic cache identity for the screen pass. This
    /// performs no allocation or dispatch and cannot make an unpublished bank
    /// readable.
    /// </summary>
    public bool TryPrepareScreenFrame(
        int frameIndex,
        in GiCausticCacheRevision revision,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            _screenFrameRevisions[frameIndex] = null;
            if (!revision.IsValid)
            {
                reason = "caustic-screen-frame-revision-invalid";
                return false;
            }
            if (_pendingScreenFrames[frameIndex].HasValue)
            {
                reason = "caustic-screen-frame-slot-still-pending";
                return false;
            }
            if (_screenPass is null ||
                !_resourceManager.TryGetActiveAllocation(out _, out _))
            {
                reason = "caustic-screen-runtime-unavailable";
                return false;
            }

            _screenFrameRevisions[frameIndex] = revision;
            reason = "caustic-screen-frame-prepared";
            return true;
        }
    }

    public bool CanExecuteScreenFrame(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            return !_disposed && _screenPass is not null &&
                _screenFrameRevisions[frameIndex].HasValue &&
                _resourceManager.Snapshot.HasReadableCache;
        }
    }

    public bool IsReadableForRevision(in GiCausticCacheRevision revision)
    {
        lock (_sync)
        {
            return !_disposed && revision.IsValid &&
                _resourceManager.TryGetReadable(revision, out _, out _, out _);
        }
    }

    public bool CanRecordTraceStage(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            return !_disposed && _pendingGraphBuilds[frameIndex] is
                { Stage: GraphBuildStage.TaskRecorded };
        }
    }

    public bool CanRecordCacheBuildStage(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            return !_disposed && _pendingGraphBuilds[frameIndex] is
                { Stage: GraphBuildStage.TraceRecorded };
        }
    }

    public bool TryRecordTaskStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            PreparedGraphFrame? preparedValue = _preparedGraphFrames[frameIndex];
            if (!preparedValue.HasValue)
            {
                reason = "caustic-graph-frame-not-prepared";
                return false;
            }
            PreparedGraphFrame prepared = preparedValue.Value;
            if (!TryResolveGraphResourcesNoLock(
                    commandBuffer,
                    prepared.AllocationId,
                    out GiCausticGpuAllocation allocation,
                    out GiCausticGpuResourceLayout layout,
                    out GiCausticNativeAllocation nativeAllocation,
                    out reason))
            {
                _preparedGraphFrames[frameIndex] = null;
                return false;
            }

            GiCausticGpuBuildBeginResult begin = _resourceManager.BeginBuild(
                prepared.Revision,
                prepared.Producer.Contract.TaskCount,
                prepared.CellOriginAndSize);
            if (!begin.Started)
            {
                reason = begin.Reason;
                _preparedGraphFrames[frameIndex] = null;
                return false;
            }

            try
            {
                GiCausticTaggedTransportProducerContract contract =
                    prepared.Producer.Contract;
                var target = new GiCausticTaggedTransportTaskUploadTarget(
                    nativeAllocation.Buffers.Tasks,
                    layout.TaskRecordOffsetBytes,
                    contract.TaskPayloadBytes,
                    layout.EmitterRecordOffsetBytes,
                    layout.HeroRecordOffsetBytes,
                    layout.ProposalPairRecordOffsetBytes,
                    contract.EmitterCount,
                    contract.HeroCount,
                    contract.ProposalPairCount,
                    contract.MetadataPayloadBytes,
                    begin.Token);
                if (!target.IsValid || !prepared.Producer.TryRecordTaskUpload(
                        commandBuffer, target, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "caustic-tagged-hero-transport-task-upload-rejected"
                        : reason.Trim();
                    _resourceManager.AbortBuild(begin.Token, reason);
                    _preparedGraphFrames[frameIndex] = null;
                    return false;
                }

                _pass!.RecordTaskStage(commandBuffer, _resourceManager, layout,
                    begin.Token, nativeAllocation.Buffers, contract, frameIndex);
                _pendingGraphBuilds[frameIndex] = new PendingGraphBuild(
                    allocation.AllocationId,
                    begin.Token,
                    contract,
                    GraphBuildStage.TaskRecorded);
                _preparedGraphFrames[frameIndex] = null;
                reason = "caustic-task-stage-recorded";
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-task-stage-recording-failed:" +
                    exception.GetType().Name;
                _resourceManager.AbortBuild(begin.Token, reason);
                _preparedGraphFrames[frameIndex] = null;
                _pendingGraphBuilds[frameIndex] = null;
                return false;
            }
        }
    }

    public bool TryRecordTraceStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            PendingGraphBuild? pendingValue = _pendingGraphBuilds[frameIndex];
            if (pendingValue is not { Stage: GraphBuildStage.TaskRecorded })
            {
                reason = "caustic-task-stage-not-recorded";
                return false;
            }
            PendingGraphBuild pending = pendingValue.Value;
            if (!TryResolveGraphResourcesNoLock(commandBuffer,
                    pending.AllocationId, out _, out GiCausticGpuResourceLayout layout,
                    out GiCausticNativeAllocation nativeAllocation, out reason))
            {
                AbortGraphBuildNoLock(frameIndex, pending, reason);
                return false;
            }

            try
            {
                _pass!.RecordTraceStage(commandBuffer, _resourceManager, layout,
                    pending.Token, nativeAllocation.Buffers, frameIndex);
                _pendingGraphBuilds[frameIndex] = pending with
                {
                    Stage = GraphBuildStage.TraceRecorded
                };
                reason = "caustic-trace-stage-recorded";
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-trace-stage-recording-failed:" +
                    exception.GetType().Name;
                AbortGraphBuildNoLock(frameIndex, pending, reason);
                return false;
            }
        }
    }

    public bool TryRecordCacheBuildStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            PendingGraphBuild? pendingValue = _pendingGraphBuilds[frameIndex];
            if (pendingValue is not { Stage: GraphBuildStage.TraceRecorded })
            {
                reason = "caustic-trace-stage-not-recorded";
                return false;
            }
            PendingGraphBuild pending = pendingValue.Value;
            if (!TryResolveGraphResourcesNoLock(commandBuffer,
                    pending.AllocationId, out GiCausticGpuAllocation allocation,
                    out GiCausticGpuResourceLayout layout,
                    out GiCausticNativeAllocation nativeAllocation, out reason))
            {
                AbortGraphBuildNoLock(frameIndex, pending, reason);
                return false;
            }

            try
            {
                _pass!.RecordCacheBuildStage(commandBuffer, _resourceManager,
                    layout, pending.Token, nativeAllocation.Buffers);
                RecordHeaderReadback(commandBuffer, frameIndex,
                    allocation.AllocationId, layout, pending.Token,
                    nativeAllocation);
                _pendingGraphBuilds[frameIndex] = null;
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.None,
                    "caustic-task-trace-cache-build-recorded-awaiting-fence-readback",
                    producerAvailable: true);
                reason = "caustic-cache-build-stage-recorded";
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-cache-build-stage-recording-failed:" +
                    exception.GetType().Name;
                _pendingReadbacks[frameIndex] = null;
                AbortGraphBuildNoLock(frameIndex, pending, reason);
                return false;
            }
        }
    }

    private bool TryResolveGraphResourcesNoLock(
        CommandBuffer commandBuffer,
        ulong expectedAllocationId,
        out GiCausticGpuAllocation allocation,
        out GiCausticGpuResourceLayout layout,
        out GiCausticNativeAllocation nativeAllocation,
        out string reason)
    {
        allocation = null!;
        layout = default;
        nativeAllocation = null!;
        if (commandBuffer.Handle == 0)
        {
            reason = "caustic-command-buffer-invalid";
            return false;
        }
        if (_pass is null || !_resourceManager.TryGetActiveAllocation(
                out allocation, out layout) ||
            allocation.AllocationId != expectedAllocationId ||
            !_allocator.TryGetNativeAllocation(
                allocation.AllocationId, out nativeAllocation))
        {
            reason = "caustic-graph-native-allocation-or-pipeline-unavailable";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void AbortGraphBuildNoLock(
        int frameIndex,
        in PendingGraphBuild pending,
        string reason)
    {
        _resourceManager.AbortBuild(pending.Token, reason);
        _pendingGraphBuilds[frameIndex] = null;
        _preparedGraphFrames[frameIndex] = null;
        UpdateDiagnosticsNoLock(
            GiCausticVulkanRuntimeCapabilityReason.BuildSubmissionRejected,
            reason,
            producerAvailable: true);
    }

    /// <summary>
    /// Records the typed producer upload and exact C4 task/trace/cache-build
    /// sequence, then copies the selected write-bank header into a fence-owned
    /// host-visible buffer.  It cannot promote the bank; call
    /// <see cref="TryReadCompletedFrame"/> only after that submission's fence
    /// has signaled.
    /// </summary>
    public bool TryRecordBuild(
        CommandBuffer commandBuffer,
        int frameIndex,
        in GiCausticCacheRevision revision,
        Vector4 cellOriginAndSize,
        IGiCausticTaggedTransportProducer? taggedTransportProducer,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
            {
                reason = "caustic-command-buffer-invalid";
                return false;
            }
            if (_pendingReadbacks[frameIndex].HasValue)
            {
                reason = "caustic-frame-slot-header-readback-still-pending";
                return false;
            }
            if (!_qualification.TryValidateBuild(out reason))
            {
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.PipelineQualificationUnavailable,
                    reason,
                    producerAvailable: taggedTransportProducer?.Contract.IsAvailable ?? false);
                return false;
            }
            if (taggedTransportProducer is null)
            {
                reason = "caustic-tagged-hero-transport-producer-unavailable";
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.TaggedTransportProducerUnavailable,
                    reason,
                    producerAvailable: false);
                return false;
            }
            if (!_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out GiCausticGpuResourceLayout layout) ||
                !_allocator.TryGetNativeAllocation(allocation.AllocationId, out GiCausticNativeAllocation nativeAllocation))
            {
                reason = "caustic-runtime-native-allocation-unavailable";
                return false;
            }
            if (_pass is null)
            {
                reason = "caustic-runtime-compute-pipeline-unavailable";
                return false;
            }
            if (!taggedTransportProducer.Contract.TryValidateForBuild(layout, revision, out reason))
            {
                UpdateDiagnosticsNoLock(
                    taggedTransportProducer.Contract.IsAvailable
                        ? GiCausticVulkanRuntimeCapabilityReason.TaggedTransportProducerContractInvalid
                        : GiCausticVulkanRuntimeCapabilityReason.TaggedTransportProducerUnavailable,
                    reason,
                    producerAvailable: taggedTransportProducer.Contract.IsAvailable);
                return false;
            }

            GiCausticGpuBuildBeginResult begin = _resourceManager.BeginBuild(
                revision,
                taggedTransportProducer.Contract.TaskCount,
                cellOriginAndSize);
            if (!begin.Started)
            {
                reason = begin.Reason;
                return false;
            }

            try
            {
                var target = new GiCausticTaggedTransportTaskUploadTarget(
                    nativeAllocation.Buffers.Tasks,
                    layout.TaskRecordOffsetBytes,
                    taggedTransportProducer.Contract.TaskPayloadBytes,
                    layout.EmitterRecordOffsetBytes,
                    layout.HeroRecordOffsetBytes,
                    layout.ProposalPairRecordOffsetBytes,
                    taggedTransportProducer.Contract.EmitterCount,
                    taggedTransportProducer.Contract.HeroCount,
                    taggedTransportProducer.Contract.ProposalPairCount,
                    taggedTransportProducer.Contract.MetadataPayloadBytes,
                    begin.Token);
                reason = string.Empty;
                if (!target.IsValid || !taggedTransportProducer.TryRecordTaskUpload(
                        commandBuffer,
                        target,
                        out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "caustic-tagged-hero-transport-task-upload-rejected"
                        : reason.Trim();
                    _resourceManager.AbortBuild(begin.Token, reason);
                    UpdateDiagnosticsNoLock(
                        GiCausticVulkanRuntimeCapabilityReason.BuildSubmissionRejected,
                        reason,
                        producerAvailable: true);
                    return false;
                }

                _pass.RecordBuild(
                    commandBuffer,
                    _resourceManager,
                    layout,
                    begin.Token,
                    nativeAllocation.Buffers,
                    taggedTransportProducer.Contract,
                    frameIndex);
                RecordHeaderReadback(
                    commandBuffer,
                    frameIndex,
                    allocation.AllocationId,
                    layout,
                    begin.Token,
                    nativeAllocation);
            }
            catch (Exception exception)
            {
                reason = "caustic-gpu-recording-failed:" + exception.GetType().Name;
                _resourceManager.AbortBuild(begin.Token, reason);
                _pendingReadbacks[frameIndex] = null;
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.BuildSubmissionRejected,
                    reason,
                    producerAvailable: true);
                return false;
            }

            UpdateDiagnosticsNoLock(
                GiCausticVulkanRuntimeCapabilityReason.None,
                "caustic-task-trace-cache-build-recorded-awaiting-fence-readback",
                producerAvailable: true);
            reason = "caustic-task-trace-cache-build-recorded-awaiting-fence-readback";
            return true;
        }
    }

    /// <summary>
    /// Reads and validates only a fence-complete write-bank header.  An
    /// unsignaled fence leaves the readback pending; a malformed header aborts
    /// its token and preserves any older readable bank.
    /// </summary>
    public bool TryReadCompletedFrame(
        int frameIndex,
        Fence submissionFence,
        out GiCausticGpuPublicationResult publication)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (submissionFence.Handle == 0)
        {
            publication = new GiCausticGpuPublicationResult(
                false,
                GiCausticGpuPublicationFailure.GpuWorkIncomplete,
                "caustic-header-readback-fence-invalid");
            return false;
        }

        Result fenceStatus = _context.Api.GetFenceStatus(_context.Device, submissionFence);
        if (fenceStatus is Result.Success or Result.NotReady)
        {
            return TryReadCompletedFrame(
                frameIndex,
                fenceStatus == Result.Success,
                out publication);
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                AbortPendingReadbacksNoLock(
                    "caustic-header-readback-fence-status-failed:" + fenceStatus);
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.HeaderReadbackRejected,
                    "caustic-header-readback-fence-status-failed:" + fenceStatus,
                    producerAvailable: true);
            }
        }
        publication = new GiCausticGpuPublicationResult(
            false,
            GiCausticGpuPublicationFailure.GpuWorkIncomplete,
            "caustic-header-readback-fence-status-failed:" + fenceStatus);
        return false;
    }

    // Kept assembly-private for scheduler adapters that already own a
    // fence-completion result.  Public callers must pass the actual Vulkan
    // fence overload above; they cannot self-attest that GPU work completed.
    internal bool TryReadCompletedFrame(
        int frameIndex,
        bool submissionFenceSignaled,
        out GiCausticGpuPublicationResult publication)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            publication = new GiCausticGpuPublicationResult(
                false,
                GiCausticGpuPublicationFailure.NoBuildInFlight,
                "caustic-no-fence-complete-header-readback");
            PendingReadback? pending = _pendingReadbacks[frameIndex];
            if (!pending.HasValue)
                return false;
            if (!submissionFenceSignaled)
            {
                publication = new GiCausticGpuPublicationResult(
                    false,
                    GiCausticGpuPublicationFailure.GpuWorkIncomplete,
                    "caustic-header-readback-fence-not-signaled");
                return false;
            }

            PendingReadback expected = pending.Value;
            _pendingReadbacks[frameIndex] = null;
            if (!_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out _) ||
                allocation.AllocationId != expected.AllocationId ||
                !_allocator.TryGetNativeAllocation(expected.AllocationId, out GiCausticNativeAllocation nativeAllocation))
            {
                bool abortedCurrentBuild = _resourceManager.AbortBuild(
                    expected.Token,
                    "caustic-header-readback-allocation-no-longer-current");
                publication = new GiCausticGpuPublicationResult(
                    false,
                    GiCausticGpuPublicationFailure.NotEnabled,
                    "caustic-header-readback-allocation-no-longer-current");
                // AbortBuild is token-bound.  If it rejected this stale token,
                // another frame slot can already own the current replacement
                // build and its readback must remain intact.
                if (abortedCurrentBuild)
                    ClearPendingReadbacksNoLock();
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.HeaderReadbackRejected,
                    publication.Reason,
                    producerAvailable: true);
                return false;
            }

            try
            {
                BufferHandle readback = nativeAllocation.ReadbackBuffers[frameIndex];
                _bufferManager.InvalidateBuffer(readback, 0UL, HeaderReadbackBytes);
                GPUCausticCacheHeaderV1 header = *(GPUCausticCacheHeaderV1*)
                    _bufferManager.GetMappedPointer(readback);
                publication = _resourceManager.CompleteBuild(
                    expected.Token,
                    gpuWorkCompleted: true,
                    header: header);
                if (!publication.Published)
                {
                    // A semantic revision can replace an in-flight build before
                    // this older fence completes.  CompleteBuild deliberately
                    // returns TokenMismatch without cancelling the replacement;
                    // retain its independently owned frame-slot readback too.
                    if (!PreservesReplacementReadback(publication.Failure))
                        ClearPendingReadbacksNoLock();
                    UpdateDiagnosticsNoLock(
                        GiCausticVulkanRuntimeCapabilityReason.HeaderReadbackRejected,
                        publication.Reason,
                        producerAvailable: true);
                    return false;
                }

                _lastPublication =
                    GiCausticPublicationTelemetry.FromValidatedHeader(header);
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.None,
                    "caustic-write-bank-header-published-after-fence-readback",
                    producerAvailable: true);
                return true;
            }
            catch (Exception exception)
            {
                string reason = "caustic-header-readback-failed:" + exception.GetType().Name;
                bool abortedCurrentBuild =
                    _resourceManager.AbortBuild(expected.Token, reason);
                publication = new GiCausticGpuPublicationResult(
                    false,
                    GiCausticGpuPublicationFailure.GpuWorkIncomplete,
                    reason);
                if (abortedCurrentBuild)
                    ClearPendingReadbacksNoLock();
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.HeaderReadbackRejected,
                    reason,
                    producerAvailable: true);
                return false;
            }
        }
    }

    internal static bool PreservesReplacementReadback(
        GiCausticGpuPublicationFailure failure) =>
        failure == GiCausticGpuPublicationFailure.TokenMismatch;

    /// <summary>
    /// Records reset, visible-receiver tile compaction, and an indirect
    /// full-resolution gather against only the previously fence-published
    /// cache bank. A concurrently recorded write generation remains isolated.
    /// </summary>
    public bool TryRecordScreenResolve(
        CommandBuffer commandBuffer,
        int frameIndex,
        in GiCausticCacheRevision revision,
        SceneRenderingData sceneData,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        ArgumentNullException.ThrowIfNull(sceneData);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
            {
                reason = "caustic-screen-command-buffer-invalid";
                return false;
            }
            if (!sceneData.HasCurrentDepthPrePass ||
                !sceneData.HasCurrentGiCausticReceiverPayload)
            {
                reason = "caustic-screen-current-depth-or-receiver-payload-unavailable";
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenResolveRejected,
                    reason,
                    producerAvailable: true);
                return false;
            }
            if (_pendingScreenFrames[frameIndex].HasValue)
            {
                reason = "caustic-screen-frame-slot-already-recorded";
                return false;
            }
            if (_screenPass is null ||
                !_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out GiCausticGpuResourceLayout layout) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out GiCausticNativeAllocation nativeAllocation))
            {
                reason = "caustic-screen-native-resources-unavailable";
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenResolveUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }
            if (!_resourceManager.TryGetReadable(
                    revision,
                    out _,
                    out _,
                    out GPUCausticCacheHeaderV1 header))
            {
                reason = "caustic-screen-readable-cache-unavailable-or-stale";
                return false;
            }

            try
            {
                uint scratchWords =
                    GiCausticGpuVulkanRuntimeContract.GetScratchWordCapacity(
                        layout);
                GPUCausticPushConstantsV1 cacheConstants =
                    _resourceManager.CreateResolvePushConstants(
                        revision,
                        scratchWords,
                        resolveRequestWordOffset: 0u,
                        resolveRequestCount: 0u);
                GPUCausticScreenPushConstantsV1 screenConstants =
                    GPUCausticScreenPushConstantsV1.FromPublishedCache(
                        cacheConstants,
                        GiCausticScreenGpuFlags.ReversedZ |
                        GiCausticScreenGpuFlags.ReceiverPayloadValidated |
                        GiCausticScreenGpuFlags.SceneColorCompositeEnabled);
                if (screenConstants.CacheGeneration != header.CacheGeneration)
                    throw new InvalidOperationException(
                        "C4 screen constants do not identify the published cache.");
                _screenPass.RecordResolve(
                    commandBuffer,
                    frameIndex,
                    sceneData,
                    screenConstants,
                    nativeAllocation.Buffers.Scratch);
                _pendingScreenFrames[frameIndex] = new PendingScreenFrame(
                    allocation.AllocationId,
                    revision,
                    screenConstants,
                    ScreenFrameStage.ResolveRecorded);
                reason = "caustic-screen-resolve-recorded";
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-screen-resolve-recording-failed:" +
                    exception.GetType().Name;
                _pendingScreenFrames[frameIndex] = null;
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenResolveRejected,
                    reason,
                    producerAvailable: true);
                return false;
            }
        }
    }

    public bool TryRecordPreparedScreenResolve(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        GiCausticCacheRevision? revision;
        lock (_sync)
        {
            ThrowIfDisposed();
            revision = _screenFrameRevisions[frameIndex];
        }
        if (!revision.HasValue)
        {
            reason = "caustic-screen-frame-not-prepared";
            return false;
        }
        return TryRecordScreenResolve(
            commandBuffer, frameIndex, revision.Value, sceneData, out reason);
    }

    /// <summary>
    /// Composites the resolved C4 radiance exactly once into canonical scene
    /// color. Confidence remains diagnostic and is not a second energy weight.
    /// </summary>
    public bool TryRecordScreenComposite(
        CommandBuffer commandBuffer,
        int frameIndex,
        in GiCausticCacheRevision revision,
        SceneRenderingData sceneData,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        ArgumentNullException.ThrowIfNull(sceneData);
        lock (_sync)
        {
            ThrowIfDisposed();
            PendingScreenFrame? pendingValue =
                _pendingScreenFrames[frameIndex];
            if (commandBuffer.Handle == 0 ||
                pendingValue is not { Stage: ScreenFrameStage.ResolveRecorded })
            {
                reason = "caustic-screen-resolve-not-recorded";
                return false;
            }
            PendingScreenFrame pending = pendingValue.Value;
            if (!pending.Revision.Equals(revision) ||
                _screenPass is null ||
                !_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out _) ||
                allocation.AllocationId != pending.AllocationId ||
                !_allocator.TryGetNativeAllocation(
                    pending.AllocationId,
                    out GiCausticNativeAllocation nativeAllocation))
            {
                reason = "caustic-screen-composite-identity-mismatch";
                _pendingScreenFrames[frameIndex] = null;
                _screenFrameRevisions[frameIndex] = null;
                return false;
            }

            try
            {
                _screenPass.RecordComposite(
                    commandBuffer,
                    frameIndex,
                    sceneData,
                    pending.PushConstants,
                    nativeAllocation.Buffers.Scratch);
                _pendingScreenFrames[frameIndex] = null;
                _screenFrameRevisions[frameIndex] = null;
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.None,
                    "caustic-screen-composite-recorded",
                    producerAvailable: true);
                reason = "caustic-screen-composite-recorded";
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-screen-composite-recording-failed:" +
                    exception.GetType().Name;
                _pendingScreenFrames[frameIndex] = null;
                _screenFrameRevisions[frameIndex] = null;
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenCompositeRejected,
                    reason,
                    producerAvailable: true);
                return false;
            }
        }
    }

    public bool TryRecordPreparedScreenComposite(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        GiCausticCacheRevision? revision;
        lock (_sync)
        {
            ThrowIfDisposed();
            revision = _screenFrameRevisions[frameIndex];
        }
        if (!revision.HasValue)
        {
            reason = "caustic-screen-frame-not-prepared";
            return false;
        }
        return TryRecordScreenComposite(
            commandBuffer, frameIndex, revision.Value, sceneData, out reason);
    }

    /// <summary>
    /// Lets a typed legacy forward consumer upload bounded receiver requests,
    /// records C4 resolve, and returns a scratch-only handoff.  The handoff
    /// never performs or implies a scene-color composite; the consumer must
    /// use it in the correctly synchronized forward path.
    /// </summary>
    public bool TryRecordResolveForForwardComposite(
        CommandBuffer commandBuffer,
        in GiCausticCacheRevision revision,
        IGiCausticForwardCompositeConsumer? forwardCompositeConsumer,
        out GiCausticForwardCompositeHandoff handoff,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            handoff = GiCausticForwardCompositeHandoff.Disabled(
                "caustic-forward-composite-handoff-unavailable");
            if (commandBuffer.Handle == 0)
            {
                reason = "caustic-command-buffer-invalid";
                return false;
            }
            if (forwardCompositeConsumer is null)
            {
                reason = "caustic-forward-composite-consumer-unavailable";
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ForwardCompositeConsumerUnavailable,
                    reason,
                    producerAvailable: false);
                return false;
            }
            if (!_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out GiCausticGpuResourceLayout layout) ||
                !_allocator.TryGetNativeAllocation(allocation.AllocationId, out GiCausticNativeAllocation nativeAllocation))
            {
                reason = "caustic-forward-composite-native-allocation-unavailable";
                return false;
            }
            uint scratchWordCapacity = GiCausticGpuVulkanRuntimeContract.GetScratchWordCapacity(layout);
            if (!forwardCompositeConsumer.Contract.TryValidate(scratchWordCapacity, out reason))
            {
                UpdateDiagnosticsNoLock(
                    forwardCompositeConsumer.Contract.IsAvailable
                        ? GiCausticVulkanRuntimeCapabilityReason.ForwardCompositeHandoffRejected
                        : GiCausticVulkanRuntimeCapabilityReason.ForwardCompositeConsumerUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }
            if (!_resourceManager.TryGetReadable(
                    revision,
                    out _,
                    out _,
                    out GPUCausticCacheHeaderV1 header))
            {
                reason = "caustic-forward-composite-readable-cache-unavailable-or-revision-mismatch";
                return false;
            }
            if (_pass is null)
            {
                reason = "caustic-forward-composite-resolve-pipeline-unavailable";
                return false;
            }

            GiCausticForwardCompositeConsumerContract contract =
                forwardCompositeConsumer.Contract;
            try
            {
                var target = new GiCausticForwardCompositeRequestTarget(
                    nativeAllocation.Buffers.Scratch,
                    (uint)GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot,
                    contract.ResolveRequestWordOffset,
                    contract.ResolveRequestCount,
                    header.CacheGeneration,
                    revision);
                reason = string.Empty;
                if (!target.IsValid || !forwardCompositeConsumer.TryRecordResolveRequests(
                        commandBuffer,
                        target,
                        out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "caustic-forward-composite-resolve-request-upload-rejected"
                        : reason.Trim();
                    UpdateDiagnosticsNoLock(
                        GiCausticVulkanRuntimeCapabilityReason.ForwardCompositeHandoffRejected,
                        reason,
                        producerAvailable: true);
                    return false;
                }

                GPUCausticPushConstantsV1 constants =
                    _resourceManager.CreateResolvePushConstants(
                        revision,
                        scratchWordCapacity,
                        contract.ResolveRequestWordOffset,
                        contract.ResolveRequestCount);
                _pass.RecordResolve(
                    commandBuffer,
                    constants,
                    nativeAllocation.Buffers,
                    contract);

                uint resultOffset = checked(
                    contract.ResolveRequestWordOffset +
                    contract.ResolveRequestCount *
                        (GiCausticGpuAbi.ResolveRequestBytes / sizeof(uint)));
                handoff = new GiCausticForwardCompositeHandoff(
                    IsAvailable: true,
                    C4GpuAbiVersion: GiCausticGpuAbi.Version,
                    ScratchBufferBindlessIndex:
                        (uint)GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot,
                    ResolveResultWordOffset: resultOffset,
                    ResolveResultCount: contract.ResolveRequestCount,
                    CacheGeneration: header.CacheGeneration,
                    ConsumerReadStageMask: contract.CompositeReadStageMask,
                    ConsumerReadAccessMask: contract.CompositeReadAccessMask,
                    Reason: "caustic-forward-composite-handoff-recorded");
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.None,
                    handoff.Reason,
                    producerAvailable: true);
                reason = handoff.Reason;
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-forward-composite-resolve-recording-failed:" +
                    exception.GetType().Name;
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ForwardCompositeHandoffRejected,
                    reason,
                    producerAvailable: true);
                return false;
            }
        }
    }

    /// <summary>Invalidates stale published placement without touching non-C4 state.</summary>
    public void Invalidate(in GiCausticCacheRevision currentRevision, string reason)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _resourceManager.Invalidate(currentRevision, reason);
            if (!_resourceManager.Snapshot.HasReadableCache)
            {
                _lastPublication = GiCausticPublicationTelemetry.Empty;
                UpdateDiagnosticsNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.None,
                    string.IsNullOrWhiteSpace(reason)
                        ? "caustic-cache-invalidated"
                        : reason.Trim(),
                    producerAvailable: true);
            }
        }
    }

    /// <summary>Aborts an unsubmitted C4 recording and retains no pending header readback.</summary>
    public void AbortPendingBuild(string reason = "caustic-build-submission-aborted")
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            AbortPendingReadbacksNoLock(reason);
            AbortPendingGraphBuildsNoLock(reason);
            AbortPendingScreenFramesNoLock(reason);
            UpdateDiagnosticsNoLock(
                GiCausticVulkanRuntimeCapabilityReason.BuildSubmissionRejected,
                string.IsNullOrWhiteSpace(reason)
                    ? "caustic-build-submission-aborted"
                    : reason.Trim(),
                producerAvailable: true);
        }
    }

    /// <summary>
    /// Releases the complete C4 allocation after the renderer has established
    /// device-idle ownership. This is used for evidence-invalidating extent
    /// transitions; no stale descriptor or readable cache survives it.
    /// </summary>
    public void DisableAndReleaseAfterDeviceIdle(string reason)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            DisableAtSafeTransitionNoLock(
                GiCausticVulkanRuntimeCapabilityReason.EffectiveModeDisabled,
                string.IsNullOrWhiteSpace(reason)
                    ? "caustic-runtime-disabled"
                    : reason.Trim(),
                producerAvailable: false);
        }
    }

    /// <summary>
    /// Rebuilds only C4's private screen descriptors/pipelines after same-
    /// extent render targets were recreated under device-idle ownership.
    /// World-cache buffers and their published generation remain intact.
    /// </summary>
    public bool RecreateScreenPassAfterDeviceIdle(out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            AbortPendingScreenFramesNoLock(
                "caustic-screen-targets-recreated");
            _screenPass?.Dispose();
            _screenPass = null;
            if (_renderTargets is null ||
                !_resourceManager.TryGetActiveAllocation(
                    out GiCausticGpuAllocation allocation,
                    out GiCausticGpuResourceLayout layout) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out GiCausticNativeAllocation nativeAllocation))
            {
                reason = "caustic-screen-recreate-native-resources-unavailable";
                return false;
            }
            try
            {
                _screenPass = new GiCausticScreenGpuPass(
                    _context,
                    _allocator.BindlessHeap!,
                    _bufferManager,
                    _renderTargets,
                    layout.ScreenResolve,
                    nativeAllocation.FrameConstantBuffers,
                    _pipelineCacheService);
                reason = "caustic-screen-pass-recreated";
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-screen-pass-recreate-failed:" +
                    exception.GetType().Name;
                DisableAtSafeTransitionNoLock(
                    GiCausticVulkanRuntimeCapabilityReason.ScreenResolveUnavailable,
                    reason,
                    producerAvailable: true);
                return false;
            }
        }
    }

    private void RecordHeaderReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        ulong allocationId,
        in GiCausticGpuResourceLayout layout,
        in GiCausticGpuBuildToken token,
        GiCausticNativeAllocation nativeAllocation)
    {
        BufferHandle readbackHandle = nativeAllocation.ReadbackBuffers[frameIndex];
        if (!readbackHandle.IsValid)
            throw new InvalidOperationException("C4 cache-header readback buffer is unavailable.");

        ulong headerOffset = GiCausticGpuVulkanRuntimeContract.GetCacheHeaderOffsetBytes(
            layout,
            token.CacheWriteBankIndex);
        VkBuffer source = _bufferManager.GetBuffer(nativeAllocation.Buffers.Cache);
        VkBuffer destination = _bufferManager.GetBuffer(readbackHandle);
        BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
            source,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            headerOffset,
            HeaderReadbackBytes);
        ExecuteBufferBarrier(commandBuffer, beforeCopy);

        var copy = new BufferCopy
        {
            SrcOffset = headerOffset,
            DstOffset = 0UL,
            Size = HeaderReadbackBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            source,
            destination,
            1u,
            &copy);

        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            destination,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit,
            0UL,
            HeaderReadbackBytes);
        ExecuteBufferBarrier(commandBuffer, afterCopy);
        _pendingReadbacks[frameIndex] = new PendingReadback(allocationId, token);
    }

    private void DisableAtSafeTransitionNoLock(
        GiCausticVulkanRuntimeCapabilityReason capabilityReason,
        string detail,
        bool producerAvailable)
    {
        AbortPendingReadbacksNoLock(detail);
        AbortPendingGraphBuildsNoLock(detail);
        AbortPendingScreenFramesNoLock(detail);
        _allocator.TryBindFallback(out _);
        _resourceManager.Disable(detail);
        _screenPass?.Dispose();
        _screenPass = null;
        _pass?.Dispose();
        _pass = null;
        _qualification = default;
        _lastPublication = GiCausticPublicationTelemetry.Empty;
        UpdateDiagnosticsNoLock(capabilityReason, detail, producerAvailable);
    }

    private void ClearPendingReadbacksNoLock() => Array.Clear(_pendingReadbacks);

    private void AbortPendingReadbacksNoLock(string reason)
    {
        foreach (PendingReadback? pending in _pendingReadbacks)
        {
            if (pending.HasValue)
                _resourceManager.AbortBuild(pending.Value.Token, reason);
        }
        ClearPendingReadbacksNoLock();
    }

    private void AbortPendingGraphBuildsNoLock(string reason)
    {
        string normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "caustic-graph-build-aborted"
            : reason.Trim();
        for (int frameIndex = 0; frameIndex < _pendingGraphBuilds.Length;
             ++frameIndex)
        {
            PendingGraphBuild? pending = _pendingGraphBuilds[frameIndex];
            if (pending.HasValue)
                _resourceManager.AbortBuild(pending.Value.Token, normalizedReason);
        }
        Array.Clear(_pendingGraphBuilds);
        Array.Clear(_preparedGraphFrames);
    }

    private void AbortPendingScreenFramesNoLock(string reason)
    {
        _ = reason;
        Array.Clear(_pendingScreenFrames);
        Array.Clear(_screenFrameRevisions);
    }

    private void SynchronizeDescriptorReadersNoLock() => _waitForDescriptorReaders?.Invoke();

    private void UpdateDiagnosticsNoLock(
        GiCausticVulkanRuntimeCapabilityReason capabilityReason,
        string detail,
        bool producerAvailable)
    {
        Diagnostics = new GiCausticVulkanRuntimeDiagnostics(
            capabilityReason,
            producerAvailable,
            _qualification.DeterministicParallelCacheBuildQualified,
            _allocator.HasDescriptorContext,
            HasPendingReadbackNoLock(),
            _resourceManager.Snapshot,
            string.IsNullOrWhiteSpace(detail) ? "unknown" : detail.Trim())
        {
            Publication = _lastPublication
        };
    }

    private bool HasPendingReadbackNoLock()
    {
        foreach (PendingReadback? pending in _pendingReadbacks)
        {
            if (pending.HasValue)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            AbortPendingReadbacksNoLock("caustic-runtime-disposed");
            AbortPendingGraphBuildsNoLock("caustic-runtime-disposed");
            AbortPendingScreenFramesNoLock("caustic-runtime-disposed");
            _disposed = true;
            _allocator.TryBindFallback(out _);
            _resourceManager.Dispose();
            _screenPass?.Dispose();
            _screenPass = null;
            _pass?.Dispose();
            _pass = null;
            _allocator.Dispose();
            _qualification = default;
            _lastPublication = GiCausticPublicationTelemetry.Empty;
            Diagnostics = new GiCausticVulkanRuntimeDiagnostics(
                GiCausticVulkanRuntimeCapabilityReason.Disposed,
                false,
                false,
                false,
                false,
                _resourceManager.Snapshot,
                "disposed");
        }
    }

    private void ExecuteBufferBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GiCausticVulkanRuntime));
    }

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private enum GraphBuildStage : byte
    {
        TaskRecorded = 1,
        TraceRecorded = 2
    }

    private enum ScreenFrameStage : byte
    {
        ResolveRecorded = 1
    }

    private readonly record struct PreparedGraphFrame(
        ulong AllocationId,
        GiCausticCacheRevision Revision,
        Vector4 CellOriginAndSize,
        IGiCausticTaggedTransportProducer Producer);

    private readonly record struct PendingGraphBuild(
        ulong AllocationId,
        GiCausticGpuBuildToken Token,
        GiCausticTaggedTransportProducerContract ProducerContract,
        GraphBuildStage Stage);

    private readonly record struct PendingReadback(
        ulong AllocationId,
        GiCausticGpuBuildToken Token);

    private readonly record struct PendingScreenFrame(
        ulong AllocationId,
        GiCausticCacheRevision Revision,
        GPUCausticScreenPushConstantsV1 PushConstants,
        ScreenFrameStage Stage);

    private sealed class VulkanAllocator : IGiCausticGpuResourceAllocator, IDisposable
    {
        private readonly BufferManager _bufferManager;
        private readonly Dictionary<ulong, GiCausticNativeAllocation> _allocations = new();
        private BindlessHeap? _bindlessHeap;
        private BufferHandle _fallbackBuffer = BufferHandle.Invalid;
        private ulong _fallbackBytes;
        private ulong _nextAllocationId;
        private bool _disposed;

        public VulkanAllocator(BufferManager bufferManager)
        {
            _bufferManager = bufferManager;
        }

        public bool HasDescriptorContext =>
            !_disposed && _bindlessHeap is not null && _fallbackBuffer.IsValid &&
            _fallbackBytes >= sizeof(uint);

        public BindlessHeap? BindlessHeap => _bindlessHeap;

        public bool TrySetDescriptorContext(
            BindlessHeap bindlessHeap,
            BufferHandle fallbackBuffer,
            ulong fallbackBytes,
            out string reason)
        {
            if (_disposed)
            {
                reason = "caustic-vulkan-allocator-disposed";
                return false;
            }
            if (!fallbackBuffer.IsValid || fallbackBytes < sizeof(uint))
            {
                reason = "caustic-safe-descriptor-fallback-invalid";
                return false;
            }
            try
            {
                if (_bufferManager.GetBufferSize(fallbackBuffer) < fallbackBytes)
                {
                    reason = "caustic-safe-descriptor-fallback-range-exceeds-buffer";
                    return false;
                }
            }
            catch (Exception exception)
            {
                reason = "caustic-safe-descriptor-fallback-not-live:" +
                    exception.GetType().Name;
                return false;
            }

            _bindlessHeap = bindlessHeap;
            _fallbackBuffer = fallbackBuffer;
            _fallbackBytes = fallbackBytes;
            return TryBindFallback(out reason);
        }

        public GiCausticGpuAllocation Allocate(in GiCausticGpuResourceLayout layout)
        {
            ThrowIfDisposed();
            if (!GiCausticGpuVulkanRuntimeContract.TryValidateRecordingLayout(layout, out string reason))
            {
                throw new ArgumentException(
                    "C4 Vulkan allocation requires the strict recording layout: " + reason,
                    nameof(layout));
            }

            BufferHandle tasks = BufferHandle.Invalid;
            BufferHandle photons = BufferHandle.Invalid;
            BufferHandle cache = BufferHandle.Invalid;
            BufferHandle scratch = BufferHandle.Invalid;
            var readbacks = new BufferHandle[RenderingConstants.FramesInFlight];
            var frameConstants =
                new BufferHandle[RenderingConstants.FramesInFlight];
            try
            {
                tasks = _bufferManager.CreateDeviceBuffer(
                    layout.TaskQueueBytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    "GI Caustic Task Queue");
                photons = _bufferManager.CreateDeviceBuffer(
                    checked(layout.CandidateStagingBytes + layout.PublishedPhotonBytes),
                    BufferUsageFlags.StorageBufferBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    "GI Caustic Candidate and Two Photon Banks");
                cache = _bufferManager.CreateDeviceBuffer(
                    layout.CacheBytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    "GI Caustic Two Cache Banks and Headers");
                scratch = _bufferManager.CreateDeviceBuffer(
                    layout.ScratchBytes,
                    BufferUsageFlags.StorageBufferBit |
                        BufferUsageFlags.TransferDstBit |
                        BufferUsageFlags.IndirectBufferBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    "GI Caustic Deterministic Build and Resolve Scratch");
                for (int frameIndex = 0; frameIndex < readbacks.Length; ++frameIndex)
                {
                    readbacks[frameIndex] = _bufferManager.CreateBuffer(
                        HeaderReadbackBytes,
                        BufferUsageFlags.TransferDstBit,
                        MemoryUsage.AutoPreferHost,
                        AllocationCreateFlags.MappedBit |
                            AllocationCreateFlags.HostAccessRandomBit,
                        $"GI Caustic Cache Header Readback Frame {frameIndex}",
                        MemoryBudgetCategory.GlobalIllumination);
                    frameConstants[frameIndex] = _bufferManager.CreateBuffer(
                        GiCausticScreenGpuAbi.FrameConstantsBytes,
                        BufferUsageFlags.StorageBufferBit,
                        MemoryUsage.AutoPreferHost,
                        AllocationCreateFlags.MappedBit |
                            AllocationCreateFlags.HostAccessSequentialWriteBit,
                        $"GI Caustic Screen Constants Frame {frameIndex}",
                        MemoryBudgetCategory.GlobalIllumination);
                }

                ulong allocationId = NextAllocationId();
                var buffers = new GiCausticVulkanBuffers(tasks, photons, cache, scratch);
                _allocations.Add(allocationId, new GiCausticNativeAllocation(
                    buffers,
                    readbacks,
                    frameConstants));
                return new GiCausticGpuAllocation(
                    allocationId,
                    new GiCausticGpuBuffer(_bufferManager.GetBuffer(tasks).Handle,
                        layout.TaskQueueBytes),
                    new GiCausticGpuBuffer(_bufferManager.GetBuffer(photons).Handle,
                        checked(layout.CandidateStagingBytes + layout.PublishedPhotonBytes)),
                    new GiCausticGpuBuffer(_bufferManager.GetBuffer(cache).Handle,
                        layout.CacheBytes),
                    new GiCausticGpuBuffer(_bufferManager.GetBuffer(scratch).Handle,
                        layout.ScratchBytes),
                    GiCausticGpuAbi.DescriptorCount);
            }
            catch
            {
                Destroy(tasks);
                Destroy(photons);
                Destroy(cache);
                Destroy(scratch);
                foreach (BufferHandle readback in readbacks)
                    Destroy(readback);
                foreach (BufferHandle frameConstant in frameConstants)
                    Destroy(frameConstant);
                throw;
            }
        }

        public void Retire(GiCausticGpuAllocation allocation)
        {
            if (!_allocations.Remove(allocation.AllocationId, out GiCausticNativeAllocation? native))
                return;
            Destroy(native.Buffers.Tasks);
            Destroy(native.Buffers.Photons);
            Destroy(native.Buffers.Cache);
            Destroy(native.Buffers.Scratch);
            foreach (BufferHandle readback in native.ReadbackBuffers)
                Destroy(readback);
            foreach (BufferHandle frameConstant in native.FrameConstantBuffers)
                Destroy(frameConstant);
        }

        public bool TryGetNativeAllocation(
            ulong allocationId,
            out GiCausticNativeAllocation allocation)
        {
            if (_allocations.TryGetValue(allocationId, out GiCausticNativeAllocation? native))
            {
                allocation = native;
                return true;
            }

            allocation = null!;
            return false;
        }

        public bool TryBindAllocation(ulong allocationId, out string reason)
        {
            if (!HasDescriptorContext)
            {
                reason = "caustic-bindless-descriptor-context-unavailable";
                return false;
            }
            if (!_allocations.TryGetValue(allocationId, out GiCausticNativeAllocation? native))
            {
                reason = "caustic-native-allocation-not-found";
                return false;
            }

            try
            {
                GiCausticGpuBindlessSlots slots = GiCausticGpuAbi.BindlessSlots;
                Register((uint)slots.TaskBufferIndex, native.Buffers.Tasks,
                    _bufferManager.GetBufferSize(native.Buffers.Tasks));
                Register((uint)slots.PhotonBufferIndex, native.Buffers.Photons,
                    _bufferManager.GetBufferSize(native.Buffers.Photons));
                Register((uint)slots.CacheBufferIndex, native.Buffers.Cache,
                    _bufferManager.GetBufferSize(native.Buffers.Cache));
                Register((uint)slots.ScratchBufferIndex, native.Buffers.Scratch,
                    _bufferManager.GetBufferSize(native.Buffers.Scratch));
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-bindless-descriptor-publication-failed:" +
                    exception.GetType().Name;
                return false;
            }
        }

        public bool TryBindFallback(out string reason)
        {
            if (!HasDescriptorContext)
            {
                reason = "caustic-safe-descriptor-fallback-unavailable";
                return false;
            }
            try
            {
                Register((uint)GiCausticGpuVulkanRuntimeContract.TaskBufferBindlessSlot,
                    _fallbackBuffer, _fallbackBytes);
                Register((uint)GiCausticGpuVulkanRuntimeContract.PhotonBufferBindlessSlot,
                    _fallbackBuffer, _fallbackBytes);
                Register((uint)GiCausticGpuVulkanRuntimeContract.CacheBufferBindlessSlot,
                    _fallbackBuffer, _fallbackBytes);
                Register((uint)GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot,
                    _fallbackBuffer, _fallbackBytes);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = "caustic-safe-descriptor-publication-failed:" +
                    exception.GetType().Name;
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (GiCausticNativeAllocation allocation in _allocations.Values)
            {
                Destroy(allocation.Buffers.Tasks);
                Destroy(allocation.Buffers.Photons);
                Destroy(allocation.Buffers.Cache);
                Destroy(allocation.Buffers.Scratch);
                foreach (BufferHandle readback in allocation.ReadbackBuffers)
                    Destroy(readback);
                foreach (BufferHandle frameConstant in allocation.FrameConstantBuffers)
                    Destroy(frameConstant);
            }
            _allocations.Clear();
            _bindlessHeap = null;
            _fallbackBuffer = BufferHandle.Invalid;
            _fallbackBytes = 0UL;
        }

        private void Register(uint slot, BufferHandle buffer, ulong bytes)
        {
            if (slot > int.MaxValue || !buffer.IsValid || bytes == 0UL)
                throw new InvalidOperationException("C4 bindless descriptor arguments are invalid.");
            _bindlessHeap!.RegisterStorageBuffer(
                (int)slot,
                _bufferManager.GetBuffer(buffer),
                0UL,
                bytes);
        }

        private ulong NextAllocationId()
        {
            do
            {
                _nextAllocationId = _nextAllocationId == ulong.MaxValue
                    ? 1UL
                    : _nextAllocationId + 1UL;
            }
            while (_allocations.ContainsKey(_nextAllocationId));
            return _nextAllocationId;
        }

        private void Destroy(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VulkanAllocator));
        }
    }
}

/// <summary>Four exact C4 buffers owned by one Vulkan allocation epoch.</summary>
internal readonly record struct GiCausticVulkanBuffers(
    BufferHandle Tasks,
    BufferHandle Photons,
    BufferHandle Cache,
    BufferHandle Scratch)
{
    public bool IsComplete => Tasks.IsValid && Photons.IsValid && Cache.IsValid && Scratch.IsValid;
}

/// <summary>Native backing plus per-frame host-visible cache-header readbacks.</summary>
internal sealed class GiCausticNativeAllocation
{
    public GiCausticNativeAllocation(
        GiCausticVulkanBuffers buffers,
        BufferHandle[] readbackBuffers,
        BufferHandle[] frameConstantBuffers)
    {
        Buffers = buffers;
        ReadbackBuffers = readbackBuffers ?? throw new ArgumentNullException(nameof(readbackBuffers));
        FrameConstantBuffers = frameConstantBuffers ??
            throw new ArgumentNullException(nameof(frameConstantBuffers));
        if (ReadbackBuffers.Length != RenderingConstants.FramesInFlight ||
            FrameConstantBuffers.Length != RenderingConstants.FramesInFlight)
        {
            throw new ArgumentException(
                "C4 native frame rings must match frames in flight.");
        }
    }

    public GiCausticVulkanBuffers Buffers { get; }

    public BufferHandle[] ReadbackBuffers { get; }

    public BufferHandle[] FrameConstantBuffers { get; }
}
