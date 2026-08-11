using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSchedulerCostModelTests
{
    [Test]
    public void MissingCompletedPrimaryWork_RetainsTheLastEstimate()
    {
        var model = new SimpleDdgiSchedulerCostModel();
        SimpleDdgiSchedulerCostEstimate before = model.Estimate;

        bool accepted = model.Observe(new SimpleDdgiSchedulerCostSample(
            PrimaryRayCount: 0,
            VisibilityRayCount: ulong.MaxValue,
            AlphaCandidateCount: ulong.MaxValue,
            MaterialEvaluationCount: ulong.MaxValue,
            FarFieldStepCount: ulong.MaxValue));

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(model.Estimate, Is.EqualTo(before));
            Assert.That(model.Estimate.AcceptedSampleCount, Is.Zero);
        });
    }

    [Test]
    public void CompletedCounters_UpdateBoundedEwmaAndPredictExpensivePrimaryWork()
    {
        var model = new SimpleDdgiSchedulerCostModel();

        Assert.That(model.Observe(new SimpleDdgiSchedulerCostSample(
            PrimaryRayCount: 100,
            VisibilityRayCount: 800,
            AlphaCandidateCount: 400,
            MaterialEvaluationCount: 800,
            FarFieldStepCount: 3200)), Is.True);

        SimpleDdgiSchedulerCostEstimate estimate = model.Estimate;
        float primary = SimpleDdgiSchedulerCostModel.EstimateRelativeCost(
            estimate,
            rayCount: 64,
            primaryTrace: true,
            cachedOnly: false);
        float cached = SimpleDdgiSchedulerCostModel.EstimateRelativeCost(
            estimate,
            rayCount: 64,
            primaryTrace: false,
            cachedOnly: true);

        Assert.Multiple(() =>
        {
            Assert.That(estimate.AcceptedSampleCount, Is.EqualTo(1));
            Assert.That(
                SimpleDdgiSchedulerCostModel.DecodeQ8(estimate.VisibilityPerPrimaryQ8),
                Is.GreaterThan(1.0f));
            Assert.That(
                SimpleDdgiSchedulerCostModel.DecodeQ8(estimate.AlphaCandidatesPerPrimaryQ8),
                Is.GreaterThan(0.0f));
            Assert.That(
                SimpleDdgiSchedulerCostModel.DecodeQ8(estimate.FarFieldStepsPerPrimaryQ8),
                Is.LessThanOrEqualTo(64.0f));
            Assert.That(primary, Is.GreaterThan(cached));
        });
    }

    [Test]
    public void ErrorReductionPerCost_RespondsToReceiverImportanceAndDeadlineDebt()
    {
        const float cost = 20.0f;
        float baseline = SimpleDdgiSchedulerCostModel.ExpectedErrorReductionPerCost(
            residual: 0.2f,
            receiverContribution: 0.0f,
            ageFrames: 1,
            maximumLatencyFrames: 60,
            relativeCost: cost);
        float visible = SimpleDdgiSchedulerCostModel.ExpectedErrorReductionPerCost(
            residual: 0.2f,
            receiverContribution: 4.0f,
            ageFrames: 1,
            maximumLatencyFrames: 60,
            relativeCost: cost);
        float deadline = SimpleDdgiSchedulerCostModel.ExpectedErrorReductionPerCost(
            residual: 0.2f,
            receiverContribution: 4.0f,
            ageFrames: 60,
            maximumLatencyFrames: 60,
            relativeCost: cost);

        Assert.Multiple(() =>
        {
            Assert.That(visible, Is.GreaterThan(baseline));
            Assert.That(deadline, Is.GreaterThan(visible));
        });
    }

    [Test]
    public void ShaderPolicy_UsesDelayedQ8CostsOnlyToReorderMaintenanceLanes()
    {
        string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_shared.glsl");
        string classify = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_classify.comp");

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEATURE_COST_AWARE_PRIORITY = 1u << 12u"));
            Assert.That(shared, Does.Contain("SchedulerDecodeCostFrequency(40u)"));
            Assert.That(shared, Does.Contain("SchedulerDecodeCostFrequency(43u)"));
            Assert.That(shared, Does.Contain("SchedulerErrorReductionPerCost"));
            Assert.That(classify, Does.Contain(
                "workClass >= SIMPLE_DDGI_SCHEDULER_WORK_NEAR_MAINTENANCE"));
            Assert.That(classify, Does.Contain("stateAge >= maximumLatency"));
            Assert.That(classify, Does.Contain("complete participant set"));
        });
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate repository file.",
            Path.Combine(pathParts));
    }
}
