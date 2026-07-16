using NUnit.Framework;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiLayoutCompilerTests
{
    [TestCase(DdgiQualityTier.DdgiLow, 4_096)]
    [TestCase(DdgiQualityTier.DdgiMedium, 8_192)]
    [TestCase(DdgiQualityTier.DdgiHigh, 16_384)]
    [TestCase(DdgiQualityTier.DdgiUltra, 32_768)]
    public void ResolvedTierBudget_UsesTheSingleAuthoritativeProbeCap(DdgiQualityTier tier, int expectedProbeBudget)
    {
        var settings = new GlobalIlluminationSettings { DdgiQualityTier = tier };

        SimpleDdgiLayoutBudget budget = SimpleDdgiLayoutBudget.Resolve(settings);

        Assert.That(budget.ProbeBudget, Is.EqualTo(expectedProbeBudget));
        Assert.That(budget.PersistentMemoryBudgetBytes, Is.EqualTo(settings.DdgiAtlasMemoryBudgetBytes));
    }

    [Test]
    public void Compile_PreservesPriorityOrderAndMakesEveryRejectedVolumeExplicit()
    {
        const int heroProbes = 60;
        const int ringProbes = 60;
        ulong memoryBudget = SimpleDdgiLayoutCompiler.EstimatePersistentBytes(heroProbes, sampledAtlasRequested: false);
        var budget = new SimpleDdgiLayoutBudget(DdgiQualityTier.DdgiHigh, heroProbes, memoryBudget, 2);
        SimpleDdgiLayoutVolumeRequest[] requests =
        [
            new("upper-facade", 1, true, SimpleDdgiVolumePurpose.ReceiverHero, 100, 1.0f, heroProbes),
            new("near-ring", 10_000, false, SimpleDdgiVolumePurpose.TransitionSupport, int.MinValue, 1.0f, ringProbes)
        ];

        SimpleDdgiLayoutReport report = SimpleDdgiLayoutCompiler.Compile(
            requests,
            budget,
            sampledAtlasRequested: false,
            SimpleDdgiLayoutAdmissionMode.Reject);

        Assert.Multiple(() =>
        {
            Assert.That(report.RequestedProbeCount, Is.EqualTo(heroProbes + ringProbes));
            Assert.That(report.AcceptedProbeCount, Is.EqualTo(heroProbes));
            Assert.That(report.Volumes[0].Decision, Is.EqualTo(SimpleDdgiLayoutDecision.Accepted));
            Assert.That(report.Volumes[1].Decision, Is.EqualTo(SimpleDdgiLayoutDecision.RejectedBudget));
            Assert.That(report.Volumes[1].Reason, Is.EqualTo("probe-and-memory-budget"));
            Assert.That(report.WasDegraded, Is.True);
            Assert.That(report.AcceptedSourceOrdinals, Is.EquivalentTo(new[] { 1 }));
        });
    }

    [Test]
    public void Compile_ReservesTheOptionalSampledAtlasBeforeAdmission()
    {
        const int probes = 100;
        ulong canonicalBudget = SimpleDdgiLayoutCompiler.EstimatePersistentBytes(probes, sampledAtlasRequested: false);
        var budget = new SimpleDdgiLayoutBudget(DdgiQualityTier.DdgiHigh, probes, canonicalBudget, 1);
        SimpleDdgiLayoutVolumeRequest[] requests =
        [new("hero", 1, true, SimpleDdgiVolumePurpose.ReceiverHero, 1, 1.0f, probes)];

        SimpleDdgiLayoutReport canonical = SimpleDdgiLayoutCompiler.Compile(
            requests, budget, sampledAtlasRequested: false, SimpleDdgiLayoutAdmissionMode.Reject);
        SimpleDdgiLayoutReport mirrored = SimpleDdgiLayoutCompiler.Compile(
            requests, budget, sampledAtlasRequested: true, SimpleDdgiLayoutAdmissionMode.Reject);

        Assert.Multiple(() =>
        {
            Assert.That(canonical.AcceptedProbeCount, Is.EqualTo(probes));
            Assert.That(mirrored.AcceptedProbeCount, Is.Zero);
            Assert.That(mirrored.Volumes[0].Reason, Is.EqualTo("persistent-memory-budget"));
            Assert.That(mirrored.RequestedPersistentBytes, Is.GreaterThan(canonical.RequestedPersistentBytes));
        });
    }
}
