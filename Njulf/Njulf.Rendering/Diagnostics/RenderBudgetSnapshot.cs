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
        public static RenderBudgetSnapshot Empty { get; } = new(
            RenderBudgetProfile.Development,
            Array.Empty<BudgetMetric>(),
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, 0, 0, 0, Array.Empty<UploadBudgetEntry>(), RenderBudgetStatus.Unknown),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, Array.Empty<RuntimeStallEvent>()),
            RenderBudgetStatus.Unknown);
    }
}
