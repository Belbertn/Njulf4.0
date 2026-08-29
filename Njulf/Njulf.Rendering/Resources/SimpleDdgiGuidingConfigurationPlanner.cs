using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Compiles the renderer-lifetime C3 prefix from immutable DDGI capacity,
/// admission, and device-limit facts. Vulkan publication remains owned by
/// <see cref="SimpleDdgiGuidingFrameCoordinator"/>.
/// </summary>
internal static class SimpleDdgiGuidingConfigurationPlanner
{
    internal const ulong ExperimentBudgetBytes = 16UL * 1024UL * 1024UL;

    public static SimpleDdgiGuidingFrameConfiguration Compile(
        in SimpleDdgiGuidingConfigurationRequest request,
        out string reason)
    {
        GlobalIlluminationSettings gi = request.Settings ??
                                        throw new ArgumentNullException(nameof(request));
        if (!request.SimpleDdgiActive ||
            !request.GraphUsesDirectionalGuiding ||
            gi.SimpleDdgiDirectionalGuidingMode is not
                (SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment or
                SimpleDdgiDirectionalGuidingMode.AutoQualified))
        {
            reason = "directional-guiding-disabled";
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        if (AdvancedGiActivationPolicy.RequiresQualification(
                gi.SimpleDdgiDirectionalGuidingMode) &&
            !request.RuntimeContentState.Matched)
        {
            reason = request.RuntimeContentState.Reason;
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        bool prerequisitesSatisfied =
            AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                gi.SimpleDdgiDirectionalGuidingMode,
                request.PrerequisiteGate);
        if (!prerequisitesSatisfied)
        {
            reason = string.IsNullOrWhiteSpace(
                request.PrerequisiteGate.FailureDetail)
                ? "guiding-global-prerequisite-gate-rejected"
                : request.PrerequisiteGate.FailureDetail;
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        int totalPhysical = request.TotalPhysicalProbeCapacity;
        int directionSlots = request.DirectionSlotsPerProbe;
        if (totalPhysical <= 0 || directionSlots is <= 0 or >
                SimpleDdgiGuidingSourceCacheLayoutCompiler
                    .MaximumDirectionSlotsPerProbe)
        {
            reason = "guiding-ddgi-physical-or-ray-capacity-unavailable";
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        int configuredUpdates = gi.SimpleDdgiProbeUpdatesPerFrame <= 0
            ? totalPhysical
            : Math.Min(totalPhysical, gi.SimpleDdgiProbeUpdatesPerFrame);
        int rayBudgetUpdates = gi.DdgiProbeUpdatePrimaryRayBudget <= 0
            ? 0
            : gi.DdgiProbeUpdatePrimaryRayBudget / directionSlots;
        int requestedScheduled = Math.Min(
            configuredUpdates,
            rayBudgetUpdates);
        if (requestedScheduled <= 0)
        {
            reason = "guiding-ddgi-frame-ray-budget-admits-no-probes";
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        int compactTraceCapacity = request.CompactTraceProbeCapacity;
        if (compactTraceCapacity <= 0)
        {
            reason = "guiding-compact-trace-payload-capacity-unavailable";
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        if (IsCompatibleLiveConfiguration(
                request.AppliedConfiguration,
                totalPhysical,
                requestedScheduled,
                compactTraceCapacity,
                directionSlots,
                request.StoragePackingMode))
        {
            reason = "guiding-existing-renderer-lifetime-allocation-reused";
            return request.AppliedConfiguration;
        }

        if (request.MemoryHeadroom == 0UL)
        {
            reason = "guiding-device-memory-headroom-unavailable";
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        int low = 1;
        int high = Math.Min(totalPhysical, compactTraceCapacity);
        SimpleDdgiGuidingFrameConfiguration best =
            SimpleDdgiGuidingFrameConfiguration.Disabled;
        while (low <= high)
        {
            int candidate = low + ((high - low) >> 1);
            if (TryCompileCandidate(
                    request,
                    totalPhysical,
                    candidate,
                    Math.Min(requestedScheduled, candidate),
                    directionSlots,
                    prerequisitesSatisfied,
                    out SimpleDdgiGuidingFrameConfiguration compiled))
            {
                best = compiled;
                low = candidate + 1;
            }
            else
            {
                high = candidate - 1;
            }
        }

        if (!best.IsEnabled)
        {
            reason = "guiding-16-mib-budget-admits-no-complete-layout";
            return SimpleDdgiGuidingFrameConfiguration.Disabled;
        }

        reason = best.RuntimeRequest.Layout.PhysicalProbeCapacity <
                 totalPhysical
            ? "guiding-admitted-deterministic-physical-probe-prefix"
            : "guiding-full-physical-probe-domain-admitted";
        return best;
    }

    internal static bool IsCompatibleLiveConfiguration(
        in SimpleDdgiGuidingFrameConfiguration configuration,
        int totalPhysicalProbeCapacity,
        int requestedScheduledProbeCapacity,
        int compactTraceProbeCapacity,
        int directionSlotsPerProbe,
        SimpleDdgiStoragePackingMode storagePackingMode)
    {
        if (!configuration.IsEnabled)
            return false;

        SimpleDdgiGuidingLayout layout =
            configuration.RuntimeRequest.Layout;
        SimpleDdgiGuidingSourceCacheLayout source =
            configuration.SourceCacheLayout;
        return layout.PhysicalProbeCapacity > 0 &&
               layout.PhysicalProbeCapacity <= totalPhysicalProbeCapacity &&
               layout.PhysicalProbeCapacity <= compactTraceProbeCapacity &&
               layout.ScheduledGuidedProbeCapacity > 0 &&
               layout.ScheduledGuidedProbeCapacity <=
               requestedScheduledProbeCapacity &&
               layout.DirectionSlotsPerProbe == directionSlotsPerProbe &&
               source.AdmittedGuidedPhysicalProbeCapacity ==
               layout.PhysicalProbeCapacity &&
               source.DirectionSlotsPerProbe == directionSlotsPerProbe &&
               configuration.RuntimeRequest.SourceStoragePackingMode ==
               storagePackingMode;
    }

    private static bool TryCompileCandidate(
        in SimpleDdgiGuidingConfigurationRequest request,
        int totalPhysicalProbeCapacity,
        int guidedPhysicalProbeCapacity,
        int scheduledGuidedProbeCapacity,
        int directionSlotsPerProbe,
        bool globalPrerequisiteGateAdmitted,
        out SimpleDdgiGuidingFrameConfiguration configuration)
    {
        configuration = SimpleDdgiGuidingFrameConfiguration.Disabled;
        try
        {
            SimpleDdgiGuidingSourceCacheLayout sidecar =
                SimpleDdgiGuidingSourceCacheLayoutCompiler.Compile(
                    new SimpleDdgiGuidingSourceCacheLayoutRequest(
                        Enabled: true,
                        TotalPhysicalProbeCapacity:
                        totalPhysicalProbeCapacity,
                        RequestedGuidedPhysicalProbeCapacity:
                        guidedPhysicalProbeCapacity,
                        DirectionSlotsPerProbe: directionSlotsPerProbe,
                        MemoryBudgetBytes: ExperimentBudgetBytes));
            if (!sidecar.IsAdmitted ||
                sidecar.AdmittedGuidedPhysicalProbeCapacity !=
                guidedPhysicalProbeCapacity)
            {
                return false;
            }

            SimpleDdgiGuidingLayout layout =
                SimpleDdgiGuidingLayoutCompiler.Compile(
                    new SimpleDdgiGuidingLayoutRequest(
                        SimpleDdgiGuidingDistributionConfiguration.EightByEight,
                        guidedPhysicalProbeCapacity,
                        scheduledGuidedProbeCapacity,
                        request.MinimumStorageBufferOffsetAlignment,
                        AllocateValidationReferenceBank: false)
                    {
                        DirectionSlotsPerProbe = directionSlotsPerProbe,
                        DirectionPdfSidecarBudgetBytes =
                            ExperimentBudgetBytes
                    });
            ulong bankBytes = layout.PersistentDoubleBufferedBytes / 2UL;
            if (!layout.TransientWorkspace.IsComplete ||
                layout.TotalBytes > ExperimentBudgetBytes ||
                layout.TotalBytes > request.MemoryHeadroom ||
                bankBytes > request.MaximumStorageBufferRange ||
                layout.TransientWorkspace.TotalBytes >
                request.MaximumStorageBufferRange ||
                layout.DirectionPdfSidecarBytes >
                request.MaximumStorageBufferRange)
            {
                return false;
            }

            configuration = new SimpleDdgiGuidingFrameConfiguration(
                new SimpleDdgiGuidingRuntimeRequest(true, layout)
                {
                    SourceStoragePackingMode = request.StoragePackingMode
                },
                sidecar,
                globalPrerequisiteGateAdmitted,
                SimpleDdgiGuidingProposalPolicy.ProductionBaseline);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                              OverflowException)
        {
            return false;
        }
    }
}

internal readonly record struct SimpleDdgiGuidingConfigurationRequest(
    bool SimpleDdgiActive,
    bool GraphUsesDirectionalGuiding,
    GlobalIlluminationSettings Settings,
    AdvancedGiRuntimeContentState RuntimeContentState,
    AdvancedGiPrerequisiteGateResult PrerequisiteGate,
    int TotalPhysicalProbeCapacity,
    int DirectionSlotsPerProbe,
    int CompactTraceProbeCapacity,
    SimpleDdgiStoragePackingMode StoragePackingMode,
    SimpleDdgiGuidingFrameConfiguration AppliedConfiguration,
    ulong MemoryHeadroom,
    ulong MinimumStorageBufferOffsetAlignment,
    ulong MaximumStorageBufferRange);
