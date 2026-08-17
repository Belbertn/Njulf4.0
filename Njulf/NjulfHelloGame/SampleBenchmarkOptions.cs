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
    public const int ProductionMinimumAdditionalSettlingFrameCount = 4_096;

    public static SampleBenchmarkOptions Disabled { get; } = new(
        Enabled: false,
        WarmupFrameCount: 0,
        MeasureFrameCount: 0,
        ReportPath: null,
        DisableVSync: true,
        BudgetProfileOverride: null,
        MaterialGiQualificationCandidate: false);

    /// <summary>Stable identity shared by deterministic A/B variants.</summary>
    public string CapturePairId { get; init; } = string.Empty;
    /// <summary>Variant label such as baseline, no-decals, or forced-old-far-field.</summary>
    public string CaptureVariant { get; init; } = "baseline";
    /// <summary>Reject captures that are not compiled/validated as ProductionTiming.</summary>
    public bool RequireProductionTiming { get; init; }
    /// <summary>Linear-RGB PFM reference used by the post-measurement image gate.</summary>
    public string HdrReferencePath { get; init; } = string.Empty;
    /// <summary>Optional destination for the post-measurement linear-RGB PFM capture.</summary>
    public string HdrCandidatePath { get; init; } = string.Empty;
    /// <summary>Maximum relative RMSE accepted by the linear-HDR comparison gate.</summary>
    public double HdrMaximumRelativeRmse { get; init; } =
        SampleBenchmarkHdrDifference.DefaultMaximumRelativeRmse;
    /// <summary>Validated njulf-nsight-shader-profile-v1 JSON artifact.</summary>
    public string ShaderProfileArtifactPath { get; init; } = string.Empty;
    public bool RequireShaderProfileEvidence { get; init; }
    /// <summary>
    /// Maximum post-warmup frames available to convergence/readback settling.
    /// Production captures must retain at least the full tail-opportunity window.
    /// </summary>
    public int MaximumAdditionalSettlingFrameCount { get; init; } =
        ProductionMinimumAdditionalSettlingFrameCount;
}
