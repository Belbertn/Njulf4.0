using System;
using System.Collections.Generic;
using System.Linq;
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
        int ProbeCount);

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
        int ProbeCount,
        int UpdateRequestCapacity,
        int RayCapacity,
        int ReadbackBufferCount,
        int SampledAtlasProbeCapacity,
        ulong ParamsBytes,
        ulong IrradianceAtlasBytes,
        ulong VisibilityAtlasBytes,
        ulong TransportIrradianceBytes,
        ulong TransportSourceCacheBytes,
        ulong ProbeStateBytes,
        ulong UpdateQueueBytes,
        ulong RelocationClassificationBytes,
        ulong ProbeStateReadbackBytes,
        ulong RayScratchBytes,
        ulong SampledAtlasImageBytes)
    {
        public const int SampledAtlasCapacityQuantum = 256;
        public const ulong ParamsHeaderBytes = 208;
        public const ulong VolumeBytes = 96;
        public const ulong IrradianceBytesPerProbe =
            (ulong)(SimpleDdgiVolumeManager.IrradianceTexelsPerProbe *
                SimpleDdgiVolumeManager.IrradianceTexelsPerProbe) * 8UL;
        public const ulong VisibilityBytesPerProbe =
            (ulong)(SimpleDdgiVolumeManager.VisibilityTexelsPerProbe *
                SimpleDdgiVolumeManager.VisibilityTexelsPerProbe) * 8UL;
        public const ulong RayResultBytes = 32;
        public const uint TransportRayCacheAbiVersion = 2;
        public const ulong TransportRayCacheBytes = 36;
        public const ulong ProbeStateBytesPerProbe = 32;
        public const ulong ProbeUpdateBytes = 32;
        public const ulong RelocationClassificationBytesPerProbe = 48;
        // Classification is diagnostics-only. Read a bounded, rotating window
        // rather than triplicating the complete 48-byte production buffer for
        // every frame in flight. Probe state remains a complete readback because
        // it drives scheduling and convergence, while the window still samples
        // every classification record over time.
        public const int ClassificationReadbackProbeCapacity = 4_096;
        public const ulong ProbeReadbackBytesPerProbe =
            ProbeStateBytesPerProbe + RelocationClassificationBytesPerProbe;

        public ulong CanonicalAtlasBytes =>
            checked(IrradianceAtlasBytes + VisibilityAtlasBytes);

        public ulong PersistentBytes => checked(
            ParamsBytes +
            IrradianceAtlasBytes +
            VisibilityAtlasBytes +
            TransportIrradianceBytes +
            TransportSourceCacheBytes +
            ProbeStateBytes +
            RelocationClassificationBytes +
            ProbeStateReadbackBytes +
            SampledAtlasImageBytes);

        public ulong WorkBytes => checked(UpdateQueueBytes + RayScratchBytes);

        public ulong LiveBytes => checked(PersistentBytes + WorkBytes);

        public static SimpleDdgiMemoryPlan Empty { get; } = new();

        public static SimpleDdgiMemoryPlan Create(
            int probeCount,
            int updateRequestCapacity,
            int rayCapacity,
            bool sampledAtlasRequested,
            bool concreteTransportBuffers,
            int readbackBufferCount)
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
            int sampledCapacity = sampledAtlasRequested
                ? ResolveSampledAtlasProbeCapacity(probes)
                : 0;

            ulong probeCount64 = checked((ulong)probes);
            ulong updateCount64 = checked((ulong)updates);
            ulong rayCount64 = checked((ulong)rays);
            ulong paramsBytes = checked(
                ParamsHeaderBytes +
                (ulong)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                VolumeBytes);
            ulong irradianceBytes = AtLeastOneAllocation(
                checked(probeCount64 * IrradianceBytesPerProbe));
            ulong visibilityBytes = AtLeastOneAllocation(
                checked(probeCount64 * VisibilityBytesPerProbe));
            ulong transportIrradianceBytes = concreteTransportBuffers
                ? AtLeastOneAllocation(checked(probeCount64 * IrradianceBytesPerProbe))
                : 0UL;
            ulong transportSourceCacheBytes = concreteTransportBuffers
                ? AtLeastOneAllocation(checked(
                    probeCount64 * rayCount64 * TransportRayCacheBytes))
                : 0UL;
            ulong stateBytes = AtLeastOneAllocation(
                checked(probeCount64 * ProbeStateBytesPerProbe));
            ulong queueBytes = AtLeastOneAllocation(
                checked(updateCount64 * ProbeUpdateBytes));
            ulong relocationBytes = AtLeastOneAllocation(
                checked(probeCount64 * RelocationClassificationBytesPerProbe));
            ulong classificationReadbackProbeCount = checked((ulong)Math.Min(
                probes,
                ClassificationReadbackProbeCapacity));
            ulong readbackBytes = readbacks == 0
                ? 0UL
                : checked(
                    (ulong)readbacks *
                    AtLeastOneAllocation(checked(
                        probeCount64 * ProbeStateBytesPerProbe +
                        classificationReadbackProbeCount *
                        RelocationClassificationBytesPerProbe)));
            ulong rayScratchBytes = AtLeastOneAllocation(checked(
                updateCount64 * rayCount64 * RayResultBytes));
            ulong sampledImageBytes = checked(
                (ulong)sampledCapacity *
                (IrradianceBytesPerProbe + VisibilityBytesPerProbe));

            return new SimpleDdgiMemoryPlan(
                probes,
                updates,
                rays,
                readbacks,
                sampledCapacity,
                paramsBytes,
                irradianceBytes,
                visibilityBytes,
                transportIrradianceBytes,
                transportSourceCacheBytes,
                stateBytes,
                queueBytes,
                relocationBytes,
                readbackBytes,
                rayScratchBytes,
                sampledImageBytes);
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
            Math.Max(16UL, bytes);
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

        public bool IsWithinBudget => RequestedProbeCount <= Budget.ProbeBudget &&
            RequestedPersistentBytes <= Budget.PersistentMemoryBudgetBytes &&
            Volumes.Count <= Budget.VolumeBudget;

        public bool WasDegraded => Volumes.Any(static volume => volume.Decision != SimpleDdgiLayoutDecision.Accepted);

        public IReadOnlySet<int> AcceptedSourceOrdinals => Volumes
            .Where(static volume => volume.Decision == SimpleDdgiLayoutDecision.Accepted)
            .Select(static volume => volume.Request.SourceOrdinal)
            .ToHashSet();

        public string Summary =>
            $"requested={RequestedProbeCount} accepted={AcceptedProbeCount} probeBudget={Budget.ProbeBudget} " +
            $"persistent={AcceptedPersistentBytes}/{Budget.PersistentMemoryBudgetBytes} " +
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
            int readbackBufferCount = 0)
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
                readbackBufferCount).LiveBytes;
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
            int readbackBufferCount = 0)
        {
            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            var decisions = new List<SimpleDdgiLayoutVolumeDecision>(requests.Count);
            int requestedProbes = 0;
            int acceptedProbes = 0;
            int acceptedVolumes = 0;
            SimpleDdgiMemoryPlan requestedPlan = CreateMemoryPlan(0);
            SimpleDdgiMemoryPlan acceptedPlan = CreateMemoryPlan(0);
            ulong previousRequestedLiveBytes = 0UL;

            for (int index = 0; index < requests.Count; index++)
            {
                SimpleDdgiLayoutVolumeRequest request = requests[index] ??
                    throw new ArgumentException("A simple-DDGI layout request cannot be null.", nameof(requests));
                int probes = Math.Max(request.ProbeCount, 0);
                requestedProbes = checked(requestedProbes + probes);
                SimpleDdgiMemoryPlan nextRequestedPlan =
                    CreateMemoryPlan(requestedProbes);
                ulong requestedIncrementalBytes = checked(
                    nextRequestedPlan.LiveBytes - previousRequestedLiveBytes);
                requestedPlan = nextRequestedPlan;
                previousRequestedLiveBytes = requestedPlan.LiveBytes;

                int nextAcceptedProbeCount = checked(acceptedProbes + probes);
                SimpleDdgiMemoryPlan nextAcceptedPlan =
                    CreateMemoryPlan(nextAcceptedProbeCount);
                ulong incrementalBytes = acceptedVolumes == 0
                    ? nextAcceptedPlan.LiveBytes
                    : checked(nextAcceptedPlan.LiveBytes - acceptedPlan.LiveBytes);

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
                AcceptedMemoryPlan = acceptedPlan
            };

            SimpleDdgiMemoryPlan CreateMemoryPlan(int totalProbeCount)
            {
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
                    readbackBufferCount);
            }
        }
    }
}
