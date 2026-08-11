using Njulf.Rendering;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class MeshBufferGrowthPolicyTests
{
    [Test]
    public void GeometricPlan_DoublesOnlyStreamsThatNeedGrowth()
    {
        MeshBufferGrowthPlan plan =
            MeshBufferGrowthPlanner.Create(
                CreateNineStreamInputs(
                    vertexPositionCurrent: 4,
                    vertexPositionRequired: 5),
                MeshBufferGrowthMode.Geometric);

        MeshBufferGrowthPlanEntry position =
            plan.Entries.Single(
                entry =>
                    entry.Stream ==
                    MeshBufferStream.VertexPosition);

        Assert.Multiple(() =>
        {
            Assert.That(position.TargetSize, Is.EqualTo(8));
            Assert.That(position.RequiresReplacement, Is.True);
            Assert.That(
                plan.Entries.Count(
                    entry => entry.RequiresReplacement),
                Is.EqualTo(1));
            Assert.That(plan.ReplacementTargetBytes, Is.EqualTo(8));
            Assert.That(plan.TotalCurrentBytes, Is.EqualTo(76));
            Assert.That(plan.TotalRequiredBytes, Is.EqualTo(77));
            Assert.That(plan.TotalTargetBytes, Is.EqualTo(80));
        });
    }

    [Test]
    public void TightPlan_UsesExactRequiredCapacity()
    {
        MeshBufferGrowthPlan plan =
            MeshBufferGrowthPlanner.Create(
                CreateNineStreamInputs(
                    vertexPositionCurrent: 4,
                    vertexPositionRequired: 13),
                MeshBufferGrowthMode.Tight);

        MeshBufferGrowthPlanEntry position =
            plan.Entries.Single(
                entry =>
                    entry.Stream ==
                    MeshBufferStream.VertexPosition);

        Assert.Multiple(() =>
        {
            Assert.That(position.TargetSize, Is.EqualTo(13));
            Assert.That(plan.ReplacementTargetBytes, Is.EqualTo(13));
            Assert.That(plan.Describe(), Does.Contain("target=13"));
        });
    }

    [Test]
    public void GeometricPlan_CheckedOverflowOccursBeforeAllocation()
    {
        Assert.Throws<OverflowException>(() =>
            MeshBufferGrowthPlanner.Create(
                new[]
                {
                    new MeshBufferGrowthInput(
                        MeshBufferStream.VertexPosition,
                        1UL << 63,
                        ulong.MaxValue)
                },
                MeshBufferGrowthMode.Geometric));
    }

    [Test]
    public void Plan_RejectsDuplicateStreams()
    {
        Assert.Throws<ArgumentException>(() =>
            MeshBufferGrowthPlanner.Create(
                new[]
                {
                    new MeshBufferGrowthInput(
                        MeshBufferStream.Index,
                        4,
                        8),
                    new MeshBufferGrowthInput(
                        MeshBufferStream.Index,
                        8,
                        16)
                },
                MeshBufferGrowthMode.Geometric));
    }

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(8)]
    public void Retry_PartialGeometricCandidatesAreReleasedInReverseOrder(
        int failureIndex)
    {
        var liveCandidates = new List<int>();
        var releasedCandidates = new List<int>();
        var calls = new List<string>();

        MeshBufferGrowthRetry.Execute(
            executeAttempt: mode =>
            {
                calls.Add($"attempt:{mode}");
                if (mode == MeshBufferGrowthMode.Tight)
                    return;

                for (int index = 0; index < 9; index++)
                {
                    if (index == failureIndex)
                    {
                        throw CreateDeviceOomAttempt(
                            MeshBufferGrowthMode.Geometric);
                    }
                    liveCandidates.Add(index);
                }
            },
            isRetryable: static failure =>
                failure is MeshBufferGrowthAttemptException,
            resetForRetry: () =>
            {
                calls.Add("reset");
                for (int index = liveCandidates.Count - 1;
                     index >= 0;
                     index--)
                {
                    releasedCandidates.Add(
                        liveCandidates[index]);
                }
                liveCandidates.Clear();
            },
            onRetrying: _ => calls.Add("retrying"),
            onRetrySucceeded: () => calls.Add("succeeded"));

        Assert.Multiple(() =>
        {
            Assert.That(
                releasedCandidates,
                Is.EqualTo(
                    Enumerable.Range(0, failureIndex)
                        .Reverse()));
            Assert.That(liveCandidates, Is.Empty);
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "attempt:Geometric",
                    "reset",
                    "retrying",
                    "attempt:Tight",
                    "succeeded"
                }));
        });
    }

    [Test]
    public void Retry_DoubleFailurePreservesBothAttemptReports()
    {
        AggregateException failure =
            Assert.Throws<AggregateException>(() =>
                MeshBufferGrowthRetry.Execute(
                    executeAttempt: mode =>
                        throw CreateDeviceOomAttempt(mode),
                    isRetryable: static exception =>
                        exception is
                            MeshBufferGrowthAttemptException,
                    resetForRetry: static () => { },
                    onRetrying: static _ => { },
                    onRetrySucceeded: static () => { }))!;

        Assert.Multiple(() =>
        {
            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(
                failure.InnerExceptions[0].Message,
                Does.Contain("mode=Geometric"));
            Assert.That(
                failure.InnerExceptions[1].Message,
                Does.Contain("mode=Tight"));
        });
    }

    [Test]
    public void Retry_NonMemoryFailureIsNotRetried()
    {
        bool resetCalled = false;
        var source = new InvalidOperationException("not memory");

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() =>
                MeshBufferGrowthRetry.Execute(
                    executeAttempt: _ => throw source,
                    isRetryable: static exception =>
                        exception is
                            MeshBufferGrowthAttemptException,
                    resetForRetry: () => resetCalled = true,
                    onRetrying: static _ => { },
                    onRetrySucceeded: static () => { }))!;

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.SameAs(source));
            Assert.That(resetCalled, Is.False);
        });
    }

    [Test]
    public void Retry_CleanupFailurePreventsTightAttempt()
    {
        int attemptCount = 0;

        AggregateException failure =
            Assert.Throws<AggregateException>(() =>
                MeshBufferGrowthRetry.Execute(
                    executeAttempt: mode =>
                    {
                        attemptCount++;
                        throw CreateDeviceOomAttempt(mode);
                    },
                    isRetryable: static exception =>
                        exception is
                            MeshBufferGrowthAttemptException,
                    resetForRetry: static () =>
                        throw new InvalidOperationException(
                            "cleanup failed"),
                    onRetrying: static _ => { },
                    onRetrySucceeded: static () => { }))!;

        Assert.Multiple(() =>
        {
            Assert.That(attemptCount, Is.EqualTo(1));
            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(
                failure.InnerExceptions[1].Message,
                Is.EqualTo("cleanup failed"));
        });
    }

    [Test]
    public void CompactionPolicy_SkipsOnlyDirectDeviceOom()
    {
        var deviceOom = new BufferAllocationException(
            "device oom",
            Result.ErrorOutOfDeviceMemory);
        var other = new VulkanException(
            "other",
            Result.ErrorUnknown);
        var incompleteRollback = new AggregateException(deviceOom);

        Assert.Multiple(() =>
        {
            Assert.That(
                MeshBufferCompactionFailurePolicy.ShouldSkip(
                    deviceOom),
                Is.True);
            Assert.That(
                MeshBufferCompactionFailurePolicy.ShouldSkip(other),
                Is.False);
            Assert.That(
                MeshBufferCompactionFailurePolicy.ShouldSkip(
                    incompleteRollback),
                Is.False);
        });
    }

    private static MeshBufferGrowthAttemptException
        CreateDeviceOomAttempt(MeshBufferGrowthMode mode)
    {
        MeshBufferGrowthPlan plan =
            MeshBufferGrowthPlanner.Create(
                new[]
                {
                    new MeshBufferGrowthInput(
                        MeshBufferStream.Index,
                        4,
                        5)
                },
                mode);
        return new MeshBufferGrowthAttemptException(
            plan,
            new VulkanException(
                "injected allocation failure",
                Result.ErrorOutOfDeviceMemory));
    }

    private static MeshBufferGrowthInput[] CreateNineStreamInputs(
        ulong vertexPositionCurrent,
        ulong vertexPositionRequired)
    {
        return
        [
            new(
                MeshBufferStream.VertexPosition,
                vertexPositionCurrent,
                vertexPositionRequired),
            new(MeshBufferStream.VertexNormalTangent, 8, 8),
            new(MeshBufferStream.VertexUvColor, 8, 8),
            new(MeshBufferStream.Index, 16, 16),
            new(MeshBufferStream.MeshMetadata, 4, 4),
            new(MeshBufferStream.Meshlet, 8, 8),
            new(MeshBufferStream.MeshletVertexIndex, 8, 8),
            new(MeshBufferStream.MeshletTriangleIndex, 16, 16),
            new(MeshBufferStream.SkinningData, 4, 4)
        ];
    }
}
