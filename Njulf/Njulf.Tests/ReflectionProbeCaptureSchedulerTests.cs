using System;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ReflectionProbeCaptureSchedulerTests
{
    [Test]
    public void RecaptureRetainsPublishedCaptureUntilGpuCompletion()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(4);
        Guid id = Guid.NewGuid();
        var oldVersion = new ReflectionCaptureVersion(1, 1, 1, 1, 1, 1, 1);
        var nextVersion = oldVersion with { AdmittedEnvironmentGeneration = 2 };
        scheduler.Register(0, id, hasPublishedCapture: true, oldVersion);
        scheduler.Request(0, id, nextVersion, ReflectionCaptureReason.EnvironmentChanged,
            default, resourceGeneration: 4, sceneRevision: 2);

        Assert.That(scheduler.HasPublishedCapture(0, id), Is.True);
        for (int face = 0; face < 6; face++)
        {
            Assert.That(scheduler.TryAcquireWork(4, 1, 1, out ReflectionProbeWork work), Is.True);
            Assert.That(work.Kind, Is.EqualTo(ReflectionProbeWorkKind.CaptureFace));
            Assert.That(work.Face, Is.EqualTo(face));
            scheduler.CompleteWork(work);
        }
        for (int mip = 1; mip < 4; mip++)
        {
            Assert.That(scheduler.TryAcquireWork(4, 1, 1, out ReflectionProbeWork work), Is.True);
            Assert.That(work.Kind, Is.EqualTo(ReflectionProbeWorkKind.PrefilterMip));
            Assert.That(work.Mip, Is.EqualTo(mip));
            scheduler.CompleteWork(work);
        }
        Assert.That(scheduler.TryAcquireWork(4, 1, 1, out ReflectionProbeWork copy), Is.True);
        Assert.That(copy.Kind, Is.EqualTo(ReflectionProbeWorkKind.PublishCopy));
        scheduler.MarkCopySubmitted(copy, 42);
        Assert.That(scheduler.TryPublishCompleted(41, out _), Is.False);
        Assert.That(scheduler.HasPublishedCapture(0, id), Is.True);
        Assert.That(scheduler.TryPublishCompleted(42, out ReflectionProbeCaptureTicket completed), Is.True);
        Assert.That(completed.Version, Is.EqualTo(nextVersion));
        Assert.That(scheduler.CapturesPublishedTotal, Is.EqualTo(1));
    }

    [Test]
    public void SceneRevisionTracksAddMutationAndRemove()
    {
        using var scene = new Scene();
        var probe = new ReflectionProbe();
        scene.Add(probe);
        uint added = scene.ReflectionProbeRevision;
        probe.Priority++;
        uint mutated = scene.ReflectionProbeRevision;
        scene.Remove(probe);
        Assert.Multiple(() =>
        {
            Assert.That(added, Is.Not.Zero);
            Assert.That(mutated, Is.Not.EqualTo(added));
            Assert.That(scene.ReflectionProbeRevision, Is.Not.EqualTo(mutated));
        });
    }

    [Test]
    public void DeferredWork_RespectsCoolingFrameWithoutBurningRetryBudget()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1, retryLimit: 1);
        Guid id = Guid.NewGuid();
        var version = new ReflectionCaptureVersion(1, 1, 1, 1, 1, 1, 1);
        scheduler.Register(0, id, hasPublishedCapture: false);
        scheduler.Request(0, id, version, ReflectionCaptureReason.SceneChanged, default, 1, 1);

        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork work, currentFrame: 0UL), Is.True);
        scheduler.DeferActive(work, currentFrame: 0UL, deferFrames: 2UL);
        Assert.Multiple(() =>
        {
            Assert.That(scheduler.TryAcquireWork(2, 1, 1, out _, currentFrame: 1UL), Is.False);
            Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork retried, currentFrame: 2UL), Is.True);
            Assert.That(retried.Kind, Is.EqualTo(ReflectionProbeWorkKind.CaptureFace));
            Assert.That(scheduler.DeferredChangingSceneTotal, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void FailedWork_StopsAfterConfiguredRetryLimit()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1, retryLimit: 1);
        Guid id = Guid.NewGuid();
        var version = new ReflectionCaptureVersion(1, 1, 1, 1, 1, 1, 1);
        scheduler.Register(0, id, hasPublishedCapture: false);
        scheduler.Request(0, id, version, ReflectionCaptureReason.SceneChanged, default, 1, 1);

        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork first), Is.True);
        scheduler.FailActive(first.Ticket, retry: true);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork second), Is.True);
        scheduler.FailActive(second.Ticket, retry: true);

        Assert.Multiple(() =>
        {
            Assert.That(scheduler.RetryExhaustedTotal, Is.EqualTo(1UL));
            Assert.That(scheduler.QueueDepth, Is.Zero);
            Assert.That(scheduler.ActiveTicketCount, Is.Zero);
        });
    }

    [Test]
    public void PublishCopy_CannotBeSubmittedTwice()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1);
        Guid id = Guid.NewGuid();
        var version = new ReflectionCaptureVersion(1, 1, 1, 1, 1, 1, 1);
        scheduler.Register(0, id, hasPublishedCapture: false);
        scheduler.Request(0, id, version, ReflectionCaptureReason.InitialLoad, default, 1, 1);

        for (int face = 0; face < 6; face++)
        {
            Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork work), Is.True);
            scheduler.CompleteWork(work);
        }
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork mip), Is.True);
        scheduler.CompleteWork(mip);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork copy), Is.True);
        scheduler.MarkCopySubmitted(copy, 99UL);
        Assert.That(() => scheduler.MarkCopySubmitted(copy, 100UL),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void NewVersionAtCopyReady_RestartsBeforePublishingStaleScratch()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1);
        Guid id = Guid.NewGuid();
        var first = new ReflectionCaptureVersion(1, 1, 1, 1, 1, 1, 1);
        var second = first with { SceneRadianceRevision = 2 };
        scheduler.Register(0, id, hasPublishedCapture: true, first);
        scheduler.Request(0, id, first, ReflectionCaptureReason.InitialLoad,
            default, resourceGeneration: 1, sceneRevision: 1);

        for (int face = 0; face < 6; face++)
        {
            Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork work), Is.True);
            scheduler.CompleteWork(work);
        }
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork mip), Is.True);
        scheduler.CompleteWork(mip);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork copyReady), Is.True);
        Assert.That(copyReady.Kind, Is.EqualTo(ReflectionProbeWorkKind.PublishCopy));

        scheduler.Request(0, id, second, ReflectionCaptureReason.SceneChanged,
            default, resourceGeneration: 2, sceneRevision: 2);

        Assert.Multiple(() =>
        {
            Assert.That(scheduler.HasPublishedCapture(0, id), Is.True);
            Assert.That(scheduler.HasWork(2, ReflectionProbeWorkKind.PublishCopy), Is.False);
            Assert.That(scheduler.HasWork(2, ReflectionProbeWorkKind.CaptureFace), Is.True);
        });
        Assert.That(scheduler.TryAcquireWork(
            2,
            1,
            1,
            out ReflectionProbeWork restarted,
            ReflectionProbeWorkKind.CaptureFace), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(restarted.Kind, Is.EqualTo(ReflectionProbeWorkKind.CaptureFace));
            Assert.That(restarted.Face, Is.Zero);
            Assert.That(restarted.Ticket.Version, Is.EqualTo(second));
            Assert.That(restarted.Ticket.ResourceGeneration, Is.EqualTo(2U));
        });
    }

    [Test]
    public void NewerRequestIsNotOverwrittenWhenActiveWorkIsDeferredOrFailed()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1, retryLimit: 0);
        Guid id = Guid.NewGuid();
        var first = new ReflectionCaptureVersion(1, 1, 1, 1, 1, 1, 1);
        var second = first with { SceneRadianceRevision = 2 };
        scheduler.Register(0, id, hasPublishedCapture: false);
        scheduler.Request(0, id, first, ReflectionCaptureReason.InitialLoad, default, 1, 1);

        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork deferred), Is.True);
        scheduler.Request(0, id, second, ReflectionCaptureReason.SceneChanged, default, 2, 2);
        scheduler.DeferActive(deferred, currentFrame: 0, deferFrames: 0);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork latest), Is.True);
        Assert.That(latest.Ticket.Version, Is.EqualTo(second));
        scheduler.FailActive(latest.Ticket, retry: false);

        scheduler.Request(0, id, second, ReflectionCaptureReason.Manual, default, 2, 2);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork failed), Is.True);
        scheduler.Request(0, id, second with { LightRevision = 2 },
            ReflectionCaptureReason.LightChanged, default, 2, 2);
        scheduler.FailActive(failed.Ticket, retry: false);

        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork afterFailure), Is.True);
        Assert.That(afterFailure.Ticket.Version, Is.EqualTo(second with { LightRevision = 2 }));
    }

    [Test]
    public void SameVersionReasonArrivingDuringCopyIsRetainedAfterCompletion()
    {
        var scheduler = new ReflectionProbeCaptureScheduler(1);
        Guid id = Guid.NewGuid();
        var version = new ReflectionCaptureVersion(1, 1, 1, 1, 1, 1, 1);
        scheduler.Register(0, id, hasPublishedCapture: false);
        scheduler.Request(0, id, version, ReflectionCaptureReason.InitialLoad, default, 1, 1);

        for (int face = 0; face < 6; face++)
        {
            Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork work), Is.True);
            scheduler.CompleteWork(work);
        }
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork mip), Is.True);
        scheduler.CompleteWork(mip);
        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork copy), Is.True);

        scheduler.Request(0, id, version, ReflectionCaptureReason.Manual, default, 1, 1);
        scheduler.MarkCopySubmitted(copy, 7UL);
        Assert.That(scheduler.TryPublishCompleted(7UL, out _), Is.True);

        Assert.That(scheduler.TryAcquireWork(2, 1, 1, out ReflectionProbeWork followUp), Is.True);
        Assert.That(followUp.Ticket.Version, Is.EqualTo(version));
        Assert.That(followUp.Ticket.Reasons, Is.EqualTo(ReflectionCaptureReason.Manual));
    }
}
