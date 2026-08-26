using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

internal sealed record AsyncComputeFramePlan(
    AsyncComputeMode RequestedMode,
    AsyncComputeMode EffectiveMode,
    bool Supported,
    AsyncComputeSubmissionPlan SubmissionPlan,
    RenderGraphDiagnostics GraphDiagnostics,
    string Status)
{
    internal bool Requested => RequestedMode != AsyncComputeMode.Disabled;
    internal bool Enabled => SubmissionPlan.ContainsAsyncCompute;
    internal IReadOnlyList<string> CandidatePasses => SubmissionPlan.Paths
        .Where(path => path.Requested &&
                       path.Supported &&
                       path.Status is not AsyncComputePathStatus.MissingResourcePlan and
                           not AsyncComputePathStatus.ValidationFallback)
        .SelectMany(path => path.Passes)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    internal IReadOnlyList<string> EnabledPasses => SubmissionPlan.ActivePasses;
    internal int QueueOwnershipTransitionCount =>
        SubmissionPlan.QueueFamilyOwnershipTransferCount;

    internal bool IsPathActive(AsyncComputePath path) =>
        SubmissionPlan.Accepted &&
        SubmissionPlan.ContainsAsyncCompute &&
        SubmissionPlan.Paths.Any(candidate =>
            candidate.Active && candidate.Path == path);
}

internal readonly record struct AsyncComputeFrameBoundaryInput(
    ulong FrameSerial,
    int ValidationErrorCount);

internal readonly record struct AsyncComputePlanningInput(
    RenderSettings Settings,
    SceneRenderingData SceneData,
    int FrameIndex,
    uint TimingFrameNumber,
    bool IndependentQueueAvailable,
    bool TimelineSemaphoreAvailable,
    bool DedicatedQueueFamilyAvailable,
    uint GraphicsQueueFamily,
    uint ComputeQueueFamily,
    QueueFlags GraphicsQueueFlags,
    QueueFlags ComputeQueueFlags,
    string DeviceName,
    string DriverVersion,
    bool FarFieldBakePending,
    int BloomMipCount,
    string GraphicsOnlyConstraintReason = "");

internal readonly record struct AsyncComputeTimingCaptureInput(
    RenderSettings Settings,
    SceneRenderingData SceneData,
    string DeviceName,
    string DriverVersion);

internal readonly record struct AsyncComputeRecordingDecision(
    AsyncComputeFramePlan Plan,
    bool RecordAsync,
    string FallbackReason);

internal readonly record struct AsyncComputeRecordingSummary(
    int EmittedReleaseBarrierCount,
    int EmittedAcquireBarrierCount,
    int OwnershipTransferCount,
    long BarrierRecordMicroseconds);

internal readonly record struct AsyncComputeRecordingPublication(
    bool Succeeded,
    string FailureReason,
    int OwnershipTransferCount,
    string GraphBarrierSummary);

internal readonly record struct AsyncComputeSubmissionPatch(
    int SubmittedGraphicsSegmentCount,
    int SubmittedComputeSegmentCount);

internal enum AsyncComputeTimingResetKind : byte
{
    SceneOrMode,
    RenderTargetsOrSwapchain
}

internal readonly record struct AsyncComputeDiagnosticsContext(
    bool IndependentQueueAvailable,
    bool DedicatedQueueFamilyAvailable,
    uint GraphicsQueueFamily,
    uint ComputeQueueFamily);

internal sealed record AsyncComputeDiagnosticsSnapshot(
    AsyncComputeFramePlan Plan,
    bool IndependentQueueAvailable,
    bool DedicatedQueueFamilyAvailable,
    uint GraphicsQueueFamily,
    uint ComputeQueueFamily,
    int PlannedGraphicsSegments,
    int PlannedComputeSegments,
    int SubmittedGraphicsSegments,
    int SubmittedComputeSegments,
    int PlannedReleaseBarriers,
    int PlannedAcquireBarriers,
    int EmittedReleaseBarriers,
    int EmittedAcquireBarriers,
    int OwnershipTransfers,
    long BarrierRecordMicroseconds,
    ulong TransferredBytes,
    int TransferredImageSubresources,
    int ValidationFallbackCount,
    string LastFallbackReason,
    ulong ResourcePlanGeneration,
    int StalePlanRejectionCount,
    long QueueBusyMicroseconds,
    long EstimatedOverlapMicroseconds,
    long FirstConsumerWaitEstimateMicroseconds,
    IReadOnlyList<AsyncComputePathDiagnostic> Paths,
    IReadOnlyList<AsyncComputeSegmentDiagnostic> Segments)
{
    internal bool Requested => Plan.Requested;
    internal bool Enabled => Plan.Enabled;
    internal bool Supported => Plan.Supported;
    internal string Status => Plan.Status;
    internal AsyncComputeMode RequestedMode => Plan.RequestedMode;
    internal AsyncComputeMode EffectiveMode => Plan.EffectiveMode;
    internal IReadOnlyList<string> CandidatePasses => Plan.CandidatePasses;
    internal IReadOnlyList<string> EnabledPasses => Plan.EnabledPasses;
    internal int QueueOwnershipTransitionCount =>
        Plan.QueueOwnershipTransitionCount;
    internal RenderGraphDiagnostics GraphDiagnostics => Plan.GraphDiagnostics;
    internal bool DdgiActuallyEnabled =>
        Plan.IsPathActive(AsyncComputePath.SimpleDdgiUpdate);
}
