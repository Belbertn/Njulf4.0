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
}
