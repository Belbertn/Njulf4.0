using System;
using Njulf.Core.Math;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>
/// Dynamic triangle content that may participate in the DDGI ray scene.
/// Values are persisted in qualification evidence and must remain stable.
/// </summary>
public enum DdgiDynamicGeometryContentClass : byte
{
    Skinned = 0,
    Foliage = 1,
    Terrain = 2,
    TopologyChanging = 3,
    DeformingWater = 4
}

/// <summary>
/// The strongest acceleration-structure operation permitted by a submission.
/// The renderer may still choose a rebuild when a refit is incompatible.
/// </summary>
public enum DdgiDynamicGeometryBuildPreference : byte
{
    RefitAllowed = 0,
    RebuildRequired = 1
}

/// <summary>
/// Immutable identity supplied to providers for one render-frame collection.
/// A provider must not publish work for another frame slot or generation.
/// </summary>
public readonly record struct DdgiDynamicGeometryFrameContext(
    ulong FrameSerial,
    ulong RaySceneResourceGeneration,
    int FrameSlot)
{
    public bool IsValid => FrameSerial != 0UL &&
        RaySceneResourceGeneration != 0U &&
        FrameSlot is >= 0 and < RenderingConstants.FramesInFlight;
}

/// <summary>
/// One immutable indexed-triangle submission. Vertex and index buffers must
/// remain alive through the renderer's ordinary frame-completion retirement
/// contract. Indices are unsigned 32-bit values and offsets are in elements.
/// </summary>
public readonly record struct DdgiDynamicGeometrySubmission
{
    public ulong StableSourceId { get; init; }
    public uint GeometryPartId { get; init; }
    public DdgiDynamicGeometryContentClass ContentClass { get; init; }
    public ulong FrameSerial { get; init; }
    public ulong ResourceGeneration { get; init; }
    public BufferHandle VertexBuffer { get; init; }
    public BufferHandle IndexBuffer { get; init; }
    public uint VertexOffset { get; init; }
    public uint VertexCount { get; init; }
    public uint VertexStride { get; init; }
    public uint PositionOffset { get; init; }
    public uint NormalOffset { get; init; }
    public uint TangentOffset { get; init; }
    public uint TexCoord0Offset { get; init; }
    public uint TexCoord1Offset { get; init; }
    public uint ColorOffset { get; init; }
    public uint IndexOffset { get; init; }
    public uint IndexCount { get; init; }
    public DdgiRayVertexFormat VertexFormat { get; init; }
    public MaterialHandle Material { get; init; }
    public Matrix4x4 WorldMatrix { get; init; }
    public BoundingBox LocalBounds { get; init; }
    public BoundingBox PreviousWorldBounds { get; init; }
    public BoundingBox CurrentWorldBounds { get; init; }
    public BoundingBox InfluenceBounds { get; init; }
    public ulong TransformRevision { get; init; }
    public ulong TopologyRevision { get; init; }
    public ulong DeformationRevision { get; init; }
    public DdgiDynamicGeometryBuildPreference BuildPreference { get; init; }
}

public enum DdgiDynamicGeometrySubmissionDisposition : byte
{
    Accepted = 0,
    FrameIdentityMismatch = 1,
    InvalidIdentity = 2,
    InvalidBuffers = 3,
    InvalidTopology = 4,
    InvalidVertexLayout = 5,
    InvalidBoundsOrTransform = 6,
    InvalidMaterial = 7,
    CapacityExceeded = 8
}

/// <summary>
/// Renderer-owned collection endpoint. Providers receive the disposition
/// synchronously and must treat every non-accepted value as exclusion for the
/// submitted generation.
/// </summary>
public interface IDdgiDynamicGeometrySink
{
    DdgiDynamicGeometrySubmissionDisposition Submit(
        in DdgiDynamicGeometrySubmission submission);
}

/// <summary>
/// Implemented by scene components that publish renderer-owned dynamic
/// triangle buffers to DDGI. Collection runs on the render thread and must not
/// mutate scene state or retain the sink.
/// </summary>
public interface IDdgiDynamicGeometryProvider
{
    ulong StableSourceId { get; }

    void CollectDdgiDynamicGeometry(
        in DdgiDynamicGeometryFrameContext context,
        IDdgiDynamicGeometrySink sink);
}

/// <summary>Pure fail-closed validation shared by the runtime and tests.</summary>
public static class DdgiDynamicGeometrySubmissionValidator
{
    public const uint MaximumVertexStride = 4_096U;

    public static DdgiDynamicGeometrySubmissionDisposition Validate(
        in DdgiDynamicGeometrySubmission submission,
        in DdgiDynamicGeometryFrameContext context)
    {
        if (!context.IsValid || submission.FrameSerial != context.FrameSerial ||
            submission.ResourceGeneration != context.RaySceneResourceGeneration)
        {
            return DdgiDynamicGeometrySubmissionDisposition.FrameIdentityMismatch;
        }

        if (submission.StableSourceId == 0UL ||
            !Enum.IsDefined(submission.ContentClass) ||
            !Enum.IsDefined(submission.BuildPreference))
        {
            return DdgiDynamicGeometrySubmissionDisposition.InvalidIdentity;
        }

        if (!submission.VertexBuffer.IsValid || !submission.IndexBuffer.IsValid)
            return DdgiDynamicGeometrySubmissionDisposition.InvalidBuffers;

        if (submission.VertexCount < 3U || submission.IndexCount < 3U ||
            submission.IndexCount % 3U != 0U ||
            submission.TopologyRevision == 0UL ||
            submission.TransformRevision == 0UL ||
            submission.DeformationRevision == 0UL)
        {
            return DdgiDynamicGeometrySubmissionDisposition.InvalidTopology;
        }

        if (submission.VertexFormat == DdgiRayVertexFormat.Invalid ||
            submission.VertexStride < 12U ||
            submission.VertexStride > MaximumVertexStride ||
            submission.VertexStride % 4U != 0U ||
            submission.PositionOffset % 4U != 0U ||
            submission.PositionOffset > submission.VertexStride - 12U ||
            !AttributeFits(submission.NormalOffset, 12U, submission.VertexStride) ||
            !AttributeFits(submission.TangentOffset, 16U, submission.VertexStride) ||
            !AttributeFits(submission.TexCoord0Offset, 8U, submission.VertexStride) ||
            !AttributeFits(submission.TexCoord1Offset, 8U, submission.VertexStride) ||
            !AttributeFits(submission.ColorOffset, 4U, submission.VertexStride))
        {
            return DdgiDynamicGeometrySubmissionDisposition.InvalidVertexLayout;
        }

        if (!submission.Material.IsValid)
            return DdgiDynamicGeometrySubmissionDisposition.InvalidMaterial;

        if (!Finite(submission.WorldMatrix) ||
            !ValidBounds(submission.LocalBounds) ||
            !ValidBounds(submission.PreviousWorldBounds) ||
            !ValidBounds(submission.CurrentWorldBounds) ||
            !ValidBounds(submission.InfluenceBounds) ||
            !Contains(submission.InfluenceBounds, submission.PreviousWorldBounds) ||
            !Contains(submission.InfluenceBounds, submission.CurrentWorldBounds))
        {
            return DdgiDynamicGeometrySubmissionDisposition.InvalidBoundsOrTransform;
        }

        return DdgiDynamicGeometrySubmissionDisposition.Accepted;
    }

    private static bool AttributeFits(uint offset, uint size, uint stride) =>
        offset == 0U || offset <= stride - Math.Min(size, stride);

    private static bool ValidBounds(BoundingBox bounds) =>
        Finite(bounds.Min) && Finite(bounds.Max) &&
        bounds.Min.X <= bounds.Max.X &&
        bounds.Min.Y <= bounds.Max.Y &&
        bounds.Min.Z <= bounds.Max.Z;

    private static bool Contains(BoundingBox outer, BoundingBox inner) =>
        outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y &&
        outer.Min.Z <= inner.Min.Z && outer.Max.X >= inner.Max.X &&
        outer.Max.Y >= inner.Max.Y && outer.Max.Z >= inner.Max.Z;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

/// <summary>
/// Per-content-class fairness input. Weight controls service order while the
/// maximum share prevents one class from monopolizing a mixed workload.
/// </summary>
public readonly record struct DdgiDynamicGeometryClassBudget(
    int Weight,
    double MaximumMixedShare)
{
    public DdgiDynamicGeometryClassBudget Normalized => new(
        Math.Clamp(Weight, 1, 64),
        Math.Clamp(MaximumMixedShare, 0.0, 1.0));
}

/// <summary>Frozen production defaults for dynamic-AS class fairness.</summary>
public sealed record DdgiDynamicGeometryBudgetPolicy
{
    public static DdgiDynamicGeometryBudgetPolicy Production { get; } = new();

    public int GpuTimeBudgetMicroseconds { get; init; } = 750;
    public DdgiDynamicGeometryClassBudget Skinned { get; init; } = new(4, 1.0);
    public DdgiDynamicGeometryClassBudget Foliage { get; init; } = new(1, 0.25);
    public DdgiDynamicGeometryClassBudget Terrain { get; init; } = new(2, 1.0);
    public DdgiDynamicGeometryClassBudget TopologyChanging { get; init; } = new(2, 1.0);
    public DdgiDynamicGeometryClassBudget DeformingWater { get; init; } = new(2, 1.0);

    public DdgiDynamicGeometryClassBudget For(
        DdgiDynamicGeometryContentClass contentClass) => contentClass switch
        {
            DdgiDynamicGeometryContentClass.Skinned => Skinned.Normalized,
            DdgiDynamicGeometryContentClass.Foliage => Foliage.Normalized,
            DdgiDynamicGeometryContentClass.Terrain => Terrain.Normalized,
            DdgiDynamicGeometryContentClass.TopologyChanging =>
                TopologyChanging.Normalized,
            DdgiDynamicGeometryContentClass.DeformingWater =>
                DeformingWater.Normalized,
            _ => throw new ArgumentOutOfRangeException(nameof(contentClass))
        };
}

/// <summary>
/// Fence-delayed feedback controller for the dynamic acceleration-structure
/// GPU budget. The estimate is deliberately conservative and allocation free:
/// a smoothed absolute deviation is added to the mean so transient spikes
/// reduce admission before they become a persistent frame-time cliff.
/// </summary>
public sealed class DdgiDynamicGeometryGpuBudgetGovernor
{
    private const double MeanAlpha = 0.25;
    private const double DeviationAlpha = 0.20;
    private const double MinimumRelativeDeviation = 0.10;
    private const double DeviationSafetyMultiplier = 2.0;

    private double _meanMicrosecondsPerBuild;
    private double _absoluteDeviationMicrosecondsPerBuild;

    public int SampleCount { get; private set; }

    public double EstimatedMicrosecondsPerBuild =>
        _meanMicrosecondsPerBuild;

    public double ConservativeMicrosecondsPerBuild
    {
        get
        {
            if (SampleCount == 0)
                return 0.0;

            double deviation = Math.Max(
                _absoluteDeviationMicrosecondsPerBuild,
                _meanMicrosecondsPerBuild * MinimumRelativeDeviation);
            return Math.Max(
                1.0,
                _meanMicrosecondsPerBuild +
                DeviationSafetyMultiplier * deviation);
        }
    }

    /// <summary>
    /// Observes one fence-complete timestamp. Invalid or empty samples are
    /// ignored rather than poisoning future admission decisions.
    /// </summary>
    public bool Observe(long gpuMicroseconds, int completedBuildCount)
    {
        if (gpuMicroseconds <= 0L || completedBuildCount <= 0)
            return false;

        double sample = gpuMicroseconds / (double)completedBuildCount;
        if (!double.IsFinite(sample) || sample <= 0.0)
            return false;

        if (SampleCount == 0)
        {
            _meanMicrosecondsPerBuild = sample;
            _absoluteDeviationMicrosecondsPerBuild = sample * 0.25;
        }
        else
        {
            double previousMean = _meanMicrosecondsPerBuild;
            double deviation = Math.Abs(sample - previousMean);
            _meanMicrosecondsPerBuild =
                previousMean + MeanAlpha * (sample - previousMean);
            _absoluteDeviationMicrosecondsPerBuild += DeviationAlpha *
                (deviation - _absoluteDeviationMicrosecondsPerBuild);
        }

        SampleCount = checked(SampleCount + 1);
        return true;
    }

    /// <summary>
    /// Returns the work-conserving build limit for the next frame. Until two
    /// samples exist, the configured cap remains authoritative. Afterwards a
    /// single build is always allowed so a slow device cannot permanently
    /// starve deforming geometry merely because one build exceeds the target.
    /// </summary>
    public int ResolveMaximumBuilds(
        int configuredMaximumBuilds,
        int gpuTimeBudgetMicroseconds)
    {
        int configured = Math.Max(0, configuredMaximumBuilds);
        if (configured == 0 || gpuTimeBudgetMicroseconds <= 0 ||
            SampleCount < 2)
        {
            return configured;
        }

        int estimated = checked((int)Math.Floor(
            gpuTimeBudgetMicroseconds / ConservativeMicrosecondsPerBuild));
        return Math.Clamp(estimated, 1, configured);
    }

    public void Reset()
    {
        _meanMicrosecondsPerBuild = 0.0;
        _absoluteDeviationMicrosecondsPerBuild = 0.0;
        SampleCount = 0;
    }
}
