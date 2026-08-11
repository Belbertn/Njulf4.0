using System;

namespace Njulf.Rendering.Data;

public readonly record struct SimpleDdgiSchedulerCostSample(
    ulong PrimaryRayCount,
    ulong VisibilityRayCount,
    ulong AlphaCandidateCount,
    ulong MaterialEvaluationCount,
    ulong FarFieldStepCount);

/// <summary>
/// Delayed, dimensionless work frequencies encoded as unsigned Q8.8 values.
/// They are scheduling hints only and never reduce the qualified request/ray
/// budgets or alter the convergence certificate.
/// </summary>
public readonly record struct SimpleDdgiSchedulerCostEstimate(
    uint VisibilityPerPrimaryQ8,
    uint AlphaCandidatesPerPrimaryQ8,
    uint MaterialEvaluationsPerPrimaryQ8,
    uint FarFieldStepsPerPrimaryQ8,
    ulong AcceptedSampleCount)
{
    public const uint OneQ8 = 256;

    public static SimpleDdgiSchedulerCostEstimate Default { get; } = new(
        VisibilityPerPrimaryQ8: OneQ8,
        AlphaCandidatesPerPrimaryQ8: 0,
        MaterialEvaluationsPerPrimaryQ8: OneQ8,
        FarFieldStepsPerPrimaryQ8: 0,
        AcceptedSampleCount: 0);
}

/// <summary>
/// Bounded EWMA over completed GPU work counters. Counter readback is delayed,
/// so samples are never associated with the frame currently being admitted.
/// Missing/invalid samples retain the last complete estimate.
/// </summary>
public sealed class SimpleDdgiSchedulerCostModel
{
    private const double Alpha = 0.125;
    private const double MaximumFrequency = 64.0;
    private double _visibility = 1.0;
    private double _alphaCandidates;
    private double _materialEvaluations = 1.0;
    private double _farFieldSteps;
    private ulong _acceptedSampleCount;

    public SimpleDdgiSchedulerCostEstimate Estimate => new(
        EncodeQ8(_visibility),
        EncodeQ8(_alphaCandidates),
        EncodeQ8(_materialEvaluations),
        EncodeQ8(_farFieldSteps),
        _acceptedSampleCount);

    public bool Observe(SimpleDdgiSchedulerCostSample sample)
    {
        if (sample.PrimaryRayCount == 0)
            return false;

        double denominator = sample.PrimaryRayCount;
        double visibility = BoundedRatio(sample.VisibilityRayCount, denominator);
        double alphaCandidates = BoundedRatio(sample.AlphaCandidateCount, denominator);
        double materialEvaluations = BoundedRatio(sample.MaterialEvaluationCount, denominator);
        double farFieldSteps = BoundedRatio(sample.FarFieldStepCount, denominator);
        if (!double.IsFinite(visibility) ||
            !double.IsFinite(alphaCandidates) ||
            !double.IsFinite(materialEvaluations) ||
            !double.IsFinite(farFieldSteps))
        {
            return false;
        }

        _visibility = Blend(_visibility, visibility);
        _alphaCandidates = Blend(_alphaCandidates, alphaCandidates);
        _materialEvaluations = Blend(_materialEvaluations, materialEvaluations);
        _farFieldSteps = Blend(_farFieldSteps, farFieldSteps);
        _acceptedSampleCount++;
        return true;
    }

    public static float EstimateRelativeCost(
        SimpleDdgiSchedulerCostEstimate estimate,
        int rayCount,
        bool primaryTrace,
        bool cachedOnly)
    {
        if (rayCount <= 0)
            return 0.0f;
        if (cachedOnly)
            return rayCount * 0.15f;
        if (!primaryTrace)
            return rayCount * 0.35f;

        float visibility = DecodeQ8(estimate.VisibilityPerPrimaryQ8);
        float alpha = DecodeQ8(estimate.AlphaCandidatesPerPrimaryQ8);
        float material = DecodeQ8(estimate.MaterialEvaluationsPerPrimaryQ8);
        float farSteps = DecodeQ8(estimate.FarFieldStepsPerPrimaryQ8);
        float perRay = 1.0f +
            visibility * 0.75f +
            alpha * 0.50f +
            material * 0.25f +
            farSteps * 0.0625f;
        return rayCount * Math.Max(perRay, 0.01f);
    }

    public static float ExpectedErrorReductionPerCost(
        float residual,
        float receiverContribution,
        int ageFrames,
        int maximumLatencyFrames,
        float relativeCost)
    {
        float safeResidual = float.IsFinite(residual)
            ? Math.Max(residual, 0.0f)
            : 1.0f;
        float safeContribution = float.IsFinite(receiverContribution)
            ? Math.Clamp(receiverContribution, 0.0f, 16.0f)
            : 0.0f;
        int safeAge = Math.Max(ageFrames, 0);
        int deadline = Math.Max(maximumLatencyFrames, 1);
        // Deadline debt is deliberately superlinear near the bound. It changes
        // order only; the scheduler's hard quota and audit still own work.
        float normalizedAge = Math.Clamp(safeAge / (float)deadline, 0.0f, 4.0f);
        float deadlineDebt = normalizedAge * normalizedAge;
        float benefit = Math.Max(safeResidual, 1e-4f) *
            (1.0f + safeContribution) + deadlineDebt;
        return benefit / Math.Max(relativeCost, 1e-4f);
    }

    private static double Blend(double previous, double current) =>
        Math.Clamp(previous + (current - previous) * Alpha, 0.0, MaximumFrequency);

    private static double BoundedRatio(ulong numerator, double denominator) =>
        Math.Clamp(numerator / denominator, 0.0, MaximumFrequency);

    private static uint EncodeQ8(double value) => checked((uint)Math.Clamp(
        Math.Round(Math.Clamp(value, 0.0, MaximumFrequency) *
            SimpleDdgiSchedulerCostEstimate.OneQ8),
        0.0,
        ushort.MaxValue));

    public static float DecodeQ8(uint value) =>
        Math.Min(value, ushort.MaxValue) /
        (float)SimpleDdgiSchedulerCostEstimate.OneQ8;
}
