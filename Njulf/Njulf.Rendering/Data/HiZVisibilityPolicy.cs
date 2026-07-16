using System;

namespace Njulf.Rendering.Data
{
    public enum HiZVisibilityPolicyStatus
    {
        Disabled,
        WarmingUp,
        Active,
        Skipped,
        NoConsumer,
        Suppressed,
        Probing,
        ForcedOn
    }

    public enum HiZCounterSource
    {
        Unavailable,
        LegacyTaskShader,
        SceneSubmissionCompaction,
        ForwardVisibilityCompaction
    }

    public sealed class HiZVisibilityPolicySettings
    {
        private int _warmupFrameCount = 1;
        private float _cameraCutDistance = 5.0f;
        private float _cameraCutForwardDotThreshold = 0.5f;
        private int _minMeasuredOcclusionTests = 512;
        private float _minUsefulOcclusionCullRate = 0.03f;
        private int _adaptiveProbeIntervalFrames = 60;
        private int _unprofitableFrameThreshold = 3;
        private long _minEstimatedSavedMicroseconds = 50;
        private float _minEstimatedSavedToCostRatio = 1.10f;
        private int _adaptiveSmoothingFrameWindow = 30;

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

        public int UnprofitableFrameThreshold
        {
            get => _unprofitableFrameThreshold;
            set => _unprofitableFrameThreshold = Math.Clamp(value, 1, 120);
        }

        public long MinEstimatedSavedMicroseconds
        {
            get => _minEstimatedSavedMicroseconds;
            set => _minEstimatedSavedMicroseconds = Math.Clamp(value, 0, 100_000);
        }

        public float MinEstimatedSavedToCostRatio
        {
            get => _minEstimatedSavedToCostRatio;
            set => _minEstimatedSavedToCostRatio = Math.Clamp(value, 0.0f, 10.0f);
        }

        public int AdaptiveSmoothingFrameWindow
        {
            get => _adaptiveSmoothingFrameWindow;
            set => _adaptiveSmoothingFrameWindow = Math.Clamp(value, 1, 240);
        }

        public bool ForceHiZOcclusionOn { get; set; }
        public bool ForceAdaptiveProbe { get; set; }
    }

    public sealed class HiZVisibilityPolicyRuntimeState
    {
        public bool PyramidValid { get; set; }
        public int WarmupFramesRemaining { get; set; }
        public bool AdaptiveSuppressed { get; set; }
        public int AdaptiveProbeCountdown { get; set; }
        public int ConsecutiveUnprofitableFrames { get; set; }
        public int SuppressedFrameCount { get; set; }
        public long LastEstimatedSavedMicroseconds { get; set; }
        public long LastEstimatedCostMicroseconds { get; set; }
        public long LastEstimatedNetMicroseconds { get; set; }
        public float SmoothedCullRate { get; set; }
        public float SmoothedSavedToCostRatio { get; set; }
        public bool HasSmoothedCullRate { get; set; }
        public bool HasSmoothedSavedToCostRatio { get; set; }
    }

    public readonly record struct HiZVisibilityPolicyInput(
        bool DepthPrePassEnabled,
        bool HiZOcclusionEnabled,
        bool FeatureIsolationDisablesHiZ,
        HiZTestMode RequestedTestMode,
        bool SceneChanged,
        bool CameraCut,
        bool AdaptiveEnabled,
        bool ProductionCountersAvailable,
        HiZCounterSource CounterSource,
        int CompletedHiZTested,
        int CompletedHiZCulled,
        bool DepthPrePassRequiredByOtherFeatures = false,
        long CompletedDepthPrePassMicroseconds = 0,
        long CompletedHiZBuildMicroseconds = 0,
        long CompletedSceneSubmissionCompactionMicroseconds = 0,
        long CompletedForwardOpaqueMicroseconds = 0);

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
        float AdaptiveCullRate,
        HiZCounterSource CounterSource = HiZCounterSource.Unavailable,
        long AdaptiveEstimatedSavedMicroseconds = 0,
        long AdaptiveEstimatedCostMicroseconds = 0,
        long AdaptiveEstimatedNetMicroseconds = 0,
        float AdaptiveSmoothedCullRate = 0.0f,
        float AdaptiveSmoothedSavedToCostRatio = 0.0f,
        int AdaptiveSuppressedFrameCount = 0,
        string AdaptiveStatus = "");

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

            float cullRate = input.CompletedHiZTested > 0
                ? (float)input.CompletedHiZCulled / input.CompletedHiZTested
                : 0.0f;
            long estimatedSavedMicroseconds = EstimateSavedForwardMicroseconds(input);
            long estimatedCostMicroseconds = EstimateHiZCostMicroseconds(input);
            long estimatedNetMicroseconds = estimatedSavedMicroseconds - estimatedCostMicroseconds;
            float savedToCostRatio = CalculateSavedToCostRatio(estimatedSavedMicroseconds, estimatedCostMicroseconds);
            state.LastEstimatedSavedMicroseconds = estimatedSavedMicroseconds;
            state.LastEstimatedCostMicroseconds = estimatedCostMicroseconds;
            state.LastEstimatedNetMicroseconds = estimatedNetMicroseconds;

            if (!input.DepthPrePassEnabled)
                return Disable(state, cullRate, "Depth prepass disabled.");

            if (!input.HiZOcclusionEnabled)
                return Disable(state, cullRate, "Hi-Z occlusion disabled.");

            if (input.FeatureIsolationDisablesHiZ)
                return Disable(state, cullRate, "Feature isolation disables Hi-Z.");

            if (input.RequestedTestMode == HiZTestMode.Off)
                return Disable(state, cullRate, "Hi-Z test mode is off.");

            bool pyramidInvalidated = !state.PyramidValid;
            if (input.SceneChanged || input.CameraCut)
            {
                ResetAdaptiveState(state, resetSmoothing: true);
                state.WarmupFramesRemaining = Math.Max(state.WarmupFramesRemaining, settings.WarmupFrameCount);
                state.PyramidValid = false;
            }
            else if (pyramidInvalidated && !state.AdaptiveSuppressed)
            {
                state.WarmupFramesRemaining = Math.Max(state.WarmupFramesRemaining, settings.WarmupFrameCount);
            }

            bool forcedOn = settings.ForceHiZOcclusionOn;
            if (forcedOn)
            {
                ResetAdaptiveState(state, resetSmoothing: false);
            }

            bool adaptiveProbe = !forcedOn && UpdateAdaptiveState(
                input,
                settings,
                state,
                cullRate,
                savedToCostRatio,
                estimatedSavedMicroseconds);
            if (!forcedOn && settings.ForceAdaptiveProbe)
            {
                adaptiveProbe = true;
                state.AdaptiveSuppressed = true;
                state.AdaptiveProbeCountdown = settings.AdaptiveProbeIntervalFrames;
            }

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
                    AdaptiveMeasuredOcclusionTests: input.CompletedHiZTested,
                    AdaptiveMeasuredOcclusionCulled: input.CompletedHiZCulled,
                    AdaptiveCullRate: cullRate,
                    CounterSource: input.CounterSource,
                    AdaptiveEstimatedSavedMicroseconds: estimatedSavedMicroseconds,
                    AdaptiveEstimatedCostMicroseconds: estimatedCostMicroseconds,
                    AdaptiveEstimatedNetMicroseconds: estimatedNetMicroseconds,
                    AdaptiveSmoothedCullRate: state.SmoothedCullRate,
                    AdaptiveSmoothedSavedToCostRatio: state.SmoothedSavedToCostRatio,
                    AdaptiveSuppressedFrameCount: state.SuppressedFrameCount,
                    AdaptiveStatus: BuildAdaptiveStatus(input, state, adaptiveProbe, forcedOn, "WarmingUp"));
            }

            if (state.AdaptiveSuppressed && !adaptiveProbe)
            {
                state.SuppressedFrameCount++;
                return new HiZVisibilityPolicyDecision(
                    BuildHiZ: false,
                    UseHiZForOcclusion: false,
                    Status: HiZVisibilityPolicyStatus.Suppressed,
                    Reason: "Adaptive Hi-Z policy suppressed Hi-Z because measured benefit is below threshold.",
                    WarmupFramesRemaining: 0,
                    SceneChanged: input.SceneChanged,
                    CameraCut: input.CameraCut,
                    PyramidInvalidated: pyramidInvalidated,
                    AdaptiveSuppressed: true,
                    AdaptiveProbe: false,
                    AdaptiveProbeCountdown: state.AdaptiveProbeCountdown,
                    AdaptiveMeasuredOcclusionTests: input.CompletedHiZTested,
                    AdaptiveMeasuredOcclusionCulled: input.CompletedHiZCulled,
                    AdaptiveCullRate: cullRate,
                    CounterSource: input.CounterSource,
                    AdaptiveEstimatedSavedMicroseconds: estimatedSavedMicroseconds,
                    AdaptiveEstimatedCostMicroseconds: estimatedCostMicroseconds,
                    AdaptiveEstimatedNetMicroseconds: estimatedNetMicroseconds,
                    AdaptiveSmoothedCullRate: state.SmoothedCullRate,
                    AdaptiveSmoothedSavedToCostRatio: state.SmoothedSavedToCostRatio,
                    AdaptiveSuppressedFrameCount: state.SuppressedFrameCount,
                    AdaptiveStatus: "Suppressed");
            }

            string reason = forcedOn
                ? "Hi-Z occlusion forced on."
                : adaptiveProbe
                ? "Adaptive Hi-Z policy is probing occlusion benefit."
                : "Hi-Z occlusion active.";
            return new HiZVisibilityPolicyDecision(
                BuildHiZ: true,
                UseHiZForOcclusion: true,
                Status: forcedOn
                    ? HiZVisibilityPolicyStatus.ForcedOn
                    : adaptiveProbe
                        ? HiZVisibilityPolicyStatus.Probing
                        : HiZVisibilityPolicyStatus.Active,
                Reason: reason,
                WarmupFramesRemaining: 0,
                SceneChanged: input.SceneChanged,
                CameraCut: input.CameraCut,
                PyramidInvalidated: pyramidInvalidated,
                AdaptiveSuppressed: state.AdaptiveSuppressed,
                AdaptiveProbe: adaptiveProbe,
                AdaptiveProbeCountdown: state.AdaptiveProbeCountdown,
                AdaptiveMeasuredOcclusionTests: input.CompletedHiZTested,
                AdaptiveMeasuredOcclusionCulled: input.CompletedHiZCulled,
                AdaptiveCullRate: cullRate,
                CounterSource: input.CounterSource,
                AdaptiveEstimatedSavedMicroseconds: estimatedSavedMicroseconds,
                AdaptiveEstimatedCostMicroseconds: estimatedCostMicroseconds,
                AdaptiveEstimatedNetMicroseconds: estimatedNetMicroseconds,
                AdaptiveSmoothedCullRate: state.SmoothedCullRate,
                AdaptiveSmoothedSavedToCostRatio: state.SmoothedSavedToCostRatio,
                AdaptiveSuppressedFrameCount: state.SuppressedFrameCount,
                AdaptiveStatus: BuildAdaptiveStatus(input, state, adaptiveProbe, forcedOn, "Active"));
        }

        private static HiZVisibilityPolicyDecision Disable(
            HiZVisibilityPolicyRuntimeState state,
            float cullRate,
            string reason)
        {
            state.PyramidValid = false;
            state.WarmupFramesRemaining = 0;
            ResetAdaptiveState(state, resetSmoothing: true);
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
                AdaptiveCullRate: cullRate,
                CounterSource: HiZCounterSource.Unavailable,
                AdaptiveStatus: "Disabled");
        }

        private static bool UpdateAdaptiveState(
            HiZVisibilityPolicyInput input,
            HiZVisibilityPolicySettings settings,
            HiZVisibilityPolicyRuntimeState state,
            float cullRate,
            float savedToCostRatio,
            long estimatedSavedMicroseconds)
        {
            if (!input.AdaptiveEnabled || !input.ProductionCountersAvailable)
            {
                ResetAdaptiveState(state, resetSmoothing: true);
                return false;
            }

            if (input.CompletedHiZTested >= settings.MinMeasuredOcclusionTests)
            {
                UpdateSmoothedMeasurements(settings, state, cullRate, savedToCostRatio);
                bool useful = IsUsefulMeasurement(
                    input,
                    settings,
                    state.HasSmoothedCullRate ? state.SmoothedCullRate : cullRate,
                    state.HasSmoothedSavedToCostRatio ? state.SmoothedSavedToCostRatio : savedToCostRatio,
                    estimatedSavedMicroseconds);
                if (useful)
                {
                    ResetAdaptiveState(state, resetSmoothing: false);
                }
                else
                {
                    state.ConsecutiveUnprofitableFrames++;
                    if (state.ConsecutiveUnprofitableFrames >= settings.UnprofitableFrameThreshold)
                    {
                        state.AdaptiveSuppressed = true;
                        if (state.AdaptiveProbeCountdown <= 0)
                            state.AdaptiveProbeCountdown = settings.AdaptiveProbeIntervalFrames;
                    }
                }
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

        private static bool IsUsefulMeasurement(
            HiZVisibilityPolicyInput input,
            HiZVisibilityPolicySettings settings,
            float cullRate,
            float savedToCostRatio,
            long estimatedSavedMicroseconds)
        {
            if (cullRate < settings.MinUsefulOcclusionCullRate)
                return false;

            if (input.CompletedForwardOpaqueMicroseconds <= 0 || savedToCostRatio <= 0.0f)
                return true;

            return estimatedSavedMicroseconds >= settings.MinEstimatedSavedMicroseconds &&
                savedToCostRatio >= settings.MinEstimatedSavedToCostRatio;
        }

        private static void UpdateSmoothedMeasurements(
            HiZVisibilityPolicySettings settings,
            HiZVisibilityPolicyRuntimeState state,
            float cullRate,
            float savedToCostRatio)
        {
            float alpha = 2.0f / (settings.AdaptiveSmoothingFrameWindow + 1.0f);
            state.SmoothedCullRate = state.HasSmoothedCullRate
                ? (alpha * cullRate) + ((1.0f - alpha) * state.SmoothedCullRate)
                : cullRate;
            state.HasSmoothedCullRate = true;

            state.SmoothedSavedToCostRatio = state.HasSmoothedSavedToCostRatio
                ? (alpha * savedToCostRatio) + ((1.0f - alpha) * state.SmoothedSavedToCostRatio)
                : savedToCostRatio;
            state.HasSmoothedSavedToCostRatio = true;
        }

        private static long EstimateSavedForwardMicroseconds(HiZVisibilityPolicyInput input)
        {
            int visibleAfterOcclusion = Math.Max(1, input.CompletedHiZTested - input.CompletedHiZCulled);
            if (input.CompletedForwardOpaqueMicroseconds <= 0 || input.CompletedHiZCulled <= 0)
                return 0;

            double saved = input.CompletedForwardOpaqueMicroseconds *
                ((double)input.CompletedHiZCulled / visibleAfterOcclusion);
            return (long)Math.Round(saved);
        }

        private static long EstimateHiZCostMicroseconds(HiZVisibilityPolicyInput input)
        {
            long cost = Math.Max(0, input.CompletedHiZBuildMicroseconds) +
                Math.Max(0, input.CompletedSceneSubmissionCompactionMicroseconds);
            if (!input.DepthPrePassRequiredByOtherFeatures)
                cost += Math.Max(0, input.CompletedDepthPrePassMicroseconds);
            return cost;
        }

        private static float CalculateSavedToCostRatio(long estimatedSavedMicroseconds, long estimatedCostMicroseconds)
        {
            if (estimatedCostMicroseconds <= 0)
                return estimatedSavedMicroseconds > 0 ? float.PositiveInfinity : 0.0f;

            return (float)estimatedSavedMicroseconds / estimatedCostMicroseconds;
        }

        private static string BuildAdaptiveStatus(
            HiZVisibilityPolicyInput input,
            HiZVisibilityPolicyRuntimeState state,
            bool adaptiveProbe,
            bool forcedOn,
            string fallbackStatus)
        {
            if (forcedOn)
                return "ForcedOn";
            if (!input.AdaptiveEnabled)
                return "Disabled";
            if (!input.ProductionCountersAvailable || input.CounterSource == HiZCounterSource.Unavailable)
                return "CountersUnavailable";
            if (adaptiveProbe)
                return "Probing";
            if (state.AdaptiveSuppressed)
                return "Suppressed";
            return fallbackStatus;
        }

        private static void ResetAdaptiveState(HiZVisibilityPolicyRuntimeState state, bool resetSmoothing)
        {
            state.AdaptiveSuppressed = false;
            state.AdaptiveProbeCountdown = 0;
            state.ConsecutiveUnprofitableFrames = 0;
            state.SuppressedFrameCount = 0;
            if (!resetSmoothing)
                return;

            state.SmoothedCullRate = 0.0f;
            state.SmoothedSavedToCostRatio = 0.0f;
            state.HasSmoothedCullRate = false;
            state.HasSmoothedSavedToCostRatio = false;
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
