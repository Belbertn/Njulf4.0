using System.Runtime.InteropServices;
using System.Linq;
using Njulf.Rendering;
using NUnit.Framework;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiLayoutCompilerTests
{
    [Test]
    public void MemoryPlan_ZeroProbeManagerStillChargesConcreteBindingFloor()
    {
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probeCount: 0,
            updateRequestCapacity: 0,
            rayCapacity: 128,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);

        Assert.Multiple(() =>
        {
            Assert.That(plan.ProbeCount, Is.Zero);
            Assert.That(plan.ParamsBytes, Is.EqualTo(1_744UL));
            Assert.That(plan.IrradianceAtlasBytes, Is.EqualTo(16UL));
            Assert.That(plan.VisibilityAtlasBytes, Is.EqualTo(16UL));
            Assert.That(plan.TransportIrradianceBytes, Is.EqualTo(16UL));
            Assert.That(plan.TransportSourceCacheBytes, Is.EqualTo(16UL));
            Assert.That(plan.ProbeStateBytes, Is.EqualTo(16UL));
            Assert.That(plan.UpdateQueueBytes, Is.EqualTo(16UL));
            Assert.That(plan.RelocationClassificationBytes, Is.EqualTo(16UL));
            Assert.That(plan.RayScratchBytes, Is.EqualTo(16UL));
            Assert.That(plan.ProbeStateReadbackBytes, Is.EqualTo(32UL));
            Assert.That(plan.SampledAtlasImageBytes, Is.Zero);
            Assert.That(plan.LiveBytes, Is.EqualTo(1_904UL));
        });
    }

    [Test]
    public void MemoryPlan_MatchesEveryConcreteManagerAllocationFormula()
    {
        const int probes = 1_000;
        const int updates = 200;
        const int rays = 64;
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probes,
            updates,
            rays,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiMemoryPlan.ParamsHeaderBytes,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiParams>()));
            Assert.That(
                SimpleDdgiMemoryPlan.VolumeBytes,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiVolume>()));
            Assert.That(
                SimpleDdgiMemoryPlan.RayResultBytes,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiRayResult>()));
            Assert.That(
                SimpleDdgiMemoryPlan.TransportRayCacheBytes,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiTransportRayCache>()));
            Assert.That(SimpleDdgiMemoryPlan.TransportRayCacheAbiVersion, Is.EqualTo(2u));
            Assert.That(
                SimpleDdgiMemoryPlan.ProbeStateBytesPerProbe,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>()));
            Assert.That(
                SimpleDdgiMemoryPlan.ProbeUpdateBytes,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiProbeUpdate>()));
            Assert.That(
                SimpleDdgiMemoryPlan.RelocationClassificationBytesPerProbe,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiRelocationClassification>()));

            Assert.That(plan.ParamsBytes, Is.EqualTo(208UL + 16UL * 96UL));
            Assert.That(plan.IrradianceAtlasBytes, Is.EqualTo((ulong)probes * 512UL));
            Assert.That(plan.VisibilityAtlasBytes, Is.EqualTo((ulong)probes * 2_048UL));
            Assert.That(plan.TransportIrradianceBytes, Is.EqualTo((ulong)probes * 512UL));
            Assert.That(
                plan.TransportSourceCacheBytes,
                Is.EqualTo(
                    (ulong)probes *
                    rays *
                    SimpleDdgiMemoryPlan.TransportRayCacheBytes));
            Assert.That(plan.ProbeStateBytes, Is.EqualTo((ulong)probes * 32UL));
            Assert.That(plan.UpdateQueueBytes, Is.EqualTo((ulong)updates * 32UL));
            Assert.That(
                plan.RelocationClassificationBytes,
                Is.EqualTo((ulong)probes * 48UL));
            Assert.That(
                plan.ProbeStateReadbackBytes,
                Is.EqualTo((ulong)RenderingConstants.FramesInFlight * probes * 32UL));
            Assert.That(
                plan.RayScratchBytes,
                Is.EqualTo((ulong)updates * rays * 32UL));
            Assert.That(plan.SampledAtlasProbeCapacity, Is.EqualTo(1_024));
            Assert.That(plan.SampledAtlasImageBytes, Is.EqualTo(1_024UL * 2_560UL));
            Assert.That(
                plan.LiveBytes,
                Is.EqualTo(plan.PersistentBytes + plan.WorkBytes));
        });
    }

    [TestCase(1, 256)]
    [TestCase(255, 256)]
    [TestCase(256, 256)]
    [TestCase(257, 512)]
    public void SampledAtlasCapacity_UsesTheRuntimeGrowthQuantum(
        int probes,
        int expectedCapacity)
    {
        Assert.That(
            SimpleDdgiMemoryPlan.ResolveSampledAtlasProbeCapacity(probes),
            Is.EqualTo(expectedCapacity));
    }

    [TestCase(4_096, 2_048, false, 2_048)]
    [TestCase(4_096, 2_048, true, 4_096)]
    [TestCase(3_000, 2_048, true, 3_000)]
    [TestCase(4_096, 0, true, 4_096)]
    public void UpdateCapacity_ReservesTheConfiguredDirtyResponseCeiling(
        int probes,
        int configured,
        bool dirtyBoost,
        int expected)
    {
        Assert.That(
            SimpleDdgiMemoryPlan.ResolveUpdateRequestCapacity(
                probes,
                configured,
                dirtyBoost),
            Is.EqualTo(expected));
    }

    [Test]
    public void MemoryAdmission_AcceptsExactLiveBudgetAndRejectsOneByteLess()
    {
        const int probes = 257;
        const int updates = 64;
        const int rays = 32;
        SimpleDdgiMemoryPlan expected = SimpleDdgiMemoryPlan.Create(
            probes,
            updateRequestCapacity: 128,
            rayCapacity: rays,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);
        SimpleDdgiLayoutVolumeRequest[] requests =
        [
            new(
                "boundary",
                1,
                true,
                SimpleDdgiVolumePurpose.ReceiverHero,
                1,
                1.0f,
                probes)
        ];

        SimpleDdgiLayoutReport accepted = SimpleDdgiLayoutCompiler.Compile(
            requests,
            new SimpleDdgiLayoutBudget(
                DdgiQualityTier.DdgiHigh,
                probes,
                expected.LiveBytes,
                1),
            sampledAtlasRequested: true,
            SimpleDdgiLayoutAdmissionMode.Reject,
            transportV2Enabled: true,
            transportRayCapacity: rays,
            configuredProbeUpdatesPerFrame: updates,
            lightingDirtyBoostEnabled: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);
        SimpleDdgiLayoutReport rejected = SimpleDdgiLayoutCompiler.Compile(
            requests,
            new SimpleDdgiLayoutBudget(
                DdgiQualityTier.DdgiHigh,
                probes,
                expected.LiveBytes - 1UL,
                1),
            sampledAtlasRequested: true,
            SimpleDdgiLayoutAdmissionMode.Reject,
            transportV2Enabled: true,
            transportRayCapacity: rays,
            configuredProbeUpdatesPerFrame: updates,
            lightingDirtyBoostEnabled: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.AcceptedProbeCount, Is.EqualTo(probes));
            Assert.That(accepted.AcceptedPersistentBytes, Is.EqualTo(expected.LiveBytes));
            Assert.That(accepted.AcceptedMemoryPlan, Is.EqualTo(expected));
            Assert.That(rejected.AcceptedProbeCount, Is.Zero);
            Assert.That(
                rejected.Volumes.Single().Reason,
                Is.EqualTo("persistent-memory-budget"));
        });
    }

    [Test]
    public void ConcreteTransportReservation_IsIndependentOfActiveShaderVersion()
    {
        SimpleDdgiMemoryPlan graphConcrete = SimpleDdgiMemoryPlan.Create(
            probeCount: 16,
            updateRequestCapacity: 4,
            rayCapacity: 8,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: 0);
        SimpleDdgiMemoryPlan graphWithoutTransport = SimpleDdgiMemoryPlan.Create(
            probeCount: 16,
            updateRequestCapacity: 4,
            rayCapacity: 8,
            sampledAtlasRequested: false,
            concreteTransportBuffers: false,
            readbackBufferCount: 0);

        Assert.Multiple(() =>
        {
            Assert.That(graphConcrete.TransportIrradianceBytes, Is.GreaterThan(0));
            Assert.That(graphConcrete.TransportSourceCacheBytes, Is.GreaterThan(0));
            Assert.That(graphWithoutTransport.TransportIrradianceBytes, Is.Zero);
            Assert.That(graphWithoutTransport.TransportSourceCacheBytes, Is.Zero);
            Assert.That(
                graphConcrete.LiveBytes - graphWithoutTransport.LiveBytes,
                Is.EqualTo(
                    graphConcrete.TransportIrradianceBytes +
                    graphConcrete.TransportSourceCacheBytes));
        });
    }

    [Test]
    public void StableCapacityReconciliation_UltraToLowCannotRetainAnOverBudgetPlan()
    {
        SimpleDdgiMemoryPlan ultra = SimpleDdgiMemoryPlan.Create(
            probeCount: 23_636,
            updateRequestCapacity: 6_144,
            rayCapacity: 192,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);
        SimpleDdgiMemoryPlan low = SimpleDdgiMemoryPlan.Create(
            probeCount: 2_648,
            updateRequestCapacity: 256,
            rayCapacity: 32,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);
        const ulong lowBudget = 64UL * 1024UL * 1024UL;

        ulong[] ultraCapacities =
        [
            ultra.IrradianceAtlasBytes,
            ultra.VisibilityAtlasBytes,
            ultra.TransportIrradianceBytes,
            ultra.TransportSourceCacheBytes,
            ultra.ProbeStateBytes,
            ultra.UpdateQueueBytes,
            ultra.RelocationClassificationBytes,
            ultra.RayScratchBytes
        ];
        ulong[] lowCapacities =
        [
            low.IrradianceAtlasBytes,
            low.VisibilityAtlasBytes,
            low.TransportIrradianceBytes,
            low.TransportSourceCacheBytes,
            low.ProbeStateBytes,
            low.UpdateQueueBytes,
            low.RelocationClassificationBytes,
            low.RayScratchBytes
        ];

        Assert.Multiple(() =>
        {
            Assert.That(ultra.LiveBytes, Is.GreaterThan(lowBudget));
            Assert.That(low.LiveBytes, Is.LessThanOrEqualTo(lowBudget));
            Assert.That(
                ultraCapacities.Zip(
                    lowCapacities,
                    SimpleDdgiVolumeManager.RequiresStableCapacityReallocation),
                Is.All.True);
            Assert.That(low.SampledAtlasImageBytes, Is.Zero);
            Assert.That(
                low.ProbeStateReadbackBytes,
                Is.EqualTo(
                    (ulong)RenderingConstants.FramesInFlight *
                    (ulong)low.ProbeCount *
                    (ulong)SimpleDdgiMemoryPlan.ProbeStateBytesPerProbe));
        });
    }

    [Test]
    public void ProductionQualityTierLayouts_FitTheirExistingHardMemoryCaps()
    {
        (string Name, int Probes, int Updates, int Rays, bool Sampled, ulong Budget)[] tiers =
        [
            ("low", 2_648, 256, 32, false, 64UL * 1024UL * 1024UL),
            ("medium", 6_892, 768, 64, false, 128UL * 1024UL * 1024UL),
            ("high", 17_600, 4_096, 128, true, 192UL * 1024UL * 1024UL),
            ("ultra", 23_636, 6_144, 192, true, 384UL * 1024UL * 1024UL)
        ];

        Assert.Multiple(() =>
        {
            foreach ((string name, int probes, int updates, int rays, bool sampled, ulong budget) in tiers)
            {
                SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
                    probes,
                    updates,
                    rays,
                    sampled,
                    concreteTransportBuffers: true,
                    readbackBufferCount: RenderingConstants.FramesInFlight);
                Assert.That(
                    plan.LiveBytes,
                    Is.LessThanOrEqualTo(budget),
                    $"{name} live DDGI storage must remain inside its existing hard tier cap");
            }
        });
    }

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
