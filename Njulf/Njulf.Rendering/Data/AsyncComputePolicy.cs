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
        FarFieldClipmapBake = 1,
        AmbientOcclusionBlur = 2,
        HiZBuild = 3,
        Fog = 4,
        Bloom = 5,
        GpuParticles = 6
    }

    [Flags]
    public enum AsyncComputePreferredPathMask : uint
    {
        None = 0,
        SimpleDdgiUpdate = 1u << (int)AsyncComputePath.SimpleDdgiUpdate,
        FarFieldClipmapBake = 1u << (int)AsyncComputePath.FarFieldClipmapBake,
        AmbientOcclusionBlur = 1u << (int)AsyncComputePath.AmbientOcclusionBlur,
        HiZBuild = 1u << (int)AsyncComputePath.HiZBuild,
        Fog = 1u << (int)AsyncComputePath.Fog,
        Bloom = 1u << (int)AsyncComputePath.Bloom,
        GpuParticles = 1u << (int)AsyncComputePath.GpuParticles,
        All = SimpleDdgiUpdate | FarFieldClipmapBake |
            AmbientOcclusionBlur | HiZBuild | Fog | Bloom | GpuParticles
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
        ValidationFallback = 7,
        Uncertified = 8,
        QuarantinedAfterValidationError = 9
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
        IReadOnlyList<string> Passes,
        string EvidenceRevision = "");

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
                AsyncComputePath.FarFieldClipmapBake => settings.FarFieldClipmapBakeEnabled,
                AsyncComputePath.AmbientOcclusionBlur => settings.AmbientOcclusionBlurEnabled,
                AsyncComputePath.HiZBuild => settings.HiZBuildEnabled,
                AsyncComputePath.Fog => settings.FogEnabled,
                AsyncComputePath.Bloom => settings.BloomEnabled,
                AsyncComputePath.GpuParticles => settings.GpuParticlesEnabled,
                _ => false
            };
        }

        public static bool IsPreferred(
            this AsyncComputeSettings settings,
            AsyncComputePath path)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            uint bit = 1u << (int)path;
            return ((uint)settings.PreferredPathMask & bit) != 0u;
        }
    }
}
