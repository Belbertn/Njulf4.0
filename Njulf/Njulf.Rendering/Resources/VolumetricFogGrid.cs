using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public enum VolumetricFogEnergyOwner : uint
{
    DirectSources = 0,
    BouncedDdgi = 1,
    MultipleScattering = 2
}

public readonly record struct VolumetricFogQualityProfile(
    uint PixelSize,
    uint MaximumWidth,
    uint MaximumHeight,
    uint DepthSlices,
    // Source lists are deliberately coarser than lighting. Their bounds are
    // conservative, so larger cells reduce list memory without dropping a
    // volume or particle that can affect a froxel.
    uint ClusterWidth,
    uint ClusterHeight,
    uint ClusterDepth,
    uint LightingWidth,
    uint LightingHeight,
    uint LightingDepth,
    uint ResolveDivisor,
    uint MaximumLocalVolumes,
    uint MaximumParticleSources,
    uint ClusterReferenceCapacity,
    uint MaximumLightsPerCluster,
    uint EmissiveCandidatesPerCluster,
    uint MultipleScatteringIterations,
    float MaximumHistoryWeight)
{
    public static VolumetricFogQualityProfile ForPreset(
        RenderQualityPreset preset) => preset switch
    {
        RenderQualityPreset.Ultra => new VolumetricFogQualityProfile(
            6, 320, 200, 104,
            8, 8, 8,
            8, 8, 8,
            2,
            512, 131_072, 128, 96, 4, 2, 0.95f),
        RenderQualityPreset.High or RenderQualityPreset.DdgiHigh =>
            new VolumetricFogQualityProfile(
                8, 256, 128, 80,
                8, 8, 8,
                8, 8, 8,
                2,
                256, 65_536, 192, 64, 2, 0, 0.90f),
        _ => default
    };

    public bool Enabled => DepthSlices != 0;
}

public readonly record struct VolumetricFogGridLayout(
    uint Width,
    uint Height,
    uint Depth,
    uint ClusterWidth,
    uint ClusterHeight,
    uint ClusterDepth,
    uint LightingWidth,
    uint LightingHeight,
    uint LightingDepth,
    uint ResolveWidth,
    uint ResolveHeight,
    float NearDistance,
    float FarDistance,
    uint PixelSize,
    uint GuardBandPixels)
{
    public ulong FroxelCount => checked((ulong)Width * Height * Depth);
    public ulong ClusterCount => checked((ulong)ClusterWidth * ClusterHeight * ClusterDepth);
    public ulong LightingCount => checked(
        (ulong)LightingWidth * LightingHeight * LightingDepth);
    public ulong ResolvePixelCount => checked((ulong)ResolveWidth * ResolveHeight);

    public float SliceBoundary(uint slice, float jitter = 0f)
    {
        if (Depth == 0)
            throw new InvalidOperationException("A disabled froxel layout has no slice boundaries.");
        float normalized = Math.Clamp((slice + jitter) / Depth, 0f, 1f);
        return NearDistance * MathF.Pow(FarDistance / NearDistance, normalized);
    }

    public float SliceCenter(uint slice, float jitter = 0.5f) =>
        SliceBoundary(Math.Min(slice, Depth - 1), jitter);

    public float ContinuousSlice(float viewDistance)
    {
        float distance = Math.Clamp(viewDistance, NearDistance, FarDistance);
        float normalized = MathF.Log(distance / NearDistance) /
            MathF.Log(FarDistance / NearDistance);
        return normalized * Depth;
    }

    public static VolumetricFogGridLayout Create(
        uint sceneWidth,
        uint sceneHeight,
        float cameraNear,
        float cameraFar,
        float maximumDistance,
        VolumetricFogQualityProfile profile)
    {
        if (!profile.Enabled || sceneWidth == 0 || sceneHeight == 0)
            return default;

        const uint guardBandPixels = 4;
        uint horizontalCells = profile.MaximumWidth > guardBandPixels * 2
            ? profile.MaximumWidth - guardBandPixels * 2
            : 1u;
        uint verticalCells = profile.MaximumHeight > guardBandPixels * 2
            ? profile.MaximumHeight - guardBandPixels * 2
            : 1u;
        uint pixelSize = Math.Max(
            profile.PixelSize,
            Math.Max(
                DivideRoundUp(sceneWidth, horizontalCells),
                DivideRoundUp(sceneHeight, verticalCells)));
        uint width = Math.Min(
            profile.MaximumWidth,
            DivideRoundUp(sceneWidth, pixelSize) + guardBandPixels * 2);
        uint height = Math.Min(
            profile.MaximumHeight,
            DivideRoundUp(sceneHeight, pixelSize) + guardBandPixels * 2);
        float near = MathF.Max(float.IsFinite(cameraNear) ? cameraNear : 0.05f, 0.05f);
        float farCandidate = MathF.Min(
            float.IsFinite(cameraFar) ? cameraFar : maximumDistance,
            float.IsFinite(maximumDistance) ? maximumDistance : cameraFar);
        float far = MathF.Max(farCandidate, near + 0.01f);

        return new VolumetricFogGridLayout(
            width,
            height,
            profile.DepthSlices,
            DivideRoundUp(width, profile.ClusterWidth),
            DivideRoundUp(height, profile.ClusterHeight),
            DivideRoundUp(profile.DepthSlices, profile.ClusterDepth),
            DivideRoundUp(width, profile.LightingWidth),
            DivideRoundUp(height, profile.LightingHeight),
            DivideRoundUp(profile.DepthSlices, profile.LightingDepth),
            DivideRoundUp(sceneWidth, Math.Max(profile.ResolveDivisor, 1u)),
            DivideRoundUp(sceneHeight, Math.Max(profile.ResolveDivisor, 1u)),
            near,
            far,
            pixelSize,
            guardBandPixels);
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);
}

public readonly record struct VolumetricFogMemoryPlan(
    ulong MediumBytes,
    ulong LightingBytes,
    ulong HistoryBytes,
    ulong IntegrationBytes,
    ulong SelfShadowBytes,
    ulong SourceBytes,
    ulong MultipleScatteringBytes,
    ulong NoiseBytes)
{
    public ulong TotalBytes => checked(
        MediumBytes + LightingBytes + HistoryBytes + IntegrationBytes +
        SelfShadowBytes + SourceBytes + MultipleScatteringBytes + NoiseBytes);

    public static VolumetricFogMemoryPlan Create(
        VolumetricFogGridLayout layout,
        VolumetricFogQualityProfile profile)
    {
        ulong clusters = layout.ClusterCount;
        // Optimal-tiled layered images are physically padded by common Vulkan
        // drivers. Model the observed portable allocation granularity instead
        // of admitting against tightly packed texel counts that the allocator
        // can never achieve.
        ulong fullRgba16 = EstimateLayeredImageBytes(
            layout.Width, layout.Height, layout.Depth, 8UL);
        ulong fullR16 = EstimateLayeredImageBytes(
            layout.Width, layout.Height, layout.Depth, 2UL);
        ulong lightingRgba16 = EstimateLayeredImageBytes(
            layout.LightingWidth,
            layout.LightingHeight,
            layout.LightingDepth,
            8UL);
        ulong clusterRgba16 = EstimateLayeredImageBytes(
            layout.ClusterWidth,
            layout.ClusterHeight,
            layout.ClusterDepth,
            8UL);
        ulong resolvedRgba16 = EstimateLayeredImageBytes(
            layout.ResolveWidth, layout.ResolveHeight, 1u, 8UL);
        // RGBA16F medium/integrated alias plus RGBA16F velocity/weighted-g.
        ulong medium = checked(fullRgba16 * 2UL);
        // RGBA16F direct, indirect, and reduced-medium lighting caches.
        ulong lighting = checked(lightingRgba16 * 3UL);
        // Two RGBA16F temporal banks plus two R16F confidence banks.
        ulong history = checked(fullRgba16 * 2UL + fullR16 * 2UL);
        // The full froxel integration aliases Medium after temporal resolve;
        // only the half-resolution resolved fog surface is additional.
        ulong integration = resolvedRgba16;
        // One RGBA16F coarse transmittance record per source cluster.
        ulong selfShadow = clusterRgba16;
        // Two frames of authored-volume records, cluster counters, and the
        // bounded per-profile source-reference lists. Particle instances are
        // consumed in-place from the canonical CPU/GPU render buffers.
        ulong source = checked(
            AlignUp(profile.MaximumLocalVolumes * 128UL, 256UL) * 2UL +
            AlignUp((clusters + 1UL) * sizeof(uint), 256UL) * 2UL +
            AlignUp(clusters * profile.ClusterReferenceCapacity *
                sizeof(uint), 256UL) * 2UL +
            1_024UL + 384UL);
        ulong multiple = profile.MultipleScatteringIterations == 0
            ? 0UL
            : checked(lightingRgba16 * 2UL);
        const ulong noise = 64UL * 64UL * 64UL * 4UL;
        return new VolumetricFogMemoryPlan(
            medium,
            lighting,
            history,
            integration,
            selfShadow,
            source,
            multiple,
            noise);
    }

    private static ulong EstimateLayeredImageBytes(
        uint width,
        uint height,
        uint layers,
        ulong bytesPerTexel)
    {
        ulong rowBytes = AlignUp(checked(width * bytesPerTexel), 64UL);
        ulong paddedHeight = height <= 256u
            ? NextPowerOfTwo(height)
            : AlignUp(height, 256UL);
        return checked(rowBytes * paddedHeight * layers);
    }

    private static ulong NextPowerOfTwo(uint value)
    {
        ulong result = 1UL;
        while (result < value)
            result <<= 1;
        return result;
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        checked(((value + alignment - 1UL) / alignment) * alignment);
}

public readonly record struct VolumetricFogCapabilities(
    bool LayeredStorageImages,
    bool RequiredFormats,
    bool RayQueryEmissiveVisibility,
    bool QualifiedProfile);

/// <summary>
/// Fence-complete evidence that the froxel stages produced finite, non-empty
/// medium and lighting output. This is deliberately stronger than pass
/// admission or command recording.
/// </summary>
public readonly record struct VolumetricFogOutputEvidence(
    bool ReadbackValid,
    bool Produced,
    int DiagnosticSampleCount,
    int MediumNonEmptyFroxelCount,
    int DirectNonZeroFroxelCount,
    int IndirectNonZeroFroxelCount,
    int DdgiSupportedFroxelCount,
    int HistoryAcceptedFroxelCount,
    int HistoryRejectedFroxelCount,
    int HistoryRejectedInvalidFroxelCount,
    int HistoryRejectedBoundsFroxelCount,
    int HistoryRejectedExtinctionFroxelCount,
    int HistoryRejectedRadianceFroxelCount,
    int HistoryRejectedVelocityFroxelCount,
    int AdmittedSourceCount,
    int ClusterOverflowCount,
    int NonFiniteCount,
    float MaximumExtinction,
    float MeanExtinction,
    float MaximumDirectLuminance,
    float MeanDirectLuminance,
    float MaximumIndirectLuminance,
    float MeanIndirectLuminance,
    float MinimumTransmittance,
    float MeanTransmittance)
{
    /// <summary>
    /// Indicates that the froxel program did not execute the diagnostic
    /// sampling contract or emitted non-finite data. A sampled but physically
    /// empty medium is valid and must not trigger this compatibility fallback.
    /// </summary>
    public bool RequiresAnalyticFallback =>
        ReadbackValid && (DiagnosticSampleCount == 0 || NonFiniteCount > 0);

    public static VolumetricFogOutputEvidence FromGpuCounters(
        in GPUVolumetricFogDiagnostics counters,
        bool readbackValid)
    {
        if (!readbackValid)
            return default;

        float sampleCount = Math.Max(counters.SampleCount, 1u);
        bool produced = counters.MediumNonEmptyCount > 0u &&
            (counters.DirectNonZeroCount > 0u ||
                counters.IndirectNonZeroCount > 0u) &&
            counters.MaximumOpacityQ > 0u &&
            counters.NonFiniteCount == 0u;
        return new VolumetricFogOutputEvidence(
            true,
            produced,
            SaturatingInt(counters.SampleCount),
            SaturatingInt(counters.MediumNonEmptyCount),
            SaturatingInt(counters.DirectNonZeroCount),
            SaturatingInt(counters.IndirectNonZeroCount),
            SaturatingInt(counters.DdgiSupportedCount),
            SaturatingInt(counters.HistoryAcceptedCount),
            SaturatingInt(counters.HistoryRejectedCount),
            SaturatingInt(counters.HistoryRejectedInvalidCount),
            SaturatingInt(counters.HistoryRejectedBoundsCount),
            SaturatingInt(counters.HistoryRejectedExtinctionCount),
            SaturatingInt(counters.HistoryRejectedRadianceCount),
            SaturatingInt(counters.HistoryRejectedVelocityCount),
            SaturatingInt(counters.AdmittedSourceCount),
            SaturatingInt(counters.ClusterOverflowCount),
            SaturatingInt(counters.NonFiniteCount),
            counters.MaximumExtinctionQ / 1024f,
            counters.ExtinctionSumQ / (sampleCount * 1024f),
            counters.MaximumDirectLuminanceQ / 256f,
            counters.DirectLuminanceSumQ / (sampleCount * 256f),
            counters.MaximumIndirectLuminanceQ / 256f,
            counters.IndirectLuminanceSumQ / (sampleCount * 256f),
            1f - counters.MaximumOpacityQ / 65535f,
            counters.TransmittanceSumQ / (sampleCount * 65535f));
    }

    private static int SaturatingInt(uint value) =>
        value > int.MaxValue ? int.MaxValue : checked((int)value);
}

public readonly record struct VolumetricFogAdmission(
    FogTechnique Requested,
    FogTechnique Effective,
    bool Active,
    string Status,
    ulong PlannedBytes)
{
    public static VolumetricFogAdmission Evaluate(
        bool enabled,
        FogTechnique requested,
        RenderQualityPreset preset,
        VolumetricFogCapabilities capabilities,
        ulong plannedBytes,
        ulong budgetBytes)
    {
        if (!enabled)
            return new(requested, FogTechnique.Analytic, false, "fog-disabled", 0UL);

        FogTechnique resolvedRequest = requested == FogTechnique.Auto
            ? preset is RenderQualityPreset.High or RenderQualityPreset.DdgiHigh or RenderQualityPreset.Ultra
                ? FogTechnique.Froxel
                : FogTechnique.Analytic
            : requested;
        if (resolvedRequest != FogTechnique.Froxel)
            return new(requested, FogTechnique.Analytic, false, "analytic-selected", 0UL);
        if (preset is not (RenderQualityPreset.High or
                RenderQualityPreset.DdgiHigh or
                RenderQualityPreset.Ultra))
        {
            return new(requested, FogTechnique.Analytic, false,
                "froxel-quality-profile-unavailable", 0UL);
        }
        if (plannedBytes == 0UL)
        {
            return new(requested, FogTechnique.Analytic, false,
                "froxel-render-extent-unavailable", 0UL);
        }
        if (!capabilities.LayeredStorageImages || !capabilities.RequiredFormats)
            return new(requested, FogTechnique.Analytic, false, "froxel-storage-image-capability-missing", 0UL);
        if (!capabilities.RayQueryEmissiveVisibility)
            return new(requested, FogTechnique.Analytic, false, "froxel-emissive-visibility-capability-missing", 0UL);
        if (requested == FogTechnique.Auto && !capabilities.QualifiedProfile)
            return new(requested, FogTechnique.Analytic, false, "froxel-profile-not-qualified", 0UL);
        if (plannedBytes > budgetBytes)
            return new(requested, FogTechnique.Analytic, false, "froxel-memory-budget-exceeded", 0UL);

        return new(requested, FogTechnique.Froxel, true, "active", plannedBytes);
    }
}

public static class VolumetricFogMath
{
    public static float HenyeyGreenstein(float cosTheta, float anisotropy)
    {
        float g = Math.Clamp(anisotropy, -0.9f, 0.9f);
        float denominator = MathF.Max(1f + g * g - 2f * g *
            Math.Clamp(cosTheta, -1f, 1f), 1e-6f);
        return (1f - g * g) /
            (4f * MathF.PI * denominator * MathF.Sqrt(denominator));
    }

    public static float SegmentTransmittance(float extinctionPerMeter, float distance) =>
        MathF.Exp(-MathF.Max(extinctionPerMeter, 0f) * MathF.Max(distance, 0f));

    public static bool RejectHistory(float currentOpticalDepth, float previousOpticalDepth)
    {
        float current = MathF.Max(currentOpticalDepth, 0f);
        float threshold = MathF.Max(0.05f, current * 0.2f);
        return MathF.Abs(current - MathF.Max(previousOpticalDepth, 0f)) > threshold;
    }

    public static float BoundMultipleScattering(
        float singleScatteringEnergy,
        float proposedMultipleScatteringEnergy,
        float limit = 0.5f) =>
        Math.Clamp(
            proposedMultipleScatteringEnergy,
            0f,
            MathF.Max(singleScatteringEnergy, 0f) * Math.Clamp(limit, 0f, 0.5f));
}
