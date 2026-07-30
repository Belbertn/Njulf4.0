using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshUploadTransactionTests
{
    [Test]
    public void Success_PublishesBindingsBeforeAuthoritativeState()
    {
        var calls = new List<string>();

        MeshUploadTransaction.Execute(
            () => calls.Add("gpu-complete"),
            () => calls.Add("bindings-published"),
            () => calls.Add("state-committed"),
            () => calls.Add("gpu-cleanup"),
            () => calls.Add("state-restored"),
            () => calls.Add("bindings-restored"),
            () => calls.Add("candidates-destroyed"),
            () => calls.Add("candidates-quarantined"),
            () => calls.Add("reservations-restored"));

        Assert.That(
            calls,
            Is.EqualTo(new[]
            {
                "gpu-complete",
                "bindings-published",
                "state-committed"
            }));
    }

    [Test]
    public void GpuUploadFailure_CleansCommandsAndDestroysOnlyCandidates()
    {
        var calls = new List<string>();
        var authoritativeBuffers = new HashSet<string> { "old-a", "old-b" };
        var destroyedBuffers = new List<string>();
        var reservations = new Stack<int>(new[] { 7, 3 });

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() =>
                MeshUploadTransaction.Execute(
                    () =>
                    {
                        calls.Add("gpu-failed");
                        throw new InvalidOperationException(
                            "injected GPU upload failure");
                    },
                    () => calls.Add("bindings-published"),
                    () => calls.Add("state-committed"),
                    () => calls.Add("gpu-cleanup"),
                    () => calls.Add("state-restored"),
                    () => calls.Add("bindings-restored"),
                    () =>
                    {
                        calls.Add("candidates-destroyed");
                        destroyedBuffers.Add("new-a");
                        destroyedBuffers.Add("new-b");
                    },
                    () => calls.Add("candidates-quarantined"),
                    () =>
                    {
                        calls.Add("reservations-restored");
                        reservations.Push(11);
                    }))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                failure.Message,
                Is.EqualTo("injected GPU upload failure"));
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "gpu-failed",
                    "gpu-cleanup",
                    "candidates-destroyed",
                    "reservations-restored"
                }));
            Assert.That(
                destroyedBuffers,
                Is.EqualTo(new[] { "new-a", "new-b" }));
            Assert.That(
                destroyedBuffers.Intersect(authoritativeBuffers),
                Is.Empty);
            Assert.That(reservations.Peek(), Is.EqualTo(11));
        });
    }

    [Test]
    public void BindlessPublicationFailure_RestoresBindingsBeforeCandidateDestruction()
    {
        var calls = new List<string>();
        string authoritativeBinding = "old";
        string currentBinding = authoritativeBinding;
        int authoritativeCounter = 4;

        Assert.Throws<InvalidOperationException>(() =>
            MeshUploadTransaction.Execute(
                () => calls.Add("gpu-complete"),
                () =>
                {
                    currentBinding = "candidate";
                    calls.Add("bindings-failed");
                    throw new InvalidOperationException(
                        "injected bindless publication failure");
                },
                () =>
                {
                    authoritativeCounter = 9;
                    calls.Add("state-committed");
                },
                () => calls.Add("gpu-cleanup"),
                () =>
                {
                    authoritativeCounter = 4;
                    calls.Add("state-restored");
                },
                () =>
                {
                    currentBinding = authoritativeBinding;
                    calls.Add("bindings-restored");
                },
                () => calls.Add("candidates-destroyed"),
                () => calls.Add("candidates-quarantined"),
                () => calls.Add("reservations-restored")));

        Assert.Multiple(() =>
        {
            Assert.That(currentBinding, Is.EqualTo(authoritativeBinding));
            Assert.That(authoritativeCounter, Is.EqualTo(4));
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "gpu-complete",
                    "bindings-failed",
                    "gpu-cleanup",
                    "bindings-restored",
                    "candidates-destroyed",
                    "reservations-restored"
                }));
        });
    }

    [Test]
    public void StatePublicationFailure_RestoresStateAndBindingsBeforeCandidateDestruction()
    {
        var calls = new List<string>();
        string currentBinding = "old";
        int authoritativeCounter = 4;

        Assert.Throws<InvalidOperationException>(() =>
            MeshUploadTransaction.Execute(
                () => calls.Add("gpu-complete"),
                () =>
                {
                    currentBinding = "candidate";
                    calls.Add("bindings-published");
                },
                () =>
                {
                    authoritativeCounter = 9;
                    calls.Add("state-failed");
                    throw new InvalidOperationException(
                        "injected state publication failure");
                },
                () => calls.Add("gpu-cleanup"),
                () =>
                {
                    authoritativeCounter = 4;
                    calls.Add("state-restored");
                },
                () =>
                {
                    currentBinding = "old";
                    calls.Add("bindings-restored");
                },
                () => calls.Add("candidates-destroyed"),
                () => calls.Add("candidates-quarantined"),
                () => calls.Add("reservations-restored")));

        Assert.Multiple(() =>
        {
            Assert.That(currentBinding, Is.EqualTo("old"));
            Assert.That(authoritativeCounter, Is.EqualTo(4));
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "gpu-complete",
                    "bindings-published",
                    "state-failed",
                    "gpu-cleanup",
                    "state-restored",
                    "bindings-restored",
                    "candidates-destroyed",
                    "reservations-restored"
                }));
        });
    }

    [Test]
    public void DescriptorRollbackFailure_QuarantinesCandidatesAndReportsBothFailures()
    {
        var calls = new List<string>();

        AggregateException failure =
            Assert.Throws<AggregateException>(() =>
                MeshUploadTransaction.Execute(
                    () => calls.Add("gpu-complete"),
                    () =>
                    {
                        calls.Add("bindings-failed");
                        throw new InvalidOperationException(
                            "injected publication failure");
                    },
                    () => calls.Add("state-committed"),
                    () => calls.Add("gpu-cleanup"),
                    () => calls.Add("state-restored"),
                    () =>
                    {
                        calls.Add("bindings-restore-failed");
                        throw new InvalidOperationException(
                            "injected descriptor rollback failure");
                    },
                    () => calls.Add("candidates-destroyed"),
                    () => calls.Add("candidates-quarantined"),
                    () => calls.Add("reservations-restored")))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                failure.InnerExceptions.Select(
                    exception => exception.Message),
                Is.EqualTo(new[]
                {
                    "injected publication failure",
                    "injected descriptor rollback failure"
                }));
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "gpu-complete",
                    "bindings-failed",
                    "gpu-cleanup",
                    "bindings-restore-failed",
                    "candidates-quarantined",
                    "reservations-restored"
                }));
            Assert.That(calls, Does.Not.Contain("candidates-destroyed"));
        });
    }

    [Test]
    public void CandidateDestructionFailure_AttemptsQuarantineAndReservationRestore()
    {
        var calls = new List<string>();

        AggregateException failure =
            Assert.Throws<AggregateException>(() =>
                MeshUploadTransaction.Execute(
                    () => throw new InvalidOperationException(
                        "injected upload failure"),
                    () => calls.Add("bindings-published"),
                    () => calls.Add("state-committed"),
                    () => calls.Add("gpu-cleanup"),
                    () => calls.Add("state-restored"),
                    () => calls.Add("bindings-restored"),
                    () =>
                    {
                        calls.Add("candidate-destroy-failed");
                        throw new InvalidOperationException(
                            "injected buffer destroy failure");
                    },
                    () => calls.Add("candidates-quarantined"),
                    () => calls.Add("reservations-restored")))!;

        Assert.Multiple(() =>
        {
            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "gpu-cleanup",
                    "candidate-destroy-failed",
                    "candidates-quarantined",
                    "reservations-restored"
                }));
        });
    }
}
