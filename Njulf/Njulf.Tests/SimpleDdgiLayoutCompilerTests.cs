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
    public void SparseHighPlan_ChargesPhysicalCapacityAndClearsMinimumSavingGate()
    {
        const int nearProbes = 28 * 14 * 28;
        const int denseOuterProbes = 18 * 10 * 18 + 12 * 8 * 12;
        const int virtualProbes = nearProbes + denseOuterProbes;
        const int virtualPages = 14 * 7 * 14;
        const int physicalPages = 960;
        SimpleDdgiMemoryPlan sparse = SimpleDdgiMemoryPlan.Create(
            probeCount: virtualProbes,
            updateRequestCapacity: 2_048,
            rayCapacity: 128,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            residentPrivateTargets: true,
            schedulerMode: SimpleDdgiSchedulerMode.GpuResident,
            schedulerActiveVolumeCount: 3,
            residencyMode: SimpleDdgiProbeResidencyMode.SparseNearRing,
            densePayloadProbeCount: denseOuterProbes,
            sparseVirtualProbeCount: nearProbes,
            sparseVirtualPageCount: virtualPages,
            sparsePhysicalPageCapacity: physicalPages,
            maximumPageAdmissionsPerFrame: 64);
        SimpleDdgiMemoryPlan dense = SimpleDdgiMemoryPlan.Create(
            virtualProbes,
            2_048,
            128,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            residentPrivateTargets: true,
            schedulerMode: SimpleDdgiSchedulerMode.GpuResident,
            schedulerActiveVolumeCount: 3);

        Assert.Multiple(() =>
        {
            // Production fixture with Compact-28 cache rays, 20-byte
            // direction-free scratch, the scheduler's 64-byte private
            // visible-page cohort reservation extension, bounded eligible-work
            // liveness evidence, residual generation/deadline state, and B1's
            // fixed 16-byte double-banked receiver-contribution record for
            // every virtual probe (15,368 * 16 = 245,888 bytes), the 256-byte
            // params-header ABI, the 192-byte per-volume scroll-transaction
            // scheduler policy, the 512-byte fail-closed counter/control arena,
            // and the current transport-audit summary.
            Assert.That(dense.LiveBytes, Is.EqualTo(143_889_928UL));
            Assert.That(sparse.LiveBytes, Is.EqualTo(115_117_336UL));
            Assert.That(dense.LiveBytes - sparse.LiveBytes,
                Is.EqualTo(28_772_592UL));
            Assert.That(sparse.VirtualProbeCount, Is.EqualTo(15_368));
            Assert.That(sparse.DensePayloadProbeCount, Is.EqualTo(4_392));
            Assert.That(sparse.SparseVirtualPageCount, Is.EqualTo(1_372));
            Assert.That(sparse.PhysicalProbeCapacity,
                Is.EqualTo(denseOuterProbes + physicalPages * 8));
            Assert.That(sparse.SparsePagePaddingProbeCount, Is.Zero);
            Assert.That(sparse.SampledAtlasPhysicalProbeCapacity, Is.EqualTo(12_288));
            Assert.That(sparse.SampledAtlasPaddingProbeCount, Is.EqualTo(216));
            Assert.That(sparse.SampledAtlasPaddingBytes,
                Is.EqualTo(216UL * (800UL + 1_296UL)));
            Assert.That(sparse.ResidencyArenaBytes,
                Is.LessThanOrEqualTo(SimpleDdgiProbePageLayout.CurrentProfileOverheadGateBytes));
            Assert.That(sparse.ResidencyArenaBytes, Is.EqualTo(139_024UL));
            Assert.That(dense.LiveBytes - sparse.LiveBytes,
                Is.GreaterThanOrEqualTo(16UL * 1024UL * 1024UL));
        });
    }

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
            Assert.That(plan.ParamsBytes, Is.EqualTo(
                SimpleDdgiMemoryPlan.ParamsHeaderBytes +
                (ulong)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                (SimpleDdgiMemoryPlan.VolumeBytes +
                    SimpleDdgiMemoryPlan.VolumePagingBytes)));
            Assert.That(plan.IrradianceAtlasBytes, Is.EqualTo(16UL));
            Assert.That(plan.VisibilityAtlasBytes, Is.EqualTo(16UL));
            Assert.That(plan.TransportIrradianceBytes, Is.EqualTo(16UL));
            Assert.That(plan.TransportSourceCacheBytes, Is.EqualTo(16UL));
            Assert.That(plan.ProbeStateBytes, Is.EqualTo(16UL));
            Assert.That(plan.ReceiverProbeBytes, Is.EqualTo(16UL));
            Assert.That(plan.UpdateQueueBytes, Is.EqualTo(16UL));
            Assert.That(plan.RelocationClassificationBytes, Is.EqualTo(16UL));
            Assert.That(plan.RayScratchBytes, Is.EqualTo(16UL));
            Assert.That(plan.ProbeStateReadbackBytes, Is.EqualTo(32UL));
            Assert.That(plan.SampledAtlasImageBytes, Is.Zero);
            ulong descriptorSafeBufferFloors = 9UL *
                SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes;
            Assert.That(
                plan.LiveBytes,
                Is.EqualTo(
                    plan.ParamsBytes +
                    descriptorSafeBufferFloors +
                    plan.ProbeStateReadbackBytes));
            Assert.That(plan.LiveBytes, Is.EqualTo(2_736UL));
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
            Assert.That(
                SimpleDdgiMemoryPlan.TransportRayCacheAbiVersion,
                Is.EqualTo((uint)SimpleDdgiStorageAbiVersion.Packed));
            Assert.That(SimpleDdgiMemoryPlan.TransportRayCacheAbiVersion, Is.EqualTo(8u));
            Assert.That(
                SimpleDdgiMemoryPlan.ProbeStateBytesPerProbe,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>()));
            Assert.That(
                SimpleDdgiMemoryPlan.ReceiverProbeBytesPerProbe,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiReceiverProbe>()));
            Assert.That(
                SimpleDdgiMemoryPlan.ProbeUpdateBytes,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiProbeUpdate>()));
            Assert.That(
                SimpleDdgiMemoryPlan.RelocationClassificationBytesPerProbe,
                Is.EqualTo((ulong)Marshal.SizeOf<GPUSimpleDdgiRelocationClassification>()));
            Assert.That(SimpleDdgiMemoryPlan.ProbeReadbackBytesPerProbe, Is.EqualTo(80UL));

            Assert.That(plan.ParamsBytes, Is.EqualTo(
                SimpleDdgiMemoryPlan.ParamsHeaderBytes +
                (ulong)GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
                (SimpleDdgiMemoryPlan.VolumeBytes +
                    SimpleDdgiMemoryPlan.VolumePagingBytes)));
            Assert.That(plan.IrradianceAtlasBytes, Is.EqualTo((ulong)probes * 512UL));
            Assert.That(plan.VisibilityAtlasBytes, Is.EqualTo((ulong)probes * 1_024UL));
            Assert.That(plan.TransportIrradianceBytes, Is.EqualTo((ulong)probes * 512UL));
            Assert.That(
                plan.TransportSourceCacheBytes,
                Is.EqualTo(
                    (ulong)probes *
                    rays *
                    SimpleDdgiMemoryPlan.Compact28TransportRayCacheBytes));
            Assert.That(plan.TransportSourceCacheLegacyBytes, Is.Zero);
            Assert.That(plan.TransportSourceCacheCompact28Bytes,
                Is.EqualTo(plan.TransportSourceCacheBytes));
            Assert.That(plan.ProbeStateBytes, Is.EqualTo((ulong)probes * 32UL));
            Assert.That(plan.ReceiverProbeBytes, Is.EqualTo((ulong)probes * 16UL));
            Assert.That(plan.UpdateQueueBytes,
                Is.EqualTo((ulong)updates * SimpleDdgiMemoryPlan.ProbeUpdateBytes));
            Assert.That(
                plan.RelocationClassificationBytes,
                Is.EqualTo((ulong)probes * 48UL));
            Assert.That(
                plan.ProbeStateReadbackBytes,
                Is.EqualTo((ulong)RenderingConstants.FramesInFlight * probes * 80UL));
            Assert.That(
                plan.ProbeStateReadbackBytesPerBuffer,
                Is.EqualTo(SimpleDdgiMemoryPlan.ResolveProbeStateReadbackBufferBytes(probes)));
            Assert.That(
                plan.RayScratchBytes,
                Is.EqualTo((ulong)updates * rays * SimpleDdgiMemoryPlan.RayResultBytes));
            Assert.That(plan.RayResultStrideBytes,
                Is.EqualTo(SimpleDdgiMemoryPlan.RayResultBytes));
            Assert.That(plan.StoragePackingMode,
                Is.EqualTo(SimpleDdgiStoragePackingMode.Packed));
            Assert.That(plan.SampledAtlasCoverageMode,
                Is.EqualTo(SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant));
            Assert.That(plan.SampledAtlasProbeCapacity, Is.EqualTo(1_024));
            Assert.That(plan.SampledAtlasImageBytes, Is.EqualTo(1_024UL * 2_096UL));
            Assert.That(plan.SchedulerMode, Is.EqualTo(SimpleDdgiSchedulerMode.CpuReference));
            Assert.That(plan.SchedulerBufferBytes, Is.Zero);
            Assert.That(
                plan.LiveBytes,
                Is.EqualTo(plan.PersistentBytes + plan.WorkBytes));
        });
    }

    [Test]
    public void MemoryPlan_DirectionalGuidingReservesAuthenticatedTracePayloadTail()
    {
        const int updates = 37;
        const int rays = 128;
        SimpleDdgiMemoryPlan baseline = SimpleDdgiMemoryPlan.Create(
            probeCount: 512,
            updateRequestCapacity: updates,
            rayCapacity: rays,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: 0);
        SimpleDdgiMemoryPlan guided = SimpleDdgiMemoryPlan.Create(
            probeCount: 512,
            updateRequestCapacity: updates,
            rayCapacity: rays,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            directionalGuidingTraceStaging: true);

        ulong expectedTailBytes = checked(
            (ulong)updates * rays *
            SimpleDdgiMemoryPlan.GuidingTraceDirectionRecordBytes);
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiMemoryPlan.GuidingTraceDirectionRecordBytes,
                Is.EqualTo(32UL));
            Assert.That(guided.GuidingTraceDirectionScratchBytes,
                Is.EqualTo(expectedTailBytes));
            Assert.That(
                guided.GuidingTraceDirectionScratchOffsetWords,
                Is.EqualTo(baseline.RayScratchBytes / sizeof(uint)));
            Assert.That(guided.RayScratchBytes,
                Is.EqualTo(checked(baseline.RayScratchBytes +
                    expectedTailBytes)));
            Assert.That(baseline.GuidingTraceDirectionScratchBytes, Is.Zero);
        });
    }

    [Test]
    public void MemoryPlan_ReportsSparsePageAndSampledAtlasPadding()
    {
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probeCount: 20,
            updateRequestCapacity: 4,
            rayCapacity: 8,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            residencyMode: SimpleDdgiProbeResidencyMode.SparseNearRing,
            densePayloadProbeCount: 5,
            sparseVirtualProbeCount: 15,
            sparseVirtualPageCount: 2,
            sparsePhysicalPageCapacity: 2,
            maximumPageAdmissionsPerFrame: 1);

        Assert.Multiple(() =>
        {
            Assert.That(plan.PhysicalProbeCapacity, Is.EqualTo(21));
            Assert.That(plan.SparsePagePaddingProbeCount, Is.EqualTo(1));
            Assert.That(plan.SampledAtlasPhysicalProbeCapacity, Is.EqualTo(256));
            Assert.That(plan.SampledAtlasPaddingProbeCount, Is.EqualTo(235));
            Assert.That(plan.SampledAtlasPaddingBytes,
                Is.EqualTo(235UL * (800UL + 1_296UL)));
        });
    }

    [Test]
    public void MemoryPlan_ChargesExactGpuSchedulerArenaAndReadbackRing()
    {
        const int probes = 15_368;
        const int updates = 2_048;
        const int volumes = 3;
        SimpleDdgiGpuSchedulerLayout schedulerLayout =
            SimpleDdgiGpuSchedulerLayout.Create(
                probes,
                updates,
                volumes,
                validationEnabled: true);
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probes,
            updates,
            rayCapacity: 128,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            residentPrivateTargets: true,
            schedulerMode: SimpleDdgiSchedulerMode.GpuResident,
            schedulerActiveVolumeCount: volumes,
            schedulerValidationEnabled: true);
        ulong expectedFeedbackBytes = checked(
            (ulong)RenderingConstants.FramesInFlight *
            SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes);
        ulong expectedAuditBytes = checked(
            (ulong)RenderingConstants.FramesInFlight *
            (ulong)Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>());

        Assert.Multiple(() =>
        {
            Assert.That(plan.SchedulerMode,
                Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
            Assert.That(plan.SchedulerActiveLaneCount,
                Is.EqualTo(schedulerLayout.ActiveLaneCount));
            Assert.That(plan.SchedulerArenaBytes,
                Is.EqualTo(schedulerLayout.TotalBytes));
            Assert.That(plan.SchedulerFeedbackReadbackBytes,
                Is.EqualTo(expectedFeedbackBytes));
            Assert.That(plan.SchedulerAuditReadbackBytes,
                Is.EqualTo(expectedAuditBytes));
            Assert.That(plan.SchedulerValidationReadbackBytes,
                Is.EqualTo(expectedFeedbackBytes));
            Assert.That(plan.SchedulerBufferBytes,
                Is.EqualTo(
                    schedulerLayout.TotalBytes +
                    expectedFeedbackBytes +
                    expectedAuditBytes));
        });
    }

    [TestCase(0, 16UL)]
    [TestCase(1_000, 80_000UL)]
    [TestCase(15_368, 688_384UL)]
    [TestCase(32_768, 1_245_184UL)]
    public void ProbeStateReadbackCapacity_UsesOneBoundedPerFrameContract(
        int probeCount,
        ulong expectedBytes)
    {
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probeCount,
            updateRequestCapacity: Math.Min(probeCount, 2_048),
            rayCapacity: 128,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiMemoryPlan.ResolveProbeStateReadbackBufferBytes(probeCount),
                Is.EqualTo(expectedBytes));
            Assert.That(plan.ProbeStateReadbackBytesPerBuffer, Is.EqualTo(expectedBytes));
            Assert.That(
                plan.ProbeStateReadbackBytes,
                Is.EqualTo(expectedBytes * (ulong)RenderingConstants.FramesInFlight));
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
        // Canonical admission has its own hard boundary. Optional image bytes
        // are selected only after this accepted volume set is frozen.
        SimpleDdgiMemoryPlan expected = SimpleDdgiMemoryPlan.Create(
            probes,
            updateRequestCapacity: 128,
            rayCapacity: rays,
            sampledAtlasRequested: false,
            concreteTransportBuffers: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);
        ulong expectedVolumePathBytes = checked(
            (ulong)probes * rays *
            (ulong)SimpleDdgiStorageLayoutCompiler.VolumePathSidecarWords *
            sizeof(uint));
        ulong expectedLiveBytes = checked(
            expected.LiveBytes + expectedVolumePathBytes);
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
                expectedLiveBytes,
                1),
            sampledAtlasRequested: false,
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
                expectedLiveBytes - 1UL,
                1),
            sampledAtlasRequested: false,
            SimpleDdgiLayoutAdmissionMode.Reject,
            transportV2Enabled: true,
            transportRayCapacity: rays,
            configuredProbeUpdatesPerFrame: updates,
            lightingDirtyBoostEnabled: true,
            readbackBufferCount: RenderingConstants.FramesInFlight);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.AcceptedProbeCount, Is.EqualTo(probes));
            Assert.That(accepted.AcceptedPersistentBytes, Is.EqualTo(expectedLiveBytes));
            Assert.That(accepted.AcceptedMemoryPlan.LiveBytes, Is.EqualTo(expectedLiveBytes));
            Assert.That(
                accepted.AcceptedMemoryPlan
                    .TransportSourceCacheVolumePathSidecarBytes,
                Is.EqualTo(expectedVolumePathBytes));
            Assert.That(accepted.AcceptedMemoryPlan.SampledAtlasImageBytes, Is.Zero);
            Assert.That(rejected.AcceptedProbeCount, Is.Zero);
            Assert.That(
                rejected.Volumes.Single().Reason,
                Is.EqualTo("persistent-memory-budget"));
        });
    }

    [Test]
    public void ConcreteTransportReservation_UsesOnlyGraphSafePlaceholdersWhenV2IsDisabled()
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
            Assert.That(
                graphWithoutTransport.TransportIrradianceBytes,
                Is.EqualTo(SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes));
            Assert.That(
                graphWithoutTransport.TransportSourceCacheBytes,
                Is.EqualTo(SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes));
            Assert.That(
                graphWithoutTransport.TransportSourceCacheLegacyBytes +
                graphWithoutTransport.TransportSourceCacheCompact28Bytes +
                graphWithoutTransport.TransportSourceCacheCompact24Bytes +
                graphWithoutTransport.TransportSourceCacheAlignmentBytes,
                Is.EqualTo(graphWithoutTransport.TransportSourceCacheBytes));
            Assert.That(
                graphWithoutTransport.TransportSourceCacheLegacyRayCount +
                graphWithoutTransport.TransportSourceCacheCompact28RayCount +
                graphWithoutTransport.TransportSourceCacheCompact24RayCount,
                Is.Zero);
            Assert.That(
                graphConcrete.LiveBytes - graphWithoutTransport.LiveBytes,
                Is.EqualTo(
                    graphConcrete.TransportIrradianceBytes +
                    graphConcrete.TransportSourceCacheBytes -
                    SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes * 2UL));
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
            ultra.ReceiverProbeBytes,
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
            low.ReceiverProbeBytes,
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
                    ((ulong)low.ProbeCount *
                        SimpleDdgiMemoryPlan.ProbeStateBytesPerProbe +
                    (ulong)Math.Min(
                        low.ProbeCount,
                        SimpleDdgiMemoryPlan.ClassificationReadbackProbeCapacity) *
                        SimpleDdgiMemoryPlan.RelocationClassificationBytesPerProbe)));
        });
    }

    [Test]
    public void ProductionQualityTierLayouts_FitTheirExistingHardMemoryCaps()
    {
        (string Name, int Probes, int Updates, int Rays, bool Sampled, ulong Budget)[] tiers =
        [
            ("low", 2_648, 256, 32, false, 64UL * 1024UL * 1024UL),
            ("medium", 6_892, 768, 64, false, 128UL * 1024UL * 1024UL),
            ("high", 17_600, 4_096, 128, true, 288UL * 1024UL * 1024UL),
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

    [Test]
    public void ResidentHighLayout_PreservesCanonicalFarRingWhenOptionalMirrorDoesNotFit()
    {
        const int nearProbes = 10_976;
        const int midProbes = 3_240;
        const int farProbes = 1_152;
        const int requestedProbes = nearProbes + midProbes + farProbes;
        const int updates = 2_048;
        const int rays = 128;
        const ulong budgetBytes = 192UL * 1024UL * 1024UL;

        SimpleDdgiLayoutVolumeRequest[] requests =
        [
            new("near", 10_000, false, SimpleDdgiVolumePurpose.ReceiverHero, 0, 1.0f, nearProbes),
            new("mid", 10_001, false, SimpleDdgiVolumePurpose.TransitionSupport, 0, 3.0f, midProbes),
            new("far", 10_002, false, SimpleDdgiVolumePurpose.TransitionSupport, 0, 9.0f, farProbes)
        ];
        var broadBudget = new SimpleDdgiLayoutBudget(
            DdgiQualityTier.DdgiHigh,
            16_384,
            budgetBytes,
            GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);

        SimpleDdgiLayoutReport canonicalOnly = SimpleDdgiLayoutCompiler.Compile(
            requests,
            broadBudget,
            sampledAtlasRequested: false,
            SimpleDdgiLayoutAdmissionMode.Reject,
            transportV2Enabled: true,
            transportRayCapacity: rays,
            configuredProbeUpdatesPerFrame: updates,
            lightingDirtyBoostEnabled: true,
            readbackBufferCount: 0,
            residentPrivateTargets: true);
        var tightCanonicalBudget = broadBudget with
        {
            PersistentMemoryBudgetBytes = canonicalOnly.AcceptedMemoryPlan.LiveBytes
        };
        SimpleDdgiLayoutReport sampled = SimpleDdgiLayoutCompiler.Compile(
            requests,
            tightCanonicalBudget,
            sampledAtlasRequested: true,
            SimpleDdgiLayoutAdmissionMode.Reject,
            transportV2Enabled: true,
            transportRayCapacity: rays,
            configuredProbeUpdatesPerFrame: updates,
            lightingDirtyBoostEnabled: true,
            readbackBufferCount: 0,
            residentPrivateTargets: true);

        Assert.Multiple(() =>
        {
            Assert.That(sampled.AcceptedProbeCount, Is.EqualTo(requestedProbes));
            Assert.That(sampled.WasDegraded, Is.False);
            Assert.That(canonicalOnly.AcceptedProbeCount, Is.EqualTo(requestedProbes));
            Assert.That(canonicalOnly.WasDegraded, Is.False);
            Assert.That(canonicalOnly.AcceptedMemoryPlan.LiveBytes, Is.LessThanOrEqualTo(budgetBytes));
            Assert.That(sampled.AcceptedSourceOrdinals,
                Is.EquivalentTo(canonicalOnly.AcceptedSourceOrdinals));
            Assert.That(sampled.SampledAtlasLayout.AdmittedProbeCount, Is.Zero);
            Assert.That(sampled.AcceptedMemoryPlan.SampledAtlasImageBytes, Is.Zero);
        });
    }

    [TestCase(DdgiQualityTier.DdgiLow, 4_096, 64)]
    [TestCase(DdgiQualityTier.DdgiMedium, 8_192, 128)]
    [TestCase(DdgiQualityTier.DdgiHigh, 16_384, 288)]
    [TestCase(DdgiQualityTier.DdgiUltra, 32_768, 384)]
    public void ResolvedTierBudget_UsesTheSingleAuthoritativeTierCaps(
        DdgiQualityTier tier,
        int expectedProbeBudget,
        int expectedMemoryBudgetMiB)
    {
        var settings = new GlobalIlluminationSettings();
        settings.ApplyDdgiQualityTier(tier);

        SimpleDdgiLayoutBudget budget = SimpleDdgiLayoutBudget.Resolve(settings);

        Assert.That(budget.ProbeBudget, Is.EqualTo(expectedProbeBudget));
        Assert.That(
            budget.PersistentMemoryBudgetBytes,
            Is.EqualTo((ulong)expectedMemoryBudgetMiB * 1024UL * 1024UL));
        Assert.That(
            budget.PersistentMemoryBudgetBytes,
            Is.EqualTo(settings.DdgiAtlasMemoryBudgetBytes));
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
    public void Compile_AdmitsCanonicalVolumesBeforeOptionalSampledAtlas()
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
            Assert.That(mirrored.AcceptedProbeCount, Is.EqualTo(probes));
            Assert.That(mirrored.AcceptedSourceOrdinals,
                Is.EquivalentTo(canonical.AcceptedSourceOrdinals));
            Assert.That(mirrored.SampledAtlasLayout.AdmittedProbeCount, Is.Zero);
            Assert.That(mirrored.AcceptedMemoryPlan.SampledAtlasImageBytes, Is.Zero);
            Assert.That(mirrored.RequestedPersistentBytes, Is.GreaterThan(canonical.RequestedPersistentBytes));
        });
    }

    [Test]
    public void CornellRequiredTopology_FitsAndRejectsRefinementAsOptional()
    {
        SimpleDdgiLayoutVolumeRequest[] requests =
        [
            new("cornell-authored", 1, true,
                SimpleDdgiVolumePurpose.ReceiverHero, 100, 0.75f, 880),
            new("ring-0", 10_000, false,
                SimpleDdgiVolumePurpose.TransitionSupport, 0, 0.875f, 10_976),
            new("ring-1", 10_001, false,
                SimpleDdgiVolumePurpose.TransitionSupport, 0, 3.137475f, 3_240),
            new("ring-2", 10_002, false,
                SimpleDdgiVolumePurpose.TransitionSupport, 0, 11.25f, 1_152),
            new("refinement-0", 30_000, false,
                SimpleDdgiVolumePurpose.ReceiverHero, 0, 0.4375f, 144)
            {
                AdmissionClass = SimpleDdgiLayoutAdmissionClass.Optional
            },
            new("refinement-1", 30_001, false,
                SimpleDdgiVolumePurpose.ReceiverHero, 0, 0.4375f, 144)
            {
                AdmissionClass = SimpleDdgiLayoutAdmissionClass.Optional
            }
        ];
        var budget = new SimpleDdgiLayoutBudget(
            DdgiQualityTier.DdgiHigh,
            ProbeBudget: 16_384,
            PersistentMemoryBudgetBytes: ulong.MaxValue,
            VolumeBudget: GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);

        SimpleDdgiLayoutReport report = SimpleDdgiLayoutCompiler.Compile(
            requests,
            budget,
            sampledAtlasRequested: false,
            SimpleDdgiLayoutAdmissionMode.Reject);

        Assert.Multiple(() =>
        {
            Assert.That(report.AcceptedProbeCount, Is.EqualTo(16_248));
            Assert.That(report.AcceptedSourceOrdinals,
                Is.EquivalentTo(new[] { 1, 10_000, 10_001, 10_002 }));
            Assert.That(report.WasDegraded, Is.True);
            Assert.That(report.HasRequiredRejection, Is.False);
            Assert.That(report.RequiredRejectionCount, Is.Zero);
            Assert.That(report.OptionalRejectionCount, Is.EqualTo(2));
            Assert.That(report.Volumes.Skip(4).All(static decision =>
                decision.Decision == SimpleDdgiLayoutDecision.RejectedBudget),
                Is.True);
        });
    }

    [Test]
    public void ShadowCompile_RetainsDensePayloadForSparseEligibleNearRing()
    {
        SimpleDdgiLayoutVolumeRequest[] requests =
        [
            new(
                "near-ring",
                10_000,
                false,
                SimpleDdgiVolumePurpose.TransitionSupport,
                0,
                1.0f,
                64)
            {
                GridCountX = 4,
                GridCountY = 4,
                GridCountZ = 4,
                SparseNearRingEligible = true
            },
            new(
                "outer-ring",
                10_001,
                false,
                SimpleDdgiVolumePurpose.TransitionSupport,
                -1,
                2.0f,
                32)
            {
                GridCountX = 4,
                GridCountY = 2,
                GridCountZ = 4
            }
        ];
        var budget = new SimpleDdgiLayoutBudget(
            DdgiQualityTier.DdgiHigh,
            ProbeBudget: 96,
            PersistentMemoryBudgetBytes: ulong.MaxValue,
            VolumeBudget: 2);

        SimpleDdgiLayoutReport report = SimpleDdgiLayoutCompiler.Compile(
            requests,
            budget,
            sampledAtlasRequested: false,
            SimpleDdgiLayoutAdmissionMode.Reject,
            transportV2Enabled: true,
            transportRayCapacity: 8,
            residentPrivateTargets: true,
            schedulerMode: SimpleDdgiSchedulerMode.GpuResident,
            residencyMode: SimpleDdgiProbeResidencyMode.Shadow,
            sparsePhysicalPageBudget: 4,
            sparseMinimumPhysicalPageBudget: 1,
            maximumPageAdmissionsPerFrame: 2);

        Assert.Multiple(() =>
        {
            Assert.That(report.AcceptedProbeCount, Is.EqualTo(96));
            Assert.That(report.AcceptedMemoryPlan.ResidencyMode,
                Is.EqualTo(SimpleDdgiProbeResidencyMode.Shadow));
            Assert.That(report.AcceptedMemoryPlan.SparseVirtualProbeCount,
                Is.EqualTo(64));
            Assert.That(report.AcceptedMemoryPlan.SparseVirtualPageCount,
                Is.EqualTo(8));
            Assert.That(report.AcceptedMemoryPlan.SparsePhysicalPageCapacity,
                Is.EqualTo(4));
            Assert.That(report.AcceptedMemoryPlan.DensePayloadProbeCount,
                Is.EqualTo(96));
            Assert.That(report.AcceptedMemoryPlan.PhysicalProbeCapacity,
                Is.EqualTo(96));
            Assert.That(report.AcceptedMemoryPlan.ResidencyArenaBytes,
                Is.GreaterThan(0UL));
            Assert.That(report.StorageLayout.Regions.Count, Is.EqualTo(2));
            Assert.That(report.StorageLayout.Regions[0].PhysicalFirstProbe,
                Is.Zero);
            Assert.That(report.StorageLayout.Regions[0].PhysicalProbeCount,
                Is.EqualTo(64));
            Assert.That(report.StorageLayout.Regions[1].PhysicalFirstProbe,
                Is.EqualTo(64));
            Assert.That(report.StorageLayout.Regions[1].PhysicalProbeCount,
                Is.EqualTo(32));
        });
    }

    [Test]
    public void SparseCompile_FallsBackToInternallyConsistentDensePlanWithoutAcceptedCoarserRing()
    {
        SimpleDdgiLayoutVolumeRequest[] requests =
        [
            new(
                "near-ring",
                10_000,
                false,
                SimpleDdgiVolumePurpose.TransitionSupport,
                0,
                1.0f,
                64)
            {
                GridCountX = 4,
                GridCountY = 4,
                GridCountZ = 4,
                SparseNearRingEligible = true
            },
            new(
                "authored-not-coarser",
                1,
                true,
                SimpleDdgiVolumePurpose.ReceiverHero,
                0,
                2.0f,
                32)
        ];
        var budget = new SimpleDdgiLayoutBudget(
            DdgiQualityTier.DdgiHigh,
            ProbeBudget: 64,
            PersistentMemoryBudgetBytes: ulong.MaxValue,
            VolumeBudget: 2);

        SimpleDdgiLayoutReport report = SimpleDdgiLayoutCompiler.Compile(
            requests,
            budget,
            sampledAtlasRequested: false,
            SimpleDdgiLayoutAdmissionMode.Degrade,
            transportV2Enabled: true,
            transportRayCapacity: 32,
            residentPrivateTargets: true,
            schedulerMode: SimpleDdgiSchedulerMode.GpuResident,
            residencyMode: SimpleDdgiProbeResidencyMode.SparseNearRing,
            sparsePhysicalPageBudget: 4,
            sparseMinimumPhysicalPageBudget: 1,
            maximumPageAdmissionsPerFrame: 2);

        Assert.Multiple(() =>
        {
            Assert.That(report.AcceptedMemoryPlan.ResidencyMode,
                Is.EqualTo(SimpleDdgiProbeResidencyMode.Dense));
            Assert.That(report.ResidencyFallbackReason,
                Is.EqualTo("dense-coarser-ring-not-admitted"));
            Assert.That(report.AcceptedMemoryPlan.SparseVirtualProbeCount,
                Is.Zero);
            Assert.That(report.AcceptedMemoryPlan.DensePayloadProbeCount,
                Is.EqualTo(report.AcceptedProbeCount));
        });
    }
}
