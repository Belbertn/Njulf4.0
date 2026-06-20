using System;

namespace Njulf.Rendering.Data
{
    public enum HiZVisibilityPolicyStatus
    {
        Disabled,
        WarmingUp,
        Active,
        Skipped
    }

    public sealed class HiZVisibilityPolicySettings
    {
        private int _warmupFrameCount = 1;
        private float _cameraCutDistance = 5.0f;
        private float _cameraCutForwardDotThreshold = 0.5f;
        private int _minMeasuredOcclusionTests = 512;
        private float _minUsefulOcclusionCullRate = 0.03f;
        private int _adaptiveProbeIntervalFrames = 60;

        public int WarmupFrameCount
        {
            get => _warmupFrameCount;
            set => _warmupFrameCount = Math.Clamp(value, 0, 8);
        }

        public float CameraCutDistance
        {
            get => _cameraCutDistance;
            set => _cameraCutDistance = Math.Clamp(value, 0.0f, 1000.0f);
        }

        public float CameraCutForwardDotThreshold
        {
            get => _cameraCutForwardDotThreshold;
            set => _cameraCutForwardDotThreshold = Math.Clamp(value, -1.0f, 1.0f);
        }

        public int MinMeasuredOcclusionTests
        {
            get => _minMeasuredOcclusionTests;
            set => _minMeasuredOcclusionTests = Math.Clamp(value, 0, 1_000_000);
        }

        public float MinUsefulOcclusionCullRate
        {
            get => _minUsefulOcclusionCullRate;
            set => _minUsefulOcclusionCullRate = Math.Clamp(value, 0.0f, 1.0f);
        }

        public int AdaptiveProbeIntervalFrames
        {
            get => _adaptiveProbeIntervalFrames;
            set => _adaptiveProbeIntervalFrames = Math.Clamp(value, 1, 10_000);
        }
    }

    public sealed class HiZVisibilityPolicyRuntimeState
    {
        public bool PyramidValid { get; set; }
        public int WarmupFramesRemaining { get; set; }
        public bool AdaptiveSuppressed { get; set; }
        public int AdaptiveProbeCountdown { get; set; }
    }

    public readonly record struct HiZVisibilityPolicyInput(
        bool DepthPrePassEnabled,
        bool HiZOcclusionEnabled,
        bool FeatureIsolationDisablesHiZ,
        HiZTestMode RequestedTestMode,
        bool SceneChanged,
        bool CameraCut,
        bool AdaptiveEnabled,
        bool MeshletCountersActive,
        int CompletedForwardOcclusionTested,
        int CompletedForwardOcclusionCulled);

    public readonly record struct HiZVisibilityPolicyDecision(
        bool BuildHiZ,
        bool UseHiZForOcclusion,
        HiZVisibilityPolicyStatus Status,
        string Reason,
        int WarmupFramesRemaining,
        bool SceneChanged,
        bool CameraCut,
        bool PyramidInvalidated,
        bool AdaptiveSuppressed,
        bool AdaptiveProbe,
        int AdaptiveProbeCountdown,
        int AdaptiveMeasuredOcclusionTests,
        int AdaptiveMeasuredOcclusionCulled,
        float AdaptiveCullRate);

    public static class HiZVisibilityPolicy
    {
        public static HiZVisibilityPolicyDecision Plan(
            HiZVisibilityPolicyInput input,
            HiZVisibilityPolicySettings settings,
            HiZVisibilityPolicyRuntimeState state)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            float cullRate = input.CompletedForwardOcclusionTested > 0
                ? (float)input.CompletedForwardOcclusionCulled / input.CompletedForwardOcclusionTested
                : 0.0f;

            if (!input.DepthPrePassEnabled)
                return Disable(state, cullRate, "Depth prepass disabled.");

            if (!input.HiZOcclusionEnabled)
                return Disable(state, cullRate, "Hi-Z occlusion disabled.");

            if (input.FeatureIsolationDisablesHiZ)
                return Disable(state, cullRate, "Feature isolation disables Hi-Z.");

            if (input.RequestedTestMode == HiZTestMode.Off)
                return Disable(state, cullRate, "Hi-Z test mode is off.");

            bool pyramidInvalidated = !state.PyramidValid;
            if (input.SceneChanged || input.CameraCut || pyramidInvalidated)
            {
                state.WarmupFramesRemaining = Math.Max(state.WarmupFramesRemaining, settings.WarmupFrameCount);
                state.PyramidValid = false;
            }

            bool adaptiveProbe = UpdateAdaptiveState(input, settings, state, cullRate);

            if (state.WarmupFramesRemaining > 0)
            {
                int warmupFramesRemaining = state.WarmupFramesRemaining;
                state.WarmupFramesRemaining--;
                return new HiZVisibilityPolicyDecision(
                    BuildHiZ: true,
                    UseHiZForOcclusion: false,
                    Status: HiZVisibilityPolicyStatus.WarmingUp,
                    Reason: BuildWarmupReason(input, pyramidInvalidated),
                    WarmupFramesRemaining: warmupFramesRemaining,
                    SceneChanged: input.SceneChanged,
                    CameraCut: input.CameraCut,
                    PyramidInvalidated: pyramidInvalidated,
                    AdaptiveSuppressed: state.AdaptiveSuppressed,
                    AdaptiveProbe: adaptiveProbe,
                    AdaptiveProbeCountdown: state.AdaptiveProbeCountdown,
                    AdaptiveMeasuredOcclusionTests: input.CompletedForwardOcclusionTested,
                    AdaptiveMeasuredOcclusionCulled: input.CompletedForwardOcclusionCulled,
                    AdaptiveCullRate: cullRate);
            }

            if (state.AdaptiveSuppressed && !adaptiveProbe)
            {
                return new HiZVisibilityPolicyDecision(
                    BuildHiZ: true,
                    UseHiZForOcclusion: false,
                    Status: HiZVisibilityPolicyStatus.Skipped,
                    Reason: "Adaptive Hi-Z policy skipped occlusion because measured benefit is below threshold.",
                    WarmupFramesRemaining: 0,
                    SceneChanged: input.SceneChanged,
                    CameraCut: input.CameraCut,
                    PyramidInvalidated: pyramidInvalidated,
                    AdaptiveSuppressed: true,
                    AdaptiveProbe: false,
                    AdaptiveProbeCountdown: state.AdaptiveProbeCountdown,
                    AdaptiveMeasuredOcclusionTests: input.CompletedForwardOcclusionTested,
                    AdaptiveMeasuredOcclusionCulled: input.CompletedForwardOcclusionCulled,
                    AdaptiveCullRate: cullRate);
            }

            string reason = adaptiveProbe
                ? "Adaptive Hi-Z policy is probing occlusion benefit."
                : "Hi-Z occlusion active.";
            return new HiZVisibilityPolicyDecision(
                BuildHiZ: true,
                UseHiZForOcclusion: true,
                Status: HiZVisibilityPolicyStatus.Active,
                Reason: reason,
                WarmupFramesRemaining: 0,
                SceneChanged: input.SceneChanged,
                CameraCut: input.CameraCut,
                PyramidInvalidated: pyramidInvalidated,
                AdaptiveSuppressed: state.AdaptiveSuppressed,
                AdaptiveProbe: adaptiveProbe,
                AdaptiveProbeCountdown: state.AdaptiveProbeCountdown,
                AdaptiveMeasuredOcclusionTests: input.CompletedForwardOcclusionTested,
                AdaptiveMeasuredOcclusionCulled: input.CompletedForwardOcclusionCulled,
                AdaptiveCullRate: cullRate);
        }

        private static HiZVisibilityPolicyDecision Disable(
            HiZVisibilityPolicyRuntimeState state,
            float cullRate,
            string reason)
        {
            state.PyramidValid = false;
            state.WarmupFramesRemaining = 0;
            state.AdaptiveSuppressed = false;
            state.AdaptiveProbeCountdown = 0;
            return new HiZVisibilityPolicyDecision(
                BuildHiZ: false,
                UseHiZForOcclusion: false,
                Status: HiZVisibilityPolicyStatus.Disabled,
                Reason: reason,
                WarmupFramesRemaining: 0,
                SceneChanged: false,
                CameraCut: false,
                PyramidInvalidated: false,
                AdaptiveSuppressed: false,
                AdaptiveProbe: false,
                AdaptiveProbeCountdown: 0,
                AdaptiveMeasuredOcclusionTests: 0,
                AdaptiveMeasuredOcclusionCulled: 0,
                AdaptiveCullRate: cullRate);
        }

        private static bool UpdateAdaptiveState(
            HiZVisibilityPolicyInput input,
            HiZVisibilityPolicySettings settings,
            HiZVisibilityPolicyRuntimeState state,
            float cullRate)
        {
            if (!input.AdaptiveEnabled || !input.MeshletCountersActive)
            {
                state.AdaptiveSuppressed = false;
                state.AdaptiveProbeCountdown = 0;
                return false;
            }

            if (input.CompletedForwardOcclusionTested >= settings.MinMeasuredOcclusionTests)
            {
                state.AdaptiveSuppressed = cullRate < settings.MinUsefulOcclusionCullRate;
                state.AdaptiveProbeCountdown = state.AdaptiveSuppressed ? settings.AdaptiveProbeIntervalFrames : 0;
            }
            else if (state.AdaptiveSuppressed && state.AdaptiveProbeCountdown > 0)
            {
                state.AdaptiveProbeCountdown--;
            }

            if (!state.AdaptiveSuppressed || state.AdaptiveProbeCountdown != 0)
                return false;

            state.AdaptiveProbeCountdown = settings.AdaptiveProbeIntervalFrames;
            return true;
        }

        private static string BuildWarmupReason(HiZVisibilityPolicyInput input, bool pyramidInvalidated)
        {
            if (input.SceneChanged)
                return "Scene changed; warming Hi-Z before occlusion.";
            if (input.CameraCut)
                return "Camera cut detected; warming Hi-Z before occlusion.";
            if (pyramidInvalidated)
                return "Hi-Z pyramid invalidated; warming before occlusion.";
            return "Hi-Z warming before occlusion.";
        }
    }
}
