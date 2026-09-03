using System.Threading;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PipelineCompilationSchedulerTests
{
    [TestCase(null, 1, 1)]
    [TestCase(null, 4, 1)]
    [TestCase(null, 8, 2)]
    [TestCase(null, 64, 4)]
    [TestCase("3", 64, 3)]
    public void WorkerCount_IsBoundedAndConfigurable(
        string? configured,
        int processorCount,
        int expected)
    {
        Assert.That(
            PipelineCompilationScheduler.ResolveWorkerCount(
                configured,
                processorCount),
            Is.EqualTo(expected));
    }

    [TestCase("0")]
    [TestCase("9")]
    [TestCase("many")]
    public void InvalidWorkerCount_IsRejected(string configured)
    {
        Assert.That(
            () => PipelineCompilationScheduler.ResolveWorkerCount(
                configured,
                16),
            Throws.InvalidOperationException);
    }

    [Test]
    public void Schedule_DeduplicatesArtifactAndHonorsConcurrencyBound()
    {
        using var scheduler = new PipelineCompilationScheduler(2);
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        int active = 0;
        int peak = 0;
        int executions = 0;
        int arrivals = 0;
        var manifest = new PipelineStartupManifest("test");

        for (int index = 0; index < 4; index++)
        {
            var id = new PipelineArtifactId($"pipeline-{index}");
            manifest.Require(id);
            scheduler.Schedule(id, _ =>
            {
                Interlocked.Increment(ref executions);
                int current = Interlocked.Increment(ref active);
                UpdateMaximum(ref peak, current);
                if (Interlocked.Increment(ref arrivals) <= 2)
                    entered.Signal();
                release.Wait(TimeSpan.FromSeconds(5));
                Interlocked.Decrement(ref active);
            });
        }

        var duplicate = new PipelineArtifactId("pipeline-0");
        scheduler.Schedule(
            duplicate,
            _ => Interlocked.Add(ref executions, 100));
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(Volatile.Read(ref peak), Is.EqualTo(2));
        release.Set();
        scheduler.Wait(manifest);

        Assert.Multiple(() =>
        {
            Assert.That(executions, Is.EqualTo(4));
            Assert.That(peak, Is.EqualTo(2));
        });
    }

    [Test]
    public void Wait_RejectsUnscheduledRequiredArtifact()
    {
        using var scheduler = new PipelineCompilationScheduler(1);
        var manifest = new PipelineStartupManifest("missing")
            .Require(new PipelineArtifactId("not-scheduled"));

        Assert.That(
            () => scheduler.Wait(manifest),
            Throws.InvalidOperationException);
    }

    [Test]
    public void CancelPending_SkipsQueuedWorkAndDrainsActiveCompile()
    {
        using var scheduler = new PipelineCompilationScheduler(1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var drainStarted = new ManualResetEventSlim();
        int activeExecutions = 0;
        int queuedExecutions = 0;
        int lateExecutions = 0;

        scheduler.Schedule(new PipelineArtifactId("active"), _ =>
        {
            Interlocked.Increment(ref activeExecutions);
            activeEntered.Set();
            releaseActive.Wait();
        });

        Assert.That(activeEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        scheduler.Schedule(
            new PipelineArtifactId("queued"),
            _ => Interlocked.Increment(ref queuedExecutions));

        scheduler.CancelPending();
        scheduler.CancelPending();
        scheduler.Schedule(
            new PipelineArtifactId("late"),
            _ => Interlocked.Increment(ref lateExecutions));

        Exception? drainFailure = null;
        Task drain = Task.Run(() =>
        {
            drainStarted.Set();
            try
            {
                scheduler.WaitForAll();
            }
            catch (Exception exception)
            {
                drainFailure = exception;
            }
        });

        try
        {
            Assert.That(drainStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(drain.IsCompleted, Is.False);
        }
        finally
        {
            releaseActive.Set();
        }

        Assert.That(drain.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(drainFailure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(activeExecutions, Is.EqualTo(1));
            Assert.That(queuedExecutions, Is.Zero);
            Assert.That(lateExecutions, Is.Zero);
        });
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            int observed = Volatile.Read(ref target);
            if (candidate <= observed ||
                Interlocked.CompareExchange(
                    ref target,
                    candidate,
                    observed) == observed)
            {
                return;
            }
        }
    }
}
