using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Exact C5 graph profile. Only the three measured power-of-two scales are
/// accepted; arbitrary research scales never silently enter production graph
/// allocation or binding.
/// </summary>
internal readonly record struct AdvancedGiNearFieldGraphProfile(
    float ResolutionScale,
    SimpleDdgiNearFieldResidualFormat SourceFormat,
    int FilterIterationCount)
{
    public static AdvancedGiNearFieldGraphProfile HalfResolutionReference { get; } = new(
        ResolutionScale: 0.5f,
        SourceFormat: SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
        FilterIterationCount: 2);

    public bool IsSupported =>
        float.IsFinite(ResolutionScale) &&
        ResolutionScale is 0.5f or 0.25f or 0.125f &&
        SourceFormat == SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat &&
        FilterIterationCount is >= 0 and <= 8;

    public RenderGraphResourceSizePolicy TraceSizePolicy => ResolutionScale switch
    {
        0.5f => RenderGraphResourceSizePolicy.HalfResolution,
        0.25f => RenderGraphResourceSizePolicy.QuarterResolution,
        0.125f => RenderGraphResourceSizePolicy.EighthResolution,
        _ => RenderGraphResourceSizePolicy.Dynamic
    };

    public static AdvancedGiNearFieldGraphProfile From(
        in SimpleDdgiNearFieldResidualProfile profile) => new(
        profile.ResolutionScale,
        profile.SourceFormat,
        profile.FilterIterationCount);
}

/// <summary>
/// Immutable graph-creation decision.  It is deliberately derived from
/// effective modes, never directly from user intent: a requested experiment
/// that failed capability, evidence, or allocation admission cannot leak a
/// descriptor or a no-op pass into the renderer.
/// </summary>
internal readonly record struct AdvancedGiRenderGraphModes(
    SimpleDdgiReceiverFeedbackMode ReceiverFeedback,
    DdgiOpacityMicromapMode OpacityMicromap,
    SimpleDdgiDirectionalGuidingMode DirectionalGuiding,
    GiCausticMode Caustics,
    SimpleDdgiNearFieldResidualMode NearFieldResidual,
    AdvancedGiNearFieldGraphProfile NearFieldProfile = default)
{
    public static AdvancedGiRenderGraphModes Disabled { get; } = new(
        SimpleDdgiReceiverFeedbackMode.Off,
        DdgiOpacityMicromapMode.Off,
        SimpleDdgiDirectionalGuidingMode.Off,
        GiCausticMode.Off,
        SimpleDdgiNearFieldResidualMode.Off);

    public bool UsesExactReceiverFeedback =>
        ReceiverFeedback == SimpleDdgiReceiverFeedbackMode.ExactCompacted;

    public bool UsesOpacityMicromaps =>
        OpacityMicromap is DdgiOpacityMicromapMode.ExtFourStateExperiment or
            DdgiOpacityMicromapMode.AutoQualified;

    public bool UsesDirectionalGuiding =>
        DirectionalGuiding is
            SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment or
            SimpleDdgiDirectionalGuidingMode.AutoQualified;

    // PhotonReference and Reference are deterministic CPU/reference modes.
    // They intentionally do not create a GPU graph variant.
    public bool UsesCausticWorldCache =>
        Caustics is GiCausticMode.WorldCacheExperiment or
            GiCausticMode.AutoQualified;

    /// <summary>
    /// A C5 request alone is insufficient to create a graph.  The source
    /// attachment/profile must be an exact graph-supported R16 contract.
    /// </summary>
    public bool UsesNearFieldHiZResidual =>
        NearFieldProfile.IsSupported &&
        NearFieldResidual is
            SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment or
            SimpleDdgiNearFieldResidualMode.HiZAdaptive or
            SimpleDdgiNearFieldResidualMode.AutoQualified;

    public bool UsesNearFieldFiltering =>
        UsesNearFieldHiZResidual && NearFieldProfile.FilterIterationCount > 0;

    /// <summary>
    /// Whether this selection changes the immutable render-graph inventory.
    /// B1 is deliberately excluded: its candidate writes are embedded in the
    /// real receiver passes and its sort/reduce transaction is recorded after
    /// graph execution, so inventing graph pass instances would duplicate or
    /// mis-order the work.
    /// </summary>
    public bool HasGpuFeature =>
        UsesOpacityMicromaps || UsesDirectionalGuiding ||
        UsesCausticWorldCache || UsesNearFieldHiZResidual;

    /// <summary>
    /// The compatibility overload intentionally leaves C5 graph work absent:
    /// mode diagnostics do not carry the validated source-format, scale, and
    /// filter profile.  A renderer that has completed C5 admission must call
    /// the overload carrying that profile instead.
    /// </summary>
    public static AdvancedGiRenderGraphModes FromEffectiveModes(
        in GiRoadmapExperimentModeDiagnostics modes) => new(
        modes.ReceiverFeedback.EffectiveMode,
        modes.OpacityMicromap.EffectiveMode,
        modes.DirectionalGuiding.EffectiveMode,
        modes.Caustic.EffectiveMode,
        modes.NearFieldResidual.EffectiveMode,
        default);

    public static AdvancedGiRenderGraphModes FromEffectiveModes(
        in GiRoadmapExperimentModeDiagnostics modes,
        in SimpleDdgiNearFieldResidualProfile nearFieldProfile) => new(
        modes.ReceiverFeedback.EffectiveMode,
        modes.OpacityMicromap.EffectiveMode,
        modes.DirectionalGuiding.EffectiveMode,
        modes.Caustic.EffectiveMode,
        modes.NearFieldResidual.EffectiveMode,
        AdvancedGiNearFieldGraphProfile.From(nearFieldProfile));
}
