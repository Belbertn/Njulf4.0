namespace Njulf.Rendering.Resources;

public enum GiExperimentStage
{
    Disabled,
    PrerequisiteMissing,
    CapabilityAvailable,
    QualificationFailed,
    Active
}

/// <summary>
/// Common fail-closed admission result for independently evaluated roadmap
/// features. Research capability is not equivalent to runtime promotion.
/// </summary>
public readonly record struct GiExperimentAdmission(
    string Id,
    bool Requested,
    bool CapabilitySupported,
    bool Active,
    GiExperimentStage Stage,
    ulong AllocatedBytes,
    string Status)
{
    public static GiExperimentAdmission Disabled(string id) => new(
        id,
        false,
        false,
        false,
        GiExperimentStage.Disabled,
        0UL,
        "disabled");

    public static GiExperimentAdmission Missing(
        string id,
        string status,
        bool capabilitySupported = false) => new(
            id,
            true,
            capabilitySupported,
            false,
            GiExperimentStage.PrerequisiteMissing,
            0UL,
            status);
}

public readonly record struct GiRoadmapExperimentDiagnostics(
    GiExperimentAdmission DirectionalFog,
    GiExperimentAdmission OpacityMicromap,
    GiExperimentAdmission RayTracingInvocationReorder,
    GiExperimentAdmission DirectionalRayGuiding,
    GiExperimentAdmission TaggedCausticCache,
    GiExperimentAdmission NearFieldResidual)
{
    /// <summary>
    /// Versioned requested/supported/admitted/effective state.  The positional
    /// legacy admissions remain during the migration so older snapshots stay
    /// readable, but callers must not infer a live GPU feature from them.
    /// </summary>
    public GiRoadmapExperimentModeDiagnostics Modes { get; init; } =
        GiRoadmapExperimentModeDiagnostics.Disabled;

    /// <summary>
    /// Fence-complete B1 capture, compaction, timing, and central-memory
    /// telemetry. A readable publication is distinct from requested or
    /// effective mode state and is never inferred from either.
    /// </summary>
    public SimpleDdgiReceiverFeedbackDiagnostics ReceiverFeedbackRuntime
    {
        get;
        init;
    } = SimpleDdgiReceiverFeedbackDiagnostics.Disabled;

    /// <summary>
    /// Fence-complete C1 native transaction and memory telemetry.  This is
    /// distinct from mode admission: an explicit development mode can be
    /// effective with no eligible meshes and therefore zero resident bytes.
    /// </summary>
    public OpacityMicromapGpuRuntimeSnapshot OpacityMicromapRuntime { get; init; } =
        OpacityMicromapGpuRuntimeSnapshot.Disabled;

    /// <summary>
    /// Fence-complete C3 resource, workload, validation, timing, and central
    /// memory telemetry. This is observability only; it is not a substitute
    /// for a signed qualification manifest.
    /// </summary>
    public SimpleDdgiDirectionalGuidingDiagnostics DirectionalGuidingRuntime
    {
        get;
        init;
    } = SimpleDdgiDirectionalGuidingDiagnostics.Disabled;

    /// <summary>
    /// Fence-complete C4 cache publication, pass timing, and exact central
    /// memory telemetry.  A requested mode or allocated buffer alone never
    /// makes this record authoritative.
    /// </summary>
    public GiCausticDiagnostics CausticRuntime { get; init; } =
        GiCausticDiagnostics.Disabled;

    public static GiRoadmapExperimentDiagnostics Disabled { get; } = new(
        GiExperimentAdmission.Disabled("B5"),
        GiExperimentAdmission.Disabled("C1"),
        GiExperimentAdmission.Disabled("C2"),
        GiExperimentAdmission.Disabled("C3"),
        GiExperimentAdmission.Disabled("C4"),
        GiExperimentAdmission.Disabled("C5"));

    public ulong AllocatedBytes => checked(
        ReceiverFeedbackRuntime.Memory.AllocatedBytes +
        DirectionalFog.AllocatedBytes +
        Math.Max(
            OpacityMicromap.AllocatedBytes,
            OpacityMicromapRuntime.AllocatedBytes) +
        RayTracingInvocationReorder.AllocatedBytes +
        Math.Max(
            DirectionalRayGuiding.AllocatedBytes,
            DirectionalGuidingRuntime?.Memory.AllocatedBytes ?? 0UL) +
        Math.Max(
            TaggedCausticCache.AllocatedBytes,
            CausticRuntime?.Memory.AllocatedBytes ?? 0UL) +
        NearFieldResidual.AllocatedBytes);
}
