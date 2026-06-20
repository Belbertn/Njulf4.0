using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record RenderGraphDiagnostics(
        int ResourceCount,
        int PassCount,
        int PlannedBarrierCount,
        int ExecutedBarrierCount,
        int TransientResourceCount,
        int PersistentResourceCount,
        int AliasableResourceCount,
        int ImportedResourceCount,
        int OwnedRenderTargetCount,
        int AsyncComputeCandidatePassCount,
        int AsyncComputeEnabledPassCount,
        int QueueOwnershipTransitionCount,
        ulong ResourceMemoryEstimateBytes,
        IReadOnlyList<RenderGraphResourceDiagnostics> Resources,
        IReadOnlyList<RenderGraphPassDiagnostics> Passes,
        IReadOnlyList<RenderGraphBarrierDiagnostics> Barriers)
    {
        public static RenderGraphDiagnostics Empty { get; } = new(
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
            0,
            0,
            0,
            [],
            [],
            []);
    }

    public sealed record RenderGraphResourceDiagnostics(
        string Id,
        string DebugName,
        string Kind,
        string Format,
        string SizePolicy,
        string Lifetime,
        bool Persistent,
        bool GraphOwned,
        int OwnedTargetCount,
        ulong EstimatedBytes);

    public sealed record RenderGraphPassDiagnostics(
        string Name,
        bool EnabledByFeatureIsolation,
        string QueueIntent,
        bool AsyncComputeCandidate,
        bool AsyncComputeEnabled,
        string AsyncComputeReason,
        IReadOnlyList<string> Reads,
        IReadOnlyList<string> Writes,
        IReadOnlyList<string> ReadWrites);

    public sealed record RenderGraphBarrierDiagnostics(
        string PassName,
        string Resource,
        string PreviousAccess,
        string NextAccess,
        string OldLayout,
        string NewLayout,
        string SourceStage,
        string SourceAccess,
        string DestinationStage,
        string DestinationAccess,
        string PreviousQueueIntent,
        string QueueIntent,
        bool QueueOwnershipTransition,
        bool Executed);
}
