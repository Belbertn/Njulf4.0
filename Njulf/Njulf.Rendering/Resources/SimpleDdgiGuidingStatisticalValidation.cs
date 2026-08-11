using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Frozen statistical policy for an offline/optional GPU C3 sampling capture.
/// This code is intentionally outside the frame loop: it may allocate a small
/// (at most 256-category) workspace while evaluating qualification evidence.
/// </summary>
public readonly record struct SimpleDdgiGuidingGoodnessOfFitPolicy(
    ulong MinimumSampleCount,
    double MinimumExpectedCountPerPooledCategory,
    double SignificanceLevel,
    double MaximumTotalVariationDistance)
{
    public static SimpleDdgiGuidingGoodnessOfFitPolicy Qualification { get; } =
        new(
            MinimumSampleCount: 16_384UL,
            MinimumExpectedCountPerPooledCategory: 5.0,
            SignificanceLevel: 0.001,
            MaximumTotalVariationDistance: 0.05);

    public bool IsValid => MinimumSampleCount > 0UL &&
        double.IsFinite(MinimumExpectedCountPerPooledCategory) &&
        MinimumExpectedCountPerPooledCategory >= 1.0 &&
        double.IsFinite(SignificanceLevel) && SignificanceLevel > 0.0 &&
        SignificanceLevel < 0.5 &&
        double.IsFinite(MaximumTotalVariationDistance) &&
        MaximumTotalVariationDistance > 0.0 &&
        MaximumTotalVariationDistance < 1.0;
}

public readonly record struct SimpleDdgiGuidingGoodnessOfFitResult(
    bool Valid,
    bool Passed,
    ulong SampleCount,
    int SourceCategoryCount,
    int PooledCategoryCount,
    int DegreesOfFreedom,
    double PearsonChiSquare,
    double PValue,
    double TotalVariationDistance,
    double MaximumAbsoluteProbabilityError,
    string Reason)
{
    public static SimpleDdgiGuidingGoodnessOfFitResult Invalid(
        string reason) => new(
            false, false, 0UL, 0, 0, 0, 0.0, 0.0, 0.0, 0.0,
            string.IsNullOrWhiteSpace(reason)
                ? "guiding-goodness-of-fit-invalid"
                : reason.Trim());
}

/// <summary>
/// Deterministic Pearson multinomial goodness-of-fit evaluation with
/// low-expectation categories pooled before the chi-square tail probability
/// is computed.  It is suitable for CPU or fence-complete GPU leaf-frequency
/// captures and never treats a single random trial as promotion evidence.
/// </summary>
public static class SimpleDdgiGuidingGoodnessOfFit
{
    public const int MaximumCategoryCount = 256;
    private const double ProbabilitySumTolerance = 1.0e-6;
    private const double GammaEpsilon = 3.0e-14;
    private const double GammaFloor = 1.0e-300;
    private const int MaximumGammaIterations = 10_000;

    public static SimpleDdgiGuidingGoodnessOfFitResult Evaluate(
        ReadOnlySpan<double> expectedProbabilityMass,
        ReadOnlySpan<ulong> observedCounts,
        in SimpleDdgiGuidingGoodnessOfFitPolicy policy)
    {
        if (!policy.IsValid)
            return SimpleDdgiGuidingGoodnessOfFitResult.Invalid(
                "guiding-goodness-of-fit-policy-invalid");
        if (expectedProbabilityMass.Length is < 2 or > MaximumCategoryCount ||
            observedCounts.Length != expectedProbabilityMass.Length)
        {
            return SimpleDdgiGuidingGoodnessOfFitResult.Invalid(
                "guiding-goodness-of-fit-category-shape-invalid");
        }

        double probabilitySum = 0.0;
        ulong sampleCount = 0UL;
        try
        {
            for (int index = 0; index < expectedProbabilityMass.Length; index++)
            {
                double probability = expectedProbabilityMass[index];
                if (!double.IsFinite(probability) || probability < 0.0)
                {
                    return SimpleDdgiGuidingGoodnessOfFitResult.Invalid(
                        "guiding-goodness-of-fit-probability-invalid");
                }
                probabilitySum += probability;
                sampleCount = checked(sampleCount + observedCounts[index]);
            }
        }
        catch (OverflowException)
        {
            return SimpleDdgiGuidingGoodnessOfFitResult.Invalid(
                "guiding-goodness-of-fit-sample-count-overflow");
        }

        if (!double.IsFinite(probabilitySum) || probabilitySum <= 0.0 ||
            Math.Abs(probabilitySum - 1.0) > ProbabilitySumTolerance)
        {
            return SimpleDdgiGuidingGoodnessOfFitResult.Invalid(
                "guiding-goodness-of-fit-probability-mass-not-normalized");
        }
        if (sampleCount < policy.MinimumSampleCount)
        {
            return new SimpleDdgiGuidingGoodnessOfFitResult(
                true, false, sampleCount, expectedProbabilityMass.Length,
                0, 0, 0.0, 0.0, 0.0, 0.0,
                "guiding-goodness-of-fit-sample-count-insufficient");
        }

        var pooledExpected = new List<double>(expectedProbabilityMass.Length);
        var pooledObserved = new List<double>(expectedProbabilityMass.Length);
        double rareExpected = 0.0;
        double rareObserved = 0.0;
        double totalVariation = 0.0;
        double maximumAbsoluteError = 0.0;

        for (int index = 0; index < expectedProbabilityMass.Length; index++)
        {
            double normalizedExpected =
                expectedProbabilityMass[index] / probabilitySum;
            ulong observed = observedCounts[index];
            if (normalizedExpected == 0.0 && observed != 0UL)
            {
                return new SimpleDdgiGuidingGoodnessOfFitResult(
                    true, false, sampleCount, expectedProbabilityMass.Length,
                    0, 0, double.PositiveInfinity, 0.0, 1.0, 1.0,
                    "guiding-goodness-of-fit-zero-support-observed");
            }

            double measured = observed / (double)sampleCount;
            double error = Math.Abs(measured - normalizedExpected);
            totalVariation += error * 0.5;
            maximumAbsoluteError = Math.Max(maximumAbsoluteError, error);

            double expectedCount = normalizedExpected * sampleCount;
            if (expectedCount >= policy.MinimumExpectedCountPerPooledCategory)
            {
                pooledExpected.Add(expectedCount);
                pooledObserved.Add(observed);
            }
            else
            {
                rareExpected += expectedCount;
                rareObserved += observed;
            }
        }

        if (rareExpected > 0.0)
        {
            if (rareExpected >= policy.MinimumExpectedCountPerPooledCategory)
            {
                pooledExpected.Add(rareExpected);
                pooledObserved.Add(rareObserved);
            }
            else if (pooledExpected.Count > 0)
            {
                int smallest = 0;
                for (int index = 1; index < pooledExpected.Count; index++)
                {
                    if (pooledExpected[index] < pooledExpected[smallest])
                        smallest = index;
                }
                pooledExpected[smallest] += rareExpected;
                pooledObserved[smallest] += rareObserved;
            }
        }

        if (pooledExpected.Count < 2)
        {
            return new SimpleDdgiGuidingGoodnessOfFitResult(
                true, false, sampleCount, expectedProbabilityMass.Length,
                pooledExpected.Count, 0, 0.0, 0.0, totalVariation,
                maximumAbsoluteError,
                "guiding-goodness-of-fit-degrees-of-freedom-insufficient");
        }

        double statistic = 0.0;
        for (int index = 0; index < pooledExpected.Count; index++)
        {
            double delta = pooledObserved[index] - pooledExpected[index];
            statistic += delta * delta / pooledExpected[index];
        }
        int degreesOfFreedom = pooledExpected.Count - 1;
        double pValue = RegularizedGammaQ(
            degreesOfFreedom * 0.5,
            statistic * 0.5);
        if (!double.IsFinite(statistic) || !double.IsFinite(pValue) ||
            pValue < 0.0 || pValue > 1.0)
        {
            return SimpleDdgiGuidingGoodnessOfFitResult.Invalid(
                "guiding-goodness-of-fit-numerical-failure");
        }

        bool passed = pValue >= policy.SignificanceLevel &&
            totalVariation <= policy.MaximumTotalVariationDistance;
        string reason = passed
            ? "guiding-goodness-of-fit-passed"
            : pValue < policy.SignificanceLevel
                ? "guiding-goodness-of-fit-pearson-rejected"
                : "guiding-goodness-of-fit-total-variation-rejected";
        return new SimpleDdgiGuidingGoodnessOfFitResult(
            true,
            passed,
            sampleCount,
            expectedProbabilityMass.Length,
            pooledExpected.Count,
            degreesOfFreedom,
            statistic,
            pValue,
            totalVariation,
            maximumAbsoluteError,
            reason);
    }

    /// <summary>Upper-tail probability Q(a,x) used by the chi-square test.</summary>
    private static double RegularizedGammaQ(double a, double x)
    {
        if (!(a > 0.0) || x < 0.0 || !double.IsFinite(a) ||
            !double.IsFinite(x))
        {
            return double.NaN;
        }
        if (x == 0.0)
            return 1.0;
        return x < a + 1.0
            ? Math.Clamp(1.0 - GammaSeriesP(a, x), 0.0, 1.0)
            : Math.Clamp(GammaContinuedFractionQ(a, x), 0.0, 1.0);
    }

    private static double GammaSeriesP(double a, double x)
    {
        double sum = 1.0 / a;
        double term = sum;
        double denominator = a;
        for (int iteration = 1; iteration <= MaximumGammaIterations; iteration++)
        {
            denominator += 1.0;
            term *= x / denominator;
            sum += term;
            if (Math.Abs(term) <= Math.Abs(sum) * GammaEpsilon)
            {
                return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
            }
        }
        return double.NaN;
    }

    private static double GammaContinuedFractionQ(double a, double x)
    {
        double b = x + 1.0 - a;
        double c = 1.0 / GammaFloor;
        double d = 1.0 / Math.Max(Math.Abs(b), GammaFloor) * Math.Sign(b == 0.0 ? 1.0 : b);
        double result = d;
        for (int iteration = 1; iteration <= MaximumGammaIterations; iteration++)
        {
            double an = -iteration * (iteration - a);
            b += 2.0;
            d = an * d + b;
            if (Math.Abs(d) < GammaFloor)
                d = GammaFloor;
            c = b + an / c;
            if (Math.Abs(c) < GammaFloor)
                c = GammaFloor;
            d = 1.0 / d;
            double delta = d * c;
            result *= delta;
            if (Math.Abs(delta - 1.0) <= GammaEpsilon)
            {
                return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * result;
            }
        }
        return double.NaN;
    }

    // Lanczos approximation with coefficients optimized for binary64.
    private static double LogGamma(double value)
    {
        ReadOnlySpan<double> coefficients =
        [
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7
        ];
        if (value < 0.5)
        {
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * value)) -
                LogGamma(1.0 - value);
        }

        double shifted = value - 1.0;
        double series = 0.99999999999980993;
        for (int index = 0; index < coefficients.Length; index++)
            series += coefficients[index] / (shifted + index + 1.0);
        double t = shifted + coefficients.Length - 0.5;
        return 0.5 * Math.Log(2.0 * Math.PI) +
            (shifted + 0.5) * Math.Log(t) - t + Math.Log(series);
    }
}

public readonly record struct SimpleDdgiGuidingEstimatorConfidenceResult(
    bool Valid,
    bool Passed,
    int IndependentRunCount,
    double Mean,
    double SampleVariance,
    double StandardError,
    double AbsoluteBias,
    double BiasStandardErrors,
    double ConfidenceHalfWidth,
    string Reason);

/// <summary>Independent-run confidence test for C3 estimator captures.</summary>
public static class SimpleDdgiGuidingEstimatorConfidence
{
    public const int MinimumIndependentRunCount = 3;
    public const double DefaultMaximumBiasStandardErrors = 3.2905267314919255;

    public static SimpleDdgiGuidingEstimatorConfidenceResult Evaluate(
        ReadOnlySpan<double> independentEstimates,
        double referenceValue,
        double absoluteTolerance,
        double maximumBiasStandardErrors = DefaultMaximumBiasStandardErrors)
    {
        if (independentEstimates.Length < MinimumIndependentRunCount ||
            !double.IsFinite(referenceValue) ||
            !double.IsFinite(absoluteTolerance) || absoluteTolerance < 0.0 ||
            !double.IsFinite(maximumBiasStandardErrors) ||
            maximumBiasStandardErrors <= 0.0)
        {
            return new(false, false, independentEstimates.Length, 0.0, 0.0,
                0.0, 0.0, 0.0, 0.0,
                "guiding-estimator-confidence-input-invalid");
        }

        double mean = 0.0;
        double m2 = 0.0;
        for (int index = 0; index < independentEstimates.Length; index++)
        {
            double value = independentEstimates[index];
            if (!double.IsFinite(value))
            {
                return new(false, false, independentEstimates.Length, 0.0,
                    0.0, 0.0, 0.0, 0.0, 0.0,
                    "guiding-estimator-confidence-non-finite-run");
            }
            double delta = value - mean;
            mean += delta / (index + 1.0);
            m2 += delta * (value - mean);
        }

        double variance = Math.Max(
            0.0,
            m2 / (independentEstimates.Length - 1.0));
        double standardError = Math.Sqrt(
            variance / independentEstimates.Length);
        double bias = Math.Abs(mean - referenceValue);
        double biasStandardErrors = standardError > 0.0
            ? bias / standardError
            : bias <= absoluteTolerance ? 0.0 : double.PositiveInfinity;
        double halfWidth = absoluteTolerance +
            maximumBiasStandardErrors * standardError;
        bool passed = bias <= halfWidth;
        return new(
            true,
            passed,
            independentEstimates.Length,
            mean,
            variance,
            standardError,
            bias,
            biasStandardErrors,
            halfWidth,
            passed
                ? "guiding-estimator-confidence-passed"
                : "guiding-estimator-persistent-bias-detected");
    }
}
