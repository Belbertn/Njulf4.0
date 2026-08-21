using System;
using System.IO;
using Njulf.Rendering.Data;
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
        string relocate = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_relocate_classify.comp");
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
            Assert.That(relocate, Does.Contain(
                "bool inactiveProbe = relocationTimedOut || (radiometricRelight"));
            Assert.That(relocate, Does.Contain(
                "previous.classification == SIMPLE_DDGI_CLASSIFICATION_INACTIVE"));
            Assert.That(pass, Does.Contain("ExecuteCacheReuseOnly"));
            Assert.That(pass, Does.Contain("ExecuteCanonicalOnly"));
            Assert.That(pass, Does.Contain("ExecuteResidentLocalOnly"));
            Assert.That(pass, Does.Contain("ExecuteSampledOnly"));
            Assert.That(pass, Does.Not.Contain("AccelerationStructure"));
            Assert.That(scheduler, Does.Contain("_layout.Counters.Offset"));
            Assert.That(scheduler, Does.Contain("_layout.Counters.ByteSize"));
            Assert.That(scheduler, Does.Contain(
                "allocation residue in word 95 can permanently make every ordinary"));
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
