using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGpuSchedulerLayoutTests
{
    [Test]
    public void Layout_IsAlignedNonOverlappingAndUsesActiveProbeOutputCapacity()
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(
            activeProbeCount: 15368,
            requestCapacity: 2048,
            activeVolumeCount: 3,
            dirtyRegionCapacity: 32,
            validationEnabled: true);

        ulong previousEnd = 0;
        foreach (SimpleDdgiSchedulerArenaRegion region in layout.Regions)
        {
            Assert.That(region.Offset % SimpleDdgiGpuSchedulerLayout.ArenaAlignmentBytes, Is.EqualTo(0), region.Name);
            Assert.That(region.ByteSize % SimpleDdgiGpuSchedulerLayout.ArenaAlignmentBytes, Is.EqualTo(0), region.Name);
            Assert.That(region.Offset, Is.GreaterThanOrEqualTo(previousEnd), region.Name);
            Assert.That(region.End, Is.LessThanOrEqualTo(layout.TotalBytes), region.Name);
            previousEnd = region.End;
        }

        Assert.Multiple(() =>
        {
            Assert.That(layout.CandidateInput.ElementCount, Is.EqualTo(15368));
            Assert.That(layout.ReceiverContribution.ElementCount, Is.EqualTo(15368));
            Assert.That(layout.ReceiverContribution.ElementStride,
                Is.EqualTo(SimpleDdgiGpuSchedulerLayout.ReceiverContributionStrideBytes));
            Assert.That(layout.ReceiverContribution.ByteSize,
                Is.EqualTo(15368UL *
                    (ulong)SimpleDdgiGpuSchedulerLayout.ReceiverContributionStrideBytes));
            Assert.That(layout.CandidateOutput.ElementCount, Is.EqualTo(15368));
            Assert.That(layout.CandidateGroupLaneCounts.ElementStride, Is.EqualTo(sizeof(uint)));
            Assert.That(layout.CandidateGroupLaneCounts.ElementCount,
                Is.EqualTo((uint)layout.CandidateGroupLaneCountWordCount));
            Assert.That(layout.CandidateGroupLaneCounts.ByteSize,
                Is.EqualTo((ulong)layout.CandidateGroupLaneCountWordCount * sizeof(uint)));
            Assert.That(layout.UpdateRecords.ElementCount, Is.EqualTo(2048));
            Assert.That(layout.Outcomes.ElementCount, Is.EqualTo(2048));
            Assert.That(layout.ActiveLaneCount, Is.EqualTo(3 * 7 * 4 * 2));
            Assert.That(layout.ValidationReadbackBytes, Is.EqualTo(SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes));
            Assert.That(layout.FeedbackSummary.ByteSize, Is.EqualTo(4096));
            Assert.That(layout.AuditWorkspace.ElementCount,
                Is.EqualTo(SimpleDdgiGpuSchedulerLayout.TransportAuditWorkspaceProbeCapacity));
            Assert.That(layout.AuditWorkspace.ElementStride,
                Is.EqualTo(SimpleDdgiGpuSchedulerLayout.TransportAuditWorkspaceStrideBytes));
            Assert.That(layout.AuditWorkspace.ByteSize,
                Is.EqualTo(SimpleDdgiGpuSchedulerLayout.TransportAuditWorkspaceBytes));
        });
    }

    [TestCase(15368)]
    [TestCase(32768)]
    public void PackedGroupLaneRegion_UsesWhole32BitWords(int probeCount)
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(
            probeCount,
            Math.Min(2048, probeCount),
            activeVolumeCount: 16);
        ulong entryCount = checked(
            (ulong)SimpleDdgiGpuSchedulerLayout.GroupsFor(probeCount) *
            (ulong)SimpleDdgiSchedulerAbi.MaxLaneCount);
        ulong expectedWords = checked((entryCount + 1UL) / 2UL);

        Assert.Multiple(() =>
        {
            Assert.That(layout.CandidateGroupLaneCounts.ElementCount, Is.EqualTo((uint)expectedWords));
            Assert.That(layout.CandidateGroupLaneCounts.ByteSize,
                Is.EqualTo(expectedWords * sizeof(uint)));
            Assert.That(layout.CandidateGroupLaneCounts.End,
                Is.LessThanOrEqualTo(layout.CandidateOutput.Offset));
        });
    }

    [Test]
    public void ResetGroupLaneWrites_DoNotTouchCandidateOutputGuardPattern()
    {
        const uint guard = 0xA5A5_5A5Au;
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(
            32768,
            2048,
            activeVolumeCount: 16);
        int outputStart = checked((int)(layout.CandidateOutput.Offset / sizeof(uint)));
        int outputWords = checked((int)(layout.CandidateOutput.ByteSize / sizeof(uint)));
        uint[] arena = new uint[checked((int)(layout.TotalBytes / sizeof(uint)))];
        Array.Fill(arena, guard, outputStart, outputWords);

        // This is the reset.comp addressing contract: one uint write per packed
        // group/lane pair. Keep the output range as a guard immediately after it.
        int groupStart = checked((int)(layout.CandidateGroupLaneCounts.Offset / sizeof(uint)));
        int groupWords = checked((int)(layout.CandidateGroupLaneCounts.ByteSize / sizeof(uint)));
        for (int word = 0; word < groupWords; word++)
            arena[groupStart + word] = 0u;

        Assert.That(arena.AsSpan(outputStart, outputWords).ToArray(),
            Is.All.EqualTo(guard));
    }

    [Test]
    public void Layout_IndirectSlotsAreFixedAndAligned()
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(64, 16, 1);

        for (SimpleDdgiSchedulerDispatchSlot slot = 0;
             slot < SimpleDdgiSchedulerDispatchSlot.Count;
             slot++)
        {
            SimpleDdgiSchedulerArenaRegion command = layout.GetIndirectCommand(slot);
            Assert.Multiple(() =>
            {
                Assert.That(command.Offset % 16, Is.EqualTo(0));
                Assert.That(command.ByteSize, Is.EqualTo(16));
                Assert.That(command.Offset, Is.EqualTo(
                    layout.IndirectCommands.Offset + (ulong)slot * 16UL));
            });
        }
    }

    [Test]
    public void Layout_RayBucketRecordsKeepIndirectDimensionsSeparateFromMetadata()
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(64, 16, 1);

        Assert.That(
            layout.RayBucketCommands.ElementStride,
            Is.EqualTo(SimpleDdgiGpuSchedulerLayout.IndirectCommandStrideBytes));
        Assert.That(
            layout.RayBucketMetadata.ElementStride,
            Is.EqualTo(SimpleDdgiGpuSchedulerLayout.RayBucketMetadataStrideBytes));
        Assert.That(layout.RayBucketCommands.Offset,
            Is.GreaterThanOrEqualTo(layout.RayBucketMetadata.End));

        for (int bucket = 0; bucket < SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount; bucket++)
        {
            SimpleDdgiSchedulerArenaRegion command = layout.GetRayBucketIndirectCommand(bucket);
            SimpleDdgiSchedulerArenaRegion metadata = layout.GetRayBucketMetadata(bucket);

            Assert.Multiple(() =>
            {
                Assert.That(command.Offset % 16, Is.EqualTo(0));
                Assert.That(command.ByteSize, Is.EqualTo(
                    (ulong)SimpleDdgiGpuSchedulerLayout.IndirectCommandStrideBytes));
                Assert.That(command.Offset, Is.EqualTo(layout.RayBucketCommands.Offset +
                    (ulong)bucket * (ulong)SimpleDdgiGpuSchedulerLayout.IndirectCommandStrideBytes));
                Assert.That(metadata.Offset, Is.EqualTo(layout.RayBucketMetadata.Offset +
                    (ulong)bucket * (ulong)SimpleDdgiGpuSchedulerLayout.RayBucketMetadataStrideBytes));
                Assert.That(metadata.ByteSize, Is.EqualTo(
                    (ulong)SimpleDdgiGpuSchedulerLayout.RayBucketMetadataStrideBytes));
            });
        }
    }

    [Test]
    public void RayBucketShaderAbi_PreservesUnitIndirectYAndZDimensions()
    {
        string schedulerShared = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_shared.glsl");
        string reset = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_reset.comp");
        string emit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_emit.comp");
        string consumerShared = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_shared.glsl");
        string trace = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_trace.comp");
        string transport = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_transport.comp");

        Assert.Multiple(() =>
        {
            Assert.That(schedulerShared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_COMMAND_WORDS"));
            Assert.That(reset, Does.Contain(
                "command * SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_COMMAND_WORDS"));
            Assert.That(reset, Does.Not.Contain(
                "SchedulerArenaWrite(pc.LaneCursorsOffsetWords + i, 0u);"));
            Assert.That(emit, Does.Contain(
                "SchedulerArenaWrite(commandBase + 1u, 1u);"));
            Assert.That(emit, Does.Contain(
                "SchedulerArenaWrite(commandBase + 2u, 1u);"));
            Assert.That(emit, Does.Contain(
                "uint metadataBase = pc.RayBucketMetadataOffsetWords +"));
            Assert.That(emit, Does.Not.Contain(
                "SchedulerArenaWrite(commandBase + 1u, bucketOffset)"));
            Assert.That(emit, Does.Not.Contain(
                "SchedulerArenaWrite(commandBase + 2u, bucketCount)"));
            Assert.That(emit, Does.Match(
                @"SIMPLE_DDGI_SCHEDULER_DISPATCH_BLEND,\s*accepted, 1u, 1u\)"));
            Assert.That(emit, Does.Match(
                @"SIMPLE_DDGI_SCHEDULER_DISPATCH_PUBLISH,\s*accepted, 1u, 1u\)"));
            Assert.That(consumerShared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_RAY_BUCKET_METADATA_WORDS = 4u"));
            Assert.That(consumerShared, Does.Contain(
                "SimpleDdgiResolveSchedulerRayBucket"));
            Assert.That(trace, Does.Contain("dispatchProbeCount = schedulerProbeCount;"));
            Assert.That(trace, Does.Contain("dispatchRaysPerProbe = schedulerRaysPerProbe;"));
            Assert.That(transport, Does.Contain("dispatchProbeCount = schedulerProbeCount;"));
            Assert.That(transport, Does.Contain("dispatchRaysPerProbe = schedulerRaysPerProbe;"));
        });
    }

    [Test]
    public void ResidentShadersUseOutcomeGenerationChecksAndPrivateCommitTargets()
    {
        string shared = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_shared.glsl");
        string metadataAbi = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_scheduler_metadata_abi.glsl");
        string consumerShared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
        string emit = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_emit.comp");
        string classify = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_classify.comp");
        string trace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
        string transport = ReadRepoText("Njulf.Shaders", "ddgi_simple_transport.comp");
        string relocate = ReadRepoText("Njulf.Shaders", "ddgi_simple_relocate_classify.comp");
        string blend = ReadRepoText("Njulf.Shaders", "ddgi_simple_blend.comp");
        string publish = ReadRepoText("Njulf.Shaders", "ddgi_simple_publish.comp");
        string sampledPublish = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_publish_sampled.comp");
        string commit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_commit_local.comp");
        string reset = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_reset.comp");
        string laneBase = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_lane_base.comp");
        string prefix = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_prefix.comp");
        string compact = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_compact.comp");
        string admit = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_admit.comp");
        string materialize = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_materialize.comp");
        string schedulePass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiSchedulePass.cs");
        string feedback = ReadRepoText("Njulf.Shaders", "ddgi_simple_schedule_feedback.comp");
        string shaderProject = ReadRepoText("Njulf.Shaders", "Njulf.Shaders.csproj");

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS = 15u"));
            Assert.That(shared, Does.Contain("SchedulerQueueGeneration()"));
            Assert.That(shared, Does.Contain("SchedulerResourceGeneration()"));
            Assert.That(shared, Does.Contain("SchedulerVolumeGeneration()"));
            Assert.That(shared, Does.Contain("SchedulerSourceGeneration()"));
            Assert.That(shared, Does.Contain("SchedulerTransportGeneration()"));
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEATURE_PERIODIC_SOURCE_WAVE = 1u << 10u"));
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEATURE_CERTIFIED_QUIESCED = 1u << 11u"));
            Assert.That(shared, Does.Contain("SchedulerCertifiedQuiesced()"));
            Assert.That(shared, Does.Contain(
                "SchedulerSourceRefreshDueAtCutoff("));
            Assert.That(shared, Does.Contain("SchedulerRequiredCompletionMask"));
            Assert.That(shared, Does.Contain("SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_MASK"));
            Assert.That(shared, Does.Contain(
                "#include \"ddgi_simple_scheduler_metadata_abi.glsl\""));
            Assert.That(metadataAbi, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR = 1u << 30u"));
            Assert.That(shared, Does.Contain("value &= ~SIMPLE_DDGI_SCHEDULER_PRIVATE_REASON_MASK"));
            Assert.That(consumerShared, Does.Contain("memoryBarrierBuffer();"));
            Assert.That(consumerShared, Does.Contain("atomicAdd("));
            Assert.That(classify, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR"));
            Assert.That(classify, Does.Contain("periodicWaveMember"));
            Assert.That(classify, Does.Contain("periodicRefreshGateOpen"));
            Assert.That(classify, Does.Contain("routineDue);"));

            Assert.That(emit, Does.Contain("SchedulerWriteOutcome("));
            Assert.That(emit, Does.Contain("SchedulerMarkOutcomeComplete("));
            Assert.That(emit, Does.Contain("SIMPLE_DDGI_SCHEDULER_TRANSPORT_NOT_REQUIRED"));
            Assert.That(trace, Does.Contain("SimpleDdgiSchedulerRayComplete("));
            Assert.That(transport, Does.Contain("SimpleDdgiSchedulerRayComplete("));

            Assert.That(relocate, Does.Contain("pc.SchedulerUpdateRecordsOffsetWords"));
            Assert.That(relocate, Does.Contain(
                "update.outcomeIndex * SIMPLE_DDGI_SCHEDULER_UPDATE_RECORD_WORDS"));
            Assert.That(consumerShared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_UPDATE_RECORD_WORDS = 10u"));
            Assert.That(relocate, Does.Contain("SimpleDdgiSchedulerCompleteSingle("));
            Assert.That(classify, Does.Contain("SIMPLE_DDGI_SCHEDULER_REASON_TOPOLOGY"));
            Assert.That(classify, Does.Not.Contain("SchedulerArenaWrite(schedulerStateBase + 5u"));
            Assert.That(classify, Does.Not.Contain("SchedulerArenaWrite(schedulerStateBase + 8u"));
            Assert.That(blend, Does.Contain("TransportWriteIrradianceAtlasBufferIndex"));
            Assert.That(blend, Does.Contain("pc.PrivateVisibilityAtlasOffsetWords"));
            Assert.That(blend, Does.Contain("aborted transaction's stale private"));
            Assert.That(blend, Does.Contain("SimpleDdgiSchedulerCompleteSingle("));

            Assert.That(publish, Does.Contain("residentTransaction"));
            Assert.That(publish, Does.Contain("SimpleDdgiSchedulerOutcomeAllowsPublication("));
            Assert.That(publish, Does.Contain("PublishSimpleDdgiReceiverProbe("));
            Assert.That(publish, Does.Contain("pc.ReceiverProbeBufferIndex"));
            Assert.That(sampledPublish, Does.Contain("residentTransaction"));
            Assert.That(sampledPublish, Does.Contain("pc.PrivateVisibilityAtlasOffsetWords"));
            Assert.That(sampledPublish, Does.Contain("SimpleDdgiSchedulerCompleteSingle("));

            Assert.That(commit, Does.Contain("completionMask & requiredMask"));
            Assert.That(commit, Does.Contain("currentGeneration != expectedGeneration"));
            Assert.That(commit, Does.Contain("MarkPrivateRepair(probeIndex)"));
            Assert.That(commit, Does.Contain("pc.TransportIrradianceAtlasBufferIndex"));
            Assert.That(commit, Does.Contain("pc.VisibilityAtlasBufferIndex"));
            Assert.That(commit, Does.Contain("SchedulerGpuResident()"));
            Assert.That(commit, Does.Contain("SchedulerFindVolume(probeIndex, volumeIndex)"));
            Assert.That(commit, Does.Contain("SchedulerArenaWrite(stateBase + 5u"));
            Assert.That(commit, Does.Contain("SchedulerArenaWrite(stateBase + 8u"));
            Assert.That(commit, Does.Contain("TryPackSimpleDdgiReceiverProbe("));
            Assert.That(commit, Does.Contain("PublishPackedSimpleDdgiReceiverProbe("));

            Assert.That(reset, Does.Not.Contain("pc.LaneCursorsOffsetWords"));
            Assert.That(reset, Does.Contain("if (!certifiedQuiesced)"));
            Assert.That(reset, Does.Contain("? 64u"));
            Assert.That(laneBase, Does.Contain("pc.LanePrefixesOffsetWords"));
            Assert.That(laneBase, Does.Not.Contain("pc.LaneCursorsOffsetWords"));
            Assert.That(laneBase, Does.Contain("if (gl_GlobalInvocationID.x != 0u)"));
            Assert.That(laneBase, Does.Contain(
                "for (uint lane = 0u; lane < SIMPLE_DDGI_SCHEDULER_MAX_LANES; lane++)"));
            Assert.That(laneBase, Does.Not.Contain("for (uint i = 0u; i < lane; i++)"));
            Assert.That(laneBase, Does.Contain(
                "pc.LaneTotalsOffsetWords + activeLaneCount"));
            Assert.That(laneBase, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_INVALID_PROBE"));
            Assert.That(laneBase, Does.Match(
                @"SchedulerWriteIndirect\(SIMPLE_DDGI_SCHEDULER_DISPATCH_ADMIT,\s*1u, 1u, 1u\);"));
            Assert.That(prefix, Does.Contain("uint lanePair = gl_GlobalInvocationID.x;"));
            Assert.That(prefix, Does.Contain("uint packedCounts = SchedulerArenaRead(wordOffset);"));
            Assert.That(prefix, Does.Not.Contain("SchedulerWriteGroupLaneValue"));
            Assert.That(prefix, Does.Not.Contain("pc.LaneTotalsOffsetWords"));
            Assert.That(compact, Does.Contain(
                "shared uint CandidateLanes[SIMPLE_DDGI_SCHEDULER_WORKGROUP_SIZE];"));
            Assert.That(compact, Does.Contain("CandidateLanes[localIndex] = lane;"));
            Assert.That(compact, Does.Not.Contain("uint priorBase ="));
            Assert.That(admit, Does.Contain("bool tailSourceOnly"));
            Assert.That(admit, Does.Contain("bool tailSolveOnly"));
            Assert.That(admit, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_ADMISSION_ROLE"));
            Assert.That(admit, Does.Contain(
                "const bool specializedTailPhase = true"));
            Assert.That(admit, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_COMPACTED) == 0u"));
            Assert.That(admit, Does.Contain("if (requestBudget == 0u)"));
            Assert.That(admit, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_DISPATCH_EMIT"));
            Assert.That(admit, Does.Contain("if (!specializedTailPhase)"));
            Assert.That(admit, Does.Contain(
                "volume >= activeVolumes || specializedTailPhase"));
            Assert.That(admit, Does.Contain("specializedPendingByVolume"));
            Assert.That(admit, Does.Contain(
                "bool specializedTransport = tailSourceOnly"));
            Assert.That(admit, Does.Contain(
                "pc.LaneTotalsOffsetWords + activeIndex"));
            Assert.That(admit, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_REASON_ROUTINE_DUE"));
            Assert.That(admit, Does.Contain(
                "(visiblePageBudget * pending) /"));
            Assert.That(admit, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_VISIBLE_PAGE_COHORT_BASE"));
            Assert.That(admit, Does.Contain(
                "(remaining * available) / residualPendingTotal"));
            Assert.That(admit, Does.Contain(
                "quotas[volume] >= specializedPendingByVolume[volume]"));
            Assert.That(admit, Does.Contain("uint admittedLaneMask[28]"));
            Assert.That(admit, Does.Match(
                @"admittedLaneMask\[lane >> 5u\]\s*\|=\s*1u << \(lane & 31u\)"));
            Assert.That(admit, Does.Contain("SchedulerWriteUpdateSelection("));
            Assert.That(admit, Does.Not.Contain("SchedulerWriteUpdate("));
            Assert.That(admit, Does.Not.Contain(
                "for (uint lane = 0u; lane < SIMPLE_DDGI_SCHEDULER_MAX_LANES; lane++)"));
            Assert.That(admit, Does.Contain(
                "uint firstPhase = tailSourceOnly ? 0u : (tailSolveOnly ? 6u : 0u);"));
            Assert.That(schedulePass, Does.Contain(
                "SimpleDdgiGpuSchedulerLayout.GroupsFor((layout.LaneCapacity + 1) / 2)"));
            Assert.That(schedulePass, Does.Contain("SimpleDdgiSchedule.Admit"));
            Assert.That(schedulePass, Does.Contain("SimpleDdgiSchedule.TailAdmit"));
            Assert.That(schedulePass, Does.Contain("SimpleDdgiSchedule.Materialize"));
            Assert.That(schedulePass, Does.Contain(
                "GroupsFor(layout.RequestCapacity)"));
            Assert.That(schedulePass, Does.Contain("GpuTimestampRecorder? timestamps"));
            Assert.That(schedulePass, Does.Not.Contain(
                "DispatchStage(cmd, pushConstants, 5, SimpleDdgiGpuSchedulerLayout.GroupsFor(layout.LaneCapacity))"));
            Assert.That(reset, Does.Not.Contain(
                "candidateCapacity * SIMPLE_DDGI_SCHEDULER_CANDIDATE_WORDS"));
            Assert.That(reset, Does.Not.Contain(
                "requestCapacity * SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS"));
            Assert.That(feedback, Does.Contain("base + 64u + lane"));
            Assert.That(feedback, Does.Contain(
                "pc.LaneTotalsOffsetWords + activeIndex"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_FEEDBACK_ACTIVE_SOURCE_MUTATION"));
            Assert.That(feedback, Does.Contain(
                "lastCommittedSourceRefreshFrame == SchedulerFrameIndex()"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEEDBACK_ACTIVE_CANONICAL_MUTATION_OFFSET"));
            Assert.That(materialize, Does.Contain("SchedulerReadUpdateSelection("));
            Assert.That(materialize, Does.Contain("SchedulerWriteUpdate("));
            Assert.That(materialize, Does.Contain(
                "updateIndex >= accepted"));
            Assert.That(shaderProject, Does.Contain(
                "SimpleDdgiAdmissionShaderVariant"));
            Assert.That(shaderProject, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_ADMISSION_ROLE"));
        });
    }

    [Test]
    public void Layout_RejectsStorageRangeOverflow()
    {
        Assert.That(
            () => SimpleDdgiGpuSchedulerLayout.Create(1024, 256, 2, maxStorageBufferRange: 1024),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void FallbackStateExport_ChargesCompletePrivateAndPublicState()
    {
        const int probes = 15_368;
        ulong privateBytes = checked(
            (ulong)probes *
            (ulong)Marshal.SizeOf<GPUSimpleDdgiSchedulerProbeState>());
        ulong publicOffset = SimpleDdgiGpuSchedulerLayout.Align(
            privateBytes,
            SimpleDdgiGpuSchedulerLayout.ArenaAlignmentBytes);
        ulong publicBytes = checked(
            (ulong)probes *
            (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>());

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiGpuScheduler.ResolveFallbackStateExportBytes(probes),
                Is.EqualTo(publicOffset + publicBytes));
            Assert.That(
                SimpleDdgiGpuScheduler.ResolveFallbackStateExportBytes(0),
                Is.Zero);
        });
    }

    [Test]
    public void Layout_GroupMathAndAbiSizesRemainPinned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(0), Is.EqualTo(0));
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(1), Is.EqualTo(1));
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(64), Is.EqualTo(1));
            Assert.That(SimpleDdgiGpuSchedulerLayout.GroupsFor(65), Is.EqualTo(2));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerFrame>(), Is.EqualTo(224));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerVolumePolicy>(), Is.EqualTo(176));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerCandidate>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiUpdateOutcome>(), Is.EqualTo(60));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerProbeState>(), Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiProbeUpdate>(), Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiParams>(), Is.EqualTo(256));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiPushConstants>(), Is.EqualTo(136));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiTransportAuditPushConstants>(), Is.EqualTo(128));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>(), Is.EqualTo(160));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulePushConstants>(), Is.EqualTo(124));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiPublishPushConstants>(), Is.EqualTo(56));
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiSchedulerFeedback>(), Is.EqualTo(256));
            Assert.That(
                Marshal.OffsetOf<GPUSimpleDdgiSchedulerFrame>(
                    nameof(GPUSimpleDdgiSchedulerFrame.PeriodicSourceRefreshControlFrame))
                    .ToInt32(),
                Is.EqualTo(44));
        });
    }

    [Test]
    public void GpuStructWordOffsetsMatchShaderAbiStrides()
    {
        Assert.Multiple(() =>
        {
            AssertWordOffsets<GPUSimpleDdgiProbeUpdate>(
                nameof(GPUSimpleDdgiProbeUpdate.ProbeIndex),
                nameof(GPUSimpleDdgiProbeUpdate.VolumeIndex),
                nameof(GPUSimpleDdgiProbeUpdate.Flags),
                nameof(GPUSimpleDdgiProbeUpdate.Reserved0),
                nameof(GPUSimpleDdgiProbeUpdate.SourceRayCount),
                nameof(GPUSimpleDdgiProbeUpdate.SourceLightingGeneration),
                nameof(GPUSimpleDdgiProbeUpdate.OutcomeIndex),
                nameof(GPUSimpleDdgiProbeUpdate.SourceEpoch),
                nameof(GPUSimpleDdgiProbeUpdate.PhysicalProbeIndex),
                nameof(GPUSimpleDdgiProbeUpdate.PageMappingGeneration),
                nameof(GPUSimpleDdgiProbeUpdate.ResidencyResourceGeneration),
                nameof(GPUSimpleDdgiProbeUpdate.CacheProbeBaseWordPlusOne));
            AssertWordOffsets<GPUSimpleDdgiSchedulerProbeState>(
                nameof(GPUSimpleDdgiSchedulerProbeState.LastCommittedUpdateFrame),
                nameof(GPUSimpleDdgiSchedulerProbeState.LastCommittedSourceRefreshFrame),
                nameof(GPUSimpleDdgiSchedulerProbeState.CommittedSourceLightingGeneration),
                nameof(GPUSimpleDdgiSchedulerProbeState.SourceEpoch),
                nameof(GPUSimpleDdgiSchedulerProbeState.OwningVolumeTableGeneration),
                nameof(GPUSimpleDdgiSchedulerProbeState.DirtyReasonFlags),
                nameof(GPUSimpleDdgiSchedulerProbeState.DirtyStartFrame),
                nameof(GPUSimpleDdgiSchedulerProbeState.PackedTransportAndLifecycle),
                nameof(GPUSimpleDdgiSchedulerProbeState.AppliedInvalidationMarker),
                nameof(GPUSimpleDdgiSchedulerProbeState.Reserved0),
                nameof(GPUSimpleDdgiSchedulerProbeState.CacheProbeBaseWordPlusOne),
                nameof(GPUSimpleDdgiSchedulerProbeState.PackedPropagationState));
            AssertWordOffsets<GPUSimpleDdgiUpdateOutcome>(
                nameof(GPUSimpleDdgiUpdateOutcome.QueueTransactionGeneration),
                nameof(GPUSimpleDdgiUpdateOutcome.SchedulerResourceGeneration),
                nameof(GPUSimpleDdgiUpdateOutcome.VolumeTableGeneration),
                nameof(GPUSimpleDdgiUpdateOutcome.SourceLightingGeneration),
                nameof(GPUSimpleDdgiUpdateOutcome.TransportGeneration),
                nameof(GPUSimpleDdgiUpdateOutcome.ProbeIndex),
                nameof(GPUSimpleDdgiUpdateOutcome.ExpectedPhysicalGeneration),
                nameof(GPUSimpleDdgiUpdateOutcome.RequiredCompletionMask),
                nameof(GPUSimpleDdgiUpdateOutcome.CompletionMask),
                nameof(GPUSimpleDdgiUpdateOutcome.FailureReason),
                nameof(GPUSimpleDdgiUpdateOutcome.UpdateFlags),
                nameof(GPUSimpleDdgiUpdateOutcome.ExpectedRayInvocationCount),
                nameof(GPUSimpleDdgiUpdateOutcome.TraceInvocationCount),
                nameof(GPUSimpleDdgiUpdateOutcome.TransportInvocationCount),
                nameof(GPUSimpleDdgiUpdateOutcome.ResidualBits));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditSummaryBufferIndex)).ToInt32(),
                Is.EqualTo(36));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditSchedulerFrameOffsetWords)).ToInt32(),
                Is.EqualTo(64));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditSchedulerResourceGeneration)).ToInt32(),
                Is.EqualTo(104));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditSchedulerProbeStateOffsetWords)).ToInt32(),
                Is.EqualTo(108));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditSolveEpoch)).ToInt32(),
                Is.EqualTo(112));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditWorkspaceBaseWord)).ToInt32(),
                Is.EqualTo(116));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditWitnessProbeIndex)).ToInt32(),
                Is.EqualTo(120));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiTransportAuditPushConstants>(
                nameof(GPUSimpleDdgiTransportAuditPushConstants.AuditWitnessTexelIndex)).ToInt32(),
                Is.EqualTo(124));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiSchedulerFrame>(
                nameof(GPUSimpleDdgiSchedulerFrame.CameraPositionAndNearProximity)).ToInt32(),
                Is.EqualTo(80));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiSchedulerFrame>(
                nameof(GPUSimpleDdgiSchedulerFrame.CostVisibilityPerPrimaryQ8)).ToInt32(),
                Is.EqualTo(160));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiSchedulerFrame>(
                nameof(GPUSimpleDdgiSchedulerFrame.CostFarFieldStepsPerPrimaryQ8)).ToInt32(),
                Is.EqualTo(172));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiSchedulerFrame>(
                nameof(GPUSimpleDdgiSchedulerFrame.ExactFeedbackSummaryBufferIndex)).ToInt32(),
                Is.EqualTo(176));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiSchedulerFrame>(
                nameof(GPUSimpleDdgiSchedulerFrame.ExactFeedbackExpectedGeneration)).ToInt32(),
                Is.EqualTo(200));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiSchedulerFrame>(
                nameof(GPUSimpleDdgiSchedulerFrame.ExactFeedbackFlags)).ToInt32(),
                Is.EqualTo(216));
            Assert.That(Marshal.OffsetOf<GPUSimpleDdgiSchedulerVolumePolicy>(
                nameof(GPUSimpleDdgiSchedulerVolumePolicy.ProximityRadiusPadding)).ToInt32(),
                Is.EqualTo(160));
        });

        string scheduleShared = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_shared.glsl");
        string schedulerMetadataAbi = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_scheduler_metadata_abi.glsl");
        string pageResidencyShared = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_page_residency_shared.glsl");
        string pageFeedback = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_page_feedback.comp");
        string transportAudit = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_transport_audit.comp");
        string producerShared = ReadRepoText("Njulf.Shaders", "ddgi_simple_shared.glsl");
        Assert.Multiple(() =>
        {
            Assert.That(schedulerMetadataAbi, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS = 12u"));
            Assert.That(scheduleShared, Does.Contain(
                "#include \"ddgi_simple_scheduler_metadata_abi.glsl\""));
            Assert.That(pageResidencyShared, Does.Contain(
                "virtualProbeIndex * SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS"));
            Assert.That(pageResidencyShared, Does.Contain(
                "vec4(0.0, 0.0, 0.0, 1.0)"));
            Assert.That(pageFeedback, Does.Contain(
                "virtualProbe * SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS"));
            Assert.That(transportAudit, Does.Contain(
                "probeIndex * SIMPLE_DDGI_SCHEDULER_PROBE_STATE_WORDS"));
            Assert.That(pageResidencyShared, Does.Not.Contain(
                "virtualProbeIndex * 10u"));
            Assert.That(pageFeedback, Does.Not.Contain("virtualProbe * 10u"));
            Assert.That(transportAudit, Does.Not.Contain(
                "SIMPLE_DDGI_AUDIT_PROBE_STATE_WORDS"));
            Assert.That(scheduleShared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_UPDATE_WORDS = 10u"));
            Assert.That(scheduleShared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS = 15u"));
            Assert.That(producerShared, Does.Contain(
                "SIMPLE_DDGI_PROBE_UPDATE_STRIDE_WORDS = 12u"));
            Assert.That(producerShared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_UPDATE_RECORD_WORDS = 10u"));
            Assert.That(producerShared, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_OUTCOME_WORDS = 15u"));
        });
    }

    [Test]
    public void IndirectCommands_UseExactPerPassFormulasAndVulkanDimensions()
    {
        uint[] acceptedCounts = [0u, 1u, 63u, 64u, 65u, 256u];
        uint[] bucketRays = [1u, 32u, 64u, 65u, 128u, 256u];
        foreach (uint accepted in acceptedCounts)
        {
            GPUSimpleDdgiDispatchIndirectCommand trace =
                SimpleDdgiIndirectDispatchMath.BuildRayBucketCommand(accepted, 64u);
            GPUSimpleDdgiDispatchIndirectCommand transport =
                SimpleDdgiIndirectDispatchMath.BuildRayBucketCommand(accepted, 64u);
            GPUSimpleDdgiDispatchIndirectCommand relocate =
                SimpleDdgiIndirectDispatchMath.BuildRequestCommand(accepted);
            GPUSimpleDdgiDispatchIndirectCommand commitLocal =
                SimpleDdgiIndirectDispatchMath.BuildRequestCommand(accepted);
            GPUSimpleDdgiDispatchIndirectCommand commitPropagation =
                SimpleDdgiIndirectDispatchMath.BuildRequestCommand(accepted);
            GPUSimpleDdgiDispatchIndirectCommand blend =
                SimpleDdgiIndirectDispatchMath.BuildProbeCommand(accepted);
            GPUSimpleDdgiDispatchIndirectCommand publish =
                SimpleDdgiIndirectDispatchMath.BuildProbeCommand(accepted);
            GPUSimpleDdgiDispatchIndirectCommand sampledPublish =
                SimpleDdgiIndirectDispatchMath.BuildProbeCommand(accepted);
            GPUSimpleDdgiDispatchIndirectCommand feedback =
                SimpleDdgiIndirectDispatchMath.BuildFeedbackCommand();

            Assert.Multiple(() =>
            {
                Assert.That(trace.GroupCountX, Is.EqualTo(accepted));
                Assert.That(transport.GroupCountX, Is.EqualTo(accepted));
                Assert.That(relocate.GroupCountX,
                    Is.EqualTo(SimpleDdgiIndirectDispatchMath.RequestThreadGroupCount(accepted)));
                Assert.That(commitLocal.GroupCountX,
                    Is.EqualTo(SimpleDdgiIndirectDispatchMath.RequestThreadGroupCount(accepted)));
                Assert.That(commitPropagation.GroupCountX,
                    Is.EqualTo(SimpleDdgiIndirectDispatchMath.RequestThreadGroupCount(accepted)));
                Assert.That(blend.GroupCountX, Is.EqualTo(accepted));
                Assert.That(publish.GroupCountX, Is.EqualTo(accepted));
                Assert.That(sampledPublish.GroupCountX, Is.EqualTo(accepted));
                Assert.That(feedback.GroupCountX, Is.EqualTo(1u));
            });

            GPUSimpleDdgiDispatchIndirectCommand[] commands =
                [trace, transport, relocate, commitLocal, commitPropagation,
                 blend, publish, sampledPublish, feedback];
            foreach (GPUSimpleDdgiDispatchIndirectCommand command in commands)
            {
                Assert.That(command.GroupCountY, Is.EqualTo(1u));
                Assert.That(command.GroupCountZ, Is.EqualTo(1u));
                Assert.That(command.Reserved, Is.EqualTo(0u));
            }
        }

        foreach (uint rays in bucketRays)
        {
            GPUSimpleDdgiDispatchIndirectCommand command =
                SimpleDdgiIndirectDispatchMath.BuildRayBucketCommand(65u, rays);
            Assert.That(command.GroupCountX,
                Is.EqualTo(SimpleDdgiIndirectDispatchMath.RayGroupCount(65u, rays)));
            Assert.That(command.GroupCountY, Is.EqualTo(1u));
            Assert.That(command.GroupCountZ, Is.EqualTo(1u));
        }
    }

    [Test]
    public void SixRayBucketCommands_DecodeAsVulkanDispatchCommands()
    {
        uint[] probeCounts = [0u, 1u, 63u, 64u, 65u, 256u];
        uint[] raysPerProbe = [1u, 32u, 64u, 65u, 128u, 256u];
        uint[] words = new uint[SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount * 4];

        for (int bucket = 0; bucket < SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount; bucket++)
        {
            GPUSimpleDdgiDispatchIndirectCommand command =
                SimpleDdgiIndirectDispatchMath.BuildRayBucketCommand(
                    probeCounts[bucket], raysPerProbe[bucket]);
            MemoryMarshal.Write(
                MemoryMarshal.AsBytes(words.AsSpan(bucket * 4, 4)),
                in command);
        }

        GPUSimpleDdgiDispatchIndirectCommand[] decoded =
            MemoryMarshal.Cast<uint, GPUSimpleDdgiDispatchIndirectCommand>(words).ToArray();
        Assert.That(decoded.Length, Is.EqualTo(SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount));
        for (int bucket = 0; bucket < decoded.Length; bucket++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(decoded[bucket].GroupCountX,
                    Is.EqualTo(SimpleDdgiIndirectDispatchMath.RayGroupCount(
                        probeCounts[bucket], raysPerProbe[bucket])));
                Assert.That(decoded[bucket].GroupCountY, Is.EqualTo(1u));
                Assert.That(decoded[bucket].GroupCountZ, Is.EqualTo(1u));
                Assert.That(decoded[bucket].Reserved, Is.EqualTo(0u));
            });
        }
    }

    [Test]
    public void ZeroWorkCommands_HaveZeroXAndUnitYAndZ()
    {
        GPUSimpleDdgiDispatchIndirectCommand[] commands =
        [
            SimpleDdgiIndirectDispatchMath.BuildRayBucketCommand(0u, 64u),
            SimpleDdgiIndirectDispatchMath.BuildRequestCommand(0u),
            SimpleDdgiIndirectDispatchMath.BuildProbeCommand(0u)
        ];
        Assert.Multiple(() =>
        {
            foreach (GPUSimpleDdgiDispatchIndirectCommand command in commands)
            {
                Assert.That(command.GroupCountX, Is.EqualTo(0u));
                Assert.That(command.GroupCountY, Is.EqualTo(1u));
                Assert.That(command.GroupCountZ, Is.EqualTo(1u));
            }
        });
    }

    [Test]
    public void MaximumArenaFitsTheSparseCapableShippingSchedulerBudget()
    {
        SimpleDdgiGpuSchedulerLayout layout = SimpleDdgiGpuSchedulerLayout.Create(
            GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount,
            GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount,
            GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount);

        Assert.That(layout.TotalBytes,
            Is.LessThanOrEqualTo(SimpleDdgiGpuSchedulerLayout.ShippingArenaBudgetBytes));
        Assert.That(layout.CandidateInput.ElementStride, Is.EqualTo(16));
        Assert.That(layout.ReceiverContribution.ElementStride, Is.EqualTo(16));
        Assert.That(layout.CandidateOutput.ElementStride, Is.EqualTo(sizeof(uint)));
        Assert.That(layout.FeedbackSummary.ElementCount,
            Is.GreaterThanOrEqualTo(64u + (uint)SimpleDdgiSchedulerAbi.MaxLaneCount));
    }

    [Test]
    public void ReceiverContributionFeedback_IsDoubleBufferedSampledAndPriorityOnly()
    {
        string receiver = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_shared.glsl");
        string classify = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_classify.comp");
        string feedback = ReadRepoText(
            "Njulf.Shaders", "ddgi_simple_schedule_feedback.comp");
        string graph = ReadRepoText(
            "Njulf.Rendering", "Pipeline",
            "ProductionRenderPipelineDeclaration.cs");

        Assert.Multiple(() =>
        {
            Assert.That(receiver, Does.Contain(
                "void SimpleDdgiRecordReceiverContribution("));
            Assert.That(receiver, Does.Contain("bank = p.frameIndex & 1u"));
            Assert.That(receiver, Does.Contain(
                "virtualProbeIndex * SIMPLE_DDGI_RECEIVER_CONTRIBUTION_WORDS"));
            Assert.That(receiver, Does.Contain("selectedDirectionalWeight);"));
            Assert.That(classify, Does.Contain(
                "SchedulerReadPreviousReceiverContribution("));
            Assert.That(classify, Does.Contain(
                "priorityReceiverContribution"));
            Assert.That(classify, Does.Contain(
                "stateAge >= maximumLatency"));
            Assert.That(feedback, Does.Contain(
                "((SchedulerFrameIndex() - 1u) & 1u)"));
            Assert.That(feedback, Does.Contain(
                "SchedulerArenaWrite(receiverRecordBase + 0u, 0u);"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_FEEDBACK_RECEIVER_CONTRIBUTION_OFFSET"));
            Assert.That(graph, Does.Contain(
                "ReadWriteGraphicsAndComputeStorage(RenderGraphResourceId.SimpleDdgiScheduler)"));
        });
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate repository file.",
            Path.Combine(pathParts));
    }

    private static void AssertWordOffsets<T>(params string[] fieldNames)
        where T : struct
    {
        for (int i = 0; i < fieldNames.Length; i++)
        {
            Assert.That(
                Marshal.OffsetOf<T>(fieldNames[i]).ToInt32(),
                Is.EqualTo(i * sizeof(uint)),
                $"{typeof(T).Name}.{fieldNames[i]}");
        }
    }
}
