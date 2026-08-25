using System.Text.Json.Serialization;
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
    /// <summary>Exact workload activation whose measured evidence is required.</summary>
    public string Activation { get; init; } = SampleBenchmarkActivation.None;
    public string ActivationFingerprint { get; init; } =
        SampleBenchmarkActivation.CreateFingerprint(
            SampleBenchmarkActivation.None);
    /// <summary>Named deterministic camera program used by this capture.</summary>
    [JsonRequired]
    public SampleBenchmarkTrajectoryKind Trajectory { get; init; } =
        SampleBenchmarkTrajectoryKind.Stationary;
    /// <summary>Stable contract fingerprint for the selected camera/state program.</summary>
    public string TrajectoryFingerprint { get; init; } = string.Empty;
    /// <summary>
    /// Exact Sponza content fixture used by the measured workload. Architecture
    /// is the shipping/default scene; animation evidence is applicable only to
    /// the explicit AnimationDemo fixture.
    /// </summary>
    [JsonRequired]
    public SampleSponzaFixtureMode SponzaFixtureMode { get; init; } =
        SampleSponzaFixtureMode.Architecture;
    /// <summary>
    /// Bistro lighting script paired with Bistro presentation/loop trajectories.
    /// It is retained in benchmark options so every measured pose can be
    /// validated without depending on mutable host state.
    /// </summary>
    [JsonRequired]
    public SampleBistroQualityCaptureVariant TrajectoryBistroVariant { get; init; } =
        SampleBistroQualityCaptureVariant.SunScaleStep;
    /// <summary>Reject captures that are not compiled/validated as ProductionTiming.</summary>
    public bool RequireProductionTiming { get; init; }
    /// <summary>Linear-RGB PFM reference used by the post-measurement image gate.</summary>
    public string HdrReferencePath { get; init; } = string.Empty;
    /// <summary>Optional destination for the post-measurement linear-RGB PFM capture.</summary>
    public string HdrCandidatePath { get; init; } = string.Empty;
    /// <summary>Maximum relative RMSE accepted by the linear-HDR comparison gate.</summary>
    public double HdrMaximumRelativeRmse { get; init; } =
        SampleBenchmarkHdrDifference.DefaultMaximumRelativeRmse;
    /// <summary>Maximum NVIDIA HDR-FLIP P95 accepted by the image gate.</summary>
    public double HdrMaximumFlipP95 { get; init; } = 0.02;
    /// <summary>Optional strict named-ROI quality contract.</summary>
    public string HdrQualityContractPath { get; init; } = string.Empty;
    /// <summary>Validated njulf-nsight-shader-profile-v1 JSON artifact.</summary>
    public string ShaderProfileArtifactPath { get; init; } = string.Empty;
    public bool RequireShaderProfileEvidence { get; init; }
    /// <summary>
    /// Enforces the shipping 1920x1080/60 Hz frame-time and two-GiB memory
    /// contract in addition to the renderer's component budget metrics.
    /// </summary>
    public bool RequireRealtime1080p60Target { get; init; }
    /// <summary>
    /// Maximum post-warmup frames available to convergence/readback settling.
    /// Production captures must retain at least the full tail-opportunity window.
    /// </summary>
    public int MaximumAdditionalSettlingFrameCount { get; init; } =
        ProductionMinimumAdditionalSettlingFrameCount;
}
