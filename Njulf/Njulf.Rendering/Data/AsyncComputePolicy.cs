using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Controls how the renderer is allowed to place work on the asynchronous compute queue.
    /// <see cref="ForceEnabledForValidation"/> still performs every capability and resource-plan
    /// validation; it only bypasses the profitability decision.
    /// </summary>
    public enum AsyncComputeMode
    {
        Disabled = 0,
        Auto = 1,
        ForceEnabledForValidation = 2
    }

    /// <summary>
    /// A user-addressable asynchronous-compute feature.  A path can contain more than one pass,
    /// but it is always enabled or disabled as one scheduling unit.
    /// </summary>
    public enum AsyncComputePath
    {
        SimpleDdgiUpdate = 0,
        FullDdgiUpdate = 1,
        FarFieldClipmapBake = 2,
        AmbientOcclusionBlur = 3,
        HiZBuild = 4,
        SsgiChain = 5,
        Fog = 6,
        Bloom = 7,
        GpuParticles = 8
    }

    /// <summary>
    /// Explains the effective state of an async path for the current frame.  These values are
    /// intentionally separate from the requested mode so telemetry cannot confuse a candidate
    /// with work that was actually submitted to a compute queue.
    /// </summary>
    public enum AsyncComputePathStatus
    {
        DisabledByPolicy = 0,
        DisabledByFeature = 1,
        UnsupportedQueue = 2,
        MissingResourcePlan = 3,
        PendingWarmup = 4,
        NoMeasuredBenefit = 5,
        Enabled = 6,
        ValidationFallback = 7
    }

    /// <summary>
    /// Snapshot-safe explanation of one async path. This intentionally contains no raw Vulkan
    /// handles, so normal performance telemetry can export it without exposing driver objects.
    /// </summary>
    public sealed record AsyncComputePathDiagnostic(
        AsyncComputePath Path,
        bool Requested,
        bool Supported,
        bool Eligible,
        bool Active,
        AsyncComputePathStatus Status,
        string Reason,
        IReadOnlyList<string> Passes);

    /// <summary>One planned submission segment and its timeline-edge summary.</summary>
    public sealed record AsyncComputeSegmentDiagnostic(
        int SegmentId,
        string Queue,
        IReadOnlyList<string> Passes,
        IReadOnlyList<ulong> TimelineWaitValues,
        ulong? TimelineSignalValue,
        int AcquireBarrierCount,
        int ReleaseBarrierCount,
        bool AccessesSwapchain,
        bool IsTerminalGraphicsSegment);

    public static class AsyncComputePathExtensions
    {
        public static bool IsEnabledBy(this AsyncComputeSettings settings, AsyncComputePath path)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            return path switch
            {
                AsyncComputePath.SimpleDdgiUpdate => settings.SimpleDdgiUpdateEnabled,
                AsyncComputePath.FullDdgiUpdate => settings.FullDdgiUpdateEnabled,
                AsyncComputePath.FarFieldClipmapBake => settings.FarFieldClipmapBakeEnabled,
                AsyncComputePath.AmbientOcclusionBlur => settings.AmbientOcclusionBlurEnabled,
                AsyncComputePath.HiZBuild => settings.HiZBuildEnabled,
                AsyncComputePath.SsgiChain => settings.SsgiChainEnabled,
                AsyncComputePath.Fog => settings.FogEnabled,
                AsyncComputePath.Bloom => settings.BloomEnabled,
                AsyncComputePath.GpuParticles => settings.GpuParticlesEnabled,
                _ => false
            };
        }
    }
}
