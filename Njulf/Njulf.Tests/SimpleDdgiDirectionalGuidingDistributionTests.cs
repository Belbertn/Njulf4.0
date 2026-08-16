using System.Numerics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiDirectionalGuidingDistributionTests
{
    [TestCase(4, 16, 21)]
    [TestCase(8, 64, 85)]
    [TestCase(16, 256, 341)]
    public void EqualAreaConfigurations_HaveExpectedLeafAndHierarchyCounts(
        int resolution,
        int expectedLeafCount,
        int expectedWeightCount)
    {
        var configuration = new SimpleDdgiGuidingDistributionConfiguration(resolution);

        Assert.Multiple(() =>
        {
            Assert.That(configuration.LeafCount, Is.EqualTo(expectedLeafCount));
            Assert.That(configuration.HierarchyWeightCount,
                Is.EqualTo(expectedWeightCount));
            Assert.That(configuration.LeafSolidAngle * expectedLeafCount,
                Is.EqualTo(4.0d * Math.PI).Within(1.0e-12d));
        });
    }

    [Test]
    public void EqualAreaDomain_IsPeriodicAtTheSeamAndUniformPdfAtThePoles()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.EightByEight;
        SimpleDdgiGuidingQuantizedHierarchy hierarchy =
            SimpleDdgiGuidingQuantizedHierarchy.CreateUniform(configuration);
        Vector3 justBeforeSeam = new(
            (float)Math.Cos(-1.0e-6d),
            (float)Math.Sin(-1.0e-6d),
            0.0f);
        Vector3 justAfterSeam = new(
            (float)Math.Cos(1.0e-6d),
            (float)Math.Sin(1.0e-6d),
            0.0f);

        Assert.That(SimpleDdgiGuidingQuantizedHierarchy.TryDirectionToSquare(
            justBeforeSeam, out double beforeU, out _), Is.True);
        Assert.That(SimpleDdgiGuidingQuantizedHierarchy.TryDirectionToSquare(
            justAfterSeam, out double afterU, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(beforeU, Is.GreaterThan(0.99999d));
            Assert.That(afterU, Is.LessThan(0.00001d));
            Assert.That(hierarchy.EvaluateGuidedPdf(Vector3.UnitZ),
                Is.EqualTo(SimpleDdgiGuidingReference.UniformSpherePdf)
                    .Within(1.0e-12d));
            Assert.That(hierarchy.EvaluateGuidedPdf(-Vector3.UnitZ),
                Is.EqualTo(SimpleDdgiGuidingReference.UniformSpherePdf)
                    .Within(1.0e-12d));
        });
    }

    [Test]
    public void QuantizedSingleLobe_SamplesAndEvaluatesTheSameLeafPdf()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.EightByEight;
        int targetLeaf = configuration.GetLeafIndex(5, 6);
        double[] energy = new double[configuration.LeafCount];
        energy[targetLeaf] = 10_000.0d;
        SimpleDdgiGuidingHierarchyBuildResult built =
            SimpleDdgiGuidingQuantizedHierarchy.BuildFromLeafEnergies(
                configuration, energy);

        Assert.Multiple(() =>
        {
            Assert.That(built.UsedUniformFallback, Is.False);
            Assert.That(built.Failure,
                Is.EqualTo(SimpleDdgiGuidingHierarchyBuildFailure.None));
            Assert.That(built.Hierarchy.Validate().IsValid, Is.True);
        });

        for (int sampleIndex = 0; sampleIndex < 32; sampleIndex++)
        {
            double branch = (sampleIndex + 0.5d) / 32.0d;
            SimpleDdgiGuidingSample sample = built.Hierarchy.SampleGuided(
                branch,
                intraLeafU: 0.37d,
                intraLeafV: 0.73d);
            Assert.Multiple(() =>
            {
                Assert.That(sample.LeafIndex, Is.EqualTo(targetLeaf));
                Assert.That(sample.GuidedPdf,
                    Is.EqualTo(built.Hierarchy.EvaluateGuidedPdf(sample.Direction))
                        .Within(1.0e-12d));
            });
        }
    }

    [Test]
    public void SparseGuide_ZeroDensityLeafRetainsUniformMixtureSupport()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.FourByFour;
        int litLeaf = configuration.GetLeafIndex(1, 2);
        int darkLeaf = configuration.GetLeafIndex(3, 0);
        double[] energy = new double[configuration.LeafCount];
        energy[litLeaf] = 100.0d;
        SimpleDdgiGuidingHierarchyBuildResult built =
            SimpleDdgiGuidingQuantizedHierarchy.BuildFromLeafEnergies(
                configuration,
                energy);

        double guidedPdf = built.Hierarchy
            .EvaluateGuidedLeafProbability(darkLeaf) /
            configuration.LeafSolidAngle;
        double mixturePdf = SimpleDdgiGuidingReference.EvaluateMixturePdf(
            guidedPdf,
            requestedUniformFraction: 0.25d);

        Assert.Multiple(() =>
        {
            Assert.That(built.Hierarchy.Validate().IsValid, Is.True);
            Assert.That(guidedPdf, Is.Zero);
            Assert.That(mixturePdf, Is.EqualTo(
                0.25d * SimpleDdgiGuidingReference.UniformSpherePdf)
                .Within(1.0e-15d));
            Assert.That(mixturePdf, Is.GreaterThan(0.0d));
        });
    }

    [Test]
    public void QuantizedHierarchy_SampleFrequencyMatchesItsOwnPublishedLeafMasses()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.FourByFour;
        double[] energy = Enumerable.Range(1, configuration.LeafCount)
            .Select(value => (double)value * value)
            .ToArray();
        SimpleDdgiGuidingQuantizedHierarchy hierarchy =
            SimpleDdgiGuidingQuantizedHierarchy.BuildFromLeafEnergies(
                configuration, energy).Hierarchy;
        const int sampleCount = 32_768;
        int[] observed = new int[configuration.LeafCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            SimpleDdgiGuidingSample direction = hierarchy.SampleGuided(
                (sample + 0.5d) / sampleCount,
                0.25d,
                0.75d);
            observed[direction.LeafIndex]++;
        }

        for (int leaf = 0; leaf < observed.Length; leaf++)
        {
            double expected = hierarchy.EvaluateGuidedLeafProbability(leaf);
            double measured = observed[leaf] / (double)sampleCount;
            Assert.That(measured, Is.EqualTo(expected).Within(0.001d),
                $"leaf {leaf}");
        }
    }

    [Test]
    public void InvalidOrZeroTrainingEnergy_FailsClosedToAUniformGuide()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.FourByFour;
        double[] zero = new double[configuration.LeafCount];
        double[] invalid = new double[configuration.LeafCount];
        invalid[3] = double.NaN;

        SimpleDdgiGuidingHierarchyBuildResult zeroResult =
            SimpleDdgiGuidingQuantizedHierarchy.BuildFromLeafEnergies(
                configuration, zero);
        SimpleDdgiGuidingHierarchyBuildResult invalidResult =
            SimpleDdgiGuidingQuantizedHierarchy.BuildFromLeafEnergies(
                configuration, invalid);

        Assert.Multiple(() =>
        {
            Assert.That(zeroResult.UsedUniformFallback, Is.True);
            Assert.That(zeroResult.Failure,
                Is.EqualTo(SimpleDdgiGuidingHierarchyBuildFailure.ZeroFiniteEnergy));
            Assert.That(invalidResult.UsedUniformFallback, Is.True);
            Assert.That(invalidResult.Failure,
                Is.EqualTo(SimpleDdgiGuidingHierarchyBuildFailure.InvalidLeafEnergy));
            Assert.That(zeroResult.Hierarchy.EvaluateGuidedPdf(Vector3.UnitX),
                Is.EqualTo(SimpleDdgiGuidingReference.UniformSpherePdf)
                    .Within(1.0e-12d));
            Assert.That(invalidResult.Hierarchy.Validate().IsValid, Is.True);
        });
    }

    [Test]
    public void QuantizedHierarchyValidation_RejectsNonFiniteAndInconsistentNodes()
    {
        var configuration =
            SimpleDdgiGuidingDistributionConfiguration.EightByEight;
        SimpleDdgiGuidingQuantizedHierarchy uniform =
            SimpleDdgiGuidingQuantizedHierarchy.CreateUniform(configuration);

        Half[] nanWeights = uniform.CopyWeights();
        nanWeights[3] = Half.NaN;
        var nanHierarchy = new SimpleDdgiGuidingQuantizedHierarchy(
            configuration, nanWeights);

        Half[] rootMismatchWeights = uniform.CopyWeights();
        rootMismatchWeights[configuration.GetNodeIndex(1, 0, 0)] = (Half)0.5f;
        var rootMismatch = new SimpleDdgiGuidingQuantizedHierarchy(
            configuration, rootMismatchWeights);

        Half[] parentMismatchWeights = uniform.CopyWeights();
        parentMismatchWeights[configuration.GetNodeIndex(2, 0, 0)] = (Half)0.5f;
        var parentMismatch = new SimpleDdgiGuidingQuantizedHierarchy(
            configuration, parentMismatchWeights);

        Assert.Multiple(() =>
        {
            Assert.That(nanHierarchy.Validate().Failure,
                Is.EqualTo(SimpleDdgiGuidingHierarchyValidationFailure.NonFiniteWeight));
            Assert.That(rootMismatch.Validate().Failure,
                Is.EqualTo(SimpleDdgiGuidingHierarchyValidationFailure.InvalidRoot));
            Assert.That(parentMismatch.Validate().Failure,
                Is.EqualTo(SimpleDdgiGuidingHierarchyValidationFailure.ParentChildMismatch));
        });
    }

    [Test]
    public void StatisticalOracle_AcceptsKnownGpuStyleLeafFrequencies()
    {
        SimpleDdgiGuidingDistributionConfiguration configuration =
            SimpleDdgiGuidingDistributionConfiguration.EightByEight;
        double[] energy = new double[configuration.LeafCount];
        for (int leaf = 0; leaf < energy.Length; leaf++)
        {
            // HDR, multi-lobe, and adversarial tiny-bin content in one fixed
            // distribution. The hierarchy's quantized masses, not the source
            // FP64 energies, are the publication-time sampling authority.
            energy[leaf] = leaf switch
            {
                7 => 20_000.0d,
                18 => 3_000.0d,
                41 => 700.0d,
                _ => 1.0e-8d * (leaf + 1)
            };
        }
        SimpleDdgiGuidingQuantizedHierarchy hierarchy =
            SimpleDdgiGuidingQuantizedHierarchy.BuildFromLeafEnergies(
                configuration,
                energy).Hierarchy;
        const int sampleCount = 65_536;
        var expected = new double[configuration.LeafCount];
        var observed = new ulong[configuration.LeafCount];
        for (int leaf = 0; leaf < expected.Length; leaf++)
            expected[leaf] = hierarchy.EvaluateGuidedLeafProbability(leaf);
        for (int sample = 0; sample < sampleCount; sample++)
        {
            SimpleDdgiGuidingSample generated = hierarchy.SampleGuided(
                (sample + 0.5d) / sampleCount,
                intraLeafU: 0.375d,
                intraLeafV: 0.625d);
            observed[generated.LeafIndex]++;
        }

        SimpleDdgiGuidingGoodnessOfFitResult result =
            SimpleDdgiGuidingGoodnessOfFit.Evaluate(
                expected,
                observed,
                SimpleDdgiGuidingGoodnessOfFitPolicy.Qualification);

        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.True, result.Reason);
            Assert.That(result.Passed, Is.True, result.Reason);
            Assert.That(result.SampleCount, Is.EqualTo((ulong)sampleCount));
            Assert.That(result.PValue, Is.InRange(0.0d, 1.0d));
            Assert.That(result.TotalVariationDistance, Is.LessThan(0.001d));
        });
    }

    [Test]
    public void StatisticalOracle_RejectsAVisibleFrequencyBias()
    {
        const int categoryCount = 64;
        var expected = Enumerable.Repeat(1.0d / categoryCount, categoryCount)
            .ToArray();
        var observed = Enumerable.Repeat(1_024UL, categoryCount).ToArray();
        observed[0] += 3_000UL;
        observed[1] -= 1_000UL;
        observed[2] -= 1_000UL;
        observed[3] -= 1_000UL;

        SimpleDdgiGuidingGoodnessOfFitResult result =
            SimpleDdgiGuidingGoodnessOfFit.Evaluate(
                expected,
                observed,
                SimpleDdgiGuidingGoodnessOfFitPolicy.Qualification);

        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.True);
            Assert.That(result.Passed, Is.False);
            Assert.That(result.PValue, Is.LessThan(
                SimpleDdgiGuidingGoodnessOfFitPolicy.Qualification
                    .SignificanceLevel));
            Assert.That(result.Reason,
                Is.EqualTo("guiding-goodness-of-fit-pearson-rejected"));
        });
    }

    [Test]
    public void StatisticalOracle_FailsClosedForMalformedOrUnderpoweredCaptures()
    {
        SimpleDdgiGuidingGoodnessOfFitPolicy policy =
            SimpleDdgiGuidingGoodnessOfFitPolicy.Qualification;
        SimpleDdgiGuidingGoodnessOfFitResult nonNormalized =
            SimpleDdgiGuidingGoodnessOfFit.Evaluate(
                new[] { 0.4d, 0.4d },
                new ulong[] { 8_192UL, 8_192UL },
                policy);
        SimpleDdgiGuidingGoodnessOfFitResult underpowered =
            SimpleDdgiGuidingGoodnessOfFit.Evaluate(
                new[] { 0.5d, 0.5d },
                new ulong[] { 100UL, 100UL },
                policy);
        SimpleDdgiGuidingGoodnessOfFitResult impossibleSupport =
            SimpleDdgiGuidingGoodnessOfFit.Evaluate(
                new[] { 1.0d, 0.0d },
                new ulong[] { 16_383UL, 1UL },
                policy);

        Assert.Multiple(() =>
        {
            Assert.That(nonNormalized.Valid, Is.False);
            Assert.That(underpowered.Valid, Is.True);
            Assert.That(underpowered.Passed, Is.False);
            Assert.That(impossibleSupport.Valid, Is.True);
            Assert.That(impossibleSupport.Passed, Is.False);
            Assert.That(impossibleSupport.Reason,
                Is.EqualTo("guiding-goodness-of-fit-zero-support-observed"));
        });
    }
}
