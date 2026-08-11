using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiDirectionalGuidingEstimatorTests
{
    [Test]
    public void MixturePdf_RetainsUniformSupportAndRecordsTheEffectiveFloor()
    {
        double pdf = SimpleDdgiGuidingReference.EvaluateMixturePdf(
            guidedPdf: 0.0d,
            requestedUniformFraction: 0.0d);

        Assert.Multiple(() =>
        {
            Assert.That(pdf, Is.EqualTo(
                SimpleDdgiGuidingReference.MinimumUniformFraction *
                SimpleDdgiGuidingReference.UniformSpherePdf).Within(1.0e-15d));
            Assert.That(SimpleDdgiGuidingReference.SelectMixtureBranch(
                SimpleDdgiGuidingReference.MinimumUniformFraction - 1.0e-12d,
                0.0d), Is.EqualTo(SimpleDdgiDirectionMixtureBranch.Uniform));
            Assert.That(SimpleDdgiGuidingReference.SelectMixtureBranch(
                SimpleDdgiGuidingReference.MinimumUniformFraction,
                0.0d), Is.EqualTo(SimpleDdgiDirectionMixtureBranch.Guided));
        });
    }

    [Test]
    public void BalanceHeuristic_IntegratesAConstantWithBothRadiometricTechniques()
    {
        const int uniformCount = 3;
        const int mixtureCount = 7;
        const double integrand = 2.75d;
        // A uniform guide makes pMix exactly the uniform sphere density,
        // independently of alpha, and gives an analytic constant integral.
        double guidedPdf = SimpleDdgiGuidingReference.UniformSpherePdf;
        double contributionSum = 0.0d;
        for (int sample = 0; sample < uniformCount; sample++)
        {
            contributionSum +=
                SimpleDdgiGuidingReference.EvaluateMultiTechniqueContribution(
                    integrand,
                    uniformCount,
                    mixtureCount,
                    SimpleDdgiDirectionSamplingTechnique.UniformMaintenance,
                    guidedPdf,
                    requestedUniformFraction: 0.35d);
        }
        for (int sample = 0; sample < mixtureCount; sample++)
        {
            contributionSum +=
                SimpleDdgiGuidingReference.EvaluateMultiTechniqueContribution(
                    integrand,
                    uniformCount,
                    mixtureCount,
                    SimpleDdgiDirectionSamplingTechnique.Mixture,
                    guidedPdf,
                    requestedUniformFraction: 0.35d);
        }

        double exactIntegral = integrand /
            SimpleDdgiGuidingReference.UniformSpherePdf;
        double uniformWeight = SimpleDdgiGuidingReference.CalculateBalanceWeight(
            uniformCount,
            SimpleDdgiGuidingReference.UniformSpherePdf,
            mixtureCount,
            SimpleDdgiGuidingReference.UniformSpherePdf);
        double mixtureWeight = SimpleDdgiGuidingReference.CalculateBalanceWeight(
            mixtureCount,
            SimpleDdgiGuidingReference.UniformSpherePdf,
            uniformCount,
            SimpleDdgiGuidingReference.UniformSpherePdf);

        Assert.Multiple(() =>
        {
            Assert.That(contributionSum, Is.EqualTo(exactIntegral).Within(1.0e-12d));
            Assert.That(uniformWeight + mixtureWeight,
                Is.EqualTo(1.0d).Within(1.0e-15d));
        });
    }

    [Test]
    public void BalanceHeuristic_HandlesAbsentTechniquesExplicitly()
    {
        double guidedPdf = 0.5d;
        double mixturePdf = SimpleDdgiGuidingReference.EvaluateMixturePdf(
            guidedPdf, 0.25d);

        double absent =
            SimpleDdgiGuidingReference.EvaluateMultiTechniqueContribution(
                1.0d,
                uniformMaintenanceSampleCount: 0,
                mixtureSampleCount: 4,
                SimpleDdgiDirectionSamplingTechnique.UniformMaintenance,
                guidedPdf,
                requestedUniformFraction: 0.25d);
        double mixture =
            SimpleDdgiGuidingReference.EvaluateMultiTechniqueContribution(
                1.0d,
                uniformMaintenanceSampleCount: 0,
                mixtureSampleCount: 4,
                SimpleDdgiDirectionSamplingTechnique.Mixture,
                guidedPdf,
                requestedUniformFraction: 0.25d);

        Assert.Multiple(() =>
        {
            Assert.That(absent, Is.Zero);
            Assert.That(mixture, Is.EqualTo(1.0d / (4.0d * mixturePdf))
                .Within(1.0e-15d));
            Assert.That(SimpleDdgiGuidingReference.CalculateBalanceWeight(
                0,
                SimpleDdgiGuidingReference.UniformSpherePdf,
                4,
                mixturePdf), Is.Zero);
        });
    }

    [Test]
    public void IndependentRunConfidence_AcceptsNoiseAroundTheReference()
    {
        double[] estimates =
        [
            3.08d, 2.91d, 3.03d, 2.96d, 3.01d, 2.98d, 3.05d, 2.95d
        ];

        SimpleDdgiGuidingEstimatorConfidenceResult result =
            SimpleDdgiGuidingEstimatorConfidence.Evaluate(
                estimates,
                referenceValue: 3.0d,
                absoluteTolerance: 0.001d);

        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.True);
            Assert.That(result.Passed, Is.True, result.Reason);
            Assert.That(result.IndependentRunCount, Is.EqualTo(estimates.Length));
            Assert.That(result.Mean, Is.EqualTo(2.99625d).Within(1.0e-12d));
            Assert.That(result.ConfidenceHalfWidth, Is.GreaterThan(0.0d));
        });
    }

    [Test]
    public void IndependentRunConfidence_RejectsPersistentEstimatorBias()
    {
        double[] biased = [1.20d, 1.21d, 1.19d, 1.205d, 1.195d];

        SimpleDdgiGuidingEstimatorConfidenceResult result =
            SimpleDdgiGuidingEstimatorConfidence.Evaluate(
                biased,
                referenceValue: 1.0d,
                absoluteTolerance: 0.001d);

        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.True);
            Assert.That(result.Passed, Is.False);
            Assert.That(result.BiasStandardErrors,
                Is.GreaterThan(
                    SimpleDdgiGuidingEstimatorConfidence
                        .DefaultMaximumBiasStandardErrors));
            Assert.That(result.Reason,
                Is.EqualTo("guiding-estimator-persistent-bias-detected"));
        });
    }

    [Test]
    public void IndependentRunConfidence_FailsClosedForTooFewOrNonFiniteRuns()
    {
        SimpleDdgiGuidingEstimatorConfidenceResult tooFew =
            SimpleDdgiGuidingEstimatorConfidence.Evaluate(
                new[] { 1.0d, 1.0d },
                referenceValue: 1.0d,
                absoluteTolerance: 0.0d);
        SimpleDdgiGuidingEstimatorConfidenceResult nonFinite =
            SimpleDdgiGuidingEstimatorConfidence.Evaluate(
                new[] { 1.0d, double.NaN, 1.0d },
                referenceValue: 1.0d,
                absoluteTolerance: 0.0d);

        Assert.Multiple(() =>
        {
            Assert.That(tooFew.Valid, Is.False);
            Assert.That(nonFinite.Valid, Is.False);
            Assert.That(nonFinite.Reason,
                Is.EqualTo("guiding-estimator-confidence-non-finite-run"));
        });
    }
}
