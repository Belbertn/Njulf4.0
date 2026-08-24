using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>Stable reason why requested reflection intent was demoted.</summary>
public enum ReflectionFallbackReason : uint
{
    None = 0,
    ReflectionsDisabled = 1,
    ReceiverPayloadUnavailable = 2,
    HiZUnavailable = 3,
    RayQueryUnsupported = 4,
    AccelerationStructureUnsupported = 5,
    RaySceneIncomplete = 6,
    RaySceneGenerationMismatch = 7,
    ResourceAllocationFailed = 8,
    InvalidConfiguration = 9,
    DeviceLost = 10
}

/// <summary>Owner of the radiance selected by the strict reflection fallback chain.</summary>
public enum ReflectionSource : uint
{
    None = 0,
    ScreenSpace = 1,
    RayQuery = 2,
    LocalProbe = 3,
    GlobalEnvironment = 4
}

/// <summary>Why a screen-space sample was admitted to the bounded recovery queue.</summary>
public enum ReflectionRayQueryReason : uint
{
    None = 0,
    Disoccluded = 1,
    InvalidOrOffScreen = 2,
    LowConfidence = 3
}

public enum ReflectionResolutionTier : uint
{
    Full = 1,
    Half = 2,
    Quarter = 4,
    AnalyticFallback = 0
}

[Flags]
public enum ReflectionHistoryResetReason : uint
{
    None = 0,
    InitialFrame = 1u << 0,
    CameraCut = 1u << 1,
    ModeChanged = 1u << 2,
    ExtentChanged = 1u << 3,
    ReceiverPayloadAbiChanged = 1u << 4,
    RoughnessBandsChanged = 1u << 5,
    RaySceneChanged = 1u << 6,
    ProbeGenerationChanged = 1u << 7,
    EnvironmentGenerationChanged = 1u << 8,
    ResourceRecreated = 1u << 9,
    DeviceRecreated = 1u << 10
}

public readonly record struct ReflectionModeCapabilities(
    bool ReceiverPayloadAvailable,
    bool HiZAvailable,
    bool RayQuerySupported,
    bool AccelerationStructureSupported,
    bool RaySceneReady);

public readonly record struct ReflectionModeResolution(
    ReflectionMode Requested,
    ReflectionMode Effective,
    ReflectionFallbackReason Reason,
    string Detail)
{
    public bool UsesDeferredPath => Effective is
        ReflectionMode.StaticProbesAndSsr or ReflectionMode.HybridRayQuery;

    public bool UsesRayQueries => Effective == ReflectionMode.HybridRayQuery;
}

public static class ReflectionModeResolver
{
    public static ReflectionModeResolution Resolve(
        ReflectionSettings settings,
        in ReflectionModeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ReflectionMode requested = settings.Enabled
            ? settings.Mode
            : ReflectionMode.Disabled;
        if (requested == ReflectionMode.Disabled)
        {
            return new ReflectionModeResolution(
                requested,
                ReflectionMode.Disabled,
                ReflectionFallbackReason.ReflectionsDisabled,
                "Reflections are disabled.");
        }

        if (requested is not ReflectionMode.StaticProbesAndSsr and
            not ReflectionMode.HybridRayQuery)
        {
            return new ReflectionModeResolution(
                requested,
                requested,
                ReflectionFallbackReason.None,
                string.Empty);
        }

        if (!capabilities.ReceiverPayloadAvailable)
        {
            return DemoteToProbes(
                requested,
                ReflectionFallbackReason.ReceiverPayloadUnavailable,
                "The opaque reflection receiver payload is unavailable.");
        }

        if (!capabilities.HiZAvailable)
        {
            return DemoteToProbes(
                requested,
                ReflectionFallbackReason.HiZUnavailable,
                "The reverse-Z Hi-Z pyramid is unavailable.");
        }

        if (requested == ReflectionMode.StaticProbesAndSsr)
        {
            return new ReflectionModeResolution(
                requested,
                requested,
                ReflectionFallbackReason.None,
                string.Empty);
        }

        if (!capabilities.RayQuerySupported)
        {
            return DemoteToSsr(
                requested,
                ReflectionFallbackReason.RayQueryUnsupported,
                "Ray queries are unsupported; retaining SSR and analytic fallbacks.");
        }

        if (!capabilities.AccelerationStructureSupported)
        {
            return DemoteToSsr(
                requested,
                ReflectionFallbackReason.AccelerationStructureUnsupported,
                "Acceleration structures are unsupported; retaining SSR and analytic fallbacks.");
        }

        if (!capabilities.RaySceneReady)
        {
            return DemoteToSsr(
                requested,
                ReflectionFallbackReason.RaySceneIncomplete,
                "The shared ray scene is incomplete; retaining SSR and analytic fallbacks.");
        }

        return new ReflectionModeResolution(
            requested,
            requested,
            ReflectionFallbackReason.None,
            string.Empty);
    }

    private static ReflectionModeResolution DemoteToProbes(
        ReflectionMode requested,
        ReflectionFallbackReason reason,
        string detail) => new(
            requested,
            ReflectionMode.StaticProbes,
            reason,
            detail);

    private static ReflectionModeResolution DemoteToSsr(
        ReflectionMode requested,
        ReflectionFallbackReason reason,
        string detail) => new(
            requested,
            ReflectionMode.StaticProbesAndSsr,
            reason,
            detail);
}

public static class HybridReflectionBudgetPlanner
{
    public const double RayQueryTargetUtilization = 0.9;

    public static ReflectionResolutionTier ResolveResolutionTier(
        ReflectionSettings settings,
        float perceptualRoughness)
    {
        ArgumentNullException.ThrowIfNull(settings);
        float roughness = float.IsFinite(perceptualRoughness)
            ? Math.Clamp(perceptualRoughness, 0.0f, 1.0f)
            : 1.0f;
        float full = settings.SsrFullResolutionRoughness;
        float half = MathF.Max(full, settings.SsrHalfResolutionRoughness);
        float quarter = MathF.Max(half, settings.SsrQuarterResolutionRoughness);
        if (roughness <= full)
            return ReflectionResolutionTier.Full;
        if (roughness <= half)
            return ReflectionResolutionTier.Half;
        if (roughness <= quarter)
            return ReflectionResolutionTier.Quarter;
        return ReflectionResolutionTier.AnalyticFallback;
    }

    public static uint ResolveRayQueryCapacity(
        ReflectionSettings settings,
        uint renderWidth,
        uint renderHeight)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ulong pixels = (ulong)renderWidth * renderHeight;
        double requested = Math.Ceiling(
            pixels * (double)settings.RayQueryPixelBudgetFraction);
        return requested >= uint.MaxValue ? uint.MaxValue : (uint)requested;
    }

    /// <summary>
    /// Resolves a hash threshold that distributes the bounded ray queue over
    /// the complete screen. Completed request telemetry avoids both chronic
    /// underfill and dispatch-order bias; a missing or invalid sample starts
    /// conservatively against the full pixel count.
    /// </summary>
    public static uint ResolveRayQueryAdmissionThreshold(
        uint capacity,
        uint renderWidth,
        uint renderHeight,
        uint previousRequestCount,
        bool previousRequestCountValid)
    {
        if (capacity == 0u || renderWidth == 0u || renderHeight == 0u)
            return 0u;

        ulong pixels = (ulong)renderWidth * renderHeight;
        ulong estimatedRequests = previousRequestCountValid &&
            previousRequestCount != 0u
                ? previousRequestCount
                : pixels;
        if ((ulong)capacity >= estimatedRequests)
            return uint.MaxValue;

        double probability = Math.Min(1.0,
            capacity * RayQueryTargetUtilization / estimatedRequests);
        double threshold = Math.Floor(probability * uint.MaxValue);
        return Math.Max(1u, (uint)threshold);
    }
}

/// <summary>
/// Keeps the depth pyramid available to SSR without changing the independent
/// occlusion-culling decision.
/// </summary>
public static class HybridReflectionHiZPolicy
{
    public static bool RequiresPyramid(
        ReflectionSettings settings,
        bool reflectionsAllowed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return reflectionsAllowed && settings.Enabled && settings.Mode is
            ReflectionMode.StaticProbesAndSsr or
            ReflectionMode.HybridRayQuery;
    }

    public static HiZVisibilityPolicyDecision RetainPyramid(
        in HiZVisibilityPolicyDecision decision,
        bool required,
        bool sceneChanged,
        bool cameraCut)
    {
        if (!required)
            return decision;

        string reason = decision.BuildHiZ
            ? decision.Reason
            : decision.Reason +
              " Hi-Z pyramid construction remains active for hybrid reflections; occlusion culling remains disabled.";
        return decision with
        {
            BuildHiZ = true,
            SceneChanged = sceneChanged,
            CameraCut = cameraCut,
            PyramidInvalidated = decision.PyramidInvalidated ||
                sceneChanged || cameraCut,
            Reason = reason
        };
    }
}

/// <summary>GPU ABI shared by classification, ray-query, resolve, and debug passes.</summary>
public static class HybridReflectionGpuContract
{
    public const uint ReceiverPayloadWords = 4;
    public const uint HistoryMetadataWords = 4;
    public const uint TaskWords = 4;
    public const uint CounterWords = 8;
    public const uint IndirectArgumentWords = 3;
    public const float NormalHistoryDotThreshold = 0.9f;
    public const float MinimumHistoryDepthToleranceMeters = 0.02f;
    public const float RelativeHistoryDepthTolerance = 0.01f;
    public const float SsrToRayQueryHistoryWeightScale = 0.35f;
    public const int MaximumPushConstantBytes = 128;
}

public readonly record struct HybridReflectionCounterSnapshot(
    int ReadbackValid,
    uint SsrHits,
    uint RayRequests,
    uint RayQueries,
    uint RayOverflows,
    uint RayHits,
    uint RayMisses,
    uint ProbeFallbacks,
    uint EnvironmentFallbacks)
{
    public static HybridReflectionCounterSnapshot Empty => default;
}

public readonly record struct HybridReflectionHistoryRevision(
    uint Width,
    uint Height,
    ReflectionMode Mode,
    uint ReceiverPayloadAbiVersion,
    float FullResolutionRoughness,
    float HalfResolutionRoughness,
    float QuarterResolutionRoughness,
    uint RaySceneGeneration,
    ulong ReflectionProbeRevision,
    uint EnvironmentGeneration,
    ulong CameraCutSerial)
{
    public ReflectionHistoryResetReason ResolveResetReasons(
        in HybridReflectionHistoryRevision previous,
        bool hasHistory)
    {
        if (!hasHistory)
            return ReflectionHistoryResetReason.InitialFrame;

        ReflectionHistoryResetReason reasons = ReflectionHistoryResetReason.None;
        if (Width != previous.Width || Height != previous.Height)
            reasons |= ReflectionHistoryResetReason.ExtentChanged;
        if (Mode != previous.Mode)
            reasons |= ReflectionHistoryResetReason.ModeChanged;
        if (ReceiverPayloadAbiVersion != previous.ReceiverPayloadAbiVersion)
            reasons |= ReflectionHistoryResetReason.ReceiverPayloadAbiChanged;
        if (FullResolutionRoughness != previous.FullResolutionRoughness ||
            HalfResolutionRoughness != previous.HalfResolutionRoughness ||
            QuarterResolutionRoughness != previous.QuarterResolutionRoughness)
        {
            reasons |= ReflectionHistoryResetReason.RoughnessBandsChanged;
        }
        if (RaySceneGeneration != previous.RaySceneGeneration)
            reasons |= ReflectionHistoryResetReason.RaySceneChanged;
        if (ReflectionProbeRevision != previous.ReflectionProbeRevision)
            reasons |= ReflectionHistoryResetReason.ProbeGenerationChanged;
        if (EnvironmentGeneration != previous.EnvironmentGeneration)
            reasons |= ReflectionHistoryResetReason.EnvironmentGenerationChanged;
        if (CameraCutSerial != previous.CameraCutSerial)
            reasons |= ReflectionHistoryResetReason.CameraCut;
        return reasons;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionSsrPushConstants
{
    public Matrix4x4 InverseViewProjectionMatrix;
    public Vector4 CameraPositionAndMaximumDistance;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint MaximumSteps;
    public uint HiZMipCount;
    public float FullResolutionRoughness;
    public float HalfResolutionRoughness;
    public float QuarterResolutionRoughness;
    public float ConfidenceThreshold;
    public uint TemporalSampleIndex;
    public uint HistoryValidAndCurrentFrameIndex;
    public uint RayQueriesEnabled;
    public uint RayAdmissionThreshold;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionRayPushConstants
{
    public Matrix4x4 InverseViewProjectionMatrix;
    public Vector4 CameraPositionAndMaximumDistance;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint TaskCapacity;
    public uint LightCount;
    public uint DirectionalLightCount;
    public uint LocalLightCount;
    public uint MaximumShadedLights;
    public uint DdgiEnabled;
    public uint CurrentFrameIndex;
    public uint TemporalSampleIndex;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionResolvePushConstants
{
    public Matrix4x4 InverseViewProjectionMatrix;
    public Vector4 CameraPositionAndIntensity;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint MaximumProbesPerPixel;
    public uint ReflectionDebugView;
    public float SsrConfidenceThreshold;
    public float Padding0;
    public float Padding1;
    public float Padding2;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionTemporalPushConstants
{
    public Matrix4x4 InverseViewProjectionMatrix;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint MaximumHistoryLength;
    public uint ResetReasons;
    public float MaximumHistoryWeight;
    public float SourceTransitionWeightScale;
    public float VarianceGamma;
    public float Padding0;
    public uint CurrentFrameIndex;
    public uint Padding1;
    public uint Padding2;
    public uint Padding3;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionSpatialPushConstants
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint Iteration;
    public uint ReadScratch;
    public float NormalPower;
    public float DepthSigma;
    public float RoughnessSigma;
    public float Padding0;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionCompositePushConstants
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint SpatialPassCount;
    public uint DebugView;
}
