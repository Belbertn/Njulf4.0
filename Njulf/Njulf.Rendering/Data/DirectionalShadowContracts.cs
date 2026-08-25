using System;
using Njulf.Core.Math;

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
    DeviceLost = 11,
    GpuBudgetDemotion = 12,
    QualificationManifestMissing = 13,
    QualificationManifestMismatch = 14,
    RaySceneBoundsInvalid = 15
}

public enum DirectionalShadowQualificationLevel : uint
{
    Developer = 0,
    Experimental = 1,
    Production = 2
}

[Flags]
public enum DirectionalShadowHistoryResetReason : uint
{
    None = 0,
    InitialFrame = 1u << 0,
    CameraCut = 1u << 1,
    LightChanged = 1u << 2,
    ModeChanged = 1u << 3,
    ExtentChanged = 1u << 4,
    RaySceneChanged = 1u << 5,
    MaterialChanged = 1u << 6,
    ResourceRecreated = 1u << 7,
    DeviceRecreated = 1u << 8,
    InvalidMotion = 1u << 9
}

public enum DirectionalShadowReceiverPolicy : uint
{
    Cascaded = 0,
    OpaqueScreenMask = 1,
    LayeredFragmentRayQuery = 2,
    DecalDepthOwnerMask = 3
}

[Flags]
public enum RaySceneConsumer : uint
{
    None = 0,
    Ddgi = 1u << 0,
    DirectionalContact = 1u << 1,
    DirectionalFull = 1u << 2,
    GiCaustics = 1u << 3,
    Reflection = 1u << 4,
    ThickTransmission = 1u << 5,
    AreaLightShadows = 1u << 6
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
    VolumeTransmission = 1u << 9,
    WaterSurface = 1u << 10,

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
    NearFieldResidual = 1u << 3,
    Reflection = 1u << 4
}

public static class SurfaceHistoryPolicy
{
    public static SurfaceHistoryConsumer Resolve(
        RenderSettings settings,
        bool nearFieldResidualActive,
        bool directionalCsmTemporalActive = false,
        bool directionalRaySoftActive = false,
        bool reflectionActive = false)
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
        if (reflectionActive)
            consumers |= SurfaceHistoryConsumer.Reflection;

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
    public Vector3 CoverageMinimum { get; init; }
    public Vector3 CoverageMaximum { get; init; }
    public RaySceneGeometryCategory ExactCategories { get; init; }
    public RaySceneGeometryCategory ProxyCategories { get; init; }

    public bool HasQualifiedBounds =>
        ResourceGeneration != 0u &&
        IsFinite(CoverageMinimum) &&
        IsFinite(CoverageMaximum) &&
        CoverageMinimum.X <= CoverageMaximum.X &&
        CoverageMinimum.Y <= CoverageMaximum.Y &&
        CoverageMinimum.Z <= CoverageMaximum.Z;

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

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
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
            DirectionalShadowMode.RayQuerySoft =>
                new RaySceneRequirement(
                    RaySceneConsumer.DirectionalFull,
                    RaySceneGeometryCategory.DirectionalShadowDefault,
                    settings.MaxShadowDistance,
                    RequiresCurrentPose: true),
            _ => None
        };
    }

    public static RaySceneRequirement ForReflections(ReflectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled || settings.Mode != ReflectionMode.HybridRayQuery)
            return None;

        return new RaySceneRequirement(
            RaySceneConsumer.Reflection,
            // Reflection hits share the DDGI alpha-mask composition. Ordinary
            // alpha blend and thin transmission remain non-binary and are
            // shaded by the forward transparent receiver path.
            RaySceneGeometryCategory.DirectionalShadowDefault,
            settings.SsrMaxDistance,
            RequiresCurrentPose: true);
    }

    public static RaySceneRequirement ForAreaLightShadows(
        ShadowSettings settings,
        bool hasSelectedAreaLight,
        float maximumRayDistance)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AreaShadowsEnabled ||
            !hasSelectedAreaLight ||
            !float.IsFinite(maximumRayDistance) ||
            maximumRayDistance <= 0f)
        {
            return None;
        }

        return new RaySceneRequirement(
            RaySceneConsumer.AreaLightShadows,
            RaySceneGeometryCategory.DirectionalShadowDefault,
            maximumRayDistance,
            RequiresCurrentPose: true);
    }

    public static RaySceneRequirement ForThickTransmission(
        TransparencySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled ||
            settings.ThickTransmissionMode != ThickTransmissionMode.RayQuery)
        {
            return None;
        }

        return new RaySceneRequirement(
            RaySceneConsumer.ThickTransmission,
            RaySceneGeometryCategory.DirectionalShadowDefault |
            RaySceneGeometryCategory.VolumeTransmission |
            RaySceneGeometryCategory.WaterSurface,
            settings.ThickTransmissionMaximumDistance,
            RequiresCurrentPose: true);
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
            bool softRayAvailable = false,
            bool transparentRayReceiverRequired = false,
            bool transparentRayVariantAvailable = true)
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
        if (requested is (DirectionalShadowMode.RayQueryHard or
                DirectionalShadowMode.RayQuerySoft) &&
            transparentRayReceiverRequired &&
            !transparentRayVariantAvailable)
        {
            return (
                DirectionalShadowMode.Cascaded,
                DirectionalShadowFallbackReason.RequiredTransparentVariantUnavailable,
                "an active transparent shadow receiver requires the ray-query fragment variant");
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
        if (!rayScene.HasQualifiedBounds)
        {
            return (
                DirectionalShadowMode.Cascaded,
                DirectionalShadowFallbackReason.RaySceneBoundsInvalid,
                "the shared ray scene did not publish finite qualified coverage bounds");
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
    public bool UsesCsmTemporal { get; init; }
    public DirectionalShadowReceiverPolicy OpaqueReceiverPolicy { get; init; }
    public DirectionalShadowReceiverPolicy TransparentReceiverPolicy { get; init; }
    public DirectionalShadowReceiverPolicy DecalReceiverPolicy { get; init; }
    public DirectionalShadowHistoryResetReason HistoryResetReason { get; init; }
    public uint ScreenResourceGeneration { get; init; }
    public float SunAngularRadiusRadians { get; init; }
    public DirectionalShadowQualificationLevel QualificationLevel { get; init; }
    public string QualificationId { get; init; } = string.Empty;
    public string QualificationDetail { get; init; } = string.Empty;
    public string QualificationDeviceRuleId { get; init; } = string.Empty;
    public string QualificationTrackId { get; init; } = string.Empty;
    public double QualifiedGpuBudgetMicroseconds { get; init; }
    public ulong QualifiedMemoryBudgetBytes { get; init; }

    public bool UsesCascadedShadowMap =>
        EffectiveMode is DirectionalShadowMode.Cascaded or
            DirectionalShadowMode.HybridContact ||
        CascadedReceiverFallbackRequired;

    public bool UsesRayQuery =>
        EffectiveMode != DirectionalShadowMode.Cascaded;

    public bool UsesSoftHistory =>
        EffectiveMode == DirectionalShadowMode.RayQuerySoft;

    public bool UsesScreenHistory => UsesSoftHistory || UsesCsmTemporal;
}
