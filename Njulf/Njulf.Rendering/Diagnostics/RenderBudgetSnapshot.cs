using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record RenderBudgetSnapshot(
        RenderBudgetProfile Profile,
        IReadOnlyList<BudgetMetric> Metrics,
        MemoryBudgetSnapshot Memory,
        UploadBudgetSnapshot Upload,
        RuntimeStallSnapshot Stalls,
        RenderBudgetStatus OverallStatus)
    {
        /// <summary>
        /// Unique GI residency and non-additive component detail. Kept as an init-only member so
        /// existing positional construction of budget snapshots remains source compatible.
        /// </summary>
        public GiResidencySnapshot GiResidency { get; init; } = GiResidencySnapshot.Unavailable;

        public static RenderBudgetSnapshot Empty { get; } = new(
            RenderBudgetProfile.Development,
            Array.Empty<BudgetMetric>(),
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, 0, 0, 0, Array.Empty<UploadBudgetEntry>(), RenderBudgetStatus.Unknown),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, Array.Empty<RuntimeStallEvent>()),
            RenderBudgetStatus.Unknown);
    }
}
