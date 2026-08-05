using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    public enum SimpleDdgiLayoutDecision : uint
    {
        Accepted = 0,
        RejectedBudget = 1,
        RejectedVolumeLimit = 2
    }

    /// <summary>One pre-allocation volume request in deterministic priority order.</summary>
    public sealed record SimpleDdgiLayoutVolumeRequest(
        string Id,
        int SourceOrdinal,
        bool IsAuthored,
        SimpleDdgiVolumePurpose Purpose,
        int Priority,
        float Spacing,
        int ProbeCount)
    {
        public int GridCountX { get; init; }
        public int GridCountY { get; init; }
        public int GridCountZ { get; init; }
        public bool SparseNearRingEligible { get; init; }
        public bool DenseCoarserRingEligible { get; init; }

        public int VirtualPageCount => SparseNearRingEligible &&
            GridCountX > 0 && GridCountY > 0 && GridCountZ > 0
                ? SimpleDdgiProbePageLayout.ResolveVirtualPageCount(
                    GridCountX,
                    GridCountY,
                    GridCountZ)
                : 0;
    }

    /// <summary>Requested/accepted accounting for one layout volume.</summary>
    public sealed record SimpleDdgiLayoutVolumeDecision(
        SimpleDdgiLayoutVolumeRequest Request,
        SimpleDdgiLayoutDecision Decision,
        int AcceptedProbeCount,
        ulong EstimatedPersistentBytes,
        string Reason)
    {
        /// <summary>
        /// Incremental live bytes contributed by this request in the original
        /// deterministic request stream. This remains populated for rejected
        /// requests so persisted admission evidence never has to reconstruct a
        /// potentially different capacity plan.
        /// </summary>
        public ulong RequestedPersistentBytes { get; init; }
    }

    /// <summary>
    /// Hard per-tier layout budget. Despite the compatibility name on
    /// <see cref="PersistentMemoryBudgetBytes"/>, admission charges every live
    /// manager allocation: persistent probe data, bounded work buffers, feedback
    /// readbacks, and the optional sampled mirror.
    /// </summary>
    public readonly record struct SimpleDdgiLayoutBudget(
        DdgiQualityTier Tier,
        int ProbeBudget,
        ulong PersistentMemoryBudgetBytes,
        int VolumeBudget)
    {
        public static SimpleDdgiLayoutBudget Resolve(GlobalIlluminationSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            int probes = settings.DdgiQualityTier switch
            {
                DdgiQualityTier.DdgiLow => 4_096,
                DdgiQualityTier.DdgiMedium => 8_192,
                DdgiQualityTier.DdgiUltra => GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount,
                _ => 16_384
            };

            // The DDGI atlas cap is the authoritative cache cap for the active
            // tier.  The layout compiler reserves the optional sampled mirror as
            // part of persistent storage rather than admitting it afterward.
            return new SimpleDdgiLayoutBudget(
                settings.DdgiQualityTier,
                probes,
                settings.DdgiAtlasMemoryBudgetBytes,
                GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);
        }
    }

    /// <summary>
    /// Pure byte-for-byte capacity plan shared by pre-allocation admission and
    /// the Vulkan manager. Work buffers contain transient data, but their
    /// allocations remain resident and therefore consume the same hard component
    /// budget while the manager is alive.
    /// </summary>
    public readonly record struct SimpleDdgiMemoryPlan(
        int VirtualProbeCount,
        int DensePayloadProbeCount,
        int SparseVirtualProbeCount,
        int SparseVirtualPageCount,
        int SparsePhysicalPageCapacity,
        int PhysicalProbeCapacity,
        int SampledAtlasPhysicalProbeCapacity,
        SimpleDdgiProbeResidencyMode ResidencyMode,
        int UpdateRequestCapacity,
        int RayCapacity,
        int ReadbackBufferCount,
        ulong ParamsBytes,
        ulong IrradianceAtlasBytes,
        ulong VisibilityAtlasBytes,
        ulong TransportIrradianceBytes,
        ulong TransportSourceCacheBytes,
        ulong ProbeStateBytes,
        ulong ReceiverProbeBytes,
        ulong UpdateQueueBytes,
        ulong RelocationClassificationBytes,
        ulong ProbeStateReadbackBytes,
        ulong RayScratchBytes,
        ulong SampledAtlasImageBytes,
        ulong ResidencyArenaBytes,
        ulong ResidencyFeedbackReadbackBytes,
        SimpleDdgiSchedulerMode SchedulerMode,
        int SchedulerActiveLaneCount,
        ulong SchedulerArenaBytes,
        ulong SchedulerFeedbackReadbackBytes,
        ulong SchedulerAuditReadbackBytes,
        ulong SchedulerValidationReadbackBytes)
    {
        public const int SampledAtlasCapacityQuantum = 256;
        public const ulong ParamsHeaderBytes = 240;
        public const ulong VolumeBytes = 96;
        public const ulong VolumePagingBytes = 32;
        public const ulong IrradianceBytesPerProbe =
            (ulong)(SimpleDdgiVolumeManager.IrradianceTexelsPerProbe *
                SimpleDdgiVolumeManager.IrradianceTexelsPerProbe) * 8UL;
        public const ulong VisibilityBytesPerProbe =
            (ulong)(SimpleDdgiVolumeManager.VisibilityTexelsPerProbe *
                SimpleDdgiVolumeManager.VisibilityTexelsPerProbe) * 8UL;
        public const ulong RayResultBytes = 32;
        public const uint TransportRayCacheAbiVersion = 4;
        public const ulong TransportRayCacheBytes = 36;
        public const ulong GraphSafePlaceholderBytes = 16;
        public const ulong ProbeStateBytesPerProbe = 32;
        public const ulong ReceiverProbeBytesPerProbe = 16;
        public const ulong ProbeUpdateBytes = 48;
        public const ulong RelocationClassificationBytesPerProbe = 48;
        // Classification is diagnostics-only. Read a bounded, rotating window
        // rather than triplicating the complete 48-byte production buffer for
        // every frame in flight. Probe state remains a complete readback because
        // it drives scheduling and convergence, while the window still samples
        // every classification record over time.
        public const int ClassificationReadbackProbeCapacity = 4_096;
        public const ulong ProbeReadbackBytesPerProbe =
            ProbeStateBytesPerProbe + RelocationClassificationBytesPerProbe;

        /// <summary>Compatibility alias for virtually indexed consumers.</summary>
        public int ProbeCount => VirtualProbeCount;

        /// <summary>Compatibility alias for sampled-atlas allocation code.</summary>
        public int SampledAtlasProbeCapacity =>
            SampledAtlasPhysicalProbeCapacity;

        public int NonResidentVirtualProbeCapacity => Math.Max(
            0,
            SparseVirtualProbeCount -
                SparsePhysicalPageCapacity *
                SimpleDdgiProbePageLayout.ProbesPerPage);

        /// <summary>
        /// Permanently invalid virtual slots introduced by ceil-dividing sparse
        /// grids into 2x2x2 pages. If an edge page is resident, these slots are
        /// charged to its fixed eight-probe physical payload.
        /// </summary>
        public int SparsePagePaddingProbeCount => Math.Max(
            0,
            checked(
                SparseVirtualPageCount *
                    SimpleDdgiProbePageLayout.ProbesPerPage -
                SparseVirtualProbeCount));

        /// <summary>Unused sampled-atlas layers caused by its 256-probe allocation quantum.</summary>
        public int SampledAtlasPaddingProbeCount => Math.Max(
            0,
            SampledAtlasPhysicalProbeCapacity - PhysicalProbeCapacity);

        public ulong SampledAtlasPaddingBytes => checked(
            (ulong)SampledAtlasPaddingProbeCount *
            (IrradianceBytesPerProbe + VisibilityBytesPerProbe));

        public ulong CanonicalAtlasBytes =>
            checked(IrradianceAtlasBytes + VisibilityAtlasBytes);

        /// <summary>
        /// Concrete live scheduler buffers. Validation readback is a semantic
        /// subset of the fixed feedback ring, not a second allocation.
        /// </summary>
        public ulong SchedulerBufferBytes => checked(
            SchedulerArenaBytes +
            SchedulerFeedbackReadbackBytes +
            SchedulerAuditReadbackBytes);

        public ulong PersistentBytes => checked(
            ParamsBytes +
            IrradianceAtlasBytes +
            VisibilityAtlasBytes +
            TransportIrradianceBytes +
            TransportSourceCacheBytes +
            ProbeStateBytes +
            ReceiverProbeBytes +
            RelocationClassificationBytes +
            ProbeStateReadbackBytes +
            SchedulerBufferBytes +
            SampledAtlasImageBytes +
            ResidencyArenaBytes +
            ResidencyFeedbackReadbackBytes);

        public ulong PhysicalPayloadBytes => checked(
            IrradianceAtlasBytes +
            VisibilityAtlasBytes +
            TransportIrradianceBytes +
            TransportSourceCacheBytes +
            SampledAtlasImageBytes);

        public ulong WorkBytes => checked(UpdateQueueBytes + RayScratchBytes);

        public ulong LiveBytes => checked(PersistentBytes + WorkBytes);

        public ulong ProbeStateReadbackBytesPerBuffer => ReadbackBufferCount > 0
            ? ProbeStateReadbackBytes / checked((ulong)ReadbackBufferCount)
            : 0UL;

        public static SimpleDdgiMemoryPlan Empty { get; } = new();

        public static SimpleDdgiMemoryPlan Create(
            int probeCount,
            int updateRequestCapacity,
            int rayCapacity,
            bool sampledAtlasRequested,
            bool concreteTransportBuffers,
            int readbackBufferCount,
            bool residentPrivateTargets = false,
            SimpleDdgiSchedulerMode schedulerMode =
                SimpleDdgiSchedulerMode.CpuReference,
            int schedulerActiveVolumeCount = 0,
            bool schedulerValidationEnabled = false,
            SimpleDdgiProbeResidencyMode residencyMode =
                SimpleDdgiProbeResidencyMode.Dense,
            int densePayloadProbeCount = -1,
            int sparseVirtualProbeCount = 0,
            int sparseVirtualPageCount = 0,
            int sparsePhysicalPageCapacity = 0,
            int maximumPageAdmissionsPerFrame = 0)
        {
            int probes = Math.Clamp(
                probeCount,
                0,
                GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
            int updates = Math.Clamp(updateRequestCapacity, 0, probes);
            int rays = Math.Clamp(
                rayCapacity,
                1,
                GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
            int readbacks = Math.Clamp(
                readbackBufferCount,
                0,
                RenderingConstants.FramesInFlight);
            SimpleDdgiProbeResidencyMode resolvedResidencyMode =
                residencyMode.Sanitize();
            int densePayloadProbes;
            int sparseVirtualProbes;
            int sparseVirtualPages;
            int sparsePhysicalPages;
            if (resolvedResidencyMode == SimpleDdgiProbeResidencyMode.Dense ||
                probes == 0)
            {
                resolvedResidencyMode = SimpleDdgiProbeResidencyMode.Dense;
                densePayloadProbes = probes;
                sparseVirtualProbes = 0;
                sparseVirtualPages = 0;
                sparsePhysicalPages = 0;
            }
            else
            {
                sparseVirtualProbes = Math.Clamp(
                    sparseVirtualProbeCount,
                    0,
                    probes);
                densePayloadProbes = densePayloadProbeCount < 0
                    ? probes - sparseVirtualProbes
                    : densePayloadProbeCount;
                if (densePayloadProbes < 0 ||
                    checked(densePayloadProbes + sparseVirtualProbes) != probes)
                {
                    throw new ArgumentException(
                        "Dense and sparse virtual probe populations must exactly partition the virtual field.");
                }
                sparseVirtualPages = sparseVirtualPageCount > 0
                    ? sparseVirtualPageCount
                    : SimpleDdgiProbePageLayout.CeilDivide(
                        sparseVirtualProbes,
                        SimpleDdgiProbePageLayout.ProbesPerPage);
                if (sparseVirtualProbes > 0 && sparseVirtualPages <= 0)
                    throw new ArgumentOutOfRangeException(nameof(sparseVirtualPageCount));
                sparsePhysicalPages = Math.Clamp(
                    sparsePhysicalPageCapacity,
                    0,
                    sparseVirtualPages);
                if (resolvedResidencyMode.UsesSparsePayloads() &&
                    sparseVirtualProbes > 0 &&
                    sparsePhysicalPages <= 0)
                {
                    throw new InvalidOperationException(
                        "Authoritative sparse Simple-DDGI requires a non-empty physical page pool.");
                }
            }

            int physicalProbeCapacity = resolvedResidencyMode.UsesSparsePayloads()
                ? checked(
                    densePayloadProbes +
                    sparsePhysicalPages *
                    SimpleDdgiProbePageLayout.ProbesPerPage)
                : probes;
            int sampledCapacity = sampledAtlasRequested
                ? ResolveSampledAtlasProbeCapacity(physicalProbeCapacity)
                : 0;

            ulong probeCount64 = checked((ulong)probes);
            ulong physicalProbeCount64 = checked((ulong)physicalProbeCapacity);
            ulong updateCount64 = checked((ulong)updates);
            ulong rayCount64 = checked((ulong)rays);
            ulong paramsBytes = checked(
                ParamsHeaderBytes +
                (ulong)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                (VolumeBytes + VolumePagingBytes));
            ulong irradianceBytes = AtLeastOneAllocation(
                checked(physicalProbeCount64 * IrradianceBytesPerProbe));
            ulong visibilityBytes = AtLeastOneAllocation(
                checked(physicalProbeCount64 * VisibilityBytesPerProbe));
            ulong transportIrradianceBytes = concreteTransportBuffers
                ? AtLeastOneAllocation(checked(
                    physicalProbeCount64 * IrradianceBytesPerProbe +
                    (residentPrivateTargets
                        ? physicalProbeCount64 * VisibilityBytesPerProbe
                        : 0UL)))
                : AtLeastOneAllocation(0UL);
            ulong transportSourceCacheBytes = concreteTransportBuffers
                ? AtLeastOneAllocation(checked(
                    physicalProbeCount64 * rayCount64 * TransportRayCacheBytes))
                : AtLeastOneAllocation(0UL);
            ulong stateBytes = AtLeastOneAllocation(
                checked(probeCount64 * ProbeStateBytesPerProbe));
            ulong receiverProbeBytes = AtLeastOneAllocation(
                checked(probeCount64 * ReceiverProbeBytesPerProbe));
            ulong queueBytes = AtLeastOneAllocation(
                checked(updateCount64 * ProbeUpdateBytes));
            ulong relocationBytes = AtLeastOneAllocation(
                checked(probeCount64 * RelocationClassificationBytesPerProbe));
            ulong readbackBytes = readbacks == 0
                ? 0UL
                : checked((ulong)readbacks *
                    ResolveProbeStateReadbackBufferBytes(probes));
            ulong rayScratchBytes = AtLeastOneAllocation(checked(
                updateCount64 * rayCount64 * RayResultBytes));
            ulong sampledImageBytes = checked(
                (ulong)sampledCapacity *
                (IrradianceBytesPerProbe + VisibilityBytesPerProbe));
            ulong residencyArenaBytes = 0UL;
            ulong residencyFeedbackReadbackBytes = 0UL;
            if (resolvedResidencyMode.CollectsDemand() && sparseVirtualPages > 0)
            {
                int admissions = Math.Clamp(
                    maximumPageAdmissionsPerFrame,
                    0,
                    sparsePhysicalPages);
                residencyArenaBytes = SimpleDdgiProbePageLayout.Create(
                    sparseVirtualPages,
                    sparsePhysicalPages,
                    admissions).TotalBytes;
                residencyFeedbackReadbackBytes = checked(
                    (ulong)RenderingConstants.FramesInFlight *
                    SimpleDdgiProbePageLayout.FeedbackSummaryBytes);
            }
            SimpleDdgiSchedulerMode resolvedSchedulerMode =
                schedulerMode.Sanitize();
            int schedulerActiveLaneCount = 0;
            ulong schedulerArenaBytes = 0UL;
            ulong schedulerFeedbackReadbackBytes = 0UL;
            ulong schedulerAuditReadbackBytes = 0UL;
            ulong schedulerValidationReadbackBytes = 0UL;
            if (resolvedSchedulerMode.IsGpuMode() && probes > 0)
            {
                int activeVolumes = Math.Clamp(
                    schedulerActiveVolumeCount,
                    1,
                    GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);
                SimpleDdgiGpuSchedulerLayout schedulerLayout =
                    SimpleDdgiGpuSchedulerLayout.Create(
                        probes,
                        updates,
                        activeVolumes,
                        SimpleDdgiGpuSchedulerLayout.MaxDirtyRegionCapacity,
                        schedulerValidationEnabled);
                schedulerActiveLaneCount = schedulerLayout.ActiveLaneCount;
                schedulerArenaBytes = schedulerLayout.TotalBytes;
                schedulerFeedbackReadbackBytes = checked(
                    (ulong)RenderingConstants.FramesInFlight *
                    SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes);
                schedulerAuditReadbackBytes = checked(
                    (ulong)RenderingConstants.FramesInFlight *
                    (ulong)Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>());
                schedulerValidationReadbackBytes = schedulerValidationEnabled
                    ? schedulerFeedbackReadbackBytes
                    : 0UL;
            }

            return new SimpleDdgiMemoryPlan(
                probes,
                densePayloadProbes,
                sparseVirtualProbes,
                sparseVirtualPages,
                sparsePhysicalPages,
                physicalProbeCapacity,
                sampledCapacity,
                resolvedResidencyMode,
                updates,
                rays,
                readbacks,
                paramsBytes,
                irradianceBytes,
                visibilityBytes,
                transportIrradianceBytes,
                transportSourceCacheBytes,
                stateBytes,
                receiverProbeBytes,
                queueBytes,
                relocationBytes,
                readbackBytes,
                rayScratchBytes,
                sampledImageBytes,
                residencyArenaBytes,
                residencyFeedbackReadbackBytes,
                resolvedSchedulerMode,
                schedulerActiveLaneCount,
                schedulerArenaBytes,
                schedulerFeedbackReadbackBytes,
                schedulerAuditReadbackBytes,
                schedulerValidationReadbackBytes);
        }

        /// <summary>
        /// Returns the one authoritative per-frame readback allocation. Probe
        /// state is complete because it drives scheduling; classification is a
        /// bounded rotating diagnostics window. Runtime allocation and capacity
        /// transition checks must use this same value to avoid release/recreate
        /// churn on every frame.
        /// </summary>
        public static ulong ResolveProbeStateReadbackBufferBytes(int probeCount)
        {
            int probes = Math.Clamp(
                probeCount,
                0,
                GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
            ulong classificationProbeCount = checked((ulong)Math.Min(
                probes,
                ClassificationReadbackProbeCapacity));
            return AtLeastOneAllocation(checked(
                (ulong)probes * ProbeStateBytesPerProbe +
                classificationProbeCount *
                RelocationClassificationBytesPerProbe));
        }

        public static int ResolveUpdateRequestCapacity(
            int probeCount,
            int configuredProbeUpdatesPerFrame,
            bool lightingDirtyBoostEnabled)
        {
            int probes = Math.Clamp(
                probeCount,
                0,
                GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
            int baseCapacity = configuredProbeUpdatesPerFrame <= 0
                ? probes
                : Math.Min(probes, configuredProbeUpdatesPerFrame);
            if (!lightingDirtyBoostEnabled || baseCapacity <= 0)
                return baseCapacity;

            return (int)Math.Min((long)probes, (long)baseCapacity * 2L);
        }

        public static int ResolveSampledAtlasProbeCapacity(int probeCount)
        {
            int probes = Math.Clamp(
                probeCount,
                0,
                GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
            if (probes == 0)
                return 0;

            return checked(
                ((probes + SampledAtlasCapacityQuantum - 1) /
                    SampledAtlasCapacityQuantum) *
                SampledAtlasCapacityQuantum);
        }

        private static ulong AtLeastOneAllocation(ulong bytes) =>
            Math.Max(GraphSafePlaceholderBytes, bytes);
    }

    public sealed record SimpleDdgiLayoutReport(
        SimpleDdgiLayoutBudget Budget,
        SimpleDdgiLayoutAdmissionMode AdmissionMode,
        int RequestedProbeCount,
        int AcceptedProbeCount,
        ulong RequestedPersistentBytes,
        ulong AcceptedPersistentBytes,
        IReadOnlyList<SimpleDdgiLayoutVolumeDecision> Volumes)
    {
        public SimpleDdgiMemoryPlan RequestedMemoryPlan { get; init; }
        public SimpleDdgiMemoryPlan AcceptedMemoryPlan { get; init; }
        public SimpleDdgiMemoryPlan DenseEquivalentMemoryPlan { get; init; }
        public bool PhysicalPageBudgetWasReduced { get; init; }
        public string PhysicalPageBudgetDecision { get; init; } = string.Empty;
        public string ResidencyFallbackReason { get; init; } = string.Empty;

        public int VirtualProbeCount => AcceptedMemoryPlan.VirtualProbeCount;
        public int DensePayloadProbeCount => AcceptedMemoryPlan.DensePayloadProbeCount;
        public int SparseVirtualProbeCount => AcceptedMemoryPlan.SparseVirtualProbeCount;
        public int SparseVirtualPageCount => AcceptedMemoryPlan.SparseVirtualPageCount;
        public int SparsePhysicalPageCapacity => AcceptedMemoryPlan.SparsePhysicalPageCapacity;
        public int PhysicalProbeCapacity => AcceptedMemoryPlan.PhysicalProbeCapacity;
        public int SampledAtlasPhysicalProbeCapacity =>
            AcceptedMemoryPlan.SampledAtlasPhysicalProbeCapacity;
        public int SparsePagePaddingProbeCount =>
            AcceptedMemoryPlan.SparsePagePaddingProbeCount;
        public int SampledAtlasPaddingProbeCount =>
            AcceptedMemoryPlan.SampledAtlasPaddingProbeCount;
        public ulong SampledAtlasPaddingBytes =>
            AcceptedMemoryPlan.SampledAtlasPaddingBytes;
        public ulong DenseEquivalentBytes => DenseEquivalentMemoryPlan.LiveBytes;
        public ulong AllocatedSparseBytes => AcceptedMemoryPlan.LiveBytes;
        public ulong AvoidedBytes => DenseEquivalentBytes > AllocatedSparseBytes
            ? DenseEquivalentBytes - AllocatedSparseBytes
            : 0UL;

        public bool IsWithinBudget => RequestedProbeCount <= Budget.ProbeBudget &&
            RequestedPersistentBytes <= Budget.PersistentMemoryBudgetBytes &&
            Volumes.Count <= Budget.VolumeBudget;

        public bool WasDegraded => PhysicalPageBudgetWasReduced ||
            Volumes.Any(static volume => volume.Decision != SimpleDdgiLayoutDecision.Accepted);

        public IReadOnlySet<int> AcceptedSourceOrdinals => Volumes
            .Where(static volume => volume.Decision == SimpleDdgiLayoutDecision.Accepted)
            .Select(static volume => volume.Request.SourceOrdinal)
            .ToHashSet();

        public string Summary =>
            $"requested={RequestedProbeCount} accepted={AcceptedProbeCount} probeBudget={Budget.ProbeBudget} " +
            $"persistent={AcceptedPersistentBytes}/{Budget.PersistentMemoryBudgetBytes} " +
            $"physical={PhysicalProbeCapacity}/{VirtualProbeCount} " +
            $"pages={SparsePhysicalPageCapacity}/{SparseVirtualPageCount} " +
            $"pagePadding={SparsePagePaddingProbeCount} " +
            $"sampledPadding={SampledAtlasPaddingProbeCount}/{SampledAtlasPaddingBytes} " +
            $"avoided={AvoidedBytes} " +
            $"rejected={Volumes.Count(static volume => volume.Decision != SimpleDdgiLayoutDecision.Accepted)}";
    }

    /// <summary>
    /// Pure, allocation-free layout admission.  Requests must already be in
    /// deterministic receiver-priority order.  The compiler never mutates the
    /// request list and therefore produces the same report for load/reload.
    /// </summary>
    public static class SimpleDdgiLayoutCompiler
    {
        // Compatibility aliases retained for capture readers and existing tools.
        public const ulong CanonicalAtlasBytesPerProbe =
            SimpleDdgiMemoryPlan.IrradianceBytesPerProbe +
            SimpleDdgiMemoryPlan.VisibilityBytesPerProbe;
        public const ulong StateBytesPerProbe =
            SimpleDdgiMemoryPlan.ProbeStateBytesPerProbe;
        public const ulong UpdateQueueBytesPerProbe =
            SimpleDdgiMemoryPlan.ProbeUpdateBytes;
        public const ulong RelocationClassificationBytesPerProbe =
            SimpleDdgiMemoryPlan.RelocationClassificationBytesPerProbe;
        public const ulong TransportIrradianceBytesPerProbe =
            SimpleDdgiMemoryPlan.IrradianceBytesPerProbe;
        public const ulong TransportSourceRayCacheBytes =
            SimpleDdgiMemoryPlan.TransportRayCacheBytes;

        public static ulong EstimatePersistentBytes(
            int probeCount,
            bool sampledAtlasRequested,
            bool transportV2Enabled = false,
            int transportRayCapacity = 0,
            int configuredProbeUpdatesPerFrame = 0,
            bool lightingDirtyBoostEnabled = false,
            int readbackBufferCount = 0,
            bool residentPrivateTargets = false,
            SimpleDdgiSchedulerMode schedulerMode =
                SimpleDdgiSchedulerMode.CpuReference,
            int schedulerActiveVolumeCount = 0,
            bool schedulerValidationEnabled = false,
            SimpleDdgiProbeResidencyMode residencyMode =
                SimpleDdgiProbeResidencyMode.Dense,
            int densePayloadProbeCount = -1,
            int sparseVirtualProbeCount = 0,
            int sparseVirtualPageCount = 0,
            int sparsePhysicalPageCapacity = 0,
            int maximumPageAdmissionsPerFrame = 0)
        {
            int updates = SimpleDdgiMemoryPlan.ResolveUpdateRequestCapacity(
                probeCount,
                configuredProbeUpdatesPerFrame,
                lightingDirtyBoostEnabled);
            return SimpleDdgiMemoryPlan.Create(
                probeCount,
                updates,
                transportRayCapacity,
                sampledAtlasRequested,
                transportV2Enabled,
                readbackBufferCount,
                residentPrivateTargets,
                schedulerMode,
                schedulerActiveVolumeCount,
                schedulerValidationEnabled,
                residencyMode,
                densePayloadProbeCount,
                sparseVirtualProbeCount,
                sparseVirtualPageCount,
                sparsePhysicalPageCapacity,
                maximumPageAdmissionsPerFrame).LiveBytes;
        }

        public static SimpleDdgiLayoutReport Compile(
            IReadOnlyList<SimpleDdgiLayoutVolumeRequest> requests,
            SimpleDdgiLayoutBudget budget,
            bool sampledAtlasRequested,
            SimpleDdgiLayoutAdmissionMode admissionMode,
            bool transportV2Enabled = false,
            int transportRayCapacity = 0,
            int configuredProbeUpdatesPerFrame = 0,
            bool lightingDirtyBoostEnabled = false,
            int readbackBufferCount = 0,
            bool residentPrivateTargets = false,
            SimpleDdgiSchedulerMode schedulerMode =
                SimpleDdgiSchedulerMode.CpuReference,
            bool schedulerValidationEnabled = false,
            SimpleDdgiProbeResidencyMode residencyMode =
                SimpleDdgiProbeResidencyMode.Dense,
            int sparsePhysicalPageBudget = 0,
            int sparseMinimumPhysicalPageBudget = 0,
            int maximumPageAdmissionsPerFrame = 0)
        {
            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            SimpleDdgiProbeResidencyMode requestedResidencyMode =
                residencyMode.Sanitize();
            SimpleDdgiProbeResidencyMode resolvedResidencyMode =
                requestedResidencyMode;
            string residencyFallbackReason = string.Empty;
            if (requestedResidencyMode.UsesSparsePayloads() &&
                schedulerMode.Sanitize() != SimpleDdgiSchedulerMode.GpuResident)
            {
                resolvedResidencyMode = SimpleDdgiProbeResidencyMode.Dense;
                residencyFallbackReason = "gpu-resident-scheduler-required";
            }
            else if (requestedResidencyMode.UsesSparsePayloads() &&
                !transportV2Enabled)
            {
                resolvedResidencyMode = SimpleDdgiProbeResidencyMode.Dense;
                residencyFallbackReason = "transport-v2-required";
            }

            var decisions = new List<SimpleDdgiLayoutVolumeDecision>(requests.Count);
            var acceptedFlags = new bool[requests.Count];
            int requestedProbes = 0;
            int acceptedProbes = 0;
            int acceptedVolumes = 0;
            bool physicalPageBudgetWasReduced = false;
            string physicalPageBudgetDecision = string.Empty;
            SimpleDdgiMemoryPlan requestedPlan = CreateMemoryPlan(
                0,
                0,
                requestedPrefixCount: 0,
                candidateIndex: -1,
                requestedSelection: true);
            SimpleDdgiMemoryPlan acceptedPlan = requestedPlan;
            ulong previousRequestedLiveBytes = 0UL;

            for (int index = 0; index < requests.Count; index++)
            {
                SimpleDdgiLayoutVolumeRequest request = requests[index] ??
                    throw new ArgumentException("A simple-DDGI layout request cannot be null.", nameof(requests));
                int probes = Math.Max(request.ProbeCount, 0);
                requestedProbes = checked(requestedProbes + probes);
                SimpleDdgiMemoryPlan nextRequestedPlan =
                    CreateMemoryPlan(
                        requestedProbes,
                        index + 1,
                        requestedPrefixCount: index + 1,
                        candidateIndex: -1,
                        requestedSelection: true);
                ulong requestedIncrementalBytes = nextRequestedPlan.LiveBytes >=
                    previousRequestedLiveBytes
                        ? nextRequestedPlan.LiveBytes - previousRequestedLiveBytes
                        : 0UL;
                requestedPlan = nextRequestedPlan;
                previousRequestedLiveBytes = requestedPlan.LiveBytes;

                int nextAcceptedProbeCount = checked(acceptedProbes + probes);
                SimpleDdgiMemoryPlan nextAcceptedPlan =
                    CreateMemoryPlan(
                        nextAcceptedProbeCount,
                        acceptedVolumes + 1,
                        requestedPrefixCount: 0,
                        candidateIndex: index,
                        requestedSelection: false);

                bool reducedForCandidate = false;
                int configuredCandidateCapacity = nextAcceptedPlan.SparsePhysicalPageCapacity;
                if (nextAcceptedPlan.LiveBytes > budget.PersistentMemoryBudgetBytes &&
                    admissionMode == SimpleDdgiLayoutAdmissionMode.Degrade &&
                    resolvedResidencyMode.UsesSparsePayloads() &&
                    nextAcceptedPlan.SparseVirtualPageCount > 0)
                {
                    int maximumCapacity = Math.Min(
                        Math.Max(1, sparsePhysicalPageBudget),
                        nextAcceptedPlan.SparseVirtualPageCount);
                    int minimumCapacity = Math.Min(
                        maximumCapacity,
                        Math.Max(1, sparseMinimumPhysicalPageBudget));
                    SimpleDdgiMemoryPlan minimumPlan = CreateMemoryPlan(
                        nextAcceptedProbeCount,
                        acceptedVolumes + 1,
                        0,
                        index,
                        false,
                        minimumCapacity);
                    if (minimumPlan.LiveBytes <= budget.PersistentMemoryBudgetBytes)
                    {
                        int low = minimumCapacity;
                        int high = maximumCapacity;
                        SimpleDdgiMemoryPlan best = minimumPlan;
                        while (low <= high)
                        {
                            int middle = low + (high - low) / 2;
                            SimpleDdgiMemoryPlan candidatePlan = CreateMemoryPlan(
                                nextAcceptedProbeCount,
                                acceptedVolumes + 1,
                                0,
                                index,
                                false,
                                middle);
                            if (candidatePlan.LiveBytes <= budget.PersistentMemoryBudgetBytes)
                            {
                                best = candidatePlan;
                                low = middle + 1;
                            }
                            else
                            {
                                high = middle - 1;
                            }
                        }
                        nextAcceptedPlan = best;
                        reducedForCandidate =
                            best.SparsePhysicalPageCapacity < configuredCandidateCapacity;
                    }
                }

                ulong incrementalBytes = acceptedVolumes == 0
                    ? nextAcceptedPlan.LiveBytes
                    : nextAcceptedPlan.LiveBytes >= acceptedPlan.LiveBytes
                        ? nextAcceptedPlan.LiveBytes - acceptedPlan.LiveBytes
                        : 0UL;

                bool exceedsVolumeBudget = acceptedVolumes >= budget.VolumeBudget;
                bool exceedsProbeBudget =
                    probes > budget.ProbeBudget ||
                    acceptedProbes > budget.ProbeBudget - probes;
                bool exceedsMemoryBudget =
                    nextAcceptedPlan.LiveBytes > budget.PersistentMemoryBudgetBytes;
                if (!exceedsVolumeBudget && !exceedsProbeBudget && !exceedsMemoryBudget)
                {
                    decisions.Add(new SimpleDdgiLayoutVolumeDecision(
                        request,
                        SimpleDdgiLayoutDecision.Accepted,
                        probes,
                        incrementalBytes,
                        "accepted")
                    {
                        RequestedPersistentBytes = requestedIncrementalBytes
                    });
                    acceptedVolumes++;
                    acceptedProbes = nextAcceptedProbeCount;
                    acceptedPlan = nextAcceptedPlan;
                    acceptedFlags[index] = true;
                    if (reducedForCandidate)
                    {
                        physicalPageBudgetWasReduced = true;
                        physicalPageBudgetDecision =
                            $"degraded-page-capacity:{configuredCandidateCapacity}->{acceptedPlan.SparsePhysicalPageCapacity}";
                    }
                    continue;
                }

                SimpleDdgiLayoutDecision decision = exceedsVolumeBudget
                    ? SimpleDdgiLayoutDecision.RejectedVolumeLimit
                    : SimpleDdgiLayoutDecision.RejectedBudget;
                string reason = exceedsVolumeBudget
                    ? "volume-budget"
                    : exceedsProbeBudget && exceedsMemoryBudget
                        ? "probe-and-memory-budget"
                        : exceedsProbeBudget
                            ? "probe-budget"
                            : "persistent-memory-budget";
                decisions.Add(new SimpleDdgiLayoutVolumeDecision(
                    request,
                    decision,
                    0,
                    0,
                    reason)
                {
                    RequestedPersistentBytes = requestedIncrementalBytes
                });
            }

            if (resolvedResidencyMode.UsesSparsePayloads())
            {
                bool acceptedSparseNear = false;
                bool acceptedDenseCoarser = false;
                for (int index = 0; index < requests.Count; index++)
                {
                    if (!acceptedFlags[index])
                        continue;
                    SimpleDdgiLayoutVolumeRequest request = requests[index];
                    acceptedSparseNear |= request.SparseNearRingEligible;
                    acceptedDenseCoarser |= request.DenseCoarserRingEligible;
                }

                string fallbackReason = !acceptedSparseNear
                    ? "sparse-near-ring-not-admitted"
                    : !acceptedDenseCoarser
                        ? "dense-coarser-ring-not-admitted"
                        : string.Empty;
                if (!string.IsNullOrEmpty(fallbackReason))
                {
                    // Admission is evaluated again because dense payload bytes
                    // differ from sparse payload bytes. Returning the sparse
                    // decisions with a dense mode would make the allocation
                    // report internally inconsistent and could overrun the
                    // accepted memory budget.
                    SimpleDdgiLayoutReport denseFallback = Compile(
                        requests,
                        budget,
                        sampledAtlasRequested,
                        admissionMode,
                        transportV2Enabled,
                        transportRayCapacity,
                        configuredProbeUpdatesPerFrame,
                        lightingDirtyBoostEnabled,
                        readbackBufferCount,
                        residentPrivateTargets,
                        schedulerMode,
                        schedulerValidationEnabled,
                        SimpleDdgiProbeResidencyMode.Dense,
                        sparsePhysicalPageBudget,
                        sparseMinimumPhysicalPageBudget,
                        maximumPageAdmissionsPerFrame);
                    return denseFallback with
                    {
                        ResidencyFallbackReason = fallbackReason
                    };
                }
            }

            int denseUpdateCapacity =
                SimpleDdgiMemoryPlan.ResolveUpdateRequestCapacity(
                    acceptedProbes,
                    configuredProbeUpdatesPerFrame,
                    lightingDirtyBoostEnabled);
            SimpleDdgiMemoryPlan denseEquivalentPlan = SimpleDdgiMemoryPlan.Create(
                acceptedProbes,
                denseUpdateCapacity,
                transportRayCapacity,
                sampledAtlasRequested,
                transportV2Enabled,
                readbackBufferCount,
                residentPrivateTargets,
                schedulerMode,
                acceptedVolumes,
                schedulerValidationEnabled,
                SimpleDdgiProbeResidencyMode.Dense);

            return new SimpleDdgiLayoutReport(
                budget,
                admissionMode,
                requestedProbes,
                acceptedProbes,
                requestedPlan.LiveBytes,
                acceptedPlan.LiveBytes,
                decisions)
            {
                RequestedMemoryPlan = requestedPlan,
                AcceptedMemoryPlan = acceptedPlan,
                DenseEquivalentMemoryPlan = denseEquivalentPlan,
                PhysicalPageBudgetWasReduced = physicalPageBudgetWasReduced,
                PhysicalPageBudgetDecision = physicalPageBudgetDecision,
                ResidencyFallbackReason = residencyFallbackReason
            };

            SimpleDdgiMemoryPlan CreateMemoryPlan(
                int totalProbeCount,
                int activeVolumeCount,
                int requestedPrefixCount,
                int candidateIndex,
                bool requestedSelection,
                int? physicalPageCapacityOverride = null)
            {
                int sparseVirtualProbes = 0;
                int sparseVirtualPages = 0;
                for (int requestIndex = 0;
                     requestIndex < requests.Count;
                     requestIndex++)
                {
                    bool include = requestedSelection
                        ? requestIndex < requestedPrefixCount
                        : acceptedFlags[requestIndex] || requestIndex == candidateIndex;
                    if (!include)
                        continue;
                    SimpleDdgiLayoutVolumeRequest request = requests[requestIndex];
                    if (!request.SparseNearRingEligible ||
                        resolvedResidencyMode == SimpleDdgiProbeResidencyMode.Dense)
                    {
                        continue;
                    }
                    sparseVirtualProbes = checked(
                        sparseVirtualProbes + Math.Max(0, request.ProbeCount));
                    sparseVirtualPages = checked(
                        sparseVirtualPages + request.VirtualPageCount);
                }
                int densePayloadProbes = checked(
                    totalProbeCount - sparseVirtualProbes);
                int configuredPhysicalPages = physicalPageCapacityOverride ??
                    Math.Min(
                        Math.Max(0, sparsePhysicalPageBudget),
                        sparseVirtualPages);
                int updateCapacity =
                    SimpleDdgiMemoryPlan.ResolveUpdateRequestCapacity(
                        totalProbeCount,
                        configuredProbeUpdatesPerFrame,
                        lightingDirtyBoostEnabled);
                return SimpleDdgiMemoryPlan.Create(
                    totalProbeCount,
                    updateCapacity,
                    transportRayCapacity,
                    sampledAtlasRequested,
                    transportV2Enabled,
                    readbackBufferCount,
                    residentPrivateTargets,
                    schedulerMode,
                    activeVolumeCount,
                    schedulerValidationEnabled,
                    resolvedResidencyMode,
                    densePayloadProbes,
                    sparseVirtualProbes,
                    sparseVirtualPages,
                    configuredPhysicalPages,
                    maximumPageAdmissionsPerFrame);
            }
        }
    }
}
