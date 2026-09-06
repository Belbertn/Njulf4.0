using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSampledAtlasLayoutTests
{
    [Test]
    public void ReceiverRelevant_CompactsAWholePriorityPrefixAcrossCanonicalHoles()
    {
        SimpleDdgiSampledAtlasRangeRequest[] requests =
        [
            Range(0, "hero", 100, 100, true, SimpleDdgiVolumePurpose.ReceiverHero, -1),
            Range(1, "near", 900, 100, false, SimpleDdgiVolumePurpose.TransitionSupport, 0),
            Range(2, "mid", 2_000, 100, false, SimpleDdgiVolumePurpose.TransitionSupport, 1),
            Range(3, "far", 4_000, 40, false, SimpleDdgiVolumePurpose.TransitionSupport, 2)
        ];
        ulong twoRangeBudget = 256UL *
            (SimpleDdgiSampledAtlasLayoutCompiler.IrradianceBytesPerProbe +
             SimpleDdgiSampledAtlasLayoutCompiler.VisibilityBytesPerProbe);

        SimpleDdgiSampledAtlasLayout layout = SimpleDdgiSampledAtlasLayoutCompiler.Compile(
            requests,
            SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
            twoRangeBudget);

        Assert.Multiple(() =>
        {
            Assert.That(layout.RequestedProbeCount, Is.EqualTo(340));
            Assert.That(layout.EligibleProbeCount, Is.EqualTo(300));
            Assert.That(layout.AdmittedProbeCount, Is.EqualTo(200));
            Assert.That(layout.ProvisionedProbeCount, Is.EqualTo(256));
            Assert.That(layout.Ranges.Select(range => range.Identity),
                Is.EqualTo(new[] { "hero", "near" }));
            Assert.That(layout.Ranges[0].CompactFirstLayer, Is.Zero);
            Assert.That(layout.Ranges[1].CompactFirstLayer, Is.EqualTo(100));
            Assert.That(layout.ExcludedIdentities, Does.Contain("mid"));
            Assert.That(layout.ExcludedIdentities, Does.Contain("far"));
        });
    }

    [Test]
    public void ReceiverRelevant_PreservesAuthoredOwnershipOrderAcrossPurposes()
    {
        SimpleDdgiSampledAtlasRangeRequest[] requests =
        [
            Range(0, "dynamic-first", 0, 32, true,
                SimpleDdgiVolumePurpose.DynamicInfluence, -1, ownershipOrder: 0),
            Range(1, "hero-second", 64, 32, true,
                SimpleDdgiVolumePurpose.ReceiverHero, -1, ownershipOrder: 1),
            Range(2, "interior-third", 128, 32, true,
                SimpleDdgiVolumePurpose.NavigableInterior, -1, ownershipOrder: 2)
        ];

        SimpleDdgiSampledAtlasLayout layout =
            SimpleDdgiSampledAtlasLayoutCompiler.Compile(
                requests,
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                ulong.MaxValue);

        Assert.That(layout.Ranges.Select(static range => range.Identity),
            Is.EqualTo(new[] { "dynamic-first", "hero-second", "interior-third" }));
    }

    [Test]
    public void DisabledCoverage_HasDistinctMappingFingerprint()
    {
        SimpleDdgiSampledAtlasLayout disabled =
            SimpleDdgiSampledAtlasLayout.Disabled(
                SimpleDdgiSampledAtlasCoverageMode.Disabled);
        SimpleDdgiSampledAtlasLayout receiverRelevant =
            SimpleDdgiSampledAtlasLayout.Disabled(
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                "no-complete-range-admitted");

        Assert.That(receiverRelevant.Fingerprint, Is.Not.EqualTo(disabled.Fingerprint));
    }

    [Test]
    public void DeferredRelease_DisablesImageUseUntilNoRangeStateIsRetired()
    {
        SimpleDdgiSampledAtlasLayout disabled =
            SimpleDdgiSampledAtlasLayout.Disabled(
                SimpleDdgiSampledAtlasCoverageMode.Disabled,
                "coverage-disabled");

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ResolveSampledAtlasInactiveFallbackReason(
                    true,
                    disabled),
                Is.EqualTo("coverage-disabled"));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveSampledAtlasInactiveFallbackReason(
                    false,
                    disabled),
                Is.Empty);
        });
    }

    [Test]
    public void ActualBudgetRejection_OnlyDestroysAnUnsubmittedGenerationImmediately()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ResolveSampledAtlasBudgetRejectionFence(
                    previousAllocationGeneration: 7UL,
                    currentAllocationGeneration: 8UL,
                    currentLastUseFrameFenceValue: 123UL),
                Is.Zero,
                "A just-created generation has not been published or submitted.");
            Assert.That(
                SimpleDdgiVolumeManager.ResolveSampledAtlasBudgetRejectionFence(
                    previousAllocationGeneration: 7UL,
                    currentAllocationGeneration: 7UL,
                    currentLastUseFrameFenceValue: 123UL),
                Is.EqualTo(123UL),
                "A stable generation may still be referenced by submitted frames.");
        });
    }

    [Test]
    public void FullCanonical_IsAllOrNothingAndNeverPartiallyMirrorsAVolumeSet()
    {
        SimpleDdgiSampledAtlasRangeRequest[] requests =
        [
            Range(0, "a", 0, 128, true, SimpleDdgiVolumePurpose.ReceiverHero, -1),
            Range(1, "b", 400, 129, true, SimpleDdgiVolumePurpose.NavigableInterior, -1)
        ];
        ulong oneQuantum = SimpleDdgiSampledAtlasLayoutCompiler.ResolveTotalBytes(256);

        SimpleDdgiSampledAtlasLayout layout = SimpleDdgiSampledAtlasLayoutCompiler.Compile(
            requests,
            SimpleDdgiSampledAtlasCoverageMode.FullCanonical,
            oneQuantum);

        Assert.Multiple(() =>
        {
            Assert.That(layout.AdmittedProbeCount, Is.Zero);
            Assert.That(layout.Ranges, Is.Empty);
            Assert.That(layout.ExcludedIdentities, Is.EquivalentTo(new[] { "a", "b" }));
            Assert.That(layout.FallbackReason, Is.EqualTo("full-canonical-budget-exhausted"));
        });
    }

    [Test]
    public void ProvisionedBytesAndTextureGroupMappingShareOneBoundary()
    {
        const int admitted = 2_049;
        int provisioned = SimpleDdgiSampledAtlasLayoutCompiler.ResolveProvisionedProbeCount(admitted);
        ulong bytes = SimpleDdgiSampledAtlasLayoutCompiler.ResolveTotalBytes(provisioned);

        Assert.Multiple(() =>
        {
            Assert.That(provisioned, Is.EqualTo(2_304));
            Assert.That(bytes, Is.EqualTo(2_304UL * 2_096UL));
            Assert.That(SimpleDdgiSampledAtlas.TryResolveProbeLayer(
                2_048, 2_048, 2, out int group, out int layer), Is.True);
            Assert.That(group, Is.EqualTo(1));
            Assert.That(layer, Is.Zero);
            Assert.That(SimpleDdgiSampledAtlas.TryResolveProbeLayer(
                4_096, 2_048, 2, out _, out _), Is.False);
        });
    }

    [Test]
    public void PartialMirrorPadding_IsMeasuredAgainstAdmittedLayers()
    {
        SimpleDdgiSampledAtlasLayout layout =
            SimpleDdgiSampledAtlasLayoutCompiler.Compile(
                [Range(0, "near", 512, 100, false,
                    SimpleDdgiVolumePurpose.TransitionSupport, 0)],
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                ulong.MaxValue);
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            probeCount: 1_000,
            updateRequestCapacity: 16,
            rayCapacity: 64,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            sampledAtlasCoverageMode:
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
            sampledAtlasLayout: layout);

        Assert.Multiple(() =>
        {
            Assert.That(plan.SampledAtlasAdmittedProbeCount, Is.EqualTo(100));
            Assert.That(plan.SampledAtlasPhysicalProbeCapacity, Is.EqualTo(256));
            Assert.That(plan.SampledAtlasPaddingProbeCount, Is.EqualTo(156));
            Assert.That(plan.SampledAtlasPaddingBytes, Is.EqualTo(156UL * 2_096UL));
        });
    }

    [Test]
    public void ZeroRangesDoNotAcquireMappingsAndInvalidRangesFailClosed()
    {
        SimpleDdgiSampledAtlasRangeRequest zero =
            Range(0, "zero", 0, 0, true,
                SimpleDdgiVolumePurpose.ReceiverHero, -1);
        SimpleDdgiSampledAtlasLayout layout =
            SimpleDdgiSampledAtlasLayoutCompiler.Compile(
                [zero],
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                ulong.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(layout.Ranges, Is.Empty);
            Assert.That(layout.AdmittedProbeCount, Is.Zero);
            Assert.That(layout.ProvisionedProbeCount, Is.Zero);
            Assert.That(() => SimpleDdgiSampledAtlasLayoutCompiler.Compile(
                [
                    Range(0, "a", 0, 16, true,
                        SimpleDdgiVolumePurpose.ReceiverHero, -1),
                    Range(1, "b", 15, 16, true,
                        SimpleDdgiVolumePurpose.NavigableInterior, -1)
                ],
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                ulong.MaxValue), Throws.TypeOf<ArgumentException>());
            Assert.That(() => SimpleDdgiSampledAtlasLayoutCompiler.Compile(
                [zero, zero with { VolumeIndex = 1 }],
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                ulong.MaxValue), Throws.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void ActualMirrorBudget_IsTheRemainderAfterCanonicalResources()
    {
        SimpleDdgiSampledAtlasLayout layout =
            SimpleDdgiSampledAtlasLayoutCompiler.Compile(
                [Range(0, "hero", 0, 100, true,
                    SimpleDdgiVolumePurpose.ReceiverHero, -1)],
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                ulong.MaxValue);
        SimpleDdgiMemoryPlan plan = SimpleDdgiMemoryPlan.Create(
            100,
            8,
            32,
            sampledAtlasRequested: true,
            concreteTransportBuffers: true,
            readbackBufferCount: 0,
            sampledAtlasCoverageMode:
                SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
            sampledAtlasLayout: layout);
        const ulong extraHeadroom = 4_096UL;

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.ResolveSampledAtlasAllocationBudget(
                checked(plan.LiveBytes + extraHeadroom),
                plan), Is.EqualTo(plan.SampledAtlasImageBytes + extraHeadroom));
            Assert.That(SimpleDdgiVolumeManager.ResolveSampledAtlasAllocationBudget(
                0UL,
                plan), Is.EqualTo(ulong.MaxValue));
            Assert.That(SimpleDdgiVolumeManager.ResolveSampledAtlasAllocationBudget(
                plan.LiveBytes - plan.SampledAtlasImageBytes,
                plan), Is.Zero);
        });
    }

    private static SimpleDdgiSampledAtlasRangeRequest Range(
        int volume,
        string identity,
        int canonicalFirst,
        int probes,
        bool authored,
        SimpleDdgiVolumePurpose purpose,
        int ring,
        int? ownershipOrder = null) => new(
            volume,
            identity,
            volume + 1,
            canonicalFirst,
            probes,
            authored,
            purpose,
            ring,
            ownershipOrder ?? volume);
}
