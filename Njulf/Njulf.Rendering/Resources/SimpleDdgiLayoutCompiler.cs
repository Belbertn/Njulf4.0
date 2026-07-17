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
        string Reason);

    /// <summary>
    /// Hard per-tier layout budget.  Persistent bytes include the canonical
    /// atlas, optional sampled mirror, V2 private transport target and source
    /// cache, state, queue, and classification storage.  Transient ray scratch
    /// remains separately bounded by the scheduled update quota.
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

    public sealed record SimpleDdgiLayoutReport(
        SimpleDdgiLayoutBudget Budget,
        SimpleDdgiLayoutAdmissionMode AdmissionMode,
        int RequestedProbeCount,
        int AcceptedProbeCount,
        ulong RequestedPersistentBytes,
        ulong AcceptedPersistentBytes,
        IReadOnlyList<SimpleDdgiLayoutVolumeDecision> Volumes)
    {
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
        // RGBA16F atlas texels: 8x8 irradiance + 16x16 visibility. State,
        // update queue, and relocation/classification are 32, 32, and 48 bytes.
        // The sampled mirror reserves a second atlas so an admitted layout can
        // enable it without violating its tier cache cap.
        public const ulong CanonicalAtlasBytesPerProbe =
            (ulong)(SimpleDdgiVolumeManager.IrradianceTexelsPerProbe * SimpleDdgiVolumeManager.IrradianceTexelsPerProbe +
                SimpleDdgiVolumeManager.VisibilityTexelsPerProbe * SimpleDdgiVolumeManager.VisibilityTexelsPerProbe) * 8UL;
        public const ulong StateBytesPerProbe = 32UL;
        public const ulong UpdateQueueBytesPerProbe = 32UL;
        public const ulong RelocationClassificationBytesPerProbe = 48UL;
        public const ulong TransportIrradianceBytesPerProbe =
            (ulong)(SimpleDdgiVolumeManager.IrradianceTexelsPerProbe *
                SimpleDdgiVolumeManager.IrradianceTexelsPerProbe) * 8UL;
        public const ulong TransportSourceRayCacheBytes = 32UL;

        public static ulong EstimatePersistentBytes(
            int probeCount,
            bool sampledAtlasRequested,
            bool transportV2Enabled = false,
            int transportRayCapacity = 0)
        {
            ulong probes = checked((ulong)Math.Max(probeCount, 0));
            ulong atlasBytes = checked(probes * CanonicalAtlasBytesPerProbe);
            if (sampledAtlasRequested)
                atlasBytes = checked(atlasBytes * 2UL);
            ulong transportBytes = 0UL;
            if (transportV2Enabled)
            {
                ulong rays = checked((ulong)Math.Clamp(
                    transportRayCapacity,
                    1,
                    GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe));
                transportBytes = checked(probes * (TransportIrradianceBytesPerProbe +
                    rays * TransportSourceRayCacheBytes));
            }
            return checked(atlasBytes + probes *
                (StateBytesPerProbe + UpdateQueueBytesPerProbe + RelocationClassificationBytesPerProbe) +
                transportBytes);
        }

        public static SimpleDdgiLayoutReport Compile(
            IReadOnlyList<SimpleDdgiLayoutVolumeRequest> requests,
            SimpleDdgiLayoutBudget budget,
            bool sampledAtlasRequested,
            SimpleDdgiLayoutAdmissionMode admissionMode,
            bool transportV2Enabled = false,
            int transportRayCapacity = 0)
        {
            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            var decisions = new List<SimpleDdgiLayoutVolumeDecision>(requests.Count);
            int requestedProbes = 0;
            ulong requestedBytes = 0;
            int acceptedProbes = 0;
            ulong acceptedBytes = 0;
            int acceptedVolumes = 0;

            for (int index = 0; index < requests.Count; index++)
            {
                SimpleDdgiLayoutVolumeRequest request = requests[index] ??
                    throw new ArgumentException("A simple-DDGI layout request cannot be null.", nameof(requests));
                int probes = Math.Max(request.ProbeCount, 0);
                ulong persistentBytes = EstimatePersistentBytes(
                    probes,
                    sampledAtlasRequested,
                    transportV2Enabled,
                    transportRayCapacity);
                requestedProbes = checked(requestedProbes + probes);
                requestedBytes = checked(requestedBytes + persistentBytes);

                bool exceedsVolumeBudget = acceptedVolumes >= budget.VolumeBudget;
                bool exceedsProbeBudget = acceptedProbes > budget.ProbeBudget - probes;
                bool exceedsMemoryBudget = persistentBytes > budget.PersistentMemoryBudgetBytes ||
                    acceptedBytes > budget.PersistentMemoryBudgetBytes - persistentBytes;
                if (!exceedsVolumeBudget && !exceedsProbeBudget && !exceedsMemoryBudget)
                {
                    decisions.Add(new SimpleDdgiLayoutVolumeDecision(
                        request,
                        SimpleDdgiLayoutDecision.Accepted,
                        probes,
                        persistentBytes,
                        "accepted"));
                    acceptedVolumes++;
                    acceptedProbes = checked(acceptedProbes + probes);
                    acceptedBytes = checked(acceptedBytes + persistentBytes);
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
                decisions.Add(new SimpleDdgiLayoutVolumeDecision(request, decision, 0, 0, reason));
            }

            return new SimpleDdgiLayoutReport(
                budget,
                admissionMode,
                requestedProbes,
                acceptedProbes,
                requestedBytes,
                acceptedBytes,
                decisions);
        }
    }
}
