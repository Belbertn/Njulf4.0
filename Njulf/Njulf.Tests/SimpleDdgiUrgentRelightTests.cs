using System;
using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiUrgentRelightTests
{
    [Test]
    public void Policy_AdmitsOnlyVisibleNearRingCacheRelights()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.EnvironmentMissRelight), Is.True);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.True);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.FullTrace), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.SegmentSelective), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.None), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, visible: false), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, ringIndex: 1), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, cachedGeometryReady: false), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, regionalDirty: true), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, topologyInvalid: true), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, scrollExposed: true), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, atlasFresh: true), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, sourceInvalid: true), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, relocationPending: true), Is.False);
            Assert.That(Eligible(SimpleDdgiSourceRefreshMode.CachedHitRelight, inactive: true), Is.False);
        });
    }

    [Test]
    public void CoherentPublicationPolicy_RequiresResidentRadiometricPrivateStorage()
    {
        static bool Defer(
            SimpleDdgiSchedulerMode schedulerMode,
            bool transportV2,
            SimpleDdgiSourceRefreshMode refreshMode,
            SimpleDdgiDirectionalRadianceMode directionalMode =
                SimpleDdgiDirectionalRadianceMode.Off,
            bool directionalStorageAvailable = true) =>
            SimpleDdgiVolumeManager.ShouldDeferRadiometricPublication(
                schedulerMode,
                transportV2,
                refreshMode,
                directionalMode,
                directionalStorageAvailable);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiSchedulerAbi
                    .SchedulerFeatureDeferRadiometricPublication,
                Is.EqualTo(1u << 17));
            Assert.That(Defer(
                SimpleDdgiSchedulerMode.GpuResident,
                true,
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight), Is.True);
            Assert.That(Defer(
                SimpleDdgiSchedulerMode.GpuResident,
                true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.True);
            Assert.That(Defer(
                SimpleDdgiSchedulerMode.GpuResident,
                true,
                SimpleDdgiSourceRefreshMode.FullTrace), Is.False);
            Assert.That(Defer(
                SimpleDdgiSchedulerMode.GpuMirror,
                true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.False);
            Assert.That(Defer(
                SimpleDdgiSchedulerMode.GpuResident,
                false,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.False);
            Assert.That(Defer(
                SimpleDdgiSchedulerMode.GpuResident,
                true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight,
                SimpleDdgiDirectionalRadianceMode.L2,
                directionalStorageAvailable: false), Is.False);
            Assert.That(Defer(
                SimpleDdgiSchedulerMode.GpuResident,
                true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight,
                SimpleDdgiDirectionalRadianceMode.L2,
                directionalStorageAvailable: true), Is.True);
        });
    }

    [Test]
    public void CoherentPublication_FreezesTopologyBeforeTheIncomingGenerationStarts()
    {
        static bool Freeze(
            bool pending,
            bool initialized,
            bool changed,
            SimpleDdgiSourceRefreshMode mode,
            SimpleDdgiSchedulerMode schedulerMode =
                SimpleDdgiSchedulerMode.GpuResident,
            bool transportV2 = true,
            SimpleDdgiDirectionalRadianceMode directionalMode =
                SimpleDdgiDirectionalRadianceMode.Off,
            bool directionalStorageAvailable = true) =>
            SimpleDdgiVolumeManager
                .ShouldFreezeVolumeTopologyForRadiometricPublication(
                    pending,
                    initialized,
                    changed,
                    schedulerMode,
                    transportV2,
                    mode,
                    directionalMode,
                    directionalStorageAvailable);

        Assert.Multiple(() =>
        {
            Assert.That(Freeze(
                pending: true,
                initialized: false,
                changed: false,
                SimpleDdgiSourceRefreshMode.FullTrace), Is.True);
            Assert.That(Freeze(
                pending: false,
                initialized: true,
                changed: true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.True);
            Assert.That(Freeze(
                pending: false,
                initialized: true,
                changed: true,
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight), Is.True);
            Assert.That(Freeze(
                pending: false,
                initialized: true,
                changed: true,
                SimpleDdgiSourceRefreshMode.FullTrace), Is.False);
            Assert.That(Freeze(
                pending: false,
                initialized: true,
                changed: false,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.False);
            Assert.That(Freeze(
                pending: false,
                initialized: false,
                changed: true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.False);
            Assert.That(Freeze(
                pending: false,
                initialized: true,
                changed: true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight,
                schedulerMode: SimpleDdgiSchedulerMode.GpuMirror), Is.False);
            Assert.That(Freeze(
                pending: false,
                initialized: true,
                changed: true,
                SimpleDdgiSourceRefreshMode.CachedHitRelight,
                directionalMode: SimpleDdgiDirectionalRadianceMode.L2,
                directionalStorageAvailable: false), Is.False);
        });
    }

    [Test]
    public void OverlappingCachedRelight_EscalatesOnlyBeforeCoherentPublication()
    {
        static bool Escalate(
            bool complete,
            bool coherentlyPublished,
            SimpleDdgiSourceRefreshMode current,
            SimpleDdgiSourceRefreshMode requested) =>
            SimpleDdgiVolumeManager.ShouldEscalateOverlappingCachedHitRelight(
                complete,
                coherentlyPublished,
                current,
                requested);

        Assert.Multiple(() =>
        {
            Assert.That(Escalate(
                complete: false,
                coherentlyPublished: false,
                SimpleDdgiSourceRefreshMode.None,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.True);
            Assert.That(Escalate(
                complete: false,
                coherentlyPublished: true,
                SimpleDdgiSourceRefreshMode.None,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.False);
            Assert.That(Escalate(
                complete: true,
                coherentlyPublished: false,
                SimpleDdgiSourceRefreshMode.CachedHitRelight,
                SimpleDdgiSourceRefreshMode.CachedHitRelight), Is.False);
            Assert.That(Escalate(
                complete: false,
                coherentlyPublished: false,
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight,
                SimpleDdgiSourceRefreshMode.FullTrace), Is.False);
            Assert.That(Escalate(
                complete: false,
                coherentlyPublished: false,
                SimpleDdgiSourceRefreshMode.CachedHitRelight,
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight), Is.True);
        });
    }

    [TestCase(-1, 0u)]
    [TestCase(0, 0u)]
    [TestCase(1, 1u)]
    [TestCase(32, 32u)]
    [TestCase(255, 255u)]
    [TestCase(256, 255u)]
    [TestCase(int.MaxValue, 255u)]
    public void Budget_IsBoundedToTelemetryCardinality(
        int configuredBudget,
        uint expectedBudget)
    {
        Assert.That(
            SimpleDdgiUrgentRelightPolicy.ResolveBudget(configuredBudget),
            Is.EqualTo(expectedBudget));
    }

    [Test]
    public void SweepPolicy_IsEvidenceBoundedAndFailsClosed()
    {
        static int Resolve(
            float residual = 0.08f,
            float tolerance = 0.01f,
            float contraction = 0.5f,
            long frame = 10_000L,
            long target = 12_000L,
            long sweep = 100L,
            int maximum = 4) =>
            SimpleDdgiUrgentRelightPolicy.ResolveSweepCount(
                maximum,
                residual,
                tolerance,
                contraction,
                frame,
                target,
                sweep);

        Assert.Multiple(() =>
        {
            Assert.That(Resolve(), Is.EqualTo(4));
            Assert.That(Resolve(residual: 0.015f), Is.EqualTo(2));
            Assert.That(Resolve(frame: 11_850L), Is.EqualTo(2));
            Assert.That(Resolve(frame: 12_000L), Is.EqualTo(1));
            Assert.That(Resolve(sweep: 0L), Is.EqualTo(1));
            Assert.That(Resolve(residual: float.NaN), Is.EqualTo(1));
            Assert.That(Resolve(contraction: 1.0f), Is.EqualTo(1));
            Assert.That(Resolve(maximum: 99), Is.LessThanOrEqualTo(4));
        });
    }

    [Test]
    public void Telemetry_IsFrameStampedAndCannotReportMoreCommitsThanAdmissions()
    {
        const uint frame = 0x12345u;
        uint packed = SimpleDdgiUrgentRelightPolicy.PackTelemetry(
            frame,
            acceptedProbeCount: 300u,
            committedProbeCount: 290u);
        SimpleDdgiUrgentRelightEvidence current =
            SimpleDdgiUrgentRelightPolicy.UnpackTelemetry(packed, frame);
        SimpleDdgiUrgentRelightEvidence stale =
            SimpleDdgiUrgentRelightPolicy.UnpackTelemetry(packed, frame + 1u);
        SimpleDdgiUrgentRelightEvidence malformed =
            SimpleDdgiUrgentRelightPolicy.UnpackTelemetry(
                SimpleDdgiUrgentRelightPolicy.PackTelemetry(frame, 3u, 9u),
                frame);

        Assert.Multiple(() =>
        {
            Assert.That(current.AcceptedProbeCount, Is.EqualTo(255u));
            Assert.That(current.CommittedProbeCount, Is.EqualTo(255u));
            Assert.That(current.RejectedProbeCount, Is.Zero);
            Assert.That(stale, Is.EqualTo(default(SimpleDdgiUrgentRelightEvidence)));
            Assert.That(malformed.AcceptedProbeCount, Is.EqualTo(3u));
            Assert.That(malformed.CommittedProbeCount, Is.EqualTo(3u));
        });
    }

    [Test]
    public void Settings_DefaultClampAndRoundTripPreserveUrgentLanePolicy()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"simple-ddgi-urgent-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            Assert.Multiple(() =>
            {
                Assert.That(settings.GlobalIllumination.SimpleDdgiUrgentRelightEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.SimpleDdgiUrgentRelightProbeBudget, Is.EqualTo(32));
            });

            settings.GlobalIllumination.SimpleDdgiUrgentRelightEnabled = false;
            settings.GlobalIllumination.SimpleDdgiUrgentRelightProbeBudget = 10_000;
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiUrgentRelightProbeBudget,
                Is.EqualTo(SimpleDdgiUrgentRelightPolicy.MaximumProbeBudget));
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.GlobalIllumination.SimpleDdgiUrgentRelightEnabled, Is.False);
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiUrgentRelightProbeBudget,
                    Is.EqualTo(SimpleDdgiUrgentRelightPolicy.MaximumProbeBudget));
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void ShaderContract_KeepsUrgentPublicationPrivateUntilCommit()
    {
        string schedule = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_shared.glsl");
        string trace = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_trace.comp");
        string commit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_commit_local.comp");
        string feedback = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_feedback.comp");
        string sampled = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_publish_sampled.comp");
        string pass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiUrgentRelightPass.cs");
        string scheduler = ReadRepoText(
            "Njulf.Rendering", "Resources", "SimpleDdgiGpuScheduler.cs");

        Assert.Multiple(() =>
        {
            Assert.That(schedule, Does.Contain("SIMPLE_DDGI_SCHEDULER_COUNTER_URGENT_CONTROL = 95u"));
            Assert.That(schedule, Does.Contain("SIMPLE_DDGI_SCHEDULER_TRANSIENT_COUNTER_WORDS = 94u"));
            Assert.That(trace, Does.Contain("SIMPLE_DDGI_TRACE_FLAG_PRIVATE_URGENT_RELIGHT"));
            Assert.That(trace, Does.Contain("if (!privateUrgentRelight)"));
            Assert.That(commit, Does.Contain("SIMPLE_DDGI_SCHEDULER_COMPLETE_URGENT_COMMIT"));
            Assert.That(feedback, Does.Contain("Consume the mailbox after copying it"));
            Assert.That(sampled, Does.Contain("COMPLETE_URGENT_COMMIT"));
            Assert.That(pass, Does.Contain("ExecuteCacheReuseOnly"));
            Assert.That(pass, Does.Contain("ExecuteCanonicalOnly"));
            Assert.That(pass, Does.Contain(
                "ExecuteResidentLocalAndPropagation"));
            Assert.That(pass, Does.Contain(
                "seed the existing sparse-residual queue"));
            Assert.That(pass, Does.Contain("ExecuteSampledOnly"));
            Assert.That(pass, Does.Not.Contain("AccelerationStructure"));
            Assert.That(scheduler, Does.Contain("_layout.Counters.Offset"));
            Assert.That(scheduler, Does.Contain("_layout.Counters.ByteSize"));
            Assert.That(scheduler, Does.Contain(
                "allocation residue in word 95 can permanently make every ordinary"));
        });
    }

    [Test]
    public void CoherentRadiometricPublication_UsesOneFenceCompleteFieldFlip()
    {
        string schedule = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_shared.glsl");
        string shared = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_shared.glsl");
        string commit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_commit_local.comp");
        string feedback = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_feedback.comp");
        string sampled = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_publish_sampled.comp");
        string directional = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_directional_publish.comp");
        string manager = ReadRepoText(
            "Njulf.Rendering", "Resources", "SimpleDdgiVolumeManager.cs");
        string accelerated = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiAcceleratedSolvePass.cs");
        string directionalPass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiDirectionalRadiancePass.cs");
        string urgent = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiUrgentRelightPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(schedule, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEATURE_DEFER_RADIOMETRIC_PUBLICATION"));
            Assert.That(schedule, Does.Contain(
                "bool SchedulerDefersRadiometricPublication()"));
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SOLVE_SINGLE_SWEEP"));
            Assert.That(commit, Does.Contain(
                "bool deferredRadiometricPublication = !urgentRelight"));
            Assert.That(commit, Does.Not.Contain(
                "SchedulerUpdateUsesRadiometricRelight(updateFlags)"));
            Assert.That(commit, Does.Contain(
                "if (SchedulerGpuResident() && !deferredRadiometricPublication)"));
            Assert.That(feedback, Does.Contain(
                "!SchedulerDefersRadiometricPublication()"));
            Assert.That(sampled, Does.Contain(
                "The canonical SSBO and this optional image mirror must cross"));
            Assert.That(directional, Does.Contain(
                "bool DeferSimpleDdgiDirectionalPublication("));
            Assert.That(manager, Does.Contain(
                "PublishDeferredRadiometricGenerationIfReady(commandBuffer);"));
            Assert.That(manager, Does.Contain(
                "ShouldFreezeVolumeTopologyForRadiometricPublication("));
            Assert.That(manager, Does.Contain(
                "_freezeVolumeTopologyForRadiometricPublicationThisFrame &&"));
            Assert.That(manager, Does.Contain(
                "_bufferManager.GetBuffer(_transportIrradianceAtlasBuffer)"));
            Assert.That(manager, Does.Contain(
                "_sampledAtlas?.MarkFullSyncRequired();"));
            Assert.That(manager, Does.Contain(
                "_gpuSchedulerLaneCursorResetPending = true;"));
            Assert.That(accelerated, Does.Contain(
                "if (!deferredRadiometricPublication)"));
            Assert.That(accelerated, Does.Contain(
                "baseFlags |= SolveSingleSweepFlag"));
            Assert.That(directional, Does.Contain(
                "SIMPLE_DDGI_DIRECTIONAL_DEFER_RADIOMETRIC_PUBLICATION"));
            Assert.That(directionalPass, Does.Contain(
                "VolumeManager.RadiometricRelightPublicationPending"));
            Assert.That(urgent, Does.Contain(
                "!_volumeManager.RadiometricRelightPublicationPending"));
        });
    }

    private static bool Eligible(
        SimpleDdgiSourceRefreshMode mode,
        bool cachedGeometryReady = true,
        bool visible = true,
        int ringIndex = 0,
        bool regionalDirty = false,
        bool topologyInvalid = false,
        bool scrollExposed = false,
        bool atlasFresh = false,
        bool sourceInvalid = false,
        bool relocationPending = false,
        bool inactive = false) =>
        SimpleDdgiUrgentRelightPolicy.IsEligible(
            mode,
            cachedGeometryReady,
            visible,
            ringIndex,
            regionalDirty,
            topologyInvalid,
            scrollExposed,
            atlasFresh,
            sourceInvalid,
            relocationPending,
            inactive);

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, segments));
    }
}
