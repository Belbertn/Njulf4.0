using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkOptions(
    bool Enabled,
    int WarmupFrameCount,
    int MeasureFrameCount,
    string? ReportPath,
    bool DisableVSync = true,
    RenderBudgetProfileKind? BudgetProfileOverride = null,
    bool MaterialGiQualificationCandidate = false)
{
    public static SampleBenchmarkOptions Disabled { get; } = new(
        Enabled: false,
        WarmupFrameCount: 0,
        MeasureFrameCount: 0,
        ReportPath: null,
        DisableVSync: true,
        BudgetProfileOverride: null,
        MaterialGiQualificationCandidate: false);
}
