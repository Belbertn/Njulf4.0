using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Records one bounded cache-only DDGI transaction immediately before forward
/// shading. With complete timing and contraction evidence it may execute up to
/// four private cached sweeps. It never dispatches a ray-query shader, never
/// consumes the normal frame budget, and leaves source-cache ownership pending
/// for the ordinary post-forward transaction.
/// </summary>
public sealed unsafe class SimpleDdgiUrgentRelightPass : RenderPassBase
{
    private const uint UrgentControlCounterWord = 95u;

    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private readonly SimpleDdgiSchedulePass _schedulePass;
    private readonly SimpleDdgiTracePass _tracePass;
    private readonly SimpleDdgiRelocateClassifyPass _relocateClassifyPass;
    private readonly SimpleDdgiDirectionalRadiancePass _directionalRadiancePass;
    private readonly SimpleDdgiAcceleratedSolvePass _acceleratedSolvePass;
    private readonly SimpleDdgiTransportPass _transportPass;
    private readonly SimpleDdgiBlendPass _blendPass;
    private readonly SimpleDdgiPublishPass _publishPass;
    private readonly SimpleDdgiSchedulerCommitPass _commitPass;

    public SimpleDdgiUrgentRelightPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager,
        SimpleDdgiSchedulePass schedulePass,
        SimpleDdgiTracePass tracePass,
        SimpleDdgiRelocateClassifyPass relocateClassifyPass,
        SimpleDdgiDirectionalRadiancePass directionalRadiancePass,
        SimpleDdgiAcceleratedSolvePass acceleratedSolvePass,
        SimpleDdgiTransportPass transportPass,
        SimpleDdgiBlendPass blendPass,
        SimpleDdgiPublishPass publishPass,
        SimpleDdgiSchedulerCommitPass commitPass)
        : base("SimpleDdgiUrgentRelightPass", context, swapchain, bindlessHeap)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _volumeManager = volumeManager ??
            throw new ArgumentNullException(nameof(volumeManager));
        _schedulePass = schedulePass ??
            throw new ArgumentNullException(nameof(schedulePass));
        _tracePass = tracePass ?? throw new ArgumentNullException(nameof(tracePass));
        _relocateClassifyPass = relocateClassifyPass ??
            throw new ArgumentNullException(nameof(relocateClassifyPass));
        _directionalRadiancePass = directionalRadiancePass ??
            throw new ArgumentNullException(nameof(directionalRadiancePass));
        _acceleratedSolvePass = acceleratedSolvePass ??
            throw new ArgumentNullException(nameof(acceleratedSolvePass));
        _transportPass = transportPass ??
            throw new ArgumentNullException(nameof(transportPass));
        _blendPass = blendPass ?? throw new ArgumentNullException(nameof(blendPass));
        _publishPass = publishPass ??
            throw new ArgumentNullException(nameof(publishPass));
        _commitPass = commitPass ?? throw new ArgumentNullException(nameof(commitPass));
    }

    // This orchestrator reuses initialized pass pipelines and records a transfer
    // control write around them. Keep it on the primary graphics command buffer
    // so forward shading naturally follows the bounded publication barrier.
    public override bool SupportsSecondaryCommandBuffer => false;
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute => false;
    public override string AsyncComputeReason =>
        "Urgent DDGI publication must complete before the current forward draw.";

    public override void Initialize()
    {
        // Child passes own every pipeline and descriptor allocation.
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        SimpleDdgiSourceRefreshMode refreshMode = _volumeManager.SourceRefreshMode;
        bool radiometricRelight = refreshMode is
            SimpleDdgiSourceRefreshMode.EnvironmentMissRelight or
            SimpleDdgiSourceRefreshMode.CachedHitRelight;
        return gi.SimpleDdgiUrgentRelightEnabled &&
            SimpleDdgiUrgentRelightPolicy.ResolveBudget(
                gi.SimpleDdgiUrgentRelightProbeBudget) != 0u &&
            _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
            _volumeManager.TransportV2Active &&
            !_volumeManager.TransportTailAuditPending &&
            gi.EffectiveUseDdgi &&
            gi.SimpleDdgiStructuredGatherEnabled &&
            gi.EffectiveUseRayQueryBackend &&
            _volumeManager.GpuSchedulerFrameExecutionAvailable &&
            _volumeManager.ProbeCount > 0 &&
            _volumeManager.GpuScheduler.IsReady &&
            !_volumeManager.RadiometricRelightPublicationPending &&
            _volumeManager.DirtyReasonFlags != 0u &&
            radiometricRelight;
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        SimpleDdgiGpuSchedulerLayout layout =
            _volumeManager.GpuScheduler.Layout ??
            throw new InvalidOperationException(
                "Simple DDGI scheduler layout is not resident.");
        uint budget = SimpleDdgiUrgentRelightPolicy.ResolveBudget(
            _settings.GlobalIllumination.SimpleDdgiUrgentRelightProbeBudget);
        if (budget == 0u)
            return;

        int sweepCount = ResolvePrivateSweepCount(sceneData);
        uint compactScratchBaseWord = layout.CandidateInput.OffsetWords;
        ulong compactScratchBytes = checked(
            layout.UpdateRecords.Offset - layout.CandidateInput.Offset);
        const ulong compactIrradianceBytesPerProbe =
            SimpleDdgiVolumeManager.IrradianceTexelsPerProbe *
            SimpleDdgiVolumeManager.IrradianceTexelsPerProbe * 2UL *
            sizeof(uint);
        uint compactScratchProbeCapacity = checked((uint)Math.Min(
            compactScratchBytes / compactIrradianceBytesPerProbe,
            uint.MaxValue));
        if (sweepCount > 1)
        {
            budget = Math.Min(budget, compactScratchProbeCapacity);
            if (budget == 0u)
                sweepCount = 1;
        }

        Silk.NET.Vulkan.Buffer arena =
            _volumeManager.GpuScheduler.GetArenaVkBuffer();
        ulong controlOffset = checked(
            layout.Counters.Offset +
            UrgentControlCounterWord * (ulong)sizeof(uint));
        _context.Api.CmdFillBuffer(
            cmd,
            arena,
            controlOffset,
            sizeof(uint),
            budget);
        InsertArenaBarrier(
            cmd,
            arena,
            controlOffset,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit);

        try
        {
            _schedulePass.Execute(cmd, frameIndex, sceneData);
            _tracePass.ExecuteCacheReuseOnly(cmd, sceneData);
            _relocateClassifyPass.Execute(cmd, frameIndex, sceneData);
            bool privateMultiSweepExecuted = sweepCount > 1 &&
                _acceleratedSolvePass.TryExecuteUrgentPrivateSolve(
                    cmd,
                    sceneData,
                    sweepCount,
                    compactScratchBaseWord,
                    budget);
            if (!privateMultiSweepExecuted)
            {
                _transportPass.Execute(cmd, frameIndex, sceneData);
                _blendPass.Execute(cmd, frameIndex, sceneData);
            }
            _directionalRadiancePass.Execute(cmd, frameIndex, sceneData);

            // Canonical publish is a producer-completion gate in resident mode.
            // CommitLocal performs the only public SSBO copy. The optional image
            // mirror follows commit and reads that canonical result.
            _publishPass.ExecuteCanonicalOnly(cmd);
            _commitPass.ExecuteResidentLocalOnly(cmd);
            _publishPass.ExecuteSampledOnly(cmd);
        }
        finally
        {
            InsertArenaBarrier(
                cmd,
                arena,
                controlOffset,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit);
            _context.Api.CmdFillBuffer(
                cmd,
                arena,
                controlOffset,
                sizeof(uint),
                0u);
            InsertArenaBarrier(
                cmd,
                arena,
                controlOffset,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit |
                    PipelineStageFlags2.DrawIndirectBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit |
                    AccessFlags2.IndirectCommandReadBit);
        }
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        // Child passes retain ownership and are cleaned up by the render graph.
    }

    private int ResolvePrivateSweepCount(SceneRenderingData sceneData)
    {
        SimpleDdgiTransportTailSummary summary =
            _volumeManager.TransportTailSummary;
        if (!summary.IsComplete || !summary.HasFiniteEvidence)
            return 1;

        long previousFrameGpuMicroseconds =
            RendererDiagnosticsAssembler.CalculateGpuFrameMicroseconds(
                sceneData);
        long targetFrameGpuMicroseconds = checked((long)Math.Round(
            _settings.DynamicResolution.TargetFrameMilliseconds * 1000.0f));
        long estimatedAdditionalSweepMicroseconds =
            sceneData.GpuSimpleDdgiUrgentRelightMicroseconds > 0L
                ? sceneData.GpuSimpleDdgiUrgentRelightMicroseconds
                : checked(
                    sceneData.GpuSimpleDdgiTransportMicroseconds +
                    sceneData.GpuSimpleDdgiBlendMicroseconds);

        return SimpleDdgiUrgentRelightPolicy.ResolveSweepCount(
            _volumeManager.TransportAcceleratedSweepCount,
            summary.FixedPointDefect,
            Math.Max(summary.Tolerance, 0.0001f),
            summary.CertifiedContractionBound,
            previousFrameGpuMicroseconds,
            targetFrameGpuMicroseconds,
            estimatedAdditionalSweepMicroseconds);
    }

    private void InsertArenaBarrier(
        CommandBuffer cmd,
        Silk.NET.Vulkan.Buffer arena,
        ulong offset,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
    {
        BufferMemoryBarrier2 barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = arena,
            Offset = offset,
            Size = sizeof(uint)
        };
        DependencyInfo dependency = new()
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }
}
