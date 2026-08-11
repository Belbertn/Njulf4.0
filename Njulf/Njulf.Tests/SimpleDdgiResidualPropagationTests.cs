using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiResidualPropagationTests
{
    [Test]
    public void CompleteReverseGraphPropagatesLocalEditToSameCertifiedFixedPoint()
    {
        const int probeCount = 48;
        const float gain = 0.55f;
        SimpleDdgiResidualDependencyGraph graph = BuildChainGraph(
            probeCount,
            gain,
            generation: 7u);
        var queue = new SimpleDdgiResidualPropagationQueue(graph);
        queue.BeginWave(7u, finalErrorBudget: 1.0e-6f, starvationFrames: 8u);

        float[] sparse = new float[probeCount];
        float[] source = new float[probeCount];
        source[0] = 1.0f;
        Assert.That(queue.Seed(0, 1.0f, predictedCost: 1.0f, currentFrame: 100u), Is.True);

        int sparseEvaluations = 0;
        uint frame = 100u;
        while (queue.TryDequeue(frame++, out SimpleDdgiResidualWorkItem work))
        {
            int probe = work.ProbeIndex;
            float prior = sparse[probe];
            float next = source[probe] + (probe == 0 ? 0.0f : gain * sparse[probe - 1]);
            sparse[probe] = next;
            sparseEvaluations++;
            queue.CompleteAndPropagate(
                work,
                MathF.Abs(next - prior),
                _ => 1.0f,
                frame);
        }

        float[] full = SolveFullChain(source, gain, tolerance: 1.0e-7f, out int fullEvaluations);
        // The sparse phase is an ordering accelerator. A complete frozen audit
        // remains mandatory and verifies that no above-budget work was missed.
        RunCompleteAuditSweeps(sparse, source, gain, tolerance: 1.0e-7f);

        Assert.Multiple(() =>
        {
            Assert.That(queue.FullSweepRequired, Is.False);
            Assert.That(queue.CompleteAuditRequired, Is.True);
            Assert.That(sparse, Is.EqualTo(full).Within(2.0e-6f));
            Assert.That(sparseEvaluations, Is.LessThan(fullEvaluations / 2));
            Assert.That(queue.EnqueuedDependentCount, Is.GreaterThan(0));
            Assert.That(queue.PendingCount, Is.Zero);
        });
    }

    [Test]
    public void OverflowAndIncompleteCoverageFailClosedToFullSweep()
    {
        var overflow = new SimpleDdgiResidualDependencyGraph(6, capacityPerSource: 2);
        overflow.BeginBuild(3u);
        Assert.That(overflow.RecordDependency(0, 1, 0.5f), Is.True);
        Assert.That(overflow.RecordDependency(0, 2, 0.5f), Is.True);
        Assert.That(overflow.RecordDependency(0, 3, 0.5f), Is.False);
        for (int probe = 0; probe < 6; probe++)
            overflow.MarkConsumerComplete(probe);
        Assert.That(overflow.Seal(AllParticipants(6)), Is.False);

        var overflowQueue = new SimpleDdgiResidualPropagationQueue(overflow);
        overflowQueue.BeginWave(3u, 0.001f);
        Assert.That(
            overflowQueue.FallbackReason,
            Is.EqualTo(SimpleDdgiResidualFallbackReason.DependencyCapacityOverflow));

        var incomplete = new SimpleDdgiResidualDependencyGraph(4);
        incomplete.BeginBuild(9u);
        incomplete.MarkConsumerComplete(0);
        Assert.That(incomplete.Seal(AllParticipants(4)), Is.False);
        var incompleteQueue = new SimpleDdgiResidualPropagationQueue(incomplete);
        incompleteQueue.BeginWave(9u, 0.001f);
        Assert.That(
            incompleteQueue.FallbackReason,
            Is.EqualTo(SimpleDdgiResidualFallbackReason.DependencyBuildIncomplete));
    }

    [Test]
    public void LargestReductionPerCostWinsUntilStarvationDeadline()
    {
        SimpleDdgiResidualDependencyGraph graph = BuildChainGraph(3, 0.5f, 11u);
        var queue = new SimpleDdgiResidualPropagationQueue(graph);
        queue.BeginWave(11u, 0.0f, starvationFrames: 5u);
        queue.Seed(0, measuredIrradianceChange: 1.0f, predictedCost: 10.0f, currentFrame: 20u);
        queue.Seed(1, measuredIrradianceChange: 0.5f, predictedCost: 1.0f, currentFrame: 20u);

        Assert.That(queue.TryDequeue(21u, out SimpleDdgiResidualWorkItem scored), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(scored.ProbeIndex, Is.EqualTo(1));
            Assert.That(scored.DeadlineForced, Is.False);
        });
        queue.CompleteAndPropagate(scored, 0.0f, _ => 1.0f, 21u);

        Assert.That(queue.TryDequeue(25u, out SimpleDdgiResidualWorkItem forced), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(forced.ProbeIndex, Is.EqualTo(0));
            Assert.That(forced.DeadlineForced, Is.True);
        });
    }

    [Test]
    public void StaleGenerationAndNonFiniteResidualRequestFullSweep()
    {
        SimpleDdgiResidualDependencyGraph graph = BuildChainGraph(2, 0.5f, 4u);
        var queue = new SimpleDdgiResidualPropagationQueue(graph);
        queue.BeginWave(5u, 0.01f);
        Assert.That(
            queue.FallbackReason,
            Is.EqualTo(SimpleDdgiResidualFallbackReason.DependencyGenerationMismatch));

        queue.BeginWave(4u, 0.01f);
        Assert.That(queue.Seed(0, float.NaN, 1.0f, 0u), Is.False);
        Assert.That(
            queue.FallbackReason,
            Is.EqualTo(SimpleDdgiResidualFallbackReason.InvalidResidual));
    }

    [Test]
    public void PackedGpuHintPreservesConservativeResidualAndWrappedDeadline()
    {
        const float residual = 0.02501f;
        uint packed = SimpleDdgiPackedResidualState.PackConservative(
            residual,
            transportGeneration: 0x1234u,
            deadlineFrame: 0x1feu);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiPackedResidualState.DecodeResidual(packed),
                Is.GreaterThanOrEqualTo(residual));
            Assert.That(
                SimpleDdgiPackedResidualState.DecodeGeneration(packed),
                Is.EqualTo(0x34u));
            Assert.That(
                SimpleDdgiPackedResidualState.DecodeDeadline(packed),
                Is.EqualTo(0xfeu));
            Assert.That(
                SimpleDdgiPackedResidualState.DeadlineReached(0x1fdu, packed),
                Is.False);
            Assert.That(
                SimpleDdgiPackedResidualState.DeadlineReached(0x1feu, packed),
                Is.True);
            Assert.That(
                SimpleDdgiPackedResidualState.DeadlineReached(0x205u, packed),
                Is.True);
        });
    }

    private static SimpleDdgiResidualDependencyGraph BuildChainGraph(
        int probeCount,
        float gain,
        uint generation)
    {
        var graph = new SimpleDdgiResidualDependencyGraph(probeCount, 4);
        graph.BeginBuild(generation);
        for (int source = 0; source + 1 < probeCount; source++)
            Assert.That(graph.RecordDependency(source, source + 1, gain), Is.True);
        for (int consumer = 0; consumer < probeCount; consumer++)
            graph.MarkConsumerComplete(consumer);
        Assert.That(graph.Seal(AllParticipants(probeCount)), Is.True);
        return graph;
    }

    private static bool[] AllParticipants(int count)
    {
        bool[] participants = new bool[count];
        Array.Fill(participants, true);
        return participants;
    }

    private static float[] SolveFullChain(
        float[] source,
        float gain,
        float tolerance,
        out int evaluations)
    {
        float[] values = new float[source.Length];
        float[] next = new float[source.Length];
        evaluations = 0;
        for (int iteration = 0; iteration < 256; iteration++)
        {
            float maximumDelta = 0.0f;
            for (int probe = 0; probe < values.Length; probe++)
            {
                next[probe] = source[probe] +
                    (probe == 0 ? 0.0f : gain * values[probe - 1]);
                maximumDelta = MathF.Max(
                    maximumDelta,
                    MathF.Abs(next[probe] - values[probe]));
                evaluations++;
            }
            (values, next) = (next, values);
            if (maximumDelta <= tolerance)
                break;
        }
        return values;
    }

    private static void RunCompleteAuditSweeps(
        float[] values,
        float[] source,
        float gain,
        float tolerance)
    {
        float[] next = new float[values.Length];
        for (int iteration = 0; iteration < 256; iteration++)
        {
            float maximumDelta = 0.0f;
            for (int probe = 0; probe < values.Length; probe++)
            {
                next[probe] = source[probe] +
                    (probe == 0 ? 0.0f : gain * values[probe - 1]);
                maximumDelta = MathF.Max(
                    maximumDelta,
                    MathF.Abs(next[probe] - values[probe]));
            }
            Array.Copy(next, values, values.Length);
            if (maximumDelta <= tolerance)
                return;
        }
        Assert.Fail("The complete audit oracle did not converge.");
    }
}
