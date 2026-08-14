namespace Njulf.Rendering.Debug
{
    /// <summary>
    /// Fence-complete counts for the sixteen scheduler-reason bits visualized
    /// by <see cref="DebugOverlayMode.DdgiUpdateReasons"/>. Counts follow the
    /// stable bit identities in SimpleDdgiSchedulerCandidateReason; a record
    /// carrying more than one bit also contributes to MultiReasonCount.
    /// </summary>
    public readonly record struct DebugDdgiUpdateReasonCounts(
        uint FreshCount,
        uint ScrollExposedCount,
        uint RegionalDirtyCount,
        uint GlobalDirtyCount,
        uint VisibleCount,
        uint RetryCount,
        uint RelocationRetryCount,
        uint SourceCacheInvalidCount,
        uint RoutineDueCount,
        uint ConvergencePendingCount,
        uint InactiveRetryCount,
        uint TopologyCount,
        uint VisiblePageCohortCount,
        uint RadiometricRelightCount,
        uint SegmentSelectiveCount,
        uint ResidualPropagationCount,
        uint MultiReasonCount)
    {
        public uint TotalReasonCount =>
            FreshCount + ScrollExposedCount + RegionalDirtyCount +
            GlobalDirtyCount + VisibleCount + RetryCount +
            RelocationRetryCount + SourceCacheInvalidCount +
            RoutineDueCount + ConvergencePendingCount + InactiveRetryCount +
            TopologyCount + VisiblePageCohortCount + RadiometricRelightCount +
            SegmentSelectiveCount + ResidualPropagationCount;
    }

    /// <summary>
    /// A bounded DDGI overlay result decoded only after its frame-slot fence
    /// completes. Generation fields prevent a result from a retired DDGI
    /// resource set from being presented as current.
    /// </summary>
    public readonly record struct DebugDdgiOverlayGpuCounters(
        bool Valid,
        DebugOverlayMode Mode,
        uint DrawnMarkerCount,
        uint FilteredMarkerCount,
        uint NonresidentMarkerCount,
        uint StaleMappingCount,
        uint StateUnavailableMarkerCount,
        uint InvalidTransactionCount,
        uint VolumeTableGeneration,
        uint SchedulerResourceGeneration,
        uint ResidencyResourceGeneration,
        DebugDdgiUpdateReasonCounts UpdateReasons)
    {
        public static DebugDdgiOverlayGpuCounters Empty => default;
    }
}
