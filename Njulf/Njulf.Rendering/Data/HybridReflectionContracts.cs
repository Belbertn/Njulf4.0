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

/// <summary>Stable reason why an adaptive implementation request rolled back.</summary>
public enum ReflectionImplementationFallbackReason : uint
{
    None = 0,
    ReflectionsDisabled = 1,
    AdaptivePipelineUnavailable = 2,
    ReceiverLobeExtensionUnavailable = 3,
    CompactHistoryUnavailable = 4,
    ResourceAllocationFailed = 5,
    InvalidConfiguration = 6,
    DeviceLost = 7,
    AutomaticPlanarUnavailable = 8,
    AutomaticPlanarMemoryDenied = 9
}

/// <summary>Owner of the radiance selected by the strict reflection fallback chain.</summary>
public enum ReflectionSource : uint
{
    None = 0,
    ScreenSpace = 1,
    RayQuery = 2,
    Ddgi = 3,
    LocalProbe = 4,
    GlobalEnvironment = 5,
    Planar = 6
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
public enum ReflectionLobeFlags : uint
{
    None = 0,
    Transmissive = 1u << 0,
    Anisotropic = 1u << 1,
    BroadAnisotropic = Anisotropic,
    Clearcoat = 1u << 2
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
    DeviceRecreated = 1u << 10,
    DdgiTopologyChanged = 1u << 11,
    MaterialRevisionChanged = 1u << 12,
    ImplementationChanged = 1u << 13,
    HistoryMetadataAbiChanged = 1u << 14
}

[Flags]
public enum ReflectionHistorySourceInvalidation : uint
{
    None = 0,
    RayScene = 1u << 0,
    Ddgi = 1u << 1,
    Material = 1u << 2,
    LocalProbe = 1u << 3,
    Environment = 1u << 4,
    Planar = 1u << 5
}

public readonly record struct ReflectionImplementationCapabilities(
    bool AdaptivePipelineAvailable,
    bool ReceiverLobeExtensionAvailable,
    bool CompactHistoryAvailable);

public readonly record struct ReflectionImplementationResolution(
    ReflectionImplementationMode Requested,
    ReflectionImplementationMode Effective,
    ReflectionImplementationFallbackReason Reason,
    string Detail)
{
    public bool UsesAdaptive =>
        Effective == ReflectionImplementationMode.Adaptive;
}

/// <summary>
/// Resolves implementation intent independently of source-mode capability.
/// Hardware qualification is intentionally not part of this decision: the
/// adaptive implementation remains selected and its individual sources use
/// the normal reflection fallback chain when a source cannot execute.
/// </summary>
public static class ReflectionImplementationResolver
{
    public static ReflectionImplementationResolution Resolve(
        ReflectionSettings settings,
        in ReflectionImplementationCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ReflectionImplementationMode requested = settings.ImplementationMode;
        if (!settings.Enabled || settings.Mode == ReflectionMode.Disabled)
        {
            return new ReflectionImplementationResolution(
                requested,
                requested == ReflectionImplementationMode.Legacy
                    ? ReflectionImplementationMode.Legacy
                    : ReflectionImplementationMode.Adaptive,
                ReflectionImplementationFallbackReason.ReflectionsDisabled,
                "Reflections are disabled.");
        }

        ReflectionImplementationMode desired = requested switch
        {
            ReflectionImplementationMode.Auto =>
                ReflectionImplementationMode.Adaptive,
            ReflectionImplementationMode.Legacy =>
                ReflectionImplementationMode.Legacy,
            ReflectionImplementationMode.Adaptive =>
                ReflectionImplementationMode.Adaptive,
            _ => ReflectionImplementationMode.Adaptive
        };
        if (desired == ReflectionImplementationMode.Legacy)
        {
            return new ReflectionImplementationResolution(
                requested,
                desired,
                ReflectionImplementationFallbackReason.None,
                string.Empty);
        }

        if (!capabilities.AdaptivePipelineAvailable)
        {
            return Fallback(
                requested,
                ReflectionImplementationFallbackReason
                    .AdaptivePipelineUnavailable,
                "The adaptive reflection pipeline is unavailable.");
        }
        if (!capabilities.ReceiverLobeExtensionAvailable)
        {
            return Fallback(
                requested,
                ReflectionImplementationFallbackReason
                    .ReceiverLobeExtensionUnavailable,
                "The adaptive receiver lobe extension is unavailable.");
        }
        if (!capabilities.CompactHistoryAvailable)
        {
            return Fallback(
                requested,
                ReflectionImplementationFallbackReason
                    .CompactHistoryUnavailable,
                "The adaptive compact history resources are unavailable.");
        }

        return new ReflectionImplementationResolution(
            requested,
            ReflectionImplementationMode.Adaptive,
            ReflectionImplementationFallbackReason.None,
            string.Empty);
    }

    private static ReflectionImplementationResolution Fallback(
        ReflectionImplementationMode requested,
        ReflectionImplementationFallbackReason reason,
        string detail) => new(
        requested,
        ReflectionImplementationMode.Legacy,
        reason,
        detail);
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
        ReflectionMode.StaticProbesAndSsr or
        ReflectionMode.StaticProbesAndPlanar or
        ReflectionMode.HybridRayQuery;

    public bool UsesRayQueries => Effective == ReflectionMode.HybridRayQuery;
}

/// <summary>
/// Keeps authored reflection probes as an explicit compatibility path. The
/// adaptive hybrid mode allocates no local-probe cubemap until the user
/// selects a probe-oriented legacy mode; its environment fallback remains
/// available independently.
/// </summary>
public static class ManualReflectionProbePolicy
{
    public static bool IsCompatibilityMode(ReflectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Enabled && settings.MaxProbes > 0 && settings.Mode is
            ReflectionMode.StaticProbes or
            ReflectionMode.StaticProbesAndSsr or
            ReflectionMode.StaticProbesAndPlanar;
    }
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
            not ReflectionMode.StaticProbesAndPlanar and
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

        if (requested != ReflectionMode.StaticProbesAndPlanar &&
            !capabilities.HiZAvailable)
        {
            return DemoteToProbes(
                requested,
                ReflectionFallbackReason.HiZUnavailable,
                "The reverse-Z Hi-Z pyramid is unavailable.");
        }

        if (requested is ReflectionMode.StaticProbesAndSsr or
            ReflectionMode.StaticProbesAndPlanar)
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
    public const float AlwaysFullRoughness = 0.08f;
    public const float MirrorF0Threshold = 0.35f;
    public const float TransmissionImportanceFloor = 0.40f;
    public const float GlossyImportanceFloor = 0.30f;
    public const float MinimumRayImportance = 0.12f;
    public const float BroadImportanceScale = 0.50f;
    public const float DdgiOwnedMinimumRoughness = 0.25f;
    public const float DdgiOwnedMaximumF0 = 0.12f;

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

    public static ReflectionResolutionTier ResolveAdaptiveResolutionTier(
        ReflectionSettings settings,
        float perceptualRoughness,
        float maximumF0,
        float specularOcclusion,
        ReflectionLobeFlags lobeFlags)
    {
        ArgumentNullException.ThrowIfNull(settings);
        float roughness = UnitOrDefault(perceptualRoughness, 1.0f);
        float f0 = UnitOrDefault(maximumF0, 0.0f);
        float occlusion = UnitOrDefault(specularOcclusion, 0.0f);
        ReflectionResolutionTier tier = ResolveResolutionTier(
            settings, roughness);
        if (tier == ReflectionResolutionTier.AnalyticFallback)
            return tier;

        bool startsInFullBand = tier == ReflectionResolutionTier.Full;
        bool transmissive = lobeFlags.HasFlag(
            ReflectionLobeFlags.Transmissive);
        bool broadAnisotropic = lobeFlags.HasFlag(
            ReflectionLobeFlags.BroadAnisotropic);
        bool clearcoat = lobeFlags.HasFlag(ReflectionLobeFlags.Clearcoat);
        if (!transmissive && !clearcoat &&
            roughness >= DdgiOwnedMinimumRoughness &&
            f0 <= DdgiOwnedMaximumF0)
        {
            // A low-F0 broad lobe does not contain enough sharp scene detail
            // to justify SSR or a ray query. Directional DDGI owns it, with
            // the environment retained as the terminal no-data fallback.
            return ReflectionResolutionTier.AnalyticFallback;
        }
        bool requiresFullQuality = roughness <= AlwaysFullRoughness ||
            transmissive || f0 >= MirrorF0Threshold;
        if (tier == ReflectionResolutionTier.Full && !requiresFullQuality)
            tier = Demote(tier);
        if (broadAnisotropic)
            tier = Demote(tier);

        float importanceFloor = transmissive
            ? TransmissionImportanceFloor
            : startsInFullBand
                ? GlossyImportanceFloor
                : 0.0f;
        float remainingGloss = 1.0f - roughness;
        float squaredGloss = remainingGloss * remainingGloss;
        float importance = MathF.Max(f0, importanceFloor) *
            squaredGloss * squaredGloss * occlusion;
        if (broadAnisotropic)
            importance *= BroadImportanceScale;
        if (importance < MinimumRayImportance &&
            (tier != ReflectionResolutionTier.Quarter ||
             !requiresFullQuality))
            tier = Demote(tier);
        return tier;
    }

    private static ReflectionResolutionTier Demote(
        ReflectionResolutionTier tier) => tier switch
        {
            ReflectionResolutionTier.Full => ReflectionResolutionTier.Half,
            ReflectionResolutionTier.Half => ReflectionResolutionTier.Quarter,
            ReflectionResolutionTier.Quarter =>
                ReflectionResolutionTier.AnalyticFallback,
            _ => tier
        };

    private static float UnitOrDefault(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : fallback;

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
    public const uint ScreenTileSize = 8;
    public const uint ReceiverPayloadWords = 4;
    public const uint ReceiverIdentityBits = 22;
    public const uint ReceiverIdentityMask = (1u << 22) - 1u;
    public const uint SpecularOcclusionShift = 22;
    public const uint SpecularOcclusionMask = 0x3fu;
    public const uint LobeFlagsShift = 28;
    public const uint LobeFlagsMask = 0x7u;
    public const uint ReceiverValidBit = 1u << 31;
    public const uint LobeExtensionWords = 2;
    public const uint HistoryMetadataWords = 2;
    public const uint TaskWords = 8;
    public const uint CounterWords = 16;
    public const uint IndirectCommandWords = 3;
    public const uint ExactMissIndirectWordOffset = 6;
    public const uint IndirectArgumentWords =
        ExactMissIndirectWordOffset + IndirectCommandWords;
    public const uint ExactMissTileRecordWords = 4;
    public const float NormalHistoryDotThreshold = 0.9f;
    public const float MinimumHistoryDepthToleranceMeters = 0.02f;
    public const float RelativeHistoryDepthTolerance = 0.01f;
    public const float SsrToRayQueryHistoryWeightScale = 0.35f;
    public const int MaximumPushConstantBytes = 128;

    public static uint CalculateScreenTileCapacity(uint width, uint height)
    {
        ulong tileSize = ScreenTileSize;
        ulong tileCountX = ((ulong)width + tileSize - 1UL) / tileSize;
        ulong tileCountY = ((ulong)height + tileSize - 1UL) / tileSize;
        return checked((uint)(tileCountX * tileCountY));
    }
}

public enum ReflectionSparseHistoryState : uint
{
    None = 0,
    ResolutionCadence = 1,
    RayBudget = 2,
    Reserved = 3
}

public readonly record struct HybridReflectionPackedHistoryMetadata(
    uint X,
    uint Y);

public readonly record struct HybridReflectionHistoryMetadata(
    uint ReceiverIdentity,
    float Depth,
    Vector3 Normal,
    ReflectionSource Source,
    uint Age,
    ReflectionSparseHistoryState SparseState);

/// <summary>CPU mirror of the compact 64-bit temporal metadata ABI.</summary>
public static class HybridReflectionHistoryMetadataCodec
{
    public static HybridReflectionPackedHistoryMetadata Pack(
        in HybridReflectionHistoryMetadata value)
    {
        if (value.Source == ReflectionSource.None)
            return default;
        uint identity = value.ReceiverIdentity &
            HybridReflectionGpuContract.ReceiverIdentityMask;
        float depth = float.IsFinite(value.Depth)
            ? Math.Clamp(value.Depth, 0.0f, 1.0f)
            : 0.0f;
        uint depth16 = BitConverter.HalfToUInt16Bits((Half)depth);
        uint normal16 = PackOct8(value.Normal);
        uint x = identity | ((depth16 & 0x03ffu) << 22);
        uint y = ((depth16 >> 10) & 0x003fu) |
            (normal16 << 6) |
            (((uint)value.Source & 0x7u) << 22) |
            ((Math.Min(value.Age, 31u) & 0x1fu) << 25) |
            (((uint)value.SparseState & 0x3u) << 30);
        return new HybridReflectionPackedHistoryMetadata(x, y);
    }

    public static bool TryDecode(
        in HybridReflectionPackedHistoryMetadata packed,
        out HybridReflectionHistoryMetadata value)
    {
        ReflectionSource source = (ReflectionSource)
            ((packed.Y >> 22) & 0x7u);
        if (source == ReflectionSource.None ||
            !Enum.IsDefined(source))
        {
            value = default;
            return false;
        }
        uint depth16 = ((packed.X >> 22) & 0x03ffu) |
            ((packed.Y & 0x003fu) << 10);
        value = new HybridReflectionHistoryMetadata(
            packed.X & HybridReflectionGpuContract.ReceiverIdentityMask,
            (float)BitConverter.UInt16BitsToHalf((ushort)depth16),
            UnpackOct8((packed.Y >> 6) & 0xffffu),
            source,
            (packed.Y >> 25) & 0x1fu,
            (ReflectionSparseHistoryState)(packed.Y >> 30));
        return true;
    }

    private static uint PackOct8(Vector3 value)
    {
        Vector3 normal = NormalizeOrFallback(value, Vector3.UnitZ);
        float inverseL1 = 1.0f /
            (MathF.Abs(normal.X) + MathF.Abs(normal.Y) +
             MathF.Abs(normal.Z));
        float x = normal.X * inverseL1;
        float y = normal.Y * inverseL1;
        if (normal.Z < 0.0f)
        {
            float oldX = x;
            x = (1.0f - MathF.Abs(y)) * MathF.CopySign(1.0f, oldX);
            y = (1.0f - MathF.Abs(oldX)) * MathF.CopySign(1.0f, y);
        }
        byte packedX = unchecked((byte)PackSnorm8(x));
        byte packedY = unchecked((byte)PackSnorm8(y));
        return packedX | ((uint)packedY << 8);
    }

    private static Vector3 UnpackOct8(uint packed)
    {
        float x = UnpackSnorm8((byte)packed);
        float y = UnpackSnorm8((byte)(packed >> 8));
        Vector3 normal = new(x, y,
            1.0f - MathF.Abs(x) - MathF.Abs(y));
        if (normal.Z < 0.0f)
        {
            float oldX = normal.X;
            normal.X = (1.0f - MathF.Abs(normal.Y)) *
                MathF.CopySign(1.0f, oldX);
            normal.Y = (1.0f - MathF.Abs(oldX)) *
                MathF.CopySign(1.0f, normal.Y);
        }
        return NormalizeOrFallback(normal, Vector3.UnitZ);
    }

    private static sbyte PackSnorm8(float value) => checked((sbyte)Math.Clamp(
        (int)MathF.Round(Math.Clamp(value, -1.0f, 1.0f) * 127.0f),
        -127,
        127));

    private static float UnpackSnorm8(byte value) => Math.Max(
        -1.0f,
        unchecked((sbyte)value) / 127.0f);

    internal static Vector3 NormalizeOrFallback(
        Vector3 value,
        Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(lengthSquared) &&
               lengthSquared > 1.0e-12f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }
}

public readonly record struct HybridReflectionPackedLobeExtension(
    uint ClearcoatNormal,
    uint Parameters);

public readonly record struct HybridReflectionLobeExtension(
    Vector3 ClearcoatNormal,
    float ClearcoatFactor,
    float ClearcoatRoughness,
    float AnisotropyStrength,
    float TangentAzimuth);

/// <summary>CPU mirror of the RG32UI forward lobe-extension ABI.</summary>
public static class HybridReflectionLobeExtensionCodec
{
    public static HybridReflectionPackedLobeExtension Pack(
        in HybridReflectionLobeExtension value)
    {
        Vector3 normal = HybridReflectionHistoryMetadataCodec
            .NormalizeOrFallback(value.ClearcoatNormal, Vector3.UnitZ);
        uint packedNormal = SimpleDdgiTransportCachePacking
            .PackOctahedralSnorm16(new System.Numerics.Vector3(
                normal.X,
                normal.Y,
                normal.Z));
        uint parameters = PackUnorm8(value.ClearcoatFactor) |
            (PackUnorm8(value.ClearcoatRoughness) << 8) |
            (PackUnorm8(MathF.Abs(value.AnisotropyStrength)) << 16) |
            (PackUnorm8(value.TangentAzimuth) << 24);
        return new HybridReflectionPackedLobeExtension(
            packedNormal,
            parameters);
    }

    public static HybridReflectionLobeExtension Decode(
        in HybridReflectionPackedLobeExtension packed)
    {
        System.Numerics.Vector3 normal = SimpleDdgiTransportCachePacking
            .UnpackOctahedralSnorm16(packed.ClearcoatNormal);
        return new HybridReflectionLobeExtension(
            new Vector3(normal.X, normal.Y, normal.Z),
            UnpackUnorm8(packed.Parameters),
            UnpackUnorm8(packed.Parameters >> 8),
            UnpackUnorm8(packed.Parameters >> 16),
            UnpackUnorm8(packed.Parameters >> 24));
    }

    private static uint PackUnorm8(float value) => checked((uint)Math.Clamp(
        (int)MathF.Round((float.IsFinite(value)
            ? Math.Clamp(value, 0.0f, 1.0f)
            : 0.0f) * 255.0f),
        0,
        255));

    private static float UnpackUnorm8(uint value) =>
        (value & 0xffu) / 255.0f;
}

public static class HybridReflectionReceiverPayloadCodec
{
    public static uint PackIdentityAndFlags(
        uint identity,
        float specularOcclusion,
        ReflectionLobeFlags lobeFlags,
        bool valid = true)
    {
        uint occlusion = checked((uint)Math.Clamp(
            (int)MathF.Round((float.IsFinite(specularOcclusion)
                ? Math.Clamp(specularOcclusion, 0.0f, 1.0f)
                : 0.0f) * HybridReflectionGpuContract
                .SpecularOcclusionMask),
            0,
            (int)HybridReflectionGpuContract.SpecularOcclusionMask));
        return identity & HybridReflectionGpuContract.ReceiverIdentityMask |
            occlusion << (int)HybridReflectionGpuContract
                .SpecularOcclusionShift |
            ((uint)lobeFlags & HybridReflectionGpuContract.LobeFlagsMask) <<
                (int)HybridReflectionGpuContract.LobeFlagsShift |
            (valid ? HybridReflectionGpuContract.ReceiverValidBit : 0u);
    }
}

public readonly record struct HybridReflectionCounterSnapshot(
    int ReadbackValid,
    uint SsrHits,
    uint RayRequests,
    uint RayQueries,
    uint RayOverflows,
    uint RayHits,
    uint RayMisses,
    uint DdgiFallbacks,
    uint ProbeFallbacks,
    uint EnvironmentFallbacks,
    uint FullRateTiles,
    uint HalfRateTiles,
    uint QuarterRateTiles,
    uint AnalyticTiles,
    uint ReuseTiles,
    uint ActiveTiles,
    uint TileOverflows)
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
    uint DdgiTopologyGeneration,
    uint MaterialRevision,
    ulong ReflectionProbeRevision,
    uint EnvironmentGeneration,
    ulong CameraCutSerial,
    ReflectionImplementationMode ImplementationMode =
        ReflectionImplementationMode.Adaptive,
    uint HistoryMetadataAbiVersion =
        ReflectionSettings.HistoryMetadataAbiVersion,
    uint PlanarGeneration = 0u)
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
        if (ImplementationMode != previous.ImplementationMode)
            reasons |= ReflectionHistoryResetReason.ImplementationChanged;
        if (ReceiverPayloadAbiVersion != previous.ReceiverPayloadAbiVersion)
            reasons |= ReflectionHistoryResetReason.ReceiverPayloadAbiChanged;
        if (HistoryMetadataAbiVersion != previous.HistoryMetadataAbiVersion)
            reasons |= ReflectionHistoryResetReason.HistoryMetadataAbiChanged;
        if (CameraCutSerial != previous.CameraCutSerial)
            reasons |= ReflectionHistoryResetReason.CameraCut;
        return reasons;
    }

    public ReflectionHistorySourceInvalidation ResolveSourceInvalidations(
        in HybridReflectionHistoryRevision previous,
        bool hasHistory)
    {
        if (!hasHistory)
            return ReflectionHistorySourceInvalidation.None;

        ReflectionHistorySourceInvalidation result =
            ReflectionHistorySourceInvalidation.None;
        if (RaySceneGeneration != previous.RaySceneGeneration)
            result |= ReflectionHistorySourceInvalidation.RayScene;
        if (DdgiTopologyGeneration != previous.DdgiTopologyGeneration)
            result |= ReflectionHistorySourceInvalidation.Ddgi;
        // Material revision participates in each receiver identity. A changed
        // material therefore invalidates only pixels that actually reference
        // it instead of flushing every reflection source globally.
        if (ReflectionProbeRevision != previous.ReflectionProbeRevision)
            result |= ReflectionHistorySourceInvalidation.LocalProbe;
        if (EnvironmentGeneration != previous.EnvironmentGeneration)
            result |= ReflectionHistorySourceInvalidation.Environment;
        if (PlanarGeneration != previous.PlanarGeneration)
            result |= ReflectionHistorySourceInvalidation.Planar;
        return result;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionDdgiPushConstants
{
    public Matrix4x4 InverseViewProjectionMatrix;
    public Vector4 CameraPositionAndPadding;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint ReceiverScale;
    public uint MaximumSurfaceGroupsPerTile;
    public float MinimumConfidence;
    public float NormalDotThreshold;
    public float MinimumWorldDepthTolerance;
    public float RelativeWorldDepthTolerance;
    public uint UseActiveTileList;
    public uint ForceExactReconstruction;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionClassifyPushConstants
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public float FullResolutionRoughness;
    public float HalfResolutionRoughness;
    public float QuarterResolutionRoughness;
    public float MaximumReuseMotionPixels;
    public uint HistoryValid;
    public uint SourceInvalidations;
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
    public float AnalyticTransitionStartRoughness;
    public float AnalyticTransitionEndRoughness;
    public uint DdgiBaseAvailable;
    public uint CurrentFrameIndex;
    public uint EffectiveReflectionMode;
    public uint UseActiveTileList;
    public uint ManualProbeFallbackEnabled;
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
    public uint CameraOnlyReprojection;
    public uint SourceInvalidations;
    public uint UseActiveTileList;
}

[StructLayout(LayoutKind.Sequential)]
public struct GPUHybridReflectionSpatialPushConstants
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint Iteration;
    public uint UseActiveTileList;
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
    public float FullResolutionRoughness;
    public float HalfResolutionRoughness;
    public float QuarterResolutionRoughness;
    public uint UseActiveTileList;
}

/// <summary>
/// CPU reference for the reverse-Z screen-space rules used by
/// <c>hybrid_reflection_ssr.comp</c>.  The shader deliberately mirrors these
/// small, side-effect-free operations so boundary and refinement behaviour can
/// be tested without a GPU.
/// </summary>
public static class HybridReflectionReverseZReference
{
    public const int SecantIterationCount = 4;

    public static bool TryClipScreenSegment(
        Vector2 startUv,
        Vector2 endUv,
        ref float minimumDistance,
        ref float maximumDistance)
    {
        if (!IsFinite(startUv) || !IsFinite(endUv) ||
            !float.IsFinite(minimumDistance) ||
            !float.IsFinite(maximumDistance) ||
            maximumDistance <= minimumDistance)
        {
            return false;
        }

        float minimumT = 0.0f;
        float maximumT = 1.0f;
        if (!ClipAxis(startUv.X, endUv.X, ref minimumT, ref maximumT) ||
            !ClipAxis(startUv.Y, endUv.Y, ref minimumT, ref maximumT))
        {
            return false;
        }

        float range = maximumDistance - minimumDistance;
        float clippedMinimum = minimumDistance + range *
            Math.Clamp(minimumT, 0.0f, 1.0f);
        float clippedMaximum = minimumDistance + range *
            Math.Clamp(maximumT, 0.0f, 1.0f);
        minimumDistance = clippedMinimum;
        maximumDistance = clippedMaximum;
        return maximumDistance > minimumDistance + 1.0e-5f;
    }

    public static bool IsPossibleIntersection(
        float rayReverseZ,
        float farthestSceneReverseZ,
        float reverseZThickness) =>
        float.IsFinite(rayReverseZ) &&
        float.IsFinite(farthestSceneReverseZ) &&
        float.IsFinite(reverseZThickness) &&
        farthestSceneReverseZ > 0.0f &&
        rayReverseZ <= farthestSceneReverseZ +
            Math.Max(reverseZThickness, 0.0f);

    public static uint SelectFootprintMip(
        float projectedFootprintPixels,
        uint mipCount)
    {
        if (mipCount == 0u)
            return 0u;
        float footprint = float.IsFinite(projectedFootprintPixels)
            ? Math.Max(projectedFootprintPixels, 1.0f)
            : 1.0f;
        uint mip = checked((uint)Math.Max(
            MathF.Floor(MathF.Log2(footprint)), 0.0f));
        return Math.Min(mip, mipCount - 1u);
    }

    public static float ResolveViewSpaceThickness(
        float projectedPixelWorldSize,
        uint resolutionTier)
    {
        float pixelSize = float.IsFinite(projectedPixelWorldSize)
            ? Math.Max(projectedPixelWorldSize, 0.0f)
            : 0.0f;
        return Math.Max(0.01f,
            pixelSize * (2.0f + Math.Max(resolutionTier, 1u)));
    }

    /// <summary>
    /// Refines a bracket where a negative difference is in front of geometry
    /// and a non-negative difference is at/behind it. Invalid samples retain
    /// the conservative back boundary.
    /// </summary>
    public static float RefineSecant(
        float frontDistance,
        float backDistance,
        Func<float, float> depthDifference,
        int iterations = SecantIterationCount)
    {
        ArgumentNullException.ThrowIfNull(depthDifference);
        float front = Math.Max(frontDistance, 0.0f);
        float back = Math.Max(backDistance, front + 1.0e-5f);
        float frontDifference = depthDifference(front);
        float backDifference = depthDifference(back);
        if (!float.IsFinite(frontDifference) ||
            !float.IsFinite(backDifference))
        {
            return back;
        }

        for (int iteration = 0;
             iteration < Math.Clamp(iterations, 0, 32);
             iteration++)
        {
            float denominator = backDifference - frontDifference;
            float candidate = MathF.Abs(denominator) > 1.0e-7f
                ? (front * backDifference - back * frontDifference) /
                    denominator
                : 0.5f * (front + back);
            candidate = Math.Clamp(candidate,
                front + (back - front) * 0.1f,
                back - (back - front) * 0.1f);
            float difference = depthDifference(candidate);
            if (!float.IsFinite(difference))
                break;
            if (difference >= 0.0f)
            {
                back = candidate;
                backDifference = difference;
            }
            else
            {
                front = candidate;
                frontDifference = difference;
            }
        }
        return back;
    }

    private static bool ClipAxis(
        float start,
        float end,
        ref float minimumT,
        ref float maximumT)
    {
        float delta = end - start;
        if (MathF.Abs(delta) <= 1.0e-8f)
            return start is >= 0.0f and <= 1.0f;
        float first = -start / delta;
        float second = (1.0f - start) / delta;
        minimumT = Math.Max(minimumT, Math.Min(first, second));
        maximumT = Math.Min(maximumT, Math.Max(first, second));
        return maximumT >= minimumT;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}

public readonly record struct HybridReflectionAnisotropicAxes(
    float AlphaX,
    float AlphaY);

/// <summary>Deterministic CPU mirror of the temporally rotated Heitz VNDF.</summary>
public static class HybridReflectionVndfReference
{
    public const float DeterministicMirrorRoughness = 0.06f;

    public static HybridReflectionAnisotropicAxes ResolveAxes(
        float roughness,
        float anisotropyStrength)
    {
        float sanitizedRoughness = float.IsFinite(roughness)
            ? Math.Clamp(roughness, 0.0f, 1.0f)
            : 1.0f;
        float strength = float.IsFinite(anisotropyStrength)
            ? Math.Clamp(anisotropyStrength, 0.0f, 1.0f)
            : 0.0f;
        float alpha = Math.Max(
            sanitizedRoughness * sanitizedRoughness, 0.001f);
        float aspect = MathF.Sqrt(Math.Max(1.0f - 0.9f * strength, 0.1f));
        return new HybridReflectionAnisotropicAxes(
            Math.Max(alpha / aspect, 0.001f),
            Math.Max(alpha * aspect, 0.001f));
    }

    public static uint CreateSeed(
        uint receiverIdentity,
        uint pixelX,
        uint pixelY,
        uint temporalSampleIndex,
        uint lobeId) =>
        receiverIdentity ^ pixelX * 0x9e3779b9u ^
        pixelY * 0x85ebca6bu ^
        temporalSampleIndex * 0xc2b2ae35u ^
        lobeId * 0x27d4eb2fu;

    public static Vector2 Random2(uint seed)
    {
        uint first = Hash(seed);
        uint second = Hash(first ^ 0x68bc21ebu);
        const float scale = 1.0f / 16_777_216.0f;
        return new Vector2(
            (first & 0x00ffffffu) * scale,
            (second & 0x00ffffffu) * scale);
    }

    public static Vector3 SampleDirection(
        Vector3 viewDirection,
        Vector3 normal,
        Vector3 tangent,
        float roughness,
        float anisotropyStrength,
        uint receiverIdentity,
        uint pixelX,
        uint pixelY,
        uint temporalSampleIndex,
        uint lobeId)
    {
        Vector3 n = Normalize(normal, Vector3.UnitZ);
        Vector3 view = Normalize(viewDirection, n);
        Vector3 mirror = Normalize(Reflect(-view, n), n);
        if (!float.IsFinite(roughness) ||
            roughness <= DeterministicMirrorRoughness)
        {
            return mirror;
        }

        Vector3 t = tangent - n * Vector3.Dot(tangent, n);
        t = t.LengthSquared() <= 1.0e-12f
            ? CanonicalTangent(n)
            : Normalize(t, CanonicalTangent(n));
        Vector3 b = Normalize(Vector3.Cross(n, t), Vector3.UnitY);
        HybridReflectionAnisotropicAxes axes = ResolveAxes(
            roughness, anisotropyStrength);
        Vector3 localView = new(
            Vector3.Dot(view, t),
            Vector3.Dot(view, b),
            Math.Max(Vector3.Dot(view, n), 1.0e-5f));
        Vector3 stretchedView = Normalize(new Vector3(
            axes.AlphaX * localView.X,
            axes.AlphaY * localView.Y,
            localView.Z), Vector3.UnitZ);
        float lensq = stretchedView.X * stretchedView.X +
            stretchedView.Y * stretchedView.Y;
        Vector3 basis1 = lensq > 1.0e-10f
            ? new Vector3(-stretchedView.Y, stretchedView.X, 0.0f) /
                MathF.Sqrt(lensq)
            : Vector3.UnitX;
        Vector3 basis2 = Vector3.Cross(stretchedView, basis1);
        Vector2 random = Random2(CreateSeed(
            receiverIdentity,
            pixelX,
            pixelY,
            temporalSampleIndex,
            lobeId));
        float radius = MathF.Sqrt(random.X);
        float phi = 2.0f * MathF.PI * random.Y;
        float diskX = radius * MathF.Cos(phi);
        float diskY = radius * MathF.Sin(phi);
        float blend = 0.5f * (1.0f + stretchedView.Z);
        diskY = Lerp(
            MathF.Sqrt(Math.Max(0.0f, 1.0f - diskX * diskX)),
            diskY,
            blend);
        Vector3 visibleNormal = diskX * basis1 + diskY * basis2 +
            MathF.Sqrt(Math.Max(
                0.0f,
                1.0f - diskX * diskX - diskY * diskY)) * stretchedView;
        Vector3 localHalf = Normalize(new Vector3(
            axes.AlphaX * visibleNormal.X,
            axes.AlphaY * visibleNormal.Y,
            Math.Max(visibleNormal.Z, 0.0f)), Vector3.UnitZ);
        Vector3 halfVector = Normalize(
            t * localHalf.X + b * localHalf.Y + n * localHalf.Z,
            n);
        Vector3 direction = Normalize(Reflect(-view, halfVector), mirror);
        return Vector3.Dot(direction, n) > 0.0f ? direction : mirror;
    }

    public static Vector3 CanonicalTangent(Vector3 normal)
    {
        Vector3 n = Normalize(normal, Vector3.UnitZ);
        Vector3 reference = MathF.Abs(n.Z) < 0.999f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        return Normalize(Vector3.Cross(reference, n), Vector3.UnitX);
    }

    public static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    private static Vector3 Reflect(Vector3 incident, Vector3 normal) =>
        incident - 2.0f * Vector3.Dot(incident, normal) * normal;

    private static Vector3 Normalize(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1.0e-12f &&
               float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z)
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    private static float Lerp(float first, float second, float amount) =>
        first + (second - first) * amount;
}

/// <summary>CPU reference for passive clearcoat-over-base composition.</summary>
public static class HybridReflectionLayerEnergyReference
{
    public static float ClearcoatFresnel(
        float normalDotView,
        float clearcoatFactor)
    {
        float cosine = float.IsFinite(normalDotView)
            ? Math.Clamp(normalDotView, 0.0f, 1.0f)
            : 0.0f;
        float factor = float.IsFinite(clearcoatFactor)
            ? Math.Clamp(clearcoatFactor, 0.0f, 1.0f)
            : 0.0f;
        return factor * (0.04f + 0.96f *
            MathF.Pow(1.0f - cosine, 5.0f));
    }

    public static Vector3 Compose(
        Vector3 baseRadiance,
        Vector3 clearcoatRadiance,
        float normalDotView,
        float clearcoatFactor)
    {
        float fresnel = ClearcoatFresnel(
            normalDotView, clearcoatFactor);
        Vector3 result = baseRadiance * (1.0f - fresnel) +
            clearcoatRadiance * fresnel;
        return new Vector3(
            Sanitize(result.X),
            Sanitize(result.Y),
            Sanitize(result.Z));
    }

    public static float WhiteFurnaceError(
        float normalDotView,
        float clearcoatFactor)
    {
        Vector3 value = Compose(
            Vector3.One,
            Vector3.One,
            normalDotView,
            clearcoatFactor);
        return Math.Max(
            MathF.Abs(value.X - 1.0f),
            Math.Max(
                MathF.Abs(value.Y - 1.0f),
                MathF.Abs(value.Z - 1.0f)));
    }

    private static float Sanitize(float value) =>
        float.IsFinite(value) ? Math.Max(value, 0.0f) : 0.0f;
}

public enum HybridReflectionQualityStep : uint
{
    ProtectedMinimum = 0,
    Reduced = 1,
    Balanced = 2,
    Full = 3
}

public readonly record struct HybridReflectionBudgetSample(
    long CompletedGpuMicroseconds,
    bool TimingValid,
    bool VerifiedTaskOverflow);

public readonly record struct HybridReflectionBudgetDecision(
    HybridReflectionQualityStep QualityStep,
    double EwmaGpuMilliseconds,
    float LowImportanceRayAdmissionScale,
    float SecondSpatialVarianceThreshold,
    uint BroadTileCadence,
    float LowPriorityPlanarScale,
    uint LowPriorityPlanarCadence,
    bool Changed,
    string Reason);

/// <summary>
/// Completed-timestamp-only quality controller for adaptive reflections.  It
/// never removes protected mirror/transmission work or the environment
/// fallback; only low-importance optional work is stepped.
/// </summary>
public sealed class HybridReflectionBudgetController
{
    public const double EwmaAlpha = 0.1;
    public const double TargetGpuMilliseconds = 3.25;
    public const double DeadBandFraction = 0.08;
    public const int MinimumFramesBetweenChanges = 8;
    public const int UnderBudgetFramesBeforeIncrease = 32;

    private HybridReflectionQualityStep _quality =
        HybridReflectionQualityStep.Full;
    private double _ewmaMilliseconds;
    private bool _hasEwma;
    private int _framesSinceChange = MinimumFramesBetweenChanges;
    private int _underBudgetFrames;

    public HybridReflectionBudgetDecision Current => CreateDecision(
        changed: false,
        "quality state retained");

    public HybridReflectionBudgetDecision Observe(
        in HybridReflectionBudgetSample sample)
    {
        if (!sample.TimingValid || sample.CompletedGpuMicroseconds < 0)
            return CreateDecision(false, "completed GPU timing unavailable");

        double milliseconds = sample.CompletedGpuMicroseconds / 1000.0;
        _ewmaMilliseconds = _hasEwma
            ? _ewmaMilliseconds + EwmaAlpha *
                (milliseconds - _ewmaMilliseconds)
            : milliseconds;
        _hasEwma = true;
        _framesSinceChange++;

        if (sample.VerifiedTaskOverflow &&
            _quality > HybridReflectionQualityStep.ProtectedMinimum)
        {
            _quality--;
            ResetChangeWindow();
            return CreateDecision(true, "verified task overflow");
        }

        double lower = TargetGpuMilliseconds * (1.0 - DeadBandFraction);
        double upper = TargetGpuMilliseconds * (1.0 + DeadBandFraction);
        if (_ewmaMilliseconds > upper)
        {
            _underBudgetFrames = 0;
            if (_framesSinceChange >= MinimumFramesBetweenChanges &&
                _quality > HybridReflectionQualityStep.ProtectedMinimum)
            {
                _quality--;
                ResetChangeWindow();
                return CreateDecision(true, "reflection EWMA exceeded budget");
            }
            return CreateDecision(false, "over budget; cadence window active");
        }

        if (_ewmaMilliseconds < lower)
        {
            _underBudgetFrames++;
            if (_underBudgetFrames >= UnderBudgetFramesBeforeIncrease &&
                _framesSinceChange >= MinimumFramesBetweenChanges &&
                _quality < HybridReflectionQualityStep.Full)
            {
                _quality++;
                ResetChangeWindow();
                return CreateDecision(true,
                    "32 completed frames remained under budget");
            }
            return CreateDecision(false, "under-budget history accumulating");
        }

        _underBudgetFrames = 0;
        return CreateDecision(false, "inside the 8% dead band");
    }

    public void Reset(
        HybridReflectionQualityStep quality =
            HybridReflectionQualityStep.Full)
    {
        _quality = Enum.IsDefined(quality)
            ? quality
            : HybridReflectionQualityStep.Full;
        _ewmaMilliseconds = 0.0;
        _hasEwma = false;
        _framesSinceChange = MinimumFramesBetweenChanges;
        _underBudgetFrames = 0;
    }

    private HybridReflectionBudgetDecision CreateDecision(
        bool changed,
        string reason)
    {
        (float rayScale, float variance, uint tileCadence,
            float planarScale, uint planarCadence) = _quality switch
        {
            HybridReflectionQualityStep.ProtectedMinimum =>
                (0.35f, 0.040f, 4u, 0.25f, 4u),
            HybridReflectionQualityStep.Reduced =>
                (0.55f, 0.020f, 2u, 0.25f, 2u),
            HybridReflectionQualityStep.Balanced =>
                (0.80f, 0.010f, 1u, 0.375f, 2u),
            _ => (1.00f, 0.005f, 1u, 0.50f, 1u)
        };
        return new HybridReflectionBudgetDecision(
            _quality,
            _hasEwma ? _ewmaMilliseconds : 0.0,
            rayScale,
            variance,
            tileCadence,
            planarScale,
            planarCadence,
            changed,
            reason);
    }

    private void ResetChangeWindow()
    {
        _framesSinceChange = 0;
        _underBudgetFrames = 0;
    }
}
