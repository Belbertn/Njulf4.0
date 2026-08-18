using Njulf.Rendering.Resources;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using Njulf.Rendering;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiVolumeManagerTests
{
    [Test]
    public void VolumeAdmissionOrder_HigherPriorityRefinementWinsBeforeSlotOrdinal()
    {
        var cameraBrick = new SimpleDdgiVolumeAdmissionOrderKey(
            KindPriority: 3,
            HonorsExplicitPriority: true,
            Priority: 128,
            PurposeRank: 0,
            Spacing: 0.59375f,
            SourceOrdinal: 30_000);
        var emitterBrick = new SimpleDdgiVolumeAdmissionOrderKey(
            KindPriority: 3,
            HonorsExplicitPriority: true,
            Priority: 195,
            PurposeRank: 0,
            Spacing: 0.59375f,
            SourceOrdinal: 30_001);
        var equalPriorityLaterSlot = emitterBrick with
        {
            SourceOrdinal = 30_002
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.CompareVolumeAdmissionOrder(
                    emitterBrick,
                    cameraBrick),
                Is.LessThan(0));
            Assert.That(
                SimpleDdgiVolumeManager.CompareVolumeAdmissionOrder(
                    cameraBrick,
                    emitterBrick),
                Is.GreaterThan(0));
            Assert.That(
                SimpleDdgiVolumeManager.CompareVolumeAdmissionOrder(
                    emitterBrick,
                    equalPriorityLaterSlot),
                Is.LessThan(0));
        });
    }

    [Test]
    public void ReceiverFeedbackProbeFocus_ResolvesToroidalLogicalPositionAndRelocation()
    {
        var volume = new GPUSimpleDdgiVolume
        {
            OriginAndSpacing = new Vector4(10f, 20f, 30f, 2f),
            GridCountsAndFirstProbe = new Vector4(3f, 2f, 2f, 5f),
            // Physical offset (1, 0, 0) means physical slot x=1 maps to
            // logical x=0 and physical slot x=0 maps to logical x=2.
            RaysAndReserved = new Vector4(0f, 1f, 0f, 0f)
        };
        Vector3[] relocations = new Vector3[17];
        relocations[5] = new Vector3(0.25f, -0.5f, 1f);

        bool resolved = SimpleDdgiVolumeManager.TryResolveVirtualProbeWorldPosition(
            [volume], relocations, true, 5u, out Vector3 position);
        bool outside = SimpleDdgiVolumeManager.TryResolveVirtualProbeWorldPosition(
            [volume], relocations, true, 17u, out _);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.True);
            Assert.That(position.X, Is.EqualTo(14.25f).Within(0.0001f));
            Assert.That(position.Y, Is.EqualTo(19.5f).Within(0.0001f));
            Assert.That(position.Z, Is.EqualTo(31f).Within(0.0001f));
            Assert.That(outside, Is.False);
        });
    }

    [Test]
    public void ReceiverFeedbackProbeFocus_RefinementIdentityCannotDriveRefinementPlacement()
    {
        var baseVolume = new GPUSimpleDdgiVolume
        {
            OriginAndSpacing = new Vector4(1f, 2f, 3f, 1f),
            GridCountsAndFirstProbe = new Vector4(1f, 1f, 1f, 0f),
            WorldMaxAndKind = new Vector4(0f, 0f, 0f, 0f)
        };
        var refinementVolume = new GPUSimpleDdgiVolume
        {
            OriginAndSpacing = new Vector4(10f, 20f, 30f, 1f),
            GridCountsAndFirstProbe = new Vector4(1f, 1f, 1f, 1f),
            WorldMaxAndKind = new Vector4(0f, 0f, 0f, 3f)
        };

        bool baseResolved =
            SimpleDdgiVolumeManager.TryResolveBaseVolumeVirtualProbeWorldPosition(
                [baseVolume, refinementVolume],
                ReadOnlySpan<Vector3>.Empty,
                relocationReadbackValid: false,
                virtualProbeId: 0u,
                out Vector3 basePosition);
        bool refinementResolved =
            SimpleDdgiVolumeManager.TryResolveBaseVolumeVirtualProbeWorldPosition(
                [baseVolume, refinementVolume],
                ReadOnlySpan<Vector3>.Empty,
                relocationReadbackValid: false,
                virtualProbeId: 1u,
                out _);

        Assert.Multiple(() =>
        {
            Assert.That(baseResolved, Is.True);
            Assert.That(basePosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(refinementResolved, Is.False);
        });
    }

    [Test]
    public void SceneBoundsSnapshot_ReusesOnlyVersionedContentFromTheSameScene()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiSceneBounds.ShouldRefreshSnapshot(
                    hasSnapshot: true,
                    sameScene: true,
                    previousSceneContentRevision: 17UL,
                    sceneContentRevision: 17UL),
                Is.False);
            Assert.That(
                SimpleDdgiSceneBounds.ShouldRefreshSnapshot(
                    hasSnapshot: true,
                    sameScene: false,
                    previousSceneContentRevision: 17UL,
                    sceneContentRevision: 17UL),
                Is.True);
            Assert.That(
                SimpleDdgiSceneBounds.ShouldRefreshSnapshot(
                    hasSnapshot: true,
                    sameScene: true,
                    previousSceneContentRevision: 17UL,
                    sceneContentRevision: 18UL),
                Is.True);
            Assert.That(
                SimpleDdgiSceneBounds.ShouldRefreshSnapshot(
                    hasSnapshot: true,
                    sameScene: true,
                    previousSceneContentRevision: 17UL,
                    sceneContentRevision: 0UL),
                Is.True);
        });
    }

    [Test]
    public void SimpleDdgiDebugView_ReservesSourceCacheAbiButRejectsUnavailableReceiverPath()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ResolveSimpleDdgiDebugViewMode(GlobalIlluminationDebugView.DdgiSourceCacheRadiance),
                Is.Zero);
            Assert.That(SimpleDdgiVolumeManager.SourceCacheRadianceDebugViewMode, Is.EqualTo(125u));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveSimpleDdgiDebugViewMode(GlobalIlluminationDebugView.None),
                Is.Zero);
        });
    }

    [Test]
    public void DirtyLatencyPercentiles_AreDeterministicAndSaturateTheFinalBucket()
    {
        uint[] histogram = new uint[16];
        histogram[0] = 10;
        histogram[1] = 5;
        histogram[8] = 5;
        histogram[15] = 1;

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 0, 0.95f), Is.EqualTo(0));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 21, 0.50f), Is.EqualTo(1));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 21, 0.95f), Is.EqualTo(8));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 21, 1.0f), Is.EqualTo(15));
        });
    }

    [Test]
    public void DirtyLatencyPercentiles_PreserveLongTailFramesBeforeTheCensoredBucket()
    {
        uint[] histogram = new uint[4_096];
        histogram[0] = 90;
        histogram[773] = 10;

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 100, 0.50f), Is.EqualTo(0));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 100, 0.95f), Is.EqualTo(773));
            Assert.That(SimpleDdgiVolumeManager.CalculateLatencyPercentile(histogram, 100, 1.0f), Is.EqualTo(773));
        });
    }

    [TestCase(-1, 0)]
    [TestCase(0, 0)]
    [TestCase(131_072, 131_072)]
    public void ConfiguredSimpleDdgiPrimaryRayBudget_IsNeverDerivedFromScheduledWork(int configured, int expected)
    {
        Assert.That(VulkanRenderer.ResolveConfiguredSimpleDdgiPrimaryRayBudget(configured), Is.EqualTo(expected));
    }

    [TestCase(2_048, 15_368, false, 2_048)]
    [TestCase(2_048, 15_368, true, 4_096)]
    [TestCase(10_000, 15_368, true, 15_368)]
    [TestCase(2_048, 3_000, true, 3_000)]
    [TestCase(-1, 15_368, true, 0)]
    [TestCase(2_048, -1, true, 0)]
    public void ConfiguredSimpleDdgiRequestBudget_DeclaresBoundedDirtyResponseHeadroom(
        int baseBudget,
        int probeCount,
        bool lightingDirtyBoostEnabled,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveConfiguredRequestBudget(
                baseBudget,
                probeCount,
                lightingDirtyBoostEnabled),
            Is.EqualTo(expected));
    }

    [Test]
    public void BufferResizes_UseObservedCompletionTokensWithoutDeviceIdle()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        int ensureBufferStart = source.IndexOf(
            "private unsafe bool EnsureBuffer(",
            StringComparison.Ordinal);
        int ensureBufferEnd = source.IndexOf(
            "internal static bool RequiresStableCapacityReallocation(",
            ensureBufferStart,
            StringComparison.Ordinal);
        Assert.That(ensureBufferStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(ensureBufferEnd, Is.GreaterThan(ensureBufferStart));
        string ensureBuffer = source[ensureBufferStart..ensureBufferEnd];

        int synchronizedGuard = ensureBuffer.IndexOf(
            "if (destroyPreviousImmediately && previousHandle.IsValid)",
            StringComparison.Ordinal);
        int synchronizedDestroy = ensureBuffer.IndexOf(
            "_bufferManager.DestroyBuffer(previousHandle);",
            StringComparison.Ordinal);
        int deferredRetirement = ensureBuffer.LastIndexOf(
            "RetireBufferResource(previousHandle, previousBytes);",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("BeginFrameResourceRetirement();"));
            Assert.That(source, Does.Contain("ObserveFrameFenceCompletion("));
            Assert.That(source, Does.Contain("GpuCompletionToken.ForFrameFence("));
            Assert.That(source, Does.Contain("_bufferRetirement.Poll("));
            Assert.That(source, Does.Not.Contain("RenderingConstants.FramesInFlight + 1UL"));
            Assert.That(source, Does.Not.Contain("RecordCapacityDeviceWaitIdle();"));
            Assert.That(source, Does.Not.Contain("_context.WaitIdle);"));
            Assert.That(source, Does.Contain(
                "EnsureBindlessDescriptorReadersComplete("));
            Assert.That(source, Does.Contain(
                "RuntimeStallReason.ResourceGenerationFenceWait"));
            Assert.That(source, Does.Contain(
                "_waitForBindlessDescriptorReaders()"));
            Assert.That(source, Does.Contain(
                "bindless-descriptor-readers-pending"));
            Assert.That(source, Does.Contain(
                "completion-pending-global-memory-budget"));
            Assert.That(synchronizedGuard, Is.GreaterThanOrEqualTo(0));
            Assert.That(synchronizedDestroy, Is.GreaterThan(synchronizedGuard));
            Assert.That(deferredRetirement, Is.GreaterThan(synchronizedDestroy));
        });
    }

    [Test]
    public void UpdateQuotas_ConsumeConfiguredBudgetBeyondPreferredRingMaximums()
    {
        int[] quotas = new int[3];
        int[] minimums = [512, 96, 24];
        int[] preferredMaximums = [1_024, 324, 128];
        int[] capacities = [10_976, 3_240, 1_152];
        int[] weights = [6, 3, 1];

        SimpleDdgiVolumeManager.AllocateUpdateQuotas(
            quotas,
            minimums,
            preferredMaximums,
            capacities,
            weights,
            updateBudget: 2_048);

        Assert.Multiple(() =>
        {
            Assert.That(quotas.Sum(), Is.EqualTo(2_048));
            Assert.That(quotas, Is.All.GreaterThanOrEqualTo(0));
            Assert.That(quotas[0], Is.GreaterThan(preferredMaximums[0]));
            Assert.That(quotas[1], Is.GreaterThan(preferredMaximums[1]));
            Assert.That(quotas[2], Is.GreaterThan(preferredMaximums[2]));
        });
    }

    [TestCase(true, true, true, true, 2, SimpleDdgiSchedulerWorkClass.FreshExposedVisible)]
    [TestCase(false, true, true, true, 1, SimpleDdgiSchedulerWorkClass.VisibleDirty)]
    [TestCase(false, true, false, true, 0, SimpleDdgiSchedulerWorkClass.VisibleRetry)]
    [TestCase(false, false, true, true, 0, SimpleDdgiSchedulerWorkClass.NearMaintenance)]
    [TestCase(false, false, true, true, 1, SimpleDdgiSchedulerWorkClass.MidMaintenance)]
    [TestCase(false, false, true, true, 2, SimpleDdgiSchedulerWorkClass.FarMaintenance)]
    public void SchedulerWorkClasses_PreserveVisibleFirstPriority(
        bool freshOrExposed,
        bool visible,
        bool dirty,
        bool retry,
        int ringIndex,
        SimpleDdgiSchedulerWorkClass expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveSchedulerWorkClass(
                freshOrExposed,
                visible,
                dirty,
                retry,
                ringIndex),
            Is.EqualTo(expected));
    }

    [Test]
    public void SchedulerWorkClasses_VisibleZeroSupportPreemptsFreshAndMaintenance()
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveSchedulerWorkClass(
                zeroSupport: true,
                freshOrExposed: true,
                visible: true,
                dirty: true,
                retry: true,
                ringIndex: 0),
            Is.EqualTo(SimpleDdgiSchedulerWorkClass.VisibleZeroSupport));
    }

    [Test]
    public void ProbeStateReadback_OldPhysicalOrSourceGenerationCannotOverwriteCurrentState()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.IsProbeStateReadbackCurrent(
                    readbackProbeGeneration: 7,
                    currentProbeGeneration: 7,
                    expectedSourceEpoch: 11,
                    currentSourceEpoch: 11),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.IsProbeStateReadbackCurrent(6, 7, 11, 11),
                Is.False);
            Assert.That(
                SimpleDdgiVolumeManager.IsProbeStateReadbackCurrent(7, 7, 10, 11),
                Is.False);
        });
    }

    [TestCase(false, 255u, false)]
    [TestCase(true, 31u, false)]
    [TestCase(true, 32u, true)]
    [TestCase(true, 255u, true)]
    public void RelocationPendingCpuMirror_RetiresAtTheShaderTimeout(
        bool relocationPending,
        uint updateAge,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldRetireRelocationPendingOnCpu(
                relocationPending,
                updateAge),
            Is.EqualTo(expected));
    }

    [TestCase(15_354, 2_048, 32)]
    [TestCase(15_354, 1, 600)]
    [TestCase(0, 2_048, 32)]
    public void ProbeLifecycleLatencyTarget_CoversConfiguredRecoveryTransitions(
        int probeCount,
        int updateBudget,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveProbeLifecycleLatencyTarget(
                probeCount,
                updateBudget),
            Is.EqualTo(expected));
    }

    [TestCase(true, true, false, 0u, 8u, true)]
    [TestCase(true, true, true, 0u, 8u, false)]
    [TestCase(true, true, false, 8u, 8u, false)]
    [TestCase(true, false, false, 0u, 8u, false)]
    [TestCase(false, true, false, 0u, 8u, false)]
    public void InactiveScheduling_DoesNotThrottleFreshRelocationRetries(
        bool classificationSchedulingEnabled,
        bool inactive,
        bool freshOrRelocationPending,
        uint age,
        uint retryFrames,
        bool expectedSkip)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ShouldSkipInactiveProbeForScheduling(
                classificationSchedulingEnabled,
                inactive,
                freshOrRelocationPending,
                age,
                retryFrames),
            Is.EqualTo(expectedSkip));
    }

    [TestCase(15_354, 0, 767, true)]
    [TestCase(15_354, 0, 768, false)]
    [TestCase(15_354, 14, 767, true)]
    [TestCase(15_354, 15, 0, false)]
    [TestCase(1_000, 1, 49, true)]
    [TestCase(1_000, 1, 50, false)]
    [TestCase(20, 0, 1, true)]
    [TestCase(19, 0, 1, false)]
    [TestCase(0, 0, 0, true)]
    public void GlobalTransportConvergence_RequiresNinetyFivePercentOfSourceReadyProbes(
        int participatingProbeCount,
        int sourceRepairProbeCount,
        int pendingConvergenceProbeCount,
        bool expectedComplete)
    {
        Assert.That(
            SimpleDdgiVolumeManager.CanCompleteTransportGlobalConvergence(
                participatingProbeCount,
                sourceRepairProbeCount,
                pendingConvergenceProbeCount),
            Is.EqualTo(expectedComplete));
    }

    [TestCase(0, 1)]
    [TestCase(999, 1)]
    [TestCase(1_000, 1)]
    [TestCase(1_024, 1)]
    [TestCase(15_354, 14)]
    [TestCase(1_000_000, 976)]
    public void GlobalTransportConvergence_SourceRepairAllowanceIsBounded(
        int participatingProbeCount,
        int expectedAllowance)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransportGlobalConvergenceSourceRepairAllowance(
                participatingProbeCount),
            Is.EqualTo(expectedAllowance));
    }

    [Test]
    public void SchedulerClassQuotas_ReserveDirtyRetryAndMaintenanceDuringContinuousExposure()
    {
        int[] pending = new int[(int)SimpleDdgiSchedulerWorkClass.Count];
        pending[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible] = 100;
        pending[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty] = 100;
        pending[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry] = 100;
        pending[(int)SimpleDdgiSchedulerWorkClass.NearMaintenance] = 100;
        int[] quotas = new int[pending.Length];

        SimpleDdgiVolumeManager.AllocateSchedulerClassQuotas(
            volumeQuota: 32,
            pending,
            quotas);

        Assert.Multiple(() =>
        {
            Assert.That(quotas.Sum(), Is.EqualTo(32));
            Assert.That(quotas[(int)SimpleDdgiSchedulerWorkClass.FreshExposedVisible], Is.EqualTo(18));
            Assert.That(quotas[(int)SimpleDdgiSchedulerWorkClass.VisibleDirty], Is.EqualTo(8));
            Assert.That(quotas[(int)SimpleDdgiSchedulerWorkClass.VisibleRetry], Is.EqualTo(4));
            Assert.That(quotas[(int)SimpleDdgiSchedulerWorkClass.NearMaintenance], Is.EqualTo(2));
            Assert.That(quotas[(int)SimpleDdgiSchedulerWorkClass.MidMaintenance], Is.Zero);
            Assert.That(quotas[(int)SimpleDdgiSchedulerWorkClass.FarMaintenance], Is.Zero);
        });
    }

    [Test]
    public void SchedulerClassQuotas_TinyBudgetsRemainStrictlyVisibleFirst()
    {
        int[] pending = Enumerable.Repeat(1, (int)SimpleDdgiSchedulerWorkClass.Count).ToArray();
        int[] quotas = new int[pending.Length];

        SimpleDdgiVolumeManager.AllocateSchedulerClassQuotas(
            volumeQuota: 1,
            pending,
            quotas);

        Assert.Multiple(() =>
        {
            Assert.That(quotas.Sum(), Is.EqualTo(1));
            Assert.That(quotas[(int)SimpleDdgiSchedulerWorkClass.VisibleZeroSupport], Is.EqualTo(1));
            Assert.That(quotas.Skip(1), Is.All.Zero);
        });
    }

    [TestCase(256, 128, false, 128)]
    [TestCase(256, 128, true, 256)]
    [TestCase(256, 0, false, 256)]
    [TestCase(-1, 128, false, 0)]
    public void SchedulerFeedbackBudget_RemainsBoundedAndValidationCanStayFixed(
        int hardBudget,
        int feedbackCap,
        bool deterministicFixedBudget,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveFeedbackLimitedUpdateBudget(
                hardBudget,
                feedbackCap,
                deterministicFixedBudget),
            Is.EqualTo(expected));
    }

    [TestCase(4_096, 128, true, 128)]
    [TestCase(4_096, 64, true, 256)]
    [TestCase(4_096, 32, true, 512)]
    [TestCase(3_072, 192, true, 85)]
    [TestCase(4_096, 128, false, 4_096)]
    [TestCase(0, 128, true, 0)]
    public void TransportV2RequestBudget_BoundsCompleteDirectionalRayWork(
        int configuredBudget,
        int maximumFullRaysPerProbe,
        bool transportV2Active,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransportV2RequestBudget(
                configuredBudget,
                maximumFullRaysPerProbe,
                transportV2Active),
            Is.EqualTo(expected));
    }

    [TestCase(4_096, 128, true, true, 512)]
    [TestCase(4_096, 64, true, true, 1_024)]
    [TestCase(256, 128, true, true, 256)]
    [TestCase(4_096, 128, true, false, 128)]
    [TestCase(4_096, 128, false, true, 4_096)]
    [TestCase(0, 128, true, true, 0)]
    public void TransportV2RequestCapacity_SeparatesCachedSolveFromSourceTracing(
        int configuredBudget,
        int maximumFullRaysPerProbe,
        bool transportV2Active,
        bool acceleratedTailSolveEnabled,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransportV2SchedulerRequestCapacity(
                configuredBudget,
                maximumFullRaysPerProbe,
                transportV2Active,
                acceleratedTailSolveEnabled),
            Is.EqualTo(expected));
    }

    [TestCase(4_096, 128, 512, true, false, 128)]
    [TestCase(4_096, 128, 512, true, true, 512)]
    [TestCase(256, 128, 512, true, true, 256)]
    [TestCase(4_096, 128, 512, false, false, 512)]
    [TestCase(-1, 128, 512, true, true, 0)]
    public void TransportV2FrameBudget_UsesSolveCapacityOnlyForCachedEpochs(
        int requestedBudget,
        int sourceBudget,
        int requestCapacity,
        bool transportV2Active,
        bool acceleratedSolveActive,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransportV2FrameRequestBudget(
                requestedBudget,
                sourceBudget,
                requestCapacity,
                transportV2Active,
                acceleratedSolveActive),
            Is.EqualTo(expected));
    }

    [TestCase(4_096, 128, 512, true, false, true, 256)]
    [TestCase(4_096, 64, 1_024, true, false, true, 128)]
    [TestCase(256, 128, 512, true, false, true, 256)]
    [TestCase(4_096, 128, 192, true, false, true, 192)]
    [TestCase(4_096, 128, 512, true, true, true, 512)]
    [TestCase(4_096, 128, 512, false, false, true, 512)]
    public void TransportV2SpatialRecoveryFrameBudget_UsesBoundedDoubleSourceEnvelope(
        int requestedBudget,
        int sourceBudget,
        int requestCapacity,
        bool transportV2Active,
        bool acceleratedSolveActive,
        bool spatialRecoveryActive,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransportV2FrameRequestBudget(
                requestedBudget,
                sourceBudget,
                requestCapacity,
                transportV2Active,
                acceleratedSolveActive,
                spatialRecoveryActive),
            Is.EqualTo(expected));
    }

    [TestCase(4_096, 128, 512, true, false, false, true, 512)]
    [TestCase(4_096, 64, 1_024, true, false, false, true, 256)]
    [TestCase(4_096, 128, 384, true, false, true, true, 384)]
    [TestCase(256, 128, 512, true, false, false, true, 256)]
    [TestCase(4_096, 128, 512, true, true, false, true, 512)]
    [TestCase(4_096, 128, 512, false, false, false, true, 512)]
    public void TransportV2RadiometricRecoveryFrameBudget_UsesBoundedFourfoldSourceEnvelope(
        int requestedBudget,
        int sourceBudget,
        int requestCapacity,
        bool transportV2Active,
        bool acceleratedSolveActive,
        bool spatialRecoveryActive,
        bool radiometricRecoveryActive,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransportV2FrameRequestBudget(
                requestedBudget,
                sourceBudget,
                requestCapacity,
                transportV2Active,
                acceleratedSolveActive,
                spatialRecoveryActive,
                radiometricRecoveryActive),
            Is.EqualTo(expected));
    }

    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(64, 64)]
    [TestCase(512, 128)]
    [TestCase(2_048, 128)]
    [TestCase(int.MaxValue, 128)]
    public void RoutineSchedulerWakeBudget_PacesOnlyBackgroundDeadlines(
        int baseUpdateBudget,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveRoutineSchedulerWakeRefreshBudget(
                baseUpdateBudget),
            Is.EqualTo(expected));
    }

    [TestCase(false, 12, true, false, false, false, false)]
    [TestCase(true, 0, true, false, false, false, false)]
    [TestCase(true, 12, true, false, false, false, false)]
    [TestCase(true, 12, true, true, false, false, true)]
    [TestCase(true, 12, true, false, true, false, true)]
    [TestCase(true, 12, true, false, false, true, true)]
    [TestCase(true, 12, false, false, false, false, true)]
    public void RegionalInvalidation_OnlyFieldBoundariesOpenGlobalConvergence(
        bool transportV2Active,
        int newlyInvalidatedProbeCount,
        bool hasRegionalDirtyWork,
        bool requiresGlobalInvalidation,
        bool atlasFresh,
        bool recenteredThisFrame,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager
                .ShouldBeginTransportGlobalConvergenceForInvalidation(
                    transportV2Active,
                    newlyInvalidatedProbeCount,
                    hasRegionalDirtyWork,
                    requiresGlobalInvalidation,
                    atlasFresh,
                    recenteredThisFrame),
            Is.EqualTo(expected));
    }

    [TestCase(2_048, 1, false, 632, 632)]
    [TestCase(2_048, 1, false, 0, 1)]
    [TestCase(256, 1, false, 512, 256)]
    [TestCase(256, 1, true, 64, 256)]
    public void SchedulerFeedbackBudget_PreservesBoundedVisibleFreshRecovery(
        int hardBudget,
        int feedbackCap,
        bool deterministicFixedBudget,
        int minimumRecoveryBudget,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveFeedbackLimitedUpdateBudget(
                hardBudget,
                feedbackCap,
                deterministicFixedBudget,
                minimumRecoveryBudget),
            Is.EqualTo(expected));
    }

    [Test]
    public void SchedulerTelemetryHooks_AreBoundedAndAllocationFreeOnTheRenderThread()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("private readonly int[] _volumeWorkClassPendingScratch"));
            Assert.That(source, Does.Contain("private readonly byte[] _queuedWorkClassScratch"));
            Assert.That(source, Does.Contain("public void ReportSchedulingFeedback(in SimpleDdgiSchedulingFeedback feedback)"));
            Assert.That(source, Does.Contain("SimpleDdgiSchedulerPressureReason.FeedbackReducedBudget"));
        });
    }

    [Test]
    public void SchedulerPriorityClasses_UsePersistentIncrementalRoundRobinQueues()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        string queues = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiPersistentProbeQueues.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("private readonly SimpleDdgiPersistentProbeQueues _schedulerWorkQueues;"));
            Assert.That(source, Does.Contain("queues.TryRotateNext(queueIndex, out int probeIndex)"));
            Assert.That(source, Does.Contain("_schedulerWorkQueues.GetQueueCount(queueIndex)"));
            Assert.That(source, Does.Not.Contain("_volumeWorkClassRoundRobinCursors"));
            Assert.That(queues, Does.Contain("public void MoveToQueue(int probeIndex, int queueIndex)"));
            Assert.That(queues, Does.Contain("public bool TryRotateNext(int queueIndex, out int probeIndex)"));
        });
    }

    [Test]
    public void ResidencyArenaReplacement_CompletesBindlessReadersBeforePublication()
    {
        string manager = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        string pageCache = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiProbePageCache.cs"));
        string renderer = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "VulkanRenderer.cs"));

        int predictsReplacement = manager.IndexOf(
            "_probePageCache.RequiresReplacement(",
            StringComparison.Ordinal);
        int completesReaders = manager.IndexOf(
            "EnsureBindlessDescriptorReadersComplete(",
            predictsReplacement,
            StringComparison.Ordinal);
        int replacesArena = manager.IndexOf(
            "_probePageCache.EnsureCapacity(",
            completesReaders,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(pageCache, Does.Contain("public bool RequiresReplacement("));
            Assert.That(predictsReplacement, Is.GreaterThanOrEqualTo(0));
            Assert.That(completesReaders, Is.GreaterThan(predictsReplacement));
            Assert.That(replacesArena, Is.GreaterThan(completesReaders));
            Assert.That(renderer, Does.Contain(
                "WaitForSimpleDdgiBindlessDescriptorReaders"));
            Assert.That(renderer, Does.Contain("_sync.WaitForFence(frameIndex);"));
            Assert.That(renderer, Does.Not.Contain(
                "WaitForSimpleDdgiBindlessDescriptorReaders()\n        {\n            _context.WaitIdle"));
        });
    }

    [TestCase(0UL, 17UL, true)]
    [TestCase(17UL, 0UL, true)]
    [TestCase(17UL, 18UL, true)]
    [TestCase(17UL, 17UL, false)]
    public void ResidencyArenaReuse_RequiresMatchingImmutableTopology(
        ulong residentTopologyFingerprint,
        ulong requestedTopologyFingerprint,
        bool expectedReplacement)
    {
        Assert.That(
            SimpleDdgiProbePageCache.RequiresTopologyReplacement(
                residentTopologyFingerprint,
                requestedTopologyFingerprint),
            Is.EqualTo(expectedReplacement));
    }

    [TestCase(0UL, 0UL)]
    [TestCase(5UL, 4UL)]
    [TestCase(10UL, 8UL)]
    [TestCase(2_147_483_648UL, 1_717_986_918UL)]
    [TestCase(ulong.MaxValue, ulong.MaxValue)]
    public void CapacityTransitionOverlap_UsesTheEightyPercentGlobalMemoryGate(
        ulong budget,
        ulong expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveTransitionMemoryLimit(budget),
            Is.EqualTo(expected));
    }

    [TestCase(0, 4_096, 4_096, 20_000UL, 3_000UL, 276)]
    [TestCase(1_024, 1_024, 4_096, 6_000UL, 3_000UL, 230)]
    [TestCase(256, 256, 4_096, 1_000UL, 3_000UL, 288)]
    [TestCase(256, 256, 4_096, 1_300UL, 3_000UL, 256)]
    public void SchedulerFeedbackController_RespondsProportionallyWithoutAuditOnlyRecovery(
        int feedbackCap,
        int effectiveBudget,
        int configuredBudget,
        ulong completedGpuMicroseconds,
        ulong targetGpuMicroseconds,
        int expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveAdaptiveSchedulingBudgetCap(
                feedbackCap,
                effectiveBudget,
                configuredBudget,
                completedGpuMicroseconds,
                targetGpuMicroseconds),
            Is.EqualTo(expected));
    }

    [TestCase(false, 3_000UL, false, true)]
    [TestCase(true, 3_000UL, false, false)]
    [TestCase(false, 0UL, false, false)]
    [TestCase(false, 3_000UL, true, false)]
    public void SchedulerFeedbackController_DoesNotThrottleTheFixedV2RayEnvelope(
        bool deterministicFixedBudget,
        ulong targetGpuMicroseconds,
        bool transportV2Active,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.UsesAdaptiveSchedulingFeedback(
                deterministicFixedBudget,
                targetGpuMicroseconds,
                transportV2Active),
            Is.EqualTo(expected));
    }

    [Test]
    public void ResidentBootstrapSeedsPrivateStateAndPreservesFenceCompleteCursors()
    {
        string manager = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        string scheduler = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiGpuScheduler.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(manager, Does.Contain("UploadGpuResidentSchedulerBootstrap"));
            Assert.That(manager, Does.Contain("BuildGpuResidentSchedulerProbeState"));
            Assert.That(manager, Does.Contain("TryCopyLastFeedbackLaneCursors"));
            Assert.That(manager, Does.Contain("_gpuResidentProbeStateBootstrapped"));
            Assert.That(manager, Does.Contain("_probeInvalidationMarkers"));
            Assert.That(manager, Does.Contain("_probeStableUpdateCounts"));
            Assert.That(manager, Does.Contain("_probeRoutineMaintenancePending"));
            Assert.That(scheduler, Does.Contain("UploadResidentBootstrap"));
            Assert.That(scheduler, Does.Contain("TransferBit"));
            Assert.That(scheduler, Does.Contain("ShaderStorageReadBit"));
            Assert.That(scheduler, Does.Contain("ShaderStorageWriteBit"));
            Assert.That(scheduler, Does.Contain("_lastFeedbackLaneCursors"));
        });
    }

    [Test]
    public void SchedulerHotPath_DoesNotAgeOrClassifyTheEntireProbePool()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        int queueStart = source.IndexOf(
            "private int BuildUpdateQueue(",
            StringComparison.Ordinal);
        int queueEnd = source.IndexOf(
            "private void ResolveSourceRefreshThroughputTarget(",
            queueStart,
            StringComparison.Ordinal);
        int reservationStart = source.IndexOf(
            "private void BuildWorkClassReservations(",
            StringComparison.Ordinal);
        int reservationEnd = source.IndexOf(
            "internal static void AllocateSchedulerClassQuotas(",
            reservationStart,
            StringComparison.Ordinal);

        Assert.That(queueStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(queueEnd, Is.GreaterThan(queueStart));
        Assert.That(reservationStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(reservationEnd, Is.GreaterThan(reservationStart));
        string queueBuilder = source[queueStart..queueEnd];
        string reservations = source[reservationStart..reservationEnd];
        Assert.Multiple(() =>
        {
            Assert.That(queueBuilder, Does.Not.Contain("_probeAges"));
            Assert.That(queueBuilder, Does.Not.Contain("for (int probeIndex = 0; probeIndex < _probeCount"));
            Assert.That(reservations, Does.Not.Contain("for (int local = 0; local < probeCount"));
            Assert.That(reservations, Does.Contain("_schedulerWorkQueues.GetQueueCount(queueIndex)"));
            Assert.That(source, Does.Contain("private uint[] _probeLastUpdatedFrames"));
        });
    }

    [Test]
    public void ProbeStateReadback_ConsumesOnlySubmittedProbeSlots()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        int readbackStart = source.IndexOf(
            "private unsafe void ReadCompletedProbeStateReadback(",
            StringComparison.Ordinal);
        int readbackEnd = source.IndexOf(
            "private float CalculateProbeRelocationFraction(",
            readbackStart,
            StringComparison.Ordinal);

        Assert.That(readbackStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(readbackEnd, Is.GreaterThan(readbackStart));
        string readback = source[readbackStart..readbackEnd];
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("_probeStateReadbackUpdatedProbeIndices"));
            Assert.That(source, Does.Contain("RecordProbeStateReadbackUpdatedSlots(frameIndex);"));
            Assert.That(readback, Does.Contain(
                "for (int updatedOffset = 0; updatedOffset < updatedProbeCount; updatedOffset++)"));
            Assert.That(readback, Does.Not.Contain(
                "for (int probeIndex = 0; probeIndex < probeCount; probeIndex++)"));
        });
    }

    [Test]
    public void SimpleDdgiDiagnostics_ReportTheScheduledPerHitLightLimit()
    {
        string manager = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        string renderer = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "VulkanRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(manager, Does.Contain("public int EffectiveMaxShadedLights => _effectiveMaxShadedLights;"));
            Assert.That(manager, Does.Contain("_effectiveMaxShadedLights = Math.Max("));
            Assert.That(renderer, Does.Contain("sceneData.DdgiEffectiveMaxShadedLights = _simpleDdgiVolumeManager.EffectiveMaxShadedLights;"));
            Assert.That(renderer, Does.Contain("PopulateSimpleDdgiLightSelectionDiagnostics("));
            Assert.That(renderer, Does.Contain("? \"simple-per-hit-top-n\""));
        });
    }

    [TestCase(100UL, 1, 0, 8, 100UL)]
    [TestCase(100UL, 2, 10, 8, 800UL)]
    [TestCase(100UL, 2, 10, 3, 300UL)]
    [TestCase(100UL, 2, 10, 0, 0UL)]
    public void SimpleDdgiShadowRayUpperBound_UsesTheActualTopNLightCapacity(
        ulong primaryRays,
        int directionalLights,
        int localLights,
        int maxShadedLights,
        ulong expected)
    {
        Assert.That(
            VulkanRenderer.EstimateSimpleDdgiShadowRayUpperBound(
                primaryRays,
                directionalLights,
                localLights,
                maxShadedLights),
            Is.EqualTo(expected));
    }

    [Test]
    public void SimpleDdgiEnvironmentSignature_InvalidatesEveryProbeTransportInput()
    {
        var baseline = new EnvironmentSettings
        {
            Enabled = true,
            SourceKind = EnvironmentSourceKind.ProceduralSky,
            SourcePath = string.Empty,
            SkyIntensity = 1.0f,
            RotationRadians = 0.0f,
            EnvironmentSize = 1024,
            IrradianceSize = 64,
            TexturePrecision = EnvironmentTexturePrecision.Float16
        };
        ulong baselineSignature = VulkanRenderer.CreateSimpleDdgiEnvironmentSignature(baseline);

        Assert.Multiple(() =>
        {
            Assert.That(SignatureWith(baseline, environment => environment.Enabled = false), Is.Not.EqualTo(baselineSignature));
            Assert.That(SignatureWith(baseline, environment => environment.SourceKind = EnvironmentSourceKind.HdrEquirectangular), Is.Not.EqualTo(baselineSignature));
            Assert.That(SignatureWith(baseline, environment => environment.SourcePath = "alternate.hdr"), Is.Not.EqualTo(baselineSignature));
            Assert.That(SignatureWith(baseline, environment => environment.SkyIntensity = 0.8f), Is.Not.EqualTo(baselineSignature));
            Assert.That(SignatureWith(baseline, environment => environment.RotationRadians = 0.5f), Is.Not.EqualTo(baselineSignature));
            Assert.That(SignatureWith(baseline, environment => environment.EnvironmentSize = 512), Is.Not.EqualTo(baselineSignature));
            Assert.That(SignatureWith(baseline, environment => environment.IrradianceSize = 32), Is.Not.EqualTo(baselineSignature));
            Assert.That(SignatureWith(baseline, environment => environment.TexturePrecision = EnvironmentTexturePrecision.Float32), Is.Not.EqualTo(baselineSignature));
        });
    }

    private static ulong SignatureWith(EnvironmentSettings baseline, Action<EnvironmentSettings> mutate)
    {
        var copy = new EnvironmentSettings
        {
            Enabled = baseline.Enabled,
            SourceKind = baseline.SourceKind,
            SourcePath = baseline.SourcePath,
            SkyIntensity = baseline.SkyIntensity,
            DiffuseIntensity = baseline.DiffuseIntensity,
            SpecularIntensity = baseline.SpecularIntensity,
            RotationRadians = baseline.RotationRadians,
            EnvironmentSize = baseline.EnvironmentSize,
            IrradianceSize = baseline.IrradianceSize,
            PrefilteredSize = baseline.PrefilteredSize,
            BrdfLutSize = baseline.BrdfLutSize,
            TexturePrecision = baseline.TexturePrecision
        };
        mutate(copy);
        return VulkanRenderer.CreateSimpleDdgiEnvironmentSignature(copy);
    }

    [Test]
    public void Renderer_PreparesSimpleDdgiAndPublishesCurrentEmissiveRevision()
    {
        string renderer = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "VulkanRenderer.cs"));
        int methodStart = renderer.IndexOf("private void PrepareDdgiProbeVolumes(", StringComparison.Ordinal);
        int methodEnd = renderer.IndexOf(
            "private void ScheduleReflectionProbeRecapturesFromGi(",
            methodStart,
            StringComparison.Ordinal);
        string method = renderer[methodStart..methodEnd].Replace("\r\n", "\n", StringComparison.Ordinal);

        int emissiveUpload = method.IndexOf("UploadDdgiEmissiveSources(", StringComparison.Ordinal);
        int simpleDirtySignature = method.IndexOf("CreateSimpleDdgiDirtySignature(", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("bool simpleDdgiActive ="));
            Assert.That(emissiveUpload, Is.GreaterThanOrEqualTo(0));
            Assert.That(simpleDirtySignature, Is.GreaterThan(emissiveUpload));
        });
    }

    [Test]
    public void Upload_EstablishesAtlasCapacityBeforeClassifyingVisibleFreshRecovery()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));

        int uploadStart = source.IndexOf(
            "public void Upload(",
            StringComparison.Ordinal);
        int uploadEnd = source.IndexOf(
            "public void EnsureDisabled(",
            uploadStart,
            StringComparison.Ordinal);
        Assert.That(uploadStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(uploadEnd, Is.GreaterThan(uploadStart));
        string upload = source[uploadStart..uploadEnd];

        int ensureCapacity = upload.IndexOf(
            "EnsureCapacity(",
            StringComparison.Ordinal);
        int configuredCapacityBudget = upload.IndexOf(
            "_schedulerConfiguredRequestBudget",
            ensureCapacity,
            StringComparison.Ordinal);
        int markFresh = upload.IndexOf(
            "MarkFreshForNewOrScrolledProbes();",
            StringComparison.Ordinal);
        int refreshImportance = upload.IndexOf(
            "int visibleFreshRecoveryBudget = RefreshProbeSchedulingImportance();",
            StringComparison.Ordinal);
        int resolveFeedback = upload.IndexOf(
            "int updateBudget = ResolveFeedbackLimitedUpdateBudget(",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(ensureCapacity, Is.GreaterThanOrEqualTo(0));
            Assert.That(configuredCapacityBudget, Is.GreaterThan(ensureCapacity));
            Assert.That(configuredCapacityBudget, Is.LessThan(markFresh));
            Assert.That(markFresh, Is.GreaterThan(ensureCapacity));
            Assert.That(refreshImportance, Is.GreaterThan(markFresh));
            Assert.That(resolveFeedback, Is.GreaterThan(refreshImportance));
        });
    }

    [Test]
    public void Renderer_FeedsCompletedSimpleDdgiGpuTimingBackIntoScheduler()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "VulkanRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("HasCompletedSimpleDdgiGpuTiming(completedGpuTimings)"));
            Assert.That(source, Does.Contain("hasCompletedSimpleDdgiScheduleTiming"));
            Assert.That(source, Does.Contain("_simpleDdgiVolumeManager.ReportSchedulingFeedback"));
            Assert.That(source, Does.Contain("EffectiveDdgiAdaptiveBudgetTimeMilliseconds"));
            Assert.That(source, Does.Contain("detailedDdgiInstrumentationActive"));
            Assert.That(source, Does.Contain("fixedSimpleDdgiBudget || hasCompletedSimpleDdgiScheduleTiming"));
            Assert.That(source, Does.Contain("DeterministicFixedBudget: fixedSimpleDdgiBudget"));
        });
    }

    [Test]
    public void Renderer_AccountsDdgiAndPagedFarFieldAgainstIndependentMemoryBudgets()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "VulkanRenderer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(
                source,
                Does.Contain("sceneData.DdgiBufferBytes = checked("));
            Assert.That(
                source,
                Does.Contain("_simpleDdgiVolumeManager.BufferBytes +"));
            Assert.That(
                source,
                Does.Contain("_forwardPlusPass?.SimpleDdgiReceiverCacheBufferBytes ?? 0"));
            Assert.That(
                source,
                Does.Contain("_forwardPlusPass?.SimpleDdgiReceiverGatherBufferTotalBytes ?? 0"));
            Assert.That(
                source,
                Does.Not.Contain(
                    "sceneData.DdgiBufferBytes = _simpleDdgiVolumeManager.BufferBytes + (_farFieldClipmapManager?.BufferBytes ?? 0UL);"));
            Assert.That(
                source,
                Does.Contain("sceneData.FarFieldCacheBytes = _farFieldClipmapManager.PageCacheBytes;"));
        });
    }

    [Test]
    public void PerRingGridSelection_UsesExplicitNearMidAndFarSettings()
    {
        var settings = new GlobalIlluminationSettings
        {
            SimpleDdgiNearRingGridSizeX = 28,
            SimpleDdgiNearRingGridSizeY = 14,
            SimpleDdgiNearRingGridSizeZ = 28,
            SimpleDdgiMidRingGridSizeX = 18,
            SimpleDdgiMidRingGridSizeY = 10,
            SimpleDdgiMidRingGridSizeZ = 18,
            SimpleDdgiFarRingGridSizeX = 12,
            SimpleDdgiFarRingGridSizeY = 8,
            SimpleDdgiFarRingGridSizeZ = 12
        };

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 0), Is.EqualTo((28, 14, 28)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 1), Is.EqualTo((18, 10, 18)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 2), Is.EqualTo((12, 8, 12)));

            settings.SimpleDdgiRingGridSizeX = 9;
            settings.SimpleDdgiRingGridSizeY = 7;
            settings.SimpleDdgiRingGridSizeZ = 5;
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 0), Is.EqualTo((9, 7, 5)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 1), Is.EqualTo((9, 7, 5)));
            Assert.That(SimpleDdgiVolumeManager.ResolveRingGrid(settings, 2), Is.EqualTo((9, 7, 5)));
        });
    }

    [Test]
    public void ProbeAgePercentile_UsesExactNearestRankWithinTheRequestedVolume()
    {
        uint[] ages = Enumerable.Range(0, 20).Select(static value => (uint)value).ToArray();
        uint[] scratch = new uint[ages.Length];

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch, 0.50f), Is.EqualTo(9u));
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch, 0.95f), Is.EqualTo(18u));
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch, 1.0f), Is.EqualTo(19u));
            Assert.That(SimpleDdgiVolumeManager.CalculateProbeAgePercentile(ages, scratch[..5], 0.95f), Is.Zero);
        });
    }

    [Test]
    public void AuthoredLatticePhase_OffsetsAndWrapsProbePlanesWithoutMovingBounds()
    {
        Vector3 min = new(-2.1f, 0.1f, 4.2f);
        Vector3 origin = SimpleDdgiVolumeManager.ResolveAuthoredLatticeOrigin(
            min,
            spacing: 1.0f,
            latticePhase: new Vector3(0.5f, 0.25f, 0.75f));
        Vector3 wrappedOrigin = SimpleDdgiVolumeManager.ResolveAuthoredLatticeOrigin(
            new Vector3(0.1f, 0.1f, 0.1f),
            spacing: 1.0f,
            latticePhase: new Vector3(-0.25f, 1.25f, float.NaN));

        Assert.Multiple(() =>
        {
            Assert.That(origin, Is.EqualTo(new Vector3(-2.5f, -0.75f, 3.75f)));
            Assert.That(origin.X, Is.LessThanOrEqualTo(min.X));
            Assert.That(origin.Y, Is.LessThanOrEqualTo(min.Y));
            Assert.That(origin.Z, Is.LessThanOrEqualTo(min.Z));
            Assert.That(wrappedOrigin, Is.EqualTo(new Vector3(-0.25f, -0.75f, 0.0f)));
        });
    }

    [Test]
    public void InfluenceBounds_PlaceTransitionOutsideAuthoredCoreReceivers()
    {
        Vector3 coreMin = new(-3.25f, -0.15f, -8.75f);
        Vector3 coreMax = new(3.25f, 4.25f, -2.25f);
        const float fadeDistance = 1.125f;

        (Vector3 influenceMin, Vector3 influenceMax) =
            SimpleDdgiVolumeManager.ResolveInfluenceBounds(coreMin, coreMax, fadeDistance);
        Vector3 floorReceiver = new(0.0f, 0.0f, -5.5f);
        Vector3 wallReceiver = new(3.0f, 2.0f, -5.5f);
        float floorEdgeDistance = MinimumFaceDistance(floorReceiver, influenceMin, influenceMax);
        float wallEdgeDistance = MinimumFaceDistance(wallReceiver, influenceMin, influenceMax);

        Assert.Multiple(() =>
        {
            Assert.That(influenceMin, Is.EqualTo(new Vector3(-4.375f, -1.275f, -9.875f)));
            Assert.That(influenceMax, Is.EqualTo(new Vector3(4.375f, 5.375f, -1.125f)));
            Assert.That(floorEdgeDistance, Is.GreaterThanOrEqualTo(fadeDistance));
            Assert.That(wallEdgeDistance, Is.GreaterThanOrEqualTo(fadeDistance));
        });
    }

    [Test]
    public void RefinementTraceDistance_IsIndependentOfCompactBrickExtent()
    {
        const float spacing = 0.59375f;
        float compactBrickDistance = SimpleDdgiVolumeManager.ResolveNativeTraceDistance(
            spacing,
            countX: 6,
            countY: 4,
            countZ: 6);
        var inheritedBaseHorizon = new GPUSimpleDdgiVolume
        {
            OriginAndSpacing = new Vector4(0.0f, 0.0f, 0.0f, spacing),
            GridCountsAndFirstProbe = new Vector4(6.0f, 4.0f, 6.0f, 0.0f),
            UpdateStartAndCount = new Vector4(40.375f, 0.0f, 128.0f, 0.0f)
        };
        GPUSimpleDdgiVolume legacyUnencoded = inheritedBaseHorizon;
        legacyUnencoded.UpdateStartAndCount.X = 0.0f;

        Assert.Multiple(() =>
        {
            Assert.That(compactBrickDistance, Is.EqualTo(3.5625f).Within(0.0001f));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveGpuVolumeTraceDistance(inheritedBaseHorizon),
                Is.EqualTo(40.375f).Within(0.0001f));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveGpuVolumeTraceDistance(legacyUnencoded),
                Is.EqualTo(compactBrickDistance).Within(0.0001f));
        });
    }

    [Test]
    public void RefinementTraceDistance_InheritsCompleteBaseOwnerAndUsesBoundaryMaximum()
    {
        Vector3 refinementMinimum = new(-0.296875f, 0.1484375f, 0.4453125f);
        Vector3 refinementMaximum = refinementMinimum +
            new Vector3(2.96875f, 1.78125f, 2.96875f);
        const float nativeDistance = 3.5625f;
        var completeDomains = new[]
        {
            new BoundingBox(
                new Vector3(-19.53125f, -4.28125f, -12.61875f),
                new Vector3(23.21875f, 15.90625f, 17.06875f)),
            new BoundingBox(new Vector3(-100.0f), new Vector3(100.0f))
        };
        float inheritedComplete = SimpleDdgiVolumeManager.ResolveRefinementTraceDistance(
            refinementMinimum,
            refinementMaximum,
            nativeDistance,
            completeDomains,
            [40.375f, 135.0f]);

        var splitDomains = new[]
        {
            new BoundingBox(
                refinementMinimum - Vector3.One,
                new Vector3(1.0f, refinementMaximum.Y + 1.0f, refinementMaximum.Z + 1.0f)),
            new BoundingBox(
                new Vector3(1.0f, refinementMinimum.Y - 1.0f, refinementMinimum.Z - 1.0f),
                refinementMaximum + Vector3.One)
        };
        float inheritedBoundary = SimpleDdgiVolumeManager.ResolveRefinementTraceDistance(
            refinementMinimum,
            refinementMaximum,
            nativeDistance,
            splitDomains,
            [40.375f, 65.79086f]);

        Assert.Multiple(() =>
        {
            Assert.That(inheritedComplete, Is.EqualTo(40.375f).Within(0.0001f));
            Assert.That(inheritedBoundary, Is.EqualTo(65.79086f).Within(0.0001f));
        });
    }

    private static float MinimumFaceDistance(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        Vector3 distance = Vector3.Min(point - minimum, maximum - point);
        return MathF.Min(distance.X, MathF.Min(distance.Y, distance.Z));
    }

    [Test]
    public void SecondVolumeOwnershipEarlyOutThreshold_IsConservativeForEveryInput()
    {
        var settings = new GlobalIlluminationSettings();

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = -1.0f;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(1.0f));

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = 2.0f;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(1.0f));

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = float.NaN;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(1.0f));

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = float.PositiveInfinity;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(1.0f));
    }

    [Test]
    public void ProbeUpdateStride_DispersesAndVisitsEveryProbeExactlyOnce()
    {
        const int probeCount = 6_912;
        int stride = SimpleDdgiVolumeManager.ResolveProbeUpdateStride(probeCount);
        bool[] visited = new bool[probeCount];
        int cursor = 0;
        for (int i = 0; i < probeCount; i++)
        {
            Assert.That(visited[cursor], Is.False, $"duplicate at sequence index {i}");
            visited[cursor] = true;
            cursor = (int)((cursor + (long)stride) % probeCount);
        }

        Assert.Multiple(() =>
        {
            Assert.That(stride, Is.GreaterThan(probeCount / 4));
            Assert.That(visited, Is.All.True);
            Assert.That(cursor, Is.Zero);
        });
    }

    [Test]
    public void ProbeUpdateMetadata_PreservesGenerationAndClampsElapsedAge()
    {
        uint metadata = SimpleDdgiVolumeManager.PackProbeUpdateMetadata(0x00abcdeu, 400u);

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiVolumeManager.ReadProbeUpdateGeneration(metadata), Is.EqualTo(0x00abcdeu));
            Assert.That(SimpleDdgiVolumeManager.ReadProbeUpdateAge(metadata), Is.EqualTo(255u));
        });
    }

    [Test]
    public void Upload_RefreshesVisibilityBeforeThePersistentSchedulerEntries()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));

        int uploadStart = source.IndexOf(
            "public void Upload(",
            StringComparison.Ordinal);
        int uploadEnd = source.IndexOf(
            "public void EnsureDisabled(",
            uploadStart,
            StringComparison.Ordinal);
        Assert.That(uploadStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(uploadEnd, Is.GreaterThan(uploadStart));
        string upload = source[uploadStart..uploadEnd];

        int prepare = upload.IndexOf(
            "PrepareTransportGlobalConvergenceState();",
            StringComparison.Ordinal);
        int importance = upload.IndexOf(
            "int visibleFreshRecoveryBudget = RefreshProbeSchedulingImportance();",
            StringComparison.Ordinal);
        int scheduler = upload.IndexOf(
            "RefreshPersistentSchedulerState();",
            importance,
            StringComparison.Ordinal);
        int evaluate = upload.IndexOf(
            "EvaluateTransportGlobalConvergenceState();",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(prepare, Is.GreaterThanOrEqualTo(0));
            Assert.That(importance, Is.GreaterThan(prepare));
            Assert.That(scheduler, Is.GreaterThan(importance));
            Assert.That(evaluate, Is.GreaterThan(scheduler));
            Assert.That(
                upload.IndexOf("RefreshPersistentSchedulerState();", scheduler + 1, StringComparison.Ordinal),
                Is.EqualTo(-1));
        });
    }

    [TestCase(0u, 128, 128)]
    [TestCase(1u, 128, 1)]
    [TestCase(32u, 128, 32)]
    [TestCase(256u, 128, 128)]
    [TestCase(0u, 0, 1)]
    public void DispatchRayCount_UsesPackedCountAndNeverExceedsTheQueueStride(
        uint packedRayCount,
        int queueRayStride,
        int expected)
    {
        var update = new GPUSimpleDdgiProbeUpdate
        {
            Flags = packedRayCount << 16
        };

        Assert.That(
            SimpleDdgiVolumeManager.ResolveDispatchRayCount(update, queueRayStride),
            Is.EqualTo(expected));
    }

    [Test]
    public void CellAlignedRemap_IncludingWholeVolumeMove_DoesNotRequireAFieldReset()
    {
        var previous = new GPUSimpleDdgiVolume
        {
            OriginAndSpacing = new Vector4(0f, 0f, 0f, 1f),
            GridCountsAndFirstProbe = new Vector4(4f, 3f, 2f, 0f),
            WorldMaxAndKind = new Vector4(0f, 0f, 0f, 3f),
            RaysAndReserved = new Vector4(30_000f, 0f, 0f, 0f)
        };
        GPUSimpleDdgiVolume oneCell = previous;
        oneCell.OriginAndSpacing.X = 1f;
        GPUSimpleDdgiVolume wholeVolume = previous;
        wholeVolume.OriginAndSpacing.X = 4f;
        GPUSimpleDdgiVolume fractional = previous;
        fractional.OriginAndSpacing.X = 0.5f;

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.IsCompatibleVolumeRemap(
                    previous,
                    oneCell),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.IsCompatibleVolumeRemap(
                    previous,
                    wholeVolume),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.IsCompatibleVolumeRemap(
                    previous,
                    fractional),
                Is.False);
        });
    }

    [TestCase(false, true, true, true, SimpleDdgiVolumeRemapKind.None)]
    [TestCase(true, true, true, true,
        SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll)]
    [TestCase(true, false, true, true,
        SimpleDdgiVolumeRemapKind.IncompatibleTopologyChange)]
    [TestCase(true, true, false, true,
        SimpleDdgiVolumeRemapKind.IncompatibleTopologyChange)]
    [TestCase(true, true, true, false,
        SimpleDdgiVolumeRemapKind.IncompatibleTopologyChange)]
    public void VolumeRemapClassification_OnlyCompatibleScrollPreservesFieldEvidence(
        bool remapped,
        bool toroidal,
        bool topologyCountsMatch,
        bool allVolumesCompatible,
        SimpleDdgiVolumeRemapKind expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveVolumeRemapKind(
                remapped,
                toroidal,
                topologyCountsMatch,
                allVolumesCompatible),
            Is.EqualTo(expected));
    }

    [Test]
    public void CompatibleToroidalScroll_RepairsExposedSlabsWithoutOpeningGlobalConvergence()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldBeginTransportGlobalConvergenceForInvalidation(
                        transportV2Active: true,
                        newlyInvalidatedProbeCount: 2_576,
                        hasRegionalDirtyWork: false,
                        requiresGlobalInvalidation: false,
                        atlasFresh: false,
                        recenteredThisFrame: true,
                        remapKind: SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll),
                Is.False,
                "a compatible camera scroll must retain the current field solve");
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldBeginTransportGlobalConvergenceForInvalidation(
                        transportV2Active: true,
                        newlyInvalidatedProbeCount: 2_576,
                        hasRegionalDirtyWork: false,
                        requiresGlobalInvalidation: true,
                        atlasFresh: false,
                        recenteredThisFrame: true,
                        remapKind: SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll),
                Is.True,
                "a simultaneous global source boundary must still restart convergence");
            Assert.That(
                SimpleDdgiVolumeManager
                    .ShouldBeginTransportGlobalConvergenceForInvalidation(
                        transportV2Active: true,
                        newlyInvalidatedProbeCount: 2_576,
                        hasRegionalDirtyWork: false,
                        requiresGlobalInvalidation: false,
                        atlasFresh: true,
                        recenteredThisFrame: true,
                        remapKind: SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll),
                Is.True,
                "an atlas bootstrap cannot reuse scroll history");
        });
    }

    [TestCase(
        SimpleDdgiSchedulerMode.GpuResident,
        SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll,
        true,
        SimpleDdgiTransportPhase.AuditFrozen,
        true)]
    [TestCase(
        SimpleDdgiSchedulerMode.CpuReference,
        SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll,
        true,
        SimpleDdgiTransportPhase.AuditFrozen,
        false)]
    [TestCase(
        SimpleDdgiSchedulerMode.GpuResident,
        SimpleDdgiVolumeRemapKind.None,
        true,
        SimpleDdgiTransportPhase.AuditFrozen,
        false)]
    [TestCase(
        SimpleDdgiSchedulerMode.GpuResident,
        SimpleDdgiVolumeRemapKind.IncompatibleTopologyChange,
        true,
        SimpleDdgiTransportPhase.AuditFrozen,
        false)]
    [TestCase(
        SimpleDdgiSchedulerMode.GpuResident,
        SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll,
        false,
        SimpleDdgiTransportPhase.AuditFrozen,
        false)]
    [TestCase(
        SimpleDdgiSchedulerMode.GpuResident,
        SimpleDdgiVolumeRemapKind.CompatibleToroidalScroll,
        true,
        SimpleDdgiTransportPhase.AcceleratedSolve,
        false)]
    public void CompatibleGpuResidentScroll_PreemptsOnlyAFrozenTailAudit(
        SimpleDdgiSchedulerMode schedulerMode,
        SimpleDdgiVolumeRemapKind remapKind,
        bool tailCertificationEnabled,
        SimpleDdgiTransportPhase phase,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager
                .ShouldPreemptFrozenTransportAuditForCompatibleScroll(
                    schedulerMode,
                    remapKind,
                    tailCertificationEnabled,
                    phase),
            Is.EqualTo(expected));
    }

    [Test]
    public void LivePropagationBoundary_SurvivesOnlyTheSameSourceGeneration()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.HasCurrentLivePropagationBoundary(9u, 9u),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.HasCurrentLivePropagationBoundary(9u, 10u),
                Is.False,
                "a lighting boundary must retire the old live solve witness");
            Assert.That(
                SimpleDdgiVolumeManager.HasCurrentLivePropagationBoundary(0u, 1u),
                Is.False);
        });
    }

    [Test]
    public void LivePropagationBoundary_AcceptsCurrentPublishedSweepWhileAuditCountDrifts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.CanEstablishLivePropagationBoundary(
                    SimpleDdgiTransportPhase.AcceleratedSolve,
                    currentSolveEpoch: 7u,
                    feedbackSolveEpoch: 7u,
                    participantCount: 14_779u,
                    visitedParticipantCount: 6_656u,
                    publishedCount: 512u,
                    blockingSourceWork: false),
                Is.True,
                "a source-clean current-epoch publication is usable while the exact audit count catches up");
            Assert.That(
                SimpleDdgiVolumeManager.CanEstablishLivePropagationBoundary(
                    SimpleDdgiTransportPhase.AcceleratedSolve,
                    currentSolveEpoch: 7u,
                    feedbackSolveEpoch: 7u,
                    participantCount: 14_779u,
                    visitedParticipantCount: 14_779u,
                    publishedCount: 0u,
                    blockingSourceWork: false),
                Is.True,
                "an already-published complete participant sweep remains a live witness");
            Assert.That(
                SimpleDdgiVolumeManager.CanEstablishLivePropagationBoundary(
                    SimpleDdgiTransportPhase.AcceleratedSolve,
                    currentSolveEpoch: 7u,
                    feedbackSolveEpoch: 7u,
                    participantCount: 14_779u,
                    visitedParticipantCount: 6_656u,
                    publishedCount: 512u,
                    blockingSourceWork: true),
                Is.False,
                "unrepaired source probes must keep a new lighting generation behind the boundary");
            Assert.That(
                SimpleDdgiVolumeManager.CanEstablishLivePropagationBoundary(
                    SimpleDdgiTransportPhase.AcceleratedSolve,
                    currentSolveEpoch: 7u,
                    feedbackSolveEpoch: 6u,
                    participantCount: 14_779u,
                    visitedParticipantCount: 14_779u,
                    publishedCount: 512u,
                    blockingSourceWork: false),
                Is.False,
                "a delayed solve epoch cannot authorize the current field");
        });
    }

    [Test]
    public void ViewForwardPlacement_MatchesFlaxCardinalCoverageBias()
    {
        Vector3 direction =
            SimpleDdgiVolumeManager.ResolveViewForwardPlacementDirection(
                new Vector3(0f, -0.25f, -4f),
                horizontalOnly: true);
        float offset = SimpleDdgiVolumeManager.ResolveViewForwardPlacementOffset(
            new Vector3(26f, 12f, 26f),
            direction,
            placementFraction: 0.6f);

        Assert.Multiple(() =>
        {
            Assert.That(direction.X, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(direction.Y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(direction.Z, Is.EqualTo(-1f).Within(0.0001f));
            // A cardinal ray exits the Flax box at 0.5 of its ray length:
            // 0.5 * 26 * 0.6 = 7.8, leaving 20% behind and 80% ahead.
            Assert.That(offset, Is.EqualTo(7.8f).Within(0.0001f));
        });
    }

    [Test]
    public void CameraRelativeRingOrigin_FollowsEachHorizontalCellLikeFlax()
    {
        bool hasOrigin = false;
        Vector3 sceneMin = new(-100f, -5f, -100f);
        Vector3 sceneMax = new(100f, 20f, 100f);
        Vector3 latticeSize = new(23.625f, 11.375f, 23.625f);
        const float spacing = 0.875f;

        Vector3 initial = SimpleDdgiVolumeManager.ResolveCameraRelativeRingOrigin(
            sceneMin,
            sceneMax,
            latticeSize,
            spacing,
            placementCenter: new Vector3(0.10f, 2.0f, 0.10f),
            currentOrigin: default,
            ref hasOrigin,
            out bool initiallyRecentered);
        Vector3 insideCell = SimpleDdgiVolumeManager.ResolveCameraRelativeRingOrigin(
            sceneMin,
            sceneMax,
            latticeSize,
            spacing,
            placementCenter: new Vector3(0.80f, 2.0f, 0.80f),
            currentOrigin: initial,
            ref hasOrigin,
            out bool insideCellRecentered);
        Vector3 nextCell = SimpleDdgiVolumeManager.ResolveCameraRelativeRingOrigin(
            sceneMin,
            sceneMax,
            latticeSize,
            spacing,
            placementCenter: new Vector3(0.90f, 2.0f, 0.90f),
            currentOrigin: insideCell,
            ref hasOrigin,
            out bool nextCellRecentered);

        Assert.Multiple(() =>
        {
            Assert.That(initiallyRecentered, Is.False);
            Assert.That(insideCellRecentered, Is.False);
            Assert.That(insideCell, Is.EqualTo(initial));
            Assert.That(nextCellRecentered, Is.True);
            Assert.That(nextCell.X - initial.X, Is.EqualTo(spacing).Within(1e-5f));
            Assert.That(nextCell.Z - initial.Z, Is.EqualTo(spacing).Within(1e-5f));
            Assert.That(
                nextCell.X + latticeSize.X * 0.5f,
                Is.EqualTo(spacing).Within(1e-5f),
                "the physical lattice centre must be the floor-snapped camera-relative centre");
        });
    }

    [Test]
    public void SmoothBlendOrigin_FollowsSubCellCameraMotionAndClampsToScene()
    {
        Vector3 interior = SimpleDdgiVolumeManager.ResolveSmoothSceneClampedOrigin(
            Vector3.Zero,
            new Vector3(100f, 40f, 100f),
            new Vector3(20f, 10f, 20f),
            new Vector3(10.25f, 7.75f, 30.5f));
        Vector3 edge = SimpleDdgiVolumeManager.ResolveSmoothSceneClampedOrigin(
            Vector3.Zero,
            new Vector3(100f, 40f, 100f),
            new Vector3(20f, 10f, 20f),
            new Vector3(99f, -4f, 2f));

        Assert.Multiple(() =>
        {
            Assert.That(interior.X, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(interior.Y, Is.EqualTo(2.75f).Within(0.0001f));
            Assert.That(interior.Z, Is.EqualTo(20.5f).Within(0.0001f));
            Assert.That(edge.X, Is.EqualTo(80f).Within(0.0001f));
            Assert.That(edge.Y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(edge.Z, Is.EqualTo(0f).Within(0.0001f));
        });
    }

    [Test]
    public void LayoutFingerprint_ExcludesRuntimeWorldPlacement()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        int start = source.IndexOf(
            "private static ulong CalculateLayoutFingerprint(",
            StringComparison.Ordinal);
        int end = source.IndexOf(
            "private static ulong CalculateTransportSourceCalibrationFingerprint(",
            start,
            StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        string method = source[start..end];

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain("if (!includeCandidateTopology)"));
            Assert.That(method, Does.Contain("candidate.CountX"));
            Assert.That(method, Does.Contain("candidate.Spacing"));
            Assert.That(method, Does.Not.Contain("candidate.Origin"));
            Assert.That(method, Does.Not.Contain("candidate.WorldMin"));
            Assert.That(method, Does.Not.Contain("candidate.WorldMax"));
        });
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, true)]
    public void SampledMirrorRemap_DoesNotClearTheCanonicalAtlas(
        bool storageContractChanged,
        bool sampledMappingChanged,
        bool expected)
    {
        Assert.That(
            SimpleDdgiVolumeManager.RequiresCanonicalAtlasClear(
                storageContractChanged,
                sampledMappingChanged),
            Is.EqualTo(expected));
    }

    [Test]
    public void BuildVolumes_AlwaysIncludesAuthoredOwnershipUnderTransportV2()
    {
        string source = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs"));
        int buildStart = source.IndexOf(
            "private void BuildVolumeTable(",
            StringComparison.Ordinal);
        int buildEnd = source.IndexOf(
            "private SimpleDdgiProbeResidencyMode ResolveProbeResidencyMode(",
            buildStart,
            StringComparison.Ordinal);
        Assert.That(buildStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(buildEnd, Is.GreaterThan(buildStart));
        string build = source[buildStart..buildEnd];

        int settingsAuthored = build.IndexOf(
            "foreach (SimpleDdgiAuthoredVolume authored in gi.SimpleDdgiAuthoredVolumes)",
            StringComparison.Ordinal);
        int sceneAuthored = build.IndexOf(
            "authoredSceneVolumes.Count",
            StringComparison.Ordinal);
        int rings = build.IndexOf(
            "int ringCount = gi.SimpleDdgiRingCount;",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(settingsAuthored, Is.GreaterThanOrEqualTo(0));
            Assert.That(sceneAuthored, Is.GreaterThan(settingsAuthored));
            Assert.That(rings, Is.GreaterThan(sceneAuthored));
            Assert.That(build, Does.Not.Contain(
                "if (!gi.SimpleDdgiTransportV2Enabled)"));
            Assert.That(build, Does.Contain(
                "AdmissionClass = candidate.Kind == VolumeKindRefinement"));
        });
    }

    private static string FindSourceFile(params string[] relativeParts)
    {
        string directory = TestContext.CurrentContext.TestDirectory;
        for (int depth = 0; depth < 8; depth++)
        {
            string candidate = Path.Combine(directory, Path.Combine(relativeParts));
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(relativeParts));
    }
}
