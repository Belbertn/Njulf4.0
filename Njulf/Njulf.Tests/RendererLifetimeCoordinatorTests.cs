using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererLifetimeCoordinatorTests
{
    [Test]
    public void Initialize_RetriesFailureAndCommitsOnlyOneSuccess()
    {
        var lifetime = CreateLifetime();
        var failure = new InvalidOperationException("injected startup failure");
        int attempts = 0;

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => lifetime.Initialize(() =>
            {
                attempts++;
                throw failure;
            }))!;

        Assert.That(thrown, Is.SameAs(failure));
        Assert.That(lifetime.InitializationSucceeded, Is.False);

        bool performed = lifetime.Initialize(() => attempts++);
        bool repeated = lifetime.Initialize(() => attempts++);

        Assert.Multiple(() =>
        {
            Assert.That(performed, Is.True);
            Assert.That(repeated, Is.False);
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(lifetime.InitializationSucceeded, Is.True);
            Assert.That(
                () => lifetime.ThrowIfInitializationSucceeded(
                    "configuration is closed"),
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "configuration is closed"));
        });
    }

    [Test]
    public void FrameTransitions_PreserveIntentSpecificGuardMessages()
    {
        var lifetime = CreateLifetime();

        Assert.Multiple(() =>
        {
            Assert.That(
                lifetime.EnsureCanEndFrame,
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "EndFrame was called without a successful BeginFrame."));
            Assert.That(
                () => lifetime.EnsureFrameInProgress("Clear"),
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "Clear requires a successful BeginFrame call."));
        });

        lifetime.EnsureCanBeginFrame();
        lifetime.MarkFrameStarted();

        Assert.Multiple(() =>
        {
            Assert.That(lifetime.FrameInProgress, Is.True);
            Assert.That(
                lifetime.EnsureCanBeginFrame,
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "BeginFrame was called while a frame is already in progress."));
            Assert.That(
                lifetime.EnsureSwapchainRecreationAllowed,
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "Swapchain cannot be recreated while command recording is in progress."));
        });

        lifetime.CompleteFrame();
        Assert.That(lifetime.FrameInProgress, Is.False);
        lifetime.MarkFrameStarted();
        lifetime.AbandonFrame();
        Assert.That(lifetime.FrameInProgress, Is.False);
    }

    [Test]
    public void RecreationIntent_ClearsOnlyAfterSuccessfulAcknowledgement()
    {
        var lifetime = CreateLifetime();

        lifetime.RequestSwapchainRecreation();
        lifetime.ObserveSwapchainRecreationAttempt(succeeded: false);
        bool afterFailure = lifetime.SwapchainRecreationRequested;
        lifetime.ObserveSwapchainRecreationAttempt(succeeded: true);

        Assert.Multiple(() =>
        {
            Assert.That(afterFailure, Is.True);
            Assert.That(lifetime.SwapchainRecreationRequested, Is.False);
        });
    }

    [Test]
    public void SubmissionFault_NormalizesReasonAndKeepsDeviceLossMonotonic()
    {
        var lifetime = CreateLifetime();
        lifetime.MarkFrameStarted();

        RendererSubmissionFault first =
            lifetime.LatchSubmissionFault("   ", deviceLost: false);

        Assert.Multiple(() =>
        {
            Assert.That(
                first.Reason,
                Is.EqualTo("A Vulkan frame submission failed."));
            Assert.That(first.DeviceLost, Is.False);
            Assert.That(lifetime.FrameInProgress, Is.True);
            Assert.That(
                lifetime.ThrowIfSubmissionFaulted,
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "A previous frame submission failed and the renderer was stopped before unsafe resource reuse. A Vulkan frame submission failed."));
        });

        lifetime.RecordDeviceLoss();
        RendererSubmissionFault later =
            lifetime.LatchSubmissionFault("later submit failed", deviceLost: false);

        Assert.Multiple(() =>
        {
            Assert.That(later.DeviceLost, Is.True);
            Assert.That(lifetime.DeviceLost, Is.True);
            Assert.That(
                lifetime.ThrowIfSubmissionFaulted,
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "The Vulkan device was lost during a frame submission. later submit failed"));
        });

        lifetime.AbandonFrame();
        Assert.That(lifetime.FrameInProgress, Is.False);
    }

    [Test]
    public void Disposal_FactoryFailureLeavesGuardsOpenThenDrainRetriesPlan()
    {
        var lifetime = CreateLifetime();
        var factoryFailure = new InvalidOperationException("factory failed");
        int factoryCalls = 0;
        int unsubscriptions = 0;
        int stageCalls = 0;
        bool failStage = true;

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => lifetime.DrainDisposal(
                () =>
                {
                    factoryCalls++;
                    throw factoryFailure;
                },
                () => unsubscriptions++))!;

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(factoryFailure));
            Assert.That(lifetime.DisposalStarted, Is.False);
            Assert.That(lifetime.DisposalCompleted, Is.False);
            Assert.That(unsubscriptions, Is.Zero);
            Assert.That(lifetime.ThrowIfDisposalStarted, Throws.Nothing);
        });

        StagedDisposalPlan CreateRetryablePlan()
        {
            factoryCalls++;
            return new StagedDisposalPlan(
            [
                new StagedDisposalStep(
                    "retryable",
                    () =>
                    {
                        stageCalls++;
                        if (failStage)
                            throw new InvalidOperationException("stage failed");
                    })
            ]);
        }

        Assert.That(
            () => lifetime.DrainDisposal(
                CreateRetryablePlan,
                () => unsubscriptions++),
            Throws.TypeOf<AggregateException>());
        Assert.Multiple(() =>
        {
            Assert.That(lifetime.DisposalStarted, Is.True);
            Assert.That(lifetime.DisposalCompleted, Is.False);
            Assert.That(
                lifetime.ThrowIfDisposalStarted,
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(
                () => lifetime.Initialize(
                    () => throw new AssertionException(
                        "Initialization action must not run.")),
                Throws.TypeOf<ObjectDisposedException>());
        });

        failStage = false;
        bool completed = lifetime.DrainDisposal(
            CreateRetryablePlan,
            () => unsubscriptions++);
        bool completedNoOp = lifetime.DrainDisposal(
            CreateRetryablePlan,
            () => unsubscriptions++);

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.True);
            Assert.That(completedNoOp, Is.False);
            Assert.That(lifetime.DisposalCompleted, Is.True);
            Assert.That(factoryCalls, Is.EqualTo(2));
            Assert.That(unsubscriptions, Is.EqualTo(1));
            Assert.That(stageCalls, Is.EqualTo(2));
        });
    }

    [Test]
    public void Disposal_TracksRawDeviceIdleResultAndDoesNotCloseConfiguration()
    {
        var lifetime = CreateLifetime();

        Assert.That(
            lifetime.DisposalDeviceIdleResult,
            Is.EqualTo(Result.ErrorUnknown));
        lifetime.RecordDisposalDeviceIdleResult(Result.ErrorDeviceLost);
        bool completed = lifetime.DrainDisposal(
            () => new StagedDisposalPlan([]),
            static () => { });

        Assert.Multiple(() =>
        {
            Assert.That(completed, Is.True);
            Assert.That(
                lifetime.DisposalDeviceIdleResult,
                Is.EqualTo(Result.ErrorDeviceLost));
            Assert.That(
                () => lifetime.ThrowIfInitializationSucceeded(
                    "configuration is closed"),
                Throws.Nothing);
        });
    }

    [Test]
    public void Disposal_ConcurrentCallersShareOnePlanAndCompletion()
    {
        var lifetime = CreateLifetime();
        using var stageEntered = new ManualResetEventSlim();
        using var releaseStage = new ManualResetEventSlim();
        int factoryCalls = 0;
        int unsubscriptions = 0;
        int stageCalls = 0;

        StagedDisposalPlan CreatePlan()
        {
            Interlocked.Increment(ref factoryCalls);
            return new StagedDisposalPlan(
            [
                new StagedDisposalStep(
                    "blocking",
                    () =>
                    {
                        Interlocked.Increment(ref stageCalls);
                        stageEntered.Set();
                        releaseStage.Wait();
                    })
            ]);
        }

        Task<bool> first = Task.Run(() => lifetime.DrainDisposal(
            CreatePlan,
            () => Interlocked.Increment(ref unsubscriptions)));
        Assert.That(stageEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Task<bool> second = Task.Run(() => lifetime.DrainDisposal(
            CreatePlan,
            () => Interlocked.Increment(ref unsubscriptions)));
        releaseStage.Set();
        Assert.That(
            Task.WaitAll([first, second], TimeSpan.FromSeconds(5)),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(new[] { first.Result, second.Result },
                Is.EquivalentTo(new[] { true, false }));
            Assert.That(factoryCalls, Is.EqualTo(1));
            Assert.That(unsubscriptions, Is.EqualTo(1));
            Assert.That(stageCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void RunStartupStep_RecordsTransitionsAndRethrowsSameFailure()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"lifetime-startup-{Guid.NewGuid():N}.jsonl");
        var failure = new InvalidOperationException("startup failed");
        try
        {
            using (var log = new RendererStartupLog(path, ["--lifetime-test"]))
            {
                var lifetime = new RendererLifetimeCoordinator(
                    "TestRenderer",
                    log);
                lifetime.RunStartupStep("successful-step", static () => { });
                InvalidOperationException thrown =
                    Assert.Throws<InvalidOperationException>(
                        () => lifetime.RunStartupStep(
                            "failed-step",
                            () => throw failure))!;
                Assert.That(thrown, Is.SameAs(failure));

                // The coordinator borrows the log; callers can keep using it.
                log.StepStarted("externally-owned-step");
                log.StepSucceeded("externally-owned-step");
            }

            string text = File.ReadAllText(path);
            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("successful-step"));
                Assert.That(text, Does.Contain("failed-step"));
                Assert.That(text, Does.Contain("startup failed"));
                Assert.That(text, Does.Contain("externally-owned-step"));
                Assert.That(
                    text.IndexOf("Started", StringComparison.Ordinal),
                    Is.LessThan(
                        text.IndexOf("Succeeded", StringComparison.Ordinal)));
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static RendererLifetimeCoordinator CreateLifetime() =>
        new("TestRenderer");
}
