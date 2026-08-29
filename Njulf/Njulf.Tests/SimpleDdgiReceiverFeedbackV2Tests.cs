using System;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverFeedbackV2Tests
{
    [Test]
    public void CaptureSourceAbi_UsesDisjointProducerRangesAndFrameRing()
    {
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.AssertManagedLayout();
        var capacities = new SimpleDdgiReceiverFeedbackProducerCapacities(
            OpaqueForward: 96u,
            AlphaMaskOrFoliage: 8u,
            TransparentWeightedOit: 7u,
            Particles: 6u,
            Fog: 5u,
            ReflectionCapture: 4u,
            RefinementOrBaseFallback: 3u);

        bool compiled = SimpleDdgiReceiverFeedbackCaptureSourceAbi.TryCreateLayout(
            256u,
            capacities,
            out SimpleDdgiReceiverFeedbackCaptureSourceLayout layout,
            out string reason);
        SimpleDdgiReceiverFeedbackCaptureProducerRange fog = layout.GetProducerRange(
            SimpleDdgiReceiverFeedbackProducer.Fog);
        GPUSimpleDdgiReceiverFeedbackCaptureControl control =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.CreateControl(
                layout,
                feedbackGeneration: 17u,
                viewportGeneration: 9u,
                frameSerial: 0x0000_0002_0000_0003UL,
                requiredProducerMask:
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.KnownProducerMask);

        Assert.Multiple(() =>
        {
            Assert.That(compiled, Is.True, reason);
            Assert.That(layout.ProducerCapacities.Total, Is.EqualTo(129UL));
            Assert.That(layout.SharedOverflowBaseRecord, Is.EqualTo(129u));
            Assert.That(layout.SharedOverflowCapacity, Is.EqualTo(127u));
            Assert.That(layout.RecordsOffsetWords, Is.EqualTo(64u));
            Assert.That(layout.FrameStrideWords, Is.EqualTo(3_136u));
            Assert.That(layout.RequiredBytes, Is.EqualTo(25_344UL));
            Assert.That(layout.GetFrameControlOffsetWords(0), Is.EqualTo(64u));
            Assert.That(layout.GetFrameControlOffsetWords(1), Is.EqualTo(3_200u));
            Assert.That(layout.GetFrameRecordOffsetWords(1), Is.EqualTo(3_264u));
            Assert.That(fog.BaseRecord, Is.EqualTo(117u));
            Assert.That(fog.Capacity, Is.EqualTo(5u));
            Assert.That(control.AbiVersion,
                Is.EqualTo(SimpleDdgiReceiverFeedbackCaptureSourceAbi.Version));
            Assert.That(control.FeedbackGeneration, Is.EqualTo(17u));
            Assert.That(control.FrameSerialLow, Is.EqualTo(3u));
            Assert.That(control.FrameSerialHigh, Is.EqualTo(2u));
            Assert.That(control.Flags, Is.Zero);
            Assert.That(control.RequiredProducerMask,
                Is.EqualTo(SimpleDdgiReceiverFeedbackCaptureSourceAbi.KnownProducerMask));
            Assert.That(
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.CompletedProducerMaskWord,
                Is.EqualTo(44u));
        });
    }

    [Test]
    public void CaptureSourceAbi_RequiresANonEmptyKnownProducerSet()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.IsValidProducerMask(0u),
                Is.False);
            Assert.That(
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.IsValidProducerMask(
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.KnownProducerMask),
                Is.True);
            Assert.That(
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.IsValidProducerMask(1u << 7),
                Is.False);
            Assert.That(
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.Version,
                Is.EqualTo(0xB101_2005u));
            Assert.That(
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.SurfaceTileScale,
                Is.EqualTo(12u));
        });
    }

    [Test]
    public void ProductionQuotaPlanner_ReservesEveryProducerAndAllFallbackTraffic()
    {
        var workload = new SimpleDdgiReceiverFeedbackProductionWorkload(
            SourceScreenTileCount: 1_024UL,
            FogWorkgroupCount: 256UL,
            MaximumParticleCount: 1_024u,
            ReflectionCaptureTileCount: 128UL,
            MaximumTransparentLayersPerTile: 4u);

        bool compiled =
            SimpleDdgiReceiverFeedbackProductionQuotaPlanner.TryCompile(
                workload,
                screenSamplingProbability: 1.0 / 16.0,
                maximumUniqueGatherOwnersPerTile: 4u,
                out SimpleDdgiReceiverFeedbackProductionQuotaPlan quotaPlan,
                out string reason);
        var quotas = new SimpleDdgiReceiverFeedbackProducerQuota[
                SimpleDdgiReceiverFeedbackProductionQuotaPlan
                    .NonOpaqueQuotaCount];
        quotaPlan.WriteNonOpaqueQuotas(quotas);

        Assert.Multiple(() =>
        {
            Assert.That(compiled, Is.True, reason);
            Assert.That(quotaPlan.SamplingPeriod, Is.EqualTo(16u));
            Assert.That(quotaPlan.ProducerCapacities.OpaqueForward,
                Is.EqualTo(256u));
            Assert.That(quotaPlan.ProducerCapacities.AlphaMaskOrFoliage,
                Is.EqualTo(256u));
            Assert.That(quotaPlan.ProducerCapacities.TransparentWeightedOit,
                Is.EqualTo(1_024u));
            Assert.That(quotaPlan.ProducerCapacities.Particles,
                Is.EqualTo(568u));
            Assert.That(quotaPlan.ProducerCapacities.Fog,
                Is.EqualTo(64u));
            Assert.That(quotaPlan.ProducerCapacities.ReflectionCapture,
                Is.EqualTo(32u));
            Assert.That(quotaPlan.OrdinaryProducerRecordCount,
                Is.EqualTo(2_200UL));
            Assert.That(
                quotaPlan.ProducerCapacities.RefinementOrBaseFallback,
                Is.EqualTo(2_200u));
            Assert.That(quotaPlan.SafetyMarginRecords, Is.EqualTo(256u));
            Assert.That(quotas.Length,
                Is.EqualTo(
                    SimpleDdgiReceiverFeedbackProductionQuotaPlan
                        .NonOpaqueQuotaCount));
            Assert.That(quotas[5].Producer,
                Is.EqualTo(SimpleDdgiReceiverFeedbackProducer
                    .RefinementOrBaseFallback));
            Assert.That(quotas[5].ReservedRecordCount, Is.EqualTo(2_200u));
        });
    }

    [Test]
    public void ProductionQuotaPlanner_RejectsUnrepresentableWorkBeforeAllocation()
    {
        bool compiled =
            SimpleDdgiReceiverFeedbackProductionQuotaPlanner.TryCompile(
                new SimpleDdgiReceiverFeedbackProductionWorkload(
                    ulong.MaxValue,
                    ulong.MaxValue,
                    uint.MaxValue,
                    ulong.MaxValue,
                    uint.MaxValue),
                1.0,
                SimpleDdgiReceiverFeedbackCaptureSourceAbi
                    .MaximumUniqueGatherOwnersPerTile,
                out _,
                out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(compiled, Is.False);
            Assert.That(reason,
                Is.EqualTo("receiver-feedback-production-quota-overflow"));
        });
    }

    [Test]
    public void CaptureSourceAbi_RejectsReservationsAndWordRangesThatCannotFit()
    {
        bool reservationsRejected =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.TryCreateLayout(
                64u,
                new SimpleDdgiReceiverFeedbackProducerCapacities(
                    65u, 0u, 0u, 0u, 0u, 0u, 0u),
                out _,
                out string reservationReason);
        bool addressRejected =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.TryCreateLayout(
                SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity,
                default,
                out _,
                out string addressReason);

        Assert.Multiple(() =>
        {
            Assert.That(reservationsRejected, Is.False);
            Assert.That(reservationReason,
                Is.EqualTo("receiver-feedback-producer-reservations-exceed-record-capacity"));
            Assert.That(addressRejected, Is.False);
            Assert.That(addressReason,
                Is.EqualTo("receiver-feedback-capture-source-u32-word-address-limit-exceeded"));
        });
    }

    [Test]
    public void V2Abi_IsNaturallyAlignedAndRejectsUnrepresentableGeneration()
    {
        SimpleDdgiReceiverFeedbackV2Abi.AssertManagedLayout();

        bool packed =
            SimpleDdgiReceiverFeedbackV2Abi.TryPackConsumerFallbackAndPageGeneration(
                SimpleDdgiReceiverFeedbackProducer.Fog,
                SimpleDdgiReceiverFeedbackFallbackRole.RefinementToBaseFallback,
                0x00ab_cdefu,
                out uint value);
        bool overflow =
            SimpleDdgiReceiverFeedbackV2Abi.TryPackConsumerFallbackAndPageGeneration(
                SimpleDdgiReceiverFeedbackProducer.Fog,
                SimpleDdgiReceiverFeedbackFallbackRole.RefinementToBaseFallback,
                0x0100_0000u,
                out _);
        bool unpublished =
            SimpleDdgiReceiverFeedbackV2Abi.TryPackConsumerFallbackAndPageGeneration(
                SimpleDdgiReceiverFeedbackProducer.Fog,
                SimpleDdgiReceiverFeedbackFallbackRole.RefinementToBaseFallback,
                0u,
                out _);

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverContributionRecordV2>(),
                Is.EqualTo(32));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiReceiverContributionRecordV2>(
                    nameof(GPUSimpleDdgiReceiverContributionRecordV2.FeedbackGeneration))
                .ToInt32(), Is.EqualTo(28));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverContributionSummaryV2>(),
                Is.EqualTo(32));
            Assert.That(packed, Is.True);
            Assert.That(SimpleDdgiReceiverFeedbackV2Abi.UnpackProducer(value),
                Is.EqualTo(SimpleDdgiReceiverFeedbackProducer.Fog));
            Assert.That(SimpleDdgiReceiverFeedbackV2Abi.UnpackFallbackRole(value),
                Is.EqualTo(SimpleDdgiReceiverFeedbackFallbackRole
                    .RefinementToBaseFallback));
            Assert.That(SimpleDdgiReceiverFeedbackV2Abi.UnpackPageGeneration(value),
                Is.EqualTo(0x00ab_cdefu));
            Assert.That(overflow, Is.False);
            Assert.That(unpublished, Is.False);
        });
    }

    [Test]
    public void ExactPlanner_UsesCalculatedDoubleBufferedCapacities()
    {
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            LayoutRequest(),
            [
                new SimpleDdgiReceiverFeedbackProducerQuota(
                    SimpleDdgiReceiverFeedbackProducer.Fog, 7),
                new SimpleDdgiReceiverFeedbackProducerQuota(
                    SimpleDdgiReceiverFeedbackProducer.Particles, 5)
            ],
            QualifiedPrerequisites());

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.RequestedMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(plan.Mode.SupportedMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(plan.Mode.AdmittedMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(plan.Mode.EffectiveMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(plan.Layout.SampledScreenTileCount, Is.EqualTo(33UL));
            Assert.That(plan.Layout.ScreenRecordCount, Is.EqualTo(132UL));
            Assert.That(plan.Layout.OtherProducerRecordCount, Is.EqualTo(12UL));
            Assert.That(plan.Layout.RecordCapacity, Is.EqualTo(256UL));
            Assert.That(plan.Layout.RecordBankBytes, Is.EqualTo(8_192UL));
            Assert.That(plan.Layout.RecordBanksBytes, Is.EqualTo(16_384UL));
            Assert.That(plan.Layout.SortScratchBytes, Is.EqualTo(18_432UL));
            Assert.That(plan.Layout.SummaryBytes, Is.EqualTo(16_352UL));
            Assert.That(plan.Layout.CaptureSource.IsValid, Is.True);
            Assert.That(plan.Layout.CaptureSource.RequiredBytes, Is.EqualTo(25_344UL));
            Assert.That(plan.Layout.CaptureSource.SharedOverflowBaseRecord,
                Is.EqualTo(144u));
            Assert.That(plan.Layout.CaptureSource.SharedOverflowCapacity,
                Is.EqualTo(112u));
            Assert.That(plan.Layout.TotalBytes, Is.EqualTo(76_512UL));
            Assert.That(plan.Layout.GpuSortAbiVersion,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuSortAbi.Version));
            Assert.That(plan.Layout.GpuSortSummaryCapacity, Is.EqualTo(100u));
            Assert.That(plan.Layout.GpuSortFallbackCapacity, Is.EqualTo(256u));
            Assert.That(plan.Layout.TryGetGpuSortLayout(out var gpuLayout, out _), Is.True);
            Assert.That(gpuLayout.RequiredTotalBytes, Is.EqualTo(51_168UL));
            Assert.That(plan.Memory.ReceiverFeedbackRecordBanks.AllocatedBytes,
                Is.EqualTo(41_728UL));
            Assert.That(plan.Memory.ReceiverFeedbackSortScratch.AllocatedBytes,
                Is.EqualTo(18_432UL));
            Assert.That(plan.Memory.ReceiverFeedbackProbeSummaries.AllocatedBytes,
                Is.EqualTo(16_352UL));
            Assert.That(plan.Memory.AllocatedBytes, Is.EqualTo(76_512UL));
        });
    }

    [Test]
    public void ExactPlanner_RejectsBeforeAllocationAndReturnsZeroEveryCategory()
    {
        SimpleDdgiReceiverFeedbackLayoutRequest request = LayoutRequest() with
        {
            IndependentMemoryBudgetBytes = 51_135UL
        };
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            request,
            Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
            QualifiedPrerequisites());

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.EffectiveMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
            Assert.That(plan.Mode.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.IndependentMemoryBudgetExceeded));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
            Assert.That(plan.Memory.Get(
                    SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks)
                .RequiredBytes, Is.Zero);
        });
    }

    [Test]
    public void ExactPlanner_RejectsPageGenerationInsteadOfTruncatingIt()
    {
        SimpleDdgiReceiverFeedbackLayoutRequest request = LayoutRequest() with
        {
            MaximumPagePublicationGeneration = 0x0100_0000u
        };
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            request,
            Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
            QualifiedPrerequisites());

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.FeedbackLayoutNotRepresentable));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void ExactPlanner_RejectsLegacyScratchMultiplierInsteadOfUnderAllocatingB1()
    {
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            LayoutRequest() with { SortScratchBytesPerRecord = 16UL },
            Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
            QualifiedPrerequisites());

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.EffectiveMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
            Assert.That(plan.Mode.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.InvalidConfiguration));
            Assert.That(plan.Mode.FallbackDetail,
                Does.Contain("legacy-sort-scratch-bytes-per-record-must-be-zero"));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void ExactPlanner_RejectsWorkgroupSizeThatDoesNotMatchTheShaderAbi()
    {
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            LayoutRequest() with { WorkgroupSize = 64u },
            Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
            QualifiedPrerequisites());

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.InvalidConfiguration));
            Assert.That(plan.Mode.FallbackDetail,
                Is.EqualTo("receiver-feedback-gpu-sort-workgroup-size-must-match-abi"));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void ExactPlanner_RejectsRecordCapacityBeyondTheShaderAddressContract()
    {
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            LayoutRequest() with
            {
                ActivePhysicalProbeCapacity = 1,
                ScreenTileCount =
                    (ulong)SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity + 1UL,
                ScreenSamplingProbability = 1.0,
                MaximumUniqueGatherOwnersPerTile = 1u,
                SafetyMarginRecords = 0u,
                IndependentMemoryBudgetBytes = ulong.MaxValue,
                RendererMemoryHeadroomBytes = ulong.MaxValue,
                MaximumStorageBufferRange = ulong.MaxValue
            },
            Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
            QualifiedPrerequisites());

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.VulkanLimitExceeded));
            Assert.That(plan.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void LegacyAndDisabledModes_DoNotAllocateV2Resources()
    {
        SimpleDdgiReceiverFeedbackPlan legacy =
            SimpleDdgiReceiverFeedbackPlanner.Compile(
                SimpleDdgiReceiverFeedbackMode.LegacyPackedReference,
                LayoutRequest(),
                Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
                QualifiedPrerequisites());
        SimpleDdgiReceiverFeedbackPlan disabled =
            SimpleDdgiReceiverFeedbackPlanner.Compile(
                SimpleDdgiReceiverFeedbackMode.Off,
                LayoutRequest(),
                Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
                QualifiedPrerequisites());

        Assert.Multiple(() =>
        {
            Assert.That(legacy.Mode.EffectiveMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.LegacyPackedReference));
            Assert.That(legacy.Memory.AllCategoriesZero, Is.True);
            Assert.That(disabled.Mode.EffectiveMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
            Assert.That(disabled.Memory.AllCategoriesZero, Is.True);
        });
    }

    [Test]
    public void AdmittedButNotResourceComplete_RetainsIntentWithoutClaimingAllocation()
    {
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            LayoutRequest(),
            Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
            QualifiedPrerequisites() with { ResourcesComplete = false });

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.RequestedMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(plan.Mode.AdmittedMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(plan.Mode.EffectiveMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
            Assert.That(plan.Mode.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.ResourceIncomplete));
            Assert.That(plan.Memory.ReceiverFeedbackRecordBanks.RequiredBytes,
                Is.GreaterThan(0UL));
            Assert.That(plan.Memory.ReceiverFeedbackRecordBanks.AllocatedBytes,
                Is.Zero);
        });
    }

    [Test]
    public void ExactCompacted_ExplicitReferenceSelectionDoesNotRequireAutoQualification()
    {
        SimpleDdgiReceiverFeedbackPlan plan = SimpleDdgiReceiverFeedbackPlanner.Compile(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            LayoutRequest(),
            Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
            QualifiedPrerequisites() with
            {
                ExactQualificationPassed = false,
                QualificationId = null
            });

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode.EffectiveMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(plan.Mode.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.None));
            Assert.That(plan.Mode.QualificationId, Is.Empty);
        });
    }

    [Test]
    public void CentralContentMemoryPlan_IncludesAdvancedSidecarWithoutHidingScratch()
    {
        SimpleDdgiReceiverFeedbackPlan feedback =
            SimpleDdgiReceiverFeedbackPlanner.Compile(
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                LayoutRequest(),
                Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
                QualifiedPrerequisites());
        SimpleDdgiContentMemoryPlan content = SimpleDdgiContentMemoryPlan.Compile(
            new GlobalIlluminationSettings(),
            localLightCount: 0,
            physicalProbeCapacity: 0,
            advancedExperimentMemory: feedback.Memory);

        Assert.Multiple(() =>
        {
            Assert.That(content.AdvancedExperimentMemory,
                Is.EqualTo(feedback.Memory));
            Assert.That(content.PersistentBytes,
                Is.EqualTo(58_080UL));
            Assert.That(content.WorkBytes, Is.EqualTo(18_432UL));
            Assert.That(content.LiveBytes, Is.EqualTo(76_512UL));
        });
    }

    [Test]
    public void CentralContentMemoryPlan_CountsRetiredAdvancedGenerationsInLiveHeadroom()
    {
        SimpleDdgiReceiverFeedbackPlan feedback =
            SimpleDdgiReceiverFeedbackPlanner.Compile(
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                LayoutRequest(),
                Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
                QualifiedPrerequisites());
        SimpleDdgiAdvancedExperimentMemoryPlan memoryWithRetirement =
            feedback.Memory with
            {
                ReceiverFeedbackRecordBanks =
                    feedback.Memory.ReceiverFeedbackRecordBanks with
                    {
                        RetiredButLiveBytes = 512UL,
                        PeakLiveBytes = 12_800UL
                    }
            };
        SimpleDdgiContentMemoryPlan content = SimpleDdgiContentMemoryPlan.Compile(
            new GlobalIlluminationSettings(),
            localLightCount: 0,
            physicalProbeCapacity: 0,
            advancedExperimentMemory: memoryWithRetirement);

        Assert.Multiple(() =>
        {
            Assert.That(content.PersistentBytes, Is.EqualTo(58_080UL));
            Assert.That(content.WorkBytes, Is.EqualTo(18_432UL));
            Assert.That(content.LiveBytes, Is.EqualTo(77_024UL));
        });
    }

    [Test]
    public void AdvancedMemoryPlans_ComposeOnlyDisjointFeatureCategories()
    {
        SimpleDdgiReceiverFeedbackPlan feedback =
            SimpleDdgiReceiverFeedbackPlanner.Compile(
                SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                LayoutRequest(),
                Array.Empty<SimpleDdgiReceiverFeedbackProducerQuota>(),
                QualifiedPrerequisites());
        SimpleDdgiAdvancedExperimentMemoryPlan caustic =
            SimpleDdgiAdvancedExperimentMemoryPlan.Empty with
            {
                CausticPhotonRecords = SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords,
                    requiredBytes: 256UL,
                    allocatedBytes: 256UL,
                    peakLiveBytes: 256UL)
            };

        SimpleDdgiAdvancedExperimentMemoryPlan combined =
            SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                feedback.Memory,
                caustic);

        Assert.Multiple(() =>
        {
            Assert.That(combined.CausticPhotonRecords.AllocatedBytes,
                Is.EqualTo(256UL));
            Assert.That(combined.AllocatedBytes, Is.EqualTo(76_768UL));
            Assert.That(() => SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                    feedback.Memory,
                    feedback.Memory),
                Throws.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void RejectedReceiverFeedbackPlan_OwnsOnlyB1FallbackCategories()
    {
        SimpleDdgiReceiverFeedbackPlan rejected =
            SimpleDdgiReceiverFeedbackPlan.Disabled(
                GiExperimentFallbackReason.ResourceIncomplete);

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Memory.IsValid, Is.True);
            Assert.That(
                rejected.Memory.ReceiverFeedbackRecordBanks.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.ResourceIncomplete));
            Assert.That(
                rejected.Memory.ReceiverFeedbackSortScratch.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.ResourceIncomplete));
            Assert.That(
                rejected.Memory.ReceiverFeedbackProbeSummaries.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.ResourceIncomplete));
            Assert.That(
                rejected.Memory.OpacityMicromapResidentData.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.None));
            Assert.That(
                rejected.Memory.NearFieldHistoryAndMoments.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.None));
        });
    }

    [Test]
    public void AdvancedMemoryPersistence_FailsClosedOnCategoryTagCorruption()
    {
        SimpleDdgiAdvancedExperimentMemoryPlan corrupt =
            SimpleDdgiAdvancedExperimentMemoryPlan.Empty with
            {
                OpacityMicromapResidentData =
                    SimpleDdgiAdvancedMemoryUsage.Admitted(
                        SimpleDdgiAdvancedMemoryCategory.CausticHistory,
                        requiredBytes: 64UL,
                        allocatedBytes: 64UL,
                        peakLiveBytes: 64UL)
            };

        Assert.Multiple(() =>
        {
            Assert.That(corrupt.IsValid, Is.False);
            Assert.That(corrupt.NormalizeForPersistence(),
                Is.EqualTo(SimpleDdgiAdvancedExperimentMemoryPlan.Empty));
            Assert.That(default(SimpleDdgiContentMemoryPlan)
                    .NormalizeForPersistence(),
                Is.EqualTo(SimpleDdgiContentMemoryPlan.Empty));
        });
    }

    [Test]
    public void BankValidator_OnlyAcceptsCompletePreviousGeneration()
    {
        var header = new SimpleDdgiReceiverFeedbackBankHeader(
            SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
            FeedbackGeneration: 9,
            ViewportGeneration: 4,
            FrameSerial: 100,
            AppendCount: 12,
            DroppedCount: 0,
            ProducerOverflowMask: 0,
            RecordCapacity: 12,
            Flags: SimpleDdgiReceiverFeedbackBankFlags.Validated);

        SimpleDdgiReceiverFeedbackBankValidation valid =
            SimpleDdgiReceiverFeedbackBankValidator.ValidateForScheduling(
                header,
                SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
                expectedFeedbackGeneration: 9,
                expectedViewportGeneration: 4,
                expectedFrameSerial: 101);
        SimpleDdgiReceiverFeedbackBankValidation stale =
            SimpleDdgiReceiverFeedbackBankValidator.ValidateForScheduling(
                header,
                SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
                expectedFeedbackGeneration: 9,
                expectedViewportGeneration: 4,
                expectedFrameSerial: 102);
        SimpleDdgiReceiverFeedbackBankValidation overflow =
            SimpleDdgiReceiverFeedbackBankValidator.ValidateForScheduling(
                header with { DroppedCount = 1 },
                SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
                expectedFeedbackGeneration: 9,
                expectedViewportGeneration: 4,
                expectedFrameSerial: 101);

        Assert.Multiple(() =>
        {
            Assert.That(valid.UseFeedback, Is.True);
            Assert.That(stale.Reason,
                Is.EqualTo(GiExperimentFallbackReason.GenerationMismatch));
            Assert.That(overflow.Reason,
                Is.EqualTo(GiExperimentFallbackReason.FeedbackBankOverflowed));
            Assert.That(SimpleDdgiReceiverFeedbackBankValidator.TryGetNextGeneration(
                    0u,
                    out uint firstGeneration),
                Is.True);
            Assert.That(firstGeneration, Is.EqualTo(1u));
            Assert.That(SimpleDdgiReceiverFeedbackBankValidator.TryGetNextGeneration(
                    uint.MaxValue,
                    out _),
                Is.False);
            Assert.That(() => SimpleDdgiReceiverFeedbackBankValidator.NextGeneration(
                    uint.MaxValue),
                Throws.TypeOf<OverflowException>());
        });
    }

    [Test]
    public void Reducer_PreservesResolvedOwnersExactTilesAndFallbackDemand()
    {
        const uint generation = 7;
        SimpleDdgiReceiverFeedbackSample[] samples =
        [
            Sample(2, 10, 100, 5, 0.25f, 2.0f,
                SimpleDdgiReceiverFeedbackProducer.OpaqueForward,
                SimpleDdgiReceiverFeedbackFallbackRole.RequestedFineOwnerFallback,
                generation, physical: 4.0f),
            Sample(10, 10, 100, 6, 0.5f, 1.0f,
                SimpleDdgiReceiverFeedbackProducer.Fog,
                SimpleDdgiReceiverFeedbackFallbackRole.ResolvedOwner,
                generation, physical: 6.0f),
            Sample(2, 10, 100, 5, 0.5f, 2.0f,
                SimpleDdgiReceiverFeedbackProducer.OpaqueForward,
                SimpleDdgiReceiverFeedbackFallbackRole.RequestedFineOwnerFallback,
                generation, physical: 2.0f),
            Sample(2, 11, 101, 8, 0.5f, 1.0f,
                SimpleDdgiReceiverFeedbackProducer.ReflectionCapture,
                SimpleDdgiReceiverFeedbackFallbackRole.RequestedFineOwnerFallback,
                generation, physical: 2.0f)
        ];

        SimpleDdgiReceiverFeedbackReductionResult result =
            SimpleDdgiReceiverFeedbackReducer.Reduce(
                samples,
                generation,
                static requestedProbe => requestedProbe == 2 ? 200u : 201u);

        SimpleDdgiReceiverFeedbackProbeSummary probe10 = result.ProbeSummaries
            .Single(summary => summary.ResolvedVirtualProbeId == 10u);
        SimpleDdgiReceiverFeedbackFallbackPressure fallback = result.FallbackPressure
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.True);
            Assert.That(probe10.Summary.EstimatedContributionMass,
                Is.EqualTo(7.0f).Within(1.0e-6f));
            Assert.That(probe10.Summary.ExactUniqueTileCount, Is.EqualTo(2u));
            Assert.That(probe10.Summary.SampledReceiverCount, Is.EqualTo(3u));
            Assert.That(probe10.Summary.ConsumerMask,
                Is.EqualTo((1u << (int)SimpleDdgiReceiverFeedbackProducer.OpaqueForward) |
                    (1u << (int)SimpleDdgiReceiverFeedbackProducer.Fog)));
            Assert.That(SimpleDdgiReceiverFeedbackV2Abi.UnpackRequestedFallbackCount(
                    probe10.Summary.PackedFallbackCounts), Is.EqualTo(2u));
            Assert.That(fallback.RequestedVirtualProbeId, Is.EqualTo(2u));
            Assert.That(fallback.RequestedVirtualPageId, Is.EqualTo(200u));
            Assert.That(fallback.EstimatedContributionMass,
                Is.EqualTo(5.0f).Within(1.0e-6f));
            Assert.That(fallback.SampledReceiverCount, Is.EqualTo(3u));
        });
    }

    [Test]
    public void Reducer_RejectsEntireWriteBankOnGenerationMismatch()
    {
        SimpleDdgiReceiverFeedbackReductionResult result =
            SimpleDdgiReceiverFeedbackReducer.Reduce(
                [Sample(1, 1, 1, 1, 1, 1,
                    SimpleDdgiReceiverFeedbackProducer.OpaqueForward,
                    SimpleDdgiReceiverFeedbackFallbackRole.ResolvedOwner,
                    generation: 6,
                    physical: 1)],
                expectedFeedbackGeneration: 7);

        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.False);
            Assert.That(result.FailureReason,
                Is.EqualTo(GiExperimentFallbackReason.GenerationMismatch));
            Assert.That(result.ProbeSummaries, Is.Empty);
            Assert.That(result.FallbackPressure, Is.Empty);
        });
    }

    [Test]
    public void StableSamplingAndPriority_UseSemanticIdentityAndOnlyClampTheScore()
    {
        var identity = new SimpleDdgiReceiverFeedbackStochasticIdentity(
            SimpleDdgiReceiverFeedbackProducer.Fog,
            StableReceiverOrTileId: 0x1_0000_0001UL,
            FrameSampleEpoch: 5);
        var differentProducer = identity with
        {
            Producer = SimpleDdgiReceiverFeedbackProducer.Particles
        };
        var contribution = new SimpleDdgiReceiverContribution(
            1, 1, 1, 1,
            InterpolationWeight: 1.0f,
            InverseInclusionProbability: 1.0f,
            SimpleDdgiReceiverFeedbackProducer.Fog,
            SimpleDdgiReceiverFeedbackFallbackRole.ResolvedOwner,
            PagePublicationGeneration: 1,
            FeedbackGeneration: 1);
        float mass = contribution.EstimateContributionMass(500.0f);
        float priority = SimpleDdgiReceiverFeedbackPriority.Transform(
            mass,
            medianContributionMass: 1.0f,
            exactUniqueTileCount: 4,
            roleBias: 0.0f,
            massWeight: 1.0f,
            coverageWeight: 1.0f,
            cap: 1.0f);

        Assert.Multiple(() =>
        {
            Assert.That(identity.Hash32(), Is.Not.EqualTo(differentProducer.Hash32()));
            Assert.That(identity.Hash32(), Is.EqualTo(identity.Hash32()));
            Assert.That(identity.IsIncluded(0.0f), Is.False);
            Assert.That(identity.IsIncluded(1.0f), Is.True);
            Assert.That(mass, Is.EqualTo(500.0f));
            Assert.That(priority, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void ModeStatePreservesAuthoredAutoSelectionWhenQualificationFails()
    {
        GiExperimentModeState<SimpleDdgiDirectionalGuidingMode> state =
            GiExperimentModeResolver.Resolve(
                SimpleDdgiDirectionalGuidingMode.AutoQualified,
                SimpleDdgiDirectionalGuidingMode.Off,
                new GiExperimentModeEvaluation(
                    Supported: true,
                    PrerequisitesSatisfied: true,
                    MemoryAdmitted: true,
                    ResourcesComplete: true,
                    RequiresQualification: true,
                    QualificationPassed: false,
                    QualificationId: "driver-content-shader-hash"));

        Assert.Multiple(() =>
        {
            Assert.That(state.RequestedMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode.AutoQualified));
            Assert.That(state.SupportedMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode.AutoQualified));
            Assert.That(state.AdmittedMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode.Off));
            Assert.That(state.EffectiveMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode.Off));
            Assert.That(state.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.QualificationNotPassed));
        });
    }

    [Test]
    public void AutoQualifiedMode_NormalizesToProductionWithoutEvidenceAuthority()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AdvancedGiActivationPolicy.NormalizeProductionMode(
                    GiCausticMode.AutoQualified),
                Is.EqualTo(GiCausticMode.WorldCacheExperiment));
            Assert.That(
                AdvancedGiActivationPolicy.RequiresQualification(
                    GiCausticMode.AutoQualified),
                Is.False);
        });
    }

    [Test]
    public void AutoQualifiedMode_RejectsOversizedEvidenceIds()
    {
        GiExperimentModeState<DdgiOpacityMicromapMode> state =
            GiExperimentModeResolver.Resolve(
                DdgiOpacityMicromapMode.AutoQualified,
                DdgiOpacityMicromapMode.Off,
                new GiExperimentModeEvaluation(
                    Supported: true,
                    PrerequisitesSatisfied: true,
                    MemoryAdmitted: true,
                    ResourcesComplete: true,
                    RequiresQualification: true,
                    QualificationPassed: true,
                    QualificationId: new string('e', 257)));

        Assert.Multiple(() =>
        {
            Assert.That(state.EffectiveMode, Is.EqualTo(DdgiOpacityMicromapMode.Off));
            Assert.That(state.FallbackReason,
                Is.EqualTo(GiExperimentFallbackReason.QualificationIdMissing));
            Assert.That(state.QualificationId, Is.Empty);
        });
    }

    [Test]
    public void AdvancedModes_DefaultRequestedPolicyAndCurrentSchemaRoundTripPreserveIntentAndEvidence()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        Assert.Multiple(() =>
        {
            Assert.That(gi.SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(gi.DdgiOpacityMicromapMode,
                Is.EqualTo(DdgiOpacityMicromapMode.ExtFourStateExperiment));
            Assert.That(gi.SimpleDdgiDirectionalGuidingMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment));
            Assert.That(gi.GiCausticMode,
                Is.EqualTo(GiCausticMode.WorldCacheExperiment));
            Assert.That(gi.SimpleDdgiNearFieldResidualMode,
                Is.EqualTo(SimpleDdgiNearFieldResidualMode
                    .HiZAdaptive));
            Assert.That(gi.DdgiRayTracingPipelineExperimentEnabled, Is.False);
        });

        gi.SimpleDdgiReceiverFeedbackMode =
            SimpleDdgiReceiverFeedbackMode.ExactCompacted;
        gi.DdgiOpacityMicromapMode = DdgiOpacityMicromapMode.AutoQualified;
        gi.SimpleDdgiDirectionalGuidingMode =
            SimpleDdgiDirectionalGuidingMode.AutoQualified;
        gi.GiCausticMode = GiCausticMode.AutoQualified;
        gi.SimpleDdgiNearFieldResidualMode =
            SimpleDdgiNearFieldResidualMode.AutoQualified;
        gi.SimpleDdgiReceiverFeedbackQualificationId = "b1-evidence";
        gi.DdgiOpacityMicromapQualificationId = "c1-evidence";
        gi.SimpleDdgiDirectionalGuidingQualificationId = "c3-evidence";
        gi.GiCausticQualificationId = "c4-evidence";
        gi.SimpleDdgiNearFieldResidualQualificationId = "c5-evidence";

        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"advanced-modes-{Guid.NewGuid():N}.json");
        try
        {
            settings.Save(path);
            GlobalIlluminationSettings loaded = RenderSettings.Load(path)
                .GlobalIllumination;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SimpleDdgiReceiverFeedbackMode,
                    Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
                Assert.That(loaded.DdgiOpacityMicromapMode,
                    Is.EqualTo(DdgiOpacityMicromapMode.AutoQualified));
                Assert.That(loaded.SimpleDdgiDirectionalGuidingMode,
                    Is.EqualTo(SimpleDdgiDirectionalGuidingMode.AutoQualified));
                Assert.That(loaded.GiCausticMode,
                    Is.EqualTo(GiCausticMode.AutoQualified));
                Assert.That(loaded.SimpleDdgiNearFieldResidualMode,
                    Is.EqualTo(SimpleDdgiNearFieldResidualMode.AutoQualified));
                Assert.That(loaded.SimpleDdgiReceiverFeedbackQualificationId,
                    Is.EqualTo("b1-evidence"));
                Assert.That(loaded.DdgiOpacityMicromapQualificationId,
                    Is.EqualTo("c1-evidence"));
                Assert.That(loaded.SimpleDdgiDirectionalGuidingQualificationId,
                    Is.EqualTo("c3-evidence"));
                Assert.That(loaded.GiCausticQualificationId,
                    Is.EqualTo("c4-evidence"));
                Assert.That(loaded.SimpleDdgiNearFieldResidualQualificationId,
                    Is.EqualTo("c5-evidence"));
                Assert.That(loaded.SimpleDdgiReceiverContributionFeedbackEnabled,
                    Is.True);
                Assert.That(loaded.DdgiOpacityMicromapExperimentEnabled, Is.True);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void AdvancedModes_ExplicitOffRoundTripsInsteadOfRevertingToDefaults()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.SimpleDdgiReceiverFeedbackMode = SimpleDdgiReceiverFeedbackMode.Off;
        gi.DdgiOpacityMicromapMode = DdgiOpacityMicromapMode.Off;
        gi.SimpleDdgiDirectionalGuidingMode =
            SimpleDdgiDirectionalGuidingMode.Off;
        gi.GiCausticMode = GiCausticMode.Off;
        gi.SimpleDdgiNearFieldResidualMode =
            SimpleDdgiNearFieldResidualMode.Off;

        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"advanced-modes-off-{Guid.NewGuid():N}.json");
        try
        {
            settings.Save(path);
            GlobalIlluminationSettings loaded = RenderSettings.Load(path)
                .GlobalIllumination;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SimpleDdgiReceiverFeedbackMode,
                    Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
                Assert.That(loaded.DdgiOpacityMicromapMode,
                    Is.EqualTo(DdgiOpacityMicromapMode.Off));
                Assert.That(loaded.SimpleDdgiDirectionalGuidingMode,
                    Is.EqualTo(SimpleDdgiDirectionalGuidingMode.Off));
                Assert.That(loaded.GiCausticMode,
                    Is.EqualTo(GiCausticMode.Off));
                Assert.That(loaded.SimpleDdgiNearFieldResidualMode,
                    Is.EqualTo(SimpleDdgiNearFieldResidualMode.Off));
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SchemaNineBooleanAliases_MigrateWithoutAutoPromotion()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"advanced-modes-v9-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 9,
                  "GlobalIllumination": {
                    "SimpleDdgiReceiverContributionFeedbackEnabled": false,
                    "DdgiOpacityMicromapExperimentEnabled": true,
                    "SimpleDdgiDirectionalRayGuidingExperimentEnabled": true,
                    "DdgiTaggedCausticCacheExperimentEnabled": true,
                    "SimpleDdgiNearFieldResidualExperimentEnabled": true,
                    "DdgiRayTracingPipelineExperimentEnabled": true
                  }
                }
                """);

            GlobalIlluminationSettings loaded = RenderSettings.Load(path)
                .GlobalIllumination;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SimpleDdgiReceiverFeedbackMode,
                    Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
                Assert.That(loaded.DdgiOpacityMicromapMode,
                    Is.EqualTo(DdgiOpacityMicromapMode.ExtFourStateExperiment));
                Assert.That(loaded.SimpleDdgiDirectionalGuidingMode,
                    Is.EqualTo(SimpleDdgiDirectionalGuidingMode
                        .PerProbeHistogramExperiment));
                Assert.That(loaded.GiCausticMode,
                    Is.EqualTo(GiCausticMode.WorldCacheExperiment));
                Assert.That(loaded.SimpleDdgiNearFieldResidualMode,
                    Is.EqualTo(SimpleDdgiNearFieldResidualMode
                        .HiZHalfResolutionExperiment));
                Assert.That(loaded.DdgiRayTracingPipelineExperimentEnabled,
                    Is.True);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void ScratchIntervalsAliasOnlyWhenTheirRenderGraphLifetimesDoNotOverlap()
    {
        GiExperimentScratchAllocation[] nonOverlapping =
        [
            new(SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch,
                100, new GiExperimentScratchInterval(0, 1)),
            new(SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                100, new GiExperimentScratchInterval(2, 3))
        ];
        GiExperimentScratchAllocation[] overlapping =
        [
            nonOverlapping[0],
            new(SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                50, new GiExperimentScratchInterval(1, 3))
        ];

        Assert.Multiple(() =>
        {
            Assert.That(nonOverlapping[0].Interval.CanAlias(nonOverlapping[1].Interval),
                Is.True);
            Assert.That(GiExperimentScratchAliasing.ComputePeakLiveBytes(nonOverlapping),
                Is.EqualTo(100UL));
            Assert.That(overlapping[0].Interval.CanAlias(overlapping[1].Interval),
                Is.False);
            Assert.That(GiExperimentScratchAliasing.ComputePeakLiveBytes(overlapping),
                Is.EqualTo(150UL));
            Assert.That(() => GiExperimentScratchAliasing.ComputePeakLiveBytes(
                    [new GiExperimentScratchAllocation(
                        SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks,
                        100UL,
                        new GiExperimentScratchInterval(0, 1))]),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    private static SimpleDdgiReceiverFeedbackLayoutRequest LayoutRequest() => new(
        ActivePhysicalProbeCapacity: 100,
        ScreenTileCount: 65,
        ScreenSamplingProbability: 0.5,
        MaximumUniqueGatherOwnersPerTile: 4,
        SafetyMarginRecords: 3,
        WorkgroupSize: SimpleDdgiReceiverFeedbackGpuSortAbi.WorkgroupSize,
        SortScratchBytesPerRecord: 0UL,
        IndependentMemoryBudgetBytes: 128UL * 1024UL,
        RendererMemoryHeadroomBytes: 128UL * 1024UL,
        MaximumStorageBufferRange: 128UL * 1024UL,
        MaximumPagePublicationGeneration: 0x00ff_ffffu);

    private static SimpleDdgiReceiverFeedbackPrerequisites QualifiedPrerequisites() => new(
        ExactBackendSupported: true,
        PrerequisitesSatisfied: true,
        ExactQualificationPassed: true,
        QualificationId: "device-driver-shader-content-evidence",
        ResourcesComplete: true);

    private static SimpleDdgiReceiverFeedbackSample Sample(
        uint requestedProbe,
        uint resolvedProbe,
        uint page,
        uint tile,
        float interpolationWeight,
        float inverseInclusionProbability,
        SimpleDdgiReceiverFeedbackProducer producer,
        SimpleDdgiReceiverFeedbackFallbackRole fallbackRole,
        uint generation,
        float physical) => new(
            new SimpleDdgiReceiverContribution(
                requestedProbe,
                resolvedProbe,
                page,
                tile,
                interpolationWeight,
                inverseInclusionProbability,
                producer,
                fallbackRole,
                PagePublicationGeneration: 1,
                FeedbackGeneration: generation),
            physical);
}
