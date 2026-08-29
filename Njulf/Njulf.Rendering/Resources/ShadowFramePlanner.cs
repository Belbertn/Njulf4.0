using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

internal readonly record struct ShadowFrameCandidate(
    DirectionalShadowMode EffectiveMode,
    DirectionalShadowFallbackReason FallbackReason,
    string FallbackDetail);

internal readonly record struct ShadowFrameCandidateInput(
    ShadowSettings Settings,
    bool HasShadowCastingDirectionalLight,
    bool RayQuerySupported,
    RaySceneReadinessSnapshot RaySceneReadiness,
    bool RayMaskAvailable,
    bool SoftHistoryAvailable,
    bool TransparentRayReceiverRequired,
    bool TransparentRayVariantAvailable,
    bool SoftCollapsesToHard,
    bool UniversalCsmFallbackAvailable,
    bool RayResourceProviderPresent,
    string RayResourceFailureDetail);

internal readonly record struct ShadowFramePlanInput(
    RenderSettings Settings,
    ShadowFrameCandidate Candidate,
    bool CsmTemporalActive,
    DirectionalShadowQualificationGateResult CsmTemporalQualification,
    DirectionalShadowQualificationGateResult RayQualification,
    ulong FrameSerial,
    DirectionalShadowRuntimeDiagnostics CompletedRuntime,
    int CascadeCount,
    ulong StableLightIdentity,
    bool NearFieldResidualHistoryActive,
    bool GeometryDecalCsmFallbackRequired,
    bool CsmDebugFallbackRequired,
    bool TransparentRayVariantAvailable,
    uint ScreenResourceGeneration,
    float SunAngularRadiusRadians,
    RaySceneReadinessSnapshot RaySceneReadiness);

internal sealed class ShadowFramePlanner
{
    private const int QualifiedBudgetOverrunThreshold = 3;
    private const ulong BudgetDemotionCooldownFrames = 120UL;

    private int _qualifiedBudgetOverrunStreak;
    private ulong _budgetDemotionUntilFrame;

    internal ShadowFrameCandidate ResolveCandidate(
        in ShadowFrameCandidateInput input)
    {
        ArgumentNullException.ThrowIfNull(input.Settings);

        var resolved = DirectionalShadowModeResolver.Resolve(
            input.Settings,
            input.HasShadowCastingDirectionalLight,
            input.RayQuerySupported,
            input.RaySceneReadiness,
            input.RayMaskAvailable,
            input.SoftCollapsesToHard || input.SoftHistoryAvailable,
            input.TransparentRayReceiverRequired,
            input.TransparentRayVariantAvailable);

        if (input.SoftCollapsesToHard &&
            resolved.Effective == DirectionalShadowMode.RayQuerySoft)
        {
            resolved = (
                DirectionalShadowMode.RayQueryHard,
                DirectionalShadowFallbackReason.None,
                "zero directional soft angular diameter resolves to deterministic hard rays");
        }

        if (input.Settings.RequestedDirectionalShadowMode !=
                DirectionalShadowMode.Cascaded &&
            !input.RayMaskAvailable &&
            resolved.Reason ==
                DirectionalShadowFallbackReason.RequiredReceiverResourceUnavailable &&
            input.RayResourceProviderPresent)
        {
            resolved = (
                DirectionalShadowMode.Cascaded,
                input.RayResourceFailureDetail.Contains(
                    "allocation failed",
                    StringComparison.OrdinalIgnoreCase)
                    ? DirectionalShadowFallbackReason.ResourceAllocationFailed
                    : resolved.Reason,
                string.IsNullOrWhiteSpace(input.RayResourceFailureDetail)
                    ? resolved.Detail
                    : input.RayResourceFailureDetail);
        }

        if (!input.UniversalCsmFallbackAvailable &&
            input.Settings.DirectionalShadowsEnabled &&
            input.HasShadowCastingDirectionalLight)
        {
            resolved = (
                DirectionalShadowMode.Cascaded,
                DirectionalShadowFallbackReason.ResourceAllocationFailed,
                "the universal directional cascade fallback resources are unavailable");
        }

        return new ShadowFrameCandidate(
            resolved.Effective,
            resolved.Reason,
            resolved.Detail);
    }

    internal DirectionalShadowFramePlan CreatePlan(
        in ShadowFramePlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input.Settings);
        ArgumentNullException.ThrowIfNull(input.CompletedRuntime);

        ShadowSettings settings = input.Settings.Shadows;
        DirectionalShadowMode effectiveMode = input.Candidate.EffectiveMode;
        DirectionalShadowFallbackReason fallbackReason =
            input.Candidate.FallbackReason;
        string fallbackDetail = input.Candidate.FallbackDetail;
        bool csmTemporalActive = input.CsmTemporalActive;
        // Auto is migrated to the ordinary Enabled production request. Keep
        // the legacy value executable, but never make a manifest the owner of
        // activation or budget demotion.
        bool csmTemporalAutoRequested = false;

        if (csmTemporalActive && csmTemporalAutoRequested &&
            IsQualifiedBudgetDemoted(
                DirectionalShadowMode.Cascaded,
                input.CsmTemporalQualification,
                input.FrameSerial,
                input.CompletedRuntime,
                out string csmTemporalBudgetDetail))
        {
            csmTemporalActive = false;
            effectiveMode = DirectionalShadowMode.Cascaded;
            fallbackReason =
                DirectionalShadowFallbackReason.GpuBudgetDemotion;
            fallbackDetail = csmTemporalBudgetDetail;
        }

        if (effectiveMode != DirectionalShadowMode.Cascaded &&
            IsQualifiedBudgetDemoted(
                effectiveMode,
                input.RayQualification,
                input.FrameSerial,
                input.CompletedRuntime,
                out string rayBudgetDetail))
        {
            effectiveMode = DirectionalShadowMode.Cascaded;
            fallbackReason =
                DirectionalShadowFallbackReason.GpuBudgetDemotion;
            fallbackDetail = rayBudgetDetail;
        }

        int cascadeCount = Math.Clamp(
            input.CascadeCount,
            0,
            ShadowSettings.MaxDirectionalCascades);
        uint activeCascadeMask = cascadeCount == 0
            ? 0u
            : (1u << cascadeCount) - 1u;
        RaySceneConsumer rayConsumer =
            settings.RequestedDirectionalShadowMode switch
            {
                DirectionalShadowMode.HybridContact =>
                    RaySceneConsumer.DirectionalContact,
                DirectionalShadowMode.RayQueryHard or
                    DirectionalShadowMode.RayQuerySoft =>
                    RaySceneConsumer.DirectionalFull,
                _ => RaySceneConsumer.None
            };
        SurfaceHistoryConsumer historyConsumers = SurfaceHistoryPolicy.Resolve(
            input.Settings,
            input.NearFieldResidualHistoryActive,
            directionalCsmTemporalActive: csmTemporalActive,
            directionalRaySoftActive:
                effectiveMode == DirectionalShadowMode.RayQuerySoft);
        bool layeredReceiverFallbackRequired =
            (effectiveMode is DirectionalShadowMode.RayQueryHard or
                DirectionalShadowMode.RayQuerySoft) &&
            (input.GeometryDecalCsmFallbackRequired ||
             input.CsmDebugFallbackRequired);

        DirectionalShadowQualificationGateResult qualification;
        DirectionalShadowQualificationLevel qualificationLevel;
        if (effectiveMode != DirectionalShadowMode.Cascaded)
        {
            qualification = input.RayQualification;
            qualificationLevel = qualification.Passed
                ? DirectionalShadowQualificationLevel.Production
                : DirectionalShadowQualificationLevel.Experimental;
        }
        else if (csmTemporalActive)
        {
            qualification = DirectionalShadowQualificationGateResult.Reject(
                settings.DirectionalCsmTemporalMode ==
                    DirectionalCsmTemporalMode.DeveloperForce
                    ? "directional-shadow-csm-temporal-developer-force"
                    : "directional-shadow-csm-temporal-production-enabled");
            qualificationLevel =
                settings.DirectionalCsmTemporalMode ==
                    DirectionalCsmTemporalMode.DeveloperForce
                    ? DirectionalShadowQualificationLevel.Developer
                    : DirectionalShadowQualificationLevel.Production;
        }
        else
        {
            qualification = DirectionalShadowQualificationGateResult.Reject(
                csmTemporalAutoRequested
                    ? input.CsmTemporalQualification.FailureDetail
                    : fallbackReason == DirectionalShadowFallbackReason.None
                        ? "directional-shadow-baseline-csm-does-not-require-manifest"
                        : "directional-shadow-ray-request-fell-back-to-production-csm");
            qualificationLevel = DirectionalShadowQualificationLevel.Production;
        }

        return new DirectionalShadowFramePlan(
            input.StableLightIdentity,
            settings.RequestedDirectionalShadowMode,
            effectiveMode,
            fallbackReason,
            fallbackDetail,
            layeredReceiverFallbackRequired,
            activeCascadeMask,
            0u,
            0u,
            activeCascadeMask,
            rayConsumer,
            historyConsumers,
            input.RaySceneReadiness.ResourceGeneration,
            input.RaySceneReadiness.ContentEpoch)
        {
            UsesCsmTemporal = csmTemporalActive,
            OpaqueReceiverPolicy =
                effectiveMode == DirectionalShadowMode.Cascaded
                    ? DirectionalShadowReceiverPolicy.Cascaded
                    : DirectionalShadowReceiverPolicy.OpaqueScreenMask,
            TransparentReceiverPolicy =
                effectiveMode == DirectionalShadowMode.Cascaded ||
                effectiveMode == DirectionalShadowMode.HybridContact &&
                !input.TransparentRayVariantAvailable
                    ? DirectionalShadowReceiverPolicy.Cascaded
                    : DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery,
            DecalReceiverPolicy =
                effectiveMode == DirectionalShadowMode.Cascaded &&
                !csmTemporalActive
                    ? DirectionalShadowReceiverPolicy.Cascaded
                    : DirectionalShadowReceiverPolicy.DecalDepthOwnerMask,
            ScreenResourceGeneration = input.ScreenResourceGeneration,
            SunAngularRadiusRadians = input.SunAngularRadiusRadians,
            QualificationLevel = qualificationLevel,
            QualificationId = qualification.QualificationId,
            QualificationDetail = qualification.FailureDetail,
            QualificationDeviceRuleId = qualification.MatchedDeviceRuleId,
            QualificationTrackId = qualification.MatchedTrackId,
            QualifiedGpuBudgetMicroseconds =
                qualification.DirectionalShadowGpuBudgetMicroseconds,
            QualifiedMemoryBudgetBytes =
                qualification.DirectionalShadowMemoryBudgetBytes
        };
    }

    private bool IsQualifiedBudgetDemoted(
        DirectionalShadowMode mode,
        in DirectionalShadowQualificationGateResult qualification,
        ulong frameSerial,
        DirectionalShadowRuntimeDiagnostics completedRuntime,
        out string detail)
    {
        detail = string.Empty;
        if (!qualification.Passed ||
            qualification.DirectionalShadowGpuBudgetMicroseconds <= 0.0 ||
            qualification.DirectionalShadowMemoryBudgetBytes == 0UL)
        {
            _qualifiedBudgetOverrunStreak = 0;
            return false;
        }

        if (frameSerial < _budgetDemotionUntilFrame)
        {
            detail =
                $"qualified directional-shadow runtime budget is cooling down until frame " +
                $"{_budgetDemotionUntilFrame}";
            return true;
        }

        if (completedRuntime.QualificationLevel !=
                DirectionalShadowQualificationLevel.Production ||
            completedRuntime.EffectiveMode != mode)
        {
            _qualifiedBudgetOverrunStreak = 0;
            return false;
        }

        long measuredGpuMicroseconds = checked(
            completedRuntime.GpuCsmMicroseconds +
            completedRuntime.GpuRayTraceMicroseconds +
            completedRuntime.GpuTemporalMicroseconds +
            completedRuntime.GpuSpatialMicroseconds);
        ulong measuredMemoryBytes = checked(
            completedRuntime.RayMaskBytes + completedRuntime.HistoryBytes);
        bool overGpu = measuredGpuMicroseconds >
            qualification.DirectionalShadowGpuBudgetMicroseconds;
        bool overMemory = measuredMemoryBytes >
            qualification.DirectionalShadowMemoryBudgetBytes;
        _qualifiedBudgetOverrunStreak = overGpu || overMemory
            ? _qualifiedBudgetOverrunStreak + 1
            : Math.Max(0, _qualifiedBudgetOverrunStreak - 1);
        if (_qualifiedBudgetOverrunStreak <
            QualifiedBudgetOverrunThreshold)
        {
            return false;
        }

        _qualifiedBudgetOverrunStreak = 0;
        _budgetDemotionUntilFrame = checked(
            frameSerial + BudgetDemotionCooldownFrames);
        detail =
            $"qualified directional-shadow budget exceeded for three completed frames: " +
            $"gpu={measuredGpuMicroseconds}us/" +
            $"{qualification.DirectionalShadowGpuBudgetMicroseconds:0.###}us, " +
            $"memory={measuredMemoryBytes}/" +
            $"{qualification.DirectionalShadowMemoryBudgetBytes} bytes";
        return true;
    }
}
