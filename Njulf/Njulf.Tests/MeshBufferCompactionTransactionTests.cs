using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshBufferCompactionTransactionTests
{
    [Test]
    public void Success_PublishesCandidateAndCountersWithoutChangingCpuState()
    {
        var state = new CompactionState();

        ExecuteCompaction(
            state,
            completeGpuUpload: () => state.Calls.Add("gpu-complete"),
            publishCandidateBindings: () =>
            {
                state.BoundBuffer = "candidate";
                state.Calls.Add("bindings-published");
            },
            commitAuthoritativeState: () =>
            {
                state.ActiveBuffer = "candidate";
                state.CompactionCount = 4;
                state.CompactedBytesSaved = 384;
                state.Calls.Add("state-committed");
            });

        Assert.Multiple(() =>
        {
            Assert.That(state.ActiveBuffer, Is.EqualTo("candidate"));
            Assert.That(state.BoundBuffer, Is.EqualTo("candidate"));
            Assert.That(state.CompactionCount, Is.EqualTo(4));
            Assert.That(state.CompactedBytesSaved, Is.EqualTo(384));
            Assert.That(
                state.CpuMeshState,
                Is.EqualTo(new[] { 7, 11, 19 }));
            Assert.That(state.DestroyedCandidates, Is.Empty);
            Assert.That(state.QuarantinedCandidates, Is.Empty);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[]
                {
                    "gpu-complete",
                    "bindings-published",
                    "state-committed"
                }));
        });
    }

    [Test]
    public void SubmitFailure_PreservesAuthoritativeStateAndDestroysCandidate()
    {
        var state = new CompactionState();

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() =>
                ExecuteCompaction(
                    state,
                    completeGpuUpload: () =>
                    {
                        state.Calls.Add("submit-failed");
                        throw new InvalidOperationException(
                            "injected submit failure");
                    }))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                failure.Message,
                Is.EqualTo("injected submit failure"));
            AssertAuthoritativeStateWasRestored(state);
            Assert.That(
                state.DestroyedCandidates,
                Is.EqualTo(new[] { "candidate" }));
            Assert.That(state.QuarantinedCandidates, Is.Empty);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[]
                {
                    "submit-failed",
                    "gpu-cleanup",
                    "candidate-destroyed"
                }));
        });
    }

    [Test]
    public void BindlessFailure_RestoresDescriptorAndPreservesAuthoritativeState()
    {
        var state = new CompactionState();

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() =>
                ExecuteCompaction(
                    state,
                    publishCandidateBindings: () =>
                    {
                        state.BoundBuffer = "candidate";
                        state.Calls.Add("bindings-failed");
                        throw new InvalidOperationException(
                            "injected bindless failure");
                    }))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                failure.Message,
                Is.EqualTo("injected bindless failure"));
            AssertAuthoritativeStateWasRestored(state);
            Assert.That(
                state.DestroyedCandidates,
                Is.EqualTo(new[] { "candidate" }));
            Assert.That(state.QuarantinedCandidates, Is.Empty);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[]
                {
                    "gpu-complete",
                    "bindings-failed",
                    "gpu-cleanup",
                    "bindings-restored",
                    "candidate-destroyed"
                }));
        });
    }

    [Test]
    public void StatePublicationFailure_RestoresBuffersDescriptorsCountersAndCpuState()
    {
        var state = new CompactionState();

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() =>
                ExecuteCompaction(
                    state,
                    commitAuthoritativeState: () =>
                    {
                        MutateAllAuthoritativeState(state);
                        state.Calls.Add("state-failed");
                        throw new InvalidOperationException(
                            "injected state publication failure");
                    }))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                failure.Message,
                Is.EqualTo("injected state publication failure"));
            AssertAuthoritativeStateWasRestored(state);
            Assert.That(
                state.DestroyedCandidates,
                Is.EqualTo(new[] { "candidate" }));
            Assert.That(state.QuarantinedCandidates, Is.Empty);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[]
                {
                    "gpu-complete",
                    "bindings-published",
                    "state-failed",
                    "gpu-cleanup",
                    "state-restored",
                    "bindings-restored",
                    "candidate-destroyed"
                }));
        });
    }

    [Test]
    public void CleanupFailure_RestoresAllStateAndQuarantinesCandidate()
    {
        var state = new CompactionState();

        AggregateException failure =
            Assert.Throws<AggregateException>(() =>
                ExecuteCompaction(
                    state,
                    commitAuthoritativeState: () =>
                    {
                        MutateAllAuthoritativeState(state);
                        state.Calls.Add("state-failed");
                        throw new InvalidOperationException(
                            "injected state publication failure");
                    },
                    cleanupGpuUpload: () =>
                    {
                        state.Calls.Add("cleanup-failed");
                        throw new InvalidOperationException(
                            "injected cleanup failure");
                    }))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                failure.InnerExceptions.Select(
                    exception => exception.Message),
                Is.EqualTo(new[]
                {
                    "injected state publication failure",
                    "injected cleanup failure"
                }));
            AssertAuthoritativeStateWasRestored(state);
            Assert.That(state.DestroyedCandidates, Is.Empty);
            Assert.That(
                state.QuarantinedCandidates,
                Is.EqualTo(new[] { "candidate" }));
            Assert.That(
                state.Calls,
                Is.EqualTo(new[]
                {
                    "gpu-complete",
                    "bindings-published",
                    "state-failed",
                    "cleanup-failed",
                    "state-restored",
                    "bindings-restored",
                    "candidate-quarantined"
                }));
        });
    }

    private static void ExecuteCompaction(
        CompactionState state,
        Action? completeGpuUpload = null,
        Action? publishCandidateBindings = null,
        Action? commitAuthoritativeState = null,
        Action? cleanupGpuUpload = null)
    {
        string originalBuffer = state.ActiveBuffer;
        string originalBinding = state.BoundBuffer;
        int originalCompactionCount = state.CompactionCount;
        ulong originalCompactedBytesSaved =
            state.CompactedBytesSaved;
        int[] originalCpuMeshState = state.CpuMeshState.ToArray();

        MeshUploadTransaction.Execute(
            completeGpuUpload ??
                (() => state.Calls.Add("gpu-complete")),
            publishCandidateBindings ??
                (() =>
                {
                    state.BoundBuffer = "candidate";
                    state.Calls.Add("bindings-published");
                }),
            commitAuthoritativeState ??
                (() =>
                {
                    state.ActiveBuffer = "candidate";
                    state.CompactionCount = 4;
                    state.CompactedBytesSaved = 384;
                    state.Calls.Add("state-committed");
                }),
            cleanupGpuUpload ??
                (() => state.Calls.Add("gpu-cleanup")),
            () =>
            {
                state.ActiveBuffer = originalBuffer;
                state.CompactionCount = originalCompactionCount;
                state.CompactedBytesSaved =
                    originalCompactedBytesSaved;
                state.CpuMeshState.Clear();
                state.CpuMeshState.AddRange(
                    originalCpuMeshState);
                state.Calls.Add("state-restored");
            },
            () =>
            {
                state.BoundBuffer = originalBinding;
                state.Calls.Add("bindings-restored");
            },
            () =>
            {
                state.DestroyedCandidates.Add("candidate");
                state.Calls.Add("candidate-destroyed");
            },
            () =>
            {
                state.QuarantinedCandidates.Add("candidate");
                state.Calls.Add("candidate-quarantined");
            },
            static () => { });
    }

    private static void MutateAllAuthoritativeState(
        CompactionState state)
    {
        state.ActiveBuffer = "candidate";
        state.CompactionCount = 99;
        state.CompactedBytesSaved = 9_999;
        state.CpuMeshState.Clear();
        state.CpuMeshState.Add(101);
        state.BoundBuffer = "candidate";
    }

    private static void AssertAuthoritativeStateWasRestored(
        CompactionState state)
    {
        Assert.Multiple(() =>
        {
            Assert.That(state.ActiveBuffer, Is.EqualTo("original"));
            Assert.That(state.BoundBuffer, Is.EqualTo("original"));
            Assert.That(state.CompactionCount, Is.EqualTo(3));
            Assert.That(state.CompactedBytesSaved, Is.EqualTo(128));
            Assert.That(
                state.CpuMeshState,
                Is.EqualTo(new[] { 7, 11, 19 }));
        });
    }

    private sealed class CompactionState
    {
        public string ActiveBuffer { get; set; } = "original";
        public string BoundBuffer { get; set; } = "original";
        public int CompactionCount { get; set; } = 3;
        public ulong CompactedBytesSaved { get; set; } = 128;
        public List<int> CpuMeshState { get; } = new()
        {
            7,
            11,
            19
        };
        public List<string> Calls { get; } = new();
        public List<string> DestroyedCandidates { get; } = new();
        public List<string> QuarantinedCandidates { get; } = new();
    }
}
