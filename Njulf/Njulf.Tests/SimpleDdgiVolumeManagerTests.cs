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
    public void SimpleDdgiDebugView_MapsSourceCacheToForwardShaderAbi()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ResolveSimpleDdgiDebugViewMode(GlobalIlluminationDebugView.DdgiSourceCacheRadiance),
                Is.EqualTo(SimpleDdgiVolumeManager.SourceCacheRadianceDebugViewMode));
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
    public void BufferResizes_SynchronizeOrDeferOldGpuBuffersBeforeDestruction()
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
            "RetireBufferResource(previousHandle);",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("BeginFrameResourceRetirement();"));
            Assert.That(source, Does.Contain("RenderingConstants.FramesInFlight + 1UL"));
            Assert.That(source, Does.Contain("_context.WaitIdle();"));
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

    [TestCase(15_354, 0, 0, true)]
    [TestCase(15_354, 1, 0, true)]
    [TestCase(15_354, 14, 0, true)]
    [TestCase(15_354, 15, 0, false)]
    [TestCase(15_354, 0, 1, false)]
    [TestCase(999, 1, 0, true)]
    [TestCase(999, 2, 0, false)]
    [TestCase(1_000, 1, 0, true)]
    [TestCase(0, 0, 0, true)]
    public void GlobalTransportConvergence_AllowsOnlyTheBoundedSourceRepairTail(
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
    public void Renderer_PreparesOnlyTheSelectedDdgiBackendAndPublishesCurrentEmissiveRevision()
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
            Assert.That(method, Does.Contain("if (ddgiActive)\n            {\n                _ddgiProbeVolumeManager.Upload("));
            Assert.That(method, Does.Contain("if (simpleDdgiActive)\n            {\n                SimpleDdgiDirtySignature"));
            Assert.That(method, Does.Contain("if (!ddgiActive)\n            {\n                _ddgiProbeVolumeManager.ClearGpuSchedulerValidationExpectedFrame"));
            Assert.That(method, Does.Not.Contain("legacy DDGI upload above is intentionally overwritten"));
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
            Assert.That(source, Does.Contain("_simpleDdgiVolumeManager.ReportSchedulingFeedback"));
            Assert.That(source, Does.Contain("EffectiveDdgiAdaptiveBudgetTimeMilliseconds"));
            Assert.That(source, Does.Contain("detailedDdgiInstrumentationActive"));
            Assert.That(source, Does.Contain("fixedSimpleDdgiBudget || hasCompletedSimpleDdgiGpuTiming"));
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
                Does.Contain("sceneData.DdgiBufferBytes = _simpleDdgiVolumeManager.BufferBytes;"));
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

    private static float MinimumFaceDistance(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        Vector3 distance = Vector3.Min(point - minimum, maximum - point);
        return MathF.Min(distance.X, MathF.Min(distance.Y, distance.Z));
    }

    [Test]
    public void SecondVolumeOwnershipEarlyOutThreshold_ClampsFiniteValuesAndFallsBackForNonFiniteValues()
    {
        var settings = new GlobalIlluminationSettings();

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = -1.0f;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.Zero);

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = 2.0f;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(1.0f));

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = float.NaN;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(0.95f));

        settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = float.PositiveInfinity;
        Assert.That(settings.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold, Is.EqualTo(0.95f));
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
