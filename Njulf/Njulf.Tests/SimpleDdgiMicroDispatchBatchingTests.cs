using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiMicroDispatchBatchingTests
{
    [Test]
    public void EmptyAndMultiVolumeWorkloads_PreserveBatchingContracts()
    {
        uint[] empty = BuildDispatches(
            candidateCapacity: 256,
            eligible: 0,
            requestBudget: 64,
            accepted: 0,
            activeVolumes: 3,
            specializedTail: false);
        Assert.Multiple(() =>
        {
            Assert.That(empty[(int)SimpleDdgiSchedulerDispatchSlot.Classify],
                Is.GreaterThan(0));
            Assert.That(empty[(int)SimpleDdgiSchedulerDispatchSlot.Compact],
                Is.Zero);
            Assert.That(empty[(int)SimpleDdgiSchedulerDispatchSlot.Admit],
                Is.Zero);
            Assert.That(empty[(int)SimpleDdgiSchedulerDispatchSlot.TailAdmit],
                Is.Zero);
            Assert.That(empty[(int)SimpleDdgiSchedulerDispatchSlot.MaterializeClassify],
                Is.Zero);
            Assert.That(empty[(int)SimpleDdgiSchedulerDispatchSlot.Commit],
                Is.Zero);
        });

        uint[] generic = BuildDispatches(256, 5, 64, 5, 3, false);
        uint[] tail = BuildDispatches(256, 5, 64, 5, 3, true);
        Assert.Multiple(() =>
        {
            Assert.That(generic[(int)SimpleDdgiSchedulerDispatchSlot.Admit],
                Is.EqualTo(1));
            Assert.That(generic[(int)SimpleDdgiSchedulerDispatchSlot.TailAdmit],
                Is.Zero);
            Assert.That(tail[(int)SimpleDdgiSchedulerDispatchSlot.Admit],
                Is.Zero);
            Assert.That(tail[(int)SimpleDdgiSchedulerDispatchSlot.TailAdmit],
                Is.EqualTo(1));
            Assert.That(generic[(int)SimpleDdgiSchedulerDispatchSlot.MaterializeClassify],
                Is.EqualTo(SimpleDdgiGpuSchedulerLayout.GroupsFor(5)));
            Assert.That(generic[(int)SimpleDdgiSchedulerDispatchSlot.EmitScatter],
                Is.EqualTo(SimpleDdgiGpuSchedulerLayout.GroupsFor(5)));
            Assert.That(generic[(int)SimpleDdgiSchedulerDispatchSlot.Commit],
                Is.EqualTo(3));
        });

        Outcome[] outcomes =
        [
            new(0, 0, true, true, true, 0),
            new(0, 0, true, true, true, 1),
            new(1, 1, false, false, true, 1),
            new(2, 2, true, true, true, 2),
            new(2, 2, true, true, false, 1)
        ];
        Dictionary<int, int> expectedScroll = new()
        {
            [0] = 2,
            [2] = 2
        };
        WorkloadResult result = RunCommitModel(outcomes, expectedScroll);

        Assert.Multiple(() =>
        {
            Assert.That(result.BucketCounts.Sum(), Is.EqualTo(outcomes.Length),
                "Every accepted outcome must appear in exactly one emit bucket.");
            Assert.That(result.BucketCounts, Is.EqualTo(new[] { 1, 3, 1, 0, 0, 0 }));
            Assert.That(result.ScrollGateByVolume[0], Is.True);
            Assert.That(result.ScrollGateByVolume[2], Is.False);
            Assert.That(result.LocalCommits.Select(static item => item.Volume),
                Is.EqualTo(new[] { 0, 0, 1 }));
            Assert.That(result.Propagations.Select(static item => item.Volume),
                Is.EqualTo(new[] { 0, 0, 1 }));
            Assert.That(result.Feedback,
                Is.EqualTo(new FeedbackTotals(5, 4, 4, 3, 3)));
        });

        foreach (int volume in new[] { 0, 1, 2 })
        {
            int validation = result.Events.IndexOf($"validate:{volume}");
            int firstLocal = result.Events.FindIndex(
                value => value.StartsWith($"local:{volume}:", StringComparison.Ordinal));
            int lastLocal = result.Events.FindLastIndex(
                value => value.StartsWith($"local:{volume}:", StringComparison.Ordinal));
            int firstPropagation = result.Events.FindIndex(
                value => value.StartsWith($"propagate:{volume}:", StringComparison.Ordinal));
            Assert.That(validation, Is.GreaterThanOrEqualTo(0));
            if (firstLocal >= 0)
            {
                Assert.That(firstLocal, Is.GreaterThan(validation));
                Assert.That(firstPropagation, Is.GreaterThan(lastLocal));
            }
        }

        string laneBase = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_lane_base.comp");
        string admit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_admit.comp");
        string materialize = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_materialize.comp");
        string commit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_commit.comp");
        string feedback = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_feedback.comp");
        Assert.Multiple(() =>
        {
            Assert.That(laneBase, Does.Contain(
                "? SIMPLE_DDGI_SCHEDULER_DISPATCH_TAIL_ADMIT"));
            Assert.That(laneBase, Does.Contain(
                ": SIMPLE_DDGI_SCHEDULER_DISPATCH_ADMIT"));
            Assert.That(admit, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_DISPATCH_MATERIALIZE_CLASSIFY"));
            Assert.That(materialize, Does.Contain("SchedulerWriteOutcome("));
            Assert.That(materialize, Does.Contain("WorkgroupBucketCounts"));
            Assert.That(commit, Does.Contain("ValidateScrollCohort(volumeIndex);"));
            Assert.That(commit, Does.Contain("barrier();"));
            Assert.That(commit.IndexOf("CommitLocalOutcome(", StringComparison.Ordinal),
                Is.LessThan(commit.IndexOf(
                    "CommitPropagationOutcome(", StringComparison.Ordinal)));
            Assert.That(feedback, Does.Contain("feedbackSummary[localIndex]"));
            Assert.That(feedback.LastIndexOf(
                    "SchedulerArenaWrite(base + 0u, SchedulerFrame(12u));",
                    StringComparison.Ordinal),
                Is.GreaterThan(feedback.LastIndexOf(
                    "SIMPLE_DDGI_SCHEDULER_FEEDBACK_ADAPTIVE_ERROR_CONTENT_OFFSET",
                    StringComparison.Ordinal)));
        });
    }

    private static uint[] BuildDispatches(
        uint candidateCapacity,
        uint eligible,
        uint requestBudget,
        uint accepted,
        uint activeVolumes,
        bool specializedTail)
    {
        uint[] dispatches = new uint[(int)SimpleDdgiSchedulerDispatchSlot.Count];
        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.Reset] = 1;
        if (candidateCapacity == 0)
            return dispatches;

        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.Classify] =
            SimpleDdgiGpuSchedulerLayout.GroupsFor((int)candidateCapacity);
        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.Prefix] =
            SimpleDdgiGpuSchedulerLayout.GroupsFor(
                (SimpleDdgiSchedulerAbi.MaxLaneCount + 1) / 2);
        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.LaneBase] = 1;
        if (eligible == 0 || requestBudget == 0)
            return dispatches;

        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.Compact] =
            SimpleDdgiGpuSchedulerLayout.GroupsFor((int)candidateCapacity);
        dispatches[(int)(specializedTail
            ? SimpleDdgiSchedulerDispatchSlot.TailAdmit
            : SimpleDdgiSchedulerDispatchSlot.Admit)] = 1;
        if (accepted == 0)
            return dispatches;

        uint acceptedGroups =
            SimpleDdgiGpuSchedulerLayout.GroupsFor((int)accepted);
        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.MaterializeClassify] =
            acceptedGroups;
        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.EmitPrefix] = 1;
        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.EmitScatter] =
            acceptedGroups;
        dispatches[(int)SimpleDdgiSchedulerDispatchSlot.Commit] = activeVolumes;
        return dispatches;
    }

    private static WorkloadResult RunCommitModel(
        IReadOnlyList<Outcome> outcomes,
        IReadOnlyDictionary<int, int> expectedScroll)
    {
        int[] bucketCounts = new int[6];
        foreach (Outcome outcome in outcomes)
            bucketCounts[outcome.Bucket]++;

        Dictionary<int, bool> gates = expectedScroll.ToDictionary(
            static pair => pair.Key,
            pair => outcomes.Count(outcome =>
                    outcome.Volume == pair.Key && outcome.IsScroll) == pair.Value &&
                outcomes.Where(outcome =>
                        outcome.Volume == pair.Key && outcome.IsScroll)
                    .All(static outcome => outcome.Complete));
        List<Outcome> local = [];
        List<Outcome> propagation = [];
        List<string> events = [];
        foreach (int volume in outcomes.Select(static item => item.Volume).Distinct())
        {
            events.Add($"validate:{volume}");
            foreach ((Outcome outcome, int index) in outcomes
                         .Select(static (item, index) => (item, index))
                         .Where(pair => pair.item.Volume == volume &&
                             pair.item.Complete &&
                             (!pair.item.MandatoryScroll || gates[volume])))
            {
                local.Add(outcome);
                events.Add($"local:{volume}:{index}");
            }
            foreach ((Outcome outcome, int index) in outcomes
                         .Select(static (item, index) => (item, index))
                         .Where(pair => pair.item.Volume == volume &&
                             pair.item.Complete &&
                             (!pair.item.MandatoryScroll || gates[volume])))
            {
                propagation.Add(outcome);
                events.Add($"propagate:{volume}:{index}");
            }
        }

        FeedbackTotals feedback = new(
            Accepted: outcomes.Count,
            ScrollExpected: expectedScroll.Values.Sum(),
            ScrollAccepted: outcomes.Count(static outcome => outcome.IsScroll),
            ScrollTraced: outcomes.Count(static outcome =>
                outcome.IsScroll && outcome.Complete),
            Committed: local.Count);
        return new(bucketCounts, gates, local, propagation, events, feedback);
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate repository file.",
            Path.Combine(pathParts));
    }

    private readonly record struct Outcome(
        int Volume,
        int Ring,
        bool MandatoryScroll,
        bool IsScroll,
        bool Complete,
        int Bucket);

    private readonly record struct FeedbackTotals(
        int Accepted,
        int ScrollExpected,
        int ScrollAccepted,
        int ScrollTraced,
        int Committed);

    private sealed record WorkloadResult(
        int[] BucketCounts,
        Dictionary<int, bool> ScrollGateByVolume,
        List<Outcome> LocalCommits,
        List<Outcome> Propagations,
        List<string> Events,
        FeedbackTotals Feedback);
}
