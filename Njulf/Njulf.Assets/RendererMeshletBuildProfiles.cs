namespace Njulf.Assets;

/// <summary>Deterministic meshlet clusterization parameters qualified per target GPU.</summary>
public sealed record RendererMeshletBuildProfile(
    string Id,
    int MaxVertices,
    int MinTriangles,
    int MaxTriangles,
    float ConeWeight,
    float SplitFactor)
{
    public MeshletBuilder CreateBuilder() => new(
        MaxVertices,
        MaxTriangles,
        MinTriangles,
        ConeWeight,
        SplitFactor);
}

public static class RendererMeshletBuildProfiles
{
    public static RendererMeshletBuildProfile Rtx3060Baseline { get; } =
        new("rtx3060-baseline-48v-64t", 48, 0, 64, 0f, 0f);

    public static RendererMeshletBuildProfile Rtx3060FlexCone025 { get; } =
        new("rtx3060-flex-48v-32-64t-cone025-split2", 48, 32, 64, 0.25f, 2f);

    public static RendererMeshletBuildProfile Rtx3060FlexCone050 { get; } =
        new("rtx3060-flex-48v-32-64t-cone050-split2", 48, 32, 64, 0.5f, 2f);

    /// <summary>
    /// The checked-in production choice remains the qualified baseline until a
    /// candidate clears the measured adoption gate. This prevents an offline
    /// heuristic from silently selecting a slower RTX program.
    /// </summary>
    public static RendererMeshletBuildProfile Production { get; } =
        Rtx3060Baseline;

    public static IReadOnlyList<RendererMeshletBuildProfile> QualificationCandidates { get; } =
    [
        Rtx3060Baseline,
        Rtx3060FlexCone025,
        Rtx3060FlexCone050
    ];
}

public sealed record RendererMeshletProfileQualificationSample(
    RendererMeshletBuildProfile Profile,
    double GpuFrameP95Milliseconds,
    bool QualityGatePassed);

public static class RendererMeshletProfileQualification
{
    public const double RequiredP95ImprovementFraction = 0.03;

    public static RendererMeshletBuildProfile SelectProductionCandidate(
        IReadOnlyList<RendererMeshletProfileQualificationSample> samples,
        RendererMeshletBuildProfile baseline)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(baseline);
        RendererMeshletProfileQualificationSample baselineSample = samples
            .SingleOrDefault(sample => sample.Profile.Id == baseline.Id) ??
            throw new ArgumentException(
                $"Qualification samples do not contain baseline '{baseline.Id}'.",
                nameof(samples));
        ValidateSample(baselineSample, nameof(samples));

        double adoptionThreshold = baselineSample.GpuFrameP95Milliseconds *
            (1.0 - RequiredP95ImprovementFraction);
        RendererMeshletProfileQualificationSample? winner = samples
            .Where(sample => sample.Profile.Id != baseline.Id)
            .Where(sample => sample.QualityGatePassed)
            .Where(sample =>
                double.IsFinite(sample.GpuFrameP95Milliseconds) &&
                sample.GpuFrameP95Milliseconds > 0.0 &&
                sample.GpuFrameP95Milliseconds <= adoptionThreshold)
            .OrderBy(sample => sample.GpuFrameP95Milliseconds)
            .ThenBy(sample => sample.Profile.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return winner?.Profile ?? baseline;
    }

    private static void ValidateSample(
        RendererMeshletProfileQualificationSample sample,
        string parameterName)
    {
        if (!double.IsFinite(sample.GpuFrameP95Milliseconds) ||
            sample.GpuFrameP95Milliseconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "GPU p95 samples must be finite and positive.");
        }
    }
}
