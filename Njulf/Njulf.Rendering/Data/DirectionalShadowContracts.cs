using System;

namespace Njulf.Rendering.Data;

/// <summary>Stable reason why requested directional-shadow intent was not selected.</summary>
public enum DirectionalShadowFallbackReason : uint
{
    None = 0,
    ShadowsDisabled = 1,
    NoShadowCastingDirectionalLight = 2,
    RayQueryUnsupported = 3,
    AccelerationStructureUnsupported = 4,
    RaySceneIncomplete = 5,
    RaySceneGenerationMismatch = 6,
    RequiredReceiverResourceUnavailable = 7,
    RequiredTransparentVariantUnavailable = 8,
    ResourceAllocationFailed = 9,
    InvalidConfiguration = 10,
    DeviceLost = 11
}

[Flags]
public enum RaySceneConsumer : uint
{
    None = 0,
    Ddgi = 1u << 0,
    DirectionalContact = 1u << 1,
    DirectionalFull = 1u << 2,
    GiCaustics = 1u << 3
}

[Flags]
public enum RaySceneGeometryCategory : uint
{
    None = 0,
    StaticOpaque = 1u << 0,
    DynamicOpaque = 1u << 1,
    AlphaTested = 1u << 2,
    SkinnedCurrentPose = 1u << 3,
    FoliageOpaque = 1u << 4,
    FoliageAlphaTested = 1u << 5,
    DoubleSided = 1u << 6,
    ThinTransmission = 1u << 7,
    AlphaBlend = 1u << 8,

    DirectionalShadowDefault = StaticOpaque | DynamicOpaque | AlphaTested |
        SkinnedCurrentPose | FoliageOpaque | FoliageAlphaTested | DoubleSided
}

[Flags]
public enum SurfaceHistoryConsumer : uint
{
    None = 0,
    TemporalAntiAliasing = 1u << 0,
    DirectionalCsmTemporal = 1u << 1,
    DirectionalRaySoft = 1u << 2,
    NearFieldResidual = 1u << 3
}

public static class SurfaceHistoryPolicy
{
    public static SurfaceHistoryConsumer Resolve(
        RenderSettings settings,
        bool nearFieldResidualActive,
        bool directionalCsmTemporalActive = false,
        bool directionalRaySoftActive = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SurfaceHistoryConsumer consumers = SurfaceHistoryConsumer.None;
        if (settings.AntiAliasing.EffectiveMode == AntiAliasingMode.Taa)
            consumers |= SurfaceHistoryConsumer.TemporalAntiAliasing;
        if (nearFieldResidualActive)
            consumers |= SurfaceHistoryConsumer.NearFieldResidual;
        if (directionalCsmTemporalActive)
            consumers |= SurfaceHistoryConsumer.DirectionalCsmTemporal;
        // Requested intent is deliberately insufficient here. A gated or
        // failed soft-shadow request must not allocate or execute history
        // resources while its effective mode is the deterministic CSM fallback.
        if (settings.Shadows.DirectionalShadowsEnabled && directionalRaySoftActive)
        {
            consumers |= SurfaceHistoryConsumer.DirectionalRaySoft;
        }

        return consumers;
    }

    public static bool RequiresMotionVectors(this SurfaceHistoryConsumer consumers) =>
        consumers != SurfaceHistoryConsumer.None;
}

/// <summary>
/// Immutable publication from the shared acceleration-structure owner. Consumer
/// readiness is explicit; a live TLAS alone is not a completeness guarantee.
/// </summary>
public readonly record struct RaySceneReadinessSnapshot(
    RaySceneConsumer RequestedConsumers,
    RaySceneConsumer ReadyConsumers,
    RaySceneGeometryCategory AdmittedCategories,
    RaySceneGeometryCategory CompleteCategories,
    uint ResourceGeneration,
    ulong ContentEpoch,
    string FailureDetail)
{
    public static RaySceneReadinessSnapshot Unavailable(
        RaySceneConsumer requested,
        string detail) => new(
            requested,
            RaySceneConsumer.None,
            RaySceneGeometryCategory.None,
            RaySceneGeometryCategory.None,
            0u,
            0UL,
            detail ?? string.Empty);

    public bool IsReady(
        RaySceneConsumer consumer,
        RaySceneGeometryCategory requiredCategories) =>
        consumer != RaySceneConsumer.None &&
        (ReadyConsumers & consumer) == consumer &&
        (CompleteCategories & requiredCategories) == requiredCategories &&
        ResourceGeneration != 0u;
}

/// <summary>
/// Union of ray consumers resolved before acceleration-structure preparation.
/// It keeps shared BLAS/TLAS ownership independent from any one lighting
/// feature and carries the strictest geometry/freshness requirements.
/// </summary>
public readonly record struct RaySceneRequirement(
    RaySceneConsumer Consumers,
    RaySceneGeometryCategory RequiredCategories,
    float MaximumRayDistance,
    bool RequiresCurrentPose)
{
    public static RaySceneRequirement None => default;

    public bool Enabled => Consumers != RaySceneConsumer.None;

    public RaySceneRequirement Union(in RaySceneRequirement other) => new(
        Consumers | other.Consumers,
        RequiredCategories | other.RequiredCategories,
        MathF.Max(MaximumRayDistance, other.MaximumRayDistance),
        RequiresCurrentPose || other.RequiresCurrentPose);

    public static RaySceneRequirement ForDirectionalShadows(
        ShadowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.RequestedDirectionalShadowMode switch
        {
            DirectionalShadowMode.HybridContact => new RaySceneRequirement(
                RaySceneConsumer.DirectionalContact,
                RaySceneGeometryCategory.DirectionalShadowDefault,
                settings.DirectionalContactShadowDistance,
                RequiresCurrentPose: true),
            DirectionalShadowMode.RayQueryHard =>
                new RaySceneRequirement(
                    RaySceneConsumer.DirectionalFull,
                    RaySceneGeometryCategory.DirectionalShadowDefault,
                    settings.MaxShadowDistance,
                    RequiresCurrentPose: true),
            // Finite-sun sampling/history is not promotion-qualified yet. Its
            // authored intent must not build a ray scene that cannot be consumed.
            DirectionalShadowMode.RayQuerySoft => None,
            _ => None
        };
    }
}

public static class DirectionalShadowModeResolver
{
    public static (DirectionalShadowMode Effective, DirectionalShadowFallbackReason Reason, string Detail)
        Resolve(
            ShadowSettings settings,
            bool hasShadowCastingDirectionalLight,
            bool rayQuerySupported,
            in RaySceneReadinessSnapshot rayScene,
            bool rayMaskAvailable = true,
            bool softRayAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.DirectionalShadowsEnabled)
            return (DirectionalShadowMode.Cascaded, DirectionalShadowFallbackReason.ShadowsDisabled, "directional shadows are disabled");
        if (!hasShadowCastingDirectionalLight)
            return (DirectionalShadowMode.Cascaded, DirectionalShadowFallbackReason.NoShadowCastingDirectionalLight, "no shadow-casting directional light is active");

        DirectionalShadowMode requested = settings.RequestedDirectionalShadowMode;
        if (requested == DirectionalShadowMode.Cascaded)
            return (requested, DirectionalShadowFallbackReason.None, string.Empty);
        if (!rayQuerySupported)
            return (DirectionalShadowMode.Cascaded, DirectionalShadowFallbackReason.RayQueryUnsupported, "ray queries are not supported by the selected device");
        if (requested == DirectionalShadowMode.RayQuerySoft && !softRayAvailable)
        {
            return (
                DirectionalShadowMode.Cascaded,
                DirectionalShadowFallbackReason.RequiredReceiverResourceUnavailable,
                "finite-sun history and denoising are not qualified; deterministic hard and CSM modes remain available");
        }
        if (!rayMaskAvailable)
        {
            return (
                DirectionalShadowMode.Cascaded,
                DirectionalShadowFallbackReason.RequiredReceiverResourceUnavailable,
                "the full-resolution directional ray-shadow mask is unavailable");
        }

        RaySceneConsumer consumer = requested == DirectionalShadowMode.HybridContact
            ? RaySceneConsumer.DirectionalContact
            : RaySceneConsumer.DirectionalFull;
        if (!rayScene.IsReady(consumer, RaySceneGeometryCategory.DirectionalShadowDefault))
        {
            return (
                DirectionalShadowMode.Cascaded,
                rayScene.ResourceGeneration == 0u
                    ? DirectionalShadowFallbackReason.RaySceneIncomplete
                    : DirectionalShadowFallbackReason.RaySceneGenerationMismatch,
                string.IsNullOrWhiteSpace(rayScene.FailureDetail)
                    ? "the shared ray scene is not complete for directional shadows"
                    : rayScene.FailureDetail);
        }

        return (requested, DirectionalShadowFallbackReason.None, string.Empty);
    }
}

/// <summary>One authoritative directional-shadow decision consumed by a frame.</summary>
public readonly record struct DirectionalShadowFramePlan(
    ulong StableLightIdentity,
    DirectionalShadowMode RequestedMode,
    DirectionalShadowMode EffectiveMode,
    DirectionalShadowFallbackReason FallbackReason,
    string FallbackDetail,
    bool CascadedReceiverFallbackRequired,
    uint ActiveCascadeMask,
    uint StaticRefreshMask,
    uint StaticReuseMask,
    uint WorkingCompositionMask,
    RaySceneConsumer RaySceneRequirement,
    SurfaceHistoryConsumer HistoryConsumers,
    uint RaySceneResourceGeneration,
    ulong RaySceneContentEpoch)
{
    public bool UsesCascadedShadowMap =>
        EffectiveMode is DirectionalShadowMode.Cascaded or
            DirectionalShadowMode.HybridContact ||
        CascadedReceiverFallbackRequired;

    public bool UsesRayQuery =>
        EffectiveMode != DirectionalShadowMode.Cascaded;

    public bool UsesSoftHistory =>
        EffectiveMode == DirectionalShadowMode.RayQuerySoft;
}
