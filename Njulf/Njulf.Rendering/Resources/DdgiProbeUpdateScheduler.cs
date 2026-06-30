using System;
using System.Collections.Generic;
using System.Diagnostics;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    internal readonly record struct DdgiProbeUpdateSchedulerResult(
        int RequestCount,
        int NextUpdateCursor,
        int CompatibilityStartProbeIndex,
        int SafetyRequestCount);

    internal readonly record struct DdgiAdaptiveBudgetSelection(
        int RequestBudget,
        int PrimaryRayBudget,
        float BudgetScale,
        bool BudgetReduced,
        bool EmergencyDegradeActive,
        int EffectiveMaxShadedLights,
        string Reason);

    internal readonly record struct DdgiWarmupSchedulingContext(
        DdgiRuntimeWarmupState State,
        int WarmupMaxAgeFrames)
    {
        public readonly bool IsWarmupActive =>
            State is DdgiRuntimeWarmupState.LocalVolumeWarmup
                or DdgiRuntimeWarmupState.NearCascadeWarmup
                or DdgiRuntimeWarmupState.Recovery;

        public static DdgiWarmupSchedulingContext Disabled { get; } =
            new(DdgiRuntimeWarmupState.Disabled, 0);
    }

    internal sealed class DdgiCpuSchedulerInstrumentation
    {
        public long PhaseClipmapDirtyMicroseconds { get; private set; }
        public long PhaseDirtyRegionsMicroseconds { get; private set; }
        public long PhaseUninitializedMicroseconds { get; private set; }
        public long PhaseFrustumMicroseconds { get; private set; }
        public long PhaseSafetyMicroseconds { get; private set; }
        public long PhaseRoundRobinMicroseconds { get; private set; }
        public int CandidateInsertCount { get; private set; }
        public int CandidateMaxShiftCount { get; private set; }
        public int ViewCandidateProbeEvaluationCount { get; private set; }

        public void Reset()
        {
            PhaseClipmapDirtyMicroseconds = 0;
            PhaseDirtyRegionsMicroseconds = 0;
            PhaseUninitializedMicroseconds = 0;
            PhaseFrustumMicroseconds = 0;
            PhaseSafetyMicroseconds = 0;
            PhaseRoundRobinMicroseconds = 0;
            CandidateInsertCount = 0;
            CandidateMaxShiftCount = 0;
            ViewCandidateProbeEvaluationCount = 0;
        }

        internal void RecordCandidateInsertion(int shiftCount)
        {
            CandidateInsertCount++;
            if (shiftCount > CandidateMaxShiftCount)
                CandidateMaxShiftCount = shiftCount;
        }

        internal void RecordViewCandidateProbeEvaluation() => ViewCandidateProbeEvaluationCount++;

        internal void RecordPhase(DdgiCpuSchedulerPhase phase, long microseconds)
        {
            switch (phase)
            {
                case DdgiCpuSchedulerPhase.ClipmapDirty:
                    PhaseClipmapDirtyMicroseconds = microseconds;
                    break;
                case DdgiCpuSchedulerPhase.DirtyRegions:
                    PhaseDirtyRegionsMicroseconds = microseconds;
                    break;
                case DdgiCpuSchedulerPhase.Uninitialized:
                    PhaseUninitializedMicroseconds = microseconds;
                    break;
                case DdgiCpuSchedulerPhase.Frustum:
                    PhaseFrustumMicroseconds = microseconds;
                    break;
                case DdgiCpuSchedulerPhase.Safety:
                    PhaseSafetyMicroseconds = microseconds;
                    break;
                case DdgiCpuSchedulerPhase.RoundRobin:
                    PhaseRoundRobinMicroseconds = microseconds;
                    break;
            }
        }
    }

    internal enum DdgiCpuSchedulerPhase
    {
        ClipmapDirty,
        DirtyRegions,
        Uninitialized,
        Frustum,
        Safety,
        RoundRobin
    }

    internal static class DdgiProbeUpdateScheduler
    {
        private const int StackCandidateQueueLimit = 256;

        internal const uint PriorityNewCell = 0u;
        internal const uint PriorityDirtyGeometry = 1u;
        internal const uint PriorityDirtyLighting = 2u;
        internal const uint PriorityDirectionalLight = 3u;
        internal const uint PriorityDirtyBounds = 4u;
        internal const uint PriorityVisibleFrustum = 5u;
        internal const uint PriorityNearCamera = 6u;
        internal const uint PriorityAgeRefresh = 7u;
        internal const uint PriorityOutsideFrustumSafety = 8u;
        internal const uint PriorityFarCascade = 9u;

        public static DdgiProbeUpdateSchedulerResult BuildRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds,
            int activeProbeCount,
            int hardMaxRequestCount,
            int updateCursor,
            GlobalIlluminationSettings settings,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks)
        {
            return BuildRequests(
                volumes,
                layout,
                dirtyBounds,
                activeProbeCount,
                hardMaxRequestCount,
                int.MaxValue,
                updateCursor,
                settings,
                destination,
                probeMarks);
        }

        public static DdgiProbeUpdateSchedulerResult BuildRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds,
            int activeProbeCount,
            int hardMaxRequestCount,
            int hardMaxPrimaryRayCount,
            int updateCursor,
            GlobalIlluminationSettings settings,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks)
        {
            return BuildRequests(
                volumes,
                layout,
                dirtyBounds,
                activeProbeCount,
                hardMaxRequestCount,
                hardMaxPrimaryRayCount,
                updateCursor,
                settings,
                destination,
                probeMarks,
                scratch: null,
                probeFeedback: ReadOnlySpan<DdgiProbeSchedulerFeedback>.Empty,
                DdgiWarmupSchedulingContext.Disabled);
        }

        public static DdgiProbeUpdateSchedulerResult BuildRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds,
            int activeProbeCount,
            int hardMaxRequestCount,
            int hardMaxPrimaryRayCount,
            int updateCursor,
            GlobalIlluminationSettings settings,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            DdgiProbeUpdateSchedulerScratch? scratch)
        {
            return BuildRequests(
                volumes,
                layout,
                dirtyBounds,
                activeProbeCount,
                hardMaxRequestCount,
                hardMaxPrimaryRayCount,
                updateCursor,
                settings,
                destination,
                probeMarks,
                scratch,
                ReadOnlySpan<DdgiProbeSchedulerFeedback>.Empty,
                DdgiWarmupSchedulingContext.Disabled);
        }

        public static DdgiProbeUpdateSchedulerResult BuildRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds,
            int activeProbeCount,
            int hardMaxRequestCount,
            int hardMaxPrimaryRayCount,
            int updateCursor,
            GlobalIlluminationSettings settings,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            DdgiProbeUpdateSchedulerScratch? scratch,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            DdgiCpuSchedulerInstrumentation? instrumentation = null)
        {
            return BuildRequests(
                volumes,
                layout,
                dirtyBounds,
                activeProbeCount,
                hardMaxRequestCount,
                hardMaxPrimaryRayCount,
                updateCursor,
                settings,
                destination,
                probeMarks,
                scratch,
                probeFeedback,
                DdgiWarmupSchedulingContext.Disabled,
                instrumentation);
        }

        public static DdgiProbeUpdateSchedulerResult BuildRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds,
            int activeProbeCount,
            int hardMaxRequestCount,
            int hardMaxPrimaryRayCount,
            int updateCursor,
            GlobalIlluminationSettings settings,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            DdgiProbeUpdateSchedulerScratch? scratch,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            DdgiWarmupSchedulingContext warmup,
            DdgiCpuSchedulerInstrumentation? instrumentation = null)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (activeProbeCount <= 0 || hardMaxRequestCount <= 0 || destination.IsEmpty)
                return new DdgiProbeUpdateSchedulerResult(0, 0, 0, 0);

            int budget = Math.Min(Math.Min(activeProbeCount, hardMaxRequestCount), destination.Length);
            ulong primaryRayBudget = hardMaxPrimaryRayCount <= 0 ? 0UL : (ulong)hardMaxPrimaryRayCount;
            if (budget <= 0 || primaryRayBudget == 0UL || settings.DdgiProbeUpdateTimeBudgetMilliseconds <= 0.0f)
                return new DdgiProbeUpdateSchedulerResult(0, Math.Clamp(updateCursor, 0, Math.Max(0, activeProbeCount - 1)), 0, 0);

            if (probeMarks.Length < activeProbeCount)
                throw new ArgumentException("Probe mark scratch storage is smaller than the active DDGI probe count.", nameof(probeMarks));

            probeMarks[..activeProbeCount].Clear();

            int cursor = NormalizeCursor(updateCursor, activeProbeCount);
            int count = 0;
            ulong primaryRaysUsed = 0UL;
            int safetyQuota = Math.Clamp(
                (int)MathF.Ceiling(budget * settings.DdgiOutOfFrustumMinimumUpdateFraction),
                0,
                budget);
            int priorityLimit = Math.Max(0, budget - safetyQuota);
            DdgiViewPriorityContext viewPriority = layout?.ViewPriority ?? default;
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions = ResolveDirtyRegions(layout, dirtyBounds);

            if (warmup.IsWarmupActive)
            {
                return BuildWarmupRequests(
                    volumes,
                    layout,
                    dirtyRegions,
                    activeProbeCount,
                    budget,
                    primaryRayBudget,
                    cursor,
                    settings,
                    destination,
                    probeMarks,
                    scratch,
                    probeFeedback,
                    warmup,
                    viewPriority,
                    instrumentation,
                    ref primaryRaysUsed);
            }

            long phaseStart = StartInstrumentedPhase(instrumentation);
            AddClipmapDirtyRequests(volumes, layout, activeProbeCount, destination, probeMarks, priorityLimit, primaryRayBudget, ref primaryRaysUsed, ref count);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.ClipmapDirty, phaseStart);
            phaseStart = StartInstrumentedPhase(instrumentation);
            AddDirtyRegionRequests(volumes, dirtyRegions, activeProbeCount, destination, probeMarks, priorityLimit, primaryRayBudget, ref primaryRaysUsed, ref count);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.DirtyRegions, phaseStart);
            phaseStart = StartInstrumentedPhase(instrumentation);
            cursor = AddUninitializedClipmapRequests(
                volumes,
                layout,
                activeProbeCount,
                cursor,
                destination,
                probeMarks,
                priorityLimit,
                primaryRayBudget,
                ref primaryRaysUsed,
                ref count);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.Uninitialized, phaseStart);
            phaseStart = StartInstrumentedPhase(instrumentation);
            AddFrustumFocusedRequests(
                volumes,
                layout,
                dirtyRegions,
                viewPriority,
                activeProbeCount,
                destination,
                probeMarks,
                priorityLimit,
                primaryRayBudget,
                settings,
                scratch,
                probeFeedback,
                instrumentation,
                ref primaryRaysUsed,
                ref count);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.Frustum, phaseStart);

            int safetyRequestCount = 0;
            phaseStart = StartInstrumentedPhase(instrumentation);
            cursor = AddSafetyShellRequests(
                volumes,
                layout,
                viewPriority,
                activeProbeCount,
                cursor,
                destination,
                probeMarks,
                Math.Min(budget, count + safetyQuota),
                primaryRayBudget,
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag,
                PriorityOutsideFrustumSafety,
                scratch,
                probeFeedback,
                instrumentation,
                ref primaryRaysUsed,
                ref count,
                ref safetyRequestCount);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.Safety, phaseStart);

            int ignoredSafetyCount = 0;
            phaseStart = StartInstrumentedPhase(instrumentation);
            cursor = AddRoundRobinRequests(
                volumes,
                activeProbeCount,
                cursor,
                destination,
                probeMarks,
                budget,
                primaryRayBudget,
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag,
                PriorityAgeRefresh,
                ref primaryRaysUsed,
                ref count,
                ref ignoredSafetyCount);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.RoundRobin, phaseStart);

            int compatibilityStart = count > 0 ? checked((int)destination[0].ProbeIndex) : 0;
            return new DdgiProbeUpdateSchedulerResult(count, cursor, compatibilityStart, safetyRequestCount);
        }

        private static long StartInstrumentedPhase(DdgiCpuSchedulerInstrumentation? instrumentation) =>
            instrumentation == null ? 0L : Stopwatch.GetTimestamp();

        private static void RecordInstrumentedPhase(
            DdgiCpuSchedulerInstrumentation? instrumentation,
            DdgiCpuSchedulerPhase phase,
            long startTimestamp)
        {
            if (instrumentation == null)
                return;

            long microseconds = (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);
            instrumentation.RecordPhase(phase, microseconds);
        }

        public static int CalculateTimeBudgetedRequestCount(
            int hardMaxRequestCount,
            int activeProbeCount,
            int coldStartMaxRequestCount,
            float budgetMilliseconds,
            long previousGpuUpdateMicroseconds)
        {
            int hardMax = Math.Min(hardMaxRequestCount, activeProbeCount);
            if (hardMax <= 0 || budgetMilliseconds <= 0.0f)
                return 0;
            if (previousGpuUpdateMicroseconds <= 0)
                return Math.Clamp(coldStartMaxRequestCount, 0, hardMax);

            long budgetMicroseconds = (long)MathF.Round(budgetMilliseconds * 1000.0f);
            if (budgetMicroseconds <= 0 || previousGpuUpdateMicroseconds <= budgetMicroseconds)
                return hardMax;

            double scale = Math.Clamp((double)budgetMicroseconds / previousGpuUpdateMicroseconds, 0.1, 1.0);
            return Math.Clamp((int)MathF.Floor(hardMax * (float)scale), 1, hardMax);
        }

        public static int CalculateTimeBudgetedPrimaryRayCount(
            int steadyPrimaryRayBudget,
            int coldStartPrimaryRayBudget,
            float budgetMilliseconds,
            long previousGpuUpdateMicroseconds)
        {
            int maxRays = previousGpuUpdateMicroseconds <= 0 ? coldStartPrimaryRayBudget : steadyPrimaryRayBudget;
            if (maxRays <= 0 || budgetMilliseconds <= 0.0f)
                return 0;
            if (previousGpuUpdateMicroseconds <= 0)
                return maxRays;

            long budgetMicroseconds = (long)MathF.Round(budgetMilliseconds * 1000.0f);
            if (budgetMicroseconds <= 0 || previousGpuUpdateMicroseconds <= budgetMicroseconds)
                return maxRays;

            double scale = Math.Clamp((double)budgetMicroseconds / previousGpuUpdateMicroseconds, 0.1, 1.0);
            return Math.Clamp((int)MathF.Floor(maxRays * (float)scale), GlobalIlluminationProbeVolume.MinRaysPerProbe, maxRays);
        }

        public static DdgiAdaptiveBudgetSelection CalculateAdaptiveBudgets(
            int hardMaxRequestCount,
            int activeProbeCount,
            int coldStartMaxRequestCount,
            int steadyPrimaryRayBudget,
            int coldStartPrimaryRayBudget,
            int minimumProbeRefreshFrames,
            int maxShadedLights,
            bool adaptiveEnabled,
            float budgetMilliseconds,
            float hysteresisFraction,
            float emergencyDegradeMultiplier,
            long previousGpuUpdateMicroseconds,
            float previousBudgetScale,
            bool previousGpuUpdateEstimated = false)
        {
            int hardMax = Math.Min(hardMaxRequestCount, activeProbeCount);
            if (hardMax <= 0 || budgetMilliseconds <= 0.0f)
            {
                return new DdgiAdaptiveBudgetSelection(
                    0,
                    0,
                    0.0f,
                    BudgetReduced: false,
                    EmergencyDegradeActive: false,
                    Math.Max(0, maxShadedLights),
                    "disabled");
            }

            int steadyRays = Math.Max(0, steadyPrimaryRayBudget);
            int coldStartRays = Math.Max(0, coldStartPrimaryRayBudget);
            int effectiveMaxShadedLights = Math.Max(0, maxShadedLights);
            if (previousGpuUpdateMicroseconds <= 0)
            {
                int coldStartRequestBudget = Math.Clamp(coldStartMaxRequestCount, 0, hardMax);
                return new DdgiAdaptiveBudgetSelection(
                    coldStartRequestBudget,
                    coldStartRays,
                    1.0f,
                    BudgetReduced: false,
                    EmergencyDegradeActive: false,
                    effectiveMaxShadedLights,
                    "cold-start");
            }

            if (!adaptiveEnabled)
            {
                int fixedRequestBudget = CalculateTimeBudgetedRequestCount(
                    hardMaxRequestCount,
                    activeProbeCount,
                    coldStartMaxRequestCount,
                    budgetMilliseconds,
                    previousGpuUpdateMicroseconds);
                int fixedPrimaryRayBudget = CalculateTimeBudgetedPrimaryRayCount(
                    steadyRays,
                    coldStartRays,
                    budgetMilliseconds,
                    previousGpuUpdateMicroseconds);
                return new DdgiAdaptiveBudgetSelection(
                    fixedRequestBudget,
                    fixedPrimaryRayBudget,
                    1.0f,
                    fixedRequestBudget < hardMax || fixedPrimaryRayBudget < steadyRays,
                    EmergencyDegradeActive: false,
                    effectiveMaxShadedLights,
                    previousGpuUpdateEstimated ? "fixed-estimate" : "fixed");
            }

            long budgetMicroseconds = (long)MathF.Round(budgetMilliseconds * 1000.0f);
            if (budgetMicroseconds <= 0)
            {
                return new DdgiAdaptiveBudgetSelection(
                    0,
                    0,
                    0.0f,
                    BudgetReduced: false,
                    EmergencyDegradeActive: false,
                    effectiveMaxShadedLights,
                    "disabled");
            }

            float clampedPreviousScale = Math.Clamp(previousBudgetScale <= 0.0f ? 1.0f : previousBudgetScale, 0.1f, 1.0f);
            float clampedHysteresis = Math.Clamp(hysteresisFraction, 0.0f, 0.75f);
            double reduceThreshold = budgetMicroseconds * (1.0 + clampedHysteresis);
            bool overBudget = previousGpuUpdateMicroseconds > reduceThreshold;
            bool emergency = previousGpuUpdateMicroseconds >= budgetMicroseconds * Math.Clamp(emergencyDegradeMultiplier, 1.0f, 8.0f);
            float budgetScale;
            string reason;
            if (overBudget)
            {
                budgetScale = Math.Min(
                    clampedPreviousScale,
                    Math.Clamp((float)((double)budgetMicroseconds / previousGpuUpdateMicroseconds), 0.1f, 1.0f));
                reason = previousGpuUpdateEstimated
                    ? (emergency ? "estimated-emergency-degrade" : "estimated-gpu-time")
                    : (emergency ? "emergency-degrade" : "gpu-time");
            }
            else if (clampedPreviousScale < 1.0f)
            {
                budgetScale = Math.Min(1.0f, clampedPreviousScale + 0.1f);
                reason = previousGpuUpdateEstimated
                    ? (budgetScale < 1.0f ? "estimated-recover" : "estimated-within-budget")
                    : (budgetScale < 1.0f ? "recover" : "within-budget");
            }
            else
            {
                budgetScale = 1.0f;
                reason = previousGpuUpdateEstimated ? "estimated-within-budget" : "within-budget";
            }

            int requestBudget = Math.Clamp((int)MathF.Floor(hardMax * budgetScale), 1, hardMax);
            if (!emergency)
            {
                int minRefreshBudget = CalculateMinimumRefreshBudget(activeProbeCount, minimumProbeRefreshFrames, hardMax);
                requestBudget = Math.Clamp(Math.Max(requestBudget, minRefreshBudget), 1, hardMax);
            }

            int primaryRayBudget = steadyRays <= 0
                ? 0
                : Math.Clamp(
                    (int)MathF.Floor(steadyRays * budgetScale),
                    GlobalIlluminationProbeVolume.MinRaysPerProbe,
                    steadyRays);
            if (emergency && effectiveMaxShadedLights > 1)
                effectiveMaxShadedLights = Math.Max(1, effectiveMaxShadedLights / 2);

            bool reduced = requestBudget < hardMax ||
                primaryRayBudget < steadyRays ||
                effectiveMaxShadedLights < Math.Max(0, maxShadedLights);
            return new DdgiAdaptiveBudgetSelection(
                requestBudget,
                primaryRayBudget,
                budgetScale,
                reduced,
                emergency,
                effectiveMaxShadedLights,
                reason);
        }

        private static int CalculateMinimumRefreshBudget(int activeProbeCount, int minimumProbeRefreshFrames, int hardMax)
        {
            if (activeProbeCount <= 0 || hardMax <= 0 || minimumProbeRefreshFrames <= 0)
                return 0;

            int budget = (int)MathF.Ceiling(activeProbeCount / (float)minimumProbeRefreshFrames);
            return Math.Clamp(budget, 1, hardMax);
        }

        private static DdgiProbeUpdateSchedulerResult BuildWarmupRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            int activeProbeCount,
            int budget,
            ulong primaryRayBudget,
            int cursor,
            GlobalIlluminationSettings settings,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            DdgiProbeUpdateSchedulerScratch? scratch,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            DdgiWarmupSchedulingContext warmup,
            DdgiViewPriorityContext viewPriority,
            DdgiCpuSchedulerInstrumentation? instrumentation,
            ref ulong primaryRaysUsed)
        {
            int count = 0;
            DdgiWarmupBudgetSplit warmupBudget = CalculateWarmupBudgetSplit(budget, warmup.State);
            int localQuota = warmupBudget.Local;
            int cascade0Quota = warmupBudget.Cascade0;
            int newCellQuota = warmupBudget.NewCell;
            int safetyQuota = warmupBudget.Safety;

            long phaseStart = StartInstrumentedPhase(instrumentation);
            AddWarmupViewRequests(
                volumes,
                layout,
                dirtyRegions,
                viewPriority,
                activeProbeCount,
                targetCount: Math.Min(budget, count + localQuota),
                includeAuthoredLocalVolumes: true,
                includeCascade0: false,
                destination,
                probeMarks,
                primaryRayBudget,
                settings,
                scratch,
                probeFeedback,
                instrumentation,
                ref primaryRaysUsed,
                ref count);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.Frustum, phaseStart);

            phaseStart = StartInstrumentedPhase(instrumentation);
            AddWarmupViewRequests(
                volumes,
                layout,
                dirtyRegions,
                viewPriority,
                activeProbeCount,
                targetCount: Math.Min(budget, count + cascade0Quota),
                includeAuthoredLocalVolumes: false,
                includeCascade0: true,
                destination,
                probeMarks,
                primaryRayBudget,
                settings,
                scratch,
                probeFeedback,
                instrumentation,
                ref primaryRaysUsed,
                ref count);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.Frustum, phaseStart);

            phaseStart = StartInstrumentedPhase(instrumentation);
            AddClipmapDirtyRequests(
                volumes,
                layout,
                activeProbeCount,
                destination,
                probeMarks,
                Math.Min(budget, count + newCellQuota),
                primaryRayBudget,
                ref primaryRaysUsed,
                ref count);
            cursor = AddUninitializedClipmapRequests(
                volumes,
                layout,
                activeProbeCount,
                cursor,
                destination,
                probeMarks,
                Math.Min(budget, count + newCellQuota),
                primaryRayBudget,
                ref primaryRaysUsed,
                ref count);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.Uninitialized, phaseStart);

            int safetyRequestCount = 0;
            phaseStart = StartInstrumentedPhase(instrumentation);
            cursor = AddSafetyShellRequests(
                volumes,
                layout,
                viewPriority,
                activeProbeCount,
                cursor,
                destination,
                probeMarks,
                Math.Min(budget, count + safetyQuota),
                primaryRayBudget,
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag |
                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag,
                PriorityOutsideFrustumSafety,
                scratch,
                probeFeedback,
                instrumentation,
                ref primaryRaysUsed,
                ref count,
                ref safetyRequestCount);
            RecordInstrumentedPhase(instrumentation, DdgiCpuSchedulerPhase.Safety, phaseStart);

            int compatibilityStart = count > 0 ? checked((int)destination[0].ProbeIndex) : 0;
            return new DdgiProbeUpdateSchedulerResult(count, cursor, compatibilityStart, safetyRequestCount);
        }

        private static DdgiWarmupBudgetSplit CalculateWarmupBudgetSplit(int requestBudget, DdgiRuntimeWarmupState state)
        {
            int budget = Math.Max(0, requestBudget);
            float localFraction = 0.45f;
            float cascade0Fraction = 0.35f;
            float newCellFraction = 0.15f;

            if (state == DdgiRuntimeWarmupState.LocalVolumeWarmup)
            {
                localFraction = 0.75f;
                cascade0Fraction = 0.15f;
                newCellFraction = 0.10f;
            }
            else if (state == DdgiRuntimeWarmupState.NearCascadeWarmup)
            {
                localFraction = 0.20f;
                cascade0Fraction = 0.65f;
                newCellFraction = 0.10f;
            }

            int local = Math.Clamp((int)MathF.Ceiling(budget * localFraction), 0, budget);
            int cascade0 = Math.Clamp((int)MathF.Ceiling(budget * cascade0Fraction), 0, Math.Max(0, budget - local));
            int newCell = Math.Clamp((int)MathF.Ceiling(budget * newCellFraction), 0, Math.Max(0, budget - local - cascade0));
            int safety = Math.Max(0, budget - local - cascade0 - newCell);
            return new DdgiWarmupBudgetSplit(local, cascade0, newCell, safety);
        }

        private static void AddWarmupViewRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            DdgiViewPriorityContext viewPriority,
            int activeProbeCount,
            int targetCount,
            bool includeAuthoredLocalVolumes,
            bool includeCascade0,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            ulong primaryRayBudget,
            GlobalIlluminationSettings settings,
            DdgiProbeUpdateSchedulerScratch? scratch,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            DdgiCpuSchedulerInstrumentation? instrumentation,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            if (!viewPriority.Enabled || count >= targetCount || activeProbeCount <= 0 || primaryRaysUsed >= primaryRayBudget)
                return;

            int queueCapacity = Math.Min(activeProbeCount, Math.Max(0, targetCount - count));
            if (queueCapacity <= 0)
                return;

            Span<DdgiProbeUpdateCandidate> candidates = queueCapacity <= StackCandidateQueueLimit
                ? stackalloc DdgiProbeUpdateCandidate[queueCapacity]
                : GetCandidateQueue(scratch, includeAuthoredLocalVolumes ? DdgiSchedulerQueueKind.VisibleNear : DdgiSchedulerQueueKind.VisibleFar, queueCapacity);
            int candidateCount = 0;
            for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
            {
                GPUDdgiProbeVolume volume = volumes[volumeIndex];
                bool cameraRelative = IsCameraRelative(volume);
                if (includeAuthoredLocalVolumes == cameraRelative)
                    continue;
                if (includeCascade0 && (!cameraRelative || CascadeIndex(volume) != 0))
                    continue;

                int firstProbe = FirstProbeIndex(volume);
                int probeCount = checked(CountX(volume) * CountY(volume) * CountZ(volume));
                int endProbe = Math.Min(activeProbeCount, firstProbe + probeCount);

                if (cameraRelative &&
                    TryBuildCameraRelativeViewCellRange(volume, viewPriority, dirtyRegions, includeSafetyShell: false, out DdgiLogicalCellRange range))
                {
                    AddCameraRelativeViewCandidates(
                        volume,
                        volumeIndex,
                        activeProbeCount,
                        range,
                        layout,
                        dirtyRegions,
                        viewPriority,
                        settings,
                        probeFeedback,
                        includeSafetyShell: false,
                        candidates,
                        ref candidateCount,
                        probeMarks,
                        instrumentation);
                    continue;
                }

                for (int probeIndex = firstProbe; probeIndex < endProbe; probeIndex++)
                {
                    instrumentation?.RecordViewCandidateProbeEvaluation();
                    TryAddViewCandidate(
                        volume,
                        volumeIndex,
                        probeIndex,
                        layout,
                        dirtyRegions,
                        viewPriority,
                        settings,
                        probeFeedback,
                        includeSafetyShell: false,
                        candidates,
                        ref candidateCount,
                        probeMarks,
                        instrumentation);
                }
            }

            for (int i = 0; i < candidateCount && count < targetCount && primaryRaysUsed < primaryRayBudget; i++)
                AddRequest(candidates[i].Request, volumes, destination, probeMarks, primaryRayBudget, ref primaryRaysUsed, ref count);
        }

        private static void AddClipmapDirtyRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            int activeProbeCount,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int priorityLimit,
            ulong primaryRayBudget,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            if (layout == null || layout.DirtyProbeRequests.Count == 0 || count >= priorityLimit || primaryRaysUsed >= primaryRayBudget)
                return;

            for (int i = 0; i < layout.DirtyProbeRequests.Count && count < priorityLimit && primaryRaysUsed < primaryRayBudget; i++)
            {
                DdgiFrameLayoutDirtyProbeRequest request = layout.DirtyProbeRequests[i];
                if (!TryResolveDirtyRequestVolume(volumes, request, out int volumeIndex, out GPUDdgiProbeVolume volume))
                    continue;

                uint reasonFlags = GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag;
                uint priority = PriorityNewCell;
                if (request.Reason == DdgiClipmapDirtyReason.DirtyBounds)
                {
                    reasonFlags |= DdgiDirtyReasonPolicy.ToProbeUpdateFlags(request.SourceReason);
                    priority = DdgiDirtyReasonPolicy.ResolvePriority(request.SourceReason);
                }
                else
                {
                    reasonFlags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag;
                }

                if (request.Reason is DdgiClipmapDirtyReason.InitialActivation or
                    DdgiClipmapDirtyReason.LayoutChange or
                    DdgiClipmapDirtyReason.Teleport or
                    DdgiClipmapDirtyReason.LargeScroll)
                {
                    reasonFlags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonTeleportWarmupFlag;
                }

                AddLogicalCellRange(
                    volumes,
                    volume,
                    volumeIndex,
                    request.MinCell,
                    request.MaxCell,
                    activeProbeCount,
                    reasonFlags,
                    priority,
                    destination,
                    probeMarks,
                    priorityLimit,
                    primaryRayBudget,
                    ref primaryRaysUsed,
                    ref count);
            }
        }

        private static IReadOnlyList<DdgiDirtyRegion>? ResolveDirtyRegions(
            DdgiFrameLayout? layout,
            IReadOnlyList<BoundingBox>? dirtyBounds)
        {
            if (layout?.DirtyRegions.Count > 0)
                return layout.DirtyRegions;
            if (dirtyBounds == null || dirtyBounds.Count == 0)
                return null;

            var regions = new DdgiDirtyRegion[dirtyBounds.Count];
            for (int i = 0; i < dirtyBounds.Count; i++)
                regions[i] = new DdgiDirtyRegion(dirtyBounds[i], DdgiDirtyReason.Unknown);
            return regions;
        }

        private static void AddDirtyRegionRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            int activeProbeCount,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int priorityLimit,
            ulong primaryRayBudget,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            if (dirtyRegions == null || dirtyRegions.Count == 0 || count >= priorityLimit || primaryRaysUsed >= primaryRayBudget)
                return;

            for (int dirtyIndex = 0; dirtyIndex < dirtyRegions.Count && count < priorityLimit && primaryRaysUsed < primaryRayBudget; dirtyIndex++)
            {
                DdgiDirtyRegion dirty = dirtyRegions[dirtyIndex];
                for (int volumeIndex = 0; volumeIndex < volumes.Length && count < priorityLimit && primaryRaysUsed < primaryRayBudget; volumeIndex++)
                {
                    GPUDdgiProbeVolume volume = volumes[volumeIndex];
                    if (!VolumeIntersectsDirtyBounds(volume, dirty.Bounds))
                        continue;

                    if (IsCameraRelative(volume))
                    {
                        continue;
                    }

                    AddAuthoredDirtyRequest(
                        volumes,
                        volume,
                        volumeIndex,
                        dirty.Bounds.Center,
                        DdgiDirtyReasonPolicy.ToProbeUpdateFlags(dirty.Reason),
                        DdgiDirtyReasonPolicy.ResolvePriority(dirty.Reason),
                        activeProbeCount,
                        destination,
                        probeMarks,
                        priorityLimit,
                        primaryRayBudget,
                        ref primaryRaysUsed,
                        ref count);
                }
            }
        }

        private static void AddFrustumFocusedRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            DdgiViewPriorityContext viewPriority,
            int activeProbeCount,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int priorityLimit,
            ulong primaryRayBudget,
            GlobalIlluminationSettings settings,
            DdgiProbeUpdateSchedulerScratch? scratch,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            DdgiCpuSchedulerInstrumentation? instrumentation,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            if (!viewPriority.Enabled || count >= priorityLimit || activeProbeCount <= 0 || primaryRaysUsed >= primaryRayBudget)
                return;

            int queueCapacity = Math.Min(activeProbeCount, Math.Max(0, priorityLimit - count));
            if (queueCapacity <= 0)
                return;

            Span<DdgiProbeUpdateCandidate> candidates = queueCapacity <= StackCandidateQueueLimit
                ? stackalloc DdgiProbeUpdateCandidate[queueCapacity]
                : GetCandidateQueue(scratch, DdgiSchedulerQueueKind.VisibleNear, queueCapacity);
            int candidateCount = 0;
            for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
            {
                GPUDdgiProbeVolume volume = volumes[volumeIndex];
                int firstProbe = FirstProbeIndex(volume);
                int countX = CountX(volume);
                int countY = CountY(volume);
                int countZ = CountZ(volume);
                int probeCount = checked(countX * countY * countZ);
                int endProbe = Math.Min(activeProbeCount, firstProbe + probeCount);

                if (IsCameraRelative(volume) &&
                    TryBuildCameraRelativeViewCellRange(volume, viewPriority, dirtyRegions, includeSafetyShell: false, out DdgiLogicalCellRange range))
                {
                    AddCameraRelativeViewCandidates(
                        volume,
                        volumeIndex,
                        activeProbeCount,
                        range,
                        layout,
                        dirtyRegions,
                        viewPriority,
                        settings,
                        probeFeedback,
                        includeSafetyShell: false,
                        candidates,
                        ref candidateCount,
                        probeMarks,
                        instrumentation);
                    continue;
                }

                for (int probeIndex = firstProbe; probeIndex < endProbe; probeIndex++)
                {
                    instrumentation?.RecordViewCandidateProbeEvaluation();
                    TryAddViewCandidate(
                        volume,
                        volumeIndex,
                        probeIndex,
                        layout,
                        dirtyRegions,
                        viewPriority,
                        settings,
                        probeFeedback,
                        includeSafetyShell: false,
                        candidates,
                        ref candidateCount,
                        probeMarks,
                        instrumentation);
                }
            }

            for (int i = 0; i < candidateCount && count < priorityLimit && primaryRaysUsed < primaryRayBudget; i++)
                AddRequest(candidates[i].Request, volumes, destination, probeMarks, primaryRayBudget, ref primaryRaysUsed, ref count);
        }

        private static int AddSafetyShellRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            DdgiViewPriorityContext viewPriority,
            int activeProbeCount,
            int cursor,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int targetCount,
            ulong primaryRayBudget,
            uint reasonFlags,
            uint priority,
            DdgiProbeUpdateSchedulerScratch? scratch,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            DdgiCpuSchedulerInstrumentation? instrumentation,
            ref ulong primaryRaysUsed,
            ref int count,
            ref int addedCount)
        {
            if (!viewPriority.Enabled || count >= targetCount || activeProbeCount <= 0 || primaryRaysUsed >= primaryRayBudget)
            {
                return AddRoundRobinRequests(
                    volumes,
                    activeProbeCount,
                    cursor,
                    destination,
                    probeMarks,
                    targetCount,
                    primaryRayBudget,
                    reasonFlags,
                    priority,
                    ref primaryRaysUsed,
                    ref count,
                    ref addedCount);
            }

            int queueCapacity = Math.Min(activeProbeCount, Math.Max(0, targetCount - count));
            if (queueCapacity <= 0)
                return cursor;

            Span<DdgiProbeUpdateCandidate> candidates = queueCapacity <= StackCandidateQueueLimit
                ? stackalloc DdgiProbeUpdateCandidate[queueCapacity]
                : GetCandidateQueue(scratch, DdgiSchedulerQueueKind.Safety, queueCapacity);
            int candidateCount = 0;
            for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
            {
                GPUDdgiProbeVolume volume = volumes[volumeIndex];
                int firstProbe = FirstProbeIndex(volume);
                int countX = CountX(volume);
                int countY = CountY(volume);
                int countZ = CountZ(volume);
                int probeCount = checked(countX * countY * countZ);
                int endProbe = Math.Min(activeProbeCount, firstProbe + probeCount);

                if (IsCameraRelative(volume) &&
                    TryBuildCameraRelativeViewCellRange(volume, viewPriority, dirtyRegions: null, includeSafetyShell: true, out DdgiLogicalCellRange range))
                {
                    AddCameraRelativeViewCandidates(
                        volume,
                        volumeIndex,
                        activeProbeCount,
                        range,
                        layout,
                        dirtyRegions: null,
                        viewPriority,
                        settings: null,
                        probeFeedback,
                        includeSafetyShell: true,
                        candidates,
                        ref candidateCount,
                        probeMarks,
                        instrumentation,
                        reasonFlags,
                        priority);
                    continue;
                }

                for (int probeIndex = firstProbe; probeIndex < endProbe; probeIndex++)
                {
                    instrumentation?.RecordViewCandidateProbeEvaluation();
                    TryAddViewCandidate(
                        volume,
                        volumeIndex,
                        probeIndex,
                        layout,
                        dirtyRegions: null,
                        viewPriority,
                        settings: null,
                        probeFeedback,
                        includeSafetyShell: true,
                        candidates,
                        ref candidateCount,
                        probeMarks,
                        instrumentation,
                        reasonFlags,
                        priority);
                }
            }

            for (int i = 0; i < candidateCount && count < targetCount && primaryRaysUsed < primaryRayBudget; i++)
            {
                if (AddRequest(candidates[i].Request, volumes, destination, probeMarks, primaryRayBudget, ref primaryRaysUsed, ref count))
                    addedCount++;
            }

            if (count >= targetCount)
                return cursor;

            return AddRoundRobinRequests(
                volumes,
                activeProbeCount,
                cursor,
                destination,
                probeMarks,
                targetCount,
                primaryRayBudget,
                reasonFlags,
                priority,
                ref primaryRaysUsed,
                ref count,
                ref addedCount);
        }

        private static int AddRoundRobinRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int activeProbeCount,
            int cursor,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int targetCount,
            ulong primaryRayBudget,
            uint reasonFlags,
            uint priority,
            ref ulong primaryRaysUsed,
            ref int count,
            ref int addedCount)
        {
            int attempts = 0;
            int nextCursor = cursor;
            while (count < targetCount && attempts < activeProbeCount && primaryRaysUsed < primaryRayBudget)
            {
                int probeIndex = (cursor + attempts) % activeProbeCount;
                attempts++;
                nextCursor = (probeIndex + 1) % activeProbeCount;
                if (!TryCreateRequestForPhysicalProbe(volumes, activeProbeCount, probeIndex, reasonFlags, priority, out GPUDdgiProbeUpdateRequest request))
                    continue;
                if (AddRequest(request, volumes, destination, probeMarks, primaryRayBudget, ref primaryRaysUsed, ref count))
                    addedCount++;
            }

            return nextCursor;
        }

        private static void AddCameraRelativeViewCandidates(
            in GPUDdgiProbeVolume volume,
            int volumeIndex,
            int activeProbeCount,
            DdgiLogicalCellRange range,
            DdgiFrameLayout? layout,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            DdgiViewPriorityContext viewPriority,
            GlobalIlluminationSettings? settings,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            bool includeSafetyShell,
            Span<DdgiProbeUpdateCandidate> candidates,
            ref int candidateCount,
            Span<byte> probeMarks,
            DdgiCpuSchedulerInstrumentation? instrumentation,
            uint overrideReasonFlags = 0u,
            uint overridePriority = 0u)
        {
            int firstProbe = FirstProbeIndex(volume);
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            DdgiClipmapCell gridMin = GridMinCell(volume);
            DdgiClipmapCell ringOffset = RingOffsetCell(volume);

            for (long z = range.Min.Z; z <= range.Max.Z; z++)
            {
                for (long y = range.Min.Y; y <= range.Max.Y; y++)
                {
                    for (long x = range.Min.X; x <= range.Max.X; x++)
                    {
                        DdgiClipmapCell logicalCell = new((int)x, (int)y, (int)z);
                        int probeIndex = DdgiClipmapAddressing.CalculatePhysicalProbeIndex(
                            logicalCell,
                            gridMin,
                            ringOffset,
                            countX,
                            countY,
                            countZ,
                            firstProbe);
                        if ((uint)probeIndex >= (uint)activeProbeCount)
                            continue;

                        instrumentation?.RecordViewCandidateProbeEvaluation();
                        TryAddViewCandidate(
                            volume,
                            volumeIndex,
                            probeIndex,
                            layout,
                            dirtyRegions,
                            viewPriority,
                            settings,
                            probeFeedback,
                            includeSafetyShell,
                            candidates,
                            ref candidateCount,
                            probeMarks,
                            instrumentation,
                            overrideReasonFlags,
                            overridePriority);
                    }
                }
            }
        }

        private static void TryAddViewCandidate(
            in GPUDdgiProbeVolume volume,
            int volumeIndex,
            int probeIndex,
            DdgiFrameLayout? layout,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            DdgiViewPriorityContext viewPriority,
            GlobalIlluminationSettings? settings,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            bool includeSafetyShell,
            Span<DdgiProbeUpdateCandidate> candidates,
            ref int candidateCount,
            Span<byte> probeMarks,
            DdgiCpuSchedulerInstrumentation? instrumentation,
            uint overrideReasonFlags = 0u,
            uint overridePriority = 0u)
        {
            if ((uint)probeIndex >= (uint)probeMarks.Length || probeMarks[probeIndex] != 0)
                return;

            if (!TryCreateViewCandidate(
                volume,
                volumeIndex,
                probeIndex,
                layout,
                dirtyRegions,
                viewPriority,
                settings,
                probeFeedback,
                includeSafetyShell,
                out DdgiProbeUpdateCandidate candidate))
            {
                return;
            }

            if (overrideReasonFlags != 0u)
                candidate.Request.Flags |= overrideReasonFlags;
            if (overridePriority != 0u)
                candidate.Request.Priority = overridePriority;
            AddCandidateToBoundedQueue(candidates, ref candidateCount, candidate, instrumentation);
        }

        private static bool TryBuildCameraRelativeViewCellRange(
            in GPUDdgiProbeVolume volume,
            DdgiViewPriorityContext viewPriority,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            bool includeSafetyShell,
            out DdgiLogicalCellRange range)
        {
            Vector3 spacing = Spacing(volume);
            if (!IsUsableSpacing(spacing.X) || !IsUsableSpacing(spacing.Y) || !IsUsableSpacing(spacing.Z))
            {
                range = default;
                return false;
            }

            float probeSpacing = MathF.Max(MathF.Max(MathF.Abs(spacing.X), MathF.Abs(spacing.Y)), MathF.Abs(spacing.Z));
            BoundingBox bounds = includeSafetyShell
                ? BuildSafetyShellBounds(volume, viewPriority, probeSpacing)
                : BuildViewPyramidBounds(viewPriority, viewPriority.GuardBandWorldUnits + probeSpacing);

            if (!includeSafetyShell &&
                viewPriority.CameraVelocity.LengthSquared() > probeSpacing * probeSpacing * 0.0625f)
            {
                DdgiViewPriorityContext predictedView = viewPriority with
                {
                    CameraPosition = viewPriority.CameraPosition + viewPriority.CameraVelocity
                };
                bounds = Union(
                    bounds,
                    BuildViewPyramidBounds(predictedView, viewPriority.GuardBandWorldUnits + probeSpacing));
            }

            if (!includeSafetyShell && dirtyRegions != null)
            {
                float dirtyPadding = probeSpacing * 0.75f;
                Vector3 padding = new(dirtyPadding);
                for (int i = 0; i < dirtyRegions.Count; i++)
                {
                    BoundingBox dirtyBounds = dirtyRegions[i].Bounds;
                    bounds = Union(bounds, new BoundingBox(dirtyBounds.Min - padding, dirtyBounds.Max + padding));
                }
            }

            range = BuildLogicalCellRange(bounds, spacing, GridMinCell(volume), CountX(volume), CountY(volume), CountZ(volume));
            return true;
        }

        private static BoundingBox BuildSafetyShellBounds(
            in GPUDdgiProbeVolume volume,
            DdgiViewPriorityContext viewPriority,
            float probeSpacing)
        {
            float radius = MathF.Max(
                viewPriority.SafetyRadius,
                probeSpacing * MathF.Max(CountX(volume), CountZ(volume)) * 0.5f);
            Vector3 r = new(MathF.Max(0.0f, radius));
            return new BoundingBox(viewPriority.CameraPosition - r, viewPriority.CameraPosition + r);
        }

        private static BoundingBox BuildViewPyramidBounds(
            DdgiViewPriorityContext viewPriority,
            float guardBandWorldUnits)
        {
            float nearGuard = MathF.Min(guardBandWorldUnits, viewPriority.NearPlane * 0.5f);
            float near = MathF.Max(0.0f, viewPriority.NearPlane - nearGuard);
            float far = MathF.Max(near, viewPriority.FarPlane + guardBandWorldUnits);
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            IncludeViewPlaneCorners(viewPriority, guardBandWorldUnits, near, ref min, ref max);
            IncludeViewPlaneCorners(viewPriority, guardBandWorldUnits, far, ref min, ref max);
            return new BoundingBox(min, max);
        }

        private static void IncludeViewPlaneCorners(
            DdgiViewPriorityContext viewPriority,
            float guardBandWorldUnits,
            float forwardDistance,
            ref Vector3 min,
            ref Vector3 max)
        {
            float positiveForward = MathF.Max(forwardDistance, 0.0f);
            float extentX = positiveForward * viewPriority.TanHalfFovX + guardBandWorldUnits;
            float extentY = positiveForward * viewPriority.TanHalfFovY + guardBandWorldUnits;
            Vector3 center = viewPriority.CameraPosition + viewPriority.Forward * forwardDistance;

            for (int ySign = -1; ySign <= 1; ySign += 2)
            {
                for (int xSign = -1; xSign <= 1; xSign += 2)
                {
                    Vector3 corner = center +
                        viewPriority.Right * (extentX * xSign) +
                        viewPriority.Up * (extentY * ySign);
                    min = Vector3.Min(min, corner);
                    max = Vector3.Max(max, corner);
                }
            }
        }

        private static DdgiLogicalCellRange BuildLogicalCellRange(
            BoundingBox bounds,
            Vector3 spacing,
            DdgiClipmapCell gridMin,
            int countX,
            int countY,
            int countZ)
        {
            DdgiClipmapCell min = ClampToGrid(
                new DdgiClipmapCell(
                    SubtractCellPadding(FloorToCell(bounds.Min.X, spacing.X)),
                    SubtractCellPadding(FloorToCell(bounds.Min.Y, spacing.Y)),
                    SubtractCellPadding(FloorToCell(bounds.Min.Z, spacing.Z))),
                gridMin,
                countX,
                countY,
                countZ);
            DdgiClipmapCell max = ClampToGrid(
                new DdgiClipmapCell(
                    AddCellPadding(CeilingToCell(bounds.Max.X, spacing.X)),
                    AddCellPadding(CeilingToCell(bounds.Max.Y, spacing.Y)),
                    AddCellPadding(CeilingToCell(bounds.Max.Z, spacing.Z))),
                gridMin,
                countX,
                countY,
                countZ);
            return new DdgiLogicalCellRange(Min(min, max), Max(min, max));
        }

        private static BoundingBox Union(BoundingBox left, BoundingBox right) =>
            new(Vector3.Min(left.Min, right.Min), Vector3.Max(left.Max, right.Max));

        private static bool IsUsableSpacing(float spacing) =>
            float.IsFinite(spacing) && MathF.Abs(spacing) > 0.000001f;

        private static int AddCellPadding(int cell) => cell == int.MaxValue ? int.MaxValue : cell + 1;

        private static int SubtractCellPadding(int cell) => cell == int.MinValue ? int.MinValue : cell - 1;

        private static int AddUninitializedClipmapRequests(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayout? layout,
            int activeProbeCount,
            int cursor,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int limit,
            ulong primaryRayBudget,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            if (layout == null || layout.CameraRelativeCascades.Count == 0 || activeProbeCount <= 0 || count >= limit || primaryRaysUsed >= primaryRayBudget)
                return NormalizeCursor(cursor, activeProbeCount);

            int normalizedCursor = NormalizeCursor(cursor, activeProbeCount);
            int nextCursor = normalizedCursor;
            for (int scanned = 0; scanned < activeProbeCount && count < limit && primaryRaysUsed < primaryRayBudget; scanned++)
            {
                int probeIndex = (normalizedCursor + scanned) % activeProbeCount;
                nextCursor = (probeIndex + 1) % activeProbeCount;
                if ((uint)probeIndex >= (uint)probeMarks.Length || probeMarks[probeIndex] != 0)
                    continue;
                if (!TryCreateRequestForPhysicalProbe(
                        volumes,
                        activeProbeCount,
                        probeIndex,
                        GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag,
                        PriorityNewCell,
                        out GPUDdgiProbeUpdateRequest request))
                {
                    continue;
                }

                if (request.VolumeIndex >= (uint)volumes.Length)
                    continue;

                GPUDdgiProbeVolume volume = volumes[(int)request.VolumeIndex];
                if (!IsCameraRelative(volume))
                    continue;

                var logicalCell = new DdgiClipmapCell(request.LogicalCellX, request.LogicalCellY, request.LogicalCellZ);
                if (!TryGetClipmapCellState(layout, volume, logicalCell, out DdgiClipmapCellState cellState) ||
                    cellState.Initialized)
                {
                    continue;
                }

                AddRequest(
                    request,
                    volumes,
                    destination,
                    probeMarks,
                    primaryRayBudget,
                    ref primaryRaysUsed,
                    ref count);
            }

            return nextCursor;
        }

        private static void AddLogicalCellRange(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            in GPUDdgiProbeVolume volume,
            int volumeIndex,
            DdgiClipmapCell minCell,
            DdgiClipmapCell maxCell,
            int activeProbeCount,
            uint reasonFlags,
            uint priority,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int limit,
            ulong primaryRayBudget,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            int firstProbe = FirstProbeIndex(volume);
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            DdgiClipmapCell gridMin = GridMinCell(volume);
            DdgiClipmapCell ringOffset = RingOffsetCell(volume);

            DdgiClipmapCell clampedMin = ClampToGrid(minCell, gridMin, countX, countY, countZ);
            DdgiClipmapCell clampedMax = ClampToGrid(maxCell, gridMin, countX, countY, countZ);
            DdgiClipmapCell orderedMin = Min(clampedMin, clampedMax);
            DdgiClipmapCell orderedMax = Max(clampedMin, clampedMax);
            clampedMin = orderedMin;
            clampedMax = orderedMax;

            int extentX = clampedMax.X - clampedMin.X + 1;
            int extentY = clampedMax.Y - clampedMin.Y + 1;
            int extentZ = clampedMax.Z - clampedMin.Z + 1;
            int totalCells = checked(extentX * extentY * extentZ);
            int stride = CalculateCoprimeStride(totalCells);

            for (int ordinal = 0; ordinal < totalCells && count < limit && primaryRaysUsed < primaryRayBudget; ordinal++)
            {
                int linear = totalCells <= 1 ? 0 : (int)(((long)ordinal * stride) % totalCells);
                int localX = linear % extentX;
                int localY = (linear / extentX) % extentY;
                int localZ = linear / (extentX * extentY);
                DdgiClipmapCell logicalCell = new(
                    clampedMin.X + localX,
                    clampedMin.Y + localY,
                    clampedMin.Z + localZ);
                int probeIndex = DdgiClipmapAddressing.CalculatePhysicalProbeIndex(
                    logicalCell,
                    gridMin,
                    ringOffset,
                    countX,
                    countY,
                    countZ,
                    firstProbe);
                if ((uint)probeIndex >= (uint)activeProbeCount)
                    continue;

                AddRequest(
                    new GPUDdgiProbeUpdateRequest
                    {
                        ProbeIndex = checked((uint)probeIndex),
                        VolumeIndex = checked((uint)volumeIndex),
                        Flags = reasonFlags,
                        Priority = priority,
                        LogicalCellX = logicalCell.X,
                        LogicalCellY = logicalCell.Y,
                        LogicalCellZ = logicalCell.Z
                    },
                    volumes,
                    destination,
                    probeMarks,
                    primaryRayBudget,
                    ref primaryRaysUsed,
                    ref count);
            }
        }

        private static int CalculateCoprimeStride(int totalCells)
        {
            if (totalCells <= 1)
                return 1;

            int stride = Math.Max(1, totalCells / 2);
            if ((stride & 1) == 0)
                stride++;

            while (stride < totalCells && GreatestCommonDivisor(stride, totalCells) != 1)
                stride += 2;

            return stride < totalCells ? stride : 1;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int remainder = a % b;
                a = b;
                b = remainder;
            }

            return a;
        }

        private static void AddAuthoredDirtyRequest(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            in GPUDdgiProbeVolume volume,
            int volumeIndex,
            Vector3 dirtyCenter,
            uint reasonFlags,
            uint priority,
            int activeProbeCount,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            int limit,
            ulong primaryRayBudget,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            if (count >= limit || primaryRaysUsed >= primaryRayBudget)
                return;

            Vector3 origin = Origin(volume);
            Vector3 spacing = Spacing(volume);
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            int x = spacing.X > 0.0f ? Math.Clamp((int)MathF.Round((dirtyCenter.X - origin.X) / spacing.X), 0, countX - 1) : 0;
            int y = spacing.Y > 0.0f ? Math.Clamp((int)MathF.Round((dirtyCenter.Y - origin.Y) / spacing.Y), 0, countY - 1) : 0;
            int z = spacing.Z > 0.0f ? Math.Clamp((int)MathF.Round((dirtyCenter.Z - origin.Z) / spacing.Z), 0, countZ - 1) : 0;
            int probeIndex = FirstProbeIndex(volume) + x + y * countX + z * countX * countY;
            if ((uint)probeIndex >= (uint)activeProbeCount)
                return;

            AddRequest(
                new GPUDdgiProbeUpdateRequest
                {
                    ProbeIndex = checked((uint)probeIndex),
                    VolumeIndex = checked((uint)volumeIndex),
                    Flags = reasonFlags |
                        GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAuthoredVolumeFlag,
                    Priority = priority,
                    LogicalCellX = x,
                    LogicalCellY = y,
                    LogicalCellZ = z
                },
                volumes,
                destination,
                probeMarks,
                primaryRayBudget,
                ref primaryRaysUsed,
                ref count);
        }

        private static bool TryCreateRequestForPhysicalProbe(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            int activeProbeCount,
            int probeIndex,
            uint reasonFlags,
            uint priority,
            out GPUDdgiProbeUpdateRequest request)
        {
            for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
            {
                GPUDdgiProbeVolume volume = volumes[volumeIndex];
                int firstProbe = FirstProbeIndex(volume);
                int countX = CountX(volume);
                int countY = CountY(volume);
                int countZ = CountZ(volume);
                int probeCount = checked(countX * countY * countZ);
                if (probeIndex < firstProbe || probeIndex >= firstProbe + probeCount || probeIndex >= activeProbeCount)
                    continue;

                int local = probeIndex - firstProbe;
                int wrappedX = local % countX;
                int wrappedY = (local / countX) % countY;
                int wrappedZ = local / (countX * countY);
                uint flags = reasonFlags;
                DdgiClipmapCell logicalCell;
                if (IsCameraRelative(volume))
                {
                    DdgiClipmapCell gridMin = GridMinCell(volume);
                    DdgiClipmapCell ringOffset = RingOffsetCell(volume);
                    logicalCell = DdgiClipmapAddressing.DecodeLogicalCellFromPhysicalProbeIndex(
                        probeIndex,
                        gridMin,
                        ringOffset,
                        countX,
                        countY,
                        countZ,
                        firstProbe);
                    flags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag;
                }
                else
                {
                    logicalCell = new DdgiClipmapCell(wrappedX, wrappedY, wrappedZ);
                    flags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAuthoredVolumeFlag;
                }

                request = new GPUDdgiProbeUpdateRequest
                {
                    ProbeIndex = checked((uint)probeIndex),
                    VolumeIndex = checked((uint)volumeIndex),
                    Flags = flags,
                    Priority = priority,
                    LogicalCellX = logicalCell.X,
                    LogicalCellY = logicalCell.Y,
                    LogicalCellZ = logicalCell.Z
                };
                return true;
            }

            request = default;
            return false;
        }

        private static bool TryCreateViewCandidate(
            in GPUDdgiProbeVolume volume,
            int volumeIndex,
            int probeIndex,
            DdgiFrameLayout? layout,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            DdgiViewPriorityContext viewPriority,
            GlobalIlluminationSettings? settings,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback,
            bool includeSafetyShell,
            out DdgiProbeUpdateCandidate candidate)
        {
            int firstProbe = FirstProbeIndex(volume);
            int countX = CountX(volume);
            int countY = CountY(volume);
            int countZ = CountZ(volume);
            int local = probeIndex - firstProbe;
            if ((uint)local >= (uint)checked(countX * countY * countZ))
            {
                candidate = default;
                return false;
            }

            int wrappedX = local % countX;
            int wrappedY = (local / countX) % countY;
            int wrappedZ = local / (countX * countY);
            bool cameraRelative = IsCameraRelative(volume);
            DdgiClipmapCell logicalCell;
            if (cameraRelative)
            {
                logicalCell = DdgiClipmapAddressing.DecodeLogicalCellFromPhysicalProbeIndex(
                    probeIndex,
                    GridMinCell(volume),
                    RingOffsetCell(volume),
                    countX,
                    countY,
                    countZ,
                    firstProbe);
            }
            else
            {
                logicalCell = new DdgiClipmapCell(wrappedX, wrappedY, wrappedZ);
            }

            Vector3 spacing = Spacing(volume);
            float probeSpacing = MathF.Max(MathF.Max(MathF.Abs(spacing.X), MathF.Abs(spacing.Y)), MathF.Abs(spacing.Z));
            if (!float.IsFinite(probeSpacing) || probeSpacing <= 0.0f)
                probeSpacing = 1.0f;

            Vector3 probePosition = cameraRelative
                ? new Vector3(logicalCell.X * spacing.X, logicalCell.Y * spacing.Y, logicalCell.Z * spacing.Z)
                : Origin(volume) + new Vector3(wrappedX * spacing.X, wrappedY * spacing.Y, wrappedZ * spacing.Z);
            bool intersectsDirtyBounds = TryResolveIntersectingDirtyRegion(probePosition, probeSpacing, dirtyRegions, out DdgiDirtyRegion dirtyRegion);
            DdgiProbeViewMetrics metrics = EvaluateProbeViewMetrics(probePosition, probeSpacing, countX, countZ, viewPriority);

            bool eligible = metrics.InCurrentFrustum ||
                metrics.InExpandedFrustum ||
                metrics.InPredictedFrustum ||
                intersectsDirtyBounds;
            if (includeSafetyShell)
                eligible = metrics.InSafetyShell;
            if (!eligible)
            {
                candidate = default;
                return false;
            }

            uint flags = cameraRelative
                ? GlobalIlluminationProbeVolumeData.ProbeUpdateReasonCameraRelativeFlag
                : GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAuthoredVolumeFlag;
            uint priority = PriorityFarCascade;
            if (metrics.InCurrentFrustum)
            {
                flags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag;
                priority = PriorityVisibleFrustum;
            }
            else if (metrics.InExpandedFrustum || metrics.InPredictedFrustum)
            {
                flags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag;
                priority = PriorityNearCamera;
            }
            else if (metrics.InSafetyShell)
            {
                flags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag;
                priority = PriorityOutsideFrustumSafety;
            }

            if (intersectsDirtyBounds)
                flags |= DdgiDirtyReasonPolicy.ToProbeUpdateFlags(dirtyRegion.Reason);

            ulong ageFrames = 0UL;
            bool initialized = true;
            if (cameraRelative && TryGetClipmapCellState(layout, volume, logicalCell, out DdgiClipmapCellState cellState))
            {
                initialized = cellState.Initialized;
                ageFrames = cellState.AgeFrames;
                if (ageFrames > 0UL)
                    flags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag;
                if (!initialized)
                    flags |= GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag;
            }
            DdgiProbeSchedulerFeedback feedback = ResolveProbeFeedback(probeIndex, ageFrames, probeFeedback);

            bool fastCameraMovement = layout?.FastCameraMovement == true;
            int cascadeIndex = CascadeIndex(volume);
            if (settings != null &&
                fastCameraMovement &&
                cascadeIndex > 0 &&
                initialized &&
                !metrics.InCurrentFrustum &&
                !intersectsDirtyBounds)
            {
                candidate = default;
                return false;
            }

            float score = ScoreViewCandidate(
                metrics,
                intersectsDirtyBounds,
                initialized,
                ageFrames,
                cascadeIndex,
                settings,
                probeSpacing,
                fastCameraMovement,
                feedback);

            candidate = new DdgiProbeUpdateCandidate(
                score,
                probeIndex,
                new GPUDdgiProbeUpdateRequest
                {
                    ProbeIndex = checked((uint)probeIndex),
                    VolumeIndex = checked((uint)volumeIndex),
                    Flags = flags,
                    Priority = priority,
                    LogicalCellX = logicalCell.X,
                    LogicalCellY = logicalCell.Y,
                    LogicalCellZ = logicalCell.Z
                });
            return true;
        }

        private static DdgiProbeViewMetrics EvaluateProbeViewMetrics(
            Vector3 probePosition,
            float probeSpacing,
            int countX,
            int countZ,
            DdgiViewPriorityContext viewPriority)
        {
            Vector3 toProbe = probePosition - viewPriority.CameraPosition;
            float distance = toProbe.Length();
            float forwardDistance = Vector3.Dot(toProbe, viewPriority.Forward);
            bool current = IsInsideViewPyramid(toProbe, viewPriority, guardBandWorldUnits: 0.0f);
            bool expanded = IsInsideViewPyramid(toProbe, viewPriority, viewPriority.GuardBandWorldUnits + probeSpacing);

            bool predicted = false;
            if (viewPriority.CameraVelocity.LengthSquared() > probeSpacing * probeSpacing * 0.0625f)
            {
                Vector3 predictedToProbe = probePosition - (viewPriority.CameraPosition + viewPriority.CameraVelocity);
                predicted = IsInsideViewPyramid(predictedToProbe, viewPriority, viewPriority.GuardBandWorldUnits + probeSpacing);
            }

            float volumeSafetyRadius = MathF.Max(
                viewPriority.SafetyRadius,
                probeSpacing * MathF.Max(countX, countZ) * 0.5f);
            bool sideOrRear = !current &&
                distance <= volumeSafetyRadius &&
                (forwardDistance < viewPriority.NearPlane || !expanded);

            return new DdgiProbeViewMetrics(current, expanded, predicted, sideOrRear, distance);
        }

        private static bool IsInsideViewPyramid(
            Vector3 toProbe,
            DdgiViewPriorityContext viewPriority,
            float guardBandWorldUnits)
        {
            float forwardDistance = Vector3.Dot(toProbe, viewPriority.Forward);
            float nearGuard = MathF.Min(guardBandWorldUnits, viewPriority.NearPlane * 0.5f);
            if (forwardDistance < viewPriority.NearPlane - nearGuard ||
                forwardDistance > viewPriority.FarPlane + guardBandWorldUnits)
            {
                return false;
            }

            float positiveForward = MathF.Max(forwardDistance, 0.0f);
            float lateralX = MathF.Abs(Vector3.Dot(toProbe, viewPriority.Right));
            float lateralY = MathF.Abs(Vector3.Dot(toProbe, viewPriority.Up));
            return lateralX <= positiveForward * viewPriority.TanHalfFovX + guardBandWorldUnits &&
                   lateralY <= positiveForward * viewPriority.TanHalfFovY + guardBandWorldUnits;
        }

        private static float ScoreViewCandidate(
            DdgiProbeViewMetrics metrics,
            bool intersectsDirtyBounds,
            bool initialized,
            ulong ageFrames,
            int cascadeIndex,
            GlobalIlluminationSettings? settings,
            float probeSpacing,
            bool fastCameraMovement,
            DdgiProbeSchedulerFeedback feedback)
        {
            float weight = settings?.DdgiFrustumPriorityWeight ?? 1.0f;
            float score = 100_000.0f;
            if (metrics.InCurrentFrustum)
                score -= 70_000.0f * weight;
            else if (metrics.InExpandedFrustum)
                score -= 52_000.0f * weight;
            else if (metrics.InPredictedFrustum)
                score -= 44_000.0f * weight;
            else if (metrics.InSafetyShell)
                score -= 18_000.0f;

            if (intersectsDirtyBounds)
                score -= 20_000.0f;

            if (!initialized)
                score -= (settings?.DdgiNewProbeUpdateBoost ?? 1.0f) * (fastCameraMovement ? 2_000.0f : 1_000.0f);

            if (ageFrames != ulong.MaxValue)
                score -= (float)Math.Min(ageFrames, 512UL) * 6.0f;
            else
                score -= 4_096.0f;

            score += Math.Max(0, cascadeIndex) * 2_500.0f;
            if (fastCameraMovement)
                score += Math.Max(0, cascadeIndex) * 7_500.0f;

            ApplyProbeFeedbackScore(
                feedback,
                settings,
                initialized,
                cascadeIndex,
                fastCameraMovement,
                ref score);
            score += metrics.DistanceToCamera / MathF.Max(probeSpacing, 0.001f);
            return score;
        }

        private static DdgiProbeSchedulerFeedback ResolveProbeFeedback(
            int probeIndex,
            ulong clipmapAgeFrames,
            ReadOnlySpan<DdgiProbeSchedulerFeedback> probeFeedback)
        {
            if ((uint)probeIndex >= (uint)probeFeedback.Length)
                return default;

            DdgiProbeSchedulerFeedback feedback = probeFeedback[probeIndex];
            if (clipmapAgeFrames != 0UL && clipmapAgeFrames != ulong.MaxValue)
                feedback.AgeFrames = (uint)Math.Min(uint.MaxValue, Math.Max(clipmapAgeFrames, feedback.AgeFrames));
            return feedback;
        }

        private static void ApplyProbeFeedbackScore(
            in DdgiProbeSchedulerFeedback feedback,
            GlobalIlluminationSettings? settings,
            bool initialized,
            int cascadeIndex,
            bool fastCameraMovement,
            ref float score)
        {
            if (!feedback.HasSample)
                return;

            float variability = feedback.VariabilityScore;
            float confidenceDeficit = 1.0f - feedback.CombinedConfidence;
            score -= variability * 18_000.0f;
            score -= confidenceDeficit * 8_000.0f;

            if (feedback.IsLowConfidence)
                score -= 5_500.0f;

            uint minimumRefreshFrames = (uint)Math.Max(1, settings?.DdgiMinimumProbeRefreshFrames ?? 1);
            uint stableRefreshFrames = minimumRefreshFrames * (uint)Math.Clamp(cascadeIndex + 1, 1, 8);
            if (feedback.IsStable && initialized && feedback.AgeFrames < stableRefreshFrames)
            {
                float stablePenalty = 9_000.0f + Math.Max(0, cascadeIndex) * 4_000.0f;
                if (fastCameraMovement && cascadeIndex > 0)
                    stablePenalty += 6_000.0f;
                score += stablePenalty;
            }
        }

        private static bool TryResolveIntersectingDirtyRegion(
            Vector3 probePosition,
            float probeSpacing,
            IReadOnlyList<DdgiDirtyRegion>? dirtyRegions,
            out DdgiDirtyRegion dirtyRegion)
        {
            dirtyRegion = default;
            if (dirtyRegions == null || dirtyRegions.Count == 0)
                return false;

            var sphere = new BoundingSphere(probePosition, probeSpacing * 0.75f);
            for (int i = 0; i < dirtyRegions.Count; i++)
            {
                DdgiDirtyRegion candidate = dirtyRegions[i];
                if (sphere.Intersects(candidate.Bounds) || candidate.Bounds.Contains(probePosition))
                {
                    dirtyRegion = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetClipmapCellState(
            DdgiFrameLayout? layout,
            in GPUDdgiProbeVolume volume,
            DdgiClipmapCell logicalCell,
            out DdgiClipmapCellState cellState)
        {
            if (layout == null)
            {
                cellState = default;
                return false;
            }

            int cascadeIndex = CascadeIndex(volume);
            IReadOnlyList<DdgiClipmapCascadeState> cascades = layout.CameraRelativeCascades;
            for (int i = 0; i < cascades.Count; i++)
            {
                DdgiClipmapCascadeState cascade = cascades[i];
                if (cascade.CascadeIndex != cascadeIndex || !cascade.ContainsLogicalCell(logicalCell))
                    continue;

                cellState = cascade.GetCellState(logicalCell);
                return true;
            }

            cellState = default;
            return false;
        }

        private readonly record struct DdgiProbeViewMetrics(
            bool InCurrentFrustum,
            bool InExpandedFrustum,
            bool InPredictedFrustum,
            bool InSafetyShell,
            float DistanceToCamera);

        private readonly record struct DdgiLogicalCellRange(
            DdgiClipmapCell Min,
            DdgiClipmapCell Max);

        internal sealed class DdgiProbeUpdateSchedulerScratch
        {
            private DdgiProbeUpdateCandidate[] _visibleNear = Array.Empty<DdgiProbeUpdateCandidate>();
            private DdgiProbeUpdateCandidate[] _visibleFar = Array.Empty<DdgiProbeUpdateCandidate>();
            private DdgiProbeUpdateCandidate[] _safety = Array.Empty<DdgiProbeUpdateCandidate>();

            internal Span<DdgiProbeUpdateCandidate> GetQueue(DdgiSchedulerQueueKind kind, int capacity)
            {
                if (capacity <= 0)
                    return Span<DdgiProbeUpdateCandidate>.Empty;

                switch (kind)
                {
                    case DdgiSchedulerQueueKind.VisibleNear:
                        if (_visibleNear.Length < capacity)
                            _visibleNear = new DdgiProbeUpdateCandidate[capacity];
                        return _visibleNear.AsSpan(0, capacity);

                    case DdgiSchedulerQueueKind.VisibleFar:
                        if (_visibleFar.Length < capacity)
                            _visibleFar = new DdgiProbeUpdateCandidate[capacity];
                        return _visibleFar.AsSpan(0, capacity);

                    case DdgiSchedulerQueueKind.Safety:
                        if (_safety.Length < capacity)
                            _safety = new DdgiProbeUpdateCandidate[capacity];
                        return _safety.AsSpan(0, capacity);

                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind));
                }
            }
        }

        internal enum DdgiSchedulerQueueKind
        {
            VisibleNear,
            VisibleFar,
            Safety
        }

        internal struct DdgiProbeUpdateCandidate
        {
            public DdgiProbeUpdateCandidate(float score, int probeIndex, GPUDdgiProbeUpdateRequest request)
            {
                Score = score;
                ProbeIndex = probeIndex;
                Request = request;
            }

            public float Score;
            public int ProbeIndex;
            public GPUDdgiProbeUpdateRequest Request;
        }

        private static void AddCandidateToBoundedQueue(
            Span<DdgiProbeUpdateCandidate> queue,
            ref int count,
            in DdgiProbeUpdateCandidate candidate,
            DdgiCpuSchedulerInstrumentation? instrumentation = null)
        {
            if (queue.IsEmpty)
                return;

            int insertIndex;
            if (count < queue.Length)
            {
                insertIndex = count++;
            }
            else
            {
                int lastIndex = count - 1;
                if (CompareCandidates(candidate, queue[lastIndex]) >= 0)
                    return;

                insertIndex = lastIndex;
            }

            int shiftCount = 0;
            while (insertIndex > 0 && CompareCandidates(candidate, queue[insertIndex - 1]) < 0)
            {
                queue[insertIndex] = queue[insertIndex - 1];
                insertIndex--;
                shiftCount++;
            }

            queue[insertIndex] = candidate;
            instrumentation?.RecordCandidateInsertion(shiftCount);
        }

        private static int CompareCandidates(in DdgiProbeUpdateCandidate x, in DdgiProbeUpdateCandidate y)
        {
            int scoreCompare = x.Score.CompareTo(y.Score);
            return scoreCompare != 0 ? scoreCompare : x.ProbeIndex.CompareTo(y.ProbeIndex);
        }

        private static Span<DdgiProbeUpdateCandidate> GetCandidateQueue(
            DdgiProbeUpdateSchedulerScratch? scratch,
            DdgiSchedulerQueueKind kind,
            int capacity)
        {
            return scratch != null
                ? scratch.GetQueue(kind, capacity)
                : new DdgiProbeUpdateCandidate[capacity];
        }

        private static bool AddRequest(
            in GPUDdgiProbeUpdateRequest request,
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            Span<GPUDdgiProbeUpdateRequest> destination,
            Span<byte> probeMarks,
            ulong primaryRayBudget,
            ref ulong primaryRaysUsed,
            ref int count)
        {
            int probeIndex = checked((int)request.ProbeIndex);
            if ((uint)probeIndex >= (uint)probeMarks.Length || probeMarks[probeIndex] != 0 || count >= destination.Length)
                return false;

            ulong requestPrimaryRays = ResolveRequestPrimaryRayCount(request, volumes);
            if (requestPrimaryRays == 0UL || primaryRaysUsed + requestPrimaryRays > primaryRayBudget)
                return false;

            primaryRaysUsed += requestPrimaryRays;
            probeMarks[probeIndex] = 1;
            destination[count++] = request;
            return true;
        }

        private static ulong ResolveRequestPrimaryRayCount(
            in GPUDdgiProbeUpdateRequest request,
            ReadOnlySpan<GPUDdgiProbeVolume> volumes)
        {
            if (request.VolumeIndex >= (uint)volumes.Length)
                return (ulong)GlobalIlluminationProbeVolume.MinRaysPerProbe;

            GPUDdgiProbeVolume volume = volumes[checked((int)request.VolumeIndex)];
            int raysPerProbe = (int)MathF.Round(volume.RayAndUpdateParams.X);
            raysPerProbe = Math.Clamp(
                raysPerProbe,
                GlobalIlluminationProbeVolume.MinRaysPerProbe,
                GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe);
            return (ulong)raysPerProbe;
        }

        private static bool TryResolveDirtyRequestVolume(
            ReadOnlySpan<GPUDdgiProbeVolume> volumes,
            DdgiFrameLayoutDirtyProbeRequest request,
            out int volumeIndex,
            out GPUDdgiProbeVolume volume)
        {
            if ((uint)request.VolumeIndex < (uint)volumes.Length)
            {
                GPUDdgiProbeVolume candidate = volumes[request.VolumeIndex];
                if (IsCameraRelative(candidate) && FirstProbeIndex(candidate) == request.PhysicalFirstProbeIndex)
                {
                    volumeIndex = request.VolumeIndex;
                    volume = candidate;
                    return true;
                }
            }

            for (int i = 0; i < volumes.Length; i++)
            {
                GPUDdgiProbeVolume candidate = volumes[i];
                if (!IsCameraRelative(candidate))
                    continue;
                if (FirstProbeIndex(candidate) != request.PhysicalFirstProbeIndex)
                    continue;

                volumeIndex = i;
                volume = candidate;
                return true;
            }

            volumeIndex = -1;
            volume = default;
            return false;
        }

        private static bool VolumeIntersectsDirtyBounds(in GPUDdgiProbeVolume volume, BoundingBox dirtyBounds)
        {
            Vector3 origin = Origin(volume);
            Vector3 size = Size(volume);
            BoundingBox volumeBounds = new(origin, origin + size);
            return dirtyBounds.Intersects(volumeBounds) || volumeBounds.Contains(dirtyBounds.Center);
        }

        private static DdgiClipmapCell ClampWorldPositionToClipmapCell(in GPUDdgiProbeVolume volume, Vector3 worldPosition)
        {
            Vector3 spacing = Spacing(volume);
            DdgiClipmapCell gridMin = GridMinCell(volume);
            return ClampToGrid(
                new DdgiClipmapCell(
                    FloorToCell(worldPosition.X, spacing.X),
                    FloorToCell(worldPosition.Y, spacing.Y),
                    FloorToCell(worldPosition.Z, spacing.Z)),
                gridMin,
                CountX(volume),
                CountY(volume),
                CountZ(volume));
        }

        private static DdgiClipmapCell ClampToGrid(
            DdgiClipmapCell cell,
            DdgiClipmapCell gridMin,
            int countX,
            int countY,
            int countZ)
        {
            return new DdgiClipmapCell(
                Math.Clamp(cell.X, gridMin.X, gridMin.X + countX - 1),
                Math.Clamp(cell.Y, gridMin.Y, gridMin.Y + countY - 1),
                Math.Clamp(cell.Z, gridMin.Z, gridMin.Z + countZ - 1));
        }

        private static DdgiClipmapCell Min(DdgiClipmapCell left, DdgiClipmapCell right) =>
            new(
                Math.Min(left.X, right.X),
                Math.Min(left.Y, right.Y),
                Math.Min(left.Z, right.Z));

        private static DdgiClipmapCell Max(DdgiClipmapCell left, DdgiClipmapCell right) =>
            new(
                Math.Max(left.X, right.X),
                Math.Max(left.Y, right.Y),
                Math.Max(left.Z, right.Z));

        private static int FloorToCell(float value, float spacing)
        {
            if (!float.IsFinite(value) || !float.IsFinite(spacing) || spacing <= 0.0f)
                return 0;

            double cell = Math.Floor(value / spacing);
            if (cell <= int.MinValue)
                return int.MinValue;
            if (cell >= int.MaxValue)
                return int.MaxValue;
            return (int)cell;
        }

        private static int CeilingToCell(float value, float spacing)
        {
            if (!float.IsFinite(value) || !float.IsFinite(spacing) || spacing <= 0.0f)
                return 0;

            double cell = Math.Ceiling(value / spacing);
            if (cell <= int.MinValue)
                return int.MinValue;
            if (cell >= int.MaxValue)
                return int.MaxValue;
            return (int)cell;
        }

        private static int NormalizeCursor(int updateCursor, int activeProbeCount)
        {
            if (activeProbeCount <= 0)
                return 0;

            int cursor = updateCursor % activeProbeCount;
            return cursor < 0 ? cursor + activeProbeCount : cursor;
        }

        private static bool IsCameraRelative(in GPUDdgiProbeVolume volume) =>
            (uint)MathF.Round(volume.ClipmapGridMinAndKind.W) == (uint)DdgiProbeVolumeKind.CameraClipmap;

        private static Vector3 Origin(in GPUDdgiProbeVolume volume) =>
            new(volume.OriginAndFirstProbeIndex.X, volume.OriginAndFirstProbeIndex.Y, volume.OriginAndFirstProbeIndex.Z);

        private static Vector3 Size(in GPUDdgiProbeVolume volume) =>
            new(volume.SizeAndProbeCountX.X, volume.SizeAndProbeCountX.Y, volume.SizeAndProbeCountX.Z);

        private static Vector3 Spacing(in GPUDdgiProbeVolume volume) =>
            new(volume.ProbeSpacingAndProbeCountY.X, volume.ProbeSpacingAndProbeCountY.Y, volume.ProbeSpacingAndProbeCountY.Z);

        private static int FirstProbeIndex(in GPUDdgiProbeVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.OriginAndFirstProbeIndex.W));

        private static int CountX(in GPUDdgiProbeVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.SizeAndProbeCountX.W));

        private static int CountY(in GPUDdgiProbeVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.ProbeSpacingAndProbeCountY.W));

        private static int CountZ(in GPUDdgiProbeVolume volume) =>
            Math.Max(1, (int)MathF.Round(volume.BiasAndProbeCountZ.W));

        private static DdgiClipmapCell GridMinCell(in GPUDdgiProbeVolume volume) =>
            new(
                (int)MathF.Round(volume.ClipmapGridMinAndKind.X),
                (int)MathF.Round(volume.ClipmapGridMinAndKind.Y),
                (int)MathF.Round(volume.ClipmapGridMinAndKind.Z));

        private static DdgiClipmapCell RingOffsetCell(in GPUDdgiProbeVolume volume) =>
            new(
                (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.X),
                (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.Y),
                (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.Z));

        private static int CascadeIndex(in GPUDdgiProbeVolume volume) =>
            Math.Max(0, (int)MathF.Round(volume.ClipmapRingOffsetAndCascade.W));
    }
}
